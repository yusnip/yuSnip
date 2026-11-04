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
        /// 注册热键。优先使用 Windows RegisterHotKey；只有被占用或系统拒绝时
        /// 才回退到低级键盘钩子。RegisterHotKey 不依赖前台窗口和输入法，也不会
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

            int nativeError = 0;
            bool nativeRegistered = _msgWindow.InvokeOnPumpThread(() =>
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

            // 兼容极少数已被其他程序占用的快捷键。回调只投递到线程池，绝不在
            // WH_KEYBOARD_LL 回调里写日志或执行截图逻辑，避免触发系统超时摘钩。
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
            if (nCode >= 0 && (wParam.ToInt32() == NativeMethods.WM_KEYDOWN || wParam.ToInt32() == NativeMethods.WM_SYSKEYDOWN))
            {
                var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                uint vk = kbd.vkCode;

                bool ctrlDown = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool altDown = (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0;
                bool shiftDown = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                bool winDown = ((NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0) || ((NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0);

                uint currentModifiers = 0;
                if (altDown) currentModifiers |= 0x0001;
                if (ctrlDown) currentModifiers |= 0x0002;
                if (shiftDown) currentModifiers |= 0x0004;
                if (winDown) currentModifiers |= 0x0008;

                if (currentModifiers == _targetModifiers && vk == _targetKey)
                {
                    QueueCallback();
                    // 返回 1 吞掉按键事件，确保具有最高优先级的拦截
                    return new IntPtr(1);
                }
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
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
