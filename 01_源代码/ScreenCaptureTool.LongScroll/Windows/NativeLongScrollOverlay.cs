using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>
/// 长截图原生轻量 Overlay：4 块遮罩 + 4 条边框 + 8 个手柄。
/// 不使用 Avalonia 透明顶层窗口，截图区域内部没有 HWND 覆盖。
/// </summary>
public sealed class NativeLongScrollOverlay : IDisposable
{
    private const uint InvokeMessage = WindowsMessages.WM_USER + 0x51;
    private const int BorderThicknessPx = 1;
    private const int BorderDashPx = 4;
    private const int BorderGapPx = 2;
    private const int FrameInsetPx = 3;
    private const byte MaskAlpha = 86;

    private readonly Rectangle _region;
    private readonly Rectangle _virtualScreen;
    private readonly string _maskClassName = "SCT_LongScrollMask_" + Guid.NewGuid().ToString("N");
    private readonly string _frameClassName = "SCT_LongScrollFrame_" + Guid.NewGuid().ToString("N");
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _readySignal = new(false);
    private readonly ManualResetEventSlim _shownSignal = new(false);
    private readonly object _invokeLock = new();
    private readonly Queue<Action> _invokeQueue = new();
    private readonly NativeMethods.WndProcDelegate _wndProcKeepAlive;
    private readonly List<IntPtr> _windows = new();

    private IntPtr _blackBrush;
    private IntPtr _frameBrush;
    private ushort _maskAtom;
    private ushort _frameAtom;
    private uint _threadId;
    private bool _shown;
    private volatile bool _disposed;

