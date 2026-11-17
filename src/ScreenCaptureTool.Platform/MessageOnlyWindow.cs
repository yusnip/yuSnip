using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// 消息载荷：当 message-only window 收到上层关心的消息时触发。
    /// </summary>
    public readonly struct WindowMessage
    {
        public readonly IntPtr Hwnd;
        public readonly uint Message;
        public readonly IntPtr WParam;
        public readonly IntPtr LParam;

        public WindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            Hwnd = hwnd;
            Message = message;
            WParam = wParam;
            LParam = lParam;
        }
    }

    /// <summary>
    /// 隐藏的 Win32 message-only window。运行在独立后台线程上，自带消息泵。
    /// 提供给 Core 的 HotKeyManager 等需要 Win32 消息回调的功能使用。
    ///
    /// 设计要点：
    /// - 窗口创建、销毁、消息泵全部在内部专用线程上完成，避免句柄跨线程归属问题
    /// - 类名带 GUID 后缀，避免和系统其他实例冲突
    /// - 通过 <see cref="WaitForReady(int)"/> 让调用方在主线程上同步等待 Handle 可用
    /// - 不抢焦点（WS_EX_NOACTIVATE），不可见（HWND_MESSAGE）
    /// </summary>
    public sealed class MessageOnlyWindow : IDisposable
    {
        private readonly string _className = "ScreenCaptureTool_MsgOnly_" + Guid.NewGuid().ToString("N");
        private const uint WM_INVOKE_ON_PUMP = WindowsMessages.WM_USER + 1;

        private readonly Thread _pumpThread;
        private readonly ManualResetEventSlim _readySignal = new ManualResetEventSlim(false);
        private readonly NativeMethods.WndProcDelegate _wndProcKeepAlive;
        private readonly object _invokeLock = new object();
        private readonly Queue<Action> _invokeQueue = new Queue<Action>();

        private IntPtr _hwnd;
        private uint _threadId;
        private ushort _atom;
        private volatile bool _disposed;

        /// <summary>
        /// 收到任何消息时触发，在内部消息泵线程上调用。
        /// 订阅方需要自己处理线程切换。
        /// </summary>
        public event Action<WindowMessage>? MessageReceived;

        public IntPtr Handle => _hwnd;

        /// <summary>
        /// 在消息泵线程上同步执行 Win32 调用。
        /// RegisterHotKey 等 API 要求目标窗口属于当前调用线程，不能在 Avalonia UI 线程直接调用。
        /// </summary>
        public T InvokeOnPumpThread<T>(Func<T> func, int timeoutMs = 3000)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            WaitForReady(timeoutMs);

            if (NativeMethods.GetCurrentThreadId() == _threadId)
            {
                return func();
            }

            T result = default!;
            Exception? exception = null;
            using ManualResetEventSlim done = new ManualResetEventSlim(false);

            lock (_invokeLock)
            {
                _invokeQueue.Enqueue(() =>
                {
                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
            }

            if (!NativeMethods.PostThreadMessage(_threadId, WM_INVOKE_ON_PUMP, IntPtr.Zero, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("PostThreadMessage 失败，错误码：" + err);
            }

            if (!done.Wait(timeoutMs))
            {
                throw new TimeoutException("消息泵线程未在指定时间内执行委托。");
            }

            if (exception != null) throw exception;
            return result;
        }

        public MessageOnlyWindow()
        {
            _wndProcKeepAlive = WndProc;

            _pumpThread = new Thread(PumpThreadMain)
            {
                IsBackground = true,
                Name = "ScreenCaptureTool.MsgOnlyWindow"
            };
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();
        }

        /// <summary>
        /// 阻塞等待消息窗口创建完成。失败抛 <see cref="TimeoutException"/>。
        /// </summary>
        public void WaitForReady(int timeoutMs = 3000)
        {
            if (!_readySignal.Wait(timeoutMs))
            {
                throw new TimeoutException("MessageOnlyWindow 消息窗口未在指定时间内创建完成。");
            }
            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("MessageOnlyWindow 句柄创建失败。");
            }
        }

        private void PumpThreadMain()
        {
            try
            {
                _threadId = NativeMethods.GetCurrentThreadId();
                IntPtr hInstance = NativeMethods.GetModuleHandle(null);

                NativeMethods.WNDCLASSEX wc = default;
                wc.cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>();
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
                wc.hInstance = hInstance;
                wc.lpszClassName = _className;

                _atom = NativeMethods.RegisterClassExW(ref wc);
                if (_atom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("RegisterClassExW 失败，错误码：" + err);
                }

                _hwnd = NativeMethods.CreateWindowExW(
                    WindowsMessages.WS_EX_NOACTIVATE,
                    _className,
                    null,
                    0,
                    0, 0, 0, 0,
                    WindowsMessages.HWND_MESSAGE,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("CreateWindowExW 失败，错误码：" + err);
                }
            }
            catch
            {
                // 创建失败也要把 ready 信号放出，避免主线程死等
                _readySignal.Set();
                return;
            }
            finally
            {
                _readySignal.Set();
            }

            // 标准消息泵
            while (NativeMethods.GetMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_INVOKE_ON_PUMP)
                {
                    DrainInvokeQueue();
                    continue;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }

        private void DrainInvokeQueue()
        {
            while (true)
            {
                Action? action;
                lock (_invokeLock)
                {
                    if (_invokeQueue.Count == 0) return;
                    action = _invokeQueue.Dequeue();
                }

                action();
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                MessageReceived?.Invoke(new WindowMessage(hWnd, msg, wParam, lParam));
            }
            catch
            {
                // 任何回调里的异常都不能让消息泵崩溃
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 让消息泵线程退出：投递 WM_QUIT 到该线程
            try
            {
                if (_threadId != 0)
                {
                    NativeMethods.PostThreadMessage(_threadId, WindowsMessages.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }

            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
            catch { }

            try
            {
                IntPtr hInstance = NativeMethods.GetModuleHandle(null);
                NativeMethods.UnregisterClassW(_className, hInstance);
            }
            catch { }

            _readySignal.Dispose();
        }
    }
}
