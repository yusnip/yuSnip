using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 模糊/马赛克区域标注。Rect 使用截图/屏幕物理像素坐标。
/// </summary>
public sealed class BlurShape : AnnotationShape
{
    public BlurShape(RectangleF rect, BlurMode mode, int uiLevel, Guid id = default)
        : base(id)
    {
        Rect = Normalize(rect);
        Mode = mode;
        UiLevel = BlurIntensityMapper.ClampUiLevel(uiLevel);
        Intensity = BlurIntensityMapper.UiToPhysical(UiLevel);
        MosaicBlockSize = BlurIntensityMapper.UiLevelToMosaicBlockSize(UiLevel);
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Blur;

    public RectangleF Rect { get; }

    public BlurMode Mode { get; }

    public int UiLevel { get; }

    public double Intensity { get; }

    public int MosaicBlockSize { get; }

    public override RectangleF Bounds => Rect;

    public BlurShape WithRect(RectangleF rect)
    {
        return new BlurShape(rect, Mode, UiLevel, Id);
    }

    public BlurShape WithMode(BlurMode mode)
    {
        return new BlurShape(Rect, mode, UiLevel, Id);
    }

    public BlurShape WithUiLevel(int uiLevel)
    {
        return new BlurShape(Rect, Mode, uiLevel, Id);
    }

    public override AnnotationShape Clone()
    {
        return new BlurShape(Rect, Mode, UiLevel, Id);
    }

    private static RectangleF Normalize(RectangleF rect)
    {
        float left = Math.Min(rect.Left, rect.Right);
        float top = Math.Min(rect.Top, rect.Bottom);
        float right = Math.Max(rect.Left, rect.Right);
        float bottom = Math.Max(rect.Top, rect.Bottom);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }
}
