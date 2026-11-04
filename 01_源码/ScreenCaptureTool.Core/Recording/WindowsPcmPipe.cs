using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace ScreenCaptureTool.Core.Recording;

/// <summary>系统 AAC 解码转 PCM；分块大小与 waveOut 槽位保持一致。</summary>
internal static class WindowsPcmPipe
{
    public static void Read(string path, double at, Channel<byte[]> channel, int chunk, CancellationToken ct,
        Action ended, Action<string> log)
    {
        try
        {
            using var reader = new WindowsMediaReader(path, audio: true);
            reader.Seek(at);
            byte[] block = new byte[chunk];
            int used = 0;
            while (!ct.IsCancellationRequested && reader.Read((data, length, time) =>
            {
                // Seek 可能退回关键帧之前；按 PCM 帧边界裁掉跳转点之前的声音。
                int skip = (int)Math.Min(length, Math.Max(0, (long)((at * 10_000_000 - time) * 48000 / 10_000_000)) * 4);
                int offset = skip;
                while (offset < length)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = Math.Min(chunk - used, length - offset);
                    Marshal.Copy(data + offset, block, used, count);
                    used += count; offset += count;
                    if (used == chunk)
                    {
                        channel.Writer.WriteAsync(block, ct).AsTask().GetAwaiter().GetResult();
                        block = new byte[chunk]; used = 0;
                    }
                }
            })) { }
            if (!ct.IsCancellationRequested && used > 0)
                channel.Writer.WriteAsync(block, ct).AsTask().GetAwaiter().GetResult();
            if (!ct.IsCancellationRequested) ended();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("Windows 音频解码失败：" + ex.Message); }
        finally { channel.Writer.TryComplete(); }
    }
}
