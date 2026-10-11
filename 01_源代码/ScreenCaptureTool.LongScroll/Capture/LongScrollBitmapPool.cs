using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.LongScroll.Capture;

/// <summary>长截图采集 Bitmap 复用池，降低高频采集时的 GC 压力。</summary>
public sealed class LongScrollBitmapPool : IDisposable
{
    private readonly ConcurrentBag<Bitmap> _pool = new();
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public LongScrollBitmapPool(int width, int height, int preallocateCount = 4)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        _width = width;
        _height = height;

        for (int i = 0; i < Math.Max(0, preallocateCount); i++)
        {
            _pool.Add(CreateBitmap());
        }
    }

    public Bitmap Rent()
    {
        ThrowIfDisposed();
        if (_pool.TryTake(out Bitmap? bitmap) && bitmap != null)
        {
            return bitmap;
        }

        return CreateBitmap();
    }

    public void Return(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        if (_disposed)
        {
            SafeDispose(bitmap);
            return;
        }

        if (bitmap.Width == _width && bitmap.Height == _height)
        {
            _pool.Add(bitmap);
            return;
        }

        SafeDispose(bitmap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_pool.TryTake(out Bitmap? bitmap))
        {
            SafeDispose(bitmap);
        }
    }

    private Bitmap CreateBitmap()
    {
        return new Bitmap(_width, _height, PixelFormat.Format32bppRgb);
    }

    private static void SafeDispose(Bitmap bitmap)
    {
        try { bitmap.Dispose(); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LongScrollBitmapPool));
        }
    }
}
