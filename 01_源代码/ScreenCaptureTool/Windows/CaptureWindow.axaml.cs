using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 全屏透明截图遮罩窗口。哑窗口：负责铺满虚拟屏幕、显示 SelectionLayer、提供坐标换算与轻量控制条。
/// 业务流程（自动检测、保存、热键）由 <see cref="Services.CaptureSession"/> 协调。
/// </summary>
// 颜色统一：所有标注工具（画笔/箭头/矩形/序号/文本前景/聚光灯描边）共享 SelectedAnnotationColor

public partial class CaptureWindow : Window
{
    private const double OverlayGap = 10;
    private const double OverlayMargin = 12;

    private Rect _lastRegionDip;
    private bool _lastHasSelection;
    private bool _isAutoMosaicScanning;
    private long _autoMosaicScanStartedTick;
    private double _autoMosaicScanHeight;
    private DispatcherTimer? _autoMosaicScanTimer;
    private readonly TranslateTransform _autoMosaicScanTransform = new();
    private string? _activeToolKey;
    private AnnotationLineStyle _selectedBrushLineStyle = AnnotationLineStyle.Solid;
    private AnnotationLineStyle _selectedShapeLineStyle = AnnotationLineStyle.Solid;
    private ArrowAnnotationStyle _selectedArrowStyle = ArrowAnnotationStyle.Designed;
    private ShapeAnnotationKind _selectedShapeKind = ShapeAnnotationKind.Rectangle;
    private SpotlightShapeKind _selectedSpotlightKind = SpotlightShapeKind.Rectangle;
    private BlurMode _selectedBlurMode = BlurMode.Blur;
    private bool _isAutoMosaicMode;
    private bool _suppressBlurSettingsEvent;
    private bool _suppressSpotlightSettingsEvent;
    // _selectedSpotlightStrokeColor 已移除：聚光灯描边统一使用 SelectedAnnotationColor

    // ==========================================
    // 文本工具状态（完全对齐旧版 ScreenCapture.Fields）
    // ==========================================
    private bool _isTextToolActive; // CaptureSession 设置此标志来激活文本工具
    private double _currentTextSize = 24;
    private bool _isTextBold = false;
    private bool _isTextItalic = false;
    private bool _hasStroke = false;     // 新建文本默认不启用描边；用户开启后记忆当前状态
    private bool _hasBackground = false; // 新建文本默认不启用填充；用户开启后记忆当前状态
    private double _textStrokeThickness = 4.0;
    private bool _isStrokeAuto = false;
    private string _currentTextFont = "Microsoft YaHei UI";
    private Color _textStrokeColor = Colors.White;
    private Color _textFillColor = Color.FromRgb(255, 204, 0);
    private Color _textForegroundColor = Color.FromRgb(51, 136, 255); // 与 SelectedAnnotationColor 默认值保持一致
    private double _textFillOpacity = 100;
    private double _textFillCornerRadius = 4;
    private double _textFillPadding = 4;
    private int _colorTargetMode = 0; // 0=Text, 1=Background, 2=Stroke
    private TextLayerInfo? _selectedText;
    private bool _isDraggingSelectedText;
    private bool _isRotatingText;
    private TextLayerInfo? _rotatingTextInfo;
    private Point _rotateStartPoint;
    private double _rotateStartAngle;
    private Point _textDragStartPoint;
    private Point _textDragStartPosition;
    private bool _textCtrlKeyDown;
    private List<TextLayerInfo> _textLayers = new();
    private bool _suppressTextSnapshotEvents;
    private bool _hasPendingTextEditSnapshot;

    // UI 控件引用（在 TextToolsPanel 初始化时填充）
    private Button? _btnTextBold;
    private Button? _btnTextItalic;
    private Button? _btnTextStroke;
    private Button? _btnTextBackground;
    private Button? _btnTargetStrokeColor;
    private Button? _btnTargetStrokeText;
    private Button? _btnTargetBackgroundColor;
    private Button? _btnTargetBackgroundText;
    private Popup? _textStrokePopup;
    private Popup? _textBackgroundPopup;
    private Popup? _textSizePopup;
    private TextBlock? _textSizeValueText;
    private Grid? _textSizeHost;
    private TextBlock? _textStrokeSizeValue;
    private TextBlock? _textFillOpacityValue;
    private TextBlock? _textFillCornerValue;
    private TextBlock? _textFillPaddingValue;
    private CheckBox? _chkStrokeEnable;
    private CheckBox? _chkBackgroundEnable;
    private Slider? _sliderStrokeSize;
    private Slider? _sliderFillOpacity;
    private Slider? _sliderFillCorner;
    private Slider? _sliderFillPadding;
    private StackPanel? _textToolsPanel;

    public event EventHandler? BrushRequested;
    public event EventHandler? ArrowRequested;
    public event EventHandler? RectangleRequested;
    public event EventHandler? TextRequested;
    public event EventHandler? TextSnapshotRequested;
    public event EventHandler? MosaicRequested;
    public event EventHandler? ManualMosaicRequested;
    public event EventHandler? AutoMosaicRequested;
    public event EventHandler? BlurSettingsChanged;
    public event EventHandler? SpotlightSettingsChanged;
    public event EventHandler? AnnotationCommonSettingsChanged;
    public event EventHandler? SpotlightRequested;
    public event EventHandler? CounterRequested;
    public event EventHandler? ColorPickerRequested;
    public event EventHandler? UndoRequested;
    public event EventHandler? RedoRequested;
    public event EventHandler? ClearAllAnnotationsRequested;
    public event EventHandler? LongScrollRequested;
    public event EventHandler? OcrRequested;
    public event EventHandler? StickerRequested;
    public event EventHandler? ConfirmRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? ClearCurrentToolRequested;

    /// <summary>
    /// 对外触发"贴图"请求，与点击贴图按钮走完全相同的链路。
    /// 供全局热键（如快速贴图 F3）在截图进行中把当前选区转化为贴图。
    /// 事件只能在声明类内部 invoke，故在此提供入口。
    /// </summary>
    public void RaiseStickerRequested() => StickerRequested?.Invoke(this, EventArgs.Empty);

