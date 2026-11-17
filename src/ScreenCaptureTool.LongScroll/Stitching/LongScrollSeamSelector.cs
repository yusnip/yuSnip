using System.Drawing;
using System.Drawing.Imaging;
using System.Buffers;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>在实际画布与当前帧的重叠区内避开局部异步绘制，保留整行原像素。</summary>
internal static class LongScrollSeamSelector
{
    public static unsafe int Select(IReadOnlyList<StitchedStrip> strips, Bitmap current,
        int canvasOffset, int desired, int minimum, int maximum)
    {
        // 不扫描或重建整张长图；最多比较接缝附近 512 行，且仅锁定实际可见条带。
        const int guard = 8;
        if (minimum >= maximum || current.Width < 3) return desired;
        int radius = Math.Clamp(current.Height / 4, 48, 256);
        int start = Math.Max(minimum, desired - radius);
        int end = Math.Min(maximum, desired + radius);
        int length = end - start;
        if (length < guard * 2) return desired;
        Span<int> owners = stackalloc int[length]; owners.Fill(-1);
        Span<int> changed = stackalloc int[length]; changed.Fill(int.MaxValue);
        Span<int> edges = stackalloc int[length]; edges.Clear();
        byte[] activeColumns = new byte[(current.Width + 15) / 16];
        int missing = length;
        // 后画条带覆盖先画条带：必须对比成品实际像素，不能只拿上一张原始捕获帧。
        for (int i = strips.Count - 1; i >= 0 && missing > 0; i--)
        {
            StitchedStrip strip = strips[i];
            int first = Math.Max(start, strip.AlongStart - canvasOffset);
            int last = Math.Min(end, strip.AlongEnd - canvasOffset);
            for (int y = first; y < last; y++)
                if (owners[y - start] < 0) { owners[y - start] = i; missing--; }
        }
        BitmapData? currentData = null;
        try
        {
            currentData = current.LockBits(new Rectangle(0, start, current.Width, length), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            // 一条历史位图可能有多段可见，按条带合并 LockBits，避免逐行锁定。
            for (int first = 0; first < length; first++)
            {
                int owner = owners[first];
                if (owner < 0) continue;
                StitchedStrip strip = strips[owner];
                int last = first;
                for (int y = first + 1; y < length; y++) if (owners[y] == owner) last = y;
                int sourceStart = start + first + canvasOffset - strip.AlongStart;
                int rowBytes = checked(current.Width * 4);
                byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(rowBytes * (last - first + 1)));
                try
                {
                    // 只在复制最多 512 行时占用原图，逐像素接缝分析在独立缓冲上进行，避免阻塞预览。
                    lock (StitchedStrip.GetBitmapSyncRoot(strip.Bitmap))
                    {
                        BitmapData data = strip.Bitmap.LockBits(new Rectangle(0, sourceStart, current.Width, last - first + 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        try
                        {
                            for (int y = first; y <= last; y++)
                                Marshal.Copy(data.Scan0 + (y - first) * data.Stride, pixels, (y - first) * rowBytes, rowBytes);
                        }
                        finally { strip.Bitmap.UnlockBits(data); }
                    }
                    fixed (byte* source = pixels)
                    for (int y = first; y <= last; y++)
                    {
                        if (owners[y] != owner) continue;
                        byte* a = (byte*)currentData.Scan0 + y * currentData.Stride;
                        byte* b = source + (y - first) * rowBytes;
                        int count = 0;
                        for (int x = 0; x < current.Width; x++, a += 4, b += 4)
                        {
                            if (((*(uint*)a ^ *(uint*)b) & 0x00ffffff) == 0) continue;
                            // 按像素计数，不能用整行平均把小头像/昵称的断层稀释掉。
                            if (Math.Abs(a[0] - b[0]) > 18 || Math.Abs(a[1] - b[1]) > 18 || Math.Abs(a[2] - b[2]) > 18)
                            {
                                count++;
                                int column = x / 16;
                                activeColumns[column] = 1;
                                if (column > 0) activeColumns[column - 1] = 1;
                                if (column + 1 < activeColumns.Length) activeColumns[column + 1] = 1;
                            }
                        }
                        changed[y] = count;
                        owners[y] = -1;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(pixels); }
            }
            // 两帧相同的纯色对象内部也可能被拼成长块；优先让接缝离开动态列中的对象轮廓。
            // 仅计算发生差异的列及其邻列，正文其他列的正常纹理不影响局部接缝。
            for (int y = 0; y < length; y++)
            {
                byte* row = (byte*)currentData.Scan0 + y * currentData.Stride;
                for (int column = 0; column < activeColumns.Length; column++)
                {
                    if (activeColumns[column] == 0) continue;
                    int lastX = Math.Min(current.Width, (column + 1) * 16);
                    for (int x = Math.Max(1, column * 16); x < lastX; x++)
                    {
                        byte* a = row + x * 4; byte* b = a - 4;
                        if (Math.Abs(a[0] - b[0]) > 18 || Math.Abs(a[1] - b[1]) > 18 || Math.Abs(a[2] - b[2]) > 18) edges[y]++;
                    }
                }
            }
        }
        catch
        {
            // 取像素失败仍沿用原边界，不能影响已通过严格验证的位移或追加预算。
            return desired;
        }
        finally { if (currentData != null) current.UnlockBits(currentData); }

        int best = desired, distance = int.MaxValue, texture = int.MaxValue, run = 0, edgeSum = 0;
        for (int y = 0; y < length; y++)
        {
            edgeSum += edges[y];
            if (y >= guard * 2) edgeSum -= edges[y - guard * 2];
            run = changed[y] < 3 ? run + 1 : 0;
            if (run < guard * 2) continue;
            // 要求接缝两边连续稳定，避免挑到文字/头像内部偶然相同的一行。
            int candidate = start + y + 1 - guard;
            int delta = Math.Abs(candidate - desired);
            if (edgeSum < texture || (edgeSum == texture && delta < distance))
            { best = candidate; distance = delta; texture = edgeSum; }
        }
        return best;
    }
}
