using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Utils;

/// <summary>
/// 菜单右侧快捷键列的显示转换器。
/// Fluent 菜单模板默认用 PlatformKeyGestureConverter（走 KeyGestureFormatInfo 的平台覆盖表），
/// 键名输出 Escape / Delete / PgDn 这类写法，修饰键顺序也是 Ctrl→Shift→Alt→Meta，
/// 与「设置 → 快捷键与动作」列表里的 Esc / Del / PageDown + Ctrl→Alt→Shift→Win 对不上；
/// 这里统一复用 BuiltinShortcutsStore.Format，让菜单与设置页永远显示同一串文字。
/// </summary>
public sealed class MenuGestureConverter : IValueConverter
{
    /// <summary>全局样式里以 x:Static 引用的单例（无状态，可复用）。</summary>
    public static readonly MenuGestureConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is KeyGesture gesture
            ? BuiltinShortcutsStore.Format(gesture.KeyModifiers, gesture.Key)
            : string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