    public CaptureWindow()
    {
        InitializeComponent();
        SliderMosaicBlockSize.ValueChanged += (_, _) =>
        {
            UpdateToolOptionLabels();
            if (!_suppressBlurSettingsEvent)
            {
                BlurSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        SliderSpotlightDarkness.ValueChanged += (_, _) =>
        {
            UpdateToolOptionLabels();
            RaiseSpotlightSettingsChanged();
        };
        SliderSpotlightStrokeThickness.ValueChanged += (_, _) =>
        {
            UpdateToolOptionLabels();
            RaiseSpotlightSettingsChanged();
        };
        AutoMosaicScanFill.RenderTransform = _autoMosaicScanTransform;
        UpdateMosaicModeVisuals();
        UpdateToolOptionLabels();

        // Opened 之后再设置位置和尺寸：窗口未创建底层句柄时 DesktopScaling 不可靠
        Opened += OnWindowOpened;

        // 文本拖拽和旋转通过 CaptureSession→TryHandleTextToolInteraction 统一处理
        // 全局 PointerPressed/Moved/Released 只处理拖拽和旋转的连续性
        OverlayCanvas.Focusable = true;
        OverlayCanvas.PointerMoved += OnOverlayCanvasPointerMoved;
        OverlayCanvas.PointerReleased += OnOverlayCanvasPointerReleased;
        PointerMoved += OnOverlayCanvasPointerMoved;
        PointerReleased += OnOverlayCanvasPointerReleased;
        AddHandler(KeyDownEvent, OnTextEditingKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyUpEvent, OnTextEditingKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
    }

    /// <summary>对外暴露 SelectionLayer，让 CaptureSession 直接给 Region 赋值。</summary>
    public SelectionLayer Layer => SelectionLayer;

    /// <summary>对外暴露 AnnotationLayer，让 CaptureSession 绑定标注文档。</summary>
    public AnnotationLayer Annotation => AnnotationLayer;

    /// <summary>设置截图选择界面的冻结背景，避免透明全屏窗口显示到 DWM 的过期桌面缓存。</summary>
    public void SetFrozenScreenBitmap(System.Drawing.Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        FrozenScreenImage.Source = new Bitmap(ms);
    }

    /// <summary>窗口在屏幕坐标系中的原点（物理像素）。</summary>
    public PixelPoint ScreenOrigin => Position;

    public System.Drawing.Color SelectedAnnotationColor { get; private set; } = System.Drawing.Color.FromArgb(255, 51, 136, 255);

    public AnnotationLineStyle SelectedBrushLineStyle => _selectedBrushLineStyle;

    public AnnotationLineStyle SelectedShapeLineStyle => _selectedShapeLineStyle;

    public ArrowAnnotationStyle SelectedArrowStyle => _selectedArrowStyle;

    public ShapeAnnotationKind SelectedShapeKind => _selectedShapeKind;

    public string? ActiveToolKey => _activeToolKey;

    public float SelectedStrokeThickness => (float)Math.Max(1.0, SliderStrokeThickness.Value);

    public BlurMode SelectedBlurMode => _selectedBlurMode;

    public int SelectedBlurUiLevel => BlurIntensityMapper.ClampUiLevel((int)Math.Round(SliderMosaicBlockSize.Value));

    public double SelectedBlurIntensity => BlurIntensityMapper.UiToPhysical(SelectedBlurUiLevel);

    public int SelectedMosaicBlockSize => BlurIntensityMapper.UiLevelToMosaicBlockSize(SelectedBlurUiLevel);

    public bool IsAutoMosaicMode => _isAutoMosaicMode;

    public void SyncBlurToolState(BlurMode mode, int uiLevel)
    {
        _suppressBlurSettingsEvent = true;
        try
        {
            _selectedBlurMode = mode;
            SliderMosaicBlockSize.Value = BlurIntensityMapper.ClampUiLevel(uiLevel);
            UpdateMosaicModeVisuals();
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressBlurSettingsEvent = false;
        }
    }

    public float SelectedSpotlightDarkness => (float)Math.Clamp(SliderSpotlightDarkness.Value / 100.0, 0.0, 1.0);

    public SpotlightShapeKind SelectedSpotlightKind => _selectedSpotlightKind;

    public System.Drawing.Color SelectedSpotlightStrokeColor => SelectedAnnotationColor;

    public float SelectedSpotlightStrokeThickness => (float)Math.Max(0.0, SliderSpotlightStrokeThickness.Value);

    public void SyncSpotlightToolState(SpotlightShape shape)
    {
        _suppressSpotlightSettingsEvent = true;
        try
        {
            _selectedSpotlightKind = shape.ShapeKind;
            SelectedAnnotationColor = shape.StrokeColor;
            SliderSpotlightDarkness.Value = Math.Clamp(Math.Round(shape.Darkness * 100.0f), SliderSpotlightDarkness.Minimum, SliderSpotlightDarkness.Maximum);
            SliderSpotlightStrokeThickness.Value = Math.Clamp(Math.Round(shape.StrokeThickness), SliderSpotlightStrokeThickness.Minimum, SliderSpotlightStrokeThickness.Maximum);
            UpdateSpotlightKindVisuals();
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressSpotlightSettingsEvent = false;
        }
    }

    public void SetSpotlightDarknessPercent(double percent, bool raiseChanged = true)
    {
        if (SliderSpotlightDarkness == null) return;
        double next = Math.Clamp(Math.Round(percent), SliderSpotlightDarkness.Minimum, SliderSpotlightDarkness.Maximum);
        bool changed = Math.Abs(SliderSpotlightDarkness.Value - next) > 0.001;
        _suppressSpotlightSettingsEvent = true;
        try
        {
            SliderSpotlightDarkness.Value = next;
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressSpotlightSettingsEvent = false;
        }
        if (raiseChanged && changed)
        {
            RaiseSpotlightSettingsChanged();
        }
    }

    private void RaiseSpotlightSettingsChanged()
    {
        if (_suppressSpotlightSettingsEvent) return;
        SpotlightSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public float SelectedCounterRadius => (float)Math.Max(8.0, SliderCounterRadius.Value);

    /// <summary>同步序号大小滑块的数值（供 CaptureSession 滚轮/选中时调用）。</summary>
    public void SyncCounterRadiusSlider(float radius)
    {
        if (SliderCounterRadius == null) return;
        double clamped = Math.Clamp(radius, SliderCounterRadius.Minimum, SliderCounterRadius.Maximum);
        SliderCounterRadius.Value = clamped;
        if (TxtCounterRadius != null) TxtCounterRadius.Text = Math.Round(clamped).ToString();
    }

    /// <summary>当前 DPI 缩放，<see cref="DesktopScaling"/> 的容错版本。</summary>
    public double SafeScaling
    {
        get
        {
            double s = DesktopScaling;
            return s > 0 ? s : 1.0;
        }
    }

    /// <summary>
    /// 屏幕物理像素矩形 → 窗口 DIP 矩形（用于把 AutoDetectController 的高亮区给 SelectionLayer）。
    /// </summary>
    public Rect ScreenRectToWindowDip(System.Drawing.Rectangle screenRect)
    {
        double s = SafeScaling;
        double x = (screenRect.X - Position.X) / s;
        double y = (screenRect.Y - Position.Y) / s;
        double w = screenRect.Width / s;
        double h = screenRect.Height / s;
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 窗口 DIP 点 → 屏幕物理像素点（用于把鼠标事件位置喂给 AutoDetectController）。
    /// </summary>
    public System.Drawing.Point WindowDipToScreenPoint(Point windowDip)
    {
        double s = SafeScaling;
        int x = (int)Math.Round(windowDip.X * s + Position.X);
        int y = (int)Math.Round(windowDip.Y * s + Position.Y);
        return new System.Drawing.Point(x, y);
    }

    public bool IsOverlayHit(object? source)
    {
        if (source is not Visual visual) return false;
        return IsOverlayVisual(visual) || visual.GetVisualAncestors().Any(IsOverlayVisual);
    }

    private bool IsOverlayVisual(Visual visual)
    {
        return ReferenceEquals(visual, ActionBar)
            || ReferenceEquals(visual, ToolOptionsBar)
            || ReferenceEquals(visual, BrushLineStyleMenu)
            || ReferenceEquals(visual, ArrowStyleMenu)
            || ReferenceEquals(visual, ColorPickerPopup);
    }

    public bool IsOverlayPoint(Point windowDip)
    {
        return ContainsWindowPoint(ActionBar, windowDip)
            || ContainsWindowPoint(ToolOptionsBar, windowDip)
            || ContainsWindowPoint(BrushLineStyleMenu, windowDip)
            || ContainsWindowPoint(ArrowStyleMenu, windowDip)
            || ContainsWindowPoint(ColorPickerPopup, windowDip);
    }

    private bool ContainsWindowPoint(Control control, Point windowDip)
    {
        if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        Point? topLeft = control.TranslatePoint(new Point(0, 0), this);
        if (!topLeft.HasValue) return false;
        return new Rect(topLeft.Value, control.Bounds.Size).Contains(windowDip);
    }

    public void UpdateAnnotationTransform()
    {
        AnnotationLayer.ScreenOrigin = Position;
        AnnotationLayer.ScreenToDipScale = 1.0 / SafeScaling;
        AnnotationLayer.InvalidateVisual();
    }

    public void ShowColorPickerPreview(System.Drawing.Point screenPoint, System.Drawing.Color color)
    {
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        TxtColorHex.Text = hex;
        TxtColorRgb.Text = $"RGB({color.R}, {color.G}, {color.B})";
        ColorPreview.Background = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B));

        Point dip = new Point(
            (screenPoint.X - Position.X) / SafeScaling,
            (screenPoint.Y - Position.Y) / SafeScaling);

        ColorPickerPopup.IsVisible = true;
        ColorPickerPopup.Measure(Size.Infinity);
        Size size = ColorPickerPopup.DesiredSize;
        double left = dip.X + 16;
        double top = dip.Y + 16;
        if (left + size.Width + OverlayMargin > Bounds.Width)
        {
            left = dip.X - size.Width - 16;
        }
        if (top + size.Height + OverlayMargin > Bounds.Height)
        {
            top = dip.Y - size.Height - 16;
        }

        Canvas.SetLeft(ColorPickerPopup, Clamp(left, OverlayMargin, Math.Max(OverlayMargin, Bounds.Width - size.Width - OverlayMargin)));
        Canvas.SetTop(ColorPickerPopup, Clamp(top, OverlayMargin, Math.Max(OverlayMargin, Bounds.Height - size.Height - OverlayMargin)));
    }

    public void HideColorPickerPreview()
    {
        ColorPickerPopup.IsVisible = false;
    }

    public void PrepareForLongScrollTransition()
    {
        SetActiveTool(null);
        AnnotationLayer.ClearPreviewShape();
        HideColorPickerPreview();
        ActionBar.IsVisible = false;
        ToolOptionsBar.IsVisible = false;
        BrushLineStyleMenu.IsVisible = false;
        ArrowStyleMenu.IsVisible = false;
    }

    public void SetActiveTool(string? toolKey)
    {
        ClearActiveTool(BtnBrush);
        ClearActiveTool(BtnArrow);
        ClearActiveTool(BtnRectangle);
        ClearActiveTool(BtnText);
        ClearActiveTool(BtnMosaic);
        ClearActiveTool(BtnSpotlight);
        ClearActiveTool(BtnCounter);

        Button? active = toolKey switch
        {
            "Brush" => BtnBrush,
            "Arrow" => BtnArrow,
            "Rectangle" => BtnRectangle,
            "Text" => BtnText,
            "Mosaic" => BtnMosaic,
            "Spotlight" => BtnSpotlight,
            "Counter" => BtnCounter,
            _ => null,
        };

        if (active != null && !active.Classes.Contains("active"))
        {
            active.Classes.Add("active");
        }

        _activeToolKey = toolKey;
        ShowToolOptions(toolKey);
    }

    private static void ClearActiveTool(Button button)
    {
        button.Classes.Remove("active");
    }

    private void ShowToolOptions(string? toolKey)
    {
        bool wasTextOptionsVisible = _textToolsPanel?.IsVisible == true;

        BrushOptionsPanel.IsVisible = false;
        ArrowOptionsPanel.IsVisible = false;
        RectangleOptionsPanel.IsVisible = false;
        if (_textToolsPanel != null) _textToolsPanel.IsVisible = false;
        MosaicOptionsPanel.IsVisible = false;
        SpotlightOptionsPanel.IsVisible = false;
        CounterOptionsPanel.IsVisible = false;
        CommonThicknessPanel.IsVisible = false;
        CommonColorPanel.IsVisible = false;
        SepAfterStyle.IsVisible = false;
        SepAfterSize.IsVisible = false;
        SepBeforeClear.IsVisible = false;
        BtnClearCurrentTool.IsVisible = false;

        BrushLineStyleMenu.IsVisible = false;
        ArrowStyleMenu.IsVisible = false;

        if (toolKey != "Text" && wasTextOptionsVisible)
            CloseTextPopups();

        bool hasOptions = toolKey is "Brush" or "Arrow" or "Rectangle" or "Text" or "Mosaic" or "Spotlight" or "Counter";
        ToolOptionsBar.IsVisible = hasOptions;

        if (toolKey == "Brush")
        {
            BrushOptionsPanel.IsVisible = true;
            CommonThicknessPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有画笔");
        }
        else if (toolKey == "Arrow")
        {
            ArrowOptionsPanel.IsVisible = true;
            CommonThicknessPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有箭头");
        }
        else if (toolKey == "Rectangle")
        {
            RectangleOptionsPanel.IsVisible = true;
            CommonThicknessPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有形状");
        }
        else if (toolKey == "Text")
        {
            _textForegroundColor = Color.FromArgb(SelectedAnnotationColor.A, SelectedAnnotationColor.R, SelectedAnnotationColor.G, SelectedAnnotationColor.B);
            _colorTargetMode = 0;
            if (_textToolsPanel != null) _textToolsPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            UpdateTextPanelUi();
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有文本");
        }
        else if (toolKey == "Mosaic")
        {
            MosaicOptionsPanel.IsVisible = true;
            UpdateMosaicModeVisuals();
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有马赛克/模糊效果");
        }
        else if (toolKey == "Spotlight")
        {
            SpotlightOptionsPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有聚光灯");
        }
        else if (toolKey == "Counter")
        {
            CounterOptionsPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有序号");
        }

        UpdateSpotlightKindVisuals();

        bool hasToolSpecific = BrushOptionsPanel.IsVisible || ArrowOptionsPanel.IsVisible || RectangleOptionsPanel.IsVisible || (_textToolsPanel?.IsVisible ?? false) || MosaicOptionsPanel.IsVisible || SpotlightOptionsPanel.IsVisible || CounterOptionsPanel.IsVisible;
        SepAfterStyle.IsVisible = hasToolSpecific && (CommonThicknessPanel.IsVisible || CommonColorPanel.IsVisible);
        SepAfterSize.IsVisible = CommonThicknessPanel.IsVisible && CommonColorPanel.IsVisible;
        SepBeforeClear.IsVisible = hasOptions;
        BtnClearCurrentTool.IsVisible = hasOptions;

        UpdateToolOptionLabels();
        PositionToolOptionsBar();
    }

    private System.Drawing.Color GetCurrentTargetColor()
    {
        if (_activeToolKey == "Text" || _selectedText != null)
        {
            Color c = _colorTargetMode switch
            {
                1 => _textFillColor,
                2 => _textStrokeColor,
                _ => Color.FromArgb(SelectedAnnotationColor.A, SelectedAnnotationColor.R, SelectedAnnotationColor.G, SelectedAnnotationColor.B),
            };
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        return SelectedAnnotationColor;
    }

    private async void OnCustomColorClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        System.Drawing.Color current = GetCurrentTargetColor();
        Color? picked = await ColorPickerWindow.PickAsync(this, Color.FromArgb(current.A, current.R, current.G, current.B));
        if (picked.HasValue)
        {
            Color c = picked.Value;
            ApplyColorToCurrentTarget(System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B));
        }
    }

    private void OnColorSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        string? hex = button.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            Avalonia.Media.Color c = Avalonia.Media.Color.Parse(hex);
            ApplyColorToCurrentTarget(System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.OnColorSwatchClick: 解析颜色失败 " + hex + "，" + ex.Message);
        }
    }

    private void OnBrushLineStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ArrowStyleMenu.IsVisible = false;
        ToggleMenuNearButton(BrushLineStyleMenu, BtnBrushLineStyle);
    }

