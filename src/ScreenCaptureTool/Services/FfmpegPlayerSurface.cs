using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Recording;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 预览播放引擎统一接口：贴图窗只认这一层，不碰解码与出声的细节。
/// 与旧版的差别是引擎不再需要借子 HWND 自己画——帧交给我们自绘，所以 <see cref="InitAsync"/>
/// 只吃媒体路径，画面通过 <see cref="Output"/> 挂到窗上的 <c>Image</c>。
/// </summary>
public interface IPlayerSurface : IDisposable
{
    /// <summary>起引擎（定位 ffmpeg → 探测母带 → 起供帧与出声）。返回 false＝起不来，原因看 <see cref="LastError"/>。</summary>
    Task<bool> InitAsync(string mediaPath, bool loop);

    /// <summary>InitAsync 返回 false 时的原因，供界面如实显示。</summary>
    string? LastError { get; }

    /// <summary>要显示的画面：母带原始尺寸的一张位图，挂给 <c>Image.Source</c>，尺寸不变所以整窗只换内容。</summary>
    WriteableBitmap? Output { get; }

    void Play();
    void Pause();
    void Toggle();
    bool IsPlaying { get; }
    long GetTimeMs();
    long GetLengthMs();

    /// <summary>跳转到毫秒处（内部＝换 ffmpeg 进程 + 按住时钟等首帧）。</summary>
    void SeekTo(long ms);

    /// <summary>
    /// 每往帧缓冲里贴成一帧，就在 <b>UI 线程</b>回调一次。
    /// ⚠ 宿主必须在这一枪里重画自己：帧缓冲是被就地改写的，Avalonia 的 <c>Image</c> 只在 Source
    /// 换实例时才重传纹理，所以画面会冻在第一次上传的那张全零底图上——现象是"时钟在走、帧在贴、
    /// 屏上一片黑"。长截图预览窗用的是同一套路（<c>PreviewImage.InvalidateVisual()</c>）。
    /// </summary>
    event Action? FramePresented;
}

/// <summary>
/// 自研预览引擎：ffmpeg 子进程供帧（母带原始尺寸，一像素不缩）＋ Avalonia 自绘上屏 ＋ winmm waveOut 出声。
///
/// 排片这一层是"卡不卡"的全部来源，六条铁律（①② 是 spike 实测出来的，④⑤⑥ 是自研引擎接上真窗以后
/// 自检台抓出来的，改动前先看 02_构建工具\spike\FfmpegPumpSpike\spike-result.txt）：
/// ① <b>独立线程睡到点再醒</b>，不用 DispatcherTimer 决定什么时候贴帧——它的间隔被量化到 15.6ms 栅格，
///    41.7ms 一格的母带会落成 31/47ms 交替，实测 p99 59.4ms；换独立线程后 p99 48.0ms。
/// ② <b>时钟不空转</b>：换进程要 95~125ms，墙钟不等人，于是首帧到手前画面冻着、时钟已经跑过去，
///    首帧一到就"停一拍再窜一下"（用户原话"慢半拍"）。改法＝跳转后把时钟与音频一起按住，
///    等这一帧真到手，再从它的时刻起表（re-anchor）。实测 ≤20ms 的追帧事件从 6 次降到 2 次。
/// ③ 帧池与位图<b>只在 Init 时建一次</b>，seek 绝不重分配（见 <see cref="FfmpegFramePump"/> 注释③）。
/// ④ <b>跳转前发出的那一帧必须作废</b>：上屏是 Post 回 UI 线程的，跳转之后它完全可能才跑到。旧帧一旦抢走
///    re-anchor，就把刚钉好的时钟拽回跳转前的位置（自检实测：跳到 23.4s 后读数掉回 7.2s，音频跟着错位）。
///    号牌见 <c>_seekToken</c>，取帧前抄、上屏时对。
/// ⑤ <b>暂停是"停表"，不是"重钉锚点"</b>：位置＝锚点＋墙钟差，重钉完它照样以 1× 往前爬——现象是画面冻住
///    而读数自己走。闸门在 <c>ClockPos()</c> 上看 <c>_playing</c>。
/// ⑥ <b>等下一帧那一觉必须盯住号牌随时醒</b>：due 是拿跳转前的快照算的，跳到 15.2s 时这一觉会一路睡到
///    9.6 秒之后（自检实测：拖完进度条画面死住 3 秒没换帧）。见 <c>WaitPrecise</c>。
/// </summary>
internal sealed class FfmpegPlayerSurface : IPlayerSurface
{
    readonly bool _useSystemDecoder;
    public FfmpegPlayerSurface(bool useSystemDecoder = false) => _useSystemDecoder = useSystemDecoder;
    ClipInfo? _info;
    IRecordingFramePump? _pump;
    WaveOutAudioPlayer? _audio;
    WriteableBitmap? _wb;
    bool _loop;

