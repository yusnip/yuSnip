using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using System.Linq;
using System.Collections.Generic;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>
/// 长截图控制窗口：悬浮紧凑控制条。
/// </summary>
public partial class LongScrollControlWindow : Window
{
    private readonly ToolbarLayoutView _customLayout;
    private ScrollAxis _axis = ScrollAxis.Vertical;
    private int _regionWidth;
    private int _regionHeight;
    // 自动滚动方向：1=尾向（纵向↓/横向→），-1=首向（纵向↑/横向←）。
    private int _autoScrollDirection = 1;

    public void ConfigureCompletionShortcuts(string cancelKey, string finishKey)
    {
        BtnStop.Tag = string.IsNullOrWhiteSpace(cancelKey) ? "取消" : $"取消 ({cancelKey})";
        BtnFinish.Tag = string.IsNullOrWhiteSpace(finishKey) ? "完成并复制到剪贴板" : $"完成并复制到剪贴板 ({finishKey})";
    }

    public void RequestCancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
    public void RequestFinish() => FinishRequested?.Invoke(this, EventArgs.Empty);

    public LongScrollControlWindow()
    {
        InitializeComponent();
        var tools = (StackPanel)BtnToggleAxis.Parent!;
        _customLayout = new ToolbarLayoutView(tools, ToolbarScene.LongScroll, new Dictionary<string, Control[]>
        {
            ["axis"] = [BtnToggleAxis], ["autoscroll"] = [BtnAutoScroll, AutoScrollFreqBox, AutoScrollDirectionBox],
            ["trim"] = [BtnManualOffset], ["sticker"] = [BtnSticker], ["quicksave"] = [BtnQuickSave], ["saveas"] = [BtnSaveAs],
            ["cancel"] = [BtnStop], ["finish"] = [BtnFinish]
        }, tools.Children[0]);
        _customLayout.Apply(AppSettings.Current);
        ((Avalonia.Controls.Shapes.Path)BtnSticker.Content!).Data = Geometry.Parse(ToolbarRegistry.Find("sticker")!.IconPath!);
        // 默认栏新增输出按钮，自定义组也可能更宽；按内容测宽，沿用会话摆位链。

        // The bar floats right above the capture region, so any DWM chrome
        // shadow it casts is baked into the top rows of every captured frame
        // and shows up as a shadow-like band in the composed image. Chrome
        // effects are disabled for both scroll axes (same window, two modes).
        Opened += (_, _) =>
        {
            try
            {
                IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    WindowHelper.DisableWindowChromeEffects(hwnd);
                }
            }
            catch
            {
            }
        };

