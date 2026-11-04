using System.Collections.Concurrent;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>
/// 长截图算法使用的字节数组池。
/// 租出的缓冲由调用方全量覆写后再归还，因此归还时不清零（清零是纯浪费的 memset）。
/// </summary>
public static class ByteArrayPool
{
    private const int MaxPoolCount = 8;
    private static readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> Pools = new();

    public static byte[] Rent(int length)
    {
        if (length <= 0) return Array.Empty<byte>();
        var bag = Pools.GetOrAdd(length, _ => new ConcurrentBag<byte[]>());
        return bag.TryTake(out byte[]? buffer) ? buffer : new byte[length];
    }

    public static void Return(byte[]? buffer)
    {
        if (buffer == null || buffer.Length == 0) return;
        var bag = Pools.GetOrAdd(buffer.Length, _ => new ConcurrentBag<byte[]>());
        if (bag.Count >= MaxPoolCount) return;
        bag.Add(buffer);
    }
}
