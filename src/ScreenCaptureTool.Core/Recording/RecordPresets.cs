using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// 录屏画质预设（2026-09-29）：帧率 / 高度上限 / 视频码率三个用户可调量，其余（峰值码率、缓冲、磁盘预警码率）
    /// 由这三个推出来。内置四档（省流 / 标准 / 高帧 / 原画）以 <see cref="RecordQuality"/> 成员名作 Id，
    /// 可改参数、可隐藏、可一键还原，但不能删；用户新建的档 Id 以 <c>u</c> 开头，可删。
    /// </summary>
    public sealed class RecordPreset
    {
        /// <summary>稳定键：内置档＝<c>Saver/Standard/HighFps/Native</c>，自建档＝<c>u</c>＋时间戳。存「上次使用」「默认档」用的就是它。</summary>
        public string Id { get; set; } = "";
        /// <summary>显示名（参数条按钮与菜单）。</summary>
        public string Name { get; set; } = "";
        public int Fps { get; set; } = 24;
        /// <summary>高度上限（像素，0＝不缩放，保留选区原生分辨率）。</summary>
        public int MaxHeight { get; set; } = 1080;
        /// <summary>目标视频码率（kbps）。峰值＝×1.25、缓冲＝×2，与内置四档旧参数表同一比例。</summary>
        public int BitrateKbps { get; set; } = 6000;
        /// <summary>是否出现在录制参数条的画质下拉里。</summary>
        public bool Visible { get; set; } = true;

        public bool IsBuiltIn => RecordPresets.IsBuiltInId(Id);

        public RecordPreset Clone() => new()
        {
            Id = Id, Name = Name, Fps = Fps, MaxHeight = MaxHeight, BitrateKbps = BitrateKbps, Visible = Visible,
        };

        /// <summary>换算成录制引擎吃的参数摘要（已按合法范围夹取）。</summary>
        public QualityProfile ToProfile()
        {
            var p = RecordPresets.Normalize(this);
            int kb = p.BitrateKbps;
            string args = $"-b:v {kb}k -maxrate {kb * 5 / 4}k -bufsize {kb * 2}k";
            return new QualityProfile(p.Fps, p.MaxHeight, args, kb * 1000L);
        }

        /// <summary>参数摘要一行（菜单 Tooltip 与设置页列表共用）。</summary>
        public string Summary()
        {
            var p = RecordPresets.Normalize(this);
            string res = p.MaxHeight == 0 ? "原生分辨率" : $"最高 {p.MaxHeight}P";
            return $"{p.Fps} FPS · {res} · 码率 {FormatMbps(p.BitrateKbps)}";
        }

        internal static string FormatMbps(int kbps)
        {
            double m = kbps / 1000.0;
            return (Math.Abs(m - Math.Round(m)) < 0.001 ? Math.Round(m).ToString(CultureInfo.InvariantCulture)
                                                       : m.ToString("0.#", CultureInfo.InvariantCulture)) + " Mbps";
        }
    }

    /// <summary>
    /// 预设表的<b>唯一真相源</b>：合法范围、内置默认值、读写 <c>record_presets.ini</c>。
    /// 录制（<c>RecordingSession</c>）、导出（<c>ExportService</c>）、参数条、设置页四处都只经这里取值。
    /// <para>落盘只记"与出厂不同"的内置档与全部自建档：出厂值将来调整时，没动过的用户自动跟上。</para>
    /// </summary>
    public static class RecordPresets
    {
        // —— 合法范围（「在允许的情况下」的具体口径）——
        // 帧率：5~60。ddagrab/gdigrab 抓屏在 60 以上基本抓不满，libx264 ultrafast 实时编码也吃紧；
        //       低于 5 的"录屏"已经是幻灯片。
        public const int MinFps = 5, MaxFps = 60;
        // 码率：0.5~50 Mbps。下限再低 1080p 糊成色块；上限对应 4K60 的常用上沿，再高只是撑体积。
        public const int MinBitrateKbps = 500, MaxBitrateKbps = 50_000;
        /// <summary>高度上限可选档（0＝原生）。给档而不给自由输入：非常规高度（如 1000）对体积/清晰度没有额外意义，
        /// 还会让"最高 xxxP"看起来像输错了。</summary>
        public static readonly int[] HeightOptions = { 480, 720, 1080, 1440, 2160, 0 };
        /// <summary>自建档上限（参数条下拉放不下太多行）。</summary>
        public const int MaxUserPresets = 8;
        public const int MaxNameLength = 8;

        public static string PresetsPath => Path.Combine(AppSettings.SettingsDirectory, "record_presets.ini");

        /// <summary>文件头标记（云同步恢复时据此校验内容确实是本文件，见 <c>CloudConfigHelper</c>）。</summary>
        public const string FileHeader = "# yuSnip record presets v";

        private static readonly (string Id, string Name, int Fps, int H, int Kbps)[] Factory =
        {
            (nameof(RecordQuality.Saver),    "省流", 15, 720,  2000),
            (nameof(RecordQuality.Standard), "标准", 24, 1080, 6000),
            (nameof(RecordQuality.HighFps),  "高帧", 50, 1080, 12000),
            (nameof(RecordQuality.Native),   "原画", 30, 0,    20000),
        };

        public static bool IsBuiltInId(string? id) => id != null && Factory.Any(f => f.Id == id);

        /// <summary>内置档的出厂值（「还原默认」用）。非内置 Id 返回 null。</summary>
        public static RecordPreset? FactoryDefault(string id)
        {
            foreach (var f in Factory)
                if (f.Id == id) return new RecordPreset { Id = f.Id, Name = f.Name, Fps = f.Fps, MaxHeight = f.H, BitrateKbps = f.Kbps };
            return null;
        }

        public static List<RecordPreset> FactoryList() => Factory.Select(f => FactoryDefault(f.Id)!).ToList();

        /// <summary>这一档的四个量是否<b>与出厂值逐字相同</b>（非内置档返回 false）。
        /// 设置页那枚「内置 / 内置 · 已修改」徽标与编辑窗里「还原默认」该不该置灰，读的都是这一份。</summary>
        public static bool IsFactoryDefault(RecordPreset p)
        {
            var f = FactoryDefault(p.Id);
            return f != null && f.Name == p.Name && f.Fps == p.Fps && f.MaxHeight == p.MaxHeight && f.BitrateKbps == p.BitrateKbps;
        }

        /// <summary>夹进合法范围（返回新实例，不改入参）。名字空白时内置档回出厂名、自建档叫「自定义」。</summary>
        public static RecordPreset Normalize(RecordPreset p)
        {
            var n = p.Clone();
            n.Fps = Math.Clamp(n.Fps, MinFps, MaxFps);
            n.BitrateKbps = Math.Clamp(n.BitrateKbps, MinBitrateKbps, MaxBitrateKbps);
            if (Array.IndexOf(HeightOptions, n.MaxHeight) < 0)
            {
                // 陌生高度归到不小于它的最近一档（手改 ini 写了 1000 ⇒ 1080），比它还大就当原生
                n.MaxHeight = HeightOptions.Where(h => h > 0 && h >= n.MaxHeight).DefaultIfEmpty(0).Min();
            }
            n.Name = (n.Name ?? "").Trim();
            if (n.Name.Length > MaxNameLength) n.Name = n.Name.Substring(0, MaxNameLength);
            if (n.Name.Length == 0) n.Name = FactoryDefault(n.Id)?.Name ?? "自定义";
            return n;
        }

        private static List<RecordPreset>? _cache;
        private static readonly object Gate = new();

        /// <summary>预设表发生变化（设置页保存后）。参数条据此重铺下拉。</summary>
        public static event Action? Changed;

        /// <summary>当前全部预设（内置四档恒在最前、顺序固定；自建档按创建顺序跟在后面）。返回副本。</summary>
        public static List<RecordPreset> Load()
        {
            lock (Gate)
            {
                _cache ??= ReadFromDisk();
                return _cache.Select(p => p.Clone()).ToList();
            }
        }

        /// <summary>按 Id 取（找不到、或 Id 为空 ⇒ 标准档）。录制与导出都走这里，永远拿得到一个合法档。</summary>
        public static RecordPreset Resolve(string? id)
        {
            var all = Load();
            return all.FirstOrDefault(p => p.Id == id)
                   ?? all.First(p => p.Id == nameof(RecordQuality.Standard));
        }

        /// <summary>参数条下拉用：只含勾了「显示」的档；一个都没勾时退回标准档，下拉不能是空的。</summary>
        public static List<RecordPreset> VisibleList()
        {
            var all = Load();
            var vis = all.Where(p => p.Visible).ToList();
            return vis.Count > 0 ? vis : all.Where(p => p.Id == nameof(RecordQuality.Standard)).ToList();
        }

        public static string NewUserId() => "u" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        /// <summary>整表保存：内置档缺哪条补出厂值（不许删），全部夹进合法范围，写盘后广播 <see cref="Changed"/>。</summary>
        public static void Save(IEnumerable<RecordPreset> presets)
        {
            var list = new List<RecordPreset>();
            var input = presets.ToList();
            foreach (var f in Factory)
                list.Add(Normalize(input.FirstOrDefault(p => p.Id == f.Id) ?? FactoryDefault(f.Id)!));
            foreach (var p in input.Where(p => !IsBuiltInId(p.Id) && !string.IsNullOrWhiteSpace(p.Id)).Take(MaxUserPresets))
                list.Add(Normalize(p));

            lock (Gate) { _cache = list.Select(p => p.Clone()).ToList(); }

            try
            {
                if (!Directory.Exists(AppSettings.SettingsDirectory)) Directory.CreateDirectory(AppSettings.SettingsDirectory);
                var sb = new StringBuilder();
                sb.AppendLine(FileHeader + "1");
                sb.AppendLine("; 录屏画质预设（设置 → 交互与行为 → 录屏 → 画质预设）。内置四档只记与出厂不同的项。");
                foreach (var p in list)
                {
                    var fd = FactoryDefault(p.Id);
                    bool same = fd != null && fd.Name == p.Name && fd.Fps == p.Fps && fd.MaxHeight == p.MaxHeight
                                && fd.BitrateKbps == p.BitrateKbps && p.Visible;
                    if (same) continue;
                    sb.AppendLine("[" + p.Id + "]");
                    sb.AppendLine("Name=" + p.Name);
                    sb.AppendLine("Fps=" + p.Fps.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("MaxHeight=" + p.MaxHeight.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("BitrateKbps=" + p.BitrateKbps.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("Visible=" + (p.Visible ? "1" : "0"));
                }
                string tmp = PresetsPath + ".tmp";
                lock (CloudSync.CloudSnapshot.FileGate)
                {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                CloudSync.CloudConfigTransaction.ReplaceFile(tmp, PresetsPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("RecordPresets.Save 失败：" + ex.Message);
            }

            try { Changed?.Invoke(); } catch (Exception ex) { AppLogger.Warn("RecordPresets.Changed 回调异常：" + ex.Message); }
        }

        /// <summary>云恢复 / 外部改了文件后重读。</summary>
        public static void Reload()
        {
            lock (Gate) { _cache = null; }
            try { Changed?.Invoke(); } catch { }
        }

        private static List<RecordPreset> ReadFromDisk()
        {
            var list = FactoryList();
            try
            {
                if (!File.Exists(PresetsPath)) return list;
                RecordPreset? cur = null;
                foreach (string raw in File.ReadAllLines(PresetsPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        string id = line.Substring(1, line.Length - 2).Trim();
                        if (id.Length == 0) { cur = null; continue; }
                        cur = list.FirstOrDefault(p => p.Id == id);
                        if (cur == null)
                        {
                            if (IsBuiltInId(id) || list.Count(p => !p.IsBuiltIn) >= MaxUserPresets) { cur = null; continue; }
                            cur = new RecordPreset { Id = id, Name = "自定义" };
                            list.Add(cur);
                        }
                        continue;
                    }
                    if (cur == null) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                    int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv);
                    switch (key)
                    {
                        case "Name": cur.Name = val; break;
                        case "Fps": cur.Fps = iv; break;
                        case "MaxHeight": cur.MaxHeight = iv; break;
                        case "BitrateKbps": cur.BitrateKbps = iv; break;
                        case "Visible": cur.Visible = val != "0"; break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("RecordPresets: 读取 record_presets.ini 失败，按出厂值：" + ex.Message);
                return FactoryList();
            }
            return list.Select(Normalize).ToList();
        }
    }
}
