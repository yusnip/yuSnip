#nullable disable

using System;
using System.IO;
using Microsoft.Win32;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 实现"开机启动"开关。
    /// 支持自动清理历史版本遗留的不同键名（如 yuSnip_新截图工具、yuSnip_yuSnip 等）及失效路径。
    /// </summary>
    public static class StartupRegistry
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>当前版本的标准注册表项名称。</summary>
        public const string CanonicalValueName = "yuSnip";

        /// <summary>历史版本曾经使用过的键名列表。</summary>
        private static readonly string[] KnownValueNames = new[]
        {
            "yuSnip",
            "yuSnip_yuSnip",
            "yuSnip_新截图工具",
            "ScreenCaptureTool",
            "ScreenCaptureStandalone"
        };

        public static string ValueName => CanonicalValueName;

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;

                    // 1. 检查已知键名
                    foreach (string name in KnownValueNames)
                    {
                        object v = key.GetValue(name);
                        if (v != null && !string.IsNullOrEmpty(v.ToString()))
                            return true;
                    }

                    // 2. 遍历检查是否有指向当前程序或 yuSnip.exe 的残留自启项
                    string exePath = GetExecutablePath();
                    foreach (string valueName in key.GetValueNames())
                    {
                        if (valueName.StartsWith("yuSnip", StringComparison.OrdinalIgnoreCase))
                        {
                            object v = key.GetValue(valueName);
                            if (v != null && !string.IsNullOrEmpty(v.ToString()))
                                return true;
                        }

                        object val = key.GetValue(valueName);
                        if (val != null)
                        {
                            string cmd = val.ToString() ?? "";
                            if (cmd.Contains("yuSnip.exe", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(exePath) && cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase)))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StartupRegistry.IsEnabled failed: " + ex.Message);
                return false;
            }
        }

        public static bool Apply(bool enabled)
            => ApplyToKey(enabled, RunKey);

        // 自测使用独立临时键，绝不修改真实 Windows 启动项。
        internal static bool ApplyToKey(bool enabled, string keyPath)
        {
            try
            {
                using (RegistryKey key = enabled ? Registry.CurrentUser.CreateSubKey(keyPath, true)
                                                : Registry.CurrentUser.OpenSubKey(keyPath, true))
                {
                    if (key == null) return !enabled;

                    // 无论开启还是关闭，先清理所有旧格式/历史遗留键名及旧路径注册项
                    CleanupAllLegacyAndOrphanedEntries(key);

                    if (enabled)
                    {
                        string exe = GetExecutablePath();
                        if (string.IsNullOrEmpty(exe)) return false;
                        string command = "\"" + exe + "\"";
                        key.SetValue(CanonicalValueName, command, RegistryValueKind.String);
                        AppLogger.Info("StartupRegistry.Apply: 已注册开机自启，项名=" + CanonicalValueName + ", 路径=" + command);
                    }
                    else
                    {
                        AppLogger.Info("StartupRegistry.Apply: 已清除所有开机自启注册表项");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartupRegistry.Apply failed.", ex);
                return false;
            }
        }

        /// <summary>清理所有历史遗留名称及指向 yuSnip.exe 的旧注册表项。</summary>
        public static void CleanupAllLegacyAndOrphanedEntries(RegistryKey key)
        {
            if (key == null) return;
            string exePath = GetExecutablePath();

            // 1. 删除所有已知的遗留键名
            foreach (string name in KnownValueNames)
            {
                try
                {
                    if (key.GetValue(name) != null)
                    {
                        key.DeleteValue(name, false);
                        AppLogger.Info("StartupRegistry: 已清理自启键 " + name);
                    }
                }
                catch { }
            }

            // 2. 遍历删除任何前缀为 yuSnip 或指向当前 exe / yuSnip.exe 的孤儿键
            try
            {
                string[] names = key.GetValueNames();
                foreach (string name in names)
                {
                    bool shouldDelete = false;
                    if (name.StartsWith("yuSnip", StringComparison.OrdinalIgnoreCase))
                    {
                        shouldDelete = true;
                    }
                    else
                    {
                        object val = key.GetValue(name);
                        if (val != null)
                        {
                            string cmd = val.ToString() ?? "";
                            if (cmd.Contains("yuSnip.exe", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(exePath) && cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase)))
                            {
                                shouldDelete = true;
                            }
                        }
                    }

                    if (shouldDelete)
                    {
                        key.DeleteValue(name, false);
                        AppLogger.Info("StartupRegistry: 已清理孤儿自启键 " + name);
                    }
                }
            }
            catch { }
        }

        private static string GetExecutablePath()
        {
            try
            {
                string p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }

            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            }
            catch { return null; }
        }
    }
}
