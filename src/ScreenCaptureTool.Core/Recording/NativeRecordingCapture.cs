using System.Drawing;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core.Recording;

/// <summary>会话内复用 GDI DIB，捕获／光标合成／缩放均在编码线程完成。</summary>
internal sealed unsafe class NativeRecordingCapture : IDisposable
{
    private readonly Rectangle _region;
    private readonly int _width, _height;
    private nint _screen, _sourceDc, _targetDc, _sourceBitmap, _targetBitmap, _sourceOld, _targetOld, _sourceBits, _targetBits;
    private HdrScreenCapture? _hdr;
    private nint _lastCursor;
    private int _hotspotX, _hotspotY;
    public NativeRecordingCapture(Rectangle region, int width, int height)
    {
        _region = region; _width = width; _height = height;
        try
        {
            _screen = NativeMethods.GetDC(0);
            if (_screen == 0) throw new InvalidOperationException("无法访问交互桌面");
            CreateDib(region.Width, region.Height, out _sourceDc, out _sourceBitmap, out _sourceOld, out _sourceBits);
            CreateDib(width, height, out _targetDc, out _targetBitmap, out _targetOld, out _targetBits);
            _hdr = HdrScreenCapture.TryCreate(region);
        }
        catch { Dispose(); throw; }
    }
    private void CreateDib(int width, int height, out nint dc, out nint bitmap, out nint old, out nint bits)
    {
        dc = NativeMethods.CreateCompatibleDC(_screen); bitmap = old = bits = 0;
        if (dc == 0) throw new InvalidOperationException("录屏内存 DC 创建失败");
        NativeMethods.BITMAPINFO info = new()
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(), biWidth = width, biHeight = -height,
                biPlanes = 1, biBitCount = 32, biSizeImage = checked(width * height * 4)
            }
        };
        bitmap = NativeMethods.CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
        if (bitmap == 0 || bits == 0) throw new InvalidOperationException("录屏 DIB 创建失败");
        old = NativeMethods.SelectObject(dc, bitmap);
    }
    public ReadOnlySpan<byte> Capture(bool drawCursor)
    {
        // 录制是高频读屏，不使用 CAPTUREBLT 触发系统指针的临时撤下／重画。
        // Windows 桌面合成结果仍由 SRCCOPY 读取，视频内的指针继续单独按热点绘制。
        if (!NativeMethods.BitBlt(_sourceDc, 0, 0, _region.Width, _region.Height, _screen, _region.X, _region.Y,
                NativeMethods.SRCCOPY) || !NativeMethods.GdiFlush())
            throw new InvalidOperationException("录屏桌面采集失败");
        _hdr?.TryCopyTo(_sourceBits, _region.Width * 4);
        if (drawCursor)
        {
            NativeMethods.RecordingCursorInfo cursor = new() { Size = (uint)Marshal.SizeOf<NativeMethods.RecordingCursorInfo>() };
            if (NativeMethods.GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0)
            {
                if (cursor.Cursor != _lastCursor && NativeMethods.GetIconInfo(cursor.Cursor, out var icon))
                {
                    _hotspotX = (int)icon.HotspotX; _hotspotY = (int)icon.HotspotY; _lastCursor = cursor.Cursor;
                    if (icon.Mask != 0) NativeMethods.DeleteObject(icon.Mask);
                    if (icon.Color != 0) NativeMethods.DeleteObject(icon.Color);
                }
                NativeMethods.DrawIconEx(_sourceDc, cursor.X - _region.X - _hotspotX, cursor.Y - _region.Y - _hotspotY,
                    cursor.Cursor, 0, 0, 0, 0, 3);
            }
        }
        NativeMethods.SetStretchBltMode(_targetDc, 4);
        if (!NativeMethods.StretchBlt(_targetDc, 0, 0, _width, _height, _sourceDc, 0, 0,
                _region.Width & ~1, _region.Height & ~1, NativeMethods.SRCCOPY) || !NativeMethods.GdiFlush())
            throw new InvalidOperationException("录屏缩放同步失败");
        return new ReadOnlySpan<byte>((void*)_targetBits, checked(_width * _height * 4));
    }
    public void Dispose()
    {
        _hdr?.Dispose(); _hdr = null;
        if (_sourceOld != 0 && _sourceDc != 0) NativeMethods.SelectObject(_sourceDc, _sourceOld);
        if (_targetOld != 0 && _targetDc != 0) NativeMethods.SelectObject(_targetDc, _targetOld);
        if (_sourceBitmap != 0) NativeMethods.DeleteObject(_sourceBitmap);
        if (_targetBitmap != 0) NativeMethods.DeleteObject(_targetBitmap);
        if (_sourceDc != 0) NativeMethods.DeleteDC(_sourceDc);
        if (_targetDc != 0) NativeMethods.DeleteDC(_targetDc);
        if (_screen != 0) NativeMethods.ReleaseDC(0, _screen);
        _sourceOld = _targetOld = _sourceDc = _targetDc = _sourceBitmap = _targetBitmap = _screen = 0;
    }
}
