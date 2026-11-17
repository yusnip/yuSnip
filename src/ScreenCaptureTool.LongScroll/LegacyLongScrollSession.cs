using System;
using HotKeyManager = ScreenCaptureTool.Core.HotKeyManager;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.LongScroll.Capture;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.LongScroll.Diagnostics;
using ScreenCaptureTool.LongScroll.Stitching;
using ScreenCaptureTool.LongScroll.Windows;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// </summary>
public sealed class LegacyLongScrollSession
{
    private bool EnablePreviewWindow => _settings?.ShowLongScrollPreview ?? true;
    private static readonly bool EnableBorderWindow = true;
    private static readonly bool EnableGlobalMouseHook = true;
    private static readonly bool EnableAutoScroll = true;
    private const int SafeCaptureIntervalMs = 16;
    private const long CaptureQueueBudgetBytes = 64L * 1024 * 1024;
    private const int CaptureLookaheadFrames = 2;

    private readonly LongScrollOptions _options;
    private readonly AppSettings _settings;
    private Rectangle _region;
    private readonly List<StitchedStrip> _strips = new();
    private readonly List<Bitmap> _retiredStripBitmaps = new();
    private readonly object _stripsLock = new();
    private readonly object _previewUpdateLock = new();

    private volatile int _totalHeight;
    private volatile bool _maxHeightReached;
    // 运行期内存复查：每攒这么多条回读一次当前空余物理内存。
    private const int BudgetRecheckAppends = 20;
    private int _budgetRecheckAppends;
    // 收口原因：true＝机器此刻拿不出更多内存，false＝攒到本次锁定的上限。两者控制条话术不同。
    private volatile bool _budgetExceeded;
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
    private volatile string _lastCanvasAction = "待命";

    private BlockingCollection<CapturedFrame>? _captureQueue;
    private int _previewFlushRunning;
    private long _processedFrames, _processingTicks, _queueTicks, _previewPushTicks, _previewPushes;
    private long _maxQueueTicks, _featureTicks, _featureCalls, _captureBackpressureTicks;
    private int _captureFramesDropped;
    private int _alignmentPending;
    private int _stitchFrameInProgress;
    private long _alignmentPendingSinceTick;
    private long _lastCaptureChangeTick;
    private long _lastStationaryViewportTick;
    private long _stationaryAnimationFrames;
    private long _lastAutoScrollInjectionTick;

    /// <summary>入队捕获帧：位图 + 捕获时间戳（帧链间隔测量与速度预测窗的输入）。</summary>
    private sealed record CapturedFrame(Bitmap Bitmap, long Timestamp);

    // v3.1 健康遥测（每 60 帧窗口汇总一次，输出后清零）。
    private long _teleWindowFrames;
    private long _teleWindowAccepted;
    private long _teleFastPathCount;
    private long _teleFastPathMsTotal;
    private long _teleFallbackCount;
    private long _teleFallbackMsTotal;
    private long _teleDeltaSum;
    private int _teleDeltaMax;
    private long _teleSinceTs;
    private long _teleGatedFrames;
    private long _lastMotionTick;
    private ConcurrentBag<Bitmap>? _bitmapPool;
    private CancellationTokenSource? _cts;
    private Task? _stitchTask;
    private Thread? _captureThread;

    private LongScrollControlWindow? _controlWindow;
    private HotKeyManager? _cancelShortcut;
    private HotKeyManager? _finishShortcut;
    private int _shortcutScopeVersion;
    private NativeLongScrollOverlay? _nativeOverlay;
    private LongScrollPreviewWindow? _previewWindow;
    private TaskCompletionSource<LongScrollResult>? _completionTcs;

    private bool _hasLastControlWindowRect;
    private int _lastControlWindowX;
    private int _lastControlWindowY;
    private int _lastControlWindowW;
    private int _lastControlWindowH;

    private bool _verticalControlPreferBelow = true;
    private bool _horizontalControlPreferBelow = false;
    private int _lastAnchoredControlScreenY = -1;

    private bool _previewSuppressedForDrag;

    private IntPtr _hMemDC = IntPtr.Zero;
    private HdrScreenCapture? _hdrCapture;
    private IntPtr _hBitmap = IntPtr.Zero;
    private IntPtr _hOldBitmap = IntPtr.Zero;
    private IntPtr _pBits = IntPtr.Zero;

    private bool _isRunning;
    private int _finishStarted;
    private int _saveDialogOpen;
    private TaskCompletionSource? _saveDialogCapturePaused;
    private int _capturedFrameCount;
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private int _lastWheelDir;
    private long _lastWheelDirectionTick;
    // 1=WM_MOUSEHWHEEL, 2=Shift+WM_MOUSEWHEEL, 3=WM_MOUSEWHEEL.
    // Keep the source separately so real-session logs can show why a direction lock
    // was chosen instead of presenting every hint as an undifferentiated wheel event.
    private int _lastWheelDirectionSource;
    private const int WheelDirectionHoldMs = 350;
    private int _wheelLogCount;
    private int _horizontalScrollbarDragActive;
    private int _horizontalScrollbarDragLastX;
    private int _lastHorizontalScrollbarDragDir;
    private long _lastHorizontalScrollbarDragDirectionTick;
    private const int ScrollbarDragDirectionHoldMs = 900;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private int _scrollbarDragLogCount;
    private int _stitchDecisionLogCount;
    private int _consecutiveIdleFrames;
    private long _lastStatusUpdateTick;
    private const int StatusUpdateIntervalMs = 120;
    // hint 最短驻留时长：UpdateStatus 周期刷新时若 hint 未到驻留期则保留原文本，
    // 否则任何提示（含工具栏按钮悬浮说明）会在下一次刷新（约120ms）被立即抹掉。
    private long _hintShownTick;
    private long _realignmentHintTick;
    private const int HintMinHoldMs = 5000;
    private const int RealignmentHintHoldMs = 2000;

    private long _lastPreviewUpdateTick;
    // Throttled-away preview updates raise this flag; the capture loop's idle branch
    // flushes it so the preview converges to the final state after scrolling stops.
    private int _previewUpdatePending;
    // Fixed-layer content hashes (dirty flags): skip clone/replace when unchanged.
    private long _topBoundaryFixedHash;
    private long _bottomBoundaryFixedHash;
    // Cross-axis static band cuts for horizontal captures. The viewport-fixed
    // header/footer (sticky bars, frosted overlays and the shadow they cast over
    // scrolling content) would otherwise be baked into every full-height strip and
    // show up as a shadow-like band across the top/bottom of the composed image.
    private int _crossTopCut;
    private int _crossBottomCut;
    private bool _crossBandsLocked;
    private const int PreviewUpdateIntervalMs = 16;

    private const int BottomReachedThreshold = 12;
    private volatile bool _bottomReached;

    private int _autoScrollDirection = 1;
    // 用户通过右键/方向 chip 显式设定过方向：开启时尊重预设，不再被引擎实际方向覆盖。
    private volatile bool _autoScrollDirectionPreset;

    private int _pendingManualOffset;
    private int _manualOffsetDragging;
    private volatile bool _manualOffsetJustApplied;

    private Bitmap? _prevWorkBuffer;
    private Bitmap? _curWorkBuffer;

    private const int MaxPoolSize = 8;

