using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Windows;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 标注<b>编辑期</b>宿主胶水：贴图窗口与录屏覆盖层共用的那一份。
///
/// <para>这里收的是"共享服务之外还必须在某个宿主里跑"的那批接线——选中态与参数条回显、已有图元的
/// 点选/拖本体/拖手柄手势（含合帧闸门）、方向键步进、滚轮八档微调表、参数条回灌与攒档、
/// 圆角浮条与序号浮条的贴位与收起、文字层的指针路由与历史并入。判据与算式一律不在此处二次实现：
/// 命中派发在 <see cref="AnnotationShapeEditor"/>，落笔在 <see cref="AnnotationInteractionController"/>，
/// 样式分派在 <see cref="AnnotationStyleApplier"/>，参数取值在 <see cref="AnnotationToolbar"/>，
/// 文字状态机在 <see cref="TextAnnotationController"/>，历史簿记在 <see cref="AnnotationEditHistory"/>。</para>
///
/// <para>宿主差异（坐标换算、成品烘焙、光标、指针捕获、承载浮条的面板）全部走
/// <see cref="IAnnotationEditHost"/>。2026-09-22 自 <c>StickerWindow</c> 逐位上提，贴图改为消费方，
/// 使录屏标注不必再写第三份。</para>
/// </summary>
internal sealed partial class AnnotationEditSession : ITextAnnotationHost
{
    private readonly IAnnotationEditHost _host;

    /// <summary>浮条与元素之间的垂直间隙（DIP，两枚同档）。</summary>
    private const double PillGap = 10;

    /// <summary>停手多久之后把攒着的连续量改动落进历史。</summary>
    private const int ContinuousCommitDelayMs = 260;

    public AnnotationEditSession(IAnnotationEditHost host)
    {
        _host = host;
        Controller = new AnnotationInteractionController(host);
        _texts = new TextAnnotationController(this);

        // 两枚浮条（与截图侧同一个控件）：控件只报意图，改文档/落历史/回显全在本类。
        if (host.RadiusPill != null)
        {
            host.RadiusPill.RadiusChanged += OnSpotlightRadiusPillChanged;
        }
        if (host.CounterPillView != null)
        {
            host.CounterPillView.NumberStepped += OnCounterPillNumberStepped;
            host.CounterPillView.NoteCommitted += OnCounterPillNoteCommitted;
            host.CounterPillView.DeleteRequested += OnCounterPillDeleteRequested;
            host.CounterPillView.CapsuleCornerChanged += OnCounterPillCapsuleCornerChanged;
            host.CounterPillView.TailToggled += OnCounterPillTailToggled;
        }
        // 选中图元的几何一变就把浮条挪过去：选中/取消、拖动与 8 向缩放的预览、撤销与方向键步进，
        // 四条来路都汇在这一个通知上（与截图侧同一个机制）。
        if (host.EditLayer != null)
        {
            host.EditLayer.SelectionGeometryChanged += RefreshSpotlightRadiusPill;
            host.EditLayer.SelectionGeometryChanged += RefreshCounterPill;
        }
    }

    // ──────────────────────────────────────────
    // 对宿主暴露的入口
    // ──────────────────────────────────────────

    /// <summary>落笔控制器：宿主在自己的指针路由里按既有时序调 Begin/Move/End。</summary>
    public AnnotationInteractionController Controller { get; }

    /// <summary>箭头整体缩放档，起笔时喂给 <c>AnnotationGestureSettings.ArrowScale</c>。
    /// 只由"选中箭头后滚轮微调"写入：微调既有箭头时顺带把默认值带走，下一支箭头沿用同一大小。</summary>
    public float ArrowScale { get; set; } = 1f;

    /// <summary>当前选中的矢量图元 Id：图层据此画高亮，命中测试据此放宽"已选中图元的外接框/端点"。</summary>
    public Guid? SelectedId => _selectedId;

    /// <summary>本次手势的指针；宿主在"松手被键盘打断"这类中断路径上要用它释放捕获。</summary>
    public IPointer? ActivePointer => _pointer;

