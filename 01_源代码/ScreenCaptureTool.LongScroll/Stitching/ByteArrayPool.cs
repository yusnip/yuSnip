using System.Collections.Concurrent;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>
/// 长截图算法使用的字节数组池。T7.2a 先提供轻量实现，T7.2b 迁移旧算法时继续复用。
/// </summary>
public static class ByteArrayPool
{
    private const int MaxPoolCount = 32;
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
        Array.Clear(buffer, 0, buffer.Length);
        bag.Add(buffer);
    }
}
