#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 启动里程碑计时器：以「进程起点」为 0 点，记录各阶段到达时刻与相邻阶段的间隔。
    ///
    /// <para><b>为什么需要它</b>：<see cref="AppLogger"/> 的第一条日志出现在
    /// <c>EmbeddedNativeLoader.Initialize()</c> 之后——而那一步会校验并按需解压近 90MB 原生 dll，
    /// 此前完全没有埋点，是启动耗时的盲区。本类的 0 点在静态字段初始化时确定，
    /// 早于 <c>Main</c> 里任何一行用户代码，故能把这段盲区也纳入测量。</para>
    ///
    /// <para><b>与 <see cref="PerformanceTimer"/> 的分工</b>：PerformanceTimer 量「一段代码跑了多久」
    /// （using 语法糖，Dispose 时立即落一条日志）；StartupProfiler 量「这个时刻距进程启动多久」
    /// （里程碑序列，最后汇总成一行）。两者互补，不要互相替代。</para>
    ///
    /// <para><b>正式版也能读到</b>：<see cref="AppLogger"/> 的内存环形缓冲在所有构建都开启，
    /// 设置「日志」页即可查看全部 PERF 行，无需依赖 Debug 的文件落盘。</para>
    /// </summary>
    public static class StartupProfiler
    {
        // 静态字段初始化早于 Main 的第一行用户代码，作为全进程统一 0 点。
        private static readonly long OriginTimestamp = Stopwatch.GetTimestamp();
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        // 里程碑数量有上限：正常启动只有十几条，上限只为防止热路径误用导致无界增长。
        private const int MaxStages = 256;

        private static readonly object SyncRoot = new object();
        private static readonly List<Stage> _stages = new List<Stage>(32);
        private static double _lastAtMs;

        private struct Stage
        {
            public string Name;
            public double AtMs;     // 距进程起点
            public double DeltaMs;  // 距上一个里程碑
        }

        /// <summary>距进程起点已过去的毫秒数（高精度，基于 QueryPerformanceCounter）。</summary>
        public static double ElapsedMs
        {
            get { return (Stopwatch.GetTimestamp() - OriginTimestamp) * MsPerTick; }
        }

        /// <summary>
        /// 记录一个里程碑：落一条 <c>PERF Startup {名称} at={距启动}ms (+{距上一点}ms)</c> 日志，
        /// 并把该点存入序列供 <see cref="Summary"/> 汇总。
        /// </summary>
        public static void Mark(string name)
        {
            if (!AppLogger.DetailedLoggingEnabled) return;
            try
            {
                double at = ElapsedMs;
                double delta;
                lock (SyncRoot)
                {
                    delta = at - _lastAtMs;
                    _lastAtMs = at;
                    if (_stages.Count < MaxStages)
                    {
                        _stages.Add(new Stage { Name = name, AtMs = at, DeltaMs = delta });
                    }
                }

                AppLogger.Info("PERF Startup " + name
                    + " at=" + at.ToString("F0") + "ms (+" + delta.ToString("F0") + "ms)");
            }
            catch
            {
                // 埋点失败绝不能影响启动流程。
            }
        }

        /// <summary>
        /// 汇总全部里程碑为一行日志，便于一眼看出哪一段最贵。
        /// 同时附上 GC 代数与内存水位，用于判断启动期是否已触发过回收。
        /// </summary>
        public static void Summary()
        {
            if (!AppLogger.DetailedLoggingEnabled)
            {
                AppLogger.Info("PERF StartupSummary total=" + ElapsedMs.ToString("F0") + "ms");
                return;
            }
            try
            {
                Stage[] snapshot;
                lock (SyncRoot)
                {
                    snapshot = _stages.ToArray();
                }

                if (snapshot.Length == 0) return;

                var sb = new StringBuilder(256);
                sb.Append("PERF StartupSummary total=");
                sb.Append(ElapsedMs.ToString("F0"));
                sb.Append("ms |");
                for (int i = 0; i < snapshot.Length; i++)
                {
                    sb.Append(' ');
                    sb.Append(snapshot[i].Name);
                    sb.Append('=');
                    sb.Append(snapshot[i].DeltaMs.ToString("F0"));
                    sb.Append("ms");
                }

                AppLogger.Info(sb.ToString());
                AppLogger.Info("PERF StartupSummary " + GcSnapshot());
            }
            catch
            {
                // 同上：埋点失败不影响主流程。
            }
        }

        /// <summary>
        /// 当前 GC 代数与内存水位快照。用于量化「一次截图触发了几次 Gen2、LOH 涨了多少」，
        /// 以及压缩式回收的真实代价。全部走零分配 API（<see cref="Environment.WorkingSet"/>），
        /// 不用 <c>Process.GetCurrentProcess()</c> 以免每次调用都新建一个 Process 对象。
        /// </summary>
        public static string GcSnapshot()
        {
            try
            {
                const double MB = 1048576.0;
                return "gc0=" + GC.CollectionCount(0)
                    + " gc1=" + GC.CollectionCount(1)
                    + " gc2=" + GC.CollectionCount(2)
                    + " lo=" + (GC.GetTotalMemory(false) / MB).ToString("F1") + "MB"
                    + " ws=" + (Environment.WorkingSet / MB).ToString("F0") + "MB";
            }
            catch
            {
                return "gc=unavailable";
            }
        }
    }
}
