using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ScreenCaptureTool.Core.Actions
{
    /// <summary>
    /// 动作存储：%APPDATA%\yuSnip\actions.ini，行式纯文本（与 settings.ini 同风格，
    /// 规避 NativeAOT 下 JSON 反射序列化问题）。
    ///
    /// 格式（Tab 分隔，字段内 \、\t、\r、\n 转义）：
    /// <code>action&lt;TAB&gt;Id&lt;TAB&gt;IsBuiltin&lt;TAB&gt;Enabled&lt;TAB&gt;Mods&lt;TAB&gt;VK&lt;TAB&gt;Name&lt;TAB&gt;Script&lt;TAB&gt;MouseMods&lt;TAB&gt;MouseButton&lt;TAB&gt;MouseTrigger</code>
    /// v4 末尾三列为独立鼠标绑定；v5 再追加 VK／回放策略／真实辅助键／按钮／扫描码／扩展位。
    /// 新引导键行的 v4 鼠标列清零，旧版读取时安全视为未绑定。
    ///
    /// 内置动作（22 条，对应原"预设指令库"+ 截图/快速贴图两条热键通道 + 录屏/快速录制）：
    /// 已存在于动作表中的内置动作，加载时脚本字段强制还原为 canonical，名称/热键归用户；
    /// 新增的预设不自动补录（用户已精简过的动作表不该被程序塞回去），
    /// 由用户在「设置 → 快捷键与动作 → 新增动作」的预设下拉里取用；
    /// 例外是带出厂默认热键的新动作（录屏 F9），按文件头版本号一次性补录（v2→v3）。
    /// 首次运行时从 AppSettings 旧热键字段（HotKeyModifiers 等）迁移截图/贴图绑定。
    /// </summary>
    public static class ActionsStore
    {
        /// <summary>动作文件路径（与 settings.ini 同目录）。</summary>
        public static string ActionsPath => Path.Combine(AppSettings.SettingsDirectory, "actions.ini");

        // —— 内置动作目录（Id / 名称 / canonical 脚本） ——
        // 命名规范：功能名 +（行为变体），如「截图并OCR（弹窗）/（仅提示）」「截图翻译（覆盖贴图）」。
        public static readonly (string Id, string Name, string Script)[] BuiltinCatalog =
        {
            ("builtin.capture",             "区域截图",                "yuSnip.screenShot();"),
            ("builtin.pin",                 "快速贴图",                "yuSnip.pinFromClipboard();"),
            ("builtin.captureCopy",         "截图并复制",              "yuSnip.screenShot('Copy');"),
            ("builtin.captureSave",         "截图并保存",              "yuSnip.screenShot('Save');"),
            ("builtin.capturePin",          "截图并贴图",              "yuSnip.screenShot('Pin');"),
            ("builtin.longScroll",          "长截图",                  "yuSnip.longScroll();"),
            ("builtin.captureOcr",          "截图并OCR（弹窗）",       "yuSnip.screenShot('Ocr');"),
            ("builtin.captureOcrToast",     "截图并OCR（仅提示）",     "yuSnip.screenShot('OcrToast');"),
            ("builtin.captureVision",       "截图并识图（默认）",      "yuSnip.screenShot('Vision');"),
            ("builtin.captureVisionPrompt", "截图并直接识图（提问）",  "yuSnip.screenShot('VisionPrompt');"),
            ("builtin.captureMattingGeneral",  "截图并抠图（通用主体）", "yuSnip.screenShot('MattingGeneral');"),
            ("builtin.captureMattingPortrait", "截图并抠图（人像精细）", "yuSnip.screenShot('MattingPortrait');"),
            ("builtin.translateImage",      "截图翻译（覆盖贴图）",    "yuSnip.translate('ImageToImage');"),
            ("builtin.translateText",       "截图翻译（文本窗口）",    "yuSnip.translate('ImageToText');"),
            ("builtin.translateSilent",     "截图翻译（静默剪贴板）",  "yuSnip.translate('Silent');"),
            ("builtin.translateDefault",    "截图翻译（默认）",        "yuSnip.translate();"),
            ("builtin.translateClipboard",  "翻译剪贴板（文本窗口）",  "yuSnip.translate('Clipboard');"),
            ("builtin.translateSticker",    "翻译贴图（焦点）",        "yuSnip.translate('Sticker');"),
            ("builtin.record",              "开始 / 取消录制",         "yuSnip.screenRecord();"),
            ("builtin.recordQuick",         "快速录制（框选即录）",    "yuSnip.screenRecord('Quick');"),
            ("builtin.toggleStickers",      "隐藏/显示所有贴图",       "yuSnip.toggleAllStickers();"),
            ("builtin.restoreLastSticker",  "恢复上次关闭的贴图",      "yuSnip.restoreLastSticker();"),
            ("builtin.destroyAllPins",      "关闭所有贴图",            "yuSnip.destroyAllPin();"),
        };

        /// <summary>
        /// 按功能分类组织的内置动作目录（用于「新增/编辑动作 → 选择动作预设」下拉列表分组展示）：
        /// 截图、录屏、贴图、OCR 与识图、翻译。
        /// </summary>
        public static readonly (string Category, (string Id, string Name, string Script)[] Items)[] CategorizedBuiltins =
        {
            ("截图", new[]
            {
                ("builtin.capture",             "区域截图",                "yuSnip.screenShot();"),
                ("builtin.captureCopy",         "截图并复制",              "yuSnip.screenShot('Copy');"),
                ("builtin.captureSave",         "截图并保存",              "yuSnip.screenShot('Save');"),
                ("builtin.capturePin",          "截图并贴图",              "yuSnip.screenShot('Pin');"),
                ("builtin.longScroll",          "长截图",                  "yuSnip.longScroll();"),
            }),
            ("录屏", new[]
            {
                ("builtin.recordQuick",         "快速录制（框选即录）",    "yuSnip.screenRecord('Quick');"),
                ("builtin.record",              "开始 / 取消录制",         "yuSnip.screenRecord();"),
            }),
            ("贴图", new[]
            {
                ("builtin.pin",                 "快速贴图",                "yuSnip.pinFromClipboard();"),
                ("builtin.restoreLastSticker",  "恢复上次关闭的贴图",      "yuSnip.restoreLastSticker();"),
                ("builtin.toggleStickers",      "隐藏/显示所有贴图",       "yuSnip.toggleAllStickers();"),
                ("builtin.destroyAllPins",      "关闭所有贴图",            "yuSnip.destroyAllPin();"),
            }),
            ("OCR 与识图", new[]
            {
                ("builtin.captureOcr",          "截图并OCR（弹窗）",       "yuSnip.screenShot('Ocr');"),
                ("builtin.captureOcrToast",     "截图并OCR（仅提示）",     "yuSnip.screenShot('OcrToast');"),
                ("builtin.captureVision",       "截图并识图（默认）",      "yuSnip.screenShot('Vision');"),
                ("builtin.captureVisionPrompt", "截图并直接识图（提问）",  "yuSnip.screenShot('VisionPrompt');"),
                ("builtin.captureMattingGeneral",  "截图并抠图（通用主体）", "yuSnip.screenShot('MattingGeneral');"),
                ("builtin.captureMattingPortrait", "截图并抠图（人像精细）", "yuSnip.screenShot('MattingPortrait');"),
            }),
            ("翻译", new[]
            {
                ("builtin.translateDefault",    "截图翻译（默认）",        "yuSnip.translate();"),
                ("builtin.translateImage",      "截图翻译（覆盖贴图）",    "yuSnip.translate('ImageToImage');"),
                ("builtin.translateText",       "截图翻译（文本窗口）",    "yuSnip.translate('ImageToText');"),
                ("builtin.translateSilent",     "截图翻译（静默剪贴板）",  "yuSnip.translate('Silent');"),
                ("builtin.translateClipboard",  "翻译剪贴板（文本窗口）",  "yuSnip.translate('Clipboard');"),
                ("builtin.translateSticker",    "翻译贴图（焦点）",        "yuSnip.translate('Sticker');"),
            }),
        };

        /// <summary>出厂默认展示的核心常用动作 Id 列表（6项）。其余预设收拢在"新增动作"弹窗中。</summary>
        public static readonly string[] DefaultActionIds =
        {
            "builtin.capture",
            "builtin.pin",
            "builtin.captureOcr",
            "builtin.translateImage",
            "builtin.record",
            "builtin.toggleStickers",
        };

        // Win32 修饰键位（与 ActionDefinition.HotKeyModifiers 位定义一致）
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        /// <summary>
        /// 内置动作的出厂默认热键（参考 Snipaste / QQ 截图等主流工具习惯）：
        /// 截图 Ctrl+Alt+A（QQ/微信习惯）、快速贴图 F3 与 显隐贴图 Shift+F3（Snipaste 习惯）、
        /// 截图并OCR Ctrl+Alt+O 与 截图翻译 Ctrl+Alt+F（QQ 屏幕识图/翻译习惯）、
        /// 录屏 F9（稿 4 热键矩阵，D26 首版固定键；裸功能键，与 F3 同类）。
        /// 其余内置动作出厂不绑定，由用户在「设置 → 快捷键与动作」自行录制。
        /// </summary>
        private static readonly Dictionary<string, (uint Mods, uint Vk)> BuiltinDefaultHotKeys = new()
        {
            ["builtin.capture"] = (MOD_CONTROL | MOD_ALT, 0x41),       // Ctrl+Alt+A
            ["builtin.pin"] = (0, 0x72),                                // F3
            ["builtin.captureOcr"] = (MOD_CONTROL | MOD_ALT, 0x4F),     // Ctrl+Alt+O
            ["builtin.translateImage"] = (MOD_CONTROL | MOD_ALT, 0x46), // Ctrl+Alt+F
            ["builtin.record"] = (0, 0x78),                             // F9
            ["builtin.toggleStickers"] = (MOD_SHIFT, 0x72),             // Shift+F3
        };

        /// <summary>v1→v2 迁移时仅补录这三个"新增默认绑定"（v1 出厂即未绑定，补上不覆盖用户已有绑定）。</summary>
        private static readonly string[] V2NewDefaultIds =
        {
            "builtin.captureOcr", "builtin.translateImage", "builtin.toggleStickers",
        };

        /// <summary>查询内置动作的出厂默认热键；无默认绑定时返回 false。</summary>
        public static bool TryGetBuiltinDefaultHotKey(string id, out uint mods, out uint vk)
        {
            if (BuiltinDefaultHotKeys.TryGetValue(id, out var def))
            {
                mods = def.Mods;
                vk = def.Vk;
                return true;
            }
            mods = 0;
            vk = 0;
            return false;
        }

        /// <summary>按 Id 查找预设动作目录项；找不到时返回 null。</summary>
        public static (string Id, string Name, string Script)? FindPresetById(string id)
        {
            foreach (var item in BuiltinCatalog)
            {
                if (item.Id == id) return item;
            }
            return null;
        }

        /// <summary>按脚本内容匹配预设动作目录项；找不到时返回 null。</summary>
        public static (string Id, string Name, string Script)? FindPresetByScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return null;
            string normalized = script.Trim();
            foreach (var item in BuiltinCatalog)
            {
                if (string.Equals(item.Script.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        /// <summary>生成默认动作表（常用 5 个预设）；截图/贴图热键从旧设置字段迁移，其余内置动作套用出厂默认绑定。</summary>
        public static List<ActionDefinition> CreateDefaults(AppSettings legacy)
        {
            var list = new List<ActionDefinition>(DefaultActionIds.Length);
            foreach (var id in DefaultActionIds)
            {
                var preset = FindPresetById(id);
                if (preset == null) continue;

                var (pId, pName, pScript) = preset.Value;
                var def = new ActionDefinition
                {
                    Id = pId,
                    Name = pName,
                    Script = pScript,
                    IsBuiltin = true,
                    Enabled = true,
                };
                if (BuiltinDefaultHotKeys.TryGetValue(pId, out var hotkey))
                {
                    def.HotKeyModifiers = hotkey.Mods;
                    def.HotKeyVirtualKey = hotkey.Vk;
                }
                list.Add(def);
            }

            // 旧热键字段迁移（一次性）：截图热键 → builtin.capture，贴图热键 → builtin.pin；
            // 旧字段为空（0）时保留出厂默认，不覆盖
            if (legacy != null)
            {
                if (legacy.HotKeyVirtualKey != 0 && list.Count > 0)
                {
                    list[0].HotKeyModifiers = legacy.HotKeyModifiers;
                    list[0].HotKeyVirtualKey = legacy.HotKeyVirtualKey;
                }
                if (legacy.StickerHotKeyVirtualKey != 0 && list.Count > 1)
                {
                    list[1].HotKeyModifiers = legacy.StickerHotKeyModifiers;
                    list[1].HotKeyVirtualKey = legacy.StickerHotKeyVirtualKey;
                }
            }
            return list;
        }

        /// <summary>
        /// 加载动作表。文件不存在时生成常用默认表（4个核心预设）并立即落盘。
        /// 读取已有动作时，内置动作强制还原 canonical 脚本，自定义动作原样保留。
        /// </summary>
        public static List<ActionDefinition> Load() => Load(ActionsPath);

        internal static List<ActionDefinition> Load(string path)
        {
            var result = new List<ActionDefinition>();
            int fileVersion = 1;

            bool fileExists = File.Exists(path);
            if (fileExists)
            {
                try
                {
                    foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0) continue;
                        if (line.StartsWith("#"))
                        {
                            // 头部版本标记："# yuSnip actions vN —— ..."
                            const string marker = ActionFileFormat.HeaderPrefix;
                            if (line.StartsWith(marker, StringComparison.Ordinal))
                            {
                                int i = marker.Length;
                                int v = 0;
                                while (i < line.Length && char.IsDigit(line[i]))
                                {
                                    v = v * 10 + (line[i] - '0');
                                    i++;
                                }
                                if (v > 0) fileVersion = v;
                            }
                            continue;
                        }
                        if (!line.StartsWith("action\t", StringComparison.Ordinal)) continue;

                        string[] fields = line.Substring("action\t".Length).Split('\t');
                        if (fields.Length < 7) continue;

                        string id = Unescape(fields[0]);
                        if (string.IsNullOrEmpty(id)) continue;

                        bool isBuiltin = fields[1] == "1" || id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase);
                        string script = Unescape(fields[6]);
                        string name = Unescape(fields[5]);

                        if (isBuiltin)
                        {
                            var preset = FindPresetById(id);
                            if (preset != null)
                            {
                                script = preset.Value.Script;
                                if (string.IsNullOrEmpty(name)) name = preset.Value.Name;
                            }
                        }

                        var def = new ActionDefinition
                        {
                            Id = id,
                            IsBuiltin = isBuiltin,
                            Enabled = fields[2] != "0",
                            HotKeyModifiers = ParseUInt(fields[3]),
                            HotKeyVirtualKey = ParseUInt(fields[4]),
                            Name = name,
                            Script = script,
                            MouseModifiers = fields.Length > 7 ? ParseUInt(fields[7]) : 0,
                            MouseButton = fields.Length > 8 ? (ActionMouseButton)ParseUInt(fields[8]) : ActionMouseButton.None,
                            MouseTrigger = fields.Length > 9 ? (ActionMouseTrigger)ParseUInt(fields[9]) : ActionMouseTrigger.Click,
                        };

                        ReadMouseExtension(def, fields);
                        if (fileVersion >= 6) ReadKeyboardExtension(def, fields, fileVersion);

                        result.Add(def);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ActionsStore.Load 读取失败，使用默认动作表: " + ex.Message);
                    result.Clear();
                }
            }

            if (!fileExists || result.Count == 0)
            {
                // 首次运行或空文件：默认表（常用 6 个预设 + 出厂默认绑定）直接落盘
                AppSettings legacy;
                try { legacy = AppSettings.Current; } catch { legacy = new AppSettings(); }
                result = CreateDefaults(legacy);
                Save(result, path);
            }
            else if (fileVersion < 3 && !result.Exists(a => a.Id == "builtin.record"))
            {
                // v2→v3 一次性补录：录屏动作带出厂热键 F9。补录后文件头升为 v3，
                // 用户之后手动删除该动作不会再被塞回（普通预设仍遵守"不自动补录"）
                var rec = new ActionDefinition
                {
                    Id = "builtin.record",
                    Name = "开始 / 取消录制",
                    Script = "yuSnip.screenRecord();",
                    IsBuiltin = true,
                    Enabled = true,
                };
                if (TryGetBuiltinDefaultHotKey("builtin.record", out uint rm, out uint rvk))
                {
                    rec.HotKeyModifiers = rm;
                    rec.HotKeyVirtualKey = rvk;
                }
                result.Add(rec);
                Save(result, path);
            }

            if (NormalizeDuplicateIds(result))
            {
                try
                {
                    BackupDuplicateIds(path);
                    Save(result, path);
                }
                catch (Exception ex) { AppLogger.Warn("ActionsStore: 重复标识备份失败，原文件保持: " + ex.GetType().Name); }
            }
            return result;
        }

        /// <summary>同一预设的额外绑定作为独立副本保留，不删除动作或热键。</summary>
        public static void EnsureUniqueIdentity(ActionDefinition action, IReadOnlyList<ActionDefinition> others, ActionDefinition? replacing = null)
        {
            while (others.Any(other => !ReferenceEquals(other, action) && !ReferenceEquals(other, replacing) && other.Id == action.Id))
            {
                action.Id = ActionDefinition.NewCustomId();
                action.IsBuiltin = false;
            }
        }

        private static bool NormalizeDuplicateIds(IReadOnlyList<ActionDefinition> actions)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false;
            foreach (var action in actions)
            {
                if (ids.Add(action.Id)) continue;
                do { action.Id = ActionDefinition.NewCustomId(); } while (!ids.Add(action.Id));
                action.IsBuiltin = false;
                changed = true;
            }
            return changed;
        }

        private static void BackupDuplicateIds(string path)
        {
            if (File.Exists(path) && !File.Exists(path + ".duplicate-ids.bak"))
                File.Copy(path, path + ".duplicate-ids.bak", false);
        }

        /// <summary>保存动作表（UTF-8 无 BOM，原子替换写入）。</summary>
        public static void Save(IReadOnlyList<ActionDefinition> actions) => Save(actions, ActionsPath);

        internal static void Save(IReadOnlyList<ActionDefinition> actions, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (NormalizeDuplicateIds(actions)) BackupDuplicateIds(path);
                var sb = new StringBuilder();
                sb.Append(ActionFileFormat.Header).AppendLine(" — 动作定义（快捷键/全局鼠标/外部调用）。请勿手工编辑，结构变动由程序维护。");
                foreach (ActionDefinition a in actions)
                {
                    sb.Append("action\t")
                        .Append(Escape(a.Id)).Append('\t')
                        .Append(a.IsBuiltin ? '1' : '0').Append('\t')
                        .Append(a.Enabled ? '1' : '0').Append('\t')
                        .Append(ActionKeyboardBinding.IsExtended(a.HotKeyModifiers) ? 0 : a.HotKeyModifiers).Append('\t')
                        .Append(ActionKeyboardBinding.IsExtended(a.HotKeyModifiers) ? 0 : a.HotKeyVirtualKey).Append('\t')
                        .Append(Escape(a.Name)).Append('\t')
                        .Append(Escape(a.Script)).Append('\t')
                        // 引导键行在旧列中必须未绑定，旧版不能降级抢占宽组合。
                        .Append(a.MousePrefixKey != default ? 0 : a.MouseModifiers).Append('\t')
                        .Append(a.MousePrefixKey != default ? 0 : (uint)a.MouseButton).Append('\t')
                        .Append((uint)a.MouseTrigger).Append('\t')
                        .Append(a.MousePrefixKey.VirtualKey).Append('\t')
                        .Append(a.MousePrefixKey != default ? 1 : 0).Append('\t')
                        .Append(a.MouseModifiers).Append('\t')
                        .Append((uint)a.MouseButton).Append('\t')
                        .Append(a.MousePrefixKey.ScanCode).Append('\t')
                        .Append(a.MousePrefixKey.Extended ? 1 : 0).Append('\t')
                        .Append(ActionKeyboardBinding.IsExtended(a.HotKeyModifiers) ? a.HotKeyModifiers : 0).Append('\t')
                        .Append(ActionKeyboardBinding.IsExtended(a.HotKeyModifiers) ? a.HotKeyVirtualKey : 0).Append('\n');
                }

                string tmp = path + ".tmp";
                lock (CloudSync.CloudSnapshot.FileGate)
                {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                CloudSync.CloudConfigTransaction.ReplaceFile(tmp, path);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ActionsStore.Save 失败: " + ex.Message);
            }
        }

        internal static void ReadMouseExtension(ActionDefinition action, string[] fields)
        {
            if (fields.Length <= 10) return;
            // 扩展块一旦声称存在但不完整／未知，不得退回更宽的旧组合。
            if (fields.Length is 16 or 18 && fields[10] == "0" && fields[11] == "0") return;
            action.MouseModifiers = 0; action.MouseButton = ActionMouseButton.None;
            if (fields.Length is not (16 or 18) || fields[11] != "1"
                || !uint.TryParse(fields[10], out uint vk) || !uint.TryParse(fields[12], out uint mods)
                || !uint.TryParse(fields[13], out uint button) || !uint.TryParse(fields[14], out uint scan)
                || fields[15] is not ("0" or "1")) return;
            var key = new ActionMouseKey(vk, scan, fields[15] == "1");
            if (!key.IsValid || mods > 15 || button is < 1 or > 5) return;
            action.MousePrefixKey = key; action.MouseModifiers = mods; action.MouseButton = (ActionMouseButton)button;
        }

        internal static void ReadKeyboardExtension(ActionDefinition action, string[] fields, int version)
        {
            // 新绑定旧列清零；损坏或未来扩展不能退化成裸主键。
            if (version == 6 && fields.Length == 18 && fields[16] == "0" && fields[17] == "0") return;
            action.HotKeyModifiers = 0; action.HotKeyVirtualKey = 0;
            if (version != 6 || fields.Length != 18
                || !uint.TryParse(fields[16], out uint modifiers) || !uint.TryParse(fields[17], out uint key)
                || !ActionKeyboardBinding.IsExtended(modifiers) || !ActionKeyboardBinding.IsValid(modifiers, key)) return;
            action.HotKeyModifiers = modifiers; action.HotKeyVirtualKey = key;
        }

        private static uint ParseUInt(string s)
        {
            return uint.TryParse(s, out uint v) ? v : 0;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n switch
                    {
                        't' => '\t',
                        'r' => '\r',
                        'n' => '\n',
                        '\\' => '\\',
                        _ => n,
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
