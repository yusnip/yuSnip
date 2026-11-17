using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图标注浮动工具栏窗口。悬浮于当前聚焦贴图外侧，形态与截图的 ActionBar + ToolOptionsBar 一致，
/// 本窗只提供透明承载与"不抢激活"的窗口样式，容器与按钮全部在 <see cref="AnnotationToolbar"/> 内。
/// </summary>
public partial class StickerAnnotationPaletteWindow : Window
{
    public AnnotationToolbar ToolbarControl => Toolbar;

    /// <summary>参数条出现/消失或宽度变化时抛出，宿主需据此重新定位本窗。</summary>
    public event Action? ToolbarLayoutChanged;
    /// <summary>浮窗不激活，点击仍需提升所属贴图的窗口顺序。</summary>
    public event Action? InteractionRequested;

    public StickerAnnotationPaletteWindow()
    {
        InitializeComponent();
        // 本窗绝不能成为前台窗口：贴图窗口 Deactivated 会走 NotifyUnfocused → 退出标注并 Hide 本窗，
        // 抢焦点等于"显示即自关"。WS_EX_NOACTIVATE 让点击工具按钮也不移交前台，
        // 贴图因此持续收到 Space/Esc（与长截图控制面板窗同一做法）。
        Opened += (_, _) => EnsureNoActivateStyle();
        AddHandler(PointerPressedEvent, OnPalettePointerPressed, RoutingStrategies.Tunnel);
        Toolbar.LayoutChanged += () => ToolbarLayoutChanged?.Invoke();
        Toolbar.CustomColorRequested += OnCustomColorRequested;
    }

    /// <summary>彩虹取色钮：与截图同一份取色窗，锚定到按钮自身。初值取**当前取色目标**的颜色，
    /// 文本语境下可能是底色或描边色，不是全局标注色。</summary>
    private async void OnCustomColorRequested(Control anchor)
    {
        // 取色窗会抢前台：编辑中直接开＝那一下输入被吞，先按"点浮窗其他控件"的同一语义提交文字再取色
        Toolbar.TextController?.EndActiveTextEditing("CustomColor");
        Color? picked = await ColorPickerWindow.PickAsync(this, Toolbar.CurrentTargetColor, anchor);
        if (picked.HasValue) Toolbar.ApplyCustomColor(picked.Value);
    }

    /// <summary>
    /// 补挂"不激活"扩展样式。宿主每次 Show() 之后都要延后一拍调用：Avalonia 会按
    /// CanResize/Topmost 等属性重算并整体覆写 GWL_EXSTYLE，只在 Opened 挂一次会被抹掉，
    /// 于是"退出标注后 Hide()、再次进入 Show()"出来的浮窗变成普通可激活窗，一点就抢前台。
    /// </summary>
    public void EnsureNoActivateStyle()
    {
        try
        {
            IntPtr hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            // 只做"不激活"，不开持久置顶守卫：EnforceTopmost 需与 StopEnforceTopmost 配对释放，
            // 而本窗与贴图同在 Topmost 组、几何上不与贴图重叠，无持久置顶必要。
            ScreenCaptureTool.Platform.WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: false);
        }
        catch { }
    }

    /// <summary>临时诊断（定位"一点浮窗就退出标注"）：记录按下瞬间的真实前台窗与自身扩展样式。</summary>
    private void OnPalettePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        InteractionRequested?.Invoke();
        try
        {
            IntPtr hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            long ex = ScreenCaptureTool.Platform.NativeMethods.GetWindowLongPtr(hwnd,
                ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE).ToInt64();
            bool noActivate = (ex & ScreenCaptureTool.Platform.NativeMethods.WS_EX_NOACTIVATE) != 0;
            IntPtr fg = ScreenCaptureTool.Platform.NativeMethods.GetForegroundWindow();
            uint fgPid = 0;
            if (fg != IntPtr.Zero) ScreenCaptureTool.Platform.NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
            AppLogger.Info($"[标注诊断] 浮窗按下 本窗hwnd={hwnd} NOACTIVATE={noActivate} ex=0x{ex:X8} " +
                           $"前台hwnd={fg} pid={fgPid}(本机{Environment.ProcessId}) class={DescribeClass(fg)} IsActive={IsActive}");
        }
        catch (Exception ex)
        {
            AppLogger.Info("[标注诊断] 浮窗按下日志失败: " + ex.Message);
        }
    }

    private static string DescribeClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "-";
        try
        {
            var sb = new System.Text.StringBuilder(256);
            ScreenCaptureTool.Platform.NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return "-"; }
    }

    /// <summary>
    /// 贴在贴图下方，且**主工具栏右缘对齐贴图右缘**（2026-09-05 由"水平居中"改为本口径，用户拍板）。
    /// 阴影余量在本控件根 Grid 的四周（<see cref="AnnotationToolbar.ShadowInset"/>），窗口客户区左缘到
    /// 可见主工具栏右缘的距离已由 <see cref="AnnotationToolbar.PrimaryBarRight"/> 一并算好。
    /// <b>窗口顶边一律不越过贴图下缘</b>：那圈透明区虽然画不出像素却照样吃点击，旧实现按
    /// "可见间距 12 − 上沿缓冲 40" 算出 -28 的负偏移，等于用透明带把贴图下缘的拖边热区整个盖掉。
    /// 上沿余量 12 与视觉间距 12 恰好相等，两者抵完正好严丝合缝：可见工具条顶边距贴图下缘 12 DIP，
    /// 光晕画在桌面那一侧。夹取**只夹可见工具条、不夹阴影余量**：那圈透明区溢出屏幕没有任何代价
    /// （本来就画不出像素），而按整窗夹取会在"贴图贴近屏幕右缘、参数条又比图标条宽"时把主工具栏推离
    /// 贴图右缘——用户实测的"弹出子工具栏时原工具栏位置偏移"正是这个。仍不翻到贴图上方（位置要稳）。
    /// </summary>
    public void PositionNearSticker(PixelRect stickerBounds, double scaling)
    {
        // 可见工具条顶边距贴图下缘的设计间距；小于上沿余量时按余量走（见下方 Math.Max）
        const double visibleGapDip = 12.0;
        var inset = ToolbarControl.ShadowInset;
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int insetLeftPx = (int)Math.Round(inset.Left * scaling);
        int insetTopPx = (int)Math.Round(inset.Top * scaling);
        int visibleHpx = Math.Max(1, (int)Math.Ceiling((DesiredSize.Height - inset.Top - inset.Bottom) * scaling));
        // 窗口左缘 → 可见工具条右缘 / 下缘的距离
        int rightOffsetPx = Math.Max(1, (int)Math.Round(ToolbarControl.PrimaryBarRight * scaling));
        int bottomOffsetPx = insetTopPx + visibleHpx;

        int targetX = stickerBounds.Right - rightOffsetPx;
        int targetY = stickerBounds.Bottom + Math.Max(0, (int)Math.Round((visibleGapDip - inset.Top) * scaling));

        var workArea = ScreenCaptureTool.Platform.MonitorHelper.GetWorkAreaUnion();
        if (workArea.Width > 0)
        {
            int minX = workArea.Left - insetLeftPx;
            int maxX = workArea.Right - rightOffsetPx;
            targetX = Math.Clamp(targetX, minX, Math.Max(minX, maxX));
        }
        if (workArea.Height > 0)
        {
            int minY = workArea.Top - insetTopPx;
            int maxY = workArea.Bottom - bottomOffsetPx;
            targetY = Math.Clamp(targetY, minY, Math.Max(minY, maxY));
        }

        Position = new PixelPoint(targetX, targetY);
    }
}
