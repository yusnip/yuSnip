using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 短暂 Toast 提示（1.5 秒自动消失）。无交互（IsHitTestVisible=False）、不抢焦点（ShowActivated=False）。
///
/// 定位：可定位到屏幕中央，或定位到指定锚定窗口（如贴图）的区域中央。
/// </summary>
public partial class StickerToastWindow : Window
{
    private DispatcherTimer? _timer;
    private bool _autoClose = true;

    public StickerToastWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>便捷入口：在屏幕中央显示一条 1.5 秒提示。</summary>
    public static void Show(string message)
    {
        Show(message, anchor: null);
    }

    /// <summary>便捷入口：在屏幕中上方显示一条 1.5 秒提示（用于 OCR 成功等提示）。</summary>
    public static void ShowAtTop(string message) => ShowAtTopCore(message, autoClose: true);

    /// <summary>复用上方消息窗显示等待状态，由本次任务在结束时关闭，不按 1.5 秒自动消失。</summary>
    public static StickerToastWindow? ShowPendingAtTop(string message)
    {
        ClosePendingAtTop();
        var toast = ShowAtTopCore(message, autoClose: false);
        if (toast != null)
        {
            _pendingTop = toast;
            toast.Closed += (_, _) => { if (ReferenceEquals(_pendingTop, toast)) _pendingTop = null; };
        }
        return toast;
    }

    private static StickerToastWindow? _pendingTop;

    /// <summary>开始下一次截图前收起等待消息，避免拍进新图片。</summary>
    public static void ClosePendingAtTop() => _pendingTop?.Close();

    private static StickerToastWindow? ShowAtTopCore(string message, bool autoClose)
    {
        try
        {
            var toast = new StickerToastWindow { _autoClose = autoClose };
            toast.SetMessage(message);
            toast.PositionAtScreenTopCenter();
            toast.Show();
            return toast;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerToastWindow.ShowAtTop 失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>可复用的那一条屏幕中上方提示（<see cref="ShowOrUpdateAtTop"/> 专用）。</summary>
    private static StickerToastWindow? _sharedTop;

    /// <summary>
    /// 连续高频触发的提示（如 Ctrl+滚轮调不透明度）：已有一条在显示就只改文字并把 1.5 秒计时重新起算，
    /// 不再逐次 new 窗口——否则一格滚轮连发十几个事件，屏幕上会叠出一摞提示窗。
    /// </summary>
    public static void ShowOrUpdateAtTop(string message)
    {
        try
        {
            var t = _sharedTop;
            if (t != null && t.IsVisible)
            {
                t.SetMessage(message);
                t.PositionAtScreenTopCenter();
                t._timer?.Stop();
                t._timer?.Start();
                return;
            }
            var toast = new StickerToastWindow();
            toast.SetMessage(message);
            toast.PositionAtScreenTopCenter();
            toast.Closed += (_, _) => { if (ReferenceEquals(_sharedTop, toast)) _sharedTop = null; };
            _sharedTop = toast;
            toast.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerToastWindow.ShowOrUpdateAtTop 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 便捷入口：显示一条 1.5 秒提示。
    /// 若提供 <paramref name="anchor"/>（如贴图窗口），提示定位到该窗口区域中央；
    /// 否则定位到主屏中央。
    /// </summary>
    public static void Show(string message, Window? anchor)
    {
        try
        {
            var toast = new StickerToastWindow();
            toast.SetMessage(message);
            if (anchor != null && anchor.IsVisible)
            {
                toast.PositionOver(anchor);
            }
            else
            {
                toast.PositionAtScreenCenter();
            }
            // ShowActivated=False（XAML）保证不激活、不抢焦点。
            toast.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerToastWindow.Show 失败：" + ex.Message);
        }
    }

    private void SetMessage(string message)
    {
        TxtMessage.Text = message;
        var body = (Control)Content!;
        double s = DesktopScaling > 0 ? DesktopScaling : 1;
        var area = MonitorHelper.GetPrimaryMonitor();
        // 限制到屏幕以内，长消息自动换行；复用实时度量，短→长更新同拍重算整棵内容树。
        body.MaxWidth = Math.Max(40, area.Width / s - 32);
        var size = LayoutMetrics.MeasureNow(body);
        SizeToContent = SizeToContent.Manual;
        Width = Math.Ceiling(size.Width * s) / s;
        Height = Math.Ceiling(size.Height * s) / s;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (!_autoClose) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    /// <summary>定位到锚定窗口的区域中央（Toast 覆盖在贴图正中）。</summary>
    private void PositionOver(Window anchor)
    {
        try
        {
            double scaling = DesktopScaling > 0 ? DesktopScaling : 1.0;
            // anchor.Bounds 是 DIP 尺寸；anchor.Position 是屏幕物理像素坐标。
            double anchorWDip = anchor.Bounds.Width;
            double anchorHDip = anchor.Bounds.Height;
            double toastWDip = Width;

            // Toast 左上角（DIP，相对于 anchor）= 居中偏移
            double offX = (anchorWDip - toastWDip) / 2.0;
            double offY = (anchorHDip - Height) / 2.0;

            // 转回屏幕物理像素：anchor.Position + 偏移×scaling
            int x = anchor.Position.X + (int)Math.Round(offX * scaling);
            int y = anchor.Position.Y + (int)Math.Round(offY * scaling);
            Position = new PixelPoint(x, y);
        }
        catch
        {
            PositionAtScreenCenter();
        }
    }

    /// <summary>把窗口定位到主显示器工作区正中央（物理像素）。</summary>
    private void PositionAtScreenCenter()
    {
        try
        {
            System.Drawing.Rectangle area = MonitorHelper.GetPrimaryMonitor();
            double scaling = DesktopScaling > 0 ? DesktopScaling : 1.0;
            double widthDip = Width;
            int x = area.X + (int)Math.Round((area.Width - widthDip * scaling) / 2.0);
            int y = area.Y + (int)Math.Round(area.Height * 0.42);
            Position = new PixelPoint(Math.Max(0, x), Math.Max(0, y));
        }
        catch
        {
            // 定位失败不阻断显示
        }
    }

    /// <summary>把窗口定位到主显示器工作区中上方（物理像素）。</summary>
    private void PositionAtScreenTopCenter()
    {
        try
        {
            System.Drawing.Rectangle area = MonitorHelper.GetPrimaryMonitor();
            double scaling = DesktopScaling > 0 ? DesktopScaling : 1.0;
            double widthDip = Width;
            int x = area.X + (int)Math.Round((area.Width - widthDip * scaling) / 2.0);
            int y = area.Y + (int)Math.Round(area.Height * 0.15);
            Position = new PixelPoint(Math.Max(0, x), Math.Max(0, y));
        }
        catch
        {
            PositionAtScreenCenter();
        }
    }
}
