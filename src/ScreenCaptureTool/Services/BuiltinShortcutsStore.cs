using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Input;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;

namespace ScreenCaptureTool.Services
{
    /// <summary>
    /// 内置快捷键绑定存储：截图会话（capture）、贴图窗口（sticker）与录制会话（record）三组快捷键，
    /// 用户可在「设置 → 快捷键与动作」中重新录制。持久化到 %APPDATA%\yuSnip\shortcuts.ini。
    ///
    /// 设计要点：
    /// - 目录（Catalog）给出全部可编辑项与出厂默认值；文件只保存"与默认不同"的覆盖项，
    ///   这样未来版本调整默认值时，未自定义过的用户能自动获得新默认；
    /// - 与全局动作热键（actions.ini，Win32 全局注册）完全独立：capture/sticker 两组只在窗口前台时生效，
    ///   使用 Avalonia 的 Key/KeyModifiers 匹配、不做全局注册；record 那三枚可改项确实要临时
    ///   <c>RegisterHotKey</c>（录制浮层拿不到常规键盘焦点），但随录随卸、且经 <see cref="LockedIds"/>
    ///   把两条钩子兜底键锁死；
    /// - 同组内不允许两项绑定相同按键（保存时由设置页校验）。
    /// </summary>
    public static class BuiltinShortcutsStore
    {
        private static int _revision;
        public static int Revision => System.Threading.Volatile.Read(ref _revision);
        public const string GroupCapture = "capture";
        public const string GroupSticker = "sticker";
        /// <summary>录屏会话键（2026-09-29 建组）。此前录屏的键散在三套机制里
        /// （全局动作 F9 ／ 会话内临时全局键 F10·F11 ／ 低级钩子 Ctrl+Del·Esc），
        /// 设置页连一个"录屏"分组都没有。本组收录屏控制键，快速启停从选中录屏工具时生效；
        /// F9 属"全局动作"，仍留在 actions.ini 那套里，不重复登记。</summary>
        public const string GroupRecord = "record";
        public const string RecordToggleId = "record.toggle";

        /// <summary>锁死项：进目录只为"看得见"，界面不给改、文件覆盖项也不认。
        /// 为什么锁这两条——<c>record.cancel</c> 的 Esc 在低级钩子里是<b>放行不吞键</b>的
        /// （<c>RecordFlow</c> 钩子注释原话"不新增全局吞键"），换成别的键就得改吞键语义，
        /// 等于在录制期把用户的一个普通按键抢走；<c>record.complete</c> 的 Ctrl+Del 是
        /// <b>热键注册失败时的兜底</b>（走钩子不走 RegisterHotKey），兜底路径不该有第二套键位。
        /// 其余条目照常可改。</summary>
        private static readonly HashSet<string> LockedIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "record.cancel", "record.complete",
        };

        /// <summary>绑定文件路径（与 settings.ini / actions.ini 同目录）。</summary>
        public static string ShortcutsPath => Path.Combine(AppSettings.SettingsDirectory, "shortcuts.ini");

        /// <summary>一条内置快捷键绑定。</summary>
        public sealed class BuiltinShortcut
        {
            /// <summary>稳定标识，如 "capture.brush"。</summary>
            public string Id = "";

            /// <summary>显示名称（设置页列表）。</summary>
            public string Name = "";

            /// <summary>所属分组：<see cref="GroupCapture"/> / <see cref="GroupSticker"/> / <see cref="GroupRecord"/>。</summary>
            public string Group = "";

            /// <summary>当前绑定（修饰键 + 主键）。</summary>
            public KeyModifiers Modifiers;
            public Key Key;

            /// <summary>只改修饰键，按住／滚轮语义由原宿主保持，文件仍使用原三字段格式。</summary>
            public bool ModifierOnly => Id is "sticker.pickhold" or "sticker.finezoom";

            /// <summary>出厂默认绑定（"恢复默认"与覆盖项判定使用）。</summary>
            public KeyModifiers DefaultModifiers;
            public Key DefaultKey;

            /// <summary>当前绑定是否与默认不同（不同的才写入文件）。</summary>
            public bool IsOverridden => Modifiers != DefaultModifiers || Key != DefaultKey;

