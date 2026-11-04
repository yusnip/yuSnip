namespace ScreenCaptureTool.Core.Recording;

public interface IRecordingSession : IDisposable
{
    event Action<TimeSpan>? ProgressChanged;
    event Action<bool>? PauseStateChanged;
    event Action<DegradeReason, string?>? Degraded;
    event Action<string, TimeSpan>? Completed;
    event Action<string>? Failed;
    bool IsRecording { get; }
    bool IsPaused { get; }
    RecordEngine Engine { get; }
    string MasterMp4Path { get; }
    (int W, int H) MasterSizePx { get; }
    bool Start(out string? error);
    void Pause();
    void Resume();
    void AutoPause();
    void Stop();
    void Abort();
    TimeSpan Elapsed();
}

public static class RecordingSessionFactory
{
    public static IRecordingSession Create(RecordingOptions options) => new SelectedRecordingSession(options);

    private sealed class SelectedRecordingSession(RecordingOptions options) : IRecordingSession
    {
        private IRecordingSession? _inner;
        public event Action<TimeSpan>? ProgressChanged;
        public event Action<bool>? PauseStateChanged;
        public event Action<DegradeReason, string?>? Degraded;
        public event Action<string, TimeSpan>? Completed;
        public event Action<string>? Failed;
        public bool IsRecording => _inner?.IsRecording == true;
        public bool IsPaused => _inner?.IsPaused == true;
        public RecordEngine Engine => _inner?.Engine ?? options.Engine;
        public string MasterMp4Path => _inner?.MasterMp4Path ?? "";
        public (int W, int H) MasterSizePx => _inner?.MasterSizePx ?? (0, 0);
        private void Bind(IRecordingSession session)
        {
            _inner = session;
            session.ProgressChanged += value => ProgressChanged?.Invoke(value);
            session.PauseStateChanged += value => PauseStateChanged?.Invoke(value);
            session.Degraded += (reason, text) => Degraded?.Invoke(reason, text);
            session.Completed += (path, duration) => Completed?.Invoke(path, duration);
            session.Failed += text => Failed?.Invoke(text);
        }
        public bool Start(out string? error)
        {
            if (options.Engine == RecordEngine.Ffmpeg)
            {
                Bind(new RecordingSession(options));
                return _inner!.Start(out error);
            }
            Bind(new WindowsRecordingSession(options));
            if (_inner!.Start(out error)) return true;
            string nativeError = error ?? "Windows 录制不可用";
            _inner.Dispose(); _inner = null;
            // 只在自动档、录制尚未开始时回退已安装的 FFmpeg；不在中途换引擎或偷偷下载。
            if (options.Engine != RecordEngine.Auto || FfmpegLocator.TryFind(options.ManualFfmpegPath) == null)
            {
                error = nativeError + (options.Engine == RecordEngine.Auto ? "\n可在设置中选择 FFmpeg 并安装运行组件。" : "");
                return false;
            }
            Bind(new RecordingSession(options));
            bool started = _inner!.Start(out error);
            if (started) Degraded?.Invoke(DegradeReason.EngineFallback, "Windows 原生引擎启动失败，已使用 FFmpeg：" + nativeError);
            return started;
        }
        public void Pause() => _inner?.Pause();
        public void Resume() => _inner?.Resume();
        public void AutoPause() => _inner?.AutoPause();
        public void Stop() => _inner?.Stop();
        public void Abort() => _inner?.Abort();
        public TimeSpan Elapsed() => _inner?.Elapsed() ?? TimeSpan.Zero;
        public void Dispose() { _inner?.Dispose(); _inner = null; }
    }
}
