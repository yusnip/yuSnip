using System.Runtime.InteropServices;
using static ScreenCaptureTool.Platform.Media.MediaFoundation;

namespace ScreenCaptureTool.Platform.Media;

/// <summary>使用系统 WIC 逐帧编码 GIF；裸 COM 调用兼容 NativeAOT，不缓存整段动画。</summary>
public sealed unsafe class WindowsGifWriter : IDisposable
{
    private static readonly Guid Bgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly Guid Indexed = new("6fddc324-4e03-4bfe-b185-3d77768dc904");
    private readonly int _width, _height;
    private readonly bool _com;
    private nint _factory, _stream, _encoder;
    private int _frames;
    private bool _finished, _disposed;

    public WindowsGifWriter(string path, int width, int height)
    {
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "GIF 宽高须在 1～65535 像素内");
        _width = width; _height = height;
        int hr = NativeMethods.MediaCoInitializeEx(0, 0);
        if (hr < 0 && hr != unchecked((int)0x80010106)) Check(hr);
        _com = hr >= 0;
        try
        {
            Guid clsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a"), iid = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out _factory));
            _stream = Create(_factory, 14); // IWICImagingFactory.CreateStream
            fixed (char* name = path)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Slot(_stream, 15))(_stream, name, 0x40000000));
            Guid gif = new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5");
            nint encoder = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, nint*, int>)Slot(_factory, 8))(_factory, &gif, 0, &encoder));
            _encoder = encoder;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(_encoder, 3))(_encoder, _stream, 2 /* NoCache */));
            nint metadata = Create(_encoder, 12);
            try
            {
                SetBytes(metadata, "/appext/application", "NETSCAPE2.0"u8);
                SetBytes(metadata, "/appext/data", [3, 1, 0, 0, 0]); // 无限循环，包含 GIF 子块长度与终止符。
            }
            finally { Release(metadata); }
        }
        catch { Dispose(); throw; }
    }

    public void WriteFrame(byte[] bgra, ushort delayCentiseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed || _finished, this);
        int length = checked(_width * _height * 4);
        if (bgra.Length < length || delayCentiseconds == 0) throw new ArgumentException("GIF 帧缓冲或延时无效");
        nint bitmap = 0, palette = 0, converter = 0, frame = 0, options = 0, metadata = 0;
        fixed (byte* pixels = bgra)
        {
            try
            {
                Guid sourceFormat = Bgra, targetFormat = Indexed;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, uint, uint, byte*, nint*, int>)Slot(_factory, 20))
                    (_factory, (uint)_width, (uint)_height, &sourceFormat, (uint)(_width * 4), (uint)length, pixels, &bitmap));
                palette = Create(_factory, 9);
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int, int>)Slot(palette, 5))(palette, bitmap, 256, 0));
                converter = Create(_factory, 10);
                Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, uint, nint, double, uint, int>)Slot(converter, 8))
                    (converter, bitmap, &targetFormat, 8 /* FloydSteinberg */, palette, 0, 0 /* Custom */));
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint*, int>)Slot(_encoder, 10))(_encoder, &frame, &options));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(frame, 3))(frame, options));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(frame, 4))(frame, (uint)_width, (uint)_height));
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(frame, 6))(frame, &targetFormat));
                if (targetFormat != Indexed) throw new InvalidOperationException("系统 GIF 编码器未接受索引像素格式");
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(frame, 8))(frame, palette));
                // NoCache 的 WriteSource 会立刻写出帧头，控制扩展必须提前设置。
                metadata = Create(frame, 13);
                SetMetadata(metadata, "/grctlext/Delay", new MetadataValue { Type = 18, Short = delayCentiseconds });
                SetMetadata(metadata, "/grctlext/Disposal", new MetadataValue { Type = 17, Byte = 1 });
                SetMetadata(metadata, "/grctlext/TransparencyFlag", new MetadataValue { Type = 11, Short = 0 });
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(frame, 11))(frame, converter, 0));
                Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(frame, 12))(frame));
                _frames++;
            }
            finally
            {
                Release(metadata); Release(frame); Release(options);
                Release(converter); Release(palette); Release(bitmap);
            }
        }
    }

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        if (_frames == 0) throw new InvalidOperationException("GIF 没有可编码的画面");
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_encoder, 11))(_encoder));
        _finished = true;
    }

    private static nint Create(nint owner, int slot)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(owner, slot))(owner, &result));
        return result;
    }

    private static void SetBytes(nint metadata, string name, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* data = bytes)
            SetMetadata(metadata, name, new MetadataValue { Type = 0x1011, Count = (uint)bytes.Length, Pointer = (nint)data });
    }

    private static void SetMetadata(nint metadata, string name, MetadataValue value)
    {
        fixed (char* query = name)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, MetadataValue*, int>)Slot(metadata, 7))(metadata, query, &value));
    }

    // SetMetadataByName 复制值；指针只借用当前固定的 Span，不由 PropVariantClear 释放。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct MetadataValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public ushort Short;
        [FieldOffset(8)] public byte Byte;
        [FieldOffset(8)] public uint Count;
        [FieldOffset(16)] public nint Pointer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release(_encoder); Release(_stream); Release(_factory);
        _encoder = _stream = _factory = 0;
        if (_com) NativeMethods.MediaCoUninitialize();
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