    /// <summary>编辑手势是否正在进行（宿主的滚轮/移动/松手路由据此早退）。</summary>
    public bool IsEditingShape => _shapeEditor?.IsEditing == true;

    /// <summary>贴图有两条预览来源——正在画的新笔与正在拖的既有图元。两者不会同时存在，拖动一路优先。
    /// 宿主实现 <c>IAnnotationHost.InvalidateAnnotationPreview</c> 时调这一条。</summary>
    public void RefreshLayerPreview()
    {
        var layer = _host.EditLayer;
        if (layer == null) return;
        layer.SetPreviewShape(_shapeEditor?.Preview ?? Controller.Preview);
        layer.InvalidateVisual();
    }

    /// <summary>浮条是贴在画布上的一层 UI：落在它上面的按下/移动/松手/滚轮归浮条自己，不参与
    /// "起笔 / 编辑图元 / 拖动窗口"。⚠ 宿主的指针事件若在<b>隧道相位</b>注册，必须靠这两道守卫，
    /// 否则按住浮条数值拖动会变成"拖动窗口"，在浮条上滚一下会变成"缩放窗口"。</summary>
    public bool IsFromRadiusPill(object? source) => IsInside(_host.RadiusPill, source);

    public bool IsFromCounterPill(object? source) => IsInside(_host.CounterPillView, source);

    private static bool IsInside(Visual? root, object? source)
    {
        if (source is not Visual visual || root == null) return false;
        if (ReferenceEquals(visual, root)) return true;
        return visual.GetVisualAncestors().Any(a => ReferenceEquals(a, root));
    }

    /// <summary>浮窗/控制条建好后注入共享文本参数面板；一个参数条一生只挂一次——
    /// 重复绑定会让"描边 Popup 点一次开、再点一次仍开"这类开关逻辑互相抵消。</summary>
    public void AttachTextPanel(AnnotationToolbar toolbar)
    {
        toolbar.TextController = _texts;
        try { _texts.InitTextToolControls(); }
        catch (Exception ex) { Core.AppLogger.Warn("标注参数条初始化文本参数面板失败: " + ex.Message); }
    }

    // ──────────────────────────────────────────
    // 已有图元的编辑手势（点选 + 拖本体 + 拖手柄）
    // ──────────────────────────────────────────

    /// <summary>共享编辑引擎（与截图侧同一个类）：拖动期只写图层预览，松手一次性写回文档＝一条撤销。</summary>
    private AnnotationShapeEditor? _shapeEditor;
    /// <summary>当前选中图元 Id。</summary>
    private Guid? _selectedId;
    /// <summary>本次手势的指针，松手中途被键盘打断时也要靠它释放捕获。</summary>
    private IPointer? _pointer;
    /// <summary>合帧闸门里压着的最后一次指针窗口坐标：PointerMoved 只更新它，落图层预览等渲染帧。</summary>
    private Point? _pendingMovePoint;
    /// <summary>是否已为本手势挂过一次渲染帧回调，避免每个输入事件都排一次队。</summary>
    private bool _moveRafScheduled;

