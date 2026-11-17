using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCaptureTool.Platform.Audio
{
    /// <summary>单路来源：系统环回 / 麦克风。</summary>
    public enum WasapiStreamKind { SystemLoopback, Microphone }

    /// <summary>
    /// WASAPI 双路共享模式采集（录屏引擎音频输入，计划 §3.1）。
    /// 系统声音＝默认播放端点环回（AUDCLNT_STREAMFLAGS_LOOPBACK），麦克风＝默认录音端点；
    /// 均为共享模式 + 事件驱动 + 设备混音格式（期望 IEEE_FLOAT，PCM16 时转 float）。
    /// 每路一个采集线程，把原始样本写进环形缓冲，由 <see cref="AudioMixerFeed"/> 按实时节奏消费。
    /// 设备消失（拔出/独占抢占）经 <see cref="DeviceLost"/> 上报，供上层走①/②降级。
    /// </summary>
    public sealed class WasapiDualCapture : IDisposable
    {
        private WasapiStream? _system;
        private WasapiStream? _mic;

        /// <summary>某一路设备失效（AUDCLNT_E_DEVICE_INVALIDATED 系）。参数：来源。</summary>
        public event Action<WasapiStreamKind>? DeviceLost;

        /// <summary>最近一次 StartStream 失败时捕获的异常文本（成功或从未失败为 null），供上层诊断"无声降级"成因。</summary>
        public string? LastStartError { get; private set; }

        /// <summary>启动指定路。返回 false＝该路当前不可用（无设备/初始化失败），调用方决定降级或忽略。</summary>
        public bool StartStream(WasapiStreamKind kind)
        {
            WasapiStream? pending = null;
            try
            {
                var s = pending = new WasapiStream(kind, OnStreamDeviceLost);
                s.Start();
                var old = kind == WasapiStreamKind.SystemLoopback ? _system : _mic;
                if (kind == WasapiStreamKind.SystemLoopback) _system = s; else _mic = s;
                pending = null;
                old?.Dispose();
                return true;
            }
            catch (Exception ex) { pending?.Dispose(); LastStartError = kind + ": " + ex; return false; }
        }

        /// <summary>暂停＝两路停泵并丢弃缓冲（暂停段不写入）。</summary>
        public void SuspendAll()
        {
            _system?.Stop();
            _mic?.Stop();
            _system?.Ring.Clear();
            _mic?.Ring.Clear();
        }

        public void ResumeAll()
        {
            _system?.Start();
            _mic?.Start();
        }

        private void OnStreamDeviceLost(WasapiStreamKind kind) => DeviceLost?.Invoke(kind);

        public SampleRing? SystemRing => _system?.Ring;
        public SampleRing? MicRing => _mic?.Ring;
        public int SystemChannels => _system?.Channels ?? 2;
        public int MicChannels => _mic?.Channels ?? 1;
        public int SystemRate => _system?.Rate ?? 48000;
        public int MicRate => _mic?.Rate ?? 48000;

        public void Dispose()
        {
            _system?.Dispose();
            _mic?.Dispose();
            _system = _mic = null;
        }
    }

    /// <summary>单路 WASAPI 采集线程 + 环形缓冲（float 交织样本）。
    /// ⚠ 全部经裸 IntPtr + 手写 vtable 派发调用 COM（见 <see cref="WasapiNative"/> 类型注释）：
    /// AOT 下内置 COM 封送被禁用，持有 [ComImport] 接口字段并靠接口方法调用会在运行期抛异常。</summary>
    internal sealed class WasapiStream : IDisposable
    {
        private readonly WasapiStreamKind _kind;
        private readonly Action<WasapiStreamKind> _lost;
        private IntPtr _enumerator;   // IMMDeviceEnumerator*，Dispose 里 ReleaseCom
        private IntPtr _device;       // IMMDevice*
        private IntPtr _audioClient;  // IAudioClient*
        private IntPtr _captureClient;// IAudioCaptureClient*
        private IntPtr _event;
        private Thread? _thread;
        private volatile bool _running;

        public SampleRing Ring { get; } = new(48 * 1024);
        public int Channels { get; private set; } = 2;
        public int Rate { get; private set; } = 48000;
        private bool _floatFormat = true;

        public WasapiStream(WasapiStreamKind kind, Action<WasapiStreamKind> lost)
        {
            _kind = kind;
            _lost = lost;
        }

        public void Start()
        {
            if (_running) return;
            WasapiNative.EnsureCom(); // 调用方线程需已初始化 COM 才能创建枚举器
            if (_audioClient == IntPtr.Zero) InitDevice();
            unsafe
            {
                var start = (delegate* unmanaged[Stdcall]<IntPtr, int>)WasapiNative.GetSlot(_audioClient, WasapiNative.SLOT_CLIENT_START);
                int hr = start(_audioClient); // InitDevice 保证 _audioClient 非空
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
            _running = true;
            _thread = new Thread(PumpLoop) { IsBackground = true, Name = "yuSnip-Wasapi-" + _kind };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _thread?.Join(500); } catch { }
            _thread = null;
            if (_audioClient != IntPtr.Zero)
            {
                unsafe
                {
                    var stop = (delegate* unmanaged[Stdcall]<IntPtr, int>)WasapiNative.GetSlot(_audioClient, WasapiNative.SLOT_CLIENT_STOP);
                    stop(_audioClient);
                }
            }
        }

        private void InitDevice()
        {
            _enumerator = WasapiNative.CreateEnumerator();
            // 环回＝默认播放端点(eRender=0)，麦克风＝默认录音端点(eCapture=1)；角色 eConsole=1
            unsafe
            {
                var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, out IntPtr, int>)
                    WasapiNative.GetSlot(_enumerator, WasapiNative.SLOT_ENUM_GETDEFAULT);
                int hr = getDefault(_enumerator, _kind == WasapiStreamKind.SystemLoopback ? 0 : 1, 1, out _device);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                Guid iidClient = WasapiNative.IID_IAudioClient;
                var activate = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, uint, IntPtr, out IntPtr, int>)
                    WasapiNative.GetSlot(_device, WasapiNative.SLOT_DEVICE_ACTIVATE);
                hr = activate(_device, ref iidClient, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out _audioClient);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                var getMix = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)
                    WasapiNative.GetSlot(_audioClient, WasapiNative.SLOT_CLIENT_GETMIXFORMAT);
                hr = getMix(_audioClient, out IntPtr fmtPtr);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var wf = Marshal.PtrToStructure<WasapiNative.WAVEFORMATEXTENSIBLE>(fmtPtr);
                    Channels = Math.Max(1, (int)wf.nChannels);
                    Rate = (int)wf.nSamplesPerSec;
                    _floatFormat = wf.wFormatTag == 0xFFFE
                        ? wf.SubFormat.Equals(WasapiNative.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                        : wf.wFormatTag == 0x0003 /*IEEE_FLOAT 非 extensible*/;
                    if (!_floatFormat && !(wf.wFormatTag == 0x0001 && wf.wBitsPerSample == 16))
                        throw new NotSupportedException("不支持的设备混音格式 tag=" + wf.wFormatTag);

                    uint flags = WasapiNative.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                               | (_kind == WasapiStreamKind.SystemLoopback ? WasapiNative.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
                    var init = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, IntPtr, int>)
                        WasapiNative.GetSlot(_audioClient, WasapiNative.SLOT_CLIENT_INITIALIZE);
                    hr = init(_audioClient, 0 /*SHARED*/, flags, 1_000_000 /*1s*/, 0, fmtPtr, IntPtr.Zero);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                    _event = WasapiNative.CreateEventW(IntPtr.Zero, false, false, null);
                    var setEvent = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)
                        WasapiNative.GetSlot(_audioClient, WasapiNative.SLOT_CLIENT_SETEVENTHANDLE);
                    hr = setEvent(_audioClient, _event);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                    Guid iidCap = WasapiNative.IID_IAudioCaptureClient;
                    var getService = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)
                        WasapiNative.GetSlot(_audioClient, WasapiNative.SLOT_CLIENT_GETSERVICE);
                    hr = getService(_audioClient, ref iidCap, out _captureClient);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                }
                finally
                {
                    WasapiNative.CoTaskMemFree(fmtPtr);
                }
            }
        }

        private void PumpLoop()
        {
            WasapiNative.EnsureCom(); // 泵线程是独立线程，须各自初始化 COM 才能调 IAudioCaptureClient
            var cap = _captureClient;
            var buf = new float[8192 * 2];
            unsafe
            {
                var getNextPacket = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)
                    WasapiNative.GetSlot(cap, WasapiNative.SLOT_CAP_GETNEXTPACKETSIZE);
                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, out uint, out uint, out ulong, out ulong, int>)
                    WasapiNative.GetSlot(cap, WasapiNative.SLOT_CAP_GETBUFFER);
                var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)
                    WasapiNative.GetSlot(cap, WasapiNative.SLOT_CAP_RELEASEBUFFER);

                while (_running)
                {
                    WasapiNative.WaitForSingleObject(_event, 200);
                    while (_running && getNextPacket(cap, out uint packets) == 0 && packets > 0)
                    {
                        int hr = getBuffer(cap, out IntPtr data, out uint frames, out uint flags, out _, out _);
                        if (hr == WasapiNative.AUDCLNT_E_DEVICE_INVALIDATED) { RaiseLost(); return; }
                        if (hr != 0) break;
                        try
                        {
                            int samples = (int)frames * Channels;
                            if (samples > buf.Length) buf = new float[samples];
                            if ((flags & WasapiNative.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            {
                                Array.Clear(buf, 0, samples);
                            }
                            else if (_floatFormat)
                            {
                                Marshal.Copy(data, buf, 0, samples);
                            }
                            else
                            {
                                var pcm = new short[samples];
                                Marshal.Copy(data, pcm, 0, samples);
                                for (int i = 0; i < samples; i++) buf[i] = pcm[i] / 32768f;
                            }
                            if ((flags & WasapiNative.AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                                Ring.MarkDiscontinuity();
                            Ring.Write(buf, samples);
                        }
                        finally
                        {
                            releaseBuffer(cap, frames);
                        }
                    }
                }
            }
        }

        private void RaiseLost()
        {
            _running = false;
            try { _lost(_kind); } catch { }
        }

        public void Dispose()
        {
            Stop();
            WasapiNative.ReleaseCom(_captureClient); _captureClient = IntPtr.Zero;
            WasapiNative.ReleaseCom(_audioClient);   _audioClient = IntPtr.Zero;
            WasapiNative.ReleaseCom(_device);        _device = IntPtr.Zero;
            WasapiNative.ReleaseCom(_enumerator);    _enumerator = IntPtr.Zero; // 裸指针须显式 Release
            if (_event != IntPtr.Zero) { NativeMethods.CloseHandle(_event); _event = IntPtr.Zero; }
        }
    }

    /// <summary>float 环形缓冲：采集线程写、混音线程读（各自加锁，量小无性能压力）。</summary>
    public sealed class SampleRing
    {
        private float[] _buf;
        private int _head, _tail, _count;
        private readonly object _lock = new();
        public bool Discontinued;

        public SampleRing(int capacityFloats) => _buf = new float[capacityFloats];

        public void Write(float[] samples, int length)
        {
            lock (_lock)
            {
                EnsureCapacity(length);
                Array.Copy(samples, 0, _buf, _head, Math.Min(length, _buf.Length - _head));
                int wrapped = length - Math.Min(length, _buf.Length - _head);
                if (wrapped > 0) Array.Copy(samples, length - wrapped, _buf, 0, wrapped);
                _head = (_head + length) % _buf.Length;
                _count = Math.Min(_buf.Length, _count + length);
                if (_count == _buf.Length) _tail = _head; // 溢出丢最旧（tail 追 head）
            }
        }

        public int Read(float[] dest, int maxSamples) => Read(dest, 0, maxSamples);

        /// <summary>从 <paramref name="destOffset"/> 处续读（重采样器要把上一拍结转的帧拼在工作区开头）。</summary>
        public int Read(float[] dest, int destOffset, int maxSamples)
        {
            lock (_lock)
            {
                int n = Math.Min(maxSamples, _count);
                int first = Math.Min(n, _buf.Length - _tail);
                Array.Copy(_buf, _tail, dest, destOffset, first);
                if (n - first > 0) Array.Copy(_buf, 0, dest, destOffset + first, n - first);
                _tail = (_tail + n) % _buf.Length;
                _count -= n;
                return n;
            }
        }

        public void MarkDiscontinuity() { lock (_lock) Discontinued = true; }

        public void Clear()
        {
            lock (_lock) { _head = _tail = _count = 0; Discontinued = false; }
        }

        private void EnsureCapacity(int extra)
        {
            int need = _count + extra;
            if (need <= _buf.Length) return;
            int newLen = _buf.Length;
            while (newLen < need) newLen *= 2;
            var nb = new float[newLen];
            for (int i = 0; i < _count; i++) nb[i] = _buf[(_tail + i) % _buf.Length];
            _buf = nb; _head = _count; _tail = 0;
        }
    }
}
