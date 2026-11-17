using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Native;

namespace ScreenCaptureTool;

class Program
{
    // 单实例锁。使用 Global\ 前缀，确保跨会话/终端服务也只允许一个实例。
    // "new Mutex(true, ...)" 创建时即请求初始所有权；createdNew=false 表示已被其它实例持有。
    private const string SingleInstanceMutexName = "Global\\yuSnip_SingleInstance";
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// 显式静态构造：让 <see cref="StartupProfiler"/> 的时间原点尽可能早地被捕获。
    /// 显式 cctor 会移除 beforefieldinit 语义，CLR 保证它在 Main 之前执行，于是 Main 里
    /// 第一个里程碑打点就能把「托管启动 + 内嵌 native 释放/加载」这段原本的盲区量出来。
    /// 这里不碰 Avalonia 与任何第三方 API（遵守 Main 上方注释的启动约束）。
    /// </summary>
    static Program()
    {
        _ = StartupProfiler.ElapsedMs;
    }

    /// <summary>接力启动等待旧实例退出释放锁的间隔与次数（250ms × 40 ≈ 10s）。</summary>
    private const int RelayLockRetryIntervalMs = 250;
    private const int RelayLockRetryCount = 40;

    public static string? PendingScriptToRun;

    /// <summary>待执行的命名动作（-a/--action 参数，主实例启动后执行）。</summary>
    public static string? PendingActionToRun;

    /// <summary>本次启动是否为"用管理员模式重启"的接力子进程（命令行带 ElevationHelper.RelayMarker）。</summary>
    public static bool IsRelayLaunch;

    /// <summary>本次启动是否为计划任务开机提权拉起（命令行带 ElevationHelper.AutostartElevatedMarker）。</summary>
    public static bool IsAutostartLaunch;

    /// <summary>本次启动是否为退出管理员模式的降权接力子进程（命令行带 ElevationHelper.DeadminRelayMarker）。</summary>
    public static bool IsDeadminRelayLaunch;

    /// <summary>IPC 管道消息中"命名动作"的前缀（与裸脚本区分）。</summary>
    public const string ActionMessagePrefix = "action:";

