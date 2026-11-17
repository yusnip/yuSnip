using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 管理员模式的开机自启计划任务（schtasks 命令行封装）。
    /// 注册表 Run 键无法以提权方式启动程序，因此「管理员模式 + 开机启动」组合改走计划任务：
    /// <c>/SC ONLOGON /RL HIGHEST</c>，一次注册后每次登录免 UAC 直接以管理员权限启动。
    /// 所有操作按 schtasks 退出码判定成败，不解析输出文本（避免本地化语言差异）。
    /// 注意：<c>/RL HIGHEST</c> 任务的创建/删除需要管理员权限，写入类方法只应在已提权实例中调用；
    /// 查询（<see cref="IsRegistered"/>）普通权限即可。
    /// </summary>
    public static class ScheduledTaskService
    {
        /// <summary>计划任务名（注册在当前用户的任务文件夹下）。</summary>
        public const string TaskName = "yuSnipAdminAutostart";

        /// <summary>查询提权自启计划任务是否已注册。</summary>
        public static bool IsRegistered()
        {
            return RunSchtasks("/Query /TN \"" + TaskName + "\"", out _);
        }

        /// <summary>
        /// 创建/覆盖提权自启计划任务：当前用户登录时以最高权限运行本程序（带 -autostart-elevated 启动标记）。
        /// 需管理员权限；幂等（/F 覆盖旧定义，程序路径变化时自动修正）。
        /// </summary>
        public static bool EnsureRegistered()
        {
            string? exe = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                AppLogger.Warn("ScheduledTaskService.EnsureRegistered: 无法确定当前程序路径");
                return false;
            }

            // XML 明确交互登录、电池策略与无限运行时长，避免 schtasks 默认值使常驻程序不启动或被停止。
            string file = Path.Combine(Path.GetTempPath(), "yusnip-startup-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                string user = identity.User?.Value ?? identity.Name;
                File.WriteAllText(file, BuildTaskXml(exe, user), new UnicodeEncoding(false, true));
                if (RunSchtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + file + "\"", out string? error))
                {
                    AppLogger.Info("ScheduledTaskService: 已注册管理员自启计划任务 " + TaskName);
                    return true;
                }
                AppLogger.Warn("ScheduledTaskService: 注册管理员自启计划任务失败：" + error);
                return false;
            }
            catch (Exception ex) { AppLogger.Error("注册管理员自启计划任务异常", ex); return false; }
            finally { try { File.Delete(file); } catch { } }
        }

        internal static string BuildTaskXml(string exe, string user)
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            XElement E(string name, object value) => new(ns + name, value);
            return new XDocument(new XDeclaration("1.0", "utf-16", null),
                new XElement(ns + "Task", new XAttribute("version", "1.2"),
                    E("Triggers", new XElement(ns + "LogonTrigger", E("Enabled", true), E("UserId", user))),
                    E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        E("UserId", user), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
                    E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"),
                        E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
                        E("StartWhenAvailable", true), E("Enabled", true), E("ExecutionTimeLimit", "PT0S") }),
                    new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                        E("Exec", new object[] { E("Command", exe), E("Arguments", ElevationHelper.AutostartElevatedMarker),
                            E("WorkingDirectory", Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory) })))).ToString();
        }

        /// <summary>删除提权自启计划任务（需管理员权限；任务本就不存在视为成功）。</summary>
        public static bool Remove()
        {
            if (!RunSchtasks("/Delete /F /TN \"" + TaskName + "\"", out string? error))
            {
                // 删除失败先确认任务是否真的存在：不存在说明已被清掉，不当作错误
                if (!IsRegistered())
                {
                    AppLogger.Info("ScheduledTaskService: 计划任务不存在，无需删除");
                    return true;
                }
                AppLogger.Warn("ScheduledTaskService: 删除管理员自启计划任务失败" +
                    (string.IsNullOrEmpty(error) ? "" : "：" + error));
                return false;
            }
            AppLogger.Info("ScheduledTaskService: 已删除管理员自启计划任务 " + TaskName);
            return true;
        }

        /// <summary>执行 schtasks 命令，返回是否成功（退出码 0）；失败时通过 error 带回输出摘要。</summary>
        internal static bool RunSchtasks(string arguments, out string? error)
        {
            error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using Process? process = Process.Start(psi);
                if (process == null)
                {
                    error = "schtasks 进程未创建";
                    return false;
                }
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    error = "schtasks 执行超时";
                    return false;
                }
                string output = (stdout.GetAwaiter().GetResult() + " " + stderr.GetAwaiter().GetResult()).Trim();
                if (process.ExitCode != 0)
                {
                    error = "exit=" + process.ExitCode + " " + output;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLogger.Error("ScheduledTaskService.RunSchtasks 异常: " + arguments, ex);
                return false;
            }
        }

        /// <summary>解析当前程序 exe 完整路径（NativeAOT 单文件下 Environment.ProcessPath 优先）。</summary>
        private static string? ResolveExecutablePath()
        {
            try
            {
                string? p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch { return null; }
        }
    }
}
