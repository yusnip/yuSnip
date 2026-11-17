using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 左键失效哨兵（2026-09-17 新增）。
///
/// 用户口径：**鼠标左键被拦截、点其他软件也不管用**，一直卡到结束进程才恢复。这类失效在窗口层面
/// 往往完全无痕（挡板窗可以看不见；若是低级钩子吞掉事件，连窗口都不参与），事后跑外部探针又只能
/// 采到"已经不卡"的那一刻。本哨兵的目标是**在卡住的那一瞬间、在应用自己的进程里**把现场留下来。
///
/// 命中判据（五条同时成立，误报面极小）：
///   ① 物理左键按下过：`GetAsyncKeyState(VK_LBUTTON)`——它读的是**设备物理状态，不受任何钩子影响**，
///      这正是"事件被别人吞掉"唯一藏不住的判据。⚠ 故这里刻意**不**用钩子做探测：挂钩子等于让自己
///      成为嫌疑人，而钩子回调本身又在系统输入链上，会污染现场；
///   ② 按下那一刻光标压的窗口**正是本哨兵挂了投递监听的某个顶层窗**（句柄逐一比对）＝这次点击
///      本该由我们收到，且"收到没收到"这个判据本身是有效的；
///   ③ 该点落在那个窗口的**客户区**内（非客户区如标题栏、边框、系统菜单由系统自己处理，
///      本就不产生托管指针事件，必须排除，否则拖窗口必误报）；
///   ③b 该点**确实会被这个窗口接收**——贴图信封态下图片+阴影之外是**故意**穿透给下层窗口的
///      （见 <see cref="Windows.StickerWindow.DescribeWheelHit"/>），那里收不到事件属正常；
///      但"穿透没挂上"时那块透明区正是吃点击的挡板，照样判命中并显著标注。**2026-09-17 首轮回传
///      的 3 份报告里有 2 份就是漏了这条而误报的**；
///   ④ 其后 <see cref="DeliverGraceMs"/> 内本进程没有任何被监听的窗口收到左键按下。
///   ①②③③b 为真而 ④ 不成立 ⇒ 这次点击在到达本进程之前就被吃掉了。
///
/// <para><b>另外两道真实性/可判读性防线（2026-09-17 加）</b>：① 武装前用 `GetLastInputInfo`
/// 校验「物理按键位」是否陈旧——真有一次点击它必然同步更新（含 `SendInput` 注入，VNC 类远控走的
/// 正是这条路），陈旧即说明那一位不可信，直接忽略并留一条日志；② 报告里附上**鼠标捕获窗口**
/// 与**距上次系统输入**两项——捕获期间鼠标消息全归捕获窗口，光标下的窗口一个都收不到，
/// 而 `WindowFromPoint` 完全看不出来，这是"点在本窗上却没进来"的经典机制之一。</para>
///
/// <para><b>刻意不覆盖的场景（无法从进程内部判定，强行判定只会刷误报）</b>：
/// 点击落在**别的进程**的窗口上——那时我们无从知道那次点击"本该"被谁收到；
/// 点击落在**本进程的弹窗**（右键菜单、下拉框等，它们是独立的顶层而非 <see cref="Window"/>，
/// 事件不会冒泡到窗口）——没有投递监听就没有判据。</para>
///
/// 命中即写一份现场报告（桌面优先，失败退 %TEMP%），并把一条 Warn 记进内存日志（设置→日志页可见）。
/// 报告刻意**不复刻外部探针的全部采样**：探针那几个采样点要靠移动鼠标，而本哨兵全程只读。
/// </summary>
public static class LeftClickSentinel
{
    /// <summary>轮询间隔。取 40ms：人手一次点击的按下时长通常 ≥60ms，边沿检测足以不漏。</summary>
    private const int PollIntervalMs = 40;

    /// <summary>按下之后给本进程留的投递宽限。超过它仍未收到按下即判"被吞"。</summary>
    private const int DeliverGraceMs = 400;

    /// <summary>检测到物理按下比真实按下最多晚 <see cref="PollIntervalMs"/>，判"已投递"时要往前放宽这么多。</summary>
    private const int DetectLagMs = 200;

    /// <summary>两次写报告的最小间隔（去抖，防止用户连点刷出一堆文件）。</summary>
    private const long MinReportIntervalMs = 60_000;

    /// <summary>单次会话最多写几份报告。</summary>
    private const int MaxReportsPerSession = 5;

    /// <summary>报告里附带的最近日志条数。</summary>
    private const int TailLogLines = 40;

