using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 文本图层快照，用于把独立的文本 UI 层纳入截图会话撤销/重做。
/// </summary>
public sealed class TextLayerSnapshot
{
    public string Text { get; init; } = string.Empty;
    public double Left { get; init; }
    public double Top { get; init; }
    public double MaxWidth { get; init; }
    public double FontSize { get; init; }
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public bool HasStroke { get; init; }
    public bool HasBackground { get; init; }
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public Color Color { get; init; }
    public Color FillColor { get; init; }
    public Color StrokeColor { get; init; }
    public double StrokeThickness { get; init; }
    public bool IsStrokeAuto { get; init; }
    public double FillOpacity { get; init; }
    public double FillCornerRadius { get; init; }
    public double FillPadding { get; init; }
    public double RotationAngle { get; init; }
}

/// <summary>
/// 文本图层信息。每个文本元素是一个完整的 Avalonia 可视树，叠加在截图画布上。
/// </summary>
public sealed class TextLayerInfo
{
    /// <summary>根容器 Grid</summary>
    public Grid Root = null!;

    /// <summary>实时可编辑的 Avalonia TextBox</summary>
    public TextBox TextBox = null!;

    /// <summary>背景填充 Border</summary>
    public Border? BackgroundBorder;

    /// <summary>描边路径 (Avalonia Path using StreamGeometry)</summary>
    public Avalonia.Controls.Shapes.Path? StrokePath;

    /// <summary>选中虚线框</summary>
    public Border SelectionBorder = null!;

    /// <summary>当前字号（逻辑像素）</summary>
    public double FontSize;

    /// <summary>是否粗体</summary>
    public bool IsBold;

    /// <summary>是否斜体</summary>
    public bool IsItalic;

    /// <summary>是否启用描边</summary>
    public bool HasStroke;

    /// <summary>是否启用背景填充</summary>
    public bool HasBackground;

    /// <summary>字体族名称</summary>
    public string FontFamily = "Microsoft YaHei UI";

    /// <summary>文字颜色</summary>
    public Color Color;

    /// <summary>背景填充颜色</summary>
    public Color FillColor;

    /// <summary>描边颜色</summary>
    public Color StrokeColor;

    /// <summary>描边粗细（逻辑像素）</summary>
    public double StrokeThickness;

    /// <summary>描边是否自动（跟随笔画粗细）</summary>
    public bool IsStrokeAuto;

    /// <summary>填充不透明度 (0-100)</summary>
    public double FillOpacity;

    /// <summary>填充圆角半径 (0-20)</summary>
    public double FillCornerRadius;

    /// <summary>填充内边距 (0-30)</summary>
    public double FillPadding;

    /// <summary>装饰层 Grid</summary>
    public Grid AdornerLayer = null!;

    /// <summary>旋转手柄 (左上)</summary>
    public Ellipse RotateHandle = null!;

    /// <summary>删除按钮 (右下)</summary>
    public Border DeleteHandle = null!;

    /// <summary>文本旋转角度（度）</summary>
    public double RotationAngle;
}
