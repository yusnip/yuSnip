using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.CloudSync;
using ScreenCaptureTool.Services;
using ScreenCaptureTool.Controls;

namespace ScreenCaptureTool.Windows;

/// <summary>保留的云端历史与冲突选择共用入口。界面上没有示例数据入口，<see cref="LoadExampleScenario"/> 只给离屏渲染探针当数据源。</summary>
public partial class CloudVersionsWindow : Window
{
    private sealed record VersionRow(CloudRevision? Revision, CloudSnapshot Snapshot, bool Head);
    /// <summary>组头与数据行共用的唯一一份列定义（版本 / 设备 / 四段差异）。改这里两处一起动。</summary>
    private const string VersionColumns = "236,132,*,*,*,*";
    private readonly CloudAutoSyncService? _service;
    private readonly Action? _saveSettings;
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _query;
    private CloudSyncHistory? _history;
    private CloudSyncReport _report = new(CloudSyncState.Disabled, "自动同步已关闭。");
    private VersionRow? _selected;
    private bool _preview, _loading, _applying;
    private Point? _placementCenterPx;
    private bool _placing;
    private string? _historyError, _actionMessage;
    private static readonly string[] Scenarios = ["同步正常", "版本冲突", "连接失败", "没有云端记录", "等待应用"];

