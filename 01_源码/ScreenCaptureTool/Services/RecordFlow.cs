using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Recording;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>一次录制流程的落点。</summary>
public enum RecordFlowOutcome
{
    /// <summary>正常停止，母带 mp4 已合并落盘（路径见 <see cref="RecordFlow.LastOutputPath"/>）。</summary>
    Completed,
    /// <summary>用户放弃（倒计时取消、或「重新录制」把本段作废），产物已丢弃。</summary>
    Abandoned,
    /// <summary>引擎失败 / 录制中途崩溃。</summary>
    Failed,
}

/// <summary>
/// 录屏会话编排（计划 §4.2）：覆盖层倒计时 → 启动 <see cref="RecordingSession"/> →
/// 录制期临时全局键（默认 F10 暂停 / F11 完成 / F8 重录，2026-09-29 起读「设置 → 快捷键 → 录屏」的
/// record 组，随录随卸）+ 低级键鼠钩子（Ctrl+Del 结束、Esc 兜底、键帽/波纹喂料）→
/// 停止/失败收尾。2026-09-21 第（七）轮：控制条上的「放弃」出口整体删除，要留要弃都从「结束录制」走，
/// 落不落盘由录完后的预览贴图窗决定；同轮加上「重新录制」（本段作废 → 重跑一次开录倒计时）。
/// </summary>
public static class RecordFlow
{
    // 临时热键 ID 独立小ID段（避开截图 0x5343 / 贴图 0x5344 / 动作体系），随录随卸。
    // 键位不写在这里了（2026-09-29）：那三枚改读 BuiltinShortcutsStore 的 record 组，
    // 下面两枚 Vk* 仍是低级钩子里的兜底判据，属"锁死项"，见 BuiltinShortcutsStore.LockedIds。
    private const int HotKeyIdStop = 0x9101;
    private const int HotKeyIdPause = 0x9102;
    private const int HotKeyIdRestart = 0x9103;
    private const uint VkDelete = 0x2E;
    private const uint VkEscape = 0x1B;
    private const int WM_KEYUP = 0x0101;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;

    private static volatile int _mouseButtonsDownMask;

    private static readonly object Gate = new();

    private static IRecordingSession? _session;
    private static RecordOverlayWindow? _overlay;
    private static TaskCompletionSource<RecordFlowOutcome>? _finishTcs;
    private static HotKeyManager? _hkStop;
    private static HotKeyManager? _hkPause;
    private static HotKeyManager? _hkRestart;
    private static IntPtr _kbHook;
    private static IntPtr _msHook;
    // 钩子委托必须被静态字段钉住，否则被 GC 回收后回调地址失效（sticker 钩子同款教训）
    private static NativeMethods.LowLevelKeyboardProc? _kbProc;
    private static NativeMethods.LowLevelMouseProc? _msProc;

    private static bool _countdownPhase;
    private static bool _rippleOn;
    private static bool _keycapOn;
    private static RecordFormat _format = RecordFormat.Mp4;
    private static double _dpiScale = 1.0;
    private static readonly List<string> _degradeDetails = new();
    private static readonly HashSet<uint> _downKeys = new();

    // ===== 标注画板的钩子侧缓存（第10份 F1）=====
    // 低级钩子回调里绝不读可视树、绝不换算坐标（与「宁可丢一次特效也不能挂系统输入链」同一条纪律），
    // 故把"选区＝画板"这段矩形预先摊成整数四边，回调里只剩一次比较。
    // 开关刻意最后写：volatile 写自带 release 语义，保证钩子读到 true 时四个边一定已经就位。
    private static volatile bool _annoBoardOn;
    private static int _annoBoardLeft;
    private static int _annoBoardTop;
    private static int _annoBoardRight;
    private static int _annoBoardBottom;
    /// <summary>打字期静音键帽：文本标注落字时每个字母都会弹一枚胶囊，那些正落在选区里＝全被录进成品。
    /// 由覆盖层经 <c>AnnotationTypingChanged</c> 推过来（UI 线程写、钩子读），钩子侧不读可视树。
    /// 只静音显示：<c>_downKeys</c> 照常记账，那一份是 Ctrl+Delete 结束录制的判据。</summary>
    private static volatile bool _keycapMuted;
    /// <summary>降级④：锁屏监听（仅录制会话期存在，TeardownInfra 注销）。</summary>
    private static SessionNotification? _sessionWatch;

    /// <summary>倒计时期保存参数，倒计时结束才真正 Start。</summary>
    private static RecordingOptions? _pendingOpt;

    public static bool IsBusy { get; private set; }
    public static bool IsRecording { get; private set; }
    public static bool IsPaused { get; private set; }
    public static string? LastOutputPath { get; private set; }

