using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>
/// 长截图原生轻量 Overlay：4 块遮罩 + 4 条边框 + 8 个手柄。
/// 不使用 Avalonia 透明顶层窗口，截图区域内部没有 HWND 覆盖。
/// </summary>
public sealed class NativeLongScrollOverlay : IDisposable
{
    private const uint InvokeMessage = WindowsMessages.WM_USER + 0x51;
    private const uint ShiftSelectionMessage = WindowsMessages.WM_USER + 0x52;
    private const int BorderThicknessPx = 2;
    private const int BorderDashPx = 4;
    private const int BorderGapPx = 2;
    private const int FrameInsetPx = 3;
    private const int MinSelectionHeightPx = 32;
    private const int MinSelectionWidthPx = 32;
    private const byte MaskAlpha = 86;

    private Rectangle _region;
    private readonly Rectangle _captureMonitor;
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
    private int _disposeStarted;

    // --- 手动偏移修正：选区整体移动 ---
    // 5 个子窗口的 HWND 和原始矩形，用于偏移时重新定位
    private IntPtr _topMaskHwnd;
    private IntPtr _bottomMaskHwnd;
    private IntPtr _leftMaskHwnd;
    private IntPtr _rightMaskHwnd;
    private IntPtr _frameHwnd;
    private Rectangle _topMaskRect;
    private Rectangle _bottomMaskRect;
    private Rectangle _leftMaskRect;
    private Rectangle _rightMaskRect;
    private Rectangle _frameRect;
    // UI 线程写、Overlay 线程读；通过 Volatile/Interlocked 保证最新总偏移可见。
    private int _selectionShiftAlong;
    private int _axisValue;
    // 消息合并标志：0=无待处理消息，1=已有 ShiftSelectionMessage。
    private int _shiftPending;
    private int _appliedFrameRegionWidth;
    private int _appliedFrameRegionHeight;
    private bool _appliedFrameRegionDashed;
    // 滚动方向：1=向下滚（活跃边缘=底部），-1=向上滚（活跃边缘=顶部）
    private volatile int _scrollDirection = 1;

    /// <summary>设置当前滚动方向，决定拖拽时移动哪条边。</summary>
    public void SetScrollDirection(int dir)
    {
        _scrollDirection = dir < 0 ? -1 : 1;
    }

    public NativeLongScrollOverlay(Rectangle region)
        : this(region, ScrollAxis.Vertical)
    {
    }

