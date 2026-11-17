using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ScreenCaptureTool.Services;

/// <summary>只观察 Quicker 实例更替；每次新实例至多请求一次监听刷新，不周期性争抢钩子。</summary>
internal sealed class KeyboardPeerStartupMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<int[]> _readPeers;
    private readonly Func<long> _clock;
    private readonly Timer _timer;
    private HashSet<int> _known;
    private bool _enabled, _disposed, _polling;
    private long _version, _serial, _pending, _notBefore;

    internal KeyboardPeerStartupMonitor(Func<int[]>? readPeers = null, Func<long>? clock = null)
    {
        _readPeers = readPeers ?? ReadPeers;
        _clock = clock ?? (() => Environment.TickCount64);
        // 初始快照发生在本方安装钩子之前；已存在的 Quicker 不要求再次调整顺序。
        try { _known = new(_readPeers()); }
        catch { _known = []; }
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled; _version++;
            if (!enabled) _pending = 0;
            _timer.Change(enabled ? 0 : Timeout.Infinite, enabled ? 1000 : Timeout.Infinite);
        }
    }

    private void Poll()
    {
        long version;
        lock (_gate) { if (!_enabled || _disposed || _polling) return; version = _version; _polling = true; }
        try
        {
            int[] peers = _readPeers();
            lock (_gate)
                if (_enabled && !_disposed && version == _version) Observe(peers, _clock());
        }
        catch { } // 查询失败不当作对方退出或重新启动。
        finally { lock (_gate) _polling = false; }
    }

    internal void Observe(int[] peers, long now)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var current = new HashSet<int>(peers);
            if (current.Any(id => !_known.Contains(id)))
            {
                _pending = ++_serial;
                // 进程出现不等于其钩子已经就绪，给启动留出延迟；此后仍须等待本方输入空闲。
                _notBefore = now + 2000;
            }
            if (current.Count == 0) _pending = 0;
            _known = current;
        }
    }

    internal long ReadyRequest(long now)
    {
        lock (_gate) return !_disposed && _pending != 0 && now >= _notBefore ? _pending : 0;
    }

    internal void Complete(long request)
    {
        lock (_gate) { if (_pending == request) _pending = 0; }
    }

    private static int[] ReadPeers()
    {
        var processes = Process.GetProcessesByName("Quicker");
        try { return processes.Select(process => process.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _pending = 0; _timer.Dispose();
        }
    }
}
