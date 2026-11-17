namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 一条笔迹栅格化后的覆盖率位图：单通道字节，0=没有墨、255=完全覆盖。
/// 颜色与不透明度在贴图时才乘上去，所以同一份缓冲既能喂给预览（Avalonia）也能喂给成品（GDI+），
/// 改标注颜色也不会让它失效。
/// </summary>
public sealed class BrushCoverage
{
    public BrushCoverage(byte[] data, int width, int height, int originX, int originY, long key)
    {
        Data = data;
        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
        Key = key;
    }

    /// <summary>行主序的单通道覆盖率，每行 <see cref="Width"/> 个字节。</summary>
    public byte[] Data { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>缓冲左上角的屏幕物理像素横坐标（绝对坐标，两侧贴图时各自换算）。</summary>
    public int OriginX { get; }

    public int OriginY { get; }

    /// <summary>笔迹几何 + 笔刷画像 + 线型的指纹（不含颜色），用作各侧位图视图的缓存键。</summary>
    public long Key { get; }
}
