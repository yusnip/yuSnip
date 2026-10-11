using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.AutoDetect;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.LongScroll;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 截图会话协调器：把 Core 业务逻辑与 Avalonia UI 串起来。
///
/// T4.7-1：自动检测高亮 → 点击高亮区保存（最小版本）。
/// T4.7-2（本版）：完整鼠标交互 = 拖框创建 / 拖手柄缩放 / 拖选区平移 + 双击或 Enter 确认。
///
/// 协调策略：
/// - 用户进行手动操作前，AutoDetectController 主导 SelectionLayer.Region；点击高亮区直接确认（T4.7-1 行为）
/// - 用户开始拖动后，<see cref="SelectionState"/> 接管，自动检测的事件被忽略，直到 Esc/确认结束本会话
/// - 单击 vs 拖动用 5px 阈值区分
///
/// 一次会话一个实例，不复用。
/// </summary>
public sealed class CaptureSessionResult
{
    public static CaptureSessionResult Saved(string path) => new CaptureSessionResult(path, false, null, false, null, false);

    public static CaptureSessionResult CopiedToClipboard() => new CaptureSessionResult(null, false, null, false, null, true);

    public static CaptureSessionResult StickerCreated() => new CaptureSessionResult(null, true, null, false, null, false);

    public static CaptureSessionResult ColorPicked(string hex) => new CaptureSessionResult(null, false, hex, false, null, false);

    public static CaptureSessionResult LongScroll(string? message) => new CaptureSessionResult(null, false, null, true, message, false);

    private CaptureSessionResult(string? savedPath, bool createdSticker, string? pickedColorHex, bool longScroll, string? message, bool clipboardCopied)
    {
        SavedPath = savedPath;
        CreatedSticker = createdSticker;
        PickedColorHex = pickedColorHex;
        IsLongScroll = longScroll;
        Message = message;
        ClipboardCopied = clipboardCopied;
    }

    public string? SavedPath { get; }

    public bool CreatedSticker { get; }

    public string? PickedColorHex { get; }

    public bool IsLongScroll { get; }

    public string? Message { get; }

    public bool ClipboardCopied { get; }

    public bool PickedColor => !string.IsNullOrWhiteSpace(PickedColorHex);
}

public sealed class CaptureSession
{
    /// <summary>"单击 vs 拖动"判定阈值（屏幕物理像素）。低于该阈值的小抖动按单击处理。</summary>
    private const int DragThresholdPx = 5;

    /// <summary>HitTestHandle 容差半径（屏幕物理像素）。沿用旧代码经验值。</summary>
    private const int HandleHitRadiusPx = 10;

    /// <summary>选区边框缩放命中范围（屏幕物理像素）。</summary>
    private const int BorderGripPx = 10;

    /// <summary>画笔采样最小点距（屏幕物理像素）。避免鼠标移动事件产生过多重复点。</summary>
    private const float BrushPointMinDistancePx = 2.0f;

    private static ScreenCaptureTool.Core.Capture.PaddleOcrService? _ocrService;

    public static void WarmUpOcr()
    {
        Task.Run(() =>
        {
            try
            {
                if (_ocrService == null)
                {
                    _ocrService = new ScreenCaptureTool.Core.Capture.PaddleOcrService();
                }
                _ocrService.WarmUp();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureSession.WarmUpOcr 异常: " + ex.Message);
            }
        });
    }

    private readonly AppSettings _settings;
    private readonly IScreenCapturer _capturer;

    /// <summary>
    /// 当前正在运行的截图窗口引用（RunAsync 期间非空，结束后置空）。
    /// 供全局热键（快速贴图 F3）在截图进行中触发"选区转贴图"。
    /// </summary>
    private CaptureWindow? _activeWindow;

    /// <summary>当前是否有一个处于交互中的截图窗口（截图会话运行中、选区可被操作）。</summary>
    public bool HasActiveSelectionWindow => _activeWindow != null && _activeWindow.IsVisible;

    /// <summary>
    /// 请求把当前截图选区转化为贴图（等价于点击工具栏"贴图"按钮）。
    /// 仅在 <see cref="HasActiveSelectionWindow"/> 为真时有意义；若无选区，
    /// CaptureWindow 的 StickerRequested 链路本身不会产生贴图（由 CompleteCurrentSelection 兜底）。
    /// </summary>
    public void RequestStickerFromCurrentSelection()
    {
        _activeWindow?.RaiseStickerRequested();
    }

    public CaptureSession(AppSettings settings, IScreenCapturer? capturer = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _capturer = capturer ?? new ScreenCapturer();
    }

