using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Update;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 更新对话框：检查版本 → 展示新版本/更新日志 → 流式下载 → 重启替换。
/// silentAutoCheck=true 时表示由启动静默检查唤起，若无新版本则自动关闭不干扰用户。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly bool _silentAutoCheck;
    private UpdateInfo? _pending;
    private CancellationTokenSource? _cts;
    private string? _downloadedNewExe;

    public UpdateWindow() : this(silentAutoCheck: false) { }

    public UpdateWindow(bool silentAutoCheck, UpdateInfo? known = null)
    {
        _silentAutoCheck = silentAutoCheck;
        _pending = known;
        InitializeComponent();
        TxtCurrentVersion.Text = "当前版本 v" + UpdateService.CurrentVersionStr;

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == ActualThemeVariantProperty)
            {
                UpdateThemeIcons();
            }
        };
        UpdateThemeIcons();
    }

    /// <summary>Logo 与窗口图标随主题切换（与设置页/托盘一致：深色用 Black.ico，浅色用 White.ico）。</summary>
    private void UpdateThemeIcons()
    {
        try
        {
            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            var uri = new Uri(isDark ? "avares://yuSnip/Assets/Black.ico" : "avares://yuSnip/Assets/White.ico");
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));
            ImgLogo.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(uri));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateWindow.UpdateThemeIcons: " + ex.Message);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try { Activate(); } catch { }
        if (_pending != null)
        {
            ShowAvailable(_pending);
        }
        else
        {
            _ = CheckAsync();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async Task CheckAsync()
    {
        ShowChecking();
        try
        {
            var info = await UpdateService.CheckAsync();
            if (UpdateService.HasNewer(info))
            {
                _pending = info;
                ShowAvailable(info);
            }
            else if (_silentAutoCheck)
            {
                Close();
            }
            else
            {
                ShowNoUpdate();
            }
        }
        catch (Exception ex)
        {
            if (_silentAutoCheck)
            {
                Close();
                return;
            }
            ShowError("无法连接更新服务器，请检查网络后重试。\n" + ex.Message);
        }
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_pending == null) return;
        var info = _pending;
        ShowDownloading();
        string? tmpPath = null;

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new InvalidOperationException("无法定位当前程序路径。");
            }

            tmpPath = Path.Combine(Path.GetTempPath(), $"yusnip_update_{ReleaseChannel.Name}_{Guid.NewGuid():N}.exe");
            var sw = Stopwatch.StartNew();
            var progress = new Progress<(long read, long total)>(p =>
            {
                if (p.total > 0)
                {
                    PbDownload.Value = p.read * 100.0 / p.total;
                    double mbRead = p.read / 1024.0 / 1024.0;
                    double mbTotal = p.total / 1024.0 / 1024.0;
                    double speed = sw.Elapsed.TotalSeconds > 0.3 ? mbRead / sw.Elapsed.TotalSeconds : 0;
                    TxtDownloadDetail.Text = $"{mbRead:F1} / {mbTotal:F1} MB（{speed:F1} MB/s）";
                }
                else
                {
                    PbDownload.Value = 0;
                    TxtDownloadDetail.Text = $"{p.read / 1024.0 / 1024.0:F1} MB 已下载";
                }
            });

            _cts = new CancellationTokenSource();
            await UpdateService.DownloadAsync(info, tmpPath, progress, _cts.Token);

            string newExe = exePath + ".new";
            File.Copy(tmpPath, newExe, overwrite: true);
            _downloadedNewExe = newExe;

            // 下载完成：无需多余提示与二次确认，直接自动重启软件完成更新
            BtnCancel.IsVisible = false;
            TxtDownloadStatus.Text = "下载完成，正在重启软件…";
            PbDownload.Value = 100;
            TxtDownloadDetail.Text = "即将自动启动新版本…";
            await Task.Delay(300);

            RestartAndApplyUpdate();
        }
        catch (OperationCanceledException)
        {
            _pending = info;
            ShowAvailable(info);
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateWindow: 下载更新失败: " + ex.Message);
            ShowError("下载更新失败：\n" + ex.Message);
        }
        finally
        {
            if (tmpPath != null)
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnRestartClick(object? sender, RoutedEventArgs e) => RestartAndApplyUpdate();

    private void RestartAndApplyUpdate()
    {
        if (_downloadedNewExe == null) return;
        App.IsShuttingDown = true;
        try
        {
            Services.StickerArchiveStore.SaveActiveSession();
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                UpdateService.LaunchSelfReplace(exePath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateWindow: 启动自替换失败: " + ex.Message);
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e) => _ = CheckAsync();

    // ---------- 状态切换 ----------

    private void HideAllPanels()
    {
        PanelChecking.IsVisible = false;
        PanelAvailable.IsVisible = false;
        PanelNoUpdate.IsVisible = false;
        PanelDownloading.IsVisible = false;
        PanelReady.IsVisible = false;
        PanelError.IsVisible = false;
        BtnCancel.IsVisible = false;
        BtnRetry.IsVisible = false;
        BtnLater.IsVisible = false;
        BtnUpdate.IsVisible = false;
        BtnRestart.IsVisible = false;
    }

    private void ShowChecking() => HideAllPanels();

    private void ShowAvailable(UpdateInfo info)
    {
        HideAllPanels();
        TxtNewVersion.Text = "v" + info.Version;
        TxtUpdateDate.Text = string.IsNullOrEmpty(info.UpdatedAt) ? "" : info.UpdatedAt + " 发布";
        TxtChangelog.Text = string.IsNullOrWhiteSpace(info.Changelog) ? "本次更新内容请见发布说明。" : info.Changelog;
        PanelAvailable.IsVisible = true;
        BtnUpdate.IsVisible = true;
        BtnLater.Content = _silentAutoCheck ? "稍后提醒" : "关闭";
        BtnLater.IsVisible = true;
    }

    private void ShowNoUpdate()
    {
        HideAllPanels();
        TxtNoUpdateHint.Text = "当前 v" + UpdateService.CurrentVersionStr + " 已是官方最新版本";
        PanelNoUpdate.IsVisible = true;
        BtnLater.Content = "关闭";
        BtnLater.IsVisible = true;
    }

    private void ShowDownloading()
    {
        HideAllPanels();
        PbDownload.Value = 0;
        TxtDownloadDetail.Text = "正在连接下载服务器…";
        PanelDownloading.IsVisible = true;
        BtnCancel.IsVisible = true;
    }

    private void ShowReady()
    {
        HideAllPanels();
        TxtReadyHint.Text = "新版本已就绪，点击“立即重启软件”完成更新；\n软件将自动退出并重新启动，无需重启电脑。";
        PanelReady.IsVisible = true;
        BtnRestart.IsVisible = true;
        BtnLater.Content = "稍后重启软件";
        BtnLater.IsVisible = true;
    }

    private void ShowError(string message)
    {
        HideAllPanels();
        TxtError.Text = message;
        PanelError.IsVisible = true;
        BtnRetry.IsVisible = true;
        BtnLater.Content = "关闭";
        BtnLater.IsVisible = true;
    }
}