        Closed += (_, _) =>
        {
            try { HideTip(); } catch { }
        };
    }

    public event EventHandler? FinishRequested;

    public event EventHandler? StickerRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? SaveAsRequested;

    public event EventHandler? QuickSaveRequested;

    public event EventHandler? AxisToggleRequested;

    public event EventHandler? AutoScrollRequested;

    public event EventHandler<int>? AutoScrollIntervalWheel;

    /// <summary>请求翻转自动滚动方向：1=尾向（下/右），-1=首向（上/左）。</summary>
    public event EventHandler? AutoScrollDirectionFlipRequested;

    /// <summary>手动偏移修正：按下时触发，通知 Session 暂停滚动与帧采集。</summary>
    public event EventHandler? ManualOffsetStarted;

    /// <summary>手动偏移修正：拖动时持续触发，参数为从按下点到当前位置的总物理像素偏移（正=向下，负=向上）。</summary>
    public event EventHandler<int>? ManualOffsetChanged;

    /// <summary>手动偏移修正：拖动释放时触发，通知 Session 执行一次性裁剪。</summary>
    public event EventHandler? ManualOffsetReleased;

    /// <summary>配置控制条使用的物理滚动轴。旧调用默认保持纵向。</summary>
    public void ConfigureAxis(ScrollAxis axis)
    {
        _axis = axis;
        bool horizontal = axis == ScrollAxis.Horizontal;
        BtnManualOffset.Cursor = new Cursor(horizontal ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
        BtnManualOffset.Tag = horizontal ? "裁剪多余内容（左右拖动）" : "裁剪多余内容（上下拖动）";
        BtnAutoScroll.Tag = "左键：开始/停止 · 右键：切换滚动方向";
        BtnToggleAxis.Tag = horizontal ? "当前：横向长截图（点击切换为纵向）" : "当前：纵向长截图（点击切换为横向）";
        ManualOffsetIcon.Data = Geometry.Parse(horizontal
            ? "M11,7L6,12L11,17M13,7L18,12L13,17"
            : "M7,11L12,6L17,11M7,13L12,18L17,13");
        // 纵横切换：三个圆角矩形（三条粗圆头短线）。横向=三横条，纵向=整体旋转90°变三竖条。
        ToggleAxisIcon.Data = Geometry.Parse("M5.5 6.5H18.5 M5.5 12H18.5 M5.5 17.5H18.5");
        ToggleAxisIcon.RenderTransform = horizontal ? null : new RotateTransform(90);
        UpdateAutoScrollDirectionIcon();
        SetAutoScrollEnabled(false);
    }

    public void SetRegionInfo(System.Drawing.Rectangle region)
    {
        _regionWidth = Math.Max(0, region.Width);
        _regionHeight = Math.Max(0, region.Height);
        _regionTopPx = region.Top;
        _regionBottomPx = region.Bottom;
        SetMetrics(null);
        TxtHint.Text = string.Empty;
    }

    public void SetStatus(string status)
    {
        TxtStatus.Text = string.IsNullOrWhiteSpace(status) ? "等待接入采集循环..." : status;
    }

    public void SetState(string status, string metrics, string? hint, bool warn, int pressurePct, bool showHint = false)
    {
        TxtStatus.Text = string.IsNullOrWhiteSpace(status) ? "采集中..." : status;
        SetMetrics(metrics);
        // hint 为 null 表示“保持现状”：周期状态刷新不清除仍在驻留期的提示文本。
        if (hint != null)
        {
            TxtHint.Text = hint;
            TxtHint.IsVisible = showHint && !string.IsNullOrWhiteSpace(hint);
        }
        // 警告态用红色，其它状态恢复为主题默认文本色（走 TextBlock 样式 SctTextPrimaryBrush）。
        TxtStatus.ClearValue(TextBlock.ForegroundProperty);
        if (warn)
        {
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(229, 57, 53));
        }
    }

    /// <summary>第二行只在有内容时占位，空文本不留下空白行（控制条整体是 SizeToContent）。</summary>
    private void SetMetrics(string? metrics)
    {
        bool hasText = !string.IsNullOrWhiteSpace(metrics);
        TxtMetrics.Text = hasText ? metrics! : string.Empty;
        TxtMetrics.IsVisible = hasText;
    }

    public void UpdateMetrics(int totalHeightPx, int frameCount)
    {
        TxtStatus.Text = _axis == ScrollAxis.Horizontal
            ? Math.Max(0, totalHeightPx) + " × " + Math.Max(0, _regionHeight) + "px"
            : Math.Max(0, _regionWidth) + " × " + Math.Max(0, totalHeightPx) + "px";
        SetMetrics(null);
        TxtHint.Text = string.Empty;
    }

    public void SetAutoScrollEnabled(bool enabled)
    {
        TxtAutoScroll.Text = enabled ? "停止滚动" : (_axis == ScrollAxis.Horizontal ? "自动横滚" : "自动滚动");
        AutoScrollFreqBox.IsVisible = enabled && _customLayout.Includes("autoscroll");
        AutoScrollDirectionBox.IsVisible = enabled && _customLayout.Includes("autoscroll");
    }

    /// <summary>同步自动滚动方向到控制条（按钮箭头 + 方向 chip）。</summary>
    public void SetAutoScrollDirection(int direction)
    {
        _autoScrollDirection = direction < 0 ? -1 : 1;
        UpdateAutoScrollDirectionIcon();
    }

    /// <summary>按当前轴向与方向刷新方向指示图标（纯色圆角三角形）。</summary>
    private void UpdateAutoScrollDirectionIcon()
    {
        // 圆角三角形：三条边各带一处圆弧过渡（Q 二次贝塞尔），与工具栏圆角风格统一。
        string data = _axis == ScrollAxis.Horizontal
            ? (_autoScrollDirection > 0
                ? "M6,5 Q6,3.2 7.8,4 L17,10.2 Q19,11.4 19,12 Q19,12.6 17,13.8 L7.8,20 Q6,20.8 6,19 Z"
                : "M18,5 Q18,3.2 16.2,4 L7,10.2 Q5,11.4 5,12 Q5,12.6 7,13.8 L16.2,20 Q18,20.8 18,19 Z")
            : (_autoScrollDirection > 0
                ? "M5,6 Q3.2,6 4,7.8 L10.2,17 Q11.4,19 12,19 Q12.6,19 13.8,17 L20,7.8 Q20.8,6 19,6 Z"
                : "M5,18 Q3.2,18 4,16.2 L10.2,7 Q11.4,5 12,5 Q12.6,5 13.8,7 L20,16.2 Q20.8,18 19,18 Z");
        AutoScrollDirectionIcon.Data = Geometry.Parse(data);
        AutoScrollDirectionChipIcon.Data = Geometry.Parse(data);
    }

    public void SetAutoScrollIntervalMs(int intervalMs)
    {
        TxtAutoScrollFreq.Text = "频率：" + Math.Max(1, intervalMs) + "ms";
    }

    private void OnToggleAxisClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AxisToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAutoScrollClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AutoScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>右键自动滚动按钮：请求翻转滚动方向（运行中实时生效）。</summary>
    private void OnAutoScrollContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        AutoScrollDirectionFlipRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>点击方向 chip：请求翻转滚动方向。</summary>
    private void OnAutoScrollDirectionClick(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        AutoScrollDirectionFlipRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAutoScrollIntervalWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        double along = _axis == ScrollAxis.Horizontal ? e.Delta.X : e.Delta.Y;
        if (Math.Abs(along) < double.Epsilon) return;
        AutoScrollIntervalWheel?.Invoke(this, along > 0 ? 120 : -120);
    }

    private void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SaveAsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnQuickSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        QuickSaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestFinish();
    }

    private void OnStickerClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StickerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestCancel();
    }

    // --- 自定义 Tooltip ---
    // 原生 ToolTip 的弹层是独立 HWND，长截图会话中控制窗与预览窗每秒重断言
    // HWND_TOPMOST（EnforceTopmost 后备定时器），Z 序反复重排会把 Tooltip 弹层
    // 压到后面，表现为"悬浮 1~2 秒就消失"。这里改为自管 Popup：只由指针进出
    // 控制开关，窗口如何重排都不影响；且永远弹向远离选区的一侧，既不会盖住
    // 选区被采进长图帧，也不会与预览窗（总在选区另一侧）争 Z 序。
    private DispatcherTimer? _tipShowTimer;
    private Control? _tipTarget;
    private long _tipLastHideMs;
    private int _regionTopPx;
    private int _regionBottomPx;

    private const int TipShowDelayMs = 350;
    private const int TipQuickReshowMs = 250;

    private void OnTipSourcePointerEnter(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || control.Tag is not string text || text.Length == 0) return;
        if (TipPopup.IsOpen && ReferenceEquals(_tipTarget, control)) return;

        _tipTarget = control;
        if (TipPopup.IsOpen || Environment.TickCount64 - _tipLastHideMs < TipQuickReshowMs)
        {
            // 相邻按钮间快速移动：立即切换显示，不再等待延迟
            ShowTip();
            return;
        }

        _tipShowTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TipShowDelayMs) };
        _tipShowTimer.Tick -= OnTipShowTimerTick;
        _tipShowTimer.Tick += OnTipShowTimerTick;
        _tipShowTimer.Stop();
        _tipShowTimer.Start();
    }

    private void OnTipShowTimerTick(object? sender, EventArgs e)
    {
        _tipShowTimer?.Stop();
        ShowTip();
    }

    private void OnTipSourcePointerLeave(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(sender, _tipTarget)) return;
        HideTip();
    }

    private void ShowTip()
    {
        Control? target = _tipTarget;
        if (target == null || target.Tag is not string text || text.Length == 0) return;

        TipHost.Content = text;
        // 以按钮为锚点：水平居中于按钮、垂直留 6px 间隙，弹向远离选区的一侧
        // （工具栏贴在选区上方时朝上弹，否则朝下弹）。
        bool barAboveRegion;
        try
        {
            double scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
            double barCenterY = Position.Y + Bounds.Height * scale / 2.0;
            barAboveRegion = _regionBottomPx > _regionTopPx
                && barCenterY < (_regionTopPx + _regionBottomPx) / 2.0;
        }
        catch
        {
            barAboveRegion = false;
        }
        TipPopup.PlacementTarget = target;
        TipPopup.Placement = barAboveRegion ? PlacementMode.Top : PlacementMode.Bottom;
        TipPopup.VerticalOffset = barAboveRegion ? -6 : 6;
        TipPopup.HorizontalOffset = 0;
        TipPopup.IsOpen = true;
    }

    private void HideTip()
    {
        _tipShowTimer?.Stop();
        if (TipPopup.IsOpen)
        {
            TipPopup.IsOpen = false;
            _tipLastHideMs = Environment.TickCount64;
        }
        _tipTarget = null;
    }

    // --- 手动偏移修正拖动 ---
    private bool _isOffsetDragging;
    private bool _offsetUsesScreenCoordinates;
    private int _offsetDragStartScreenAlong;
    private double _offsetDragStartDipAlong;
    private double _offsetDragScale = 1.0;
    private int _offsetLastTotal;

    private void OnManualOffsetPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 开始裁剪拖动时收起 Tooltip，避免跟随拖动悬浮。
        HideTip();

        _isOffsetDragging = true;
        var startPosition = e.GetPosition(this);
        _offsetDragStartDipAlong = _axis == ScrollAxis.Horizontal ? startPosition.X : startPosition.Y;
        _offsetDragScale = DesktopScaling > 0 ? DesktopScaling : 1.0;
        _offsetLastTotal = 0;
        _offsetUsesScreenCoordinates = NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
        if (_offsetUsesScreenCoordinates)
        {
            _offsetDragStartScreenAlong = _axis == ScrollAxis.Horizontal ? cursor.X : cursor.Y;
        }
        else
        {
            _offsetDragStartScreenAlong = (int)Math.Round(_offsetDragStartDipAlong * _offsetDragScale);
        }

        ManualOffsetStarted?.Invoke(this, EventArgs.Empty);
        try { e.Pointer.Capture(BtnManualOffset); } catch { }
        e.Handled = true;
    }

    private void OnManualOffsetMoved(object? sender, PointerEventArgs e)
    {
        if (!_isOffsetDragging) return;

        int totalDelta;
        if (_offsetUsesScreenCoordinates && NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            int cursorAlong = _axis == ScrollAxis.Horizontal ? cursor.X : cursor.Y;
            totalDelta = cursorAlong - _offsetDragStartScreenAlong;
        }
        else
        {
            var currentPosition = e.GetPosition(this);
            double currentAlong = _axis == ScrollAxis.Horizontal ? currentPosition.X : currentPosition.Y;
            totalDelta = (int)Math.Round((currentAlong - _offsetDragStartDipAlong) * _offsetDragScale);
        }

        if (totalDelta == _offsetLastTotal) return;
        _offsetLastTotal = totalDelta;
        ManualOffsetChanged?.Invoke(this, totalDelta);
        e.Handled = true;
    }

    private void EndManualOffsetDrag(IPointer? pointer)
    {
        if (!_isOffsetDragging) return;
        _isOffsetDragging = false;
        try { pointer?.Capture(null); } catch { }
        ManualOffsetReleased?.Invoke(this, EventArgs.Empty);
    }

    private void OnManualOffsetReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isOffsetDragging) return;
        EndManualOffsetDrag(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// 指针捕获丢失时（窗口失焦、系统弹窗等），必须重置拖动状态并触发释放事件，
    /// 否则 _isOffsetDragging 永远为 true，后续所有指针移动都会被误判为拖动。
    /// </summary>
    private void OnManualOffsetCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndManualOffsetDrag(null);
    }
}
