using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Windows;

/// <summary>录屏鼠标特效按键枚举（左键、右键、中键）。</summary>
public enum RecordMouseButton
{
    Left,
    Right,
    Middle
}

/// <summary>
/// 录屏覆盖层：铺满虚拟屏的单窗口，兼作<b>标注的第三处画布</b>（截图遮罩、贴图之后）。
/// 承载倒计时环、选区边框、只读 REC 徽标、降级 toast、光标特效（波纹/键帽）层、标注层、文本宿主层与两枚浮条。
/// <para><b>本窗不含任何标注判据与算式</b>：落笔/端点手柄/拖动缩放/滚轮八档/参数条回灌/两枚浮条/活文字层
/// 全在 <see cref="AnnotationEditSession"/>（贴图与录屏共用那一份）里，本窗只是它的第二个消费方，
/// 实现 <see cref="IAnnotationEditHost"/> 交出坐标口径、穿透、光标与历史容器。</para>
/// <para>鼠标穿透分两段：<b>未选标注工具＝整窗 <c>WS_EX_TRANSPARENT</c> 直接放行</b>（底座应用的点击/拖拽/
/// 滚轮照常）；选中工具才摘掉它、改由 <see cref="WindowHelper.MakeSelectiveHitTestTransparent"/> 逐坐标判定，
/// 只有选区矩形内（以及贴在选区外的两枚浮条上）被本窗接管＝画板。交互控制条由独立的
/// <see cref="RecordControlBarWindow"/> 小窗口承载。</para>
/// <para>坐标口径与截图遮罩逐位相同：图元存<b>屏幕物理像素</b>、渲染时按 <c>ScreenOrigin/ScreenToDipScale</c>
/// 换回 DIP；画布 DIP 与窗口 DIP 1:1，故两个倍率恒为 1。与另两处的唯一差别是<b>没有烘焙</b>：
/// ffmpeg 抓的是活的屏幕合成，落笔即成品，撤销/重做只改模型不动像素（<see cref="AnnotationEditHistory"/>
/// 的 <c>rebuild</c> 委托因此传 null）。马赛克/模糊要真像素底图，本画布给不了，故六档工具里没有它。</para>
/// </summary>
public partial class RecordOverlayWindow : Window, IAnnotationEditHost
{
    /// <summary>倒计时环：半径 52（稿1 D04 乙案）。弧走 Path 圆弧，弧长＝角度，与线宽解耦。</summary>
    private const double RingRadius = 52;

    private DrawingRectangle _regionPhys;
    private DrawingRectangle _virtPhys = DrawingRectangle.Empty;
    private double _scale = 1.0;
    private bool _closed;
    /// <summary>Opened 时锁定的窗口句柄：卸载穿透与切换穿透态都按它记账。
    /// ⚠ 关窗后实时取会落空（贴图侧 <c>_maxTrackHwnd</c> 同一教训），落空就等于卸载不执行、留下按句柄记账的僵尸记录。</summary>
    private IntPtr _hwnd;

    private RecordControlBarWindow? _controlBarWindow;
    private DispatcherTimer? _blinkTimer;
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _mouseEffectZTimer;
    private Avalonia.Point _mouseEffectScreenPoint;
    private long _mouseEffectZUntil;
    private int _countdownTotalMs;
    private int _countdownRemainMs;
    private bool _paused;
    /// <summary>上一次写给右上徽标的读数长度。徽标只在长度真的变了（跨过 1 小时、「暂停中」出现）才重摆位，
    /// 见 <see cref="SetElapsed"/>。初值 -1＝首次必定摆。</summary>
    private int _lastBadgeLen = -1;

    /// <summary>预览态的三件引用（<see cref="EnterPreviewMask"/> 给的）：暗纱的洞对准<b>预览卡的画面</b>，
    /// 故卡一挪/一变形就要重算。null＝还没进预览态，这一层压根不画。
    /// <c>_previewRadiusDip</c> 是<b>卡自己的</b> DIP 半径（不是本窗口径），换算在 BuildPreviewMask 里做。</summary>
    private Window? _previewCard;
    private Control? _previewPlate;
    private double _previewRadiusDip;

    // ================= 鼠标动效状态（与录屏参考 1:1 对齐） =================
    private Control? _leftHoldVisual;
    private Control? _rightHoldVisual;
    private Control? _middleHoldVisual;
    private Control? _lastLeftClickVisual;
    private Control? _lastRightClickVisual;
    private DateTime _lastLeftClickTime = DateTime.MinValue;
    private DateTime _lastRightClickTime = DateTime.MinValue;
    private Avalonia.Point _lastLeftClickPos;

    // 滚轮指示器控件与定时器
    private Canvas? _scrollEffectHost;
    private TranslateTransform? _scrollEffectTranslate;
    private DispatcherTimer? _scrollEffectHideTimer;

    // 键盘按键特效控件与定时器（与录屏参考 1:1 对齐）
    private Border? _keyEffectBorder;
    private DateTime _keyEffectCreatedTime = DateTime.MinValue;
    private DispatcherTimer? _keyEffectFadeTimer;

    // ================= 对外事件（由 RecordFlow 订阅，均已在 UI 线程） =================
    public event EventHandler? CountdownFinished;
    public event EventHandler? CountdownCancelRequested;
    public event EventHandler? PauseToggleRequested;
    public event EventHandler? CompleteRequested;
    public event EventHandler? RestartRequested;
    /// <summary>键鼠特效两枚勾选（参数＝键盘、鼠标）：控制条上报，RecordFlow 那边即时翻钩子开关。</summary>
    public event Action<bool, bool>? FxChanged;

    /// <summary>选区屏幕物理矩形（RecordFlow 装钩子/热键时要用）。</summary>
    public DrawingRectangle RegionPhys => _regionPhys;

    /// <summary>此刻正在文本档里打字（含序号浮条的说明格）。本窗键盘梯子据此不吞 Esc/Delete/方向键等，
    /// 录屏键帽特效据此静音——后者不在此直接读，走 <see cref="AnnotationTypingChanged"/> 推给钩子侧缓存。</summary>
    private bool IsTypingText => _annoSession.Texts.HasActiveTextEditing || (CounterPillHost?.IsEditingNote ?? false);

    // ================= 标注（共享编辑会话的第三个消费方） =================

    /// <summary>图元 + 文字层的撤销/重做簿记。<b>不传 rebuild</b>：录屏画布是活的，历史只改模型，
    /// 图层经 <c>Document.Changed</c> 自行重绘。</summary>
    private readonly AnnotationEditHistory _annoHistory = new();
    private readonly AnnotationEditSession _annoSession;
    private string? _annoKey;
    private ToolMode _annoTool = ToolMode.None;
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor HiddenBrushCursor = new(StandardCursorType.None);

    /// <summary>标注模式下 Esc 交还鼠标、以及"该点是否落在画板上"的钩子排除判定，都只看这一个状态；
    /// RecordFlow 订阅它来刷新钩子侧的那份缓存（钩子回调里绝不读可视树）。</summary>
    public event Action? AnnotationModeChanged;

    /// <summary>撤销可用态变化（落笔/撤销/重做/清除）：控制条据此回写「撤销」钮。</summary>
    public event Action? AnnotationHistoryChanged;

    /// <summary>打字态变化（参数＝该不该静音键帽特效）：RecordFlow 订阅它翻钩子侧那一格缓存。
    /// 打字期每敲一个字母都弹一枚胶囊，而键帽层就在选区里 ⇒ 全被录进成品。</summary>
    public event Action<bool>? AnnotationTypingChanged;

    internal bool AnnotationToolActive => _annoTool != ToolMode.None;

    /// <summary>上次推给 RecordFlow 的打字态，用于只在跳变时发事件。</summary>
    private bool _typingPublished;

    internal bool CanUndoAnnotation => _annoHistory.CanUndo;

