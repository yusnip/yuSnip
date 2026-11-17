using System.Drawing;
using System.Drawing.Drawing2D;
using ScreenCaptureTool.Core.Capture.Annotations;
using SkiaSharp;

namespace ScreenCaptureTool.Services;

/// <summary>聚光灯成品栅格化：沿用既有路径及合并判据，使用与预览同族的 Skia 覆盖率抗锯齿。</summary>
internal static class SpotlightBitmapRenderer
{
    private const int TileSize = AnnotationCoverageTile.Size;
    private const int SampleScale = AnnotationCoverageTile.Scale;
    private const int TilePadding = AnnotationCoverageTile.Padding;

    internal static void Render(Bitmap target, IReadOnlyList<SpotlightShape> shapes,
        Func<SpotlightShape, float, GraphicsPath?> createPath)
    {
        if (shapes.Count == 0) return;
        var layers = new List<(SKPath Path, SKColor Color)>();
        var holePaths = new List<SKPath>();
        var borderPaths = new List<(SKPath Outer, SKPath? Inner)>();
        try
        {
            using var holes = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var shape in shapes)
            {
                var hole = Create(shape, 0, createPath);
                if (hole != null) { holePaths.Add(hole); holes.AddPath(hole); }
            }
            if (holes.IsEmpty) return;
            byte alpha = (byte)Math.Round(Math.Clamp(shapes[0].Darkness, 0, 1) * 255);
            if (alpha > 0)
            {
                using var canvas = new SKPath();
                canvas.AddRect(new SKRect(0, 0, target.Width, target.Height));
                layers.Add((Combine(canvas, holes, SKPathOp.Difference), new SKColor(0, 0, 0, alpha)));
            }
            if (shapes[^1].StrokeThickness > 0)
            foreach (var shape in shapes)
            {
                if (shape.StrokeThickness <= 0) continue;
                float half = Math.Max(1, shape.StrokeThickness) / 2;
                using var outer = Create(shape, half, createPath);
                if (outer == null) continue;
                using var inner = Create(shape, -half, createPath);
                borderPaths.Add((new SKPath(outer), inner == null ? null : new SKPath(inner)));
                using var ring = inner == null ? new SKPath(outer) : Combine(outer, inner, SKPathOp.Difference);
                using var others = new SKPath { FillType = SKPathFillType.Winding };
                foreach (var other in SpotlightOutlineMerge.ClipSources(shapes, shape))
                {
                    using var hole = Create(other, 0, createPath);
                    if (hole != null) others.AddPath(hole);
                }
                var path = others.IsEmpty ? new SKPath(ring) : Combine(ring, others, SKPathOp.Difference);
                var color = shape.StrokeColor;
                layers.Add((path, new SKColor(color.R, color.G, color.B, color.A)));
            }
            if (layers.Count == 0) return;

            // 只栅格化标注覆盖层，不缩放底图；大长图也仅复用一块约 1 MiB 的临时像素缓冲。
            using var tile = new AnnotationCoverageTile();
            using var raster = new SKCanvas(tile.Samples);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.SrcOver };
            using var graphics = Graphics.FromImage(target);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.AssumeLinear;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            using var dark = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            for (int y = 0; y < target.Height; y += TileSize)
            for (int x = 0; x < target.Width; x += TileSize)
            {
                // 原洞口为凸路径。四角全在同一个洞内即可证明整块无暗幕；边框内缘也按同样方式保守判定。
                // 检查范围额外外扩 1px，边界覆盖率仍交给采样，不能用快速路径截断抗锯齿。
                var bounds = new SKRect(x - 1, y - 1, x + TileSize + 1, y + TileSize + 1);
                bool border = borderPaths.Any(p => Intersects(p.Outer.Bounds, bounds) && (p.Inner == null || !Contains(p.Inner, bounds)));
                if (!border)
                {
                    if (holePaths.Any(p => Contains(p, bounds))) continue;
                    if (holePaths.All(p => !Intersects(p.Bounds, bounds)))
                    {
                        if (alpha > 0) graphics.FillRectangle(dark, x, y, TileSize, TileSize);
                        continue;
                    }
                }
                raster.Clear(SKColors.Transparent);
                raster.Save(); raster.Scale(SampleScale); raster.Translate(-x + TilePadding, -y + TilePadding);
                foreach (var layer in layers)
                {
                    paint.Color = layer.Color;
                    raster.DrawPath(layer.Path, paint);
                }
                raster.Restore(); raster.Flush();
                tile.Resolve();
                graphics.DrawImageUnscaled(tile.Bitmap, x, y);
            }
        }
        finally
        {
            foreach (var layer in layers) layer.Path.Dispose();
            foreach (var hole in holePaths) hole.Dispose();
            foreach (var border in borderPaths) { border.Outer.Dispose(); border.Inner?.Dispose(); }
        }
    }

    private static bool Intersects(SKRect a, SKRect b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    private static bool Contains(SKPath path, SKRect rect)
        => path.IsConvex && path.Contains(rect.Left, rect.Top) && path.Contains(rect.Right, rect.Top)
            && path.Contains(rect.Right, rect.Bottom) && path.Contains(rect.Left, rect.Bottom);

    private static SKPath Combine(SKPath a, SKPath b, SKPathOp operation)
    {
        var result = new SKPath();
        if (a.Op(b, operation, result)) return result;
        result.Dispose();
        throw new InvalidOperationException("聚光灯路径合并失败。");
    }

    private static SKPath? Create(SpotlightShape shape, float inflate,
        Func<SpotlightShape, float, GraphicsPath?> createPath)
    {
        using var source = createPath(shape, inflate);
        if (source == null || source.PointCount == 0) return null;
        // 原路径只在栅格化前按固定亚像素容差展开，避免 Skia 按每块裁切范围分别细分圆弧而留下接缝。
        // 不另算半径、裁切或外扩／内缩；容差远小于一个输出像素，不改变存档几何。
        source.Flatten(null, 1.0f / 64.0f);
        var points = source.PathPoints; var types = source.PathTypes;
        var path = new SKPath { FillType = source.FillMode == FillMode.Winding ? SKPathFillType.Winding : SKPathFillType.EvenOdd };
        for (int i = 0; i < points.Length; i++)
        {
            int type = types[i] & (byte)PathPointType.PathTypeMask;
            if (type == (byte)PathPointType.Start) path.MoveTo(points[i].X, points[i].Y);
            else if (type == (byte)PathPointType.Line) path.LineTo(points[i].X, points[i].Y);
            else if (type == (byte)PathPointType.Bezier3)
            {
                path.CubicTo(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, points[i + 2].X, points[i + 2].Y);
                i += 2;
            }
            if ((types[i] & (byte)PathPointType.CloseSubpath) != 0) path.Close();
        }
        return path;
    }
}
