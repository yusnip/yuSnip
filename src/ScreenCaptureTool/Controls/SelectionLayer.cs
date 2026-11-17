using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
// 轮廓点沿用 Core 的 System.Drawing 口径；只引这两个类型，整体 using 会让 Point 与 Avalonia 的同名类型打架。
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;
using SelectionEffects = ScreenCaptureTool.Core.Capture.SelectionEffects;
using ScreenCaptureTool.Core;
// 选框 Pen 与暗窗浓度已上提到这一份共享件（录屏预览外框/录制暗化共用同一实现），本层只是第一个消费方
using SelVisuals = ScreenCaptureTool.Services.SelectionVisuals;

#if LONGSCROLL_SELECTION_LAYER
namespace ScreenCaptureTool.LongScroll.Controls;
#else
namespace ScreenCaptureTool.Controls;
#endif

/// <summary>
/// 选区视觉层：4 矩形遮罩（半透明黑）+ 选区边框（白色虚线）。
/// 选框四周控制手柄方块默认不显示以保持视觉清爽，手柄及选框四周调整缩放交互逻辑仍完整保留在交互层。
///
/// 阶段 T4.5d 产物，迁移自旧 <c>01_核心内核/SelectionLayer.cs</c>（WPF FrameworkElement + DrawingVisual）。
/// 主要改动：
/// - 改为继承 <see cref="Control"/>（Avalonia 11.x 的轻量自绘基类）
/// - <c>OnRender</c>（旧版 RenderOpen + DrawingContext）→ Avalonia 的 <see cref="Render(DrawingContext)"/>
/// - WPF 的 <c>DependencyProperty</c> → Avalonia 的 <see cref="StyledProperty{T}"/>
/// - 不再 Freeze() Brush/Pen，Avalonia 默认就能跨线程访问
///
/// 视觉参数（角手柄 6×18 L 形、边手柄 8×22）严格沿用旧版尺寸，保持视觉一致。
///
/// 设计原则：
/// - 控件尺寸由父布局给（CaptureWindow 内 Stretch）
/// - <see cref="Region"/> 是 DIP 单位、相对窗口左上角的坐标，调用方负责物理像素 ↔ DIP 换算
/// - 改 <see cref="Region"/> 时自动 InvalidateVisual（通过 AffectsRender）
/// </summary>
public sealed class SelectionLayer : Control
{
    // 路径选区由编辑器在输入变化时构造；矩形分支保持原缓存和效果算法。
    public Geometry? PathOutline { get; private set; }
    private Geometry? _pathMask;
    public void SetPathGeometry(Geometry? outline, Geometry? mask)
    {
        PathOutline = outline; _pathMask = mask; InvalidateVisual();
    }
    private bool _renderDiagnostics;
    private long _diagnosticStart;
    private int _renderCount;
    private long _renderTotalTicks, _renderMaxTicks;
    private int _maskBuildCount;
    private long _maskBuildTotalTicks, _maskBuildMaxTicks;
    private bool _firstRegionRecorded;
    private string _diagnosticEntry = "";
    private string? _firstRenderRecord, _firstRegionRecord;

    internal void BeginRenderDiagnostics(long startTimestamp, string entry)
    {
        _renderDiagnostics = AppLogger.DetailedLoggingEnabled;
        _diagnosticStart = startTimestamp;
        _diagnosticEntry = entry;
        _renderCount = _maskBuildCount = 0;
        _renderTotalTicks = _renderMaxTicks = _maskBuildTotalTicks = _maskBuildMaxTicks = 0;
        _firstRegionRecorded = false;
        _firstRenderRecord = _firstRegionRecord = null;
    }

