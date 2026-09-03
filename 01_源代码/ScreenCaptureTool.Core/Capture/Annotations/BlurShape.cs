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

    /// <summary>兼容旧调用：第三个参数作为马赛克块大小时，反推最近 UI 档位。</summary>
    public BlurShape(RectangleF rect, BlurMode mode, int blockSize, bool fromBlockSize, Guid id = default)
        : this(rect, mode, fromBlockSize ? BlockSizeToNearestUiLevel(blockSize) : blockSize, id)
    {
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Blur;

    public RectangleF Rect { get; }

    public BlurMode Mode { get; }

    public int UiLevel { get; }

    public double Intensity { get; }

    public int MosaicBlockSize { get; }

    public int BlockSize => MosaicBlockSize;

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

    private static int BlockSizeToNearestUiLevel(int blockSize)
    {
        if (blockSize <= 0) return 0;
        int bestLevel = 1;
        int bestDistance = int.MaxValue;
        for (int level = 1; level <= BlurIntensityMapper.MaxUiLevel; level++)
        {
            int candidate = BlurIntensityMapper.UiLevelToMosaicBlockSize(level);
            int distance = Math.Abs(candidate - blockSize);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestLevel = level;
            }
        }
        return bestLevel;
    }
}
