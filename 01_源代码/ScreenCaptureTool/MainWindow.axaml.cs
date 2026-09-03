using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Services;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool;

/// <summary>
/// 临时启动菜单 + 全局热键宿主（T5.3 + T4.7-1 + T4.7-2 阶段产物）。
/// TODO[阶段5 T5.2]: 由 TrayIcon + NativeMenu 替代后删除本窗口。
/// </summary>
public partial class MainWindow : Window
{
    private ScreenCaptureTool.Core.HotKeyManager? _hotKey;
    private bool _captureRunning;

    public MainWindow()
    {
        InitializeComponent();

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 注册全局热键：优先使用设置里的组合；若被占用，自动尝试备用组合，避免应用不可用。
        try
        {
            var settings = AppSettings.Load();
            _hotKey = new ScreenCaptureTool.Core.HotKeyManager(OnHotKeyPressed);

            uint preferredModifiers = settings.HotKeyModifiers;
            uint preferredKey = settings.HotKeyVirtualKey;
            string preferredText = FormatHotKey(preferredModifiers, preferredKey);

            (uint Modifiers, uint Key, string Text)[] candidates =
            {
                (preferredModifiers, preferredKey, preferredText),
                (0x0002 | 0x0004, 0x41, "Ctrl+Shift+A"),
                (0x0002 | 0x0001, 0x53, "Ctrl+Alt+S"),
                (0x0002 | 0x0001, 0x51, "Ctrl+Alt+Q"),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (_hotKey.Register(candidate.Modifiers, candidate.Key))
                {
                    LblStatus.Text = i == 0
                        ? "已注册热键：" + candidate.Text
                        : "已注册备用热键：" + candidate.Text + "（" + preferredText + " 已被占用）";
                    return;
                }

                AppLogger.Warn("热键候选注册失败：" + candidate.Text + " error=" + _hotKey.LastRegisterError);
            }

            LblStatus.Text = "热键注册失败：所有候选均不可用，最后错误码 " + _hotKey.LastRegisterError;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow.OnWindowOpened 注册热键异常", ex);
            LblStatus.Text = "热键注册异常：" + ex.Message;
        }
    }

    private static string FormatHotKey(uint modifiers, uint virtualKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");

        string keyText = virtualKey >= 0x41 && virtualKey <= 0x5A
            ? ((char)virtualKey).ToString()
            : "VK_" + virtualKey.ToString("X");
        parts.Add(keyText);
        return string.Join("+", parts);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        try { _hotKey?.Dispose(); } catch { }
        _hotKey = null;
    }

    /// <summary>热键回调来自消息泵线程，必须 Post 回 UI 线程再创建窗口。</summary>
    private void OnHotKeyPressed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 重用截图按钮的逻辑（已经做了 Hide / Delay / Run / Show 完整流程）
            OnRunCaptureClick(this, new RoutedEventArgs());
        });
    }

    /// <summary>截图入口：点按钮或热键都走这里。重入保护避免连续触发。</summary>
    private async void OnRunCaptureClick(object? sender, RoutedEventArgs e)
    {
        if (_captureRunning) return;
        _captureRunning = true;

        BtnRunCapture.IsEnabled = false;
        LblStatus.Text = "正在截图…";

        try
        {
            Hide();
            await Task.Delay(120);

            var settings = AppSettings.Load();
            var session = new CaptureSession(settings);
            CaptureSessionResult? result = await session.RunAsync();

            if (result == null)
            {
                LblStatus.Text = "已取消";
            }
            else if (result.CreatedSticker)
            {
                LblStatus.Text = "已创建贴图";
            }
            else if (result.PickedColor)
            {
                LblStatus.Text = "已复制颜色：" + result.PickedColorHex;
            }
            else if (result.ClipboardCopied)
            {
                LblStatus.Text = "已复制到剪贴板";
            }
            else
            {
                LblStatus.Text = "已保存：" + result.SavedPath;
                if (settings.ShowSaveNotification)
                {
                    ShowSavedNotification(result.SavedPath);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow.OnRunCaptureClick 异常", ex);
            LblStatus.Text = "失败：" + ex.Message;
        }
        finally
        {
            BtnRunCapture.IsEnabled = true;
            _captureRunning = false;
            Show();
            Activate();
        }
    }

    private void ShowSavedNotification(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var notification = new SavedNotificationWindow(path);
            notification.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.ShowSavedNotification: 显示保存通知失败：" + ex.Message);
        }
    }

    /// <summary>仅打开遮罩，不接业务（保留阶段 T5.3 的快捷验证入口）。</summary>
    private void OnOpenCaptureClick(object? sender, RoutedEventArgs e)
    {
        var capture = new CaptureWindow();
        capture.Show();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }
}
