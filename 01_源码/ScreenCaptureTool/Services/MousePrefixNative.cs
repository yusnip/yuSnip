using System;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>引导键录制、补发共享的原生事件及上下文出口。</summary>
internal static class MousePrefixNative
{
    internal readonly record struct Context(nint Foreground, nint Focus, uint Thread, nint Layout);
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeMethods.RECT CaretRect;
    }

    internal static Context ReadContext()
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        GuiInfo gui = new() { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (foreground == 0 || thread == 0 || !GetGUIThreadInfo(thread, ref gui) || gui.Focus == 0 || (gui.Flags & 0x1E) != 0) return default;
        return new(foreground, gui.Focus, thread, GetKeyboardLayout(thread));
    }

    internal static bool CanReplay(Context context)
    {
        if (context == default) return false;
        NativeMethods.GetWindowThreadProcessId(context.Foreground, out uint process);
        uint target = Integrity(process), current = Integrity((uint)Environment.ProcessId);
        return target != 0 && current != 0 && target <= current;
    }

    private static uint Integrity(uint process)
    {
        nint handle = NativeMethods.OpenProcess(0x1000, false, process);
        if (handle == 0) return 0;
        nint token = 0, buffer = 0;
        try
        {
            if (!NativeMethods.OpenProcessToken(handle, 8, out token)) return 0;
            GetTokenInformation(token, 25, 0, 0, out int size);
            if (size <= 0 || size > 65536) return 0;
            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, 25, buffer, size, out _)) return 0;
            nint sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count == 0 ? 0 : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            if (token != 0) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(handle);
        }
    }

    internal static ActionMouseKey Key(NativeMethods.KBDLLHOOKSTRUCT data) => new(data.vkCode, data.scanCode, (data.flags & 1) != 0);
    internal static NativeMethods.INPUT Keyboard(NativeMethods.KBDLLHOOKSTRUCT data, bool up)
    {
        // Pause／Break／PrintScreen 有特殊多字节序列，不能把单个扫描码重放成 NumLock 等其他键。
        bool scan = data.scanCode != 0 && data.vkCode is not (0x13 or 0x03 or 0x2C);
        return new() { type = NativeMethods.INPUT_KEYBOARD, U = new() { ki = new()
        {
            wVk = (ushort)(scan ? 0 : data.vkCode), wScan = (ushort)data.scanCode,
            dwFlags = (scan ? 8u : 0u) | ((data.flags & 1) != 0 ? 1u : 0) | (up ? 2u : 0),
        } } };
    }

    internal static NativeMethods.INPUT Mouse(int message, NativeMethods.MSLLHOOKSTRUCT data)
    {
        // 延迟的按钮／滚轮仍须落在原事件的物理坐标，不能误点补发时的新指针位置。
        uint flags = message switch { 0x201 => 2, 0x202 => 4, 0x204 => 8, 0x205 => 16,
            0x207 => 32, 0x208 => 64, 0x20B => 128, 0x20C => 256, 0x20A => 0x800, 0x20E => 0x1000, _ => 0 };
        var input = AbsoluteMouse(data.pt.X, data.pt.Y);
        input.U.mi.dwFlags |= flags;
        input.U.mi.mouseData = message is 0x20B or 0x20C ? data.mouseData >> 16 : message is 0x20A or 0x20E ? (uint)(int)(short)(data.mouseData >> 16) : 0;
        if (flags == 0) input.U.mi.dwFlags = 0;
        return input;
    }

    private static NativeMethods.INPUT AbsoluteMouse(int x, int y)
    {
        // LL 鼠标点为物理像素；度量不能受调用线程的 DPI 虚拟化影响。
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = Math.Max(1, GetSystemMetrics(78)), height = Math.Max(1, GetSystemMetrics(79));
        return new() { U = new() { mi = new() { dx = (int)Math.Clamp(((long)x - left) * 65536 / width + 65536 / (width * 2L), 0, 65535),
            dy = (int)Math.Clamp(((long)y - top) * 65536 / height + 65536 / (height * 2L), 0, 65535), dwFlags = 0xC001 } } };
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }

    internal static uint SendRestoringPointer(NativeMethods.INPUT[] inputs)
    {
        bool mouse = Array.Exists(inputs, input => input.type == 0);
        if (!mouse || !GetPhysicalCursorPos(out var current)) return Send(inputs);
        var batch = new NativeMethods.INPUT[inputs.Length + 1]; inputs.CopyTo(batch, 0);
        // 只补发离散事件；最后恢复入队期间真实指针已经到达的位置，不冒充额外确认。
        batch[^1] = AbsoluteMouse(current.X, current.Y);
        uint sent = Send(batch);
        return Math.Min(sent, (uint)inputs.Length);
    }

    internal static uint Send(NativeMethods.INPUT[] inputs) => inputs.Length == 0 ? 0
        : NativeMethods.SendInput((uint)inputs.Length, ref inputs[0], Marshal.SizeOf<NativeMethods.INPUT>());

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalCursorPos(out NativeMethods.POINT point);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int kind, nint buffer, int length, out int size);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
}
