using System.Drawing;

namespace ScreenCaptureTool.Core.Matting;

/// <summary>
/// 智能抠图执行结果
/// </summary>
public sealed class MattingResult
{
    /// <summary>
    /// 是否处理成功
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 处理后的带 Alpha 通道位图（32 位 ARGB，需由调用方负责释放）
    /// </summary>
    public Bitmap? ResultBitmap { get; init; }

    /// <summary>
    /// 错误信息（若失败）
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 推理及图像后处理耗时（毫秒）
    /// </summary>
    public long ElapsedMilliseconds { get; init; }

    public static MattingResult Ok(Bitmap resultBitmap, long elapsedMs) => new()
    {
        Success = true,
        ResultBitmap = resultBitmap,
        ElapsedMilliseconds = elapsedMs
    };

    public static MattingResult Fail(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
