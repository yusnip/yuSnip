using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform.Media;

/// <summary>Windows SDK 的 IMFAttributes／IMFSample 裸槽位。所有对象由创建线程释放。</summary>
public static unsafe class MediaFoundation
{
    public const uint VideoStream = 0xfffffffc, AudioStream = 0xfffffffd, AllStreams = 0xfffffffe;
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid Aac = new("00001610-0000-0010-8000-00aa00389b71");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid BlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid AacPayload = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid AacProfile = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    public static readonly Guid Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid VideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid HardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    public static nint Slot(nint p, int index) => (*(nint**)p)[index];
    public static void Check(int hr)
    {
        if (hr < 0) throw new InvalidOperationException($"Windows 媒体接口失败（0x{hr:X8}）", Marshal.GetExceptionForHR(hr));
    }
    public static void Release(nint p)
    {
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p);
    }
    public static nint Type(Guid major, Guid subtype)
    {
        Check(NativeMethods.MFCreateMediaType(out nint p));
        try { SetGuid(p, MajorType, major); SetGuid(p, Subtype, subtype); return p; }
        catch { Release(p); throw; }
    }
    public static void SetGuid(nint p, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(p, 24))(p, &key, &value));
    public static void Set32(nint p, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(p, 21))(p, &key, value));
    public static void Set64(nint p, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(p, 22))(p, &key, value));
    public static uint Get32(nint p, Guid key, uint fallback = 0)
    {
        uint value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(p, 7))(p, &key, &value) >= 0 ? value : fallback;
    }
    public static ulong Get64(nint p, Guid key)
    {
        ulong value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(p, 8))(p, &key, &value));
        return value;
    }
    public static ulong Pair(uint high, uint low) => ((ulong)high << 32) | low;
    public static nint Sample(ReadOnlySpan<byte> bytes, long time, long duration)
    {
        nint sample = 0, buffer = 0;
        try
        {
            Check(NativeMethods.MFCreateSample(out sample));
            Check(NativeMethods.MFCreateMemoryBuffer((uint)bytes.Length, out buffer));
            nint data = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, nint, int>)Slot(buffer, 3))(buffer, &data, 0, 0));
            try { bytes.CopyTo(new Span<byte>((void*)data, bytes.Length)); }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)bytes.Length));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, duration));
            return sample;
        }
        catch { Release(sample); throw; }
        finally { Release(buffer); }
    }
    public static void CopySample(nint sample, Action<nint, int> copy)
    {
        nint buffer = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));
        try
        {
            nint data = 0; uint size = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, uint*, int>)Slot(buffer, 3))(buffer, &data, 0, &size));
            try { copy(data, checked((int)size)); }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
        }
        finally { Release(buffer); }
    }

    public readonly record struct VideoBufferLayout(int Pitch, uint BufferLength, string Access);

    public static nint TryQuery(nint instance, Guid iid)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result);
        if (hr == unchecked((int)0x80004002)) return 0; // E_NOINTERFACE：按接口能力依次回落。
        Check(hr);
        return result;
    }

    /// <summary>优先按二维缓冲实际 pitch 读 RGB32；线性缓冲使用当前协商的 stride。</summary>
    public static VideoBufferLayout CopyVideoSample(nint sample, byte[] destination, int width, int height, int defaultStride, int bufferHeight = 0)
    {
        int rowBytes = checked(width * 4);
        if (width <= 0 || height <= 0 || destination.Length < checked(rowBytes * height))
            throw new ArgumentException("预览目标尺寸或缓冲长度无效");
        nint buffer = 0, surface = 0;
        try
        {
            uint count = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(sample, 39))(sample, &count));
            if (count == 1)
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(sample, 40))(sample, 0, &buffer));
            else
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));

            surface = TryQuery(buffer, new Guid("33ae5ea6-4316-436f-8ddd-d73d22f829ec")); // IMF2DBuffer2
            if (surface != 0)
            {
                nint firstRow = 0, start = 0; int pitch = 0; uint length = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int*, nint*, uint*, int>)Slot(surface, 10))
                    (surface, 1 /* MF2DBuffer_LockFlags_Read */, &firstRow, &pitch, &start, &length));
                try
                {
                    // Scanline0 已是画面顶行；负 pitch 时直接向低地址走，不能再次翻转。
                    ValidateVideoRange(firstRow, pitch, start, length, rowBytes, height);
                    CopyVideoRows(firstRow, pitch, destination, rowBytes, height);
                    return new VideoBufferLayout(pitch, length, "Lock2DSize");
                }
                finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(surface, 4))(surface)); }
            }

            surface = TryQuery(buffer, new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb")); // IMF2DBuffer
            if (surface != 0)
            {
                nint firstRow = 0; int pitch = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int*, int>)Slot(surface, 3))(surface, &firstRow, &pitch));
                try
                {
                    // 旧接口不提供物理缓冲边界，不以 GetCurrentLength 冒充二维内存长度。
                    ValidateVideoPitch(firstRow, pitch, rowBytes);
                    CopyVideoRows(firstRow, pitch, destination, rowBytes, height);
                    return new VideoBufferLayout(pitch, 0, "Lock2D");
                }
                finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(surface, 4))(surface)); }
            }

            // 线性缓冲没有 pitch 接口，必须使用本次解码输出媒体类型的 stride，保留行填充。
            nint data = 0; uint size = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, uint*, int>)Slot(buffer, 3))(buffer, &data, 0, &size));
            try
            {
                int pitch = defaultStride;
                int storedHeight = bufferHeight == 0 ? height : bufferHeight;
                if (storedHeight < height) throw new InvalidOperationException("解码缓冲高度小于可见画面");
                nint firstRow = pitch < 0 ? data + checked((nint)((storedHeight - 1) * Math.Abs((long)pitch))) : data;
                ValidateVideoRange(firstRow, pitch, data, size, rowBytes, height);
                CopyVideoRows(firstRow, pitch, destination, rowBytes, height);
                return new VideoBufferLayout(pitch, size, "Lock");
            }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
        }
        finally { Release(surface); Release(buffer); }
    }

    private static void ValidateVideoPitch(nint firstRow, int pitch, int rowBytes)
    {
        if (firstRow == 0 || Math.Abs((long)pitch) < rowBytes)
            throw new InvalidOperationException("解码帧的行指针或实际步长无效");
    }

    private static void ValidateVideoRange(nint firstRow, int pitch, nint start, uint length, int rowBytes, int height)
    {
        ValidateVideoPitch(firstRow, pitch, rowBytes);
        long top = checked(firstRow.ToInt64() - start.ToInt64());
        long bottom = checked(top + (long)(height - 1) * pitch);
        if (start == 0 || Math.Min(top, bottom) < 0 || checked(Math.Max(top, bottom) + rowBytes) > length)
            throw new InvalidOperationException("解码帧行范围超出实际缓冲边界");
    }

    private static void CopyVideoRows(nint firstRow, int pitch, byte[] destination, int rowBytes, int height)
    {
        fixed (byte* target = destination)
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy((void*)(firstRow + checked((nint)((long)y * pitch))), target + y * rowBytes, rowBytes, rowBytes);
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }
}

/// <summary>每个媒体工作线程单独平衡 COM／MF 生命周期，不使用反射封送。</summary>
public sealed class MediaFoundationContext : IDisposable
{
    private readonly bool _com;
    private bool _disposed;
    public MediaFoundationContext()
    {
        int hr = NativeMethods.MediaCoInitializeEx(0, 0);
        if (hr < 0 && hr != unchecked((int)0x80010106)) MediaFoundation.Check(hr);
        _com = hr >= 0;
        try { MediaFoundation.Check(NativeMethods.MFStartup(0x20070, 0)); }
        catch { if (_com) NativeMethods.MediaCoUninitialize(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.MFShutdown();
        if (_com) NativeMethods.MediaCoUninitialize();
    }
}
