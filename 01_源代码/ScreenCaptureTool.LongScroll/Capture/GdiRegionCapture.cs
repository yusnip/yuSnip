using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll.Capture;

/// <summary>
/// 使用 GDI DIB Section 高频采集指定屏幕区域。
/// </summary>
public sealed class GdiRegionCapture : IDisposable
{
    private readonly Rectangle _region;
    private IntPtr _memoryDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private bool _disposed;

    public GdiRegionCapture(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "采集区域宽高必须大于 0。 ");
        }

        _region = region;
        InitializeDib();
    }

    public Rectangle Region => _region;

    public Bitmap Capture()
    {
        ThrowIfDisposed();

        var bitmap = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb);
        if (!TryCapture(bitmap))
        {
            bitmap.Dispose();
            throw new InvalidOperationException("GDI 区域采集失败。 ");
        }

        return bitmap;
    }

    public bool TryCapture(Bitmap target)
    {
        ThrowIfDisposed();
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (target.Width != _region.Width || target.Height != _region.Height)
        {
            throw new ArgumentException("目标 bitmap 尺寸必须与采集区域一致。", nameof(target));
        }

        IntPtr screenDc = IntPtr.Zero;
        BitmapData? data = null;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;

            if (!NativeMethods.BitBlt(
                    _memoryDc,
                    0,
                    0,
                    _region.Width,
                    _region.Height,
                    screenDc,
                    _region.X,
                    _region.Y,
                    NativeMethods.SRCCOPY))
            {
                return false;
            }

            data = target.LockBits(
                new Rectangle(0, 0, target.Width, target.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppRgb);

            int sourceStride = ((_region.Width * 32 + 31) / 32) * 4;
            int destinationStride = data.Stride;
            int absoluteDestinationStride = destinationStride < 0 ? -destinationStride : destinationStride;

            if (absoluteDestinationStride == sourceStride && destinationStride > 0)
            {
                NativeMethods.CopyMemory(data.Scan0, _bits, checked((uint)(sourceStride * _region.Height)));
            }
            else
            {
                int copyBytes = Math.Min(sourceStride, absoluteDestinationStride);
                for (int row = 0; row < _region.Height; row++)
                {
                    IntPtr destination = IntPtr.Add(
                        data.Scan0,
                        destinationStride > 0
                            ? row * destinationStride
                            : (_region.Height - 1 - row) * absoluteDestinationStride);
                    IntPtr source = IntPtr.Add(_bits, row * sourceStride);
                    NativeMethods.CopyMemory(destination, source, checked((uint)copyBytes));
                }
            }

            return true;
        }
        finally
        {
            if (data != null)
            {
                target.UnlockBits(data);
            }
            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupDib();
        GC.SuppressFinalize(this);
    }

    private void InitializeDib()
    {
        IntPtr screenDc = IntPtr.Zero;
        try
        {
            var bitmapInfo = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = _region.Width,
                    biHeight = -_region.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.DIB_RGB_COLORS,
                },
            };

            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetDC 失败。 ");
            }

            _memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC 失败。 ");
            }

            _dibBitmap = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DIB_RGB_COLORS,
                out _bits,
                IntPtr.Zero,
                0);

            if (_dibBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection 失败。 ");
            }

            _oldBitmap = NativeMethods.SelectObject(_memoryDc, _dibBitmap);
            if (_oldBitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("SelectObject 失败。 ");
            }
        }
        finally
        {
            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private void CleanupDib()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memoryDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }

            NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dibBitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dibBitmap);
            _dibBitmap = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GdiRegionCapture));
        }
    }
}
