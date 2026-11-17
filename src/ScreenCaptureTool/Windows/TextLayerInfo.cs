using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 文本图层快照，用于把独立的文本 UI 层纳入截图会话撤销/重做。
/// </summary>
public sealed class TextLayerSnapshot
{
    public System.Guid Id { get; init; } = System.Guid.NewGuid();
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
    public double FillOpacity { get; init; }
    public double FillCornerRadius { get; init; }
    public double FillPadding { get; init; }
    public double RotationAngle { get; init; }

    // ──────────────────────────────────────────
    // 烘焙实测量的留存
    // 截图侧原本在导出时现读活控件（Root.Bounds / TextBox.LineHeight）；贴图侧退出编辑后活控件
    // 会被拆掉，只剩快照，所以这三项必须在快照生成时就实测下来，快照才能脱离可视树复现同一份图。
    // ──────────────────────────────────────────

    /// <summary>根容器实测宽度（DIP），烘焙时乘换算系数得到背景/文字版面宽。</summary>
    public double MeasuredWidth { get; init; }

    /// <summary>根容器实测高度（DIP）。</summary>
    public double MeasuredHeight { get; init; }

    /// <summary>行高（DIP），已含"TextBox.LineHeight 无效时按字号 1.35 倍兜底"的口径。</summary>
    public double LineHeight { get; init; }

    /// <summary>
    /// 从活控件图层产出快照。撤销链与导出烘焙链共用这一处读取式，避免两边各抄一遍导致口径漂移。
    /// </summary>
    public static TextLayerSnapshot FromLayer(TextLayerInfo info) => new()
    {
        Id = info.Id,
        Text = info.TextBox?.Text ?? string.Empty,
        Left = Canvas.GetLeft(info.Root),
        Top = Canvas.GetTop(info.Root),
        MaxWidth = info.TextBox?.MaxWidth ?? 0,
        FontSize = info.FontSize,
        IsBold = info.IsBold,
        IsItalic = info.IsItalic,
        HasStroke = info.HasStroke,
        HasBackground = info.HasBackground,
        FontFamily = info.FontFamily,
        Color = info.Color,
        FillColor = info.FillColor,
        StrokeColor = info.StrokeColor,
        StrokeThickness = info.StrokeThickness,
        FillOpacity = info.FillOpacity,
        FillCornerRadius = info.FillCornerRadius,
        FillPadding = info.FillPadding,
        RotationAngle = info.RotationAngle,
        MeasuredWidth = info.Root.Bounds.Width,
        MeasuredHeight = info.Root.Bounds.Height,
        LineHeight = (info.TextBox?.LineHeight > 0) ? info.TextBox.LineHeight : info.FontSize * 1.35,
    };
}

/// <summary>
/// 文本图层信息。每个文本元素是一个完整的 Avalonia 可视树，叠加在截图画布上。
/// </summary>
public sealed class TextLayerInfo
{
    public System.Guid Id = System.Guid.NewGuid();
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

    /// <summary>填充不透明度 (0-100)</summary>
    public double FillOpacity;

    /// <summary>填充圆角半径 (0-50)</summary>
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
