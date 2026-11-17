using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 箭头标注。Start/End 使用截图/屏幕物理像素坐标。
/// ControlPoint 为贝塞尔曲线控制点，位于 Start 和 End 的中点时箭头为直线。
/// </summary>
public sealed class ArrowShape : AnnotationShape
{
    private const float ArrowHeadPadding = 18.0f;

    /// <summary>
    /// 向后兼容构造器：控制点默认为 Start/End 中点（直线箭头）。
    /// </summary>
    public ArrowShape(PointF start, PointF end, Color color, float thickness, ArrowAnnotationStyle style = ArrowAnnotationStyle.Designed, float scale = 1.0f, Guid id = default)
        : this(start, end, color, thickness, style, scale, id, new PointF((start.X + end.X) / 2f, (start.Y + end.Y) / 2f))
    {
    }

    /// <summary>
    /// 完整构造器，支持弯曲箭头。
    /// </summary>
    public ArrowShape(PointF start, PointF end, Color color, float thickness, ArrowAnnotationStyle style, float scale, Guid id, PointF controlPoint)
        : base(id)
    {
        Start = start;
        End = end;
        ControlPoint = controlPoint;
        Color = color;
        Thickness = Math.Max(1.0f, thickness);
        Style = style;
        Scale = Math.Clamp(scale, 0.2f, 4.0f);
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Arrow;

    public PointF Start { get; }

    public PointF End { get; }

    /// <summary>
    /// 二次贝塞尔曲线控制点。当控制点位于 Start/End 中点时箭头为直线。
    /// </summary>
    public PointF ControlPoint { get; }

    public Color Color { get; }

    public float Thickness { get; }

    /// <summary>
    /// 整体缩放倍率，范围 0.2~4.0，默认 1.0。
    /// 控制箭身高度和箭头头部大小，但不影响长度。
    /// </summary>
    public float Scale { get; }

    public ArrowAnnotationStyle Style { get; }

    public override RectangleF Bounds
    {
        get
        {
            float left = Math.Min(Math.Min(Start.X, End.X), ControlPoint.X);
            float top = Math.Min(Math.Min(Start.Y, End.Y), ControlPoint.Y);
            float right = Math.Max(Math.Max(Start.X, End.X), ControlPoint.X);
            float bottom = Math.Max(Math.Max(Start.Y, End.Y), ControlPoint.Y);
            float pad = (Thickness + ArrowHeadPadding) * Scale;
            return RectangleF.FromLTRB(left - pad, top - pad, right + pad, bottom + pad);
        }
    }

    public override AnnotationShape Clone()
        => new ArrowShape(Start, End, Color, Thickness, Style, Scale, Id, ControlPoint);

    /// <summary>换描边色，头型/缩放/弯折点原样带走。</summary>
    public ArrowShape WithColor(Color color)
        => new(Start, End, color, Thickness, Style, Scale, Id, ControlPoint);

    /// <summary>换粗细（下限 1 由构造器夹），其余原样带走。</summary>
    public ArrowShape WithThickness(float thickness)
        => new(Start, End, Color, thickness, Style, Scale, Id, ControlPoint);

    /// <summary>换箭头头型档（参数条那枚下拉的去处），其余原样带走。</summary>
    public ArrowShape WithStyle(ArrowAnnotationStyle style)
        => new(Start, End, Color, Thickness, style, Scale, Id, ControlPoint);

    /// <summary>换整体缩放倍率（滚轮那一路的去处），其余原样带走。</summary>
    public ArrowShape WithScale(float scale)
        => new(Start, End, Color, Thickness, Style, scale, Id, ControlPoint);
}
