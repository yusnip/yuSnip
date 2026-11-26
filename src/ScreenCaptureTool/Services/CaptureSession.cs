using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.AutoDetect;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Core.Matting;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.LongScroll;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Windows;
using ScreenCaptureTool.Controls;
using System.Text;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 截图会话协调器：把 Core 业务逻辑与 Avalonia UI 串起来。
///
/// T4.7-1：自动检测高亮 → 点击高亮区保存（最小版本）。
/// T4.7-2（本版）：完整鼠标交互 = 拖框创建 / 拖手柄缩放 / 拖选区平移 + 双击或 Enter 确认。
///
/// 协调策略：
/// - 用户进行手动操作前，AutoDetectController 主导 SelectionLayer.Region；点击高亮区直接确认（T4.7-1 行为）
/// - 用户开始拖动后，<see cref="SelectionState"/> 接管，自动检测的事件被忽略，直到 Esc/确认结束本会话
/// - 单击 vs 拖动用 5px 阈值区分
///
/// 一次会话一个实例，不复用。
/// </summary>
public sealed class CaptureSessionResult
{
    public static CaptureSessionResult Saved(string path) => new CaptureSessionResult(path, false, null, false, null, false);

    public static CaptureSessionResult CopiedToClipboard() => new CaptureSessionResult(null, false, null, false, null, true);

    public static CaptureSessionResult StickerCreated() => new CaptureSessionResult(null, true, null, false, null, false);

    public static CaptureSessionResult ColorPicked(string hex) => new CaptureSessionResult(null, false, hex, false, null, false);

    public static CaptureSessionResult LongScroll(string? message, bool createdSticker = false) => new CaptureSessionResult(null, createdSticker, null, true, message, false);

    public static CaptureSessionResult Record(string? message) => new CaptureSessionResult(null, false, null, false, message, false, record: true);

    private CaptureSessionResult(string? savedPath, bool createdSticker, string? pickedColorHex, bool longScroll, string? message, bool clipboardCopied, bool record = false)
    {
        SavedPath = savedPath;
        CreatedSticker = createdSticker;
        PickedColorHex = pickedColorHex;
        IsLongScroll = longScroll;
        Message = message;
        ClipboardCopied = clipboardCopied;
        IsRecord = record;
    }

    public string? SavedPath { get; }

    public bool CreatedSticker { get; }

    public string? PickedColorHex { get; }

    public bool IsLongScroll { get; }

    public bool IsRecord { get; }

    public string? Message { get; }

    public bool ClipboardCopied { get; }

    public bool PickedColor => !string.IsNullOrWhiteSpace(PickedColorHex);
}

public sealed class CaptureSession
{
    /// <summary>"单击 vs 拖动"判定阈值（屏幕物理像素）。低于该阈值的小抖动按单击处理。</summary>
    internal const int DragThresholdPx = 5;

    /// <summary>
    /// 落笔期间"写文档＋整层重绘"的合帧闸门（毫秒）。一次鼠标采样很便宜，一次整层重绘很贵，
    /// 压到约 60 次/秒既不迟滞也不会把 UI 线程压满。计时必须用 <see cref="GetPreciseTimeMs"/>：
    /// <c>Environment.TickCount64</c> 在本机步进 15.6 毫秒，判"小于 16 就跳过"会被量化成两格（≈32 次/秒）。
    /// 配套红线：被闸门压住的那一次要在收笔时补落，否则洞会缺尾巴。
    /// </summary>
    internal const double PreviewFrameGateMs = 16.0;

    /// <summary>擦除合帧闸门自适应放宽的上限（毫秒，约 12 次/秒）。只能按"上一次整层重绘耗时"放宽：
    /// 4K 上一次重绘实测几十到两百多毫秒，放宽到重绘耗时的 1.6 倍再封顶，才留得出处理鼠标事件的空闲。
    /// 再放宽就成"点一下才见洞"了，这条线是手感底线。</summary>
    internal const double EraserPublishGateMaxMs = 80.0;

    /// <summary>
    /// 取证埋点（2026-10-06 定位"橡皮擦画笔涂抹仍卡"）：一次手势结束打一行汇总，日志量＝每手势一行。
    /// 读数是"发布耗时 / 整层重绘耗时 / 其中洞几何耗时 / 落点峰值 / 采样次数"，用来判病灶在哪一段——
    /// 拿到结论后把本常量置 false，运行时不再产生任何计时代价（配套的 AnnotationLayer 计数器同理）。
    /// </summary>
    internal const bool EraserProbe = true;
    private int probeErasePublishes;
    private int probeEraseMarksPeak;
    private int probeEraseEvents;
    private double probeErasePublishMs;
    private double probeErasePublishMaxMs;
    private double probeEraseGestureStartMs;

    /// <summary>HitTestHandle 容差半径（屏幕物理像素）。沿用旧代码经验值。</summary>
    private const int HandleHitRadiusPx = 10;

    /// <summary>选区边框缩放命中范围（屏幕物理像素）。</summary>
    private const int BorderGripPx = 10;

    private static readonly double StopwatchToMs = 1000.0 / Stopwatch.Frequency;
    private static double GetPreciseTimeMs() => Stopwatch.GetTimestamp() * StopwatchToMs;

    private static ScreenCaptureTool.Core.Capture.PaddleOcrService? _ocrService;
    private static readonly object _ocrServiceLock = new object();
    /// <summary>共享 OCR 实例最近一次被请求的时间（UTC）。空闲回收据此判定。</summary>
    private static DateTime _ocrLastUseUtc = DateTime.MinValue;

    /// <summary>
    /// 获取全局共享的 OCR 服务单例：首次调用才初始化（懒加载），10 分钟未使用会被
    /// <see cref="TryEvictIdleOcr"/> 回收，之后再次调用时自动重建。
    /// 所有 OCR 调用（截图识别、贴图识别、文本选择）必须复用此实例。
    /// 严禁各自 new：一个 PaddleOcrService 实例对应一对 det+rec ONNX 推理会话，
    /// 频繁创建/销毁会重复加载 30MB 模型，严重影响性能。
    /// </summary>
    public static ScreenCaptureTool.Core.Capture.PaddleOcrService GetSharedOcrService()
    {
        // 模型改按需下载后，这里是所有 OCR 入口（截图识别、贴图取字、历史图、后台补识别）唯一的公共咽喉：
        // 缺件就把居中进度卡开出来并把下载排上队。已 await 过闸口的路径不会走到这（此时必已就绪），
        // 其余同步入口本次仍会抛「未就绪」那句人话，但下一次点就能用。
        if (!Core.Deps.DependencyManager.IsReady(Core.Deps.DepIds.Ocr))
            DependencyGate.KickOff(Core.Deps.DepIds.Ocr);

        lock (_ocrServiceLock)
        {
            _ocrLastUseUtc = DateTime.UtcNow;
            _ocrService ??= new ScreenCaptureTool.Core.Capture.PaddleOcrService();
            return _ocrService;
        }
    }

    /// <summary>
    /// 共享 OCR 实例最近一次被请求的时刻（UTC）。空闲补识别据此判断"用户是不是正在用识别"并让路
    /// —— 推理整段持有 <c>PaddleOcrService</c> 内部锁，抢进去会让前台识别多等 0.3–1.2 秒。
    /// </summary>
    public static DateTime LastOcrRequestUtc
    {
        get { lock (_ocrServiceLock) return _ocrLastUseUtc; }
    }

    /// <summary>应用退出时释放共享 OCR 进程。</summary>
    public static void ShutdownSharedOcr()
    {
        lock (_ocrServiceLock)
        {
            try { _ocrService?.Dispose(); } catch { }
            _ocrService = null;
            _ocrLastUseUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 空闲回收：若共享 OCR 实例存在且距最近一次使用超过 idleTimeout，摘除实例并在
    /// 后台线程释放（Dispose ONNX 会话是原生资源释放，不在锁内做）。
    /// 返回 true 表示本次确实发生了回收，调用方应随后做一次温和 GC 归还物理内存。
    /// </summary>
    public static bool TryEvictIdleOcr(TimeSpan idleTimeout)
    {
        ScreenCaptureTool.Core.Capture.PaddleOcrService? victim;
        lock (_ocrServiceLock)
        {
            if (_ocrService == null) return false;
            if (DateTime.UtcNow - _ocrLastUseUtc < idleTimeout) return false;

            victim = _ocrService;
            _ocrService = null;
            _ocrLastUseUtc = DateTime.MinValue;
        }

        Task.Run(() =>
        {
            try { victim.Dispose(); } catch { }
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("OCR 引擎空闲超过 10 分钟，ONNX 会话已释放（下次使用时重建）");
        });
        return true;
    }

    private readonly AppSettings _settings;
    private readonly IScreenCapturer _capturer;

    /// <summary>
    /// 当前正在运行的截图窗口引用（RunAsync 期间非空，结束后置空）。
    /// 供全局热键（快速贴图 F3）在截图进行中触发"选区转贴图"。
    /// </summary>
    private CaptureWindow? _activeWindow;
    private volatile bool _recordToolSelected;
    private volatile bool _recordStarting;
    private bool _recordPreparing;
    private IntPtr _recordShortcutWindow;
    private Action? _startRecordFromSelection;
    private Action? _detachRecordShortcut;
    private BuiltinShortcutsStore.BuiltinShortcut? _recordShortcutBinding;
    private IDisposable? _recordShortcutRegistration;
    private bool _recordShortcutSuspended;
    // 注册入口可替换，离线自测核对会话生命周期而不占用真实系统按键。
    internal Func<Action, KeyModifiers, Key, IDisposable?> RecordingShortcutRegistrar = RegisterRecordingShortcut;

    internal bool CanHandleCaptureRecordingShortcut => CanHandleRecordingShortcut
        && (_recordShortcutBinding == null || _recordShortcutBinding.Key == Key.None);

    internal void SetRecordingShortcutSuspended(bool suspended)
    {
        _recordShortcutSuspended = suspended;
        UpdateRecordingShortcutRegistration();
    }

    private void UpdateRecordingShortcutRegistration()
    {
        if (!CanHandleRecordingShortcut || _recordShortcutSuspended || _recordShortcutBinding?.Key is null or Key.None)
        {
            _recordShortcutRegistration?.Dispose();
            _recordShortcutRegistration = null;
            return;
        }
        _recordShortcutRegistration ??= RecordingShortcutRegistrar(() => Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current is App app && app.GlobalHotkeysSuspended) return;
            if (!_recordShortcutSuspended && CanHandleRecordingShortcut) TryHandleRecordingShortcut();
        }, DispatcherPriority.Input), _recordShortcutBinding.Modifiers, _recordShortcutBinding.Key);
    }

    private static IDisposable? RegisterRecordingShortcut(Action callback, KeyModifiers modifiers, Key key)
    {
        var manager = new ScreenCaptureTool.Core.HotKeyManager(callback, 0x5954);
        try
        {
            if (manager.Register(BuiltinShortcutsStore.WinModifiersOf(modifiers), BuiltinShortcutsStore.VirtualKeyOf(key)))
                return manager;
            AppLogger.Warn("CaptureSession: 快速启停录屏快捷键注册失败，错误码=" + manager.LastRegisterError);
        }
        catch (Exception ex) { AppLogger.Error("CaptureSession: 快速启停录屏快捷键注册失败", ex); }
        manager.Dispose();
        return null;
    }

    // 钩子线程只读取布尔快照；工具状态与实际执行仍在 UI 线程处理。
    internal bool CanHandleRecordingShortcut => _recordToolSelected || _recordStarting;

    internal bool CanHandleRecordingShortcutInWindow(IntPtr window) => _recordStarting
        || (_recordToolSelected && window != IntPtr.Zero
            && window == System.Threading.Volatile.Read(ref _recordShortcutWindow));

    internal bool TryHandleRecordingShortcut()
    {
        if (_recordPreparing) return true;
        // 真正开录后截图窗口已关闭，会话仍等待成片；只控制本会话启动的录屏。
        if (_recordStarting)
        {
            if (RecordFlow.IsBusy) RecordFlow.RequestComplete();
            return true;
        }
        if (!HasActiveSelectionWindow || _activeWindow!.ActiveToolKey != "Record"
            || _startRecordFromSelection == null) return false;
        _startRecordFromSelection();
        return true;
    }
    private CaptureSelectionPath? _selectionPath;

    /// <summary>当前是否有一个处于交互中的截图窗口（截图会话运行中、选区可被操作）。</summary>
    public bool HasActiveSelectionWindow => _activeWindow != null && _activeWindow.IsVisible;

    /// <summary>脚本指定的高级动作，如 "Copy", "Save", "Pin", "Ocr"</summary>
    public string? ScriptAction { get; set; }

    /// <summary>选区已交给识别或翻译等后台处理；此时空返回值不代表用户取消。</summary>
    public bool OutputHandedOff { get; private set; }

    /// <summary>组合动作接收本次截图图片的所有权和含描边的屏幕位置，不通过系统剪贴板传递。</summary>
    public Action<Bitmap, Rectangle>? CapturedImageSink { get; set; }

    internal bool SuppressCaptureStroke { get; init; }

    /// <summary>全局鼠标入口传入的原始拖动，使用屏幕物理坐标。</summary>
    public MouseActionGesture? MouseGesture { get; set; }
    private Action? _refreshMouseGesture;
    private Action? _detachMouseGesture;

    /// <summary>
    /// 请求把当前截图选区转化为贴图（等价于点击工具栏"贴图"按钮）。
    /// 仅在 <see cref="HasActiveSelectionWindow"/> 为真时有意义；若无选区，
    /// CaptureWindow 的 StickerRequested 链路本身不会产生贴图（由 CompleteCurrentSelection 兜底）。
    /// </summary>
    public void RequestStickerFromCurrentSelection()
    {
        _activeWindow?.RaiseStickerRequested();
    }

    /// <summary>
    /// 请求把当前截图选区进行智能抠图并生成贴图（等价于点击工具栏"智能抠图"按钮）。
    /// 仅在 <see cref="HasActiveSelectionWindow"/> 为真时有意义。
    /// </summary>
    public void RequestMattingFromCurrentSelection()
    {
        _activeWindow?.RaiseMattingRequested();
    }

    private readonly Action<string> _writeColorClipboard;

    public CaptureSession(AppSettings settings, IScreenCapturer? capturer = null)
        : this(settings, capturer, ClipboardService.SetText) { }

    // 离线交互回归替换颜色输出，避免测试覆盖用户剪贴板。
    internal CaptureSession(AppSettings settings, IScreenCapturer? capturer, Action<string> writeColorClipboard)
    {
        _writeColorClipboard = writeColorClipboard;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _capturer = capturer ?? new ScreenCapturer();
        // 每次会话一份游标归零：让第一下 R 一定是"上一次截图成功的那个框"，而不是接着上一会话退到的位置往下走。
        Core.Capture.SelectionHistory.ResetCursor();
    }

    /// <summary>
    /// 跑完一次完整截图流程。返回保存到的文件路径；用户取消返回 null；失败抛异常。
    /// 必须在 UI 线程上调（创建 Avalonia Window）。
    /// </summary>
    public async Task<CaptureSessionResult?> RunAsync()
    {
        if (MouseGesture?.Read().Cancelled == true) return null;
        // 录屏进行中不允许叠加一次截图会话：两套全屏覆盖层互踩（录屏遮罩是穿透式，截图遮罩不是）
        if (RecordFlow.IsBusy)
        {
            StickerToastWindow.Show("录屏进行中，请先结束录制");
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 录屏会话占用中，忽略截图请求");
            return null;
        }

        // 🔴 性能埋点：用户感知的「截图启动响应慢」＝热键按下到遮罩首帧出现这一段。
        // 下面各阶段一律以本会话起点为基准记录相对耗时，用于区分到底是截屏、建窗、
        // UIA 元素预热还是 Show 本身在拖。纯只读打点，不改变任何执行顺序、并发关系与语义。
        StickerToastWindow.ClosePendingAtTop(); // 先撤旧 OCR 等待提示，再抓取下一张屏幕。
        double sessionStartMs = GetPreciseTimeMs();
        long sessionStartTimestamp = Stopwatch.GetTimestamp();
        string At() => "+" + (GetPreciseTimeMs() - sessionStartMs).ToString("0.#") + "ms";
        // 全局拖框已经明确选区起点，无需先吸附其它程序的窗口或元素。
        // 普通截图仍在 Show 前预热 UIA，保留 QQ 等应用的无障碍树快照。
        bool startsWithMouseDrag = MouseGesture != null;

        CapturedFrame? frame = null;
        CaptureWindow? window = null;
        AutoDetectController? autoDetect = null;
        var annotations = new AnnotationDocument();
        var tcs = new TaskCompletionSource<CaptureCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // ── 两条自愈兜底（与具体成因无关，只封住最坏后果）────────────────────────
        // 症状：多次截图后鼠标左键在全盘范围内点不动，只有结束进程才恢复。能同时满足"全盘"与
        // "持续到杀进程"的形态只有一种——一张覆盖整屏、置顶、却没有画面可看的遮罩窗还留在屏幕上
        // （帧位图在会话收尾时已被释放，窗口本身没关掉）。下面两条看门狗保证这种状态最多活几秒。
        // 关窗同时是经 `window.Closed → tcs.TrySetResult(null)`（见 WireUpInteractions 末段）把
        // 挂死的 await 一并放行，会话因此还能正常走完 finally，不会留下第二张残留窗。
        // ⚠ 两者都靠 UI 线程的消息泵驱动：若现场是"UI 线程本身彻底挂死"，它们同样跑不动——
        // 那种成因要用 02_构建工具/诊断 下的点击探针在现场区分，不在本兜底的能力范围内。
        bool teardownStarted = false;

        // 阶段一（还在等用户操作）：撤幕只由某条完成路径发起，它紧接着就该落定会话或把遮罩放回去。
        // 撤幕后还悬着 5 秒 = 该路径漏了收尾，用户此刻看不见窗口也按不到 Esc。
        IDisposable maskAbandonedGuard = DispatcherTimer.Run(() =>
        {
            if (teardownStarted) return false;
            var w = window;
            if (w != null && !w.IsVisible)
            {
                DateTime? hiddenAt = w.LastMaskHiddenUtc;
                if (hiddenAt.HasValue && (DateTime.UtcNow - hiddenAt.Value).TotalSeconds >= 5)
                {
                    AppLogger.Warn("CaptureSession: 遮罩已撤但会话悬着超过 5 秒未落定，判定为漏网收尾路径，强制关窗放行");
                    try { w.Close(); } catch { }
                }
            }
            return true;
        }, TimeSpan.FromSeconds(1));

        // 阶段二（完成动作已落定，正在做裁剪/写剪贴板/落盘这一串重活）：正常远不到 20 秒。
        void ArmTeardownWatchdog()
        {
            DispatcherTimer.RunOnce(() =>
            {
                if (teardownStarted) return;
                AppLogger.Warn("CaptureSession: 完成动作落定后 20 秒未回到会话出口，强制撤除遮罩窗口");
                try { window?.Hide(); } catch { }
                try { window?.Close(); } catch { }
            }, TimeSpan.FromSeconds(20));
        }

        try
        {
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 并发启动屏幕捕获与 CaptureWindow 构建，入口="
                + (startsWithMouseDrag ? "全局鼠标拖框（跳过吸附预热）" : "普通截图（按检测模式预热）"));

            // 0. 先建 autoDetect：它只是持有配置字段、成本可忽略；提前构造的唯一目的是让下面 1b 的
            //    prewarmTask 能在截屏和 CaptureWindow 构造期间并行推进。「预热必须完成于 Show 之前」
            //    的 QQ 塌缩不变量由第 4 步的 WhenAny await 原样守住，await 位置与旧写法等价。
            autoDetect = new AutoDetectController();
            autoDetect.IsPaused = startsWithMouseDrag;
            autoDetect.Mode = DetectModeExtensions.FromLegacyString(_settings.AutoDetectMode);
            // 「检测元素」悬停检测参数（用户可配）
            autoDetect.ElementDwellMs = _settings.ElementDwellMs;
            autoDetect.ElementDwellDeadbandPx = _settings.ElementDwellDeadbandPx;
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 自动检测模式=" + autoDetect.Mode);

            // 1. 后台线程池并发执行 Win32 DIBSection 高速截屏与冻结位图转换
            //    实测（4K 3840x2160，2026-09-14 日志）：截屏 43~79ms，位图转换 ~10-40ms。
            //    优化：位图转换彻底移至后台线程池，与 UI 线程的 CaptureWindow 构造完全并行，
            //    UI 线程开销降至 <0.1ms 属性赋值，消除 UI 线程 33MB 内存拷贝与 LockBits 阻塞。
            var pipelineWarmup = (Application.Current as App)?.StopCapturePipelineWarmup() ?? Task.CompletedTask;
            var captureTask = Task.Run(async () =>
            {
                if (!pipelineWarmup.IsCompleted)
                {
                    using (PerformanceTimer.Measure("CaptureSession.WaitStartupCapture"))
                        await pipelineWarmup.ConfigureAwait(false);
                }
                CapturedFrame capturedFrame;
                using (PerformanceTimer.Measure("CaptureSession.CaptureScreen"))
                {
                    capturedFrame = _capturer.CaptureVirtualScreen();
                }

                Avalonia.Media.Imaging.Bitmap? avaloniaBmp = null;
                if (capturedFrame?.FullScreen != null)
                {
                    using (PerformanceTimer.Measure("CaptureSession.ConvertBitmap"))
                    {
                        avaloniaBmp = Utils.BitmapConversion.ToAvaloniaCaptureBitmap(capturedFrame);
                    }
                }

                return (Frame: capturedFrame, AvaloniaBitmap: avaloniaBmp);
            });

            // 1b. 「检测元素」模式：预热与截屏同时点火，让 UIA 子树快照的墙钟时间与下面的
            //     CaptureWindow 构造（30~70ms，UI 线程）完全重叠。PrewarmElementSnapshots 明确
            //     「可在后台线程调用」（见 AutoDetectController 注释），不读 frame、不读 window、
            //     不触碰任何 UI 线程对象，只做跨进程 COM 抓其它窗口的无障碍子树，与 ScreenCapturer
            //     的 GDI/DXGI 通路无共享态。资源画像互补：UIA 预热的墙钟主要花在等其他进程 IPC 上，
            //     与截屏的 CPU/内存带宽竞争极小，同时跑几乎零拖累。
            Task? prewarmTask = null;
            long prewarmStart = 0;
            if (!startsWithMouseDrag && autoDetect.Mode == DetectMode.Element &&
                NativeMethods.GetCursorPos(out NativeMethods.POINT prewarmCursorPt))
            {
                var prewarmPoint = MouseGesture?.Start ?? new System.Drawing.Point(prewarmCursorPt.X, prewarmCursorPt.Y);
                prewarmStart = Environment.TickCount64;
                prewarmTask = Task.Run(() => autoDetect.PrewarmElementSnapshots(prewarmPoint));
            }

            // 2. UI 线程创建与初始化 CaptureWindow，消除串行空转等待
            //    实测（2026-09-14 日志）：30~70ms。⚠ 旧注释写的「15~20ms」偏低，勿再据此判断。
            using (PerformanceTimer.Measure("CaptureSession.ConstructWindow"))
            {
                window = (Application.Current as App)?.TakePreparedCaptureWindow();
                bool usedPreparedWindow = window != null;
                if (window == null)
                {
                    using (PerformanceTimer.Measure("CaptureSession.NewWindow")) window = new CaptureWindow();
                }
                _activeWindow = window; // 暴露给全局热键（快速贴图 F3）使用
                using (PerformanceTimer.Measure("CaptureSession.TextInit")) window.InitTextToolControls();
                // 当前设置先落到控件，再绑定会话；与后台捕获重叠，Show 内不再触发整套参数初始化。
                using (PerformanceTimer.Measure("CaptureSession.AnnotationDefaults"))
                {
                    try { window.ApplyAnnotationDefaults(_settings); }
                    catch (Exception ex) { AppLogger.Warn("CaptureSession.ApplyAnnotationDefaults 失败: " + ex.Message); }
                }
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 文本工具控件初始化完成，启动预备窗口=" + usedPreparedWindow);
            }
            window.FlushConstructionDiagnostics("Session");
            window.BeginCaptureDiagnostics(sessionStartTimestamp, startsWithMouseDrag);

            // 3. 等待截屏完成（截屏 ‖ CaptureWindow 构造 ‖ UIA 预热三者并行，此处等的是截屏那一路）
            bool wasCaptureCompletedEarly = captureTask.IsCompleted;
            long awaitCaptureStart = Environment.TickCount64;
            var captureResult = await captureTask;
            long awaitCaptureCost = Environment.TickCount64 - awaitCaptureStart;
            frame = captureResult.Frame;
            var preconvertedBitmap = captureResult.AvaloniaBitmap;

            if (wasCaptureCompletedEarly)
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: [并发取证] 截屏任务在建窗期间已先行完成 (同步续行)");
                // 阻断连续长阻塞：由于 await captureTask 提前完成发生了同步续行，
                // 主动让行一拍给消息泵，让积累的鼠标/输入事件先得到处理，消除鼠标卡顿
                await Task.Yield();
            }
            else
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: [并发取证] 截屏任务中途让行等待完成，等待耗时=" + awaitCaptureCost + "ms");
            }

            if (frame != null)
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 屏幕捕获完成 " + At() + ", VirtualScreen=" + frame.VirtualScreenRect + ", Bitmap=" + frame.FullScreen.Width + "x" + frame.FullScreen.Height);
                // 预先设定物理窗口位置与尺寸，消除 Show 之后 Opened 再调整产生的重排开销与闪烁
                window.PreLayoutVirtualScreen(frame.VirtualScreenRect, frame.DpiScale);
                window.SetFrozenScreenBitmap(preconvertedBitmap ?? Utils.BitmapConversion.ToAvaloniaCaptureBitmap(frame));
                window.Annotation.SourceBitmap = frame.FullScreen;
                window.Annotation.SourceScreenRect = frame.VirtualScreenRect;
            }

            using (PerformanceTimer.Measure("CaptureSession.WireUp"))
            {
                var selection = new SelectionState();
                annotations = new AnnotationDocument();
                var annotationHistory = new AnnotationHistory(annotations);
                window.Annotation.Document = annotations;

                WireUpInteractions(window, autoDetect, selection, annotations, annotationHistory, frame, tcs);
            }

            // 4. 等待 1b 已点火的预热落定（await 仍在 Show 之前，QQ 塌缩不变量原样保留）。
            //    超时 1200ms 从此刻起算（与旧写法同一时点），但预热已提前跑了「建窗 + 等截屏」
            //    那一段时间，正常应用此刻往往已经完成或即将完成 → 超时命中率下降、开场高亮更稳。
            //    硬上限语义不变：个别应用的 UIA provider 可能长时间不响应（跨进程 COM 调用无法
            //    设超时），绝不让预热无限期拖住遮罩显示；超时后 COM 请求已在途，快照由后台线程
            //    收尾后通过 WarmupCompleted 复测生效。
            bool prewarmCompleted = false;
            if (prewarmTask != null)
            {
                bool wasPrewarmCompletedEarly = prewarmTask.IsCompleted;
                if (await Task.WhenAny(prewarmTask, Task.Delay(1200)) != prewarmTask)
                {
                    AppLogger.Warn("CaptureSession.RunAsync: 元素预热超时(>1200ms 等待上限)，先显示遮罩，快照后台继续构建");
                }
                else
                {
                    prewarmCompleted = true;
                    long prewarmCost = Environment.TickCount64 - prewarmStart;
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 元素预热完成，耗时=" + prewarmCost + "ms (提前完成=" + wasPrewarmCompletedEarly + ")");
                }
            }

            // 首帧高亮预取：在 Show() 之前同步检测光标处的初始高亮并直接应用到 SelectionLayer，
            // 让第一个呈现帧就带高亮挖洞，消除"先全屏暗一下、遮罩高亮随后才弹入"的开场闪烁。
            // （Element 模式仅在预热已完成时预取，避免冷快照同步构建阻塞 UI 线程。）
            if (!startsWithMouseDrag && NativeMethods.GetCursorPos(out NativeMethods.POINT prefetchPt))
            {
                autoDetect.PrefetchInitialHighlight(new System.Drawing.Point(prefetchPt.X, prefetchPt.Y), prewarmCompleted);
            }

            window.Opened += (_, _) =>
            {
                using (PerformanceTimer.Measure("CaptureSession.Opened"))
                {
                    IntPtr hwnd = window.GetWindowHwnd();
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureWindow Opened " + At() + ". HWND=" + hwnd);
                    if (hwnd != IntPtr.Zero)
                    {
                        autoDetect.IgnoredWindow = hwnd;
                        autoDetect.WindowDetector.ExcludedHwnds.Add(hwnd);
                    }
                    else
                    {
                        AppLogger.Warn("CaptureWindow.GetWindowHwnd() returned Zero! This will break AutoDetect!");
                    }
                    autoDetect.WindowDetector.RefreshWindowSnapshot();
                    autoDetect.Start();

                    // 初始检测：遮罩打开后立即按当前光标位置推送一次检测，
                    // Window 模式同步出结果——避免"先全屏暗、等鼠标动了才亮起窗口"的开场闪烁；
                    // 配合 SelectionLayer 的全屏豁免（高亮覆盖整屏时不画遮罩），
                    // 全屏窗口场景遮罩从头到尾不再出现无意义的暗→亮跳变。
                    if (!startsWithMouseDrag && NativeMethods.GetCursorPos(out NativeMethods.POINT initialPt))
                    {
                        autoDetect.NotifyPointerMoved(new System.Drawing.Point(initialPt.X, initialPt.Y));
                    }

                }
            };

            // 🔴 性能埋点：分别记录 Show 的「提交时刻」与「返回时刻」。
            // 返回埋点紧贴 Show；全局鼠标首读单列，避免把选区更新／提前松手确认混算为显示开销。
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 遮罩 Show 提交 " + At());
            Task firstFramePresentation;
            using (PerformanceTimer.Measure("CaptureSession.Show")) firstFramePresentation = window.ShowCaptureAsync();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 遮罩 Show 返回 " + At());
            if (_refreshMouseGesture != null)
            {
                using (PerformanceTimer.Measure("CaptureSession.FirstMouseRefresh")) _refreshMouseGesture.Invoke();
                double sincePress = (Stopwatch.GetTimestamp() - MouseGesture!.StartedTimestamp) * 1000.0 / Stopwatch.Frequency;
                AppLogger.Info($"PERF GlobalMouse.InteractionReady sincePress={sincePress:F2}ms {At()} visible={window.IsVisible}");
            }
            else
            {
                AppLogger.Info($"PERF CaptureSession.InteractionReady entry=Normal {At()} visible={window.IsVisible}");
            }
            // RAF 是显示帧回调，不等于 GPU 已呈现；只记录第一、二格，用于和绘制记录交叉比对。
            if (AppLogger.DetailedLoggingEnabled)
            {
                bool frameProbeAlive = true;
                window.Closed += (_, _) => frameProbeAlive = false;
                window.RequestAnimationFrame(_ =>
                {
                    if (!frameProbeAlive || !window.IsVisible) return;
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("PERF CaptureSession.FrameCallback first " + At() + " (未测GPU上屏)");
                    window.RequestAnimationFrame(__ =>
                    {
                        if (frameProbeAlive && window.IsVisible)
                            { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("PERF CaptureSession.FrameCallback second " + At() + " (未测GPU上屏)"); }
                    });
                });
            }

            window.FlushOpenedDiagnostics();

            // 保留 Show 返回与鼠标首读的原埋点，首帧绘制等待单列，不能混算为原生 Show 耗时。
            using (PerformanceTimer.Measure("CaptureSession.WaitFirstFrame")) await firstFramePresentation;

            CaptureCompletion? completion = await tcs.Task;
            // 会话已落定：阶段一那条"撤幕即视为漏网"的判据到此失效（下面的重活期间窗口本就该一直隐着），
            // 换成阶段二的整段超时兜底。
            try { maskAbandonedGuard.Dispose(); } catch { }
            ArmTeardownWatchdog();
            if (completion == null)
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession: 用户取消");
                return null;
            }

            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 完成动作=" + completion.Action + ", Region=" + completion.Region);

            // 先把遮罩从屏幕上撤掉，再做后面的重活（裁剪/烘阴影/写剪贴板/录入历史/落盘）。
            // 见 HideCaptureWindowForCompletion 的说明：另存为要留着窗口当系统对话框的 owner，故排除。
            if (completion.Action != CaptureCompletionAction.SaveAs)
            {
                HideCaptureWindowForCompletion(window);
            }

            if (completion.Action == CaptureCompletionAction.LongScroll)
            {
                var captureKeys = BuiltinShortcutsStore.LoadGroupMap(BuiltinShortcutsStore.GroupCapture);
                LongScrollShortcut LongScrollKey(string id)
                {
                    if (!captureKeys.TryGetValue(id, out var key)) return new(0, 0, string.Empty);
                    return new(BuiltinShortcutsStore.WinModifiersOf(key.Modifiers),
                        BuiltinShortcutsStore.VirtualKeyOf(key.Key), BuiltinShortcutsStore.Format(key));
                }
                var longScrollOptions = new LongScrollOptions(completion.Region)
                {
                    Axis = completion.Axis,
                    CaptureIntervalMs = 16, // 统一使用 16ms 高速极速跟手采样 (OpenCV < 1ms 计算开销)
                    EnableFixedLayerMode = false,
                    MaxFixedLayerAlongLengthPx = completion.Axis == ScrollAxis.Horizontal ? 180 : 120,
                    IsDarkMode = IsDarkThemeActive(),
                    CancelShortcut = LongScrollKey("capture.cancel"),
                    FinishShortcut = LongScrollKey("capture.confirm")
                };
                try
                {
                    window.Close(); // 🎯 无条件彻底关闭并销毁 CaptureWindow，释放全屏 Win32 焦点
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await Task.Delay(80);
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: CaptureWindow 已退场，启动 LongScrollSession。Region=" + completion.Region);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureSession.RunAsync: 关闭 CaptureWindow 后启动长截图前处理异常：" + ex.Message);
                }
                using LongScrollResult longScrollResult = await new LongScrollSession(longScrollOptions, _settings).RunAsync();
                // 统计：长截图拿到成品（保存或出图）才算一次截图，中途取消不计
                if (!longScrollResult.Canceled) UsageStatistics.RecordScreenshot();
                bool createdLongSticker = !longScrollResult.Canceled && longScrollResult.HasBitmap;
                if (createdLongSticker)
                {
                    ShowLongScrollSticker(longScrollResult, new Avalonia.PixelPoint(completion.Region.X, completion.Region.Y), frame?.DpiScale ?? window.RenderScaling);
                }
                // 文件按路径导入；复制分支由会话录入，贴图分支在所有权交接前录入。
                if (!longScrollResult.Canceled && !string.IsNullOrWhiteSpace(longScrollResult.SavedPath))
                {
                    CaptureHistoryStore.TryImportFile(longScrollResult.SavedPath, CaptureHistorySource.LongScroll, _settings);
                }
                return CaptureSessionResult.LongScroll(longScrollResult.Message, createdLongSticker);
            }
            if (completion.Action == CaptureCompletionAction.Record)
            {
                // 此刻录制引擎已在跑、覆盖层已上屏；截图窗按长截图同款配方彻底退场。
                // RunAsync 会挂到整段录制结束（与长截图挂到拼图为先例一致），finally 里收尾。
                try
                {
                    window.Close();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RunAsync: 录制已开始，CaptureWindow 已退场");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureSession.RunAsync: 关闭 CaptureWindow 后启动录屏前处理异常：" + ex.Message);
                }
                RecordFlowOutcome recordOutcome = await RecordFlow.WaitFinishAsync();
                // 统计：录屏拿到成品才算一次截图（与长截图口径一致）；放弃/失败不计
                if (recordOutcome == RecordFlowOutcome.Completed) UsageStatistics.RecordScreenshot();
                string recordMessage = recordOutcome switch
                {
                    RecordFlowOutcome.Completed => "录制完成，预览贴图已钉屏（在其工具条保存/复制）",
                    RecordFlowOutcome.Abandoned => "已放弃，未保存",
                    _ => "录制失败",
                };
                return CaptureSessionResult.Record(recordMessage);
            }
            if (completion.Action == CaptureCompletionAction.Sticker)
            {
                return CreateSticker(frame, completion.Region, annotations, window?.TextLayers);
            }
            if (completion.Action == CaptureCompletionAction.Matting)
            {
                return CreateSticker(frame, completion.Region, annotations, window?.TextLayers, autoMatting: true, mattingModelType: completion.MattingModelType);
            }
            if (completion.Action == CaptureCompletionAction.ColorPick)
            {
                return CaptureSessionResult.ColorPicked(completion.PickedColorHex ?? string.Empty);
            }

            if (completion.Action == CaptureCompletionAction.Clipboard)
            {
                if (ScriptAction == "Workflow" && CapturedImageSink != null)
                {
                    int strokePx = CaptureStrokePx;
                    Bitmap? image = CreateCroppedResultBitmap(frame, completion.Region, annotations, window?.TextLayers, "组合动作",
                        strokePx: strokePx, strokeArgb: CaptureStrokeArgb);
                    if (image == null) return null;
                    int padding = SelectionEffects.StrokeMarginOf(strokePx);
                    var imageRegion = Rectangle.Inflate(completion.Region, padding, padding);
                    try { CapturedImageSink(image, imageRegion); }
                    catch { image.Dispose(); throw; }
                    OutputHandedOff = true;
                    UsageStatistics.RecordScreenshot();
                    return null;
                }
                return await CopyResultToClipboardAsync(frame, completion.Region, annotations, window?.TextLayers) ? CaptureSessionResult.CopiedToClipboard() : null;
            }
            if (completion.Action == CaptureCompletionAction.SaveAs)
            {
                string? explicitPath = completion.PickedColorHex;
                string? savedAsPath = SaveResult(frame, completion.Region, annotations, window?.TextLayers, explicitPath);
                return savedAsPath == null ? null : CaptureSessionResult.Saved(savedAsPath);
            }
            string? savedPath = SaveResult(frame, completion.Region, annotations, window?.TextLayers);
            return savedPath == null ? null : CaptureSessionResult.Saved(savedPath);
        }
        finally
        {
            _detachMouseGesture?.Invoke();
            _detachMouseGesture = _refreshMouseGesture = null;
            _activeWindow = null; // 会话结束，热键注入入口失效
            _detachRecordShortcut?.Invoke();
            _detachRecordShortcut = _startRecordFromSelection = null;
            _recordToolSelected = _recordStarting = false;
            _recordPreparing = false;
            System.Threading.Interlocked.Exchange(ref _recordShortcutWindow, IntPtr.Zero);
            teardownStarted = true; // 两条看门狗自此失效：窗口归这里独家收尾
            try { maskAbandonedGuard.Dispose(); } catch { }
            try
            {
                if (window != null)
                {
                    // 保存当前标注工具参数，供下次会话恢复
                    try
                    {
                        window.CaptureAnnotationDefaults(_settings);
                        _settings.Save();
                        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession: 已保存标注工具参数");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("CaptureSession.CaptureAnnotationDefaults 失败: " + ex.Message);
                    }

                    IntPtr hwnd = window.GetWindowHwnd();
                    if (hwnd != IntPtr.Zero)
                    {
                        // 强制立即将旧窗口设为鼠标穿透并隐藏，防止其在 Avalonia 延迟销毁期间拦截下一次截图的 WindowFromPoint
                        try
                        {
                            IntPtr oldStyle = ScreenCaptureTool.Platform.NativeMethods.GetWindowLongPtr(hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE);
                            long style = oldStyle.ToInt64();
                            ScreenCaptureTool.Platform.NativeMethods.SetWindowLongPtr(hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE, new IntPtr(style | ScreenCaptureTool.Platform.NativeMethods.WS_EX_TRANSPARENT | ScreenCaptureTool.Platform.NativeMethods.WS_EX_LAYERED));
                        }
                        catch { }
                    }
                    // Hide 与 Close 必须各自一个 try：同在一个 try 里时 Hide 抛异常就永久跳过 Close，
                    // 那张覆盖整屏的置顶窗于是留在原地吃掉所有点击（帧位图随后被释放 → 窗口看起来是透明的），
                    // 症状即"左键全盘失效、只有结束进程才恢复"。
                    try
                    {
                        using (PerformanceTimer.Measure("CaptureSession.FinallyHide")) window.Hide();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("CaptureSession: 会话收尾 Hide 遮罩失败，仍继续 Close：" + ex.Message);
                    }
                    try
                    {
                        using (PerformanceTimer.Measure("CaptureSession.CloseWindow")) window.Close();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("CaptureSession: 会话收尾 Close 遮罩失败，遮罩可能仍占着屏幕：" + ex.Message);
                    }
                }
            }
            catch { }

            try { autoDetect?.Dispose(); } catch { }
            try { frame?.Dispose(); } catch { }
        }
    }

    // -------------------- 交互 wiring --------------------

    private static bool IsDarkThemeActive()
    {
        try
        {
            ThemeVariant? variant = Application.Current?.ActualThemeVariant ?? Application.Current?.RequestedThemeVariant;
            return ReferenceEquals(variant, ThemeVariant.Dark) || string.Equals(variant?.Key?.ToString(), "Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private enum CaptureCompletionAction
    {
        Save,
        SaveAs,
        Clipboard,
        LongScroll,
        Sticker,
        ColorPick,
        Matting,
        Record,
    }

    private sealed class CaptureCompletion
    {
        public CaptureCompletion(Rectangle region, CaptureCompletionAction action, string? pickedColorHex = null, ScrollAxis axis = ScrollAxis.Vertical, MattingModelType? mattingModelType = null)
        {
            Region = region;
            Action = action;
            PickedColorHex = pickedColorHex;
            Axis = axis;
            MattingModelType = mattingModelType;
        }

        public Rectangle Region { get; }

        public CaptureCompletionAction Action { get; }

        public string? PickedColorHex { get; }

        public ScrollAxis Axis { get; }

        public MattingModelType? MattingModelType { get; }
    }

    private enum InteractionMode
    {
        Idle,
        DraggingNew,
        MovingSelection,
        ResizingHandle,
    }

    private enum AnnotationTool
    {
        None,
        Brush,
        Arrow,
        Rectangle,
        Text,
        Mosaic,
        Spotlight,
        Counter,
        Eraser,
    }

    private void WireUpInteractions(
        CaptureWindow window,
        AutoDetectController autoDetect,
        SelectionState selection,
        AnnotationDocument annotations,
        AnnotationHistory annotationHistory,
        CapturedFrame? frame,
        TaskCompletionSource<CaptureCompletion?> tcs)
    {
        long wireUpStarted = Stopwatch.GetTimestamp();
        _selectionPath = null;
        var pathEditor = window.InitializeSelectionModes(MouseGesture == null);
        var pathCursor = new Cursor(StandardCursorType.Cross);
        // 自动检测最新高亮（屏幕物理像素），用于"点击高亮区直接确认"路径
        bool mouseDragPending = MouseGesture != null;
        // 起手已由全局钩子接管；准备与拖动期间阻止右键取消、中键贴图及控件命中。
        window.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
        {
            if (mouseDragPending) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (mouseDragPending) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (mouseDragPending) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Rectangle? currentHighlight = null;
        Rectangle? pressedHighlight = null;

        // 用户手动模式状态
        InteractionMode mode = InteractionMode.Idle;
        bool hasManualSelection = false; // 一旦为 true，自动检测就不再写 SelectionLayer
        bool suppressOverlayDuringTransform = false;
        bool pendingExpandResize = false;
        bool _isCtrlPressed = false; // 跟踪 Ctrl 键状态（比 e.KeyModifiers 更可靠）
        bool isSpacePressed = false; // 框选时是否按住空格键平移选区
        bool spaceToggleArmed = true; // 空格切换"上次标注工具"的防重触发：仅首次按下生效，抬起后重新武装
        bool shiftPressedAlone = false; // Shift 是否单独按下（未与其他键组合）；单独松开时才切换放大镜色值格式

        // 截图辅助线：光标位置（屏幕物理像素）与十字瞄准光标状态。
        // Alt 按住态不在此记状态位，改为按需读系统物理按键，见 IsAltDown()
        System.Drawing.Point? guidelinePointerScreen = null; // 准星与瞄准光标跟随的光标位置
        bool guidelineShown = false;
        double[] noGuidelineLines = Array.Empty<double>();

        // 准星与辅助线合帧调度状态 (P0-1)
        bool guidelinesUpdateScheduled = false;
        int guidelinesUpdateToken = 0;

        // 准星与十字线性能度量 (P1-4)
        int crosshairFlushCount = 0;
        long crosshairFlushTotalTicks = 0;
        long crosshairFlushMaxTicks = 0;
        long crosshairLastApplyMs = 0;
        long crosshairMaxGapMs = 0;

        // 准星分段对比色采样状态与双缓冲区（零稳态分配）
        const float CrosshairDarkThreshold = 0.45f;
        const float CrosshairLightThreshold = 0.55f;
        int? lastCrosshairAimX = null;
        int? lastCrosshairAimY = null;
        byte[]? segsV_BufA = null;
        byte[]? segsV_BufB = null;
        bool toggleV = false;
        byte[]? segsH_BufA = null;
        byte[]? segsH_BufB = null;
        bool toggleH = false;
        // 阶段一用自绘十字替换系统光标，故要真正隐藏它。实例必须缓存：
        // 每次 PointerMoved 都 new 一个会让 Avalonia 认为是"换光标"而反复重设原生光标。
        Cursor noneCursor = new Cursor(StandardCursorType.None);
        Cursor rectangleEraserCursor = new Cursor(StandardCursorType.Cross);
        bool aimCursorActive = false;
        HandleDirection pendingExpandResizeDirection = HandleDirection.None;
        System.Drawing.Point pendingExpandStartPoint = default;
        Rectangle pendingExpandStartRect = default;
        Rectangle? pendingSelectionVisualRect = null;
        bool pendingSelectionShowControls = false;
        bool selectionVisualUpdateScheduled = false;
        Rectangle liveTransformRect = Rectangle.Empty;

        var captureHistory = new CaptureSnapshotHistory(annotations, window);
        window.TextSnapshotRequested += (_, _) => captureHistory.CommitSnapshot();

        AnnotationTool activeAnnotationTool = AnnotationTool.None;
        bool drawingStroke = false;
        bool drawingArrow = false;
        bool drawingRectangle = false;
        bool drawingMosaic = false;
        bool drawingSpotlight = false;
        bool drawingEraser = false;
        EraserGestureMode eraserGestureMode = EraserGestureMode.Rectangle;
        bool eraserHistoryCommitted = false;
        ErasureShape? currentErasure = null;
        var currentErasureMarks = new List<ErasureMark>();
        PointF eraserStartPoint = default;
        PointF lastEraserPoint = default;
        // 擦除发布的合帧闸门读数与被压住标记，口径见 PreviewFrameGateMs。落点的攒积不闸，只闸"写文档＋重绘"。
        double eraserLastPublishMs = 0;
        bool eraserPublishPending = false;
        // 上一次擦除发布的实际耗时（写文档＋标脏那一段），只喂取证汇总；闸门读的是重绘耗时，见 EraserPublishGateMs
        double eraserPublishCostMs = 0;
        // 序号引线手势：截图侧的序号是"按下即建徽章"（与升级前同一时刻），引线靠随后这一段拖动补上，
        // 所以它不能复用 shapeEditor（那位只编辑已存在的图元）。拖动过阈值才落引线，纯单击＝没有引线。
        bool draggingCounterTail = false;
        Guid counterTailShapeId = Guid.Empty;
        PointF counterTailAnchor = default;
        bool longScrollStarting = false;
        var currentStrokePoints = new List<StrokePoint>();
        var freehandStrokePoints = new List<StrokePoint>(); // 自由手绘点迹副本，供中途按下/松开 Shift 时无缝回退与恢复
        PointF brushStartPoint = default;                   // 画笔落笔起点（物理像素）
        double brushStartTimeMs = 0.0;                      // 画笔落笔时刻（高精度毫秒）
        PointF lastBrushMousePoint = default;               // 画笔拖拽中最新鼠标位置（供按键事件触发直线吸附重算）
        Avalonia.Point? brushPointerDip = null;             // 画笔/橡皮擦在标注层上的圆形光标中心
        bool isBrushShiftConstrained = false;               // 当前笔迹是否处于按住 Shift 约束为直线的状态
        // 六类作画手势是否正在进行。Shift 有两个互斥语义：拖图途中那一下是"锁比例/锁直线"，
        // 空手按一下则是"切放大镜色值格式"——同一个按键只能靠"当时有没有在拖"分流。
        // 2026-09-20 形状/箭头/序号引线也开始吃 Shift，判据从原先的"只认画笔"扩成这一份。
        bool ShapeGestureInFlight() => drawingStroke || drawingArrow || drawingRectangle
            || drawingMosaic || drawingSpotlight || draggingCounterTail;
        // 画笔输入整形（One-Euro 平滑 + 最小点距）：与贴图标注共用 Core 那一份实现
        BrushInputSmoother brushSmoother = new();
        long lastBrushPreviewTick = 0;
        PointF arrowStart = default;
        PointF rectangleStart = default;
        PointF mosaicStart = default;
        PointF spotlightStart = default;
        // 全窗口唯一一份选中态。原先按六类图元各存一个 id（selectedBlur/Spotlight/Stroke/Arrow/Rectangle/Counter），
        // 但每次选中前都走 ClearAllSelections，"同时选中两类"从来不可能发生，故塌成一个 id + 按 id 现取图元本体。
        Guid? selectedShapeId = null;
        float currentArrowScale = 1.0f; // 对齐旧版 _currentArrowScale，选中箭头时同步
        // 聚光灯预览的合帧闸门。计时用 Stopwatch.GetTimestamp 而非 Environment.TickCount64：
        // 后者步进只到 ~15.6ms 一格（整数化后表现为 15/16ms 交替），拿它判 16ms 会把落在 15ms
        // 那一档的间隔一并挡掉，实际节流退化成 16ms/31ms 交替——帧距不匀比恒定偏慢更容易被感知为「发涩」。
        // 口径与下面拖动闸门的 shapeGestureGateTicks 一致，阈值仍是原来的 16ms，只把计时基准换准。
        long spotlightPreviewGateTicks = (Stopwatch.Frequency * 16) / 1000;  // 16ms
        long lastSpotlightPreviewStamp = 0;
        var autoMosaicHighlights = new List<RectangleF>();
        // 一段"拖动手柄／拖动图元"手势的全部状态都在共享编辑器里（截图与贴图同一份实现）：
        // 原先这里按六类图元各存一份"是否在拖 + 起手坐标 + 起手几何"，共 12 个标志位与 11 个起点量，
        // 同一件事有六种写法。现在只剩一个对象，把手柄种类由 AnnotationEditTarget 表达。
        AnnotationShapeEditor shapeEditor = new(
            id => annotations.Shapes.FirstOrDefault(x => x.Id == id),
            shape => window.Annotation.SetPreviewShape(shape),
            // 提交出口。除写文档外还要按类型回灌一次参数条：改动前松手走的是 ReplaceSelectedBlur/SpotlightShape，
            // 那两处会顺带 Select 一次从而同步工具条档位，这条通用通路不能把它丢了。
            shape =>
            {
                annotations.ReplaceShape(shape);
                switch (shape)
                {
                    case BlurShape blur: SelectBlurShape(blur.Id); break;
                    case SpotlightShape spotlight: SelectSpotlightShape(spotlight.Id); break;
                    case RectangleShape rect: SelectShape(rect.Id); break;
                    default: window.Annotation.InvalidateVisual(); break;
                }
            },
            // 历史由宿主记账：截图侧＝改动前先压一条快照（贴图侧的历史在提交时才落，那边是空实现）
            () => captureHistory.CommitSnapshot(),
            // 聚光灯的夹取范围＝选区。截图侧内容坐标就是屏幕物理像素，与图元几何同单位。
            SpotlightClampBounds);
        Rect autoHoverTargetDip = default;
        Rect autoHoverDisplayDip = default;

        // 双击首击防误触追踪：记录在 ClickCount == 1 时由当前标注工具产生的候选图形或文字
        long lastClick1TickMs = 0;
        System.Drawing.Point lastClick1ScreenPt = default;
        bool click1CreatedAnnotation = false;
        bool click1IsBrushCandidate = false;

        // —— 鼠标速度闸门 ——
        // 鼠标快速划过中间元素时，丢弃中间检测结果，只认落脚点（对齐 PixPin 行为）。
        // 速度高于阈值时，新 hover 目标不立即采用，挂起在 pendingAutoHoverTargetDip；
        // 速度降下来（或停止）后，立刻采用挂起的最新目标。
        System.Drawing.Point lastPointerScreen = default;
        long lastPointerTickMs = 0;       // 0 = 哨兵，首次 PointerMoved 跳过速度计算
        double pointerSpeedPxPerMs = 0;   // 最近一次 PointerMoved 的瞬时速度

        void StopAutoHoverAnimation(bool clearVisual = true)
        {
            autoHoverTargetDip = default;
            autoHoverDisplayDip = default;
            if (clearVisual)
            {
                window.Layer.AutoHoverRegion = default;
                window.Layer.MousePosition = null;
            }
        }

        static bool IsValidHoverRect(Rect rect)
        {
            return rect.Width > 0.5 && rect.Height > 0.5 && !double.IsNaN(rect.X) && !double.IsNaN(rect.Y) && !double.IsNaN(rect.Width) && !double.IsNaN(rect.Height);
        }

        void SetAutoHoverTarget(Rect targetDip)
        {
            if (!IsValidHoverRect(targetDip))
            {
                StopAutoHoverAnimation();
                return;
            }

            // 正常采用

            window.Layer.Region = default;
            window.Annotation.SpotlightBoundsScreenRect = null;
            window.UpdateOverlay(default, null, false, false);

            // 瞬间对齐目标，去除缓动延迟
            autoHoverTargetDip = targetDip;
            autoHoverDisplayDip = targetDip;
            window.Layer.AutoHoverRegion = targetDip;
        }

        void ApplySelectionVisual(Rectangle? rectScreen, bool showControls)
        {
            StopAutoHoverAnimation();
            if (longScrollStarting) showControls = false;
            if (rectScreen.HasValue)
            {
                window.Annotation.SpotlightBoundsScreenRect = rectScreen.Value;
                var dipRect = window.ScreenRectToWindowDip(rectScreen.Value);
                window.Layer.Region = dipRect;
                if (suppressOverlayDuringTransform)
                {
                    window.BeginSelectionTransform(keepDragPill: mouseDragPending);
                    window.ShowDragInfoPill(dipRect, rectScreen.Value);
                }
                else
                {
                    window.UpdateOverlay(dipRect, rectScreen.Value, true, showControls);
                }
            }
            else
            {
                window.Annotation.SpotlightBoundsScreenRect = null;
                window.Layer.Region = default;
                window.UpdateOverlay(default, null, false, false);
            }
            // 选区尺寸变了（滚轮扩缩、方向键微移、点击确认等），参考线要立刻跟上，不能等下一次鼠标移动
            UpdateGuidelines();
        }

        /// <summary>
        /// 把圆角/描边/阴影推进绘制层做实时预览：遮罩挖洞的圆角、边框描边、描边色带与投影
        /// 全由这几项属性驱动。StyledProperty 变化自带 AffectsRender，不必再手动 InvalidateVisual。
        ///
        /// 描边与阴影只在鼠标悬浮到参数条这两组控件上时**画出来**（<see cref="CaptureWindow.CaptureEffectHover"/>
        /// → <c>SelectionLayer.EffectsVisible</c>）：这两样是预览层唯一要算等距偏移与投影扩散的开销，而"看效果"
        /// 这个动作本来就只发生在调整参数的那一瞬。注意闸门只翻"画不画"，宽度/颜色/开关这些**真值始终照常
        /// 下发**——旧实现是关闸门时下发 0/false，于是每次悬浮都要把几十圈几何整个重算一遍。
        /// 圆角不设闸门：它只改遮罩挖洞与边框的那条轮廓，缓存命中后是常数开销，且是选区形状本身的一部分。
        /// 成图出口读的是 _settings，与预览无关。
        /// </summary>
        void ApplyCaptureEffectPreview()
        {
            window.Layer.EffectRadiusPx = CaptureCornerRadiusPx;
            window.Layer.EffectStrokePx = CaptureStrokePx;
            window.Layer.EffectStrokeArgb = CaptureStrokeArgb;
            window.Layer.EffectShadow = _settings.CaptureShadow;
            window.Layer.EffectsVisible = window.CaptureEffectHover;
        }

        void ApplyAutoHoverVisual(Rectangle? rectScreen)
        {
            if (pathEditor.Mode != CaptureSelectionMode.Rectangle) return;
            if (hasManualSelection || mode != InteractionMode.Idle)
            {
                StopAutoHoverAnimation();
                return;
            }

            if (!rectScreen.HasValue)
            {
                StopAutoHoverAnimation();
                return;
            }

            SetAutoHoverTarget(window.ScreenRectToWindowDip(rectScreen.Value));
        }

        int selectionVisualUpdateToken = 0;

        void RequestSelectionVisualUpdate(Rectangle? rectScreen, bool showControls)
        {
            pendingSelectionVisualRect = rectScreen;
            pendingSelectionShowControls = showControls;
            if (selectionVisualUpdateScheduled) return;
            selectionVisualUpdateScheduled = true;
            int token = ++selectionVisualUpdateToken;
            // Render 优先级仍可能在同一显示帧里执行多轮；只在下一显示帧应用最新选区。
            window.RequestAnimationFrame(_ =>
            {
                if (!selectionVisualUpdateScheduled || token != selectionVisualUpdateToken) return;
                selectionVisualUpdateScheduled = false;
                if (!window.IsVisible || longScrollStarting) return;
                ApplySelectionVisual(pendingSelectionVisualRect, pendingSelectionShowControls);
            });
        }

        void UpdateSelectionVisualImmediate(Rectangle? rectScreen, bool showControls)
        {
            selectionVisualUpdateScheduled = false;
            selectionVisualUpdateToken++;
            pendingSelectionVisualRect = rectScreen;
            pendingSelectionShowControls = showControls;
            ApplySelectionVisual(rectScreen, showControls);
        }

        void UpdateSelectionVisual(Rectangle? rectScreen, bool showControls)
        {
            RequestSelectionVisualUpdate(rectScreen, showControls);
        }

        static Rectangle CalculateResizedRect(Rectangle startRect, HandleDirection direction, int dx, int dy)
        {
            int left = startRect.Left;
            int top = startRect.Top;
            int right = startRect.Right;
            int bottom = startRect.Bottom;

            switch (direction)
            {
                case HandleDirection.TopLeft: left += dx; top += dy; break;
                case HandleDirection.Top: top += dy; break;
                case HandleDirection.TopRight: right += dx; top += dy; break;
                case HandleDirection.Right: right += dx; break;
                case HandleDirection.BottomRight: right += dx; bottom += dy; break;
                case HandleDirection.Bottom: bottom += dy; break;
                case HandleDirection.BottomLeft: left += dx; bottom += dy; break;
                case HandleDirection.Left: left += dx; break;
            }

            return Rectangle.FromLTRB(
                Math.Min(left, right),
                Math.Min(top, bottom),
                Math.Max(left, right),
                Math.Max(top, bottom));
        }

        void UpdateTransformVisual(Rectangle rectScreen)
        {
            Rectangle normalized = Rectangle.FromLTRB(
                Math.Min(rectScreen.Left, rectScreen.Right),
                Math.Min(rectScreen.Top, rectScreen.Bottom),
                Math.Max(rectScreen.Left, rectScreen.Right),
                Math.Max(rectScreen.Top, rectScreen.Bottom));
            liveTransformRect = normalized;
            window.Annotation.SpotlightBoundsScreenRect = normalized;
            var dipRect = window.ScreenRectToWindowDip(normalized);
            window.Layer.Region = dipRect;
            window.ShowDragInfoPill(dipRect, normalized);
        }

        void BeginSelectionTransform()
        {
            suppressOverlayDuringTransform = true;
            liveTransformRect = selection.Region;
            window.BeginSelectionTransform();
            if (selection.HasSelection)
            {
                window.ShowDragInfoPill(window.ScreenRectToWindowDip(selection.Region), selection.Region);
            }
        }

        void EndSelectionTransform()
        {
            suppressOverlayDuringTransform = false;
            if (!selection.HasSelection) return;
            var dipRect = window.ScreenRectToWindowDip(selection.Region);
            window.EndSelectionTransform(dipRect, selection.Region);
        }

        /// <summary>
        /// 截图成功的唯一出口：先把这一刻的矩形记进选区历史（R 键往后退的那一格），再把结果交回调用方。
        /// 取消／异常那几路走的是 <c>tcs.TrySetResult(null)</c>，不经这里 ⇒ 半途放弃的框一律不留痕（2026-09-29 定稿口径）。
        /// </summary>
        void SettleSuccess(CaptureCompletion completion)
        {
            // 旧矩形历史不保存路径外接框，避免恢复后误截入原本排除的像素。
            if (_selectionPath == null) Core.Capture.SelectionHistory.Push(completion.Region);
            tcs.TrySetResult(completion);
        }

        /// <summary>
        /// 「恢复上一次截图框」（默认裸 R，每按一次往历史栈深处退一格）：取到一格就按<b>拖框松手那一路的同款四步</b>
        /// 摆上屏幕——少任何一步都会退化成"自动检测继续往 SelectionLayer 上写"或"工具栏不出来"。
        /// 取不到（没有历史／剩下的全落在当前虚拟屏外／鼠标手势正在飞）就一动不动，只由 <c>SelectionHistory</c> 记日志。
        /// 恢复出来的框<b>不再入栈</b>：入栈只发生在 <see cref="SettleSuccess"/>，所以按两下 R 不会把历史自我复制。
        /// </summary>
        void TryRestorePreviousSelection()
        {
            if (mode != InteractionMode.Idle) return;
            if (!Core.Capture.SelectionHistory.TryNext(out Rectangle rect)) return;

            selection.SetRegion(rect);
            hasManualSelection = true;
            StopAutoHoverAnimation();
            UpdateSelectionVisualImmediate(selection.Region, true);
            UpdateGuidelines();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RestoreSelection: 已恢复选区 " + rect + "（剩余可退="
                + Core.Capture.SelectionHistory.Count + "）");
        }

        void CompleteCurrentSelection(CaptureCompletionAction action, MattingModelType? mattingModelType = null, Rectangle? overrideRect = null)
        {
            Rectangle? final = overrideRect ?? ResolveFinalRect();
            if (final.HasValue)
            {
                // 先撤遮罩再清视觉：纯悬停检测元素（尚未单击/拖框形成正式选区）直接回车或双击确认时，
                // StopAutoHoverAnimation 会把 AutoHoverRegion 挖洞清空，而此刻正式 Region 仍为空，
                // SelectionLayer 随即渲染出一帧「整屏无洞全暗遮罩」，要等 continuation 才 Hide——
                // 用户看到的就是「先暗一下、再完成截图」。这里与 OCR/翻译/AI 识图路径同源：先 Hide
                // （只关 HWND，图元/TextLayers 等内存数据照常可读）再清理；下游 continuation 的 Hide
                // 会因窗口已不可见而直接跳过。SaveAs 不走本方法，不存在保留窗口当系统对话框 owner 的问题。
                HideCaptureWindowForCompletion(window);
                StopAutoHoverAnimation();
                window.SetActiveTool(null);
                window.Annotation.ClearPreviewShape();
                SettleSuccess(new CaptureCompletion(final.Value, action, mattingModelType: mattingModelType));
            }
        }

        /// <param name="forceMode">输出模式强制覆盖："Window"=弹结果窗，"Toast"=写剪贴板+提示条；null=跟随设置。</param>
        async void TriggerOcrForSelection(string? forceMode = null)
        {
            Rectangle? final = ResolveFinalRect();

            if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0)
            {
                return;
            }

            // 前置拦截：OCR 模型未就绪时在选区界面就地弹窗下载，下载完成后自动继续识别，取消则保留选区
            if (!Core.Deps.DependencyManager.IsReady(Core.Deps.DepIds.Ocr))
            {
                bool ready = await EnsureDependencyOnCaptureWindowAsync(window, Core.Deps.DepIds.Ocr, "OCR 识别引擎未就绪，识别已取消");
                if (!ready || !window.IsVisible) return;
                final = (hasManualSelection && selection.HasSelection) ? selection.Region : final;
                if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0) return;
            }

            Bitmap? cropped = null;
            StickerToastWindow? ocrPendingToast = null;
            try
            {
                // 先撤遮罩再取图：裁剪＋烘阴影这一串在 UI 线程同步跑，几百毫秒起步；遮罩若等重活跑完
                // （tcs.TrySetResult 落定之后）才收，用户看到的就是"按了回车没反应、过一拍才消失"。
                // AI 识图那条路径 2026-09-11 已这么改，OCR / 翻译两条同源路径当时漏改，这里补齐。
                HideCaptureWindowForCompletion(window);
                cropped = CreateCroppedResultBitmap(frame, final.Value, annotations, window.TextLayers, "文字识别");
                if (cropped == null)
                {
                    // 取图失败就把遮罩放回去：否则会话卡在一个看不见的窗口里，等不到任何按键。
                    RestoreCaptureWindowForCompletion(window);
                    return;
                }

                // 框已经裁成图交给下游（文字识别／翻译／AI 识图），按"截图成功"记一格。
                // 下面这句的结果是 null，语义只是"这次没有落盘文件"，不是"用户放弃了截图"。
                ocrPendingToast = StickerToastWindow.ShowPendingAtTop("正在识别文字...");
                Core.Capture.SelectionHistory.Push(final.Value);
                OutputHandedOff = true;
                tcs.TrySetResult(null);

                // 在后台线程异步执行 OCR 识别
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 缺 OCR 模型：遮罩此刻已收，居中开卡就地下载，下完自动续跑识别、结果窗照旧开
                        if (!await DependencyGate.EnsureAsync(Core.Deps.DepIds.Ocr).ConfigureAwait(false))
                            return;

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var ocrResult = await GetSharedOcrService().ExtractFullResultAsync(cropped);
                        sw.Stop();
                        double elapsedSeconds = sw.Elapsed.TotalSeconds;

                        if (!string.IsNullOrWhiteSpace(ocrResult.Text))
                        {
                            UsageStatistics.RecordOcr(); // 统计：OCR 识别出文字一次

                            // 静默复制模式：结果直接写入剪贴板，不打开窗口；
                            // 提示条展示识别文本，点击仍可进入编辑窗口（位图所有权随点击转移）。
                            bool silentCopy = forceMode == "Toast" ||
                                (forceMode == null && string.Equals(
                                    AppSettings.Load().OcrOutputMode, "Clipboard", StringComparison.OrdinalIgnoreCase));
                            if (silentCopy)
                            {
                                ClipboardService.SetText(ocrResult.Text);
                                Bitmap? owned = cropped;
                                cropped = null;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    ocrPendingToast?.Close();
                                    ScreenCaptureTool.Windows.OcrResultToastWindow.Show(ocrResult, owned, elapsedSeconds);
                                });
                            }
                            else
                            {
                                // 截图所有权转移给结果窗口（内部转换后释放）
                                Bitmap? owned = cropped;
                                cropped = null;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    ocrPendingToast?.Close();
                                    ScreenCaptureTool.Windows.OcrWindow.ShowResult(ocrResult, owned, elapsedSeconds);
                                });
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(ocrResult.Error))
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                ocrPendingToast?.Close();
                                ScreenCaptureTool.Windows.StickerToastWindow.ShowAtTop("OCR 识别失败: " + ocrResult.Error);
                            });
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                ocrPendingToast?.Close();
                                ScreenCaptureTool.Windows.StickerToastWindow.ShowAtTop("未识别到文字");
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("后台 OCR 识别异常", ex);
                        Dispatcher.UIThread.Post(() =>
                        {
                            ocrPendingToast?.Close();
                            StickerToastWindow.ShowAtTop("OCR 识别失败，请重试");
                        });
                    }
                    finally
                    {
                        // 依赖取消／异常也收起等待提示；只关闭本次任务持有的消息窗。
                        Dispatcher.UIThread.Post(() => ocrPendingToast?.Close());
                        try { cropped?.Dispose(); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                ocrPendingToast?.Close();
                AppLogger.Error("CaptureSession.OcrRequested 初始化异常", ex);
                // 遮罩此刻已被提前撤掉：只记日志会让会话的 await 永挂、_captureRunning 永久为真
                // （此后所有截图热键都按了没反应）。落定会话让 finally 正常收尾。
                tcs.TrySetResult(null);
                try { cropped?.Dispose(); } catch { }
            }
        }

        async void TriggerTranslateForSelection()
        {
            Rectangle? final = ResolveFinalRect();

            if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0)
            {
                return;
            }

            var settings = AppSettings.Load();
            var config = settings.ToTranslationConfig();

            // 确定翻译模式：脚本指令优先，其次跟随设置
            //   TranslateImageToImage → 强制图片到图片
            //   TranslateImageToText  → 强制图片到文本
            //   TranslateSilent       → 强制静默剪贴板
            //   Translate (默认)      → 跟随 config.Mode
            bool isSilent = string.Equals(ScriptAction, "TranslateSilent", StringComparison.OrdinalIgnoreCase);
            bool forceImageToImage = string.Equals(ScriptAction, "TranslateImageToImage", StringComparison.OrdinalIgnoreCase);
            bool forceImageToText = string.Equals(ScriptAction, "TranslateImageToText", StringComparison.OrdinalIgnoreCase);

            bool useImageToImage = config.IsValid &&
                (forceImageToImage || (!forceImageToText && !isSilent && config.Mode == TranslationMode.ImageToImage));

            // 若走依赖本地 OCR 提取文字的分支，前置拦截：OCR 模型缺失时在选区界面就地弹窗下载，下载完成后自动继续
            if (!useImageToImage && !Core.Deps.DependencyManager.IsReady(Core.Deps.DepIds.Ocr))
            {
                bool ready = await EnsureDependencyOnCaptureWindowAsync(window, Core.Deps.DepIds.Ocr, "OCR 识别引擎未就绪，翻译已取消");
                if (!ready || !window.IsVisible) return;
                final = (hasManualSelection && selection.HasSelection) ? selection.Region : final;
                if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0) return;
            }

            Bitmap? cropped = null;
            try
            {
                // 与文字识别同一条改法：先把遮罩撤掉再取图，别让"裁剪＋烘阴影"的耗时变成回车后的停顿。
                HideCaptureWindowForCompletion(window);
                cropped = CreateCroppedResultBitmap(frame, final.Value, annotations, window.TextLayers, "翻译");
                if (cropped == null)
                {
                    RestoreCaptureWindowForCompletion(window);
                    return;
                }

                // 框已经裁成图交给下游（文字识别／翻译／AI 识图），按"截图成功"记一格。
                // 下面这句的结果是 null，语义只是"这次没有落盘文件"，不是"用户放弃了截图"。
                Core.Capture.SelectionHistory.Push(final.Value);
                OutputHandedOff = true;
                tcs.TrySetResult(null);

                if (isSilent)
                {
                    // 静默剪贴板：OCR → 翻译 → 写入剪贴板，不弹窗
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // ① OCR 识别行级文本（复用全局共享实例，避免频繁加载模型）
                            //    缺模型时先开卡下载，下完接着翻（与「提取文字」那条同源）
                            if (!await DependencyGate.EnsureAsync(Core.Deps.DepIds.Ocr).ConfigureAwait(false))
                                return;
                            var ocrService = GetSharedOcrService();
                            var ocrLines = await ocrService.ExtractLinesWithBoxesAsync(cropped).ConfigureAwait(false);

                            if (ocrLines.Count == 0)
                            {
                                Dispatcher.UIThread.Post(() => StickerToastWindow.ShowAtTop("未识别到文字"));
                                return;
                            }

                            // ② 拼接原文
                            var sb = new StringBuilder();
                            foreach (var line in ocrLines)
                            {
                                if (sb.Length > 0) sb.Append('\n');
                                sb.Append(line.Text);
                            }
                            string sourceText = sb.ToString();

                            // ③ 文本翻译（走无语言参数重载：源语言与目标语言相同时自动回退至次级目标语言）
                            var translator = new TranslationService(config);
                            var textResult = await translator.TranslateTextAsync(sourceText).ConfigureAwait(false);

                            if (!textResult.Success)
                            {
                                Dispatcher.UIThread.Post(() =>
                                    StickerToastWindow.ShowAtTop("翻译失败: " + textResult.ErrorMessage));
                                return;
                            }

                            UsageStatistics.RecordTranslate(); // 统计：翻译成功一次（内部 OCR 不另计）

                            // ④ 写入剪贴板
                            string translatedText = textResult.TranslatedText ?? "";
                            Dispatcher.UIThread.Post(() =>
                            {
                                ClipboardService.SetText(translatedText);
                                StickerToastWindow.ShowAtTop("译文已复制到剪贴板");
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("静默剪贴板翻译异常", ex);
                            Dispatcher.UIThread.Post(() =>
                                StickerToastWindow.ShowAtTop("翻译失败: " + ex.Message));
                        }
                        finally
                        {
                            try { cropped?.Dispose(); } catch { }
                        }
                    });
                }
                else if (useImageToImage)
                {
                    // 图片到图片：先贴原图（带加载遮罩）→ 后台产商图片翻译 → 出结果替换贴图内容。
                    //
                    // 先贴图的好处是用户立刻能看到截图内容并可自由拖动/缩放，不必干等翻译；
                    // 替换时 ReplaceImage 不重置 _scale、也不动窗口位置，故等待期间做的
                    // 缩放与移动都会被保留。
                    int screenX = frame!.VirtualScreenRect.X + final.Value.X;
                    int screenY = frame!.VirtualScreenRect.Y + final.Value.Y;

                    // 贴图窗口引用需回传给后台任务，供其替换内容或撤下遮罩。
                    // 贴图持有 cropped 的**副本**：原 cropped 仍由后台任务编码上传，
                    // 两者生命周期独立，避免一方释放导致另一方拿到已释放位图。
                    var stickerReady = new TaskCompletionSource<StickerWindow?>();
                    var previewBitmap = new System.Drawing.Bitmap(cropped!);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            var preview = new StickerWindow(previewBitmap) { RecordUsageOnOpen = false };
                            preview.SetStickerScreenPosition(new PixelPoint(screenX, screenY));
                            preview.Show();
                            StickerFocusManager.SetFocus(preview);
                            preview.ShowTranslatingOverlay("翻译中…");
                            // 占用该贴图的翻译闸门：与贴图右键入口共用同一把锁，
                            // 等待期间用户再对这张新贴图点「翻译」会被拒绝
                            if (!preview.TryBeginTranslate())
                                AppLogger.Warn("[翻译] 新建贴图的闸门占用失败（不应发生），本次翻译仍继续");
                            stickerReady.TrySetResult(preview);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("创建预览贴图失败", ex);
                            try { previewBitmap.Dispose(); } catch { }
                            stickerReady.TrySetResult(null);
                        }
                    });

                    _ = Task.Run(async () =>
                    {
                        var sticker = await stickerReady.Task.ConfigureAwait(false);
                        try
                        {
                            // ① 原图编 PNG 字节（产商只收 base64，容器格式由门面内部按需转 JPEG）
                            byte[] pngBytes;
                            using (var ms = new MemoryStream())
                            {
                                cropped!.Save(ms, ImageFormat.Png);
                                pngBytes = ms.ToArray();
                            }

                            // ② 产商图片翻译：识别、擦除、译文绘制一次完成，本地不参与任何渲染。
                            //    走无语言参数重载：产商回传的图上文字语种与目标语言同族时自动改翻次级目标语言
                            var translator = new TranslationService(config);
                            var result = await TranslationFlow.TranslateImageAsync(translator, pngBytes).ConfigureAwait(false);

                            if (!result.Success || result.ImageBytes == null)
                            {
                                // 失败：保留已贴出的原图，撤遮罩并提示，用户仍可继续使用该贴图
                                string message = result.UserMessage;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    sticker?.HideTranslatingOverlay();
                                    StickerToastWindow.ShowAtTop(message);
                                });
                                return;
                            }

                            UsageStatistics.RecordTranslate(); // 统计：翻译成功一次

                            // ③ 成品图字节 → 位图。GDI+ 的 Image 依附于解码流，流一关便不可再绘制，
                            //    故读完立刻拷一份独立 Bitmap 再释放两者。
                            //
                            // 备份原图与译文独立副本（用于 Toggle 切换原文/译文）：
                            // 必须在后台线程趁 cropped 尚未被 finally 释放时完成拷贝！
                            // 严禁推迟到 UIThread.Post 回调中才去访问 cropped，否则存在竞态：
                            // 后台线程跑完直接进入 finally 执行 cropped.Dispose()，UI 线程才执行委托访问已释放的 cropped，
                            // 将导致 GDI+ "Parameter is not valid" 异常并使整个进程闪退。
                            System.Drawing.Bitmap? renderedBitmap = null;
                            System.Drawing.Bitmap? originalBackup = null;
                            System.Drawing.Bitmap? translatedBackup = null;
                            try
                            {
                                using (var decoded = new MemoryStream(result.ImageBytes))
                                using (var image = System.Drawing.Image.FromStream(decoded))
                                {
                                    renderedBitmap = new System.Drawing.Bitmap(image);
                                }

                                originalBackup = new System.Drawing.Bitmap(cropped!);
                                translatedBackup = new System.Drawing.Bitmap(renderedBitmap);
                            }
                            catch (Exception ex)
                            {
                                try { originalBackup?.Dispose(); } catch { }
                                try { translatedBackup?.Dispose(); } catch { }
                                try { renderedBitmap?.Dispose(); } catch { }
                                AppLogger.Error("解码翻译结果或创建原图/译文备份位图失败", ex);
                                throw;
                            }

                            // ④ 替换已贴出的预览图为译文图，并备份原图和译文用于 Toggle 切换。
                            // renderedBitmap 所有权转移给贴图窗口（OnClosed 时释放），禁止在此 Dispose，
                            // 否则贴图后续的 OCR"文本可选择"/翻译/另存会拿到已释放位图而失败。
                            // ReplaceImage 内部会撤下遮罩，并保留用户此前的缩放与窗口位置。
                            Dispatcher.UIThread.Post(() =>
                            {
                                try
                                {
                                    if (sticker != null && !sticker.IsClosed)
                                    {
                                        // 备份原图和译文，支持 Toggle 切换
                                        sticker.SetTranslationImages(originalBackup, translatedBackup);
                                        sticker.ReplaceImage(renderedBitmap);
                                        // 成功文案取结果自身，本机回填有跳过时才多出「N 处未能回填」，措辞不外落到此处。
                                        StickerToastWindow.ShowAtTop(result.UserMessage + "（再次点击可切换原文/译文）");
                                    }
                                    else
                                    {
                                        // 贴图窗口已关闭或创建失败，妥善释放持有的副本
                                        try { originalBackup.Dispose(); } catch { }
                                        try { translatedBackup.Dispose(); } catch { }
                                        if (sticker == null)
                                        {
                                            // 预览贴图创建失败（极少见）→ 退回原行为：直接新建贴图
                                            var fallback = new StickerWindow(renderedBitmap) { RecordUsageOnOpen = false };
                                            fallback.SetStickerScreenPosition(new PixelPoint(screenX, screenY));
                                            fallback.Show();
                                            StickerFocusManager.SetFocus(fallback);
                                        }
                                        else
                                        {
                                            try { renderedBitmap.Dispose(); } catch { }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Error("替换翻译贴图失败", ex);
                                    try { originalBackup.Dispose(); } catch { }
                                    try { translatedBackup.Dispose(); } catch { }
                                    try { renderedBitmap.Dispose(); } catch { }
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("图片到图片翻译异常", ex);
                            // 异常同样要撤遮罩，否则「翻译中」会永久停在贴图上
                            Dispatcher.UIThread.Post(() =>
                            {
                                sticker?.HideTranslatingOverlay();
                                StickerToastWindow.ShowAtTop("翻译失败: " + ex.Message);
                            });
                        }
                        finally
                        {
                            sticker?.EndTranslate(); // 归还闸门，否则该贴图此后永久拒绝翻译
                            try { cropped?.Dispose(); } catch { }
                        }
                    });
                }
                else
                {
                    // 图片到文本：OCR 出原文后分流——短文本出极简浮窗（锚定选区），长文本直接开编辑窗口
                    int screenX = frame!.VirtualScreenRect.X + final.Value.X;
                    int screenY = frame!.VirtualScreenRect.Y + final.Value.Y;
                    var anchor = new PixelRect(screenX, screenY, final.Value.Width, final.Value.Height);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await TranslationFlow.RunOcrAndDispatchAsync(cropped!, settings, anchor)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("后台翻译异常", ex);
                            Dispatcher.UIThread.Post(() =>
                            {
                                StickerToastWindow.ShowAtTop("翻译失败: " + ex.Message);
                            });
                        }
                        finally
                        {
                            try { cropped?.Dispose(); } catch { }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("CaptureSession.TriggerTranslateForSelection 初始化异常", ex);
                tcs.TrySetResult(null); // 同 OCR：撤幕之后异常若不落定会话，await 永挂、热键此后再不响应
                try { cropped?.Dispose(); } catch { }
            }
        }

        /// <param name="forcePrompt">交互流程强制覆盖：true=强制弹提问窗，null=跟随设置（AiUseDefaultPrompt）。</param>
        void TriggerAiVisionForSelection(bool? forcePrompt = null)
        {
            Rectangle? final = ResolveFinalRect();

            if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0)
            {
                return;
            }

            Bitmap? cropped = null;
            try
            {
                HideCaptureWindowForCompletion(window);
                cropped = CreateCroppedResultBitmap(frame, final.Value, annotations, window.TextLayers, "AI识图");
                if (cropped == null)
                {
                    RestoreCaptureWindowForCompletion(window);
                    return;
                }

                // 框已经裁成图交给下游（文字识别／翻译／AI 识图），按"截图成功"记一格。
                // 下面这句的结果是 null，语义只是"这次没有落盘文件"，不是"用户放弃了截图"。
                Core.Capture.SelectionHistory.Push(final.Value);
                OutputHandedOff = true;
                tcs.TrySetResult(null);

                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        var settings = AppSettings.Load();
                        string? customPrompt = null;

                        bool askPrompt = forcePrompt ?? !settings.AiUseDefaultPrompt;
                        if (askPrompt)
                        {
                            var promptWin = new AiPromptInputWindow();
                            promptWin.Show();
                            promptWin.Activate();

                            var tcsPrompt = new TaskCompletionSource<bool>();
                            promptWin.Closed += (_, _) => tcsPrompt.TrySetResult(promptWin.IsConfirmed);
                            bool confirmed = await tcsPrompt.Task;

                            if (!confirmed)
                            {
                                return;
                            }

                            if (!string.IsNullOrWhiteSpace(promptWin.ResultPrompt))
                            {
                                customPrompt = promptWin.ResultPrompt;
                            }
                        }

                        var aiWin = new AiVisionResultWindow();
                        aiWin.SetTargetImage(cropped, customPrompt);
                        aiWin.Show();
                        aiWin.Activate();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("打开 AI 识图窗口异常", ex);
                    }
                    finally
                    {
                        cropped.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("CaptureSession.TriggerAiVisionForSelection 初始化异常", ex);
                tcs.TrySetResult(null); // 同 OCR/翻译：撤幕之后异常若不落定会话，await 永挂、热键此后再不响应
                try { cropped?.Dispose(); } catch { }
            }
        }

        void ConfirmCurrentSelection()
        {
            if (string.Equals(ScriptAction, "Ocr", StringComparison.OrdinalIgnoreCase))
            {
                // 「截图并OCR（弹窗）」：强制打开 OCR 结果窗口（不跟随设置的输出模式）
                TriggerOcrForSelection("Window");
            }
            else if (string.Equals(ScriptAction, "OcrToast", StringComparison.OrdinalIgnoreCase))
            {
                // 「截图并OCR（仅提示）」：结果写入剪贴板 + 提示条，不弹大窗
                TriggerOcrForSelection("Toast");
            }
            else if (string.Equals(ScriptAction, "Vision", StringComparison.OrdinalIgnoreCase))
            {
                // 「截图并识图（默认）」：交互流程跟随设置（直接识图 / 每次提问）
                TriggerAiVisionForSelection(null);
            }
            else if (string.Equals(ScriptAction, "VisionPrompt", StringComparison.OrdinalIgnoreCase))
            {
                // 「截图并直接识图（提问）」：强制弹出提问窗口
                TriggerAiVisionForSelection(true);
            }
            else if (string.Equals(ScriptAction, "Translate", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScriptAction, "TranslateImageToImage", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScriptAction, "TranslateImageToText", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScriptAction, "TranslateSilent", StringComparison.OrdinalIgnoreCase))
            {
                TriggerTranslateForSelection();
            }
            else if (string.Equals(ScriptAction, "Matting", StringComparison.OrdinalIgnoreCase))
            {
                TriggerMattingForSelection();
            }
            else if (string.Equals(ScriptAction, "MattingGeneral", StringComparison.OrdinalIgnoreCase))
            {
                TriggerMattingForSelection(MattingModelType.General);
            }
            else if (string.Equals(ScriptAction, "MattingPortrait", StringComparison.OrdinalIgnoreCase))
            {
                TriggerMattingForSelection(MattingModelType.Portrait);
            }
            else if (string.Equals(ScriptAction, "Record", StringComparison.OrdinalIgnoreCase))
            {
                // 脚本/热键动作「开始 / 取消录制」：框完区自动展开工具栏与录屏参数条（消费掉一次性 ScriptAction，
                // 避免后续在选区界面点确认时重复触发 ToggleRecordTool）
                ScriptAction = null;
                if (selection.HasSelection)
                {
                    UpdateSelectionVisualImmediate(selection.Region, true);
                }
                ToggleRecordTool();
            }
            else if (string.Equals(ScriptAction, "RecordQuick", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScriptAction, "QuickRecord", StringComparison.OrdinalIgnoreCase))
            {
                // 全局动作「快速录制（框选即录）」：框选松手跳过工具栏与参数条，直接按当前设置启动录制
                ScriptAction = null;
                StartRecordFromCurrentSelection();
            }
            else if (string.Equals(ScriptAction, "Pin", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ScriptAction, "Sticker", StringComparison.OrdinalIgnoreCase))
            {
                CompleteCurrentSelection(CaptureCompletionAction.Sticker);
            }
            else if (string.Equals(ScriptAction, "Save", StringComparison.OrdinalIgnoreCase))
            {
                CompleteCurrentSelection(CaptureCompletionAction.Save);
            }
            else
            {
                CompleteCurrentSelection(CaptureCompletionAction.Clipboard);
            }
        }

        void SaveCurrentSelection()
        {
            CompleteCurrentSelection(CaptureCompletionAction.Save);
        }

        async void SaveAsCurrentSelectionAsync()
        {
            Rectangle? final = ResolveFinalRect();
            if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0) return;

            string? chosenPath = await PickSaveAsPathAsync(window);
            if (string.IsNullOrWhiteSpace(chosenPath))
            {
                // 用户在保存对话框中点击了取消，保持当前截图状态不变
                return;
            }

            StopAutoHoverAnimation();
            window.SetActiveTool(null);
            window.Annotation.ClearPreviewShape();
            window.Annotation.SetPointerFootprint(null, 0);
            SettleSuccess(new CaptureCompletion(final.Value, CaptureCompletionAction.SaveAs, pickedColorHex: chosenPath));
        }

        void ShowLongScrollMessage(string message)
        {
            try
            {
                NativeMethods.MessageBoxW(window.GetWindowHwnd(), message, "长截图", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
            }
            catch
            {
                AppLogger.Warn("CaptureSession.StartLongScroll: 提示失败，message=" + message);
            }
        }

        void StartLongScrollFromCurrentSelection(ScrollAxis axis)
        {
            if (pathEditor.Mode != CaptureSelectionMode.Rectangle) { ShowLongScrollMessage("长截图需要矩形选区，请先切换到矩形模式。"); return; }
            if (longScrollStarting) return;

            Rectangle? final = ResolveFinalRect();

            if (!final.HasValue || final.Value.Width <= 1 || final.Value.Height <= 1)
            {
                ShowLongScrollMessage("请先框选截图区域后再启动长截图。");
                return;
            }

            if (!MonitorHelper.IsRegionOnSingleMonitor(final.Value))
            {
                ShowLongScrollMessage("长截图暂不支持跨屏区域，请将选区限制在单显示器内。");
                return;
            }

            StopAutoHoverAnimation();
            longScrollStarting = true;
            activeAnnotationTool = AnnotationTool.None;
            drawingStroke = false;
            drawingArrow = false;
            drawingRectangle = false;
            drawingMosaic = false;
            drawingSpotlight = false;
            CancelEraserGesture();
            draggingCounterTail = false;
            currentStrokePoints.Clear();
            window.PrepareForLongScrollTransition();
            try
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.StartLongScroll: 隐藏 CaptureWindow，准备启动长截图。Axis=" + axis + ", Region=" + final.Value);
                window.Hide();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureSession.StartLongScroll: 隐藏 CaptureWindow 失败：" + ex.Message);
            }
            SettleSuccess(new CaptureCompletion(final.Value, CaptureCompletionAction.LongScroll, axis: axis));
        }

        void SyncRecordShortcut(object? sender, EventArgs args)
        {
            System.Threading.Interlocked.Exchange(ref _recordShortcutWindow, window.GetWindowHwnd());
            _recordToolSelected = window.ActiveToolKey == "Record";
            UpdateRecordingShortcutRegistration();
        }
        _recordShortcutBinding = BuiltinShortcutsStore.LoadGroupMap(BuiltinShortcutsStore.GroupRecord)[BuiltinShortcutsStore.RecordToggleId];
        window.ActiveToolChanged += SyncRecordShortcut;
        SyncRecordShortcut(null, EventArgs.Empty);
        _detachRecordShortcut = () =>
        {
            window.ActiveToolChanged -= SyncRecordShortcut;
            _recordShortcutRegistration?.Dispose();
            _recordShortcutRegistration = null;
        };

        /// <summary>稿1 校验提示：钉黑胶囊 toast（复用 StickerToastWindow，锚在遮罩窗中央）。</summary>
        void ShowRecordMessage(string message)
        {
            try
            {
                StickerToastWindow.Show(message, window);
            }
            catch
            {
                AppLogger.Warn("CaptureSession.StartRecord: 提示失败，message=" + message);
            }
        }

        /// <summary>工具栏录屏图标点击＝切换：已激活收起参数条，未激活进入录屏模式。</summary>
        void ToggleRecordTool()
        {
            if (string.Equals(window.ActiveToolKey, "Record", StringComparison.OrdinalIgnoreCase))
            {
                window.SetActiveTool(null);
                return;
            }
            // 与标注工具互斥：进录屏模式收走进行中的画笔工具（参数条只露录屏参数）
            activeAnnotationTool = AnnotationTool.None;
            window.SetActiveTool("Record");
        }

        /// <summary>
        /// 「开始录制」按钮 / Enter / 脚本注入的统一入口：校验选区 → 前置确认 FFmpeg 就绪 →
        /// 隐藏遮罩窗（不关！倒计时取消要回 S02）→ 交给 <see cref="RecordFlow.Begin"/>；
        /// 只有真正起跑录制才落定 tcs（遮罩窗随之 Close）。
        /// </summary>
        async void StartRecordFromCurrentSelection()
        {
            if (pathEditor.Mode != CaptureSelectionMode.Rectangle) { ShowRecordMessage("录屏需要矩形选区，请先切换到矩形模式。"); return; }
            if (_recordPreparing || _recordStarting || RecordFlow.IsBusy) return;

            Rectangle? final = ResolveFinalRect();

            if (!final.HasValue || final.Value.Width <= 1 || final.Value.Height <= 1)
            {
                ShowRecordMessage("请先框选一个屏幕区域");
                return;
            }

            if (!MonitorHelper.IsRegionOnSingleMonitor(final.Value))
            {
                if (selection.HasSelection)
                {
                    UpdateSelectionVisualImmediate(selection.Region, true);
                    if (window.ActiveToolKey != "Record") window.SetActiveTool("Record");
                }
                ShowRecordMessage("录屏暂不支持跨屏区域，请在单屏内框选");
                return;
            }

            var opt = window.BuildRecordOptions();
            bool useCustomFfmpeg = !string.IsNullOrWhiteSpace(opt.ManualFfmpegPath);
            if (opt.Engine == Core.Recording.RecordEngine.Ffmpeg
                && (useCustomFfmpeg || !Core.Deps.DependencyManager.IsReady(Core.Deps.DepIds.Ffmpeg)))
            {
                bool ready;
                _recordPreparing = true;
                try
                {
                    ready = useCustomFfmpeg
                        ? await Task.Run(() => Core.Recording.FfmpegLocator.TryFind(opt.ManualFfmpegPath) != null)
                        : await EnsureDependencyOnCaptureWindowAsync(window, Core.Deps.DepIds.Ffmpeg, "FFmpeg 录屏引擎未就绪，录制已取消");
                }
                finally { _recordPreparing = false; }
                if (!window.IsVisible || _recordStarting || RecordFlow.IsBusy) return;
                if (!ready)
                {
                    if (useCustomFfmpeg) ShowRecordMessage(Core.Recording.FfmpegLocator.UnavailableMessage(opt.ManualFfmpegPath));
                    return;
                }
                final = (hasManualSelection && selection.HasSelection) ? selection.Region : final;
                if (!final.HasValue || final.Value.Width <= 1 || final.Value.Height <= 1) return;
                if (!MonitorHelper.IsRegionOnSingleMonitor(final.Value))
                {
                    ShowRecordMessage("录屏暂不支持跨屏区域，请在单屏内框选");
                    return;
                }
            }

            opt.Region = final.Value;

            _recordStarting = true;
            StopAutoHoverAnimation();
            window.Annotation.ClearPreviewShape();
            window.Annotation.SetPointerFootprint(null, 0);
            try
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.StartRecord: 隐藏 CaptureWindow，进入录屏流程。Region=" + final.Value);
                window.Hide();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureSession.StartRecord: 隐藏 CaptureWindow 失败：" + ex.Message);
            }

            void BackToSelectionUi(string? error)
            {
                _recordStarting = false;
                try
                {
                    window.Show();
                    if (selection.HasSelection)
                    {
                        UpdateSelectionVisualImmediate(selection.Region, true);
                        if (window.ActiveToolKey != "Record")
                        {
                            window.SetActiveTool("Record");
                        }
                    }
                }
                catch { }
                if (error != null)
                {
                    NativeMethods.MessageBoxW(window.GetWindowHwnd(), error, "录屏",
                        NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
                }
            }

            RecordFlow.Begin(
                opt,
                format: window.RecordFormatChoice,
                dpiScale: window.DesktopScaling,
                onRecordingStarted: () => SettleSuccess(new CaptureCompletion(final.Value, CaptureCompletionAction.Record)),
                onCountdownCancelled: () => BackToSelectionUi(null),
                onStartFailed: err => BackToSelectionUi(err));
        }

        void CreateStickerFromCurrentSelection()
        {
            CompleteCurrentSelection(CaptureCompletionAction.Sticker);
        }

        async void TriggerMattingForSelection(MattingModelType? modelType = null)
        {
            Rectangle? final = ResolveFinalRect();
            if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0)
            {
                return;
            }

            // 前置拦截：智能抠图模型未就绪时在选区界面就地弹窗下载，下载完成后自动继续抠图，取消则保留选区
            if (!Core.Deps.DependencyManager.IsReady(Core.Deps.DepIds.Matting))
            {
                bool ready = await EnsureDependencyOnCaptureWindowAsync(window, Core.Deps.DepIds.Matting, "抠图模型未就绪，抠图已取消");
                if (!ready || !window.IsVisible) return;
                final = (hasManualSelection && selection.HasSelection) ? selection.Region : final;
                if (!final.HasValue || final.Value.Width <= 0 || final.Value.Height <= 0) return;
            }

            CompleteCurrentSelection(CaptureCompletionAction.Matting, mattingModelType: modelType, overrideRect: final);
        }

        void CancelCapture()
        {
            // 全局拖框的 Esc／不足阈值松手先撤遮罩，参数落盘与窗口清理随后按原流程完成。
            if (MouseGesture != null) HideCaptureWindowForCompletion(window);
            StopAutoHoverAnimation();
            isSpacePressed = false;
            window.Cursor = null;
            window.SetActiveTool(null);
            window.Annotation.ClearPreviewShape();
            try
            {
                window.CaptureAnnotationDefaults(_settings);
                _settings.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CancelCapture: 保存美化与标注参数失败: " + ex.Message);
            }
            tcs.TrySetResult(null);
        }

        bool IsPointInsideManualSelection(System.Drawing.Point pt)
        {
            return hasManualSelection && selection.HasSelection && selection.Region.Contains(pt);
        }

        static HandleDirection GetExpandResizeDirection(System.Drawing.Point pt, Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return HandleDirection.None;
            bool left = pt.X <= rect.Left;
            bool right = pt.X >= rect.Right;
            bool top = pt.Y <= rect.Top;
            bool bottom = pt.Y >= rect.Bottom;

            if (top && left) return HandleDirection.TopLeft;
            if (top && right) return HandleDirection.TopRight;
            if (bottom && right) return HandleDirection.BottomRight;
            if (bottom && left) return HandleDirection.BottomLeft;
            if (top) return HandleDirection.Top;
            if (right) return HandleDirection.Right;
            if (bottom) return HandleDirection.Bottom;
            if (left) return HandleDirection.Left;
            return HandleDirection.None;
        }

        void ExpandSelectionToPoint(System.Drawing.Point pt)
        {
            if (!selection.HasSelection) return;
            Rectangle rect = selection.Region;
            int left = Math.Min(rect.Left, pt.X);
            int top = Math.Min(rect.Top, pt.Y);
            int right = Math.Max(rect.Right, pt.X);
            int bottom = Math.Max(rect.Bottom, pt.Y);
            if (right - left <= 0 || bottom - top <= 0) return;
            selection.SetRegion(Rectangle.FromLTRB(left, top, right, bottom));
        }

        bool TryExpandSelectionByMaskClick(System.Drawing.Point pt)
        {
            if (!hasManualSelection || !selection.HasSelection) return false;
            if (activeAnnotationTool != AnnotationTool.None) return false;
            if (selection.Region.Contains(pt)) return false;

            HandleDirection direction = GetExpandResizeDirection(pt, selection.Region);
            ExpandSelectionToPoint(pt);
            pendingExpandResize = direction != HandleDirection.None;
            pendingExpandResizeDirection = direction;
            pendingExpandStartPoint = pt;
            pendingExpandStartRect = selection.Region;
            return true;
        }

        static Rectangle InflateSelection(Rectangle rect, int delta)
        {
            int left = rect.Left - delta;
            int top = rect.Top - delta;
            int right = rect.Right + delta;
            int bottom = rect.Bottom + delta;
            if (right - left <= 4 || bottom - top <= 4) return rect;
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        static string? ToToolKey(AnnotationTool tool)
        {
            return tool switch
            {
                AnnotationTool.Brush => "Brush",
                AnnotationTool.Arrow => "Arrow",
                AnnotationTool.Rectangle => "Rectangle",
                AnnotationTool.Text => "Text",
                AnnotationTool.Mosaic => "Mosaic",
                AnnotationTool.Spotlight => "Spotlight",
                AnnotationTool.Counter => "Counter",
                AnnotationTool.Eraser => "Eraser",
                _ => null,
            };
        }

        void RecordLastAnnotationTool(AnnotationTool tool)
        {
            if (tool != AnnotationTool.None)
            {
                string? key = ToToolKey(tool);
                if (!string.IsNullOrEmpty(key))
                {
                    _settings.LastAnnotationTool = key;
                }
            }
        }

        /// <summary>把持久化的"上次标注工具"键名解析为工具枚举；无记录时返回 None。</summary>
        AnnotationTool ResolveLastAnnotationTool()
        {
            if (string.IsNullOrWhiteSpace(_settings.LastAnnotationTool)) return AnnotationTool.None;
            return _settings.LastAnnotationTool switch
            {
                "Brush" => AnnotationTool.Brush,
                "Arrow" => AnnotationTool.Arrow,
                "Rectangle" => AnnotationTool.Rectangle,
                "Text" => AnnotationTool.Text,
                "Mosaic" => AnnotationTool.Mosaic,
                "Spotlight" => AnnotationTool.Spotlight,
                "Counter" => AnnotationTool.Counter,
                "Eraser" => AnnotationTool.Eraser,
                _ => AnnotationTool.None,
            };
        }

        void TryApplyLastAnnotationTool()
        {
            if (!_settings.ReuseLastAnnotationTool) return;
            if (!hasManualSelection || !selection.HasSelection) return;

            AnnotationTool toolToApply = ResolveLastAnnotationTool();
            if (toolToApply != AnnotationTool.None)
            {
                ForceAnnotationTool(toolToApply);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession: 已自动沿用上次标注工具=" + toolToApply);
            }
        }

        void SetAnnotationTool(AnnotationTool tool)
        {
            if (!hasManualSelection || !selection.HasSelection) return;
            activeAnnotationTool = activeAnnotationTool == tool ? AnnotationTool.None : tool;
            ResetActiveAnnotationGestures();
            currentStrokePoints.Clear();
            freehandStrokePoints.Clear();
            isBrushShiftConstrained = false;
            window.Annotation.ClearPreviewShape();
            brushPointerDip = null;
            window.Annotation.SetPointerFootprint(null, 0);
            if (tool != AnnotationTool.Mosaic)
            {
                SelectBlurShape(null);
                window.Annotation.HoverAutoMosaicHighlight = null;
            }
            if (tool != AnnotationTool.Spotlight)
            {
                SelectSpotlightShape(null);
            }
            window.Cursor = null;
            window.SetActiveTool(ToToolKey(activeAnnotationTool));
            // 通知窗口文本工具是否激活
            window.IsTextToolActive = (activeAnnotationTool == AnnotationTool.Text);
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.SetAnnotationTool: 当前工具=" + activeAnnotationTool);
            if (activeAnnotationTool != AnnotationTool.None)
            {
                RecordLastAnnotationTool(activeAnnotationTool);
            }
        }

        void ForceAnnotationTool(AnnotationTool tool)
        {
            if (!hasManualSelection || !selection.HasSelection) return;
            activeAnnotationTool = tool;
            ResetActiveAnnotationGestures();
            currentStrokePoints.Clear();
            freehandStrokePoints.Clear();
            isBrushShiftConstrained = false;
            window.Annotation.ClearPreviewShape();
            brushPointerDip = null;
            window.Annotation.SetPointerFootprint(null, 0);
            if (tool != AnnotationTool.Mosaic)
            {
                SelectBlurShape(null);
                window.Annotation.HoverAutoMosaicHighlight = null;
            }
            window.Cursor = null;
            window.SetActiveTool(ToToolKey(activeAnnotationTool));
            window.IsTextToolActive = (activeAnnotationTool == AnnotationTool.Text);
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.ForceAnnotationTool: 当前工具=" + activeAnnotationTool);
            if (activeAnnotationTool != AnnotationTool.None)
            {
                RecordLastAnnotationTool(activeAnnotationTool);
            }
        }

        void ResetActiveAnnotationGestures()
        {
            // 本方法是"切工具"清空全部手势标志位的唯一出口。途中换工具必须先把手势提交掉（补落最后一帧 + 写回文档），
            // 否则调用方紧随其后的 ClearPreviewShape 会把整段拖动丢掉、图元回弹到起手位置（issue #13 阶段 1 的"只推迟不丢弃"）。
            CommitShapeGestureMove();
            drawingStroke = false;
            isBrushShiftConstrained = false;
            freehandStrokePoints.Clear();
            drawingArrow = false;
            drawingRectangle = false;
            drawingMosaic = false;
            drawingSpotlight = false;
            CancelEraserGesture();
            // 引线手势随工具一并作废：已经建好的徽章留着（＝升级前的单击建号），只丢弃没松手的那段拖动
            draggingCounterTail = false;
            // 手势状态（11 个标志位 + 8 个起手量 + 3 个把手方向）全收在共享编辑器里，一次 Cancel 清空。
            // Commit 已经清过一遍，这里是"没在编辑却切了工具"那条路径的兜底，顺带抹掉残留预览。
            shapeEditor.Cancel();
            click1CreatedAnnotation = false;
            click1IsBrushCandidate = false;
        }

        // 纯几何与手柄命中已上提到 Core（AnnotationGeometry / AnnotationHitTest，截图遮罩与贴图标注共用一份实现）：
        // 这里只保留同名局部函数转发，既有调用点与容差默认值（截图＝1:1 屏幕像素）一律不动。
        static RectangleF NormalizeRect(PointF start, PointF end) => AnnotationGeometry.NormalizeRect(start, end);

        // 锁 1:1 的算式本体在 Core（截图/贴图共用，2026-09-20「形状 Shift 锁比例」时上提），
        // 本函数只把"有没有选区"翻成 bounds。
        RectangleF CreateDrawRect(PointF start, PointF end, bool forceSquare)
            => forceSquare
                ? AnnotationGeometry.SquareFromDrag(start, end,
                    selection.HasSelection
                        ? new RectangleF(selection.Region.X, selection.Region.Y,
                            selection.Region.Width, selection.Region.Height)
                        : null)
                : NormalizeRect(start, end);

        static bool Contains(RectangleF rect, PointF pt) => AnnotationHitTest.Contains(rect, pt);

        // 聚光灯的夹取范围＝选区（贴图侧传的是干净底图的内容矩形，同一个算式两种 bounds）。
        // 无选区＝不夹，那是截图第一阶段的全屏聚光灯，本来就铺满整屏。
        RectangleF? SpotlightClampBounds()
            => selection.HasSelection
                ? new RectangleF(selection.Region.X, selection.Region.Y, selection.Region.Width, selection.Region.Height)
                : null;

        // 夹取算法本身在 Core（截图/贴图共用），本函数只把"有没有选区"翻成 bounds。
        RectangleF ClampSpotlightRect(RectangleF rect)
            => SpotlightClampBounds() is RectangleF b ? AnnotationGeometry.ClampRect(rect, b) : rect;
        static RectangleF ExpandAutoMosaicRect(RectangleF rect, Rectangle bounds)
        {
            float padX = Math.Clamp(rect.Width * 0.12f, 4.0f, 10.0f);
            float padY = Math.Clamp(rect.Height * 0.22f, 4.0f, 8.0f);
            return RectangleF.FromLTRB(
                Math.Max(bounds.Left, rect.Left - padX),
                Math.Max(bounds.Top, rect.Top - padY),
                Math.Min(bounds.Right, rect.Right + padX),
                Math.Min(bounds.Bottom, rect.Bottom + padY));
        }

        /// <summary>按唯一选中 id 现取图元本体；图元已被撤销/清除掉时为 null。</summary>
        AnnotationShape? GetSelectedShape()
        {
            if (!selectedShapeId.HasValue) return null;
            return annotations.Shapes.FirstOrDefault(x => x.Id == selectedShapeId.Value);
        }

        /// <summary>
        /// 选中/取消选中的唯一写入口：簿记 id、图层高亮与重绘三件一起改，禁止在别处散写。
        ///
        /// <para>末尾那一段是"选中谁、参数条就回灌显示谁"的唯一落点（2026-09-19）：把这一颗图元自己的样式
        /// 摆回参数条，于是用户在条上看到的读数与画布上选中的那个一致，改其中一档也只改到它身上。
        /// 模糊与聚光灯不在这里——它们各自的 <see cref="SelectBlurShape"/> / <see cref="SelectSpotlightShape"/>
        /// 已经在做同一件事，且带专属的抑制位与"暗度同步全部"的语义，不并进这张表。</para>
        /// </summary>
        void SelectShape(Guid? id)
        {
            selectedShapeId = id;
            window.Annotation.SelectedShapeId = id;
            window.Annotation.InvalidateVisual();
            switch (GetSelectedShape())
            {
                case ArrowShape arrow: window.SyncArrowToolState(arrow); break;
                case RectangleShape rect: window.SyncShapeToolState(rect); break;
                case CounterShape counter: window.SyncCounterToolState(counter); break;
                case StrokeShape stroke: window.SyncStrokeToolColor(stroke.Color); break;
            }
        }

        // 按类型取的都只是同一份 id 的视图，不再各自存一份簿记
        BlurShape? GetSelectedBlurShape() => GetSelectedShape() as BlurShape;
        SpotlightShape? GetSelectedSpotlightShape() => GetSelectedShape() as SpotlightShape;

        void SelectBlurShape(Guid? id)
        {
            SelectShape(id);
            if (GetSelectedBlurShape() is BlurShape blur)
            {
                window.SyncBlurToolState(blur.Mode, blur.UiLevel);
            }
        }

        void ReplaceSelectedBlurShape(BlurShape next)
        {
            annotations.ReplaceShape(next);
            SelectBlurShape(next.Id);
        }

        SpotlightShape? FindSpotlightShapeAt(PointF pt) => AnnotationHitTest.FindSpotlightShapeAt(annotations, pt);

        void SelectSpotlightShape(Guid? id)
        {
            SelectShape(id);
            if (GetSelectedSpotlightShape() is SpotlightShape spotlight)
            {
                window.SyncSpotlightToolState(spotlight);
            }
        }

        /// <summary>
        /// 参数条某一档被用户改动 → <b>只把这一档</b>落到当前选中的图元上（2026-09-20 全线补齐）。
        ///
        /// <para>九支事件（颜色／粗细／形状档／形状线型／填充／箭头头型／序号样式／序号大小／引线箭头）
        /// 共用这一条通路，分派表在 <see cref="AnnotationStyleApplier"/>，贴图侧读的也是同一张表。
        /// <paramref name="parts"/> 必须只带被点那一两档：一次动作把整把样式灌下去，就会把用户没碰的档
        /// 静默换成工具栏记忆值（选中虚线矩形去点「形状」即中招）。</para>
        ///
        /// <para>分派表返回 null（这一档在该图元上没有字段，或比过字段确实没变）时<b>整段跳过</b>——
        /// 先压快照再改的写法会在撤销栈里留下一条空记录，一次点击就"撤销一次什么也没发生"。</para>
        ///
        /// <para>末尾走 <see cref="SelectShape"/> 而不是直接 <c>InvalidateVisual</c>：既保住选中不丢
        /// （选中一丢，浮条当场收起、滑条也拖不动了），也把改完的图元再回灌一次参数条。
        /// 椭圆同样吃填充，故填充那一档不按 <c>ShapeKind</c> 设限（只有圆角那一档才需要挡椭圆，见
        /// <see cref="ApplyShapeRadiusToSelection"/>）。</para>
        /// </summary>
        void ApplyStyleToSelection(AnnotationStylePart parts)
        {
            AnnotationShape? selected = GetSelectedShape();
            if (selected == null) return;
            AnnotationShape? next = AnnotationStyleApplier.Apply(selected, window.GetAnnotationGestureSettings(), parts);
            if (next == null) return;
            captureHistory.CommitSnapshot();
            annotations.ReplaceShape(next);
            SelectShape(next.Id);
        }

        /// <summary>
        /// 把类型档（123 / ABC / ⅠⅡⅢ / 一二三）换到**图上已有的全部序号**上——其余各档最多只改选中的那一颗，
        /// 因为类型是显示层的事：同一个编号换类型，留一半旧样式就等于一张图上两套编号体系。
        /// 先确认确实有图元要改再压快照，否则一次点击会在撤销栈里留一条空记录。
        /// </summary>
        void ApplyCounterSerialKindToAll(CounterSerialKind kind)
        {
            if (!annotations.Shapes.OfType<CounterShape>().Any(c => c.Serial != kind)) return;
            captureHistory.CommitSnapshot();
            int changed = annotations.ApplyCounterSerialKind(kind);
            window.Layer.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CounterSerial: 已把 " + changed + " 个序号换成 " + kind);
        }

        /// <summary>
        /// 改<b>这一个</b>序号的编号：键盘 +/− 与选中后浮条的 − / + 两条入口汇到这一处，
        /// 免得"撞号只提示不阻止"这类口径在两处各写一遍、日后只改一处。
        /// </summary>
        void StepCounterNumber(CounterShape counter, int delta)
        {
            // 1 是下限：编号从 1 起算，减到 0 会显示成空标签。
            int nextNumber = Math.Max(1, counter.Number + delta);
            if (nextNumber == counter.Number) return;
            captureHistory.CommitSnapshot();
            annotations.ReplaceShape(counter.WithNumber(nextNumber));
            window.Annotation.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.CounterStep: 调整编号={nextNumber}, id={counter.Id}");
            // 撞号只提示不阻止：重号在教程图上是合法需求（两处都可能标"第 1 步"）。
            if (annotations.Shapes.Any(s => s is CounterShape other && other.Id != counter.Id && other.Number == nextNumber))
                { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CounterStep: 编号 " + nextNumber + " 已被另一个序号占用（按口径允许，仅提示）"); }
        }

        /// <summary>
        /// 把浮条里那格说明文字写进当前选中的序号（空串＝清除）。与改编号同一条落地口径；
        /// 值没变就整个跳过，不然一次失焦会在撤销栈里留一条空记录。
        /// </summary>
        void SetSelectedCounterNote(string note)
        {
            if (GetSelectedShape() is not CounterShape counter) return;
            if (counter.Note == note) return;
            captureHistory.CommitSnapshot();
            annotations.ReplaceShape(counter.WithNote(note));
            window.Annotation.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CounterNote: 编号 " + counter.Label + " 的说明文字＝"
                + (note.Length == 0 ? "（已清除）" : "\"" + note + "\"") + ", id=" + counter.Id);
        }

        /// <summary>
        /// 把浮条「圆角」那一格改的值（百分比 0–100）落到当前选中的**胶囊**序号上。
        ///
        /// <para>与 <see cref="ApplyShapeRadiusToSelection"/> 同一条口径：每改一档压一条快照。截图侧的
        /// 历史栈本来就是这个粒度（圆角浮条、聚光灯圆角都如此），故"一段拖动＝一条撤销"在序号这里同样
        /// 做不到——这是**已知且刻意沿用**的既有行为，不是本条通路新引入的偏差。</para>
        ///
        /// <para>只认胶囊外形：其余外形档读不到这个字段，改了也不会有任何画面变化，那就是一次空快照。
        /// 值没变同样整段跳过，理由相同。</para>
        /// </summary>
        void SetSelectedCounterCapsuleCorner(int corner)
        {
            if (GetSelectedShape() is not CounterShape { Glyph: CounterGlyphShape.Capsule } counter) return;
            float next = Math.Clamp((float)corner, 0f, 100f);
            if (Math.Abs(counter.CapsuleCorner - next) < 0.01f) return;
            captureHistory.CommitSnapshot();
            annotations.ReplaceShape(counter.WithCapsuleCorner(next));
            window.Annotation.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CounterCorner: 编号 " + counter.Label + " 的胶囊圆角＝"
                + next.ToString("0") + ", id=" + counter.Id);
        }

        /// <summary>
        /// 把浮条「→」那一格翻到当前选中的序号上：藏掉／画出**整条引线**。
        ///
        /// <para>只翻 <see cref="CounterShape.TailShown"/> 这一个开关，引线端点 <c>Tail</c> 原样留在图元里——
        /// 藏掉只是不画、也点不中（预览/导出/命中三处都从 <c>GetCounterTailPoints</c> 那一个出口走），
        /// 再点一次按原样回来，不会因为"藏"而永久丢失用户拖出来的那个方向。</para>
        ///
        /// <para>浮条那一格只在有引线时出现；这里的 <c>Tail is null</c> 守的是另一条来路——撤销/重做按 id
        /// 换人后，选中可能落到一颗没有引线的序号上，那时翻开关就是一次空快照。</para>
        /// </summary>
        void ToggleSelectedCounterTail()
        {
            if (GetSelectedShape() is not CounterShape counter || counter.Tail is null) return;
            bool nextShown = !counter.TailShown;
            captureHistory.CommitSnapshot();
            annotations.ReplaceShape(counter.WithTailShown(nextShown));
            window.Annotation.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CounterTail: 编号 " + counter.Label + " 的引线＝"
                + (nextShown ? "显示" : "藏起") + ", id=" + counter.Id);
        }

        /// <summary>
        /// 把宿主记忆里的矩形圆角落到当前选中的**矩形图元**上（圆角浮条改值 → 这里）。
        ///
        /// <para>与 <see cref="ApplySpotlightStyleToSelection"/> 逐条对称：先压一次快照（一段拖动＝一条撤销），
        /// 再重建图元并保持选中——选中不能丢，否则浮条会因为"没有选中图元"而当场收起，拖动就断了。</para>
        ///
        /// <para>只动半径、不动描边/线型等其余样式：浮条只服务圆角一件事，别的字段必须原样透传
        /// （<c>WithCornerRadius</c> 正是这个语义，故不重建整个形状）。</para>
        ///
        /// <para>没有选中矩形时什么都不做——记忆字段本身已经被宿主写好了，它会成为"下一笔矩形"的默认圆角
        /// （与滑块时代同一条语义）。椭圆档没有圆角概念，浮条本就不会显示，这里再挡一道。</para>
        /// </summary>
        void ApplyShapeRadiusToSelection()
        {
            if (GetSelectedShape() is not RectangleShape { ShapeKind: ShapeAnnotationKind.Rectangle } rect) return;
            captureHistory.CommitSnapshot();
            RectangleShape next = rect.WithCornerRadius(window.SelectedShapeCornerRadius);
            annotations.ReplaceShape(next);
            SelectShape(next.Id);
        }


        void ApplySpotlightStyleToSelection()
        {
            SpotlightShape? selected = GetSelectedSpotlightShape();
            bool hasSpotlights = annotations.Shapes.OfType<SpotlightShape>().Any();
            if (!hasSpotlights) return;
            captureHistory.CommitSnapshot();
            ApplySpotlightStyleWithoutSnapshot(selected?.Id);
        }

        void ApplySpotlightStyleWithoutSnapshot(Guid? selectedId)
        {
            float darkness = window.SelectedSpotlightDarkness;
            if (selectedId.HasValue)
            {
                // 只有被选中的聚光灯才修改描边颜色、粗细、形状类别和圆角；未选中的聚光灯保持各自原有样式。
                if (annotations.Shapes.OfType<SpotlightShape>().FirstOrDefault(s => s.Id == selectedId.Value) is SpotlightShape spotlight)
                {
                    SpotlightShape next = spotlight.WithStyle(
                        darkness,
                        window.SelectedSpotlightKind,
                        window.SelectedAnnotationColor,
                        window.SelectedSpotlightStrokeThickness,
                        window.SelectedSpotlightCornerRadius);
                    annotations.ReplaceShape(next);
                    SelectSpotlightShape(selectedId.Value);
                }
            }
            else
            {
                // 未选中任何一个聚光灯时：
                // 暗度作为全局环境光遮罩同步给已有聚光灯；
                // 描边颜色、粗细、形状类别和圆角保持各图元自身设置，工具栏值仅作为下一笔新建的记忆参数。
                foreach (SpotlightShape spotlight in annotations.Shapes.OfType<SpotlightShape>().ToArray())
                {
                    SpotlightShape next = spotlight.WithStyle(
                        darkness,
                        spotlight.ShapeKind,
                        spotlight.StrokeColor,
                        spotlight.StrokeThickness,
                        spotlight.CornerRadius);
                    annotations.ReplaceShape(next);
                }
                window.Annotation.InvalidateVisual();
            }
        }

        /// <summary>删除当前选中的图元，六类共用一条通路（原先每类一份，只差类型过滤与序号重编）。
        /// 返回 false＝没有选中项，或选中的 id 已随撤销/清除不在文档里。</summary>
        bool DeleteSelectedShape()
        {
            AnnotationShape? target = GetSelectedShape();
            if (target == null) return false;
            Guid id = target.Id;

            captureHistory.CommitSnapshot();
            int deletedNumber = target is CounterShape counter ? counter.Number : 0;
            annotations.RemoveShape(id);

            // 重新编号：所有大于被删序号的计数器递减 1，保持顺序连续（规则与贴图侧共用文档类那一份实现）。
            // 设置项关掉这一档＝保留原有编号不重排（删掉的号就此空出），两侧宿主读的是同一个开关。
            bool renumber = deletedNumber > 0 && _settings.CounterRenumberOnDelete;
            int renumbered = renumber ? annotations.RenumberCountersAfterDeletion(deletedNumber) : 0;

            SelectShape(null);
            window.Annotation.ClearPreviewShape();
            string kind = target switch
            {
                SpotlightShape => "聚光灯",
                BlurShape => "模糊/马赛克",
                StrokeShape => "画笔",
                ArrowShape => "箭头",
                RectangleShape => "矩形/椭圆",
                CounterShape => "序号",
                _ => "图元",
            };
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.DeleteSelectedShape: 已删除" + kind + " id=" + id
                + (deletedNumber > 0 ? " number=" + deletedNumber
                    + (renumber ? " 已重新编号 " + renumbered + " 个剩余序号" : "（按设置保留原编号）") : ""));
            return true;
        }

        void ClearAllSelections()
        {
            SelectShape(null);
            window.DeselectText();
        }

        bool AnyShapeSelected() => selectedShapeId.HasValue;

        /// <summary>
        /// 文本工具下 Ctrl+点击别的图元＝切过去并起一段编辑手势。派发链与截图其他入口完全同一条，
        /// 这里只是"文本层不参与本体命中"（文本已由调用处先行处理）的一个特例。
        /// </summary>
        bool TrySwitchFromTextToOtherAnnotation(PointerPressedEventArgs e, PointF pt)
            => TryBeginShapeEdit(e, pt, editExisting: true, _ => false);

        void SetSpotlightPreviewThrottled(SpotlightShape shape, bool force = false)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && now - lastSpotlightPreviewStamp < spotlightPreviewGateTicks) return;
            lastSpotlightPreviewStamp = now;
            window.Annotation.SetPreviewShape(shape);
        }

        // ===== 标注图元拖动的合帧闸门（issue #13 阶段 1）=====
        // 六个 move/resize 分支原先**每个输入事件**就把新图元写进文档：探针实测一条 600 点的笔迹
        // 每次事件 9.7 微秒、38824 字节（Translate 与 ReplaceShape 里的 Clone 各拷一份点阵），
        // 1000Hz 鼠标下＝38MB/秒托管分配，gen0 回收按十几次的节奏插进画面，就是"高回报率鼠标下拖动发顿"。
        // 闸门形式由探针定：先写的"把落文档 Post 到 DispatcherPriority.Render"版本经实测**完全无效**——
        // Render 档排在 Input 之上，1000 个排队输入事件之间会插进 1000 次回调，一次也没合掉，
        // 白搭一个委托分配。故改用本文件聚光灯既有的时间闸口径（见 SetSpotlightPreviewThrottled），
        // 上限与调度器语义无关。取 8ms 而非聚光灯的 16ms：本机的屏幕刷新率可能高于 60Hz，
        // 8ms＝至多 125 次/秒，在 120Hz 下仍是一帧一次，不会因为节流而比原来更不跟手。
        // **只推迟不丢弃**：被压住的那次一定会补落，松手与切工具两处先 Flush 一次，
        // 元素绝不会停在倒数第二帧上（那等于松手回弹）。
        // 计时用 Stopwatch 而非 Environment.TickCount64：后者的步进只到 ~15.6ms 一格，
        // 拿它做 8ms 判定会静默退化成 16ms，等于把上面的刷新率考量作废。
        // （聚光灯预览那段原先也用 TickCount64，已同批换成 Stopwatch，见 spotlightPreviewGateTicks。）
        long shapeGestureGateTicks = Stopwatch.Frequency / 125;  // 8ms
        PointF? pendingShapeGesturePoint = null;
        long lastShapeGestureApplyStamp = 0;

        void RequestShapeGestureMove(PointF pt)
        {
            pendingShapeGesturePoint = pt;
            if (Stopwatch.GetTimestamp() - lastShapeGestureApplyStamp < shapeGestureGateTicks) return;
            FlushShapeGestureMove();
        }

        /// <summary>无条件把压着的最后一次坐标落进图层预览；没有待落坐标时是空操作。</summary>
        void FlushShapeGestureMove()
        {
            PointF? pt = pendingShapeGesturePoint;
            if (pt == null) return;
            pendingShapeGesturePoint = null;
            lastShapeGestureApplyStamp = Stopwatch.GetTimestamp();
            shapeEditor.Apply(pt.Value);
        }

        /// <summary>
        /// 切工具／起新笔等"上一段手势被打断"的出口：闸门里还压着最后一帧就先补落，再让共享编辑器
        /// 把这一次性写回文档。一段手势＝一次文档写入＝一条撤销（protection.md「标注图元拖动只有一种提交语义」）。
        /// </summary>
        void CommitShapeGestureMove()
        {
            if (!shapeEditor.IsEditing) return;
            FlushShapeGestureMove();
            shapeEditor.Commit();
        }


        /// <summary>图元类型 → 该类型对应的标注工具（点中已有图元时把工具切过去，参数条随之展开）。</summary>
        static AnnotationTool ToolOfShape(AnnotationShape shape) => shape switch
        {
            SpotlightShape => AnnotationTool.Spotlight,
            BlurShape => AnnotationTool.Mosaic,
            CounterShape => AnnotationTool.Counter,
            ArrowShape => AnnotationTool.Arrow,
            RectangleShape => AnnotationTool.Rectangle,
            StrokeShape => AnnotationTool.Brush,
            _ => AnnotationTool.None,
        };

        /// <summary>当前标注工具 → 图元类型枚举（用于同工具免 Ctrl 点选判断）。</summary>
        static AnnotationShapeKind? ActiveShapeKindOfTool(AnnotationTool tool) => tool switch
        {
            AnnotationTool.Brush => AnnotationShapeKind.Stroke,
            AnnotationTool.Arrow => AnnotationShapeKind.Arrow,
            AnnotationTool.Rectangle => AnnotationShapeKind.Rectangle,
            AnnotationTool.Mosaic => AnnotationShapeKind.Blur,
            AnnotationTool.Spotlight => AnnotationShapeKind.Spotlight,
            AnnotationTool.Counter => AnnotationShapeKind.Counter,
            _ => null,
        };

        /// <summary>
        /// 起一段"拖动已有图元／手柄"手势的<b>宿主副作用</b>：切工具、选中、箭头缩放同步、指针捕获四件一起改。
        /// 命中判据与 <c>shapeEditor.Begin</c> 都在共享派发里（<see cref="AnnotationShapeEditor.TryBeginPress"/>），
        /// 贴图侧接的是同一个入口，故两侧"点哪里能拖什么"不可能漂移。
        /// </summary>
        void BeginShapeEdit(PointerPressedEventArgs e, AnnotationShape shape, AnnotationEditTarget target)
        {
            // 手柄只在图元选中后才画出来，抓它＝继续编辑同一个目标，不该顺手清掉别的选中；
            // 点本体＝另起一个编辑目标，先让选中互斥。
            if (!target.IsHandle) ClearAllSelections();
            ForceAnnotationTool(ToolOfShape(shape));
            switch (shape)
            {
                case BlurShape blur: SelectBlurShape(blur.Id); break;
                case SpotlightShape spotlight: SelectSpotlightShape(spotlight.Id); break;
                default: SelectShape(shape.Id); break;
            }
            if (shape is ArrowShape arrow) currentArrowScale = arrow.Scale;   // 滚轮改箭头缩放要接着这次的值
            pendingShapeGesturePoint = null;                                  // 上一段残留的待落坐标不能喂给这一段
            // 上面那几句 Select* 会顺带把圆角浮条按**旧**几何贴出来，而拖动途中不再重算位置 ⇒ 手势起手即收起，
            // 松手由提交路径（PointerReleased 里 Commit 之后）贴回新位置。放在末尾是刻意的：早于 Select 就会被
            // 那一路的 SelectionGeometryChanged 重新显示回来。序号浮条同一条道理，与它一起收、一起贴回。
            window.HideSpotlightRadiusPill();
            window.HideCounterPill();
            try { e.Pointer.Capture(window); } catch { }
            e.Handled = true;
        }

        /// <summary>
        /// 编辑已有图元的唯一派发入口。<paramref name="editExisting"/>＝本次按下是否按住了 Ctrl。
        /// "手柄免 Ctrl、同工具免 Ctrl 点选、跨工具要 Ctrl、本体顺序＝z 序、文本层插在序号与箭头之间"
        /// 这些判据贴图侧共用一份，见 <see cref="AnnotationShapeEditor.TryBeginPress"/>。
        /// 返回 true＝已起笔一段编辑手势，调用处直接 return。
        /// </summary>
        bool TryBeginShapeEdit(PointerPressedEventArgs e, PointF pt, bool editExisting, Func<PointF, bool> tryTextBody)
            => shapeEditor.TryBeginPress(annotations, pt, selectedShapeId, editExisting, tryTextBody,
                (shape, target) => BeginShapeEdit(e, shape, target),
                tol: 1.0f,
                activeKind: ActiveShapeKindOfTool(activeAnnotationTool),
                isActiveTextTool: activeAnnotationTool == AnnotationTool.Text);

        /// <summary>
        /// 手柄/缩放带方向 → 光标图形。<b>方向到箭头的映射只允许这一份</b>：图元手柄悬停
        /// （<see cref="EditCursor"/>）与标注态直接拖选区缩放共用它，同一档方向在两处必给同一个箭头。
        /// 方向的归类仍走 Core 的 <c>AnnotationEditTarget.CursorDirectionOf</c>（截图与贴图共用）。
        /// </summary>
        Cursor? ResizeCursorOf(HandleDirection direction)
            => AnnotationEditTarget.CursorDirectionOf(direction) switch
            {
                CursorDirection.NorthSouth => new Cursor(StandardCursorType.SizeNorthSouth),
                CursorDirection.WestEast => new Cursor(StandardCursorType.SizeWestEast),
                CursorDirection.NorthWestSouthEast => new Cursor(StandardCursorType.TopLeftCorner),
                CursorDirection.NorthEastSouthWest => new Cursor(StandardCursorType.TopRightCorner),
                _ => null,
            };

        /// <summary>
        /// 悬停时该给什么光标：抓到缩放手柄给对应的双向箭头、抓到端点／弯折点那一类手柄（箭头的三枚与
        /// 序号引线的两枚）给手型、其余按可拖给 SizeAll。
        /// 手柄判据与 <see cref="TryBeginShapeEdit"/> 用的是同一个 <c>HitTestEditTarget</c>，
        /// 故"看着能拖"与"真能拖"必然一致。截图与贴图共用这一份。
        /// <paramref name="hitBody"/> 让调用方限定"本工具下哪些本体算可拖"（聚光灯工具历来只认聚光灯），
        /// 不传＝任何图元都算。
        /// </summary>
        Cursor? EditCursor(PointF pt, Func<PointF, AnnotationShape?>? hitBody = null)
        {
            AnnotationShape? selected = GetSelectedShape();
            if (selected != null)
            {
                AnnotationEditTarget target = AnnotationHitTest.HitTestEditTarget(selected, pt, isSelected: true);
                switch (target.Kind)
                {
                    case AnnotationEditKind.Resize:
                        return ResizeCursorOf(target.Direction);
                    case AnnotationEditKind.ArrowStart:
                    case AnnotationEditKind.ArrowEnd:
                    case AnnotationEditKind.ArrowBend:
                    case AnnotationEditKind.CounterTail:
                    case AnnotationEditKind.CounterTailBend:
                        return new Cursor(StandardCursorType.Hand);
                }
            }
            bool bodyHit = hitBody != null
                ? hitBody(pt) != null
                : AnnotationHitTest.FindTopmost(annotations, pt, selectedShapeId) != null;
            return bodyHit ? new Cursor(StandardCursorType.SizeAll) : null;
        }

        // 方向键步进会话（issue #13）：一段连续步进只压一条撤销快照，Ctrl+Z 一次回到整段步进之前。
        // 会话以"当前步进的那个图元 Id"为界：换图元＝新会话；按下任何非方向键也结束会话
        // （否则"步进 5px → 改颜色 → 再步进 5px"会把两段无关改动并进一条撤销记录）。
        Guid? nudgedShapeId = null;

        /// <summary>
        /// 把当前选中的矢量图元平移 dx/dy 内容像素（截图侧内容坐标＝屏幕物理像素），一次一个图元。
        /// 返回 false＝没有任何矢量图元选中，调用处据此回落到选区框微移。
        /// 文本图层刻意不在本档：它由 <c>TextAnnotationController</c> 以画布 DIP 定位，量纲要单独换算，
        /// 且那是第三个文件的改动。
        /// </summary>
        bool StepSelectedShape(int dx, int dy)
        {
            AnnotationShape? target = GetSelectedShape();
            if (target == null) return false;

            if (nudgedShapeId != target.Id)
            {
                captureHistory.CommitSnapshot();
                nudgedShapeId = target.Id;
            }

            // 六类图元的平移解与拖动手势共用 Core 那一份（AnnotationGeometry.Translate 就是 EditShape 的 Move 档）。
            // 聚光灯传 bounds＝选区，故"步进到边缘"与"拖到边缘"结果一致；改动前这里逐边夹取，
            // 步进过界会把聚光灯压扁成一条，与拖动口径不符。
            AnnotationShape? moved = AnnotationGeometry.Translate(target, dx, dy, SpotlightClampBounds());
            if (moved == null) return false;
            annotations.ReplaceShape(moved);
            return true;
        }

        static bool ShouldAddBrushPoint(List<StrokePoint> points, PointF next)
            => BrushInputSmoother.ShouldAddPoint(points, next);

        /// <summary>
        /// 画笔输入 One-Euro 滤波：实现已上提到 <see cref="BrushInputSmoother"/>，与贴图标注共用一份。
        /// 平滑度为 0 时完全旁路，直接返回原始点。
        /// </summary>
        PointF SmoothBrushInput(PointF raw, double nowMs)
            => brushSmoother.Push(raw, nowMs, window.SelectedBrushProfile.Smoothing);

        /// <summary>
        /// 按住 Shift 拉直线的角度吸附：算式本体在 <see cref="AnnotationGeometry.SnapStraightLine"/>
        /// （画笔／箭头／序号引线三支共用，截图与贴图同一份），这里只留同名转发。
        /// </summary>
        static PointF SnapStraightLine(PointF start, PointF current)
            => AnnotationGeometry.SnapStraightLine(start, current);

        /// <summary>落一个画笔采样：输入平滑 → 最小点距去重 → 合帧刷新预览。</summary>
        void AppendBrushSample(PointF raw)
        {
            double nowMs = GetPreciseTimeMs();
            PointF smoothed = SmoothBrushInput(raw, nowMs);
            if (!ShouldAddBrushPoint(freehandStrokePoints, smoothed)) return;
            var sp = new StrokePoint(smoothed.X, smoothed.Y, nowMs);
            freehandStrokePoints.Add(sp);
            if (!isBrushShiftConstrained)
            {
                currentStrokePoints.Add(sp);
                SetBrushPreviewThrottled();
            }
        }

        void UpdateBrushShiftPreview(bool isShiftDown)
        {
            if (!drawingStroke) return;
            if (isShiftDown)
            {
                isBrushShiftConstrained = true;
                float dx = lastBrushMousePoint.X - brushStartPoint.X;
                float dy = lastBrushMousePoint.Y - brushStartPoint.Y;
                currentStrokePoints.Clear();
                currentStrokePoints.Add(new StrokePoint(brushStartPoint.X, brushStartPoint.Y, brushStartTimeMs));
                if (Math.Sqrt((double)dx * dx + (double)dy * dy) >= 1.0)
                {
                    PointF snapped = SnapStraightLine(brushStartPoint, lastBrushMousePoint);
                    currentStrokePoints.Add(new StrokePoint(snapped.X, snapped.Y, GetPreciseTimeMs()));
                }
                UpdateBrushPreview();
            }
            else
            {
                isBrushShiftConstrained = false;
                brushSmoother.Reset();
                currentStrokePoints.Clear();
                if (freehandStrokePoints.Count > 0)
                {
                    currentStrokePoints.AddRange(freehandStrokePoints);
                }
                else
                {
                    currentStrokePoints.Add(new StrokePoint(brushStartPoint.X, brushStartPoint.Y, brushStartTimeMs));
                }
                UpdateBrushPreview();
            }
        }

        void SetBrushPreviewThrottled()
        {
            long now = Environment.TickCount64;
            if (now - lastBrushPreviewTick < 16) return;
            lastBrushPreviewTick = now;
            UpdateBrushPreview();
        }

        void UpdateBrushPreview()
        {
            if (currentStrokePoints.Count == 0) return;
            window.Annotation.SetPreviewShape(new StrokeShape(
                currentStrokePoints.ToArray(),
                window.SelectedAnnotationColor,
                window.SelectedStrokeThickness,
                window.SelectedBrushProfile,
                window.SelectedBrushLineStyle));
        }

        void RevertDoubleClickCandidateAnnotation(System.Drawing.Point currentPt)
        {
            // 1. 中止可能仍在进行中的未收笔绘制手势（防止快速连击中途的临时图形残留）
            if (drawingStroke)
            {
                drawingStroke = false;
                currentStrokePoints.Clear();
                freehandStrokePoints.Clear();
                isBrushShiftConstrained = false;
                window.Annotation.ClearPreviewShape();
            }
            if (drawingArrow) { drawingArrow = false; window.Annotation.ClearPreviewShape(); }
            if (drawingRectangle) { drawingRectangle = false; window.Annotation.ClearPreviewShape(); }
            if (drawingMosaic) { drawingMosaic = false; window.Annotation.ClearPreviewShape(); }
            if (drawingSpotlight) { drawingSpotlight = false; window.Annotation.ClearPreviewShape(); }

            // 2. 检查双击首击是否在落笔点误触生成了临时标注（如画笔单点、序号徽章、空文本框）
            long elapsed = Environment.TickCount64 - lastClick1TickMs;
            float dx = currentPt.X - lastClick1ScreenPt.X;
            float dy = currentPt.Y - lastClick1ScreenPt.Y;
            if (elapsed <= 800 && (dx * dx + dy * dy) <= 40 * 40 && click1CreatedAnnotation)
            {
                captureHistory.Undo();
                click1CreatedAnnotation = false;
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession: 双击确认前已自动撤销首击在落笔点产生的误触标注");
            }

            ClearAllSelections();
            window.Annotation.ClearPreviewShape();
        }

        void FinishStroke()
        {
            window.Annotation.ClearPreviewShape();
            // 门槛放宽到 1 个采样：单击也要落一个笔点，而不是像旧版那样什么都不画
            if (currentStrokePoints.Count >= 1)
            {
                captureHistory.CommitSnapshot();
                var shape = AnnotationInteractionController.CreateStroke(
                    currentStrokePoints.ToArray(),
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedBrushProfile,
                    window.SelectedBrushLineStyle);
                annotations.AddShape(shape);
                // 绘制完成自动选中，显示可编辑状态
                ClearAllSelections();
                SelectShape(shape.Id);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.FinishStroke: 已添加并选中画笔，samples=" + currentStrokePoints.Count
                    + ", color=" + window.SelectedAnnotationColor
                    + ", thickness=" + window.SelectedStrokeThickness
                    + ", lineStyle=" + window.SelectedBrushLineStyle);

                long elapsed = Environment.TickCount64 - lastClick1TickMs;
                if (click1IsBrushCandidate && elapsed <= 800)
                {
                    click1CreatedAnnotation = true;
                }
            }

            click1IsBrushCandidate = false;
            currentStrokePoints.Clear();
            freehandStrokePoints.Clear();
            isBrushShiftConstrained = false;
            drawingStroke = false;
        }

        void FinishArrow(PointF arrowEnd, bool lockStraight)
        {
            window.Annotation.ClearPreviewShape();
            // 提交侧必须同样量化一次终点：预览那一支已经锁过，这里不锁就会出现"画时是直线、落成斜角"的分叉
            if (lockStraight) arrowEnd = SnapStraightLine(arrowStart, arrowEnd);
            float dx = arrowEnd.X - arrowStart.X;
            float dy = arrowEnd.Y - arrowStart.Y;
            if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var shape = AnnotationInteractionController.CreateArrow(
                    arrowStart,
                    arrowEnd,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedArrowStyle,
                    currentArrowScale);
                annotations.AddShape(shape);
                // 绘制完成自动选中，显示锚点手柄
                ClearAllSelections();
                SelectShape(shape.Id);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.FinishArrow: 已添加并选中箭头，start=" + arrowStart + ", end=" + arrowEnd + ", color=" + window.SelectedAnnotationColor + ", thickness=" + window.SelectedStrokeThickness + ", style=" + window.SelectedArrowStyle);
            }

            drawingArrow = false;
        }

        void FinishRectangle(PointF rectangleEnd, bool forceSquare)
        {
            window.Annotation.ClearPreviewShape();
            // 与预览那一支同一个算式，否则锁出来的正方形会在松手瞬间退回原始矩形
            RectangleF rect = CreateDrawRect(rectangleStart, rectangleEnd, forceSquare);
            if (rect.Width >= DragThresholdPx && rect.Height >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var shape = AnnotationInteractionController.CreateShape(
                    rect,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedShapeKind,
                    window.SelectedShapeLineStyle,
                    window.SelectedShapeCornerRadius,
                    window.SelectedShapeFillOpacity);
                annotations.AddShape(shape);
                // 绘制完成自动选中，显示缩放手柄和边框
                ClearAllSelections();
                SelectShape(shape.Id);
                // 圆角一并记：记忆值经 ClampCornerRadius 后才是真正落进几何的数，
                // 只记记忆值看不出"被短边一半封顶"这类情况。
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.FinishRectangle: 已添加并选中形状，rect=" + rect + ", color=" + window.SelectedAnnotationColor + ", thickness=" + window.SelectedStrokeThickness + ", kind=" + window.SelectedShapeKind + ", lineStyle=" + window.SelectedShapeLineStyle + ", 记忆圆角=" + window.SelectedShapeCornerRadius + ", 落图圆角=" + (shape is RectangleShape loggedRect ? loggedRect.CornerRadius.ToString("0.#") : "-"));
            }

            drawingRectangle = false;
        }

        void FinishMosaic(PointF mosaicEnd, bool forceSquare)
        {
            window.Annotation.ClearPreviewShape();
            RectangleF rect = CreateDrawRect(mosaicStart, mosaicEnd, forceSquare);
            if (rect.Width >= DragThresholdPx && rect.Height >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var blur = AnnotationInteractionController.CreateBlur(
                    rect,
                    window.SelectedBlurMode,
                    window.SelectedBlurUiLevel);
                annotations.AddShape(blur);
                // 新建马赛克/模糊完成后保留边线编辑框，清掉拖拽时的蓝色半透明填充预览。
                SelectBlurShape(blur.Id);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.FinishMosaic: 已添加效果，rect=" + rect + ", mode=" + window.SelectedBlurMode + ", uiLevel=" + blur.UiLevel + ", blockSize=" + blur.MosaicBlockSize + ", forceSquare=" + forceSquare);
            }

            drawingMosaic = false;
        }

        void FinishSpotlight(PointF spotlightEnd, bool forceSquare)
        {
            RectangleF rect = ClampSpotlightRect(CreateDrawRect(spotlightStart, spotlightEnd, forceSquare));
            window.Annotation.ClearPreviewShape();
            if (rect.Width >= DragThresholdPx && rect.Height >= DragThresholdPx)
            {
                captureHistory.CommitSnapshot();
                var spotlight = AnnotationInteractionController.CreateSpotlight(
                    rect,
                    window.SelectedSpotlightDarkness,
                    window.SelectedSpotlightKind,
                    window.SelectedAnnotationColor,
                    window.SelectedSpotlightStrokeThickness,
                    window.SelectedSpotlightCornerRadius);
                annotations.AddShape(spotlight);
                SelectSpotlightShape(spotlight.Id);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.FinishSpotlight: 已添加聚光，rect=" + rect + ", darkness=" + window.SelectedSpotlightDarkness + ", kind=" + window.SelectedSpotlightKind);
            }

            drawingSpotlight = false;
        }

        void AddCounter(PointF center)
        {
            captureHistory.CommitSnapshot();
            CounterStyleSettings style = window.SelectedCounterStyle;
            int number = style.NextNumber(annotations);
            var shape = AnnotationInteractionController.CreateCounter(
                center,
                number,
                window.SelectedCounterRadius,
                window.SelectedAnnotationColor,
                style);
            annotations.AddShape(shape);
            // 绘制完成自动选中，显示选中圈
            ClearAllSelections();
            SelectShape(shape.Id);
            // 建号这一刻就起一段"拖引线"的手势：按下点＝圆心、松开点＝箭头所指。引线固定为"拖出"，
            // 原地松手不会给出引线（阈值见 TailIfDragged），所以这段手势每次都起。
            draggingCounterTail = true;
            counterTailShapeId = shape.Id;
            counterTailAnchor = center;
            // 上面 SelectShape 已经把浮条贴出来了，而这段手势期间不重算位置 ⇒ 起手收掉，松手由调用处无条件贴回。
            window.HideCounterPill();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.AddCounter: 已添加并选中编号，number=" + number + ", center=" + center
                + ", radius=" + window.SelectedCounterRadius + ", tailWidth=" + shape.TailWidth.ToString("0.00")
                + ", color=" + window.SelectedAnnotationColor
                + ", style=" + style);

            long elapsed = Environment.TickCount64 - lastClick1TickMs;
            if (elapsed <= 800)
            {
                click1CreatedAnnotation = true;
            }
        }

        /// <summary>
        /// 引线拖动中的一帧：只写图层预览（同 id 就地顶替文档里的那一个），文档一个字都不改。
        /// <paramref name="lockStraight"/> 只作用于这一段——序号的第①段（定气泡）是单击落点，没有方向可锁。
        /// </summary>
        void UpdateCounterTailPreview(PointF pt, bool lockStraight)
        {
            if (annotations.Shapes.FirstOrDefault(s => s.Id == counterTailShapeId) is not CounterShape counter)
            {
                draggingCounterTail = false;
                window.Annotation.ClearPreviewShape();
                return;
            }
            pt = SnapCounterTailPoint(pt, lockStraight);
            if (!IsCounterTailDragged(pt)) return;
            window.Annotation.SetPreviewShape(counter.WithTail(pt));
        }

        /// <summary>
        /// Shift 锁直线：绕气泡圆心把引线终点量化到 45° 步进，与画笔／箭头共用 <see cref="SnapStraightLine"/>。
        /// 量化只改方向不改长度，故"有没有拖出阈值"不会因为锁了方向而变。
        /// </summary>
        PointF SnapCounterTailPoint(PointF pt, bool lockStraight)
            => lockStraight ? SnapStraightLine(counterTailAnchor, pt) : pt;

        bool IsCounterTailDragged(PointF pt)
            => Math.Abs(pt.X - counterTailAnchor.X) >= DragThresholdPx
                || Math.Abs(pt.Y - counterTailAnchor.Y) >= DragThresholdPx;

        /// <summary>
        /// 松手收口：确有位移才把引线写进文档（不再压快照——起手那次 <c>AddCounter</c> 已压过，
        /// 于是一段"按下＋拖出引线"＝一条撤销）。纯单击＝什么都不写。
        /// </summary>
        void CommitCounterTail(PointF pt, bool lockStraight)
        {
            bool dragged = draggingCounterTail;
            draggingCounterTail = false;
            window.Annotation.ClearPreviewShape();
            if (!dragged) return;
            if (annotations.Shapes.FirstOrDefault(s => s.Id == counterTailShapeId) is not CounterShape counter) return;
            pt = SnapCounterTailPoint(pt, lockStraight);
            if (!IsCounterTailDragged(pt)) return;

            annotations.ReplaceShape(counter.WithTail(pt));
            window.Annotation.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CounterTail: 已为序号挂上引线，anchor=" + counterTailAnchor + ", tail=" + pt);
        }

        bool ShouldClearShapeForCurrentTool(AnnotationShape shape)
        {
            return activeAnnotationTool switch
            {
                AnnotationTool.Brush => shape is StrokeShape,
                AnnotationTool.Arrow => shape is ArrowShape,
                AnnotationTool.Rectangle => shape is RectangleShape,
                AnnotationTool.Mosaic => shape is BlurShape,
                AnnotationTool.Spotlight => shape is SpotlightShape,
                AnnotationTool.Counter => shape is CounterShape,
                _ => false,
            };
        }

        void ClearCurrentToolAnnotations()
        {
            if (activeAnnotationTool == AnnotationTool.None) return;

            // 文本工具：清空窗口层上的所有文本
            if (activeAnnotationTool == AnnotationTool.Text)
            {
                captureHistory.CommitSnapshot();
                window.ClearAllTexts();
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.ClearCurrentToolAnnotations: 已清除所有文本");
                return;
            }

            AnnotationShape[] remaining = annotations.Shapes.Where(shape => !ShouldClearShapeForCurrentTool(shape)).ToArray();
            if (remaining.Length == annotations.Count) return;

            captureHistory.CommitSnapshot();
            annotations.ReplaceAll(remaining);
            window.Annotation.ClearPreviewShape();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.ClearCurrentToolAnnotations: 已清除工具标注，tool=" + activeAnnotationTool);
        }

        void ClearAllAnnotations()
        {
            CancelEraserGesture();
            if (annotations.Count == 0 && window.TextLayers.Count == 0) return;

            captureHistory.CommitSnapshot();
            annotations.Clear();
            window.ClearAllTexts();
            window.Annotation.ClearPreviewShape();
            window.Annotation.ClearAutoMosaicHighlights();
            autoMosaicHighlights.Clear();
            ClearAllSelections();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.ClearAllAnnotations: 已清除所有标注和文本");
        }

        void CancelEraserGesture()
        {
            if (drawingEraser && currentErasure != null)
            {
                annotations.RemoveShape(currentErasure.Id);
                window.ApplyAnnotationErasureMasks(annotations);
                if (eraserHistoryCommitted) captureHistory.DiscardLatestSnapshot();
            }
            drawingEraser = false;
            currentErasure = null;
            currentErasureMarks.Clear();
            eraserHistoryCommitted = false;
            eraserLastPublishMs = 0;
            eraserPublishPending = false;
            window.Annotation.EraserRectanglePreview = null;
        }

        /// <summary>
        /// 把点到的那一个（矢量图元或文字层）从所有既有擦除记录的"影响名单"里摘出去＝让它浮到橡皮擦之上
        /// （2026-10-06 用户定义的 Ctrl+点击语义）。洞一枚都不动、图元一个都不删，改的只是"这个洞管谁"，
        /// 因此撤销一步就能把它放回洞里，且预览与导出按同一份名单算、不会不一致。
        /// 之后再拿橡皮擦盖它一次，新手势的名单重新含它——即"一直显形，直到下一次被新的橡皮擦覆盖"。
        /// </summary>
        void RaiseTargetAboveEraser(PointF pt, TextLayerInfo? textLayer)
        {
            // 文字层画在标注层之上：点在文字上就先认文字，与用户看到的上下关系一致
            Guid textId = textLayer?.Id ?? Guid.Empty;
            Guid shapeId = textId == Guid.Empty
                ? AnnotationHitTest.FindTopmost(annotations, pt, selectedShapeId)?.Id ?? Guid.Empty
                : Guid.Empty;
            if (textId == Guid.Empty && shapeId == Guid.Empty)
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RaiseAboveEraser: 这一点上没有可提升的内容，忽略");
                return;
            }
            List<ErasureShape> replacements = new List<ErasureShape>();
            foreach (AnnotationShape item in annotations.Shapes)
            {
                if (item is ErasureShape erase && erase.WithoutTargets(shapeId, textId) is ErasureShape next)
                    replacements.Add(next);
            }
            if (replacements.Count == 0)
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RaiseAboveEraser: 目标本就不在任何擦除区内，无变化");
                return;
            }
            captureHistory.CommitSnapshot();   // 一次提升＝一步撤销，且只在真改到时才压快照
            foreach (ErasureShape next in replacements) annotations.ReplaceShape(next);
            window.ApplyAnnotationErasureMasks(annotations);
            window.Annotation.InvalidateVisual();
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.RaiseAboveEraser: 已浮到橡皮擦之上，目标="
                + (textId != Guid.Empty ? "文字层" : "图元") + "，改写擦除记录=" + replacements.Count + "枚");
        }

        /// <summary>把攒下的擦除落点写进文档。一次写入＝一次整层重绘，所以按 <see cref="PreviewFrameGateMs"/> 合帧；
        /// 被压住的那一次记在 <paramref name="force"/> 的调用方（收笔）补落，落点本身一直在攒、不会丢。</summary>
        bool PublishEraserMarks(bool force = false)
        {
            if (currentErasure == null) return false;
            double nowMs = GetPreciseTimeMs();
            if (!force && eraserLastPublishMs != 0 && nowMs - eraserLastPublishMs < EraserPublishGateMs())
            {
                eraserPublishPending = true;
                return false;
            }
            eraserLastPublishMs = nowMs;
            eraserPublishPending = false;
            if (currentErasureMarks.Count == 0)
            {
                annotations.RemoveShape(currentErasure.Id);
                window.ApplyAnnotationErasureMasks(annotations);
                return false;
            }
            if (!eraserHistoryCommitted)
            {
                captureHistory.CommitSnapshot();
                eraserHistoryCommitted = true;
            }
            double probeStart = GetPreciseTimeMs();
            currentErasure = currentErasure.WithMarks(currentErasureMarks);
            if (!annotations.ReplaceShape(currentErasure)) annotations.AddShape(currentErasure);
            window.ApplyAnnotationErasureMasks(annotations);
            // 没有选中任何东西时这一步是空转，却会顺手把整层再标脏一次：每枚落点都白标一次脏
            if (AnyShapeSelected() || window.SelectedText != null) ClearAllSelections();
            window.Annotation.InvalidateVisual();
            eraserPublishCostMs = GetPreciseTimeMs() - probeStart;
            if (EraserProbe)
            {
                probeErasePublishes++;
                probeErasePublishMs += eraserPublishCostMs;
                probeEraseMarksPeak = Math.Max(probeEraseMarksPeak, currentErasureMarks.Count);
                if (eraserPublishCostMs > probeErasePublishMaxMs) probeErasePublishMaxMs = eraserPublishCostMs;
            }
            return true;
        }

        /// <summary>擦除发布的合帧闸门：常态 16 毫秒；上一次<b>整层重绘</b>实测超过半格（8 毫秒）就按它的 1.6 倍放宽，
        /// 上限 <see cref="EraserPublishGateMaxMs"/>。读数只能取重绘耗时、不能取发布耗时——发布只是写文档＋标脏，
        /// 实测恒为 0.0 毫秒，拿它当输入的话闸门永远不收，4K 长涂抹就把鼠标事件堆成队列（用户原话"光标不跟手"）。
        /// 宁可洞少刷几帧，也要把输入线程让出来。</summary>
        double EraserPublishGateMs()
        {
            double renderMs = window.Annotation.LastRenderMs;
            return renderMs > PreviewFrameGateMs * 0.5
                ? Math.Min(EraserPublishGateMaxMs, renderMs * 1.6)
                : PreviewFrameGateMs;
        }

        bool EraseAnnotationsAt(PointF point)
        {
            if (currentErasure == null) return false;
            if (eraserGestureMode == EraserGestureMode.Rectangle)
            {
                var rect = RectangleF.FromLTRB(Math.Min(eraserStartPoint.X, point.X),
                    Math.Min(eraserStartPoint.Y, point.Y), Math.Max(eraserStartPoint.X, point.X),
                    Math.Max(eraserStartPoint.Y, point.Y));
                window.Annotation.EraserRectanglePreview = rect;
                currentErasureMarks.Clear();
                if (rect.Width >= 1f && rect.Height >= 1f)
                    currentErasureMarks.Add(new ErasureMark(rect, false));
            }
            else
            {
                float diameter = window.SelectedEraserDiameter;
                currentErasureMarks.Add(new ErasureMark(new RectangleF(point.X - diameter / 2f,
                    point.Y - diameter / 2f, diameter, diameter), true));
            }
            return PublishEraserMarks();
        }

        void EraseAnnotationsAlong(PointF from, PointF to)
        {
            if (eraserGestureMode == EraserGestureMode.Rectangle)
            {
                EraseAnnotationsAt(to);
                return;
            }
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float step = Math.Max(2f, window.SelectedEraserDiameter / 4f);
            int count = Math.Max(1, (int)MathF.Ceiling(distance / step));
            for (int i = 1; i <= count; i++)
            {
                float t = i / (float)count;
                var point = new PointF(from.X + dx * t, from.Y + dy * t);
                float diameter = window.SelectedEraserDiameter;
                currentErasureMarks.Add(new ErasureMark(new RectangleF(point.X - diameter / 2f,
                    point.Y - diameter / 2f, diameter, diameter), true));
            }
            PublishEraserMarks();
        }

        async void RunAutoMosaicRecognition()
        {
            if (!hasManualSelection || !selection.HasSelection || frame?.FullScreen == null)
            {
                window.SetAutoMosaicBusy(false);
                return;
            }

            window.SetAutoMosaicBusy(true);
            autoMosaicHighlights.Clear();
            window.Annotation.ClearAutoMosaicHighlights();

            Rectangle region = selection.Region;
            Rectangle bmpRect = new Rectangle(region.X - frame.VirtualScreenRect.X, region.Y - frame.VirtualScreenRect.Y, region.Width, region.Height);
            bmpRect.Intersect(new Rectangle(0, 0, frame.FullScreen.Width, frame.FullScreen.Height));
            if (bmpRect.Width <= 0 || bmpRect.Height <= 0)
            {
                window.SetAutoMosaicBusy(false);
                return;
            }

            long start = Environment.TickCount64;
            try
            {
                using Bitmap cropped = frame.FullScreen.Clone(bmpRect, PixelFormat.Format32bppArgb);
                // cropped 是物理像素位图，WinRT OCR 返回的 BoundingRect 也是该位图像素坐标；这里不能再除以 DPI，否则识别框会偏小且对不上文本。
                IReadOnlyList<RectangleF> localRects = await OcrTextRegionDetector.DetectTextRegionsAsync(cropped, 1.0);
                long elapsed = Environment.TickCount64 - start;
                if (elapsed < 1200) await Task.Delay((int)(1200 - elapsed));

                foreach (RectangleF local in localRects)
                {
                    RectangleF screen = RectangleF.FromLTRB(
                        region.X + local.Left,
                        region.Y + local.Top,
                        region.X + local.Right,
                        region.Y + local.Bottom);
                    screen = ExpandAutoMosaicRect(screen, region);
                    if (screen.Width >= 2 && screen.Height >= 2) autoMosaicHighlights.Add(screen);
                }
                window.Annotation.SetAutoMosaicHighlights(autoMosaicHighlights.ToArray());
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.AutoMosaic: OCR 完成，count=" + autoMosaicHighlights.Count);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureSession.AutoMosaic: OCR 失败：" + ex.Message);
            }
            finally
            {
                window.SetAutoMosaicBusy(false);
            }
        }

        bool TrySampleColor(System.Drawing.Point screenPt, out Color color)
        {
            color = Color.Empty;
            if (frame?.FullScreen == null) return false;

            int x = screenPt.X - frame.VirtualScreenRect.X;
            int y = screenPt.Y - frame.VirtualScreenRect.Y;
            if (x < 0 || y < 0 || x >= frame.FullScreen.Width || y >= frame.FullScreen.Height) return false;

            color = frame.FullScreen.GetPixel(x, y);
            return true;
        }

        void CopyPointerColor(System.Drawing.Point point)
        {
            // 始终采样当前原始冻结图，不能复用放大镜节流前的旧像素／受遮罩影响的成图。
            if (!TrySampleColor(point, out Color sample)) return;
            string value = _magnifierUseHexFormat ? FormatHex(sample) : $"{sample.R}, {sample.G}, {sample.B}";
            HideCaptureWindowForCompletion(window);
            try { _writeColorClipboard(value); } catch { }
            SettleSuccess(new CaptureCompletion(selection.Region, CaptureCompletionAction.ColorPick, value));
        }

        static string FormatHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        // 拖动起始信息（屏幕物理像素）
        System.Drawing.Point dragStartScreen = default;
        System.Drawing.Point lastDragPointerScreen = default;
        Rectangle dragStartRect = default;
        HandleDirection resizeDirection = HandleDirection.None;
        bool dragMovedPastThreshold = false; // 单击 vs 拖动判定

        /// <summary>
        /// 起手拖选区：<paramref name="screenPt"/>（屏幕物理像素）命中选区缩放带/手柄就进
        /// <c>ResizingHandle</c> 并返回 true。<b>两个派发点共用这一份</b>：工具未激活时的常规拖边，
        /// 与 2026-09-09 起标注态下"不必退出工具栏也能直接拖选区"，判据与起手动作必须逐字相同。
        /// 命中判据只用 Core 的 <c>HitTestResizeDirection</c>（protection.md ㈠ 命中测试只允许一份实现）。
        /// </summary>
        bool TryBeginSelectionResize(PointerPressedEventArgs e, System.Drawing.Point screenPt)
        {
            if (pathEditor.Mode != CaptureSelectionMode.Rectangle) return false;
            if (!hasManualSelection || !selection.HasSelection) return false;
            HandleDirection handle = selection.HitTestResizeDirection(
                new System.Drawing.Point(screenPt.X, screenPt.Y), HandleHitRadiusPx, BorderGripPx);
            if (handle == HandleDirection.None) return false;

            mode = InteractionMode.ResizingHandle;
            resizeDirection = handle;
            // 起手基准必须在这里落：标注态的调用点排在调用方那两行赋值之前，
            // 不自己写就会拿上一次手势的陈旧起点算第一帧增量，选区当场跳一截。
            dragStartScreen = screenPt;
            dragMovedPastThreshold = false;
            dragStartRect = selection.Region;
            BeginSelectionTransform();
            try { e.Pointer.Capture(window); } catch { }
            e.Handled = true;
            return true;
        }

        /// <summary>
        /// 所有「完成类动作」（贴图 / 复制 / 保存 / OCR / 翻译 / 长截图）取矩形的唯一入口。
        /// 优先级：已确认的手动选区 ＞ 拖框未松手时的实时框 ＞ 点住不松手的那个检测元素 ＞ 当前悬停检测框。
        /// 中间两态在松手前都还没写进 <c>hasManualSelection</c>，旧写法一律读成 null、
        /// 于是按住左键点中键出贴图毫无反应。
        /// </summary>
        Rectangle? ResolveFinalRect()
        {
            if (pathEditor.Mode != CaptureSelectionMode.Rectangle)
            {
                if (pathEditor.Path is not { } path) return null;
                Rectangle bounds = path.Bounds;
                if (frame != null) bounds.Intersect(frame.VirtualScreenRect);
                return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
            }
            if (hasManualSelection && selection.HasSelection) return selection.Region;
            if (mode == InteractionMode.DraggingNew && (dragMovedPastThreshold || (selection.HasSelection && selection.Region.Width > 5 && selection.Region.Height > 5))) return selection.Region;
            return pressedHighlight ?? currentHighlight;
        }

        // ===== 截图附加线：参考线与十字准星各一独立三态档位 ＋ 两阶段线集合下发 =====

        // Alt 是否真的还按着：以系统物理按键状态为准。只记 KeyDown/KeyUp 会被 Alt+Tab 打断——
        // 焦点切走后抬起那一次 KeyUp 永远收不到，辅助线就卡在激活态再也关不掉。
        // KeyDown/KeyUp 仍保留，只负责在按键瞬间触发一次刷新。
        bool IsAltDown() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

        // 三态：关闭=永不；开启=按住 Alt 才显示；始终显示=常亮。
        // 参考线与十字准星是两个独立设置项，各自套同一份判据，Alt 只点亮自己那一项。
        bool ModeRequested(string mode)
        {
            string gm = AppSettings.NormalizeGuidelineMode(mode);
            if (gm == AppSettings.GuidelineModeOff) return false;
            if (gm == AppSettings.GuidelineModeHoldAlt && !IsAltDown()) return false;
            return true;
        }

        bool LinesRequested() => ModeRequested(_settings.GuidelineMode);

        bool CrosshairRequested() => ModeRequested(_settings.CrosshairMode);

        /// <summary>
        /// 只在「线已显示且 Alt 仍按着」期间续期的一次性看门狗：Alt+Tab 切走时焦点已不在本窗口，
        /// Alt 抬起的那一次 KeyUp 永远收不到，靠按键/鼠标事件刷新就会一直卡着，直到用户下一次动鼠标。
        /// 每 tick 重读物理按键状态，松开即停止续期；不常驻，「始终显示」档下完全不启动。
        /// 单飞保护是必需的：本函数由 UpdateGuidelines 尾部调用，而鼠标每动一次都会走那里。
        /// </summary>
        bool guidelineWatchdogPending = false;
        void ArmGuidelineWatchdog()
        {
            if (guidelineWatchdogPending) return;
            guidelineWatchdogPending = true;
            DispatcherTimer.RunOnce(() =>
            {
                guidelineWatchdogPending = false;
                if (!guidelineShown || !window.IsVisible) return;
                UpdateGuidelines();
            }, TimeSpan.FromMilliseconds(150));
        }

        /// <summary>
        /// 阶段一是否用自绘十字接管光标。只在值真的不同时写 <c>window.Cursor</c>：
        /// 每帧都写会让 Avalonia 反复重设原生光标，光标会抖；比对当前值而不是只记状态位，
        /// 是因为切工具/悬手柄等处也会直接写光标，只信状态位就会漏掉那一次覆盖。
        /// </summary>
        void SetAimCursor(bool on)
        {
            bool wasAimCursorActive = aimCursorActive;
            aimCursorActive = on;
            if (on)
            {
                if (!ReferenceEquals(window.Cursor, noneCursor)) window.Cursor = noneCursor;
            }
            // 准星与笔触圈共用隐藏光标；收起准星只能归还自己持有的光标，不能清掉画笔的接管。
            else if (wasAimCursorActive && !brushPointerDip.HasValue && ReferenceEquals(window.Cursor, noneCursor))
            {
                window.Cursor = null;
            }
        }

        void UpdateBrushPointerFootprint()
        {
            bool isBrush = activeAnnotationTool == AnnotationTool.Brush;
            bool isEraser = activeAnnotationTool == AnnotationTool.Eraser
                && window.SelectedEraserMode == EraserGestureMode.Brush;
            if (!brushPointerDip.HasValue || (!isBrush && !isEraser))
            {
                window.Annotation.SetPointerFootprint(null, 0);
                return;
            }
            double size = isBrush ? window.SelectedStrokeThickness : window.SelectedEraserDiameter;
            window.Annotation.SetPointerFootprint(brushPointerDip,
                size * window.Annotation.ScreenToDipScale);
        }
        window.AnnotationThicknessChanged += (_, _) => UpdateBrushPointerFootprint();
        window.EraserSizeChanged += (_, _) => UpdateBrushPointerFootprint();
        window.EraserModeChanged += (_, _) =>
        {
            UpdateBrushPointerFootprint();
            if (activeAnnotationTool == AnnotationTool.Eraser)
                window.Cursor = window.SelectedEraserMode == EraserGestureMode.Brush
                    ? noneCursor : rectangleEraserCursor;
        };
        window.PointerExited += (_, _) =>
        {
            brushPointerDip = null;
            window.Annotation.SetPointerFootprint(null, 0);
        };

        void HideGuidelines()
        {
            SetAimCursor(false);
            if (!guidelineShown) return;
            guidelineShown = false;
            window.Layer.GuidelineVerticals = noGuidelineLines;
            window.Layer.GuidelineHorizontals = noGuidelineLines;
            window.Layer.Crosshair = null;
            window.Layer.CrosshairSegmentsV = null;
            window.Layer.CrosshairSegmentsH = null;
            lastCrosshairAimX = null;
            lastCrosshairAimY = null;
            window.Layer.AimCursor = null;
        }

        unsafe void UpdateCrosshairSegments(System.Drawing.Point aimScreen, double s, double ox, double oy)
        {
            if (frame?.FullScreen == null) return;
            var bmp = frame.FullScreen;
            int bmpW = bmp.Width;
            int bmpH = bmp.Height;
            if (bmpW <= 0 || bmpH <= 0) return;

            double screenW = window.Bounds.Width;
            double screenH = window.Bounds.Height;
            if (screenW <= 0 || screenH <= 0) return;

            int numSegsV = (int)Math.Ceiling(screenH / SelectionLayer.CrosshairSegmentDip);
            int numSegsH = (int)Math.Ceiling(screenW / SelectionLayer.CrosshairSegmentDip);
            if (numSegsV <= 0 || numSegsH <= 0) return;

            if (segsV_BufA == null || segsV_BufA.Length != numSegsV)
            {
                segsV_BufA = new byte[numSegsV];
                segsV_BufB = new byte[numSegsV];
                lastCrosshairAimX = null;
            }
            if (segsH_BufA == null || segsH_BufA.Length != numSegsH)
            {
                segsH_BufA = new byte[numSegsH];
                segsH_BufB = new byte[numSegsH];
                lastCrosshairAimY = null;
            }

            int winPhysX = (int)Math.Round(ox - frame.VirtualScreenRect.X);
            int winPhysY = (int)Math.Round(oy - frame.VirtualScreenRect.Y);

            // 竖线采样：仅在 X 坐标变动时重采
            if (!lastCrosshairAimX.HasValue || lastCrosshairAimX.Value != aimScreen.X)
            {
                lastCrosshairAimX = aimScreen.X;
                byte[] targetV = toggleV ? segsV_BufB! : segsV_BufA!;
                toggleV = !toggleV;

                int colPhysX = aimScreen.X - frame.VirtualScreenRect.X;
                colPhysX = Math.Clamp(colPhysX, 0, bmpW - 1);

                try
                {
                    var rectCol = new System.Drawing.Rectangle(colPhysX, 0, 1, bmpH);
                    var data = bmp.LockBits(rectCol, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        byte* scan0 = (byte*)data.Scan0;
                        int stride = data.Stride;

                        for (int sIdx = 0; sIdx < numSegsV; sIdx++)
                        {
                            double midDip = (sIdx + 0.5) * SelectionLayer.CrosshairSegmentDip;
                            int physY = winPhysY + (int)Math.Round(midDip * s);
                            physY = Math.Clamp(physY, 0, bmpH - 1);

                            byte* pixel = scan0 + physY * stride;
                            byte b = pixel[0];
                            byte g = pixel[1];
                            byte r = pixel[2];
                            float L = (0.299f * r + 0.587f * g + 0.114f * b) / 255.0f;
                            targetV[sIdx] = (L < CrosshairDarkThreshold) ? (byte)1
                                          : (L > CrosshairLightThreshold) ? (byte)0
                                          : (byte)2;
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                    window.Layer.CrosshairSegmentsV = targetV;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("UpdateCrosshairSegments 列采样 LockBits 失败: " + ex.Message);
                }
            }

            // 横线采样：仅在 Y 坐标变动时重采
            if (!lastCrosshairAimY.HasValue || lastCrosshairAimY.Value != aimScreen.Y)
            {
                lastCrosshairAimY = aimScreen.Y;
                byte[] targetH = toggleH ? segsH_BufB! : segsH_BufA!;
                toggleH = !toggleH;

                int rowPhysY = aimScreen.Y - frame.VirtualScreenRect.Y;
                rowPhysY = Math.Clamp(rowPhysY, 0, bmpH - 1);

                try
                {
                    var rectRow = new System.Drawing.Rectangle(0, rowPhysY, bmpW, 1);
                    var data = bmp.LockBits(rectRow, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        byte* scan0 = (byte*)data.Scan0;

                        for (int sIdx = 0; sIdx < numSegsH; sIdx++)
                        {
                            double midDip = (sIdx + 0.5) * SelectionLayer.CrosshairSegmentDip;
                            int physX = winPhysX + (int)Math.Round(midDip * s);
                            physX = Math.Clamp(physX, 0, bmpW - 1);

                            byte* pixel = scan0 + physX * 4;
                            byte b = pixel[0];
                            byte g = pixel[1];
                            byte r = pixel[2];
                            float L = (0.299f * r + 0.587f * g + 0.114f * b) / 255.0f;
                            targetH[sIdx] = (L < CrosshairDarkThreshold) ? (byte)1
                                          : (L > CrosshairLightThreshold) ? (byte)0
                                          : (byte)2;
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                    window.Layer.CrosshairSegmentsH = targetH;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("UpdateCrosshairSegments 行采样 LockBits 失败: " + ex.Message);
                }
            }
        }

        void RequestGuidelinesUpdate()
        {
            if (guidelinesUpdateScheduled) return;
            guidelinesUpdateScheduled = true;
            int token = ++guidelinesUpdateToken;
            window.RequestAnimationFrame(_ =>
            {
                if (!guidelinesUpdateScheduled || token != guidelinesUpdateToken) return;
                guidelinesUpdateScheduled = false;
                if (!window.IsVisible || longScrollStarting) return;
                UpdateGuidelines();
            });
        }

        void UpdateGuidelines()
        {
            guidelinesUpdateScheduled = false;
            guidelinesUpdateToken++;

            var layer = window.Layer;
            // 公共抑制态：不与标注抢画面，两类附加线一律收起，各自的档位不参与判断
            bool suppressed = longScrollStarting
                || activeAnnotationTool != AnnotationTool.None || window.HasActiveTextEditing;
            bool wantLines = !suppressed && LinesRequested();
            bool wantCrosshair = !suppressed && CrosshairRequested();
            if (!wantLines && !wantCrosshair)
            {
                HideGuidelines();
                return;
            }

            // 阶段二判据：框已经出现。拖框中用 selection.Region，改尺寸/平移中用尚未提交的 liveTransformRect。
            bool transforming = mode == InteractionMode.MovingSelection || mode == InteractionMode.ResizingHandle;
            Rectangle boxRect = Rectangle.Empty;
            if (transforming) boxRect = liveTransformRect;
            else if (mode == InteractionMode.DraggingNew) { if (dragMovedPastThreshold) boxRect = selection.Region; }
            else if (hasManualSelection) boxRect = selection.Region;
            bool hasBox = boxRect.Width > 0 && boxRect.Height > 0;

            // 阶段一：还没起框 → 线来自当前识别高亮的窗口/元素；没识别到目标就没有参考线可画
            Rectangle baseRect = hasBox ? boxRect : (currentHighlight ?? Rectangle.Empty);

            double s = window.SafeScaling;
            double ox = window.Position.X;
            double oy = window.Position.Y;

            if (wantLines && baseRect.Width > 0 && baseRect.Height > 0)
            {
                int[] v = GuidelineEngine.VerticalLines(baseRect);
                int[] h = GuidelineEngine.HorizontalLines(baseRect);
                double[] vDip = new double[v.Length];
                for (int i = 0; i < v.Length; i++) vDip[i] = (v[i] - ox) / s;
                double[] hDip = new double[h.Length];
                for (int i = 0; i < h.Length; i++) hDip[i] = (h[i] - oy) / s;
                layer.GuidelineVerticals = vDip;
                layer.GuidelineHorizontals = hDip;
            }
            else
            {
                layer.GuidelineVerticals = noGuidelineLines;
                layer.GuidelineHorizontals = noGuidelineLines;
            }

            // 中心准星与十字瞄准光标：起框前跟随光标，按住拖拽拉框期间持续跟随鼠标活动角点辅助对齐；松手确立选区后收起
            bool canShowCrosshair = !hasManualSelection && (mode == InteractionMode.Idle || mode == InteractionMode.DraggingNew);
            if (wantCrosshair && canShowCrosshair && TryResolveAimPoint(out System.Drawing.Point aimScreen))
            {
                long flushStartTicks = Stopwatch.GetTimestamp();
                long nowMs = Environment.TickCount64;
                if (crosshairLastApplyMs > 0)
                {
                    long gap = nowMs - crosshairLastApplyMs;
                    if (gap > crosshairMaxGapMs) crosshairMaxGapMs = gap;
                    if (gap > 50)
                    {
                        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF 十字准星: 移动中刷新间隔偏大 gap={gap}ms (阈值 50ms)");
                    }
                }
                crosshairLastApplyMs = nowMs;

                var aimDip = new Avalonia.Point((aimScreen.X - ox) / s, (aimScreen.Y - oy) / s);
                layer.Crosshair = aimDip;
                layer.AimCursor = aimDip;
                UpdateCrosshairSegments(aimScreen, s, ox, oy);
                SetAimCursor(true);

                long flushCostTicks = Stopwatch.GetTimestamp() - flushStartTicks;
                crosshairFlushCount++;
                crosshairFlushTotalTicks += flushCostTicks;
                if (flushCostTicks > crosshairFlushMaxTicks) crosshairFlushMaxTicks = flushCostTicks;
            }
            else
            {
                layer.Crosshair = null;
                layer.CrosshairSegmentsV = null;
                layer.CrosshairSegmentsH = null;
                lastCrosshairAimX = null;
                lastCrosshairAimY = null;
                layer.AimCursor = null;
                SetAimCursor(false);
            }
            guidelineShown = true;
            if (IsAltDown()) ArmGuidelineWatchdog();
        }

        // 准星与瞄准光标的中心：优先用 PointerMoved 记下的点（与遮罩同帧），取不到再问系统光标
        bool TryResolveAimPoint(out System.Drawing.Point pt)
        {
            if (guidelinePointerScreen.HasValue)
            {
                pt = guidelinePointerScreen.Value;
                return true;
            }
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cp))
            {
                pt = new System.Drawing.Point(cp.X, cp.Y);
                return true;
            }
            pt = default;
            return false;
        }

        // (a) SelectionState.Changed → SelectionLayer.Region（DIP）
        selection.Changed += s =>
        {
            // SelectionState 在 UI 线程上被改（鼠标事件触发），无须 Post
            if (!window.IsVisible || mouseDragPending) return;
            UpdateSelectionVisual(s.HasSelection ? s.Region : null, hasManualSelection);
        };

        // (b) AutoDetectController.HighlightChanged → SelectionLayer.Region（仅手动选区前）
        // 窗口存活判定用 Closed 事件而非 IsVisible：首帧高亮预取发生在 Show() 之前，
        // 此时 IsVisible 仍为 false，用 IsVisible 判定会把预取高亮拦掉。
        bool captureWindowAlive = true;
        window.Closed += (_, _) => captureWindowAlive = false;
        autoDetect.HighlightChanged += rectScreen =>
        {
            void ApplyHighlight()
            {
                if (!captureWindowAlive) return;

                // 已经手动选过 / 正在手动操作 / 检测已暂停 / 工具激活 → 不让自动检测覆盖。忽略此高亮，并不再更新 currentHighlight。
                if (hasManualSelection || mode != InteractionMode.Idle || autoDetect.IsPaused || activeAnnotationTool != AnnotationTool.None)
                {
                    StopAutoHoverAnimation();
                    return;
                }

                currentHighlight = rectScreen;
                ApplyAutoHoverVisual(rectScreen);
                UpdateGuidelines();
            }

            // 已在 UI 线程（如 Show() 前的首帧高亮预取）时直接同步应用，
            // 避免 Post 一跳让首帧仍是无高亮的纯暗遮罩。
            if (Dispatcher.UIThread.CheckAccess()) ApplyHighlight();
            else Dispatcher.UIThread.Post(ApplyHighlight);
        };

        // (c0) 隧道监听 PointerMovedEvent：
        // Avalonia 在已有按键（如左键拖拽选区）按住时，再按其他鼠标键（中键）不会触发 PointerPressedEvent，
        // 而是将事件转为 PointerMovedEvent 派发（Avalonia MouseDevice.cs ButtonCount > 1 机制）。
        // 在隧道层监听中键按下边缘，使得按住左键拖拽选区时按下中键能直接完成贴图（对齐 F3 行为）。
        // ⚠ 这里**只**服务"左键正按住"那一种情形：那时中键的松开不会作为独立事件送回来，等松开就永远等不到。
        //    普通中键点击（未按左键）绝不在这里落定会话——按下即撤幕会让本段手势剩余的松开命中下层窗口（见 (e)）。
        bool wasMiddleButtonPressed = false;

        window.AddHandler(
            Avalonia.Input.InputElement.PointerMovedEvent,
            (_, e) =>
            {
                if (mouseDragPending) { e.Handled = true; return; }
                var pp = e.GetCurrentPoint(window);
                bool isMiddleDown = pp.Properties.IsMiddleButtonPressed;
                bool middleJustPressed = pp.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed
                    || (isMiddleDown && !wasMiddleButtonPressed);
                wasMiddleButtonPressed = isMiddleDown;

                if (middleJustPressed)
                {
                    e.Handled = true;
                    if (pp.Properties.IsLeftButtonPressed)
                    {
                        CreateStickerFromCurrentSelection();
                    }
                }
            },
            RoutingStrategies.Tunnel);

        // (c) PointerMoved → 推给自动检测（仅 Idle）；或更新手动操作
        window.PointerMoved += (_, e) =>
        {
            if (mouseDragPending) { e.Handled = true; return; }
            if (e.Handled) return;
            var dip = e.GetPosition(window);
            window.Layer.MousePosition = dip;

            bool isInteracting = mode != InteractionMode.Idle
                || drawingStroke
                || drawingArrow
                || drawingRectangle
                || drawingMosaic
                || drawingSpotlight
                || drawingEraser
                || draggingCounterTail
                || shapeEditor.IsEditing
                || window.IsDraggingText
                || window.IsRotatingText;

            if (!isInteracting && (window.IsOverlayHit(e.Source) || window.IsOverlayPoint(dip)))
            {
                brushPointerDip = null;
                window.Annotation.SetPointerFootprint(null, 0);
                StopAutoHoverAnimation();
                currentHighlight = null;
                HideGuidelines();
                HideMagnifier(window);
                try { autoDetect.IsPaused = true; } catch { }
                if (!aimCursorActive && window.Cursor != null) window.Cursor = null;
                return;
            }

            if (mode == InteractionMode.Idle && !hasManualSelection && activeAnnotationTool == AnnotationTool.None && autoDetect.IsPaused)
            {
                try { autoDetect.IsPaused = false; } catch { }
            }

            var screenPt = window.WindowDipToScreenPoint(dip);
            brushPointerDip = hasManualSelection && selection.HasSelection
                && IsPointInsideManualSelection(screenPt)
                && (activeAnnotationTool == AnnotationTool.Brush
                    || activeAnnotationTool == AnnotationTool.Eraser
                        && window.SelectedEraserMode == EraserGestureMode.Brush)
                ? dip : null;
            UpdateBrushPointerFootprint();
            bool brushCursorActive = brushPointerDip.HasValue && mode != InteractionMode.ResizingHandle;
            if (brushCursorActive && !ReferenceEquals(window.Cursor, noneCursor))
                window.Cursor = noneCursor;
            else if (activeAnnotationTool == AnnotationTool.Eraser
                && window.SelectedEraserMode == EraserGestureMode.Rectangle)
                window.Cursor = rectangleEraserCursor;
            guidelinePointerScreen = screenPt;

            // 记录瞬时鼠标速度，供 hover 闸门判断"是否在快速移动"。
            {
                long nowMs = Environment.TickCount64;
                if (lastPointerTickMs != 0)
                {
                    double dt = Math.Max(1.0, nowMs - lastPointerTickMs);
                    double dx = screenPt.X - lastPointerScreen.X;
                    double dy = screenPt.Y - lastPointerScreen.Y;
                    pointerSpeedPxPerMs = Math.Sqrt(dx * dx + dy * dy) / dt;
                }
                lastPointerScreen = screenPt;
                lastPointerTickMs = nowMs;
            }

            if (activeAnnotationTool == AnnotationTool.Brush && drawingStroke)
            {
                if (click1IsBrushCandidate)
                {
                    float dxMove = screenPt.X - lastClick1ScreenPt.X;
                    float dyMove = screenPt.Y - lastClick1ScreenPt.Y;
                    if (dxMove * dxMove + dyMove * dyMove > 16 * 16)
                    {
                        click1IsBrushCandidate = false;
                    }
                }

                lastBrushMousePoint = new PointF(screenPt.X, screenPt.Y);
                bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (isShift)
                {
                    shiftPressedAlone = false;
                    isBrushShiftConstrained = true;
                    float dx = lastBrushMousePoint.X - brushStartPoint.X;
                    float dy = lastBrushMousePoint.Y - brushStartPoint.Y;
                    currentStrokePoints.Clear();
                    currentStrokePoints.Add(new StrokePoint(brushStartPoint.X, brushStartPoint.Y, brushStartTimeMs));
                    if (Math.Sqrt((double)dx * dx + (double)dy * dy) >= 1.0)
                    {
                        PointF snapped = SnapStraightLine(brushStartPoint, lastBrushMousePoint);
                        currentStrokePoints.Add(new StrokePoint(snapped.X, snapped.Y, GetPreciseTimeMs()));
                    }
                    SetBrushPreviewThrottled();
                }
                else
                {
                    if (isBrushShiftConstrained)
                    {
                        isBrushShiftConstrained = false;
                        brushSmoother.Reset();
                        currentStrokePoints.Clear();
                        currentStrokePoints.AddRange(freehandStrokePoints);
                    }
                    if (IsPointInsideManualSelection(screenPt))
                    {
                        AppendBrushSample(new PointF(screenPt.X, screenPt.Y));
                    }
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Eraser && drawingEraser
                && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed
                && IsPointInsideManualSelection(screenPt))
            {
                var next = new PointF(screenPt.X, screenPt.Y);
                if (EraserProbe) probeEraseEvents++;
                EraseAnnotationsAlong(lastEraserPoint, next);
                lastEraserPoint = next;
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Arrow && drawingArrow)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                // Shift 锁直线：与画笔共用 SnapStraightLine（45° 八向量化，本体在 Core），量化只改方向不缩长度
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) end = SnapStraightLine(arrowStart, end);
                window.Annotation.SetPreviewShape(new ArrowShape(
                    arrowStart,
                    end,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedArrowStyle,
                    currentArrowScale));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Rectangle && drawingRectangle)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                // Shift 锁 1:1（矩形档锁成正方形、椭圆档锁成正圆）：与马赛克/聚光灯共用 CreateDrawRect
                // 那一份算式（本体在 Core，贴图侧读同一个数），判据每帧现读 ⇒ 拖动中途按下/松开即时切换。
                RectangleF rect = CreateDrawRect(rectangleStart, end, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                window.Annotation.SetPreviewShape(new RectangleShape(
                    rect,
                    window.SelectedAnnotationColor,
                    window.SelectedStrokeThickness,
                    window.SelectedShapeKind,
                    window.SelectedShapeLineStyle,
                    window.SelectedShapeCornerRadius,
                    window.SelectedShapeFillOpacity));
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Mosaic && drawingMosaic)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                RectangleF rect = CreateDrawRect(mosaicStart, end, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                window.Annotation.SetPreviewShape(new BlurShape(rect, window.SelectedBlurMode, window.SelectedBlurUiLevel));
                if (!aimCursorActive && window.Cursor != null) window.Cursor = null;
                e.Handled = true;
                return;
            }

            // 模糊的移动/缩放原先在这里各占一支，现与其余图元合流，见下面的 shapeEditor.IsEditing。

            if (activeAnnotationTool == AnnotationTool.Spotlight && drawingSpotlight)
            {
                var end = new PointF(screenPt.X, screenPt.Y);
                RectangleF rect = ClampSpotlightRect(CreateDrawRect(spotlightStart, end, e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
                SetSpotlightPreviewThrottled(new SpotlightShape(
                    rect,
                    window.SelectedSpotlightDarkness,
                    window.SelectedSpotlightKind,
                    window.SelectedAnnotationColor,
                    window.SelectedSpotlightStrokeThickness,
                    window.SelectedSpotlightCornerRadius));
                if (!aimCursorActive && window.Cursor != null) window.Cursor = null;
                e.Handled = true;
                return;
            }

            // 序号引线：本击刚建的徽章被同 id 的预览就地顶替，拖动期间只画不写文档
            if (activeAnnotationTool == AnnotationTool.Counter && draggingCounterTail)
            {
                UpdateCounterTailPreview(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                return;
            }

            // 六类图元的"拖本体／拖手柄"手势塌成这一支（阶段 3 的共享编辑器）：判据不再按"类型 × 手势"
            // 组合枚举，起手抓到的是哪个把手全部由编辑器握着。这里只把最新坐标交给 8ms 时间闸，
            // 每至多一帧落一次图层预览，松手时才一次性写回文档（阶段 1 的"只推迟不丢弃"口径不变）。
            if (shapeEditor.IsEditing)
            {
                RequestShapeGestureMove(new PointF(screenPt.X, screenPt.Y));
                e.Handled = true;
                return;
            }

            // 笔触圈已在本次移动的开头接管光标，不能再先给悬停箭头、最后又改回隐藏。
            // 选区缩放仍走下面的方向光标与拖动收口；其它标注的悬停判据保持原样。
            if (brushCursorActive) return;

            if (activeAnnotationTool == AnnotationTool.Mosaic)
            {
                PointF pt = new PointF(screenPt.X, screenPt.Y);
                RectangleF? hover = null;
                foreach (RectangleF r in autoMosaicHighlights)
                {
                    if (Contains(r, pt)) { hover = r; break; }
                }
                window.Annotation.HoverAutoMosaicHighlight = hover;
                window.Annotation.InvalidateVisual();
                bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                Func<PointF, AnnotationShape?>? hitBody = isCtrl
                    ? (Func<PointF, AnnotationShape?>)(p => AnnotationHitTest.FindTopmost(annotations, p, selectedShapeId))
                    : (p => AnnotationHitTest.FindBlurShapeAt(annotations, p));
                window.Cursor = EditCursor(pt, hitBody);
            }
            else if (activeAnnotationTool == AnnotationTool.Spotlight)
            {
                bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                Func<PointF, AnnotationShape?>? hitBody = isCtrl
                    ? (Func<PointF, AnnotationShape?>)(p => AnnotationHitTest.FindTopmost(annotations, p, selectedShapeId))
                    : FindSpotlightShapeAt;
                window.Cursor = EditCursor(new PointF(screenPt.X, screenPt.Y), hitBody);
            }
            else if (activeAnnotationTool != AnnotationTool.None)
            {
                PointF pt = new PointF(screenPt.X, screenPt.Y);
                bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                Func<PointF, AnnotationShape?>? hitBody = isCtrl
                    ? (Func<PointF, AnnotationShape?>)(p => AnnotationHitTest.FindTopmost(annotations, p, selectedShapeId))
                    : (activeAnnotationTool switch
                    {
                        AnnotationTool.Rectangle => p => AnnotationHitTest.FindRectangleShapeAt(annotations, p),
                        AnnotationTool.Arrow => p => AnnotationHitTest.FindArrowShapeAt(annotations, p, selectedShapeId),
                        AnnotationTool.Counter => p => AnnotationHitTest.FindCounterShapeAt(annotations, p),
                        // 画笔免 Ctrl 点不到（2026-09-18 例外档），光标也就不能许诺"可拖"——判据与
                        // AnnotationShapeEditor.TryBeginPress 那一路必须同源，否则就是"看着能抓、点下去起新笔"。
                        _ => null,
                    });
                window.Cursor = EditCursor(pt, hitBody);
            }
            else
            {
                // 阶段一的十字瞄准光标由 SetAimCursor 统一下发，这里别把它抢回成箭头
                if (!aimCursorActive) window.Cursor = null;
            }

            // 选区缩放带的光标反馈：判据与下面按下派发完全同一份，故"看着能拖"＝"真能拖"。
            // 在选区确认态与标注态下，命中手柄/缩放带或正在拖选手柄时接管光标（已选中图元手柄优先）。
            if (hasManualSelection && selection.HasSelection)
            {
                // 已选中图元的手柄在按下时排在选区缩放之前（protection.md 约束 ①②），光标也要让给它
                AnnotationShape? hoverSelected = activeAnnotationTool != AnnotationTool.None ? GetSelectedShape() : null;
                bool shapeHandleWins = hoverSelected != null
                    && AnnotationHitTest.HitTestEditTarget(hoverSelected, new PointF(screenPt.X, screenPt.Y),
                        isSelected: true).IsHandle;
                if (!shapeHandleWins)
                {
                    HandleDirection grip = mode == InteractionMode.ResizingHandle
                        ? resizeDirection
                        : selection.HitTestResizeDirection(screenPt, HandleHitRadiusPx, BorderGripPx);
                    if (ResizeCursorOf(grip) is Cursor resizeCursor) window.Cursor = resizeCursor;
                }
            }

            // 工具激活期间的其余移动仍归标注自己；唯独缩放选区这一段要一路走到松手的 SetRegion 收口
            if (activeAnnotationTool != AnnotationTool.None && mode != InteractionMode.ResizingHandle) return;

            if (pendingExpandResize)
            {
                bool leftPressed = e.GetCurrentPoint(window).Properties.IsLeftButtonPressed;
                if (!leftPressed)
                {
                    pendingExpandResize = false;
                    pendingExpandResizeDirection = HandleDirection.None;
                }
                else
                {
                    int dx0 = screenPt.X - pendingExpandStartPoint.X;
                    int dy0 = screenPt.Y - pendingExpandStartPoint.Y;
                    if (Math.Abs(dx0) >= DragThresholdPx || Math.Abs(dy0) >= DragThresholdPx)
                    {
                        mode = InteractionMode.ResizingHandle;
                        resizeDirection = pendingExpandResizeDirection;
                        dragStartScreen = pendingExpandStartPoint;
                        dragStartRect = pendingExpandStartRect;
                        dragMovedPastThreshold = true;
                        pendingExpandResize = false;
                        pendingExpandResizeDirection = HandleDirection.None;
                        BeginSelectionTransform();
                        try { e.Pointer.Capture(window); } catch { }
                    }
                }
            }

            switch (mode)
            {
                case InteractionMode.Idle:
                    autoDetect.NotifyPointerMoved(screenPt);
                    break;

                case InteractionMode.DraggingNew:
                    if (!dragMovedPastThreshold &&
                        (Math.Abs(screenPt.X - dragStartScreen.X) >= DragThresholdPx ||
                        Math.Abs(screenPt.Y - dragStartScreen.Y) >= DragThresholdPx))
                    {
                        dragMovedPastThreshold = true;
                        currentHighlight = null;
                        StopAutoHoverAnimation();
                    }
                    if (dragMovedPastThreshold)
                    {
                        if (isSpacePressed)
                        {
                            int dx = screenPt.X - lastDragPointerScreen.X;
                            int dy = screenPt.Y - lastDragPointerScreen.Y;
                            dragStartScreen = new System.Drawing.Point(dragStartScreen.X + dx, dragStartScreen.Y + dy);
                        }
                        lastDragPointerScreen = screenPt;

                        int x = Math.Min(dragStartScreen.X, screenPt.X);
                        int y = Math.Min(dragStartScreen.Y, screenPt.Y);
                        int w = Math.Abs(screenPt.X - dragStartScreen.X);
                        int h = Math.Abs(screenPt.Y - dragStartScreen.Y);
                        selection.SetRegion(new Rectangle(x, y, w, h));
                    }
                    break;

                case InteractionMode.MovingSelection:
                    {
                        int dx = screenPt.X - dragStartScreen.X;
                        int dy = screenPt.Y - dragStartScreen.Y;
                        dragMovedPastThreshold = dragMovedPastThreshold || dx != 0 || dy != 0;
                        if (dragMovedPastThreshold)
                        {
                            UpdateTransformVisual(new Rectangle(
                                dragStartRect.X + dx, dragStartRect.Y + dy,
                                dragStartRect.Width, dragStartRect.Height));
                        }
                    }
                    break;

                case InteractionMode.ResizingHandle:
                    {
                        int dx = screenPt.X - dragStartScreen.X;
                        int dy = screenPt.Y - dragStartScreen.Y;
                        dragMovedPastThreshold = dragMovedPastThreshold || dx != 0 || dy != 0;
                        if (dragMovedPastThreshold)
                        {
                            UpdateTransformVisual(CalculateResizedRect(dragStartRect, resizeDirection, dx, dy));
                        }
                    }
                    break;
            }

            RequestGuidelinesUpdate();

            bool showMagnifier = activeAnnotationTool == AnnotationTool.None &&
                                 (mode == InteractionMode.DraggingNew || mode == InteractionMode.ResizingHandle || (mode == InteractionMode.Idle && !selection.HasSelection));

            if (showMagnifier && frame != null)
            {
                UpdateMagnifier(window, dip, frame);
            }
            else
            {
                HideMagnifier(window);
            }
        };

        // (d) PointerPressed → 决定进入哪种 InteractionMode
        // 1. 优先通过 Tunnel 隧道策略在顶层窗口捕获双击手势，
        //    防止在文本工具下 Click 1 生成的 TextBox 在 Click 2 时因 Avalonia TextBox 原生选词逻辑吞掉双击（e.Handled=true）导致无法确认截图。
        window.AddHandler(
            Avalonia.Input.InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (pathEditor.Mode != CaptureSelectionMode.Rectangle) return;
                var pp = e.GetCurrentPoint(window);
                if (pp.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

                var dip = e.GetPosition(window);
                if (window.IsOverlayPoint(dip) || window.IsOverlayHit(e.Source)) return;

                var screenPt = window.WindowDipToScreenPoint(dip);

                if (e.ClickCount == 1)
                {
                    lastClick1TickMs = Environment.TickCount64;
                    lastClick1ScreenPt = screenPt;
                    click1CreatedAnnotation = false;
                    click1IsBrushCandidate = (activeAnnotationTool == AnnotationTool.Brush);
                }
                else if (e.ClickCount >= 2)
                {
                    bool isInside = (hasManualSelection && selection.HasSelection && selection.Region.Contains(screenPt))
                                 || (!hasManualSelection && (currentHighlight.HasValue || pressedHighlight.HasValue));

                    if (isInside)
                    {
                        // 如果在文本工具下且非首击刚创建的误触文本框，且命中了已有非空文本，放行给双击编辑
                        if (activeAnnotationTool == AnnotationTool.Text && !click1CreatedAnnotation)
                        {
                            TextLayerInfo? hit = window.FindTextAtPoint(dip);
                            if (hit != null && hit.TextBox != null && !string.IsNullOrEmpty(hit.TextBox.Text))
                            {
                                return;
                            }
                        }

                        e.Handled = true;
                        RevertDoubleClickCandidateAnnotation(screenPt);
                        ConfirmCurrentSelection();
                    }
                }
            },
            RoutingStrategies.Tunnel);

        // 2. 正常冒泡处理各工具与选区交互逻辑
        window.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Handled) return;
            var pp = e.GetCurrentPoint(window);
            if (pp.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
            {
                // 只吞不办：取消改到 (e) 的 RightButtonReleased。按下即落定会话会在本段手势结束前撤掉遮罩，
                // 剩下的松开会被系统按位置命中下层窗口，被它当成一次完整右键而弹出自己的菜单。
                e.Handled = true;
                return;
            }
            if (pp.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
            {
                wasMiddleButtonPressed = true;
                e.Handled = true; // 同上：贴图改到 (e) 的 MiddleButtonReleased
                return;
            }

            var pressDip = e.GetPosition(window);
            bool isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool isToolActive = activeAnnotationTool != AnnotationTool.None;

            if (window.IsOverlayPoint(pressDip) || window.IsOverlayHit(e.Source))
            {
                StopAutoHoverAnimation();
                currentHighlight = null;
                pressedHighlight = null;
                try { autoDetect.IsPaused = true; } catch { }

                // 无工具 + Ctrl + 点击文本图层 → 激活文本工具并选中
                if (!isToolActive && isCtrlPressed && hasManualSelection && selection.HasSelection)
                {
                    var ovDip = e.GetPosition(window);
                    TextLayerInfo? hitText = window.FindTextAtPoint(ovDip);
                    if (hitText != null)
                    {
                        e.Handled = true;
                        ClearAllSelections();
                        ForceAnnotationTool(AnnotationTool.Text);
                        window.SelectText(hitText);
                        try { e.Pointer.Capture(window); } catch { }
                        return;
                    }
                }

                // 文本工具下 Ctrl+点击其他标注图形 → 切换到对应工具并选中
                if (activeAnnotationTool == AnnotationTool.Text && _isCtrlPressed)
                {
                    var ovPt = e.GetPosition(window);
                    var ovScreen = window.WindowDipToScreenPoint(ovPt);
                    // 尝试切换到其他工具
                    if (TrySwitchFromTextToOtherAnnotation(e, new PointF(ovScreen.X, ovScreen.Y))) return;
                    // 如果没有命中其他图形，也不应该创建新文本
                    return;
                }

                // 文本工具特例：点击 OverlayCanvas 时需要检测选中/创建文本
                if (activeAnnotationTool == AnnotationTool.Text)
                {
                    var ovDip = e.GetPosition(window);
                    if (!window.TryHandleTextToolInteraction(ovDip, e.ClickCount))
                    {
                        // ⚠ 建框前必须比一次选区：覆盖层的命中矩形含工具条的 Padding 留白与圆角外沿，
                        // 只判"点到了覆盖层"就会在选区外的空白处弹出输入框（2026-09-20 用户实报）。
                        // 判据与主链路那一支同一条 IsPointInsideManualSelection，不在此重复一份。
                        var ovScreen = window.WindowDipToScreenPoint(ovDip);
                        if (hasManualSelection && selection.HasSelection
                            && IsPointInsideManualSelection(ovScreen))
                        {
                            captureHistory.CommitSnapshot();
                            window.AddTextAt(ovDip);
                            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.Text: 在 (" + ovDip.X + "," + ovDip.Y + ") 创建文本");
                            long elapsed = Environment.TickCount64 - lastClick1TickMs;
                            if (elapsed <= 800)
                            {
                                click1CreatedAnnotation = true;
                            }
                        }
                    }
                    if (window.IsDraggingText)
                    {
                        try { e.Pointer.Capture(window); } catch { }
                    }
                }
                return;
            }

            if (pp.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

            // 鼠标一旦点击，立即暂停自动检测元素，给选区让步，优先级max
            try { autoDetect.IsPaused = true; } catch { }

            var dip = e.GetPosition(window);
            var screenPt = window.WindowDipToScreenPoint(dip);

            if (e.ClickCount == 1)
            {
                lastClick1TickMs = Environment.TickCount64;
                lastClick1ScreenPt = screenPt;
                click1CreatedAnnotation = false;
                click1IsBrushCandidate = (activeAnnotationTool == AnnotationTool.Brush);
            }

            // 任何标注工具处于激活状态时，都允许双击直接确认截图；
            // 并在确认前撤销双击第一击在落笔点可能产生的误触图形（如画笔单点、序号徽章、空文字框）。
            if (e.ClickCount >= 2 && activeAnnotationTool != AnnotationTool.None)
            {
                // 如果是双击已有的旧文本图层（非首击刚创建的），放行给文本编辑逻辑
                if (activeAnnotationTool == AnnotationTool.Text && !click1CreatedAnnotation && window.FindTextAtPoint(dip) != null)
                {
                    // 允许走后续的文本编辑命中链路
                }
                else
                {
                    e.Handled = true;
                    RevertDoubleClickCandidateAnnotation(screenPt);
                    ConfirmCurrentSelection();
                    return;
                }
            }

            // Ctrl+点击文本图层 → 切回文本工具并选中（不进入编辑态）
            if (isCtrlPressed && isToolActive)
            {
                var textHitDip = e.GetPosition(window);
                TextLayerInfo? ctrlTextHit = window.FindTextAtPoint(textHitDip);
                if (ctrlTextHit != null)
                {
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Text);
                    window.SelectText(ctrlTextHit);
                    try { e.Pointer.Capture(window); } catch { }
                    return;
                }
            }

            // 文本工具必须在主点击链路处理之前，优先检测 Ctrl+点击其他标注图形
            if (hasManualSelection && selection.HasSelection && activeAnnotationTool == AnnotationTool.Text && _isCtrlPressed)
            {
                PointF ctrlScreenPt = new PointF(screenPt.X, screenPt.Y);
                if (TrySwitchFromTextToOtherAnnotation(e, ctrlScreenPt)) return;
            }

            // 文本工具必须在主点击链路处理：点击截图图层时 e.Source 通常不是 OverlayCanvas。
            if (hasManualSelection && selection.HasSelection && activeAnnotationTool == AnnotationTool.Text && IsPointInsideManualSelection(screenPt))
            {
                e.Handled = true;
                var textDip = e.GetPosition(window);
                if (!window.TryHandleTextToolInteraction(textDip, e.ClickCount))
                {
                    captureHistory.CommitSnapshot();
                    window.AddTextAt(textDip);
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.Text: 在 (" + textDip.X + "," + textDip.Y + ") 创建文本");
                    long elapsed = Environment.TickCount64 - lastClick1TickMs;
                    if (elapsed <= 800)
                    {
                        click1CreatedAnnotation = true;
                    }
                }
                if (window.IsDraggingText)
                {
                    try { e.Pointer.Capture(window); } catch { }
                }
                return;
            }

            // 手势优先级：
            // 1. 已选中图元手柄永远免 Ctrl，直接起拖；
            // 2. 当前工具激活时，无需按住 Ctrl 即可直接点选当前工具对应的图元（同工具免 Ctrl）；
            //    画笔是这条的唯一例外（2026-09-18）——它免 Ctrl 点不到，选中老笔迹一律按 Ctrl；
            // 3. 跨标注工具选择时，需按住 Ctrl+点击；未按 Ctrl 且未命中当前工具图元时，按当前工具继续画新图形。
            //    形状工具"没命中"的口径含 2026-09-18 的改动：未填充时体内空白不算命中，只有线条那一圈算。
            bool editExisting = isCtrlPressed;

            if (activeAnnotationTool != AnnotationTool.Eraser && hasManualSelection && selection.HasSelection && (editExisting || AnyShapeSelected() || activeAnnotationTool != AnnotationTool.None))
            {
                PointF ctrlPt = new PointF(screenPt.X, screenPt.Y);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PointerPressed: 进入形状选择逻辑 at ({ctrlPt.X}, {ctrlPt.Y}), isCtrl={isCtrlPressed}, tool={activeAnnotationTool}, shapesCount={annotations.Count}");
                if (TryBeginShapeEdit(e, ctrlPt, editExisting, _ =>
                {
                    // 文本图层：命中就走文本自己的点选/起拖通路（它是活可视树，不在共享编辑器的六种图元里）
                    var textDip = e.GetPosition(window);
                    if (window.FindTextAtPoint(textDip) == null) return false;
                    e.Handled = true;
                    ClearAllSelections();
                    ForceAnnotationTool(AnnotationTool.Text);
                    window.TryHandleTextToolInteraction(textDip, e.ClickCount);
                    try { e.Pointer.Capture(window); } catch { }
                    return true;
                })) return;
            }

            // 标注态直接拖选区（2026-09-09 方案 A）：命中选区缩放带/手柄＝缩放选区，判据与起手动作
            // 与工具未激活那一路完全是同一个函数。位置是硬约束——
            // ① 晚于上面的图元编辑链：已选中图元的手柄排在选区缩放之前（protection.md 约束 ①②）；
            // ② 晚于「工具激活时双击＝复制并完成截图」那一支；③ 早于下面"画新一笔"那一大段工具分支。
            if (activeAnnotationTool != AnnotationTool.None && activeAnnotationTool != AnnotationTool.Eraser
                && TryBeginSelectionResize(e, screenPt)) return;

            if (activeAnnotationTool != AnnotationTool.None)
            {
                e.Handled = true;
                if (IsPointInsideManualSelection(screenPt))
                {
                    if (activeAnnotationTool == AnnotationTool.Brush
                        || activeAnnotationTool == AnnotationTool.Eraser && window.SelectedEraserMode == EraserGestureMode.Brush)
                    {
                        // 起笔也由笔触圈接管，避免按下瞬间闪回箭头，或切工具后未移动就起笔时漏掉接管。
                        brushPointerDip = e.GetPosition(window);
                        UpdateBrushPointerFootprint();
                        if (!ReferenceEquals(window.Cursor, noneCursor)) window.Cursor = noneCursor;
                    }
                    else if (!aimCursorActive && window.Cursor != null) window.Cursor = null;
                    if (activeAnnotationTool == AnnotationTool.Brush)
                    {
                        drawingStroke = true;
                        currentStrokePoints.Clear();
                        freehandStrokePoints.Clear();
                        brushSmoother.Reset();
                        lastBrushPreviewTick = 0;
                        window.Annotation.ClearPreviewShape();

                        PointF pt = new PointF(screenPt.X, screenPt.Y);
                        brushStartPoint = pt;
                        brushStartTimeMs = GetPreciseTimeMs();
                        lastBrushMousePoint = pt;

                        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                        if (isShift)
                        {
                            shiftPressedAlone = false;
                            isBrushShiftConstrained = true;
                            currentStrokePoints.Add(new StrokePoint(pt.X, pt.Y, brushStartTimeMs));
                            freehandStrokePoints.Add(new StrokePoint(pt.X, pt.Y, brushStartTimeMs));
                            UpdateBrushPreview();
                        }
                        else
                        {
                            isBrushShiftConstrained = false;
                            AppendBrushSample(pt);
                        }
                    }
                    else if (activeAnnotationTool == AnnotationTool.Eraser)
                    {
                        // 橡皮擦工具下 Ctrl+点击＝把点到的那一个"提到橡皮擦之上"：从所有既有擦除记录的影响名单里
                        // 摘掉它，于是它整块显形并且**一直保持**（取消选中不回缩），直到下一次橡皮擦再盖住它。
                        // 这里不起擦除手势、不新建洞，只改"洞管谁"；一条撤销即可收回。
                        if (isCtrlPressed)
                        {
                            RaiseTargetAboveEraser(new PointF(screenPt.X, screenPt.Y), window.FindTextAtPoint(dip));
                            e.Handled = true;
                            return;
                        }
                        drawingEraser = true;
                        eraserGestureMode = window.SelectedEraserMode;
                        eraserHistoryCommitted = false;
                        // 起手这一枚不闸：闸门读数清零，落笔当下立刻出一个洞，别让人觉得"点下去没反应"
                        eraserLastPublishMs = 0;
                        eraserPublishPending = false;
                        eraserPublishCostMs = 0;
                        if (EraserProbe)
                        {
                            probeEraseGestureStartMs = GetPreciseTimeMs();
                            AnnotationLayer.ResetEraserProbe();
                        }
                        eraserStartPoint = lastEraserPoint = new PointF(screenPt.X, screenPt.Y);
                        currentErasureMarks.Clear();
                        currentErasure = new ErasureShape(
                            annotations.Shapes.Where(shape => shape is not ErasureShape).Select(shape => shape.Id),
                            window.TextLayers.Select(layer => layer.Id), Array.Empty<ErasureMark>());
                        if (eraserGestureMode == EraserGestureMode.Brush)
                            EraseAnnotationsAt(lastEraserPoint);
                        try { e.Pointer.Capture(window); } catch { }
                    }
                    else if (activeAnnotationTool == AnnotationTool.Arrow)
                    {
                        drawingArrow = true;
                        arrowStart = new PointF(screenPt.X, screenPt.Y);
                        window.Annotation.ClearPreviewShape();
                    }
                    else if (activeAnnotationTool == AnnotationTool.Rectangle)
                    {
                        drawingRectangle = true;
                        rectangleStart = new PointF(screenPt.X, screenPt.Y);
                        window.Annotation.ClearPreviewShape();
                    }
                    else if (activeAnnotationTool == AnnotationTool.Mosaic)
                    {
                        PointF pt = new PointF(screenPt.X, screenPt.Y);

                        int autoIndex = autoMosaicHighlights.FindIndex(r => Contains(r, pt));
                        if (autoIndex >= 0)
                        {
                            RectangleF r = autoMosaicHighlights[autoIndex];
                            autoMosaicHighlights.RemoveAt(autoIndex);
                            window.Annotation.SetAutoMosaicHighlights(autoMosaicHighlights.ToArray());
                            captureHistory.CommitSnapshot();
                            var blur = new BlurShape(r, window.SelectedBlurMode, window.SelectedBlurUiLevel);
                            annotations.AddShape(blur);
                            SelectBlurShape(blur.Id);
                            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.AutoMosaic: 点击识别框创建效果，rect=" + r + ", mode=" + window.SelectedBlurMode + ", uiLevel=" + window.SelectedBlurUiLevel);
                            return;
                        }

                        // 同聚光灯：选中模糊的手柄与 Ctrl+点本体都已在 TryBeginShapeEdit 派发掉，这里只剩起笔画新的。

                        SelectBlurShape(null);
                        drawingMosaic = true;
                        mosaicStart = pt;
                    }
                    else if (activeAnnotationTool == AnnotationTool.Spotlight)
                    {
                        // 选中聚光灯的缩放手柄与 Ctrl+点本体，都已在上面的 TryBeginShapeEdit 一次派发掉
                        //（圆角手柄已于 2026-09-18 退役，改走元素上方的圆角浮条，不在本派发链里）；
                        // 这一段从前逐字重复过那份判据，而派发块在它之前跑，故永远走不到——现在只留"起笔画一个新的"。
                        SelectSpotlightShape(null);
                        drawingSpotlight = true;
                        spotlightStart = new PointF(screenPt.X, screenPt.Y);
                        window.Annotation.ClearPreviewShape();
                        lastSpotlightPreviewStamp = 0;
                    }
                    else if (activeAnnotationTool == AnnotationTool.Counter)
                    {
                        AddCounter(new PointF(screenPt.X, screenPt.Y));
                    }
                }
                return;
            }

            // 双击：直接确认（不需要先有手动拖动；自动检测高亮也接受）
            if (e.ClickCount >= 2)
            {
                e.Handled = true;
                RevertDoubleClickCandidateAnnotation(screenPt);
                ConfirmCurrentSelection();
                return;
            }

            dragStartScreen = screenPt;
            dragMovedPastThreshold = false;

            if (hasManualSelection && selection.HasSelection)
            {
                // 在手柄/缩放带上？（起手动作与标注态那一路共用 TryBeginSelectionResize，不写第二份）
                if (TryBeginSelectionResize(e, screenPt)) return;

                // 在选区内部？拖动平移
                if (selection.Region.Contains(screenPt))
                {
                    mode = InteractionMode.MovingSelection;
                    dragStartRect = selection.Region;
                    BeginSelectionTransform();
                    try { e.Pointer.Capture(window); } catch { }
                    e.Handled = true;
                    return;
                }

                // 在选区外，按旧版逻辑先扩展当前选区；按住继续拖动可继续缩放。
                if (TryExpandSelectionByMaskClick(screenPt))
                {
                    BeginSelectionTransform();
                    try { e.Pointer.Capture(window); } catch { }
                    e.Handled = true;
                    return;
                }

                // 扩展未生效时才重新拖框（清空旧选区）
                hasManualSelection = false;
                activeAnnotationTool = AnnotationTool.None;
                drawingStroke = false;
                drawingArrow = false;
                drawingRectangle = false;
                drawingMosaic = false;
                drawingSpotlight = false;
                draggingCounterTail = false;
                currentStrokePoints.Clear();
                window.SetActiveTool(null);
                annotations.Clear();
                captureHistory.Clear();
                selection.Clear();
                StopAutoHoverAnimation();
                mode = InteractionMode.DraggingNew;
                window.SetShortcutHintState(ShortcutHintState.DraggingNew);
                guidelinePointerScreen = screenPt;
                UpdateGuidelines();
                try { e.Pointer.Capture(window); } catch { }
                e.Handled = true;
                return;
            }

            // 无手动选区时：开始拖框；仅当点击位置确实位于高亮元素内部时才记录为单击候选，并立即清除隐藏高亮以提供灵敏的框选体验。
            if (currentHighlight.HasValue && currentHighlight.Value.Contains(screenPt))
            {
                pressedHighlight = currentHighlight;
                // 上面那句 autoDetect.IsPaused = true 会同步把检测框的遮罩挖洞清空，于是按住不松手的
                // 这段时间整屏一直是暗的、松手才亮回——这就是"闪一下"。这里按同一矩形立刻把洞钉回去，
                // 让检测框在按住期间保持发亮，松手转正式选区时由同一矩形无缝接管、不再经历暗→亮跳变。
                window.Layer.AutoHoverRegion = window.ScreenRectToWindowDip(pressedHighlight.Value);
                currentHighlight = null;
            }
            else
            {
                pressedHighlight = null;
                currentHighlight = null;
                StopAutoHoverAnimation();
            }

            mode = InteractionMode.DraggingNew;
            window.SetShortcutHintState(ShortcutHintState.DraggingNew);
            guidelinePointerScreen = screenPt;
            // 按下开始拉选区时保留中心准星与瞄准光标，跟随当前拖拽角点
            UpdateGuidelines();
            try { e.Pointer.Capture(window); } catch { }
            e.Handled = true;
        });

        // (e) PointerReleased → 结束本次手势
        window.PointerReleased += (_, e) =>
        {
            wasMiddleButtonPressed = e.GetCurrentPoint(window).Properties.IsMiddleButtonPressed;
            if (e.Handled) return;
            var releaseDip = e.GetPosition(window);
            var pp = e.GetCurrentPoint(window);

            // 右键取消 / 中键贴图的正主：手势到这里才算完整走完（按下那两路只吞不办）。
            // ⚠ 位次是硬约束：必须排在下面的 `IsOverlayPoint || IsOverlayHit` 早退**之前**，
            //    与按下侧排在 `pressDip` 覆盖层判定之前完全对称——否则鼠标停在工具栏、参数条、
            //    圆角浮条上时松开直接早退，右键取消与中键贴图双双失效。
            if (pp.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
            {
                e.Handled = true;
                CancelCapture();
                return;
            }
            if (pp.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            {
                e.Handled = true;
                CreateStickerFromCurrentSelection();
                return;
            }

            bool isInteracting = mode != InteractionMode.Idle
                || drawingStroke
                || drawingArrow
                || drawingRectangle
                || drawingMosaic
                || drawingSpotlight
                || draggingCounterTail
                || shapeEditor.IsEditing
                || window.IsDraggingText
                || window.IsRotatingText;

            if (!isInteracting && (window.IsOverlayPoint(releaseDip) || window.IsOverlayHit(e.Source)))
            {
                StopAutoHoverAnimation();
                currentHighlight = null;
                pressedHighlight = null;
                try { autoDetect.IsPaused = true; } catch { }
                return;
            }

            // 仅左键释放（右键、中键已在处理器开头收口）
            if (pp.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;

            // 缩放选区那一段不在此列：它要落到下面的 SetRegion + EndSelectionTransform 收口
            if (activeAnnotationTool != AnnotationTool.None && mode != InteractionMode.ResizingHandle)
            {
                var dip = e.GetPosition(window);
                var screenPt = window.WindowDipToScreenPoint(dip);

                if (activeAnnotationTool == AnnotationTool.Brush && drawingStroke)
                {
                    bool isShift = isBrushShiftConstrained || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    if (isShift)
                    {
                        shiftPressedAlone = false;
                        PointF endPt = new PointF(screenPt.X, screenPt.Y);
                        float dx = endPt.X - brushStartPoint.X;
                        float dy = endPt.Y - brushStartPoint.Y;
                        currentStrokePoints.Clear();
                        currentStrokePoints.Add(new StrokePoint(brushStartPoint.X, brushStartPoint.Y, brushStartTimeMs));
                        if (Math.Sqrt((double)dx * dx + (double)dy * dy) >= 1.0)
                        {
                            PointF snapped = SnapStraightLine(brushStartPoint, endPt);
                            currentStrokePoints.Add(new StrokePoint(snapped.X, snapped.Y, GetPreciseTimeMs()));
                        }
                    }
                    else
                    {
                        // 收笔点绕过输入平滑直接入库：滤波天然落后原始点约一个时间常数，否则笔尾会停在光标后面
                        if (IsPointInsideManualSelection(screenPt))
                        {
                            var endPt = new PointF(screenPt.X, screenPt.Y);
                            if (ShouldAddBrushPoint(currentStrokePoints, endPt))
                            {
                                currentStrokePoints.Add(new StrokePoint(
                                    endPt.X,
                                    endPt.Y,
                                    GetPreciseTimeMs()));
                            }
                        }
                    }
                    FinishStroke();
                }
                else if (activeAnnotationTool == AnnotationTool.Eraser && drawingEraser)
                {
                    var endPt = new PointF(screenPt.X, screenPt.Y);
                    EraseAnnotationsAlong(lastEraserPoint, endPt);
                    // 收笔必须把被闸门压住的最后一段补落：否则洞缺尾，且撤销快照里少一段、与松手前所见不一致
                    if (eraserPublishPending) PublishEraserMarks(true);
                    if (eraserHistoryCommitted && currentErasureMarks.Count == 0)
                        captureHistory.DiscardLatestSnapshot();
                    if (EraserProbe)
                    {
                        double freq = Stopwatch.Frequency;
                        double renderMs = AnnotationLayer.ProbeRenderTicks * 1000.0 / freq;
                        double renderMaxMs = AnnotationLayer.ProbeRenderMaxTicks * 1000.0 / freq;
                        double clipMs = AnnotationLayer.ProbeClipTicks * 1000.0 / freq;
                        double clipBuildMs = AnnotationLayer.ProbeClipBuildTicks * 1000.0 / freq;
                        double clipPushMs = AnnotationLayer.ProbeClipPushTicks * 1000.0 / freq;
                        // 闸门末值＝收笔那一刻的放宽量：16 就是"全程没放宽"，说明这一手势的重绘并不贵，病灶在别处
                        double gateMs = EraserPublishGateMs();
                        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("[橡皮擦取证] 模式=" + eraserGestureMode
                            + " 采样=" + probeEraseEvents + "次 发布=" + probeErasePublishes + "次"
                            + " 落点峰值=" + probeEraseMarksPeak + " 图元数=" + annotations.Count
                            + " | 发布合计=" + probeErasePublishMs.ToString("F1") + "ms 峰值=" + probeErasePublishMaxMs.ToString("F1") + "ms"
                            + " 闸门=" + gateMs.ToString("F1") + "ms"
                            + " | 整层重绘=" + AnnotationLayer.ProbeRenderCount + "次 合计=" + renderMs.ToString("F1") + "ms 单次峰值=" + renderMaxMs.ToString("F1") + "ms"
                            + " 其中洞几何=" + clipMs.ToString("F1") + "ms/" + AnnotationLayer.ProbeClipCount + "次"
                            + "（攒路径=" + clipBuildMs.ToString("F1") + "ms 推裁切=" + clipPushMs.ToString("F1") + "ms）"
                            + " | 手势=" + (GetPreciseTimeMs() - probeEraseGestureStartMs).ToString("F0") + "ms");
                        probeEraseEvents = 0;
                        probeErasePublishes = 0;
                        probeEraseMarksPeak = 0;
                        probeErasePublishMs = 0;
                        probeErasePublishMaxMs = 0;
                        AnnotationLayer.ResetEraserProbe();
                    }
                    drawingEraser = false;
                    currentErasure = null;
                    currentErasureMarks.Clear();
                    eraserHistoryCommitted = false;
                    eraserLastPublishMs = 0;
                    eraserPublishPending = false;
                    window.Annotation.EraserRectanglePreview = null;
                    try { e.Pointer.Capture(null); } catch { }
                }
                else if (activeAnnotationTool == AnnotationTool.Arrow && drawingArrow)
                {
                    FinishArrow(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
                else if (activeAnnotationTool == AnnotationTool.Rectangle && drawingRectangle)
                {
                    FinishRectangle(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
                else if (activeAnnotationTool == AnnotationTool.Mosaic && drawingMosaic)
                {
                    FinishMosaic(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
                else if (activeAnnotationTool == AnnotationTool.Spotlight && drawingSpotlight)
                {
                    FinishSpotlight(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
                else if (activeAnnotationTool == AnnotationTool.Counter && draggingCounterTail)
                {
                    CommitCounterTail(new PointF(screenPt.X, screenPt.Y), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    // 引线一挂上，图元的外形色盒没变但整块（含引线）范围变了 ⇒ 浮条按新几何重贴。
                    // 纯单击（没拖出位移）什么都不写、没有任何通知，故这里无条件补一次。
                    window.RefreshCounterPill();
                }

                // 松手＝这段手势唯一的提交点。顺序不能反：先把合帧闸门压着的最后一帧补落进预览，
                // 再让共享编辑器把最后那一帧一次性写回文档（六类图元同一条通路，一段手势一条撤销）。
                if (shapeEditor.IsEditing)
                {
                    FlushShapeGestureMove();
                    shapeEditor.Commit();
                    // 起手时被 BeginShapeEdit 收起的圆角浮条在这里贴回新位置。确有位移时 ReplaceShape 会经文档
                    // Changed 间接通知一次，但零位移的纯点选什么都不写、没有任何通知 ⇒ 这里无条件补一次。
                    window.RefreshSpotlightRadiusPill();
                    window.RefreshCounterPill();
                    try { e.Pointer.Capture(null); } catch { }
                }

                e.Handled = true;
                return;
            }

            bool endedTransform = mode == InteractionMode.MovingSelection || mode == InteractionMode.ResizingHandle || pendingExpandResize;
            switch (mode)
            {
                case InteractionMode.DraggingNew:
                    if (dragMovedPastThreshold && selection.HasSelection)
                    {
                        hasManualSelection = true;
                        if (!string.IsNullOrEmpty(ScriptAction))
                        {
                            ConfirmCurrentSelection();
                        }
                        else
                        {
                            UpdateSelectionVisualImmediate(selection.Region, true);
                            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.PointerReleased: 手动选区完成，Region=" + selection.Region);
                            TryApplyLastAnnotationTool();
                        }
                    }
                    else
                    {
                        var releasePt = window.WindowDipToScreenPoint(releaseDip);
                        if (pressedHighlight.HasValue && pressedHighlight.Value.Contains(releasePt))
                        {
                            hasManualSelection = true;
                            selection.SetRegion(pressedHighlight.Value);
                            StopAutoHoverAnimation();
                            if (!string.IsNullOrEmpty(ScriptAction))
                            {
                                ConfirmCurrentSelection();
                            }
                            else
                            {
                                UpdateSelectionVisualImmediate(selection.Region, true);
                                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.PointerReleased: 点击高亮元素完成选区，Region=" + selection.Region);
                                TryApplyLastAnnotationTool();
                            }
                            e.Handled = true;
                        }
                        else
                        {
                            StopAutoHoverAnimation();
                            currentHighlight = null;
                            pressedHighlight = null;
                            try { autoDetect.IsPaused = false; } catch { }
                        }
                    }
                    pressedHighlight = null;
                    break;

                case InteractionMode.MovingSelection:
                case InteractionMode.ResizingHandle:
                    // 拖动平移/缩放结束时再一次性提交状态，拖动中只更新视觉层。
                    if (dragMovedPastThreshold && liveTransformRect.Width > 0 && liveTransformRect.Height > 0)
                    {
                        selection.SetRegion(liveTransformRect);
                    }
                    break;
            }

            pendingExpandResize = false;
            pendingExpandResizeDirection = HandleDirection.None;
            mode = InteractionMode.Idle;
            resizeDirection = HandleDirection.None;
            isSpacePressed = false;
            try { e.Pointer.Capture(null); } catch { }
            if (endedTransform) EndSelectionTransform();

            // 松手后重算一次：若已确立选区则收起准星/瞄准光标并切换为选区参考线；若未确立选区则准星继续保留
            UpdateGuidelines();

            HideMagnifier(window);
            e.Handled = true;
        };

        // (f) 滚轮缩放选区：对齐旧版，每格四边各扩/缩 1px。
        window.PointerWheelChanged += (_, e) =>
        {
            // 覆盖层豁免：浮层（工具栏 / 浮层菜单 / 参数条）自己管滚轮，别在这里抢走。
            // ⚠ 用滚轮**专用**判据而不是 IsOverlayHit——参数条里的「尺寸」读数是只读的、自身没有滚轮处理，
            // 这里放行让它继续走到下面第 (f) 段的「滚轮缩放选区」，
            // 用户口径是"鼠标停在尺寸上滚轮＝缩放选区尺寸，跟窗口其余区域一样"。
            if (window.IsOverlayHitForWheel(e.Source)) return;

            // 自动检测的元素模式：没有手动选区时，滚轮用来切换元素级联吸附深度
            if (!hasManualSelection && autoDetect != null && autoDetect.Mode == DetectMode.Element)
            {
                int scrollDelta = e.Delta.Y > 0 ? 1 : -1;
                autoDetect.ChangeHighlightLevel(scrollDelta);
                e.Handled = true;
                return;
            }

            if (!hasManualSelection || !selection.HasSelection) return;
            if (mode != InteractionMode.Idle) return;
            if (longScrollStarting) return;

            if (activeAnnotationTool == AnnotationTool.Mosaic && GetSelectedBlurShape() is BlurShape selectedBlur)
            {
                int deltaLevel = e.Delta.Y > 0 ? 1 : -1;
                int nextLevel = BlurIntensityMapper.ClampUiLevel(selectedBlur.UiLevel + deltaLevel);
                if (nextLevel != selectedBlur.UiLevel)
                {
                    captureHistory.CommitSnapshot();
                    ReplaceSelectedBlurShape(selectedBlur.WithUiLevel(nextLevel));
                    window.SyncBlurToolState(selectedBlur.Mode, nextLevel);
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.BlurWheel: 调整图层强度，id=" + selectedBlur.Id + ", level=" + nextLevel + ", mode=" + selectedBlur.Mode);
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool == AnnotationTool.Spotlight && annotations.Shapes.OfType<SpotlightShape>().Any())
            {
                // Ctrl+滚轮＝改边框粗细（2026-09-18 用户要求）：原先 Ctrl 是暗幕的 ±2 细步档，现已让位给粗细。
                // 只写既有的描边滑杆——它的 ValueChanged → SpotlightSettingsChanged →
                // ApplySpotlightStyleToSelection 那条通路自会压一条撤销快照、并决定"选中则改这一颗、
                // 未选中则只作下一颗的记忆"，这里不另写第二份落地；夹取区间也归滑杆自己（不在此复制常量）。
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    var stroke = window.SliderSpotlightStrokeThickness;
                    double nextStroke = Math.Clamp(Math.Round(stroke.Value) + (e.Delta.Y > 0 ? 1 : -1),
                        stroke.Minimum, stroke.Maximum);
                    stroke.Value = nextStroke;   // 已在边界时赋同值不触发 ValueChanged＝什么都不改，也不白压快照
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.SpotlightWheel: Ctrl+滚轮调整边框粗细，stroke=" + nextStroke);
                    e.Handled = true;
                    return;
                }

                double step = 5.0;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 10.0;
                double nextPercent = window.SelectedSpotlightDarkness * 100.0 + (e.Delta.Y > 0 ? step : -step);
                captureHistory.CommitSnapshot();
                window.SetSpotlightDarknessPercent(nextPercent);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.SpotlightWheel: 调整暗幕透明度，percent=" + Math.Round(window.SelectedSpotlightDarkness * 100.0));
                e.Handled = true;
                return;
            }

            // 橡皮擦（仅画笔档）：滚轮改笔触大小，与贴图/录屏那一条分派表同一档（只写滑杆值，
            // 数字回显与光标轮廓由滑杆那条 ValueChanged→EraserSizeChanged 现成通路负责，区间归滑杆自身）。
            // 矩形档参数条上没有"大小"这一格，故不接管——它照旧落到下面"滚轮缩放选区"那一档。
            if (activeAnnotationTool == AnnotationTool.Eraser && window.SelectedEraserMode == EraserGestureMode.Brush)
            {
                double sizeStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 8 : 2;
                window.SliderEraserSize.Value += (e.Delta.Y > 0 ? sizeStep : -sizeStep);   // Slider 自己夹到 Min/Max
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.EraserWheel: 调整笔触大小，size=" + window.SelectedEraserDiameter);
                e.Handled = true;
                return;
            }

            // ===== 选中形状的滚轮调整（对齐旧版 OnShapeLayerMouseWheel，优先于工具参数）=====
            
            // 选中箭头：滚轮调整整体缩放（对齐旧版 ChangeArrowScale，factor=1.1）
            if (GetSelectedShape() is ArrowShape arrow && !drawingArrow)
            {
                float factor = e.Delta.Y > 0 ? 1.1f : (1.0f / 1.1f);
                float nextScale = Math.Clamp(arrow.Scale * factor, 0.2f, 4.0f);
                currentArrowScale = nextScale;
                captureHistory.CommitSnapshot();
                var updatedArrow = arrow.WithScale(nextScale);
                annotations.ReplaceShape(updatedArrow);
                window.Annotation.InvalidateVisual();
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.ArrowScaleWheel: 调整箭头缩放={nextScale:F2}, id={arrow.Id}");
                e.Handled = true;
                return;
            }
            
            // 选中序号：滚轮改大小，Ctrl+滚轮改编号（引线拖动进行中一律不接管）
            if (GetSelectedShape() is CounterShape counter && !draggingCounterTail)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
                    int nextNumber = Math.Max(1, counter.Number + (e.Delta.Y > 0 ? step : -step));
                    if (nextNumber == counter.Number)
                    {
                        e.Handled = true;
                        return;
                    }
                    captureHistory.CommitSnapshot();
                    annotations.ReplaceShape(counter.WithNumber(nextNumber));
                    window.Annotation.InvalidateVisual();
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.CounterNumberWheel: 调整序号编号={nextNumber}, id={counter.Id}");
                    e.Handled = true;
                    return;
                }

                float step2 = 2.0f;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step2 = 8.0f;
                float nextRadius = Math.Clamp(counter.Radius + (e.Delta.Y > 0 ? step2 : -step2), 8.0f, 80.0f);
                captureHistory.CommitSnapshot();
                // WithRadius 而不是 new CounterShape(...)：类型档、外形、引线、说明文字都得原样留下
                annotations.ReplaceShape(counter.WithRadius(nextRadius));
                window.Annotation.InvalidateVisual();
                window.SyncCounterRadiusSlider(nextRadius);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.CounterWheel: 调整序号大小={nextRadius}, id={counter.Id}");
                e.Handled = true;
                return;
            }
            
            // 选中矩形：滚轮调整描边粗细
            if (GetSelectedShape() is RectangleShape rect && !drawingRectangle)
            {
                int step = 1;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 5;
                float nextThickness = Math.Clamp(rect.StrokeThickness + (e.Delta.Y > 0 ? step : -step),
                    (float)window.SliderStrokeThickness.Minimum, (float)window.SliderStrokeThickness.Maximum);
                if (nextThickness == rect.StrokeThickness)
                {
                    e.Handled = true;
                    return;
                }
                captureHistory.CommitSnapshot();
                var updatedRect = rect.WithThickness(nextThickness);
                annotations.ReplaceShape(updatedRect);
                window.SyncShapeToolState(updatedRect);
                window.Annotation.InvalidateVisual();
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.RectangleWheel: 调整矩形粗细={nextThickness}, id={rect.Id}");
                e.Handled = true;
                return;
            }
            
            // 画笔/箭头/矩形工具：滚轮调整工具粗细（画笔选中时同样走这里——只影响下一笔）
            if ((activeAnnotationTool == AnnotationTool.Brush || activeAnnotationTool == AnnotationTool.Arrow || activeAnnotationTool == AnnotationTool.Rectangle) 
                && !drawingStroke && !drawingArrow && !drawingRectangle)
            {
                int step = 1;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step = 5;
                double nextValue = Math.Clamp(window.SliderStrokeThickness.Value + (e.Delta.Y > 0 ? step : -step), 
                    window.SliderStrokeThickness.Minimum, window.SliderStrokeThickness.Maximum);
                window.SliderStrokeThickness.Value = nextValue;
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.ThicknessWheel: 调整粗细={nextValue}, tool={activeAnnotationTool}");
                e.Handled = true;
                return;
            }
            
            // 文字工具：滚轮调整字体大小
            if (activeAnnotationTool == AnnotationTool.Text && !drawingStroke && !drawingArrow && !drawingRectangle)
            {
                double step = 2;
                if (window.CurrentTextSize >= 48) step = 8;
                else if (window.CurrentTextSize >= 24) step = 4;
                double nextSize = Math.Clamp(window.CurrentTextSize + (e.Delta.Y > 0 ? step : -step), 8.0, 200.0);
                if (Math.Abs(nextSize - window.CurrentTextSize) > 0.1)
                {
                    captureHistory.CommitSnapshot();
                    window.CurrentTextSize = nextSize;
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession.TextWheel: 调整文字字号={nextSize:0}, selectedText={(window.SelectedText != null)}, deltaY={e.Delta.Y}");
                }
                e.Handled = true;
                return;
            }

            if (activeAnnotationTool != AnnotationTool.None || drawingStroke || drawingArrow || drawingRectangle || drawingMosaic || drawingSpotlight) return;

            int delta = e.Delta.Y > 0 ? 1 : -1;
            Rectangle next = InflateSelection(selection.Region, delta);
            if (next == selection.Region) return;
            selection.SetRegion(next);
            UpdateSelectionVisual(selection.Region, true);
            e.Handled = true;
        };

        // (g) 键盘：内置快捷键按用户绑定表分发（设置页可重新录制，默认见 BuiltinShortcutsStore 目录）
        var captureShortcuts = BuiltinShortcutsStore.LoadGroupMap(BuiltinShortcutsStore.GroupCapture);

        window.KeyDown += (_, e) =>
        {
            if (e.Handled) return;
            if (mouseDragPending)
            {
                e.Handled = true;
                if (e.Key == Key.Escape) { MouseGesture?.Cancel(); CancelCapture(); }
                return;
            }
            // 任何非方向键按下都结束方向键步进会话：下一次方向键重新压一条撤销快照。
            // 放在分发之前，因为下面每条快捷通路都可能提前 return。
            if (e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Up && e.Key != Key.Down)
            {
                nudgedShapeId = null;
            }

            // 跟踪 Ctrl 键状态
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                _isCtrlPressed = true;
            }

            // Alt 按下：辅助线「开启」档按住才显示。按住态由 IsAltDown() 现读物理按键，
            // 这里只负责在按下的瞬间刷新一次（不拦截事件，Alt 仍是其他快捷键的修饰键）
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                UpdateGuidelines();
            }

            // Shift 按下：先记为"单独按下"，若随后与其他键组合则取消；
            // 色值格式切换挪到 KeyUp 且仅在单独按下时执行，避免 Shift+字母 工具键误触格式切换
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                shiftPressedAlone = true;
                // 作画手势进行中（含马赛克/聚光灯，此前只在鼠标移动的帧里清）这一下 Shift 是给锁方向用的，
                // 不能再到松开时去翻放大镜的色值格式。
                if (ShapeGestureInFlight()) shiftPressedAlone = false;
                if (activeAnnotationTool == AnnotationTool.Brush && drawingStroke)
                {
                    UpdateBrushShiftPreview(true);
                }
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                shiftPressedAlone = false;
            }

            // 录屏参数条激活期间：Enter/空格 = 开始录制，Esc = 收起参数条回普通标注态。
            // 必须排在下面「空格两段语义」与内置 confirm/cancel 分发**之前**——否则空格被"切换上次标注工具"
            // 吃掉、Enter 被 confirm（直接出图）吃掉。修饰键必须为空，Ctrl+Space 之类不误伤。
            if (e.KeyModifiers == KeyModifiers.None && !window.HasActiveTextEditing)
            {
                if ((e.Key == Key.Enter || e.Key == Key.Return || e.Key == Key.Space)
                    && window.TryStartRecordByKeyboard())
                {
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Escape && window.TryCancelRecordByKeyboard())
                {
                    e.Handled = true;
                    return;
                }
            }

            // 空格两段语义：选区已确认（标注阶段）→ 切换"上次使用标注工具"（已激活则取消，再按重新激活）；
            // 未有选区（框选阶段）→ 按住空格平移选区（原有行为）。
            // spaceToggleArmed 防按住不放时系统自动重复连发导致状态狂闪。
            if (e.Key == Key.Space && !window.HasActiveTextEditing && activeAnnotationTool != AnnotationTool.Text)
            {
                bool toggled = false;
                if (spaceToggleArmed && hasManualSelection && selection.HasSelection && mode == InteractionMode.Idle)
                {
                    AnnotationTool lastTool = ResolveLastAnnotationTool();
                    if (lastTool != AnnotationTool.None)
                    {
                        SetAnnotationTool(lastTool); // 已是该工具时内部会切换为 None，实现"取消激活"
                        spaceToggleArmed = false;
                        toggled = true;
                        e.Handled = true;
                    }
                }
                if (!toggled)
                {
                    isSpacePressed = true;
                }
            }

            // 文本编辑兜底：后续新建文本若焦点短暂落在窗口而不是 TextBox，Ctrl+Enter 仍应提交编辑，不能走全局 Enter 确认截图。
            if ((e.Key == Key.Enter || e.Key == Key.Return) && e.KeyModifiers.HasFlag(KeyModifiers.Control) && window.HasActiveTextEditing)
            {
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.TextCtrlEnter: 会话键盘兜底提交文本编辑");
                window.EndActiveTextEditing("CaptureSessionKeyDown");
                e.Handled = true;
                return;
            }

            // 单键和 Shift 字符输入优先交给正在编辑的输入框；Esc 仍按截图取消绑定分发。
            if (window.HasActiveTextEditing && e.Key != Key.Escape
                && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
                return;

            // 内置快捷键分发：按绑定表匹配（修饰键要求精确一致，组内唯一）
            BuiltinShortcutsStore.BuiltinShortcut? matched = null;
            foreach (var sc in captureShortcuts.Values)
            {
                if (BuiltinShortcutsStore.Matches(sc, e))
                {
                    matched = sc;
                    break;
                }
            }
            if (matched != null)
            {
                switch (matched.Id)
                {
                    case "capture.brush":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Brush);
                        return;
                    case "capture.arrow":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Arrow);
                        return;
                    case "capture.shape":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Rectangle);
                        return;
                    case "capture.text":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Text);
                        return;
                    case "capture.mosaic":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Mosaic);
                        return;
                    case "capture.spotlight":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Spotlight);
                        return;
                    case "capture.colorpicker":
                        e.Handled = true;
                        var samplePoint = lastPointerScreen;
                        if (samplePoint.IsEmpty && ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var cp))
                            samplePoint = new System.Drawing.Point(cp.X, cp.Y);
                        CopyPointerColor(samplePoint);
                        return;
                    case "capture.counter":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Counter);
                        return;
                    case "capture.eraser":
                        e.Handled = true;
                        SetAnnotationTool(AnnotationTool.Eraser);
                        return;
                    case "capture.undo":
                        e.Handled = true;
                        captureHistory.Undo();
                        return;
                    case "capture.redo":
                        e.Handled = true;
                        captureHistory.Redo();
                        return;
                    case "capture.translate":
                        e.Handled = true;
                        TriggerTranslateForSelection();
                        return;
                    case "capture.matting":
                        e.Handled = true;
                        TriggerMattingForSelection();
                        return;
                    // 六枚动作类（2026-09-29 补进目录）：一律转调 CaptureWindow 的 Request*，
                    // 与点按钮走同一条路（同一份可用性判定、同一个宿主事件），不在此另抄一遍逻辑。
                    // 返回 false＝那枚按钮此刻不可用（选区还没成形、工具栏收起），那就不吃掉这次按键。
                    case "capture.longscroll":
                        if (window.RequestLongScroll()) e.Handled = true;
                        return;
                    case "capture.ocr":
                        if (window.RequestOcr()) e.Handled = true;
                        return;
                    case "capture.record":
                        if (window.RequestRecord()) e.Handled = true;
                        return;
                    case "capture.sticker":
                        if (window.RequestSticker()) e.Handled = true;
                        return;
                    case "capture.aivision":
                        if (window.RequestAiVision()) e.Handled = true;
                        return;
                    case "capture.watermark":
                        if (window.RequestWatermark()) e.Handled = true;
                        return;
                    case "capture.saveas":
                        e.Handled = true;
                        SaveAsCurrentSelectionAsync();
                        return;
                    case "capture.quicksave":
                        e.Handled = true;
                        SaveCurrentSelection();
                        return;
                    case "capture.cancel":
                        e.Handled = true;
                        CancelCapture();
                        return;
                    case "capture.restore":
                        e.Handled = true;
                        TryRestorePreviousSelection();
                        return;
                    case "capture.delete":
                    {
                        // 删除当前选中的任何形状（六类共用一条通路）
                        if (DeleteSelectedShape())
                        {
                            e.Handled = true;
                            return;
                        }
                        if (window.SelectedText != null)
                        {
                            captureHistory.CommitSnapshot();
                            bool deleted = window.DeleteSelectedText();
                            if (deleted)
                            {
                                e.Handled = true;
                                return;
                            }
                        }
                        break;
                    }
                    case "capture.confirm":
                        e.Handled = true;
                        ConfirmCurrentSelection();
                        return;
                }
            }

            // 方向键步进已选图元（issue #13 新增能力）：每次 1 内容像素。必须排在下面的选区框微移**之前**
            // 且两者互斥——否则"方向键到底动的是图元还是选区框"就成了含混题。正在文本框里编辑时不接管，
            // 那四个键归光标。
            if (!window.HasActiveTextEditing
                && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
            {
                int stepDx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int stepDy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (StepSelectedShape(stepDx, stepDy))
                {
                    e.Handled = true;
                    return;
                }
            }

            // 方向键微移选区框：每次 1px（选区已确认且不在拖拽/缩放中时生效），
            // 视觉更新与滚轮扩缩选区走同一通路
            if (!window.HasActiveTextEditing && hasManualSelection && selection.HasSelection
                && mode == InteractionMode.Idle && !longScrollStarting)
            {
                int nudgeDx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int nudgeDy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (nudgeDx != 0 || nudgeDy != 0)
                {
                    Rectangle nudged = selection.Region;
                    nudged.Offset(nudgeDx, nudgeDy);
                    selection.SetRegion(nudged);
                    UpdateSelectionVisual(nudged, true);
                    e.Handled = true;
                    return;
                }
            }

            // 选中序号的键盘微调（设计稿第三条"浮条 + 快捷键同时可用"里的键盘那半）：+ / − 改**这一个**的编号。
            // 落法与上面 Ctrl+滚轮改编号那一路逐条一致（压快照 → WithNumber → ReplaceShape → 重绘）。
            // ⚠ 这里没有 A：A 键归下面的「WSAD 微移光标」，加说明文字的入口走选中后浮出的序号浮条
            //   （CounterPill 里那格点着直接输），不再抢一个已被占用的字母键。
            int counterStep = e.Key switch
            {
                Key.Add or Key.OemPlus => 1,
                Key.Subtract or Key.OemMinus => -1,
                _ => 0,
            };
            if (!window.HasActiveTextEditing && e.KeyModifiers == KeyModifiers.None
                && counterStep != 0 && GetSelectedShape() is CounterShape kbCounter)
            {
                StepCounterNumber(kbCounter, counterStep);
                e.Handled = true;
                return;
            }

            // WSAD 微移鼠标光标：每次 1px（除辅助线用的 Alt 外无其他修饰键、未被用户快捷键绑定占用时生效），
            // 光标移动会照常驱动元素自动检测悬停，便于精细定位后再点击/框选
            if (matched == null && (e.KeyModifiers & ~KeyModifiers.Alt) == KeyModifiers.None && !window.HasActiveTextEditing)
            {
                int curDx = 0, curDy = 0;
                switch (e.Key)
                {
                    case Key.W: curDy = -1; break;
                    case Key.S: curDy = 1; break;
                    case Key.A: curDx = -1; break;
                    case Key.D: curDx = 1; break;
                }
                if (curDx != 0 || curDy != 0)
                {
                    if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPt))
                    {
                        NativeMethods.SetCursorPos(cursorPt.X + curDx, cursorPt.Y + curDy);
                    }
                    e.Handled = true;
                    return;
                }
            }
        };

        window.KeyUp += (_, e) =>
        {
            // 跟踪Ctrl键状态
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                _isCtrlPressed = false;
            }

            // Alt 抬起：只刷新一次，按住态由 IsAltDown() 现读系统物理按键
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                UpdateGuidelines();
            }

            // Shift 单独按下并松开：切换放大镜色值格式（HEX ↔ RGB）
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                if (ShapeGestureInFlight()) shiftPressedAlone = false;
                if (activeAnnotationTool == AnnotationTool.Brush && drawingStroke)
                {
                    UpdateBrushShiftPreview(false);
                }

                if (shiftPressedAlone)
                {
                    _magnifierUseHexFormat = !_magnifierUseHexFormat;
                    if (window.MagnifierHost != null && window.MagnifierHost.IsVisible)
                    {
                        string colorText = _magnifierUseHexFormat ?
                            string.Format("#{0:X2}{1:X2}{2:X2}", _magnifierLastR, _magnifierLastG, _magnifierLastB) :
                            string.Format("{0}, {1}, {2}", _magnifierLastR, _magnifierLastG, _magnifierLastB);
                        window.MagnifierColorText.Text = colorText;
                        _magnifierLastColorStr = colorText;
                    }
                }
                shiftPressedAlone = false;
            }

            // 释放空格键：恢复调整选区尺寸，并重新武装"空格切换上次标注工具"
            if (e.Key == Key.Space)
            {
                isSpacePressed = false;
                spaceToggleArmed = true;
            }
        };

        // (g) 控制条按钮
        window.BrushRequested += (_, _) => SetAnnotationTool(AnnotationTool.Brush);
        window.EraserRequested += (_, _) => SetAnnotationTool(AnnotationTool.Eraser);
        window.ArrowRequested += (_, _) => SetAnnotationTool(AnnotationTool.Arrow);
        window.RectangleRequested += (_, _) => SetAnnotationTool(AnnotationTool.Rectangle);
        window.TextRequested += (_, _) => SetAnnotationTool(AnnotationTool.Text);
        window.MosaicRequested += (_, _) => SetAnnotationTool(AnnotationTool.Mosaic);
        window.ManualMosaicRequested += (_, _) =>
        {
            autoMosaicHighlights.Clear();
            window.Annotation.ClearAutoMosaicHighlights();
        };
        window.AutoMosaicRequested += (_, _) => RunAutoMosaicRecognition();
        window.BlurSettingsChanged += (_, _) =>
        {
            BlurShape? selected = GetSelectedBlurShape();
            if (selected == null) return;
            captureHistory.CommitSnapshot();
            ReplaceSelectedBlurShape(new BlurShape(selected.Rect, window.SelectedBlurMode, window.SelectedBlurUiLevel, selected.Id));
        };
        window.SpotlightRequested += (_, _) => SetAnnotationTool(AnnotationTool.Spotlight);
        window.SpotlightSettingsChanged += (_, _) => ApplySpotlightStyleToSelection();
        // ── 参数条各档 → 只把被点那一档落到当前选中的图元上（2026-09-20 全线补齐）──────────────
        // 一支事件对应一档，粒度不许合并：一次动作灌整把样式，就会把用户没碰的档静默换成工具栏记忆值。
        // 原先颜色与粗细共用一条 AnnotationCommonSettingsChanged，正是这条合并让"改个颜色"顺手改了粗细。
        window.AnnotationColorChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.Color);
        window.AnnotationThicknessChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.Thickness);
        window.ShapeKindChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.ShapeKind);
        window.ShapeLineStyleChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.ShapeLineStyle);
        window.ArrowStyleChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.ArrowStyle);
        window.CounterStyleChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.CounterStyle);
        window.CounterRadiusChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.CounterRadius);
        window.CounterTailHeadChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.CounterTailHead);
        // 2026-09-12「圆角滑块退役」时本通路随滑块一并移除（当时圆角只从画布四角手柄进来，经
        // SelectShape → SyncShapeToolState 记进字段）。2026-09-18 圆角浮条补上"用户改圆角"入口，
        // 通路随之复活：与上面 ApplySpotlightStyleToSelection 逐条对称，只是目标换成矩形图元。
        // ⚠ 同日稍后矩形的画布四角手柄也退役了，故浮条如今是矩形圆角的**唯一入口**，本通路即唯一落地路径。
        // 圆角不进 AnnotationStylePart 那张表也是同一原因：它的入口从来不在参数条上。
        window.ShapeRadiusChanged += (_, _) => ApplyShapeRadiusToSelection();
        // 填充档（勾选框 + 浓淡滑条）与圆角同一条语义：有选中形状就当场改它，没有就只当下一笔的记忆。
        window.ShapeFillChanged += (_, _) => ApplyStyleToSelection(AnnotationStylePart.ShapeFill);
        window.CounterSerialChanged += (_, kind) => ApplyCounterSerialKindToAll(kind);
        // 序号浮条（2026-09-18）：与圆角浮条同一条分工——控件只报意图，压快照/写图元/回显全在 CaptureSession。
        // 三条都只作用于**当前选中的那一个**序号（浮条本身就是选中才出现的），故不需要按 id 找图元。
        window.CounterNumberStepRequested += (_, delta) =>
        {
            if (GetSelectedShape() is CounterShape counter) StepCounterNumber(counter, delta);
        };
        window.CounterNoteRequested += (_, note) => SetSelectedCounterNote(note);
        // 胶囊圆角（2026-09-19）：浮条上「圆角」那一格的去处，同样只作用于当前选中的那一个。
        // "下一颗胶囊沿用多少"由宿主（CaptureWindow）当场回灌进 CounterOptions 的记忆位，不经过这里。
        window.CounterCapsuleCornerRequested += (_, corner) => SetSelectedCounterCapsuleCorner(corner);
        // 引线开关（2026-09-19）：藏/显整条引线，同样只作用于当前选中的那一个，图元里的引线端点不动。
        window.CounterTailToggleRequested += (_, _) => ToggleSelectedCounterTail();
        // × 与 Delete 键删的是同一个目标，走同一通路：内部已带"按设置项 CounterRenumberOnDelete 决定要不要重编号"。
        window.CounterDeleteRequested += (_, _) => DeleteSelectedShape();
        window.CounterRequested += (_, _) => SetAnnotationTool(AnnotationTool.Counter);
        // 水印（2026-09-20）：与圆角/描边/阴影同族的<b>效果式</b>参数——不落笔、不进撤销栈，所以这里没有
        // "把这一档落到当前选中的那颗"那一套，只有收笔与刷预览两件事。
        // 顺序不能反：SetAnnotationTool 末尾会自己写一次 window.SetActiveTool，水印那条必须写在它后面。
        window.WatermarkRequested += (_, _) =>
        {
            SetAnnotationTool(AnnotationTool.None);
            window.SetActiveTool(window.WatermarkEnabled ? "Watermark" : null);
            window.ApplyWatermarkPreview();
        };
        // 五档（内容/位置/不透明度/大小/间距）改动当场回灌设置：三路成品出口读的就是这五项；
        // 落盘沿用会话收尾处既有的 _settings.Save()，与其余标注参数的记忆口径一致。
        window.WatermarkChanged += (_, _) =>
        {
            window.WatermarkOptions.CaptureDefaults(_settings);
            window.ApplyWatermarkPreview();
        };
        window.UndoRequested += (_, _) => captureHistory.Undo();
        window.RedoRequested += (_, _) => captureHistory.Redo();
        window.LongScrollRequested += (_, args) => StartLongScrollFromCurrentSelection(args.Axis);
        window.RecordRequested += (_, _) => ToggleRecordTool();
        _startRecordFromSelection = StartRecordFromCurrentSelection;
        window.RecordStartRequested += (_, _) => StartRecordFromCurrentSelection();
        window.OcrRequested += (_, _) => TriggerOcrForSelection();
        window.TranslateRequested += (_, _) => TriggerTranslateForSelection();
        window.MattingRequested += (_, _) => TriggerMattingForSelection();
        window.AiVisionRequested += (_, _) => TriggerAiVisionForSelection();
        window.StickerRequested += (_, _) => CreateStickerFromCurrentSelection();
        window.ConfirmRequested += (_, _) => ConfirmCurrentSelection();
        window.QuickSaveRequested += (_, _) => SaveCurrentSelection();
        window.SaveAsRequested += (_, _) => SaveAsCurrentSelectionAsync();
        window.SaveRequested += (_, _) => SaveCurrentSelection();
        window.CancelRequested += (_, _) => CancelCapture();
        window.ClearCurrentToolRequested += (_, _) => ClearCurrentToolAnnotations();
        window.ClearAllAnnotationsRequested += (_, _) => ClearAllAnnotations();

        // (g2) 参数条：圆角滑杆 + 描边色块与滑杆 + 阴影勾选。改一下即刻写进 _settings（成品出口读的就是这四项），
        // 落盘沿用会话收尾处既有的 _settings.Save()，与标注工具参数的记忆口径完全一致。
        long bindingsCompleted = Stopwatch.GetTimestamp();
        window.SyncCaptureEffectState(CaptureCornerRadiusParam, _settings.CaptureShadow, CaptureStrokePx, CaptureStrokeArgb,
            SuppressCaptureStroke);
        window.CaptureEffectChanged += (_, _) =>
        {
            _settings.CaptureCornerRadius = window.CaptureRadiusParam;
            window.CaptureStrokeDefaults(_settings);
            _settings.CaptureShadow = window.CaptureShadowChecked;
            ApplyCaptureEffectPreview();
        };
        // 悬浮闸门只改预览、不落盘，故走独立事件；两条都汇到同一个下发函数，口径不会分叉。
        window.CaptureEffectHoverChanged += (_, _) => ApplyCaptureEffectPreview();
        ApplyCaptureEffectPreview();
        window.Layer.SelectionBorderStyle = _settings.SelectionBorderStyle;
        window.Layer.SelectionBorderColorArgb = _settings.SelectionBorderColorArgb;
        window.Layer.SelectionBorderThickness = _settings.SelectionBorderThickness;
        long effectsCompleted = Stopwatch.GetTimestamp();

        // 首帧确定性就位 (P1-3)：
        // 1. 在 window.Show() 之前提前应用准星与瞄准光标，使首帧呈现即对齐物理光标
        UpdateGuidelines();
        // 2. 在 window.Opened 打开后再次对齐，确保建窗等待期间若光标发生移动能立刻纠正
        window.Opened += (_, _) => UpdateGuidelines();
        long guidelinesCompleted = Stopwatch.GetTimestamp();

        if (MouseGesture is { } gesture)
        {
            window.GlobalMouseDragActive = true;
            System.Drawing.Point? lastMouseGesturePosition = null;
            bool mouseGestureFrameScheduled = false;
            long lastReadUpdateCount = 0;
            int appliedUpdates = 0, frameCallbacks = 0;
            long updateTotalTicks = 0, updateMaxTicks = 0, latestInputAgeTotalTicks = 0, latestInputAgeMaxTicks = 0;
            long lastFrameTimestamp = 0, frameGapTotalTicks = 0, frameGapMaxTicks = 0, changedFrameGapMaxTicks = 0;
            int longFrameGaps = 0;
            bool firstInputRecorded = false;
            void RefreshMouseGesture()
            {
                if (!captureWindowAlive || !mouseDragPending || !window.IsVisible) return;
                var state = gesture.ReadTimed();
                lastReadUpdateCount = state.UpdateCount;
                if (lastMouseGesturePosition == state.Position && !state.Released && !state.Cancelled) return;
                // 正常运行只消费输入；详细取证开启后才计时、累计并构造诊断内容。
                if (!AppLogger.DetailedLoggingEnabled)
                {
                    ApplyMouseGesture(state.Position, state.Released, state.Cancelled, state.Moved);
                    return;
                }
                long start = Stopwatch.GetTimestamp();
                long inputAge = Math.Max(0, start - state.UpdatedTimestamp);
                string? consumeRecord = null;
                if (!firstInputRecorded || state.Released || state.Cancelled)
                {
                    consumeRecord = $"PERF GlobalMouse.Consume first={!firstInputRecorded} released={state.Released} cancelled={state.Cancelled} "
                        + $"sincePress={(start - gesture.StartedTimestamp) * 1000.0 / Stopwatch.Frequency:F2}ms "
                        + $"latestInputAge={inputAge * 1000.0 / Stopwatch.Frequency:F2}ms hookUpdates={state.UpdateCount}";
                    firstInputRecorded = true;
                }
                long applyStarted = Stopwatch.GetTimestamp();
                try { ApplyMouseGesture(state.Position, state.Released, state.Cancelled, state.Moved); }
                finally
                {
                    long completed = Stopwatch.GetTimestamp();
                    long took = completed - applyStarted;
                    if (consumeRecord != null) { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug(consumeRecord); }
                    if (state.Released || state.Cancelled)
                        { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF GlobalMouse.TerminalApply cost={took * 1000.0 / Stopwatch.Frequency:F3}ms "
                            + $"sinceLatestInput={(completed - state.UpdatedTimestamp) * 1000.0 / Stopwatch.Frequency:F2}ms "
                            + $"cancelled={state.Cancelled} (含确认／取消同步操作，取消时间未单测)"); }
                    else
                    {
                        appliedUpdates++;
                        updateTotalTicks += took;
                        updateMaxTicks = Math.Max(updateMaxTicks, took);
                        latestInputAgeTotalTicks += inputAge;
                        latestInputAgeMaxTicks = Math.Max(latestInputAgeMaxTicks, inputAge);
                    }
                }
            }
            void ApplyMouseGesture(System.Drawing.Point position, bool released, bool cancelled, bool moved)
            {
                if (cancelled)
                {
                    mouseDragPending = false;
                    window.GlobalMouseDragActive = false;
                    CancelCapture();
                    return;
                }
                lastMouseGesturePosition = position;
                window.SetGlobalMousePointerPosition(position);
                // 起手状态只建立一次；稳定拖框无需反复清空检测器、收起浮层和刷新提示状态。
                if (!suppressOverlayDuringTransform)
                {
                    StopAutoHoverAnimation();
                    autoDetect.IsPaused = true;
                    currentHighlight = pressedHighlight = null;
                    mode = InteractionMode.DraggingNew;
                    BeginSelectionTransform();
                }
                dragMovedPastThreshold = moved;
                guidelinePointerScreen = position;
                Rectangle region = Rectangle.FromLTRB(
                    Math.Min(gesture.Start.X, position.X), Math.Min(gesture.Start.Y, position.Y),
                    Math.Max(gesture.Start.X, position.X), Math.Max(gesture.Start.Y, position.Y));
                if (frame != null) region = Rectangle.Intersect(region, frame.VirtualScreenRect);
                selection.SetRegion(region);
                if (selection.HasSelection)
                {
                    // 已在显示帧中，直接走原有轻量变换显示；不再排队随后立即作废的 Render 更新。
                    UpdateTransformVisual(selection.Region);
                    UpdateGuidelines();
                }
                else UpdateGuidelines();
                // 区域不变（例如移动到屏幕边界外）时也要刷新指针避让；仍只在消费新输入时做。
                window.EvaluateShortcutHintVisibility();
                if (!released) return;
                mouseDragPending = false;
                window.GlobalMouseDragActive = false;
                mode = InteractionMode.Idle;
                if (!moved || !selection.HasSelection)
                {
                    CancelCapture();
                    return;
                }
                hasManualSelection = true;
                if (!string.IsNullOrEmpty(ScriptAction)) ConfirmCurrentSelection();
                else
                {
                    EndSelectionTransform();
                    TryApplyLastAnnotationTool();
                }
                UpdateGuidelines();
            }
            // 跟显示刷新节拍读取最新坐标；固定 16ms 的低优先级定时器会限速高刷屏，忙时还会积压。
            void ScheduleMouseGestureFrame()
            {
                if (mouseGestureFrameScheduled || !captureWindowAlive || !mouseDragPending || !window.IsVisible) return;
                mouseGestureFrameScheduled = true;
                window.RequestAnimationFrame(_ =>
                {
                    mouseGestureFrameScheduled = false;
                    if (!captureWindowAlive || !mouseDragPending || !window.IsVisible) return;
                    if (!AppLogger.DetailedLoggingEnabled)
                    {
                        RefreshMouseGesture();
                        ScheduleMouseGestureFrame();
                        return;
                    }
                    long now = Stopwatch.GetTimestamp();
                    long gap = lastFrameTimestamp == 0 ? 0 : now - lastFrameTimestamp;
                    lastFrameTimestamp = now;
                    frameCallbacks++;
                    frameGapTotalTicks += gap;
                    frameGapMaxTicks = Math.Max(frameGapMaxTicks, gap);
                    if (gap * 1000.0 / Stopwatch.Frequency > 50) longFrameGaps++;
                    long priorUpdates = lastReadUpdateCount;
                    RefreshMouseGesture();
                    if (lastReadUpdateCount != priorUpdates)
                        changedFrameGapMaxTicks = Math.Max(changedFrameGapMaxTicks, gap);
                    ScheduleMouseGestureFrame();
                });
            }
            // Show 后立即读一次，包含准备期间已松手的终态；随后每个显示帧最多更新一次。
            _refreshMouseGesture = () => { RefreshMouseGesture(); ScheduleMouseGestureFrame(); };
            _detachMouseGesture = () =>
            {
                mouseDragPending = false;
                window.GlobalMouseDragActive = false;
            };
            window.Closed += (_, _) => _detachMouseGesture?.Invoke();
            window.Closed += (_, _) =>
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF GlobalMouse.Summary hookUpdates={gesture.ReadTimed().UpdateCount} applied={appliedUpdates} "
                    + $"updateAvg={updateTotalTicks * ms / Math.Max(1, appliedUpdates):F3}ms updateMax={updateMaxTicks * ms:F3}ms "
                    + $"latestInputAgeAvg={latestInputAgeTotalTicks * ms / Math.Max(1, appliedUpdates):F3}ms "
                    + $"latestInputAgeMax={latestInputAgeMaxTicks * ms:F3}ms frameCallbacks={frameCallbacks} "
                    + $"frameGapAvg={frameGapTotalTicks * ms / Math.Max(1, frameCallbacks - 1):F3}ms "
                    + $"frameGapMax={frameGapMaxTicks * ms:F3}ms changedFrameGapMax={changedFrameGapMaxTicks * ms:F3}ms gapsOver50ms={longFrameGaps} "
                    + "(帧回调间隔，非GPU上屏；只保留最新坐标，合帧数不是丢输入)");
            };
        }

        // 路径输入先于矩形交互处理，只消费画布事件；工具栏与标注输入继续走原链。
        bool pathPointerCaptured = false;
        IPointer? pathPointer = null;
        void ReleasePathPointer()
        {
            pathPointerCaptured = false;
            IPointer? pointer = pathPointer; pathPointer = null; pointer?.Capture(null);
        }
        void RefreshPathPreview() => window.Layer.SetPathGeometry(pathEditor.Geometry(), pathEditor.Geometry(true, window.Bounds.Size));
        pathEditor.PreviewChanged += RefreshPathPreview;
        // Button 可能消费后续指针事件，进入浮栏时先撤掉上一帧放大镜。
        window.ModeToolbar.PointerEntered += (_, _) => HideMagnifier(window);
        window.ModeToolbar.CancelRequested += () => { HideCaptureWindowForCompletion(window); CancelCapture(); };
        window.ModeToolbar.FullScreenRequested += () =>
        {
            if (!window.IsVisible || tcs.Task.IsCompleted) return;
            Rectangle screen = window.SelectionModeScreenBounds;
            if (frame != null) screen.Intersect(frame.VirtualScreenRect);
            if (screen.Width <= 0 || screen.Height <= 0) return;
            ReleasePathPointer();
            // 全屏是矩形输出，不携带未完成路径；其余分流沿用原确认链。
            window.ModeToolbar.SelectMode(CaptureSelectionMode.Rectangle);
            StopAutoHoverAnimation(); autoDetect.IsPaused = true;
            currentHighlight = pressedHighlight = null; mode = InteractionMode.Idle;
            selection.SetRegion(screen); hasManualSelection = true;
            UpdateSelectionVisualImmediate(screen, true);
            ConfirmCurrentSelection();
        };
        pathEditor.Changed += () =>
        {
            _selectionPath = pathEditor.Path;
            if (pathEditor.Mode == CaptureSelectionMode.Rectangle)
            {
                window.Layer.SetPathGeometry(null, null); return;
            }
            StopAutoHoverAnimation(); currentHighlight = pressedHighlight = null;
            autoDetect.IsPaused = true; hasManualSelection = pathEditor.IsClosed;
            window.Layer.AutoHoverRegion = default;
            if (_selectionPath is { } path)
            {
                selection.SetRegion(path.Bounds);
                UpdateSelectionVisualImmediate(path.Bounds, true);
            }
            else
            {
                selection.Clear(); UpdateSelectionVisualImmediate(null, false);
            }
            RefreshPathPreview();
            window.HideRectangularEffectPill();
            window.RefreshSelectionModeHints();
        };
        window.ModeToolbar.ModeChanged += selectedMode =>
        {
            ReleasePathPointer();
            window.SetActiveTool(null); selection.Clear(); hasManualSelection = false;
            currentHighlight = pressedHighlight = null; mode = InteractionMode.Idle;
            UpdateSelectionVisualImmediate(null, false); pathEditor.SetMode(selectedMode);
            autoDetect.IsPaused = selectedMode != CaptureSelectionMode.Rectangle;
            window.Cursor = null;
            window.RefreshSelectionModeHints();
            pathEditor.Focus();
        };
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (mouseDragPending || window.IsOverlayHit(e.Source) || window.IsOverlayPoint(e.GetPosition(window))) return;
            bool modeBarWasVisible = window.ModeToolbar.IsVisible;
            window.ModeToolbar.IsVisible = false;
            if (modeBarWasVisible) pathEditor.Focus();
            if (e.Handled) return;
            var point = e.GetCurrentPoint(window);
            if (pathEditor.Mode == CaptureSelectionMode.Rectangle) return;
            if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
            if (pathEditor.IsClosed && window.HasAnnotationTool) return;
            // 现有标注的编辑优先，节点和控制柄编辑在无标注工具时使用。
            if (pathEditor.IsClosed && annotations.Shapes.Any(s => s.Bounds.Contains(window.WindowDipToScreenPoint(e.GetPosition(window))))) return;
            if (pathEditor.IsClosed && e.ClickCount == 2) { ConfirmCurrentSelection(); e.Handled = true; return; }
            if (pathEditor.Press(e.GetPosition(window)))
            {
                pathPointerCaptured = true; pathPointer = e.Pointer; e.Pointer.Capture(window); pathEditor.Focus();
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (mouseDragPending || e.Handled) return;
            if (pathEditor.Mode == CaptureSelectionMode.Rectangle) return;
            if (!pathPointerCaptured && (window.HasAnnotationTool || window.IsOverlayHit(e.Source) || window.IsOverlayPoint(e.GetPosition(window))))
            {
                HideMagnifier(window); return;
            }
            lastPointerScreen = window.WindowDipToScreenPoint(e.GetPosition(window));
            pathEditor.Move(e.GetPosition(window));
            if (!pathEditor.IsClosed && frame != null) UpdateMagnifier(window, e.GetPosition(window), frame);
            else HideMagnifier(window);
            if (pathPointerCaptured || !window.HasAnnotationTool) { window.Cursor = pathCursor; e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (mouseDragPending || pathEditor.Mode == CaptureSelectionMode.Rectangle) return;
            var kind = e.GetCurrentPoint(window).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.RightButtonReleased && pathEditor.IsDrawing)
            {
                pathEditor.Undo(); e.Handled = true; return;
            }
            if (!pathPointerCaptured || kind != PointerUpdateKind.LeftButtonReleased) return;
            pathEditor.Move(e.GetPosition(window)); pathEditor.Release();
            ReleasePathPointer();
            if (pathEditor.IsClosed) HideMagnifier(window);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        window.PointerCaptureLost += (_, _) =>
        {
            if (pathPointerCaptured) { pathPointerCaptured = false; pathPointer = null; pathEditor.Release(commit: false); }
        };
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (pathEditor.Mode != CaptureSelectionMode.Rectangle && !window.HasAnnotationTool && !window.IsOverlayHit(e.Source)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (mouseDragPending || window.HasActiveTextEditing) return;
            if (e.Key is not (Key.LeftShift or Key.RightShift) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) shiftPressedAlone = false;
            bool colorShortcut = captureShortcuts.Values.Any(sc => sc.Id == "capture.colorpicker" && BuiltinShortcutsStore.Matches(sc, e));
            if (pathEditor.Mode == CaptureSelectionMode.Rectangle) return;
            // 路径模式同样一次取消整次截图；不能把 Esc 当成清空轮廓再返回。
            if (e.Key == Key.Escape || captureShortcuts.Values.Any(sc => sc.Id == "capture.cancel" && BuiltinShortcutsStore.Matches(sc, e)))
            {
                ReleasePathPointer(); pathEditor.EndDrag(); HideMagnifier(window);
                HideCaptureWindowForCompletion(window); CancelCapture(); e.Handled = true; return;
            }
            // 让原快捷键分发与 Shift 格式切换在所有模式中共用同一条链。
            if (colorShortcut || e.Key is Key.LeftShift or Key.RightShift) return;
            if (window.ModeToolbar.IsVisible && (e.Key == Key.Tab || window.IsOverlayHit(e.Source))) return;
            if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.None)
            {
                window.ModeToolbar.SelectMode(CaptureSelectionMode.Rectangle); return;
            }
            if (window.HasAnnotationTool) return;
            if ((e.Key == Key.Back || e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control)) && pathEditor.Nodes.Count > 0)
            {
                pathEditor.Undo(); e.Handled = true; return;
            }
            if ((e.Key == Key.Enter || e.Key == Key.Return) && !pathEditor.IsClosed)
            {
                if (pathEditor.ClosePath()) HideMagnifier(window); e.Handled = true; return;
            }
            if (!pathEditor.IsClosed)
            {
                if (e.Key != Key.Escape) e.Handled = true;
                return;
            }
            int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                pathEditor.Translate(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0,
                    e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0); e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // (h) 兜底：窗口被外部关掉
        window.Closed += (_, _) =>
        {
            ReleasePathPointer(); pathCursor.Dispose();
            tcs.TrySetResult(null);
            if (crosshairFlushCount > 0)
            {
                double avgMs = (double)crosshairFlushTotalTicks / Stopwatch.Frequency * 1000.0 / crosshairFlushCount;
                double maxMs = (double)crosshairFlushMaxTicks / Stopwatch.Frequency * 1000.0;
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF 十字准星: flushes={crosshairFlushCount} avg={avgMs:F2}ms max={maxMs:F2}ms maxGap={crosshairMaxGapMs}ms");
            }
        };
        long wireUpCompleted = Stopwatch.GetTimestamp();
        double wireUpMs = 1000.0 / Stopwatch.Frequency;
        if (AppLogger.DetailedLoggingEnabled) window.RecordWireUpDiagnostics($"PERF CaptureSession.WireUp.Phases "
            + $"setup={(bindingsCompleted - wireUpStarted) * wireUpMs:F3}ms "
            + $"effects={(effectsCompleted - bindingsCompleted) * wireUpMs:F3}ms "
            + $"guidelines={(guidelinesCompleted - effectsCompleted) * wireUpMs:F3}ms "
            + $"finish={(wireUpCompleted - guidelinesCompleted) * wireUpMs:F3}ms "
            + $"total={(wireUpCompleted - wireUpStarted) * wireUpMs:F3}ms");
    }

    // -------------------- 输出 --------------------

    /// <summary>记忆中的选区圆角参数档位（0-50 档）。</summary>
    private int CaptureCornerRadiusParam => SelectionEffects.NormalizeParam(_settings.CaptureCornerRadius);

    /// <summary>记忆中的成品圆角半径（物理像素 0-200px，参数 × 4）。阴影直接读 <c>_settings.CaptureShadow</c>。</summary>
    private int CaptureCornerRadiusPx => _selectionPath == null ? SelectionEffects.ParamToRadiusPx(CaptureCornerRadiusParam) : 0;

    /// <summary>记忆中的成品描边宽度（物理像素 0-20，档位与像素 1:1）。钳位只在 SelectionEffects 做一次。</summary>
    private int CaptureStrokePx => !SuppressCaptureStroke && _selectionPath == null
        ? SelectionEffects.ParamToStrokePx(_settings.CaptureStrokeWidth) : 0;

    private bool UseCaptureShadow => _selectionPath == null && _settings.CaptureShadow;

    /// <summary>记忆中的成品描边颜色（ARGB）。预览与三个成品出口读的都是这一份。</summary>
    private int CaptureStrokeArgb => _settings.CaptureStrokeColor;

    /// <summary>
    /// 确认动作一经落定，遮罩窗口必须<b>立刻</b>从屏幕上消失（「回车确认有延迟」的根因就在这里）：
    /// 裁剪 + 烘阴影 + 写剪贴板 + 录入历史 + 落盘干净底图这一串全在 UI 线程同步跑，几百毫秒起步，
    /// 窗口若等到会话 finally 才 Hide，用户看到的就是"按了回车没反应、过一拍才消失"。
    /// Hide 只关 HWND、不动控件状态，之后照常读 <c>window.TextLayers</c> / 图元（都是内存数据）；
    /// 唯一不能提前藏的是「另存为」——系统文件对话框要拿它当 owner。
    /// </summary>
    private static void HideCaptureWindowForCompletion(CaptureWindow? window)
    {
        try
        {
            if (window != null && window.IsVisible)
            {
                using (PerformanceTimer.Measure("CaptureSession.HideMask")) window.Hide();
                window.LastMaskHiddenUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureSession.HideCaptureWindowForCompletion 隐藏遮罩失败：" + ex.Message);
        }
    }

    /// <summary>
    /// <see cref="HideCaptureWindowForCompletion"/> 的逆操作：只有在"提前隐藏之后、结果又没取到"时用。
    /// 那种情况下会话并没有真的结束（<c>tcs</c> 未落定），遮罩要还回去，否则用户面对一个看不见的全屏窗口，
    /// 既没法重新框选也按不到 Esc，只能杀进程。
    /// </summary>
    private static void RestoreCaptureWindowForCompletion(CaptureWindow? window)
    {
        try
        {
            if (window != null && !window.IsVisible)
                using (PerformanceTimer.Measure("CaptureSession.RestoreMask")) window.Show();
            if (window != null) window.LastMaskHiddenUtc = null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureSession.RestoreCaptureWindowForCompletion 恢复遮罩失败：" + ex.Message);
        }
    }

    private async Task<bool> CopyResultToClipboardAsync(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations, IReadOnlyList<TextLayerInfo>? textLayers)
    {
        Bitmap? cropped = null;
        try
        {
            int radiusPx = CaptureCornerRadiusPx;
            bool shadow = UseCaptureShadow;
            cropped = CreateCroppedResultBitmap(frame, screenRect, annotations, textLayers, "复制剪贴板",
                radiusPx, shadow, CaptureStrokePx, CaptureStrokeArgb, _activeWindow?.CurrentWatermark);
            if (cropped == null) return false;

            // 只有圆角/阴影真的生效时成品才有透明像素，才需要附加保留 alpha 的格式；
            // 未加效果时走老的单 CF_DIB 路径，与旧实现逐字节一致。
            if (!await ClipboardService.SetImageAsync(cropped, preserveAlpha: radiusPx > 0 || shadow)) return false;
            // 记住截图区域，供 F3 快速贴图还原位置（剪贴板裸图不带位置信息）。
            // 成品左上角对应的屏幕点在选区左上角的左上方一个边距处（描边外扩 + 阴影扩散各自贡献），
            // 这里回退回去，F3 还原出来的内容才正好落在原来的选区上。
            int pad = SelectionEffects.MarginOf(shadow, CaptureStrokePx);
            // 卡片几何一并记住：剪贴板裸图不带这些参数，F3 贴图时窗体阴影要靠它才贴着圆角+描边的外沿。
            var card = SelectionEffects.CardGeometryOf(
                radiusPx, CaptureStrokePx, cropped.Width - pad * 2, cropped.Height - pad * 2);
            LastCaptureMemory.Record(new Rectangle(screenRect.X - pad, screenRect.Y - pad, screenRect.Width, screenRect.Height),
                cropped.Size, frame?.DpiScale ?? 1.0, card);
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession.CopyResultToClipboard: 已复制截图到剪贴板");
            UsageStatistics.RecordScreenshot(); // 统计：截图成功输出一次
            // 历史：内部会同步克隆位图后交给后台写盘，故 finally 照常 Dispose 不受影响
            string? historyId = CaptureHistoryStore.TryRecord(cropped, CaptureHistorySource.Clipboard, null, _settings);
            if (historyId != null)
            {
                try
                {
                    using var clean = CreateCroppedResultBitmap(frame, screenRect,
                        new AnnotationDocument(), null, "剪贴板干净底图",
                        radiusPx, shadow: shadow, strokePx: CaptureStrokePx, strokeArgb: CaptureStrokeArgb,
                        _activeWindow?.CurrentWatermark);   // "干净"＝不含标注；水印属效果那一族，与圆角/描边同进同出
                    if (clean != null)
                    {
                        CaptureHistoryStore.SaveCleanImage(historyId, clean);
                        int annotationOffset = pad;
                        var editableShapes = TranslateAnnotations(annotations, screenRect, annotationOffset, annotationOffset);
                        var editableTexts = TranslateTextSnapshots(textLayers, screenRect, frame?.DpiScale ?? 1.0, frame?.VirtualScreenRect ?? Rectangle.Empty, annotationOffset, annotationOffset);
                        int marginPx = (int)Math.Round(16.0 * (frame?.DpiScale ?? 1.0));
                        var initialArchive = new StickerArchive
                        {
                            HistoryId = historyId,
                            Kind = "image",
                            ClosedAt = DateTime.Now,
                            Shapes = editableShapes.Select(s => s.Clone()).ToList(),
                            Texts = editableTexts.ToList(),
                            Card = new StickerArchiveCard
                            {
                                ContentRadiusPx = card.ContentRadiusPx,
                                StrokeWidthPx = card.StrokeWidthPx,
                                BakedShadow = shadow,
                            },
                            State = new StickerArchiveState
                            {
                                X = screenRect.X - pad - marginPx,
                                Y = screenRect.Y - pad - marginPx,
                                Width = cropped.Width + marginPx * 2,
                                Height = cropped.Height + marginPx * 2,
                                Scale = 1.0,
                                Shadow = true,
                            }
                        };
                        StickerArchiveStore.TryWrite(initialArchive);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureSession.CopyResultToClipboard 备份干净底图与标注失败: " + ex.Message);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureSession.CopyResultToClipboard 异常", ex);
            throw;
        }
        finally
        {
            try { cropped?.Dispose(); } catch { }
        }
    }

    private async Task<string?> PickSaveAsPathAsync(Window window)
    {
        IntPtr hwnd = window is CaptureWindow cw ? cw.GetWindowHwnd() : IntPtr.Zero;
        // ⚠ 这两个状态必须声明在 try 之外：finally 恢复时要用（C# 中 try 内声明的变量在 finally 不可见）
        bool hadGuard = false;
        bool ownerWasTopmost = false;
        try
        {
            // 弹出模态系统文件选择对话框前，临时卸载全屏遮罩的持久置顶守卫并降置顶，
            // 避免 Windows 系统文件对话框被全屏 Topmost 遮罩压在后面或抢夺焦点。
            // ⚠ 降置顶必须走 Avalonia 的 Topmost **属性**，不能裸 SetWindowPos(HWND_NOTOPMOST)：
            //    裸 p/invoke 会被框架按 XAML Topmost="True" 的账本"自愈"回置顶，模态对话框随即
            //    被压在遮罩下面、owner 又被模态禁用，表现为"卡死"（2026-09-07 取色器卡死的
            //    实锤根因，此处为同一模式，一并修复）。
            hadGuard = hwnd != IntPtr.Zero && WindowHelper.IsEnforceTopmostActive(hwnd);
            if (hadGuard)
            {
                try { WindowHelper.StopEnforceTopmost(hwnd); } catch { }
            }
            ownerWasTopmost = window.Topmost;
            if (ownerWasTopmost) window.Topmost = false;

            IReadOnlyList<FilePickerFileType> filters = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("Bitmap Image") { Patterns = new[] { "*.bmp" } },
            };

            string defaultFileName = ResolveFileName(_settings);
            IStorageFile? file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "截图另存为",
                SuggestedFileName = defaultFileName,
                FileTypeChoices = filters,
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
            });

            return file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureSession: 另存为文件对话框不可用：" + ex.Message);
            return null;
        }
        finally
        {
            // 对话框关闭后（无论保存还是取消），若截图窗口仍处于活动状态，恢复置顶与持久置顶守卫
            // （守卫只在先前确实存在时重装，避免给未挂守卫的窗口凭空装上）
            if (!window.IsVisible)
            {
                // 窗口已经关闭或隐藏，无需恢复
            }
            else
            {
                if (ownerWasTopmost) window.Topmost = true;
                if (hadGuard)
                {
                    try { WindowHelper.EnforceTopmost(hwnd); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 在截图窗口上就地守候指定依赖组件：
    /// 若已就绪直接返回 true；
    /// 若未就绪，临时解除全屏遮罩的持久置顶守卫并降置顶，以便下载进度卡能够正常呈现在最前层；
    /// 等待下载完成后恢复置顶并返回 true；若取消或失败则恢复置顶、给出轻提示并返回 false。
    /// </summary>
    private static async Task<bool> EnsureDependencyOnCaptureWindowAsync(CaptureWindow window, string depId, string? cancelHint = null)
    {
        if (Core.Deps.DependencyManager.IsReady(depId)) return true;

        IntPtr hwnd = window.GetWindowHwnd();
        bool hadGuard = hwnd != IntPtr.Zero && WindowHelper.IsEnforceTopmostActive(hwnd);
        bool ownerWasTopmost = window.Topmost;

        try
        {
            if (hadGuard)
            {
                try { WindowHelper.StopEnforceTopmost(hwnd); } catch { }
            }
            if (ownerWasTopmost) window.Topmost = false;

            bool ok = await DependencyGate.EnsureAsync(depId).ConfigureAwait(true);
            if (!ok && !string.IsNullOrEmpty(cancelHint) && window.IsVisible)
            {
                StickerToastWindow.Show(cancelHint, window);
            }
            return ok;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CaptureSession: 等待依赖 {depId} 异常: {ex.Message}");
            return false;
        }
        finally
        {
            if (window.IsVisible)
            {
                if (ownerWasTopmost) window.Topmost = true;
                if (hadGuard && hwnd != IntPtr.Zero)
                {
                    try { WindowHelper.EnforceTopmost(hwnd); } catch { }
                }
            }
        }
    }

    private string? SaveResult(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations, IReadOnlyList<TextLayerInfo>? textLayers, string? explicitPath = null)
    {
        Bitmap? cropped = null;
        try
        {
            cropped = CreateCroppedResultBitmap(frame, screenRect, annotations, textLayers, explicitPath != null ? "另存为" : "保存",
                CaptureCornerRadiusPx, UseCaptureShadow, CaptureStrokePx, CaptureStrokeArgb, _activeWindow?.CurrentWatermark);
            if (cropped == null) return null;

            // 「保存」与「另存为」按钮仅保存文件，不写入剪贴板。
            // 剪贴板写入由「完成」按钮或双击触发（CopyResultToClipboard），职责分离。
            string path;
            try
            {
                path = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : ResolveSavePath();
                // 构造截图元数据：截图区域(物理像素) + DPI + 捕获时间，供 PNG tEXt chunk 写入，
                // 将来「从文件打开为贴图」读取后可恢复原尺寸与原位置。
                // 注意：这里记的始终是**选区**矩形（内容来自屏幕哪里），带阴影时成品图会比它大一圈边距。
                CaptureMetadata? metadata = null;
                if (frame != null)
                {
                    metadata = new CaptureMetadata(
                        screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
                        frame.DpiScale, frame.CapturedAt);
                }

                ImageExportService.Save(cropped, path, metadata);
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession: 保存成功 → " + path + (metadata != null ? "（含元数据）" : ""));
            }
            catch (Exception ex)
            {
                AppLogger.Error("CaptureSession: 保存截图文件失败：" + ex.Message, ex);
                return null;
            }

            UsageStatistics.RecordScreenshot(); // 统计：截图成功输出一次
            // 历史：记原始保存路径，供历史窗口「打开文件位置」
            string? historyId = CaptureHistoryStore.TryRecord(cropped, CaptureHistorySource.Save, path, _settings);
            if (historyId != null)
            {
                try
                {
                    bool bakeShadow = UseCaptureShadow;
                    using var clean = CreateCroppedResultBitmap(frame, screenRect,
                        new AnnotationDocument(), null, "保存干净底图",
                        CaptureCornerRadiusPx, shadow: bakeShadow, strokePx: CaptureStrokePx,
                        strokeArgb: CaptureStrokeArgb, watermark: _activeWindow?.CurrentWatermark);
                    if (clean != null)
                    {
                        CaptureHistoryStore.SaveCleanImage(historyId, clean);
                        int annotationOffset = SelectionEffects.MarginOf(bakeShadow, CaptureStrokePx);
                        var editableShapes = TranslateAnnotations(annotations, screenRect, annotationOffset, annotationOffset);
                        var editableTexts = TranslateTextSnapshots(textLayers, screenRect, frame?.DpiScale ?? 1.0, frame?.VirtualScreenRect ?? Rectangle.Empty, annotationOffset, annotationOffset);
                        int marginPx = (int)Math.Round(16.0 * (frame?.DpiScale ?? 1.0));
                        var initialArchive = new StickerArchive
                        {
                            HistoryId = historyId,
                            Kind = "image",
                            ClosedAt = DateTime.Now,
                            Shapes = editableShapes.Select(s => s.Clone()).ToList(),
                            Texts = editableTexts.ToList(),
                            Card = new StickerArchiveCard
                            {
                                ContentRadiusPx = CaptureCornerRadiusPx,
                                StrokeWidthPx = CaptureStrokePx,
                                BakedShadow = bakeShadow,
                            },
                            State = new StickerArchiveState
                            {
                                X = screenRect.X - annotationOffset - marginPx,
                                Y = screenRect.Y - annotationOffset - marginPx,
                                Width = cropped.Width + marginPx * 2,
                                Height = cropped.Height + marginPx * 2,
                                Scale = 1.0,
                                Shadow = true,
                            }
                        };
                        StickerArchiveStore.TryWrite(initialArchive);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureSession.SaveResult 备份干净底图与标注失败: " + ex.Message);
                }
            }
            return path;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureSession.SaveResult 异常", ex);
            throw;
        }
        finally
        {
            try { cropped?.Dispose(); } catch { }
        }
    }

    private StickerWindow ShowLongScrollSticker(LongScrollResult result, Avalonia.PixelPoint position, double scaling)
    {
        Bitmap? bitmap = result.TakeBitmap();
        if (bitmap == null) throw new InvalidOperationException("长截图成片不存在");
        StickerWindow? sticker = null;
        try
        {
            string? historyId = CaptureHistoryStore.TryRecord(bitmap, CaptureHistorySource.LongScroll, null, _settings);
            sticker = new StickerWindow(bitmap, preShowScaling: scaling);
            bitmap = null; // 窗口已取得成片，结果及本方法都不能再次释放。
            sticker.SetHistoryId(historyId);
            sticker.SetStickerScreenPosition(position);
            sticker.Show();
            sticker.Activate();
            StickerFocusManager.SetFocus(sticker);
            return sticker;
        }
        catch
        {
            sticker?.Close();
            throw;
        }
        finally { bitmap?.Dispose(); }
    }

    private CaptureSessionResult? CreateSticker(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations, IReadOnlyList<TextLayerInfo>? textLayers, bool autoMatting = false, MattingModelType? mattingModelType = null)
    {
        if (frame == null) return null;
        Bitmap? cropped = null;
        Bitmap? clean = null;
        try
        {
            // 勾了阴影的截图，贴图位图同样烘入那圈真实半透明阴影带（2026-09-07 用户拍板，
            // 推翻 2026-09-06"贴图一律 shadow:false"的口径）：用户要的是"图片的阴影"——贴图上
            // 垫棋盘格看着它，复制/另存为拿到的就是与"截图写剪贴板"完全一致的带 alpha 成品。
            // 窗体阴影对这类贴图**不让位**：窗体阴影是 UI 状态指示、烘焙阴影是图片素材，
            // 两种阴影并存；光晕包在棋盘格（位图）外沿，由 ApplyBakedCaptureEffects 归零几何实现。
            // ⚠ stickerPad 与下面的 shadow 实参必须是同一个 bool：边距按"带阴影"算、图却不带阴影，
            //   贴图会整体往左上错开 ShadowMarginPx（37 物理 px），肉眼直接可见。
            // 智能抠图（autoMatting）模式下固定不烘阴影与圆角描边，以获取纯净的主体像素，抠图完成后棋盘格由贴图窗口统一接管。
            bool bakeShadow = !autoMatting && UseCaptureShadow;
            int cornerRadiusPx = autoMatting ? 0 : CaptureCornerRadiusPx;
            int strokePx = autoMatting ? 0 : CaptureStrokePx;
            int strokeArgb = autoMatting ? 0 : CaptureStrokeArgb;
            // 智能抠图同样不盖水印：它要的是纯净主体像素，与上面三项效果把值归零是同一个道理。
            // 成品与编辑底图都盖同一份：底图将来重烘标注时水印才不会被抹掉（它在标注之下，烘一次少一次）。
            WatermarkConfig? watermark = autoMatting ? null : _activeWindow?.CurrentWatermark;
            int stickerPad = SelectionEffects.MarginOf(bakeShadow, strokePx);
            cropped = CreateCroppedResultBitmap(frame, screenRect, annotations, textLayers, autoMatting ? "智能抠图" : "贴图",
                cornerRadiusPx, shadow: bakeShadow, strokePx: strokePx, strokeArgb: strokeArgb, watermark);
            if (cropped == null) return null;

            // 判重（2026-09-29 用户口径「同一张图片已经贴过，就不要重复贴图」）：成品像素与屏幕上某张
            // 贴图相同 → 定位过去，不再贴第二份、也不录这条历史（两张位图交给 finally 统一释放）。
            // 位置必须卡在干净底图渲染之前：那一次重渲染实测 80-100ms，判重命中时纯属白做。
            // ⚠ 智能抠图不判：那张窗口是抠图的工作台而不是"贴一张图"，判重会整次吞掉抠图结果。
            if (!autoMatting)
            {
                var duplicate = StickerDedup.FindSameImage(cropped);
                if (duplicate != null)
                {
                    StickerDedup.Reveal(duplicate, "截图贴图");
                    return null;
                }
            }

            // 成品位图已经包含标注，贴图进入编辑时必须另带一张同尺寸的干净底图，
            // 否则矢量/模糊/文字会被当成普通像素，无法再次编辑。
            // ⚠ 性能（2026-09-24）：本次截图**没有任何标注/文字层**时，干净底图与成品是同一套参数
            // （圆角/阴影/描边/水印全同）、渲染结果逐像素相同——再走一遍整图渲染纯属白做（实测显示前
            // 准备 ~200ms 里两次渲染占大头）。此时直接克隆成品当底图（内存拷贝几 ms vs 重渲染 ~80-100ms）。
            // 有标注时两图确实不同（成品烘了图元、底图没有），照旧各渲染一次。
            bool noAnnotations = annotations.Count == 0 && (textLayers == null || textLayers.Count == 0);
            if (noAnnotations)
            {
                clean = (Bitmap)cropped.Clone();
            }
            else
            {
                clean = CreateCroppedResultBitmap(frame, screenRect,
                    new AnnotationDocument(), null, autoMatting ? "智能抠图底图" : "贴图编辑底图",
                    cornerRadiusPx, shadow: bakeShadow, strokePx: strokePx,
                    strokeArgb: strokeArgb, watermark);
            }
            if (clean == null) return null;

            // 历史：**必须在所有权转移给贴图窗口之前**记录（内部同步克隆一份，不动原图）。
            // 下面 cropped 会被置 null，之后就再也拿不到这张位图了。
            string? stickerHistoryId = CaptureHistoryStore.TryRecord(cropped, CaptureHistorySource.Sticker, null, _settings);
            if (stickerHistoryId != null)
            {
                CaptureHistoryStore.SaveCleanImage(stickerHistoryId, clean);
            }

            // SelectionEffects 以 stickerPad 向四边扩展，模型坐标也要落到扩展后的内容坐标系。
            int annotationOffset = stickerPad;
            var editableShapes = TranslateAnnotations(
                annotations, screenRect, annotationOffset, annotationOffset);
            var editableTexts = TranslateTextSnapshots(
                textLayers, screenRect, frame.DpiScale, frame.VirtualScreenRect, annotationOffset, annotationOffset);
            if (stickerHistoryId != null)
            {
                int marginPx = (int)Math.Round(16.0 * frame.DpiScale);
                var initialArchive = new StickerArchive
                {
                    HistoryId = stickerHistoryId,
                    Kind = "image",
                    ClosedAt = DateTime.Now,
                    Shapes = editableShapes.Select(s => s.Clone()).ToList(),
                    Texts = editableTexts.ToList(),
                    Card = new StickerArchiveCard
                    {
                        ContentRadiusPx = cornerRadiusPx,
                        StrokeWidthPx = strokePx,
                        BakedShadow = bakeShadow,
                    },
                    State = new StickerArchiveState
                    {
                        X = screenRect.X - stickerPad - marginPx,
                        Y = screenRect.Y - stickerPad - marginPx,
                        Width = cropped.Width + marginPx * 2,
                        Height = cropped.Height + marginPx * 2,
                        Scale = 1.0,
                        Shadow = true,
                    }
                };
                StickerArchiveStore.TryWrite(initialArchive);
            }
            var sticker = new StickerWindow(cropped, cleanAnnotationSource: clean,
                annotationShapes: editableShapes, annotationTexts: editableTexts,
                // 截图那一刻就实测到的 DPI，交贴图窗在 Show 前用掉：否则构造函数那次 ApplyScale 只能按
                // 1.0 占位，150% 屏上首帧窗口大 1.5 倍、阴影边距也少算一圈，Opened 再夹回来就是"弹一下"
                preShowScaling: frame.DpiScale);
            sticker.SetHistoryId(stickerHistoryId);
            // 🔴 **截图时「阴影」的勾选意图**（2026-10-01，issue #50）：必须在这里**无条件**下发，
            // 不能并进下面那个 `if (cornerRadiusPx > 0 || strokePx > 0 || bakeShadow)`——三项全零时
            // 那个 if 整段不执行，而"没勾阴影"恰恰是意图位要表达的一种取值（用户明确不要边界）。
            // ⚠ 这与 `_hasBakedShadow` 是两件事：后者说"图的像素里已经有一圈阴影带"（物理事实），
            // 前者说"用户认为这张图需要边界"（意图）。翻译换图会让前者的依据消失、后者必须留住——
            // 那正是 issue #50「翻译后看不清边界」的成因。
            sticker.SetCaptureShadowIntent(bakeShadow);
            // 仅为剪贴板抠图临时创建的载体，不算一次用户贴图。
            sticker.RecordUsageOnOpen = !autoMatting || _settings.GetMattingResultDestination()
                != ScreenCaptureTool.Core.Matting.MattingResultDestination.Clipboard;
            // 卡片几何交贴图窗体阴影同步用。内容半径按**成品实际内容尺寸**（= 成品位图扣掉四周边距）算，
            // 小选区上 SelectionEffects.EffectiveRadius 会把圆角夹到短边一半，抄原始设置值就会与图上的
            // 轮廓不符；描边宽必须一并传——光晕基准线是内容轮廓外扩描边宽的真等距线，只传半径就会
            // 在四个角各留一条缝（同族缩放的固有误差，2026-09-06 修）。
            // 烘了阴影的贴图用不上这份几何（光晕包在棋盘格外沿），但 hasBakedShadow 标记要从这里下发。
            if (cornerRadiusPx > 0 || strokePx > 0 || bakeShadow)
            {
                var card = SelectionEffects.CardGeometryOf(
                    cornerRadiusPx, strokePx,
                    cropped.Width - stickerPad * 2, cropped.Height - stickerPad * 2);
                sticker.ApplyBakedCaptureEffects(card, hasBakedShadow: bakeShadow, shadowPadPx: stickerPad);
                // 诊断：贴图阴影观感不对时（角部不贴、整体错位）先用这几行数比对，不必猜。
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"CaptureSession: 贴图效果 内容圆角={card.ContentRadiusPx:F1}px"
                    + $" 描边={card.StrokeWidthPx:F1}px 光晕基准内缩={card.ContentInsetPx:F1}px"
                    + $" 定位边距={stickerPad}px 阴影=" + (bakeShadow ? "烘焙入图" : "窗体"));
            }
            // 位图所有权已转移给贴图窗口（StickerWindow 直接引用并在 OnClosed 释放），
            // 置空防止 finally 提前 Dispose——此前提前释放导致贴图的"文本可选择"OCR、
            // 翻译、另存为全部抛 "Parameter is not valid"（显示不受影响，因为显示用
            // 的是构造时已拷贝好的 Avalonia 位图）。
            cropped = null;
            clean = null;
            // screenRect 是屏幕物理像素坐标 = 内容（选区）该出现的位置；SetStickerScreenPosition
            // 对齐的是整张位图的左上角，故先减去成品自己外扩的边距。窗口本身还会再左移上移一圈窗体阴影。
            sticker.SetStickerScreenPosition(new Avalonia.PixelPoint(screenRect.X - stickerPad, screenRect.Y - stickerPad));
            sticker.Show();
            sticker.Activate();
            StickerFocusManager.SetFocus(sticker); // 转化的贴图自动获得焦点（阴影变蓝）
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureSession: 已创建贴图，原位 screenRect=" + screenRect);
            UsageStatistics.RecordScreenshot(); // 统计：截图成功输出一次

            if (autoMatting)
            {
                var modelType = mattingModelType ?? _settings.GetMattingModelType();
                // 本贴图为抠图临时创建：结果去向设为「仅剪贴板」时抠图完成后自动关闭（桌面不留窗）
                sticker.ExecuteMatting(modelType, closeWhenClipboardOnly: true);
            }

            return CaptureSessionResult.StickerCreated();
        }
        catch (Exception ex)
        {
            AppLogger.Error("CaptureSession.CreateSticker 异常", ex);
            throw;
        }
        finally
        {
            try { cropped?.Dispose(); } catch { }
            try { clean?.Dispose(); } catch { }
        }
    }

    private static IReadOnlyList<AnnotationShape> TranslateAnnotations(
        AnnotationDocument source, Rectangle selectedScreenRect, int dx, int dy)
    {
        if (source.Count == 0) return Array.Empty<AnnotationShape>();
        return source.Shapes.Select(shape => shape switch
        {
            StrokeShape stroke => stroke.Translate(dx - selectedScreenRect.X, dy - selectedScreenRect.Y),
            ArrowShape arrow => new ArrowShape(
                new PointF(arrow.Start.X - selectedScreenRect.X + dx, arrow.Start.Y - selectedScreenRect.Y + dy),
                new PointF(arrow.End.X - selectedScreenRect.X + dx, arrow.End.Y - selectedScreenRect.Y + dy), arrow.Color,
                arrow.Thickness, arrow.Style, arrow.Scale, arrow.Id,
                new PointF(arrow.ControlPoint.X - selectedScreenRect.X + dx, arrow.ControlPoint.Y - selectedScreenRect.Y + dy)),
            RectangleShape rectangle => new RectangleShape(
                new RectangleF(rectangle.Rect.X - selectedScreenRect.X + dx, rectangle.Rect.Y - selectedScreenRect.Y + dy,
                    rectangle.Rect.Width, rectangle.Rect.Height), rectangle.StrokeColor,
                rectangle.StrokeThickness, rectangle.ShapeKind, rectangle.LineStyle, rectangle.CornerRadius,
                rectangle.FillOpacity, rectangle.Id),
            // Translated 而不是重建：它同时搬圆点与引线终点（引线不会被拉断），其余字段整份留着——
            // 类型档、形状底色、说明文字这些 2026-09-18 才加的参数，手写构造抄一遍就会静默丢一片。
            CounterShape counter => counter.Translated(dx - selectedScreenRect.X, dy - selectedScreenRect.Y),
            BlurShape blur => blur.WithRect(new RectangleF(
                blur.Rect.X - selectedScreenRect.X + dx, blur.Rect.Y - selectedScreenRect.Y + dy, blur.Rect.Width, blur.Rect.Height)),
            SpotlightShape spotlight => spotlight.WithRect(new RectangleF(
                spotlight.Rect.X - selectedScreenRect.X + dx, spotlight.Rect.Y - selectedScreenRect.Y + dy,
                spotlight.Rect.Width, spotlight.Rect.Height)),
            ErasureShape erase => erase.WithMarks(erase.Marks.Select(mark => new ErasureMark(
                new RectangleF(mark.Bounds.X - selectedScreenRect.X + dx,
                    mark.Bounds.Y - selectedScreenRect.Y + dy, mark.Bounds.Width, mark.Bounds.Height),
                mark.IsCircle))),
            _ => shape.Clone()
        }).ToArray();
    }

    private static IReadOnlyList<TextLayerSnapshot> TranslateTextSnapshots(
        IReadOnlyList<TextLayerInfo>? layers, Rectangle selectedScreenRect, double dpiScale,
        Rectangle virtualScreenRect, int dx, int dy)
    {
        if (layers == null || layers.Count == 0) return Array.Empty<TextLayerSnapshot>();
        return layers.Where(x => x?.TextBox != null && x.Root != null)
            .Select(x =>
            {
                var s = TextLayerSnapshot.FromLayer(x);
                double dpi = dpiScale > 0 ? dpiScale : 1.0;
                double localLeft = (s.Left * dpi + virtualScreenRect.X - selectedScreenRect.X) / dpi + dx / dpi;
                double localTop = (s.Top * dpi + virtualScreenRect.Y - selectedScreenRect.Y) / dpi + dy / dpi;
                return new TextLayerSnapshot
                {
                    Id = s.Id,
                    Text = s.Text, Left = localLeft, Top = localTop, MaxWidth = s.MaxWidth,
                    FontSize = s.FontSize, IsBold = s.IsBold, IsItalic = s.IsItalic,
                    HasStroke = s.HasStroke, HasBackground = s.HasBackground,
                    FontFamily = s.FontFamily, Color = s.Color, FillColor = s.FillColor,
                    StrokeColor = s.StrokeColor, StrokeThickness = s.StrokeThickness,
                    FillOpacity = s.FillOpacity, FillCornerRadius = s.FillCornerRadius,
                    FillPadding = s.FillPadding, RotationAngle = s.RotationAngle,
                    MeasuredWidth = s.MeasuredWidth, MeasuredHeight = s.MeasuredHeight,
                    LineHeight = s.LineHeight
                };
            }).ToArray();
    }

    private Bitmap? CreateCroppedResultBitmap(CapturedFrame? frame, Rectangle screenRect, AnnotationDocument annotations,
        IReadOnlyList<TextLayerInfo>? textLayers, string operationName, int cornerRadiusPx = 0, bool shadow = false, int strokePx = 0,
        int strokeArgb = SelectionEffects.DefaultStrokeArgb, WatermarkConfig? watermark = null)
    {
        if (frame == null) return null;

        Rectangle bmpRect = new Rectangle(
            screenRect.X - frame.VirtualScreenRect.X,
            screenRect.Y - frame.VirtualScreenRect.Y,
            screenRect.Width,
            screenRect.Height);

        bmpRect.Intersect(new Rectangle(0, 0, frame.FullScreen.Width, frame.FullScreen.Height));
        if (bmpRect.Width <= 0 || bmpRect.Height <= 0)
        {
            AppLogger.Warn("CaptureSession.CreateCroppedResultBitmap: 选区与位图无交集，operation=" + operationName);
            return null;
        }

        Bitmap cropped = frame.FullScreen.Clone(bmpRect, PixelFormat.Format32bppArgb);
        // 水印排在标注<b>之前</b>：拍板第 1 条要它盖在底图之上、所有标注之下（马赛克/模糊是对底图做处理，
        // 字压进码块里看着就像码没打全）。此刻位图还是"纯内容"，圆角/描边/阴影那圈边距尚未展开，
        // 故 WatermarkEffect 一律以位图自身尺寸为内容矩形。
        WatermarkEffect.Apply(cropped, watermark);
        RenderAnnotationsToBitmap(cropped, screenRect, frame.VirtualScreenRect, annotations);
        RenderTextLayersToBitmap(cropped, screenRect, textLayers, frame.DpiScale, frame.VirtualScreenRect, annotations);

        if (_selectionPath is { } selectionPath)
        {
            // 在水印／标注之后裁 Alpha，保证它们不会越出路径；矩形装饰不套在异形外接框上。
            try
            {
                selectionPath.ApplyAlphaMask(cropped, new Rectangle(frame.VirtualScreenRect.X + bmpRect.X,
                    frame.VirtualScreenRect.Y + bmpRect.Y, bmpRect.Width, bmpRect.Height));
                return cropped;
            }
            catch { cropped.Dispose(); throw; }
        }

        // 圆角/描边/投影放在最后一步：它们处理的是"含标注的成品"，先做后画会让标注溢出到圆角外。
        // 返回 null 表示三项都不需要，原裁图原样交出去（逐像素与旧版一致）。
        Bitmap? effected = SelectionEffects.Create(cropped, cornerRadiusPx, shadow, strokePx, strokeArgb);
        if (effected != null)
        {
            cropped.Dispose();
            return effected;
        }
        return cropped;
    }

    /// <summary>供贴图宿主复用既有标注栅格算法；传入的底图由调用方独占，坐标与文档同源。</summary>
    internal static void RenderAnnotationsToBitmap(Bitmap bitmap, Rectangle selectedScreenRect, Rectangle virtualScreenRect, AnnotationDocument annotations)
    {
        if (bitmap == null || annotations == null || annotations.Count == 0) return;
        ErasureShape[] erasures = annotations.Shapes.OfType<ErasureShape>().ToArray();

        // 叠放顺序与截图预览一致：模糊/马赛克 → 聚光灯暗幕与边框 → 线条/箭头/形状/序号。
        foreach (AnnotationShape shape in annotations.Shapes)
        {
            if (shape is BlurShape blur)
            {
                ErasureMark[] marks = ErasureMarksFor(erasures, blur.Id, blur.Bounds);
                using Bitmap? before = marks.Length > 0 ? (Bitmap)bitmap.Clone() : null;
                ApplyBlurShape(bitmap, selectedScreenRect, virtualScreenRect, blur);
                if (before != null) RestoreErasedPixels(bitmap, before, selectedScreenRect, marks);
            }
        }

        var spotlights = new List<SpotlightShape>();
        foreach (AnnotationShape shape in annotations.Shapes)
        {
            if (shape is SpotlightShape spotlight)
            {
                spotlights.Add(spotlight);
            }
        }
        ErasureMark[] spotlightMarks = spotlights.SelectMany(shape => ErasureMarksFor(erasures, shape.Id)).ToArray();
        using (Bitmap? beforeSpotlights = spotlightMarks.Length > 0 ? (Bitmap)bitmap.Clone() : null)
        {
            ApplySpotlightShapes(bitmap, selectedScreenRect, spotlights);
            if (beforeSpotlights != null) RestoreErasedPixels(bitmap, beforeSpotlights, selectedScreenRect, spotlightMarks);
        }

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        foreach (AnnotationShape shape in annotations.Shapes)
        {
            if (shape is not (StrokeShape or ArrowShape or RectangleShape or CounterShape)) continue;
            ErasureMark[] marks = ErasureMarksFor(erasures, shape.Id, shape.Bounds);
            GraphicsState? state = null;
            if (marks.Length > 0)
            {
                state = g.Save();
                using Region eraseRegion = CreateErasureRegion(marks, selectedScreenRect);
                g.ExcludeClip(eraseRegion);
            }
            try
            {
            if (shape is StrokeShape stroke)
            {
                RenderStroke(g, selectedScreenRect, stroke);
            }
            else if (shape is ArrowShape arrow)
            {
                RenderArrow(g, selectedScreenRect, arrow);
            }
            else if (shape is RectangleShape rect)
            {
                RenderAnnotationRectangle(g, selectedScreenRect, rect);
            }
            else if (shape is CounterShape counter)
            {
                RenderCounter(g, selectedScreenRect, counter);
            }
            }
            finally { if (state != null) g.Restore(state); }
        }
    }

    private static ErasureMark[] ErasureMarksFor(IReadOnlyList<ErasureShape> erasures, Guid id,
        RectangleF? cullBounds = null)
    {
        // cullBounds＝只保留可能作用到这个范围的落点；布尔运算/区域构造的成本只随落点枚数增长，
        // 一笔长涂抹里绝大多数落点其实与这个图元毫无关系。传 null＝不裁剪（聚光灯暗幕铺满整个取景区）。
        // 余量按落点自身尺寸给，兜住描边溢出包围盒与取整。与预览侧 AnnotationLayer.CollectErasureMarks 同一判据。
        IEnumerable<ErasureMark> picked = erasures.Where(erase => erase.AffectsShape(id)).SelectMany(erase => erase.Marks);
        if (!cullBounds.HasValue) return picked.ToArray();
        RectangleF bounds = cullBounds.Value;
        return picked.Where(mark =>
        {
            float reach = Math.Max(mark.Bounds.Width, mark.Bounds.Height);
            return mark.Bounds.IntersectsWith(RectangleF.FromLTRB(bounds.Left - reach, bounds.Top - reach,
                bounds.Right + reach, bounds.Bottom + reach));
        }).ToArray();
    }

    private static Region CreateErasureRegion(IReadOnlyList<ErasureMark> marks, Rectangle origin)
    {
        using var path = new GraphicsPath
        {
            // 洞必须取并集：GraphicsPath 默认 FillMode.Alternate 是"奇偶"，两枚洞重叠处会被判成"不是洞"，
            // 于是"在同一处再擦一次"反而把上次擦掉的标注放回来（2026-10-06，与预览侧 NonZero 同一修法）。
            FillMode = FillMode.Winding
        };
        foreach (ErasureMark mark in marks)
        {
            var rect = new RectangleF(mark.Bounds.X - origin.X, mark.Bounds.Y - origin.Y,
                mark.Bounds.Width, mark.Bounds.Height);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            if (mark.IsCircle) path.AddEllipse(rect);
            else path.AddRectangle(rect);
        }
        return new Region(path);
    }

    private static void RestoreErasedPixels(Bitmap target, Bitmap before, Rectangle origin,
        IReadOnlyList<ErasureMark> marks)
    {
        using Graphics graphics = Graphics.FromImage(target);
        using Region region = CreateErasureRegion(marks, origin);
        graphics.SetClip(region, CombineMode.Replace);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(before, 0, 0);
    }

    /// <summary>将 OverlayCanvas 上的文本图层渲染到输出位图（绘制算法与换算口径见 <see cref="TextLayerRenderer"/>，与贴图定稿共用）。</summary>
    /// <param name="dpiScale">截屏 DPI 缩放（96 DPI = 1.0），用于将 DIP 坐标换算为物理像素。</param>
    /// <param name="virtualScreenRect">虚拟屏幕矩形（物理像素），即窗口 Position 对应的屏幕区域。</param>
    internal static void RenderTextLayersToBitmap(Bitmap bitmap, Rectangle selectedScreenRect,
        IReadOnlyList<TextLayerInfo>? textLayers, double dpiScale, Rectangle virtualScreenRect,
        AnnotationDocument? annotations = null)
    {
        if (textLayers == null || textLayers.Count == 0) return;

        // 与搬迁前一致：活控件不全的层不参与烘焙。
        var snapshots = new List<TextLayerSnapshot>(textLayers.Count);
        foreach (TextLayerInfo info in textLayers)
        {
            if (info?.TextBox == null || info.Root == null) continue;
            snapshots.Add(TextLayerSnapshot.FromLayer(info));
        }

        TextLayerRenderer.RenderToBitmap(bitmap, snapshots,
            TextBakeContext.CreateCaptureContext(bitmap, selectedScreenRect, dpiScale, virtualScreenRect), annotations);
    }

    private static void RenderStroke(Graphics g, Rectangle selectedScreenRect, StrokeShape stroke)
    {
        BrushCoverage coverage = BrushRasterizer.Build(stroke);
        float opacity = BrushRasterizer.GetInkOpacity(stroke);
        if (coverage.Width <= 0 || coverage.Height <= 0 || opacity <= 0.0f) return;

        BlitCoverage(g, coverage, stroke.Color, opacity,
            coverage.OriginX - selectedScreenRect.X,
            coverage.OriginY - selectedScreenRect.Y);
    }

    /// <summary>
    /// 覆盖率位图 1:1 贴到结果位图。贴图字节由 Core 的 <see cref="BrushRasterizer.BakeBitmap"/> 烘出，
    /// 预览侧走同一批字节换算出的 WriteableBitmap；这里把三项 Graphics 状态临时锁死：
    /// 最近邻 + 半像素偏移防重采样把墨边挪出 1 像素，合成模式强制线性（AssumeLinear）——
    /// 上游的 HighQuality 会让 GDI+ 做带 gamma 的色彩匹配，把预乘墨水的 alpha 折算掉（实测 153→85，笔迹发淡），
    /// 而预览侧 Skia 是逐字节线性合成，两侧必须同一口径，否则又是"截图时与截图后不一样"。
    /// </summary>
    private static void BlitCoverage(Graphics g, BrushCoverage coverage, Color color, float opacity, int dstX, int dstY)
    {
        using Bitmap? layer = BrushRasterizer.BakeBitmap(coverage, color, opacity);
        if (layer == null) return;

        InterpolationMode previousInterpolation = g.InterpolationMode;
        PixelOffsetMode previousPixelOffset = g.PixelOffsetMode;
        CompositingQuality previousCompositingQuality = g.CompositingQuality;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.AssumeLinear;
        try
        {
            g.DrawImageUnscaled(layer, dstX, dstY);
        }
        finally
        {
            g.InterpolationMode = previousInterpolation;
            g.PixelOffsetMode = previousPixelOffset;
            g.CompositingQuality = previousCompositingQuality;
        }
    }

    private static void RenderArrow(Graphics g, Rectangle selectedScreenRect, ArrowShape arrow)
    {
        PointF start = ToLocalPoint(arrow.Start, selectedScreenRect);
        PointF end = ToLocalPoint(arrow.End, selectedScreenRect);
        PointF control = ToLocalPoint(arrow.ControlPoint, selectedScreenRect);
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0) return;

        // 箭头模板坐标(0~100)与尺寸一律按物理像素运算，与画笔/形状/聚光灯的粗细口径一致。
        float thickness = Math.Clamp(Math.Max(1.0f, arrow.Thickness), 1.0f, 45.0f);
        float midX = (start.X + end.X) / 2f;
        float midY = (start.Y + end.Y) / 2f;
        float ctrlOff = (float)Math.Sqrt((control.X - midX) * (control.X - midX) + (control.Y - midY) * (control.Y - midY));

        if (ArrowHeadGeometry.IsLegacyStyle(arrow.Style))
        {
            (GraphicsPath Shaft, GraphicsPath Head) paths = ctrlOff < 2f
                ? CreateLegacyArrowPaths(start, end, thickness, arrow.Style, arrow.Scale)
                : CreateBentArrowPaths(start, end, control, thickness, arrow.Style, arrow.Scale);
            using (paths.Shaft)
            using (paths.Head)
            using (var brush = new SolidBrush(arrow.Color))
            {
                g.FillPath(brush, paths.Shaft);
                g.FillPath(brush, paths.Head);
            }
            return;
        }

        // 新增头型：几何全在 Core 那一份算式里，成品只照点列填/描（预览侧 DrawArrow 消费同一份）
        float unit = (float)Math.Sqrt(Math.Max(arrow.Scale, 0.01f));
        float worldWidth = thickness * unit;
        var parts = ctrlOff < 2f
            ? ArrowHeadGeometry.BuildStraight(start, end, worldWidth, unit, arrow.Style, doubleHead: true)
            : ArrowHeadGeometry.BuildBent(start, control, end, worldWidth, unit, arrow.Style, doubleHead: true);
        foreach (ArrowPart part in parts) DrawArrowPart(g, arrow.Color, part);
    }

    /// <summary>箭头部件：填充件压多边形，描边件用圆角连接的开放/闭合折线（与预览侧 DrawArrowPart 同一套）。</summary>
    private static void DrawArrowPart(Graphics g, Color color, ArrowPart part)
    {
        if (part.Points.Length < 2) return;

        using var path = new GraphicsPath();
        if (part.Kind == ArrowPartKind.Fill)
        {
            path.AddPolygon(part.Points);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
            return;
        }

        path.AddLines(part.Points);
        if (part.Kind == ArrowPartKind.StrokeClosed) path.CloseFigure();
        using var pen = new Pen(color, part.Width)
        {
            LineJoin = LineJoin.Round,
            StartCap = part.RoundCap ? LineCap.Round : LineCap.Flat,
            EndCap = part.RoundCap ? LineCap.Round : LineCap.Flat,
        };
        g.DrawPath(pen, path);
    }

    private static void RenderAnnotationRectangle(Graphics g, Rectangle selectedScreenRect, RectangleShape shape)
    {
        RectangleF r = shape.Rect;
        RectangleF local = RectangleF.FromLTRB(
            r.Left - selectedScreenRect.X,
            r.Top - selectedScreenRect.Y,
            r.Right - selectedScreenRect.X,
            r.Bottom - selectedScreenRect.Y);
        if (local.Width <= 0 || local.Height <= 0) return;

        using var pen = new Pen(shape.StrokeColor, Math.Max(1.0f, shape.StrokeThickness))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        if (shape.LineStyle == AnnotationLineStyle.DashLarge)
        {
            pen.DashPattern = new float[]
            {
                (float)AnnotationRenderOptions.DashLargeOn,
                (float)AnnotationRenderOptions.DashLargeOff,
            };
            pen.DashCap = DashCap.Round;
        }

        // 填充色不单存字段：RGB 恒取线条色，alpha 由浓淡档折算（与描边自身的 alpha 互不牵连）。
        // 与预览侧 AnnotationLayer.DrawAnnotationRectangle 同一口径，两侧不一致会出现"预览有填充、成品没有"。
        using SolidBrush? fillBrush = shape.FillOpacity > 0.0f
            ? new SolidBrush(Color.FromArgb((byte)Math.Clamp(shape.FillOpacity * 255.0f, 0.0f, 255.0f),
                shape.StrokeColor.R, shape.StrokeColor.G, shape.StrokeColor.B))
            : null;

        if (shape.ShapeKind == ShapeAnnotationKind.Ellipse)
        {
            if (fillBrush != null) g.FillEllipse(fillBrush, local.X, local.Y, local.Width, local.Height);
            g.DrawEllipse(pen, local.X, local.Y, local.Width, local.Height);
        }
        else if (shape.CornerRadius > 0.0f)
        {
            // 与预览（AnnotationLayer.DrawAnnotationRectangle 的 RectangleGeometry 圆角绘制）同一口径：
            // 半径上限夹到短边一半。放大/缩小时图元几何不变，故成品与预览的圆角必然一致。
            float radius = Math.Clamp(shape.CornerRadius, 0.0f, Math.Min(local.Width, local.Height) / 2.0f);
            using GraphicsPath path = CreateRoundedRectanglePath(local, radius);
            RenderAntialiasedShapePath(g, path, pen, fillBrush);
        }
        else
        {
            if (fillBrush != null) g.FillRectangle(fillBrush, local.X, local.Y, local.Width, local.Height);
            g.DrawRectangle(pen, local.X, local.Y, local.Width, local.Height);
        }
    }

    // 仅提高圆角矩形覆盖层的采样精度，保留 GDI+ 线宽、虚线周期、圆头及擦除裁切。
    private static void RenderAntialiasedShapePath(Graphics target, GraphicsPath path, Pen pen, SolidBrush? fill)
    {
        // 实线统一细分提高圆弧精度；虚线保留原 GDI+ 曲线路径，避免改变其曲线度量。
        if (pen.DashStyle == DashStyle.Solid) path.Flatten(null, 1.0f / 64.0f);
        RectangleF bounds = path.GetBounds(null, pen);
        bounds.Inflate(2, 2);
        bounds.Intersect(target.VisibleClipBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        int left = (int)Math.Floor(bounds.Left), top = (int)Math.Floor(bounds.Top);
        int right = (int)Math.Ceiling(bounds.Right), bottom = (int)Math.Ceiling(bounds.Bottom);
        using var tile = new AnnotationCoverageTile();
        using var raster = Graphics.FromImage(tile.Drawing);
        raster.SmoothingMode = SmoothingMode.AntiAlias;
        raster.CompositingQuality = CompositingQuality.AssumeLinear;
        raster.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var state = target.Save();
        try
        {
            target.CompositingMode = CompositingMode.SourceOver;
            target.CompositingQuality = CompositingQuality.AssumeLinear;
            target.InterpolationMode = InterpolationMode.NearestNeighbor;
            target.PixelOffsetMode = PixelOffsetMode.Half;
            for (int y = top; y < bottom; y += AnnotationCoverageTile.Size)
            for (int x = left; x < right; x += AnnotationCoverageTile.Size)
            {
                raster.ResetTransform();
                raster.Clear(Color.Transparent);
                const int scale = AnnotationCoverageTile.Scale;
                const int padding = AnnotationCoverageTile.Padding;
                using var transform = new System.Drawing.Drawing2D.Matrix(scale, 0, 0, scale, (-x + padding) * scale, (-y + padding) * scale);
                raster.Transform = transform;
                if (fill != null) raster.FillPath(fill, path);
                raster.DrawPath(pen, path);
                raster.Flush(FlushIntention.Sync);
                tile.Resolve();
                tile.DrawTo(target, x, y);
            }
        }
        finally { target.Restore(state); }
    }

    private static (GraphicsPath Shaft, GraphicsPath Head) CreateLegacyArrowPaths(PointF start, PointF end, float thickness, ArrowAnnotationStyle style, float scale = 1.0f)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0f)
        {
            return (new GraphicsPath(), new GraphicsPath());
        }

        float ux = dx / len;
        float uy = dy / len;
        float nx = -uy;
        float ny = ux;

        // 对齐 AnnotationLayer.CreateLegacyArrowGeometry：visualScale = sqrt(scale)
        // templateHeight/headSize/headOverlap 一律按物理像素取值，与预览同一单位口径
        float visualScale = (float)Math.Sqrt(Math.Max(scale, 0.01f));
        float templateHeight = 50.0f * visualScale;
        float headSize = 50.0f * visualScale;
        float headOverlap = 25.0f * visualScale;
        float shaftSlotWidth = Math.Max(1.0f, len - headOverlap);
        float shaftXScale = shaftSlotWidth / 100.0f;
        float shaftYScale = templateHeight / 100.0f;
        float headX = len - headSize;
        float headScale = headSize / 100.0f;
        // thickness 是模板坐标(0~100)内的取值，两侧同口径，不参与像素/DIP 换算
        float legacyThickness = Math.Clamp(thickness, 0.5f, 45.0f);
        float ctrlYTop = 50.0f - legacyThickness;
        float ctrlYBottom = 50.0f + legacyThickness;

        PointF T(float x, float y)
        {
            float localX = x * shaftXScale;
            float localY = (y - 50.0f) * shaftYScale;
            return new PointF(start.X + ux * localX + nx * localY, start.Y + uy * localX + ny * localY);
        }

        PointF H(float x, float y)
        {
            float localX = headX + x * headScale;
            float localY = (y - 50.0f) * headScale;
            return new PointF(start.X + ux * localX + nx * localY, start.Y + uy * localX + ny * localY);
        }

        var shaft = new GraphicsPath();
        if (style == ArrowAnnotationStyle.Sharp)
        {
            shaft.StartFigure();
            AddQuadraticBezier(shaft, T(0, 40), T(50, ctrlYTop), T(100, 35));
            shaft.AddLine(T(100, 35), T(100, 65));
            AddQuadraticBezier(shaft, T(100, 65), T(50, ctrlYBottom), T(0, 60));
            shaft.CloseFigure();
        }
        else
        {
            shaft.StartFigure();
            AddQuadraticBezier(shaft, T(0, 50), T(50, ctrlYTop), T(100, 35));
            shaft.AddLine(T(100, 35), T(100, 65));
            AddQuadraticBezier(shaft, T(100, 65), T(50, ctrlYBottom), T(0, 50));
            shaft.CloseFigure();
        }

        var head = new GraphicsPath();
        if (style == ArrowAnnotationStyle.Sharp)
        {
            head.AddPolygon(new[] { H(20, 20), H(100, 50), H(20, 80) });
        }
        else
        {
            head.AddPolygon(new[] { H(50, 50), H(20, 20), H(100, 50), H(20, 80) });
        }

        return (shaft, head);
    }


    private static (GraphicsPath Shaft, GraphicsPath Head) CreateBentArrowPaths(PointF start, PointF end, PointF control, float thickness, ArrowAnnotationStyle style, float scale = 1.0f)
    {
        // 完全对齐 AnnotationLayer.CreateBentArrowGeometry：模板扫描法
        // th/hs/ho 一律按物理像素取值，与预览同一单位口径
        float vs = (float)Math.Sqrt(Math.Max(scale, 0.01f));
        float th = 50.0f * vs, hs = 50.0f * vs, ho = 25.0f * vs;
        // thickness 是模板坐标(0~100)内的取值，两侧同口径，不参与像素/DIP 换算
        float mt2 = Math.Clamp(thickness, 0.5f, 45.0f);
        float cYt = 50.0f - mt2, cYb = 50.0f + mt2;
        double ch1 = Math.Sqrt((control.X - start.X) * (control.X - start.X) + (control.Y - start.Y) * (control.Y - start.Y));
        double ch2 = Math.Sqrt((end.X - control.X) * (end.X - control.X) + (end.Y - control.Y) * (end.Y - control.Y));
        double aLen = ch1 + ch2;
        double ssw = Math.Max(1.0, aLen - ho);
        float sYs = th / 100.0f;
        const int N = 40;
        var up = new PointF[N]; var lp = new PointF[N];

        for (int i = 0; i < N; i++)
        {
            double t = i / (double)(N - 1);
            float uy, ly;
            if (style == ArrowAnnotationStyle.Sharp)
            {
                uy = (float)((1 - t) * (1 - t) * 40.0 + 2 * (1 - t) * t * cYt + t * t * 35.0);
                ly = (float)((1 - t) * (1 - t) * 60.0 + 2 * (1 - t) * t * cYb + t * t * 65.0);
            }
            else
            {
                uy = (float)((1 - t) * (1 - t) * 50.0 + 2 * (1 - t) * t * cYt + t * t * 35.0);
                ly = (float)((1 - t) * (1 - t) * 50.0 + 2 * (1 - t) * t * cYb + t * t * 65.0);
            }
            double mt = 1.0 - t;
            double cx = mt * mt * start.X + 2 * mt * t * control.X + t * t * end.X;
            double cy = mt * mt * start.Y + 2 * mt * t * control.Y + t * t * end.Y;
            double tx = 2 * mt * (control.X - start.X) + 2 * t * (end.X - control.X);
            double ty = 2 * mt * (control.Y - start.Y) + 2 * t * (end.Y - control.Y);
            double tl = Math.Sqrt(tx * tx + ty * ty);
            double nx = 0, ny = 0;
            if (tl > 0.001) { nx = -ty / tl; ny = tx / tl; }
            double yo = (uy - 50.0) * sYs;
            up[i] = new PointF((float)(cx + nx * yo), (float)(cy + ny * yo));
            yo = (ly - 50.0) * sYs;
            lp[i] = new PointF((float)(cx + nx * yo), (float)(cy + ny * yo));
        }

        // 终点切线方向
        double eTx = 2.0 * (end.X - control.X), eTy = 2.0 * (end.Y - control.Y);
        double eTl = Math.Sqrt(eTx * eTx + eTy * eTy);
        if (eTl < 0.001)
        {
            eTx = end.X - start.X; eTy = end.Y - start.Y;
            eTl = Math.Sqrt(eTx * eTx + eTy * eTy);
            if (eTl < 0.001) return (new GraphicsPath(), new GraphicsPath());
        }
        float eUx = (float)(eTx / eTl), eUy = (float)(eTy / eTl), eNx = -eUy, eNy = eUx;
        float hSc = hs / 100.0f, hbO = ho - hs;

        PointF H(float hx, float hy)
        {
            float lx = hbO + hx * hSc, ly = (hy - 50.0f) * hSc;
            return new PointF(end.X + eUx * lx + eNx * ly, end.Y + eUy * lx + eNy * ly);
        }

        // 箭身：上边界 → 下边界（反向）
        var shaft = new GraphicsPath();
        shaft.StartFigure();
        shaft.AddLine(up[0].X, up[0].Y, up[0].X, up[0].Y); // 起点
        for (int i = 1; i < N; i++)
            shaft.AddLine(up[i - 1].X, up[i - 1].Y, up[i].X, up[i].Y);
        for (int i = N - 1; i >= 0; i--)
            shaft.AddLine(lp[i + 1 < N ? i + 1 : i].X, lp[i + 1 < N ? i + 1 : i].Y, lp[i].X, lp[i].Y);
        shaft.CloseFigure();

        // 箭头：与直线版完全相同的模板多边形
        var head = new GraphicsPath();
        if (style == ArrowAnnotationStyle.Sharp)
        {
            head.AddPolygon(new[] { H(20, 20), H(100, 50), H(20, 80) });
        }
        else
        {
            head.AddPolygon(new[] { H(50, 50), H(20, 20), H(100, 50), H(20, 80) });
        }

        return (shaft, head);
    }

    private static void AddQuadraticBezier(GraphicsPath path, PointF start, PointF control, PointF end)
    {
        PointF c1 = new PointF(
            start.X + (control.X - start.X) * 2.0f / 3.0f,
            start.Y + (control.Y - start.Y) * 2.0f / 3.0f);
        PointF c2 = new PointF(
            end.X + (control.X - end.X) * 2.0f / 3.0f,
            end.Y + (control.Y - end.Y) * 2.0f / 3.0f);
        path.AddBezier(start, c1, c2, end);
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.0f)
        {
            path.AddRectangle(rect);
            return path;
        }

        float d = Math.Min(Math.Min(rect.Width, rect.Height), radius * 2.0f);
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void RenderCounter(Graphics g, Rectangle selectedScreenRect, CounterShape counter)
    {
        RectangleF screenGlyph = counter.GlyphRect;
        RectangleF glyph = RectangleF.FromLTRB(
            screenGlyph.Left - selectedScreenRect.X,
            screenGlyph.Top - selectedScreenRect.Y,
            screenGlyph.Right - selectedScreenRect.X,
            screenGlyph.Bottom - selectedScreenRect.Y);
        if (glyph.Width <= 0 || glyph.Height <= 0) return;

        RenderCounterTail(g, selectedScreenRect, counter);

        // 外形三档共用圆角矩形路径：圆档半径＝半高，AddArc 四段拼出来就是正圆
        float corner = AnnotationRenderOptions.GetCounterGlyphCornerRadius(counter);
        using var glyphPath = CreateRoundedRectanglePath(glyph, corner);
        float outline = Math.Max(1.0f, counter.OutlineWidth);
        switch (counter.Fill)
        {
            case CounterFillMode.Solid:
                using (var fill = new SolidBrush(counter.FillColor))
                using (var white = new Pen(Color.White, outline))
                {
                    g.FillPath(fill, glyphPath);
                    g.DrawPath(white, glyphPath);
                }
                break;
            case CounterFillMode.Outline:
                using (var pen = new Pen(counter.FillColor, outline))
                    g.DrawPath(pen, glyphPath);
                break;
        }

        string fontName = AnnotationRenderOptions.GetCounterFontFamilyName(counter.FontFamily);
        var fontStyle = counter.FontBold ? FontStyle.Bold : FontStyle.Regular;
        using var numberFont = new Font(fontName, Math.Max(6.0f, counter.NumberFontSize), fontStyle, GraphicsUnit.Pixel);
        // ⚠ 这两个排版盒**不能**加 StringFormatFlags.LineLimit：下面的盒高正好取的就是行高（零余量），而 LineLimit 要
        // 求整行严格落在盒内，浮点取整一过就被判成"装不下"⇒ AddString 返回空路径，不报错也不画。
        // 现象＝成品/贴图/导出里编号与说明文字整颗消失（2026-09-19 实报）。NoWrap 已保证只有一行，LineLimit 在此无事可做。
        using var nearFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var centerFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        // 编号垂直居中于外形盒，并定下与预览同一条基线（预览侧用 FormattedText.Baseline，口径一致）
        float numberAscent = numberFont.Size * FontAscentRatio(numberFont);
        float numberLineHeight = numberFont.Size * FontLineSpacingRatio(numberFont);
        float baseline = glyph.Y + glyph.Height / 2.0f - numberLineHeight / 2.0f + numberAscent;
        DrawCounterText(g, counter.Label, numberFont, centerFormat,
            new RectangleF(glyph.X, baseline - numberAscent, glyph.Width, numberLineHeight),
            counter.TextColor, halo: counter.Fill != CounterFillMode.Solid);

        if (counter.Note.Length > 0)
        {
            using var noteFont = new Font(fontName, Math.Max(6.0f, counter.NoteFontSizeEffective), fontStyle, GraphicsUnit.Pixel);
            float noteAscent = noteFont.Size * FontAscentRatio(noteFont);
            float noteLineHeight = noteFont.Size * FontLineSpacingRatio(noteFont);
            // ⚠ 说明文字**不**跟编号共用字色：编号压在底色上，实心档给它白字是对的；说明文字排在外形盒外面、
            // 底下没有那层底色，白字再配白描边就成了浅底上一团糊白（2026-09-19 用户实报）。故它恒用标注色＋白 halo。
            DrawCounterText(g, counter.Note, noteFont, nearFormat,
                new RectangleF(glyph.Right + CounterShape.NoteGap, baseline - noteAscent,
                    AnnotationRenderOptions.GetCounterTextWidth(counter.Note, noteFont.Size) * 2.0f + 20.0f,
                    noteLineHeight),
                counter.FillColor, halo: true);
        }
    }

    /// <summary>
    /// 序号文字：先描一圈白色 halo 再填字，halo 粗细走 <see cref="AnnotationRenderOptions.GetCounterHaloThickness"/>，
    /// 与预览侧 CounterPainter.DrawText 同一算式（顺序也必须一致，否则成品比预览少一圈边）。
    /// </summary>
    private static void DrawCounterText(Graphics g, string text, Font font, StringFormat format,
        RectangleF layout, Color color, bool halo)
    {
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, layout, format);
        if (halo)
        {
            using var pen = new Pen(Color.White, (float)AnnotationRenderOptions.GetCounterHaloThickness(font.Size))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawPath(pen, path);
        }
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>字体的首行基线相对 em 高的比例（GDI+ 里没有现成的"基线"取值，只能由单元格高度换算）。</summary>
    private static float FontAscentRatio(Font font)
        => font.FontFamily.GetCellAscent(font.Style) / (float)font.FontFamily.GetEmHeight(font.Style);

    /// <summary>字体的行高相对 em 高的比例，用于把文字盒垂直居中。</summary>
    private static float FontLineSpacingRatio(Font font)
        => font.FontFamily.GetLineSpacing(font.Style) / (float)font.FontFamily.GetEmHeight(font.Style);

    /// <summary>
    /// 引线（含箭头）：与预览侧 CounterPainter.DrawTail 消费同一份 <see cref="ArrowHeadGeometry"/> 点列，
    /// 两侧共用 DrawArrowPart 那套"照点列填/描"，不再各写一遍头型。成品空间＝物理像素，故比例尺取 1。
    /// </summary>
    private static void RenderCounterTail(Graphics g, Rectangle selectedScreenRect, CounterShape counter)
    {
        PointF[] screenPath = AnnotationRenderOptions.GetCounterTailPoints(counter);
        if (screenPath.Length < 2) return;

        var spine = new PointF[screenPath.Length];
        for (int i = 0; i < screenPath.Length; i++)
            spine[i] = ToLocalPoint(screenPath[i], selectedScreenRect);

        foreach (ArrowPart part in ArrowHeadGeometry.Build(spine,
                     Math.Max(1.0f, counter.TailWidth), 1.0f, counter.TailHead))
            DrawArrowPart(g, counter.FillColor, part);
    }

    private static void ApplySpotlightShapes(Bitmap bitmap, Rectangle selectedScreenRect, IReadOnlyList<SpotlightShape> shapes)
    {
        if (bitmap == null || shapes == null || shapes.Count == 0) return;
        // FillRegion 不提供抗锯齿；原路径和合并规则不变，覆盖层改由 Skia 栅格化。
        SpotlightBitmapRenderer.Render(bitmap, shapes, (shape, inflate) =>
            CreateSpotlightLocalPath(shape, selectedScreenRect, bitmap.Width, bitmap.Height, inflate));
    }

    /// <param name="inflate">相对洞口边界的法向外扩量（负值为内缩），用于构造居中描边的外/内边界。</param>
    private static GraphicsPath? CreateSpotlightLocalPath(SpotlightShape shape, Rectangle selectedScreenRect, int bitmapWidth, int bitmapHeight, float inflate = 0.0f)
    {
        RectangleF local = RectangleF.FromLTRB(
            shape.Rect.Left - selectedScreenRect.X,
            shape.Rect.Top - selectedScreenRect.Y,
            shape.Rect.Right - selectedScreenRect.X,
            shape.Rect.Bottom - selectedScreenRect.Y);
        local.Intersect(new RectangleF(0, 0, bitmapWidth, bitmapHeight));
        if (local.Width <= Math.Abs(inflate) * 2 || local.Height <= Math.Abs(inflate) * 2) return null;

        local = RectangleF.Inflate(local, inflate, inflate);

        var path = new GraphicsPath();
        if (shape.ShapeKind == SpotlightShapeKind.Ellipse)
        {
            path.AddEllipse(local);
        }
        else
        {
            using GraphicsPath rounded = CreateRoundedRectanglePath(local, Math.Max(0.0f, shape.CornerRadius + inflate));
            path.AddPath(rounded, false);
        }
        return path;
    }

    private static void ApplyBlurShape(Bitmap bitmap, Rectangle selectedScreenRect, Rectangle virtualScreenRect, BlurShape shape)
    {
        Rectangle local = Rectangle.Round(RectangleF.FromLTRB(
            shape.Rect.Left - selectedScreenRect.X,
            shape.Rect.Top - selectedScreenRect.Y,
            shape.Rect.Right - selectedScreenRect.X,
            shape.Rect.Bottom - selectedScreenRect.Y));
        local.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (local.Width <= 0 || local.Height <= 0) return;
        if (shape.UiLevel <= 0) return;

        if (shape.Mode == BlurMode.Mosaic)
        {
            ApplyMosaicShape(bitmap, local, selectedScreenRect, virtualScreenRect, shape);
        }
        else
        {
            ApplyScaledBlurShape(bitmap, local, shape);
        }
    }

    /// <summary>
    /// 导出马赛克：块网格相位按虚拟屏幕原点对齐、块宽严格等于设定块长，
    /// 与截图内预览同一套切块口径（预览按整屏网格切块后再裁剪到形状矩形）。
    /// </summary>
    private static void ApplyMosaicShape(Bitmap bitmap, Rectangle local, Rectangle selectedScreenRect, Rectangle virtualScreenRect, BlurShape shape)
    {
        int block = Math.Max(2, shape.MosaicBlockSize);
        // 整屏网格原点换算到成品图（选区）坐标系，多屏时可为负值
        int gridX = virtualScreenRect.X - selectedScreenRect.X;
        int gridY = virtualScreenRect.Y - selectedScreenRect.Y;
        Rectangle bitmapRect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

        Rectangle gridRect = Rectangle.FromLTRB(
            gridX + (int)Math.Floor((local.X - gridX) / (double)block) * block,
            gridY + (int)Math.Floor((local.Y - gridY) / (double)block) * block,
            gridX + (int)Math.Ceiling((local.Right - gridX) / (double)block) * block,
            gridY + (int)Math.Ceiling((local.Bottom - gridY) / (double)block) * block);
        gridRect.Intersect(bitmapRect);
        if (gridRect.Width <= 0 || gridRect.Height <= 0) return;

        int cellsX = Math.Max(1, (int)Math.Ceiling(gridRect.Width / (double)block));
        int cellsY = Math.Max(1, (int)Math.Ceiling(gridRect.Height / (double)block));
        Rectangle dest = new Rectangle(gridRect.X, gridRect.Y, cellsX * block, cellsY * block);

        using Bitmap source = bitmap.Clone(gridRect, PixelFormat.Format32bppArgb);
        using Bitmap down = new Bitmap(cellsX, cellsY, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(down))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(source, new Rectangle(0, 0, cellsX, cellsY));
        }

        using Graphics target = Graphics.FromImage(bitmap);
        target.CompositingQuality = CompositingQuality.HighSpeed;
        target.InterpolationMode = InterpolationMode.NearestNeighbor;
        target.SmoothingMode = SmoothingMode.None;
        target.PixelOffsetMode = PixelOffsetMode.Half;
        target.SetClip(local, CombineMode.Replace);
        target.DrawImage(down, dest, new Rectangle(0, 0, cellsX, cellsY), GraphicsUnit.Pixel);
    }

    private static void ApplyScaledBlurShape(Bitmap bitmap, Rectangle local, BlurShape shape)
    {
        GaussianBlurHelper.ApplyGaussianBlur(bitmap, local, shape.Intensity);
    }

    private static PointF ToLocalPoint(PointF screenPoint, Rectangle selectedScreenRect)
    {
        return new PointF(
            screenPoint.X - selectedScreenRect.X,
            screenPoint.Y - selectedScreenRect.Y);
    }

    // ==========================================
    // 放大镜功能
    // ==========================================
    private Avalonia.Media.Imaging.WriteableBitmap? _magnifierBitmap;
    private long _magnifierLastUpdateTick = 0;
    private string? _magnifierLastColorStr;
    private bool _magnifierUseHexFormat = true;
    private byte _magnifierLastR = 0;
    private byte _magnifierLastG = 0;
    private byte _magnifierLastB = 0;
    private byte[]? _magnifierPixelBuffer;
    private bool _magnifierCrossUseBlack;

    private void HideMagnifier(CaptureWindow window)
    {
        if (window.MagnifierHost != null && window.MagnifierHost.IsVisible)
        {
            window.MagnifierHost.IsVisible = false;
        }
    }

    private void UpdateMagnifier(CaptureWindow window, Avalonia.Point cursorCanvasPt, CapturedFrame frame)
    {
        // 即使从拖动／其他更新入口到达，也不能在工具栏上重新显出取色器。
        if (window.IsOverlayPoint(cursorCanvasPt)) { HideMagnifier(window); return; }
        var host = window.MagnifierHost;
        if (host == null || window.MagnifierImage == null) return;
        if (frame.FullScreen == null) return;

        double hostW = 152;
        double hostH = 152;

        double x = cursorCanvasPt.X + 10;
        double y = cursorCanvasPt.Y + 10;

        double cw = window.OverlayCanvas.Bounds.Width;
        double ch = window.OverlayCanvas.Bounds.Height;

        if (cw > 0 && ch > 0)
        {
            if (x + hostW > cw) x = cursorCanvasPt.X - hostW - 10;
            if (y + hostH > ch) y = cursorCanvasPt.Y - hostH - 10;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
        }

        Avalonia.Controls.Canvas.SetLeft(host, x);
        Avalonia.Controls.Canvas.SetTop(host, y);

        if (!host.IsVisible) host.IsVisible = true;

        long now = Environment.TickCount64;
        long dt = unchecked(now - _magnifierLastUpdateTick);
        if (_magnifierLastUpdateTick != 0 && dt >= 0 && dt < 15) return;
        _magnifierLastUpdateTick = now;

        var screenPt = window.PointToScreen(cursorCanvasPt);
        int sx = screenPt.X;
        int sy = screenPt.Y;

        int sizePx = 17;
        int half = sizePx / 2;
        
        int l = sx - half - frame.VirtualScreenRect.X;
        int t = sy - half - frame.VirtualScreenRect.Y;

        if (l < 0) l = 0;
        if (t < 0) t = 0;
        if (l + sizePx > frame.FullScreen.Width) l = frame.FullScreen.Width - sizePx;
        if (t + sizePx > frame.FullScreen.Height) t = frame.FullScreen.Height - sizePx;

        if (l < 0 || t < 0 || l + sizePx > frame.FullScreen.Width || t + sizePx > frame.FullScreen.Height) return;

        if (_magnifierBitmap == null || _magnifierBitmap.PixelSize.Width != sizePx)
        {
            _magnifierBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                new Avalonia.PixelSize(sizePx, sizePx),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);
            window.MagnifierImage.Source = _magnifierBitmap;
            _magnifierPixelBuffer = new byte[sizePx * sizePx * 4];
        }

        try
        {
            var rect = new System.Drawing.Rectangle(l, t, sizePx, sizePx);
            var bmpData = frame.FullScreen.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = bmpData.Stride;
                IntPtr scan0 = bmpData.Scan0;

                for (int i = 0; i < sizePx; i++)
                {
                    IntPtr srcRow = IntPtr.Add(scan0, i * stride);
                    System.Runtime.InteropServices.Marshal.Copy(srcRow, _magnifierPixelBuffer!, i * sizePx * 4, sizePx * 4);
                }
            }
            finally
            {
                frame.FullScreen.UnlockBits(bmpData);
            }

            using (var fb = _magnifierBitmap.Lock())
            {
                System.Runtime.InteropServices.Marshal.Copy(_magnifierPixelBuffer!, 0, fb.Address, _magnifierPixelBuffer!.Length);
            }

            int centerIndex = (half * sizePx * 4) + (half * 4);
            byte bb = _magnifierPixelBuffer[centerIndex];
            byte gg = _magnifierPixelBuffer[centerIndex + 1];
            byte rr = _magnifierPixelBuffer[centerIndex + 2];

            _magnifierLastR = rr;
            _magnifierLastG = gg;
            _magnifierLastB = bb;

            string colorText = _magnifierUseHexFormat ? 
                string.Format("#{0:X2}{1:X2}{2:X2}", rr, gg, bb) :
                string.Format("{0}, {1}, {2}", rr, gg, bb);
            Avalonia.Media.Color color = Avalonia.Media.Color.FromRgb(rr, gg, bb);

            int lum = (rr * 2126 + gg * 7152 + bb * 722) / 10000;
            bool useBlack = lum >= 200;
            if (useBlack != _magnifierCrossUseBlack)
            {
                Avalonia.Media.IBrush crossBrush = useBlack
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3A, 0x3A, 0x3A))
                    : Avalonia.Media.Brushes.White;
                window.MagnifierCrossLineH.Stroke = crossBrush;
                window.MagnifierCrossLineV.Stroke = crossBrush;
                _magnifierCrossUseBlack = useBlack;
            }

            window.MagnifierCoordText.Text = string.Format("X:{0}  Y:{1}", sx, sy);
            window.MagnifierColorText.Text = colorText;
            window.MagnifierColorSwatch.Fill = new Avalonia.Media.SolidColorBrush(color);
            _magnifierLastColorStr = colorText;
        }
        catch { }
    }

    private sealed class CaptureSnapshotHistory
    {
        private readonly AnnotationDocument _annotations;
        private readonly CaptureWindow _window;
        private readonly int _maxSnapshots;
        private readonly Stack<CaptureSnapshot> _undo = new Stack<CaptureSnapshot>();
        private readonly Stack<CaptureSnapshot> _redo = new Stack<CaptureSnapshot>();
        private CaptureSnapshot[]? _redoBeforeLatestCommit;

        public CaptureSnapshotHistory(AnnotationDocument annotations, CaptureWindow window, int maxSnapshots = 100)
        {
            _annotations = annotations;
            _window = window;
            _maxSnapshots = Math.Max(1, maxSnapshots);
        }

        public void CommitSnapshot()
        {
            PushUndo(CreateSnapshot());
            _redoBeforeLatestCommit = _redo.ToArray();
            _redo.Clear();
            NotifyState();
        }

        public void DiscardLatestSnapshot()
        {
            if (_undo.Count > 0) _undo.Pop();
            if (_redoBeforeLatestCommit != null)
            {
                _redo.Clear();
                for (int i = _redoBeforeLatestCommit.Length - 1; i >= 0; i--)
                    _redo.Push(_redoBeforeLatestCommit[i]);
                _redoBeforeLatestCommit = null;
            }
            NotifyState();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redoBeforeLatestCommit = null;
            _redo.Push(CreateSnapshot());
            RestoreSnapshot(_undo.Pop());
            NotifyState();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _redoBeforeLatestCommit = null;
            _undo.Push(CreateSnapshot());
            RestoreSnapshot(_redo.Pop());
            NotifyState();
            return true;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _redoBeforeLatestCommit = null;
            NotifyState();
        }

        private void NotifyState()
        {
            _window.SetUndoEnabled(_undo.Count > 0);
        }

        private CaptureSnapshot CreateSnapshot()
        {
            return new CaptureSnapshot(_annotations.CreateSnapshot(), _window.CreateTextSnapshot());
        }

        private void RestoreSnapshot(CaptureSnapshot snapshot)
        {
            _annotations.ReplaceAll(snapshot.Shapes);
            _window.RestoreTextSnapshot(snapshot.Texts);
            _window.ApplyAnnotationErasureMasks(_annotations);
        }

        private void PushUndo(CaptureSnapshot snapshot)
        {
            _undo.Push(snapshot);
            if (_undo.Count <= _maxSnapshots) return;
            var items = _undo.Reverse().Skip(1).ToList();
            _undo.Clear();
            foreach (var item in items) _undo.Push(item);
        }
    }

    private sealed class CaptureSnapshot
    {
        public CaptureSnapshot(IReadOnlyList<AnnotationShape> shapes, IReadOnlyList<TextLayerSnapshot> texts)
        {
            Shapes = shapes;
            Texts = texts;
        }

        public IReadOnlyList<AnnotationShape> Shapes { get; }
        public IReadOnlyList<TextLayerSnapshot> Texts { get; }
    }

    private static string ResolveFileName(AppSettings settings)
    {
        string template = string.IsNullOrWhiteSpace(settings.FileNameTemplate)
            ? "Screenshot_{yyyyMMdd_HHmmss}"
            : settings.FileNameTemplate;

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = template.Replace("{yyyyMMdd_HHmmss}", stamp);

        // 截图统一保存为 PNG（保留元数据），扩展名固定。
        return baseName + ".png";
    }

    /// <summary>
    /// 解析截图保存路径。配置的保存目录不可写时回退到"我的图片"或临时目录，
    /// 避免跨机器同步配置后路径失效导致保存崩溃。
    /// </summary>
    private string ResolveSavePath()
    {
        return ResolveActionSavePath(_settings, ".png");
    }

    /// <summary>截图与组合动作共用保存目录回退及文件名模板。</summary>
    internal static string ResolveActionSavePath(AppSettings settings, string extension)
    {
        string fileName = Path.ChangeExtension(ResolveFileName(settings), extension);
        string? dir = settings.SaveDirectory;
        if (!IsDirectoryAccessible(dir))
        {
            AppLogger.Warn("CaptureSession: 保存目录不可写，回退到我的图片。原目录=" + (dir ?? "(null)"));
            dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(dir) || !IsDirectoryAccessible(dir))
            {
                dir = Path.GetTempPath();
            }
        }
        return Path.Combine(dir, fileName);
    }

    private static bool IsDirectoryAccessible(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try { return Directory.Exists(dir); }
        catch { return false; }
    }
}
