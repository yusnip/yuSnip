using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.LongScroll.Stitching;
using ScreenCaptureTool.Platform;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>长截图实时预览窗口：固定宽度、底部对齐、只渲染屏幕可见区，正常向下滚动时增量追加。</summary>
public partial class LongScrollPreviewWindow : Window
{
    private static bool EnablePreviewDebugLog => AppLogger.DetailedLoggingEnabled;
    private const int MinPreviewWindowWidthPx = 60;
    // 尺寸参数对齐 Quicker：直接采用物理像素，不随 DPI 放大。
    private const int GapPx = LongScrollPreviewPlacer.Margin;
    private const int MaxVerticalPreviewWidthPx = LongScrollPreviewPlacer.MaxVerticalWidthPx;
    private const int MaxVerticalPreviewHeightPx = LongScrollPreviewPlacer.MaxVerticalHeightPx;
    private const int MaxHorizontalPreviewWidthPx = LongScrollPreviewPlacer.MaxHorizontalWidthPx;
    private const int MaxHorizontalPreviewHeightPx = LongScrollPreviewPlacer.MaxHorizontalHeightPx;
    private const int BlueBorderDip = 2;
    private const int IncrementalRedrawPaddingSourcePx = 120;
    // 宽度收缩/生长时按该物理像素步长离散步进窗口尺寸，避免每像素 SetWindowPos
    // resize 透明分层窗口造成的 DWM 抖动（宽度以固定档位跳变，档内内容与窗口同步稳定）。
    private const int VerticalResizeStepPx = 4;
    // 纵向预览“生长式”宽度策略（占选区宽度比例）：初始较宽，随内容变长收窄到底线。
    private const double VerticalWidthStartRatio = 0.40;
    private const double VerticalWidthFloorRatio = 0.20;

    private readonly DrawingRectangle _captureRegion;
    private readonly DrawingRectangle _virtualScreen;
    private readonly object _dataLock = new();
    private readonly object _renderWorkerLock = new();
    private readonly ManualResetEventSlim _renderIdle = new(true);

    private ScrollAxis _axis = ScrollAxis.Vertical;
    private List<StitchedStrip>? _pendingPreviewStrips;
    private DrawingBitmap? _pendingFixedTopBitmap;
    private DrawingBitmap? _pendingFixedBottomBitmap;
    private int _pendingTotalHeight;

    // S2 包装复用缓存：_lastSnapshotSources 保存会话侧源条带对象，
    // _lastSnapshotWrappers 保存对应的预览包装（与 _pendingPreviewStrips 同源）。
    private List<StitchedStrip>? _lastSnapshotSources;
    private List<StitchedStrip>? _lastSnapshotWrappers;
    private ScrollAxis _wrapperCacheAxis = ScrollAxis.Vertical;

    private int _currentViewportY;
    private int _currentViewportHeight;
    private int _fixedTopHeight;
    private int _fixedBottomHeight;
    private int _lastViewportYForDirection;
    private int _scrollDirection = 1;
    private bool _hasViewportInfo;
    private bool _renderWorkerRunning;
    private volatile bool _isClosing;

    // 悬停交互：鼠标停留在预览窗时才显示视口高亮与遮罩，滚轮则注入目标窗口滚动。
    private const bool _isHovering = false;
    private bool _lastShowTopTail;
    private bool _lastShowBottomTail;

    private int _frameLimitWidthPx = 300;
    private int _frameLimitHeightPx = 400;
    private bool _dockRight = true;
    private DrawingRectangle _placementRegion;
    private DrawingRectangle? _avoidRect;
    private int _placementDirection;

    // GDI cache：只在 RenderWorker 线程访问。UI 线程只接收裁剪后的像素帧。
    private DrawingBitmap? _previewCacheBitmap;
    private Graphics? _previewCacheGraphics;
    private int _previewCacheWidthPx;
    private int _previewCacheHeightPx;

    // S3 像素签名（CitePix hover_preview 同款思路）：影响预览像素的全部输入
    // 汇总为一份签名，签名不变则跳过 GDI 合成 / LockBits 拷出 / WriteableBitmap
    // 写回 / GPU 上传，仅以 OverlayOnly 帧更新视口高亮框等叠加层；连叠加层都
    // 未变化时整帧丢弃。只在 RenderWorker 线程读写。
    private RenderSignature _lastPixelSignature;
    private bool _hasPixelSignature;
    private int _lastPresentedBoxX = int.MinValue;
    private int _lastPresentedBoxY = int.MinValue;
    private int _lastPresentedBoxW = int.MinValue;
    private int _lastPresentedBoxH = int.MinValue;
    private int _lastPresentedWindowX = int.MinValue;
    private int _lastPresentedWindowY = int.MinValue;
    private int _lastPresentedWindowW = int.MinValue;
    private int _lastPresentedWindowH = int.MinValue;
    private int _overlayFrameLogCount;
    private int _droppedFrameLogCount;

    // S4 显示器矩形 / DPI 缓存：渲染线程每帧都要确定宿主位置，旧实现每帧
    // EnumDisplayMonitors + GetDpiForWindow 两次系统调用，60fps 下纯属浪费。
    private long _monitorCacheTimestamp;
    private DrawingRectangle _monitorCacheInput;
    private DrawingRectangle _monitorCacheResult;
    private bool _hasMonitorCache;
    private readonly object _monitorCacheLock = new();
    private long _dpiCacheTimestamp;
    private double _dpiCacheValue = 1.0;
    private const long MonitorCacheLifetimeMs = 500;
    private const long DpiCacheLifetimeMs = 1000;


    // P4：选区拖动与预览重叠时临时隐藏预览窗。
    private volatile bool _suppressedForSelectionDrag;

    private readonly object _uiFrameLock = new();
    private PreviewRenderFrame? _latestFrameForUi;
    private int _uiPresentScheduled;

    private WriteableBitmap? _writeableBitmap;
    private int _writeableBitmapWidth;
    private int _writeableBitmapHeight;
    private int _lastHostX;
    private int _lastHostY;
    private int _lastHostW;
    private int _lastHostH;
    private bool _hasLastHostRect;
    // 首次实际求解后记住候选侧；UI 初始定位和后台生长共用，不能按滚动方向重新选边。
    private readonly object _placementLock = new();
    private bool _dockLocked;
    private bool _lockedDockRight;
    private bool _lockedPreferBottom;
    private ScrollAxis _lockedPlacementAxis;
    private DrawingRectangle _lockedPlacementRegion, _lockedPlacementBounds;
    // 内容代数：每次把新内容推进窗口就递增，并入渲染签名，
    // 确保条带复用（内部像素更新而尺寸不变）时不因"签名没变"被跳帧冻结。
    private int _contentVersion;
    private long _pendingRequestedAtTick;
    private long _previewRequests, _renderAttempts, _pixelFrames, _presentedFrames;
    private long _renderTicks, _maxRenderTicks, _deliveryTicks, _maxDeliveryTicks;
    private long _renderFailures, _renderRetries;
    private double _lastWindowWidthDip = -1;
    private double _lastWindowHeightDip = -1;
    private double _lastCanvasWidthDip = -1;
    private double _lastCanvasHeightDip = -1;
    private double _lastImageWidthDip = -1;
    private double _lastImageHeightDip = -1;
    private double _lastViewportX = -1;
    private double _lastViewportY = -1;
    private double _lastViewportW = -1;
    private double _lastViewportH = -1;
    private double _smoothedViewportX;
    private double _smoothedViewportY;
    private double _smoothedViewportW;
    private double _smoothedViewportH;

    private int _logSeq;
    private int _updateFrameLogCount;
    private int _updateStripsLogCount;
    private int _renderLogCount;
    private int _hRenderLogCount;
    private int _presentLogCount;

    public LongScrollPreviewWindow()
        : this(DrawingRectangle.Empty)
    {
    }

    public LongScrollPreviewWindow(DrawingRectangle captureRegion)
        : this(captureRegion, ScrollAxis.Vertical)
    {
    }

    public LongScrollPreviewWindow(DrawingRectangle captureRegion, ScrollAxis axis)
    {
        _captureRegion = captureRegion.Width > 0 && captureRegion.Height > 0 ? captureRegion : new DrawingRectangle(100, 100, 320, 480);
        _placementRegion = _captureRegion;
        _virtualScreen = MonitorHelper.GetVirtualScreenRect();
        _axis = axis;
        InitializeComponent();
        PreviewLog("ctor capture=" + RectText(_captureRegion) + ", virtual=" + RectText(_virtualScreen));

        // 纯取景器：不接收任何鼠标/滚动交互，视口高亮与遮罩保持隐藏，鼠标直接穿透到底层窗口。
        Dispatcher.UIThread.Post(ApplyHoverVisibility, DispatcherPriority.Normal);

        Opened += (_, _) =>
        {
            try
            {
                PositionNearCaptureRegion();
                IntPtr hwnd = GetWindowHwnd();
                // 命中测试穿透 + 透明，让预览窗不拦截鼠标与滚轮，用户可正常操作下方目标窗口。
                WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: true);
                WindowHelper.MakeHitTestTransparent(hwnd);
                WindowHelper.DisableWindowChromeEffects(hwnd);
                // 启用持久置顶，防止预览窗被其他前台窗口挤到后面
                WindowHelper.EnforceTopmost(hwnd);
            }
            catch { }
        };

