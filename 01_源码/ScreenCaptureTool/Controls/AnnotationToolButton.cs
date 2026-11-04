using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;
using Path = Avalonia.Controls.Shapes.Path;

namespace ScreenCaptureTool.Controls;

/// <summary>截图与贴图共用的标注按钮，保留 Button 原有事件、样式与命名引用。</summary>
public sealed class AnnotationToolButton : Button
{
    public static readonly StyledProperty<string> ToolKeyProperty =
        AvaloniaProperty.Register<AnnotationToolButton, string>(nameof(ToolKey), string.Empty);

    public string ToolKey
    {
        get => GetValue(ToolKeyProperty);
        set => SetValue(ToolKeyProperty, value);
    }

    public AnnotationToolButton() => Classes.Add("toolIcon");

    /// <summary>
    /// 工具键名 → 工具模式。键名词汇的权威就是本类内那张图标 switch（未知键返回 <see cref="ToolMode.None"/>
    /// 而非抛异常——宿主用 null/空串表示"退出画板"），故各宿主一律转调这里，不再各抄一份映射。
    /// </summary>
    public static ToolMode ModeOfToolKey(string? toolKey) => toolKey switch
    {
        "brush" => ToolMode.Brush,
        "arrow" => ToolMode.Arrow,
        "text" => ToolMode.Text,
        "rectangle" => ToolMode.Rect,
        "mosaic" => ToolMode.Blur,
        "spotlight" => ToolMode.Spotlight,
        "counter" => ToolMode.Counter,
        "eraser" => ToolMode.Eraser,
        _ => ToolMode.None,
    };