    // 播放时钟：位置 = _posAtStart + (墙钟 - _stampAtStart)。排片线程靠一份不可变快照读它，
    // 所以整体一次性换（引用类型才能 volatile），不会看到半新半旧。
    double _posAtStart, _videoPos, _duration;
    long _stampAtStart;
    volatile PaceState _state = new(0, 0);
    sealed record class PaceState(double Pos, long Stamp);

    bool _playing, _closed, _reanchor, _eofPending, _paceStarted;
    double _lastPts = -1;
    CancellationTokenSource _paceCts = new();
    // 每次跳转发一张号牌。排片线程取帧前抄下当时的号牌、随 Post 一起交回 UI 线程，号牌对不上＝跳转前
    // 发出的旧帧，整张作废。不作废会怎样：旧帧抢先把 _reanchor 用掉，把刚钉好的时钟拽回跳转前的位置
    // （自检实测跳到 23.4s 后读数掉回 7.2s，差 -16.2s，且音频跟着一起错位）。
    long _seekToken;
    long _framesShown;
    Stopwatch? _seekWatch;

    public string? LastError { get; private set; }
    public WriteableBitmap? Output => _wb;
    public bool IsPlaying => _playing;
    /// <summary>真正写进位图的次数：判"上屏快不快"只能数这个数——画面内容本来就可能是静止的，数像素变化会误判。</summary>
    public long FramesShown => Interlocked.Read(ref _framesShown);
    public event Action? FramePresented;

    public async Task<bool> InitAsync(string mediaPath, bool loop)
    {
        _loop = loop;
        string? ffmpeg = null;
        ClipInfo? info = null;
        try
        {
            // 定位与探测都要起子进程，绝不上 UI 线程
            (ffmpeg, info) = await Task.Run(() =>
            {
                if (_useSystemDecoder)
                {
                    using var reader = new WindowsMediaReader(mediaPath);
                    return ((string?)"Windows", reader.Info);
                }
                string? exe = FfmpegLocator.TryFind(AppSettings.Current.RecFfmpegPath);
                return (exe, exe == null ? null : ClipProbe.Probe(exe, mediaPath));
            });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLogger.Warn("预览引擎: 探测异常 " + ex.Message);
            return false;
        }
        if (_closed) return false; // 探测期间关窗时不再创建位图、解码线程或音频设备。
        if (ffmpeg == null) { LastError = "找不到 ffmpeg.exe"; return false; }
        if (info == null) { LastError = "这条录屏文件读不出画面信息"; return false; }

        _info = info;
        _duration = info.Duration;
        try
        {
            _wb = new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        }
        catch (Exception ex)
        {
            LastError = "位图创建失败：" + ex.Message;
            return false;
        }

        try
        {
            _pump = _useSystemDecoder ? new WindowsFramePump(mediaPath, info, Log) : new FfmpegFramePump(ffmpeg, mediaPath, info, Log);
            _pump.Start(0);
        }
        catch (Exception ex)
        {
            LastError = "供帧进程没起来：" + ex.Message;
            DisposeCore();
            return false;
        }

        if (info.HasAudio)
        {
            var audio = new WaveOutAudioPlayer(ffmpeg, mediaPath, Log, _useSystemDecoder);
            if (audio.Open()) _audio = audio;
            else { AppLogger.Warn("预览引擎: 出声设备打不开，本轮无声——" + audio.LastError); audio.Dispose(); }
        }

        // 开播同样有那一百毫秒的空窗，与 seek 走同一条按住-起表规则
        _reanchor = true;
        _seekWatch = Stopwatch.StartNew();
        _stampAtStart = Stopwatch.GetTimestamp();
        _state = new PaceState(0, _stampAtStart);
        AppLogger.Info($"预览引擎: {info.Width}×{info.Height}@{info.Fps:F2}fps {info.Duration:F2}s " +
                       $"音频={(info.HasAudio ? (_audio != null ? "开" : "设备不可用") : "无母带音轨")} 循环={(loop ? "开" : "关")}");
        return true;
    }

