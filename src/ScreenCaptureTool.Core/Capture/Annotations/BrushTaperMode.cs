namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>画笔笔锋的驱动方式（现统一为速度驱动）。</summary>
public enum BrushTaperMode
{
    /// <summary>无笔锋：整笔等宽（强度为 0）。</summary>
    None,

    /// <summary>速度笔锋：画得越快越细，慢速饱满，鼠标友好。</summary>
    Velocity,
}
