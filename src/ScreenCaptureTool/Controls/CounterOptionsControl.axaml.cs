using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 序号工具的参数（条上五段：样式小样 / 类型 / 起始值 / 大小 / 引线箭头），截图遮罩与贴图浮窗共用这一份。
///
/// <para>本控件是"下一个序号长什么样"的**唯一真相源**：宿主只读 <see cref="Current"/>，
/// 不再各自拼一份样式档。字符串枚举字段（<c>CounterSerial</c> 那一族）的解析也只在这里做一次，
/// 设置页、截图侧、贴图侧读写的都是同一批 <see cref="AppSettings"/> 键。</para>
///
/// <para>2026-09-19 样式做减法：描边宽、字体、引线粗细、说明字号退出界面（固定常量），
/// 样式层从七行面板换成 §7.5 工具栏皮的一排 7 枚成品小样。同日稍晚，「引线箭头」按用户要求回到条上
/// （「起始」右边那一格，与箭头标注工具共用同一份头型档与同一个下拉）。⚠ 三档各有去向：「类型」会回头改
/// 已经画在图上的<b>全部</b>序号（类型是显示层的事，同一编号换类型不该留下半图旧样式），发
/// <see cref="SerialKindChanged"/>；「样式」小样与「引线箭头」各发一支事件，宿主只把这一档改到
/// <b>当前选中的那一颗</b>上；起始值始终只影响下一笔，不发事件。</para>
/// </summary>
public partial class CounterOptionsControl : UserControl
{
    /// <summary>悬浮进/出延迟：250ms 挡住鼠标路过时的闪现，400ms 让鼠标从按钮斜插进栏里不中断。</summary>
    private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>滚轮改「大小」的档位——与画布上滚轮改选中序号大小同表（每格 ±2、Shift ±8）。</summary>
    private const double RadiusWheelStep = 2.0;
    private const double RadiusWheelStepFast = 8.0;

    /// <summary>浮层与屏缘的最小间距、级间距、缝合箭深入栏体的量——全部取截图侧同名常量（OverlayMargin / OverlayBarGap）。</summary>
    private const double ScreenMargin = 12;
    private const double BarGap = 6;
    private const double ArrowStitch = 4;
    private const double ArrowWidth = 14;

    /// <summary>小样格 index →（外形，底色）。无底只有第 7 枚一枚，形状恒圆。</summary>
    private static readonly CounterGlyphShape[] CellGlyph =
    {
        CounterGlyphShape.Circle, CounterGlyphShape.Circle,
        CounterGlyphShape.RoundedSquare, CounterGlyphShape.RoundedSquare,
        CounterGlyphShape.Capsule, CounterGlyphShape.Capsule,
        CounterGlyphShape.Circle,
    };

    private static readonly CounterFillMode[] CellFill =
    {
        CounterFillMode.Solid, CounterFillMode.Outline,
        CounterFillMode.Solid, CounterFillMode.Outline,
        CounterFillMode.Solid, CounterFillMode.Outline,
        CounterFillMode.None,
    };

    private static readonly CounterSerialKind[] SerialByTag =
    {
        CounterSerialKind.Number, CounterSerialKind.Letter,
        CounterSerialKind.Roman, CounterSerialKind.Chinese,
    };

    /// <summary>程序性写值（读记忆 / 回显）时的抑制位：摆控件不该反手发一次用户变更事件。</summary>
    private bool _suppress;

    /// <summary>上一次有效提交的起始值：输入框里敲了个非法内容时回退到它。</summary>
    private int _startValue = 1;

    private CounterGlyphShape _glyph = CounterGlyphShape.Circle;
    private CounterFillMode _fill = CounterFillMode.Solid;

    /// <summary>
    /// 引线箭头的头型档（默认 <see cref="ArrowAnnotationStyle.Sharp" />＝加这一档之前引线一直画的那颗实心三角）。
    /// 双去处：换档既成为<b>下一颗</b>序号的记忆值，也经 <see cref="TailHeadChanged"/> 报给宿主改到
    /// <b>当前选中的那一颗</b>上（没有选中时后者自然什么都不做）。
    /// </summary>
    private ArrowAnnotationStyle _tailHead = ArrowAnnotationStyle.Sharp;

