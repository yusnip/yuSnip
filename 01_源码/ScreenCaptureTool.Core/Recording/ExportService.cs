using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// 导出/转码服务（计划 §3.11，稿 3 D16/D17/D18）：录制引擎永远产 MP4 母带，
    /// Windows 母带可用系统组件转 GIF；WebP、其他 GIF 与烙条 MP4 由 ffmpeg 从母带转码（不重录）。
    /// 滤镜链逐字移植参考工程 RecorderHelper :14019（palettegen/paletteuse）、:14032（WebP setpts）、
    /// :13958-13968（进度条，见 <see cref="BuildBarGraph"/>：12px 高、通栏、贴死底边、black@0.4 轨＋0x0099FF@0.6 进度）。
    /// <para>🔴 进度条**不能**用"两条 drawbox＋第二条 <c>w='iw*t/时长'</c>"实现：drawbox 只对 x/y 逐帧求值，
    /// w/h 初始化时算一次就不再动，那样烙出来是一条恒满的条、进度头从不出现。本文件此前正是这个写法，
    /// 并且注释里写着"drawbox 表达式逐帧求值，天然动画"——那句是错的判据，2026-09-24 抽三帧采样底行才实测出来。</para>
    /// <para>进度条与 GIF/WebP 是同一条滤镜图里串起来跑的（一遍编码、无中间文件），所以三种格式都带条。</para>
    /// </summary>
    public static class ExportService
    {
        public static string ExtensionFor(RecordFormat f) => f switch
        {
            RecordFormat.Gif => ".gif",
            RecordFormat.WebP => ".webp",
            _ => ".mp4",
        };

        /// <summary>MP4 且未勾烙印 = 母带原样搬运（合并时已 faststart），不走 ffmpeg。</summary>
        public static bool IsPassthroughMp4(RecordFormat format, bool burnProgressBar)
            => format == RecordFormat.Mp4 && !burnProgressBar;

        /// <summary>依赖下载与实际导出共用路由；Windows GIF 包含烙进度条，不需要 FFmpeg。</summary>
        public static bool RequiresFfmpeg(RecordFormat format, bool burnProgressBar, bool useWindowsGif)
            => !IsPassthroughMp4(format, burnProgressBar) && !(format == RecordFormat.Gif && useWindowsGif);

        public static double ProbeDuration(string masterPath, string? manualFfmpegPath = null)
        {
            string? ffmpeg = FfmpegLocator.TryFind(manualFfmpegPath);
            return ffmpeg != null ? FfmpegProcess.ProbeDurationSeconds(ffmpeg, masterPath) : 0;
        }

        /// <summary>
        /// 母带 → 目标格式转码。<paramref name="progress"/> 收 0..1（ffmpeg -progress 的 out_time/时长）。
        /// 返回 null = 成功；否则为错误文本（含 ffmpeg stderr 尾部）。
        /// </summary>
        public static async Task<string?> ExportAsync(
            string masterPath, string destPath, RecordFormat format, bool burnProgressBar,
            string? presetId, IProgress<double>? progress = null,
            string? manualFfmpegPath = null, CancellationToken ct = default, bool useWindowsGif = false)
        {
            if (IsPassthroughMp4(format, burnProgressBar))
            {
                try { File.Copy(masterPath, destPath, true); return null; }
                catch (Exception ex) { return "复制母带失败：" + ex.Message; }
            }

            if (format == RecordFormat.Gif && useWindowsGif)
                return await WindowsGifExporter.ExportAsync(masterPath, destPath, burnProgressBar,
                    Math.Min(QualityProfiles.Get(presetId).Fps, 24), progress, ct);

            string? ffmpeg = FfmpegLocator.TryFind(manualFfmpegPath);
            if (ffmpeg == null) return "未检测到 FFmpeg，无法转码（可在「设置 → 录屏」手动指定路径）";

            double dur = FfmpegProcess.ProbeDurationSeconds(ffmpeg, masterPath);
            string args = "-hide_banner -nostats -progress pipe:1 -y -i \"" + masterPath + "\" "
                        + BuildFormatArgs(format, burnProgressBar, presetId, dur)
                        + " \"" + destPath + "\"";
            return await RunWithProgressAsync(ffmpeg, args, dur, progress, ct);
        }

        private static string BuildFormatArgs(RecordFormat format, bool burn, string? presetId, double dur)
        {
            var prof = QualityProfiles.Get(presetId);
            // 探不到时长就不烙：这是唯一一处有意偏离参考工程（它是 duration<=0 时按 10 秒兜底），
            // 因为按 10 秒兜底会烙出一条"只走到一小截"的错条，而宁缺毋错。
            bool doBurn = burn && dur > 0;
            switch (format)
            {
                case RecordFormat.Gif:
                {
                    // 参考工程按录制帧率转 GIF；yuSnip 高帧档 50fps 的 GIF 体积爆炸，封顶 24fps（拍板）
                    int fps = Math.Min(prof.Fps, 24);
                    // 进度条与调色板两遍合在同一条滤镜图里跑完（一遍编码、不留中间文件）：
                    // 参考工程是"先烙成中间 MP4 再转"（RecorderHelper.cs:13801-13813），观感等价而少一次转码损失。
                    string pre = doBurn ? BuildBarGraph(dur, "burned") + ";[burned]" : "";
                    return "-vf \"" + pre + "fps=" + fps + ",setpts=N/FRAME_RATE/TB,split[s0][s1];" +
                           "[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=diff_mode=rectangle\" " +
                           "-loop 0 -an";
                }
                case RecordFormat.WebP:
                {
                    int fps = Math.Min(prof.Fps, 30);
                    // WebP 质量按预设码率推（2026-09-29 预设可自定义后不再按档名写死）：
                    // 四档出厂码率 2/6/12/20 Mbps 恰落在 50/75/80/90 上，自定义档按同一条折线插值。
                    int q = WebpQualityFor(prof.ApproxVideoBitsPerSec / 1000);
                    string pre = doBurn ? BuildBarGraph(dur, "burned") + ";[burned]" : "";
                    return "-vf \"" + pre + "setpts=PTS-STARTPTS,fps=" + fps + "\" " +
                           "-vcodec libwebp -lossless 0 -q:v " + q + " -loop 0 -an";
                }
                default:
                {
                    if (!doBurn) return "-c copy -movflags +faststart";
                    // 进度条：逐字移植参考工程（BuildBarGraph 里给了规格与两处不可改的坑）
                    return "-map 0:v -map 0:a? -vf \"" + BuildBarGraph(dur, "") + "\" -c:v libx264 -preset ultrafast -pix_fmt yuv420p " +
                           "-color_range pc " + prof.BitrateArgs + " -movflags +faststart -c:a copy";
                }
            }
        }

        /// <summary>WebP -q:v 由预设码率（kbps）按 (2000→50, 6000→75, 12000→80, 20000→90) 折线插值，两端夹 40~95。</summary>
        private static int WebpQualityFor(long kbps)
        {
            (double k, double q)[] pts = { (2000, 50), (6000, 75), (12000, 80), (20000, 90) };
            if (kbps <= pts[0].k) return (int)Math.Max(40, Math.Round(pts[0].q - (pts[0].k - kbps) / 150.0));
            for (int i = 1; i < pts.Length; i++)
            {
                if (kbps <= pts[i].k)
                {
                    double t = (kbps - pts[i - 1].k) / (pts[i].k - pts[i - 1].k);
                    return (int)Math.Round(pts[i - 1].q + (pts[i].q - pts[i - 1].q) * t);
                }
            }
            return (int)Math.Min(95, Math.Round(90 + (kbps - 20000) / 6000.0));
        }

        /// <summary>
        /// 进度条滤镜图，逐字移植参考工程 <c>ApplyProgressBarMp4</c>
        /// （<c>录屏参考/北京录屏备份5 （最新）/01_核心内核/ScreenRecorderLib/RecorderHelper.cs:13958-13968</c>，
        /// 与其两份成品 DLL 里的字符串一致）。
        /// 规格：12px 高 · 通栏（x=0、w=iw，不缩进）· 贴死底边（y=ih-12）· 无圆角无小球无阴影 ·
        /// 未播轨 <c>black@0.4</c> · 已播 <c>0x0099FF@0.6</c> · 进度按时间走
        /// （一整幅宽的半透明蓝块从屏外左侧滑进来：<c>overlay x='-w+(w*t/时长)'</c>）。
        /// <para>🔴 不要"简化"成两条 drawbox（一条轨＋一条 <c>w='iw*t/时长'</c> 的生长框）：
        /// <c>drawbox</c> 只对 x/y 逐帧求值，w/h 在初始化时算一次就不再动 ⇒ 那样烙出来是一条恒满的条，
        /// 进度头从不出现。本文件此前正是那个写法，2026-09-24 抽三帧采样底行才实测出来。</para>
        /// <para>⚠ 颜色取 <c>0x0099FF</c> 而非 <c>#0099FF</c>：同色，只是避开 <c>#</c> 在命令行里的转义面。
        /// 本机 ffmpeg 7.1 会打 <c>scale2ref is deprecated</c> 警告，属正常，像素与参考工程一致。</para>
        /// </summary>
        /// <param name="dur">母带时长（秒），调用方保证 &gt; 0。</param>
        /// <param name="outLabel">末级输出标签：GIF/WebP 后面还要接自己的链时传 <c>"burned"</c>；
        /// 直接喂编码器时传空串——<c>-vf</c> 的图里最后一级不带标签才等于"这就是输出"。</param>
        private static string BuildBarGraph(double dur, string outLabel)
        {
            string t = dur.ToString("0.00", CultureInfo.InvariantCulture);
            string tail = outLabel.Length == 0 ? "" : "[" + outLabel + "]";
            return "[0:v]drawbox=x=0:y=ih-12:w=iw:h=12:color=black@0.4:t=fill[bg_v];"
                 + "color=c=0x0099FF@0.6:s=100x12,format=rgba[bar_tmp];"
                 + "[bar_tmp][bg_v]scale2ref=w=iw:h=12[bar][v_final_ref];"
                 + "[v_final_ref][bar]overlay=x='-w+(w*t/" + t + ")':y=H-12:shortest=1" + tail;
        }

        private static async Task<string?> RunWithProgressAsync(
            string ffmpeg, string args, double durationSeconds, IProgress<double>? progress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = new Process { StartInfo = psi };
            var errTail = new StringBuilder();
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && errTail.Length < 4000) errTail.AppendLine(e.Data);
            };

            try { p.Start(); }
            catch (Exception ex) { return "启动 FFmpeg 失败：" + ex.Message; }
            p.BeginErrorReadLine();

            try
            {
                while (true)
                {
                    string? line = await p.StandardOutput.ReadLineAsync(ct);
                    if (line == null) break;
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    {
                        // out_time_ms 历史原因是同一微秒值（ffmpeg 两键同值），统一按微秒处理
                        string v = line.Substring(line.IndexOf('=') + 1);
                        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)
                            && durationSeconds > 0 && progress != null)
                        {
                            progress.Report(Math.Clamp(us / 1_000_000.0 / durationSeconds, 0, 0.99));
                        }
                    }
                }
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "已取消";
            }

            if (p.ExitCode != 0)
            {
                string tail = errTail.ToString();
                int cut = tail.Length > 600 ? tail.Length - 600 : 0;
                return "FFmpeg 退出码 " + p.ExitCode + "：" + tail.Substring(cut);
            }
            progress?.Report(1);
            return null;
        }
    }
}
