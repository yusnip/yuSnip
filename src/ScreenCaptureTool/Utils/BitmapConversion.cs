using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Utils;

/// <summary>
/// 位图转换工具：DIB/Framebuffer 之间直接拷贝，避免 PNG 编解码。
/// 大屏转换仍有分配、GDI+ 格式转换和像素复制开销，按阶段记录实际耗时。
/// </summary>
public static class BitmapConversion
{
    private static readonly object CaptureTargetGate = new();
    private static WriteableBitmap? _preparedCaptureTarget;
    private static bool _captureTargetPreparationStopped;

    /// <summary>只预备一个空白目标，提前提交内存页；不缓存桌面像素，不复用已结束会话。</summary>
    internal static unsafe void PrepareCaptureTarget(PixelSize size, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || size.Width <= 0 || size.Height <= 0 ||
            (long)size.Width * size.Height * 4 > 64L * 1024 * 1024) return;
        WriteableBitmap? target = null;
        try
        {
            using (PerformanceTimer.Measure("CaptureStartup.PrepareBlankTarget"))
            {
                target = new WriteableBitmap(size, new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using var fb = target.Lock();
                NativeMemory.Clear((void*)fb.Address, checked((nuint)((long)fb.RowBytes * fb.Size.Height)));
            }
            WriteableBitmap? previous;
            lock (CaptureTargetGate)
            {
                if (cancellation.IsCancellationRequested || _captureTargetPreparationStopped) return;
                previous = _preparedCaptureTarget;
                _preparedCaptureTarget = target;
                target = null;
            }
            previous?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("BitmapConversion: 空白截图缓冲预备失败，正式截图正常分配：" + ex.Message);
        }
        finally { target?.Dispose(); }
    }

    internal static void ShutdownCaptureTargetPreparation()
    {
        WriteableBitmap? target;
        lock (CaptureTargetGate)
        {
            _captureTargetPreparationStopped = true;
            target = _preparedCaptureTarget;
            _preparedCaptureTarget = null;
        }
        target?.Dispose();
    }

    /// <summary>原生截图是 BGRX 不透明底图，直接复制到 Opaque 目标，避免 GDI+ 预乘格式转换。</summary>
    public static WriteableBitmap? ToAvaloniaCaptureBitmap(CapturedFrame frame)
    {
        if (!frame.HasOpaqueDib) return ToAvaloniaBitmap(frame.FullScreen);
        WriteableBitmap? target = null;
        try
        {
            var size = new PixelSize(frame.FullScreen.Width, frame.FullScreen.Height);
            bool reused;
            using (PerformanceTimer.Measure("BitmapConversion.Allocate"))
            {
                lock (CaptureTargetGate)
                {
                    target = _preparedCaptureTarget;
                    _preparedCaptureTarget = null;
                }
                if (target != null && target.PixelSize != size)
                {
                    target.Dispose();
                    target = null;
                }
                reused = target != null;
                target ??= new WriteableBitmap(size, new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (PerformanceTimer.Measure("BitmapConversion.CopyOpaqueDib"))
            using (var fb = target.Lock())
            {
                if (!frame.TryCopyOpaqueDibTo(fb.Address, fb.RowBytes, fb.Size.Height))
                    throw new InvalidOperationException("原生截图像素不可用");
            }
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF BitmapConversion.CaptureTarget reused={reused} size={size.Width}x{size.Height} opaqueDib=True");
            return target;
        }
        catch (Exception ex)
        {
            target?.Dispose();
            AppLogger.Warn("BitmapConversion: 原生截图直拷失败，恢复通用转换：" + ex.Message);
            return ToAvaloniaBitmap(frame.FullScreen);
        }
    }

    public static WriteableBitmap? ToAvaloniaBitmap(System.Drawing.Bitmap? bitmap)
    {
        if (bitmap == null) return null;
        try
        {
            return ToAvaloniaBitmapCore(bitmap);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("BitmapConversion.ToAvaloniaBitmap 快路径异常: " + ex.Message);
            try
            {
                // 兜底一：若原图不支持 Format32bppPArgb 直接锁定，克隆为标准 32bppPArgb 后重试快路径
                using var clone = new System.Drawing.Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = System.Drawing.Graphics.FromImage(clone))
                {
                    g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
                }
                return ToAvaloniaBitmapCore(clone);
            }
            catch (Exception ex2)
            {
                AppLogger.Warn("BitmapConversion.ToAvaloniaBitmap 克隆直拷重试失败，改用极速 BMP 流回退: " + ex2.Message);
                try
                {
                    // 兜底二：改用无压缩的 BMP 内存流，彻底杜绝 PNG 压缩带来的 ~300ms 级阻塞
                    using var ms = new MemoryStream();
                    bitmap.Save(ms, ImageFormat.Bmp);
                    ms.Position = 0;
                    using var loaded = new Avalonia.Media.Imaging.Bitmap(ms);
                    var wb = new WriteableBitmap(loaded.PixelSize, loaded.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var fb = wb.Lock())
                    {
                        loaded.CopyPixels(fb, Avalonia.Platform.AlphaFormat.Premul);
                    }
                    return wb;
                }
                catch (Exception ex3)
                {
                    AppLogger.Warn("BitmapConversion.ToAvaloniaBitmap 全部回退路径均失败: " + ex3.Message);
                    return null;
                }
            }
        }
    }

    private static WriteableBitmap? ToAvaloniaBitmapCore(System.Drawing.Bitmap bitmap)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        if (w <= 0 || h <= 0) return null;

        WriteableBitmap wb;
        using (PerformanceTimer.Measure("BitmapConversion.Allocate"))
            wb = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                AlphaFormat.Premul);

        try
        {
            BitmapData data;
            using (PerformanceTimer.Measure("BitmapConversion.LockBits"))
                data = bitmap.LockBits(
                    new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            try
            {
                using (PerformanceTimer.Measure("BitmapConversion.CopyPixels"))
                using (ILockedFramebuffer fb = wb.Lock())
                {
                    int srcStride = data.Stride;
                    int dstStride = fb.RowBytes;
                    int rowBytes = Math.Min(Math.Abs(srcStride), dstStride);
                    int copyRows = Math.Min(h, fb.Size.Height);

                    if (srcStride == dstStride && srcStride > 0)
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy(
                                (void*)data.Scan0,
                                (void*)fb.Address,
                                (long)dstStride * copyRows,
                                (long)dstStride * copyRows);
                        }
                    }
                    else
                    {
                        unsafe
                        {
                            byte* pSrc = (byte*)data.Scan0;
                            byte* pDst = (byte*)fb.Address;
                            for (int y = 0; y < copyRows; y++)
                            {
                                Buffer.MemoryCopy(
                                    pSrc + (long)y * srcStride,
                                    pDst + (long)y * dstStride,
                                    dstStride,
                                    rowBytes);
                            }
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return wb;
        }
        catch
        {
            // 锁图、拷贝或解锁失败时释放目标，再交给现有回退通路。
            wb.Dispose();
            throw;
        }
    }
}