    /// <summary>UI 线程探活超时。比卡顿阈值宽：这里只判"活着没有"，不判"是否够快"。</summary>
    private const int UiProbeTimeoutMs = 1200;

    /// <summary>「距上次系统输入」超过这个毫秒数即认定物理按键位陈旧、本次按下根本没发生。
    /// 取 1500：正常路径下武装时该值 ≤ 一个轮询周期（40ms），出报告时 ≤ 宽限 400ms + 一个周期，
    /// 留足两个数量级的余量，不会误杀真命中。</summary>
    private const int StaleInputMs = 1500;

    private static readonly object Gate = new object();
    private static bool _started;
    private static Thread? _thread;
    private static int _reportCount;
    private static long _lastReportTick;

    /// <summary>本进程最近一次收到左键按下的时刻（由各窗口的隧道监听写入，UI 线程写、哨兵线程读）。</summary>
    private static long _lastPressTick;

    private static readonly List<Window> Observed = new List<Window>();
    private static readonly EventHandler<PointerPressedEventArgs> PressObserver = OnAnyPointerPressed;

    /// <summary>
    /// 被监听窗口的快照（UI 线程重建、哨兵线程读）。判据②"这次点击本该我们收"就靠它：
    /// 只有光标下的窗口**正是**我们挂了投递监听的窗口，才有"收到 / 没收到"这个判据。
    /// ⚠ 2026-09-17 起连 <see cref="Window"/> 引用一起存：判据③b 需要回问贴图窗口
    /// "这个点你到底会不会放行"（信封态下图片+阴影之外是**故意**穿透给下层窗口的，
    /// 见 <c>StickerWindow.DescribeWheelHit</c>），只有句柄没法问。
    /// </summary>
    private static volatile ObservedWindow[] _observed = Array.Empty<ObservedWindow>();

    /// <summary>被监听的一个顶层窗（句柄 + 托管窗口引用）。</summary>
    private sealed class ObservedWindow
    {
        public IntPtr Hwnd;
        public Window Window = null!;
    }

    /// <summary>一次待验证的物理按下（判据①②③全过才武装；宽限期内没收到按下就出报告）。</summary>
    private struct Pending
    {
        /// <summary>武装时刻（<c>Environment.TickCount64</c>，仅供间隔比较）。</summary>
        public long Tick;

        /// <summary>武装时刻的**墙钟**。报告里直接打印它——首轮回传的报告把 <c>TickCount64</c>
        /// 当 Unix 毫秒去格式化，印出来是 1970 年的 09:24:12，跟日志时间完全对不上，等于白采。</summary>
        public DateTime Wall;

        public int X, Y;          // 光标屏幕物理坐标
        public IntPtr Hwnd;       // 这次点击本该落到的本进程顶层窗
        public string Note;       // 判据③b 的实况描述（贴图信封放行状态），原样进报告
        public long SinceInput;   // 武装时距上一次系统级输入事件的毫秒数（真实性校验的读数）
    }

    /// <summary>
    /// 枚举期间的回填目标。`EnumWindowsProc` 委托是静态缓存的，故目标必须走静态字段——
    /// 若让 lambda 捕获局部 `list`，第二次调用会继续往第一次那个已返回的列表里塞。
    /// </summary>
    private static List<IntPtr>? _enumSink;

    private static readonly NativeMethods.EnumWindowsProc EnumOwnProc = OnEnumOwnWindow;

    private static volatile bool _stopped;