    /// <summary>
    /// 编辑已有图元的唯一派发入口。<paramref name="editExisting"/>＝本次按下是否按住了 Ctrl。
    /// 手柄免 Ctrl、同工具免 Ctrl 点选、跨工具要 Ctrl、本体顺序＝z 序、文本层插在序号与箭头之间，
    /// 全部收在 <c>AnnotationShapeEditor.TryBeginPress</c>，截图遮罩调的是同一个入口，
    /// 故两侧"点哪里能拖什么"不可能漂移。这里只负责三件事：坐标换算、方向键步进会话收口、
    /// Ctrl 点空白取消选中。返回 false＝未命中，调用处照常起笔画新图元。
    /// </summary>
    public bool TryBeginShapeEdit(Point windowPoint, PointerPressedEventArgs e, bool editExisting)
    {
        var history = _host.History;
        if (history == null || _host.EditLayer == null) return false;
        if (!_host.TryGetAnnotationPoint(windowPoint, out Point contentPoint)) return false;
        // 起一次拖动手势＝另起一笔编辑：先把还开着的方向键步进会话提交掉，
        // 否则两笔改动会并进同一条撤销记录里。
        FlushNudgeSession();
        AnnotationShapeEditor editor = EnsureShapeEditor();

        bool began = editor.TryBeginPress(
            history.Document,
            new System.Drawing.PointF((float)contentPoint.X, (float)contentPoint.Y),
            _selectedId,
            editExisting,
            // 文本层是活可视树、不在共享编辑器的六种图元里，那一脚交回这里（命中即选中并起拖，不算编辑手势）
            tryTextBody: _ => TryBeginTextMove(windowPoint, e),
            hostBegin: (shape, target) => BeginShapeEdit(shape, target, e),
            tol: _host.HitTolerance,
            activeKind: AnnotationToolShapes.KindOfTool(_host.ActiveAnnotationTool),
            isActiveTextTool: _host.ActiveAnnotationTool == ToolMode.Text);

        if (!began && editExisting)
        {
            // Ctrl 点空白＝"取消选中"这一个动作，矢量与文字两侧一起清：只清矢量会留下一条仍有高亮的
            // 文字选中，用户看不见差别，按 Delete 却删掉了它。免 Ctrl 那一路不清——它本来就只是"没抓到手柄"。
            ClearAllSelections();
        }
        return began;
    }

    /// <summary>
    /// 共享编辑器命中后的<b>宿主副作用</b>：切工具、选中、箭头缩放档同步、指针捕获四件一起改，
    /// 与截图侧 <c>CaptureSession.BeginShapeEdit</c> 一一对应（起手由编辑器自己做，这里不碰）。
    /// </summary>
    private void BeginShapeEdit(AnnotationShape shape, AnnotationEditTarget target, PointerPressedEventArgs e)
    {
        // 手柄只在图元选中后才画出来，抓它＝继续编辑同一个目标，不该顺手清掉别的选中；点本体＝另起一个目标
        if (!target.IsHandle) ClearAllSelections();
        // 与截图侧同一动作：点中哪种图元就切到哪种工具（切工具会展开参数条并触发重新定位）
        _host.ForceToolForShape(shape.Kind);
        SelectShape(shape.Id);
        if (shape is ArrowShape arrow) ArrowScale = arrow.Scale;  // 滚轮改箭头缩放要接着这次的值
        _pendingMovePoint = null;                                 // 上一段残留的待落坐标不能喂给这一段
        _pointer = e.Pointer;
        // 捕获指针：图元可以被拖出画板外，不捕获就收不到后续的 Moved/Released
        _host.CapturePointer(e.Pointer);
        // 上面 SelectShape 会顺带把圆角浮条按**旧**几何贴出来，而拖动途中位置不再重算 ⇒ 起手即收起，
        // 松手由 EndShapeEdit 贴回新位置（与截图侧 CaptureSession.BeginShapeEdit 同一口径）。
        // 序号浮条同一条道理，与它一起收、一起贴回。
        HideSpotlightRadiusPill();
        HideCounterPill();
        // 抓手柄时不改光标：截图侧也只在拖本体那一路给 SizeAll，方向光标留给以后的悬停档
        _host.SetMoveCursor(!target.IsHandle);
        _host.LogDiag($"编辑手势 kind={shape.Kind} 把手={target.Kind} id={shape.Id:N} "
                      + $"图元数={_host.History?.Document.Count ?? 0} tol={_host.HitTolerance:F2}");
    }

