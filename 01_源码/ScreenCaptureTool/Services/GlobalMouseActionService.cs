using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>跨钩子线程和界面的单次鼠标操作；窗口准备期间也保留最后坐标及松手、取消状态。</summary>
public sealed class MouseActionGesture
{
    private readonly object _gate = new();
    private Point _position;
    private bool _released;
    private bool _cancelled;
    private bool _moved;
    private long _updatedTimestamp;
    private long _updateCount;
    public Point Start { get; }
    public long StartedTimestamp { get; } = Stopwatch.GetTimestamp();

    public MouseActionGesture(Point start) { Start = start; _position = start; _updatedTimestamp = StartedTimestamp; }

    public (Point Position, bool Released, bool Cancelled, bool Moved) Read()
    {
        lock (_gate) return (_position, _released, _cancelled, _moved);
    }

    /// <summary>同一锁内读取输入与时间，诊断只由 UI 消费；钩子里不写日志、不触碰控件。</summary>
    internal (Point Position, bool Released, bool Cancelled, bool Moved, long UpdatedTimestamp, long UpdateCount) ReadTimed()
    {
        lock (_gate) return (_position, _released, _cancelled, _moved, _updatedTimestamp, _updateCount);
    }

    internal void Update(Point position, bool released)
    {
        lock (_gate)
        {
            _position = position;
            _updatedTimestamp = Stopwatch.GetTimestamp();
            _updateCount++;
            _released |= released;
            _moved |= Math.Abs(position.X - Start.X) >= CaptureSession.DragThresholdPx
                || Math.Abs(position.Y - Start.Y) >= CaptureSession.DragThresholdPx;
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancelled = true;
    }
}

/// <summary>
/// 在专用 Win32 消息泵监听组合鼠标和附加辅助键的键盘组合，共用原键消费与补发。
/// 钩子只读绑定快照和原生状态，不访问 Avalonia 控件；认领后配对处理按下／松开。
/// </summary>
internal sealed class GlobalMouseActionService : IDisposable
{
    private readonly MessageOnlyWindow _pump = new();
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private int _keyboardHookGeneration = 1;
    private int _keyboardCallbackDepth;
    private readonly List<NativeMethods.LowLevelKeyboardProc> _keyboardProcKeepAlive = [];
    private readonly List<KeyboardHookEvent> _activeKeyboardEvents = [];
    private readonly record struct KeyboardHookEvent(IntPtr Message, uint Key, uint Scan, uint Flags, uint Time, IntPtr Extra);
    private readonly List<(IntPtr Hook, NativeMethods.LowLevelKeyboardProc Proc)> _retiredKeyboardHooks = [];
    private readonly Action<ActionDefinition, MouseActionGesture?> _dispatch;
    private readonly Func<ActionDefinition, bool> _canStart;
    private readonly Func<ActionDefinition, bool> _canStartKeyboard;
    private readonly Func<IntPtr, bool>? _allowKeyboardInOwnWindow;
    private bool _recordingPaused;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private IntPtr _allowedSettingsWindow;
    private ActionDefinition[] _bindings = Array.Empty<ActionDefinition>();
    private ActionDefinition[] _keyboardBindings = [];
    private readonly HashSet<uint> _keyboardOwnedKeys = [];
    private readonly HashSet<uint> _peerRestoredKeys = [];
    private readonly HashSet<uint> _restoredKeyboardOwnedKeys = [];
    private readonly HashSet<uint> _abandonedRestoreReleases = [];
    private long _abandonedRestoreAt;
    private readonly HashSet<uint> _rawReleasedOwnedKeys = [];
    private readonly HashSet<uint> _rawReleasedSharedKeys = [];
    private readonly Action<ActionDefinition> _dispatchKeyboard;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private ActionDefinition? _ownedAction;
    private MouseActionGesture? _gesture;
    private uint _menuMask;
    private bool _escapeOwned;
    private bool _disposed;
    private const uint PrefixReplayMessage = 0x8000 + 82;
    private readonly MousePrefixSession _prefix;
    private readonly HashSet<uint> _physicalKeys = [];
    private MousePrefixNative.Context _prefixContext;
    private bool _prefixTimer;
    private readonly nuint _testInputTag;
    private readonly Func<int, IntPtr, IntPtr, IntPtr>? _testKeyboardChain;
    private bool _prefixHookFailed;
    private readonly SharedKeyboardInput _sharedKeyboard;
    private readonly KeyboardPeerStartupMonitor? _peerStartup;
    private long _lastRefreshWaitRequest, _lastRefreshWaitAt;
    private string? _lastRefreshWaitReason;
    private readonly HashSet<uint> _sharedReleases = [];
    private readonly HashSet<uint> _restoredReleases = [];
    private uint _sharedConsumedModifiers;
    private long _sharedConsumedAt;
    private bool _yieldedToPeer;
    private bool _peerClaimedPrefix;
    private bool _prefixFromPeerRestore;
    // Quicker 的恢复输入使用 0x2234 标记；只在当前实际组合附近处理，不接管普通模拟输入。
    private const nuint QuickerRestoreTag = 0x2234;