    private System.Threading.Timer? _autoScrollTimer;
    private volatile bool _autoScrollEnabled;
    private int _autoScrollIntervalMs = 70;
    private int _autoScrollTickRunning;
    private int _autoScrollInjectionSerial;
    private int _autoScrollLastResponseSerial;
    private int _autoScrollResponseCount;
    private long _autoScrollLastResponseTick;
    private int _autoScrollInjectionLogCount;
    private int _autoScrollResponseLogCount;
    private int _autoScrollCursorOutsideLogCount;
    // A horizontal canvas can legitimately start with a long, nearly featureless
    // stretch (Photoshop gradients are the common case).  Ten wheel injections is
    // enough to prove a normal vertical page did not respond, but is far too short
    // to reach the first reliable horizontal anchor.
    private const int VerticalAutoScrollNoResponseInjectionThreshold = 10;
    private const int HorizontalAutoScrollNoResponseInjectionThreshold = 80;
    private int _captureResourcesDirty;

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
            return LongScrollResult.Cancel("长截图发生异常：");
        }
        try
        {
            _options.Validate();
        }
        catch (Exception ex)
        {
            return LongScrollResult.Cancel("长截图发生异常：" + ex.Message);
        }

        AppLogger.Info("LegacyLongScrollSession.RunAsync: 启动会话 axis=" + _options.Axis
            + ", Region=" + _region
            + ", Preview=" + EnablePreviewWindow
            + ", Border=" + EnableBorderWindow
            + ", MouseHookRequested=" + EnableGlobalMouseHook
            + ", AutoScrollAvailable=" + EnableAutoScroll
            + ", AutoScrollEnabled=" + _autoScrollEnabled
            + ", CaptureInterval=" + _options.CaptureIntervalMs + "ms");

        // 上限在这条日志里一次性锁死并留痕：maxAlong 是会话全程唯一生效的值，
        // 排查"为什么滚到 N 万像素就停"只需要比对 availPhys/budget/maxAlong 三个数。
        long availPhysAtStart = LongScrollBudget.ReadAvailableBytes();
        AppLogger.Info("LongScroll.Budget axis=" + _options.Axis
            + ", cross=" + LongScrollBudget.CrossLength(_options.Axis, _region) + "px"
            + ", availPhys=" + LongScrollBudget.ToMb(availPhysAtStart) + "MB"
            + ", budget=" + LongScrollBudget.ToMb(LongScrollBudget.SessionBudgetBytes(availPhysAtStart)) + "MB"
            + ", fixedReserved=" + (2 * _options.MaxFixedLayerAlongLengthPx) + "px"
            + ", maxAlong=" + _options.MaxAlongLengthPx + "px");
        _completionTcs = new TaskCompletionSource<LongScrollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 字节预算限制队列容量，正常采集另由两帧前瞻控制节拍，不能把容量当作追赶历史的目标。
        long frameBytes = Math.Max(1L, (long)_region.Width * _region.Height * 4);
        int queueCapacity = (int)Math.Clamp(CaptureQueueBudgetBytes / frameBytes, 8L, 64L);
        _captureQueue = new BlockingCollection<CapturedFrame>(queueCapacity);
        AppLogger.Info("LongScroll.CaptureQueue: capacity=" + queueCapacity + ", lookahead=" + CaptureLookaheadFrames + ", frameBytes=" + frameBytes);
        _bitmapPool = new ConcurrentBag<Bitmap>();
        _cts = new CancellationTokenSource();

        for (int i = 0; i < 4; i++)
        {
            ReturnToPool(new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb));
        }

        LongScrollMemoryDiagnostics.LogImage("LongScroll.Start", _region.Width, 0);

        try
        {
            _controlWindow = new LongScrollControlWindow
            {
                RequestedThemeVariant = _options.IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            _controlWindow.ConfigureAxis(_options.Axis);
            _controlWindow.ConfigureCompletionShortcuts(_options.CancelShortcut.DisplayText, _options.FinishShortcut.DisplayText);
            _controlWindow.SetRegionInfo(_region);
            _controlWindow.SetStatus(FormatOutputSize(GetDisplayHeight()));
            _controlWindow.UpdateMetrics(GetDisplayHeight(), 0);
            _controlWindow.SetAutoScrollIntervalMs(_autoScrollIntervalMs);

            _controlWindow.FinishRequested += async (_, _) => await FinishInternalAsync(true, null, false);
            _controlWindow.StickerRequested += async (_, _) => await FinishAsStickerAsync();
            _controlWindow.CancelRequested += async (_, _) => await FinishInternalAsync(false, null, true);
            _controlWindow.QuickSaveRequested += async (_, _) => await QuickSaveAsync();
            _controlWindow.SaveAsRequested += async (_, _) => await SaveAsAsync();
            _controlWindow.AutoScrollRequested += (_, _) => OnAutoScrollClicked();
            _controlWindow.AxisToggleRequested += (_, _) => OnAxisToggleClicked();
            _controlWindow.AutoScrollIntervalWheel += (_, delta) => OnAutoScrollIntervalWheel(delta);
            _controlWindow.AutoScrollDirectionFlipRequested += (_, _) => OnAutoScrollDirectionFlip();
            _controlWindow.ManualOffsetStarted += (_, _) => OnManualOffsetStarted();
            _controlWindow.ManualOffsetChanged += (_, totalDelta) => OnManualOffset(totalDelta);
            _controlWindow.ManualOffsetReleased += (_, _) => OnManualOffsetReleased();
            _controlWindow.Opened += (_, _) =>
            {
                try
                {
                    PlaceControlWindow();
                    Dispatcher.UIThread.Post(PlaceControlWindow, DispatcherPriority.Loaded);
                    Dispatcher.UIThread.Post(PlaceControlWindow, DispatcherPriority.Render);
                    WindowHelper.MakeToolWindowNoActivate(GetWindowHwnd(_controlWindow), transparent: false);
                    WindowHelper.EnforceTopmost(GetWindowHwnd(_controlWindow));
                }
                catch { }
            };
            _controlWindow.LayoutUpdated += (_, _) => PlaceControlWindow();
            _controlWindow.Closed += async (_, _) =>
            {
                try { WindowHelper.StopEnforceTopmost(GetWindowHwnd(_controlWindow)); } catch { }

                if (Volatile.Read(ref _finishStarted) == 0)
                {
                    await FinishInternalAsync(false, null, true);
                }
            };

            if (EnableBorderWindow)
            {
                _nativeOverlay = new NativeLongScrollOverlay(_region, _options.Axis);
            }
            else
            {
                _nativeOverlay = null;
                AppLogger.Warn("LegacyLongScrollSession: 捕获窗口已退出（滚轮/快捷键/关闭），停止捕获");
            }

            if (EnablePreviewWindow)
            {
                _previewWindow = new LongScrollPreviewWindow(_region, _options.Axis);
            }
            else
            {
                _previewWindow = null;
            }

            SeedInitialFrame();

            _isRunning = true;
            StartCompletionShortcuts();
            if (EnableGlobalMouseHook)
            {
                InstallMouseHook();
            }
            else
            {
                AppLogger.Warn("LegacyLongScrollSession: 置前捕获窗口失败，可能混入其他窗口内容");
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
            AppLogger.Info("LegacyLongScrollSession.RunAsync: 捕获线程已启动 priority=" + _captureThread.Priority + ", TargetInterval>= " + SafeCaptureIntervalMs + "ms");
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

            if (_options.VerificationAutoFinishAfterMs > 0)
            {
                int delayMs = _options.VerificationAutoFinishAfterMs;
                AppLogger.Info("LongScroll.VerificationAutoFinish scheduled after " + delayMs + "ms");
                DispatcherTimer.RunOnce(
                    async () => await FinishInternalAsync(true, null, false),
                    TimeSpan.FromMilliseconds(delayMs));
            }

            LongScrollResult result = await _completionTcs.Task;
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LegacyLongScrollSession.RunAsync 异常", ex);
            await CleanupAfterFinishAsync();
            return LongScrollResult.Cancel("长截图发生异常：" + ex.Message);
        }
    }

    private void ApplyLongScrollWindowZOrder()
    {
        if (Volatile.Read(ref _saveDialogOpen) != 0) return;
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

            // 种子帧与后续帧必须同一颜色口径，设备和白电平在捕获线程中继续复用。
            _hdrCapture = HdrScreenCapture.TryCreate(_region);
            if (_hdrCapture != null && NativeMethods.GdiFlush())
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
                try { _hdrCapture.TryCopyTo(data.Scan0, data.Stride); }
                finally { bmp.UnlockBits(data); }
            }

            _captureQueue.TryAdd(new CapturedFrame((Bitmap)bmp.Clone(), Stopwatch.GetTimestamp()));
        }
        catch { }
    }

    private void OnAutoScrollClicked()
    {
        if (!EnableAutoScroll)
        {
            try { _controlWindow?.SetStatus(FormatOutputSize(GetDisplayHeight())); } catch { }
            AppLogger.Warn("LegacyLongScrollSession: 捕获窗口句柄已失效");
            return;
        }

        try
        {
            if (!_autoScrollEnabled)
            {
                // 尊重右键/方向 chip 的显式预设；未预设时跟随引擎实际采集方向
                //（用户开启前手动滚动过即已确定）。
                if (!_autoScrollDirectionPreset && _captureDirection != 0)
                {
                    _autoScrollDirection = _captureDirection < 0 ? -1 : 1;
                }
                _bottomReached = false;
            }
            SetAutoScrollEnabled(!_autoScrollEnabled);
        }
        catch (Exception ex) { AppLogger.Warn("LegacyLongScrollSession: OnAutoScrollClicked 异常: " + ex.Message); }
    }

    /// <summary>
    /// 翻转自动滚动方向：未开启时设定待用方向；运行中实时反转注入方向
    ///（拼接引擎本身动态跟随画面方向，无需额外切换）。
    /// </summary>
    private void OnAutoScrollDirectionFlip()
    {
        try
        {
            _autoScrollDirection = _autoScrollDirection < 0 ? 1 : -1;
            _autoScrollDirectionPreset = true;
            if (_autoScrollEnabled)
            {
                // 运行中翻转：重置触底判定与响应计时，让新方向立刻注入并重给响应窗口。
                _bottomReached = false;
                Interlocked.Exchange(ref _autoScrollLastResponseTick, Stopwatch.GetTimestamp());
            }
            AppLogger.Info("LongScroll.AutoScrollDirectionFlip: direction="
                + (_autoScrollDirection < 0 ? "Leading" : "Trailing")
                + ", running=" + _autoScrollEnabled);
            Dispatcher.UIThread.Post(() =>
            {
                try { _controlWindow?.SetAutoScrollDirection(_autoScrollDirection); } catch { }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex) { AppLogger.Warn("LegacyLongScrollSession: OnAutoScrollDirectionFlip 异常: " + ex.Message); }
    }

    private void SetAutoScrollEnabled(bool enabled)
    {
        bool wasEnabled = _autoScrollEnabled;
        _autoScrollEnabled = enabled;
        AppLogger.Info("LongScroll.AutoScrollState: enabled=" + enabled
            + ", previous=" + wasEnabled
            + ", axis=" + _options.Axis
            + ", direction=" + (_autoScrollDirection < 0 ? "Leading" : "Trailing")
            + ", interval=" + _autoScrollIntervalMs + "ms");
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _controlWindow?.SetAutoScrollEnabled(enabled);
                    _controlWindow?.SetAutoScrollIntervalMs(_autoScrollIntervalMs);
                    _controlWindow?.SetAutoScrollDirection(_autoScrollDirection);
                    PlaceControlWindow();
                }
                catch { }
            }, DispatcherPriority.Background);
        }
        catch { }

        if (enabled)
        {
            Interlocked.Exchange(ref _autoScrollInjectionSerial, 0);
            Interlocked.Exchange(ref _lastAutoScrollInjectionTick, 0);
            Interlocked.Exchange(ref _autoScrollLastResponseSerial, 0);
            Interlocked.Exchange(ref _autoScrollResponseCount, 0);
            Interlocked.Exchange(ref _autoScrollLastResponseTick, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _autoScrollInjectionLogCount, 0);
            Interlocked.Exchange(ref _autoScrollResponseLogCount, 0);
            Interlocked.Exchange(ref _autoScrollCursorOutsideLogCount, 0);
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
        bool wasEnabled = _autoScrollEnabled;
        _autoScrollEnabled = false;
        if (wasEnabled)
        {
            AppLogger.Info("LongScroll.AutoScrollState: enabled=False, previous=True, axis="
                + _options.Axis + ", reason=internal-stop, injections="
                + Volatile.Read(ref _autoScrollInjectionSerial) + ", responses="
                + Volatile.Read(ref _autoScrollResponseCount));
        }
        try { _autoScrollTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _controlWindow?.SetAutoScrollEnabled(false);
                    _controlWindow?.SetAutoScrollIntervalMs(_autoScrollIntervalMs);
                }
                catch { }
            }, DispatcherPriority.Background);
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

    private void OnAxisToggleClicked()
    {
        if (Volatile.Read(ref _finishStarted) != 0) return;
        ScrollAxis newAxis = _options.Axis == ScrollAxis.Vertical ? ScrollAxis.Horizontal : ScrollAxis.Vertical;
        SwitchAxis(newAxis);
    }

    private void SwitchAxis(ScrollAxis newAxis)
    {
        if (_options.Axis == newAxis) return;
        AppLogger.Info("LegacyLongScrollSession.SwitchAxis: 从 " + _options.Axis + " 切换到 " + newAxis);

        StopAutoScrollInternal();
        _options.Axis = newAxis;
        _controlWindow?.ConfigureAxis(newAxis);
        _previewWindow?.ConfigureAxis(newAxis);
        _nativeOverlay?.ResetSelection(newAxis);

        lock (_stripsLock)
        {
            if (_baseStrip != null && _baseStrip.Bitmap != null)
            {
                Bitmap baseBmp = _baseStrip.Bitmap;
                for (int i = 0; i < _strips.Count; i++)
                {
                    if (!ReferenceEquals(_strips[i], _baseStrip))
                    {
                        RetireStripLocked(_strips[i]);
                    }
                }
                _strips.Clear();
                StitchedStrip newBase = CreateStrip(baseBmp, 0);
                _strips.Add(newBase);
                _baseStrip = newBase;
                // 画布已重置回基准帧：上一轮"最后追加的那条"不再是任何东西的前一条，
                // 留着会让重置后的第一次追加与它误比（2026-10-01 重复守卫）
                _lastAppendedFingerprint = null;
                _duplicateAppendBlocked = 0;
                _fixedTopHeight = 0;
                _fixedBottomHeight = 0;
                _crossTopCut = 0;
                _crossBottomCut = 0;
                _crossBandsLocked = false;
                RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
                RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
                _topBoundaryFixedBitmap = null;
                _bottomBoundaryFixedBitmap = null;
                _totalHeight = GetAlongLength(baseBmp);
                _currentViewportY = 0;
                _canvasTopY = 0;
                _canvasBottomY = _totalHeight;
                _viewportHeight = _totalHeight;
                _captureDirection = 0;
            }
        }

        _manualOffsetJustApplied = true;
        _capturedFrameCount = 0;
        Interlocked.Exchange(ref _lastPreviewUpdateTick, 0);
        UpdateStatus();
        try { PlaceControlWindow(); } catch { }
        UpdatePreview();
    }

    /// <summary>
    /// </summary>
    private void OnManualOffsetStarted()
    {
        _pendingManualOffset = 0;
        Interlocked.Exchange(ref _manualOffsetDragging, 1);
        StopAutoScrollInternal();
        _nativeOverlay?.SetScrollDirection(_captureDirection);
        _nativeOverlay?.ResetSelection(_options.Axis);

        if (_captureQueue != null)
        {
            while (_captureQueue.TryTake(out CapturedFrame? queued))
            {
                ReturnToPool(queued.Bitmap);
            }
        }
    }

    /// <summary>
    /// </summary>
    private void OnManualOffset(int totalDelta)
    {
        if (Volatile.Read(ref _manualOffsetDragging) == 0) return;
        _nativeOverlay?.SetScrollDirection(_captureDirection);
        int appliedOffset = _nativeOverlay?.ShiftSelection(_options.Axis, totalDelta) ?? totalDelta;
        _pendingManualOffset = appliedOffset;
        UpdatePreviewDragSuppression(appliedOffset);
    }

    /// <summary>
    ///
    ///
    ///
    /// </summary>
    private void OnManualOffsetReleased()
    {
        int totalDelta = _nativeOverlay?.GetSelectionShift(_options.Axis) ?? _pendingManualOffset;
        _pendingManualOffset = 0;

        if (totalDelta == 0)
        {
            _nativeOverlay?.ResetSelection(_options.Axis);
            _manualOffsetJustApplied = true;
            Interlocked.Exchange(ref _manualOffsetDragging, 0);
            return;
        }

        int actualTopCrop = 0;
        int actualBottomCrop = 0;
        bool changed = false;

        bool isScrollUp = _captureDirection < 0;
        bool isCrop = isScrollUp ? (totalDelta > 0) : (totalDelta < 0);

        try
        {
            lock (_stripsLock)
            {
                if (_strips.Count == 0 && _topBoundaryFixedBitmap == null && _bottomBoundaryFixedBitmap == null)
                {
                    _nativeOverlay?.ResetSelection(_options.Axis);
                    return;
                }

                if (!isCrop)
                {
                    if (isScrollUp)
                    {
                        int absDelta = -totalDelta;
                        _currentViewportY -= absDelta;
                        actualTopCrop = -absDelta;
                    }
                    else
                    {
                        _currentViewportY += totalDelta;
                        actualBottomCrop = -totalDelta;
                    }
                    changed = true;

                    int displayH = GetDisplayHeight();
                    AppLogger.Info("ManualOffset: extend-" + (isScrollUp ? "top" : "bottom") + " delta=" + Math.Abs(totalDelta) + ", viewportY=" + _currentViewportY + ", displayH=" + displayH);
                }
                else if (isScrollUp && totalDelta > 0)
                {
                    int remainCrop = totalDelta;

                    if (_topBoundaryFixedBitmap != null && _fixedTopHeight > 0)
                    {
                        int cropFromFixed = Math.Min(remainCrop, _fixedTopHeight);
                        int newH = _fixedTopHeight - cropFromFixed;
                        if (newH > 0)
                        {
                            Bitmap newFixed = CloneAlongBitmap(_topBoundaryFixedBitmap, cropFromFixed, newH);
                            RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
                            _topBoundaryFixedBitmap = newFixed;
                        }
                        else
                        {
                            RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
                            _topBoundaryFixedBitmap = null;
                        }
                        _fixedTopHeight = newH;
                        remainCrop -= cropFromFixed;
                    }

                    if (remainCrop > 0 && _strips.Count > 0)
                    {
                        int compBottom = CalculateCompositedHeight(_strips, _totalHeight);
                        int maxCrop = compBottom - 10;
                        int cropLine = Math.Min(remainCrop, maxCrop);

                        if (cropLine > 0)
                        {
                            for (int i = _strips.Count - 1; i >= 0; i--)
                            {
                                var s = _strips[i];
                                if (s.AlongEnd <= cropLine)
                                {
                                    RetireStripLocked(s);
                                    _strips.RemoveAt(i);
                                }
                                else if (s.AlongStart < cropLine)
                                {
                                    int trimTop = cropLine - s.AlongStart;
                                    int newH = s.AlongLength - trimTop;
                                    Bitmap newBmp = CloneAlongBitmap(s.Bitmap, trimTop, newH);
                                    int newOverlay = Math.Max(0, s.OverlaySize - trimTop);
                                    RetireStripLocked(s);
                                    _strips[i] = CreateStrip(newBmp, cropLine, newOverlay);
                                }
                            }

                            foreach (var s in _strips)
#pragma warning disable CS0618
                                s.OffsetY(-cropLine);
#pragma warning restore CS0618

                            _currentViewportY -= cropLine;
                            if (_currentViewportY < _canvasTopY) _currentViewportY = _canvasTopY;

                            remainCrop -= cropLine;
                        }
                    }

                    actualTopCrop = totalDelta - remainCrop;
                    changed = actualTopCrop > 0;

                    if (!changed)
                    {
                        _nativeOverlay?.ResetSelection(_options.Axis);
                        return;
                    }

                    _totalHeight = CalculateCompositedHeight(_strips, _totalHeight);
                    _canvasBottomY = _canvasTopY + _totalHeight;

                    int displayH = _totalHeight + _fixedTopHeight + _fixedBottomHeight;
                    AppLogger.Info("ManualOffset: trim-top crop=" + actualTopCrop + " (fixed+strips), remain=" + remainCrop + ", displayH=" + displayH + " (strips=" + _totalHeight + "+fixed=" + (_fixedTopHeight + _fixedBottomHeight) + ")");
                }
                else if (!isScrollUp && totalDelta < 0)
                {
                    int absDelta = -totalDelta;
                    int remainCrop = absDelta;

                    if (_bottomBoundaryFixedBitmap != null && _fixedBottomHeight > 0)
                    {
                        int cropFromFixed = Math.Min(remainCrop, _fixedBottomHeight);
                        int newH = _fixedBottomHeight - cropFromFixed;
                        if (newH > 0)
                        {
                            Bitmap newFixed = CloneAlongBitmap(_bottomBoundaryFixedBitmap, 0, newH);
                            RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
                            _bottomBoundaryFixedBitmap = newFixed;
                        }
                        else
                        {
                            RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
                            _bottomBoundaryFixedBitmap = null;
                        }
                        _fixedBottomHeight = newH;
                        remainCrop -= cropFromFixed;
                    }

                    if (remainCrop > 0 && _strips.Count > 0)
                    {
                        int currentBottom = CalculateCompositedHeight(_strips, _totalHeight);
                        int newBottom = Math.Max(10, currentBottom - remainCrop);
                        int actualStripCrop = currentBottom - newBottom;

                        if (actualStripCrop > 0)
                        {
                            for (int i = _strips.Count - 1; i >= 0; i--)
                            {
                                var s = _strips[i];
                                if (s.AlongStart >= newBottom)
                                {
                                    RetireStripLocked(s);
                                    _strips.RemoveAt(i);
                                }
                                else if (s.AlongEnd > newBottom)
                                {
                                    int newH = newBottom - s.AlongStart;
                                    Bitmap newBmp = CloneAlongBitmap(s.Bitmap, 0, newH);
                                    RetireStripLocked(s);
                                    _strips[i] = CreateStrip(newBmp, s.AlongStart, Math.Min(s.OverlapLength, newH));
                                }
                            }

                            remainCrop -= actualStripCrop;
                        }
                    }

                    actualBottomCrop = absDelta - remainCrop;
                    changed = actualBottomCrop > 0;

                    if (!changed)
                    {
                        _nativeOverlay?.ResetSelection(_options.Axis);
                        return;
                    }

                    _totalHeight = CalculateCompositedHeight(_strips, _totalHeight);
                    _canvasBottomY = _canvasTopY + _totalHeight;
                    ClampCurrentViewportLocked();

                    int displayH2 = _totalHeight + _fixedTopHeight + _fixedBottomHeight;
                    AppLogger.Info("ManualOffset: trim-bottom crop=" + actualBottomCrop + " (fixed+strips), remain=" + remainCrop + ", displayH=" + displayH2 + " (strips=" + _totalHeight + "+fixed=" + (_fixedTopHeight + _fixedBottomHeight) + ")");
                }
            }

            if (changed)
            {
                _nativeOverlay?.CommitCrop(_options.Axis, actualTopCrop, actualBottomCrop);
                ApplyCaptureRegionCrop(actualTopCrop, actualBottomCrop);

                UpdatePreview();
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LegacyLongScrollSession.OnManualOffsetReleased 异常: " + ex.Message);
            _nativeOverlay?.ResetSelection(_options.Axis);
        }
        finally
        {
            _manualOffsetJustApplied = true;
            SetPreviewDragSuppression(false);
            Interlocked.Exchange(ref _manualOffsetDragging, 0);
        }
    }

    private void UpdatePreviewDragSuppression(int appliedOffset)
    {
        if (_previewWindow == null) return;
        Rectangle previewRect = _previewWindow.GetHostScreenRect();
        if (previewRect.Width <= 0 || previewRect.Height <= 0) return;
        Rectangle selection = _region;
        if (_options.Axis == ScrollAxis.Horizontal) selection.Offset(appliedOffset, 0);
        else selection.Offset(0, appliedOffset);
        SetPreviewDragSuppression(selection.IntersectsWith(previewRect));
    }

    private void SetPreviewDragSuppression(bool suppressed)
    {
        if (_previewWindow == null || _previewSuppressedForDrag == suppressed) return;
        _previewSuppressedForDrag = suppressed;
        _previewWindow.SetSuppressedForSelectionDrag(suppressed);
    }

    private void ApplyCaptureRegionCrop(int leadingDelta, int trailingDelta)
    {
        if (leadingDelta == 0 && trailingDelta == 0) return;

        Rectangle oldRegion = _region;
        int oldAlong = _options.Axis == ScrollAxis.Horizontal ? oldRegion.Width : oldRegion.Height;
        int newAlong = oldAlong - leadingDelta - trailingDelta;
        if (newAlong < 2)
        {
            AppLogger.Warn("ManualOffset: 手动偏移已调整为 " + newAlong + "px");
            return;
        }

        Rectangle newRegion = _options.Axis == ScrollAxis.Horizontal
            ? new Rectangle(oldRegion.X + leadingDelta, oldRegion.Y, newAlong, oldRegion.Height)
            : new Rectangle(oldRegion.X, oldRegion.Y + leadingDelta, oldRegion.Width, newAlong);
        if (newRegion == oldRegion) return;

        _region = newRegion;
        _viewportHeight = Math.Max(1, newAlong - _fixedTopHeight - _fixedBottomHeight);
        Interlocked.Exchange(ref _captureResourcesDirty, 1);
        Volatile.Write(ref _capturePixelsValid, false);

        if (_captureQueue != null)
        {
            while (_captureQueue.TryTake(out CapturedFrame? queued)) ReturnToPool(queued.Bitmap);
        }

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _controlWindow?.SetRegionInfo(newRegion);
                    PlaceControlWindow();
                }
                catch { }
            }, DispatcherPriority.Background);
        }
        catch { }

        AppLogger.Info("ManualOffset: capture-region " + oldRegion + " -> " + newRegion + ", axis=" + _options.Axis);
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
        if (Volatile.Read(ref _saveDialogOpen) != 0) return;
        if (!_autoScrollEnabled) return;
        if (!_isRunning) return;
        if (Volatile.Read(ref _finishStarted) != 0) return;

        if (_bottomReached)
        {
            SetAutoScrollEnabled(false);
            try { _controlWindow?.SetStatus(FormatOutputSize(GetDisplayHeight()) + "，滚动以开始捕获"); } catch { }
            return;
        }

        if (_maxHeightReached || _totalHeight >= _options.MaxAlongLengthPx)
        {
            ShowMaxHeightNotice();
            return;
        }

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            AppLogger.Warn("LongScroll.AutoScrollTick: GetCursorPos failed; no input injected.");
            return;
        }
        if (!_region.Contains(pt.X, pt.Y))
        {
            int outsideCount = Interlocked.Increment(ref _autoScrollCursorOutsideLogCount);
            if (outsideCount <= 3 || outsideCount % 25 == 0)
            {
                AppLogger.Info("LongScroll.AutoScrollTick: cursor outside capture region, injection paused. count="
                    + outsideCount + ", cursor=(" + pt.X + "," + pt.Y + "), region=" + _region);
            }
            return;
        }
        Interlocked.Exchange(ref _autoScrollCursorOutsideLogCount, 0);

        int injectionSerial = Volatile.Read(ref _autoScrollInjectionSerial);
        int lastResponseSerial = Volatile.Read(ref _autoScrollLastResponseSerial);
        long lastResponseTick = Interlocked.Read(ref _autoScrollLastResponseTick);
        long noResponseTicks = Stopwatch.GetTimestamp() - lastResponseTick;
        long minimumWaitTicks = Stopwatch.Frequency * Math.Max(1200, _autoScrollIntervalMs * 4) / 1000;
        int noResponseLimit = _options.Axis == ScrollAxis.Horizontal
            ? HorizontalAutoScrollNoResponseInjectionThreshold
            : VerticalAutoScrollNoResponseInjectionThreshold;
        if (injectionSerial - lastResponseSerial >= noResponseLimit
            && noResponseTicks >= minimumWaitTicks)
        {
            bool everResponded = Volatile.Read(ref _autoScrollResponseCount) > 0;
            StopAutoScrollInternal();
            UpdateStatusHint(
                everResponded
                    ? "自动滚动已停止：窗口不再响应滚动注入，已拼接内容将保留"
                    : "自动滚动已停止：目标窗口未响应滚动，请改用手动滚动",
                true);
            return;
        }

        // 有在途帧或尚未恢复对齐时先消费/重试，避免继续滚动把可恢复的重叠区冲掉。
        if (Volatile.Read(ref _alignmentPending) != 0)
        {
            long pendingSince = Interlocked.Read(ref _alignmentPendingSinceTick);
            if (pendingSince != 0 && Stopwatch.GetTimestamp() - pendingSince > Stopwatch.Frequency * 3)
            {
                StopAutoScrollInternal();
                UpdateStatusHint("当前画面暂时无法对齐，请向回滚动少许补齐，已拼接内容保留", true, realigning: true);
            }
            return;
        }
        if (ShouldWaitForAutoScroll(Stopwatch.GetTimestamp())) return;

        // A low-texture frame is never committed without image evidence. Keep an
        // explicitly enabled controlled pan alive until a verifiable overlap enters
        // the viewport, and explain why the canvas has not grown yet.
        if (_options.Axis == ScrollAxis.Horizontal
            && Volatile.Read(ref _autoScrollResponseCount) == 0
            && injectionSerial > 0
            && injectionSerial % 10 == 0)
        {
            UpdateStatusHint("自动滚动进行中：请勿操作鼠标键盘，工具正在自动滚动页面", false);
        }

        int delta = _options.Axis == ScrollAxis.Horizontal
            ? (_autoScrollDirection > 0 ? 240 : -240)
            : (_autoScrollDirection > 0 ? -120 : 120);
        uint wheelFlag = _options.Axis == ScrollAxis.Horizontal
            ? NativeMethods.MOUSEEVENTF_HWHEEL
            : NativeMethods.MOUSEEVENTF_WHEEL;
        try
        {
            if (Volatile.Read(ref _saveDialogOpen) != 0) return;
            Interlocked.Exchange(ref _lastAutoScrollInjectionTick, Stopwatch.GetTimestamp());
            NativeMethods.mouse_event(wheelFlag, 0, 0, delta, IntPtr.Zero);
            int serial = Interlocked.Increment(ref _autoScrollInjectionSerial);
            int logCount = Interlocked.Increment(ref _autoScrollInjectionLogCount);
            if (logCount <= 5 || logCount % 25 == 0)
            {
                AppLogger.Info("LongScroll.AutoScrollInject: serial=" + serial
                    + ", axis=" + _options.Axis
                    + ", direction=" + (_autoScrollDirection < 0 ? "Leading" : "Trailing")
                    + ", wheelDelta=" + delta
                    + ", cursor=(" + pt.X + "," + pt.Y + ")");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LongScroll.AutoScrollInject failed: " + ex.Message);
        }
    }

    private bool ShouldWaitForAutoScroll(long nowTick)
    {
        if (Volatile.Read(ref _alignmentPending) != 0 || Volatile.Read(ref _stitchFrameInProgress) != 0
            || (_captureQueue?.Count ?? 0) > 0) return true;
        long changed = Interlocked.Read(ref _lastCaptureChangeTick);
        long stationary = Interlocked.Read(ref _lastStationaryViewportTick);
        bool animatedIdle = _autoScrollEnabled && stationary != 0 && nowTick >= stationary
            && nowTick - stationary <= Stopwatch.Frequency / 4
            && stationary >= changed
            && stationary >= Interlocked.Read(ref _lastMotionTick);
        // GIF 不必完全停播；只有近期、原位纹理证据确认视口静止时绕过整帧像素静默等待。
        if (!animatedIdle && changed != 0 && nowTick - changed < Stopwatch.Frequency * 80 / 1000) return true;
        long injected = Interlocked.Read(ref _lastAutoScrollInjectionTick);
        if (injected == 0) injected = Interlocked.Read(ref _autoScrollLastResponseTick);
        return Volatile.Read(ref _autoScrollInjectionSerial) > Volatile.Read(ref _autoScrollLastResponseSerial)
            && nowTick - injected < Stopwatch.Frequency / 4;
    }

    // 不丢已捕获的连续帧；下游繁忙时在下一次 BitBlt 前等待，避免预览消费半秒前的积压。
    private bool ShouldWaitForCapture() => (_captureQueue?.Count ?? 0) >= CaptureLookaheadFrames;

    // 值类型作用域不分配堆对象，所有continue/异常路径统一释放在途状态。
    private readonly struct FrameProcessingScope : IDisposable
    {
        private readonly LegacyLongScrollSession _owner;
        private readonly long _started;
        public FrameProcessingScope(LegacyLongScrollSession owner, long capturedAt)
        {
            _owner = owner;
            _started = Stopwatch.GetTimestamp();
            long queueTicks = Math.Max(0, _started - capturedAt);
            owner._queueTicks += queueTicks;
            owner._maxQueueTicks = Math.Max(owner._maxQueueTicks, queueTicks);
            Volatile.Write(ref owner._stitchFrameInProgress, 1);
        }
        public void Dispose()
        {
            _owner._processedFrames++;
            _owner._processingTicks += Stopwatch.GetTimestamp() - _started;
            Volatile.Write(ref _owner._stitchFrameInProgress, 0);
        }
    }

    private void SetAlignmentPending()
    {
        Volatile.Write(ref _alignmentPending, 1);
        Interlocked.CompareExchange(ref _alignmentPendingSinceTick, Stopwatch.GetTimestamp(), 0);
    }

    private void MarkAutoScrollResponse()
    {
        if (!_autoScrollEnabled) return;
        int serial = Volatile.Read(ref _autoScrollInjectionSerial);
        Interlocked.Exchange(ref _autoScrollLastResponseSerial, serial);
        Interlocked.Exchange(ref _autoScrollLastResponseTick, Stopwatch.GetTimestamp());
        int responseCount = Interlocked.Increment(ref _autoScrollResponseCount);
        int logCount = Interlocked.Increment(ref _autoScrollResponseLogCount);
        if (logCount <= 5 || logCount % 25 == 0)
        {
            AppLogger.Info("LongScroll.AutoScrollResponse: count=" + responseCount
                + ", injectionSerial=" + serial + ", total=" + _totalHeight);
        }
    }

    private void PlaceControlWindow()
    {
        if (Volatile.Read(ref _saveDialogOpen) != 0) return;
        try
        {
            if (_controlWindow == null) return;
            double scale = _controlWindow.DesktopScaling > 0 ? _controlWindow.DesktopScaling : 1.0;
            int ww = (int)Math.Round(Math.Max(1, _controlWindow.Bounds.Width) * scale);
            int wh = (int)Math.Round(Math.Max(1, _controlWindow.Bounds.Height) * scale);
            if (ww <= 1) ww = (int)Math.Round(Math.Max(1, _controlWindow.Width) * scale);
            if (wh <= 1) wh = (int)Math.Round(Math.Max(1, _controlWindow.Height) * scale);

            // 尺寸抖动量化：SizeToContent + DPI 缩放下 Bounds 存在亚像素抖动（如
            // 439.99/440.01 取整来回跳），与上次尺寸差 <2px 时复用旧值，避免
            // 每 120ms 的状态刷新反复触发 SetWindowPos 移动窗口、顶掉按钮 Tooltip。
            if (_hasLastControlWindowRect
                && Math.Abs(ww - _lastControlWindowW) < 2
                && Math.Abs(wh - _lastControlWindowH) < 2)
            {
                ww = _lastControlWindowW;
                wh = _lastControlWindowH;
            }

            Rectangle vs = MonitorHelper.GetVirtualScreenRect();
            // 间距与主工具栏一致：下缘 gap=10、边缘留白 edge=12。
            int gap = 10;
            int edge = 12;
            // 与 LongScrollControlWindow.axaml 外层 Border 的 Margin="14,14,14,20" 一致，
            // 这些是为圆角+阴影保留的透出空间。对齐基准是“可视面板”（窗口去掉这圈留白后的内容）。
            int shTop = 14, shRight = 14, shBottom = 20, shLeft = 14;
            int pbh = Math.Max(1, wh - shTop - shBottom);
            int pbw = Math.Max(1, ww - shLeft - shRight);

            bool horizontal = _options.Axis == ScrollAxis.Horizontal;

            // 确定性分工：预览窗进空间大的一侧，工具栏进空间小的一侧，互不抢位。
            // 用迟滞阈值避免临界持平时的来回横跳。
            const int smallSideThreshold = 40;
            int freeBelow = vs.Bottom - _region.Bottom;
            int freeAbove = _region.Top - vs.Top;
            bool preferBelow;
            if (freeAbove >= freeBelow + smallSideThreshold)
            {
                preferBelow = true; // 上方空间大→预览去上，工具栏放选区下方
            }
            else if (freeBelow >= freeAbove + smallSideThreshold)
            {
                preferBelow = false; // 下方空间大→预览去下，工具栏放选区上方
            }
            else
            {
                preferBelow = horizontal ? _horizontalControlPreferBelow : _verticalControlPreferBelow;
            }

            // 面板右缘默认对齐选区右缘（与主工具栏 actionX 一致），并 clamp 到虚拟屏边缘。
            int panelLeft = _region.Right - pbw;
            panelLeft = Math.Max(vs.Left + edge, Math.Min(panelLeft, vs.Right - edge - pbw));
            int left = panelLeft - shLeft;

            // 面板下缘默认贴选区下方 gap=10；超出时翻转到选区上方。
            int panelTop = preferBelow ? _region.Bottom + gap : _region.Top - gap - pbh;
            panelTop = Math.Max(vs.Top + edge, Math.Min(panelTop, vs.Bottom - edge - pbh));
            int top = panelTop - shTop;

            Rectangle regionClearance = Rectangle.Inflate(_region, 8, 8);
            // 预览窗也作为避让对象：工具栏与预览窗分居选区两侧，互不重叠。
            Rectangle previewRect = _previewWindow?.GetHostScreenRect() ?? Rectangle.Empty;
            Rectangle? previewClearance = previewRect.Width > 0 && previewRect.Height > 0
                ? Rectangle.Inflate(previewRect, 8, 8)
                : null;

            bool RectConflicts(Rectangle r)
                => r.IntersectsWith(regionClearance)
                   || (previewClearance.HasValue && r.IntersectsWith(previewClearance.Value))
                   || r.Top < vs.Top
                   || r.Bottom > vs.Bottom;

            Rectangle panelRect = new Rectangle(panelLeft, panelTop, pbw, pbh);
            if (RectConflicts(panelRect))
            {
                int origSide = preferBelow ? 1 : 0;
                int chosenSide = origSide;
                int chosenTop = panelTop;
                int[] tryOrder = { 1 - origSide, origSide }; // 对侧优先，再回原侧
                foreach (int side in tryOrder)
                {
                    bool bottom = side == 1;
                    int cTop = bottom ? _region.Bottom + gap : _region.Top - gap - pbh;
                    cTop = Math.Max(vs.Top + edge, Math.Min(cTop, vs.Bottom - edge - pbh));
                    Rectangle cand = new Rectangle(panelLeft, cTop, pbw, pbh);
                    if (cand.Top >= vs.Top && cand.Bottom <= vs.Bottom && !RectConflicts(cand))
                    {
                        chosenSide = side;
                        chosenTop = cTop;
                        break;
                    }
                }
                if (chosenSide != origSide)
                {
                    if (horizontal) _horizontalControlPreferBelow = chosenSide == 1;
                    else _verticalControlPreferBelow = chosenSide == 1;
                }
                panelTop = chosenTop;
                // 兜底：仍冲突则锚定上次位置，避免抖动。
                if (RectConflicts(new Rectangle(panelLeft, panelTop, pbw, pbh))
                    && _lastAnchoredControlScreenY >= vs.Top && _lastAnchoredControlScreenY + wh <= vs.Bottom)
                {
                    panelTop = _lastAnchoredControlScreenY;
                }
                top = panelTop - shTop;
            }
            _lastAnchoredControlScreenY = top;

            // 记录最终落位侧到记忆字段，作为迟滞带内的稳定基准。
            bool finallyBelow = panelTop >= _region.Bottom;
            if (horizontal) _horizontalControlPreferBelow = finallyBelow;
            else _verticalControlPreferBelow = finallyBelow;

            bool sameRect = _hasLastControlWindowRect
                && _lastControlWindowX == left
                && _lastControlWindowY == top
                && _lastControlWindowW == ww
                && _lastControlWindowH == wh;
            _previewWindow?.SetPlacementContext(_region, new Rectangle(left, top, ww, wh), _captureDirection);
            // 悬浮冻结：指针停在工具栏上（正阅读按钮 Tooltip）时跳过重定位/重设尺寸，
            // 任何原因导致的窗口移动都会让静止的指针“脱离”按钮、关闭 Tooltip 且不再
            // 重开（无 mousemove 无法恢复命中测试）。指针离开后下一次调用自然恢复对齐。
            bool pointerHovering = false;
            try
            {
                pointerHovering = _hasLastControlWindowRect
                    && Dispatcher.UIThread.CheckAccess()
                    && _controlWindow.IsPointerOver;
            }
            catch { }
            if (sameRect || pointerHovering) return;

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
        _hdrCapture ??= HdrScreenCapture.TryCreate(_region);
    }

    private void CleanupDIB()
    {
        _hdrCapture?.Dispose();
        _hdrCapture = null;
        _lastCapturePixels = null;
        Volatile.Write(ref _capturePixelsValid, false);
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
            CleanupDIB();
            _saveDialogCapturePaused?.TrySetResult();
            try { _captureQueue?.CompleteAdding(); } catch { }
            return;
        }

        int consecutiveUnchanged = 0;
        while (_isRunning && _cts != null && !_cts.IsCancellationRequested)
        {
            // 在完整一帧交付之后确认暂停，保存对话框出现前不会再有采集中的帧。
            if (Volatile.Read(ref _saveDialogOpen) != 0)
            {
                _saveDialogCapturePaused?.TrySetResult();
                Thread.Sleep(16);
                continue;
            }
            long start = Stopwatch.GetTimestamp();
            if (Volatile.Read(ref _manualOffsetDragging) != 0)
            {
                Thread.Sleep(8);
                continue;
            }

            if (ShouldWaitForCapture())
            {
                long waitStart = Stopwatch.GetTimestamp();
                Thread.Sleep(2);
                _captureBackpressureTicks += Stopwatch.GetTimestamp() - waitStart;
                continue;
            }

            if (Interlocked.Exchange(ref _captureResourcesDirty, 0) != 0)
            {
                try
                {
                    CleanupDIB();
                    InitDIB();
                    Volatile.Write(ref _capturePixelsValid, false);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("LegacyLongScrollSession.CaptureLoop: 重建捕获资源失败", ex);
                    try { _captureQueue?.CompleteAdding(); } catch { }
                    break;
                }
            }

            IntPtr hdcSrc = NativeMethods.GetDC(IntPtr.Zero);
            bool copied = NativeMethods.BitBlt(_hMemDC, 0, 0, _region.Width, _region.Height, hdcSrc, _region.X, _region.Y, NativeMethods.SRCCOPY);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcSrc);

            // SDR 同样直接访问 DIB 指针，必须先完成本线程 GDI 绘制，不能只在 HDR 分支同步。
            if (!copied || !NativeMethods.GdiFlush()) { Thread.Sleep(SafeCaptureIntervalMs); continue; }
            if (_hdrCapture != null)
                _hdrCapture.TryCopyTo(_pBits, checked(_region.Width * 4));

            if (IsScreenBitsUnchanged())
            {
                // Escalating backoff while the screen is static: 4->8->16->33->66ms.
                // Keeps latency low right after scrolling stops but drops idle CPU.
                consecutiveUnchanged++;
                int idleSleepMs = consecutiveUnchanged switch
                {
                    1 => 4,
                    2 => 8,
                    3 => 16,
                    4 => 33,
                    _ => 66,
                };
                Thread.Sleep(idleSleepMs);

                // Flush a preview update that was throttled away so the preview
                // converges to the final state once scrolling stops.
                if (Volatile.Read(ref _previewUpdatePending) != 0
                    && Interlocked.CompareExchange(ref _previewFlushRunning, 1, 0) == 0)
                {
                    Interlocked.Exchange(ref _previewUpdatePending, 0);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { if (_isRunning) UpdatePreview(); }
                        catch (Exception ex) { AppLogger.Error("LongScroll.PreviewFlush: 预览刷新失败", ex); }
                        finally { Volatile.Write(ref _previewFlushRunning, 0); }
                    });
                }
                continue;
            }
            consecutiveUnchanged = 0;
            Interlocked.Exchange(ref _lastCaptureChangeTick, Stopwatch.GetTimestamp());

            Bitmap? bmp = null;
            if (_bitmapPool == null || !_bitmapPool.TryTake(out bmp) || bmp == null)
            {
                bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb);
            }
            else if (bmp.Width != _region.Width || bmp.Height != _region.Height)
            {
                try { bmp.Dispose(); } catch { }
                bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb);
            }

            try
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int srcStride = ((_region.Width * 32 + 31) / 32) * 4;
                    int dstStride = Math.Abs(data.Stride);
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

                var frame = new CapturedFrame(bmp, Stopwatch.GetTimestamp());
                if (_captureQueue != null && !_captureQueue.TryAdd(frame))
                {
                    // 满队列时保留已入队的连续历史帧；丢掉新帧并显式标记本次可能断档。
                    ReturnToPool(frame.Bitmap);
                    // 本次画面没有入队，下一轮即使屏幕不再变化也必须重试交付。
                    Volatile.Write(ref _capturePixelsValid, false);
                    int dropped = Interlocked.Increment(ref _captureFramesDropped);
                    if (dropped == 1 || dropped % 32 == 0)
                        AppLogger.Info("LongScroll.CaptureQueueOverflow: dropped=" + dropped
                            + ", capacity=" + _captureQueue.BoundedCapacity);
                }
            }
            catch
            {
                ReturnToPool(bmp);
            }

            // 静止哨兵：拼接线程超过 500ms 未见"滚动式运动"（_lastMotionTick 由
            // 运动门控更新）时，捕获降到 250ms 节拍，只负责监听"何时开始滚"。
            // 悬停高亮/光标闪烁会让 IsScreenBitsUnchanged 判定"有变化"，从而把
            // BitBlt 持续运行（空闲期 CPU 大头），哨兵在该场景强制降速。
            // 恢复滚动的探测延迟 ≤250ms + 一帧，人眼无感。
            long motionTick = Volatile.Read(ref _lastMotionTick);
            long nowTick = Stopwatch.GetTimestamp();
            bool motionIdle = motionTick != 0
                && (nowTick - motionTick) * 1000 / Math.Max(1, Stopwatch.Frequency) > 500;
            bool scrollActive = _autoScrollEnabled || GetRecentWheelDirection() != 0;
            Thread.Sleep(motionIdle && !scrollActive ? 250 : Math.Max(SafeCaptureIntervalMs, _options.CaptureIntervalMs));
        }

        CleanupDIB();
        _saveDialogCapturePaused?.TrySetResult();
    }

    private void StitchWorker()
    {
        // 快速路径保留原严格复核；二维特征在歧义/动态内容上提供独立证据。
        // 未对齐时保留最后一次已确认的参考帧，以累计位移恢复；禁止无坐标地换基线。
        int failStreak = 0;
        int lastAcceptedDirection = 0;
        int lastAcceptedDelta = 0;
        int lastLoggedDirectionHint = int.MinValue;
        string lastLoggedDirectionSource = string.Empty;
        Bitmap? prevBmp = null;
        long prevTimestamp = 0;

        int dynamicFixedFramesChecked = 0;
        const int DynamicFixedCheckFrames = 5;
        const int ColdPathAfterFails = 3;
        using var featureCache = new ImageStitcher.FeatureTranslationCache();
        ScrollEstimate EstimateFeatures(Bitmap previous, Bitmap current, AxisDirection? direction,
            long currentVersion, int leading, int trailing)
        {
            long started = Stopwatch.GetTimestamp();
            try { return featureCache.Estimate(previous, current, _options.Axis, direction, prevTimestamp, currentVersion, leading, trailing); }
            finally { _featureCalls++; _featureTicks += Stopwatch.GetTimestamp() - started; }
        }

        try
        {
            if (_captureQueue == null || _cts == null || _bitmapPool == null) return;
            foreach (CapturedFrame queuedFrame in _captureQueue.GetConsumingEnumerable(_cts.Token))
            {
                using var processing = new FrameProcessingScope(this, queuedFrame.Timestamp);
                Bitmap curBmp = queuedFrame.Bitmap;
                long curTimestamp = queuedFrame.Timestamp;
                if (Volatile.Read(ref _manualOffsetDragging) != 0)
                {
                    ReturnToPool(curBmp);
                    continue;
                }

                if (!_isRunning) { ReturnToPool(curBmp); continue; }

                if (_maxHeightReached || _totalHeight >= _options.MaxAlongLengthPx)
                {
                    ReturnToPool(curBmp);
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
                        CalculateFixedLayerHeights(GetAlongLength(curBmp), out fixedTopH, out fixedBottomH);
                        firstTop = CropFixedTop(curBmp, fixedTopH);
                        firstBottom = CropFixedBottom(curBmp, fixedBottomH);
                        firstBmp = CropScrollableMiddle(curBmp, fixedTopH, fixedBottomH);
                        if (firstBmp != null && GetAlongLength(firstBmp) > _options.MaxAlongLengthPx)
                        {
                            Bitmap clipped = firstBmp.Clone(CreateAlongRectangle(firstBmp, 0, _options.MaxAlongLengthPx), firstBmp.PixelFormat);
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
                        ReturnToPool(curBmp);
                        continue;
                    }

                    lock (_stripsLock)
                    {
                        _fixedTopHeight = fixedTopH;
                        _fixedBottomHeight = fixedBottomH;
                        RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
                        RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
                        _topBoundaryFixedBitmap = firstTop;
                        _bottomBoundaryFixedBitmap = firstBottom;
                        StitchedStrip baseStrip = CreateStrip(firstBmp, 0);
                        _strips.Add(baseStrip);
                        _baseStrip = baseStrip;
                        _captureDirection = 0;
                        _totalHeight = GetAlongLength(firstBmp);
                        _currentViewportY = 0;
                        _canvasTopY = 0;
                        _canvasBottomY = _totalHeight;
                        _viewportHeight = _totalHeight;
                        _lastCanvasAction = _options.EnableFixedLayerMode ? "画布初始化（固定层模式）" : "画布初始化";
                    }

                    prevBmp = curBmp; prevTimestamp = curTimestamp;
                    UpdatePreview();
                    UpdateStatus();
                    continue;
                }

                if (_manualOffsetJustApplied)
                {
                    _manualOffsetJustApplied = false;
                    failStreak = 0;
                    ReturnToPool(prevBmp);
                    prevBmp = curBmp; prevTimestamp = curTimestamp;
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
                    if (fixedTopForFrame == 0 && fixedBottomForFrame == 0)
                    {
                        prevWorkBmp = prevBmp;
                        curWorkBmp = curBmp;
                    }
                    else if (TryCropScrollableMiddleInPlace(prevBmp, fixedTopForFrame, fixedBottomForFrame, ref _prevWorkBuffer)
                        && TryCropScrollableMiddleInPlace(curBmp, fixedTopForFrame, fixedBottomForFrame, ref _curWorkBuffer))
                    {
                        prevWorkBmp = _prevWorkBuffer;
                        curWorkBmp = _curWorkBuffer;
                    }
                    else
                    {
                        prevWorkBmp = CropScrollableMiddle(prevBmp, fixedTopForFrame, fixedBottomForFrame);
                        curWorkBmp = CropScrollableMiddle(curBmp, fixedTopForFrame, fixedBottomForFrame);
                    }
                }
                catch
                {
                    prevWorkBmp = null;
                    curWorkBmp = null;
                }
                if (prevWorkBmp == null || curWorkBmp == null)
                {
                    if (prevWorkBmp != null && prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != null && curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    ReturnToPool(curBmp);
                    continue;
                }

                if (AreImagesIdentical(prevWorkBmp, curWorkBmp))
                {
                    if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    // 光标在选区外（悬停工具栏/预览窗等）时注入本就暂停，页面静止
                    // 属预期，不能累积空闲帧误判“已到底”——否则悬停即永久停止自动滚动。
                    bool cursorOutsideRegion = NativeMethods.GetCursorPos(out NativeMethods.POINT idlePt)
                        && !_region.Contains(idlePt.X, idlePt.Y);
                    if (cursorOutsideRegion)
                    {
                        Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
                    }
                    else
                    {
                        Interlocked.Increment(ref _consecutiveIdleFrames);
                        if (Interlocked.CompareExchange(ref _consecutiveIdleFrames, 0, 0) >= BottomReachedThreshold)
                        {
                            _bottomReached = true;
                        }
                    }
                    ReturnToPool(curBmp);
                    continue;
                }

                // 自动滚动时先排除“局部动图在播、页面原位不动”。不改参考帧、不追加像素、
                // 不把动图当滚动响应；保留原逐像素捕获判重和真实位移的全部严格校验。
                if (_autoScrollEnabled && LongScrollFrameComparer.StationaryWithLocalChanges(prevWorkBmp, curWorkBmp, _options.Axis))
                {
                    Interlocked.Exchange(ref _lastStationaryViewportTick, curTimestamp);
                    Interlocked.Increment(ref _stationaryAnimationFrames);
                    failStreak = 0;
                    if (Interlocked.Exchange(ref _alignmentPending, 0) != 0)
                    {
                        Interlocked.Exchange(ref _alignmentPendingSinceTick, 0);
                        ClearRealignmentHint();
                    }
                    if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    ReturnToPool(curBmp);
                    continue;
                }

                // 先取滚动方向证据，供运动门控与方向提示共用：滚轮/滚动条已给出
                // 方向时，用户确在滚动（即使内容稀疏——聊天气泡、大段空白、正文行
                // 绝大多数行仍是背景色，像素变化行占比很低），此时不应被下面的
                // 「变化行占比」门控误杀，交由估计器做最终校验。
                int wheelDir = GetRecentWheelDirection(out string wheelDirectionSource);
                int scrollbarDragDir = _options.Axis == ScrollAxis.Horizontal
                    ? GetRecentHorizontalScrollbarDragDirection()
                    : 0;

                // 运动门控：滚动会移动视口内几乎所有行，悬停高亮/光标闪烁/局部
                // 动画只改少数行。覆盖率不足 30% 的帧默认丢弃——不进匹配器、
                // 不计失败、不污染方向记忆，也不累加空闲计数。存在明确方向证据
                // （滚轮/滚动条）时跳过该门控。
                bool hasDirectionEvidence = wheelDir != 0 || scrollbarDragDir != 0;
                ScrollEstimate? sparseFeature = null;
                if (!hasDirectionEvidence && !IsMotionScrollLike(prevWorkBmp, curWorkBmp))
                {
                    sparseFeature = EstimateFeatures(prevWorkBmp, curWorkBmp, null, curTimestamp, fixedTopForFrame, fixedBottomForFrame);
                    if (!sparseFeature.Value.IsReliable)
                    {
                        if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                        if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                        Interlocked.Increment(ref _teleGatedFrames);
                        ReturnToPool(curBmp);
                        continue;
                    }
                }
                Volatile.Write(ref _lastMotionTick, Stopwatch.GetTimestamp());

                int autoScrollDir = _options.Axis == ScrollAxis.Horizontal && _autoScrollEnabled
                    ? Math.Sign(Volatile.Read(ref _autoScrollDirection))
                    : 0;

                int directionHint;
                string directionSource;
                if (wheelDir != 0) { directionHint = wheelDir; directionSource = wheelDirectionSource; }
                else if (scrollbarDragDir != 0) { directionHint = scrollbarDragDir; directionSource = "ScrollbarDrag"; }
                else if (autoScrollDir != 0) { directionHint = autoScrollDir; directionSource = "AutoScrollDirection"; }
                else { directionHint = 0; directionSource = "None"; }

                // 手动连续滚动时，滚轮方向常因指针落在选区外（inside=False）或
                // hold 时间窗（WheelDirectionHoldMs=350ms）过期而丢失，导致最后一层
                // 方向证据消失、估计器退回双向扫描。周期纹理（列表行/表格/正文行）
                // 在双向扫描下会得到两个近等高相关峰 → WideAmbiguous → GAP(delta=0)，
                // 甚至会交替把位移判成反方向 → 触发下方“方向反转重置”逐帧丢帧，
                // 表现就是“滚几下就停住，得往回滚再往下滚才能继续”。
                // 回退到上一次已确认方向作为约束，可让连续滚动始终保持单向扫描，
                // 消除歧义与反转抖动；真实反向滚动仍由冷路径全引擎（无方向约束）兜底识别。
                if (directionHint == 0 && lastAcceptedDirection != 0)
                {
                    directionHint = lastAcceptedDirection;
                    directionSource = "LastAccepted";
                }

                if (directionHint != lastLoggedDirectionHint
                    || !string.Equals(directionSource, lastLoggedDirectionSource, StringComparison.Ordinal))
                {
                    AppLogger.Info("LongScroll.DirectionHint: source=" + directionSource
                        + ", direction=" + (directionHint < 0 ? "Leading" : directionHint > 0 ? "Trailing" : "None")
                        + ", wheel=" + wheelDir
                        + ", scrollbarDrag=" + scrollbarDragDir
                        + ", auto=" + autoScrollDir
                        + ", accepted=" + lastAcceptedDirection
                        + ", failStreak=" + failStreak);
                    lastLoggedDirectionHint = directionHint;
                    lastLoggedDirectionSource = directionSource;
                }
                AxisDirection? expectedDirection = directionHint < 0
                    ? AxisDirection.Leading
                    : directionHint > 0 ? AxisDirection.Trailing : null;

                // ==== L1：1D 投影快速路径（固定宽窗，亚毫秒级） ====
                int viewport = GetAlongLength(curWorkBmp);
                int window = Math.Clamp(viewport / 2, 160, 480);
                long fastStart = Stopwatch.GetTimestamp();
                ScrollEstimate estimate = ImageStitcher.EstimateFastPath(
                    prevWorkBmp, curWorkBmp, _options.Axis, window, expectedDirection);
                long fastMs = (Stopwatch.GetTimestamp() - fastStart) * 1000 / Math.Max(1, Stopwatch.Frequency);
                ScrollEstimate fastPathEstimate = estimate;
                double fastPathMargin = double.PositiveInfinity;
                bool needsIndependentConfirmation = estimate.IsReliable
                    && string.Equals(estimate.Reason, "FastPath1D", StringComparison.Ordinal)
                    && TryReadFastPathMargin(estimate.Diagnostics, out fastPathMargin)
                    && fastPathMargin < 0.18;

                string verdict;
                if (estimate.IsReliable)
                {
                    Interlocked.Increment(ref _teleFastPathCount);
                    Interlocked.Add(ref _teleFastPathMsTotal, fastMs);
                    verdict = "accept";
                }
                else
                {
                    // ==== L2：宽窗 1D + 双候选 2D 复核（毫秒级，④兜底新主路径） ====
                    estimate = ImageStitcher.EstimateWidePath(
                        prevWorkBmp, curWorkBmp, _options.Axis,
                        Math.Max(window, Math.Min(viewport - 64, 480)),
                        expectedDirection, out _, out _);
                    if (estimate.IsReliable)
                    {
                        Interlocked.Increment(ref _teleFastPathCount);
                        Interlocked.Add(ref _teleFastPathMsTotal, fastMs);
                        verdict = "accept";
                    }
                    else if (estimate.Reason == "WideAmbiguous")
                    {
                        // 保留歧义候选，继续尝试局部特征与后续累计位移恢复。
                        verdict = "gap";
                    }
                    else if (++failStreak >= ColdPathAfterFails)
                    {
                        // ==== 冷路径：旧全引擎最后确认（限频，防积压螺旋） ====
                        long fullStart = Stopwatch.GetTimestamp();
                        estimate = ImageStitcher.Estimate(
                            prevWorkBmp, curWorkBmp, _options.Axis, null,
                            Math.Clamp((int)Math.Round(viewport * 0.60), 320, 480),
                            preferredDelta: 0);
                        long fullMs = (Stopwatch.GetTimestamp() - fullStart) * 1000 / Math.Max(1, Stopwatch.Frequency);
                        Interlocked.Increment(ref _teleFallbackCount);
                        Interlocked.Add(ref _teleFallbackMsTotal, fullMs);
                        verdict = estimate.IsReliable ? "accept" : "gap";
                    }
                    else
                    {
                        verdict = "reject";
                    }
                }

                // ==== L3：周期纹理兜底（速度记忆 + 接缝复核 / 冷启动方向兜底） ====
                // 运动门控（AreImagesIdentical + IsMotionScrollLike，或方向证据）已保证此帧
                // 确实发生滚动，只是重复纹理（聊天气泡/正文行/大段空白）让常规估计器陷入
                // 「±一个周期」的方向/幅度歧义而返回 delta=0。分两种情况兜底：
                //  1) 已有速度先验 → 用上一次已确认位移作候选，邻域内 2D 接缝带 MAD 复核；
                //  2) 冷启动（尚无先验）但方向证据在 → 沿方向全范围搜真实接缝，打破
                //     「先有鸡还是先有蛋」死锁。
                if (verdict != "accept")
                {
                    if (lastAcceptedDelta > 0 && lastAcceptedDirection != 0)
                    {
                        var hintEstimate = ImageStitcher.EstimateByDeltaHint(
                            prevWorkBmp, curWorkBmp, _options.Axis,
                            lastAcceptedDelta * lastAcceptedDirection);
                        if (hintEstimate.IsReliable)
                        {
                            estimate = hintEstimate;
                            verdict = "accept";
                        }
                        else if (hintEstimate.Reason == "HintUnreliable")
                        {
                            // 先验已失效：hint 邻域接缝 MAD 不优于噪声地板，说明上一次
                            // 位移先验已过期（文字/聊天气泡的每格位移高度不稳定）。不再枯等
                            // failStreak，立即走全引擎兜底，避免连续甩帧导致「滚不动」。
                            estimate = ImageStitcher.Estimate(
                                prevWorkBmp, curWorkBmp, _options.Axis, null,
                                Math.Clamp((int)Math.Round(viewport * 0.60), 320, 480),
                                preferredDelta: 0);
                            verdict = estimate.IsReliable ? "accept" : "gap";
                        }
                    }
                    else if (directionHint != 0)
                    {
                        var dirEstimate = ImageStitcher.EstimateByDirection(
                            prevWorkBmp, curWorkBmp, _options.Axis, directionHint);
                        if (dirEstimate.IsReliable)
                        {
                            estimate = dirEstimate;
                            verdict = "accept";
                        }
                    }
                }

                // 低置信度快速匹配虽通过相邻帧接缝检查，周期性聊天气泡仍可能让错误周期侥幸通过。
                // 再以全重叠像素差独立搜索一次同方向位移；证据不足就暂缓写入，后续帧会累计位移。
                if (needsIndependentConfirmation)
                {
                    int confirmDirection = Math.Sign(fastPathEstimate.ViewportDeltaAlong);
                    int fastPathAbsDelta = Math.Abs(fastPathEstimate.ViewportDeltaAlong);
                    int confirmMaxDelta = Math.Min(GetAlongLength(curWorkBmp) - 128,
                        Math.Max(96, fastPathAbsDelta + 48));
                    int confirmMinDelta = Math.Max(1, fastPathAbsDelta - 48);
                    ScrollEstimate confirmation = ImageStitcher.EstimateByDirection(
                        prevWorkBmp, curWorkBmp, _options.Axis, confirmDirection,
                        maxDelta: confirmMaxDelta,
                        minDelta: confirmMinDelta);
                    if (confirmation.IsReliable
                        && Math.Sign(confirmation.ViewportDeltaAlong) == confirmDirection)
                    {
                        estimate = confirmation with
                        {
                            Diagnostics = confirmation.Diagnostics
                                + " fast=" + fastPathEstimate.ViewportDeltaAlong
                                + " margin=" + fastPathMargin.ToString("0.00", CultureInfo.InvariantCulture)
                        };
                        verdict = "accept";
                        AppLogger.Info("LongScroll.FastPathIndependentConfirm: fast="
                            + fastPathEstimate.ViewportDeltaAlong + ", confirmed="
                            + confirmation.ViewportDeltaAlong + ", margin="
                            + fastPathMargin.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        estimate = ScrollEstimate.Unreliable(
                            "FastPathConsensusReject",
                            score: fastPathEstimate.Score,
                            diagnostics: "fast=" + fastPathEstimate.ViewportDeltaAlong
                                + " margin=" + fastPathMargin.ToString("0.00", CultureInfo.InvariantCulture)
                                + " confirm=" + confirmation.Reason + " " + confirmation.Diagnostics);
                        verdict = "reject";
                        AppLogger.Info("LongScroll.FastPathIndependentReject: fast="
                            + fastPathEstimate.ViewportDeltaAlong + ", margin="
                            + fastPathMargin.ToString("0.00", CultureInfo.InvariantCulture)
                            + ", confirm=" + confirmation.Reason + " " + confirmation.Diagnostics);
                    }
                }

                if (sparseFeature.HasValue || verdict != "accept"
                    || Math.Abs(estimate.ViewportDeltaAlong) >= Math.Max(64, (int)Math.Round(viewport * 0.60))
                    || estimate.Reason is "Wide2D" or "HintSeam" or "DirectionHintSeam")
                {
                    var feature = sparseFeature ?? EstimateFeatures(prevWorkBmp, curWorkBmp,
                        hasDirectionEvidence ? expectedDirection : null, curTimestamp, fixedTopForFrame, fixedBottomForFrame);
                    if (feature.IsReliable)
                    {
                        estimate = feature;
                        verdict = "accept";
                    }
                }

                int signedViewportDelta = estimate.IsReliable ? estimate.ViewportDeltaAlong : 0;
                bool frameDirUp = signedViewportDelta < 0;
                int delta = Math.Abs(signedViewportDelta);
                int staticTop = estimate.StaticLeading;
                int staticBottom = estimate.StaticTrailing;

                // [FixedLayer] 全路径回填：FastPath/WidePath/HintSeam 等快速路径
                // 的 ScrollEstimate 不检测固定区，StaticLeading/Trailing 恒为 0。
                // 当固定层尚未剥离且本帧结果没带 static 时，单独补测一次固定区，
                // 保证自动剥离不依赖"恰好命中全引擎路径"。
                // （prevWorkBmp/curWorkBmp 在上方 1526 行已判空并 continue，此处非空；
                //  条件里不再重复 != null 判空，否则 Roslyn 可空流分析在 false 分支
                //  学习到"可能为 null"，毒化后续所有解引用点的警告状态。）
                if (!_options.EnableFixedLayerMode
                    && _fixedTopHeight == 0 && _fixedBottomHeight == 0
                    && staticTop < 8 && staticBottom < 8)
                {
                    ImageStitcher.DetectStaticBoundsForPair(prevWorkBmp, curWorkBmp, _options.Axis, out int fbTop, out int fbBottom);
                    if (fbTop > staticTop) staticTop = fbTop;
                    if (fbBottom > staticBottom) staticBottom = fbBottom;
                }

                if (Volatile.Read(ref _manualOffsetDragging) != 0)
                {
                    if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    ReturnToPool(curBmp);
                    continue;
                }

                if (!_options.EnableFixedLayerMode
                    && _capturedFrameCount == 0
                    && _fixedTopHeight == 0
                    && _fixedBottomHeight == 0
                    && delta > 0
                    && delta < 8)
                {
                    failStreak = 0;
                    if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    // 首次小位移继续累计，不能推进参考帧却不推进画布，否则慢滚一直丢内容。
                    ReturnToPool(curBmp);
                    continue;
                }

                // [FixedLayer Diag] 打印固定层自动剥离判定所需的全部条件参数，
                // 用于定位“向上滚动固定层重复”时卡在哪个子条件。仅诊断用，不影响行为。
                AppLogger.Info("LongScroll.FixedLayerDiag: Enable=" + _options.EnableFixedLayerMode
                    + ", captured=" + _capturedFrameCount
                    + ", top=" + _fixedTopHeight + ", bottom=" + _fixedBottomHeight
                    + ", delta=" + delta + ", staticTop=" + staticTop + ", staticBottom=" + staticBottom
                    + ", dirUp=" + frameDirUp + ", verdict=" + verdict
                    + ", estReason=" + estimate.Reason + ", estStatic=" + estimate.StaticLeading + "/" + estimate.StaticTrailing);

                if (!_options.EnableFixedLayerMode
                    && _fixedTopHeight == 0
                    && _fixedBottomHeight == 0
                    && delta >= 8
                    && (staticTop >= 8 || staticBottom >= 8)
                    && TryPromoteInitialFixedLayers(staticTop, staticBottom))
                {
                    failStreak = 0;
                    bool promotedStitched = TryCommitFrameAfterFixedLayerPromotion(curBmp, delta, frameDirUp, out bool promotedPositionUpdated);
                    if (promotedStitched || promotedPositionUpdated)
                    {
                        Volatile.Write(ref _alignmentPending, 0);
                        Interlocked.Exchange(ref _alignmentPendingSinceTick, 0);
                        ClearRealignmentHint();
                        MarkAutoScrollResponse();
                        if (frameDirUp) UpdateTopBoundaryFixedLayer(curBmp);
                        else UpdateBottomBoundaryFixedLayer(curBmp);
                        lastAcceptedDirection = frameDirUp ? -1 : 1;
                        lastAcceptedDelta = BlendAcceptedDelta(lastAcceptedDelta, delta);
                        Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
                        if (promotedStitched) _capturedFrameCount++;
                        LogStitchDecision(promotedStitched ? "promoted-accepted" : "promoted-covered",
                            delta, frameDirUp, staticTop, staticBottom, promotedStitched, promotedPositionUpdated);
                    }
                    if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    ReturnToPool(prevBmp);
                    prevBmp = curBmp; prevTimestamp = curTimestamp;
                    UpdatePreview();
                    UpdateStatus();
                    continue;
                }

                // StaticLeading/Trailing 是位移估算时的候选屏蔽区，不能直接当作画布裁剪量。
                // 纯色空白行在相邻帧里天然相同，慢速向上滚动时常会得到 1~7px 的假顶部固定区；
                // 若把它直接传给顶部追加，每帧都会跳过几像素并带入同量旧内容，形成密集断层。
                // 真固定层已在上面的 promotion 分支确认并从 curWorkBmp 物理裁掉，此处统一归零。
                staticTop = 0;
                staticBottom = 0;

                // 原快速路径保留位移上限；跨区域特征与像素复核已确认的大跨度可恢复，至少留96px重叠。
                if (verdict == "accept")
                {
                    double absoluteCapRatio = _options.Axis == ScrollAxis.Horizontal ? 0.65 : 0.60;
                    int absoluteDeltaCap = Math.Max(64, (int)Math.Round(viewport * absoluteCapRatio));
                    if (delta >= absoluteDeltaCap && estimate.Reason != "FeatureTranslation")
                    {
                        AppLogger.Info("LongScroll.DeltaOverCap: axis=" + _options.Axis
                            + ", delta=" + delta + ", cap=" + absoluteDeltaCap);
                        verdict = "gap";
                    }
                    else if (delta == 0 || delta >= viewport)
                    {
                        verdict = "reject";
                    }
                }

                LogStitchDecision("estimate", delta, frameDirUp, staticTop, staticBottom, false, false);
                ReportTelemetryOnce(delta);

                if (verdict == "accept")
                {
                    int frameDirection = frameDirUp ? -1 : 1;
                    if (lastAcceptedDirection != 0 && frameDirection != lastAcceptedDirection)
                    {
                        // 已有可靠图像位移时直接更新带符号坐标，反转不能丢掉一帧位移。
                        lastAcceptedDirection = frameDirection;
                        lastAcceptedDelta = 0;
                    }

                    int newViewportY;
                    lock (_stripsLock)
                    {
                        newViewportY = frameDirUp ? (_currentViewportY - delta) : (_currentViewportY + delta);
                    }

                    if (_options.Axis == ScrollAxis.Horizontal && !_crossBandsLocked)
                        TryLockCrossStaticBands(prevBmp, curBmp, delta);

                    bool stitched = TryApplyFrameToCanvas(curWorkBmp, newViewportY, frameDirUp, staticTop, staticBottom, out bool positionUpdated);
                    if (stitched || positionUpdated)
                    {
                        if (frameDirUp) UpdateTopBoundaryFixedLayer(curBmp);
                        else UpdateBottomBoundaryFixedLayer(curBmp);
                    }

                    if (stitched)
                    {
                        if (_options.EnableFixedLayerMode && dynamicFixedFramesChecked < DynamicFixedCheckFrames)
                        {
                            dynamicFixedFramesChecked++;
                            lock (_stripsLock)
                            {
                                int currentAlongLength = GetAlongLength(curBmp);
                                if (staticTop > 0 && staticTop > _fixedTopHeight && staticTop < currentAlongLength / 2)
                                {
                                    _fixedTopHeight = staticTop;
                                }
                                if (staticBottom > 0 && staticBottom > _fixedBottomHeight && staticBottom < currentAlongLength / 2)
                                {
                                    _fixedBottomHeight = staticBottom;
                                }
                            }
                        }
                    }

                    if (stitched || positionUpdated)
                    {
                        Volatile.Write(ref _alignmentPending, 0);
                        Interlocked.Exchange(ref _alignmentPendingSinceTick, 0);
                        ClearRealignmentHint();
                        MarkAutoScrollResponse();
                        Interlocked.Increment(ref _teleWindowAccepted);
                        LogStitchDecision(stitched ? "accepted" : "covered",
                            delta, frameDirUp, staticTop, staticBottom, stitched, positionUpdated);
                        if (delta > 0) { lastAcceptedDirection = frameDirUp ? -1 : 1; lastAcceptedDelta = BlendAcceptedDelta(lastAcceptedDelta, delta); }
                        failStreak = 0;
                        Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
                        UpdatePreview();

                        if (positionUpdated && _previewWindow != null)
                        {
                            try
                            {
                                lock (_stripsLock)
                                {
                                    int vpY = _currentViewportY - _canvasTopY;
                                    if (vpY < 0) vpY = 0;
                                    int vpH = _viewportHeight > 0 ? _viewportHeight : (_options.Axis == ScrollAxis.Horizontal ? _region.Width : _region.Height);
                                    int totH = _totalHeight > 0 ? _totalHeight : CalculateCompositedHeight(_strips, _options.Axis == ScrollAxis.Horizontal ? _region.Width : _region.Height);
                                    _previewWindow.UpdateViewport(_options.Axis, vpY, vpH, totH, _fixedTopHeight, _fixedBottomHeight);
                                }
                            }
                            catch { }
                        }

                        UpdateStatus();
                        if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                        if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                        ReturnToPool(prevBmp);
                        prevBmp = curBmp; prevTimestamp = curTimestamp;
                        if (stitched) _capturedFrameCount++;
                        continue;
                    }

                    // TryApply 未通过：画布拒收，按 REJECT 处理（基线不动）。
                    verdict = "reject";
                }

                if (verdict == "gap")
                {
                    // 保留最后一次已对齐的参考帧。后续以累计位移重新匹配，不能把未知距离压成零。
                    failStreak++;
                    SetAlignmentPending();
                    AppLogger.Info("LongScroll.AlignmentRetry: axis=" + _options.Axis
                        + ", reason=" + estimate.Reason + ", detail=" + estimate.Diagnostics);
                    // 短暂重试只记日志，控制条保持默认提示，避免对齐/正常状态来回闪烁。
                    LogStitchDecision("retry", delta, frameDirUp, staticTop, staticBottom, false, false);
                    if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                    if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                    ReturnToPool(curBmp);
                    continue;
                }

                // REJECT：丢弃当前帧，基线保持，等下一帧。
                SetAlignmentPending();
                LogStitchDecision("rejected", delta, frameDirUp, staticTop, staticBottom, false, false);
                Interlocked.Increment(ref _consecutiveIdleFrames);
                if (prevWorkBmp != _prevWorkBuffer && prevWorkBmp != prevBmp) try { prevWorkBmp.Dispose(); } catch { }
                if (curWorkBmp != _curWorkBuffer && curWorkBmp != curBmp) try { curWorkBmp.Dispose(); } catch { }
                ReturnToPool(curBmp);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("LegacyLongScrollSession.StitchWorker 异常", ex);
        }
        finally
        {
            try { if (prevBmp != null) _bitmapPool?.Add(prevBmp); } catch { }
        }
    }

    /// <summary>v3.1 健康遥测：每 60 帧汇总输出（吞吐 / 快速路径命中率 / 位移分布 / 匹配耗时）。</summary>
    private void ReportTelemetryOnce(int delta)
    {
        long w = Interlocked.Increment(ref _teleWindowFrames);
        Interlocked.Add(ref _teleDeltaSum, Math.Max(0, delta));
        if (delta > _teleDeltaMax) _teleDeltaMax = delta;
        if (w % 20 != 0) return;

        long now = Stopwatch.GetTimestamp();
        // 首个窗口只建立时间基线不输出（无基准算不出 fps，避免打印误导性的 0）。
        if (Interlocked.CompareExchange(ref _teleSinceTs, now, 0) == 0) return;
        double sec = (now - Volatile.Read(ref _teleSinceTs)) / (double)Stopwatch.Frequency;
        double fps = sec > 0 ? 20 / sec : 0;

        long fast = Volatile.Read(ref _teleFastPathCount);
        long fb = Volatile.Read(ref _teleFallbackCount);
        long matched = Math.Max(1, fast + fb);
        double avgDelta = (double)Volatile.Read(ref _teleDeltaSum) / Math.Max(1, w);
        AppLogger.Info("LongScroll.Telemetry: fps=" + fps.ToString("0.0")
            + " accepted=" + Volatile.Read(ref _teleWindowAccepted) + "/" + w
            + " fast1D=" + fast + " (" + (100 * fast / matched).ToString("0") + "%)"
            + " fastAvgMs=" + (fast > 0 ? (Volatile.Read(ref _teleFastPathMsTotal) / (double)fast).ToString("0.0") : "-")
            + " fallbackAvgMs=" + (fb > 0 ? (Volatile.Read(ref _teleFallbackMsTotal) / (double)fb).ToString("0.0") : "-")
            + " delta(avg/max)=" + avgDelta.ToString("0") + "/" + _teleDeltaMax
            + " gated=" + Volatile.Read(ref _teleGatedFrames));
        Interlocked.Exchange(ref _teleSinceTs, now);
        Interlocked.Exchange(ref _teleWindowFrames, 0);
        Interlocked.Exchange(ref _teleWindowAccepted, 0);
        Interlocked.Exchange(ref _teleFastPathCount, 0);
        Interlocked.Exchange(ref _teleFastPathMsTotal, 0);
        Interlocked.Exchange(ref _teleFallbackCount, 0);
        Interlocked.Exchange(ref _teleFallbackMsTotal, 0);
        Interlocked.Exchange(ref _teleDeltaSum, 0);
        Interlocked.Exchange(ref _teleGatedFrames, 0);
        _teleDeltaMax = 0;
    }

    private static bool TryReadFastPathMargin(string diagnostics, out double margin)
    {
        const string Prefix = "margin=";
        margin = double.PositiveInfinity;
        return diagnostics != null
            && diagnostics.StartsWith(Prefix, StringComparison.Ordinal)
            && double.TryParse(
                diagnostics.AsSpan(Prefix.Length),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out margin);
    }

    private int GetAlongLength(Bitmap bitmap) => _options.Axis == ScrollAxis.Horizontal ? bitmap.Width : bitmap.Height;

    private int GetCrossLength(Bitmap bitmap) => _options.Axis == ScrollAxis.Horizontal ? bitmap.Height : bitmap.Width;

    /// <summary>
    /// v3.1 速度先验平滑：L3 周期纹理兜底用「上一次已确认位移」作幅度先验，
    /// 但单帧微小滚动（如 25px 的微调）会把先验直接拖垮，真正的大位移到来时
    /// 用陈旧小先验在 ±searchRadius 内搜不到真实接缝而失败。用 EMA 融合历史
    /// 与本次位移，并设 60% 下限，保持先验始终接近惯性速度。
    /// </summary>
    private static int BlendAcceptedDelta(int previous, int current)
    {
        if (previous <= 0) return Math.Max(1, current);
        int blended = (int)Math.Round(previous * 0.5 + current * 0.5);
        int floor = Math.Max(1, (int)(previous * 0.6));
        return Math.Max(blended, floor);
    }

    private Rectangle CreateAlongRectangle(Bitmap bitmap, int alongStart, int alongLength)
    {
        return _options.Axis == ScrollAxis.Horizontal
            ? new Rectangle(alongStart, 0, alongLength, bitmap.Height)
            : new Rectangle(0, alongStart, bitmap.Width, alongLength);
    }

    private StitchedStrip CreateStrip(Bitmap bitmap, int alongStart, int overlapLength = 0)
    {
        lock (StitchedStrip.GetBitmapSyncRoot(bitmap))
            return new StitchedStrip(
                bitmap,
                alongStart,
                GetCrossLength(bitmap),
                GetAlongLength(bitmap),
                overlapLength,
                _options.Axis);
    }

    private Bitmap CloneAlongBitmap(Bitmap bitmap, int alongStart, int alongLength)
    {
        lock (StitchedStrip.GetBitmapSyncRoot(bitmap))
            return bitmap.Clone(CreateAlongRectangle(bitmap, alongStart, alongLength), bitmap.PixelFormat);
    }

    /// <summary>
    /// Along-axis rectangle limited to the non-static cross extent for horizontal
    /// captures, so strips exclude the viewport-fixed header/footer bands.
    /// </summary>
    private Rectangle CreateStripRectangle(Bitmap bmp, int alongStart, int alongLength)
    {
        if (_options.Axis == ScrollAxis.Horizontal && (_crossTopCut > 0 || _crossBottomCut > 0))
        {
            int cross = bmp.Height - _crossTopCut - _crossBottomCut;
            if (cross > 40)
            {
                return new Rectangle(alongStart, _crossTopCut, alongLength, cross);
            }
        }
        return CreateAlongRectangle(bmp, alongStart, alongLength);
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

    /// <summary>
    /// Sparse FNV-1a 64-bit hash over a bitmap region. Samples locked BGRA bits
    /// directly (about 32 rows x 64 columns) with no allocation. Returns 0 on
    /// failure; callers treat 0 as "unknown" and fall through to a real update.
    /// </summary>
    private static long HashBitmapRegion(Bitmap bmp, Rectangle rect)
    {
        if (bmp == null || rect.Width <= 0 || rect.Height <= 0) return 0;
        BitmapData? data = null;
        try
        {
            PixelFormat lockFormat = bmp.PixelFormat == PixelFormat.Format32bppRgb
                ? PixelFormat.Format32bppRgb
                : PixelFormat.Format32bppArgb;
            data = bmp.LockBits(rect, ImageLockMode.ReadOnly, lockFormat);
            int stride = data.Stride;
            IntPtr basePtr = data.Scan0;

            unchecked
            {
                long hash = 1469598103934665603L;
                int stepY = Math.Max(1, rect.Height / 32);
                int stepX = Math.Max(1, rect.Width / 64);
                for (int y = 0; y < rect.Height; y += stepY)
                {
                    IntPtr row = IntPtr.Add(basePtr, y * stride);
                    for (int x = 0; x < rect.Width; x += stepX)
                    {
                        // Read a single BGRA pixel (4 bytes) to stay inside the row.
                        long v = Marshal.ReadInt32(row, x * 4);
                        hash ^= v;
                        hash *= 1099511628211L;
                    }
                }
                return hash == 0 ? 1 : hash;
            }
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (data != null)
            {
                try { bmp.UnlockBits(data); } catch { }
            }
        }
    }

    private const int CrossStaticMinRows = 40;
    private const int CrossStaticShadowMargin = 16;

    /// <summary>
    /// One-shot detection of cross-axis static bands (viewport-fixed header/footer)
    /// for horizontal captures, run when the first reliably measurable scroll
    /// occurs. The rows plus a shadow margin are cut from every strip so a frozen
    /// or frosted band is not baked into the composed image.
    /// </summary>
    private void TryLockCrossStaticBands(Bitmap? prevFrame, Bitmap? currFrame, int absDelta)
    {
        if (_crossBandsLocked || _options.Axis != ScrollAxis.Horizontal) return;
        if (prevFrame == null || currFrame == null || absDelta < 12) return;
        _crossBandsLocked = true;

        int height = Math.Min(prevFrame.Height, currFrame.Height);
        int maxCut = (int)(height * 0.32);
        if (maxCut < CrossStaticMinRows) return;

        DetectCrossStaticRows(prevFrame, currFrame, maxCut, out int topRows, out int bottomRows);
        int topCut = topRows >= CrossStaticMinRows ? Math.Min(topRows + CrossStaticShadowMargin, maxCut) : 0;
        int bottomCut = bottomRows >= CrossStaticMinRows ? Math.Min(bottomRows + CrossStaticShadowMargin, maxCut) : 0;

        int minKeep = Math.Max(120, height / 3);
        if (height - topCut - bottomCut < minKeep)
        {
            int budget = Math.Max(0, height - minKeep);
            topCut = Math.Min(topCut, budget / 2);
            bottomCut = Math.Min(bottomCut, budget - topCut);
        }
        if (topCut <= 0 && bottomCut <= 0) return;

        _crossTopCut = topCut;
        _crossBottomCut = bottomCut;
        RebuildStripsWithCrossCuts();
    }

    /// <summary>
    /// Re-applies the locked cross cuts to every strip already in the canvas and to
    /// the along-axis fixed layers, keeping a single uniform cross length.
    /// </summary>
    private void RebuildStripsWithCrossCuts()
    {
        lock (_stripsLock)
        {
            for (int i = 0; i < _strips.Count; i++)
            {
                StitchedStrip old = _strips[i];
                if (old == null) continue;
                Bitmap? cropped = TryCrossCropClone(old.Bitmap);
                if (cropped == null) continue;
                StitchedStrip replacement = new StitchedStrip(
                    cropped, old.AlongStart, cropped.Height, cropped.Width, old.OverlapLength, ScrollAxis.Horizontal);
                _strips[i] = replacement;
                if (_baseStrip == old) _baseStrip = replacement;
                // 预览窗持有该条带位图的非拥有引用，立即 Dispose 会与渲染线程竞态，
                // 走 RetireStripLocked 延迟释放（与手动裁剪路径同一契约）。
                RetireStripLocked(old);
            }

            Bitmap? newTop = _topBoundaryFixedBitmap != null ? TryCrossCropClone(_topBoundaryFixedBitmap) : null;
            if (newTop != null)
            {
                Bitmap? oldTop = _topBoundaryFixedBitmap;
                _topBoundaryFixedBitmap = newTop;
                _topBoundaryFixedHash = 0;
                RetireFixedBitmapLocked(oldTop);
            }
            Bitmap? newBottom = _bottomBoundaryFixedBitmap != null ? TryCrossCropClone(_bottomBoundaryFixedBitmap) : null;
            if (newBottom != null)
            {
                Bitmap? oldBottom = _bottomBoundaryFixedBitmap;
                _bottomBoundaryFixedBitmap = newBottom;
                _bottomBoundaryFixedHash = 0;
                RetireFixedBitmapLocked(oldBottom);
            }
        }
    }

    /// <summary>
    /// Returns a cross-cropped clone for horizontal captures after the bands are
    /// locked; null when not applicable or when the clone fails.
    /// </summary>
    private Bitmap? TryCrossCropClone(Bitmap src)
    {
        if (_options.Axis != ScrollAxis.Horizontal) return null;
        if (_crossTopCut <= 0 && _crossBottomCut <= 0) return null;
        try
        {
            lock (StitchedStrip.GetBitmapSyncRoot(src))
            {
                int newHeight = src.Height - _crossTopCut - _crossBottomCut;
                if (newHeight <= 0) return null;
                return src.Clone(new Rectangle(0, _crossTopCut, src.Width, newHeight), src.PixelFormat);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the leading/trailing rows that stayed identical (within AA tolerance)
    /// between two frames while the content in between scrolled horizontally.
    /// </summary>
    private static unsafe void DetectCrossStaticRows(Bitmap prev, Bitmap curr, int maxCut, out int topRows, out int bottomRows)
    {
        topRows = 0;
        bottomRows = 0;
        int width = Math.Min(prev.Width, curr.Width);
        int height = Math.Min(prev.Height, curr.Height);
        if (width < 8 || height < 24 || maxCut <= 0) return;

        BitmapData? prevData = null;
        BitmapData? currData = null;
        try
        {
            prevData = prev.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            currData = curr.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            int step = Math.Max(4, width / 48);
            int samples = (width + step - 1) / step;
            int allowed = Math.Max(1, samples / 50);
            int prevStride = prevData.Stride;
            int currStride = currData.Stride;

            unsafe
            {
                byte* prevBase = (byte*)prevData.Scan0;
                byte* currBase = (byte*)currData.Scan0;

                for (int y = 0; y < maxCut; y++)
                {
                    byte* pr = prevBase + (long)y * prevStride;
                    byte* cr = currBase + (long)y * currStride;
                    int bad = 0;
                    for (int x = 0; x < width; x += step)
                    {
                        byte* pp = pr + x * 4;
                        byte* cp = cr + x * 4;
                        int d = Math.Abs(pp[0] - cp[0]) + Math.Abs(pp[1] - cp[1]) + Math.Abs(pp[2] - cp[2]);
                        if (d > 18 && ++bad > allowed) break;
                    }
                    if (bad > allowed) break;
                    topRows++;
                }

                for (int y = height - 1; y >= height - maxCut; y--)
                {
                    byte* pr = prevBase + (long)y * prevStride;
                    byte* cr = currBase + (long)y * currStride;
                    int bad = 0;
                    for (int x = 0; x < width; x += step)
                    {
                        byte* pp = pr + x * 4;
                        byte* cp = cr + x * 4;
                        int d = Math.Abs(pp[0] - cp[0]) + Math.Abs(pp[1] - cp[1]) + Math.Abs(pp[2] - cp[2]);
                        if (d > 18 && ++bad > allowed) break;
                    }
                    if (bad > allowed) break;
                    bottomRows++;
                }
            }
        }
        catch
        {
            topRows = 0;
            bottomRows = 0;
        }
        finally
        {
            if (prevData != null) try { prev.UnlockBits(prevData); } catch { }
            if (currData != null) try { curr.UnlockBits(currData); } catch { }
        }
    }

    private Bitmap? CropFixedTop(Bitmap bmp, int topH)
    {
        if (bmp == null || topH <= 0) return null;
        int alongLength = GetAlongLength(bmp);
        if (topH > alongLength) topH = alongLength;
        return bmp.Clone(CreateAlongRectangle(bmp, 0, topH), bmp.PixelFormat);
    }

    private Bitmap? CropFixedBottom(Bitmap bmp, int bottomH)
    {
        if (bmp == null || bottomH <= 0) return null;
        int alongLength = GetAlongLength(bmp);
        if (bottomH > alongLength) bottomH = alongLength;
        return bmp.Clone(CreateAlongRectangle(bmp, alongLength - bottomH, bottomH), bmp.PixelFormat);
    }

    private Bitmap? CropScrollableMiddle(Bitmap bmp, int topH, int bottomH)
    {
        if (bmp == null) return null;
        if (topH <= 0 && bottomH <= 0)
        {
            return (Bitmap)bmp.Clone();
        }

        if (topH < 0) topH = 0;
        if (bottomH < 0) bottomH = 0;
        int alongLength = GetAlongLength(bmp);
        if (topH > alongLength) topH = alongLength;
        if (bottomH > alongLength - topH) bottomH = alongLength - topH;
        int middleH = alongLength - topH - bottomH;
        if (middleH <= 0) return null;
        return bmp.Clone(CreateAlongRectangle(bmp, topH, middleH), bmp.PixelFormat);
    }

    /// <summary>
    /// </summary>
    private bool TryCropScrollableMiddleInPlace(Bitmap src, int topH, int bottomH, ref Bitmap? buffer)
    {
        if (src == null) return false;
        int top = topH < 0 ? 0 : topH;
        int bottom = bottomH < 0 ? 0 : bottomH;
        int sourceAlongLength = GetAlongLength(src);
        if (top > sourceAlongLength) top = sourceAlongLength;
        if (bottom > sourceAlongLength - top) bottom = sourceAlongLength - top;
        int middleH = sourceAlongLength - top - bottom;
        if (middleH <= 0) return false;

        int targetWidth = _options.Axis == ScrollAxis.Horizontal ? middleH : src.Width;
        int targetHeight = _options.Axis == ScrollAxis.Horizontal ? src.Height : middleH;
        if (buffer == null || buffer.Width != targetWidth || buffer.Height != targetHeight)
        {
            try { buffer?.Dispose(); } catch { }
            buffer = new Bitmap(targetWidth, targetHeight, src.PixelFormat);
        }

        try
        {
            using Graphics g = Graphics.FromImage(buffer);
            g.DrawImage(
                src,
                new Rectangle(0, 0, targetWidth, targetHeight),
                CreateAlongRectangle(src, top, middleH),
                GraphicsUnit.Pixel);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// </summary>
    private void ReturnToPool(Bitmap? b)
    {
        if (b == null) return;
        Rectangle region = _region;
        if (b.Width != region.Width || b.Height != region.Height
            || _bitmapPool == null || _bitmapPool.Count >= MaxPoolSize)
        {
            try { b.Dispose(); } catch { }
        }
        else
        {
            _bitmapPool.Add(b);
        }
    }

    private byte[]? _lastCapturePixels;
    private bool _capturePixelsValid;

    private bool IsScreenBitsUnchanged()
    {
        if (_pBits == IntPtr.Zero || _region.Width <= 0 || _region.Height <= 0) return false;
        try
        {
            bool unchanged = LongScrollFrameComparer.Update(_pBits, _region.Width, _region.Height,
                ref _lastCapturePixels, Volatile.Read(ref _capturePixelsValid));
            Volatile.Write(ref _capturePixelsValid, true);
            return unchanged;
        }
        catch { Volatile.Write(ref _capturePixelsValid, false); return false; }
    }

    private void ClampCurrentViewportLocked()
    {
        int viewportH = _viewportHeight > 0 ? _viewportHeight : (_options.Axis == ScrollAxis.Horizontal ? _region.Width : _region.Height);
        int minY = _canvasTopY;
        int maxY = _canvasBottomY - viewportH;
        if (maxY < minY) maxY = minY;
        if (_currentViewportY < minY) _currentViewportY = minY;
        if (_currentViewportY > maxY) _currentViewportY = maxY;
    }

    /// <summary>
    /// </summary>
    private bool TryPromoteInitialFixedLayers(int detectedTop, int detectedBottom)
    {
        if (detectedTop < 8) detectedTop = 0;
        if (detectedBottom < 8) detectedBottom = 0;
        if (detectedTop == 0 && detectedBottom == 0) return false;

        lock (_stripsLock)
        {
            if (_fixedTopHeight > 0 || _fixedBottomHeight > 0) return false;
            if (_strips.Count != 1 || _baseStrip == null || !ReferenceEquals(_strips[0], _baseStrip)) return false;

            Bitmap source = _baseStrip.Bitmap;
            int sourceAlongLength = _baseStrip.AlongLength;
            int maxLayer = Math.Min(_options.MaxFixedLayerHeightPx, sourceAlongLength / 3);
            int topH = Math.Clamp(detectedTop, 0, maxLayer);
            int bottomH = Math.Clamp(detectedBottom, 0, maxLayer);
            int middleH = sourceAlongLength - topH - bottomH;
            int minMiddle = Math.Max(80, (int)Math.Round(sourceAlongLength * 0.60));
            if (middleH < minMiddle) return false;

            Bitmap? top = null;
            Bitmap? bottom = null;
            Bitmap? middle = null;
            try
            {
                if (topH > 0) top = CloneAlongBitmap(source, 0, topH);
                if (bottomH > 0) bottom = CloneAlongBitmap(source, sourceAlongLength - bottomH, bottomH);
                middle = CloneAlongBitmap(source, topH, middleH);
            }
            catch
            {
                try { top?.Dispose(); } catch { }
                try { bottom?.Dispose(); } catch { }
                try { middle?.Dispose(); } catch { }
                return false;
            }

            StitchedStrip oldBase = _baseStrip;
            StitchedStrip newBase = CreateStrip(middle, 0);
            _strips[0] = newBase;
            _baseStrip = newBase;
            RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
            RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
            _topBoundaryFixedBitmap = top;
            _bottomBoundaryFixedBitmap = bottom;
            _fixedTopHeight = topH;
            _fixedBottomHeight = bottomH;
            _totalHeight = middleH;
            _currentViewportY = 0;
            _canvasTopY = 0;
            _canvasBottomY = middleH;
            _viewportHeight = middleH;
            _lastCanvasAction = "固定层高度更新 " + topH + "/" + bottomH + "px";
            AppLogger.Info("LongScroll.FixedLayerPromoted top=" + topH + ", bottom=" + bottomH + ", middle=" + middleH);
            RetireStripLocked(oldBase);
            return true;
        }
    }

    private bool TryCommitFrameAfterFixedLayerPromotion(Bitmap fullFrame, int delta, bool directionIsUp, out bool positionUpdated)
    {
        positionUpdated = false;
        if (fullFrame == null || delta <= 0) return false;

        int topH;
        int bottomH;
        lock (_stripsLock)
        {
            topH = _fixedTopHeight;
            bottomH = _fixedBottomHeight;
        }

        int middleH = GetAlongLength(fullFrame) - topH - bottomH;
        if (middleH <= 0 || delta >= middleH) return false;

        try
        {
            using Bitmap middle = fullFrame.Clone(CreateAlongRectangle(fullFrame, topH, middleH), fullFrame.PixelFormat);
            int newViewportY = directionIsUp ? -delta : delta;
            return TryApplyFrameToCanvas(middle, newViewportY, directionIsUp, 0, 0, out positionUpdated);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateTopBoundaryFixedLayer(Bitmap fullFrame)
    {
        if (fullFrame == null) return;
        int topH;
        lock (_stripsLock) { topH = _fixedTopHeight; }

        if (topH <= 0)
        {
            lock (_stripsLock)
            {
                if (_topBoundaryFixedBitmap != null)
                {
                    RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
                    _topBoundaryFixedBitmap = null;
                    _topBoundaryFixedHash = 0;
                }
            }
            return;
        }

        // Dirty check first: hash the source region and skip the per-frame
        // Clone entirely while the fixed layer content is unchanged.
        int hashLen = Math.Min(topH, GetAlongLength(fullFrame));
        if (hashLen <= 0) return;
        long newHash = HashBitmapRegion(fullFrame, CreateStripRectangle(fullFrame, 0, hashLen));
        lock (_stripsLock)
        {
            if (newHash != 0 && _topBoundaryFixedBitmap != null && newHash == _topBoundaryFixedHash)
                return;
        }

        Bitmap? top = null;
        try { top = CropFixedTop(fullFrame, topH); } catch { top = null; }
        if (top != null)
        {
            Bitmap? crossCropped = TryCrossCropClone(top);
            if (crossCropped != null)
            {
                top.Dispose();
                top = crossCropped;
            }
        }
        if (top == null) return;
        lock (_stripsLock)
        {
            RetireFixedBitmapLocked(_topBoundaryFixedBitmap);
            _topBoundaryFixedBitmap = top;
            _topBoundaryFixedHash = newHash;
        }
    }

    private void UpdateBottomBoundaryFixedLayer(Bitmap fullFrame)
    {
        if (fullFrame == null) return;
        int bottomH;
        lock (_stripsLock) { bottomH = _fixedBottomHeight; }

        if (bottomH <= 0)
        {
            lock (_stripsLock)
            {
                if (_bottomBoundaryFixedBitmap != null)
                {
                    RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
                    _bottomBoundaryFixedBitmap = null;
                    _bottomBoundaryFixedHash = 0;
                }
            }
            return;
        }

        // Dirty check first: hash the source region and skip the per-frame
        // Clone entirely while the fixed layer content is unchanged.
        int alongLength = GetAlongLength(fullFrame);
        int hashLen = Math.Min(bottomH, alongLength);
        if (hashLen <= 0) return;
        long newHash = HashBitmapRegion(fullFrame, CreateStripRectangle(fullFrame, alongLength - hashLen, hashLen));
        lock (_stripsLock)
        {
            if (newHash != 0 && _bottomBoundaryFixedBitmap != null && newHash == _bottomBoundaryFixedHash)
                return;
        }

        Bitmap? bottom = null;
        try { bottom = CropFixedBottom(fullFrame, bottomH); } catch { bottom = null; }
        if (bottom != null)
        {
            Bitmap? crossCropped = TryCrossCropClone(bottom);
            if (crossCropped != null)
            {
                bottom.Dispose();
                bottom = crossCropped;
            }
        }
        if (bottom == null) return;
        lock (_stripsLock)
        {
            RetireFixedBitmapLocked(_bottomBoundaryFixedBitmap);
            _bottomBoundaryFixedBitmap = bottom;
            _bottomBoundaryFixedHash = newHash;
        }
    }

    // =====================================================================
    // 追加内容的重复守卫（2026-10-01，修"长截图偶发重复拼接"）
    // =====================================================================
    // 🔴 这是**落笔前的最后一道关卡**，补的是一个结构性空档：位移估计那一侧无论做多强的验证，
    //    都只在"前后两帧之间"求证，**从不回头看画布上已经有什么**。于是一旦位移被估小
    //    （渐变/网格/空白区上窄带 MAD 分不出错位，见 ImageStitcher.VerifyCandidateStrict），
    //    同一段内容就被一小条一小条反复追加 —— 用户 2026-10-01 实报的"同一段重复 4 次、
    //    每次只偏一点"正是这个形态。
    // 判据取"**本次要追加的那条**与**上一次追加的那条**是否几乎相同"：真实滚动时相邻两次追加
    //    的是画面上不同的内容，绝不会连续雷同；而位移估小时它们必然高度重复。
    // ⚠ 刻意**不**与整张画布比对（代价随长度线性增长，长图上不可接受），只比上一条 —— 重复拼接
    //    是连续发生的，拦住"连续雷同"就打断了整条链。
    // ⚠ 容差比较而非精确哈希：亚像素渲染、抗锯齿、光标闪烁都会让两条"看起来一样"的条带哈希不同，
    //    精确比较形同虚设（本文件的 AreImagesIdentical 就吃过这个亏——100 像素精确采样兜不住近似重复）。

    /// <summary>上一次真正追加进画布的那条内容的指纹（灰度均值网格，8×8＝64 格）。空＝还没追加过。</summary>
    private byte[]? _lastAppendedFingerprint;

    /// <summary>连续被判为重复而拦下的次数（仅用于诊断与"连续多次即说明判据可能过严"的采证）。</summary>
    private int _duplicateAppendBlocked;

    /// <summary>
    /// 对一条位图区域取 8×8 灰度均值指纹。抗亚像素抖动（每格是一片区域的均值），
    /// 又足以区分不同内容（64 维）。失败返回 null（调用方按"无从比较"放行）。
    /// </summary>
    private static byte[]? ComputeStripFingerprint(Bitmap bmp, Rectangle rect)
    {
        if (bmp == null || rect.Width <= 0 || rect.Height <= 0) return null;
        const int Grid = 8;
        if (rect.Width < Grid || rect.Height < Grid) return null;
        BitmapData? data = null;
        try
        {
            PixelFormat lockFormat = bmp.PixelFormat == PixelFormat.Format32bppRgb
                ? PixelFormat.Format32bppRgb
                : PixelFormat.Format32bppArgb;
            data = bmp.LockBits(rect, ImageLockMode.ReadOnly, lockFormat);
            var fp = new byte[Grid * Grid];
            int cellW = rect.Width / Grid, cellH = rect.Height / Grid;
            if (cellW <= 0 || cellH <= 0) return null;
            // 每格内再降采样，避免大图上逐像素遍历
            int sx = Math.Max(1, cellW / 4), sy = Math.Max(1, cellH / 4);
            for (int gy = 0; gy < Grid; gy++)
            {
                for (int gx = 0; gx < Grid; gx++)
                {
                    long sum = 0; int n = 0;
                    for (int y = gy * cellH; y < (gy + 1) * cellH; y += sy)
                    {
                        IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                        for (int x = gx * cellW; x < (gx + 1) * cellW; x += sx)
                        {
                            int v = Marshal.ReadInt32(row, x * 4);
                            int b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                            sum += (b * 114 + g * 587 + r * 299) / 1000;
                            n++;
                        }
                    }
                    fp[gy * Grid + gx] = n > 0 ? (byte)(sum / n) : (byte)0;
                }
            }
            return fp;
        }
        catch { return null; }
        finally { if (data != null) try { bmp.UnlockBits(data); } catch { } }
    }

    /// <summary>两条指纹的平均绝对差（灰度 0~255）。任一为空返回 +∞＝无从比较。</summary>
    private static double FingerprintDistance(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0) return double.PositiveInfinity;
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    /// <summary>重复判据阈值：指纹平均绝对差小于此值＝两条内容几乎相同。
    /// 取 3.0（灰度尺度）——真实不同内容的条带差值通常在十几以上，而亚像素抖动只有 1~2。</summary>
    private const double DuplicateAppendMaxDistance = 3.0;

    /// <summary>重复守卫只对**小位移**生效的上限（像素）。见 <see cref="IsDuplicateAppend"/> 的 ③。</summary>
    private const int DuplicateGuardMaxAddH = 64;

    /// <summary>连续拦截多少次后强制放行一条，避免判据在极端页面上把采集彻底卡死。</summary>
    private const int DuplicateGuardMaxStreak = 12;

    /// <summary>
    /// 本次要追加的这一条是不是"上一条的重复"（2026-10-01，修长截图偶发重复拼接）。
    ///
    /// <para>三条刻意的收窄，每一条都为了**不误伤正常滚动**：
    /// ① 只与**上一条**比（不扫全画布）——重复拼接是连续发生的，拦住连续雷同即打断整条链，
    ///    且代价恒定、不随图长增长；
    /// ② 指纹取不到（条带太窄、锁位图失败）时**一律放行**——守卫的职责是拦住确凿的重复，
    ///    而不是在信息不足时阻断采集；
    /// ③ 🔴 **只在 <c>addH ≤ 64</c> 时生效**。这是最要紧的一条：真实滚动一次几十到几百像素，
    ///    追加的内容必然不同；而位移被估小才会出现"每次只追加一小条"。大位移时页面上**确实**
    ///    可能有大段雷同内容（连续空行、等宽表格、纯色区），那时拦下来就是真的丢内容。
    ///    把守卫限制在小位移区间，既覆盖了本缺陷的形态，又让正常滚动完全不受影响。
    /// ④ 连续拦 <see cref="DuplicateGuardMaxStreak"/> 次即强制放行一条并留痕：万一某个页面真的
    ///    在小位移下连续产出雷同内容，也不至于把整次长截图卡死（宁可留一处小瑕疵，不要彻底不动）。</para>
    /// </summary>
    private bool IsDuplicateAppend(byte[]? fingerprint, int addH, string side)
    {
        if (fingerprint == null) return false;                    // ②
        if (addH > DuplicateGuardMaxAddH) return false;           // ③
        double dist = FingerprintDistance(_lastAppendedFingerprint, fingerprint);
        if (double.IsPositiveInfinity(dist)) return false;        // 还没有上一条可比
        if (dist >= DuplicateAppendMaxDistance)
        {
            _duplicateAppendBlocked = 0;
            return false;
        }
        _duplicateAppendBlocked++;
        if (_duplicateAppendBlocked > DuplicateGuardMaxStreak)    // ④
        {
            AppLogger.Info("LongScroll.DuplicateAppendForcePass: 连续拦截 " + _duplicateAppendBlocked
                + " 次后强制放行（side=" + side + ", addH=" + addH + ", dist=" + dist.ToString("0.00") + "）"
                + " —— 该页面在小位移下确实连续产出雷同内容，或判据过严，请据此条复核");
            _duplicateAppendBlocked = 0;
            _lastAppendedFingerprint = fingerprint;
            return false;
        }
        AppLogger.Info("LongScroll.DuplicateAppendBlocked: 拦下一条重复追加（side=" + side
            + ", addH=" + addH + ", dist=" + dist.ToString("0.00")
            + ", streak=" + _duplicateAppendBlocked + "）—— 位移疑被估小，本帧按未拼接处理");
        _lastCanvasAction = "重复内容已跳过";
        return true;
    }

    private bool TryApplyFrameToCanvas(Bitmap curBmp, int newViewportY, bool directionIsUp, int staticTop, int staticBottom, out bool positionUpdated)
    {
        positionUpdated = false;
        if (curBmp == null) return false;

        try
        {
            int viewH = GetAlongLength(curBmp);
            int viewTop = newViewportY;
            int viewBottom = newViewportY + viewH;
            bool stitched = false;

            lock (_stripsLock)
            {
                if (_totalHeight <= 0 || _strips.Count == 0) return false;
                if (_viewportHeight <= 0) _viewportHeight = viewH;

                int viewportDelta = newViewportY - _currentViewportY;
                if ((directionIsUp && viewportDelta >= 0) || (!directionIsUp && viewportDelta <= 0))
                {
                    _lastCanvasAction = "滚动方向相反";
                    return false;
                }

                if (viewBottom <= viewTop)
                {
                    _lastCanvasAction = "视口越界";
                    return false;
                }

                int topOverflow = _canvasTopY - viewTop;
                int bottomOverflow = viewBottom - _canvasBottomY;

                if (topOverflow > 0 && bottomOverflow > 0)
                {
                    _lastCanvasAction = "视口越界";
                    return false;
                }

                if (directionIsUp && bottomOverflow > 0 && topOverflow <= 0)
                {
                    _lastCanvasAction = "单侧越界";
                    return false;
                }
                if (!directionIsUp && topOverflow > 0 && bottomOverflow <= 0)
                {
                    _lastCanvasAction = "单侧越界";
                    return false;
                }

                if (topOverflow <= 0 && bottomOverflow <= 0)
                {
                    _currentViewportY = newViewportY;
                    _lastCanvasAction = "视口平移";
                    positionUpdated = true;
                    return false;
                }

                int appendDirection = topOverflow > 0 ? -1 : bottomOverflow > 0 ? 1 : 0;
                if (appendDirection != 0)
                {
                    _captureDirection = appendDirection;
                }


                int remaining = _options.MaxAlongLengthPx - _totalHeight;
                if (remaining <= 0)
                {
                    _maxHeightReached = true;
                    _lastCanvasAction = "已达长度上限";
                    return false;
                }

                if (topOverflow > 0)
                {
                    int addH = topOverflow;
                    if (addH > remaining) addH = remaining;
                    int dynBottom = viewH - staticBottom;
                    if (dynBottom < staticTop) dynBottom = staticTop;
                    if (addH > dynBottom - staticTop) addH = dynBottom - staticTop;
                    if (addH <= 0)
                    {
                        _currentViewportY = newViewportY;
                        _lastCanvasAction = "顶部扩展完成";
                        positionUpdated = true;
                        return false;
                    }

                    int srcY = staticTop;
                    if (srcY + addH > viewH) addH = viewH - srcY;
                    if (addH <= 0) return false;

                    // 与向下追加保持同一套“新增区 + 重叠刷新”策略。慢速滚动时 addH 只有几像素，
                    // 若只把这条细片永久钉在画布顶部，捕获时机或位移估算的 1px 波动都会变成
                    // 一条可见断层；带入当前帧后续约 1/3 视口，让新帧覆盖接缝附近的旧画面。
                    int viewForOverlay = _viewportHeight > 0 ? _viewportHeight : viewH;
                    int overlayH = _options.Axis == ScrollAxis.Horizontal
                        ? 0
                        : viewForOverlay / 3 - addH;
                    if (overlayH < 0) overlayH = 0;
                    int maxOverlay = dynBottom - srcY - addH;
                    if (maxOverlay < 0) maxOverlay = 0;
                    if (overlayH > maxOverlay) overlayH = maxOverlay;

                    int cropH = addH + overlayH;
                    if (_options.Axis == ScrollAxis.Vertical && addH == topOverflow && staticTop == 0 && staticBottom == 0)
                    {
                        int cut = LongScrollSeamSelector.Select(_strips, curBmp, newViewportY - _canvasTopY,
                            srcY + cropH, srcY + addH, dynBottom);
                        if (cut != srcY + cropH)
                            AppLogger.Info("LongScroll.SeamAdjusted: side=top, from=" + (srcY + cropH) + ", to=" + cut + ", add=" + addH);
                        cropH = cut - srcY;
                        overlayH = cropH - addH;
                    }
                    if (srcY + cropH > dynBottom) cropH = dynBottom - srcY;
                    if (cropH <= 0) return false;
                    if (overlayH > cropH - addH) overlayH = cropH - addH;
                    if (overlayH < 0) overlayH = 0;

                    // 重复守卫：与上一次追加的那条几乎相同 ⇒ 位移被估小了，这一条是同内容的再一次追加
                    var fpTop = ComputeStripFingerprint(curBmp, CreateStripRectangle(curBmp, srcY, addH));
                    if (IsDuplicateAppend(fpTop, addH, "top"))
                    {
                        // 守卫只跳过这次条带，视口本身仍然真实移动。推进视口坐标但不扩画布，
                        // 让后续帧把被跳过的小段累计起来；否则每帧都从旧坐标重试，微小位移
                        // 会被连续误判并造成向上滚动时画布长期落后。
                        _currentViewportY = newViewportY;
                        positionUpdated = true;
                        return false;
                    }
                    _lastAppendedFingerprint = fpTop;

                    Bitmap stripBmp = curBmp.Clone(CreateStripRectangle(curBmp, srcY, cropH), curBmp.PixelFormat);
                    for (int i = 0; i < _strips.Count; i++)
                    {
                        _strips[i].OffsetY(addH);
                    }
                    // 顶部重叠位于条带尾部，StitchedStrip 的 OverlapLength 只描述“头部重叠”，
                    // 因此保持为 0；把当前帧追加到绘制顺序末尾，使它覆盖已平移旧条带的重叠区。
                    _strips.Add(CreateStrip(stripBmp, 0));
                    _totalHeight += addH;
                    _canvasTopY -= addH;
                    _currentViewportY = newViewportY;
                    if (_totalHeight >= _options.MaxAlongLengthPx) _maxHeightReached = true;
                    _lastCanvasAction = "顶部扩展 +" + addH + "px";
                    stitched = true;
                }
                else if (bottomOverflow > 0)
                {
                    int addH = bottomOverflow;
                    if (addH > remaining) addH = remaining;
                    int dynBottom = viewH - staticBottom;
                    int srcY = dynBottom - addH;
                    if (srcY < staticTop) srcY = staticTop;
                    if (srcY + addH > dynBottom) addH = dynBottom - srcY;
                    if (addH <= 0)
                    {
                        _currentViewportY = newViewportY;
                        _lastCanvasAction = "底部扩展完成";
                        positionUpdated = true;
                        return false;
                    }

                    if (srcY + addH > viewH) addH = viewH - srcY;
                    if (addH <= 0) return false;

                    int cropEnd = viewH;
                    int viewForOverlay = _viewportHeight > 0 ? _viewportHeight : viewH;
                    // The vertical path keeps a generous overlap so sticky headers/footers can
                    // be refreshed. Horizontally this rewrote up to one third of the already
                    // composed canvas. Repeated cards can offer several plausible alignments,
                    // allowing a later strip to overwrite correct history. Horizontal fixed
                    // edges are dedicated layers, so append only genuinely new trailing pixels.
                    int overlayH = _options.Axis == ScrollAxis.Horizontal
                        ? 0
                        : viewForOverlay / 3 - addH;
                    if (overlayH < 0) overlayH = 0;
                    int minOverlayForFixedBottom = staticBottom > 0 ? staticBottom + 12 : 0;
                    if (overlayH < minOverlayForFixedBottom) overlayH = minOverlayForFixedBottom;
                    int maxOverlay = cropEnd - staticTop - addH;
                    if (maxOverlay < 0) maxOverlay = 0;
                    if (overlayH > maxOverlay) overlayH = maxOverlay;

                    int cropH = addH + overlayH;
                    int cropY = cropEnd - cropH;
                    if (_options.Axis == ScrollAxis.Vertical && addH == bottomOverflow && staticTop == 0 && staticBottom == 0)
                    {
                        int cut = LongScrollSeamSelector.Select(_strips, curBmp, newViewportY - _canvasTopY,
                            cropY, staticTop, cropEnd - addH - minOverlayForFixedBottom);
                        if (cut != cropY)
                            AppLogger.Info("LongScroll.SeamAdjusted: side=bottom, from=" + cropY + ", to=" + cut + ", add=" + addH);
                        cropY = cut;
                        cropH = cropEnd - cropY;
                        overlayH = cropH - addH;
                    }
                    if (cropY < staticTop)
                    {
                        cropY = staticTop;
                        cropH = cropEnd - cropY;
                    }

                    if (cropY + cropH > viewH) cropH = viewH - cropY;
                    if (cropH <= 0) return false;
                    if (overlayH > cropH - addH) overlayH = cropH - addH;
                    if (overlayH < 0) overlayH = 0;

                    // 重复守卫：按**真正新增的那一段**（srcY 起 addH 行）取指纹，不含 overlay 复写段
                    // ——overlay 本来就是故意重写已有像素的，把它算进去会让指纹恒定偏向"重复"。
                    var fpBottom = ComputeStripFingerprint(curBmp, CreateStripRectangle(curBmp, srcY, addH));
                    if (IsDuplicateAppend(fpBottom, addH, "bottom"))
                    {
                        // 同顶部分支：跳过条带不等于视口没有移动，累计后再整体补入。
                        _currentViewportY = newViewportY;
                        positionUpdated = true;
                        return false;
                    }
                    _lastAppendedFingerprint = fpBottom;

                    Bitmap stripBmp = curBmp.Clone(CreateStripRectangle(curBmp, cropY, cropH), curBmp.PixelFormat);
                    int y = _totalHeight - overlayH;
                    _strips.Add(CreateStrip(stripBmp, y, overlayH));
                    _totalHeight += addH;
                    _canvasBottomY += addH;
                    _currentViewportY = newViewportY;
                    if (_totalHeight >= _options.MaxAlongLengthPx) _maxHeightReached = true;
                    _lastCanvasAction = "底部扩展 +" + addH + "px";
                    stitched = true;
                }

                if (stitched) EvaluateBudgetPressureLocked();
            }

            return stitched;
        }
        catch
            {
                _lastCanvasAction = "视口越界";
            return false;
        }
    }

    /// <summary>
    /// 运行期内存复查（调用方持有 <c>_stripsLock</c>）：每攒 20 条回读一次当前空余物理内存，
    /// 已攒内容一旦超过机器此刻拿得出的预算就<b>单向</b>收口——不解除、不回头，否则边界会随
    /// 内存起伏"到顶了又能滚"。收口后已攒内容全部保留，仍由用户点保存出图；
    /// 目的是把失败挡在"停止追加"这一刻，而不是留到最后合成时抛异常把整张图丢掉。
    /// ⚠ 读不到内存真值时一律放行：宁可持续采集，也不因一次查询失败打断正在滚动的用户。
    /// </summary>
    private void EvaluateBudgetPressureLocked()
    {
        if (_maxHeightReached) return;
        if (++_budgetRecheckAppends < BudgetRecheckAppends) return;
        _budgetRecheckAppends = 0;

        long allocated = LongScrollBudget.BytesForLength(_options.Axis, _region, _totalHeight);
        long allowed = LongScrollBudget.CurrentAllowedBytes();
        if (allocated <= allowed) return;

        _budgetExceeded = true;
        _maxHeightReached = true;
        _lastCanvasAction = "内存预算耗尽";
        AppLogger.Warn("LongScroll.BudgetPressure 提前收口 allocated=" + LongScrollBudget.ToMb(allocated)
            + "MB allowed=" + LongScrollBudget.ToMb(allowed) + "MB total=" + _totalHeight
            + "px region=" + _region);
    }

    private void UpdatePreview()
    {
        lock (_previewUpdateLock) UpdatePreviewCore();
    }

    private void UpdatePreviewCore()
    {
        if (!EnablePreviewWindow || _previewWindow == null) return;

        // Time-based throttle: cap preview refresh at PreviewUpdateIntervalMs.
        // Dropped calls raise the pending flag; the capture loop flushes it later.
        long now = Stopwatch.GetTimestamp();
        long lastPreviewTick = Volatile.Read(ref _lastPreviewUpdateTick);
        if (lastPreviewTick != 0 && now - lastPreviewTick < Stopwatch.Frequency * PreviewUpdateIntervalMs / 1000)
        {
            Interlocked.Exchange(ref _previewUpdatePending, 1);
            return;
        }
        Volatile.Write(ref _lastPreviewUpdateTick, now);
        Interlocked.Exchange(ref _previewUpdatePending, 0);

        // [Preview Diag] 只有当真正走到这里（未被节流挂起）才有快照被构建并推送，
        // 用于定位“向上滚动到一定高度后预览不再更新”。
        if (AppLogger.DetailedLoggingEnabled)
        {
            int diagStrips = 0;
            int diagTotal = -1;
            lock (_stripsLock)
            {
                diagStrips = _strips.Count;
                diagTotal = _totalHeight;
            }
            AppLogger.Info("LongScroll.UpdatePreviewPush: total=" + diagTotal
                + ", strips=" + diagStrips
                + ", pendingInfo=" + Volatile.Read(ref _previewUpdatePending));
        }

        try
        {
            long pushStarted = Stopwatch.GetTimestamp();
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
            // 固定层改为传引用（非拥有借用）：预览渲染期间会话保证位图存活，
            // 被替换的旧位图转入 _retiredStripBitmaps 延迟释放（与条带位图同一生命周期契约）。
            // 旧实现在此每 16ms 于 _stripsLock 内全量 Clone 两张全宽位图，
            // MB 级分配 × 60fps 造成 LOH churn/GC 卡顿并阻塞捕获线程。
            Bitmap? fixedTopPreview;
            Bitmap? fixedBottomPreview;
            lock (_stripsLock)
            {
                if (_strips.Count == 0) return;
                snapshot = new List<StitchedStrip>(_strips);
                totalHeightSnapshot = _totalHeight > 0
                    ? _totalHeight
                    : CalculateCompositedHeight(_strips, _options.Axis == ScrollAxis.Horizontal ? _region.Width : _region.Height);
                currentViewportYSnapshot = _currentViewportY - _canvasTopY;
                if (currentViewportYSnapshot < 0) currentViewportYSnapshot = 0;
                viewportHeightSnapshot = _viewportHeight > 0 ? _viewportHeight : (_options.Axis == ScrollAxis.Horizontal ? _region.Width : _region.Height);
                fixedTopSnapshot = _fixedTopHeight;
                fixedBottomSnapshot = _fixedBottomHeight;
                fixedTopPreview = _topBoundaryFixedBitmap;
                fixedBottomPreview = _bottomBoundaryFixedBitmap;
            }

            int displayViewportHeightSnapshot = Math.Max(1, viewportHeightSnapshot + fixedTopSnapshot + fixedBottomSnapshot);
            int displayTotalHeightSnapshot = Math.Max(totalHeightSnapshot, totalHeightSnapshot + fixedTopSnapshot + fixedBottomSnapshot);

            _previewWindow.UpdateFrame(frameW, frameH);
            _previewWindow.UpdateViewport(_options.Axis, currentViewportYSnapshot, displayViewportHeightSnapshot, displayTotalHeightSnapshot, fixedTopSnapshot, fixedBottomSnapshot);
            _previewWindow.SetAnchor(_region, dockRight);
            Rectangle? controlRect = _hasLastControlWindowRect
                ? new Rectangle(_lastControlWindowX, _lastControlWindowY, _lastControlWindowW, _lastControlWindowH)
                : null;
            _previewWindow.SetPlacementContext(_region, controlRect, _captureDirection);
            _previewWindow.UpdateStrips(_options.Axis, snapshot, totalHeightSnapshot, fixedTopPreview, fixedBottomPreview);
            _previewPushTicks += Stopwatch.GetTimestamp() - pushStarted;
            _previewPushes++;
        }
        catch
        {
        }
    }

    private int GetDisplayHeight()
    {
        lock (_stripsLock)
        {
            if (_strips.Count == 0) return Math.Max(0, _options.Axis == ScrollAxis.Horizontal ? _region.Width : _region.Height);
            int stripH = CalculateCompositedHeight(_strips, _totalHeight);
            int fixedTotal = _fixedTopHeight + _fixedBottomHeight;
            return stripH + fixedTotal;
        }
    }

    private string FormatOutputSize(int alongLength)
    {
        int safeAlong = Math.Max(0, alongLength);
        int crossLength = Math.Max(0, _region.Height - _crossTopCut - _crossBottomCut);
        return _options.Axis == ScrollAxis.Horizontal
            ? (safeAlong + " × " + crossLength + "px")
            : (_region.Width + " × " + safeAlong + "px");
    }

    private void LogStitchDecision(string stage, int delta, bool directionIsUp, int staticTop, int staticBottom, bool stitched, bool positionUpdated)
    {
        int n = Interlocked.Increment(ref _stitchDecisionLogCount);
        // 热路径日志抽样：拒绝/提升帧全记（诊断价值所在）；估计/接受帧只在
        // 大会话抽样（每 100 帧）或大位移（可能撞护栏）时记录，消除常态写盘。
        bool alwaysLog = stage != "estimate" && stage != "accepted";
        // ⚠ **小位移必须全记**（2026-10-01 加）：原抽样条件是 `delta < 300` 才允许跳过，
        // 恰好把"位移被估小"这件事的证据整个过滤掉了——而那正是偶发重复拼接要查的东西
        // （用户 2026-10-01 实报，复现时日志里一条都看不到）。小位移本就稀少（正常滚动
        // 一次几十到几百像素），全记不会形成常态写盘；真正该抽样的是中段位移。
        bool smallDelta = delta > 0 && delta <= DuplicateGuardMaxAddH;
        if (!alwaysLog && !smallDelta && n > 20 && n % 100 != 0 && delta < 300) return;

        int viewportY;
        int canvasTop;
        int canvasBottom;
        int totalHeight;
        lock (_stripsLock)
        {
            viewportY = _currentViewportY;
            canvasTop = _canvasTopY;
            canvasBottom = _canvasBottomY;
            totalHeight = _totalHeight;
        }

        AppLogger.Info(
            "LongScroll.StitchDecision stage=" + stage
            + ", n=" + n
            + ", delta=" + delta
            + ", dir=" + (directionIsUp ? "up" : "down")
            + ", static=" + staticTop + "/" + staticBottom
            + ", stitched=" + stitched
            + ", positionUpdated=" + positionUpdated
            + ", viewportY=" + viewportY
            + ", canvas=" + canvasTop + ".." + canvasBottom
            + ", total=" + totalHeight
            + ", reason=" + ImageStitcher.GetLastDeltaSource()
            + ", score=" + ImageStitcher.GetLastDeltaScore());
    }

    private void UpdateStatus()
    {
        if (_controlWindow == null) return;
        long nowTick = Stopwatch.GetTimestamp();
        long lastTick = Interlocked.Read(ref _lastStatusUpdateTick);
        long minTicks = Stopwatch.Frequency * StatusUpdateIntervalMs / 1000;
        if (lastTick != 0 && nowTick - lastTick >= 0 && nowTick - lastTick < minTicks) return;
        Interlocked.Exchange(ref _lastStatusUpdateTick, nowTick);
        string status = FormatOutputSize(GetDisplayHeight());
        string metrics = FormatBudgetMetrics();
        bool warn = _maxHeightReached;
        int pressurePct = 0;

        // hint 驻留：周期刷新默认无 hint，但若最近刚显示过提示且未到驻留期，
        // 保留当前 hint 文本（状态栏会以“无 hint”调用覆盖显示中的提示）。
        string hint = string.Empty;
        long hintShown = Interlocked.Read(ref _hintShownTick);
        if (hintShown != 0 && nowTick - hintShown < Stopwatch.Frequency * HintMinHoldMs / 1000)
        {
            hint = null!; // 语义标记：null = 保持现状，SetState 内部跳过覆盖
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _controlWindow?.SetState(status, metrics, hint, warn, pressurePct,
                    showHint: Interlocked.Read(ref _realignmentHintTick) != 0);
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
            // 措辞必须与实际操作一致：到顶只是"不再追加新内容"，出图仍由用户点保存触发，
            // 旧文案承诺的"自动保存"从不发生（2026-10-02 核查）。
            UpdateStatusHint(_budgetExceeded
                ? "这台机器此刻内存吃紧，已停止追加，请点保存"
                : "已达本次上限，已停止追加，请点保存", true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 控制条第二行只在到顶时显示原因；丢帧与匹配失败不是成图缺失的证明。
    /// 上限数值只看日志 <c>LongScroll.Budget … maxAlong=</c>。
    /// </summary>
    private string FormatBudgetMetrics()
    {
        if (_maxHeightReached)
            return _budgetExceeded ? "内存吃紧·已停止追加" : "已达本次上限·可点保存";
        return string.Empty;
    }

    /// <summary>成功接入画面时立即解除重新对齐提示，不等待普通提示的最短驻留期。</summary>
    private void ClearRealignmentHint()
    {
        long shownTick = Interlocked.Exchange(ref _realignmentHintTick, 0);
        if (shownTick == 0) return;
        Interlocked.CompareExchange(ref _hintShownTick, 0, shownTick);
        Interlocked.Exchange(ref _lastStatusUpdateTick, 0);
    }

    private void UpdateStatusHint(string hint, bool warn, bool realigning = false)
    {
        if (_controlWindow == null) return;
        // 到达上限的提示有明确依据，不能被匹配困难的过程提示覆盖。
        if (realigning && _maxHeightReached) return;
        long shownTick = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _hintShownTick, shownTick);
        Interlocked.Exchange(ref _realignmentHintTick, realigning ? shownTick : 0);
        string status = FormatOutputSize(GetDisplayHeight());
        string metrics = FormatBudgetMetrics();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (realigning && Interlocked.Read(ref _realignmentHintTick) != shownTick) return;
                _controlWindow?.SetState(status, metrics, hint, warn, 0, showHint: realigning);
                try { PlaceControlWindow(); } catch { }
                if (realigning)
                {
                    // 即使停止滚动、不再刷新状态，也按时收起；旧计时器不能清掉新提示。
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (Interlocked.CompareExchange(ref _realignmentHintTick, 0, shownTick) != shownTick) return;
                        Interlocked.CompareExchange(ref _hintShownTick, 0, shownTick);
                        Interlocked.Exchange(ref _lastStatusUpdateTick, 0);
                        UpdateStatus();
                    }, TimeSpan.FromMilliseconds(RealignmentHintHoldMs));
                }
            }
            catch
            {
                try { _controlWindow?.SetStatus(status); } catch { }
            }
        }, DispatcherPriority.Background);
    }

    private Task FinishInternalAsync(bool copyToClipboard, string? saveFilePath, bool canceled)
        => FinishInternalCoreAsync(copyToClipboard, saveFilePath, canceled, false);

    private Task FinishAsStickerAsync()
        => Volatile.Read(ref _saveDialogOpen) == 0
            ? FinishInternalCoreAsync(false, null, false, true) : Task.CompletedTask;

    private async Task FinishInternalCoreAsync(bool copyToClipboard, string? saveFilePath, bool canceled, bool createSticker)
    {
        if (Interlocked.Exchange(ref _finishStarted, 1) == 1) return;
        StopCompletionShortcuts();

        LongScrollMemoryDiagnostics.LogImage(
            "LongScroll.BeforeFinish",
            _options.Axis == ScrollAxis.Horizontal ? _totalHeight : _region.Width,
            _options.Axis == ScrollAxis.Horizontal ? _region.Height : _totalHeight);

        _isRunning = false;
        StopAutoScrollInternal();
        UninstallMouseHook();
        try { _captureQueue?.CompleteAdding(); } catch { }
        try { _cts?.Cancel(); } catch { }

        try
        {
            if (_controlWindow != null)
            {
                _controlWindow.SetStatus(FormatOutputSize(GetDisplayHeight()));
            }
        }
        catch { }

        LongScrollResult result;
        try
        {
            await Task.Run(() =>
            {
                if (_captureThread != null && _captureThread.IsAlive) _captureThread.Join();
                _stitchTask?.GetAwaiter().GetResult();
            });

            await Dispatcher.UIThread.InvokeAsync(() => _previewWindow?.ShutdownRendering());
            double toMs = 1000.0 / Stopwatch.Frequency;
            AppLogger.Info($"LongScroll.PipelinePerf frames={_processedFrames} "
                + $"processMeanMs={_processingTicks * toMs / Math.Max(1, _processedFrames):F2} "
                + $"queueMeanMs={_queueTicks * toMs / Math.Max(1, _processedFrames):F2} "
                + $"queueMaxMs={_maxQueueTicks * toMs:F2} "
                + $"featureCalls={_featureCalls} featureMeanMs={_featureTicks * toMs / Math.Max(1, _featureCalls):F2} "
                + $"captureBackpressureMs={_captureBackpressureTicks * toMs:F2} "
                + $"animationIdleFrames={_stationaryAnimationFrames} "
                + $"previewPushMeanMs={_previewPushTicks * toMs / Math.Max(1, _previewPushes):F2}");

            if (canceled)
            {
                result = LongScrollResult.Cancel("长截图已取消");
            }
            else
            {
                result = await SaveFinalAsync(copyToClipboard, saveFilePath, createSticker);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LegacyLongScrollSession.FinishInternalAsync 异常", ex);
            result = LongScrollResult.Cancel("长截图发生异常：" + ex.Message);
        }
        finally
        {
            await CleanupAfterFinishAsync();
        }

        if (_completionTcs?.TrySetResult(result) != true) result.Dispose();
    }

    private async Task<LongScrollResult> SaveFinalAsync(bool copyToClipboard, string? saveFilePath, bool createSticker)
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
                return LongScrollResult.Cancel("长截图发生异常：");
            }

            int middleH = CalculateCompositedHeight(finalStrips, _totalHeight);
            if (middleH <= 0) middleH = _totalHeight;
            int estimatedWidth = _options.Axis == ScrollAxis.Horizontal ? middleH : _region.Width;
            int estimatedHeight = _options.Axis == ScrollAxis.Horizontal ? _region.Height : middleH;
            LongScrollMemoryDiagnostics.LogImage("LongScroll.BeforeCompose", estimatedWidth, estimatedHeight);
            Bitmap? ownedOutput = LongScrollComposer.Compose(finalStrips, middleH, finalTopFixed, finalBottomFixed, _options.Axis, _options);
            try
            {
                Bitmap output = ownedOutput;
                LongScrollMemoryDiagnostics.LogImage("LongScroll.AfterCompose", output.Width, output.Height);

                if (createSticker)
                {
                    // 成片直接交接，避免经剪贴板编码／解码或再复制一整张长图。
                    var result = LongScrollResult.FromBitmap(output, "长截图已贴图");
                    ownedOutput = null;
                    return result;
                }

                if (_controlWindow != null)
                {
                    _controlWindow.SetStatus(FormatOutputSize(middleH));
                }

                if (copyToClipboard)
                {
                    LongScrollMemoryDiagnostics.LogImage("LongScroll.BeforeClipboard", output.Width, output.Height);
                    try
                    {
                        // 本会话独占成片到 await 完成，无需为了异步写入再克隆整张长图。
                        if (!await ClipboardService.SetImageAsync(output))
                            return LongScrollResult.Cancel("本次复制已被更新的复制请求取代");
                        AppLogger.Info("LegacyLongScrollSession: 长截图保存流程完成");

                        // 历史：复制到剪贴板的长截图同样录入（2026-09-10）。
                        // 此前该分支 SavedPath 为空串、CaptureSession 只认文件入口，导致剪贴板长截图
                        // 完全不进历史、长截图筛选栏也看不到。趁合成位图还在（using 释放前）走位图入口，
                        // TryRecord 同步克隆后由后台线程编码写盘，不阻塞返回。
                        CaptureHistoryStore.TryRecord(output, CaptureHistorySource.LongScroll, null, _settings);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("LegacyLongScrollSession: 保存流程发生异常：" + ex.Message, ex);
                        return LongScrollResult.Cancel("长截图发生异常：" + ex.Message);
                    }
                    LongScrollMemoryDiagnostics.LogImage("LongScroll.AfterClipboard", output.Width, output.Height);

                    try { _controlWindow?.SetStatus(FormatOutputSize(middleH)); } catch { }
                    return LongScrollResult.Saved(string.Empty, "长截图已复制到剪贴板");
                }

                string path = string.Empty;
                string? saveError = null;
                try
                {
                    path = string.IsNullOrWhiteSpace(saveFilePath) ? ResolveDefaultOutputPath() : NormalizeSaveAsPath(saveFilePath);
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    await Task.Run(() => ImageExportService.Save(output, path));
                    LongScrollMemoryDiagnostics.LogImage("LongScroll.AfterSave", output.Width, output.Height);
                }
                catch (Exception ex)
                {
                    saveError = ex.Message;
                    AppLogger.Error("LegacyLongScrollSession: 保存文件发生异常：" + ex.Message, ex);
                }

                string message;
                if (saveError != null) return LongScrollResult.Cancel("长截图保存失败：" + saveError);
                if (!string.IsNullOrWhiteSpace(saveFilePath)) message = "长截图已保存至：" + path;
                else message = "长截图已保存：" + path;

                try { _controlWindow?.SetStatus(FormatOutputSize(middleH)); } catch { }
                return LongScrollResult.Saved(path, message);
            }
            finally { ownedOutput?.Dispose(); }
        }
        finally
        {
            try { finalTopFixed?.Dispose(); } catch { }
            try { finalBottomFixed?.Dispose(); } catch { }
            DisposeStrips(finalStrips);
        }
    }

    public int CalculateCompositedHeight(IReadOnlyList<StitchedStrip> strips, int fallbackHeight)
    {
        try
        {
            int minAlong = int.MaxValue;
            int maxAlong = int.MinValue;
            if (strips != null)
            {
                for (int i = 0; i < strips.Count; i++)
                {
                    StitchedStrip? s = strips[i];
                    if (s == null) continue;
                    if (s.AlongStart < minAlong) minAlong = s.AlongStart;
                    if (s.AlongEnd > maxAlong) maxAlong = s.AlongEnd;
                }
            }

            if (minAlong != int.MaxValue && maxAlong > minAlong)
            {
                return checked(maxAlong - minAlong);
            }
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
            try
            {
                BitmapData db = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, format);
                try { return LongScrollFrameComparer.EqualRows(da.Scan0, da.Stride, db.Scan0, db.Stride, a.Width, a.Height); }
                finally { b.UnlockBits(db); }
            }
            finally { a.UnlockBits(da); }
        }
        catch { return false; }
    }

    /// <summary>
    /// 廉价运动判别：滚动会改变视口内几乎所有行，悬停/动画只改少数行。
    /// 稀疏采样（每 ~1/64 行取 1 行、每 ~1/32 列取 1 点，绿色通道差 >12 记变化），
    /// 行内 ≥25% 采样点变化记为"该行变化"；变化行占比 ≥30% 视为滚动。
    /// 尺寸不一致等异常情况按"是滚动"放行，交由后续流程处理（保守方向）。
    /// </summary>
    private static unsafe bool IsMotionScrollLike(Bitmap prev, Bitmap cur)
    {
        if (prev.Width != cur.Width || prev.Height != cur.Height || prev.Width < 8 || prev.Height < 8) return true;
        try
        {
            BitmapData pd = prev.LockBits(new Rectangle(0, 0, prev.Width, prev.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            BitmapData cd;
            try { cd = cur.LockBits(new Rectangle(0, 0, cur.Width, cur.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb); }
            catch { prev.UnlockBits(pd); return true; }
            try
            {
                int rowStep = Math.Max(1, prev.Height / 64);
                int colStep = Math.Max(1, prev.Width / 32);
                int rowsSampled = 0;
                int rowsChanged = 0;
                for (int y = 0; y < prev.Height; y += rowStep)
                {
                    byte* prow = (byte*)pd.Scan0 + y * pd.Stride;
                    byte* crow = (byte*)cd.Scan0 + y * cd.Stride;
                    int sampled = 0;
                    int changed = 0;
                    for (int x = 0; x < prev.Width; x += colStep)
                    {
                        int off = x * 4;
                        sampled++;
                        int d = prow[off + 1] - crow[off + 1];
                        if (d > 12 || d < -12) changed++;
                    }
                    rowsSampled++;
                    if (sampled > 0 && changed * 4 >= sampled) rowsChanged++;
                }
                return rowsSampled > 0 && rowsChanged * 10 >= rowsSampled * 3;
            }
            finally
            {
                prev.UnlockBits(pd);
                cur.UnlockBits(cd);
            }
        }
        catch
        {
            return true;
        }
    }

    /// <summary>控制条不抢前台：复用既有热键管理器，只在本次会话期间注册完成／取消键。</summary>
    private void StartCompletionShortcuts()
    {
        if (_cancelShortcut != null || _finishShortcut != null) return;
        int version = Interlocked.Increment(ref _shortcutScopeVersion);
        HotKeyManager? Register(LongScrollShortcut key, int id, bool cancel)
        {
            if (key.VirtualKey == 0) return null;
            var manager = new HotKeyManager(() => Dispatcher.UIThread.Post(() =>
            {
                // 对话框打开、重新注册或会话结束后，旧消息泵中的请求均作废。
                if (version != Volatile.Read(ref _shortcutScopeVersion) || !_isRunning
                    || Volatile.Read(ref _finishStarted) != 0) return;
                if (cancel) _controlWindow?.RequestCancel();
                else _controlWindow?.RequestFinish();
            }), id);
            if (manager.Register(key.Modifiers, key.VirtualKey)) return manager;
            AppLogger.Warn("LongScroll.Shortcut: 注册失败 " + key.DisplayText + ", win32=" + manager.LastRegisterError);
            manager.Dispose();
            return null;
        }
        try
        {
            _cancelShortcut = Register(_options.CancelShortcut, 0x9301, true);
            _finishShortcut = Register(_options.FinishShortcut, 0x9302, false);
        }
        catch (Exception ex)
        {
            StopCompletionShortcuts();
            AppLogger.Error("LongScroll.Shortcut: 注册会话键失败", ex);
        }
    }

    private void StopCompletionShortcuts()
    {
        Interlocked.Increment(ref _shortcutScopeVersion);
        _cancelShortcut?.Dispose();
        _finishShortcut?.Dispose();
        _cancelShortcut = null;
        _finishShortcut = null;
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            AppLogger.Info("LongScroll.MouseHook: install skipped; already installed. handle=" + _mouseHook);
            return;
        }
        try
        {
            _mouseProc = HookMouseProc;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                AppLogger.Warn("LongScroll.MouseHook: install failed. win32=" + error
                    + ", axis=" + _options.Axis + ", processIntegrityMayBlockInput=True");
                _mouseProc = null;
            }
            else
            {
                AppLogger.Info("LongScroll.MouseHook: installed. handle=" + _mouseHook
                    + ", axis=" + _options.Axis
                    + ", horizontalSources=HWHEEL|ShiftWheel|ScrollbarDrag");
            }
        }
        catch (Exception ex)
        {
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
            AppLogger.Warn("LongScroll.MouseHook: install exception: " + ex.Message);
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            IntPtr handle = _mouseHook;
            try
            {
                bool removed = NativeMethods.UnhookWindowsHookEx(handle);
                AppLogger.Info("LongScroll.MouseHook: uninstalled=" + removed + ", handle=" + handle
                    + (removed ? string.Empty : ", win32=" + Marshal.GetLastWin32Error()));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LongScroll.MouseHook: uninstall exception: " + ex.Message);
            }
            _mouseHook = IntPtr.Zero;
        }

        Interlocked.Exchange(ref _horizontalScrollbarDragActive, 0);
        Interlocked.Exchange(ref _lastHorizontalScrollbarDragDir, 0);
        Interlocked.Exchange(ref _lastHorizontalScrollbarDragDirectionTick, 0);
        _mouseProc = null;
    }

    private int GetRecentWheelDirection()
    {
        return GetRecentWheelDirection(out _);
    }

    private int GetRecentWheelDirection(out string source)
    {
        source = "None";
        int direction = Volatile.Read(ref _lastWheelDir);
        if (direction == 0) return 0;

        long tick = Interlocked.Read(ref _lastWheelDirectionTick);
        if (tick <= 0) return 0;
        long elapsed = Stopwatch.GetTimestamp() - tick;
        long holdTicks = Stopwatch.Frequency * WheelDirectionHoldMs / 1000;
        if (elapsed < 0 || elapsed > holdTicks) return 0;

        source = Volatile.Read(ref _lastWheelDirectionSource) switch
        {
            1 => "HorizontalWheel",
            2 => "ShiftWheel",
            3 => "VerticalWheel",
            _ => "Wheel",
        };
        return direction;
    }

    private int GetRecentHorizontalScrollbarDragDirection()
    {
        int direction = Volatile.Read(ref _lastHorizontalScrollbarDragDir);
        if (direction == 0) return 0;

        long tick = Interlocked.Read(ref _lastHorizontalScrollbarDragDirectionTick);
        if (tick <= 0) return 0;
        long elapsed = Stopwatch.GetTimestamp() - tick;
        long holdTicks = Stopwatch.Frequency * ScrollbarDragDirectionHoldMs / 1000;
        return elapsed >= 0 && elapsed <= holdTicks ? direction : 0;
    }

    private bool IsHorizontalScrollbarBandPoint(int x, int y)
    {
        if (_options.Axis != ScrollAxis.Horizontal) return false;

        // The control bar is commonly placed just below the capture rectangle. Its
        // buttons/sliders must not be mistaken for a document scrollbar drag.
        if (_hasLastControlWindowRect
            && x >= _lastControlWindowX
            && x < _lastControlWindowX + _lastControlWindowW
            && y >= _lastControlWindowY
            && y < _lastControlWindowY + _lastControlWindowH)
        {
            return false;
        }

        // The initial press must be in a narrow band around the bottom of the selected
        // viewport. It includes a small area below the capture rectangle because users
        // commonly select only the Photoshop canvas while its scrollbar sits just under it.
        int insideBandHeight = Math.Clamp(_region.Height / 12, 24, 64);
        int outsideBandHeight = 36;
        int horizontalMargin = Math.Clamp(_region.Width / 24, 16, 48);
        return x >= _region.Left - horizontalMargin
            && x <= _region.Right + horizontalMargin
            && y >= _region.Bottom - insideBandHeight
            && y <= _region.Bottom + outsideBandHeight;
    }

    private void HandleHorizontalScrollbarDragMessage(int message, NativeMethods.MSLLHOOKSTRUCT mouse)
    {
        if (_options.Axis != ScrollAxis.Horizontal) return;

        if (message == WmLeftButtonDown)
        {
            if (!IsHorizontalScrollbarBandPoint(mouse.pt.X, mouse.pt.Y)) return;
            Interlocked.Exchange(ref _horizontalScrollbarDragActive, 1);
            Interlocked.Exchange(ref _horizontalScrollbarDragLastX, mouse.pt.X);
            Interlocked.Exchange(ref _lastHorizontalScrollbarDragDir, 0);
            Interlocked.Exchange(ref _lastHorizontalScrollbarDragDirectionTick, 0);
            int logCount = Interlocked.Increment(ref _scrollbarDragLogCount);
            if (logCount <= 8 || logCount % 25 == 0)
            {
                AppLogger.Info("LongScroll.ScrollbarDrag: phase=start, pt=(" + mouse.pt.X + ","
                    + mouse.pt.Y + "), bandBottom=" + _region.Bottom);
            }
            return;
        }

        if (message == WmMouseMove && Volatile.Read(ref _horizontalScrollbarDragActive) != 0)
        {
            int previousX = Interlocked.Exchange(ref _horizontalScrollbarDragLastX, mouse.pt.X);
            int movementX = mouse.pt.X - previousX;
            if (Math.Abs(movementX) < 3) return;

            // Mouse movement supplies direction only. Its magnitude is deliberately not
            // used as a viewport delta because a scrollbar thumb has an application-defined
            // document-to-pixel ratio.
            int direction = movementX < 0 ? -1 : 1;
            Interlocked.Exchange(ref _lastHorizontalScrollbarDragDir, direction);
            Interlocked.Exchange(ref _lastHorizontalScrollbarDragDirectionTick, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
            int logCount = Interlocked.Increment(ref _scrollbarDragLogCount);
            if (logCount <= 8 || logCount % 25 == 0)
            {
                AppLogger.Info("LongScroll.ScrollbarDrag: phase=move, source=BottomBand, direction="
                    + (direction < 0 ? "Leading" : "Trailing")
                    + ", pointerDx=" + movementX + ", usedAsPixelDelta=False");
            }
            return;
        }

        if (message == WmLeftButtonUp
            && Interlocked.Exchange(ref _horizontalScrollbarDragActive, 0) != 0)
        {
            int direction = Volatile.Read(ref _lastHorizontalScrollbarDragDir);
            AppLogger.Info("LongScroll.ScrollbarDrag: phase=end, recentDirection="
                + (direction < 0 ? "Leading" : direction > 0 ? "Trailing" : "None")
                + ", pt=(" + mouse.pt.X + "," + mouse.pt.Y + ")");
        }
    }

    private IntPtr HookMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _isRunning)
            {
                var lh = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                int message = unchecked((int)wParam.ToInt64());

                // Scrollbar drag handling is observational only and never swallows the
                // mouse message. Limit activation to the bottom band to avoid treating an
                // arbitrary Photoshop brush/selection drag as horizontal scrolling.
                if (_options.Axis == ScrollAxis.Horizontal)
                {
                    if (Volatile.Read(ref _manualOffsetDragging) != 0 && message == WmMouseMove)
                    {
                        Interlocked.Exchange(ref _horizontalScrollbarDragActive, 0);
                    }
                    else
                    {
                        HandleHorizontalScrollbarDragMessage(message, lh);
                    }
                }

                bool isVerticalWheel = message == NativeMethods.WM_MOUSEWHEEL;
                bool isHorizontalWheel = message == NativeMethods.WM_MOUSEHWHEEL;
                bool shiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
                bool acceptsWheel = isVerticalWheel || isHorizontalWheel;
                if (acceptsWheel)
                {
                    int delta = (short)((lh.mouseData >> 16) & 0xffff);
                    Rectangle expandedRegion = new Rectangle(_region.X - 300, _region.Y - 300, _region.Width + 600, _region.Height + 600);
                    bool inside = expandedRegion.Contains(lh.pt.X, lh.pt.Y);
                    bool injected = (lh.flags & NativeMethods.LLMHF_INJECTED) != 0;
                    bool lowerIntegrityInjected = (lh.flags & NativeMethods.LLMHF_LOWER_IL_INJECTED) != 0;
                    int sourceCode = isHorizontalWheel ? 1 : shiftDown ? 2 : 3;
                    string sourceName = sourceCode == 1 ? "HWHEEL" : sourceCode == 2 ? "ShiftWheel" : "Wheel";
                    int logCount = Interlocked.Increment(ref _wheelLogCount);
                    if (logCount <= 8 || logCount % 50 == 0)
                    {
                        AppLogger.Info("LegacyLongScrollSession.HookMouseProc: axis=" + _options.Axis
                            + ", source=" + sourceName
                            + ", injected=" + injected
                            + ", lowerIntegrityInjected=" + lowerIntegrityInjected
                            + ", delta=" + delta
                            + ", pt=(" + lh.pt.X + "," + lh.pt.Y + ")"
                            + ", inside=" + inside + ", max=" + _maxHeightReached + ", total=" + _totalHeight);
                    }

                    int dir = 0;
                    if (_options.Axis == ScrollAxis.Horizontal)
                    {
                        if (isHorizontalWheel)
                        {
                            if (delta > 0) dir = 1;
                            else if (delta < 0) dir = -1;
                        }
                        else
                        {
                            if (delta < 0) dir = 1;
                            else if (delta > 0) dir = -1;
                        }
                    }
                    else
                    {
                        if (delta > 0) dir = -1;
                        else if (delta < 0) dir = 1;
                    }
                    // 方向证据不绑定指针位置：用户手动滚动时，鼠标常落在滚动条或
                    // 内容右缘——超出「选区+300px 外扩」的任意边界。旧逻辑以 inside
                    // 门控记录方向，导致此处发车时机随机（外扩 300px 是一刀切边界，
                    // 鼠标差几十像素就丢方向），进而退化为双向扫描 → RepeatedTexture
                    // → delta=0 → 「滚几下就停」。滚轮方向只是先验，本身无副作用，
                    // 任何真实（非注入）滚轮都应记录；注入事件（预览窗/自动滚动）方向
                    // 语义一致，照记无妨。真正危险的「吞掉滚轮」仍由下方 inside 门控。
                    if (dir != 0)
                    {
                        Interlocked.Exchange(ref _lastWheelDir, dir);
                        Interlocked.Exchange(ref _lastWheelDirectionSource, sourceCode);
                        Interlocked.Exchange(ref _lastWheelDirectionTick, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _consecutiveIdleFrames, 0);
                    }

                    if ((_maxHeightReached || _totalHeight >= _options.MaxAlongLengthPx) && inside)
                    {
                        ShowMaxHeightNotice();
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LongScroll.MouseHook callback exception: " + ex.Message);
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private async Task QuickSaveAsync()
    {
        if (Volatile.Read(ref _saveDialogOpen) == 0) await FinishInternalAsync(false, null, false);
    }

    private async Task SaveAsAsync(Func<Window, Task<string?>>? pickPath = null)
    {
        if (_controlWindow == null || Volatile.Read(ref _finishStarted) != 0 || Volatile.Read(ref _saveDialogOpen) != 0) return;
        Window owner = _controlWindow;
        LongScrollPreviewWindow? preview = _previewWindow;
        bool restorePreview = preview?.IsVisible == true;
        _saveDialogCapturePaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _saveDialogOpen, 1);
        StopCompletionShortcuts();
        try
        {
            // 保留会话与窗口实例，原生对话框独占 Enter／Esc 和前台，取消后原位恢复。
            _autoScrollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            WindowHelper.StopEnforceTopmost(GetWindowHwnd(owner));
            if (preview != null)
            {
                WindowHelper.StopEnforceTopmost(GetWindowHwnd(preview));
                preview.Hide();
            }
            if (_captureThread?.IsAlive == true)
                await _saveDialogCapturePaused.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // 等已进入的滚轮回调退出，不能让暂停前的一次注入落到原生文件列表上。
            var pauseWait = Stopwatch.StartNew();
            while (Volatile.Read(ref _autoScrollTickRunning) != 0)
            {
                if (pauseWait.ElapsedMilliseconds >= 3000) throw new TimeoutException("自动滚动暂停超时");
                await Task.Delay(8);
            }
            if (!_isRunning || Volatile.Read(ref _finishStarted) != 0) return;

            string? filePath = await (pickPath ?? PickSaveAsPathAsync)(owner);
            if (!string.IsNullOrWhiteSpace(filePath)) await FinishInternalAsync(false, filePath, false);
        }
        catch (Exception ex) { AppLogger.Warn("LongScroll.SaveAs: 路径选择失败：" + ex.Message); }
        finally
        {
            try
            {
                if (_isRunning && Volatile.Read(ref _finishStarted) == 0)
                {
                    // 对话框退场后再恢复采集，避免关闭动画被混入下一帧；保留原自动滚动开关／方向／频率。
                    await Task.Delay(80);
                    // 等退场期间仍可能由其他入口结束会话，不能给已结束的会话重新注册热键／显窗。
                    if (_isRunning && Volatile.Read(ref _finishStarted) == 0)
                    {
                        Interlocked.Exchange(ref _autoScrollLastResponseTick, Stopwatch.GetTimestamp());
                        if (restorePreview && ReferenceEquals(preview, _previewWindow)) preview!.Show();
                        StartCompletionShortcuts();
                        WindowHelper.EnforceTopmost(GetWindowHwnd(owner));
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warn("LongScroll.SaveAs: 恢复会话显示失败：" + ex.Message); }
            finally
            {
                Volatile.Write(ref _saveDialogOpen, 0);
                _saveDialogCapturePaused = null;
            }
            if (_isRunning && Volatile.Read(ref _finishStarted) == 0)
            {
                if (_autoScrollEnabled) _autoScrollTimer?.Change(_autoScrollIntervalMs, _autoScrollIntervalMs);
                ApplyLongScrollWindowZOrder();
            }
        }
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
                Title = "长截图保存",
                SuggestedFileName = "LongScreenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                FileTypeChoices = filters,
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
            });

            return file?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LegacyLongScrollSession: 保存文件对话框不可用：" + ex.Message);
            return null;
        }
    }

    private string ResolveDefaultOutputPath()
    {
        string fileName = ResolveFileName(_settings);
        string? dir = _settings.SaveDirectory;
        if (!IsDirectoryWritable(dir))
        {
            AppLogger.Warn("LegacyLongScrollSession: 保存目录不可写，改用默认目录 dir=" + (dir ?? "(null)"));
            dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(dir) || !IsDirectoryWritable(dir))
            {
                dir = Path.GetTempPath();
            }
        }
        return Path.Combine(dir, fileName);
    }

    private static bool IsDirectoryWritable(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            if (!Directory.Exists(dir)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeSaveAsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("保存路径不能为空", nameof(path));
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

        return baseName + ".png";
    }

    private async Task CleanupAfterFinishAsync()
    {
        _isRunning = false;
        StopCompletionShortcuts();
        StopAutoScrollInternal();
        UninstallMouseHook();
        try { _autoScrollTimer?.Dispose(); } catch { }
        _autoScrollTimer = null;
        try { _captureQueue?.CompleteAdding(); } catch { }
        try { _cts?.Cancel(); } catch { }
        // 异常出口也须等采集线程退出，避免释放仍在使用的 DIB／D3D 原生资源。
        Thread? captureThread = _captureThread;
        if (captureThread != null && captureThread.IsAlive && captureThread != Thread.CurrentThread)
            await Task.Run(() => captureThread.Join());
        CleanupDIB();
        LongScrollMemoryDiagnostics.Log("LongScroll.BeforeDisposeStrips");

        try
        {
            while (_captureQueue != null && _captureQueue.TryTake(out CapturedFrame? queued))
            {
                try { queued.Bitmap.Dispose(); } catch { }
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

        try { _prevWorkBuffer?.Dispose(); } catch { }
        try { _curWorkBuffer?.Dispose(); } catch { }
        _prevWorkBuffer = null;
        _curWorkBuffer = null;

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
            _lastCanvasAction = "已复位";
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
                _previewWindow?.Close();
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

        // Off the finish path: a double blocking compacting GC on a large long-scroll
        // heap used to freeze the UI for hundreds of ms right after the result toast.
        _ = Task.Run(() => ForceLongScrollMemoryCleanup());
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

    /// <summary>
    /// Preview snapshots hold non-owning references to source strip bitmaps. A strip
    /// removed by manual trimming must therefore stay alive until the preview renderer
    /// is stopped during session cleanup; disposing it immediately causes render races.
    /// Caller holds <see cref="_stripsLock"/>.
    /// </summary>
    private void RetireStripLocked(StitchedStrip? strip)
    {
        if (strip == null) return;
        try { _retiredStripBitmaps.Add(strip.Bitmap); } catch { }
    }

    /// <summary>
    /// 固定层位图延迟释放：预览窗现在以非拥有引用直接渲染固定层位图，
    /// 因此被替换/置空的旧位图必须像条带位图一样延迟到会话清理阶段
    /// （预览渲染线程已停止后）再 Dispose，避免渲染中使用已释放对象。
    /// Monitor 可重入，调用方是否持有 _stripsLock 均安全。
    /// </summary>
    private void RetireFixedBitmapLocked(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        lock (_stripsLock)
        {
            try { _retiredStripBitmaps.Add(bitmap); } catch { }
        }
    }

    private static IntPtr GetWindowHwnd(Window? window)
    {
        try { return window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}
