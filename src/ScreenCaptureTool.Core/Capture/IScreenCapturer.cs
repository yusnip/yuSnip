namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 截屏底层接口。GDI 采集 SDR，HDR 输出自动以 FP16 采集并映射为 SDR；返回统一 Bitmap。
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
