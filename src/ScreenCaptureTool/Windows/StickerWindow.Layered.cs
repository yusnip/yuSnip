using System;
using NativeMethods = ScreenCaptureTool.Platform.NativeMethods;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图呈现层替换第 1 步（2026-09-28）：图片贴图的像素改由 <see cref="LayeredPinSurface"/> 呈现，
/// 本窗口（Avalonia）退成叠在它<b>正上方</b>的透明交互层。
///
/// <para><b>唯一的数据通道</b>是 <see cref="TryBuildPinFrame"/>：它按 Avalonia 自己的几何口径
/// （窗口原点 + RootGrid 外边距 + RootGrid 的缩放变换 + 各层 Margin/显式宽高）换算出屏幕物理矩形。
/// 读的是<b>属性真值</b>而不是 <c>Bounds</c>——布局要到下一帧才落，读 Bounds 会让呈现层晚一拍。
/// 各条几何通路（滚轮、补帧、拖边、平移、换图、收口）一行未改，它们对这几层的任何写入都会经
/// <see cref="RequestPinSync"/> 合并成"本次调度作业结束后提交一帧"。</para>
///
/// <para><b>Avalonia 侧的四层</b>（图片 / 阴影 / 棋盘格 / 底色）在呈现层接管后只把 <c>Opacity</c> 置 0：
/// 合成器对 <c>Opacity == 0</c> 的视觉直接跳过绘制（11.2.8 <c>ServerCompositionVisual.Render</c> 首行），
/// 30 档光晕的 CPU 遮罩与大图纹理上传一并消失；命中测试不看不透明度，交互一行不变。
/// 退回（缩略图、文本贴图、开关关闭、格式不认）时只要把不透明度还原，旧通路逐字未改。</para>
/// </summary>
public partial class StickerWindow
{
    /// <summary>贴图升层的统一上界：真实截图遮罩优先，其次是当前可见的贴图取色窗。</summary>
    private static IntPtr ActiveFloatingToolHwnd
    {
        get
        {
            IntPtr overlay = CaptureWindow.ActiveOverlayHwnd;
            return overlay != IntPtr.Zero ? overlay : StickerColorPickerWindow.ActivePickerHwnd;
        }
    }

    /// <summary>复用原窗口过程约束带顶升层，覆盖原生激活／Show 先于托管事件的阶段。</summary>
    internal static void KeepStickerBelowFloatingTools(IntPtr windowPos)
    {
        if (windowPos == IntPtr.Zero) return;
        IntPtr boundary = ActiveFloatingToolHwnd;
        if (boundary == IntPtr.Zero) return;
        var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(windowPos);
        if ((pos.flags & NativeMethods.SWP_NOZORDER) != 0
            || (pos.hwndInsertAfter != IntPtr.Zero && pos.hwndInsertAfter != NativeMethods.HWND_TOPMOST)) return;
        // 只约束带顶请求，不改降置顶、几何、显隐或呈现窗相对所属交互窗的排序。
        pos.hwndInsertAfter = boundary;
        pos.flags |= 0x0200; // SWP_NOOWNERZORDER
        System.Runtime.InteropServices.Marshal.StructureToPtr(pos, windowPos, false);
    }

    /// <summary>按最近新建／编辑／点击的顺序升层；只重排当前置顶带，不改变框架的置顶状态或焦点。</summary>
    private void BringStickerToFront()
    {
        if (_isClosed || !IsVisible) return;
        IntPtr owner = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (owner == IntPtr.Zero) return;
        RaiseStickerHandle(owner);
        // 图片呈现窗与透明交互窗必须成对排列；文本贴图没有独立呈现窗。
        _pin?.SyncZOrder();
    }

    private static bool RaiseStickerHandle(IntPtr owner)
    {
        // 有截图遮罩／贴图取色窗时只升到其下方，否则按最近交互升到现有带顶部。
        // NOOWNERZORDER 防止共享隐藏 owner 连带重排其他贴图，呈现窗仍由 SyncZOrder 钉在交互窗之下。
        const uint NoOwnerZOrder = 0x0200;
        return NativeMethods.SetWindowPos(owner, ActiveFloatingToolHwnd, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NoOwnerZOrder);
    }

