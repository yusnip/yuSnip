using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using ScreenCaptureTool.Core.Capture.Annotations;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 标注渲染层。坐标输入使用截图/屏幕物理像素，渲染时换算为窗口 DIP。
///
/// 阶段 11：增加 PreviewShape，用于当前正在绘制的画笔/箭头/矩形实时预览。
/// PreviewShape 不进入正式 AnnotationDocument，不参与撤销历史，避免鼠标移动时反复提交文档。
/// </summary>
public sealed class AnnotationLayer : Control
{
    private AnnotationDocument? _document;
    private AnnotationShape? _previewShape;
    private IReadOnlyList<DrawingRectangleF> _autoMosaicHighlights = Array.Empty<DrawingRectangleF>();
    private readonly Dictionary<int, Bitmap> _mosaicDownscaledCache = new Dictionary<int, Bitmap>();
    private readonly Dictionary<int, Bitmap> _blurDownscaledCache = new Dictionary<int, Bitmap>();
    private DrawingBitmap? _sourceBitmap;

    public DrawingBitmap? SourceBitmap
    {
        get => _sourceBitmap;
        set
        {
            if (ReferenceEquals(_sourceBitmap, value)) return;
            _sourceBitmap = value;
            ClearEffectCaches();
            InvalidateVisual();
        }
    }

    public DrawingRectangle SourceScreenRect { get; set; }

    public DrawingRectangleF? SpotlightBoundsScreenRect { get; set; }

    public Guid? SelectedShapeId { get; set; }

    public DrawingRectangleF? HoverAutoMosaicHighlight { get; set; }

