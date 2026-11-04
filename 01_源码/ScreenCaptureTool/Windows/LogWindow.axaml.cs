using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 运行日志数据模型包装：纯布尔驱动样式类，C# 零画刷。
/// </summary>
public sealed class LogEntryViewModel
{
    public AppLogger.LogEntry Raw { get; }
    public string TimeText { get; }
    public string Level => Raw.Level;
    public string Message => Raw.Message;

    public bool IsError => string.Equals(Level, "ERROR", StringComparison.OrdinalIgnoreCase);
    public bool IsWarn => string.Equals(Level, "WARN", StringComparison.OrdinalIgnoreCase);
    public bool IsInfo => string.Equals(Level, "INFO", StringComparison.OrdinalIgnoreCase);
    public bool IsDebug => string.Equals(Level, "DEBUG", StringComparison.OrdinalIgnoreCase);

    public LogEntryViewModel(AppLogger.LogEntry raw)
    {
        Raw = raw;
        TimeText = raw.Time.ToString("HH:mm:ss.fff");
    }
}

/// <summary>
/// 独立运行日志窗口：高性能虚拟化列表、实时增量追加、多维过滤、导出与定位。
/// </summary>
public partial class LogWindow : Window
{
    private static LogWindow? _instance;

    /// <summary>
    /// 静态单例唤起入口：已打开时平滑激活置顶，未打开时创建并以 DWM Cloak 原子平滑呈现。
    /// </summary>
    public static void ShowLogs()
    {
        if (_instance != null)
        {
            WindowPresentationHelper.BringToForeground(_instance);
            return;
        }

        _instance = new LogWindow();
        WindowPresentationHelper.ShowSmoothly(_instance);
    }

    private readonly ObservableCollection<LogEntryViewModel> _items = new();
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _searchDebounce;
    private int _renderedSnapshotCount;

    public LogWindow()
    {
        InitializeComponent();

        LstLogs.ItemsSource = _items;

        UpdateAppIcon();
        BtnLogOpenFolder.IsVisible = AppLogger.FileLoggingEnabled;
        TxtLogFile.Text = AppLogger.FileLoggingEnabled
            ? "文件：" + AppLogger.LogPath
            : "模式：仅内存缓存（Release 构建）";

        // 构造阶段即完成首屏日志预载：使 Visual Tree 在 Show 之前就具备数据项，
        // 配合 WindowPresentationHelper.ShowSmoothly 首帧原子级弹出，杜绝白框先显和迟滞出字。
        ReloadLogs();

        // 窗口级按键：按 Esc 直接关闭
        AddHandler(KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == ActualThemeVariantProperty)
            {
                UpdateAppIcon();
            }
        };
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 启动后台增量轮询定时器（1.5 秒）
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _refreshTimer.Tick += (_, _) => CheckIncrementalLogs();
        _refreshTimer.Start();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _searchDebounce?.Stop();
        _searchDebounce = null;

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private static Avalonia.Controls.WindowIcon? _cachedDarkIcon;
    private static Avalonia.Controls.WindowIcon? _cachedLightIcon;
    private static Avalonia.Media.Imaging.Bitmap? _cachedDarkBitmap;
    private static Avalonia.Media.Imaging.Bitmap? _cachedLightBitmap;

