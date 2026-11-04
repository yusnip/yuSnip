using System;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 录制状态色点的呼吸闪烁：<b>遮罩右上角 REC 徽标与浮动控制条共用这一份口径</b>。
/// 原先两个窗口各写了一份逐字相同的 <c>UpdateBlink</c>＋<c>100ms DispatcherTimer</c>，
/// 改一处忘另一处就会让同一状态在屏幕上下两处不同步闪。
/// 录制＝<c>SctRecRedBrush</c> 按 1.6s 周期明灭（亮 0.8s ／ 暗 0.15 档 0.8s）；
/// 暂停＝<c>SctRecAmberBrush</c> 常亮（时间停走，不需要"还在录"的暗示）。
/// </summary>
internal static class RecBlink
{
    /// <summary>驱动节拍：0.1s 一次，把 1.6s 周期切成足够细的明暗沿。</summary>
    private const int TimerIntervalMs = 100;

    private const long PeriodMs = 1600;
    private const long OnMs = 800;
    private const double DimOpacity = 0.15;

    /// <summary>把一枚色点刷成当前应有的状态（取不到 Token 时留空并记警告，不兜底成硬编码色）。</summary>
    internal static void Apply(Shape dot, bool paused)
    {
        if (paused)
        {
            dot.Opacity = 1;
            dot.Fill = App.ResolveThemeBrush("SctRecAmberBrush");
            return;
        }

        dot.Opacity = Environment.TickCount64 % PeriodMs < OnMs ? 1.0 : DimOpacity;
        dot.Fill = App.ResolveThemeBrush("SctRecRedBrush");
    }

    /// <summary>起一路闪烁计时并立即刷一次；返回值交给宿主在窗口 Closed 时 <c>Stop()</c>。</summary>
    internal static DispatcherTimer StartTimer(Shape dot, Func<bool> isPaused)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TimerIntervalMs) };
        timer.Tick += (_, _) => Apply(dot, isPaused());
        timer.Start();
        Apply(dot, isPaused());
        return timer;
    }
}