    public AnnotationDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document != null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document != null) _document.Changed += OnDocumentChanged;
            InvalidateVisual();
        }
    }

    public AnnotationShape? PreviewShape
    {
        get => _previewShape;
        set
        {
            _previewShape = value?.Clone();
            InvalidateVisual();
        }
    }

    public PixelPoint ScreenOrigin { get; set; }

    public double ScreenToDipScale { get; set; } = 1.0;

    public void SetAutoMosaicHighlights(IReadOnlyList<DrawingRectangleF>? highlights)
    {
        _autoMosaicHighlights = highlights ?? Array.Empty<DrawingRectangleF>();
        InvalidateVisual();
    }

    public void ClearAutoMosaicHighlights()
    {
        SetAutoMosaicHighlights(null);
        HoverAutoMosaicHighlight = null;
    }

    public void SetPreviewShape(AnnotationShape? shape)
    {
        PreviewShape = shape;
    }

    public void ClearPreviewShape()
    {
        PreviewShape = null;
    }

    private void ClearEffectCaches()
    {
        foreach (Bitmap bitmap in _mosaicDownscaledCache.Values)
        {
            try { bitmap.Dispose(); } catch { }
        }
        _mosaicDownscaledCache.Clear();

        foreach (Bitmap bitmap in _blurDownscaledCache.Values)
        {
            try { bitmap.Dispose(); } catch { }
        }
        _blurDownscaledCache.Clear();

    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        AnnotationDocument? document = Document;
        var spotlights = new List<SpotlightShape>();
        var foregroundShapes = new List<AnnotationShape>();
        if (document != null && document.Count > 0)
        {
            foreach (AnnotationShape shape in document.Shapes)
            {
                if (_previewShape is BlurShape previewBlur && shape is BlurShape && shape.Id == previewBlur.Id)
                {
                    continue;
                }
                if (_previewShape is SpotlightShape previewSpotlight && shape is SpotlightShape && shape.Id == previewSpotlight.Id)
                {
                    continue;
                }
                if (shape is BlurShape blur)
                {
                    DrawBlurPreview(context, blur);
                    continue;
                }
                if (shape is SpotlightShape spotlight)
                {
                    spotlights.Add(spotlight);
                    continue;
                }
                foregroundShapes.Add(shape);
            }
        }

        if (_previewShape != null)
        {
            if (_previewShape is BlurShape previewBlur)
            {
                bool previewsExistingLayer = document?.Shapes.Any(shape => shape.Id == previewBlur.Id) == true;
                if (previewsExistingLayer)
                {
                    DrawBlurPreview(context, previewBlur);
                }
                else
                {
                    DrawLightweightBlurPreview(context, previewBlur);
                }
            }
            else if (_previewShape is SpotlightShape previewSpotlight)
            {
                spotlights.Add(previewSpotlight);
            }
            else
            {
                foregroundShapes.Add(_previewShape);
            }
        }

        DrawSpotlightMask(context, spotlights);
        DrawSpotlightBorders(context, spotlights);
        foreach (AnnotationShape shape in foregroundShapes)
        {
            DrawShape(context, shape);
        }
        DrawAutoMosaicHighlights(context);
        DrawSelectedShapeHandles(context);
    }

    private void DrawShape(DrawingContext context, AnnotationShape shape)
    {
        if (shape is StrokeShape stroke)
        {
            DrawStroke(context, stroke);
        }
        else if (shape is ArrowShape arrow)
        {
            DrawArrow(context, arrow);
        }
        else if (shape is RectangleShape rect)
        {
            DrawAnnotationRectangle(context, rect);
        }
        else if (shape is BlurShape blur)
        {
            DrawBlurPreview(context, blur);
        }
        else if (shape is SpotlightShape spotlight)
        {
            DrawSpotlightMask(context, new[] { spotlight });
            DrawSpotlightBorder(context, spotlight);
        }
        else if (shape is CounterShape counter)
        {
            DrawCounter(context, counter);
        }
    }

    private void DrawStroke(DrawingContext context, StrokeShape stroke)
    {
        if (stroke.Points.Count < 2) return;

        var brush = new SolidColorBrush(Color.FromArgb(stroke.Color.A, stroke.Color.R, stroke.Color.G, stroke.Color.B));
        double thickness = Math.Max(1.0, stroke.Thickness * ScreenToDipScale);
        var pen = CreateLinePen(brush, thickness, AnnotationLineStyle.Solid);

        Point[] points = new Point[stroke.Points.Count];
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            points[i] = ToDip(stroke.Points[i]);
        }

        if (stroke.LineStyle == AnnotationLineStyle.DashLarge)
        {
            DrawDashedPolyline(context, pen, points, Math.Max(10.0, thickness * 3.2), Math.Max(7.0, thickness * 2.2));
            return;
        }

        for (int i = 1; i < points.Length; i++)
        {
            context.DrawLine(pen, points[i - 1], points[i]);
        }
    }

    private static void DrawDashedPolyline(DrawingContext context, Pen pen, Point[] points, double dashLength, double gapLength)
    {
        if (points.Length < 2) return;

        bool drawing = true;
        double remaining = dashLength;
        Point current = points[0];

        for (int i = 1; i < points.Length; i++)
        {
            Point target = points[i];
            double dx = target.X - current.X;
            double dy = target.Y - current.Y;
            double segmentLength = Math.Sqrt(dx * dx + dy * dy);
            if (segmentLength <= 0.01)
            {
                current = target;
                continue;
            }

            double ux = dx / segmentLength;
            double uy = dy / segmentLength;
            double consumed = 0.0;
            Point segmentStart = current;

            while (consumed < segmentLength)
            {
                double step = Math.Min(remaining, segmentLength - consumed);
                Point segmentEnd = new Point(segmentStart.X + ux * step, segmentStart.Y + uy * step);
                if (drawing)
                {
                    context.DrawLine(pen, segmentStart, segmentEnd);
                }

                consumed += step;
                segmentStart = segmentEnd;
                remaining -= step;

                if (remaining <= 0.01)
                {
                    drawing = !drawing;
                    remaining = drawing ? dashLength : gapLength;
                }
            }

            current = target;
        }
    }

    private void DrawArrow(DrawingContext context, ArrowShape arrow)
    {
        Point start = ToDip(arrow.Start);
        Point end = ToDip(arrow.End);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0) return;

        var brush = new SolidColorBrush(Color.FromArgb(arrow.Color.A, arrow.Color.R, arrow.Color.G, arrow.Color.B));
        double thickness = Math.Clamp(Math.Max(1.0, (double)arrow.Thickness), 1.0, 45.0);
        LegacyArrowGeometry arrowGeometry = CreateLegacyArrowGeometry(start, end, thickness, arrow.Style, arrow.Scale);
        context.DrawGeometry(brush, null, arrowGeometry.Shaft);
        context.DrawGeometry(brush, null, arrowGeometry.Head);
    }

    private void DrawAnnotationRectangle(DrawingContext context, RectangleShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var brush = new SolidColorBrush(Color.FromArgb(shape.StrokeColor.A, shape.StrokeColor.R, shape.StrokeColor.G, shape.StrokeColor.B));
        var pen = CreateLinePen(brush, Math.Max(1.0, shape.StrokeThickness * ScreenToDipScale), shape.LineStyle);
        if (shape.ShapeKind == ShapeAnnotationKind.Ellipse)
        {
            context.DrawEllipse(null, pen, rect.Center, rect.Width / 2.0, rect.Height / 2.0);
        }
        else
        {
            context.DrawRectangle(null, pen, rect);
        }
    }

    private static Pen CreateLinePen(IBrush brush, double thickness, AnnotationLineStyle lineStyle)
    {
        var pen = new Pen(brush, thickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (lineStyle == AnnotationLineStyle.DashLarge)
        {
            pen.DashStyle = new DashStyle(new double[] { 4.0, 3.0 }, 0);
        }

        return pen;
    }

    private sealed class LegacyArrowGeometry
    {
        public LegacyArrowGeometry(Geometry shaft, Geometry head)
        {
            Shaft = shaft;
            Head = head;
        }

        public Geometry Shaft { get; }

        public Geometry Head { get; }
    }

    private static LegacyArrowGeometry CreateLegacyArrowGeometry(Point start, Point end, double thickness, ArrowAnnotationStyle style, float scale = 1.0f)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0)
        {
            return new LegacyArrowGeometry(new StreamGeometry(), new StreamGeometry());
        }

        double ux = dx / len;
        double uy = dy / len;
        double nx = -uy;
        double ny = ux;

        // 旧版 WPF 模板：箭身使用 0..100 的模板坐标横向拉伸，模板高度 50；
        // 箭头头部固定 50×50，左边距 -25，尖端正好落在绘制终点。
        // visualScale = sqrt(scale) 对齐旧版 UpdateArrowView 的缩放行为
        double visualScale = Math.Sqrt(Math.Max(scale, 0.01f));
        double templateHeight = 50.0 * visualScale;
        double headSize = 50.0 * visualScale;
        double headOverlap = 25.0 * visualScale;
        double shaftSlotWidth = Math.Max(1.0, len - headOverlap);
        double shaftXScale = shaftSlotWidth / 100.0;
        double shaftYScale = templateHeight / 100.0;
        double headX = len - headSize;
        double headScale = headSize / 100.0;

        // 版本转换：新版Thickness直接使用旧版的模板坐标系值（1~45）
        // 旧版默认 ShaftThickness = 15，对应新版默认 thickness = 15
        double mappedThickness = Math.Clamp(thickness, 0.5, 45.0);
        double centerY = 50.0;
        double ctrlYTop = centerY - mappedThickness;
        double ctrlYBottom = centerY + mappedThickness;

        Point T(double x, double y)
        {
            double localX = x * shaftXScale;
            double localY = (y - 50.0) * shaftYScale;
            return new Point(start.X + ux * localX + nx * localY, start.Y + uy * localX + ny * localY);
        }

        Point H(double x, double y)
        {
            double localX = headX + x * headScale;
            double localY = (y - 50.0) * headScale;
            return new Point(start.X + ux * localX + nx * localY, start.Y + uy * localX + ny * localY);
        }

        var shaft = new StreamGeometry();
        using (var ctx = shaft.Open())
        {
            if (style == ArrowAnnotationStyle.Sharp)
            {
                ctx.BeginFigure(T(0, 40), true);
                ctx.QuadraticBezierTo(T(50, ctrlYTop), T(100, 35));
                ctx.LineTo(T(100, 65));
                ctx.QuadraticBezierTo(T(50, ctrlYBottom), T(0, 60));
            }
            else
            {
                ctx.BeginFigure(T(0, 50), true);
                ctx.QuadraticBezierTo(T(50, ctrlYTop), T(100, 35));
                ctx.LineTo(T(100, 65));
                ctx.QuadraticBezierTo(T(50, ctrlYBottom), T(0, 50));
            }
            ctx.EndFigure(true);
        }

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            if (style == ArrowAnnotationStyle.Sharp)
            {
                ctx.BeginFigure(H(20, 20), true);
                ctx.LineTo(H(100, 50));
                ctx.LineTo(H(20, 80));
            }
            else
            {
                ctx.BeginFigure(H(50, 50), true);
                ctx.LineTo(H(20, 20));
                ctx.LineTo(H(100, 50));
                ctx.LineTo(H(20, 80));
            }
            ctx.EndFigure(true);
        }

        return new LegacyArrowGeometry(shaft, head);
    }

    private void DrawLightweightBlurPreview(DrawingContext context, BlurShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var fill = new SolidColorBrush(Color.FromArgb(32, 76, 194, 255));
        var stroke = new SolidColorBrush(Color.FromArgb(220, 76, 194, 255));
        var pen = new Pen(stroke, 1.5);
        context.DrawRectangle(fill, pen, rect, 2, 2);
    }

    private void DrawBlurPreview(DrawingContext context, BlurShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (shape.UiLevel <= 0)
        {
            return;
        }

        if (shape.Mode == BlurMode.Mosaic)
        {
            DrawMosaicPreview(context, shape);
            return;
        }

        DrawBlurApproxPreview(context, shape);
    }

    private void DrawMosaicPreview(DrawingContext context, BlurShape shape)
    {
        DrawingBitmap? source = SourceBitmap;
        if (source == null || source.Width <= 0 || source.Height <= 0)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Mosaic");
            return;
        }

        int block = Math.Max(2, shape.MosaicBlockSize);
        Bitmap? downscaled = GetOrBuildMosaicDownscaled(source, block);
        if (downscaled == null)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Mosaic");
            return;
        }

        DrawingRectangleF r = shape.Rect;
        double localX = r.Left - SourceScreenRect.X;
        double localY = r.Top - SourceScreenRect.Y;
        double localRight = r.Right - SourceScreenRect.X;
        double localBottom = r.Bottom - SourceScreenRect.Y;
        if (localRight <= localX || localBottom <= localY) return;

        double vx = Math.Floor(localX / block);
        double vy = Math.Floor(localY / block);
        double vx2 = Math.Ceiling(localRight / block);
        double vy2 = Math.Ceiling(localBottom / block);
        double vw = Math.Max(1.0, vx2 - vx);
        double vh = Math.Max(1.0, vy2 - vy);

        if (vx < 0) { vw += vx; vx = 0; }
        if (vy < 0) { vh += vy; vy = 0; }
        if (vx + vw > downscaled.PixelSize.Width) vw = downscaled.PixelSize.Width - vx;
        if (vy + vh > downscaled.PixelSize.Height) vh = downscaled.PixelSize.Height - vy;
        if (vw <= 0.5 || vh <= 0.5) return;

        double alignedLeft = SourceScreenRect.X + vx * block;
        double alignedTop = SourceScreenRect.Y + vy * block;
        double alignedRight = SourceScreenRect.X + (vx + vw) * block;
        double alignedBottom = SourceScreenRect.Y + (vy + vh) * block;

        using (context.PushClip(ToDip(r)))
        {
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
            context.DrawImage(
                downscaled,
                new Rect(vx, vy, vw, vh),
                ToDip(DrawingRectangleF.FromLTRB((float)alignedLeft, (float)alignedTop, (float)alignedRight, (float)alignedBottom)));
        }
    }

    private Bitmap? GetOrBuildMosaicDownscaled(DrawingBitmap source, int block)
    {
        if (_mosaicDownscaledCache.TryGetValue(block, out Bitmap? cached)) return cached;

        int downW = Math.Max(1, (source.Width + block - 1) / block);
        int downH = Math.Max(1, (source.Height + block - 1) / block);
        using var down = new DrawingBitmap(downW, downH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(down))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.DrawImage(source, new System.Drawing.Rectangle(0, 0, downW, downH));
        }

        Bitmap? bitmap = ToAvaloniaBitmap(down);
        if (bitmap != null) _mosaicDownscaledCache[block] = bitmap;
        return bitmap;
    }

    private static Bitmap? ToAvaloniaBitmap(DrawingBitmap bitmap)
    {
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private void DrawBlurApproxPreview(DrawingContext context, BlurShape shape)
    {
        DrawingBitmap? source = SourceBitmap;
        if (source == null || source.Width <= 0 || source.Height <= 0)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Blur");
            return;
        }

        int scale = GetBlurPreviewScale(shape);
        Bitmap? blurredSource = GetOrBuildBlurDownscaled(source, shape, scale);
        if (blurredSource == null)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Blur");
            return;
        }

        DrawingRectangleF r = shape.Rect;
        double localX = r.Left - SourceScreenRect.X;
        double localY = r.Top - SourceScreenRect.Y;
        double localRight = r.Right - SourceScreenRect.X;
        double localBottom = r.Bottom - SourceScreenRect.Y;
        if (localRight <= localX || localBottom <= localY) return;

        double vx = Math.Floor(localX / scale);
        double vy = Math.Floor(localY / scale);
        double vx2 = Math.Ceiling(localRight / scale);
        double vy2 = Math.Ceiling(localBottom / scale);
        double vw = Math.Max(1.0, vx2 - vx);
        double vh = Math.Max(1.0, vy2 - vy);

        if (vx < 0) { vw += vx; vx = 0; }
        if (vy < 0) { vh += vy; vy = 0; }
        if (vx + vw > blurredSource.PixelSize.Width) vw = blurredSource.PixelSize.Width - vx;
        if (vy + vh > blurredSource.PixelSize.Height) vh = blurredSource.PixelSize.Height - vy;
        if (vw <= 0.5 || vh <= 0.5) return;

        double alignedLeft = SourceScreenRect.X + vx * scale;
        double alignedTop = SourceScreenRect.Y + vy * scale;
        double alignedRight = SourceScreenRect.X + (vx + vw) * scale;
        double alignedBottom = SourceScreenRect.Y + (vy + vh) * scale;

        using (context.PushClip(ToDip(r)))
        {
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
            context.DrawImage(
                blurredSource,
                new Rect(vx, vy, vw, vh),
                ToDip(DrawingRectangleF.FromLTRB((float)alignedLeft, (float)alignedTop, (float)alignedRight, (float)alignedBottom)));
        }
    }

    private static int GetBlurPreviewScale(BlurShape shape)
    {
        int radius = Math.Max(1, (int)Math.Round(shape.Intensity));
        return Math.Clamp(radius / 2, 4, 16);
    }

    private Bitmap? GetOrBuildBlurDownscaled(DrawingBitmap source, BlurShape shape, int scale)
    {
        int key = HashCode.Combine(shape.UiLevel, scale);
        if (_blurDownscaledCache.TryGetValue(key, out Bitmap? cached)) return cached;

        int downW = Math.Max(1, (source.Width + scale - 1) / scale);
        int downH = Math.Max(1, (source.Height + scale - 1) / scale);
        using var down = new DrawingBitmap(downW, downH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(down))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.DrawImage(source, new System.Drawing.Rectangle(0, 0, downW, downH));
        }

        Bitmap? bitmap = ToAvaloniaBitmap(down);
        if (bitmap != null) _blurDownscaledCache[key] = bitmap;
        return bitmap;
    }


    private void DrawEffectFallback(DrawingContext context, Rect rect, string label)
    {
        var fill = new SolidColorBrush(Color.FromArgb(72, 51, 136, 255));
        var stroke = new SolidColorBrush(Color.FromArgb(220, 51, 136, 255));
        var pen = new Pen(stroke, 2.0);
        context.DrawRectangle(fill, pen, rect);
    }

    private void DrawSpotlightMask(DrawingContext context, IReadOnlyList<SpotlightShape> spotlights)
    {
        if (spotlights.Count == 0) return;

        DrawingRectangleF boundsScreen = SpotlightBoundsScreenRect ?? new DrawingRectangleF(SourceScreenRect.X, SourceScreenRect.Y, SourceScreenRect.Width, SourceScreenRect.Height);
        if (boundsScreen.Width <= 0 || boundsScreen.Height <= 0) return;

        byte alpha = (byte)Math.Round(Math.Clamp(spotlights[0].Darkness, 0.0f, 1.0f) * 255.0f);
        if (alpha == 0) return;

        var group = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd
        };
        group.Children.Add(new RectangleGeometry(ToDip(boundsScreen)));
        foreach (SpotlightShape shape in spotlights)
        {
            Geometry? next = CreateSpotlightGeometry(shape);
            if (next != null)
            {
                group.Children.Add(next);
            }
        }

        var fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        context.DrawGeometry(fill, null, group);
    }

    private void DrawSpotlightBorders(DrawingContext context, IReadOnlyList<SpotlightShape> spotlights)
    {
        if (spotlights.Count == 0) return;

        SpotlightShape style = spotlights.FirstOrDefault(x => x.Id == SelectedShapeId) ?? spotlights[spotlights.Count - 1];
        if (style.StrokeThickness <= 0.0f) return;

        var group = new GeometryGroup();
        foreach (SpotlightShape shape in spotlights)
        {
            Geometry? next = CreateSpotlightGeometry(shape);
            if (next != null)
            {
                group.Children.Add(next);
            }
        }

        var stroke = new SolidColorBrush(Color.FromArgb(style.StrokeColor.A, style.StrokeColor.R, style.StrokeColor.G, style.StrokeColor.B));
        var pen = new Pen(stroke, Math.Max(1.0, style.StrokeThickness * ScreenToDipScale));
        context.DrawGeometry(null, pen, group);
    }

    private void DrawSpotlightBorder(DrawingContext context, SpotlightShape shape)
    {
        if (shape.StrokeThickness <= 0.0f) return;
        Geometry? geometry = CreateSpotlightGeometry(shape);
        if (geometry == null) return;

        var stroke = new SolidColorBrush(Color.FromArgb(shape.StrokeColor.A, shape.StrokeColor.R, shape.StrokeColor.G, shape.StrokeColor.B));
        var pen = new Pen(stroke, Math.Max(1.0, shape.StrokeThickness * ScreenToDipScale));
        context.DrawGeometry(null, pen, geometry);
    }

    private Geometry? CreateSpotlightGeometry(SpotlightShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (shape.ShapeKind == SpotlightShapeKind.Ellipse)
        {
            return new EllipseGeometry(rect);
        }

        double radius = Math.Clamp(shape.CornerRadius * ScreenToDipScale, 0.0, Math.Min(rect.Width, rect.Height) / 2.0);
        return new RectangleGeometry(rect, radius, radius);
    }

    private void DrawCounter(DrawingContext context, CounterShape shape)
    {
        Point center = ToDip(shape.Center);
        double radius = Math.Max(4.0, shape.Radius * ScreenToDipScale);

        var fill = new SolidColorBrush(Color.FromArgb(shape.FillColor.A, shape.FillColor.R, shape.FillColor.G, shape.FillColor.B));
        var stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        var textBrush = new SolidColorBrush(Color.FromArgb(shape.TextColor.A, shape.TextColor.R, shape.TextColor.G, shape.TextColor.B));
        var pen = new Pen(stroke, Math.Max(1.0, 2.0 * ScreenToDipScale));

        context.DrawEllipse(fill, pen, center, radius, radius);

        var formatted = new FormattedText(
            shape.Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            Math.Max(8.0, shape.Radius * ScreenToDipScale),
            textBrush);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2.0, center.Y - formatted.Height / 2.0));
    }

    private void DrawAutoMosaicHighlights(DrawingContext context)
    {
        if (_autoMosaicHighlights.Count == 0) return;
        var stroke = new SolidColorBrush(Color.FromArgb(180, 76, 194, 255));
        var hoverStroke = new SolidColorBrush(Color.FromArgb(230, 76, 194, 255));
        foreach (DrawingRectangleF r in _autoMosaicHighlights)
        {
            Rect rect = ToDip(r);
            bool hover = HoverAutoMosaicHighlight.HasValue && NearlySameRect(HoverAutoMosaicHighlight.Value, r);
            context.DrawRectangle(null, new Pen(hover ? hoverStroke : stroke, hover ? 2.0 : 1.5), rect, 3, 3);
        }
    }

    private void DrawSelectedShapeHandles(DrawingContext context)
    {
        if (!SelectedShapeId.HasValue || Document == null) return;
        AnnotationShape? selected = _previewShape != null && _previewShape.Id == SelectedShapeId.Value
            ? _previewShape
            : Document.Shapes.FirstOrDefault(x => x.Id == SelectedShapeId.Value);

        if (selected == null) return;

        // 根据不同形状类型绘制选中状态
        switch (selected)
        {
            case BlurShape blur:
                DrawBlurSelectedHandles(context, blur);
                break;
            case SpotlightShape spotlight:
                DrawSpotlightSelectedHandles(context, spotlight);
                break;
            case ArrowShape arrow:
                DrawArrowSelectedHandles(context, arrow);
                break;
            case RectangleShape rectangle:
                DrawRectangleSelectedHandles(context, rectangle);
                break;
            case CounterShape counter:
                DrawCounterSelectedHandles(context, counter);
                break;
            case StrokeShape:
                // 画笔选中时不绘制包围虚线框，避免干扰笔迹观感。
                break;
        }
    }

    private void DrawBlurSelectedHandles(DrawingContext context, BlurShape blur)
    {
        DrawingRectangleF bounds = blur.Rect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.0);
        context.DrawRectangle(null, pen, rect);

        var fill = Brushes.White;
        double size = 10.0;
        double half = size / 2.0;
        Point[] points =
        {
            new(rect.Left, rect.Top),
            new(rect.Left + rect.Width / 2.0, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2.0),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2.0, rect.Bottom),
            new(rect.Left, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2.0),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, half, half);
        }
    }

    private void DrawSpotlightSelectedHandles(DrawingContext context, SpotlightShape spotlight)
    {
        DrawingRectangleF bounds = spotlight.Rect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        Rect selectionRect = rect.Inflate(3.0);
        var stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x88, 0xFF));
        var pen = new Pen(stroke, 1.0);
        context.DrawRectangle(null, pen, selectionRect);

        var fill = Brushes.White;
        double size = 10.0;
        double half = size / 2.0;
        Point[] points =
        {
            new(rect.Left, rect.Top),
            new(rect.Left + rect.Width / 2.0, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2.0),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2.0, rect.Bottom),
            new(rect.Left, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2.0),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, half, half);
        }

        if (spotlight.ShapeKind == SpotlightShapeKind.Rectangle)
        {
            DrawSpotlightRadiusHandles(context, rect, pen);
        }
    }

    private void DrawArrowSelectedHandles(DrawingContext context, ArrowShape arrow)
    {
        Point start = ToDip(arrow.Start);
        Point end = ToDip(arrow.End);

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.0);
        var fill = Brushes.White;
        double size = 12.0;
        double half = size / 2.0;

        // 绘制起点和终点锚点
        context.DrawEllipse(fill, pen, start, half, half);
        context.DrawEllipse(fill, pen, end, half, half);

        // 绘制连接线（虚线）
        var dashedPen = new Pen(stroke, 1.0) { DashStyle = new DashStyle(new double[] { 4.0, 4.0 }, 0) };
        context.DrawLine(dashedPen, start, end);
    }

    private void DrawRectangleSelectedHandles(DrawingContext context, RectangleShape rectangle)
    {
        DrawingRectangleF bounds = rectangle.Rect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.0);
        context.DrawRectangle(null, pen, rect);

        var fill = Brushes.White;
        double size = 10.0;
        double half = size / 2.0;
        Point[] points =
        {
            new(rect.Left, rect.Top),
            new(rect.Left + rect.Width / 2.0, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2.0),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2.0, rect.Bottom),
            new(rect.Left, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2.0),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, half, half);
        }
    }

    private void DrawCounterSelectedHandles(DrawingContext context, CounterShape counter)
    {
        Point center = ToDip(counter.Center);
        double radius = Math.Max(4.0, counter.Radius * ScreenToDipScale);

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 2.0);
        
        // 绘制选中圆圈
        context.DrawEllipse(null, pen, center, radius + 4.0, radius + 4.0);
    }

    private void DrawStrokeSelectedHandles(DrawingContext context, StrokeShape stroke)
    {
        if (stroke.Points.Count < 2) return;

        var color = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(color, 2.0) { DashStyle = new DashStyle(new double[] { 4.0, 4.0 }, 0) };

        // 绘制边界框
        float minX = stroke.Points.Min(p => p.X);
        float minY = stroke.Points.Min(p => p.Y);
        float maxX = stroke.Points.Max(p => p.X);
        float maxY = stroke.Points.Max(p => p.Y);

        Point topLeft = ToDip(new System.Drawing.PointF(minX, minY));
        Point bottomRight = ToDip(new System.Drawing.PointF(maxX, maxY));
        Rect bounds = new Rect(topLeft, bottomRight);

        context.DrawRectangle(null, pen, bounds);
    }

    private static void DrawSpotlightRadiusHandles(DrawingContext context, Rect rect, Pen pen)
    {
        double minSide = Math.Min(rect.Width, rect.Height);
        if (minSide <= 0) return;

        double padding = minSide < 40.0 ? minSide / 4.0 : 15.0;
        double radius = 6.0;
        var fill = new SolidColorBrush(Color.FromRgb(0x33, 0x88, 0xFF));
        Point[] points =
        {
            new(rect.Left + padding, rect.Top + padding),
            new(rect.Right - padding, rect.Top + padding),
            new(rect.Right - padding, rect.Bottom - padding),
            new(rect.Left + padding, rect.Bottom - padding),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, radius, radius);
        }
    }

    private static bool NearlySameRect(DrawingRectangleF a, DrawingRectangleF b)
    {
        return Math.Abs(a.Left - b.Left) < 0.5f
            && Math.Abs(a.Top - b.Top) < 0.5f
            && Math.Abs(a.Width - b.Width) < 0.5f
            && Math.Abs(a.Height - b.Height) < 0.5f;
    }

    private Rect ToDip(System.Drawing.RectangleF rect)
    {
        Point topLeft = ToDip(new System.Drawing.PointF(rect.Left, rect.Top));
        Point bottomRight = ToDip(new System.Drawing.PointF(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Point ToDip(System.Drawing.PointF point)
    {
        return new Point(
            (point.X - ScreenOrigin.X) * ScreenToDipScale,
            (point.Y - ScreenOrigin.Y) * ScreenToDipScale);
    }

    private void OnDocumentChanged()
    {
        InvalidateVisual();
    }

}
