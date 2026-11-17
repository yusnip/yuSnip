using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 选中序号后浮在它上方的黑条（2026-09-18，设计稿第三条的方案甲）：
/// <c>− 值 + ·〔圆角〕·〔→〕· A/说明文字 · ×</c>，管"这一个序号"的编号、（胶囊外形时）它的圆角、
/// （带引线时）引线画不画、说明文字与删除。
///
/// <para>「圆角」那一格与 <see cref="SpotlightRadiusPill"/> 是**同一套交互**（同一个
/// <see cref="PillValueFineController"/>、同一句"按下即浮起 → 展开 5 档刻度 → 松手 300ms 收回"、
/// 滚轮同样 ±1 档），差别只在量程：这里是 0–100 的**百分比**，且只服务胶囊外形——胶囊两端的高度
/// 恒等于序号大小（没有独立高度可写死像素），故圆角只能按半高的比例收，见
/// <c>AnnotationRenderOptions.GetCounterGlyphCornerRadius</c>。</para>
///
/// <para>⚠ 本控件**只报意图、不改文档**：宿主（<c>CaptureWindow</c> / <c>StickerWindow</c>）订阅六个事件，
/// 负责压撤销快照、写图元、回显。定位与显隐也全在宿主（两侧坐标系不同）。
/// 宿主量**居中**可用控件矩形（黑条居中于可绘制区 ⇒ 控件居中 ⇔ 黑条居中），但量**命中/让位**必须用
/// <see cref="PillBounds"/>：可绘制区比黑条宽（给浮起层刻度留的余量），照控件矩形判会把透明带也算成浮条。</para>
/// </summary>
public partial class CounterPill : UserControl
{
    /// <summary>胶囊圆角量程（百分比）。</summary>
    internal const int CornerMin = 0;
    internal const int CornerMax = 100;

    /// <summary>
    /// 拖动行程倍率：一档仍是 1 个百分点，但走满一档只需 20px（常态）/ 3.75px（加速档），
    /// 0→100 全程 2000px，与选区圆角 0–50、聚光灯圆角 0–200 **同一段行程**。
    /// 见 <see cref="PillValueFineController.Slot.StepScale"/>。
    /// </summary>
    private const double CornerStepScale = 0.5;

    private readonly PillValueFineController _fine;
    private readonly PillValueFineController.Slot _cornerSlot;

    /// <summary>挂在窗口根上的 Tunnel 按下处理器（编辑说明途中点到框外任何地方都先提交）。</summary>
    private EventHandler<PointerPressedEventArgs>? _rootPressHandler;

    /// <summary>
    /// 宿主注入的判据：<b>这一笔是不是落在画布表面上</b>（而非落在工具栏 / 参数条 / 任一浮条这些 chrome 上）。
    /// 只有宿主认得自己的浮层清单（截图侧＝<c>CaptureWindow.IsOverlayPoint</c> + <c>IsOverlayHit</c>），
    /// 故判据交给它。用途见 <see cref="TryCommitNoteFromCanvasPress"/>。
    /// </summary>
    internal Func<PointerPressedEventArgs, bool>? CanvasPressTest { get; set; }

    private string _note = string.Empty;
    private bool _editing;
    private int _corner = CornerMax;
    private bool _tailShown = true;

    /// <summary>用户按了 − 或 +：参数＝编号增量（-1 / +1）。</summary>
    public event Action<int>? NumberStepped;

    /// <summary>说明文字落定（空串＝清除说明）。</summary>
    public event Action<string>? NoteCommitted;

    /// <summary>用户按了 × ：只删这一个序号。</summary>
    public event Action? DeleteRequested;

    /// <summary>用户改了胶囊圆角（拖动或滚轮）。参数＝百分比 0–100。</summary>
    public event Action<int>? CapsuleCornerChanged;

    /// <summary>用户按了「→」：把这一颗的引线翻个面（画出来／藏掉）。本控件只报意图，
    /// 当前是哪种状态由宿主照图元取反，回显走 <see cref="SetState"/>。</summary>
    public event Action? TailToggled;

    /// <summary>XAML 可达性要求 public 无参构造（AVLN3000），故这里不能用 internal。</summary>
    public CounterPill()
    {
        InitializeComponent();
        NoteBox.KeyDown += OnNoteBoxKeyDown;
        NoteBox.LostFocus += (_, _) => CommitNote();

        // dimRoot 传整条内容栈：微调态下除「圆角」这一组以外全部让位（按钮与分隔线一起压暗）。
        // plate 传**黑条本体**：本条的自然宽度可能小于整排刻度（尤其编号只有一位时），刻度落到条外
        // 就只剩"白字压白底"——控制器会在浮起时把黑条撑到刚好装下整排刻度（见其"底片"那段）。
        _fine = new PillValueFineController(PillContent, FineLayer, FineM2, FineM1, FineCur, FineP1, FineP2, PillHost);
        _cornerSlot = _fine.Attach(GroupCorner, CornerValue, CornerMin, CornerMax,
            () => _corner, ApplyFromFine, CornerStepScale);

        GroupCorner.PointerWheelChanged += OnCornerWheel;
    }

