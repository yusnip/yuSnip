using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 模糊/马赛克工具强度映射。UI 等级为 1..30，物理模糊半径为 >0..26，马赛克块大小为 2..50。
/// </summary>
public static class BlurIntensityMapper
{
    public const int MinUiLevel = 1;
    public const int MaxUiLevel = 30;
    public const int DefaultUiLevel = 10;
    public const double MaxPhysicalIntensity = 26.0;

    public static int ClampUiLevel(int level)
    {
        return Math.Clamp(level, MinUiLevel, MaxUiLevel);
    }

    public static double UiToPhysical(int level)
    {
        level = ClampUiLevel(level);
        return MaxPhysicalIntensity * level / MaxUiLevel;
    }

    public static int PhysicalToUi(double intensity)
    {
        if (intensity <= 0.0) return MinUiLevel;
        intensity = Math.Clamp(intensity, 0.0, MaxPhysicalIntensity);
        return ClampUiLevel((int)Math.Round(intensity * MaxUiLevel / MaxPhysicalIntensity));
    }

    public static int UiLevelToMosaicBlockSize(int level)
    {
        level = ClampUiLevel(level);
        double t = (level - 1) * 48.0 / (MaxUiLevel - MinUiLevel);
        return Math.Clamp(2 + (int)Math.Round(t), 2, 50);
    }
}

/// <summary>
/// 高性能标准高斯模糊（Gaussian Blur）帮助类。
/// 使用 1D 分离高斯卷积（Horizontal + Vertical Pass）直接在 32bpp ARGB 图像字节内存上快速计算。
/// 支持多线程并行加速与扩展 Padding 外边距采样，确保矩形边缘与中间获得 100% 均匀一致的高斯模糊强度。
/// 导出按全分辨率卷积；实时预览为保帧率会先把整屏降采样 2 倍再卷积，故预览模糊细节略少，强度与范围一致。
/// </summary>
public static class GaussianBlurHelper
{
    /// <summary>
    /// 生成全图高斯模糊滤镜图层（用于实时交互/拖拽/移动预览，计算一次后全屏复用）。
    /// </summary>
    public static Bitmap CreateBlurredLayer(Bitmap source, double intensity, int downsampleFactor = 2)
    {
        if (source == null || source.Width <= 0 || source.Height <= 0) return null!;
        int factor = Math.Max(1, downsampleFactor);
        int downW = Math.Max(1, (source.Width + factor - 1) / factor);
        int downH = Math.Max(1, (source.Height + factor - 1) / factor);

        var down = new Bitmap(downW, downH, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(down))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(source, new Rectangle(0, 0, downW, downH));
        }

