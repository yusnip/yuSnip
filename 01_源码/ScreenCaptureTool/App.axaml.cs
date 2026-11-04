using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Services;
using ScreenCaptureTool.Windows;
using NativeMethods = ScreenCaptureTool.Platform.NativeMethods;

using System.Linq;
using System.IO;
using ScreenCaptureTool.Core.Scripting;

namespace ScreenCaptureTool;

public partial class App : Application
{
    // —— 动作热键体系（统一注册/分发，见 ActionsStore） ——
    private readonly System.Collections.Generic.List<ScreenCaptureTool.Core.HotKeyManager> _actionHotkeys = new();
    private ScreenCaptureTool.Platform.MessageOnlyWindow? _sharedHotkeyWindow;
    private System.Collections.Generic.List<ActionDefinition> _actions = new();
    private int _nextActionHotKeyId = 0x5350; // 动作热键 ID 起点（0x5343/0x5344 为历史遗留）
    private SettingsWindow? _settingsWindow;
    internal CloudAutoSyncService? AutoCloudSync { get; private set; }
    internal string AutoCloudSyncError { get; private set; } = "";
    private int _runningScripts;
    private CommandIpc? _commandIpc;
    private volatile bool _captureRunning;
    private GlobalMouseActionService? _mouseActions;
    /// <summary>托盘菜单「截图」「贴图」两项的快捷键展示手势（由已注册动作热键换算而来，null=未绑定/无法映射）。</summary>
    private KeyGesture? _captureHotKeyGesture = new KeyGesture(Key.A, KeyModifiers.Control | KeyModifiers.Alt);
    private KeyGesture? _stickerHotKeyGesture = new KeyGesture(Key.F3);
    /// <summary>当前正在运行的截图会话（RunCaptureAsync 期间非空）。供 F3 注入用。</summary>
    private volatile CaptureSession? _activeSession;
    private volatile string[] _captureShortcutActionIds = [];
    /// <summary>启动时准备、第一次截图独占的一次性窗口；不存放任何旧截图内容。</summary>
    private CaptureWindow? _preparedCaptureWindow;
    private Task<Avalonia.Media.Imaging.WriteableBitmap?>? _capturePipelineWarmup;
    private System.Threading.CancellationTokenSource? _capturePipelineWarmupCancellation;

    /// <summary>应用是否处于退出/重启流程中（防止退出阶段单个贴图关闭清空活跃会话）。</summary>
    public static bool IsShuttingDown { get; set; } = false;

