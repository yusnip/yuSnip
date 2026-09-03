using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 矩形标注。Rect 使用截图/屏幕物理像素坐标。
/// </summary>
public sealed class RectangleShape : AnnotationShape
{
    public RectangleShape(RectangleF rect, Color strokeColor, float strokeThickness, ShapeAnnotationKind shapeKind = ShapeAnnotationKind.Rectangle, AnnotationLineStyle lineStyle = AnnotationLineStyle.Solid, Guid id = default)
        : base(id)
    {
        Rect = Normalize(rect);
        StrokeColor = strokeColor;
        StrokeThickness = Math.Max(1.0f, strokeThickness);
        ShapeKind = shapeKind;
        LineStyle = lineStyle;
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Rectangle;

    public RectangleF Rect { get; }

    public Color StrokeColor { get; }

    public float StrokeThickness { get; }

    public ShapeAnnotationKind ShapeKind { get; }

    public AnnotationLineStyle LineStyle { get; }

    public override RectangleF Bounds
    {
        get
        {
            float pad = StrokeThickness / 2.0f;
            return RectangleF.FromLTRB(Rect.Left - pad, Rect.Top - pad, Rect.Right + pad, Rect.Bottom + pad);
        }
    }

    public override AnnotationShape Clone()
    {
        return new RectangleShape(Rect, StrokeColor, StrokeThickness, ShapeKind, LineStyle, Id);
    }

    private static RectangleF Normalize(RectangleF rect)
    {
        float left = Math.Min(rect.Left, rect.Right);
        float top = Math.Min(rect.Top, rect.Bottom);
        float right = Math.Max(rect.Left, rect.Right);
        float bottom = Math.Max(rect.Top, rect.Bottom);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }
}
