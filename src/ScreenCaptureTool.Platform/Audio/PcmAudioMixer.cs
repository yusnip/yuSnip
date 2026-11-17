namespace ScreenCaptureTool.Platform.Audio;

/// <summary>由媒体工作线程按共同时间轴拉取 PCM；复用现有 WASAPI 重采样，不经过命名管道。</summary>
public sealed class PcmAudioMixer
{
    private readonly RingResampler _system, _microphone;
    private readonly bool _useSystem, _useMicrophone;
    public PcmAudioMixer(WasapiDualCapture capture, bool useSystem, bool useMicrophone, float micVolume)
    {
        _useSystem = useSystem; _useMicrophone = useMicrophone;
        _system = new RingResampler(() => capture.SystemRing, () => capture.SystemChannels, () => capture.SystemRate, 1);
        _microphone = new RingResampler(() => capture.MicRing, () => capture.MicChannels, () => capture.MicRate, Math.Clamp(micVolume, 0, 2));
    }
    public void Read(int frames, float[] system, float[] microphone)
    {
        Array.Clear(system); Array.Clear(microphone);
        if (_useSystem) _system.Next(frames, system);
        if (_useMicrophone) _microphone.Next(frames, microphone);
    }
    public void Reset() { _system.Reset(); _microphone.Reset(); }
    public static void ToPcm16(ReadOnlySpan<float> system, ReadOnlySpan<float> microphone, Span<byte> destination)
    {
        for (int i = 0; i < system.Length; i++)
        {
            float value = system[i] + (microphone.IsEmpty ? 0 : microphone[i]);
            if (!float.IsFinite(value)) value = 0;
            short sample = (short)Math.Clamp((int)MathF.Round(Math.Clamp(value, -1, 1) * 32767), -32768, 32767);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), sample);
        }
    }
}
