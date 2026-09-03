namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 标注工具模式。阶段 6 的具体工具（画笔/箭头/文字/矩形/模糊/聚光灯/计数）将以此驱动。
    /// 阶段 4 仅占位，<see cref="None"/> 表示当前不在任何标注工具下。
    /// </summary>
    public enum ToolMode
    {
        None = 0,
        Brush,
        Arrow,
        Text,
        Rect,
        Blur,
        Spotlight,
        Counter,
    }
}