    internal void FinishRenderDiagnostics()
    {
        if (!_renderDiagnostics) return;
        _renderDiagnostics = false;
        // Render 内只留记录，撤幕后再写同步日志，避免取证阻塞首帧绘制。
        if (_firstRenderRecord != null) { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug(_firstRenderRecord); }
        if (_firstRegionRecord != null) { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug(_firstRegionRecord); }
        _firstRenderRecord = _firstRegionRecord = null;
        double ms = 1000.0 / Stopwatch.Frequency;
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF CaptureMask.Summary entry={_diagnosticEntry} records={_renderCount} "
            + $"recordAvg={_renderTotalTicks * ms / Math.Max(1, _renderCount):F3}ms recordMax={_renderMaxTicks * ms:F3}ms "
            + $"geometryBuilds={_maskBuildCount} geometryAvg={_maskBuildTotalTicks * ms / Math.Max(1, _maskBuildCount):F3}ms "
            + $"geometryMax={_maskBuildMaxTicks * ms:F3}ms (CPU绘制指令记录，不含GPU上屏)");
    }

    private Rect _animatedHoverRegion;
    private Rect _animatingTargetRegion;
    private Avalonia.Threading.DispatcherTimer? _animationTimer;
    private long _lastTickTime;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AutoHoverRegionProperty)
        {
            Rect target = (Rect)change.NewValue!;
            if (target.Width <= 0 || target.Height <= 0)
            {
                _animatedHoverRegion = default;
                _animatingTargetRegion = default;
                StopAnimationTimer();
                InvalidateVisual();
            }
            else
            {
                _animatingTargetRegion = target;
                if (_animatedHoverRegion.Width <= 0 || _animatedHoverRegion.Height <= 0)
                {
                    // 第一次出现，或者重新显示：直接对齐，不要出现滑过来的动画
                    _animatedHoverRegion = target;
                    StopAnimationTimer();
                    InvalidateVisual();
                }
                else
                {
                    // 已经在显示，且目标变了：启动平滑过渡
                    StartAnimationTimer();
                }
            }
        }
    }

    private void StartAnimationTimer()
    {
        if (_animationTimer == null)
        {
            int maxHz = ScreenCaptureTool.Platform.MonitorHelper.GetMaxDisplayFrequency();
            int intervalMs = Math.Clamp(1000 / Math.Max(1, maxHz), 2, 16);
            _animationTimer = new Avalonia.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(intervalMs),
                Avalonia.Threading.DispatcherPriority.Render, // 提升渲染优先级
                OnAnimationTick);
        }

        if (!_animationTimer.IsEnabled)
        {
            _lastTickTime = System.Diagnostics.Stopwatch.GetTimestamp();
            _animationTimer.Start();
        }
    }

    private void StopAnimationTimer()
    {
        _animationTimer?.Stop();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        Rect target = _animatingTargetRegion;
        if (target.Width <= 0 || target.Height <= 0)
        {
            _animatedHoverRegion = default;
            StopAnimationTimer();
            InvalidateVisual();
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsedMs = (double)(now - _lastTickTime) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        _lastTickTime = now;
        
        if (elapsedMs > 50) elapsedMs = 16;
        if (elapsedMs <= 0) elapsedMs = 1;

        // Exponential smoothing (lerp) - 产生自然顺滑的过渡弹性
        double speed = 0.04; 
        double factor = 1.0 - Math.Exp(-speed * elapsedMs);

        double dx = target.X - _animatedHoverRegion.X;
        double dy = target.Y - _animatedHoverRegion.Y;
        double dw = target.Width - _animatedHoverRegion.Width;
        double dh = target.Height - _animatedHoverRegion.Height;

        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5 && Math.Abs(dw) < 0.5 && Math.Abs(dh) < 0.5)
        {
            _animatedHoverRegion = target;
            StopAnimationTimer();
        }
        else
        {
            _animatedHoverRegion = new Rect(
                _animatedHoverRegion.X + dx * factor,
                _animatedHoverRegion.Y + dy * factor,
                _animatedHoverRegion.Width + dw * factor,
                _animatedHoverRegion.Height + dh * factor);
        }

        InvalidateVisual();
    }

    /// <summary>当前选区（DIP）。空 Rect 表示无选区，仅画整屏遮罩。</summary>
    public static readonly StyledProperty<Rect> RegionProperty =
        AvaloniaProperty.Register<SelectionLayer, Rect>(nameof(Region));

    /// <summary>是否画选区边框与 8 手柄（false 时只画遮罩，用于初始拖框尚未释放鼠标的过渡态）。</summary>
    public static readonly StyledProperty<bool> ShowBorderAndHandlesProperty =
        AvaloniaProperty.Register<SelectionLayer, bool>(nameof(ShowBorderAndHandles), defaultValue: true);

    /// <summary>是否画选区外遮罩。长截图边框窗口可关闭遮罩，只复用边框/手柄绘制。</summary>
    public static readonly StyledProperty<bool> ShowMaskProperty =
        AvaloniaProperty.Register<SelectionLayer, bool>(nameof(ShowMask), defaultValue: true);

    /// <summary>自动检测候选框（DIP）。候选框与正式选区分离，单击确认后才进入 Region。</summary>
    public static readonly StyledProperty<Rect> AutoHoverRegionProperty =
        AvaloniaProperty.Register<SelectionLayer, Rect>(nameof(AutoHoverRegion));

    /// <summary>鼠标当前位置（DIP），用于在缩放/平移过渡中拉引检测框中心</summary>
    public static readonly StyledProperty<Point?> MousePositionProperty =
        AvaloniaProperty.Register<SelectionLayer, Point?>(nameof(MousePosition));

    /// <summary>竖参考线的 x 坐标（DIP）。空数组 = 不画参考线。</summary>
    public static readonly StyledProperty<double[]> GuidelineVerticalsProperty =
        AvaloniaProperty.Register<SelectionLayer, double[]>(nameof(GuidelineVerticals), Array.Empty<double>());

    /// <summary>横参考线的 y 坐标（DIP）。空数组 = 不画参考线。</summary>
    public static readonly StyledProperty<double[]> GuidelineHorizontalsProperty =
        AvaloniaProperty.Register<SelectionLayer, double[]>(nameof(GuidelineHorizontals), Array.Empty<double>());

    /// <summary>贯穿整屏的中心准星中心点（DIP）。null = 不画。</summary>
    public static readonly StyledProperty<Point?> CrosshairProperty =
        AvaloniaProperty.Register<SelectionLayer, Point?>(nameof(Crosshair));

    /// <summary>中心准星竖线分段对比色（0=黑, 1=白, 2=品牌紫）。null/空 = 不画。</summary>
    public static readonly StyledProperty<byte[]?> CrosshairSegmentsVProperty =
        AvaloniaProperty.Register<SelectionLayer, byte[]?>(nameof(CrosshairSegmentsV));

    /// <summary>中心准星横线分段对比色（0=黑, 1=白, 2=品牌紫）。null/空 = 不画。</summary>
    public static readonly StyledProperty<byte[]?> CrosshairSegmentsHProperty =
        AvaloniaProperty.Register<SelectionLayer, byte[]?>(nameof(CrosshairSegmentsH));

    /// <summary>自绘的十字瞄准光标中心（DIP）。系统光标同时隐藏，故这个点就是用户的实际指向。null = 不画。</summary>
    public static readonly StyledProperty<Point?> AimCursorProperty =
        AvaloniaProperty.Register<SelectionLayer, Point?>(nameof(AimCursor));

    /// <summary>成品圆角半径（<b>物理像素</b>，与设置里存的是同一个数）。0 = 直角。</summary>
    public static readonly StyledProperty<int> EffectRadiusPxProperty =
        AvaloniaProperty.Register<SelectionLayer, int>(nameof(EffectRadiusPx));

    /// <summary>是否在预览里画投影。</summary>
    public static readonly StyledProperty<bool> EffectShadowProperty =
        AvaloniaProperty.Register<SelectionLayer, bool>(nameof(EffectShadow));

    /// <summary>成品描边宽度（<b>物理像素</b>，与设置里存的是同一个数）。0 = 不描边。</summary>
    public static readonly StyledProperty<int> EffectStrokePxProperty =
        AvaloniaProperty.Register<SelectionLayer, int>(nameof(EffectStrokePx));

    /// <summary>成品描边颜色（ARGB 整数，与成品侧 <c>SelectionEffects.Create</c> 收到的是同一个数）。默认品牌紫。</summary>
    public static readonly StyledProperty<int> EffectStrokeArgbProperty =
        AvaloniaProperty.Register<SelectionLayer, int>(nameof(EffectStrokeArgb),
            defaultValue: ScreenCaptureTool.Core.Capture.SelectionEffects.DefaultStrokeArgb);

    /// <summary>
    /// 是否在预览里画描边与阴影。<b>只决定画不画，不参与几何口径</b>——宽度/颜色/阴影开关这些真值
    /// 始终照常下发，于是闸门反复开合时缓存口径不变、一次都不重建。旧实现把闸门做进了宽度与开关本身
    /// （关闭时下发 0/false），每关一次就把几十条几何连同其背后的原生 path 一起丢掉、下一次悬浮再整轮
    /// 重建（探针实测开帧 ~2.9ms，一轮重建抛弃 ~70 个几何/画笔对象）。
    /// </summary>
    public static readonly StyledProperty<bool> EffectsVisibleProperty =
        AvaloniaProperty.Register<SelectionLayer, bool>(nameof(EffectsVisible));

    /// <summary>物理像素 → DIP 的折算系数（= 1 / 窗口缩放）。投影的模糊与偏移按它换算，预览才是成品的等比复现。</summary>
    public static readonly StyledProperty<double> ScreenToDipScaleProperty =
        AvaloniaProperty.Register<SelectionLayer, double>(nameof(ScreenToDipScale), defaultValue: 1.0);

    /// <summary>截图选框边框线型："Dashed"（虚线，默认）| "Solid"（实线）。</summary>
    public static readonly StyledProperty<string> SelectionBorderStyleProperty =
        AvaloniaProperty.Register<SelectionLayer, string>(nameof(SelectionBorderStyle),
            defaultValue: AppSettings.SelectionBorderStyleDashed);

    /// <summary>截图选框边框颜色（ARGB 整数）。出厂默认纯白 #FFFFFF。</summary>
    public static readonly StyledProperty<int> SelectionBorderColorArgbProperty =
        AvaloniaProperty.Register<SelectionLayer, int>(nameof(SelectionBorderColorArgb),
            defaultValue: AppSettings.DefaultSelectionBorderColorArgb);

    /// <summary>截图选框边框粗细（像素，0~5，默认 2；0 表示不画边框）。</summary>
    public static readonly StyledProperty<int> SelectionBorderThicknessProperty =
        AvaloniaProperty.Register<SelectionLayer, int>(nameof(SelectionBorderThickness),
            defaultValue: AppSettings.DefaultSelectionBorderThickness);

    static SelectionLayer()
    {
        // 这些属性变化都要触发重绘
        AffectsRender<SelectionLayer>(RegionProperty, ShowBorderAndHandlesProperty, ShowMaskProperty, AutoHoverRegionProperty,
            GuidelineVerticalsProperty, GuidelineHorizontalsProperty, CrosshairProperty,
            CrosshairSegmentsVProperty, CrosshairSegmentsHProperty, AimCursorProperty,
            EffectRadiusPxProperty, EffectShadowProperty, EffectStrokePxProperty, EffectStrokeArgbProperty,
            EffectsVisibleProperty, ScreenToDipScaleProperty,
            SelectionBorderStyleProperty, SelectionBorderColorArgbProperty, SelectionBorderThicknessProperty);
    }

    public Rect Region
    {
        get => GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    public bool ShowBorderAndHandles
    {
        get => GetValue(ShowBorderAndHandlesProperty);
        set => SetValue(ShowBorderAndHandlesProperty, value);
    }

    public bool ShowMask
    {
        get => GetValue(ShowMaskProperty);
        set => SetValue(ShowMaskProperty, value);
    }

    public Rect AutoHoverRegion
    {
        get => GetValue(AutoHoverRegionProperty);
        set => SetValue(AutoHoverRegionProperty, value);
    }

    public Point? MousePosition
    {
        get => GetValue(MousePositionProperty);
        set => SetValue(MousePositionProperty, value);
    }

    public double[] GuidelineVerticals
    {
        get => GetValue(GuidelineVerticalsProperty);
        set => SetValue(GuidelineVerticalsProperty, value);
    }

    public double[] GuidelineHorizontals
    {
        get => GetValue(GuidelineHorizontalsProperty);
        set => SetValue(GuidelineHorizontalsProperty, value);
    }

    public Point? Crosshair
    {
        get => GetValue(CrosshairProperty);
        set => SetValue(CrosshairProperty, value);
    }

    public byte[]? CrosshairSegmentsV
    {
        get => GetValue(CrosshairSegmentsVProperty);
        set => SetValue(CrosshairSegmentsVProperty, value);
    }

    public byte[]? CrosshairSegmentsH
    {
        get => GetValue(CrosshairSegmentsHProperty);
        set => SetValue(CrosshairSegmentsHProperty, value);
    }

    public Point? AimCursor
    {
        get => GetValue(AimCursorProperty);
        set => SetValue(AimCursorProperty, value);
    }

    public int EffectRadiusPx
    {
        get => GetValue(EffectRadiusPxProperty);
        set => SetValue(EffectRadiusPxProperty, value);
    }

    public bool EffectShadow
    {
        get => GetValue(EffectShadowProperty);
        set => SetValue(EffectShadowProperty, value);
    }

    public int EffectStrokePx
    {
        get => GetValue(EffectStrokePxProperty);
        set => SetValue(EffectStrokePxProperty, value);
    }

    public int EffectStrokeArgb
    {
        get => GetValue(EffectStrokeArgbProperty);
        set => SetValue(EffectStrokeArgbProperty, value);
    }

    public bool EffectsVisible
    {
        get => GetValue(EffectsVisibleProperty);
        set => SetValue(EffectsVisibleProperty, value);
    }

    public double ScreenToDipScale
    {
        get => GetValue(ScreenToDipScaleProperty);
        set => SetValue(ScreenToDipScaleProperty, value);
    }

    public string SelectionBorderStyle
    {
        get => GetValue(SelectionBorderStyleProperty);
        set => SetValue(SelectionBorderStyleProperty, value);
    }

    public int SelectionBorderColorArgb
    {
        get => GetValue(SelectionBorderColorArgbProperty);
        set => SetValue(SelectionBorderColorArgbProperty, value);
    }

    public int SelectionBorderThickness
    {
        get => GetValue(SelectionBorderThicknessProperty);
        set => SetValue(SelectionBorderThicknessProperty, value);
    }

    /// <summary>
    /// 获取截图选框边框的最外侧视觉延伸偏移量（DIP/物理像素），用于主工具栏等覆盖物右对齐平齐吸附。
    /// 选框基于半像素偏移矩形居中描边，外沿为 0.5 + thickness / 2.0；0 像素时返回 0。
    /// </summary>
    public static double GetSelectionOuterOffset(int thickness)
    {
        if (thickness <= 0) return 0.0;
        return 0.5 + (thickness / 2.0);
    }

    // ----- 资源缓存（构造一次） -----

    // 暗窗浓度（MaskBrush）不再住在本层：与录屏遮罩同一档，收在 SelVisuals 那一份里。
    private static readonly IBrush HandleBrush = Brushes.White;
    // 元素检测框、参考线、瞄准光标芯都取同一个紫色：三者都表达「这里是可对齐的目标」。
    private static readonly IBrush ElementAccentBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x19, 0xF2));
    private static readonly IPen AutoHoverPen = new Pen(ElementAccentBrush, 2.0);
    // 遮罩层不随明暗主题变色，故与 BorderPen 一样写成静态常量而非 DynamicResource。
    private const double GuidelineThickness = 1.6;
    private static readonly IPen GuidelinePen = new Pen(ElementAccentBrush, GuidelineThickness)
    {
        DashStyle = SelVisuals.DashPx(4.0, 2.0, GuidelineThickness),
    };
    public const double CrosshairSegmentDip = 32.0;
    private static readonly IPen CrosshairPenBlack = new Pen(Brushes.Black, 1.0);
    private static readonly IPen CrosshairPenWhite = new Pen(Brushes.White, 1.0);
    private static readonly IPen CrosshairPenAccent = new Pen(ElementAccentBrush, 1.0);
    private static readonly IPen[] CrosshairPens = new IPen[]
    {
        CrosshairPenBlack,  // 0 = 黑
        CrosshairPenWhite,  // 1 = 白
        CrosshairPenAccent  // 2 = 品牌紫
    };

    // 十字瞄准光标：系统光标在该阶段隐藏，由这里自绘。四段断开的十字 + 中心圆点，
    // 「断开」是与贯穿整屏的黑色准星线最直观的区分，紫芯白晕则保证压在明暗内容上都看得清。
    private const double AimGap = 3.5;
    private const double AimArmLength = 9.0;
    private const double AimDotHaloRadius = 3.0;
    private const double AimDotRadius = 1.6;
    private static readonly IPen AimHaloPen = new Pen(Brushes.White, 3.6) { LineCap = PenLineCap.Round };
    private static readonly IPen AimCorePen = new Pen(ElementAccentBrush, 1.6) { LineCap = PenLineCap.Round };
