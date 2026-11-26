using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>动作键盘快捷键原生录制；辅助键不触发 Tab 导航、空格点击或大小写切换。</summary>
internal sealed class KeyboardShortcutRecorder : IDisposable
{
    private static readonly HashSet<KeyboardShortcutRecorder> Draining = [];
    private readonly MessageOnlyWindow _pump = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private RecordingState _state = new([]);
    private readonly nint _window;
    private readonly Action<uint, uint> _completed;
    private readonly Action _rejected;
    private readonly nuint _testInputTag;
    private bool _finished;
    private nint _hook;
    private long _cancelledAt;
    internal int ObservedHookEvents { get; private set; }

    internal KeyboardShortcutRecorder(nint window, Action<uint, uint> completed, Action rejected, nuint testInputTag = 0)
    {
        _window = window; _completed = completed; _rejected = rejected; _testInputTag = testInputTag; _proc = Callback;
        _pump.MessageReceived += message =>
        {
            if (message.Message != 0x113) return;
            if (_state.Active && NativeMethods.GetForegroundWindow() != _window) Cancel();
            if (!_state.Active && _state.OwnedCount != 0 && Stopwatch.GetElapsedTime(_cancelledAt).TotalSeconds > 3) Finish();
        };
        try
        {
            _pump.InvokeOnPumpThread(() =>
            {
                var held = new List<uint>();
                for (uint vk = 8; vk < 255; vk++)
                    if ((NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0) held.Add(vk);
                _state = new RecordingState(held);
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
                if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (MousePrefixNative.SetTimer(_pump.Handle, 84, 100, 0) == 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return true;
            });
        }
        catch { if (_hook != 0) NativeMethods.UnhookWindowsHookEx(_hook); _pump.Dispose(); throw; }
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        ObservedHookEvents++;
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (_testInputTag != 0 && (nuint)data.dwExtraInfo != _testInputTag
            || (data.flags & 0x10) != 0 && (_testInputTag == 0 || (nuint)data.dwExtraInfo != _testInputTag))
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        bool up = (int)wParam is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        if (_state.Active && NativeMethods.GetForegroundWindow() != _window) Cancel();
        var result = _state.Read(data.vkCode, up);
        ApplyResult(result);
        return result.Consumed ? 1 : NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void ApplyResult(RecordingState.Result result)
    {
        if (result.Cancelled) KeepForDrain();
        if (result.Complete) Finish();
        else if (result.Rejected) _rejected();
    }

    /// <summary>录制状态不访问系统，可在后台验证完整按下／松开流程。</summary>
    internal sealed class RecordingState
    {
        private readonly HashSet<uint> _owned = [], _initiallyHeld;
        private uint _nativeCandidate, _candidateModifiers;
        private bool _invalid;
        internal bool Active { get; private set; } = true;
        internal uint Modifiers { get; private set; }
        internal uint Key { get; private set; }
        internal int OwnedCount => _owned.Count;

        internal RecordingState(IEnumerable<uint> initiallyHeld)
        {
            _initiallyHeld = new(initiallyHeld);
            // GetAsyncKeyState 同时报通用键和左右键；钩子只报左右键，不能留下永远等不到松开的别名。
            _initiallyHeld.Remove(0x10); _initiallyHeld.Remove(0x11); _initiallyHeld.Remove(0x12);
        }

        internal readonly record struct Result(bool Consumed, bool Complete = false, bool Rejected = false, bool Cancelled = false);

        internal Result Read(uint key, bool up)
        {
            if (_initiallyHeld.Contains(key))
            { if (up) _initiallyHeld.Remove(key); return default; }
            if (_owned.Contains(key))
            {
                if (up)
                {
                    // 原有 Ctrl+Space／Ctrl+Tab 等仍可直接录制，未按其他主键时保存旧组合。
                    if (Active && Key == 0 && !_invalid && _nativeCandidate == key)
                    { Key = _nativeCandidate; Modifiers = _candidateModifiers; }
                    _owned.Remove(key);
                    if (_owned.Count == 0)
                    {
                        if (!Active || Key != 0) return new(true, Complete: true);
                        if (_invalid) { _invalid = false; _nativeCandidate = 0; return new(true, Rejected: true); }
                    }
                }
                return new(true);
            }
            if (!Active || _initiallyHeld.Count != 0) return default;
            if (up)
            {
                // 兼容只收到 PrtSc 松开消息的输入。已有组合仍按原来的按下/松开配对处理。
                if (key != NativeMethods.VK_SNAPSHOT || _owned.Count != 0 || _invalid || Key != 0)
                    return default;
                Key = key;
                Modifiers = 0;
                return new(true, Complete: true);
            }
            _owned.Add(key);
            if (Key != 0 || _invalid) return new(true);
            if (key == 0x1B) { Active = false; return new(true, Cancelled: true); }
            uint bit = ActionKeyboardBinding.ModifierOf(key);
            uint modifiers = 0;
            foreach (uint held in _owned) modifiers |= ActionKeyboardBinding.ModifierOf(held);
            if (bit != 0)
            {
                if ((bit & ActionKeyboardBinding.ExtendedMask) != 0)
                {
                    uint extended = modifiers & ActionKeyboardBinding.ExtendedMask;
                    _nativeCandidate = extended == bit && (modifiers & 15) != 0 ? key : 0;
                    _candidateModifiers = modifiers & 15;
                }
                return new(true);
            }
            _nativeCandidate = 0;
            if (!ActionKeyboardBinding.IsValid(modifiers, key)
                || modifiers == 0 && key is not (NativeMethods.VK_SNAPSHOT or (>= 0x70 and <= 0x7B)))
            { _invalid = true; return new(true); }
            Modifiers = modifiers; Key = key;
            return new(true);
        }

        internal void Cancel() => Active = false;
    }

    private void Cancel()
    {
        if (!_state.Active) return;
        _state.Cancel(); KeepForDrain();
    }

    private void KeepForDrain()
    {
        _cancelledAt = Stopwatch.GetTimestamp();
        if (_state.OwnedCount == 0) Finish();
        else lock (Draining) Draining.Add(this);
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_hook != 0) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0; MousePrefixNative.KillTimer(_pump.Handle, 84);
        lock (Draining) Draining.Remove(this);
        _completed(_state.Active ? _state.Modifiers : 0, _state.Active ? _state.Key : 0); _pump.Dispose();
    }

    public void Dispose()
    {
        if (!_finished) _pump.InvokeOnPumpThread(() => { Cancel(); return true; });
    }
}