    /// <summary>
    /// 图元拖动的合帧入口：PointerMoved 只记下最新指针位置，落图层预览一律推到渲染帧内。
    /// 不这么做的话，1000Hz 鼠标下每次事件都要把整条笔迹重算一份塞进图层（探针实测 600 点笔迹
    /// 9.7 微秒／事件＝38MB 每秒的垃圾），gen0 回收就按十几次的节奏插进画面。
    /// 落点仍在松手时无条件补一次，绝不会停在倒数第二帧上。
    /// 换算与"首次越过 2 像素才算真拖动"的判据都在共享编辑器里，截图侧同一条。
    /// </summary>
    public void RequestShapeMove(Point windowPoint)
    {
        _pendingMovePoint = windowPoint;
        if (_moveRafScheduled) return;
        _moveRafScheduled = true;
        _host.PostRenderFrame(() =>
        {
            _moveRafScheduled = false;
            // 手势可能已在同一帧内结束（End 里已 Flush），此时不能再拿旧坐标喂编辑器
            if (_shapeEditor?.IsEditing != true) return;
            FlushShapeMove();
        });
    }

    /// <summary>把压着的最后一次指针位置落进图层预览；无待落坐标时是空操作。
    /// 一律由编辑器从按下瞬间抓到的原始图元重算，与逐事件跑的是同一个换算，故结果只有"晚一帧"这一种差别。</summary>
    private void FlushShapeMove()
    {
        Point? pending = _pendingMovePoint;
        if (pending == null || _shapeEditor == null) return;
        _pendingMovePoint = null;
        Point content = _host.ToContentPoint(pending.Value);
        _shapeEditor.Apply(new System.Drawing.PointF((float)content.X, (float)content.Y));
    }

    /// <summary>松手收尾：确有位移时编辑器把最后一次预览一次性写回文档并返回 true，据此提交一次撤销历史
    /// （撤销即回到拖前）；零位移的纯点选什么都不写、也不进历史。
    /// 选中态<b>跨松手保留</b>（与截图侧同一口径）：留着高亮，用户才能接着按 Delete 删掉它。</summary>
    public void EndShapeEdit(IPointer? pointer)
    {
        // 松手前无条件补落一次：闸门只推迟不丢弃，否则图元会回弹到倒数第二帧的位置。
        // 必须在提交之前跑——"确有位移"的判定本身就由这一次落点做出。
        FlushShapeMove();
        bool committed = _shapeEditor?.Commit() == true;
        if (pointer != null) _host.ReleasePointer(pointer);
        _pointer = null;
        _host.SetMoveCursor(false);

        // 若按着方向键又起了一次拖动，先结清那段步进，免得它和这次拖动并成一条撤销记录
        FlushNudgeSession();
        if (committed) CommitChange();
        // 起手时被 BeginShapeEdit 收起的圆角浮条在这里贴回新位置：确有位移时它已随文档 Changed
        // 通知过一遍，但零位移的纯点选什么都不写、没有任何通知 ⇒ 无条件补一次（与截图侧同一口径）。
        RefreshSpotlightRadiusPill();
        RefreshCounterPill();
    }

    /// <summary>丢掉手上那段编辑手势（宿主退出标注/换档时收中断路径）。
    /// 编辑器 Cancel 里已经把预览抹掉并经宿主重绘，这里不再补一次。</summary>
    public void CancelShapeEdit() => _shapeEditor?.Cancel();

    /// <summary>编辑器与宿主的会话同生命周期：底图还没就绪时不建（此时也没有可编辑的图元）。</summary>
    private AnnotationShapeEditor EnsureShapeEditor()
    {
        if (_shapeEditor != null) return _shapeEditor;
        return _shapeEditor = new AnnotationShapeEditor(
            // 每帧按 id 现取文档现值：滚轮在拖动途中改过的粗细/箭头档/序号半径不能被回退
            id => _host.History?.Document.Shapes.FirstOrDefault(x => x.Id == id),
            // 预览值由编辑器自己的 Preview 属性给出，本委托只负责把它连同"正在画的新笔"一起推给图层
            _ => _host.InvalidateAnnotationPreview(),
            shape => _host.History?.Document.ReplaceShape(shape),
            // 这里没有"改动前先压一份快照"这一步：历史压的是上一份已提交状态，
            // 故历史由松手时 Commit() 的返回值驱动（见 EndShapeEdit）
            () => { },
            () => _host.SpotlightContentBounds);
    }