    /// <summary>
    /// 由 CaptureSession 的「开始录制」入口调用（UI 线程）。三种回调都在 UI 线程回来：
    /// <paramref name="onRecordingStarted"/> 此刻遮罩已可安全退场；
    /// <paramref name="onCountdownCancelled"/> 与 <paramref name="onStartFailed"/> 表示要回到截图界面。
    /// <paramref name="format"/> 是参数条冻结的导出格式，录制完成后进贴图窗供转码（M3）。
    /// <para>键鼠特效不在此传参：勾选住在参数条（录制前）与控制条（录制中）两处。2026-09-24 起参数条那侧
    /// 选择即落盘、落的是<b>上次使用</b>族（<c>RecLastVisKeycap</c> / <c>RecLastVisRipple</c>），设置页那侧才是
    /// <b>默认值</b>族，故本类在起跑这一刻经 <see cref="AppSettings.Pick(bool?,bool)"/> 读一次当钩子初值
    /// （解析口径与参数条 <c>ApplyDefaults</c> 完全同一份，两边不会各算一遍）；
    /// 之后由控制条经 <see cref="RecordOverlayWindow.FxChanged"/> 即时翻开关，并同步经
    /// <see cref="RememberFxChoice"/> 记回上次使用族（2026-09-26 起录制中的勾选也留得住；默认族始终不动）。</para>
    /// </summary>
    public static void Begin(
        RecordingOptions opt,
        RecordFormat format,
        double dpiScale,
        Action onRecordingStarted,
        Action onCountdownCancelled,
        Action<string> onStartFailed)
    {
        lock (Gate)
        {
            if (IsBusy) return;
            IsBusy = true;
        }

        if (opt.Engine == RecordEngine.Ffmpeg) EnsureFfmpegGuidance();

        _pendingOpt = opt;
        // 🟠 涟漪必须带光标：设置页那条三档分段（关 / 光标 / 光标+涟漪）已保证不会出现"涟漪开、光标关"，
        //    但参数条那两枚勾选是独立布尔，理论上能勾出这个组合。这里再兜一道——涟漪的视觉是画在
        //    光标位置上的圈，没有光标时那圈会跟着一个看不见的点乱闪，比不开还糟。
        _rippleOn = AppSettings.Pick(AppSettings.Current.RecLastVisRipple, AppSettings.Current.RecVisRipple) && opt.DrawCursor;
        _keycapOn = AppSettings.Pick(AppSettings.Current.RecLastVisKeycap, AppSettings.Current.RecVisKeycap);
        _format = format;
        _dpiScale = dpiScale > 0 ? dpiScale : 1.0;
        _countdownPhase = true;
        _stopRequested = false;
        _degradeDetails.Clear();
        _downKeys.Clear();
        _annoBoardOn = false;
        _keycapMuted = false;
        LastOutputPath = null;
        _finishTcs = new TaskCompletionSource<RecordFlowOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 三条回调存成字段：重录之后没有"截图界面"可回了，RestartRecording 会把三条一并置空
        // （置空后的失败出口改弹钉黑 toast，见 FailOut）
        _onStarted = onRecordingStarted;
        _onCancelled = onCountdownCancelled;
        _onFailed = onStartFailed;

        var virt = MonitorHelper.GetVirtualScreenRect();
        // 上一段录制的预览卡还开着的话，那扇遮罩窗仍然活着（2026-09-22 第三批起它兼任预览态暗纱，随预览窗退场）。
        // 这里不先收，_overlay 一被覆写就再没人负责——旧的那层会一直挂到那张卡被关，新录制还叠在它下面。
        if (_overlay != null) CloseOverlay();
        _overlay = new RecordOverlayWindow();
        _overlay.Setup(opt.Region, virt, dpiScale > 0 ? dpiScale : 1.0);
        _overlay.CountdownFinished += (_, _) => StartSession();
        _overlay.CountdownCancelRequested += (_, _) => CancelBeforeStart();
        _overlay.PauseToggleRequested += (_, _) => TogglePause();
        _overlay.CompleteRequested += (_, _) => StopForComplete();
        _overlay.RestartRequested += (_, _) => RestartRecording();
        _overlay.FxChanged += (keycap, ripple) => SetFxState(keycap, ripple);
        _overlay.AnnotationModeChanged += SyncAnnotationBoardRect;
        _overlay.AnnotationTypingChanged += typing => _keycapMuted = typing;
        _overlay.Show();

        InstallKeyboardHook();
        if (_rippleOn) InstallMouseHook();

        RunCountdownOrStart();

        AppLogger.Info("RecordFlow: 会话启动。Region=" + opt.Region + " Preset=" + opt.EffectivePresetId
            + " Audio=" + opt.Audio + " Countdown=" + opt.CountdownSec);
    }

    private static Action? _onStarted;
    private static Action? _onCancelled;
    private static Action<string>? _onFailed;

    /// <summary>「立即」档不进倒计时，直接起跑（延后一拍让覆盖层先完成首帧布局）。</summary>
    private static void RunCountdownOrStart()
    {
        int sec = _pendingOpt?.CountdownSec ?? 0;
        if (sec <= 0) DispatcherTimer.RunOnce(StartSession, TimeSpan.FromMilliseconds(60));
        else _overlay?.StartCountdown(sec);
    }

