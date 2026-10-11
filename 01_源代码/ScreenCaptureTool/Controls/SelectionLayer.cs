using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

#if LONGSCROLL_SELECTION_LAYER
namespace ScreenCaptureTool.LongScroll.Controls;
#else
namespace ScreenCaptureTool.Controls;
#endif

/// <summary>
/// 选区视觉层：4 矩形遮罩（半透明黑）+ 选区边框（白色虚线）+ 8 个手柄（4 角 L 形 + 4 边中点矩形）。
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
    private Rect _animatedHoverRegion;
    private bool _isAnimating;
    private long _lastTick;
    private Avalonia.Threading.DispatcherTimer? _animationTimer;
    private Rect _debouncedTargetRegion;
    private Rect _animatingTargetRegion;
    private Avalonia.Threading.DispatcherTimer? _debounceTimer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AutoHoverRegionProperty)
        {
            Rect target = (Rect)change.NewValue!;
            if (target.Width <= 0 || target.Height <= 0)
            {
                _animatedHoverRegion = default;
                _debouncedTargetRegion = default;
                _animatingTargetRegion = default;
                StopDebounceTimer();
                StopAnimationTimer();
                InvalidateVisual();
            }
            else
            {
                if (_animatedHoverRegion.Width <= 0 || _animatedHoverRegion.Height <= 0)
                {
                    // 第一次出现，或者重新显示：直接对齐，不要出现滑过来的动画
                    _animatedHoverRegion = target;
                    _animatingTargetRegion = target;
                    _debouncedTargetRegion = default;
                    StopDebounceTimer();
                    StopAnimationTimer();
                    InvalidateVisual();
                }
                else
                {
                    // 已经在显示，且目标变了：启动平滑过渡或防抖
                    double currentArea = _animatedHoverRegion.Width * _animatedHoverRegion.Height;
                    double targetArea = target.Width * target.Height;

                    // 如果目标面积比当前区域大 3 倍以上，说明是要向一个显著更大的区域过渡
                    // 引入 80ms 的防抖延时，避免快速扫过大区域时频繁闪烁
                    if (currentArea > 0 && targetArea > currentArea * 3.0)
                    {
                        _debouncedTargetRegion = target;
                        StartDebounceTimer();
                    }
                    else
                    {
                        // 否则（如缩小或微调），直接取消防抖，开始过渡
                        StopDebounceTimer();
                        _animatingTargetRegion = target;
                        StartAnimationTimer();
                    }
                }
            }
        }
    }

    private void StartAnimationTimer()
    {
        _isAnimating = true;
        if (_animationTimer == null)
        {
            _animationTimer = new Avalonia.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                Avalonia.Threading.DispatcherPriority.Input,
                OnAnimationTick);
        }

        if (!_animationTimer.IsEnabled)
        {
            _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _animationTimer.Start();
        }
    }

    private void StopAnimationTimer()
    {
        _isAnimating = false;
        _animationTimer?.Stop();
    }

    private void StartDebounceTimer()
    {
        if (_debounceTimer == null)
        {
            _debounceTimer = new Avalonia.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(30), // 80毫秒防抖延时，过滤快速滑过的大框
                Avalonia.Threading.DispatcherPriority.Input,
                OnDebounceTick);
        }
        else
        {
            _debounceTimer.Stop();
        }
        _debounceTimer.Start();
    }

    private void StopDebounceTimer()
    {
        _debounceTimer?.Stop();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        StopDebounceTimer();
        _animatingTargetRegion = _debouncedTargetRegion;
        StartAnimationTimer();
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
        double elapsedMs = (double)(now - _lastTick) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        _lastTick = now;

        // 限制时间片，防止卡死或过大跳跃
        if (elapsedMs < 1) elapsedMs = 1;
        if (elapsedMs > 100) elapsedMs = 100;

        double targetX = target.X;
        double targetY = target.Y;

        double dx = targetX - _animatedHoverRegion.X;
        double dy = targetY - _animatedHoverRegion.Y;
        double dw = target.Width - _animatedHoverRegion.Width;
        double dh = target.Height - _animatedHoverRegion.Height;

        // 判定结束的条件
        if (Math.Abs(dx) < 0.25 && 
            Math.Abs(dy) < 0.25 && 
            Math.Abs(dw) < 0.25 && 
            Math.Abs(dh) < 0.25)
        {
            _animatedHoverRegion = target;
            StopAnimationTimer();
        }
        else
        {
            // 协同限速 Lerp 算法：
            // 指数逼近系数 k = 0.05
            double k = 0.02;
            double factor = 1.0 - Math.Exp(-k * elapsedMs);

            double stepX = dx * factor;
            double stepY = dy * factor;
            double stepW = dw * factor;
            double stepH = dh * factor;

            // 限制最大位移/缩放速度，单位：像素 / 秒 (DIP/s)
            // 4500 像素/秒 相比之前的 3500 更快，能够保持极高的瞬时反应速度，同时不丢失过渡的中间帧细节
            double maxSpeed = 32000.0; 
            double maxStep = maxSpeed * (elapsedMs / 800.0);

            if (maxStep > 0)
            {
                // 计算各个维度的步长超出比例，取最大超出比例作为协同缩放因子
                double ratioX = Math.Abs(stepX) / maxStep;
                double ratioY = Math.Abs(stepY) / maxStep;
                double ratioW = Math.Abs(stepW) / maxStep;
                double ratioH = Math.Abs(stepH) / maxStep;

                double maxRatio = Math.Max(Math.Max(ratioX, ratioY), Math.Max(ratioW, ratioH));

                // 如果有任何一个维度超速，则等比例缩小所有维度，确保过渡过程呈完美的直线运动，从根本上解决“先左后右”/缩放畸变的问题
                if (maxRatio > 1.0)
                {
                    stepX /= maxRatio;
                    stepY /= maxRatio;
                    stepW /= maxRatio;
                    stepH /= maxRatio;
                }
            }

            _animatedHoverRegion = new Rect(
                _animatedHoverRegion.X + stepX,
                _animatedHoverRegion.Y + stepY,
                _animatedHoverRegion.Width + stepW,
                _animatedHoverRegion.Height + stepH
            );
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

    static SelectionLayer()
    {
        // 这些属性变化都要触发重绘
        AffectsRender<SelectionLayer>(RegionProperty, ShowBorderAndHandlesProperty, ShowMaskProperty, AutoHoverRegionProperty);
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

    // ----- 资源缓存（构造一次） -----

    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0));
    private static readonly IBrush HandleBrush = Brushes.White;
    private static readonly IPen AutoHoverPen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x88, 0xFF)), 2.0);
