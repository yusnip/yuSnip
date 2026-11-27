using System.Linq;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Core.Scripting;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Controls.ActionEditor;

internal static class ActionModuleIconHelper
{
    // 统一 24×24 坐标系的极简清晰矢量图标（告别模糊字符 ↑↓▾→）
    public const string GripDotsSvg = "M9 6 A1.3 1.3 0 1 0 9 8.6 A1.3 1.3 0 1 0 9 6 Z M15 6 A1.3 1.3 0 1 0 15 8.6 A1.3 1.3 0 1 0 15 6 Z M9 11 A1.3 1.3 0 1 0 9 13.6 A1.3 1.3 0 1 0 9 11 Z M15 11 A1.3 1.3 0 1 0 15 13.6 A1.3 1.3 0 1 0 15 11 Z M9 16 A1.3 1.3 0 1 0 9 18.6 A1.3 1.3 0 1 0 9 16 Z M15 16 A1.3 1.3 0 1 0 15 18.6 A1.3 1.3 0 1 0 15 16 Z";
    public const string ArrowUpSvg = "M12 19 V5 M6 11 L12 5 L18 11";
    public const string ArrowDownSvg = "M12 5 V19 M6 13 L12 19 L18 13";
    public const string ChevronDownSvg = "M6 9 L12 15 L18 9";
    public const string ChevronRightSvg = "M9 6 L15 12 L9 18";
    public const string FlowDownSvg = "M12 3 V17 M7.5 12.5 L12 17.5 L16.5 12.5";
    public const string EditPencilSvg = "M4 20 H8 L19 9 L15 5 L4 16 V20 Z M13.5 6.5 L17.5 10.5";
    public const string MinusSvg = "M6 12 H18";
    public const string PlusSvg = "M12 6 V18 M6 12 H18";

    public static Color CategoryAccentColor(ActionModuleCategory category) => category switch
    {
        ActionModuleCategory.Input => Color.Parse("#6366F1"),    // 蓝紫：获取内容
        ActionModuleCategory.Process => Color.Parse("#0EA5E9"),  // 青蓝：智能处理
        ActionModuleCategory.Output => Color.Parse("#10B981"),   // 翠绿：结果输出
        _ => Color.Parse("#F59E0B")                              // 琥珀：互动与控制
    };

    public static IBrush CategoryAccentBrush(ActionModuleCategory category) =>
        new SolidColorBrush(CategoryAccentColor(category));

    public static IBrush CategorySoftFillBrush(ActionModuleCategory category)
    {
        var c = CategoryAccentColor(category);
        return new SolidColorBrush(Color.FromArgb(26, c.R, c.G, c.B));
    }

    public static IBrush ContentKindBrush(ActionContentKind kind) => kind switch
    {
        ActionContentKind.Image => new SolidColorBrush(Color.Parse("#6366F1")),
        ActionContentKind.Text => new SolidColorBrush(Color.Parse("#0EA5E9")),
        _ => new SolidColorBrush(Color.Parse("#8B8697"))
    };

    public static IBrush ContentKindSoftBrush(ActionContentKind kind) => kind switch
    {
        ActionContentKind.Image => new SolidColorBrush(Color.FromArgb(28, 99, 102, 241)),
        ActionContentKind.Text => new SolidColorBrush(Color.FromArgb(28, 14, 165, 233)),
        _ => new SolidColorBrush(Color.FromArgb(22, 139, 134, 151))
    };

    public static Avalonia.Controls.Shapes.Path CreateVectorStrokeIcon(string svgData, double size = 13, IBrush? stroke = null, double thickness = 1.65)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(svgData),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = null,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (stroke != null) path.Stroke = stroke;
        else path.Classes.Add("actionModuleStroke");
        return path;
    }

    public static Avalonia.Controls.Shapes.Path CreateVectorFillIcon(string svgData, double size = 13, IBrush? fill = null)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(svgData),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (fill != null) path.Fill = fill;
        else path.Classes.Add("sctIcon");
        return path;
    }

    public static Avalonia.Controls.Shapes.Path CreateIcon(string iconKey, IBrush? accentOverride = null, double size = 14)
    {
        if (ToolbarRegistry.Buttons.FirstOrDefault(t => t.Id == iconKey) is { } tool)
        {
            var icon = OcrIconGeometry.CreateIcon(tool.IconPath, tool.IsFill ? "sctIcon" : "actionModuleStroke");
            icon.Width = icon.Height = size * tool.IconScale;
            if (accentOverride != null)
            {
                if (tool.IsFill) icon.Fill = accentOverride;
                else icon.Stroke = accentOverride;
            }
            return icon;
        }

        string? geom = iconKey switch
        {
            "copy" => OcrIconGeometry.CopyOutline,
            "fullscreen" => OcrIconGeometry.AspectRatio,
            "region" => OcrIconGeometry.AspectRatio,
            "paste" => OcrIconGeometry.Paste,
            "wait" => OcrIconGeometry.TrayHistory,
            "close" => OcrIconGeometry.Close,
            _ => OcrIconGeometry.Image
        };

        bool isStroke = iconKey == "copy";
        var path = OcrIconGeometry.CreateIcon(geom, isStroke ? "actionModuleStroke" : "sctIcon");
        path.Width = path.Height = size;
        path.VerticalAlignment = VerticalAlignment.Center;
        path.HorizontalAlignment = HorizontalAlignment.Center;
        if (accentOverride != null)
        {
            if (isStroke) path.Stroke = accentOverride;
            else path.Fill = accentOverride;
        }
        return path;
    }
}