    /// <summary>
    /// 引线箭头那一格的下拉：条目表与箭头标注工具共用 <see cref="ArrowHeadStyleMenu"/> 那一份。
    /// legacyPath 传 false——引线从来没套过箭头工具的模板，故 Designed/Sharp 两档在这里是新算式里的
    /// 深凹口与实心三角，形状与箭头工具上的那两档不同，小样必须照引线的画法出，不然"菜单里看到的"和"画出来的"分叉。
    /// <para>doubleHead 同理传 false：引线那头钉在圆点上、没地方挂第二个头，故这一版下拉十一档全是单头
    /// （箭头工具那两处是 true，名单里那三档 V 形家族在工具上两端各挂一个头）。</para>
    /// </summary>
    private readonly ArrowHeadStyleMenu _tailHeadMenu = new(legacyPath: false, doubleHead: false);

    /// <summary>
    /// 胶囊圆角（百分比 0–100）。⚠ 本控件上**没有**它的输入口——改它的是选中序号后浮上来的那条
    /// <see cref="CounterPill"/>，这里只当"下一颗胶囊沿用哪一档"的记忆位（宿主落定时调
    /// <see cref="SetCapsuleCorner"/> 回灌，新建序号从 <see cref="Current"/> 取走）。
    /// </summary>
    private float _capsuleCorner = AnnotationRenderOptions.CounterCapsuleCornerDefault;

    /// <summary>样式栏是否被"点一下钉住"（钉住后移开鼠标不收，再点一下取消）。</summary>
    private bool _pinned;

    /// <summary>起始值输入框是否正持焦（= 用户正在这一格里打字）。</summary>
    private bool _startEditing;

    /// <summary>挂在窗口根上的 Tunnel 按下处理器（持焦期间点到本控件以外任何地方都先落定焦点）。</summary>
    private EventHandler<PointerPressedEventArgs>? _rootPressHandler;

    private IDisposable? _pendingOpen;
    private IDisposable? _pendingClose;

    private Button[] _cells = Array.Empty<Button>();
    private CounterSwatch[] _cellSwatches = Array.Empty<CounterSwatch>();

    /// <summary>宿主据此把已画出的序号整体换成新类型（并补一次撤销快照）。</summary>
    public event Action<CounterSerialKind>? SerialKindChanged;

    /// <summary>「样式」小样被用户换档（外形＋底色）。宿主据此把这一档落到<b>当前选中的那一颗</b>序号上，
    /// 图上其余序号不动（2026-09-19 拍板）；没有选中序号时它自然只成为下一颗的记忆值。</summary>
    public event Action? StyleChanged;

    /// <summary>「引线箭头」下拉被用户换档。同上，只落当前选中的那一颗。</summary>
    public event Action? TailHeadChanged;

    /// <summary>
    /// 下方放不下时是否允许把样式栏翻到按钮上方。截图遮罩要（选区贴屏幕底部时下面根本没地方），
    /// 贴图浮窗关着——那边浮窗本就贴着贴图摆，弹层由宿主夹取、不参与翻边（与贴图侧"越界只夹不翻边"同一口径）。
    /// </summary>
    public bool AllowFlipUp { get; set; } = true;

