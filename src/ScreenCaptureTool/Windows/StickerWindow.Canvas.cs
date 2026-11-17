using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Services;
using GdiBitmap = System.Drawing.Bitmap;

namespace ScreenCaptureTool.Windows;

public partial class StickerWindow
{
    private bool _isCanvasMode;
    private bool _canvasFlattening;
    private CanvasPictureView? _canvasView;
    private CanvasPicture? _canvasMoving;
    private Point _canvasLastPoint;
    private Point _canvasPendingPanPoint;
    private bool _canvasPanPending;
    private bool _canvasPanRafScheduled;
    private int _canvasPanGeneration;
    private IPointer? _canvasPointer;
    private static StickerWindow? _canvasDragSource;
    private static string? _canvasDragFile;
    private bool _canvasDropAccepted;
    private double _canvasBaseWidth, _canvasBaseHeight;
    private double _canvasWindowScale = 1;
    private static readonly Cursor CanvasMoveCursor = new(StandardCursorType.SizeAll);
    private bool _canvasMovingWindow;
    private Point _canvasWindowDragStart;
    private PixelPoint _canvasWindowDragOrigin;
    private WindowEdge? _canvasResizeCorner;
    private Rect _canvasResizeStartWindow;
    private Point _canvasResizeStartMouse, _canvasResizeMouse;
    private Rect[]? _canvasResizeStartPictures;
    private bool _canvasResizeRafScheduled;
    private int _canvasResizeGeneration;
    private static readonly Cursor CanvasResizeNwCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CanvasResizeNeCursor = new(StandardCursorType.TopRightCorner);
    private int _wordContentVersion;
    private double WordDisplayScale => _isCanvasMode ? 1 : _scale;
    private int _canvasNextPictureOrder;
    private readonly List<CanvasPicture> _canvasWordOwners = new();

    // 画布只占当前视口，不参与原有信封扩张；所有图片位置均为视口内 DIP。
    private sealed class CanvasPicture : IDisposable
    {
        public readonly GdiBitmap Pixels;
        public readonly Avalonia.Media.Imaging.Bitmap Image;
        public Rect Rect;
        public int TextOrder;
        public List<OcrWordRegion>? Words;
        public Task<List<OcrWordRegion>>? WordRecognition;
        public CanvasPicture(GdiBitmap pixels, Rect rect)
        {
            Pixels = pixels;
            Image = ToAvaloniaBitmap(pixels);
            Rect = rect;
        }
        public void Dispose() { Image.Dispose(); Pixels.Dispose(); }
    }

