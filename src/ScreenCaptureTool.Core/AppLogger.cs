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
    /// 双通道记录：
    /// 1. 内存环形缓冲（所有构建都开启）——供设置「日志」页查看，容量 2000 条；
    /// 2. 文件落盘（仅 Debug 构建，即 开发运行.bat）——写到源码根目录 log/app.log；
    ///    正式版（Release/NativeAOT 混淆发布）不写文件，避免在用户机器上产生日志目录。
    ///
    /// 日志目录优先定位到当前新项目根目录下的 log：
    ///   10_Avalonia新项目/log/app.log
    ///
    /// 注意：这里不硬编码绝对路径，也不硬编码中文目录完整名称。
    /// 查找方式是从 exe 目录向上找包含公开解决方案与版本号文件的目录。
    /// 如果找不到（例如用户只单独拷走 portable 目录），则回退到 exe 目录下的 log。
    /// </summary>
    public static class AppLogger
    {
        private const int MemoryCapacity = 2000;

        private static readonly object SyncRoot = new object();
        private static readonly Lazy<string> LazyLogDirectory = new Lazy<string>(ResolveLogDirectory);
        private static readonly List<LogEntry> _memoryBuffer = new List<LogEntry>(256);

        // 进程号只取一次：旧实现每条日志都 Process.GetCurrentProcess()，每行新建一个带句柄的
        // Process 对象。实测其时间成本可忽略（~0.002ms/行），省下的是每行一次分配与句柄开关，
        // 不是毫秒级耗时——别把它当启动瓶颈看待。
        private static readonly int CachedProcessId = SafeGetProcessIdOnce();

        // 常驻文件写入器（受 SyncRoot 保护）。旧实现每条日志都 new StreamWriter(path, append)
        // 再 Dispose，等于每行一次 CreateFile + 写 + CloseHandle。
        // 实测（本机 PowerShell 复刻同一写入路径，1000 行/轮 × 3 轮，单进程无竞争）：
        //   旧 0.294 / 0.246 / 0.273 → 中位 0.273ms/行；新 0.007 / 0.006 / 0.006 → 中位 0.006ms/行。
        //   即每行省约 0.267ms（约 46 倍）。
        // ⚠ 这 0.273ms 是**下界**：测时本机 WinDefend 服务处于 Stopped（Get-MpPreference 报
        //   0x800106ba），故其中不含任何实时扫描成分；开了实时防护的机器每次打开 .log 还要多付一次扫描。
        //   早先笔记里写的「实测约 2ms/行」高出实测约 7 倍，且把成本归因给 Defender，两者均无依据，已作废。
        // 量级定位（据 log/app.log 中 pid=33908 那次启动）：启动窗口只写 38 行（UI 线程 34 行），
        //   故对启动的收益仅约 9ms，**不是启动瓶颈**；整会话 1107 行累计省约 296ms 主线程时间。
        // 崩溃安全性与旧实现等价：AutoFlush 保证每条写完即进入 OS 文件缓存，进程崩溃不丢已写行。
        private static StreamWriter _fileWriter;
        private static string _cachedLogPath;
        private static bool _fileWriterDisabled;

        /// <summary>细节诊断默认关闭；排查时在启动前设置 YUSNIP_VERBOSE_LOG=1，重启进程生效。</summary>
        public static bool DetailedLoggingEnabled { get; } =
            string.Equals(Environment.GetEnvironmentVariable("YUSNIP_VERBOSE_LOG"), "1", StringComparison.Ordinal);

        /// <summary>一条内存日志记录（供设置「日志」页展示）。</summary>
        public sealed class LogEntry
        {
            public DateTime Time { get; }
            public string Level { get; }
            public string Message { get; }

            public LogEntry(DateTime time, string level, string message)
            {
                Time = time;
                Level = level;
                Message = LogPrivacy.Redact(message);
            }
        }

        /// <summary>是否写日志文件：仅 Debug 构建启用；正式版（Release/NativeAOT）只在内存记录。</summary>
        public static bool FileLoggingEnabled
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        public static string LogDirectory
        {
            get { return LazyLogDirectory.Value; }
        }

        public static string LogPath
        {
            get { return Path.Combine(LogDirectory, "app.log"); }
        }

        /// <summary>获取内存缓冲的线程安全快照（按时间顺序）。</summary>
        public static LogEntry[] GetSnapshot()
        {
            lock (SyncRoot)
            {
                return _memoryBuffer.ToArray();
            }
        }

        public static void Debug(string message)
        {
            if (!DetailedLoggingEnabled) return;
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
            Info("Runtime=" + RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.ProcessArchitecture
                + "; OS=" + RuntimeInformation.OSDescription);
            if (!DetailedLoggingEnabled) return;
            Info("ProcessId=" + SafeGetProcessId());
            Info("BaseDirectory=" + AppContext.BaseDirectory);
            Info("CurrentDirectory=" + SafeGetCurrentDirectory());
            Info("LogPath=" + (FileLoggingEnabled ? LogPath : "(memory only)"));
            Info("OS=" + RuntimeInformation.OSDescription);
            Info("Framework=" + RuntimeInformation.FrameworkDescription);
            Info("ProcessArchitecture=" + RuntimeInformation.ProcessArchitecture);
            Info("CommandLineArgs=" + Environment.GetCommandLineArgs().Length);
        }

        public static void Shutdown(string source)
        {
            Info("========== ScreenCaptureTool shutdown: " + (source ?? "unknown") + " ==========");
            // 退出前释放常驻句柄：AutoFlush 已保证不丢行，这里只是不把文件句柄带进进程退出。
            try
            {
                lock (SyncRoot)
                {
                    CloseFileWriterNoLock();
                }
            }
            catch { }
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                string lv = level ?? "INFO";
                string msg = message ?? "";
                if (exception != null)
                {
                    if (!string.IsNullOrEmpty(exception.Message)) msg = msg.Replace(exception.Message, "[异常消息已脱敏]", StringComparison.Ordinal);
                    msg = msg + Environment.NewLine + LogPrivacy.DescribeException(exception);
                }
                DateTime now = DateTime.Now;

                lock (SyncRoot)
                {
                    var entry = new LogEntry(now, lv, msg);
                    msg = entry.Message;
                    _memoryBuffer.Add(entry);
                    if (_memoryBuffer.Count > MemoryCapacity)
                    {
                        _memoryBuffer.RemoveRange(0, _memoryBuffer.Count - MemoryCapacity);
                    }

                    if (!FileLoggingEnabled) return;

                    StreamWriter writer = EnsureFileWriter();
                    if (writer == null) return;

                    try
                    {
                        // AutoFlush 下每次 Write 都会刷新；整条一次写入，保留原格式及逐条落盘语义。
                        writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{lv}] pid={CachedProcessId} tid={Thread.CurrentThread.ManagedThreadId} {msg}");
                    }
                    catch
                    {
                        // 句柄失效（磁盘满 / 文件被外部占用等）：关掉常驻写入器并**永久停用**文件通道。
                        // 刻意不重试：全仓 796 个写日志调用点，若每条都重试打开，持久性故障下
                        // 就变成每行付一次异常——正是本项要消除的开销，重试反而把它放大。
                        // 内存环形缓冲不受影响，设置「日志」页仍然可读。
                        // 注：双实例并发写同一 .log 不成立——Program.cs 用 Global\yuSnip_SingleInstance
                        // 强制单实例，拿不到锁的实例直接退出（含"被更高权限实例持有"那条分支）。
                        CloseFileWriterNoLock();
                        _fileWriterDisabled = true;
                    }
                }
            }
            catch
            {
                // 日志失败必须吞掉，不能影响主流程。
            }
        }

        /// <summary>
        /// 取得常驻文件写入器（调用方必须已持有 <see cref="SyncRoot"/>）。
        /// 刻意不指定 Encoding：与旧实现 <c>new StreamWriter(path, true)</c> 保持同一默认
        /// （UTF-8 无 BOM），避免新建日志文件时多出一个 BOM 改变既有字节格式。
        /// </summary>
        private static StreamWriter EnsureFileWriter()
        {
            if (_fileWriter != null) return _fileWriter;
            if (_fileWriterDisabled) return null;

            try
            {
                string path = _cachedLogPath;
                if (path == null)
                {
                    // LogPath 每次访问都要 Path.Combine，缓存下来
                    path = LogPath;
                    _cachedLogPath = path;
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                _fileWriter = new StreamWriter(path, true) { AutoFlush = true };
                return _fileWriter;
            }
            catch
            {
                _fileWriterDisabled = true;
                return null;
            }
        }

        /// <summary>关闭常驻写入器（调用方必须已持有 <see cref="SyncRoot"/>）。不停用文件通道，下条日志会重开。</summary>
        private static void CloseFileWriterNoLock()
        {
            if (_fileWriter == null) return;
            try { _fileWriter.Dispose(); } catch { }
            _fileWriter = null;
        }

        private static string ResolveLogDirectory()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "src", "ScreenCaptureTool.sln"))
                        && File.Exists(Path.Combine(dir.FullName, "版本号.txt")))
                    {
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

        private static int SafeGetProcessIdOnce()
        {
            // Environment.ProcessId 是 .NET 5+ 的零分配属性，不需要新建 Process 对象。
            try { return Environment.ProcessId; }
            catch
            {
                try { return Process.GetCurrentProcess().Id; }
                catch { return -1; }
            }
        }

        private static int SafeGetProcessId()
        {
            return CachedProcessId;
        }

        private static string SafeGetCurrentDirectory()
        {
            try { return Environment.CurrentDirectory; }
            catch { return string.Empty; }
        }
    }
}