            /// <summary>锁死项（见 <see cref="LockedIds"/>）：设置页只展示不给改，文件里的覆盖项也一律忽略。</summary>
            public bool Locked { get; set; }

            public BuiltinShortcut Clone()
            {
                return new BuiltinShortcut
                {
                    Id = Id,
                    Name = Name,
                    Group = Group,
                    Modifiers = Modifiers,
                    Key = Key,
                    DefaultModifiers = DefaultModifiers,
                    DefaultKey = DefaultKey,
                    Locked = Locked,
                };
            }
        }

        // —— 内置快捷键目录（Id / 名称 / 分组 / 默认修饰键 / 默认主键） ——
        // 默认参考 Snipaste / PixPin / ShareX 习惯：标注工具统一 Shift+字母，防单字母误触。
        private static readonly (string Id, string Name, string Group, KeyModifiers Mods, Key Key)[] Catalog =
        {
            // 截图会话
            ("capture.arrow",       "箭头工具",        GroupCapture, KeyModifiers.Shift, Key.A),
            ("capture.brush",       "画笔工具",        GroupCapture, KeyModifiers.Shift, Key.B),
            ("capture.shape",       "形状工具",        GroupCapture, KeyModifiers.Shift, Key.R),
            ("capture.text",        "文字工具",        GroupCapture, KeyModifiers.Shift, Key.T),
            ("capture.mosaic",      "马赛克 / 模糊",   GroupCapture, KeyModifiers.Shift, Key.M),
            ("capture.spotlight",   "聚光灯",          GroupCapture, KeyModifiers.Shift, Key.S),
            ("capture.counter",     "序号工具",        GroupCapture, KeyModifiers.Shift, Key.C),
            ("capture.eraser",      "橡皮擦",          GroupCapture, KeyModifiers.Shift, Key.E),
            ("capture.colorpicker", "取色器",          GroupCapture, KeyModifiers.None, Key.C),
            ("capture.undo",        "撤销",            GroupCapture, KeyModifiers.Control, Key.Z),
            ("capture.redo",        "重做",            GroupCapture, KeyModifiers.Control, Key.Y),
            ("capture.translate",   "翻译选区",        GroupCapture, KeyModifiers.Control, Key.Q),
            ("capture.matting",     "智能抠图",        GroupCapture, KeyModifiers.Shift, Key.K),
            ("capture.saveas",      "另存为",          GroupCapture, KeyModifiers.Control, Key.S),
            ("capture.quicksave",   "快速保存",        GroupCapture, KeyModifiers.Control | KeyModifiers.Shift, Key.S),
            ("capture.delete",      "删除选中图元",    GroupCapture, KeyModifiers.None, Key.Delete),
            ("capture.confirm",     "完成截图",        GroupCapture, KeyModifiers.None, Key.Enter),
            ("capture.cancel",      "取消截图",        GroupCapture, KeyModifiers.None, Key.Escape),
            // 选区历史（2026-09-29）：裸 R 在历史栈上往后退一格。裸键与上面那排 Shift+字母 工具键不同桶
            // （Matches 要求修饰键精确相等），与 capture.shape 的 Shift+R 互不冲突。
            ("capture.restore",     "恢复上一次截图框", GroupCapture, KeyModifiers.None, Key.R),
            // 工具栏上"动作类"六枚（2026-09-29 补）：此前它们要么只有全局键（录屏 F9／OCR Ctrl+Alt+O），
            // 要么压根没有键（长截图／贴图／AI 识图／水印），tooltip 里的键名全是 XAML 写死的。
            // 一律 Shift+字母，与上面那排标注工具键同一族；字母避开已占的 A B R T M S C E K。
            // ⚠ 全局动作键（F9／Ctrl+Alt+O 等，走 actions.ini）不写在这里，也不出现在工具栏 tooltip 上——
            //   一条 tooltip 里混"全局作用域"与"窗口内作用域"两种键，正是这次被判"违和"的病根。
            ("capture.longscroll",  "长截图",          GroupCapture, KeyModifiers.Shift, Key.L),
            ("capture.ocr",         "文字识别",        GroupCapture, KeyModifiers.Shift, Key.O),
            ("capture.record",      "录屏",            GroupCapture, KeyModifiers.Shift, Key.F),
            ("capture.sticker",     "贴为贴图",        GroupCapture, KeyModifiers.Shift, Key.P),
            ("capture.aivision",    "AI 识图",         GroupCapture, KeyModifiers.Shift, Key.I),
            ("capture.watermark",   "水印开关",        GroupCapture, KeyModifiers.Shift, Key.W),

            // 贴图窗口
            ("sticker.lock",        "锁定 / 解锁",     GroupSticker, KeyModifiers.Control, Key.L),
            ("sticker.canvas",      "进出画布模式",    GroupSticker, KeyModifiers.Control, Key.X),
            ("sticker.penetrate",   "鼠标穿透",        GroupSticker, KeyModifiers.Control, Key.P),
            ("sticker.thumbnail",   "缩略图模式",      GroupSticker, KeyModifiers.Shift, Key.R),
            ("sticker.shadow",      "贴图阴影",        GroupSticker, KeyModifiers.Shift, Key.Y),
            ("sticker.pickhold",    "取色器唤出键",    GroupSticker, KeyModifiers.Shift, Key.None),
            ("sticker.finezoom",    "精细缩放修饰键",  GroupSticker, KeyModifiers.Alt, Key.None),
            ("sticker.copy",        "复制贴图",        GroupSticker, KeyModifiers.Control, Key.C),
            ("sticker.save",        "保存贴图",        GroupSticker, KeyModifiers.Control, Key.S),
            ("sticker.translate",   "贴图翻译",        GroupSticker, KeyModifiers.Control, Key.Q),
            // 标注编辑三键：与截图组 capture.undo / capture.redo / capture.delete 同一键位口径，
            // 不另立一套习惯（Delete 与 Backspace 的等价由 Matches 的别名负责，不重复建条目）
            ("sticker.undo",        "撤销标注",        GroupSticker, KeyModifiers.Control, Key.Z),
            ("sticker.redo",        "重做标注",        GroupSticker, KeyModifiers.Control, Key.Y),
            ("sticker.delete",      "删除选中标注",    GroupSticker, KeyModifiers.None, Key.Delete),
            ("sticker.close",       "关闭贴图",        GroupSticker, KeyModifiers.None, Key.Escape),
            // 贴图取色器的两枚动作键（2026-09-29）：取色器本身是「按住唤出键期间才存在」的手势，
            // 未自定义的两枚动作键跟随唤出修饰键；用户已单独改过的动作绑定保持。
            ("sticker.pickcopy",    "取色器·复制颜色",  GroupSticker, KeyModifiers.Shift, Key.C),
            ("sticker.pickformat",  "取色器·切换格式",  GroupSticker, KeyModifiers.Shift, Key.X),
            // 进出标注模式（2026-09-29 补登记）：一直是硬编码 Space，设置页里查不到。
            // 其余固定手势不建改键项：
            //   方向键步进（4 个键＝1 个动作）、+/- 改序号（2 个键＝1 个动作）、
            //   Ctrl+滚轮 不透明度、裸滚轮缩放、Shift+双击缩略图、Alt+拖选文字、Ctrl+拖拽导出——
            //   两枚修饰键专用项只改变修饰键，其余拖拽／双击等手势保持。
            //   它们在设置页底部那行说明与 docs/shortcuts.md 里交代。
            ("sticker.annotate",    "进出标注模式",    GroupSticker, KeyModifiers.None, Key.Space),

            // 录屏会话键（2026-09-29 建组）。默认值与旧实现逐字相同 ⇒ 没动过的用户行为零变化。
            // 生效域＝"录制会话期间"（倒计时＋录制中），随录制开始临时注册、结束即卸载，
            // 快速启停提前到选中录屏工具时注册，因此也检查截图组占用；其余控制键仍按组判重。
            (RecordToggleId,        "快速启停录屏",      GroupRecord, KeyModifiers.None, Key.None),
            ("record.pause",        "暂停 / 继续录制",  GroupRecord, KeyModifiers.None, Key.F10),
            ("record.stop",         "完成录制（进预览）", GroupRecord, KeyModifiers.None, Key.F11),
            ("record.restart",      "重新录制（本段作废）", GroupRecord, KeyModifiers.None, Key.F8),
            ("record.complete",     "完成录制（兜底键）", GroupRecord, KeyModifiers.Control, Key.Delete),
            ("record.cancel",       "取消录制 / 倒计时", GroupRecord, KeyModifiers.None, Key.Escape),
        };

