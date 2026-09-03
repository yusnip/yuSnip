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

        private bool _disposed;

        public CapturedFrame(Bitmap fullScreen, Rectangle virtualScreenRect, double dpiScale)
        {
            FullScreen = fullScreen ?? throw new ArgumentNullException(nameof(fullScreen));
            VirtualScreenRect = virtualScreenRect;
            DpiScale = dpiScale > 0 ? dpiScale : 1.0;
            CapturedAt = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { FullScreen.Dispose(); } catch { /* 资源释放不向上抛 */ }
        }
    }
}
