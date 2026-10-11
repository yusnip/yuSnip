using System;
using System.Drawing;
using System.Threading;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// MSAA 检测的"快速" worker：UI 线程提交点 → 独立后台线程消化 → 事件回吐结果。
    ///
    /// 解决的问题：
    /// - <see cref="ElementDetector"/> 是同步阻塞调用（IAccessible COM marshal 数十毫秒级别）
    /// - 鼠标移动每秒数十次回调，直接在 UI 线程上跑会把界面卡死
    /// - 即使 ElementDetector 已带 80ms 超时降级，仍不应在 UI 线程上发起
    ///
    /// 设计要点：
    /// - 一个专用 Thread（不进 ThreadPool，避免污染线程池工作线程）
    /// - 新点覆盖旧点（最新最优策略），不排队
    /// - 结果去重：连续相同矩形不重复触发事件
    /// - 事件在 worker 线程上触发，调用方负责切回 UI 线程
    /// </summary>
    public sealed class FastMsaaWorker : IDisposable
    {
        private readonly ElementDetector _detector;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _stateLock = new object();

        // 共享状态：volatile 让 worker 与生产者之间立即可见。
        // 注意 Point 是值类型（两个 int），volatile 不直接支持 struct，所以拆成两个 int + Pending 标志。
        private volatile int _latestX;
        private volatile int _latestY;
        private volatile bool _hasPending;
        private volatile bool _forceNextResult;
        private volatile bool _started;
        private volatile bool _disposed;
        private volatile bool _isPaused;
        private IntPtr _ignoredWindow;
        private long _lastDetectTick;
        private long _lastPerfLogTick;
        private int _detectCount;
        private int _slowDetectCount;
        private const int MinDetectIntervalMs = 20;

        // 上一次成功结果，用来事件去重。读写都在 worker 线程上，无并发问题。
        private Rectangle? _lastResult;

        /// <summary>
        /// 检测结果改变时触发。值为新结果（可能为 null = 当前点没有元素 / 失败 / 超时）。
        ///
        /// **触发线程是 worker 线程**。订阅方需要自己用 Avalonia.Threading.Dispatcher.UIThread.Post
        /// 切回 UI 线程再操作 UI。
        /// </summary>
        public event Action<Rectangle?, Point>? ElementChanged;

        /// <summary>最近一次成功的检测结果（可能为 null）。</summary>
        public Rectangle? CurrentElement
        {
            get { lock (_stateLock) return _lastResult; }
        }

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                _isPaused = value;
                if (value)
                {
                    lock (_stateLock)
                    {
                        _hasPending = false;
                        _lastResult = null;
                    }
                }
            }
        }

        public FastMsaaWorker(ElementDetector? detector = null)
        {
            _detector = detector ?? new ElementDetector();
            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ScreenCaptureTool.FastMsaaWorker",
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public IntPtr IgnoredWindow
        {
            get => _ignoredWindow;
            set => _ignoredWindow = value;
        }

        /// <summary>
        /// 启动 worker。多次调用幂等。
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FastMsaaWorker));
            if (_started) return;
            _started = true;
            _thread.Start();
        }

        /// <summary>
        /// 停止 worker 并清空"上次结果"。可以在 <see cref="Start"/> 之后任意时刻调，
        /// 调完后允许再次 Start（重新创建 CancellationTokenSource 不在本类范围，
        /// 实际使用方应直接 Dispose 重建）。
        /// </summary>
        public void Stop()
        {
            if (!_started) return;

            try { _cts.Cancel(); } catch { }
            try { _wake.Set(); } catch { }

            // 主循环退出后，清空状态。Join 加超时兜底，避免 Stop 永远不返回。
            try { _thread.Join(500); } catch { }

            lock (_stateLock)
            {
                _hasPending = false;
                _lastResult = null;
            }
        }

        /// <summary>
        /// UI 线程调：提交一个新点。覆盖旧的"待处理点"，零阻塞。
        /// </summary>
        public void NotifyPointerMoved(Point screenPoint)
        {
            NotifyPointerMoved(screenPoint, false);
        }

        public void NotifyPointerMovedForce(Point screenPoint)
        {
            NotifyPointerMoved(screenPoint, true);
        }

        private void NotifyPointerMoved(Point screenPoint, bool forceResult)
        {
            if (_disposed || _cts.IsCancellationRequested) return;

            _latestX = screenPoint.X;
            _latestY = screenPoint.Y;
            if (forceResult) _forceNextResult = true;
            _hasPending = true;

            try { _wake.Set(); } catch { }
        }

        private void WorkerLoop()
        {
            CancellationToken token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // 等下一次提交。无信号就阻塞，不空转。
                try { _wake.WaitOne(); }
                catch { return; }

                if (token.IsCancellationRequested) return;

                // 一次性吃掉所有"在 worker 干活之间堆积的点"，只取最新的。
                if (!_hasPending || _isPaused) continue;

                int x = _latestX;
                int y = _latestY;
                bool forceResult = _forceNextResult;
                _forceNextResult = false;
                _hasPending = false;

                long now = Environment.TickCount64;
                long sinceLastDetect = now - _lastDetectTick;
                if (sinceLastDetect >= 0 && sinceLastDetect < MinDetectIntervalMs)
                {
                    try { _wake.WaitOne(MinDetectIntervalMs - (int)sinceLastDetect); } catch { }
                    if (token.IsCancellationRequested) return;
                    x = _latestX;
                    y = _latestY;
                    forceResult = forceResult || _forceNextResult;
                    _forceNextResult = false;
                    _hasPending = false;
                }

                if (_isPaused) continue;

                Rectangle? result;
                long detectStart = Environment.TickCount64;
                try
                {
                    result = _detector.DetectElementAt(new Point(x, y), _ignoredWindow);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("FastMsaaWorker.DetectElementAt threw: " + ex.Message);
                    result = null;
                }
                long elapsed = Environment.TickCount64 - detectStart;
                _lastDetectTick = Environment.TickCount64;
                _detectCount++;
                if (elapsed >= 40) _slowDetectCount++;
                if (_lastDetectTick - _lastPerfLogTick >= 1000)
                {
                    AppLogger.Info("AutoDetect.FastMsaa perf: count=" + _detectCount + ", slow=" + _slowDetectCount + ", last=" + elapsed + "ms, result=" + (result.HasValue ? result.Value.ToString() : "null"));
                    _detectCount = 0;
                    _slowDetectCount = 0;
                    _lastPerfLogTick = _lastDetectTick;
                }

                // 去重：与上次结果完全一致就不重复触发
                bool changed;
                lock (_stateLock)
                {
                    changed = forceResult || !NullableRectEquals(_lastResult, result);
                    if (changed) _lastResult = result;
                }

                if (changed)
                {
                    try { ElementChanged?.Invoke(result, new Point(x, y)); }
                    catch (Exception ex) { AppLogger.Warn("FastMsaaWorker.ElementChanged subscriber threw: " + ex.Message); }
                }
            }
        }

        private static bool NullableRectEquals(Rectangle? a, Rectangle? b)
        {
            if (!a.HasValue && !b.HasValue) return true;
            if (a.HasValue != b.HasValue) return false;
            return a!.Value == b!.Value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            try { _cts.Dispose(); } catch { }
            try { _wake.Dispose(); } catch { }
        }
    }
}
