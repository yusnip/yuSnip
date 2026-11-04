using System.IO.Pipes;
using System.Threading;

namespace ScreenCaptureTool.Platform.Audio
{
    /// <summary>
    /// 混音喂给线程（计划 §3.1）：消费 <see cref="WasapiDualCapture"/> 两路环形缓冲，
    /// 各自线性重采样到 48k 立体声 → 麦克风按音量系数缩放 → 相加削顶钳位 → 写命名管道
    /// （ffmpeg 以 <c>-f f32le -ar 48000 -ac 2 -i \\.\pipe\…</c> 读取）。
    /// split=true 时不混音，系统/麦克风各写一条管道（分离双轨档）。
    /// 每个录制分段对应一条新管道（<see cref="StartNewSegment"/>）；暂停＝停泵且丢弃缓冲。
    /// 无数据时按实时节奏补静音，保证管道时间轴连续（否则 ffmpeg 音频会越录越短）。
    /// </summary>
    public sealed class AudioMixerFeed : IDisposable
    {
        internal const int TargetRate = 48000;
        private const int TickFrames = 480;              // 10ms
        private const int TickMs = 10;

        private readonly WasapiDualCapture _capture;
        private readonly bool _useSystem, _useMic, _split;
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _paused;
        private PipeHub? _pipe;      // 混音/单路（读写一律走 Volatile.* / Interlocked.Exchange，故不标 volatile）
        private PipeHub? _pipeSys;   // 分离双轨
        private PipeHub? _pipeMic;

        private readonly RingResampler _sysRes;
        private readonly RingResampler _micRes;

        /// <summary>某路设备失效（已自动降级为另一路）。上层用它发①/②降级事件。</summary>
        public event Action<WasapiStreamKind>? ChannelLost;

        /// <summary>取证用：两路各自"读空补静音"的拍数。任一非零即说明该路当时供不上数。</summary>
        public (int sys, int mic) StarveTicks => (_sysRes.StarveTicks, _micRes.StarveTicks);

        /// <summary>本段管道实际写出的拍数（10ms 一拍）。</summary>
        public long PumpTicks => Interlocked.Read(ref _pumpTicks);
        private long _pumpTicks;

        /// <summary>
        /// 参与混音的每一路都几乎全程读空 ⇒ 这条音轨基本是数字静音。
        /// 5 秒以内的短段不判：起录与恢复的头几拍本来就要等设备和管道就位。
        /// </summary>
        public bool AllStarved
        {
            get
            {
                long t = PumpTicks;
                if (t < 500) return false;
                long budget = t * 98 / 100;
                var (sys, mic) = StarveTicks;
                if (_useSystem && sys < budget) return false;
                if (_useMic && mic < budget) return false;
                return true;
            }
        }

        /// <summary>当前分段管道路径：混音/单路＝PipePath；分离双轨＝PipePathSystem + PipePathMic。</summary>
        public string PipePath => Volatile.Read(ref _pipe)?.Path ?? "";
        public string PipePathSystem => Volatile.Read(ref _pipeSys)?.Path ?? "";
        public string PipePathMic => Volatile.Read(ref _pipeMic)?.Path ?? "";

        public AudioMixerFeed(WasapiDualCapture capture, bool useSystem, bool useMic, float micVolume01, bool split = false)
        {
            _capture = capture;
            _useSystem = useSystem;
            _useMic = useMic;
            _split = split; // 编码端已按分离布局绑定双管道；缺失一路也保留该轨并补静音。
            _sysRes = new RingResampler(() => _capture.SystemRing, () => _capture.SystemChannels,
                () => _capture.SystemRate, gain: 1f);
            _micRes = new RingResampler(() => _capture.MicRing, () => _capture.MicChannels,
                () => _capture.MicRate, gain: Math.Clamp(micVolume01, 0f, 2f));
            capture.DeviceLost += k => ChannelLost?.Invoke(k);
            _thread = new Thread(MixLoop) { IsBackground = true, Name = "yuSnip-AudioMixFeed" };
            _thread.Start();
        }

        /// <summary>为下一个分段新建管道（旧管道立即废弃）。返回主管道路径（分离档返回系统路）。</summary>
        public string StartNewSegment()
        {
            Interlocked.Exchange(ref _pipe, null)?.Dispose();
            Interlocked.Exchange(ref _pipeSys, null)?.Dispose();
            Interlocked.Exchange(ref _pipeMic, null)?.Dispose();
            if (_split)
            {
                Volatile.Write(ref _pipeSys, new PipeHub());
                Volatile.Write(ref _pipeMic, new PipeHub());
                return PipePathSystem;
            }
            Volatile.Write(ref _pipe, new PipeHub());
            return PipePath;
        }

