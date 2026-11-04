using System;
using System.Drawing;
using System.Threading.Tasks;
using ScreenCaptureTool.Platform.Accessibility;

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
        private Rectangle[]? _activeElementChain;
        private int _currentChainIndex;

        private Point _autoStableAnchor;
        private long _autoStableAnchorTick;
        private bool _hasAutoStableAnchor;
        private Rectangle? _autoCandidateElement;
        private int _autoCandidateStableCount;
        private int _autoNoChildStableCount; // 稳定探测中连续无子窗口结果的次数（UIA 单点探测门控）
        private Rectangle? _autoLockedElement;
        private long _autoElementLockedUntil;
        private int _autoStableProbeVersion;
        private long _lastAutoStableProbeTick;

        // Auto 模式候选/锁定状态锁：候选与锁定原本只在 UI 线程访问，
        // UIA 单点探测（后台线程）加入后需要跨线程保护这些字段
        private readonly object _autoStateLock = new object();

        // 鼠标速度相关字段
        private Point _lastPointerForSpeed;
        private long _lastPointerTickForSpeed;
        private double _pointerSpeedPxPerMs;

        // Element 模式悬停检测（dwell）：移动期间不提交检测点（高亮冻结），
        // 指针停稳 ElementDwellMs 后才做一次元素检测，消除鼠标划过时的暗遮罩闪烁
        private int _elementDwellVersion;
        private Point _elementLastMovePoint;
        private bool _elementHasLastMovePoint;
        // 层级保持仅服务 +/- 手动切换：用户手动调整过层级后，新检测结果若仍包含
        // 当前高亮则保持该层级；否则（包括检测默认的最深层）总是跟随最新结果，
        // 否则悬停进入大容器内部的小元素时会被旧容器高亮锁死、无法细化
        private bool _elementChainManual;

        private const int AutoStableMs = 250;
        private const int AutoStableRadiusPx = 8;
        private const int AutoFastMoveRadiusPx = 20;
        private const int AutoElementLockMs = 400;
        private const int AutoElementLeavePaddingPx = 20;

        // Element 悬停检测参数（用户可配，AppSettings.ElementDwellMs / ElementDwellDeadbandPx）：
        // 停稳判定时长；死区半径（小于该距离的移动不重置悬停计时）
        public int ElementDwellMs { get; set; } = 10;
        public double ElementDwellDeadbandPx { get; set; } = 20.0;

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
                    ResetElementDwell();
                    _activeElementChain = null;
                    _currentChainIndex = 0;
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
                if (_mode == value) return;

                _mode = value;

                // 切换模式时立刻清空候选，避免旧 mode 的残留把 UI 停在错误位置
                ClearHighlight();
                ResetElementDwell();

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

        /// <summary>
        /// 遮罩显示前预热元素快照（仅「检测元素」模式有意义）：
        /// QQ 等应用的无障碍树会在遮罩出现（失焦/被覆盖）后立即塌缩且会话内无法重建，
        /// 必须在 CaptureWindow.Show() 之前抓取。可在后台线程调用。
        /// </summary>
        public void PrewarmElementSnapshots(Point screenPoint)
        {
            if (_disposed || _mode != DetectMode.Element) return;
            try { _worker.Detector.PrewarmSnapshots(screenPoint); }
            catch (Exception ex) { AppLogger.Warn("AutoDetectController.PrewarmElementSnapshots failed: " + ex.Message); }
        }

        /// <summary>
        /// 遮罩窗口 Show() 之前同步预取光标处的初始高亮：
        /// Window/Auto 模式走同步 Win32 窗口检测；Element 模式复用预热快照同步检测
        /// （<paramref name="allowElementDetect"/> 为 false 时跳过，避免冷快照同步构建阻塞 UI 线程）。
        /// <see cref="HighlightChanged"/> 在调用方线程同步触发，
        /// 让首个呈现帧即带高亮挖洞，消除"先全屏暗、高亮后到"的开场闪烁。
        /// </summary>
        public void PrefetchInitialHighlight(Point screenPoint, bool allowElementDetect)
        {
            if (_disposed) return;
            try
            {
                switch (_mode)
                {
                    case DetectMode.Window:
                    case DetectMode.Auto:
                        // Auto 模式首帧只取窗口级结果，子窗口/元素级吸附仍交给后续稳定探测
                        SetHighlight(_windowDetector.DetectWindowAt(screenPoint));
                        break;

                    case DetectMode.Element:
                        if (!allowElementDetect) break;
                        // 复用预热快照同步检测；结果进链（链首 = 最深层），与停稳后的正式检测保持同一语义
                        Rectangle[]? chain = _worker.Detector.DetectElementHierarchyAt(screenPoint, IgnoredWindow);
                        if (chain != null && chain.Length > 0)
                        {
                            _activeElementChain = chain;
                            _currentChainIndex = 0;
                            _elementChainManual = false;
                            SetHighlight(chain[0]);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AutoDetectController.PrefetchInitialHighlight failed: " + ex.Message);
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
            ResetElementDwell();
            _activeElementChain = null;
            _currentChainIndex = 0;
        }

        /// <inheritdoc />
        public void NotifyPointerMoved(Point screenPoint)
        {
            if (_disposed || !_started || _isPaused) return;
            _latestPointer = screenPoint;

            // 计算鼠标移动的瞬时速度，用于判定是否快速划过
            long now = Environment.TickCount64;
            if (_lastPointerTickForSpeed != 0)
            {
                double dt = Math.Max(1.0, now - _lastPointerTickForSpeed);
                double dx = screenPoint.X - _lastPointerForSpeed.X;
                double dy = screenPoint.Y - _lastPointerForSpeed.Y;
                _pointerSpeedPxPerMs = Math.Sqrt(dx * dx + dy * dy) / dt;
            }
            _lastPointerForSpeed = screenPoint;
            _lastPointerTickForSpeed = now;

            UpdateAutoPointerStability(screenPoint);

            switch (_mode)
            {
                case DetectMode.None:
                    return;

                case DetectMode.Window:
                    SetHighlight(_windowDetector.DetectWindowAt(screenPoint));
                    return;

                case DetectMode.Element:
                    // 悬停才检测：移动期间不提交检测点（高亮冻结），
                    // 指针停稳 ElementDwellMs 后做一次检测并应用结果
                    UpdateElementDwell(screenPoint);
                    return;

                case DetectMode.Auto:
                    // 自动模式：只走轻量 Win32。窗口立即反馈，稳定悬停后才吸附原生子 HWND。
                    ApplyAutoWindowFirst(screenPoint);
                    ProbeAutoChildWindow(screenPoint);
                    ScheduleAutoStableProbe(AutoStableMs + 20);
                    return;
            }
        }

        private void OnWorkerElementChanged(Rectangle[]? elementChain, Point queryPoint)
        {
            // worker 是后台线程触发；只服务“检测元素”模式。
            // 悬停才检测模型下结果只在指针停稳后到来，直接应用即可。
            // 层级保持只在用户手动 +/- 调整过层级时生效（当前高亮仍在新链中则保持）；
            // 未手动调整时总是应用最新链的最深层，保证能从大容器细化到内部小元素。
            if (_disposed || !_started || _isPaused || _mode != DetectMode.Element) return;

            if (elementChain != null && elementChain.Length > 0)
            {
                Rectangle? current;
                lock (_stateLock) { current = _currentHighlight; }
                if (_elementChainManual && current.HasValue)
                {
                    for (int i = 0; i < elementChain.Length; i++)
                    {
                        if (AreSimilarRects(elementChain[i], current.Value))
                        {
                            _activeElementChain = elementChain;
                            _currentChainIndex = i;
                            return;
                        }
                    }
                }

                _elementChainManual = false;
                _activeElementChain = elementChain;
                _currentChainIndex = 0; // 默认选中最深层子元素
                SetHighlight(elementChain[0]);
            }
            else
            {
                _activeElementChain = null;
                _currentChainIndex = 0;
                SetHighlight(null);
            }
        }

        /// <summary>
        /// Element 模式悬停调度：移动事件只负责（重）启动停稳计时；
        /// 指针停稳 <see cref="ElementDwellMs"/> 且期间无超出死区的移动时，
        /// 才向 worker 提交一次检测点。移动期间高亮保持冻结，暗遮罩不闪烁。
        /// </summary>
        private void UpdateElementDwell(Point screenPoint)
        {
            // 死区过滤：与上次移动距离小于死区半径视为手抖，不重置悬停计时
            if (_elementHasLastMovePoint && Distance(_elementLastMovePoint, screenPoint) < ElementDwellDeadbandPx)
                return;
            _elementHasLastMovePoint = true;
            _elementLastMovePoint = screenPoint;

            int version = ++_elementDwellVersion;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ElementDwellMs);
                    if (_disposed || !_started || _isPaused || _mode != DetectMode.Element) return;
                    if (version != _elementDwellVersion) return; // 期间又发生移动，由最新计时接管

                    // 停稳：提交检测点（worker 侧有结果去重，同一元素不会重复触发事件）
                    _worker.NotifyPointerMoved(_latestPointer);
                }
                catch { }
            });
        }

        private void ApplyAutoWindowFirst(Point pointer)
        {
            Rectangle? winRect = _windowDetector.DetectWindowAt(pointer);
            lock (_autoStateLock) { _latestWindowHighlight = winRect; }

            Rectangle? keep = null;
            lock (_autoStateLock)
            {
                if (ShouldKeepLockedElementLocked(pointer)) keep = _autoLockedElement;
            }
            if (keep.HasValue)
            {
                SetHighlight(keep);
                return;
            }

            ClearAutoElementLock();
            SetHighlight(winRect);
        }

        private void ProbeAutoChildWindow(Point pointer)
        {
            if (_isPaused || !IsAutoPointerStableForElement(pointer)) return;

            Rectangle? winRect;
            lock (_autoStateLock) { winRect = _latestWindowHighlight; }
            Rectangle? childRect = _childWindowDetector.DetectChildWindowAt(pointer, winRect);
            if (!childRect.HasValue)
            {
                ResetAutoCandidate();

                // 单 HWND 渲染应用（QQ/Chrome 等 Chromium 系）没有可吸附的原生子窗口：
                // 连续 2 次稳定探测都无子窗口结果后，做一次 UIA 单点探测（不建链）尝试拿小元素
                bool probeUia = false;
                lock (_autoStateLock)
                {
                    if (++_autoNoChildStableCount >= 2)
                    {
                        _autoNoChildStableCount = 0;
                        probeUia = true;
                    }
                }
                if (probeUia)
                {
                    // UIA COM 查询放线程池线程执行，避免阻塞 UI 线程
                    _ = Task.Run(() => ProbeAutoUiaElement(pointer));
                }

                // 维持稳定探测循环：静止的鼠标不再产生移动事件，需主动重排下一次探测，
                // 无子窗口计数才能累积、UIA 命中后的锁定也才能周期性刷新
                ScheduleAutoStableProbe(100);
                return;
            }

            Rectangle candidate = childRect.Value;
            bool reschedule = false;
            bool lockNow = false;
            lock (_autoStateLock)
            {
                if (_autoCandidateElement.HasValue && AreSimilarRects(_autoCandidateElement.Value, candidate))
                {
                    _autoCandidateStableCount++;
                }
                else
                {
                    _autoCandidateElement = candidate;
                    _autoCandidateStableCount = 1;
                    reschedule = true;
                }
                lockNow = _autoCandidateStableCount >= 2;
            }
            if (reschedule) ScheduleAutoStableProbe(70);
            if (lockNow)
            {
                LockAutoElement(candidate);
                SetHighlight(candidate);
            }
        }

        /// <summary>
        /// Auto 模式的 UIA 单点探测：为单 HWND 渲染应用（Chromium 系）补充元素级吸附。
        /// 复用 <see cref="ElementDetector"/> 的元素快照（替代旧的 ElementFromPoint 单点查询——
        /// 后者对 Chromium 系常只返回到窗口/渲染层）；命中后经 <see cref="IsStrongElementRectForAuto"/>
        /// 过滤、复用候选稳定计数（连续 2 次相似）后锁定，与 Win32 子窗口吸附路径行为一致。
        /// </summary>
        private void ProbeAutoUiaElement(Point pointer)
        {
            if (_disposed || !_started || _isPaused || _mode != DetectMode.Auto) return;
            if (!IsAutoPointerStableForElement(pointer)) return; // 探测期间鼠标已移走则放弃

            Rectangle? uiaRect = null;
            try
            {
                Rectangle[]? chain = _worker.Detector.DetectElementHierarchyAt(pointer, IgnoredWindow);
                if (chain != null && chain.Length > 0)
                    uiaRect = chain[0]; // 链首 = 最深层元素
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AutoDetectController: auto UIA probe threw: " + ex.Message);
            }

            Rectangle? winRect;
            lock (_autoStateLock) { winRect = _latestWindowHighlight; }
            if (!uiaRect.HasValue || !IsStrongElementRectForAuto(uiaRect, pointer, winRect)) return;

            Rectangle candidate = uiaRect.Value;
            bool lockNow = false;
            lock (_autoStateLock)
            {
                if (_autoCandidateElement.HasValue && AreSimilarRects(_autoCandidateElement.Value, candidate))
                {
                    _autoCandidateStableCount++;
                }
                else
                {
                    _autoCandidateElement = candidate;
                    _autoCandidateStableCount = 1;
                }
                lockNow = _autoCandidateStableCount >= 2;
            }
            if (lockNow)
            {
                LockAutoElement(candidate);
                SetHighlight(candidate);
            }
        }

        private void ResetAutoCandidate()
        {
            lock (_autoStateLock)
            {
                _autoCandidateElement = null;
                _autoCandidateStableCount = 0;
            }
        }

        /// <summary>清空 Element 模式悬停检测状态（取消进行中的停稳计时与手动层级标记）。</summary>
        private void ResetElementDwell()
        {
            _elementDwellVersion++;
            _elementHasLastMovePoint = false;
            _elementChainManual = false;
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
            lock (_autoStateLock)
            {
                _autoLockedElement = rect;
                _autoElementLockedUntil = Environment.TickCount64 + AutoElementLockMs;
            }
        }

        private void ClearAutoElementLock()
        {
            lock (_autoStateLock)
            {
                _autoLockedElement = null;
                _autoElementLockedUntil = 0;
            }
        }

        /// <summary>调用方必须已持有 _autoStateLock。</summary>
        private bool ShouldKeepLockedElementLocked(Point pointer)
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
                lock (_autoStateLock) { _autoNoChildStableCount = 0; }
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

        public void ChangeHighlightLevel(int delta)
        {
            if (_disposed || !_started || _isPaused || _mode != DetectMode.Element) return;

            lock (_stateLock)
            {
                if (_activeElementChain == null || _activeElementChain.Length == 0) return;

                int nextIndex = Math.Clamp(_currentChainIndex + delta, 0, _activeElementChain.Length - 1);
                if (nextIndex != _currentChainIndex)
                {
                    _currentChainIndex = nextIndex;
                    _elementChainManual = true; // 手动切换层级：后续检测结果保持该层级（当前高亮仍在链中时）
                    SetHighlight(_activeElementChain[_currentChainIndex]);
                }
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
