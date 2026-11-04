using System.Diagnostics;
using ScreenCaptureTool.Platform.Audio;

namespace ScreenCaptureTool.Core.Recording;

/// <summary>单工作线程完成采集、混音、编码与收尾。音视频共同使用排除暂停的单调时间轴。</summary>
public sealed class WindowsRecordingSession(RecordingOptions options) : IRecordingSession
{
    private readonly QualityProfile _profile = QualityProfiles.Get(options.EffectivePresetId);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _recording, _paused, _wantPause, _stop, _abort;
    private string? _startError;
    private string _directory = "";
    private long _elapsed100ns;
    private bool _disposed;
    public event Action<TimeSpan>? ProgressChanged;
    public event Action<bool>? PauseStateChanged;
    public event Action<DegradeReason, string?>? Degraded;
    public event Action<string, TimeSpan>? Completed;
    public event Action<string>? Failed;
    public bool IsRecording => _recording;
    public bool IsPaused => _paused;
    public RecordEngine Engine => RecordEngine.Windows;
    public string MasterMp4Path { get; private set; } = "";
    public (int W, int H) MasterSizePx { get; private set; }
    public bool Start(out string? error)
    {
        if (_worker != null) { error = "录制已启动"; return false; }
        if (options.Region.Width < 2 || options.Region.Height < 2) { error = "录制区域太小"; return false; }
        _directory = TempSpace.CreateSessionDir();
        MasterMp4Path = Path.Combine(_directory, "Rec_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4");
        MasterSizePx = QualityProfiles.ScaledDims(_profile, options.Region.Width, options.Region.Height);
        _worker = new Thread(Run) { IsBackground = true, Name = "yuSnip-WindowsRecording" };
        _worker.Start();
        _ready.Wait();
        error = _startError;
        return error == null;
    }
    private void Run()
    {
        bool initialized = false;
        WindowsMp4Writer? writer = null;
        try
        {
            using var capture = new NativeRecordingCapture(options.Region, MasterSizePx.W, MasterSizePx.H);
            using var audio = new WasapiDualCapture();
            bool wantSystem = options.Audio is AudioLayout.SystemOnly or AudioLayout.Mixed or AudioLayout.Split;
            bool wantMic = options.Audio is AudioLayout.MicOnly or AudioLayout.Mixed or AudioLayout.Split;
            bool system = wantSystem && audio.StartStream(WasapiStreamKind.SystemLoopback);
            if (wantSystem && !system) Degraded?.Invoke(DegradeReason.SystemAudioLost, "系统声音不可用：" + audio.LastStartError);
            bool mic = wantMic && audio.StartStream(WasapiStreamKind.Microphone);
            if (wantMic && !mic) Degraded?.Invoke(DegradeReason.MicLost, "麦克风不可用：" + audio.LastStartError);
            if ((wantSystem || wantMic) && !system && !mic) throw new InvalidOperationException("所选录音设备均不可用，请检查设备或关闭录音后重试");
            audio.DeviceLost += kind => Degraded?.Invoke(kind == WasapiStreamKind.SystemLoopback ? DegradeReason.SystemAudioLost : DegradeReason.MicLost,
                "录音设备已断开，该路补静音；其他音轨与画面继续录制");
            int tracks = options.Audio == AudioLayout.None ? 0 : options.Audio == AudioLayout.Split ? 2 : 1;
            try { writer = new WindowsMp4Writer(MasterMp4Path, MasterSizePx.W, MasterSizePx.H, _profile.Fps, _profile.ApproxVideoBitsPerSec, tracks); }
            catch (Exception ex)
            {
                AppLogger.Warn("Windows 录制：硬件转换初始化失败，尝试系统软件编码：" + ex.Message);
                writer = new WindowsMp4Writer(MasterMp4Path, MasterSizePx.W, MasterSizePx.H, _profile.Fps, _profile.ApproxVideoBitsPerSec, tracks, false);
            }
            var mixer = new PcmAudioMixer(audio, system, mic, options.MicVolume01);
            float[] sys = new float[960], microphone = new float[960];
            byte[] pcm = new byte[1920], pcmMic = new byte[1920];
            audio.SuspendAll(); audio.ResumeAll();
            long audioFrames = 0, nextVideo = 1, lastProgress = -1, lastVideoEnd = 10_000_000 / _profile.Fps;
            // 首次真实采集也属于启动校验；失败时自动档仍可安全回退，不交付一条假启动会话。
            writer.WriteVideo(capture.Capture(options.DrawCursor), 0, lastVideoEnd);
            Stopwatch clock = Stopwatch.StartNew();
            _recording = initialized = true;
            AppLogger.Info($"Windows 录制：GDI DIB + Media Foundation，{MasterSizePx.W}x{MasterSizePx.H}@{_profile.Fps}，AAC 音轨={tracks} 系统={system} 麦克风={mic}");
            _ready.Set();
            while (!_stop)
            {
                if (_wantPause != _paused)
                {
                    if (_wantPause) { clock.Stop(); audio.SuspendAll(); mixer.Reset(); }
                    else { audio.ResumeAll(); mixer.Reset(); clock.Start(); }
                    _paused = _wantPause;
                    PauseStateChanged?.Invoke(_paused);
                }
                long now = clock.Elapsed.Ticks;
                Interlocked.Exchange(ref _elapsed100ns, now);
                if (!_paused)
                {
                    // 落后时直接抓当前画面，不补录已经错过的桌面；PTS 仍保持真实时间。
                    if (now >= nextVideo * 10_000_000 / _profile.Fps)
                    {
                        long frame = Math.Max(nextVideo, now * _profile.Fps / 10_000_000);
                        long start = frame * 10_000_000 / _profile.Fps;
                        long end = (frame + 1) * 10_000_000 / _profile.Fps;
                        writer.WriteVideo(capture.Capture(options.DrawCursor), start, end - start);
                        nextVideo = frame + 1; lastVideoEnd = end;
                    }
                    WriteAudioUntil(now * 48000 / 10_000_000, false);
                }
                if (now / 2_500_000 != lastProgress)
                {
                    lastProgress = now / 2_500_000;
                    ProgressChanged?.Invoke(TimeSpan.FromTicks(now));
                    var drive = new DriveInfo(Path.GetPathRoot(_directory)!);
                    if (drive.AvailableFreeSpace < (_profile.ApproxVideoBitsPerSec / 8 + 40000) * 15)
                    {
                        Degraded?.Invoke(DegradeReason.DiskLow, "磁盘空间不足，已自动收尾");
                        _stop = true;
                    }
                }
                _wake.WaitOne(2);
            }
            clock.Stop();
            if (_abort) return;
            // 音轨补到最后一张画面的末点，避免末尾只有画面没有声音；不足一拍按实际帧数写。
            WriteAudioUntil(lastVideoEnd * 48000 / 10_000_000, true);
            writer.Finish();
            Interlocked.Exchange(ref _elapsed100ns, lastVideoEnd);
            _recording = _paused = false;
            Completed?.Invoke(MasterMp4Path, TimeSpan.FromTicks(lastVideoEnd));

            void WriteAudioUntil(long target, bool flush)
            {
                if (tracks == 0) return;
                while (target - audioFrames >= 480 || (flush && target > audioFrames))
                {
                    int frames = (int)Math.Min(480, target - audioFrames);
                    mixer.Read(frames, sys, microphone);
                    if (tracks == 2)
                    {
                        PcmAudioMixer.ToPcm16(sys.AsSpan(0, frames * 2), default, pcm);
                        PcmAudioMixer.ToPcm16(microphone.AsSpan(0, frames * 2), default, pcmMic);
                        writer.WriteAudio(0, pcm.AsSpan(0, frames * 4), audioFrames);
                        writer.WriteAudio(1, pcmMic.AsSpan(0, frames * 4), audioFrames);
                    }
                    else
                    {
                        PcmAudioMixer.ToPcm16(sys.AsSpan(0, frames * 2), microphone.AsSpan(0, frames * 2), pcm);
                        writer.WriteAudio(0, pcm.AsSpan(0, frames * 4), audioFrames);
                    }
                    audioFrames += frames;
                }
            }
        }
        catch (Exception ex)
        {
            if (!initialized) _startError = "Windows 原生录制启动失败：" + ex.Message;
            else if (!_abort) Failed?.Invoke("Windows 原生录制失败：" + ex.Message);
            AppLogger.Error("Windows 录制失败", ex);
        }
        finally
        {
            writer?.Dispose();
            _recording = _paused = false;
            _ready.Set();
            if (_abort || !initialized) TempSpace.DeleteNow(_directory);
        }
    }
    public TimeSpan Elapsed() => TimeSpan.FromTicks(Interlocked.Read(ref _elapsed100ns));
    public void Pause() { if (_recording) { _wantPause = true; _wake.Set(); } }
    public void Resume() { if (_recording) { _wantPause = false; _wake.Set(); } }
    public void AutoPause()
    {
        if (_recording && !_wantPause) { Degraded?.Invoke(DegradeReason.ScreenLocked, "检测到锁屏，已自动暂停"); Pause(); }
    }
    public void Stop() { _stop = true; _wake.Set(); if (Thread.CurrentThread != _worker) _worker?.Join(); }
    public void Abort() { _abort = true; Stop(); if (_directory.Length > 0) TempSpace.DeleteNow(_directory); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_recording) Abort();
        if (Thread.CurrentThread != _worker) _worker?.Join();
        _ready.Dispose(); _wake.Dispose();
    }
}