        public void Pause()
        {
            _paused = true;
            _capture.SuspendAll();
        }

        public void Resume()
        {
            _capture.ResumeAll();
            _paused = false;
        }

        /// <summary>
        /// 结束当前分段：关闭本段管道（ffmpeg 侧读到 EOF，从阻塞的音频读中解出，进而处理 stdin 的 'q' 并冲刷视频缓冲）。
        /// 采集与线程保持暂停态，不新建管道——Resume 会为下一段另起新 PipeHub。
        /// 若不做这一步，暂停时 ffmpeg 卡在读管道上，'q' 无法被处理 → 500ms 超时后被强杀 → 本段 .ts 缓冲未冲刷＝0 字节被丢弃。
        /// </summary>
        public void EndSegment()
        {
            Interlocked.Exchange(ref _pipe, null)?.Dispose();
            Interlocked.Exchange(ref _pipeSys, null)?.Dispose();
            Interlocked.Exchange(ref _pipeMic, null)?.Dispose();
        }

        private void MixLoop()
        {
            var dstSys = new float[TickFrames * 2];
            var dstMic = new float[TickFrames * 2];
            var bytes = new byte[TickFrames * 8];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextTick = 0;

            while (!_stop)
            {
                nextTick += TickMs;
                int sleep = (int)(nextTick - sw.ElapsedMilliseconds);
                if (sleep > 0) Thread.Sleep(sleep);
                else nextTick = sw.ElapsedMilliseconds; // 落后则重置节奏，不追写积压

                if (_paused) continue;
                var pipe = Volatile.Read(ref _pipe);
                var pipeSys = Volatile.Read(ref _pipeSys);
                var pipeMic = Volatile.Read(ref _pipeMic);
                bool mixedReady = !_split && pipe != null && pipe.IsConnected;
                if (!_split)
                {
                    if (pipe == null || !pipe.IsConnected) continue;
                }
                else if ((pipeSys == null || !pipeSys.IsConnected) && (pipeMic == null || !pipeMic.IsConnected))
                {
                    continue;
                }
                if (mixedReady || _split) { /* 进入产出 */ }

                Array.Clear(dstSys, 0, dstSys.Length);
                Array.Clear(dstMic, 0, dstMic.Length);
                if (_useSystem) _sysRes.Next(TickFrames, dstSys);
                if (_useMic) _micRes.Next(TickFrames, dstMic);

                if (_split)
                {
                    if (pipeSys != null && pipeSys.IsConnected)
                        TryWrite(pipeSys, dstSys, bytes);
                    if (pipeMic != null && pipeMic.IsConnected)
                        TryWrite(pipeMic, dstMic, bytes);
                    continue;
                }

                var dst = dstSys;
                if (_useSystem && _useMic)
                {
                    for (int i = 0; i < dst.Length; i++) dst[i] += dstMic[i];
                }
                else if (!_useSystem)
                {
                    dst = dstMic;
                }
                TryWrite(pipe!, dst, bytes);
            }
        }

