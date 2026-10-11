using Avalonia.Media;

namespace ScreenCaptureTool;

/// <summary>
/// 贴图阴影样式的固定值（本次按需求硬编码）。
/// 预留为可扩展数据结构：将来要做设置项时，把这些常量改成属性 + 设置 UI 即可。
///
/// 需求规格（经用户确认调整为四周均匀）：
/// - 偏移 (0, 0)，模糊半径 12px，不透明度 40%（柔和的四周环绕阴影）
/// - 焦点阴影色 #1E90FF（蓝），非焦点 #555555（灰黑）
/// </summary>
public static class StickerStyling
{
    public const double ShadowOffsetX = 0;
    public const double ShadowOffsetY = 0;
    public const double ShadowBlurRadius = 6;
    public const double ShadowSpread = 2;
    public const double ShadowOpacity = 0.85;

    public const string FocusedShadowColorHex = "#1E90FF";
    public const string UnfocusedShadowColorHex = "#555555";

    /// <summary>焦点状态阴影（蓝色）。offset/blur 固定，alpha 由 opacity 折算进颜色。</summary>
    public static BoxShadows FocusedShadow => Build(FocusedShadowColorHex);

    /// <summary>非焦点状态阴影（灰黑色）。</summary>
    public static BoxShadows UnfocusedShadow => Build(UnfocusedShadowColorHex);

    private static BoxShadows Build(string hex)
    {
        Color baseColor = Color.Parse(hex);
        // 把 opacity 折算成颜色的 alpha 通道（0.4 → 约 0x66）。
        byte alpha = (byte)Math.Round(baseColor.A * ShadowOpacity);
        return new BoxShadows(new BoxShadow
        {
            OffsetX = ShadowOffsetX,
            OffsetY = ShadowOffsetY,
            Blur = ShadowBlurRadius,
            Spread = ShadowSpread,
            Color = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B),
        });
    }
}
