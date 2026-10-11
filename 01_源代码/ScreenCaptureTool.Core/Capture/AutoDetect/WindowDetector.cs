using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// 粗粒度自动检测：返回光标下"顶级窗口"的边界（屏幕物理像素）。
    ///
    /// 与 <see cref="ElementDetector"/>（细粒度，UI 元素）配对，由 <c>AutoDetectController</c>
    /// 在不同 <see cref="DetectMode"/> 下选择启用。
    ///
    /// 本类无可变状态、线程安全（除 <see cref="ExcludedHwnds"/> 集合本身的线程安全外）。
    /// </summary>
    public sealed class WindowDetector
    {
        private sealed class WindowSnapshotItem
        {
            public required IntPtr Hwnd { get; init; }
            public required Rectangle Bounds { get; init; }
        }

        private readonly object _snapshotLock = new object();
        private List<WindowSnapshotItem> _windowSnapshot = new List<WindowSnapshotItem>();
        private long _lastPerfLogTick;
        private int _detectCount;
        private int _fallbackCount;
        private int _slowCount;

        /// <summary>
        /// 不应被检测命中的窗口句柄集合。最常见的成员是"我们自己的截图遮罩窗口"
        /// （否则光标在自己窗口上时会把整屏当成检测目标）。
        ///
        /// 调用方在创建检测器后立即赋值。本类不负责线程同步——HashSet 在调用前不再修改即可。
        /// </summary>
        public ISet<IntPtr> ExcludedHwnds { get; } = new HashSet<IntPtr>();

        /// <summary>枚举并缓存一次当前 Z 序窗口列表，作为 WindowFromPoint 失败时的轻量兜底。</summary>
        public void RefreshWindowSnapshot()
        {
            var next = new List<WindowSnapshotItem>();
            foreach (IntPtr hwnd in WindowHelper.EnumerateVisibleTopLevelWindows())
            {
                if (hwnd == IntPtr.Zero || ExcludedHwnds.Contains(hwnd)) continue;
                Rectangle bounds = WindowHelper.GetWindowBounds(hwnd);
                if (!IsValidWindowBounds(bounds)) continue;
                next.Add(new WindowSnapshotItem { Hwnd = hwnd, Bounds = bounds });
            }

            lock (_snapshotLock)
            {
                _windowSnapshot = next;
            }
        }

        /// <summary>
        /// 屏幕物理像素点处的顶级窗口边界。
        /// 命中失败、被排除、或几何无效时返回 <c>null</c>。
        /// </summary>
        public Rectangle? DetectWindowAt(Point screenPoint)
        {
            long start = Environment.TickCount64;
            bool usedFallback = false;
            Rectangle? result = null;
            try
            {
                IntPtr ignored = ExcludedHwnds.Count > 0 ? ExcludedHwnds.First() : IntPtr.Zero;
                IntPtr hwnd = ignored != IntPtr.Zero
                    ? WindowHelper.GetTopLevelWindowFromPointIgnoringWindow(screenPoint, ignored)
                    : WindowHelper.GetTopLevelWindowFromPoint(screenPoint);

                if (hwnd != IntPtr.Zero && !ExcludedHwnds.Contains(hwnd))
                {
                    Rectangle bounds = WindowHelper.GetWindowBounds(hwnd);
                    if (IsValidWindowBounds(bounds) && bounds.Contains(screenPoint)) result = bounds;
                }

                if (!result.HasValue)
                {
                    usedFallback = true;
                    result = DetectFromSnapshot(screenPoint);
                }

                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WindowDetector.DetectWindowAt failed: " + ex.Message);
                usedFallback = true;
                result = DetectFromSnapshot(screenPoint);
                return result;
            }
            finally
            {
                long elapsed = Environment.TickCount64 - start;
                _detectCount++;
                if (usedFallback) _fallbackCount++;
                if (elapsed >= 12) _slowCount++;
                long now = Environment.TickCount64;
                if (now - _lastPerfLogTick >= 1000)
                {
                    AppLogger.Info("AutoDetect.Window perf: count=" + _detectCount + ", fallback=" + _fallbackCount + ", slow=" + _slowCount + ", last=" + elapsed + "ms, result=" + (result.HasValue ? result.Value.ToString() : "null"));
                    _detectCount = 0;
                    _fallbackCount = 0;
                    _slowCount = 0;
                    _lastPerfLogTick = now;
                }
            }
        }

        private Rectangle? DetectFromSnapshot(Point screenPoint)
        {
            List<WindowSnapshotItem> snapshot;
            lock (_snapshotLock)
            {
                snapshot = _windowSnapshot;
            }

            foreach (WindowSnapshotItem item in snapshot)
            {
                if (ExcludedHwnds.Contains(item.Hwnd)) continue;
                if (item.Bounds.Contains(screenPoint)) return item.Bounds;
            }
            return null;
        }

        private static bool IsValidWindowBounds(Rectangle bounds)
        {
            return !bounds.IsEmpty && bounds.Width > 2 && bounds.Height > 2;
        }
    }
}