    /// <summary>RunAsync 的 record 分支等待整段录制落定。</summary>
    public static Task<RecordFlowOutcome> WaitFinishAsync()
        => _finishTcs?.Task ?? Task.FromResult(RecordFlowOutcome.Failed);

    /// <summary>F9 再按 / Ctrl+Del：录制中＝结束录制（与 F11 同义）；倒计时中＝取消倒计时。
    /// 「放弃」这一档已按用户要求删除——要留要弃都先结束录制，在预览贴图窗里决定。</summary>
    public static void RequestComplete()
    {
        if (!IsBusy) return;
        if (_countdownPhase)
        {
            Dispatcher.UIThread.Post(() => _overlay?.RequestCountdownCancel());
            return;
        }
        Dispatcher.UIThread.Post(StopForComplete);
    }

    // ================= 起跑 =================

    /// <summary>
    /// 起跑前 FFmpeg 缺失（纯读缓存，不阻塞倒计时）：居中开那一枚共用进度卡并就地排队下载
    /// （设计稿 05 节）。旧的首次引导窗与钉黑提示条一并退场——失败态与「去设置」都由那张卡自己给。
    /// 下载不占本次会话：本次仍按既有失败路径退出，下完后重新框一次就能开录。
    /// </summary>
    private static void EnsureFfmpegGuidance()
    {
        try
        {
            if (Core.Deps.DependencyManager.IsReady(Core.Deps.DepIds.Ffmpeg)) return;
            DependencyGate.KickOff(Core.Deps.DepIds.Ffmpeg);
            AppLogger.Info("RecordFlow: FFmpeg 缺失，已开运行组件进度卡就地下载");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RecordFlow: FFmpeg 组件卡唤起失败（不阻断录制）：" + ex.Message);
        }
    }

    private static void StartSession()
    {
        if (!_countdownPhase) return; // 立即档与倒计时环只会触发其一，双保险
        RecordingOptions? opt = _pendingOpt;
        if (opt == null) { CancelBeforeStart(); return; }

        try
        {
            // 🔴 提前创建并显示控制条（issue #47 修复）：录制会话启动时会探测 DDA 输出，
            // 每个输出最多耗时 3 秒，导致控制条延迟显示。将控制条创建提前到会话启动之前，
            // 让用户立即看到控制条（处于等待状态），避免"启动后3秒才出现"的感知延迟。
            _overlay?.EnsureControlBarCreated();

            var session = RecordingSessionFactory.Create(opt);
            session.ProgressChanged += t => Dispatcher.UIThread.Post(() => _overlay?.SetElapsed(t));
            session.PauseStateChanged += paused => Dispatcher.UIThread.Post(() =>
            {
                IsPaused = paused;
                _overlay?.SetPaused(paused);
            });
            session.Degraded += (reason, detail) => Dispatcher.UIThread.Post(() =>
            {
                string line = reason + "：" + (detail ?? "");
                _degradeDetails.Add(line);
                _overlay?.NotifyDegrade(_degradeDetails.Count == 1
                    ? line
                    : CombineDegradeText());
            });
            session.Completed += (mp4, dur) => Dispatcher.UIThread.Post(() => OnCompleted(mp4, dur));
            session.Failed += msg => Dispatcher.UIThread.Post(() => OnFailed(msg));

            if (!session.Start(out string? err))
            {
                try { session.Abort(); } catch { }
                try { session.Dispose(); } catch { }
                _countdownPhase = false;
                CloseOverlay();
                TeardownInfra();
                IsBusy = false;
                string error = err ?? "录制引擎启动失败";
                AppLogger.Warn("RecordFlow: 启动失败：" + error);
                if (error.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    error += "\n未检测到 FFmpeg，可在「设置 → 运行组件」中下载安装。";
                }
                _finishTcs?.TrySetResult(RecordFlowOutcome.Failed);
                FailOut(error);
                return;
            }

            _session = session;
            _countdownPhase = false;
            IsRecording = true;
            _stopRequested = false;
            _overlay?.EnterRecording(AppSettings.Current.RecShowBadge, _keycapOn, _rippleOn);
            RegisterPauseStopHotkeys();
            StartSessionWatch();
            NotifyTrayRecording(true);
            AppLogger.Info("RecordFlow: 录制已开始。Master=" + session.MasterMp4Path);
            _onStarted?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error("RecordFlow: 启动录制异常", ex);
            CancelBeforeStart();
            FailOut("录制启动异常：" + ex.Message);
        }
    }

    /// <summary>起跑失败出口：首段还有截图界面可回（回调里带弹窗），重录之后只剩一条钉黑 toast。</summary>
    private static void FailOut(string error)
    {
        if (_onFailed != null) _onFailed(error);
        else StickerToastWindow.Show("录屏未能重新起跑：" + error);
    }

    private static void CancelBeforeStart()
    {
        if (!_countdownPhase) return;
        _countdownPhase = false;
        CloseOverlay();
        TeardownInfra();
        IsBusy = false;
        _finishTcs?.TrySetResult(RecordFlowOutcome.Abandoned);
        AppLogger.Info("RecordFlow: 倒计时取消，回到截图界面");
        _onCancelled?.Invoke();
    }

