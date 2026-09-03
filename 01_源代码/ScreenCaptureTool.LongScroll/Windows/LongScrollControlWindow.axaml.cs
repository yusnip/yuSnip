using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>
/// 长截图控制窗口。Avalonia 版按旧版 ControlWindow 紧凑控制条复刻。
/// </summary>
public partial class LongScrollControlWindow : Window
{
    private int _regionWidth;

    public LongScrollControlWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? FinishRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? SaveAsRequested;

    public event EventHandler? AutoScrollRequested;

    public event EventHandler<int>? AutoScrollIntervalWheel;

    public void SetRegionInfo(System.Drawing.Rectangle region)
    {
        _regionWidth = Math.Max(0, region.Width);
        TxtMetrics.Text = string.Empty;
        TxtHint.Text = string.Empty;
    }

    public void SetStatus(string status)
    {
        TxtStatus.Text = string.IsNullOrWhiteSpace(status) ? "等待接入采集循环..." : status;
    }

    public void SetState(string status, string metrics, string hint, bool warn, int pressurePct)
    {
        TxtStatus.Text = string.IsNullOrWhiteSpace(status) ? "采集中..." : status;
        TxtMetrics.Text = string.IsNullOrWhiteSpace(metrics) ? string.Empty : metrics;
        TxtHint.Text = hint ?? string.Empty;
        ToolTip.SetTip(TxtMetrics, hint ?? string.Empty);
        TxtStatus.Foreground = warn ? new SolidColorBrush(Color.FromRgb(229, 57, 53)) : (IBrush?)Resources["TextBrush"] ?? Brushes.Black;
    }

    public void UpdateMetrics(int totalHeightPx, int frameCount)
    {
        TxtStatus.Text = Math.Max(0, _regionWidth) + " × " + Math.Max(0, totalHeightPx) + "px";
        TxtMetrics.Text = string.Empty;
        TxtHint.Text = string.Empty;
    }

    public void SetAutoScrollEnabled(bool enabled)
    {
        TxtAutoScroll.Text = enabled ? "停止滚动" : "自动滚动";
        AutoScrollFreqBox.IsVisible = enabled;
    }

    public void SetAutoScrollIntervalMs(int intervalMs)
    {
        TxtAutoScrollFreq.Text = "频率：" + Math.Max(1, intervalMs) + "ms";
    }

    public void ApplyLongScrollTheme(bool isDarkMode)
    {
        if (isDarkMode)
        {
            SetBrush("PanelBackgroundBrush", Color.FromRgb(0x2E, 0x2E, 0x2E));
            SetBrush("SubtleBackgroundBrush", Color.FromRgb(0x3E, 0x3E, 0x3E));
            SetBrush("TextBrush", Color.FromRgb(0xD5, 0xD5, 0xD5));
            SetBrush("IconBrush", Color.FromRgb(0xD5, 0xD5, 0xD5));
            SetBrush("BarBorderBrush", Color.FromRgb(0x44, 0x44, 0x44));
            SetBrush("SeparatorBrush", Color.FromRgb(0x55, 0x55, 0x55));
            SetBrush("ButtonHoverBrush", Color.FromRgb(0x3E, 0x3E, 0x3E));
            SetBrush("ButtonPressedBrush", Color.FromRgb(0x4E, 0x4E, 0x4E));
            return;
        }

        SetBrush("PanelBackgroundBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
        SetBrush("SubtleBackgroundBrush", Color.FromRgb(0xF0, 0xF0, 0xF0));
        SetBrush("TextBrush", Color.FromRgb(0x2B, 0x2F, 0x36));
        SetBrush("IconBrush", Color.FromRgb(0x2B, 0x2F, 0x36));
        SetBrush("BarBorderBrush", Color.FromRgb(0xD0, 0xD0, 0xD0));
        SetBrush("SeparatorBrush", Color.FromRgb(0xE0, 0xE0, 0xE0));
        SetBrush("ButtonHoverBrush", Color.FromRgb(0xEA, 0xEA, 0xEA));
        SetBrush("ButtonPressedBrush", Color.FromRgb(0xDC, 0xDC, 0xDC));
    }

    private void SetBrush(string key, Color color)
    {
        Resources[key] = new SolidColorBrush(color);
    }

    private void OnAutoScrollClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AutoScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAutoScrollIntervalWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        AutoScrollIntervalWheel?.Invoke(this, e.Delta.Y > 0 ? 120 : -120);
    }

    private void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SaveAsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FinishRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