    public NativeLongScrollOverlay(Rectangle region, ScrollAxis axis)
    {
        _region = region.Width > 0 && region.Height > 0 ? region : new Rectangle(100, 100, 320, 240);
        _captureMonitor = FindCaptureMonitor(_region);
        _axisValue = (int)axis;
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
        if (IsUnavailable) return;
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
        if (IsUnavailable) return;
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

    private bool IsUnavailable => _disposed || Volatile.Read(ref _disposeStarted) != 0;

    private void InvokeOnOverlayThread(Action action, bool wait = true, int timeoutMs = 3000, bool allowDuringDispose = false)
    {
        if (action == null) return;
        if (_disposed || (!allowDuringDispose && Volatile.Read(ref _disposeStarted) != 0)) return;

        if (NativeMethods.GetCurrentThreadId() == _threadId)
        {
            action();
            return;
        }

        if (!wait)
        {
            lock (_invokeLock)
            {
                _invokeQueue.Enqueue(() =>
                {
                    try { action(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("NativeLongScrollOverlay async invocation failed: " + ex.Message); }
                });
            }
            NativeMethods.PostThreadMessage(_threadId, InvokeMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_invokeLock)
        {
            _invokeQueue.Enqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(ex);
                }
            });
        }

        if (!NativeMethods.PostThreadMessage(_threadId, InvokeMessage, IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("NativeLongScrollOverlay 无法投递线程消息。");
        }
        if (!completion.Task.Wait(timeoutMs)) throw new TimeoutException("NativeLongScrollOverlay 调用超时。");
        Exception? error = completion.Task.GetAwaiter().GetResult();
        if (error != null) throw error;
    }

    private void PumpThreadMain()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            IntPtr hInstance = NativeMethods.GetModuleHandle(null);
            _blackBrush = NativeMethods.CreateSolidBrush(0x00000000);
            // 高亮品牌蓝 #3388FF（对齐主工具栏 AutoHover 高亮色），COLORREF = 0x00BBGGRR。
            _frameBrush = NativeMethods.CreateSolidBrush(0x00FF8833);

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

            if (msg.message == ShiftSelectionMessage)
            {
                Interlocked.Exchange(ref _shiftPending, 0);
                try { ApplySelectionShift(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("NativeLongScrollOverlay.ApplySelectionShift 异常: " + ex.Message); }
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
        _topMaskRect = new Rectangle(_captureMonitor.Left, _captureMonitor.Top, _captureMonitor.Width, Math.Max(0, _region.Top - _captureMonitor.Top));
        _bottomMaskRect = new Rectangle(_captureMonitor.Left, _region.Bottom, _captureMonitor.Width, Math.Max(0, _captureMonitor.Bottom - _region.Bottom));
        _leftMaskRect = new Rectangle(_captureMonitor.Left, _region.Top, Math.Max(0, _region.Left - _captureMonitor.Left), _region.Height);
        _rightMaskRect = new Rectangle(_region.Right, _region.Top, Math.Max(0, _captureMonitor.Right - _region.Right), _region.Height);

        _topMaskHwnd = CreateMaskWindow(_topMaskRect);
        _bottomMaskHwnd = CreateMaskWindow(_bottomMaskRect);
        _leftMaskHwnd = CreateMaskWindow(_leftMaskRect);
        _rightMaskHwnd = CreateMaskWindow(_rightMaskRect);
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

        _frameRect = new Rectangle(left, top, width, height);
        _frameHwnd = CreateFrameWindow(_frameRect, t);
        if (_frameHwnd != IntPtr.Zero)
        {
            _appliedFrameRegionWidth = width;
            _appliedFrameRegionHeight = height;
            _appliedFrameRegionDashed = true;
        }
    }

    private IntPtr CreateMaskWindow(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return IntPtr.Zero;
        uint exStyle = (uint)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);
        IntPtr hwnd = CreateOverlayWindow(_maskClassName, exStyle, rect);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, MaskAlpha, NativeMethods.LWA_ALPHA);
        _windows.Add(hwnd);
        return hwnd;
    }

    private IntPtr CreateFrameWindow(Rectangle outerRect, int thickness)
    {
        if (outerRect.Width <= 0 || outerRect.Height <= 0 || thickness <= 0) return IntPtr.Zero;
        uint exStyle = (uint)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        IntPtr hwnd = CreateOverlayWindow(_frameClassName, exStyle, outerRect);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr frameRegion = CreateDashedFrameRegion(outerRect.Width, outerRect.Height, thickness);
        if (frameRegion != IntPtr.Zero)
        {
            NativeMethods.SetWindowRgn(hwnd, frameRegion, true);
            // SetWindowRgn 成功后系统接管 HRGN 生命周期，不再 DeleteObject。
        }
        _windows.Add(hwnd);
        return hwnd;
    }

    /// <summary>
    /// 手动偏移修正：按总偏移移动当前滚动方向对应的活动边缘。
    /// 返回钳制后的实际偏移，确保释放时的逻辑裁剪与视觉位置完全一致。
    /// </summary>
    public int ShiftSelectionY(int totalOffset) => ShiftSelection(ScrollAxis.Vertical, totalOffset);

