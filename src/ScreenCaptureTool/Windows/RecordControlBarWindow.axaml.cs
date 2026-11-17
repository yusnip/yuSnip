using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 录屏独立浮动控制条小窗口：尺寸仅包裹控制条内容，不带透明穿透，支持常规鼠标交互与拖动。
/// <para>第10份案丙：六枚标注工具钮＋撤销并进第一行（与录制控制同一栏），第二行是共享
/// <see cref="AnnotationToolbar"/> 的<b>参数条</b>（<c>Embedded</c> 档：图标条与那圈阴影余量由本窗自持）。
/// 本窗只是钮的容器与参数源，画板开合、图元与撤销栈都在 <see cref="RecordOverlayWindow"/> 那一份里。</para>
/// </summary>
public partial class RecordControlBarWindow : Window
{
    private readonly ToolbarLayoutView _customLayout;
    internal IEnumerable<AnnotationToolButton> AnnotationButtons => _customLayout.Controls.OfType<AnnotationToolButton>();
    private bool _paused;
    /// <summary>Opened 时缓存的句柄：Closed 阶段实时取多半已是 0，置顶守卫必须按它来停（protection.md 置顶条）。</summary>
    private IntPtr _openedHwnd;
    private DispatcherTimer? _blinkTimer;
    /// <summary>重录倒计时期间为真：那三枚（暂停／重录／结束录制）禁用，各自的 ToolTip 原值存 <see cref="_waitingTips"/>。</summary>
    private bool _waiting;
    private readonly Dictionary<Control, object?> _waitingTips = new();

    /// <summary>遮罩那条选区边框厚 2 DIP、跨压在选区边界上 ⇒ 视觉外沿比选区矩形向外多占 1 DIP。</summary>
    private const double BorderOutsetDip = 1;

    private DrawingRectangle _regionPhys;
    private DrawingRectangle _virtPhys;
    private double _scale = 1.0;
    private bool _dragged;
    private int _lastElapsedLen = -1;

    public event EventHandler? PauseToggleRequested;
    public event EventHandler? CompleteRequested;
    public event EventHandler? RestartRequested;

    /// <summary>键鼠特效两枚勾选任一改动（参数＝当下勾选态）。会话内即时生效，勾选本身另记进「上次使用」族。</summary>
    public event Action<bool, bool>? FxChanged;

    /// <summary>钮上按下的键名（含参数条里那枚「清除当前工具」转进来的 <c>clearCurrent</c>）；
    /// <b>本窗不判翻转</b>——"再点已选中的即退出画板"这条开关语义只在遮罩那一份实现里。</summary>
    public event Action<string>? AnnotationToolRequested;
    public event EventHandler? AnnotationUndoRequested;
    public event EventHandler? AnnotationClearRequested;

    /// <summary>当前选中的工具键名（由遮罩回写，本窗不自行翻转）。</summary>
    private string? _annoKey;
    /// <summary>撤销的"逻辑可用态"与"位置可用态"各管一头，最后一起与：<see cref="_annoCanUndo"/> 来自遮罩，
    /// <see cref="_insideCrop"/> 来自钉位（A10：条身落在 crop 内＝会被录进画面，整段标注置灰）。</summary>
    private bool _annoCanUndo;
    private bool _insideCrop;
    /// <summary>受 A10 置灰的那批控件（六枚工具钮＋撤销），连同各自的原始 ToolTip 一起存着以便还原。</summary>
    private readonly List<Control> _annoTargets = new();
    private readonly List<object?> _annoTips = new();

