using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 文本烘焙的换算口径：把「画布 DIP → 输出位图像素」的全部系数收在一处，
/// 让截图（画布铺满虚拟屏、选区可能被屏边界裁掉）与贴图（画布原点即内容原点、无裁剪）
/// 共用同一套绘制算法，只有系数不同。
///
/// 换算表达式的书写顺序与 float 强转位置与截图侧原式逐字一致（先加原点再相减、最后一次性转 float），
/// 因为重排次序会带来浮点末位差异，而本项目的验收门槛是导出逐像素不变。
/// </summary>
public sealed class TextBakeContext
{
    /// <summary>DIP → 物理像素（截图=屏幕 DPI 缩放；贴图=定稿烘焙比例）。</summary>
    public float DipScale { get; init; } = 1.0f;

    /// <summary>物理像素 → 位图像素，X 方向（选区被虚拟屏边界裁剪时 ≠ 1）。</summary>
    public float ScaleX { get; init; } = 1.0f;

    /// <summary>物理像素 → 位图像素，Y 方向。</summary>
    public float ScaleY { get; init; } = 1.0f;

    /// <summary>文本画布原点在屏幕上的物理像素坐标（截图=虚拟屏左上；贴图=0,0）。</summary>
    public double CanvasOriginX { get; init; }

    /// <summary>文本画布原点在屏幕上的物理像素坐标（Y）。</summary>
    public double CanvasOriginY { get; init; }

    /// <summary>输出位图左上角在屏幕上的物理像素坐标（截图=选区左上；贴图=0,0）。</summary>
    public double BitmapOriginX { get; init; }

    /// <summary>输出位图左上角在屏幕上的物理像素坐标（Y）。</summary>
    public double BitmapOriginY { get; init; }

    /// <summary>DIP → 位图像素的 X 综合系数（宽度类量用它）。</summary>
    public float DipToBmpX { get; init; } = 1.0f;

    /// <summary>DIP → 位图像素的 Y 综合系数（高度类量用它）。</summary>
    public float DipToBmpY { get; init; } = 1.0f;

    /// <summary>DIP → 位图像素的各向同性系数：字号、圆角、内距、描边、行高用它，取 X/Y 中较小者。</summary>
    public float DipToBmpMin { get; init; } = 1.0f;

    /// <summary>
    /// 图层 DIP 左上角 → 位图局部像素 X。
    /// 链路：DIP 相对文本画布原点 → ×DipScale 加画布原点得屏幕物理像素 → 减位图原点 → ×裁剪比例。
    /// </summary>
    public float ToBitmapX(double left)
        => (float)((left * DipScale + CanvasOriginX - BitmapOriginX) * ScaleX);

    /// <summary>图层 DIP 左上角 → 位图局部像素 Y。</summary>
    public float ToBitmapY(double top)
        => (float)((top * DipScale + CanvasOriginY - BitmapOriginY) * ScaleY);

    /// <summary>
    /// 截图侧口径：逐字复现原导出函数里现算的 dipScale / scaleX / scaleY 与两个屏幕原点。
    /// </summary>
    public static TextBakeContext CreateCaptureContext(Bitmap bitmap, Rectangle selectedScreenRect,
        double dpiScale, Rectangle virtualScreenRect)
    {
        float dipScale = (float)(dpiScale > 0 ? dpiScale : 1.0);

        // scaleX/scaleY：位图裁剪比例（通常≈1.0），处理选区被虚拟屏幕边界裁剪的情况。
        float scaleX = selectedScreenRect.Width > 0 ? bitmap.Width / (float)selectedScreenRect.Width : 1.0f;
        float scaleY = selectedScreenRect.Height > 0 ? bitmap.Height / (float)selectedScreenRect.Height : 1.0f;
        float minScale = Math.Min(scaleX, scaleY);

        return new TextBakeContext
        {
            DipScale = dipScale,
            ScaleX = scaleX,
            ScaleY = scaleY,
            CanvasOriginX = virtualScreenRect.X,
            CanvasOriginY = virtualScreenRect.Y,
            BitmapOriginX = selectedScreenRect.X,
            BitmapOriginY = selectedScreenRect.Y,
            // 综合缩放：DIP → 物理像素 → 位图局部像素
            DipToBmpX = dipScale * scaleX,
            DipToBmpY = dipScale * scaleY,
            DipToBmpMin = dipScale * minScale,
        };
    }