    private sealed class CanvasPictureView : Control
    {
        public readonly List<CanvasPicture> Pictures = new();
        public IBrush? Backing;
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(Backing, null, new Rect(Bounds.Size));
            using (context.PushClip(new Rect(Bounds.Size)))
                foreach (var p in Pictures)
                    context.DrawImage(p.Image, new Rect(p.Image.Size), p.Rect);
        }
    }

    private void InitCanvasMode()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnCanvasDragOver);
        AddHandler(DragDrop.DropEvent, OnCanvasDrop);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndCanvasPan());
        ActualThemeVariantChanged += (_, _) =>
        {
            RefreshCanvasTheme();
            if (_isCanvasMode) ApplyCanvasShadow();
        };
    }

    private void OnCanvasModeClick(object? sender, RoutedEventArgs e) => ToggleCanvasMode();

    private void ToggleCanvasMode()
    {
        if (_isLocked || _isMousePenetration || IsMattingInFlight || IsTranslateInFlight) return;
        EndStickerDrag("切换画布");
        if (_isCanvasMode) { ExitCanvasMode(); return; }
        if (_bitmap == null || TextViewbox.IsVisible || StickerColorSwatch.IsVisible
            || _isThumbnailMode || _thumbApplyPending) return;
        FinalizeAnnotationForGeometryChange("画布模式");
        if (_isResizing) EndEdgeResize();
        // 只有真实平移手势才用屏幕坐标结算；普通贴图的平移簿记尚未建立，不能反写 Margin。
        if (_isPanningContent || _panRafScheduled) FlushPendingContentPan();
        _isPanningContent = false;
        _isDragging = false;
        StopZoomEase();
        _wheelGen++;
        _wheelPendingRatio = 1;
        _pinSettleTimer?.Stop();
        // 不提交信封：超长图已经被压在一屏视口中，画布固定的就是此刻窗口大小。
        double w = Math.Max(1, ClientSize.Width - ShadowMargin * 2);
        double h = Math.Max(1, ClientSize.Height - ShadowMargin * 2);
        bool reusePictures = _canvasView != null;
        if (_canvasView == null)
        {
            using var annotated = AcquireAnnotationCompositeForExport();
            var pixels = annotated != null ? new GdiBitmap(annotated) : new GdiBitmap(_bitmap);
            BakeImageSelectionMarks(pixels);
            _canvasView = new CanvasPictureView
            {
                Width = w, Height = h,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            };
            _canvasView.Pictures.Add(new CanvasPicture(pixels,
                new Rect(ScaleCoordinateAroundAnchor(StickerImage.Margin.Left,
                        StickerImage.Margin.Left + StickerImage.Width / 2, 0.9),
                    ScaleCoordinateAroundAnchor(StickerImage.Margin.Top,
                        StickerImage.Margin.Top + StickerImage.Height / 2, 0.9),
                    StickerImage.Width * 0.9, StickerImage.Height * 0.9))
                { TextOrder = _canvasNextPictureOrder++, Words = _wordRegions });
            var panel = (Panel)RootBorder.Child!;
            panel.Children.Insert(panel.Children.IndexOf(StickerCanvas), _canvasView);
        }
        else
        {
            // 普通态缩放后再进入，图层按新的视口同比例恢复，原图和遮挡部分一直保留。
            double sx = w / _canvasView.Width, sy = h / _canvasView.Height;
            foreach (var p in _canvasView.Pictures)
                p.Rect = new Rect(p.Rect.X * sx, p.Rect.Y * sy, p.Rect.Width * sx, p.Rect.Height * sy);
            _canvasView.Width = w; _canvasView.Height = h;
        }
        _isCanvasMode = true;
        Cursor = null;
        _canvasBaseWidth = w;
        _canvasBaseHeight = h;
        _canvasWindowScale = 1;
        _wheelVisualOverride = false;
        _wheelGestureActive = false;
        _wheelGestureAnchorPx = new Point(double.NaN, double.NaN);
        UninstallWheelHitTransparency(this);
        StickerImage.IsVisible = false;
        CheckerboardBg.IsVisible = false;
        ImageBackdrop.IsVisible = false;
        CardHighlightBorder.IsVisible = false;
        HideSelectionToolbar();
        HideColorPicker();
        if (_bubbleShown) HideTranslateBubble();
        InvalidateCanvasText(preserveMarks: reusePictures);
        CanvasGrip.IsVisible = true;
        _canvasView.IsVisible = true;
        if (_pinActive) SetAvaloniaPixelLayersHidden(true);
        foreach (var p in _canvasView.Pictures) ClampCanvasPicture(p);
        RefreshCanvasTextGeometry();
        RefreshCanvasTheme();
        ApplyCanvasShadow();
        RequestPinSync();
        if (_isTextSelectable) SetTextSelectable(true);
        UpdateContextMenuState();
    }

    private void RefreshCanvasTheme()
    {
        if (_canvasView == null) return;
        if (this.TryFindResource("SctCheckerboardBrush", ActualThemeVariant, out var brush))
            _canvasView.Backing = brush as IBrush;
        _canvasView.InvalidateVisual();
    }

    private void ApplyCanvasShadow()
    {
        if (_canvasView == null) return;
        // 抓手始终留在视口内，整窗缩小后也不能伸进窗外阴影区域。
        CanvasResizePoints.Width = _canvasView.Width;
        CanvasResizePoints.Height = _canvasView.Height;
        if (_isLocked || _isMousePenetration) UpdateCanvasResizeHints(false);
        CanvasGrip.Width = Math.Min(64, Math.Max(1, _canvasView.Width - 8));
        CanvasGrip.Height = Math.Min(24, Math.Max(1, _canvasView.Height - 4));
        CanvasGrip.Margin = new Thickness(0, Math.Min(8,
            (_canvasView.Height - CanvasGrip.Height) / 2), 0, 0);
        ShadowBorder.Width = _canvasView.Width;
        ShadowBorder.Height = _canvasView.Height;
        ShadowBorder.Margin = new Thickness(0);
        ShadowBorder.SetCardGeometry(0, 0, 0);
        ShadowBorder.IsShadowEnabled = ShouldPaintWindowShadow();
        if (this.TryFindResource("SctStickerCanvasGlowColor", ActualThemeVariant, out var color) && color is Color c)
            ShadowBorder.ShadowColor = c;
        CardHighlightBorder.IsVisible = false;
    }

    // 每张图片至少留下 24 DIP（小图则整边）的角块，不能平移成再也点不到的状态。
    private void ClampCanvasPicture(CanvasPicture p)
    {
        if (_canvasView == null) return;
        double vx = Math.Min(24, Math.Min(p.Rect.Width, _canvasView.Width));
        double vy = Math.Min(24, Math.Min(p.Rect.Height, _canvasView.Height));
        p.Rect = new Rect(Math.Clamp(p.Rect.X, vx - p.Rect.Width, _canvasView.Width - vx),
            Math.Clamp(p.Rect.Y, vy - p.Rect.Height, _canvasView.Height - vy), p.Rect.Width, p.Rect.Height);
    }

    private CanvasPicture? CanvasHit(Point point)
    {
        if (_canvasView == null) return null;
        for (int i = _canvasView.Pictures.Count - 1; i >= 0; i--)
            if (_canvasView.Pictures[i].Rect.Contains(point)) return _canvasView.Pictures[i];
        return null;
    }

    private bool HandleCanvasPressed(PointerPressedEventArgs e)
    {
        if (!_isCanvasMode || _canvasView == null) return false;
        if (IsCanvasTextInteraction(e.Source)) return true;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed) { Close(); e.Handled = true; return true; }
        if (props.IsRightButtonPressed)
        {
            EndCanvasPan(); UpdateContextMenuState();
            RootBorder.ContextMenu?.Open(RootBorder);
            e.Handled = true; return true;
        }
        if (props.IsLeftButtonPressed && e.ClickCount >= 2 && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            EndCanvasPan();
            StickerToastWindow.ShowOrUpdateAtTop("请先退出画布模式，再双击关闭贴图");
            e.Handled = true;
            return true;
        }
        if (!props.IsLeftButtonPressed || _isLocked || _isMousePenetration) return true;
        RootBorder.ContextMenu?.Close();
        StickerFocusManager.SetFocus(this);
        var corner = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            ? GetCanvasResizeCorner(e.GetPosition(this)) : null;
        if (corner.HasValue)
        {
            BeginCanvasResize(corner.Value, e);
            e.Handled = true;
            return true;
        }
        if (IsVisualDescendant(CanvasGrip, e.Source as Visual))
        {
            EndCanvasPan();
            var origin = GetWindowOriginPx();
            var point = e.GetPosition(this);
            _canvasWindowDragOrigin = origin;
            _canvasWindowDragStart = new Point(origin.X + point.X * SafeScaling, origin.Y + point.Y * SafeScaling);
            _lastAppliedDragPos = origin;
            _canvasMovingWindow = true;
            _canvasPointer = e.Pointer;
            e.Pointer.Capture(this);
            Cursor = CanvasMoveCursor;
            e.Handled = true;
            return true;
        }
        bool altHeld = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (altHeld || _isTextSelectable && _wordRegions == null)
        {
            BeginAltQuickSelect(e);
            e.Handled = true;
            return true;
        }
        if (_altQuickSelect && !_isTextSelectable) EndAltQuickSelect();
        if (_isTextSelectable && FindWordIndexAt(e.GetPosition(StickerCanvas)) >= 0)
        {
            TryAnchorAltSelectionAt(e.GetPosition(StickerCanvas));
            e.Handled = true;
            return true;
        }
        _canvasLastPoint = e.GetPosition(_canvasView);
        _canvasMoving = CanvasHit(_canvasLastPoint);
        if (_canvasMoving != null)
        {
            // 点击图片即提到最前，便于整理重叠层。
            bool changedOrder = !ReferenceEquals(_canvasView.Pictures[^1], _canvasMoving);
            _canvasView.Pictures.Remove(_canvasMoving);
            _canvasView.Pictures.Add(_canvasMoving);
            if (changedOrder) RefreshCanvasTextGeometry();
            _canvasView.InvalidateVisual();
            RequestPinSync();
            _canvasPointer = e.Pointer;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
        return true;
    }

    private bool HandleCanvasMoved(PointerEventArgs e)
    {
        if (!_isCanvasMode || _canvasView == null) return false;
        if (IsCanvasTextInteraction(e.Source)) return true;
        if (_canvasResizeCorner.HasValue)
        {
            _canvasResizeMouse = CanvasScreenPoint(e);
            if (!_isLocked && !_isMousePenetration && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                ScheduleCanvasResize();
            else EndCanvasPan();
            e.Handled = true;
            return true;
        }
        UpdateCanvasResizeHints(e.KeyModifiers.HasFlag(KeyModifiers.Control), e.GetPosition(this));
        if (_canvasMovingWindow && !_isLocked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var origin = GetWindowOriginPx();
            var p = e.GetPosition(this);
            var screen = new Point(origin.X + p.X * SafeScaling, origin.Y + p.Y * SafeScaling);
            ApplyDragMove(new PixelPoint((int)Math.Round(_canvasWindowDragOrigin.X + screen.X - _canvasWindowDragStart.X),
                (int)Math.Round(_canvasWindowDragOrigin.Y + screen.Y - _canvasWindowDragStart.Y)));
            e.Handled = true;
            return true;
        }
        if (_isWordSelecting)
        {
            _altLastMoveCanvasPos = e.GetPosition(StickerCanvas);
            if (_wordRegions != null) UpdateSelectionTo(_altLastMoveCanvasPos);
            e.Handled = true;
            return true;
        }
        if (_canvasMoving != null && !_isLocked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ScheduleCanvasPan(e.GetPosition(_canvasView));
            if (!ReferenceEquals(Cursor, CanvasMoveCursor)) Cursor = CanvasMoveCursor;
        }
        else
        {
            EndCanvasPan();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                || _isTextSelectable && FindWordIndexAt(e.GetPosition(StickerCanvas)) >= 0)
                Cursor = new Cursor(StandardCursorType.Ibeam);
            UpdateCanvasResizeHints(e.KeyModifiers.HasFlag(KeyModifiers.Control), e.GetPosition(this));
        }
        e.Handled = true;
        return true;
    }

    private void EndCanvasPan()
    {
        FlushCanvasPan();
        _canvasPanGeneration++;
        _canvasPanRafScheduled = false;
        if (_canvasResizeCorner.HasValue) ApplyCanvasResizeFrame();
        _canvasResizeCorner = null;
        _canvasResizeStartPictures = null;
        _canvasResizeGeneration++;
        _canvasResizeRafScheduled = false;
        _canvasMoving = null;
        _canvasMovingWindow = false;
        var pointer = _canvasPointer;
        _canvasPointer = null;
        pointer?.Capture(null);
        Cursor = null;
    }

    /// <summary>高回报率移动只覆盖最新位置；图片呈现与 OCR 字框投影每显示帧合并一次。</summary>
    private void ScheduleCanvasPan(Point point)
    {
        _canvasPendingPanPoint = point;
        _canvasPanPending = true;
        if (_canvasPanRafScheduled) return;
        _canvasPanRafScheduled = true;
        int generation = _canvasPanGeneration;
        RequestAnimationFrame(_ =>
        {
            if (generation != _canvasPanGeneration) return;
            _canvasPanRafScheduled = false;
            FlushCanvasPan();
        });
    }

    private void FlushCanvasPan()
    {
        if (!_canvasPanPending) return;
        _canvasPanPending = false;
        if (_isClosed || !_isCanvasMode || _canvasView == null || _canvasMoving == null) return;
        var point = _canvasPendingPanPoint;
        var delta = point - _canvasLastPoint;
        _canvasLastPoint = point;
        if (delta.X == 0 && delta.Y == 0) return;
        var before = _canvasMoving.Rect;
        _canvasMoving.Rect = before.Translate(delta);
        ClampCanvasPicture(_canvasMoving);
        if (_canvasMoving.Rect == before) return;
        _canvasView.InvalidateVisual();
        RefreshCanvasTextGeometry();
        RequestPinSync();
    }

    private Point CanvasScreenPoint(PointerEventArgs e)
    {
        var origin = GetWindowOriginPx();
        var p = e.GetPosition(this);
        return new Point(origin.X + p.X * SafeScaling, origin.Y + p.Y * SafeScaling);
    }

    private WindowEdge? GetCanvasResizeCorner(Point point)
    {
        if (_canvasView == null) return null;
        // 以画布本身四角为基准，包含内侧端点及少量窗内阴影，避免大热区吃掉小画布。
        double x = point.X - ShadowMargin, y = point.Y - ShadowMargin;
        double w = _canvasView.Width, h = _canvasView.Height;
        if (x < -4 || y < -4 || x > w + 4 || y > h + 4) return null;
        bool west = x <= w / 2, north = y <= h / 2;
        if (Math.Abs(x - (west ? 0 : w)) > Math.Min(12, w / 2)
            || Math.Abs(y - (north ? 0 : h)) > Math.Min(12, h / 2)) return null;
        return north ? (west ? WindowEdge.NorthWest : WindowEdge.NorthEast)
            : (west ? WindowEdge.SouthWest : WindowEdge.SouthEast);
    }

    private void UpdateCanvasResizeHints(bool ctrl, Point? point = null)
    {
        CanvasResizePoints.IsVisible = ctrl && _isCanvasMode && !_isLocked && !_isMousePenetration;
        if (!CanvasResizePoints.IsVisible || !point.HasValue || _canvasMoving != null || _canvasMovingWindow) return;
        var corner = GetCanvasResizeCorner(point.Value);
        if (corner.HasValue)
            Cursor = EdgeDragsWest(corner.Value) == EdgeDragsNorth(corner.Value)
                ? CanvasResizeNwCursor : CanvasResizeNeCursor;
    }

    private void BeginCanvasResize(WindowEdge corner, PointerPressedEventArgs e)
    {
        // 排队滚轮可能移动窗口，按下位置必须在结算前保存为屏幕坐标。
        var screenMouse = CanvasScreenPoint(e);
        EndCanvasPan();
        HideSelectionToolbar();
        _isWordSelecting = false;
        _blankAnchorPending = false;
        // 先结清排队的滚轮，拖角基准只读最新窗口及图层，不与下一帧的缩放抢尺寸。
        ApplyWheelZoomAccumulated();
        _wheelGestureAnchorPx = new Point(double.NaN, double.NaN);
        var origin = GetWindowOriginPx();
        _canvasResizeStartWindow = new Rect(origin.X, origin.Y, Width * SafeScaling, Height * SafeScaling);
        _canvasResizeStartMouse = _canvasResizeMouse = screenMouse;
        _canvasResizeStartPictures = _canvasView!.Pictures.Select(p => p.Rect).ToArray();
        _canvasResizeCorner = corner;
        _canvasPointer = e.Pointer;
        e.Pointer.Capture(this);
        UpdateCanvasResizeHints(true);
        Cursor = EdgeDragsWest(corner) == EdgeDragsNorth(corner)
            ? CanvasResizeNwCursor : CanvasResizeNeCursor;
    }

    private void ScheduleCanvasResize()
    {
        if (_canvasResizeRafScheduled) return;
        _canvasResizeRafScheduled = true;
        int generation = _canvasResizeGeneration;
        RequestAnimationFrame(_ =>
        {
            if (generation != _canvasResizeGeneration) return;
            _canvasResizeRafScheduled = false;
            ApplyCanvasResizeFrame();
        });
    }

    private void ApplyCanvasResizeFrame()
    {
        if (!_isCanvasMode || _canvasView == null || !_canvasResizeCorner.HasValue
            || _canvasResizeStartPictures == null || _isLocked || _isMousePenetration || _isClosed) return;
        var corner = _canvasResizeCorner.Value;
        bool west = EdgeDragsWest(corner), north = EdgeDragsNorth(corner);
        var delta = _canvasResizeMouse - _canvasResizeStartMouse;
        double s = SafeScaling;
        var work = GetStableWorkAreaUnion();
        // 已有极小贴图不因点下角点而自动长大，单轴最小尺寸不超过起手尺寸。
        int minWidthPx = Math.Min(SnapStablePx(40 + ShadowMargin * 2, s), (int)Math.Round(_canvasResizeStartWindow.Width));
        int minHeightPx = Math.Min(SnapStablePx(40 + ShadowMargin * 2, s), (int)Math.Round(_canvasResizeStartWindow.Height));
        int widthPx = (int)Math.Clamp(Math.Round(_canvasResizeStartWindow.Width + (west ? -delta.X : delta.X)),
            minWidthPx, Math.Max(minWidthPx, work.Width));
        int heightPx = (int)Math.Clamp(Math.Round(_canvasResizeStartWindow.Height + (north ? -delta.Y : delta.Y)),
            minHeightPx, Math.Max(minHeightPx, work.Height));
        int xPx = (int)Math.Round(west ? _canvasResizeStartWindow.Right - widthPx : _canvasResizeStartWindow.Left);
        int yPx = (int)Math.Round(north ? _canvasResizeStartWindow.Bottom - heightPx : _canvasResizeStartWindow.Top);
        if (Math.Abs(Width * s - widthPx) < 0.01 && Math.Abs(Height * s - heightPx) < 0.01
            && GetWindowOriginPx() == new PixelPoint(xPx, yPx)) return;
        _canvasView.Width = widthPx / s - ShadowMargin * 2;
        _canvasView.Height = heightPx / s - ShadowMargin * 2;
        var shift = new Vector((_canvasResizeStartWindow.Left - xPx) / s, (_canvasResizeStartWindow.Top - yPx) / s);
        for (int i = 0; i < _canvasView.Pictures.Count; i++)
        {
            // 只改视口，图片保持屏幕位置与原尺寸；缩小时仍保留可选中的角块。
            var p = _canvasView.Pictures[i];
            p.Rect = _canvasResizeStartPictures[i].Translate(shift);
            ClampCanvasPicture(p);
        }
        _canvasBaseWidth = _canvasView.Width;
        _canvasBaseHeight = _canvasView.Height;
        _canvasWindowScale = 1;
        ApplyCanvasWindowRect(xPx, yPx, widthPx, heightPx, s);
    }

    private void FinishCanvasResize(PointerEventArgs e)
    {
        if (_canvasResizeCorner.HasValue) _canvasResizeMouse = CanvasScreenPoint(e);
        if (_canvasMoving != null && _canvasView != null)
        {
            _canvasPendingPanPoint = e.GetPosition(_canvasView);
            _canvasPanPending = true;
        }
        EndCanvasPan();
    }

    // 浮条、翻译圆点和气泡保留控件自己的按下/松开/悬浮路由，窗口不能截断按钮 Click。
    private bool IsCanvasTextInteraction(object? source)
        => IsVisualDescendant(SelectionToolbar, source as Visual)
           || IsVisualDescendant(TranslateDotLayer, source as Visual)
           || IsVisualDescendant(TranslateBubble, source as Visual)
           || IsVisualDescendant(TranslateBubbleClose, source as Visual);

    private async Task<List<OcrWordRegion>> RecognizeWordRegionsAsync()
    {
        if (!_isCanvasMode || _canvasView == null)
        {
            using var source = _bitmap == null ? null : new GdiBitmap(_bitmap);
            return source == null ? new() : await CaptureSession.GetSharedOcrService().ExtractTextRegionsAsync(source);
        }
        int version = _wordContentVersion;
        var pictures = _canvasView.Pictures.OrderBy(p => p.TextOrder).ToArray();
        foreach (var p in pictures)
        {
            if (_isClosed || !_isCanvasMode || version != _wordContentVersion) return new();
            if (p.Words != null) continue;
            // 共用在途任务；识别持有自己的原图副本，关闭贴图不会释放它正在读取的像素。
            p.WordRecognition ??= RecognizeCanvasPictureAsync(p);
            try { p.Words = await p.WordRecognition; }
            finally { p.WordRecognition = null; }
        }
        // 等待过程中可以缩放/平移，完成后按最新位置投影，不能使用开始识别时的窗口截图。
        return _isClosed || !_isCanvasMode || version != _wordContentVersion ? new() : ProjectCanvasWords(pictures);
    }

    private static async Task<List<OcrWordRegion>> RecognizeCanvasPictureAsync(CanvasPicture p)
    {
        using var source = new GdiBitmap(p.Pixels);
        return await CaptureSession.GetSharedOcrService().ExtractTextRegionsAsync(source);
    }

    private List<OcrWordRegion> ProjectCanvasWords(IEnumerable<CanvasPicture> pictures)
    {
        var projected = new List<OcrWordRegion>();
        _canvasWordOwners.Clear();
        int nextLine = 0;
        foreach (var p in pictures.OrderBy(p => p.TextOrder))
        {
            var lines = new Dictionary<int, int>();
            if (p.Words == null) continue;
            double sx = p.Rect.Width * SafeScaling / p.Pixels.Width;
            double sy = p.Rect.Height * SafeScaling / p.Pixels.Height;
            foreach (var word in p.Words)
            {
                if (!lines.TryGetValue(word.LineIndex, out int line))
                    lines[word.LineIndex] = line = nextLine++;
                var b = word.Bounds;
                projected.Add(new OcrWordRegion { Text = word.Text, LineIndex = line,
                    Bounds = new System.Drawing.RectangleF((float)(p.Rect.X * SafeScaling + b.X * sx),
                        (float)(p.Rect.Y * SafeScaling + b.Y * sy), (float)(b.Width * sx), (float)(b.Height * sy)) });
                _canvasWordOwners.Add(p);
            }
        }
        return projected;
    }

    private bool CanvasBandAt(WordLineBand band, float px, float py)
    {
        if (!_isCanvasMode || _canvasView == null) return true;
        var point = new Point(px / SafeScaling, py / SafeScaling);
        if (!new Rect(0, 0, _canvasView.Width, _canvasView.Height).Contains(point)) return false;
        int index = band.Indices is { Count: > 0 } ? band.Indices[0] : -1;
        return index >= 0 && index < _canvasWordOwners.Count
            && ReferenceEquals(_canvasWordOwners[index], CanvasHit(point));
    }

    private bool CanvasBandsSharePicture(WordLineBand a, WordLineBand b)
        => !_isCanvasMode || a.Indices is not { Count: > 0 } || b.Indices is not { Count: > 0 }
           || a.Indices[0] >= _canvasWordOwners.Count || b.Indices[0] >= _canvasWordOwners.Count
           || ReferenceEquals(_canvasWordOwners[a.Indices[0]], _canvasWordOwners[b.Indices[0]]);

    private void RefreshCanvasTextGeometry()
    {
        if (!_isCanvasMode || _canvasView == null) return;
        StickerCanvas.Width = _canvasView.Width;
        StickerCanvas.Height = _canvasView.Height;
        // 原图没有变化就保留识别结果，包括空结果；叠放顺序不改变字下标的身份。
        bool incomplete = _canvasView.Pictures.Any(p => p.Words == null);
        _wordRegions = ProjectCanvasWords(_canvasView.Pictures);
        _textContent = BuildTextFromWordRegions(_wordRegions);
        RebuildWordLineBands();
        foreach (var m in _selMarks)
            if (m.WordIndices != null) m.WordRects = MergeWordRunsInOcrPixels(OrderWordIndices(m.WordIndices));
        foreach (var m in _transMarks)
            if (m.WordIndices != null) m.WordRects = MergeWordRunsInOcrPixels(OrderWordIndices(m.WordIndices));
        if (!incomplete && (_isTextSelectable || _altQuickSelect))
        {
            BuildWordOverlay();
            ApplyWordSelection();
        }
        RedrawTextDecorations();
        if (incomplete)
        {
            // 新层尚未识别时，旧层标记仍跟随几何；保持总缓存为空，让下次选词补齐新层。
            _wordRegions = null;
            _textContent = null;
            _canvasWordOwners.Clear();
            RebuildWordLineBands();
        }
        ScheduleSelectionToolbarUpdate();
    }

    private void InvalidateCanvasText(bool preserveMarks = false)
    {
        // 进入画布或加入新原图时切换文字身份；纯几何变化只走投影刷新，不作废识别。
        _wordContentVersion++;
        _wordRegions = null;
        _canvasWordOwners.Clear();
        _textContent = null;
        _selectedWordIndices.Clear();
        _isWordSelecting = false;
        _altQuickSelect = false;
        _blankAnchorPending = false;
        _dragStartWordIndex = -1;
        HideSelectionToolbar();
        ClearWordOverlay();
        if (!preserveMarks) DropWordMarksForNewPicture();
        RebuildWordLineBands();
        StickerCanvas.Margin = new Thickness(0);
        if (_canvasView != null)
        {
            StickerCanvas.Width = _canvasView.Width;
            StickerCanvas.Height = _canvasView.Height;
        }
    }

    private bool SetCanvasPinPictures(LayeredPinSurface pin)
    {
        if (_canvasView == null) return false;
        var origin = GetWindowOriginPx();
        double s = SafeScaling;
        return pin.SetPictures(_canvasView.Pictures.Select(p => new PinPicture(p.Image,
            origin.X + (ShadowMargin + p.Rect.X) * s, origin.Y + (ShadowMargin + p.Rect.Y) * s,
            p.Rect.Width * s, p.Rect.Height * s)).ToArray());
    }

    private bool TryBuildCanvasPinFrame(out PinFrame f)
    {
        f = default;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        double s = SafeScaling;
        if (_canvasView == null || hwnd == IntPtr.Zero
            || !ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc)
            || Math.Abs(Width * s - rc.Width) > 1.5 || Math.Abs(Height * s - rc.Height) > 1.5)
        {
            _pinFrameSkipReason = "画布托管尺寸与原生窗口尚未对齐";
            return false;
        }
        double l = rc.Left + ShadowMargin * s, t = rc.Top + ShadowMargin * s;
        double w = _canvasView.Width * s, h = _canvasView.Height * s;
        f.ImgL = f.ClipL = f.ShL = f.CkL = l;
        f.ImgT = f.ClipT = f.ShT = f.CkT = t;
        f.ImgW = f.ShW = f.CkW = w;
        f.ImgH = f.ShH = f.CkH = h;
        f.ClipR = l + w; f.ClipB = t + h;
        f.HaloClipL = rc.Left; f.HaloClipT = rc.Top;
        f.HaloClipR = rc.Right; f.HaloClipB = rc.Bottom;
        f.ShadowOn = ShadowBorder.IsShadowEnabled;
        f.ShadowArgb = ShadowBorder.ShadowColor.ToUInt32();
        f.ExtentPx = ScreenCaptureTool.Controls.StickerShadowLayer.HaloExtentDip * s;
        f.SigmaPx = ScreenCaptureTool.Controls.StickerShadowLayer.HaloSigmaDip * s;
        f.CheckerOn = true;
        if (this.TryFindResource("SctCheckerboardBgColor", ActualThemeVariant, out var bg) && bg is Color b)
            f.CheckerBgArgb = b.ToUInt32();
        if (this.TryFindResource("SctCheckerboardTileColor", ActualThemeVariant, out var tile) && tile is Color c)
            f.CheckerTileArgb = c.ToUInt32();
        f.CheckerCellPx = Math.Max(1, (int)Math.Round(8 * s));
        f.ConstantAlpha = StickerOpacityAlpha;
        _pinFrameSkipReason = string.Empty;
        return true;
    }

    private bool HandleCanvasWheel(PointerWheelEventArgs e)
    {
        if (!_isCanvasMode || _canvasView == null) return false;
        if (IsCanvasTextInteraction(e.Source)) return true;
        EndCanvasPan();
        if (!_isLocked && !_isMousePenetration && e.Delta.Y != 0)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _wheelPendingRatio *= WheelZoomRatio(e.Delta.Y, e.KeyModifiers, continuous: true);
                UpdateWheelGestureAnchor(e);
                ScheduleWheelApplyRaf();
                e.Handled = true;
                return true;
            }
            var point = e.GetPosition(_canvasView);
            var p = CanvasHit(point) ?? (_canvasView.Pictures.Count > 0 ? _canvasView.Pictures[^1] : null);
            if (p != null)
            {
                // 鼠标下的图片独立缩放；落在空白时缩放最上层，窗口尺寸始终不写。
                double current = p.Rect.Width / p.Pixels.Width;
                double min = Math.Max(0.02, Math.Max(20.0 / p.Pixels.Width, 20.0 / p.Pixels.Height));
                double next = Math.Clamp(current * WheelZoomRatio(e.Delta.Y, e.KeyModifiers, continuous: true),
                    Math.Min(min, current), Math.Max(Math.Max(5 / SafeScaling, min), current));
                double ratio = next / current;
                p.Rect = new Rect(ScaleCoordinateAroundAnchor(p.Rect.X, point.X, ratio),
                    ScaleCoordinateAroundAnchor(p.Rect.Y, point.Y, ratio), p.Rect.Width * ratio, p.Rect.Height * ratio);
                ClampCanvasPicture(p);
                _canvasView.InvalidateVisual();
                RefreshCanvasTextGeometry();
                RequestPinSync();
            }
        }
        e.Handled = true;
        return true;
    }

    private void ZoomCanvasWindow(double ratio, Point mouseDip)
    {
        if (_canvasView == null) return;
        EndCanvasPan();
        double s = SafeScaling;
        double min = Math.Min(1, Math.Max(0.1, Math.Max(40 / _canvasBaseWidth, 40 / _canvasBaseHeight)));
        double max = 5;
        var work = GetStableWorkAreaUnion();
        if (work.Width > 0 && work.Height > 0)
            max = Math.Min(max, Math.Min((work.Width / s - ShadowMargin * 2) / _canvasBaseWidth,
                (work.Height / s - ShadowMargin * 2) / _canvasBaseHeight));
        max = Math.Max(min, max);
        double next = Math.Clamp(_canvasWindowScale * ratio, min, max);
        if (Math.Abs(next - _canvasWindowScale) < 0.000001) return;
        // 沿用普通贴图的屏幕锚点与稳定像素取整，按实际视口倍率保持鼠标下的内容位置。
        var origin = GetWindowOriginPx();
        double anchorX = origin.X + mouseDip.X * s, anchorY = origin.Y + mouseDip.Y * s;
        int widthPx = SnapStablePx(_canvasBaseWidth * next + ShadowMargin * 2, s);
        int heightPx = SnapStablePx(_canvasBaseHeight * next + ShadowMargin * 2, s);
        double width = widthPx / s, height = heightPx / s;
        double w = width - ShadowMargin * 2, h = height - ShadowMargin * 2;
        double sx = w / _canvasView.Width, sy = h / _canvasView.Height;
        int xPx = (int)Math.Round(ScaleCoordinateAroundAnchor(origin.X + ShadowMargin * s, anchorX, sx) - ShadowMargin * s);
        int yPx = (int)Math.Round(ScaleCoordinateAroundAnchor(origin.Y + ShadowMargin * s, anchorY, sy) - ShadowMargin * s);
        foreach (var p in _canvasView.Pictures)
            p.Rect = new Rect(p.Rect.X * sx, p.Rect.Y * sy, p.Rect.Width * sx, p.Rect.Height * sy);
        _canvasView.Width = w;
        _canvasView.Height = h;
        _canvasWindowScale = next;
        foreach (var p in _canvasView.Pictures) ClampCanvasPicture(p);
        ApplyCanvasWindowRect(xPx, yPx, widthPx, heightPx, s);
    }

    private void ApplyCanvasWindowRect(int xPx, int yPx, int widthPx, int heightPx, double s)
    {
        ClampVisibleContentPosition(ref xPx, ref yPx,
            GetVisibleContentInWindow(widthPx, heightPx, s), s, GetStableWorkAreaRects());
        // 滚轮与拖角共用原子移动＋尺寸同步及分层呈现，避免旧位置残影。
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
            ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx,
                widthPx, heightPx, StickerStyling.ResizeSwpFlags);
        ApplyWindowRectManaged(xPx, yPx, widthPx, heightPx, s);
        ApplyCanvasShadow();
        _canvasView!.InvalidateVisual();
        RefreshCanvasTextGeometry();
        RequestPinSync();
    }

    private bool CanAcceptCanvasDrop(DragEventArgs e)
        => !_isLocked && !_isMousePenetration && !_canvasResizeCorner.HasValue && !IsMattingInFlight && !IsTranslateInFlight
           && _bitmap != null && !TextViewbox.IsVisible && !StickerColorSwatch.IsVisible
           && !_isThumbnailMode && !_thumbApplyPending && _canvasDragSource != null
           && _canvasDragSource != this && !_canvasDragSource._isClosed && _canvasDragFile != null
           && e.KeyModifiers.HasFlag(KeyModifiers.Control)
           && e.Data.Get(DataFormats.FileNames) is IEnumerable<string> paths
           && paths.Any(path => string.Equals(path, _canvasDragFile, StringComparison.OrdinalIgnoreCase));

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAcceptCanvasDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        if (!CanAcceptCanvasDrop(e)) return;
        try
        {
            using var file = new GdiBitmap(_canvasDragFile!);
            var source = _canvasDragSource!;
            // 普通图片贴图也可接收：松手且文件可读之后才进入画布，悬停不改变目标状态。
            if (!_isCanvasMode) ToggleCanvasMode();
            if (!_isCanvasMode || _canvasView == null) return;
            var point = e.GetPosition(_canvasView);
            double w = source.DisplayWidthDipAt(source._scale) * source.SafeScaling / SafeScaling;
            double h = source.DisplayHeightDipAt(source._scale) * source.SafeScaling / SafeScaling;
            var p = new CanvasPicture(new GdiBitmap(file), new Rect(point.X - w / 2, point.Y - h / 2, w, h));
            p.TextOrder = _canvasNextPictureOrder++;
            _canvasView.Pictures.Add(p);
            ClampCanvasPicture(p);
            _canvasView.InvalidateVisual();
            InvalidateCanvasText(preserveMarks: true);
            RefreshCanvasTextGeometry();
            RetainWordMarkLayer();
            RequestPinSync();
            source._canvasDropAccepted = true;
            BringStickerToFront();
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        catch (Exception ex) { AppLogger.Error("StickerWindow: 合并画布贴图失败", ex); }
    }

    private GdiBitmap? SnapshotCanvas()
    {
        if (!_isCanvasMode || _canvasView == null) return null;
        FlushCanvasPan();
        double s = SafeScaling;
        var bitmap = new GdiBitmap(Math.Max(1, (int)Math.Round(_canvasView.Width * s)),
            Math.Max(1, (int)Math.Round(_canvasView.Height * s)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        foreach (var p in _canvasView.Pictures)
            g.DrawImage(p.Pixels, new System.Drawing.RectangleF((float)(p.Rect.X * s), (float)(p.Rect.Y * s),
                (float)(p.Rect.Width * s), (float)(p.Rect.Height * s)));
        return bitmap;
    }

    private void ExitCanvasMode()
    {
        if (!_isCanvasMode || _canvasView == null) return;
        // 先结清拖角最后一帧，合成图尺寸与即将保留的窗口视口必须相同。
        EndCanvasPan();
        var bitmap = SnapshotCanvas();
        if (bitmap == null) return;
        EndAltQuickSelect();
        HideSelectionToolbar();
        if (_bubbleShown) HideTranslateBubble();
        _isCanvasMode = false;
        CanvasGrip.IsVisible = false;
        CanvasResizePoints.IsVisible = false;
        _wheelGen++;
        _wheelPendingRatio = 1;
        _wheelGestureAnchorPx = new Point(double.NaN, double.NaN);
        _canvasView.IsVisible = false;
        StickerImage.IsVisible = true;
        CardHighlightBorder.IsVisible = true;
        _scale = 1;
        StickerImage.Margin = new Thickness(0);
        _canvasFlattening = true;
        try { ReplaceImage(bitmap); }
        finally { _canvasFlattening = false; }
        // 模式切换没有更换文字身份；字框已在合成图像素坐标中，复用原有标记层与导出烘焙。
        if (_isTextSelectable) SetTextSelectable(true);
        RetainWordMarkLayer();
        RedrawTextDecorations();
        // 合成图已经包含旧标注，后续标注／恢复不能再拿旧干净底图叠一遍。
        if (_initialAnnotationSource != null && _initialAnnotationSource != _bitmap)
            _initialAnnotationSource.Dispose();
        _initialAnnotationSource = null;
        _initialAnnotationShapes = null;
        _initialAnnotationTexts = null;
        if (!string.IsNullOrEmpty(HistoryId))
        {
            try
            {
                ScreenCaptureTool.Core.History.CaptureHistoryStore.SaveCleanImage(HistoryId, _bitmap!);
                ScreenCaptureTool.Core.History.CaptureHistoryStore.TryUpdateImage(HistoryId, _bitmap!, AppSettings.Load());
            }
            catch (Exception ex) { AppLogger.Warn("StickerWindow: 画布合成写回历史失败: " + ex.Message); }
        }
        UpdateContextMenuState();
    }

    private void DisposeCanvasPictures()
    {
        EndCanvasPan();
        CanvasGrip.IsVisible = false;
        CanvasResizePoints.IsVisible = false;
        if (_canvasView == null) return;
        if (_isCanvasMode)
        {
            _isCanvasMode = false;
            StickerImage.IsVisible = true;
            CardHighlightBorder.IsVisible = true;
        }
        ((Panel)RootBorder.Child!).Children.Remove(_canvasView);
        var pictures = _canvasView.Pictures.ToArray();
        _canvasView.Pictures.Clear();
        _canvasWordOwners.Clear();
        _canvasView = null;
        // 合成器可能仍在消费上一帧，沿用贴图换源的延后释放口径。
        Dispatcher.UIThread.Post(() => { foreach (var p in pictures) p.Dispose(); }, DispatcherPriority.Background);
    }

    private bool HandleCanvasKey(KeyEventArgs e)
    {
        if (TextTool.HasActiveTextEditing || CounterPillHost.IsEditingNote) return false;
        if (ShortcutMatches("sticker.canvas", e))
        {
            ToggleCanvasMode(); e.Handled = true; return true;
        }
        if (!_isCanvasMode) return false;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateCanvasResizeHints(true);
            e.Handled = true;
            return true;
        }
        EndCanvasPan();
        UpdateCanvasResizeHints(false);
        if (ShortcutMatches("sticker.close", e)) Close();
        else if (ShortcutMatches("sticker.copy", e))
        {
            if (_selectedWordIndices.Count > 0) CopySelectedText();
            else OnCopyImageClick(null, new RoutedEventArgs());
        }
        else if (ShortcutMatches("sticker.save", e)) OnSaveImageClick(null, new RoutedEventArgs());
        else if (ShortcutMatches("sticker.lock", e)) { EndCanvasPan(); ToggleLock(); }
        else if (ShortcutMatches("sticker.shadow", e) && _isFocused)
        {
            _shadowVisible = !ShouldPaintWindowShadow();
            _shadowManualOverride = true;
            ApplyShadow();
            App.RaiseStickerShadowToast(_shadowVisible, this);
        }
        else if (ShortcutMatches("sticker.penetrate", e)) ToggleMousePenetration();
        else if (ShortcutMatches("sticker.annotate", e) && !_isLocked && !_isMousePenetration)
        {
            ExitCanvasMode();
            return false;
        }
        e.Handled = true;
        return true;
    }

    private void UpdateCanvasMenuState()
    {
        SetMenuGesture(MenuCanvasMode, "sticker.canvas");
        MenuCanvasMode.Classes.Set("on", _isCanvasMode);
        MenuCanvasMode.IsEnabled = !_isLocked && !_isMousePenetration && _bitmap != null
            && !TextViewbox.IsVisible && !StickerColorSwatch.IsVisible && !_isThumbnailMode && !_thumbApplyPending
            && !IsMattingInFlight && !IsTranslateInFlight;
        MenuImageAdjust.IsEnabled = !_isCanvasMode && !_isLocked && _bitmap != null && !TextViewbox.IsVisible;
        MenuTranslate.IsEnabled = !_isCanvasMode && !_isLocked;
        if (!_isCanvasMode) return;
        MenuThumbnailMode.IsEnabled = false;
        MenuFitToScreen.IsEnabled = false;
        MenuSize.IsEnabled = false;
        MenuImageAdjust.IsEnabled = false;
        MenuSelectableText.IsEnabled = !_isLocked && !_isMousePenetration;
        MenuMatting.IsEnabled = false;
        MenuTranslate.IsEnabled = false;
        MenuAnnotationToolbar.IsEnabled = false;
    }
}