    public NativeLongScrollOverlay(Rectangle region)
    {
        _region = region.Width > 0 && region.Height > 0 ? region : new Rectangle(100, 100, 320, 240);
        _virtualScreen = MonitorHelper.GetVirtualScreenRect();
        _wndProcKeepAlive = WndProc;
        _thread = new Thread(PumpThreadMain)
        {
            IsBackground = true,
            Name = "LongScroll.NativeOverlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Show()
    {
        if (_disposed) return;
        WaitReady();
        InvokeOnOverlayThread(() =>
        {
            if (_shown) return;
            CreateOverlayWindows();
            _shown = true;
            _shownSignal.Set();
        });
    }

    public void BringToTop()
    {
        if (_disposed) return;
        if (!_readySignal.IsSet) return;
        InvokeOnOverlayThread(() =>
        {
            foreach (IntPtr hwnd in _windows)
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }, wait: false);
    }

    private void WaitReady(int timeoutMs = 3000)
    {
        if (!_readySignal.Wait(timeoutMs))
        {
            throw new TimeoutException("NativeLongScrollOverlay 消息泵未就绪。");
        }
    }

    private void InvokeOnOverlayThread(Action action, bool wait = true, int timeoutMs = 3000)
    {
        if (action == null) return;
        if (_disposed) return;

        if (NativeMethods.GetCurrentThreadId() == _threadId)
        {
            action();
            return;
        }

        Exception? error = null;
        using ManualResetEventSlim done = new(false);
        lock (_invokeLock)
        {
            _invokeQueue.Enqueue(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
        }

        NativeMethods.PostThreadMessage(_threadId, InvokeMessage, IntPtr.Zero, IntPtr.Zero);
        if (!wait) return;
        if (!done.Wait(timeoutMs)) throw new TimeoutException("NativeLongScrollOverlay 调用超时。");
        if (error != null) throw error;
    }

    private void PumpThreadMain()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            IntPtr hInstance = NativeMethods.GetModuleHandle(null);
            _blackBrush = NativeMethods.CreateSolidBrush(0x00000000);
            _frameBrush = NativeMethods.CreateSolidBrush(0x00FFFFFF);

            RegisterWindowClass(hInstance, _maskClassName, _blackBrush, out _maskAtom);
            RegisterWindowClass(hInstance, _frameClassName, _frameBrush, out _frameAtom);
        }
        catch
        {
            _readySignal.Set();
            return;
        }
        finally
        {
            _readySignal.Set();
        }

        while (NativeMethods.GetMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == InvokeMessage)
            {
                DrainInvokeQueue();
                continue;
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    private void RegisterWindowClass(IntPtr hInstance, string className, IntPtr backgroundBrush, out ushort atom)
    {
        NativeMethods.WNDCLASSEX wc = default;
        wc.cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>();
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
        wc.hInstance = hInstance;
        wc.hbrBackground = backgroundBrush;
        wc.lpszClassName = className;

        atom = NativeMethods.RegisterClassExW(ref wc);
        if (atom == 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException("RegisterClassExW 失败，错误码：" + err);
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

    private void CreateOverlayWindows()
    {
        CreateMaskWindows();
        CreateFrameWindows();
    }

    private void CreateMaskWindows()
    {
        AddMaskWindow(new Rectangle(_virtualScreen.Left, _virtualScreen.Top, _virtualScreen.Width, Math.Max(0, _region.Top - _virtualScreen.Top)));
        AddMaskWindow(new Rectangle(_virtualScreen.Left, _region.Bottom, _virtualScreen.Width, Math.Max(0, _virtualScreen.Bottom - _region.Bottom)));
        AddMaskWindow(new Rectangle(_virtualScreen.Left, _region.Top, Math.Max(0, _region.Left - _virtualScreen.Left), _region.Height));
        AddMaskWindow(new Rectangle(_region.Right, _region.Top, Math.Max(0, _virtualScreen.Right - _region.Right), _region.Height));
    }

    private void CreateFrameWindows()
    {
        int t = BorderThicknessPx;
        int inset = FrameInsetPx;
        int left = _region.Left - inset;
        int top = _region.Top - inset;
        int width = _region.Width + inset * 2;
        int height = _region.Height + inset * 2;
        if (width <= 0 || height <= 0) return;

        AddDashedFrameWindow(new Rectangle(left, top, width, height), t);
    }

    private void AddMaskWindow(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        uint exStyle = (uint)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);
        IntPtr hwnd = CreateOverlayWindow(_maskClassName, exStyle, rect);
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, MaskAlpha, NativeMethods.LWA_ALPHA);
        _windows.Add(hwnd);
    }

    private void AddDashedFrameWindow(Rectangle outerRect, int thickness)
    {
        if (outerRect.Width <= 0 || outerRect.Height <= 0 || thickness <= 0) return;
        uint exStyle = (uint)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        IntPtr hwnd = CreateOverlayWindow(_frameClassName, exStyle, outerRect);
        if (hwnd == IntPtr.Zero) return;

        IntPtr frameRegion = CreateDashedFrameRegion(outerRect.Width, outerRect.Height, thickness);
        if (frameRegion != IntPtr.Zero)
        {
            NativeMethods.SetWindowRgn(hwnd, frameRegion, true);
            // SetWindowRgn 成功后系统接管 HRGN 生命周期，不再 DeleteObject。
        }
        _windows.Add(hwnd);
    }

    private static IntPtr CreateDashedFrameRegion(int width, int height, int thickness)
    {
        IntPtr frameRegion = IntPtr.Zero;
        try
        {
            frameRegion = NativeMethods.CreateRectRgn(0, 0, 0, 0);
            int step = BorderDashPx + BorderGapPx;

            for (int x = 0; x < width; x += step)
            {
                int dashW = Math.Min(BorderDashPx, width - x);
                AddRegionRect(frameRegion, x, 0, x + dashW, thickness);
                AddRegionRect(frameRegion, x, Math.Max(0, height - thickness), x + dashW, height);
            }

            for (int y = 0; y < height; y += step)
            {
                int dashH = Math.Min(BorderDashPx, height - y);
                AddRegionRect(frameRegion, 0, y, thickness, y + dashH);
                AddRegionRect(frameRegion, Math.Max(0, width - thickness), y, width, y + dashH);
            }

            return frameRegion;
        }
        catch
        {
            if (frameRegion != IntPtr.Zero) NativeMethods.DeleteObject(frameRegion);
            return IntPtr.Zero;
        }
    }

    private static void AddRegionRect(IntPtr targetRegion, int left, int top, int right, int bottom)
    {
        if (targetRegion == IntPtr.Zero || right <= left || bottom <= top) return;
        IntPtr rectRegion = NativeMethods.CreateRectRgn(left, top, right, bottom);
        if (rectRegion == IntPtr.Zero) return;
        try
        {
            NativeMethods.CombineRgn(targetRegion, targetRegion, rectRegion, NativeMethods.RGN_OR);
        }
        finally
        {
            NativeMethods.DeleteObject(rectRegion);
        }
    }

    private IntPtr CreateOverlayWindow(string className, uint exStyle, Rectangle rect)
    {
        IntPtr hwnd = NativeMethods.CreateWindowExW(
            exStyle,
            className,
            null,
            NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        WindowHelper.DisableWindowChromeEffects(hwnd);
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        return hwnd;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            return NativeMethods.HTTRANSPARENT;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_readySignal.IsSet)
            {
                InvokeOnOverlayThread(() =>
                {
                    for (int i = _windows.Count - 1; i >= 0; i--)
                    {
                        try { NativeMethods.DestroyWindow(_windows[i]); } catch { }
                    }
                    _windows.Clear();

                    IntPtr hInstance = NativeMethods.GetModuleHandle(null);
                    try { if (_maskAtom != 0) NativeMethods.UnregisterClassW(_maskClassName, hInstance); } catch { }
                    try { if (_frameAtom != 0) NativeMethods.UnregisterClassW(_frameClassName, hInstance); } catch { }
                    try { if (_blackBrush != IntPtr.Zero) NativeMethods.DeleteObject(_blackBrush); } catch { }
                    try { if (_frameBrush != IntPtr.Zero) NativeMethods.DeleteObject(_frameBrush); } catch { }
                    _blackBrush = IntPtr.Zero;
                    _frameBrush = IntPtr.Zero;
                });
            }
        }
        catch
        {
        }

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
            if (_thread.IsAlive) _thread.Join(1000);
        }
        catch { }

        _readySignal.Dispose();
        _shownSignal.Dispose();
    }
}
