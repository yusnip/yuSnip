using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenCaptureTool.Core.Deps
{
    /// <summary>三个外置运行的依赖 id，与服务器 deps.json 的 id 字段一一对应。</summary>
    public static class DepIds
    {
        public const string Ocr = "ocr";
        public const string Matting = "matting";
        public const string Ffmpeg = "ffmpeg";
    }

    /// <summary>单个依赖的生命周期状态。UI 侧只按此枚举出文案，不自行推断。</summary>
    public enum DepState
    {
        /// <summary>未落盘（含"清单里还没有这个依赖"）。</summary>
        Absent,
        /// <summary>已排进单飞队列，还没轮到。</summary>
        Queued,
        /// <summary>正在下载（含"正在取清单"这段不确定进度）。</summary>
        Downloading,
        /// <summary>正在校验与解包落位。</summary>
        Verifying,
        /// <summary>已就绪。</summary>
        Ready,
        /// <summary>失败，Error 里是外因文案。</summary>
        Failed,
    }

    /// <summary>包内单文件描述：文件名／体积／SHA-256。文件级哈希是"收编"能成立的前提。</summary>
    public sealed class DepFileDef
    {
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public string Sha256 { get; init; } = "";
    }

    /// <summary>一个依赖包（zip）的描述。</summary>
    public sealed class DepPackageDef
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string FileName { get; init; } = "";
        public long Size { get; init; }
        public string Sha256 { get; init; } = "";
        public IReadOnlyList<DepFileDef> Files { get; init; } = Array.Empty<DepFileDef>();

        /// <summary>落盘目录名：依赖版本随包内容走，同版本不重下。</summary>
        public string DirName => Id + Path.DirectorySeparatorChar + Version;
    }

    /// <summary>服务器清单。零反射手动解析（NativeAOT 口径，同 UpdateService）。</summary>
    public sealed class DepManifest
    {
        public int Schema { get; private init; }
        public string Generated { get; private init; } = "";
        public IReadOnlyList<DepPackageDef> Packages { get; private init; } = Array.Empty<DepPackageDef>();

        public DepPackageDef? Find(string id)
        {
            foreach (var p in Packages)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>解析清单；结构不合法（缺 id／version／files）时返回 false，调用方沿用旧缓存。</summary>
        public static bool TryParse(string json, out DepManifest? manifest)
        {
            manifest = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                int schema = 0;
                if (root.TryGetProperty("schema", out var sp) && sp.ValueKind == JsonValueKind.Number)
                    schema = sp.GetInt32();
                if (schema != 1) return false;

                if (!root.TryGetProperty("deps", out var deps) || deps.ValueKind != JsonValueKind.Array)
                    return false;

                string generated = "";
                if (root.TryGetProperty("generated", out var gp)) generated = gp.GetString() ?? "";

                var list = new List<DepPackageDef>();
                foreach (var pe in deps.EnumerateArray())
                {
                    if (pe.ValueKind != JsonValueKind.Object) continue;
                    string id = Str(pe, "id"), ver = Str(pe, "version"), file = Str(pe, "file");
                    string sha = Str(pe, "sha256");
                    if (id.Length == 0 || ver.Length == 0 || file.Length == 0 || sha.Length == 0) continue;
                    if (!pe.TryGetProperty("files", out var fe) || fe.ValueKind != JsonValueKind.Array) continue;

                    var files = new List<DepFileDef>();
                    foreach (var f in fe.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object) continue;
                        string name = Str(f, "name"), fsha = Str(f, "sha256");
                        if (name.Length == 0 || fsha.Length == 0) continue;
                        files.Add(new DepFileDef { Name = name, Size = Num(f, "size"), Sha256 = fsha });
                    }
                    if (files.Count == 0) continue;

                    list.Add(new DepPackageDef
                    {
                        Id = id, Version = ver, FileName = file,
                        Size = Num(pe, "size"), Sha256 = sha, Files = files,
                    });
                }
                if (list.Count == 0) return false;

                manifest = new DepManifest { Schema = schema, Generated = generated, Packages = list };
                return true;
            }
            catch { return false; }
        }

        private static string Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        private static long Num(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }

    /// <summary>已就绪登记（deps.lock.json）里的一条。</summary>
    internal sealed class DepLockEntry
    {
        public string Version = "";
        public string PackageSha256 = "";
        public string Source = "";     // download / adoption / redownload
        public DateTime VerifiedAtUtc;
    }

    /// <summary>
    /// 依赖目录布局与本地真值：清单缓存、落盘目录、就绪登记、文件哈希校验。
    /// 只做"是不是它"的判断，不做任何下载与 UI。
    /// </summary>
    public static class DependencyCatalog
    {
        public static string RootDir => Path.Combine(AppSettings.SettingsDirectory, "deps");
        private static string ManifestCachePath => Path.Combine(RootDir, "deps.json");
        private static string LockPath => Path.Combine(RootDir, "deps.lock.json");
        public static string DownloadDir => Path.Combine(RootDir, "_download");

        /// <summary>旧「一键下载 FFmpeg」的落位处，收编的 FFmpeg 来源；与 deps 目录并存。</summary>
        public static string LegacyFfmpegDir => Path.Combine(AppSettings.SettingsDirectory, "ffmpeg");

        /// <summary>包落盘目录（不含就绪判断）。</summary>
        public static string PackageDir(DepPackageDef p) => Path.Combine(RootDir, p.DirName);

        public static string FilePath(DepPackageDef p, string name) => Path.Combine(PackageDir(p), name);

        /// <summary>当前清单缓存；无缓存或解析失败返回 null。</summary>
        public static DepManifest? LoadCachedManifest()
        {
            try
            {
                if (!File.Exists(ManifestCachePath)) return null;
                return DepManifest.TryParse(File.ReadAllText(ManifestCachePath), out var m) ? m : null;
            }
            catch { return null; }
        }

        public static void SaveManifestCache(string json)
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(ManifestCachePath, json, new UTF8Encoding(false));
        }

        // ================= 就绪登记 =================

        private static readonly object LockIoSync = new();

        internal static Dictionary<string, DepLockEntry> LoadLock()
        {
            lock (LockIoSync) return LoadLockNoLock();
        }

        public static void WriteLock(string id, DepPackageDef p, string source)
        {
            lock (LockIoSync)
            {
                var dict = LoadLockNoLock();
                dict[id] = new DepLockEntry
                {
                    Version = p.Version,
                    PackageSha256 = p.Sha256,
                    Source = source,
                    VerifiedAtUtc = DateTime.UtcNow,
                };
                SaveLockNoLock(dict);
            }
        }

        /// <summary>抹掉一条就绪登记（卸载用）；没有这条登记时什么都不做。</summary>
        public static void RemoveLock(string id)
        {
            lock (LockIoSync)
            {
                var dict = LoadLockNoLock();
                if (!dict.Remove(id)) return;
                SaveLockNoLock(dict);
            }
        }

        private static void SaveLockNoLock(Dictionary<string, DepLockEntry> dict)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteNumber("schema", 1);
                    w.WriteString("updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    w.WriteStartObject("packages");
                    foreach (var kv in dict)
                    {
                        w.WriteStartObject(kv.Key);
                        w.WriteString("version", kv.Value.Version);
                        w.WriteString("sha256", kv.Value.PackageSha256);
                        w.WriteString("source", kv.Value.Source);
                        w.WriteString("verifiedUtc", kv.Value.VerifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                bytes = ms.ToArray();
            }
            Directory.CreateDirectory(RootDir);
            File.WriteAllBytes(LockPath, bytes);
        }

        private static Dictionary<string, DepLockEntry> LoadLockNoLock()
        {
            // 调用方（LoadLock / WriteLock / RemoveLock）已持锁，这里不再重复
            var dict = new Dictionary<string, DepLockEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(LockPath)) return dict;
                using var doc = JsonDocument.Parse(File.ReadAllText(LockPath));
                if (!doc.RootElement.TryGetProperty("packages", out var pk) ||
                    pk.ValueKind != JsonValueKind.Object) return dict;
                foreach (var e in pk.EnumerateObject())
                {
                    if (e.Value.ValueKind != JsonValueKind.Object) continue;
                    var entry = new DepLockEntry
                    {
                        Version = ReadStr(e.Value, "version"),
                        PackageSha256 = ReadStr(e.Value, "sha256"),
                        Source = ReadStr(e.Value, "source"),
                    };
                    if (DateTime.TryParse(ReadStr(e.Value, "verifiedUtc"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                        entry.VerifiedAtUtc = t;
                    dict[e.Name] = entry;
                }
            }
            catch { /* 登记损坏＝全部按未就绪重算，不影响功能正确性 */ }
            return dict;
        }

        private static string ReadStr(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        // ================= 校验 =================

        /// <summary>
        /// 廉价就绪判断：登记在案 ＋ 版本与包哈希对得上 ＋ 每个文件存在且体积吻合。
        /// 全量 SHA-256 只在落盘那一刻与"重新下载"时跑，日常启动不重算 100 MB。
        /// </summary>
        internal static bool IsReady(DepManifest manifest, Dictionary<string, DepLockEntry> lockDict, DepPackageDef p)
        {
            if (!lockDict.TryGetValue(p.Id, out var entry)) return false;
            if (!string.Equals(entry.Version, p.Version, StringComparison.Ordinal)) return false;
            if (!string.Equals(entry.PackageSha256, p.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            return AllFilesPresentWithSize(p);
        }

        public static bool AllFilesPresentWithSize(DepPackageDef p)
        {
            foreach (var f in p.Files)
            {
                try
                {
                    var fi = new FileInfo(FilePath(p, f.Name));
                    if (!fi.Exists || fi.Length != f.Size) return false;
                }
                catch { return false; }
            }
            return true;
        }

        /// <summary>逐文件全量哈希比对（下载／收编落位后跑一次）。</summary>
        public static bool VerifyFilesByHash(DepPackageDef p)
        {
            foreach (var f in p.Files)
            {
                if (!string.Equals(Sha256File(FilePath(p, f.Name)), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>算文件 SHA-256 小写十六进制；读不到返回空串。</summary>
        public static string Sha256File(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                var hash = SHA256.HashData(fs);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch { return ""; }
        }
    }
}
