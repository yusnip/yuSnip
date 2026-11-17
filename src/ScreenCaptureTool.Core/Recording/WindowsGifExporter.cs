using ScreenCaptureTool.Platform.Media;

namespace ScreenCaptureTool.Core.Recording;

/// <summary>Windows 母带经系统解码／WIC 转 GIF；固定帧缓冲，失败不覆盖目标文件。</summary>
public static class WindowsGifExporter
{
    public static Task<string?> ExportAsync(string masterPath, string destPath, bool burnProgressBar,
        int fps, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Export(masterPath, destPath, burnProgressBar, Math.Clamp(fps, 1, 24), progress, ct));

    private static string? Export(string masterPath, string destPath, bool burn, int fps,
        IProgress<double>? progress, CancellationToken ct)
    {
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            string destination = Path.GetFullPath(destPath);
            if (string.Equals(Path.GetFullPath(masterPath), destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("GIF 目标文件不能覆盖录制母带");
            staging = Path.Combine(Path.GetDirectoryName(destination)!, ".yusnip-gif-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var reader = new WindowsMediaReader(masterPath))
            {
                var info = reader.Info;
                if (!double.IsFinite(info.Duration) || info.Duration <= 0)
                    throw new InvalidOperationException("视频时长无效");
                long duration = checked((long)Math.Round(info.Duration * 10_000_000));
                long frames = checked((duration * fps + 9_999_999) / 10_000_000);
                byte[] current = new byte[info.FrameBytes], next = new byte[info.FrameBytes];
                byte[] rendered = burn ? new byte[info.FrameBytes] : current;
                if (!reader.ReadVideo(current, out _)) throw new InvalidOperationException("视频没有可导出的画面");
                bool hasNext = reader.ReadVideo(next, out double nextSeconds);
                long nextTime = (long)Math.Round(nextSeconds * 10_000_000);
                using var writer = new WindowsGifWriter(staging, info.Width, info.Height);
                progress?.Report(0);
                int reported = 0;
                for (long frame = 0; frame < frames; frame++)
                {
                    ct.ThrowIfCancellationRequested();
                    long start = frame * 10_000_000 / fps;
                    while (hasNext && nextTime <= start)
                    {
                        ct.ThrowIfCancellationRequested();
                        (current, next) = (next, current);
                        hasNext = reader.ReadVideo(next, out nextSeconds);
                        nextTime = (long)Math.Round(nextSeconds * 10_000_000);
                    }
                    rendered = burn ? rendered : current;
                    if (burn)
                    {
                        Buffer.BlockCopy(current, 0, rendered, 0, current.Length);
                        BurnProgress(rendered, info.Width, info.Height, start / (double)duration);
                    }
                    long end = Math.Min(duration, (frame + 1) * 10_000_000 / fps);
                    // GIF 以 10ms 记时；按累计端点取整，24fps 不会因逐帧固定 40ms 而越播越快。
                    long fromCs = (start + 50_000) / 100_000, toCs = (end + 50_000) / 100_000;
                    writer.WriteFrame(rendered, checked((ushort)Math.Max(1, toCs - fromCs)));
                    int percent = (int)((frame + 1) * 99 / frames);
                    if (percent > reported) { reported = percent; progress?.Report(percent / 100.0); }
                }
                ct.ThrowIfCancellationRequested();
                writer.Finish();
                AppLogger.Info($"Windows GIF 转换：{info.Width}x{info.Height} {frames} 帧 @{fps}fps 进度条={burn}");
            }
            // 先关闭 WIC 文件句柄，再原子替换目标；取消或编码失败只清理本次临时文件。
            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination, true);
            staging = null;
            progress?.Report(1);
            return null;
        }
        catch (OperationCanceledException) { return "已取消"; }
        catch (Exception ex) { return "Windows GIF 转换失败：" + ex.Message; }
        finally
        {
            if (staging != null) try { File.Delete(staging); } catch { }
        }
    }

    private static void BurnProgress(byte[] pixels, int width, int height, double ratio)
    {
        int filled = (int)Math.Floor(width * Math.Clamp(ratio, 0, 1));
        for (int y = Math.Max(0, height - 12); y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                // 与既有导出同规格：12px 底轨 black@0.4，其上覆盖 0x0099FF@0.6。
                bool active = x < filled;
                pixels[p] = (byte)Math.Round(pixels[p] * (active ? .24 : .6) + (active ? 255 * .6 : 0));
                pixels[p + 1] = (byte)Math.Round(pixels[p + 1] * (active ? .24 : .6) + (active ? 153 * .6 : 0));
                pixels[p + 2] = (byte)Math.Round(pixels[p + 2] * (active ? .24 : .6));
                pixels[p + 3] = 255;
            }
    }
}