        Closed += (_, _) =>
        {
            // 先停止持久置顶，还原 WndProc
            try { WindowHelper.StopEnforceTopmost(GetWindowHwnd()); } catch { }
            ShutdownRendering();
        };
    }

    private void ApplyHoverVisibility()
    {
        // 纯取景器：_isHovering 恒为 false，视口高亮、遮罩与方向箭头保持隐藏。
        bool h = _isHovering;
        CurrentViewportBorder.IsVisible = h;
        PreviewMaskTop.IsVisible = h && PreviewMaskTop.Width > 0.5 && PreviewMaskTop.Height > 0.5;
        PreviewMaskBottom.IsVisible = h && PreviewMaskBottom.Width > 0.5 && PreviewMaskBottom.Height > 0.5;
        PreviewMaskLeft.IsVisible = h && PreviewMaskLeft.Width > 0.5 && PreviewMaskLeft.Height > 0.5;
        PreviewMaskRight.IsVisible = h && PreviewMaskRight.Width > 0.5 && PreviewMaskRight.Height > 0.5;
        TopTailShadow.Opacity = h && _lastShowTopTail ? 1.0 : 0.0;
        BottomTailShadow.Opacity = h && _lastShowBottomTail ? 1.0 : 0.0;
        TopTailArrow.Opacity = h && _lastShowTopTail ? 0.90 : 0.0;
        BottomTailArrow.Opacity = h && _lastShowBottomTail ? 0.90 : 0.0;
    }

    public void ConfigureAxis(ScrollAxis axis)
    {
        lock (_dataLock)
        {
            if (_axis == axis) return;
            _axis = axis;
            _hasViewportInfo = false;
            _lastHostX = 0;
            _lastHostY = 0;
            _lastHostW = 0;
            _lastHostH = 0;
            _hasLastHostRect = false;
            _lastSnapshotSources = null;
            _lastSnapshotWrappers = null;
        }
        _hasPixelSignature = false;

        Dispatcher.UIThread.Post(() =>
        {
            TopTailArrow.Text = axis == ScrollAxis.Horizontal ? "◀" : "▲";
            BottomTailArrow.Text = axis == ScrollAxis.Horizontal ? "▶" : "▼";
            PositionNearCaptureRegion();
        }, DispatcherPriority.Normal);

        QueueRenderWorker();
    }

    public void UpdateFrame(int frameWidthPx, int frameHeightPx)
    {
        int inputW = frameWidthPx;
        int inputH = frameHeightPx;
        lock (_dataLock)
        {
            if (frameWidthPx < MinPreviewWindowWidthPx) frameWidthPx = MinPreviewWindowWidthPx;
            if (frameHeightPx < 1) frameHeightPx = 1;
            _frameLimitWidthPx = frameWidthPx;
            _frameLimitHeightPx = frameHeightPx;
        }

        int n = Interlocked.Increment(ref _updateFrameLogCount);
        if (n <= 8 || n % 50 == 0)
        {
            PreviewLog("UpdateFrame input=" + inputW + "x" + inputH + ", clamped=" + frameWidthPx + "x" + frameHeightPx);
        }
    }

    public void UpdateViewport(int currentViewportY, int viewportHeight, int totalHeight, int fixedTopHeight = 0, int fixedBottomHeight = 0)
        => UpdateViewport(ScrollAxis.Vertical, currentViewportY, viewportHeight, totalHeight, fixedTopHeight, fixedBottomHeight);

    public void UpdateViewport(ScrollAxis axis, int currentViewportAlong, int viewportAlongLength, int totalAlongLength, int fixedLeadingLength = 0, int fixedTrailingLength = 0)
    {
        lock (_dataLock)
        {
            _axis = axis;
            int previousViewportY = _currentViewportY;
            bool hadViewportInfo = _hasViewportInfo;
            _currentViewportY = currentViewportAlong;
            _currentViewportHeight = viewportAlongLength;
            _fixedTopHeight = Math.Max(0, fixedLeadingLength);
            _fixedBottomHeight = Math.Max(0, fixedTrailingLength);
            if (_currentViewportHeight <= 0) _currentViewportHeight = axis == ScrollAxis.Horizontal ? _captureRegion.Width : _captureRegion.Height;
            if (totalAlongLength > 0 && _currentViewportY + _currentViewportHeight > totalAlongLength)
            {
                _currentViewportY = Math.Max(0, totalAlongLength - _currentViewportHeight);
            }
            if (hadViewportInfo)
            {
                if (_currentViewportY > previousViewportY) _scrollDirection = 1;
                else if (_currentViewportY < previousViewportY) _scrollDirection = -1;
            }
            _lastViewportYForDirection = _currentViewportY;
            _hasViewportInfo = true;
        }
    }

    public void SetAnchor(DrawingRectangle region, bool dockRight)
    {
        lock (_dataLock)
        {
            if (region.Width > 0 && region.Height > 0) _placementRegion = region;
            _dockRight = dockRight;
        }
    }

    /// <summary>
    /// Supplies the current physical capture region and an optional control-bar exclusion
    /// rectangle. Horizontal previews choose a non-overlapping host around these bounds.
    /// </summary>
    public void SetPlacementContext(DrawingRectangle region, DrawingRectangle? avoidRect, int captureDirection)
    {
        lock (_dataLock)
        {
            if (region.Width > 0 && region.Height > 0) _placementRegion = region;
            _avoidRect = avoidRect is { Width: > 0, Height: > 0 } ? avoidRect : null;
            Volatile.Write(ref _placementDirection, captureDirection < 0 ? -1 : captureDirection > 0 ? 1 : 0);
        }
    }

    public void UpdateStrips(IReadOnlyList<StitchedStrip> strips, int totalHeight, DrawingBitmap? fixedTopBitmap = null, DrawingBitmap? fixedBottomBitmap = null)
        => UpdateStrips(ScrollAxis.Vertical, strips, totalHeight, fixedTopBitmap, fixedBottomBitmap);

    public void UpdateStrips(ScrollAxis axis, IReadOnlyList<StitchedStrip> strips, int totalAlongLength, DrawingBitmap? fixedLeadingBitmap = null, DrawingBitmap? fixedTrailingBitmap = null)
    {
        if (_isClosing || strips == null || strips.Count == 0) return;

        // 预览只冻结条带坐标并持有非 owning 位图引用，不能在每次刷新时克隆
        // 所有像素。会话把被裁剪/替换的条带与固定位图延迟释放到预览线程停止
        // 之后，保证这些引用全程有效，因此本窗口对条带与固定层均为"借用"，
        // 不做任何 Dispose。
        //
        // S2 包装复用：向下追加时旧条带对象与坐标都不变，可直接复用上一帧包装，
        // 每次刷新只分配新增条带。向上追加会对全部旧条带执行 OffsetY；对象身份虽未变，
        // 包装里冻结的 AlongStart 已失效，因此复用条件必须同时核对完整几何。
        // 只按对象身份复用会让最终合成正确、预览却仍按上一帧坐标绘制。
        List<StitchedStrip>? previousSources;
        List<StitchedStrip>? previousWrappers;
        lock (_dataLock)
        {
            previousSources = _lastSnapshotSources;
            previousWrappers = _lastSnapshotWrappers;
            if (previousWrappers == null || previousSources == null || _wrapperCacheAxis != axis)
            {
                previousSources = null;
                previousWrappers = null;
            }
        }

        var snapshot = new List<StitchedStrip>(strips.Count);
        var sources = new List<StitchedStrip>(strips.Count);
        int reused = 0;
        bool failed = false;
        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip? strip = strips[i];
            if (strip == null || strip.Bitmap == null) continue;
            StitchedStrip? wrapper = null;
            if (previousSources != null
                && sources.Count < previousSources.Count
                && ReferenceEquals(strip, previousSources[sources.Count])
                && previousWrappers![sources.Count].AlongStart == strip.AlongStart
                && previousWrappers[sources.Count].AlongLength == strip.AlongLength
                && previousWrappers[sources.Count].CrossLength == strip.CrossLength
                && previousWrappers[sources.Count].OverlapLength == strip.OverlapLength)
            {
                wrapper = previousWrappers[sources.Count];
                reused++;
            }
            else
            {
                try { wrapper = strip.CreatePreviewReference(axis); }
                catch { failed = true; break; }
            }
            snapshot.Add(wrapper);
            sources.Add(strip);
        }

        if (failed || snapshot.Count == 0) return;

        lock (_dataLock)
        {
            if (_isClosing) return;
            _contentVersion++;
            _pendingRequestedAtTick = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _previewRequests);
            _axis = axis;
            _pendingPreviewStrips = snapshot;
            _pendingFixedTopBitmap = fixedLeadingBitmap;
            _pendingFixedBottomBitmap = fixedTrailingBitmap;
            _pendingTotalHeight = totalAlongLength;
            _lastSnapshotSources = sources;
            _lastSnapshotWrappers = snapshot;
            _wrapperCacheAxis = axis;
        }

        int n = Interlocked.Increment(ref _updateStripsLogCount);
        if (n <= 8 || n % 50 == 0)
        {
            StitchedStrip first = snapshot[0];
            StitchedStrip last = snapshot[^1];
            PreviewLog("UpdateStrips axis=" + axis + ", count=" + snapshot.Count + ", reused=" + reused + ", total=" + totalAlongLength + ", first=" + first.Width + "x" + first.Height + "@" + first.Y + ", last=" + last.Width + "x" + last.Height + "@" + last.Y);
        }

        QueueRenderWorker();
    }

    /// <summary>
    /// P4：选区拖动与预览窗重叠时临时隐藏预览窗（Quicker 同款交互），
    /// 结束拖动后恢复显示。调用方可从任意线程调用。
    /// </summary>
    public void SetSuppressedForSelectionDrag(bool suppressed)
    {
        if (_suppressedForSelectionDrag == suppressed) return;
        _suppressedForSelectionDrag = suppressed;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                IntPtr hwnd = GetWindowHwnd();
                if (hwnd == IntPtr.Zero) return;
                if (suppressed)
                {
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
                        | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
                }
                else
                {
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                        | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                }
            }
            catch { }
        }, DispatcherPriority.Render);
    }

    /// <summary>P4：当前预览窗宿主的物理屏幕矩形（供选区重叠检测）；尚未呈现过时返回空矩形。</summary>
    public DrawingRectangle GetHostScreenRect()
    {
        return _hasLastHostRect
            ? new DrawingRectangle(_lastHostX, _lastHostY, _lastHostW, _lastHostH)
            : DrawingRectangle.Empty;
    }

    public void ShutdownRendering()
    {
        if (_isClosing) return;
        _isClosing = true;
        // P4：关闭前确保不再处于拖动隐藏状态。
        _suppressedForSelectionDrag = false;
        lock (_dataLock)
        {
            // 条带与固定层均为非拥有借用（会话延迟释放），此处只清引用。
            _pendingPreviewStrips = null;
            _pendingFixedTopBitmap = null;
            _pendingFixedBottomBitmap = null;
            _lastSnapshotSources = null;
            _lastSnapshotWrappers = null;
        }
        _hasPixelSignature = false;
        lock (_uiFrameLock)
        {
            ReleasePreviewRenderFrame(_latestFrameForUi);
            _latestFrameForUi = null;
        }
        Interlocked.Exchange(ref _uiPresentScheduled, 0);

        bool renderStopped = false;
        try { renderStopped = _renderIdle.Wait(3000); } catch { }
        // 超时时由 worker 的 finally 释放缓存，避免关闭线程与渲染线程并发 Dispose。
        if (renderStopped) DisposePreviewCache();
        if (_renderAttempts > 0)
        {
            double ms = 1000.0 / Stopwatch.Frequency;
            AppLogger.Info($"LongScroll.PreviewPerf requests={_previewRequests} renders={_renderAttempts} pixels={_pixelFrames} presents={_presentedFrames} "
                + $"renderMeanMs={_renderTicks * ms / _renderAttempts:F2} renderMaxMs={_maxRenderTicks * ms:F2} "
                + $"renderFailures={_renderFailures} renderRetries={_renderRetries} "
                + $"deliveryMeanMs={_deliveryTicks * ms / Math.Max(1, _presentedFrames):F2} deliveryMaxMs={_maxDeliveryTicks * ms:F2}");
        }

        void ClearUi()
        {
            try { PreviewImage.Source = null; } catch { }
            try { _writeableBitmap?.Dispose(); } catch { }
            _writeableBitmap = null;
            _writeableBitmapWidth = 0;
            _writeableBitmapHeight = 0;
        }

        if (Dispatcher.UIThread.CheckAccess()) ClearUi();
        else Dispatcher.UIThread.Post(ClearUi, DispatcherPriority.Background);
    }

    private void QueueRenderWorker()
        => SchedulePreviewFrame();

    // 输入只覆盖最新快照。启动合成与写回都受显示帧约束，UI 忙时不在后台空转追帧。
    private void SchedulePreviewFrame()
    {
        if (_isClosing || Interlocked.Exchange(ref _uiPresentScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) { Interlocked.Exchange(ref _uiPresentScheduled, 0); return; }
            RequestAnimationFrame(_ => DrainLatestPreviewFrameOnUi());
        }, DispatcherPriority.Render);
    }

    private void StartRenderWorkerForFrame()
    {
        lock (_renderWorkerLock)
        {
            if (_isClosing || _renderWorkerRunning) return;
            lock (_uiFrameLock)
            {
                // worker 可能恰好在本显示帧摘帧之后完成。未写回的像素仍需先交付，
                // 不能立刻再生成一个无像素的叠加帧把它覆盖。
                if (_latestFrameForUi != null) { SchedulePreviewFrame(); return; }
            }
            lock (_dataLock)
                if (_pendingPreviewStrips == null) return;
            _renderWorkerRunning = true;
            _renderIdle.Reset();
        }

        Task.Run(RenderWorkerLoop);
    }

    private void RenderWorkerLoop()
    {
        try
        {
            if (!_isClosing)
            {
                RenderInput? input;
                lock (_dataLock)
                {
                    if (_pendingPreviewStrips == null || _pendingPreviewStrips.Count == 0)
                    {
                        input = null;
                    }
                    else
                    {
                        input = new RenderInput
                        {
                            Strips = _pendingPreviewStrips,
                            Axis = _axis,
                            FixedTopBitmap = _pendingFixedTopBitmap,
                            FixedBottomBitmap = _pendingFixedBottomBitmap,
                            TotalHeight = _pendingTotalHeight,
                            FrameLimitW = _frameLimitWidthPx,
                            FrameLimitH = _frameLimitHeightPx,
                            DockRight = _dockRight,
                            PlacementRegion = _placementRegion,
                            AvoidRect = _avoidRect,
                            PlacementDirection = _placementDirection,
                            CurrentViewportY = _currentViewportY,
                            CurrentViewportHeight = _currentViewportHeight > 0 ? _currentViewportHeight : _captureRegion.Height,
                            FixedTopHeight = _fixedTopHeight,
                            FixedBottomHeight = _fixedBottomHeight,
                            ScrollDirection = _scrollDirection,
                            HasViewportInfo = _hasViewportInfo,
                            ContentVersion = _contentVersion,
                            RequestedAtTick = _pendingRequestedAtTick,
                        };
                        _pendingPreviewStrips = null;
                        _pendingFixedTopBitmap = null;
                        _pendingFixedBottomBitmap = null;
                    }
                }

                if (input != null)
                {
                    // 条带包装与固定层位图均为非拥有借用（S2 复用 / 会话延迟释放），
                    // 渲染输入用完即弃，不做任何 Dispose。
                    long started = Stopwatch.GetTimestamp();
                    long failuresBefore = Interlocked.Read(ref _renderFailures);
                    PreviewRenderFrame? frame = RenderPreviewFrame(input);
                    if (frame == null && !_isClosing && Interlocked.Read(ref _renderFailures) != failuresBefore)
                    {
                        bool retry;
                        lock (_dataLock) retry = _pendingPreviewStrips == null;
                        // 没有更新输入时只补试一次，保证瞬时绘制失败不会永久吞掉停止滚动后的末帧。
                        if (retry)
                        {
                            Interlocked.Increment(ref _renderRetries);
                            frame = RenderPreviewFrame(input);
                        }
                    }
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    Interlocked.Increment(ref _renderAttempts);
                    Interlocked.Add(ref _renderTicks, elapsed);
                    _maxRenderTicks = Math.Max(_maxRenderTicks, elapsed);
                    if (frame?.Pixels != null) Interlocked.Increment(ref _pixelFrames);
                    if (frame != null && !_isClosing)
                    {
                        QueuePresentPreviewFrame(frame);
                    }
                    else
                    {
                        ReleasePreviewRenderFrame(frame);
                    }
                }

            }
        }
        catch
        {
        }
        finally
        {
            if (_isClosing) DisposePreviewCache();
            lock (_renderWorkerLock)
            {
                _renderWorkerRunning = false;
                _renderIdle.Set();
            }
            lock (_dataLock)
                if (_pendingPreviewStrips != null) SchedulePreviewFrame();
        }
    }

    /// <summary>
    /// 一帧预览的完整几何规划（横纵通用）。沿轴(along)=内容生长方向，
    /// 横轴(cross)=垂直方向。两个 Plan 函数负责全部横纵差异，
    /// 公共骨架 RenderFrameFromGeometry 负责签名/跳帧/合成/组帧。
    /// </summary>
    private sealed class FrameGeometry
    {
        public ScrollAxis Axis = ScrollAxis.Vertical;
        public int ImageW, ImageH;        // 含固定层的源图总尺寸
        public int CanvasW, CanvasH;      // 合成画布尺寸
        public int HostW, HostH;          // 宿主窗口尺寸
        public int WindowX, WindowY;      // 宿主屏幕位置
        public int ContentX, ContentY;    // 内容窗（含边框）在宿主内的偏移
        public int ContentW, ContentH;    // 内容窗尺寸（含边框）
        public int ScaledCrossStart;      // cross 方向绘制起点（纵向内容居中时>0）
        public int ScaledCrossLen;        // cross 方向绘制长度
        public int MinAlong;              // 条带沿轴基准（画布起点对应的源坐标）
        public int FixedLeading, FixedTrailing;
        public double Scale;
        public double SrcOffset;          // 沿轴源裁剪偏移（保留最新内容可见）
        public int BoxX, BoxY, BoxW, BoxH; // 视口高亮框
        public double DpiScale = 1.0;
    }

    private PreviewRenderFrame? RenderPreviewFrame(RenderInput input)
    {
        // 拖动选区与预览重叠期间暂停渲染，避免隐藏后的窗口被 Present 再次显示。
        if (_suppressedForSelectionDrag) return null;

        FrameGeometry? geo = input.Axis == ScrollAxis.Horizontal
            ? PlanHorizontalGeometry(input)
            : PlanVerticalGeometry(input);
        if (geo == null) return null;
        return RenderFrameFromGeometry(input, geo);
    }

    /// <summary>纵向预览：侧边停靠、fit 宽度、沿高度裁剪（底部锚定最新内容）。</summary>
    private FrameGeometry? PlanVerticalGeometry(RenderInput input)
    {
        IReadOnlyList<StitchedStrip> strips = input.Strips;
        if (strips.Count == 0) return null;

        int minAlong = int.MaxValue;
        int maxAlong = int.MinValue;
        int imageCross = 0;
        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip strip = strips[i];
            if (strip == null || strip.Bitmap == null) continue;
            minAlong = Math.Min(minAlong, strip.AlongStart);
            maxAlong = Math.Max(maxAlong, strip.AlongEnd);
            imageCross = Math.Max(imageCross, strip.CrossLength);
        }
        if (minAlong == int.MaxValue || maxAlong <= minAlong) return null;
        int middleImageH = Math.Max(Math.Max(1, input.TotalHeight), checked(maxAlong - minAlong));
        if (imageCross <= 0) imageCross = _captureRegion.Width;

        int fixedLeading = Math.Max(0, input.FixedTopHeight);
        int fixedTrailing = Math.Max(0, input.FixedBottomHeight);
        int imageH = checked(middleImageH + fixedLeading + fixedTrailing);
        if (imageCross <= 0 || middleImageH <= 0 || imageH <= 0) return null;

        double dpiScale = GetPreviewDpiScale();
        DrawingRectangle placement = input.PlacementRegion.Width > 0 && input.PlacementRegion.Height > 0
            ? input.PlacementRegion
            : _captureRegion;
        DrawingRectangle monitor = GetMonitorRect(placement);
        int borderPx = Math.Max(2, (int)Math.Round(BlueBorderDip * 2 * dpiScale));

        // 生长式纵向预览：底部锚定选区底部、只往上长。
        // 宽度：初始约等于选区宽的40%，随内容变长收窄到选区宽的20%。
        // 高度：向上长，最多到选区顶部（选区高度）；达到极限后长宽锁死，内容在窗内滚动。
        int captureWidthPx = Math.Max(1, _captureRegion.Width);
        int floorW = Math.Max(MinPreviewWindowWidthPx, (int)Math.Round(captureWidthPx * VerticalWidthFloorRatio));
        int startW = Math.Clamp((int)Math.Round(captureWidthPx * VerticalWidthStartRatio), floorW, captureWidthPx);
        if (input.FrameLimitW > 0) startW = Math.Min(startW, Math.Max(MinPreviewWindowWidthPx, input.FrameLimitW));
        if (startW < MinPreviewWindowWidthPx) startW = MinPreviewWindowWidthPx;
        if (floorW > startW) floorW = startW;

        int startContentW = Math.Max(1, startW - borderPx);
        int floorContentW = Math.Max(1, floorW - borderPx);

        // 高度上限 = 向上长到选区顶部（选区高度），并受屏幕顶边约束。
        int previewBottom = placement.Bottom;
        int maxContentH = placement.Height;
        int toScreenTop = previewBottom - monitor.Top;
        if (maxContentH > toScreenTop) maxContentH = toScreenTop;
        if (maxContentH <= borderPx + 1) return null;

        // 三段式等比 scale：完整显示优先，宽度在 [floor, start] 间收窄，高度截止在 maxContentH。
        double scaleStart = (double)startContentW / imageCross;
        double scaleFloor = (double)floorContentW / imageCross;
        double scaleHeight = (double)maxContentH / imageH;
        if (scaleStart <= 0.000001) scaleStart = 0.000001;
        if (scaleFloor <= 0.000001) scaleFloor = 0.000001;

        double scale;
        if (imageH * scaleStart <= maxContentH)
        {
            // 阶段A：内容尚矮，用初始宽度完整显示，窗随内容加长。
            scale = scaleStart;
        }
        else if (scaleHeight >= scaleFloor)
        {
            // 阶段B：高度封顶，宽度在 [floor, start] 间随内容收窄。
            scale = scaleHeight;
        }
        else
        {
            // 阶段C：宽度触底，长宽锁死，内容沿高度裁剪滚动（底部锚定最新内容）。
            scale = scaleFloor;
        }

        int contentW = Math.Max(1, (int)Math.Round(imageCross * scale));

        double scaledContentH = imageH * scale;
        double srcOffsetY = 0.0;
        if (scaledContentH > maxContentH)
        {
            // 随当前视口在整张图中的位置取可见区；反向回滚时预览才能跟着视口移动。
            // 仅在尚无视口信息时借用初始采集方向作为锚点。
            srcOffsetY = CalculatePreviewSourceOffset(input, imageH, maxContentH, scale);
        }
        int visibleCanvasH = Math.Max(1, (int)Math.Ceiling(Math.Min(scaledContentH, maxContentH)));

        int hostWindowW = contentW + borderPx;
        if (hostWindowW < MinPreviewWindowWidthPx) hostWindowW = MinPreviewWindowWidthPx;
        // 离散步进抑制 DWM 抖动：宽度按固定步长向上取整，档内内容与窗口同步稳定，不逐像素 resize。
        hostWindowW = ((hostWindowW + VerticalResizeStepPx - 1) / VerticalResizeStepPx) * VerticalResizeStepPx;
        int canvasW = Math.Max(1, hostWindowW - borderPx);
        int scaledContentW = canvasW;
        // 透明宿主从第一帧就占最终高度；生长阶段只移动内部内容的顶边。
        // 原来每新增一条都 SetWindowPos 改高度和 Y，分层窗口会反复重建表面并闪烁。
        int hostWindowH = maxContentH + borderPx;
        int contentX = 0;
        int contentY = hostWindowH - visibleCanvasH - borderPx;

        LongScrollPreviewPlacer.PlacementResult solved = SolvePlacement(
            placement, input.AvoidRect, ScrollAxis.Vertical, hostWindowW, hostWindowH, monitor,
            preferLeft: !input.DockRight);

        CalculateViewportBox(input, srcOffsetY, imageH, scale, canvasW, visibleCanvasH,
            out int boxX, out int boxY, out int boxW, out int boxH);

        return new FrameGeometry
        {
            Axis = ScrollAxis.Vertical,
            ImageW = imageCross,
            ImageH = imageH,
            CanvasW = canvasW,
            CanvasH = visibleCanvasH,
            HostW = hostWindowW,
            HostH = hostWindowH,
            WindowX = solved.Rect.X,
            WindowY = solved.Rect.Y,
            ContentX = contentX,
            ContentY = contentY,
            ContentW = hostWindowW,
            ContentH = visibleCanvasH + borderPx,
            ScaledCrossStart = Math.Max(0, (canvasW - scaledContentW) / 2),
            ScaledCrossLen = scaledContentW,
            MinAlong = minAlong,
            FixedLeading = fixedLeading,
            FixedTrailing = fixedTrailing,
            Scale = scale,
            SrcOffset = srcOffsetY,
            BoxX = boxX,
            BoxY = boxY,
            BoxW = boxW,
            BoxH = boxH,
            DpiScale = dpiScale,
        };
    }

    /// <summary>横向预览：下方/上方放置、fit 高度、沿宽度裁剪（右端锚定最新内容）。</summary>
    private FrameGeometry? PlanHorizontalGeometry(RenderInput input)
    {
        IReadOnlyList<StitchedStrip> strips = input.Strips;
        if (strips.Count == 0) return null;

        int minAlong = int.MaxValue;
        int maxAlong = int.MinValue;
        int imageCross = 0;
        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip strip = strips[i];
            if (strip == null || strip.Bitmap == null) continue;
            minAlong = Math.Min(minAlong, strip.AlongStart);
            maxAlong = Math.Max(maxAlong, strip.AlongEnd);
            imageCross = Math.Max(imageCross, strip.CrossLength);
        }
        if (minAlong == int.MaxValue || maxAlong <= minAlong) return null;
        int middleWidth = Math.Max(Math.Max(1, input.TotalHeight), checked(maxAlong - minAlong));
        if (imageCross <= 0) imageCross = _captureRegion.Height;

        int fixedLeading = Math.Max(0, input.FixedTopHeight);
        int fixedTrailing = Math.Max(0, input.FixedBottomHeight);
        int imageWidth = checked(middleWidth + fixedLeading + fixedTrailing);
        if (imageWidth <= 0 || imageCross <= 0) return null;

        double dpiScale = GetPreviewDpiScale();
        DrawingRectangle placement = input.PlacementRegion.Width > 0 && input.PlacementRegion.Height > 0
            ? input.PlacementRegion
            : _captureRegion;
        DrawingRectangle monitor = GetMonitorRect(placement);
        int borderPx = Math.Max(2, (int)Math.Round(BlueBorderDip * 2 * dpiScale));
        // 上/下侧可用空间：按选区上/下方整片屏幕高度计算（不含工具栏），
        // 工具栏仅作为避让对象交给 Solve 处理，避免工具栏占据某一侧时把该侧可用空间算成 0。
        int spaceBelow = (monitor.Bottom - GapPx) - (placement.Bottom + GapPx);
        int spaceAbove = (placement.Top - GapPx) - (monitor.Top + GapPx);

        // 生长式横向预览：右缘锚定选区右缘、只向左长（镜像纵向）。
        // 高度（交叉轴）：初始约等于选区高的40%，随内容变长收窄到选区高的20%。
        // 宽度（沿轴）：向左长，最多到选区左缘（选区宽度）；达到极限后长宽锁死，内容在窗内横向滚动。
        // 位置：优先放在上/下侧中空间更大的一侧；若该侧放不下 40%，高度自适应缩小到能完整放下。
        int captureHeightPx = Math.Max(1, _captureRegion.Height);
        int floorH = Math.Max(MinPreviewWindowWidthPx, (int)Math.Round(captureHeightPx * VerticalWidthFloorRatio));
        int wantStartH = Math.Clamp((int)Math.Round(captureHeightPx * VerticalWidthStartRatio), floorH, captureHeightPx);
        int availH = Math.Max(spaceBelow, spaceAbove);
        int startH = Math.Min(wantStartH, Math.Max(MinPreviewWindowWidthPx, availH));
        if (startH < MinPreviewWindowWidthPx) startH = MinPreviewWindowWidthPx;
        if (floorH > startH) floorH = startH;

        // 宽度（长度方向）上限 = 长到选区左缘（选区宽度），并受屏幕左右边界约束。
        int maxContentW = Math.Max(1, _captureRegion.Width);
        int screenRoomW = Math.Max(MinPreviewWindowWidthPx, monitor.Width - GapPx - GapPx - borderPx);
        if (maxContentW > screenRoomW) maxContentW = screenRoomW;

        int startContentH = Math.Max(1, startH - borderPx);
        int floorContentH = Math.Max(1, floorH - borderPx);

        // 三段式等比 scale：完整显示优先，高度在 [floor, start] 间收窄，宽度截止在 maxContentW。
        double scaleStart = (double)startContentH / imageCross;
        double scaleFloor = (double)floorContentH / imageCross;
        double scaleWidth = (double)maxContentW / imageWidth;
        if (scaleStart <= 0.000001) scaleStart = 0.000001;
        if (scaleFloor <= 0.000001) scaleFloor = 0.000001;

        double scale;
        if (imageWidth * scaleStart <= maxContentW)
        {
            // 阶段A：内容尚短，用初始高度完整显示，窗随内容向左加长。
            scale = scaleStart;
        }
        else if (scaleWidth >= scaleFloor)
        {
            // 阶段B：宽度封顶，高度在 [floor, start] 间随内容收窄。
            scale = scaleWidth;
        }
        else
        {
            // 阶段C：高度触底，长宽锁死，内容沿宽度裁剪横向滚动（右缘锚定最新内容）。
            scale = scaleFloor;
        }

        int canvasWidth = Math.Max(1, (int)Math.Ceiling(Math.Min(imageWidth * scale, maxContentW)));
        int canvasHeight = Math.Max(1, (int)Math.Round(imageCross * scale));
        double srcOffsetX = CalculatePreviewSourceOffset(input, imageWidth, canvasWidth, scale);

        int hostWindowH = canvasHeight + borderPx;
        if (hostWindowH < MinPreviewWindowWidthPx) hostWindowH = MinPreviewWindowWidthPx;
        // 离散步进抑制 DWM 抖动：高度按固定步长向上取整，档内内容与窗口同步稳定。
        hostWindowH = ((hostWindowH + VerticalResizeStepPx - 1) / VerticalResizeStepPx) * VerticalResizeStepPx;
        int hostWindowW = canvasWidth + borderPx;
        if (hostWindowW < MinPreviewWindowWidthPx) hostWindowW = MinPreviewWindowWidthPx;

        int contentWindowWidth = canvasWidth + borderPx;
        int contentWindowHeight = canvasHeight + borderPx;
        if (contentWindowWidth > hostWindowW) contentWindowWidth = hostWindowW;
        if (contentWindowHeight > hostWindowH) contentWindowHeight = hostWindowH;

        int contentX = Math.Max(0, (hostWindowW - contentWindowWidth) / 2);
        int contentY = Math.Max(0, (hostWindowH - contentWindowHeight) / 2);

        // 视口框：初始未滚动时不显示（宽度未超过选区且视口在起点）。
        bool isInitialUnscrolledState = imageWidth <= _captureRegion.Width + 4 && (input.CurrentViewportY <= 2);
        int boxX = 0;
        int boxW = 0;
        if (!isInitialUnscrolledState)
        {
            double viewportStart = input.HasViewportInfo ? input.CurrentViewportY : Math.Max(0, imageWidth - _captureRegion.Width);
            double viewportLengthForBox = input.HasViewportInfo && input.CurrentViewportHeight > 0 ? input.CurrentViewportHeight : _captureRegion.Width;
            boxX = Math.Clamp((int)Math.Round((viewportStart - srcOffsetX) * scale), 0, Math.Max(0, canvasWidth - 1));
            boxW = Math.Clamp((int)Math.Round(viewportLengthForBox * scale), 1, Math.Max(1, canvasWidth - boxX));
        }
        int boxY = 0;
        int boxH = canvasHeight;

        // 右缘锚定 + 上/下侧取空间更大者 + 冲突回退。
        bool preferBottom = spaceBelow >= spaceAbove;
        LongScrollPreviewPlacer.PlacementResult solved = SolvePlacement(
            placement,
            input.AvoidRect,
            ScrollAxis.Horizontal,
            hostWindowW,
            hostWindowH,
            monitor,
            preferLeft: false,
            preferBottom: preferBottom);

        return new FrameGeometry
        {
            Axis = ScrollAxis.Horizontal,
            ImageW = imageWidth,
            ImageH = imageCross,
            CanvasW = canvasWidth,
            CanvasH = canvasHeight,
            HostW = hostWindowW,
            HostH = hostWindowH,
            WindowX = solved.Rect.X,
            WindowY = solved.Rect.Y,
            ContentX = contentX,
            ContentY = contentY,
            ContentW = contentWindowWidth,
            ContentH = contentWindowHeight,
            ScaledCrossStart = 0,
            ScaledCrossLen = canvasHeight,
            MinAlong = minAlong,
            FixedLeading = fixedLeading,
            FixedTrailing = fixedTrailing,
            Scale = scale,
            SrcOffset = srcOffsetX,
            BoxX = boxX,
            BoxY = boxY,
            BoxW = boxW,
            BoxH = boxH,
            DpiScale = dpiScale,
        };
    }

    /// <summary>
    /// 公共渲染骨架：像素签名跳帧 → OverlayOnly 判定 → GDI 合成（沿轴统一寻址）
    /// → 组帧。横纵的全部差异都封闭在 FrameGeometry 里。
    /// </summary>
    private PreviewRenderFrame? RenderFrameFromGeometry(RenderInput input, FrameGeometry g)
    {
        try
        {
            bool horizontal = g.Axis == ScrollAxis.Horizontal;

            RenderSignature sig = BuildSignature(
                g.Axis, g.CanvasW, g.CanvasH, g.ImageW, g.ImageH,
                input.Strips, input.FixedTopBitmap, input.FixedBottomBitmap,
                g.FixedLeading, g.FixedTrailing, g.SrcOffset, g.Scale,
                input.ContentVersion);
            bool pixelsSame = _hasPixelSignature && sig.Equals(_lastPixelSignature);
            bool boxSame = g.BoxX == _lastPresentedBoxX && g.BoxY == _lastPresentedBoxY
                && g.BoxW == _lastPresentedBoxW && g.BoxH == _lastPresentedBoxH;
            bool rectSame = g.WindowX == _lastPresentedWindowX && g.WindowY == _lastPresentedWindowY
                && g.HostW == _lastPresentedWindowW && g.HostH == _lastPresentedWindowH;

            if (pixelsSame && boxSame && rectSame)
            {
                // 像素、视口框、宿主位置全部未变：整帧丢弃（暂停/滚动停顿时的
                // 重复刷新在此归零成本）。
                int dropN = Interlocked.Increment(ref _droppedFrameLogCount);
                if (dropN <= 8 || dropN % 100 == 0) PreviewLog((horizontal ? "H-" : "") + "DropUnchangedFrame #" + dropN);
                return null;
            }

            _lastPresentedBoxX = g.BoxX; _lastPresentedBoxY = g.BoxY;
            _lastPresentedBoxW = g.BoxW; _lastPresentedBoxH = g.BoxH;
            _lastPresentedWindowX = g.WindowX; _lastPresentedWindowY = g.WindowY;
            _lastPresentedWindowW = g.HostW; _lastPresentedWindowH = g.HostH;

            PreviewRenderFrame BuildFrame(byte[]? pixels, bool overlayOnly, int stride)
            {
                return new PreviewRenderFrame
                {
                    Pixels = pixels,
                    OverlayOnly = overlayOnly,
                    Width = g.CanvasW,
                    Height = g.CanvasH,
                    Stride = stride > 0 ? stride : g.CanvasW * 4,
                    WindowX = g.WindowX,
                    WindowY = g.WindowY,
                    WindowWidthPx = g.HostW,
                    WindowHeightPx = g.HostH,
                    CanvasWidthPx = g.HostW,
                    CanvasHeightPx = g.HostH,
                    ContentX = g.ContentX,
                    ContentY = g.ContentY,
                    ContentWidthPx = g.ContentW,
                    ContentHeightPx = g.ContentH,
                    ViewportBoxX = g.BoxX,
                    ViewportBoxY = g.BoxY,
                    ViewportBoxW = g.BoxW,
                    ViewportBoxH = g.BoxH,
                    ScrollDirection = input.ScrollDirection,
                    ShowTopTail = g.SrcOffset > 4,
                    ShowBottomTail = false,
                    DpiScale = g.DpiScale,
                    Axis = g.Axis,
                    RequestedAtTick = input.RequestedAtTick,
                };
            }

            if (pixelsSame)
            {
                // 像素未变但叠加层（视口框/宿主）有变化：跳过 GDI 合成、
                // LockBits 拷出、WriteableBitmap 写回与 GPU 上传，
                // 仅以 OverlayOnly 帧驱动叠加层定位（CitePix 同款"滚动=移动窗口"）。
                int overlayN = Interlocked.Increment(ref _overlayFrameLogCount);
                if (overlayN <= 8 || overlayN % 100 == 0) PreviewLog((horizontal ? "H-" : "") + "OverlayOnlyFrame #" + overlayN);
                return BuildFrame(null, true, 0);
            }

            // 超轻量直接缩放绘制（仅在极小预览画布 ~200x500px 上绘制，内存 < 0.4MB）。
            EnsurePreviewCache(g.CanvasW, g.CanvasH);
            if (_previewCacheBitmap == null || _previewCacheGraphics == null) return null;

            Graphics gdi = _previewCacheGraphics;
            // 直角裁剪：四角保持透明，与上层直角边框吻合（视觉与截图选区统一）。
            gdi.Clear(Color.Transparent);
            int previewRadius = 0;
            using (var roundClip = CreateRoundedRectPath(0, 0, g.CanvasW, g.CanvasH, previewRadius))
            {
                gdi.SetClip(roundClip);
            }

            // 固定 leading 层（纵向=顶栏，横向=左栏）：沿轴起点随裁剪偏移左移。
            if (input.FixedTopBitmap != null && g.FixedLeading > 0)
            lock (StitchedStrip.GetBitmapSyncRoot(input.FixedTopBitmap))
            {
                int leadDestAlong = (int)Math.Floor(-g.SrcOffset * g.Scale);
                int leadDestEnd = (int)Math.Ceiling((g.FixedLeading - g.SrcOffset) * g.Scale);
                if (leadDestEnd > leadDestAlong && leadDestEnd > 0)
                {
                    int bmpAlong = horizontal ? input.FixedTopBitmap.Width : input.FixedTopBitmap.Height;
                    int bmpCross = horizontal ? input.FixedTopBitmap.Height : input.FixedTopBitmap.Width;
                    int srcAlong = horizontal ? Math.Min(bmpAlong, g.FixedLeading) : bmpAlong;
                    int srcCross = horizontal ? Math.Min(bmpCross, g.ImageH) : Math.Min(bmpCross, g.ImageW);
                    var leadDest = horizontal
                        ? new DrawingRectangle(leadDestAlong, 0, leadDestEnd - leadDestAlong, g.CanvasH)
                        : new DrawingRectangle(g.ScaledCrossStart, leadDestAlong, g.ScaledCrossLen, leadDestEnd - leadDestAlong);
                    var leadSrc = horizontal
                        ? new DrawingRectangle(0, 0, srcAlong, srcCross)
                        : new DrawingRectangle(0, 0, srcCross, srcAlong);
                    gdi.DrawImage(input.FixedTopBitmap, leadDest, leadSrc, GraphicsUnit.Pixel);
                }
            }

            // 条带循环：沿轴统一寻址（destAlong/destAlongLen），cross 由几何给出。
            int canvasAlongLen = horizontal ? g.CanvasW : g.CanvasH;
            int thumbCrossLen = horizontal ? g.CanvasH : g.CanvasW;
            IReadOnlyList<StitchedStrip> strips = input.Strips;
            for (int i = 0; i < strips.Count; i++)
            {
                StitchedStrip strip = strips[i];
                if (strip == null || strip.Bitmap == null) continue;

                double stripAlongStart = g.FixedLeading + (strip.AlongStart - g.MinAlong) - g.SrcOffset;
                int destAlong = (int)Math.Floor(stripAlongStart * g.Scale);
                int destAlongLen = Math.Max(1, (int)Math.Ceiling((stripAlongStart + strip.AlongLength) * g.Scale) - destAlong);
                if (destAlong >= canvasAlongLen || destAlong + destAlongLen <= 0) continue;

                DrawingBitmap thumb = strip.GetThumbnailBitmap(thumbCrossLen);
                var destRect = horizontal
                    ? new DrawingRectangle(destAlong, 0, destAlongLen, g.CanvasH)
                    : new DrawingRectangle(g.ScaledCrossStart, destAlong, g.ScaledCrossLen, destAlongLen);
                // 小图或缩略图创建失败时 thumb 仍可能是原图；读绘制也必须遵守同一源图锁。
                lock (StitchedStrip.GetBitmapSyncRoot(thumb))
                    gdi.DrawImage(thumb, destRect,
                        new DrawingRectangle(0, 0, thumb.Width, thumb.Height),
                        GraphicsUnit.Pixel);
            }

            // 固定 trailing 层位于整张图的末端；预览裁到前半段时它应不可见。
            // 旧实现无条件钉在当前可见画布底部，向上采集后回滚会凭空出现一块滚动区域。
            if (input.FixedBottomBitmap != null && g.FixedTrailing > 0)
            lock (StitchedStrip.GetBitmapSyncRoot(input.FixedBottomBitmap))
            {
                int fullAlong = horizontal ? g.ImageW : g.ImageH;
                double trailStart = fullAlong - g.FixedTrailing - g.SrcOffset;
                int trailDestAlong = (int)Math.Floor(trailStart * g.Scale);
                int trailDestEnd = (int)Math.Ceiling((trailStart + g.FixedTrailing) * g.Scale);
                if (trailDestEnd > trailDestAlong && trailDestAlong < canvasAlongLen && trailDestEnd > 0)
                {
                    int bmpAlong = horizontal ? input.FixedBottomBitmap.Width : input.FixedBottomBitmap.Height;
                    int bmpCross = horizontal ? input.FixedBottomBitmap.Height : input.FixedBottomBitmap.Width;
                    int srcAlong = horizontal ? Math.Min(bmpAlong, g.FixedTrailing) : bmpAlong;
                    int srcCross = horizontal ? Math.Min(bmpCross, g.ImageH) : Math.Min(bmpCross, g.ImageW);
                    var trailDest = horizontal
                        ? new DrawingRectangle(trailDestAlong, 0, trailDestEnd - trailDestAlong, g.CanvasH)
                        : new DrawingRectangle(g.ScaledCrossStart, trailDestAlong, g.ScaledCrossLen, trailDestEnd - trailDestAlong);
                    var trailSrc = horizontal
                        ? new DrawingRectangle(0, 0, srcAlong, srcCross)
                        : new DrawingRectangle(0, 0, srcCross, srcAlong);
                    gdi.DrawImage(input.FixedBottomBitmap, trailDest, trailSrc, GraphicsUnit.Pixel);
                }
            }

            gdi.ResetClip();
            byte[] pixels = CopyBitmapPixels(_previewCacheBitmap, out int stride);

            if (horizontal)
            {
                int hLogN = Interlocked.Increment(ref _hRenderLogCount);
                if (hLogN <= 12 || hLogN % 50 == 0)
                {
                    PreviewLog("H-frame canvas=" + g.CanvasW + "x" + g.CanvasH + " window=(" + g.WindowX + "," + g.WindowY + ") " + g.HostW + "x" + g.HostH + " imgW=" + g.ImageW + " imgH=" + g.ImageH);
                }
            }
            else
            {
                int logN = Interlocked.Increment(ref _renderLogCount);
                if (logN <= 12 || logN % 50 == 0)
                {
                    PreviewLog("RenderFrame visible=" + g.CanvasW + "x" + g.CanvasH + ", image=" + g.ImageW + "x" + g.ImageH + ", scale=" + g.Scale.ToString("0.0000") + ", host=" + g.WindowX + "," + g.WindowY + " " + g.HostW + "x" + g.HostH);
                }
            }

            // 记录本帧像素签名，供后续帧比对跳过。
            _lastPixelSignature = sig;
            _hasPixelSignature = true;
            return BuildFrame(pixels, false, stride);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _renderFailures);
            _hasPixelSignature = false;
            DisposePreviewCache();
            try { AppLogger.Warn("LongScroll.RenderFrame(" + g.Axis + "): " + ex.GetType().Name + ": " + ex.Message); } catch { }
            return null;
        }
    }
    private void EnsurePreviewCache(int width, int height)
    {
        if (_previewCacheBitmap != null && _previewCacheGraphics != null && _previewCacheWidthPx == width && _previewCacheHeightPx == height) return;
        try { _previewCacheGraphics?.Dispose(); } catch { }
        try { _previewCacheBitmap?.Dispose(); } catch { }
        _previewCacheGraphics = null;
        _previewCacheBitmap = null;
        _previewCacheBitmap = new DrawingBitmap(width, height, PixelFormat.Format32bppPArgb);
        _previewCacheGraphics = Graphics.FromImage(_previewCacheBitmap);
        _previewCacheGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        _previewCacheGraphics.SmoothingMode = SmoothingMode.None;
        _previewCacheGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        _previewCacheGraphics.InterpolationMode = InterpolationMode.Low;
        _previewCacheWidthPx = width;
        _previewCacheHeightPx = height;
    }

    private void DisposePreviewCache()
    {
        try { _previewCacheGraphics?.Dispose(); } catch { }
        try { _previewCacheBitmap?.Dispose(); } catch { }
        _previewCacheGraphics = null;
        _previewCacheBitmap = null;
        _previewCacheWidthPx = 0;
        _previewCacheHeightPx = 0;
    }

    private static GraphicsPath CreateRoundedRectPath(int x, int y, int width, int height, int radius)
    {
        var path = new GraphicsPath();
        if (width <= 0 || height <= 0)
        {
            return path;
        }
        // 半径 ≤ 0 时画直角矩形，不再强制最小 1px 圆角。
        if (radius <= 0)
        {
            path.AddRectangle(new DrawingRectangle(x, y, width, height));
            return path;
        }
        int r = Math.Max(1, Math.Min(radius, Math.Min(width, height) / 2));
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + width - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + width - r * 2, y + height - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + height - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static double CalculatePreviewSourceOffset(RenderInput input, int imageAlong, int canvasAlong, double scale)
    {
        double visibleAlong = canvasAlong / scale;
        double maxOffset = Math.Max(0.0, imageAlong - visibleAlong);
        if (maxOffset <= 0.0) return 0.0;
        if (!input.HasViewportInfo)
            return input.PlacementDirection < 0 ? 0.0 : maxOffset;

        double viewportStart = input.FixedTopHeight + input.CurrentViewportY;
        double viewportLength = Math.Max(1, input.CurrentViewportHeight);
        double centered = viewportStart + viewportLength / 2.0 - visibleAlong / 2.0;
        return Math.Clamp(centered, 0.0, maxOffset);
    }

    private void CalculateViewportBox(RenderInput input, double visibleSourceTop, double visibleSourceBottom, double scale, int canvasW, int canvasH, out int boxX, out int boxY, out int boxW, out int boxH)
    {
        boxX = 0;
        boxW = canvasW;
        double top;
        double viewportH;
        if (input.HasViewportInfo)
        {
            // 展示层保持用户认知：用户框选的是完整截图区域，预览里的滚动捕获框也显示完整截图区域。
            // 固定顶/底层排除只作为后台拼接算法细节，不暴露到 UI，避免让用户误以为只截取了中间滚动区。
            top = input.CurrentViewportY;
            viewportH = input.CurrentViewportHeight > 0 ? input.CurrentViewportHeight : _captureRegion.Height;
        }
        else
        {
            top = Math.Max(0, visibleSourceBottom - _captureRegion.Height);
            viewportH = _captureRegion.Height;
        }
        double bottom = top + viewportH;
        if (bottom < visibleSourceTop || top > visibleSourceBottom)
        {
            // 边界/回弹时，viewport 可能短暂落在当前预览可见片段外。
            // 旧逻辑统一兜底到底部，会导致“滚动捕获框”突然跳到另一端。
            // 这里改为贴近真实方向钉住边缘：在上方就贴顶部，在下方就贴底部，避免突兀瞬移。
            double fallbackH = Math.Max(1, viewportH * scale);
            boxH = Math.Max(1, Math.Min(canvasH, (int)Math.Round(fallbackH)));
            boxY = bottom < visibleSourceTop ? 0 : Math.Max(0, canvasH - boxH);
            return;
        }

        double clippedTop = Math.Max(top, visibleSourceTop);
        double clippedBottom = Math.Min(bottom, visibleSourceBottom);
        // 🎯 视口同频极速平滑定位：高亮矩形框毫秒级同步显示当前屏幕视口在长图全貌中的精准位置
        boxY = (int)Math.Round((clippedTop - visibleSourceTop) * scale);
        boxH = Math.Max(2, (int)Math.Round((clippedBottom - clippedTop) * scale));
        if (boxY < 0) boxY = 0;
        if (boxY >= canvasH) boxY = canvasH - 1;
        if (boxY + boxH > canvasH) boxH = canvasH - boxY;
        if (boxH < 2) boxH = 2;
    }

    private byte[] CopyBitmapPixels(DrawingBitmap bitmap, out int stride)
    {
        BitmapData data = bitmap.LockBits(new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            stride = Math.Abs(data.Stride);
            int required = stride * bitmap.Height;

            // UI present 是异步的，不能复用同一块缓冲区；否则 worker 产出下一帧时会覆盖上一帧像素，
            // 造成像素内容和 frame 元数据不匹配，表现为预览图错位、拉花或局部残影。
            byte[] pixels = ArrayPool<byte>.Shared.Rent(required);
            if (data.Stride > 0)
            {
                Marshal.Copy(data.Scan0, pixels, 0, required);
            }
            else
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    IntPtr src = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(src, pixels, y * stride, stride);
                }
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void QueuePresentPreviewFrame(PreviewRenderFrame frame)
    {
        if (_isClosing || frame == null)
        {
            ReleasePreviewRenderFrame(frame);
            return;
        }
        lock (_uiFrameLock)
        {
            if (_isClosing) { ReleasePreviewRenderFrame(frame); return; }
            ReleasePreviewRenderFrame(_latestFrameForUi);
            _latestFrameForUi = frame;
        }

        SchedulePreviewFrame();
    }

    private void DrainLatestPreviewFrameOnUi()
    {
        Interlocked.Exchange(ref _uiPresentScheduled, 0);
        try
        {
            PreviewRenderFrame? frame;
            lock (_uiFrameLock)
            {
                frame = _latestFrameForUi;
                _latestFrameForUi = null;
            }

            // 每个显示帧只写回一次；先消费像素，再启动下一次合成，避免未呈现的像素
            // 被后续 OverlayOnly 帧替换。停止滚动后的末帧也由 worker 主动排入显示帧。
            if (frame != null && !_isClosing)
            {
                try
                {
                    PresentPreviewFrame(frame);
                    Interlocked.Increment(ref _presentedFrames);
                    if (frame.RequestedAtTick > 0)
                    {
                        long elapsed = Stopwatch.GetTimestamp() - frame.RequestedAtTick;
                        _deliveryTicks += elapsed;
                        _maxDeliveryTicks = Math.Max(_maxDeliveryTicks, elapsed);
                    }
                }
                finally
                {
                    ReleasePreviewRenderFrame(frame);
                }
            }
            else
            {
                ReleasePreviewRenderFrame(frame);
            }
        }
        finally
        {
            StartRenderWorkerForFrame();
        }
    }

    private void PresentPreviewFrame(PreviewRenderFrame frame)
    {
        if (_isClosing || frame.Width <= 0 || frame.Height <= 0) return;

        try
        {
            double dpiScale = frame.DpiScale > 0 ? frame.DpiScale : 1.0;
            bool sourceChanged = false;
            // OverlayOnly 帧不携带像素：跳过位图重建与写回，仅更新叠加层。
            if (!frame.OverlayOnly && frame.Pixels != null)
            {
                if (_writeableBitmap == null || _writeableBitmapWidth != frame.Width || _writeableBitmapHeight != frame.Height)
                {
                    // Release the previous native bitmap; canvas size changes as the
                    // long image grows and each reallocation leaked one before.
                    try { _writeableBitmap?.Dispose(); } catch { }
                    // 只让 WriteableBitmap 承载“图片内容区”，不要再承载整个透明宿主窗口。
                    // 宿主透明区、蓝框、绿框都由 Avalonia 控件定位，避免 DPI/透明像素/裁剪混在同一张 bitmap 里导致显示残缺。
                    _writeableBitmap = new WriteableBitmap(
                        new PixelSize(frame.Width, frame.Height),
                        new Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    _writeableBitmapWidth = frame.Width;
                    _writeableBitmapHeight = frame.Height;
                    sourceChanged = true;
                }

                using (ILockedFramebuffer fb = _writeableBitmap.Lock())
                {
                    int copyBytes = Math.Min(Math.Min(frame.Stride, frame.Width * 4), fb.RowBytes);
                    // 🎯 极致优化：如果 Stride 与 RowBytes 完全一致，1 次全局内存块拷贝，零循环开销！
                    if (frame.Stride == fb.RowBytes && copyBytes == fb.RowBytes)
                    {
                        int totalBytes = Math.Min(frame.Pixels.Length, fb.RowBytes * frame.Height);
                        Marshal.Copy(frame.Pixels, 0, fb.Address, totalBytes);
                    }
                    else
                    {
                        for (int y = 0; y < frame.Height; y++)
                        {
                            if (y < 0 || y >= fb.Size.Height) continue;
                            IntPtr dst = IntPtr.Add(fb.Address, y * fb.RowBytes);
                            Marshal.Copy(frame.Pixels, y * frame.Stride, dst, copyBytes);
                        }
                    }
                }
            }

            IntPtr hwnd = GetWindowHwnd();
            bool rectChanged = !_hasLastHostRect
                || _lastHostX != frame.WindowX
                || _lastHostY != frame.WindowY
                || _lastHostW != frame.WindowWidthPx
                || _lastHostH != frame.WindowHeightPx;
            if (hwnd != IntPtr.Zero && rectChanged)
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    frame.WindowX,
                    frame.WindowY,
                    frame.WindowWidthPx,
                    frame.WindowHeightPx,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                _lastHostX = frame.WindowX;
                _lastHostY = frame.WindowY;
                _lastHostW = frame.WindowWidthPx;
                _lastHostH = frame.WindowHeightPx;
                _hasLastHostRect = true;
            }

            double windowWidthDip = frame.WindowWidthPx / dpiScale;
            double windowHeightDip = frame.WindowHeightPx / dpiScale;
            double canvasWidthDip = frame.CanvasWidthPx / dpiScale;
            double canvasHeightDip = frame.CanvasHeightPx / dpiScale;
            double contentXDip = frame.ContentX / dpiScale;
            double contentYDip = frame.ContentY / dpiScale;
            double contentWidthDip = frame.ContentWidthPx / dpiScale;
            double contentHeightDip = frame.ContentHeightPx / dpiScale;
            double borderDip = BlueBorderDip;

            if (Math.Abs(_lastWindowWidthDip - windowWidthDip) > 0.001) { Width = windowWidthDip; _lastWindowWidthDip = windowWidthDip; }
            if (Math.Abs(_lastWindowHeightDip - windowHeightDip) > 0.001) { Height = windowHeightDip; _lastWindowHeightDip = windowHeightDip; }
            if (rectChanged) Position = new PixelPoint(frame.WindowX, frame.WindowY);

            if (Math.Abs(_lastCanvasWidthDip - canvasWidthDip) > 0.001) { HostCanvas.Width = canvasWidthDip; _lastCanvasWidthDip = canvasWidthDip; }
            if (Math.Abs(_lastCanvasHeightDip - canvasHeightDip) > 0.001) { HostCanvas.Height = canvasHeightDip; _lastCanvasHeightDip = canvasHeightDip; }

            if (sourceChanged || !ReferenceEquals(PreviewImage.Source, _writeableBitmap)) PreviewImage.Source = _writeableBitmap;
            double imageWidthDip = frame.Width / dpiScale;
            double imageHeightDip = frame.Height / dpiScale;
            double imageXDip = contentXDip + borderDip;
            double imageYDip = contentYDip + borderDip;
            if (Math.Abs(_lastImageWidthDip - imageWidthDip) > 0.001) { PreviewImage.Width = imageWidthDip; _lastImageWidthDip = imageWidthDip; }
            if (Math.Abs(_lastImageHeightDip - imageHeightDip) > 0.001) { PreviewImage.Height = imageHeightDip; _lastImageHeightDip = imageHeightDip; }
            Avalonia.Controls.Canvas.SetLeft(PreviewImage, imageXDip);
            Avalonia.Controls.Canvas.SetTop(PreviewImage, imageYDip);
            PreviewImage.InvalidateVisual();

            // 预览窗外框与截图选区逐像素一致：黑色实线底层 + 白色虚线叠加在同一轮廓，直角无圆角。
            // 两个元素定位到完全相同的位置，白虚线(ZIndex 11)盖在黑实线(ZIndex 10)上，
            // 虚线段之间露出黑线，构成选区的对比虚线效果。
            double blueBorderInset = borderDip / 2.0;
            double blueBorderX = contentXDip + blueBorderInset;
            double blueBorderY = contentYDip + blueBorderInset;
            double blueBorderW = Math.Max(1.0, contentWidthDip - borderDip);
            double blueBorderH = Math.Max(1.0, contentHeightDip - borderDip);
            Avalonia.Controls.Canvas.SetLeft(OuterBlueBorder, blueBorderX);
            Avalonia.Controls.Canvas.SetTop(OuterBlueBorder, blueBorderY);
            if (double.IsNaN(OuterBlueBorder.Width) || Math.Abs(OuterBlueBorder.Width - blueBorderW) > 0.001) OuterBlueBorder.Width = blueBorderW;
            if (double.IsNaN(OuterBlueBorder.Height) || Math.Abs(OuterBlueBorder.Height - blueBorderH) > 0.001) OuterBlueBorder.Height = blueBorderH;
            Avalonia.Controls.Canvas.SetLeft(DashedWhiteBorder, blueBorderX);
            Avalonia.Controls.Canvas.SetTop(DashedWhiteBorder, blueBorderY);
            if (double.IsNaN(DashedWhiteBorder.Width) || Math.Abs(DashedWhiteBorder.Width - blueBorderW) > 0.001) DashedWhiteBorder.Width = blueBorderW;
            if (double.IsNaN(DashedWhiteBorder.Height) || Math.Abs(DashedWhiteBorder.Height - blueBorderH) > 0.001) DashedWhiteBorder.Height = blueBorderH;

            // 当前滚动捕获框与预览窗外框允许重合；绿色框 ZIndex 更高且不透明，会直接覆盖蓝色框。
            double boxX = contentXDip + borderDip + frame.ViewportBoxX / dpiScale;
            double boxY = contentYDip + borderDip + frame.ViewportBoxY / dpiScale;
            double boxW = frame.ViewportBoxW / dpiScale;
            double boxH = frame.ViewportBoxH / dpiScale;
            SmoothViewportRect(ref boxX, ref boxY, ref boxW, ref boxH, rectChanged || sourceChanged);
            if (Math.Abs(_lastViewportX - boxX) > 0.001) { Avalonia.Controls.Canvas.SetLeft(CurrentViewportBorder, boxX); _lastViewportX = boxX; }
            if (Math.Abs(_lastViewportY - boxY) > 0.001) { Avalonia.Controls.Canvas.SetTop(CurrentViewportBorder, boxY); _lastViewportY = boxY; }
            if (Math.Abs(_lastViewportW - boxW) > 0.001) { CurrentViewportBorder.Width = boxW; _lastViewportW = boxW; }
            if (Math.Abs(_lastViewportH - boxH) > 0.001) { CurrentViewportBorder.Height = boxH; _lastViewportH = boxH; }

            UpdatePreviewViewportMask(
                contentXDip + borderDip,
                contentYDip + borderDip,
                Math.Max(1.0, contentWidthDip - borderDip * 2.0),
                Math.Max(1.0, contentHeightDip - borderDip * 2.0),
                boxX,
                boxY,
                boxW,
                boxH);

            UpdateTailCue(frame, dpiScale, contentXDip + borderDip, contentYDip + borderDip, contentWidthDip - borderDip * 2, contentHeightDip - borderDip * 2);

            int presentN = Interlocked.Increment(ref _presentLogCount);
            if (presentN <= 12 || presentN % 50 == 0)
            {
                PreviewLog("Present frame=" + frame.Width + "x" + frame.Height + ", window=" + frame.WindowX + "," + frame.WindowY + " " + frame.WindowWidthPx + "x" + frame.WindowHeightPx + ", box=" + frame.ViewportBoxX + "," + frame.ViewportBoxY + " " + frame.ViewportBoxW + "x" + frame.ViewportBoxH + ", rectChanged=" + rectChanged + ", sourceChanged=" + sourceChanged);
            }
        }
        catch
        {
        }
    }

    private void SmoothViewportRect(ref double x, ref double y, ref double w, ref double h, bool reset)
    {
        // 🎯 彻底取消平滑追赶的阻尼延迟，高亮蓝框 0 毫秒即时同频硬跟屏幕视口位置！
        _smoothedViewportX = x;
        _smoothedViewportY = y;
        _smoothedViewportW = w;
        _smoothedViewportH = h;
    }

    private void UpdatePreviewViewportMask(double contentX, double contentY, double contentW, double contentH, double viewportX, double viewportY, double viewportW, double viewportH)
    {
        try
        {
            double left = Math.Max(contentX, viewportX);
            double top = Math.Max(contentY, viewportY);
            double right = Math.Min(contentX + contentW, viewportX + viewportW);
            double bottom = Math.Min(contentY + contentH, viewportY + viewportH);
            if (right <= left || bottom <= top)
            {
                SetMaskRect(PreviewMaskTop, contentX, contentY, contentW, contentH);
                SetMaskRect(PreviewMaskBottom, 0, 0, 0, 0);
                SetMaskRect(PreviewMaskLeft, 0, 0, 0, 0);
                SetMaskRect(PreviewMaskRight, 0, 0, 0, 0);
                return;
            }

            SetMaskRect(PreviewMaskTop, contentX, contentY, contentW, Math.Max(0.0, top - contentY));
            SetMaskRect(PreviewMaskBottom, contentX, bottom, contentW, Math.Max(0.0, contentY + contentH - bottom));
            SetMaskRect(PreviewMaskLeft, contentX, top, Math.Max(0.0, left - contentX), Math.Max(0.0, bottom - top));
            SetMaskRect(PreviewMaskRight, right, top, Math.Max(0.0, contentX + contentW - right), Math.Max(0.0, bottom - top));
        }
        catch { }
    }

    private void SetMaskRect(Control mask, double x, double y, double width, double height)
    {
        Avalonia.Controls.Canvas.SetLeft(mask, x);
        Avalonia.Controls.Canvas.SetTop(mask, y);
        mask.Width = Math.Max(0.0, width);
        mask.Height = Math.Max(0.0, height);
        mask.IsVisible = _isHovering && width > 0.5 && height > 0.5;
    }

    private void UpdateTailCue(PreviewRenderFrame frame, double dpiScale, double contentX, double contentY, double contentW, double contentH)
    {
        try
        {
            if (contentW < 1) contentW = 1;
            if (contentH < 1) contentH = 1;
            bool showTop = frame.ShowTopTail;
            bool showBottom = frame.ShowBottomTail;
            _lastShowTopTail = showTop;
            _lastShowBottomTail = showBottom;
            if (frame.Axis == ScrollAxis.Horizontal)
            {
                double shadowW = Math.Min(42.0, Math.Max(18.0, contentW * 0.12));
                double arrowWHorizontal = 28.0;
                double arrowHHorizontal = 32.0;
                double arrowY = contentY + Math.Max(0, (contentH - arrowHHorizontal) / 2.0);
                TopTailArrow.Text = "◀";
                BottomTailArrow.Text = "▶";
                TopTailShadow.Opacity = _isHovering && showTop ? 1.0 : 0.0;
                BottomTailShadow.Opacity = _isHovering && showBottom ? 1.0 : 0.0;
                TopTailArrow.Opacity = _isHovering && showTop ? 0.90 : 0.0;
                BottomTailArrow.Opacity = _isHovering && showBottom ? 0.90 : 0.0;
                TopTailShadow.Width = shadowW;
                BottomTailShadow.Width = shadowW;
                TopTailShadow.Height = contentH;
                BottomTailShadow.Height = contentH;
                Avalonia.Controls.Canvas.SetLeft(TopTailShadow, contentX);
                Avalonia.Controls.Canvas.SetTop(TopTailShadow, contentY);
                Avalonia.Controls.Canvas.SetLeft(BottomTailShadow, contentX + Math.Max(0, contentW - shadowW));
                Avalonia.Controls.Canvas.SetTop(BottomTailShadow, contentY);
                TopTailArrow.Width = arrowWHorizontal;
                TopTailArrow.Height = arrowHHorizontal;
                BottomTailArrow.Width = arrowWHorizontal;
                BottomTailArrow.Height = arrowHHorizontal;
                Avalonia.Controls.Canvas.SetLeft(TopTailArrow, contentX + 2);
                Avalonia.Controls.Canvas.SetTop(TopTailArrow, arrowY);
                Avalonia.Controls.Canvas.SetLeft(BottomTailArrow, contentX + Math.Max(0, contentW - arrowWHorizontal - 2));
                Avalonia.Controls.Canvas.SetTop(BottomTailArrow, arrowY);
                return;
            }

            TopTailArrow.Text = "▲";
            BottomTailArrow.Text = "▼";
            double shadowH = Math.Min(42.0, Math.Max(18.0, contentH * 0.18));
            double arrowW = 32.0;
            double arrowH = 28.0;
            double arrowX = contentX + Math.Max(0, (contentW - arrowW) / 2.0);

            TopTailShadow.Opacity = _isHovering && showTop ? 1.0 : 0.0;
            BottomTailShadow.Opacity = _isHovering && showBottom ? 1.0 : 0.0;
            TopTailArrow.Opacity = _isHovering && showTop ? 0.90 : 0.0;
            BottomTailArrow.Opacity = _isHovering && showBottom ? 0.90 : 0.0;

            TopTailShadow.Width = contentW;
            BottomTailShadow.Width = contentW;
            TopTailShadow.Height = shadowH;
            BottomTailShadow.Height = shadowH;
            Avalonia.Controls.Canvas.SetLeft(TopTailShadow, contentX);
            Avalonia.Controls.Canvas.SetTop(TopTailShadow, contentY);
            Avalonia.Controls.Canvas.SetLeft(BottomTailShadow, contentX);
            Avalonia.Controls.Canvas.SetTop(BottomTailShadow, contentY + Math.Max(0, contentH - shadowH));

            TopTailArrow.Width = arrowW;
            TopTailArrow.Height = arrowH;
            BottomTailArrow.Width = arrowW;
            BottomTailArrow.Height = arrowH;
            Avalonia.Controls.Canvas.SetLeft(TopTailArrow, arrowX);
            Avalonia.Controls.Canvas.SetTop(TopTailArrow, contentY + 4);
            Avalonia.Controls.Canvas.SetLeft(BottomTailArrow, arrowX);
            Avalonia.Controls.Canvas.SetTop(BottomTailArrow, contentY + Math.Max(0, contentH - arrowH - 2));
        }
        catch { }
    }

    private void PositionNearCaptureRegion()
    {
        try
        {
            DrawingRectangle placement;
            DrawingRectangle? avoid;
            bool dockRight;
            int frameLimitW;
            lock (_dataLock)
            {
                placement = _placementRegion.Width > 0 && _placementRegion.Height > 0 ? _placementRegion : _captureRegion;
                avoid = _avoidRect;
                dockRight = _dockRight;
                frameLimitW = _frameLimitWidthPx;
            }

            double dpiScale = GetPreviewDpiScale();
            DrawingRectangle captureMonitor = GetMonitorRect(placement);
            int borderPx = Math.Max(2, (int)Math.Round(BlueBorderDip * 2 * dpiScale));

            if (_axis == ScrollAxis.Horizontal)
            {
                // 生长式横向首帧：右缘锚定选区右缘、向左长；高≈选区高40%（20% 底线）；
                // position 上/下侧选空间更大的一侧；空间不足时高度自适应缩小。
                int capHpx = Math.Max(1, placement.Height);
                int floorH = Math.Max(MinPreviewWindowWidthPx, (int)Math.Round(capHpx * VerticalWidthFloorRatio));
                int wantStartH = Math.Clamp((int)Math.Round(capHpx * VerticalWidthStartRatio), floorH, capHpx);
                // 按选区上/下方整片屏幕高度计算可用空间（不含工具栏），工具栏避让交给 Solve。
                int spaceBelow = (captureMonitor.Bottom - GapPx) - (placement.Bottom + GapPx);
                int spaceAbove = (placement.Top - GapPx) - (captureMonitor.Top + GapPx);
                int availH = Math.Max(spaceBelow, spaceAbove);
                int startH = Math.Min(wantStartH, Math.Max(MinPreviewWindowWidthPx, availH));
                if (startH < MinPreviewWindowWidthPx) startH = MinPreviewWindowWidthPx;
                if (floorH > startH) floorH = startH;
                int height = startH + borderPx;

                int maxContentW = Math.Max(1, placement.Width);
                int screenRoomW = Math.Max(MinPreviewWindowWidthPx, captureMonitor.Width - GapPx - GapPx - borderPx);
                if (maxContentW > screenRoomW) maxContentW = screenRoomW;
                double scale = (double)Math.Max(1, startH - borderPx) / Math.Max(1, placement.Height);
                int width = Math.Min(maxContentW, Math.Max(MinPreviewWindowWidthPx, (int)Math.Ceiling(placement.Width * scale))) + borderPx;
                width = Math.Max(MinPreviewWindowWidthPx, width);

                bool preferBottom = spaceBelow >= spaceAbove;
                LongScrollPreviewPlacer.PlacementResult solved = SolvePlacement(
                    placement, avoid, ScrollAxis.Horizontal, width, height, captureMonitor,
                    preferLeft: false, preferBottom: preferBottom);
                Position = new PixelPoint(solved.Rect.X, solved.Rect.Y);
                return;
            }

            // 生长式纵向预览首帧尺寸：底部对齐选区底部、向上长，宽≈选区宽40%（复用上方 borderPx）。
            int capW = Math.Max(1, placement.Width);
            int capH = Math.Max(1, placement.Height);
            int fFloorW = Math.Max(MinPreviewWindowWidthPx, (int)Math.Round(capW * VerticalWidthFloorRatio));
            int fStartW = Math.Clamp((int)Math.Round(capW * VerticalWidthStartRatio), fFloorW, capW);
            if (frameLimitW > 0) fStartW = Math.Min(fStartW, Math.Max(MinPreviewWindowWidthPx, frameLimitW));
            if (fStartW < MinPreviewWindowWidthPx) fStartW = MinPreviewWindowWidthPx;
            if (fFloorW > fStartW) fFloorW = fStartW;

            int startContentW = Math.Max(1, fStartW - borderPx);
            int floorContentW = Math.Max(1, fFloorW - borderPx);

            int maxContentH = capH;
            int toTop = placement.Bottom - captureMonitor.Top;
            if (maxContentH > toTop) maxContentH = toTop;
            if (maxContentH <= borderPx + 1) maxContentH = borderPx + 2;

            double scStart = (double)startContentW / capW;
            double scFloor = (double)floorContentW / capW;
            double scHeight = (double)maxContentH / capH;
            if (scStart <= 0.000001) scStart = 0.000001;
            if (scFloor <= 0.000001) scFloor = 0.000001;

            double sc;
            if (capH * scStart <= maxContentH) sc = scStart;
            else if (scHeight >= scFloor) sc = scHeight;
            else sc = scFloor;

            int initContentW = Math.Max(1, (int)Math.Round(capW * sc));

            int initHostW = initContentW + borderPx;
            if (initHostW < MinPreviewWindowWidthPx) initHostW = MinPreviewWindowWidthPx;
            // 与渲染侧同一步进，首帧窗口尺寸即落在 4px 档位上，避免起帧即二次 resize 跳变。
            initHostW = ((initHostW + VerticalResizeStepPx - 1) / VerticalResizeStepPx) * VerticalResizeStepPx;
            // 与后台第一帧相同的最终透明宿主高度选边，不能先按短缩略图选一侧再换边。
            int initHostH = maxContentH + borderPx;

            LongScrollPreviewPlacer.PlacementResult solvedV = SolvePlacement(
                placement, avoid, ScrollAxis.Vertical, initHostW, initHostH, captureMonitor, preferLeft: !dockRight);
            Position = new PixelPoint(solvedV.Rect.X, solvedV.Rect.Y);
        }
        catch { }
    }

    private LongScrollPreviewPlacer.PlacementResult SolvePlacement(DrawingRectangle capture, DrawingRectangle? toolbar,
        ScrollAxis axis, int width, int height, DrawingRectangle bounds, bool preferLeft = false, bool preferBottom = true)
    {
        lock (_placementLock)
        {
            // 尺寸、滚动方向和工具栏展开不是换边理由；只有真实摆放环境改变才重新求解。
            bool locked = _dockLocked && _lockedPlacementAxis == axis
                && _lockedPlacementRegion == capture && _lockedPlacementBounds == bounds;
            if (locked)
            {
                preferLeft = !_lockedDockRight;
                preferBottom = _lockedPreferBottom;
            }
            else if (axis == ScrollAxis.Vertical)
            {
                int leftRoom = capture.Left - GapPx - bounds.Left;
                int rightRoom = bounds.Right - capture.Right - GapPx;
                if (leftRoom < width && rightRoom < width) preferLeft = leftRoom > rightRoom;
            }
            var result = LongScrollPreviewPlacer.SolveCore(capture, toolbar, axis, width, height, bounds,
                preferLeft, preferBottom, stayOnPreferredSide: locked);
            if (!locked)
            {
                // 记录求解器实际采用的侧边，不能记传入偏好，否则第一次滚动会翻回被否决的一侧。
                _lockedDockRight = result.CandidateIndex == 0 ? !preferLeft : preferLeft;
                _lockedPreferBottom = result.CandidateIndex == 0 ? preferBottom : !preferBottom;
                _lockedPlacementAxis = axis; _lockedPlacementRegion = capture; _lockedPlacementBounds = bounds;
                _dockLocked = true;
            }
            return result;
        }
    }

    private DrawingRectangle GetMonitorRect(DrawingRectangle region)
    {
        long now = Environment.TickCount64;
        lock (_monitorCacheLock)
        {
            if (_hasMonitorCache
                && now - _monitorCacheTimestamp < MonitorCacheLifetimeMs
                && _monitorCacheInput.Left == region.Left
                && _monitorCacheInput.Top == region.Top
                && _monitorCacheInput.Width == region.Width
                && _monitorCacheInput.Height == region.Height)
            {
                return _monitorCacheResult;
            }
        }

        DrawingRectangle result = ComputeMonitorRect(region);

        lock (_monitorCacheLock)
        {
            _monitorCacheTimestamp = Environment.TickCount64;
            _monitorCacheInput = region;
            _monitorCacheResult = result;
            _hasMonitorCache = true;
        }
        return result;
    }

    private DrawingRectangle ComputeMonitorRect(DrawingRectangle region)
    {
        try
        {
            var monitors = MonitorHelper.GetMonitorRects();
            int cx = region.Left + region.Width / 2;
            int cy = region.Top + region.Height / 2;
            foreach (DrawingRectangle monitor in monitors)
            {
                if (monitor.Contains(cx, cy)) return monitor;
            }

            foreach (DrawingRectangle monitor in monitors)
            {
                if (monitor.IntersectsWith(region)) return monitor;
            }
        }
        catch { }

        return _virtualScreen;
    }

    private double GetPreviewDpiScale()
    {
        long now = Environment.TickCount64;
        if (now - _dpiCacheTimestamp < DpiCacheLifetimeMs && _dpiCacheValue > 0)
        {
            return _dpiCacheValue;
        }

        double scale = ComputePreviewDpiScale();
        _dpiCacheTimestamp = Environment.TickCount64;
        _dpiCacheValue = scale;
        return scale;
    }

    private double ComputePreviewDpiScale()
    {
        try
        {
            IntPtr hwnd = GetWindowHwnd();
            if (hwnd != IntPtr.Zero)
            {
                uint windowDpi = NativeMethods.GetDpiForWindow(hwnd);
                if (windowDpi > 0) return Math.Max(1.0, windowDpi / 96.0);
            }
        }
        catch { }

        try
        {
            double desktopScaling = DesktopScaling;
            if (desktopScaling > 0) return Math.Max(1.0, desktopScaling);
        }
        catch { }

        try
        {
            uint dpi = NativeMethods.GetDpiForSystem();
            if (dpi > 0) return Math.Max(1.0, dpi / 96.0);
        }
        catch { }

        return 1.0;
    }

    // S3 像素签名：把"决定合成像素的全部输入"折叠成可比较结构。
    // 裁剪偏移与缩放比必须单列：同一画布可在正反方向间切换可见区，
    // 仅靠画布/图像尺寸会误判像素未变，导致反向滚动时继续显示旧画面。
    // 条带部分折叠为 64 位组合哈希（对象身份 + 几何），配合 S2 包装复用，
    // 正常滚动时只有尾部新增条带会改变哈希。
    private static RenderSignature BuildSignature(
        ScrollAxis axis, int canvasW, int canvasH, int imageW, int imageH,
        IReadOnlyList<StitchedStrip> strips, DrawingBitmap? fixedTop, DrawingBitmap? fixedBottom,
        int fixedTopH, int fixedBottomH, double srcOffset, double scale, int contentVersion)
    {
        long stripHash = unchecked((long)0x9E3779B97F4A7C15UL) ^ strips.Count;
        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip? s = strips[i];
            if (s == null)
            {
                stripHash = (stripHash * 16777619L) ^ (i + 1);
                continue;
            }
            long part = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s);
            part = (part * 16777619L) ^ s.Y;
            part = (part * 16777619L) ^ s.Width;
            part = (part * 16777619L) ^ s.Height;
            stripHash = (stripHash * 16777619L) ^ part;
        }
        return new RenderSignature(axis, canvasW, canvasH, imageW, imageH,
            fixedTopH, fixedBottomH, srcOffset, scale, stripHash, fixedTop, fixedBottom, contentVersion);
    }

    private readonly struct RenderSignature : IEquatable<RenderSignature>
    {
        private readonly ScrollAxis _axis;
        private readonly int _canvasW;
        private readonly int _canvasH;
        private readonly int _imageW;
        private readonly int _imageH;
        private readonly int _fixedTopH;
        private readonly int _fixedBottomH;
        private readonly double _srcOffset;
        private readonly double _scale;
        private readonly long _stripHash;
        private readonly int _contentVersion;
        private readonly DrawingBitmap? _fixedTop;
        private readonly DrawingBitmap? _fixedBottom;

        public RenderSignature(
            ScrollAxis axis, int canvasW, int canvasH, int imageW, int imageH,
            int fixedTopH, int fixedBottomH, double srcOffset, double scale, long stripHash,
            DrawingBitmap? fixedTop, DrawingBitmap? fixedBottom, int contentVersion)
        {
            _axis = axis;
            _canvasW = canvasW;
            _canvasH = canvasH;
            _imageW = imageW;
            _imageH = imageH;
            _fixedTopH = fixedTopH;
            _fixedBottomH = fixedBottomH;
            _srcOffset = srcOffset;
            _scale = scale;
            _stripHash = stripHash;
            _contentVersion = contentVersion;
            _fixedTop = fixedTop;
            _fixedBottom = fixedBottom;
        }

        public bool Equals(RenderSignature other)
            => _axis == other._axis
            && _canvasW == other._canvasW
            && _canvasH == other._canvasH
            && _imageW == other._imageW
            && _imageH == other._imageH
            && _fixedTopH == other._fixedTopH
            && _fixedBottomH == other._fixedBottomH
            && _srcOffset.Equals(other._srcOffset)
            && _scale.Equals(other._scale)
            && _stripHash == other._stripHash
            && _contentVersion == other._contentVersion
            && ReferenceEquals(_fixedTop, other._fixedTop)
            && ReferenceEquals(_fixedBottom, other._fixedBottom);

        public override bool Equals(object? obj)
            => obj is RenderSignature other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = (h * 31) + (int)_axis;
                h = (h * 31) + _canvasW;
                h = (h * 31) + _canvasH;
                h = (h * 31) + _imageW;
                h = (h * 31) + _imageH;
                h = (h * 31) + _fixedTopH;
                h = (h * 31) + _fixedBottomH;
                h = (h * 31) + _srcOffset.GetHashCode();
                h = (h * 31) + _scale.GetHashCode();
                h = (h * 31) + (int)(_stripHash ^ (_stripHash >> 32));
                h = (h * 31) + _contentVersion;
                return h;
            }
        }
    }

    private void PreviewLog(string message)
    {
        if (!EnablePreviewDebugLog) return;
        try
        {
            int seq = Interlocked.Increment(ref _logSeq);
            AppLogger.Info("LONG_PREVIEW_NEW #" + seq + " " + (message ?? string.Empty));
        }
        catch { }
    }

    private static void ReleasePreviewRenderFrame(PreviewRenderFrame? frame)
    {
        if (frame?.Pixels == null) return;
        try { ArrayPool<byte>.Shared.Return(frame.Pixels); } catch { }
    }

    private static string RectText(DrawingRectangle r)
    {
        return r.Left + "," + r.Top + " " + r.Width + "x" + r.Height;
    }

    private IntPtr GetWindowHwnd()
    {
        try
        {
            return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private sealed class RenderInput
    {
        public required List<StitchedStrip> Strips { get; init; }
        public ScrollAxis Axis { get; init; }
        public DrawingBitmap? FixedTopBitmap { get; init; }
        public DrawingBitmap? FixedBottomBitmap { get; init; }
        public int TotalHeight { get; init; }
        public int FrameLimitW { get; init; }
        public int FrameLimitH { get; init; }
        public bool DockRight { get; set; }
        public DrawingRectangle PlacementRegion { get; init; }
        public DrawingRectangle? AvoidRect { get; init; }
        public int PlacementDirection { get; init; }
        public int CurrentViewportY { get; init; }
        public int CurrentViewportHeight { get; init; }
        public int FixedTopHeight { get; init; }
        public int FixedBottomHeight { get; init; }
        public int ScrollDirection { get; init; }
        public bool HasViewportInfo { get; init; }
        public int ContentVersion { get; init; }
        public long RequestedAtTick { get; init; }
    }

    private sealed class PreviewRenderFrame
    {
        // OverlayOnly = true 时 Pixels 为 null：呈现路径跳过位图重建/写回/
        // GPU 上传，仅更新窗口定位与视口高亮框等叠加层。
        public bool OverlayOnly { get; init; }
        public byte[]? Pixels { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Stride { get; init; }
        public int WindowX { get; init; }
        public int WindowY { get; init; }
        public int WindowWidthPx { get; init; }
        public int WindowHeightPx { get; init; }
        public int CanvasWidthPx { get; init; }
        public int CanvasHeightPx { get; init; }
        public int ContentX { get; init; }
        public int ContentY { get; init; }
        public int ContentWidthPx { get; init; }
        public int ContentHeightPx { get; init; }
        public int ViewportBoxX { get; init; }
        public int ViewportBoxY { get; init; }
        public int ViewportBoxW { get; init; }
        public int ViewportBoxH { get; init; }
        public int ScrollDirection { get; init; }
        public bool ShowTopTail { get; init; }
        public bool ShowBottomTail { get; init; }
        public double DpiScale { get; init; }
        public ScrollAxis Axis { get; init; }
        public long RequestedAtTick { get; init; }
    }
}