    public CounterOptionsControl()
    {
        InitializeComponent();

        // PlacementTarget 在代码里挂而不是 XAML 的 {Binding #BtnStyle}：控件被两个宿主复用时，
        // 名字绑定要经过程序集里的名称查找，代码直挂少一层不确定。
        StylePopup.PlacementTarget = BtnStyle;

        // 序号类型四档＝纯文字短档名，与字号／水印位置同走 §6.12 的窄条 + 居中两条变体：
        // 不挂居中时最宽的"罗马数字 ⅠⅡⅢ"会把其余三行甩成左堆右空。
        MenuFlyoutPresenterStyle.Attach(MiSerialNumber,
            MenuFlyoutPresenterStyle.Compact, MenuFlyoutPresenterStyle.Centered);

        _cells = [Cell0, Cell1, Cell2, Cell3, Cell4, Cell5, Cell6];
        _cellSwatches = [Swatch0, Swatch1, Swatch2, Swatch3, Swatch4, Swatch5, Swatch6];

        RadiusSlider.ValueChanged += (_, _) => RadiusText.Text = Math.Round(RadiusSlider.Value).ToString();
        StartBox.KeyDown += OnStartBoxKeyDown;
        StartBox.GotFocus += (_, _) => _startEditing = true;
        // 失焦＝提交：Alt+Tab 走掉、或焦点被别处抢走时不留半截输入。点在遮罩画布上不会让输入框失焦
        // （画布与遮罩本身就不可聚焦），那一枪由挂在窗口根上的 Tunnel 按下补（见 OnAttachedToVisualTree）。
        StartBox.LostFocus += (_, _) => { _startEditing = false; CommitStartBox(); };
        StylePopup.Closed += (_, _) => _pinned = false;

        // 引线箭头：与箭头标注工具同一份下拉；点选既换"下一颗"的记忆，也报给宿主改当前选中的那一颗
        _tailHeadMenu.Picked += head =>
        {
            _tailHead = head;
            SyncTailHead();
            if (!_suppress) TailHeadChanged?.Invoke();
        };
        BtnTailHead.Flyout = _tailHeadMenu.Flyout;
        SyncTailHead();

        SyncSerial(CounterSerialKind.Number);
        SyncStyleSelection();
    }

    /// <summary>序号半径滑杆（宿主既有的 <c>SyncCounterRadiusSlider</c> 与滚轮回灌要用它）。</summary>
    public Slider Slider => RadiusSlider;

    /// <summary>半径数值文本（宿主刷新 UI 用）。</summary>
    public TextBlock ValueText => RadiusText;

    /// <summary>起始值（只影响之后新建的序号）。</summary>
    public int StartValue => _startValue;

    /// <summary>
    /// 起始值输入框此刻是否持焦。
    /// ⚠ 宿主**必须**把它并进各自的"正在打字"总闸（截图 <c>CaptureWindow.Text.HasActiveTextEditing</c> /
    /// 贴图 <c>StickerWindow</c> 键盘早退）：两侧窗口的 KeyDown 都走 Tunnel 相位，会抢在持焦的输入框之前看到按键，
    /// 不拦的话在框里敲空格＝切走工具、敲字母＝触发快捷键。
    /// </summary>
    public bool IsEditingStartValue => _startEditing;

    /// <summary>当前选中的序号类型档。</summary>
    public CounterSerialKind SerialKind { get; private set; } = CounterSerialKind.Number;

    /// <summary>
    /// 当前这套样式档的快照，供起笔时冻结进新图元。调用方是截图遮罩与贴图浮窗的建号路径。
    /// </summary>
    public CounterStyleSettings Current => new(SerialKind, _glyph, _fill, _startValue, _capsuleCorner, _tailHead);

    /// <summary>
    /// 回灌胶囊圆角（浮条改完一颗号后调用）：夹到 0–100 写进记忆位，**不**发事件也不重绘——
    /// 已经画在图上的那一颗由宿主自己改，这里只保证"下一颗胶囊沿用这个圆角"。
    /// </summary>
    public void SetCapsuleCorner(float corner)
        => _capsuleCorner = Math.Clamp(corner, 0f, 100f);