    /// <summary>是否正在编辑说明文字。宿主据此在重绘途中不要把浮条收掉。</summary>
    public bool IsEditingNote => _editing;

    /// <summary>
    /// 是否正处在"按住数值微调"中。宿主据此两件事：把**重活**推到松手之后；以及**不要重算浮条位置**
    /// （黑条此刻被撑宽，照撑宽后的矩形判"与参数条是否相交"会在拖到一半时突然翻边）。
    /// </summary>
    public bool IsFineActive => _fine.IsFineActive;

    /// <summary>
    /// **可见黑条**在控件内的矩形（相对控件左上角）。命中与让位判定用它，理由见类型注释。
    ///
    /// <para>用 <see cref="Layoutable.DesiredSize"/> 现算、**不读 Bounds**：宿主量宽走的是
    /// <c>LayoutMetrics.MeasureNow</c>（只测不排），那时 Bounds 还停在上一帧的值。</para>
    /// </summary>
    internal Rect PillBounds
    {
        get
        {
            double w = PillHost.DesiredSize.Width;
            double h = PillHost.DesiredSize.Height;
            // 可绘制区 = RootPanel 的期望宽（已含 MinWidth=260 那道下限）；黑条更宽时以黑条为准。
            double cw = Math.Max(RootPanel.DesiredSize.Width, w);
            double ch = Math.Max(RootPanel.DesiredSize.Height, h);
            return new Rect((cw - w) / 2, (ch - h) / 2, w, h);
        }
    }

    /// <summary>
    /// 回显：编号显示文本、说明文字、胶囊圆角与「圆角」「→」两格的显隐。只写字段、**不触发任何事件**。
    /// 编辑途中跳过说明那一格——宿主在提交后会回灌一次，那时若把输入框摆回按钮态，光标会凭空消失。
    /// </summary>
    public void SetState(string label, string note, int capsuleCorner, bool showCorner,
        bool showTail, bool tailShown)
    {
        ValueText.Text = label;

        // 「→」这一格：没有引线的序号不显示它（连分隔线一起），有引线才谈得上藏不藏。
        // 状态只用明暗表达，故这里只写 Opacity；放在编辑态早退之前——改它不碰说明框的光标。
        BtnTail.IsVisible = showTail;
        SepTail.IsVisible = showTail;
        if (showTail && tailShown != _tailShown)
        {
            _tailShown = tailShown;
            TailIcon.Opacity = tailShown ? 1.0 : 0.3;
        }

        _note = note ?? string.Empty;
        if (_editing) return;
        NoteLabel.Text = _note.Length == 0 ? "A" : _note;
        // 说明文字可能很长（渲染侧一行排开、不折行），把条撑到横跨屏幕就不像浮条了；
        // 输入框另有 40 字的硬上限（XAML 的 MaxLength），这里是防御性回显。
        NoteLabel.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        NoteLabel.MaxWidth = 220;

        // 非胶囊外形时整格连它的分隔线一起收掉：只藏格子会留一道悬空的分隔线。
        GroupCorner.IsVisible = showCorner;
        SepCorner.IsVisible = showCorner;
        if (showCorner)
        {
            int clamped = Math.Clamp(capsuleCorner, CornerMin, CornerMax);
            // 幂等早退：值没变就别碰 Text——宿主的回灌很频繁，无谓的写值会连带一次布局（条宽会变）。
            if (clamped != _corner)
            {
                _corner = clamped;
                CornerValue.Text = clamped.ToString();
            }
        }
    }

    /// <summary>立刻收回浮起层（浮条隐藏、选区变化、窗口失活等）。不走过渡——残影比动效更糟。</summary>
    public void Collapse() => _fine.Collapse();

    private void OnMinusClick(object? sender, RoutedEventArgs e) => NumberStepped?.Invoke(-1);
    private void OnPlusClick(object? sender, RoutedEventArgs e) => NumberStepped?.Invoke(1);
    private void OnDeleteClick(object? sender, RoutedEventArgs e) => DeleteRequested?.Invoke();

    /// <summary>「→」：只报"翻一下"，本地那个 <c>_tailShown</c> 一动不动——等宿主改完图元回灌一次 SetState。
    /// 这样本控件永远不掌握第二份真相，撤销/重做把引线翻回原样时图标也会跟着回去。</summary>
    private void OnTailClick(object? sender, RoutedEventArgs e) => TailToggled?.Invoke();

    private void OnNoteClick(object? sender, RoutedEventArgs e) => BeginNoteEdit();

