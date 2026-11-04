using System.Runtime.InteropServices;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Platform.Media;
using static ScreenCaptureTool.Platform.Media.MediaFoundation;

namespace ScreenCaptureTool.Core.Recording;

/// <summary>进程内 Windows H.264／AAC 编码与 MP4 封装，不启动 FFmpeg。</summary>
public sealed unsafe class WindowsMp4Writer : IDisposable
{
    private readonly MediaFoundationContext _context;
    private nint _writer;
    private uint _video;
    private readonly uint[] _audio;
    private bool _finished;
    public WindowsMp4Writer(string path, int width, int height, int fps, long bitrate, int audioTracks,
        bool enableHardware = true)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 || fps <= 0 || audioTracks is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(width), "编码尺寸必须为正偶数，音轨最多两路");
        _context = new MediaFoundationContext();
        _audio = new uint[audioTracks];
        nint attributes = 0;
        try
        {
            Check(NativeMethods.MFCreateAttributes(out attributes, 2));
            Set32(attributes, HardwareTransforms, enableHardware ? 1u : 0u);
            Set32(attributes, DisableThrottling, 1);
            Check(NativeMethods.MFCreateSinkWriterFromURL(path, 0, attributes, out _writer));
            nint output = Type(Video, H264), input = 0;
            try
            {
                Set32(output, Bitrate, checked((uint)Math.Clamp(bitrate, 100_000, 200_000_000)));
                Set64(output, FrameSize, Pair((uint)width, (uint)height));
                Set64(output, FrameRate, Pair((uint)fps, 1));
                Set64(output, PixelAspect, Pair(1, 1));
                Set32(output, Interlace, 2);
                uint stream = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(_writer, 3))(_writer, output, &stream));
                _video = stream;
                input = Type(Video, Rgb32);
                Set64(input, FrameSize, Pair((uint)width, (uint)height));
                Set64(input, FrameRate, Pair((uint)fps, 1));
                Set64(input, PixelAspect, Pair(1, 1));
                Set32(input, Interlace, 2);
                Set32(input, Stride, checked((uint)(width * 4)));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(_writer, 4))(_writer, _video, input, 0));
            }
            finally { Release(input); Release(output); }
            for (int i = 0; i < _audio.Length; i++) _audio[i] = AddAudio();
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_writer, 5))(_writer));
        }
        catch { Release(_writer); _writer = 0; _context.Dispose(); throw; }
        finally { Release(attributes); }
    }
    private uint AddAudio()
    {
        nint output = Type(Audio, Aac), input = 0;
        try
        {
            Set32(output, Channels, 2); Set32(output, SampleRate, 48000);
            Set32(output, BytesPerSecond, 20000); Set32(output, BitsPerSample, 16);
            Set32(output, AacPayload, 0); Set32(output, AacProfile, 0x29);
            uint stream = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(_writer, 3))(_writer, output, &stream));
            input = Type(Audio, Pcm);
            Set32(input, Channels, 2); Set32(input, SampleRate, 48000);
            Set32(input, BytesPerSecond, 192000); Set32(input, BlockAlign, 4); Set32(input, BitsPerSample, 16);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(_writer, 4))(_writer, stream, input, 0));
            return stream;
        }
        finally { Release(input); Release(output); }
    }
    public void WriteVideo(ReadOnlySpan<byte> bgra, long time100ns, long duration100ns) => Write(_video, bgra, time100ns, duration100ns);
    public void WriteAudio(int track, ReadOnlySpan<byte> pcm16, long sampleFrame)
    {
        long frames = pcm16.Length / 4;
        long start = sampleFrame * 10_000_000 / 48000;
        Write(_audio[track], pcm16, start, (sampleFrame + frames) * 10_000_000 / 48000 - start);
    }
    private void Write(uint stream, ReadOnlySpan<byte> bytes, long time, long duration)
    {
        ObjectDisposedException.ThrowIf(_writer == 0 || _finished, this);
        nint sample = Sample(bytes, time, duration);
        try { Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(_writer, 6))(_writer, stream, sample)); }
        finally { Release(sample); }
    }
    public void Finish()
    {
        if (_finished || _writer == 0) return;
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_writer, 11))(_writer));
        _finished = true;
    }
    public void Dispose()
    {
        Release(_writer); _writer = 0;
        _context.Dispose();
    }
}

