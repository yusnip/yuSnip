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
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly string _savedPath;
    private TimeSpan _autoCloseRemaining = TimeSpan.FromSeconds(3);
    private DateTime _autoCloseDeadline;

    public SavedNotificationWindow()
        : this(string.Empty)
    {
    }

    public SavedNotificationWindow(string savedPath)
    {
        _savedPath = savedPath ?? string.Empty;
        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _autoCloseTimer.Tick += OnAutoCloseTimerTick;

        InitializeComponent();
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
            int pixelWidth = (int)Math.Round(Bounds.Width * scale);
            int pixelHeight = (int)Math.Round(Bounds.Height * scale);
            if (pixelWidth <= 0) pixelWidth = (int)Math.Round(620 * scale);
            if (pixelHeight <= 0) pixelHeight = (int)Math.Round(110 * scale);

            int x = area.X + Math.Max(0, (area.Width - pixelWidth) / 2);
            int y = area.Y + Math.Max(0, area.Height - pixelHeight - 40);
            Position = new PixelPoint(x, y);
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
