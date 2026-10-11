using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 自由画笔路径。为 T6.1 BrushTool 做准备。
/// </summary>
public sealed class StrokeShape : AnnotationShape
{
    private readonly List<PointF> _points;

    public StrokeShape(IEnumerable<PointF> points, Color color, float thickness, AnnotationLineStyle lineStyle = AnnotationLineStyle.Solid, Guid id = default)
        : base(id)
    {
        _points = points?.ToList() ?? new List<PointF>();
        Color = color;
        Thickness = Math.Max(1.0f, thickness);
        LineStyle = lineStyle;
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Stroke;

    public IReadOnlyList<PointF> Points => _points;

    public Color Color { get; }

    public float Thickness { get; }

    public AnnotationLineStyle LineStyle { get; }

    public override RectangleF Bounds
    {
        get
        {
            if (_points.Count == 0) return RectangleF.Empty;

            float minX = _points[0].X;
            float minY = _points[0].Y;
            float maxX = _points[0].X;
            float maxY = _points[0].Y;

            for (int i = 1; i < _points.Count; i++)
            {
                PointF p = _points[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            float pad = Thickness / 2.0f;
            return RectangleF.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
        }
    }

    public override AnnotationShape Clone()
    {
        return new StrokeShape(_points.ToArray(), Color, Thickness, LineStyle, Id);
    }
}
