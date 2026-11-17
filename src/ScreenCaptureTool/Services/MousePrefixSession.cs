using System;
using System.Collections.Generic;
using System.Diagnostics;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>消息泵独占的引导键生命周期；补发经队列交付，钩子不等待注入回调。</summary>
internal sealed class MousePrefixSession
{
    private readonly Action _schedule;
    private readonly Func<NativeMethods.INPUT[], uint> _send;
    private readonly Func<bool> _sameContext;
    private readonly Action<string> _warn;
    private readonly List<NativeMethods.INPUT> _pending = [];
    private readonly List<NativeMethods.INPUT> _queue = [];
    private readonly Dictionary<ulong, NativeMethods.INPUT> _forwardedDowns = [];
    private ActionMouseKey _key;
    private readonly List<ActionMouseKey> _keys = [];
    private readonly HashSet<ActionMouseKey> _heldKeys = [];
    private bool _held, _consumed, _posted, _sending;
    private bool _replayInterrupted;
    private int _outstanding;
    private long _sentAt;
    internal bool Disabled { get; private set; }
    internal bool NeedsHook => _key.IsValid || IsDraining;
    internal bool IsDraining => _posted || _outstanding != 0 || _queue.Count != 0;
    internal bool HasPending => _pending.Count != 0;
    internal bool IsConsumed => _consumed && _held;
    internal ActionMouseKey Key => _key;
    // 本机 SendInput 鼠标回调会截去 dwExtraInfo 高 32 位；两条链共用正的 31 位随机标记。
    internal nuint ReplayTag { get; } = (nuint)Random.Shared.Next(0x20000000, int.MaxValue);

    internal MousePrefixSession(Action schedule, Func<NativeMethods.INPUT[], uint> send, Func<bool> sameContext, Action<string> warn)
    { _schedule = schedule; _send = send; _sameContext = sameContext; _warn = warn; }

    internal bool Begin(ActionMouseKey key, NativeMethods.INPUT input)
    {
        if (Disabled || NeedsHook || !key.IsValid) return false;
        _key = key; _keys.Add(key); _heldKeys.Add(key); _held = true; _consumed = false; _pending.Add(input); return true;
    }

    /// <summary>同一次键盘组合新增的辅助键共用原事件队列，避免键鼠重复补发。</summary>
    internal bool AddModifier(ActionMouseKey key, NativeMethods.INPUT input)
    {
        if (!CanClaimKeyboard || _keys.Exists(existing => existing.Matches(key))) return false;
        _keys.Add(key); _heldKeys.Add(key); _held = true;
        if (!_consumed) _pending.Add(input);
        return true;
    }

    internal uint HeldModifiers
    {
        get
        {
            uint modifiers = 0;
            foreach (var key in _heldKeys) modifiers |= ActionKeyboardBinding.ModifierOf(key.VirtualKey);
            return modifiers;
        }
    }

    internal bool CanClaimKeyboard => !Disabled && _held && !IsDraining
        && (_pending.Count != 0 || _consumed) && (_consumed || _sameContext());
    internal bool CanClaim(ActionMouseKey key) => CanClaimKeyboard && key.Matches(_key) && _heldKeys.Contains(_key);

    internal void Consume() { _pending.Clear(); _consumed = true; }

    /// <summary>已确认松开或恢复流被新物理输入打断时，结束未交付的内存认领，不补造按下。</summary>
    internal bool ReleaseUnobservedKey(uint virtualKey)
    {
        if (IsDraining || _forwardedDowns.Count != 0
            || _heldKeys.RemoveWhere(key => key.VirtualKey == virtualKey) == 0) return false;
        if (_pending.Count != 0)
        {
            _pending.Clear(); _consumed = true;
            _warn("引导键输入周期中断：取消未交付候选，不凭旧状态补造按下");
        }
        _held = _heldKeys.Count != 0;
        if (!_held) ClearKey();
        return true;
    }

    internal void ResetFailure() { if (!NeedsHook) Disabled = false; }

    internal bool Keyboard(ActionMouseKey key, bool up, NativeMethods.INPUT input)
    {
        if (up) _forwardedDowns.Remove(InputIdentity(input));
        bool own = _keys.Exists(existing => existing.Matches(key));
        if (own)
        {
            if (up) _heldKeys.RemoveWhere(existing => existing.Matches(key));
            else _heldKeys.Add(_keys.Find(existing => existing.Matches(key)));
            _held = _heldKeys.Count != 0;
        }
        if (IsDraining) { Enqueue(input); return true; }
        if (own && _consumed)
        { if (!_held) ClearKey(); return true; }
        if (_pending.Count != 0)
        {
            if (!_sameContext())
            {
                // 系统异步换焦点后不向新目标错投；本周期只配对吃掉未交付的原键松开。
                _pending.Clear(); _consumed = true;
                _warn("引导键上下文变化：停止补发原键，本次原功能未恢复");
                if (!_held) ClearKey();
                return own;
            }
            _pending.Add(input);
            if (!own || up || _pending.Count >= 64) Replay();
            return true;
        }
        if (own && up && !_held) ClearKey();
        return false;
    }

    internal bool Mouse(NativeMethods.INPUT input)
    {
        if (IsRelease(input)) _forwardedDowns.Remove(InputIdentity(input));
        if (IsDraining) { Enqueue(input); return true; }
        if (_pending.Count == 0) return false;
        if (!_sameContext())
        {
            _pending.Clear(); _consumed = true;
            _warn("引导键上下文变化：停止补发原键，本次原功能未恢复"); return false;
        }
        _pending.Add(input); Replay(); return true;
    }