    public GlobalMouseActionService(Action<ActionDefinition, MouseActionGesture?> dispatch, Func<ActionDefinition, bool> canStart,
        nuint testInputTag = 0, Action<ActionDefinition>? dispatchKeyboard = null,
        Func<int, IntPtr, IntPtr, IntPtr>? testKeyboardChain = null,
        Func<ActionDefinition, bool>? canStartKeyboard = null, Func<IntPtr, bool>? allowKeyboardInOwnWindow = null)
    {
        if (testKeyboardChain != null && testInputTag == 0) throw new ArgumentException("后台键盘链夹具必须显式设置探针输入标记");
        _dispatch = dispatch;
        _dispatchKeyboard = dispatchKeyboard ?? (action => dispatch(action, null));
        _canStart = canStart;
        _canStartKeyboard = canStartKeyboard ?? canStart;
        _allowKeyboardInOwnWindow = allowKeyboardInOwnWindow;
        // 仅友元探针显式使用标记输入；产品调用保持 0，一般外部注入不认领。
        _testInputTag = testInputTag;
        _testKeyboardChain = testKeyboardChain;
        if (testInputTag == 0) _peerStartup = new KeyboardPeerStartupMonitor();
        _mouseProc = MouseCallback;
        _keyboardProc = CreateKeyboardProc(_keyboardHookGeneration);
        _pump.WaitForReady();
        _sharedKeyboard = new SharedKeyboardInput(_pump.Handle, ObserveSharedKey);
        _prefix = new MousePrefixSession(
            () => { if (!MousePrefixNative.PostMessage(_pump.Handle, PrefixReplayMessage, 0, 0))
                    _prefix!.Fail("引导键补发排队失败，停止本次入口"); },
            MousePrefixNative.SendRestoringPointer, () => MousePrefixNative.ReadContext() == _prefixContext,
            reason => AppLogger.Warn(reason));
        _pump.MessageReceived += message =>
        {
            if (message.Message == PrefixReplayMessage) { _prefix.Flush(); SyncHooks(); }
            if (message.Message == 0x113 && message.WParam == new IntPtr(82)) { _prefix.Tick(); SyncHooks(); }
            if (message.Message == 0xFF) _sharedKeyboard.Read(message.LParam);
        };
    }

    /// <summary>界面线程同步已打开的设置窗口句柄；钩子线程只读原生句柄，不访问控件。</summary>
    public void SetSettingsWindow(IntPtr window) => Interlocked.Exchange(ref _allowedSettingsWindow, window);

    // 暂停识别但保留监听位置，避免每次录制后重新挂钩改变第三方软件的优先顺序。
    public void SetRecordingPaused(bool paused) => _pump.InvokeOnPumpThread(() =>
    {
        _recordingPaused = paused;
        if (paused) { _gesture?.Cancel(); _prefix.Replay(); _sharedKeyboard.Pending.Clear(); }
        RefreshSharedInput();
        return true;
    });

    private bool CanStart(ActionDefinition action) => !_recordingPaused && _canStart(action);
    private bool CanStartKeyboard(ActionDefinition action) => !_recordingPaused && _canStartKeyboard(action);

