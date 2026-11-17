using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 保存完成后的轻量通知窗口。
/// </summary>
public partial class SavedNotificationWindow : Window
{
    /// <summary>卡片四周为投影预留的透明缓冲（DIP），须与 axaml 中根 Border 的 Margin 一致。</summary>
    private const double ShadowPadDip = 26;

    private readonly DispatcherTimer _autoCloseTimer;
    private readonly string _savedPath;
    private TimeSpan _autoCloseRemaining = TimeSpan.FromSeconds(3);
    private DateTime _autoCloseDeadline;

    public SavedNotificationWindow()
        : this(string.Empty)
    {
    }

    public SavedNotificationWindow(string savedPath)
        : this(savedPath, null)
    {
    }

    /// <summary>
    /// <paramref name="title"/> 可换标题文案（计划 §3.11 D20：录屏保存复用本窗，文案「视频已保存」）；
    /// 传 null/空 = 维持截图侧默认「截图已保存」，既有调用零改动。
    /// </summary>
    public SavedNotificationWindow(string savedPath, string? title)
    {
        _savedPath = savedPath ?? string.Empty;
        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _autoCloseTimer.Tick += OnAutoCloseTimerTick;

        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title)) TxtTitle.Text = title;
        TxtSavedPath.Text = string.IsNullOrWhiteSpace(_savedPath) ? "保存路径未知" : _savedPath;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PositionAtBottomCenter();
        StartAutoClose(TimeSpan.FromSeconds(3));
    }

    private void PositionAtBottomCenter()
    {
        try
        {
            PixelRect area;
            var screen = Screens.Primary;
            if (screen != null)
            {
                area = screen.WorkingArea;
                if (area.Width <= 0 || area.Height <= 0)
                {
                    area = screen.Bounds;
                }
            }
            else
            {
                area = new PixelRect(0, 0, 1280, 720);
            }

            double scale = DesktopScaling > 0 ? DesktopScaling : 1.0;

            // 只有"看得见的卡片"参与贴边排版；窗口比卡片四周各多出一段投影缓冲，定位时要扣回来
            double cardWidth = Bounds.Width > 0 ? Math.Max(1, Bounds.Width - ShadowPadDip * 2) : 568;   // 兜底＝窗口声明宽 620 扣两侧缓冲
            double cardHeight = Bounds.Height > 0 ? Math.Max(1, Bounds.Height - ShadowPadDip * 2) : 90;

            int pixelPad = (int)Math.Round(ShadowPadDip * scale);
            int pixelWidth = (int)Math.Round(cardWidth * scale);
            int pixelHeight = (int)Math.Round(cardHeight * scale);

            int cardX = area.X + Math.Max(0, (area.Width - pixelWidth) / 2);
            int cardY = area.Y + Math.Max(0, area.Height - pixelHeight - 40);
            Position = new PixelPoint(cardX - pixelPad, cardY - pixelPad);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SavedNotificationWindow.PositionAtBottomCenter: 定位失败：" + ex.Message);
        }
    }

    private void StartAutoClose(TimeSpan delay)
    {
        _autoCloseRemaining = delay;
        _autoCloseDeadline = DateTime.Now.Add(delay);
        _autoCloseTimer.Start();
    }

    private void PauseAutoClose()
    {
        if (!_autoCloseTimer.IsEnabled) return;

        TimeSpan remaining = _autoCloseDeadline - DateTime.Now;
        _autoCloseRemaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        _autoCloseTimer.Stop();
    }

    private void ResumeAutoClose()
    {
        if (_autoCloseTimer.IsEnabled) return;
        if (_autoCloseRemaining <= TimeSpan.Zero)
        {
            _autoCloseRemaining = TimeSpan.FromMilliseconds(200);
        }

        _autoCloseDeadline = DateTime.Now.Add(_autoCloseRemaining);
        _autoCloseTimer.Start();
    }

    private void OnAutoCloseTimerTick(object? sender, EventArgs e)
    {
        if (DateTime.Now < _autoCloseDeadline) return;
        _autoCloseTimer.Stop();
        Close();
    }

    private void OpenSavedLocation()
    {
        try
        {
            _autoCloseTimer.Stop();

            if (File.Exists(_savedPath))
            {
                string args = "/select,\"" + _savedPath + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args)
                {
                    UseShellExecute = true,
                });
                return;
            }

            string? folder = Path.GetDirectoryName(_savedPath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SavedNotificationWindow.OpenSavedLocation: 打开保存位置失败：" + ex.Message);
        }
        finally
        {
            Close();
        }
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        OpenSavedLocation();
    }

    private void OnRootPointerEntered(object? sender, PointerEventArgs e)
    {
        PauseAutoClose();
    }

    private void OnRootPointerExited(object? sender, PointerEventArgs e)
    {
        ResumeAutoClose();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _autoCloseTimer.Stop(); } catch { }
        _autoCloseTimer.Tick -= OnAutoCloseTimerTick;
    }
}
