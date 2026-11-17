using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 高性能 Win32 DIBSection BitBlt 屏幕截图实现。一次性截下整虚拟屏幕（含多显示器）。
    /// 
    /// 性能升级：
    /// - 废弃缓慢的 GDI+ Graphics.CopyFromScreen（耗时 55~70ms）
    /// - 改用底层的 CreateDIBSection + BitBlt(SRCCOPY | CAPTUREBLT) + 零拷贝 Bitmap 包装（耗时 5~12ms）
    /// - 内存直接映射，极大减少内存分配与 CPU 拷贝时延
    /// </summary>
    public sealed class ScreenCapturer : IScreenCapturer
    {
        public CapturedFrame CaptureVirtualScreen()
        {
            using (PerformanceTimer.Measure("ScreenCapturer.CaptureVirtualScreen"))
            {
                Rectangle virt = MonitorHelper.GetVirtualScreenRect();
                int width = virt.Width;
                int height = virt.Height;

                if (width <= 0 || height <= 0)
                {
                    throw new InvalidOperationException($"虚拟屏幕尺寸非法: {width}x{height}");
                }

                IntPtr hdcScreen = IntPtr.Zero;
                IntPtr hdcMem = IntPtr.Zero;
                IntPtr hBmp = IntPtr.Zero;
                IntPtr hOldBmp = IntPtr.Zero;
                Bitmap? bmp = null;

                try
                {
                    hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
                    if (hdcScreen == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("无法获取屏幕设备上下文 (GetDC 失败)");
                    }

                    int logPixelsX = NativeMethods.GetDeviceCaps(hdcScreen, NativeMethods.LOGPIXELSX);
                    double dpiScale = logPixelsX > 0 ? logPixelsX / 96.0 : 1.0;

                    hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
                    if (hdcMem == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("无法创建内存设备上下文 (CreateCompatibleDC 失败)");
                    }

                    var bmi = new NativeMethods.BITMAPINFO
                    {
                        bmiHeader = new NativeMethods.BITMAPINFOHEADER
                        {
                            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                            biWidth = width,
                            biHeight = -height, // 负数表示从上到下的 DIB (Top-Down)
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = 0, // BI_RGB
                            biSizeImage = width * height * 4
                        }
                    };

                    IntPtr pBits;
                    using (PerformanceTimer.Measure("ScreenCapturer.CreateDib"))
                        hBmp = NativeMethods.CreateDIBSection(
                            hdcMem,
                            ref bmi,
                            (uint)NativeMethods.DIB_RGB_COLORS,
                            out pBits,
                            IntPtr.Zero,
                            0);

                    if (hBmp == IntPtr.Zero || pBits == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("创建 DIBSection 失败");
                    }

                    hOldBmp = NativeMethods.SelectObject(hdcMem, hBmp);

                    // 拷贝屏幕内容：SRCCOPY | CAPTUREBLT 保证捕获半透明和分层窗口
                    int rop = NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT;
                    bool bltSuccess;
                    using (PerformanceTimer.Measure("ScreenCapturer.BitBlt"))
                        bltSuccess = NativeMethods.BitBlt(
                            hdcMem,
                            0, 0,
                            width, height,
                            hdcScreen,
                            virt.X, virt.Y,
                            rop);

                    if (!bltSuccess)
                    {
                        throw new InvalidOperationException("BitBlt 截屏操作失败");
                    }

                    // DIB 随后会被后台转换器直接读内存，必须在发起 BitBlt 的线程冲刷 GDI 批次。
                    // 否则首帧可能读到尚未完成写入的像素，不能靠换线程后的 LockBits 补救。
                    if (!NativeMethods.GdiFlush())
                        throw new InvalidOperationException("GDI 截屏像素同步失败");

                    // 冻结并发布前补齐 HDR 输出，未完成的输出可补采一次。
                    HdrScreenCapture.CopySnapshot(virt, pBits, checked(width * 4));

                    // 使用零拷贝 Bitmap 包装 DIBSection 内存块
                    int stride = width * 4;
                    bmp = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, pBits);

                    // 释放 DC 资源（hBmp 交由 CapturedFrame 管理释放）
                    NativeMethods.SelectObject(hdcMem, hOldBmp);
                    hOldBmp = IntPtr.Zero;
                    NativeMethods.DeleteDC(hdcMem);
                    hdcMem = IntPtr.Zero;
                    NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
                    hdcScreen = IntPtr.Zero;

                    IntPtr ownedHBmp = hBmp;
                    hBmp = IntPtr.Zero; // 移交所有权

                    return new CapturedFrame(bmp, virt, dpiScale, ownedHBmp, pBits);
                }
                catch (Exception ex)
                {
                    try { bmp?.Dispose(); } catch { }
                    if (hOldBmp != IntPtr.Zero && hdcMem != IntPtr.Zero)
                    {
                        try { NativeMethods.SelectObject(hdcMem, hOldBmp); } catch { }
                    }
                    if (hBmp != IntPtr.Zero)
                    {
                        try { NativeMethods.DeleteObject(hBmp); } catch { }
                    }
                    if (hdcMem != IntPtr.Zero)
                    {
                        try { NativeMethods.DeleteDC(hdcMem); } catch { }
                    }
                    if (hdcScreen != IntPtr.Zero)
                    {
                        try { NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen); } catch { }
                    }

                    AppLogger.Error("ScreenCapturer.CaptureVirtualScreen 失败", ex);
                    throw new InvalidOperationException("屏幕截图失败：" + ex.Message, ex);
                }
            }
        }
    }
}