        /// <summary>加载全部内置绑定（目录骨架 + 文件覆盖项）。文件不存在或损坏时返回纯默认。</summary>
        public static List<BuiltinShortcut> Load()
        {
            var result = new List<BuiltinShortcut>(Catalog.Length);
            foreach (var (id, name, group, mods, key) in Catalog)
            {
                result.Add(new BuiltinShortcut
                {
                    Id = id,
                    Name = name,
                    Group = group,
                    Modifiers = mods,
                    Key = key,
                    DefaultModifiers = mods,
                    DefaultKey = key,
                    Locked = LockedIds.Contains(id),
                });
            }

            if (!File.Exists(ShortcutsPath)) return result;

            try
            {
                var byId = new Dictionary<string, BuiltinShortcut>(result.Count);
                foreach (var sc in result) byId[sc.Id] = sc;

                foreach (string rawLine in File.ReadAllLines(ShortcutsPath, Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (!line.StartsWith("shortcut\t", StringComparison.Ordinal)) continue;

                    string[] fields = line.Substring("shortcut\t".Length).Split('\t');
                    if (fields.Length < 3) continue;
                    if (!byId.TryGetValue(fields[0], out BuiltinShortcut? sc)) continue;
                    if (sc.Locked) continue;                       // 锁死项连手工改 ini 也不认（见 LockedIds 的说明）
                    if (!int.TryParse(fields[1], out int mods)) continue;
                    if (!int.TryParse(fields[2], out int key)) continue;
                    if (sc.ModifierOnly && (key != (int)Key.None || !IsAllowedModifier(sc, (KeyModifiers)mods))) continue;

                    sc.Modifiers = (KeyModifiers)mods;
                    sc.Key = (Key)key;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("BuiltinShortcutsStore.Load 读取失败，使用默认绑定: " + ex.Message);
            }
            RefreshModifierDefaults(result);
            return result;
        }

        public static KeyModifiers ModifierOfKey(Key key) => key switch
        {
            Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
            Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
            Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
            _ => KeyModifiers.None,
        };

        public static bool IsAllowedModifier(BuiltinShortcut sc, KeyModifiers modifier)
            => modifier is KeyModifiers.Shift or KeyModifiers.Alt
                || sc.Id == "sticker.pickhold" && modifier == KeyModifiers.Control;

        public static string ConflictKey(BuiltinShortcut sc)
            => sc.Group + "|" + (sc.Id is "sticker.pickcopy" or "sticker.pickformat" ? "picker" : "key")
                + "|" + (int)sc.Modifiers + "|" + (int)(sc.Key == Key.Back ? Key.Delete : NormalizeKey(sc.Key));

        /// <summary>按实际匹配语义判重；不同窗口域、取色器专属动作与修饰键手势仍可复用。</summary>
        public static bool Conflicts(BuiltinShortcut first, BuiltinShortcut second)
        {
            if (first.ModifierOnly || second.ModifierOnly || first.Key == Key.None || second.Key == Key.None
                || (first.Group != second.Group
                    && !(first.Id == RecordToggleId && second.Group == GroupCapture)
                    && !(second.Id == RecordToggleId && first.Group == GroupCapture))) return false;
            bool Picker(BuiltinShortcut value) => value.Id is "sticker.pickcopy" or "sticker.pickformat";
            if (Picker(first) != Picker(second)) return false;
            return Matches(first, second.Key, second.Modifiers) || Matches(second, first.Key, first.Modifiers);
        }

        public static string? FindConflict(BuiltinShortcut candidate, IReadOnlyList<BuiltinShortcut> shortcuts,
            IReadOnlyList<ActionDefinition> actions)
        {
            foreach (var other in shortcuts)
                if (other.Id != candidate.Id && Conflicts(candidate, other)) return "「" + other.Name + "」";
            if (!candidate.ModifierOnly && candidate.Key != Key.None)
                foreach (var action in actions)
                    if (action.Enabled && action.HasHotKey && ConflictsWithAction(candidate, action.HotKeyModifiers, action.HotKeyVirtualKey))
                        return "全局动作「" + action.Name + "」";
            return null;
        }

        public static string? FindActionConflict(uint modifiers, uint key, IReadOnlyList<ActionDefinition> actions,
            ActionDefinition? original, IReadOnlyList<BuiltinShortcut> shortcuts)
        {
            if (key == 0) return null;
            foreach (var other in actions)
                if (!ReferenceEquals(other, original) && other.Enabled && other.HasHotKey
                    && ActionKeyboardBinding.Conflicts(modifiers, key, other.HotKeyModifiers, other.HotKeyVirtualKey))
                    return "全局动作「" + other.Name + "」";
            foreach (var shortcut in shortcuts)
                if (!shortcut.ModifierOnly && shortcut.Key != Key.None && ConflictsWithAction(shortcut, modifiers, key))
                    return (shortcut.Group == GroupCapture ? "截图" : shortcut.Group == GroupSticker ? "贴图" : "录屏")
                        + "快捷键「" + shortcut.Name + "」";
            return null;
        }

        private static bool ConflictsWithAction(BuiltinShortcut shortcut, uint modifiers, uint key)
        {
            if (ActionKeyboardBinding.IsExtended(modifiers)) return false;
            // 全局注册先消费消息，局部窗口匹配同一组合时将收不到输入；包含 Delete／Backspace 别名。
            uint canonical = ConflictVirtualKeyOf(shortcut.Key);
            if (canonical == 0 || canonical != (key == 8 ? 46u : key)) return false;
            var pressed = (KeyModifiers)(((modifiers & 1) != 0 ? (int)KeyModifiers.Alt : 0)
                | ((modifiers & 2) != 0 ? (int)KeyModifiers.Control : 0)
                | ((modifiers & 4) != 0 ? (int)KeyModifiers.Shift : 0)
                | ((modifiers & 8) != 0 ? (int)KeyModifiers.Meta : 0));
            return Matches(shortcut, shortcut.Key, pressed);
        }

        // 判重补齐可录制的 Windows 键，不扩大 VirtualKeyOf 原有系统注册目录。
        private static uint ConflictVirtualKeyOf(Key key)
        {
            key = key == Key.Back ? Key.Delete : NormalizeKey(key);
            uint known = VirtualKeyOf(key);
            if (known != 0) return known;
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return (uint)(0x60 + key - Key.NumPad0);
            if (key >= Key.Multiply && key <= Key.Divide) return (uint)(0x6A + key - Key.Multiply);
            if (key >= Key.BrowserBack && key <= Key.LaunchApplication2) return (uint)(0xA6 + key - Key.BrowserBack);
            return key switch
            {
                Key.Cancel => 3, Key.Clear => 12, Key.Pause => 19, Key.CapsLock => 20, Key.KanaMode => 21,
                Key.JunjaMode => 23, Key.FinalMode => 24, Key.KanjiMode => 25, Key.Escape => 27,
                Key.ImeConvert => 28, Key.ImeNonConvert => 29, Key.ImeAccept => 30, Key.ImeModeChange => 31,
                Key.Select => 41, Key.Print => 42, Key.Execute => 43, Key.Snapshot => 44, Key.Help => 47,
                Key.LWin => 91, Key.RWin => 92, Key.Apps => 93, Key.Sleep => 95,
                Key.NumLock => 144, Key.Scroll => 145, Key.AbntC1 => 193, Key.AbntC2 => 194,
                Key.Oem8 => 223, Key.OemBackslash => 226, Key.ImeProcessed => 229,
                Key.Attn => 246, Key.CrSel => 247, Key.ExSel => 248, Key.EraseEof => 249,
                Key.Play => 250, Key.Zoom => 251, Key.NoName => 252, Key.Pa1 => 253, Key.OemClear => 254,
                _ => 0,
            };
        }

        /// <summary>默认复制／格式键跟随唤出键，显式覆盖不被连带改写。</summary>
        public static void RefreshModifierDefaults(IReadOnlyList<BuiltinShortcut> bindings)
        {
            KeyModifiers summon = KeyModifiers.Shift;
            foreach (var sc in bindings) if (sc.Id == "sticker.pickhold") summon = sc.Modifiers;
            foreach (var sc in bindings)
            {
                if (sc.Id is not ("sticker.pickcopy" or "sticker.pickformat")) continue;
                bool wasDefault = !sc.IsOverridden;
                sc.DefaultModifiers = summon;
                if (wasDefault) sc.Modifiers = summon;
            }
        }

        public static void ResetToDefault(BuiltinShortcut sc)
        {
            foreach (var entry in Catalog)
                if (entry.Id == sc.Id)
                {
                    sc.Modifiers = sc.DefaultModifiers = entry.Mods;
                    sc.Key = sc.DefaultKey = entry.Key;
                    return;
                }
        }

        /// <summary>按分组加载为 Id → 绑定 的字典（会话/窗口创建时一次性取出使用）。</summary>
        public static Dictionary<string, BuiltinShortcut> LoadGroupMap(string group)
        {
            var map = new Dictionary<string, BuiltinShortcut>();
            foreach (var sc in Load())
            {
                if (sc.Group == group) map[sc.Id] = sc;
            }
            return map;
        }

        // —— Win32 热键换算（给"要临时注册全局热键"的消费方用，如 RecordFlow）——
        // 🔴 这份映射原先只活在 ActionEditorWindow 的私有方法里；录屏会话键要读目录注册热键，
        //    再抄一份就是两处坐标改一处必漏一处，故收到这里，两处共用（设计规范「同一逻辑只许一份实现」）。

        /// <summary>Win32 <c>RegisterHotKey</c> 的修饰键位。</summary>
        public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

        /// <summary>Avalonia 主键 → Win32 虚拟键码；表外的键返回 0（调用方按"这条没绑"处理）。
        /// 数字小键盘的 <c>Add</c>/<c>Subtract</c> 刻意不收录：<c>RegisterHotKey</c> 对它们要额外给
        /// <c>MAPVK_VK_TO_VSC</c> 才生效，收进来只会让用户绑上一个"注册成功却按不动"的键。</summary>
        public static uint VirtualKeyOf(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return (uint)(0x41 + (key - Key.A));
            if (key >= Key.D0 && key <= Key.D9) return (uint)(0x30 + (key - Key.D0));
            if (key >= Key.F1 && key <= Key.F24) return (uint)(0x70 + (key - Key.F1));

            return key switch
            {
                Key.Space => 0x20,
                Key.Tab => 0x09,
                Key.Enter => 0x0D,
                Key.Back => 0x08,
                Key.Delete => 0x2E,
                Key.Insert => 0x2D,
                Key.Home => 0x24,
                Key.End => 0x23,
                Key.PageUp => 0x21,
                Key.PageDown => 0x22,
                Key.Left => 0x25,
                Key.Up => 0x26,
                Key.Right => 0x27,
                Key.Down => 0x28,
                Key.OemTilde => 0xC0,
                Key.OemMinus => 0xBD,
                Key.OemPlus => 0xBB,
                Key.OemOpenBrackets => 0xDB,
                Key.OemCloseBrackets => 0xDD,
                Key.OemPipe => 0xDC,
                Key.OemSemicolon => 0xBA,
                Key.OemQuotes => 0xDE,
                Key.OemComma => 0xBC,
                Key.OemPeriod => 0xBE,
                Key.OemQuestion => 0xBF,
                _ => 0,
            };
        }

        /// <summary>Avalonia 修饰键 → Win32 修饰键位。</summary>
        public static uint WinModifiersOf(KeyModifiers m)
        {
            uint mods = 0;
            if (m.HasFlag(KeyModifiers.Alt)) mods |= ModAlt;
            if (m.HasFlag(KeyModifiers.Control)) mods |= ModControl;
            if (m.HasFlag(KeyModifiers.Shift)) mods |= ModShift;
            if (m.HasFlag(KeyModifiers.Meta)) mods |= ModWin;
            return mods;
        }

        /// <summary>保存绑定（仅写入与默认不同的覆盖项；UTF-8 无 BOM，原子替换写入）。</summary>
        public static void Save(IReadOnlyList<BuiltinShortcut> bindings)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.SettingsDirectory);
                var sb = new StringBuilder();
                sb.AppendLine("# yuSnip shortcuts v1 — 内置快捷键绑定（截图/贴图窗口内生效，录屏那组在录制会话期内生效）。仅保存与默认不同的项，请勿手工编辑（锁死项写了也不认）。");
                foreach (var sc in bindings)
                {
                    if (sc.ModifierOnly && (sc.Key != Key.None || !IsAllowedModifier(sc, sc.Modifiers))) continue;
                    if (!sc.IsOverridden) continue;
                    sb.Append("shortcut\t")
                        .Append(sc.Id).Append('\t')
                        .Append((int)sc.Modifiers).Append('\t')
                        .Append((int)sc.Key).Append('\n');
                }

                string tmp = ShortcutsPath + ".tmp";
                lock (ScreenCaptureTool.Core.CloudSync.CloudSnapshot.FileGate)
                {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                ScreenCaptureTool.Core.CloudSync.CloudConfigTransaction.ReplaceFile(tmp, ShortcutsPath);
                }
                System.Threading.Interlocked.Increment(ref _revision);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("BuiltinShortcutsStore.Save 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 按键事件是否命中某条绑定。修饰键要求精确一致（避免劫持更强的组合键）；
        /// Return≡Enter、Backspace≡Delete 视为等价。
        /// </summary>
        public static bool Matches(BuiltinShortcut sc, KeyEventArgs e) => Matches(sc, e.Key, e.KeyModifiers);

        /// <summary>
        /// 匹配内核（不吃 <see cref="KeyEventArgs"/>，供调用方先按自己的窗口的口径预处理修饰键再进来）。
        /// 算法只有这一份，勿在别处再抄一遍。
        /// </summary>
        public static bool Matches(BuiltinShortcut sc, Key key, KeyModifiers pressedMods)
        {
            Key pressed = NormalizeKey(key);
            Key bound = NormalizeKey(sc.Key);
            if (pressed != bound)
            {
                bool pressedIsDelete = pressed == Key.Delete || pressed == Key.Back;
                bool boundIsDelete = bound == Key.Delete || bound == Key.Back;
                if (!(pressedIsDelete && boundIsDelete)) return false;
            }
            // Alt 已被"按住 Alt 显示辅助线"占用：绑定本身不含 Alt 时忽略按住的 Alt，
            // 否则按住 Alt 期间 Shift+X 这类工具键全部失效。
            // 绑定本身含 Alt 时仍精确比对，不把普通单键劫持成 Alt 组合。
            if (!sc.Modifiers.HasFlag(KeyModifiers.Alt)) pressedMods &= ~KeyModifiers.Alt;
            return pressedMods == sc.Modifiers;
        }

        private static Key NormalizeKey(Key key)
        {
            return key == Key.Return ? Key.Enter : key;
        }

        /// <summary>格式化为展示文本，如 "Ctrl+Shift+A" / "Del" / "Enter"。</summary>
        public static string Format(KeyModifiers mods, Key key)
        {
            var sb = new StringBuilder();
            if (mods.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
            if (mods.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
            if (mods.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
            if (mods.HasFlag(KeyModifiers.Meta)) sb.Append("Win+");
            sb.Append(FriendlyKeyName(key));
            return sb.ToString();
        }

        /// <summary>格式化为展示文本（按绑定）。</summary>
        public static string Format(BuiltinShortcut sc)
        {
            if (sc.Id == RecordToggleId && sc.Key == Key.None) return "未设置";
            if (sc.ModifierOnly) return sc.Modifiers switch
            {
                KeyModifiers.Control => "Ctrl", KeyModifiers.Alt => "Alt", KeyModifiers.Shift => "Shift", _ => "未绑定",
            };
            return Format(sc.Modifiers, sc.Key);
        }

        private static string FriendlyKeyName(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return key.ToString();
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (key - Key.NumPad0);
            if (key >= Key.F1 && key <= Key.F12) return key.ToString();
            return key switch
            {
                Key.Escape => "Esc",
                Key.Enter or Key.Return => "Enter",
                Key.Delete => "Del",
                Key.Back => "Backspace",
                Key.Space => "Space",
                Key.Tab => "Tab",
                Key.OemTilde => "`",
                Key.OemMinus => "-",
                Key.OemPlus => "=",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets => "]",
                Key.OemSemicolon => ";",
                Key.OemQuotes => "'",
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                Key.OemQuestion => "/",
                Key.OemBackslash or Key.OemPipe => "\\",
                _ => key.ToString(),
            };
        }
    }
}
