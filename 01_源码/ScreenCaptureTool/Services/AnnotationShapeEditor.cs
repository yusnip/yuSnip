using System;
using System.Drawing;
using System.Linq;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 一段"拖动手柄／拖动图元"手势的状态机。截图遮罩与贴图标注共用这一份：
/// 按下时抓到哪个把手（<see cref="AnnotationHitTest.HitTestEditTarget(AnnotationShape, PointF, float, bool)"/>）、
/// 拖到哪就变成什么（<see cref="AnnotationGeometry.EditShape"/>）、松手才写回文档，三件事在这里各只有一种做法。
///
/// <para>提交语义（protection.md「标注图元拖动只有一种提交语义」）：拖动期间只把结果交给 <c>setPreview</c>
/// 画进图层，文档一个字节都不改；<see cref="Commit"/> 才把最后那一帧一次性写回。于是一段手势＝一次文档写入
/// ＝一条撤销，且绝不会回弹到倒数第二帧上。</para>
///
/// <para>合帧闸门留在宿主：截图侧是 8ms 时间闸、贴图侧是每渲染帧一次，两者只决定"什么时候调
/// <see cref="Apply"/>"，本类对节流与时钟一无所知。</para>
/// </summary>
internal sealed class AnnotationShapeEditor
{
    /// <summary>纯点选（位移不足阈值）什么都不写、也不进撤销历史：贴图侧从第一天就是这么判的，截图侧照抄同一条。</summary>
    const float DisplaceThresholdPx = 2.0f;

    private readonly Func<Guid, AnnotationShape?> _resolve;
    private readonly Action<AnnotationShape?> _setPreview;
    private readonly Action<AnnotationShape> _replaceShape;
    private readonly Action _beginSession;
    private readonly Func<RectangleF?> _spotlightBounds;

    private AnnotationShape? _startShape;
    private AnnotationShape? _lastApplied;
    private PointF _start;