#if LONGSCROLL_SELECTION_LAYER
    private static readonly IPen BorderPen = new Pen(Brushes.White, 2.0);
    private IPen? GetBorderPen() => BorderPen;
#else
    private IPen? _cachedBorderPen;
    private string? _cachedBorderStyle;
    private int _cachedBorderColorArgb;
    private int _cachedBorderThickness;

    private IPen? GetBorderPen()
    {
        int thickness = SelectionBorderThickness;
        if (thickness <= 0) return null; // 0px 表示没有选框，跳过绘制

        string style = SelectionBorderStyle ?? AppSettings.SelectionBorderStyleDashed;
        int colorArgb = SelectionBorderColorArgb;

        if (_cachedBorderPen != null &&
            _cachedBorderThickness == thickness &&
            string.Equals(_cachedBorderStyle, style, StringComparison.OrdinalIgnoreCase) &&
            _cachedBorderColorArgb == colorArgb)
        {
            return _cachedBorderPen;
        }

        // 取色／取线型／Alpha 兜底全在共享件那一份里（录屏预览外框引的是同一支构造器），本层只留缓存。
        IPen? pen = SelVisuals.BuildBorderPen(style, colorArgb, thickness);
        if (pen == null) return null;

        _cachedBorderPen = pen;
        _cachedBorderThickness = thickness;
        _cachedBorderStyle = style;
        _cachedBorderColorArgb = colorArgb;
        return pen;
    }