    public RecordOverlayWindow()
    {
        InitializeComponent();
        SetupNumberScale();
        // 会话在 XAML 字段就绪后建：它构造时就要订阅两枚浮条与图层的事件
        _annoSession = new AnnotationEditSession(this);
        // 文档一改就重绘由 AnnotationLayer 自己订阅（Document setter 里挂的 OnDocumentChanged），宿主不再重复挂；
        // 文档归历史持有，撤销/重做换的就是这一份实例里的内容。
        AnnotationLayer.Document = _annoHistory.Document;

        // 指针/滚轮/键盘全部<b>冒泡相位</b>注册（截图侧同一相位）：正被编辑的文字框、两枚浮条自己在内层
        // 先吃掉那一下并置 Handled，本窗的起笔分派就看不到它——贴图侧因为走隧道才需要那两道"这一下是不是
        // 浮条来的"守卫（见 IsFromRadiusPill），这里换了相位就不再需要。
        AddHandler(PointerPressedEvent, OnAnnotationPointerPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnAnnotationPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerExitedEvent, (_, _) =>
        {
            _annotationPointerPosition = null;
            AnnotationLayer.SetPointerFootprint(null, 0);
        }, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnAnnotationPointerReleased, RoutingStrategies.Bubble);
        AddHandler(PointerWheelChangedEvent, OnAnnotationPointerWheelChanged, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnAnnotationKeyDown, RoutingStrategies.Bubble);
        // Ctrl+Enter 提交在写的字：焦点短暂落在窗口而不是文本框时也要能收（与贴图同一对共享处理器）
        AddHandler(KeyDownEvent, _annoSession.Texts.OnTextEditingKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, _annoSession.Texts.OnTextEditingKeyUp,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // 打字态上报（静音录屏的键帽特效）：焦点事件走「隧道＋冒泡」双相位且 handledEventsToo——
        // 那一下常被内层控件吃掉，只挂冒泡会漏。Activated/Deactivated 两路是必须的：切去别的应用时
        // 活文字框在模型里仍是"编辑中"，但此刻的键盘输入已经不是本窗的字，键帽该照常弹。
        AddHandler(GotFocusEvent, OnTypingFocusChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(LostFocusEvent, OnTypingFocusChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Activated += (_, _) => PublishTypingState();
        Deactivated += (_, _) => PublishTypingState(typing: false);

        Opened += (_, _) =>
        {
            IntPtr hwnd = GetWindowHwnd();
            _hwnd = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: false);
                // 一次性挂上、回调按当下状态逐坐标判定：这条子类化有出口（下面 Closed 里释放）。
                // 它只负责"进画板后只有选区内收笔"这一半；整窗不挡点击由下面的 WS_EX_TRANSPARENT 兜。
                if (!WindowHelper.MakeSelectiveHitTestTransparent(hwnd, IsPointInteractive))
                    Core.AppLogger.Warn("RecordOverlayWindow: 选择性命中穿透安装失败，进画板后选区外将吞掉鼠标点击（hwnd=0x"
                        + hwnd.ToInt64().ToString("X") + "）");
                ApplyBoardClickThrough();
                WindowHelper.DisableWindowChromeEffects(hwnd);
                // 持久置顶（2026-09-29 用户问"会不会被别的软件顶掉"）：别的置顶窗（其他录屏/悬浮球/游戏覆盖层）
                // 一抢到置顶带最上层，这扇遮罩（边框、徽标、标注画板）就被压在下面。守卫拦 WM_WINDOWPOSCHANGING
                // 把插入位改回 TOPMOST，外加 1 秒兜底重断言；Closed 里按这里缓存的句柄释放（红线：不能实时取句柄）。
                WindowHelper.EnforceTopmost(hwnd);
            }
            else
            {
                // 没句柄＝两条穿透路一条都挂不上：这扇铺满虚拟屏的窗此刻正吃着全盘点击，必须留痕
                Core.AppLogger.Warn("RecordOverlayWindow: Opened 取不到窗口句柄，鼠标穿透未安装，覆盖层将吞掉全屏点击");
            }
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _blinkTimer?.Stop();
            _countdownTimer?.Stop();
            _mouseEffectZTimer?.Stop();
            // 预览态的几何回调订在**卡**身上：卡活得比本窗久，不退订就是把已关的遮罩窗一直拽在事件表里
            _previewZTimer?.Stop();
            if (_previewCard != null)
            {
                _previewCard.PositionChanged -= OnPreviewCardMoved;
                _previewCard.PropertyChanged -= OnPreviewGeometryChanged;
                _previewCard.Activated -= OnPreviewCardActivated;
            }
            if (_previewPlate != null) _previewPlate.PropertyChanged -= OnPreviewGeometryChanged;
            _previewCard = null;
            _previewPlate = null;
            // 句柄会被系统复用，退出标注/关闭必须把原 WndProc 还原回去（停止、放弃、异常都汇流到这一条）。
            // 两条子类化按后进先出拆：置顶守卫装在命中穿透之后，先拆它
            WindowHelper.StopEnforceTopmost(_hwnd);
            WindowHelper.ReleaseSelectiveHitTestTransparent(_hwnd);
            CloseControlBar();
        };
    }

    /// <summary>焦点事件双相位转发（隧道＋冒泡）：与 <see cref="PublishTypingState"/> 同一个出口。</summary>
    private void OnTypingFocusChanged(object? sender, RoutedEventArgs e)
        => PublishTypingState();

    /// <summary>
    /// 把「此刻是不是正在本窗里打字」推给 <see cref="AnnotationTypingChanged"/>（RecordFlow 的钩子侧缓存）。
    /// <b>只在跳变时发</b>：焦点在控件之间挪动会连发多条事件，没必要让钩子那一格跟着抖。
    /// <paramref name="typing"/> 供失活那一路强制按 false 结——活文字框在模型里还开着，但键盘已不归本窗，
    /// 此刻的按键是别的应用在打字，键帽特效该照常弹。
    /// </summary>
    private void PublishTypingState(bool? typing = null)
    {
        bool v = typing ?? IsTypingText;
        if (v == _typingPublished) return;
        _typingPublished = v;
        AnnotationTypingChanged?.Invoke(v);
    }

    /// <summary>
    /// Win32 命中测试回调（屏幕物理像素）。运行在窗口消息线程＝UI 线程，只做几次矩形比较。
    /// 与标注层那块裁剪矩形是<b>同一段判据</b>：能吃进来的点必然在选区内，画不到选区外。
    /// <para>两枚浮条是额外认的一格：图元压着选区上缘时浮条会翻到选区外（贴位算式在共享会话里，
    /// 只夹窗口边界不夹选区），本函数若不认它们，浮条就表现为"看得见、点不着"。</para>
    /// </summary>
    private bool IsPointInteractive(int screenXPhys, int screenYPhys)
    {
        if (_annoTool == ToolMode.None) return false;
        if (_regionPhys.Contains(screenXPhys, screenYPhys)) return true;
        return IsScreenPointOnPill(SpotlightRadiusPillHost, screenXPhys, screenYPhys)
            || IsScreenPointOnPill(CounterPillHost, screenXPhys, screenYPhys);
    }

    /// <summary>屏幕物理点是否落在某枚浮条上。浮条是用 Margin 摆在"铺满虚拟屏的 Root"上的裸元素，
    /// 故它的窗口 DIP 坐标即绝对坐标，换算与图元反变换同一口径（× <c>_scale</c> ＋ 虚拟屏原点）。</summary>
    private bool IsScreenPointOnPill(Control? pill, int screenXPhys, int screenYPhys)
    {
        if (pill is not { IsVisible: true }) return false;
        var b = pill.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return false;
        int left = _virtPhys.X + (int)Math.Round(b.X * _scale);
        int top = _virtPhys.Y + (int)Math.Round(b.Y * _scale);
        int right = left + (int)Math.Round(b.Width * _scale);
        int bottom = top + (int)Math.Round(b.Height * _scale);
        return screenXPhys >= left && screenXPhys < right && screenYPhys >= top && screenYPhys < bottom;
    }

    /// <summary>
    /// 画板开着才让系统来问本窗（摘掉 <c>WS_EX_TRANSPARENT</c>，由 <see cref="IsPointInteractive"/> 限定
    /// 只有选区内收笔）；画板关着＝整窗直接放行给下层应用，不给它任何把点击留给本窗的机会。
    /// <para>为什么不只靠那条命中测试回调：它只答 <c>WM_NCHITTEST</c> 这一问，实测兜不住全屏点击，
    /// 表现为录制期间除控制条外整机点不动；<c>WS_EX_TRANSPARENT</c> 是系统在发问前就整体跳过本窗，
    /// 与长截图全屏遮罩同一配方。</para>
    /// </summary>
    private void ApplyBoardClickThrough()
        => WindowHelper.SetWholeWindowClickThrough(_hwnd, _annoTool == ToolMode.None);

    /// <summary>
    /// 只有文本档才摘掉 <c>WS_EX_NOACTIVATE</c>：不激活窗收不到键盘焦点——鼠标点击不移交前台，
    /// 挂在自家画布上的 <c>TextBox</c> 就算拿到 Avalonia 内部焦点也打不进一个字。
    /// <para>⚠ 必须<b>在按下之前</b>就摘掉（故挂在切档时而非起笔时）：这一位是系统在"点下这一下"当帧决定的，
    /// 等本窗看见按下再摘已经晚了。退出文本档一定要挂回去，否则这扇铺满虚拟屏的窗会抢走底座应用的前台。</para>
    /// </summary>
    private void ApplyTextKeyboardPresence()
        => WindowHelper.SetWindowNoActivate(_hwnd, _annoTool != ToolMode.Text);

    /// <summary>物理像素选区 + 虚拟屏，Show() 之前调用。</summary>
    public void Setup(DrawingRectangle regionPhys, DrawingRectangle virtPhys, double dpiScale)
    {
        _regionPhys = regionPhys;
        _virtPhys = virtPhys;
        _scale = dpiScale > 0 ? dpiScale : 1.0;

        // 预铺满虚拟屏（物理→DIP），避免 Show 后二次布局闪烁
        Position = new PixelPoint(virtPhys.X, virtPhys.Y);
        Width = virtPhys.Width / _scale;
        Height = virtPhys.Height / _scale;

        // 标注层坐标系与截图遮罩同一份口径：图元存屏幕物理像素，ToDip 时减 ScreenOrigin、乘 1/DPI。
        // 铺满整窗（不是只铺选区）才能让那套换算保持不变，越出选区的部分交给下面的 Clip。
        // ⚠ ScreenOrigin 取回读的 Position 而非传入的 virtPhys：Position 经手物理→DIP→物理的往返可能差一格，
        // 而宿主正变换（TryGetAnnotationPoint）用的也是 Position，两边取同一个值才严格互逆。
        AnnotationLayer.ScreenOrigin = Position;
        AnnotationLayer.ScreenToDipScale = 1.0 / _scale;
        // 聚光灯压暗的那一层也止步于选区：与 Clip、与 Win32 命中判定用的是同一段矩形的三种单位。
        AnnotationLayer.SpotlightBoundsScreenRect = _regionPhys;
        LayoutAnnotationLayer();

        ApplySelectionBorderLayout();
        // 这里**不**建暗纱：2026-09-22（第三批）按用户拍板，"选区外压暗"属于**录制结束后的预览态**而不是录制中
        // （原话"遮罩出现在'录制中'状态，而不是在录制结束后的预览状态。说明理解错了"）。
        // 录制中本来也不该压：ffmpeg 抓的就是选区那块屏，压暗一旦越界就永久烙进成品（见 .axaml ⓪⁻ 那段）。
        // 预览态那一层的入口在 <see cref="EnterPreviewMask"/>。
    }

    /// <summary>
    /// 标注层与文本宿主层<b>成对</b>铺满整窗、可见区裁到选区（R4）。选区外的笔迹与文字必然落在抓帧 crop
    /// 之外，留着只是"看得见录不到"。两层的 DIP 宽高与 Margin 逐值相同，共享会话里那两笔浮条贴位
    /// （内容坐标 × 图层缩放系数 ＋ 图层 Margin）与文本层的画布 DIP 换算才仍然互逆。
    /// </summary>
    private void LayoutAnnotationLayer()
    {
        AnnotationLayer.Width = Width;
        AnnotationLayer.Height = Height;
        AnnotationLayer.Clip = new RectangleGeometry(SelDip());
        TextHost.Width = Width;
        TextHost.Height = Height;
        TextHost.Clip = new RectangleGeometry(SelDip());
    }

    // ================= 标注：工具切换（画板开合的唯一入口） =================

    /// <summary>
    /// 控制条那六枚钮的统一入口。<b>再点已选中的那一枚＝退出画板</b>——开关语义收在这里（与贴图宿主
    /// <c>OnAnnotationToolRequested</c> 同一条），控制条只报键名、不自己判翻转，两处才不会各判各的。
    /// </summary>
    internal void RequestAnnotationTool(string toolKey)
    {
        if (toolKey == "clearall")
        {
            _annoSession.ClearAllAnnotations();
            return;
        }
        ApplyAnnotationTool(string.Equals(_annoKey, toolKey, StringComparison.Ordinal) ? null : toolKey);
    }

    /// <summary>退出画板：Esc、停止、放弃、锁屏各条路径都汇流到这里，交还鼠标＝整窗回到全穿透。</summary>
    internal void ExitAnnotationMode() => ApplyAnnotationTool(null);

    /// <summary>
    /// 带收口的切档：先把手上那一笔/那一行字结干净，再落新状态。退出画板与换档走同一条，
    /// 因为要收的东西完全一样（贴图侧原来分两处写，收口次序的坑两边各踩过一次）。
    /// </summary>
    private void ApplyAnnotationTool(string? toolKey)
    {
        if (_annoKey == toolKey) return;

        // ⚠ 顺序即口径（与贴图 ExitAnnotationMode 逐位相同）：三处落定都会经文档通知把浮条贴回来，
        // 松手收尾也会把浮条贴到新位置，故它们必须排在"藏浮条"之前——先藏后贴＝画板上留下一条挂着的浮条。
        _annoSession.FlushToolbarStyleCommit();
        _annoSession.FlushSpotlightRadiusCommit();
        _annoSession.FlushCounterCornerCommit();
        if (_annoSession.IsEditingShape) _annoSession.EndShapeEdit(_annoSession.ActivePointer);
        _annoSession.Controller.Cancel();      // 丢掉手上那半笔：它冻结的参数已不属于新档位
        _annoSession.HideSpotlightRadiusPill();
        _annoSession.HideCounterPill();
        // 中断路径也在这里收口：按住左键拖图元的同时切档/退出，与"写字写到一半切档"是同一类
        _annoSession.Texts.EndActiveTextEditing("ToolSwitch");
        _annoSession.Texts.CloseTextPopups();
        _annoSession.ClearAllSelections();

        SetAnnotationToolState(toolKey);
    }

    /// <summary>
    /// 切档的唯一落点：写状态＋回显＋穿透/键盘位/光标/边框色。这里<b>不含</b>任何"收上一笔"的收口，
    /// 故 <see cref="ForceAnnotationToolForShape"/>（点中哪种图元就切哪种工具）也走它而不会误结手上的字。
    /// </summary>
    private void SetAnnotationToolState(string? toolKey)
    {
        _annoKey = toolKey;
        _annoTool = AnnotationToolButton.ModeOfToolKey(toolKey);
        _annotationPointerPosition = null;
        Cursor = _annoTool == ToolMode.None ? null
            : _annoTool == ToolMode.Brush || _annoTool == ToolMode.Eraser
                && _controlBarWindow?.OptionsBar.EraserMode == EraserGestureMode.Brush
                ? HiddenBrushCursor : CrossCursor;
        AnnotationLayer.SetPointerFootprint(null, 0);
        // 文本层是活可视树：只有文本档才吃命中（选中、拖本体、旋转/删除柄、光标点进文本框），
        // 其余档位下它照常显示已写好的字，但那一笔归矢量分派。
        TextHost.IsHitTestVisible = _annoTool == ToolMode.Text;
        ApplyBorderStyle();
        ApplyBoardClickThrough();
        ApplyTextKeyboardPresence();
        AnnotationModeChanged?.Invoke();
        // 切档收口时那半笔字已被结掉，这里补一次发布，免得钩子侧那格停在 true 上等一个不会来的焦点事件
        PublishTypingState();
    }

    /// <summary>点中已有图元→切到该档（不带"再点即退出"的开关语义，故绕开 <see cref="ApplyAnnotationTool"/> 的收口）。</summary>
    private void ForceAnnotationToolForShape(AnnotationShapeKind kind)
    {
        string? key = AnnotationToolButton.ToolKeyOfShapeKind(kind);
        if (key == null || _annoKey == key) return;
        SetAnnotationToolState(key);
    }

    private void ForceAnnotationToolForText()
    {
        if (_annoKey == "text") return;
        SetAnnotationToolState("text");
    }

    /// <summary>
    /// 选区边框颜色的<b>唯一</b>出处：倒计时＝紫、标注期＝紫（D2：紫＝"这块区域现在是画板"）、
    /// 暂停＝琥珀、录制＝红。原先三处各自 <c>BorderBrush = …</c> 各写一遍，加一档状态必漏。
    /// </summary>
    private void ApplyBorderStyle()
    {
        string key = CountdownPill.IsVisible || _annoTool != ToolMode.None
            ? "SctAccentBrush"
            : _paused ? "SctRecAmberBrush" : "SctRecRedBrush";
        SelectionBorder.BorderBrush = App.ResolveThemeBrush(key);
    }

    // ================= 标注：撤销 / 重做 / 清除（全部转发共享会话，本窗只补控制条读数） =================

    /// <summary>退回上一步。控制条「撤销」钮与 Ctrl+Z 共用这一份收口（会话内部已带选中态作废与参数条回写）。</summary>
    internal void UndoAnnotation()
    {
        _annoSession.FlushNudgeSession();
        _annoSession.Undo();
        AnnotationHistoryChanged?.Invoke();
    }

    internal void RedoAnnotation()
    {
        _annoSession.FlushNudgeSession();
        _annoSession.Redo();
        AnnotationHistoryChanged?.Invoke();
    }

    /// <summary>只清当前工具那一档画下的图元（文本档清的是活文字层）；判据在共享会话，与贴图同源。</summary>
    internal void ClearCurrentToolAnnotations()
    {
        if (_annoTool == ToolMode.None) return;
        _annoSession.ClearCurrentToolAnnotations();
        AnnotationHistoryChanged?.Invoke();
    }

    // ================= 标注：IAnnotationEditHost（本窗与共享会话之间的差异全收敛在这里） =================
    // 判据与算式一律不在本文件二次实现：命中派发在 AnnotationShapeEditor，落笔在
    // AnnotationInteractionController，样式分派在 AnnotationStyleApplier，滚轮八档与两枚浮条在
    // AnnotationEditSession 的分部类，文字状态机在 TextAnnotationController，历史簿记在 AnnotationEditHistory。

    AnnotationDocument IAnnotationHost.AnnotationDocument => _annoHistory.Document;
    System.Collections.Generic.IReadOnlyList<Guid> IAnnotationHost.AnnotationTextIds
        => _annoSession.Texts.TextLayers.Select(layer => layer.Id).ToArray();

    ToolMode IAnnotationHost.ActiveAnnotationTool => _annoTool;

    Rect IAnnotationHost.AnnotationContentBounds => SelDip();

    AnnotationEditHistory IAnnotationEditHost.History => _annoHistory;

    AnnotationLayer? IAnnotationEditHost.EditLayer => AnnotationLayer;

    /// <summary>参数条长在<b>另一扇窗</b>（控制条）里，取不到＝画板还没开或已收，会话各处据此整段跳过回显。</summary>
    AnnotationToolbar? IAnnotationEditHost.EditToolbar => _controlBarWindow?.OptionsBar;

    SpotlightRadiusPill? IAnnotationEditHost.RadiusPill => SpotlightRadiusPillHost;

    CounterPill? IAnnotationEditHost.CounterPillView => CounterPillHost;

    Canvas IAnnotationEditHost.TextLayerCanvas => TextHost;
    Rect IAnnotationEditHost.ErasureToTextCanvasRect(System.Drawing.RectangleF mark)
    {
        double scale = AnnotationLayer.ScreenToDipScale;
        return new Rect((mark.X - AnnotationLayer.ScreenOrigin.X) * scale,
            (mark.Y - AnnotationLayer.ScreenOrigin.Y) * scale,
            mark.Width * scale, mark.Height * scale);
    }

    IInputElement IAnnotationEditHost.PointerCaptureTarget => this;

    // 两枚浮条夹在承载面板（铺满虚拟屏的 Root）内：越界只夹不翻边，算式在会话里。
    Rect IAnnotationEditHost.PillClampBounds => Root.Bounds;

    /// <summary>截图/录屏的容差常量本就是屏幕物理像素口径，且画布 DIP 与屏幕 1:1，故倍率恒 1。</summary>
    float IAnnotationEditHost.HitTolerance => 1f;

    /// <summary>聚光灯可拖到的范围＝选区（截图侧同一档位），缺了它会一路拖出画外留下抓不回来的图元。</summary>
    DrawingRectangleF? IAnnotationEditHost.SpotlightContentBounds
        => new(_regionPhys.X, _regionPhys.Y, _regionPhys.Width, _regionPhys.Height);

    /// <summary>文字可活动范围与"选区"两档都取选区的画布 DIP：本画布原点＝窗口原点，故与图元同一段几何。</summary>
    Rect IAnnotationEditHost.GetTextValidRect() => SelDip();

    bool IAnnotationEditHost.HasTextRegion => true;

    Rect IAnnotationEditHost.TextSelectionDip => SelDip();

    bool IAnnotationEditHost.TryFindPanelResource(string key, out object? value)
        => this.TryFindResource(key, ActualThemeVariant, out value);

    /// <summary>本画布 DIP 与窗口 DIP 1:1（<see cref="AnnotationLayer.ScreenOrigin"/> 取的就是
    /// <see cref="Window.Position"/>），故这一条正变换与图层里的反变换严格互逆。</summary>
    Point IAnnotationEditHost.ToContentPoint(Point windowPoint)
        => new(Position.X + windowPoint.X * _scale, Position.Y + windowPoint.Y * _scale);

    /// <summary>
    /// 界外不起笔——Win32 那层已经放行过一次，这一道是防"选区改了、钩子矩形还没跟上"的窗口期。
    /// 与 <see cref="IAnnotationEditHost.ToContentPoint"/> 共用同一个正变换，两边不可能算成两个结果。
    /// </summary>
    bool IAnnotationHost.TryGetAnnotationPoint(Point windowPoint, out Point contentPoint)
    {
        if (!SelDip().Contains(windowPoint))
        {
            contentPoint = default;
            return false;
        }
        contentPoint = ((IAnnotationEditHost)this).ToContentPoint(windowPoint);
        return true;
    }

    /// <summary>起笔当帧冻结 18 档参数：算式收在共享参数条那一份里（贴图走同一条），本窗只在条不在时兜底。</summary>
    AnnotationGestureSettings IAnnotationEditHost.CreateGestureSettings(float arrowScale)
        => _controlBarWindow is { } bar
            ? bar.CreateGestureSettings(_annoTool)
            : AnnotationGestureSettings.Fallback(arrowScale);

    /// <summary>完整提交＝压一份历史。录屏没有成品可重烘（画布是活的），故历史构造时 rebuild 传 null；
    /// 图层经 <c>Document.Changed</c> 自行重绘，这里只把「撤销」钮的可用态报给控制条。</summary>
    void IAnnotationHost.CommitAnnotationChange()
    {
        _annoHistory.Commit();
        AnnotationHistoryChanged?.Invoke();
    }

    /// <summary>文本层只压历史：它是活可视树，编辑期由它自己画，每次提交都重排一遍全部文字层纯属白跑。</summary>
    void IAnnotationEditHost.CommitTextHistory()
    {
        _annoHistory.CommitHistoryOnly();
        AnnotationHistoryChanged?.Invoke();
    }

    /// <summary>两条预览来源（正在画的新笔 / 正在拖的既有图元）在共享会话里合流，本窗只转发。</summary>
    void IAnnotationHost.InvalidateAnnotationPreview()
    {
        _annoSession.RefreshLayerPreview();
        _annoSession.Texts.ApplyErasureMasks(_annoHistory.Document);
    }
    void IAnnotationHost.SetEraserRectanglePreview(System.Drawing.RectangleF? rect)
        => AnnotationLayer.EraserRectanglePreview = rect;
    double IAnnotationHost.AnnotationLayerRenderMs => AnnotationLayer.LastRenderMs;

    void IAnnotationEditHost.ForceToolForShape(AnnotationShapeKind kind) => ForceAnnotationToolForShape(kind);

    void IAnnotationEditHost.ForceToolForText() => ForceAnnotationToolForText();

    // 图元能被拖出画板外，不捕获就收不到后续的 Moved/Released；捕获目标一律是窗口本身。
    void IAnnotationEditHost.CapturePointer(IPointer pointer)
    {
        try { pointer.Capture(this); } catch { }
    }

    void IAnnotationEditHost.ReleasePointer(IPointer pointer)
    {
        try { pointer.Capture(null); } catch { }
    }

    // 抓手柄时不改光标：截图侧也只在拖本体那一路给 SizeAll；本画布的常态光标是十字（不是贴图的 null）。
    void IAnnotationEditHost.SetMoveCursor(bool on)
        => Cursor = on ? SizeAllCursor : (_annoTool == ToolMode.None ? null
            : _annoTool == ToolMode.Brush || _annoTool == ToolMode.Eraser
                && _controlBarWindow?.OptionsBar.EraserMode == EraserGestureMode.Brush
                ? HiddenBrushCursor : CrossCursor);

    /// <summary>合帧闸门用的渲染帧：本窗没有贴图的 RAF 循环，用 Dispatcher 的渲染优先级同一语义。</summary>
    void IAnnotationEditHost.PostRenderFrame(Action callback)
        => Dispatcher.UIThread.Post(callback, DispatcherPriority.Render);

    void IAnnotationEditHost.LogDiag(string message) => Core.AppLogger.Info("[录屏标注] " + message);

    // ================= 阶段一：倒计时 =================

    /// <summary>显示倒计时环并开跑；seconds≤0 由调用方自行跳过（「立即」档不进本方法）。</summary>
    public void StartCountdown(int seconds)
    {
        double sec = Math.Clamp(seconds, 1, Core.Recording.RecordCountdown.MaxSeconds);
        _countdownTotalMs = (int)(sec * 1000);
        _countdownRemainMs = _countdownTotalMs;

        CountdownPill.IsVisible = true;
        ApplyBorderStyle();
        CenterOnSelection(CountdownPill);
        _lastCountdownShown = null;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
        UpdateCountdownVisual();

        try { Activate(); } catch { }
    }

    /// <summary>上一次已经显示出去的整秒文字（用来判"这一拍要不要重放入场动效"，含首次显示）。</summary>
    /// <remarks>2026-09-29 按用户要求撤掉了盘上方那枚「即将开始 Esc 取消」副行（Esc 取消本身在 OnKeyDown 里，不依赖它），
    /// 于是圆盘＝倒计时的全部呈现，原先为副行写的「先量再显、上缘不够就翻到下方」摆位判定一并删除。</remarks>
    private string? _lastCountdownShown;

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _countdownRemainMs -= 50;
        if (_countdownRemainMs <= 0)
        {
            _countdownTimer?.Stop();
            CountdownPill.IsVisible = false;
            CountdownFinished?.Invoke(this, EventArgs.Empty);
            return;
        }
        UpdateCountdownVisual();
    }

