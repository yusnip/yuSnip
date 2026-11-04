using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 圆角浮条（2026-09-18）：选中**「矩形 + 圆角」这一族**的图元后浮在元素上方，按住数值左右拖动调圆角。
///
/// <para>这一族目前有两个成员：**矩形档聚光灯**与**矩形图元**（矩形工具画出来的那个）。两者在 Core 里
/// 本就同族——`SpotlightShape` 与 `RectangleShape` 的圆角都只是"矩形 + 一个半径"，
/// 判定与渲染都只看矩形本身、不问它属于哪类图元。椭圆档聚光灯与椭圆档矩形图元都没有圆角概念，
/// 宿主据此不显示浮条。</para>
///
/// <para>⚠ 本控件是这一族圆角的**唯一入口**（2026-09-18）：四角圆角手柄先随聚光灯、同日再随矩形整体退役，
/// 故 Core 里已不再有圆角手柄的命中与算式，圆角一律由这里"直接给值"。</para>
///
/// <para>⚠ 类名与宿主里的 <c>SpotlightRadiusPillHost</c> / <c>RefreshSpotlightRadiusPill</c> 等标识符
/// 都还带着 "Spotlight"，是**历史遗留**（它最早只为聚光灯而建，矩形是当天晚些时候补进来的）。
/// 语义早已是"圆角浮条"，读代码时按语义理解即可，别照名字推断它只服务聚光灯。</para>
///
/// <para>交互与选区参数条的「圆角 / 描边」两格**完全同源**——同一个 <see cref="PillValueFineController"/>，
/// 同一套「按下即浮起 → 就地展开 5 档刻度 → 松手 300ms 收回」，滚轮同样是 ±1 档。
/// 两处刻意不同的一点是行程：本控件量程 0–200（物理/内容像素），是选区圆角 0–50 的 4 倍，
/// 照 40px/档走全程要 8000px，故按 <see cref="SlotStepScale"/> 把行程压到与选区圆角同一段。</para>
///
/// <para>⚠ 本控件**只负责"圆角是多少"**，不碰图元：宿主（<c>CaptureWindow</c> / <c>StickerWindow</c>）
/// 订阅 <see cref="RadiusChanged"/>，负责写文档、进撤销、回写圆角记忆。定位与显隐也全在宿主
/// （两边的坐标系不同：截图＝屏幕物理像素，贴图＝内容像素 × 缩放倍率），故这里不做任何定位。</para>
/// </summary>
public partial class SpotlightRadiusPill : UserControl
{
    /// <summary>圆角量程（图元坐标系单位），与 <c>CaptureWindow.CornerRadiusMax</c> 同值。</summary>
    internal const int RadiusMin = 0;
    internal const int RadiusMax = 200;

    /// <summary>
    /// 拖动行程倍率：一档仍是 1 个单位，但走满一档只需 10px（常态）/ 3.75px（加速档）。
    /// 见 <see cref="PillValueFineController.Slot.StepScale"/> 的说明。
    /// </summary>
    private const double SlotStepScale = 0.25;

    private readonly PillValueFineController _fine;
    private readonly PillValueFineController.Slot _radiusSlot;
    private double _radius;

    /// <summary>用户改动了圆角（拖动或滚轮）。值＝圆角，单位同宿主图元坐标系。</summary>
    internal event Action<double>? RadiusChanged;

    /// <summary>XAML 可达性要求 public 无参构造（AVLN3000），故这里不能用 internal。</summary>
    public SpotlightRadiusPill()
    {
        InitializeComponent();

        // dimRoot 传浮条自己的内容栈：微调态下「圆角 24」这一组整体让位给浮起的 24，
        // 与参数条那一处的让位语义一致（区别只是本控件只有一组，没有"其余组"要压暗）。
        //
        // 末位 plate 传**黑条本体**（PillHost）：浮条只有约 75px 宽，而整排刻度要 136px（2 位数）
        // 到 160px（3 位数），刻度必然越出黑条；而刻度是白系字，落到条外就只能看背景脸色——
        // 白底截图/白卡片贴图上就是"白字压白底"，一个字都看不见。传进去之后控制器会在浮起时
        // 把黑条撑到刚好装下整排刻度（参数条不传，因为它本身就够宽、从不越出）。
        _fine = new PillValueFineController(PillContent, FineLayer, FineM2, FineM1, FineCur, FineP1, FineP2, PillHost);
        _radiusSlot = _fine.Attach(GroupRadius, RadiusValue, RadiusMin, RadiusMax,
            () => (int)Math.Round(_radius), ApplyFromFine, SlotStepScale);

        GroupRadius.PointerWheelChanged += OnRadiusWheel;
    }

