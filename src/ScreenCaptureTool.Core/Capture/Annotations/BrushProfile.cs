using System;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 画笔笔刷画像：一组相互独立的参数（透明度、笔锋强度、输入平滑、最细比例）。
/// 硬度固定为实心边抗锯齿，笔锋驱动统一为速度驱动（强度为 0 时即等宽无笔锋）。
/// 不含颜色，因此改标注颜色不会使已栅格化的覆盖率缓冲失效。BrushRasterizer（成品/预览共用）是其唯一消费者。
/// </summary>
public readonly struct BrushProfile
{
    /// <summary>
    /// 无默认值：默认画像必须显式取自 <see cref="Default"/>，
    /// 因为 <c>default(BrushProfile)</c> 的不透明度为 0（画出来完全看不见）。
    /// </summary>
    public BrushProfile(
        float opacity,
        float taperStrength,
        float smoothing,
        float minWidthRatio)
    {
        Opacity = Math.Clamp(opacity, 0.0f, 1.0f);
        TaperStrength = Math.Clamp(taperStrength, 0.0f, 1.0f);
        Smoothing = Math.Clamp(smoothing, 0.0f, 1.0f);
        MinWidthRatio = Math.Clamp(minWidthRatio, 0.05f, 1.0f);
    }

    /// <summary>兼容旧版调用签名的构造函数。</summary>
    [Obsolete("硬度已移除并固定为实边，笔锋统一为速度驱动")]
    public BrushProfile(
        float opacity,
        float hardness,
        BrushTaperMode taperMode,
        float taperStrength,
        float smoothing,
        float minWidthRatio)
        : this(opacity, taperStrength, smoothing, minWidthRatio)
    {
    }

    /// <summary>整笔单合成的不透明度 0-1（自交处不叠加变深）。</summary>
    public float Opacity { get; }

    /// <summary>笔尖硬度固定为 1.0（实边 + 0.5px 抗锯齿）。</summary>
    public float Hardness => 1.0f;

    /// <summary>笔锋驱动方式（强度大于 0 为速度笔锋，0 为等宽无笔锋）。</summary>
    public BrushTaperMode TaperMode => TaperStrength > 0.0f ? BrushTaperMode.Velocity : BrushTaperMode.None;

    /// <summary>笔锋强度 0-1：0 为等宽，越大则快慢带来的粗细差别越明显。</summary>
    public float TaperStrength { get; }

    /// <summary>输入平滑强度 0-1：越大则抖动越小、转折越圆，代价是笔迹跟手感变迟。</summary>
    public float Smoothing { get; }

    /// <summary>笔锋最细处相对基准粗细的比例下限。</summary>
    public float MinWidthRatio { get; }

    /// <summary>
    /// 默认画像：带轻微速度笔锋与平滑，实边、不透明。
    /// </summary>
    public static BrushProfile Default { get; } = new(
        opacity: 1.0f,
        taperStrength: 0.35f,
        smoothing: 0.40f,
        minWidthRatio: 0.25f);
}