    // ──────────────────────────────────────────
    // 方向键步进
    // ──────────────────────────────────────────

    /// <summary>步进会话的目标图元：与当前选中项不一致即视为另起一段，先结清上一段。</summary>
    private Guid? _stepTargetId;
    /// <summary>本段步进是否已有未提交的位移。历史是"改完再 Commit 上一份已提交状态"，
    /// 故这里压的是"待提交"而不是截图侧那种"待快照"——同一会话内不落提交，Ctrl+Z 一次回到整段步进之前。</summary>
    private bool _stepPending;

    /// <summary>结清方向键步进会话：一段连续步进只在结束时提交一次。</summary>
    public void FlushNudgeSession()
    {
        if (!_stepPending) return;
        _stepPending = false;
        _stepTargetId = null;
        CommitChange();
    }

    /// <summary>把当前选中的矢量图元平移 dx/dy 内容像素。返回 false＝没有选中图元，
    /// 调用处据此把按键交还给其他通路。文本图层刻意不在本档，理由同截图侧。</summary>
    public bool StepSelectedShape(int dx, int dy)
    {
        var history = _host.History;
        if (history == null || !_selectedId.HasValue) return false;
        AnnotationShape? target = history.Document.Shapes.FirstOrDefault(x => x.Id == _selectedId.Value);
        if (target == null) return false;

        // 六类图元的平移解与拖动手势共用 Core 那一份（AnnotationGeometry.Translate 就是 EditShape 的 Move 档）。
        // 聚光灯传 bounds＝整张内容矩形，与截图侧传选区同一口径，故"步进到边缘"和"拖到边缘"结果一致。
        AnnotationShape? moved = AnnotationGeometry.Translate(target, dx, dy, _host.SpotlightContentBounds);
        if (moved == null) return false;

        if (_stepTargetId != target.Id)
        {
            FlushNudgeSession();
            _stepTargetId = target.Id;
        }
        history.Document.ReplaceShape(moved);
        _stepPending = true;
        return true;
    }

    // ──────────────────────────────────────────
    // 选中态与参数条回显
    // ──────────────────────────────────────────

    /// <summary>
    /// 选中/取消选中的唯一写入口：簿记 id、图层高亮与重绘三件一起改（对齐截图侧
    /// <c>CaptureSession.SelectShape</c>）。图层那个 <c>SelectedShapeId</c> 是普通属性、不自绘重绘，
    /// 少了后面两下就是"点中但不拖"的纯选中毫无高亮反馈。
    /// </summary>
    public void SelectShape(Guid? id)
    {
        _selectedId = id;
        var layer = _host.EditLayer;
        if (layer == null) return;
        layer.SelectedShapeId = id;
        layer.InvalidateVisual();
        // 选中谁、参数条就回显谁（与截图侧 CaptureSession.SelectShape 同一张分派表）：图元上已有的样式既是
        // 条上的读数，也顺带成为下一笔的默认值。没有回显的那些档会停在"上一颗的记忆"上，
        // 于是选中一个虚线矩形再点线型按钮，改出来的还是别的档——2026-09-19 用户报的正是这件事。
        if (id.HasValue && _host.EditToolbar is { } tb)
        {
            switch (SelectedShape())
            {
                case ArrowShape arrow: tb.SyncArrowToolState(arrow); break;
                case RectangleShape rect: tb.SyncShapeToolState(rect); break;
                case CounterShape counter: tb.SyncCounterToolState(counter); break;
                case StrokeShape stroke: tb.SyncStrokeToolColor(stroke.Color); break;
                // 聚光灯与马赛克各自整包回显：滚轮那一路改完图元会把滑杆摆到新值，摆杆经"面板整包"报点回流到
                // 分派表；条上若还留着上一颗的取值，那一次回流就会顺手盖掉这一颗（截图侧同一套路）。
                case SpotlightShape spot: tb.SyncSpotlightToolState(spot); break;
                case BlurShape blur: tb.SyncBlurToolState(blur); break;
            }
        }
    }

