using System;
using System.Collections.Generic;
using System.Drawing;

namespace ScreenCaptureTool.Platform;

/// <summary>
/// Win32 命中测试辅助。集中封装 WindowFromPoint / 顶级窗口过滤等逻辑。
/// </summary>
public static class HitTestHelper
{
    /// <summary>
    /// 返回屏幕物理像素点处的原始窗口句柄。失败返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IntPtr GetWindowFromPoint(Point screenPoint)
    {
        try
        {
            return NativeMethods.WindowFromPoint(new NativeMethods.POINT_INT(screenPoint.X, screenPoint.Y));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 返回屏幕物理像素点处的顶级窗口句柄。失败返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IntPtr GetTopLevelWindowFromPoint(Point screenPoint)
    {
        try
        {
            return WindowHelper.GetTopLevelWindowFromPoint(screenPoint);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 返回屏幕点处可用于自动检测的可见顶级窗口，自动过滤 excluded、不可见、cloaked、hung 窗口。
    /// </summary>
    public static IntPtr GetVisibleTopLevelWindowFromPoint(Point screenPoint, ISet<IntPtr>? excludedHwnds = null)
    {
        IntPtr hwnd = GetTopLevelWindowFromPoint(screenPoint);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        if (excludedHwnds != null && excludedHwnds.Contains(hwnd)) return IntPtr.Zero;

        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return IntPtr.Zero;
            if (WindowHelper.IsWindowCloaked(hwnd)) return IntPtr.Zero;
            if (NativeMethods.IsHungAppWindow(hwnd)) return IntPtr.Zero;
            return hwnd;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 判断屏幕物理像素点是否落在窗口外框内。
    /// </summary>
    public static bool IsPointInsideWindow(IntPtr hwnd, Point screenPoint)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            Rectangle bounds = WindowHelper.GetWindowBounds(hwnd);
            return !bounds.IsEmpty && bounds.Contains(screenPoint);
        }
        catch
        {
            return false;
        }
    }
}
