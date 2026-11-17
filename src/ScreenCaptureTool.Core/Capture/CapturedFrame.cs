using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 一次"冻结整屏"截图的产物。<see cref="FullScreen"/> 为整虚拟屏幕的物理像素位图，
    /// <see cref="VirtualScreenRect"/> 是该位图在系统坐标系中的位置（多屏时左上角可能为负）。
    ///
    /// 实现 <see cref="IDisposable"/>，因为持有 <see cref="Bitmap"/> 资源（非托管 GDI+ 句柄）。
    /// 调用方有义务在使用完毕后 Dispose。
    /// </summary>
    public sealed class CapturedFrame : IDisposable
    {
        /// <summary>整虚拟屏幕的位图，物理像素。所有权归本对象，Dispose 时一并释放。</summary>
        public Bitmap FullScreen { get; }

        /// <summary>该位图在屏幕坐标系中的矩形（物理像素）。</summary>
        public Rectangle VirtualScreenRect { get; }

        /// <summary>截屏时的主显示器 DPI 缩放（96 DPI = 1.0）。多显示器各自缩放时此值为参考值。</summary>
        public double DpiScale { get; }

        /// <summary>截屏时机的高精度时间戳，便于性能分析与日志。</summary>
        public DateTime CapturedAt { get; }

        private readonly IntPtr _hBitmap;
        private readonly IntPtr _opaqueDibPixels;
        private readonly int _opaqueDibStride;
        private bool _disposed;

        public CapturedFrame(Bitmap fullScreen, Rectangle virtualScreenRect, double dpiScale, IntPtr hBitmap = default,
            IntPtr opaqueDibPixels = default)
        {
            FullScreen = fullScreen ?? throw new ArgumentNullException(nameof(fullScreen));
            VirtualScreenRect = virtualScreenRect;
            DpiScale = dpiScale > 0 ? dpiScale : 1.0;
            CapturedAt = DateTime.UtcNow;
            _hBitmap = hBitmap;
            // 仅 ScreenCapturer 的自有、正步长、32 位不透明 DIB 可以走此通路。
            if (hBitmap != IntPtr.Zero && opaqueDibPixels != IntPtr.Zero &&
                FullScreen.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppRgb)
            {
                _opaqueDibPixels = opaqueDibPixels;
                _opaqueDibStride = checked(FullScreen.Width * 4);
            }
        }

        public bool HasOpaqueDib => !_disposed && _opaqueDibPixels != IntPtr.Zero;

        /// <summary>
        /// 在帧所有权内复制原生不透明像素；不暴露可在 Dispose 后使用的源指针。
        /// 调用方须保证本帧尚未释放，且目标 framebuffer 在整个复制期间已锁定。
        /// </summary>
        public unsafe bool TryCopyOpaqueDibTo(IntPtr destination, int destinationStride, int destinationRows)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opaqueDibPixels == IntPtr.Zero) return false;
            if (destination == IntPtr.Zero || destinationStride < _opaqueDibStride || destinationRows < FullScreen.Height)
                throw new ArgumentException("目标缓冲区不足以容纳截图像素");
            if (destinationStride == _opaqueDibStride)
            {
                long bytes = (long)_opaqueDibStride * FullScreen.Height;
                Buffer.MemoryCopy((void*)_opaqueDibPixels, (void*)destination, bytes, bytes);
            }
            else
            {
                for (int y = 0; y < FullScreen.Height; y++)
                    Buffer.MemoryCopy((byte*)_opaqueDibPixels + (long)y * _opaqueDibStride,
                        (byte*)destination + (long)y * destinationStride, destinationStride, _opaqueDibStride);
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { FullScreen.Dispose(); } catch { /* 资源释放不向上抛 */ }
            if (_hBitmap != IntPtr.Zero)
            {
                try { ScreenCaptureTool.Platform.NativeMethods.DeleteObject(_hBitmap); } catch { }
            }
        }
    }
}
