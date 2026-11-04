using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 矩形标注。Rect 使用截图/屏幕物理像素坐标。
/// </summary>
public sealed class RectangleShape : AnnotationShape
{
    public RectangleShape(RectangleF rect, Color strokeColor, float strokeThickness, ShapeAnnotationKind shapeKind = ShapeAnnotationKind.Rectangle, AnnotationLineStyle lineStyle = AnnotationLineStyle.Solid, float cornerRadius = 0.0f, float fillOpacity = 0.0f, Guid id = default)
        : base(id)
    {
        Rect = Normalize(rect);
        StrokeColor = strokeColor;
        StrokeThickness = Math.Max(1.0f, strokeThickness);
        ShapeKind = shapeKind;
        LineStyle = lineStyle;
        CornerRadius = ClampCornerRadius(Rect, cornerRadius);
        FillOpacity = Math.Clamp(fillOpacity, 0.0f, 1.0f);
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Rectangle;

    public RectangleF Rect { get; }

    public Color StrokeColor { get; }

    public float StrokeThickness { get; }

    public ShapeAnnotationKind ShapeKind { get; }

    public AnnotationLineStyle LineStyle { get; }

    /// <summary>
    /// 圆角半径，单位与 <see cref="Rect"/> 同坐标系（截图＝屏幕物理像素，贴图＝内容像素）。
    /// 只在 <see cref="ShapeAnnotationKind.Rectangle"/> 下生效；椭圆档忽略。上限＝短边一半，故不会自交。
    /// </summary>
    public float CornerRadius { get; }

    /// <summary>
    /// 填充不透明度 0～1；0＝不填充。填充色不单列字段——一律取 <see cref="StrokeColor"/> 的 RGB，
    /// 只按本值算 alpha，故"勾选填充"永远与线条同色，也就不存在两份会漂移的配色。
    /// </summary>
    public float FillOpacity { get; }

    public override RectangleF Bounds
    {
        get
        {
            float pad = StrokeThickness / 2.0f;
            return RectangleF.FromLTRB(Rect.Left - pad, Rect.Top - pad, Rect.Right + pad, Rect.Bottom + pad);
        }
    }

    public override AnnotationShape Clone() => Build(Rect, StrokeColor, StrokeThickness, ShapeKind,
        LineStyle, CornerRadius, FillOpacity, Id);

    /// <summary>换圆角半径，其余属性原样带走（与 <see cref="SpotlightShape.WithCornerRadius"/> 同口径）。</summary>
    public RectangleShape WithCornerRadius(float cornerRadius) => Build(Rect, StrokeColor, StrokeThickness,
        ShapeKind, LineStyle, cornerRadius, FillOpacity, Id);

    /// <summary>换填充不透明度，其余属性原样带走（工具栏勾选/滑条回灌选中图元用）。</summary>
    public RectangleShape WithFill(float fillOpacity) => Build(Rect, StrokeColor, StrokeThickness,
        ShapeKind, LineStyle, CornerRadius, fillOpacity, Id);

    /// <summary>换描边色（参数条取色那一路），填充色按既有口径跟着走（它恒取本色的 RGB）。</summary>
    public RectangleShape WithColor(Color strokeColor) => Build(Rect, strokeColor, StrokeThickness,
        ShapeKind, LineStyle, CornerRadius, FillOpacity, Id);

    /// <summary>换描边粗细，其余属性原样带走。</summary>
    public RectangleShape WithThickness(float strokeThickness) => Build(Rect, StrokeColor, strokeThickness,
        ShapeKind, LineStyle, CornerRadius, FillOpacity, Id);

    /// <summary>换线型（实线／虚线那一格），形状档与圆角填充一律不动。</summary>
    public RectangleShape WithLineStyle(AnnotationLineStyle lineStyle) => Build(Rect, StrokeColor, StrokeThickness,
        ShapeKind, lineStyle, CornerRadius, FillOpacity, Id);

    /// <summary>换形状档（矩形／椭圆）。圆角字段原样留着：换回矩形时用户拖出来的圆角还在。</summary>
    public RectangleShape WithShapeKind(ShapeAnnotationKind shapeKind) => Build(Rect, StrokeColor, StrokeThickness,
        shapeKind, LineStyle, CornerRadius, FillOpacity, Id);

    /// <summary>唯一的构造出口：八参数全带默认值的构造器漏传哪一档就被静默抹回哪一档，故一律走这里。</summary>
    static RectangleShape Build(RectangleF rect, Color strokeColor, float strokeThickness,
        ShapeAnnotationKind shapeKind, AnnotationLineStyle lineStyle, float cornerRadius,
        float fillOpacity, Guid id)
        => new(rect, strokeColor, strokeThickness, shapeKind, lineStyle, cornerRadius, fillOpacity, id);

    private static float ClampCornerRadius(RectangleF rect, float radius)
    {
        if (float.IsNaN(radius) || float.IsInfinity(radius)) return 0.0f;
        float max = Math.Max(0.0f, Math.Min(rect.Width, rect.Height) / 2.0f);
        return Math.Clamp(radius, 0.0f, max);
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
