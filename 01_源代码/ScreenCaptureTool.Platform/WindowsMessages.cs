using System;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// Win32 消息常量。集中存放避免分散硬编码。
    /// </summary>
    public static class WindowsMessages
    {
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE   = 0x0010;
        public const uint WM_QUIT    = 0x0012;
        public const uint WM_HOTKEY  = 0x0312;
        public const uint WM_USER    = 0x0400;

        /// <summary>WS_EX_NOACTIVATE：消息窗口不抢焦点。</summary>
        public const uint WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>HWND_MESSAGE：把窗口创建为 message-only window。</summary>
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
    }
}
