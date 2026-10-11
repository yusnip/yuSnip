using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 长截图会话选项。第一版仅描述核心算法/采集需要的输入，不包含 UI 状态。
/// </summary>
public sealed class LongScrollOptions
{
    public LongScrollOptions(Rectangle captureRegion)
    {
        CaptureRegion = captureRegion;
    }

    /// <summary>屏幕物理像素坐标中的采集区域。</summary>
    public Rectangle CaptureRegion { get; }

    /// <summary>最大合成长图高度，沿用旧实现的安全上限。</summary>
    public int MaxTotalHeightPx { get; init; } = 28_888;

    /// <summary>是否启用固定顶部/底部区域探测。</summary>
    public bool EnableFixedLayerMode { get; init; } = true;

    /// <summary>固定顶部区域估算比例。</summary>
    public double FixedTopRatio { get; init; } = 0.12;

    /// <summary>固定底部区域估算比例。</summary>
    public double FixedBottomRatio { get; init; } = 0.12;

    /// <summary>固定区域最大高度。</summary>
    public int MaxFixedLayerHeightPx { get; init; } = 120;

    /// <summary>采集线程目标间隔。旧实现约 60fps。</summary>
    public int CaptureIntervalMs { get; init; } = 16;

    /// <summary>启动长截图时主截图窗口的主题状态，用于长截图窗口保持主题一致。</summary>
    public bool IsDarkMode { get; init; }

    public bool IsValid => CaptureRegion.Width > 1 && CaptureRegion.Height > 1;
}