    /// <summary>取消图元选中（只清高亮与命中放宽条件，不动文档）。</summary>
    public void ClearSelection() => SelectShape(null);

    /// <summary>取消全部标注选中：矢量图元的高亮与文字层的选中一起清。对齐截图侧
    /// <c>CaptureSession.ClearAllSelections</c>——取消选中是"一个动作"，只清一半会让 Delete
    /// 打在一个已经看不见的高亮目标上。</summary>
    public void ClearAllSelections()
    {
        _texts.DeselectText();
        ClearSelection();
    }

    /// <summary>橡皮擦下 Ctrl+点击：与截图一样，把目标从既有擦除名单中摘出，直到下一笔再次擦到。</summary>
    public void RaiseTargetAboveEraser(Point windowPoint, Point textCanvasPoint)
    {
        var history = _host.History;
        if (history == null || !_host.TryGetAnnotationPoint(windowPoint, out Point content)) return;
        _texts.EndActiveTextEditing("RaiseAboveEraser");
        // 文字层位于矢量层之上，先认文字；图元仍按同一份命中逻辑与宿主缩放容差判定。
        Guid textId = _texts.FindTextAtPoint(textCanvasPoint)?.Id ?? Guid.Empty;
        Guid shapeId = textId == Guid.Empty
            ? AnnotationHitTest.FindTopmost(history.Document,
                new System.Drawing.PointF((float)content.X, (float)content.Y), null, _host.HitTolerance)?.Id ?? Guid.Empty
            : Guid.Empty;
        if (shapeId == Guid.Empty && textId == Guid.Empty) return;
        var replacements = history.Document.Shapes.OfType<ErasureShape>()
            .Select(erase => erase.WithoutTargets(shapeId, textId)).OfType<ErasureShape>().ToArray();
        if (replacements.Length == 0) return;
        FlushNudgeSession();
        FlushToolbarStyleCommit();
        history.SetTextSnapshots(_texts.CreateTextSnapshot());
        foreach (ErasureShape next in replacements) history.Document.ReplaceShape(next);
        _texts.ApplyErasureMasks(history.Document);
        _host.InvalidateAnnotationPreview();
        // 贴图／录屏历史都在改后压上一份已提交态；一次提升＝一条撤销。
        CommitChange();
        _host.LogDiag("橡皮擦提升目标=" + (textId != Guid.Empty ? "文字" : "图元")
            + "，改写擦除记录=" + replacements.Length);
    }

    /// <summary>当前选中的矢量图元；未选中、或已被撤销/删除掉时返回 null。</summary>
    public AnnotationShape? SelectedShape()
    {
        var history = _host.History;
        if (history == null || !_selectedId.HasValue) return null;
        return history.Document.Shapes.FirstOrDefault(x => x.Id == _selectedId.Value);
    }

    // ──────────────────────────────────────────
    // 参数条回灌已画好的图元（2026-09-20）
    // ──────────────────────────────────────────
    // 截图侧那一族事件的对应物：参数条只报"用户刚改了哪一档"，落哪一格由 Core 之外唯一的
    // 分派表 AnnotationStyleApplier 决定——两侧共用同一张表，才不会一边补齐、另一边继续漏。

    /// <summary>参数条某一档被用户改过：把它落到<b>当前选中的那一个</b>图元上。没有选中图元时整段跳过，
    /// 这一档自然只成为下一笔的记忆值（记忆位本来就住在参数条里）。</summary>
    public void ApplyToolbarStyleToSelection(AnnotationStylePart parts)
    {
        var history = _host.History;
        if (history == null) return;
        AnnotationShape? selected = SelectedShape();
        if (selected == null) return;
        // null ＝ 这一档在该图元上没有对应字段、或比过字段确实没变（滚轮回灌滑杆时会自己撞回来），
        // 据此挡掉一次空提交——否则撤销栈里多出一条"按了撤销画面却不动"的假记录。
        AnnotationShape? next = AnnotationStyleApplier.Apply(selected, _host.CreateGestureSettings(ArrowScale), parts);
        if (next == null) return;

        history.Document.ReplaceShape(next);
        _host.InvalidateAnnotationPreview();
        _host.LogDiag("参数条回灌图元 档=" + parts + " kind=" + next.Kind + " id=" + next.Id.ToString("N"));
        // 拖滑杆是逐帧的连续量，攒到停手后落一次；点选档（颜色／形状／线型／头型／样式小样）一次点击＝一次改动。
        if (IsContinuousStylePart(parts)) ScheduleToolbarStyleCommit();
        else CommitChange();
    }

