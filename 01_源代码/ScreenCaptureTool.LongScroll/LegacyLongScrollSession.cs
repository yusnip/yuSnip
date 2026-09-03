using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.LongScroll.Diagnostics;
using ScreenCaptureTool.LongScroll.Stitching;
using ScreenCaptureTool.LongScroll.Windows;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 旧版 LongScrollCapture.cs 的直移植会话路径。
/// UI 适配到新版 Avalonia LongScrollControlWindow；选区边框使用原生轻量 Overlay。
/// </summary>
public sealed class LegacyLongScrollSession
{
    private static readonly bool EnablePreviewWindow = true;
    private static readonly bool EnableBorderWindow = true;
    private static readonly bool EnableGlobalMouseHook = true;
    private static readonly bool EnableAutoScroll = true;
    private const int SafeCaptureIntervalMs = 16;

    private readonly LongScrollOptions _options;
    private readonly AppSettings _settings;
    private readonly Rectangle _region;
    private readonly List<StitchedStrip> _strips = new();
    private readonly List<Bitmap> _retiredStripBitmaps = new();
    private readonly object _stripsLock = new();

    private volatile int _totalHeight;
    private volatile bool _maxHeightReached;
    private int _currentViewportY;
    private int _canvasTopY;
    private int _canvasBottomY;
    private int _viewportHeight;
    private int _captureDirection;
    private StitchedStrip? _baseStrip;
    private int _fixedTopHeight;
    private int _fixedBottomHeight;
    private Bitmap? _topBoundaryFixedBitmap;
    private Bitmap? _bottomBoundaryFixedBitmap;
    private volatile string _lastCanvasAction = "初始化";

    private BlockingCollection<Bitmap>? _captureQueue;
    private ConcurrentBag<Bitmap>? _bitmapPool;
    private CancellationTokenSource? _cts;
    private Task? _stitchTask;
    private Thread? _captureThread;

    private LongScrollControlWindow? _controlWindow;
    private NativeLongScrollOverlay? _nativeOverlay;
    private LongScrollPreviewWindow? _previewWindow;
    private TaskCompletionSource<LongScrollResult>? _completionTcs;

    private bool _hasLastControlWindowRect;
    private int _lastControlWindowX;
    private int _lastControlWindowY;
    private int _lastControlWindowW;
    private int _lastControlWindowH;

    private IntPtr _hMemDC = IntPtr.Zero;
    private IntPtr _hBitmap = IntPtr.Zero;
    private IntPtr _hOldBitmap = IntPtr.Zero;
    private IntPtr _pBits = IntPtr.Zero;

    private bool _isRunning;
    private int _finishStarted;
    private int _capturedFrameCount;
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private int _lastWheelDir;
    private int _wheelLogCount;
    private int _consecutiveIdleFrames;
    private long _lastStatusUpdateTick;
    private const int StatusUpdateIntervalMs = 120;

    private System.Threading.Timer? _autoScrollTimer;
    private volatile bool _autoScrollEnabled;
    private int _autoScrollIntervalMs = 160;
    private int _autoScrollTickRunning;

    public LegacyLongScrollSession(LongScrollOptions options, AppSettings? settings = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _settings = settings ?? AppSettings.Load();
        _region = _options.CaptureRegion;
    }

    public async Task<LongScrollResult> RunAsync()
    {
        if (!_options.IsValid)
        {
            return LongScrollResult.Cancel("长截图区域无效。");
        }

        AppLogger.Info("LegacyLongScrollSession.RunAsync: 开始。Region=" + _region + ", Preview=" + EnablePreviewWindow + ", Border=" + EnableBorderWindow + ", MouseHook=" + EnableGlobalMouseHook + ", AutoScroll=" + EnableAutoScroll + ", SafeInterval=" + SafeCaptureIntervalMs + "ms");
        _completionTcs = new TaskCompletionSource<LongScrollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureQueue = new BlockingCollection<Bitmap>(3);
        _bitmapPool = new ConcurrentBag<Bitmap>();
        _cts = new CancellationTokenSource();

        for (int i = 0; i < 4; i++)
        {
            _bitmapPool.Add(new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb));
        }

        LongScrollMemoryDiagnostics.LogImage("LongScroll.Start", _region.Width, 0);

        try
        {
            _controlWindow = new LongScrollControlWindow
            {
                RequestedThemeVariant = _options.IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            _controlWindow.ApplyLongScrollTheme(_options.IsDarkMode);
            _controlWindow.SetRegionInfo(_region);
            _controlWindow.SetStatus(string.Format("{0} × {1}px", _region.Width, GetDisplayHeight()));
            _controlWindow.UpdateMetrics(GetDisplayHeight(), 0);
            _controlWindow.SetAutoScrollIntervalMs(_autoScrollIntervalMs);

            _controlWindow.FinishRequested += async (_, _) => await FinishInternalAsync(true, null, false);
            _controlWindow.CancelRequested += async (_, _) => await FinishInternalAsync(false, null, true);
            _controlWindow.SaveAsRequested += async (_, _) =>
            {
                if (Volatile.Read(ref _finishStarted) != 0) return;
                string? filePath = await PickSaveAsPathAsync(_controlWindow);
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    _controlWindow.SetStatus(string.Format("{0} × {1}px", _region.Width, GetDisplayHeight()));
                    return;
                }

                await FinishInternalAsync(false, filePath, false);
            };
            _controlWindow.AutoScrollRequested += (_, _) => OnAutoScrollClicked();
            _controlWindow.AutoScrollIntervalWheel += (_, delta) => OnAutoScrollIntervalWheel(delta);
            _controlWindow.Opened += (_, _) =>
            {
                try
                {
                    PlaceControlWindow();
                    Dispatcher.UIThread.Post(PlaceControlWindow, DispatcherPriority.Loaded);
                    Dispatcher.UIThread.Post(PlaceControlWindow, DispatcherPriority.Render);
                    WindowHelper.MakeToolWindowNoActivate(GetWindowHwnd(_controlWindow), transparent: false);
                }
                catch { }
            };
            _controlWindow.LayoutUpdated += (_, _) => PlaceControlWindow();
            _controlWindow.Closed += async (_, _) =>
            {
                if (Volatile.Read(ref _finishStarted) == 0)
                {
                    await FinishInternalAsync(false, null, true);
                }
            };

            if (EnableBorderWindow)
            {
                _nativeOverlay = new NativeLongScrollOverlay(_region);
            }
            else
            {
                _nativeOverlay = null;
                AppLogger.Warn("LegacyLongScrollSession: 安全模式已禁用长截图遮罩/边框窗口，避免阻塞系统鼠标输入。");
            }

            if (EnablePreviewWindow)
            {
                _previewWindow = new LongScrollPreviewWindow(_region);
            }
            else
            {
                _previewWindow = null;
            }

            SeedInitialFrame();

            _isRunning = true;
            if (EnableGlobalMouseHook)
            {
                InstallMouseHook();
            }
            else
            {
                AppLogger.Warn("LegacyLongScrollSession: 安全模式已禁用全局低级鼠标钩子。滚轮方向辅助暂不可用。");
            }

            _stitchTask = Task.Factory.StartNew(
                StitchWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _captureThread = new Thread(CaptureLoop)
            {
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true,
                Name = "LegacyLongScrollCaptureLoop",
            };
            AppLogger.Info("LegacyLongScrollSession.RunAsync: 启动采集线程。Priority=" + _captureThread.Priority + ", TargetInterval>= " + SafeCaptureIntervalMs + "ms");
            _captureThread.Start();

            UpdateStatus();

            if (_nativeOverlay != null)
            {
                _nativeOverlay.Show();
                AppLogger.Info("LegacyLongScrollSession.RunAsync: Native overlay shown.");
            }
            else
            {
                AppLogger.Info("LegacyLongScrollSession.RunAsync: Native overlay disabled in safe mode.");
            }
            if (_previewWindow != null)
            {
                _previewWindow.Show();
                AppLogger.Info("LegacyLongScrollSession.RunAsync: PreviewWindow shown.");
            }
            _controlWindow.Show();
            WindowHelper.MakeToolWindowNoActivate(GetWindowHwnd(_controlWindow), transparent: false);
            ApplyLongScrollWindowZOrder();
            AppLogger.Info("LegacyLongScrollSession.RunAsync: ControlWindow shown no-activate applied; Z-order normalized.");

            LongScrollResult result = await _completionTcs.Task;
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LegacyLongScrollSession.RunAsync 异常", ex);
            await CleanupAfterFinishAsync();
            return LongScrollResult.Cancel("长截图失败：" + ex.Message);
        }
    }