    private void UpdateCountdownVisual()
    {
        double progress = Math.Clamp((double)_countdownRemainMs / _countdownTotalMs, 0, 1);
        CountdownArc.Data = BuildRingArc(progress);

        string shown = Math.Ceiling(_countdownRemainMs / 1000.0).ToString("0");
        if (shown == _lastCountdownShown) return;
        _lastCountdownShown = shown;
        TxtCountdown.Text = shown;

        // 整秒换字的那一刻把数字顶到 1.15／压到 0.35，隔一帧再落回 1.0/1.0——
        // XAML 上挂的 DoubleTransition（150ms）自动补出收缩＋淡入，这就是稿 14 的动效档（E4）。
        // 🔴 只能这么写：Animation.RunAsync 驱动 Transform 会抛 InvalidCastException 且被静默吞掉（AGENTS.md §7）。
        // 时间闸取 16ms 而不是 DispatcherPriority：本仓实测优先级排不出"下一帧"，TickCount64 步进约 15.6ms。
        _numScale.ScaleX = _numScale.ScaleY = NumberPopFrom;
        TxtCountdown.Opacity = NumberPopOpacityFrom;
        DispatcherTimer.RunOnce(SettleNumberPop, TimeSpan.FromMilliseconds(16));
    }

    /// <summary>数字入场的起始值：1.15 倍收缩到 1.0（2026-09-20 D04 甲案写过、落地时漏掉的那一条）。</summary>
    private const double NumberPopFrom = 1.15;
    private const double NumberPopOpacityFrom = 0.35;
    private const int NumberPopDurationMs = 150;