    private void UpdateAppIcon()
    {
        try
        {
            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            if (isDark)
            {
                _cachedDarkIcon ??= new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/Black.ico")));
                _cachedDarkBitmap ??= new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/Black.ico")));
                Icon = _cachedDarkIcon;
                if (ImgAppLogo != null)
                {
                    ImgAppLogo.Source = _cachedDarkBitmap;
                }
            }
            else
            {
                _cachedLightIcon ??= new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/White.ico")));
                _cachedLightBitmap ??= new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/White.ico")));
                Icon = _cachedLightIcon;
                if (ImgAppLogo != null)
                {
                    ImgAppLogo.Source = _cachedLightBitmap;
                }
            }
        }
        catch { }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // -------------------- 数据加载与增量追加 --------------------

    private void ReloadLogs()
    {
        AppLogger.LogEntry[] snapshot = AppLogger.GetSnapshot();
        _renderedSnapshotCount = snapshot.Length;

        _items.Clear();
        foreach (AppLogger.LogEntry entry in snapshot)
        {
            if (MatchesFilter(entry))
            {
                _items.Add(new LogEntryViewModel(entry));
            }
        }

        UpdateMeta(snapshot.Length);
        ScrollToLatestIfNeeded();
    }

    private void CheckIncrementalLogs()
    {
        AppLogger.LogEntry[] snapshot = AppLogger.GetSnapshot();
        if (snapshot.Length < _renderedSnapshotCount)
        {
            // 外部发生了清空或截断，全量重载
            ReloadLogs();
            return;
        }

        if (snapshot.Length == _renderedSnapshotCount)
        {
            return;
        }

        // 增量追加新条目
        bool anyAdded = false;
        for (int i = _renderedSnapshotCount; i < snapshot.Length; i++)
        {
            AppLogger.LogEntry entry = snapshot[i];
            if (MatchesFilter(entry))
            {
                _items.Add(new LogEntryViewModel(entry));
                anyAdded = true;
            }
        }
        _renderedSnapshotCount = snapshot.Length;

        UpdateMeta(snapshot.Length);
        if (anyAdded)
        {
            ScrollToLatestIfNeeded();
        }
    }

    private bool MatchesFilter(AppLogger.LogEntry entry)
    {
        int levelIndex = CmbLogLevelFilter?.SelectedIndex ?? 0;
        // 0=全部 1=信息 2=警告 3=错误
        if (levelIndex == 1 && !string.Equals(entry.Level, "INFO", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(entry.Level, "DEBUG", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (levelIndex == 2 && !string.Equals(entry.Level, "WARN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (levelIndex == 3 && !string.Equals(entry.Level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string keyword = TxtLogSearch?.Text?.Trim() ?? string.Empty;
        if (keyword.Length > 0)
        {
            if (entry.Message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                entry.Level.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateMeta(int totalCount)
    {
        string meta = $"本次运行共 {totalCount} 条记录 · 当前显示 {_items.Count} 条";
        TxtLogMeta.Text = meta;
        PnlLogEmpty.IsVisible = _items.Count == 0;
    }

    private void ScrollToLatestIfNeeded()
    {
        if (ChkAutoScroll?.IsChecked == true && _items.Count > 0)
        {
            LstLogs.ScrollIntoView(_items[_items.Count - 1]);
        }
    }

    // -------------------- 过滤事件 --------------------

    private void OnLogLevelFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshTimer == null) return; // 窗口尚未完成初始化
        ReloadLogs();
    }

    private void OnLogSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                ReloadLogs();
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // -------------------- 操作按钮 --------------------

    private async void OnLogCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var item in _items)
        {
            sb.Append(item.Raw.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(item.Raw.Level).Append("] ")
              .AppendLine(LogPrivacy.Redact(item.Raw.Message));
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(sb.ToString());
            StickerToastWindow.ShowAtTop("已将日志复制到剪贴板");
        }
    }

    private async void OnLogExportClick(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出运行日志",
                DefaultExtension = "txt",
                SuggestedFileName = $"yuSnip_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("文本文件 (*.txt)") { Patterns = new[] { "*.txt" } }
                }
            });

            if (file != null)
            {
                var sb = new StringBuilder();
                foreach (var item in _items)
                {
                    sb.Append(item.Raw.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                      .Append(" [").Append(item.Raw.Level).Append("] ")
                      .AppendLine(LogPrivacy.Redact(item.Raw.Message));
                }

                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteAsync(sb.ToString());

                StickerToastWindow.ShowAtTop("日志已成功导出");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LogWindow.OnLogExportClick 导出失败", ex);
        }
    }

    private void OnLogOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = AppLogger.LogDirectory;
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            else
            {
                StickerToastWindow.ShowAtTop("日志文件夹尚未创建");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LogWindow.OnLogOpenFolderClick 失败: " + ex.Message);
        }
    }

    private void OnLogClearClick(object? sender, RoutedEventArgs e)
    {
        _items.Clear();
        UpdateMeta(_renderedSnapshotCount);
        StickerToastWindow.ShowAtTop("已清空当前日志界面");
    }
}
