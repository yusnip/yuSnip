using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// An owned bitmap strip positioned in the canvas' signed along-axis coordinate space.
/// Axis is intentionally owned by <c>LongScrollCanvasState</c>, not by each strip.
/// </summary>
public sealed class StitchedStrip : IDisposable
{
    private int _alongStart;
    private readonly bool _ownsBitmap;
    private bool _disposed;

    /// <summary>Vertical compatibility constructor.</summary>
    public StitchedStrip(Bitmap bitmap, int y)
        : this(bitmap, y, bitmap?.Width ?? 0, bitmap?.Height ?? 0, 0)
    {
    }

    /// <summary>Vertical compatibility constructor.</summary>
    public StitchedStrip(Bitmap bitmap, int y, int overlaySize)
        : this(bitmap, y, bitmap?.Width ?? 0, bitmap?.Height ?? 0, overlaySize)
    {
    }

    /// <summary>
    /// Vertical compatibility constructor. Width maps to cross length and height maps
    /// to along length. New axis-neutral code should use the overload with ScrollAxis.
    /// </summary>
    public StitchedStrip(Bitmap bitmap, int y, int width, int height, int overlaySize = 0)
        : this(bitmap, y, width, height, overlaySize, ScrollAxis.Vertical)
    {
    }

    /// <summary>Creates a strip from physical bitmap geometry for the selected axis.</summary>
    public StitchedStrip(
        Bitmap bitmap,
        int alongStart,
        int crossLength,
        int alongLength,
        int overlapLength,
        ScrollAxis axis)
        : this(bitmap, alongStart, crossLength, alongLength, overlapLength, axis, ownsBitmap: true)
    {
    }

    private StitchedStrip(
        Bitmap bitmap,
        int alongStart,
        int crossLength,
        int alongLength,
        int overlapLength,
        ScrollAxis axis,
        bool ownsBitmap)
    {
        AxisGeometry.Validate(axis);
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        if (crossLength <= 0) throw new ArgumentOutOfRangeException(nameof(crossLength));
        if (alongLength <= 0) throw new ArgumentOutOfRangeException(nameof(alongLength));
        if (overlapLength < 0 || overlapLength > alongLength)
            throw new ArgumentOutOfRangeException(nameof(overlapLength));

        int bitmapCross = AxisGeometry.CrossLength(axis, bitmap);
        int bitmapAlong = AxisGeometry.AlongLength(axis, bitmap);
        if (crossLength > bitmapCross)
            throw new ArgumentException("Declared cross length exceeds the bitmap.", nameof(crossLength));
        if (alongLength > bitmapAlong)
            throw new ArgumentException("Declared along length exceeds the bitmap.", nameof(alongLength));

        _ = checked(alongStart + alongLength);
        _alongStart = alongStart;
        _ownsBitmap = ownsBitmap;
        Axis = axis;
        CrossLength = crossLength;
        AlongLength = alongLength;
        OverlapLength = overlapLength;
        Width = axis == ScrollAxis.Vertical ? crossLength : alongLength;
        Height = axis == ScrollAxis.Vertical ? alongLength : crossLength;
    }

    public Bitmap Bitmap { get; }

    /// <summary>Scroll axis the strip geometry is expressed in.</summary>
    public ScrollAxis Axis { get; }

    /// <summary>Signed logical position. It may be negative after a leading expansion.</summary>
    public int AlongStart => _alongStart;

    public int AlongLength { get; }

    public int CrossLength { get; }

    /// <summary>Leading portion that overlaps content already present in the canvas.</summary>
    public int OverlapLength { get; }

    public int AlongEnd => checked(_alongStart + AlongLength);

    // Vertical compatibility surface. Do not use these for new horizontal code.
    public int Y => _alongStart;
    public int Width { get; }
    public int Height { get; }
    public int OverlaySize => OverlapLength;
    public int Bottom => checked(Y + Height);

    /// <summary>
    /// Compatibility-only mutation used by the legacy vertical session. The axis-neutral
    /// canvas never calls this method when prepending; its strip coordinates remain stable.
    /// </summary>
    public void OffsetY(int delta)
    {
        _alongStart = checked(_alongStart + delta);
    }

