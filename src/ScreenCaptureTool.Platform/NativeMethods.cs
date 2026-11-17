using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// 集中存放 Win32 P/Invoke 声明。
    /// 阶段 2/3 边搬边补，原则上只在本类追加，不让 DllImport 散落到各业务类里。
    /// </summary>
    public static partial class NativeMethods
    {
        // -------------------- 模块 --------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        // -------------------- Native DLL 加载（嵌入式单 exe 用） --------------------

        /// <summary>
        /// 把一个目录追加到用户 DLL 搜索路径末尾（作用域：整个进程）。
        /// 用于让原生→原生的 LoadLibrary（如 OpenCvSharpExtern 加载 opencv_videoio_ffmpeg、
        /// Avalonia.Win32 加载 av_libglesv2）能命中 %TEMP% 下释放出来的 dll。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDllDirectoryW(string? lpPathName);

        /// <summary>
        /// 按指定全路径加载 native dll，并允许其依赖在路径目录内查找。
        /// dwFlags 见 LOAD_WITH_ALTERED_SEARCH_PATH 等，下方常量已给出。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeLibrary(IntPtr hLibModule);

        /// <summary>
        /// 取已加载模块的实际全路径。用于校验 app-local dll 是否真的进了进程：
        /// Windows 按"基础文件名"去重，若系统目录下同名模块被更早加载，
        /// LoadLibrary 只会返回那个旧句柄，此时本函数返回的是系统路径而非我们的释放目录。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleFileNameW(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

        /// <summary>让被加载 dll 的依赖优先在自身目录（lpLibFileName 所在目录）查找。</summary>
        public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        // -------------------- 窗口类 / 窗口 --------------------

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string? lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        // IMM32：解除窗口 IME 上下文关联（快捷键不再被中文输入法拦截）
        [DllImport("imm32.dll", SetLastError = true)]
        public static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateSolidBrush(uint colorRef);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int iMode);

        public const int RGN_OR = 2;
        public const int RGN_DIFF = 4;
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_VISIBLE = 0x10000000;
        public const uint LWA_ALPHA = 0x00000002;

        // -------------------- 消息循环 --------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // -------------------- 全局热键 --------------------

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // -------------------- 系统指标 --------------------

        /// <summary>SM_XVIRTUALSCREEN：虚拟屏幕左边界（物理像素，可能为负）。</summary>
        public const int SM_XVIRTUALSCREEN  = 76;
        /// <summary>SM_YVIRTUALSCREEN：虚拟屏幕上边界（物理像素，可能为负）。</summary>
        public const int SM_YVIRTUALSCREEN  = 77;
        /// <summary>SM_CXVIRTUALSCREEN：虚拟屏幕宽度（物理像素）。</summary>
        public const int SM_CXVIRTUALSCREEN = 78;
        /// <summary>SM_CYVIRTUALSCREEN：虚拟屏幕高度（物理像素）。</summary>
        public const int SM_CYVIRTUALSCREEN = 79;

        /// <summary>SM_REMOTESESSION：当前会话是否为远程会话（RDP / 终端服务）。
        /// 供左键哨兵现场报告判读"是不是远程环境"，2026-09-17 加。⚠ VNC 类软件以服务方式
        /// 注入输入**不会**置这一位（它只是往本地会话里灌事件），故为 0 不能排除 VNC。</summary>
        public const int SM_REMOTESESSION = 0x1000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetSystemMetrics(int nIndex);

        /// <summary>取当前**捕获了鼠标**的窗口（没有则返回 0）。捕获期间鼠标消息全部送给捕获窗口、
        /// 而不是光标下的窗口——`WindowFromPoint` 照样返回光标下的窗，于是"点击被吞"的现场里
        /// 光标下窗口与本进程都正常，真凶是捕获者。2026-09-17 为左键哨兵补。</summary>
        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

        /// <summary>`GetLastInputInfo` 的入参结构（cbSize 必须先填结构体大小）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        /// <summary>
        /// 取**本会话**最后一次输入事件（鼠标或键盘，含 `SendInput` 注入的）的 tick。
        /// 用途：给"物理按键位"做真实性校验——真有一次点击，这一位必然同步更新；
        /// 若它明显陈旧，说明 `GetAsyncKeyState` 那位是陈旧状态，本次"按下"根本没发生。
        /// ⚠ 返回值是 `GetTickCount` 口径的 32 位 tick，不是 `Environment.TickCount64`，
        /// 比较时必须先取 `(uint)Environment.TickCount64` 再按 32 位差值算（见哨兵侧）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        public const uint MB_OK = 0x00000000;
        public const uint MB_ICONINFORMATION = 0x00000040;

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        // -------------------- 鼠标输入 / 低级钩子 --------------------

        public const int WH_MOUSE_LL = 14;
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_MOUSEHWHEEL = 0x020E;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_SHIFT = 0x10;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x01000;

        /// <summary>MSLLHOOKSTRUCT.flags：事件由 SendInput/mouse_event 注入。</summary>
        public const uint LLMHF_INJECTED = 0x00000001;

        /// <summary>MSLLHOOKSTRUCT.flags：事件由较低完整性级别进程注入。</summary>
        public const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, IntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        /// <summary>鼠标左键（`GetAsyncKeyState` 的 nVirtKey 实参，配合 <see cref="GetAsyncKeyState"/> 用）。
        /// ⚠ 它读的是**设备物理状态**，不受任何 `WH_MOUSE_LL` 钩子影响——"事件被别人吞了"靠这一位才藏不住。</summary>
        public const int VK_LBUTTON = 0x01;

        // -------------------- 输入注入（SendInput） --------------------

        /// <summary>INPUT.type：键盘事件。</summary>
        public const uint INPUT_KEYBOARD = 1;
        /// <summary>KEYBDINPUT.dwFlags：本次为松开按键（缺省表示按下）。</summary>
        public const uint KEYEVENTF_KEYUP = 0x0002;
        /// <summary>MapVirtualKeyW.uMapType：虚拟键码 → 扫描码。</summary>
        public const uint MAPVK_VK_TO_VSC = 0;

        public const int VK_C = 0x43;

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        /// <summary>
        /// INPUT 的联合体成员。三个分支同偏移 0，联合体尺寸 = 最大成员 MOUSEINPUT（x64 下 32 字节）。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        /// <summary>
        /// SendInput 的事件描述。联合体含 8 字节指针 → 它必须落在偏移 8，故 x64 下
        /// <c>Marshal.SizeOf&lt;INPUT&gt;()</c> 必须为 40；cbSize 与真实尺寸不符时
        /// SendInput 直接返回 0 且 GetLastError 为 87，注入静默失败（无异常）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", SetLastError = true)]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // -------------------- 窗口枚举与几何 --------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width  => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT_INT
        {
            public int X;
            public int Y;
            public POINT_INT(int x, int y) { X = x; Y = y; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        /// <summary>MONITORINFOEX.dwFlags：表示主显示器。</summary>
        public const uint MONITORINFOF_PRIMARY = 0x00000001;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr BeginDeferWindowPos(int nNumWindows);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DeferWindowPos(
            IntPtr hWinPosInfo,
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint ULW_ALPHA = 0x00000002;

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            ref BLENDFUNCTION pblend,
            uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
        public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>窗口是否启用。**被禁用的窗口仍然会参与命中测试**：点在它上面的点击既不会被它处理、
        /// 也不会转给下层（2026-09-07「取色器被压在遮罩下」那类假卡死正是这一位才认得出），
        /// 故诊断时必须与可见性分开看。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref System.Drawing.Point lpPoint);

        /// <summary>客户区矩形（客户区坐标，原点恒为 0,0）。窗口最小化时全 0。
        /// 与 <see cref="GetWindowRect"/> 配合可区分「点在客户区」与「点在标题栏/边框」——
        /// 后者由系统非客户区处理，**不会**产生任何托管指针事件，故不能算作"本进程漏收点击"。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        /// <summary>把客户区坐标换算为屏幕坐标（此处用 <see cref="POINT_INT"/> 版，便于与 <see cref="GetCursorPos"/> 的结果直接比）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT_INT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowTextW(IntPtr hWnd, [Out] System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassNameW(IntPtr hWnd, [Out] System.Text.StringBuilder lpClassName, int nMaxCount);

        /// <summary>当前真正持有键盘焦点的顶级窗口（与本进程无关，按 SendInput 的目标理解）。</summary>
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        /// <summary>返回窗口所属线程 id，进程 id 由 out 参数带出（窗口句柄无效时返回 0）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr WindowFromPoint(POINT_INT point);

        /// <summary>
        /// 在指定父窗口的子窗口树中查找包含指定坐标的最深层子窗口。
        /// 与 WindowFromPoint 的关键区别：只在 parentHwnd 的子树中查找，
        /// 不会命中遮罩窗口（因为遮罩窗口不是 parentHwnd 的子窗口），因此不需要 WS_EX_TRANSPARENT。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT pt, uint uFlags);

        /// <summary>CWP_SKIPINVISIBLE：跳过不可见子窗口。</summary>
        public const uint CWP_SKIPINVISIBLE = 0x0001;
        /// <summary>CWP_SKIPTRANSPARENT：跳过透明子窗口。</summary>
        public const uint CWP_SKIPTRANSPARENT = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>GA_ROOT：拿到祖先链的根（顶级窗口）。</summary>
        public const uint GA_ROOT = 2;
        /// <summary>GW_HWNDNEXT：枚举链中的下一个窗口。</summary>
        public const uint GW_HWNDNEXT = 2;

        /// <summary>HWND_TOPMOST：把窗口放入置顶窗口组。</summary>
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        /// <summary>HWND_NOTOPMOST：把窗口移出置顶窗口组（降级为普通顶层窗口）。</summary>
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const int GWL_WNDPROC = -4;
        public const int GWL_EXSTYLE = -20;
        public const int WM_NCHITTEST = 0x0084;

        /// <summary>WM_WINDOWPOSCHANGING：窗口位置/尺寸/Z序即将变化时发送。</summary>
        public const uint WM_WINDOWPOSCHANGING = 0x0046;

        /// <summary>WM_WINDOWPOSCHANGED：窗口位置/尺寸/Z序已变化后发送。</summary>
        public const uint WM_WINDOWPOSCHANGED = 0x0047;

        /// <summary>WM_SYSCOMMAND：系统命令消息（最小化、最大化、关闭等）。</summary>
        public const uint WM_SYSCOMMAND = 0x0112;

        /// <summary>SC_MINIMIZE：最小化命令。wParam 低 4 位为保留位，比较时需掩码 0xFFF0。</summary>
        public const int SC_MINIMIZE = 0xF020;
        public static readonly IntPtr HTTRANSPARENT = new IntPtr(-1);
        public const long WS_EX_TRANSPARENT = 0x00000020L;
        public const long WS_EX_LAYERED = 0x00080000L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;
        public const long WS_EX_TOOLWINDOW = 0x00000080L;
        /// <summary>WS_EX_APPWINDOW：强制为窗口在任务栏上给出按钮。
        /// ⚠ <b>实测不足以单独起作用</b>（2026-09-29 录屏预览卡）：Avalonia 的 <c>ShowInTaskbar=False</c> 除了清掉这一枚，
        /// 还把窗<b>挂到一个隐藏父窗</b>上（见 <c>StickerWindow</c> 里量过的那条注释），只补样式位并不把窗从那个父窗上摘下来，
        /// 实测结果就是"点了、任务栏仍然没有图标"。⇒ 要给一扇 Avalonia 窗放任务栏按钮，改框架侧的 <c>ShowInTaskbar</c>，别补这一枚。</summary>
        public const long WS_EX_APPWINDOW = 0x00040000L;

        // -------------------- 窗口激活与还原 --------------------
        /// <summary>将指定窗口置于前台并激活（获得焦点）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>控制窗口的显示状态（如 SW_RESTORE、SW_SHOW 等）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>判断指定窗口是否处于最小化（图标化）状态。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const long WS_EX_TOPMOST = 0x00000008L;

        /// <summary>SWP_NOSIZE：保持现有大小。</summary>
        public const uint SWP_NOSIZE = 0x0001;
        /// <summary>SWP_NOMOVE：保持现有位置。</summary>
        public const uint SWP_NOMOVE = 0x0002;
        /// <summary>SWP_NOZORDER：保持现有 Z 序。</summary>
        public const uint SWP_NOZORDER = 0x0004;
        /// <summary>SWP_NOACTIVATE：不激活窗口。</summary>
        public const uint SWP_NOACTIVATE = 0x0010;
        /// <summary>SWP_SHOWWINDOW：显示窗口。</summary>
        public const uint SWP_SHOWWINDOW = 0x0040;
        /// <summary>SWP_HIDEWINDOW：隐藏窗口。</summary>
        public const uint SWP_HIDEWINDOW = 0x0080;
        /// <summary>SWP_FRAMECHANGED：强制重新计算窗口框架。</summary>
        public const uint SWP_FRAMECHANGED = 0x0020;

        /// <summary>
        /// WINDOWPOS 结构体：SetWindowPos 参数 / WM_WINDOWPOSCHANGING 消息的 lParam。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        // -------------------- DWM 窗口属性 --------------------

        /// <summary>DWMWA_CLOAKED：窗口是否被 DWM 隐藏（UWP 后台窗口典型场景）。</summary>
        public const int DWMWA_CLOAKED = 14;
        /// <summary>DWMWA_NCRENDERING_POLICY：非客户区渲染策略，可用于关闭 DWM 阴影。</summary>
        public const int DWMWA_NCRENDERING_POLICY = 2;
        /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS：精确窗口外框（绕开 Aero 阴影），物理像素。</summary>
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE：Windows 11 窗口圆角偏好。</summary>
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        public const int DWMNCRP_DISABLED = 1;
        public const int DWMWCP_DONOTROUND = 1;

        [DllImport("dwmapi.dll", PreserveSig = true, EntryPoint = "DwmGetWindowAttribute")]
        public static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll", PreserveSig = true, EntryPoint = "DwmGetWindowAttribute")]
        public static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll", PreserveSig = true, EntryPoint = "DwmSetWindowAttribute")]
        public static extern int DwmSetWindowAttributeInt(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        // -------------------- MSAA / IAccessible --------------------

        /// <summary>OBJID_CLIENT：窗口客户区的可访问对象（最常用）。注意是 uint，最高位置位。</summary>
        public const uint OBJID_CLIENT = 0xFFFFFFFC;

        /// <summary>CHILDID_SELF：表示"对象自己"而非某个子元素。</summary>
        public const int CHILDID_SELF = 0;

        /// <summary>IAccessible 的 IID。<c>AccessibleObjectFromWindow</c> 必填。</summary>
        public static readonly Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

        [DllImport("oleacc.dll", PreserveSig = true)]
        public static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint dwObjectID,
            [In] ref Guid riid,
            out IntPtr ppvObject);

        [DllImport("oleacc.dll", PreserveSig = true)]
        public static extern int AccessibleObjectFromPoint(
            POINT_INT ptScreen,
            [MarshalAs(UnmanagedType.Interface)] out object ppacc,
            [MarshalAs(UnmanagedType.Struct)] out object pvarChild);

        [DllImport("oleacc.dll", PreserveSig = true)]
        public static extern int WindowFromAccessibleObject(
            [In, MarshalAs(UnmanagedType.Interface)] object pacc,
            out IntPtr phwnd);

        // -------------------- 剪贴板 --------------------

        public const uint CF_DIB = 8;
        public const uint CF_UNICODETEXT = 13;
        public const uint GMEM_MOVEABLE = 0x0002;

        /// <summary>句柄型标准格式：剪贴板里存的是 GDI 句柄而非可复制的字节，快照时不能按 GlobalSize 读取。</summary>
        public const uint CF_BITMAP = 2;
        public const uint CF_METAFILEPICT = 3;
        public const uint CF_PALETTE = 9;
        public const uint CF_ENHMETAFILE = 14;
        public const uint CF_OWNERDISPLAY = 0x0080;
        public const uint CF_DIBV5 = 17;

        /// <summary>已注册格式 id 的下界：此值以上由 RegisterClipboardFormat 动态分配，跨会话需按名字重取。</summary>
        public const uint CF_REGISTERED = 0xC000;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint RegisterClipboardFormatW(string lpszFormat);

        /// <summary>把已注册格式 id 反解成格式名（标准格式返回 0）。用于还原时按名字重新注册。</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetClipboardFormatNameW(uint format, [Out] System.Text.StringBuilder lpszFormatName, int cchMaxCount);

        /// <summary>剪贴板内容序号：每次实际变更（含 EmptyClipboard）都会自增。判「有没有复制成功」的唯一可靠依据。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetClipboardSequenceNumber();

        /// <summary>枚举当前剪贴板持有数据的格式。首次传 0 取第一个，之后传上一个返回值；0 表示结束。必须先 OpenClipboard。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern UIntPtr GlobalSize(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr hMem);

        // -------------------- GDI 区域采集 --------------------

        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        public const int DIB_RGB_COLORS = 0;
        public const int LOGPIXELSX = 88;

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GdiFlush();

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        // -------------------- 进程与内存清理 --------------------
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>
        /// 读取整机物理内存真值（总容量与当前空余）。长截图的上限按**当前空余**折算，
        /// 不能用总容量——总容量只说明这台机器理论上有钱，不代表此刻拿得出来。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            /// <summary>调用前必须把 dwLength 填成 sizeof(MEMORYSTATUSEX)，否则 API 直接返回失败。</summary>
            public static MEMORYSTATUSEX Create() => new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        }

        // -------------------- 高精度多媒体时钟与显示模式 --------------------
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = false)]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = false)]
        public static extern uint timeEndPeriod(uint uPeriod);

        public const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        // -------------------- 进程令牌与权限级别 --------------------
        /// <summary>返回当前进程的伪句柄（值为 -1），供 OpenProcessToken 使用，不可 CloseHandle。</summary>
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>打开进程访问令牌，desiredAccess 传 TOKEN_QUERY。</summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processToken, uint desiredAccess, out IntPtr tokenHandle);

        /// <summary>
        /// 打开已存在的进程。只想读令牌信息时传 PROCESS_QUERY_LIMITED_INFORMATION(0x1000)——
        /// 该访问位对受保护的进程也能拿到，且不需要提权。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;

        /// <summary>读取令牌信息，tokenInformationClass 传 TokenElevation(20) 可判断是否已提权。</summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            ref TOKEN_ELEVATION tokenInformation, uint tokenInformationLength, out uint returnLength);

        public const int TokenElevation = 20;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;

        public const int SecurityImpersonation = 2;
        public const int TokenPrimary = 1;

        /// <summary>获取 Windows 桌面 Shell（通常为 explorer.exe）的顶级窗口句柄。</summary>
        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        /// <summary>复制现有的访问令牌，可将模拟令牌转换为主要令牌。</summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        /// <summary>使用指定的主要访问令牌创建新进程及其主线程（管理员向标准用户降权的关键 API）。</summary>
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            string? lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_ELEVATION
        {
            public uint TokenIsElevated;
        }

        // ===== 录屏引擎：Job Object（ffmpeg 子进程防孤，yuSnip 崩溃则子进程必亡）=====

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        /// <summary>informationClass 传 JobObjectExtendedLimitInformation(9)。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        // ================= WTS 会话通知（M4 锁屏自动暂停） =================

        /// <summary>注册窗口接收 WM_WTSSESSION_CHANGE。dwFlags：0=NOTIFY_FOR_THIS_SESSION。</summary>
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        /// <summary>WTSRegisterSessionNotification 的 dwFlags：只关心本会话（锁屏/解锁够用）。</summary>
        public const uint NOTIFY_FOR_THIS_SESSION = 0;
    }
}
