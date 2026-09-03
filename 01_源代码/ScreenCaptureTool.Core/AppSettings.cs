#nullable disable

using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 应用设置。从 <c>%APPDATA%/yuSnip/settings.ini</c> 读写。
    /// 阶段 2 搬迁自旧项目，按 D009 改为 <c>public</c>，命名空间改为 <c>ScreenCaptureTool.Core</c>。
    /// </summary>
    public sealed class AppSettings
    {
        // 保存
        public string SaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        public string FileNameTemplate = "Screenshot_{yyyyMMdd_HHmmss}";
        /// <summary>已废弃：截图统一保存为 PNG（以保留元数据）。保留字段仅为旧配置文件兼容，值恒为 "PNG"。</summary>
        public string ImageFormat = "PNG";
        /// <summary>已废弃：随 JPG 格式移除而失效，保留字段仅为旧配置文件兼容。</summary>
        public int JpegQuality = 92;       // 1..100

        // 截图行为
        public int DelaySeconds = 0;
        public string AutoDetectMode = "仅窗口"; // 不检测 | 仅窗口 | 检测元素
        public bool CopyToClipboardAfterSave = true;
        public bool ShowSaveNotification = true;
        public bool PlaySaveSound = false;

        // 外观
        public bool DarkMode = true;

        // 启动
        public bool RunOnStartup = false;
        public bool ShowStartupNotification = true;

        // 热键
        public uint HotKeyModifiers = 0x0002 | 0x0001; // Ctrl + Alt
        public uint HotKeyVirtualKey = 0x41;           // A
        /// <summary>快速贴图热键的修饰键（默认 0 = 无修饰，配合 F3 单键触发）。</summary>
        public uint StickerHotKeyModifiers = 0;
        /// <summary>快速贴图热键的主键（默认 VK_F3 = 0x72）。</summary>
        public uint StickerHotKeyVirtualKey = 0x72;
        /// <summary>是否显示贴图阴影切换（Y 键）的 Toast 提示。默认开。</summary>
        public bool ShowStickerShadowToast = true;

        public static string SettingsDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuSnip"); }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(SettingsDirectory, "settings.ini"); }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    s.Save();
                    return s;
                }

                string[] lines = File.ReadAllLines(SettingsPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int p = line.IndexOf('=');
                    if (p <= 0) continue;
                    string key = line.Substring(0, p).Trim();
                    string value = line.Substring(p + 1).Trim();
                    s.Apply(key, value);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load settings.", ex);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory)) Directory.CreateDirectory(SettingsDirectory);
                List<string> lines = new List<string>();
                lines.Add("# yuSnip settings");
                lines.Add("SaveDirectory=" + (SaveDirectory ?? ""));
                lines.Add("FileNameTemplate=" + (FileNameTemplate ?? ""));
                lines.Add("ImageFormat=PNG"); // 强制 PNG，忽略废弃字段
                lines.Add("JpegQuality=" + JpegQuality.ToString());
                lines.Add("DelaySeconds=" + DelaySeconds.ToString());
                lines.Add("AutoDetectMode=" + NormalizeAutoDetectMode(AutoDetectMode));
                lines.Add("CopyToClipboardAfterSave=" + CopyToClipboardAfterSave.ToString());
                lines.Add("ShowSaveNotification=" + ShowSaveNotification.ToString());
                lines.Add("PlaySaveSound=" + PlaySaveSound.ToString());
                lines.Add("DarkMode=" + DarkMode.ToString());
                lines.Add("RunOnStartup=" + RunOnStartup.ToString());
                lines.Add("ShowStartupNotification=" + ShowStartupNotification.ToString());
                lines.Add("HotKeyModifiers=" + HotKeyModifiers.ToString());
                lines.Add("HotKeyVirtualKey=" + HotKeyVirtualKey.ToString());
                lines.Add("StickerHotKeyModifiers=" + StickerHotKeyModifiers.ToString());
                lines.Add("StickerHotKeyVirtualKey=" + StickerHotKeyVirtualKey.ToString());
                lines.Add("ShowStickerShadowToast=" + ShowStickerShadowToast.ToString());
                File.WriteAllLines(SettingsPath, lines.ToArray());
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save settings.", ex);
            }
        }

        public void CopyFrom(AppSettings other)
        {
            if (other == null) return;
            SaveDirectory = other.SaveDirectory;
            FileNameTemplate = other.FileNameTemplate;
            ImageFormat = "PNG"; // 强制 PNG，忽略源对象可能的废弃值
            JpegQuality = other.JpegQuality;
            DelaySeconds = other.DelaySeconds;
            AutoDetectMode = NormalizeAutoDetectMode(other.AutoDetectMode);
            CopyToClipboardAfterSave = other.CopyToClipboardAfterSave;
            ShowSaveNotification = other.ShowSaveNotification;
            PlaySaveSound = other.PlaySaveSound;
            DarkMode = other.DarkMode;
            RunOnStartup = other.RunOnStartup;
            ShowStartupNotification = other.ShowStartupNotification;
            HotKeyModifiers = other.HotKeyModifiers;
            HotKeyVirtualKey = other.HotKeyVirtualKey;
            StickerHotKeyModifiers = other.StickerHotKeyModifiers;
            StickerHotKeyVirtualKey = other.StickerHotKeyVirtualKey;
            ShowStickerShadowToast = other.ShowStickerShadowToast;
        }

        public AppSettings Clone()
        {
            AppSettings s = new AppSettings();
            s.CopyFrom(this);
            return s;
        }

        private void Apply(string key, string value)
        {
            if (string.Equals(key, "SaveDirectory", StringComparison.OrdinalIgnoreCase)) SaveDirectory = value;
            else if (string.Equals(key, "FileNameTemplate", StringComparison.OrdinalIgnoreCase)) FileNameTemplate = value;
            else if (string.Equals(key, "ImageFormat", StringComparison.OrdinalIgnoreCase)) ImageFormat = "PNG"; // 强制 PNG，兼容旧配置文件里的 JPG/BMP
            else if (string.Equals(key, "JpegQuality", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(value, out v)) JpegQuality = Math.Min(100, Math.Max(1, v));
            }
            else if (string.Equals(key, "DelaySeconds", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(value, out v)) DelaySeconds = Math.Max(0, v);
            }
            else if (string.Equals(key, "AutoDetectMode", StringComparison.OrdinalIgnoreCase)) AutoDetectMode = NormalizeAutoDetectMode(value);
            else if (string.Equals(key, "CopyToClipboardAfterSave", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) CopyToClipboardAfterSave = v;
            }
            else if (string.Equals(key, "ShowSaveNotification", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowSaveNotification = v;
            }
            else if (string.Equals(key, "PlaySaveSound", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) PlaySaveSound = v;
            }
            else if (string.Equals(key, "DarkMode", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) DarkMode = v;
            }
            else if (string.Equals(key, "RunOnStartup", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) RunOnStartup = v;
            }
            else if (string.Equals(key, "ShowStartupNotification", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowStartupNotification = v;
            }
            else if (string.Equals(key, "HotKeyModifiers", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) HotKeyModifiers = v;
            }
            else if (string.Equals(key, "HotKeyVirtualKey", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) HotKeyVirtualKey = v;
            }
            else if (string.Equals(key, "StickerHotKeyModifiers", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) StickerHotKeyModifiers = v;
            }
            else if (string.Equals(key, "StickerHotKeyVirtualKey", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) StickerHotKeyVirtualKey = v;
            }
            else if (string.Equals(key, "ShowStickerShadowToast", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowStickerShadowToast = v;
            }
        }

        public static string NormalizeAutoDetectMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "仅窗口";
            string v = value.Trim();
            if (string.Equals(v, "None", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "不检测", StringComparison.OrdinalIgnoreCase)) return "不检测";
            if (string.Equals(v, "Window", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "仅窗口", StringComparison.OrdinalIgnoreCase)) return "仅窗口";
            if (string.Equals(v, "Element", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "检测元素", StringComparison.OrdinalIgnoreCase) || v.StartsWith("检测元", StringComparison.OrdinalIgnoreCase))
            {
                if (v.StartsWith("检测元", StringComparison.OrdinalIgnoreCase) && !string.Equals(v, "检测元素", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"AppSettings: Recovered corrupted auto-detect mode '{v}' to '检测元素'");
                }
                return "检测元素";
            }
            if (string.Equals(v, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "自动", StringComparison.OrdinalIgnoreCase)) return "仅窗口";
            
            AppLogger.Warn($"AppSettings: Unknown auto-detect mode '{v}', falling back to '仅窗口'");
            return "仅窗口";
        }

        public CaptureOptions ToCaptureOptions()
        {
            return new CaptureOptions
            {
                SaveDirectory = SaveDirectory,
                FileNameTemplate = FileNameTemplate,
                ImageFormat = ImageFormat,
                JpegQuality = JpegQuality,
                DelaySeconds = DelaySeconds,
                AutoDetectMode = NormalizeAutoDetectMode(AutoDetectMode),
                CopyToClipboardAfterSave = CopyToClipboardAfterSave,
                ShowSaveNotification = ShowSaveNotification,
                PlaySaveSound = PlaySaveSound,
                DarkMode = DarkMode,
                HotKeyModifiers = HotKeyModifiers,
                HotKeyVirtualKey = HotKeyVirtualKey
            };
        }

        public Dictionary<string, object> ToCaptureVars()
        {
            return ToCaptureOptions().ToLegacyVars();
        }
    }
}