    /// <summary>
    /// 回显<b>选中那颗序号</b>自己的样式档与引线头型（宿主在选中变化时调用）：写进记忆位并摆好控件，
    /// 但<b>不</b>发 <see cref="StyleChanged"/> / <see cref="TailHeadChanged"/>——否则"选中一颗号"会反过来
    /// 把它的样式再灌回它自己，与 <see cref="SetCapsuleCorner"/>、<c>SyncShapeCornerRadius</c> 同一条规矩。
    /// 顺带的效果是"下一颗沿用这一颗的样式"，与圆角那一档的用户口径一致。
    /// </summary>
    public void SyncStyleFromShape(CounterGlyphShape glyph, CounterFillMode fill, ArrowAnnotationStyle tailHead)
    {
        _suppress = true;
        try
        {
            _glyph = glyph;
            _fill = fill;
            _tailHead = tailHead;
            SyncStyleSelection();
            SyncTailHead();
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// 标注色回灌：小样吃的是全局标注色，不跟着刷新的话"改了颜色，条上看不出来"。
    /// 宿主在各自的取色刷新口（截图 <c>UpdateToolOptionLabels</c> / 贴图 <c>UpdateColorVisuals</c>）里调用。
    /// </summary>
    public void SetAnnotationColor(Color color)
    {
        CurrentSwatch.AnnotationColor = color;
        foreach (var swatch in _cellSwatches) swatch.AnnotationColor = color;
    }

    /// <summary>
    /// 读记忆值摆控件。<paramref name="s"/> 为 null 时保持各档默认。
    /// 「记住上次的类型/外形/底色」关掉时这几档（含胶囊圆角与引线箭头）钉回默认，起始值每次回到 1。
    /// </summary>
    public void ApplyDefaults(AppSettings? s)
    {
        _suppress = true;
        try
        {
            bool remember = s?.CounterRememberStyle ?? true;
            SyncSerial(remember ? Parse(s?.CounterSerial, CounterSerialKind.Number) : CounterSerialKind.Number);
            _glyph = remember ? Parse(s?.CounterGlyph, CounterGlyphShape.Circle) : CounterGlyphShape.Circle;
            _fill = remember ? Parse(s?.CounterFill, CounterFillMode.Solid) : CounterFillMode.Solid;
            // 无底档的画面与形状无关，记忆值里若残留"无底 + 方/胶囊"这种旧组合，一律按圆重画
            if (_fill == CounterFillMode.None) _glyph = CounterGlyphShape.Circle;
            // 胶囊圆角与外形同进同退：它只对胶囊有意义，"记住样式"关掉时一并回到 100（两端全圆＝升级前的样子）
            _capsuleCorner = (float)Clamp(remember
                ? s?.CounterCapsuleCorner ?? AnnotationRenderOptions.CounterCapsuleCornerDefault
                : AnnotationRenderOptions.CounterCapsuleCornerDefault, 0, 100);
            // 引线箭头与外形/底色同进同退：受同一个"记住样式"开关管辖，关掉回到实心三角
            _tailHead = remember ? Parse(s?.CounterTailHead, ArrowAnnotationStyle.Sharp) : ArrowAnnotationStyle.Sharp;
            SyncStyleSelection();
            SyncTailHead();

            RadiusSlider.Value = Clamp(s?.CounterRadius ?? 14, RadiusSlider.Minimum, RadiusSlider.Maximum);
            RadiusText.Text = Math.Round(RadiusSlider.Value).ToString();
            SetStartValue(1);
        }
        finally
        {
            _suppress = false;
        }

        if (s != null) SetAnnotationColor(ColorFromArgb(s.AnnotationColor));
    }

    /// <summary>把当前各项写回 AppSettings（宿主的 <c>CaptureAnnotationDefaults</c> 调）。起始值刻意不落盘。</summary>
    public void CaptureDefaults(AppSettings? s)
    {
        if (s == null) return;
        s.CounterSerial = SerialKind.ToString();
        s.CounterGlyph = _glyph.ToString();
        s.CounterFill = _fill.ToString();
        s.CounterRadius = RadiusSlider.Value;
        s.CounterCapsuleCorner = _capsuleCorner;
        s.CounterTailHead = _tailHead.ToString();
    }

    /// <summary>摆起始值（不触发用户变更）：窗口初始化时置 1，撤销/重做不回灌它。</summary>
    public void SetStartValue(int value)
    {
        _startValue = Math.Clamp(value, 1, 9999);
        StartBox.Text = _startValue.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 收回样式栏与起始值输入框的焦点（切换工具、序号浮条出现、窗口收起时宿主调用）。
    /// 焦点一起摘：切走了序号工具还留着一个持焦输入框，键盘就卡在这格上了。
    /// </summary>
    public void CloseStylePopup()
    {
        _pendingOpen?.Dispose();
        _pendingOpen = null;
        _pendingClose?.Dispose();
        _pendingClose = null;
        _pinned = false;
        _tailHeadMenu.HideIfOpen();   // 引线箭头那枚下拉同属"切走就该收"的浮层
        BlurStartBox();   // 失焦那一枪自带提交，与点画布同一条路
        StylePopup.IsOpen = false;
    }

    // ── 起始值 ─────────────────────────────────────────────────────────

    /// <summary>认框里的值：非法（空 / 0 / 非数字）则把上一个有效值摆回去，不留一个看着不对的读数。</summary>
    void CommitStartBox()
    {
        if (int.TryParse(StartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 1)
            SetStartValue(v);
        else
            StartBox.Text = _startValue.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把焦点从输入框上摘掉（走失焦那条提交通路，与用户点别处同一结果）。
    /// 留着焦点，Delete / +/- 这些快捷键就全被框吃掉——所以这一句是"收起"唯一的正解，别直接写 <c>_startEditing = false</c>。
    /// </summary>
    void BlurStartBox()
    {
        if (!_startEditing) return;
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    private void OnStartBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BlurStartBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // 取消＝先把框摆回上一次有效值再落焦点，否则那一下失焦会把半截输入提交进去
            StartBox.Text = _startValue.ToString(CultureInfo.InvariantCulture);
            BlurStartBox();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 「大小」滑杆的滚轮档。滑杆自身只在持焦时才吃滚轮，而条上这些控件一律不可聚焦 ⇒ 滚轮过去没反应，
    /// 故在本控件里自己接住（与圆角浮条那条 <c>PointerWheelChanged</c> 同一套路）。
    /// 档位与画布上滚轮改选中序号大小**同一张表**（每格 ±2、Shift ±8，区间仍由滑杆自己夹）。
    /// 只写滑杆值：它的 ValueChanged → 数值回显 + 宿主的记忆与"下一笔取值"通路照旧走，这里不另开第二条落地。
    /// </summary>
    private void OnRadiusSliderWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? RadiusWheelStepFast : RadiusWheelStep;
        double next = Math.Clamp(Math.Round(RadiusSlider.Value) + (e.Delta.Y > 0 ? step : -step),
            RadiusSlider.Minimum, RadiusSlider.Maximum);
        RadiusSlider.Value = next;   // 已在边界时赋同值不触发 ValueChanged＝什么都不改
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 点在遮罩画布上时输入框不会失焦（画布与遮罩本身就不可聚焦），LostFocus 那一枪打不出来，
        // 故在窗口根上补一个 Tunnel 按下：早于任何业务处理器看到这一笔。挂在根上而不是改 CaptureSession——
        // 那个文件在重点保护清单里，为一个收身时机不值得动它。
        if (e.Root is Interactive root)
        {
            _rootPressHandler = (_, args) =>
            {
                if (!_startEditing) return;
                if (args.Source is Visual src && IsInsideStartBox(src)) return;
                BlurStartBox();
            };
            root.AddHandler(PointerPressedEvent, _rootPressHandler, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is Interactive root && _rootPressHandler != null)
        {
            root.RemoveHandler(PointerPressedEvent, _rootPressHandler);
            _rootPressHandler = null;
        }
    }

    /// <summary>该可视元素是否就是输入框自己（含其模板内的文字层）。框内拖动选字不该被当成"点到别处"。</summary>
    bool IsInsideStartBox(Visual node)
    {
        if (ReferenceEquals(node, StartBox)) return true;
        foreach (var ancestor in node.GetVisualAncestors())
            if (ReferenceEquals(ancestor, StartBox)) return true;
        return false;
    }

    // ── 样式栏：悬浮即出、移开即收，点一下钉住 ──────────────────────────

    private void OnStyleButtonClick(object? sender, RoutedEventArgs e)
    {
        // 已经开着（不论悬浮还是钉住）：这一笔把钉住翻回来——钉住时取消并收回，悬浮出来的则钉住别松手即收
        if (StylePopup.IsOpen)
        {
            if (_pinned) CloseStylePopup();
            else _pinned = true;
            return;
        }
        _pinned = true;
        ShowStyleBar();
    }

    private void OnStyleHoverEntered(object? sender, PointerEventArgs e) => ScheduleOpen();
    private void OnStyleHoverExited(object? sender, PointerEventArgs e) => ScheduleClose();
    private void OnBarHoverEntered(object? sender, PointerEventArgs e)
    {
        _pendingClose?.Dispose();
        _pendingClose = null;
    }

    private void OnBarHoverExited(object? sender, PointerEventArgs e) => ScheduleClose();

    void ScheduleOpen()
    {
        _pendingClose?.Dispose();
        _pendingClose = null;
        if (_pinned || StylePopup.IsOpen || _pendingOpen != null) return;
        _pendingOpen = DispatcherTimer.RunOnce(() =>
        {
            _pendingOpen = null;
            if (!_pinned) ShowStyleBar();
        }, HoverOpenDelay);
    }

    void ScheduleClose()
    {
        _pendingOpen?.Dispose();
        _pendingOpen = null;
        if (_pinned || !StylePopup.IsOpen || _pendingClose != null) return;
        _pendingClose = DispatcherTimer.RunOnce(() =>
        {
            _pendingClose = null;
            if (!_pinned) StylePopup.IsOpen = false;
        }, HoverCloseDelay);
    }

    /// <summary>
    /// 打开样式栏并把它钉准「样式」按钮：水平居中对齐该按钮、越出屏缘即内收（栏宽 − 2×7 内收不进时
    /// 让缝合箭去贴栏缘），下方放不下且上方有空间时整条翻到按钮上方、缝合箭方向随之反转。
    /// 算法与截图侧 <c>LayoutOverlayStack</c> / <c>PositionOverlayArrow</c> 同一份，只是目标按钮换成「样式」。
    /// </summary>
    void ShowStyleBar()
    {
        Size barSize = LayoutMetrics.MeasureNow(StyleBar);
        double barW = barSize.Width;
        double barH = barSize.Height;
        if (barW <= 0) return;

        var root = BtnStyle.GetVisualRoot() as Visual;
        double btnW = BtnStyle.Bounds.Width;
        double btnH = BtnStyle.Bounds.Height;
        if (root is null || btnW <= 0 || btnH <= 0
            || BtnStyle.TranslatePoint(new Point(btnW / 2.0, btnH), root) is not Point anchor
            || BtnStyle.TranslatePoint(new Point(btnW / 2.0, 0), root) is not Point anchorTop
            || BtnStyle.TranslatePoint(new Point(0, 0), root) is not Point targetTop)
        {
            StylePopup.IsOpen = true;   // 还没布局：先按默认摆放弹出来，别为了算位置把这一格悬浮吞掉
            return;
        }

        double roomW = Math.Max(1, root.Bounds.Width);
        double roomH = Math.Max(1, root.Bounds.Height);
        double contentH = barH + ArrowStitch;
        double x = Clamp(anchor.X - barW / 2.0, ScreenMargin, Math.Max(ScreenMargin, roomW - barW - ScreenMargin));
        bool flipUp = AllowFlipUp
            && anchor.Y + BarGap + contentH > roomH - ScreenMargin
            && anchorTop.Y - BarGap - contentH >= ScreenMargin;
        double contentTop = flipUp ? anchorTop.Y - BarGap - contentH : anchor.Y + BarGap;

        // 缝合箭：水平指准按钮中心，两端各留半枚箭头宽，越界就贴栏缘（箭头与栏体之间不脱节）
        double arrowCenterX = Clamp(anchor.X, x + ArrowWidth / 2.0, x + barW - ArrowWidth / 2.0);
        StyleBarArrow.Data = Geometry.Parse(flipUp ? "M0,0 L7,7 L14,0 Z" : "M0,7 L7,0 L14,7 Z");
        StyleBar.Margin = flipUp ? new Thickness(0) : new Thickness(0, ArrowStitch, 0, 0);
        StyleBarArrow.Margin = new Thickness(arrowCenterX - ArrowWidth / 2.0 - x, flipUp ? barH - 3 : 0, 0, 0);

        // BottomEdgeAlignedLeft：弹层左上角锚在目标左下角 + 偏移，故偏移量按"想要的屏坐标 − 锚点屏坐标"给
        StylePopup.HorizontalOffset = x - targetTop.X;
        StylePopup.VerticalOffset = contentTop - anchor.Y;
        StylePopup.IsOpen = true;
    }

    private void OnStyleCellClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int index)) return;
        e.Handled = true;
        if (index < 0 || index >= CellGlyph.Length) return;
        _glyph = CellGlyph[index];
        _fill = CellFill[index];
        SyncStyleSelection();
        // 点的是已经选中那一格时值没变，这里照发：宿主侧的分派表比过字段后返回 null，不会多留一条空撤销
        if (!_suppress) StyleChanged?.Invoke();
    }

    // ── 类型下拉 ───────────────────────────────────────────────────────

    private void OnSerialTypeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out int index)) return;
        e.Handled = true;
        if (index < 0 || index >= SerialByTag.Length) return;
        SelectSerial(SerialByTag[index]);
    }