    public int ShiftSelection(ScrollAxis axis, int totalOffset)
    {
        if (IsUnavailable || _threadId == 0) return 0;

        Volatile.Write(ref _axisValue, (int)axis);
        int appliedOffset = ClampSelectionShift(axis, totalOffset);
        Volatile.Write(ref _selectionShiftAlong, appliedOffset);

        // 消息合并：队列中最多保留一条刷新消息，Overlay 线程总是读取最新总偏移。
        if (Interlocked.Exchange(ref _shiftPending, 1) == 0)
        {
            if (!NativeMethods.PostThreadMessage(_threadId, ShiftSelectionMessage, IntPtr.Zero, IntPtr.Zero))
            {
                Interlocked.Exchange(ref _shiftPending, 0);
            }
        }

        return appliedOffset;
    }

    public int GetSelectionShiftY() => GetSelectionShift(ScrollAxis.Vertical);

    public int GetSelectionShift(ScrollAxis axis)
        => (ScrollAxis)Volatile.Read(ref _axisValue) == axis ? Volatile.Read(ref _selectionShiftAlong) : 0;

    /// <summary>选区归位到原始位置。</summary>
    public void ResetSelectionY()
    {
        ResetSelection(ScrollAxis.Vertical);
    }

    public void ResetSelection(ScrollAxis axis) => ShiftSelection(axis, 0);

    /// <summary>
    /// 提交视口调整：永久更新选区边界。
    /// 正 delta = 裁剪（选区缩小），负 delta = 延伸（选区扩大）。
    /// topDelta > 0: 顶边下移（裁顶部）；topDelta < 0: 顶边上移（延伸顶部）
    /// bottomDelta > 0: 底边上移（裁底部）；bottomDelta < 0: 底边下移（延伸底部）
    /// 调用后 _selectionShiftY 归零，后续拖拽从新边界开始。
    /// </summary>
    public void CommitCrop(int topDelta, int bottomDelta)
        => CommitCrop(ScrollAxis.Vertical, topDelta, bottomDelta);

    public void CommitCrop(ScrollAxis axis, int topDelta, int bottomDelta)
    {
        if (IsUnavailable || _threadId == 0) return;

        if (topDelta == 0 && bottomDelta == 0)
        {
            ResetSelection(axis);
            return;
        }

        Volatile.Write(ref _axisValue, (int)axis);
        if (axis == ScrollAxis.Horizontal)
        {
            CommitHorizontalCrop(topDelta, bottomDelta);
            return;
        }

        int total = topDelta + bottomDelta;

        // 永久更新选区（total < 0 = 延伸，Height 增加）
        _region = new Rectangle(
            _region.X, _region.Y + topDelta,
            _region.Width, _region.Height - total);

        _frameRect = new Rectangle(
            _frameRect.Left, _frameRect.Top + topDelta,
            _frameRect.Width, _frameRect.Height - total);

        // 上遮罩：topDelta > 0 增长（裁顶部），topDelta < 0 缩减（延伸顶部）
        _topMaskRect = new Rectangle(
            _topMaskRect.Left, _topMaskRect.Top,
            _topMaskRect.Width, _topMaskRect.Height + topDelta);

        // 下遮罩：bottomDelta > 0 增长（裁底部），bottomDelta < 0 缩减（延伸底部）
        _bottomMaskRect = new Rectangle(
            _bottomMaskRect.Left, _bottomMaskRect.Top - bottomDelta,
            _bottomMaskRect.Width, _bottomMaskRect.Height + bottomDelta);

        _leftMaskRect = new Rectangle(
            _leftMaskRect.Left, _leftMaskRect.Top + topDelta,
            _leftMaskRect.Width, _leftMaskRect.Height - total);
        _rightMaskRect = new Rectangle(
            _rightMaskRect.Left, _rightMaskRect.Top + topDelta,
            _rightMaskRect.Width, _rightMaskRect.Height - total);

        // 偏移归零，用新基础矩形和最终虚线 Region 重绘。
        Volatile.Write(ref _selectionShiftAlong, 0);
        if (Interlocked.Exchange(ref _shiftPending, 1) == 0)
        {
            if (!NativeMethods.PostThreadMessage(_threadId, ShiftSelectionMessage, IntPtr.Zero, IntPtr.Zero))
            {
                Interlocked.Exchange(ref _shiftPending, 0);
            }
        }
    }

