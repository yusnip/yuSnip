using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenCaptureTool.Core.Actions;

namespace ScreenCaptureTool.Core.CloudSync;

/// <summary>自动同步专用范围；连接状态和设备现场参数永远留在本机。</summary>
public sealed class CloudSnapshot
{
    public static readonly string[] Files = ["settings.ini", "actions.ini", "shortcuts.ini", "record_presets.ini"];
    private static readonly string[] Segments = ["ini", "actions", "shortcuts", "recordPresets"];
    private static readonly string[] Headers = ["", ActionFileFormat.Header, "# yuSnip shortcuts v1", "# yuSnip record presets v1"];
    private static readonly HashSet<string> LocalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SaveDirectory", "RunAsAdmin", "RunOnStartup", "RecFfmpegPath", "RecSaveDirectory",
        "CaptureRectHistory", "LastAnnotationTool", "ToolbarMigrated", "WatermarkMigrated", "RecMigrated", "ClearAllAnnotationsMigrated", "LongScrollStickerMigrated"
    };
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    { "AiApiKey", "BaiduAppId", "BaiduSecretKey", "YoudaoAppId", "YoudaoSecretKey" };
    public bool IncludeSecrets { get; }
    public string[] Content { get; }
    public string Hash { get; }
    public static object FileGate { get; } = new();
    public static SemaphoreSlim OperationGate { get; } = new(1, 1);
    public static async Task<IDisposable> EnterOperationAsync()
    {
        await OperationGate.WaitAsync();
        return new OperationLease();
    }
    private sealed class OperationLease : IDisposable
    {
        public void Dispose() => OperationGate.Release();
    }

    public static bool IsLocalKey(string key) => LocalKeys.Contains(key)
        || key.StartsWith("Cloud", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("WebDav", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("RecLast", StringComparison.OrdinalIgnoreCase);
    private static bool IsSecret(string key) => SecretKeys.Contains(key)
        || (key.StartsWith("AiProfile_", StringComparison.OrdinalIgnoreCase) && key.EndsWith("_ApiKey", StringComparison.OrdinalIgnoreCase));

    public CloudSnapshot(string[] content, bool secrets)
    {
        if (content.Length != 4) throw new InvalidDataException("配置必须包含四个数据段。");
        IncludeSecrets = secrets;
        Content = new string[4];
        var values = ParseSettings(content[0]);
        foreach (string key in values.Keys.ToArray())
            if (IsLocalKey(key) || (!secrets && IsSecret(key))
                || (IsSecret(key) && values[key].StartsWith("dp:", StringComparison.Ordinal))) values.Remove(key);
        if (values.Count == 0) throw new InvalidDataException("配置段没有可同步的设置，拒绝空配置。");
        Content[0] = EncodeSettings(values);
        for (int i = 1; i < 4; i++) Content[i] = CanonicalSidecar(content[i], i);
        Hash = Digest((secrets ? "secrets:on\n" : "secrets:off\n") + string.Join("\n\u0000\n", Content));
    }

    public static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static SortedDictionary<string, string> ParseSettings(string text)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.TrimStart('\uFEFF').Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int pos = line.IndexOf('=');
            if (pos <= 0) throw new InvalidDataException("配置段包含无效行。");
            string key = line[..pos].Trim();
            if (!result.TryAdd(key, line[(pos + 1)..].Trim())) throw new InvalidDataException("配置段包含重复字段：" + key);
        }
        return result;
    }
    private static string EncodeSettings(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("\n", values.Select(p => p.Key.ToLowerInvariant() + "=" + p.Value)) + "\n";
    private static string CanonicalSidecar(string text, int index)
    {
        if (string.IsNullOrWhiteSpace(text)) text = Headers[index];
        string[] lines = text.TrimStart('\uFEFF').Replace("\r", "").Split('\n');
        string prefix = Headers[index][..(Headers[index].LastIndexOf('v') + 1)];
        string? header = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (header == null) throw new InvalidDataException(Files[index] + " 格式标记无效。");
        string? versionText = header[prefix.Length..].Split([' ', '\t', '—'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!int.TryParse(versionText, out int version) || (index == 1 ? ActionFileFormat.ColumnCount(version) == 0 : version != 1))
            throw new InvalidDataException(Files[index] + " 格式版本不受支持。");
        var data = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#') && !l.TrimStart().StartsWith(';')).ToArray();
        if (index < 3)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in data)
            {
                string[] parts = line.Split('\t');
                int expected = index == 2 ? 4 : ActionFileFormat.ColumnCount(version);
                if (parts.Length != expected || parts[0] != (index == 1 ? "action" : "shortcut") || string.IsNullOrWhiteSpace(parts[1]) || !ids.Add(parts[1]))
                    throw new InvalidDataException(Files[index] + " 数据行无效或 ID 重复。");
                var numbers = Enumerable.Range(2, expected - 2).Where(n => n is not (6 or 7));
                if (numbers.Any(n => !uint.TryParse(parts[n], out _))) throw new InvalidDataException(Files[index] + " 数字字段无效。");
            }
        }
        else
        {
            var ids = new HashSet<string>();
            bool hasSection = false;
            var keys = new HashSet<string>();
            foreach (string line in data)
            {
                if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
                { if (!ids.Add(line[1..^1])) throw new InvalidDataException("录屏预设 ID 重复。"); hasSection = true; keys.Clear(); continue; }
                int pos = line.IndexOf('=');
                if (!hasSection || pos < 1 || !keys.Add(line[..pos])) throw new InvalidDataException("录屏预设字段无效。");
                string key = line[..pos];
                if (key != "Name" && (key is not ("Fps" or "MaxHeight" or "BitrateKbps" or "Visible") || !int.TryParse(line[(pos + 1)..], out _)))
                    throw new InvalidDataException("录屏预设参数无效。");
            }
        }
        return prefix + version.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", data) + (data.Length > 0 ? "\n" : "");
    }
    public static CloudSnapshot Capture(string directory, bool secrets)
    {
        lock (FileGate)
        {
            return new CloudSnapshot(Files.Select((name, i) => File.Exists(Path.Combine(directory, name))
                ? File.ReadAllText(Path.Combine(directory, name), Encoding.UTF8) : Headers[i]).ToArray(), secrets);
        }
    }
    public CloudSnapshot ForPolicy(bool secrets) => new(Content, secrets);

    /// <summary>保留本机字段；不同步密钥时按配置 ID 回填，不能按列表位置串号。</summary>
    public string[] MergeLocal(string directory)
    {
        var local = ParseSettings(File.ReadAllText(Path.Combine(directory, Files[0]), Encoding.UTF8));
        var incoming = ParseSettings(Content[0]);
        foreach (var pair in local)
            if (IsLocalKey(pair.Key)) incoming[pair.Key] = pair.Value;
        {
            foreach (var pair in local.Where(p => SecretKeys.Contains(p.Key)))
                if (!IncludeSecrets || !incoming.ContainsKey(pair.Key)) incoming[pair.Key] = pair.Value;
            foreach (var pair in incoming.ToArray())
            {
                if (!Regex.IsMatch(pair.Key, "^AiProfile_[0-9]+_Id$", RegexOptions.IgnoreCase)) continue;
                string? localIdKey = local.FirstOrDefault(p => p.Key.StartsWith("AiProfile_", StringComparison.OrdinalIgnoreCase)
                    && p.Key.EndsWith("_Id", StringComparison.OrdinalIgnoreCase) && p.Value == pair.Value).Key;
                if ((!IncludeSecrets || !incoming.ContainsKey(pair.Key[..^3] + "_ApiKey")) && localIdKey != null && local.TryGetValue(localIdKey[..^3] + "_ApiKey", out string? secret))
                    incoming[pair.Key[..^3] + "_ApiKey"] = secret;
            }
        }
        return [EncodeSettings(incoming), Content[1], Content[2], Content[3]];
    }
    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("secrets", IncludeSecrets);
        for (int i = 0; i < 4; i++) writer.WriteString(Segments[i], Convert.ToBase64String(Encoding.UTF8.GetBytes(Content[i])));
        writer.WriteEndObject();
    }
    public static CloudSnapshot Read(JsonElement element, bool? legacySecrets = null, CloudSnapshot? fallback = null)
    {
        bool secrets = legacySecrets ?? element.GetProperty("secrets").GetBoolean();
        var content = new string[4];
        var utf8 = new UTF8Encoding(false, true);
        for (int i = 0; i < 4; i++)
        {
            if (!element.TryGetProperty(Segments[i], out var segment))
            {
                if (fallback == null || i == 0) throw new InvalidDataException("配置缺少 " + Segments[i] + " 段。");
                content[i] = fallback.Content[i];
            }
            else
            {
                content[i] = utf8.GetString(Convert.FromBase64String(segment.GetString() ?? ""));
                if (string.IsNullOrWhiteSpace(content[i]))
                {
                    if (legacySecrets != null && fallback != null && i > 0) content[i] = fallback.Content[i];
                    else throw new InvalidDataException("配置的 " + Segments[i] + " 段为空。");
                }
            }
        }
        return new CloudSnapshot(content, secrets);
    }
    public static CloudSnapshot ReadLegacy(string json, CloudSnapshot local)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int version = root.TryGetProperty("version", out var format) ? format.GetInt32() : 1;
        if (root.GetProperty("app").GetString() != "yuSnip" || version is < 1 or > 2)
            throw new InvalidDataException("旧备份应用名或版本不受支持。");
        return Read(root, local.IncludeSecrets, local);
    }
}
