using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.Platform.Accessibility;

namespace ScreenCaptureTool.Core.Capture.AutoDetect
{
    /// <summary>
    /// 细粒度自动检测：用 MSAA 拿光标下"UI 元素"边界（按钮/输入框/列表项等比窗口更细）。
    ///
    /// 与 <see cref="WindowDetector"/>（粗粒度）配对，由 <c>AutoDetectController</c>
    /// 在 <see cref="DetectMode.Element"/> / <see cref="DetectMode.Auto"/> 下使用。
    ///
    /// 设计要点：
    /// - 同步阻塞调用（COM marshal 在 STA 上下文中可能耗时数百 ms）
    /// - 内部用 <see cref="Task.Run"/> + <c>Wait(timeoutMs)</c> 实现超时降级
    /// - 严格释放底层 COM 对象，避免泄漏
    /// - 任何异常都吞成 null（AppLogger.Warn 留痕）
    ///
    /// 注意：这是同步入口。<c>AutoDetectController</c> 高频调用时应外包一个独立 worker
    /// 线程（T4.5a <c>FastMsaaWorker</c>），不要在 UI 线程上直接 hit-test。
    /// </summary>
    public sealed class ElementDetector
    {
        /// <summary>默认超时：80ms。旧实现里典型 hit-test 完成在 10~50ms，超过 80ms 视作"卡了"。</summary>
        public const int DefaultTimeoutMs = 80;

        /// <summary>
        /// 屏幕物理像素点处的 UI 元素边界。
        /// 失败、超时、几何无效 → 返回 <c>null</c>。
        /// </summary>
        public Rectangle? DetectElementAt(Point screenPoint, int timeoutMs = DefaultTimeoutMs)
        {
            return DetectElementAt(screenPoint, IntPtr.Zero, timeoutMs);
        }

        public Rectangle? DetectElementAt(Point screenPoint, IntPtr ignoredWindow, int timeoutMs = DefaultTimeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            try
            {
                // 在线程池上跑同步 MSAA 调用，外面用 Wait(timeout) 控制超时。
                // 注意：这里不传 CancellationToken，因为 IAccessible 同步调用本身不可取消；
                // 超时后我们仅"放弃等待结果"，COM 调用会在线程池线程上自然完成并释放。
                Task<Rectangle?> task = Task.Run(() => DetectCore(screenPoint, ignoredWindow));

                if (!task.Wait(timeoutMs))
                {
                    // 让 task 在后台默默完成（含 COM 释放），不抛
                    AppLogger.Warn("ElementDetector: timeout " + timeoutMs + "ms at " + screenPoint);
                    _ = task.ContinueWith(t =>
                    {
                        if (t.IsFaulted) AppLogger.Warn("ElementDetector(late): " + t.Exception?.GetBaseException().Message);
                    }, TaskScheduler.Default);
                    return null;
                }

                return task.Result;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ElementDetector.DetectElementAt failed: " + ex.Message);
                return null;
            }
        }

        private static Rectangle? DetectCore(Point screenPoint, IntPtr ignoredWindow)
        {
            try
            {
                Rectangle? uiaRect = UIAutomationHelper.GetElementRectAtPoint(screenPoint, ignoredWindow);
                if (uiaRect.HasValue)
                {
                    Rectangle r = uiaRect.Value;
                    if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                    {
                        return r;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ElementDetector: UI Automation check threw: " + ex.Message);
            }

            AccessibleHit? hit = null;
            try
            {
                hit = AccessibilityHelper.GetAccessibleAtPoint(screenPoint, ignoredWindow);
                if (hit == null) return null;

                Rectangle r = AccessibilityHelper.GetBounds(hit);
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;
                return r;
            }
            finally
            {
                AccessibilityHelper.ReleaseSafely(hit);
            }
        }
    }
}