    /// <summary>切进说明文字的编辑态（点那一格、或宿主用快捷键派发时都走这里）。</summary>
    public void BeginNoteEdit()
    {
        if (_editing) return;
        // 进编辑态前先收掉可能还挂着的浮起层：说明输入框与圆角刻度同时出现会叠在黑条中段。
        _fine.Collapse();
        _editing = true;
        NoteBox.Text = _note;
        BtnNote.IsVisible = false;
        NoteBox.IsVisible = true;
        NoteBox.Focus();
        NoteBox.SelectAll();
    }

    private void OnNoteBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndNoteEdit();
            e.Handled = true;
        }
    }

    void CommitNote()
    {
        if (!_editing) return;
        string next = (NoteBox.Text ?? string.Empty).Trim();
        EndNoteEdit();
        if (next == _note) return;
        _note = next;
        NoteLabel.Text = _note.Length == 0 ? "A" : _note;
        NoteCommitted?.Invoke(_note);
    }

    void EndNoteEdit()
    {
        _editing = false;
        NoteBox.IsVisible = false;
        BtnNote.IsVisible = true;
    }

    /// <summary>
    /// 「点空白处＝提交这句说明」这一笔的**唯一收口动作**：正在编辑说明则就地提交并返回 true，宿主据此
    /// 必须把这一笔**当成已被消费**（<c>e.Handled = true</c> 后直接 return）。
    ///
    /// <para>⚠ 为什么必须吞：序号工具下的"在画布上按下"本身就是"在这里落一颗新序号"。不吞的话同一下点击
    /// 会既结字又落一颗（2026-09-19 实报），用户看到的现象是"写完说明点旁边，凭空多出一个序号"。</para>
    ///
    /// <para>两个宿主的调用时机不同，是相位差异不是两种做法：截图侧由本控件挂在窗口根上的 Tunnel 处理器
    /// 自己吞（<c>CaptureSession</c> 的落图元在<b>冒泡</b>里）；贴图侧的按下走窗口自己的 Tunnel 且注册得
    /// 比本控件早，等不到这里，故由 <c>StickerWindow</c> 在起笔前直接调本方法。</para>
    /// </summary>
    public bool TryCommitNoteFromCanvasPress()
    {
        if (!_editing) return false;
        CommitNote();
        return true;
    }

    /// <summary>拖动/滚轮落值：夹量程 → 刷数值 → 上报宿主。</summary>
    private void ApplyFromFine(int value)
    {
        int next = Math.Clamp(value, CornerMin, CornerMax);
        if (next == _corner) return;
        _corner = next;
        CornerValue.Text = next.ToString();
        CapsuleCornerChanged?.Invoke(next);
    }

    private void OnCornerWheel(object? sender, PointerWheelEventArgs e)
    {
        int delta = e.Delta.Y > 0 ? 1 : e.Delta.Y < 0 ? -1 : 0;
        if (delta == 0) return;
        _fine.Bump(_cornerSlot, delta);
        // 吞掉滚轮：否则会继续冒泡到窗口级的「滚轮缩放选区」（贴图侧则是滚轮缩放贴图）。
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 「点空白处＝确定」：构造里挂在 LostFocus 上的提交在两个宿主里都打不出来——遮罩画布与贴图本体
        // 一律不可聚焦，点上去不会把焦点从输入框上摘走，于是用户看到的现象是"点了别处＝这句说明白写了"。
        // 故在窗口根上补一个 Tunnel 按下：早于业务处理器看到这一笔，先按新值提交，并且**当这一笔落在画布上时
        // 就地吞掉**（否则序号工具会把同一下点击再当成"在这里落一颗新序号"，2026-09-19 实报）。
        // 挂在根上而不是改 CaptureSession——那个文件在重点保护清单里，不为收身时机动它。
        if (e.Root is Interactive root)
        {
            _rootPressHandler = (_, args) =>
            {
                if (!_editing) return;
                if (args.Source is Visual src && IsInsideNoteBox(src)) return;
                // 先问宿主"这是不是一笔画布按下"，再提交：落在画布上的那一笔只用来结字，吞掉它才不会再
                // 顺手落一颗新序号（见 TryCommitNoteFromCanvasPress）；落在别条栏上的照旧放行，
                // 否则用户得点第二下才按得到那个按钮。
                bool onCanvas = CanvasPressTest?.Invoke(args) ?? false;
                if (!TryCommitNoteFromCanvasPress()) return;
                if (onCanvas) args.Handled = true;
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

    /// <summary>按下点是否落在说明输入框自己（含模板内的文字层）上——框内拖动选字不该被当成"点了别处"。</summary>
    bool IsInsideNoteBox(Visual node)
    {
        if (ReferenceEquals(node, NoteBox)) return true;
        foreach (var ancestor in node.GetVisualAncestors())
            if (ReferenceEquals(ancestor, NoteBox)) return true;
        return false;
    }
}