    static void Log(string s) => AppLogger.Info("预览引擎: " + s);

    public void Play()
    {
        if (_playing || _closed || _pump == null) return;
        // 非循环档放过一遍后停在尾帧，此时再按播放＝从头重放，而不是原地不动
        if (!_loop && _duration > 0 && _videoPos >= _duration - 0.05) { SeekTo(0); return; }
        _playing = true;
        _stampAtStart = Stopwatch.GetTimestamp();
        _posAtStart = _videoPos;
        _state = new PaceState(_videoPos, _stampAtStart);
        // 刚跳转还没首帧时不在这里放行音频，交给 Present 的 re-anchor 一起放行
        if (!_reanchor) _audio?.Start();
        // 排片线程只起一次：暂停期间它自己空转（等 20ms 再看 _playing），
        // 每次 Play 都新起一条会出现两路同时取帧。
        if (!_paceStarted)
        {
            _paceStarted = true;
            _paceCts = new CancellationTokenSource();
            _ = Task.Run(() => PaceLoopAsync(_paceCts.Token));
        }
    }

    public void Pause()
    {
        if (!_playing) return;
        double at = ClockPos();
        _playing = false;
        // 停表：把锚点重钉在"此刻"，否则 ClockPos 会带着暂停前的锚点继续往前走，读数停在暂停后又自己爬
        _posAtStart = _videoPos = at;
        _stampAtStart = Stopwatch.GetTimestamp();
        _state = new PaceState(at, _stampAtStart);
        _audio?.Pause();
    }

    public void Toggle()
    {
        if (_playing) Pause(); else Play();
    }

    public void SeekTo(long ms)
    {
        if (_closed || _pump == null || _info == null) return;
        double to = Math.Clamp(ms / 1000.0, 0, Math.Max(0, _duration - 0.05));
        try
        {
            _pump.Seek(to);
            Interlocked.Increment(ref _seekToken);
            _seekWatch = Stopwatch.StartNew();
            _audio?.Pause();        // 按住音频，等首帧到手再重新起表放行
            _reanchor = true;
            _lastPts = -1;
            _eofPending = false;
            _posAtStart = to;
            _videoPos = to;
            _stampAtStart = Stopwatch.GetTimestamp();
            _state = new PaceState(to, _stampAtStart);
            if (!_playing) Play();
        }
        catch (Exception ex) { AppLogger.Warn("预览引擎: 跳转失败 " + ex.Message); }
    }

    public long GetTimeMs() => (long)(Math.Clamp(ClockPos(), 0, _duration) * 1000);

    public long GetLengthMs() => (long)(_duration * 1000);

    // ⚠ 停表必须靠 _playing 这一道闸：Pause 里"把锚点重钉在此刻"只是让读数不跳，
    // 锚点自身仍按墙钟往前爬——实测暂停后读数照以 1× 走满整段观察窗（自检报的 703/712/715ms 就是它）。
    double ClockPos() => _playing
        ? _posAtStart + (Stopwatch.GetTimestamp() - _stampAtStart) / (double)Stopwatch.Frequency
        : _videoPos;

