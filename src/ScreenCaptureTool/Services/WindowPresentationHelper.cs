using System;
using Avalonia.Controls;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 窗口平滑呈现辅助类：消除 Windows DWM 在 Avalonia 窗口首帧未完成时先行合成空白边框/阴影的视觉瑕疵。
/// 核心原理：在 <c>Show()</c> 之后立即应用 <c>DWMWA_CLOAK(13)</c> 让 DWM 暂时跳过对该窗口的合成，
/// 待 Avalonia 完成首帧 Measure/Arrange 与 Direct3D 交换链首帧 Present（双格 RAF 回调）后，原子级瞬间揭开，
/// 从而彻底消除白框抢跑、迟滞出字和尺寸二次跳变。
/// </summary>
public static class WindowPresentationHelper
{
    private const int DWMWA_CLOAK = 13;

    /// <summary>给启动预备窗口复用现有 DWM 隐匿通路；失败时不得保留一张无法揭开的备用窗。</summary>
    internal static bool TrySetCloaked(Window window, bool cloaked)
    {
        try
        {
            IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return false;
            int value = cloaked ? 1 : 0;
            return NativeMethods.DwmSetWindowAttributeInt(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 平滑展示窗口：首次显示时原子级呈现，已有窗口则激活并置前。
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="onPresented">首帧呈现并置前后的回调</param>
    /// <param name="beforeReveal">揭开 cloak **之前**的最后一次回调。用于"位置/尺寸要一次到位"的窗：
    /// Show() 之前窗未 attach，<c>SizeToContent</c> 的真实外框要 attach 后经 DPI 取整才定稿，
    /// 故 Show 前那次摆位量到的是偏窄的宽度 ⇒ 出现后还会自己挪一下。把这个窗的最终重钉排在这里，
    /// 用户看到的第一帧就已经是定稿位置（录屏控制条 2026-09-22）。</param>
    /// <param name="activate">揭开后是否抢前台。<c>false</c> 给那些**按设计不该抢焦点**的窗
    /// （录屏控制条是 WS_EX_NOACTIVATE 置顶小窗，一次 Activate 就会把焦点从用户正在打字的应用上抢走）。</param>
    /// <param name="gateReveal">把"揭开"这一步<b>交给调用方择机执行</b>：本参数会同步收到一个 Action
    /// （就是真正的揭开），在它认为"内容真的到位了"的那一刻调用它；重复调用无效（只揭一次）。
    /// 给了它，常规那两条通路（双格 RAF ＋ 160ms 保险）一律<b>不再揭开</b>，只留
    /// <paramref name="revealFallbackMs"/> 那道长保险。
    /// 为什么要给：RAF 只保证"Avalonia 把自己那层画完了"，管不到窗里的<b>内容</b>是否到位。
    /// 录屏预览卡就是这一类——窗画完了，ffmpeg 首帧还要 ~120ms 才喂进来，按常规通路揭开就是
    /// 一张浅色底板（<c>SctPlayerBgBrush</c>，浅色态 #FAFAFA）白闪一下（用户 2026-09-22 实报）。
    /// ⚠ 用了这一条，调用方<b>必须</b>保证一定会调用那个 Action（失败分支、关窗分支都要），
    /// 否则用户等到的是长保险那一下，等于把白闪换成"窗迟迟不出来"。</param>
    /// <param name="revealFallbackMs">配合 <paramref name="gateReveal"/> 的那道保险（毫秒）。
    /// 默认 2500 是"首帧确实可能慢，但不能永远不出"的下限口径：常规窗压根走不到这里。</param>
    public static void ShowSmoothly(Window window, Action? onPresented = null, Action? beforeReveal = null,
        bool activate = true, Action<Action>? gateReveal = null, int revealFallbackMs = 2500)
    {
        if (window == null) return;

        // 若窗口当前已处于可见状态，直接走平滑激活与置顶，不重复经历首帧隐匿流程
        if (window.IsVisible)
        {
            beforeReveal?.Invoke();
            BringToForeground(window);
            onPresented?.Invoke();
            return;
        }

        // 1. 调用 Avalonia 的 Show() 创建 Native HWND 并初始化呈现管线
        window.Show();

        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            // 兜底：未拿到 HWND 则直接走常规激活
            beforeReveal?.Invoke();
            if (activate) window.Activate();
            onPresented?.Invoke();
            return;
        }

        // 2. 立即将 HWND 置为 Cloak（隐匿）状态：
        //    DWM 在桌面上不绘制该窗口的任何像素（无边框、无阴影、无空白背景），
        //    而 Avalonia UI 线程正常进行排版，渲染管线正常生成并提交首帧。
        int cloak = 1;
        NativeMethods.DwmSetWindowAttributeInt(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

        bool uncloaked = false;
        void Uncloak()
        {
            if (uncloaked) return;
            uncloaked = true;

            // 揭开前最后一次重排/重钉（见 beforeReveal 参数说明）：此刻窗已 attach、宽度已定稿，
            // 量到的是真值，而画面还盖着，怎么写位置用户都看不见中间态。
            beforeReveal?.Invoke();

            int uncloakVal = 0;
            NativeMethods.DwmSetWindowAttributeInt(hwnd, DWMWA_CLOAK, ref uncloakVal, sizeof(int));

            // 揭开后同步激活并确保焦点置顶
            if (activate)
            {
                window.Activate();
                BringNativeToForeground(hwnd);
            }
            onPresented?.Invoke();
        }

        // 3. 揭开时机的两条通路，按有没有 gateReveal 二选一：
        //    ① 常规：调度双格 RAF——Avalonia 的 RequestAnimationFrame 在该帧实际渲染之前触发，
        //       第一格提交首帧渲染命令，第二格时 GPU/Direct3D 交换链已完成 Present，
        //       此时揭开（Uncloak）呈现的是 100% 完整、已排版且已绘制完毕的成品界面。
        //    ② 门控：把 Uncloak 这个 Action 交出去，由调用方等"内容到位"（如引擎首帧）再调；
        //       RAF 这条路一概不走——它只保证"窗画完了"，而白闪恰恰出在窗画完、内容未到之间。
        if (gateReveal != null)
        {
            gateReveal(Uncloak);
            // 保险照常留着，只是从 160ms 换成长档：调用方漏调时窗仍会出来（宁可慢，不可永久不见）
            DispatcherTimer.RunOnce(() =>
            {
                if (!uncloaked) AppLogger.Warn("WindowPresentationHelper: 门控揭开超时，走 " + revealFallbackMs + "ms 保险揭开");
                Uncloak();
            }, TimeSpan.FromMilliseconds(revealFallbackMs));
            return;
        }

        try
        {
            window.RequestAnimationFrame(_ =>
            {
                window.RequestAnimationFrame(__ =>
                {
                    Uncloak();
                });
            });
        }
        catch
        {
            // 极端情况下若调度 RAF 失败，直接揭开
            Uncloak();
        }

        // 4. 安全兜底定时器（160ms）：防止某些无 GPU 合成环境或极低频刷新下 RAF 延滞导致窗口长时间未显
        DispatcherTimer.RunOnce(Uncloak, TimeSpan.FromMilliseconds(160));
    }

    /// <summary>
    /// 将已有窗口平滑激活并带到前台（支持从最小化恢复）。
    /// </summary>
    public static void BringToForeground(Window window)
    {
        if (window == null) return;

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();

        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            BringNativeToForeground(hwnd);
        }
    }

    private static void BringNativeToForeground(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch
        {
            // 忽略 Windows 权限争夺或前台锁定时抛出的异常
        }
    }
}
