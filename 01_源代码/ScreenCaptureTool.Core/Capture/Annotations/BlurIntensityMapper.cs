using System;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 旧版马赛克/模糊工具强度映射。UI 使用 0..12，物理模糊半径使用 0..26，马赛克块大小使用 2..50。
/// </summary>
public static class BlurIntensityMapper
{
    public const int MinUiLevel = 0;
    public const int MaxUiLevel = 12;
    public const int DefaultUiLevel = 5;
    public const double MaxPhysicalIntensity = 26.0;

    public static int ClampUiLevel(int level)
    {
        return Math.Clamp(level, MinUiLevel, MaxUiLevel);
    }

    public static double UiToPhysical(int level)
    {
        level = ClampUiLevel(level);
        if (level <= 0) return 0.0;
        return MaxPhysicalIntensity * level / MaxUiLevel;
    }

    public static int PhysicalToUi(double intensity)
    {
        if (intensity <= 0.0) return 0;
        intensity = Math.Clamp(intensity, 0.0, MaxPhysicalIntensity);
        return ClampUiLevel((int)Math.Round(intensity * MaxUiLevel / MaxPhysicalIntensity));
    }

    public static int UiLevelToMosaicBlockSize(int level)
    {
        level = ClampUiLevel(level);
        if (level <= 0) return 0;
        double t = (level - 1) * 48.0 / 11.0;
        return Math.Clamp(2 + (int)Math.Round(t), 2, 50);
    }
}