    public StitchedStrip CloneOwned(ScrollAxis axis)
    {
        ThrowIfDisposed();
        Bitmap clone;
        lock (GetBitmapSyncRoot(Bitmap)) clone = (Bitmap)Bitmap.Clone();
        try
        {
            return new StitchedStrip(clone, AlongStart, CrossLength, AlongLength, OverlapLength, axis);
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a metadata-only preview lease. It deliberately does not own or clone the
    /// source bitmap, avoiding a full-canvas pixel copy on every preview refresh.
    /// The producer must retain the bitmap until the preview renderer has stopped.
    /// </summary>
    public StitchedStrip CreatePreviewReference(ScrollAxis axis)
    {
        lock (GetBitmapSyncRoot(Bitmap))
        {
            ThrowIfDisposed();
            int maxCross = AxisGeometry.CrossLength(axis, Bitmap);
            int maxAlong = AxisGeometry.AlongLength(axis, Bitmap);
            int cross = Math.Min(CrossLength, maxCross);
            int along = Math.Min(AlongLength, maxAlong);
            if (cross <= 0) cross = maxCross;
            if (along <= 0) along = maxAlong;
            return new StitchedStrip(Bitmap, AlongStart, cross, along, Math.Min(OverlapLength, along), axis, ownsBitmap: false);
        }
    }

    private sealed class ThumbnailSet
    {
        public Dictionary<int, Bitmap>? ByTarget;
    }

    // Cross-frame thumbnail cache keyed by source bitmap: preview wrapper strips are
    // recreated on every preview update, so a per-instance cache never hits across
    // frames. Downscales happen once per (source bitmap, target length) pair and die
    // with the source (weak key), so a changed preview target never stays stuck on
    // an entry sized for an older, different canvas.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, ThumbnailSet> ThumbnailCache = new();

    // 包装条带与原条带必须按同一源位图同步；锁包装对象无法保护接缝扫描与预览的 GDI 访问。
    internal static object GetBitmapSyncRoot(Bitmap bitmap) => ThumbnailCache.GetOrCreateValue(bitmap);

    /// <summary>
    /// 获取/生成轻量化预览缩略图（单次缩放并按目标尺寸缓存），避免预览窗在每一帧遍历重复下采样 2K/4K 巨幅原图。
    /// 最终长截图合成始终使用 100% 原始全分辨率无损原图 Bitmap。
    /// </summary>
    public Bitmap GetThumbnailBitmap(int targetCrossLength = 160)
    {
        if (_disposed) return Bitmap;
        lock (GetBitmapSyncRoot(Bitmap))
        {
            try
            {
                ThumbnailSet set = ThumbnailCache.GetOrCreateValue(Bitmap);
                if (set.ByTarget != null && set.ByTarget.TryGetValue(targetCrossLength, out var cached))
                {
                    return cached;
                }
                Bitmap thumb = CreateThumbnail(targetCrossLength);
                (set.ByTarget ??= new Dictionary<int, Bitmap>())[targetCrossLength] = thumb;
                return thumb;
            }
            catch
            {
                return Bitmap;
            }
        }
    }

    private Bitmap CreateThumbnail(int targetCrossLength)
    {
        int srcW = Bitmap.Width;
        int srcH = Bitmap.Height;
        // The preview draws the thumbnail at canvas cross length, so the downscale
        // aligns to the cross axis. Scaling by max(w,h) over-shrank the cross axis
        // of wide strips (the first strip spans the whole viewport width) and the
        // stretch-back on draw rendered the first preview frame blurry.
        int crossLen = AxisGeometry.CrossLength(Axis, Bitmap);
        if (crossLen <= 0) return Bitmap;
        if (crossLen <= targetCrossLength)
        {
            return Bitmap;
        }

        double scale = (double)targetCrossLength / crossLen;
        int thumbW = Math.Max(1, (int)Math.Round(srcW * scale));
        int thumbH = Math.Max(1, (int)Math.Round(srcH * scale));
        var thumb = new Bitmap(thumbW, thumbH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(Bitmap, new Rectangle(0, 0, thumbW, thumbH), new Rectangle(0, 0, srcW, srcH), GraphicsUnit.Pixel);
            return thumb;
        }
        catch { thumb.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Thumbnails are shared through ThumbnailCache (possibly by many wrapper
        // strips); their lifetime follows the source bitmap, never disposed here.
        if (_ownsBitmap) Bitmap.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StitchedStrip));
    }
}
