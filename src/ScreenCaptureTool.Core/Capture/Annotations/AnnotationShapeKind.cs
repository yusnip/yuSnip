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
    Erasure,
}

/// <summary>
/// 工具档（<see cref="ToolMode"/>）↔ 图元类型 的唯一映射，供接 <c>IAnnotationHost</c> 的宿主（贴图、
/// 录屏覆盖层）共用「只清当前工具的图元」这条判据——各自写一份 <c>shape is XxxShape</c> 的 switch，
/// 加一种工具就要同步改多处、漏一处即"清了不该清的"。
/// 截图遮罩的 <c>CaptureSession</c> 走的是它私有的 <c>AnnotationTool</c> 枚举，不经本映射。
/// </summary>
public static class AnnotationToolShapes
{
    /// <summary>该工具档画出来的是哪一类图元。<b>返回 null 有两种</b>：<see cref="ToolMode.None"/> 与
    /// <see cref="ToolMode.Text"/>——文本层是活可视树、不在文档的图元体系里，宿主必须单开一条收口。</summary>
    public static AnnotationShapeKind? KindOfTool(ToolMode tool) => tool switch
    {
        ToolMode.Brush => AnnotationShapeKind.Stroke,
        ToolMode.Arrow => AnnotationShapeKind.Arrow,
        ToolMode.Rect => AnnotationShapeKind.Rectangle,
        ToolMode.Blur => AnnotationShapeKind.Blur,
        ToolMode.Spotlight => AnnotationShapeKind.Spotlight,
        ToolMode.Counter => AnnotationShapeKind.Counter,
        _ => null,
    };

    public static bool MatchesTool(AnnotationShape shape, ToolMode tool)
        => KindOfTool(tool) is { } kind && shape.Kind == kind;
}