    /// <summary>连续量档位：拖一次滑块就是十几到几十次改值，每档一次 Commit 等于把"克隆整张干净底图 +
    /// 重渲染全部图元"压在手指移动的每一帧上。其余档位都是单击（取色、形状档、线型、头型、样式小样…）。
    /// 两个"面板整包"归连续量：包里主导的那一格（暗幕／强度）本来就是滑杆，形状档／模式那两枚按钮跟着
    /// 晚 260ms 落一条历史，画面早已改到位，看不出来。</summary>
    private static bool IsContinuousStylePart(AnnotationStylePart parts) => parts
        is AnnotationStylePart.Thickness
        or AnnotationStylePart.CounterRadius
        or AnnotationStylePart.ShapeFill
        or AnnotationStylePart.SpotlightSettings
        or AnnotationStylePart.BlurSettings;

    private bool _toolbarStylePending;
    private DispatcherTimer? _toolbarStyleCommitTimer;

    /// <summary>把参数条的连续量改动攒到停手后落一次（连续改值会把上一次的定时器推后）。</summary>
    private void ScheduleToolbarStyleCommit()
    {
        _toolbarStylePending = true;
        _toolbarStyleCommitTimer ??= CreateCommitTimer("参数条", FlushToolbarStyleCommit);
        _toolbarStyleCommitTimer.Stop();
        _toolbarStyleCommitTimer.Start();
    }

    /// <summary>立即落定攒着的参数条改动（停手、退出标注前都会调到）。幂等。</summary>
    public void FlushToolbarStyleCommit()
    {
        _toolbarStyleCommitTimer?.Stop();
        if (!_toolbarStylePending) return;
        _toolbarStylePending = false;
        Core.AppLogger.Debug("[ToolbarStyle] 落定参数条改动 Commit");
        CommitChange();
    }

    // ──────────────────────────────────────────
    // 提交与历史
    // ──────────────────────────────────────────

    /// <summary>一次完整提交：浮条与参数条攒着的连续量改动一并落进这一次历史，标志位跟着清。
    /// 不清的话 260ms 后那个定时器会再提交一条内容相同的空历史（空推一次快照，白付一次开销）。</summary>
    public void CommitChange()
    {
        _spotlightRadiusPending = false;
        _counterCornerPending = false;
        _toolbarStylePending = false;
        _host.CommitAnnotationChange();
        SyncHistoryState();
    }

    /// <summary>参数条在另一扇窗（贴图浮窗／录屏控制条）里，撤销可用态只能由本类主动回写。</summary>
    public void SyncHistoryState()
    {
        _host.EditToolbar?.SetHistoryState(_host.History?.CanUndo == true);
    }

    /// <summary>撤销一步标注。控制条/浮窗撤销按钮与 Ctrl+Z 共用这一份实现。</summary>
    public void Undo()
    {
        var history = _host.History;
        if (history == null) return;
        if (history.Undo())
        {
            // 历史只存快照不存控件：图元由 Document.Changed 自行重绘，文本层必须显式重建
            RestoreTextSnapshot();
            _host.InvalidateAnnotationPreview();
        }
        SyncHistoryState();
        // 撤销很可能把当前选中的那个图元整个撤掉：高亮一律作废，否则 Delete 打在已不存在的 id 上
        ClearAllSelections();
    }

    /// <summary>重做一步标注。与撤销同一份收口。</summary>
    public void Redo()
    {
        var history = _host.History;
        if (history == null) return;
        if (history.Redo())
        {
            RestoreTextSnapshot();
            _host.InvalidateAnnotationPreview();
        }
        SyncHistoryState();
        ClearAllSelections();
    }