    /// <summary>
    /// 「重新录制」：本段已录全部作废（杀 ffmpeg ＋ 删临时段）→ 时长归零 → 退出标注画板 →
    /// 重跑一次开录倒计时（沿用参数条那一档延迟，选「无延迟」即直接开录）。
    /// <para>⚠ 三条回调一并置空：首段那份 <c>onCountdownCancelled</c>／<c>onStartFailed</c> 里带着
    /// "把 CaptureWindow 重新 Show 出来"，而录制一开始那张窗就 Close 了，重跑时再 Show 会把截图遮罩
    /// 复活在桌面上。改判为只弹钉黑 toast（与 <see cref="OnFailed"/> 同一出口）。</para>
    /// </summary>
    private static void RestartRecording()
    {
        IRecordingSession? s = _session;
        if (s == null || !IsRecording || _stopRequested) return;
        _session = null;
        try { s.Abort(); } catch { }
        try { s.Dispose(); } catch { }

        IsRecording = false;
        IsPaused = false;
        _countdownPhase = true;
        _degradeDetails.Clear();
        _downKeys.Clear();
        _onStarted = null;
        _onCancelled = null;
        _onFailed = null;

        // 只摘"这一段"的基建：低级键鼠钩子留着（重录期间还要靠它接 F9/Esc/Ctrl+Del），
        // 热键与锁屏监听、托盘录制态随本段结束一并卸掉，下一次起跑再注册。
        UnregisterPauseStopHotkeys();
        StopSessionWatch();
        NotifyTrayRecording(false);

        _overlay?.PrepareForRestart();
        RunCountdownOrStart();
        AppLogger.Info("RecordFlow: 已作废本段并重跑开录倒计时。Countdown="
            + (_pendingOpt?.CountdownSec ?? 0));
    }

    // ================= 键鼠特效开关（控制条那两枚勾选即时生效） =================

    /// <summary>翻键盘钩子的喂料开关、按需装卸鼠标钩子（波纹那一路）。两枚都是会话内状态，本方法不落设置。</summary>
    public static void SetFxState(bool keycap, bool ripple)
    {
        _keycapOn = keycap;
        _rippleOn = ripple;
        if (ripple) InstallMouseHook();
        else
        {
            UninstallMouseHook();
            _overlay?.ClearMouseEffects();
        }
        if (!keycap)
        {
            _downKeys.Clear();
            Dispatcher.UIThread.Post(() => _overlay?.ClearKeyEffects());
        }
    }

    /// <summary>
    /// 录制中在控制条上勾的两枚，一并记进 <c>RecLastVis*</c>「上次使用」族——<b>不碰</b>设置页的默认族
    /// <c>RecVis*</c>（口径见 AppSettings 那族的分层判据：条／窗那侧只写上次的族）。
    /// 与预录制参数条用的是同一对键，故下次开条就直接回放到这两枚上。
    /// 只由控制条"用户真点了"的那条分支调用；开窗回放初值那一下不写（见 RecordControlBarWindow.SetFxInitial）。
    /// </summary>
    public static void RememberFxChoice(bool keycap, bool ripple)
    {
        var s = AppSettings.Current;
        if (s.RecLastVisKeycap == keycap && s.RecLastVisRipple == ripple) return;
        s.RecLastVisKeycap = keycap;
        s.RecLastVisRipple = ripple;
        s.Save();
        AppLogger.Info($"RecordFlow: 键鼠特效记为上次使用 键帽={keycap} 涟漪={ripple}");
    }

    // ================= 热键与钩子 =================

    private static void RegisterPauseStopHotkeys()
    {
        if (_hkStop != null || _hkPause != null || _hkRestart != null) return; // 重录第二次起跑：上一段已摘干净才算没注册过
        try
        {
            // 键位从 shortcuts.ini 的 record 组读（2026-09-29）：这三枚原先写死在本文件里，
            // 设置页连"录屏"一栏都没有。目录默认值与原实现逐字相同 ⇒ 没改过键的用户行为零变化。
            var map = BuiltinShortcutsStore.LoadGroupMap(BuiltinShortcutsStore.GroupRecord);
            _hkStop = RegisterRecordKey(map, "record.stop", HotKeyIdStop, () => Dispatcher.UIThread.Post(StopForComplete));
            _hkPause = RegisterRecordKey(map, "record.pause", HotKeyIdPause, () => Dispatcher.UIThread.Post(TogglePause));
            _hkRestart = RegisterRecordKey(map, "record.restart", HotKeyIdRestart, () => Dispatcher.UIThread.Post(RestartRecording));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RecordFlow: 临时热键注册失败（不阻断录制）：" + ex.Message);
            UnregisterPauseStopHotkeys();   // 只注册成功一半时把那一半也摘掉，免得留着占键
        }
    }

