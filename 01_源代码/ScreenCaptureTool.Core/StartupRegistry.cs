#nullable disable

using System;
using Microsoft.Win32;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 实现"开机启动"开关。
    /// 阶段 2 搬迁自旧项目，按 D009 改为 <c>public</c>。
    /// </summary>
    public static class StartupRegistry
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string ValueName
        {
            get { return "yuSnip_" + AppBuildInfo.SoftwareName; }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null && !string.IsNullOrEmpty(v.ToString());
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StartupRegistry.IsEnabled failed: " + ex.Message);
                return false;
            }
        }

        public static bool Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        string exe = GetExecutablePath();
                        if (string.IsNullOrEmpty(exe)) return false;
                        string command = "\"" + exe + "\"";
                        key.SetValue(ValueName, command, RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                        {
                            key.DeleteValue(ValueName, false);
                        }
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