    /// <summary>
    /// 倒计时的缩放件。写在代码里而不是 XAML 里：<b>Avalonia 的 <c>x:Name</c> 只为控件生成字段</b>，
    /// <c>ScaleTransform</c> 是 <c>Animatable</c> 子类，挂上去代码侧引用不到（实测报 CS0103）。
    /// 过渡一律用 <see cref="DoubleTransition"/>：它走 <c>DoubleAnimator</c>，不做 <c>Visual</c> 强转，
    /// 对 <c>ScaleTransform</c> 合法；换成 <c>Animation.RunAsync</c> 就正是 AGENTS.md §7 那条"静默失败"的坑。
    /// </summary>
    private readonly ScaleTransform _numScale = new() { ScaleX = 1, ScaleY = 1 };

    private void SetupNumberScale()
    {
        // 缓动取三次曲线慢收：稿 14 动效档写的是 cubic-bezier(.2,.8,.3,1)（快起慢收），本仓在用的同一档就是 CubicEaseOut
        var ease = new CubicEaseOut();
        _numScale.Transitions = new Transitions
        {
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(NumberPopDurationMs), Easing = ease },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(NumberPopDurationMs), Easing = ease },
        };
        TxtCountdown.RenderTransform = _numScale;
    }

    private void SettleNumberPop()
    {
        if (!CountdownPill.IsVisible) return;   // 倒计时已结束／已取消：别把值写回一个隐藏控件上还留下一次过渡的尾巴
        _numScale.ScaleX = _numScale.ScaleY = 1;
        TxtCountdown.Opacity = 1;
    }

    /// <summary>
    /// 生成倒计时进度弧（圆心 (60,60)、半径 <see cref="RingRadius"/>）。
    /// 起点恒在正上方（-90°），**顺时针**扫过 <c>progress × 360°</c>——剩余时间越少弧越短，
    /// 视觉上就是"圈在走掉"。
    ///
    /// <para>🔴 为什么不用 <c>StrokeDashArray</c>（2026-09-22 换掉的原写法）：
    /// Avalonia 的 dash 数组单位是 <b>stroke thickness 的倍数</b>而不是像素，本处线宽 4，
    /// 而旧代码直接把像素周长 326.73 当倍数传 ⇒ 实际弧长被放大 4 倍，
    /// 剩余时间还有 75% 时弧就已经画满整圈，只有最后 25% 才看得出在缩——
    /// 用户实报「3 秒延迟，前两秒圈不动、最后一秒才动」就是这个 3×75%＝2.25 秒。
    /// 改走圆弧 Data 之后弧长直接由角度决定，不存在任何单位换算，也不可能再踩这个坑。</para>
    /// </summary>
    private static Geometry BuildRingArc(double progress)
    {
        // 圆心与半径：Grid 是 120×120，弧按整格铺，圆心落在正中
        const double cx = 60, cy = 60;
        const double x0 = cx, y0 = cy - RingRadius;   // 正上方起点

        if (progress >= 1.0)
        {
            // 满圈：用两段半圆拼（单段圆弧在起终点重合时按规范退化成 0 长，画不出东西）
            double bottom = cy + RingRadius;
            return Geometry.Parse(FormattableString.Invariant(
                $"M {x0},{y0} A {RingRadius},{RingRadius} 0 0 1 {cx},{bottom} A {RingRadius},{RingRadius} 0 0 1 {x0},{y0}"));
        }
        if (progress <= 0.0) return Geometry.Parse("M 0,0");

        double sweep = progress * 2 * Math.PI;                    // 顺时针扫过的弧度
        // 屏幕坐标 y 向下 ⇒ 顺时针参数化为 (sin, -cos)
        double x1 = cx + RingRadius * Math.Sin(sweep);
        double y1 = cy - RingRadius * Math.Cos(sweep);
        int largeArc = sweep > Math.PI ? 1 : 0;                   // >180° 必须置大弧标志
        return Geometry.Parse(FormattableString.Invariant(
            $"M {x0},{y0} A {RingRadius},{RingRadius} 0 {largeArc} 1 {x1},{y1}"));
    }

    /// <summary>外部（全局键钩子）请求取消倒计时。</summary>
    public void RequestCountdownCancel()
    {
        _countdownTimer?.Stop();
        CountdownPill.IsVisible = false;
        CountdownCancelRequested?.Invoke(this, EventArgs.Empty);
    }

    // ================= 阶段二：录制中 =================

    /// <summary>倒计时结束/立即开始时调用：切录制态外观，并显示独立浮动控制条。
    /// 重录后的第二次起跑也走这里（控制条与闪烁定时器都只造一次，靠 <see cref="RecordControlBarWindow.SetWaiting"/> 复位）。</summary>
    public void EnterRecording(bool showBadge, bool keycap, bool ripple)
    {
        CountdownPill.IsVisible = false;
        ApplyBorderStyle();

        BadgePanel.IsVisible = showBadge;
        PositionBadge();

        if (_blinkTimer == null) _blinkTimer = RecBlink.StartTimer(BadgeDot, () => _paused);

        // 创建并显示独立浮动控制栏（若尚未创建）
        EnsureControlBarCreated();
        
        _controlBarWindow!.SetFxInitial(keycap, ripple);
        _controlBarWindow.SetWaiting(false);
    }

    /// <summary>
    /// 确保控制条已创建并显示。可在录制启动前提前调用以避免启动时的延迟。
    /// 多次调用是安全的——已创建时直接返回。
    /// </summary>
    public void EnsureControlBarCreated()
    {
        if (_controlBarWindow != null) return;

        _controlBarWindow = new RecordControlBarWindow();
        _controlBarWindow.PauseToggleRequested += (s, e) => PauseToggleRequested?.Invoke(this, e);
        _controlBarWindow.CompleteRequested += (s, e) => CompleteRequested?.Invoke(this, e);
        _controlBarWindow.RestartRequested += (s, e) => RestartRequested?.Invoke(this, e);
        _controlBarWindow.FxChanged += (k, r) => FxChanged?.Invoke(k, r);
        // 标注这一路：钮只报键名，翻转/穿透/图元与历史全在本窗那份共享会话里
        _controlBarWindow.AnnotationToolRequested += key => RequestAnnotationTool(key);
        _controlBarWindow.AnnotationUndoRequested += (s, e) => UndoAnnotation();
        _controlBarWindow.AnnotationClearRequested += (s, e) => ClearCurrentToolAnnotations();
        AnnotationModeChanged += () => _controlBarWindow?.SetAnnotationTool(_annoKey);
        AnnotationHistoryChanged += () => _controlBarWindow?.SetAnnotationUndoEnabled(CanUndoAnnotation);
        // 参数条上"能回改已画好图元"的那九档与序号类型档：一档一事件，落到当前选中的那一个
        // （与贴图浮窗、截图遮罩共用同一张分派表 AnnotationStyleApplier，三处不可能判成两种结果）
        var optionsBar = _controlBarWindow.OptionsBar;
        optionsBar.ParametersChanged += () =>
        {
            UpdateAnnotationPointerFootprint();
            if (_annoTool == ToolMode.Eraser)
                Cursor = optionsBar.EraserMode == EraserGestureMode.Brush ? HiddenBrushCursor : CrossCursor;
        };
        optionsBar.StylePartChanged += parts => _annoSession.ApplyToolbarStyleToSelection(parts);
        optionsBar.CounterSerialKindChanged += kind => _annoSession.ApplyCounterSerialKindToAll(kind);
        // 文本参数面板注入 + 补一次记忆值恢复：控制条构造函数里那次 ApplyDefaults 发生在注入<b>之前</b>，
        // 文本那 12 个 Text* 键没人读，起点会与截图/贴图不一致（贴图侧同一顺序：先注入再恢复）。
        _annoSession.AttachTextPanel(optionsBar);
        try { optionsBar.ApplyDefaults(Core.AppSettings.Load()); }
        catch (Exception ex) { Core.AppLogger.Warn("录屏标注恢复标注参数失败: " + ex.Message); }
        _controlBarWindow.PositionAtRegion(_regionPhys, _virtPhys, _scale);
        // 🔴 出现即定稿（2026-09-22 用户原话"我希望它出现的时候就是刚刚好的地方，不要等后面再调一下"）：
        // Show 之前窗未 attach，SizeToContent 的真实外框要 attach 后经 DPI 取整才定稿 ⇒ 那次钉位量到的是
        // 偏窄的宽度，约 1 秒后第一拍 Repin 才把它挪到正确位置，肉眼看见"出现后再自己调一下"。
        // 走 cloak 通路把这一拍藏掉：揭开前最后一次重钉（beforeReveal），用户看到的第一帧就是终位。
        // activate:false——本窗是 WS_EX_NOACTIVATE 置顶小窗，抢前台会把焦点从用户正在打字的应用上夺走。
        WindowPresentationHelper.ShowSmoothly(_controlBarWindow,
            beforeReveal: () => _controlBarWindow.Repin(),
            activate: false);
    }

    /// <summary>
    /// 「重新录制」在下一段倒计时起跑前的复位：退出标注画板（连带把整窗穿透交回常态）、红点回到常闪、
    /// 时长读数归零、控制条进入"等待起跑"禁用态。徽标按设置重新决定露不露（与首次起跑同一判据）。
    /// </summary>
    public void PrepareForRestart()
    {
        ExitAnnotationMode();
        ClearMouseEffects();
        ClearKeyEffects();
        _paused = false;
        RecBlink.Apply(BadgeDot, false);
        BadgePanel.IsVisible = false;
        TxtBadgeTime.Text = "REC " + FormatElapsed(TimeSpan.Zero);
        TxtBadgePaused.IsVisible = false;
        _lastBadgeLen = -1;
        _controlBarWindow?.SetPaused(false);
        _controlBarWindow?.SetElapsed(TimeSpan.Zero);
        _controlBarWindow?.SetWaiting(true);
        ApplyBorderStyle();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        ApplyBorderStyle();
        RecBlink.Apply(BadgeDot, paused);
        // 🔴 顺序红线：先改可见性、再摆位。「暂停中」一显一隐会改变徽标宽度，而摆位量的是实测宽——
        // 晚一步就按旧宽度钉，右缘与选区右缘不平齐（LayoutMetrics.MeasureNow 会读到过期宽度，项目记忆已记）。
        TxtBadgePaused.IsVisible = paused;
        PositionBadge();
        _controlBarWindow?.SetPaused(paused);
    }

    public void SetElapsed(TimeSpan t)
    {
        string text = FormatElapsed(t);
        string badge = "REC " + text;
        TxtBadgeTime.Text = badge;
        // 徽标此前只在开录时摆过一次位（PositionBadge 唯一调用点在 EnterRecording），此后读数变宽它就
        // 一路往左长、右缘越出选区右缘。变宽的时机只有两种：跨过 1 小时（mm:ss → h:mm:ss）与「暂停中」出现，
        // 都是低频事件 ⇒ 按长度比较补摆一次，不每拍调（每拍一次 MeasureNow 是白付一笔布局）。
        if (badge.Length != _lastBadgeLen)
        {
            _lastBadgeLen = badge.Length;
            PositionBadge();
        }
        _controlBarWindow?.SetElapsed(t);
    }

    /// <summary>降级：徽标琥珀⚠（常亮，悬停看详情）＋一次性钉黑 toast。</summary>
    public void NotifyDegrade(string detail)
    {
        BadgeWarn.IsVisible = true;
        ToolTip.SetTip(BadgePanel, "录制降级：\n" + detail);
        ShowToast(detail);
    }

    /// <summary>提示胶囊 toast（随主题，与 FFmpeg 引导 toast 同一份全局资产），2.5s 自动消失；选区顶部居中外挂。</summary>
    public void ShowToast(string text)
    {
        TxtDegradeToast.Text = text;
        DegradeToast.IsVisible = true;
        Avalonia.Size toastSize = LayoutMetrics.MeasureNow(DegradeToast);
        Rect sel = SelDip();
        double x = sel.X + (sel.Width - toastSize.Width) / 2;
        double y = Math.Max(4, sel.Y - toastSize.Height - 12);
        if (sel.Y - toastSize.Height - 12 < 0) y = sel.Y + 8; // 贴屏幕顶：翻进选区内
        Canvas.SetLeft(DegradeToast, x);
        Canvas.SetTop(DegradeToast, y);

        DispatcherTimer.RunOnce(() =>
        {
            if (!_closed) DegradeToast.IsVisible = false;
        }, TimeSpan.FromMilliseconds(2500));
    }

    /// <summary>结束录制进入收尾态：控制条立即置灰并显示保存中，按键特效即时清理。</summary>
    public void SetFinishing()
    {
        ClearKeyEffects();
        _controlBarWindow?.SetFinishing();
    }

    // ================= 光标特效（RecordFlow 钩子喂入，须先转到 UI 线程） =================

    /// <summary>鼠标按下特效（左键、右键、中键扩散波纹条与按住指示器）。</summary>
    public void TriggerMouseDown(Avalonia.Point screenPhys, RecordMouseButton button)
    {
        if (_closed || !IsPointInRegion(screenPhys)) return;
        KeepMouseEffectsAboveTarget(screenPhys);

        double x = (screenPhys.X - _virtPhys.X) / _scale;
        double y = (screenPhys.Y - _virtPhys.Y) / _scale;
        DateTime now = DateTime.Now;

        if (button == RecordMouseButton.Left)
        {
            // 350ms 内且距离小于 25px 判定为双击
            bool isDouble = (now - _lastLeftClickTime).TotalMilliseconds <= 350
                && Math.Abs(screenPhys.X - _lastLeftClickPos.X) < 25
                && Math.Abs(screenPhys.Y - _lastLeftClickPos.Y) < 25;
            Color clickColor = isDouble ? Color.FromRgb(255, 68, 68) : Color.FromRgb(0, 191, 255);
            _lastLeftClickTime = now;
            _lastLeftClickPos = screenPhys;

            var v = ShowClickEffect(x, y, clickColor, "Left", keepVisible: true);
            _lastLeftClickVisual = v;
            _leftHoldVisual = v;

            // 120ms 内左+右点击合并为中键视觉
            if (_lastRightClickVisual != null && (now - _lastRightClickTime).TotalMilliseconds <= 120)
            {
                if (_lastRightClickVisual != null) EffectsLayer.Children.Remove(_lastRightClickVisual);
                _lastRightClickVisual = null;
                _rightHoldVisual = null;
                if (v != null) EffectsLayer.Children.Remove(v);
                var m = ShowClickEffect(x, y, Color.FromRgb(0, 191, 255), "Middle", keepVisible: true);
                _middleHoldVisual = m;
            }
        }
        else if (button == RecordMouseButton.Right)
        {
            _lastRightClickTime = now;
            var v = ShowClickEffect(x, y, Color.FromRgb(0, 191, 255), "Right", keepVisible: true);
            _lastRightClickVisual = v;
            _rightHoldVisual = v;

            // 120ms 内右+左点击合并为中键视觉
            if (_lastLeftClickVisual != null && (now - _lastLeftClickTime).TotalMilliseconds <= 120)
            {
                if (_lastLeftClickVisual != null) EffectsLayer.Children.Remove(_lastLeftClickVisual);
                _lastLeftClickVisual = null;
                _leftHoldVisual = null;
                if (v != null) EffectsLayer.Children.Remove(v);
                var m = ShowClickEffect(x, y, Color.FromRgb(0, 191, 255), "Middle", keepVisible: true);
                _middleHoldVisual = m;
            }
        }
        else if (button == RecordMouseButton.Middle)
        {
            var v = ShowClickEffect(x, y, Color.FromRgb(0, 191, 255), "Middle", keepVisible: true);
            _middleHoldVisual = v;
        }
    }

    /// <summary>鼠标抬起（按住指示器平滑淡出）。</summary>
    public void TriggerMouseUp(Avalonia.Point screenPhys, RecordMouseButton button)
    {
        if (_closed) return;
        if (_leftHoldVisual != null || _rightHoldVisual != null || _middleHoldVisual != null)
            KeepMouseEffectsAboveTarget(screenPhys);
        if (button == RecordMouseButton.Left)
        {
            FadeAndClearHoldVisual(ref _leftHoldVisual);
        }
        else if (button == RecordMouseButton.Right)
        {
            FadeAndClearHoldVisual(ref _rightHoldVisual);
        }
        else if (button == RecordMouseButton.Middle)
        {
            FadeAndClearHoldVisual(ref _middleHoldVisual);
        }
    }

    /// <summary>鼠标移动：按住拖拽时指示器平滑跟随，边界自动吸附。</summary>
    public void TriggerMouseMove(Avalonia.Point screenPhys)
    {
        if (_closed) return;
        if (_leftHoldVisual != null || _rightHoldVisual != null || _middleHoldVisual != null)
            KeepMouseEffectsAboveTarget(screenPhys);
        if (_leftHoldVisual != null) UpdateHoldVisualPosition(_leftHoldVisual, screenPhys);
        if (_rightHoldVisual != null) UpdateHoldVisualPosition(_rightHoldVisual, screenPhys);
        if (_middleHoldVisual != null) UpdateHoldVisualPosition(_middleHoldVisual, screenPhys);
    }

    /// <summary>滚轮滚动动效：方向箭头（3层 Chevron）与半透明胶囊边框，微弹淡入淡出。</summary>
    public void TriggerMouseWheel(Avalonia.Point screenPhys, bool isUp)
    {
        if (_closed || !IsPointInRegion(screenPhys)) return;
        KeepMouseEffectsAboveTarget(screenPhys);

        double x = (screenPhys.X - _virtPhys.X) / _scale;
        double y = (screenPhys.Y - _virtPhys.Y) / _scale;
        double scale = GetEffectScale();

        if (_scrollEffectHost == null)
        {
            _scrollEffectHost = new Canvas { IsHitTestVisible = false };
            _scrollEffectTranslate = new TranslateTransform(0, 0);
            _scrollEffectHost.RenderTransform = _scrollEffectTranslate;
            _scrollEffectHost.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(300) }
            };
            EffectsLayer.Children.Add(_scrollEffectHost);
        }

        UpdateScrollEffectVisual(_scrollEffectHost, isUp, scale);
        Canvas.SetLeft(_scrollEffectHost, x);
        Canvas.SetTop(_scrollEffectHost, y);
        _scrollEffectHost.Opacity = 1.0;

        NudgeScrollEffect(isUp, scale);
        RestartScrollEffectHideTimer();
    }

    /// <summary>清空所有激活的鼠标特效与指示器。</summary>
    public void ClearMouseEffects()
    {
        _mouseEffectZTimer?.Stop();
        _mouseEffectZUntil = 0;
        FadeAndClearHoldVisual(ref _leftHoldVisual);
        FadeAndClearHoldVisual(ref _rightHoldVisual);
        FadeAndClearHoldVisual(ref _middleHoldVisual);
        _lastLeftClickVisual = null;
        _lastRightClickVisual = null;
        if (_scrollEffectHost != null)
        {
            EffectsLayer.Children.Remove(_scrollEffectHost);
            _scrollEffectHost = null;
            _scrollEffectTranslate = null;
        }
        _scrollEffectHideTimer?.Stop();
        _scrollEffectHideTimer = null;
    }

    /// <summary>只在鼠标特效存活期间检查真实 Z 序；置顶带成员资格不代表没有被 Quicker 等窗口盖住。</summary>
    private void KeepMouseEffectsAboveTarget(Avalonia.Point screenPhys)
    {
        if (_closed || !IsVisible || _previewCardHwnd != IntPtr.Zero || _hwnd == IntPtr.Zero) return;
        _mouseEffectScreenPoint = new Avalonia.Point(
            Math.Clamp(screenPhys.X, _regionPhys.Left, _regionPhys.Right - 1),
            Math.Clamp(screenPhys.Y, _regionPhys.Top, _regionPhys.Bottom - 1));
        _mouseEffectZUntil = Environment.TickCount64 + 600;
        RefreshMouseEffectZOrder();
        if (_mouseEffectZTimer == null)
        {
            _mouseEffectZTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _mouseEffectZTimer.Tick += (_, _) =>
            {
                if (_closed || !IsVisible || _previewCardHwnd != IntPtr.Zero ||
                    (Environment.TickCount64 >= _mouseEffectZUntil && _leftHoldVisual == null &&
                     _rightHoldVisual == null && _middleHoldVisual == null))
                {
                    _mouseEffectZTimer.Stop();
                    return;
                }
                RefreshMouseEffectZOrder();
            };
        }
        _mouseEffectZTimer.Start();
    }

    private void RefreshMouseEffectZOrder()
    {
        // 只在特效所在位置被外部可见窗口遮住时提位，跳过自家控制条、浮条与弹层。
        IntPtr above = NativeMethods.GetWindow(_hwnd, 3); // GW_HWNDPREV
        for (int count = 0; above != IntPtr.Zero && count < 128; count++, above = NativeMethods.GetWindow(above, 3))
        {
            if (!NativeMethods.IsWindowVisible(above) || WindowHelper.IsWindowCloaked(above)) continue;
            NativeMethods.GetWindowThreadProcessId(above, out uint processId);
            if (processId == Environment.ProcessId) continue;
            if (NativeMethods.GetWindowRect(above, out var rect) &&
                _mouseEffectScreenPoint.X >= rect.Left && _mouseEffectScreenPoint.X < rect.Right &&
                _mouseEffectScreenPoint.Y >= rect.Top && _mouseEffectScreenPoint.Y < rect.Bottom)
            {
                WindowHelper.ForceTopmost(_hwnd); // 不激活、不显示隐藏窗，不更改穿透状态。
                return;
            }
        }
    }

    /// <summary>点击波纹（向后兼容）：默认触发左键点击波纹。</summary>
    public void TriggerRipple(Avalonia.Point screenPhys)
    {
        TriggerMouseDown(screenPhys, RecordMouseButton.Left);
        TriggerMouseUp(screenPhys, RecordMouseButton.Left);
    }

    private Control? ShowClickEffect(double x, double y, Color color, string kind, bool keepVisible)
    {
        if (_closed) return null;

        double scale = GetEffectScale();
        double innerRadius = 18.0 * scale;
        double barLength = 18.0 * scale;
        double barThickness = Math.Max(4.5 * scale, 3.0);
        double barRadius = barThickness / 2.0;
        double barCenterRadius = innerRadius + barLength / 2.0;

        var host = new Canvas { IsHitTestVisible = false };
        var brush = new SolidColorBrush(color);

        if (kind is "Left" or "Middle")
        {
            DrawArcBars(host, 180, 90, 4, barLength, barThickness, barRadius, barCenterRadius, brush);
        }
        if (kind is "Right" or "Middle")
        {
            DrawArcBars(host, 270, 90, 4, barLength, barThickness, barRadius, barCenterRadius, brush);
        }

        Canvas.SetLeft(host, x);
        Canvas.SetTop(host, y);
        EffectsLayer.Children.Add(host);

        // 弹性放大动画：从 0.5 弹跳放大到 1.0
        var st = new ScaleTransform(0.5, 0.5);
        st.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new BackEaseOut()
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new BackEaseOut()
            }
        };
        host.RenderTransform = st;
        host.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);

        host.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(220)
            }
        };

        DispatcherTimer.RunOnce(() =>
        {
            if (_closed) return;
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;
        }, TimeSpan.FromMilliseconds(10));

        if (!keepVisible)
        {
            DispatcherTimer.RunOnce(() =>
            {
                if (_closed) return;
                host.Opacity = 0.0;
                DispatcherTimer.RunOnce(() =>
                {
                    if (!_closed) EffectsLayer.Children.Remove(host);
                }, TimeSpan.FromMilliseconds(240));
            }, TimeSpan.FromMilliseconds(160));
        }

        return host;
    }

    private static void DrawArcBars(Canvas host, double startDeg, double rangeDeg, int segments,
        double length, double thickness, double radius, double centerDist, IBrush brush)
    {
        if (segments < 2) segments = 2;
        double step = rangeDeg / (segments - 1);

        for (int i = 0; i < segments; i++)
        {
            double deg = startDeg + i * step;

            var border = new Border
            {
                Width = length,
                Height = thickness,
                CornerRadius = new CornerRadius(radius),
                Background = brush,
                RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative)
            };

            var tg = new TransformGroup();
            tg.Children.Add(new TranslateTransform(centerDist - length / 2.0, -thickness / 2.0));
            tg.Children.Add(new RotateTransform(deg));

            border.RenderTransform = tg;
            host.Children.Add(border);
        }
    }

    private void FadeAndClearHoldVisual(ref Control? holdVisual)
    {
        var host = holdVisual;
        holdVisual = null;
        if (host == null || _closed) return;

        host.Opacity = 0.0;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_closed) EffectsLayer.Children.Remove(host);
        }, TimeSpan.FromMilliseconds(240));
    }

    private void UpdateHoldVisualPosition(Control? host, Avalonia.Point screenPhys)
    {
        if (host == null || _closed) return;
        if (_regionPhys.Width <= 0 || _regionPhys.Height <= 0) return;

        double sx = Math.Clamp(screenPhys.X, _regionPhys.Left, _regionPhys.Right);
        double sy = Math.Clamp(screenPhys.Y, _regionPhys.Top, _regionPhys.Bottom);

        double x = (sx - _virtPhys.X) / _scale;
        double y = (sy - _virtPhys.Y) / _scale;

        Canvas.SetLeft(host, x);
        Canvas.SetTop(host, y);
    }

    private static void UpdateScrollEffectVisual(Canvas host, bool isUp, double scale)
    {
        double arrowWidth = Math.Max(16.0 * scale, 10.0);
        double arrowHeight = Math.Max(10.0 * scale, 7.0);
        double gap = Math.Max(3.0 * scale, 2.0);
        double pad = Math.Max(6.0 * scale, 4.0);
        double thickness = Math.Max(2.2 * scale, 1.6);
        double framePaddingX = Math.Max(6.0 * scale, 4.0);
        double framePaddingY = Math.Max(5.0 * scale, 3.0);
        double frameCorner = Math.Max(7.0 * scale, 5.0);
        double frameStroke = Math.Max(1.4 * scale, 1.0);

        int count = 3;
        double stackHeight = count * arrowHeight + (count - 1) * gap;
        double frameWidth = arrowWidth + framePaddingX * 2.0;
        double frameHeight = stackHeight + framePaddingY * 2.0;
        double yBase = isUp ? (-stackHeight - pad) : pad;

        host.Children.Clear();

        // 半透明深天蓝外框胶囊
        var frame = new Border
        {
            Width = frameWidth,
            Height = frameHeight,
            CornerRadius = new CornerRadius(frameCorner),
            Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 0, 191, 255)),
            BorderThickness = new Thickness(frameStroke)
        };
        Canvas.SetLeft(frame, -frameWidth / 2.0);
        Canvas.SetTop(frame, yBase - framePaddingY);
        host.Children.Add(frame);

        // 3 组 Chevron 箭头
        for (int i = 0; i < count; i++)
        {
            double yOffset = i * (arrowHeight + gap);
            double tipX = arrowWidth / 2.0;
            double tipY = isUp ? 0.0 : arrowHeight;
            double baseY = isUp ? arrowHeight : 0.0;

            var path = new Avalonia.Controls.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 191, 255)),
                StrokeThickness = thickness,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Data = Geometry.Parse(FormattableString.Invariant($"M 0,{baseY} L {tipX},{tipY} L {arrowWidth},{baseY}"))
            };
            Canvas.SetLeft(path, -arrowWidth / 2.0);
            Canvas.SetTop(path, yBase + yOffset);
            host.Children.Add(path);
        }
    }

    private void NudgeScrollEffect(bool isUp, double scale)
    {
        if (_scrollEffectTranslate == null) return;
        double move = (isUp ? -1.0 : 1.0) * (4.0 * scale);
        _scrollEffectTranslate.Y = move;
        DispatcherTimer.RunOnce(() =>
        {
            if (_scrollEffectTranslate != null) _scrollEffectTranslate.Y = 0;
        }, TimeSpan.FromMilliseconds(90));
    }

    private void RestartScrollEffectHideTimer()
    {
        if (_scrollEffectHideTimer == null)
        {
            _scrollEffectHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _scrollEffectHideTimer.Tick += (s, e) =>
            {
                try { _scrollEffectHideTimer?.Stop(); } catch { }
                if (_scrollEffectHost != null)
                {
                    _scrollEffectHost.Opacity = 0.0;
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (_scrollEffectHost != null)
                        {
                            EffectsLayer.Children.Remove(_scrollEffectHost);
                            _scrollEffectHost = null;
                            _scrollEffectTranslate = null;
                        }
                    }, TimeSpan.FromMilliseconds(320));
                }
            };
        }
        try
        {
            _scrollEffectHideTimer.Stop();
            _scrollEffectHideTimer.Start();
        }
        catch { }
    }

    private double GetEffectScale()
    {
        Rect sel = SelDip();
        if (sel.Width <= 0 || sel.Height <= 0) return 1.0;
        double s = Math.Min(sel.Width / 800.0, sel.Height / 600.0);
        return Math.Clamp(s, 0.4, 1.0);
    }

    private bool IsPointInRegion(Avalonia.Point screenPhys)
    {
        return screenPhys.X >= _regionPhys.Left && screenPhys.X <= _regionPhys.Right &&
               screenPhys.Y >= _regionPhys.Top && screenPhys.Y <= _regionPhys.Bottom;
    }

    /// <summary>按键动效（1:1 对标录屏参考 ShowKeyEffect）：半透明圆角黑色背景、高亮白字，支持响应式字号与内边距。</summary>
    public void ShowKeycap(string keyText)
    {
        if (_closed) return;

        ClearKeyEffects();

        double s = GetEffectScale();
        double fontSize = Math.Max(28.0 * s, 24.0);
        double padX = Math.Max(16.0 * s, 12.0);
        double padY = Math.Max(8.0 * s, 4.0);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(padX, padY, padX, padY),
            Margin = new Thickness(0),
            IsHitTestVisible = false
        };

        var textBlock = new TextBlock
        {
            Text = keyText,
            Foreground = Brushes.White,
            FontSize = fontSize,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap
        };

        border.Child = textBlock;
        _keyEffectBorder = border;
        _keyEffectCreatedTime = DateTime.Now;

        EffectsLayer.Children.Add(border);
        UpdateKeyPanelPosition();

        if (_keyEffectFadeTimer == null)
        {
            _keyEffectFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _keyEffectFadeTimer.Tick += (s, e) => UpdateKeyEffectFade();
        }
        _keyEffectFadeTimer.Start();
    }

    /// <summary>更新按键特效渐隐生命周期（1:1 对标录屏参考 UpdateEffects）：持续 2 秒线性淡出并移除。</summary>
    private void UpdateKeyEffectFade()
    {
        if (_keyEffectBorder != null)
        {
            double elapsed = (DateTime.Now - _keyEffectCreatedTime).TotalSeconds;
            if (elapsed > 2.0)
            {
                ClearKeyEffects();
            }
            else
            {
                _keyEffectBorder.Opacity = 1.0 - (elapsed / 2.0);
            }
        }
    }

    /// <summary>清空并移除当前活动的按键特效与定时器（1:1 对标录屏参考 ClearKeyEffects）。</summary>
    public void ClearKeyEffects()
    {
        _keyEffectFadeTimer?.Stop();
        if (_keyEffectBorder != null)
        {
            EffectsLayer.Children.Remove(_keyEffectBorder);
            _keyEffectBorder = null;
        }
    }

    /// <summary>计算按键特效卡片在选区及屏幕边界内的展示位置（1:1 对标录屏参考 UpdateKeyPanelPosition）。</summary>
    private void UpdateKeyPanelPosition()
    {
        if (_keyEffectBorder == null) return;

        Avalonia.Size size = LayoutMetrics.MeasureNow(_keyEffectBorder);
        Rect sel = SelDip();

        // 默认放置在选区内部左下角，边距 20
        double x = sel.Left + 20.0;
        double y = sel.Bottom - 20.0 - size.Height;

        // 限制在有效画布与选区范围内
        double maxWidth = Bounds.Width > 0 ? Bounds.Width : (_virtPhys.Width / _scale);
        double maxHeight = Bounds.Height > 0 ? Bounds.Height : (_virtPhys.Height / _scale);

        if (x < 10.0) x = 10.0;
        if (x + size.Width > maxWidth - 10.0) x = maxWidth - size.Width - 10.0;
        if (y < 10.0) y = 10.0;
        if (y + size.Height > maxHeight - 10.0) y = maxHeight - size.Height - 10.0;

        // 如果选区尺寸足够，优先保证在选区内部
        if (sel.Width >= size.Width + 20.0 && x + size.Width > sel.Right - 10.0)
            x = sel.Right - size.Width - 10.0;
        if (sel.Height >= size.Height + 20.0 && y < sel.Top + 10.0)
            y = sel.Top + 10.0;

        Canvas.SetLeft(_keyEffectBorder, x);
        Canvas.SetTop(_keyEffectBorder, y);
    }

    // ================= 标注：指针与键盘分派（相位、时序、判据与截图/贴图同一份） =================

    /// <summary>
    /// 起笔。只认左键，且必须当帧从参数条冻结出 <see cref="AnnotationGestureSettings"/>
    /// （起笔后改滑杆不回写这一笔，与截图/贴图同一语义）。
    /// <para>派发次序照抄截图遮罩：<b>先</b>浮条结字 → <b>再</b>编辑既有图元（Ctrl 或已选中图元的手柄）→
    /// <b>再</b>文本档的活文字层 → 都没命中才起新笔。反过来就会把"点中已画的箭头"画成一个新的箭头。</para>
    /// <para>⚠ <c>e.Pointer.Capture(this)</c> 不是可选项：本窗按坐标选择性穿透，指针一旦拖出选区，
    /// 系统就不再问本窗，<b>移动与松手会整段漏给底下的应用</b>——那半笔预览从此挂在图层上出不来。
    /// 捕获走 Win32 的 SetCapture，它优先于命中测试，故拖出选区期间手势照走、松手照常提交。</para>
    /// </summary>
    private void OnAnnotationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_annoTool == ToolMode.None) return;
        // 冒泡相位：正被编辑的文本框、两枚浮条已经吃掉那一下并置了 Handled，本窗不插手（截图侧同一条）
        if (e.Handled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 控制条上那几枚菜单（线型／笔锋／箭头头型／文本参数）没有"点别处自动收"的通路——本窗是不激活窗，
        // 让 Popup 自己去抢前台会把刚拿到的键盘焦点抢走，故由宿主在每次起笔前主动收（贴图同一动作）。
        _controlBarWindow?.OptionsBar.CloseMenus();

        // 正在写序号说明时点到画面上：这一笔只用来结字，吞掉它——否则同一下点击会既提交说明、
        // 又在落点盖一颗新序号（贴图侧 2026-09-19 实报，同一收口）。
        if (CounterPillHost.TryCommitNoteFromCanvasPress())
        {
            e.Handled = true;
            return;
        }

        var winPos = e.GetPosition(this);
        bool textToolCurrent = _annoKey == "text";
        bool eraserToolCurrent = _annoTool == ToolMode.Eraser;
        if (eraserToolCurrent && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _annoSession.RaiseTargetAboveEraser(winPos, e.GetPosition(TextHost));
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !eraserToolCurrent)
        {
            // Ctrl+点击＝编辑已有图元。"点中矢量本体 / 点中手柄 / 点中文本层"三级由共享派发按同一条
            // z 序一次判完，与截图遮罩不可能漂移；未命中时内部已清掉选中并返回 false。
            if (_annoSession.TryBeginShapeEdit(winPos, e, editExisting: true))
            {
                e.Handled = true;
                return;
            }
            // 文本工具下 Ctrl 点空：截图侧明确"没命中其他图形也不该创建新文本"，此处同样什么都不做
            if (textToolCurrent)
            {
                e.Handled = true;
                return;
            }
        }
        else
        {
            // 免 Ctrl 的唯一例外＝已选中图元的手柄；文本工具下连这个例外也没有（截图侧那条通路先 return）
            if (!textToolCurrent && !eraserToolCurrent && _annoSession.TryBeginShapeEdit(winPos, e, editExisting: false))
            {
                e.Handled = true;
                return;
            }
            if (textToolCurrent && _annoSession.TryHandleTextPointerPressed(winPos, e))
            {
                // 文本档下的普通按下整体归共享文本状态机，不进矢量图笔状态机。
                // 再推一把激活：WS_EX_NOACTIVATE 已在进文本档时摘掉（见 ApplyTextKeyboardPresence），
                // 这里只是把"点了画板就该有键盘"变成确定动作，而不是等系统的隐式激活。
                try { Activate(); } catch { }
                e.Handled = true;
                return;
            }
        }

        var controller = _annoSession.Controller;
        // 自愈：PointerReleased 会丢（贴图侧 v7 实测过一次），丢一次就让控制器永久停在 IsDrawing 上，
        // 而 Begin 开头的判据正是"上一笔还没收口就拒绝"⇒ 画板当场表现为"怎么点都画不出来"。
        controller.Cancel();
        if (!controller.Begin(winPos, ((IAnnotationEditHost)this).CreateGestureSettings(_annoSession.ArrowScale))) return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnAnnotationPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_annoTool == ToolMode.None) return;
        UpdateAnnotationPointerFootprint(e.GetPosition(AnnotationLayer));

        // 文本层拖动 / 旋转中：与图元拖动同一优先级、同样先接管（指针捕获在本窗）
        if (_annoSession.Texts is { IsDraggingText: true } or { IsRotatingText: true })
        {
            _annoSession.Texts.TryHandleGlobalPointerMoved(sender, e);
            e.Handled = true;
            return;
        }

        // 图元编辑手势（拖本体 / 拖手柄）进行中：优先于控制器，提前 return 才不会被下面覆盖
        if (_annoSession.IsEditingShape)
        {
            // 兜住"收不到 Released"：捕获被外部夺走（弹菜单/系统对话框）时若不判左键，
            // 就变成松手后鼠标一动、图元跟着一动。
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                _annoSession.RequestShapeMove(e.GetPosition(this));
            else
                _annoSession.EndShapeEdit(_annoSession.ActivePointer);
            e.Handled = true;
            return;
        }

        if (_annoSession.Controller is { IsDrawing: true } controller)
        {
            controller.Move(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private Point? _annotationPointerPosition;

    private void UpdateAnnotationPointerFootprint(Point? position = null)
    {
        if (position.HasValue) _annotationPointerPosition = position;
        if ((_annoTool != ToolMode.Brush && !(_annoTool == ToolMode.Eraser
                && _controlBarWindow?.OptionsBar.EraserMode == EraserGestureMode.Brush))
            || !_annotationPointerPosition.HasValue)
        {
            AnnotationLayer.SetPointerFootprint(null, 0);
            return;
        }
        double size = _annoTool == ToolMode.Brush
            ? (_controlBarWindow?.OptionsBar.StrokeThickness ?? 1)
            : (_controlBarWindow?.OptionsBar.EraserDiameter ?? 1);
        AnnotationLayer.SetPointerFootprint(_annotationPointerPosition,
            size * AnnotationLayer.ScreenToDipScale);
    }

    /// <summary>收笔。未达阈值的退化一笔不产图元、也不压历史（<c>End</c> 返回 null），撤销栈里不会多出空档。</summary>
    private void OnAnnotationPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_annoTool == ToolMode.None) return;

        if (_annoSession.Texts is { IsDraggingText: true } or { IsRotatingText: true })
        {
            _annoSession.Texts.TryHandleGlobalPointerReleased(sender, e);
            e.Handled = true;
            return;
        }

        if (_annoSession.IsEditingShape)
        {
            _annoSession.EndShapeEdit(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_annoSession.Controller is { IsDrawing: true } controller)
        {
            AnnotationShape? committed = controller.End(
                e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            try { e.Pointer.Capture(null); } catch { }
            if (committed != null)
            {
                // 与截图侧每个 Finish* 收尾的同一动作：画完自动选中，手柄当场出现，
                // Delete / 方向键步进 / 拖手柄都无需再 Ctrl 点一次。
                _annoSession.ClearAllSelections();
                _annoSession.SelectShape(committed.Id);
            }
            e.Handled = true;
        }
    }

    /// <summary>标注期滚轮：八档分派表在共享会话里（与截图/贴图同一张）。被消费掉就不许再往下走——
    /// 本窗没有缩放窗口的通路，但"消费掉"这件事本身要保证事件不外溢到底座应用。</summary>
    private void OnAnnotationPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_annoTool == ToolMode.None) return;
        if (_annoSession.TryHandleWheel(e)) e.Handled = true;
    }

    /// <summary>
    /// 标注期键盘：撤销 / 重做 / 删除 / 方向键步进 / 序号编号 / Esc 退出画板。
    /// 与截图、贴图同一条早退：<b>正在打字时一个都不接管</b>——Ctrl+Z 撤的是文本框里的字，
    /// Delete 删的是选中的那几个字，Esc 在说明格里是"取消这次输入"。
    /// </summary>
    private void OnAnnotationKeyDown(object? sender, KeyEventArgs e)
    {
        if (CountdownPill.IsVisible && e.Key == Key.Escape)
        {
            RequestCountdownCancel();
            e.Handled = true;
            return;
        }

        if (_annoTool == ToolMode.None || e.Handled) return;
        if (IsTypingText) return;

        // 非方向键按下＝这段步进到此为止：下一次方向键另起一段、另算一条撤销记录
        bool isArrow = e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
        if (!isArrow) _annoSession.FlushNudgeSession();

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape:
                // 全局钩子那一条也会投一次退出（两条来路同一收口，ApplyAnnotationTool 幂等）；
                // 本窗拿到焦点时先就地退掉，省一拍投递。
                ExitAnnotationMode();
                e.Handled = true;
                return;
            case Key.Delete or Key.Back when !ctrl:
                _annoSession.DeleteSelected();
                e.Handled = true;
                return;
            case Key.Z when ctrl:
                UndoAnnotation();
                e.Handled = true;
                return;
            case Key.Y when ctrl:
                RedoAnnotation();
                e.Handled = true;
                return;
        }

        if (isArrow)
        {
            int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
            int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            if (_annoSession.StepSelectedShape(dx, dy))
            {
                e.Handled = true;
                return;
            }
        }

        // 选中序号的键盘微调（与截图/贴图逐条一致）：+ / − 改**这一个**的编号，一步 1、下限 1。
        // 带任何修饰键一律不接管；没选中序号时不吞按键。这里刻意没有"A 加说明文字"那一档——
        // 截图侧它是可改键，录屏画布没有可改键表，入口走浮条中间那一格。
        if (e.KeyModifiers == KeyModifiers.None && _annoSession.SelectedShape() is CounterShape)
        {
            int numberStep = e.Key switch
            {
                Key.Add or Key.OemPlus => 1,
                Key.Subtract or Key.OemMinus => -1,
                _ => 0,
            };
            if (numberStep != 0)
            {
                _annoSession.StepCounterNumber(numberStep, "键盘");
                e.Handled = true;
            }
        }
    }

    // ================= 布局与杂项辅助 =================

    private IntPtr GetWindowHwnd()
    {
        try { return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// 选区边框整条描在选区<b>外</b>侧。Avalonia 的 <c>Border</c> 把描边画在自身矩形内侧，所以先把框
    /// 放大 <c>线宽＋1 DIP</c>，让那条描边正好落在 [外沿−3, 外沿−1)：多留的 1 DIP 是给非整数 DPI 的取整
    /// 余量（1.5× 下 1 DIP＝1.5 物理像素，一点余量不留时描边内沿会被舍进抓帧 crop）。
    /// ⚠ 不许压进选区内沿哪怕一像素：ddagrab／gdigrab 抓的就是选区那块屏，压进去的颜色会永久烙进母带四边
    /// （现象＝预览窗里画面内圈一圈红框，且暂停档是琥珀色、倒计时档是紫色）。
    /// </summary>
    private void ApplySelectionBorderLayout()
    {
        Rect dip = SelDip();
        double outset = SelectionBorder.BorderThickness.Left + 1;   // 线宽与 .axaml 那条同源，不在此另写一份
        Canvas.SetLeft(SelectionBorder, dip.X - outset);
        Canvas.SetTop(SelectionBorder, dip.Y - outset);
        SelectionBorder.Width = dip.Width + outset * 2;
        SelectionBorder.Height = dip.Height + outset * 2;
    }

    /// <summary>
    /// 选区外暗化层（预览态）：整窗矩形 ＋ 预览卡画面洞，EvenOdd 一次成型；几何与浓度都取自截图那层的共享件
    /// <see cref="SelectionVisuals"/>（2026-09-22 用户原话"实现直接复用截图那层现成的挖洞几何和浓淡，
    /// 跟截图保持一致就行"）。铺满虚拟屏⇒多屏时其它屏幕一起压暗，这也是截图层的现行口径。
    /// <para>洞带圆角（2026-09-24）：洞此前恒为四点直角矩形，而预览卡是圆角带投影的一张卡 ⇒ 四个角缝里
    /// 漏出没压暗的桌面，用户实报"预览下遮罩是直角的"。现在洞的半径由卡那边同读「截图圆角」设置算好后转交
    /// （<see cref="EnterPreviewMask"/>），轮廓走截图成品那条 <c>SelectionEffects.BuildOutline</c> 超椭圆
    /// ⇒ 暗纱的洞、卡片的弧、截图选框的预览三处同一个弧度，不再各算各的。</para>
    /// <para>🔴 转交的那一枚必须是卡的 <c>PlateRadiusDip</c> 本身，<b>不是</b>宿主光晕用的 <c>+1</c> 那枚
    /// （2026-09-25 用户实报"视频跟遮罩，还有描边是三个东西吗"）：洞与卡同矩形时"半径 +1"不是往外扩而是
    /// <b>往里切得更多</b>（超椭圆角部离矩形角点 0.1295×半径，半径越大越远离角点）⇒ 洞反而比卡小，
    /// 是"同族缩放"那一类错（同 2026-09-06 成品阴影的病根）。真要留余量得把洞的<b>矩形</b>一起外扩，
    /// 而外扩会在卡的直边外露出一圈没压暗的桌面 ⇒ 不留余量，洞与卡同一条曲线。</para>
    /// <para>🔴 hole 必须按<b>窗口真实原点</b>反算：用 <see cref="Window.Position"/> 而不是 <c>_virtPhys</c>。
    /// 后者经手"物理→DIP→物理"的往返可能差一格（<see cref="Setup"/> 里 ScreenOrigin 取 Position 就是同一教训），
    /// 差一格在预览态只是"暗纱啃掉画面一条边"那么难看——预览期不再抓屏，烙不进成品，但也够用户报一轮。</para>
    /// <para>卡内偏移乘的是<b>卡自己的</b> <c>RenderScaling</c>，不是本窗的 <c>_scale</c>：多屏不同 DPI 时两者不等，
    /// 拿错缩放，卡越靠右下洞偏得越多。</para>
    /// <para>全屏豁免与截图层同判据：卡几乎铺满整屏时无处可暗，画了只会白闪一下，故整层收起。</para>
    /// </summary>
    private void BuildPreviewMask()
    {
        // 🔴 收起闸门：卡被收进任务栏那一段时间里，暗纱整层不许被点回来（2026-09-29 用户实报"点击最小化，遮罩没有消失"）。
        // 必要性在于下面那三根跟随钩子——最小化那一刻卡的 Position／Bounds 正在跳（系统把窗挪到屏幕外），
        // 钩子**必然**发，只把 `SelectionMask.IsVisible` 置 false 会被紧随其后的这一趟重算直接覆盖回 true。
        if (_previewMaskGated) return;

        Window? card = _previewCard;
        Control? plate = _previewPlate;
        if (card == null || plate == null) return;

        Rect plateDip = plate.Bounds;
        if (plateDip.Width <= 0 || plateDip.Height <= 0) return;   // 卡还没布局：它一变就会再叫这一趟

        double cardScale = card.RenderScaling;
        int px = card.Position.X + (int)Math.Round(plateDip.X * cardScale);
        int py = card.Position.Y + (int)Math.Round(plateDip.Y * cardScale);
        int pw = (int)Math.Round(plateDip.Width * cardScale);
        int ph = (int)Math.Round(plateDip.Height * cardScale);

        // 本窗 DIP 口径的洞（与 AnnotationLayer 的正反变换同一个原点）
        Rect hole = new((px - Position.X) / _scale, (py - Position.Y) / _scale, pw / _scale, ph / _scale);

        // 🔴 卡整块不在本窗覆盖的范围里 ⇒ 这一层什么都不画（2026-09-29）。最小化时系统把卡挪到屏幕外
        // （Avalonia 会把那个位置推成 `Position` 变更，正是它触发了上面那道闸），从任务栏唤回的那一拍
        // 若 Avalonia 还没把真实位置推回来，这里读到的仍是屏外坐标——**洞在窗外等于满屏无洞**，
        // 画出来就是一下整屏压暗。不画、等下一次钩子带正确几何再来。
        if (hole.Right <= 0 || hole.Bottom <= 0 || hole.X >= Width || hole.Y >= Height)
        {
            SelectionMask.IsVisible = false;
            return;
        }

        bool coversWholeScreen = hole.X <= 1 && hole.Y <= 1
            && hole.Right >= Width - 2.0 && hole.Bottom >= Height - 2.0;
        if (coversWholeScreen)
        {
            SelectionMask.IsVisible = false;
            return;
        }

        SelectionMask.Fill = SelectionVisuals.MaskBrush;
        // 洞的圆角＝卡自己的那道弧（同一条超椭圆算法、同一个来源），换算走"卡 DIP → 物理 → 本窗 DIP"这一趟，
        // 与上面 hole 的正反变换同一个原点；多屏不同 DPI 时只有这么换才对得上。
        // 钳到短边一半与截图层 EffectRadiusDip 同一条：半径过半就不再是"圆角"而是"胶囊"，画出来必崩。
        double radiusDip = Math.Min(_previewRadiusDip * cardScale / _scale,
                                    Math.Min(hole.Width, hole.Height) / 2);
        SelectionMask.Data = SelectionVisuals.BuildRectMaskGeometry(hole, Width, Height, radiusDip);
        SelectionMask.IsVisible = true;
    }

    /// <summary>卡的<b>尺寸</b>变了（重钉窗宽、画面本身换尺寸都会让它自己的 Bounds 跳）→ 洞跟着重算。
    /// <para>🔴 挪位**不在这条通路上**，必须走 <see cref="OnPreviewCardMoved"/>：实测
    /// <c>Window.Position</c> 根本不是 Avalonia 属性（Window/WindowBase/TopLevel 上没有任何带 Position 的
    /// 公开静态字段，写一次 <c>PropertyChanged</c> 一条都不发——这也是 <c>Window.PositionProperty</c>
    /// 编译期报"未包含定义"的同一件事），订它的变更等于订空气，洞会原地不动。</para></summary>
    private void OnPreviewGeometryChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Bounds)) BuildPreviewMask();
    }

    /// <summary>卡的<b>位置</b>变了（含 <c>BeginMoveDrag</c> 那种系统侧拖拽）→ 洞跟着重算。
    /// <see cref="WindowBase.PositionChanged"/> 是实测唯一会响的那条。</summary>
    private void OnPreviewCardMoved(object? sender, EventArgs e) => BuildPreviewMask();

    /// <summary>
    /// 录制中那一整套浮层收起，本窗在预览期只剩"卡外压暗"这一层（2026-09-22 第三批：暗纱从录制中搬到预览态，
    /// 见 <see cref="Setup"/> 里那条说明）。
    /// <para>为什么各件都要显式收：它们各自都有"下一次状态变化又把自己 Show 回来"的通路
    /// （徽标读数、toast、浮条各归各的事件），只藏其中几枚＝预览期某一刻又冒出来压在视频上。</para>
    /// <para>⚠ 收的是<b>可见性</b>，不是 <c>Close()</c>：这扇窗此刻是唯一还盖着桌面的自家层，
    /// 预览窗揭开与它退场之间不能有缝（那条缝就是"闪一下桌面/白屏"）。</para>
    /// <para><paramref name="plateRadiusDip"/>＝<b>卡那一圈的</b>圆角 <c>VideoStickerWindow.PlateRadiusDip</c>
    /// （卡窗口 DIP，由卡自己现算给出，本窗不再自己读设置）：洞与卡同读<b>同一枚</b>，才不会出现
    /// "卡圆角、洞直角"那四块漏出的桌面，也不会出现"卡一条弧、洞另一条弧"那两圈细缝
    /// （宿主光晕那枚 <c>+1</c> 不能传到这里，见 <see cref="BuildPreviewMask"/> 里那条）。
    /// 由调用方给而不是本窗去读卡的控制属性，是因为半径此刻可能还没被 SetupPreview 覆盖过——卡自己最清楚它用了几。</para>
    /// </summary>
    public void EnterPreviewMask(Window card, Control plate, double plateRadiusDip)
    {
        // 画板还开着先把工具落回 None：连带收两枚浮条、清选中，并把整窗换回全穿透。
        // 预览期这张铺满虚拟屏的窗一像素输入都不该吃——用户要点的是卡上的播放/保存。
        ExitAnnotationMode();
        ClearMouseEffects();
        ClearKeyEffects();
        _blinkTimer?.Stop();
        // 录制中控制条归"录制"这一态，进预览即收。以前它跟着 CloseOverlay 一起消失，现在本窗要活到预览结束，
        // 不收就是条子一直挂在卡下面（它带的是暂停/停止/标注那套，预览期按下去没有对象）。
        CloseControlBar();

        _previewCard = card;
        _previewPlate = plate;
        _previewRadiusDip = plateRadiusDip;

        SelectionBorder.IsVisible = false;   // 那道红/紫框是"正在录这一块"的语义，录完还挂着＝给卡片又套一道边
        CountdownPill.IsVisible = false;
        BadgePanel.IsVisible = false;
        DegradeToast.IsVisible = false;
        SpotlightRadiusPillHost.IsVisible = false;
        CounterPillHost.IsVisible = false;
        AnnotationLayer.IsVisible = false;   // 笔迹已经录进母带，预览期再摆一份＝在画面上叠两层
        TextHost.IsVisible = false;
        EffectsLayer.IsVisible = false;

        // 跟卡走：位置发在卡的 PositionChanged 上（挪窗），尺寸发在卡与画面各自的 Bounds 上（重钉窗宽、
        // 换画面尺寸）——三条都订，缺一角就有一角不跟。
        card.PositionChanged += OnPreviewCardMoved;
        card.PropertyChanged += OnPreviewGeometryChanged;
        plate.PropertyChanged += OnPreviewGeometryChanged;
        BuildPreviewMask();

        // 🔴 预览态的 Z 序：暗纱必须恒在卡<b>之下</b>（2026-09-29 用户实报"预览下面的工具栏是灰色的，像被遮罩盖住"）。
        // 洞只挖在画面卡上，卡下方那条工具条本就落在暗纱范围里——两扇窗同在置顶带，谁在上全凭最后一次抢位。
        // 录制期挂的持久置顶守卫（1 秒兜底重断言 + 改写插入位为 HWND_TOPMOST）会把本窗一次次拽回带顶，
        // 于是暗纱时不时压到卡上、工具条发灰。进预览即：① 摘守卫；② 把本窗插到卡正下方；
        // ③ 卡自己挂着守卫（VideoStickerWindow.OnOpened），第三方置顶窗照样顶不掉卡，暗纱紧贴其下即可。
        WindowHelper.StopEnforceTopmost(_hwnd);
        _previewCardHwnd = card.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        PlaceBelowPreviewCard();
        // 卡的 Z 序会因用户点它、另存为对话框收尾恢复置顶等再动；本窗跟着再压一次（便宜：只在不在卡下方时才调）
        card.Activated += OnPreviewCardActivated;
        _previewZTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => PlaceBelowPreviewCard());
        _previewZTimer.Start();
    }

    /// <summary>
    /// 预览态暗纱的<b>可见性开关</b>（2026-09-29 转码期最小化）：卡收进任务栏时把整屏暗纱一起收掉，
    /// 唤回时按<b>当前</b>几何重算再放回来。
    /// <para>刻意只动"可见性＋那台 Z 序定时器＋那道闸"三样：<b>不关本窗</b>——<see cref="EnterPreviewMask"/> 的注释写明了
    /// "预览窗揭开与它退场之间不能有缝"，关掉就是那道缝；<b>不摘四根跟随钩子</b>——卡没关，钩子留着才对，
    /// 唤回后卡一挪位暗纱照样跟；本窗此刻本就整窗 <c>WS_EX_TRANSPARENT</c> 全穿透，暗纱一藏就等于不存在。</para>
    /// <para>🔴 首版只写了"藏一次可见性"，用户实报<b>「点击最小化，遮罩没有消失」</b>：卡最小化那一刻
    /// Position/Bounds 在跳，钩子随之把暗纱重算并设回可见。⇒ 收起必须**连闸一起落**（见 <see cref="_previewMaskGated"/>），
    /// 光藏一下等于没藏。</para>
    /// </summary>
    public void SetPreviewMaskVisible(bool visible)
    {
        if (_previewCard == null)
        {
            Core.AppLogger.Warn("RecordOverlayWindow.SetPreviewMaskVisible: 未进预览态（_previewCard=null），暗纱不动，visible=" + visible);
            return;
        }
        _previewMaskGated = !visible;   // 先落闸，再去改可见性：中间那句 WindowState 变更会同步发钩子
        if (visible)
        {
            // 重算而不是"把原来那张几何亮回来"：最小化期间卡可能被系统恢复到了别的位子上
            BuildPreviewMask();
            _previewZTimer?.Start();
        }
        else
        {
            _previewZTimer?.Stop();   // 别对着一扇最小化的卡每 500ms 调一次 SetWindowPos
            SelectionMask.IsVisible = false;
        }
        Core.AppLogger.Info("RecordOverlayWindow: 预览暗纱已" + (visible ? "放回" : "收起")
            + "（闸=" + _previewMaskGated + "）");
    }

    /// <summary>暗纱的"整层收起"闸门（<see cref="SetPreviewMaskVisible"/> 拨动）：为 true 时
    /// <see cref="BuildPreviewMask"/> 直接早退。<b>不靠"藏一次可见性"是因为藏不住</b>——卡的三根跟随钩子
    /// 在收起期照常发（最小化那一刻位置与尺寸都在跳），每一发都会把 <c>SelectionMask.IsVisible</c> 设回 true。</summary>
    private bool _previewMaskGated;

    /// <summary>预览卡的句柄（Z 序锚点）。</summary>
    private IntPtr _previewCardHwnd;
    private DispatcherTimer? _previewZTimer;

    private void OnPreviewCardActivated(object? sender, EventArgs e) => PlaceBelowPreviewCard();

    /// <summary>
    /// 把暗纱窗插到预览卡正下方（同在置顶带内）。已经在卡下方就不动——每次无差别 SetWindowPos 都会发一轮
    /// WM_WINDOWPOSCHANGED，打断卡上的 Tooltip 等弹层（与置顶守卫里那条幂等检查同一理由）。
    /// </summary>
    private void PlaceBelowPreviewCard()
    {
        if (_closed || _hwnd == IntPtr.Zero || _previewCardHwnd == IntPtr.Zero) return;
        if (IsBelow(_hwnd, _previewCardHwnd)) return;
        NativeMethods.SetWindowPos(_hwnd, _previewCardHwnd, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>沿 Z 序从 <paramref name="upper"/> 往下找，能走到 <paramref name="lower"/> ＝它在下面。</summary>
    private static bool IsBelow(IntPtr lower, IntPtr upper)
    {
        IntPtr h = NativeMethods.GetWindow(upper, NativeMethods.GW_HWNDNEXT);
        for (int guard = 0; h != IntPtr.Zero && guard < 4096; guard++)
        {
            if (h == lower) return true;
            h = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDNEXT);
        }
        return false;
    }

    private void CenterOnSelection(Control c)
    {
        Avalonia.Size size = LayoutMetrics.MeasureNow(c);
        Rect sel = SelDip();
        Canvas.SetLeft(c, Math.Max(2, sel.X + (sel.Width - size.Width) / 2));
        Canvas.SetTop(c, Math.Max(2, sel.Y + (sel.Height - size.Height) / 2));
    }

    private void PositionBadge()
    {
        if (!BadgePanel.IsVisible) return;
        Avalonia.Size size = LayoutMetrics.MeasureNow(BadgePanel);
        Rect sel = SelDip();
        double top = sel.Y - size.Height - 8;
        double left = sel.Right - size.Width + 1;
        if (top < 2)
        {
            // 选区贴屏幕顶：改挂到选区<b>下</b>沿外 8 DIP——翻进选区内部等于把这枚徽标永久录进画面
            // （徽标只有上下两格可退，横向压过选区不算，因为它在 crop 之外）
            top = sel.Bottom + 8;
            left = sel.X + 8;
            if (top + size.Height > Root.Bounds.Height - 2)
            {
                // 上下都无容身之地＝选区铺满整屏，只能进画面：如实留此注释，不再静默
                top = sel.Y + 8;
            }
        }
        Canvas.SetLeft(BadgePanel, Math.Max(2, left));
        Canvas.SetTop(BadgePanel, top);
    }

    private Rect SelDip() => new(
        (_regionPhys.X - _virtPhys.X) / _scale,
        (_regionPhys.Y - _virtPhys.Y) / _scale,
        _regionPhys.Width / _scale,
        _regionPhys.Height / _scale);

    internal static string FormatElapsed(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";

    private void CloseControlBar()
    {
        if (_controlBarWindow != null)
        {
            try { _controlBarWindow.Close(); } catch { }
            _controlBarWindow = null;
        }
    }

    /// <summary>安全关闭：停表 + 关闭控制条 + 幂等。</summary>
    public void CloseOverlay()
    {
        if (_closed) return;
        ClearMouseEffects();
        ClearKeyEffects();
        _blinkTimer?.Stop();
        _countdownTimer?.Stop();
        CloseControlBar();
        try { Close(); } catch { }
    }
}
