using System;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 贴图标注浮窗工具栏。图标条 + 参数条 + 箭头三段完全照抄截图窗口
/// （CaptureWindow.axaml 的 ActionBar / ToolOptionsBar / ToolOptionsArrow 与 ShowToolOptions() 显隐规则），
/// 只按贴图场景删掉无对应链路的按钮。主工具栏改版时需同步本控件。
/// </summary>
public partial class AnnotationToolbar : UserControl
{
    private ToolbarLayoutView? _customLayout;
    public void ApplyCustomLayout(ScreenCaptureTool.Core.AppSettings settings)
    {
        if (_embedded) return;
        _customLayout ??= new ToolbarLayoutView(Tools, ScreenCaptureTool.Core.ToolbarScene.StickerAnnotation,
            Tools.Children.OfType<AnnotationToolButton>().ToDictionary(b => b.ToolKey, b => new Control[] { b }));
        _customLayout.Apply(settings);
        ReflowBars();
    }
    public event Action<string>? ToolRequested;
    public event Action? ParametersChanged;
    /// <summary>
    /// 参数条上「能回改已画好图元」的那九档，用户改动了<b>其中一档</b>时报这一次（一档一报点，不合并）。
    ///
    /// <para>与 <see cref="ParametersChanged"/> 的分工：那条只让宿主重画"下一笔"的预览，本条要求宿主把
    /// 这一档落到<b>当前选中的那一个</b>图元上（与截图侧 <c>CaptureWindow</c> 那八支事件同一语义，只是这里
    /// 用载荷区分档位，免得九支事件在宿主订阅表里各写一遍）。没有选中图元时它自然只成为下一笔的记忆值。</para>
    ///
    /// <para>形状填充档原先挂着一支 <c>ShapeFillChanged</c>：全仓无人订阅，于是"选中一个矩形后拖浓淡"
    /// 在贴图侧永远不落地（2026-09-19 审计）。它现在并进本条的 <see cref="AnnotationStylePart.ShapeFill"/>。</para>
    /// </summary>
    public event Action<AnnotationStylePart>? StylePartChanged;
    /// <summary>序号类型档被用户换掉（123 / ABC / ⅠⅡⅢ / 一二三）：宿主据此把已画出的序号一起换类型，
    /// 并补一次撤销快照。与截图侧 <c>CaptureWindow.CounterSerialChanged</c> → <c>CaptureSession.ApplyCounterSerialKindToAll</c> 同一语义。</summary>
    public event Action<CounterSerialKind>? CounterSerialKindChanged;
    public event Action<Control>? CustomColorRequested;
    /// <summary>参数条显隐/宽度变化，宿主需据此重新定位浮窗。</summary>
    public event Action? LayoutChanged;

    public Color SelectedColor { get; private set; } = Color.FromRgb(51, 136, 255);

    private AnnotationLineStyle _brushLineStyle = AnnotationLineStyle.Solid;
    private AnnotationLineStyle _shapeLineStyle = AnnotationLineStyle.Solid;
    private ArrowAnnotationStyle _arrowStyle = ArrowAnnotationStyle.Designed;
    /// <summary>箭头样式下拉：条目表与截图遮罩、序号引线共用 <see cref="ArrowHeadStyleMenu"/> 那一份。</summary>
    private readonly ArrowHeadStyleMenu _arrowStyleMenu = new(legacyPath: true, doubleHead: true);
    private ShapeAnnotationKind _shapeKind = ShapeAnnotationKind.Rectangle;
    private string? _activeKey;
    /// <summary>程序性摆位（选中图元回显）期间的抑制位：见 <see cref="SuppressStyleEvents"/>。</summary>
    private bool _suppressStyleEvents;
    // 圆角记忆（与图元几何同坐标系，0-200）：滑块拆除后由字段承载，选中图元经 Sync* 刷新，
    // 新画图元起笔时取这里（ApplyDefaults 从 settings 恢复初值）。
    private const double CornerRadiusMax = 200.0;
    private double _shapeCornerRadius;
    public float EraserDiameter => (float)SliderEraserSize.Value;
    public EraserGestureMode EraserMode { get; private set; } = EraserGestureMode.Rectangle;
    private double _spotlightCornerRadius;

    /// <summary>
    /// 文本标注共享实现（由宿主 <c>StickerWindow</c> 建好并注入）。参数面板的控件长在本 UserControl 的
    /// 命名域里，宿主经 <c>Control.FindControl</c> 按名抓取后交给它绑定；本控件只负责把按钮点击转过去。
    /// </summary>
    public ScreenCaptureTool.Services.TextAnnotationController? TextController { get; set; }

