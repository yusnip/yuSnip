using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 截图标注形状基类。坐标采用截图位图坐标系（物理像素），不是窗口 DIP。
/// </summary>
public abstract class AnnotationShape
{
    protected AnnotationShape(Guid id)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
    }

    public Guid Id { get; }

    public abstract AnnotationShapeKind Kind { get; }

    public abstract RectangleF Bounds { get; }

    public abstract AnnotationShape Clone();
}
