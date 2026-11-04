using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 截图选区参数条上「按住数值 → 就地浮起 → 左右拖动微调」的交互控制器。
///
/// 交互来源：另一个项目（`D:\OneDrive\开发\图片拼接`）预览区的「缩放微调」——按住百分比数字后
/// 数字就地放大、左右拖动步进。本类是那套交互在 yuSnip 的落地，参数按本项目的量程重新定过。
/// **触发口径是"按下即浮起"**（2026-09-17 用户拍板去掉 120ms 长按门槛，连带去掉 6px 死区，见下 ④）。
///
/// 与参考实现的四处**刻意不同**（都是踩过或想清楚的，不要"对齐回去"）：
/// ① 参考用 WPF 的 `BeginAnimation`；Avalonia 没有这个 API，这里用 **`Transitions`**（`DoubleTransition`）。
///    **不要改用 `Animation.RunAsync`**：`Animation` 按 Setter 里的属性挑 animator，
///    `ScaleTransform.ScaleXProperty` 挑中的是 `TransformAnimator`，而它的 `Apply` 把 control 强转成 `Visual`
///    —— 对 `ScaleTransform` 实例调 `RunAsync` 必抛 `InvalidCastException`。配上 `_ = RunAsync(...)`
///    这种 fire-and-forget 写法，异常会被吞成 `UnobservedTaskException`（只在 `TaskScheduler.UnobservedTaskException`
///    里露面，不细看日志根本发现不了），现象是"缩放动画从未生效、数字永远停在起始缩放"。
///    2026-09-17 实机踩过这一次，排查代价很高，不要再回去。
///    改 `Transitions` 后还顺带解决了两件事：整条路径没有 async / 没有异常分支；
///    且收回时会把值**显式写回** `PopFrom`，于是每次浮起的值都必然变化、过渡必然播放
///    （原本担心的"目标值相同 ⇒ 过渡不播"在显式写终值的前提下并不成立）。
/// ② 参考把越界档位夹到端点重复显示（值在 0 时会看到 `0 0 0 0 1 2`），本实现**越界档位直接不显示**。
/// ③ 参考的拖动坐标取元素自身坐标系（元素一动基准就漂）；本实现取浮起层所在 Canvas 的坐标系，稳定。
/// ④ **没有"按住不动"的门槛，也没有"移开即撤销"**（2026-09-17 用户拍板：按下就有效果）。
///    原实现是"长按 120ms 成立 + 6px 死区撤销"，这两个是一对：门槛负责防误触，死区负责在门槛尚未
///    成立时撤退。**去掉门槛后死区必须一起去掉**——40px 的步长本身就吸收手抖，而 6px 的撤销阈值比
///    第一档步长小得多，留着会让"刚浮起就因正常拖动而收回"，功能直接不可用。防误触改由另外两层承担：
///    ① 参数条在遮罩的命中白名单里，落在条上的按下不会起新框；② 组内嵌的取色块按钮有 IsFromInnerButton 守卫。
/// ⑤ **可选"底片"**（2026-09-18 第二轮）：宿主可以传一个"刻度该压在上面的那个不透明底"（浮条的黑条）。
///    传了之后有两处变化，且是同一件事的两面——**锚点改取底片中心**、**底片在浮起时横向撑开**
///    到刚好装下整排刻度（见 `Layout()` 里那两段）。不传（选区参数条）则行为与 2026-09-17 一字不差。
///    为什么需要它：刻度是白系字（`#99FFFFFF` / `#5CFFFFFF`），压在底片上永远可读，落到片外就只能看背景
///    脸色——白底截图或白卡片贴图上就是"白字压白底"，一个字都看不见（用户截图实测）。参数条没暴露过，
///    只因为它本身够宽（320）、刻度从不越出；**不是这套交互本身没问题**，别再照它"看起来没事"去改。
///
/// 拖动步进的关键是**基准化**：每走满一档就把基准前移 `steps × 步长`，余量带到下一档。
/// 不重基准的话每帧都拿原始起点算、余量被反复丢弃，手感发涩且丢步。
/// </summary>
internal sealed class PillValueFineController
{
    // ---- 运动与手感参数（参考实现是"长按 100ms / 40px 一档"；这里改成"按下即浮起"，并加加速档）----
    private const double StepNormalPx = 40;      // 常态步长：每 40px 一档（微调用）
    private const double StepFastPx = 15;        // 加速档：每 15px 一档（圆角 0–50 全程由 2000px 压到 750px）
    private const double AccelAfterMs = 700;     // 进入微调多久后切加速档
    private const double CollapseDelayMs = 300;  // 松手后多久收回（参考 100ms 太快看不清落定值；450ms 用户实测嫌慢 ⇒ 取中）
    private const double StepGapPx = 8;          // 相邻档位与当前值的水平间距
    private const double PlatePadX = 8;          // 浮起时"底片"（黑条）在整排刻度两侧各留的余量
    private const int PlateGrowMs = 140;         // 底片长成刻度尺的时长（与 PopMs 同步：条变宽与数字放大是一件事）
    private const double PopFrom = 0.6;          // 浮起数字的起始缩放
    private const int PopMs = 140;
    private const int FadeMs = 150;
    // 收尾定时器在淡出时长之外多留的余量。收回**不依赖**过渡的完成回调（Transitions 没有完成通知），
    // 而是靠这个定时器无条件把层藏掉：浮起层永久挂在屏幕上，是这套交互最糟的失败形态。
    private const int SettleGraceMs = 70;