    /// <summary>
    /// 尝试取得单实例锁：成功返回已持有所有权的 Mutex，已有实例在运行返回 null。
    /// 跨完整性级别时（例如已有实例是高权限），打开 Global\ 命名对象可能直接抛
    /// UnauthorizedAccessException，这里一并按"已有实例在运行"处理，不让进程在锁检查这一步崩掉。
    /// </summary>
    private static Mutex? TryAcquireInstanceMutex()
    {
        try
        {
            bool createdNew;
            Mutex mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out createdNew);
            if (createdNew) return mutex;
            try { mutex.Dispose(); } catch { }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            AppLogger.Warn("Program.TryAcquireInstanceMutex: 单实例对象由更高权限的实例持有，按已有实例处理");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Program.TryAcquireInstanceMutex 异常", ex);
            return null;
        }
    }

    private static string? TryGetScriptFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "-r", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
            if (string.Equals(args[i], "-f", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string path = args[i + 1];
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        return System.IO.File.ReadAllText(path);
                    }
                    else
                    {
                        AppLogger.Warn("IPC: 脚本文件不存在: " + path);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("IPC: 读取脚本文件失败: " + path, ex);
                }
            }
        }
        return null;
    }

    /// <summary>解析 -a / --action &lt;动作名&gt; 参数（命名动作调用，对应 actions.ini 中的动作）。</summary>
    private static string? TryGetActionFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((string.Equals(args[i], "-a", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[i], "--action", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void SendScriptToMainInstance(string script)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Services.CommandIpc.SendAsync(script).GetAwaiter().GetResult();
            AppLogger.Info($"IPC: 转发完成，通信={watch.ElapsedMilliseconds}ms，入口累计={StartupProfiler.ElapsedMs:F0}ms");
        }
        catch (Exception ex)
        {
            AppLogger.Error("IPC: 发送脚本给主实例失败", ex);
        }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        RegisterGlobalExceptionLogging();

        // 启动标记解析：提权接力（-admin-relay）/ 计划任务开机提权（-autostart-elevated）/
        // 降权接力（-deadmin-relay）/ 同权限重启（-restart-relay）。
        // 先记录再剥离，避免这些标记被当成脚本/动作参数或继续传进界面层。
        IsRelayLaunch = ElevationHelper.IsRelayLaunch(args);
        IsAutostartLaunch = ElevationHelper.IsAutostartLaunch(args);
        IsDeadminRelayLaunch = ElevationHelper.IsDeadminRelayLaunch(args);
        bool isRestartRelay = IsRelayLaunch || IsAutostartLaunch || IsDeadminRelayLaunch
            || ElevationHelper.IsRestartRelayLaunch(args);
        args = ElevationHelper.StripLaunchMarkers(args);

        string? cmdScript = TryGetScriptFromArgs(args);
        string? cmdAction = TryGetActionFromArgs(args);

        // 单实例检查：若已有实例在运行，则直接退出，不重复启动。
        _singleInstanceMutex = TryAcquireInstanceMutex();
        if (_singleInstanceMutex == null && isRestartRelay)
        {
            // 接力/自启启动：父实例此刻正在退出（或 Run 键与计划任务在登录瞬间短暂竞争），单实例锁与热键都还没释放。
            // 这里必须等有界时间让它退出，不能当成"重复启动"转发后退出，否则提权/降权重启会静默失败。
            for (int i = 0; i < RelayLockRetryCount && _singleInstanceMutex == null; i++)
            {
                Thread.Sleep(RelayLockRetryIntervalMs);
                _singleInstanceMutex = TryAcquireInstanceMutex();
            }
            if (_singleInstanceMutex != null)
            {
                AppLogger.Info("Program.Main: 接力启动已等到旧实例退出并接管单实例锁");
            }
            else
            {
                AppLogger.Warn("Program.Main: 接力启动等待旧实例退出超时，本次重启未完成");
            }
        }
        if (_singleInstanceMutex == null)
        {
            if (!string.IsNullOrEmpty(cmdAction))
            {
                AppLogger.Info("Program.Main: 检测到另一个实例运行，且存在命名动作参数。转发给主实例: " + cmdAction);
                SendScriptToMainInstance(ActionMessagePrefix + cmdAction);
            }
            else if (!string.IsNullOrEmpty(cmdScript))
            {
                AppLogger.Info("Program.Main: 检测到另一个实例运行，且存在命令行脚本。发送脚本给主实例。");
                SendScriptToMainInstance(cmdScript);
            }
            else if (!isRestartRelay && args.Length == 0)
            {
                // 无参数再次打开 exe：复用后台实例的截图入口，保留会话防重入与录屏占用判断。
                AppLogger.Info("Program.Main: 无参数再次启动，通知后台实例开始截图");
                SendScriptToMainInstance("yuSnip.screenShot();");
            }
            else
            {
                AppLogger.Warn("Program.Main: 检测到另一个实例正在运行，本次启动退出");
            }
            return 2; // 2 = 已有实例在运行
        }

        return RunMainInstance(args, cmdScript, cmdAction, isRestartRelay);
    }

    // 转发进程只使用 BCL；单独的方法阻止 JIT 在转发前解析 Avalonia 等主程序依赖。
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunMainInstance(string[] args, string? cmdScript, string? cmdAction, bool isRestartRelay)
    {
        // 只有取得单实例锁的主进程加载原生库；仍必须早于任何 Avalonia/OpenCV/OCR 调用，CRT 先于 ONNX。
        var nativeSw = System.Diagnostics.Stopwatch.StartNew();
        EmbeddedNativeLoader.Initialize();
        nativeSw.Stop();
        StartupProfiler.Mark($"NativeReady(native {nativeSw.ElapsedMilliseconds}ms)");
        ScreenCaptureTool.Platform.Accessibility.AccessibilityHelper.LogHandler = msg => AppLogger.Warn("Accessibility: " + msg);
        ScreenCaptureTool.Platform.Accessibility.UIAutomationHelper.LogHandler = msg => AppLogger.Warn("UIAutomation: " + msg);
        AppLogger.Startup("Program.Main");

        // 仅持有单实例锁的主进程恢复未完成事务；转发命令的第二进程不能碰配置。
        try { Core.CloudSync.CloudConfigTransaction.Recover(AppSettings.SettingsDirectory); }
        catch (Exception ex)
        {
            AppLogger.Error("自动云同步事务恢复失败，停止启动以保留恢复材料", ex);
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose(); _singleInstanceMutex = null;
            return 1;
        }

        if (!string.IsNullOrEmpty(cmdAction))
        {
            PendingActionToRun = cmdAction;
        }
        if (!string.IsNullOrEmpty(cmdScript))
        {
            PendingScriptToRun = cmdScript;
        }

        // 管理员模式已开启但本次为普通权限手动启动：自动接力提权重启，保证"开启期间始终以管理员运行"。
        // 仅限干净的手动启动（无接力/自启标记、无命令行脚本/动作，避免丢失命令行意图）；
        // UAC 被拒绝或启动失败时继续以普通权限运行，由设置页提示文案说明。
        if (!isRestartRelay && string.IsNullOrEmpty(cmdScript) && string.IsNullOrEmpty(cmdAction) &&
            !ElevationHelper.IsElevated() && AppSettings.Current.RunAsAdmin)
        {
            AppLogger.Info("Program.Main: 管理员模式已开启而本次为普通权限启动，尝试提权接力重启");
            if (ElevationHelper.TryRestartElevated(out _) == RestartElevatedResult.Started)
            {
                // 高权限接力实例已创建（其会重试等待本实例释放单实例锁），本进程立即退出让位
                AppLogger.Shutdown("Program.Main relay to elevated");
                return 0;
            }
            AppLogger.Warn("Program.Main: 管理员模式自动提权未完成，本次继续以普通权限运行");
        }

        // 提升 Windows 系统时钟解析度至 1ms，确保高精度计时与拖拽响应具有 1ms 物理时钟精度
        try { ScreenCaptureTool.Platform.NativeMethods.timeBeginPeriod(1); } catch { }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLogger.Shutdown("Program.Main normal exit");
            return 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Program.Main 顶层异常", ex);
            return 1;
        }
        finally
        {
            try { ScreenCaptureTool.Platform.NativeMethods.timeEndPeriod(1); } catch { }
            // 无论正常退出还是异常退出，都释放单实例锁，允许下一次启动。
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
            _singleInstanceMutex = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[]
                {
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface
                }
            })
            // Skia GPU 资源缓存上限（2026-09-29）：默认约 28MB，而一张 4K 截图纹理就有 33MB（截图遮罩的冻结底图、
            // 大图贴图退回 Avalonia 呈现时都是），超限即被逐出、下一帧重新上传。256MB 足够装下几张 4K 纹理，
            // 仍是上限不是预占（Avalonia #20658 / lindexi 同一建议）。
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256L * 1024 * 1024 })
            .WithInterFont()
            .LogToTrace();
    }

    private static void RegisterGlobalExceptionLogging()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    AppLogger.Error("AppDomain.UnhandledException，IsTerminating=" + e.IsTerminating, ex);
                }
                else
                {
                    AppLogger.Error("AppDomain.UnhandledException，IsTerminating=" + e.IsTerminating + "，ExceptionObject=" + (e.ExceptionObject?.ToString() ?? "null"));
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.Error("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error("注册全局异常日志失败", ex);
        }
    }
}
