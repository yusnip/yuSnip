using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>母带元信息：探测一次给全，供帧泵与音频播放器共用。</summary>
    public sealed class ClipInfo
    {
        public int Width;
        public int Height;
        public double Fps = 25;
        public double Duration;
        public bool HasAudio;
        public bool HasVideo => Width > 0 && Height > 0 && Fps > 0;
        public int FrameBytes => Width * Height * 4;
    }

    /// <summary>
    /// 用 <c>ffmpeg -i</c> 的 stderr 文本解析母带信息（工程里不带 ffprobe，只依赖 ffmpeg.exe 这一个文件）。
    /// 帧率取 "N fps" 那个字段；我们自己的录屏产物是标准 CFR（已用 showinfo 逐帧核对 pts 恒等），
    /// 所以供帧侧可以放心按「起表点 + 帧序号/帧率」合成时间戳，不必解析容器里的真实 pts。
    /// </summary>
    public static class ClipProbe
    {
        static readonly Regex ReVideo = new(@"Stream #\d+:\d+.*?: Video:.*?(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
        static readonly Regex ReFps = new(@"(\d+(?:\.\d+)?) fps", RegexOptions.Compiled);
        static readonly Regex ReDuration = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        public static ClipInfo? Probe(string ffmpegExe, string mediaPath)
        {
            if (!File.Exists(ffmpegExe) || !File.Exists(mediaPath)) return null;
            if (!FfmpegProcess.RunProbe(ffmpegExe, "-hide_banner -i \"" + mediaPath + "\"", out string stderr, 6000))
                return null;
            var m = ReVideo.Match(stderr);
            if (!m.Success) return null;
            var info = new ClipInfo
            {
                Width = int.Parse(m.Groups[1].Value),
                Height = int.Parse(m.Groups[2].Value),
                HasAudio = Regex.IsMatch(stderr, @"Stream #\d+:\d+.*?: Audio:"),
            };
            var f = ReFps.Match(stderr);
            if (f.Success && double.TryParse(f.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps)
                && fps > 0.5 && fps < 400) info.Fps = fps;
            var d = ReDuration.Match(stderr);
            if (d.Success)
                info.Duration = int.Parse(d.Groups[1].Value) * 3600
                    + int.Parse(d.Groups[2].Value) * 60
                    + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
            return info.Duration > 0.05 && info.HasVideo ? info : null;
        }
    }

    /// <summary>一帧可显示的画面：Buffer 由帧泵的环形池持有（调用方只读不释放），Pts 是它在母带上的时刻。</summary>
    public sealed record class ShowFrame(byte[] Buffer, double Pts) : IDisposable
    {
        // 系统解码器把缓冲租约交给显示线程；FFmpeg 的既有环形池不使用此回调。
        public Action? ReturnBuffer { get; init; }
        public void Dispose() => ReturnBuffer?.Invoke();
    }

    public interface IRecordingFramePump : IDisposable
    {
        void Start(double at);
        void Seek(double at);
        Task<ShowFrame?> ReadUpToAsync(double due, CancellationToken ct);
        double NextDue(double now);
    }

    /// <summary>
    /// 视频供帧泵：常驻 ffmpeg 子进程把母带解成裸帧（bgra）从 stdout 管道推过来，UI 侧自绘上屏。
    /// <b>恒按母带原始尺寸直出，一像素都不缩</b>（<c>-vf format=bgra</c>，没有 scale）——预览画质与母带等同，
    /// 需要变小交给显示层的 GPU 缩放，绝不在解码侧损像素。
    ///
    /// 三条硬约束（spike 实测得出，改动前先看 02_构建工具\spike\FfmpegPumpSpike\spike-result.txt）：
    /// ① seek＝换进程。输入侧 <c>-ss</c> 让 ffmpeg 自己解到目标点（实测 95~125ms 出首帧）；
    ///    输出侧 -ss 那条从 0 解起，越拖越慢，不许换成它。
    /// ② 背压＝管道＋有界 Channel。消费不动时 ffmpeg 写在管道上阻塞，解码自然停，
    ///    内存天花板＝环形槽数 × 帧字节，不会跑成整片。
    /// ③ 帧缓冲只在构造时分配一次，<b>禁止每次 seek 重 new</b>——大数组走 LOH 不会及时还给系统，
    ///    现象是"拖几下进度条内存涨一百兆"（spike 踩过，修后增长从 +117MB 降到 +37MB）。
    /// </summary>
    public sealed class FfmpegFramePump : IRecordingFramePump
    {
        readonly string _ffmpeg, _media;
        readonly ClipInfo _info;
        readonly Action<string>? _log;
        readonly int _slots, _frameBytes;
        readonly byte[][] _pool;
        readonly Channel<(byte[] Buf, double Pts, ulong Gen)> _ch;

        Process? _proc;
        CancellationTokenSource? _cts;
        long _writeCursor;          // 仅读线程访问
        ulong _gen;                 // 仅 UI 线程读写
        volatile bool _eof;
        ShowFrame? _held, _last;

        public double GenStart { get; private set; }
        /// <summary>本代已交付进通道的帧数（诊断用：分清卡在泵侧还是消费侧）。</summary>
        public long Produced => Volatile.Read(ref _writeCursor);
        public bool Eof => _eof;
        public ClipInfo Info => _info;
        /// <summary>ffmpeg 子进程当前占用：这条路线的真实内存成本是两进程相加，只报自己会低估一半。</summary>
        public long ChildWorkingSet
        {
            get { try { _proc?.Refresh(); return _proc?.WorkingSet64 ?? 0; } catch { return 0; } }
        }

        public FfmpegFramePump(string ffmpeg, string media, ClipInfo info, Action<string>? log = null)
        {
            _ffmpeg = ffmpeg; _media = media; _info = info; _log = log;
            _frameBytes = info.FrameBytes;
            // 环形池总量压在 ~32MB 内：1080p 全尺寸 4 格、小母带最多 8 格
            _slots = Math.Max(4, Math.Min(8, 32_000_000 / Math.Max(1, _frameBytes)));
            _pool = new byte[_slots][];
            for (int i = 0; i < _slots; i++) _pool[i] = new byte[_frameBytes];
            _ch = Channel.CreateBounded<(byte[], double, ulong)>(new BoundedChannelOptions(_slots)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        public void Start(double at) => Restart(at);
        public void Seek(double at) => Restart(at);

        void Restart(double at)
        {
            KillCurrent();
            _writeCursor = 0;
            _eof = false;
            _held = _last = null;
            _gen++;
            GenStart = at;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            string args = "-hide_banner -loglevel error -nostdin"
                + $" -ss {at.ToString("F3", CultureInfo.InvariantCulture)}"
                + $" -i \"{_media}\" -map 0:v:0 -vf format=bgra -f rawvideo -pix_fmt bgra -an -sn -";
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 供帧进程启动失败：" + _ffmpeg);
            try { FfmpegProcess.AttachToJob(_proc); } catch { }   // 我们崩了 ffmpeg 必须跟着没，不留孤儿进程
            var proc = _proc;
            var gen = _gen;
            _ = Task.Run(() => ReadLoopAsync(proc, ct, gen));
        }

        async Task ReadLoopAsync(Process proc, CancellationToken ct, ulong gen)
        {
            var errTask = Task.Run(async () =>
            {
                try { return await proc.StandardError.ReadToEndAsync(ct); } catch { return string.Empty; }
            }, ct);

            var stream = proc.StandardOutput.BaseStream;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int slot = (int)(_writeCursor % _slots);
                    byte[] buf = _pool[slot];
                    int off = 0;
                    while (off < _frameBytes)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(off, _frameBytes - off), ct);
                        if (n <= 0) break;
                        off += n;
                    }
                    if (off < _frameBytes) break;

                    double pts = GenStart + _writeCursor / _info.Fps;
                    _writeCursor++;
                    await _ch.Writer.WriteAsync((buf, pts, gen), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _log?.Invoke("[供帧] 读线程异常：" + ex.Message); }

            if (!ct.IsCancellationRequested)
            {
                string err = await errTask;
                if (!string.IsNullOrWhiteSpace(err)) _log?.Invoke("[供帧] stderr：" + err.Trim());
                _eof = true;
            }
            // ⚠ 这里绝不能 TryComplete()：通道是构造时建一次、跨整个播放期复用的，一旦 Complete
            // 就永久不可写——母带跑到尾部（ffmpeg 正常退出＝走到这条路径）之后，循环播放与"看完再拖回去"
            // 都会撞在 ChannelClosedException 上，表现是"放过一遍之后再也放不出来"。只有 Dispose 才收口。
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        /// <summary>
        /// 取 pts ≤ due 的最新一帧；没有新帧时返回上一帧（调用方按 Pts 相同自行去重）。
        /// ⚠ 「还没到点」的那一帧必须留住下次用（_held）：当场丢掉是错的——泵比墙钟快（实测 2.8×），
        /// 队列里最新帧的 pts 永远领先 due，于是每帧都被扔、每帧都"没有可显示的"（spike 抓到的第一个真 bug）。
        /// </summary>
        public async Task<ShowFrame?> ReadUpToAsync(double due, CancellationToken ct)
        {
            ShowFrame? best = null;
            if (_held != null)
            {
                if (_held.Pts <= due) { best = _held; _held = null; }
                else return _last;
            }
            while (!ct.IsCancellationRequested)
            {
                if (_ch.Reader.TryRead(out var item))
                {
                    if (item.Item3 != _gen) continue;   // 上一代换进程留下的帧，丢弃
                    var f = new ShowFrame(item.Item1, item.Item2);
                    if (f.Pts <= due) best = f;
                    else { _held = f; break; }
                    continue;
                }
                if (best != null || _eof) break;
                try { await Task.Delay(3, ct); } catch { break; }
            }
            _last = best ?? _last;
            return _last;
        }

        /// <summary>now 之后紧接着该出现的那一帧的时刻。母带 CFR，格点＝GenStart + k/Fps，算得出，不必问进程。</summary>
        public double NextDue(double now)
        {
            double g = GenStart;
            if (now < g) return g;
            return g + (Math.Floor((now - g) * _info.Fps) + 1) / _info.Fps;
        }

        void KillCurrent()
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
            try { _proc?.Kill(entireProcessTree: true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }

        public void Dispose()
        {
            KillCurrent();
            try { _ch.Writer.TryComplete(); } catch { }
        }
    }
}
