using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NativeMethods = ScreenCaptureTool.Platform.NativeMethods;

namespace ScreenCaptureTool.Core
{
    /// <summary>以管理员权限重启本程序的结果。</summary>
    public enum RestartElevatedResult
    {
        /// <summary>用户已在 UAC 中确认，新的高权限实例已创建。</summary>
        Started,
        /// <summary>用户在 UAC 弹窗中点了"否"（Win32 错误 1223）。</summary>
        Declined,
        /// <summary>提权启动失败，原因见 error。</summary>
        Failed
    }

    /// <summary>
    /// 进程权限级别（完整性级别）辅助：判断自身是否已提权，以及管理员模式的提权/降权接力重启。
    /// 配合设置项 <c>RunAsAdmin</c> 与计划任务（<see cref="ScheduledTaskService"/>）实现
    /// "开启管理员模式后，重启、手动启动、开机自启都以管理员权限运行"。
    /// </summary>
    public static class ElevationHelper
    {
        /// <summary>
        /// 提权接力启动标记。父实例（普通权限）退出与子实例（高权限）取单实例锁之间存在竞态，
        /// 子进程凭该标记知道"上一个实例正在退出"，改为有界等待锁释放，而不是当成重复启动转发后退出。
        /// </summary>
        public const string RelayMarker = "-admin-relay";

        /// <summary>计划任务开机提权启动标记：登录时由计划任务（/RL HIGHEST）拉起的实例带此参数。</summary>
        public const string AutostartElevatedMarker = "-autostart-elevated";

        /// <summary>降权接力启动标记：退出管理员模式时由 runas /trustlevel 拉起的普通权限子进程带此参数。</summary>
        public const string DeadminRelayMarker = "-deadmin-relay";

        /// <summary>
        /// 同权限重启标记：托盘「重新启动程序」拉起的子进程带此参数。
        /// 不改权限，但同样面临"父实例还没退出、单实例锁未释放"的竞态，
        /// 故必须与上面三个标记同等进入 <see cref="StripLaunchMarkers"/> 与主入口的有界等待判定。
        /// </summary>
        public const string RestartMarker = "-restart-relay";

        /// <summary>UAC 弹窗被用户取消。</summary>
        private const int ErrorCancelled = 1223;

        private static bool? _isElevated;

