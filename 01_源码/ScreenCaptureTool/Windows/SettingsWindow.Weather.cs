using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

public partial class SettingsWindow
{
    private OverviewWeatherService? _weatherService;
    private CancellationTokenSource? _weatherCts;
    private DateTimeOffset _weatherRetryAt;
    private string? _weatherError;
    private WeatherRegion? _weatherDraft;
    private bool _weatherPickerLoading;
    private (WeatherRegion Region, Avalonia.Rect Area, long PressedAt)? _weatherSearchClick;
    private IPointer? _weatherSearchConfirmPointer;
    private bool WeatherVisible => _runtimeEnabled && !_overviewClosed && IsVisible
        && WindowState != WindowState.Minimized && _activePage == SettingsPage.Overview;

    private void EnsureOverviewWeather()
    {
        if (!_runtimeEnabled || _weatherService != null) return;
        _weatherService = new(Path.Combine(AppSettings.SettingsDirectory, "overview-weather.json"));
        _weatherService.Load();
        RenderOverviewWeather(DateTimeOffset.Now);
    }
    private void UpdateWeatherLifecycle()
    {
        if (!WeatherVisible)
        {
            _weatherCts?.Cancel();
            if (_overviewClosed || !IsVisible || WindowState == WindowState.Minimized || _activePage != SettingsPage.Overview) CloseWeatherPicker(false);
            return;
        }
        EnsureOverviewWeather();
        _ = RefreshOverviewWeatherAsync();
    }
    private async Task RefreshOverviewWeatherAsync()
    {
        if (!WeatherVisible || _weatherService == null || _weatherCts != null) return;
        var region = WeatherRegions.Find(_weatherService.RegionId);
        var now = DateTimeOffset.UtcNow;
        RenderOverviewWeather(now);
        if (region == null || _weatherService.Cached?.IsFresh(now) == true || now < _weatherRetryAt) return;
        using var cts = new CancellationTokenSource();
        _weatherCts = cts;
        RenderOverviewWeather(now);
        try
        {
            var result = await _weatherService.FetchAsync(region, cts.Token);
            // 取消、切区与关窗均不能让旧请求覆盖当前地区。
            if (cts.IsCancellationRequested || !WeatherVisible || _weatherService.RegionId != region.Id) return;
            _weatherService.Accept(result.Weather, result.Payload);
            _weatherError = null;
            _weatherRetryAt = default;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _weatherService.RegionId == region.Id && !_overviewClosed)
            {
                _weatherError = "天气暂不可用";
                _weatherRetryAt = DateTimeOffset.UtcNow + (ex is WeatherRequestException rate ? rate.RetryAfter : TimeSpan.FromMinutes(5));
                AppLogger.Warn("概览天气请求失败：" + ex.GetType().Name);
            }
        }
        finally
        {
            if (ReferenceEquals(_weatherCts, cts)) _weatherCts = null;
            if (!_overviewClosed)
            {
                RenderOverviewWeather(DateTimeOffset.Now);
                RefreshOverviewClock(DateTime.Now);
                // 短暂隐藏后重新显示，或快速切区时补上被取消的新一轮请求。
                if (cts.IsCancellationRequested && WeatherVisible) _ = RefreshOverviewWeatherAsync();
            }
        }
    }
    private void RenderOverviewWeather(DateTimeOffset now)
    {
        var region = WeatherRegions.Find(_weatherService?.RegionId);
        var weather = _weatherService?.Cached;
        bool usable = region != null && weather?.RegionId == region.Id && weather.IsUsable(now);
        TxtOverviewRegion.Text = region?.Label ?? "选择地区 · 添加天气";
        ToolTip.SetTip(BtnOverviewRegion, region == null ? "选择省、市、区县，开启当地天气" : region.Province + " · " + region.Label + "，点击更换或关闭天气");
        TxtOverviewWeather.IsVisible = region != null;
        TxtOverviewWeather.FontSize = usable ? 20 : 12;
        TxtOverviewWeather.Text = usable ? $"{weather!.Temperature:0}° {OverviewWeather.Description(weather.Code)}"
            : _weatherCts != null ? "正在看看当地的天空…" : _weatherError ?? "等待天气更新";
        TxtWeatherAttribution.IsVisible = region != null;
        TxtWeatherAttribution.Text = usable ? "Open-Meteo · " + weather!.ValidAt.ToString("HH:mm") + (weather.IsFresh(now) ? "" : " · 缓存") : "Open-Meteo";
        ToolTip.SetTip(TxtWeatherAttribution, "模型天气来自 Open-Meteo（CC BY 4.0）；区县代表点不等于精确定位。" + (usable && _weatherError != null ? " 当前显示缓存，更新暂失败。" : ""));
        SkyOverview.Scene = usable ? weather!.Scene : WeatherScene.Clear;
        SkyOverview.WindKmh = usable ? weather!.WindKmh : 0;
        SkyOverview.CloudPercent = usable ? weather!.CloudPercent : 15;
        SkyOverview.Precipitation = usable ? weather!.Precipitation : 0;
    }
    private void ApplyWeatherClock(DateTimeOffset now)
    {
        var weather = _weatherService?.Cached;
        if (weather != null && weather.RegionId == _weatherService!.RegionId && weather.IsUsable(now))
            SkyOverview.Period = weather.PeriodAt(now);
        var brush = this.TryFindResource("SctSky" + SkyOverview.Period + "TextBrush", ActualThemeVariant, out var value) ? value as IBrush : null;
        TxtOverviewWeather.Foreground = brush;
        TxtOverviewRegion.Foreground = brush;
        BtnOverviewRegion.Foreground = brush;
        TxtWeatherAttribution.Foreground = brush;
    }
    private const string WeatherPrivacyHint = "仅查询所选区县的天气，不获取设备定位。";
    private bool WeatherSearching => !string.IsNullOrWhiteSpace(TxtWeatherSearch.Text);
    private void OnWeatherPickerClick(object? sender, RoutedEventArgs e)
    {
        ResetWeatherSearchClick();
        CloseSearchPopup();
        WeatherSearchPopup.IsOpen = false;
        _weatherPickerLoading = true;
        try
        {
            WeatherPickerOverlay.IsVisible = true;
            TxtWeatherSearch.Text = "";
            WeatherSearchResults.ItemsSource = null;
            WeatherSearchResults.IsVisible = false;
            WeatherSearchEmpty.IsVisible = false;
            WeatherCascadeScroll.IsVisible = true;
            BtnWeatherClearSearch.IsVisible = false;
            BtnWeatherDisable.IsVisible = !string.IsNullOrEmpty(_weatherService?.RegionId);
            // 没有已选地区时留空，不能默默默认北京。
            var region = WeatherRegions.Find(_weatherService?.RegionId);
            CmbWeatherProvince.ItemsSource = WeatherRegions.All.Select(r => r.Province).Distinct().ToArray();
            CmbWeatherProvince.SelectedItem = region?.Province;
            PopulateWeatherCities(region);
            UpdateWeatherDraft();
            Dispatcher.UIThread.Post(() => { if (WeatherPickerOverlay.IsVisible) TxtWeatherSearch.Focus(); }, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            _weatherDraft = null;
            BtnWeatherApply.IsEnabled = false;
            TxtWeatherDraft.Text = "地区目录暂不可用";
            TxtWeatherPickerHint.Text = "请关闭面板后重试。";
            AppLogger.Warn("天气地区目录不可用：" + ex.GetType().Name);
        }
        finally { _weatherPickerLoading = false; }
    }
    private void PopulateWeatherCities(WeatherRegion? selected = null)
    {
        var cities = WeatherRegions.All.Where(r => r.Province == CmbWeatherProvince.SelectedItem as string).Select(r => r.City).Distinct().ToArray();
        CmbWeatherCity.ItemsSource = cities;
        CmbWeatherCity.IsEnabled = cities.Length > 0;
        CmbWeatherCity.SelectedItem = selected != null && cities.Contains(selected.City) ? selected.City : cities.FirstOrDefault();
        PopulateWeatherDistricts(selected);
    }
    private void PopulateWeatherDistricts(WeatherRegion? selected = null)
    {
        var regions = WeatherRegions.All.Where(r => r.Province == CmbWeatherProvince.SelectedItem as string && r.City == CmbWeatherCity.SelectedItem as string).ToArray();
        CmbWeatherDistrict.ItemsSource = regions;
        CmbWeatherDistrict.IsEnabled = regions.Length > 0;
        CmbWeatherDistrict.SelectedItem = selected != null ? regions.FirstOrDefault(r => r.Id == selected.Id) : regions.FirstOrDefault();
    }
    private void UpdateWeatherDraft()
    {
        _weatherDraft = WeatherSearching ? WeatherSearchResults.SelectedItem as WeatherRegion : CmbWeatherDistrict.SelectedItem as WeatherRegion;
        BtnWeatherApply.IsEnabled = _weatherDraft != null;
        TxtWeatherDraft.Text = _weatherDraft == null ? (WeatherSearching ? "请选择一个搜索结果" : "请选择一个区县") : "将使用：" + _weatherDraft.FullLabel;
        TxtWeatherPickerHint.Text = WeatherPrivacyHint;
    }
    private void OnWeatherProvinceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_weatherPickerLoading) return;
        _weatherPickerLoading = true;
        try { PopulateWeatherCities(); } finally { _weatherPickerLoading = false; }
        EndWeatherSearch();
    }
    private void OnWeatherCityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_weatherPickerLoading) return;
        _weatherPickerLoading = true;
        try { PopulateWeatherDistricts(); } finally { _weatherPickerLoading = false; }
        EndWeatherSearch();
    }
    private void OnWeatherDistrictChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_weatherPickerLoading) EndWeatherSearch();
    }
    private void OnWeatherSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_weatherPickerLoading || WeatherSearchResults == null || WeatherPickerOverlay?.IsVisible != true) return;
        string query = TxtWeatherSearch.Text?.Trim() ?? "";
        if (query.Length > 0) ResetWeatherSearchClick();
        var matches = query.Length == 0 ? Array.Empty<WeatherRegion>() : WeatherRegions.Search(query);
        _weatherPickerLoading = true;
        try
        {
            WeatherSearchResults.SelectedItem = null;
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            WeatherSearchResults.ItemTemplate = new FuncDataTemplate<WeatherRegion>((region, _) =>
            {
                if (region == null) return null;
                var row = CreateSearchResultRow(region.District, region.ParentLabel, "地区", terms);
                row.Classes.Set("sel", Equals(WeatherSearchResults.SelectedItem, region));
                ToolTip.SetTip(row, region.FullLabel);
                // 单击立即结束搜索；记录屏幕落点，供主窗口接住原位置的第二击。
                bool pressed = false;
                long pressedAt = 0;
                Avalonia.PixelPoint pressPosition = default;
                row.PointerEntered += (_, _) => WeatherSearchResults.SelectedItem = region;
                row.PointerPressed += (_, e) =>
                {
                    pressed = e.GetCurrentPoint(row).Properties.IsLeftButtonPressed;
                    pressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    pressPosition = row.PointToScreen(e.GetPosition(row));
                };
                row.PointerReleased += (_, e) =>
                {
                    bool wasPressed = pressed;
                    pressed = false;
                    if (!wasPressed || e.InitialPressMouseButton != MouseButton.Left
                        || !new Avalonia.Rect(row.Bounds.Size).Contains(e.GetPosition(row))) return;
                    WeatherSearchResults.SelectedItem = region;
                    var tolerance = PlatformSettings!.GetDoubleTapSize(PointerType.Mouse) * RenderScaling;
                    EndWeatherSearch();
                    BtnWeatherApply.Focus();
                    if (e.Pointer.Type == PointerType.Mouse && e.KeyModifiers == KeyModifiers.None)
                        _weatherSearchClick = (region, new Avalonia.Rect(pressPosition.X - tolerance.Width / 2,
                            pressPosition.Y - tolerance.Height / 2, tolerance.Width, tolerance.Height), pressedAt);
                    e.Handled = true;
                };
                return row;
            });
            WeatherSearchResults.ItemsSource = matches;
            WeatherSearchResults.IsVisible = query.Length > 0 && matches.Count > 0;
            WeatherSearchEmpty.IsVisible = query.Length > 0 && matches.Count == 0;
            BtnWeatherClearSearch.IsVisible = query.Length > 0;
            WeatherSearchPopup.IsOpen = query.Length > 0;
        }
        finally { _weatherPickerLoading = false; }
        // 查询变化即撤销旧搜索选择；无结果时禁止套用下方的级联值。
        UpdateWeatherDraft();
        UpdateWeatherSearchSelection();
    }
    private void OnWeatherSearchSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_weatherPickerLoading) return;
        if (WeatherSearchResults.SelectedItem is WeatherRegion region)
        {
            _weatherPickerLoading = true;
            try { CmbWeatherProvince.SelectedItem = region.Province; PopulateWeatherCities(region); }
            finally { _weatherPickerLoading = false; }
        }
        UpdateWeatherDraft();
        UpdateWeatherSearchSelection();
    }
    private void UpdateWeatherSearchSelection()
    {
        int index = WeatherSearchResults.SelectedIndex;
        TxtWeatherSearchHeader.Text = WeatherSearchResults.ItemCount == 0 ? "没有找到这个地区"
            : (index < 0 ? $"{WeatherSearchResults.ItemCount} 项结果" : $"{index + 1} / {WeatherSearchResults.ItemCount}") + "  ·  ↑↓ 选择 · 双击确定";
        foreach (var row in WeatherSearchResults.GetRealizedContainers())
        {
            var surface = row.GetVisualDescendants().OfType<ScreenCaptureTool.Controls.ContinuousBorder>().FirstOrDefault(b => b.Classes.Contains("searchResultRow"));
            surface?.Classes.Set("sel", row is ListBoxItem { IsSelected: true });
        }
    }
    private void OnWeatherSearchGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (WeatherPickerOverlay.IsVisible && WeatherSearching) WeatherSearchPopup.IsOpen = true;
    }
    private void EndWeatherSearch()
    {
        ResetWeatherSearchClick();
        WeatherSearchPopup.IsOpen = false;
        TxtWeatherSearch.Text = "";
        BtnWeatherClearSearch.IsVisible = false;
        UpdateWeatherDraft();
    }
    // 弹层在第一击后已经关闭，第二击会落到窗口。按系统双击时限和距离接续，
    // 在隧道路由中拦截并捕获鼠标，避免误打开原位置下方的省市区下拉。
    private void OnWeatherSearchFollowupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_weatherSearchClick is not { } click) return;
        if (!WeatherPickerOverlay.IsVisible || WeatherSearching || _weatherDraft?.Id != click.Region.Id
            || e.Pointer.Type != PointerType.Mouse || e.KeyModifiers != KeyModifiers.None
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || System.Diagnostics.Stopwatch.GetElapsedTime(click.PressedAt) > PlatformSettings!.GetDoubleTapTime(PointerType.Mouse)
            || !IsInWeatherSearchClick(e, click.Area))
        {
            ResetWeatherSearchClick();
            return;
        }
        _weatherSearchConfirmPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }
    private void OnWeatherSearchFollowupReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_weatherSearchConfirmPointer != e.Pointer) return;
        bool confirm = _weatherSearchClick is { } click && _weatherDraft?.Id == click.Region.Id
            && e.InitialPressMouseButton == MouseButton.Left && IsInWeatherSearchClick(e, click.Area);
        ResetWeatherSearchClick();
        e.Handled = true;
        if (confirm) OnWeatherApplyClick(null, new RoutedEventArgs());
    }
    private bool IsInWeatherSearchClick(PointerEventArgs e, Avalonia.Rect area)
    {
        var point = this.PointToScreen(e.GetPosition(this));
        return area.Contains(new Avalonia.Point(point.X, point.Y));
    }
    private void ResetWeatherSearchClick()
    {
        _weatherSearchClick = null;
        var pointer = _weatherSearchConfirmPointer;
        _weatherSearchConfirmPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
    }
    private void OnWeatherClearSearchClick(object? sender, RoutedEventArgs e) { TxtWeatherSearch.Text = ""; TxtWeatherSearch.Focus(); }
    private void OnWeatherCancelClick(object? sender, RoutedEventArgs e) => CloseWeatherPicker();
    private void OnWeatherBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == WeatherPickerOverlay) { CloseWeatherPicker(); e.Handled = true; }
    }
    private void CloseWeatherPicker(bool restoreFocus = true)
    {
        ResetWeatherSearchClick();
        if (WeatherPickerOverlay?.IsVisible != true) return;
        WeatherSearchPopup.IsOpen = false;
        CmbWeatherProvince.IsDropDownOpen = CmbWeatherCity.IsDropDownOpen = CmbWeatherDistrict.IsDropDownOpen = false;
        WeatherPickerOverlay.IsVisible = false;
        _weatherDraft = null;
        if (restoreFocus && !_overviewClosed && IsVisible) BtnOverviewRegion.Focus();
    }
    private void HandleWeatherPickerKey(KeyEventArgs e)
    {
        ResetWeatherSearchClick();
        if (e.Key == Key.Escape)
        {
            // 让下拉框先消费 Esc，仅关闭自己的列表。
            if (CmbWeatherProvince.IsDropDownOpen || CmbWeatherCity.IsDropDownOpen || CmbWeatherDistrict.IsDropDownOpen) return;
            if (WeatherSearching) { TxtWeatherSearch.Text = ""; TxtWeatherSearch.Focus(); }
            else CloseWeatherPicker();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.K or Key.F)
        {
            TxtWeatherSearch.Focus(); TxtWeatherSearch.SelectAll(); e.Handled = true;
        }
        else if (WeatherSearching && TxtWeatherSearch.IsFocused && e.Key is Key.Down or Key.Up)
        {
            int count = WeatherSearchResults.ItemCount;
            if (count > 0)
            {
                int index = WeatherSearchResults.SelectedIndex;
                WeatherSearchResults.SelectedIndex = e.Key == Key.Down ? (index + 1) % count : (index <= 0 ? count - 1 : index - 1);
                WeatherSearchResults.ScrollIntoView(WeatherSearchResults.SelectedItem!);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && WeatherSearching && (TxtWeatherSearch.IsFocused || WeatherSearchResults.IsKeyboardFocusWithin))
        {
            if (_weatherDraft != null) { EndWeatherSearch(); BtnWeatherApply.Focus(); }
            else TxtWeatherPickerHint.Text = "先选择一个搜索结果，再确认地区。";
            e.Handled = true;
        }
    }
    private void OnWeatherApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_weatherDraft == null || _weatherService == null) return;
        if (_weatherService.RegionId != _weatherDraft.Id)
        {
            _weatherCts?.Cancel();
            _weatherService.Select(_weatherDraft.Id);
            _weatherError = null;
            _weatherRetryAt = default;
        }
        CloseWeatherPicker();
        RenderOverviewWeather(DateTimeOffset.Now);
        RefreshOverviewClock(DateTime.Now);
        _ = RefreshOverviewWeatherAsync();
    }
    private void OnWeatherDisableClick(object? sender, RoutedEventArgs e)
    {
        _weatherCts?.Cancel();
        _weatherService?.Select("");
        _weatherError = null;
        _weatherRetryAt = default;
        CloseWeatherPicker();
        RenderOverviewWeather(DateTimeOffset.Now);
        RefreshOverviewClock(DateTime.Now);
    }
}