    // 按下即浮起 ⇒ 没有"已按下、等门槛成立"的中间态（原 Phase.Armed 随 120ms 门槛一并删除）
    private enum Phase { Idle, Fine, Closing }

    /// <summary>一个可微调的数值槽（圆角 / 描边 / 聚光灯圆角各一个）。</summary>
    internal sealed class Slot
    {
        internal Control Group = null!;
        internal TextBlock Value = null!;
        internal int Min;
        internal int Max;
        internal Func<int> Get = null!;
        internal Action<int> Set = null!;

        /// <summary>
        /// 拖动行程的倍率（默认 1）：一档仍是 <b>1 个单位</b>，但走满一档所需的像素＝
        /// <c>StepNormalPx / StepFastPx</c> 乘上本值。存在的理由是两个入口的量程差 4 倍——
        /// 选区圆角 0–50、聚光灯圆角 0–200；照 40px/档走，后者全程要 8000px（加速档 3000px），
        /// 手得拖出屏幕去。取 0.25 后聚光灯圆角全程 2000px / 750px，与选区圆角**同一段行程**，
        /// 而精度仍是 1 个单位（不像"每档 4 单位"那样把最小步长也放大）。
        /// </summary>
        internal double StepScale = 1.0;
    }

    private readonly Control _dimRoot;
    private readonly Canvas _layer;
    private readonly TextBlock _m2, _m1, _cur, _p1, _p2;

    /// <summary>
    /// 可选"底片"：浮起时会被**横向撑开**以装下整排刻度的那个不透明底（浮条的黑条）。
    /// 为 null（选区参数条）时行为与 2026-09-17 完全一致——不撑、也不改锚点。
    /// 见 <see cref="Layout"/> 里"底片"那一段：它同时是"刻度压在什么底上"的保证。
    /// </summary>
    private readonly Control? _plate;
    private readonly Transitions? _plateGrow, _plateShrink;
    private double _plateNaturalWidth;

    private readonly ScaleTransform _curScale = new(PopFrom, PopFrom);
    private readonly Transitions _popScale, _shrinkScale, _fade;
    private readonly DispatcherTimer _hideTimer, _accelTimer, _settleTimer;
    private readonly List<Slot> _slots = new();

    private Phase _phase = Phase.Idle;
    private Slot? _active;
    private IPointer? _pointer;
    private double _baseX;   // 步进基准：每走满一档就前移，余量带到下一档
    private double _lastX;
    private bool _fast;

