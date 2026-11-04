using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.LongScroll.Capture;

/// <summary>独立采集线程：高频 GDI 捕获并写入有界队列。</summary>
public sealed class LongScrollCaptureWorker : IDisposable
{
    private readonly Rectangle _region;
    private readonly int _captureIntervalMs;
    private readonly BlockingCollection<Bitmap> _queue;
    private readonly LongScrollBitmapPool _bitmapPool;
    private readonly CancellationToken _cancellationToken;
    private Thread? _thread;
    private volatile bool _started;
    private volatile bool _disposed;

    public LongScrollCaptureWorker(
        Rectangle region,
        int captureIntervalMs,
        BlockingCollection<Bitmap> queue,
        LongScrollBitmapPool bitmapPool,
        CancellationToken cancellationToken)
    {
        _region = region;
        _captureIntervalMs = Math.Max(1, captureIntervalMs);
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _bitmapPool = bitmapPool ?? throw new ArgumentNullException(nameof(bitmapPool));
        _cancellationToken = cancellationToken;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LongScrollCaptureWorker",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop(int joinTimeoutMs = 800)
    {
        try { _queue.CompleteAdding(); } catch { }
        try
        {
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(Math.Max(1, joinTimeoutMs));
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void Run()
    {
        try
        {
            using var capture = new GdiRegionCapture(_region);
            while (!_cancellationToken.IsCancellationRequested && !_queue.IsAddingCompleted)
            {
                long start = Stopwatch.GetTimestamp();
                Bitmap? bitmap = null;
                try
                {
                    bitmap = _bitmapPool.Rent();
                    if (!capture.TryCapture(bitmap))
                    {
                        _bitmapPool.Return(bitmap);
                        bitmap = null;
                        continue;
                    }

                    EnqueueLatest(bitmap);
                    bitmap = null;
                }
                catch (OperationCanceledException)
                {
                    if (bitmap != null) _bitmapPool.Return(bitmap);
                    break;
                }
                catch (InvalidOperationException) when (_queue.IsAddingCompleted || _cancellationToken.IsCancellationRequested)
                {
                    if (bitmap != null) _bitmapPool.Return(bitmap);
                    break;
                }
                catch (Exception ex)
                {
                    if (bitmap != null) _bitmapPool.Return(bitmap);
                    AppLogger.Warn("LongScrollCaptureWorker.Run: 采集帧失败：" + ex.Message);
                }

                int sleep = _captureIntervalMs - (int)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                if (sleep > 0)
                {
                    if (_cancellationToken.WaitHandle.WaitOne(sleep)) break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LongScrollCaptureWorker.Run 异常", ex);
        }
        finally
        {
            try { _queue.CompleteAdding(); } catch { }
        }
    }

    private void EnqueueLatest(Bitmap bitmap)
    {
        if (_queue.TryAdd(bitmap)) return;

        if (_queue.TryTake(out Bitmap? dropped))
        {
            _bitmapPool.Return(dropped);
        }

        if (_queue.TryAdd(bitmap)) return;

        try
        {
            if (_queue.TryAdd(bitmap, 10, _cancellationToken)) return;
        }
        catch
        {
        }

        _bitmapPool.Return(bitmap);
    }
}
