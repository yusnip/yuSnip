using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// 预览窗出声：winmm <c>waveOut</c> ＋ 常驻 ffmpeg 解码 PCM 管道。
    ///
    /// 为什么不用 WASAPI：本机（装了第三方音频组件）实测「默认播放端点 + SHARED + Initialize/Start 都 S_OK」
    /// 之后，GetService 仍对 IAudioRenderClient / IAudioClock / IAudioSessionControl 全回 E_NOINTERFACE。
    /// 录屏采集那一路是好的，所以工程里那份 WASAPI 槽位表**不许**按这个现象去改；播放则退回 waveOut——
    /// 它不碰 COM、走系统混音器（自动重采样到设备格式），Win7 起人人都有。
    ///
    /// 三个必须留在原地的实测坑：
    /// ① <c>waveOutOpen</c> 传 <c>CALLBACK_EVENT</c> 会让进程当场 0xc0000409(FAST_FAIL_INVALID_SP) 消失，
    ///    连托管异常都没有（<c>waveOutGetNumDevs</c> 正常、改 CALLBACK_NULL 同参数完全无恙）⇒ 只用轮询补格。
    /// ② <c>WAVEHDR.dwUser/reserved</c> 原生是 DWORD_PTR，写成 uint 会让 dwFlags 从 24 挪到 20，
    ///    驱动按 24 记账、我们按 20 读 ⇒ 标志位全是垃圾，PrepareHeader 当场快速失败。
    /// ③ <c>uDeviceID</c> 原生是 UINT_PTR，WAVE_MAPPER ＝ ((UINT_PTR)-1)&amp;0xffff ＝ <c>0xFFFF</c>，
    ///    不是直觉上的 0xFFFFFFFF，且必须按 8 字节传。
    /// </summary>
    public sealed unsafe class WaveOutAudioPlayer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public nuint dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext;
            public nuint reserved;
        }

        const uint WHDR_DONE = 0x00000001, WHDR_PREPARED = 0x00000002, WHDR_INQUEUE = 0x00000010;
        const int Headers = 6;              // 6 × 50ms ≈ 300ms 抖动余量
        int SlotFrames => Rate / 20;        // 每格 50ms
        int BytesPerFrame => Channels * 2;  // 恒 PCM16

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, nuint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr cb, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR wh, int cbwh);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR wh, int cbwh);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR wh, int cbwh);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);
        [DllImport("winmm.dll")] static extern int waveOutGetErrorTextW(int err, IntPtr buf, uint len);
        [DllImport("ole32.dll")] static extern IntPtr CoTaskMemAlloc(nuint cb);
        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

        readonly string _ffmpeg, _media;
        readonly bool _useWindowsDecoder;
        readonly Action<string>? _log;
        IntPtr _hwo;
        WAVEHDR* _hdrs;
        readonly GCHandle[] _pins = new GCHandle[Headers];
        Channel<byte[]>? _pcm;
        Process? _proc;
        CancellationTokenSource? _cts;
        long _writtenFrames;
        int _underrun;
        double _streamStart;
        ulong _gen;
        volatile bool _running, _paused, _eof, _finishFired;
        double _volume = 1.0;
        Thread? _thread;
        readonly object _lock = new();

        public int Rate { get; private set; } = 48000;
        public int Channels { get; private set; } = 2;
        public string LastError { get; private set; } = "";
        public bool Available => _hwo != IntPtr.Zero;
        public bool Eof => _eof;
        /// <summary>管道供不上时补静音的次数：落地后如果这台机器上非零，说明音频解码被拖慢了，先看这个数。</summary>
        public int Underruns => Volatile.Read(ref _underrun);
        /// <summary>已交给 waveOut 的采样帧数：判"真出声了没"只能数这个——设备打开成功不代表有数据在流。</summary>
        public long WrittenFrames => Interlocked.Read(ref _writtenFrames);
        /// <summary>读到母带尾部。</summary>
        public event Action? Finished;

        public WaveOutAudioPlayer(string ffmpeg, string media, Action<string>? log = null, bool useWindowsDecoder = false)
        {
            _ffmpeg = ffmpeg; _media = media; _log = log;
            _useWindowsDecoder = useWindowsDecoder;
        }

        /// <summary>打开设备并备好 6 格缓冲。失败不抛异常——预览没声也好过崩，LastError 说明原因。</summary>
        public bool Open()
        {
            if (Available) return true;   // 幂等：本实例与一个预览窗同生命周期，不重复开
            try
            {
                var wf = new WAVEFORMATEX
                {
                    wFormatTag = 1 /*PCM*/,
                    nChannels = (ushort)Channels,
                    nSamplesPerSec = (uint)Rate,
                    nAvgBytesPerSec = (uint)(Rate * BytesPerFrame),
                    nBlockAlign = (ushort)BytesPerFrame,
                    wBitsPerSample = 16,
                    cbSize = 0,
                };
                int err = waveOutOpen(out _hwo, 0xFFFF /*WAVE_MAPPER*/, ref wf, IntPtr.Zero, IntPtr.Zero, 0u /*CALLBACK_NULL*/);
                if (err != 0) { LastError = ErrText(err); _hwo = IntPtr.Zero; return false; }
                _hdrs = (WAVEHDR*)CoTaskMemAlloc((nuint)(Headers * sizeof(WAVEHDR)));
                for (int i = 0; i < Headers; i++)
                {
                    int bytes = SlotFrames * BytesPerFrame;
                    _pins[i] = GCHandle.Alloc(new byte[bytes], GCHandleType.Pinned);
                    _hdrs[i] = default;
                    _hdrs[i].lpData = _pins[i].AddrOfPinnedObject();
                    _hdrs[i].dwBufferLength = (uint)bytes;
                    int e2 = waveOutPrepareHeader(_hwo, ref _hdrs[i], sizeof(WAVEHDR));
                    if (e2 != 0) { LastError = "PrepareHeader " + ErrText(e2); CloseDevice(); return false; }
                }
                ApplyVolume();
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        static string ErrText(int err)
        {
            try
            {
                var buf = stackalloc char[128];
                new Span<char>(buf, 128).Clear();   // 不先清零：API 没写文本时按 C 串读会读到栈上的随机垃圾
                waveOutGetErrorTextW(err, (IntPtr)buf, 128);
                return "err=" + err + " " + Marshal.PtrToStringUni((nint)buf);
            }
            catch { return "err=" + err; }
        }

        /// <summary>音量 0~1：waveOut 只有左右两路 16 位定点，够用且不需要碰 COM。</summary>
        public void SetVolume(double v)
        {
            _volume = Math.Clamp(v, 0, 1);
            ApplyVolume();
        }

        void ApplyVolume()
        {
            if (!Available) return;
            ushort q = (ushort)(_volume * 0xFFFF);
            waveOutSetVolume(_hwo, (uint)(q | (q << 16)));
        }

        public void Seek(double at) => Restart(Math.Max(0, at));
        void StartStream(double at) => Restart(at);

        void Restart(double at)
        {
            lock (_lock)
            {
                StopDecoder();
                if (!Available) return;
                waveOutReset(_hwo);
                for (int i = 0; i < Headers; i++) _hdrs[i].dwFlags &= ~WHDR_INQUEUE;
                _writtenFrames = 0;
                _underrun = 0;
                _eof = false;
                _finishFired = false;
                _streamStart = at;
                _gen++;
                _cts = new CancellationTokenSource();
                _pcm = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Headers * 4)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                });
                var cts = _cts;
                var ch = _pcm;
                try
                {
                    if (_useWindowsDecoder)
                    {
                        ulong nativeGen = _gen;
                        CancellationToken nativeToken = cts.Token;
                        _ = Task.Run(() => WindowsPcmPipe.Read(_media, at, ch, SlotFrames * BytesPerFrame, nativeToken,
                            () => { if (nativeGen == Volatile.Read(ref _gen)) _eof = true; }, msg => _log?.Invoke(msg)));
                        return;
                    }
                    string args = "-hide_banner -loglevel error -nostdin"
                        + $" -ss {at.ToString("F3", CultureInfo.InvariantCulture)}"
                        + $" -i \"{_media}\" -map 0:a:0 -vn -sn -c:a pcm_s16le -f s16le"
                        + $" -ac {Channels} -ar {Rate} -";
                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffmpeg,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    _proc = Process.Start(psi);
                    if (_proc == null) { ch.Writer.TryComplete(); return; }
                    try { FfmpegProcess.AttachToJob(_proc); } catch { }
                    var proc = _proc;
                    ulong gen = _gen;
                    // 读循环必须待在非 unsafe 的类型里：类型上的 unsafe 会传染给成员，而 C# 禁止在 unsafe 上下文中 await
                    _ = Task.Run(() => PcmPipe.ReadAsync(proc, ch, SlotFrames * BytesPerFrame, cts.Token,
                        onEnded: () => { if (gen != Volatile.Read(ref _gen)) return; _eof = true; if (!_finishFired) { _finishFired = true; try { Finished?.Invoke(); } catch { } } },
                        onLog: msg => _log?.Invoke(msg)));
                }
                catch (Exception ex)
                {
                    _log?.Invoke("[音频] 解码进程没起来（本轮无声）：" + ex.Message);
                    ch.Writer.TryComplete();
                    _proc = null;
                }
            }
        }

        /// <summary>播放：首次起泵线程；暂停后再调用＝醒设备。</summary>
        public void Start()
        {
            if (!Available) return;
            if (_running) { _paused = false; waveOutRestart(_hwo); return; }
            _running = true;
            _paused = false;
            _thread = new Thread(PumpLoop) { IsBackground = true, Name = "yusnip-waveout" };
            _thread.Start();
        }

        public void Pause()
        {
            if (!Available) return;
            waveOutPause(_hwo);
            _paused = true;
        }

        public void Stop()
        {
            if (!Available) return;
            _paused = true;
            waveOutPause(_hwo);
            StopDecoder();
        }

        void StopDecoder()
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
            try { _proc?.Kill(entireProcessTree: true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }

        /// <summary>此刻真正在响的位置（母带时刻）：已交给设备的帧数 − 还没播完的队列，再加队列的半格估计。</summary>
        public double Clock()
        {
            if (!Available) return _streamStart;
            int queued = 0;
            for (int i = 0; i < Headers; i++)
                if ((_hdrs[i].dwFlags & (WHDR_INQUEUE | WHDR_DONE)) == WHDR_INQUEUE) queued++;
            long pending = Math.Max(0L, (long)queued * SlotFrames - SlotFrames / 2);
            return _streamStart + Math.Max(0, _writtenFrames - pending) / (double)Rate;
        }

        void PumpLoop()
        {
            while (_running)
            {
                if (!_paused && _pcm != null)
                {
                    lock (_lock)
                    {
                        for (int i = 0; i < Headers; i++)
                        {
                            if ((_hdrs[i].dwFlags & WHDR_INQUEUE) != 0) continue;  // 这格还没播完
                            FillAndSubmit(i);
                        }
                    }
                }
                Thread.Sleep(20);   // 只能用轮询：见类型注释①
            }
        }

        // 解码线程交下来的块恒等于一格（不足已在读循环里补零），所以这里没有"半块"要留。
        void FillAndSubmit(int i)
        {
            ref WAVEHDR h = ref _hdrs[i];
            var dst = new Span<byte>((byte*)h.lpData, (int)h.dwBufferLength);
            int filled = 0;
            var ch = _pcm;
            if (ch != null && ch.Reader.TryRead(out var block))
            {
                int n = Math.Min(block.Length, dst.Length);
                block.AsSpan(0, n).CopyTo(dst.Slice(filled, n));
                filled = n;
            }
            if (filled < dst.Length)
            {
                dst.Slice(filled).Clear();            // 供不上就补静音，时钟继续走，别让设备停住
                if (filled == 0) Interlocked.Increment(ref _underrun);
            }
            h.dwBytesRecorded = 0;
            h.dwFlags &= ~WHDR_DONE;
            waveOutWrite(_hwo, ref h, sizeof(WAVEHDR));
            _writtenFrames += SlotFrames;
        }

        void CloseDevice()
        {
            if (_hwo != IntPtr.Zero)
            {
                waveOutReset(_hwo);
                if (_hdrs != null)
                    for (int i = 0; i < Headers; i++) waveOutUnprepareHeader(_hwo, ref _hdrs[i], sizeof(WAVEHDR));
                waveOutClose(_hwo);
                _hwo = IntPtr.Zero;
            }
            for (int i = 0; i < _pins.Length; i++) if (_pins[i].IsAllocated) _pins[i].Free();
            if (_hdrs != null) { CoTaskMemFree((nint)_hdrs); _hdrs = null; }
        }

        public void Dispose()
        {
            _running = false;
            var th = _thread; _thread = null;
            try { th?.Join(300); } catch { }   // 先让泵线程退出，否则它可能正往已 close 的 hwo 上写
            StopDecoder();
            CloseDevice();
            try { _pcm?.Writer.TryComplete(); } catch { }
        }
    }

    /// <summary>
    /// ffmpeg 裸 PCM 管道读线程。单独成类只因为 <see cref="WaveOutAudioPlayer"/> 是 <c>unsafe</c> 类型，
    /// 而类型上的 unsafe 会传染给成员、C# 又禁止在 unsafe 上下文中 <c>await</c>。
    /// 交下来的块恒为 <paramref name="chunk"/> 字节（尾部不足补零），所以下游不需要处理半块。
    /// </summary>
    static class PcmPipe
    {
        public static async Task ReadAsync(Process proc, Channel<byte[]> ch, int chunk,
            CancellationToken ct, Action onEnded, Action<string> onLog)
        {
            var buf = new byte[chunk];
            var stream = proc.StandardOutput.BaseStream;
            bool ended = false;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int off = 0;
                    while (off < chunk)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(off, chunk - off), ct);
                        if (n <= 0) break;
                        off += n;
                    }
                    if (off <= 0) { ended = true; break; }
                    if (off < chunk) new Span<byte>(buf, off, chunk - off).Clear();
                    await ch.Writer.WriteAsync((byte[])buf.Clone(), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { onLog("[音频] 读线程异常：" + ex.Message); }
            try { ch.Writer.TryComplete(); } catch { }
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ended) onEnded();
        }
    }
}
