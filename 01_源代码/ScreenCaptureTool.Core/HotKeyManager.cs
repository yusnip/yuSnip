#nullable disable

using System;
using System.Runtime.InteropServices;
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
        private bool _registered;
        private bool _disposed;

        private IntPtr _hookHandle = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _hookProc;
        private uint _targetModifiers;
        private uint _targetKey;

        public int LastRegisterError { get; private set; }

        /// <param name="callback">热键触发回调（在消息泵线程，调用方需自行切 UI 线程）。</param>
        /// <param name="hotKeyId">本实例使用的热键 ID；多实例并存时必须各不相同（如截图 0x5343、贴图 0x5344）。</param>
        public HotKeyManager(Action callback, int hotKeyId = HOTKEY_ID_CAPTURE)
        {
            _callback = callback;
            _hotKeyId = hotKeyId;
            _msgWindow = new MessageOnlyWindow();
        }

        /// <summary>
        /// 注册热键（使用全局键盘钩子，具备更高优先级）。
        /// </summary>
        public bool Register(uint modifiers, uint virtualKey)
        {
            if (_disposed) return false;

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

            LastRegisterError = 0;
            _registered = _msgWindow.InvokeOnPumpThread(() =>
            {
                _targetModifiers = modifiers;
                _targetKey = virtualKey;
                _hookProc = HookCallback;
                IntPtr hMod = NativeMethods.GetModuleHandle(null);
                _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, hMod, 0);
                LastRegisterError = _hookHandle != IntPtr.Zero ? 0 : Marshal.GetLastWin32Error();
                return _hookHandle != IntPtr.Zero;
            });

            if (!_registered)
            {
                AppLogger.Warn("HotKeyManager.Register 失败。error=" + LastRegisterError
                    + " modifiers=0x" + modifiers.ToString("X")
                    + " vk=0x" + virtualKey.ToString("X"));
            }
            else
            {
                AppLogger.Info("HotKeyManager.Register 成功 (键盘钩子方式)。modifiers=0x"
                    + modifiers.ToString("X") + " vk=0x" + virtualKey.ToString("X"));
            }

            return _registered;
        }

        public void Unregister()
        {
            if (!_registered) return;
            try
            {
                _msgWindow.InvokeOnPumpThread(() =>
                {
                    if (_hookHandle != IntPtr.Zero)
                    {
                        NativeMethods.UnhookWindowsHookEx(_hookHandle);
                        _hookHandle = IntPtr.Zero;
                    }
                    return true;
                });
            }
            catch { }
            _registered = false;
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
                    try
                    {
                        _callback?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("HotKeyManager 回调异常。", ex);
                    }
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
                _msgWindow.Dispose();
            }
            catch { }
        }
    }
}
