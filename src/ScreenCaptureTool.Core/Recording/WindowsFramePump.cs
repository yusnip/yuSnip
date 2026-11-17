using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ScreenCaptureTool.Core.Recording;

/// <summary>系统解码供帧；四格固定缓冲按租约交接，显示完成前不会被下一帧覆盖。</summary>
public sealed class WindowsFramePump : IRecordingFramePump
{
    private readonly string _path;
    private readonly ClipInfo _info;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<byte[]> _buffers = new();
    private readonly Channel<(ShowFrame Frame, long Gen)> _frames = Channel.CreateBounded<(ShowFrame, long)>(3);
    private readonly CancellationTokenSource _cancel = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private long _generation;
    private double _seek;
    private bool _started, _disposed;
    private volatile bool _eof;
    private (ShowFrame Frame, long Gen)? _held;
    private string? _error;
    public WindowsFramePump(string path, ClipInfo info, Action<string>? log = null)
    {
        _path = path; _info = info; _log = log;
        for (int i = 0; i < 4; i++) _buffers.Enqueue(new byte[checked(info.FrameBytes)]);
        _thread = new Thread(Decode) { IsBackground = true, Name = "yuSnip-WindowsVideoDecode" };
    }
    public void Start(double at) => Seek(at);
    public void Seek(double at)
    {
        lock (_gate)
        {
            _seek = at; _generation++; _eof = false;
            if (!_started) { _started = true; _thread.Start(); }
        }
        _wake.Set();
    }
    private void Decode()
    {
        try
        {
            using var reader = new WindowsMediaReader(_path);
            long current = -1;
            double start = 0;
            while (!_cancel.IsCancellationRequested)
            {
                long gen;
                lock (_gate)
                {
                    gen = _generation;
                    if (gen != current) { start = _seek; reader.Seek(start); current = gen; _eof = false; }
                }
                if (_eof || !_buffers.TryDequeue(out var buffer)) { _wake.WaitOne(5); continue; }
                bool transferred = false;
                try
                {
                    if (!reader.ReadVideo(buffer, out double pts))
                    {
                        if (gen == Interlocked.Read(ref _generation)) _eof = true;
                        continue;
                    }
                    if (pts + .001 < start) continue;
                    var frame = new ShowFrame(buffer, pts) { ReturnBuffer = () => { _buffers.Enqueue(buffer); } };
                    _frames.Writer.WriteAsync((frame, gen), _cancel.Token).AsTask().GetAwaiter().GetResult();
                    transferred = true;
                }
                finally { if (!transferred) _buffers.Enqueue(buffer); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; _log?.Invoke("Windows 视频解码失败：" + ex.Message); }
        finally { _eof = true; _frames.Writer.TryComplete(); }
    }
    public async Task<ShowFrame?> ReadUpToAsync(double due, CancellationToken ct)
    {
        ShowFrame? best = null;
        try
        {
            while (!ct.IsCancellationRequested && !_cancel.IsCancellationRequested)
            {
                (ShowFrame Frame, long Gen) item;
                if (_held is { } held) { item = held; _held = null; }
                else if (!_frames.Reader.TryRead(out item))
                {
                    if (best != null || _eof) break;
                    await Task.Delay(3, ct); continue;
                }
                if (item.Gen != Interlocked.Read(ref _generation)) { item.Frame.Dispose(); continue; }
                if (item.Frame.Pts > due && best == null) { _held = item; break; }
                if (item.Frame.Pts > due) { _held = item; break; }
                best?.Dispose(); best = item.Frame;
            }
            if (_error != null) throw new InvalidOperationException(_error);
            var result = best; best = null; return result;
        }
        finally { best?.Dispose(); }
    }
    public double NextDue(double now) => (Math.Floor(now * _info.Fps) + 1) / _info.Fps;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _cancel.Cancel(); _wake.Set();
        if (_started) _thread.Join();
        while (_frames.Reader.TryRead(out var item)) item.Frame.Dispose();
        _held?.Frame.Dispose(); _held = null;
        _wake.Dispose(); _cancel.Dispose();
    }
}
