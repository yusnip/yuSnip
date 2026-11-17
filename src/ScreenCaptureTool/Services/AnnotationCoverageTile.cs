using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SkiaSharp;

namespace ScreenCaptureTool.Services;

/// <summary>成品标注共用的固定块覆盖率缓冲：4× 采样、预乘面积平均，不重采样底图。</summary>
internal sealed class AnnotationCoverageTile : IDisposable
{
    internal const int Size = 128;
    internal const int Scale = 4;
    internal const int Padding = 2;
    internal SKBitmap Samples { get; }
    internal Bitmap Bitmap { get; }
    private readonly SKBitmap _resolved;
    private Bitmap? _drawing;
    internal Bitmap Drawing => _drawing ??= new Bitmap(Samples.Width, Samples.Height, Samples.RowBytes,
        PixelFormat.Format32bppPArgb, Samples.GetPixels());

    internal AnnotationCoverageTile()
    {
        Samples = new SKBitmap((Size + Padding * 2) * Scale, (Size + Padding * 2) * Scale, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            _resolved = new SKBitmap(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
            try { Bitmap = new Bitmap(Size, Size, _resolved.RowBytes, PixelFormat.Format32bppPArgb, _resolved.GetPixels()); }
            catch { _resolved.Dispose(); throw; }
        }
        catch { Samples.Dispose(); throw; }
    }

    internal unsafe void Resolve()
    {
        byte* input = (byte*)Samples.GetPixels().ToPointer();
        byte* output = (byte*)_resolved.GetPixels().ToPointer();
        const int count = Scale * Scale;
        // 大多数块完全在洞内或暗幕内；SIMD 扫描确认恒色后直接复制覆盖率。
        if (Samples.RowBytes == Samples.Width * 4 && _resolved.RowBytes == _resolved.Width * 4)
        {
            var values = new ReadOnlySpan<uint>(input, Samples.Width * Samples.Height);
            if (values.IndexOfAnyExcept(values[0]) < 0)
            {
                new Span<uint>(output, _resolved.Width * _resolved.Height).Fill(values[0]);
                return;
            }
        }
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            uint br = 0, ga = 0;
            for (int sy = 0; sy < Scale; sy++)
            {
                uint* row = (uint*)(input + ((y + Padding) * Scale + sy) * Samples.RowBytes) + (x + Padding) * Scale;
                for (int sx = 0; sx < Scale; sx++)
                {
                    uint pixel = row[sx];
                    br += pixel & 0x00ff00ff; ga += (pixel >> 8) & 0x00ff00ff;
                }
            }
            // 每通道最多 16×255，分隔的 16 位和不会进位到相邻通道。
            const uint rounding = (count / 2 << 16) | count / 2;
            uint rb = ((br + rounding) >> 4) & 0x00ff00ff;
            uint ag = ((ga + rounding) >> 4) & 0x00ff00ff;
            *(uint*)(output + y * _resolved.RowBytes + x * 4) = rb | (ag << 8);
        }
    }

    internal unsafe void DrawTo(Graphics target, int x, int y)
    {
        // GDI+ 对透明底图也会做预乘往返取整；零覆盖率像素必须完全绕过合成。
        using var coverage = new GraphicsPath(FillMode.Winding);
        byte* input = (byte*)_resolved.GetPixels().ToPointer();
        bool full = true;
        for (int row = 0; row < Size; row++)
        {
            byte* pixels = input + row * _resolved.RowBytes;
            int column = 0;
            while (column < Size)
            {
                while (column < Size && pixels[column * 4 + 3] == 0) { full = false; column++; }
                int start = column;
                while (column < Size && pixels[column * 4 + 3] != 0) column++;
                if (column > start) coverage.AddRectangle(new Rectangle(x + start, y + row, column - start, 1));
            }
        }
        if (coverage.PointCount == 0) return;
        if (full) { target.DrawImageUnscaled(Bitmap, x, y); return; }
        var state = target.Save();
        try
        {
            target.SetClip(coverage, CombineMode.Intersect);
            target.DrawImageUnscaled(Bitmap, x, y);
        }
        finally { target.Restore(state); }
    }

    public void Dispose()
    {
        _drawing?.Dispose(); Bitmap.Dispose(); _resolved.Dispose(); Samples.Dispose();
    }
}
