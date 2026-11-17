using System.Drawing;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>画质四档（对应稿 1 表格：省流 / 标准 / 高帧 / 原画）。</summary>
    public enum RecordQuality { Saver, Standard, HighFps, Native }

    /// <summary>导出容器格式。录制引擎本体始终录 MP4，GIF/WebP 由 M3 转码。</summary>
    public enum RecordFormat { Mp4, Gif, WebP }

    /// <summary>音频通路布局（稿 1 D19）。Split 仅对 MP4 有效，出双 AAC 轨。</summary>
    public enum AudioLayout { None, SystemOnly, MicOnly, Mixed, Split }

    public enum RecordEngine { Auto, Windows, Ffmpeg }

    public static class RecordEngines
    {
        public static RecordEngine Normalize(string? value) =>
            System.Enum.TryParse<RecordEngine>(value, out var engine) && System.Enum.IsDefined(engine) ? engine : RecordEngine.Auto;
    }

    /// <summary>一次录制会话的全部参数。Region 为物理像素。</summary>
    public sealed class RecordingOptions
    {
        public RecordEngine Engine { get; set; } = RecordEngine.Auto;
        public Rectangle Region { get; set; }
        public RecordQuality Quality { get; set; } = RecordQuality.Standard;
        /// <summary>画质预设 Id（2026-09-29 起录制与导出以它为准，见 <see cref="RecordPresets"/>）。
        /// 空＝按 <see cref="Quality"/> 的成员名取（兼容旧调用方与自测台）。</summary>
        public string? PresetId { get; set; }
        /// <summary>实际生效的预设 Id。</summary>
        public string EffectivePresetId => string.IsNullOrEmpty(PresetId) ? Quality.ToString() : PresetId!;
        public AudioLayout Audio { get; set; } = AudioLayout.Mixed;
        /// <summary>麦克风音量系数 0..1（设置项 20/40/60/100% 归一后传入）。</summary>
        public float MicVolume01 { get; set; } = 1f;
        /// <summary>系统硬件光标是否入画（ffmpeg draw_mouse）。</summary>
        public bool DrawCursor { get; set; } = true;
        /// <summary>开始前倒计时秒数；合法档见 <see cref="RecordCountdown"/>（0＝不倒计时）。</summary>
        public int CountdownSec { get; set; } = RecordCountdown.DefaultSec;
        /// <summary>手动指定的 ffmpeg.exe 路径（可空；探测优先项）。</summary>
        public string? ManualFfmpegPath { get; set; }
        /// <summary>画面采集方式（默认 <see cref="RecordCaptureSource.Auto"/>＝优先 ddagrab，打不开再回落 gdigrab）。</summary>
        public RecordCaptureSource CaptureSource { get; set; } = RecordCaptureSource.Auto;
    }

    /// <summary>
    /// 画面采集方式。ddagrab 走 Windows 桌面复制接口（DXGI），gdigrab 走 GDI 截屏老通道。
    /// 老通道是纯定时器 BitBlt，画在硬件叠加层上的内容它抄不到（录出来就是一帧不动）。
    /// </summary>
    public enum RecordCaptureSource { Auto, GdiOnly }

    /// <summary><see cref="RecordCaptureSource"/> 的档位表与解析（设置页与录制侧共用这一份）。</summary>
    public static class RecordCaptureSources
    {
        /// <summary>存设置用的成员名（与 RecQuality 同口径）。</summary>
        public static string ToSetting(RecordCaptureSource s) => s.ToString();

        /// <summary>陌生值（旧设置残留／手改 ini）一律回默认档 Auto。</summary>
        public static RecordCaptureSource Normalize(string? v) =>
            System.Enum.TryParse<RecordCaptureSource>(v, out var s) ? s : RecordCaptureSource.Auto;
    }

    /// <summary>
    /// 录制延迟（倒计时）的档位表——<b>唯一真相源</b>。参数条下拉、设置页下拉、落盘校验、
    /// 倒计时实现四处共用这一份，加一档只改 <see cref="Seconds"/>，不再各处各写一套 0/3/5。
    /// </summary>
    public static class RecordCountdown
    {
        /// <summary>可选秒数（0＝无延迟）。数组顺序即下拉里的呈现顺序。</summary>
        public static readonly int[] Seconds = { 0, 1, 2, 3, 5, 10 };

        /// <summary>默认档（稿 1 D14 推荐 3 秒）。</summary>
        public const int DefaultSec = 3;

        /// <summary>表内最大秒数：倒计时实现按它做上限夹取，免得自己另写一个 9。</summary>
        public static int MaxSeconds => Seconds[Seconds.Length - 1];

        /// <summary>档名：0 读作「无延迟」，其余「N 秒」。</summary>
        public static string Label(int sec) => sec == 0 ? "无延迟" : sec + " 秒";

        /// <summary>把任意来源的值归到表内一档（旧设置残留 / 手改 ini 的陌生值一律回默认档）。</summary>
        public static int Normalize(int sec) => System.Array.IndexOf(Seconds, sec) >= 0 ? sec : DefaultSec;
    }

    /// <summary>单档 ffmpeg 参数摘要。</summary>
    public readonly struct QualityProfile
    {
        public QualityProfile(int fps, int scaleMaxHeight, string bitrateArgs, long approxVideoBitsPerSec)
        {
            Fps = fps;
            ScaleMaxHeight = scaleMaxHeight;
            BitrateArgs = bitrateArgs;
            ApproxVideoBitsPerSec = approxVideoBitsPerSec;
        }

        /// <summary>请求帧率。</summary>
        public int Fps { get; }
        /// <summary>高度上限（0 = 不缩放，用选区原生分辨率）。超出按 scale=-2:H 等比降。</summary>
        public int ScaleMaxHeight { get; }
        /// <summary>码率族参数（-b:v/-maxrate/-bufsize）。</summary>
        public string BitrateArgs { get; }
        /// <summary>估算视频码率（bps），供磁盘将满预警：剩余 &lt; 本值/8×60 秒即告警。</summary>
        public long ApproxVideoBitsPerSec { get; }
    }

    /// <summary>档→ffmpeg 参数表。2026-09-29 起参数来自用户可编辑的 <see cref="RecordPresets"/>（内置四档出厂值与旧表同一组数）。</summary>
    public static class QualityProfiles
    {
        public static QualityProfile Get(RecordQuality q) => Get(q.ToString());

        /// <summary>按预设 Id 取（找不到回标准档）。</summary>
        public static QualityProfile Get(string? presetId) => RecordPresets.Resolve(presetId).ToProfile();

        /// <summary>把 scale 计算成具体宽高（数值化，避免依赖 ffmpeg -2 表达式与 crop 顺序耦合）。
        /// 返回偶数化的目标 (w,h)；无需缩放时原样返回偶数化后的输入。</summary>
        public static (int w, int h) ScaledDims(QualityProfile p, int cropW, int cropH)
        {
            int w = cropW, h = cropH;
            if (p.ScaleMaxHeight > 0 && h > p.ScaleMaxHeight)
            {
                double ratio = (double)p.ScaleMaxHeight / h;
                w = (int)System.Math.Round(cropW * ratio);
                h = p.ScaleMaxHeight;
            }
            if (w % 2 != 0) w--;
            if (h % 2 != 0) h--;
            if (w < 2) w = 2;
            if (h < 2) h = 2;
            return (w, h);
        }
    }
}
