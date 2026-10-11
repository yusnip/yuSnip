using System;
using System.Drawing;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// GDI 屏幕截图实现。一次性截下整虚拟屏幕（含多显示器）。
    ///
    /// 设计要点：
    /// - 不动用户的窗口（隐藏自身那是 UI 层职责，本类只负责"按下快门"）
    /// - 不调 <c>Application.DoEvents</c>（旧实现的 WPF/WinForms 包袱）
    /// - 失败抛 <see cref="InvalidOperationException"/>，包装真实异常
    ///
    /// 实现简单，主要是把旧 <c>ScreenCapture.FrozenAndSelection.CaptureScreenFrozen</c>
    /// 中的"截屏"部分剥离出来，去掉所有 WPF 相关的副作用。
    /// </summary>
    public sealed class ScreenCapturer : IScreenCapturer
    {
        public CapturedFrame CaptureVirtualScreen()
        {
            using (PerformanceTimer.Measure("ScreenCapturer.CaptureVirtualScreen"))
            {
                Rectangle virt = MonitorHelper.GetVirtualScreenRect();

                Bitmap? bmp = null;
                try
                {
                    bmp = new Bitmap(virt.Width, virt.Height);
                    double dpiScale;

                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        // CopyFromScreen 接收"源左上角的屏幕坐标 + 目标位图内的偏移 + 拷贝尺寸"
                        // 用 virt.X/Y 作为源原点，确保多屏左/上边界为负的情况也能正确取到。
                        g.CopyFromScreen(
                            sourceX: virt.X,
                            sourceY: virt.Y,
                            destinationX: 0,
                            destinationY: 0,
                            blockRegionSize: bmp.Size);

                        // DPI 缩放只用主显示器的 DpiX 当作参考值；
                        // Avalonia 11.x 自身按 per-monitor DPI v2 工作，UI 层不依赖此值做关键决策。
                        dpiScale = g.DpiX > 0 ? g.DpiX / 96.0 : 1.0;
                    }

                    return new CapturedFrame(bmp, virt, dpiScale);
                }
                catch (Exception ex)
                {
                    // 创建到一半异常，自己释放 bitmap，避免泄漏 GDI+ 句柄
                    try { bmp?.Dispose(); } catch { }
                    AppLogger.Error("ScreenCapturer.CaptureVirtualScreen failed.", ex);
                    throw new InvalidOperationException(
                        "屏幕截图失败：" + ex.Message, ex);
                }
            }
        }
    }
}
