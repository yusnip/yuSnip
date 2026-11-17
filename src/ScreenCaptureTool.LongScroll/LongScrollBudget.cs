using System.Drawing;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 长截图的真实上限：把「这台机器此刻拿得出来的内存」折算成成品图的字节预算，
/// 再按选区宽度换算成沿轴长度。
///
/// 两条口径缺一不可：
/// ① <b>只按当前空余内存算，不按总容量算</b>——总容量大不等于此刻拿得出来；
/// ② <b>会话启动时算一次就锁死</b>——滚动途中反复重算会让边界"会呼吸"（到顶了、
///    稍等又能滚了），用户无从判断到底停在哪。中途只有空余跌破预算时才单向收口。
///
/// ⚠ 长度上限是字节预算的<b>派生值</b>，不是独立的一档硬编码。旧版那个 28888 是拍脑袋
/// 写死的常数，与机器无关，也解释不了"为什么到这儿就停"。
/// </summary>
public static class LongScrollBudget
{
    /// <summary>成品图固定 32bpp，每像素 4 字节。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// 留余地的除数：导出那一刻「已攒的条带＋新合成的成品＋剪贴板 DIB＋PNG 编码缓冲」
    /// 是并存的，峰值约为成品图的 3 倍，所以预算只取空余的三分之一。
    /// </summary>
    public const long HeadroomDivisor = 3;

    /// <summary>
    /// 地板：空余很少的机器也给一个能用的值，而不是"滚两下就到顶"的废上限。
    /// 只在<b>会话启动</b>时兜底；中途复查（<see cref="CurrentAllowedBytes"/>）不兜底，
    /// 否则等于允许一路攒到超过机器实际拿得出的量，最后卡在合成那一步丢整张图。
    /// </summary>
    public const long MinBudgetBytes = 256L * 1024 * 1024;

    /// <summary>
    /// 天花板：单张成品图最多 512MB。这档与机器内存无关，是 GDI+ 自己的墙——
    /// 缓冲区逼近 2GB 时其内部 int32 索引溢出，实测 2000×400000（3.2GB）在空余 45GB
    /// 的机器上照样创建失败。512MB 留了 4 倍余量。
    /// </summary>
    public const long MaxBudgetBytes = 512L * 1024 * 1024;

    /// <summary>读取整机当前空余物理内存；查询失败返回 0（调用方按"取不到真值"处理）。</summary>
    public static long ReadAvailableBytes()
    {
        try
        {
            NativeMethods.MEMORYSTATUSEX status = NativeMethods.MEMORYSTATUSEX.Create();
            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullAvailPhys == 0)
                return 0;
            return (long)Math.Min(status.ullAvailPhys, long.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>本次会话锁定的成品图字节预算。</summary>
    public static long SessionBudgetBytes(long availableBytes)
    {
        long byAvailable = availableBytes > 0 ? availableBytes / HeadroomDivisor : 0;
        return Math.Clamp(byAvailable, MinBudgetBytes, MaxBudgetBytes);
    }

    /// <summary>
    /// 中途复查用的字节上限：与 <see cref="SessionBudgetBytes"/> 同源，但<b>不取地板</b>。
    /// 查询失败返回 <see cref="long.MaxValue"/>——读不到真值时绝不因为"猜"而打断用户正在进行的采集。
    /// </summary>
    public static long CurrentAllowedBytes()
    {
        long available = ReadAvailableBytes();
        if (available <= 0) return long.MaxValue;
        return Math.Min(available / HeadroomDivisor, MaxBudgetBytes);
    }

    /// <summary>交叉轴长度＝选区在"非滚动方向"上的边长（纵向是宽，横向是高）。</summary>
    public static int CrossLength(ScrollAxis axis, Rectangle region)
        => axis == ScrollAxis.Horizontal ? region.Height : region.Width;

    /// <summary>沿轴一行的字节数。用完整选区边长算，不扣除横向裁掉的静止带——宁可高估单行成本。</summary>
    public static long RowBytes(ScrollAxis axis, Rectangle region)
        => (long)Math.Max(1, CrossLength(axis, region)) * BytesPerPixel;

    /// <summary>已攒内容占用的字节数（按完整选区交叉轴保守计）。</summary>
    public static long BytesForLength(ScrollAxis axis, Rectangle region, int alongLength)
        => RowBytes(axis, region) * (long)Math.Max(0, alongLength);

    /// <summary>
    /// 把字节预算折算成沿轴长度上限。<paramref name="reservedAlongLength"/> 是固定层
    /// （首尾吸附条）在成品里额外占掉的沿轴长度，必须先扣掉——上限管的是可滚动内容。
    /// 结果不低于一个视口高度，保证任何机器都至少能把当前这一屏拼出来。
    /// </summary>
    public static int ResolveAlongLength(ScrollAxis axis, Rectangle region, int reservedAlongLength)
    {
        int viewportAlong = axis == ScrollAxis.Horizontal ? Math.Max(1, region.Width) : Math.Max(1, region.Height);
        long budget = SessionBudgetBytes(ReadAvailableBytes());
        long rowBytes = RowBytes(axis, region);

        long usable = budget / rowBytes - Math.Max(0, reservedAlongLength);
        if (usable < viewportAlong) usable = viewportAlong;
        return usable > int.MaxValue ? int.MaxValue : (int)usable;
    }

    public static long ToMb(long bytes) => Math.Max(0, bytes) / (1024 * 1024);
}