    /// <summary>
    /// 按目录绑定注册一枚录制期临时全局键。注册失败/键码换算不出来都返回 <c>null</c> 而不抛：
    /// 少一枚快捷键不该阻断录制，用户仍可用控制条上的同一枚按钮（Ctrl+Del／Esc 那两条兜底键走钩子，不经这里）。
    /// </summary>
    private static HotKeyManager? RegisterRecordKey(
        Dictionary<string, BuiltinShortcutsStore.BuiltinShortcut> map, string id, int hotKeyId, Action callback)
    {
        if (!map.TryGetValue(id, out BuiltinShortcutsStore.BuiltinShortcut? sc)) return null;

        uint vk = BuiltinShortcutsStore.VirtualKeyOf(sc.Key);
        if (vk == 0)
        {
            AppLogger.Warn("RecordFlow: 「" + sc.Name + "」绑的键换算不出 Win32 键码，本次不注册");
            return null;
        }

        var mgr = new HotKeyManager(callback, hotKeyId);
        if (mgr.Register(BuiltinShortcutsStore.WinModifiersOf(sc.Modifiers), vk))
        {
            AppLogger.Info("RecordFlow: 已注册录制期热键「" + sc.Name + "」= " + BuiltinShortcutsStore.Format(sc));
            return mgr;
        }
        AppLogger.Warn("RecordFlow: 「" + sc.Name + "」热键注册失败（已被系统或其他软件占用），错误码: " + mgr.LastRegisterError);
        mgr.Dispose();
        return null;
    }

    /// <summary>只摘那三枚临时全局键（重录换段时用；低级钩子不在此列，见 <see cref="TeardownInfra"/>）。</summary>
    private static void UnregisterPauseStopHotkeys()
    {
        try { _hkStop?.Dispose(); } catch { }
        _hkStop = null;
        try { _hkPause?.Dispose(); } catch { }
        _hkPause = null;
        try { _hkRestart?.Dispose(); } catch { }
        _hkRestart = null;
    }

