using System.Drawing;

namespace ScreenCaptureTool.Core.Capture;

/// <summary>线性 scRGB → SDR sRGB。参数按输出锁定，不根据每帧直方图改曝光，避免长截图接缝。</summary>
internal static class HdrToneMapper
{
    // CPU 参考实现专用查表；生产 GPU 路径仅调用 SourceRect，不初始化这些表。
    private static class Lookup
    {
        internal static readonly float[] Linear = BuildLinear();
        internal static readonly byte[] Srgb = BuildSrgb();
    }

    private static float[] BuildLinear()
    {
        var table = new float[65536];
        for (int i = 0; i < table.Length; i++)
        {
            float value = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            table[i] = float.IsFinite(value) ? value : value > 0 ? 65504 : 0;
        }
        return table;
    }

    private static byte[] BuildSrgb()
    {
        var table = new byte[65536];
        for (int i = 0; i < table.Length; i++)
        {
            float x = i / 65535f;
            float encoded = x <= .0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - .055f;
            table[i] = (byte)Math.Clamp((int)MathF.Round(encoded * 255), 0, 255);
        }
        return table;
    }

    private static byte Encode(float value) => Lookup.Srgb[(int)Math.Clamp(value * 65535 + .5f, 0, 65535)];

    internal static (byte R, byte G, byte B) Map(ushort red, ushort green, ushort blue, float whiteScale)
        => MapNormalized(red, green, blue, 1 / whiteScale);

    private static (byte R, byte G, byte B) MapNormalized(ushort red, ushort green, ushort blue, float inverseWhite)
    {
        float r = Lookup.Linear[red] * inverseWhite, g = Lookup.Linear[green] * inverseWhite, b = Lookup.Linear[blue] * inverseWhite;
        float luminance = .2126f * r + .7152f * g + .0722f * b;
        if (luminance <= 0) return (0, 0, 0);
        // 按 RGB 峰值统一缩放，保持线性色度比例；不能为了保亮度把超范围亮红／亮黄推成灰白。
        float peak = MathF.Max(r, MathF.Max(g, b));
        // 普通 SDR 色域 [0,1] 原样编码，参考白必须是 255；只对越界峰值归一化，不为 HDR 留白而压暗桌面。
        // 代价是超过参考白的中性高光合并为白色，不能同时在 8 位 SDR 中保留更亮的白。
        float gain = 1 / MathF.Max(1, peak);
        r *= gain; g *= gain; b *= gain;
        // 正 RGB 已落入 SDR 色域；仅对 scRGB 负分量围绕当前亮度压色域，避免逐通道硬截断。
        float mapped = luminance * gain;
        float min = MathF.Min(r, MathF.Min(g, b));
        if (min >= 0) return (Encode(r), Encode(g), Encode(b));
        float saturation = 1;
        if (min < 0) saturation = MathF.Min(saturation, mapped / (mapped - min));
        return (Encode(mapped + (r - mapped) * saturation), Encode(mapped + (g - mapped) * saturation), Encode(mapped + (b - mapped) * saturation));
    }

    /// <summary>把桌面局部矩形换到 DXGI 未旋转纹理；rotation 对应 DXGI_MODE_ROTATION 的 1～4。</summary>
    internal static Rectangle SourceRect(Rectangle desktop, int sourceWidth, int sourceHeight, uint rotation) => rotation switch
    {
        2 => new Rectangle(desktop.Y, sourceHeight - desktop.Right, desktop.Height, desktop.Width),
        3 => new Rectangle(sourceWidth - desktop.Right, sourceHeight - desktop.Bottom, desktop.Width, desktop.Height),
        4 => new Rectangle(sourceWidth - desktop.Bottom, desktop.X, desktop.Height, desktop.Width),
        _ => desktop,
    };

    internal static unsafe void Convert(nint source, int rowPitch, int sourceWidth, int sourceHeight,
        uint rotation, float whiteScale, Span<byte> destination)
    {
        if (source == 0 || sourceWidth <= 0 || sourceHeight <= 0 || rowPitch < checked(sourceWidth * 8)
            || !(whiteScale > 0) || !float.IsFinite(whiteScale) || rotation is < 1 or > 4)
            throw new ArgumentException("HDR 源纹理参数非法");
        int width = rotation is 2 or 4 ? sourceHeight : sourceWidth;
        int height = rotation is 2 or 4 ? sourceWidth : sourceHeight;
        if (destination.Length < checked(width * height * 4)) throw new ArgumentException("HDR 目标缓冲不足");
        fixed (byte* dest = destination)
        {
            float inverseWhite = 1 / whiteScale;
            for (int y = 0; y < height; y++)
            {
                byte* row = dest + (long)y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int sx = x, sy = y;
                    if (rotation == 2) { sx = y; sy = sourceHeight - 1 - x; }
                    else if (rotation == 3) { sx = sourceWidth - 1 - x; sy = sourceHeight - 1 - y; }
                    else if (rotation == 4) { sx = sourceWidth - 1 - y; sy = x; }
                    ushort* pixel = (ushort*)((byte*)source + (long)sy * rowPitch + sx * 8L);
                    var color = MapNormalized(pixel[0], pixel[1], pixel[2], inverseWhite);
                    row[x * 4] = color.B; row[x * 4 + 1] = color.G; row[x * 4 + 2] = color.R; row[x * 4 + 3] = 255;
                }
            }
        }
    }
}
