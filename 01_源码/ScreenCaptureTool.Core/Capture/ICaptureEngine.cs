using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 顶层截图引擎。一次"用户按热键 → 选完区域 → 拿到结果"的完整交互。
    /// 实现由 T4.7 在最小宿主里完成。阶段 4 仅定义入口签名。
    /// </summary>
    public interface ICaptureEngine
    {
        /// <summary>
        /// 启动一次交互式截图。返回结果包含用户最终确认的选区位图（已裁剪，物理像素），
        /// 或 <c>null</c> 表示用户取消。
        /// </summary>
        /// <param name="options">本次截图的运行配置（来自 <see cref="AppSettings.ToCaptureOptions"/>）。</param>
        /// <param name="cancellationToken">外部取消信号（如热键再次触发）。</param>
        Task<CaptureResult?> RunInteractiveAsync(CaptureOptions options, CancellationToken cancellationToken = default);
    }
}