    /// <summary>当前圆角（图元坐标系单位）。</summary>
    internal double Radius => _radius;

    /// <summary>
    /// 是否正处在"按住数值微调"中（按下已成立、尚未松手）。宿主据此把**重活**推到松手之后——
    /// 贴图侧一次落定要克隆整张干净底图并重渲染全部图元，压在手指移动的每一帧上就是"越调越卡、画面直闪"。
    /// </summary>
    internal bool IsFineActive => _fine.IsFineActive;

    /// <summary>
    /// **可见黑条**在控件内的矩形（相对控件左上角）。
    ///
    /// <para>控件的可绘制区比黑条宽——多出来的是给浮起层刻度留的余量（见 axaml 里
    /// `RootPanel` 宽度的说明）。所以宿主做**定位居中**与**命中判定**时一律要用这个矩形、
    /// 不能用控件矩形：照控件矩形判，黑条左右各约 70px 的透明带会吃掉点击；
    /// 而"黑条居中在可绘制区里"这一条保证了**控件居中 ⇔ 黑条居中**，宿主原有的居中算式不用改。</para>
    ///
    /// <para>⚠ 微调态下这个矩形会**变宽**（控制器把黑条撑开以装下整排刻度），而宿主定位用的是
    /// **控件**尺寸（<c>RootPanel</c> 恒宽 260），与它无关——故黑条撑开不会牵动宿主定位。</para>
    ///
    /// <para>用 <see cref="Control.DesiredSize"/> 与 <c>RootPanel.Width</c> 现算，**不读
    /// <see cref="Visual.Bounds"/>**：宿主量宽走的是 <c>LayoutMetrics.MeasureNow</c>（只测不排），
    /// 那时 <c>Bounds</c> 还停在上一帧的值，首次显示时更是 0×0。</para>
    /// </summary>
    internal Rect PillBounds
    {
        get
        {
            double w = PillHost.DesiredSize.Width;
            double h = PillHost.DesiredSize.Height;
            double cw = double.IsNaN(RootPanel.Width) ? w : RootPanel.Width;
            double ch = double.IsNaN(RootPanel.Height) ? h : RootPanel.Height;
            return new Rect((cw - w) / 2, (ch - h) / 2, w, h);
        }
    }

    /// <summary>
    /// 回显圆角（宿主选中图元、或圆角被别处改动时调用）：只写值、**不触发 <see cref="RadiusChanged"/>**，
    /// 否则会形成"回显 → 上报 → 回写图元 → 再回显"的自激环。
    /// </summary>
    internal void SetRadius(double radius)
    {
        double clamped = Math.Clamp(radius, RadiusMin, RadiusMax);
        // 幂等早退：值没变就一根手指都别碰可视树。宿主每次 Document.Changed 都会回灌一次，
        // 而无谓地写 RadiusValue.Text 会连带触发一次布局（条宽可能变），高频回灌时就是肉眼可见的抖动。
        if (Math.Abs(clamped - _radius) < 0.01) return;
        _radius = clamped;
        RadiusValue.Text = ((int)Math.Round(_radius)).ToString();
        AppLogger.Debug($"[SpotPill] 回显 SetRadius {_radius:0.##}");
    }

    /// <summary>立刻收回浮起层（浮条隐藏、选区变化、窗口失活等）。不走过渡——这些场合残影比动效更糟。</summary>
    internal void Collapse() => _fine.Collapse();

    /// <summary>拖动/滚轮落值：夹量程 → 刷数值 → 上报宿主。</summary>
    private void ApplyFromFine(int value)
    {
        double next = Math.Clamp(value, RadiusMin, RadiusMax);
        if (Math.Abs(next - _radius) < 0.5) return;
        _radius = next;
        RadiusValue.Text = ((int)Math.Round(_radius)).ToString();
        RadiusChanged?.Invoke(_radius);
    }

    private void OnRadiusWheel(object? sender, PointerWheelEventArgs e)
    {
        int delta = e.Delta.Y > 0 ? 1 : e.Delta.Y < 0 ? -1 : 0;
        if (delta == 0) return;
        _fine.Bump(_radiusSlot, delta);
        // 吞掉滚轮：否则会继续冒泡到窗口级的「滚轮缩放选区」。
        e.Handled = true;
    }
}
