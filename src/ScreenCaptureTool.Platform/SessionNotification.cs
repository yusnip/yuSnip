using System;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// WTS 会话锁屏/解锁监听（M4 降级④：锁屏自动暂停录制）。
    /// 依托现成 <see cref="MessageOnlyWindow"/> 专用消息线程：注册/注销都在泵线程上做，
    /// 事件在泵线程回调，订阅方自行切回 UI 线程。
    /// </summary>
    public sealed class SessionNotification : IDisposable
    {
        private readonly MessageOnlyWindow _window;
        private bool _registered;

        /// <summary>会话被锁（Win+L / 屏保锁屏）。泵线程触发。</summary>
        public event Action? SessionLocked;

        /// <summary>会话解锁回到桌面。泵线程触发。</summary>
        public event Action? SessionUnlocked;

        public SessionNotification()
        {
            _window = new MessageOnlyWindow();
            _window.WaitForReady();
            _window.MessageReceived += OnMessage;
            try
            {
                _registered = _window.InvokeOnPumpThread(() =>
                    NativeMethods.WTSRegisterSessionNotification(_window.Handle, NativeMethods.NOTIFY_FOR_THIS_SESSION));
            }
            catch
            {
                _registered = false;
            }
        }

        /// <summary>注册是否成功（失败时订阅方应记日志，锁屏自动暂停不可用但不影响其余功能）。</summary>
        public bool IsRegistered => _registered;

        private void OnMessage(WindowMessage msg)
        {
            if (msg.Message != WindowsMessages.WM_WTSSESSION_CHANGE) return;
            int code = msg.WParam.ToInt32();
            if (code == WindowsMessages.WTS_SESSION_LOCK) SessionLocked?.Invoke();
            else if (code == WindowsMessages.WTS_SESSION_UNLOCK) SessionUnlocked?.Invoke();
        }

        public void Dispose()
        {
            if (_registered)
            {
                _registered = false;
                try
                {
                    _window.InvokeOnPumpThread(() =>
                    {
                        NativeMethods.WTSUnRegisterSessionNotification(_window.Handle);
                        return 0;
                    }, timeoutMs: 1000);
                }
                catch { }
            }
            _window.MessageReceived -= OnMessage;
            _window.Dispose();
        }
    }
}
