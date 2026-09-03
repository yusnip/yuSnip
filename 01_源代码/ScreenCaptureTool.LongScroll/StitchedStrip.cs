using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 长截图拼接条带。Y 为条带在最终中间画布中的逻辑位置。
/// </summary>
public sealed class StitchedStrip : IDisposable
{
    private bool _disposed;

    public StitchedStrip(Bitmap bitmap, int y)
        : this(bitmap, y, bitmap?.Width ?? 0, bitmap?.Height ?? 0, 0)
    {
    }

    public StitchedStrip(Bitmap bitmap, int y, int overlaySize)
        : this(bitmap, y, bitmap?.Width ?? 0, bitmap?.Height ?? 0, overlaySize)
    {
    }

    public StitchedStrip(Bitmap bitmap, int y, int width, int height, int overlaySize = 0)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        Y = y;
        Width = width;
        Height = height;
        OverlaySize = overlaySize;
    }

    public Bitmap Bitmap { get; private set; }

    public int Y { get; private set; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>与上一条带重叠的像素高度，用于预览或最终合成裁剪。</summary>
    public int OverlaySize { get; }

    public int Bottom => Y + Height;

    public void OffsetY(int delta)
    {
        Y += delta;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Bitmap.Dispose();
    }
}
