using Avalonia;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Services;

/// <summary>标注输入宿主：内容坐标与窗口坐标的转换只由宿主负责。</summary>
internal interface IAnnotationHost
{
    AnnotationDocument AnnotationDocument { get; }
    System.Collections.Generic.IReadOnlyList<System.Guid> AnnotationTextIds { get; }
    ToolMode ActiveAnnotationTool { get; }
    Rect AnnotationContentBounds { get; }

    /// <summary>将窗口 DIP 转为内容坐标；超出内容时不允许起笔。</summary>
    bool TryGetAnnotationPoint(Point windowPoint, out Point contentPoint);

    /// <summary>提交一次已完成的操作，从源内容重新合成，不修改源模型。</summary>
    void CommitAnnotationChange();

    /// <summary>只刷新临时笔迹，不生成历史或写入输出图。</summary>
    void InvalidateAnnotationPreview();
    void SetEraserRectanglePreview(System.Drawing.RectangleF? rect);

    /// <summary>标注图层最近一次整层重绘的实际耗时（毫秒）。擦除的合帧闸门只认这个读数：写文档那一下几乎不花时间，
    /// 真正压住鼠标的是随后排在 UI 线程上的那次重绘（截图侧同一口径，见 <c>CaptureSession.EraserPublishGateMs</c>）。</summary>
    double AnnotationLayerRenderMs { get; }
}
