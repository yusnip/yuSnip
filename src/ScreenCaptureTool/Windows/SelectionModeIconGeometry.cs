using Avalonia.Media;

namespace ScreenCaptureTool.Windows;

/// <summary>Material 单色填充几何，与项目既有图标相同的 Path 资产方式。</summary>
public static class SelectionModeIconGeometry
{
    public static Geometry FullScreen { get; } = Geometry.Parse("M3,3 H9 V5 H5 V9 H3 Z M15,3 H21 V9 H19 V5 H15 Z M19,15 H21 V21 H15 V19 H19 Z M3,15 H5 V19 H9 V21 H3 Z");
    public static Geometry Rectangle { get; } = Geometry.Parse("M3,3 H21 V21 H3 Z M5,5 V19 H19 V5 Z");
    public static Geometry Pen { get; } = Geometry.Parse("M12,2 L20,10 L16,20 L4,22 L2,20 L4,8 Z M12,4.83 L5.88,9.91 L4.28,18.31 L9.2,13.39 A2,2 0 1 1 10.61,14.8 L5.69,19.72 L14.55,18.24 L17.66,10.49 Z");
    public static Geometry Freehand { get; } = Geometry.Parse("M12,3 C6,3 2,6 2,10 C2,13 4,15 7,16 C5,18 5,21 8,22 C11,23 13,20 11,18 L9,19 C10,20 9,21 8,20 C7,19 8,17 9,16 C10,17 11,17 12,17 C18,17 22,14 22,10 C22,6 18,3 12,3 Z M12,5 C17,5 20,7 20,10 C20,13 17,15 12,15 C7,15 4,13 4,10 C4,7 7,5 12,5 Z");
}
