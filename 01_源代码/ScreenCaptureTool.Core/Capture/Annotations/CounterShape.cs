using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 编号标注。Center 使用截图/屏幕物理像素坐标。
/// </summary>
public sealed class CounterShape : AnnotationShape
{
    public CounterShape(PointF center, int number, float radius, Color fillColor, Color textColor, Guid id = default)
        : base(id)
    {
        Center = center;
        Number = Math.Max(1, number);
        Radius = Math.Max(8.0f, radius);
        FillColor = fillColor;
        TextColor = textColor;
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Counter;

    public PointF Center { get; }

    public int Number { get; }

    public float Radius { get; }

    public Color FillColor { get; }

    public Color TextColor { get; }

    public override RectangleF Bounds => new RectangleF(
        Center.X - Radius,
        Center.Y - Radius,
        Radius * 2.0f,
        Radius * 2.0f);

    public override AnnotationShape Clone()
    {
        return new CounterShape(Center, Number, Radius, FillColor, TextColor, Id);
    }
}