    public override void Initialize()
    {
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.Initialize: 开始加载 Avalonia XAML");
        // XAML 加载实测 116ms（App.axaml + Colors/Controls 字典 + Fluent 主题），是启动第二大块。
        // 单独打点，后续裁剪合并字典时能直接看到收益。
        using (PerformanceTimer.Measure("App.Initialize XAML 加载"))
        {
            AvaloniaXamlLoader.Load(this);
        }
        StartupProfiler.Mark("XamlLoaded");
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.Initialize: Avalonia XAML 加载完成");

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == ActualThemeVariantProperty)
            {
                UpdateTrayIcon();
                UpdateTrayMenuIcons();
            }
        };
    }

    private void UpdateTrayIcon()
    {
        try
        {
            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            var uri = new Uri(isDark ? "avares://yuSnip/Assets/Black.ico" : "avares://yuSnip/Assets/White.ico");
            var icons = TrayIcon.GetIcons(this);
            var trayIcon = icons?.FirstOrDefault();
            if (trayIcon != null)
            {
                trayIcon.Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateTrayIcon: " + ex.Message);
        }
    }

    /// <summary>
    /// 按条目文案取对应的矢量几何。<c>null</c> 表示该条目不配图标。
    /// 「显示贴图」按当前显隐返回眼睛 / 闭眼两种几何。
    /// </summary>
    private string? TrayGeometryFor(string header)
    {
        // 「显示贴图」必须先于「贴图」判定，「截图历史」必须先于「截图」判定，否则会被其前缀抢走
        if (header.StartsWith("截图历史")) return OcrIconGeometry.TrayHistory;
        if (header.StartsWith("截图")) return OcrIconGeometry.TrayCapture;
        if (header.StartsWith("显示贴图"))
            return _stickersHidden ? OcrIconGeometry.TrayHideStickers : OcrIconGeometry.TrayShowStickers;
        if (header.StartsWith("贴图")) return OcrIconGeometry.TraySticker;
        if (header.StartsWith("保存目录")) return OcrIconGeometry.TraySaveDir;
        if (header.StartsWith("使用统计")) return OcrIconGeometry.TrayStats;
        if (header.StartsWith("主题颜色")) return OcrIconGeometry.TrayPalette;
        if (header.StartsWith("设置")) return OcrIconGeometry.TraySettings;
        if (header.StartsWith("运行日志")) return OcrIconGeometry.TrayLog;
        if (header.StartsWith("管理员模式")) return OcrIconGeometry.TrayAdmin;
        if (header.StartsWith("重新启动程序")) return OcrIconGeometry.TrayRestart;
        if (header.StartsWith("退出")) return OcrIconGeometry.TrayExit;
        return null;
    }

    /// <summary>
    /// 托盘「管理员模式」当前档位。以当前进程真实提权状态为权威判定标准：
    /// 当进程为管理员时展示高亮已开启态，点击即请求降权；
    /// 当进程为普通权限时展示常规态，点击即请求提权。
    /// 彻底杜绝落盘配置与当前进程实际权限脱节导致的死锁与反向操作。
    /// </summary>
    private static bool TrayAdminModeOn() => ElevationHelper.IsElevated();

    /// <summary>
    /// 托盘菜单图标：把框架塞进 <c>MenuItem.Icon</c> 的裸 <c>Image</c> **换成 `Path.menuIcon`**，
    /// 使托盘与贴图右键菜单走完全相同的一套图标实现（同槽 16×16、同色 Token、同 `.on` / `.danger` 语义）。
    ///
    /// 为什么必须换掉而不是约束它：<c>NativeMenuItem.Icon</c> 只收 <c>Bitmap</c>，框架在
    /// <c>NativeMenuBarPresenter.CreateContainerForNativeItem</c> 里写死
    /// <c>new Image { Source = i }</c>——**不带任何尺寸**，于是位图按自身尺寸参与布局，
    /// 超出 Fluent 图标槽位就被裁掉右下角（这正是「图标显示不全」的真因，连查四轮）。
    /// 而该 <c>Image</c> 位于 <c>Icon</c> 属性内、由模板 <c>PART_IconPresenter</c> 承载，
    /// 不在 <c>MenuItem</c> 的逻辑/视觉子树上，实测 <c>MenuItem Image</c>、
    /// <c>MenuItem:icon /template/ Image</c>、甚至全局 <c>Image</c> 选择器**都匹配不到**。
    /// 好在 <c>MenuItem.Icon</c> 的类型是 <c>object?</c>，可以直接放 <c>Path</c>——
    /// 于是绕开位图这条路，回到与贴图菜单同源的矢量实现。
    ///
    /// 时机：托盘浮层每次右键都会新建 <c>TrayPopupRoot</c> 与容器，故挂在 <c>MenuItem</c> 的
    /// <c>Loaded</c> 类处理器上按需替换（<see cref="HookTrayMenuIconFixer"/>）。
    /// </summary>
    private void UpdateTrayMenuIcons()
    {
        // 几何与配色都在替换那一刻按当前主题/状态解析，这里只需触发一次重挂钩子。
        // 保留此方法名是为了让既有调用点（主题切换、状态变更）无需改动。
        HookTrayMenuIconFixer();
    }

    /// <summary>是否已挂过托盘图标修正钩子（全局一次即可，浮层复用同一份样式作用域）。</summary>
    private bool _trayIconFixerHooked;

    /// <summary>
    /// 给全局 <c>MenuItem</c> 挂一次性钩子：任何 <c>MenuItem</c> 加载时，
    /// 若它的 <c>Icon</c> 是框架塞的 <c>Image</c>（即来自托盘 <c>NativeMenuItem</c>），
    /// 就按 Header 换成等效的矢量 <c>Path</c>。贴图等自绘菜单的 Icon 本来就是 <c>Path</c>，不受影响。
    ///
    /// ⚠ **尺寸与颜色必须在这里直接赋值，不能只挂 <c>menuIcon</c> class 指望样式。**
    /// `MenuItem Path.menuIcon` 那条样式要求 <c>Path</c> 是 <c>MenuItem</c> 的**后代**，
    /// 而放进 <c>Icon</c> 属性的元素由模板 <c>PART_IconPresenter</c> 承载、不在其逻辑/视觉子树上，
    /// 选择器匹配不到（同一处坑已实测四种写法全落空）。少了赋值就是
    /// <c>Width/Height=NaN</c> + <c>Fill=null</c> → **图标变空白**（用户实测反馈）。
    /// class 仍然保留，纯粹为了让 Live Visual Tree 里可辨识，不承担任何视觉职责。
    /// </summary>
    private void HookTrayMenuIconFixer()
    {
        if (_trayIconFixerHooked) return;
        _trayIconFixerHooked = true;

        Avalonia.Controls.Control.LoadedEvent.AddClassHandler<MenuItem>((item, _) =>
        {
            try
            {
                // ⚠ 判定**只按 Header 匹配**，不要加任何前置条件筛"这是不是托盘条目"。
                // 两种试过且都失败的写法（各让全部图标消失一次）：
                //   ① `if (item.Icon is not Image) return`
                //      —— NativeMenuItem.Icon 现已不赋值（位图那套 Rasterize 已删），
                //         框架不会生成那个 Image，容器 Icon 恒为 null，此判断永远 return；
                //   ② `if (item.DataContext is not NativeMenuItem) return`
                //      —— 实测容器的 DataContext 也是 null，同样永远 return。
                // 靠 Header 匹配天然只命中托盘那九项；其他窗口的菜单文案不在表内，返回 null 即跳过，
                // 且它们的 Icon 早已是 XAML 里声明好的 Path，下面的"已是 Path 就跳过"也会拦住。
                // P9：只在框架实际托盘 HWND 的作用域启用，不能以 Header 相同波及其他菜单。
                if (item.GetVisualRoot() is Window trayRoot
                    && trayRoot.Name?.StartsWith("AvaloniaTrayPopupRoot_", StringComparison.Ordinal) == true
                    && !trayRoot.Styles.OfType<Themes.WorkspaceTray>().Any())
                    trayRoot.Styles.Add(new Themes.WorkspaceTray());
                string header = item.Header?.ToString() ?? "";
                string? geo = TrayGeometryFor(header);
                if (geo == null) return;   // 不是托盘那九项：原样不动（切勿清空别人的图标）

                // 取色：默认弱灰，「退出」危险红，「显示贴图」/「管理员模式」开启态品牌紫（同一开关态表达）
                string colorKey = header.StartsWith("退出") ? "SctDangerBrush"
                    : (header.StartsWith("显示贴图") && !_stickersHidden) ? "SctAccentBrush"
                    : (header.StartsWith("管理员模式") && TrayAdminModeOn()) ? "SctAccentBrush"
                    : "SctMenuIconBrush";
                var brush = item.TryFindResource(colorKey, item.ActualThemeVariant, out var scopedBrush)
                    && scopedBrush is Avalonia.Media.IBrush resolved ? resolved : TrayBrush(colorKey);

                // 悬停提示：为管理员模式条目配置明确的当前权限状态与操作反馈
                if (header.StartsWith("管理员模式"))
                {
                    bool isAdmin = TrayAdminModeOn();
                    ToolTip.SetTip(item, isAdmin
                        ? "当前为管理员模式（已开启，点击可降权为普通权限并重启软件）"
                        : "当前为普通模式（未开启管理员模式，点击可提权并重启软件）");
                }

                // 若已换过 Path 矢量图标，直接刷新几何与颜色（支持容器复用、状态切换与明暗主题重绘）
                if (item.Icon is Avalonia.Controls.Shapes.Path existingPath)
                {
                    existingPath.Data = Avalonia.Media.StreamGeometry.Parse(geo);
                    existingPath.Fill = brush;
                    return;
                }

                var path = OcrIconGeometry.CreateIcon(geo, "menuIcon");

                // 规格与 Themes/Controls.axaml 的 `MenuItem Path.menuIcon` 逐条一致，
                // 但必须在此直接赋值（原因见方法注释：样式够不到 Icon 属性内的元素）
                path.Width = 16;
                path.Height = 16;
                path.Stretch = Avalonia.Media.Stretch.Uniform;
                path.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                path.Fill = brush;

                item.Icon = path;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TrayMenuIconFixer: " + ex.Message);
            }
        }, handledEventsToo: true);
    }

    /// <summary>按当前主题解析颜色 Token 为画刷（无参 FindResource 不感知明暗，深色下会错取浅色值）。</summary>
    private Avalonia.Media.IBrush? TrayBrush(string key) => ResolveThemeBrush(key);

    /// <summary>
    /// 按当前主题解析颜色 Token 为画刷。<b>全应用取主题画刷只此一份</b>——原先录屏遮罩与
    /// 控制条各写了一份私有 FindBrush，且都在取不到时兜底成 <c>Brushes.Red</c>，
    /// 违反设计规范 §8.2（禁止硬编码兜底色）。失败一律记警告并返回 null，与 <see cref="ResolveThemeColor"/> 同口径。
    /// </summary>
    internal static Avalonia.Media.IBrush? ResolveThemeBrush(string key)
    {
        Application? app = Current;
        if (app != null && app.TryFindResource(key, app.ActualThemeVariant, out object? res)
            && res is Avalonia.Media.IBrush brush)
        {
            return brush;
        }
        AppLogger.Warn("App.ResolveThemeBrush: 未能解析主题资源 " + key);
        return null;
    }

    /// <summary>
    /// 按当前主题解析颜色 Token 为 Color（文本贴图要走 GDI+，画刷版不够用）。
    /// 刻意从 Application 现取而非 <c>this.</c>：贴图窗口在 Show 之前未必已入资源查找链。
    /// </summary>
    internal static Avalonia.Media.Color? ResolveThemeColor(string key)
    {
        Application? app = Current;
        if (app != null && app.TryFindResource(key, app.ActualThemeVariant, out object? res)
            && res is Avalonia.Media.Color color)
        {
            return color;
        }
        AppLogger.Warn("App.ResolveThemeColor: 未能解析主题资源 " + key);
        return null;
    }

    /// <summary>GDI 版取色（System.Drawing.Color 与 Avalonia 的同名类型必须全程显式限定）。</summary>
    private static System.Drawing.Color? ResolveGdiColor(string key)
    {
        Avalonia.Media.Color? c = ResolveThemeColor(key);
        return c.HasValue ? System.Drawing.Color.FromArgb(c.Value.A, c.Value.R, c.Value.G, c.Value.B) : null;
    }

    /// <summary>
    /// 剪贴板 HTML 底色 → 贴图画布色。null 表示"HTML 没自带背景"，由贴图跟随主题底色。
    /// 带透明度的先叠到主题底色合成成实心色，与位图路由 <c>TextSticker.RenderHtml</c> 同一口径：
    /// 既不把透明度当不存在（旧写法会把 rgba(0,0,0,.05) 变成纯黑画布），也不让正文压在透出的壁纸上。
    /// </summary>
    private static Avalonia.Media.Color? ComposeStickerCanvas(System.Drawing.Color? gdiBg)
    {
        if (!gdiBg.HasValue) return null;

        System.Drawing.Color bg = gdiBg.Value;
        if (bg.A != 255)
        {
            System.Drawing.Color over = ResolveGdiColor("SctStickerBgColor") ?? System.Drawing.Color.White;
            bg = Core.TextStickerRenderer.CompositeOver(bg, over);
        }
        return Avalonia.Media.Color.FromArgb(bg.A, bg.R, bg.G, bg.B);
    }

    /// <summary>
    /// 文本贴图配色：四枚 SctSticker*Color Token 一次取齐。
    /// 解析失败的字段留 null，由渲染器沿用它内置的浅色字面量兜底——贴图是产物不是界面，
    /// 这里若兜成透明色，用户拿到的就是一张看不见内容的贴图。
    /// </summary>
    private static Core.TextStickerPalette ResolveStickerPalette() => new Core.TextStickerPalette
    {
        Text = ResolveGdiColor("SctStickerTextColor"),
        Back = ResolveGdiColor("SctStickerBgColor"),
        CodeBack = ResolveGdiColor("SctStickerCodeBgColor"),
        CodeBorder = ResolveGdiColor("SctStickerCodeBorderColor"),
    };

    public override void OnFrameworkInitializationCompleted()
    {
        StartupProfiler.Mark("FrameworkInit");
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.OnFrameworkInitializationCompleted: Avalonia 框架初始化完成");

        // 全局取消 ToolTip 自动超时隐藏（只要鼠标不移开，提示就一直显示）
        try
        {
            ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((sender, e) =>
            {
                try
                {
                    if (e.NewValue is true)
                    {
                        StopToolTipCloseTimers(sender);
                        if (sender is Control control && ToolTip.GetTip(control) is Control tipControl)
                        {
                            StopToolTipCloseTimers(tipControl);
                        }
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("配置全局 ToolTip 悬停时长异常: " + ex.Message);
        }

        ApplyThemeVariant();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnDesktopExit;
            // 系统注销／关机走请求事件；Exit 发生在所有窗口关闭之后，不能再从空窗口列表保存现场。
            desktop.ShutdownRequested += (_, _) => Services.StickerArchiveStore.SaveActiveSession();
            // 旧版 RapidOCR-json 残留进程清理已挪到后台（见 RunDeferredStartupWorkAsync）。
            // 它内部是 Process.GetProcessesByName，需要一次全系统进程快照（实测 ~17ms）；
            // 而 OCR 自 2026-08 起已是进程内 ONNX 推理，这里几乎永远扫不到东西。
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.OnFrameworkInitializationCompleted: ShutdownMode=OnExplicitShutdown");
        }
        else
        {
            AppLogger.Warn("App.OnFrameworkInitializationCompleted: ApplicationLifetime 不是 ClassicDesktop");
        }

        RegisterActionHotkeys();
        StartupProfiler.Mark("HotkeysReady");

        // 录屏临时区清扫：上次崩溃/强杀残留的录制会话目录只在本机自用前提下整包删除
        try { Core.Recording.TempSpace.SweepOnStartup(); }
        catch (Exception ex) { AppLogger.Warn("App: 录屏临时区清扫异常: " + ex.Message); }

        // 稿 4 检测时机：应用启动后台静默探测一次 FFmpeg（结果缓存供「开始录制前快检」，不阻塞启动）
        try { Core.Recording.FfmpegLocator.WarmUp(AppSettings.Current.RecFfmpegPath); }
        catch (Exception ex) { AppLogger.Warn("App: FFmpeg 预热探测异常: " + ex.Message); }

        TryShowStartupNotification();

        // 管理员模式反馈：托盘提示常驻标注权限级别；接力启动的那一次再补一条 toast，让用户确认重启已生效。
        if (ElevationHelper.IsElevated())
        {
            UpdateTrayToolTip("yuSnip（管理员模式）");
            if (Program.IsRelayLaunch) Windows.StickerToastWindow.ShowAtTop("已以管理员模式重启");
        }
        else
        {
            UpdateTrayToolTip("yuSnip");
            if (Program.IsDeadminRelayLaunch) Windows.StickerToastWindow.ShowAtTop("已以普通权限重启");
        }
        // 托盘图标出现＝用户主观感知到「程序起来了」的时刻，单独打点
        StartupProfiler.Mark("TrayReady");

        // 启动时核对开机自启状态与配置一致性，自动清理历史旧版本遗留的孤儿注册表项。
        // 「管理员模式 + 开机启动」组合由计划任务接管（登录免 UAC 提权启动）并关闭 Run 键防止双启动；
        // 其余情况走注册表 Run 键，有权限时顺带清理残留的提权计划任务。
        //
        // 🔴 性能：AutostartCoordinator.Apply 经 ScheduledTaskService 派生 schtasks.exe 并阻塞等待
        // 其输出（实测 ~250ms），StartupRegistry.Apply 还要遍历 HKCU Run 键。挂在启动关键路径上会把
        // 首帧推迟同样时长，故按「本次启动是否需要降权接力」分成两条路：
        //   · 需要降权接力（极少数：接力/自启实例发现管理员模式已被关闭）→ 维持原同步时序不动。
        //     这条路紧跟 TryRestartDeElevated + Shutdown，且原本就排在 StartIpcServer 之前，
        //     属 docs/protection.md 接力启动不变量的覆盖范围，不改动其相对顺序。
        //   · 不需要（绝大多数启动）→ 丢后台线程，首帧不再等它。
        try
        {
            var settings = AppSettings.Current;

            // 降权接力的判定条件全部在 Apply 之前就已知（不依赖 Apply 的返回值），
            // 故可以先算出来再决定走同步还是后台。
            bool needDeElevateRestart = ElevationHelper.IsElevated() && !settings.RunAsAdmin
                && (Program.IsRelayLaunch || Program.IsAutostartLaunch)
                && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

            if (needDeElevateRestart)
            {
                AutostartCoordinator.Apply(settings);

                // 接力/开机自启实例发现管理员模式已被关闭（例如普通权限下关闭开关时任务清理被搁置）：
                // 协调器刚清理了计划任务并恢复了 Run 键，这里降权重启回普通模式，完成"关闭"的闭环。
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime relayDesktop
                    && ElevationHelper.TryRestartDeElevated())
                {
                    Dispatcher.UIThread.Post(() => relayDesktop.Shutdown());
                }
            }
            else
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        // 执行时重新取 Current（设置窗口与会话共用同一缓存实例），
                        // 不捕获启动瞬间的快照——否则用户在这 250ms 内改「开机启动」会被旧值反超。
                        // Apply 内部已串行化，与设置页保存路径并发也安全。
                        AutostartCoordinator.Apply(AppSettings.Current);
                        StartupProfiler.Mark("AutostartApplied(bg)");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("App: 后台同步自启状态异常: " + ex.Message);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.OnFrameworkInitializationCompleted: 同步自启状态异常: " + ex.Message);
        }

        // 其余无时序要求的启动杂务（含旧版 OCR 残留进程清理）统一延后，不占首帧
        _ = RunDeferredStartupWorkAsync();

        // 延迟静默检查更新：启动 6 秒后台请求 version.json，仅在发现新版本时弹窗提示
        _ = SilentCheckUpdateAsync();

        // 截图自动识别文字：历史每落一条新记录就按需排队跑一次 OCR（开关在 CaptureAutoOcrService 内部现读，
        // 关闭时该回调只做一次 bool 判断即返回）
        CaptureHistoryStore.ItemRecorded += Services.CaptureAutoOcrService.OnItemRecorded;

        // 存量贴图补做文字识别：内部先等 30 秒，只在共享 OCR 安静时逐条跑，跑完这轮模型照旧被
        // 下面的空闲回收摘掉（补识别本身按需，不改变"纯后台待命不常驻 ONNX"的口径）
        _ = Services.StickerTextBackfillService.RunScheduledAsync();

        // OCR 引擎不预热、按需懒加载：纯后台待命不再常驻 ONNX 会话与模型。
        // 每分钟检查一次，超过 10 分钟未使用则释放会话收回内存，下次使用时重建。
        _ = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (s, e) =>
        {
            if (Services.CaptureSession.TryEvictIdleOcr(TimeSpan.FromMinutes(10)))
            {
                RequestMemoryCleanup("OcrIdleEvict");
            }
        });

        // 启动 IPC 命令行服务并检测启动脚本/启动动作
        StartIpcServer();
        if (!string.IsNullOrEmpty(Program.PendingScriptToRun))
        {
            string startupScript = Program.PendingScriptToRun;
            Program.PendingScriptToRun = null;
            Dispatcher.UIThread.Post(() => RunScript(startupScript));
        }
        if (!string.IsNullOrEmpty(Program.PendingActionToRun))
        {
            string startupAction = Program.PendingActionToRun;
            Program.PendingActionToRun = null;
            Dispatcher.UIThread.Post(() => RunAction(startupAction));
        }

        base.OnFrameworkInitializationCompleted();
        try
        {
            AutoCloudSync = new CloudAutoSyncService(
                () => _settingsWindow == null && !_captureRunning && !RecordFlow.IsBusy && _runningScripts == 0,
                ApplyAutomaticCloudConfigurationAsync);
        }
        catch (Exception ex)
        {
            AutoCloudSyncError = "自动同步无法启动：" + ex.GetType().Name + "。请检查配置目录权限或重启应用。";
            AppLogger.Warn("自动云同步初始化失败：" + ex.GetType().Name);
        }
        StartupProfiler.Mark("FrameworkInitCompleted");

        // 自动恢复重启或退出前屏幕上打开的贴图
        Dispatcher.UIThread.Post(() => Services.StickerRestoreService.RestoreActiveSession(), DispatcherPriority.Loaded);

        TryStartHorizontalVerificationFromEnvironment();

        Dispatcher.UIThread.Post(async () =>
        {
            // 首次截图接手这同一扇窗口，把构造、样式、首轮布局与首次显示提前到启动空闲期。
            CaptureWindow? prepared = null;
            Avalonia.Media.Imaging.WriteableBitmap? warmupBitmap = null;
            bool warmupConsumed = false;
            try
            {
                if (!_captureRunning && !IsShuttingDown)
                {
                    var cancellation = new System.Threading.CancellationTokenSource();
                    _capturePipelineWarmupCancellation = cancellation;
                    _capturePipelineWarmup = Task.Run(() => WarmCapturePipeline(cancellation.Token));
                    prepared = new CaptureWindow();
                    _preparedCaptureWindow = prepared;
                    prepared.InitTextToolControls();
                    prepared.FlushConstructionDiagnostics("Startup");
                    warmupBitmap = await _capturePipelineWarmup;
                    warmupConsumed = true;
                    // 等后台工作时窗口可能已被首用接手或退出关闭，旧预热不能再 Show 或 Close 它。
                    if (ReferenceEquals(_preparedCaptureWindow, prepared) && !_captureRunning && !IsShuttingDown)
                    {
                        var bitmapForWindow = warmupBitmap;
                        warmupBitmap = null; // 所有权交给窗口，在收回／首用／关闭时释放。
                        await prepared.PrepareForFirstCaptureAsync(bitmapForWindow);
                        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.OnFrameworkInitializationCompleted: 首次截图窗口及捕获管线准备完成");
                    }
                }
                else
                {
                    // 用户已先发起截图，真实窗口正在加载相同资源，不再并发创建第二棵视觉树。
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.OnFrameworkInitializationCompleted: 截图已启动，跳过重复的 CaptureWindow 预热");
                }
            }
            catch (Exception ex)
            {
                if (prepared != null && ReferenceEquals(_preparedCaptureWindow, prepared))
                {
                    _preparedCaptureWindow = null;
                    try { prepared.Close(); } catch { }
                }
                AppLogger.Warn("App.OnFrameworkInitializationCompleted: CaptureWindow 预热异常: " + ex.Message);
            }
            finally
            {
                // 建窗异常也必须等后台收尾，不能丢失它随后生成的位图。
                if (!warmupConsumed && _capturePipelineWarmup != null)
                {
                    _capturePipelineWarmupCancellation?.Cancel();
                    warmupBitmap = await _capturePipelineWarmup;
                }
                warmupBitmap?.Dispose();
                _capturePipelineWarmup = null;
                _capturePipelineWarmupCancellation?.Dispose();
                _capturePipelineWarmupCancellation = null;
            }

            RequestMemoryCleanup("StartupPrewarm");
            StartupProfiler.Mark("CaptureWindowPrewarm");
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.OnFrameworkInitializationCompleted: 启动预热完成，已排队基础内存清理");
            StartupProfiler.Summary();
        }, DispatcherPriority.Background);
    }

    private static Avalonia.Media.Imaging.WriteableBitmap? WarmCapturePipeline(System.Threading.CancellationToken cancellation)
    {
        Avalonia.Media.Imaging.WriteableBitmap? bitmap = null;
        try
        {
            if (cancellation.IsCancellationRequested) return null;
            ScreenCaptureTool.Core.Capture.CapturedFrame frame;
            using (PerformanceTimer.Measure("CaptureStartup.CaptureScreen"))
                frame = new ScreenCaptureTool.Core.Capture.ScreenCapturer().CaptureVirtualScreen();
            using var ownedFrame = frame;
            if (cancellation.IsCancellationRequested) return null;
            using (PerformanceTimer.Measure("CaptureStartup.ConvertBitmap"))
                bitmap = Utils.BitmapConversion.ToAvaloniaCaptureBitmap(frame);
            if (cancellation.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return null;
            }
            if (bitmap != null)
            {
                // 只预热图片呈现管线，不把启动时的桌面留在预备窗口或 GPU 纹理里。
                using (var framebuffer = bitmap.Lock())
                {
                    unsafe
                    {
                        System.Runtime.InteropServices.NativeMemory.Clear((void*)framebuffer.Address,
                            checked((nuint)((long)framebuffer.RowBytes * framebuffer.Size.Height)));
                    }
                }
                Utils.BitmapConversion.PrepareCaptureTarget(bitmap.PixelSize, cancellation);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            bitmap?.Dispose();
            AppLogger.Warn("App.WarmCapturePipeline: 捕获预热失败，正式截图沿用原路径：" + ex.Message);
            return null;
        }
    }

    /// <summary>UI 线程先取消剩余预热；正式捕获在后台等当前原生调用收尾，避免双路截屏争抢。</summary>
    internal Task StopCapturePipelineWarmup()
    {
        Dispatcher.UIThread.VerifyAccess();
        _capturePipelineWarmupCancellation?.Cancel();
        return (Task?)_capturePipelineWarmup ?? Task.CompletedTask;
    }

    /// <summary>只借出一次未绑定会话的启动窗口，后续会话继续新建并按原流程销毁。</summary>
    internal CaptureWindow? TakePreparedCaptureWindow()
    {
        var prepared = _preparedCaptureWindow;
        _preparedCaptureWindow = null;
        if (prepared == null) return null;
        try
        {
            prepared.FinishFirstCapturePreparation();
            prepared.RefreshPreparedCaptureSettings();
            return prepared;
        }
        catch (Exception ex)
        {
            try { prepared.Close(); } catch { }
            AppLogger.Warn("App.TakePreparedCaptureWindow: 预备窗口不可用，改为新建：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 启动后延迟 1.5 秒静默检查更新（可在设置中关闭）。
    /// 网络失败完全静默不弹窗；发现新版本时在屏幕中央置顶强提醒弹出 UpdateWindow。
    /// </summary>
    private static async Task SilentCheckUpdateAsync()
    {
        try
        {
            if (!AppSettings.Current.CheckUpdateOnStartup)
            {
                AppLogger.Info("SilentCheckUpdate: 用户已关闭启动自动检查，跳过");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5));

            var info = await Core.Update.UpdateService.CheckAsync();
            if (!Core.Update.UpdateService.HasNewer(info))
            {
                AppLogger.Info($"SilentCheckUpdate: 已是最新版本 v{Core.Update.UpdateService.CurrentVersionStr}");
                return;
            }

            AppLogger.Info($"SilentCheckUpdate: 发现新版本 v{info.Version} (Build {info.Build})");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    var win = new Windows.UpdateWindow(silentAutoCheck: true, known: info);
                    win.Topmost = true;
                    win.Show();
                    win.Activate();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("SilentCheckUpdate: 弹出更新窗口失败: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            // 更新检查永不打扰用户：任何失败仅记日志
            AppLogger.Info("SilentCheckUpdate: 检查失败(静默跳过): " + ex.Message);
        }
    }

    private void TryStartHorizontalVerificationFromEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable("YUSNIP_VERIFY_HORIZONTAL_REGION");
        if (string.IsNullOrWhiteSpace(raw)) return;

        string[] parts = raw.Split(',');
        if (parts.Length != 4
            || !int.TryParse(parts[0], out int x)
            || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int width)
            || !int.TryParse(parts[3], out int height)
            || width <= 1 || height <= 1)
        {
            AppLogger.Warn("Horizontal verification ignored: invalid region=" + raw);
            return;
        }

        int autoFinishMs = 12_000;
        string? timeoutRaw = Environment.GetEnvironmentVariable("YUSNIP_VERIFY_HORIZONTAL_FINISH_MS");
        if (int.TryParse(timeoutRaw, out int parsedTimeout))
            autoFinishMs = Math.Clamp(parsedTimeout, 2_000, 60_000);

        var region = new System.Drawing.Rectangle(x, y, width, height);
        Dispatcher.UIThread.Post(async () =>
        {
            AppLogger.Info("Horizontal verification starting. Region=" + region + ", AutoFinish=" + autoFinishMs + "ms");
            var options = new ScreenCaptureTool.LongScroll.LongScrollOptions(region)
            {
                Axis = ScreenCaptureTool.LongScroll.ScrollAxis.Horizontal,
                CaptureIntervalMs = 180,
                EnableFixedLayerMode = false,
                MaxFixedLayerAlongLengthPx = 180,
                VerificationAutoFinishAfterMs = autoFinishMs,
                IsDarkMode = false
            };
            var session = new ScreenCaptureTool.LongScroll.LongScrollSession(options, AppSettings.Load());
            using var result = await session.RunAsync();
            AppLogger.Info("Horizontal verification completed. Success=" + (!result.Canceled) + ", Message=" + result.Message);
        });
    }

    public void ApplyThemeVariant()
    {
        var settings = AppSettings.Load();
        if (settings.ThemeMode == "浅色")
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        }
        else if (settings.ThemeMode == "暗色")
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }
        else
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        }
        UpdateTrayIcon();
        UpdateTrayMenuIcons();
    }

    private void TryShowStartupNotification()
    {
        try
        {
            var settings = AppSettings.Load();
            if (!settings.ShowStartupNotification) return;

            UpdateTrayToolTip("yuSnip");
            AppLogger.Info("App.TryShowStartupNotification: 已更新托盘提示为 yuSnip");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.TryShowStartupNotification: 失败 " + ex.Message);
        }
    }

    private void UpdateTrayToolTip(string text)
    {
        try
        {
            var icons = TrayIcon.GetIcons(this);
            if (icons == null) return;
            foreach (var icon in icons)
            {
                icon.ToolTipText = text;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.UpdateTrayToolTip: 更新托盘提示失败 " + ex.Message);
        }
    }

    // -------------------- 动作热键体系（统一注册/分发） --------------------

    /// <summary>
    /// 统一注册所有动作的全局热键。动作定义来自 <see cref="ActionsStore"/>（actions.ini），
    /// 每个绑定一个 <see cref="HotKeyManager"/> 实例、共享同一个消息泵窗口（按热键 ID 区分）。
    /// 同一绑定被多个动作占用时只注册第一个，其余记冲突日志跳过。
    /// </summary>
    private void RegisterActionHotkeys()
    {
        // 录制期间的动作刷新不能重新装回监听；恢复时统一读取最新动作表。
        if (_hotkeysSuspended) return;
        UnregisterActionHotkeys();
        try
        {
            _actions = ActionsStore.Load();
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.RegisterActionHotkeys: 加载动作表失败", ex);
            _actions = new System.Collections.Generic.List<ActionDefinition>();
        }

        _captureShortcutActionIds = _actions.Where(a => a.Enabled && a.HasHotKey && IsPlainCaptureAction(a)).Select(a => a.Id).ToArray();
        var seenBindings = new System.Collections.Generic.HashSet<long>();
        foreach (ActionDefinition action in _actions)
        {
            if (!action.Enabled || !action.HasHotKey) continue;

            long bindingKey = ((long)action.HotKeyModifiers << 32) | action.HotKeyVirtualKey;
            string hotkeyText = FormatHotKey(action.HotKeyModifiers, action.HotKeyVirtualKey);
            if (!seenBindings.Add(bindingKey))
            {
                AppLogger.Warn("App.RegisterActionHotkeys: 动作「" + action.Name + "」热键 " + hotkeyText + " 与其他动作冲突，跳过注册");
                continue;
            }
            if (ActionKeyboardBinding.IsExtended(action.HotKeyModifiers)) continue;

            try
            {
                _sharedHotkeyWindow ??= new ScreenCaptureTool.Platform.MessageOnlyWindow();
                string actionId = action.Id;
                string actionName = action.Name;
                var mgr = new ScreenCaptureTool.Core.HotKeyManager(
                    () => Dispatcher.UIThread.Post(() => RunActionById(actionId), DispatcherPriority.Input),
                    _nextActionHotKeyId++,
                    _sharedHotkeyWindow);

                if (mgr.Register(action.HotKeyModifiers, action.HotKeyVirtualKey))
                {
                    _actionHotkeys.Add(mgr);
                    AppLogger.Info("已注册动作热键：" + actionName + " = " + hotkeyText);
                }
                else
                {
                    AppLogger.Warn("动作「" + actionName + "」热键 " + hotkeyText + " 注册失败（已被系统或其他软件占用），错误码: " + mgr.LastRegisterError);
                    mgr.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("App.RegisterActionHotkeys 注册动作热键异常: " + action.Name, ex);
            }
        }

        // 托盘菜单显示：取内置截图/贴图动作的当前绑定，换算成手势交给快捷键列
        foreach (ActionDefinition action in _actions)
        {
            if (action.Id == "builtin.capture")
                _captureHotKeyGesture = BuildTrayGesture(action.HotKeyModifiers, action.HotKeyVirtualKey);
            else if (action.Id == "builtin.pin")
                _stickerHotKeyGesture = BuildTrayGesture(action.HotKeyModifiers, action.HotKeyVirtualKey);
        }
        UpdateTrayMenuHotkeys();
        if (_mouseActions != null || _actions.Exists(a => a.Enabled && (ActionMouseBinding.IsSupported(a)
            || a.HasHotKey && ActionKeyboardBinding.IsExtended(a.HotKeyModifiers))))
        {
            try
            {
                var input = EnsureKeyboardInputService();
                input.SetSettingsWindow(_settingsWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                input.Configure(_actions);
            }
            catch (Exception ex)
            {
                AppLogger.Error("App.RegisterActionHotkeys: 全局鼠标配置失败", ex);
            }
        }
    }

    internal GlobalMouseActionService EnsureKeyboardInputService()
    {
        _mouseActions ??= new GlobalMouseActionService(
            (action, gesture) => Dispatcher.UIThread.Post(() => RunMouseAction(action, gesture), DispatcherPriority.Input),
            action => !_captureRunning && !_hotkeysSuspended
                && (action.MouseTrigger == ActionMouseTrigger.Click || !RecordFlow.IsBusy),
            dispatchKeyboard: binding => Dispatcher.UIThread.Post(() =>
            {
                var action = _actions.Find(a => a.Id == binding.Id && a.Enabled
                    && a.HotKeyModifiers == binding.HotKeyModifiers && a.HotKeyVirtualKey == binding.HotKeyVirtualKey);
                if (action != null && CanDispatchKeyboardAction(action)) RunActionById(action.Id);
            }, DispatcherPriority.Input),
            canStartKeyboard: CanDispatchKeyboardAction,
            allowKeyboardInOwnWindow: window => !_hotkeysSuspended
                && _activeSession?.CanHandleRecordingShortcutInWindow(window) == true);
        return _mouseActions;
    }

    private void RunMouseAction(ActionDefinition binding, MouseActionGesture? gesture)
    {
        if (gesture != null)
        {
            double dispatchMs = (System.Diagnostics.Stopwatch.GetTimestamp() - gesture.StartedTimestamp)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF GlobalMouse.Dispatch age={dispatchMs:F2}ms action={binding.Id}");
        }
        var action = _actions.Find(a => a.Id == binding.Id && a.Enabled
            && a.MouseModifiers == binding.MouseModifiers && a.MouseButton == binding.MouseButton
            && a.MousePrefixKey == binding.MousePrefixKey
            && a.MouseTrigger == binding.MouseTrigger && a.Script == binding.Script);
        if (action == null || _captureRunning || _hotkeysSuspended
            || gesture?.Read().Cancelled == true)
        {
            gesture?.Cancel();
            return;
        }
        RunScript(ActionStepCatalog.ExecutionScript(action), gesture);
    }

    private void UnregisterActionHotkeys()
    {
        foreach (var mgr in _actionHotkeys)
        {
            try { mgr.Dispose(); } catch { }
        }
        _actionHotkeys.Clear();
        try { _sharedHotkeyWindow?.Dispose(); } catch { }
        _sharedHotkeyWindow = null;
    }

    // -------------------- 热键录制拦截（录制期间临时反注册全局热键） --------------------

    private volatile bool _hotkeysSuspended;
    internal bool GlobalHotkeysSuspended => _hotkeysSuspended;

    /// <summary>
    /// 临时反注册全局热键，并暂停附加键盘组合与鼠标引导键候选。
    /// 用于快捷键录制期间防止已注册的全局热键拦截用户按键。
    /// 可重复调用（幂等），仅在首次从已注册状态切换时执行实际反注册。
    /// </summary>
    public void SuspendGlobalHotkeys()
    {
        if (_hotkeysSuspended) return;
        _hotkeysSuspended = true;
        _activeSession?.SetRecordingShortcutSuspended(true);

        // 附加组合暂停识别、清理候选并等待已有配对；保留监听位置，不与第三方反复争先后顺序。
        try { _mouseActions?.SetRecordingPaused(true); }
        catch (Exception ex) { AppLogger.Error("App.SuspendGlobalHotkeys: 暂停键鼠监听失败", ex); }

        // 反注册动作热键（不 Dispose 消息窗口，仅 Unregister 每个热键）
        foreach (var mgr in _actionHotkeys)
        {
            try { mgr.Unregister(); } catch { }
        }

        // 反注册旧式截图热键（MainWindow 持有）
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mw)
            {
                try { mw.SuspendHotKey(); } catch { }
            }
        }
    }

    /// <summary>
    /// 恢复所有全局热键注册。与 <see cref="SuspendGlobalHotkeys"/> 配对使用。
    /// 重新调用 RegisterActionHotkeys 完整重注册。
    /// </summary>
    public void ResumeGlobalHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;
        _activeSession?.SetRecordingShortcutSuspended(false);

        // 重新注册动作热键（含旧式截图/贴图热键，因为它们也走 actions.ini）
        RegisterActionHotkeys();
        try { _mouseActions?.SetRecordingPaused(false); }
        catch (Exception ex) { AppLogger.Error("App.ResumeGlobalHotkeys: 恢复键鼠识别失败", ex); }
    }

    internal static bool IsPlainCaptureAction(ActionDefinition action) =>
        ScriptExecutor.TryParse(action.Script, out var calls, out _, validateOrder: false)
        && calls.Count == 1 && calls[0].Method is "screenshot" or "screenshotandedit"
        && calls[0].Arguments.Length == 0;

    // 只读 UI 发布的动作 ID 和录屏状态；低级钩子中不解析脚本、不读取控件。
    internal bool CanDispatchKeyboardAction(ActionDefinition action) => !_hotkeysSuspended
        && (!_captureRunning || (_activeSession?.CanHandleCaptureRecordingShortcut == true
            && Array.IndexOf(_captureShortcutActionIds, action.Id) >= 0));

    internal bool TryHandleRecordingCaptureShortcut(ActionDefinition action) => _captureRunning
        && !_hotkeysSuspended && IsPlainCaptureAction(action)
        && _activeSession?.CanHandleCaptureRecordingShortcut == true
        && _activeSession?.TryHandleRecordingShortcut() == true;

    /// <summary>按 Id 执行动作（热键分发路径，已在 UI 线程）。</summary>
    public void RunActionById(string actionId)
    {
        ActionDefinition? action = null;
        foreach (ActionDefinition a in _actions)
        {
            if (a.Id == actionId) { action = a; break; }
        }
        if (action == null || !action.Enabled) return;
        if (TryHandleRecordingCaptureShortcut(action)) return;
        AppLogger.Info("App: 动作热键触发「" + action.Name + "」");
        RunScript(ActionStepCatalog.ExecutionScript(action));
    }

    /// <summary>
    /// 按名称或 Id 执行动作（外部调用路径：<c>yuSnip.exe -a "名称"</c> / IPC action: 前缀）。
    /// 大小写不敏感；动作不存在或未启用时仅记日志。
    /// </summary>
    public void RunAction(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return;
        if (_actions.Count == 0)
        {
            try { _actions = ActionsStore.Load(); } catch { }
        }

        ActionDefinition? action = null;
        foreach (ActionDefinition a in _actions)
        {
            if (string.Equals(a.Id, nameOrId, StringComparison.Ordinal)) { action = a; break; }
        }
        if (action == null)
        {
            foreach (ActionDefinition a in _actions)
            {
                if (string.Equals(a.Name, nameOrId, StringComparison.OrdinalIgnoreCase)) { action = a; break; }
            }
        }

        if (action == null)
        {
            AppLogger.Warn("App.RunAction: 未找到动作「" + nameOrId + "」");
            return;
        }
        if (!action.Enabled)
        {
            AppLogger.Warn("App.RunAction: 动作「" + action.Name + "」已停用，忽略调用");
            return;
        }

        AppLogger.Info("App.RunAction: 执行动作「" + action.Name + "」");
        RunScript(ActionStepCatalog.ExecutionScript(action));
    }

    private static string FormatHotKey(uint modifiers, uint virtualKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        ActionKeyboardBinding.AddModifierNames(parts, modifiers);

        string keyText;
        if (virtualKey >= 0x70 && virtualKey <= 0x7B)
        {
            keyText = "F" + (virtualKey - 0x70 + 1); // VK_F1..VK_F12
        }
        else if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            keyText = ((char)virtualKey).ToString();
        }
        else
        {
            keyText = "VK_" + virtualKey.ToString("X");
        }
        parts.Add(keyText);
        return string.Join("+", parts);
    }

    /// <summary>
    /// Win32 全局热键（MOD_* 位 + VK）→ 菜单快捷键列手势。
    /// MOD_ALT/CONTROL/SHIFT/WIN 的位值与 <see cref="KeyModifiers"/> 的 Alt/Control/Shift/Meta 逐位一致，可直接强转。
    /// 主键无法映射（未绑定或表外按键）时返回 null，让快捷键列留空，避免显示 "VK_20" 这类内部码。
    /// </summary>
    private static KeyGesture? BuildTrayGesture(uint modifiers, uint virtualKey)
    {
        if (ActionKeyboardBinding.IsExtended(modifiers)) return null;
        Key key = VirtualKeyToKey(virtualKey);
        if (key == Key.None) return null;
        return new KeyGesture(key, (KeyModifiers)(int)modifiers);
    }

    /// <summary>VK → Avalonia <see cref="Key"/>，与热键录制端 ActionEditorWindow.KeyToVirtualKey 保持同一套按键范围。</summary>
    private static Key VirtualKeyToKey(uint vk)
    {
        // Avalonia 的 Key 枚举取值与 VK 不同，但 A–Z / D0–D9 / F1–F12 三段内部连续，可按偏移换算
        if (vk >= 0x41 && vk <= 0x5A) return (Key)((int)Key.A + (int)(vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39) return (Key)((int)Key.D0 + (int)(vk - 0x30));
        if (vk >= 0x70 && vk <= 0x7B) return (Key)((int)Key.F1 + (int)(vk - 0x70));

        return vk switch
        {
            0x08 => Key.Back,
            0x09 => Key.Tab,
            0x0D => Key.Enter,
            0x20 => Key.Space,
            0x21 => Key.PageUp,
            0x22 => Key.PageDown,
            0x23 => Key.End,
            0x24 => Key.Home,
            0x25 => Key.Left,
            0x26 => Key.Up,
            0x27 => Key.Right,
            0x28 => Key.Down,
            0x2D => Key.Insert,
            0x2E => Key.Delete,
            0xBA => Key.OemSemicolon,
            0xBB => Key.OemPlus,
            0xBC => Key.OemComma,
            0xBD => Key.OemMinus,
            0xBE => Key.OemPeriod,
            0xBF => Key.OemQuestion,
            0xC0 => Key.OemTilde,
            0xDB => Key.OemOpenBrackets,
            0xDC => Key.OemPipe,
            0xDD => Key.OemCloseBrackets,
            0xDE => Key.OemQuotes,
            _ => Key.None,
        };
    }

    /// <summary>
    /// 快速贴图分支逻辑（UI 线程）：
    /// a) 截图会话进行中且有选区窗口 → 把选区转贴图（复用现有贴图链路）。
    /// a′) 「选中文本贴图」开关开启 → 后台注入一次 Ctrl+C 抓选区文本（见 <see cref="GrabSelectedTextThenPinAsync"/>）。
    /// b) 否则取剪贴板内容生成贴图（图片优先，其次文本）。
    /// c) 都没有 → 提示"无可贴图内容"。
    /// </summary>
    private void HandleQuickSticker()
    {
        try
        {
            // 分支 a：截图进行中
            if (_activeSession != null && _activeSession.HasActiveSelectionWindow)
            {
                AppLogger.Info("App.HandleQuickSticker: 截图选区 → 贴图");
                _activeSession.RequestStickerFromCurrentSelection();
                return;
            }

            // 分支 a′：选中文本贴图。抓词要 sleep 轮询（最长 250ms），绝不能在 UI 线程做；
            // 在途标志防的是连按两次 F3 让两次「快照 → 复制 → 还原」互相踩剪贴板。
            if (AppSettings.Current.StickerGrabSelectedText)
            {
                if (_grabbingSelectedText)
                {
                    AppLogger.Info("App.HandleQuickSticker: 上一次抓词仍在进行，忽略本次触发");
                    return;
                }
                _ = GrabSelectedTextThenPinAsync();
                return;
            }

            PinFromClipboardContent();
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.HandleQuickSticker 异常", ex);
        }
    }

    private bool _grabbingSelectedText;

    /// <summary>
    /// 「选中文本贴图」的异步外壳：后台跑 <see cref="Platform.SelectedTextGrabber.Grab"/>，
    /// 结果回 UI 线程落地。三种结局：
    /// 抓到文本 → 用快照的 text+html 出贴图（原剪贴板已在 Grab 内还原）；
    /// 前台权限更高 → 只提示，不偷偷贴一份旧剪贴板内容；
    /// 其余（没复制成功 / 复制的不是文本）→ 原样落到 <see cref="PinFromClipboardContent"/>，行为与开关无关。
    /// </summary>
    private async Task GrabSelectedTextThenPinAsync()
    {
        _grabbingSelectedText = true;
        Platform.SelectedTextGrabResult grab;
        try
        {
            grab = await Task.Run(Platform.SelectedTextGrabber.Grab);
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.GrabSelectedTextThenPinAsync: 抓词任务异常", ex);
            grab = new Platform.SelectedTextGrabResult
            {
                Status = Platform.SelectedTextGrabStatus.Failed,
                Detail = ex.Message,
            };
        }
        finally
        {
            _grabbingSelectedText = false;
        }

        AppLogger.Info("App: 选中文本抓词 status=" + grab.Status + "，" + grab.Detail);

        switch (grab.Status)
        {
            case Platform.SelectedTextGrabStatus.GrabbedText when !string.IsNullOrEmpty(grab.Text):
                string text = grab.Text!;
                string? html = grab.Html;
                // await 的续体通常已回到 UI 线程，显式 Post 是防调用方没有同步上下文（脚本线程）
                Dispatcher.UIThread.Post(() =>
                {
                    AppLogger.Info("App.HandleQuickSticker: 选区文本 → 贴图，长度=" + text.Length);
                    ShowStickerFromClipboardText(text, html);
                });
                return;

            case Platform.SelectedTextGrabStatus.BlockedElevation:
                Dispatcher.UIThread.Post(() =>
                    Windows.StickerToastWindow.Show(grab.UserHint ?? "目标窗口权限更高，无法读取其选区"));
                return;

            default:
                // NothingChanged / ChangedNotText / BlockedForeground / Failed：与开关关闭时完全一致
                Dispatcher.UIThread.Post(PinFromClipboardContent);
                return;
        }
    }

    /// <summary>
    /// 快速贴图的剪贴板兜底通路（原「分支 b / 分支 c」）：图片文件 &gt; 剪贴板图片 &gt; 剪贴板文本 &gt; 提示无内容。
    /// </summary>
    private void PinFromClipboardContent()
    {
        try
        {
            // 分支 b：剪贴板图片。图片文件优先——资源管理器复制图片文件时会附带一份渲染出的
            // CF_DIB，先读 DIB 会拿到那份小图并遮蔽同一剪贴板里的全分辨率原文件。
            System.Drawing.Bitmap? img = ClipboardService.TryGetImageFromClipboardFile();
            if (img == null)
            {
                img = ClipboardService.TryGetImage();
            }
            if (img != null)
            {
                // 大图保护：如果图片面积超过 4000×4000（≈61MB DIB），先缩放再贴图，
                // 避免多次拷贝导致 OOM 崩溃。
                const int MaxStickerPixels = 4000 * 4000;
                long pixelCount = (long)img.Width * img.Height;
                AppLogger.Info($"App.HandleQuickSticker: 剪贴板图片 → 贴图，尺寸={img.Width}x{img.Height}，像素={pixelCount}");

                if (pixelCount > MaxStickerPixels)
                {
                    double downscale = Math.Sqrt((double)MaxStickerPixels / pixelCount);
                    int newW = Math.Max(1, (int)(img.Width * downscale));
                    int newH = Math.Max(1, (int)(img.Height * downscale));
                    AppLogger.Info($"App.HandleQuickSticker: 大图缩放 {img.Width}x{img.Height} → {newW}x{newH}（避免 OOM）");

                    var downscaled = new System.Drawing.Bitmap(newW, newH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(downscaled))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, newW, newH);
                    }
                    img.Dispose();
                    img = downscaled;
                }

                ShowStickerFromClipboardImage(img);
                return;
            }

            // 分支 b：剪贴板文本
            string? text = ClipboardService.TryGetText();
            if (!string.IsNullOrEmpty(text))
            {
                AppLogger.Info("App.HandleQuickSticker: 剪贴板文本 → 贴图，长度=" + text.Length);
                ShowStickerFromClipboardText(text);
                return;
            }

            // 分支 c：无内容
            Windows.StickerToastWindow.Show("无可贴图内容");
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.PinFromClipboardContent 异常", ex);
        }
    }

    /// <summary>
    /// 剪贴板图片生成贴图。
    /// 若该图是"最近一次截图"（尺寸匹配且未过期），还原到原位置、原尺寸（不缩放）；
    /// 否则保持宽高比缩放（≤屏宽60%/屏高70%），居中于鼠标所在屏。
    /// </summary>
    private void ShowStickerFromClipboardImage(System.Drawing.Bitmap bitmap)
    {
        // 判重（2026-09-29 用户口径「同一张图片已经贴过，就不要重复贴图」）：屏幕上已有逐像素相同的那张
        // → 定位过去就完事。必须在录历史之前判：为重复贴图再灌一条同图历史，是把历史列表灌满垃圾的成因。
        var duplicate = StickerDedup.FindSameImage(bitmap);
        if (duplicate != null)
        {
            StickerDedup.Reveal(duplicate, "剪贴板贴图");
            try { bitmap.Dispose(); } catch { }   // 位图所有权本来要交给贴图窗口，这里没人接就得本地释放
            return;
        }

        // 历史：必须在所有权交给贴图窗口之前记录（内部同步克隆，不动原图）
        string? historyId = CaptureHistoryStore.TryRecord(bitmap, CaptureHistorySource.Sticker, null, AppSettings.Current);

        var sticker = new Windows.StickerWindow(bitmap);
        sticker.SetHistoryId(historyId);

        // 先尝试匹配"最近截图"——还原原位置。卡片几何（内容圆角 + 描边宽）也一并从这份记忆里取：
        // 剪贴板裸图不带这些参数，只有尺寸匹配上"刚刚自己复制出去的那张"时才拿得到确定值。
        System.Drawing.Point? remembered = LastCaptureMemory.TryMatch(bitmap.Width, bitmap.Height);

        if (ClipboardService.LastWritePreservedAlpha && IsCornerTransparent(bitmap))
        {
            // 四角透明＝"这圈透明是图形本身，不是图没填满"→ 藏棋盘格。
            // 背后是否改垫棋盘格另判一件事：外圈有没有真的一圈半透明**阴影带**。旧写法把两件事混成
            // 一句，纯圆角的剪贴板图也被当成"自带阴影"（2026-09-06 修）；带阴影带也不再关窗体阴影，
            // 两种阴影并存（2026-09-07 用户拍板）。
            // 记忆对不上（别的应用复制来的圆角图）时半径改从轮廓反推，不至于退回方角阴影。
            // ⚠ 反推只能得到"外沿总半径"这一个数，拆不出内容半径与描边宽（对角像素探针要 0.2px 精度，
            // 1px 误差就换成 4.3px 描边宽，得不偿失），故按无描边处理：外沿就是内容轮廓，与改版前一致。
            var card = remembered.HasValue
                ? LastCaptureMemory.LastCardGeometry
                : new Core.Capture.SelectionEffects.CardGeometry
                {
                    ContentRadiusPx = Core.Capture.SelectionEffects.ProbeOuterRadiusPx(bitmap),
                };
            sticker.ApplyBakedCaptureEffects(card, Core.Capture.SelectionEffects.HasBakedShadowBand(bitmap));
        }

        if (remembered.HasValue)
        {
            AppLogger.Info("App.ShowStickerFromClipboardImage: 匹配到最近截图，还原原位置 " + remembered.Value);
            sticker.SetStickerScreenPosition(new PixelPoint(remembered.Value.X, remembered.Value.Y));
        }
        else
        {
            // 无记忆 → 居中 + 按需缩放。
            var monitor = ScreenCaptureTool.Platform.MonitorHelper.GetMonitorAtCursor();
            int maxW = (int)(monitor.Width * 0.6);
            int maxH = (int)(monitor.Height * 0.7);

            double scale = Math.Min((double)maxW / bitmap.Width, (double)maxH / bitmap.Height);
            if (scale < 1.0)
            {
                int targetW = Math.Max(1, (int)(bitmap.Width * scale));
                int targetH = Math.Max(1, (int)(bitmap.Height * scale));
                PositionStickerCentered(sticker, monitor, targetW, targetH);
            }
            else
            {
                PositionStickerCentered(sticker, monitor, bitmap.Width, bitmap.Height);
            }
        }

        sticker.Show();
        StickerFocusManager.SetFocus(sticker);
    }

    private static bool IsCornerTransparent(System.Drawing.Bitmap bitmap)
    {
        if (bitmap.Width < 2 || bitmap.Height < 2) return false;
        try
        {
            // 检查四角像素是否有显著透明度 (Alpha < 32)，确认为圆角/阴影截图烘焙位图
            System.Drawing.Color tl = bitmap.GetPixel(0, 0);
            System.Drawing.Color tr = bitmap.GetPixel(bitmap.Width - 1, 0);
            System.Drawing.Color bl = bitmap.GetPixel(0, bitmap.Height - 1);
            System.Drawing.Color br = bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1);
            return tl.A < 32 || tr.A < 32 || bl.A < 32 || br.A < 32;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 剪贴板文本生成贴图：渲染为位图后按图片贴图规则居中。
    ///
    /// <paramref name="htmlOverride"/> 非空时用它作为富文本来源，不再现读剪贴板——
    /// 「选中文本贴图」抓完词会把原剪贴板还原，那时现读到的 HTML 属于上一个复制者，
    /// 与新文本配成一张贴图就是图文错配。传 null 则保持现读剪贴板的老行为。
    /// </summary>
    internal void ShowStickerFromClipboardText(string text, string? htmlOverride = null)
    {
        // 判重（与图片贴图同一条口径）：文本 / Markdown / 颜色贴图按<b>源文本</b>判，
        // 命中就定位过去，省掉一次整版排版与一条同内容历史。
        var duplicate = StickerDedup.FindSameText(text);
        if (duplicate != null)
        {
            StickerDedup.Reveal(duplicate, "文本贴图");
            return;
        }

        var monitor = ScreenCaptureTool.Platform.MonitorHelper.GetMonitorAtCursor();
        
        // 版宽上限＝屏宽的 2/3。旧值取"屏幕宽高较小边的 1/3"（1080 高屏＝360px），
        // 长文本被这条窄栏拉成两三千 px 高，反而更容易整幅超出工作区（窗口被
        // StickerWindow.ClampRectToWorkArea 夹住后，越出窗口的部分不在绘制表面上＝真丢字）。
        // EstimateOptimalStickerWidth 把它当**上限**而非固定值，短文本仍按内容自动取窄。
        int monitorLimit = (int)(monitor.Width * 2.0 / 3.0);
        int maxContentW = EstimateOptimalStickerWidth(text, monitorLimit);

        string? htmlText = htmlOverride ?? ClipboardService.TryGetHtml();
        TextStickerResult result;
        
        bool isHighlyColored = false;
        if (!string.IsNullOrEmpty(htmlText))
        {
            var lines = Core.TextStickerRenderer.ParseHtmlToLines(htmlText, out _);
            isHighlyColored = Core.TextStickerRenderer.HasMultipleColors(lines);
        }

        AppLogger.Info($"App.ShowStickerFromClipboardText: text.Length={text?.Length ?? 0}, htmlText.Length={htmlText?.Length ?? 0}, isHighlyColored={isHighlyColored}, isMarkdown={Core.TextStickerRenderer.IsMarkdown(text ?? "")}, theme={Current?.ActualThemeVariant}");

        if (!isHighlyColored && Core.TextStickerRenderer.IsMarkdown(text))
        {
            AppLogger.Info("App.ShowStickerFromClipboardText: 路由 → Markdown 贴图");
            Avalonia.Media.Color? avaBgColor = null;
            if (!string.IsNullOrEmpty(htmlText))
            {
                Core.TextStickerRenderer.ParseHtmlToLines(htmlText, out var gdiBgColor);
                avaBgColor = ComposeStickerCanvas(gdiBgColor);
            }

            var sticker = Windows.StickerWindow.FromMarkdown(text, maxContentW, avaBgColor);
            PositionStickerCentered(sticker, monitor, sticker.SourcePixelWidth, sticker.SourcePixelHeight);
            sticker.Show();
            StickerFocusManager.SetFocus(sticker);
            // Markdown 贴图创建时没有位图（活体控件渲染），排版完成后由贴图自身拍快照补录历史
            sticker.ScheduleHistoryRecord();
            return;
        }

        // Try converting rich text to Markdown only if it contains structural tags (avoiding destroying IDE code snippets)
        if (!isHighlyColored && !string.IsNullOrEmpty(htmlText) && System.Text.RegularExpressions.Regex.IsMatch(htmlText, @"<(h[1-6]|b\b|strong\b|i\b|em\b|ul\b|ol\b|li\b|blockquote\b|table\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            string convertedMd = Core.HtmlToMarkdownConverter.Convert(htmlText);
            // If the conversion yielded significant Markdown structural tags
            if (Core.TextStickerRenderer.IsMarkdown(convertedMd))
            {
                AppLogger.Info("App.ShowStickerFromClipboardText: 路由 → HTML→Markdown 转换贴图");
                Core.TextStickerRenderer.ParseHtmlToLines(htmlText, out var gdiBgColor);
                Avalonia.Media.Color? avaBgColor = ComposeStickerCanvas(gdiBgColor);
                var sticker = Windows.StickerWindow.FromMarkdown(convertedMd, maxContentW, avaBgColor);
                PositionStickerCentered(sticker, monitor, sticker.SourcePixelWidth, sticker.SourcePixelHeight);
                sticker.Show();
                StickerFocusManager.SetFocus(sticker);
                sticker.ScheduleHistoryRecord();
                return;
            }
        }

        if (!string.IsNullOrEmpty(htmlText))
        {
            AppLogger.Info("App.ShowStickerFromClipboardText: 路由 → HTML 富文本位图贴图");
            result = Core.TextStickerRenderer.RenderHtml(htmlText, text, maxContentW, ResolveStickerPalette());
        }
        else
        {
            AppLogger.Info("App.ShowStickerFromClipboardText: 路由 → 纯文本位图贴图");
            result = Core.TextStickerRenderer.Render(text, maxContentW, ResolveStickerPalette());
        }

        // 位图所有权转移给贴图窗口（StickerWindow 直接引用并在 OnClosed 释放）。
        // 此处不得 using/Dispose result——TextStickerResult.Dispose 会释放 Bitmap，
        // 会导致贴图后续使用 _bitmap 的功能（另存为等）拿到已释放对象而抛
        // "Parameter is not valid"。LayoutInfo 为纯托管类，无需额外释放。
        var textSticker = new Windows.StickerWindow(result.Bitmap, text, result.LayoutInfo);
        PositionStickerCentered(textSticker, monitor, result.Bitmap.Width, result.Bitmap.Height);
        textSticker.Show();
        StickerFocusManager.SetFocus(textSticker);
        // 交贴图自己录：位图贴图直接入历史，颜色贴图（屏幕上显示的是色卡不是这张位图）
        // 会改拍活体快照，历史缩略图才与屏幕所见一致。
        textSticker.ScheduleHistoryRecord();
    }

    /// <summary>
    /// 估算文本贴图的最佳内容宽度（含内边距）。
    /// 区分 CJK（~18px/字）和 ASCII（~10px/字）宽度，考虑显式换行。
    /// 短文本不强制换行；长文本按高宽比约 1.3 估算。
    /// </summary>
    private static int EstimateOptimalStickerWidth(string text, int maxWidthLimit)
    {
        const float fontSize = 18f;
        const int padding = 16;
        const float cjkCharWidth = fontSize;        // CJK 字符宽度 ≈ 字号
        const float asciiCharWidth = fontSize * 0.55f; // ASCII 字符宽度 ≈ 字号 * 0.55
        const float lineHeight = fontSize * 1.35f;   // 近似行高

        int monitorLimit = maxWidthLimit;

        // 统计字符
        int cjkCount = 0;
        int asciiCount = 0;
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r') continue;
            if (IsCjkCharForEstimation(c)) cjkCount++;
            else asciiCount++;
        }

        // 估算所有文字排在单行的总宽度
        float estimatedTextWidth = cjkCount * cjkCharWidth + asciiCount * asciiCharWidth;

        // 显式换行中最长行的估算宽度
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        float maxLineWidth = 0;
        foreach (var ln in lines)
        {
            int lnCjk = 0, lnAscii = 0;
            foreach (char c in ln)
            {
                if (c == '\r') continue;
                if (IsCjkCharForEstimation(c)) lnCjk++;
                else lnAscii++;
            }
            float lw = lnCjk * cjkCharWidth + lnAscii * asciiCharWidth;
            if (lw > maxLineWidth) maxLineWidth = lw;
        }

        // 如果文本很短（单行能放下），直接用估算宽度 + 内边距
        float singleLineWidth = maxLineWidth + padding * 2;
        if (singleLineWidth <= monitorLimit)
        {
            return Math.Max(120, (int)singleLineWidth);
        }

        // 长文本：按面积估算最佳宽度，期望高宽比 ≈ 1.3
        // 总面积 = 文字宽度 * 行高（每行面积）
        // 已有显式换行的面积 + 待换行面积
        double totalArea = estimatedTextWidth * lineHeight;
        int optimalWidth = (int)Math.Sqrt(totalArea / 1.3);

        // 不能短于最长单行 + 内边距（否则该行无法放下）
        int minByLongestLine = (int)(maxLineWidth + padding * 2);

        int result = Math.Max(optimalWidth, minByLongestLine);
        result = Math.Max(200, Math.Min(result, monitorLimit));
        return result;
    }

    /// <summary>快速判断字符是否为 CJK 字符（用于宽度估算，与 TextStickerRenderer.IsCjkCharacter 逻辑一致）。</summary>
    private static bool IsCjkCharForEstimation(char c)
    {
        if (c >= 0x4E00 && c <= 0x9FFF) return true;
        if (c >= 0x3400 && c <= 0x4DBF) return true;
        if (c >= 0xF900 && c <= 0xFAFF) return true;
        if (c >= 0x3040 && c <= 0x309F) return true;
        if (c >= 0x30A0 && c <= 0x30FF) return true;
        if (c >= 0xAC00 && c <= 0xD7AF) return true;
        if (c >= 0x3000 && c <= 0x303F) return true;
        if (c >= 0xFF00 && c <= 0xFFEF) return true;
        return false;
    }

    /// <summary>把贴图 Image 定位到指定显示器中央（窗口本身会带阴影边距，由 SetStickerScreenPosition 处理）。</summary>
    private static void PositionStickerCentered(Windows.StickerWindow sticker, System.Drawing.Rectangle monitor, int bmpW, int bmpH)
    {
        // Image 目标位置 = 显示器中央（按位图尺寸居中，物理像素）。
        int x = monitor.X + (monitor.Width - bmpW) / 2;
        int y = monitor.Y + (monitor.Height - bmpH) / 2;
        sticker.SetStickerScreenPosition(new Avalonia.PixelPoint(Math.Max(0, x), Math.Max(0, y)));
    }

    /// <summary>
    /// Y 键切换阴影时调用：显示 toast 提示。
    /// 由 StickerWindow.OnWindowKeyDown 调用，toast 定位到该贴图区域中央。
    /// </summary>
    internal static void RaiseStickerShadowToast(bool shadowOn, Windows.StickerWindow? sticker)
    {
        Windows.StickerToastWindow.Show(shadowOn ? "贴图阴影：开" : "贴图阴影：关", sticker);
    }

    /// <summary>当前 App 实例（供静态入口访问实例字段）。</summary>
    private static new App? Current => Application.Current as App;

    /// <summary>设置窗口保存动作表后触发：全量重注册动作热键。</summary>
    private void OnSettingsActionsChanged(object? sender, EventArgs e)
    {
        try
        {
            RegisterActionHotkeys();
            AppLogger.Info("App.OnSettingsActionsChanged: 动作热键已按新动作表重注册");
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.OnSettingsActionsChanged 异常", ex);
        }
    }

    private async void OnTrayClicked(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnTrayClicked: 托盘点击触发截图");
        await RunCaptureAsync();
    }

    private async void OnMenuCaptureClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuCaptureClick: 菜单触发截图");
        await RunCaptureAsync();
    }

    private bool _stickersHidden = false;

    private void OnMenuToggleStickersVisibilityClick(object? sender, EventArgs e)
    {
        // 状态以 _stickersHidden 为唯一真相：该项已去掉 ToggleType/IsChecked（不再有勾选列），
        // 不能再从 item.IsChecked 反推，否则永远读到 false、切换只能单向。
        _stickersHidden = !_stickersHidden;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var windows = desktop.Windows.OfType<StickerWindow>().ToList();
            foreach (var w in windows)
            {
                w.IsVisible = !_stickersHidden;
            }
        }

        // 开关态由图标表达（眼睛/闭眼 + 品牌紫/弱灰），故切换后需重绘托盘图标
        UpdateTrayMenuIcons();
    }

    private void OnMenuStickerClick(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(HandleQuickSticker);
    }

    /// <summary>托盘「截图历史」：打开历史窗口（静态单例，重复点击只激活）。</summary>
    private void OnMenuCaptureHistoryClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuCaptureHistoryClick: 打开截图历史");
        CaptureHistoryWindow.ShowHistory();
    }

    private void UpdateTrayMenuHotkeys()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons == null || icons.Count == 0) return;
        var menu = icons[0].Menu;
        if (menu == null) return;

        // 托盘菜单由 Avalonia 自绘，Header 只留功能名，快捷键走 Gesture → 全局 MenuItem 右对齐快捷键列
        foreach (var item in menu.Items)
        {
            if (item is not NativeMenuItem nativeItem || nativeItem.Header == null) continue;
            string headerStr = nativeItem.Header.ToString() ?? "";
            // 「截图历史」没有快捷键，必须先排除：否则会被「截图」前缀抢走截图热键的展示手势
            if (headerStr.StartsWith("截图历史"))
                continue;
            if (headerStr.StartsWith("截图"))
                nativeItem.Gesture = _captureHotKeyGesture;
            else if (headerStr.StartsWith("贴图"))
                nativeItem.Gesture = _stickerHotKeyGesture;
        }
    }

    private void OnMenuOpenSaveDirClick(object? sender, EventArgs e)
    {
        try
        {
            var settings = AppSettings.Load();
            string dir = settings.SaveDirectory;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
            {
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.OnMenuOpenSaveDirClick: 打开保存目录失败", ex);
        }
    }

    private void OnMenuToggleThemeClick(object? sender, EventArgs e)
    {
        var settings = AppSettings.Load();
        if (settings.ThemeMode == "暗色")
        {
            settings.ThemeMode = "浅色";
        }
        else
        {
            settings.ThemeMode = "暗色";
        }
        settings.Save();
        ApplyThemeVariant();
    }

    private void OnMenuSettingsClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuSettingsClick: 打开设置");
        ShowSettingsWindow();
    }

    private void OnMenuLogClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuLogClick: 打开运行日志");
        LogWindow.ShowLogs();
    }

    private void OnMenuUsageStatsClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuUsageStatsClick: 打开使用统计");
        ShowSettingsWindow(navigateToStats: true);
    }

    /// <summary>
    /// 打开（或激活已打开的）设置窗口。
    /// 供窗口外部调用（如截图历史窗口空态的「打开设置」引导、工具栏组二级栏的设置入口等）；内部入口仍走 <see cref="ShowSettingsWindow"/>。
    /// </summary>
    public void OpenSettingsWindow(bool navigateToStats = false, SettingsPage? initialPage = null) => ShowSettingsWindow(navigateToStats, initialPage);

    /// <summary>稿 2 01 卡：录制期托盘进入/退出录制态（tooltip 标注，还原时保留管理员后缀）。</summary>
    public void NotifyRecordState(bool recording)
    {
        if (recording) UpdateTrayToolTip("yuSnip · 录制中");
        else UpdateTrayToolTip(ElevationHelper.IsElevated() ? "yuSnip（管理员模式）" : "yuSnip");
    }

    /// <summary>「去设置」统一入口：直达「依赖运行」页并高亮组件卡（进度卡与录屏缺组件提示都走这里）。</summary>
    public void OpenSettingsDeps()
    {
        ShowSettingsWindow(initialPage: SettingsPage.Deps);
        _settingsWindow?.NavigateToDeps();
    }

    /// <summary>
    /// 打开（或激活已打开的）运行日志窗口。
    /// </summary>
    public void OpenLogWindow() => LogWindow.ShowLogs();

    private void ShowSettingsWindow(bool navigateToStats = false, SettingsPage? initialPage = null)
    {
        SettingsPage? targetPage = initialPage ?? (navigateToStats ? SettingsPage.Stats : null);
        if (_settingsWindow != null)
        {
            AppLogger.Info("App.ShowSettingsWindow: 复用已有设置窗口");
            WindowPresentationHelper.BringToForeground(_settingsWindow);
            if (targetPage.HasValue)
            {
                _settingsWindow.NavigateToPage(targetPage.Value);
            }
            return;
        }

        AppLogger.Info("App.ShowSettingsWindow: 创建设置窗口");
        _settingsWindow = new SettingsWindow(initialNavigateToStats: navigateToStats, initialPage: targetPage);
        _settingsWindow.ActionsChanged += OnSettingsActionsChanged;
        _settingsWindow.Opened += (_, _) =>
            _mouseActions?.SetSettingsWindow(_settingsWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        _settingsWindow.Closed += (_, _) =>
        {
            AppLogger.Info("App.ShowSettingsWindow: 设置窗口已关闭");
            if (_settingsWindow != null)
            {
                _settingsWindow.ActionsChanged -= OnSettingsActionsChanged;
            }
            _mouseActions?.SetSettingsWindow(IntPtr.Zero);
            _settingsWindow = null;
            AutoCloudSync?.RequestCheck();
            RequestMemoryCleanup("SettingsClosed");
        };
        WindowPresentationHelper.ShowSmoothly(_settingsWindow);
    }

    /// <summary>
    /// 托盘「管理员模式」：点一下就是切换（当前已提权→降权重启；未提权→提权重启）。
    /// 决策树与设置页共用 <see cref="AdminModeCoordinator"/>，文案逐字一致；
    /// 图标几何/取色在下次打开菜单时按落盘值重算，故这里无需手动重绘。
    /// </summary>
    private async void OnMenuAdminModeClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuAdminModeClick: 托盘请求切换管理员模式");

        bool wantAdmin = !ElevationHelper.IsElevated();
        AdminModeResult result = await AdminModeCoordinator.ToggleAsync(
            wantAdmin, msg => StickerToastWindow.ShowAtTop(msg));

        switch (result.Outcome)
        {
            case AdminModeOutcome.Restarted:
                // 新实例已在路上（它会在有界时间内等本实例释放单实例锁），本进程让位
                await ShutdownAfterTrayClose();
                return;
            case AdminModeOutcome.Failed:
                StickerToastWindow.ShowAtTop("切换管理员模式失败：" + (result.Error ?? "未知原因"));
                break;
            default:
                StickerToastWindow.ShowAtTop(result.Message);
                break;
        }

        // 托盘切换不改权限档位（需要改的都走上面的重启），故只同步设置页的回显与托盘图标
        _settingsWindow?.SyncAdminModeFromExternal();
        UpdateTrayMenuIcons();
    }

    /// <summary>
    /// 托盘「重新启动程序」：以当前权限原地重启（同权限、无 UAC），
    /// 用于热键注册异常、显存/内存占用偏高时自助复位，无需先退出再手动打开。
    /// </summary>
    private async void OnMenuRestartClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuRestartClick: 用户请求重新启动程序");
        if (!ElevationHelper.TryRestartSelf())
        {
            StickerToastWindow.ShowAtTop("重新启动失败，请手动退出后重新打开");
            return;
        }
        await ShutdownAfterTrayClose();
    }

    /// <summary>
    /// 退出前先让托盘自绘浮层关闭：浮层还开着就 Shutdown，新实例可能把关闭动画的
    /// 残留消息当成本进程的输入。延迟只有几十毫秒，用户无感。
    /// 退出顺序仍由 lifetime.Exit 保证（注销热键 → finally 释放单实例互斥锁）。
    /// </summary>
    private async Task ShutdownAfterTrayClose()
    {
        IsShuttingDown = true;
        _commandIpc?.Dispose();
        _capturePipelineWarmupCancellation?.Cancel();
        Utils.BitmapConversion.ShutdownCaptureTargetPreparation();
        var prepared = _preparedCaptureWindow;
        _preparedCaptureWindow = null;
        try { prepared?.Close(); } catch { }
        try { Services.StickerArchiveStore.SaveActiveSession(); } catch { }
        await Task.Delay(40);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            AppLogger.Warn("App.ShutdownAfterTrayClose: 无 ClassicDesktop lifetime，无法正常 Shutdown");
        }
    }

    private void OnMenuExitClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuExitClick: 用户请求退出");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IsShuttingDown = true;
            Services.StickerArchiveStore.SaveActiveSession();
            desktop.Shutdown();
        }
        else
        {
            AppLogger.Warn("App.OnMenuExitClick: 无 ClassicDesktop lifetime，无法正常 Shutdown");
        }
    }

    public async Task<bool> RunCaptureAsync(string? scriptAction = null, MouseActionGesture? mouseGesture = null,
        Action<System.Drawing.Bitmap, System.Drawing.Rectangle>? capturedImage = null)
    {
        if (_captureRunning)
        {
            AppLogger.Warn("App.RunCaptureAsync: 截图会话已在运行，忽略重复触发");
            mouseGesture?.Cancel();
            return false;
        }
        _captureRunning = true;

        // 🔴 性能埋点：会话总耗时（热键入口 → 收尾完成）。与 CaptureSession 内部的分阶段打点配合，
        // 可定位「启动慢」到底慢在遮罩出现前还是完成动作（裁剪/落盘/贴图）那一段。
        var sessionSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.RunCaptureAsync: 截图会话开始");

            var settings = AppSettings.Current;
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("App.RunCaptureAsync: 设置就绪，AutoDetectMode=" + settings.AutoDetectMode + ", SaveDirectory=" + settings.SaveDirectory);

            var session = new CaptureSession(settings);
            session.ScriptAction = scriptAction;
            session.MouseGesture = mouseGesture;
            session.CapturedImageSink = capturedImage;
            _activeSession = session; // 暴露给 F3 注入用
            CaptureSessionResult? result = await session.RunAsync();

            if (result == null && session.OutputHandedOff)
            {
                AppLogger.Info("截图内容已交给后续处理。");
            }
            else if (result == null)
            {
                AppLogger.Info("截图已取消。");
            }
            else if (result.CreatedSticker)
            {
                AppLogger.Info("截图已创建贴图。");
            }
            else if (result.PickedColor)
            {
                AppLogger.Info("已复制颜色：" + result.PickedColorHex);
            }
            else if (result.IsLongScroll)
            {
                AppLogger.Info("长截图：" + (result.Message ?? "已结束。"));
            }
            else if (result.IsRecord)
            {
                AppLogger.Info("录屏：" + (result.Message ?? "已结束。"));
            }
            else if (result.ClipboardCopied)
            {
                AppLogger.Info("截图已复制到剪贴板。");
            }
            else
            {
                AppLogger.Info("截图已保存：" + result.SavedPath);
                if (settings.PlaySaveSound)
                {
                    PlaySaveSound();
                }
                if (settings.ShowSaveNotification)
                {
                    ShowSavedNotification(result.SavedPath);
                }
            }
            return result != null || session.OutputHandedOff;
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.RunCaptureAsync 异常", ex);
            StickerToastWindow.ShowAtTop("截图未完成：" + ex.Message);
            return false;
        }
        finally
        {
            mouseGesture?.Cancel();
            _activeSession = null;
            _captureRunning = false;
            sessionSw.Stop();
            AppLogger.Info("App.RunCaptureAsync: 截图会话结束，总耗时 " + sessionSw.ElapsedMilliseconds + "ms");
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("PERF CaptureSession.Memory " + StartupProfiler.GcSnapshot());
            RequestMemoryCleanup("CaptureSessionEnd");
        }
    }

    /// <summary>
    /// 各 Type 上「疑似定时器字段」的反射结果缓存。
    ///
    /// <para><b>为什么必须缓存</b>：本处理器注册在 <see cref="ToolTip.IsOpenProperty"/> 的类处理器上，
    /// 对全应用每一个 <see cref="Control"/> 生效，而项目里有 151 处 ToolTip
    /// （CaptureWindow 38 / AnnotationToolbar 22 / SettingsWindow 21 / OcrWindow 15 …）。
    /// 旧实现每开一次提示就沿整条继承链重新 <c>GetFields</c> + 逐字段做名字字符串比较 + 反射取值，
    /// 鼠标划过工具栏时是连续触发，反射开销直接体现在手感上。</para>
    ///
    /// <para>缓存只改「同一个 Type 不再重复扫描」，命中的字段集合与旧实现逐一相同，语义零变化。</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.FieldInfo[]> _timerFieldCache =
        new System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.FieldInfo[]>();

    // IL2070/IL2075：反射沿继承链找 ToolTip 内部隐藏定时器，字段名与继承深度随 Avalonia
    // 版本变化，无法静态标注；与加缓存前的行为完全一致，只是结果按 Type 缓存复用。
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Reflection", "IL2070", Justification = "反射获取 ToolTip 内部隐藏定时器以阻止其自动超期隐藏")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Reflection", "IL2075", Justification = "反射获取 ToolTip 内部隐藏定时器以阻止其自动超期隐藏")]
    private static System.Reflection.FieldInfo[] GetTimerCandidateFields(Type type)
    {
        return _timerFieldCache.GetOrAdd(type, static t =>
        {
            var found = new System.Collections.Generic.List<System.Reflection.FieldInfo>();
            var cur = t;
            while (cur != null && cur != typeof(object))
            {
                var fields = cur.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                foreach (var f in fields)
                {
                    // 判据与旧实现完全一致。旧的 `|| f.Name.Contains("Timer")`（ordinal）是
                    // `Contains("timer", OrdinalIgnoreCase)` 的真子集，去掉后命中集合不变。
                    if (f.FieldType == typeof(DispatcherTimer) || f.Name.Contains("timer", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(f);
                    }
                }
                cur = cur.BaseType;
            }
            return found.ToArray();
        });
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Reflection", "IL2075", Justification = "反射获取 ToolTip 内部隐藏定时器以阻止其自动超期隐藏")]
    private static void StopToolTipCloseTimers(object? target)
    {
        if (target == null) return;
        try
        {
            foreach (var f in GetTimerCandidateFields(target.GetType()))
            {
                if (f.GetValue(target) is DispatcherTimer timer)
                {
                    timer.Stop();
                }
            }
        }
        catch
        {
            // 反射拿不到就放弃，绝不影响提示本身显示。
        }
    }

    private void ShowSavedNotification(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var notification = new SavedNotificationWindow(path);
            notification.Show();
            AppLogger.Info("App.ShowSavedNotification: 已显示保存通知，path=" + path);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.ShowSavedNotification: 显示保存通知失败：" + ex.Message);
        }
    }

    private void PlaySaveSound()
    {
        try
        {
            // MessageBeep 播放系统默认提示音，无需引用 System.Windows.Extensions。
            MessageBeep(0x00000040); // MB_ICONASTERISK
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.PlaySaveSound: 播放提示音失败：" + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    /// <summary>距上次内存回收的最小间隔（毫秒）。节流避免连续截图时反复做昂贵的 LOH 压缩。</summary>
    private const long MemoryCleanupMinIntervalMs = 5000;
    private static long _lastMemoryCleanupTick;

    /// <summary>
    /// 请求一次内存回收。<b>刻意不就地执行</b>：四个调用点里「截图会话结束的 finally」正是用户
    /// 能立刻感知的时刻（刚松手、很可能马上按第二次 F1），而阻塞式压缩 GC 会冻结全部线程——
    /// 4K 截图刚释放多个 33MB 位图、LOH 满是碎片时，压缩是 O(LOH 大小) 的内存搬移，实测可上百毫秒。
    /// 这里投到 <see cref="DispatcherPriority.ApplicationIdle"/>（空闲才跑，绝不打断交互）并做最小间隔节流。
    /// 回收口径本身（阻塞压缩 + 一次性 LOH 压缩，把常驻工作集压回基线）保持不变。
    /// </summary>
    private static void RequestMemoryCleanup(string reason)
    {
        Dispatcher.UIThread.Post(() => ForceMemoryCleanup(reason), DispatcherPriority.ApplicationIdle);
    }

    private static void ForceMemoryCleanup(string reason)
    {
        try
        {
            // 空闲优先级不代表截图没有进行：截屏任务 await 期间消息泵仍可能进入空闲。
            // 阻塞式 GC 会连钩子线程一起暂停，当前会话结束后已有清理请求接手回收。
            if (Application.Current is App app && app._captureRunning) return;
            long now = Environment.TickCount64;
            long last = System.Threading.Interlocked.Read(ref _lastMemoryCleanupTick);
            // last == 0 表示进程内首次，放行
            if (last != 0 && now - last < MemoryCleanupMinIntervalMs) return;
            System.Threading.Interlocked.Exchange(ref _lastMemoryCleanupTick, now);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            sw.Stop();

            // 把真实代价记下来：这是判断"压缩式回收值不值得留"的唯一依据
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("PERF MemoryCleanup " + reason + " " + sw.ElapsedMilliseconds + "ms " + StartupProfiler.GcSnapshot());
        }
        catch { }
    }

    /// <summary>
    /// 启动后的延后杂务：无时序要求、且各自都要做阻塞式系统调用的工作，统一丢到后台线程，
    /// 不占首帧。<see cref="AutostartCoordinator"/> 的自启核对另有分支（见调用处注释），不在此列。
    /// </summary>
    private static Task RunDeferredStartupWorkAsync()
    {
        // 用 Task.Run 而不是 async + await Task.Yield()：后者的延续会被投回 UI 线程的
        // SynchronizationContext，等于白延后。这里要的是真正离开 UI 线程。
        return Task.Run(() =>
        {
            try
            {
                // 旧版 RapidOCR-json 残留进程清理：Process.GetProcessesByName 需要一次全系统进程快照
                // （实测 ~17ms）。OCR 自 2026-08 起已是进程内 ONNX 推理，这里几乎永远扫不到东西，
                // 但保留清理以兼容从很旧版本升级上来的用户。
                ScreenCaptureTool.Core.Capture.PaddleOcrService.KillStaleOcrProcesses();
                // 老位置上的 OCR／抠图/FFmpeg 文件按 SHA-256 零字节收编进 deps 目录（设计稿 07 节）：
                // 只有哈希校验与同盘改名，不加载模型、不建推理会话，与「OCR 禁止加启动预热」那条不冲突。
                _ = ScreenCaptureTool.Core.Deps.DependencyManager.StartAdoption();
                // UI 卡顿哨兵放在延后任务里启动：它自身开销极小，但没必要挤在启动关键路径上。
                Services.UiStallWatchdog.Start();
                // 左键失效哨兵同理：40ms 一次 GetAsyncKeyState 的轮询开销可忽略，但同样不必挤在启动路径上。
                // 它负责在「左键点在本软件窗口上却没人收到」的那一瞬间把现场写下来（详见该类的判据说明）。
                Services.LeftClickSentinel.Start();
                StartupProfiler.Mark("DeferredStartupWork");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("App.RunDeferredStartupWorkAsync 异常: " + ex.Message);
            }
        });
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        IsShuttingDown = true;
        _commandIpc?.Dispose();
        AutoCloudSync?.Dispose();
        _capturePipelineWarmupCancellation?.Cancel();
        Utils.BitmapConversion.ShutdownCaptureTargetPreparation();
        var prepared = _preparedCaptureWindow;
        _preparedCaptureWindow = null;
        try { prepared?.Close(); } catch { }
        AppLogger.Info("App.OnDesktopExit: 应用退出，开始释放热键");
        try { UnregisterActionHotkeys(); AppLogger.Info("App.OnDesktopExit: 动作热键已释放"); } catch (Exception ex) { AppLogger.Error("App.OnDesktopExit: 释放动作热键异常", ex); }
        try { _mouseActions?.Dispose(); } catch (Exception ex) { AppLogger.Error("释放全局鼠标异常", ex); }
        try { CaptureSession.ShutdownSharedOcr(); AppLogger.Info("App.OnDesktopExit: 共享 OCR 引擎已释放"); } catch (Exception ex) { AppLogger.Error("App.OnDesktopExit: 释放共享 OCR 异常", ex); }
        // 使用统计走节流落盘，退出前补一次同步写盘，防止最后 1.5s 内的计数丢失
        try { UsageStatistics.Flush(); } catch (Exception ex) { AppLogger.Warn("App.OnDesktopExit: 统计落盘异常：" + ex.Message); }
        // 哨兵要在 AppLogger.Shutdown 之前停，否则它那条汇总日志写不进本次运行的日志
        try { Services.UiStallWatchdog.Stop(); } catch (Exception ex) { AppLogger.Warn("App.OnDesktopExit: 停止 UI 卡顿哨兵异常：" + ex.Message); }
        // 左键哨兵也先停：退出路径上没必要再写现场报告，且它的报告会落盘
        try { Services.LeftClickSentinel.Stop(); } catch (Exception ex) { AppLogger.Warn("App.OnDesktopExit: 停止左键哨兵异常：" + ex.Message); }
        AppLogger.Shutdown("App.OnDesktopExit");
    }

    public async void RunScript(string script, MouseActionGesture? mouseGesture = null)
    {
        await RunScriptAsync(script, mouseGesture);
    }

    public async Task<bool> RunScriptAsync(string script, MouseActionGesture? mouseGesture = null)
    {
        System.Threading.Interlocked.Increment(ref _runningScripts);
        try
        {
            AppLogger.Info("App.RunScript: 开始执行脚本");
            if (!ScriptExecutor.TryParse(script, out var calls, out string error)) throw new ArgumentException(error);
            using var env = new AppScriptEnv(mouseGesture, calls.Count > 1);
            return await ScriptExecutor.ExecuteAsync(script, env);
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.RunScript: 脚本执行异常", ex);
            mouseGesture?.Cancel();
            StickerToastWindow.ShowAtTop("动作未完成：" + ex.Message);
            return false;
        }
        finally { System.Threading.Interlocked.Decrement(ref _runningScripts); }
    }

    private Task<bool> ApplyAutomaticCloudConfigurationAsync(ScreenCaptureTool.Core.CloudSync.CloudSnapshot snapshot, string expectedHash, bool explicitChoice)
    {
        // 所有调用都在 UI 调度器续接；检查与四文件提交之间不让出线程。
        if (IsShuttingDown || _captureRunning || RecordFlow.IsBusy || _runningScripts > 0
            || (_settingsWindow != null && (!explicitChoice || _settingsWindow.HasManualCloudOperation))) return Task.FromResult(false);
        lock (ScreenCaptureTool.Core.CloudSync.CloudSnapshot.FileGate)
        {
            var current = ScreenCaptureTool.Core.CloudSync.CloudSnapshot.Capture(AppSettings.SettingsDirectory, AppSettings.Current.CloudSyncSecrets);
            if (current.Hash != expectedHash) throw new IOException("本机配置已变化，云端配置尚未应用。");
            ScreenCaptureTool.Core.CloudSync.CloudConfigTransaction.Apply(AppSettings.SettingsDirectory, snapshot,
                ScreenCaptureTool.Core.CloudSync.CloudSnapshot.Capture(AppSettings.SettingsDirectory, snapshot.IncludeSecrets).Hash);
            AppSettings.ReloadInPlace();
        }
        ScreenCaptureTool.Core.Recording.RecordPresets.Reload();
        _settingsWindow?.RefreshAfterAutomaticSync();
        ApplyThemeVariant();
        RegisterActionHotkeys();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var sticker in desktop.Windows.OfType<StickerWindow>()) sticker.RefreshCloudShortcuts();
        return Task.FromResult(true);
    }

    private void StartIpcServer()
    {
        try
        {
            _commandIpc = new CommandIpc(message =>
            {
                var queued = System.Diagnostics.Stopwatch.StartNew();
                // 与全局快捷键相同的 Input 优先级，避免排在普通 UI 工作之后。
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsShuttingDown) return;
                    AppLogger.Info($"IPC: 派发命令，UI 排队={queued.ElapsedMilliseconds}ms");
                    if (message.StartsWith(Program.ActionMessagePrefix, StringComparison.Ordinal))
                        RunAction(message.Substring(Program.ActionMessagePrefix.Length));
                    else
                        RunScript(message);
                }, DispatcherPriority.Input);
            });
        }
        catch (Exception ex) { AppLogger.Error("IPC: 启动命令监听失败", ex); }
    }

    private class AppScriptEnv : IActionWorkflowEnv, IDisposable
    {
        private readonly MouseActionGesture? _mouseGesture;
        private readonly bool _clipboardOnly;
        private ActionWorkflowContext? _workflow;
        public AppScriptEnv(MouseActionGesture? mouseGesture = null, bool clipboardOnly = false)
        {
            _mouseGesture = mouseGesture;
            _clipboardOnly = clipboardOnly;
        }
        private Task<bool> _pendingOperation = Task.FromResult(true);
        private uint? _expectedClipboard;
        public Task<bool> CompleteStepAsync() => _pendingOperation;

        public void RunWorkflowStep(string method, string[] arguments) => Queue(() =>
        {
            _workflow ??= new ActionWorkflowContext((App)Application.Current!, _mouseGesture);
            return _workflow.RunAsync(method, arguments);
        });
        public void Dispose() => _workflow?.Dispose();

        private void Queue(Action operation) => Queue(() =>
        {
            operation();
            return Task.FromResult(true);
        });

        private void Queue(Func<Task<bool>> operation)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _pendingOperation = operation();
                return;
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingOperation = completion.Task;
            Dispatcher.UIThread.Post(async () =>
            {
                try { completion.TrySetResult(await operation()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }

        public void ScreenShot(string? action)
        {
            Queue(async () =>
            {
                uint before = NativeMethods.GetClipboardSequenceNumber();
                bool completed = await ((App)Application.Current!).RunCaptureAsync(action, _mouseGesture);
                if (completed && string.Equals(action, "Copy", StringComparison.OrdinalIgnoreCase))
                {
                    uint after = NativeMethods.GetClipboardSequenceNumber();
                    if (after == before) throw new InvalidOperationException("本次截图没有复制，后续步骤已停止。");
                    _expectedClipboard = after;
                }
                return completed;
            });
        }

        public void DirectScreenShot(int x, int y, int w, int h, string? action)
        {
            Queue(async () =>
            {
                try
                {
                    using var frame = new ScreenCaptureTool.Core.Capture.ScreenCapturer().CaptureVirtualScreen();
                    int localX = x - frame.VirtualScreenRect.X;
                    int localY = y - frame.VirtualScreenRect.Y;
                    var rect = new System.Drawing.Rectangle(localX, localY, w, h);
                    
                    rect.Intersect(new System.Drawing.Rectangle(0, 0, frame.FullScreen.Width, frame.FullScreen.Height));
                    if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("截图区域不在屏幕内。");

                    var cropped = frame.FullScreen.Clone(rect, frame.FullScreen.PixelFormat);

                    if (string.Equals(action, "Pin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(action, "Sticker", StringComparison.OrdinalIgnoreCase))
                    {
                        // 历史：必须在所有权交给贴图窗口之前记录（内部同步克隆，不动原图）
                        string? scriptHistoryId = CaptureHistoryStore.TryRecord(cropped, CaptureHistorySource.Sticker, null, AppSettings.Current);

                        var sticker = new StickerWindow(cropped);
                        sticker.SetHistoryId(scriptHistoryId);
                        sticker.SetStickerScreenPosition(new PixelPoint(x, y));
                        sticker.Show();
                        sticker.Activate();
                        StickerFocusManager.SetFocus(sticker);
                        AppLogger.Info("DirectScreenShot: Created sticker at " + rect);
                    }
                    else if (string.Equals(action, "Save", StringComparison.OrdinalIgnoreCase))
                    {
                        var settings = AppSettings.Load();
                        string dir = settings.SaveDirectory;
                        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                        string fileName = settings.FileNameTemplate;
                        fileName = fileName.Replace("{yyyyMMdd_HHmmss}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        string path = System.IO.Path.Combine(dir, fileName + ".png");
                        cropped.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        AppLogger.Info("DirectScreenShot: Saved image to " + path);
                        if (settings.ShowSaveNotification)
                        {
                            StickerToastWindow.ShowAtTop("已保存到：" + path);
                        }
                    }
                    else if (string.Equals(action, "Matting", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "MattingGeneral", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "MattingPortrait", StringComparison.OrdinalIgnoreCase))
                    {
                        string? scriptHistoryId = CaptureHistoryStore.TryRecord(cropped, CaptureHistorySource.Sticker, null, AppSettings.Current);
                        var sticker = new StickerWindow(cropped);
                        sticker.SetHistoryId(scriptHistoryId);
                        sticker.SetStickerScreenPosition(new PixelPoint(x, y));
                        sticker.Show();
                        sticker.Activate();
                        StickerFocusManager.SetFocus(sticker);
                        var modelType = string.Equals(action, "MattingPortrait", StringComparison.OrdinalIgnoreCase)
                            ? ScreenCaptureTool.Core.Matting.MattingModelType.Portrait
                            : string.Equals(action, "MattingGeneral", StringComparison.OrdinalIgnoreCase)
                                ? ScreenCaptureTool.Core.Matting.MattingModelType.General
                                : AppSettings.Current.GetMattingModelType();
                        // 本贴图为抠图临时创建：结果去向设为「仅剪贴板」时抠图完成后自动关闭（桌面不留窗）
                        sticker.ExecuteMatting(modelType, closeWhenClipboardOnly: true);
                        AppLogger.Info("DirectScreenShot: Created sticker and started matting (" + modelType + ") at " + rect);
                    }
                    else // Default is "Copy"
                    {
                        using (cropped)
                        {
                            if (!await ClipboardService.SetImageAsync(cropped)) return false;
                            _expectedClipboard = NativeMethods.GetClipboardSequenceNumber();
                            StickerToastWindow.ShowAtTop("已复制到剪贴板");
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("DirectScreenShot 异常", ex);
                    throw;
                }
            });
        }

        public void PinFromClipboard()
        {
            Queue(async () =>
            {
                // 多步骤只消费剪贴板，避免选中文字抓取覆盖本次截图；单步保留快速贴图的原有设置。
                if (_expectedClipboard.HasValue && _expectedClipboard != NativeMethods.GetClipboardSequenceNumber())
                    throw new InvalidOperationException("剪贴板内容已变化，已停止以免贴错内容。");
                var app = (App)Application.Current!;
                if (!_clipboardOnly && app._activeSession?.HasActiveSelectionWindow == true)
                {
                    app.HandleQuickSticker();
                    return true;
                }
                var desktop = app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                int before = desktop?.Windows.OfType<StickerWindow>().Count() ?? 0;
                if (!_clipboardOnly && AppSettings.Current.StickerGrabSelectedText)
                {
                    if (app._grabbingSelectedText) return false;
                    await app.GrabSelectedTextThenPinAsync();
                    // 抓词方法把贴图派发回 UI；等它完成提交后再报告本步结果。
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                else app.PinFromClipboardContent();
                if (desktop == null || desktop.Windows.OfType<StickerWindow>().Count() <= before)
                    throw new InvalidOperationException("剪贴板中没有可贴图的内容。");
                return true;
            });
        }

        public void DestroyAllPin()
        {
            Queue(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var windows = desktop.Windows.OfType<StickerWindow>().ToList();
                    foreach (var w in windows)
                    {
                        w.Close();
                    }
                }
            });
        }

        public void ToggleAllStickers()
        {
            Queue(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var windows = desktop.Windows.OfType<StickerWindow>().ToList();
                    bool anyVisible = windows.Any(w => w.IsVisible);
                    foreach (var w in windows)
                    {
                        if (anyVisible) w.Hide();
                        else w.Show();
                    }
                }
            });
        }

        public void RestoreLastSticker()
        {
            Queue(() =>
            {
                var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                int before = desktop?.Windows.OfType<StickerWindow>().Count() ?? 0;
                ScreenCaptureTool.Services.StickerRestoreService.RestoreLast();
                if (desktop == null || desktop.Windows.OfType<StickerWindow>().Count() <= before)
                    throw new InvalidOperationException("没有恢复出贴图，后续步骤已停止。");
            });
        }

        public void ScreenRecord(string? action = null)
        {
            Queue(async () =>
            {
                // 录制/倒计时中再按一次（动作热键）：录制中＝结束录制（与 F11 同义）；倒计时中＝取消回到截图界面
                if (ScreenCaptureTool.Services.RecordFlow.IsBusy)
                {
                    ScreenCaptureTool.Services.RecordFlow.RequestComplete();
                    return await RecordFlow.WaitFinishAsync() == RecordFlowOutcome.Completed;
                }
                bool isQuick = string.Equals(action, "Quick", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(action, "Fast", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(action, "RecordQuick", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(action, "QuickRecord", StringComparison.OrdinalIgnoreCase);
                return await ((App)Application.Current!).RunCaptureAsync(isQuick ? "RecordQuick" : "Record", _mouseGesture);
            });
        }

        public void Translate(string? action)
        {
            Queue(async () =>
            {
                try
                {
                    var settings = AppSettings.Load();
                    var config = settings.ToTranslationConfig();
                    if (!config.IsValid)
                    {
                        _mouseGesture?.Cancel();
                        StickerToastWindow.ShowAtTop("请先在设置中配置翻译服务 API 密钥");
                        return false;
                    }

                    // 默认行为（无参数 / Screen / Capture）：启动截图会话，翻译模式跟随设置
                    if (string.IsNullOrEmpty(action) ||
                        string.Equals(action, "Screen", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(action, "Capture", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("Translate", _mouseGesture);
                    }
                    // 截图翻译（覆盖贴图）：强制 ImageToImage 模式
                    else if (string.Equals(action, "ImageToImage", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "Image", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("TranslateImageToImage", _mouseGesture);
                    }
                    // 截图翻译（文本窗口）：强制 ImageToText 模式
                    else if (string.Equals(action, "ImageToText", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("TranslateImageToText", _mouseGesture);
                    }
                    // 截图翻译（静默剪贴板）：翻译结果直接写入剪贴板，不弹窗
                    else if (string.Equals(action, "Silent", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "ClipboardSilent", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("TranslateSilent", _mouseGesture);
                    }
                    // 从剪贴板翻译文本
                    else if (string.Equals(action, "Clipboard", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "Text", StringComparison.OrdinalIgnoreCase))
                    {
                        string? clipText = ClipboardService.TryGetText();
                        if (string.IsNullOrWhiteSpace(clipText))
                        {
                            StickerToastWindow.ShowAtTop("剪贴板中没有文本");
                            return false;
                        }
                        var win = TranslationResultWindow.ForText(clipText, settings);
                        win.Show();
                        win.Activate();
                    }
                    // 翻译贴图（焦点）
                    else if (string.Equals(action, "Sticker", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "Pin", StringComparison.OrdinalIgnoreCase))
                    {
                        var focusedSticker = StickerFocusManager.Focused;
                        if (focusedSticker != null)
                        {
                            focusedSticker.TriggerTranslate();
                        }
                        else
                        {
                            StickerToastWindow.ShowAtTop("没有焦点贴图可翻译");
                            return false;
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("AppScriptEnv.Translate 异常", ex);
                    throw;
                }
            });
        }

        public void Matting(string? action)
        {
            Queue(async () =>
            {
                try
                {
                    // 抠图贴图（焦点）
                    if (string.Equals(action, "Sticker", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(action, "Pin", StringComparison.OrdinalIgnoreCase))
                    {
                        var focusedSticker = StickerFocusManager.Focused;
                        if (focusedSticker != null)
                        {
                            // 既有贴图：结果去向跟随设置（「仅剪贴板」时贴图保持原图，不被改动）
                            focusedSticker.ExecuteMatting(null);
                        }
                        else
                        {
                            StickerToastWindow.ShowAtTop("没有焦点贴图可抠图");
                        }
                        return focusedSticker != null;
                    }

                    if (string.Equals(action, "Portrait", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(action, "MattingPortrait", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("MattingPortrait", _mouseGesture);
                    }
                    else if (string.Equals(action, "General", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(action, "MattingGeneral", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("MattingGeneral", _mouseGesture);
                    }
                    else
                    {
                        return await ((App)Application.Current!).RunCaptureAsync("Matting", _mouseGesture);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("AppScriptEnv.Matting 异常", ex);
                    throw;
                }
            });
        }
    }
}
