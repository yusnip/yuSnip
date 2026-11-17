using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform.Audio
{
    /// <summary>
    /// WASAPI 互操作底座。⚠ 必须走「裸 IntPtr + 手写 vtable 派发」，不能用 [ComImport] 接口 +
    /// [MarshalAs(UnmanagedType.Interface)] 的封送写法：主程序 PublishAot=true 会在 runtimeconfig
    /// 里把 System.Runtime.InteropServices.BuiltInComInterop.IsSupported 置 false，禁用内置 COM 封送，
    /// 届时 InterfaceMarshaler.ConvertToManaged 直接抛 NotSupportedException（"Built-in COM has been
    /// disabled via a feature switch"），两路音频全失败→退化为无声。本工程 Accessibility/UIAutomationHelper
    /// 早已用同款裸指针 vtable 派发在 AOT 产线验证，这里与之保持一致。
    /// IID/CLSID 与系统头文件一致（并经 NAudio 反射实证）：
    /// IMMDeviceEnumerator 尾段 DE8DB63617E6、IMMDevice 尾段 B948E807363F——
    /// 网传拷贝常见的 ...4F37-A746-E8113D58703E / ...B948E8073685 是错的，本机会直接 E_NOINTERFACE。
    /// </summary>
    internal static class WasapiNative
    {
        public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");

        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
        public const uint INFINITE = 0xFFFFFFFF;

        // ===== vtable 槽位（IUnknown 占 0=QueryInterface 1=AddRef 2=Release，方法从 3 起）=====
        // IMMDeviceEnumerator：EnumAudioEndpoints=3 GetDefaultAudioEndpoint=4 GetDevice=5 Reg=6 Unreg=7
        public const int SLOT_ENUM_GETDEFAULT = 4;
        // IMMDevice：Activate=3 OpenPropertyStore=4 GetId=5 GetState=6
        public const int SLOT_DEVICE_ACTIVATE = 3;
        // IAudioClient（经 NAudio 2.2.1 同进程反射实证，⚠ 与网传"Initialize 后紧跟 GetCapabilities"不同）：
        // Initialize=3 GetBufferSize=4 GetStreamLatency=5 GetCurrentPadding=6 IsFormatSupported=7
        // GetMixFormat=8 GetDevicePeriod=9 Start=10 Stop=11 Reset=12 SetEventHandle=13 GetService=14
        public const int SLOT_CLIENT_INITIALIZE = 3;
        public const int SLOT_CLIENT_GETMIXFORMAT = 8;
        public const int SLOT_CLIENT_START = 10;
        public const int SLOT_CLIENT_STOP = 11;
        public const int SLOT_CLIENT_SETEVENTHANDLE = 13;
        public const int SLOT_CLIENT_GETSERVICE = 14;
        // IAudioCaptureClient（本机 SDK 头 Audioclient.h 实证）：GetBuffer=3 ReleaseBuffer=4 GetNextPacketSize=5
        // ⚠ ResetNextPacketSize 属 Win11 的 IAudioCaptureClient2，塞进本接口会整体错位。
        public const int SLOT_CAP_GETBUFFER = 3;
        public const int SLOT_CAP_RELEASEBUFFER = 4;
        public const int SLOT_CAP_GETNEXTPACKETSIZE = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEXTENSIBLE
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit); // dwCoInit: 0=MTA, 2=STA

        /// <summary>
        /// 确保当前线程已初始化 COM。每个做 COM 调用的线程（调用方线程、采集泵线程）都必须各自初始化一次。
        /// 线程已初始化/模式不同一律视为成功，仅在真正硬错误时抛出。
        /// </summary>
        public static void EnsureCom()
        {
            int hr = CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
            // S_OK(0)=新初始化, S_FALSE(1)=已初始化, RPC_E_CHANGED_MODE(0x80010106)=已按其他模式初始化——均可用
            const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
            if (hr != 0 && hr != 1 && hr != RPC_E_CHANGED_MODE)
                Marshal.ThrowExceptionForHR(hr);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In] ref Guid riid,
            out IntPtr ppv);

        /// <summary>创建 MMDeviceEnumerator，返回裸 IUnknown 指针（调用方负责 ReleaseCom）。
        /// ⚠ 出参用 IntPtr 而非 [ComImport] 接口：AOT 下内置 COM 封送被禁用，接口出参会抛
        /// NotSupportedException（见类型注释）。指针交由手写 vtable 派发使用。</summary>
        public static IntPtr CreateEnumerator()
        {
            EnsureCom();
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out IntPtr pEnum);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            return pEnum;
        }

        /// <summary>取某 COM 对象的 vtable 第 slot 项，转成 unmanaged stdcall 函数指针。</summary>
        public static unsafe IntPtr GetSlot(IntPtr comPtr, int slot) => (*(IntPtr**)comPtr)[slot];

        /// <summary>释放一个 COM 接口指针（调 IUnknown::Release，slot 2）。空指针安全。</summary>
        public static unsafe void ReleaseCom(IntPtr comPtr)
        {
            if (comPtr == IntPtr.Zero) return;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)comPtr)[2];
            release(comPtr);
        }
    }
}
