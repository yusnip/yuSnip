#nullable disable

using System;
using System.Diagnostics;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// using 语法糖配合的性能计时器。Dispose 时把耗时写入 <see cref="AppLogger"/>。
    /// 用法：<c>using (PerformanceTimer.Measure("阶段名")) { ... }</c>
    /// 阶段 2 搬迁自旧项目，按 D009 改为 <c>public</c>。
    /// </summary>
    public sealed class PerformanceTimer : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _watch;
        private bool _disposed;

        private PerformanceTimer(string name)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name;
            _watch = Stopwatch.StartNew();
        }

        public static PerformanceTimer Measure(string name)
        {
            return new PerformanceTimer(name);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watch.Stop();
            AppLogger.Info("PERF " + _name + " " + _watch.ElapsedMilliseconds + "ms");
        }
    }
}
