using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>明确录制期间拦截原生键，Esc／Tab／Enter／系统键均可选；松开后交付结果。</summary>
internal sealed class MousePrefixRecorder : IDisposable
{
    private static readonly HashSet<MousePrefixRecorder> Draining = [];
    private readonly MessageOnlyWindow _pump = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly HashSet<uint> _owned = [], _initiallyHeld = [];
    private readonly nint _window;
    private readonly Action<ActionMouseKey> _completed;
    private nint _hook;
    private ActionMouseKey _selected;
    private bool _active = true, _finished;
    private long _cancelledAt;
    private readonly nuint _testInputTag;

    internal MousePrefixRecorder(nint window, Action<ActionMouseKey> completed, nuint testInputTag = 0)
    {
        _window = window; _completed = completed; _proc = Callback; _testInputTag = testInputTag;
        _pump.MessageReceived += message =>
        {
            if (message.Message != 0x113) return;
            if (_active && NativeMethods.GetForegroundWindow() != _window) Cancel();
            if (!_active && _owned.Count != 0 && Stopwatch.GetElapsedTime(_cancelledAt).TotalSeconds > 3) Finish(default);
        };
        try
        {
            _pump.InvokeOnPumpThread(() =>
            {
                for (uint vk = 8; vk < 255; vk++)
                    if ((NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0) _initiallyHeld.Add(vk);
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
                if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (MousePrefixNative.SetTimer(_pump.Handle, 83, 100, 0) == 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return true;
            });
        }
        catch { if (_hook != 0) NativeMethods.UnhookWindowsHookEx(_hook); _hook = 0; _pump.Dispose(); throw; }
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (_testInputTag != 0 && (nuint)data.dwExtraInfo != _testInputTag) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        if ((data.flags & 0x10) != 0 && (_testInputTag == 0 || (nuint)data.dwExtraInfo != _testInputTag)) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        bool up = (int)wParam is 0x101 or 0x105;
        if (_initiallyHeld.Contains(data.vkCode))
        { if (up) _initiallyHeld.Remove(data.vkCode); return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam); }
        if (_active && NativeMethods.GetForegroundWindow() != _window) Cancel();
        if (_owned.Contains(data.vkCode))
        {
            if (up)
            {
                _owned.Remove(data.vkCode);
                if (_owned.Count == 0) Finish(_active ? _selected : default);
            }
            return 1;
        }
        if (_active && !up && MousePrefixNative.Key(data) is var key && key.IsValid)
        {
            if (_selected == default) _selected = key;
            _owned.Add(data.vkCode); return 1;
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Cancel()
    {
        if (!_active) return;
        _active = false; _cancelledAt = Stopwatch.GetTimestamp();
        if (_owned.Count == 0) Finish(default);
        else lock (Draining) Draining.Add(this);
    }

    private void Finish(ActionMouseKey key)
    {
        if (_finished) return;
        _finished = true;
        if (_hook != 0) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
        MousePrefixNative.KillTimer(_pump.Handle, 83);
        lock (Draining) Draining.Remove(this);
        _completed(key); _pump.Dispose();
    }

    public void Dispose()
    {
        if (_finished) return;
        _pump.InvokeOnPumpThread(() => { Cancel(); return true; });
    }
}
