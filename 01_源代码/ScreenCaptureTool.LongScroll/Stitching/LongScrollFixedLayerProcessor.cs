using System.Drawing;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>固定顶部/底部层处理器：完整帧拆分为固定层与可滚动中间区域。</summary>
public sealed class LongScrollFixedLayerProcessor : IDisposable
{
    private readonly LongScrollOptions _options;
    private readonly object _sync = new();
    private Bitmap? _fixedTop;
    private Bitmap? _fixedBottom;
    private bool _disposed;

    public LongScrollFixedLayerProcessor(LongScrollOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool Enabled => _options.EnableFixedLayerMode;

    public int FixedTopHeight { get; private set; }

    public int FixedBottomHeight { get; private set; }

    public void Initialize(Bitmap firstFrame)
    {
        if (firstFrame == null) throw new ArgumentNullException(nameof(firstFrame));
        ThrowIfDisposed();

        CalculateFixedLayerHeights(firstFrame.Height, out int topH, out int bottomH);
        lock (_sync)
        {
            FixedTopHeight = topH;
            FixedBottomHeight = bottomH;
            ReplaceTopLocked(CropFixedTop(firstFrame, topH));
            ReplaceBottomLocked(CropFixedBottom(firstFrame, bottomH));
        }
    }

    public Bitmap? CropScrollableMiddle(Bitmap fullFrame)
    {
        if (fullFrame == null) return null;
        ThrowIfDisposed();

        int topH;
        int bottomH;
        lock (_sync)
        {
            topH = FixedTopHeight;
            bottomH = FixedBottomHeight;
        }

        return CropScrollableMiddle(fullFrame, topH, bottomH);
    }

    public void UpdateTop(Bitmap fullFrame)
    {
        if (!Enabled || fullFrame == null) return;
        ThrowIfDisposed();
        Bitmap? top;
        lock (_sync)
        {
            top = CropFixedTop(fullFrame, FixedTopHeight);
            if (top == null && FixedTopHeight > 0) return;
            ReplaceTopLocked(top);
        }
    }

    public void UpdateBottom(Bitmap fullFrame)
    {
        if (!Enabled || fullFrame == null) return;
        ThrowIfDisposed();
        Bitmap? bottom;
        lock (_sync)
        {
            bottom = CropFixedBottom(fullFrame, FixedBottomHeight);
            if (bottom == null && FixedBottomHeight > 0) return;
            ReplaceBottomLocked(bottom);
        }
    }

    public Bitmap? SnapshotFixedTop()
    {
        lock (_sync)
        {
            return _fixedTop;
        }
    }

    public Bitmap? SnapshotFixedBottom()
    {
        lock (_sync)
        {
            return _fixedBottom;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            SafeDispose(_fixedTop);
            SafeDispose(_fixedBottom);
            _fixedTop = null;
            _fixedBottom = null;
        }
    }

    private void CalculateFixedLayerHeights(int frameHeight, out int topH, out int bottomH)
    {
        topH = 0;
        bottomH = 0;
        if (!Enabled || frameHeight <= 0) return;

        topH = (int)Math.Round(frameHeight * _options.FixedTopRatio);
        bottomH = (int)Math.Round(frameHeight * _options.FixedBottomRatio);
        if (topH > _options.MaxFixedLayerHeightPx) topH = _options.MaxFixedLayerHeightPx;
        if (bottomH > _options.MaxFixedLayerHeightPx) bottomH = _options.MaxFixedLayerHeightPx;
        if (topH < 0) topH = 0;
        if (bottomH < 0) bottomH = 0;

        int minMiddle = (int)Math.Round(frameHeight * 0.65);
        if (minMiddle < 80) minMiddle = 80;
        int middle = frameHeight - topH - bottomH;
        if (middle >= minMiddle) return;

        int allowedFixed = frameHeight - minMiddle;
        if (allowedFixed < 0) allowedFixed = 0;
        double total = topH + bottomH;
        if (total <= 0)
        {
            topH = 0;
            bottomH = 0;
            return;
        }

        topH = (int)Math.Round(allowedFixed * (topH / total));
        bottomH = allowedFixed - topH;
    }

    private Bitmap? CropFixedTop(Bitmap bmp, int topH)
    {
        if (!Enabled || bmp == null || topH <= 0) return null;
        if (topH > bmp.Height) topH = bmp.Height;
        return bmp.Clone(new Rectangle(0, 0, bmp.Width, topH), bmp.PixelFormat);
    }

    private Bitmap? CropFixedBottom(Bitmap bmp, int bottomH)
    {
        if (!Enabled || bmp == null || bottomH <= 0) return null;
        if (bottomH > bmp.Height) bottomH = bmp.Height;
        return bmp.Clone(new Rectangle(0, bmp.Height - bottomH, bmp.Width, bottomH), bmp.PixelFormat);
    }

    private Bitmap? CropScrollableMiddle(Bitmap bmp, int topH, int bottomH)
    {
        if (bmp == null) return null;
        if (!Enabled)
        {
            return (Bitmap)bmp.Clone();
        }

        if (topH < 0) topH = 0;
        if (bottomH < 0) bottomH = 0;
        if (topH > bmp.Height) topH = bmp.Height;
        if (bottomH > bmp.Height - topH) bottomH = bmp.Height - topH;
        int middleH = bmp.Height - topH - bottomH;
        if (middleH <= 0) return null;
        return bmp.Clone(new Rectangle(0, topH, bmp.Width, middleH), bmp.PixelFormat);
    }

    private void ReplaceTopLocked(Bitmap? bitmap)
    {
        SafeDispose(_fixedTop);
        _fixedTop = bitmap;
    }

    private void ReplaceBottomLocked(Bitmap? bitmap)
    {
        SafeDispose(_fixedBottom);
        _fixedBottom = bitmap;
    }

    private static void SafeDispose(Bitmap? bitmap)
    {
        try { bitmap?.Dispose(); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LongScrollFixedLayerProcessor));
        }
    }
}
