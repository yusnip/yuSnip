using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>只补充普通辅助键的物理状态；不屏蔽传统输入，不执行窗口或脚本操作。</summary>
internal sealed class SharedKeyboardInput : IDisposable
{
    private readonly nint _window;
    private readonly Action<uint, bool> _observe;
    private bool _registered;
    private bool _draining;
    internal readonly HashSet<uint> Held = [];
    internal readonly HashSet<uint> Handled = [];
    internal readonly HashSet<uint> SeenByHook = [];
    internal readonly Dictionary<uint, (ActionDefinition Action, MousePrefixNative.Context Context)> Pending = [];

    internal SharedKeyboardInput(nint window, Action<uint, bool> observe)
    { _window = window; _observe = observe; }

    internal void Configure(bool enabled)
    {
        if (_registered == enabled) return;
        var device = new Device { UsagePage = 1, Usage = 6, Flags = enabled ? 0x100u : 1u, Target = enabled ? _window : 0 };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<Device>()))
        {
            AppLogger.Warn("共享键盘物理监听" + (enabled ? "注册" : "移除") + "失败，错误码：" + Marshal.GetLastWin32Error());
            return;
        }
        _registered = enabled;
        Held.Clear(); Pending.Clear(); Handled.Clear(); SeenByHook.Clear();
    }

    internal uint Modifiers
    {
        get { uint value = 0; foreach (uint key in Held) value |= ActionKeyboardBinding.ModifierOf(key); return value; }
    }

    internal void Read(nint input)
    {
        if (!_registered) return;
        uint size = 0, header = (uint)Marshal.SizeOf<Header>();
        if (GetRawInputData(input, 0x10000003, 0, ref size, header) == uint.MaxValue || size < header + 16 || size > 4096) return;
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(input, 0x10000003, buffer, ref size, header) != size) return;
            var source = Marshal.PtrToStructure<Header>(buffer);
            // SendInput 也可生成 Device=0 的 Raw Input，不能把模拟／恢复流当成物理按住状态。
            if (source.Type != 1 || source.Device == 0) return;
            var key = Marshal.PtrToStructure<Keyboard>(buffer + (int)header);
            uint vk = key.VirtualKey;
            // Raw Input 的左右修饰键共用 VK，使用扫描码和 E0 位还原身份。
            if (vk == 0x10) vk = key.ScanCode == 0x36 ? 0xA1u : 0xA0u;
            if (vk == 0x11) vk = (key.Flags & 2) != 0 ? 0xA3u : 0xA2u;
            if (vk == 0x12) vk = (key.Flags & 2) != 0 ? 0xA5u : 0xA4u;
            _observe(vk, (key.Flags & 1) != 0);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>钩子先于队列消息到达时，只补读截至当前事件的原始输入，不等待未来事件。</summary>
    internal void Drain(uint throughTime)
    {
        if (!_registered || _draining) return;
        _draining = true;
        try
        {
            for (int i = 0; i < 32 && PeekMessageW(out var next, _window, 0xFF, 0xFF, 0); i++)
            {
                if (unchecked((int)(next.time - throughTime)) > 0) break;
                if (!PeekMessageW(out next, _window, 0xFF, 0xFF, 1)) break;
                Read(next.lParam);
                NativeMethods.DefWindowProcW(next.hwnd, next.message, next.wParam, next.lParam);
            }
        }
        finally { _draining = false; }
    }

    public void Dispose() => Configure(false);
    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public nint Device, Parameter; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort ScanCode, Flags, Reserved, VirtualKey; public uint Message, Extra; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(ref Device devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint input, uint command, nint buffer, ref uint size, uint header);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMethods.MSG message, nint window, uint first, uint last, uint remove);
}