    private void RefreshSharedInput()
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint foregroundProcess);
        // 本进程前台同时注册 Raw Input 会使低级键盘钩子失去回调，录制与设置操作必须先停用。
        // 切换监听不重新安装钩子，保持与第三方软件的钩子顺序。
        _sharedKeyboard.Configure(!_recordingPaused && foregroundProcess != _processId && _testInputTag == 0
            && (_keyboardBindings.Length != 0 || HasSharedTail));
    }

    public void Configure(IReadOnlyList<ActionDefinition> actions)
    {
        var bindings = new List<ActionDefinition>();
        var keyboardBindings = new List<ActionDefinition>();
        foreach (var action in actions)
        {
            if (!action.Enabled || !action.HasHotKey || !ActionKeyboardBinding.IsExtended(action.HotKeyModifiers)
                || !ActionKeyboardBinding.IsValid(action.HotKeyModifiers, action.HotKeyVirtualKey)) continue;
            if (System.Linq.Enumerable.Any(actions, other => other.Enabled && other.HasHotKey
                && !ActionKeyboardBinding.IsExtended(other.HotKeyModifiers)
                && ActionKeyboardBinding.Conflicts(action.HotKeyModifiers, action.HotKeyVirtualKey,
                    other.HotKeyModifiers, other.HotKeyVirtualKey)))
            { AppLogger.Warn("附加辅助键与已有键盘热键冲突，跳过动作「" + action.Name + "」"); continue; }
            if (keyboardBindings.Exists(other => other.HotKeyModifiers == action.HotKeyModifiers
                && other.HotKeyVirtualKey == action.HotKeyVirtualKey)) continue;
            keyboardBindings.Add(action.Clone());
        }
        foreach (var action in actions)
        {
            if (!action.Enabled || !ActionMouseBinding.IsSupported(action)) continue;
            if (action.MousePrefixKey.IsValid && System.Linq.Enumerable.Any(actions, other => other.Enabled && other.HasHotKey
                && other.HotKeyVirtualKey == action.MousePrefixKey.VirtualKey && other.HotKeyModifiers == ActionMouseBinding.EffectiveModifiers(action)))
            { AppLogger.Warn("引导键与动作热键冲突，跳过鼠标入口「" + action.Name + "」"); continue; }
            if (bindings.Exists(other => ActionMouseBinding.Conflicts(other, action)))
            {
                AppLogger.Warn("全局鼠标组合重复，跳过动作「" + action.Name + "」");
                continue;
            }
            bindings.Add(action.Clone());
        }
        _pump.InvokeOnPumpThread(() =>
        {
            _gesture?.Cancel();
            _prefix.Replay();
            _prefix.ResetFailure();
            _prefixHookFailed = false;
            _bindings = bindings.ToArray();
            _keyboardBindings = keyboardBindings.ToArray();
            _peerStartup?.Configure(_keyboardBindings.Length != 0);
            _sharedKeyboard.Pending.Clear();
            SyncHooks();
            return true;
        });
    }

    private void SyncHooks()
    {
        // 显式后台链夹具只运行回调状态机，不注册系统监听或计时器；产品从不设置此出口。
        if (_testKeyboardChain != null) return;
        if (_abandonedRestoreReleases.Count != 0 && Stopwatch.GetElapsedTime(_abandonedRestoreAt).TotalMilliseconds >= 500)
            _abandonedRestoreReleases.Clear();
        bool sharedTail = HasSharedTail;
        if (!sharedTail) _sharedConsumedModifiers = 0;
        RefreshSharedInput();
        // 正在认领的一次按下必须吃到对应松开后才卸钩，防止落到底下程序形成孤立松手。
        // Alt/Win 与 Esc 的键盘收尾也必须配对完成，不能随配置移除提前卸掉。
        if (!sharedTail && _bindings.Length == 0 && _keyboardBindings.Length == 0 && _keyboardOwnedKeys.Count == 0 && _restoredKeyboardOwnedKeys.Count == 0
            && _ownedAction == null && _menuMask == 0 && !_escapeOwned && !_prefix.NeedsHook)
        {
            Unhook();
            return;
        }
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc,
                NativeMethods.GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                AppLogger.Warn("全局鼠标钩子安装失败，错误码：" + Marshal.GetLastWin32Error());
                return;
            }
        }
        // 旧绑定空闲仍只观察鼠标；只有引导键配置存在才预先挂键盘钩子。
        // 新配置移除后等待原键／补发／手势配对收尾，再摘除相关钩子。
        bool prefixConfigured = (_keyboardBindings.Length != 0 || Array.Exists(_bindings, action => action.MousePrefixKey.IsValid))
            && !_prefix.Disabled && !_prefixHookFailed;
        if (prefixConfigured && !EnsureGestureKeyboardHook()) { _prefixHookFailed = true; prefixConfigured = false; }
        long startupRequest = _peerStartup?.ReadyRequest(Environment.TickCount64) ?? 0;
        if (startupRequest != 0)
        {
            string? blocker = KeyboardRefreshBlocker();
            if (blocker == null)
            {
                // 一次新实例只尝试一次；失败保留旧监听，不在计时器上反复重挂。
                RefreshKeyboardHook();
                _peerStartup!.Complete(startupRequest);
            }
            else if (startupRequest != _lastRefreshWaitRequest || blocker != _lastRefreshWaitReason
                && Stopwatch.GetElapsedTime(_lastRefreshWaitAt).TotalSeconds >= 2)
            {
                // 只记录等待类别，不记录按键；相同原因不重复刷日志。
                AppLogger.Info("检测到 Quicker 新实例，等待刷新键盘监听：" + blocker);
                _lastRefreshWaitRequest = startupRequest; _lastRefreshWaitReason = blocker;
                _lastRefreshWaitAt = Stopwatch.GetTimestamp();
            }
        }
        bool needsTimer = prefixConfigured || _prefix.NeedsHook || sharedTail;
        if (needsTimer && !_prefixTimer)
        {
            _prefixTimer = MousePrefixNative.SetTimer(_pump.Handle, 82, 100, 0) != 0;
            if (!_prefixTimer) { _prefix.Fail("引导键收尾计时器创建失败，暂停新入口"); prefixConfigured = false; }
        }
        if (!needsTimer && _prefixTimer) { MousePrefixNative.KillTimer(_pump.Handle, 82); _prefixTimer = false; }
        if (!sharedTail && !prefixConfigured && !_prefix.NeedsHook && _keyboardOwnedKeys.Count == 0 && _restoredKeyboardOwnedKeys.Count == 0
            && _ownedAction == null && _menuMask == 0 && !_escapeOwned && _keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private bool EnsureGestureKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return true;
        _physicalKeys.Clear();
        _peerRestoredKeys.Clear();
        for (uint vk = 8; vk < 255; vk++)
            if (vk is not (0x10 or 0x11 or 0x12)
                && (NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0) _physicalKeys.Add(vk);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc,
            NativeMethods.GetModuleHandle(null), 0);
        if (_keyboardHook != IntPtr.Zero) return true;
        AppLogger.Warn("全局鼠标取消键监听安装失败，本次组合透传，错误码：" + Marshal.GetLastWin32Error());
        return false;
    }

    private bool HasKeyboardRefreshInput => _prefix.NeedsHook || HasSharedTail
        || _ownedAction != null || _gesture != null || _menuMask != 0 || _escapeOwned
        || _physicalKeys.Count != 0 || _peerRestoredKeys.Count != 0
        || _keyboardOwnedKeys.Count != 0 || _restoredKeyboardOwnedKeys.Count != 0
        || _sharedReleases.Count != 0 || _sharedKeyboard.Held.Count != 0 || _sharedKeyboard.Pending.Count != 0;

    private string? KeyboardRefreshBlocker()
    {
        if (_recordingPaused) return "正在录制快捷键";
        if (_prefixHookFailed || _prefix.Disabled) return "当前输入恢复已暂停";
        if (_keyboardHook == IntPtr.Zero || _keyboardBindings.Length == 0) return "附加键盘监听尚未启用";
        // 保留整份输入状态的替换不能等待可能已被第三方吞掉的松开；只避开正在交付的回调。
        if (_keyboardCallbackDepth != 0) return "正在交付键盘事件";
        nint foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        // 录制入口在创建录制器之前同步暂停；普通设置根窗口沿用既有允许边界。
        if (_testInputTag == 0 && IsBlockedOwnWindowForProcess(foreground, foregroundProcess)) return "软件内部交互窗口";
        return null;
    }

    private bool RefreshKeyboardHook()
    {
        bool activeInput = HasKeyboardRefreshInput;
        int generation = unchecked(_keyboardHookGeneration + 1);
        var replacementProc = CreateKeyboardProc(generation);
        var replacement = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, replacementProc,
            NativeMethods.GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero)
        {
            AppLogger.Warn("第三方新实例启动后刷新键盘监听失败，保留原监听，错误码：" + Marshal.GetLastWin32Error());
            return false;
        }
        var previous = _keyboardHook;
        var previousProc = _keyboardProc;
        _keyboardHookGeneration = generation;
        _keyboardProc = replacementProc;
        _keyboardHook = replacement;
        // 旧委托保活到服务结束，覆盖卸载后仍在途的原生回调。
        _keyboardProcKeepAlive.Add(previousProc);
        if (previous != IntPtr.Zero && !NativeMethods.UnhookWindowsHookEx(previous))
            _retiredKeyboardHooks.Add((previous, previousProc));
        GC.KeepAlive(previousProc);
        // 不清空候选、物理／恢复按住、主键认领、回放确认或对方认领标记，后续松开由新监听继续处理。
        AppLogger.Info("检测到 Quicker 新实例：已刷新一次键盘监听" + (activeInput ? "（保留在途输入）" : ""));
        return true;
    }

    private NativeMethods.LowLevelKeyboardProc CreateKeyboardProc(int generation) => (code, message, pointer) =>
    {
        if (code < 0 || _disposed) return NextKeyboardHook(code, message, pointer);
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(pointer);
        var current = new KeyboardHookEvent(message, data.vkCode, data.scanCode, data.flags, data.time, data.dwExtraInfo);
        // 只略过当前调用链中由新代处理过的同一事件；旧代独立迟到的松开仍必须更新状态。
        if (generation != _keyboardHookGeneration && _activeKeyboardEvents.Contains(current))
            return NextKeyboardHook(code, message, pointer);
        _activeKeyboardEvents.Add(current);
        try { return KeyboardCallback(code, message, pointer); }
        finally { _activeKeyboardEvents.RemoveAt(_activeKeyboardEvents.Count - 1); }
    };

    private void Unhook()
    {
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        for (int i = _retiredKeyboardHooks.Count - 1; i >= 0; i--)
            if (NativeMethods.UnhookWindowsHookEx(_retiredKeyboardHooks[i].Hook)) _retiredKeyboardHooks.RemoveAt(i);
        _mouseHook = _keyboardHook = IntPtr.Zero;
        _menuMask = 0;
        _escapeOwned = false;
        if (_prefixTimer) { MousePrefixNative.KillTimer(_pump.Handle, 82); _prefixTimer = false; }
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        int message = (int)wParam;
        // 未认领手势时的高频移动只透传，不读取结构、按键状态或绑定目录。
        if (message == 0x0200 && _gesture == null)
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (_testInputTag != 0 && (nuint)data.dwExtraInfo != _testInputTag && (nuint)data.dwExtraInfo != _prefix.ReplayTag)
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            if ((nuint)data.dwExtraInfo == _prefix.ReplayTag)
            {
                bool release = message is 0x202 or 0x205 or 0x208 or 0x20C;
                if (!_prefix.ObserveReplay(release)) return new IntPtr(1);
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }
            if ((data.flags & NativeMethods.LLMHF_INJECTED) != 0 && (_testInputTag == 0 || (nuint)data.dwExtraInfo != _testInputTag))
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            var point = new Point(data.pt.X, data.pt.Y);
            if (message == 0x0200 && _gesture != null)
                _gesture.Update(point, false);

            var (button, down) = DecodeButton(message, data.mouseData);
            if (_ownedAction != null)
            {
                if (button == _ownedAction.MouseButton)
                {
                    if (!down)
                    {
                        var action = _ownedAction;
                        var gesture = _gesture!;
                        gesture.Update(point, true);
                        var state = gesture.Read();
                        _ownedAction = null;
                        _gesture = null;
                        // 点击在松手才执行；等待期间焦点／录制状态变了就作废，但已认领的松手仍配对拦截。
                        if (action.MouseTrigger == ActionMouseTrigger.Click && !state.Cancelled && !state.Moved
                            && CanStart(action) && !IsBlockedOwnWindow(NativeMethods.GetForegroundWindow())
                            && !IsBlockedOwnWindow(NativeMethods.WindowFromPoint(
                                new NativeMethods.POINT_INT { X = data.pt.X, Y = data.pt.Y })))
                            _dispatch(action, null);
                        SyncHooks();
                    }
                    return new IntPtr(1);
                }
            }
            else if (button != ActionMouseButton.None && down)
            {
                uint modifiers = ReadModifiers();
                foreach (var action in _bindings)
                {
                    uint effective = modifiers & ~action.MousePrefixKey.ModifierMask;
                    if (action.MouseButton != button || ActionMouseBinding.EffectiveModifiers(action) != effective || !CanStart(action)) continue;
                    if (action.MousePrefixKey.IsValid ? !_prefix.CanClaim(action.MousePrefixKey) : _prefix.HasPending || _prefix.IsDraining || _prefix.IsConsumed) continue;
                    if ((_prefix.HeldModifiers & ActionKeyboardBinding.ExtendedMask
                        & ~ActionKeyboardBinding.ModifierOf(action.MousePrefixKey.VirtualKey)) != 0) continue;
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    if (IsBlockedOwnWindow(foreground)) break;
                    IntPtr hitWindow = NativeMethods.WindowFromPoint(new NativeMethods.POINT_INT { X = data.pt.X, Y = data.pt.Y });
                    if (IsBlockedOwnWindow(hitWindow)) break;
                    // 已有另一个鼠标键按下时属于原窗口的操作，不在拖动中途抢走新按键。
                    if (HasOtherButtonDown(button) || !EnsureGestureKeyboardHook()) break;
                    _ownedAction = action;
                    if (action.MousePrefixKey.IsValid) _prefix.Consume();
                    _gesture = new MouseActionGesture(point);
                    _menuMask |= modifiers & 9;
                    if ((modifiers & 9) != 0) MaskSystemMenu();
                    if (action.MouseTrigger == ActionMouseTrigger.Drag) _dispatch(action, _gesture);
                    return new IntPtr(1);
                }
            }
            if (message is not 0x200 && MousePrefixNative.Mouse(message, data) is var delayed && delayed.U.mi.dwFlags != 0
                && _prefix.Mouse(delayed)) return new IntPtr(1);
        }
        catch (Exception ex)
        {
            _gesture?.Cancel();
            AppLogger.Warn("全局鼠标处理失败：" + ex.Message);
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool IsBlockedOwnWindow(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        return IsBlockedOwnWindowForProcess(window, process);
    }

    private bool IsBlockedKeyboardWindow(IntPtr window) => IsBlockedOwnWindow(window)
        && _allowKeyboardInOwnWindow?.Invoke(window) != true;

    private bool IsBlockedOwnWindowForProcess(IntPtr window, uint process)
    {
        if (process != _processId) return false;

        // 设置页允许使用已保存的组合；只归并子窗口，不沿 owner 链放行动作编辑等独立窗口。
        IntPtr settings = Volatile.Read(ref _allowedSettingsWindow);
        return settings == IntPtr.Zero
            || (window != settings && NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT) != settings);
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        _keyboardCallbackDepth++;
        try { return ProcessKeyboardCallback(code, wParam, lParam); }
        finally { _keyboardCallbackDepth--; }
    }

    private IntPtr ProcessKeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (_testInputTag != 0 && (nuint)data.dwExtraInfo != _testInputTag && (nuint)data.dwExtraInfo != _prefix.ReplayTag
                && (nuint)data.dwExtraInfo != QuickerRestoreTag)
                return NextKeyboardHook(code, wParam, lParam);
            if ((nuint)data.dwExtraInfo == _prefix.ReplayTag)
            {
                if (!_prefix.ObserveReplay(((int)wParam) is 0x101 or 0x105)) return new IntPtr(1);
                return NextKeyboardHook(code, wParam, lParam);
            }
            bool restored = (_keyboardBindings.Length != 0 || HasSharedTail || _peerRestoredKeys.Count != 0
                || _restoredKeyboardOwnedKeys.Count != 0) && (nuint)data.dwExtraInfo == QuickerRestoreTag;
            // 当前真实松开在补读 Raw Input 前标记，避免把同一事件误判成钩子漏收。
            if (!restored && ((data.flags & 0x10) == 0 || _testInputTag != 0 && (nuint)data.dwExtraInfo == _testInputTag)
                && ((int)wParam) is 0x101 or 0x105) _physicalKeys.Remove(data.vkCode);
            if (!restored && ((data.flags & 0x10) == 0 || _testInputTag != 0 && (nuint)data.dwExtraInfo == _testInputTag)
                && ((int)wParam) is 0x100 or 0x104) _sharedKeyboard.SeenByHook.Add(data.vkCode);
            _sharedKeyboard.Drain(data.time);
            if (restored && SuppressPeerRestore(data.vkCode, ((int)wParam) is 0x101 or 0x105)) return new IntPtr(1);
            if (restored && ((int)wParam) is 0x101 or 0x105
                && (_keyboardOwnedKeys.Contains(data.vkCode) || !_prefixFromPeerRestore && _physicalKeys.Contains(data.vkCode)
                    && (ActionKeyboardBinding.ModifierOf(data.vkCode) & _prefix.HeldModifiers & ActionKeyboardBinding.ExtendedMask) != 0))
            {
                // 上一恢复周期的迟到松开不能结束当前物理按下。
                _peerRestoredKeys.Remove(data.vkCode); _restoredKeyboardOwnedKeys.Remove(data.vkCode);
                return new IntPtr(1);
            }
            if (restored && _yieldedToPeer) return NextKeyboardHook(code, wParam, lParam);
            if ((data.flags & 0x10) == 0 || restored || (_testInputTag != 0 && (nuint)data.dwExtraInfo == _testInputTag))
            {
                int message = (int)wParam;
                bool down = message is 0x100 or 0x104;
                bool up = message is 0x101 or 0x105;
                if (!down && !up) return NextKeyboardHook(code, wParam, lParam);
                var key = MousePrefixNative.Key(data);
                var sourceKeys = restored ? _peerRestoredKeys : _physicalKeys;
                bool fresh = down && sourceKeys.Add(data.vkCode);
                if (fresh && !restored)
                { _rawReleasedOwnedKeys.Remove(data.vkCode); _rawReleasedSharedKeys.Remove(data.vkCode); }
                if (up) sourceKeys.Remove(data.vkCode);
                if (!restored && up) _peerRestoredKeys.Remove(data.vkCode);
                if (!restored && fresh && (ActionKeyboardBinding.ModifierOf(data.vkCode) & ActionKeyboardBinding.NativeMask) == 0)
                    AbandonInterruptedRestore();
                if (down && !restored) _sharedKeyboard.SeenByHook.Add(data.vkCode);
                bool sharedRelease = up && (_sharedReleases.Remove(data.vkCode)
                    || !restored && _rawReleasedSharedKeys.Remove(data.vkCode));
                if (sharedRelease && (ActionKeyboardBinding.ModifierOf(data.vkCode) & _sharedConsumedModifiers) != 0)
                    _sharedConsumedAt = Stopwatch.GetTimestamp();
                nint sharedReleaseResult = sharedRelease ? NextKeyboardHook(code, wParam, lParam) : 0;
                if (sharedRelease && _sharedReleases.Count == 0) _yieldedToPeer = false;
                if (!restored && up && _rawReleasedOwnedKeys.Remove(data.vkCode)) return new IntPtr(1);
                // 已认领的主键独立配对，先松引导键也不能把主键松开漏给前台。
                if (!restored && up && _restoredKeyboardOwnedKeys.Remove(data.vkCode))
                { _keyboardOwnedKeys.Remove(data.vkCode); SyncHooks(); return new IntPtr(1); }
                var ownedKeys = restored ? _restoredKeyboardOwnedKeys : _keyboardOwnedKeys;
                if (ownedKeys.Contains(data.vkCode))
                {
                    if (up) { ownedKeys.Remove(data.vkCode); SyncHooks(); }
                    return new IntPtr(1);
                }
                if (fresh && TryKeyboardCombination(key, data))
                    return ActionKeyboardBinding.ModifierOf(key.VirtualKey) != 0
                        ? ObservePrefixChain(code, wParam, lParam, key.VirtualKey) : new IntPtr(1);
                if (!restored && fresh && TrySharedKeyboardCombination(key.VirtualKey)) return new IntPtr(1);
                // Ctrl／Shift／Alt 也可能是对方组合的主键；本方未匹配时须交付当次真实按下，不能改走延迟补发。
                if (fresh && down && TryYieldPrefix(code, wParam, lParam, out nint yielded)) return yielded;
                if (_prefix.Keyboard(key, up, MousePrefixNative.Keyboard(data, up)))
                {
                    if (up) _menuMask &= ~(ActionKeyboardBinding.ModifierOf(key.VirtualKey) & 9);
                    SyncHooks(); return new IntPtr(1);
                }
                if (fresh && !_prefix.Disabled && !_prefix.NeedsHook && _ownedAction == null)
                {
                    if (TryBeginKeyboardPrefix(key, data)) return ObservePrefixChain(code, wParam, lParam, key.VirtualKey);
                    uint modifiers = ReadModifiers() & ~key.ModifierMask;
                    foreach (var binding in _bindings)
                    {
                        if (!binding.MousePrefixKey.IsValid || !binding.MousePrefixKey.Matches(key)
                            || ActionMouseBinding.EffectiveModifiers(binding) != modifiers || !CanStart(binding)) continue;
                        // 连续打字中另一个普通键未松开时不接管；系统快捷键的后键也不会变成鼠标引导。
                        bool otherHeld = false;
                        foreach (uint held in _physicalKeys)
                            if (held != data.vkCode && new ActionMouseKey(held, 0, false).ModifierMask == 0) { otherHeld = true; break; }
                        if (otherHeld || HasOtherButtonDown(ActionMouseButton.None)) break;
                        var context = MousePrefixNative.ReadContext();
                        if (IsBlockedOwnWindow(context.Foreground) || !MousePrefixNative.CanReplay(context)) break;
                        _prefixContext = context;
                        if (_prefix.Begin(key, MousePrefixNative.Keyboard(data, false)))
                        { _prefixFromPeerRestore = false; return new IntPtr(1); }
                    }
                }
                if (data.vkCode == 0x1B && (_gesture != null || _escapeOwned))
                {
                    if (down) { _escapeOwned = true; _gesture?.Cancel(); }
                    if (up) { _escapeOwned = false; SyncHooks(); }
                    return new IntPtr(1);
                }
                uint menu = data.vkCode is 0x5B or 0x5C ? 8u : data.vkCode is 0x12 or 0xA4 or 0xA5 ? 1u : 0;
                if (up && (_menuMask & menu) != 0)
                {
                    MaskSystemMenu();
                    _menuMask &= ~menu;
                    SyncHooks();
                }
                if (sharedRelease) return sharedReleaseResult;
            }
        }
        }
        catch (Exception ex) { _gesture?.Cancel(); _prefix.Fail("引导键处理异常：" + ex.Message); }
        return NextKeyboardHook(code, wParam, lParam);
    }

    // 产品始终调用原生后续钩子；后台夹具仅替换该出口，不安装钩子或生成系统按键。
    private IntPtr NextKeyboardHook(int code, IntPtr message, IntPtr data) =>
        _testKeyboardChain?.Invoke(code, message, data) ?? NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);

    private uint PhysicalModifiers()
    {
        uint modifiers = 0;
        foreach (uint held in _physicalKeys) modifiers |= ActionKeyboardBinding.ModifierOf(held);
        return modifiers;
    }

    private uint KeyboardModifiers(bool restored)
    {
        uint modifiers = PhysicalModifiers();
        if (restored)
            foreach (uint key in _peerRestoredKeys) modifiers |= ActionKeyboardBinding.ModifierOf(key);
        return modifiers;
    }

    private void AbandonInterruptedRestore()
    {
        if (!_prefixFromPeerRestore || _prefix.IsDraining || !_prefix.IsConsumed && !_prefix.HasPending) return;
        // 恢复流不是物理按住状态。新物理普通键到达时不能沿用其漏收的辅助键松开。
        foreach (uint key in _peerRestoredKeys)
        {
            if (_prefix.ReleaseUnobservedKey(key) || _restoredKeyboardOwnedKeys.Contains(key))
                _abandonedRestoreReleases.Add(key);
            _sharedReleases.Remove(key);
        }
        if (_abandonedRestoreReleases.Count != 0) _abandonedRestoreAt = Stopwatch.GetTimestamp();
        _peerRestoredKeys.Clear();
        _restoredKeyboardOwnedKeys.Clear();
        if (!_prefix.NeedsHook) _prefixFromPeerRestore = false;
    }

    private bool TryBeginKeyboardPrefix(ActionMouseKey key, NativeMethods.KBDLLHOOKSTRUCT data)
    {
        uint bit = ActionKeyboardBinding.ModifierOf(key.VirtualKey) & ActionKeyboardBinding.ExtendedMask;
        if (bit == 0 || HasOtherButtonDown(ActionMouseButton.None)) return false;
        foreach (uint held in _physicalKeys)
            if (held != key.VirtualKey && (ActionKeyboardBinding.ModifierOf(held) & ActionKeyboardBinding.NativeMask) == 0) return false;
        uint modifiers = KeyboardModifiers((nuint)data.dwExtraInfo == QuickerRestoreTag);
        if (!Array.Exists(_keyboardBindings, action => (action.HotKeyModifiers & bit) != 0
            && (modifiers & ~action.HotKeyModifiers) == 0 && CanStartKeyboard(action))) return false;
        var context = MousePrefixNative.ReadContext();
        if (IsBlockedKeyboardWindow(context.Foreground) || !MousePrefixNative.CanReplay(context)) return false;
        _prefixContext = context;
        _peerClaimedPrefix = false;
        _prefixFromPeerRestore = (nuint)data.dwExtraInfo == QuickerRestoreTag;
        _sharedConsumedModifiers = 0;
        return _prefix.Begin(key, MousePrefixNative.Keyboard(data, false));
    }

    private bool TryKeyboardCombination(ActionMouseKey key, NativeMethods.KBDLLHOOKSTRUCT data)
    {
        if (_ownedAction != null || !_prefix.CanClaimKeyboard
            || (_prefix.HeldModifiers & ActionKeyboardBinding.ExtendedMask) == 0
            || IsBlockedKeyboardWindow(_prefixContext.Foreground)
            || MousePrefixNative.ReadContext() != _prefixContext) return false;
        bool restored = (nuint)data.dwExtraInfo == QuickerRestoreTag;
        uint modifiers = KeyboardModifiers(restored);
        uint bit = ActionKeyboardBinding.ModifierOf(key.VirtualKey);
        if (bit != 0)
        {
            // 辅助键可连续叠加，原生修饰键后按时也与引导键一起暂存／消费。
            if (Array.Exists(_keyboardBindings, action => (modifiers & ~action.HotKeyModifiers) == 0 && CanStartKeyboard(action)))
            {
                return _prefix.AddModifier(key, MousePrefixNative.Keyboard(data, false));
            }
            return false;
        }
        uint ownedExtended = _prefix.HeldModifiers & ActionKeyboardBinding.ExtendedMask;
        if ((modifiers & ActionKeyboardBinding.ExtendedMask) != ownedExtended) return false;
        foreach (uint held in _physicalKeys)
            if (held != key.VirtualKey && ActionKeyboardBinding.ModifierOf(held) == 0) return false;
        foreach (var action in _keyboardBindings)
        {
            if (action.HotKeyVirtualKey != key.VirtualKey || action.HotKeyModifiers != modifiers || !CanStartKeyboard(action)) continue;
            _prefix.Consume();
            _sharedKeyboard.Handled.Add(key.VirtualKey);
            // 对方恢复流本身就是本次组合的来源，不能把它再当作本方物理周期的重复补发。
            // 否则 500ms 收尾会吞掉下一次合法恢复组合，快速连按只触发第一轮。
            if (_peerClaimedPrefix && !_prefixFromPeerRestore) RememberSharedConsumption(modifiers);
            (restored ? _restoredKeyboardOwnedKeys : _keyboardOwnedKeys).Add(key.VirtualKey);
            _menuMask |= modifiers & 9;
            if ((modifiers & 9) != 0) MaskSystemMenu();
            _dispatchKeyboard(action);
            return true;
        }
        return false;
    }

    private bool TryYieldPrefix(int code, nint message, nint current, out nint result)
    {
        result = 0;
        if (!_prefix.HasPending || _prefix.IsDraining || _sharedReleases.Count == 0
            || (_prefix.HeldModifiers & ActionKeyboardBinding.ExtendedMask) == 0
            || MousePrefixNative.ReadContext() != _prefixContext) return false;
        result = NextKeyboardHook(code, message, current);
        if (result == 0) return false;
        _prefix.Consume();
        _yieldedToPeer = true;
        return true;
    }

    private nint ObservePrefixChain(int code, nint message, nint data, uint key)
    {
        // 对方观察真实的辅助键；最后仍由本回调阻止它直接输入到前台。
        // 不能在后续主键回调里换 lParam 补前导键：跨线程钩子会继续收到当次主键。
        nint next = NextKeyboardHook(code, message, data);
        _peerClaimedPrefix |= next != 0 || (nuint)Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(data).dwExtraInfo == QuickerRestoreTag;
        _sharedReleases.Add(key);
        return new IntPtr(1);
    }

    private bool TrySharedKeyboardCombination(uint key)
    {
        if (_prefix.NeedsHook || (_sharedKeyboard.Modifiers & ActionKeyboardBinding.ExtendedMask) == 0
            || IsBlockedKeyboardWindow(NativeMethods.GetForegroundWindow())) return false;
        uint modifiers = _sharedKeyboard.Modifiers | (PhysicalModifiers() & ActionKeyboardBinding.NativeMask);
        foreach (var action in _keyboardBindings)
        {
            if (action.HotKeyVirtualKey != key || action.HotKeyModifiers != modifiers || !CanStartKeyboard(action)) continue;
            _keyboardOwnedKeys.Add(key); _sharedKeyboard.Handled.Add(key);
            RememberSharedConsumption(modifiers);
            _dispatchKeyboard(action);
            return true;
        }
        return false;
    }

    private void ObserveSharedKey(uint key, bool up)
    {
        if (!up)
        {
            if ((ActionKeyboardBinding.ModifierOf(key) & ActionKeyboardBinding.ExtendedMask) != 0
                && (_sharedKeyboard.Modifiers & ActionKeyboardBinding.ExtendedMask) == 0
                && !_physicalKeys.Contains(key))
                _sharedConsumedModifiers = 0;
            if (!_sharedKeyboard.Held.Add(key)) return;
            uint modifiers = _sharedKeyboard.Modifiers;
            foreach (var action in _keyboardBindings)
                if (action.HotKeyVirtualKey == key && action.HotKeyModifiers == modifiers && CanStartKeyboard(action))
                {
                    var context = MousePrefixNative.ReadContext();
                    if (context != default && !IsBlockedKeyboardWindow(context.Foreground))
                        _sharedKeyboard.Pending[key] = (action, context);
                    break;
                }
            return;
        }
        _sharedKeyboard.Held.Remove(key);
        if (_physicalKeys.Remove(key))
        {
            // 对方先吞了松开时，Raw Input 仍须清掉本方物理状态、主键认领和辅助键周期。
            bool owned = _keyboardOwnedKeys.Remove(key);
            bool prefixOwned = _prefix.ReleaseUnobservedKey(key);
            // 原始输入已确认松开时不能永久保留活动共享认领；真实松开迟到仍须交给下游一次。
            if (_sharedReleases.Remove(key))
            {
                _rawReleasedSharedKeys.Add(key);
                if (_sharedReleases.Count == 0) _yieldedToPeer = false;
            }
            if (owned || prefixOwned) _rawReleasedOwnedKeys.Add(key);
            _menuMask &= ~(ActionKeyboardBinding.ModifierOf(key) & 9);
        }
        if ((ActionKeyboardBinding.ModifierOf(key) & _sharedConsumedModifiers & ActionKeyboardBinding.ExtendedMask) != 0)
            _sharedConsumedAt = Stopwatch.GetTimestamp();
        bool handled = _sharedKeyboard.Handled.Remove(key);
        bool seen = _sharedKeyboard.SeenByHook.Remove(key);
        if (!_sharedKeyboard.Pending.Remove(key, out var pending) || handled || seen
            || !Array.Exists(_keyboardBindings, action => action.Id == pending.Action.Id
                && action.HotKeyModifiers == pending.Action.HotKeyModifiers && action.HotKeyVirtualKey == key)
            || !CanStartKeyboard(pending.Action) || pending.Context == default || IsBlockedKeyboardWindow(pending.Context.Foreground)
            || pending.Context != MousePrefixNative.ReadContext()) return;
        // 外部钩子连主键也认领时，从物理按下／松开恢复一次动作，不通过模拟按键触发自身。
        RememberSharedConsumption(pending.Action.HotKeyModifiers);
        _dispatchKeyboard(pending.Action);
    }

    private void RememberSharedConsumption(uint modifiers)
    {
        _sharedConsumedModifiers = modifiers;
        _sharedConsumedAt = Stopwatch.GetTimestamp();
    }

    private bool HasSharedTail => _restoredReleases.Count != 0
        || _abandonedRestoreReleases.Count != 0 && Stopwatch.GetElapsedTime(_abandonedRestoreAt).TotalMilliseconds < 500
        || _sharedConsumedModifiers != 0
        && (Stopwatch.GetElapsedTime(_sharedConsumedAt).TotalMilliseconds < 500
            || (_sharedKeyboard.Modifiers & _sharedConsumedModifiers & ActionKeyboardBinding.ExtendedMask) != 0);

    private bool SuppressPeerRestore(uint key, bool up)
    {
        if (up && _abandonedRestoreReleases.Remove(key)) return true;
        if (!up) _abandonedRestoreReleases.Remove(key);
        if (up && _restoredReleases.Remove(key))
        {
            if (_restoredReleases.Count == 0 && (PhysicalModifiers() & _sharedConsumedModifiers & ActionKeyboardBinding.ExtendedMask) == 0)
                _sharedConsumedModifiers = 0;
            return true;
        }
        // 原辅助键仍由本方暂存／补发时，不能再接一份对方的单按恢复。
        if (!up && !_yieldedToPeer && _peerClaimedPrefix && _prefix.NeedsHook
            && (ActionKeyboardBinding.ModifierOf(key) & _prefix.HeldModifiers & ActionKeyboardBinding.ExtendedMask) != 0)
        { _restoredReleases.Add(key); return true; }
        if (up || Stopwatch.GetElapsedTime(_sharedConsumedAt).TotalMilliseconds > 500
            && (_sharedKeyboard.Modifiers & _sharedConsumedModifiers & ActionKeyboardBinding.ExtendedMask) == 0) return false;
        if ((ActionKeyboardBinding.ModifierOf(key) & _sharedConsumedModifiers & ActionKeyboardBinding.ExtendedMask) == 0
            && !Array.Exists(_keyboardBindings, action => action.HotKeyVirtualKey == key && action.HotKeyModifiers == _sharedConsumedModifiers)) return false;
        _restoredReleases.Add(key);
        return true;
    }

    private static void MaskSystemMenu()
    {
        // 辅助键已经传给系统，补一对无操作键标记组合已被使用；不吞辅助键松开以免逻辑粘键。
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
        input.U.ki.wVk = 0xE8;
        NativeMethods.SendInput(1, ref input, Marshal.SizeOf<NativeMethods.INPUT>());
        input.U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
        NativeMethods.SendInput(1, ref input, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static uint ReadModifiers()
    {
        bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
        return (Down(0x10) ? 4u : 0) | (Down(0x11) ? 2u : 0) | (Down(0x12) ? 1u : 0)
            | (Down(0x5B) || Down(0x5C) ? 8u : 0);
    }

    private static bool HasOtherButtonDown(ActionMouseButton button)
    {
        bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
        return (button != ActionMouseButton.Left && Down(0x01))
            || (button != ActionMouseButton.Right && Down(0x02))
            || (button != ActionMouseButton.Middle && Down(0x04))
            || (button != ActionMouseButton.X1 && Down(0x05))
            || (button != ActionMouseButton.X2 && Down(0x06));
    }

    private static (ActionMouseButton Button, bool Down) DecodeButton(int message, uint data) => message switch
    {
        0x0201 => (ActionMouseButton.Left, true), 0x0202 => (ActionMouseButton.Left, false),
        0x0204 => (ActionMouseButton.Right, true), 0x0205 => (ActionMouseButton.Right, false),
        0x0207 => (ActionMouseButton.Middle, true), 0x0208 => (ActionMouseButton.Middle, false),
        0x020B => ((data >> 16) == 1 ? ActionMouseButton.X1 : ActionMouseButton.X2, true),
        0x020C => ((data >> 16) == 1 ? ActionMouseButton.X1 : ActionMouseButton.X2, false),
        _ => (ActionMouseButton.None, false),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peerStartup?.Dispose();
        _pump.InvokeOnPumpThread(() => { _gesture?.Cancel(); _prefix.Replay(); _prefix.Flush(); _sharedKeyboard.Dispose(); Unhook(); return true; });
        _pump.Dispose();
    }
}