    private static void InstallKeyboardHook()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProc = KeyboardHookCallback;
        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, hMod, 0);
    }

    private static void InstallMouseHook()
    {
        if (_msHook != IntPtr.Zero) return;
        _msProc = MouseHookCallback;
        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _msHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _msProc, hMod, 0);
    }

    private static void UninstallMouseHook()
    {
        _mouseButtonsDownMask = 0;
        if (_msHook == IntPtr.Zero) return;
        try { NativeMethods.UnhookWindowsHookEx(_msHook); } catch { }
        _msHook = IntPtr.Zero;
    }

    // 钩子回调只投递不重活（WH_KEYBOARD_LL 回调超时会被系统静默摘钩，与 HotKeyManager 同款纪律）
    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                bool isDown = (long)wParam == NativeMethods.WM_KEYDOWN || (long)wParam == NativeMethods.WM_SYSKEYDOWN;
                bool isUp = (long)wParam == WM_KEYUP;

                if (isDown && kb.vkCode == VkDelete && IsCtrlDown())
                {
                    Dispatcher.UIThread.Post(RequestComplete);
                }
                if (isDown && kb.vkCode == VkEscape)
                {
                    // 只在既有分支里加一档（D4）：不新增全局吞键，Esc 照常放行给底下的应用
                    if (_countdownPhase) Dispatcher.UIThread.Post(() => _overlay?.RequestCountdownCancel());
                    else if (_annoBoardOn) Dispatcher.UIThread.Post(() => _overlay?.ExitAnnotationMode());
                }

                if (_keycapOn && IsRecording)
                {
                    if (isDown)
                    {
                        _downKeys.Add(kb.vkCode);
                        string label = BuildKeyDisplayText(kb.vkCode);
                        if (!string.IsNullOrEmpty(label) && !_keycapMuted)
                            Dispatcher.UIThread.Post(() => _overlay?.ShowKeycap(label));
                    }
                    else if (isUp)
                    {
                        _downKeys.Remove(kb.vkCode);
                    }
                }
                else if (isUp)
                {
                    _downKeys.Remove(kb.vkCode);
                }
            }
        }
        catch
        {
            // 钩子内绝不抛出、绝不记日志：宁可丢一次特效也不能挂系统输入链
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && IsRecording && _rippleOn)
            {
                long msg = (long)wParam;
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_LBUTTONDBLCLK
                    or WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP
                    or WM_MOUSEMOVE or WM_MOUSEWHEEL)
                {
                    var ms = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    // 画板内的按下＝在作画，起波纹会在画面正中"一笔一圈"还被录进成品（F1）
                    if (!IsPointOnAnnotationBoard(ms.pt.X, ms.pt.Y))
                    {
                        var pt = new Avalonia.Point(ms.pt.X, ms.pt.Y);

                        if (msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
                        {
                            _mouseButtonsDownMask |= 1;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseDown(pt, RecordMouseButton.Left));
                        }
                        else if (msg == WM_RBUTTONDOWN)
                        {
                            _mouseButtonsDownMask |= 2;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseDown(pt, RecordMouseButton.Right));
                        }
                        else if (msg == WM_MBUTTONDOWN)
                        {
                            _mouseButtonsDownMask |= 4;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseDown(pt, RecordMouseButton.Middle));
                        }
                        else if (msg == WM_LBUTTONUP)
                        {
                            _mouseButtonsDownMask &= ~1;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseUp(pt, RecordMouseButton.Left));
                        }
                        else if (msg == WM_RBUTTONUP)
                        {
                            _mouseButtonsDownMask &= ~2;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseUp(pt, RecordMouseButton.Right));
                        }
                        else if (msg == WM_MBUTTONUP)
                        {
                            _mouseButtonsDownMask &= ~4;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseUp(pt, RecordMouseButton.Middle));
                        }
                        else if (msg == WM_MOUSEMOVE)
                        {
                            // 仅在有按键按住（拖拽）时派发移动更新，无按住时零开销
                            if (_mouseButtonsDownMask != 0)
                            {
                                Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseMove(pt));
                            }
                        }
                        else if (msg == WM_MOUSEWHEEL)
                        {
                            int delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                            bool isUp = delta > 0;
                            Dispatcher.UIThread.Post(() => _overlay?.TriggerMouseWheel(pt, isUp));
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return NativeMethods.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 画板开合时刷新钩子侧那份矩形（UI 线程；选区在会话内不变，故只有"开/关"两种结果）。
    /// 与覆盖层里 Win32 命中判定、标注层裁剪用的是同一段几何，判据不再另起炉灶。
    /// </summary>
    private static void SyncAnnotationBoardRect()
    {
        RecordOverlayWindow? ov = _overlay;
        if (ov == null || !ov.AnnotationToolActive)
        {
            _annoBoardOn = false;
            _keycapMuted = false;
            return;
        }
        Rectangle r = ov.RegionPhys;
        _annoBoardLeft = r.Left;
        _annoBoardTop = r.Top;
        _annoBoardRight = r.Right;
        _annoBoardBottom = r.Bottom;
        _annoBoardOn = true;   // volatile 写收尾：见字段注释
    }

    private static bool IsPointOnAnnotationBoard(int x, int y)
        => _annoBoardOn && x >= _annoBoardLeft && x < _annoBoardRight
            && y >= _annoBoardTop && y < _annoBoardBottom;

    private static bool IsCtrlDown() => _downKeys.Contains(NativeMethods.VK_CONTROL);

    /// <summary>
    /// 生成按键显示文本（1:1 对标录屏参考 BuildKeyDisplayText 与 GetKeyName）。
    /// 支持 Win 徽标键、Ctrl/Alt/Shift 硬件按键状态检测与单个修饰键或组合键展示。
    /// </summary>
    private static string BuildKeyDisplayText(uint vk)
    {
        // 检查修饰键硬件物理状态
        bool ctrl = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool shift = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool alt = (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool win = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0;

        // 若当前按下的就是修饰键本身，确保置为 true
        if (vk is 0x11 or 0xA2 or 0xA3) ctrl = true;
        if (vk is 0x10 or 0xA0 or 0xA1) shift = true;
        if (vk is 0x12 or 0xA4 or 0xA5) alt = true;
        if (vk is 0x5B or 0x5C) win = true;

        var keys = new List<string>(4);
        if (ctrl) keys.Add("Ctrl");
        if (shift) keys.Add("Shift");
        if (alt) keys.Add("Alt");
        if (win) keys.Add("Win");

        bool isMod = vk is 0x11 or 0xA2 or 0xA3
                         or 0x10 or 0xA0 or 0xA1
                         or 0x12 or 0xA4 or 0xA5
                         or 0x5B or 0x5C;

        if (!isMod)
        {
            string name = GetKeyName(vk);
            if (!string.IsNullOrEmpty(name)) keys.Add(name);
        }

        if (keys.Count == 0) return "";
        return string.Join(" + ", keys.Distinct());
    }

    /// <summary>
    /// 解析单个非修饰键名称（1:1 对标录屏参考 GetKeyName）。
    /// </summary>
    private static string GetKeyName(uint vk)
    {
        // 0-9
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        // A-Z
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        // F1-F12
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
        // F13-F24
        if (vk >= 0x7C && vk <= 0x87) return "F" + (vk - 0x7C + 13);
        // NumPad 0-9
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);

        return vk switch
        {
            0x0D => "Enter",
            0x1B => "Esc",
            0x08 => "Back",
            0x09 => "Tab",
            0x20 => "Space",
            0x2D => "Ins",
            0x2E => "Del",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x2C => "PrtSc",
            0x91 => "ScrLk",
            0x13 => "Pause",
            0x14 => "Caps",

            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",

            0x6A => "*",
            0x6B => "+",
            0x6C => "Enter",
            0x6D => "-",
            0x6E => ".",
            0x6F => "/",

            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            0xBA => ";",
            0xDE => "'",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBD => "-",
            0xBB => "=",
            0xC0 => "`",

            0xAD => "Mute",
            0xAE => "Vol-",
            0xAF => "Vol+",
            0xB0 => "Next",
            0xB1 => "Prev",
            0xB3 => "Play",
            0xB2 => "Stop",
            _ => ""
        };
    }

    // ================= 停止 / 收尾 =================

    private static void TogglePause()
    {
        IRecordingSession? s = _session;
        if (s == null || !IsRecording || _stopRequested) return;
        if (s.IsPaused) s.Resume();
        else s.Pause();
    }

    private static volatile bool _stopRequested;

    private static void StopForComplete()
    {
        IRecordingSession? s = _session;
        if (s == null || !IsRecording || _stopRequested) return;
        _stopRequested = true;
        // 所有停止入口已在 UI 线程：接受请求后同步冻结控制条，避免多排一帧或在预览交接后才回写。
        _overlay?.SetFinishing();
        // Stop() 内含合并+探测，放后台线程，UI 由 Completed/Failed 事件收尾
        Task.Run(() =>
        {
            try { s.Stop(); }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => OnFailed("停止失败：" + ex.Message));
            }
        });
    }

    private static void OnCompleted(string masterMp4, TimeSpan duration)
    {
        IRecordingSession? s = _session;
        _session = null;
        // 母带物理尺寸要在 Dispose 之前从会话上取走：预览画面卡按它摆（不按选区），
        // 否则两个宽高比一不等就会留一条露底色的缝。取不到是 (0,0)，贴图窗自己回落到选区算式。
        var (masterW, masterH) = s?.MasterSizePx ?? (0, 0);
        bool systemPlayback = s?.Engine == RecordEngine.Windows;
        try { s?.Dispose(); } catch { } // Completed 后 IsRecording 已假，Dispose 不会删临时目录

        // 🔴 这里**不**收遮罩，也从 2026-09-22（第三批）起**不**在预览揭开时收：遮罩窗改当预览态的暗纱，
        // 随预览窗一起退场（<see cref="ShowPreviewSticker"/> 里订了 sticker.Closed → CloseOverlay）。
        // 完成那一刻就收的话，预览窗还没亮，中间那几帧露的是桌面/下层内容（用户实报"闪一下白屏才切换到预览"）。
        // 录制中控制条在进预览那一拍收（EnterPreviewMask 里），不留到预览结束。
        // ⚠ 现在多活的是"预览窗开着的那一整段"：期间若再按 F1 截图，两层的暗色会叠一次——纯视觉、
        //    且这扇窗全穿透，吃不进任何点击，故不做额外处理（真被反馈碍事再收）。
        TeardownInfra();
        IsRecording = false;
        IsPaused = false;
        IsBusy = false;
        // M3 语义：母带留在临时盘，LastOutputPath 只是兜底指针；真正落盘由贴图窗保存后自管
        LastOutputPath = masterMp4;
        _finishTcs?.TrySetResult(RecordFlowOutcome.Completed);
        AppLogger.Info("RecordFlow: 录制完成，时长 " + duration + "，母带 " + masterMp4);
        ShowPreviewSticker(masterMp4, duration, masterW, masterH, systemPlayback);
    }

    /// <summary>稿 3 D12 乙案：完成即「视频贴图钉屏」，初始位置=选区原位；引擎起不来时由贴图窗如实说明原因、
    /// 保存与复制那两条链路照常可用（见 <c>VideoStickerWindow.ShowNoPlayback</c>）。
    /// <para>预览窗走 <see cref="WindowPresentationHelper.ShowSmoothly"/> 的<b>门控揭开</b>：窗画完不等于
    /// 内容到位——ffmpeg 首帧还要约 120ms，按常规通路（双格 RAF）揭开就是浅色底板白闪一下（用户 2026-09-22
    /// 实报，那一条至今没消掉的原因就在这里）。改由贴图窗在<b>首帧真的合成上屏</b>后调用揭开。</para>
    /// <para>揭开那一刻同时把遮罩窗切到<b>预览态暗纱</b>（2026-09-22 第三批拍板：压暗属于预览态，不属于录制中）：
    /// 卡与暗纱同帧出现，且两扇窗之间任何时刻都不留露底的缝。遮罩改由预览窗 <c>Closed</c> 收，
    /// 保存后关窗／放弃／✕／Esc 四条出口都汇到那一个地方。</para></summary>
    private static void ShowPreviewSticker(string masterMp4, TimeSpan duration, int masterW, int masterH, bool systemPlayback)
    {
        try
        {
            var virt = MonitorHelper.GetVirtualScreenRect();
            var region = _pendingOpt?.Region ?? default;
            if (region.Width < 2 || region.Height < 2)
            {
                // 理论上到不了这里（框选校验过）；给个居中 640×360 兜底而不是弹在 (0,0)
                region = new Rectangle(virt.X + (virt.Width - 640) / 2, virt.Y + (virt.Height - 360) / 2, 640, 360);
            }
            var sticker = new VideoStickerWindow();
            sticker.UseSystemPlayback = systemPlayback;
            sticker.SetupPreview(masterMp4, Path.GetDirectoryName(masterMp4) ?? "",
                region, _format, _pendingOpt?.EffectivePresetId, duration, _dpiScale,
                masterW, masterH);
            // 本段预览对应的那一扇遮罩：它现在要活到这张卡关掉，所以退场排在这里而不是揭开的那一刻。
            // ⚠ 认死"自己那一扇"——预览卡开着时用户完全可以再起一次录制，那时 _overlay 已指向新窗，
            // 无条件 CloseOverlay() 等于把正在录的那层收掉。
            var previewOverlay = _overlay;
            // 卡要能把"自己这一扇"遮罩的预览暗纱一起收放（转码期最小化到任务栏），所以把同一份引用交给它。
            // 认死的仍是这一扇：预览卡开着时用户再起一次录制，_overlay 已指向新窗，那张卡收的是它自己那扇。
            sticker.AttachPreviewOverlay(previewOverlay);
            sticker.Closed += (_, _) =>
            {
                if (ReferenceEquals(_overlay, previewOverlay)) CloseOverlay();
                else { try { previewOverlay?.CloseOverlay(); } catch { } }
            };
            WindowPresentationHelper.ShowSmoothly(sticker,
                // ⚠ 第三枚参数＝卡那一圈的圆角，必须传 PlateRadiusDip（卡片可见轮廓用的就是这一枚），
                // 不是 PreviewPlateRadiusDip（那一枚 = +1，只喂外层宿主的 BoxShadow 光晕）。
                // 传 +1 那枚是"同族缩放"的错法：同一个矩形上半径越大角越往里切 ⇒ 洞比卡小，
                // 四角各啃进卡身一条，用户实报"视频跟遮罩还有描边是三个东西"。
                onPresented: () => _overlay?.EnterPreviewMask(sticker, sticker.PreviewPlate, sticker.PlateRadiusDip),
                gateReveal: sticker.ArmRevealGate);
        }
        catch (Exception ex)
        {
            AppLogger.Error("RecordFlow: 预览贴图拉起失败", ex);
            // 预览起不来也得把遮罩收掉——否则它会一直挂在那儿没人负责（收遮罩已从 OnCompleted 搬到这里）
            CloseOverlay();
            StickerToastWindow.Show("预览窗口拉起失败，母带仍在临时目录：" + masterMp4);
        }
    }

    private static void OnFailed(string message)
    {
        IRecordingSession? s = _session;
        _session = null;
        try { s?.Dispose(); } catch { }
        CloseOverlay();
        TeardownInfra();
        IsRecording = false;
        IsPaused = false;
        IsBusy = false;
        _finishTcs?.TrySetResult(RecordFlowOutcome.Failed);
        AppLogger.Warn("RecordFlow: 录制失败：" + message);
        StickerToastWindow.Show("录制失败：" + message);
    }

    private static string CombineDegradeText()
    {
        return _degradeDetails.Count <= 1
            ? _degradeDetails[0]
            : _degradeDetails[0] + $"（另有 {_degradeDetails.Count - 1} 项降级，悬停徽标查看）";
    }

    private static void CloseOverlay()
    {
        _annoBoardOn = false;   // 窗口都没了，钩子却可能还挂着（摘钩在 TeardownInfra）：先把开关落下
        _keycapMuted = false;
        try { _overlay?.CloseOverlay(); } catch { }
        _overlay = null;
    }

    private static void TeardownInfra()
    {
        UnregisterPauseStopHotkeys();
        if (_kbHook != IntPtr.Zero) { try { NativeMethods.UnhookWindowsHookEx(_kbHook); } catch { } _kbHook = IntPtr.Zero; }
        UninstallMouseHook();
        _downKeys.Clear();
        StopSessionWatch();
        NotifyTrayRecording(false);
    }

    // ================= 锁屏自动暂停（降级④） =================

    /// <summary>录制会话期挂 WTS 锁屏监听：锁屏即自动暂停（等同用户暂停，琥珀三件套 + toast 由 Degraded 事件链完成）。</summary>
    private static void StartSessionWatch()
    {
        try
        {
            _sessionWatch = new SessionNotification();
            if (!_sessionWatch.IsRegistered)
            {
                AppLogger.Warn("RecordFlow: WTSRegisterSessionNotification 失败，本段录制无锁屏自动暂停");
            }
            _sessionWatch.SessionLocked += () => Dispatcher.UIThread.Post(() => _session?.AutoPause());
        }
        catch (Exception ex)
        {
            _sessionWatch = null;
            AppLogger.Warn("RecordFlow: 锁屏监听启动失败（不阻断录制）：" + ex.Message);
        }
    }

    private static void StopSessionWatch()
    {
        try { _sessionWatch?.Dispose(); } catch { }
        _sessionWatch = null;
    }

    /// <summary>稿 2 01 卡：录制期托盘图标进入录制态（tooltip 标注；停止/放弃后还原）。</summary>
    private static void NotifyTrayRecording(bool recording)
    {
        try { (Avalonia.Application.Current as App)?.NotifyRecordState(recording); } catch { }
    }
}
