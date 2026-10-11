namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 截屏底层接口。实现在 T4.2 完成（用 GDI BitBlt + System.Drawing.Bitmap）。
    /// </summary>
    public interface IScreenCapturer
    {
        /// <summary>
        /// 截下整虚拟屏幕（多显示器场景下覆盖所有屏幕区域）。
        /// 调用方负责 Dispose 返回的 <see cref="CapturedFrame"/>。
        /// </summary>
        CapturedFrame CaptureVirtualScreen();
    }
}
