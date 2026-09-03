using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// 自动检测控制器：把 <see cref="WindowDetector"/>、<see cref="ElementDetector"/>、
    /// <see cref="FastMsaaWorker"/> 按 <see cref="DetectMode"/> 调度起来。
    ///
    /// 实现 <see cref="IAutoDetectController"/>，UI 层只需调 <see cref="NotifyPointerMoved"/>，
    /// 订阅 <see cref="HighlightChanged"/>，不接触检测内部细节。
    ///
    /// 各 Mode 行为：
    /// <list type="bullet">
    ///   <item><c>None</c>：不做任何检测，CurrentHighlight 永远为 null</item>
    ///   <item><c>Window</c>：同步调 WindowDetector，鼠标移动直接出结果</item>
    ///   <item><c>Element</c>：转发给 worker（异步、独立线程），事件由 worker 触发</item>
    ///   <item><c>Auto</c>：先同步出窗口结果立即推（保证有反馈），同时交给 worker；
    ///         worker 后到的元素结果若非 null 就覆盖窗口结果</item>
    /// </list>
    ///
    /// 事件触发线程：Window/Auto 同步路径在调用方线程；Element/Auto 异步路径在 worker 线程。
    /// 调用方负责切回 UI 线程。
    /// </summary>
    public sealed class AutoDetectController : IAutoDetectController
    {
        private readonly WindowDetector _windowDetector;
        private readonly ChildWindowDetector _childWindowDetector;
        private readonly FastMsaaWorker _worker;
        private readonly object _stateLock = new object();

        private DetectMode _mode = DetectMode.Window;
        private bool _started;
        private bool _disposed;
        private bool _isPaused;
        private Rectangle? _currentHighlight;
        private Point _latestPointer;
        private Rectangle? _latestWindowHighlight;

        private Point _autoStableAnchor;
        private long _autoStableAnchorTick;
        private bool _hasAutoStableAnchor;
        private Rectangle? _autoCandidateElement;
        private int _autoCandidateStableCount;
        private Rectangle? _autoLockedElement;
        private long _autoElementLockedUntil;
        private int _autoStableProbeVersion;
        private long _lastAutoStableProbeTick;

        private const int AutoStableMs = 250;
        private const int AutoStableRadiusPx = 8;
        private const int AutoFastMoveRadiusPx = 20;
        private const int AutoElementLockMs = 400;
        private const int AutoElementLeavePaddingPx = 20;

        /// <inheritdoc />
        public event Action<Rectangle?>? HighlightChanged;

        /// <inheritdoc />
        public Rectangle? CurrentHighlight
        {
            get { lock (_stateLock) return _currentHighlight; }
        }

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                _isPaused = value;
                _worker.IsPaused = value;
                if (value)
                {
                    ClearHighlight();
                    ResetAutoCandidate();
                    ClearAutoElementLock();
                }
            }
        }

        /// <inheritdoc />
        public DetectMode Mode
        {
            get => _mode;
            set
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AutoDetectController));
                if (value == DetectMode.Auto) value = DetectMode.Window;
                if (_mode == value) return;

                _mode = value;

                // 切换模式时立刻清空候选，避免旧 mode 的残留把 UI 停在错误位置
                ClearHighlight();

                ApplyModeToWorker();
            }
        }

        public AutoDetectController(WindowDetector? windowDetector = null, FastMsaaWorker? worker = null)
        {
            _windowDetector = windowDetector ?? new WindowDetector();
            _childWindowDetector = new ChildWindowDetector();
            _worker = worker ?? new FastMsaaWorker();
            _worker.ElementChanged += OnWorkerElementChanged;
        }

        /// <summary>
        /// 暴露给 T4.7 最小宿主：把"我们自己的截图遮罩窗口"加进排除集合，
        /// 避免光标在自己窗口上时把整屏识别为目标窗口。
        /// </summary>
        public WindowDetector WindowDetector => _windowDetector;

        public IntPtr IgnoredWindow
        {
            get => _worker.IgnoredWindow;
            set
            {
                _worker.IgnoredWindow = value;
                _childWindowDetector.IgnoredWindow = value;
            }
        }

        /// <inheritdoc />
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AutoDetectController));
            if (_started) return;
            _started = true;
            ApplyModeToWorker();
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!_started) return;
            _started = false;

            try { _worker.Stop(); } catch { /* 优雅停止失败也不抛 */ }
            ClearHighlight();
        }

        /// <inheritdoc />
        public void NotifyPointerMoved(Point screenPoint)
        {
            if (_disposed || !_started || _isPaused) return;
            _latestPointer = screenPoint;
            UpdateAutoPointerStability(screenPoint);

            switch (_mode)
            {
                case DetectMode.None:
                    return;

                case DetectMode.Window:
                    SetHighlight(_windowDetector.DetectWindowAt(screenPoint));
                    return;

                case DetectMode.Element:
                    _worker.NotifyPointerMoved(screenPoint);
                    return;

                case DetectMode.Auto:
                    // 自动模式：只走轻量 Win32。窗口立即反馈，稳定悬停后才吸附原生子 HWND。
                    ApplyAutoWindowFirst(screenPoint);
                    ProbeAutoChildWindow(screenPoint);
                    ScheduleAutoStableProbe(AutoStableMs + 20);
                    return;
            }
        }

        private void OnWorkerElementChanged(Rectangle? elementRect, Point queryPoint)
        {
            // worker 是后台线程触发；只服务“检测元素”模式。Auto 模式不再使用 MSAA，避免卡顿。
            if (_disposed || !_started || _isPaused || _mode != DetectMode.Element) return;

            if (IsReasonableElementRect(elementRect, queryPoint, null))
            {
                Rectangle fine = elementRect.GetValueOrDefault();
                SetHighlight(fine);
            }
            else
            {
                if (elementRect.HasValue)
                {
                    AppLogger.Warn($"AutoDetectController: Rejected elementRect {elementRect.Value} for queryPoint {queryPoint}. Contains={elementRect.Value.Contains(queryPoint)}");
                }
                SetHighlight(null);
            }
        }

        private void ApplyAutoWindowFirst(Point pointer)
        {
            Rectangle? winRect = _windowDetector.DetectWindowAt(pointer);
            _latestWindowHighlight = winRect;
            if (ShouldKeepLockedElement(pointer))
            {
                SetHighlight(_autoLockedElement);
                return;
            }

            ClearAutoElementLock();
            SetHighlight(winRect);
        }

        private void ProbeAutoChildWindow(Point pointer)
        {
            if (_isPaused || !IsAutoPointerStableForElement(pointer)) return;

            Rectangle? childRect = _childWindowDetector.DetectChildWindowAt(pointer, _latestWindowHighlight);
            if (!childRect.HasValue)
            {
                ResetAutoCandidate();
                return;
            }

            Rectangle candidate = childRect.Value;
            if (_autoCandidateElement.HasValue && AreSimilarRects(_autoCandidateElement.Value, candidate))
            {
                _autoCandidateStableCount++;
            }
            else
            {
                _autoCandidateElement = candidate;
                _autoCandidateStableCount = 1;
                ScheduleAutoStableProbe(70);
            }

            if (_autoCandidateStableCount >= 2)
            {
                LockAutoElement(candidate);
                SetHighlight(candidate);
            }
        }

        private void ResetAutoCandidate()
        {
            _autoCandidateElement = null;
            _autoCandidateStableCount = 0;
        }

        private void ScheduleAutoStableProbe(int delayMs)
        {
            if (_mode != DetectMode.Auto || !_started || _disposed || _isPaused) return;
            int version = ++_autoStableProbeVersion;
            Point scheduledPoint = _latestPointer;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(1, delayMs));
                    if (_disposed || !_started || _isPaused || _mode != DetectMode.Auto) return;
                    if (version != _autoStableProbeVersion) return;
                    if (Distance(scheduledPoint, _latestPointer) > AutoStableRadiusPx) return;
                    long now = Environment.TickCount64;
                    if (now - _lastAutoStableProbeTick < 45) return;
                    _lastAutoStableProbeTick = now;
                    ProbeAutoChildWindow(_latestPointer);
                }
                catch { }
            });
        }

        private void LockAutoElement(Rectangle rect)
        {
            _autoLockedElement = rect;
            _autoElementLockedUntil = Environment.TickCount64 + AutoElementLockMs;
        }

        private void ClearAutoElementLock()
        {
            _autoLockedElement = null;
            _autoElementLockedUntil = 0;
        }

        private bool ShouldKeepLockedElement(Point pointer)
        {
            if (!_autoLockedElement.HasValue) return false;
            Rectangle expanded = ExpandRect(_autoLockedElement.Value, AutoElementLeavePaddingPx);
            if (!expanded.Contains(pointer)) return false;
            long now = Environment.TickCount64;
            if (now <= _autoElementLockedUntil) return true;
            return _autoLockedElement.Value.Contains(pointer);
        }

        private static Rectangle ExpandRect(Rectangle rect, int padding)
        {
            return Rectangle.FromLTRB(rect.Left - padding, rect.Top - padding, rect.Right + padding, rect.Bottom + padding);
        }

        private static bool AreSimilarRects(Rectangle a, Rectangle b)
        {
            double maxDelta = Math.Max(
                Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Top - b.Top)),
                Math.Max(Math.Abs(a.Width - b.Width), Math.Abs(a.Height - b.Height)));
            double areaA = Math.Max(1.0, (double)a.Width * a.Height);
            double areaB = Math.Max(1.0, (double)b.Width * b.Height);
            double ratio = Math.Abs(areaA - areaB) / Math.Max(areaA, areaB);
            return maxDelta <= 10.0 && ratio <= 0.18;
        }

        private void UpdateAutoPointerStability(Point pointer)
        {
            if (_mode != DetectMode.Auto)
            {
                _hasAutoStableAnchor = false;
                return;
            }

            long now = Environment.TickCount64;
            if (!_hasAutoStableAnchor)
            {
                _autoStableAnchor = pointer;
                _autoStableAnchorTick = now;
                _hasAutoStableAnchor = true;
                return;
            }

            double distance = Distance(pointer, _autoStableAnchor);
            if (distance > AutoFastMoveRadiusPx)
            {
                _autoStableAnchor = pointer;
                _autoStableAnchorTick = now;
                ResetAutoCandidate();
                ClearAutoElementLock();
                return;
            }

            if (distance > AutoStableRadiusPx)
            {
                // 小幅移动时慢慢跟随锚点，避免轻微手抖导致稳定计时完全重置。
                _autoStableAnchor = new Point(
                    (int)Math.Round(_autoStableAnchor.X * 0.65 + pointer.X * 0.35),
                    (int)Math.Round(_autoStableAnchor.Y * 0.65 + pointer.Y * 0.35));
                _autoStableAnchorTick = now;
            }
        }

        private bool IsAutoPointerStableForElement(Point pointer)
        {
            if (_mode != DetectMode.Auto || !_hasAutoStableAnchor) return false;
            long age = Environment.TickCount64 - _autoStableAnchorTick;
            if (age < 0 || age < AutoStableMs) return false;
            return Distance(pointer, _autoStableAnchor) <= AutoStableRadiusPx;
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }



        private static bool IsStrongElementRectForAuto(Rectangle? elementRect, Point pointer, Rectangle? windowRect)
        {
            if (!IsReasonableElementRect(elementRect, pointer, windowRect)) return false;
            if (!windowRect.HasValue || windowRect.Value.IsEmpty) return false;

            Rectangle r = elementRect!.Value;
            Rectangle w = windowRect.Value;
            double windowArea = Math.Max(1.0, (double)w.Width * w.Height);
            double elementArea = (double)r.Width * r.Height;

            if (elementArea > windowArea * 0.35) return false;
            if (r.Width > w.Width * 0.70 || r.Height > w.Height * 0.70) return false;
            if (r.Width >= w.Width - 12 || r.Height >= w.Height - 12) return false;
            return true;
        }

        private static bool IsReasonableElementRect(Rectangle? elementRect, Point pointer, Rectangle? windowRect)
        {
            if (!elementRect.HasValue) return false;
            Rectangle r = elementRect.Value;
            if (r.IsEmpty || r.Width < 3 || r.Height < 3) return false;
            if (!r.Contains(pointer)) return false;

            if (windowRect.HasValue && !windowRect.Value.IsEmpty)
            {
                Rectangle w = windowRect.Value;
                double windowArea = Math.Max(1.0, (double)w.Width * w.Height);
                double elementArea = (double)r.Width * r.Height;
                if (elementArea >= windowArea * 0.92) return false;
                if (r.Width > w.Width + 4 || r.Height > w.Height + 4) return false;
            }
            else
            {
                const int hugeWidth = 12000;
                const int hugeHeight = 8000;
                if (r.Width > hugeWidth || r.Height > hugeHeight) return false;
            }

            return true;
        }

        private void ApplyModeToWorker()
        {
            // MSAA worker 只在“检测元素”模式运行；Auto 模式改用轻量 Win32 子窗口检测，避免卡顿。
            bool needWorker = _started && _mode == DetectMode.Element;

            if (needWorker)
            {
                try { _worker.Start(); }
                catch (Exception ex) { AppLogger.Warn("AutoDetectController: worker.Start failed: " + ex.Message); }
            }
            else
            {
                try { _worker.Stop(); }
                catch (Exception ex) { AppLogger.Warn("AutoDetectController: worker.Stop failed: " + ex.Message); }
            }
        }

        private void SetHighlight(Rectangle? value)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = !NullableRectEquals(_currentHighlight, value);
                if (changed) _currentHighlight = value;
            }

            if (changed)
            {
                try { HighlightChanged?.Invoke(value); }
                catch (Exception ex) { AppLogger.Warn("AutoDetectController.HighlightChanged subscriber threw: " + ex.Message); }
            }
        }

        private void ClearHighlight() => SetHighlight(null);

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

            try { _worker.ElementChanged -= OnWorkerElementChanged; } catch { }
            try { _worker.Dispose(); } catch { }
        }
    }
}