        double scaledIntensity = intensity / factor;
        ApplyGaussianBlurDirect(down, scaledIntensity);
        return down;
    }

    /// <summary>
    /// 对位图指定区域执行高斯卷积（带外边距扩展以保证边缘连续性）。
    /// </summary>
    public static void ApplyGaussianBlur(Bitmap bitmap, Rectangle rect, double intensity)
    {
        if (bitmap == null || rect.Width <= 1 || rect.Height <= 1 || intensity <= 0.1) return;

        rect.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (rect.Width <= 1 || rect.Height <= 1) return;

        // 如果刚好是整个位图，直接在原位图上做内存计算，避免冗余的 Clone 和 GDI+ DrawImage
        if (rect.X == 0 && rect.Y == 0 && rect.Width == bitmap.Width && rect.Height == bitmap.Height)
        {
            ApplyGaussianBlurDirect(bitmap, intensity);
            return;
        }

        // intensity (0.0..26.0) 映射为高斯 Sigma 和卷积核半径 Radius
        double sigma = Math.Max(0.5, intensity);
        int radius = Math.Clamp((int)Math.Ceiling(sigma * 2.0), 1, 60);

        // 为确保矩形边缘与中间获得 100% 相同、均匀对称的高斯卷积强度，
        // 向外扩展高斯半径 radius 像素的外边距，提取周围真实原图像素参与卷积采样。
        int margin = radius + 2;
        Rectangle expRect = Rectangle.FromLTRB(
            Math.Max(0, rect.Left - margin),
            Math.Max(0, rect.Top - margin),
            Math.Min(bitmap.Width, rect.Right + margin),
            Math.Min(bitmap.Height, rect.Bottom + margin));

        if (expRect.Width <= 1 || expRect.Height <= 1) return;

        // 从原图提取包含外边距的扩展区域进行卷积
        using Bitmap procBmp = bitmap.Clone(expRect, PixelFormat.Format32bppArgb);
        ApplyGaussianBlurDirect(procBmp, intensity);

        // 计算目标 rect 在扩展位图 procBmp 中的相对 crop 区域
        int cropX = rect.Left - expRect.Left;
        int cropY = rect.Top - expRect.Top;
        Rectangle cropInProc = new Rectangle(cropX, cropY, rect.Width, rect.Height);

        // 将卷积完成后的像素精确贴回原 bitmap 的目标 rect 位置
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(procBmp, rect, cropInProc, GraphicsUnit.Pixel);
        }
    }

    /// <summary>
    /// 直接在位图全图内存上执行 1D 分离高斯模糊（多线程并行加速）。
    /// </summary>
    private static void ApplyGaussianBlurDirect(Bitmap bitmap, double intensity)
    {
        if (bitmap == null || bitmap.Width <= 1 || bitmap.Height <= 1 || intensity <= 0.1) return;

        double sigma = Math.Max(0.5, intensity);
        int radius = Math.Clamp((int)Math.Ceiling(sigma * 2.0), 1, 60);

        // 计算 1D 高斯核
        int kernelSize = radius * 2 + 1;
        float[] kernel = new float[kernelSize];
        double twoSigmaSq = 2.0 * sigma * sigma;
        double sum = 0.0;
        for (int i = -radius; i <= radius; i++)
        {
            double val = Math.Exp(-(i * i) / twoSigmaSq);
            kernel[i + radius] = (float)val;
            sum += val;
        }
        for (int i = 0; i < kernelSize; i++)
        {
            kernel[i] = (float)(kernel[i] / sum);
        }

        BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = bd.Stride;
        byte[] srcPixels = new byte[stride * height];
        byte[] tmpPixels = new byte[stride * height];
        byte[] dstPixels = new byte[stride * height];
        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, srcPixels, 0, srcPixels.Length);

        // 1. 水平高斯 Pass（多线程并行加速）
        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int px = Math.Clamp(x + k, 0, width - 1);
                    int idx = rowOffset + px * 4;
                    float w = kernel[k + radius];
                    b += srcPixels[idx] * w;
                    g += srcPixels[idx + 1] * w;
                    r += srcPixels[idx + 2] * w;
                    a += srcPixels[idx + 3] * w;
                }
                int outIdx = rowOffset + x * 4;
                tmpPixels[outIdx] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
                tmpPixels[outIdx + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
                tmpPixels[outIdx + 2] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
                tmpPixels[outIdx + 3] = (byte)Math.Clamp((int)Math.Round(a), 0, 255);
            }
        });

        // 2. 垂直高斯 Pass（多线程并行加速）
        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int py = Math.Clamp(y + k, 0, height - 1);
                    int idx = py * stride + x * 4;
                    float w = kernel[k + radius];
                    b += tmpPixels[idx] * w;
                    g += tmpPixels[idx + 1] * w;
                    r += tmpPixels[idx + 2] * w;
                    a += tmpPixels[idx + 3] * w;
                }
                int outIdx = rowOffset + x * 4;
                dstPixels[outIdx] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
                dstPixels[outIdx + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
                dstPixels[outIdx + 2] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
                dstPixels[outIdx + 3] = (byte)Math.Clamp((int)Math.Round(a), 0, 255);
            }
        });

        System.Runtime.InteropServices.Marshal.Copy(dstPixels, 0, bd.Scan0, dstPixels.Length);
        bitmap.UnlockBits(bd);
    }
}
