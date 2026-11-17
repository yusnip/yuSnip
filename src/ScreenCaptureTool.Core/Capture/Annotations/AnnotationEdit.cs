using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 一次编辑手势抓到的把手种类。<see cref="Move"/>＝拖图元本体，其余都是手柄。
/// 截图遮罩与贴图标注共用这一个枚举：两侧的"按下去到底在拖什么"从此只有这一份答案表。
/// <para>⚠ 曾经的第六档 `CornerRadius`（四角圆角手柄）已整体退役（2026-09-18 先摘聚光灯、同日再摘矩形）：
/// 「矩形 + 圆角」这一族的圆角如今只有**元素上方浮出的圆角浮条**一个入口
/// （<c>Controls/SpotlightRadiusPill</c>），不经"抓着手柄拖位移"换算，故这里不该再有圆角档。
/// 别因为"矩形本来就带一个半径"就把手柄接回来。</para>
/// </summary>
public enum AnnotationEditKind
{
    None,
    Move,
    Resize,
    ArrowStart,
    ArrowEnd,
    ArrowBend,
    /// <summary>序号引线的端点：拖它改指向。只有带引线的序号被选中时才有这一枚手柄。</summary>
    CounterTail,
    /// <summary>
    /// 序号引线的中间弯折点：拖它把引线拉弯，与箭头的 <see cref="ArrowBend"/> 是同一件事、
    /// 同一枚手柄长相（蓝底白边大点）。同样只有带引线的序号被选中时才有。
    /// </summary>
    CounterTailBend,
}

/// <summary>
/// "点到了某个已存在图元的哪个把手"的唯一结论。产出方是同文件的
/// <see cref="AnnotationHitTest.HitTestEditTarget(AnnotationShape, PointF, float, bool)"/>，
/// 消费方是 <see cref="AnnotationGeometry.EditShape"/>；宿主只决定"什么情况下允许抓手柄"，
/// 不再各自存一份把手表。<see cref="AnnotationHitTest"/> 回答"点到了什么"，
/// <see cref="AnnotationGeometry"/> 回答"变成什么"，本类型就是两者之间传递的那句话。
/// </summary>
public readonly struct AnnotationEditTarget
{
    public AnnotationEditTarget(AnnotationEditKind kind, HandleDirection direction = HandleDirection.None)
    {
        Kind = kind;
        Direction = direction;
    }

    public AnnotationEditKind Kind { get; }

    /// <summary>仅 <see cref="AnnotationEditKind.Resize"/> 有意义：8 向中的哪一向。</summary>
    public HandleDirection Direction { get; }

    public bool IsNone => Kind == AnnotationEditKind.None;

    /// <summary>抓到的是手柄还是本体。悬停光标靠它决定"给不给方向箭头"。</summary>
    public bool IsHandle => Kind is not (AnnotationEditKind.None or AnnotationEditKind.Move);

    /// <summary>本体或手柄都算"能起一段拖动手势"。</summary>
    public bool IsEditable => Kind != AnnotationEditKind.None;

    public static AnnotationEditTarget Resize(HandleDirection direction)
        => new(AnnotationEditKind.Resize, direction);

    /// <summary>
    /// 悬停光标的方向档：把"抓到哪个把手"归成横、竖、两条斜线四类。截图与贴图共用这一份映射，
    /// 同一根手柄在两处必须给同一个方向。具体光标图形由宿主换（Core 不引用 Avalonia）。
    /// </summary>
    public static CursorDirection? CursorDirectionOf(HandleDirection direction) => direction switch
    {
        HandleDirection.Top or HandleDirection.Bottom => CursorDirection.NorthSouth,
        HandleDirection.Left or HandleDirection.Right => CursorDirection.WestEast,
        HandleDirection.TopLeft or HandleDirection.BottomRight => CursorDirection.NorthWestSouthEast,
        HandleDirection.TopRight or HandleDirection.BottomLeft => CursorDirection.NorthEastSouthWest,
        _ => null,
    };
}

/// <summary>手柄悬停光标方向档，由宿主翻成各自的 <c>Avalonia.Input.StandardCursorType</c>。</summary>
public enum CursorDirection
{
    NorthSouth,
    WestEast,
    NorthWestSouthEast,
    NorthEastSouthWest,
}