        /// <summary>当前进程是否以管理员（已提权）权限运行。结果只查一次并缓存。</summary>
        public static bool IsElevated()
        {
            if (_isElevated.HasValue) return _isElevated.Value;

            bool elevated = false;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_QUERY, out token))
                {
                    NativeMethods.TOKEN_ELEVATION info = new NativeMethods.TOKEN_ELEVATION();
                    uint size = (uint)Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                    if (NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, ref info, size, out _))
                    {
                        elevated = info.TokenIsElevated != 0;
                    }
                    else
                    {
                        AppLogger.Warn("ElevationHelper: GetTokenInformation 失败，" +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
                else
                {
                    AppLogger.Warn("ElevationHelper: OpenProcessToken 失败，" +
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ElevationHelper.IsElevated 异常: " + ex.Message);
            }
            finally
            {
                if (token != IntPtr.Zero)
                {
                    try { NativeMethods.CloseHandle(token); } catch { }
                }
            }

            _isElevated = elevated;
            AppLogger.Info("ElevationHelper: 当前权限级别 = " + (elevated ? "管理员（已提权）" : "普通"));
            return elevated;
        }

        /// <summary>命令行参数里是否带指定启动标记。</summary>
        private static bool HasLaunchMarker(string[]? args, string marker)
        {
            if (args == null || args.Length == 0) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, marker, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>命令行参数里是否带提权接力启动标记。</summary>
        public static bool IsRelayLaunch(string[]? args)
        {
            return HasLaunchMarker(args, RelayMarker);
        }

        /// <summary>命令行参数里是否带计划任务开机提权启动标记。</summary>
        public static bool IsAutostartLaunch(string[]? args)
        {
            return HasLaunchMarker(args, AutostartElevatedMarker);
        }

        /// <summary>命令行参数里是否带降权接力启动标记。</summary>
        public static bool IsDeadminRelayLaunch(string[]? args)
        {
            return HasLaunchMarker(args, DeadminRelayMarker);
        }

        /// <summary>命令行参数里是否带同权限重启标记。</summary>
        public static bool IsRestartRelayLaunch(string[]? args)
        {
            return HasLaunchMarker(args, RestartMarker);
        }

        /// <summary>剥掉所有启动标记（提权接力 / 计划任务自启 / 降权接力 / 同权限重启），得到要交给界面层/脚本解析的干净参数。</summary>
        public static string[] StripLaunchMarkers(string[]? args)
        {
            if (args == null || args.Length == 0) return Array.Empty<string>();
            bool hasAny = IsRelayLaunch(args) || IsAutostartLaunch(args) || IsDeadminRelayLaunch(args) || IsRestartRelayLaunch(args);
            if (!hasAny) return args;

            List<string> kept = new List<string>(args.Length);
            foreach (string arg in args)
            {
                if (string.Equals(arg, RelayMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(arg, AutostartElevatedMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(arg, DeadminRelayMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(arg, RestartMarker, StringComparison.OrdinalIgnoreCase)) continue;
                kept.Add(arg);
            }
            return kept.ToArray();
        }

        /// <summary>解析当前程序 exe 完整路径（NativeAOT 单文件下 Environment.ProcessPath 优先）。</summary>
        private static string? ResolveExecutablePath()
        {
            string? exe = null;
            try { exe = Environment.ProcessPath; } catch { }
            if (string.IsNullOrEmpty(exe))
            {
                try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
            }
            return string.IsNullOrEmpty(exe) ? null : exe;
        }

        /// <summary>
        /// 以管理员权限重启本程序。注意：Verb="runas" 的 ShellExecute 会**阻塞调用线程**直到用户答复 UAC，
        /// 因此必须在后台线程调用，不能放在 UI 线程上。
        /// </summary>
        public static RestartElevatedResult TryRestartElevated(out string? error)
        {
            error = null;

            string? exe = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                error = "无法确定当前程序路径";
                AppLogger.Warn("ElevationHelper.TryRestartElevated: " + error);
                return RestartElevatedResult.Failed;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                    Arguments = RelayMarker,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using (Process? child = Process.Start(psi))
                {
                    AppLogger.Info("ElevationHelper: 已以管理员权限重启 yuSnip" +
                        (child != null ? "，子进程 pid=" + child.Id : ""));
                }
                return RestartElevatedResult.Started;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                AppLogger.Info("ElevationHelper: 用户在 UAC 中取消，本次继续以普通权限运行");
                return RestartElevatedResult.Declined;
            }
            catch (Exception ex)
            {
                AppLogger.Error("ElevationHelper.TryRestartElevated 失败", ex);
                error = ex.Message;
                return RestartElevatedResult.Failed;
            }
        }

        /// <summary>
        /// 以普通权限重启本程序（从管理员进程降权）。Windows 没有"降权启动"的直接 API，
        /// 这里采用双引擎策略：
        /// 1. 优先通过获取 Explorer Shell 桌面进程令牌并复制为 Primary Token（CreateProcessWithTokenW），
        ///    无需依赖 Secondary Logon 服务，且完全受支持、NativeAOT 友好；
        /// 2. 当 Explorer 未运行或令牌复制失败时，回退至 runas.exe /trustlevel:0x20000 兜底。
        /// 子进程带 <see cref="DeadminRelayMarker"/> 标记，取单实例锁时会等待本实例退出。
        /// 与提权不同：本方法不弹 UAC、不长时间阻塞，可在 UI 线程直接调用。
        /// </summary>
        public static bool TryRestartDeElevated()
        {
            string? exe = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                AppLogger.Warn("ElevationHelper.TryRestartDeElevated: 无法确定当前程序路径");
                return false;
            }

            // 引擎 1：计划任务以普通用户权限（/RL LIMITED）拉起降权实例。
            // 这是 Windows 下从管理员降回标准用户完整性级别（Medium Mandatory Level）
            // 最稳定可靠、无需 SeImpersonatePrivilege 特权且完全 NativeAOT 友好的机制。
            if (TryRestartDeElevatedViaTaskScheduler(exe))
            {
                AppLogger.Info("ElevationHelper: 已通过计划任务(/RL LIMITED)成功拉起降权子进程");
                return true;
            }

            AppLogger.Info("ElevationHelper: 计划任务降权未成功，回退至 Explorer 令牌复制引擎");

            // 引擎 2：Explorer Shell 令牌复制（在具备 SeImpersonatePrivilege 特权的环境中直接生效）
            if (TryRestartDeElevatedViaShellToken(exe))
            {
                AppLogger.Info("ElevationHelper: 已通过 Explorer 令牌降权拉起子进程");
                return true;
            }

            AppLogger.Info("ElevationHelper: Explorer 令牌降权未成功，回退至 runas /trustlevel 引擎");

            // 引擎 3：runas /trustlevel:0x20000 兜底
            if (TryRestartDeElevatedViaRunAs(exe))
            {
                AppLogger.Info("ElevationHelper: 已通过 runas /trustlevel:0x20000 降权拉起子进程");
                return true;
            }

            AppLogger.Error("ElevationHelper: 降权重启三引擎均失败");
            return false;
        }

        /// <summary>
        /// 降权引擎 1：通过计划任务以普通用户权限（/RL LIMITED）拉起降权实例。
        /// </summary>
        private static bool TryRestartDeElevatedViaTaskScheduler(string exe)
        {
            const string tempTaskName = "yuSnipDeadminRelay";
            try
            {
                string taskRun = "\\\"" + exe + "\\\" " + DeadminRelayMarker;
                string createArgs = "/Create /F /TN \"" + tempTaskName + "\" /TR \"" + taskRun + "\" /SC ONCE /ST 00:00 /RL LIMITED";

                if (!ScheduledTaskService.RunSchtasks(createArgs, out string? createErr))
                {
                    AppLogger.Warn("TryRestartDeElevatedViaTaskScheduler: 创建临时降权任务失败: " + createErr);
                    return false;
                }

                bool ran = ScheduledTaskService.RunSchtasks("/Run /TN \"" + tempTaskName + "\"", out string? runErr);

                // 启动触发后立即清理临时任务（任务已进队列，不影响子进程拉起）
                _ = ScheduledTaskService.RunSchtasks("/Delete /F /TN \"" + tempTaskName + "\"", out _);

                if (ran)
                {
                    return true;
                }
                else
                {
                    AppLogger.Warn("TryRestartDeElevatedViaTaskScheduler: 运行临时降权任务失败: " + runErr);
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TryRestartDeElevatedViaTaskScheduler 异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 降权引擎 2：通过 Windows 桌面 Shell（explorer.exe）的访问令牌复制并创建基本用户进程。
        /// </summary>
        private static bool TryRestartDeElevatedViaShellToken(string exe)
        {
            IntPtr hShellWnd = NativeMethods.GetShellWindow();
            if (hShellWnd == IntPtr.Zero)
            {
                AppLogger.Warn("TryRestartDeElevatedViaShellToken: 未找到 ShellWindow (Explorer 可能未运行)");
                return false;
            }

            uint shellPid = 0;
            NativeMethods.GetWindowThreadProcessId(hShellWnd, out shellPid);
            if (shellPid == 0)
            {
                AppLogger.Warn("TryRestartDeElevatedViaShellToken: 无法获取 Shell 进程 PID");
                return false;
            }

            IntPtr hShellProcess = IntPtr.Zero;
            IntPtr hShellToken = IntPtr.Zero;
            IntPtr hPrimaryToken = IntPtr.Zero;

            try
            {
                hShellProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, shellPid);
                if (hShellProcess == IntPtr.Zero)
                {
                    hShellProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
                }
                if (hShellProcess == IntPtr.Zero)
                {
                    AppLogger.Warn("TryRestartDeElevatedViaShellToken: OpenProcess 失败, err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                if (!NativeMethods.OpenProcessToken(hShellProcess, NativeMethods.TOKEN_DUPLICATE, out hShellToken))
                {
                    AppLogger.Warn("TryRestartDeElevatedViaShellToken: OpenProcessToken 失败, err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                if (!NativeMethods.DuplicateTokenEx(
                    hShellToken,
                    0,
                    IntPtr.Zero,
                    NativeMethods.SecurityImpersonation,
                    NativeMethods.TokenPrimary,
                    out hPrimaryToken))
                {
                    AppLogger.Warn("TryRestartDeElevatedViaShellToken: DuplicateTokenEx 失败, err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                NativeMethods.STARTUPINFO si = new NativeMethods.STARTUPINFO();
                si.cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>();

                string cmdLine = "\"" + exe + "\" " + DeadminRelayMarker;
                string workDir = Path.GetDirectoryName(exe) ?? string.Empty;

                if (!NativeMethods.CreateProcessWithTokenW(
                    hPrimaryToken,
                    0,
                    null,
                    cmdLine,
                    0,
                    IntPtr.Zero,
                    workDir,
                    ref si,
                    out NativeMethods.PROCESS_INFORMATION pi))
                {
                    AppLogger.Warn("TryRestartDeElevatedViaShellToken: CreateProcessWithTokenW 失败, err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                try
                {
                    if (pi.hThread != IntPtr.Zero) NativeMethods.CloseHandle(pi.hThread);
                    if (pi.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(pi.hProcess);
                }
                catch { }

                AppLogger.Info("ElevationHelper: 已成功通过 Explorer 令牌复制降权拉起子进程 pid=" + pi.dwProcessId);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TryRestartDeElevatedViaShellToken 异常: " + ex.Message);
                return false;
            }
            finally
            {
                if (hPrimaryToken != IntPtr.Zero) try { NativeMethods.CloseHandle(hPrimaryToken); } catch { }
                if (hShellToken != IntPtr.Zero) try { NativeMethods.CloseHandle(hShellToken); } catch { }
                if (hShellProcess != IntPtr.Zero) try { NativeMethods.CloseHandle(hShellProcess); } catch { }
            }
        }

        /// <summary>
        /// 降权引擎 2：通过 runas.exe /trustlevel:0x20000 降权拉起基本用户进程（需 Secondary Logon 服务）。
        /// </summary>
        private static bool TryRestartDeElevatedViaRunAs(string exe)
        {
            try
            {
                string command = "\"" + exe + "\" " + DeadminRelayMarker;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "runas.exe",
                    Arguments = "/trustlevel:0x20000 \"" + command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process? runner = Process.Start(psi))
                {
                    if (runner == null)
                    {
                        AppLogger.Warn("TryRestartDeElevatedViaRunAs: runas 进程未创建");
                        return false;
                    }

                    if (runner.WaitForExit(3000))
                    {
                        if (runner.ExitCode != 0)
                        {
                            AppLogger.Warn("TryRestartDeElevatedViaRunAs: runas 退出码异常: " + runner.ExitCode);
                            return false;
                        }
                    }
                    else
                    {
                        AppLogger.Warn("TryRestartDeElevatedViaRunAs: runas 进程等待超时");
                    }
                }
                AppLogger.Info("ElevationHelper: 已通过 runas /trustlevel:0x20000 降权拉起子进程");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TryRestartDeElevatedViaRunAs 失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 以**当前权限**重启本程序（托盘「重新启动程序」）。刻意不设 Verb、不走 runas：
        /// 子进程直接继承本进程令牌，管理员态点了重启还是管理员态，普通态亦然。
        /// 子进程带 <see cref="RestartMarker"/>，进主入口后会等有界时间等本实例释放单实例锁。
        /// </summary>
        public static bool TryRestartSelf()
        {
            string? exe = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                AppLogger.Warn("ElevationHelper.TryRestartSelf: 无法确定当前程序路径");
                return false;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                    Arguments = RestartMarker,
                    UseShellExecute = true
                };
                using (Process? child = Process.Start(psi))
                {
                    if (child == null)
                    {
                        AppLogger.Warn("ElevationHelper.TryRestartSelf: 子进程未创建");
                        return false;
                    }
                    AppLogger.Info("ElevationHelper: 已发起同权限重启，子进程 pid=" + child.Id);
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("ElevationHelper.TryRestartSelf 失败", ex);
                return false;
            }
        }
    }
}