    void SelectSerial(CounterSerialKind kind)
    {
        if (SerialKind == kind) return;
        SyncSerial(kind);
        if (!_suppress) SerialKindChanged?.Invoke(kind);
    }

    void SyncSerial(CounterSerialKind kind)
    {
        SerialKind = kind;
        TxtSerialType.Text = kind switch
        {
            CounterSerialKind.Letter => "ABC",
            CounterSerialKind.Roman => "ⅠⅡⅢ",
            CounterSerialKind.Chinese => "一二三",
            _ => "123",
        };
        // 当前档染品牌紫（§6.12 禁在 Header 上拼 ✓），其余条目回主文本色
        MiSerialNumber.Classes.Set("current", kind == CounterSerialKind.Number);
        MiSerialLetter.Classes.Set("current", kind == CounterSerialKind.Letter);
        MiSerialRoman.Classes.Set("current", kind == CounterSerialKind.Roman);
        MiSerialChinese.Classes.Set("current", kind == CounterSerialKind.Chinese);

        // 小样上写的是 1 / A / Ⅰ / 一，类型换档要一起跟上，否则条上与画布对不上
        CurrentSwatch.Serial = kind;
        foreach (var swatch in _cellSwatches) swatch.Serial = kind;
    }

    void SyncStyleSelection()
    {
        for (int i = 0; i < _cells.Length; i++)
            _cells[i].Classes.Set("on", CellGlyph[i] == _glyph && CellFill[i] == _fill);

        CurrentSwatch.Glyph = _glyph;
        CurrentSwatch.Fill = _fill;
    }

    /// <summary>摆「引线箭头」那一格：按钮上的小样、下拉里的当前档、ToolTip 上的档名（与箭头工具两处同口径）。</summary>
    void SyncTailHead()
    {
        TailHeadSwatch.Style = _tailHead;
        TailHeadSwatch.LegacyPath = false;
        _tailHeadMenu.Sync(_tailHead);
        ToolTip.SetTip(BtnTailHead, "引线箭头：" + ArrowHeadStyleMenu.DisplayName(_tailHead));
    }

    static Color ColorFromArgb(int argb) => Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    static T Parse<T>(string? name, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(name, true, out var v) ? v : fallback;

    static double Clamp(double value, double min, double max) => max < min ? min : Math.Clamp(value, min, max);
}
