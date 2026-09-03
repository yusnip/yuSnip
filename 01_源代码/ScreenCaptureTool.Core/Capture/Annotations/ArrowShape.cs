using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 箭头标注。Start/End 使用截图/屏幕物理像素坐标。
/// </summary>
public sealed class ArrowShape : AnnotationShape
{
    private const float ArrowHeadPadding = 18.0f;

    public ArrowShape(PointF start, PointF end, Color color, float thickness, ArrowAnnotationStyle style = ArrowAnnotationStyle.Designed, float scale = 1.0f, Guid id = default)
        : base(id)
    {
        Start = start;
        End = end;
        Color = color;
        Thickness = Math.Max(1.0f, thickness);
        Style = style;
        Scale = Math.Clamp(scale, 0.2f, 4.0f);
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Arrow;

    public PointF Start { get; }

    public PointF End { get; }

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
            float left = Math.Min(Start.X, End.X);
            float top = Math.Min(Start.Y, End.Y);
            float right = Math.Max(Start.X, End.X);
            float bottom = Math.Max(Start.Y, End.Y);
            float pad = (Thickness + ArrowHeadPadding) * Scale;
            return RectangleF.FromLTRB(left - pad, top - pad, right + pad, bottom + pad);
        }
    }

    public override AnnotationShape Clone()
    {
        return new ArrowShape(Start, End, Color, Thickness, Style, Scale, Id);
    }
}
