using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ScreenCaptureTool.Core.CloudSync
{
    /// <summary>
    /// 配置导出/导入工具：将本地三份 INI 配置文件打包为 JSON 信封，或从 JSON 信封恢复。
    /// JSON 格式：{"app":"yuSnip","version":2,"exportedAt":"...","ini":"&lt;base64&gt;","actions":"&lt;base64&gt;","shortcuts":"&lt;base64&gt;"}
    /// version 1 的老信封只有 ini 段，导入时动作表与内置快捷键保持本机现状不动。
    /// </summary>
    public static class CloudConfigHelper
    {
        private const string AppName = "yuSnip";
        /// <summary>信封格式版本：v2 起在 ini 之外附带 actions / shortcuts 两段。</summary>
        private const int ConfigVersion = 2;

        /// <summary>两份 ini 的头部标记：导入时用于校验解出的内容确实是该文件，避免把别的东西整体覆盖进去。</summary>
        private const string ActionsHeader = "# yuSnip actions v";
        private const string ShortcutsHeader = "# yuSnip shortcuts v";

        private const string AiProfilePrefix = "AiProfile_";
        private const string AiProfileApiKeySuffix = "_ApiKey";

        /// <summary>
        /// 不参与云同步的 key：保存目录含机器特定路径；
        /// Google Drive 登录凭据属于本机当前账号，同步到其他机器/时间点会互相覆盖，
        /// 导致"登录后仍提示授权已失效"，且凭据外泄到云端文件存在安全风险。
        /// 导出时剔除，导入时保留本地值不被云端覆盖。
        /// </summary>
        private static readonly string[] ExcludedKeys =
        {
            "SaveDirectory",
            "CloudAutoSync",
            "GoogleDriveClientId",
            "GoogleDriveClientSecret",
            "GoogleDriveRefreshToken",
            "GoogleDriveAccessToken",
            "GoogleDriveTokenExpiry",
            "GoogleDriveFileId",
            "GoogleDriveUserEmail",
            "GoogleDriveUserAvatar",
            "GoogleDriveLastSync",
        };

        /// <summary>
        /// 密钥与凭据类固定 key：用户选择"不同步密钥与凭据"时从云端信封剔除，
        /// 导入时若信封缺失则保留本机值（见 <see cref="SecretKeys"/> 之外的索引 key 处理）。
        /// 每个 AI 服务商的 key 是索引 key（AiProfile_{i}_ApiKey），单独按前缀 + 后缀识别。
        /// </summary>
        private static readonly string[] SecretKeys =
        {
            "AiApiKey",
            "BaiduAppId",
            "BaiduSecretKey",
            "YoudaoAppId",
            "YoudaoSecretKey",
            "WebDavUsername",
            "WebDavPassword",
        };

        /// <summary>动作表 ini 路径（Core 内已有权威定义，直接复用）。</summary>
        private static string ActionsIniPath => ScreenCaptureTool.Core.Actions.ActionsStore.ActionsPath;

        /// <summary>内置快捷键 ini 路径，与主程序 Services.BuiltinShortcutsStore.ShortcutsPath 同源同值。</summary>
        private static string ShortcutsIniPath => Path.Combine(AppSettings.SettingsDirectory, "shortcuts.ini");
        /// <summary>录屏画质预设（2026-09-29），与 <c>Recording.RecordPresets.PresetsPath</c> 同源。</summary>
        private static string RecordPresetsIniPath => Recording.RecordPresets.PresetsPath;

        /// <summary>
        /// 生成云端上传/下载用的 JSON 信封：是否附带密钥与凭据由设置项 CloudSyncSecrets 决定。
        /// </summary>
        public static string ExportToJson()
        {
            return BuildEnvelope(AppSettings.Current.CloudSyncSecrets);
        }

        /// <summary>
        /// 打包信封。三份 ini 一律按磁盘现状打包（调用方需先补存未失焦的输入框）。
        /// includeSecrets=false 时剔除密钥与凭据；本地备份恒为 true（全量明文，不受云端开关影响）。
        /// </summary>
        private static string BuildEnvelope(bool includeSecrets)
        {
            lock (CloudSnapshot.FileGate) return BuildEnvelopeLocked(includeSecrets);
        }

        private static string BuildEnvelopeLocked(bool includeSecrets)
        {
            string iniContent = ReadIniFile(AppSettings.SettingsPath);

            // 剔除机器特定与 Google Drive 登录凭据 key
            iniContent = StripIniLines(iniContent, ExcludedKeys);
            // Beta 密钥密文（dp: 前缀）机器绑定，其他机器解不开，不上传
            iniContent = StripIniLineIfValueStartsWith(iniContent, "AiApiKey", "dp:");
            if (!includeSecrets)
            {
                iniContent = StripIniLinesWhere(iniContent, IsSecretIniLine);
            }

            string exportedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            // 手动构建 JSON，避免 NativeAOT 反序列化问题
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"app\":\"").Append(AppName).Append('"');
            sb.Append(",\"version\":").Append(ConfigVersion);
            sb.Append(",\"exportedAt\":\"").Append(exportedAt).Append('"');
            sb.Append(",\"ini\":\"").Append(ToBase64(iniContent)).Append('"');
            sb.Append(",\"actions\":\"").Append(ToBase64(ReadIniFile(ActionsIniPath))).Append('"');
            sb.Append(",\"shortcuts\":\"").Append(ToBase64(ReadIniFile(ShortcutsIniPath))).Append('"');
            sb.Append(",\"recordPresets\":\"").Append(ToBase64(ReadIniFile(RecordPresetsIniPath))).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 从 JSON 信封恢复 settings.ini，并在信封携带相应段时覆盖动作表与内置快捷键。
        /// 保存目录与 Google Drive 登录凭据（见 ExcludedKeys）保留本地值，不被云端覆盖；
        /// 密钥与凭据在信封中缺失时同样保留本地值（用户选择了"不同步密钥"或恢复的是 v1 老信封）。
        /// </summary>
        public static CloudSyncResult ImportFromJson(string json)
        {
            lock (CloudSnapshot.FileGate) return ImportFromJsonLocked(json);
        }

        private static CloudSyncResult ImportFromJsonLocked(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                    return CloudSyncResult.Fail("云端配置内容为空");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 验证应用名
                if (root.TryGetProperty("app", out var appProp))
                {
                    string app = appProp.GetString() ?? "";
                    if (!string.Equals(app, AppName, StringComparison.OrdinalIgnoreCase))
                        AppLogger.Warn($"CloudConfigHelper.ImportFromJson: 应用名不匹配 ({app})，继续恢复");
                }

                int version = root.TryGetProperty("version", out var versionProp)
                              && versionProp.TryGetInt32(out int parsedVersion)
                    ? parsedVersion : 1;

                if (!root.TryGetProperty("ini", out var iniProp))
                    return CloudSyncResult.Fail("配置文件格式无效：缺少 ini 字段");

                string base64 = iniProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(base64))
                    return CloudSyncResult.Fail("配置文件内容为空");

                string iniContent;
                try
                {
                    iniContent = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
                catch (FormatException)
                {
                    return CloudSyncResult.Fail("配置文件 Base64 解码失败，数据可能已损坏");
                }

                // 剔除云端内容中的敏感/机器特定 key（兼容历史云端文件中已包含的凭据），
                // 再回填本地当前值，确保本机登录状态不被旧数据覆盖
                iniContent = StripIniLines(iniContent, ExcludedKeys);
                foreach (string key in ExcludedKeys)
                {
                    string? localValue = ReadLocalIniValue(key);
                    if (!string.IsNullOrWhiteSpace(localValue))
                        iniContent += "\r\n" + key + "=" + localValue;
                }

                // AiApiKey 特殊处理：云端若携带已移除 Beta 通道的 dp: 机器绑定密文（他机不可解），
                // 剔除之并回填本地明文 key
                if (ContainsIniLineWithValuePrefix(iniContent, "AiApiKey", "dp:"))
                {
                    iniContent = StripIniLines(iniContent, new[] { "AiApiKey" });
                    string? localKey = ReadLocalIniValue("AiApiKey");
                    if (!string.IsNullOrWhiteSpace(localKey) && !localKey.StartsWith("dp:", StringComparison.Ordinal))
                        iniContent += "\r\nAiApiKey=" + localKey;
                }

                // 信封未携带的密钥与凭据（选了"不同步"或 v1 老信封）保留本机现值
                iniContent = BackfillMissingSecrets(iniContent);

                // 写入 INI 文件
                Directory.CreateDirectory(AppSettings.SettingsDirectory);
                File.WriteAllText(AppSettings.SettingsPath, iniContent, Encoding.UTF8);

                bool actionsRestored = TryRestoreSidecar(root, "actions", ActionsIniPath, ActionsHeader);
                bool shortcutsRestored = TryRestoreSidecar(root, "shortcuts", ShortcutsIniPath, ShortcutsHeader);
                // 老信封没有这一段 ⇒ 不动本机预设（TryRestoreSidecar 缺段即返回 false）
                bool presetsRestored = TryRestoreSidecar(root, "recordPresets", RecordPresetsIniPath, Recording.RecordPresets.FileHeader);
                if (presetsRestored) Recording.RecordPresets.Reload();

                var result = CloudSyncResult.Success();
                // v1 老信封只含 settings 段，动作表与快捷键未被恢复，供调用方如实提示
                result.LegacyEnvelope = version < 2;
                result.SidecarsRestored = actionsRestored || shortcutsRestored || presetsRestored;
                return result;
            }
            catch (JsonException ex)
            {
                return CloudSyncResult.Fail("配置文件 JSON 解析失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("恢复配置失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 恢复信封中的附带段（动作表 / 内置快捷键）。段缺失或为空表示"本次不涉及"，本机文件保持原样。
        /// </summary>
        private static bool TryRestoreSidecar(JsonElement root, string sectionName, string targetPath, string expectedHeader)
        {
            try
            {
                if (!root.TryGetProperty(sectionName, out var section)) return false;
                string base64 = section.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(base64)) return false;

                string content;
                try
                {
                    content = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
                catch (FormatException)
                {
                    AppLogger.Warn($"CloudConfigHelper：{sectionName} 段 Base64 解码失败，跳过该项恢复");
                    return false;
                }

                if (!content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith(expectedHeader, StringComparison.Ordinal))
                {
                    AppLogger.Warn($"CloudConfigHelper：{sectionName} 段缺少预期文件头标记，跳过该项恢复以免覆盖本机数据");
                    return false;
                }

                Directory.CreateDirectory(AppSettings.SettingsDirectory);
                File.WriteAllText(targetPath, content, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"CloudConfigHelper：恢复 {sectionName} 段失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 信封中缺失的密钥与凭据回填本机现值：固定 key 直接回填；
        /// 各 AI 服务商的 key 是索引 key，必须按 profile 的 Id 匹配回填——
        /// 按索引回填会在两台机器服务商顺序不同时把 Key 配到错误的服务商上。
        /// </summary>
        private static string BackfillMissingSecrets(string iniContent)
        {
            var append = new StringBuilder();

            foreach (string key in SecretKeys)
            {
                if (ContainsIniKey(iniContent, key)) continue;
                string? local = ReadLocalIniValue(key);
                // dp: 是本机绑定的受保护密文，跨机无意义，不回填
                if (string.IsNullOrWhiteSpace(local) || local.StartsWith("dp:", StringComparison.Ordinal)) continue;
                append.Append("\r\n").Append(key).Append('=').Append(local);
            }

            append.Append(BuildProfileKeyBackfill(iniContent));

            return append.Length == 0 ? iniContent : iniContent + append;
        }

        /// <summary>为信封内未携带 ApiKey 的每个 AI 服务商，按 Id 找出本机对应服务商的 Key 生成回填行。</summary>
        private static StringBuilder BuildProfileKeyBackfill(string iniContent)
        {
            var result = new StringBuilder();
            var envelopeProfiles = ParseAiProfiles(iniContent);
            if (envelopeProfiles.Count == 0) return result;

            var localProfiles = ParseAiProfiles(ReadIniFile(AppSettings.SettingsPath));

            foreach (var envelope in envelopeProfiles)
            {
                if (envelope.Value.HasApiKey) continue;
                if (string.IsNullOrWhiteSpace(envelope.Value.Id)) continue;

                foreach (var local in localProfiles)
                {
                    if (!string.Equals(local.Value.Id, envelope.Value.Id, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrWhiteSpace(local.Value.ApiKey) || local.Value.ApiKey!.StartsWith("dp:", StringComparison.Ordinal)) break;

                    result.Append("\r\n").Append(AiProfilePrefix).Append(envelope.Key)
                          .Append(AiProfileApiKeySuffix).Append('=').Append(local.Value.ApiKey);
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// 解析 INI 文本中的 AiProfile_{i}_ 索引段，返回「索引 → (Id, 是否带 ApiKey, ApiKey 值)」。
        /// </summary>
        private static Dictionary<int, (string? Id, bool HasApiKey, string? ApiKey)> ParseAiProfiles(string iniContent)
        {
            var profiles = new Dictionary<int, (string? Id, bool HasApiKey, string? ApiKey)>();
            if (string.IsNullOrEmpty(iniContent)) return profiles;

            string[] lines = iniContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int start = SkipLeadingWhitespace(line);
                if (!line.Substring(start).StartsWith(AiProfilePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line.Substring(start, eq - start).Trim();
                string remainder = key.Substring(AiProfilePrefix.Length);
                int separator = remainder.IndexOf('_');
                if (separator <= 0) continue;
                if (!int.TryParse(remainder.Substring(0, separator), out int index) || index < 0) continue;

                string field = remainder.Substring(separator + 1);
                string value = line.Substring(eq + 1).Trim();

                profiles.TryGetValue(index, out var current);
                if (string.Equals(field, "Id", StringComparison.OrdinalIgnoreCase))
                    current.Id = value;
                else if (string.Equals(field, "ApiKey", StringComparison.OrdinalIgnoreCase))
                {
                    current.HasApiKey = true;
                    current.ApiKey = value;
                }
                else continue;

                profiles[index] = current;
            }

            return profiles;
        }

        /// <summary>判断一行 INI 是否属于密钥与凭据（固定 key 或 AiProfile_{i}_ApiKey 索引 key）。</summary>
        private static bool IsSecretIniLine(string line)
        {
            for (int k = 0; k < SecretKeys.Length; k++)
            {
                if (IsIniLineKey(line, SecretKeys[k])) return true;
            }

            int start = SkipLeadingWhitespace(line);
            int eq = line.IndexOf('=');
            if (eq <= start) return false;

            string key = line.Substring(start, eq - start).Trim();
            return key.StartsWith(AiProfilePrefix, StringComparison.OrdinalIgnoreCase)
                   && key.EndsWith(AiProfileApiKeySuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将 INI 文本中满足条件的行剔除。
        /// </summary>
        private static string StripIniLinesWhere(string iniContent, Func<string, bool> shouldStrip)
        {
            if (string.IsNullOrEmpty(iniContent)) return iniContent;
            string[] lines = iniContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var kept = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (shouldStrip(lines[i])) continue;
                kept.Add(lines[i]);
            }
            return string.Join("\r\n", kept);
        }

        /// <summary>把文本按 UTF-8 编码转为 Base64 字符串。</summary>
        private static string ToBase64(string content)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        /// <summary>读取 ini 文件全文，文件不存在或读取失败返回空串。</summary>
        private static string ReadIniFile(string path)
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"CloudConfigHelper.ReadIniFile 读取 {path} 失败：" + ex.Message);
            }
            return "";
        }

        /// <summary>
        /// 从 INI 文本中剔除指定 key 集合的行（不区分大小写，忽略前导空白）。
        /// </summary>
        private static string StripIniLines(string iniContent, string[] keys)
        {
            if (string.IsNullOrEmpty(iniContent)) return iniContent;
            string[] lines = iniContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var kept = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                bool excluded = false;
                for (int k = 0; k < keys.Length; k++)
                {
                    if (IsIniLineKey(lines[i], keys[k])) { excluded = true; break; }
                }
                if (!excluded) kept.Add(lines[i]);
            }
            return string.Join("\r\n", kept);
        }

        /// <summary>判断 INI 文本中是否存在指定 key 的行（存在即视为信封已给出该值，含空值）。</summary>
        private static bool ContainsIniKey(string iniContent, string key)
        {
            if (string.IsNullOrEmpty(iniContent)) return false;
            string[] lines = iniContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsIniLineKey(lines[i], key)) return true;
            }
            return false;
        }

        /// <summary>判断 INI 文本中是否存在指定 key 且值以指定前缀开头的行。</summary>
        private static bool ContainsIniLineWithValuePrefix(string iniContent, string key, string valuePrefix)
        {
            string[] lines = iniContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsIniLineKey(lines[i], key)) continue;
                int eq = lines[i].IndexOf('=');
                if (eq < 0) continue;
                string value = lines[i][(eq + 1)..].Trim();
                if (value.StartsWith(valuePrefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>从 INI 文本中剔除指定 key 且值以指定前缀开头的行。</summary>
        private static string StripIniLineIfValueStartsWith(string iniContent, string key, string valuePrefix)
        {
            if (string.IsNullOrEmpty(iniContent)) return iniContent;
            string[] lines = iniContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var kept = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsIniLineKey(lines[i], key))
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq >= 0)
                    {
                        string value = lines[i][(eq + 1)..].Trim();
                        if (value.StartsWith(valuePrefix, StringComparison.Ordinal)) continue;
                    }
                }
                kept.Add(lines[i]);
            }
            return string.Join("\r\n", kept);
        }

        /// <summary>返回一行首个非空白字符的下标。</summary>
        private static int SkipLeadingWhitespace(string line)
        {
            int start = 0;
            while (start < line.Length && (line[start] == ' ' || line[start] == '\t')) start++;
            return start;
        }

        /// <summary>
        /// 判断一行 INI 是否为指定 key（不区分大小写，忽略前导空白）。
        /// </summary>
        private static bool IsIniLineKey(string line, string key)
        {
            if (string.IsNullOrEmpty(line)) return false;
            int start = 0;
            while (start < line.Length && (line[start] == ' ' || line[start] == '\t')) start++;
            if (line.Length - start < key.Length + 1) return false;
            for (int i = 0; i < key.Length; i++)
            {
                if (char.ToUpperInvariant(line[start + i]) != char.ToUpperInvariant(key[i])) return false;
            }
            return line[start + key.Length] == '=';
        }

        /// <summary>
        /// 读取本地 settings.ini 中指定 key 的值。
        /// </summary>
        private static string? ReadLocalIniValue(string key)
        {
            try
            {
                if (!File.Exists(AppSettings.SettingsPath)) return null;
                string[] lines = File.ReadAllLines(AppSettings.SettingsPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsIniLineKey(lines[i], key))
                    {
                        int eq = lines[i].IndexOf('=');
                        if (eq >= 0) return lines[i].Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 将配置导出为本地备份文件（恒为全量明文，不受云端密钥同步开关影响）。
        /// </summary>
        public static CloudSyncResult ExportToFile(string filePath)
        {
            try
            {
                string json = BuildEnvelope(true);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");
                File.WriteAllText(filePath, json, Encoding.UTF8);
                return CloudSyncResult.Success();
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("导出配置到文件失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 从本地备份文件恢复配置。
        /// </summary>
        public static CloudSyncResult ImportFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return CloudSyncResult.Fail("文件不存在：" + filePath);

                string json = File.ReadAllText(filePath, Encoding.UTF8);
                return ImportFromJson(json);
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("从文件恢复配置失败：" + ex.Message);
            }
        }
    }
}