    private void CommitHorizontalCrop(int leftDelta, int rightDelta)
    {
        int total = leftDelta + rightDelta;
        _region = new Rectangle(_region.X + leftDelta, _region.Y, _region.Width - total, _region.Height);
        _frameRect = new Rectangle(_frameRect.Left + leftDelta, _frameRect.Top, _frameRect.Width - total, _frameRect.Height);
        _leftMaskRect = new Rectangle(_leftMaskRect.Left, _leftMaskRect.Top, _leftMaskRect.Width + leftDelta, _leftMaskRect.Height);
        _rightMaskRect = new Rectangle(_rightMaskRect.Left - rightDelta, _rightMaskRect.Top, _rightMaskRect.Width + rightDelta, _rightMaskRect.Height);
        _topMaskRect = new Rectangle(_topMaskRect.Left + leftDelta, _topMaskRect.Top, _topMaskRect.Width - total, _topMaskRect.Height);
        _bottomMaskRect = new Rectangle(_bottomMaskRect.Left + leftDelta, _bottomMaskRect.Top, _bottomMaskRect.Width - total, _bottomMaskRect.Height);
        Volatile.Write(ref _selectionShiftAlong, 0);
        PostSelectionRefresh();
    }

    private void PostSelectionRefresh()
    {
        if (Interlocked.Exchange(ref _shiftPending, 1) != 0) return;
        if (!NativeMethods.PostThreadMessage(_threadId, ShiftSelectionMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Interlocked.Exchange(ref _shiftPending, 0);
        }
    }

    private int ClampSelectionShift(ScrollAxis axis, int offset)
    {
        if (axis == ScrollAxis.Horizontal)
        {
            int minWidth = Math.Min(MinSelectionWidthPx, Math.Max(1, _region.Width));
            if (_scrollDirection < 0)
            {
                return Math.Clamp(offset, _captureMonitor.Left - _region.Left, _region.Width - minWidth);
            }

            return Math.Clamp(offset, minWidth - _region.Width, _captureMonitor.Right - _region.Right);
        }

        int minHeight = Math.Min(MinSelectionHeightPx, Math.Max(1, _region.Height));
        if (_scrollDirection < 0)
        {
            int minOffset = _captureMonitor.Top - _region.Top;
            int maxOffset = _region.Height - minHeight;
            return Math.Clamp(offset, minOffset, maxOffset);
        }

        int minBottomOffset = minHeight - _region.Height;
        int maxBottomOffset = _captureMonitor.Bottom - _region.Bottom;
        return Math.Clamp(offset, minBottomOffset, maxBottomOffset);
    }

    /// <summary>
    /// 在 Overlay 线程上执行：根据总偏移调整活动边缘。所有 HWND 使用 DeferWindowPos
    /// 原子提交，拖动期间使用轻量实线 Region，释放后恢复最终虚线 Region。
    /// </summary>
    private void ApplySelectionShift()
    {
        ScrollAxis axis = (ScrollAxis)Volatile.Read(ref _axisValue);
        int offset = ClampSelectionShift(axis, Volatile.Read(ref _selectionShiftAlong));
        Volatile.Write(ref _selectionShiftAlong, offset);

        Rectangle frame = _frameRect;
        Rectangle topMask = _topMaskRect;
        Rectangle bottomMask = _bottomMaskRect;
        Rectangle leftMask = _leftMaskRect;
        Rectangle rightMask = _rightMaskRect;

        if (axis == ScrollAxis.Horizontal && _scrollDirection < 0)
        {
            int newWidth = Math.Max(1, _frameRect.Width - offset);
            frame = new Rectangle(_frameRect.Left + offset, _frameRect.Top, newWidth, _frameRect.Height);
            leftMask = SafeWindowRect(new Rectangle(
                _leftMaskRect.Left, _leftMaskRect.Top,
                _leftMaskRect.Width + offset, _leftMaskRect.Height));
            topMask = SafeWindowRect(new Rectangle(
                _topMaskRect.Left + offset, _topMaskRect.Top,
                _topMaskRect.Width - offset, _topMaskRect.Height));
            bottomMask = SafeWindowRect(new Rectangle(
                _bottomMaskRect.Left + offset, _bottomMaskRect.Top,
                _bottomMaskRect.Width - offset, _bottomMaskRect.Height));
        }
        else if (axis == ScrollAxis.Horizontal)
        {
            int newWidth = Math.Max(1, _frameRect.Width + offset);
            frame = new Rectangle(_frameRect.Left, _frameRect.Top, newWidth, _frameRect.Height);
            rightMask = SafeWindowRect(new Rectangle(
                _rightMaskRect.Left + offset, _rightMaskRect.Top,
                _rightMaskRect.Width - offset, _rightMaskRect.Height));
            topMask = SafeWindowRect(new Rectangle(
                _topMaskRect.Left, _topMaskRect.Top,
                _topMaskRect.Width + offset, _topMaskRect.Height));
            bottomMask = SafeWindowRect(new Rectangle(
                _bottomMaskRect.Left, _bottomMaskRect.Top,
                _bottomMaskRect.Width + offset, _bottomMaskRect.Height));
        }
        else if (_scrollDirection < 0)
        {
            int newHeight = Math.Max(1, _frameRect.Height - offset);
            frame = new Rectangle(_frameRect.Left, _frameRect.Top + offset, _frameRect.Width, newHeight);
            topMask = SafeWindowRect(new Rectangle(
                _topMaskRect.Left, _topMaskRect.Top,
                _topMaskRect.Width, _topMaskRect.Height + offset));
            leftMask = SafeWindowRect(new Rectangle(
                _leftMaskRect.Left, _leftMaskRect.Top + offset,
                _leftMaskRect.Width, _leftMaskRect.Height - offset));
            rightMask = SafeWindowRect(new Rectangle(
                _rightMaskRect.Left, _rightMaskRect.Top + offset,
                _rightMaskRect.Width, _rightMaskRect.Height - offset));
        }
        else
        {
            int newHeight = Math.Max(1, _frameRect.Height + offset);
            frame = new Rectangle(_frameRect.Left, _frameRect.Top, _frameRect.Width, newHeight);
        }

        // 拖动期间遮罩保持原样不动，只有选区边框随裁剪移动；
        // 避免灰色遮罩随大范围平移而显得像渲染 bug。释放后由 CommitCrop 真正更新遮罩。
        topMask = _topMaskRect;
        bottomMask = _bottomMaskRect;
        leftMask = _leftMaskRect;
        rightMask = _rightMaskRect;

        frame = SafeWindowRect(frame);
        topMask = SafeWindowRect(topMask);
        bottomMask = SafeWindowRect(bottomMask);
        leftMask = SafeWindowRect(leftMask);
        rightMask = SafeWindowRect(rightMask);

        MoveOverlayWindowsAtomically(frame, topMask, bottomMask, leftMask, rightMask);
        ApplyFrameRegion(frame.Width, frame.Height, dashed: offset == 0);
    }

    private static Rectangle SafeWindowRect(Rectangle rect)
    {
        return new Rectangle(rect.Left, rect.Top, Math.Max(1, rect.Width), Math.Max(1, rect.Height));
    }

    private void MoveOverlayWindowsAtomically(
        Rectangle frame,
        Rectangle topMask,
        Rectangle bottomMask,
        Rectangle leftMask,
        Rectangle rightMask)
    {
        uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        (IntPtr Hwnd, Rectangle Rect)[] targets =
        [
            (_frameHwnd, frame),
            (_topMaskHwnd, topMask),
            (_bottomMaskHwnd, bottomMask),
            (_leftMaskHwnd, leftMask),
            (_rightMaskHwnd, rightMask),
        ];

        int count = 0;
        foreach ((IntPtr hwnd, _) in targets)
        {
            if (hwnd != IntPtr.Zero) count++;
        }

        bool deferredSucceeded = false;
        IntPtr deferred = count > 0 ? NativeMethods.BeginDeferWindowPos(count) : IntPtr.Zero;
        if (deferred != IntPtr.Zero)
        {
            bool failed = false;
            foreach ((IntPtr hwnd, Rectangle rect) in targets)
            {
                if (hwnd == IntPtr.Zero) continue;
                IntPtr next = NativeMethods.DeferWindowPos(
                    deferred, hwnd, IntPtr.Zero,
                    rect.Left, rect.Top, rect.Width, rect.Height, flags);
                if (next == IntPtr.Zero)
                {
                    failed = true;
                    break;
                }
                deferred = next;
            }

            bool ended = NativeMethods.EndDeferWindowPos(deferred);
            deferredSucceeded = !failed && ended;
        }

        if (deferredSucceeded) return;

        foreach ((IntPtr hwnd, Rectangle rect) in targets)
        {
            if (hwnd == IntPtr.Zero) continue;
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero,
                rect.Left, rect.Top, rect.Width, rect.Height, flags);
        }
    }