    /// <summary>启动哨兵（幂等）。放在延后启动工作里调用，不占启动关键路径。</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "yuSnip.LeftClickSentinel",
            };
            _thread.Start();
            AppLogger.Info("LeftClickSentinel: 已启动（物理按键状态 vs 本进程投递的分歧检测）");
        }
    }

    /// <summary>退出时停表，避免关机路径上还去写报告。幂等。</summary>
    public static void Stop()
    {
        _stopped = true;
    }

    /// <summary>自动清理旧版遗留在用户桌面的 yusnip-leftclick-*.txt 临时文件。</summary>
    private static void CleanupLegacyDesktopReports()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
            {
                string[] files = Directory.GetFiles(desktop, "yusnip-leftclick-*.txt");
                int removed = 0;
                foreach (string f in files)
                {
                    try
                    {
                        File.Delete(f);
                        removed++;
                    }
                    catch { }
                }
                if (removed > 0)
                {
                    AppLogger.Info($"LeftClickSentinel: 已自动清理桌面遗留临时日志 {removed} 份");
                }
            }
        }
        catch { }
    }

    private static void Loop()
    {
        CleanupLegacyDesktopReports();

        bool wasDown = false;
        var pending = default(Pending);
        int tick = 0;

        while (!_stopped)
        {
            try
            {
                Thread.Sleep(PollIntervalMs);
                tick++;

                // 每约 1 秒补挂一次监听：窗口是动态创建的，漏挂的窗口会让"收到没收到"失去判据
                if (tick % 25 == 0) EnsureObservers();

                short state = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
                bool down = (state & 0x8000) != 0;
                bool pressedSinceLastQuery = (state & 0x0001) != 0;

                if (pressedSinceLastQuery || (down && !wasDown))
                {
                    // 物理上确实按下了一次：此刻采样"这次点击本该落到谁头上"
                    pending = NativeMethods.GetCursorPos(out var pt) ? ArmIfOurs(pt.X, pt.Y) : default;
                }

                if (pending.Tick != 0 && Environment.TickCount64 - pending.Tick >= DeliverGraceMs)
                {
                    long lastPress = Volatile.Read(ref _lastPressTick);
                    bool delivered = lastPress >= pending.Tick - DetectLagMs;
                    if (!delivered) Report(pending);
                    pending = default;
                }

                wasDown = down;
            }
            catch (Exception ex)
            {
                // 哨兵自身绝不允许影响主流程
                AppLogger.Warn("LeftClickSentinel.Loop 异常: " + ex.Message);
                pending = default;
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// 判据②③：光标下的窗口必须是**我们挂了投递监听的某个顶层窗**，且该点落在它的客户区内，
    /// 且该点**确实会被这个窗口接收**（判据③b）。全过才武装这次待验证的按下；否则不归我们判。
    /// </summary>
    private static Pending ArmIfOurs(int x, int y)
    {
        var result = default(Pending);

        IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT_INT(x, y));
        if (hwnd == IntPtr.Zero) return result;
        ObservedWindow? entry = FindObserved(hwnd);
        if (entry == null) return result;
        if (!NativeMethods.IsWindowEnabled(hwnd)) return result;
        if (!IsPointInClientArea(hwnd, x, y)) return result;

        string note = string.Empty;
        if (!ShouldJudge(entry, x, y, out note)) return result;

        // 真实性校验（2026-09-17 加）：真发生过一次点击，系统级"最后一次输入事件"必然同步更新
        // （含 SendInput 注入的，VNC 类远控走的正是这条路）。若它明显陈旧，说明 GetAsyncKeyState
        // 那一位是**陈旧状态**（该位在部分场景下并不可靠，且它恰好是"或"进本判据的），
        // 本次"按下"其实没发生过——写报告只会污染现场，故忽略并留一条可见的日志。
        long sinceInput = MsSinceLastSystemInput();
        if (sinceInput >= StaleInputMs)
        {
            AppLogger.Warn("LeftClickSentinel: 忽略一次疑似误报——物理按键位为陈旧状态"
                + "（距上次系统输入 " + sinceInput + "ms，无对应输入事件）");
            return result;
        }

        result.Tick = Environment.TickCount64;
        result.Wall = DateTime.Now;
        result.X = x;
        result.Y = y;
        result.Hwnd = hwnd;
        result.Note = note;
        result.SinceInput = sinceInput;
        return result;
    }

    /// <summary>
    /// 距本会话上一次系统级输入事件的毫秒数；读不到返回 -1。
    /// ⚠ 两个 tick 口径不同：`GetLastInputInfo` 给的是 32 位 `GetTickCount`，故一律先截成 `uint`
    /// 再按无符号差值算——直接拿 `Environment.TickCount64` 相减会在开机 49.7 天后离谱。
    /// </summary>
    private static long MsSinceLastSystemInput()
    {
        try
        {
            var li = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
            };
            if (!NativeMethods.GetLastInputInfo(ref li)) return -1;
            uint now = unchecked((uint)Environment.TickCount64);
            return unchecked(now - li.dwTime);
        }
        catch
        {
            return -1;
        }
    }

    private static ObservedWindow? FindObserved(IntPtr hwnd)
    {
        ObservedWindow[] snapshot = _observed;
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Hwnd == hwnd) return snapshot[i];
        }
        return null;
    }

    /// <summary>
    /// 判据③b（2026-09-17 新增，由现场报告实测倒逼）：贴图信封态下，图片+阴影之外的区域
    /// **本来就会被窗口自己返回 <c>HTTRANSPARENT</c> 放给下层窗口**——那里永远收不到本进程的
    /// 指针事件，不排掉就会把"正常穿透"报成"左键被吞"。首轮回传的 3 份报告里有 2 份正是这么误报的
    /// （光标分别落在贴图左侧/上方的透明带里，几何完全对得上）。
    ///
    /// <para>⚠ 但**不能**一律排除：穿透没挂上时那块透明区正是吃点击的挡板，恰恰最该报。
    /// 于是判据交给贴图窗口自己回答（<c>StickerWindow.DescribeWheelHit</c>），
    /// 只在 <c>PassThroughOk</c>（穿透确已挂上、确实放行）时才放弃本次判定；
    /// <c>PassThroughBroken</c>（本该放行却没人放行）照常报，并在报告里显著标注。</para>
    /// </summary>
    private static bool ShouldJudge(ObservedWindow entry, int x, int y, out string note)
    {
        note = string.Empty;
        if (entry.Window is Windows.RecordOverlayWindow) return false;
        if (entry.Window is not Windows.StickerWindow sw) return true;

        try
        {
            Windows.StickerWindow.WheelHitState state = sw.DescribeWheelHit(x, y, out string detail);
            note = detail;

            // 穿透正常在位且点在矩形外：故意放给下层窗口的，不判被吞
            if (state == Windows.StickerWindow.WheelHitState.PassThroughOk) return false;
            if (state == Windows.StickerWindow.WheelHitState.PassThroughBroken)
                note = "⚠ 信封透明区正在吃点击（本该放行却没人放行）：" + detail;
        }
        catch (Exception ex)
        {
            note = $"DescribeWheelHit异常={ex.GetType().Name}";
        }

        return true;
    }

    /// <summary>
    /// 点是否在窗口客户区内。标题栏 / 边框 / 系统菜单属非客户区，由系统自己处理，
    /// **不产生任何托管指针事件**——不排掉它们，用户每拖一次窗口就会误报一次。
    /// </summary>
    private static bool IsPointInClientArea(IntPtr hwnd, int x, int y)
    {
        try
        {
            if (!NativeMethods.GetClientRect(hwnd, out var rc)) return false;
            // 最小化时客户区为全 0，天然被下面的范围判断排除
            if (rc.Width <= 0 || rc.Height <= 0) return false;

            var origin = new NativeMethods.POINT_INT(0, 0);
            if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return false;

            return x >= origin.X && x < origin.X + rc.Width
                && y >= origin.Y && y < origin.Y + rc.Height;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>给还没挂过监听的窗口补挂左键监听（UI 线程执行）。</summary>
    private static void EnsureObservers()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

                var windows = desktop.Windows;
                for (int i = 0; i < windows.Count; i++)
                {
                    var w = windows[i];
                    if (IsTracked(w)) continue;
                    // Tunnel + handledEventsToo：抢在窗口自身处理之前记账，且即便某窗把事件标了 handled 也要能看见
                    w.AddHandler(InputElement.PointerPressedEvent, PressObserver, RoutingStrategies.Tunnel, handledEventsToo: true);
                    Observed.Add(w);
                }

                // 已关闭的窗口从表里摘掉，避免长期运行下无限增长（已关闭的窗口再挂监听无意义）
                for (int i = Observed.Count - 1; i >= 0; i--)
                {
                    if (!ContainsWindow(windows, Observed[i])) Observed.RemoveAt(i);
                }

                PublishObservedHwnds();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LeftClickSentinel.EnsureObservers 异常: " + ex.Message);
            }
        });
    }

    private static bool IsTracked(Window w)
    {
        for (int i = 0; i < Observed.Count; i++)
        {
            if (ReferenceEquals(Observed[i], w)) return true;
        }
        return false;
    }

    private static bool ContainsWindow(IReadOnlyList<Window> windows, Window w)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            if (ReferenceEquals(windows[i], w)) return true;
        }
        return false;
    }

    /// <summary>
    /// 把被监听窗口发布成快照供哨兵线程比对。
    /// 取不到句柄的窗口（平台窗口尚未创建）不进快照——于是"点它"也不会被判定，
    /// 因为没有投递监听就没有判据，宁可不报。
    /// </summary>
    private static void PublishObservedHwnds()
    {
        var list = new List<ObservedWindow>(Observed.Count);
        for (int i = 0; i < Observed.Count; i++)
        {
            IntPtr h = IntPtr.Zero;
            try { h = Observed[i].TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
            catch { /* 取不到就当它不参与判定 */ }
            if (h != IntPtr.Zero) list.Add(new ObservedWindow { Hwnd = h, Window = Observed[i] });
        }
        _observed = list.ToArray();
    }

    private static void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            Volatile.Write(ref _lastPressTick, Environment.TickCount64);
        }
        catch
        {
            // 监听失败不影响任何交互
        }
    }

    /// <summary>命中：写现场报告 + 记一条 Warn。</summary>
    private static void Report(Pending p)
    {
        lock (Gate)
        {
            if (_reportCount >= MaxReportsPerSession) return;
            if (_lastReportTick != 0 && Environment.TickCount64 - _lastReportTick < MinReportIntervalMs) return;
            _lastReportTick = Environment.TickCount64;
            _reportCount++;
        }

        DateTime now = DateTime.Now;
        try
        {
            string text = BuildReport(now, p);
            string name = "yusnip-leftclick-" + now.ToString("yyyyMMdd-HHmmss") + ".txt";
            string path = WriteReport(name, text);
            AppLogger.Warn("LeftClickSentinel: 命中「左键在到达本进程前被吞」——现场报告=" + path);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LeftClickSentinel: 写现场报告失败: ", ex);
        }
    }

    /// <summary>写报告：严格禁止写入桌面，仅在调试日志开启时写入 log 目录，并记录 AppLogger.Warn</summary>
    private static string WriteReport(string name, string text)
    {
        if (!AppLogger.FileLoggingEnabled)
        {
            return "仅内存日志（Release 未启用文件落盘）";
        }

        try
        {
            string logDir = AppLogger.LogDirectory;
            if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            {
                string p = Path.Combine(logDir, name);
                File.WriteAllText(p, text, new UTF8Encoding(true));
                return p;
            }
        }
        catch
        {
            // 写入失败忽略
        }

        return "写入失败";
    }

    private static string BuildReport(DateTime now, Pending p)
    {
        int cursorX = p.X, cursorY = p.Y;
        IntPtr hitHwnd = p.Hwnd;

        var sb = new StringBuilder(4096);
        sb.AppendLine("=== yuSnip 左键失效现场（应用内哨兵） " + now.ToString("yyyyMMdd-HHmmss") + " ===");
        sb.AppendLine("判定：物理左键已按下、光标正压在本进程某个被监听的窗口客户区上，但本进程 " + DeliverGraceMs
            + "ms 内没有收到这次按下 → 左键在**到达本进程之前**就被吃掉了。");
        // ⚠ 必须打墙钟：首轮回传的报告曾把 Environment.TickCount64（系统运行毫秒）当 Unix 毫秒格式化，
        // 印出来是 1970-01-01 的 09:24:12，与日志时间对不上，等于白采（2026-09-17 修）。
        sb.AppendLine("      按下采样时刻=" + p.Wall.ToString("HH:mm:ss.fff")
            + "（本报告写于 " + now.ToString("HH:mm:ss.fff")
            + "，相隔 " + (int)(now - p.Wall).TotalMilliseconds + "ms）");
        sb.AppendLine();

        var vScreen = MonitorHelper.GetVirtualScreenRect();
        long screenArea = Math.Max(1L, (long)vScreen.Width * vScreen.Height);

        sb.AppendLine("--- 0. 进程 ---");
        sb.AppendLine("    PID=" + Environment.ProcessId
            + "  运行时长=" + FormatUptime()
            + "  文件日志=" + (AppLogger.FileLoggingEnabled ? "开(Debug)" : "关(Release，日志只在内存)"));
        sb.AppendLine();

        sb.AppendLine("--- 1. 触发点 ---");
        sb.AppendLine("    光标=(" + cursorX + "," + cursorY + ")");
        AppendWindowLine(sb, "    光标下窗口: ", hitHwnd, cursorX, cursorY, screenArea);
        AppendWindowLine(sb, "    前台窗口:   ", NativeMethods.GetForegroundWindow(), cursorX, cursorY, screenArea);
        sb.AppendLine("    鼠标捕获:   " + DescribeCapture());
        sb.AppendLine("    距上次系统输入=" + (p.SinceInput < 0 ? "(读不到)" : p.SinceInput + "ms") + "（武装那一刻的读数）");
        if (!string.IsNullOrEmpty(p.Note))
        {
            sb.AppendLine("    命中点实况: " + p.Note);
        }
        sb.AppendLine();

        sb.AppendLine("--- 2. 本进程全部顶层窗（含被隐藏但仍存活的） ---");
        var mine = EnumOwnTopLevelWindows();
        if (mine.Count == 0)
        {
            sb.AppendLine("    （无）");
        }
        else
        {
            for (int i = 0; i < mine.Count; i++)
            {
                IntPtr h = mine[i];
                var r = GetRectOrEmpty(h);
                bool visible = NativeMethods.IsWindowVisible(h);
                bool enabled = NativeMethods.IsWindowEnabled(h);
                bool cloaked = IsCloaked(h);
                long ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
                int cover = (int)Math.Round(100.0 * Math.Max(0, r.Width) * Math.Max(0, r.Height) / screenArea, MidpointRounding.AwayFromZero);
                bool containsCursor = cursorX >= r.Left && cursorX < r.Right && cursorY >= r.Top && cursorY < r.Bottom;

                sb.AppendLine("    [" + (i + 1) + "] HWND=0x" + h.ToInt64().ToString("X")
                    + " 类名='" + GetClassNameOf(h) + "' 标题='" + GetTitleOf(h) + "'"
                    + " 可见=" + (visible ? "是" : "否") + " 启用=" + (enabled ? "是" : "否")
                    + " cloaked=" + (cloaked ? "是" : "否")
                    + " 占屏=" + cover + "% 矩形=(" + r.Left + "," + r.Top + ")" + r.Width + "x" + r.Height
                    + " 扩展[" + DescribeExStyle(ex) + "]"
                    + (containsCursor ? " ← 含光标点" : ""));
            }
        }
        sb.AppendLine();

        sb.AppendLine("--- 3. 应用自身状态 ---");
        sb.AppendLine("    被监听窗口=" + _observed.Length + " 个"
            + "   穿透态贴图数=" + Windows.StickerWindow.PenetrationCount
            + "   全局鼠标钩子=" + (Windows.StickerWindow.MouseHookInstalled ? "已挂" : "未挂"));
        sb.AppendLine("    UI 线程=" + (ProbeUiThread() ? "响应" : "无响应（超过 " + UiProbeTimeoutMs + "ms 未回话）"));
        sb.AppendLine("    HKCU\\Control Panel\\Desktop\\LowLevelHooksTimeout=" + ReadHooksTimeout());
        sb.AppendLine("    远程环境: " + DescribeRemoteEnvironment());
        sb.AppendLine();

        sb.AppendLine("--- 4. 最近 " + TailLogLines + " 条应用日志（内存环形缓冲，Release 版不落盘） ---");
        var logs = AppLogger.GetSnapshot();
        int from = Math.Max(0, logs.Length - TailLogLines);
        for (int i = from; i < logs.Length; i++)
        {
            sb.AppendLine("    " + logs[i].Time.ToString("HH:mm:ss.fff") + " [" + logs[i].Level + "] " + logs[i].Message);
        }
        sb.AppendLine();

        sb.AppendLine("--- 5. 判读 ---");
        sb.AppendLine("    ⓪ 先看第 1 节「命中点实况」：若它写着「⚠ 信封透明区正在吃点击」⇒ 就是本软件的问题，");
        sb.AppendLine("       贴图信封（近整屏）本该把图片+阴影之外的点击放给下层窗口，此刻却没人放行；");
        sb.AppendLine("       若写着「信封态=否 / 在命中矩形内=是」⇒ 这次点击本该由本窗收到，继续往下判。");
        sb.AppendLine("    ① 第 2 节里若存在「可见 + 含光标点 + 未打 WS_EX_TRANSPARENT」的本进程大窗 ⇒ 属 A 类，");
        sb.AppendLine("       本软件某窗在挡点击（窗口编号与矩形已在上面给出）；");
        sb.AppendLine("    ② 第 2 节里没有这样的窗口、而本次判定成立 ⇒ 本次点击在进入本进程前就没了，");
        sb.AppendLine("       优先怀疑**第三方全局低级鼠标钩子**（链上任一环 return 非 0 即可吞掉左键），");
        sb.AppendLine("       或第三方全屏/置顶窗。此类问题本软件无法规避，只能靠对比排查（逐条退出可疑常驻软件复现）；");
        sb.AppendLine("    ③ 若第 3 节 UI 线程=无响应 ⇒ 属 B 类（界面线程卡住），此时全系统输入都可能被拖慢。");
        sb.AppendLine("       注意：模态循环也会让 UI 线程停止派发——Ctrl+左键拖拽导出（OLE DoDragDrop）、拖窗口、");
        sb.AppendLine("       拖边框调整大小期间「无响应」是正常的，不能据此判 B 类。");
        sb.AppendLine("    ④ 第 3 节「远程环境」列出疑似远控进程时，把它当**线索而非结论**：远控软件自己注入输入");
        sb.AppendLine("       （SendInput/mouse_event）与真实手点在本哨兵眼里完全一样，本进程内无法分辨。");
        sb.AppendLine("       要坐实只能对比：断开/退出远控后按同一操作路径复现。");
        sb.AppendLine();
        sb.AppendLine("盲区（本哨兵刻意不判的场景，别据此下「没问题」的结论）：");
        sb.AppendLine("    · 点击落在**别的进程**的窗口上：我们无从知道那次点击本该被谁收到；");
        sb.AppendLine("    · 点击落在**本进程的弹窗**（右键菜单、下拉框等独立顶层）上：那里没有投递监听；");
        sb.AppendLine("    · 点击落在本进程窗口的**非客户区**（标题栏、边框、系统菜单）上：系统自己处理，不产生托管事件。");
        sb.AppendLine("    · 贴图**信封态**下图片+阴影之外的点击：那是**故意**放给下层窗口的（窗口对那里返回 HTTRANSPARENT），");
        sb.AppendLine("      故不判命中；但「穿透没挂上」这一种会被判并在第 1 节实况里显著标注（判据③b）。");
        sb.AppendLine("    · 远控软件自己注入的输入：与真实手点在进程内完全同形，分辨不了。");
        sb.AppendLine();
        sb.AppendLine("说明：本文件由 yuSnip 检测到「左键被吞」时自动生成（全程只读、不移动鼠标），请发回给开发者。");

        return sb.ToString();
    }

    private static void AppendWindowLine(StringBuilder sb, string prefix, IntPtr hwnd, int cursorX, int cursorY, long screenArea)
    {
        if (hwnd == IntPtr.Zero)
        {
            sb.AppendLine(prefix + "（无）");
            return;
        }

        uint pid = 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        var r = GetRectOrEmpty(hwnd);
        int cover = (int)Math.Round(100.0 * Math.Max(0, r.Width) * Math.Max(0, r.Height) / screenArea, MidpointRounding.AwayFromZero);
        sb.AppendLine(prefix + "HWND=0x" + hwnd.ToInt64().ToString("X")
            + " 类名='" + GetClassNameOf(hwnd) + "'"
            + " 标题='" + GetTitleOf(hwnd) + "'"
            + " PID=" + pid + (pid == (uint)Environment.ProcessId ? "(本进程)" : "")
            + " 占屏=" + cover + "% 矩形=(" + r.Left + "," + r.Top + ")" + r.Width + "x" + r.Height);
    }

    /// <summary>
    /// 当前捕获了鼠标的窗口（没有则"无"）。捕获期间鼠标消息**全部**送给捕获窗口，光标下的窗口
    /// 一个都收不到——这正是"点在本窗上却没进来"的经典机制之一，而 `WindowFromPoint` 完全看不出来
    /// （它照样返回光标下的窗）。2026-09-17 为现场报告补。
    /// ⚠ 拖动、拖边框、Ctrl+左键拖拽导出期间本进程自己会持捕获，那时这里是**正常现象**，不是真凶。
    /// </summary>
    private static string DescribeCapture()
    {
        try
        {
            IntPtr cap = NativeMethods.GetCapture();
            if (cap == IntPtr.Zero) return "无（鼠标未被任何窗口捕获）";
            uint pid = 0;
            NativeMethods.GetWindowThreadProcessId(cap, out pid);
            return "HWND=0x" + cap.ToInt64().ToString("X")
                + " 类名='" + GetClassNameOf(cap) + "' 标题='" + GetTitleOf(cap) + "'"
                + " PID=" + pid + (pid == (uint)Environment.ProcessId ? "(本进程)" : "（**别的进程**）");
        }
        catch
        {
            return "(读不到)";
        }
    }

    private static List<IntPtr> EnumOwnTopLevelWindows()
    {
        var list = new List<IntPtr>(16);
        _enumSink = list;
        try
        {
            // EnumWindows 按 Z 序自顶向下回调，报告里的编号即前后层级顺序
            NativeMethods.EnumWindows(EnumOwnProc, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LeftClickSentinel: 枚举本进程窗口失败: " + ex.Message);
        }
        finally
        {
            _enumSink = null;
        }
        return list;
    }

    private static bool OnEnumOwnWindow(IntPtr hwnd, IntPtr lParam)
    {
        uint pid = 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == (uint)Environment.ProcessId) _enumSink?.Add(hwnd);
        return true;
    }

    private static NativeMethods.RECT GetRectOrEmpty(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r)) return r;
        return default;
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static string GetTitleOf(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return "?"; }
    }

    /// <summary>DWM cloaked：UWP 后台窗、被 DWM 隐藏的窗都靠这一位与"真的可见"区分（样式位看不出来）。</summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DWMWA_CLOAKED, out int v, sizeof(int)) == 0 && v != 0;
        }
        catch { return false; }
    }

    private static string DescribeExStyle(long ex)
    {
        var sb = new StringBuilder();
        if ((ex & NativeMethods.WS_EX_TOPMOST) != 0) sb.Append("TOPMOST ");
        if ((ex & NativeMethods.WS_EX_TRANSPARENT) != 0) sb.Append("TRANSPARENT(穿透) ");
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) sb.Append("TOOLWINDOW ");
        if ((ex & NativeMethods.WS_EX_LAYERED) != 0) sb.Append("LAYERED ");
        if ((ex & NativeMethods.WS_EX_NOACTIVATE) != 0) sb.Append("NOACTIVATE ");
        if (sb.Length > 0) sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// UI 线程探活：卡住时（B 类）这一项本身就是判据，故不能靠 UI 线程自己去算。
    /// 用「投递一个最高优先级回调 + 等事件」而不是 DispatcherOperation.Wait：后者在
    /// Avalonia 11.2 的 Wait(TimeSpan) 不返回结果，超时与否只能靠异常分辨，不如直接等信号清楚。
    /// </summary>
    private static bool ProbeUiThread()
    {
        try
        {
            using var done = new ManualResetEventSlim(false);
            Dispatcher.UIThread.Post(() => done.Set(), DispatcherPriority.Send);
            return done.Wait(TimeSpan.FromMilliseconds(UiProbeTimeoutMs));
        }
        catch
        {
            return false;
        }
    }

    private static string ReadHooksTimeout()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            object? v = key?.GetValue("LowLevelHooksTimeout");
            return v == null ? "(未设置，取系统默认)" : v + " ms";
        }
        catch
        {
            return "(读不到)";
        }
    }

    /// <summary>
    /// 疑似远控/输入注入类软件的进程名片段（用户 2026-09-17 提出"像是和 VNC 冲突"，故把这条线索
    /// 一并采下来）。**命中不等于有问题**，只是把"现场有没有这类常驻件"变成可判读的事实。
    /// 匹配用 `Contains`：厂商进程名普遍带前后缀（`tvnserver`、`winvnc4`、`ToDesk_Service`…）。
    /// </summary>
    private static readonly string[] RemoteControlNames =
    {
        "winvnc", "vncserver", "vncviewer", "tvnserver", "tvncserver", "uvnc", "vnc",
        "rustdesk", "anydesk", "teamviewer", "todesk", "sunlogin", "oray", "aweray",
        "parsec", "nomachine", "nxnode", "mstsc", "rdpclip", "splashtop", "screenconnect",
        "gotohttp", "logmein", "ammyy", "radmin", "ultraviewer", "dwservice", "netsupport",
    };

    /// <summary>
    /// 远程环境线索：是否为终端服务会话（`SM_REMOTESESSION`）＋ 现场有没有疑似远控进程。
    /// ⚠ `SM_REMOTESESSION` 只对 RDP/终端服务为真；**VNC 类软件以服务方式往本地会话里灌事件，
    /// 不会置这一位**，故它为 0 不能排除 VNC——真正的线索是下面那份进程扫描。
    /// </summary>
    private static string DescribeRemoteEnvironment()
    {
        var sb = new StringBuilder(256);
        try
        {
            sb.Append("远程会话(SM_REMOTESESSION)=")
              .Append(NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0 ? "是" : "否");
        }
        catch
        {
            sb.Append("远程会话(SM_REMOTESESSION)=(读不到)");
        }

        sb.Append("  疑似远控进程: ");
        var hits = new List<string>(4);
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    string name = proc.ProcessName.ToLowerInvariant();
                    for (int i = 0; i < RemoteControlNames.Length; i++)
                    {
                        if (name.Contains(RemoteControlNames[i]))
                        {
                            hits.Add(proc.ProcessName + "(PID " + proc.Id + ")");
                            break;
                        }
                    }
                }
                catch
                {
                    // 个别进程读不到名字（权限/已退出）属常态，跳过
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // 枚举失败就如实说"没扫到"，不假装干净
        }

        sb.Append(hits.Count == 0 ? "未发现" : string.Join("、", hits));
        return sb.ToString();
    }

    private static string FormatUptime()
    {
        try
        {
            var span = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            return (int)span.TotalHours + "h" + span.Minutes + "m";
        }
        catch
        {
            return "?";
        }
    }
}
