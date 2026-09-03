using System;
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
    private static readonly bool EnablePreviewDebugLog = false;
    private static readonly int PreviewSyncIntervalMs = 0;
    private const int MinPreviewWindowWidthPx = 60;
    private const int DefaultMaxWidthDip = 340;
    private const int DefaultMaxWidthDipAt100 = 320;
    private const int MaxPreviewWidthDip = 340;
    private const int GapPx = 20;
    private const int BlueBorderDip = 2;
    private const int IncrementalRedrawPaddingSourcePx = 120;

    private readonly DrawingRectangle _captureRegion;
    private readonly DrawingRectangle _virtualScreen;
    private readonly object _dataLock = new();
    private readonly object _renderWorkerLock = new();

    private List<StitchedStrip>? _pendingPreviewStrips;
    private DrawingBitmap? _pendingFixedTopBitmap;
    private DrawingBitmap? _pendingFixedBottomBitmap;
    private int _pendingTotalHeight;
    private int _currentViewportY;
    private int _currentViewportHeight;
    private int _fixedTopHeight;
    private int _fixedBottomHeight;
    private int _lastViewportYForDirection;
    private int _scrollDirection = 1;
    private bool _hasViewportInfo;
    private bool _renderWorkerRunning;
    private bool _renderWorkerQueued;
    private volatile bool _isClosing;
    private long _lastPreviewSyncTick;

    private int _frameLimitWidthPx = 300;
    private int _frameLimitHeightPx = 400;
    private bool _dockRight = true;

    // GDI cache：只在 RenderWorker 线程访问。UI 线程只接收裁剪后的像素帧。
    private DrawingBitmap? _previewCacheBitmap;
    private Graphics? _previewCacheGraphics;
    private int _previewCacheWidthPx;
    private int _previewCacheHeightPx;
    private double _previewCacheScale;
    private double _cacheSourceTop;
    private double _cacheSourceBottom;
    private int _cacheImageWidthPx;
    private int _cacheImageHeightPx;
    private int _cacheFixedTopHeight;
    private int _cacheFixedBottomHeight;

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
    private bool _hasSmoothedViewportRect;
    private double _smoothedViewportX;
    private double _smoothedViewportY;
    private double _smoothedViewportW;
    private double _smoothedViewportH;

    private int _logSeq;
    private int _updateFrameLogCount;
    private int _updateStripsLogCount;
    private int _renderLogCount;
    private int _presentLogCount;

    public LongScrollPreviewWindow()
        : this(DrawingRectangle.Empty)
    {
    }

    public LongScrollPreviewWindow(DrawingRectangle captureRegion)
    {
        _captureRegion = captureRegion.Width > 0 && captureRegion.Height > 0 ? captureRegion : new DrawingRectangle(100, 100, 320, 480);
        _virtualScreen = MonitorHelper.GetVirtualScreenRect();
        InitializeComponent();
        PreviewLog("ctor capture=" + RectText(_captureRegion) + ", virtual=" + RectText(_virtualScreen));

        Opened += (_, _) =>
        {
            try
            {
                PositionNearCaptureRegion();
                IntPtr hwnd = GetWindowHwnd();
                WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: true);
                WindowHelper.MakeHitTestTransparent(hwnd);
                WindowHelper.DisableWindowChromeEffects(hwnd);
            }
            catch { }
        };

        Closed += (_, _) => ShutdownRendering();
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
    {
        lock (_dataLock)
        {
            int previousViewportY = _currentViewportY;
            bool hadViewportInfo = _hasViewportInfo;
            _currentViewportY = currentViewportY;
            _currentViewportHeight = viewportHeight;
            _fixedTopHeight = Math.Max(0, fixedTopHeight);
            _fixedBottomHeight = Math.Max(0, fixedBottomHeight);
            if (_currentViewportHeight <= 0) _currentViewportHeight = _captureRegion.Height;
            if (totalHeight > 0 && _currentViewportY + _currentViewportHeight > totalHeight)
            {
                _currentViewportY = Math.Max(0, totalHeight - _currentViewportHeight);
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
        _dockRight = dockRight;
    }

    public void UpdateStrips(IReadOnlyList<StitchedStrip> strips, int totalHeight, DrawingBitmap? fixedTopBitmap = null, DrawingBitmap? fixedBottomBitmap = null)
    {
        if (_isClosing || strips == null || strips.Count == 0) return;

        // 预览渲染线程只需要条带的只读坐标和 Bitmap 引用。
        // 不能直接 new List<StitchedStrip>(strips)：向上滚动补顶部时采集线程会 OffsetY 旧条带，
        // 浅拷贝里的同一批 StitchedStrip 坐标会在渲染过程中被改动，预览缓存就会把同一段反复画出来。
        // 这里冻结一份轻量快照：不克隆 Bitmap，不增加大内存，只固定 Y/尺寸元数据。
        var snapshot = new List<StitchedStrip>(strips.Count);
        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip? strip = strips[i];
            if (strip == null || strip.Bitmap == null) continue;
            snapshot.Add(new StitchedStrip(strip.Bitmap, strip.Y, strip.Width, strip.Height, strip.OverlaySize));
        }
        if (snapshot.Count == 0) return;
        lock (_dataLock)
        {
            try { _pendingFixedTopBitmap?.Dispose(); } catch { }
            try { _pendingFixedBottomBitmap?.Dispose(); } catch { }
            _pendingPreviewStrips = snapshot;
            _pendingFixedTopBitmap = fixedTopBitmap;
            _pendingFixedBottomBitmap = fixedBottomBitmap;
            _pendingTotalHeight = totalHeight;
        }

        int n = Interlocked.Increment(ref _updateStripsLogCount);
        if (n <= 8 || n % 50 == 0)
        {
            StitchedStrip first = snapshot[0];
            StitchedStrip last = snapshot[^1];
            PreviewLog("UpdateStrips count=" + snapshot.Count + ", total=" + totalHeight + ", first=" + first.Width + "x" + first.Height + "@" + first.Y + ", last=" + last.Width + "x" + last.Height + "@" + last.Y);
        }

        QueueRenderWorker();
    }

    public void ShutdownRendering()
    {
        _isClosing = true;
        lock (_dataLock)
        {
            _pendingPreviewStrips = null;
        }
        lock (_uiFrameLock)
        {
            _latestFrameForUi = null;
        }
        Interlocked.Exchange(ref _uiPresentScheduled, 0);

        DisposePreviewCache();

        Dispatcher.UIThread.Post(() =>
        {
            try { PreviewImage.Source = null; } catch { }
            _writeableBitmap = null;
            _writeableBitmapWidth = 0;
            _writeableBitmapHeight = 0;
        }, DispatcherPriority.Background);
    }

    private void QueueRenderWorker()
    {
        lock (_renderWorkerLock)
        {
            if (_renderWorkerRunning)
            {
                _renderWorkerQueued = true;
                return;
            }

            _renderWorkerRunning = true;
        }

        Task.Run(RenderWorkerLoop);
    }

    private void RenderWorkerLoop()
    {
        try
        {
            while (!_isClosing)
            {
                if (PreviewSyncIntervalMs > 0)
                {
                    int delayMs = 0;
                    long now = Stopwatch.GetTimestamp();
                    long minTicks = Stopwatch.Frequency * PreviewSyncIntervalMs / 1000;
                    long dt = _lastPreviewSyncTick == 0 ? minTicks : now - _lastPreviewSyncTick;
                    if (dt >= 0 && dt < minTicks)
                    {
                        delayMs = (int)Math.Ceiling((double)(minTicks - dt) * 1000.0 / Stopwatch.Frequency);
                        if (delayMs < 1) delayMs = 1;
                        if (delayMs > PreviewSyncIntervalMs) delayMs = PreviewSyncIntervalMs;
                    }
                    if (delayMs > 0) Thread.Sleep(delayMs);
                    _lastPreviewSyncTick = Stopwatch.GetTimestamp();
                }

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
                            FixedTopBitmap = _pendingFixedTopBitmap,
                            FixedBottomBitmap = _pendingFixedBottomBitmap,
                            TotalHeight = _pendingTotalHeight,
                            FrameLimitW = _frameLimitWidthPx,
                            FrameLimitH = _frameLimitHeightPx,
                            DockRight = _dockRight,
                            CurrentViewportY = _currentViewportY,
                            CurrentViewportHeight = _currentViewportHeight > 0 ? _currentViewportHeight : _captureRegion.Height,
                            FixedTopHeight = _fixedTopHeight,
                            FixedBottomHeight = _fixedBottomHeight,
                            ScrollDirection = _scrollDirection,
                            HasViewportInfo = _hasViewportInfo,
                        };
                        _pendingPreviewStrips = null;
                        _pendingFixedTopBitmap = null;
                        _pendingFixedBottomBitmap = null;
                    }
                }

                if (input != null)
                {
                    PreviewRenderFrame? frame = RenderPreviewFrame(input);
                    if (frame != null && !_isClosing)
                    {
                        QueuePresentPreviewFrame(frame);
                    }
                }

                lock (_renderWorkerLock)
                {
                    if (_renderWorkerQueued || _pendingPreviewStrips != null)
                    {
                        _renderWorkerQueued = false;
                        continue;
                    }

                    _renderWorkerRunning = false;
                    return;
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
            }
        }
    }

    private PreviewRenderFrame? RenderPreviewFrame(RenderInput input)
    {
        try
        {
            IReadOnlyList<StitchedStrip> strips = input.Strips;
            if (strips.Count == 0) return null;

            StitchedStrip first = strips[0];
            int imageW = first.Width > 0 ? first.Width : first.Bitmap.Width;
            int middleImageH = LongScrollComposer.CalculateCompositedHeight(strips, input.TotalHeight);
            int fixedTopForPreview = Math.Max(0, input.FixedTopHeight);
            int fixedBottomForPreview = Math.Max(0, input.FixedBottomHeight);
            int imageH = middleImageH + fixedTopForPreview + fixedBottomForPreview;
            if (imageW <= 0 || middleImageH <= 0 || imageH <= 0) return null;

            double dpiScale = GetPreviewDpiScale();
            DrawingRectangle monitor = GetCaptureMonitorRect();
            int sideAvailableW = input.DockRight
                ? monitor.Right - (_captureRegion.Right + GapPx)
                : (_captureRegion.Left - GapPx) - monitor.Left;
            if (sideAvailableW < MinPreviewWindowWidthPx)
            {
                int rightW = monitor.Right - (_captureRegion.Right + GapPx);
                int leftW = (_captureRegion.Left - GapPx) - monitor.Left;
                input.DockRight = rightW >= leftW;
                sideAvailableW = Math.Max(rightW, leftW);
            }

            int defaultMaxW = (int)Math.Round((dpiScale <= 1.01 ? DefaultMaxWidthDipAt100 : DefaultMaxWidthDip) * dpiScale);
            int maxByDip = (int)Math.Round(MaxPreviewWidthDip * dpiScale);
            int fixedWindowW = Math.Min(Math.Min(defaultMaxW, maxByDip), Math.Max(MinPreviewWindowWidthPx, sideAvailableW));
            if (input.FrameLimitW > 0) fixedWindowW = Math.Min(fixedWindowW, Math.Max(MinPreviewWindowWidthPx, input.FrameLimitW));
            if (fixedWindowW < MinPreviewWindowWidthPx) fixedWindowW = MinPreviewWindowWidthPx;

            int borderPx = Math.Max(2, (int)Math.Round(BlueBorderDip * 2 * dpiScale));
            int contentW = Math.Max(1, fixedWindowW - borderPx);
            double scale = (double)contentW / imageW;
            if (scale > 1.0) scale = 1.0;
            if (scale <= 0.000001) scale = 0.000001;
            int scaledContentW = Math.Max(1, (int)Math.Round(imageW * scale));
            int windowW = Math.Max(scaledContentW + borderPx, MinPreviewWindowWidthPx);
            int canvasW = Math.Max(1, windowW - borderPx);

            int previewBottom = _captureRegion.Bottom;
            int screenTop = monitor.Top;
            int maxVisibleWindowH = previewBottom - screenTop;
            if (maxVisibleWindowH <= borderPx + 1) return null;

            int contentH = Math.Max(1, (int)Math.Ceiling(imageH * scale));
            int visibleCanvasH = Math.Min(contentH, Math.Max(1, maxVisibleWindowH - borderPx));
            int contentWindowH = visibleCanvasH + borderPx;
            if (contentWindowH < 1) contentWindowH = 1;
            if (contentWindowH > maxVisibleWindowH) contentWindowH = maxVisibleWindowH;
            visibleCanvasH = Math.Max(1, contentWindowH - borderPx);

            int hostWindowH = maxVisibleWindowH;
            int hostWindowW = windowW;
            int contentX = 0;
            int contentY = Math.Max(0, hostWindowH - contentWindowH);

            int windowX = input.DockRight ? _captureRegion.Right + GapPx : _captureRegion.Left - GapPx - hostWindowW;
            windowX = Math.Clamp(windowX, monitor.Left + GapPx, monitor.Right - hostWindowW - GapPx);
            int windowY = previewBottom - hostWindowH;
            if (windowY < monitor.Top) windowY = monitor.Top;

            double visibleSourceTop;
            double visibleSourceBottom;
            double visibleSourceSpan = visibleCanvasH / scale;
            if (visibleSourceSpan >= imageH)
            {
                visibleSourceTop = 0;
                visibleSourceBottom = imageH;
            }
            else if (input.HasViewportInfo)
            {
                GetDisplayViewport(input, out double viewportTop, out double viewportHeight);
                double viewportBottom = viewportTop + viewportHeight;
                double viewportCenter = (viewportTop + viewportBottom) * 0.5;
                double maxTop = Math.Max(0, imageH - visibleSourceSpan);
                // 预览窗以当前完整截图框的中心为锚点，而不是按方向强行吸到顶部/底部。
                // 这样用户轻轻滚一下，绿色框也只是轻轻移动；只有接近真实边界时才自然贴边。
                visibleSourceTop = Math.Clamp(viewportCenter - visibleSourceSpan * 0.5, 0, maxTop);
                visibleSourceBottom = visibleSourceTop + visibleSourceSpan;
            }
            else
            {
                // 没有视口信息时保留旧策略：显示长图底部末端。
                visibleSourceBottom = imageH;
                visibleSourceTop = imageH - visibleSourceSpan;
            }
            if (visibleSourceTop < 0) visibleSourceTop = 0;
            if (visibleSourceBottom > imageH) visibleSourceBottom = imageH;
            if (visibleSourceBottom < visibleSourceTop) visibleSourceBottom = visibleSourceTop;

            bool forceRebuildForTopDirection = input.ScrollDirection < 0;
            bool rebuilt = forceRebuildForTopDirection || !TryRenderIncremental(strips, imageW, imageH, canvasW, visibleCanvasH, scale, visibleSourceTop, visibleSourceBottom, fixedTopForPreview, fixedBottomForPreview, input.FixedTopBitmap, input.FixedBottomBitmap);
            if (rebuilt)
            {
                RebuildVisibleCache(strips, imageW, imageH, canvasW, visibleCanvasH, scale, visibleSourceTop, visibleSourceBottom, fixedTopForPreview, fixedBottomForPreview, input.FixedTopBitmap, input.FixedBottomBitmap);
            }

            try { input.FixedTopBitmap?.Dispose(); } catch { }
            try { input.FixedBottomBitmap?.Dispose(); } catch { }

            if (_previewCacheBitmap == null) return null;
            byte[] pixels = CopyBitmapPixels(_previewCacheBitmap, out int stride);
            CalculateViewportBox(input, visibleSourceTop, visibleSourceBottom, scale, canvasW, visibleCanvasH, out int boxX, out int boxY, out int boxW, out int boxH);

            int logN = Interlocked.Increment(ref _renderLogCount);
            if (logN <= 12 || logN % 50 == 0)
            {
                PreviewLog("RenderFrame visible=" + canvasW + "x" + visibleCanvasH + ", image=" + imageW + "x" + imageH + ", scale=" + scale.ToString("0.0000") + ", src=" + visibleSourceTop.ToString("0.0") + ".." + visibleSourceBottom.ToString("0.0") + ", rebuilt=" + rebuilt + ", host=" + windowX + "," + windowY + " " + hostWindowW + "x" + hostWindowH + ", content=" + contentX + "," + contentY + " " + windowW + "x" + contentWindowH);
            }

            return new PreviewRenderFrame
            {
                Pixels = pixels,
                Width = canvasW,
                Height = visibleCanvasH,
                Stride = stride,
                WindowX = windowX,
                WindowY = windowY,
                WindowWidthPx = hostWindowW,
                WindowHeightPx = hostWindowH,
                CanvasWidthPx = hostWindowW,
                CanvasHeightPx = hostWindowH,
                ContentX = contentX,
                ContentY = contentY,
                ContentWidthPx = windowW,
                ContentHeightPx = contentWindowH,
                ViewportBoxX = boxX,
                ViewportBoxY = boxY,
                ViewportBoxW = boxW,
                ViewportBoxH = boxH,
                ScrollDirection = input.ScrollDirection,
                ShowTopTail = visibleSourceTop > 0.5,
                ShowBottomTail = visibleSourceBottom < imageH - 0.5,
                DpiScale = dpiScale,
            };
        }
        catch
        {
            return null;
        }
    }

    private bool TryRenderIncremental(IReadOnlyList<StitchedStrip> strips, int imageW, int imageH, int canvasW, int canvasH, double scale, double visibleSourceTop, double visibleSourceBottom, int fixedTopHeight, int fixedBottomHeight, DrawingBitmap? fixedTopBitmap, DrawingBitmap? fixedBottomBitmap)
    {
        if (_previewCacheBitmap == null || _previewCacheGraphics == null) return false;
        if (_previewCacheWidthPx != canvasW || _previewCacheHeightPx != canvasH) return false;
        if (_cacheImageWidthPx != imageW || _cacheImageHeightPx > imageH) return false;
        if (_cacheFixedTopHeight != fixedTopHeight || _cacheFixedBottomHeight != fixedBottomHeight) return false;
        // 固定顶/底层存在时，整张缓存上滚会把固定层像素带进中间内容区。
        // 为了预览准确，固定层场景只重建当前可见区域；预览尺寸很小，仍然轻量。
        if (fixedTopHeight > 0 || fixedBottomHeight > 0) return false;
        if (Math.Abs(_previewCacheScale - scale) > 0.000001) return false;
        if (visibleSourceTop < _cacheSourceTop - 0.5 || visibleSourceBottom < _cacheSourceBottom - 0.5) return false;

        double sourceDelta = visibleSourceTop - _cacheSourceTop;
        int scrollPx = (int)Math.Round(sourceDelta * scale);
        if (scrollPx < 0 || scrollPx >= canvasH) return false;

        if (scrollPx > 0)
        {
            if (!ScrollBitmapUpInPlace(_previewCacheBitmap, scrollPx, Color.White)) return false;
        }

        double redrawTop = Math.Max(visibleSourceTop, _cacheSourceBottom - IncrementalRedrawPaddingSourcePx);
        DrawVisibleStrips(strips, _previewCacheGraphics, imageW, canvasW, canvasH, scale, visibleSourceTop, visibleSourceBottom, redrawTop, visibleSourceBottom, fixedTopHeight);
        DrawFixedLayerPreview(fixedTopBitmap, fixedBottomBitmap, _previewCacheGraphics, imageW, imageH, canvasW, canvasH, scale, visibleSourceTop, visibleSourceBottom, fixedTopHeight, fixedBottomHeight);
        _cacheSourceTop = visibleSourceTop;
        _cacheSourceBottom = visibleSourceBottom;
        _cacheImageHeightPx = imageH;
        return true;
    }

    private static bool ScrollBitmapUpInPlace(DrawingBitmap bitmap, int scrollPx, Color clearColor)
    {
        if (bitmap == null || scrollPx <= 0) return true;
        if (scrollPx >= bitmap.Height) return false;

        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            int stride = data.Stride;
            int absStride = Math.Abs(stride);
            int height = bitmap.Height;
            int moveRows = height - scrollPx;
            int bytesPerRow = Math.Min(absStride, bitmap.Width * 4);
            byte a = clearColor.A;
            byte r = clearColor.R;
            byte g = clearColor.G;
            byte b = clearColor.B;

            IntPtr basePtr = data.Scan0;
            if (stride < 0) basePtr = IntPtr.Add(basePtr, (height - 1) * stride);

            for (int y = 0; y < moveRows; y++)
            {
                IntPtr dst = IntPtr.Add(basePtr, y * absStride);
                IntPtr src = IntPtr.Add(basePtr, (y + scrollPx) * absStride);
                NativeMethods.CopyMemory(dst, src, (uint)bytesPerRow);
            }

            byte[] clearRow = new byte[bytesPerRow];
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = x * 4;
                clearRow[offset] = b;
                clearRow[offset + 1] = g;
                clearRow[offset + 2] = r;
                clearRow[offset + 3] = a;
            }

            for (int y = moveRows; y < height; y++)
            {
                IntPtr row = IntPtr.Add(basePtr, y * absStride);
                Marshal.Copy(clearRow, 0, row, clearRow.Length);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (data != null) bitmap.UnlockBits(data);
            }
            catch { }
        }
    }

    private void RebuildVisibleCache(IReadOnlyList<StitchedStrip> strips, int imageW, int imageH, int canvasW, int canvasH, double scale, double visibleSourceTop, double visibleSourceBottom, int fixedTopHeight, int fixedBottomHeight, DrawingBitmap? fixedTopBitmap, DrawingBitmap? fixedBottomBitmap)
    {
        EnsurePreviewCache(canvasW, canvasH);
        if (_previewCacheGraphics == null) return;
        _previewCacheGraphics.Clear(Color.White);
        DrawVisibleStrips(strips, _previewCacheGraphics, imageW, canvasW, canvasH, scale, visibleSourceTop, visibleSourceBottom, visibleSourceTop, visibleSourceBottom, fixedTopHeight);
        DrawFixedLayerPreview(fixedTopBitmap, fixedBottomBitmap, _previewCacheGraphics, imageW, imageH, canvasW, canvasH, scale, visibleSourceTop, visibleSourceBottom, fixedTopHeight, fixedBottomHeight);
        _previewCacheScale = scale;
        _cacheSourceTop = visibleSourceTop;
        _cacheSourceBottom = visibleSourceBottom;
        _cacheImageWidthPx = imageW;
        _cacheImageHeightPx = imageH;
        _cacheFixedTopHeight = fixedTopHeight;
        _cacheFixedBottomHeight = fixedBottomHeight;
    }

    private static void DrawFixedLayerPreview(DrawingBitmap? fixedTopBitmap, DrawingBitmap? fixedBottomBitmap, Graphics g, int imageW, int imageH, int canvasW, int canvasH, double scale, double visibleSourceTop, double visibleSourceBottom, int fixedTopHeight, int fixedBottomHeight)
    {
        if (g == null || imageW <= 0 || imageH <= 0 || canvasW <= 0 || canvasH <= 0) return;
        int destW = Math.Max(1, Math.Min(canvasW, (int)Math.Round(imageW * scale)));
        int destX = Math.Max(0, (canvasW - destW) / 2);

        if (fixedTopBitmap != null && fixedTopHeight > 0)
        {
            DrawFixedLayerBitmap(fixedTopBitmap, g, destX, destW, canvasH, scale, visibleSourceTop, visibleSourceBottom, 0, fixedTopHeight);
        }

        if (fixedBottomBitmap != null && fixedBottomHeight > 0)
        {
            int layerTop = Math.Max(0, imageH - fixedBottomHeight);
            DrawFixedLayerBitmap(fixedBottomBitmap, g, destX, destW, canvasH, scale, visibleSourceTop, visibleSourceBottom, layerTop, fixedBottomHeight);
        }
    }

    private static void DrawFixedLayerBitmap(DrawingBitmap bitmap, Graphics g, int destX, int destW, int canvasH, double scale, double visibleSourceTop, double visibleSourceBottom, int layerTop, int layerHeight)
    {
        if (bitmap == null || g == null || layerHeight <= 0 || scale <= 0) return;
        double layerBottom = layerTop + layerHeight;
        double overlapTop = Math.Max(layerTop, visibleSourceTop);
        double overlapBottom = Math.Min(layerBottom, visibleSourceBottom);
        if (overlapBottom <= overlapTop) return;

        int srcY = Math.Max(0, (int)Math.Floor(overlapTop - layerTop));
        int srcH = Math.Min(bitmap.Height - srcY, Math.Max(1, (int)Math.Ceiling(overlapBottom - overlapTop)));
        if (srcH <= 0) return;

        double destYDouble = (overlapTop - visibleSourceTop) * scale;
        double destHDouble = srcH * scale;
        int destY = (int)Math.Floor(destYDouble);
        int destH = Math.Max(1, (int)Math.Ceiling(destYDouble + destHDouble) - destY);
        if (destY >= canvasH || destY + destH <= 0) return;
        if (destY < 0)
        {
            int cut = -destY;
            int cutSource = (int)Math.Floor(cut / scale);
            srcY += cutSource;
            srcH -= cutSource;
            destH -= cut;
            destY = 0;
        }
        if (destY + destH > canvasH) destH = canvasH - destY;
        if (srcH <= 0 || destH <= 0) return;

        int srcW = Math.Min(bitmap.Width, Math.Max(1, destW > 0 ? bitmap.Width : bitmap.Width));
        try
        {
            g.DrawImage(bitmap, new DrawingRectangle(destX, destY, destW, destH), new DrawingRectangle(0, srcY, srcW, srcH), GraphicsUnit.Pixel);
        }
        catch { }
    }

    private void DrawVisibleStrips(IReadOnlyList<StitchedStrip> strips, Graphics g, int imageW, int canvasW, int canvasH, double scale, double visibleSourceTop, double visibleSourceBottom, double drawSourceTop, double drawSourceBottom, int fixedTopHeight)
    {
        int destW = Math.Max(1, Math.Min(canvasW, (int)Math.Round(imageW * scale)));
        int destX = Math.Max(0, (canvasW - destW) / 2);
        double contentOffsetY = Math.Max(0, fixedTopHeight);
        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip? strip = strips[i];
            if (strip == null || strip.Bitmap == null) continue;
            int stripW = Math.Min(imageW, Math.Min(strip.Width, strip.Bitmap.Width));
            int stripH = Math.Min(strip.Height, strip.Bitmap.Height);
            if (stripW <= 0 || stripH <= 0) continue;

            double stripTop = strip.Y + contentOffsetY;
            double stripBottom = stripTop + stripH;
            double overlapTop = Math.Max(Math.Max(stripTop, visibleSourceTop), drawSourceTop);
            double overlapBottom = Math.Min(Math.Min(stripBottom, visibleSourceBottom), drawSourceBottom);
            if (overlapBottom <= overlapTop) continue;

            int srcY = Math.Max(0, (int)Math.Floor(overlapTop - stripTop));
            int srcH = Math.Min(stripH - srcY, Math.Max(1, (int)Math.Ceiling(overlapBottom - overlapTop)));
            if (srcH <= 0) continue;

            double destYDouble = (overlapTop - visibleSourceTop) * scale;
            double destHDouble = srcH * scale;
            int destY = (int)Math.Floor(destYDouble);
            int destH = Math.Max(1, (int)Math.Ceiling(destYDouble + destHDouble) - destY);
            if (destY >= canvasH || destY + destH <= 0) continue;
            if (destY < 0)
            {
                int cut = -destY;
                double cutSource = cut / scale;
                srcY += (int)Math.Floor(cutSource);
                srcH -= (int)Math.Floor(cutSource);
                destH -= cut;
                destY = 0;
            }
            if (destY + destH > canvasH) destH = canvasH - destY;
            if (srcH <= 0 || destH <= 0) continue;

            try
            {
                g.DrawImage(strip.Bitmap, new DrawingRectangle(destX, destY, destW, destH), new DrawingRectangle(0, srcY, stripW, srcH), GraphicsUnit.Pixel);
            }
            catch { }
        }
    }

    private void EnsurePreviewCache(int width, int height)
    {
        if (_previewCacheBitmap != null && _previewCacheGraphics != null && _previewCacheWidthPx == width && _previewCacheHeightPx == height) return;
        DisposePreviewCache();
        _previewCacheBitmap = new DrawingBitmap(width, height, PixelFormat.Format32bppPArgb);
        _previewCacheGraphics = Graphics.FromImage(_previewCacheBitmap);
        _previewCacheGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        _previewCacheGraphics.SmoothingMode = SmoothingMode.None;
        _previewCacheGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        _previewCacheGraphics.InterpolationMode = InterpolationMode.Low;
        _previewCacheWidthPx = width;
        _previewCacheHeightPx = height;
        _previewCacheScale = 0;
        _cacheSourceTop = 0;
        _cacheSourceBottom = 0;
        _cacheImageWidthPx = 0;
        _cacheImageHeightPx = 0;
        _cacheFixedTopHeight = 0;
        _cacheFixedBottomHeight = 0;
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

    private static void GetDisplayViewport(RenderInput input, out double top, out double height)
    {
        top = input.HasViewportInfo ? input.CurrentViewportY : 0;
        height = input.CurrentViewportHeight > 0 ? input.CurrentViewportHeight : 1;
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
        boxY = (int)Math.Round((clippedTop - visibleSourceTop) * scale);
        boxH = Math.Max(1, (int)Math.Round((clippedBottom - clippedTop) * scale));
        if (boxY < 0) boxY = 0;
        if (boxY >= canvasH) boxY = canvasH - 1;
        if (boxY + boxH > canvasH) boxH = canvasH - boxY;
        if (boxH < 1) boxH = 1;
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
            byte[] pixels = new byte[required];
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
        if (_isClosing || frame == null) return;
        lock (_uiFrameLock)
        {
            _latestFrameForUi = frame;
        }

        if (Interlocked.Exchange(ref _uiPresentScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(DrainLatestPreviewFrameOnUi, DispatcherPriority.Render);
    }

    private void DrainLatestPreviewFrameOnUi()
    {
        try
        {
            while (!_isClosing)
            {
                PreviewRenderFrame? frame;
                lock (_uiFrameLock)
                {
                    frame = _latestFrameForUi;
                    _latestFrameForUi = null;
                }

                if (frame == null) break;
                PresentPreviewFrame(frame);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _uiPresentScheduled, 0);
            lock (_uiFrameLock)
            {
                if (_latestFrameForUi != null && !_isClosing && Interlocked.Exchange(ref _uiPresentScheduled, 1) == 0)
                {
                    Dispatcher.UIThread.Post(DrainLatestPreviewFrameOnUi, DispatcherPriority.Render);
                }
            }
        }
    }

    private void PresentPreviewFrame(PreviewRenderFrame frame)
    {
        if (_isClosing || frame.Width <= 0 || frame.Height <= 0) return;

        try
        {
            double dpiScale = frame.DpiScale > 0 ? frame.DpiScale : 1.0;
            bool sourceChanged = false;
            if (_writeableBitmap == null || _writeableBitmapWidth != frame.Width || _writeableBitmapHeight != frame.Height)
            {
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
                for (int y = 0; y < frame.Height; y++)
                {
                    if (y < 0 || y >= fb.Size.Height) continue;
                    IntPtr dst = IntPtr.Add(fb.Address, y * fb.RowBytes);
                    Marshal.Copy(frame.Pixels, y * frame.Stride, dst, copyBytes);
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

            // 蓝框使用内容外框尺寸；图片内容区从蓝框内侧 borderDip 开始。
            // 注意：Avalonia 控件初始 Width/Height 是 NaN，不能直接 Math.Abs(NaN - x) 比较，
            // 否则第一次不会赋值，表现就是蓝色描边完全不显示。
            // 预览窗外框保持在内容框边界；当前视口框与其重叠时由绿色覆盖蓝色。
            double blueBorderInset = borderDip / 2.0;
            double blueBorderX = contentXDip + blueBorderInset;
            double blueBorderY = contentYDip + blueBorderInset;
            double blueBorderW = Math.Max(1.0, contentWidthDip - borderDip);
            double blueBorderH = Math.Max(1.0, contentHeightDip - borderDip);
            Avalonia.Controls.Canvas.SetLeft(OuterBlueBorder, blueBorderX);
            Avalonia.Controls.Canvas.SetTop(OuterBlueBorder, blueBorderY);
            if (double.IsNaN(OuterBlueBorder.Width) || Math.Abs(OuterBlueBorder.Width - blueBorderW) > 0.001) OuterBlueBorder.Width = blueBorderW;
            if (double.IsNaN(OuterBlueBorder.Height) || Math.Abs(OuterBlueBorder.Height - blueBorderH) > 0.001) OuterBlueBorder.Height = blueBorderH;

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
        if (reset || !_hasSmoothedViewportRect)
        {
            _smoothedViewportX = x;
            _smoothedViewportY = y;
            _smoothedViewportW = w;
            _smoothedViewportH = h;
            _hasSmoothedViewportRect = true;
            return;
        }

        double dy = Math.Abs(y - _smoothedViewportY);
        double dh = Math.Abs(h - _smoothedViewportH);
        double factor = dy > Math.Max(80.0, h * 0.45) ? 0.42 : 0.55;
        _smoothedViewportX += (x - _smoothedViewportX) * factor;
        _smoothedViewportY += (y - _smoothedViewportY) * factor;
        _smoothedViewportW += (w - _smoothedViewportW) * Math.Min(0.7, factor + 0.1);
        _smoothedViewportH += (h - _smoothedViewportH) * (dh > h * 0.25 ? 0.65 : 0.8);

        if (Math.Abs(x - _smoothedViewportX) < 0.35) _smoothedViewportX = x;
        if (Math.Abs(y - _smoothedViewportY) < 0.35) _smoothedViewportY = y;
        if (Math.Abs(w - _smoothedViewportW) < 0.35) _smoothedViewportW = w;
        if (Math.Abs(h - _smoothedViewportH) < 0.35) _smoothedViewportH = h;

        x = _smoothedViewportX;
        y = _smoothedViewportY;
        w = Math.Max(1.0, _smoothedViewportW);
        h = Math.Max(1.0, _smoothedViewportH);
    }

    private static void ClearFramebufferRect(ILockedFramebuffer fb, int x, int y, int width, int height)
    {
        if (fb == null || width <= 0 || height <= 0) return;
        int startX = Math.Max(0, x);
        int startY = Math.Max(0, y);
        int endX = Math.Min(fb.Size.Width, x + width);
        int endY = Math.Min(fb.Size.Height, y + height);
        if (endX <= startX || endY <= startY) return;

        int bytes = (endX - startX) * 4;
        byte[] clear = new byte[bytes];
        for (int row = startY; row < endY; row++)
        {
            IntPtr dst = IntPtr.Add(fb.Address, row * fb.RowBytes + startX * 4);
            Marshal.Copy(clear, 0, dst, bytes);
        }
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

    private static void SetMaskRect(Control mask, double x, double y, double width, double height)
    {
        Avalonia.Controls.Canvas.SetLeft(mask, x);
        Avalonia.Controls.Canvas.SetTop(mask, y);
        mask.Width = Math.Max(0.0, width);
        mask.Height = Math.Max(0.0, height);
        mask.IsVisible = width > 0.5 && height > 0.5;
    }

    private void UpdateTailCue(PreviewRenderFrame frame, double dpiScale, double contentX, double contentY, double contentW, double contentH)
    {
        try
        {
            if (contentW < 1) contentW = 1;
            if (contentH < 1) contentH = 1;
            bool showTop = frame.ShowTopTail;
            bool showBottom = frame.ShowBottomTail;
            double shadowH = Math.Min(42.0, Math.Max(18.0, contentH * 0.18));
            double arrowW = 32.0;
            double arrowH = 28.0;
            double arrowX = contentX + Math.Max(0, (contentW - arrowW) / 2.0);

            TopTailShadow.Opacity = showTop ? 1.0 : 0.0;
            BottomTailShadow.Opacity = showBottom ? 1.0 : 0.0;
            TopTailArrow.Opacity = showTop ? 0.90 : 0.0;
            BottomTailArrow.Opacity = showBottom ? 0.90 : 0.0;

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
            double scale = GetPreviewDpiScale();
            int widthPx = Math.Max(MinPreviewWindowWidthPx, (int)Math.Round(DefaultMaxWidthDip * scale));
            int heightPx = Math.Max(1, Math.Min(_captureRegion.Height, _captureRegion.Bottom - GetCaptureMonitorRect().Top));
            int x = _dockRight ? _captureRegion.Right + GapPx : _captureRegion.Left - GapPx - widthPx;
            int y = _captureRegion.Bottom - heightPx;
            DrawingRectangle monitor = GetCaptureMonitorRect();
            x = Math.Clamp(x, monitor.Left + GapPx, monitor.Right - widthPx - GapPx);
            if (y < monitor.Top) y = monitor.Top;
            Position = new PixelPoint(x, y);
        }
        catch { }
    }

    private DrawingRectangle GetCaptureMonitorRect()
    {
        try
        {
            var monitors = MonitorHelper.GetMonitorRects();
            int cx = _captureRegion.Left + _captureRegion.Width / 2;
            int cy = _captureRegion.Top + _captureRegion.Height / 2;
            foreach (DrawingRectangle monitor in monitors)
            {
                if (monitor.Contains(cx, cy)) return monitor;
            }

            foreach (DrawingRectangle monitor in monitors)
            {
                if (monitor.IntersectsWith(_captureRegion)) return monitor;
            }
        }
        catch { }

        return _virtualScreen;
    }

    private double GetPreviewDpiScale()
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
        public DrawingBitmap? FixedTopBitmap { get; init; }
        public DrawingBitmap? FixedBottomBitmap { get; init; }
        public int TotalHeight { get; init; }
        public int FrameLimitW { get; init; }
        public int FrameLimitH { get; init; }
        public bool DockRight { get; set; }
        public int CurrentViewportY { get; init; }
        public int CurrentViewportHeight { get; init; }
        public int FixedTopHeight { get; init; }
        public int FixedBottomHeight { get; init; }
        public int ScrollDirection { get; init; }
        public bool HasViewportInfo { get; init; }
    }

    private sealed class PreviewRenderFrame
    {
        public required byte[] Pixels { get; init; }
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
    }
}
