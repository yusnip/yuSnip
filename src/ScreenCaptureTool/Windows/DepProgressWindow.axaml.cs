using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Deps;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 运行组件下载进度卡（设计稿 05 节·乙）：OCR／抠图／录屏三处入口共用同一枚窗，非模态、可拖开。
/// 本窗只是进度的一枚视图——关掉它下载照跑，再点同一功能重开即接上当前读数。
/// </summary>
public partial class DepProgressWindow : Window
{
    private readonly string _id;
    private bool _started;
    private bool _closeScheduled;

    /// <summary>仅供 XAML 设计器与反射式创建使用；功能入口一律走 <see cref="Services.DependencyGate.ShowCard"/>。</summary>
    public DepProgressWindow() : this(DepIds.Ocr) { }

    public DepProgressWindow(string id)
    {
        _id = id;
        InitializeComponent();
        DependencyManager.Changed += OnDepSignal;
        DependencyManager.ProgressTick += OnDepSignal;
        Closed += (_, _) =>
        {
            DependencyManager.Changed -= OnDepSignal;
            DependencyManager.ProgressTick -= OnDepSignal;
        };
        Render(DependencyManager.Get(id));
    }

    /// <summary>状态跃迁与进度节拍都来自后台线程，一律转回 UI 线程再取读数。</summary>
    private void OnDepSignal(string id)
    {
        if (!string.Equals(id, _id, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.UIThread.Post(() => Render(DependencyManager.Get(_id)));
    }

    private void Render(DepSnapshot s)
    {
        switch (s.State)
        {
            case DepState.Queued:
            case DepState.Downloading:
            case DepState.Verifying:
                _started = true;
                break;
            case DepState.Failed:
                ShowFailed(s.Error);
                return;
            case DepState.Ready:
                ShowRunning(s);
                ScheduleClose();
                return;
            default: // Absent：用户取消就收卡；开卡那一拍还没拿到状态时先按"正在下"出图，不留空白卡
                if (_started) { Close(); return; }
                break;
        }
        ShowRunning(s);
    }

    private void ShowRunning(DepSnapshot s)
    {
        PanelFailed.IsVisible = false;
        PanelRunning.IsVisible = true;
        BtnCancel.IsVisible = true;
        BtnRetry.IsVisible = false;
        BtnSettings.IsVisible = false;

        string name = Services.DependencyGate.TitleOf(_id);
        TxtTitle.Text = s.State switch
        {
            DepState.Queued => $"正在排队下载 {name}",
            DepState.Verifying => $"正在校验并解包 {name}",
            _ => $"正在下载 {name}"
        };

        Pb.IsIndeterminate = s.Total <= 0;
        Pb.Value = s.Percent;
        TxtDetail.Text = s.State switch
        {
            DepState.Queued => "前面还有组件在下载，请稍候",
            DepState.Verifying => $"已下载 {Mb(s.Total)}，正在校验文件完整性并解包落位",
            _ => s.Total > 0
                ? $"{Mb(s.Done)} / {Mb(s.Total)} · {s.SpeedBps / (1024.0 * 1024.0):F1} MB/s"
                : $"{Mb(s.Done)} 已下载"
        };
        TxtEta.Text = s.ShowEta ? EtaText(s.EtaSeconds) : "";
    }

    private void ShowFailed(string error)
    {
        PanelRunning.IsVisible = false;
        PanelFailed.IsVisible = true;
        BtnCancel.IsVisible = false;
        BtnRetry.IsVisible = true;
        BtnSettings.IsVisible = true;

        string cause = error.StartsWith("下载失败：", StringComparison.Ordinal) ? error[5..] : error;
        int cut = cause.IndexOf('：');
        if (cut > 0) cause = cause[..cut];
        if (cause.Length > 20) cause = cause[..20] + "…";
        TxtFailTitle.Text = "下载失败 · " + cause;
        TxtFailWhy.Text = error;
    }

    /// <summary>就绪不弹"成功"：读数停在满格，1.2 秒后自己收，原操作随即续跑。</summary>
    private void ScheduleClose()
    {
        if (_closeScheduled) return;
        _closeScheduled = true;
        _ = DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(1.2));
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";

    private static string EtaText(long seconds) =>
        seconds < 60 ? $"剩 {seconds} 秒" : $"剩 {(long)Math.Ceiling(seconds / 60.0)} 分";

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>✕ 只收这张卡，不掐下载。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => DependencyManager.Cancel(_id);

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        ShowRunning(DependencyManager.Get(_id));
        _ = Services.DependencyGate.EnsureAsync(_id);
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            (Avalonia.Application.Current as App)?.OpenSettingsDeps();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DepProgressWindow: 打开设置失败：" + ex.Message);
        }
    }
}