#if LONGSCROLL_SELECTION_LAYER
    private static readonly IPen BorderPen = new Pen(Brushes.White, 1.0);
#else
    private static readonly IPen BorderUnderlayPen = new Pen(Brushes.Black, 1.0);
    private static readonly IPen BorderPen = new Pen(Brushes.White, 1.0)
    {
        DashStyle = new DashStyle(new double[] { 4.0, 2.0 }, 0),
    };
#endif

    // 视觉参数（与旧版一致）
    private const double CornerThickness = 6;
    private const double CornerLength    = 18;
    private const double CornerRadius    = 3.0;
    private const double SideThickness   = 8;
    private const double SideLength      = 22;
    private const double HandleMinSize   = 20; // 选区宽高都 ≥ 20 才画手柄

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double screenW = Bounds.Width;
        double screenH = Bounds.Height;

        Rect formalRegion = NormalizeRegion(Region, screenW, screenH);
        Rect hoverRegionToShow = _isAnimating ? _animatedHoverRegion : _animatingTargetRegion;
        Rect hoverRegion = NormalizeRegion(hoverRegionToShow, screenW, screenH);
        bool hasFormalRegion = formalRegion.Width > 0 && formalRegion.Height > 0;
        bool hasHoverRegion = hoverRegion.Width > 0 && hoverRegion.Height > 0;
        Rect displayRegion = hasFormalRegion ? formalRegion : hoverRegion;

        double x = displayRegion.X;
        double y = displayRegion.Y;
        double w = displayRegion.Width;
        double h = displayRegion.Height;

        // 1) 遮罩：整屏遮罩 + 在选区位置挖洞。
        // 旧实现用 4 个矩形拼出挖洞效果，相邻矩形在共享边会被 Avalonia 反走样留下
        // 半透明像素；自动悬停动画过程中（框尺寸逐帧变化）这条缝会暴露底层亮色截图，
        // 视觉上呈现"贯穿屏幕的白色细线"。改用 CombinedGeometry（整屏 - 选区）一次性
        // 绘制，从几何层面消除共享边，无论选区怎么变化都不会有缝。
        if (ShowMask)
        {
            var screenRect = new Rect(0, 0, screenW, screenH);
            if (w <= 0 || h <= 0)
            {
                context.DrawRectangle(MaskBrush, null, screenRect);
            }
            else
            {
                var screenGeo = new RectangleGeometry(screenRect);
                var holeGeo = new RectangleGeometry(new Rect(x, y, w, h));
                var maskGeo = new CombinedGeometry(GeometryCombineMode.Exclude, screenGeo, holeGeo);
                context.DrawGeometry(MaskBrush, null, maskGeo);
            }
        }

        if (!hasFormalRegion)
        {
            if (hasHoverRegion)
            {
                Rect hoverRect = new Rect(x - 0.5, y - 0.5, w + 1.0, h + 1.0);
                context.DrawRectangle(null, AutoHoverPen, hoverRect);
            }
            return;
        }

        // 2) 边框 + 手柄（仅在有有效正式选区且 ShowBorderAndHandles 时画）
        if (!ShowBorderAndHandles) return;

        // 半像素偏移让 1px 线条画在像素正中（Avalonia 11.x 默认 PixelSnap 不一定开启）
        Rect borderRect = (w >= 2.0 && h >= 2.0)
            ? new Rect(x - 0.5, y - 0.5, w + 1.0, h + 1.0)
            : new Rect(x, y, w, h);
#if !LONGSCROLL_SELECTION_LAYER
        context.DrawRectangle(null, BorderUnderlayPen, borderRect);
#endif
        context.DrawRectangle(null, BorderPen, borderRect);

#if !LONGSCROLL_SELECTION_LAYER
        if (w < HandleMinSize || h < HandleMinSize) return;

        DrawHandles(context, x - 1.0, y - 1.0, w + 2.0, h + 2.0);
#endif
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