    private void ApplyLongScrollWindowZOrder()
    {
        try { _nativeOverlay?.BringToTop(); } catch { }
        try { WindowHelper.ForceTopmost(GetWindowHwnd(_previewWindow)); } catch { }
        try { WindowHelper.ForceTopmost(GetWindowHwnd(_controlWindow)); } catch { }
    }

    private void SeedInitialFrame()
    {
        try
        {
            if (_captureQueue == null) return;
            using var bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size);
            }

            _captureQueue.TryAdd((Bitmap)bmp.Clone());
        }
        catch { }
    }

    private void OnAutoScrollClicked()
    {
        if (!EnableAutoScroll)
        {
            try { _controlWindow?.SetStatus(string.Format("{0} × {1}px", _region.Width, GetDisplayHeight())); } catch { }
            AppLogger.Warn("LegacyLongScrollSession: 安全模式已禁用自动滚动。");
            return;
        }

        try
        {
            SetAutoScrollEnabled(!_autoScrollEnabled);
        }
        catch { }
    }

    private void SetAutoScrollEnabled(bool enabled)
    {
        _autoScrollEnabled = enabled;
        try
        {
            _controlWindow?.SetAutoScrollEnabled(enabled);
            _controlWindow?.SetAutoScrollIntervalMs(_autoScrollIntervalMs);
        }
        catch { }

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { PlaceControlWindow(); } catch { }
            }, DispatcherPriority.Background);
        }
        catch { }

        if (enabled)
        {
            MoveCursorToCaptureRegionCenterForAutoScroll();
            EnsureAutoScrollTimer();
            try { _autoScrollTimer?.Change(0, _autoScrollIntervalMs); } catch { }
        }
        else
        {
            try { _autoScrollTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }
    }

    private void StopAutoScrollInternal()
    {
        _autoScrollEnabled = false;
        try { _autoScrollTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try
        {
            _controlWindow?.SetAutoScrollEnabled(false);
            _controlWindow?.SetAutoScrollIntervalMs(_autoScrollIntervalMs);
        }
        catch { }
    }

    private void OnAutoScrollIntervalWheel(int delta)
    {
        if (delta == 0) return;

        int steps = Math.Abs(delta) / 120;
        if (steps < 1) steps = 1;

        int ms = _autoScrollIntervalMs;
        for (int i = 0; i < steps; i++)
        {
            if (delta > 0) ms -= 20;
            else ms += 20;
        }

        _autoScrollIntervalMs = Math.Clamp(ms, 120, 1000);

        try
        {
            if (_autoScrollTimer != null && _autoScrollEnabled) _autoScrollTimer.Change(_autoScrollIntervalMs, _autoScrollIntervalMs);
        }
        catch { }

        try { _controlWindow?.SetAutoScrollIntervalMs(_autoScrollIntervalMs); } catch { }
    }

    private void MoveCursorToCaptureRegionCenterForAutoScroll()
    {
        try
        {
            if (_region.Width <= 0 || _region.Height <= 0) return;
            int x = _region.Left + _region.Width / 2;
            int y = _region.Top + _region.Height / 2;
            if (x < _region.Left) x = _region.Left;
            if (x >= _region.Right) x = _region.Right - 1;
            if (y < _region.Top) y = _region.Top;
            if (y >= _region.Bottom) y = _region.Bottom - 1;
            NativeMethods.SetCursorPos(x, y);
        }
        catch { }
    }

    private void EnsureAutoScrollTimer()
    {
        if (_autoScrollTimer != null) return;

        _autoScrollIntervalMs = Math.Clamp(_autoScrollIntervalMs, 120, 1000);
        _autoScrollTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _autoScrollTickRunning, 1) != 0) return;
            try { AutoScrollTick(); }
            catch { }
            finally { Interlocked.Exchange(ref _autoScrollTickRunning, 0); }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void AutoScrollTick()
    {
        if (!_autoScrollEnabled) return;
        if (!_isRunning) return;
        if (Volatile.Read(ref _finishStarted) != 0) return;

        if (_maxHeightReached || _totalHeight >= _options.MaxTotalHeightPx)
        {
            ShowMaxHeightNotice();
            return;
        }

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt)) return;
        if (!_region.Contains(pt.X, pt.Y)) return;

        try { NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_WHEEL, 0, 0, -120, IntPtr.Zero); } catch { }
    }

    private void PlaceControlWindow()
    {
        try
        {
            if (_controlWindow == null) return;
            double scale = _controlWindow.DesktopScaling > 0 ? _controlWindow.DesktopScaling : 1.0;
            int ww = (int)Math.Round(Math.Max(1, _controlWindow.Bounds.Width) * scale);
            int wh = (int)Math.Round(Math.Max(1, _controlWindow.Bounds.Height) * scale);
            if (ww <= 1) ww = (int)Math.Round(Math.Max(1, _controlWindow.Width) * scale);
            if (wh <= 1) wh = (int)Math.Round(Math.Max(1, _controlWindow.Height) * scale);

            Rectangle vs = MonitorHelper.GetVirtualScreenRect();
            int gap = 10;
            int left = _region.Right - ww;
            if (left < vs.Left) left = vs.Left;
            if (left + ww > vs.Right) left = vs.Right - ww;

            int top = _region.Bottom + gap;
            if (top + wh > vs.Bottom) top = _region.Top - wh - gap;
            if (top < vs.Top) top = vs.Top;

            bool sameRect = _hasLastControlWindowRect
                && _lastControlWindowX == left
                && _lastControlWindowY == top
                && _lastControlWindowW == ww
                && _lastControlWindowH == wh;
            if (sameRect) return;

            _controlWindow.Position = new PixelPoint(left, top);
            IntPtr hwndCtrl = GetWindowHwnd(_controlWindow);
            if (hwndCtrl != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(hwndCtrl, NativeMethods.HWND_TOPMOST, left, top, ww, wh, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }

            _lastControlWindowX = left;
            _lastControlWindowY = top;
            _lastControlWindowW = ww;
            _lastControlWindowH = wh;
            _hasLastControlWindowRect = true;
        }
        catch { }
    }

    private void InitDIB()
    {
        if (_hMemDC != IntPtr.Zero) return;

        NativeMethods.BITMAPINFO bmi = new();
        bmi.bmiHeader.biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = _region.Width;
        bmi.bmiHeader.biHeight = -_region.Height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = NativeMethods.DIB_RGB_COLORS;

        IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
        _hMemDC = NativeMethods.CreateCompatibleDC(hdc);
        _hBitmap = NativeMethods.CreateDIBSection(hdc, ref bmi, NativeMethods.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
        _hOldBitmap = NativeMethods.SelectObject(_hMemDC, _hBitmap);
        NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
    }

    private void CleanupDIB()
    {
        if (_hMemDC != IntPtr.Zero)
        {
            try { NativeMethods.SelectObject(_hMemDC, _hOldBitmap); } catch { }
            try { NativeMethods.DeleteDC(_hMemDC); } catch { }
            _hMemDC = IntPtr.Zero;
            _hOldBitmap = IntPtr.Zero;
        }

        if (_hBitmap != IntPtr.Zero)
        {
            try { NativeMethods.DeleteObject(_hBitmap); } catch { }
            _hBitmap = IntPtr.Zero;
            _pBits = IntPtr.Zero;
        }
    }

    private void CaptureLoop()
    {
        try { InitDIB(); }
        catch
        {
            try { _captureQueue?.CompleteAdding(); } catch { }
            return;
        }

        while (_isRunning && _cts != null && !_cts.IsCancellationRequested)
        {
            long start = Stopwatch.GetTimestamp();
            Bitmap? bmp = null;
            if (_bitmapPool == null || !_bitmapPool.TryTake(out bmp) || bmp == null)
            {
                bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb);
            }

            try
            {
                IntPtr hdcSrc = NativeMethods.GetDC(IntPtr.Zero);
                NativeMethods.BitBlt(_hMemDC, 0, 0, _region.Width, _region.Height, hdcSrc, _region.X, _region.Y, NativeMethods.SRCCOPY);
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcSrc);

                BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int srcStride = ((_region.Width * 32 + 31) / 32) * 4;
                    int dstStride = data.Stride;
                    if (dstStride < 0) dstStride = -dstStride;

                    if (dstStride == srcStride)
                    {
                        NativeMethods.CopyMemory(data.Scan0, _pBits, (uint)(srcStride * _region.Height));
                    }
                    else
                    {
                        int copyBytes = Math.Min(srcStride, dstStride);
                        for (int row = 0; row < _region.Height; row++)
                        {
                            IntPtr dst = IntPtr.Add(data.Scan0, row * data.Stride);
                            IntPtr src = IntPtr.Add(_pBits, row * srcStride);
                            NativeMethods.CopyMemory(dst, src, (uint)copyBytes);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                if (_captureQueue == null || !_captureQueue.TryAdd(bmp))
                {
                    Bitmap? dropped = null;
                    try
                    {
                        if (_captureQueue != null && _captureQueue.TryTake(out dropped)) _bitmapPool?.Add(dropped);
                    }
                    catch { dropped = null; }

                    if (_captureQueue == null || !_captureQueue.TryAdd(bmp))
                    {
                        if (_cts == null || _captureQueue == null || !_captureQueue.TryAdd(bmp, 10, _cts.Token))
                        {
                            _bitmapPool?.Add(bmp);
                        }
                    }
                }
            }
            catch
            {
                if (bmp != null) _bitmapPool?.Add(bmp);
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            int targetInterval = SafeCaptureIntervalMs;
            int sleep = targetInterval - (int)elapsedMs;
            if (sleep > 0) Thread.Sleep(sleep);
        }

        CleanupDIB();
    }

    private void StitchWorker()
    {
        int consecutiveNoMatchCount = 0;
        int lastAcceptedDirection = 0;
        Bitmap? prevBmp = null;

        try
        {
            if (_captureQueue == null || _cts == null || _bitmapPool == null) return;
            foreach (Bitmap queuedBmp in _captureQueue.GetConsumingEnumerable(_cts.Token))
            {
                Bitmap curBmp = queuedBmp;
                // 拼接线程不要一次性清空队列跳到最新帧。
                // 频繁滚动时过度跳帧会拉大相邻帧位移，降低匹配稳定性，导致漏段/重复段。
                // UI 预览仍会合并到最新帧；后台拼接优先保证连续性。
                if (!_isRunning) { _bitmapPool.Add(curBmp); continue; }

                if (_maxHeightReached || _totalHeight >= _options.MaxTotalHeightPx)
                {
                    _bitmapPool.Add(curBmp);
                    continue;
                }

                if (prevBmp == null)
                {
                    Bitmap? firstBmp = null;
                    Bitmap? firstTop = null;
                    Bitmap? firstBottom = null;
                    int fixedTopH = 0;
                    int fixedBottomH = 0;
                    try
                    {
                        CalculateFixedLayerHeights(curBmp.Height, out fixedTopH, out fixedBottomH);
                        firstTop = CropFixedTop(curBmp, fixedTopH);
                        firstBottom = CropFixedBottom(curBmp, fixedBottomH);
                        firstBmp = CropScrollableMiddle(curBmp, fixedTopH, fixedBottomH);
                        if (firstBmp != null && firstBmp.Height > _options.MaxTotalHeightPx)
                        {
                            Bitmap clipped = firstBmp.Clone(new Rectangle(0, 0, firstBmp.Width, _options.MaxTotalHeightPx), firstBmp.PixelFormat);
                            firstBmp.Dispose();
                            firstBmp = clipped;
                            _maxHeightReached = true;
                        }
                    }
                    catch
                    {
                        try { firstBmp = (Bitmap)curBmp.Clone(); } catch { firstBmp = null; }
                    }

                    if (firstBmp == null)
                    {
                        try { firstTop?.Dispose(); } catch { }
                        try { firstBottom?.Dispose(); } catch { }
                        _bitmapPool.Add(curBmp);
                        continue;
                    }

                    lock (_stripsLock)
                    {
                        _fixedTopHeight = fixedTopH;
                        _fixedBottomHeight = fixedBottomH;
                        try { _topBoundaryFixedBitmap?.Dispose(); } catch { }
                        try { _bottomBoundaryFixedBitmap?.Dispose(); } catch { }
                        _topBoundaryFixedBitmap = firstTop;
                        _bottomBoundaryFixedBitmap = firstBottom;
                        StitchedStrip baseStrip = new(firstBmp, 0);
                        _strips.Add(baseStrip);
                        _baseStrip = baseStrip;
                        _captureDirection = 0;
                        _totalHeight = firstBmp.Height;
                        _currentViewportY = 0;
                        _canvasTopY = 0;
                        _canvasBottomY = firstBmp.Height;
                        _viewportHeight = firstBmp.Height;
                        _lastCanvasAction = _options.EnableFixedLayerMode ? "初始化固定层画布" : "初始化画布";
                    }

                    prevBmp = curBmp;
                    UpdatePreview();
                    UpdateStatus();
                    continue;
                }

                int fixedTopForFrame;
                int fixedBottomForFrame;
                lock (_stripsLock)
                {
                    fixedTopForFrame = _fixedTopHeight;
                    fixedBottomForFrame = _fixedBottomHeight;
                }

                Bitmap? prevWorkBmp = null;
                Bitmap? curWorkBmp = null;
                try
                {
                    prevWorkBmp = CropScrollableMiddle(prevBmp, fixedTopForFrame, fixedBottomForFrame);
                    curWorkBmp = CropScrollableMiddle(curBmp, fixedTopForFrame, fixedBottomForFrame);
                }
                catch
                {
                    prevWorkBmp = null;
                    curWorkBmp = null;
                }

                if (prevWorkBmp == null || curWorkBmp == null)
                {
                    try { prevWorkBmp?.Dispose(); } catch { }
                    try { curWorkBmp?.Dispose(); } catch { }
                    _bitmapPool.Add(curBmp);
                    continue;
                }

                if (AreImagesIdentical(prevWorkBmp, curWorkBmp))
                {
                    try { prevWorkBmp.Dispose(); } catch { }
                    try { curWorkBmp.Dispose(); } catch { }
                    Interlocked.Increment(ref _consecutiveIdleFrames);
                    _bitmapPool.Add(curBmp);
                    continue;
                }

                int staticTop;
                int staticBottom;
                int wheelDir;
                try { wheelDir = Interlocked.Exchange(ref _lastWheelDir, 0); } catch { wheelDir = 0; }

                bool frameDirUp;
                int delta;
                if (wheelDir != 0)
                {
                    bool fUp = wheelDir > 0;
                    delta = ImageStitcher.EstimateScrollDelta(prevWorkBmp, curWorkBmp, null, out staticTop, out staticBottom, fUp, !fUp);
                    if (delta < 0) delta = -delta;
                    frameDirUp = fUp;
                }
                else
                {
                    delta = ImageStitcher.EstimateScrollDelta(prevWorkBmp, curWorkBmp, null, out staticTop, out staticBottom, false, false);
                    if (delta > 0) frameDirUp = true;
                    else if (delta < 0) { frameDirUp = false; delta = -delta; }
                    else frameDirUp = false;
                }

                bool stitched = false;
                bool positionUpdated = false;
                int maxTrustedDelta = Math.Max(24, (int)Math.Round(curWorkBmp.Height * 0.52));
                if (delta >= maxTrustedDelta)
                {
                    // 用户快速滚动或系统回弹时，单帧位移过大会显著增加误匹配概率。
                    // 这类帧只作为新的比较基准，不写入画布，优先保证最终拼接正确。
                    consecutiveNoMatchCount = 0;
                    UpdateStatusHint("滚动较快，正在稳定当前帧。", false);
                    try { prevWorkBmp.Dispose(); } catch { }
                    try { curWorkBmp.Dispose(); } catch { }
                    _bitmapPool.Add(prevBmp);
                    prevBmp = curBmp;
                    continue;
                }

                if (delta > 0 && delta < curWorkBmp.Height)
                {
                    int frameDirection = frameDirUp ? -1 : 1;
                    if (lastAcceptedDirection != 0 && frameDirection != lastAcceptedDirection)
                    {
                        // 上下反复滚动、触边回弹时，方向切换后的第一帧最容易出现匹配抖动。
                        // 这帧只作为新的比较基准，不写入画布，避免污染最终拼接结果；下一帧稳定后再拼接。
                        lastAcceptedDirection = frameDirection;
                        consecutiveNoMatchCount = 0;
                        UpdateStatusHint("滚动方向切换，正在稳定当前帧。", false);
                        try { prevWorkBmp.Dispose(); } catch { }
                        try { curWorkBmp.Dispose(); } catch { }
                        _bitmapPool.Add(prevBmp);
                        prevBmp = curBmp;
                        continue;
                    }

                    int newViewportY;
                    lock (_stripsLock)
                    {
                        newViewportY = frameDirUp ? (_currentViewportY - delta) : (_currentViewportY + delta);
                    }

                    stitched = TryApplyFrameToCanvas(curWorkBmp, newViewportY, frameDirUp, staticTop, staticBottom, out positionUpdated);
                    if (stitched)
                    {
                        if (frameDirUp) UpdateTopBoundaryFixedLayer(curBmp);
                        else UpdateBottomBoundaryFixedLayer(curBmp);
                    }
                }

                if (stitched || positionUpdated)
                {
                    if (delta > 0) lastAcceptedDirection = frameDirUp ? -1 : 1;
                    consecutiveNoMatchCount = 0;
                    Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
                    UpdatePreview();
                    UpdateStatus();
                    try { prevWorkBmp.Dispose(); } catch { }
                    try { curWorkBmp.Dispose(); } catch { }
                    _bitmapPool.Add(prevBmp);
                    prevBmp = curBmp;
                    if (stitched) _capturedFrameCount++;
                }
                else
                {
                    consecutiveNoMatchCount++;
                    Interlocked.Increment(ref _consecutiveIdleFrames);
                    if (consecutiveNoMatchCount == 8 || consecutiveNoMatchCount == 20)
                    {
                        string reason = ImageStitcher.GetLastDeltaSource();
                        string canvasAction = _lastCanvasAction;
                        if (canvasAction == "跳过可疑坐标") UpdateStatusHint("当前位置坐标不稳定，已跳过可疑帧。", false);
                        else if (reason == "RepeatedTexture") UpdateStatusHint("检测到重复纹理，匹配不唯一，已跳过可疑帧。", false);
                        else if (reason == "LowConfidence") UpdateStatusHint("当前重叠匹配置信度偏低，建议慢一点滚动。", false);
                        else if (reason == "LowTexture") UpdateStatusHint("当前区域纹理过少，暂不强行拼接。", false);
                        else UpdateStatusHint("未检测到稳定重叠，建议放慢滚动并保持内容连续。", false);
                    }

                    try { prevWorkBmp.Dispose(); } catch { }
                    try { curWorkBmp.Dispose(); } catch { }
                    _bitmapPool.Add(curBmp);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { if (prevBmp != null) _bitmapPool?.Add(prevBmp); } catch { }
        }
    }

    private void CalculateFixedLayerHeights(int frameHeight, out int topH, out int bottomH)
    {
        topH = 0;
        bottomH = 0;
        if (!_options.EnableFixedLayerMode || frameHeight <= 0) return;
        topH = (int)Math.Round(frameHeight * _options.FixedTopRatio);
        bottomH = (int)Math.Round(frameHeight * _options.FixedBottomRatio);
        if (topH > _options.MaxFixedLayerHeightPx) topH = _options.MaxFixedLayerHeightPx;
        if (bottomH > _options.MaxFixedLayerHeightPx) bottomH = _options.MaxFixedLayerHeightPx;
        if (topH < 0) topH = 0;
        if (bottomH < 0) bottomH = 0;
        int minMiddle = (int)Math.Round(frameHeight * 0.65);
        if (minMiddle < 80) minMiddle = 80;
        int middle = frameHeight - topH - bottomH;
        if (middle < minMiddle)
        {
            int allowedFixed = frameHeight - minMiddle;
            if (allowedFixed < 0) allowedFixed = 0;
            double total = topH + bottomH;
            if (total <= 0)
            {
                topH = 0;
                bottomH = 0;
            }
            else
            {
                topH = (int)Math.Round(allowedFixed * (topH / total));
                bottomH = allowedFixed - topH;
            }
        }
    }

    private Bitmap? CropFixedTop(Bitmap bmp, int topH)
    {
        if (!_options.EnableFixedLayerMode || bmp == null || topH <= 0) return null;
        if (topH > bmp.Height) topH = bmp.Height;
        return bmp.Clone(new Rectangle(0, 0, bmp.Width, topH), bmp.PixelFormat);
    }

    private Bitmap? CropFixedBottom(Bitmap bmp, int bottomH)
    {
        if (!_options.EnableFixedLayerMode || bmp == null || bottomH <= 0) return null;
        if (bottomH > bmp.Height) bottomH = bmp.Height;
        return bmp.Clone(new Rectangle(0, bmp.Height - bottomH, bmp.Width, bottomH), bmp.PixelFormat);
    }

    private Bitmap? CropScrollableMiddle(Bitmap bmp, int topH, int bottomH)
    {
        if (bmp == null) return null;
        if (!_options.EnableFixedLayerMode)
        {
            return (Bitmap)bmp.Clone();
        }

        if (topH < 0) topH = 0;
        if (bottomH < 0) bottomH = 0;
        if (topH > bmp.Height) topH = bmp.Height;
        if (bottomH > bmp.Height - topH) bottomH = bmp.Height - topH;
        int middleH = bmp.Height - topH - bottomH;
        if (middleH <= 0) return null;
        return bmp.Clone(new Rectangle(0, topH, bmp.Width, middleH), bmp.PixelFormat);
    }

    private void ClampCurrentViewportLocked()
    {
        int viewportH = _viewportHeight > 0 ? _viewportHeight : _region.Height;
        int minY = _canvasTopY;
        int maxY = _canvasBottomY - viewportH;
        if (maxY < minY) maxY = minY;
        if (_currentViewportY < minY) _currentViewportY = minY;
        if (_currentViewportY > maxY) _currentViewportY = maxY;
    }

    private void UpdateTopBoundaryFixedLayer(Bitmap fullFrame)
    {
        if (!_options.EnableFixedLayerMode || fullFrame == null) return;
        Bitmap? top = null;
        int topH;
        lock (_stripsLock) { topH = _fixedTopHeight; }
        try { top = CropFixedTop(fullFrame, topH); } catch { top = null; }
        if (top == null && topH > 0) return;
        lock (_stripsLock)
        {
            try { _topBoundaryFixedBitmap?.Dispose(); } catch { }
            _topBoundaryFixedBitmap = top;
        }
    }

    private void UpdateBottomBoundaryFixedLayer(Bitmap fullFrame)
    {
        if (!_options.EnableFixedLayerMode || fullFrame == null) return;
        Bitmap? bottom = null;
        int bottomH;
        lock (_stripsLock) { bottomH = _fixedBottomHeight; }
        try { bottom = CropFixedBottom(fullFrame, bottomH); } catch { bottom = null; }
        if (bottom == null && bottomH > 0) return;
        lock (_stripsLock)
        {
            try { _bottomBoundaryFixedBitmap?.Dispose(); } catch { }
            _bottomBoundaryFixedBitmap = bottom;
        }
    }

    private bool TryApplyFrameToCanvas(Bitmap curBmp, int newViewportY, bool directionIsUp, int staticTop, int staticBottom, out bool positionUpdated)
    {
        positionUpdated = false;
        if (curBmp == null) return false;

        try
        {
            int viewH = curBmp.Height;
            int viewTop = newViewportY;
            int viewBottom = newViewportY + viewH;
            bool stitched = false;

            lock (_stripsLock)
            {
                if (_totalHeight <= 0 || _strips.Count == 0) return false;
                if (_viewportHeight <= 0) _viewportHeight = viewH;

                if (viewBottom <= viewTop)
                {
                    _lastCanvasAction = "跳过可疑坐标";
                    return false;
                }

                int topOverflow = _canvasTopY - viewTop;
                int bottomOverflow = viewBottom - _canvasBottomY;

                if (topOverflow > 0 && bottomOverflow > 0)
                {
                    _lastCanvasAction = "跳过可疑坐标";
                    return false;
                }

                if (topOverflow <= 0 && bottomOverflow <= 0)
                {
                    _currentViewportY = newViewportY;
                    _lastCanvasAction = "回看已覆盖区域";
                    positionUpdated = true;
                    return false;
                }

                int appendDirection = topOverflow > 0 ? -1 : bottomOverflow > 0 ? 1 : 0;
                if (appendDirection != 0)
                {
                    _captureDirection = appendDirection;
                }


                int remaining = _options.MaxTotalHeightPx - _totalHeight;
                if (remaining <= 0)
                {
                    _maxHeightReached = true;
                    _lastCanvasAction = "达到最大高度";
                    return false;
                }

                if (topOverflow > 0)
                {
                    int addH = topOverflow;
                    if (addH > remaining) addH = remaining;
                    int dynBottom = curBmp.Height - staticBottom;
                    if (dynBottom < staticTop) dynBottom = staticTop;
                    if (addH > dynBottom - staticTop) addH = dynBottom - staticTop;
                    if (addH <= 0)
                    {
                        _currentViewportY = newViewportY;
                        _lastCanvasAction = "顶部无有效新增";
                        positionUpdated = true;
                        return false;
                    }

                    int srcY = staticTop;
                    if (srcY + addH > curBmp.Height) addH = curBmp.Height - srcY;
                    if (addH <= 0) return false;

                    Bitmap stripBmp = curBmp.Clone(new Rectangle(0, srcY, curBmp.Width, addH), curBmp.PixelFormat);
                    for (int i = 0; i < _strips.Count; i++)
                    {
                        _strips[i].OffsetY(addH);
                    }
                    _strips.Insert(0, new StitchedStrip(stripBmp, 0));
                    _totalHeight += addH;
                    _canvasTopY -= addH;
                    _currentViewportY = newViewportY;
                    if (_totalHeight >= _options.MaxTotalHeightPx) _maxHeightReached = true;
                    _lastCanvasAction = "补充顶部 " + addH + "px";
                    stitched = true;
                }
                else if (bottomOverflow > 0)
                {
                    int addH = bottomOverflow;
                    if (addH > remaining) addH = remaining;
                    int dynBottom = curBmp.Height - staticBottom;
                    int srcY = dynBottom - addH;
                    if (srcY < staticTop) srcY = staticTop;
                    if (srcY + addH > dynBottom) addH = dynBottom - srcY;
                    if (addH <= 0)
                    {
                        _currentViewportY = newViewportY;
                        _lastCanvasAction = "底部无有效新增";
                        positionUpdated = true;
                        return false;
                    }

                    if (srcY + addH > curBmp.Height) addH = curBmp.Height - srcY;
                    if (addH <= 0) return false;

                    int cropEnd = curBmp.Height;
                    int viewForOverlay = _viewportHeight > 0 ? _viewportHeight : curBmp.Height;
                    int overlayH = viewForOverlay / 3 - addH;
                    if (overlayH < 0) overlayH = 0;
                    int minOverlayForFixedBottom = staticBottom > 0 ? staticBottom + 12 : 0;
                    if (overlayH < minOverlayForFixedBottom) overlayH = minOverlayForFixedBottom;
                    int maxOverlay = cropEnd - staticTop - addH;
                    if (maxOverlay < 0) maxOverlay = 0;
                    if (overlayH > maxOverlay) overlayH = maxOverlay;

                    int cropH = addH + overlayH;
                    int cropY = cropEnd - cropH;
                    if (cropY < staticTop)
                    {
                        cropY = staticTop;
                        cropH = cropEnd - cropY;
                    }

                    if (cropY + cropH > curBmp.Height) cropH = curBmp.Height - cropY;
                    if (cropH <= 0) return false;
                    if (overlayH > cropH - addH) overlayH = cropH - addH;
                    if (overlayH < 0) overlayH = 0;

                    Bitmap stripBmp = curBmp.Clone(new Rectangle(0, cropY, curBmp.Width, cropH), curBmp.PixelFormat);
                    int y = _totalHeight - overlayH;
                    _strips.Add(new StitchedStrip(stripBmp, y, overlayH));
                    _totalHeight += addH;
                    _canvasBottomY += addH;
                    _currentViewportY = newViewportY;
                    if (_totalHeight >= _options.MaxTotalHeightPx) _maxHeightReached = true;
                    _lastCanvasAction = "追加底部 " + addH + "px";
                    stitched = true;
                }
            }

            return stitched;
        }
        catch
        {
            _lastCanvasAction = "跳过可疑坐标";
            return false;
        }
    }

    private void UpdatePreview()
    {
        if (!EnablePreviewWindow || _previewWindow == null) return;

        try
        {
            Rectangle wa = MonitorHelper.GetVirtualScreenRect();
            int gap = 20;
            int availRightW = wa.Right - (_region.Right + gap);
            int availLeftW = (_region.Left - gap) - wa.Left;
            if (availRightW < 0) availRightW = 0;
            if (availLeftW < 0) availLeftW = 0;

            bool dockRight;
            if (availRightW >= 60) dockRight = true;
            else if (availLeftW >= 60) dockRight = false;
            else dockRight = availRightW >= availLeftW;

            int availW = dockRight ? availRightW : availLeftW;
            if (availW < 60) availW = 60;
            if (availW > wa.Width) availW = wa.Width;

            int top = _region.Top;
            if (top < wa.Top) top = wa.Top;
            int availH = wa.Bottom - top;
            if (availH <= 0) availH = wa.Height;
            if (availH > wa.Height) availH = wa.Height;

            int frameW = availW;
            int frameH = availH;
            if (frameW < 60) frameW = 60;
            if (frameH < 120) frameH = 120;

            List<StitchedStrip> snapshot;
            int totalHeightSnapshot;
            int currentViewportYSnapshot;
            int viewportHeightSnapshot;
            int fixedTopSnapshot;
            int fixedBottomSnapshot;
            Bitmap? fixedTopPreview = null;
            Bitmap? fixedBottomPreview = null;
            lock (_stripsLock)
            {
                if (_strips.Count == 0) return;
                snapshot = new List<StitchedStrip>(_strips);
                totalHeightSnapshot = _totalHeight > 0
                    ? _totalHeight
                    : LongScrollComposer.CalculateCompositedHeight(_strips, _region.Height);
                currentViewportYSnapshot = _currentViewportY - _canvasTopY;
                if (currentViewportYSnapshot < 0) currentViewportYSnapshot = 0;
                viewportHeightSnapshot = _viewportHeight > 0 ? _viewportHeight : _region.Height;
                fixedTopSnapshot = _fixedTopHeight;
                fixedBottomSnapshot = _fixedBottomHeight;
                try { if (_topBoundaryFixedBitmap != null) fixedTopPreview = (Bitmap)_topBoundaryFixedBitmap.Clone(); } catch { fixedTopPreview = null; }
                try { if (_bottomBoundaryFixedBitmap != null) fixedBottomPreview = (Bitmap)_bottomBoundaryFixedBitmap.Clone(); } catch { fixedBottomPreview = null; }
            }

            int displayViewportHeightSnapshot = Math.Max(1, viewportHeightSnapshot + fixedTopSnapshot + fixedBottomSnapshot);
            int displayTotalHeightSnapshot = Math.Max(totalHeightSnapshot, totalHeightSnapshot + fixedTopSnapshot + fixedBottomSnapshot);

            _previewWindow.UpdateFrame(frameW, frameH);
            _previewWindow.UpdateViewport(currentViewportYSnapshot, displayViewportHeightSnapshot, displayTotalHeightSnapshot, fixedTopSnapshot, fixedBottomSnapshot);
            _previewWindow.UpdateStrips(snapshot, totalHeightSnapshot, fixedTopPreview, fixedBottomPreview);
            _previewWindow.SetAnchor(_region, dockRight);
        }
        catch
        {
            // 预览失败不能影响长截图采集。
        }
    }

    private int GetDisplayHeight()
    {
        int height = _totalHeight;
        return height > 0 ? height : Math.Max(0, _region.Height);
    }

    private void UpdateStatus()
    {
        if (_controlWindow == null) return;
        long nowTick = Stopwatch.GetTimestamp();
        long lastTick = Interlocked.Read(ref _lastStatusUpdateTick);
        long minTicks = Stopwatch.Frequency * StatusUpdateIntervalMs / 1000;
        if (lastTick != 0 && nowTick - lastTick >= 0 && nowTick - lastTick < minTicks) return;
        Interlocked.Exchange(ref _lastStatusUpdateTick, nowTick);
        string status = string.Format("{0} × {1}px", _region.Width, GetDisplayHeight());
        string metrics = string.Empty;
        string hint = string.Empty;
        bool warn = false;
        int pressurePct = 0;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _controlWindow?.SetState(status, metrics, hint, warn, pressurePct);
                try { PlaceControlWindow(); } catch { }
            }
            catch
            {
                try { _controlWindow?.SetStatus(status); } catch { }
            }
        }, DispatcherPriority.Background);
    }

    private void ShowMaxHeightNotice()
    {
        try
        {
            UpdateStatusHint("已达最大长度，无法继续截图。", true);
        }
        catch
        {
        }
    }

    private void UpdateStatusHint(string hint, bool warn)
    {
        if (_controlWindow == null) return;
        string status = string.Format("{0} × {1}px", _region.Width, GetDisplayHeight());
        string metrics = string.Empty;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _controlWindow?.SetState(status, metrics, string.Empty, false, 0);
                try { PlaceControlWindow(); } catch { }
            }
            catch
            {
                try { _controlWindow?.SetStatus(status); } catch { }
            }
        }, DispatcherPriority.Background);
    }

    private async Task FinishInternalAsync(bool saveToClipboard, string? saveFilePath, bool canceled)
    {
        if (Interlocked.Exchange(ref _finishStarted, 1) == 1) return;

        LongScrollMemoryDiagnostics.LogImage("LongScroll.BeforeFinish", _region.Width, _totalHeight);

        _isRunning = false;
        StopAutoScrollInternal();
        UninstallMouseHook();
        try { _captureQueue?.CompleteAdding(); } catch { }
        try { _cts?.Cancel(); } catch { }

        try
        {
            if (_controlWindow != null)
            {
                _controlWindow.SetStatus(string.Format("{0} × {1}px", _region.Width, GetDisplayHeight()));
            }
        }
        catch { }

        LongScrollResult result;
        try
        {
            await Task.Run(() =>
            {
                try { if (_captureThread != null && _captureThread.IsAlive) _captureThread.Join(2500); } catch { }
                try { _stitchTask?.Wait(2500); } catch { }
            });

            if (canceled)
            {
                result = LongScrollResult.Cancel("长截图已取消。");
            }
            else
            {
                result = await SaveFinalAsync(saveToClipboard, saveFilePath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LegacyLongScrollSession.FinishInternalAsync 异常", ex);
            result = LongScrollResult.Cancel("长截图失败：" + ex.Message);
        }
        finally
        {
            await CleanupAfterFinishAsync();
        }

        _completionTcs?.TrySetResult(result);
    }

    private async Task<LongScrollResult> SaveFinalAsync(bool saveToClipboard, string? saveFilePath)
    {
        List<StitchedStrip> finalStrips;
        Bitmap? finalTopFixed = null;
        Bitmap? finalBottomFixed = null;
        lock (_stripsLock)
        {
            finalStrips = new List<StitchedStrip>(_strips);
            try { if (_topBoundaryFixedBitmap != null) finalTopFixed = (Bitmap)_topBoundaryFixedBitmap.Clone(); } catch { finalTopFixed = null; }
            try { if (_bottomBoundaryFixedBitmap != null) finalBottomFixed = (Bitmap)_bottomBoundaryFixedBitmap.Clone(); } catch { finalBottomFixed = null; }
            _strips.Clear();
        }

        try
        {
            if (finalStrips.Count == 0)
            {
                return LongScrollResult.Cancel("没有采集到可合成的帧。");
            }

            int middleH = CalculateCompositedHeight(finalStrips, _totalHeight);
            if (middleH <= 0) middleH = _totalHeight;
            LongScrollMemoryDiagnostics.LogImage("LongScroll.BeforeCompose", _region.Width, middleH);
            using Bitmap output = LongScrollComposer.Compose(finalStrips, middleH, finalTopFixed, finalBottomFixed);
            LongScrollMemoryDiagnostics.LogImage("LongScroll.AfterCompose", output.Width, output.Height);

            string path = string.IsNullOrWhiteSpace(saveFilePath) ? ResolveDefaultOutputPath() : NormalizeSaveAsPath(saveFilePath);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (_controlWindow != null)
            {
                _controlWindow.SetStatus(string.Format("{0} × {1}px", _region.Width, middleH));
            }

            await Task.Run(() => ImageExportService.Save(output, path));
            LongScrollMemoryDiagnostics.LogImage("LongScroll.AfterSave", output.Width, output.Height);

            string? clipboardError = null;
            if (saveToClipboard)
            {
                LongScrollMemoryDiagnostics.LogImage("LongScroll.BeforeClipboard", output.Width, output.Height);
                try { ClipboardService.SetImage(output); }

                catch (Exception ex)
                {
                    clipboardError = ex.Message;
                    AppLogger.Warn("LegacyLongScrollSession: 复制剪贴板失败：" + ex.Message);
                }
                LongScrollMemoryDiagnostics.LogImage("LongScroll.AfterClipboard", output.Width, output.Height);
            }

            string message;
            if (!string.IsNullOrWhiteSpace(saveFilePath)) message = "长截图已另存为：" + path;
            else if (saveToClipboard && string.IsNullOrWhiteSpace(clipboardError)) message = "长截图已保存并复制到剪贴板：" + path;
            else if (saveToClipboard) message = "长截图已保存：" + path + "；复制剪贴板失败：" + clipboardError;
            else message = "长截图已保存：" + path;

            try { _controlWindow?.SetStatus(string.Format("{0} × {1}px", _region.Width, middleH)); } catch { }
            return LongScrollResult.Saved(path, message);
        }
        finally
        {
            try { finalTopFixed?.Dispose(); } catch { }
            try { finalBottomFixed?.Dispose(); } catch { }
            DisposeStrips(finalStrips);
        }
    }

    public static int CalculateCompositedHeight(List<StitchedStrip> strips, int fallbackHeight)
    {
        try
        {
            int bottom = 0;
            if (strips != null)
            {
                for (int i = 0; i < strips.Count; i++)
                {
                    StitchedStrip? s = strips[i];
                    if (s == null) continue;
                    int b = s.Y + s.Height;
                    if (b > bottom) bottom = b;
                }
            }

            if (bottom > 0) return bottom;
        }
        catch { }

        return fallbackHeight;
    }

    private bool AreImagesIdentical(Bitmap a, Bitmap b)
    {
        if (a == null || b == null) return false;
        if (a.Width != b.Width || a.Height != b.Height) return false;

        try
        {
            PixelFormat format = a.PixelFormat == PixelFormat.Format32bppRgb ? PixelFormat.Format32bppRgb : PixelFormat.Format32bppArgb;
            BitmapData da = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, format);
            BitmapData db = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, format);

            bool diff = false;
            try
            {
                int count = 100;
                int totalPixels = Math.Max(1, a.Width * a.Height);
                int step = Math.Max(1, totalPixels / count);
                int byteStep = step * 4;
                int maxBytes = Math.Min(Math.Abs(da.Stride), Math.Abs(db.Stride)) * a.Height;
                for (int i = 0; i < count; i++)
                {
                    int offset = i * byteStep;
                    if (offset + 4 > maxBytes) break;
                    int valA = Marshal.ReadInt32(da.Scan0, offset);
                    int valB = Marshal.ReadInt32(db.Scan0, offset);
                    if (valA != valB) { diff = true; break; }
                }
            }
            finally
            {
                a.UnlockBits(da);
                b.UnlockBits(db);
            }

            return !diff;
        }
        catch { return false; }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        try
        {
            _mouseProc = HookMouseProc;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
        }
        catch { }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    private IntPtr HookMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _isRunning && wParam == (IntPtr)NativeMethods.WM_MOUSEWHEEL)
            {
                var lh = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                int delta = (short)((lh.mouseData >> 16) & 0xffff);
                bool inside = _region.Contains(lh.pt.X, lh.pt.Y);
                int logCount = Interlocked.Increment(ref _wheelLogCount);
                if (logCount <= 5 || logCount % 50 == 0)
                {
                    AppLogger.Info("LegacyLongScrollSession.HookMouseProc: wheel delta=" + delta + ", pt=(" + lh.pt.X + "," + lh.pt.Y + "), inside=" + inside + ", max=" + _maxHeightReached + ", total=" + _totalHeight);
                }

                int dir = 0;
                if (delta > 0) dir = 1;
                else if (delta < 0) dir = -1;
                if (dir != 0 && inside)
                {
                    Interlocked.Exchange(ref _lastWheelDir, dir);
                    Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
                }

                if ((_maxHeightReached || _totalHeight >= _options.MaxTotalHeightPx) && inside)
                {
                    ShowMaxHeightNotice();
                    return (IntPtr)1;
                }
            }
        }
        catch { }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private async Task<string?> PickSaveAsPathAsync(Window window)
    {
        try
        {
            IReadOnlyList<FilePickerFileType> filters = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("Bitmap Image") { Patterns = new[] { "*.bmp" } },
            };

            IStorageFile? file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为长截图",
                SuggestedFileName = "LongScreenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                FileTypeChoices = filters,
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
            });

            return file?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LegacyLongScrollSession: 打开另存为对话框失败：" + ex.Message);
            return null;
        }
    }

    private string ResolveDefaultOutputPath()
    {
        string fileName = ResolveFileName(_settings);
        return Path.Combine(_settings.SaveDirectory ?? string.Empty, fileName);
    }

    private static string NormalizeSaveAsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("另存为路径为空。", nameof(path));
        string normalized = path;
        string ext = Path.GetExtension(normalized);
        if (string.IsNullOrWhiteSpace(ext)) normalized += ".png";
        return normalized;
    }

    private static string ResolveFileName(AppSettings settings)
    {
        string template = string.IsNullOrWhiteSpace(settings.FileNameTemplate)
            ? "LongScreenshot_{yyyyMMdd_HHmmss}"
            : settings.FileNameTemplate;

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = template.Replace("{yyyyMMdd_HHmmss}", stamp);
        if (!baseName.Contains("Long", StringComparison.OrdinalIgnoreCase) && !baseName.Contains("长截图", StringComparison.OrdinalIgnoreCase))
        {
            baseName += "_Long";
        }

        // 长截图统一保存为 PNG（保留元数据），扩展名固定。
        return baseName + ".png";
    }

    private async Task CleanupAfterFinishAsync()
    {
        _isRunning = false;
        StopAutoScrollInternal();
        UninstallMouseHook();
        try { _autoScrollTimer?.Dispose(); } catch { }
        _autoScrollTimer = null;
        try { _captureQueue?.CompleteAdding(); } catch { }
        try { _cts?.Cancel(); } catch { }
        CleanupDIB();
        LongScrollMemoryDiagnostics.Log("LongScroll.BeforeDisposeStrips");

        try
        {
            while (_captureQueue != null && _captureQueue.TryTake(out Bitmap? queued))
            {
                try { queued.Dispose(); } catch { }
            }
        }
        catch { }

        try
        {
            while (_bitmapPool != null && _bitmapPool.TryTake(out Bitmap? pooled))
            {
                try { pooled.Dispose(); } catch { }
            }
        }
        catch { }

        lock (_stripsLock)
        {
            DisposeStrips(_strips);
            _strips.Clear();
            for (int i = 0; i < _retiredStripBitmaps.Count; i++)
            {
                try { _retiredStripBitmaps[i]?.Dispose(); } catch { }
            }
            _retiredStripBitmaps.Clear();
            try { _topBoundaryFixedBitmap?.Dispose(); } catch { }
            try { _bottomBoundaryFixedBitmap?.Dispose(); } catch { }
            _topBoundaryFixedBitmap = null;
            _bottomBoundaryFixedBitmap = null;
            _fixedTopHeight = 0;
            _fixedBottomHeight = 0;
            _totalHeight = 0;
            _currentViewportY = 0;
            _canvasTopY = 0;
            _canvasBottomY = 0;
            _viewportHeight = 0;
            _captureDirection = 0;
            _baseStrip = null;
            _lastCanvasAction = "已清理";
        }
        LongScrollMemoryDiagnostics.Log("LongScroll.AfterDisposeStrips");

        try { _captureQueue?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _captureQueue = null;
        _bitmapPool = null;
        _cts = null;

        try { _nativeOverlay?.Dispose(); } catch { }
        _nativeOverlay = null;

        LongScrollMemoryDiagnostics.Log("LongScroll.BeforePreviewShutdown");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                _previewWindow?.ShutdownRendering();
                if (_previewWindow != null && _previewWindow.IsVisible) _previewWindow.Close();
            }
            catch { }
            try
            {
                if (_controlWindow != null && _controlWindow.IsVisible) _controlWindow.Close();
            }
            catch { }
            _previewWindow = null;
            _controlWindow = null;
        });
        LongScrollMemoryDiagnostics.Log("LongScroll.AfterWindowClose");

        ForceLongScrollMemoryCleanup();
    }

    private static void ForceLongScrollMemoryCleanup()
    {
        LongScrollMemoryDiagnostics.Log("LongScroll.BeforeGC");
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { }
        LongScrollMemoryDiagnostics.Log("LongScroll.AfterGC");

        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
        LongScrollMemoryDiagnostics.Log("LongScroll.AfterEmptyWorkingSet");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000).ConfigureAwait(false);
                LongScrollMemoryDiagnostics.Log("LongScroll.AfterDelay2s");
                await Task.Delay(8000).ConfigureAwait(false);
                LongScrollMemoryDiagnostics.Log("LongScroll.AfterDelay10s");
            }
            catch { }
        });
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static void DisposeStrips(IList<StitchedStrip> strips)
    {
        for (int i = 0; i < strips.Count; i++)
        {
            try { strips[i]?.Dispose(); } catch { }
        }
    }

    private static IntPtr GetWindowHwnd(Window? window)
    {
        try { return window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}