    // 无参构造用于离屏渲染，不读取用户配置或访问云端。
    public CloudVersionsWindow()
    {
        InitializeComponent();
        HdrVersions.ColumnDefinitions = new ColumnDefinitions(VersionColumns);
        Closed += (_, _) => { _closed.Cancel(); if (_service != null) _service.Changed -= OnReportChanged; };
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && !_applying) { e.Handled = true; Close(); } }, RoutingStrategies.Tunnel);
        Closing += (_, e) => { if (_applying) e.Cancel = true; };
        Opened += (_, _) =>
        {
            // Opened 当帧的 SizeToContent 高度仍可能未收敛，中心取稳定的所属窗口／工作区。
            if (Owner is Window owner)
                _placementCenterPx = new Point(owner.Position.X + owner.Bounds.Width * owner.RenderScaling / 2,
                    owner.Position.Y + owner.Bounds.Height * owner.RenderScaling / 2);
            else if (Screens.ScreenFromWindow(this) is { } screen)
                _placementCenterPx = new Point(screen.WorkingArea.X + screen.WorkingArea.Width / 2.0,
                    screen.WorkingArea.Y + screen.WorkingArea.Height / 2.0);
            FitListHeight();
            KeepPlacementCentered();
        };
        SizeChanged += (_, _) => KeepPlacementCentered();
        LayoutUpdated += (_, _) => FitListHeight();
        PositionChanged += (_, _) =>
        {
            // 用户移动后尊重其位置；内容加载和刷新不再把窗口拉回中心。
            if (!_placing) _placementCenterPx = null;
            FitListHeight();
        };
    }
    /// <summary>SizeToContent 加载后变高时保持首次居中的中心，按实际尺寸而非预估高度摆位。</summary>
    private void KeepPlacementCentered()
    {
        if (_placementCenterPx is not { } center || Bounds.Height <= 0) return;
        double width = Bounds.Width * RenderScaling, height = Bounds.Height * RenderScaling;
        double x = center.X - width / 2, y = center.Y - height / 2;
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea;
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        }
        var target = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        if (target == Position) return;
        _placing = true;
        try { Position = target; }
        finally { _placing = false; }
    }
    /// <summary>窗口高度改成内容驱动，所以"最多占多少屏"必须钉在会滚的那一块上、而不是钉在窗口上。</summary>
    private void FitListHeight()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen != null) FitListHeight(screen.WorkingArea.Height / screen.Scaling);
    }
    private void FitListHeight(double workingHeight)
    {
        // 按实际布局扣除标题、状态、表头、说明和底栏；长错误文案或确认态换行也纳入预算。
        double chromeHeight = LayoutRoot.Bounds.Height - LstVersions.Bounds.Height;
        if (chromeHeight <= 0) return;
        double limit = Math.Max(0, Math.Min(workingHeight * 0.55, workingHeight - chromeHeight - 32));
        if (Math.Abs(LstVersions.MaxHeight - limit) > 0.5) LstVersions.MaxHeight = limit;
    }
    internal CloudVersionsWindow(CloudAutoSyncService? service, Action saveSettings) : this()
    {
        _service = service; _saveSettings = saveSettings;
        if (_service != null) _service.Changed += OnReportChanged;
        Opened += async (_, _) => await RefreshAsync();
    }
    private void OnReportChanged()
    {
        if (_preview || _closed.IsCancellationRequested) return;
        _report = _service!.Report; RenderStatus(); UpdateAction();
    }
    private async Task RefreshAsync()
    {
        if (_loading || _applying || _closed.IsCancellationRequested) return;
        // 🔴 读取期间不清空表格：窗口高度由内容决定（SizeToContent），一清空就塌成只剩表头那一小截、
        // 数据到了再撑回去，每次刷新都看得见整窗抖一下（探针量得：6 行时 481 高、清空后 253）。
        // 旧记录留着、主钮灭着，等新数据到了由 RenderVersions 一次性换掉。
        _preview = false;
        _historyError = null;
        _loading = true; ShowConfirmChoice(false); UpdateAction(); RenderStatus();
        TxtNote.Text = "正在读取云端记录…";
        using var query = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _query = query;
        try
        {
            _saveSettings?.Invoke();
            if (_service == null) throw new InvalidOperationException("自动同步服务未就绪。");
            var history = await _service.ReadHistoryAsync(query.Token);
            if (query.IsCancellationRequested || _preview) return;
            _history = history;
            _report = _service.Report;
            RenderVersions(); RenderStatus();
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (query.IsCancellationRequested || _preview || !ReferenceEquals(_query, query)) return;
            _history = null; RenderVersions();   // 表脚那句「暂时无法读取云端记录」由 RenderVersions 统一写
            _historyError = ex is OperationCanceledException ? "读取超时，请重试。" : "读取未完成：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_query, query)) { _query = null; _loading = false; UpdateAction(); RenderStatus(); }
        }
    }
    private void RenderStatus()
    {
        if (_applying)
        {
            TxtStatus.Text = "正在应用所选配置，请稍候…";
            TxtMode.Text = "完成前请保持窗口打开。"; return;
        }
        if (_loading)
        {
            TxtStatus.Text = "正在读取云端版本…";
            TxtMode.Text = "仅查询历史，不会上传或恢复配置。"; return;
        }
        if (_historyError != null)
        {
            TxtStatus.Text = _historyError;
            TxtMode.Text = _preview ? "示例数据 · 不修改本机或云端。" : "刷新记录后再选择版本。"; return;
        }
        if (_actionMessage != null)
        {
            TxtStatus.Text = _actionMessage;
            TxtMode.Text = $"保留最近 {CloudRevision.HistoryLimit} 个版本 · 旧历史自动清理。"; return;
        }
        TxtStatus.Text = _report.Message;
        if (_report.State == CloudSyncState.Synced && _report.LastSuccess is { } success)
            TxtStatus.Text = "已同步 · " + success.LocalDateTime.ToString("MM-dd HH:mm");
        if (_report.State == CloudSyncState.Disabled && _history != null) TxtStatus.Text = "云端版本记录已加载 · 自动同步已关闭。";
        TxtMode.Text = _preview ? "示例数据 · 所有操作仅演示，不修改本机或云端。"
            : _report.State == CloudSyncState.Conflict ? "分叉未解决时保留双方历史，解决后自动清理旧版本。"
            : $"保留最近 {CloudRevision.HistoryLimit} 个版本 · 恢复会发布新版本。";
    }
    private void RenderVersions()
    {
        ShowConfirmChoice(false);
        var rows = new List<VersionRow>();
        if (_history != null)
        {
            rows.Add(new(null, _history.Local, false));
            foreach (var revision in _history.Revisions)
                rows.Add(new(revision, revision.Snapshot.ForPolicy(_history.Local.IncludeSecrets && revision.Snapshot.IncludeSecrets), _history.Heads.Contains(revision.Id)));
        }
        var items = new List<ListBoxItem>(rows.Count);
        if (_history != null)
            for (int i = 0; i < rows.Count; i++) items.Add(CreateRow(rows[i], _history.Local, i == rows.Count - 1));
        _selected = null;
        // 重建表后一律回到「未选中」：Avalonia 会按索引把旧选中带进新表，那等于替用户预先挑了一行
        LstVersions.ItemsSource = items;
        LstVersions.SelectedIndex = -1;
        TxtNote.Text = _history == null ? "暂时无法读取云端记录"
            : _history.Revisions.Count == 0 ? "云端还没有记录"
            : "云端 " + _history.Revisions.Count + " 条 · 本机置顶、按发布时间排列";
    }
    /// <summary>四段差异在建表时一次算完、直接落在该行的四个格子上（判据与旧版逐字相同，只是从"选中才算"提前到"全算"）。</summary>
    private static string[] DiffCells(CloudSnapshot local, CloudSnapshot incoming, bool isLocalRow)
    {
        if (isLocalRow) return ["基准", "基准", "基准", "基准"];
        var a = CloudSnapshot.ParseSettings(local.Content[0]); var b = CloudSnapshot.ParseSettings(incoming.Content[0]);
        int different = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Count(key => !a.TryGetValue(key, out var av) || !b.TryGetValue(key, out var bv) || av != bv);
        return [different == 0 ? "一致" : different + " 项不同",
            Segment(1), Segment(2), Segment(3)];
        string Segment(int index) => local.Content[index] == incoming.Content[index] ? "一致" : "内容不同";
    }
    private ListBoxItem CreateRow(VersionRow row, CloudSnapshot local, bool last)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = row.Revision == null ? "当前本机配置" : FormatTime(row.Revision), Classes = { "versionTime" } });
        // 徽标只标两处例外（本机、当前云端）；「历史」是这一列的常态，戴牌等于不携带信息，
        // 而全局只有徽标紫一档，三种状态同色更读不出差别。
        if (row.Revision == null || row.Head)
            title.Children.Add(new ContinuousBorder
            {
                Classes = { "badge" }, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = row.Revision == null ? "本机" : "当前云端", Classes = { "badgeText" } }
            });

        var device = row.Revision?.Device ?? "此设备";
        var deviceText = new TextBlock { Text = device, Classes = { "versionDevice" } };
        ToolTip.SetTip(deviceText, device);
        // 版本 ID 那串十六进制不是用来比较的信息，从明面收进 ToolTip 与右键菜单
        ToolTip.SetTip(title, row.Revision == null ? "此设备当前保存的四份共享配置" : FormatTime(row.Revision) + "\n" + device + "\n版本 " + row.Revision.Id);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(VersionColumns), Margin = new Thickness(14, 0) };
        grid.Children.Add(title);
        grid.Children.Add(At(deviceText, 1));
        var diff = DiffCells(local, row.Snapshot, row.Revision == null);
        for (int i = 0; i < diff.Length; i++)
            grid.Children.Add(At(new TextBlock { Text = diff[i], Classes = { diff[i] is "一致" or "基准" ? "diffSame" : "diffDiff" } }, 2 + i));
        static T At<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }

        // 行底发丝线走样式类（含 .last 去线），代码里不造画刷——§2「C# 无画刷原则」
        var line = new Border { Child = grid, Classes = { "versionRow" } };
        if (last) line.Classes.Add("last");
        var item = new ListBoxItem { Content = line, Tag = row };
        if (row.Revision != null)
        {
            string id = row.Revision.Id;
            var copy = new MenuItem { Header = "复制版本号" };
            copy.Click += async (_, _) => await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(id) ?? Task.CompletedTask);
            item.ContextMenu = new ContextMenu { Items = { copy } };
        }
        return item;
    }
    private static string FormatTime(CloudRevision revision) => revision.PublishedAt == DateTimeOffset.MinValue ? "旧版手动备份" : revision.PublishedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    /// <summary>差异读数已在建表时逐行算好，选中只做一件事：决定主按钮要对哪一行动手。</summary>
    private void OnVersionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        _selected = (LstVersions.SelectedItem as ListBoxItem)?.Tag as VersionRow;
        ShowConfirmChoice(false);
        UpdateAction();
    }
    private void UpdateAction()
    {
        bool busy = _loading || _applying || (!_preview && _report.State == CloudSyncState.Checking);
        BtnRefresh.IsEnabled = !_loading && !_applying;
        PnlRefresh.IsEnabled = BtnRefresh.IsEnabled;
        LstVersions.IsEnabled = !_applying;
        BtnConfirm.IsEnabled = !busy;
        BtnCancel.IsEnabled = !_applying;
        PnlCancel.IsEnabled = BtnCancel.IsEnabled;
        PnlConfirmAction.IsEnabled = BtnConfirm.IsEnabled;
        BtnClose.IsEnabled = !_applying;
        BtnConfirm.Content = _applying ? "应用中…" : "确认使用";
        BtnUseVersion.Content = _selected == null ? "请选择版本" : _selected.Revision == null ? "使用本机版本" : _selected.Head ? "使用此版本" : "恢复此版本";
        bool enabled = _preview || _service != null;
        bool localChoice = _selected?.Revision == null;
        bool localCanResolve = _report.State == CloudSyncState.Conflict || (_history != null && _history.Revisions.Any(r => _history.Heads.Contains(r.Id)
            && r.Snapshot.ForPolicy(_history.Local.IncludeSecrets && r.Snapshot.IncludeSecrets).Hash != _history.Local.Hash));
        bool useful = _selected != null && _history != null && (localChoice ? localCanResolve
            : _selected.Snapshot.Hash != _history.Local.Hash || _report.State == CloudSyncState.Conflict);
        BtnUseVersion.IsEnabled = !busy && enabled && useful;
        PnlUseVersion.IsEnabled = BtnUseVersion.IsEnabled;
        TxtSelectionHint.Text = _preview ? "示例数据：操作只演示确认与结果，不会执行真实同步。"
            : !enabled ? "云同步服务未就绪，请重启应用后重试。"
            : _selected == null ? "在上方表格里选择一行记录。"
            : localChoice && !localCanResolve ? "当前本机配置已保留，无需重复发布。"
            : !useful ? "此版本与本机配置一致。"
            : "将整份使用所选配置，当前云端连接与设备路径保留本机。";
    }
    private void OnUseVersionClick(object? sender, RoutedEventArgs e)
    {
        if (!BtnUseVersion.IsEnabled || _selected == null) return;
        _actionMessage = null;
        // 「会发布新修订」已经常驻在标题下的说明行，这里只问一句，不在两处各说一遍
        TxtConfirm.Text = _selected.Revision == null
            ? "确认使用当前本机配置？"
            : "确认使用「" + FormatTime(_selected.Revision) + "」的配置？";
        ShowConfirmChoice(true);
    }
    /// <summary>确认态与常态在底栏同一格原地互换：主按钮与选择提示让位给「取消→确认使用」和那一句问话。</summary>
    private void ShowConfirmChoice(bool confirming)
    {
        PnlConfirm.IsVisible = confirming;
        PnlUseVersion.IsVisible = !confirming;
        TxtSelectionHint.IsVisible = !confirming;
        TxtConfirm.IsVisible = confirming;
    }
    private async void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || _history == null || _applying || !BtnUseVersion.IsEnabled) return;
        if (_preview)
        {
            ShowConfirmChoice(false);
            TxtStatus.Text = "示例：所选配置已应用，并生成新版本。"; return;
        }
        if (_service == null) return;
        var choice = new CloudSyncChoice(_history.ChoiceToken, _selected.Revision?.Id);
        _applying = true; UpdateAction(); RenderStatus();
        try
        {
            _saveSettings?.Invoke();
            await _service.SynchronizeAsync(choice);
            _report = _service.Report;
            _actionMessage = _report.Message;
            ShowConfirmChoice(false);
        }
        catch (Exception ex) { _actionMessage = "使用版本未完成：" + ex.Message; }
        finally { _applying = false; UpdateAction(); RenderStatus(); }
        await RefreshAsync();
    }
    private void OnCancelChoiceClick(object? sender, RoutedEventArgs e) { if (!_applying) ShowConfirmChoice(false); }
    private async void OnRefreshClick(object? sender, RoutedEventArgs e) { _actionMessage = null; await RefreshAsync(); }
    /// <summary>
    /// 用内存示例数据填满整扇窗，供离屏渲染探针在不连云账号、不制造真实异常的前提下看到五种状态。
    /// 界面上没有任何入口调用它；<c>_preview</c> 为真时所有出口都只演示、不写盘、不访问云端。
    /// </summary>
    public void LoadExampleScenario(string scenario)
    {
        if (_loading || _applying || !Scenarios.Contains(scenario)) return;
        _preview = true;
        _historyError = _actionMessage = null;
        var local = new CloudSnapshot(["ThemeMode=Dark\nCaptureCursor=true\nAiModel=demo-model\n", "", "", ""], false);
        var records = new List<CloudRevision>();
        var now = new DateTimeOffset(2026, 10, 9, 14, 30, 0, TimeSpan.FromHours(8));
        for (int i = 0; i < 5; i++)
        {
            var snapshot = i == 0 && scenario is not ("版本冲突" or "等待应用") ? local
                : new CloudSnapshot(["ThemeMode=Light\nCaptureCursor=false\nAiModel=demo-model-" + i + "\n", "", "", ""], false);
            records.Add(new((i + 1).ToString("x32"), [], i % 2 == 0 ? "工作电脑 · 示例设备" : "家用电脑 · 示例设备", now.AddHours(-i * 3), snapshot));
        }
        bool empty = scenario == "没有云端记录", failed = scenario == "连接失败";
        _history = failed ? null : new(local, empty ? [] : records, empty ? [] : records.Take(scenario == "版本冲突" ? 2 : 1).Select(r => r.Id).ToArray(), "preview");
        _report = scenario switch
        {
            "版本冲突" => new(CloudSyncState.Conflict, "本机与云端存在不同修改，请选择要使用的版本。"),
            "连接失败" => new(CloudSyncState.Retry, "无法连接云端，请检查网络后刷新记录。"),
            "没有云端记录" => new(CloudSyncState.Synced, "云端暂无历史，首次同步后会在这里留下记录。"),
            "等待应用" => new(CloudSyncState.PendingApply, "云端更新已就绪，关闭设置后将自动应用。"),
            _ => new(CloudSyncState.Synced, "配置已同步。", now)
        };
        if (failed) _historyError = _report.Message;
        RenderVersions(); RenderStatus(); UpdateAction();
    }
    private void OnCloseClick(object? sender, RoutedEventArgs e) { if (!_applying) Close(); }
    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _placementCenterPx = null;
            try { BeginMoveDrag(e); } catch { }
        }
    }
}