    private LayeredPinSurface? _pin;
    private bool _pinActive;
    private bool _pinSyncQueued;
    private bool _pinUnsupported;
    private bool _pinHooked;
    private int _pinSyncRetry;
    private DispatcherTimer? _pinSettleTimer;

    /// <summary>几何对不齐时最多补排多少次同步（见 <see cref="SchedulePinSyncRetry"/>）。
    /// 够覆盖"托管尺寸已写、Avalonia 还没把它下发到原生窗口"的那几拍；超出即放弃本帧，
    /// 交给下一次真实窗口几何变化（<c>WM_WINDOWPOSCHANGED</c>）或窗口激活重新对齐。</summary>
    private const int PinSyncMaxRetry = 8;

    /// <summary>交互窗句柄 → 宿主窗口（窗口过程里按句柄找回实例：z 序、显隐与几何重对齐都要用到；
    /// 只在 UI 线程读写）。<b>值存宿主而不是呈现窗</b>——几何重对齐必须回到
    /// <see cref="RequestPinSync"/>（唯一的数据通道在宿主侧），只拿到呈现窗是算不出新矩形的。</summary>
    private static readonly Dictionary<IntPtr, StickerWindow> PinByOwner = new();

    /// <summary>呈现层此刻是否在接管图片像素（几何口径的分支判据一律读它）。</summary>
    private bool PinLayeredActive => _pinActive;

    /// <summary>缩放上限可放开到 5 倍的条件：本贴图走（或将走）分层呈现。用"资格"而不是"此刻已接管"：
    /// 开窗那一刻呈现窗还没建，按已接管判会把初始缩放先夹回纹理上限。缩略图不在此列（它退回 Avalonia 呈现）。</summary>
    private bool LayeredZoomUncapped =>
        StickerStyling.LayeredPinSurface && !_pinUnsupported && _bitmap != null
        && StickerImage != null && StickerImage.IsVisible && !_isThumbnailMode;

    private bool PinEligible =>
        StickerStyling.LayeredPinSurface && !_pinUnsupported && _handleReady && !_isClosed
        && _bitmap != null && !_isThumbnailMode
        && (_isCanvasMode && _canvasView != null || StickerImage is { IsVisible: true, Source: Bitmap });

