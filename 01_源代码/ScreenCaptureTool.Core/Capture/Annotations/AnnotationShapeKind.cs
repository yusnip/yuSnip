namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 标注形状类型。阶段 6 各工具会逐步填充对应 Shape 实现。
/// </summary>
public enum AnnotationShapeKind
{
    Stroke,
    Arrow,
    Rectangle,
    Blur,
    Spotlight,
    Counter,
}