    /// <summary>
    /// 图元类型档 → 工具键名（<see cref="ModeOfToolKey"/> 的反向表，键名词汇同样以本类那张图标 switch 为权威）。
    /// 用途：点中哪种图元就把宿主切到哪种工具（<c>AnnotationEditSession.BeginShapeEdit</c>），
    /// 贴图与录屏共用这一张，不在各自宿主里内联第二份 kind→键名。文字不是矢量图元，故不在这张表里。
    /// </summary>
    public static string? ToolKeyOfShapeKind(AnnotationShapeKind kind) => kind switch
    {
        AnnotationShapeKind.Stroke => "brush",
        AnnotationShapeKind.Arrow => "arrow",
        AnnotationShapeKind.Rectangle => "rectangle",
        AnnotationShapeKind.Blur => "mosaic",
        AnnotationShapeKind.Spotlight => "spotlight",
        AnnotationShapeKind.Counter => "counter",
        _ => null,
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ToolKeyProperty) return;
        Content = CreateIcon(ToolKey);
    }

    /// <summary>工具栏与设置预览复用同一图标绘制配方，包含填充、描边和视觉尺寸。</summary>
    public static Path? CreateIcon(string toolKey)
    {
        var data = toolKey switch
        {
            "brush" => "M 4 20 L 4 16 L 16 4 A 2.8 2.8 0 0 1 20 8 L 8 20 Z M 14.5 5.5 L 18.5 9.5 M 4 16 L 8 20 M 4 20 L 6 18",
            "arrow" => "M 4 20 L 20 4 M 10 4 H 20 V 14",
            "text" => "M 4 20 L 10 6 A 2 2 0 0 1 14 6 L 20 20 M 8 14 H 16",
            "rectangle" => "M 7 4 H 11 A 3 3 0 0 1 14 7 V 11 A 3 3 0 0 1 11 14 H 7 A 3 3 0 0 1 4 11 V 7 A 3 3 0 0 1 7 4 Z M 20 15 A 5 5 0 1 1 10 15 A 5 5 0 1 1 20 15 Z",
            // 矩形擦除：沿用截图形状预览的 24 单位画板与 R3 圆角，不带形状工具的叠圆。
            "eraserRectangle" => "M 7 4 H 17 A 3 3 0 0 1 20 7 V 17 A 3 3 0 0 1 17 20 H 7 A 3 3 0 0 1 4 17 V 7 A 3 3 0 0 1 7 4 Z",
            "counter" => "M 20 12 A 8 8 0 1 1 4 12 A 8 8 0 1 1 20 12 Z M 10 9 L 12 8 V 16 M 10 16 H 14",
            // 水印：三条 45° 平行斜线＝斜向平铺的那几行字（本功能默认档）。刻意不套外框——那圈圆角方框已被
            // 马赛克/聚光灯/长截图共用成"公共脸"，且框内两笔会被读成玻璃反光（2026-09-20 用户挑定「丁 · 裸三条斜排」）
            "watermark" => "M 4 12 L 12 4 M 4 20 L 20 4 M 12 20 L 20 12",
            "undo" => "M 9 10 L 4 15 L 9 20 M 4 15 H 14.5 A 5.5 5.5 0 0 0 20 9.5 A 5.5 5.5 0 0 0 14.5 4 H 10",
            "redo" => "M 15 10 L 20 15 L 15 20 M 20 15 H 9.5 A 5.5 5.5 0 0 1 4 9.5 A 5.5 5.5 0 0 1 9.5 4 H 14",
            "mosaic" => "F0 M 7 4 H 17 A 3 3 0 0 1 20 7 V 17 A 3 3 0 0 1 17 20 H 7 A 3 3 0 0 1 4 17 V 7 A 3 3 0 0 1 7 4 Z M 12 12 H 20 V 7 A 3 3 0 0 0 17 4 H 12 Z M 12 12 H 4 V 17 A 3 3 0 0 0 7 20 H 12 Z",
            "spotlight" => "M 7 4 H 17 A 3 3 0 0 1 20 7 V 17 A 3 3 0 0 1 17 20 H 7 A 3 3 0 0 1 4 17 V 7 A 3 3 0 0 1 7 4 Z M 12 8 A 4 4 0 1 0 12 16 A 4 4 0 1 0 12 8 Z",
            "eraser" => "M982.827818 408.890055a32.85895 32.85895 0 0 0 0-46.434253L629.96587 9.593854a32.85895 32.85895 0 0 0-46.434253 0L69.972616 523.104886a131.147983 131.147983 0 0 0 0 185.641074l278.845365 277.118471a131.33986 131.33986 0 0 0 92.484752 38.135569h421.64988a32.81098 32.81098 0 0 0 0-65.621961h-271.122312a65.66993 65.66993 0 0 1-46.434253-112.056214zM422.978472 875.966834a65.573992 65.573992 0 0 1-92.772568 0L116.502808 662.311707a65.573992 65.573992 0 0 1 0-92.772568L247.458914 438.439125l306.523633 306.523633-131.100014 131.004076z",
            "clearall" => "M 4 6 H 20 M 9 6 V 4 A 1 1 0 0 1 10 3 H 14 A 1 1 0 0 1 15 4 V 6 M 6 6 V 18 A 3 3 0 0 0 9 21 H 15 A 3 3 0 0 0 18 18 V 6 M 10 10 V 17 M 14 10 V 17",
            "" => null,
            _ => throw new ArgumentException("未知标注按钮：" + toolKey)
        };
        if (data == null) return null;
        var icon = new Path { Data = Geometry.Parse(data) };
        if (toolKey is not ("spotlight" or "eraser")) icon.Classes.Add("icon");
        if (toolKey is "mosaic" or "spotlight" or "eraser")
        {
            icon.Width = icon.Height = toolKey == "mosaic" ? 14.5 : 16;
            icon.Stretch = Stretch.Uniform;
            icon.Bind(Path.FillProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctIconBrush"));
        }
        return icon;
    }

    // 派生控件沿用全局 Button/toolIcon 样式，不重新定义窗口私有按钮样式。
    protected override Type StyleKeyOverride => typeof(Button);
}
