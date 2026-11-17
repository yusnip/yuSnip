using System;
using System.Drawing;
using System.Threading;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// MSAA 检测的"快速" worker：UI 线程提交点 → 独立后台线程消化 → 事件回吐结果。
    ///
    /// 解决的问题：
    /// - <see cref="ElementDetector"/> 含同步阻塞 COM 调用（窗口切换时构建元素快照约 60~180ms）
    /// - 鼠标移动每秒数十次回调，直接在 UI 线程上跑会把界面卡死
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

        /// <summary>内部使用的元素检测器（供宿主在 Element 模式之外复用其元素快照缓存）。</summary>
        public ElementDetector Detector => _detector;
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
        private volatile bool _hasDetectedOnce; // 是否至少完成过一次检测（复测需要有效的最新点）

        // 上一次成功结果，用来事件去重。读写都在 worker 线程上，无并发问题。
        private Rectangle[]? _lastResult;

        /// <summary>
        /// 检测结果改变时触发。值为新结果（可能为 null = 当前点没有元素 / 失败 / 超时）。
        ///
        /// **触发线程是 worker 线程**。订阅方需要自己用 Avalonia.Threading.Dispatcher.UIThread.Post
        /// 切回 UI 线程再操作 UI。
        /// </summary>
        public event Action<Rectangle[]?, Point>? ElementChanged;

        /// <summary>最近一次成功的检测结果（可能为 null）。</summary>
        public Rectangle[]? CurrentElement
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
            // 无障碍树预热成功后强制复测最新点：用户悬停不动时，树就绪后也能自动升级到元素级结果
            _detector.WarmupCompleted = OnDetectorWarmupCompleted;
            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ScreenCaptureTool.FastMsaaWorker",
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        /// <summary>
        /// ElementDetector.WarmupCompleted 回调（后台线程触发）：
        /// 无障碍树保活就绪后强制复测最新点，避免用户必须晃动鼠标才能拿到小元素。
        /// </summary>
        private void OnDetectorWarmupCompleted()
        {
            if (_disposed || !_started || !_hasDetectedOnce) return;
            NotifyPointerMovedForce(new Point(_latestX, _latestY));
        }

        public IntPtr IgnoredWindow
        {
            get => _ignoredWindow;
            set => _ignoredWindow = value;
        }

        /// <summary>
        /// 启动 worker。多次调用幂等。
        /// 初始化（COM 会话 + 窗口快照枚举）延迟到 worker 线程首次运行时执行，
        /// 避免在 UI 线程上做全窗口枚举卡住截图遮罩弹出。
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

            _detector.ClearSession(); // 释放并清理整个内存树缓存

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

            // 首次运行：在 worker 线程上初始化检测会话（COM + 窗口快照枚举）
            try
            {
                _detector.InitializeSession(_ignoredWindow);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FastMsaaWorker.InitializeSession failed: " + ex.Message);
            }

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

                if (_isPaused) continue;

                // 限制检测频率：确保两次检测的间隔（上一次完成到这一次开始）至少为 10ms (100fps max)
                long now = Environment.TickCount64;
                long minInterval = 10;
                long elapsedSinceLast = now - _lastDetectTick;
                if (elapsedSinceLast < minInterval)
                {
                    int sleepMs = (int)(minInterval - elapsedSinceLast);
                    if (sleepMs > 0)
                    {
                        try { Thread.Sleep(sleepMs); }
                        catch { return; }

                        // 睡醒后鼠标位置可能变化，重新加载最新的物理像素点坐标，并清除在此期间产生的新 pending
                        x = _latestX;
                        y = _latestY;
                        _hasPending = false;
                    }
                }

                Rectangle[]? result;
                long detectStart = Environment.TickCount64;
                try
                {
                    result = _detector.DetectElementHierarchyAt(new Point(x, y), _ignoredWindow);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("FastMsaaWorker.DetectElementHierarchyAt threw: " + ex.Message);
                    result = null;
                }
                long elapsed = Environment.TickCount64 - detectStart;
                _lastDetectTick = Environment.TickCount64;
                _detectCount++;
                _hasDetectedOnce = true;
                if (elapsed >= 40) _slowDetectCount++;
                if (_lastDetectTick - _lastPerfLogTick >= 1000)
                {
                    AppLogger.Info("AutoDetect.FastMsaa perf: count=" + _detectCount + ", slow=" + _slowDetectCount + ", last=" + elapsed + "ms, result=" + (result != null ? ("chain length=" + result.Length) : "null"));
                    _detectCount = 0;
                    _slowDetectCount = 0;
                    _lastPerfLogTick = _lastDetectTick;
                }

                // 去重：与上次结果完全一致就不重复触发
                bool changed;
                lock (_stateLock)
                {
                    changed = forceResult || !NullableRectArraysEqual(_lastResult, result);
                    if (changed) _lastResult = result;
                }

                if (changed)
                {
                    try { ElementChanged?.Invoke(result, new Point(x, y)); }
                    catch (Exception ex) { AppLogger.Warn("FastMsaaWorker.ElementChanged subscriber threw: " + ex.Message); }
                }
            }
        }

        private static bool NullableRectArraysEqual(Rectangle[]? a, Rectangle[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            try { _detector.WarmupCompleted = null; } catch { }
            try { _cts.Dispose(); } catch { }
            try { _wake.Dispose(); } catch { }
        }
    }
}