    internal void Replay()
    {
        if (_pending.Count == 0) return;
        _queue.AddRange(_pending); _pending.Clear(); Post();
    }

    private void Enqueue(NativeMethods.INPUT input)
    {
        if (_queue.Count >= 512)
        {
            Fail("引导键补发队列超限，停止本次入口；未交付事件已中断"); return;
        }
        _queue.Add(input); Post();
    }

    private void Post()
    {
        if (_posted || _outstanding != 0 || _sending) return;
        _posted = true; _schedule();
    }

    internal void Flush()
    {
        _posted = false;
        if (_outstanding != 0 || _queue.Count == 0) return;
        if (!_sameContext()) { Fail("引导键补发前上下文变化，停止交付；未交付原功能未恢复"); return; }
        var batch = _queue.ToArray(); _queue.Clear();
        foreach (ref var input in batch.AsSpan())
        {
            if (input.type == NativeMethods.INPUT_KEYBOARD) input.U.ki.dwExtraInfo = ReplayTag;
            else input.U.mi.dwExtraInfo = ReplayTag;
        }
        _sending = true; _outstanding = batch.Length; _sentAt = Stopwatch.GetTimestamp(); _replayInterrupted = false;
        try
        {
            uint sent = _send(batch);
            for (int i = 0; i < Math.Min(sent, (uint)batch.Length); i++)
            {
                ulong identity = InputIdentity(batch[i]);
                if (IsRelease(batch[i])) _forwardedDowns.Remove(identity);
                else if (identity != 0) _forwardedDowns[identity] = batch[i];
            }
            if (sent != batch.Length)
            {
                // 部分成功不能重发整批；永久停新入口并明确告警，不重复输入。
                Fail($"引导键补发失败：插入 {sent}/{batch.Length}，停止本次入口"); return;
            }
        }
        catch (Exception ex) { Fail("引导键补发异常：" + ex.Message); }
        finally { _sending = false; }
        FinishBatch();
    }

    internal void Acknowledge()
    {
        if (_outstanding > 0) _outstanding--;
        if (!_sending) FinishBatch();
    }

    internal bool ObserveReplay(bool release)
    {
        // 入队与操作系统交付之间仍可换焦点；最后一层在实际注入钩子回调中复核。
        bool allowed = release || _sameContext();
        if (!allowed) _replayInterrupted = true;
        Acknowledge(); return allowed;
    }

    internal void Tick()
    {
        if (_outstanding != 0 && Stopwatch.GetElapsedTime(_sentAt).TotalMilliseconds > 500)
            Fail($"引导键补发确认超时，尚待 {_outstanding} 个事件；停止本次入口，交付状态未知，不自动重发");
        if (_pending.Count != 0 && !_sameContext())
        {
            _pending.Clear(); _consumed = true;
            _warn("引导键等待期间上下文变化：本次原功能未恢复");
        }
    }

    private void FinishBatch()
    {
        if (_outstanding != 0) return;
        if (_replayInterrupted) { _replayInterrupted = false; Fail("引导键实际交付时上下文变化，已阻止新的按下；本次原功能未完整恢复"); return; }
        if (_queue.Count != 0) Post();
        else if (!_held) ClearKey();
    }

    internal void Fail(string reason)
    {
        Disabled = true; _pending.Clear(); _queue.Clear(); _outstanding = 0; _posted = false;
        // 只补配对松开，不重放任何失败的按下；防部分 SendInput 成功后遗留逻辑粘键。
        if (_forwardedDowns.Count != 0)
        {
            var releases = new List<NativeMethods.INPUT>();
            foreach (var forwarded in _forwardedDowns.Values)
            {
                var input = forwarded;
                if (input.type == NativeMethods.INPUT_KEYBOARD) input.U.ki.dwFlags |= 2;
                else input.U.mi.dwFlags = ((input.U.mi.dwFlags & (2u | 8u | 32u | 128u)) << 1)
                    | (input.U.mi.dwFlags & 0xC001u);
                releases.Add(input);
            }
            _forwardedDowns.Clear();
            try { uint sent = _send(releases.ToArray()); if (sent != releases.Count) reason += "；配对松开补偿未全部插入"; }
            catch { reason += "；配对松开补偿失败"; }
        }
        _consumed = true;
        if (!_held) ClearKey();
        _warn(reason);
    }

    private void ClearKey() { _key = default; _keys.Clear(); _heldKeys.Clear(); _held = false; _consumed = false; }

    private static bool IsRelease(NativeMethods.INPUT input) => input.type == NativeMethods.INPUT_KEYBOARD
        ? (input.U.ki.dwFlags & 2) != 0 : (input.U.mi.dwFlags & (4u | 16u | 64u | 256u)) != 0;

    private static ulong InputIdentity(NativeMethods.INPUT input)
    {
        if (input.type == NativeMethods.INPUT_KEYBOARD)
            return 0x1000000000000UL | (uint)input.U.ki.wVk | (uint)input.U.ki.wScan << 16 | (ulong)(input.U.ki.dwFlags & 1) << 32;
        uint flag = input.U.mi.dwFlags;
        uint button = (flag & 6) != 0 ? 1u : (flag & 24) != 0 ? 2u : (flag & 96) != 0 ? 3u : (flag & 384) != 0 ? 3u + input.U.mi.mouseData : 0;
        return button;
    }
}
