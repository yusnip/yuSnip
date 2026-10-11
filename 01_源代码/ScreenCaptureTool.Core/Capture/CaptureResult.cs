using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 一次交互式截图的结果。用户取消时 <see cref="ICaptureEngine.RunInteractiveAsync"/> 返回 null，
    /// 不会构造本对象。
    ///
    /// 实现 <see cref="IDisposable"/>，因为持有 <see cref="CroppedImage"/> 资源。
    /// </summary>
    public sealed class CaptureResult : IDisposable
    {
        /// <summary>用户最终框选的矩形（屏幕物理像素，与 <see cref="CapturedFrame.VirtualScreenRect"/> 同坐标系）。</summary>
        public Rectangle Region { get; }

        /// <summary>已裁剪到 <see cref="Region"/> 范围的位图，物理像素。所有权归本对象。</summary>
        public Bitmap CroppedImage { get; }

        /// <summary>结果生成时刻。</summary>
        public DateTime FinishedAt { get; }

        private bool _disposed;

        public CaptureResult(Rectangle region, Bitmap croppedImage)
        {
            Region = region;
            CroppedImage = croppedImage ?? throw new ArgumentNullException(nameof(croppedImage));
            FinishedAt = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { CroppedImage.Dispose(); } catch { /* 资源释放不向上抛 */ }
        }
    }
}
