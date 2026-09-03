using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>
/// 长截图条带最终合成器。
/// </summary>
public static class LongScrollComposer
{
    public static int CalculateCompositedHeight(IReadOnlyList<StitchedStrip> strips, int fallbackHeight)
    {
        int bottom = 0;
        if (strips != null)
        {
            for (int i = 0; i < strips.Count; i++)
            {
                StitchedStrip? strip = strips[i];
                if (strip == null) continue;
                if (strip.Bottom > bottom) bottom = strip.Bottom;
            }
        }

        return bottom > 0 ? bottom : Math.Max(0, fallbackHeight);
    }

    public static Bitmap Compose(IReadOnlyList<StitchedStrip> strips, int fallbackHeight)
    {
        return Compose(strips, fallbackHeight, null, null);
    }

    public static Bitmap Compose(
        IReadOnlyList<StitchedStrip> strips,
        int fallbackHeight,
        Bitmap? fixedTop,
        Bitmap? fixedBottom)
    {
        if (strips == null || strips.Count == 0)
        {
            throw new ArgumentException("条带不能为空。", nameof(strips));
        }

        StitchedStrip first = strips[0] ?? throw new ArgumentException("首个条带为空。", nameof(strips));
        int width = first.Width;
        if (width <= 0) width = first.Bitmap.Width;
        if (width <= 0) throw new InvalidOperationException("条带宽度无效。 ");

        int middleHeight = CalculateCompositedHeight(strips, fallbackHeight);
        if (middleHeight <= 0) middleHeight = first.Height;

        int topHeight = fixedTop?.Height ?? 0;
        int bottomHeight = fixedBottom?.Height ?? 0;
        int finalHeight = topHeight + middleHeight + bottomHeight;
        if (finalHeight <= 0) finalHeight = middleHeight;

        var output = new Bitmap(width, finalHeight, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(output);
        g.Clear(Color.White);

        if (fixedTop != null && topHeight > 0)
        {
            g.DrawImage(fixedTop, 0, 0);
        }

        for (int i = 0; i < strips.Count; i++)
        {
            StitchedStrip? strip = strips[i];
            if (strip == null) continue;

            int drawY = topHeight + strip.Y;
            int stripWidth = Math.Min(width, Math.Min(strip.Width, strip.Bitmap.Width));
            int stripHeight = Math.Min(strip.Height, strip.Bitmap.Height);
            if (stripWidth <= 0 || stripHeight <= 0) continue;

            // Overlay 语义：LongScrollCanvasState 已通过 strip.Y = totalHeight - overlayH
            // 把重叠区域放到旧内容之上；这里按顺序绘制即可让新条带自然覆盖旧条带。
            if (drawY < topHeight)
            {
                int skip = topHeight - drawY;
                if (skip >= stripHeight) continue;

                g.DrawImage(
                    strip.Bitmap,
                    new Rectangle(0, topHeight, stripWidth, stripHeight - skip),
                    new Rectangle(0, skip, stripWidth, stripHeight - skip),
                    GraphicsUnit.Pixel);
            }
            else if (drawY < finalHeight)
            {
                int drawHeight = Math.Min(stripHeight, finalHeight - drawY);
                if (drawHeight <= 0) continue;
                g.DrawImage(
                    strip.Bitmap,
                    new Rectangle(0, drawY, stripWidth, drawHeight),
                    new Rectangle(0, 0, stripWidth, drawHeight),
                    GraphicsUnit.Pixel);
            }
        }

        if (fixedBottom != null && bottomHeight > 0)
        {
            g.DrawImage(fixedBottom, 0, topHeight + middleHeight);
        }

        return output;
    }
}