    internal PillValueFineController(
        Control dimRoot, Canvas layer,
        TextBlock m2, TextBlock m1, TextBlock cur, TextBlock p1, TextBlock p2,
        Control? plate = null)
    {
        _dimRoot = dimRoot;
        _layer = layer;
        _m2 = m2; _m1 = m1; _cur = cur; _p1 = p1; _p2 = p2;
        _plate = plate;
        if (plate != null)
        {
            _plateGrow = BuildPlateTransitions(new CubicEaseOut());
            _plateShrink = BuildPlateTransitions(new CubicEaseIn());
        }

        // 缩放必须绕自身中心。代码侧用 RelativePoint 显式指定——写成 XAML 字符串 "0.5,0.5" 会被
        // 解析成**绝对像素**（= 控件左上角），绕左上角缩放会让数字整体飞出原位且不报任何错（AGENTS.md 红线）。
        _cur.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _cur.RenderTransform = _curScale;

        _popScale = BuildScaleTransitions(new CubicEaseOut());
        _shrinkScale = BuildScaleTransitions(new CubicEaseIn());
        _fade = BuildFadeTransitions();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseDelayMs) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); BeginCollapse(); };

        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FadeMs + SettleGraceMs) };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); FinishCollapse("settle-timer"); };

        _accelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AccelAfterMs) };
        _accelTimer.Tick += (_, _) =>
        {
            _accelTimer.Stop();
            if (_phase != Phase.Fine) return;
            // 切加速档必须重基准化：不重的话下一帧会拿旧基准按新步长算出一个大跳
            _fast = true;
            _baseX = _lastX;
            Refresh();
        };
    }

    /// <summary>把一个数值槽接上交互。返回的句柄供滚轮调用 <see cref="Bump"/>。</summary>
    internal Slot Attach(Control group, TextBlock value, int min, int max, Func<int> get, Action<int> set,
        double stepScale = 1.0)
    {
        var slot = new Slot { Group = group, Value = value, Min = min, Max = max, Get = get, Set = set, StepScale = stepScale };
        _slots.Add(slot);
        group.PointerPressed += (_, e) => OnPressed(slot, e);
        group.PointerMoved += (_, e) => OnMoved(slot, e);
        group.PointerReleased += (_, e) => OnReleased(slot, e);
        group.PointerCaptureLost += (_, _) => OnCaptureLost(slot);
        return slot;
    }

    /// <summary>滚轮微调：±1 档。微调态正开着时同步刷新浮起数字。</summary>
    internal void Bump(Slot slot, int delta)
    {
        int old = slot.Get();
        int v = Math.Clamp(old + delta, slot.Min, slot.Max);
        if (v == old) return;
        slot.Set(v);
        // Closing（收回动画正在淡出）也照刷，不去取消收回：那 450ms 里缩放/透明动画已经在飞，
        // 强行掉头就得跟它们抢同一个属性（谁后落定取决于两条动画的剩余时长，会抖），
        // 而刷新只是把文字改掉，没有任何代价，数字淡出的同时显示新值反而更连贯。
        if (ReferenceEquals(slot, _active) && _phase is Phase.Fine or Phase.Closing) Refresh();
    }

    /// <summary>
    /// 是否正处在"按住数值微调"中（按下已成立、尚未松手）。
    ///
    /// <para>给宿主用来把**重活**推到松手之后：贴图侧一次落定要克隆整张干净底图并重渲染全部图元
    /// （见 <c>StickerAnnotationSession.Commit</c>），压在手指移动的每一帧上就是"越调越卡、画面直闪"。</para>
    /// </summary>
    internal bool IsFineActive => _phase == Phase.Fine;

    /// <summary>立即收回（参数条隐藏、选区变化、窗口失活等）。不走过渡——这些场合下残影比动效更糟。</summary>
    internal void Collapse()
    {
        if (_phase == Phase.Idle) return;
        _accelTimer.Stop();
        _hideTimer.Stop();
        // 先落相位再放捕获：Capture(null) 会**同步**触发 PointerCaptureLost，
        // 若此刻 _phase 还是 Fine，OnCaptureLost 会再调一次 Collapse 形成重入（幂等但白跑一遍）。
        _phase = Phase.Idle;
        _pointer?.Capture(null);
        _pointer = null;
        ResetVisual("collapse");
        _active = null;
    }

    // ----- 指针路径 -----

    private void OnPressed(Slot slot, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(slot.Group).Properties.IsLeftButtonPressed) return;
        if (_phase == Phase.Fine) return;
        // 组里可能嵌着按钮（描边组左端那枚取色块）：按在按钮上的那次按下不算调数值。
        // 注：Avalonia 的 Button 自己会 e.Handled=true 吞掉 PointerPressed，而本订阅是普通 `+=`（不收已处理事件），
        // 所以今天这条守卫多半不会命中——留着是为了把"按在按钮上不归调值"这个意图写在代码里，
        // 并且内联控件将来换成不吃 Handled 的类型时能兜住。
        if (IsFromInnerButton(e.Source, slot.Group)) return;

        // 收回还没走完就再按下：把半收的浮起层立刻收干净，再当作一次全新的起手。
        // 刻意**不**"续接微调态"：收回过渡已经在飞（缩放正往 0.6 走），续接就得跟它抢同一个属性，
        // 谁后落定要看两条过渡的剩余时长，会抖。重按一次重走一遍浮起，最省事也最可预期。
        if (_phase == Phase.Closing) Collapse();

        Point p = e.GetPosition(_layer);
        _baseX = p.X;
        _lastX = p.X;
        _active = slot;
        _pointer = e.Pointer;
        e.Pointer.Capture(slot.Group);

        // 按下即浮起：没有等待门槛，故直接进微调态（EnterFine 内部会重置 _fast / _baseX）。
        EnterFine();
    }

    /// <summary>事件源头到组之间是否有按钮。有则这次按下属于那个按钮，不归调数值。</summary>
    private static bool IsFromInnerButton(object? source, Control group)
    {
        Visual? node = source as Visual;
        while (node != null && !ReferenceEquals(node, group))
        {
            if (node is Button) return true;
            node = node.GetVisualParent();
        }
        return false;
    }

    private void OnMoved(Slot slot, PointerEventArgs e)
    {
        if (!ReferenceEquals(slot, _active)) return;
        if (_phase != Phase.Fine) return;

        double x = e.GetPosition(_layer).X;
        _lastX = x;

        // 一档恒为 1 个单位，行程按 Slot.StepScale 缩放（见 Slot.StepScale：两个入口的量程差 4 倍）
        double step = (_fast ? StepFastPx : StepNormalPx) * slot.StepScale;
        if (step <= 0) step = StepNormalPx;
        int steps = (int)Math.Truncate((x - _baseX) / step);
        if (steps == 0) return;
        _baseX += steps * step;
        _active!.Set(Math.Clamp(_active.Get() + steps, _active.Min, _active.Max));
        Refresh();
    }

    private void OnReleased(Slot slot, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(slot, _active)) return;
        if (_phase != Phase.Fine) return;

        // 松手不立刻收回：留 CollapseDelayMs 让用户看清落定值，期间滚轮仍可继续微调（Bump 认 Closing）。
        // 真正的收尾由 _settleTimer 无条件兜底，不依赖过渡回调。
        _phase = Phase.Closing;
        _pointer?.Capture(null);
        _pointer = null;
        _hideTimer.Stop();
        _hideTimer.Start();
        AppLogger.Debug($"[FinePill] Released → Closing，{CollapseDelayMs:0}ms 后收回 slot={SlotName(slot)}");
    }

    private void OnCaptureLost(Slot slot)
    {
        if (!ReferenceEquals(slot, _active)) return;
        // 捕获被外力拿走（窗口失活、别处抢捕获）：直接收回，否则数字会一直挂在屏上。
        // Closing 期间不管——那正是松手后主动放掉捕获的正常流程。
        if (_phase == Phase.Fine) Collapse();
    }

    /// <summary>日志用短名（取 x:Name，如 TxtCaptureRadius）。</summary>
    private static string SlotName(Slot slot) => slot.Value.Name ?? "?";

    // ----- 浮起 / 收回 -----

    private void EnterFine()
    {
        if (_active == null) return;
        _phase = Phase.Fine;
        _fast = false;
        _baseX = _lastX;

        _active.Group.Classes.Add("active");
        _dimRoot.Classes.Add("fineDim");

        _hideTimer.Stop();
        // 上一轮的收尾定时器还没跑到就再次浮起：必须撤掉它，否则它会在 210ms 后把**新**的浮起层藏掉
        _settleTimer.Stop();

        // 底片起手：先量出"自然宽"，再落成一个**具体数**，最后挂过渡等 Layout 给出目标宽。
        // 三步都不能省：
        //   ① 自然宽只能在此刻量——下面写了显式 Width 之后就再也量不到"内容想要多宽"了（收回时要用它还原）；
        //      且收回路径把 Width 还原成了 NaN（auto），而 DesiredSize 是**上一趟排布**留下的，
        //      所以这里必须走 MeasureNow 强制重量一次，不能读 DesiredSize。
        //   ② 写显式 Width 之前要摘掉过渡：从 NaN 起步的插值没有意义，留着过渡这一下会变成瞬变。
        //   ③ 目标宽由 Layout 给出（它才知道整排刻度有多宽），故这里只负责把"起点"摆好。
        if (_plate != null)
        {
            _plate.Transitions = null;
            _plateNaturalWidth = Math.Max(LayoutMetrics.MeasureNow(_plate).Width, 1);
            _plate.Width = _plateNaturalWidth;
            _plate.Transitions = _plateGrow;
        }

        // 必须先置可见再量宽定位：不可见元素显式 Measure 得 0×0，TranslatePoint 也不可靠
        // （同 LayoutMetrics 类型注释里的坑 A）
        _layer.IsVisible = true;
        Refresh();

        // 挂过渡再赋值 ⇒ 过渡由"值变化"驱动，播放是必然的（收缩态值由 BeginCollapse / ResetVisual 显式写回）。
        // 这里若改用 Animation.RunAsync 会直接抛 InvalidCastException，见类注释 ①。
        _curScale.Transitions = _popScale;
        _layer.Transitions = _fade;
        _curScale.ScaleX = 1.0;
        _curScale.ScaleY = 1.0;
        _layer.Opacity = 1;

        _accelTimer.Stop();
        _accelTimer.Start();
        AppLogger.Debug($"[FinePill] EnterFine slot={SlotName(_active)} 值={_active.Get()} → scale 1.0 / opacity 1");
    }

    private void BeginCollapse()
    {
        if (_active == null) { FinishCollapse("begin-collapse-empty"); return; }
        _phase = Phase.Closing;

        _curScale.Transitions = _shrinkScale;
        _layer.Transitions = _fade;
        _curScale.ScaleX = PopFrom;
        _curScale.ScaleY = PopFrom;
        _layer.Opacity = 0;

        // 底片同步收回自然宽。**必须与刻度淡出同时开始**：条先缩、数字还在，会看到数字悬在条外的空处；
        // 条后缩、数字已没，会看到一条空荡荡的长条自己缩回去。两者同起同落才是一件事。
        // ⚠ 这里是"收回"而非"还原成 auto"：过渡要有两个具体数才播得动，故先过渡到自然宽，
        //   真正还原成 auto 留到 FinishCollapse（那时刻度已不可见，切 auto 的差值看不出来）。
        if (_plate != null)
        {
            _plate.Transitions = _plateShrink;
            _plate.Width = _plateNaturalWidth;
        }

        // 收尾走无条件兜底定时器，而不是 await 过渡完成——Transitions 没有完成通知，
        // 硬凑一个回调只会把"没收回"的可能性重新引回来。
        _settleTimer.Stop();
        _settleTimer.Start();
        AppLogger.Debug($"[FinePill] BeginCollapse slot={SlotName(_active)} → scale {PopFrom} / opacity 0");
    }

    /// <summary>
    /// 无条件收尾：藏层 + 撤让位类 + 清 <c>_active</c>。幂等，任何路径都可安全重复调用。
    /// 之所以"无条件"，是因为收回**不能**依赖过渡的完成回调（Transitions 无完成通知），
    /// 而"浮起层永久挂在屏幕上"是这套交互最糟的失败形态——宁可多一次冗余调用。
    /// </summary>
    private void FinishCollapse(string reason)
    {
        _settleTimer.Stop();
        _layer.IsVisible = false;
        RestorePlateWidth();
        _dimRoot.Classes.Remove("fineDim");
        if (_active != null) _active.Group.Classes.Remove("active");
        _active = null;
        if (_phase == Phase.Closing) _phase = Phase.Idle;
        AppLogger.Debug($"[FinePill] FinishCollapse({reason})");
    }

    /// <summary>
    /// 把底片还原成"由内容定宽"（<c>Width = NaN</c>）。
    /// <para>⚠ 必须先摘过渡：<c>NaN</c> 不能参与插值，挂着过渡写 NaN 只会让动画器算出一串无效值。
    /// 摘了过渡的这一下是**瞬变**，但此刻刻度已经不可见（调用点都在 <c>_layer.IsVisible = false</c> 之后），
    /// 而条的宽度早已在 <see cref="BeginCollapse"/> 里过渡回自然宽，故差值只在 1–2px（位数变化）之内。</para>
    /// </summary>
    private void RestorePlateWidth()
    {
        if (_plate == null) return;
        _plate.Transitions = null;
        _plate.Width = double.NaN;
    }

    /// <summary>
    /// 立刻收回（不走过渡）：摘过渡 + 写终值。
    /// 摘过渡是必须的——这些场合要的是"立刻消失"，挂着过渡会照播 150ms 淡出，
    /// 在窗口失活 / 参数条隐藏时就是一段看得见的残影。
    /// 把 Opacity / Scale 写回收缩态也不是装饰：它保证**下一次浮起**的赋值必然产生值变化，
    /// 从而必然触发过渡（若值停在 1.0，再赋 1.0 不会播，数字会"瞬现"而没有放大过程）。
    /// </summary>
    private void ResetVisual(string reason)
    {
        _settleTimer.Stop();
        _layer.Transitions = null;
        _curScale.Transitions = null;
        _layer.Opacity = 0;
        _curScale.ScaleX = PopFrom;
        _curScale.ScaleY = PopFrom;
        _layer.IsVisible = false;
        RestorePlateWidth();
        _dimRoot.Classes.Remove("fineDim");
        if (_active != null) _active.Group.Classes.Remove("active");
        // 注：若此刻还有过渡实例在飞（浮起/收回未完就被强制收回），它会以 Animation 优先级把它自己的
        // 终值写到 Opacity / Scale 上，压过上面这两行本地赋值。那只是"值不准"，可见性由
        // IsVisible=false 兜住；且下一轮 EnterFine 会重挂过渡并显式写 1.0，不会留下错值。
        AppLogger.Debug($"[FinePill] ResetVisual({reason}) 立刻收回");
    }

    // ----- 档位与定位 -----

    private void Refresh()
    {
        if (_active == null) return;
        int v = Math.Clamp(_active.Get(), _active.Min, _active.Max);
        SetStep(_m2, v - 2, _active.Min, _active.Max);
        SetStep(_m1, v - 1, _active.Min, _active.Max);
        _cur.Text = v.ToString();
        SetStep(_p1, v + 1, _active.Min, _active.Max);
        SetStep(_p2, v + 2, _active.Min, _active.Max);
        Layout();
    }

    /// <summary>越界档位不显示（而不是夹到端点重复）。</summary>
    private static void SetStep(TextBlock tb, int value, int min, int max)
    {
        bool ok = value >= min && value <= max;
        tb.Text = ok ? value.ToString() : string.Empty;
        tb.IsVisible = ok;
    }

    private void Layout()
    {
        if (_active == null) return;

        // 刚改过 Text ⇒ 必须递归失效后重测，否则读到的还是上一档的宽度（LayoutMetrics 的坑 B）
        Size c = LayoutMetrics.MeasureNow(_cur);
        Size a1 = LayoutMetrics.MeasureNow(_m1);
        Size a2 = LayoutMetrics.MeasureNow(_m2);
        Size b1 = LayoutMetrics.MeasureNow(_p1);
        Size b2 = LayoutMetrics.MeasureNow(_p2);

        // 锚点＝整排刻度的中心，两种宿主取法不同：
        //   ① 有底片（浮条的黑条）时取**黑条中心**。黑条是居中于可绘制区的，而数值中心偏在它右侧
        //      （左边还站着"圆角"两个字与内边距）。照数值中心展开，整排必然整体偏右，黑条要包住它就得在
        //      左侧留一大块空白（实测 36px vs 右侧 8px，明显不匀）；以黑条中心展开则两侧余量相等，
        //      浮起时"黑条就地长成一把刻度尺"是匀称的。
        //      ⚠ 黑条居中 ⇒ 它的中心恒等于可绘制区中心，**写显式 Width 也推不动它**，故这条不会与
        //        "按刻度撑开黑条"自激（那才是这里敢用它的前提）。
        //   ② 无底片（选区参数条）时仍取被按数值的中心，行为与 2026-09-17 一字不差。
        //  `Bounds.Width > 0` 那道守卫只兜"本帧刚显示、还没排布过"的一帧：那时 Bounds 还是 0×0，
        //  TranslatePoint 会拿左上角当中心。退回数值中心那一帧数字还在淡入（不可见），看不出差别。
        Point? center = _plate != null && _plate.Bounds.Width > 0
            ? _plate.TranslatePoint(new Point(_plate.Bounds.Width / 2, _plate.Bounds.Height / 2), _layer)
            : _active.Value.TranslatePoint(
                new Point(_active.Value.Bounds.Width / 2, _active.Value.Bounds.Height / 2), _layer);
        if (center is null) return;

        double cx = center.Value.X;
        double cy = center.Value.Y;

        // 先按"当前值中心对齐被按数值标签的中心"把五档摆开（RenderTransformOrigin 是自身中心 ⇒ 缩放不改变这个对齐），
        // 每档只记左边界，摆完再整体夹一次（见下方 shift）。
        double curLeft = cx - c.Width / 2;
        double? m2Left = null, m1Left = null, p1Left = null, p2Left = null;

        double left = curLeft - StepGapPx;
        if (_m1.IsVisible) { m1Left = left - a1.Width; left = m1Left.Value - StepGapPx; }
        if (_m2.IsVisible) { m2Left = left - a2.Width; }

        double right = curLeft + c.Width + StepGapPx;
        if (_p1.IsVisible) { p1Left = right; right = p1Left.Value + b1.Width + StepGapPx; }
        if (_p2.IsVisible) { p2Left = right; }

        // 整排刻度的左右边界。夹取与"底片该多宽"共用这一对值，故提到夹取之外算一次。
        double minLeft = m2Left ?? m1Left ?? curLeft;
        double maxRight = p2Left is double q2 ? q2 + b2.Width
            : p1Left is double q1 ? q1 + b1.Width
            : curLeft + c.Width;

        // 整体夹进可绘制区：兜住"刻度被容器裁掉"这一层（极端位数 / 大字号 / 高 DPI 下仍可能越界）。
        // ⚠ 这一层**不是**"右侧看不见"的正解——真正的原因是刻度落到黑条外后失去对比度，
        //   见方法末尾"底片撑开"那段。别看到用户还报看不见就回来加宽这里的容器。
        // ⚠ 只在层宽已知时夹，两种"已知不了"的情形都在预期内：
        //   ① 首帧布局尚未跑完时 Bounds 还是 0×0，那一帧不夹也不会错到哪去；
        //   ② 本层挂在 `Canvas` 下时它**恒为 0**——`Canvas` 按子元素的 DesiredSize 排布，而
        //      `Canvas` 自身的 DesiredSize 恒 0×0（选区参数条的 `ValueFineLayer` 就是这种，见 MEMORY 里那条）。
        //      那种宿主下这段夹取等于不生效，故**别把它当"参数条也受保护"的依据**：
        //      参数条那边靠的是条本身够宽（320px）、刻度从不溢出。
        double extent = _layer.Bounds.Width;
        if (extent > 0)
        {
            double shift = 0;
            if (maxRight > extent) shift = extent - maxRight;   // 先把右边收回来（用户报的就是这一侧）
            if (minLeft + shift < 0) shift = -minLeft;          // 再保证左边不出界
            if (shift != 0)
            {
                curLeft += shift;
                if (m1Left is double v1) m1Left = v1 + shift;
                if (m2Left is double v2) m2Left = v2 + shift;
                if (p1Left is double v3) p1Left = v3 + shift;
                if (p2Left is double v4) p2Left = v4 + shift;
                minLeft += shift;
                maxRight += shift;
            }
        }

        Canvas.SetLeft(_cur, curLeft);
        Canvas.SetTop(_cur, cy - c.Height / 2);
        if (m1Left is double x1) { Canvas.SetLeft(_m1, x1); Canvas.SetTop(_m1, cy - a1.Height / 2); }
        if (m2Left is double x2) { Canvas.SetLeft(_m2, x2); Canvas.SetTop(_m2, cy - a2.Height / 2); }
        if (p1Left is double x3) { Canvas.SetLeft(_p1, x3); Canvas.SetTop(_p1, cy - b1.Height / 2); }
        if (p2Left is double x4) { Canvas.SetLeft(_p2, x4); Canvas.SetTop(_p2, cy - b2.Height / 2); }

        // 🔴 底片撑到装得下整排刻度——这是"2 位数以上右侧看不见"的**结构性**正解（2026-09-18 第二轮）。
        // 前面那几轮修法（把可绘制区加宽到 260、把整排夹进可绘制区）解决的是"被裁掉"，
        // 而真正让人看不见的是**对比度**：刻度是白系字（#99FFFFFF / #5CFFFFFF），压在黑条上永远可读，
        // 一旦落到条外就只能看背景脸色——深色截图上看得出，白底截图或白卡片贴图上就是"白字压白底"，
        // 一个字都看不见（用户截图实测：条内 32/33 清晰、条外两段纯白）。
        // 参数条从没暴露过，只因为它本身够宽（320）、刻度从不越出——不是这套交互本身没问题。
        // 宽度以**黑条中心**为基准对称取，两侧余量才相等（锚点为什么取黑条中心见上）。
        // ⚠ 只在 Phase.Fine 里撑：Closing 期间 Refresh 还会被滚轮 Bump 调到，
        //   那时再撑一次会跟 BeginCollapse 的收窄过渡抢同一个属性，条会来回抖。
        if (_plate != null && _phase == Phase.Fine)
        {
            double need = 2 * Math.Max(cx - minLeft, maxRight - cx) + 2 * PlatePadX;
            if (extent > 0) need = Math.Min(need, extent);   // 底片本身也别越过可绘制区
            _plate.Width = Math.Max(_plateNaturalWidth, need);
        }
    }

    // ----- 过渡工厂 -----
    //
    // 为什么是 Transitions 而不是 Animation.RunAsync（详见类注释 ①）：
    // Transition<T> 走 DoubleAnimator，直接 SetValue(prop, v, BindingPriority.Animation)，不做类型强转；
    // 而 Animation 会按属性挑 animator，ScaleTransform.ScaleXProperty 挑中的 TransformAnimator
    // 会把 control 强转成 Visual ⇒ 对 ScaleTransform 实例必抛 InvalidCastException。

    private static Transitions BuildScaleTransitions(Easing easing) => new()
    {
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleXProperty,
            Duration = TimeSpan.FromMilliseconds(PopMs),
            Easing = easing,
        },
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleYProperty,
            Duration = TimeSpan.FromMilliseconds(PopMs),
            Easing = easing,
        },
    };

    private static Transitions BuildFadeTransitions() => new()
    {
        new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(FadeMs),
            Easing = new LinearEasing(),
        },
    };

    /// <summary>
    /// 底片（黑条）横向撑开/收回的过渡。<c>Width</c> 是 <c>StyledProperty</c>，走 <c>DoubleAnimator</c>，
    /// 与上面两条缩放过渡同一机制——**不要**换成 <c>Animation.RunAsync</c>（见类注释 ①）。
    /// 代价是每帧一次布局；本控件只有一层 Border 加几个 TextBlock，可忽略。
    /// </summary>
    private static Transitions BuildPlateTransitions(Easing easing) => new()
    {
        new DoubleTransition
        {
            Property = Layoutable.WidthProperty,
            Duration = TimeSpan.FromMilliseconds(PlateGrowMs),
            Easing = easing,
        },
    };
}
