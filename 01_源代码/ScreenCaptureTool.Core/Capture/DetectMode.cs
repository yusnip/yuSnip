using System;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 自动检测模式。对应旧 <c>AppSettings.AutoDetectMode</c> 的字符串配置。
    ///
    /// 字符串 ⇄ 枚举的映射由 <see cref="DetectModeExtensions"/> 提供，
    /// 让旧设置文件 / 持久化层（仍写中文）与新业务代码（用枚举）解耦。
    /// </summary>
    public enum DetectMode
    {
        /// <summary>不检测：用户完全手动框选。</summary>
        None = 0,

        /// <summary>仅窗口：检测光标下的顶级窗口边界。</summary>
        Window = 1,

        /// <summary>检测元素：用 IAccessible/MSAA 检测 UI 元素边界。</summary>
        Element = 2,

        /// <summary>兼容旧配置：自动已移除，运行时会映射为 Window。</summary>
        Auto = 3,
    }

    /// <summary>
    /// <see cref="DetectMode"/> 与旧版字符串值之间的转换。
    /// 旧字符串值取自 <c>AppSettings.NormalizeAutoDetectMode</c>。
    /// </summary>
    public static class DetectModeExtensions
    {
        public const string LegacyNone    = "不检测";
        public const string LegacyWindow  = "仅窗口";
        public const string LegacyElement = "检测元素";
        public const string LegacyAuto    = "自动";

        public static string ToLegacyString(this DetectMode mode)
        {
            switch (mode)
            {
                case DetectMode.None:    return LegacyNone;
                case DetectMode.Window:  return LegacyWindow;
                case DetectMode.Element: return LegacyElement;
                case DetectMode.Auto:    return LegacyWindow;
                default:                 return LegacyWindow;
            }
        }

        public static DetectMode FromLegacyString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DetectMode.Window;
            string v = value.Trim();
            if (string.Equals(v, LegacyNone, StringComparison.Ordinal) ||
                string.Equals(v, "None", StringComparison.OrdinalIgnoreCase))    return DetectMode.None;
            if (string.Equals(v, LegacyWindow, StringComparison.Ordinal) ||
                string.Equals(v, "Window", StringComparison.OrdinalIgnoreCase))  return DetectMode.Window;
            if (string.Equals(v, LegacyElement, StringComparison.Ordinal) ||
                string.Equals(v, "Element", StringComparison.OrdinalIgnoreCase)) return DetectMode.Element;
            return DetectMode.Window;
        }
    }
}
