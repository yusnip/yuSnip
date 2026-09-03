using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 聚光灯区域标注。Rect 使用截图/屏幕物理像素坐标。
/// </summary>
public sealed class SpotlightShape : AnnotationShape
{
    public SpotlightShape(
        RectangleF rect,
        float darkness,
        SpotlightShapeKind shapeKind = SpotlightShapeKind.Rectangle,
        Color? strokeColor = null,
        float strokeThickness = 3.0f,
        float cornerRadius = 0.0f,
        Guid id = default)
        : base(id)
    {
        Rect = Normalize(rect);
        Darkness = Math.Clamp(darkness, 0.0f, 1.0f);
        ShapeKind = shapeKind;
        StrokeColor = strokeColor ?? Color.FromArgb(255, 255, 214, 102);
        StrokeThickness = Math.Clamp(strokeThickness, 0.0f, 30.0f);
        CornerRadius = ClampCornerRadius(Rect, cornerRadius);
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Spotlight;

    public RectangleF Rect { get; }

    public float Darkness { get; }

    public SpotlightShapeKind ShapeKind { get; }

    public Color StrokeColor { get; }

    public float StrokeThickness { get; }

    public float CornerRadius { get; }

    public override RectangleF Bounds => Rect;

    public override AnnotationShape Clone()
    {
        return new SpotlightShape(Rect, Darkness, ShapeKind, StrokeColor, StrokeThickness, CornerRadius, Id);
    }

    public SpotlightShape WithRect(RectangleF rect)
    {
        return new SpotlightShape(rect, Darkness, ShapeKind, StrokeColor, StrokeThickness, CornerRadius, Id);
    }

    public SpotlightShape WithCornerRadius(float cornerRadius)
    {
        return new SpotlightShape(Rect, Darkness, ShapeKind, StrokeColor, StrokeThickness, cornerRadius, Id);
    }

    public SpotlightShape WithStyle(
        float darkness,
        SpotlightShapeKind shapeKind,
        Color strokeColor,
        float strokeThickness)
    {
        return new SpotlightShape(Rect, darkness, shapeKind, strokeColor, strokeThickness, CornerRadius, Id);
    }

    private static RectangleF Normalize(RectangleF rect)
    {
        float left = Math.Min(rect.Left, rect.Right);
        float top = Math.Min(rect.Top, rect.Bottom);
        float right = Math.Max(rect.Left, rect.Right);
        float bottom = Math.Max(rect.Top, rect.Bottom);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static float ClampCornerRadius(RectangleF rect, float radius)
    {
        if (float.IsNaN(radius) || float.IsInfinity(radius)) return 0.0f;
        float max = Math.Max(0.0f, Math.Min(rect.Width, rect.Height) / 2.0f);
        return Math.Clamp(radius, 0.0f, max);
    }
}
