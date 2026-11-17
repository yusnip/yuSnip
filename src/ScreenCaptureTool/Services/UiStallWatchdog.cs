using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Services;

/// <summary>
/// UI 卡顿哨兵：探测 Avalonia 主线程被同步工作阻塞的时长。
///
/// <para><b>为什么需要它</b>：「整体操作发涩」是一类最难定位的问题——它不崩、不报错，
/// 单次耗时也不长，但用户手感明显。此前只能靠事后猜（GC？反射？Win32 同步调用？），
/// 现场没有任何证据。本哨兵把「主线程卡了多久」变成可观测的一行日志。</para>
///
/// <para><b>工作原理</b>：线程池定时器每 <see cref="ProbeIntervalMs"/> 向 UI 线程投递一个
/// <see cref="DispatcherPriority.Normal"/> 优先级的空回调，并记下投递时刻；回调真正执行时
/// 两者之差＝主线程从「有空处理普通消息」到「实际处理」被拖延的时间。该值超过
/// <see cref="StallThresholdMs"/> 即判定为一次用户可感知的卡顿，连同 GC 快照一起落日志，
/// 便于把卡顿归因到托管堆压力还是业务代码。</para>
///
/// <para><b>开销</b>：每 500ms 一次线程池唤醒 + 一次 dispatcher 投递，均无分配热路径，
/// 远低于它要探测的那些卡顿本身。故在所有构建档常开（与 <see cref="StartupProfiler"/> 同口径，
/// 正式版可在设置「日志」页读到），便于现场取证。</para>
///
/// <para><b>不重复投递</b>：上一次心跳还没被 UI 线程消化时，本轮只更新「仍在持续」的时长报告，
/// 不再叠加投递——否则长时间卡顿会在队列里堆积一串回调，恢复瞬间集中爆发。</para>
/// </summary>
public static class UiStallWatchdog
{
    /// <summary>探测节奏：多久投递一次心跳。</summary>
    private const int ProbeIntervalMs = 500;

    /// <summary>卡顿判定阈值：主线程拖延超过此值才报告，滤掉正常的布局/绘制抖动。</summary>
    private const double StallThresholdMs = 200;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private static Timer? _timer;

    /// <summary>在途心跳的投递时刻（<see cref="Stopwatch.GetTimestamp"/>）；0 表示当前无在途探测。</summary>
    private static long _pendingSinceTicks;

    /// <summary>本轮卡顿是否已报告过，保证一次卡顿只出一条日志。</summary>
    private static int _reported;

    /// <summary>本次运行累计报告的卡顿次数。</summary>
    private static int _stallCount;

    /// <summary>本次运行观测到的最长单次拖延，退出时汇总输出。</summary>
    private static double _worstDelayMs;

    /// <summary>
    /// 启动哨兵。幂等：已在运行时直接返回。
    /// 在后台线程调用即可（<see cref="Timer"/> 的构造与回调都是线程安全的）。
    /// </summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(Probe, null, ProbeIntervalMs, ProbeIntervalMs);
        AppLogger.Info($"UI卡顿哨兵: 已启动（探测间隔 {ProbeIntervalMs}ms，阈值 {StallThresholdMs:F0}ms）");
    }

    /// <summary>停止哨兵并输出本次运行的卡顿汇总。幂等。</summary>
    public static void Stop()
    {
        Timer? timer = Interlocked.Exchange(ref _timer, null);
        if (timer == null) return;
        timer.Dispose();
        int stalls = Volatile.Read(ref _stallCount);
        double worst = Volatile.Read(ref _worstDelayMs);
        AppLogger.Info($"UI卡顿哨兵: 已停止，本次运行卡顿 {stalls} 次，最长单次 {worst:F0}ms");
    }

    private static void Probe(object? state)
    {
        try
        {
            long pending = Interlocked.Read(ref _pendingSinceTicks);
            if (pending != 0)
            {
                // 上一次心跳还在队列里没被执行 → 主线程正卡着。按已等待时长补一次报告即可。
                double waited = (Stopwatch.GetTimestamp() - pending) * MsPerTick;
                if (waited >= StallThresholdMs && Interlocked.Exchange(ref _reported, 1) == 0)
                {
                    ReportStall(waited, ongoing: true);
                }
                return;
            }

            Interlocked.Exchange(ref _reported, 0);
            Interlocked.Exchange(ref _pendingSinceTicks, Stopwatch.GetTimestamp());
            // Normal 而非 Background：后者要等 UI 线程空闲，会把正常的连续渲染误判成卡顿。
            Dispatcher.UIThread.Post(OnHeartbeat, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            // 哨兵是诊断设施，任何异常都不得影响主程序。
            AppLogger.Warn("UI卡顿哨兵: 探测异常 " + ex.Message);
        }
    }

    private static void OnHeartbeat()
    {
        try
        {
            long postedAt = Interlocked.Exchange(ref _pendingSinceTicks, 0);
            if (postedAt == 0) return;

            double delay = (Stopwatch.GetTimestamp() - postedAt) * MsPerTick;
            TrackWorst(delay);

            if (delay >= StallThresholdMs && Interlocked.Exchange(ref _reported, 1) == 0)
            {
                ReportStall(delay, ongoing: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UI卡顿哨兵: 心跳处理异常 " + ex.Message);
        }
    }

    private static void TrackWorst(double delayMs)
    {
        // 单写多读的松弛竞争即可：这里只要量级正确，不需要严格的最值语义。
        double current = Volatile.Read(ref _worstDelayMs);
        while (delayMs > current)
        {
            double prev = Interlocked.CompareExchange(ref _worstDelayMs, delayMs, current);
            if (prev == current) break;
            current = prev;
        }
    }

    private static void ReportStall(double delayMs, bool ongoing)
    {
        int count = Interlocked.Increment(ref _stallCount);
        AppLogger.Warn($"UI卡顿哨兵: 主线程无响应 {delayMs:F0}ms（{(ongoing ? "仍在持续" : "已恢复")}）"
            + $"，本次运行第 {count} 次，{StartupProfiler.GcSnapshot()}");
    }
}