    /// <summary>删除当前选中的标注对象（Delete 与 Backspace 等价）。
    /// 走两条不同的提交路：文字层是活可视树、进历史只能走 <see cref="CommitTextChange"/>；
    /// 矢量图元改完文档走 <see cref="CommitChange"/> 正常提交一次。</summary>
    public void DeleteSelected()
    {
        var history = _host.History;
        if (history == null) return;

        if (_texts.SelectedText != null)
        {
            if (!_texts.DeleteSelectedText()) return;
            CommitTextChange();
            _host.LogDiag("键盘删除文字层");
            return;
        }

        Guid? id = _selectedId;
        if (!id.HasValue) return;

        AnnotationDocument document = history.Document;
        int deletedNumber = document.Shapes.FirstOrDefault(x => x.Id == id.Value) is CounterShape counter
            ? counter.Number : 0;

        if (!document.RemoveShape(id.Value)) return;
        // 重编号按设置项走（与截图侧 DeleteSelectedShape 读同一个开关）：
        // 关掉＝保留原编号，删掉的那个号就此空出。
        bool renumber = deletedNumber > 0 && Core.AppSettings.Current.CounterRenumberOnDelete;
        int renumbered = renumber ? document.RenumberCountersAfterDeletion(deletedNumber) : 0;

        CommitChange();
        ClearAllSelections();
        _host.LogDiag("键盘删除图元 id=" + id.Value.ToString("N") + " 序号=" + deletedNumber
            + (deletedNumber > 0 ? (renumber ? " 已重新编号 " + renumbered + " 个剩余序号" : "（按设置保留原编号）") : ""));
    }

    /// <summary>只清当前工具那一档画下的图元（文本档清的是活文字层）。类型判据与截图逐条一致。</summary>
    public void ClearCurrentToolAnnotations()
    {
        var history = _host.History;
        if (history == null) return;

        // 文本层不在 Document 里（它是活可视树），RemoveWhere 永远看不见它，故单开一条收口。
        // 时序与截图一致：先压"改动前"的那一份，再拆控件——撤销正好退回清掉的那批字。
        if (_host.ActiveAnnotationTool == ToolMode.Text)
        {
            if (_texts.TextLayers.Count == 0) return;
            CommitTextChange();
            _texts.ClearAllTexts();
            _host.InvalidateAnnotationPreview();
            SyncHistoryState();
            return;
        }

        if (history.Document.RemoveWhere(shape =>
                AnnotationToolShapes.MatchesTool(shape, _host.ActiveAnnotationTool)) == 0) return;
        CommitChange();
    }

    /// <summary>橡皮擦：矢量与文字一起清，一次清空＝一条历史。</summary>
    public void ClearAllAnnotations()
    {
        var history = _host.History;
        if (history == null) return;
        _texts.ClearAllTexts();
        history.ClearAnnotations();
        _host.InvalidateAnnotationPreview();
        SyncHistoryState();
    }

    /// <summary>把当前活文字层并入历史（不重建成品）。滑杆一次拖动几十 tick 都走这一条；
    /// 宿主自己的文本入口（如贴图的导出/清除）也调这一份，不另写第二遍。</summary>
    public void CommitTextChange()
    {
        var history = _host.History;
        if (history == null) return;
        history.SetTextSnapshots(_texts.CreateTextSnapshot());
        _host.CommitTextHistory();
        SyncHistoryState();
    }

    /// <summary>把历史里那一份文字快照重建回活可视树。</summary>
    private void RestoreTextSnapshot()
    {
        var history = _host.History;
        if (history == null) return;
        _texts.RestoreTextSnapshot(history.Texts);
        _texts.ApplyErasureMasks(history.Document);
    }

    /// <summary>260ms 攒档定时器：一次创建、反复 Stop/Start。</summary>
    private DispatcherTimer CreateCommitTimer(string origin, Action flush)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ContinuousCommitDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            flush();
        };
        return timer;
    }
}