    public AnnotationToolbar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { if (_customLayout?.HasGroups == true) ReflowBars(); };
        ColorOptions.ColorSelected += c => ApplyColorToCurrentTarget(c);
        ColorOptions.CustomColorRequested += anchor => CustomColorRequested?.Invoke(anchor);
        BrushOptions.Slider.ValueChanged += (_, _) => { ParametersChanged?.Invoke(); UpdateStyleOptionVisuals(); };
        // 粗细：拖滑块与在滑块上滚滚轮是同一个档的两条来路，都落到滑杆值上，故报点只挂 ValueChanged 这一处
        // （共享控件的 Adjust 先写值再发 WheelAdjusted，两处都报＝一次滚轮报两遍；截图侧 CaptureWindow 同一口径）。
        ThicknessOptions.Slider.ValueChanged += (_, _) => { ParametersChanged?.Invoke(); UpdateColorVisuals(); RaiseStylePart(AnnotationStylePart.Thickness); };
        BlurOptions.Slider.ValueChanged += (_, _) => { ParametersChanged?.Invoke(); RaiseStylePart(AnnotationStylePart.BlurSettings); };
        CounterOptions.Slider.ValueChanged += (_, _) => { ParametersChanged?.Invoke(); RaiseStylePart(AnnotationStylePart.CounterRadius); };
        // 类型档会回头改已经画在图上的那些序号（与截图侧同一口径），故单独转一笔给宿主；
        // 面板里「起始」那一格只影响"下一个"，不发事件。
        CounterOptions.SerialKindChanged += kind => CounterSerialKindChanged?.Invoke(kind);
        // 「样式」小样与「引线箭头」各报自己那一档：只落到当前选中的那一颗序号上（截图侧同口径）
        CounterOptions.StyleChanged += () => RaiseStylePart(AnnotationStylePart.CounterStyle);
        CounterOptions.TailHeadChanged += () => RaiseStylePart(AnnotationStylePart.CounterTailHead);
        // 形状填充：记忆值住在共享控件里，这里只把改动点名转给宿主（落到选中图元那一路不归 ParametersChanged）
        ShapeFillOptions.FillChanged += () => RaiseStylePart(AnnotationStylePart.ShapeFill);
        // 箭头样式下拉：与截图侧同源（条目由枚举生成），挂在 Button.Flyout 上，不再自架一层 Popup
        _arrowStyleMenu.Picked += style =>
        {
            _arrowStyle = style;
            UpdateStyleOptionVisuals();
            RaiseStylePart(AnnotationStylePart.ArrowStyle);
        };
        BtnArrowStyle.Flyout = _arrowStyleMenu.Flyout;
        SpotlightOptions.Darkness.ValueChanged += (_, _) => { ParametersChanged?.Invoke(); RaiseStylePart(AnnotationStylePart.SpotlightSettings); };
        // 描边：滚轮那一路（AdjustStroke）先写滑杆值再发 StrokeAdjusted，报点同样只挂 ValueChanged 一处
        SpotlightOptions.Stroke.ValueChanged += (_, _) => { ParametersChanged?.Invoke(); RaiseStylePart(AnnotationStylePart.SpotlightSettings); };
        // 圆角滑块已拆除（2026-09-12「圆角滑块退役」），画布四角手柄也于 2026-09-18 退役：
        // 圆角一律在「选中后元素上方浮出的圆角浮条」上调整，记忆由下方 _shapeCornerRadius /
        // _spotlightCornerRadius 字段承载。
        SliderBrushTaper.ValueChanged += (_, _) => { TxtBrushTaper.Text = Math.Round(SliderBrushTaper.Value).ToString(); ParametersChanged?.Invoke(); UpdateStyleOptionVisuals(); };
        SliderBrushSmoothing.ValueChanged += (_, _) => TxtBrushSmoothing.Text = Math.Round(SliderBrushSmoothing.Value).ToString();
        SliderBrushMinWidth.ValueChanged += (_, _) => TxtBrushMinWidth.Text = Math.Round(SliderBrushMinWidth.Value).ToString();
        // 笔迹两枚菜单的"点条上别处就关"（与截图侧 CaptureWindow 那条窗口根 Tunnel 同一目的、不同挂点）
        AddHandler(InputElement.PointerPressedEvent, OnBrushMenusDismissPressed, RoutingStrategies.Tunnel);
        UpdateColorVisuals();
    }

    /// <summary>
    /// 按下落在本工具栏内、且不在这两枚开关钮上时收起笔迹菜单。
    ///
    /// 为什么贴图侧必须自己收：三枚 Popup 都关着 <c>IsLightDismissEnabled</c>——本工具栏住在
    /// <c>StickerAnnotationPaletteWindow</c> 那枚 <c>WS_EX_NOACTIVATE</c> 不激活浮窗里，窗口永不进前台，
    /// "失焦即关"与 light dismiss 两条通路在这扇窗上结构性失效（开了还会与宿主抢前台）。
    ///
    /// 挂点选<b>本控件自身</b>而不是窗口根：菜单内容弹出后住在独立的 PopupRoot 里，它的按下根本不走
    /// 本控件这条路由 ⇒ 摸菜单内的滑杆天然不会被误关，判据里只需把两枚开关钮算作自己人。
    /// 开关钮那一格不能省：否则按下那一下先把菜单关掉，紧随其后的 Click 里 <see cref="ToggleMenu"/>
    /// 读到的已是 false ⇒ 又把它弹回来，表现为"那枚按钮点两下也关不掉"。
    /// </summary>
    private void OnBrushMenusDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!BrushLineStylePopup.IsOpen && !BrushAdvancedPopup.IsOpen) return;
        if (e.Source is not Visual source) return;
        if (ReferenceEquals(source, BtnBrushLineStyle) || ReferenceEquals(source, BtnBrushAdvanced)
            || source.GetVisualAncestors().Any(a =>
                ReferenceEquals(a, BtnBrushLineStyle) || ReferenceEquals(a, BtnBrushAdvanced))) return;
        BrushLineStylePopup.IsOpen = false;
        BrushAdvancedPopup.IsOpen = false;
    }

    // ── 共享参数读取（宿主每一起笔取一次） ──────────────────────────────

    public double BrushOpacity => BrushOptions.Slider.Value;
    public double StrokeThickness => ThicknessOptions.Slider.Value;
    public int BlurLevel => (int)BlurOptions.Slider.Value;
    public float CounterRadius => (float)CounterOptions.Slider.Value;
    /// <summary>整套序号样式档（类型 / 外形 / 底色 / 起始值 / 胶囊圆角 / 引线箭头），
    /// 与截图侧 <c>CaptureWindow.SelectedCounterStyle</c> 同一份实现、同一口径。</summary>
    public CounterStyleSettings CounterStyle => CounterOptions.Current;
    public float SpotlightDarkness => (float)(SpotlightOptions.Darkness.Value / 100.0);
    public float SpotlightStrokeThickness => (float)SpotlightOptions.Stroke.Value;
    public SpotlightShapeKind SpotlightKind => SpotlightOptions.Kind;
    /// <summary>聚光灯圆角半径（与图元几何同坐标系），供宿主起笔时取默认值。</summary>
    public float SpotlightCornerRadius => (float)Math.Max(0.0, _spotlightCornerRadius);
    /// <summary>矩形圆角半径（与图元几何同坐标系），供宿主起笔时取默认值。</summary>
    public float ShapeCornerRadius => (float)Math.Max(0.0, _shapeCornerRadius);

    /// <summary>圆角记忆写口（选中图元 / 圆角浮条改值后由宿主回灌）：滑块已从参数条拆除、画布手柄也已退役，字段即记忆本体。</summary>
    public void SyncShapeCornerRadius(double radius) => _shapeCornerRadius = Math.Clamp(radius, 0.0, CornerRadiusMax);

    /// <summary>聚光灯圆角记忆写口，与 <see cref="SyncShapeCornerRadius"/> 同一语义。</summary>
    public void SyncSpotlightCornerRadius(double radius) => _spotlightCornerRadius = Math.Clamp(radius, 0.0, CornerRadiusMax);

    /// <summary>胶囊圆角记忆写口（百分比，序号浮条改值后由宿主回灌）：参数条上没有它的输入格，
    /// 记忆位就住在共享的 <c>CounterOptions</c> 里，下一颗胶囊起笔时从 <see cref="CounterStyle"/> 取走。</summary>
    public void SyncCounterCapsuleCorner(float corner) => CounterOptions.SetCapsuleCorner(corner);

    /// <summary>形状填充不透明度 0～1（未勾选＝0＝不填充），供宿主起笔时取默认值。</summary>
    public float ShapeFillOpacity => ShapeFillOptions.FillOpacity;

    /// <summary>填充档写口（选中图元回显）：只摆控件、不触发 <see cref="StylePartChanged"/>，避免回灌自己引发一次重绘。</summary>
    public void SyncShapeFill(bool filled, double opacityPercent) => ShapeFillOptions.SetState(filled, opacityPercent);

    /// <summary>
    /// 参数条 → 手势参数包：接本控件作参数源的宿主（贴图浮窗、录屏控制条）起笔当帧都调这里，
    /// 18 档的取值算式只留这一份。<paramref name="activeTool"/> 只决定圆角那一格取哪个记忆值
    /// （矩形档取矩形、聚光档取聚光，同一时刻只有一档在画）；两个倍率由宿主给：贴图传自己的显示倍率与
    /// 箭头缩放，与屏幕 1:1 的遮罩一律传 1。
    /// </summary>
    internal AnnotationGestureSettings CreateGestureSettings(
        ScreenCaptureTool.Core.Capture.ToolMode activeTool, float arrowScale, float velocityScale)
    {
        var c = SelectedColor;
        return new AnnotationGestureSettings(
            System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B),
            (float)Math.Max(1.0, StrokeThickness),
            Brush,
            BrushLineStyle,
            ArrowStyle,
            arrowScale,
            ShapeKind,
            ShapeLineStyle,
            BlurMode,
            BlurLevel,
            SpotlightDarkness,
            SpotlightKind,
            SpotlightStrokeThickness,
            activeTool == ScreenCaptureTool.Core.Capture.ToolMode.Rect
                ? ShapeCornerRadius : SpotlightCornerRadius,
            ShapeFillOpacity,
            (float)Math.Max(8.0, CounterRadius),
            CounterStyle,
            EraserDiameter,
            velocityScale,
            EraserMode);
    }

    // ── 选中图元的样式回显（与截图侧 CaptureWindow 同名四支、同一口径）──────
    //
    // 规矩只有一条：这里写的是滑杆与颜色字段，而它们各自挂着向宿主报点的出口。不挡住，
    // "选中一个图元"就反手触发一次回灌、在撤销栈里留一条空记录（截图侧 _suppressCommonSettingsEvent 同病）。

    /// <summary>回显<b>选中那颗形状图元</b>自己的样式：颜色、粗细、形状档、线型、填充五格摆回参数条控件，
    /// 圆角那一格条上没有控件、只写 <c>_shapeCornerRadius</c> 记忆字段（入口在元素上方的圆角浮条）。</summary>
    public void SyncShapeToolState(RectangleShape rect) => SuppressStyleEvents(() =>
    {
        SetColor(FromDrawing(rect.StrokeColor));
        ThicknessOptions.Slider.Value = ClampSliderValue(ThicknessOptions.Slider, rect.StrokeThickness);
        _shapeKind = rect.ShapeKind;
        _shapeLineStyle = rect.LineStyle;
        _shapeCornerRadius = Math.Clamp(rect.CornerRadius, 0.0, CornerRadiusMax);
        ShapeFillOptions.SetState(rect.FillOpacity > 0.0f, rect.FillOpacity * 100.0);
        UpdateStyleOptionVisuals();
    });

    /// <summary>回显<b>选中那支箭头</b>自己的样式：颜色、粗细、箭头头型。整体缩放（滚轮那一档）不在参数条上，
    /// 故这里不回灌它。</summary>
    public void SyncArrowToolState(ArrowShape arrow) => SuppressStyleEvents(() =>
    {
        SetColor(FromDrawing(arrow.Color));
        ThicknessOptions.Slider.Value = ClampSliderValue(ThicknessOptions.Slider, arrow.Thickness);
        _arrowStyle = arrow.Style;
        UpdateStyleOptionVisuals();
    });

    /// <summary>回显<b>选中那颗序号</b>自己的样式：标注色、大小滑杆、样式小样（外形＋底色）、引线箭头头型。
    /// 「类型」档刻意不动——它作用的是全图所有序号，不是这一颗的私有样式，摆上去会让条上读数与其余序号对不上。</summary>
    public void SyncCounterToolState(CounterShape counter) => SuppressStyleEvents(() =>
    {
        SetColor(FromDrawing(counter.FillColor));
        CounterOptions.Slider.Value = ClampSliderValue(CounterOptions.Slider, counter.Radius);
        CounterOptions.SyncStyleFromShape(counter.Glyph, counter.Fill, counter.TailHead);
    });

    /// <summary>回显<b>选中那条笔迹</b>的颜色——只有颜色这一档能改到已画好的笔迹上（粗细/线型/笔锋属于"这一笔"，
    /// 见 protection.md「标注画布鼠标手势优先级」④），故条上也只摆这一格。</summary>
    public void SyncStrokeToolColor(System.Drawing.Color color) => SuppressStyleEvents(() => SetColor(FromDrawing(color)));

    /// <summary>回显<b>选中那颗聚光灯</b>自己的样式：颜色、暗幕、描边粗细、形状档、圆角。
    /// 与截图侧 <c>SyncSpotlightToolState</c> 同一份口径——滚轮那一路改完图元会把滑杆摆到新值，
    /// 摆杆会经「聚光灯整包」报点回流到分派表；条上其余几格若还留着上一颗的取值，就会顺手盖掉这一颗。
    /// 回显做过一遍之后那些格恒等于图元自身，报点回来时逐字段比平、自然什么都不做。</summary>
    public void SyncSpotlightToolState(SpotlightShape spot) => SuppressStyleEvents(() =>
    {
        SetColor(FromDrawing(spot.StrokeColor));
        SpotlightOptions.Darkness.Value = ClampSliderValue(SpotlightOptions.Darkness, spot.Darkness * 100.0f);
        SpotlightOptions.Stroke.Value = ClampSliderValue(SpotlightOptions.Stroke, spot.StrokeThickness);
        SpotlightOptions.SyncKind(spot.ShapeKind);
        _spotlightCornerRadius = Math.Clamp(spot.CornerRadius, 0.0, CornerRadiusMax);
    });

    /// <summary>回显<b>选中那块马赛克/模糊</b>自己的样式：模式与强度档（同一口径，理由见
    /// <see cref="SyncSpotlightToolState"/>）。它不吃标注色——马赛克面板上压根没有取色格。</summary>
    public void SyncBlurToolState(BlurShape blur) => SuppressStyleEvents(() =>
    {
        BlurOptions.SyncMode(blur.Mode);
        BlurOptions.Slider.Value = ClampSliderValue(BlurOptions.Slider, blur.UiLevel);
    });

    /// <summary>程序性摆位包一层：期间任何控件变更都不反手报给宿主（嵌套调用时存/还原，不能清位）。</summary>
    private void SuppressStyleEvents(Action apply)
    {
        bool old = _suppressStyleEvents;
        _suppressStyleEvents = true;
        try { apply(); }
        finally { _suppressStyleEvents = old; }
    }

    /// <summary>用户改动某一档时报给宿主；程序性摆位（<see cref="SuppressStyleEvents"/> 期间）不报。</summary>
    private void RaiseStylePart(AnnotationStylePart part)
    {
        if (!_suppressStyleEvents) StylePartChanged?.Invoke(part);
    }

    /// <summary>把图元值摆进滑杆：四舍五入到刻度并夹进量程，越界的记忆值不会把滑杆拖出可显示范围。</summary>
    private static double ClampSliderValue(Slider slider, float value)
        => Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);

    private static Color FromDrawing(System.Drawing.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public BlurMode BlurMode => BlurOptions.Mode;
    public BrushProfile Brush => new((float)(BrushOptions.Slider.Value / 100.0),
        (float)(SliderBrushTaper.Value / 100.0),
        (float)(SliderBrushSmoothing.Value / 100.0),
        (float)(SliderBrushMinWidth.Value / 100.0));
    public AnnotationLineStyle BrushLineStyle => _brushLineStyle;
    public AnnotationLineStyle ShapeLineStyle => _shapeLineStyle;
    public ArrowAnnotationStyle ArrowStyle => _arrowStyle;
    public ShapeAnnotationKind ShapeKind => _shapeKind;

    /// <summary>
    /// 参数滑杆的<b>写口</b>，给宿主在贴图内容区滚轮调参用（浮窗是独立窗口，贴图根窗口的 Tunnel
    /// 滚轮处理器看不见参数条上那枚滑杆）。这里刻意只转发、不放步长与夹取策略——策略属于滚轮分派，
    /// 须与截图 <c>CaptureSession</c> 的滚轮档逐条对齐。赋 <c>Value</c> 会走构造里已挂的
    /// <c>ValueChanged → ParametersChanged</c>，数字回显与宿主重绘自动生效，不必另发事件。
    /// </summary>
    public Slider StrokeThicknessSlider => ThicknessOptions.Slider;
    public Slider BlurLevelSlider => BlurOptions.Slider;
    public Slider CounterRadiusSlider => CounterOptions.Slider;
    public Slider SpotlightDarknessSlider => SpotlightOptions.Darkness;
    public Slider SpotlightStrokeSlider => SpotlightOptions.Stroke;
    public Slider EraserSizeSlider => SliderEraserSize;

    /// <summary>浮窗四周的阴影余量（＝根 Grid 那圈固定行列表）。光晕只画在本控件根 Grid 的矩形内，
    /// 宿主定位必须把这圈透明区从"可见工具条"的位置里抵掉。<b>数值单一来源是 XAML</b>：这里读
    /// <c>RowDefinitions/ColumnDefinitions</c> 的固定值，改 XAML 不必同步改这里。</summary>
    public Thickness ShadowInset => new(
        Root.ColumnDefinitions[0].Width.Value,
        Root.RowDefinitions[0].Height.Value,
        Root.ColumnDefinitions[2].Width.Value,
        Root.RowDefinitions[4].Height.Value);

    /// <summary>主工具栏右缘相对<b>本控件左缘</b>（＝浮窗客户区左缘，含阴影余量）的 DIP 偏移。
    /// <see cref="ReflowBars"/> 把图标条贴内容列右缘，故该值恒等于本控件宽度；保留访问器是为了让
    /// 宿主不必依赖这个内部事实。现算而不读 Bounds：宿主首次定位可能还没跑完 arrange。</summary>
    public double PrimaryBarRight
    {
        get
        {
            IconBar.Measure(Size.Infinity);
            return ShadowInset.Left + IconBar.Margin.Left + OwnWidth(IconBar);
        }
    }

    private bool _embedded;

    /// <summary>
    /// 「宿主供容器」模式：本控件只出参数条那一行，图标条、缝合三角与四周的阴影余量一律由宿主自己画。
    /// <para>存在的理由：录屏控制条要把五枚工具钮<b>并进它那一条栏</b>（与录制控制同一行），本控件那圈
    /// §7.5 外环就成了"栏里套栏"，而它自己的摆位（<see cref="ReflowBars"/> 的箭头夹取）也与宿主冲突。
    /// 关掉这三样是同一个诉求的三个落点，故只开一个开关，不留三个可能被漏设的布尔。</para>
    /// <para>贴图浮窗用默认的 false，逐像素行为不变。</para>
    /// </summary>
    public bool Embedded
    {
        get => _embedded;
        set
        {
            if (_embedded == value) return;
            _embedded = value;
            ApplyLayoutMode();
        }
    }

    /// <summary>把 <see cref="Embedded"/> 摊到可视树上：余量行列塌成 0，图标条收起，再走一次摆位。</summary>
    private void ApplyLayoutMode()
    {
        IconBar.IsVisible = !_embedded;
        Root.ColumnDefinitions[0].Width = Root.ColumnDefinitions[2].Width = new GridLength(_embedded ? 0 : 20);
        Root.RowDefinitions[0].Height = new GridLength(_embedded ? 0 : 12);
        Root.RowDefinitions[4].Height = new GridLength(_embedded ? 0 : 26);
        ReflowBars();
    }

    /// <summary>写口：摆颜色字段与取色预览。<b>不</b>报 <see cref="StylePartChanged"/>——它还被文本宿主
    /// <c>SharedAnnotationColor</c> 的 setter 当程序性回写用（点选一个文字层即触发），报出去就等于
    /// "选中的形状被那次回写改了色"。用户取色的报点在 <see cref="ApplyColorToCurrentTarget"/>。</summary>
    public void SetColor(Color color)
    {
        SelectedColor = color;
        ColorOptions.Preview.Background = new SolidColorBrush(color);
        UpdateColorVisuals();
        ParametersChanged?.Invoke();
    }

    /// <summary>
    /// 当前取色目标的实际颜色：文本语境下由共享实现给出（文字/底色/描边三选一），
    /// 其余工具恒等于全局标注色。彩虹取色窗的初值也走这里，才会"改哪个色就预置哪个色"。
    /// </summary>
    public Color CurrentTargetColor => GetCurrentTargetColor();

    private Color GetCurrentTargetColor()
    {
        if (TextController != null && TextController.TryGetTextTargetColor(out var textTarget))
            return Color.FromArgb(textTarget.A, textTarget.R, textTarget.G, textTarget.B);
        return SelectedColor;
    }

    /// <summary>逐字照抄截图 ApplyColorToCurrentTarget 的分流：文本语境先交给共享实现，其余工具统一改全局色。</summary>
    private void ApplyColorToCurrentTarget(Color color)
    {
        if (TextController != null &&
            TextController.TryApplyColorToTextTarget(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B)))
        {
            // 共享实现改完状态后经 NotifyTextPanelNeedsRefresh 回到这里刷预览，取色目标可能已不是全局色
            UpdateColorPreviewForTarget();
            return;
        }
        SetColor(color);
        RaiseStylePart(AnnotationStylePart.Color);
    }

    /// <summary>把取色预览刷成当前目标的实际颜色（宿主实现 <c>ITextAnnotationHost.NotifyTextPanelNeedsRefresh</c> 时调用）。</summary>
    public void UpdateColorPreviewForTarget()
    {
        ColorOptions.Preview.Background = new SolidColorBrush(GetCurrentTargetColor());
    }

    /// <summary>取色窗回传的颜色按「当前取色目标」落位——截图侧 OnSharedCustomColorRequested 同一口径，
    /// 直接走 <see cref="SetColor"/> 会让"给描边取色"变成"改全局标注色"。</summary>
    public void ApplyCustomColor(Color color) => ApplyColorToCurrentTarget(color);

    /// <summary>粗细胶囊圆点与聚光灯描边圆点跟随当前标注色（与截图 UpdateToolOptionLabels 同一口径）。</summary>
    private void UpdateColorVisuals()
    {
        double previewSize = Math.Clamp(ThicknessOptions.Slider.Value, 2.0, 18.0);
        ThicknessOptions.Preview.Width = previewSize;
        ThicknessOptions.Preview.Height = previewSize;
        ThicknessOptions.Preview.Fill = new SolidColorBrush(SelectedColor);
        SpotlightOptions.StrokePreview.Fill = new SolidColorBrush(SelectedColor);
        // 序号那排小样画的就是这颗色（截图侧同一笔）
        CounterOptions.SetAnnotationColor(SelectedColor);
    }

    /// <summary>从上次会话的标注参数恢复取值，让贴图浮窗与截图参数条起点一致。
    /// ⚠ 恢复记忆值<b>不是</b>"用户改动"：期间那几处滑杆赋值各自挂着按档报点，不挡住就会在进标注的瞬间
    /// 把上一次残留的选中图元改一遍样式。</summary>
    public void ApplyDefaults(ScreenCaptureTool.Core.AppSettings s)
    {
        if (s == null) return;
        ApplyCustomLayout(s);
        SuppressStyleEvents(() => ApplyRememberedValues(s));
    }

    private void ApplyRememberedValues(ScreenCaptureTool.Core.AppSettings s)
    {
        var c = ColorFromArgb(s.AnnotationColor);
        SelectedColor = c;
        ColorOptions.Preview.Background = new SolidColorBrush(c);
        ThicknessOptions.Slider.Value = Clamp(s.StrokeThickness, ThicknessOptions.Slider.Minimum, ThicknessOptions.Slider.Maximum);
        BrushOptions.Slider.Value = Clamp(s.BrushOpacity, BrushOptions.Slider.Minimum, BrushOptions.Slider.Maximum);
        SliderBrushTaper.Value = Clamp(s.BrushTaperStrength, SliderBrushTaper.Minimum, SliderBrushTaper.Maximum);
        SliderBrushSmoothing.Value = Clamp(s.BrushSmoothing, SliderBrushSmoothing.Minimum, SliderBrushSmoothing.Maximum);
        SliderBrushMinWidth.Value = Clamp(s.BrushMinWidthRatio, SliderBrushMinWidth.Minimum, SliderBrushMinWidth.Maximum);
        _brushLineStyle = ParseEnum(s.BrushLineStyle, AnnotationLineStyle.Solid);
        _shapeLineStyle = ParseEnum(s.ShapeLineStyle, AnnotationLineStyle.Solid);
        _arrowStyle = ParseEnum(s.ArrowStyle, ArrowAnnotationStyle.Designed);
        _shapeKind = ParseEnum(s.ShapeKind, ShapeAnnotationKind.Rectangle);
        BlurOptions.SyncMode(ParseEnum(s.BlurMode, BlurMode.Blur));
        BlurOptions.Slider.Value = Clamp(s.BlurUiLevel, BlurOptions.Slider.Minimum, BlurOptions.Slider.Maximum);
        SpotlightOptions.Darkness.Value = Clamp(s.SpotlightDarkness, SpotlightOptions.Darkness.Minimum, SpotlightOptions.Darkness.Maximum);
        SpotlightOptions.Stroke.Value = Clamp(s.SpotlightStrokeThickness, SpotlightOptions.Stroke.Minimum, SpotlightOptions.Stroke.Maximum);
        SyncSpotlightCornerRadius(s.SpotlightCornerRadius);
        SyncShapeCornerRadius(s.ShapeCornerRadius);
        ShapeFillOptions.SetState(s.ShapeFill, s.ShapeFillOpacity);
        CounterOptions.ApplyDefaults(s);
        // 文本参数（12 个 Text* 键）与截图共用同一份持久化值，浮窗只读不写
        TextController?.ApplyDefaults(s);
        UpdateColorVisuals();
        UpdateStyleOptionVisuals();
        UpdateColorPreviewForTarget();
    }

    private static Color ColorFromArgb(int argb) => Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    private static T ParseEnum<T>(string? text, T fallback) where T : struct
        => Enum.TryParse<T>(text, true, out var value) ? value : fallback;

    private static double Clamp(double value, double min, double max)
        => max < min ? min : Math.Clamp(value, min, max);

    // ── 逐工具显隐：照抄 CaptureWindow.ShowToolOptions() ─────────────────

    public void SetActiveTool(string? key)
    {
        _activeKey = key;

        BrushOptionsPanel.IsVisible = key == "brush";
        ArrowOptionsPanel.IsVisible = key == "arrow";
        RectangleOptionsPanel.IsVisible = key == "rectangle";
        MosaicOptionsPanel.IsVisible = key == "mosaic";
        SpotlightOptionsPanel.IsVisible = key == "spotlight";
        CounterOptionsPanel.IsVisible = key == "counter";
        EraserOptionsPanel.IsVisible = key == "eraser";
        // 面板是「点击才弹」的，切走工具后还把它支在屏幕上，就是设计稿里点名要避免的那一层常驻浮窗
        if (key != "counter") CounterOptions.CloseStylePopup();
        TextToolsPanel.IsVisible = key == "text";
        bool hasOptions = key is "brush" or "arrow" or "rectangle" or "text" or "mosaic" or "spotlight" or "counter" or "eraser";
        // 马赛克既不吃粗细也不吃颜色，与截图同一规则；文本只吃颜色不吃粗细（截图 Text 分支同样不开 CommonThicknessPanel）
        CommonThicknessPanel.IsVisible = key is "brush" or "arrow" or "rectangle";
        CommonColorPanel.IsVisible = hasOptions && key is not ("mosaic" or "eraser");

        // 切进文本工具：重置取色目标并刷新面板回显
        if (key == "text" && TextController != null)
        {
            // 与截图 ShowToolOptions 的 Text 分支逐行同口径：切进文本即把取色目标复位到「文字」，
            // 并把前景色对齐全局标注色，避免上一条文本留下的描边/填充目标串到这次来。
            TextController.ForegroundTextColor = SelectedColor;
            TextController.ColorTargetMode = 0;
            TextController.UpdateTextPanelUi();
        }

        bool hasToolSpecific = BrushOptionsPanel.IsVisible || ArrowOptionsPanel.IsVisible
            || RectangleOptionsPanel.IsVisible || MosaicOptionsPanel.IsVisible
            || SpotlightOptionsPanel.IsVisible || CounterOptionsPanel.IsVisible || EraserOptionsPanel.IsVisible || TextToolsPanel.IsVisible;
        SepAfterStyle.IsVisible = hasToolSpecific && (CommonThicknessPanel.IsVisible || CommonColorPanel.IsVisible);
        SepAfterSize.IsVisible = CommonThicknessPanel.IsVisible && CommonColorPanel.IsVisible;
        SepBeforeClear.IsVisible = hasOptions && key != "eraser";
        BtnClearCurrentTool.IsVisible = hasOptions && key != "eraser";
        OptionsBar.IsVisible = hasOptions;
        // 宿主供容器模式下箭头由宿主画（本控件的图标条根本不显示，指过去也没有落点）
        OptionsArrow.IsVisible = hasOptions && !_embedded;
        ToolTip.SetTip(BtnClearCurrentTool, key switch
        {
            "brush" => "清除所有画笔",
            "arrow" => "清除所有箭头",
            "rectangle" => "清除所有形状",
            "mosaic" => "清除所有马赛克/模糊效果",
            "spotlight" => "清除所有聚光灯",
            "counter" => "清除所有序号",
            "text" => "清除所有文本",
            _ => "清除当前工具标注",
        });

        _customLayout?.SelectTool(key);
        foreach (var child in _customLayout?.Controls ?? Tools.Children)
            if (child is AnnotationToolButton button)
                button.Classes.Set("active", button.ToolKey == key);

        CloseMenus();
        UpdateStyleOptionVisuals();
        UpdateColorVisuals();
        UpdateColorPreviewForTarget();
        ReflowBars();
    }

    /// <summary>
    /// 图标条贴浮窗右缘，参数条水平居中于当前工具按钮并夹在本控件宽度内，箭头尖对准该按钮中心
    /// ——箭头与夹取算法与截图侧整叠摆位 LayoutOverlayStack / PositionOverlayArrow 同一份，
    /// 只是"屏宽"换成"本控件宽度"，且图标条不居中（截图侧 ActionBar 铺满屏宽，居中无副作用；本浮窗居中会让
    /// 宿主的主工具栏右对齐在参数条出现时被夹取推走）。
    /// </summary>
    private void ReflowBars()
    {
        // 未接入宿主时量宽会提前缓存不完整的样式，动态组头随后无法继承全局按钮样式。
        if (_customLayout?.HasGroups == true && TopLevel.GetTopLevel(this) == null) return;
        // ⚠ 量宽前必须**递归**失效：SetActiveTool 刚翻完各面板的 IsVisible，同一拍里栏内那层 StackPanel 的
        // measure 缓存还是**上一档工具**的宽度（真控件探针实测 text→counter 量到 497 而非 380；只调栏自身的
        // InvalidateMeasure 没用——它不递归，刷不到子面板）。用过期宽度算出的 width/iconX/optionsX/箭头 X
        // 有两个后果：① 参数条被夹在旧窗口宽里，右侧「透明度」及其后各项被窗口右缘切掉，且窗口尺寸恰好没变时
        // OnSizeChanged 不再触发，错误状态永久留着；② 箭头按上一档的 iconX 定位，快速切工具时看着就是没对准。
        LayoutMetrics.InvalidateMeasureDeep(IconBar);
        IconBar.Measure(Size.Infinity);
        double iconW = OwnWidth(IconBar);
        double optionsW = 0;
        if (OptionsBar.IsVisible)
        {
            LayoutMetrics.InvalidateMeasureDeep(OptionsBar);
            OptionsBar.Measure(Size.Infinity);
            optionsW = OwnWidth(OptionsBar);
        }
        double width = Math.Max(iconW, optionsW);
        // 图标条贴浮窗右缘（截图侧的 ActionBar 铺满屏宽、居中无副作用，本浮窗不行）：宿主按
        // "主工具栏右缘对齐贴图右缘"定位，居中会让主工具栏右缘比窗口右缘短一截居中留白，参数条一出现
        // 窗口变宽、越界夹取就把整条主工具栏往左推（用户实测"弹出子工具栏时原工具栏偏移"）。
        // 贴右缘后两者恒等，参数条只向左侧伸展，主工具栏位置与工具切换彻底解耦。
        double iconX = Math.Max(0, width - iconW);

        double optionsX = 0;
        double arrowCenterX = width / 2.0;
        if (optionsW > 0)
        {
            double targetCenterX = iconX + ActiveToolCenterX();
            optionsX = Clamp(targetCenterX - optionsW / 2.0, 0, Math.Max(0, width - optionsW));
            arrowCenterX = Clamp(targetCenterX, optionsX + 7, optionsX + optionsW - 7);
        }

        // 间距行（"12,Auto,6,Auto,26" 的第 3 行）：参数条可见时 6 DIP，否则塌成 0（未选工具时浮窗净高 44）。
        // 宿主供容器模式一律 0：那一档间距由宿主的 Spacing 给，这里再给就是双份。
        Root.RowDefinitions[2].Height = new GridLength(!_embedded && optionsW > 0 ? 6 : 0);
        // 箭头尖距图标条底 2 DIP，底部越出本行 3 DIP 压住参数条顶边消除缝隙（同截图 arrowY = optionsY - 4）
        IconBar.Margin = new Thickness(iconX, 0, 0, 0);
        OptionsBar.Margin = new Thickness(optionsX, 0, 0, 0);
        OptionsArrow.Margin = new Thickness(arrowCenterX - 7, 2, 0, 0);
        OptionsArrow.IsVisible = !_embedded && optionsW > 0
            && (_customLayout != null ? _activeKey != null && _customLayout.Includes(_activeKey) : Tools.Children.OfType<AnnotationToolButton>().Any(b => b.IsVisible && b.ToolKey == _activeKey));
        LayoutChanged?.Invoke();
    }

    /// <summary>栏自身净宽度。<b>必须</b>从 DesiredSize 里减掉左右外边距：Avalonia 的 DesiredSize
    /// 已把 Margin 计进去，而本控件的 Margin 正是上一轮 <see cref="ReflowBars"/> 写进去的偏移量，
    /// 直接拿来当"内容宽度"会让 iconX 读到自己上一轮的值、宽度自指，两轮之间来回摆动并收敛成 0
    /// ——图标条从来没真正贴过右缘（实测：mL=254 立刻被下一轮改回 0）。</summary>
    private static double OwnWidth(Control bar)
        => Math.Max(0, bar.DesiredSize.Width - bar.Margin.Left - bar.Margin.Right);

    /// <summary>当前工具按钮中心相对图标条左缘的偏移；外层 1 + 内层 8 的 Padding 先算进去。</summary>
    private double ActiveToolCenterX()
    {
        const double barInset = 1 + 8;
        double x = 0;
        foreach (var child in Tools.Children)
        {
            if (child is not Control control || !control.IsVisible) continue;
            double width = control.Bounds.Width > 0
                ? control.Bounds.Width
                : control is AnnotationToolButton ? 28 : 1;
            if (ReferenceEquals(control, _activeKey == null ? null : _customLayout?.Anchor(_activeKey))
                || control is AnnotationToolButton button && button.ToolKey == _activeKey)
                return barInset + x + control.Margin.Left + width / 2.0;
            x += control.Margin.Left + width + control.Margin.Right;
        }
        return barInset + Math.Max(0, x) / 2.0;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ReflowBars();
    }

    public void SetHistoryState(bool canUndo)
    {
        UndoButton.IsEnabled = canUndo;
    }

    // ── 线型 / 笔锋 / 箭头样式 / 形状 回显 ─────────────────────────────

    private void UpdateStyleOptionVisuals()
    {
        BrushLineStylePreview.StrokeDashArray = _brushLineStyle == AnnotationLineStyle.DashLarge
            ? new AvaloniaList<double> { 2, 2 } : null;
        ToolTip.SetTip(BtnBrushLineStyle, _brushLineStyle == AnnotationLineStyle.DashLarge ? "画笔线型：虚线" : "画笔线型：实线");

        bool tapered = SliderBrushTaper.Value > 0;
        ToolTip.SetTip(BtnBrushAdvanced, tapered
            ? $"笔迹：速度笔锋 / 强度 {Math.Round(SliderBrushTaper.Value)} / 平滑 {Math.Round(SliderBrushSmoothing.Value)}"
            : "笔迹：等宽（笔锋关闭）");

        ShapeLineStylePreview.StrokeDashArray = _shapeLineStyle == AnnotationLineStyle.DashLarge
            ? new AvaloniaList<double> { 2, 2 } : null;
        ToolTip.SetTip(BtnShapeLineStyle, _shapeLineStyle == AnnotationLineStyle.DashLarge ? "形状线型：虚线" : "形状线型：实线");

        ArrowStyleSwatch.Style = _arrowStyle;
        _arrowStyleMenu.Sync(_arrowStyle);
        ToolTip.SetTip(BtnArrowStyle, "箭头样式：" + ArrowHeadStyleMenu.DisplayName(_arrowStyle));

        ShapeKindPreview.Data = Geometry.Parse(_shapeKind == ShapeAnnotationKind.Ellipse
            ? "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z"
            : "M7,4 H17 A3,3 0 0 1 20,7 V17 A3,3 0 0 1 17,20 H7 A3,3 0 0 1 4,17 V7 A3,3 0 0 1 7,4 Z");
        ToolTip.SetTip(BtnShapeKind, _shapeKind == ShapeAnnotationKind.Ellipse ? "形状：圆形" : "形状：矩形");
    }

    // ── 事件 ─────────────────────────────────────────────────────────

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is AnnotationToolButton button) ToolRequested?.Invoke(button.ToolKey);
    }

    private void OnClearAllAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToolRequested?.Invoke("clearall");
    }

    private void OnEraserSizeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        EraserSizeValue.Text = Math.Round(e.NewValue).ToString();
        ParametersChanged?.Invoke();
    }

    private void OnEraserRectangleClick(object? sender, RoutedEventArgs e)
        => SetEraserMode(EraserGestureMode.Rectangle);

    private void OnEraserBrushClick(object? sender, RoutedEventArgs e)
        => SetEraserMode(EraserGestureMode.Brush);

    private void SetEraserMode(EraserGestureMode mode)
    {
        if (EraserMode == mode) return;
        EraserMode = mode;
        BtnEraserRectangle.Classes.Set("active", mode == EraserGestureMode.Rectangle);
        BtnEraserBrush.Classes.Set("active", mode == EraserGestureMode.Brush);
        EraserBrushSizePanel.IsVisible = mode == EraserGestureMode.Brush;
        ReflowBars();
        ParametersChanged?.Invoke();
    }

    private void OnClearCurrentToolClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_activeKey != null) ToolRequested?.Invoke("clearCurrent");
    }

    // 文本参数面板那五枚按钮在复刻 XAML 里仍写死 Click="On…Click"，而 XAML 只认本控件上的同名方法，
    // 故留一行转发给共享实现（与截图侧 CaptureWindow.Text.cs 同一角色）。
    private void OnTextBoldClick(object? sender, RoutedEventArgs e) => TextController?.OnTextBoldClick(sender, e);
    private void OnTextItalicClick(object? sender, RoutedEventArgs e) => TextController?.OnTextItalicClick(sender, e);
    private void OnTextStrokeClick(object? sender, RoutedEventArgs e) => TextController?.OnTextStrokeClick(sender, e);
    private void OnTextBackgroundClick(object? sender, RoutedEventArgs e) => TextController?.OnTextBackgroundClick(sender, e);
    private void OnColorTargetButtonClick(object? sender, RoutedEventArgs e) => TextController?.OnColorTargetButtonClick(sender, e);

    private void OnBlurModeChanged(object? sender, EventArgs e)
    {
        ParametersChanged?.Invoke();
        RaiseStylePart(AnnotationStylePart.BlurSettings);
    }

    private void OnSpotlightKindChanged(object? sender, EventArgs e)
    {
        ParametersChanged?.Invoke();
        RaiseStylePart(AnnotationStylePart.SpotlightSettings);
    }

    private void OnSpotlightStrokeAdjusted(object? sender, EventArgs e) => ParametersChanged?.Invoke();

    private void OnStrokeThicknessAdjusted(object? sender, EventArgs e) => ParametersChanged?.Invoke();

    private void OnBrushLineStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleMenu(BrushLineStylePopup);
    }

    private void OnBrushAdvancedClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleMenu(BrushAdvancedPopup);
    }

    private void ToggleMenu(Popup menu)
    {
        bool show = !menu.IsOpen;
        CloseMenus();
        menu.IsOpen = show;
    }

    public void CloseMenus()
    {
        BrushLineStylePopup.IsOpen = false;
        BrushAdvancedPopup.IsOpen = false;
        _arrowStyleMenu.HideIfOpen();
        // 文本参数面板的两枚 Popup 归共享实现所有（第三枚字号 Popup 由它在代码里自建，同样经它收）
        TextController?.CloseTextPopups();
    }

    /// <summary>
    /// 收回序号样式工具栏（2026-09-19 样式减法）。宿主在序号浮条出现前调它：
    /// 与截图侧 <c>CaptureWindow.RefreshCounterPill</c> 同一互斥口径——同屏只留一条浮条。
    /// 刻意不并进 <see cref="CloseMenus"/>：那条会在每次切工具时被调，这里只认"序号浮条要出来了"这一个时机。
    /// </summary>
    public void CloseCounterStyleBar() => CounterOptions.CloseStylePopup();

    // ⚠ 条上那格「起始」输入框在本宿主上事实上只是个读数：本工具栏住在 StickerAnnotationPaletteWindow
    // 那枚 WS_EX_NOACTIVATE 不激活窗里，点它不会把前台交过来，键盘根本到不了这个框（贴图侧所有真正的打字
    // ——文本标注、序号浮条的说明文字——都挂在贴图窗口里、不挂在浮窗里）。
    // 若要开一个**真能打字**的输入框到这条栏上，必须同步把它并进 StickerWindow 键盘 Tunnel 的早退判定
    // （与 CounterPillHost.IsEditingNote 同一处理），否则在框里敲空格＝当场退出标注。

    private void OnBrushSolidItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _brushLineStyle = AnnotationLineStyle.Solid;
        BrushLineStylePopup.IsOpen = false;
        UpdateStyleOptionVisuals();
    }

    private void OnBrushDashItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _brushLineStyle = AnnotationLineStyle.DashLarge;
        BrushLineStylePopup.IsOpen = false;
        UpdateStyleOptionVisuals();
    }

    private void OnShapeKindClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _shapeKind = _shapeKind == ShapeAnnotationKind.Rectangle ? ShapeAnnotationKind.Ellipse : ShapeAnnotationKind.Rectangle;
        UpdateStyleOptionVisuals();
        RaiseStylePart(AnnotationStylePart.ShapeKind);
    }

    private void OnShapeLineStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _shapeLineStyle = _shapeLineStyle == AnnotationLineStyle.Solid ? AnnotationLineStyle.DashLarge : AnnotationLineStyle.Solid;
        UpdateStyleOptionVisuals();
        RaiseStylePart(AnnotationStylePart.ShapeLineStyle);
    }
}
