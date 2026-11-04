using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// 显示器/虚拟屏几何信息查询。物理像素，多显示器场景下涵盖所有屏幕。
    ///
    /// 阶段 3 衍生工具类（T3.7 DPI 相关将来也归这里）。本类只读，没有可变状态。
    /// </summary>
    public static class MonitorHelper
    {
        /// <summary>
        /// 整虚拟屏幕的矩形（物理像素）。
        /// 多显示器时，左/上边界可能为负（如副屏放在主屏左侧）。
        ///
        /// 失败时（极少数：会话锁屏初期等）退化为 (0,0,1920,1080) 占位，
        /// 调用方应该把 <see cref="Rectangle.IsEmpty"/> 视作有效但兜底。
        /// </summary>
        public static Rectangle GetVirtualScreenRect()
        {
            int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            // 极端兜底：GetSystemMetrics 在某些状态下可能返回 0
            if (w <= 0 || h <= 0)
            {
                return new Rectangle(0, 0, 1920, 1080);
            }

            return new Rectangle(x, y, w, h);
        }

        /// <summary>枚举所有显示器矩形（物理像素）。失败时退化为虚拟屏幕矩形。</summary>
        public static IReadOnlyList<Rectangle> GetMonitorRects()
        {
            var monitors = new List<Rectangle>();
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT monitorRect, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                    };

                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info) && info.rcMonitor.Width > 0 && info.rcMonitor.Height > 0)
                    {
                        monitors.Add(new Rectangle(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height));
                    }
                    else if (monitorRect.Width > 0 && monitorRect.Height > 0)
                    {
                        monitors.Add(new Rectangle(monitorRect.Left, monitorRect.Top, monitorRect.Width, monitorRect.Height));
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                monitors.Clear();
            }

            if (monitors.Count == 0)
            {
                monitors.Add(GetVirtualScreenRect());
            }

            return monitors;
        }

        /// <summary>
        /// 所有显示器"工作区"（rcWork，已扣除任务栏等 Appbar）的并集包围盒（物理像素）。
        /// 用于把贴图信封窗口钳制在任务栏之外——Topmost 巨幅窗口一旦盖住任务栏，
        /// Shell 会反复抬升/压回任务栏（z 序争抢＝闪烁）。失败退化为整虚拟屏幕。
        /// </summary>
        public static Rectangle GetWorkAreaUnion()
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT monitorRect, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                    };
                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info) && info.rcWork.Width > 0 && info.rcWork.Height > 0)
                    {
                        l = Math.Min(l, info.rcWork.Left);
                        t = Math.Min(t, info.rcWork.Top);
                        r = Math.Max(r, info.rcWork.Right);
                        b = Math.Max(b, info.rcWork.Bottom);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // 枚举失败走兜底
            }

            if (l == int.MaxValue || r <= l || b <= t)
            {
                return GetVirtualScreenRect();
            }
            return new Rectangle(l, t, r - l, b - t);
        }

        /// <summary>
        /// 获取所有显示器的工作区矩形列表（物理像素，已扣除任务栏等 Appbar）。
        /// </summary>
        public static IReadOnlyList<Rectangle> GetWorkAreaRects()
        {
            var list = new List<Rectangle>();
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT monitorRect, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                    };
                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info) && info.rcWork.Width > 0 && info.rcWork.Height > 0)
                    {
                        list.Add(new Rectangle(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height));
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                list.Clear();
            }

            if (list.Count == 0)
            {
                list.Add(GetVirtualScreenRect());
            }

            return list;
        }

        /// <summary>判断区域是否完整落在同一块显示器内。用于长截图避免跨屏坐标/DPI 风险。</summary>
        public static bool IsRegionOnSingleMonitor(Rectangle region)
        {
            if (region.Width <= 0 || region.Height <= 0) return false;
            foreach (Rectangle monitor in GetMonitorRects())
            {
                if (monitor.Contains(region)) return true;
            }

            return false;
        }

        /// <summary>
        /// 当前鼠标光标所在的显示器矩形（物理像素）。光标不在任何显示器内时
        /// （理论上极少发生）退化为整虚拟屏幕矩形。
        /// </summary>
        public static Rectangle GetMonitorAtCursor()
        {
            try
            {
                if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
                {
                    var point = new Point(pt.X, pt.Y);
                    foreach (Rectangle monitor in GetMonitorRects())
                    {
                        if (monitor.Contains(point)) return monitor;
                    }
                }
            }
            catch
            {
                // 取光标失败，走兜底
            }
            return GetVirtualScreenRect();
        }

        /// <summary>
        /// 主显示器矩形（物理像素）。无法识别时退化为整虚拟屏幕矩形。
        /// </summary>
        public static Rectangle GetPrimaryMonitor()
        {
            var list = GetMonitorsWithFlags();
            foreach (var item in list)
            {
                if ((item.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
                {
                    return item.Rect;
                }
            }
            // 没找到主屏标志，取第一个或虚拟屏幕
            return list.Count > 0 ? list[0].Rect : GetVirtualScreenRect();
        }

        private readonly struct MonitorEntry
        {
            public readonly Rectangle Rect;
            public readonly uint Flags;
            public MonitorEntry(Rectangle rect, uint flags) { Rect = rect; Flags = flags; }
        }

        /// <summary>枚举所有显示器及其标志（含 MONITORINFOF_PRIMARY）。失败退化为整虚拟屏。</summary>
        private static IReadOnlyList<MonitorEntry> GetMonitorsWithFlags()
        {
            var monitors = new List<MonitorEntry>();
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT monitorRect, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                    };

                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info) && info.rcMonitor.Width > 0 && info.rcMonitor.Height > 0)
                    {
                        monitors.Add(new MonitorEntry(
                            new Rectangle(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height),
                            info.dwFlags));
                    }
                    else if (monitorRect.Width > 0 && monitorRect.Height > 0)
                    {
                        monitors.Add(new MonitorEntry(
                            new Rectangle(monitorRect.Left, monitorRect.Top, monitorRect.Width, monitorRect.Height), 0u));
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                monitors.Clear();
            }

            if (monitors.Count == 0)
            {
                monitors.Add(new MonitorEntry(GetVirtualScreenRect(), 0u));
            }

            return monitors;
        }

        /// <summary>
        /// 覆盖指定物理坐标点的显示器信息（矩形=该屏虚拟屏绝对坐标、设备名如 \\.\DISPLAY1、是否主屏）。
        /// 录屏 DDA 路径据设备名解析 ddagrab output_idx。找不到返回 null。
        /// </summary>
        public static (Rectangle rect, string device, bool primary)? GetMonitorInfoAtPoint(int x, int y)
        {
            try
            {
                (Rectangle rect, string device, bool primary)? hit = null;
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT r, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                    {
                        var rc = new Rectangle(info.rcMonitor.Left, info.rcMonitor.Top,
                            info.rcMonitor.Width, info.rcMonitor.Height);
                        if (rc.Contains(x, y))
                        {
                            bool pri = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                            hit = (rc, info.szDevice ?? "", pri);
                            return false; // 命中即停
                        }
                    }
                    return true;
                }, IntPtr.Zero);
                return hit;
            }
            catch { return null; }
        }

        /// <summary>
        /// 与 <paramref name="region"/> 重叠面积最大的那块显示器的整屏矩形与工作区（rcWork，扣掉任务栏）。
        /// 录屏预览窗 / 控制条按"选区所在那块屏"摆位用；都不相交时退回光标所在屏。
        /// </summary>
        public static (Rectangle monitor, Rectangle work) GetMonitorAndWorkAreaForRegion(Rectangle region)
        {
            Rectangle bestMon = Rectangle.Empty, bestWork = Rectangle.Empty;
            long bestArea = -1;
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT r, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info) && info.rcMonitor.Width > 0 && info.rcMonitor.Height > 0)
                    {
                        var mon = new Rectangle(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height);
                        var work = info.rcWork.Width > 0 && info.rcWork.Height > 0
                            ? new Rectangle(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height)
                            : mon;
                        var inter = Rectangle.Intersect(mon, region);
                        long area = inter.IsEmpty ? 0 : (long)inter.Width * inter.Height;
                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestMon = mon;
                            bestWork = work;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                bestArea = -1;
            }

            if (bestArea <= 0)
            {
                Rectangle mon = GetMonitorAtCursor();
                Rectangle work = mon;
                foreach (var w in GetWorkAreaRects())
                {
                    if (mon.IntersectsWith(w)) { work = Rectangle.Intersect(mon, w); break; }
                }
                return (mon, work);
            }
            return (bestMon, bestWork);
        }

        /// <summary>
        /// 获取当前所有活动显示器中的最高刷新率（Hz，例如 60、120、144、165、240、360）。
        /// 若获取失败或数值异常，默认兜底返回 60 Hz。
        /// </summary>
        public static int GetMaxDisplayFrequency()
        {
            int maxHz = 60;
            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT monitorRect, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                    };

                    if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                    {
                        var devMode = new NativeMethods.DEVMODEW
                        {
                            dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODEW>()
                        };

                        if (NativeMethods.EnumDisplaySettingsW(info.szDevice, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
                        {
                            if (devMode.dmDisplayFrequency > maxHz && devMode.dmDisplayFrequency <= 500)
                            {
                                maxHz = devMode.dmDisplayFrequency;
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // 异常兜底 60
            }

            return maxHz;
        }
    }
}