    /// <summary>排片线程：睡到这一帧该出现的时刻再醒取帧、贴上屏。不靠定时器的 15.6ms 栅格（见类注释①）。</summary>
    async Task PaceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_closed)
        {
            try
            {
                var pump = _pump;
                if (!_playing || pump == null || _info == null) { ct.WaitHandle.WaitOne(20); continue; }

                var st = _state;
                long token = Interlocked.Read(ref _seekToken);
                double now = st.Pos + (Stopwatch.GetTimestamp() - st.Stamp) / (double)Stopwatch.Frequency;
                if (now >= _duration && !_eofPending)
                {
                    // 收尾交回 UI 线程：与 Play/Pause/SeekTo 同一条线程改状态，不打架
                    _eofPending = true;
                    Dispatcher.UIThread.Post(HandleEof);
                }

                var frame = await pump.ReadUpToAsync(now, ct);
                if (frame != null)
                {
                    var f = frame;
                    Dispatcher.UIThread.Post(() => Present(f, token));
                }
                WaitPrecise(pump.NextDue(now), st, token, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLogger.Warn("预览引擎: 排片线程异常 " + ex.Message);
                Thread.Sleep(30);
            }
        }
    }

    void HandleEof()
    {
        _eofPending = false;
        if (_closed || _pump == null) return;
        if (_loop) { SeekTo(0); return; }
        _playing = false;
        _videoPos = _duration;
        _audio?.Pause();
    }

    /// <summary>睡到 due 对应的墙钟时刻：粗的一段用等待，最后 3ms 自旋（系统定时器粒度 15.6ms，全靠 Sleep 必然过头）。
    /// ⚠ 必须盯住号牌随时醒：due 与 target 都是拿<b>跳转前</b>那份时钟快照算的，跳转后 <c>NextDue</c> 已经换成
    /// 新落点的时刻，而 target 仍是"旧锚点＋到新落点的距离"——跳到 15.2s 时这一觉会一路睡到 9.6 秒之后，
    /// 现象是拖完进度条画面死住不动，醒来还把播放头起表到十秒开外（自检实测首帧 3s 没到手）。</summary>
    void WaitPrecise(double due, PaceState st, long token, CancellationToken ct)
    {
        long target = st.Stamp + (long)((due - st.Pos) * Stopwatch.Frequency);
        long remain;
        while ((remain = target - Stopwatch.GetTimestamp()) > 0 && !ct.IsCancellationRequested)
        {
            if (token != Interlocked.Read(ref _seekToken)) return;   // 已跳转：这一觉睡错点了，立刻醒
            double ms = remain * 1000.0 / Stopwatch.Frequency;
            if (ms > 3) ct.WaitHandle.WaitOne(Math.Max(1, (int)ms - 2));
            else while (Stopwatch.GetTimestamp() < target) { Thread.SpinWait(64); ct.ThrowIfCancellationRequested(); }
        }
    }

    /// <summary>UI 线程：把一帧写进位图。跳转后的第一帧在这里重新起表（见类注释②）。</summary>
    void Present(ShowFrame frame, long token)
    {
        try { PresentCore(frame, token); }
        finally { frame.Dispose(); }
    }

    void PresentCore(ShowFrame frame, long token)
    {
        if (_closed || _info == null) return;
        if (token != Interlocked.Read(ref _seekToken)) return;   // 跳转前发出的旧帧，作废
        if (frame.Pts == _lastPts) return;

        // 只在真播放时起表：暂停中迟到的这一帧该改画面，但不许把停住的读数往前挪
        if (_reanchor && _playing)
        {
            _reanchor = false;
            long anchor = Stopwatch.GetTimestamp();
            _posAtStart = frame.Pts;
            _videoPos = frame.Pts;
            _stampAtStart = anchor;
            _state = new PaceState(frame.Pts, anchor);
            var w = _seekWatch;
            if (w != null)
            {
                _seekWatch = null;
                AppLogger.Info($"预览引擎: 首帧 {frame.Pts:F2}s 到手，换进程到起表 {w.ElapsedMilliseconds}ms");
            }
            try
            {
                _audio?.Seek(frame.Pts);
                if (_playing) _audio?.Start();
            }
            catch (Exception ex) { AppLogger.Warn("预览引擎: 音频对齐失败 " + ex.Message); }
        }
        _lastPts = frame.Pts;
        Blit(frame.Buffer);
        Interlocked.Increment(ref _framesShown);
        FramePresented?.Invoke();
    }

    void Blit(byte[] src)
    {
        var wb = _wb;
        if (wb == null || _info == null) return;
        try
        {
            using var fb = wb.Lock();
            int rowBytes = _info.Width * 4;
            if (fb.RowBytes == rowBytes)
                Marshal.Copy(src, 0, fb.Address, Math.Min(src.Length, rowBytes * _info.Height));
            else
                // 极少数平台上行有对齐填充，逐行搬，不然整幅会斜切
                for (int y = 0; y < _info.Height; y++)
                    Marshal.Copy(src, y * rowBytes, IntPtr.Add(fb.Address, y * fb.RowBytes), rowBytes);
        }
        catch (Exception ex) { AppLogger.Warn("预览引擎: 上屏失败 " + ex.Message); }
    }

    public void Dispose() => DisposeCore();

    void DisposeCore()
    {
        _closed = true;
        _playing = false;
        try { _paceCts.Cancel(); } catch { }
        _paceCts.Dispose();
        try { _pump?.Dispose(); } catch { }
        _pump = null;
        try { _audio?.Dispose(); } catch { }
        _audio = null;
        try { _wb?.Dispose(); } catch { }
        _wb = null;
    }
}
