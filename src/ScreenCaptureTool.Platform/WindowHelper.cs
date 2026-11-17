using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

        // -------------------- 防最小化子类化 --------------------

        private sealed class AntiMinimizeSubclass
        {
            public required IntPtr PreviousWndProc { get; init; }
            public required NativeMethods.WndProcDelegate WndProc { get; init; }
        }

        private static readonly ConcurrentDictionary<IntPtr, AntiMinimizeSubclass> AntiMinimizeSubclasses = new();

        /// <summary>
        /// 拦截系统最小化命令（Win+D、Win+M、Shell minimize all 等），
        /// 将其转换为自定义回调（通常用于 Hide 而非 Minimize）。
        /// <para>原理：子类化窗口 WndProc，截获 WM_SYSCOMMAND + SC_MINIMIZE 并吞掉。</para>
        /// </summary>
        /// <param name="hwnd">目标窗口句柄。</param>
        /// <param name="onMinimizeAttempt">系统尝试最小化时的回调（在原始线程/UI线程上自行处理）。</param>
        public static void PreventMinimize(IntPtr hwnd, Action? onMinimizeAttempt = null)
        {
            if (hwnd == IntPtr.Zero) return;
            if (AntiMinimizeSubclasses.ContainsKey(hwnd)) return;

            try
            {
                NativeMethods.WndProcDelegate? proc = null;
                proc = (hWnd, msg, wParam, lParam) =>
                {
                    // 拦截 WM_SYSCOMMAND / SC_MINIMIZE
                    if (msg == NativeMethods.WM_SYSCOMMAND)
                    {
                        long command = wParam.ToInt64() & 0xFFF0;
                        if (command == NativeMethods.SC_MINIMIZE)
                        {
                            try { onMinimizeAttempt?.Invoke(); } catch { }
                            return IntPtr.Zero; // 吞掉最小化命令
                        }
                    }

                    if (AntiMinimizeSubclasses.TryGetValue(hWnd, out var state))
                    {
                        return NativeMethods.CallWindowProc(state.PreviousWndProc, hWnd, msg, wParam, lParam);
                    }

                    return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
                };

                IntPtr previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
                if (previous == IntPtr.Zero) return;

                AntiMinimizeSubclasses[hwnd] = new AntiMinimizeSubclass
                {
                    PreviousWndProc = previous,
                    WndProc = proc,
                };
            }
            catch
            {
            }
        }

        /// <summary>移除防最小化钩子（窗口关闭时调用，释放委托引用防止 GC 失效）。</summary>
        public static void RemoveMinimizePrevention(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            AntiMinimizeSubclasses.TryRemove(hwnd, out _);
        }

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
        /// 在指定父窗口的子窗口树中查找包含指定屏幕坐标的最深层子窗口句柄。
        /// 循环下钻：<c>ChildWindowFromPointEx</c> 只查直接子窗口（MSDN：Grandchildren and deeper
        /// descendants are not searched），因此从 parentHwnd 逐层下钻直到不再命中更深的子窗口。
        /// 不需要 <c>WS_EX_TRANSPARENT</c>（因为遮罩窗口不是 parentHwnd 的子窗口）。
        /// 返回的最深层子窗口可用于 UIA <c>ElementFromHandle</c> 和 MSAA <c>accHitTest</c>。
        /// </summary>
        /// <param name="parentHwnd">顶级窗口句柄（由窗口快照提供）。</param>
        /// <param name="screenPoint">屏幕物理像素坐标。</param>
        /// <returns>最深层子窗口句柄；若无子窗口则返回 parentHwnd 本身。</returns>
        public static IntPtr GetChildWindowFromPoint(IntPtr parentHwnd, Point screenPoint)
        {
            if (parentHwnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr current = parentHwnd;
            // 防御性上限：Windows HWND 嵌套实际不会超过 30 层
            for (int depth = 0; depth < 32; depth++)
            {
                // 屏幕坐标 → 当前层客户区坐标
                System.Drawing.Point pt = new System.Drawing.Point(screenPoint.X, screenPoint.Y);
                if (!NativeMethods.ScreenToClient(current, ref pt))
                    return current;

                NativeMethods.POINT clientPt = new NativeMethods.POINT { X = pt.X, Y = pt.Y };
                IntPtr child = NativeMethods.ChildWindowFromPointEx(
                    current, clientPt,
                    NativeMethods.CWP_SKIPINVISIBLE | NativeMethods.CWP_SKIPTRANSPARENT);

                // 没有更深的子窗口命中（返回自身）或失败 → 已到最深层
                if (child == IntPtr.Zero || child == current)
                    return current;

                current = child;
            }

            return current;
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
        /// 把窗口放入 Topmost Z 序组，但不移动、不改变大小、不抢焦点、<b>也不显示</b>。
        /// ⚠ 这里禁止带 <c>SWP_SHOWWINDOW</c>：置顶守卫的 1 秒定时器会对一个可能已被系统回收复用的
        /// 句柄反复断言，带上它就等于把某张<b>已隐藏</b>的窗口凭空弹回屏幕最上层（并可顺带激活抢焦点）。
        /// </summary>
        public static bool ForceTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            uint flags = NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE;

            bool ok = NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                flags);

            return ok;
        }

        // -------------------- 持久置顶 --------------------

        private sealed class TopmostGuard
        {
            public required IntPtr Hwnd { get; init; }
            public required NativeMethods.WndProcDelegate WndProc { get; init; }
            public required IntPtr PreviousWndProc { get; init; }
            public required Timer RefreshTimer { get; init; }
        }

        private static readonly ConcurrentDictionary<IntPtr, TopmostGuard> TopmostGuards = new();

        /// <summary>
        /// 启用持久置顶：子类化 WndProc 拦截 WM_WINDOWPOSCHANGING，
        /// 防止其他窗口把本窗口从 Topmost Z 序组中挤下去；
        /// 同时用 1 秒间隔的定时器作为后备，周期性重新断言 HWND_TOPMOST。
        /// </summary>
        public static void EnforceTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (TopmostGuards.ContainsKey(hwnd)) return;

            // 立即断言一次
            ForceTopmost(hwnd);

            try
            {
                NativeMethods.WndProcDelegate? proc = null;
                proc = (hWnd, msg, wParam, lParam) =>
                {
                    // 拦截 WM_WINDOWPOSCHANGING：如果 Z 序正在被改变（非 SWP_NOZORDER），
                    // 强制把 hwndInsertAfter 改为 HWND_TOPMOST
                    if (msg == NativeMethods.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
                    {
                        try
                        {
                            var wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                            if ((wp.flags & NativeMethods.SWP_NOZORDER) == 0)
                            {
                                wp.hwndInsertAfter = NativeMethods.HWND_TOPMOST;
                                Marshal.StructureToPtr(wp, lParam, fDeleteOld: false);
                            }
                        }
                        catch { }
                    }

                    if (TopmostGuards.TryGetValue(hWnd, out var guard))
                    {
                        return NativeMethods.CallWindowProc(guard.PreviousWndProc, hWnd, msg, wParam, lParam);
                    }

                    return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
                };

                IntPtr previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
                if (previous == IntPtr.Zero) return;

                // 1 秒间隔定时器：周期性重新断言 Topmost，应对子类化未覆盖的场景。
                // 幂等检查：窗口仍在 TOPMOST 组时跳过 SetWindowPos——每次无差别的
                // Z 序重排都会触发 WM_WINDOWPOSCHANGED，打断 Avalonia 的 Tooltip
                // 弹层（表现为悬浮提示刚出现就被关闭/压没）。
                var timer = new Timer(_ =>
                {
                    try
                    {
                        IntPtr exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
                        if ((exStyle.ToInt64() & NativeMethods.WS_EX_TOPMOST) != 0) return;
                        ForceTopmost(hwnd);
                    }
                    catch { }
                }, null, 1000, 1000);

                TopmostGuards[hwnd] = new TopmostGuard
                {
                    Hwnd = hwnd,
                    WndProc = proc,
                    PreviousWndProc = previous,
                    RefreshTimer = timer,
                };
            }
            catch { }
        }

        /// <summary>
        /// 停止持久置顶：还原原始 WndProc，停止定时器。
        /// 应在窗口关闭时调用。
        /// </summary>
        public static void StopEnforceTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (TopmostGuards.TryRemove(hwnd, out var guard))
            {
                try { guard.RefreshTimer.Dispose(); } catch { }
                try { NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC, guard.PreviousWndProc); } catch { }
            }
        }

        /// <summary>指定窗口当前是否挂着持久置顶守卫（供"先停后恢复"的调用方判断要不要恢复，
        /// 避免给从未挂守卫的窗口凭空装上 <see cref="EnforceTopmost"/>）。</summary>
        public static bool IsEnforceTopmostActive(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && TopmostGuards.ContainsKey(hwnd);
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

        // -------------------- 选择性命中测试穿透（录屏覆盖层） --------------------

        private sealed class SelectiveHitTestSubclass
        {
            public required IntPtr PreviousWndProc { get; init; }
            public required NativeMethods.WndProcDelegate WndProc { get; init; }
            /// <summary>本条记录实际写进 GWL_WNDPROC 的那个函数指针（幂等分支拿它验真身）。</summary>
            public required IntPtr WndProcPtr { get; init; }
            public Func<int, int, bool>? IsInteractive;
        }

        private static readonly ConcurrentDictionary<IntPtr, SelectiveHitTestSubclass> SelectiveHitTestSubclasses = new();

        /// <summary>
        /// 选择性鼠标穿透：窗口整体在 WM_NCHITTEST 阶段穿透（点下的应用照常收到鼠标），
        /// 只有 <paramref name="isInteractive"/> 判真的屏幕物理坐标点才交给原 WndProc 正常响应。
        /// <para>与 <see cref="MakeHitTestTransparent"/> 的区别：那个是无条件全穿透；本方法允许同一覆盖层上
        /// 的徽标/控制条等小块保持可点，无需为每块单开窗口。</para>
        /// <para>可对同一 hwnd 重复调用以更新回调（不会二次子类化）。</para>
        /// </summary>
        public static bool MakeSelectiveHitTestTransparent(IntPtr hwnd, Func<int, int, bool> isInteractive)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                if (SelectiveHitTestSubclasses.TryGetValue(hwnd, out SelectiveHitTestSubclass? existing))
                {
                    // ⚠ 字典里有这条 ≠ 它还挂在这个窗口上：句柄值会被系统复用，记录也可能属于上一个
                    //    同值窗口（关窗时还原取不到句柄就会留僵尸记录）。只认"当前 GWL_WNDPROC 真的是我
                    //    挂进去的那个指针"，否则按未安装重走——走了快捷路的结果是整窗不再穿透、吞掉全屏点击。
                    if (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC) == existing.WndProcPtr)
                    {
                        existing.IsInteractive = isInteractive;
                        return true;
                    }

                    SelectiveHitTestSubclasses.TryRemove(hwnd, out _);
                }

                NativeMethods.WndProcDelegate? proc = null;
                proc = (hWnd, msg, wParam, lParam) =>
                {
                    if (msg == NativeMethods.WM_NCHITTEST)
                    {
                        // lParam：低 16 位 = 屏幕 X、高 16 位 = 屏幕 Y（物理像素，有符号短）
                        int x = (short)(lParam.ToInt64() & 0xFFFF);
                        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                        bool interactive = SelectiveHitTestSubclasses.TryGetValue(hWnd, out var state)
                            && state.IsInteractive?.Invoke(x, y) == true;
                        if (!interactive) return NativeMethods.HTTRANSPARENT;

                        // 交互点交给原 WndProc，命中客户区即返回 HTCLIENT，Avalonia 照常收到输入
                        if (state != null)
                        {
                            return NativeMethods.CallWindowProc(state.PreviousWndProc, hWnd, msg, wParam, lParam);
                        }
                        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
                    }

                    if (SelectiveHitTestSubclasses.TryGetValue(hWnd, out SelectiveHitTestSubclass? st))
                    {
                        return NativeMethods.CallWindowProc(st.PreviousWndProc, hWnd, msg, wParam, lParam);
                    }

                    return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
                };

                IntPtr procPtr = Marshal.GetFunctionPointerForDelegate(proc);
                IntPtr previous = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC, procPtr);
                if (previous == IntPtr.Zero) return false;

                SelectiveHitTestSubclasses[hwnd] = new SelectiveHitTestSubclass
                {
                    PreviousWndProc = previous,
                    WndProc = proc,
                    WndProcPtr = procPtr,
                    IsInteractive = isInteractive,
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>卸载选择性穿透子类化并还原原始 WndProc（覆盖层窗口关闭时调用；句柄会被系统复用，必须还原）。</summary>
        public static void ReleaseSelectiveHitTestTransparent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (SelectiveHitTestSubclasses.TryRemove(hwnd, out var state))
            {
                // 句柄已易主时把旧指针写回去＝给别人的窗口挂上悬空窗口过程，只摘账不还原。
                if (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC) != state.WndProcPtr) return;
                try { NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_WNDPROC, state.PreviousWndProc); } catch { }
            }
        }

        /// <summary>
        /// 整窗鼠标穿透开关（<c>WS_EX_TRANSPARENT</c>）：系统在问「这一点归谁」<b>之前</b>就把本窗整体跳过，
        /// 比 <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c> 那条形而上的路硬——后者只答命中测试这一问，
        /// 录屏覆盖层实测靠它没能兜住全屏点击（表现为除控制条外整机点不动）。
        /// <para>与 <see cref="MakeToolWindowNoActivate"/> 的 <c>transparent:true</c> 同一配方，区别是本方法可关：
        /// 需要本窗在部分坐标收笔的宿主（录屏标注画板）先关它，再交回 <see cref="MakeSelectiveHitTestTransparent"/>
        /// 逐坐标判定。<c>WS_EX_LAYERED</c> 只加不摘——本窗的透明合成由平台侧负责，摘掉会整体变实心底。</para>
        /// </summary>
        public static void SetWholeWindowClickThrough(IntPtr hwnd, bool enabled)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if (enabled) style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
                else style &= ~NativeMethods.WS_EX_TRANSPARENT;

                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE
                        | NativeMethods.SWP_NOSIZE
                        | NativeMethods.SWP_NOACTIVATE
                        | NativeMethods.SWP_FRAMECHANGED);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 摘掉／挂回 <c>WS_EX_NOACTIVATE</c>（只动这一位，其余扩展样式原样留着）。
        /// <para>存在的理由：不激活窗收不到键盘焦点——鼠标点击永不移交前台，挂在自家画布上的
        /// <c>TextBox</c> 就算拿到 Avalonia 内部焦点也收不到任何按键。要在覆盖层上打字（录屏标注的
        /// 文本档）就必须在该档期间让窗口可被点击激活，退出该档再挂回去，否则整窗又会抢前台。</para>
        /// <para>样式变更后与 <see cref="SetWholeWindowClickThrough"/> 同一收口：叠一次 <c>SWP_FRAMECHANGED</c>
        /// 让系统重读样式，且带 <c>SWP_NOACTIVATE</c>——摘掉那一位只为"之后的点击可以激活本窗"，
        /// 不是为了在这一下就把前台抢过来。</para>
        /// </summary>
        public static void SetWindowNoActivate(IntPtr hwnd, bool noActivate)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if (noActivate) style |= NativeMethods.WS_EX_NOACTIVATE;
                else style &= ~NativeMethods.WS_EX_NOACTIVATE;

                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE
                        | NativeMethods.SWP_NOSIZE
                        | NativeMethods.SWP_NOACTIVATE
                        | NativeMethods.SWP_FRAMECHANGED);
            }
            catch
            {
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
        /// 判断窗口是否为"鼠标穿透型遮罩/悬浮层"（本工具长截图原生遮罩与虚线框、
        /// 录屏软件的置顶高亮边框/悬浮条等）。这类窗口永远不会接收鼠标输入，
        /// 窗口/元素命中检测必须跳过它们、命中下层真实窗口：
        /// - WS_EX_TRANSPARENT + WS_EX_LAYERED：经典穿透组合（录屏高亮层、悬浮字幕条）；
        /// - WS_EX_TRANSPARENT + WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW：无 LAYERED 的
        ///   原生遮罩（如长截图虚线框，靠 WM_NCHITTEST 返回 HTTRANSPARENT 穿透）。
        /// 普通可交互窗口（即使带 WS_EX_LAYERED 做半透明效果）不会设置 WS_EX_TRANSPARENT。
        /// </summary>
        public static bool IsInputTransparentOverlay(IntPtr hwnd)
        {
            try
            {
                long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((ex & NativeMethods.WS_EX_TRANSPARENT) == 0) return false;
                if ((ex & NativeMethods.WS_EX_LAYERED) != 0) return true;
                return (ex & NativeMethods.WS_EX_NOACTIVATE) != 0 && (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解除窗口与 IME 上下文的关联（ImmAssociateContext(hwnd, NULL)）。
        /// 中文输入法处于激活状态时会把字母键作为组字处理（快捷键收不到 KeyDown），
        /// 解除关联后所有按键直接送达窗口，快捷键 100% 响应。
        /// 不影响文本输入：TextBox 获得焦点时 Avalonia 会自动重建 IME 关联
        /// （ImmGetContext 为零则 ImmCreateContext 新建），中文照常输入。
        /// </summary>
        public static void DisableIme(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { NativeMethods.ImmAssociateContext(hwnd, IntPtr.Zero); }
            catch { }
        }

        /// <summary>
        /// 枚举所有顶级窗口（已过滤：不可见、cloaked、应用挂起、鼠标穿透型遮罩的全部跳过）。
        /// 穿透型遮罩（长截图遮罩/录屏悬浮层）不可能成为鼠标目标，跳过后元素检测才能
        /// 命中被遮罩覆盖的真实窗口。Z 序与 EnumWindows 一致（前到后）。
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
                    if (IsInputTransparentOverlay(hwnd)) return true;
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