    public AnnotationShapeEditor(Func<Guid, AnnotationShape?> resolve, Action<AnnotationShape?> setPreview,
        Action<AnnotationShape> replaceShape, Action beginSession, Func<RectangleF?> spotlightBounds)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _setPreview = setPreview ?? throw new ArgumentNullException(nameof(setPreview));
        _replaceShape = replaceShape ?? throw new ArgumentNullException(nameof(replaceShape));
        _beginSession = beginSession ?? throw new ArgumentNullException(nameof(beginSession));
        _spotlightBounds = spotlightBounds ?? throw new ArgumentNullException(nameof(spotlightBounds));
    }

    /// <summary>当前正抓着的把手；未拖动时为 <see cref="AnnotationEditKind.None"/>。</summary>
    public AnnotationEditTarget Target { get; private set; }

    public bool IsEditing => _startShape != null;

    /// <summary>
    /// 已写进图层预览的那一帧（松手时由 <see cref="Commit"/> 一次性落进文档）。贴图侧的图层同时还要画
    /// <c>AnnotationInteractionController</c>"正在画的新笔"，两路预览靠它在一个漏斗里合流。
    /// </summary>
    public AnnotationShape? Preview => _lastApplied;

    /// <summary>
    /// 一次按下到底该起哪段编辑手势——截图遮罩与贴图标注共用的唯一判据：
    /// <list type="bullet">
    /// <item>选中图元的<b>手柄永远免 Ctrl</b>：它只在图元选中后才画出来（<c>DrawSelectedShapeHandles</c>），
    /// 点中它已是明确的编辑意图。</item>
    /// <item>拖<b>本体</b>一律要 <paramref name="editExisting"/>＝按住了 Ctrl（工具在按时按下默认画新图形）。
    /// 唯一例外见下条。</item>
    /// <item><b>画笔是"同工具免 Ctrl 点选"的唯一例外</b>（2026-09-18）：画笔工具下不按 Ctrl 永远＝起新笔，
    /// 要选中/拖动已有笔迹必须按住 Ctrl。</item>
    /// <item>本体命中顺序＝z 序：聚光灯 &gt; 模糊 &gt; 序号 &gt; <b>文本</b> &gt; 箭头 &gt; 矩形 &gt; 画笔。
    /// 文本层是宿主的活可视树、不在本类管的六种图元之内，故刻意插在序号与箭头之间由
    /// <paramref name="tryTextBody"/> 交回宿主自己完成——这也是这里不能直接用
    /// <c>AnnotationHitTest.FindTopmost</c> 的原因（那份不含文本层）。
    /// 各类型"什么叫命中"一律在 <c>AnnotationHitTest</c> 里定义（如形状工具未填充时只有线条算命中，
    /// 体内空白不命中），本类只管派发顺序，不再另写一份几何判据。</item>
    /// </list>
    /// 命中后先跑宿主的 <paramref name="hostBegin"/>（切工具、选中、捕获指针、置 Handled），再本类起手：
    /// 顺序不能反——宿主那句"切工具"可能顺带 Cancel 掉上一段手势，必须发生在起手之前。
    /// <paramref name="tol"/>＝屏幕像素口径的容差倍率（截图恒为 1，贴图按显示倍率放大）。
    /// <paramref name="activeKind"/>＝当前激活的标注工具对应图元类型；未按 Ctrl 时仅允许点选当前工具对应的图元，
    /// 按住 Ctrl 时才允许跨标注工具选择任意图元。
    /// <paramref name="isActiveTextTool"/>＝当前是否激活了文本工具。
    /// </summary>
    public bool TryBeginPress(AnnotationDocument doc, PointF pt, Guid? selectedId,
        bool editExisting, Func<PointF, bool>? tryTextBody,
        Action<AnnotationShape, AnnotationEditTarget> hostBegin, float tol = 1.0f,
        AnnotationShapeKind? activeKind = null, bool isActiveTextTool = false)
    {
        if (doc == null) return false;

        AnnotationShape? selected = selectedId.HasValue
            ? doc.Shapes.FirstOrDefault(s => s.Id == selectedId.Value)
            : null;
        if (selected != null)
        {
            AnnotationEditTarget handle = AnnotationHitTest.HitTestEditTarget(selected, pt, tol, isSelected: true);
            if (handle.IsHandle)
            {
                hostBegin(selected, handle);
                Begin(selected, handle, pt);
                return true;
            }
        }
        if (!editExisting && !activeKind.HasValue && !isActiveTextTool) return false;

        AnnotationShape? body = null;
        if (editExisting)
        {
            // 按了 Ctrl：按全局 z 序自上而下跨工具查找（与 AnnotationLayer 的绘制顺序同一份）
            body = AnnotationHitTest.FindSpotlightShapeAt(doc, pt);
            if (body == null) body = AnnotationHitTest.FindBlurShapeAt(doc, pt);
            if (body == null) body = AnnotationHitTest.FindCounterShapeAt(doc, pt, tol);
            if (body == null)
            {
                if (tryTextBody?.Invoke(pt) == true) return true;
                body = AnnotationHitTest.FindArrowShapeAt(doc, pt, selectedId, tol);
                if (body == null) body = AnnotationHitTest.FindRectangleShapeAt(doc, pt, tol);
                if (body == null) body = AnnotationHitTest.FindStrokeShapeAt(doc, pt, selectedId, tol);
            }
        }
        else
        {
            // 没按 Ctrl：仅对当前激活工具对应的图元类型进行命中测试（同工具免 Ctrl 点选/拖动）
            if (isActiveTextTool)
            {
                if (tryTextBody?.Invoke(pt) == true) return true;
                return false;
            }

            switch (activeKind)
            {
                case AnnotationShapeKind.Spotlight:
                    body = AnnotationHitTest.FindSpotlightShapeAt(doc, pt);
                    break;
                case AnnotationShapeKind.Blur:
                    body = AnnotationHitTest.FindBlurShapeAt(doc, pt);
                    break;
                case AnnotationShapeKind.Counter:
                    body = AnnotationHitTest.FindCounterShapeAt(doc, pt, tol);
                    break;
                case AnnotationShapeKind.Arrow:
                    body = AnnotationHitTest.FindArrowShapeAt(doc, pt, selectedId, tol);
                    break;
                case AnnotationShapeKind.Rectangle:
                    body = AnnotationHitTest.FindRectangleShapeAt(doc, pt, tol);
                    break;
                // 画笔刻意没有这一档＝"同工具免 Ctrl 点选"的唯一例外（2026-09-18 用户拍板）：
                // 笔迹命中半径 30 屏幕像素、且老笔迹随手可见，免 Ctrl 也能点中就等于"画过的地方不能再画"。
                // 画笔工具下不按 Ctrl 永远＝起新笔，要选中/拖动某条老笔迹一律按 Ctrl（走上面那一档全局 z 序）。
            }
        }

        if (body == null) return false;

        var move = new AnnotationEditTarget(AnnotationEditKind.Move);
        hostBegin(body, move);
        Begin(body, move, pt);
        return true;
    }

    /// <summary>
    /// 起手。<paramref name="startShape"/> 取按下那一刻文档里的本体：本类每次都用它的几何加总位移重算，
    /// 不做增量累加，故不会随帧数漂走（protection.md「贴图标注显示管线」⑧㈢）。
    /// </summary>
    public void Begin(AnnotationShape startShape, AnnotationEditTarget target, PointF start)
    {
        _startShape = startShape;
        Target = target;
        _start = start;
        _lastApplied = null;
    }

    /// <summary>
    /// 拖动中的一帧：算出结果、只写图层预览。首次越过位移阈值时才开一次历史会话
    /// （截图侧＝压一条撤销快照；贴图侧的历史是"改完再提交"，那里是空实现）。
    /// </summary>
    public void Apply(PointF current)
    {
        if (_startShape == null) return;
        AnnotationShape? live = _resolve(_startShape.Id);
        if (live == null) return;   // 图元已被撤销／删除，这段拖动作废

        float dx = current.X - _start.X;
        float dy = current.Y - _start.Y;
        if (_lastApplied == null && (Math.Abs(dx) >= DisplaceThresholdPx || Math.Abs(dy) >= DisplaceThresholdPx))
            _beginSession();

        AnnotationShape? next = AnnotationGeometry.EditShape(_startShape, live, Target, _start, current,
            _spotlightBounds());
        if (next == null) return;
        _lastApplied = next;
        _setPreview(next);
    }

    /// <summary>
    /// 松手收尾：确有位移才把最后一次算出的图元一次性写回文档并返回 true（贴图侧据此提交历史），
    /// 零位移的纯点选什么都不写。无论有无位移都清状态并抹掉预览。
    /// </summary>
    public bool Commit()
    {
        AnnotationShape? applied = _lastApplied;
        Clear();
        if (applied == null) return false;
        _replaceShape(applied);
        return true;
    }

    /// <summary>丢掉这一段手势：不写文档、抹掉预览（取消与窗口失焦时用）。</summary>
    public void Cancel() => Clear();

    void Clear()
    {
        _startShape = null;
        _lastApplied = null;
        Target = default;
        _setPreview(null);
    }
}