    /// <summary>
    /// 跑完一次完整截图流程。返回保存到的文件路径；用户取消返回 null；失败抛异常。
    /// 必须在 UI 线程上调（创建 Avalonia Window）。
    /// </summary>
    public async Task<CaptureSessionResult?> RunAsync()
    {
        CapturedFrame? frame = null;
        CaptureWindow? window = null;
        AutoDetectController? autoDetect = null;
        var tcs = new TaskCompletionSource<CaptureCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            AppLogger.Info("CaptureSession.RunAsync: 开始捕获虚拟屏幕");
            using (PerformanceTimer.Measure("CaptureSession.CaptureScreen"))
            {
                frame = _capturer.CaptureVirtualScreen();
            }
            if (frame != null)
            {
                AppLogger.Info("CaptureSession.RunAsync: 屏幕捕获完成，VirtualScreen=" + frame.VirtualScreenRect + ", Bitmap=" + frame.FullScreen.Width + "x" + frame.FullScreen.Height);
            }

            AppLogger.Info("CaptureSession.RunAsync: 创建 CaptureWindow");
            window = new CaptureWindow();
            _activeWindow = window; // 暴露给全局热键（快速贴图 F3）使用
            window.InitTextToolControls();
            AppLogger.Info("CaptureSession.RunAsync: 文本工具控件初始化完成");
            autoDetect = new AutoDetectController();
            autoDetect.Mode = DetectModeExtensions.FromLegacyString(_settings.AutoDetectMode);
            AppLogger.Info("CaptureSession.RunAsync: 自动检测模式=" + autoDetect.Mode);

            var selection = new SelectionState();
            var annotations = new AnnotationDocument();
            var annotationHistory = new AnnotationHistory(annotations);
            window.Annotation.Document = annotations;
            if (frame != null)
            {
                window.SetFrozenScreenBitmap(frame.FullScreen);
                window.Annotation.SourceBitmap = frame.FullScreen;
                window.Annotation.SourceScreenRect = frame.VirtualScreenRect;
            }

            WireUpInteractions(window, autoDetect, selection, annotations, annotationHistory, frame, tcs);

            window.Opened += (_, _) =>
            {
                IntPtr hwnd = window.GetWindowHwnd();
                AppLogger.Info("CaptureWindow Opened. HWND=" + hwnd);
                if (hwnd != IntPtr.Zero)
                {
                    autoDetect.IgnoredWindow = hwnd;
                    autoDetect.WindowDetector.ExcludedHwnds.Add(hwnd);
                }
                else
                {
                    AppLogger.Warn("CaptureWindow.GetWindowHwnd() returned Zero! This will break AutoDetect!");
                }
                autoDetect.WindowDetector.RefreshWindowSnapshot();
                autoDetect.Start();
            };

            window.Show();

            CaptureCompletion? completion = await tcs.Task;
            if (completion == null)
            {
                AppLogger.Info("CaptureSession: 用户取消");
                return null;
            }

            AppLogger.Info("CaptureSession.RunAsync: 完成动作=" + completion.Action + ", Region=" + completion.Region);

            if (completion.Action == CaptureCompletionAction.LongScroll)
            {
                var longScrollOptions = new LongScrollOptions(completion.Region)
                {
                    IsDarkMode = IsDarkThemeActive()
                };
                try
                {
                    if (window.IsVisible) window.Close();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await Task.Delay(80);
                    AppLogger.Info("CaptureSession.RunAsync: CaptureWindow 已退场，启动 LongScrollSession。Region=" + completion.Region);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureSession.RunAsync: 关闭 CaptureWindow 后启动长截图前处理异常：" + ex.Message);
                }
                using LongScrollResult longScrollResult = await new LongScrollSession(longScrollOptions, _settings).RunAsync();
                return CaptureSessionResult.LongScroll(longScrollResult.Message);
            }
            if (completion.Action == CaptureCompletionAction.Sticker)
            {
                return CreateSticker(frame, completion.Region, annotations, window?.TextLayers);
            }
            if (completion.Action == CaptureCompletionAction.ColorPick)
            {
                return CaptureSessionResult.ColorPicked(completion.PickedColorHex ?? string.Empty);
            }

            if (completion.Action == CaptureCompletionAction.Clipboard)
            {
                return CopyResultToClipboard(frame, completion.Region, annotations, window?.TextLayers) ? CaptureSessionResult.CopiedToClipboard() : null;
            }
            string? savedPath = SaveResult(frame, completion.Region, annotations, window?.TextLayers);
            return savedPath == null ? null : CaptureSessionResult.Saved(savedPath);
        }
        finally
        {
            _activeWindow = null; // 会话结束，热键注入入口失效
            try
            {
                if (window != null)
                {
                    IntPtr hwnd = window.GetWindowHwnd();
                    if (hwnd != IntPtr.Zero)
                    {
                        // 强制立即将旧窗口设为鼠标穿透并隐藏，防止其在 Avalonia 延迟销毁期间拦截下一次截图的 WindowFromPoint
                        try
                        {
                            IntPtr oldStyle = ScreenCaptureTool.Platform.NativeMethods.GetWindowLongPtr(hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE);
                            long style = oldStyle.ToInt64();
                            ScreenCaptureTool.Platform.NativeMethods.SetWindowLongPtr(hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE, new IntPtr(style | ScreenCaptureTool.Platform.NativeMethods.WS_EX_TRANSPARENT | ScreenCaptureTool.Platform.NativeMethods.WS_EX_LAYERED));
                        }
                        catch { }
                    }
                    if (window.IsVisible)
                    {
                        window.Hide();
                        window.Close();
                    }
                }
            }
            catch { }

            try { autoDetect?.Dispose(); } catch { }
            try { frame?.Dispose(); } catch { }
        }
    }

    // -------------------- 交互 wiring --------------------

    private static bool IsDarkThemeActive()
    {
        try
        {
            ThemeVariant? variant = Application.Current?.ActualThemeVariant ?? Application.Current?.RequestedThemeVariant;
            return ReferenceEquals(variant, ThemeVariant.Dark) || string.Equals(variant?.Key?.ToString(), "Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private enum CaptureCompletionAction
    {
        Save,
        Clipboard,
        LongScroll,
        Sticker,
        ColorPick,
    }

    private sealed class CaptureCompletion
    {
        public CaptureCompletion(Rectangle region, CaptureCompletionAction action, string? pickedColorHex = null)
        {
            Region = region;
            Action = action;
            PickedColorHex = pickedColorHex;
        }

        public Rectangle Region { get; }

        public CaptureCompletionAction Action { get; }

        public string? PickedColorHex { get; }
    }

    private enum InteractionMode
    {
        Idle,
        DraggingNew,
        MovingSelection,
        ResizingHandle,
    }

    private enum AnnotationTool
    {
        None,
        Brush,
        Arrow,
        Rectangle,
        Text,
        Mosaic,
        Spotlight,
        Counter,
        ColorPicker,
    }

    private void WireUpInteractions(
        CaptureWindow window,
        AutoDetectController autoDetect,
        SelectionState selection,
        AnnotationDocument annotations,
        AnnotationHistory annotationHistory,
        CapturedFrame? frame,
        TaskCompletionSource<CaptureCompletion?> tcs)
    {
        // 自动检测最新高亮（屏幕物理像素），用于"点击高亮区直接确认"路径
        Rectangle? currentHighlight = null;
        Rectangle? pressedHighlight = null;

        // 用户手动模式状态
        InteractionMode mode = InteractionMode.Idle;
        bool hasManualSelection = false; // 一旦为 true，自动检测就不再写 SelectionLayer
        bool suppressOverlayDuringTransform = false;
        bool pendingExpandResize = false;
        bool _isCtrlPressed = false; // 跟踪 Ctrl 键状态（比 e.KeyModifiers 更可靠）
        HandleDirection pendingExpandResizeDirection = HandleDirection.None;
        System.Drawing.Point pendingExpandStartPoint = default;
        Rectangle pendingExpandStartRect = default;
        Rectangle? pendingSelectionVisualRect = null;
        bool pendingSelectionShowControls = false;
        bool selectionVisualUpdateScheduled = false;
        Rectangle liveTransformRect = Rectangle.Empty;

        var captureHistory = new CaptureSnapshotHistory(annotations, window);
        window.TextSnapshotRequested += (_, _) => captureHistory.CommitSnapshot();

        AnnotationTool activeAnnotationTool = AnnotationTool.None;
        bool drawingStroke = false;
        bool drawingArrow = false;
        bool drawingRectangle = false;
        bool drawingMosaic = false;
        bool drawingSpotlight = false;
        bool longScrollStarting = false;
        var currentStrokePoints = new List<PointF>();
        PointF arrowStart = default;
        PointF rectangleStart = default;
        PointF mosaicStart = default;
        PointF spotlightStart = default;
        Guid? selectedBlurShapeId = null;
        bool movingBlurShape = false;
        bool resizingBlurShape = false;
        HandleDirection blurResizeDirection = HandleDirection.None;
        PointF blurGestureStart = default;
        RectangleF blurGestureStartRect = default;
        Guid? selectedSpotlightShapeId = null;
        bool movingSpotlightShape = false;
        bool pendingMoveSpotlightShape = false;
        bool resizingSpotlightShape = false;
        bool adjustingSpotlightRadiusShape = false;
        HandleDirection spotlightResizeDirection = HandleDirection.None;
        string spotlightRadiusHandleTag = string.Empty;
        PointF spotlightGestureStart = default;
        RectangleF spotlightGestureStartRect = default;
        float spotlightRadiusStartValue = 0.0f;
        long lastSpotlightPreviewTick = 0;
        var autoMosaicHighlights = new List<RectangleF>();
        
        // 其他形状的选择和编辑状态
        Guid? selectedStrokeShapeId = null;
        Guid? selectedArrowShapeId = null;
        Guid? selectedRectangleShapeId = null;
        Guid? selectedCounterShapeId = null;
        bool movingArrowShape = false;
        bool resizingRectangleShape = false;
        bool movingRectangleShape = false;
        bool movingCounterShape = false;
        HandleDirection arrowHandleType = HandleDirection.None; // Start=Left, End=Right
        float currentArrowScale = 1.0f; // 对齐旧版 _currentArrowScale，选中箭头时同步
        HandleDirection rectangleResizeDirection = HandleDirection.None;
        PointF arrowGestureStart = default;
        PointF arrowStartOriginal = default;
        PointF arrowEndOriginal = default;
        PointF rectangleGestureStart = default;
        RectangleF rectangleGestureStartRect = default;
        PointF counterGestureStart = default;
        PointF counterOriginalCenter = default;
        Rect autoHoverTargetDip = default;
        Rect autoHoverDisplayDip = default;

        // —— 鼠标速度闸门 ——
        // 鼠标快速划过中间元素时，丢弃中间检测结果，只认落脚点（对齐 PixPin 行为）。
        // 速度高于阈值时，新 hover 目标不立即采用，挂起在 pendingAutoHoverTargetDip；
        // 速度降下来（或停止）后，立刻采用挂起的最新目标。
        System.Drawing.Point lastPointerScreen = default;
        long lastPointerTickMs = 0;       // 0 = 哨兵，首次 PointerMoved 跳过速度计算
        double pointerSpeedPxPerMs = 0;   // 最近一次 PointerMoved 的瞬时速度
        Rect pendingAutoHoverTargetDip = default; // 高速期挂起的目标（仅保留最新一个）
        bool hasPendingAutoHoverTarget = false;
        const double FastMoveThresholdPxPerMs = 3.5; // ≈ 210 px/s，放宽速度限制以提高移动时的跟随度
        const double SettledSpeedPxPerMs = 2.0;      // 鼠标轻微减速即立刻渲染最新目标

        void StopAutoHoverAnimation(bool clearVisual = true)
        {
            autoHoverTargetDip = default;
            autoHoverDisplayDip = default;
            hasPendingAutoHoverTarget = false;
            pendingAutoHoverTargetDip = default;
            if (clearVisual)
            {
                window.Layer.AutoHoverRegion = default;
                window.Layer.MousePosition = null;
            }
        }

        static bool IsValidHoverRect(Rect rect)
        {
            return rect.Width > 0.5 && rect.Height > 0.5 && !double.IsNaN(rect.X) && !double.IsNaN(rect.Y) && !double.IsNaN(rect.Width) && !double.IsNaN(rect.Height);
        }

        void SetAutoHoverTarget(Rect targetDip)
        {
            if (!IsValidHoverRect(targetDip))
            {
                StopAutoHoverAnimation();
                return;
            }

            // —— 速度闸门 ——
            // 鼠标快速移动时，丢弃中间检测结果，只把最新目标挂起；
            // 等鼠标慢下来（PointerMoved 末尾 flush）再采用。对齐 PixPin："停下才认"。
            if (pointerSpeedPxPerMs > FastMoveThresholdPxPerMs)
            {
                pendingAutoHoverTargetDip = targetDip;
                hasPendingAutoHoverTarget = true;
                return;
            }

            // 速度够慢：清掉残留的 pending，正常采用
            hasPendingAutoHoverTarget = false;

            window.Layer.Region = default;
            window.Annotation.SpotlightBoundsScreenRect = null;
            window.UpdateOverlay(default, null, false, false);

            // 瞬间对齐目标，去除缓动延迟
            autoHoverTargetDip = targetDip;
            autoHoverDisplayDip = targetDip;
            window.Layer.AutoHoverRegion = targetDip;
        }

        void ApplySelectionVisual(Rectangle? rectScreen, bool showControls)
        {
            StopAutoHoverAnimation();
            if (rectScreen.HasValue)
            {
                window.Annotation.SpotlightBoundsScreenRect = rectScreen.Value;
                var dipRect = window.ScreenRectToWindowDip(rectScreen.Value);
                window.Layer.Region = dipRect;
                if (suppressOverlayDuringTransform)
                {
                    window.BeginSelectionTransform();
                }
                else
                {
                    window.UpdateOverlay(dipRect, rectScreen.Value, true, showControls);
                }
            }
            else
            {
                window.Annotation.SpotlightBoundsScreenRect = null;
                window.Layer.Region = default;
                window.UpdateOverlay(default, null, false, false);
            }
        }

        void ApplyAutoHoverVisual(Rectangle? rectScreen)
        {
            if (hasManualSelection || mode != InteractionMode.Idle)
            {
                StopAutoHoverAnimation();
                return;
            }

            if (!rectScreen.HasValue)
            {
                StopAutoHoverAnimation();
                return;
            }

            SetAutoHoverTarget(window.ScreenRectToWindowDip(rectScreen.Value));
        }

        void RequestSelectionVisualUpdate(Rectangle? rectScreen, bool showControls)
        {
            pendingSelectionVisualRect = rectScreen;
            pendingSelectionShowControls = showControls;
            if (selectionVisualUpdateScheduled) return;
            selectionVisualUpdateScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                selectionVisualUpdateScheduled = false;
                if (!window.IsVisible) return;
                ApplySelectionVisual(pendingSelectionVisualRect, pendingSelectionShowControls);
            }, DispatcherPriority.Render);
        }

        void UpdateSelectionVisual(Rectangle? rectScreen, bool showControls)
        {
            RequestSelectionVisualUpdate(rectScreen, showControls);
        }

        static Rectangle CalculateResizedRect(Rectangle startRect, HandleDirection direction, int dx, int dy)
        {
            int left = startRect.Left;
            int top = startRect.Top;
            int right = startRect.Right;
            int bottom = startRect.Bottom;

            switch (direction)
            {
                case HandleDirection.TopLeft: left += dx; top += dy; break;
                case HandleDirection.Top: top += dy; break;
                case HandleDirection.TopRight: right += dx; top += dy; break;
                case HandleDirection.Right: right += dx; break;
                case HandleDirection.BottomRight: right += dx; bottom += dy; break;
                case HandleDirection.Bottom: bottom += dy; break;
                case HandleDirection.BottomLeft: left += dx; bottom += dy; break;
                case HandleDirection.Left: left += dx; break;
            }

            return Rectangle.FromLTRB(
                Math.Min(left, right),
                Math.Min(top, bottom),
                Math.Max(left, right),
                Math.Max(top, bottom));
        }

        void UpdateTransformVisual(Rectangle rectScreen)
        {
            Rectangle normalized = Rectangle.FromLTRB(
                Math.Min(rectScreen.Left, rectScreen.Right),
                Math.Min(rectScreen.Top, rectScreen.Bottom),
                Math.Max(rectScreen.Left, rectScreen.Right),
                Math.Max(rectScreen.Top, rectScreen.Bottom));
            liveTransformRect = normalized;
            window.Layer.Region = window.ScreenRectToWindowDip(normalized);
        }

        void BeginSelectionTransform()
        {
            suppressOverlayDuringTransform = true;
            liveTransformRect = selection.Region;
            window.BeginSelectionTransform();
        }

        void EndSelectionTransform()
        {
            suppressOverlayDuringTransform = false;
            if (!selection.HasSelection) return;
            var dipRect = window.ScreenRectToWindowDip(selection.Region);
            window.EndSelectionTransform(dipRect, selection.Region);
        }

        void CompleteCurrentSelection(CaptureCompletionAction action)
        {
            Rectangle? final = hasManualSelection && selection.HasSelection
                ? selection.Region
                : currentHighlight;
            if (final.HasValue)
            {
                StopAutoHoverAnimation();
                window.SetActiveTool(null);
                window.Annotation.ClearPreviewShape();
                window.HideColorPickerPreview();
                tcs.TrySetResult(new CaptureCompletion(final.Value, action));
            }
        }

        void ConfirmCurrentSelection()
        {
            CompleteCurrentSelection(CaptureCompletionAction.Clipboard);
        }

        void SaveCurrentSelection()
        {
            CompleteCurrentSelection(CaptureCompletionAction.Save);
        }

        void ShowLongScrollMessage(string message)
        {
            try
            {
                NativeMethods.MessageBoxW(window.GetWindowHwnd(), message, "长截图", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
            }
            catch
            {
                AppLogger.Warn("CaptureSession.StartLongScroll: 提示失败，message=" + message);
            }
        }

        void StartLongScrollFromCurrentSelection()
        {
            if (longScrollStarting) return;

            Rectangle? final = hasManualSelection && selection.HasSelection
                ? selection.Region
                : currentHighlight;

            if (!final.HasValue || final.Value.Width <= 1 || final.Value.Height <= 1)
            {
                ShowLongScrollMessage("请先框选截图区域后再启动长截图。");
                return;
            }

            if (!MonitorHelper.IsRegionOnSingleMonitor(final.Value))
            {
                ShowLongScrollMessage("长截图暂不支持跨屏区域，请将选区限制在单显示器内。");
                return;
            }

            StopAutoHoverAnimation();
            longScrollStarting = true;
            activeAnnotationTool = AnnotationTool.None;
            drawingStroke = false;
            drawingArrow = false;
            drawingRectangle = false;
            drawingMosaic = false;
            drawingSpotlight = false;
            currentStrokePoints.Clear();
            window.PrepareForLongScrollTransition();
            try
            {
                AppLogger.Info("CaptureSession.StartLongScroll: 隐藏 CaptureWindow，准备启动长截图。Region=" + final.Value);
                window.Hide();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureSession.StartLongScroll: 隐藏 CaptureWindow 失败：" + ex.Message);
            }
            tcs.TrySetResult(new CaptureCompletion(final.Value, CaptureCompletionAction.LongScroll));
        }

        void CreateStickerFromCurrentSelection()
        {
            CompleteCurrentSelection(CaptureCompletionAction.Sticker);
        }

        void CancelCapture()
        {
            StopAutoHoverAnimation();
            window.Cursor = null;
            window.SetActiveTool(null);
            window.Annotation.ClearPreviewShape();
            window.HideColorPickerPreview();
            tcs.TrySetResult(null);
        }

        bool IsPointInsideManualSelection(System.Drawing.Point pt)
        {
            return hasManualSelection && selection.HasSelection && selection.Region.Contains(pt);
        }

        static HandleDirection GetExpandResizeDirection(System.Drawing.Point pt, Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return HandleDirection.None;
            bool left = pt.X <= rect.Left;
            bool right = pt.X >= rect.Right;
            bool top = pt.Y <= rect.Top;
            bool bottom = pt.Y >= rect.Bottom;

            if (top && left) return HandleDirection.TopLeft;
            if (top && right) return HandleDirection.TopRight;
            if (bottom && right) return HandleDirection.BottomRight;
            if (bottom && left) return HandleDirection.BottomLeft;
            if (top) return HandleDirection.Top;
            if (right) return HandleDirection.Right;
            if (bottom) return HandleDirection.Bottom;
            if (left) return HandleDirection.Left;
            return HandleDirection.None;
        }

        void ExpandSelectionToPoint(System.Drawing.Point pt)
        {
            if (!selection.HasSelection) return;
            Rectangle rect = selection.Region;
            int left = Math.Min(rect.Left, pt.X);
            int top = Math.Min(rect.Top, pt.Y);
            int right = Math.Max(rect.Right, pt.X);
            int bottom = Math.Max(rect.Bottom, pt.Y);
            if (right - left <= 0 || bottom - top <= 0) return;
            selection.SetRegion(Rectangle.FromLTRB(left, top, right, bottom));
        }

        bool TryExpandSelectionByMaskClick(System.Drawing.Point pt)
        {
            if (!hasManualSelection || !selection.HasSelection) return false;
            if (activeAnnotationTool != AnnotationTool.None) return false;
            if (selection.Region.Contains(pt)) return false;

            HandleDirection direction = GetExpandResizeDirection(pt, selection.Region);
            ExpandSelectionToPoint(pt);
            pendingExpandResize = direction != HandleDirection.None;
            pendingExpandResizeDirection = direction;
            pendingExpandStartPoint = pt;
            pendingExpandStartRect = selection.Region;
            return true;
        }

        static Rectangle InflateSelection(Rectangle rect, int delta)
        {
            int left = rect.Left - delta;
            int top = rect.Top - delta;
            int right = rect.Right + delta;
            int bottom = rect.Bottom + delta;
            if (right - left <= 4 || bottom - top <= 4) return rect;
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        static string? ToToolKey(AnnotationTool tool)
        {
            return tool switch
            {
                AnnotationTool.Brush => "Brush",
                AnnotationTool.Arrow => "Arrow",
                AnnotationTool.Rectangle => "Rectangle",
                AnnotationTool.Text => "Text",
                AnnotationTool.Mosaic => "Mosaic",
                AnnotationTool.Spotlight => "Spotlight",
                AnnotationTool.Counter => "Counter",
                AnnotationTool.ColorPicker => "ColorPicker",
                _ => null,
            };
        }

        void SetAnnotationTool(AnnotationTool tool)
        {
            if (!hasManualSelection || !selection.HasSelection) return;
            activeAnnotationTool = activeAnnotationTool == tool ? AnnotationTool.None : tool;
            ResetActiveAnnotationGestures();
            currentStrokePoints.Clear();
            window.Annotation.ClearPreviewShape();
            if (tool != AnnotationTool.Mosaic)
            {
                SelectBlurShape(null);
                window.Annotation.HoverAutoMosaicHighlight = null;
            }
            if (tool != AnnotationTool.Spotlight)
            {
                SelectSpotlightShape(null);
            }
            window.HideColorPickerPreview();
            window.Cursor = null;
            window.SetActiveTool(ToToolKey(activeAnnotationTool));
            // 通知窗口文本工具是否激活
            window.IsTextToolActive = (activeAnnotationTool == AnnotationTool.Text);
            AppLogger.Info("CaptureSession.SetAnnotationTool: 当前工具=" + activeAnnotationTool);
        }

        void ForceAnnotationTool(AnnotationTool tool)
        {
            if (!hasManualSelection || !selection.HasSelection) return;
            activeAnnotationTool = tool;
            ResetActiveAnnotationGestures();
            currentStrokePoints.Clear();
            window.Annotation.ClearPreviewShape();
            if (tool != AnnotationTool.Mosaic)
            {
                SelectBlurShape(null);
                window.Annotation.HoverAutoMosaicHighlight = null;
            }
            window.HideColorPickerPreview();
            window.Cursor = null;
            window.SetActiveTool(ToToolKey(activeAnnotationTool));
            window.IsTextToolActive = (activeAnnotationTool == AnnotationTool.Text);
            AppLogger.Info("CaptureSession.ForceAnnotationTool: 当前工具=" + activeAnnotationTool);
        }

        void ResetActiveAnnotationGestures()
        {
            drawingStroke = false;
            drawingArrow = false;
            drawingRectangle = false;
            drawingMosaic = false;
            drawingSpotlight = false;
            movingBlurShape = false;
            resizingBlurShape = false;
            movingSpotlightShape = false;
            pendingMoveSpotlightShape = false;
            resizingSpotlightShape = false;
            adjustingSpotlightRadiusShape = false;
            blurResizeDirection = HandleDirection.None;
            spotlightResizeDirection = HandleDirection.None;
            spotlightRadiusHandleTag = string.Empty;
            movingArrowShape = false;
            movingRectangleShape = false;
            movingCounterShape = false;
            resizingRectangleShape = false;
            arrowHandleType = HandleDirection.None;
            rectangleResizeDirection = HandleDirection.None;
        }

        static RectangleF NormalizeRect(PointF start, PointF end)
        {
            float left = Math.Min(start.X, end.X);
            float top = Math.Min(start.Y, end.Y);
            float right = Math.Max(start.X, end.X);
            float bottom = Math.Max(start.Y, end.Y);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        RectangleF CreateDrawRect(PointF start, PointF end, bool forceSquare)
        {
            if (!forceSquare) return NormalizeRect(start, end);

            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float signX = dx < 0 ? -1.0f : 1.0f;
            float signY = dy < 0 ? -1.0f : 1.0f;
            float side = Math.Min(Math.Abs(dx), Math.Abs(dy));

            if (selection.HasSelection)
            {
                Rectangle bounds = selection.Region;
                float availableX = signX >= 0 ? bounds.Right - start.X : start.X - bounds.Left;
                float availableY = signY >= 0 ? bounds.Bottom - start.Y : start.Y - bounds.Top;
                side = Math.Min(side, Math.Max(0.0f, availableX));
                side = Math.Min(side, Math.Max(0.0f, availableY));
            }

            PointF constrainedEnd = new PointF(start.X + signX * side, start.Y + signY * side);
            return NormalizeRect(start, constrainedEnd);
        }

        static bool Contains(RectangleF rect, PointF pt)
        {
            return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
        }

        RectangleF ClampSpotlightRect(RectangleF rect)
        {
            if (!selection.HasSelection) return rect;
            Rectangle bounds = selection.Region;
            float left = Math.Clamp(rect.Left, bounds.Left, bounds.Right);
            float top = Math.Clamp(rect.Top, bounds.Top, bounds.Bottom);
            float right = Math.Clamp(rect.Right, bounds.Left, bounds.Right);
            float bottom = Math.Clamp(rect.Bottom, bounds.Top, bounds.Bottom);
            if (right < left) (left, right) = (right, left);
            if (bottom < top) (top, bottom) = (bottom, top);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        RectangleF TranslateAndClampSpotlightRect(RectangleF start, float dx, float dy)
        {
            if (!selection.HasSelection) return new RectangleF(start.X + dx, start.Y + dy, start.Width, start.Height);
            Rectangle bounds = selection.Region;
            float x = start.X + dx;
            float y = start.Y + dy;
            if (start.Width <= bounds.Width)
            {
                x = Math.Clamp(x, bounds.Left, bounds.Right - start.Width);
            }
            else
            {
                x = bounds.Left;
            }
            if (start.Height <= bounds.Height)
            {
                y = Math.Clamp(y, bounds.Top, bounds.Bottom - start.Height);
            }
            else
            {
                y = bounds.Top;
            }
            return new RectangleF(x, y, Math.Min(start.Width, bounds.Width), Math.Min(start.Height, bounds.Height));
        }

        RectangleF ResizeAndClampSpotlightRect(RectangleF start, HandleDirection direction, float dx, float dy)
        {
            RectangleF resized = ResizeRect(start, direction, dx, dy);
            resized = ClampSpotlightRect(resized);
            const float minSide = 10.0f;
            if (resized.Width < minSide || resized.Height < minSide) return start;
            return resized;
        }

        static RectangleF ExpandAutoMosaicRect(RectangleF rect, Rectangle bounds)
        {
            float padX = Math.Clamp(rect.Width * 0.12f, 4.0f, 10.0f);
            float padY = Math.Clamp(rect.Height * 0.22f, 4.0f, 8.0f);
            return RectangleF.FromLTRB(
                Math.Max(bounds.Left, rect.Left - padX),
                Math.Max(bounds.Top, rect.Top - padY),
                Math.Min(bounds.Right, rect.Right + padX),
                Math.Min(bounds.Bottom, rect.Bottom + padY));
        }

        static HandleDirection HitTestBlurHandle(RectangleF rect, PointF pt, float radius = 7.0f)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return HandleDirection.None;
            (HandleDirection Dir, PointF Pt)[] handles =
            {
                (HandleDirection.TopLeft, new PointF(rect.Left, rect.Top)),
                (HandleDirection.Top, new PointF(rect.Left + rect.Width / 2.0f, rect.Top)),
                (HandleDirection.TopRight, new PointF(rect.Right, rect.Top)),
                (HandleDirection.Right, new PointF(rect.Right, rect.Top + rect.Height / 2.0f)),
                (HandleDirection.BottomRight, new PointF(rect.Right, rect.Bottom)),
                (HandleDirection.Bottom, new PointF(rect.Left + rect.Width / 2.0f, rect.Bottom)),
                (HandleDirection.BottomLeft, new PointF(rect.Left, rect.Bottom)),
                (HandleDirection.Left, new PointF(rect.Left, rect.Top + rect.Height / 2.0f)),
            };

            float r2 = radius * radius;
            foreach ((HandleDirection dir, PointF hp) in handles)
            {
                float dx = pt.X - hp.X;
                float dy = pt.Y - hp.Y;
                if (dx * dx + dy * dy <= r2) return dir;
            }
            return HandleDirection.None;
        }

        static RectangleF ResizeRect(RectangleF start, HandleDirection direction, float dx, float dy)
        {
            float left = start.Left;
            float top = start.Top;
            float right = start.Right;
            float bottom = start.Bottom;
            switch (direction)
            {
                case HandleDirection.TopLeft: left += dx; top += dy; break;
                case HandleDirection.Top: top += dy; break;
                case HandleDirection.TopRight: right += dx; top += dy; break;
                case HandleDirection.Right: right += dx; break;
                case HandleDirection.BottomRight: right += dx; bottom += dy; break;
                case HandleDirection.Bottom: bottom += dy; break;
                case HandleDirection.BottomLeft: left += dx; bottom += dy; break;
                case HandleDirection.Left: left += dx; break;
            }
            return RectangleF.FromLTRB(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
        }

        static string HitTestSpotlightRadiusHandle(SpotlightShape shape, PointF pt, float radius = 18.0f)
        {
            if (shape.ShapeKind != SpotlightShapeKind.Rectangle) return string.Empty;
            RectangleF rect = shape.Rect;
            if (rect.Width <= 0 || rect.Height <= 0) return string.Empty;

            float padding = Math.Min(rect.Width, rect.Height) < 40.0f ? Math.Min(rect.Width, rect.Height) / 4.0f : 15.0f;
            (string Tag, PointF Pt)[] handles =
            {
                ("RadiusTL", new PointF(rect.Left + padding, rect.Top + padding)),
                ("RadiusTR", new PointF(rect.Right - padding, rect.Top + padding)),
                ("RadiusBR", new PointF(rect.Right - padding, rect.Bottom - padding)),
                ("RadiusBL", new PointF(rect.Left + padding, rect.Bottom - padding)),
            };

            float r2 = radius * radius;
            foreach ((string tag, PointF hp) in handles)
            {
                float dx = pt.X - hp.X;
                float dy = pt.Y - hp.Y;
                if (dx * dx + dy * dy <= r2) return tag;
            }
            return string.Empty;
        }

        static float CalculateSpotlightRadius(string tag, float startRadius, PointF start, PointF current, RectangleF rect)
        {
            float dx = current.X - start.X;
            float dy = current.Y - start.Y;
            float delta = tag switch
            {
                "RadiusTL" => dx + dy,
                "RadiusTR" => -dx + dy,
                "RadiusBR" => -dx - dy,
                "RadiusBL" => dx - dy,
                _ => 0.0f,
            };
            float next = startRadius + delta / 2.0f;
            float max = Math.Max(0.0f, Math.Min(rect.Width, rect.Height) / 2.0f);
            return Math.Clamp(next, 0.0f, max);
        }

        BlurShape? FindBlurShapeAt(PointF pt)
        {
            for (int i = annotations.Shapes.Count - 1; i >= 0; i--)
            {
                if (annotations.Shapes[i] is BlurShape blur && Contains(blur.Rect, pt)) return blur;
            }
            return null;
        }

        BlurShape? GetSelectedBlurShape()
        {
            if (!selectedBlurShapeId.HasValue) return null;
            return annotations.Shapes.OfType<BlurShape>().FirstOrDefault(x => x.Id == selectedBlurShapeId.Value);
        }

        void SelectBlurShape(Guid? id)
        {
            selectedBlurShapeId = id;
            window.Annotation.SelectedShapeId = id;
            if (id.HasValue && annotations.Shapes.OfType<BlurShape>().FirstOrDefault(x => x.Id == id.Value) is BlurShape blur)
            {
                window.SyncBlurToolState(blur.Mode, blur.UiLevel);
            }
            window.Annotation.InvalidateVisual();
        }

        void ReplaceSelectedBlurShape(BlurShape next)
        {
            annotations.ReplaceShape(next);
            SelectBlurShape(next.Id);
        }

        SpotlightShape? FindSpotlightShapeAt(PointF pt)
        {
            for (int i = annotations.Shapes.Count - 1; i >= 0; i--)
            {
                if (annotations.Shapes[i] is SpotlightShape spotlight && Contains(spotlight.Rect, pt)) return spotlight;
            }
            return null;
        }

        SpotlightShape? GetSelectedSpotlightShape()
        {
            if (!selectedSpotlightShapeId.HasValue) return null;
            return annotations.Shapes.OfType<SpotlightShape>().FirstOrDefault(x => x.Id == selectedSpotlightShapeId.Value);
        }

        void SelectSpotlightShape(Guid? id)
        {
            selectedSpotlightShapeId = id;
            selectedBlurShapeId = null;
            window.Annotation.SelectedShapeId = id;
            if (id.HasValue && annotations.Shapes.OfType<SpotlightShape>().FirstOrDefault(x => x.Id == id.Value) is SpotlightShape spotlight)
            {
                window.SyncSpotlightToolState(spotlight);
            }
            window.Annotation.InvalidateVisual();
        }


        void ReplaceSelectedSpotlightShape(SpotlightShape next)
        {
            annotations.ReplaceShape(next);
            SelectSpotlightShape(next.Id);
        }

        void ApplySpotlightStyleToSelection()
        {
            SpotlightShape? selected = GetSelectedSpotlightShape();
            bool hasSpotlights = annotations.Shapes.OfType<SpotlightShape>().Any();
            if (!hasSpotlights) return;
            captureHistory.CommitSnapshot();
            ApplySpotlightStyleWithoutSnapshot(selected?.Id);
        }

        void ApplySpotlightStyleWithoutSnapshot(Guid? selectedId)
        {
            float darkness = window.SelectedSpotlightDarkness;
            foreach (SpotlightShape spotlight in annotations.Shapes.OfType<SpotlightShape>().ToArray())
            {
                bool isSelected = selectedId.HasValue && spotlight.Id == selectedId.Value;
                SpotlightShape next = spotlight.WithStyle(
                    darkness,
                    isSelected ? window.SelectedSpotlightKind : spotlight.ShapeKind,
                    isSelected ? window.SelectedSpotlightStrokeColor : spotlight.StrokeColor,
                    isSelected ? window.SelectedSpotlightStrokeThickness : spotlight.StrokeThickness);
                annotations.ReplaceShape(next);
            }
            if (selectedId.HasValue)
            {
                SelectSpotlightShape(selectedId.Value);
            }
            else
            {
                window.Annotation.InvalidateVisual();
            }
        }

        void ApplyCommonSettingsToSelection()
        {
            var color = window.SelectedAnnotationColor;
            var thickness = window.SelectedStrokeThickness;
            
            // 更新选中箭头的颜色和粗细
            if (selectedArrowShapeId.HasValue)
            {
                var arrow = annotations.Shapes.OfType<ArrowShape>().FirstOrDefault(x => x.Id == selectedArrowShapeId.Value);
                if (arrow != null)
                {
                    captureHistory.CommitSnapshot();
                    var next = new ArrowShape(arrow.Start, arrow.End, color, thickness, arrow.Style, arrow.Scale, arrow.Id);
                    annotations.ReplaceShape(next);
                    window.Annotation.InvalidateVisual();
                    return;
                }
            }
            
            // 更新选中矩形的颜色和描边粗细
            if (selectedRectangleShapeId.HasValue)
            {
                var rect = annotations.Shapes.OfType<RectangleShape>().FirstOrDefault(x => x.Id == selectedRectangleShapeId.Value);
                if (rect != null)
                {
                    captureHistory.CommitSnapshot();
                    var next = new RectangleShape(rect.Rect, color, thickness, rect.ShapeKind, rect.LineStyle, rect.Id);
                    annotations.ReplaceShape(next);
                    window.Annotation.InvalidateVisual();
                    return;
                }
            }
            
            // 更新选中画笔的颜色和粗细
            if (selectedStrokeShapeId.HasValue)
            {
                var stroke = annotations.Shapes.OfType<StrokeShape>().FirstOrDefault(x => x.Id == selectedStrokeShapeId.Value);
                if (stroke != null)
                {
                    captureHistory.CommitSnapshot();
                    var next = new StrokeShape(stroke.Points, color, thickness, stroke.LineStyle, stroke.Id);
                    annotations.ReplaceShape(next);
                    window.Annotation.InvalidateVisual();
                    return;
                }
            }
            
        }

        void DeleteSelectedSpotlightShape()
        {
            if (!selectedSpotlightShapeId.HasValue) return;
            Guid id = selectedSpotlightShapeId.Value;
            if (!annotations.Shapes.OfType<SpotlightShape>().Any(x => x.Id == id)) return;
            captureHistory.CommitSnapshot();
            annotations.RemoveShape(id);
            SelectSpotlightShape(null);
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.DeleteSelectedSpotlightShape: 已删除聚光灯，id=" + id);
        }

        void DeleteSelectedBlurShape()
        {
            if (!selectedBlurShapeId.HasValue) return;
            Guid id = selectedBlurShapeId.Value;
            if (!annotations.Shapes.OfType<BlurShape>().Any(x => x.Id == id)) return;
            captureHistory.CommitSnapshot();
            annotations.RemoveShape(id);
            SelectBlurShape(null);
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.DeleteSelectedBlurShape: 已删除模糊/马赛克，id=" + id);
        }

        void DeleteSelectedStrokeShape()
        {
            if (!selectedStrokeShapeId.HasValue) return;
            Guid id = selectedStrokeShapeId.Value;
            if (!annotations.Shapes.OfType<StrokeShape>().Any(x => x.Id == id)) return;
            captureHistory.CommitSnapshot();
            annotations.RemoveShape(id);
            selectedStrokeShapeId = null;
            window.Annotation.SelectedShapeId = null;
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.DeleteSelectedStrokeShape: 已删除画笔，id=" + id);
        }

        void DeleteSelectedArrowShape()
        {
            if (!selectedArrowShapeId.HasValue) return;
            Guid id = selectedArrowShapeId.Value;
            if (!annotations.Shapes.OfType<ArrowShape>().Any(x => x.Id == id)) return;
            captureHistory.CommitSnapshot();
            annotations.RemoveShape(id);
            selectedArrowShapeId = null;
            window.Annotation.SelectedShapeId = null;
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.DeleteSelectedArrowShape: 已删除箭头，id=" + id);
        }

        void DeleteSelectedRectangleShape()
        {
            if (!selectedRectangleShapeId.HasValue) return;
            Guid id = selectedRectangleShapeId.Value;
            if (!annotations.Shapes.OfType<RectangleShape>().Any(x => x.Id == id)) return;
            captureHistory.CommitSnapshot();
            annotations.RemoveShape(id);
            selectedRectangleShapeId = null;
            window.Annotation.SelectedShapeId = null;
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.DeleteSelectedRectangleShape: 已删除矩形/椭圆，id=" + id);
        }

        void DeleteSelectedCounterShape()
        {
            if (!selectedCounterShapeId.HasValue) return;
            Guid id = selectedCounterShapeId.Value;
            var target = annotations.Shapes.OfType<CounterShape>().FirstOrDefault(x => x.Id == id);
            if (target == null) return;

            captureHistory.CommitSnapshot();

            // 记录被删除的序号值
            int deletedNumber = target.Number;
            annotations.RemoveShape(id);

            // 重新编号：所有大于被删序号的计数器递减 1，保持顺序连续
            var countersToRenumber = annotations.Shapes.OfType<CounterShape>()
                .Where(x => x.Number > deletedNumber)
                .OrderBy(x => x.Number)
                .ToList();
            foreach (var counter in countersToRenumber)
            {
                var renumbered = new CounterShape(
                    counter.Center,
                    counter.Number - 1,
                    counter.Radius,
                    counter.FillColor,
                    counter.TextColor,
                    counter.Id);
                annotations.ReplaceShape(renumbered);
            }

            selectedCounterShapeId = null;
            window.Annotation.SelectedShapeId = null;
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.DeleteSelectedCounterShape: 已删除序号 number=" + deletedNumber + " id=" + id + " 已重新编号 " + countersToRenumber.Count + " 个剩余序号");
        }

        void ClearAllSelections()
        {
            selectedStrokeShapeId = null;
            selectedArrowShapeId = null;
            selectedRectangleShapeId = null;
            selectedCounterShapeId = null;
            selectedBlurShapeId = null;
            selectedSpotlightShapeId = null;
            window.Annotation.SelectedShapeId = null;
            window.DeselectText();
        }

        // 查找和选择其他形状的辅助函数
        StrokeShape? FindStrokeShapeAt(PointF pt)
        {
            const float hitRadius = 30.0f; // 宽松的碰撞半径，对齐旧版体验
            for (int i = annotations.Shapes.Count - 1; i >= 0; i--)
            {
                if (annotations.Shapes[i] is not StrokeShape stroke) continue;
                foreach (PointF p in stroke.Points)
                {
                    float dx = pt.X - p.X;
                    float dy = pt.Y - p.Y;
                    if (dx * dx + dy * dy <= hitRadius * hitRadius)
                    {
                        AppLogger.Info($"FindStrokeShapeAt: 找到画笔 id={stroke.Id} at ({pt.X}, {pt.Y})");
                        return stroke;
                    }
                }
            }
            AppLogger.Info($"FindStrokeShapeAt: 未找到画笔 at ({pt.X}, {pt.Y})");
            return null;
        }

        ArrowShape? FindArrowShapeAt(PointF pt)
        {
            const float hitRadius = 30.0f; // 宽松的碰撞半径，对齐旧版体验
            for (int i = annotations.Shapes.Count - 1; i >= 0; i--)
            {
                if (annotations.Shapes[i] is not ArrowShape arrow) continue;
                
                // 检查箭头线段
                float dx = arrow.End.X - arrow.Start.X;
                float dy = arrow.End.Y - arrow.Start.Y;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 1.0f) continue;
                
                // 计算点到线段的距离
                float t = Math.Clamp(((pt.X - arrow.Start.X) * dx + (pt.Y - arrow.Start.Y) * dy) / (len * len), 0.0f, 1.0f);
                float projX = arrow.Start.X + t * dx;
                float projY = arrow.Start.Y + t * dy;
                float distX = pt.X - projX;
                float distY = pt.Y - projY;
                
                if (distX * distX + distY * distY <= hitRadius * hitRadius)
                {
                    AppLogger.Info($"FindArrowShapeAt: 找到箭头 id={arrow.Id} at ({pt.X}, {pt.Y})");
                    return arrow;
                }
            }
            AppLogger.Info($"FindArrowShapeAt: 未找到箭头 at ({pt.X}, {pt.Y})");
            return null;
        }

        RectangleShape? FindRectangleShapeAt(PointF pt)
        {
            const float hitRadius = 25.0f; // 宽松的碰撞半径，对齐旧版体验
            for (int i = annotations.Shapes.Count - 1; i >= 0; i--)
            {
                if (annotations.Shapes[i] is not RectangleShape rect) continue;
                
                // 检查是否在边框附近或内部
                RectangleF r = rect.Rect;
                
                // 先检查是否在矩形内部
                bool inside = pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
                
                // 再检查是否在边框附近
                bool nearLeft = Math.Abs(pt.X - r.Left) <= hitRadius && pt.Y >= r.Top - hitRadius && pt.Y <= r.Bottom + hitRadius;
                bool nearRight = Math.Abs(pt.X - r.Right) <= hitRadius && pt.Y >= r.Top - hitRadius && pt.Y <= r.Bottom + hitRadius;
                bool nearTop = Math.Abs(pt.Y - r.Top) <= hitRadius && pt.X >= r.Left - hitRadius && pt.X <= r.Right + hitRadius;
                bool nearBottom = Math.Abs(pt.Y - r.Bottom) <= hitRadius && pt.X >= r.Left - hitRadius && pt.X <= r.Right + hitRadius;
                
                if (nearLeft || nearRight || nearTop || nearBottom || inside)
                {
                    AppLogger.Info($"FindRectangleShapeAt: 找到矩形 id={rect.Id} at ({pt.X}, {pt.Y}), rect={r}");
                    return rect;
                }
            }
            AppLogger.Info($"FindRectangleShapeAt: 未找到矩形 at ({pt.X}, {pt.Y})");
            return null;
        }

        CounterShape? FindCounterShapeAt(PointF pt)
        {
            for (int i = annotations.Shapes.Count - 1; i >= 0; i--)
            {
                if (annotations.Shapes[i] is not CounterShape counter) continue;
                
                float dx = pt.X - counter.Center.X;
                float dy = pt.Y - counter.Center.Y;
                float r = counter.Radius + 15.0f; // 增加15px容差，宽松点击
                
                if (dx * dx + dy * dy <= r * r)
                {
                    AppLogger.Info($"FindCounterShapeAt: 找到序号 id={counter.Id} at ({pt.X}, {pt.Y})");
                    return counter;
                }
            }
            AppLogger.Info($"FindCounterShapeAt: 未找到序号 at ({pt.X}, {pt.Y})");
            return null;
        }

        /// <summary>
        /// 文本工具下 Ctrl+点击其他标注图形 → 切换到对应工具并选中。
        /// 返回 true 表示已处理（命中了某个图形）。
        /// </summary>
        bool TrySwitchFromTextToOtherAnnotation(PointerPressedEventArgs e, PointF pt)
        {
            // 聚光灯
            SpotlightShape? hitSpotlight = FindSpotlightShapeAt(pt);
            if (hitSpotlight != null)
            {
                e.Handled = true;
                ClearAllSelections();
                ForceAnnotationTool(AnnotationTool.Spotlight);
                SelectSpotlightShape(hitSpotlight.Id);
                pendingMoveSpotlightShape = true;
                spotlightGestureStart = pt;
                spotlightGestureStartRect = ClampSpotlightRect(hitSpotlight.Rect);
                lastSpotlightPreviewTick = 0;
                try { e.Pointer.Capture(window); } catch { }
                return true;
            }

            // 模糊/马赛克
            BlurShape? hitBlur = FindBlurShapeAt(pt);
            if (hitBlur != null)
            {
                e.Handled = true;
                ClearAllSelections();
                ForceAnnotationTool(AnnotationTool.Mosaic);
                SelectBlurShape(hitBlur.Id);
                HandleDirection handle = HitTestBlurHandle(hitBlur.Rect, pt);
                if (handle != HandleDirection.None)
                {
                    captureHistory.CommitSnapshot();
                    resizingBlurShape = true;
                    blurResizeDirection = handle;
                    blurGestureStart = pt;
                    blurGestureStartRect = hitBlur.Rect;
                }
                else
                {
                    captureHistory.CommitSnapshot();
                    movingBlurShape = true;
                    blurGestureStart = pt;
                    blurGestureStartRect = hitBlur.Rect;
                }
                try { e.Pointer.Capture(window); } catch { }
                return true;
            }

            // 序号
            CounterShape? hitCounter = FindCounterShapeAt(pt);
            if (hitCounter != null)
            {
                e.Handled = true;
                ClearAllSelections();
                ForceAnnotationTool(AnnotationTool.Counter);
                selectedCounterShapeId = hitCounter.Id;
                window.Annotation.SelectedShapeId = hitCounter.Id;
                movingCounterShape = true;
                counterGestureStart = pt;
                counterOriginalCenter = hitCounter.Center;
                try { e.Pointer.Capture(window); } catch { }
                return true;
            }

            // 箭头
            ArrowShape? hitArrow = FindArrowShapeAt(pt);
            if (hitArrow != null)
            {
                e.Handled = true;
                ClearAllSelections();
                ForceAnnotationTool(AnnotationTool.Arrow);
                selectedArrowShapeId = hitArrow.Id;
                window.Annotation.SelectedShapeId = hitArrow.Id;
                currentArrowScale = hitArrow.Scale;
                HandleDirection arrowHandle = HitTestArrowHandle(hitArrow, pt);
                captureHistory.CommitSnapshot();
                movingArrowShape = true;
                arrowHandleType = arrowHandle;
                arrowGestureStart = pt;
                arrowStartOriginal = hitArrow.Start;
                arrowEndOriginal = hitArrow.End;
                try { e.Pointer.Capture(window); } catch { }
                return true;
            }

            // 矩形
            RectangleShape? hitRect = FindRectangleShapeAt(pt);
            if (hitRect != null)
            {
                e.Handled = true;
                ClearAllSelections();
                ForceAnnotationTool(AnnotationTool.Rectangle);
                selectedRectangleShapeId = hitRect.Id;
                window.Annotation.SelectedShapeId = hitRect.Id;
                HandleDirection rectHandle = HitTestRectangleHandle(hitRect, pt);
                if (rectHandle != HandleDirection.None)
                {
                    captureHistory.CommitSnapshot();
                    resizingRectangleShape = true;
                    rectangleResizeDirection = rectHandle;
                    rectangleGestureStart = pt;
                    rectangleGestureStartRect = hitRect.Rect;
                }
                else
                {
                    captureHistory.CommitSnapshot();
                    movingRectangleShape = true;
                    rectangleGestureStart = pt;
                    rectangleGestureStartRect = hitRect.Rect;
                }
                try { e.Pointer.Capture(window); } catch { }
                return true;
            }

            // 画笔
            StrokeShape? hitStroke = FindStrokeShapeAt(pt);
            if (hitStroke != null)
            {
                e.Handled = true;
                ClearAllSelections();
                ForceAnnotationTool(AnnotationTool.Brush);
                selectedStrokeShapeId = hitStroke.Id;
                window.Annotation.SelectedShapeId = hitStroke.Id;
                window.Annotation.InvalidateVisual();
                return true;
            }

            return false;
        }

        HandleDirection HitTestArrowHandle(ArrowShape arrow, PointF pt, float radius = 10.0f)
        {
            float r2 = radius * radius;
            
            float dx = pt.X - arrow.Start.X;
            float dy = pt.Y - arrow.Start.Y;
            if (dx * dx + dy * dy <= r2) return HandleDirection.Left; // Start handle
            
            dx = pt.X - arrow.End.X;
            dy = pt.Y - arrow.End.Y;
            if (dx * dx + dy * dy <= r2) return HandleDirection.Right; // End handle
            
            return HandleDirection.None;
        }

        HandleDirection HitTestRectangleHandle(RectangleShape rect, PointF pt, float radius = 7.0f)
        {
            return HitTestBlurHandle(rect.Rect, pt, radius);
        }

        void UpdateSpotlightCursor(PointF pt)
        {
            if (activeAnnotationTool != AnnotationTool.Spotlight || movingSpotlightShape || pendingMoveSpotlightShape || resizingSpotlightShape || adjustingSpotlightRadiusShape)
            {
                window.Cursor = null;
                return;
            }

            SpotlightShape? selected = GetSelectedSpotlightShape();
            if (selected != null)
            {
                if (!string.IsNullOrEmpty(HitTestSpotlightRadiusHandle(selected, pt)))
                {
                    window.Cursor = new Cursor(StandardCursorType.Hand);
                    return;
                }

                HandleDirection handle = HitTestBlurHandle(selected.Rect, pt);
                if (handle != HandleDirection.None)
                {
                    window.Cursor = handle switch
                    {
                        HandleDirection.Top or HandleDirection.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
                        HandleDirection.Left or HandleDirection.Right => new Cursor(StandardCursorType.SizeWestEast),
                        HandleDirection.TopLeft or HandleDirection.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
                        HandleDirection.TopRight or HandleDirection.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
                        _ => new Cursor(StandardCursorType.Arrow),
                    };
                    return;
                }
            }

            window.Cursor = FindSpotlightShapeAt(pt) != null
                ? new Cursor(StandardCursorType.SizeAll)
                : null;
        }

        void SetSpotlightPreviewThrottled(SpotlightShape shape, bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now - lastSpotlightPreviewTick < 16) return;
            lastSpotlightPreviewTick = now;
            window.Annotation.SetPreviewShape(shape);
        }

        static bool ShouldAddBrushPoint(List<PointF> points, PointF next)
        {
            if (points.Count == 0) return true;
            PointF last = points[points.Count - 1];
            float dx = next.X - last.X;
            float dy = next.Y - last.Y;
            return dx * dx + dy * dy >= BrushPointMinDistancePx * BrushPointMinDistancePx;
        }

        void UpdateBrushPreview()
        {
            if (currentStrokePoints.Count >= 2)
            {
                window.Annotation.SetPreviewShape(new StrokeShape(
                    currentStrokePoints.ToArray(),
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedBrushLineStyle));
            }
        }

        void FinishStroke()
        {
            window.Annotation.ClearPreviewShape();
            if (currentStrokePoints.Count >= 2)
            {
                captureHistory.CommitSnapshot();
                var shape = new StrokeShape(
                    currentStrokePoints.ToArray(),
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedBrushLineStyle);
                annotations.AddShape(shape);
                // 绘制完成自动选中，显示可编辑状态
                ClearAllSelections();
                selectedStrokeShapeId = shape.Id;
                window.Annotation.SelectedShapeId = shape.Id;
                window.Annotation.InvalidateVisual();
                AppLogger.Info("CaptureSession.FinishStroke: 已添加并选中画笔，points=" + currentStrokePoints.Count + ", color=" + window.SelectedAnnotationColor + ", thickness=" + window.SelectedStrokeThickness + ", lineStyle=" + window.SelectedBrushLineStyle);
            }

            currentStrokePoints.Clear();
            drawingStroke = false;
        }

        void FinishArrow(PointF arrowEnd)
        {
            window.Annotation.ClearPreviewShape();
            float dx = arrowEnd.X - arrowStart.X;
            float dy = arrowEnd.Y - arrowStart.Y;
            if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var shape = new ArrowShape(
                    arrowStart,
                    arrowEnd,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedArrowStyle,
                    currentArrowScale);
                annotations.AddShape(shape);
                // 绘制完成自动选中，显示锚点手柄
                ClearAllSelections();
                selectedArrowShapeId = shape.Id;
                window.Annotation.SelectedShapeId = shape.Id;
                window.Annotation.InvalidateVisual();
                AppLogger.Info("CaptureSession.FinishArrow: 已添加并选中箭头，start=" + arrowStart + ", end=" + arrowEnd + ", color=" + window.SelectedAnnotationColor + ", thickness=" + window.SelectedStrokeThickness + ", style=" + window.SelectedArrowStyle);
            }

            drawingArrow = false;
        }

        void FinishRectangle(PointF rectangleEnd)
        {
            window.Annotation.ClearPreviewShape();
            RectangleF rect = NormalizeRect(rectangleStart, rectangleEnd);
            if (rect.Width >= DragThresholdPx && rect.Height >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var shape = new RectangleShape(
                    rect,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedShapeKind,
                    window.SelectedShapeLineStyle);
                annotations.AddShape(shape);
                // 绘制完成自动选中，显示缩放手柄和边框
                ClearAllSelections();
                selectedRectangleShapeId = shape.Id;
                window.Annotation.SelectedShapeId = shape.Id;
                window.Annotation.InvalidateVisual();
                AppLogger.Info("CaptureSession.FinishRectangle: 已添加并选中形状，rect=" + rect + ", color=" + window.SelectedAnnotationColor + ", thickness=" + window.SelectedStrokeThickness + ", kind=" + window.SelectedShapeKind + ", lineStyle=" + window.SelectedShapeLineStyle);
            }

            drawingRectangle = false;
        }

        void FinishMosaic(PointF mosaicEnd, bool forceSquare)
        {
            window.Annotation.ClearPreviewShape();
            RectangleF rect = CreateDrawRect(mosaicStart, mosaicEnd, forceSquare);
            if (rect.Width >= DragThresholdPx && rect.Height >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var blur = new BlurShape(
                    rect,
                    window.SelectedBlurMode,
                    window.SelectedBlurUiLevel);
                annotations.AddShape(blur);
                // 新建马赛克/模糊完成后保留边线编辑框，清掉拖拽时的蓝色半透明填充预览。
                SelectBlurShape(blur.Id);
                AppLogger.Info("CaptureSession.FinishMosaic: 已添加效果，rect=" + rect + ", mode=" + window.SelectedBlurMode + ", uiLevel=" + window.SelectedBlurUiLevel + ", blockSize=" + window.SelectedMosaicBlockSize + ", forceSquare=" + forceSquare);
            }

            drawingMosaic = false;
        }

        void FinishSpotlight(PointF spotlightEnd, bool forceSquare)
        {
            RectangleF rect = ClampSpotlightRect(CreateDrawRect(spotlightStart, spotlightEnd, forceSquare));
            window.Annotation.ClearPreviewShape();
            if (rect.Width >= DragThresholdPx && rect.Height >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var spotlight = new SpotlightShape(
                    rect,
                    window.SelectedSpotlightDarkness,
                    window.SelectedSpotlightKind,
                    window.SelectedSpotlightStrokeColor,
                    window.SelectedSpotlightStrokeThickness);
                annotations.AddShape(spotlight);
                SelectSpotlightShape(spotlight.Id);
                AppLogger.Info("CaptureSession.FinishSpotlight: 已添加聚光，rect=" + rect + ", darkness=" + window.SelectedSpotlightDarkness + ", kind=" + window.SelectedSpotlightKind);
            }

            drawingSpotlight = false;
        }

        int GetNextCounterNumber()
        {
            int max = 0;
            foreach (AnnotationShape shape in annotations.Shapes)
            {
                if (shape is CounterShape counter && counter.Number > max)
                {
                    max = counter.Number;
                }
            }
            return max + 1;
        }

        void AddCounter(PointF center)
        {
            captureHistory.CommitSnapshot();
            int number = GetNextCounterNumber();
            var shape = new CounterShape(
                center,
                number,
                window.SelectedCounterRadius,
                window.SelectedAnnotationColor,
                Color.White);
            annotations.AddShape(shape);
            // 绘制完成自动选中，显示选中圈
            ClearAllSelections();
            selectedCounterShapeId = shape.Id;
            window.Annotation.SelectedShapeId = shape.Id;
            window.Annotation.InvalidateVisual();
            AppLogger.Info("CaptureSession.AddCounter: 已添加并选中编号，number=" + number + ", center=" + center + ", radius=" + window.SelectedCounterRadius + ", color=" + window.SelectedAnnotationColor);
        }

        bool ShouldClearShapeForCurrentTool(AnnotationShape shape)
        {
            return activeAnnotationTool switch
            {
                AnnotationTool.Brush => shape is StrokeShape,
                AnnotationTool.Arrow => shape is ArrowShape,
                AnnotationTool.Rectangle => shape is RectangleShape,
                AnnotationTool.Mosaic => shape is BlurShape,
                AnnotationTool.Spotlight => shape is SpotlightShape,
                AnnotationTool.Counter => shape is CounterShape,
                _ => false,
            };
        }

        void ClearCurrentToolAnnotations()
        {
            if (activeAnnotationTool == AnnotationTool.None || activeAnnotationTool == AnnotationTool.ColorPicker) return;

            // 文本工具：清空窗口层上的所有文本
            if (activeAnnotationTool == AnnotationTool.Text)
            {
                captureHistory.CommitSnapshot();
                window.ClearAllTexts();
                AppLogger.Info("CaptureSession.ClearCurrentToolAnnotations: 已清除所有文本");
                return;
            }

            AnnotationShape[] remaining = annotations.Shapes.Where(shape => !ShouldClearShapeForCurrentTool(shape)).ToArray();
            if (remaining.Length == annotations.Count) return;

            captureHistory.CommitSnapshot();
            annotations.ReplaceAll(remaining);
            window.Annotation.ClearPreviewShape();
            AppLogger.Info("CaptureSession.ClearCurrentToolAnnotations: 已清除工具标注，tool=" + activeAnnotationTool);
        }

        void ClearAllAnnotations()
        {
            if (annotations.Count == 0 && window.TextLayers.Count == 0) return;

            captureHistory.CommitSnapshot();
            annotations.Clear();
            window.ClearAllTexts();
            window.Annotation.ClearPreviewShape();
            window.Annotation.ClearAutoMosaicHighlights();
            autoMosaicHighlights.Clear();
            ClearAllSelections();
            AppLogger.Info("CaptureSession.ClearAllAnnotations: 已清除所有标注和文本");
        }

        async void RunAutoMosaicRecognition()
        {
            if (!hasManualSelection || !selection.HasSelection || frame?.FullScreen == null)
            {
                window.SetAutoMosaicBusy(false);
                return;
            }

            window.SetAutoMosaicBusy(true);
            autoMosaicHighlights.Clear();
            window.Annotation.ClearAutoMosaicHighlights();

            Rectangle region = selection.Region;
            Rectangle bmpRect = new Rectangle(region.X - frame.VirtualScreenRect.X, region.Y - frame.VirtualScreenRect.Y, region.Width, region.Height);
            bmpRect.Intersect(new Rectangle(0, 0, frame.FullScreen.Width, frame.FullScreen.Height));
            if (bmpRect.Width <= 0 || bmpRect.Height <= 0)
            {
                window.SetAutoMosaicBusy(false);
                return;
            }

            long start = Environment.TickCount64;
            try
            {
                using Bitmap cropped = frame.FullScreen.Clone(bmpRect, PixelFormat.Format32bppArgb);
                // cropped 是物理像素位图，WinRT OCR 返回的 BoundingRect 也是该位图像素坐标；这里不能再除以 DPI，否则识别框会偏小且对不上文本。
                IReadOnlyList<RectangleF> localRects = await OcrTextRegionDetector.DetectTextRegionsAsync(cropped, 1.0);
                long elapsed = Environment.TickCount64 - start;
                if (elapsed < 1200) await Task.Delay((int)(1200 - elapsed));

                foreach (RectangleF local in localRects)
                {
                    RectangleF screen = RectangleF.FromLTRB(
                        region.X + local.Left,
                        region.Y + local.Top,
                        region.X + local.Right,
                        region.Y + local.Bottom);
                    screen = ExpandAutoMosaicRect(screen, region);
                    if (screen.Width >= 2 && screen.Height >= 2) autoMosaicHighlights.Add(screen);
                }
                window.Annotation.SetAutoMosaicHighlights(autoMosaicHighlights.ToArray());
                AppLogger.Info("CaptureSession.AutoMosaic: OCR 完成，count=" + autoMosaicHighlights.Count);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureSession.AutoMosaic: OCR 失败：" + ex.Message);
            }
            finally
            {
                window.SetAutoMosaicBusy(false);
            }
        }

        bool TrySampleColor(System.Drawing.Point screenPt, out Color color)
        {
            color = Color.Empty;
            if (frame?.FullScreen == null) return false;

            int x = screenPt.X - frame.VirtualScreenRect.X;
            int y = screenPt.Y - frame.VirtualScreenRect.Y;
            if (x < 0 || y < 0 || x >= frame.FullScreen.Width || y >= frame.FullScreen.Height) return false;

            color = frame.FullScreen.GetPixel(x, y);
            return true;
        }

        static string FormatHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        void CompleteColorPick(System.Drawing.Point screenPt)
        {
            if (!IsPointInsideManualSelection(screenPt)) return;
            if (!TrySampleColor(screenPt, out Color color)) return;

            string hex = FormatHex(color);
            ClipboardService.SetText(hex);
            AppLogger.Info("CaptureSession.CompleteColorPick: 已复制颜色 " + hex + ", point=" + screenPt);
            window.SetActiveTool(null);
            window.HideColorPickerPreview();
            tcs.TrySetResult(new CaptureCompletion(selection.Region, CaptureCompletionAction.ColorPick, hex));
        }

        // 拖动起始信息（屏幕物理像素）
        System.Drawing.Point dragStartScreen = default;
        Rectangle dragStartRect = default;
        HandleDirection resizeDirection = HandleDirection.None;
        bool dragMovedPastThreshold = false; // 单击 vs 拖动判定

        // (a) SelectionState.Changed → SelectionLayer.Region（DIP）
        selection.Changed += s =>
        {
            // SelectionState 在 UI 线程上被改（鼠标事件触发），无须 Post
            if (!window.IsVisible) return;
            UpdateSelectionVisual(s.HasSelection ? s.Region : null, hasManualSelection);
        };

        // (b) AutoDetectController.HighlightChanged → SelectionLayer.Region（仅手动选区前）
        autoDetect.HighlightChanged += rectScreen =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!window.IsVisible) return;

                // 已经手动选过 / 正在手动操作 → 不让自动检测覆盖。忽略此高亮，并不再更新 currentHighlight。
                if (hasManualSelection || mode != InteractionMode.Idle)
                {
                    StopAutoHoverAnimation();
                    return;
                }

                currentHighlight = rectScreen;
                ApplyAutoHoverVisual(rectScreen);
            });
        };

        // (c) PointerMoved → 推给自动检测（仅 Idle）；或更新手动操作
        window.PointerMoved += (_, e) =>
        {
            if (e.Handled) return;
            var dip = e.GetPosition(window);
            window.Layer.MousePosition = dip;

            bool isInteracting = mode != InteractionMode.Idle
                || drawingStroke
                || drawingArrow
                || drawingRectangle
                || drawingMosaic
                || drawingSpotlight
                || movingBlurShape
                || resizingBlurShape
                || movingSpotlightShape
                || pendingMoveSpotlightShape
                || resizingSpotlightShape
                || adjustingSpotlightRadiusShape
                || movingArrowShape
                || movingRectangleShape
                || movingCounterShape
                || resizingRectangleShape
                || window.IsDraggingText
                || window.IsRotatingText;

            if (!isInteracting && (window.IsOverlayHit(e.Source) || window.IsOverlayPoint(dip))) return;

            var screenPt = window.WindowDipToScreenPoint(dip);

            // 记录瞬时鼠标速度，供 hover 闸门判断"是否在快速移动"。
            {
                long nowMs = Environment.TickCount64;
                if (lastPointerTickMs != 0)
                {
                    double dt = Math.Max(1.0, nowMs - lastPointerTickMs);
                    double dx = screenPt.X - lastPointerScreen.X;
                    double dy = screenPt.Y - lastPointerScreen.Y;
                    pointerSpeedPxPerMs = Math.Sqrt(dx * dx + dy * dy) / dt;
                }
                lastPointerScreen = screenPt;
                lastPointerTickMs = nowMs;
            }

            if (activeAnnotationTool == AnnotationTool.ColorPicker)
            {
                if (IsPointInsideManualSelection(screenPt) && TrySampleColor(screenPt, out Color color))
                {
                    window.ShowColorPickerPreview(screenPt, color);
                }
                else
                {
                    window.HideColorPickerPreview();
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Brush && drawingStroke)
            {
                if (IsPointInsideManualSelection(screenPt))
                {
                    var next = new PointF(screenPt.X, screenPt.Y);
                    if (ShouldAddBrushPoint(currentStrokePoints, next))
                    {
                        currentStrokePoints.Add(next);
                        UpdateBrushPreview();
                    }
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Arrow && drawingArrow)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                window.Annotation.SetPreviewShape(new ArrowShape(
                    arrowStart,
                    end,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedArrowStyle,
                    currentArrowScale));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Rectangle && drawingRectangle)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                RectangleF rect = NormalizeRect(rectangleStart, end);
                window.Annotation.SetPreviewShape(new RectangleShape(
                    rect,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedShapeKind,
                    window.SelectedShapeLineStyle));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Mosaic && drawingMosaic)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                RectangleF rect = CreateDrawRect(mosaicStart, end, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                window.Annotation.SetPreviewShape(new BlurShape(rect, window.SelectedBlurMode, window.SelectedBlurUiLevel));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Mosaic && movingBlurShape && GetSelectedBlurShape() is BlurShape movingBlur)
            {
                float dx = screenPt.X - blurGestureStart.X;
                float dy = screenPt.Y - blurGestureStart.Y;
                RectangleF nextRect = new RectangleF(blurGestureStartRect.X + dx, blurGestureStartRect.Y + dy, blurGestureStartRect.Width, blurGestureStartRect.Height);
                window.Annotation.SetPreviewShape(movingBlur.WithRect(nextRect));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Mosaic && resizingBlurShape && GetSelectedBlurShape() is BlurShape resizingBlur)
            {
                float dx = screenPt.X - blurGestureStart.X;
                float dy = screenPt.Y - blurGestureStart.Y;
                RectangleF nextRect = ResizeRect(blurGestureStartRect, blurResizeDirection, dx, dy);
                window.Annotation.SetPreviewShape(resizingBlur.WithRect(nextRect));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && drawingSpotlight)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                RectangleF rect = ClampSpotlightRect(CreateDrawRect(spotlightStart, end, e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
                SetSpotlightPreviewThrottled(new SpotlightShape(
                    rect,
                    window.SelectedSpotlightDarkness,
                    window.SelectedSpotlightKind,
                    window.SelectedSpotlightStrokeColor,
                    window.SelectedSpotlightStrokeThickness));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && pendingMoveSpotlightShape && GetSelectedSpotlightShape() is SpotlightShape pendingSpotlight)
            {
                float dx = screenPt.X - spotlightGestureStart.X;
                float dy = screenPt.Y - spotlightGestureStart.Y;
                if (Math.Abs(dx) >= DragThresholdPx || Math.Abs(dy) >= DragThresholdPx)
                {
                    captureHistory.CommitSnapshot();
                    pendingMoveSpotlightShape = false;
                    movingSpotlightShape = true;
                    RectangleF nextRect = TranslateAndClampSpotlightRect(spotlightGestureStartRect, dx, dy);
                    SetSpotlightPreviewThrottled(pendingSpotlight.WithRect(nextRect), true);
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && movingSpotlightShape && GetSelectedSpotlightShape() is SpotlightShape movingSpotlight)
            {
                float dx = screenPt.X - spotlightGestureStart.X;
                float dy = screenPt.Y - spotlightGestureStart.Y;
                RectangleF nextRect = TranslateAndClampSpotlightRect(spotlightGestureStartRect, dx, dy);
                SetSpotlightPreviewThrottled(movingSpotlight.WithRect(nextRect));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && resizingSpotlightShape && GetSelectedSpotlightShape() is SpotlightShape resizingSpotlight)
            {
                float dx = screenPt.X - spotlightGestureStart.X;
                float dy = screenPt.Y - spotlightGestureStart.Y;
                RectangleF nextRect = ResizeAndClampSpotlightRect(spotlightGestureStartRect, spotlightResizeDirection, dx, dy);
                SetSpotlightPreviewThrottled(resizingSpotlight.WithRect(nextRect));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && adjustingSpotlightRadiusShape && GetSelectedSpotlightShape() is SpotlightShape radiusSpotlight)
            {
                PointF pt = new PointF(screenPt.X, screenPt.Y);
                RectangleF clampedRect = ClampSpotlightRect(radiusSpotlight.Rect);
                float nextRadius = CalculateSpotlightRadius(spotlightRadiusHandleTag, spotlightRadiusStartValue, spotlightGestureStart, pt, clampedRect);
                SetSpotlightPreviewThrottled(radiusSpotlight.WithRect(clampedRect).WithCornerRadius(nextRadius), true);
                e.Handled = true;
                return;
            }

            // 处理通过Ctrl键选择后的形状移动
            if (movingArrowShape && selectedArrowShapeId.HasValue && annotations.Shapes.OfType<ArrowShape>().FirstOrDefault(x => x.Id == selectedArrowShapeId.Value) is ArrowShape movingArrow)
            {
                float dx = screenPt.X - arrowGestureStart.X;
                float dy = screenPt.Y - arrowGestureStart.Y;
                
                PointF newStart, newEnd;
                if (arrowHandleType == HandleDirection.Left)
                {
                    // 拖动起点
                    newStart = new PointF(arrowStartOriginal.X + dx, arrowStartOriginal.Y + dy);
                    newEnd = arrowEndOriginal;
                }
                else if (arrowHandleType == HandleDirection.Right)
                {
                    // 拖动终点
                    newStart = arrowStartOriginal;
                    newEnd = new PointF(arrowEndOriginal.X + dx, arrowEndOriginal.Y + dy);
                }
                else
                {
                    // 移动整个箭头
                    newStart = new PointF(arrowStartOriginal.X + dx, arrowStartOriginal.Y + dy);
                    newEnd = new PointF(arrowEndOriginal.X + dx, arrowEndOriginal.Y + dy);
                }
                
                var nextArrow = new ArrowShape(newStart, newEnd, movingArrow.Color, movingArrow.Thickness, movingArrow.Style, movingArrow.Scale, movingArrow.Id);
                annotations.ReplaceShape(nextArrow);
                e.Handled = true;
                return;
            }

            if (movingRectangleShape && selectedRectangleShapeId.HasValue)
            {
                var movingRect = annotations.Shapes.OfType<RectangleShape>().FirstOrDefault(x => x.Id == selectedRectangleShapeId.Value);
                if (movingRect != null)
            {
                float dx = screenPt.X - rectangleGestureStart.X;
                float dy = screenPt.Y - rectangleGestureStart.Y;
                RectangleF nextRect = new RectangleF(rectangleGestureStartRect.X + dx, rectangleGestureStartRect.Y + dy, rectangleGestureStartRect.Width, rectangleGestureStartRect.Height);
                var next = new RectangleShape(nextRect, movingRect.StrokeColor, movingRect.StrokeThickness, movingRect.ShapeKind, movingRect.LineStyle, movingRect.Id);
                annotations.ReplaceShape(next);
                e.Handled = true;
                return;
            }
            }

            if (resizingRectangleShape && selectedRectangleShapeId.HasValue)
            {
                var resizingRect = annotations.Shapes.OfType<RectangleShape>().FirstOrDefault(x => x.Id == selectedRectangleShapeId.Value);
                if (resizingRect != null)
                {
                float dx = screenPt.X - rectangleGestureStart.X;
                float dy = screenPt.Y - rectangleGestureStart.Y;
                RectangleF nextRect = ResizeRect(rectangleGestureStartRect, rectangleResizeDirection, dx, dy);
                var next = new RectangleShape(nextRect, resizingRect.StrokeColor, resizingRect.StrokeThickness, resizingRect.ShapeKind, resizingRect.LineStyle, resizingRect.Id);
                annotations.ReplaceShape(next);
                e.Handled = true;
                return;
            }
            }

            if (movingCounterShape && selectedCounterShapeId.HasValue)
            {
                var movingCounter = annotations.Shapes.OfType<CounterShape>().FirstOrDefault(x => x.Id == selectedCounterShapeId.Value);
                if (movingCounter != null)
                {
                float dx = screenPt.X - counterGestureStart.X;
                float dy = screenPt.Y - counterGestureStart.Y;
                PointF newCenter = new PointF(counterOriginalCenter.X + dx, counterOriginalCenter.Y + dy);
                var next = new CounterShape(newCenter, movingCounter.Number, movingCounter.Radius, movingCounter.FillColor, movingCounter.TextColor, movingCounter.Id);
                annotations.ReplaceShape(next);
                e.Handled = true;
                return;
            }
            }

            if (activeAnnotationTool == AnnotationTool.Mosaic)
            {
                PointF pt = new PointF(screenPt.X, screenPt.Y);
                RectangleF? hover = null;
                foreach (RectangleF r in autoMosaicHighlights)
                {
                    if (Contains(r, pt)) { hover = r; break; }
                }
                window.Annotation.HoverAutoMosaicHighlight = hover;
                window.Annotation.InvalidateVisual();
            }
            else if (activeAnnotationTool == AnnotationTool.Spotlight)
            {
                UpdateSpotlightCursor(new PointF(screenPt.X, screenPt.Y));
            }
            else
            {
                window.Cursor = null;
            }

            if (activeAnnotationTool != AnnotationTool.None) return;

            if (pendingExpandResize)
            {
                bool leftPressed = e.GetCurrentPoint(window).Properties.IsLeftButtonPressed;
                if (!leftPressed)
                {
                    pendingExpandResize = false;
                    pendingExpandResizeDirection = HandleDirection.None;
                }
                else
                {
                    int dx0 = screenPt.X - pendingExpandStartPoint.X;
                    int dy0 = screenPt.Y - pendingExpandStartPoint.Y;
                    if (Math.Abs(dx0) >= DragThresholdPx || Math.Abs(dy0) >= DragThresholdPx)
                    {
                        mode = InteractionMode.ResizingHandle;
                        resizeDirection = pendingExpandResizeDirection;
                        dragStartScreen = pendingExpandStartPoint;
                        dragStartRect = pendingExpandStartRect;
                        dragMovedPastThreshold = true;
                        pendingExpandResize = false;
                        pendingExpandResizeDirection = HandleDirection.None;
                        BeginSelectionTransform();
                        try { e.Pointer.Capture(window); } catch { }
                    }
                }
            }

            switch (mode)
            {
                case InteractionMode.Idle:
                    autoDetect.NotifyPointerMoved(screenPt);
                    break;

                case InteractionMode.DraggingNew:
                    if (!dragMovedPastThreshold &&
                        (Math.Abs(screenPt.X - dragStartScreen.X) >= DragThresholdPx ||
                        Math.Abs(screenPt.Y - dragStartScreen.Y) >= DragThresholdPx))
                    {
                        dragMovedPastThreshold = true;
                        currentHighlight = null;
                        StopAutoHoverAnimation();
                    }
                    if (dragMovedPastThreshold)
                    {
                        int x = Math.Min(dragStartScreen.X, screenPt.X);
                        int y = Math.Min(dragStartScreen.Y, screenPt.Y);
                        int w = Math.Abs(screenPt.X - dragStartScreen.X);
                        int h = Math.Abs(screenPt.Y - dragStartScreen.Y);
                        selection.SetRegion(new Rectangle(x, y, w, h));
                    }
                    break;

                case InteractionMode.MovingSelection:
                    {
                        int dx = screenPt.X - dragStartScreen.X;
                        int dy = screenPt.Y - dragStartScreen.Y;
                        dragMovedPastThreshold = dragMovedPastThreshold || dx != 0 || dy != 0;
                        if (dragMovedPastThreshold)
                        {
                            UpdateTransformVisual(new Rectangle(
                                dragStartRect.X + dx, dragStartRect.Y + dy,
                                dragStartRect.Width, dragStartRect.Height));
                        }
                    }
                    break;

                case InteractionMode.ResizingHandle:
                    {
                        int dx = screenPt.X - dragStartScreen.X;
                        int dy = screenPt.Y - dragStartScreen.Y;
                        dragMovedPastThreshold = dragMovedPastThreshold || dx != 0 || dy != 0;
                        if (dragMovedPastThreshold)
                        {
                            UpdateTransformVisual(CalculateResizedRect(dragStartRect, resizeDirection, dx, dy));
                        }
                    }
                    break;
            }

            // —— 速度闸门 flush：鼠标慢下来后，采用挂起的最新目标 ——
            // 加 mode/hasManualSelection 守卫，避免在拖拽/已选区时误启动动画。
            // 此时 pointerSpeedPxPerMs 已 < SettledSpeedPxPerMs < FastMoveThresholdPxPerMs，
            // SetAutoHoverTarget 内的闸门会放行，不会再次挂起，无递归。
            if (mode == InteractionMode.Idle && !hasManualSelection
                && hasPendingAutoHoverTarget
                && pointerSpeedPxPerMs < SettledSpeedPxPerMs)
            {
                hasPendingAutoHoverTarget = false;
                Rect pending = pendingAutoHoverTargetDip;
                pendingAutoHoverTargetDip = default;
                SetAutoHoverTarget(pending);
            }
        };

        // (d) PointerPressed → 决定进入哪种 InteractionMode
        // 使用 AddHandler 确保即使事件被其他控件（如 TextBox）标记为 Handled，我们仍能接收。
        window.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Handled) return;
            var pressDip = e.GetPosition(window);
            if (window.IsOverlayPoint(pressDip)) return;
            var pp = e.GetCurrentPoint(window);
            if (pp.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
            {
                e.Handled = true;
                CancelCapture();
                return;
            }

            bool isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool isToolActive = activeAnnotationTool != AnnotationTool.None;

            if (window.IsOverlayHit(e.Source))
            {
                // 无工具 + Ctrl + 点击文本图层 → 激活文本工具并选中
                if (!isToolActive && isCtrlPressed && hasManualSelection && selection.HasSelection)
                {
                    var ovDip = e.GetPosition(window);
                    TextLayerInfo? hitText = window.FindTextAtPoint(ovDip);
                    if (hitText != null)
                    {
                        e.Handled = true;
                        ClearAllSelections();
                        ForceAnnotationTool(AnnotationTool.Text);
                        window.SelectText(hitText);
                        try { e.Pointer.Capture(window); } catch { }
                        return;
                    }
                }

                // 文本工具下 Ctrl+点击其他标注图形 → 切换到对应工具并选中
                if (activeAnnotationTool == AnnotationTool.Text && _isCtrlPressed)
                {
                    var ovPt = e.GetPosition(window);
                    var ovScreen = window.WindowDipToScreenPoint(ovPt);
                    // 尝试切换到其他工具
                    if (TrySwitchFromTextToOtherAnnotation(e, new PointF(ovScreen.X, ovScreen.Y))) return;
                    // 如果没有命中其他图形，也不应该创建新文本
                    return;
                }

                // 文本工具特例：点击 OverlayCanvas 时需要检测选中/创建文本
                if (activeAnnotationTool == AnnotationTool.Text)
                {
                    var ovDip = e.GetPosition(window);
                    if (!window.TryHandleTextToolInteraction(ovDip, e.ClickCount))
                    {
                        captureHistory.CommitSnapshot();
                        window.AddTextAt(ovDip);
                        AppLogger.Info("CaptureSession.Text: 在 (" + ovDip.X + "," + ovDip.Y + ") 创建文本");
                    }
                    if (window.IsDraggingText)
                    {
                        try { e.Pointer.Capture(window); } catch { }
                    }
                }
                return;
            }

            if (pp.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

            // 鼠标一旦点击，立即暂停自动检测元素，给选区让步，优先级max
            try { autoDetect.IsPaused = true; } catch { }

            var dip = e.GetPosition(window);
            var screenPt = window.WindowDipToScreenPoint(dip);

            // 马赛克/聚光灯工具处于激活状态时，也允许双击直接确认截图；
            // 否则双击会先被工具绘制/选中逻辑吃掉，必须退出工具才能确认。
            if (e.ClickCount >= 2 && (activeAnnotationTool == AnnotationTool.Mosaic || activeAnnotationTool == AnnotationTool.Spotlight))
            {
                e.Handled = true;
                ConfirmCurrentSelection();
                return;
            }

            // Ctrl+点击文本图层 → 切回文本工具并选中（不进入编辑态）
            if (isCtrlPressed && isToolActive)
            {
                var textHitDip = e.GetPosition(window);
                TextLayerInfo? ctrlTextHit = window.FindTextAtPoint(textHitDip);
                if (ctrlTextHit != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Text);
                    window.SelectText(ctrlTextHit);
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }
            }

            // 文本工具必须在主点击链路处理之前，优先检测 Ctrl+点击其他标注图形
            if (hasManualSelection && selection.HasSelection && activeAnnotationTool == AnnotationTool.Text && _isCtrlPressed)
            {
                PointF ctrlScreenPt = new PointF(screenPt.X, screenPt.Y);
                if (TrySwitchFromTextToOtherAnnotation(e, ctrlScreenPt)) return;
            }

            // 文本工具必须在主点击链路处理：点击截图图层时 e.Source 通常不是 OverlayCanvas。
            if (hasManualSelection && selection.HasSelection && activeAnnotationTool == AnnotationTool.Text && IsPointInsideManualSelection(screenPt))
            {
                e.Handled = true;
                var textDip = e.GetPosition(window);
                if (!window.TryHandleTextToolInteraction(textDip, e.ClickCount))
                {
                    captureHistory.CommitSnapshot();
                    window.AddTextAt(textDip);
                    AppLogger.Info("CaptureSession.Text: 在 (" + textDip.X + "," + textDip.Y + ") 创建文本");
                }
                if (window.IsDraggingText)
                {
                    try { e.Pointer.Capture(window); } catch { }
                }
                return;
            }

            // 按Ctrl键或当前工具激活时，允许选择和编辑已绘制的形状
            // 旧版逻辑：isToolActive && (isCtrlPressed || 当前工具匹配)

            if (hasManualSelection && selection.HasSelection && (isToolActive || isCtrlPressed))
            {
                PointF ctrlPt = new PointF(screenPt.X, screenPt.Y);
                AppLogger.Info($"PointerPressed: 进入形状选择逻辑 at ({ctrlPt.X}, {ctrlPt.Y}), isCtrl={isCtrlPressed}, tool={activeAnnotationTool}, shapesCount={annotations.Count}");
                
                // 优先级：聚光灯编辑手柄 > 聚光灯 > 模糊 > 序号 > 文本 > 箭头 > 矩形 > 画笔
                // 任何工具激活时，点击形状都能自动选中并切换工具（无需Ctrl）。
                // 聚光灯必须先判定当前选中层的圆角/缩放手柄，否则手柄点会被内部命中提前当作移动。
                SpotlightShape? selectedSpotlightForEdit = GetSelectedSpotlightShape();
                if (selectedSpotlightForEdit != null)
                {
                    string radiusTag = HitTestSpotlightRadiusHandle(selectedSpotlightForEdit, ctrlPt);
                    if (!string.IsNullOrEmpty(radiusTag))
                    {
                        e.Handled = true;
                        ForceAnnotationTool(AnnotationTool.Spotlight);
                        SelectSpotlightShape(selectedSpotlightForEdit.Id);
                        captureHistory.CommitSnapshot();
                        adjustingSpotlightRadiusShape = true;
                        spotlightRadiusHandleTag = radiusTag;
                        spotlightGestureStart = ctrlPt;
                        spotlightGestureStartRect = selectedSpotlightForEdit.Rect;
                        spotlightRadiusStartValue = selectedSpotlightForEdit.CornerRadius;
                        lastSpotlightPreviewTick = 0;
                        try { e.Pointer.Capture(window); } catch { }
                        return;
                    }

                    HandleDirection spotlightHandle = HitTestBlurHandle(selectedSpotlightForEdit.Rect, ctrlPt);
                    if (spotlightHandle != HandleDirection.None)
                    {
                        e.Handled = true;
                        ForceAnnotationTool(AnnotationTool.Spotlight);
                        SelectSpotlightShape(selectedSpotlightForEdit.Id);
                        captureHistory.CommitSnapshot();
                        resizingSpotlightShape = true;
                        spotlightResizeDirection = spotlightHandle;
                        spotlightGestureStart = ctrlPt;
                        spotlightGestureStartRect = selectedSpotlightForEdit.Rect;
                        lastSpotlightPreviewTick = 0;
                        try { e.Pointer.Capture(window); } catch { }
                        return;
                    }
                }

                SpotlightShape? ctrlHitSpotlight = FindSpotlightShapeAt(ctrlPt);
                if (ctrlHitSpotlight != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Spotlight);
                    SelectSpotlightShape(ctrlHitSpotlight.Id);
                    pendingMoveSpotlightShape = true;
                    spotlightGestureStart = ctrlPt;
                    spotlightGestureStartRect = ClampSpotlightRect(ctrlHitSpotlight.Rect);
                    lastSpotlightPreviewTick = 0;
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }
                
                BlurShape? ctrlHitBlur = FindBlurShapeAt(ctrlPt);
                if (ctrlHitBlur != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Mosaic);
                    SelectBlurShape(ctrlHitBlur.Id);
                    
                    HandleDirection handle = HitTestBlurHandle(ctrlHitBlur.Rect, ctrlPt);
                    if (handle != HandleDirection.None)
                    {
                        captureHistory.CommitSnapshot();
                        resizingBlurShape = true;
                        blurResizeDirection = handle;
                        blurGestureStart = ctrlPt;
                        blurGestureStartRect = ctrlHitBlur.Rect;
                    }
                    else
                    {
                        captureHistory.CommitSnapshot();
                        movingBlurShape = true;
                        blurGestureStart = ctrlPt;
                        blurGestureStartRect = ctrlHitBlur.Rect;
                    }
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }
                
                CounterShape? ctrlHitCounter = FindCounterShapeAt(ctrlPt);
                if (ctrlHitCounter != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Counter);
                    selectedCounterShapeId = ctrlHitCounter.Id;
                    window.Annotation.SelectedShapeId = ctrlHitCounter.Id;
                    captureHistory.CommitSnapshot();
                    movingCounterShape = true;
                    counterGestureStart = ctrlPt;
                    counterOriginalCenter = ctrlHitCounter.Center;
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }

                // 文本图层（优先级在序号之后、箭头之前）
                TextLayerInfo? ctrlHitText = window.FindTextAtPoint(new Avalonia.Point(ctrlPt.X, ctrlPt.Y));
                if (ctrlHitText != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Text);
                    window.TryHandleTextToolInteraction(new Avalonia.Point(ctrlPt.X, ctrlPt.Y), e.ClickCount);
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }

                ArrowShape? ctrlHitArrow = FindArrowShapeAt(ctrlPt);
                if (ctrlHitArrow != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Arrow);
                    selectedArrowShapeId = ctrlHitArrow.Id;
                    window.Annotation.SelectedShapeId = ctrlHitArrow.Id;
                    currentArrowScale = ctrlHitArrow.Scale; // 同步缩放值
                    
                    HandleDirection arrowHandle = HitTestArrowHandle(ctrlHitArrow, ctrlPt);
                    if (arrowHandle != HandleDirection.None)
                    {
                        captureHistory.CommitSnapshot();
                        movingArrowShape = true;
                        arrowHandleType = arrowHandle;
                        arrowGestureStart = ctrlPt;
                        arrowStartOriginal = ctrlHitArrow.Start;
                        arrowEndOriginal = ctrlHitArrow.End;
                    }
                    else
                    {
                        captureHistory.CommitSnapshot();
                        movingArrowShape = true;
                        arrowHandleType = HandleDirection.None; // 移动整个箭头
                        arrowGestureStart = ctrlPt;
                        arrowStartOriginal = ctrlHitArrow.Start;
                        arrowEndOriginal = ctrlHitArrow.End;
                    }
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }
                
                RectangleShape? ctrlHitRectangle = FindRectangleShapeAt(ctrlPt);
                if (ctrlHitRectangle != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Rectangle);
                    selectedRectangleShapeId = ctrlHitRectangle.Id;
                    window.Annotation.SelectedShapeId = ctrlHitRectangle.Id;
                    
                    HandleDirection rectHandle = HitTestRectangleHandle(ctrlHitRectangle, ctrlPt);
                    if (rectHandle != HandleDirection.None)
                    {
                        captureHistory.CommitSnapshot();
                        resizingRectangleShape = true;
                        rectangleResizeDirection = rectHandle;
                        rectangleGestureStart = ctrlPt;
                        rectangleGestureStartRect = ctrlHitRectangle.Rect;
                    }
                    else
                    {
                        captureHistory.CommitSnapshot();
                        movingRectangleShape = true;
                        rectangleGestureStart = ctrlPt;
                        rectangleGestureStartRect = ctrlHitRectangle.Rect;
                    }
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }
                
                StrokeShape? ctrlHitStroke = FindStrokeShapeAt(ctrlPt);
                if (ctrlHitStroke != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Brush);
                    selectedStrokeShapeId = ctrlHitStroke.Id;
                    window.Annotation.SelectedShapeId = ctrlHitStroke.Id;
                    window.Annotation.InvalidateVisual();
                    // 画笔笔画只支持选中和删除，不支持移动
                    return;
                }
            }

            if (activeAnnotationTool != AnnotationTool.None)
            {
                e.Handled = true;
                if (IsPointInsideManualSelection(screenPt))
                {
                    if (activeAnnotationTool == AnnotationTool.Brush)
                    {
                        drawingStroke = true;
                        currentStrokePoints.Clear();
                        currentStrokePoints.Add(new PointF(screenPt.X, screenPt.Y));
                        window.Annotation.ClearPreviewShape();
                    }
                    else if (activeAnnotationTool == AnnotationTool.Arrow)
                    {
                        drawingArrow = true;
                        arrowStart = new PointF(screenPt.X, screenPt.Y);
                        window.Annotation.ClearPreviewShape();
                    }
                    else if (activeAnnotationTool == AnnotationTool.Rectangle)
                    {
                        drawingRectangle = true;
                        rectangleStart = new PointF(screenPt.X, screenPt.Y);
                        window.Annotation.ClearPreviewShape();
                    }
                    else if (activeAnnotationTool == AnnotationTool.Mosaic)
                    {
                        PointF pt = new PointF(screenPt.X, screenPt.Y);

                        int autoIndex = autoMosaicHighlights.FindIndex(r => Contains(r, pt));
                        if (autoIndex >= 0)
                        {
                            RectangleF r = autoMosaicHighlights[autoIndex];
                            autoMosaicHighlights.RemoveAt(autoIndex);
                            window.Annotation.SetAutoMosaicHighlights(autoMosaicHighlights.ToArray());
                            captureHistory.CommitSnapshot();
                            var blur = new BlurShape(r, window.SelectedBlurMode, window.SelectedBlurUiLevel);
                            annotations.AddShape(blur);
                            SelectBlurShape(blur.Id);
                            AppLogger.Info("CaptureSession.AutoMosaic: 点击识别框创建效果，rect=" + r + ", mode=" + window.SelectedBlurMode + ", uiLevel=" + window.SelectedBlurUiLevel);
                            return;
                        }

                        BlurShape? selectedBlur = GetSelectedBlurShape();
                        if (selectedBlur != null)
                        {
                            HandleDirection handle = HitTestBlurHandle(selectedBlur.Rect, pt);
                            if (handle != HandleDirection.None)
                            {
                                captureHistory.CommitSnapshot();
                                resizingBlurShape = true;
                                blurResizeDirection = handle;
                                blurGestureStart = pt;
                                blurGestureStartRect = selectedBlur.Rect;
                                try { e.Pointer.Capture(window); } catch { }
                                return;
                            }
                        }

                        BlurShape? hitBlur = FindBlurShapeAt(pt);
                        if (hitBlur != null)
                        {
                            SelectBlurShape(hitBlur.Id);
                            captureHistory.CommitSnapshot();
                            movingBlurShape = true;
                            blurGestureStart = pt;
                            blurGestureStartRect = hitBlur.Rect;
                            try { e.Pointer.Capture(window); } catch { }
                            return;
                        }

                        SelectBlurShape(null);
                        drawingMosaic = true;
                        mosaicStart = pt;
                    }
                    else if (activeAnnotationTool == AnnotationTool.Spotlight)
                    {
                        PointF pt = new PointF(screenPt.X, screenPt.Y);

                        SpotlightShape? selectedSpotlight = GetSelectedSpotlightShape();
                        if (selectedSpotlight != null)
                        {
                            string radiusTag = HitTestSpotlightRadiusHandle(selectedSpotlight, pt);
                            if (!string.IsNullOrEmpty(radiusTag))
                            {
                                captureHistory.CommitSnapshot();
                                            adjustingSpotlightRadiusShape = true;
                                spotlightRadiusHandleTag = radiusTag;
                                spotlightGestureStart = pt;
                                spotlightGestureStartRect = selectedSpotlight.Rect;
                                spotlightRadiusStartValue = selectedSpotlight.CornerRadius;
                                lastSpotlightPreviewTick = 0;
                                try { e.Pointer.Capture(window); } catch { }
                                return;
                            }

                            HandleDirection handle = HitTestBlurHandle(selectedSpotlight.Rect, pt);
                            if (handle != HandleDirection.None)
                            {
                                captureHistory.CommitSnapshot();
                                            resizingSpotlightShape = true;
                                spotlightResizeDirection = handle;
                                spotlightGestureStart = pt;
                                spotlightGestureStartRect = selectedSpotlight.Rect;
                                lastSpotlightPreviewTick = 0;
                                try { e.Pointer.Capture(window); } catch { }
                                return;
                            }
                        }

                        SpotlightShape? hitSpotlight = FindSpotlightShapeAt(pt);
                        if (hitSpotlight != null)
                        {
                            SelectSpotlightShape(hitSpotlight.Id);
                            pendingMoveSpotlightShape = true;
                                            spotlightGestureStart = pt;
                            spotlightGestureStartRect = ClampSpotlightRect(hitSpotlight.Rect);
                            lastSpotlightPreviewTick = 0;
                            try { e.Pointer.Capture(window); } catch { }
                            return;
                        }

                        SelectSpotlightShape(null);
                        drawingSpotlight = true;
                        spotlightStart = pt;
                        window.Annotation.ClearPreviewShape();
                        lastSpotlightPreviewTick = 0;
                    }
                    else if (activeAnnotationTool == AnnotationTool.Counter)
                    {
                        AddCounter(new PointF(screenPt.X, screenPt.Y));
                    }
                    else if (activeAnnotationTool == AnnotationTool.ColorPicker)
                    {
                        CompleteColorPick(screenPt);
                    }
                }
                return;
            }

            // 双击：直接确认（不需要先有手动拖动；自动检测高亮也接受）
            if (e.ClickCount >= 2)
            {
                e.Handled = true;
                ConfirmCurrentSelection();
                return;
            }

            dragStartScreen = screenPt;
            dragMovedPastThreshold = false;

            if (hasManualSelection && selection.HasSelection)
            {
                // 在手柄上？
                var handle = selection.HitTestResizeDirection(
                    new System.Drawing.Point(screenPt.X, screenPt.Y), HandleHitRadiusPx, BorderGripPx);

                if (handle != HandleDirection.None)
                {
                    mode = InteractionMode.ResizingHandle;
                    resizeDirection = handle;
                    dragStartRect = selection.Region;
                    BeginSelectionTransform();
                    try { e.Pointer.Capture(window); } catch { }
                    e.Handled = true;
                    return;
                }

                // 在选区内部？拖动平移
                if (selection.Region.Contains(screenPt))
                {
                    mode = InteractionMode.MovingSelection;
                    dragStartRect = selection.Region;
                    BeginSelectionTransform();
                    try { e.Pointer.Capture(window); } catch { }
                    e.Handled = true;
                    return;
                }

                // 在选区外，按旧版逻辑先扩展当前选区；按住继续拖动可继续缩放。
                if (TryExpandSelectionByMaskClick(screenPt))
                {
                    BeginSelectionTransform();
                    try { e.Pointer.Capture(window); } catch { }
                    e.Handled = true;
                    return;
                }

                // 扩展未生效时才重新拖框（清空旧选区）
                hasManualSelection = false;
                activeAnnotationTool = AnnotationTool.None;
                drawingStroke = false;
                drawingArrow = false;
                drawingRectangle = false;
                drawingMosaic = false;
                drawingSpotlight = false;
                currentStrokePoints.Clear();
                window.HideColorPickerPreview();
                window.SetActiveTool(null);
                annotations.Clear();
                captureHistory.Clear();
                selection.Clear();
                StopAutoHoverAnimation();
                mode = InteractionMode.DraggingNew;
                try { e.Pointer.Capture(window); } catch { }
                e.Handled = true;
                return;
            }

            // 无手动选区时：开始拖框；保存当前高亮为单击候选，并立即清除隐藏高亮以提供灵敏的框选体验。
            pressedHighlight = currentHighlight;
            currentHighlight = null;
            StopAutoHoverAnimation();

            mode = InteractionMode.DraggingNew;
            try { e.Pointer.Capture(window); } catch { }
            e.Handled = true;
        });

        // (e) PointerReleased → 结束本次手势
        window.PointerReleased += (_, e) =>
        {
            if (e.Handled) return;
            var releaseDip = e.GetPosition(window);

            bool isInteracting = mode != InteractionMode.Idle
                || drawingStroke
                || drawingArrow
                || drawingRectangle
                || drawingMosaic
                || drawingSpotlight
                || movingBlurShape
                || resizingBlurShape
                || movingSpotlightShape
                || pendingMoveSpotlightShape
                || resizingSpotlightShape
                || adjustingSpotlightRadiusShape
                || movingArrowShape
                || movingRectangleShape
                || movingCounterShape
                || resizingRectangleShape
                || window.IsDraggingText
                || window.IsRotatingText;

            if (!isInteracting && window.IsOverlayPoint(releaseDip)) return;
            var pp = e.GetCurrentPoint(window);
            if (pp.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
            {
                e.Handled = true;
                CancelCapture();
                return;
            }

            if (!isInteracting && window.IsOverlayHit(e.Source)) return;

            // 仅左键释放
            if (pp.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;

            if (activeAnnotationTool != AnnotationTool.None)
            {
                var dip = e.GetPosition(window);
                var screenPt = window.WindowDipToScreenPoint(dip);

                if (activeAnnotationTool == AnnotationTool.ColorPicker)
                {
                    e.Handled = true;
                    return;
                }

                if (activeAnnotationTool == AnnotationTool.Brush && drawingStroke)
                {
                    FinishStroke();
                }
                else if (activeAnnotationTool == AnnotationTool.Arrow && drawingArrow)
                {
                    FinishArrow(new PointF(screenPt.X, screenPt.Y));
                }
                else if (activeAnnotationTool == AnnotationTool.Rectangle && drawingRectangle)
                {
                    FinishRectangle(new PointF(screenPt.X, screenPt.Y));
                }
                else if (activeAnnotationTool == AnnotationTool.Mosaic && drawingMosaic)
                {
                    FinishMosaic(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
                else if (activeAnnotationTool == AnnotationTool.Spotlight && drawingSpotlight)
                {
                    FinishSpotlight(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }

                if (movingBlurShape || resizingBlurShape)
                {
                    if (window.Annotation.PreviewShape is BlurShape previewBlur && GetSelectedBlurShape() is BlurShape currentBlur)
                    {
                        ReplaceSelectedBlurShape(currentBlur.WithRect(previewBlur.Rect));
                    }
                    window.Annotation.ClearPreviewShape();
                    movingBlurShape = false;
                    resizingBlurShape = false;
                    blurResizeDirection = HandleDirection.None;
                    try { e.Pointer.Capture(null); } catch { }
                }

                if (pendingMoveSpotlightShape || movingSpotlightShape || resizingSpotlightShape || adjustingSpotlightRadiusShape)
                {
                    if (window.Annotation.PreviewShape is SpotlightShape previewSpotlight && GetSelectedSpotlightShape() is SpotlightShape currentSpotlight)
                    {
                        ReplaceSelectedSpotlightShape(new SpotlightShape(
                            ClampSpotlightRect(previewSpotlight.Rect),
                            currentSpotlight.Darkness,
                            currentSpotlight.ShapeKind,
                            currentSpotlight.StrokeColor,
                            currentSpotlight.StrokeThickness,
                            previewSpotlight.CornerRadius,
                            currentSpotlight.Id));
                    }
                    window.Annotation.ClearPreviewShape();
                    pendingMoveSpotlightShape = false;
                    movingSpotlightShape = false;
                    resizingSpotlightShape = false;
                    adjustingSpotlightRadiusShape = false;
                            spotlightResizeDirection = HandleDirection.None;
                    spotlightRadiusHandleTag = string.Empty;
                    try { e.Pointer.Capture(null); } catch { }
                }

                // 释放通过Ctrl键选择的其他形状
                if (movingArrowShape)
                {
                    movingArrowShape = false;
                    arrowHandleType = HandleDirection.None;
                    try { e.Pointer.Capture(null); } catch { }
                }

                if (movingRectangleShape || resizingRectangleShape)
                {
                    movingRectangleShape = false;
                    resizingRectangleShape = false;
                    rectangleResizeDirection = HandleDirection.None;
                    try { e.Pointer.Capture(null); } catch { }
                }

                if (movingCounterShape)
                {
                    movingCounterShape = false;
                    try { e.Pointer.Capture(null); } catch { }
                }

                e.Handled = true;
                return;
            }

            bool endedTransform = mode == InteractionMode.MovingSelection || mode == InteractionMode.ResizingHandle || pendingExpandResize;
            switch (mode)
            {
                case InteractionMode.DraggingNew:
                    if (dragMovedPastThreshold && selection.HasSelection)
                    {
                        // 完成拖框，进入手动选区状态
                        hasManualSelection = true;
                        UpdateSelectionVisual(selection.Region, true);
                        AppLogger.Info("CaptureSession.PointerReleased: 手动选区完成，Region=" + selection.Region);
                    }
                    else
                    {
                        // 没拖动 → 视作单击；若自动检测有高亮 → 将高亮框设为当前选区并显示控制条
                        if (pressedHighlight.HasValue)
                        {
                            hasManualSelection = true;
                            selection.SetRegion(pressedHighlight.Value);
                            StopAutoHoverAnimation();
                            UpdateSelectionVisual(selection.Region, true);
                            AppLogger.Info("CaptureSession.PointerReleased: 点击高亮元素完成选区，Region=" + selection.Region);
                            e.Handled = true;
                        }
                        else
                        {
                            // 否则什么都不做。重新启动自动检测
                            try { autoDetect.IsPaused = false; } catch { }
                        }
                    }
                    pressedHighlight = null;
                    break;

                case InteractionMode.MovingSelection:
                case InteractionMode.ResizingHandle:
                    // 拖动平移/缩放结束时再一次性提交状态，拖动中只更新视觉层。
                    if (dragMovedPastThreshold && liveTransformRect.Width > 0 && liveTransformRect.Height > 0)
                    {
                        selection.SetRegion(liveTransformRect);
                    }
                    break;
            }

            pendingExpandResize = false;
            pendingExpandResizeDirection = HandleDirection.None;
            mode = InteractionMode.Idle;
            resizeDirection = HandleDirection.None;
            try { e.Pointer.Capture(null); } catch { }
            if (endedTransform) EndSelectionTransform();
            e.Handled = true;
        };

        // (f) 滚轮缩放选区：对齐旧版，每格四边各扩/缩 1px。
        window.PointerWheelChanged += (_, e) =>
        {
            if (window.IsOverlayHit(e.Source)) return;
            if (!hasManualSelection || !selection.HasSelection) return;
            if (mode != InteractionMode.Idle) return;
            if (longScrollStarting) return;

            if (activeAnnotationTool == AnnotationTool.Mosaic && GetSelectedBlurShape() is BlurShape selectedBlur)
            {
                int deltaLevel = e.Delta.Y > 0 ? 1 : -1;
                int nextLevel = BlurIntensityMapper.ClampUiLevel(selectedBlur.UiLevel + deltaLevel);
                if (nextLevel != selectedBlur.UiLevel)
                {
                    captureHistory.CommitSnapshot();
                    ReplaceSelectedBlurShape(selectedBlur.WithUiLevel(nextLevel));
                    window.SyncBlurToolState(selectedBlur.Mode, nextLevel);
                    AppLogger.Info("CaptureSession.BlurWheel: 调整图层强度，id=" + selectedBlur.Id + ", level=" + nextLevel + ", mode=" + selectedBlur.Mode);
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && annotations.Shapes.OfType<SpotlightShape>().Any())
            {
                double step = 5.0;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 10.0;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) step = 2.0;
                double nextPercent = window.SelectedSpotlightDarkness * 100.0 + (e.Delta.Y > 0 ? step : -step);
                captureHistory.CommitSnapshot();
                window.SetSpotlightDarknessPercent(nextPercent);
                AppLogger.Info("CaptureSession.SpotlightWheel: 调整暗幕透明度，percent=" + Math.Round(window.SelectedSpotlightDarkness * 100.0));
                e.Handled = true;
                return;
            }

            // ===== 选中形状的滚轮调整（对齐旧版 OnShapeLayerMouseWheel，优先于工具参数）=====
            
            // 选中箭头：滚轮调整整体缩放（对齐旧版 ChangeArrowScale，factor=1.1）
            if (selectedArrowShapeId.HasValue && !drawingArrow)
            {
                var arrow = annotations.Shapes.OfType<ArrowShape>().FirstOrDefault(x => x.Id == selectedArrowShapeId.Value);
                if (arrow != null)
                {
                    float factor = e.Delta.Y > 0 ? 1.1f : (1.0f / 1.1f);
                    float nextScale = Math.Clamp(arrow.Scale * factor, 0.2f, 4.0f);
                    currentArrowScale = nextScale;
                    captureHistory.CommitSnapshot();
                    var updatedArrow = new ArrowShape(arrow.Start, arrow.End, arrow.Color, arrow.Thickness, arrow.Style, nextScale, arrow.Id);
                    annotations.ReplaceShape(updatedArrow);
                    window.Annotation.InvalidateVisual();
                    AppLogger.Info($"CaptureSession.ArrowScaleWheel: 调整箭头缩放={nextScale:F2}, id={arrow.Id}");
                }
                e.Handled = true;
                return;
            }
            
            // 选中序号：滚轮调整大小
            if (selectedCounterShapeId.HasValue)
            {
                var counter = annotations.Shapes.OfType<CounterShape>().FirstOrDefault(x => x.Id == selectedCounterShapeId.Value);
                if (counter != null)
                {
                    float step = 2.0f;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 8.0f;
                    float nextRadius = Math.Clamp(counter.Radius + (e.Delta.Y > 0 ? step : -step), 8.0f, 80.0f);
                    captureHistory.CommitSnapshot();
                    var updatedCounter = new CounterShape(counter.Center, counter.Number, nextRadius, counter.FillColor, counter.TextColor, counter.Id);
                    annotations.ReplaceShape(updatedCounter);
                    window.Annotation.InvalidateVisual();
                    window.SyncCounterRadiusSlider(nextRadius);
                    AppLogger.Info($"CaptureSession.CounterWheel: 调整序号大小={nextRadius}, id={counter.Id}");
                }
                e.Handled = true;
                return;
            }
            
            // 选中矩形：滚轮调整描边粗细
            if (selectedRectangleShapeId.HasValue && !drawingRectangle)
            {
                var rect = annotations.Shapes.OfType<RectangleShape>().FirstOrDefault(x => x.Id == selectedRectangleShapeId.Value);
                if (rect != null)
                {
                    int step = 1;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 5;
                    float nextThickness = Math.Clamp(rect.StrokeThickness + (e.Delta.Y > 0 ? step : -step), 1.0f, 60.0f);
                    captureHistory.CommitSnapshot();
                    var updatedRect = new RectangleShape(rect.Rect, rect.StrokeColor, nextThickness, rect.ShapeKind, rect.LineStyle, rect.Id);
                    annotations.ReplaceShape(updatedRect);
                    window.Annotation.InvalidateVisual();
                    AppLogger.Info($"CaptureSession.RectangleWheel: 调整矩形粗细={nextThickness}, id={rect.Id}");
                }
                e.Handled = true;
                return;
            }
            
            // 选中画笔：滚轮调整粗细
            if (selectedStrokeShapeId.HasValue && !drawingStroke)
            {
                var stroke = annotations.Shapes.OfType<StrokeShape>().FirstOrDefault(x => x.Id == selectedStrokeShapeId.Value);
                if (stroke != null)
                {
                    int step = 1;
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 5;
                    float nextThickness = Math.Clamp(stroke.Thickness + (e.Delta.Y > 0 ? step : -step), 1.0f, 60.0f);
                    captureHistory.CommitSnapshot();
                    var updatedStroke = new StrokeShape(stroke.Points, stroke.Color, nextThickness, stroke.LineStyle, stroke.Id);
                    annotations.ReplaceShape(updatedStroke);
                    window.Annotation.InvalidateVisual();
                    AppLogger.Info($"CaptureSession.StrokeWheel: 调整画笔粗细={nextThickness}, id={stroke.Id}");
                }
                e.Handled = true;
                return;
            }
            
            // 画笔/箭头/矩形工具：滚轮调整工具粗细（无选中形状时）
            if ((activeAnnotationTool == AnnotationTool.Brush || activeAnnotationTool == AnnotationTool.Arrow || activeAnnotationTool == AnnotationTool.Rectangle) 
                && !drawingStroke && !drawingArrow && !drawingRectangle)
            {
                int step = 1;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 5;
                double nextValue = Math.Clamp(window.SliderStrokeThickness.Value + (e.Delta.Y > 0 ? step : -step), 
                    window.SliderStrokeThickness.Minimum, window.SliderStrokeThickness.Maximum);
                window.SliderStrokeThickness.Value = nextValue;
                AppLogger.Info($"CaptureSession.ThicknessWheel: 调整粗细={nextValue}, tool={activeAnnotationTool}");
                e.Handled = true;
                return;
            }
            
            // 文字工具：滚轮调整字体大小
            if (activeAnnotationTool == AnnotationTool.Text && !drawingStroke && !drawingArrow && !drawingRectangle)
            {
                double step = 2;
                if (window.CurrentTextSize >= 48) step = 8;
                else if (window.CurrentTextSize >= 24) step = 4;
                double nextSize = Math.Clamp(window.CurrentTextSize + (e.Delta.Y > 0 ? step : -step), 8.0, 200.0);
                if (Math.Abs(nextSize - window.CurrentTextSize) > 0.1)
                {
                    captureHistory.CommitSnapshot();
                    window.CurrentTextSize = nextSize;
                    AppLogger.Info($"CaptureSession.TextWheel: 调整文字字号={nextSize:0}, selectedText={(window.SelectedText != null)}, deltaY={e.Delta.Y}");
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool != AnnotationTool.None || drawingStroke || drawingArrow || drawingRectangle || drawingMosaic || drawingSpotlight) return;

            int delta = e.Delta.Y > 0 ? 1 : -1;
            Rectangle next = InflateSelection(selection.Region, delta);
            if (next == selection.Region) return;
            selection.SetRegion(next);
            UpdateSelectionVisual(selection.Region, true);
            e.Handled = true;
        };

        // (g) 键盘
        window.KeyDown += (_, e) =>
        {
            // 跟踪 Ctrl 键状态
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                _isCtrlPressed = true;
            }

            // 文本编辑兜底：后续新建文本若焦点短暂落在窗口而不是 TextBox，Ctrl+Enter 仍应提交编辑，不能走全局 Enter 确认截图。
            if ((e.Key == Key.Enter || e.Key == Key.Return) && e.KeyModifiers.HasFlag(KeyModifiers.Control) && window.HasActiveTextEditing)
            {
                AppLogger.Info("CaptureSession.TextCtrlEnter: 会话键盘兜底提交文本编辑");
                window.EndActiveTextEditing("CaptureSessionKeyDown");
                e.Handled = true;
                return;
            }

            if (window.HasActiveTextEditing)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CancelCapture();
                }
                return;
            }

            // 清除选中状态（点击空白区域或开始新操作时自动清除）

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z)
            {
                e.Handled = true;
                captureHistory.Undo();
                return;
            }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y)
            {
                e.Handled = true;
                captureHistory.Redo();
                return;
            }
            if (e.Key == Key.B)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Brush);
                return;
            }
            if (e.Key == Key.A)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Arrow);
                return;
            }
            if (e.Key == Key.R)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Rectangle);
                return;
            }
            if (e.Key == Key.T)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Text);
                return;
            }
            if (e.Key == Key.M)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Mosaic);
                return;
            }
            if (e.Key == Key.S)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Spotlight);
                return;
            }
            if (e.Key == Key.C)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.Counter);
                return;
            }
            if (e.Key == Key.P)
            {
                e.Handled = true;
                SetAnnotationTool(AnnotationTool.ColorPicker);
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelCapture();
                return;
            }
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                // 删除当前选中的任何形状
                bool deleted = false;
                if (selectedSpotlightShapeId.HasValue)
                {
                    DeleteSelectedSpotlightShape();
                    deleted = true;
                }
                else if (selectedBlurShapeId.HasValue)
                {
                    DeleteSelectedBlurShape();
                    deleted = true;
                }
                else if (selectedStrokeShapeId.HasValue)
                {
                    DeleteSelectedStrokeShape();
                    deleted = true;
                }
                else if (selectedArrowShapeId.HasValue)
                {
                    DeleteSelectedArrowShape();
                    deleted = true;
                }
                else if (selectedRectangleShapeId.HasValue)
                {
                    DeleteSelectedRectangleShape();
                    deleted = true;
                }
                else if (selectedCounterShapeId.HasValue)
                {
                    DeleteSelectedCounterShape();
                    deleted = true;
                }
                else if (window.SelectedText != null)
                {
                    captureHistory.CommitSnapshot();
                    deleted = window.DeleteSelectedText();
                }
                
                if (deleted)
                {
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                e.Handled = true;
                ConfirmCurrentSelection();
            }
        };

        window.KeyUp += (_, e) =>
        {
            // 跟踪Ctrl键状态
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                _isCtrlPressed = false;
                // Ctrl键释放时清除所有选择
                ClearAllSelections();
                window.Annotation.InvalidateVisual();
            }
        };

        // (g) 控制条按钮
        window.BrushRequested += (_, _) => SetAnnotationTool(AnnotationTool.Brush);
        window.ArrowRequested += (_, _) => SetAnnotationTool(AnnotationTool.Arrow);
        window.RectangleRequested += (_, _) => SetAnnotationTool(AnnotationTool.Rectangle);
        window.TextRequested += (_, _) => SetAnnotationTool(AnnotationTool.Text);
        window.MosaicRequested += (_, _) => SetAnnotationTool(AnnotationTool.Mosaic);
        window.ManualMosaicRequested += (_, _) =>
        {
            autoMosaicHighlights.Clear();
            window.Annotation.ClearAutoMosaicHighlights();
        };
        window.AutoMosaicRequested += (_, _) => RunAutoMosaicRecognition();
        window.BlurSettingsChanged += (_, _) =>
        {
            BlurShape? selected = GetSelectedBlurShape();
            if (selected == null) return;
            captureHistory.CommitSnapshot();
            ReplaceSelectedBlurShape(new BlurShape(selected.Rect, window.SelectedBlurMode, window.SelectedBlurUiLevel, selected.Id));
        };
        window.SpotlightRequested += (_, _) => SetAnnotationTool(AnnotationTool.Spotlight);
        window.SpotlightSettingsChanged += (_, _) => ApplySpotlightStyleToSelection();
        window.AnnotationCommonSettingsChanged += (_, _) => ApplyCommonSettingsToSelection();
        window.CounterRequested += (_, _) => SetAnnotationTool(AnnotationTool.Counter);
        window.ColorPickerRequested += (_, _) => SetAnnotationTool(AnnotationTool.ColorPicker);
        window.UndoRequested += (_, _) => captureHistory.Undo();
        window.RedoRequested += (_, _) => captureHistory.Redo();
        window.LongScrollRequested += (_, _) => StartLongScrollFromCurrentSelection();
        window.OcrRequested += (_, _) =>
        {
            if (!selection.HasSelection || selection.Region.Width <= 0 || selection.Region.Height <= 0)
            {
                return;
            }

            Bitmap? cropped = null;
            try
            {
                cropped = CreateCroppedResultBitmap(frame, selection.Region, annotations, window.TextLayers, "文字识别");
                if (cropped == null) return;

                // 立即关闭截图窗口，结束截图会话
                tcs.TrySetResult(null);

                // 在后台线程异步执行 OCR 识别
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_ocrService == null)
                        {
                            _ocrService = new ScreenCaptureTool.Core.Capture.PaddleOcrService();
                        }

                        string text = await _ocrService.ExtractTextAsync(cropped);

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            ClipboardService.SetText(text);
                            // 在屏幕中上方显示悬浮提示
                            Dispatcher.UIThread.Post(() =>
                            {
                                ScreenCaptureTool.Windows.StickerToastWindow.ShowAtTop("文本复制成功");
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("后台 OCR 识别异常", ex);
                    }
                    finally
                    {
                        try { cropped?.Dispose(); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("CaptureSession.OcrRequested 初始化异常", ex);
                try { cropped?.Dispose(); } catch { }
            }
        };
        window.StickerRequested += (_, _) => CreateStickerFromCurrentSelection();
        window.ConfirmRequested += (_, _) => ConfirmCurrentSelection();
        window.SaveRequested += (_, _) => SaveCurrentSelection();
        window.CancelRequested += (_, _) => CancelCapture();
        window.ClearCurrentToolRequested += (_, _) => ClearCurrentToolAnnotations();
        window.ClearAllAnnotationsRequested += (_, _) => ClearAllAnnotations();

        // (h) 兜底：窗口被外部关掉
        window.Closed += (_, _) => tcs.TrySetResult(null);
    }

    // -------------------- 输出 --------------------

    private bool CopyResultToClipboard(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations, IReadOnlyList<TextLayerInfo>? textLayers)
    {
        Bitmap? cropped = null;
        try
        {
            cropped = CreateCroppedResultBitmap(frame, screenRect, annotations, textLayers, "复制剪贴板");
            if (cropped == null) return false;

            ClipboardService.SetImage(cropped);
            // 记住截图区域，供 F3 快速贴图还原位置（剪贴板裸图不带位置信息）。
            LastCaptureMemory.Record(screenRect, cropped.Size, frame?.DpiScale ?? 1.0);
            AppLogger.Info("CaptureSession.CopyResultToClipboard: 已复制截图到剪贴板");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureSession.CopyResultToClipboard 异常", ex);
            throw;
        }
        finally
        {
            try { cropped?.Dispose(); } catch { }
        }
    }

    private string? SaveResult(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations, IReadOnlyList<TextLayerInfo>? textLayers)
    {
        Bitmap? cropped = null;
        try
        {
            cropped = CreateCroppedResultBitmap(frame, screenRect, annotations, textLayers, "保存");
            if (cropped == null) return null;

            string fileName = ResolveFileName(_settings);
            string path = Path.Combine(_settings.SaveDirectory ?? string.Empty, fileName);

            // 构造截图元数据：截图区域(物理像素) + DPI + 捕获时间，供 PNG tEXt chunk 写入，
            // 将来「从文件打开为贴图」读取后可恢复原尺寸与原位置。
            CaptureMetadata? metadata = null;
            if (frame != null)
            {
                metadata = new CaptureMetadata(
                    screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
                    frame.DpiScale, frame.CapturedAt);
            }

            ImageExportService.Save(cropped, path, metadata);
            AppLogger.Info("CaptureSession: 保存成功 → " + path + (metadata != null ? "（含元数据）" : ""));

            if (_settings.CopyToClipboardAfterSave)
            {
                try
                {
                    ClipboardService.SetImage(cropped);
                    AppLogger.Info("CaptureSession.SaveResult: 已复制截图到剪贴板");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureSession: 复制剪贴板失败：" + ex.Message);
                }
            }

            return path;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureSession.SaveResult 异常", ex);
            throw;
        }
        finally
        {
            try { cropped?.Dispose(); } catch { }
        }
    }

    private CaptureSessionResult? CreateSticker(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations, IReadOnlyList<TextLayerInfo>? textLayers)
    {
        Bitmap? cropped = null;
        try
        {
            cropped = CreateCroppedResultBitmap(frame, screenRect, annotations, textLayers, "贴图");
            if (cropped == null) return null;

            var sticker = new StickerWindow(cropped);
            // screenRect 是屏幕物理像素坐标；SetStickerScreenPosition 让 Image 左上角对齐截图区域左上角，
            // 窗口本身会左移上移阴影边距以容纳阴影。
            sticker.SetStickerScreenPosition(new Avalonia.PixelPoint(screenRect.X, screenRect.Y));
            sticker.Show();
            sticker.Activate();
            StickerFocusManager.SetFocus(sticker); // 转化的贴图自动获得焦点（阴影变蓝）
            AppLogger.Info("CaptureSession: 已创建贴图，原位 screenRect=" + screenRect);
            return CaptureSessionResult.StickerCreated();
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureSession.CreateSticker 异常", ex);
            throw;
        }
        finally
        {
            try { cropped?.Dispose(); } catch { }
        }
    }

    private static Bitmap? CreateCroppedResultBitmap(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations,
        IReadOnlyList<TextLayerInfo>? textLayers, string operationName)
    {
        if (frame == null) return null;

        Rectangle bmpRect = new Rectangle(
            screenRect.X - frame.VirtualScreenRect.X,
            screenRect.Y - frame.VirtualScreenRect.Y,
            screenRect.Width,
            screenRect.Height);

        bmpRect.Intersect(new Rectangle(0, 0, frame.FullScreen.Width, frame.FullScreen.Height));
        if (bmpRect.Width <= 0 || bmpRect.Height <= 0)
        {
            AppLogger.Warn("CaptureSession.CreateCroppedResultBitmap: 选区与位图无交集，operation=" + operationName);
            return null;
        }

        Bitmap cropped = frame.FullScreen.Clone(bmpRect, PixelFormat.Format32bppArgb);
        RenderAnnotationsToBitmap(cropped, screenRect, frame.VirtualScreenRect, annotations);
        RenderTextLayersToBitmap(cropped, screenRect, textLayers);
        return cropped;
    }

    private static void RenderAnnotationsToBitmap(Bitmap bitmap, Rectangle selectedScreenRect, Rectangle virtualScreenRect, AnnotationDocument annotations)
    {
        if (bitmap == null || annotations == null || annotations.Count == 0) return;

        // 先处理聚光灯和马赛克/模糊类标注，再把线条/文字画在上方。
        var spotlights = new List<SpotlightShape>();
        foreach (AnnotationShape shape in annotations.Shapes)
        {
            if (shape is SpotlightShape spotlight)
            {
                spotlights.Add(spotlight);
            }
        }
        ApplySpotlightShapes(bitmap, selectedScreenRect, spotlights);

        foreach (AnnotationShape shape in annotations.Shapes)
        {
            if (shape is BlurShape blur)
            {
                ApplyBlurShape(bitmap, selectedScreenRect, virtualScreenRect, blur);
            }
        }

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        foreach (AnnotationShape shape in annotations.Shapes)
        {
            if (shape is StrokeShape stroke)
            {
                RenderStroke(g, selectedScreenRect, stroke);
            }
            else if (shape is ArrowShape arrow)
            {
                RenderArrow(g, selectedScreenRect, arrow);
            }
            else if (shape is RectangleShape rect)
            {
                RenderAnnotationRectangle(g, selectedScreenRect, rect);
            }
            else if (shape is CounterShape counter)
            {
                RenderCounter(g, selectedScreenRect, counter);
            }
        }
    }

    /// <summary>将 OverlayCanvas 上的文本图层渲染到输出位图。</summary>
    private static void RenderTextLayersToBitmap(Bitmap bitmap, Rectangle selectedScreenRect,
        IReadOnlyList<TextLayerInfo>? textLayers)
    {
        if (textLayers == null || textLayers.Count == 0) return;

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        float scaleX = selectedScreenRect.Width > 0 ? bitmap.Width / (float)selectedScreenRect.Width : 1.0f;
        float scaleY = selectedScreenRect.Height > 0 ? bitmap.Height / (float)selectedScreenRect.Height : 1.0f;

        foreach (var info in textLayers)
        {
            if (info?.TextBox == null || info.Root == null) continue;
            string text = info.TextBox.Text ?? string.Empty;
            if (string.IsNullOrEmpty(text)) continue;

            double left = Canvas.GetLeft(info.Root);
            double top = Canvas.GetTop(info.Root);
            float x = (float)((left - selectedScreenRect.X) * scaleX);
            float y = (float)((top - selectedScreenRect.Y) * scaleY);
            float w = (float)Math.Max(1.0, info.Root.Bounds.Width * scaleX);
            float h = (float)Math.Max(1.0, info.Root.Bounds.Height * scaleY);

            var state = g.Save();
            try
            {
                if (Math.Abs(info.RotationAngle) > 0.01)
                {
                    g.TranslateTransform(x + w / 2.0f, y + h / 2.0f);
                    g.RotateTransform((float)info.RotationAngle);
                    g.TranslateTransform(-(x + w / 2.0f), -(y + h / 2.0f));
                }

                if (info.HasBackground)
                {
                    int alpha = Math.Clamp((int)Math.Round(info.FillOpacity / 100.0 * 255), 0, 255);
                    using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(alpha, info.FillColor.R, info.FillColor.G, info.FillColor.B));
                    float cr = (float)(info.FillCornerRadius * Math.Min(scaleX, scaleY));
                    if (cr > 0)
                    {
                        using var gp = CreateRoundedRectPath(x, y, w, h, cr);
                        g.FillPath(bgBrush, gp);
                    }
                    else
                    {
                        g.FillRectangle(bgBrush, x, y, w, h);
                    }
                }

                var fontStyle = info.IsItalic ? System.Drawing.FontStyle.Italic : System.Drawing.FontStyle.Regular;
                if (info.IsBold) fontStyle |= System.Drawing.FontStyle.Bold;
                using var font = new Font(info.FontFamily ?? "Microsoft YaHei UI", (float)(info.FontSize * Math.Min(scaleX, scaleY)), fontStyle, GraphicsUnit.Pixel);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near,
                    Trimming = StringTrimming.None,
                    FormatFlags = StringFormatFlags.LineLimit,
                };

                float pad = (float)((info.HasBackground ? info.FillPadding : 0) * Math.Min(scaleX, scaleY));
                float strokePad = info.HasStroke ? (float)(info.StrokeThickness * Math.Min(scaleX, scaleY)) : 0;
                RectangleF layout = new RectangleF(x + pad + strokePad, y + pad + strokePad, Math.Max(1, w - 2 * (pad + strokePad)), Math.Max(1, h - 2 * (pad + strokePad)));

                using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(info.Color.A, info.Color.R, info.Color.G, info.Color.B));
                using var textPath = new GraphicsPath();
                float lineHeight = (float)(((info.TextBox.LineHeight > 0) ? info.TextBox.LineHeight : info.FontSize * 1.35) * Math.Min(scaleX, scaleY));
                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    var lineLayout = new RectangleF(layout.X, layout.Y + i * lineHeight, layout.Width, Math.Max(1.0f, lineHeight));
                    textPath.AddString(lines[i], font.FontFamily, (int)fontStyle, font.Size, lineLayout, format);
                }

                if (info.HasStroke)
                {
                    using var strokePen = new Pen(
                        System.Drawing.Color.FromArgb(info.StrokeColor.A, info.StrokeColor.R, info.StrokeColor.G, info.StrokeColor.B),
                        GetTextStrokeRenderThickness((float)(info.StrokeThickness * Math.Min(scaleX, scaleY)), font.Size))
                    {
                        LineJoin = LineJoin.Round,
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                    };
                    g.DrawPath(strokePen, textPath);
                }

                g.FillPath(textBrush, textPath);
            }
            finally
            {
                g.Restore(state);
            }
        }
    }

    private static float GetTextStrokeRenderThickness(float strokeThickness, float fontSize)
    {
        if (strokeThickness <= 0.0f) return 0.0f;

        float maxByFont = Math.Max(1.0f, fontSize * 0.22f);
        float softened = strokeThickness <= 1.0f
            ? strokeThickness * 1.45f
            : 1.45f + (strokeThickness - 1.0f) * 1.15f;
        return Math.Clamp(softened, 0.75f, maxByFont);
    }

    private static GraphicsPath CreateRoundedRectPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        if (r <= 0) { path.AddRectangle(new RectangleF(x, y, w, h)); return path; }
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void RenderStroke(Graphics g, Rectangle selectedScreenRect, StrokeShape stroke)
    {
        if (stroke.Points.Count < 2) return;

        PointF[] localPoints = new PointF[stroke.Points.Count];
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            PointF p = stroke.Points[i];
            localPoints[i] = new PointF(
                p.X - selectedScreenRect.X,
                p.Y - selectedScreenRect.Y);
        }

        using var pen = new Pen(stroke.Color, Math.Max(1.0f, stroke.Thickness))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        if (stroke.LineStyle == AnnotationLineStyle.DashLarge)
        {
            DrawDashedPolyline(g, pen, localPoints, Math.Max(10.0f, stroke.Thickness * 3.2f), Math.Max(7.0f, stroke.Thickness * 2.2f));
            return;
        }

        g.DrawLines(pen, localPoints);
    }

    private static void DrawDashedPolyline(Graphics g, Pen pen, PointF[] points, float dashLength, float gapLength)
    {
        if (points.Length < 2) return;

        bool drawing = true;
        float remaining = dashLength;
        PointF current = points[0];

        for (int i = 1; i < points.Length; i++)
        {
            PointF target = points[i];
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float segmentLength = (float)Math.Sqrt(dx * dx + dy * dy);
            if (segmentLength <= 0.01f)
            {
                current = target;
                continue;
            }

            float ux = dx / segmentLength;
            float uy = dy / segmentLength;
            float consumed = 0.0f;
            PointF segmentStart = current;

            while (consumed < segmentLength)
            {
                float step = Math.Min(remaining, segmentLength - consumed);
                PointF segmentEnd = new PointF(segmentStart.X + ux * step, segmentStart.Y + uy * step);
                if (drawing)
                {
                    g.DrawLine(pen, segmentStart, segmentEnd);
                }

                consumed += step;
                segmentStart = segmentEnd;
                remaining -= step;

                if (remaining <= 0.01f)
                {
                    drawing = !drawing;
                    remaining = drawing ? dashLength : gapLength;
                }
            }

            current = target;
        }
    }

    private static void RenderArrow(Graphics g, Rectangle selectedScreenRect, ArrowShape arrow)
    {
        PointF start = ToLocalPoint(arrow.Start, selectedScreenRect);
        PointF end = ToLocalPoint(arrow.End, selectedScreenRect);
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0) return;

        float thickness = Math.Clamp(Math.Max(1.0f, arrow.Thickness), 1.0f, 45.0f);
        (GraphicsPath Shaft, GraphicsPath Head) paths = CreateLegacyArrowPaths(start, end, thickness, arrow.Style);
        using (paths.Shaft)
        using (paths.Head)
        using (var brush = new SolidBrush(arrow.Color))
        {
            g.FillPath(brush, paths.Shaft);
            g.FillPath(brush, paths.Head);
        }
    }

    private static void RenderAnnotationRectangle(Graphics g, Rectangle selectedScreenRect, RectangleShape shape)
    {
        RectangleF r = shape.Rect;
        RectangleF local = RectangleF.FromLTRB(
            r.Left - selectedScreenRect.X,
            r.Top - selectedScreenRect.Y,
            r.Right - selectedScreenRect.X,
            r.Bottom - selectedScreenRect.Y);
        if (local.Width <= 0 || local.Height <= 0) return;

        using var pen = new Pen(shape.StrokeColor, Math.Max(1.0f, shape.StrokeThickness))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        if (shape.LineStyle == AnnotationLineStyle.DashLarge)
        {
            pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            pen.DashCap = DashCap.Round;
        }

        if (shape.ShapeKind == ShapeAnnotationKind.Ellipse)
        {
            g.DrawEllipse(pen, local.X, local.Y, local.Width, local.Height);
        }
        else
        {
            g.DrawRectangle(pen, local.X, local.Y, local.Width, local.Height);
        }
    }

    private static (GraphicsPath Shaft, GraphicsPath Head) CreateLegacyArrowPaths(PointF start, PointF end, float thickness, ArrowAnnotationStyle style)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0f)
        {
            return (new GraphicsPath(), new GraphicsPath());
        }

        float ux = dx / len;
        float uy = dy / len;
        float nx = -uy;
        float ny = ux;

        // 旧版 WPF 模板：箭身 0..100 横向拉伸，模板高度 50；头部固定 50×50，尖端落终点。
        float visualScale = 1.0f;
        float templateHeight = 50.0f * visualScale;
        float headSize = 50.0f * visualScale;
        float headOverlap = 25.0f * visualScale;
        float shaftSlotWidth = Math.Max(1.0f, len - headOverlap);
        float shaftXScale = shaftSlotWidth / 100.0f;
        float shaftYScale = templateHeight / 100.0f;
        float headX = len - headSize;
        float headScale = headSize / 100.0f;
        float legacyThickness = Math.Clamp(thickness * 5.0f, 1.0f, 45.0f);
        float ctrlYTop = 50.0f - legacyThickness;
        float ctrlYBottom = 50.0f + legacyThickness;

        PointF T(float x, float y)
        {
            float localX = x * shaftXScale;
            float localY = (y - 50.0f) * shaftYScale;
            return new PointF(start.X + ux * localX + nx * localY, start.Y + uy * localX + ny * localY);
        }

        PointF H(float x, float y)
        {
            float localX = headX + x * headScale;
            float localY = (y - 50.0f) * headScale;
            return new PointF(start.X + ux * localX + nx * localY, start.Y + uy * localX + ny * localY);
        }

        var shaft = new GraphicsPath();
        if (style == ArrowAnnotationStyle.Sharp)
        {
            shaft.StartFigure();
            AddQuadraticBezier(shaft, T(0, 40), T(50, ctrlYTop), T(100, 35));
            shaft.AddLine(T(100, 35), T(100, 65));
            AddQuadraticBezier(shaft, T(100, 65), T(50, ctrlYBottom), T(0, 60));
            shaft.CloseFigure();
        }
        else
        {
            shaft.StartFigure();
            AddQuadraticBezier(shaft, T(0, 50), T(50, ctrlYTop), T(100, 35));
            shaft.AddLine(T(100, 35), T(100, 65));
            AddQuadraticBezier(shaft, T(100, 65), T(50, ctrlYBottom), T(0, 50));
            shaft.CloseFigure();
        }

        var head = new GraphicsPath();
        if (style == ArrowAnnotationStyle.Sharp)
        {
            head.AddPolygon(new[] { H(20, 20), H(100, 50), H(20, 80) });
        }
        else
        {
            head.AddPolygon(new[] { H(50, 50), H(20, 20), H(100, 50), H(20, 80) });
        }

        return (shaft, head);
    }

    private static void AddQuadraticBezier(GraphicsPath path, PointF start, PointF control, PointF end)
    {
        PointF c1 = new PointF(
            start.X + (control.X - start.X) * 2.0f / 3.0f,
            start.Y + (control.Y - start.Y) * 2.0f / 3.0f);
        PointF c2 = new PointF(
            end.X + (control.X - end.X) * 2.0f / 3.0f,
            end.Y + (control.Y - end.Y) * 2.0f / 3.0f);
        path.AddBezier(start, c1, c2, end);
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.0f)
        {
            path.AddRectangle(rect);
            return path;
        }

        float d = Math.Min(Math.Min(rect.Width, rect.Height), radius * 2.0f);
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void RenderCounter(Graphics g, Rectangle selectedScreenRect, CounterShape counter)
    {
        PointF center = ToLocalPoint(counter.Center, selectedScreenRect);
        float radius = Math.Max(8.0f, counter.Radius);
        var rect = new RectangleF(center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);

        using var fill = new SolidBrush(counter.FillColor);
        using var outline = new Pen(Color.White, 2.0f);
        using var textBrush = new SolidBrush(counter.TextColor);
        using var font = new Font(SystemFonts.DefaultFont.FontFamily, Math.Max(8.0f, radius), FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        g.FillEllipse(fill, rect);
        g.DrawEllipse(outline, rect);
        g.DrawString(counter.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), font, textBrush, rect, format);
    }

    private static void ApplySpotlightShapes(Bitmap bitmap, Rectangle selectedScreenRect, IReadOnlyList<SpotlightShape> shapes)
    {
        if (bitmap == null || shapes == null || shapes.Count == 0) return;

        int alpha = (int)Math.Round(Math.Clamp(shapes[0].Darkness, 0.0f, 1.0f) * 255.0f);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var overlay = new GraphicsPath();
        overlay.AddRectangle(new RectangleF(0, 0, bitmap.Width, bitmap.Height));
        using var holesRegion = new Region();
        holesRegion.MakeEmpty();
        bool hasHole = false;
        foreach (SpotlightShape shape in shapes)
        {
            using GraphicsPath? hole = CreateSpotlightLocalPath(shape, selectedScreenRect, bitmap.Width, bitmap.Height);
            if (hole == null || hole.PointCount == 0) continue;
            holesRegion.Union(hole);
            hasHole = true;
        }

        if (hasHole && alpha > 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            using var region = new Region(overlay);
            region.Exclude(holesRegion);
            g.FillRegion(brush, region);
        }

        SpotlightShape style = shapes[shapes.Count - 1];
        if (hasHole && style.StrokeThickness > 0.0f)
        {
            using var pen = new Pen(style.StrokeColor, Math.Max(1.0f, style.StrokeThickness))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            DrawSpotlightUnionOutline(g, pen, shapes, selectedScreenRect, bitmap.Width, bitmap.Height);
        }
    }

    private static GraphicsPath? CreateSpotlightLocalPath(SpotlightShape shape, Rectangle selectedScreenRect, int bitmapWidth, int bitmapHeight)
    {
        RectangleF local = RectangleF.FromLTRB(
            shape.Rect.Left - selectedScreenRect.X,
            shape.Rect.Top - selectedScreenRect.Y,
            shape.Rect.Right - selectedScreenRect.X,
            shape.Rect.Bottom - selectedScreenRect.Y);
        local.Intersect(new RectangleF(0, 0, bitmapWidth, bitmapHeight));
        if (local.Width <= 0 || local.Height <= 0) return null;

        var path = new GraphicsPath();
        if (shape.ShapeKind == SpotlightShapeKind.Ellipse)
        {
            path.AddEllipse(local);
        }
        else
        {
            using GraphicsPath rounded = CreateRoundedRectanglePath(local, Math.Max(0.0f, shape.CornerRadius));
            path.AddPath(rounded, false);
        }
        return path;
    }

    private static void DrawSpotlightUnionOutline(Graphics g, Pen pen, IReadOnlyList<SpotlightShape> shapes, Rectangle selectedScreenRect, int bitmapWidth, int bitmapHeight)
    {
        foreach (SpotlightShape shape in shapes)
        {
            using GraphicsPath? path = CreateSpotlightLocalPath(shape, selectedScreenRect, bitmapWidth, bitmapHeight);
            if (path == null || path.PointCount == 0) continue;
            g.DrawPath(pen, path);
        }
    }

    private static void ApplyBlurShape(Bitmap bitmap, Rectangle selectedScreenRect, Rectangle virtualScreenRect, BlurShape shape)
    {
        Rectangle local = Rectangle.Round(RectangleF.FromLTRB(
            shape.Rect.Left - selectedScreenRect.X,
            shape.Rect.Top - selectedScreenRect.Y,
            shape.Rect.Right - selectedScreenRect.X,
            shape.Rect.Bottom - selectedScreenRect.Y));
        local.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (local.Width <= 0 || local.Height <= 0) return;
        if (shape.UiLevel <= 0) return;

        if (shape.Mode == BlurMode.Mosaic)
        {
            ApplyScaledMosaicShape(bitmap, local, shape);
        }
        else
        {
            ApplyScaledBlurShape(bitmap, local, shape);
        }
    }

    private static void ApplyScaledMosaicShape(Bitmap bitmap, Rectangle local, BlurShape shape)
    {
        int scale = Math.Max(2, shape.MosaicBlockSize);
        ApplyScaledEffect(bitmap, local, scale, InterpolationMode.NearestNeighbor, InterpolationMode.NearestNeighbor, PixelOffsetMode.Half);
    }

    private static void ApplyScaledBlurShape(Bitmap bitmap, Rectangle local, BlurShape shape)
    {
        int scale = GetBlurPreviewScale(shape);
        ApplyScaledEffect(bitmap, local, scale, InterpolationMode.HighQualityBilinear, InterpolationMode.HighQualityBilinear, PixelOffsetMode.Half);
    }

    private static int GetBlurPreviewScale(BlurShape shape)
    {
        int radius = Math.Max(1, (int)Math.Round(shape.Intensity));
        return Math.Clamp(radius / 2, 4, 16);
    }

    private static void ApplyScaledEffect(Bitmap bitmap, Rectangle local, int scale, InterpolationMode downInterpolation, InterpolationMode upInterpolation, PixelOffsetMode pixelOffsetMode)
    {
        if (local.Width <= 0 || local.Height <= 0) return;

        int downW = Math.Max(1, (local.Width + scale - 1) / scale);
        int downH = Math.Max(1, (local.Height + scale - 1) / scale);

        using Bitmap source = bitmap.Clone(local, PixelFormat.Format32bppArgb);
        using Bitmap down = new Bitmap(downW, downH, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(down))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = downInterpolation;
            g.PixelOffsetMode = pixelOffsetMode;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(source, new Rectangle(0, 0, downW, downH));
        }

        using Graphics target = Graphics.FromImage(bitmap);
        target.CompositingQuality = CompositingQuality.HighSpeed;
        target.InterpolationMode = upInterpolation;
        target.SmoothingMode = SmoothingMode.None;
        target.PixelOffsetMode = PixelOffsetMode.Half;
        target.DrawImage(down, local, new Rectangle(0, 0, downW, downH), GraphicsUnit.Pixel);
    }

    private static PointF ToLocalPoint(PointF screenPoint, Rectangle selectedScreenRect)
    {
        return new PointF(
            screenPoint.X - selectedScreenRect.X,
            screenPoint.Y - selectedScreenRect.Y);
    }

    private sealed class CaptureSnapshotHistory
    {
        private readonly AnnotationDocument _annotations;
        private readonly CaptureWindow _window;
        private readonly int _maxSnapshots;
        private readonly Stack<CaptureSnapshot> _undo = new Stack<CaptureSnapshot>();
        private readonly Stack<CaptureSnapshot> _redo = new Stack<CaptureSnapshot>();

        public CaptureSnapshotHistory(AnnotationDocument annotations, CaptureWindow window, int maxSnapshots = 100)
        {
            _annotations = annotations;
            _window = window;
            _maxSnapshots = Math.Max(1, maxSnapshots);
        }

        public void CommitSnapshot()
        {
            PushUndo(CreateSnapshot());
            _redo.Clear();
            NotifyState();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redo.Push(CreateSnapshot());
            RestoreSnapshot(_undo.Pop());
            NotifyState();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.Push(CreateSnapshot());
            RestoreSnapshot(_redo.Pop());
            NotifyState();
            return true;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            NotifyState();
        }

        private void NotifyState()
        {
            _window.SetUndoEnabled(_undo.Count > 0);
        }

        private CaptureSnapshot CreateSnapshot()
        {
            return new CaptureSnapshot(_annotations.CreateSnapshot(), _window.CreateTextSnapshot());
        }

        private void RestoreSnapshot(CaptureSnapshot snapshot)
        {
            _annotations.ReplaceAll(snapshot.Shapes);
            _window.RestoreTextSnapshot(snapshot.Texts);
        }

        private void PushUndo(CaptureSnapshot snapshot)
        {
            _undo.Push(snapshot);
            if (_undo.Count <= _maxSnapshots) return;
            var items = _undo.Reverse().Skip(1).ToList();
            _undo.Clear();
            foreach (var item in items) _undo.Push(item);
        }
    }

    private sealed class CaptureSnapshot
    {
        public CaptureSnapshot(IReadOnlyList<AnnotationShape> shapes, IReadOnlyList<TextLayerSnapshot> texts)
        {
            Shapes = shapes;
            Texts = texts;
        }

        public IReadOnlyList<AnnotationShape> Shapes { get; }
        public IReadOnlyList<TextLayerSnapshot> Texts { get; }
    }

    private static string ResolveFileName(AppSettings settings)
    {
        string template = string.IsNullOrWhiteSpace(settings.FileNameTemplate)
            ? "Screenshot_{yyyyMMdd_HHmmss}"
            : settings.FileNameTemplate;

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = template.Replace("{yyyyMMdd_HHmmss}", stamp);

        // 截图统一保存为 PNG（保留元数据），扩展名固定。
        return baseName + ".png";
    }
}