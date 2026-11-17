using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Platform.Accessibility;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// 元素检测器（2026-08-25 快照架构重构）。
    ///
    /// 取代旧的"窗口快照 + ChildWindowFromPointEx + MSAA accHitTest + UIA ElementFromPoint +
    /// 缓存父链回溯"五段混合流水线。旧流水线对 Chromium/Electron 自绘应用系统性失效：
    /// ElementFromPoint 依赖提供方命中下钻，常只返回到窗口/渲染层，即便控件树完整。
    ///
    /// 新流水线只有两步：
    /// 1. 窗口快照定位光标下的顶级窗口（纯内存查找，无穿透副作用）；
    /// 2. 该窗口的 <see cref="ElementSnapshot"/> 内存命中测试，得到"最深层 → 窗口"矩形链。
    ///
    /// 截图会话期间屏幕内容被冻结，元素快照每窗口每会话只构建一次（60~180ms，一次性），
    /// 之后的每次鼠标移动都是纯内存计算（&lt;1ms）。
    ///
    /// 冷无障碍树（Chromium/Electron/Qt 懒激活）：窗口 UIA 子树为空时，
    /// 后台建立 MSAA+UIA 客户端连接促使目标进程建树，随后每 120ms 轮询重建一次
    /// （树就绪立即生效，上限约 2.4s）；成功后通过 <see cref="WarmupCompleted"/> 通知宿主强制复测。
    /// 若仍为空则该窗口本会话按窗口级兜底（与"检测窗口"模式行为一致），不再反复尝试。
    ///
    /// 并发模型：快照构建（跨进程 COM 调用）不持有任何全局锁，按窗口状态机串行；
    /// 某个应用的 provider 挂死只影响该窗口，不波及其他窗口与会话整体。
    /// </summary>
    public sealed class ElementDetector
    {
        // —— 窗口快照 ——
        // 预枚举的可见顶级窗口列表（Z序，前到后），用于替代 WindowFromPoint + WS_EX_TRANSPARENT。
        // 纯内存查找，不修改窗口样式，不会导致鼠标事件穿透遮罩窗口。
        private List<(IntPtr Hwnd, Rectangle Bounds)> _windowSnapshot = new();
        private readonly object _snapshotLock = new();
        private readonly object _snapshotRefreshLock = new();
        private long _lastSnapshotRefreshTick;
        private volatile bool _sessionCleared;

        // —— 元素快照缓存（按窗口） ——
        // 每窗口一个状态机条目：Building(构建在途) / Cold(冷树，激活流程负责重建) / Ready(就绪)。
        // 关键设计：跨进程 COM 构建绝不持有任何锁——某个应用的 UIA provider 挂死时，
        // 只影响它自己那个 hwnd，其他窗口的检测、预热、激活互不阻塞（旧实现用全局
        // _buildLock 串行化所有构建，一个挂死的 provider 会拖垮整个会话的检测并耗尽线程池）。
        private const int StateBuilding = 0;
        private const int StateCold = 1;
        private const int StateReady = 2;

        private sealed class SnapshotEntry
        {
            public int State;
            public ElementSnapshot? Snapshot;
        }

        private readonly Dictionary<IntPtr, SnapshotEntry> _entries = new();
        private readonly object _entriesLock = new(); // 仅保护字典与状态读写，持锁期间禁止 COM 调用

        // —— 冷树激活（保活 + 一次性重建） ——
        private readonly object _warmupLock = new();
        private IntPtr _warmedHwnd;
        private IntPtr _heldAccessible;   // 持有的根 IAccessible（MSAA 客户端连接）
        private IntPtr _heldUiaElement;   // 持有的窗口 IUIAutomationElement（UIA 客户端连接）
        private long _warmupRetryNotBeforeTick; // 激活失败后的重试节流时间点（TickCount64）

        /// <summary>
        /// 冷树激活成功（元素快照已重建）后在后台线程触发。
        /// 供宿主（FastMsaaWorker）强制复测当前点：用户悬停不动时也能自动升级到元素级结果。
        /// </summary>
        public Action? WarmupCompleted { get; set; }

        public void InitializeSession(IntPtr ignoredWindow)
        {
            _sessionCleared = false;
            _ignoredWindowForSnapshot = ignoredWindow;
            RefreshWindowSnapshot();
        }

        public void ClearSession()
        {
            _sessionCleared = true;
            lock (_snapshotLock)
            {
                _windowSnapshot.Clear();
            }
            lock (_entriesLock)
            {
                _entries.Clear();
            }
            lock (_warmupLock)
            {
                ReleaseHoldsNoLock();
                _warmedHwnd = IntPtr.Zero;
            }
        }

        private void ReleaseHoldsNoLock()
        {
            if (_heldAccessible != IntPtr.Zero)
            {
                UIAutomationHelper.ReleaseCOM(_heldAccessible);
                _heldAccessible = IntPtr.Zero;
            }
            if (_heldUiaElement != IntPtr.Zero)
            {
                UIAutomationHelper.ReleaseCOM(_heldUiaElement);
                _heldUiaElement = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 刷新窗口快照：枚举当前所有可见顶级窗口（Z序），缓存其 HWND 和边界。
        /// 在 InitializeSession 时调用一次，整个截图会话期间复用。
        /// </summary>
        public void RefreshWindowSnapshot()
        {
            var next = new List<(IntPtr Hwnd, Rectangle Bounds)>();
            IntPtr ignored = _ignoredWindowForSnapshot;
            foreach (IntPtr hwnd in WindowHelper.EnumerateVisibleTopLevelWindows())
            {
                if (hwnd == IntPtr.Zero || hwnd == ignored) continue;
                Rectangle bounds = WindowHelper.GetWindowBounds(hwnd);
                if (bounds.IsEmpty || bounds.Width <= 2 || bounds.Height <= 2) continue;
                next.Add((hwnd, bounds));
            }
            lock (_snapshotLock)
            {
                _windowSnapshot = next;
            }
        }

        // 遮罩窗口句柄仅用于窗口快照过滤，由 InitializeSession/DetectElementHierarchyAt 记录
        private IntPtr _ignoredWindowForSnapshot;

        /// <summary>
        /// 从窗口快照中查找包含指定屏幕坐标的顶级窗口 HWND（纯内存查找，不调用任何 Win32 命中测试 API）。
        /// 未命中时按需刷新一次快照（会话期间可能弹出新窗口/下拉菜单），刷新有 500ms 节流。
        /// </summary>
        private IntPtr GetHwndFromSnapshot(Point screenPoint, IntPtr ignoredWindow)
        {
            IntPtr hwnd = LookupSnapshot(screenPoint, ignoredWindow);
            if (hwnd != IntPtr.Zero) return hwnd;

            // 未命中：可能快照已过期（新弹窗口），节流刷新后重查一次
            long now = Environment.TickCount64;
            bool shouldRefresh = false;
            lock (_snapshotRefreshLock)
            {
                if (now - _lastSnapshotRefreshTick >= 500)
                {
                    _lastSnapshotRefreshTick = now;
                    shouldRefresh = true;
                }
            }
            if (!shouldRefresh) return IntPtr.Zero;

            RefreshWindowSnapshot();
            return LookupSnapshot(screenPoint, ignoredWindow);
        }

        private IntPtr LookupSnapshot(Point screenPoint, IntPtr ignoredWindow)
        {
            lock (_snapshotLock)
            {
                foreach (var item in _windowSnapshot)
                {
                    if (item.Hwnd == ignoredWindow) continue;
                    if (item.Bounds.Contains(screenPoint)) return item.Hwnd;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 获取屏幕物理像素点处的 UI 元素矩形链。链首为最深层元素，链尾为顶级窗口。
        /// 由 FastMsaaWorker 的专用 STA 线程调用（Auto 模式的稳定探测也可能从线程池调用，
        /// 两条路径不会同时活跃；同一窗口的并发构建由按窗口状态机去重）。
        /// 窗口快照未初始化时按需自愈（首次未命中即刷新），因此不依赖 InitializeSession。
        /// 元素快照不可用时退化为窗口级单元素链（与"检测窗口"模式一致，保证有反馈）。
        /// </summary>
        public Rectangle[]? DetectElementHierarchyAt(Point screenPoint, IntPtr ignoredWindow)
        {
            try
            {
                _ignoredWindowForSnapshot = ignoredWindow;

                IntPtr hwnd = GetHwndFromSnapshot(screenPoint, ignoredWindow);
                if (hwnd == IntPtr.Zero) return null;

                ElementSnapshot? snap = GetOrBuildSnapshot(hwnd);
                Rectangle[]? chain = snap?.HitTestChain(screenPoint);
                if (chain != null && chain.Length > 0)
                    return chain;

                // 兜底：窗口级单元素链（快照为空/未命中时的降级反馈）
                Rectangle bounds = WindowHelper.GetWindowBounds(hwnd);
                if (!bounds.IsEmpty && bounds.Contains(screenPoint))
                    return new Rectangle[] { bounds };
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ElementDetector.DetectElementHierarchyAt failed: " + ex.Message);
                return null;
            }
        }

        // 塌缩树识别阈值：Chromium/Electron/Qt 窗口被遮挡时 UIA 子树会塌缩成仅剩
        // 框架按钮的个位数节点（实测 WorkBuddy 被遮挡时 159→9 节点），与"无树"同等对待
        private const int MinUsefulNodes = 10;

        /// <summary>
        /// 获取（或构建）指定窗口的元素快照。三种情形：
        /// - Ready：直接返回内存快照（&lt;1ms）；
        /// - Building / Cold：立即返回 null（调用方退回窗口级兜底，绝不阻塞等待——
        ///   构建在途只意味着几十到几百毫秒后自然就绪，冷树由激活流程重建）；
        /// - 首次见到：认领构建权后在锁外同步构建一次（60~180ms，调用方是 worker/预热线程）。
        /// 构建完成（Ready）时触发 <see cref="WarmupCompleted"/> 让宿主复测当前点，
        /// 悬停不动的鼠标也能在快照就绪后自动升级到元素级结果。
        /// </summary>
        private ElementSnapshot? GetOrBuildSnapshot(IntPtr hwnd)
        {
            lock (_entriesLock)
            {
                if (_entries.TryGetValue(hwnd, out SnapshotEntry? existing))
                {
                    return existing.State == StateReady ? existing.Snapshot : null;
                }
                _entries[hwnd] = new SnapshotEntry { State = StateBuilding };
            }

            // 锁外构建：跨进程 COM 调用不持有任何锁，单个 provider 挂死不波及其他窗口
            long start = Environment.TickCount64;
            Rectangle winRect = WindowHelper.GetWindowBounds(hwnd);
            ElementSnapshot? snap = null;
            try { snap = ElementSnapshot.Build(hwnd, winRect); }
            catch (Exception ex) { AppLogger.Warn("ElementDetector build threw: " + ex.Message); }
            long elapsed = Environment.TickCount64 - start;

            bool useful = snap != null && snap.Rects.Length >= MinUsefulNodes;
            lock (_entriesLock)
            {
                if (_sessionCleared)
                {
                    _entries.Remove(hwnd);
                    return null;
                }
                SnapshotEntry entry = _entries[hwnd];
                entry.State = useful ? StateReady : StateCold;
                entry.Snapshot = useful ? snap : null;
            }

            if (useful)
            {
                // 树已建成：不再需要保活连接，立即释放
                lock (_warmupLock) { ReleaseHoldsNoLock(); }
                AppLogger.Info("ElementDetector: snapshot hwnd=" + hwnd +
                    " nodes=" + snap!.Rects.Length + " build=" + elapsed + "ms");
                try { WarmupCompleted?.Invoke(); } catch { /* 宿主回调异常不影响构建 */ }
                return snap;
            }

            AppLogger.Info("ElementDetector: empty/collapsed a11y tree for hwnd=" + hwnd +
                " (nodes=" + (snap?.Rects.Length ?? 0) + ", " + elapsed + "ms, activation scheduled)");
            StartActivation(hwnd, winRect);
            return null;
        }

        /// <summary>
        /// 遮罩显示前预热（「检测元素」模式专用，必须在截图遮罩 Show 之前调用）：
        /// QQ 等应用的无障碍树会在遮罩出现（失焦/被覆盖）后立即塌缩，实测遮罩出现后
        /// 331 节点塌缩为 8 节点且会话内无法重建——因此必须在遮罩显示前抓取快照。
        /// 1) 同步构建光标下窗口的快照（用户指向哪里，最可能要截哪里）；
        /// 2) 其余可见顶级窗口后台并行构建（与"树塌缩"赛跑，尽力而为，不阻塞遮罩显示）。
        /// 已在缓存中的窗口自动跳过。
        /// </summary>
        public void PrewarmSnapshots(Point screenPoint)
        {
            List<(IntPtr Hwnd, Rectangle Bounds)> windows;
            try
            {
                windows = new List<(IntPtr, Rectangle)>();
                foreach (IntPtr hwnd in WindowHelper.EnumerateVisibleTopLevelWindows())
                {
                    if (hwnd == IntPtr.Zero) continue;
                    Rectangle bounds = WindowHelper.GetWindowBounds(hwnd);
                    if (bounds.IsEmpty || bounds.Width <= 2 || bounds.Height <= 2) continue;
                    windows.Add((hwnd, bounds));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ElementDetector.PrewarmSnapshots enum failed: " + ex.Message);
                return;
            }

            // 光标下窗口（Z序最前且包含光标的第一个）优先同步构建
            IntPtr cursorHwnd = IntPtr.Zero;
            foreach (var w in windows)
            {
                if (w.Bounds.Contains(screenPoint)) { cursorHwnd = w.Hwnd; break; }
            }
            if (cursorHwnd != IntPtr.Zero)
            {
                AppLogger.Info("ElementDetector: prewarm cursor window hwnd=" + cursorHwnd);
                SafePrewarmBuild(cursorHwnd);
            }

            // 其余窗口后台并行构建（不等待）
            foreach (var w in windows)
            {
                if (w.Hwnd == cursorHwnd) continue;
                IntPtr hwnd = w.Hwnd;
                _ = Task.Run(() => SafePrewarmBuild(hwnd));
            }
        }

        private void SafePrewarmBuild(IntPtr hwnd)
        {
            try { GetOrBuildSnapshot(hwnd); }
            catch (Exception ex) { AppLogger.Warn("ElementDetector prewarm build failed: " + ex.Message); }
        }

        /// <summary>
        /// 冷树激活：对目标窗口建立 MSAA + UIA 客户端连接（读屏器探测路径，促使
        /// Chromium/Electron/Qt 构建懒激活的无障碍树），然后**轮询重建**——每 120ms
        /// 尝试一次，树就绪立即收网（懒激活建树通常 100~300ms，感知延迟从旧实现的
        /// 固定 600ms+ 降至树实际就绪时间），上限约 2.5s 后放弃本会话重建。
        /// 重建成功触发 <see cref="WarmupCompleted"/> 让宿主强制复测。
        /// </summary>
        private void StartActivation(IntPtr hwnd, Rectangle winRect)
        {
            lock (_warmupLock)
            {
                if (hwnd == _warmedHwnd) return; // 已在激活流程中
                if (Environment.TickCount64 < _warmupRetryNotBeforeTick) return;
                _warmedHwnd = hwnd;
            }

            _ = Task.Run(async () =>
            {
                IntPtr pAcc = IntPtr.Zero;
                IntPtr pUia = IntPtr.Zero;
                try
                {
                    // 1) MSAA：AccessibleObjectFromWindow(OBJID_CLIENT) 是读屏器的经典探测路径
                    pAcc = AccessibilityHelper.HoldAccessibleForWindow(hwnd);
                    // 2) UIA：ElementFromHandle 建立客户端连接
                    pUia = UIAutomationHelper.ElementFromHandle(hwnd);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ElementDetector.StartActivation threw: " + ex.Message);
                }

                // 交换持有引用：仅当目标窗口仍是当前窗口且会话未结束时存储
                bool store;
                lock (_warmupLock)
                {
                    store = !_sessionCleared && _warmedHwnd == hwnd;
                    if (store)
                    {
                        ReleaseHoldsNoLock();
                        _heldAccessible = pAcc;
                        _heldUiaElement = pUia;
                    }
                }
                if (!store)
                {
                    if (pAcc != IntPtr.Zero) UIAutomationHelper.ReleaseCOM(pAcc);
                    if (pUia != IntPtr.Zero) UIAutomationHelper.ReleaseCOM(pUia);
                    return;
                }

                AppLogger.Info("ElementDetector: a11y keep-alive for hwnd " + hwnd +
                    " (msaa=" + (pAcc != IntPtr.Zero) + ", uia=" + (pUia != IntPtr.Zero) + ")");

                if (pAcc == IntPtr.Zero && pUia == IntPtr.Zero)
                {
                    // 两个客户端连接都拿不到：清回 Zero 允许重试，500ms 节流防抖动
                    lock (_warmupLock)
                    {
                        if (_warmedHwnd == hwnd) _warmedHwnd = IntPtr.Zero;
                        _warmupRetryNotBeforeTick = Environment.TickCount64 + 500;
                    }
                    return;
                }

                // 轮询重建：树一旦建成立即收网，避免旧实现"固定等 600ms"造成的感知延迟
                const int maxAttempts = 20; // 20 × 120ms ≈ 2.4s 上限
                bool upgraded = false;
                for (int attempt = 0; attempt < maxAttempts && !_sessionCleared; attempt++)
                {
                    try { await Task.Delay(120); }
                    catch { return; }

                    // 可能已被其他路径（预热/悬停构建）升级为 Ready
                    bool alreadyReady;
                    lock (_entriesLock)
                    {
                        alreadyReady = !_entries.TryGetValue(hwnd, out SnapshotEntry? e) || e.State == StateReady;
                    }
                    if (alreadyReady) break;

                    ElementSnapshot? snap = null;
                    try { snap = ElementSnapshot.Build(hwnd, winRect); }
                    catch (Exception ex) { AppLogger.Warn("ElementDetector activation rebuild threw: " + ex.Message); }

                    if (snap != null && snap.Rects.Length >= MinUsefulNodes && !_sessionCleared)
                    {
                        lock (_entriesLock)
                        {
                            // 仅当该窗口仍是冷树状态时替换（避免覆盖更新的快照）
                            if (_entries.TryGetValue(hwnd, out SnapshotEntry? e) && e.State == StateCold)
                            {
                                e.State = StateReady;
                                e.Snapshot = snap;
                                upgraded = true;
                            }
                        }
                        if (upgraded)
                        {
                            AppLogger.Info("ElementDetector: cold tree activated, hwnd=" + hwnd +
                                " nodes=" + snap.Rects.Length + " attempt=" + (attempt + 1));
                            try { WarmupCompleted?.Invoke(); }
                            catch { /* 宿主回调异常不影响激活本身 */ }
                        }
                        break;
                    }
                }

                // 激活流程结束：释放保活连接，复位以允许本会话后续必要时再次激活
                lock (_warmupLock)
                {
                    ReleaseHoldsNoLock();
                    if (_warmedHwnd == hwnd) _warmedHwnd = IntPtr.Zero;
                }
            });
        }
    }
}
