#nullable disable

using System;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 全局热键管理。阶段 2 改造自旧 <c>ScreenCaptureStandalone.HotKeyManager</c>。
    ///
    /// 改造要点（去 WPF）：
    /// - 旧实现依赖 WPF <c>HwndSource</c> 在主窗口上挂消息钩子；
    /// - 新实现把消息窗口下沉到 <see cref="ScreenCaptureTool.Platform.MessageOnlyWindow"/>，
    ///   一个独立后台线程跑 GetMessage 循环，与 Avalonia UI 完全解耦；
    /// - 收到 WM_HOTKEY 后从消息泵线程触发回调，<b>调用方需要自己切回 UI 线程</b>
    ///   （由主项目里 Avalonia.Threading.Dispatcher.UIThread.Post 完成）。
    ///
    /// 使用：
    /// <code>
    /// using var mgr = new HotKeyManager(() => Console.WriteLine("hit"));
    /// mgr.Register(modifiers: 0x0003, virtualKey: 0x41); // Ctrl+Alt+A
    /// </code>
    /// </summary>
    public sealed class HotKeyManager : IDisposable
    {
        private const int HOTKEY_ID_CAPTURE = 0x5343;

        private readonly Action _callback;
        private readonly int _hotKeyId;
        private readonly MessageOnlyWindow _msgWindow;
        private readonly bool _ownsWindow;
        private bool _registered;
        private bool _registeredWithNativeHotKey;
        private bool _registeredWithHook;
        private bool _disposed;

        private IntPtr _hookHandle = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _hookProc;
        private uint _targetModifiers;
        private uint _targetKey;
        private bool? _printScreenPressOwned;

        private bool IsStandalonePrintScreen => _targetModifiers == 0 && _targetKey == NativeMethods.VK_SNAPSHOT;

        private const uint MOD_NOREPEAT = 0x4000;

        public int LastRegisterError { get; private set; }

        /// <param name="callback">热键触发回调（在消息泵线程，调用方需自行切 UI 线程）。</param>
        /// <param name="hotKeyId">本实例使用的热键 ID；多实例并存时必须各不相同（如截图 0x5343、贴图 0x5344）。</param>
        /// <param name="sharedWindow">
        /// 可选的共享消息窗口：多个 HotKeyManager 实例共用一个消息泵线程（动作热键体系），
        /// 通过不同 hotKeyId 区分。传入后本实例 Dispose 不会销毁该窗口（由共享方统一释放）。
        /// </param>
        public HotKeyManager(Action callback, int hotKeyId = HOTKEY_ID_CAPTURE, MessageOnlyWindow sharedWindow = null)
        {
            _callback = callback;
            _hotKeyId = hotKeyId;
            _ownsWindow = sharedWindow == null;
            _msgWindow = sharedWindow ?? new MessageOnlyWindow();
            _msgWindow.MessageReceived += OnWindowMessage;
        }

        /// <summary>
        /// 注册热键。单键 PrtSc 用低级键盘钩子处理 Windows 的截图绑定。
        /// 其余热键优先使用 Windows RegisterHotKey，注册失败时回退到低级键盘钩子。
        /// RegisterHotKey 不依赖前台窗口和输入法，也不会
        /// 像低级钩子那样因回调超时被 Windows 静默移除。
        /// </summary>
        public bool Register(uint modifiers, uint virtualKey)
        {
            if (_disposed) return false;
            // 附加辅助键只能由统一输入服务处理，不能回退成更宽的原生组合。
            if (!Actions.ActionKeyboardBinding.IsValid(modifiers, virtualKey)
                || Actions.ActionKeyboardBinding.IsExtended(modifiers)) return false;

            try
            {
                _msgWindow.WaitForReady();
            }
            catch (Exception ex)
            {
                AppLogger.Error("HotKeyManager: 等待消息窗口就绪失败。", ex);
                return false;
            }

            if (_msgWindow.Handle == IntPtr.Zero) return false;

            Unregister();

            _targetModifiers = modifiers;
            _targetKey = virtualKey;
            _printScreenPressOwned = null;

            int nativeError = 0;
            bool nativeRegistered = !IsStandalonePrintScreen && _msgWindow.InvokeOnPumpThread(() =>
            {
                bool ok = NativeMethods.RegisterHotKey(
                    _msgWindow.Handle,
                    _hotKeyId,
                    modifiers | MOD_NOREPEAT,
                    virtualKey);
                nativeError = ok ? 0 : Marshal.GetLastWin32Error();
                return ok;
            });

            if (nativeRegistered)
            {
                _registered = true;
                _registeredWithNativeHotKey = true;
                _registeredWithHook = false;
                LastRegisterError = 0;
                AppLogger.Info("HotKeyManager.Register 成功 (RegisterHotKey)。modifiers=0x"
                    + modifiers.ToString("X") + " vk=0x" + virtualKey.ToString("X"));
                return true;
            }

            // 单键 PrtSc 和原生注册失败的绑定走同一键盘钩子。
            int hookError = 0;
            bool hookRegistered = _msgWindow.InvokeOnPumpThread(() =>
            {
                _hookProc = HookCallback;
                IntPtr hMod = NativeMethods.GetModuleHandle(null);
                _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, hMod, 0);
                hookError = _hookHandle != IntPtr.Zero ? 0 : Marshal.GetLastWin32Error();
                return _hookHandle != IntPtr.Zero;
            });

            if (hookRegistered)
            {
                _registered = true;
                _registeredWithNativeHotKey = false;
                _registeredWithHook = true;
                LastRegisterError = 0;
                if (IsStandalonePrintScreen)
                    AppLogger.Info("HotKeyManager 单键 PrtSc 已通过键盘钩子注册。");
                else
                    AppLogger.Warn("HotKeyManager.RegisterHotKey 被拒绝，已回退到键盘钩子。error="
                        + nativeError + " modifiers=0x" + modifiers.ToString("X")
                        + " vk=0x" + virtualKey.ToString("X"));
                return true;
            }

            _registered = false;
            _registeredWithNativeHotKey = false;
            _registeredWithHook = false;
            LastRegisterError = hookError != 0 ? hookError : nativeError;
            AppLogger.Warn("HotKeyManager.Register 失败。nativeError=" + nativeError
                + " hookError=" + LastRegisterError
                + " modifiers=0x" + modifiers.ToString("X")
                + " vk=0x" + virtualKey.ToString("X"));
            return false;
        }

        public void Unregister()
        {
            if (!_registered) return;
            try
            {
                _msgWindow.InvokeOnPumpThread(() =>
                {
                    if (_registeredWithNativeHotKey)
                    {
                        NativeMethods.UnregisterHotKey(_msgWindow.Handle, _hotKeyId);
                    }
                    if (_registeredWithHook && _hookHandle != IntPtr.Zero)
                    {
                        NativeMethods.UnhookWindowsHookEx(_hookHandle);
                        _hookHandle = IntPtr.Zero;
                    }
                    return true;
                });
            }
            catch { }
            _registered = false;
            _registeredWithNativeHotKey = false;
            _registeredWithHook = false;
        }

        private void OnWindowMessage(WindowMessage message)
        {
            if (!_registeredWithNativeHotKey
                || message.Message != WindowsMessages.WM_HOTKEY
                || message.WParam.ToInt32() != _hotKeyId)
            {
                return;
            }

            QueueCallback();
        }

        private void QueueCallback()
        {
            if (_disposed) return;
            try
            {
                // 直接调用注册的回调（回调内部负责 Post 到 UI 调度器），消除 ThreadPool 调度延迟
                _callback?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Error("HotKeyManager 回调异常。", ex);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            bool down = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool up = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
            if (nCode >= 0 && (down || IsStandalonePrintScreen && up))
            {
                var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (kbd.vkCode == _targetKey)
                {
                    if (IsStandalonePrintScreen)
                    {
                        // 保留按下时的组合判断，避免先松开修饰键时误触发单键动作。
                        bool owned = _printScreenPressOwned ?? (ReadNativeModifiers() == 0);
                        _printScreenPressOwned = down ? owned : null;
                        if (owned)
                        {
                            if (up) QueueCallback();
                            return new IntPtr(1);
                        }
                    }
                    else if (ReadNativeModifiers() == _targetModifiers)
                    {
                        QueueCallback();
                        return new IntPtr(1);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static uint ReadNativeModifiers()
        {
            uint modifiers = 0;
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0) modifiers |= 0x0001;
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) modifiers |= 0x0002;
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0) modifiers |= 0x0004;
            if (((NativeMethods.GetAsyncKeyState(0x5B) | NativeMethods.GetAsyncKeyState(0x5C)) & 0x8000) != 0) modifiers |= 0x0008;
            return modifiers;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Unregister();

            try
            {
                _msgWindow.MessageReceived -= OnWindowMessage;
                if (_ownsWindow)
                {
                    _msgWindow.Dispose();
                }
            }
            catch { }
        }
    }
}
