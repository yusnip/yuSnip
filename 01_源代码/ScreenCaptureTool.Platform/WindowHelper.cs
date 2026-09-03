using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// 顶级窗口的查询/几何/枚举助手。
    /// 阶段 4 与 T3.3/T3.4 共用，DWM 相关也合并在此（无需独立 DwmHelper）。
    /// </summary>
    public static class WindowHelper
    {
        private sealed class HitTestTransparentSubclass
        {
            public required IntPtr PreviousWndProc { get; init; }
            public required NativeMethods.WndProcDelegate WndProc { get; init; }
        }

        private static readonly ConcurrentDictionary<IntPtr, HitTestTransparentSubclass> HitTestTransparentSubclasses = new();
        private static readonly object StyleLock = new();

        /// <summary>
        /// 屏幕物理像素点处的"顶级窗口"句柄。返回 <see cref="IntPtr.Zero"/> 表示无窗口或失败。
        /// 内部用 <c>WindowFromPoint</c> 拿到深层窗口后，再用 <c>GetAncestor(GA_ROOT)</c> 升到顶级。
        /// </summary>
        public static IntPtr GetTopLevelWindowFromPoint(Point screenPoint)
        {
            var pt = new NativeMethods.POINT_INT(screenPoint.X, screenPoint.Y);
            IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root != IntPtr.Zero ? root : hwnd;
        }

        /// <summary>
        /// 屏幕物理像素点处的原始窗口句柄；查询期间临时让 excludedHwnd 命中穿透。
        /// </summary>
        public static IntPtr GetWindowFromPointIgnoringWindow(Point screenPoint, IntPtr excludedHwnd)
        {
            if (excludedHwnd == IntPtr.Zero)
            {
                return NativeMethods.WindowFromPoint(new NativeMethods.POINT_INT(screenPoint.X, screenPoint.Y));
            }

            lock (StyleLock)
            {
                IntPtr oldStyle = IntPtr.Zero;
                bool changed = false;
                try
                {
                    oldStyle = NativeMethods.GetWindowLongPtr(excludedHwnd, NativeMethods.GWL_EXSTYLE);
                    long style = oldStyle.ToInt64();
                    long next = style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
                    if (next != style)
                    {
                        NativeMethods.SetWindowLongPtr(excludedHwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(next));
                        changed = true;
                    }

                    return NativeMethods.WindowFromPoint(new NativeMethods.POINT_INT(screenPoint.X, screenPoint.Y));
                }
                catch
                {
                    return IntPtr.Zero;
                }
                finally
                {
                    if (changed)
                    {
                        try { NativeMethods.SetWindowLongPtr(excludedHwnd, NativeMethods.GWL_EXSTYLE, oldStyle); }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// 临时让 excludedHwnd 命中穿透并执行一段操作。
        /// </summary>
        public static T InvokeIgnoringWindow<T>(IntPtr excludedHwnd, Func<T> action)
        {
            if (excludedHwnd == IntPtr.Zero)
            {
                return action();
            }

            lock (StyleLock)
            {
                IntPtr oldStyle = IntPtr.Zero;
                bool changed = false;
                try
                {
                    oldStyle = NativeMethods.GetWindowLongPtr(excludedHwnd, NativeMethods.GWL_EXSTYLE);
                    long style = oldStyle.ToInt64();
                    long next = style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
                    if (next != style)
                    {
                        NativeMethods.SetWindowLongPtr(excludedHwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(next));
                        changed = true;
                    }

                    return action();
                }
                finally
                {
                    if (changed)
                    {
                        try { NativeMethods.SetWindowLongPtr(excludedHwnd, NativeMethods.GWL_EXSTYLE, oldStyle); }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// 屏幕物理像素点处的"顶级窗口"句柄；查询期间临时让 excludedHwnd 命中穿透，复刻旧版自动检测能穿过截图窗口命中底层窗口的行为。
        /// </summary>
        public static IntPtr GetTopLevelWindowFromPointIgnoringWindow(Point screenPoint, IntPtr excludedHwnd)
        {
            IntPtr hwnd = GetWindowFromPointIgnoringWindow(screenPoint, excludedHwnd);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root != IntPtr.Zero ? root : hwnd;
        }

        /// <summary>
        /// 窗口外框（屏幕物理像素）。优先使用 DWM 的"延展框"
        /// （绕开 Aero 阴影、与用户视觉一致），fallback 到 <c>GetWindowRect</c>。
        ///
        /// 失败返回 <see cref="Rectangle.Empty"/>。调用方应通过
        /// <see cref="Rectangle.IsEmpty"/> 判断有效性。
        /// </summary>
        public static Rectangle GetWindowBounds(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Rectangle.Empty;

            // 1) DWM 延展框（推荐路径）
            int hr = NativeMethods.DwmGetWindowAttributeRect(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT dwmRect,
                Marshal.SizeOf<NativeMethods.RECT>());

            if (hr == 0 && dwmRect.Width > 0 && dwmRect.Height > 0)
            {
                return new Rectangle(dwmRect.Left, dwmRect.Top, dwmRect.Width, dwmRect.Height);
            }

            // 2) Fallback：GetWindowRect（含 Aero 阴影）
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r) &&
                r.Width > 0 && r.Height > 0)
            {
                return new Rectangle(r.Left, r.Top, r.Width, r.Height);
            }

            return Rectangle.Empty;
        }

        /// <summary>
        /// 窗口是否被 DWM "cloaked"（UWP 后台、AppFrame 二次窗口的典型现象）。
        /// 失败时保守返回 false（视为可见，避免误过滤）。
        /// </summary>
        public static bool IsWindowCloaked(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            int hr = NativeMethods.DwmGetWindowAttributeInt(
                hwnd,
                NativeMethods.DWMWA_CLOAKED,
                out int cloaked,
                sizeof(int));

            return hr == 0 && cloaked != 0;
        }

        /// <summary>窗口标题（最多 256 字符，足够日志/调试用）。失败返回空串。</summary>
        public static string GetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            int len = NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        /// <summary>窗口类名。失败返回空串。</summary>
        public static string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            int len = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        /// <summary>
        /// 把窗口放入 Topmost Z 序组，但不移动、不改变大小、不抢焦点。
        /// </summary>
        public static bool ForceTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            uint flags = NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE
                | NativeMethods.SWP_SHOWWINDOW;

            bool ok = NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                flags);

            return ok;
        }

        /// <summary>设置工具窗口样式：不抢焦点，可选鼠标穿透。</summary>
        public static void MakeToolWindowNoActivate(IntPtr hwnd, bool transparent)
        {
            if (hwnd == IntPtr.Zero) return;

            IntPtr oldStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            long style = oldStyle.ToInt64() | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            if (transparent)
            {
                style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
            }

            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
            ForceTopmost(hwnd);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE
                    | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_FRAMECHANGED);
        }

        /// <summary>让窗口在 Win32 命中测试阶段直接穿透，避免透明 Avalonia 窗口吞掉鼠标/滚轮。</summary>
        public static bool MakeHitTestTransparent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (HitTestTransparentSubclasses.ContainsKey(hwnd)) return true;

            try
            {
                NativeMethods.WndProcDelegate? proc = null;
                proc = (hWnd, msg, wParam, lParam) =>
                {
                    if (msg == NativeMethods.WM_NCHITTEST)
                    {
                        return NativeMethods.HTTRANSPARENT;
                    }

                    if (HitTestTransparentSubclasses.TryGetValue(hWnd, out HitTestTransparentSubclass? state))
                    {
                        return NativeMethods.CallWindowProc(state.PreviousWndProc, hWnd, msg, wParam, lParam);
                    }

                    return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
                };

                IntPtr previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
                if (previous == IntPtr.Zero) return false;

                HitTestTransparentSubclasses[hwnd] = new HitTestTransparentSubclass
                {
                    PreviousWndProc = previous,
                    WndProc = proc,
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>关闭 DWM 给无边框小工具窗口附加的非客户区阴影和 Windows 11 圆角。</summary>
        public static bool DisableWindowChromeEffects(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                int policy = NativeMethods.DWMNCRP_DISABLED;
                int shadowHr = NativeMethods.DwmSetWindowAttributeInt(
                    hwnd,
                    NativeMethods.DWMWA_NCRENDERING_POLICY,
                    ref policy,
                    sizeof(int));

                int cornerPreference = NativeMethods.DWMWCP_DONOTROUND;
                int cornerHr = NativeMethods.DwmSetWindowAttributeInt(
                    hwnd,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref cornerPreference,
                    sizeof(int));

                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE
                        | NativeMethods.SWP_NOSIZE
                        | NativeMethods.SWP_NOACTIVATE
                        | NativeMethods.SWP_FRAMECHANGED);

                return shadowHr == 0 || cornerHr == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 枚举所有顶级窗口（已过滤：不可见、cloaked、应用挂起的全部跳过）。
        /// 阶段 4 暂未使用，留给将来"手动选窗"功能。Z 序与 EnumWindows 一致（前到后）。
        /// </summary>
        public static IEnumerable<IntPtr> EnumerateVisibleTopLevelWindows()
        {
            var list = new List<IntPtr>();

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    if (IsWindowCloaked(hwnd)) return true;
                    if (NativeMethods.IsHungAppWindow(hwnd)) return true;
                    list.Add(hwnd);
                }
                catch
                {
                    // 单个窗口枚举失败不能中断整体
                }
                return true;
            }, IntPtr.Zero);

            return list;
        }
    }
}
