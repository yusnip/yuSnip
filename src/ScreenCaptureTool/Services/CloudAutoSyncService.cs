using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.CloudSync;

namespace ScreenCaptureTool.Services;

/// <summary>应用级调度器：保存后防抖，启动/周期/联网补查，退出只取消网络。</summary>
internal sealed class CloudAutoSyncService : IDisposable
{
    private readonly CloudSyncEngine _engine;
    private readonly Func<bool> _canApply;
    private readonly Func<CloudSnapshot, string, bool, Task<bool>> _apply;
    private readonly DispatcherTimer _timer;
    private readonly FileSystemWatcher _watcher;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _nextCheck = DateTimeOffset.UtcNow.AddSeconds(2), _lastTick = DateTimeOffset.UtcNow;
    private DateTimeOffset? _firstDirty, _lastDirty;
    private string _stamp = "", _lastHash = "";
    private int _fileChanged = 1, _failures;
    private bool _busy, _authPaused;
    public event Action? Changed;
    public CloudSyncReport Report { get; private set; } = new(CloudSyncState.Disabled, "自动同步已关闭。");

    public CloudAutoSyncService(Func<bool> canApply, Func<CloudSnapshot, string, bool, Task<bool>> apply)
    {
        _canApply = canApply; _apply = apply;
        _engine = new CloudSyncEngine(AppSettings.SettingsDirectory);
        _watcher = new FileSystemWatcher(AppSettings.SettingsDirectory) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += OnFileChanged; _watcher.Created += OnFileChanged; _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged; _watcher.Error += (_, _) => Interlocked.Exchange(ref _fileChanged, 1);
        _watcher.EnableRaisingEvents = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick; _timer.Start();
    }
    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (Array.IndexOf(CloudSnapshot.Files, args.Name) >= 0) Interlocked.Exchange(ref _fileChanged, 1);
    }
    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (args.IsAvailable) Dispatcher.UIThread.Post(() => { if (!_stop.IsCancellationRequested && !_authPaused) _nextCheck = DateTimeOffset.UtcNow; });
    }
    private static string SettingsStamp(AppSettings s) => CloudSnapshot.Digest(string.Join("\n", s.CloudAutoSync, s.CloudSyncProvider, s.CloudSyncSecrets,
        s.WebDavUrl, s.WebDavUsername, s.WebDavPassword, s.WebDavFileName, s.GoogleDriveUserEmail, s.GoogleDriveClientId,
        s.GoogleDriveClientSecret, s.GoogleDriveRefreshToken));
    public void RequestCheck()
    {
        if (_stop.IsCancellationRequested) return;
        _authPaused = false; _nextCheck = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _fileChanged, 1);
    }
    private void SetReport(CloudSyncReport report) { Report = report; Changed?.Invoke(); }
    private static ICloudRevisionTransport CreateTransport(AppSettings settings, GoogleDriveSyncService? google = null)
        => settings.CloudSyncProvider == "gdrive"
            ? new GoogleDriveRevisionTransport(settings.GoogleDriveUserEmail, settings.GoogleDriveClientId,
                google != null ? google.GetAutomaticSyncTokenAsync : _ => Task.FromResult(""))
            : new WebDavRevisionTransport(settings.WebDavUrl, settings.WebDavUsername, settings.WebDavPassword, settings.WebDavFileName);
    /// <summary>时间来自当前目标账本，关闭自动同步或切换账号不会串用上一目标的时间。</summary>
    public DateTimeOffset? GetLastSuccess()
    {
        var settings = AppSettings.Current;
        if (settings.CloudSyncProvider == "none") return null;
        try { return _engine.ReadLastSuccess(CreateTransport(settings).TargetIdentity); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException or InvalidOperationException or FormatException or System.Collections.Generic.KeyNotFoundException or CloudTransportException)
        { return null; }
    }
    public async Task<CloudSyncHistory> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        var settings = AppSettings.Current;
        if (settings.CloudSyncProvider == "none") throw new InvalidOperationException("请先选择并连接云存储服务。");
        string stamp = SettingsStamp(settings);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        using var google = settings.CloudSyncProvider == "gdrive" ? new GoogleDriveSyncService(settings) : null;
        ICloudRevisionTransport transport = CreateTransport(settings, google);
        return await _engine.ReadHistoryAsync(transport,
            () => CloudSnapshot.Capture(AppSettings.SettingsDirectory, AppSettings.Current.CloudSyncSecrets),
            () => !_stop.IsCancellationRequested && SettingsStamp(AppSettings.Current) == stamp, cancellation.Token);
    }
    private async void OnTick(object? sender, EventArgs args)
    {
        if (_busy || _stop.IsCancellationRequested) return;
        try
        {
            var settings = AppSettings.Current; var now = DateTimeOffset.UtcNow;
            string stamp = SettingsStamp(settings);
            if (stamp != _stamp)
            {
                _stamp = stamp; _authPaused = false; _failures = 0;
                _firstDirty = _lastDirty = null; _lastHash = ""; _nextCheck = now;
                Interlocked.Exchange(ref _fileChanged, 1);
                SetReport(new(CloudSyncState.Checking, "同步目标或范围已变化，等待检查。", GetLastSuccess()));
            }
            if (!settings.CloudAutoSync || settings.CloudSyncProvider == "none")
            {
                var success = GetLastSuccess();
                if (Report.State != CloudSyncState.Disabled || Report.LastSuccess != success)
                    SetReport(new(CloudSyncState.Disabled, "自动同步已关闭。", success));
                _lastTick = now; return;
            }
            if (now - _lastTick > TimeSpan.FromMinutes(1)) _nextCheck = now;
            _lastTick = now;
            if (Interlocked.Exchange(ref _fileChanged, 0) == 1)
            {
                string hash = CloudSnapshot.Capture(AppSettings.SettingsDirectory, settings.CloudSyncSecrets).Hash;
                if (_lastHash.Length > 0 && hash != _lastHash)
                {
                    _firstDirty ??= now; _lastDirty = now;
                    if (Report.State is not (CloudSyncState.Conflict or CloudSyncState.AuthenticationRequired))
                        SetReport(new(CloudSyncState.PendingUpload, "配置已保存，等待自动同步。", Report.LastSuccess));
                }
                _lastHash = hash;
            }
            bool dirtyDue = _lastDirty != null && (now - _lastDirty >= TimeSpan.FromSeconds(10) || now - _firstDirty >= TimeSpan.FromSeconds(60));
            if (!_authPaused && (now >= _nextCheck || dirtyDue || (Report.State == CloudSyncState.PendingApply && _canApply())))
                await SynchronizeAsync();
        }
        catch (Exception ex) { Failure(ex); }
    }
    public async Task SynchronizeAsync(CloudSyncChoice? choice = null)
    {
        if (_busy || _stop.IsCancellationRequested) return;
        var settings = AppSettings.Current;
        // 显式版本选择是一次手动操作，不需要开启后台自动同步；同样执行目标和版本令牌校验。
        if ((choice == null && !settings.CloudAutoSync) || settings.CloudSyncProvider == "none") return;
        _busy = true; string stamp = SettingsStamp(settings);
        var elapsed = Stopwatch.StartNew();
        var previous = stamp == _stamp ? Report : new CloudSyncReport(CloudSyncState.Disabled, "同步目标已变化。", GetLastSuccess());
        SetReport(new(CloudSyncState.Checking, "正在检查本机与云端配置…", previous.LastSuccess));
        try
        {
            // 单个网络请求由传输层限时，整轮迁移、读取与历史清理不合用 30 秒预算。
            using var google = settings.CloudSyncProvider == "gdrive" ? new GoogleDriveSyncService(settings) : null;
            ICloudRevisionTransport transport = CreateTransport(settings, google);
            var result = await _engine.RunAsync(transport,
                () => CloudSnapshot.Capture(AppSettings.SettingsDirectory, AppSettings.Current.CloudSyncSecrets),
                _apply, () => !_stop.IsCancellationRequested && SettingsStamp(AppSettings.Current) == stamp, _stop.Token, choice);
            SetReport(result); _failures = 0; _authPaused = false;
            _lastHash = CloudSnapshot.Capture(AppSettings.SettingsDirectory, AppSettings.Current.CloudSyncSecrets).Hash;
            _firstDirty = _lastDirty = result.State == CloudSyncState.PendingUpload ? DateTimeOffset.UtcNow : null;
            _nextCheck = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Report = previous; Failure(ex, elapsed.Elapsed); }
        // 等待多次网络请求不等于系统休眠，避免长同步结束后误判唤醒而跳过退避。
        finally { _lastTick = DateTimeOffset.UtcNow; _busy = false; }
    }
    private void Failure(Exception ex, TimeSpan? elapsed = null)
    {
        if (_stop.IsCancellationRequested) return;
        _authPaused = ex is CloudTransportException { AuthFailed: true };
        _failures++;
        _nextCheck = DateTimeOffset.UtcNow.AddSeconds(_failures == 1 ? 30 : _failures == 2 ? 60 : 300);
        _firstDirty = _lastDirty = null;
        SetReport(new(_authPaused ? CloudSyncState.AuthenticationRequired : CloudSyncState.Retry,
            _authPaused ? "需要重新连接：" + ex.Message : (AppSettings.Current.CloudAutoSync ? "同步未完成，将自动重试：" : "使用版本未完成，请重试：")
                + (ex is OperationCanceledException ? "请求超时。" : ex.Message), Report.LastSuccess));
        AppLogger.Warn("自动云同步：" + (_authPaused ? "需要重新连接" : "等待重试") + "（" + ex.GetType().Name + "）"
            + (ex is CloudRequestTimeoutException ? " " + ex.Message : "")
            + (ex is CloudTransportException { StatusCode: { } status } transport ? $" HTTP={status} reason={transport.Reason ?? "unknown"}" : "")
            + (elapsed.HasValue ? $" 本轮耗时={elapsed.Value.TotalSeconds:0.0}s" : ""));
    }
    public void Dispose()
    {
        _stop.Cancel(); _timer.Stop(); _timer.Tick -= OnTick; _watcher.Dispose();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }
}