    /// <summary>
    /// 贴图侧口径：文本画布原点＝输出位图原点（都是内容矩形的 0 起点），也无选区裁剪，
    /// 故三个屏幕原点与裁剪比例一律取默认值，只有 DIP→位图这一个系数。
    /// 该系数必须是 <see cref="StickerWindow"/> 图层 <c>ScreenToDipScale</c>（= _scale / SafeScaling）的倒数，
    /// 否则编辑期看着对、定稿后位置与字号都会错。
    /// </summary>
    public static TextBakeContext CreateStickerContext(double dipToPixel)
    {
        float scale = (float)(dipToPixel > 0 ? dipToPixel : 1.0);
        return new TextBakeContext
        {
            DipScale = scale,
            DipToBmpX = scale,
            DipToBmpY = scale,
            DipToBmpMin = scale,
        };
    }
}

/// <summary>
/// 文本图层的 GDI+ 烘焙实现，截图导出与贴图定稿共用。
/// 输入是脱离活控件的 <see cref="TextLayerSnapshot"/>（含烘焙所需的实测宽/高/行高），
/// 坐标与缩放口径由 <see cref="TextBakeContext"/> 提供；共享代码不猜单位，两侧必须各自把口径算全。
/// </summary>
public static class TextLayerRenderer
{
    public static void RenderToBitmap(Bitmap bitmap, IReadOnlyList<TextLayerSnapshot>? layers,
        TextBakeContext context, AnnotationDocument? annotations = null)
    {
        if (bitmap == null || layers == null || layers.Count == 0) return;

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        foreach (var info in layers)
        {
            if (info == null) continue;
            string text = info.Text;
            if (string.IsNullOrEmpty(text)) continue;

            float x = context.ToBitmapX(info.Left);
            float y = context.ToBitmapY(info.Top);
            // ⚠ 实测宽高为 0＝这一层压根没被布局量过（2026-09-29 那轮"只剩第一个字"就是这么来的：0 被下面兜成
            // 1 像素宽的排版框，一行只塞得下一个字）。病根已在 `TextAnnotationController.RestoreTextSnapshot` 收口，
            // 这里留一行痕，好让下一轮日志能直接证明那条路封住了没有。
            if (info.MeasuredWidth <= 0 || info.MeasuredHeight <= 0)
            {
                Core.AppLogger.Warn("TextLayerRenderer: 文字层实测宽高为 " + info.MeasuredWidth.ToString("F1")
                    + "×" + info.MeasuredHeight.ToString("F1") + "（未布局就被烘焙），字数=" + text.Length
                    + " 首字=" + text.Substring(0, 1));
            }
            float w = (float)Math.Max(1.0, info.MeasuredWidth * context.DipToBmpX);
            float h = (float)Math.Max(1.0, info.MeasuredHeight * context.DipToBmpY);

            var state = g.Save();
            try
            {
                if (annotations != null)
                {
                    ErasureMark[] marks = annotations.Shapes.OfType<ErasureShape>()
                        .Where(erase => erase.AffectsText(info.Id)).SelectMany(erase => erase.Marks).ToArray();
                    if (marks.Length > 0)
                    {
                        // 洞必须取并集：GraphicsPath 默认 FillMode.Alternate 是"奇偶"，两枚洞重叠处会被判成"不是洞"，
                        // 于是"在同一处再擦一次"反而把上次擦掉的文字放回来（2026-10-06，与预览侧 NonZero 同一修法）。
                        using var path = new GraphicsPath { FillMode = FillMode.Winding };
                        foreach (ErasureMark mark in marks)
                        {
                            var cut = new RectangleF(
                                (float)((mark.Bounds.X - context.BitmapOriginX) * context.ScaleX),
                                (float)((mark.Bounds.Y - context.BitmapOriginY) * context.ScaleY),
                                mark.Bounds.Width * context.ScaleX, mark.Bounds.Height * context.ScaleY);
                            if (cut.Width <= 0 || cut.Height <= 0) continue;
                            // 够不着本层矩形的洞不影响这一层的任何像素，省掉它＝省掉一次区域构造的开销。
                            // 实测宽高可疑（未布局量到 0）时不裁剪，照旧全量建洞，否则会一个都没擦掉。
                            if (w > 1f && h > 1f)
                            {
                                float reach = Math.Max(cut.Width, cut.Height);
                                if (!cut.IntersectsWith(RectangleF.FromLTRB(x - reach, y - reach,
                                    x + w + reach, y + h + reach))) continue;
                            }
                            if (mark.IsCircle) path.AddEllipse(cut);
                            else path.AddRectangle(cut);
                        }
                        if (path.PointCount > 0)
                        {
                            using var region = new Region(path);
                            g.ExcludeClip(region);
                        }
                    }
                }
                if (Math.Abs(info.RotationAngle) > 0.01)
                {
                    g.TranslateTransform(x + w / 2.0f, y + h / 2.0f);
                    g.RotateTransform((float)info.RotationAngle);
                    g.TranslateTransform(-(x + w / 2.0f), -(y + h / 2.0f));
                }

                if (info.HasBackground)
                {
                    int alpha = Math.Clamp((int)Math.Round(info.FillOpacity / 100.0 * 255), 0, 255);
                    using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(alpha, info.FillColor.R, info.FillColor.G, info.FillColor.B));
                    float cr = (float)(info.FillCornerRadius * context.DipToBmpMin);
                    if (cr > 0)
                    {
                        using var gp = CreateRoundedRectPath(x, y, w, h, cr);
                        g.FillPath(bgBrush, gp);
                    }
                    else
                    {
                        g.FillRectangle(bgBrush, x, y, w, h);
                    }
                }

                var fontStyle = info.IsItalic ? System.Drawing.FontStyle.Italic : System.Drawing.FontStyle.Regular;
                if (info.IsBold) fontStyle |= System.Drawing.FontStyle.Bold;
                using var font = new Font(info.FontFamily ?? "Microsoft YaHei UI", (float)(info.FontSize * context.DipToBmpMin), fontStyle, GraphicsUnit.Pixel);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Near,
                    Trimming = StringTrimming.None,
                    FormatFlags = StringFormatFlags.LineLimit,
                };

                float pad = (float)((info.HasBackground ? info.FillPadding : 0) * context.DipToBmpMin);
                // 编辑态描边覆盖在字形上，不占内距；只有文字底色的 FillPadding 改变文字原点。
                RectangleF layout = new RectangleF(x + pad, y + pad, Math.Max(1, w - 2 * pad), Math.Max(1, h - 2 * pad));

                using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(info.Color.A, info.Color.R, info.Color.G, info.Color.B));
                using var textPath = CreateAlignedTextPath(info, context, font, format, layout);

