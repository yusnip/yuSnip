#nullable disable

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 简易追加式应用日志。线程安全（通过单一 SyncRoot 串行化写入）。
    ///
    /// 日志目录优先定位到当前新项目根目录下的 log：
    ///   10_Avalonia新项目/log/app.log
    ///
    /// 注意：这里不硬编码绝对路径，也不硬编码中文目录完整名称。
    /// 查找方式是从 exe 目录向上找同时包含 01_* / 02_* / 03_* 的目录。
    /// 如果找不到（例如用户只单独拷走 portable 目录），则回退到 exe 目录下的 log。
    /// </summary>
    public static class AppLogger
    {
        private static readonly object SyncRoot = new object();
        private static readonly Lazy<string> LazyLogDirectory = new Lazy<string>(ResolveLogDirectory);

        public static string LogDirectory
        {
            get { return LazyLogDirectory.Value; }
        }

        public static string LogPath
        {
            get { return Path.Combine(LogDirectory, "app.log"); }
        }

        public static void Debug(string message)
        {
            Write("DEBUG", message, null);
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Error(string message)
        {
            Write("ERROR", message, null);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        public static void Startup(string source)
        {
            Info("========== ScreenCaptureTool startup: " + (source ?? "unknown") + " ==========");
            Info("ProcessId=" + SafeGetProcessId());
            Info("BaseDirectory=" + AppContext.BaseDirectory);
            Info("CurrentDirectory=" + SafeGetCurrentDirectory());
            Info("LogPath=" + LogPath);
            Info("OS=" + RuntimeInformation.OSDescription);
            Info("Framework=" + RuntimeInformation.FrameworkDescription);
            Info("ProcessArchitecture=" + RuntimeInformation.ProcessArchitecture);
            Info("CommandLine=" + Environment.CommandLine);
        }

        public static void Shutdown(string source)
        {
            Info("========== ScreenCaptureTool shutdown: " + (source ?? "unknown") + " ==========");
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                lock (SyncRoot)
                {
                    if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                    using (StreamWriter writer = new StreamWriter(LogPath, true))
                    {
                        writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        writer.Write(" [");
                        writer.Write(level ?? "INFO");
                        writer.Write("] pid=");
                        writer.Write(SafeGetProcessId());
                        writer.Write(" tid=");
                        writer.Write(Thread.CurrentThread.ManagedThreadId);
                        writer.Write(" ");
                        writer.WriteLine(message ?? "");
                        if (exception != null)
                        {
                            writer.WriteLine(exception.ToString());
                        }
                    }
                }
            }
            catch
            {
                // 日志失败必须吞掉，不能影响主流程。
            }
        }

        private static string ResolveLogDirectory()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (ContainsPrefixedDirectory(dir, "01_")
                        && ContainsPrefixedDirectory(dir, "02_")
                        && ContainsPrefixedDirectory(dir, "03_"))
                    {
                        DirectoryInfo[] dirs = dir.GetDirectories("04_*");
                        if (dirs != null && dirs.Length > 0)
                        {
                            return dirs[0].FullName;
                        }
                        return Path.Combine(dir.FullName, "log");
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
                // ignore and fallback
            }

            return Path.Combine(AppContext.BaseDirectory, "log");
        }

        private static bool ContainsPrefixedDirectory(DirectoryInfo parent, string prefix)
        {
            try
            {
                DirectoryInfo[] dirs = parent.GetDirectories(prefix + "*");
                return dirs != null && dirs.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static int SafeGetProcessId()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        private static string SafeGetCurrentDirectory()
        {
            try { return Environment.CurrentDirectory; }
            catch { return string.Empty; }
        }
    }
}
