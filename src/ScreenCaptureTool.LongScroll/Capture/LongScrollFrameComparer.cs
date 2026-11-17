using System.Numerics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.LongScroll.Capture;

/// <summary>逐像素比较 RGB；SIMD 提前退出，无采样盲区，忽略 GDI 未定义的第四字节。</summary>
internal static class LongScrollFrameComparer
{
    /// <summary>仅供自动滚动调度：局部变化之外，分散的纹理块逐像素原位相同，说明视口未移动。</summary>
    public static unsafe bool StationaryWithLocalChanges(Bitmap first, Bitmap second, ScrollAxis axis)
    {
        if (first.Width != second.Width || first.Height != second.Height || first.Width < 48 || first.Height < 48) return false;
        try
        {
            var rectangle = new Rectangle(0, 0, first.Width, first.Height);
            var a = first.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var b = second.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    const int alongTiles = 12, crossTiles = 8;
                    int along = axis == ScrollAxis.Vertical ? first.Height : first.Width;
                    int cross = axis == ScrollAxis.Vertical ? first.Width : first.Height;
                    int changed = 0, anchors = 0; uint alongMask = 0, crossMask = 0;
                    for (int row = 0; row < alongTiles; row++)
                    for (int column = 0; column < crossTiles; column++)
                    {
                        int u = row * along / alongTiles, v = column * cross / crossTiles;
                        int h = (row + 1) * along / alongTiles - u, w = (column + 1) * cross / crossTiles - v;
                        var tile = axis == ScrollAxis.Vertical ? new Rectangle(v, u, w, h) : new Rectangle(u, v, h, w);
                        nint p = a.Scan0 + tile.Y * a.Stride + tile.X * 4;
                        nint q = b.Scan0 + tile.Y * b.Stride + tile.X * 4;
                        if (!EqualRows(p, a.Stride, q, b.Stride, tile.Width, tile.Height)) { changed++; continue; }
                        // 纯背景相同不能证明不滚动；纹理锚点必须横纵分散，且不能全挤在固定头尾或侧栏。
                        if (!HasTexture(p, a.Stride, tile.Width, tile.Height)) continue;
                        anchors++; alongMask |= 1u << row; crossMask |= 1u << column;
                    }
                    return changed > 0 && changed <= alongTiles * crossTiles * 0.45 && anchors >= 12
                        && BitOperations.PopCount(alongMask) >= 7 && BitOperations.PopCount(crossMask) >= 4
                        && BitOperations.PopCount(alongMask & 0b000111111000) >= 2
                        && BitOperations.PopCount(crossMask & 0b00111100) >= 2;
                }
                finally { second.UnlockBits(b); }
            }
            finally { first.UnlockBits(a); }
        }
        catch { return false; }
    }

    private static unsafe bool HasTexture(nint bits, int stride, int width, int height)
    {
        int edges = 0;
        for (int y = 1; y < height; y++)
        {
            uint* row = (uint*)((byte*)bits + y * stride);
            for (int x = 1; x < width; x++)
            {
                uint p = row[x], q = row[x - 1];
                if (Math.Abs((int)(p & 255) - (int)(q & 255)) >= 16
                    || Math.Abs((int)((p >> 8) & 255) - (int)((q >> 8) & 255)) >= 16
                    || Math.Abs((int)((p >> 16) & 255) - (int)((q >> 16) & 255)) >= 16)
                    if (++edges >= 12) return true;
            }
        }
        return false;
    }

    public static bool EqualRgb(ReadOnlySpan<uint> first, ReadOnlySpan<uint> second)
    {
        if (first.Length != second.Length) return false;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var mask = new Vector<uint>(0x00ffffff);
            for (; i <= first.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                if (!Vector.EqualsAll((new Vector<uint>(first.Slice(i)) ^ new Vector<uint>(second.Slice(i))) & mask, Vector<uint>.Zero)) return false;
        }
        for (; i < first.Length; i++) if (((first[i] ^ second[i]) & 0x00ffffff) != 0) return false;
        return true;
    }

    public static unsafe bool EqualRows(IntPtr first, int firstStride, IntPtr second, int secondStride, int width, int height)
    {
        for (int y = 0; y < height; y++)
            if (!EqualRgb(new ReadOnlySpan<uint>((byte*)first + y * firstStride, width),
                new ReadOnlySpan<uint>((byte*)second + y * secondStride, width))) return false;
        return true;
    }

    public static unsafe bool Update(IntPtr bits, int width, int height, ref byte[]? previous, bool valid)
    {
        var current = new ReadOnlySpan<byte>((void*)bits, checked(width * height * 4));
        if (previous == null || previous.Length != current.Length) { previous = new byte[current.Length]; valid = false; }
        if (valid && EqualRgb(MemoryMarshal.Cast<byte, uint>(current), MemoryMarshal.Cast<byte, uint>(previous))) return true;
        current.CopyTo(previous);
        return false;
    }
}