    private void ApplyFrameRegion(int width, int height, bool dashed)
    {
        if (_frameHwnd == IntPtr.Zero || width <= 0 || height <= 0) return;
        if (_appliedFrameRegionWidth == width
            && _appliedFrameRegionHeight == height
            && _appliedFrameRegionDashed == dashed)
        {
            return;
        }

        IntPtr region = dashed
            ? CreateDashedFrameRegion(width, height, BorderThicknessPx)
            : CreateSolidFrameRegion(width, height, BorderThicknessPx);
        if (region == IntPtr.Zero) return;

        int applied = NativeMethods.SetWindowRgn(_frameHwnd, region, true);
        if (applied == 0)
        {
            NativeMethods.DeleteObject(region);
            return;
        }

        // SetWindowRgn 成功后系统接管 HRGN 生命周期。
        _appliedFrameRegionWidth = width;
        _appliedFrameRegionHeight = height;
        _appliedFrameRegionDashed = dashed;
    }

    private static IntPtr CreateSolidFrameRegion(int width, int height, int thickness)
    {
        if (width <= 0 || height <= 0 || thickness <= 0) return IntPtr.Zero;

        IntPtr outer = NativeMethods.CreateRectRgn(0, 0, width, height);
        if (outer == IntPtr.Zero) return IntPtr.Zero;

        int innerLeft = Math.Min(thickness, width);
        int innerTop = Math.Min(thickness, height);
        int innerRight = Math.Max(innerLeft, width - thickness);
        int innerBottom = Math.Max(innerTop, height - thickness);
        if (innerRight <= innerLeft || innerBottom <= innerTop)
        {
            return outer;
        }

        IntPtr inner = NativeMethods.CreateRectRgn(innerLeft, innerTop, innerRight, innerBottom);
        if (inner == IntPtr.Zero) return outer;
        try
        {
            NativeMethods.CombineRgn(outer, outer, inner, NativeMethods.RGN_DIFF);
            return outer;
        }
        finally
        {
            NativeMethods.DeleteObject(inner);
        }
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
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

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
                }, allowDuringDispose: true);
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

        _disposed = true;
        _readySignal.Dispose();
        _shownSignal.Dispose();
    }

    private static Rectangle FindCaptureMonitor(Rectangle region)
    {
        try
        {
            IReadOnlyList<Rectangle> monitors = MonitorHelper.GetMonitorRects();
            int centerX = region.Left + region.Width / 2;
            int centerY = region.Top + region.Height / 2;
            foreach (Rectangle monitor in monitors)
            {
                if (monitor.Contains(centerX, centerY)) return monitor;
            }
            foreach (Rectangle monitor in monitors)
            {
                if (monitor.IntersectsWith(region)) return monitor;
            }
        }
        catch { }

        return MonitorHelper.GetVirtualScreenRect();
    }
}