/// <summary>Source Reader 同步解码，仅在所属解码线程使用；视频返回原尺寸 BGRA，音频返回 48k 双声道 PCM16。</summary>
public sealed unsafe class WindowsMediaReader : IDisposable
{
    private readonly MediaFoundationContext _context;
    private nint _reader;
    private readonly uint _stream;
    private int _stride;
    private int _bufferHeight;
    private bool _loggedVideoLayout;
    public ClipInfo Info { get; } = new();
    public WindowsMediaReader(string path, bool audio = false, int audioOrdinal = 0)
    {
        _context = new MediaFoundationContext();
        _stream = audio ? AudioStream : VideoStream;
        nint attributes = 0, type = 0, current = 0;
        try
        {
            Check(NativeMethods.MFCreateAttributes(out attributes, 1));
            Set32(attributes, VideoProcessing, 1);
            Check(NativeMethods.MFCreateSourceReaderFromURL(path, attributes, out _reader));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_reader, 4))(_reader, AllStreams, 0));
            if (audio)
            {
                var streams = new List<uint>();
                for (uint i = 0; i < 32; i++)
                {
                    nint native = 0;
                    int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_reader, 5))(_reader, i, 0, &native);
                    if (hr < 0) break;
                    try
                    {
                        Guid major = default, key = MajorType;
                        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(native, 10))(native, &key, &major));
                        if (major == Audio) streams.Add(i);
                    }
                    finally { Release(native); }
                }
                if (audioOrdinal < 0 || audioOrdinal >= streams.Count) throw new InvalidOperationException("指定音轨不存在");
                // 本程序 MP4 母带由 Windows MPEG-4 源逆序枚举；按写入顺序还原系统声／麦克风双轨。
                // 不能以 FIRST_AUDIO_STREAM 当作文件首轨，否则分离档预览会只播放麦克风。
                _stream = streams[streams.Count - 1 - audioOrdinal];
                AppLogger.Info($"Windows 音轨选择：文件轨={audioOrdinal} 系统流={_stream} 音轨数={streams.Count}");
            }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_reader, 4))(_reader, _stream, 1));
            type = Type(audio ? Audio : Video, audio ? Pcm : Rgb32);
            if (audio)
            {
                Set32(type, Channels, 2); Set32(type, SampleRate, 48000);
                Set32(type, BitsPerSample, 16); Set32(type, BlockAlign, 4); Set32(type, BytesPerSecond, 192000);
            }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(_reader, 7))(_reader, _stream, 0, type));
            nint result = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, _stream, &result));
            current = result;
            if (!audio)
            {
                ulong size = Get64(current, FrameSize), rate = Get64(current, FrameRate);
                Info.Width = (int)(size >> 32); Info.Height = (int)(uint)size;
                _bufferHeight = Info.Height;
                Info.Fps = (uint)rate == 0 ? 25 : (rate >> 32) / (double)(uint)rate;
                _stride = unchecked((int)Get32(current, Stride, (uint)(Info.Width * 4)));
                nint audioType = 0;
                Info.HasAudio = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_reader, 5))(_reader, AudioStream, 0, &audioType) >= 0;
                Release(audioType);
            }
            Guid durationKey = Duration;
            PropVariant value = default;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, PropVariant*, int>)Slot(_reader, 12))(_reader, 0xffffffff, &durationKey, &value));
            if (value.Type != 21 && value.Type != 20) throw new InvalidOperationException("媒体时长类型无效");
            Info.Duration = value.Value / 10_000_000.0;
        }
        catch { Release(_reader); _reader = 0; _context.Dispose(); throw; }
        finally { Release(current); Release(type); Release(attributes); }
    }
    public void Seek(double seconds)
    {
        Guid format = Guid.Empty;
        PropVariant value = new() { Type = 20, Value = (long)(Math.Max(0, seconds) * 10_000_000) };
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(_reader, 8))(_reader, &format, &value));
    }
    public bool Read(Action<nint, int, long> consume)
        => ReadSample((sample, time) => CopySample(sample, (data, length) => consume(data, length, time)));

    private bool ReadSample(Action<nint, long> consume)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            uint actual = 0, flags = 0; long time = 0; nint sample = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(_reader, 9))
                (_reader, _stream, 0, &actual, &flags, &time, &sample));
            try
            {
                if ((flags & 1) != 0) throw new InvalidOperationException("Windows 媒体解码失败");
                // 首次 ReadSample 可能重新协商为带填充的 RGB32，不能沿用 SetCurrentMediaType 后的旧 stride。
                if (_stream == VideoStream && ((flags & 0x20) != 0 || (sample != 0 && !_loggedVideoLayout)))
                {
                    nint mediaType = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, _stream, &mediaType));
                    try
                    {
                        ulong size = Get64(mediaType, FrameSize);
                        int width = checked((int)(size >> 32)), height = checked((int)(size & uint.MaxValue));
                        Guid key = Subtype, subtype = default;
                        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(mediaType, 10))(mediaType, &key, &subtype));
                        if (subtype != Rgb32 || width < Info.Width || height < Info.Height)
                            throw new InvalidOperationException("Windows 解码输出格式或尺寸不兼容");
                        _stride = unchecked((int)Get32(mediaType, Stride, checked((uint)(width * 4))));
                        _bufferHeight = height;
                        AppLogger.Info($"Windows 解码协商：flags=0x{flags:X} 输出={width}x{height} stride={_stride} 可见={Info.Width}x{Info.Height}");
                    }
                    finally { Release(mediaType); }
                }
                if (sample != 0) { consume(sample, time); return true; }
                if ((flags & 2) != 0) return false;
            }
            finally { Release(sample); }
        }
        throw new InvalidOperationException("Windows 媒体解码没有返回有效样本");
    }
    public bool ReadVideo(byte[] destination, out double seconds)
    {
        double pts = 0;
        bool read = ReadSample((sample, time) =>
        {
            var layout = CopyVideoSample(sample, destination, Info.Width, Info.Height, _stride, _bufferHeight);
            if (!_loggedVideoLayout)
            {
                _loggedVideoLayout = true;
                AppLogger.Info($"Windows 预览帧：{Info.Width}x{Info.Height} 默认步长={_stride} 实际步长={layout.Pitch} 缓冲长度={layout.BufferLength} 读取={layout.Access}");
            }
            // RGB32 未定义的 alpha 不可交给 Premul 显示层。
            for (int i = 3; i < checked(Info.Width * Info.Height * 4); i += 4) destination[i] = 255;
            pts = time / 10_000_000.0;
        });
        seconds = pts;
        return read;
    }
    public void Dispose()
    {
        Release(_reader); _reader = 0;
        _context.Dispose();
    }
}