        private void TryWrite(PipeHub pipe, float[] samples, byte[] bytes)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), v);
            }
            try { pipe.Write(bytes, 0, samples.Length * 4); Interlocked.Increment(ref _pumpTicks); }
            catch { /* 管道断开＝分段进程已退，等下一次 StartNewSegment */ }
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread.Join(300); } catch { }
            Volatile.Read(ref _pipe)?.Dispose();
            Volatile.Read(ref _pipeSys)?.Dispose();
            Volatile.Read(ref _pipeMic)?.Dispose();
        }
    }

    /// <summary>
    /// 单路环形缓冲 → 48k 立体声帧序列：线性插值重采样，跨调用保持源相位；
    /// 单声道复制到双声道，多声道取前两路；增益在输出侧乘。数据不足补零（保时间轴连续）。
    /// <para>⚠ 相位记账只能"消费掉真正用到的整帧"，其余必须整帧结转给下一拍（<c>_carry</c>）。
    /// 2026-09-24 之前的写法是每拍从环形缓冲多读 2 帧不要、再把负相位强行归零，后果有两层：
    /// ①每秒 100 次、每次 2 帧的波形跳变（听感"碎、毛"）；②一旦某拍读空，小数相位按"每拍 +480"
    /// 暴涨，之后要几十秒才爬回来，这期间整条音轨被填成数字静音（实测母带峰值 −inf）。
    /// 系统环回在不出声时本就不投递数据包，所以②是常态而非边角。</para>
    /// </summary>
    internal sealed class RingResampler
    {
        private readonly Func<SampleRing?> _ring;
        private readonly Func<int> _channels;
        private readonly Func<int> _rate;
        private readonly float _gain;

        private float[] _buf = new float[4096];      // 本拍工作区：开头是结转帧，后面是刚从环形缓冲读到的
        private float[] _carry = new float[64];      // 上一拍没用完的整帧（交织）
        private int _carryFrames;
        private double _frac;                        // 本拍起点在 _buf 里的小数相位（0 ≤ _frac < 1）

        /// <summary>读空补静音的拍数（取证用：非零说明这一路供不上数）。</summary>
        public int StarveTicks { get; private set; }

        // 原生录制暂停后清除插值结转，避免把暂停前声音带进下一段。
        public void Reset() { _carryFrames = 0; _frac = 0; }

        public RingResampler(Func<SampleRing?> ring, Func<int> channels, Func<int> rate, float gain)
        {
            _ring = ring; _channels = channels; _rate = rate; _gain = gain;
        }

        /// <summary>产出 <paramref name="outFrames"/> 帧交织立体声 float 到 <paramref name="dst"/>（调用方负责预清 0）。</summary>
        public void Next(int outFrames, float[] dst)
        {
            int ch = Math.Max(1, _channels());
            int srcRate = Math.Max(8000, _rate());
            double ratio = srcRate / (double)AudioMixerFeed.TargetRate;

            // 最后一帧输出用 i0 与 i0+1，故上界 = _frac + (outFrames-1)*ratio 的整帧号 + 2
            int needSrc = (int)Math.Ceiling(_frac + (outFrames - 1) * ratio) + 2;
            int floats = needSrc * ch;
            if (_buf.Length < floats) Array.Resize(ref _buf, floats);

            int keepFloats = _carryFrames * ch;
            if (keepFloats > 0) Array.Copy(_carry, 0, _buf, 0, keepFloats);
            var ring = _ring();
            // floats - keepFloats 天然是 ch 的整数倍（两项都是），不会切碎一帧
            int got = ring != null ? ring.Read(_buf, keepFloats, floats - keepFloats) : 0;
            int srcFrames = _carryFrames + got / ch;
            _carryFrames = 0;

            int f = 0;
            for (; f < outFrames; f++)
            {
                double p = _frac + f * ratio;
                int i0 = (int)p;
                if (i0 >= srcFrames) break;   // 断供：其余输出保持调用方预置的 0
                float frac = (float)(p - i0);
                FrameAt(_buf, ch, i0, out float l0, out float r0);
                float l1 = l0, r1 = r0;
                if (i0 + 1 < srcFrames) FrameAt(_buf, ch, i0 + 1, out l1, out r1);
                int di = f * 2;
                dst[di] = (l0 + (l1 - l0) * frac) * _gain;
                dst[di + 1] = (r0 + (r1 - r0) * frac) * _gain;
            }
            if (f < outFrames) StarveTicks++;

            // 记账：真正消费到 consumed，剩下的一帧不丢，整帧结转
            double consumedPos = _frac + f * ratio;
            int consumed = (int)consumedPos;
            if (consumed > srcFrames) consumed = srcFrames;
            _frac = consumedPos - consumed;
            if (_frac >= 1.0) _frac = 0;      // 只有断供那一拍会 ≥1：下一拍从头对齐，宁可重复半帧也不跳帧
            int keep = srcFrames - consumed;
            if (keep > 0)
            {
                if (_carry.Length < keep * ch) Array.Resize(ref _carry, keep * ch);
                Array.Copy(_buf, consumed * ch, _carry, 0, keep * ch);
                _carryFrames = keep;
            }
        }

        private static void FrameAt(float[] buf, int ch, int frame, out float l, out float r)
        {
            int baseIdx = frame * ch;
            l = buf[baseIdx];
            r = ch >= 2 ? buf[baseIdx + 1] : buf[baseIdx];
        }
    }

    /// <summary>单段命名管道服务：Out 方向、字节流、后台等待 ffmpeg 连接。</summary>
    internal sealed class PipeHub : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        public string Path { get; }
        public bool IsConnected => _server.IsConnected;

        public PipeHub()
        {
            // ⚠ NamedPipeServerStream 第一参是裸管道名——它自己会加 \\.\pipe\ 前缀；
            // 传全路径会注册成 \\.\pipe\pipe\... 幽灵管道，ffmpeg 端表现为 No such file or directory。
            string name = "yuSnip_rec_" + Guid.NewGuid().ToString("N");
            Path = @"\\.\pipe\" + name;
            _server = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 18, 1 << 18);
            ThreadPool.QueueUserWorkItem(async _ =>
            {
                try { await _server.WaitForConnectionAsync().ConfigureAwait(false); }
                catch { }
            });
        }

        public void Write(byte[] buf, int off, int len) => _server.Write(buf, off, len);

        public void Dispose()
        {
            try { _server.Dispose(); } catch { }
        }
    }
}