#endif

    // 视觉参数（与旧版一致）
    private const double CornerThickness = 6;
    private const double CornerLength    = 18;
    private const double CornerRadius    = 3.0;
    private const double SideThickness   = 8;
    private const double SideLength      = 22;
    private const double HandleMinSize   = 20; // 选区宽高都 ≥ 20 才画手柄

    // 距离→阴影 alpha 查表：与成品 SelectionEffects.WriteShadow 共用同一张表（BuildShadowAlphaLut），
    // 两端对同一浓度曲线的量化分档完全一致。
    private static readonly byte[] ShadowAlphaLut = SelectionEffects.BuildShadowAlphaLut();

    public override void Render(DrawingContext context)
    {
        if (!_renderDiagnostics) { RenderCore(context); return; }
        long start = Stopwatch.GetTimestamp();
        try { RenderCore(context); }
        finally
        {
            long took = Stopwatch.GetTimestamp() - start;
            _renderCount++;
            _renderTotalTicks += took;
            _renderMaxTicks = Math.Max(_renderMaxTicks, took);
            bool hasRegion = Region.Width > 0 && Region.Height > 0;
            if (_renderCount == 1 || hasRegion && !_firstRegionRecorded)
            {
                string record = $"PERF CaptureMask.Record entry={_diagnosticEntry} first={_renderCount == 1} "
                    + $"hasRegion={hasRegion} sinceSession={(start - _diagnosticStart) * 1000.0 / Stopwatch.Frequency:F2}ms "
                    + $"cost={took * 1000.0 / Stopwatch.Frequency:F3}ms (撤幕后汇总，未测GPU上屏)";
                if (_renderCount == 1) _firstRenderRecord = record;
                else _firstRegionRecord = record;
            }
            _firstRegionRecorded |= hasRegion;
        }
    }

    private void RenderCore(DrawingContext context)
    {
        base.Render(context);

        if (PathOutline != null)
        {
            if (ShowMask && _pathMask != null) context.DrawGeometry(SelVisuals.MaskBrush, null, _pathMask);
            context.DrawGeometry(null, GetBorderPen(), PathOutline);
            return;
        }

        double screenW = Bounds.Width;
        double screenH = Bounds.Height;

        Rect formalRegion = NormalizeRegion(Region, screenW, screenH);
        Rect hoverRegionToShow = _animatedHoverRegion;
        Rect hoverRegion = NormalizeRegion(hoverRegionToShow, screenW, screenH);
        bool hasFormalRegion = formalRegion.Width > 0 && formalRegion.Height > 0;
        bool hasHoverRegion = hoverRegion.Width > 0 && hoverRegion.Height > 0;
        Rect displayRegion = hasFormalRegion ? formalRegion : hoverRegion;

        double x = displayRegion.X;
        double y = displayRegion.Y;
        double w = displayRegion.Width;
        double h = displayRegion.Height;

        // 圆角跟随记忆值，检测框与选区/成品同口径；点击高亮元素转正式选区时圆角连续无跳变。
        double radiusDip = EffectRadiusDip(w, h);
        ShapeCache shape = EnsureShape(w, h, radiusDip);
        // 描边与阴影只在参数条悬浮期间画（EffectsVisible），这是预览层唯一的重开销。真值始终照常下发，
        // 闸门只管画不画，所以反复悬浮不再整轮重建；拖动或缩放选区起手就收闸门，那一帧绝不带上阴影。
        bool showEffects = hasFormalRegion && EffectsVisible && (EffectShadow || EffectStrokePx > 0);

        // 1) 遮罩：整屏 + 在选区位置挖洞。洞用「整屏矩形 ＋ 选区轮廓」两个子路径按 EvenOdd 一次填充
        // 成型：既没有更旧版四矩形拼接在共享边上留下的亮线（自动悬停动画中尤其明显），也不用像上一版
        // 那样每帧做一次多边形布尔运算——CombinedGeometry 要把上百个顶点的洞跟整屏求差，
        // 是「开了圆角和描边后移动选区掉帧」的主因之一。
        //
        // 全屏豁免：高亮区域几乎覆盖整屏（如全屏窗口检测）时跳过遮罩只画边框——
        // 否则遮罩先全屏暗下、又因高亮区覆盖全屏而全部亮回，产生一次无意义的
        // "暗→亮"闪屏。边框仍在，用户依旧能感知当前吸附的是整个屏幕。
        bool coversFullScreen = w >= screenW - 2.0 && h >= screenH - 2.0 && x <= 1.0 && y <= 1.0;
        if (ShowMask && !coversFullScreen)
        {
            IBrush mask = SelVisuals.MaskBrush;
            if (w <= 0 || h <= 0)
            {
                context.DrawRectangle(mask, null, new Rect(0, 0, screenW, screenH));
            }
            else
            {
                long geometryStart = _renderDiagnostics ? Stopwatch.GetTimestamp() : 0;
                var maskGeometry = SelVisuals.BuildMaskGeometry(shape.Hole, x, y, screenW, screenH);
                if (_renderDiagnostics)
                {
                    long took = Stopwatch.GetTimestamp() - geometryStart;
                    _maskBuildCount++;
                    _maskBuildTotalTicks += took;
                    _maskBuildMaxTicks = Math.Max(_maskBuildMaxTicks, took);
                }
                context.DrawGeometry(mask, null, maskGeometry);
            }
        }

        // 1.5) 辅助线：紫色虚线参考线（贯穿整屏）＋高对比中心准星（贯穿整屏）。
        // 放在遮罩之后、选区边框之前：线要压在暗色遮罩上，但不许盖住选区自身的边框与手柄。
        DrawGuidelines(context, screenW, screenH);

        if (!hasFormalRegion)
        {
            if (hasHoverRegion)
            {
                using (context.PushTransform(new Matrix(1, 0, 0, 1, x, y)))
                {
                    context.DrawGeometry(null, AutoHoverPen, shape.Border);
                }
            }
            // 未起框阶段：系统光标已隐藏，自绘十字瞄准光标盖在检测框之上
            DrawAimCursor(context, screenW, screenH);
            return;
        }

        // 1.8/1.9) 投影与描边预览：画在暗遮罩之上、边框之下。描边是一条环形几何直接重放；阴影是
        // EnsureEffects 一次按距离场写好的半分辨率位图，这里每帧只贴**四条边带**——选区内部的位图
        // 像素本来就是全透明的，整张贴等于白贴 80% 面积。两样都与选区在哪无关，故按口径缓存，
        // 只把坐标系平移到选区左上角重放。
        if (showEffects)
        {
            EnsureEffects(shape, w, h, radiusDip);
            using (context.PushTransform(new Matrix(1, 0, 0, 1, x, y)))
            {
                DrawShadowBands(context, shape);
                if (shape.StrokeRing != null)
                    context.DrawGeometry(shape.StrokeBrush, null, shape.StrokeRing);
            }
        }

        // 2) 边框 + 手柄（仅在有有效正式选区且 ShowBorderAndHandles 时画）
        if (!ShowBorderAndHandles) return;

        IPen? borderPen = GetBorderPen();
        if (borderPen != null)
        {
            // 半像素偏移让 1px 线条画在像素正中（Avalonia 11.x 默认 PixelSnap 不一定开启）——
            // 这条口径已经烘进缓存的 Border 里了。
            using (context.PushTransform(new Matrix(1, 0, 0, 1, x, y)))
            {
                // 绘制选区边框（支持虚线/实线与自定义颜色，0px 时免除绘制）
                context.DrawGeometry(null, borderPen, shape.Border);
            }
        }

#if !LONGSCROLL_SELECTION_LAYER
        // 移除选框四周 8 个手柄方块的视觉绘制，手柄及选区四周缩放交互逻辑仍完整保留在交互层（CaptureSession / SelectionState）。
        _ = HandleMinSize;
#endif
    }

    /// <summary>DIP 折算系数；调用方没设置时按 1:1 处理，宁可预览偏大也不要画不出来。</summary>
    private double DipScale => ScreenToDipScale > 0 ? ScreenToDipScale : 1.0;

    /// <summary>圆角预览半径（DIP）：物理半径折算后不超过短边一半，与成品 <c>EffectiveRadius</c> 同一算法。</summary>
    private double EffectRadiusDip(double widthDip, double heightDip)
    {
        int radiusPx = EffectRadiusPx;
        if (radiusPx <= 0) return 0;
        return Math.Min(radiusPx * DipScale, Math.Min(widthDip, heightDip) / 2.0);
    }

    // ----- 形状缓存 -----
    // 挖洞轮廓与边框只由「选区宽高 + 圆角 + DIP 系数」决定，跟选区摆在屏幕上哪个位置毫无关系。可 Region
    // 每被赋一次值就要重画一帧，旧实现每帧都把它们整个重算一遍：描边环的等距偏移是平方级开销（实测 28 档
    // 圆角 + 15px 描边 = 每帧 495µs、还丢掉 1MB 垃圾给 GC），于是"移动选区"这种纯粹改变位置的操作掉帧。
    // 故按口径缓存，命中时只做一次平移。
    //
    // 描边环与阴影再单立一套口径（EffectsBuilt），只在闸门开着的帧才建——"看效果"本来就只发生在调参数
    // 那一瞬。描边是一条环形几何，建好直接重放；阴影自 2026-09-04 第四轮起是**一次距离场直写成的
    // 半分辨率位图、每帧只贴四条边带**（详见 EnsureEffects 里的沿革注释），建好之后闸门反复开合完全
    // 命中，同一次悬浮里拖动滑块才是真的每次重建。

    private ShapeCache _shape = new ShapeCache();

    /// <summary>一套"局部坐标（选区左上角为原点）"的缓存几何。形状口径全同才复用。</summary>
    private sealed class ShapeCache
    {
        public double W, H, Radius, Scale;

        /// <summary>内容轮廓（挖洞用）。洞的外框是整屏、随选区位置变，故每帧带偏移发射一次。</summary>
        public PointF[] Hole = Array.Empty<PointF>();

        /// <summary>边框轮廓（已含让 1px 线落在像素正中的 0.5px 外扩）。</summary>
        public Geometry Border = new RectangleGeometry();

        // 效果口径。EffectsBuilt=false 表示下面这几样已随形状口径作废，下次要用得重建。
        public bool EffectsBuilt;
        public double StrokeDip;
        public int Argb;
        public bool Shadow;

        public Geometry? StrokeRing;
        public IBrush? StrokeBrush;

        /// <summary>阴影预览位图（半分辨率距离场，带 alpha）。null = 当前口径不开阴影。
        /// 尺寸按最大描边口径固定，只随选区宽高/缩放换代；实际使用的逻辑子区见
        /// <see cref="ShadowPixelW"/>/<see cref="ShadowPixelH"/>。</summary>
        public WriteableBitmap? ShadowBitmap;

        /// <summary>位图像素缓冲（BGRA）复用：与位图同尺寸固定分配，滑杆每档重算只重写内容不重新分配。</summary>
        public byte[]? ShadowBuffer;

        /// <summary>当前口径实际用到的逻辑子区宽（位图像素）。贴图源矩形按它取，不用整张位图。</summary>
        public int ShadowPixelW;

        /// <summary>当前口径实际用到的逻辑子区高（位图像素）。</summary>
        public int ShadowPixelH;

        /// <summary>半分辨率位图的边带厚度（位图像素）：位图边缘 → 形状边沿＋阴影扩散＋内渗。</summary>
        public int ShadowBandPx;

        /// <summary>位图相对选区左上角的外扩（物理像素）。边带落点换算 DIP 用。</summary>
        public int ShadowOuterPx;

        /// <summary>
        /// 换代下来的阴影位图滞留队列。合成器线程可能还在重放引用旧位图的上一帧场景，立刻 Dispose
        /// 会与它赛跑；压满两代（期间至少翻过两帧）再释放，原生内存既可回收又无竞态。
        /// </summary>
        public readonly Queue<WriteableBitmap> RetiredShadows = new();

        public bool Matches(double w, double h, double radius, double scale)
            => W == w && H == h && Radius == radius && Scale == scale;

        public bool MatchesEffects(double strokeDip, int argb, bool shadow)
            => EffectsBuilt && StrokeDip == strokeDip && Argb == argb && Shadow == shadow;

        /// <summary>随形状口径作废：描边几何只断引用交给 GC；阴影位图与像素缓冲按尺寸键控，
        /// 留给 <see cref="EnsureEffects"/> 复用（圆角滑杆不改 outerPhys、位图尺寸不变，逐档只重写
        /// 内容即可），尺寸变了或关掉阴影时才在那里换代进滞留队列。</summary>
        public void DropEffects()
        {
            StrokeRing = null;
            StrokeBrush = null;
            EffectsBuilt = false;
            FlushRetiredShadows();
        }

        /// <summary>滞留超过两代的旧位图可以安全释放。</summary>
        public void FlushRetiredShadows()
        {
            while (RetiredShadows.Count > 2) RetiredShadows.Dequeue().Dispose();
        }
    }

    /// <summary>取当前形状口径的缓存几何；口径没变直接复用上一次的，变了才重建并作废效果缓存。</summary>
    private ShapeCache EnsureShape(double w, double h, double radiusDip)
    {
        double scale = DipScale;
        ShapeCache c = _shape;
        if (c.Matches(w, h, radiusDip, scale)) return c;

        c.W = w; c.H = h; c.Radius = radiusDip; c.Scale = scale;
        c.DropEffects();

        var local = new RectangleF(0f, 0f, (float)w, (float)h);
        c.Hole = SelectionEffects.BuildOutline(local, (float)radiusDip);

        // 半像素偏移让 1px 线条画在像素正中（Avalonia 11.x 默认 PixelSnap 不一定开启）
        var borderLocal = w >= 2.0 && h >= 2.0
            ? new RectangleF(-0.5f, -0.5f, (float)(w + 1.0), (float)(h + 1.0))
            : local;
        c.Border = BuildClosedGeometry(SelectionEffects.BuildOutline(borderLocal, (float)radiusDip));

        return c;
    }

    /// <summary>
    /// 保证缓存里的描边环与阴影位图对应当前效果口径。只在闸门开着的帧被调用，所以平时一分钱不花。
    /// </summary>
    private void EnsureEffects(ShapeCache c, double w, double h, double radiusDip)
    {
        double scale = DipScale;
        double strokeDip = EffectStrokePx * scale;
        int argb = EffectStrokeArgb;
        bool shadow = EffectShadow;
        if (c.MatchesEffects(strokeDip, argb, shadow)) return;

        var swatch = System.Diagnostics.Stopwatch.StartNew();
        c.StrokeDip = strokeDip; c.Argb = argb; c.Shadow = shadow;
        bool probeTimings = Environment.GetEnvironmentVariable("SHADOWPROBE_TIMING") != null;
        long tRing = 0, tFill = 0, tCopy = 0;

        c.StrokeRing = null;
        c.StrokeBrush = null;
        if (strokeDip > 0)
        {
            var local = new RectangleF(0f, 0f, (float)w, (float)h);
            // 与成品 SelectionEffects.DrawStroke 调用同一个 BuildStrokeRing（StrokeInnerBleedPx = 0 取消内缩），
            // 色带宽度整个长在边界以外，所以不再受"短边一半"封顶。
            SelectionEffects.BuildStrokeRing(local, (float)radiusDip,
                (float)(SelectionEffects.StrokeInnerBleedPx * scale), (float)strokeDip,
                out PointF[] outer, out PointF[] inner);
            c.StrokeRing = BuildRingGeometry(outer, inner);
            c.StrokeBrush = new SolidColorBrush(ArgbToColor(argb));
            if (probeTimings) tRing = swatch.ElapsedTicks;
        }

        if (!shadow && c.ShadowBitmap != null)
        {
            c.RetiredShadows.Enqueue(c.ShadowBitmap);
            c.ShadowBitmap = null;
        }
        int shadowPixelW = 0, shadowPixelH = 0;
        if (shadow)
        {
            // 阴影以**卡片外沿**为扩散基准：整张卡片一起投影，不被不透明的描边切掉内侧那截。
            // 与成品端 SelectionEffects.WriteShadow 完全同一条算法——距离按**内容轮廓**（选区本身）算，
            // 末端统一减掉描边宽 grow；凸集的等距线就是"距离减常量"，与描边色带的外沿严格同一条线。
            // ⚠ 不要再回到"矩形与半径同步外扩描边宽"：那在 45° 方向多出 0.2312×描边宽，四个角各切出
            //    一片纯透明楔形（阴影与卡片断开），20px 描边实测留 3px 缝，预览与贴图、成品都不再对齐。
            double wPhys = w / scale, hPhys = h / scale;
            var shape = new RectangleF(0f, 0f, (float)wPhys, (float)hPhys);
            // 内容半径被短边一半封顶，与成品 EffectiveRadius 同一口径
            float shapeR = (float)Math.Min(radiusDip / scale, Math.Min(wPhys, hPhys) / 2.0);
            float grow = EffectStrokePx;

            // ==== 阴影预览 = 距离场直写半分辨率位图 + 每帧贴四条边带。沿革： ====
            // ① 最早每帧从零重算全部几何（O(边数²) 等距偏移 + 36 圈阴影），是拖动选区掉帧的主因；
            // ② 2026-09-04（十）改成口径缓存后几何不再每帧重建，但每帧仍要重放 36 条宽描边 AA 路径
            //    ——CPU 栅格探针 ~1730µs/帧（闸门关 459µs），悬浮调参那几秒的爆发式负载让紧随其后
            //    的拖动选区依然卡（用户真机实测第三轮回报）；
            // ③ 同日（十一）把 36 圈栅格成半分辨率位图后贴图降到 ~1131µs/帧——但整张贴把全透明的
            //    选区内部也贴了（80% 面积白贴），且圈叠圈的浓度在贴边处比理论曲线低 24/255（探针
            //    实测，36 圈本就是离散台阶近似）、与成品逐像素剖面也对不上（max|Δ|≈25）；
            // ④ 现行：按成品 WriteShadow 同一距离场直接写像素——内缘贴边即峰值、单调衰减、外缘与
            //    成品同点位截止；半分辨率采样对 36px 高斯软晕的剖面误差 ≤ 2/255（软晕没有高频细节，
            //    圈时代的台阶/凹陷反而更大）。纯黑阴影的预乘与非预乘字节表示完全相同，直接按预乘
            //    口径建位图，贴图路径上省一次 alpha 转换。
            // ⑤ 同日（十二）：四条边带原按直边口径取厚，角部超椭圆弧往里退（45° 退 r·(1−2^(−1/5))），
            //    弧线退进带内侧的那片楔形阴影被整块漏掉（探针角部网格 vs 成品 max|Δ|=43/255 即此），
            //    边带厚度并入 CornerRecedePx 后盖全；同时位图与像素缓冲按**最大描边口径**固定成
            //    「选区宽高」的函数——描边滑杆在 0-20 拖动只改左上角逻辑子区（源矩形跟着缩），
            //    不再逐档分配/释放 1MB+ 的位图与缓冲，圆角滑杆连逻辑尺寸都不改（outerPhys 与半径无关）。
            int outerPhys = EffectStrokePx + SelectionEffects.ShadowBlurPx + 3; // 3px 余量盖扩散最外沿
            // 固定分配口径：描边取量程上限（防外部直设更大值再兜底取 max），滑杆怎么拖都不换尺寸。
            int outerMax = Math.Max(EffectStrokePx, SelectionEffects.MaxStrokeParam)
                + SelectionEffects.ShadowBlurPx + 3;
            int bitmapW = Math.Max(1, (int)Math.Ceiling((wPhys + outerMax * 2) / 2.0));
            int bitmapH = Math.Max(1, (int)Math.Ceiling((hPhys + outerMax * 2) / 2.0));
            // 逻辑子区：当前描边口径实际用到的左上角区域，贴图源矩形按它取
            shadowPixelW = Math.Max(1, (int)Math.Ceiling((wPhys + outerPhys * 2) / 2.0));
            shadowPixelH = Math.Max(1, (int)Math.Ceiling((hPhys + outerPhys * 2) / 2.0));
            // 边带厚度（位图像素）= 位图边缘 → 形状边沿（ShadowBlurPx+3 物理px）＋ 内渗 1px ＋ 进位余量
            // ＋ 角部内缩量：直边段的卡片边沿与带内沿平行，前几项就够；角部那段弧线比直边段往里凹
            // （ShadowBandCornerReachPx），带不加宽就会在四个角各漏一片楔形阴影（见沿革 ⑤）。
            // outerPhys−描边宽度 恒为 ShadowBlurPx+3，与描边宽度无关；内缩量随圆角半径与描边宽度一起变。
            int bandPx = Math.Max(1, (int)Math.Ceiling(
                (outerPhys - EffectStrokePx + 2 + SelectionEffects.ShadowBandCornerReachPx(shapeR, grow)) / 2.0));

            // 缓冲按固定口径分配、按固定宽度（bitmapW）作行距；逻辑子区外的列不写也不贴
            int bufLen = checked(bitmapW * bitmapH * 4);
            byte[] buf = c.ShadowBuffer is { } cached && cached.Length == bufLen ? cached : new byte[bufLen];
            c.ShadowBuffer = buf;
            // bandPx 随圆角半径变宽、逻辑子区随描边宽度变大小，复用的缓冲里可能残留上一档的旧带——
            // 整幅清零后再填，带外与子区外的透明区域不再依赖"写回 0"来维持（1MB 级 Clear 不到 0.1ms）。
            Array.Clear(buf);

            // 四条带与成品 WriteShadow 同构：上下全宽、左右只补中段，四角由上下带覆盖（重叠处写的是
            // 同一个距离场，幂等无害）。坐标都在逻辑子区内，行距用固定位图宽。
            int topEnd = Math.Min(bandPx, shadowPixelH);
            int botStart = Math.Max(0, shadowPixelH - bandPx);
            int leftEnd = Math.Min(bandPx, shadowPixelW);
            int rightStart = Math.Max(0, shadowPixelW - bandPx);
            FillShadowBand(buf, bitmapW, 0, shadowPixelW, 0, topEnd, shape, shapeR, grow, outerPhys);
            FillShadowBand(buf, bitmapW, 0, shadowPixelW, botStart, shadowPixelH, shape, shapeR, grow, outerPhys);
            FillShadowBand(buf, bitmapW, 0, leftEnd, topEnd, botStart, shape, shapeR, grow, outerPhys);
            FillShadowBand(buf, bitmapW, rightStart, shadowPixelW, topEnd, botStart, shape, shapeR, grow, outerPhys);
            if (probeTimings) tFill = swatch.ElapsedTicks - tRing;

            // 位图同样按固定口径分配，只在选区宽高（或缩放）变了才换代——那时效果闸门已收、重建
            // 发生在下一次悬浮，换代走滞留队列延迟释放，避免与合成器线程赛跑。
            if (c.ShadowBitmap is not { } bmp ||
                bmp.PixelSize.Width != bitmapW || bmp.PixelSize.Height != bitmapH)
            {
                if (c.ShadowBitmap != null) c.RetiredShadows.Enqueue(c.ShadowBitmap);
                bmp = new WriteableBitmap(new PixelSize(bitmapW, bitmapH), new Vector(96, 96),
                    PixelFormats.Bgra8888, AlphaFormat.Premul);
            }
            using (var fb = bmp.Lock())
            {
                // 缓冲行距与位图行距同宽（都是固定位图宽）：逻辑子区的前 N 行整块一次拷贝；
                // 行距不一致（理论上的对齐差异）才退回逐行拷。
                int rowBytes = bitmapW * 4;
                if (rowBytes == fb.RowBytes)
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, fb.Address, checked(shadowPixelH * rowBytes));
                else
                    for (int row = 0; row < shadowPixelH; row++)
                        System.Runtime.InteropServices.Marshal.Copy(buf, row * rowBytes,
                            System.IntPtr.Add(fb.Address, row * fb.RowBytes), rowBytes);
            }
            c.ShadowBitmap = bmp;
            c.ShadowBandPx = bandPx;
            c.ShadowOuterPx = outerPhys;
            c.ShadowPixelW = shadowPixelW;
            c.ShadowPixelH = shadowPixelH;
            if (probeTimings) tCopy = swatch.ElapsedTicks - tRing - tFill;
        }
        c.FlushRetiredShadows();

        c.EffectsBuilt = true;
        swatch.Stop();
        if (probeTimings)
        {
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine($"[EnsureEffects] ring={tRing * ms:F2}ms fill={tFill * ms:F2}ms copy={tCopy * ms:F2}ms total={swatch.Elapsed.TotalMilliseconds:F2}ms (stroke={EffectStrokePx} r={EffectRadiusPx})");
        }
        if (ScreenCaptureTool.Core.AppLogger.DetailedLoggingEnabled) ScreenCaptureTool.Core.AppLogger.Debug(
            $"SelectionLayer: 效果预览重建 {swatch.Elapsed.TotalMilliseconds:F1}ms（描边 {EffectStrokePx}px"
            + (shadow ? $" + 阴影距离场 {shadowPixelW}x{shadowPixelH}" : "")
            + $"，选区 {(int)(w / scale)}x{(int)(h / scale)}px）");
    }

    /// <summary>
    /// 把一条边带里的半分辨率像素按距离场写上阴影 alpha。像素中心 = 物理坐标 (2x+1−outerPhys,
    /// 2y+1−outerPhys)（选区左上角为原点）；距离用成品同一套 <see cref="SelectionEffects.DistanceToRoundedRect"/>，
    /// alpha 查两端共用的 LUT。<b>口径与成品 <c>WriteShadowBand</c> 严格同一</b>：<paramref name="shape"/>/<paramref name="radius"/>
    /// 是<b>内容轮廓</b>（选区本身），算出的距离再减 <paramref name="grow"/>（描边宽）才换算成"到卡片外沿的距离"——
    /// 直接拿内容轮廓当基准线画阴影，会在角部留一条与描边等宽的透明缝（详见 <see cref="SelectionEffects"/> 的
    /// "卡片外沿只有一条线"不变量）。只写 A 通道——B/G/R 恒为 0 且缓冲初值即 0（纯黑阴影）。d &lt; −1px
    /// （比成品的内渗还深）不写=全透明，与成品 <c>WriteShadowBand</c> 的跳过条件一致。
    ///
    /// 与成品 <c>WriteShadowBand</c> 同构的快慢两路：每列的横向外部距离先算好（ox），行内只剩一次
    /// 比较；只有同时落在角部象限（ox、oy 都 &gt; 0）的像素才需要超椭圆弧的黄金分割搜索。角部再加两道
    /// 精确下界过滤，把必然 alpha=0 的像素挡在搜索之外：点到弧 ≥ 点到弧外接方 [0,r]² 的距离（弧是
    /// 方的子集），也 ≥ r−|q|（弧上任意点模长 ≥ r，最小在坐标轴端点）——拖圆角滑杆逐档重栅格化时，
    /// 这两道过滤砍掉角部大半的黄金分割调用。两道过滤各留了 1px² 余量，保证被跳过的像素在成品端
    /// 逐像素计算下也必然是 0，两端逐点一致不被过滤破坏。
    /// </summary>
    private static void FillShadowBand(byte[] buf, int bufW, int x0, int x1, int y0, int y1,
        RectangleF shape, float radius, float grow, int outerPhys)
    {
        if (x0 >= x1 || y0 >= y1) return;
        byte[] lut = ShadowAlphaLut;
        int lutMax = lut.Length - 1;
        int rowBytes = bufW * 4;

        bool round = radius > 0.5f;
        float r = round ? radius : 0f;          // 直角按 0 处理：核心矩形即形状本身，与 DistanceToRoundedRect 的直角分支同口径
        float coreL = shape.X + r, coreT = shape.Y + r;
        float coreR = shape.Right - r, coreB = shape.Bottom - r;
        float blurOuter = SelectionEffects.ShadowBlurPx + grow;
        float blurOuter2 = blurOuter * blurOuter + 1f;
        float innerR = radius - 1f;             // 比它还靠里 → d &lt; −1 → alpha 0（直角时 innerR &lt; 0 自然失效）
        float inner2 = innerR * innerR - 1f;

        // 每列的横向外部距离一次算好，行内循环只剩比较、查表与写 alpha
        int cols = x1 - x0;
        float[] ox = new float[cols];
        for (int i = 0; i < cols; i++)
        {
            float px = 2 * (x0 + i) + 1 - outerPhys;
            ox[i] = Math.Max(Math.Max(coreL - px, px - coreR), 0f);
        }

        for (int by = y0; by < y1; by++)
        {
            float py = 2 * by + 1 - outerPhys;
            float oy = Math.Max(Math.Max(coreT - py, py - coreB), 0f);
            int baseOff = by * rowBytes + x0 * 4 + 3;
            for (int i = 0; i < cols; i++)
            {
                float d;
                if (ox[i] > 0f && oy > 0f)
                {
                    if (!round)
                    {
                        d = MathF.Sqrt(ox[i] * ox[i] + oy * oy);
                    }
                    else
                    {
                        float px = 2 * (x0 + i) + 1 - outerPhys;
                        float qx = px < coreL ? coreL - px : px - coreR;
                        float qy = py < coreT ? coreT - py : py - coreB;
                        float ex = Math.Max(qx - radius, 0f), ey = Math.Max(qy - radius, 0f);
                        if (ex > 0f || ey > 0f
                                ? ex * ex + ey * ey >= blurOuter2      // 到外接方已超扩散半径 → d ≥ 36 → alpha 0
                                : qx * qx + qy * qy < inner2)          // 模长比 r 还小 1px+ → d &lt; −1 → alpha 0
                            continue;                                   // 缓冲已清零，不写即透明
                        d = SelectionEffects.DistanceToRoundedRect(px, py, shape, radius);
                    }
                }
                else
                {
                    d = MathF.Max(ox[i], oy) - r;
                }
                d -= grow; // 换算成"到卡片外沿的距离"：描边把这条基准线整体往外挪了 grow
                byte a = d < -1f ? (byte)0 : lut[Math.Clamp((int)(d * 4f), 0, lutMax)];
                buf[baseOff + i * 4] = a;
            }
        }
    }

    /// <summary>
    /// 贴阴影位图：只贴四条边带（上下全宽、左右中段），跳过全透明的选区内部——整张贴有 ~80% 面积
    /// 是白贴的。位图本身按最大描边口径分配，这里一律按逻辑子区（ShadowPixelW/H）取源矩形。
    /// 选区小到边带互相压过（左右带与上下带重叠）时退回整张一次贴：重叠带各贴一遍会把
    /// alpha 叠两遍变深，而小选区的位图本来就小，整张贴不亏。
    /// </summary>
    private static void DrawShadowBands(DrawingContext context, ShapeCache c)
    {
        if (c.ShadowBitmap is not { } bmp) return;
        int w = c.ShadowPixelW, h = c.ShadowPixelH;
        if (w <= 0 || h <= 0) return;
        int band = c.ShadowBandPx, outer = c.ShadowOuterPx;
        double scale = c.Scale;

        if (band * 2 >= w || band * 2 >= h)
        {
            context.DrawImage(bmp, new Rect(0, 0, w, h), ShadowDest(0, 0, w, h, outer, scale));
            return;
        }
        int mid = h - band; // 左右带共享的纵向范围 [band, h−band)
        context.DrawImage(bmp, new Rect(0, 0, w, band), ShadowDest(0, 0, w, band, outer, scale));
        context.DrawImage(bmp, new Rect(0, mid, w, band), ShadowDest(0, mid, w, band, outer, scale));
        context.DrawImage(bmp, new Rect(0, band, band, h - band * 2), ShadowDest(0, band, band, h - band * 2, outer, scale));
        context.DrawImage(bmp, new Rect(w - band, band, band, h - band * 2), ShadowDest(w - band, band, band, h - band * 2, outer, scale));
    }

    /// <summary>半分辨率位图的源矩形 → 选区局部 DIP 落点：位图 1 像素 = 2 物理像素 = 2×scale DIP，
    /// 位图左上角在选区左上角往外 outerPx 物理像素处。</summary>
    private static Rect ShadowDest(int sx, int sy, int sw, int sh, int outerPx, double scale)
        => new((2.0 * sx - outerPx) * scale, (2.0 * sy - outerPx) * scale, 2.0 * sw * scale, 2.0 * sh * scale);

    /// <summary>把一串局部坐标轮廓点闭合成 Avalonia StreamGeometry。</summary>
    private static Geometry BuildClosedGeometry(PointF[] pts)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(new Point(pts[i].X, pts[i].Y));
            ctx.EndFigure(true);
        }
        return geo;
    }

    /// <summary>
    /// 描边环形 = 外沿与内沿两个子闭图按 EvenOdd 填充。EvenOdd 与子图的绕行方向无关，所以两条同为
    /// 顺时针也能正确得到环；内缩后轮廓不存在（选区窄到被色带整个吃掉）时实心填外沿，与成品一致。
    /// </summary>
    private static Geometry BuildRingGeometry(PointF[] outer, PointF[] inner)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);
            EmitFigure(ctx, outer);
            if (inner.Length >= 3) EmitFigure(ctx, inner);
        }
        return geo;
    }

    /// <summary>把一条闭合轮廓点串写进流几何上下文。</summary>
    private static void EmitFigure(StreamGeometryContext ctx, PointF[] pts)
    {
        ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true);
        for (int i = 1; i < pts.Length; i++) ctx.LineTo(new Point(pts[i].X, pts[i].Y));
        ctx.EndFigure(true);
    }

    /// <summary>ARGB 整数 → Avalonia 颜色。设置项与成品侧都用 int，只有界面这一端需要拆分量。</summary>
    private static Color ArgbToColor(int argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>
    /// 画辅助线：紫色虚线参考线（与元素检测框同色，贯穿整屏）＋黑色实线中心准星（贯穿整屏）。
    /// </summary>
    private void DrawGuidelines(DrawingContext context, double screenW, double screenH)
    {
        double[] verticals = GuidelineVerticals;
        if (verticals != null)
        {
            for (int i = 0; i < verticals.Length; i++)
            {
                double vx = verticals[i];
                if (double.IsNaN(vx) || vx < 0 || vx > screenW) continue;
                context.DrawLine(GuidelinePen, new Point(vx, 0), new Point(vx, screenH));
            }
        }

        double[] horizontals = GuidelineHorizontals;
        if (horizontals != null)
        {
            for (int i = 0; i < horizontals.Length; i++)
            {
                double hy = horizontals[i];
                if (double.IsNaN(hy) || hy < 0 || hy > screenH) continue;
                context.DrawLine(GuidelinePen, new Point(0, hy), new Point(screenW, hy));
            }
        }

        Point? cross = Crosshair;
        byte[]? segsH = CrosshairSegmentsH;
        byte[]? segsV = CrosshairSegmentsV;
        if (cross.HasValue && !double.IsNaN(cross.Value.X) && !double.IsNaN(cross.Value.Y)
            && segsH != null && segsH.Length > 0 && segsV != null && segsV.Length > 0)
        {
            double cx = Math.Clamp(cross.Value.X, 0.0, screenW);
            double cy = Math.Clamp(cross.Value.Y, 0.0, screenH);

            // 横线：分段按背景对比色绘制
            for (int s = 0; s < segsH.Length; s++)
            {
                double x0 = s * CrosshairSegmentDip;
                if (x0 >= screenW) break;
                double x1 = Math.Min((s + 1) * CrosshairSegmentDip, screenW);
                byte code = segsH[s];
                IPen pen = code < CrosshairPens.Length ? CrosshairPens[code] : CrosshairPenWhite;
                context.DrawLine(pen, new Point(x0, cy), new Point(x1, cy));
            }

            // 竖线：分段按背景对比色绘制
            for (int s = 0; s < segsV.Length; s++)
            {
                double y0 = s * CrosshairSegmentDip;
                if (y0 >= screenH) break;
                double y1 = Math.Min((s + 1) * CrosshairSegmentDip, screenH);
                byte code = segsV[s];
                IPen pen = code < CrosshairPens.Length ? CrosshairPens[code] : CrosshairPenWhite;
                context.DrawLine(pen, new Point(cx, y0), new Point(cx, y1));
            }
        }
    }

    /// <summary>
    /// 画自绘十字瞄准光标：四段断开的十字 + 中心圆点，白晕衬紫芯。
    /// 该阶段系统光标被隐藏，故圆心就是用户的实际指向。
    /// </summary>
    private void DrawAimCursor(DrawingContext context, double screenW, double screenH)
    {
        Point? aim = AimCursor;
        if (!aim.HasValue || double.IsNaN(aim.Value.X) || double.IsNaN(aim.Value.Y)) return;

        double cx = Math.Clamp(aim.Value.X, 0.0, screenW);
        double cy = Math.Clamp(aim.Value.Y, 0.0, screenH);

        // 先整体描一遍白晕再叠紫芯，避免相邻手臂的白晕盖住已画好的紫芯
        for (int pass = 0; pass < 2; pass++)
        {
            IPen pen = pass == 0 ? AimHaloPen : AimCorePen;
            for (int i = 0; i < 4; i++)
            {
                double dx = i == 0 ? 1 : i == 1 ? -1 : 0;
                double dy = i == 2 ? 1 : i == 3 ? -1 : 0;
                context.DrawLine(pen,
                    new Point(cx + dx * AimGap, cy + dy * AimGap),
                    new Point(cx + dx * (AimGap + AimArmLength), cy + dy * (AimGap + AimArmLength)));
            }
        }

        context.DrawEllipse(Brushes.White, null, new Point(cx, cy), AimDotHaloRadius, AimDotHaloRadius);
        context.DrawEllipse(ElementAccentBrush, null, new Point(cx, cy), AimDotRadius, AimDotRadius);
    }

    private static Rect NormalizeRegion(Rect region, double screenW, double screenH)
    {
        double x = Math.Clamp(region.X, 0.0, screenW);
        double y = Math.Clamp(region.Y, 0.0, screenH);
        double right = Math.Clamp(region.Right, 0.0, screenW);
        double bottom = Math.Clamp(region.Bottom, 0.0, screenH);
        double left = Math.Min(x, right);
        double top = Math.Min(y, bottom);
        right = Math.Max(x, right);
        bottom = Math.Max(y, bottom);
        return new Rect(left, top, Math.Max(0.0, right - left), Math.Max(0.0, bottom - top));
    }

    private static void DrawHandles(DrawingContext context, double x, double y, double w, double h)
    {
        double cornerHalfT = CornerThickness / 2;
        double sideHalfT   = SideThickness / 2;
        double sideHalfL   = SideLength / 2;

        DrawHandlesCore(context, x, y, w, h, cornerHalfT, sideHalfT, sideHalfL, HandleBrush, 0.0, CornerThickness, SideThickness, CornerLength, SideLength);
    }

    private static void DrawHandlesCore(DrawingContext context, double x, double y, double w, double h, double cornerHalfT, double sideHalfT, double sideHalfL, IBrush brush, double offset, double cornerThickness, double sideThickness, double cornerLength, double sideLength)
    {
        double cornerHalf = cornerThickness / 2;
        double sideHalf = sideThickness / 2;
        double sideHalfLong = sideLength / 2;

        // ---- 4 角的 L 形（每角两条相互垂直的矩形，圆角 1.5）----
        // TL
        DrawRoundedRect(context, x - cornerHalf + offset, y - cornerHalf + offset, cornerLength,    cornerThickness, CornerRadius, brush);
        DrawRoundedRect(context, x - cornerHalf + offset, y - cornerHalf + offset, cornerThickness, cornerLength,    CornerRadius, brush);

        // TR
        DrawRoundedRect(context, x + w - cornerLength + cornerHalf - offset, y - cornerHalf + offset, cornerLength,    cornerThickness, CornerRadius, brush);
        DrawRoundedRect(context, x + w - cornerHalf - offset,                y - cornerHalf + offset, cornerThickness, cornerLength,    CornerRadius, brush);

        // BR
        DrawRoundedRect(context, x + w - cornerLength + cornerHalf - offset, y + h - cornerHalf - offset,                cornerLength,    cornerThickness, CornerRadius, brush);
        DrawRoundedRect(context, x + w - cornerHalf - offset,                y + h - cornerLength + cornerHalf - offset, cornerThickness, cornerLength,    CornerRadius, brush);

        // BL
        DrawRoundedRect(context, x - cornerHalf + offset, y + h - cornerHalf - offset,                cornerLength,    cornerThickness, CornerRadius, brush);
        DrawRoundedRect(context, x - cornerHalf + offset, y + h - cornerLength + cornerHalf - offset, cornerThickness, cornerLength,    CornerRadius, brush);

        // ---- 4 边中点的矩形手柄（无圆角即可）----
        context.DrawRectangle(brush, null,
            new Rect(x + w / 2 - sideHalfLong, y - sideHalf + offset,        sideLength,    sideThickness));
        context.DrawRectangle(brush, null,
            new Rect(x + w - sideHalf - offset,    y + h / 2 - sideHalfLong, sideThickness, sideLength));
        context.DrawRectangle(brush, null,
            new Rect(x + w / 2 - sideHalfLong, y + h - sideHalf - offset,    sideLength,    sideThickness));
        context.DrawRectangle(brush, null,
            new Rect(x - sideHalf + offset,        y + h / 2 - sideHalfLong, sideThickness, sideLength));
    }

    private static void DrawRoundedRect(DrawingContext context, double x, double y, double w, double h, double radius, IBrush brush)
    {
        var rrect = new RoundedRect(new Rect(x, y, w, h), radius);
        context.DrawRectangle(brush, null, rrect);
    }
}
