using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 短暂 Toast 提示（1.5 秒自动消失）。无交互（IsHitTestVisible=False）、不抢焦点（ShowActivated=False）。
///
/// 定位：可定位到屏幕中央，或定位到指定锚定窗口（如贴图）的区域中央。
/// </summary>
public partial class StickerToastWindow : Window
{
    private DispatcherTimer? _timer;

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
    public static void ShowAtTop(string message)
    {
        try
        {
            var toast = new StickerToastWindow();
            toast.TxtMessage.Text = message;
            toast.Width = 220;
            toast.SizeToContent = SizeToContent.Height;
            toast.PositionAtScreenTopCenter();
            toast.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerToastWindow.ShowAtTop 失败：" + ex.Message);
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
            toast.TxtMessage.Text = message;
            // 内容尺寸未知（SizeToContent），给一个合理宽度估算；高度按内容自适应。
            toast.Width = 220;
            toast.SizeToContent = SizeToContent.Height;
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

    private void OnOpened(object? sender, EventArgs e)
    {
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
            double toastWDip = Width > 0 ? Width : 220;
            // 高度未知，估算（Opened 后 Avalonia 会按内容收紧，水平居中是主要诉求）。
            const double toastHEstimate = 44;

            // Toast 左上角（DIP，相对于 anchor）= 居中偏移
            double offX = (anchorWDip - toastWDip) / 2.0;
            double offY = (anchorHDip - toastHEstimate) / 2.0;

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
            double widthDip = Width > 0 ? Width : 220;
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
            double widthDip = Width > 0 ? Width : 220;
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