    public RecordControlBarWindow()
    {
        InitializeComponent();
        var map = NormalRow.Children.OfType<AnnotationToolButton>().ToDictionary(b => b.ToolKey, b => new Control[] { b });
        map["fx"] = [(Control)BtnFx.Parent!];
        map["pause"] = [BtnPause, BtnResume]; map["restart"] = [BtnRestart]; map["finish"] = [BtnComplete];
        _customLayout = new ToolbarLayoutView(NormalRow, Core.ToolbarScene.RecordControl, map, NormalRow.Children[0], NormalRow.Children[1]);
        _customLayout.Apply(Core.AppSettings.Current);

        Opened += (_, _) =>
        {
            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _openedHwnd = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: false);
                WindowHelper.DisableWindowChromeEffects(hwnd);
                // 持久置顶：与遮罩同一守卫。控制条在遮罩<b>之后</b>才 Show，同在置顶带里它天然压在遮罩之上；
                // 守卫只管"别被第三方挤出置顶带"。取色弹窗那条"先停后恢复"由 ColorPickerWindow.PickAsync 按
                // IsEnforceTopmostActive 处理，本窗不用另写。
                WindowHelper.EnforceTopmost(hwnd);
            }
        };

        // 时长读数的预留宽＝现场量「00:00」在这一档字体下的实际宽度（不手算），只为防每次跳秒带动条宽抖动
        TxtBarTime.MinWidth = LayoutMetrics.MeasureNow(TxtBarTime).Width;

        // 参数条起点与截图/贴图同一份记忆值（同一个 AppSettings 键集），三处工具切到哪都接着上次的粗细颜色
        try { AnnoOptions.ApplyDefaults(Core.AppSettings.Load()); } catch { }

        // A10 的置灰名单：XAML 里只给撤销起了名，六枚工具钮按 ToolKey 认，两边不再各写一遍
        foreach (var button in AnnotationButtons)
        {
            _annoTargets.Add(button);
            _annoTips.Add(ToolTip.GetTip(button));
        }

        // 键名必须在置灰名单之后、任何 SetWaiting 之前上好：SetWaiting 是"存当前值→换提示→还原当前值"，
        // 存进去的得是带键名的那份，否则退出等待态会把键名抹掉。
        ApplyShortcutHints();

        AnnoOptions.ToolRequested += key =>
        {
            // Embedded 档图标条收起，参数条上只有「清除当前工具」这一支会报进来
            if (key == "clearCurrent") AnnotationClearRequested?.Invoke(this, EventArgs.Empty);
            else AnnotationToolRequested?.Invoke(key);
        };
        // 参数行一涨落，右缘平齐与缝合三角都要跟着重算（Repin 量的是含两行的整窗）
        AnnoOptions.LayoutChanged += () => { UpdateStitchArrow(); Repin(); };
        // 彩虹取色钮：与截图/贴图同一份取色窗，锚定到按钮自身，初值取**当前取色目标**的颜色
        //（文本语境下可能是底色或描边色，不是全局标注色）。取色窗会抢前台，先按同一语义把手上的字提交掉。
        AnnoOptions.CustomColorRequested += async anchor =>
        {
            AnnoOptions.TextController?.EndActiveTextEditing("CustomColor");
            Avalonia.Media.Color? picked = await ColorPickerWindow.PickAsync(this, AnnoOptions.CurrentTargetColor, anchor);
            if (picked.HasValue) AnnoOptions.ApplyCustomColor(picked.Value);
        };

        _blinkTimer = RecBlink.StartTimer(BarDot, () => _paused);

        Closed += (_, _) =>
        {
            WindowHelper.StopEnforceTopmost(_openedHwnd);
            _blinkTimer?.Stop();
            _dockPoll?.Stop();
            _dockAnim?.Stop();
        };
    }

    /// <summary>光晕四边余量，<b>单一来源是 XAML 根 Grid 那圈行列</b>（与 AnnotationToolbar.ShadowInset 同法）：
    /// 钉位算式要从"整窗尺寸"换算到"可见条的右外缘/下外缘"，抵掉的正是这四条透明带。
    /// ⚠ 下沿恒为<b>最后一行</b>——第二行（标注参数行）插在中间，写死成索引 2 就量到 6 DIP 的栏间距上了。</summary>
    private Thickness ShadowInset => new(
        Root.ColumnDefinitions[0].Width.Value,
        Root.RowDefinitions[0].Height.Value,
        Root.ColumnDefinitions[2].Width.Value,
        Root.RowDefinitions[^1].Height.Value);

    /// <summary>登记选区与虚拟屏并钉位（<c>Show()</c> 之前调用）；此后内容一变即按同一算式重钉。</summary>
    public void PositionAtRegion(DrawingRectangle regionPhys, DrawingRectangle virtPhys, double dpiScale)
    {
        _regionPhys = regionPhys;
        _virtPhys = virtPhys;
        _scale = dpiScale > 0 ? dpiScale : 1.0;
        Repin();
    }

    /// <summary>
    /// 用<b>实测</b>窗宽把可见条的右外缘钉到选区边框的右外沿（第9份位点 ⑧＋⑭ 的收口）。三条判据：
    /// ① 右缘含 2 DIP 描边的视觉外扩、逐像素平齐；② 内容一变就重钉，但用户手动拖过之后停在拖到的位置不再抢；
    /// ③ 条身<b>永不翻进 crop 区内</b>——ffmpeg 抓的是整屏合成、不排除自家窗口，翻进去等于把这条灰底栏录进成品视频。
    /// 向左溢出是允许的（溢出段落在 crop 外＝不入画），只有整窗要飞出虚拟屏左缘时才夹住。
    /// <para>internal：遮罩那边首次Show走 cloak，需要在"揭开前"再钉最后一次（见
    /// <see cref="WindowPresentationHelper.ShowSmoothly"/> 的 beforeReveal），不放开就只能再抄一份算式。</para>
    /// </summary>
    internal void Repin()
    {
        if (_finishing || _dragged || _virtPhys.Width <= 0 || _regionPhys.Width <= 0) return;

        Size win = LayoutMetrics.MeasureNow(Root);          // 含光晕余量与两行的整窗尺寸（DIP）
        Thickness inset = ShadowInset;
        // ⚠ 右缘取<b>合并行那块板</b>的实测宽，不取整窗宽：第二行（参数行）可以比合并行宽得多，
        // 整窗宽≠合并行宽，用窗宽钉位会让"与选区右缘平齐"这条凭空偏出去（合并行 Left 对齐、恒从 20 起步）。
        double plateRightDip = inset.Left + LayoutMetrics.MeasureNow(BarPlate).Width;

        int rightOffsetPx = (int)Math.Round(plateRightDip * _scale);            // 窗口左缘 → 合并行右缘
        int bottomOffsetPx = (int)Math.Round((win.Height - inset.Bottom) * _scale);   // 窗口顶缘 → 参数行下缘
        int visibleTopPx = (int)Math.Round(inset.Top * _scale);
        int outsetPx = (int)Math.Round(BorderOutsetDip * _scale);
        int gapPx = (int)Math.Round(CaptureWindow.OverlayGap * _scale);

        int x = Math.Max(_regionPhys.Right + outsetPx - rightOffsetPx, _virtPhys.Left);

        // 首选：整条落在选区下缘之外
        int yBelow = _regionPhys.Bottom + outsetPx + gapPx - visibleTopPx;
        // 次选：翻到选区上缘之外（翻转分支要退掉条身自身高度，否则整条压回选区里）
        int yAbove = _regionPhys.Y - outsetPx - gapPx - bottomOffsetPx;
        // 末选：上下两侧都没有余量（选区几乎顶满虚拟屏）⇒ 夹到屏幕下缘。此时条身必然落在 crop 内，
        // 这与任何一款录屏软件在全屏录制时的处境相同，属已知的不可消除项，故只在"无余量"时才走。
        int yClamped = Math.Max(_virtPhys.Bottom - bottomOffsetPx, _virtPhys.Top);
        int y = yBelow + bottomOffsetPx <= _virtPhys.Bottom ? yBelow
            : yAbove + visibleTopPx >= _virtPhys.Top ? yAbove
            : yClamped;

        // 上下两侧都放不下（全屏／近全屏录制）：改走自动隐藏档——条身常规时藏在屏幕底边外、只露一截，
        // 鼠标靠近滑出、移开收回（2026-09-29）。旧口径是"夹到屏幕下缘常驻"，那条灰底栏会整段录进成品。
        if (y == yClamped && ComputeDockLayout(win, inset))
        {
            ApplyPlacementState(false);
            ApplyDockPosition();
            return;
        }
        LeaveDock();

        // 值一样就一个字节都不写：Position 赋相同值也照样发一次 SetWindowPos，而"发出去"就有中间态
        // （DPI 取整、合成器重排）。这一条是"出现即刚好、此后不再自己挪"的判据落点。
        PixelPoint next = new(x, y);
        if (Position != next) Position = next;
        ApplyPlacementState(y == yClamped);   // A10：走到末选＝条身在 crop 内，会被录进成品
    }

    // ================= 收边自动隐藏（全屏／近全屏录制，2026-09-29） =================
    //
    // 判据＝Repin 的"上下两侧都放不下、只能夹进屏内"那一档：条身放在任何位置都会录进画面，
    // 与其整条常驻，不如收进任务栏那一侧的屏幕边外、只露一截，鼠标靠近滑出、移开收回。
    // 位置由本段独占（不给拖），窗宽窗高仍由 SizeToContent 自己给，每次 Repin 只重算两个目标点。

    /// <summary>收起时露在屏内的那一截（DIP，量的是条身可见板，不含光晕余量）。</summary>
    private const double DockPeekDip = 6;
    /// <summary>滑出后条身与工作区边缘（任务栏上沿）的间距（DIP）。</summary>
    private const double DockRevealGapDip = 8;
    /// <summary>靠近判定：光标距屏幕边多少 DIP 以内、且水平落在条身左右各外扩这么多的范围里就滑出。</summary>
    private const double DockHotZoneDip = 14, DockHotSlackDip = 40;
    /// <summary>移开判定：光标离开滑出后条身外扩这么多仍算"还在条上"，避免擦边抖动。</summary>
    private const double DockLeaveSlackDip = 24;
    /// <summary>移开后等这么久再收（毫秒）；首次出现先亮这么久让用户知道条在哪。</summary>
    private const int DockHideDelayMs = 450, DockIntroMs = 1600;

    private bool _docked;
    private bool _dockTop;              // 任务栏在顶部 ⇒ 收进上边；其余（底部／左右／自动隐藏）一律收进下边
    private bool _dockRevealed;
    private PixelPoint _dockHiddenPos, _dockShownPos;
    private System.Drawing.Rectangle _dockMon;
    private DispatcherTimer? _dockPoll, _dockAnim;
    private long _dockLeaveSince = -1, _dockIntroUntil;
    private double _dockAnimProgress;   // 0＝收起，1＝滑出

    /// <summary>按当前整窗尺寸算收起／滑出两个目标点。返回 false＝不走收边档（例如取不到屏幕信息）。</summary>
    private bool ComputeDockLayout(Size win, Thickness inset)
    {
        var (mon, work) = MonitorHelper.GetMonitorAndWorkAreaForRegion(_regionPhys);
        if (mon.Width <= 0 || mon.Height <= 0) return false;

        double s = _scale;
        int winW = (int)Math.Round(win.Width * s);
        int contentTopPx = (int)Math.Round(inset.Top * s);                       // 窗顶 → 条身上沿
        int contentBottomPx = (int)Math.Round((win.Height - inset.Bottom) * s);  // 窗顶 → 条身下沿（含参数行）
        int plateBottomPx = contentTopPx + (int)Math.Round(LayoutMetrics.MeasureNow(BarPlate).Height * s);
        int peekPx = (int)Math.Round(DockPeekDip * s);
        int gapPx = (int)Math.Round(DockRevealGapDip * s);

        _dockTop = work.Top > mon.Top && work.Bottom >= mon.Bottom;
        int x = mon.X + (mon.Width - winW) / 2;
        if (_dockTop)
        {
            // 收起：合并行下沿只露 peek 在屏顶之下；滑出：条身上沿落在工作区（任务栏下沿）之下 gap
            _dockHiddenPos = new PixelPoint(x, mon.Top + peekPx - plateBottomPx);
            _dockShownPos = new PixelPoint(x, work.Top + gapPx - contentTopPx);
        }
        else
        {
            // 收起：合并行上沿只露 peek 在屏底之上；滑出：条身（含参数行）下沿落在工作区（任务栏上沿）之上 gap
            _dockHiddenPos = new PixelPoint(x, mon.Bottom - peekPx - contentTopPx);
            _dockShownPos = new PixelPoint(x, work.Bottom - gapPx - contentBottomPx);
        }
        _dockMon = mon;

        if (!_docked)
        {
            _docked = true;
            _dockRevealed = true;          // 首次出现先亮着：让用户看见条在哪，再按光标位置决定收不收
            _dockAnimProgress = 1;
            _dockIntroUntil = Environment.TickCount64 + DockIntroMs;
            _dockLeaveSince = -1;
            EnsureDockPoll();
            Core.AppLogger.Info($"RecordControlBar: 选区 {_regionPhys.Width}×{_regionPhys.Height} 上下无余量，" +
                           $"控制条改为收边自动隐藏（{(_dockTop ? "上" : "下")}边）");
        }
        return true;
    }

    /// <summary>按当前动画进度落位（Repin 与动画共用这一个出口）。</summary>
    private void ApplyDockPosition()
    {
        if (_finishing) return;
        double t = _dockAnimProgress;
        double e = t * t * (3 - 2 * t);   // smoothstep：起止都缓
        var next = new PixelPoint(
            (int)Math.Round(_dockHiddenPos.X + (_dockShownPos.X - _dockHiddenPos.X) * e),
            (int)Math.Round(_dockHiddenPos.Y + (_dockShownPos.Y - _dockHiddenPos.Y) * e));
        if (Position != next) Position = next;
    }

    /// <summary>选区不再是全屏档（理论上会话内选区不变，这里只为重录换选区时收干净）。</summary>
    private void LeaveDock()
    {
        if (!_docked) return;
        _docked = false;
        _dockPoll?.Stop();
        _dockAnim?.Stop();
    }

    private void EnsureDockPoll()
    {
        if (_dockPoll == null)
        {
            _dockPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _dockPoll.Tick += (_, _) => PollDock();
        }
        _dockPoll.Start();
    }

    /// <summary>光标靠近 → 滑出；离开滑出后的条身（外扩余量）超过 <see cref="DockHideDelayMs"/> → 收回。
    /// 三种情形钉着不收：特效面板开着、左键按住（正点着钮或拖参数滑杆）、保存中（条上正显示「正在保存...」）。</summary>
    private void PollDock()
    {
        if (_finishing || !_docked || !IsVisible) return;
        if (!NativeMethods.GetCursorPos(out var cp)) return;

        double s = _scale;
        long now = Environment.TickCount64;
        var shownRect = new System.Drawing.Rectangle(_dockShownPos.X, _dockShownPos.Y,
            (int)Math.Round(Bounds.Width * s), (int)Math.Round(Bounds.Height * s));
        int leave = (int)Math.Round(DockLeaveSlackDip * s);
        bool overShown = cp.X >= shownRect.Left - leave && cp.X < shownRect.Right + leave
                         && cp.Y >= shownRect.Top - leave && cp.Y < shownRect.Bottom + leave;

        int hot = (int)Math.Round(DockHotZoneDip * s), slack = (int)Math.Round(DockHotSlackDip * s);
        bool inX = cp.X >= shownRect.Left - slack && cp.X < shownRect.Right + slack
                   && cp.X >= _dockMon.Left && cp.X < _dockMon.Right;
        bool nearEdge = inX && (_dockTop ? cp.Y < _dockMon.Top + hot : cp.Y >= _dockMon.Bottom - hot);

        bool pinned = FxPopup.IsOpen || _finishing
                      || (_dockRevealed && overShown
                          && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0)
                      || now < _dockIntroUntil;

        if (nearEdge || (_dockRevealed && overShown) || pinned)
        {
            _dockLeaveSince = -1;
            if (!_dockRevealed) SetDockRevealed(true);
            return;
        }
        if (!_dockRevealed) return;
        if (_dockLeaveSince < 0) { _dockLeaveSince = now; return; }
        if (now - _dockLeaveSince >= DockHideDelayMs) SetDockRevealed(false);
    }

    private void SetDockRevealed(bool revealed)
    {
        if (_finishing) return;
        _dockRevealed = revealed;
        _dockLeaveSince = -1;
        if (_dockAnim == null)
        {
            _dockAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            _dockAnim.Tick += (_, _) =>
            {
                // 约 160ms 走完全程；中途换向从当前进度接着走，不跳
                double target = _dockRevealed ? 1 : 0;
                double step = 15.0 / 160.0;
                _dockAnimProgress = target > _dockAnimProgress
                    ? Math.Min(target, _dockAnimProgress + step)
                    : Math.Max(target, _dockAnimProgress - step);
                ApplyDockPosition();
                if (Math.Abs(_dockAnimProgress - target) < 1e-6) _dockAnim!.Stop();
            };
        }
        _dockAnim.Start();
    }

    /// <summary>保存中：固定当前位置与栏宽，不再参与重钉或收边动画。</summary>
    private bool _finishing;

    public void SetPaused(bool paused)
    {
        if (_finishing) return;
        _paused = paused;
        // 条上不再有状态文字（「已暂停」已挪去右上 REC 徽标）：这条宽度恒定，末尾那次 Repin 量到同值即不动。
        BtnPause.IsVisible = !paused && _customLayout.Includes("pause");
        BtnResume.IsVisible = paused && _customLayout.Includes("pause");
        RecBlink.Apply(BarDot, paused);
        Repin();
    }

    public void SetElapsed(TimeSpan t)
    {
        if (_finishing) return;
        string text = RecordOverlayWindow.FormatElapsed(t);
        TxtBarTime.Text = text;
        if (text.Length != _lastElapsedLen)   // 跨过 1 小时那一档读数变长、条宽随之而变 ⇒ 重钉
        {
            _lastElapsedLen = text.Length;
            Repin();
        }
    }

    /// <summary>
    /// 重录倒计时期间：暂停／重录／结束录制三枚禁用（此时既没有会话可停，也不该让人在起跑前重复点重录）。
    /// 三枚的 ToolTip 由 <see cref="_waitingTips"/> 存原值，回到录制态逐条还原。
    /// </summary>
    public void SetWaiting(bool waiting)
    {
        if (_finishing) return;
        if (_waiting == waiting) return;
        _waiting = waiting;
        BtnPause.IsEnabled = !waiting;
        BtnResume.IsEnabled = !waiting;
        BtnRestart.IsEnabled = !waiting;
        BtnComplete.IsEnabled = !waiting;
        const string WaitingTip = "正在重新起跑，请稍候";
        foreach (var b in new Control[] { BtnPause, BtnResume, BtnRestart, BtnComplete })
        {
            if (!_waitingTips.ContainsKey(b)) _waitingTips[b] = ToolTip.GetTip(b);
            ToolTip.SetTip(b, waiting ? WaitingTip : _waitingTips[b]);
        }
    }

    /// <summary>
    /// 结束录制中（对标录屏参考 StopAndShowPreview）：立即将控制条全部禁用，
    /// 保留时间格宽度与参数行占位，仅显示「保存」，避免退场前涨宽／缩高引起窗口重排闪动。
    /// </summary>
    public void SetFinishing()
    {
        if (_finishing) return;
        _finishing = true;
        // 标记先落：关闭弹层及预览交接会带来延后布局／状态通知，这些通知不能再搬动控制条。
        _blinkTimer?.Stop();
        _dockPoll?.Stop();
        _dockAnim?.Stop();
        NormalRow.IsEnabled = false;
        OptionsHost.IsEnabled = false;
        FxPopup.IsOpen = false;
        ToolTip.SetIsOpen(BtnComplete, false);
        double timeWidth = TxtBarTime.Bounds.Width;
        if (timeWidth <= 0) timeWidth = LayoutMetrics.MeasureNow(TxtBarTime).Width;
        TxtBarTime.Width = timeWidth;
        TxtBarTime.Text = "保存";
        ToolTip.SetTip(TxtBarTime, "正在保存…");
    }

    /// <summary>键鼠勾选初值（由遮罩把 RecordFlow 那两份当下状态传进来，本窗不自行读设置）。
    /// ⚠ 回放初值那一下也走 <c>IsCheckedChanged</c>，故用 <see cref="_fxSeeding"/> 挡住"记为上次使用"：
    /// 否者「光标入画」关着时涟漪被起跑判据夹成 false，一开条就把用户真正勾过的那份记成 false。</summary>
    private bool _fxSeeding;

    public void SetFxInitial(bool keycap, bool ripple)
    {
        _fxSeeding = true;
        try
        {
            ChkKeycap.IsChecked = keycap;
            ChkRipple.IsChecked = ripple;
        }
        finally { _fxSeeding = false; }
    }

    // ================= 标注（第10份案丙：钮在第一行、参数行是第二行，本窗只当容器与参数源） =================

    /// <summary>共享参数条本体。画板开合、图元、撤销栈与滚轮分派全在遮罩那份共享会话里，
    /// 本窗只把这枚控件交出去当会话的 <c>EditToolbar</c>（贴图侧是浮窗的同一控件、同一角色）。</summary>
    internal AnnotationToolbar OptionsBar => AnnoOptions;

    /// <summary>第一行上那六枚工具钮（撤销不是工具，按 ToolKey 排除）。判据只在这里写一份。</summary>
    private IEnumerable<AnnotationToolButton> ToolButtons()
        => AnnotationButtons.Where(b => b.ToolKey != "undo");

    /// <summary>
    /// 遮罩回写当前工具（null＝已退出画板）。本窗只做外观与参数行的开合，不判"再点同一枚即退出"——
    /// 那条翻转在遮罩里，控制条与参数条两条来路都要经过它，写两份就会互相看不见。
    /// </summary>
    public void SetAnnotationTool(string? key)
    {
        if (_finishing) return;
        _annoKey = key;
        foreach (var button in ToolButtons())
            button.Classes.Set("active", button.ToolKey == key);
        // 先撑开容器再让共享控件量宽：SetActiveTool 末尾那次 ReflowBars 会报 LayoutChanged，
        // 那里的 Repin 要按"两行都在"的整窗高算下缘，晚一步就钉到旧的单行高度上。
        OptionsHost.IsVisible = key != null;
        AnnoOptions.SetActiveTool(key);
    }

    /// <summary>撤销可用态（落笔/撤销/清除后由遮罩回写）；A10 置灰期间一律按禁用摆。</summary>
    public void SetAnnotationUndoEnabled(bool canUndo)
    {
        _annoCanUndo = canUndo;
        BtnAnnoUndo.IsEnabled = EffectiveEnabled(canUndo);
    }

    /// <summary>一条钮的实际可用态＝遮罩给的逻辑档 与 "没被 A10 位置判据禁掉"。</summary>
    private bool EffectiveEnabled(bool logical) => !_finishing && !_insideCrop && logical;

    /// <summary>当帧冻结一笔的手势参数：18 档的算式收在共享参数条那一份里（贴图走同一条），本窗只补两个倍率。
    /// 遮罩与抓帧像素 1:1，故箭头缩放与画笔速度都按 1 传（与截图侧 <c>GetAnnotationGestureSettings</c> 同口径）。</summary>
    internal AnnotationGestureSettings CreateGestureSettings(ToolMode tool)
        => AnnoOptions.CreateGestureSettings(tool, 1f, 1f);

    /// <summary>
    /// 缝合三角对准所选钮的中心（§7.5 双浮层配方）。两个夹取各管一头：X 越出<b>两栏的重叠段</b>就不再是
    /// "把两栏缝起来"，而是飘在参数条外面的一枚孤三角；最右一钮对着窄参数条时就会走到这一档。
    /// </summary>
    private void UpdateStitchArrow()
    {
        if (_finishing) return;
        _customLayout.SelectTool(_annoKey);
        var button = ToolButtons().FirstOrDefault(b => b.IsVisible && b.ToolKey == _annoKey);
        OptionsArrow.IsVisible = _annoKey != null && OptionsHost.IsVisible && button != null;
        if (button == null) return;
        // 刚切模式时还没排版，按主行本轮实测宽度找按钮中心，避免 TranslatePoint 读到旧位置。
        double plateWidth = LayoutMetrics.MeasureNow(BarPlate).Width;
        double optionsWidth = LayoutMetrics.MeasureNow(AnnoOptions).Width;
        double center = 1 + 8;
        foreach (var child in NormalRow.Children.OfType<Control>().Where(c => c.IsVisible))
        {
            if (ReferenceEquals(child, _annoKey == null ? null : _customLayout.Anchor(_annoKey)))
            {
                center += child.Margin.Left + (child.DesiredSize.Width - child.Margin.Left - child.Margin.Right) / 2.0;
                break;
            }
            center += child.DesiredSize.Width;
        }
        // 橡皮擦的图标参数条较短，左贴主栏会让缝合三角够不到工具钮；按其他浮栏规则居中并夹取。
        double optionsX = _annoKey == "eraser"
            ? Math.Clamp(center - optionsWidth / 2.0, 0, Math.Max(0, plateWidth - optionsWidth))
            : 0;
        OptionsHost.Margin = new Thickness(optionsX, 0, 0, 0);
        double arrowW = OptionsArrow.Width;
        double overlap = Math.Min(plateWidth - optionsX, optionsWidth);
        double x = Math.Clamp(center - optionsX - arrowW / 2.0, 0, Math.Max(0, overlap - arrowW));
        OptionsArrow.Margin = new Thickness(x, 2, 0, 0);
    }

    /// <summary>
    /// A10：钉位走到"末选夹到屏幕下缘"那一档＝条身已经在 crop 区内，此时标注画在选区里会连着这条灰底栏
    /// 一起被录进成品，故整段标注置灰并换掉 ToolTip 说明原因（不是"功能坏了"，是"这里不能画"）。
    /// </summary>
    private void ApplyPlacementState(bool insideCrop)
    {
        if (_insideCrop == insideCrop) return;
        _insideCrop = insideCrop;

        const string BlockedTip = "控制条此时在录制区域内，标注会被录进画面，已禁用";
        for (int i = 0; i < _annoTargets.Count; i++)
        {
            _annoTargets[i].IsEnabled = !_insideCrop;
            ToolTip.SetTip(_annoTargets[i], insideCrop ? BlockedTip : _annoTips[i]);
        }
        BtnAnnoUndo.IsEnabled = EffectiveEnabled(_annoCanUndo);
        if (insideCrop) OptionsHost.IsVisible = false;
    }

    /// <summary>
    /// 四枚动作钮的 tooltip 键名从 <c>shortcuts.ini</c> 的 record 组现取（与截图工具栏同一口径：
    /// XAML 只写动作名，键名由目录生成）。用户在设置页改了键，这里不会继续教旧键。
    /// </summary>
    private void ApplyShortcutHints()
    {
        var map = Services.BuiltinShortcutsStore.LoadGroupMap(Services.BuiltinShortcutsStore.GroupRecord);
        void Tip(Control c, string id, string label)
        {
            string key = map.TryGetValue(id, out var sc) ? Services.BuiltinShortcutsStore.Format(sc) : "";
            ToolTip.SetTip(c, string.IsNullOrEmpty(key) ? label : label + " (" + key + ")");
        }
        // 暂停/继续共用 record.pause（一枚键在两枚互斥钮上）；BtnComplete 走 record.stop，
        // 它教的不是那条兜底 Ctrl+Del——兜底键锁死且不经热键注册，写在条上只会让人以为只有那一条路。
        Tip(BtnPause, "record.pause", "暂停");
        Tip(BtnResume, "record.pause", "继续");
        Tip(BtnRestart, "record.restart", "重新录制（已录内容作废）");
        Tip(BtnComplete, "record.stop", "结束录制并预览");
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e) => PauseToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnAnnotationToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is AnnotationToolButton { ToolKey.Length: > 0 } button)
            AnnotationToolRequested?.Invoke(button.ToolKey);
    }

    private void OnAnnotationUndoClick(object? sender, RoutedEventArgs e)
        => AnnotationUndoRequested?.Invoke(this, EventArgs.Empty);

    private void OnCompleteClick(object? sender, RoutedEventArgs e)
    {
        if (_finishing) return;
        CompleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        FxPopup.IsOpen = false;
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnFxClick(object? sender, RoutedEventArgs e) => FxPopup.IsOpen = !FxPopup.IsOpen;

    private void OnFxCheckChanged(object? sender, RoutedEventArgs e)
    {
        bool keycap = ChkKeycap.IsChecked == true;
        bool ripple = ChkRipple.IsChecked == true;
        FxChanged?.Invoke(keycap, ripple);
        if (!_fxSeeding) Services.RecordFlow.RememberFxChoice(keycap, ripple);
    }

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_finishing) return;
        // 允许通过空白处拖拽浮动条。⚠ 面板开着时不参与：那一下是「点外面即收」的收面板手势，
        // 同时起一次拖动就会既关了面板又把整条挪走。
        if (FxPopup.IsOpen) return;
        // 收边档不给拖：它的位置由"藏／露"两态算式独占，拖走再回吸反而困惑
        if (_docked) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && e.Source is not Button
            && (e.Source as Visual)?.FindAncestorOfType<Button>() == null)
        {
            BeginMoveDrag(e);
            _dragged = true;   // 判据②：拖过之后停在拖到的位置，内容再变也不回吸
        }
    }
}