    /// <summary>在 <c>OnWindowOpened</c> 末尾调用一次：挂上属性变化订阅并提交首帧。</summary>
    private void InitLayeredPinSurface()
    {
        if (!StickerStyling.LayeredPinSurface || _pinHooked) return;
        _pinHooked = true;

        void Watch(Control? c)
        {
            if (c == null) return;
            c.PropertyChanged += (_, e) =>
            {
                // 自己写的不透明度与布局回填的 Bounds 不构成几何变化
                if (e.Property == Visual.OpacityProperty || e.Property == Visual.BoundsProperty) return;
                RequestPinSync();
            };
        }
        Watch(StickerImage);
        Watch(ShadowBorder);
        if (ShadowBorder != null) ShadowBorder.Changed += RequestPinSync;
        Watch(CheckerboardBg);
        Watch(ImageBackdrop);
        Watch(RootGrid);

        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) _pin?.SetOwnerVisible(IsVisible);
            else if (e.Property == WidthProperty || e.Property == HeightProperty
                     || e.Property == ClientSizeProperty) RequestPinSync();
        };
        // 焦点回到本贴图（Alt+Tab、从设置窗切回…）即强制重对齐一帧：这些路径上窗口几何可能
        // 已被系统/布局改过，而当时那次同步恰好撞上"托管尺寸与原生窗口不一致"的守卫被跳过了
        // ⇒ 呈现窗停在旧矩形、交互窗已在新矩形＝用户报的"看得到的点不到"（issue #52）。
        Activated += (_, _) => ForcePinResync();
        PositionChanged += (_, _) => RequestPinSync();   // 挪窗后与上一帧逐位相同则零重画（见 LayeredPinSurface.Commit）
        ActualThemeVariantChanged += (_, _) => RequestPinSync();
        RequestPinSync();
    }

    /// <summary>合并提交：同一次调度作业里的任意多次几何写入只画一帧，在该作业结束后立刻执行。</summary>
    private void RequestPinSync()
    {
        if (!StickerStyling.LayeredPinSurface || _pinSyncQueued || _isClosed || !_pinHooked) return;
        _pinSyncQueued = true;
        _pinSyncRetry = 0;   // 新的几何写入：重试预算重新开张（旧的那串重试针对的是已被取代的几何）
        // Normal 优先级：高于 Render/Input，同一批几何写入（多在渲染帧回调里）结束后紧接着提交
        Dispatcher.UIThread.Post(SyncPinNow, DispatcherPriority.Normal);
    }

    /// <summary>
    /// 强制重对齐一帧，<b>不信任 <c>_hasLast</c> 的逐位判等</b>（<see cref="LayeredPinSurface.Invalidate"/>）。
    ///
    /// <para>用在"几何可能已被别处改过、但本侧没收到对应属性变化"的汇合点（窗口激活、
    /// <c>WM_WINDOWPOSCHANGED</c>）。普通 <see cref="RequestPinSync"/> 算出的矩形若与上一帧逐位相同就零操作，
    /// 而错位态下"上一帧"记的恰好是**错的**那个矩形——它与新算出的真值不同则本来就会重画，
    /// 相同则说明呈现窗其实没动过、而它的真实位置已被外力挪走 ⇒ 必须跳过判等真打一帧。</para>
    /// </summary>
    private void ForcePinResync()
    {
        if (!StickerStyling.LayeredPinSurface || _isClosed || !_pinHooked) return;
        _pin?.Invalidate();
        RequestPinSync();
    }

    /// <summary>几何此刻还对不齐（托管尺寸已改、原生窗口尚未跟上）时补排一次同步。
    /// <b>不可改回静默 return</b>：那会让呈现窗停在上一帧的矩形，而交互窗（命中区）已经在新矩形上
    /// ——两者分家正是 issue #52 的"视觉在 A、点击在 B"。重试有限次（<see cref="PinSyncMaxRetry"/>），
    /// 用完即交给下一次真实几何变化或窗口激活重新对齐，不会无限自排。</summary>
    private void SchedulePinSyncRetry(string reason)
    {
        if (_isClosed || _pinSyncQueued) return;
        if (_pinSyncRetry >= PinSyncMaxRetry)
        {
            WheelDiag($"分层呈现：几何对齐重试用尽（{reason}），本帧放弃，等下一次几何变化/激活重对齐");
            return;
        }
        _pinSyncRetry++;
        _pinSyncQueued = true;
        // Render 优先级：排在布局之后——等的就是"Avalonia 把托管尺寸下发到原生窗口"这一拍
        Dispatcher.UIThread.Post(SyncPinNow, DispatcherPriority.Render);
    }

    private void SyncPinNow()
    {
        _pinSyncQueued = false;
        if (_isClosed) return;
        if (!PinEligible)
        {
            DeactivatePin();
            return;
        }

        if (_pin == null)
        {
            IntPtr owner = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _pin = LayeredPinSurface.TryCreate(owner);
            if (_pin == null)
            {
                _pinUnsupported = true;
                return;
            }
            PinByOwner[owner] = this;
            _pin.SetOwnerVisible(IsVisible);
            WheelDiag("分层呈现：已创建呈现窗 hwnd=" + _pin.Handle);
        }

        if (!(_isCanvasMode ? SetCanvasPinPictures(_pin) : StickerImage.Source is Bitmap bmp && _pin.SetImage(bmp)))
        {
            _pinUnsupported = true;
            WheelDiag("分层呈现：源图格式不支持，本贴图退回 Avalonia 呈现");
            DeactivatePin();
            return;
        }
        // 算不出本帧几何（托管与原生窗口暂不一致、尺寸还没量出来）：补排重试，**不能就这么放着**
        // ——呈现窗会停在上一帧矩形上，而交互窗的命中区已经在新矩形上（issue #52）。
        if (!TryBuildPinFrame(out var frame))
        {
            SchedulePinSyncRetry(_pinFrameSkipReason);
            return;
        }

        // ULW 提交失败同样要重试：失败时呈现窗仍显示着上一帧的像素与位置，而 _last 已被
        // Commit 保护性地作废，下一次普通同步不会因为"逐位相同"而跳过，但得先有那一次同步。
        if (!_pin.Commit(frame))
        {
            SchedulePinSyncRetry("ULW 提交失败");
            return;
        }
        if (!_pinActive)
        {
            // 先让呈现窗出帧、再藏 Avalonia 的像素层：两者之间最多重叠一帧（同一张图叠同一张图），不会空一帧
            _pinActive = true;
            SetAvaloniaPixelLayersHidden(true);
            WheelDiag("分层呈现：接管图片像素");
        }
    }

    private void DeactivatePin()
    {
        if (!_pinActive) { _pin?.Hide(); return; }
        _pinActive = false;
        SetAvaloniaPixelLayersHidden(false);
        _pin?.Hide();
        WheelDiag("分层呈现：交回 Avalonia 呈现"
                  + (_isThumbnailMode ? "（缩略图）" : StickerImage?.IsVisible != true ? "（非图片内容）" : ""));
    }

    private void SetAvaloniaPixelLayersHidden(bool hidden)
    {
        double o = hidden ? 0.0 : 1.0;
        if (StickerImage != null) StickerImage.Opacity = o;
        // 阴影层与 RootBorder 是兄弟、不吃 RootBorder 的不透明度，退路下单独乘上贴图不透明度
        if (ShadowBorder != null) ShadowBorder.Opacity = hidden ? 0.0 : _stickerOpacity;
        if (CheckerboardBg != null) CheckerboardBg.Opacity = o;
        if (ImageBackdrop != null) ImageBackdrop.Opacity = o;
        if (CardHighlightBorder != null) CardHighlightBorder.Opacity = o;
        if (_canvasView != null) _canvasView.Opacity = o;
    }

    /// <summary>
    /// 按 Avalonia 的几何口径换算本帧要画的一切（屏幕物理像素）。
    /// 变换：RootGrid 局部 DIP 点 p → 屏幕 = RootGrid 原点 + (O + (p − O)·v)·s，
    /// 其中 O＝<c>RenderTransformOrigin</c> 换算到 RootGrid 尺寸上的点、v＝补帧倍率、s＝DPI。
    /// RootGrid 尺寸一律取<b>窗口真实矩形</b>减两侧阴影边距（与 <c>BeginZoomEase</c> 算变换原点同一口径）。
    /// </summary>
    private bool TryBuildPinFrame(out PinFrame f)
    {
        if (_isCanvasMode) return TryBuildCanvasPinFrame(out f);
        f = default;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || !ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            _pinFrameSkipReason = "取不到窗口矩形";
            return false;
        }
        double s = SafeScaling;
        // 静止态托管尺寸已改、原生窗口尚未跟上（Avalonia 在下一次布局才下发）：这一帧的窗口矩形是旧的，
        // 按它画会把图片裁掉一截或阴影错位一帧。
        // ⚠ 这里**只许推迟、不许丢弃**（2026-10-01，issue #52）：原写法直接 return 并指望"窗口真改尺寸时
        // ClientSize 变化会再排一次"，但那次变化**可能已经发生过了**——正是它排了当前这一次同步。
        // 于是呈现窗停在旧矩形、交互窗（命中区）已在新矩形 ⇒ 视觉在 A、点击在 B。改由
        // SchedulePinSyncRetry 在 Render 优先级上等布局下发完再提交。
        // 手势/平移态窗口由原生 SetWindowPos 同步改好，不经这条延迟，不受此守卫影响。
        if (!_wheelVisualOverride && !double.IsNaN(Width) && !double.IsNaN(Height)
            && (Math.Abs(Width * s - rc.Width) > 1.5 || Math.Abs(Height * s - rc.Height) > 1.5))
        {
            _pinFrameSkipReason = $"托管({Width * s:F0}x{Height * s:F0})与原生({rc.Width}x{rc.Height})尺寸不一致";
            return false;
        }

        double rootL = rc.Left + ShadowMargin * s, rootT = rc.Top + ShadowMargin * s;
        double rootW = Math.Max(0, rc.Width / s - ShadowMargin * 2);
        double rootH = Math.Max(0, rc.Height / s - ShadowMargin * 2);
        if (rootW <= 0 || rootH <= 0)
        {
            _pinFrameSkipReason = "窗口尺寸不足一圈阴影边距";
            return false;
        }

        double v = _zoomFx != null ? _zoomFx.ScaleX : 1.0;
        if (!(v > 0)) v = 1.0;
        var origin = RootGrid.RenderTransformOrigin;
        double ox = origin.Unit == RelativeUnit.Relative ? origin.Point.X * rootW : origin.Point.X;
        double oy = origin.Unit == RelativeUnit.Relative ? origin.Point.Y * rootH : origin.Point.Y;
        double k = s * v;
        double MapX(double x) => rootL + (ox + (x - ox) * v) * s;
        double MapY(double y) => rootT + (oy + (y - oy) * v) * s;

        // —— 图片（含缩略图那层视口平移；本通路在缩略图下不启用，此项恒 0，写上是为口径完整）——
        double iw = StickerImage.Width, ih = StickerImage.Height;
        if (double.IsNaN(iw) || iw <= 0) iw = StickerImage.Bounds.Width;
        if (double.IsNaN(ih) || ih <= 0) ih = StickerImage.Bounds.Height;
        if (iw <= 0 || ih <= 0)
        {
            _pinFrameSkipReason = "图片尺寸尚未量出";
            return false;
        }
        var im = StickerImage.Margin;
        f.ImgL = MapX(im.Left + _imageTransform.X);
        f.ImgT = MapY(im.Top + _imageTransform.Y);
        f.ImgW = iw * k;
        f.ImgH = ih * k;
        f.Downscale = f.ImgW + 0.5 < _sourcePixelWidth || f.ImgH + 0.5 < _sourcePixelHeight;

        // —— 裁剪：RootBorder/Panel 的 ClipToBounds（RootGrid 局部 (0,0,W,H)，随变换缩放），再交窗口矩形——
        // Avalonia 的一切像素最终都被 HWND 边裁掉；呈现窗不受交互窗边界约束，这里显式补上同一刀，
        // 否则补帧放大中途越出窗口的那一截会"多出来"（且那里点不到）。
        double gL = MapX(0), gT = MapY(0), gR = gL + rootW * k, gB = gT + rootH * k;
        f.ClipL = Math.Max(gL, rc.Left);
        f.ClipT = Math.Max(gT, rc.Top);
        f.ClipR = Math.Min(gR, rc.Right);
        f.ClipB = Math.Min(gB, rc.Bottom);

        // 大图越出视口时，仅物理屏幕外缘可以铺到窗口边；窗口边仍在屏幕内必须留阴影。
        // 贴图允许半移出屏幕，不能再假设“窗口顶满工作区＝每条窗口边都在屏幕外缘”。
        // Screens.All 为框架维护的屏幕读数，不在每帧新增 Win32 枚举；逐屏判断避免把屏间空洞当屏幕。
        double imgR = f.ImgL + f.ImgW, imgB = f.ImgT + f.ImgH;
        bool cutL = f.ImgL < gL - 1, cutT = f.ImgT < gT - 1;
        bool cutR = imgR > gR + 1, cutB = imgB > gB + 1;
        if (cutL || cutT || cutR || cutB)
        {
            var ownerRect = new PixelRect(rc.Left, rc.Top, rc.Width, rc.Height);
            var screens = Screens.All;
            int shadowEdges = screens.Count == 0 ? 15 : 0;
            foreach (var screen in screens) shadowEdges |= GetPinShadowEdges(ownerRect, screen.Bounds);
            double margin = ShadowMargin * s;
            // 补帧变换也可能把 RootGrid 的边推过窗口边，至少保留实际窗口内的一圈阴影间距。
            if (cutL) f.ClipL = (shadowEdges & 1) == 0 ? rc.Left : Math.Max(f.ClipL, rc.Left + margin);
            if (cutT) f.ClipT = (shadowEdges & 2) == 0 ? rc.Top : Math.Max(f.ClipT, rc.Top + margin);
            if (cutR) f.ClipR = (shadowEdges & 4) == 0 ? rc.Right : Math.Min(f.ClipR, rc.Right - margin);
            if (cutB) f.ClipB = (shadowEdges & 8) == 0 ? rc.Bottom : Math.Min(f.ClipB, rc.Bottom - margin);
        }
        // 光晕画在 RootBorder 之外、只受窗口边裁
        f.HaloClipL = rc.Left; f.HaloClipT = rc.Top;
        f.HaloClipR = rc.Right; f.HaloClipB = rc.Bottom;

        // —— 阴影：层矩形（静止态 Stretch 铺满 RootGrid；手势/平移态显式跟随画面）＋三值口径 ——
        if (ShadowBorder != null && ShadowBorder.IsShadowEnabled)
        {
            // 静止态阴影层是 Stretch 铺满 RootGrid，而静止窗＝画面＋阴影边距 ⇒ 与图片同矩形；
            // 直接取图片矩形，免得布局未落时按旧窗口尺寸算出错位的一圈
            double sx = im.Left, sy = im.Top, sw = iw, sh = ih;
            if (!double.IsNaN(ShadowBorder.Width) && !double.IsNaN(ShadowBorder.Height))
            {
                sx = ShadowBorder.Margin.Left; sy = ShadowBorder.Margin.Top;
                sw = ShadowBorder.Width; sh = ShadowBorder.Height;
            }
            var g = ShadowBorder.CardGeometryDip;
            f.ShadowOn = true;
            f.ShL = MapX(sx); f.ShT = MapY(sy); f.ShW = sw * k; f.ShH = sh * k;

            f.InsetPx = g.Inset * k;
            f.RadiusPx = g.Radius * k;
            f.StrokePx = g.Stroke * k;
            // 图片轮廓随补帧倍率缩放；窗体光晕始终按屏幕 DIP 保持扩散和羽化尺寸。
            f.ExtentPx = Controls.StickerShadowLayer.HaloExtentDip * s;
            f.SigmaPx = Controls.StickerShadowLayer.HaloSigmaDip * s;
            f.ShadowArgb = ShadowBorder.ShadowColor.ToUInt32();
        }

        // —— 棋盘格：与 SctCheckerboardBrush 同构，颜色按本窗实际明暗档取 ——
        if (CheckerboardBg != null && CheckerboardBg.IsVisible)
        {
            f.CheckerOn = true;
            var cm = CheckerboardBg.Margin;
            double cw = double.IsNaN(CheckerboardBg.Width) ? iw : CheckerboardBg.Width;
            double ch = double.IsNaN(CheckerboardBg.Height) ? ih : CheckerboardBg.Height;
            f.CkL = MapX(cm.Left); f.CkT = MapY(cm.Top); f.CkW = cw * k; f.CkH = ch * k;
            f.CheckerBgArgb = ThemeColor("SctCheckerboardBgColor", Color.FromRgb(0xF7, 0xF8, 0xFA)).ToUInt32();
            f.CheckerTileArgb = ThemeColor("SctCheckerboardTileColor", Color.FromRgb(0xE9, 0xEC, 0xF8)).ToUInt32();
            f.CheckerCellPx = Math.Max(1, (int)Math.Round(8 * k));
        }

        // —— 底色（取自图片 (8,8) 像素的实底，垫在图片下方）——
        if (ImageBackdrop != null && ImageBackdrop.IsVisible && ImageBackdrop.Background is ISolidColorBrush sb)
        {
            var c = sb.Color;
            byte a = (byte)Math.Round(c.A * Math.Clamp(sb.Opacity, 0, 1));
            if (a > 0)
            {
                f.BackdropOn = true;
                var bm = ImageBackdrop.Margin;
                double bw = double.IsNaN(ImageBackdrop.Width) ? iw : ImageBackdrop.Width;
                double bh = double.IsNaN(ImageBackdrop.Height) ? ih : ImageBackdrop.Height;
                f.BdL = MapX(bm.Left); f.BdT = MapY(bm.Top); f.BdW = bw * k; f.BdH = bh * k;
                f.BackdropArgb = Color.FromArgb(a, c.R, c.G, c.B).ToUInt32();
            }
        }

        // —— 内容主体高亮（带烘焙阴影贴图，高亮紧贴内容卡片、不含烘焙阴影）——
        if (_hasBakedShadow && _bakedShadowPadPx > 0 && (_isFocused || _isLocked || _isMousePenetration) && !_isThumbnailMode)
        {
            double ratio = _sourcePixelWidth > 0 && iw > 0 ? iw / _sourcePixelWidth : _scale / SafeScaling;
            double padDip = _bakedShadowPadPx * ratio;
            double cardW = iw - padDip * 2;
            double cardH = ih - padDip * 2;
            if (cardW > 0 && cardH > 0)
            {
                double rDip = _bakedCardGeometry.ContentRadiusPx * ratio;
                rDip = Math.Min(Math.Max(0, rDip), Math.Min(cardW, cardH) / 2.0);

                f.CardHighlightOn = true;
                f.CardL = MapX(im.Left + _imageTransform.X + padDip);
                f.CardT = MapY(im.Top + _imageTransform.Y + padDip);
                f.CardW = cardW * k;
                f.CardH = cardH * k;
                f.CardR = rDip * k;
                f.CardStroke = _bakedCardGeometry.StrokeWidthPx * ratio * k;
                f.CardHighlightArgb = _isMousePenetration ? StickerStyling.PenetrationShadowColor.ToUInt32()
                                    : _isLocked ? StickerStyling.LockedShadowColor.ToUInt32()
                                    : StickerStyling.FocusedShadowColor.ToUInt32();
            }
        }

        f.ConstantAlpha = StickerOpacityAlpha;
        _pinFrameSkipReason = string.Empty;
        return true;
    }

    /// <summary>上一次 <see cref="TryBuildPinFrame"/> 放弃的原因（只用于诊断落盘与重试日志）。</summary>
    private string _pinFrameSkipReason = string.Empty;

    // 位掩码：左／上／右／下为 1／2／4／8；只有边外确有屏幕像素时，裁切边才需要可见阴影。
    private static int GetPinShadowEdges(PixelRect owner, PixelRect screen)
    {
        int edges = 0;
        bool vertical = screen.Y < owner.Bottom && screen.Bottom > owner.Y;
        bool horizontal = screen.X < owner.Right && screen.Right > owner.X;
        // 1px 容差吸收几何取整；相邻屏也能提供边外空间，但不跨越真实的屏间空洞。
        if (vertical && screen.X < owner.X - 1 && screen.Right >= owner.X - 1) edges |= 1;
        if (horizontal && screen.Y < owner.Y - 1 && screen.Bottom >= owner.Y - 1) edges |= 2;
        if (vertical && screen.Right > owner.Right + 1 && screen.X <= owner.Right + 1) edges |= 4;
        if (horizontal && screen.Bottom > owner.Bottom + 1 && screen.Y <= owner.Bottom + 1) edges |= 8;
        return edges;
    }

    private Color ThemeColor(string key, Color fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out object? o) && o is Color c) return c;
        return fallback;
    }

    /// <summary>滚轮停下 <see cref="StickerStyling.LayeredWheelSettleMs"/> 后自动收口手势（仅分层呈现档）。
    /// 收口让窗口回到严格贴合、各层落回真排版，拖动随之回到"只挪窗口"的最快通路。</summary>
    private void SchedulePinWheelSettle()
    {
        if (!PinLayeredActive) return;
        if (_pinSettleTimer == null)
        {
            _pinSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StickerStyling.LayeredWheelSettleMs) };
            _pinSettleTimer.Tick += (_, _) =>
            {
                _pinSettleTimer.Stop();
                if (_isClosed || !_wheelGestureActive || !_wheelVisualOverride) return;
                if (_isAnnotating || _isResizing || _isDragging) return;   // 这几种手势自己收尾，松手时会再排一次
                if (_zoomEasing || _wheelApplyRafScheduled) { _pinSettleTimer.Start(); return; }
                CommitWheelGesture("滚轮静止");
            };
        }
        _pinSettleTimer.Stop();
        _pinSettleTimer.Start();
    }

    /// <summary>拖动挪窗：呈现窗在位时与交互窗放进同一批 <c>DeferWindowPos</c>，本帧零重画。</summary>
    private bool TryMoveWithPin(int x, int y, int dx, int dy)
        => _pinActive && _pin != null && _pin.MoveWithOwner(x, y, dx, dy);

    /// <summary>交互窗窗口过程里 <c>WM_WINDOWPOSCHANGED</c> 的后处理：z 序一变就把呈现窗钉回自己正下方；
    /// 原生显隐同步到呈现窗（托盘隐藏全部等不经 Avalonia 属性的路径）；
    /// <b>位置或尺寸真的变了就强制重对齐一帧几何</b>。
    ///
    /// <para>最后一条是 issue #52 的收口（2026-10-01）：这条消息是"原生窗口几何已经变了"的
    /// <b>唯一权威信号</b>——它覆盖了所有不经 Avalonia 属性的改动（系统在激活/z 序事务里挪窗、
    /// 外部 <c>SetWindowPos</c>、DPI 变化、我们自己那些刻意不写 <c>Position</c> 的原生通路）。
    /// 原先只在这里管 z 序与显隐，几何全靠托管属性变化去推，于是凡是"原生动了、托管没动"的
    /// 那几条路径上呈现窗就留在旧矩形上，而命中区跟着真实窗口走 ⇒ 视觉与点击分家。</para></summary>
    private static void OnOwnerWindowPosChanged(IntPtr hwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero || !PinByOwner.TryGetValue(hwnd, out var owner)) return;
        var pin = owner._pin;
        if (pin == null) return;
        const int FlagsOffset = 32;   // WINDOWPOS{hwnd,hwndInsertAfter,x,y,cx,cy,flags}，x64 下 flags 在 +32
        uint flags = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam, FlagsOffset);
        if ((flags & 0x0080) != 0) pin.SetOwnerVisible(false);        // SWP_HIDEWINDOW
        else if ((flags & 0x0040) != 0) pin.SetOwnerVisible(true);    // SWP_SHOWWINDOW
        if ((flags & 0x0004) == 0) pin.SyncZOrder();                  // 没带 SWP_NOZORDER＝z 序可能变了

        // SWP_NOMOVE(0x0002) 与 SWP_NOSIZE(0x0001) 都带＝几何没动，无需重对齐（挪窗/缩放的绝大多数
        // 帧走的是这条，开销为零）。任一没带就按"几何已变"处理：RequestPinSync 自己会与上一帧逐位判等，
        // 真没变化时仍是零重画，这里只负责"不漏排"。
        if ((flags & 0x0003) != 0x0003) owner.RequestPinSync();
    }

    /// <summary>本段呈现层采证（并复位），拼在"缩放段采证 / 拖拽段采证"那一行末尾。</summary>
    private string TakePinStats() => _pin?.TakeStats() ?? string.Empty;

    /// <summary>
    /// 呈现窗与交互窗的几何对账（issue #52 的看门狗，2026-10-01）：按当前真实窗口矩形重算一帧，
    /// 与呈现窗<b>实际</b>所在的矩形比；差超 2px 即判为错位，强制重对齐并留痕。
    ///
    /// <para>与 <see cref="ForcePinResync"/> 的分工：那个是"到了该重对齐的时机就无条件打一帧"，
    /// 这个是"查有没有真的错位、错了才动手并记一条可据以复盘的日志"。挂在指针按下上——
    /// 用户感知错位的那一刻正是他点下去的那一刻，在这里兜住就等于"点第一下就自愈"。</para>
    /// </summary>
    private void VerifyPinAlignmentOnInput()
    {
        if (!_pinActive || _pin == null || _isClosed) return;
        if (!_pin.TryGetCommittedRect(out int cl, out int ct, out int cw, out int ch)) return;
        if (!TryBuildPinFrame(out var f))
        {
            // 这一刻算不出几何：按既有口径补排重试（别在对账里顺手画一帧错的）
            SchedulePinSyncRetry(_pinFrameSkipReason);
            return;
        }
        if (!_pin.TryPlanRect(f, out int wl, out int wt, out int ww, out int wh)) return;
        if (Math.Abs(cl - wl) <= 2 && Math.Abs(ct - wt) <= 2
            && Math.Abs(cw - ww) <= 2 && Math.Abs(ch - wh) <= 2) return;

        WheelDiag($"分层呈现：检出错位（呈现窗({cl},{ct}){cw}x{ch} 应为({wl},{wt}){ww}x{wh}）→ 强制重对齐");
        ForcePinResync();
    }

    /// <summary>设置页切换"透明图片背景"后，让所有已贴出的贴图当场按新档位重算背衬层（只动显示）。</summary>
    public static void RefreshAllAlphaBacking()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var w in desktop.Windows)
        {
            if (w is StickerWindow sw && !sw._isClosed) sw.ApplyShadow();
        }
    }

    private void DisposeLayeredPinSurface()
    {
        _pinSettleTimer?.Stop();
        if (_pin == null) return;
        foreach (var kv in PinByOwner)
        {
            if (ReferenceEquals(kv.Value, this)) { PinByOwner.Remove(kv.Key); break; }
        }
        _pin.Dispose();
        _pin = null;
        _pinActive = false;
    }
}
