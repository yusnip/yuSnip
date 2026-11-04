using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 序号图元的 Avalonia 侧绘制，截图预览图层与工具栏上的样式小样共用这一份。
/// <para>为什么单独收出来：小样要"所见即所得"，另写一套画法迟早和预览对不上（同一颗号在条上看着
/// 是一个样、画在图上又是另一个样）。这里只吃 <see cref="AnnotationRenderOptions"/> 的几何算式，
/// 坐标换算由调用方给，预览层传物理像素→DIP 的映射，小样直接按 DIP 画。</para>
/// </summary>
internal static class CounterPainter
{
    /// <param name="s">内容像素→DIP 的比例尺（小样恒为 1）。</param>
    /// <param name="toDip">内容像素坐标→DIP 坐标的换算（预览层要减屏幕原点）。</param>
    public static void Paint(DrawingContext context, CounterShape shape, double s,
        Func<System.Drawing.PointF, Point> toDip)
    {
        Rect glyph = ToDip(shape.GlyphRect);
        if (glyph.Width <= 0 || glyph.Height <= 0) return;

        var colorBrush = new SolidColorBrush(Color.FromArgb(shape.FillColor.A, shape.FillColor.R, shape.FillColor.G, shape.FillColor.B));

        // 引线先画：让线头压在色底下面，圆点移动时不会出现"线从底下钻出一截"
        DrawTail(context, shape, s, toDip);

        // 外形三档共用一次调用：圆档的圆角半径＝半高，画出来就是正圆（导出侧 CreateRoundedRectanglePath 同口径）
        double corner = Math.Clamp(AnnotationRenderOptions.GetCounterGlyphCornerRadius(shape) * s,
            0.0, Math.Min(glyph.Width, glyph.Height) / 2.0);
        double outline = Math.Max(1.0, shape.OutlineWidth * s);
        switch (shape.Fill)
        {
            case CounterFillMode.Solid:
                context.DrawRectangle(colorBrush, new Pen(Brushes.White, outline), glyph, corner, corner);
                break;
            case CounterFillMode.Outline:
                context.DrawRectangle(null, new Pen(colorBrush, outline), glyph, corner, corner);
                break;
        }

        var typeface = new Typeface(
            new FontFamily(AnnotationRenderOptions.GetCounterFontFamilyName(shape.FontFamily)),
            FontStyle.Normal,
            shape.FontBold ? FontWeight.Bold : FontWeight.Normal);
        var textBrush = new SolidColorBrush(Color.FromArgb(shape.TextColor.A, shape.TextColor.R, shape.TextColor.G, shape.TextColor.B));

        var number = new FormattedText(
            shape.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, Math.Max(6.0, shape.NumberFontSize * s), textBrush);
        // 编号垂直居中于外形盒，顺带定下整块（编号＋说明）共用的那条基线——说明文字因此不会各按各的中线错位
        double baseline = glyph.Center.Y - number.Height / 2.0 + number.Baseline;
        double numberSize = Math.Max(6.0, shape.NumberFontSize * s);
        DrawText(context, number, textBrush,
            new Point(glyph.Center.X - number.Width / 2.0, baseline - number.Baseline),
            numberSize, halo: shape.Fill != CounterFillMode.Solid);

        if (shape.Note.Length > 0)
        {
            // ⚠ 说明文字**不**跟编号共用字色：编号压在同色底上，实心档只能是白；说明排在色底**外面**、
            // 落在被截的画面上，再用白字＋白描边就成了浅底上一个都看不见的白团。故它恒用标注色＋白描边
            // ——与「无底」档的编号同一规则（导出侧 RenderCounter 同口径，别只改一边）。
            var noteBrush = new SolidColorBrush(Color.FromArgb(shape.FillColor.A, shape.FillColor.R, shape.FillColor.G, shape.FillColor.B));
            double noteSize = Math.Max(6.0, shape.NoteFontSizeEffective * s);
            var note = new FormattedText(
                shape.Note, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, noteSize, noteBrush);
            DrawText(context, note, noteBrush,
                new Point(glyph.Right + CounterShape.NoteGap * s, baseline - note.Baseline),
                noteSize, halo: true);
        }

        Rect ToDip(System.Drawing.RectangleF rect)
        {
            Point topLeft = toDip(new System.Drawing.PointF(rect.Left, rect.Top));
            Point bottomRight = toDip(new System.Drawing.PointF(rect.Right, rect.Bottom));
            return new Rect(topLeft, bottomRight);
        }
    }

    /// <summary>
    /// 序号文字：需要 halo 时走几何体描边（先描一圈白再压字），与导出侧 GDI+ 的
    /// DrawPath＋FillPath 同一顺序。用 DrawText 是画不出描边的，别改回去。
    /// </summary>
    static void DrawText(DrawingContext context, FormattedText text, IBrush textBrush,
        Point origin, double fontSize, bool halo)
    {
        Geometry? geometry = halo ? text.BuildGeometry(origin) : null;
        if (geometry == null)
        {
            context.DrawText(text, origin);
            return;
        }

        var pen = new Pen(Brushes.White, AnnotationRenderOptions.GetCounterHaloThickness(fontSize))
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, pen, geometry);
        context.DrawGeometry(textBrush, null, geometry);
    }

    /// <summary>
    /// 引线（含箭头）：几何全部取自 <see cref="ArrowHeadGeometry"/>——与箭头标注工具同一份算式、
    /// 同一份头型列表，故"下拉里看到什么形状，画出来就是什么形状"。点列换算到 DIP 空间后交给
    /// 预览侧那份 <see cref="AnnotationLayer.DrawArrowPart"/>，不在这里重复一遍画法。
    /// </summary>
    static void DrawTail(DrawingContext context, CounterShape shape, double s,
        Func<System.Drawing.PointF, Point> toDip)
    {
        System.Drawing.PointF[] pathPx = AnnotationRenderOptions.GetCounterTailPoints(shape);
        if (pathPx.Length < 2) return;

        var brush = new SolidColorBrush(Color.FromArgb(shape.FillColor.A, shape.FillColor.R, shape.FillColor.G, shape.FillColor.B));
        var spine = new System.Drawing.PointF[pathPx.Length];
        for (int i = 0; i < pathPx.Length; i++)
        {
            Point dip = toDip(pathPx[i]);
            spine[i] = new System.Drawing.PointF((float)dip.X, (float)dip.Y);
        }

        foreach (ArrowPart part in ArrowHeadGeometry.Build(spine,
                     (float)Math.Max(1.0, shape.TailWidth * s), (float)s, shape.TailHead))
            AnnotationLayer.DrawArrowPart(context, brush, part);
    }
}