                if (info.HasStroke)
                {
                    // 注意：这里传的是「DIP 描边粗细 × 烘焙系数」，与编辑期可视树传 DIP 原值不是同一口径。
                    // DPI≠100% 时两者本就分叉，属截图侧既有行为；贴图侧烘焙系数在缩放到 50% 时可达 ≈3.0，
                    // 量级比截图侧更极端，改任何一边都会让历史风格的成品图变化，故原样保留。
                    using var strokePen = new Pen(
                        System.Drawing.Color.FromArgb(info.StrokeColor.A, info.StrokeColor.R, info.StrokeColor.G, info.StrokeColor.B),
                        (float)AnnotationRenderOptions.GetTextStrokeRenderThickness(info.StrokeThickness * context.DipToBmpMin, font.Size))
                    {
                        LineJoin = LineJoin.Round,
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round,
                    };
                    g.DrawPath(strokePen, textPath);
                }

                g.FillPath(textBrush, textPath);
            }
            finally
            {
                g.Restore(state);
            }
        }
    }

    /// <summary>以编辑态同源字形度量对齐 GDI+ 路径，避免两套字体引擎的行高／基线造成细小跳位。</summary>
    internal static GraphicsPath CreateAlignedTextPath(TextLayerSnapshot info, TextBakeContext context,
        Font font, StringFormat format, RectangleF layout)
    {
        var result = new GraphicsPath();
        try
        {
            double lineHeight = info.LineHeight * context.DipToBmpMin;
            var typeface = new Avalonia.Media.Typeface(info.FontFamily,
                info.IsItalic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
                info.IsBold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal);
            string[] lines = info.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var lineLayout = new RectangleF(layout.X, layout.Y + (float)(i * lineHeight),
                    layout.Width, (float)Math.Max(1, lineHeight));
                using var linePath = new GraphicsPath();
                linePath.AddString(lines[i], font.FontFamily, (int)font.Style, font.Size, lineLayout, format);
                if (linePath.PointCount == 0) continue;
                var reference = new Avalonia.Media.FormattedText(lines[i], System.Globalization.CultureInfo.CurrentCulture,
                    Avalonia.Media.FlowDirection.LeftToRight, typeface, font.Size, Avalonia.Media.Brushes.Black)
                {
                    MaxTextWidth = layout.Width,
                    LineHeight = lineHeight,
                    Trimming = Avalonia.Media.TextTrimming.None,
                };
                Avalonia.Media.Geometry? geometry = reference.BuildGeometry(default);
                if (geometry != null)
                {
                    var expected = geometry.Bounds;
                    var actual = linePath.GetBounds();
                    using var shift = new Matrix();
                    shift.Translate((float)(lineLayout.X + expected.X - actual.X),
                        (float)(lineLayout.Y + expected.Y - actual.Y));
                    linePath.Transform(shift);
                }
                result.AddPath(linePath, false);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static GraphicsPath CreateRoundedRectPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        if (r <= 0) { path.AddRectangle(new RectangleF(x, y, w, h)); return path; }
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
