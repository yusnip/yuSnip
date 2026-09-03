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

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // 最先释放并挂载内嵌 native dll，必须在任何 Avalonia/OpenCvSharp 的 P/Invoke 之前。
        EmbeddedNativeLoader.Initialize();

        ScreenCaptureTool.Platform.Accessibility.AccessibilityHelper.LogHandler = msg => AppLogger.Warn("Accessibility: " + msg);
        ScreenCaptureTool.Platform.Accessibility.UIAutomationHelper.LogHandler = msg => AppLogger.Warn("UIAutomation: " + msg);

        RegisterGlobalExceptionLogging();
        AppLogger.Startup("Program.Main");

        // 单实例检查：若已有实例在运行，则直接退出，不重复启动。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            AppLogger.Warn("Program.Main: 检测到另一个实例正在运行，本次启动退出");
            return 2; // 2 = 已有实例在运行
        }

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
            // 无论正常退出还是异常退出，都释放单实例锁，允许下一次启动。
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex.Dispose(); } catch { }
            _singleInstanceMutex = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

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
