using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace ScreenCaptureTool.Themes;

/// <summary>托盘浮层按实例启用的编译期主题，不影响普通菜单或四模块工具条。</summary>
public sealed partial class WorkspaceTray : Styles
{
    public WorkspaceTray() => AvaloniaXamlLoader.Load(this);
}