    private void OnArrowStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BrushLineStyleMenu.IsVisible = false;
        ToggleMenuNearButton(ArrowStyleMenu, BtnArrowStyle);
    }

    private void OnBrushSolidItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedBrushLineStyle = AnnotationLineStyle.Solid;
        BrushLineStyleMenu.IsVisible = false;
        UpdateStyleOptionVisuals();
    }

    private void OnBrushDashItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedBrushLineStyle = AnnotationLineStyle.DashLarge;
        BrushLineStyleMenu.IsVisible = false;
        UpdateStyleOptionVisuals();
    }

    private void OnArrowDesignedItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedArrowStyle = ArrowAnnotationStyle.Designed;
        ArrowStyleMenu.IsVisible = false;
        UpdateStyleOptionVisuals();
    }

    private void OnArrowSharpItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedArrowStyle = ArrowAnnotationStyle.Sharp;
        ArrowStyleMenu.IsVisible = false;
        UpdateStyleOptionVisuals();
    }

    private void ToggleMenuNearButton(Control menu, Control anchor)
    {
        bool show = !menu.IsVisible;
        BrushLineStyleMenu.IsVisible = false;
        ArrowStyleMenu.IsVisible = false;
        if (!show) return;

        menu.IsVisible = true;
        menu.Measure(Size.Infinity);
        Size menuSize = menu.DesiredSize;
        Point? pt = anchor.TranslatePoint(new Point(anchor.Bounds.Width - menuSize.Width, anchor.Bounds.Height + 4), OverlayCanvas);
        if (!pt.HasValue)
        {
            pt = new Point(Canvas.GetLeft(ToolOptionsBar), Canvas.GetTop(ToolOptionsBar) + ToolOptionsBar.Bounds.Height + 4);
        }

        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double left = Clamp(pt.Value.X, OverlayMargin, Math.Max(OverlayMargin, screenW - menuSize.Width - OverlayMargin));
        double top = pt.Value.Y;
        if (top + menuSize.Height + OverlayMargin > screenH)
        {
            Point? above = anchor.TranslatePoint(new Point(anchor.Bounds.Width - menuSize.Width, -menuSize.Height - 4), OverlayCanvas);
            if (above.HasValue) top = above.Value.Y;
        }
        top = Clamp(top, OverlayMargin, Math.Max(OverlayMargin, screenH - menuSize.Height - OverlayMargin));
        Canvas.SetLeft(menu, left);
        Canvas.SetTop(menu, top);
    }

    private void OnShapeKindClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedShapeKind = _selectedShapeKind == ShapeAnnotationKind.Rectangle ? ShapeAnnotationKind.Ellipse : ShapeAnnotationKind.Rectangle;
        UpdateStyleOptionVisuals();
    }

    private void OnShapeLineStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedShapeLineStyle = _selectedShapeLineStyle == AnnotationLineStyle.Solid ? AnnotationLineStyle.DashLarge : AnnotationLineStyle.Solid;
        UpdateStyleOptionVisuals();
    }

    private static void ToggleActive(Button button)
    {
        if (button.Classes.Contains("active")) button.Classes.Remove("active");
        else button.Classes.Add("active");
    }

    private IBrush GetCurrentTargetBrush()
    {
        System.Drawing.Color c = GetCurrentTargetColor();
        return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private void ApplyColorToCurrentTarget(System.Drawing.Color color)
    {
        if (_activeToolKey == "Text" || _selectedText != null)
        {
            var textColor = Color.FromArgb(color.A, color.R, color.G, color.B);
            if (_colorTargetMode == 0)
            {
                // 文本前景色：同步到全局 SelectedAnnotationColor
                _textForegroundColor = textColor;
                SelectedAnnotationColor = color;
            }
            else if (_colorTargetMode == 1) _textFillColor = textColor;
            else if (_colorTargetMode == 2) _textStrokeColor = textColor;
            RequestTextSnapshot();
            ApplyTextSettingsToSelected();
            UpdateToolOptionLabels();
            UpdateTextPanelUi();
            return;
        }

        // 所有非文本工具（画笔/箭头/矩形/序号/聚光灯）统一设置 SelectedAnnotationColor
        SelectedAnnotationColor = color;
        AnnotationCommonSettingsChanged?.Invoke(this, EventArgs.Empty);
        UpdateToolOptionLabels();
        if (_activeToolKey == "Spotlight")
        {
            RaiseSpotlightSettingsChanged();
        }
    }

    private void OnMosaicManualClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideAutoMosaicScanAnimation(true);
        SetMosaicAutoMode(false);
        ManualMosaicRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMosaicAutoClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetMosaicAutoMode(true);
        AutoMosaicRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnBlurTypeBlurClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedBlurMode = BlurMode.Blur;
        UpdateMosaicModeVisuals();
        BlurSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBlurTypeMosaicClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedBlurMode = BlurMode.Mosaic;
        UpdateMosaicModeVisuals();
        BlurSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutoMosaicBusy(bool busy)
    {
        if (BtnMosaicAuto == null || TxtMosaicAuto == null) return;
        BtnMosaicAuto.IsEnabled = !busy;
        TxtMosaicAuto.Text = busy ? "识别中" : "自动";
        if (busy) ShowAutoMosaicScanAnimation();
        else HideAutoMosaicScanAnimation();
    }

    private void ShowAutoMosaicScanAnimation()
    {
        if (!_lastHasSelection || _lastRegionDip.Width <= 0 || _lastRegionDip.Height <= 0) return;

        _isAutoMosaicScanning = true;
        _autoMosaicScanHeight = _lastRegionDip.Height;
        _autoMosaicScanStartedTick = Environment.TickCount64;
        AutoMosaicScanOverlay.Width = _lastRegionDip.Width;
        AutoMosaicScanOverlay.Height = _lastRegionDip.Height;
        AutoMosaicScanFill.Width = _lastRegionDip.Width;
        AutoMosaicScanFill.Height = _lastRegionDip.Height;
        AutoMosaicScanFill.RenderTransform = _autoMosaicScanTransform;
        AutoMosaicScanOverlay.Opacity = 1;
        AutoMosaicScanOverlay.IsVisible = true;
        _autoMosaicScanTransform.Y = -_autoMosaicScanHeight;
        Canvas.SetLeft(AutoMosaicScanOverlay, _lastRegionDip.X);
        Canvas.SetTop(AutoMosaicScanOverlay, _lastRegionDip.Y);
        AutoMosaicScanOverlay.ZIndex = 900;
        EnsureAutoMosaicScanTimer();
        _autoMosaicScanTimer?.Start();
    }

    private void EnsureAutoMosaicScanTimer()
    {
        if (_autoMosaicScanTimer != null) return;
        _autoMosaicScanTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _autoMosaicScanTimer.Tick += (_, _) =>
        {
            if (!_isAutoMosaicScanning || !AutoMosaicScanOverlay.IsVisible)
            {
                _autoMosaicScanTimer.Stop();
                return;
            }

            double elapsed = Math.Max(0, Environment.TickCount64 - _autoMosaicScanStartedTick);
            double progress = Math.Clamp(elapsed / 800.0, 0.0, 1.0);
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            _autoMosaicScanTransform.Y = -_autoMosaicScanHeight * (1.0 - eased);
            if (progress >= 1.0)
            {
                _autoMosaicScanTransform.Y = 0;
                _autoMosaicScanTimer.Stop();
            }
        };
    }

    private async void HideAutoMosaicScanAnimation(bool immediate = false)
    {
        _isAutoMosaicScanning = false;
        _autoMosaicScanTimer?.Stop();
        if (AutoMosaicScanOverlay == null || !AutoMosaicScanOverlay.IsVisible) return;

        if (immediate)
        {
            AutoMosaicScanOverlay.IsVisible = false;
            AutoMosaicScanOverlay.Opacity = 1;
            _autoMosaicScanTransform.Y = 0;
            return;
        }

        double startOpacity = AutoMosaicScanOverlay.Opacity;
        long fadeStarted = Environment.TickCount64;
        while (!_isAutoMosaicScanning && AutoMosaicScanOverlay.IsVisible)
        {
            double progress = Math.Clamp((Environment.TickCount64 - fadeStarted) / 200.0, 0.0, 1.0);
            AutoMosaicScanOverlay.Opacity = startOpacity * (1.0 - progress);
            if (progress >= 1.0) break;
            await Task.Delay(16);
        }

        if (!_isAutoMosaicScanning)
        {
            AutoMosaicScanOverlay.IsVisible = false;
            AutoMosaicScanOverlay.Opacity = 1;
            _autoMosaicScanTransform.Y = 0;
        }
    }

    private void SetMosaicAutoMode(bool isAuto)
    {
        _isAutoMosaicMode = isAuto;
        UpdateMosaicModeVisuals();
    }

    private void UpdateMosaicModeVisuals()
    {
        if (BtnMosaicManual == null || BtnMosaicAuto == null || BtnBlurTypeBlur == null || BtnBlurTypeMosaic == null) return;

        if (_isAutoMosaicMode)
        {
            BtnMosaicAuto.Classes.Add("active");
            BtnMosaicManual.Classes.Remove("active");
        }
        else
        {
            BtnMosaicManual.Classes.Add("active");
            BtnMosaicAuto.Classes.Remove("active");
        }

        if (_selectedBlurMode == BlurMode.Mosaic)
        {
            BtnBlurTypeMosaic.Classes.Add("active");
            BtnBlurTypeBlur.Classes.Remove("active");
        }
        else
        {
            BtnBlurTypeBlur.Classes.Add("active");
            BtnBlurTypeMosaic.Classes.Remove("active");
        }
    }

    private void OnSpotlightRectClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedSpotlightKind = SpotlightShapeKind.Rectangle;
        UpdateSpotlightKindVisuals();
        RaiseSpotlightSettingsChanged();
    }

    private void OnSpotlightCircleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedSpotlightKind = SpotlightShapeKind.Ellipse;
        UpdateSpotlightKindVisuals();
        RaiseSpotlightSettingsChanged();
    }

    private void UpdateSpotlightKindVisuals()
    {
        if (BtnSpotlightRect == null || BtnSpotlightCircle == null) return;
        if (_selectedSpotlightKind == SpotlightShapeKind.Rectangle)
        {
            BtnSpotlightRect.Classes.Add("active");
            BtnSpotlightCircle.Classes.Remove("active");
        }
        else
        {
            BtnSpotlightCircle.Classes.Add("active");
            BtnSpotlightRect.Classes.Remove("active");
        }
    }

    private void OnSpotlightStrokeWheel(object? sender, PointerWheelEventArgs e)
    {
        double delta = e.Delta.Y > 0 ? 1.0 : -1.0;
        SliderSpotlightStrokeThickness.Value = Math.Clamp(Math.Round(SliderSpotlightStrokeThickness.Value + delta), SliderSpotlightStrokeThickness.Minimum, SliderSpotlightStrokeThickness.Maximum);
        e.Handled = true;
        UpdateToolOptionLabels();
        RaiseSpotlightSettingsChanged();
    }

    private void OnClearCurrentToolClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearCurrentToolRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnStrokeThicknessWheel(object? sender, PointerWheelEventArgs e)
    {
        double delta = e.Delta.Y > 0 ? 1.0 : -1.0;
        SliderStrokeThickness.Value = Math.Clamp(Math.Round(SliderStrokeThickness.Value + delta), SliderStrokeThickness.Minimum, SliderStrokeThickness.Maximum);
        e.Handled = true;
        UpdateToolOptionLabels();
        AnnotationCommonSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ForceTopmostSafely(string reason)
    {
        try
        {
            IntPtr hwnd = GetWindowHwnd();
            if (hwnd == IntPtr.Zero) return;

            if (!WindowHelper.ForceTopmost(hwnd))
            {
                AppLogger.Warn("CaptureWindow.ForceTopmostSafely: 置顶失败，reason=" + reason);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.ForceTopmostSafely: 置顶异常，reason=" + reason + "，" + ex.Message);
        }
    }

    private void OnBrushClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BrushRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnArrowClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ArrowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRectangleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RectangleRequested?.Invoke(this, EventArgs.Empty);
    }

    // ==========================================
    // 文本工具选项按钮事件将在 Phase 2 重建
    // ==========================================

    private void OnMosaicClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        MosaicRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpotlightClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SpotlightRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCounterClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CounterRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnColorPickerClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ColorPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRedoClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RedoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearAllAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearAllAnnotationsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLongScrollClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        LongScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOcrClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OcrRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnStickerClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StickerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ConfirmRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>更新撤销按钮的可用状态：无可撤销历史时置灰不可点。</summary>
    public void SetUndoEnabled(bool enabled)
    {
        if (BtnUndo != null) BtnUndo.IsEnabled = enabled;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateToolOptionLabels()
    {
        if (SliderStrokeThickness == null || SliderMosaicBlockSize == null || SliderSpotlightDarkness == null || SliderCounterRadius == null) return;
        TxtStrokeThickness.Text = Math.Round(SliderStrokeThickness.Value).ToString();
        TxtMosaicBlockSize.Text = SelectedBlurUiLevel.ToString();
        TxtSpotlightDarkness.Text = Math.Round(SliderSpotlightDarkness.Value).ToString();
        TxtCounterRadius.Text = Math.Round(SliderCounterRadius.Value).ToString();
        if (TextSpotlightStrokeValue != null) TextSpotlightStrokeValue.Text = Math.Round(SliderSpotlightStrokeThickness.Value).ToString();

        double previewSize = Math.Clamp(SliderStrokeThickness.Value, 2.0, 18.0);
        StrokeThicknessPreview.Width = previewSize;
        StrokeThicknessPreview.Height = previewSize;
        var c = SelectedAnnotationColor;
        StrokeThicknessPreview.Fill = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        if (SpotlightStrokeCircle != null) SpotlightStrokeCircle.Fill = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        CurrentColorPreview.Background = GetCurrentTargetBrush();
        UpdateStyleOptionVisuals();
    }

    private static void HighlightTargetButton(Button? btn, bool active)
    {
        if (btn == null) return;
        if (active)
        {
            if (!btn.Classes.Contains("active")) btn.Classes.Add("active");
        }
        else
        {
            btn.Classes.Remove("active");
        }
    }

    private void UpdateStyleOptionVisuals()
    {
        if (BrushLineStylePreview != null)
        {
            BrushLineStylePreview.StrokeDashArray = _selectedBrushLineStyle == AnnotationLineStyle.DashLarge ? new AvaloniaList<double> { 2, 2 } : null;
            ToolTip.SetTip(BtnBrushLineStyle, _selectedBrushLineStyle == AnnotationLineStyle.DashLarge ? "画笔线型：虚线" : "画笔线型：实线");
        }
        if (ShapeLineStylePreview != null)
        {
            ShapeLineStylePreview.StrokeDashArray = _selectedShapeLineStyle == AnnotationLineStyle.DashLarge ? new AvaloniaList<double> { 2, 2 } : null;
            ToolTip.SetTip(BtnShapeLineStyle, _selectedShapeLineStyle == AnnotationLineStyle.DashLarge ? "形状线型：虚线" : "形状线型：实线");
        }
        if (ArrowStyleHeadPreview != null)
        {
            if (_selectedArrowStyle == ArrowAnnotationStyle.Sharp)
            {
                ArrowStyleShaftPreview.Data = Geometry.Parse("M5,9 L43,9 L43,6 L68,12 L43,18 L43,15 L5,15 Z");
                ArrowStyleHeadPreview.Data = Geometry.Parse("M0,0 Z");
            }
            else
            {
                ArrowStyleShaftPreview.Data = Geometry.Parse("M5,12 C18,9 34,9 47,9 L47,6 L68,12 L47,18 L47,15 C34,15 18,15 5,12 Z");
                ArrowStyleHeadPreview.Data = Geometry.Parse("M0,0 Z");
            }
            ToolTip.SetTip(BtnArrowStyle, _selectedArrowStyle == ArrowAnnotationStyle.Sharp ? "箭头样式：尖锐箭头" : "箭头样式：设计箭头");
        }
        if (ShapeKindPreview != null)
        {
            ShapeKindPreview.Data = _selectedShapeKind == ShapeAnnotationKind.Ellipse
                ? Geometry.Parse("M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z")
                : Geometry.Parse("M7,4 H17 A3,3 0 0 1 20,7 V17 A3,3 0 0 1 17,20 H7 A3,3 0 0 1 4,17 V7 A3,3 0 0 1 7,4 Z");
            ToolTip.SetTip(BtnShapeKind, _selectedShapeKind == ShapeAnnotationKind.Ellipse ? "形状：圆形" : "形状：矩形");
        }
    }

    public void BeginSelectionTransform()
    {
        InfoPill.IsVisible = false;
        ActionBar.IsVisible = false;
        ToolOptionsBar.IsVisible = false;
    }

    public void EndSelectionTransform(Rect regionDip, System.Drawing.Rectangle screenRect)
    {
        UpdateOverlay(regionDip, screenRect, true, true);
    }

    public void UpdateOverlay(Rect regionDip, System.Drawing.Rectangle? screenRect, bool hasSelection, bool showActionBar = true)
    {
        _lastRegionDip = regionDip;
        _lastHasSelection = hasSelection && regionDip.Width > 0 && regionDip.Height > 0 && screenRect != null;
        if (!_lastHasSelection)
        {
            HideAutoMosaicScanAnimation(true);
            InfoPill.IsVisible = false;
            ActionBar.IsVisible = false;
            ToolOptionsBar.IsVisible = false;
            return;
        }
        TxtSelectionSize.Text = screenRect!.Value.Width + " × " + screenRect.Value.Height;
        InfoPill.IsVisible = true;
        ActionBar.IsVisible = showActionBar;
        if (!showActionBar) ToolOptionsBar.IsVisible = false;

        InfoPill.Measure(Size.Infinity);
        ActionBar.Measure(Size.Infinity);
        Size infoSize = InfoPill.DesiredSize;
        Size actionSize = ActionBar.DesiredSize;
        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double infoX = Clamp(regionDip.Right - infoSize.Width, OverlayMargin, Math.Max(OverlayMargin, screenW - infoSize.Width - OverlayMargin));
        double infoY = regionDip.Y - infoSize.Height - OverlayGap;
        if (infoY < OverlayMargin) infoY = regionDip.Y + OverlayGap;
        infoY = Clamp(infoY, OverlayMargin, Math.Max(OverlayMargin, screenH - infoSize.Height - OverlayMargin));
        double actionX = Clamp(regionDip.Right - actionSize.Width, OverlayMargin, Math.Max(OverlayMargin, screenW - actionSize.Width - OverlayMargin));
        double actionY = regionDip.Y + regionDip.Height + OverlayGap;
        if (actionY + actionSize.Height + OverlayMargin > screenH) actionY = regionDip.Y - actionSize.Height - OverlayGap;
        actionY = Clamp(actionY, OverlayMargin, Math.Max(OverlayMargin, screenH - actionSize.Height - OverlayMargin));
        Canvas.SetLeft(InfoPill, infoX);
        Canvas.SetTop(InfoPill, infoY);
        Canvas.SetLeft(ActionBar, actionX);
        Canvas.SetTop(ActionBar, actionY);
        PositionToolOptionsBar();
    }

    private void PositionToolOptionsBar()
    {
        if (!_lastHasSelection || !ActionBar.IsVisible || !ToolOptionsBar.IsVisible) return;
        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double actionX = Canvas.GetLeft(ActionBar);
        double actionY = Canvas.GetTop(ActionBar);
        if (double.IsNaN(actionX) || double.IsNaN(actionY)) return;
        ActionBar.Measure(Size.Infinity);
        ToolOptionsBar.Measure(Size.Infinity);
        Size actionSize = ActionBar.DesiredSize;
        Size optionsSize = ToolOptionsBar.DesiredSize;
        double optionsX = Clamp(actionX + actionSize.Width - optionsSize.Width, OverlayMargin, Math.Max(OverlayMargin, screenW - optionsSize.Width - OverlayMargin));
        double optionsY = actionY + actionSize.Height + 6;
        if (optionsY + optionsSize.Height + OverlayMargin > screenH) optionsY = actionY - optionsSize.Height - 6;
        optionsY = Clamp(optionsY, OverlayMargin, Math.Max(OverlayMargin, screenH - optionsSize.Height - OverlayMargin));
        Canvas.SetLeft(ToolOptionsBar, optionsX);
        Canvas.SetTop(ToolOptionsBar, optionsY);
    }

    public IntPtr GetWindowHwnd()
    {
        try
        {
            var handle = TryGetPlatformHandle();
            return handle?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private async Task ForceTopmostAgainAsync()
    {
        try
        {
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => ForceTopmostSafely("Delayed"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.ForceTopmostAgainAsync: 延迟置顶异常：" + ex.Message);
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        try
        {
            System.Drawing.Rectangle virt = MonitorHelper.GetVirtualScreenRect();
            double scale = SafeScaling;

            // Position 是物理像素（PixelPoint），Width/Height 是 DIP
            Position = new PixelPoint(virt.X, virt.Y);
            Width = virt.Width / scale;
            Height = virt.Height / scale;

            UpdateAnnotationTransform();
            ForceTopmostSafely("Opened");
            _ = ForceTopmostAgainAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.OnWindowOpened: 铺满虚拟屏幕失败：" + ex.Message);
        }
    }

    // ==========================================
    // 文本工具实现
    // ==========================================

    private void RequestTextSnapshot()
    {
        if (_suppressTextSnapshotEvents) return;
        TextSnapshotRequested?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<TextLayerSnapshot> CreateTextSnapshot()
    {
        return _textLayers.Select(info => new TextLayerSnapshot
        {
            Text = info.TextBox?.Text ?? string.Empty,
            Left = Canvas.GetLeft(info.Root),
            Top = Canvas.GetTop(info.Root),
            MaxWidth = info.TextBox?.MaxWidth ?? 0,
            FontSize = info.FontSize,
            IsBold = info.IsBold,
            IsItalic = info.IsItalic,
            HasStroke = info.HasStroke,
            HasBackground = info.HasBackground,
            FontFamily = info.FontFamily,
            Color = info.Color,
            FillColor = info.FillColor,
            StrokeColor = info.StrokeColor,
            StrokeThickness = info.StrokeThickness,
            IsStrokeAuto = info.IsStrokeAuto,
            FillOpacity = info.FillOpacity,
            FillCornerRadius = info.FillCornerRadius,
            FillPadding = info.FillPadding,
            RotationAngle = info.RotationAngle,
        }).ToList();
    }

    public void RestoreTextSnapshot(IReadOnlyList<TextLayerSnapshot>? snapshots)
    {
        _suppressTextSnapshotEvents = true;
        try
        {
            foreach (var info in _textLayers.ToList())
            {
                if (info.Root != null && OverlayCanvas.Children.Contains(info.Root))
                    OverlayCanvas.Children.Remove(info.Root);
            }
            _textLayers.Clear();
            _selectedText = null;
            _isDraggingSelectedText = false;
            _isRotatingText = false;
            _rotatingTextInfo = null;

            if (snapshots == null) return;
            foreach (var snapshot in snapshots)
            {
                var info = CreateTextLayer();
                info.TextBox.Text = snapshot.Text;
                Canvas.SetLeft(info.Root, snapshot.Left);
                Canvas.SetTop(info.Root, snapshot.Top);
                if (snapshot.MaxWidth > 0) info.TextBox.MaxWidth = snapshot.MaxWidth;
                info.FontSize = snapshot.FontSize;
                info.IsBold = snapshot.IsBold;
                info.IsItalic = snapshot.IsItalic;
                info.HasStroke = snapshot.HasStroke;
                info.HasBackground = snapshot.HasBackground;
                info.FontFamily = snapshot.FontFamily;
                info.Color = snapshot.Color;
                info.FillColor = snapshot.FillColor;
                info.StrokeColor = snapshot.StrokeColor;
                info.StrokeThickness = snapshot.StrokeThickness;
                info.IsStrokeAuto = snapshot.IsStrokeAuto;
                info.FillOpacity = snapshot.FillOpacity;
                info.FillCornerRadius = snapshot.FillCornerRadius;
                info.FillPadding = snapshot.FillPadding;
                info.RotationAngle = snapshot.RotationAngle;
                ApplyTextRotation(info);
                UpdateTextView(info);
                OverlayCanvas.Children.Add(info.Root);
            }
        }
        finally
        {
            _suppressTextSnapshotEvents = false;
        }
    }

    public bool DeleteSelectedText()
    {
        if (_selectedText == null) return false;
        DeleteTextLayer(_selectedText);
        return true;
    }

    /// <summary>CaptureSession 设置文本工具是否激活</summary>
    public bool IsTextToolActive
    {
        get => _isTextToolActive;
        set
        {
            _isTextToolActive = value;
            if (!value)
            {
                CloseTextPopups();
                EndActiveTextEditing("ToolDeactivated");
            }
        }
    }

    /// <summary>处理文本工具点击交互。返回 true 表示已处理（无需创建新文本）。</summary>
    public bool TryHandleTextToolInteraction(Point dip, int clickCount)
    {
        // 在点击位置查找已有文本
        // 注意：这里不能直接用 FindTextInfoFromSource，因为事件源来自 CaptureSession
        // 需要通过坐标查找
        TextLayerInfo? hit = FindTextAtPoint(dip);
        if (hit != null)
        {
            if (_activeToolKey != "Text" || _selectedText != hit)
            {
                SelectText(hit);
            }

            if (clickCount >= 2 && hit.TextBox != null)
            {
                // 双击 → 进入编辑模式
                if (!_hasPendingTextEditSnapshot)
                {
                    RequestTextSnapshot();
                    _hasPendingTextEditSnapshot = true;
                }
                BeginTextEditing(hit);
            }
            else
            {
                // 旧版语义：双击才编辑，单击永远移动图层。
                // 如果当前 TextBox 正在编辑，单击移动前先结束编辑，避免 TextBox 抢输入焦点/选择逻辑。
                if (hit.TextBox != null)
                {
                    hit.TextBox.IsHitTestVisible = false;
                    _hasPendingTextEditSnapshot = false;
                    OverlayCanvas.Focus();
                }

                // 单击 → 开始拖拽。后续移动只做纯平移，不再改文本 MaxWidth。
                RequestTextSnapshot();
                _isDraggingSelectedText = true;
                _textDragStartPoint = dip;
                double startLeft = Canvas.GetLeft(hit.Root);
                double startTop = Canvas.GetTop(hit.Root);
                if (double.IsNaN(startLeft)) startLeft = 0;
                if (double.IsNaN(startTop)) startTop = 0;
                _textDragStartPosition = new Point(startLeft, startTop);
            }
            return true; // 已处理
        }
        return false; // 空白区域，让 CaptureSession 创建新文本
    }

    /// <summary>通过坐标查找文本图层（供 CaptureSession 在其他标注工具下 Ctrl+Click 切换使用）</summary>
    public TextLayerInfo? FindTextAtPoint(Point dip)
    {
        // 从后往前遍历（后添加的在上面），找到命中测试通过的文本
        for (int i = _textLayers.Count - 1; i >= 0; i--)
        {
            var info = _textLayers[i];
            if (info.Root == null) continue;
            double left = Canvas.GetLeft(info.Root);
            double top = Canvas.GetTop(info.Root);
            double w = info.Root.Bounds.Width;
            double h = info.Root.Bounds.Height;
            if (w <= 0) w = 80;  // fallback for unmeasured
            if (h <= 0) h = 30;
            Point local = ToTextLocalPoint(info, dip, left, top, w, h);
            if (local.X >= 0 && local.X <= w && local.Y >= 0 && local.Y <= h)
            {
                return info;
            }
        }
        return null;
    }

    private static Point ToTextLocalPoint(TextLayerInfo info, Point dip, double left, double top, double width, double height)
    {
        double x = dip.X - left;
        double y = dip.Y - top;
        double angle = info.RotationAngle;
        if (Math.Abs(angle) < 0.01) return new Point(x, y);

        double cx = width / 2.0;
        double cy = height / 2.0;
        double radians = -angle * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double dx = x - cx;
        double dy = y - cy;
        return new Point(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    private void ApplyTextRotation(TextLayerInfo info)
    {
        double w = Math.Max(1, info.Root.Bounds.Width);
        double h = Math.Max(1, info.Root.Bounds.Height);
        info.Root.RenderTransform = new RotateTransform(info.RotationAngle, w / 2.0, h / 2.0);
        info.Root.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
    }

    private Rect GetTextValidRect()
    {
        if (_lastHasSelection && _lastRegionDip.Width > 1 && _lastRegionDip.Height > 1)
            return _lastRegionDip;
        return new Rect(0, 0, Math.Max(OverlayCanvas.Bounds.Width, 1), Math.Max(OverlayCanvas.Bounds.Height, 1));
    }

    private void MoveSelectedTextWithinBounds(TextLayerInfo info, Point currentPos)
    {
        if (info.Root == null || info.TextBox == null) return;

        Rect valid = GetTextValidRect();
        double dx = currentPos.X - _textDragStartPoint.X;
        double dy = currentPos.Y - _textDragStartPoint.Y;
        double candidateLeft = _textDragStartPosition.X + dx;
        double candidateTop = _textDragStartPosition.Y + dy;

        // 移动文本必须是纯平移：不能在拖动过程中修改 MaxWidth。
        // 否则 TextBox 会重新排版，Root.Bounds / 命中框 / 选中框都会变化，表现为拖动时文本框变大或跳动。
        info.Root.UpdateLayout();
        double width = info.Root.Bounds.Width;
        double height = info.Root.Bounds.Height;
        if (width <= 0) width = Math.Max(info.TextBox.Bounds.Width, 40.0);
        if (height <= 0) height = Math.Max(info.TextBox.Bounds.Height, info.FontSize * 1.35);

        if (width <= valid.Width)
            candidateLeft = Math.Clamp(candidateLeft, valid.Left, valid.Right - width);
        else
            candidateLeft = valid.Left;

        if (height <= valid.Height)
            candidateTop = Math.Clamp(candidateTop, valid.Top, valid.Bottom - height);
        else
            candidateTop = valid.Top;

        Canvas.SetLeft(info.Root, candidateLeft);
        Canvas.SetTop(info.Root, candidateTop);
        UpdateTextSelectionFrame(info);
    }

    // ──────────────────────────────────────────
    // 全局指针事件（仅处理拖拽/旋转的连续性）
    // ──────────────────────────────────────────

    private void OnTextEditingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            _textCtrlKeyDown = true;
            return;
        }

        bool isEnter = e.Key == Key.Enter || e.Key == Key.Return;
        if (!isEnter) return;

        bool ctrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control) || _textCtrlKeyDown;
        if (!HasActiveTextEditing)
        {
            AppLogger.Info("CaptureWindow.TextKeyDown: Enter ignored, no active text editing, ctrl=" + ctrlPressed + ", keyModifiers=" + e.KeyModifiers + ", handled=" + e.Handled);
            return;
        }

        AppLogger.Info("CaptureWindow.TextKeyDown: Enter while editing, ctrl=" + ctrlPressed + ", keyModifiers=" + e.KeyModifiers + ", handled=" + e.Handled + ", selectedText=" + (_selectedText != null) + ", textLayers=" + _textLayers.Count);
        if (!ctrlPressed) return;

        EndActiveTextEditing("WindowKeyDown");
        e.Handled = true;
    }

    private void OnTextEditingKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            _textCtrlKeyDown = false;
    }

    private void OnOverlayCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingSelectedText && _selectedText != null)
        {
            var currentPos = e.GetPosition(OverlayCanvas);
            MoveSelectedTextWithinBounds(_selectedText, currentPos);
            e.Handled = true;
            return;
        }

        if (_isRotatingText && _rotatingTextInfo != null)
        {
            var currentPos = e.GetPosition(OverlayCanvas);
            double left = Canvas.GetLeft(_rotatingTextInfo.Root);
            double top = Canvas.GetTop(_rotatingTextInfo.Root);
            double cx = left + _rotatingTextInfo.Root.Bounds.Width / 2;
            double cy = top + _rotatingTextInfo.Root.Bounds.Height / 2;
            double startAngle = Math.Atan2(_rotateStartPoint.Y - cy, _rotateStartPoint.X - cx) * 180.0 / Math.PI;
            double currentAngle = Math.Atan2(currentPos.Y - cy, currentPos.X - cx) * 180.0 / Math.PI;
            _rotatingTextInfo.RotationAngle = _rotateStartAngle + currentAngle - startAngle;
            ApplyTextRotation(_rotatingTextInfo);
            e.Handled = true;
        }
    }

    private void OnOverlayCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingSelectedText || _isRotatingText)
        {
            try { e.Pointer.Capture(null); } catch { }
            e.Handled = true;
        }
        _isDraggingSelectedText = false;
        _isRotatingText = false;
        _rotatingTextInfo = null;
    }

    // ──────────────────────────────────────────
    // 文本工具按钮事件
    // ──────────────────────────────────────────

    private void OnTextClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TextRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>初始化文本工具所有UI控件引用并绑定事件</summary>
    public void InitTextToolControls()
    {
        _textToolsPanel = this.FindControl<StackPanel>("TextToolsPanel");
        _textSizeHost = this.FindControl<Grid>("TextSizeHost");
        _btnTextBold = this.FindControl<Button>("BtnTextBold");
        _btnTextItalic = this.FindControl<Button>("BtnTextItalic");
        _btnTextStroke = this.FindControl<Button>("BtnTextStroke");
        _btnTextBackground = this.FindControl<Button>("BtnTextBackground");
        _btnTargetStrokeColor = this.FindControl<Button>("BtnTargetStrokeColor");
        _btnTargetStrokeText = this.FindControl<Button>("BtnTargetStrokeText");
        _btnTargetBackgroundColor = this.FindControl<Button>("BtnTargetBackgroundColor");
        _btnTargetBackgroundText = this.FindControl<Button>("BtnTargetBackgroundText");
        _textBackgroundPopup = this.FindControl<Popup>("TextBackgroundPopup");
        _textStrokePopup = this.FindControl<Popup>("TextStrokePopup");
        _textSizeValueText = this.FindControl<TextBlock>("TextSizeValueText");
        _textStrokeSizeValue = this.FindControl<TextBlock>("TextStrokeSizeValue");
        _textFillOpacityValue = this.FindControl<TextBlock>("TextFillOpacityValue");
        _textFillCornerValue = this.FindControl<TextBlock>("TextFillCornerValue");
        _textFillPaddingValue = this.FindControl<TextBlock>("TextFillPaddingValue");
        _chkStrokeEnable = this.FindControl<CheckBox>("ChkStrokeEnable");
        _chkBackgroundEnable = this.FindControl<CheckBox>("ChkBackgroundEnable");
        _sliderStrokeSize = this.FindControl<Slider>("SliderStrokeSize");
        _sliderFillOpacity = this.FindControl<Slider>("SliderFillOpacity");
        _sliderFillCorner = this.FindControl<Slider>("SliderFillCorner");
        _sliderFillPadding = this.FindControl<Slider>("SliderFillPadding");

        // 字号区域滚轮/点击事件
        if (_textSizeHost != null)
        {
            _textSizeHost.PointerWheelChanged += OnTextSizeHostWheel;
            _textSizeHost.PointerPressed += OnTextSizeHostPressed;
            EnsureTextSizePopup();
        }

        // 描边 Popup 事件
        if (_chkStrokeEnable != null)
            _chkStrokeEnable.IsCheckedChanged += (_, _) => ToggleStrokeEnable();
        if (_sliderStrokeSize != null)
            _sliderStrokeSize.ValueChanged += (_, _) => OnStrokeSizeSliderChanged();

        // 填充 Popup 事件
        if (_chkBackgroundEnable != null)
            _chkBackgroundEnable.IsCheckedChanged += (_, _) => ToggleBackgroundEnable();
        if (_sliderFillOpacity != null)
            _sliderFillOpacity.ValueChanged += (_, _) => OnFillOpacitySliderChanged();
        if (_sliderFillCorner != null)
            _sliderFillCorner.ValueChanged += (_, _) => OnFillCornerSliderChanged();
        if (_sliderFillPadding != null)
            _sliderFillPadding.ValueChanged += (_, _) => OnFillPaddingSliderChanged();
    }

    private void EnsureTextSizePopup()
    {
        if (_textSizePopup != null || _textSizeHost == null) return;
        var panel = new StackPanel { Width = 92 };
        int[] sizes = { 8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 96, 128, 160, 200 };
        foreach (int size in sizes)
        {
            var item = new Button
            {
                Content = size + " px",
                Tag = size,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 28,
                Padding = new Thickness(8, 3),
            };
            item.Click += (_, args) =>
            {
                if (item.Tag is int selectedSize) SetCurrentTextSize(selectedSize);
                if (_textSizePopup != null) _textSizePopup.IsOpen = false;
                args.Handled = true;
            };
            panel.Children.Add(item);
        }

        _textSizePopup = new Popup
        {
            PlacementTarget = _textSizeHost,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            VerticalOffset = 4,
            Child = new Border
            {
                Background = this.TryFindResource("SctPanelBrush", ActualThemeVariant, out var bg) && bg is IBrush bgBrush ? bgBrush : Brushes.White,
                BorderBrush = this.TryFindResource("SctBorderBrush", ActualThemeVariant, out var br) && br is IBrush borderBrush ? borderBrush : Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Child = panel,
            }
        };
    }

    private void OnTextSizeHostPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        EnsureTextSizePopup();
        if (_textSizePopup != null) _textSizePopup.IsOpen = !_textSizePopup.IsOpen;
        if (_textStrokePopup != null) _textStrokePopup.IsOpen = false;
        if (_textBackgroundPopup != null) _textBackgroundPopup.IsOpen = false;
    }

    private void CloseTextPopups()
    {
        if (_textSizePopup != null) _textSizePopup.IsOpen = false;
        if (_textStrokePopup != null) _textStrokePopup.IsOpen = false;
        if (_textBackgroundPopup != null) _textBackgroundPopup.IsOpen = false;
    }

    /// <summary>更新文本工具面板 UI 状态</summary>
    private void UpdateTextPanelUi()
    {
        try
        {
            if (_textSizeValueText != null)
                _textSizeValueText.Text = _currentTextSize.ToString("0");
        }
        catch { }

        try
        {
            // 按钮 active 状态（对齐旧版 IsChecked → 样式切换）
            SetClassActive(_btnTextBold, _isTextBold);
            SetClassActive(_btnTextItalic, _isTextItalic);
            SetClassActive(_btnTextStroke, _hasStroke);
            SetClassActive(_btnTextBackground, _hasBackground);
            SetClassActive(_btnTargetStrokeColor, _colorTargetMode == 2);
            SetClassActive(_btnTargetStrokeText, _colorTargetMode == 0);
            SetClassActive(_btnTargetBackgroundColor, _colorTargetMode == 1);
            SetClassActive(_btnTargetBackgroundText, _colorTargetMode == 0);

            // 描边 Popup 状态
            if (_sliderStrokeSize != null && _sliderStrokeSize.Value != _textStrokeThickness)
                _sliderStrokeSize.Value = _textStrokeThickness;
            if (_textStrokeSizeValue != null)
                _textStrokeSizeValue.Text = _textStrokeThickness.ToString("0");
            if (_chkStrokeEnable != null && _chkStrokeEnable.IsChecked != _hasStroke)
                _chkStrokeEnable.IsChecked = _hasStroke;

            // 填充 Popup 状态
            if (_sliderFillOpacity != null && Math.Abs(_sliderFillOpacity.Value - _textFillOpacity) > 0.1)
                _sliderFillOpacity.Value = _textFillOpacity;
            if (_textFillOpacityValue != null)
                _textFillOpacityValue.Text = ((int)_textFillOpacity).ToString();

            if (_sliderFillCorner != null && Math.Abs(_sliderFillCorner.Value - _textFillCornerRadius) > 0.1)
                _sliderFillCorner.Value = _textFillCornerRadius;
            if (_textFillCornerValue != null)
                _textFillCornerValue.Text = ((int)_textFillCornerRadius).ToString();

            if (_sliderFillPadding != null && Math.Abs(_sliderFillPadding.Value - _textFillPadding) > 0.1)
                _sliderFillPadding.Value = _textFillPadding;
            if (_textFillPaddingValue != null)
                _textFillPaddingValue.Text = ((int)_textFillPadding).ToString();
        }
        catch { }
    }

    private static void SetClassActive(Button? btn, bool active)
    {
        if (btn == null) return;
        if (active && !btn.Classes.Contains("active"))
            btn.Classes.Add("active");
        else if (!active && btn.Classes.Contains("active"))
            btn.Classes.Remove("active");
    }

    private void OnTextBoldClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestTextSnapshot();
        _isTextBold = !_isTextBold;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    private void OnTextItalicClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestTextSnapshot();
        _isTextItalic = !_isTextItalic;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    private void OnTextStrokeClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_textStrokePopup != null)
        {
            if (_textStrokePopup.IsOpen)
            {
                _textStrokePopup.IsOpen = false;
            }
            else
            {
                // 互斥：关闭填充 Popup
                if (_textBackgroundPopup != null && _textBackgroundPopup.IsOpen)
                    _textBackgroundPopup.IsOpen = false;
                _textStrokePopup.IsOpen = true;
                SetColorTarget(2); // 描边色
                if (_chkStrokeEnable != null)
                    _chkStrokeEnable.IsChecked = _hasStroke;
            }
        }
        UpdateTextPanelUi();
    }

    private void OnTextBackgroundClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        // 与旧版工具按钮体验对齐：点击“填充背景”按钮即启用填充；弹窗只用于细调颜色/透明度/圆角/内边距。
        if (!_hasBackground)
        {
            RequestTextSnapshot();
            _hasBackground = true;
            if (_chkBackgroundEnable != null) _chkBackgroundEnable.IsChecked = true;
            ApplyTextSettingsToSelected();
            AppLogger.Info("CaptureWindow.TextBackground: 点击按钮启用填充，selectedText=" + (_selectedText != null));
        }

        if (_textBackgroundPopup != null)
        {
            if (_textBackgroundPopup.IsOpen)
            {
                _textBackgroundPopup.IsOpen = false;
            }
            else
            {
                // 互斥：关闭描边 Popup
                if (_textStrokePopup != null && _textStrokePopup.IsOpen)
                    _textStrokePopup.IsOpen = false;
                _textBackgroundPopup.IsOpen = true;
                SetColorTarget(1); // 填充色
                if (_chkBackgroundEnable != null)
                    _chkBackgroundEnable.IsChecked = _hasBackground;
            }
        }
        UpdateTextPanelUi();
    }

    private void OnColorTargetButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int mode))
        {
            SetColorTarget(mode);
        }
    }

    private void SetColorTarget(int mode)
    {
        _colorTargetMode = mode;
        UpdateTextPanelUi();
        UpdateToolOptionLabels();
    }

    private void ToggleStrokeEnable()
    {
        RequestTextSnapshot();
        if (_chkStrokeEnable != null)
            _hasStroke = _chkStrokeEnable.IsChecked == true;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    private void ToggleBackgroundEnable()
    {
        RequestTextSnapshot();
        if (_chkBackgroundEnable != null)
            _hasBackground = _chkBackgroundEnable.IsChecked == true;
        AppLogger.Info("CaptureWindow.TextBackground: 切换填充=" + _hasBackground + ", selectedText=" + (_selectedText != null));
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    private void OnStrokeSizeSliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderStrokeSize != null)
        {
            _textStrokeThickness = _sliderStrokeSize.Value;
            if (_textStrokeSizeValue != null)
                _textStrokeSizeValue.Text = _textStrokeThickness.ToString("0");
        }
        ApplyTextSettingsToSelected();
    }

    private void OnFillOpacitySliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderFillOpacity != null)
        {
            _textFillOpacity = _sliderFillOpacity.Value;
            if (_textFillOpacityValue != null)
                _textFillOpacityValue.Text = ((int)_textFillOpacity).ToString();
        }
        ApplyTextSettingsToSelected();
    }

    private void OnFillCornerSliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderFillCorner != null)
        {
            _textFillCornerRadius = _sliderFillCorner.Value;
            if (_textFillCornerValue != null)
                _textFillCornerValue.Text = ((int)_textFillCornerRadius).ToString();
        }
        ApplyTextSettingsToSelected();
    }

    private void OnFillPaddingSliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderFillPadding != null)
        {
            _textFillPadding = _sliderFillPadding.Value;
            if (_textFillPaddingValue != null)
                _textFillPaddingValue.Text = ((int)_textFillPadding).ToString();
        }
        ApplyTextSettingsToSelected();
    }

    private void OnTextSizeHostWheel(object? sender, PointerWheelEventArgs e)
    {
        // 智能步进：<24=2, 24-48=4, >48=8（对齐旧版）
        double step = 2;
        if (_currentTextSize >= 48) step = 8;
        else if (_currentTextSize >= 24) step = 4;

        double newSize = _currentTextSize + (e.Delta.Y > 0 ? step : -step);
        SetCurrentTextSize(newSize);
        e.Handled = true;
    }

    private void SetCurrentTextSize(double size)
    {
        RequestTextSnapshot();
        _currentTextSize = size;
        if (_currentTextSize < 8) _currentTextSize = 8;
        if (_currentTextSize > 200) _currentTextSize = 200;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    private void ApplyTextSettingsToSelected()
    {
        if (_selectedText != null)
        {
            _selectedText.FontSize = _currentTextSize;
            _selectedText.IsBold = _isTextBold;
            _selectedText.IsItalic = _isTextItalic;
            _selectedText.HasStroke = _hasStroke;
            _selectedText.HasBackground = _hasBackground;
            _selectedText.FontFamily = _currentTextFont;
            _selectedText.StrokeThickness = _textStrokeThickness;
            _selectedText.IsStrokeAuto = _isStrokeAuto;
            _selectedText.FillOpacity = _textFillOpacity;
            _selectedText.FillCornerRadius = _textFillCornerRadius;
            _selectedText.FillPadding = _textFillPadding;

            if (_colorTargetMode == 0) _selectedText.Color = _textForegroundColor;
            else if (_colorTargetMode == 1) _selectedText.FillColor = _textFillColor;
            else if (_colorTargetMode == 2) _selectedText.StrokeColor = _textStrokeColor;

            UpdateTextView(_selectedText);
        }
    }

    // ──────────────────────────────────────────
    // 文本图层核心操作
    // ──────────────────────────────────────────

    /// <summary>创建文本 UI 树 — 完全对齐旧版 CreateTextLayer</summary>
    private TextLayerInfo CreateTextLayer()
    {
        Grid root = new Grid
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = true
        };

        Border bg = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var strokePath = new Avalonia.Controls.Shapes.Path
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        TextBox tb = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.Wrap,
            ContextMenu = null,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        tb.Classes.Add("captureTextLayer");
        tb.Resources["TextControlBorderBrush"] = Brushes.Transparent;
        tb.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
        tb.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent;
        tb.Resources["TextControlBackground"] = Brushes.Transparent;
        tb.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        tb.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;

        // 文本框自身事件：右键完成编辑；左键单击按旧版语义移动图层，双击才编辑。
        // Ctrl+点击时允许事件冒泡，以便 CaptureSession 处理工具切换。
        tb.PointerPressed += (s, args) =>
        {
            var point = args.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed)
            {
                tb.IsHitTestVisible = false;
                _hasPendingTextEditSnapshot = false;
                OverlayCanvas.Focus();
                args.Handled = true;
                return;
            }

            // 如果按下 Ctrl，不拦截事件，让 CaptureSession 处理工具切换
            if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return;
            }

            if (point.Properties.IsLeftButtonPressed && args.ClickCount < 2)
            {
                Point dip = args.GetPosition(this);
                TryHandleTextToolInteraction(dip, args.ClickCount);
                args.Pointer.Capture(this);
                args.Handled = true;
            }
        };

        // 选中虚线框
        Border selBorder = new Border
        {
            BorderThickness = new Thickness(0),
            Background = null,
            IsHitTestVisible = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(-2, -2)
        };

        var selRect = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(30, 144, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 }
        };
        selBorder.Child = selRect;

        // 装饰层
        Grid adornerLayer = new Grid
        {
            IsVisible = false,
            IsHitTestVisible = true
        };

        // 旋转手柄 (左上)
        Ellipse rotateHandle = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(-6, -6, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "RotateHandle"
        };
        ToolTip.SetTip(rotateHandle, "旋转");

        // 删除手柄 (右下)
        Border deleteHandle = new Border
        {
            Width = 16,
            Height = 16,
            Background = new SolidColorBrush(Color.FromRgb(255, 85, 85)),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -8, -8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "DeleteHandle"
        };
        ToolTip.SetTip(deleteHandle, "删除");

        var xIcon = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        deleteHandle.Child = xIcon;

        adornerLayer.Children.Add(rotateHandle);
        adornerLayer.Children.Add(deleteHandle);

        root.Children.Add(bg);
        root.Children.Add(strokePath);
        root.Children.Add(tb);
        root.Children.Add(selBorder);
        root.Children.Add(adornerLayer);

        // ★ 关键：在注册任何事件之前先创建 info 并设置 Tag
        TextLayerInfo info = new TextLayerInfo();
        info.Root = root;
        info.TextBox = tb;
        info.BackgroundBorder = bg;
        info.StrokePath = strokePath;
        info.SelectionBorder = selBorder;
        info.AdornerLayer = adornerLayer;
        info.RotateHandle = rotateHandle;
        info.DeleteHandle = deleteHandle;

        root.Tag = info; // ★ 必须在事件注册前设置，因为事件内部需要用到

        // 失焦结束编辑
        tb.LostFocus += (s, args) =>
        {
            tb.IsHitTestVisible = false;
            _hasPendingTextEditSnapshot = false;
        };

        tb.GotFocus += (s, args) =>
        {
            if (!_hasPendingTextEditSnapshot)
            {
                RequestTextSnapshot();
                _hasPendingTextEditSnapshot = true;
            }
        };

        // Ctrl+Enter 完成编辑。用 Tunnel 优先级拦截，避免 TextBox 的 AcceptsReturn 先把 Enter 当换行吃掉。
        tb.AddHandler(KeyDownEvent, (s, args) =>
        {
            if ((args.Key == Key.Enter || args.Key == Key.Return) && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                AppLogger.Info("CaptureWindow.TextCtrlEnter: TextBox 提交文本编辑，selectedText=" + (_selectedText != null) + ", textLayers=" + _textLayers.Count);
                EndActiveTextEditing("TextBoxKeyDown");
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        // 文本变化 → 先刷新布局，再更新描边/背景/选中框，避免最后一个字超出输入框
        tb.TextChanged += (s, args) =>
        {
            if (root != null)
            {
                root.InvalidateMeasure();
                root.InvalidateArrange();
                root.UpdateLayout();
            }

            UpdateTextGeometry(info);
            UpdateTextBackgroundFrame(info);
            UpdateTextSelectionFrame(info);

            if (root != null && OverlayCanvas != null)
            {
                double ct = Canvas.GetTop(root);
                double ch = root.Bounds.Height;
                double maxY = OverlayCanvas.Bounds.Height;
                if (ch > 0 && ct + ch > maxY)
                {
                    double nt = maxY - ch;
                    if (nt < 0) nt = 0;
                    Canvas.SetTop(root, nt);
                }
            }
        };

        // 旋转手柄事件
        rotateHandle.PointerPressed += (s, args) =>
        {
            RequestTextSnapshot();
            _isRotatingText = true;
            _rotatingTextInfo = info;
            _rotateStartPoint = args.GetPosition(OverlayCanvas);
            _rotateStartAngle = info.RotationAngle;
            try { args.Pointer.Capture(this); } catch { }
            args.Handled = true;
        };

        // 删除手柄事件
        deleteHandle.PointerPressed += (s, args) =>
        {
            RequestTextSnapshot();
            DeleteTextLayer(info);
            args.Handled = true;
        };

        // 每个文本元素的滚轮缩放（含越界保护）— 对齐旧版 root.PreviewMouseWheel。
        // 同时挂到 TextBox，本体编辑态时滚轮可能被 TextBox 先接收，不能只依赖 root 冒泡。
        root.PointerWheelChanged += (s, args) => AdjustTextFontSizeByWheel(info, args, "RootWheel");
        tb.PointerWheelChanged += (s, args) => AdjustTextFontSizeByWheel(info, args, "TextBoxWheel");

        // 尺寸变化 → 同步输入框视觉范围，并自动上推防溢出（对齐旧版 root.SizeChanged）
        root.SizeChanged += (s, args) =>
        {
            UpdateTextBackgroundFrame(info);
            UpdateTextSelectionFrame(info);

            if (root != null && OverlayCanvas != null)
            {
                double ct = Canvas.GetTop(root);
                double ch = root.Bounds.Height;
                double maxY = OverlayCanvas.Bounds.Height;
                if (ch > 0 && ct + ch > maxY)
                {
                    double nt = maxY - ch;
                    if (nt < 0) nt = 0;
                    Canvas.SetTop(root, nt);
                }
            }
        };

        // 应用默认属性
        info.FontSize = _currentTextSize;
        info.IsBold = _isTextBold;
        info.IsItalic = _isTextItalic;
        info.FontFamily = _currentTextFont;
        info.HasStroke = _hasStroke;
        info.HasBackground = _hasBackground;

        if (!_hasStroke && !_hasBackground)
            info.Color = _textForegroundColor;
        else
            info.Color = _textForegroundColor;

        info.FillColor = _textFillColor;
        info.StrokeColor = _textStrokeColor;
        info.StrokeThickness = _textStrokeThickness;
        info.IsStrokeAuto = _isStrokeAuto;
        info.FillOpacity = _textFillOpacity;
        info.FillCornerRadius = _textFillCornerRadius;
        info.FillPadding = _textFillPadding;

        _textLayers.Add(info);

        return info;
    }

    private static double GetTextRightSafetyMargin(TextLayerInfo info)
    {
        double margin = 10.0;
        if (info.HasStroke) margin += Math.Max(0.0, info.StrokeThickness);
        if (info.HasBackground) margin += Math.Max(0.0, info.FillPadding);
        if (info.IsItalic) margin += 10.0;
        return margin;
    }

    /// <summary>在指定屏幕位置添加新文本</summary>
    public TextLayerInfo AddTextAt(Point dipPoint)
    {
        TextLayerInfo info = CreateTextLayer();
        Canvas.SetLeft(info.Root, dipPoint.X);
        Canvas.SetTop(info.Root, dipPoint.Y);
        OverlayCanvas.Children.Add(info.Root);
        OverlayCanvas.ZIndex = 1000;

        // ★ 关键：限制 MaxWidth 到当前选区边界，实现自动换行
        double rightSafetyMargin = GetTextRightSafetyMargin(info);
        if (_lastHasSelection)
        {
            double regionRight = _lastRegionDip.X + _lastRegionDip.Width;
            double maxWidth = regionRight - dipPoint.X - rightSafetyMargin;
            if (maxWidth < 20) maxWidth = 20;
            info.TextBox.MaxWidth = maxWidth;
        }
        else
        {
            double regionRight = Math.Max(OverlayCanvas.Bounds.Width, 100);
            double maxWidth = regionRight - dipPoint.X - rightSafetyMargin;
            if (maxWidth < 20) maxWidth = 20;
            info.TextBox.MaxWidth = maxWidth;
        }

        UpdateTextView(info);

        // 自动进入编辑模式。聚焦延迟到布局后执行，避免连续创建文本时焦点仍停在旧 TextBox/窗口上。
        SelectText(info);
        BeginTextEditing(info);

        return info;
    }

    /// <summary>更新文本 UI — 完全对齐旧版 UpdateTextView</summary>
    private void UpdateTextView(TextLayerInfo info)
    {
        if (info == null || info.TextBox == null) return;

        info.TextBox.FontSize = info.FontSize;
        info.TextBox.FontWeight = info.IsBold ? FontWeight.Bold : FontWeight.Normal;
        info.TextBox.FontStyle = info.IsItalic ? FontStyle.Italic : FontStyle.Normal;

        if (!string.IsNullOrEmpty(info.FontFamily))
        {
            try { info.TextBox.FontFamily = new FontFamily(info.FontFamily); } catch { }
        }

        double lineHeight = info.FontSize * 1.35;
        info.TextBox.LineHeight = lineHeight;

        info.TextBox.Foreground = new SolidColorBrush(info.Color);

        if (info.BackgroundBorder != null)
        {
            if (info.HasBackground)
            {
                var bgBrush = new SolidColorBrush(info.FillColor);
                bgBrush.Opacity = info.FillOpacity / 100.0;
                info.BackgroundBorder.Background = bgBrush;
                info.BackgroundBorder.Padding = new Thickness(0);
                info.BackgroundBorder.CornerRadius = new CornerRadius(info.FillCornerRadius);

                var margin = new Thickness(info.FillPadding);
                info.TextBox.Margin = margin;
                info.TextBox.Padding = info.IsItalic
                    ? new Thickness(0, 0, 10, 0)
                    : new Thickness(0);

                if (info.StrokePath != null)
                    info.StrokePath.Margin = margin;
            }
            else
            {
                info.BackgroundBorder.Background = Brushes.Transparent;
                info.BackgroundBorder.Padding = new Thickness(0);
                info.BackgroundBorder.CornerRadius = new CornerRadius(0);
                info.TextBox.Margin = new Thickness(0);
                info.TextBox.Padding = info.IsItalic
                    ? new Thickness(0, 0, 10, 0)
                    : new Thickness(0);

                if (info.StrokePath != null)
                    info.StrokePath.Margin = new Thickness(0);
            }
        }

        if (info.HasStroke && info.StrokePath != null)
        {
            Color sc = info.StrokeColor;
            if (info.IsStrokeAuto)
            {
                double brightness = (0.299 * info.Color.R + 0.587 * info.Color.G + 0.114 * info.Color.B) / 255.0;
                sc = (brightness < 0.5) ? Colors.White : Colors.Black;
            }

            info.StrokePath.IsVisible = true;
            info.StrokePath.Stroke = new SolidColorBrush(sc);
            info.StrokePath.StrokeThickness = GetTextStrokeRenderThickness(info.StrokeThickness, info.FontSize);
            info.StrokePath.StrokeLineCap = PenLineCap.Round;
            info.StrokePath.StrokeJoin = PenLineJoin.Round;

            UpdateTextGeometry(info);
        }
        else if (info.StrokePath != null)
        {
            info.StrokePath.IsVisible = false;
        }

        try { UpdateTextBackgroundFrame(info); } catch { }
        try { UpdateTextSelectionFrame(info); } catch { }
    }

    private void UpdateTextBackgroundFrame(TextLayerInfo info)
    {
        if (info?.BackgroundBorder == null || info.TextBox == null) return;

        Thickness margin = info.TextBox.Margin;
        double w = info.TextBox.Bounds.Width;
        double h = info.TextBox.Bounds.Height;
        if (w <= 0) w = info.TextBox.DesiredSize.Width;
        if (h <= 0) h = info.TextBox.DesiredSize.Height;
        if (w <= 0) w = Math.Max(40.0, info.FontSize * 2.0);
        if (h <= 0) h = Math.Max(20.0, info.FontSize * 1.35);

        info.BackgroundBorder.Width = Math.Max(1.0, w + margin.Left + margin.Right);
        info.BackgroundBorder.Height = Math.Max(1.0, h + margin.Top + margin.Bottom);
    }

    private static double GetTextStrokeRenderThickness(double strokeThickness, double fontSize)
    {
        if (strokeThickness <= 0.0) return 0.0;

        double maxByFont = Math.Max(1.0, fontSize * 0.22);
        double softened = strokeThickness <= 1.0
            ? strokeThickness * 1.45
            : 1.45 + (strokeThickness - 1.0) * 1.15;
        return Math.Clamp(softened, 0.75, maxByFont);
    }

    /// <summary>更新描边路径几何体 — 使用 Avalonia FormattedText</summary>
    private void UpdateTextGeometry(TextLayerInfo info)
    {
        if (info == null || info.TextBox == null || info.StrokePath == null) return;

        try
        {
            string text = info.TextBox.Text ?? "";
            if (string.IsNullOrEmpty(text))
            {
                info.StrokePath.Data = null;
                return;
            }

            double effectiveMaxWidth = 0;
            if (info.TextBox.MaxWidth > 0 && !double.IsInfinity(info.TextBox.MaxWidth))
            {
                double hPadding = info.TextBox.Padding.Left + info.TextBox.Padding.Right;
                double hBorder = info.TextBox.BorderThickness.Left + info.TextBox.BorderThickness.Right;
                effectiveMaxWidth = info.TextBox.MaxWidth - hPadding - hBorder;
                if (effectiveMaxWidth < 0) effectiveMaxWidth = 0;
            }

            double offsetX = info.TextBox.Padding.Left + info.TextBox.BorderThickness.Left;
            double offsetY = info.TextBox.Padding.Top + info.TextBox.BorderThickness.Top;

            var fontFamily = FontFamily.Parse(info.FontFamily ?? "Microsoft YaHei UI");
            var fontWeight = info.IsBold ? FontWeight.Bold : FontWeight.Normal;
            var fontStyle = info.IsItalic ? FontStyle.Italic : FontStyle.Normal;
            var typeface = new Typeface(fontFamily, fontStyle, fontWeight);

            var formattedText = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                info.FontSize,
                Brushes.Black);

            if (effectiveMaxWidth > 0)
                formattedText.MaxTextWidth = effectiveMaxWidth;

            formattedText.TextAlignment = TextAlignment.Left;
            formattedText.Trimming = TextTrimming.None;
            formattedText.LineHeight = info.TextBox.LineHeight > 0 ? info.TextBox.LineHeight : info.FontSize * 1.35;

            var geometry = formattedText.BuildGeometry(new Avalonia.Point(offsetX, offsetY));

            info.StrokePath.Data = geometry;

            try { UpdateTextSelectionFrame(info); } catch { }
        }
        catch { }
    }

    /// <summary>更新选中框位置</summary>
    private void UpdateTextSelectionFrame(TextLayerInfo info)
    {
        if (info == null || info.SelectionBorder == null || info.TextBox == null) return;

        try
        {
            if (!info.SelectionBorder.IsVisible) return;

            // 不能用 Root.Bounds：SelectionBorder 自己也是 Root 的子元素。
            // 如果用 Root.Bounds 再设置 SelectionBorder.Width = w + 4，会每次更新都反向撑大 Root，导致虚线框无限放大。
            Thickness margin = info.TextBox.Margin;
            double w = info.TextBox.Bounds.Width;
            double h = info.TextBox.Bounds.Height;
            if (w <= 0) w = info.TextBox.DesiredSize.Width;
            if (h <= 0) h = info.TextBox.DesiredSize.Height;
            if (w <= 0) w = Math.Max(40.0, info.FontSize * 2.0);
            if (h <= 0) h = Math.Max(20.0, info.FontSize * 1.35);

            w += margin.Left + margin.Right;
            h += margin.Top + margin.Bottom;

            double frameWidth = Math.Max(1.0, w + 4.0);
            double frameHeight = Math.Max(1.0, h + 4.0);

            info.SelectionBorder.Width = frameWidth;
            info.SelectionBorder.Height = frameHeight;

            if (info.SelectionBorder.Child is Avalonia.Controls.Shapes.Rectangle rect)
            {
                rect.Width = frameWidth;
                rect.Height = frameHeight;
            }
        }
        catch { }
    }

    /// <summary>选中文本 — 同步全局状态到 UI（对齐旧版）</summary>
    public void SelectText(TextLayerInfo info)
    {
        if (info == null) return;
        DeselectText();

        _selectedText = info;

        if (_selectedText.SelectionBorder != null)
            _selectedText.SelectionBorder.IsVisible = true;

        if (_selectedText.AdornerLayer != null)
        {
            if (string.IsNullOrEmpty(_selectedText.TextBox?.Text))
                _selectedText.AdornerLayer.IsVisible = false;
            else
                _selectedText.AdornerLayer.IsVisible = true;
        }

        // 同步全局状态
        _currentTextSize = info.FontSize;
        _isTextBold = info.IsBold;
        _isTextItalic = info.IsItalic;
        _hasStroke = info.HasStroke;
        _hasBackground = info.HasBackground;
        _textStrokeThickness = info.StrokeThickness;
        _isStrokeAuto = info.IsStrokeAuto;
        _textFillOpacity = info.FillOpacity;
        _textFillCornerRadius = info.FillCornerRadius;
        _textFillPadding = info.FillPadding;

        if (!string.IsNullOrEmpty(info.FontFamily)) _currentTextFont = info.FontFamily;

        if (_colorTargetMode == 0)
        {
            _textForegroundColor = info.Color;
            SelectedAnnotationColor = System.Drawing.Color.FromArgb(info.Color.A, info.Color.R, info.Color.G, info.Color.B);
        }
        else if (_colorTargetMode == 1) _textFillColor = info.FillColor;
        else if (_colorTargetMode == 2) _textStrokeColor = info.StrokeColor;

        _textFillColor = info.FillColor;
        _textStrokeColor = info.StrokeColor;

        UpdateTextPanelUi();
    }

    /// <summary>取消选中文本（供 CaptureSession.ClearAllSelections 调用）</summary>
    public void DeselectText()
    {
        if (_selectedText != null)
        {
            if (_selectedText.SelectionBorder != null)
                _selectedText.SelectionBorder.IsVisible = false;
            if (_selectedText.AdornerLayer != null)
                _selectedText.AdornerLayer.IsVisible = false;
            _selectedText = null;
        }
    }

    /// <summary>删除指定文本图层</summary>
    private void DeleteTextLayer(TextLayerInfo info)
    {
        if (info == null) return;
        if (_selectedText == info) DeselectText();
        if (info.Root != null && OverlayCanvas.Children.Contains(info.Root))
            OverlayCanvas.Children.Remove(info.Root);
        _textLayers.Remove(info);
    }

    /// <summary>清除所有文本</summary>
    public void ClearAllTexts()
    {
        foreach (var info in _textLayers.ToList())
        {
            if (info.Root != null && OverlayCanvas.Children.Contains(info.Root))
                OverlayCanvas.Children.Remove(info.Root);
        }
        _textLayers.Clear();
        _selectedText = null;
    }

    private void BeginTextEditing(TextLayerInfo info)
    {
        if (info?.TextBox == null) return;

        foreach (var layer in _textLayers)
        {
            if (!ReferenceEquals(layer, info) && layer.TextBox != null)
                layer.TextBox.IsHitTestVisible = false;
        }

        info.TextBox.IsHitTestVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (info.TextBox == null || !info.TextBox.IsHitTestVisible) return;
            info.TextBox.Focus();
            info.TextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public bool HasActiveTextEditing => _textLayers.Any(info => info.TextBox?.IsHitTestVisible == true);

    /// <summary>结束当前文本编辑</summary>
    public void EndActiveTextEditing(string reason = "Unknown")
    {
        int ended = 0;
        foreach (var info in _textLayers)
        {
            if (info.TextBox != null && info.TextBox.IsHitTestVisible)
            {
                info.TextBox.IsHitTestVisible = false;
                ended++;
            }
        }
        _hasPendingTextEditSnapshot = false;
        _textCtrlKeyDown = false;

        bool focused = false;
        try { focused = OverlayCanvas.Focus(); } catch { }
        AppLogger.Info("CaptureWindow.EndActiveTextEditing: reason=" + reason + ", ended=" + ended + ", selectedText=" + (_selectedText != null) + ", textLayers=" + _textLayers.Count + ", overlayFocus=" + focused);
    }

    /// <summary>从 Avalonia 控件树查找 TextLayerInfo</summary>
    public TextLayerInfo? FindTextInfoFromSource(object? source)
    {
        if (source is not StyledElement element) return null;
        var current = element;
        while (current != null)
        {
            if (current is Grid grid && grid.Tag is TextLayerInfo info)
                return info;
            current = current.Parent;
        }
        return null;
    }

    private void AdjustTextFontSizeByWheel(TextLayerInfo info, PointerWheelEventArgs args, string source)
    {
        if (info?.Root == null || info.TextBox == null) return;
        RequestTextSnapshot();

        double delta = args.Delta.Y > 0 ? 2 : -2;
        double newSize = Math.Clamp(info.FontSize + delta, 8.0, 200.0);
        double oldSize = info.FontSize;
        double oldMaxWidth = info.TextBox.MaxWidth;
        double oldTop = Canvas.GetTop(info.Root);
        if (double.IsNaN(oldTop)) oldTop = 0;

        // 1. 计算有效选区
        Rect validRect = _lastHasSelection
            ? _lastRegionDip
            : new Rect(0, 0, Math.Max(OverlayCanvas.Bounds.Width, 1), Math.Max(OverlayCanvas.Bounds.Height, 1));

        // 2. 按当前位置计算可用宽度
        double currentLeft = Canvas.GetLeft(info.Root);
        if (double.IsNaN(currentLeft)) currentLeft = 0;
        double safetyMargin = GetTextRightSafetyMargin(info);
        double availableWidth = validRect.Right - currentLeft - safetyMargin;
        if (availableWidth < 50) availableWidth = 50;

        // 3. 应用新字号和宽度，强制布局
        info.FontSize = newSize;
        info.TextBox.FontSize = newSize;
        info.TextBox.LineHeight = newSize * 1.35;
        info.TextBox.MaxWidth = availableWidth;
        info.Root.InvalidateMeasure();
        info.Root.InvalidateArrange();
        info.Root.UpdateLayout();

        double newHeight = info.Root.Bounds.Height;
        if (newHeight <= 0) newHeight = newSize * 1.35;

        // 4. 检查是否超出选区总高度 → 完全回滚
        if (newHeight > validRect.Height)
        {
            info.FontSize = oldSize;
            info.TextBox.FontSize = oldSize;
            info.TextBox.LineHeight = oldSize * 1.35;
            info.TextBox.MaxWidth = oldMaxWidth;
            AppLogger.Info($"CaptureWindow.TextWheel: 拒绝字号调整 source={source}, newHeight={newHeight:0}, validH={validRect.Height:0}");
            args.Handled = true;
            return;
        }

        // 5. 底部越界 → 上推
        double currentTop = oldTop;
        if (currentTop + newHeight > validRect.Bottom)
        {
            double newTop = validRect.Bottom - newHeight;
            if (newTop < validRect.Top)
            {
                // 上推后仍撞顶 → 回滚
                info.FontSize = oldSize;
                info.TextBox.FontSize = oldSize;
                info.TextBox.LineHeight = oldSize * 1.35;
                info.TextBox.MaxWidth = oldMaxWidth;
                Canvas.SetTop(info.Root, oldTop);
                AppLogger.Info($"CaptureWindow.TextWheel: 拒绝字号调整 source={source}, 上推后撞顶 newTop={newTop:0}, validTop={validRect.Top:0}");
                args.Handled = true;
                return;
            }
            Canvas.SetTop(info.Root, newTop);
        }

        // 6. 顶部越界 → 下拉
        currentTop = Canvas.GetTop(info.Root);
        if (double.IsNaN(currentTop)) currentTop = 0;
        if (currentTop < validRect.Top)
        {
            Canvas.SetTop(info.Root, validRect.Top);
        }

        // 7. 完成
        UpdateTextView(info);
        _currentTextSize = newSize;
        if (_textSizeValueText != null) _textSizeValueText.Text = ((int)newSize).ToString();
        AppLogger.Info($"CaptureWindow.TextWheel: 调整文本图层字号 source={source}, old={oldSize:0}, next={newSize:0}, editing={info.TextBox.IsHitTestVisible}");
        args.Handled = true;
    }

    /// <summary>测量文本在给定宽度下的高度</summary>
    public double MeasureTextHeightForWidth(TextLayerInfo info, double maxWidth)
    {
        if (info?.TextBox == null) return 0;
        double saved = info.TextBox.MaxWidth;
        try
        {
            info.TextBox.MaxWidth = maxWidth > 0 ? maxWidth : double.PositiveInfinity;
            info.TextBox.InvalidateMeasure();
            info.TextBox.InvalidateArrange();
            info.Root.InvalidateMeasure();
            info.Root.InvalidateArrange();
            info.Root.UpdateLayout();
            return info.Root.Bounds.Height;
        }
        finally
        {
            info.TextBox.MaxWidth = saved;
        }
    }

    // ──────────────────────────────────────────
    // 公开属性（供 CaptureSession 使用）
    // ──────────────────────────────────────────

    public TextLayerInfo? SelectedText => _selectedText;

    public double CurrentTextSize
    {
        get => _currentTextSize;
        set
        {
            _currentTextSize = value;
            if (_selectedText != null)
            {
                _selectedText.FontSize = value;
                UpdateTextView(_selectedText);
            }
            if (_textSizeValueText != null)
                _textSizeValueText.Text = $"{_currentTextSize:F0}";
        }
    }

    public double TextStrokeThickness
    {
        get => _textStrokeThickness;
        set
        {
            _textStrokeThickness = value;
            if (_selectedText != null)
            {
                _selectedText.StrokeThickness = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public double TextFillOpacity
    {
        get => _textFillOpacity;
        set
        {
            _textFillOpacity = value;
            if (_selectedText != null)
            {
                _selectedText.FillOpacity = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public double TextFillCornerRadius
    {
        get => _textFillCornerRadius;
        set
        {
            _textFillCornerRadius = value;
            if (_selectedText != null)
            {
                _selectedText.FillCornerRadius = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public double TextFillPadding
    {
        get => _textFillPadding;
        set
        {
            _textFillPadding = value;
            if (_selectedText != null)
            {
                _selectedText.FillPadding = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public bool IsDraggingText => _isDraggingSelectedText;
    public bool IsRotatingText => _isRotatingText;

    public IReadOnlyList<TextLayerInfo> TextLayers => _textLayers;

    public Color ForegroundTextColor => _textForegroundColor;
    public Color TextFillColor => _textFillColor;
    public Color TextStrokeColor => _textStrokeColor;
    public int ColorTargetMode => _colorTargetMode;
    public bool HasStroke => _hasStroke;
    public bool HasBackground => _hasBackground;

    public async System.Threading.Tasks.Task PlayOcrSuccessAnimation()
    {
        if (IconOcr == null) return;
        var origData = IconOcr.Data;
        
        IconOcr.Data = Avalonia.Media.Geometry.Parse("M 5 12 L 10 17 L 19 6");
        IconOcr.SetValue(Avalonia.Controls.Shapes.Shape.StrokeProperty, Avalonia.Media.Brushes.LimeGreen);

        await System.Threading.Tasks.Task.Delay(1000);

        IconOcr.Data = origData;
        IconOcr.ClearValue(Avalonia.Controls.Shapes.Shape.StrokeProperty);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            if (FrozenScreenImage.Source is IDisposable disposableSource)
            {
                disposableSource.Dispose();
            }
            FrozenScreenImage.Source = null;
        }
        catch { }

        try
        {
            Annotation.SourceBitmap = null;
        }
        catch { }
    }
}
