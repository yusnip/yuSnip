using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Media;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 使用统计趋势图（分系列渐隐面积／堆叠柱，自绘）。
///
/// 为什么自绘而不是拼 XAML <c>Rectangle</c>：30 天 × 5 类 = 150 个矩形加布局开销，
/// 自绘一次成型，且能顺手画网格线、日期刻度与悬停明细，不必为此堆一层容器。
///
/// 取色规则（设计规范 §2 / §8.2）：控件**不暴露任何 <c>IBrush</c> 属性**，
/// 五档系列色由自己按 Key 走 <c>TryFindResource(key, ActualThemeVariant, out …)</c> 解析，
/// 主题变体变化时 <see cref="InvalidateVisual"/> 重绘，故深浅色自动跟随、无硬编码兜底色。
/// </summary>
public sealed class UsageChart : Control
{
    /// <summary>纵向网格线条数（不含基线）。</summary>
    private const int GridLineCount = 3;

    /// <summary>横轴日期刻度区带高度。</summary>
    private const double AxisHeight = 16.0;

    /// <summary>柱与柱之间的间隙占单格宽度的比例。</summary>
    private const double BarGapRatio = 0.28;

    /// <summary>横轴日期字号（同时用于推算左右留白，两处必须同值）。</summary>
    private const double AxisFontSize = 10.0;

    /// <summary>单根柱的最大宽度（像素），防止天数较少时柱子被拉得过粗。</summary>
    private const double MaxBarWidth = 32.0;

    /// <summary>数据源：按日期升序的分日计数。</summary>
    public static readonly StyledProperty<IReadOnlyList<UsageDayBucket>?> DaysProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<UsageDayBucket>?>(nameof(Days));

    public IReadOnlyList<UsageDayBucket>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>按系列绘制独立趋势线与渐隐面积；不改变分日数据或统计口径。</summary>
    public static readonly StyledProperty<bool> IsAreaChartProperty =
        AvaloniaProperty.Register<UsageChart, bool>(nameof(IsAreaChart));
    public bool IsAreaChart { get => GetValue(IsAreaChartProperty); set => SetValue(IsAreaChartProperty, value); }

    // 自绘指针与节点的过渡仍走 Avalonia 属性动画，无额外循环计时器。
    public static readonly StyledProperty<double> PointerXProperty = AvaloniaProperty.Register<UsageChart, double>("PointerX");
    public static readonly StyledProperty<double> PointerYProperty = AvaloniaProperty.Register<UsageChart, double>("PointerY");
    public static readonly StyledProperty<double> PointerOpacityProperty = AvaloniaProperty.Register<UsageChart, double>("PointerOpacity");
    public static readonly StyledProperty<double> NodeStrengthProperty = AvaloniaProperty.Register<UsageChart, double>("NodeStrength");
    private Point[][] _seriesPoints = Array.Empty<Point[]>();
    private int _hoverSeries = -1;
    private int _nodeSeries = -1;
    private Point _nodePoint;
    private bool _pointerInside;
    private bool _motionEnabled;

    private void UpdatePointer(Point point)
    {
        bool motion = (VisualRoot as Window)?.Classes.Contains("reduceMotion") != true;
        if (!_pointerInside || motion != _motionEnabled)
        {
            Transitions = null;
            SetValue(PointerXProperty, point.X); SetValue(PointerYProperty, point.Y);
            if (motion)
                Transitions = new Transitions
                {
                    new DoubleTransition { Property = PointerXProperty, Duration = TimeSpan.FromMilliseconds(65), Easing = new CubicEaseOut() },
                    new DoubleTransition { Property = PointerYProperty, Duration = TimeSpan.FromMilliseconds(65), Easing = new CubicEaseOut() },
                    new DoubleTransition { Property = PointerOpacityProperty, Duration = TimeSpan.FromMilliseconds(120) },
                    new DoubleTransition { Property = NodeStrengthProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() },
                };
            _motionEnabled = motion;
        }
        _pointerInside = true;
        Cursor = new Cursor(StandardCursorType.None);
        SetValue(PointerXProperty, point.X); SetValue(PointerYProperty, point.Y);
        SetValue(PointerOpacityProperty, 1d);
    }

    /// <summary>当前鼠标悬停的柱索引，-1 表示无。</summary>
    private int _hoverIndex = -1;

    /// <summary>
    /// 列布局的**单一口径**，由 <see cref="ComputePlotLayout"/> 每次 Render 算好：
    /// 柱区左内缩、单列宽、刻度标签间隔。命中测试与画柱/画线/画标签必须全部读这三个字段，
    /// 任何一处自己再算一次 <c>w / days.Count</c> 就会让标签与柱子错位。
    /// </summary>
    private double _plotX0;
    private double _slot;
    private int _tickStride = 5;

    static UsageChart()
    {
        AffectsRender<UsageChart>(DaysProperty, IsAreaChartProperty, PointerXProperty, PointerYProperty, PointerOpacityProperty, NodeStrengthProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 主题切换：系列色是运行时按 Key 解析的，必须主动重绘。
        // 属性的声明类型是 ThemeVariantScope（Window / TopLevel 只是继承了同一个静态字段，
        // 所以在窗口类里能裸写、在 Control 派生类里不能），这里必须完全限定。
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
        if (change.Property == DaysProperty || change.Property == IsAreaChartProperty)
        {
            _hoverIndex = -1;
            _hoverSeries = -1;
            _seriesPoints = Array.Empty<Point[]>();
            _pointerInside = false;
            Cursor = Cursor.Default;
            SetValue(PointerOpacityProperty, 0d);
            SetValue(NodeStrengthProperty, 0d);
            ToolTip.SetTip(this, null);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        IReadOnlyList<UsageDayBucket>? days = Days;
        if (days == null || days.Count == 0) return;

        Point p = e.GetPosition(this);
        // 列宽由 Render 算好（含为日期标签留的左右内缩）；尚未绘制过时 _slot 为 0，无命中可言
        if (_slot <= 0) return;

        int idx = IsAreaChart ? (int)Math.Floor((p.X - _plotX0) / _slot + 0.5)
            : (int)Math.Floor((p.X - _plotX0) / _slot);
        if (IsAreaChart && p.X >= 0 && p.X <= Bounds.Width) idx = Math.Clamp(idx, 0, days.Count - 1);
        else if (idx < 0 || idx >= days.Count) idx = -1;
        if (IsAreaChart)
        {
            _hoverSeries = -1;
            double distance = 12 * 12;
            if (idx >= 0)
                for (int series = 0; series < _seriesPoints.Length; series++)
                {
                    if (idx >= _seriesPoints[series].Length) continue;
                    Point node = _seriesPoints[series][idx];
                    double d = (node.X - p.X) * (node.X - p.X) + (node.Y - p.Y) * (node.Y - p.Y);
                    if (d <= distance) { distance = d; _hoverSeries = series; _nodePoint = node; }
                }
            if (_hoverSeries >= 0) _nodeSeries = _hoverSeries;
            UpdatePointer(p);
            SetValue(NodeStrengthProperty, _hoverSeries >= 0 ? 1d : 0d);
        }
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            ToolTip.SetTip(this, idx >= 0 ? HoverLabel(days[idx]) : null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
        Cursor = Cursor.Default;
        SetValue(PointerOpacityProperty, 0d);
        SetValue(NodeStrengthProperty, 0d);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 命中测试底：完全透明，只为让 OnPointerMoved 能收到事件
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        IReadOnlyList<UsageDayBucket>? days = Days;
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 8 || h <= AxisHeight + 8)
        {
            _plotX0 = 0;
            _slot = 0;
            return;
        }

        IBrush grid = Resolve("SctSoftBorderBrush");
        IBrush axisText = Resolve("SctTextTertiaryBrush");

        double plotH = h - AxisHeight;

        ComputePlotLayout(w, days?.Count ?? 0, axisText);

        if (days == null || days.Count == 0)
        {
            DrawBaseline(context, grid, plotH, w);
            DrawCenteredText(context, "暂无趋势数据", axisText, 12, w, plotH);
            return;
        }

        // 峰值：面积图按各系列最大值、堆叠柱按总数；所有系列共用同一纵向标尺；全 0 时用 1 兜底，避免除零并让基线看起来正常
        long peak = 0;
        for (int i = 0; i < days.Count; i++)
        {
            long t = IsAreaChart ? Math.Max(days[i].Sticker, Math.Max(Math.Max(days[i].Screenshot, days[i].AiChat), Math.Max(days[i].Ocr, days[i].Translate))) : days[i].Total;
            if (t > peak) peak = t;
        }

        // 网格线（等分 GridLineCount 档）与峰值标注
        for (int g = 1; g <= GridLineCount; g++)
        {
            double y = plotH - plotH * g / (GridLineCount + 1.0);
            context.FillRectangle(grid, new Rect(IsAreaChart ? 2.5 : 0, Math.Round(y), IsAreaChart ? w - 5 : w, 1));
        }
        DrawBaseline(context, grid, plotH, w);

        if (peak <= 0)
        {
            DrawCenteredText(context, "所选区间内暂无使用记录", axisText, 12, w, plotH);
            DrawDateTicks(context, days, axisText, plotH);
            DrawPointer(context);
            return;
        }

        // 峰值刻度贴右上角
        var peakText = MakeText(peak.ToString(CultureInfo.InvariantCulture), axisText, 10);
        context.DrawText(peakText, new Point(w - peakText.Width - 1, 0));

        IBrush s1 = Resolve("SctChartSeries1Brush");
        IBrush s2 = Resolve("SctChartSeries2Brush");
        IBrush s3 = Resolve("SctChartSeries3Brush");
        IBrush s4 = Resolve("SctChartSeries4Brush");
        IBrush s5 = Resolve("SctChartSeries5Brush");

        // 顶部留 14px 给峰值标注，柱子最高不顶到框
        double usableH = Math.Max(4.0, plotH - 14.0);
        double slot = _slot;
        double barW = Math.Clamp(slot * (1.0 - BarGapRatio), 2.0, MaxBarWidth);
        double offset = (slot - barW) / 2.0;

        if (IsAreaChart)
        {
            DrawAreaSeries(context, days, new[] { s1, s2, s3, s4, s5 }, peak, usableH, plotH);
        }
        else
        {
            for (int i = 0; i < days.Count; i++)
            {
                UsageDayBucket d = days[i];
                if (d.Total <= 0) continue;

                double x = _plotX0 + i * slot + offset;
                double y = plotH; // 自底向上堆叠

                y = StackSegment(context, s1, x, y, barW, d.Screenshot, peak, usableH);
                y = StackSegment(context, s2, x, y, barW, d.AiChat, peak, usableH);
                y = StackSegment(context, s3, x, y, barW, d.Ocr, peak, usableH);
                y = StackSegment(context, s4, x, y, barW, d.Translate, peak, usableH);
                StackSegment(context, s5, x, y, barW, d.Sticker, peak, usableH);
            }

        }

        DrawDateTicks(context, days, axisText, plotH);

        // 悬停明细：上方空间足够时绘制摘要，完整明细始终由 ToolTip 提供。
        if (_hoverIndex >= 0 && _hoverIndex < days.Count)
        {
            UsageDayBucket d = days[_hoverIndex];
            string label = HoverLabel(d);
            IBrush hoverText = Resolve("SctTextSecondaryBrush");
            var ft = MakeText(label, hoverText, 11);
            // 明细行宽达「日期 + 五个计数」，而内缩只留了半个日期标签，仍须钳到画布内
            // （它是一条游离的信息行，不像日期刻度那样必须正对自己那一列，钳位不损失对齐语义）
            double lx = Math.Clamp(DataX(_hoverIndex) - ft.Width / 2.0, 0, Math.Max(0, w - ft.Width));
            if (ft.Width <= w) context.DrawText(ft, new Point(lx, 0));

            // 悬停列的竖向指示（弱色细线，帮助对准是哪一天）
            double markTop = ft.Height + 2;
            context.FillRectangle(grid, new Rect(Math.Round(DataX(_hoverIndex)), markTop, 1, Math.Max(0, plotH - markTop)));
        }
        if (IsAreaChart)
        {
            double strength = GetValue(NodeStrengthProperty);
            if (strength > 0 && _nodeSeries >= 0)
            {
                IBrush series = new[] { s1, s2, s3, s4, s5 }[_nodeSeries];
                using (context.PushOpacity(strength))
                {
                    using (context.PushOpacity(.16)) context.DrawEllipse(series, null, _nodePoint, 10 + 3 * strength, 10 + 3 * strength);
                    context.DrawEllipse(Resolve("SctCardFillBrush"), new Pen(Resolve($"SctChartSeries{_nodeSeries + 1}LineBrush"), 2), _nodePoint, 3 + 2 * strength, 3 + 2 * strength);
                }
            }
            DrawPointer(context);
        }
    }

    private void DrawPointer(DrawingContext context)
    {
        double opacity = GetValue(PointerOpacityProperty);
        if (IsAreaChart && opacity > 0)
            using (context.PushOpacity(opacity))
            {
                var point = new Point(GetValue(PointerXProperty), GetValue(PointerYProperty));
                // 品牌色十字沿用跟随过渡，浅深底片描边保证与曲线交叠时仍可辨认。
                var outline = new Pen(Resolve("SctCardFillBrush"), 3, lineCap: PenLineCap.Round);
                var ink = new Pen(Resolve("SctAccentBrush"), 1.5, lineCap: PenLineCap.Round);
                foreach (var pen in new[] { outline, ink })
                {
                    context.DrawLine(pen, new Point(point.X - 6, point.Y), new Point(point.X + 6, point.Y));
                    context.DrawLine(pen, new Point(point.X, point.Y - 6), new Point(point.X, point.Y + 6));
                }
            }
    }

    private static string HoverLabel(UsageDayBucket d) => d.Date.ToString("MM-dd", CultureInfo.InvariantCulture)
        + "  截图 " + d.Screenshot + " · AI " + d.AiChat + " · OCR " + d.Ocr + " · 翻译 " + d.Translate + " · 贴图 " + d.Sticker;

    /// <summary>同一纵轴绘制五项真实计数；线条实色，面积低透明度向基线渐隐。</summary>
    private void DrawAreaSeries(DrawingContext context, IReadOnlyList<UsageDayBucket> days,
        IBrush[] brushes, long peak, double usableH, double baseline)
    {
        _seriesPoints = new Point[brushes.Length][];
        for (int series = 0; series < brushes.Length; series++)
        {
            long Value(UsageDayBucket d) => series switch { 0 => d.Screenshot, 1 => d.AiChat, 2 => d.Ocr, 3 => d.Translate, _ => d.Sticker };
            bool any = false;
            var points = new Point[days.Count];
            _seriesPoints[series] = points;
            for (int i = 0; i < days.Count; i++)
            {
                long value = Math.Max(0, Value(days[i])); any |= value > 0;
                points[i] = new Point(DataX(i), baseline - usableH * value / peak);
            }
            if (!any) continue;
            var line = new StreamGeometry();
            using (var path = line.Open())
            {
                path.BeginFigure(points[0], false);
                AppendSmoothSegments(path, points);
                path.EndFigure(false);
            }
            var area = new StreamGeometry();
            using (var path = area.Open())
            {
                path.BeginFigure(new Point(points[0].X, baseline), true);
                path.LineTo(points[0]);
                AppendSmoothSegments(path, points);
                path.LineTo(new Point(points[^1].X, baseline)); path.EndFigure(true);
            }
            if (brushes[series] is ISolidColorBrush solid)
            {
                var c = solid.Color;
                var fade = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = new GradientStops
                    {
                        new GradientStop(Color.FromArgb(33, c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                    },
                };
                context.DrawGeometry(fade, null, area);
            }
            // 线条和端点单独消费可辨认色，避免为了细线清晰把整块占比条加深。
            IBrush lineBrush = Resolve($"SctChartSeries{series + 1}LineBrush");
            context.DrawGeometry(null, new Pen(lineBrush, 2), line);
            // 单日数据也可见，不用插值制造不存在的峰值。
            context.DrawEllipse(lineBrush, null, points[^1], 2.5, 2.5);
        }
    }

    /// <summary>保形三次插值：经过真实节点，局部极值处切线归零，禁止插值超出相邻计数范围。</summary>
    private static double[] CurveTangents(Point[] points)
    {
        var tangent = new double[points.Length];
        if (points.Length < 2) return tangent;
        var slope = new double[points.Length - 1];
        for (int i = 0; i < slope.Length; i++) slope[i] = (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        tangent[0] = slope[0]; tangent[^1] = slope[^1];
        for (int i = 1; i < tangent.Length - 1; i++)
            tangent[i] = slope[i - 1] * slope[i] <= 0 ? 0 : (slope[i - 1] + slope[i]) / 2;
        for (int i = 0; i < slope.Length; i++)
        {
            if (Math.Abs(slope[i]) < 1e-12) { tangent[i] = 0; tangent[i + 1] = 0; continue; }
            double a = tangent[i] / slope[i], b = tangent[i + 1] / slope[i];
            double magnitude = a * a + b * b;
            if (magnitude > 9)
            {
                double scale = 3 / Math.Sqrt(magnitude);
                tangent[i] = scale * a * slope[i]; tangent[i + 1] = scale * b * slope[i];
            }
        }
        return tangent;
    }

    private static void AppendSmoothSegments(StreamGeometryContext path, Point[] points)
    {
        var tangent = CurveTangents(points);
        for (int i = 1; i < points.Length; i++)
        {
            double third = (points[i].X - points[i - 1].X) / 3;
            path.CubicBezierTo(new Point(points[i - 1].X + third, points[i - 1].Y + tangent[i - 1] * third),
                new Point(points[i].X - third, points[i].Y - tangent[i] * third), points[i]);
        }
    }

    /// <summary>画一段堆叠柱，返回下一段的顶部 y。</summary>
    private static double StackSegment(DrawingContext context, IBrush brush, double x, double bottomY,
        double barW, long value, long peak, double usableH)
    {
        if (value <= 0) return bottomY;
        double segH = usableH * value / peak;
        if (segH < 1.0) segH = 1.0; // 非零就至少画 1px，否则"有记录却看不见"
        double top = bottomY - segH;
        context.FillRectangle(brush, new Rect(x, top, barW, segH));
        return top;
    }

    private void DrawBaseline(DrawingContext context, IBrush grid, double plotH, double w)
    {
        context.FillRectangle(grid, new Rect(IsAreaChart ? 2.5 : 0, Math.Round(plotH), IsAreaChart ? w - 5 : w, 1));
    }

    /// <summary>折线按首尾端点分配，柱图按单格中心分配；绘制、刻度与指针共用。</summary>
    private double DataX(int index) => _plotX0 + (index + (IsAreaChart ? 0 : 0.5)) * _slot;

    /// <summary>
    /// 横轴布局单一口径：折线首尾按端点分配（单日居中），柱图按内缩后的单格中心分配。
    /// 柱图的半标签留白沿用原规则；折线仅留圆点半径，首末日期向画布内对齐避免裁字。
    /// 刻度间隔由标签宽度与实际步长决定，绘制和指针命中不能各自另算横坐标。
    /// </summary>
    private void ComputePlotLayout(double totalW, int dayCount, IBrush axisText)
    {
        _plotX0 = 0;
        _slot = 0;
        if (dayCount <= 0 || totalW <= 0) return;

        double labelW = MakeText("12/31", axisText, AxisFontSize).Width;
        double pad = Math.Min(Math.Ceiling(labelW / 2.0) + 1.0, totalW / 4.0);
        if (IsAreaChart)
        {
            // 端点只留圆点半径，取消柱状图的半格留白；单日居中显示。
            _plotX0 = dayCount == 1 ? totalW / 2 : 2.5;
            _slot = Math.Max(1, totalW - 5) / Math.Max(1, dayCount - 1);
        }
        else
        {
            _plotX0 = pad;
            _slot = Math.Max(1.0, totalW - pad * 2.0) / dayCount;
        }

        // 自适应步长：
        // 若每列宽足够容纳标签（含舒适留白），则逐日或隔日显示；
        // 列宽较窄时（如 30 天 / 90 天）按 5 的倍数分档，确保刻度疏密得当且不重叠。
        double minColForEveryDay = labelW + 10.0;
        if (_slot >= minColForEveryDay)
        {
            _tickStride = 1;
        }
        else if (_slot * 2.0 >= minColForEveryDay)
        {
            _tickStride = 2;
        }
        else
        {
            _tickStride = Math.Max(5, (int)Math.Ceiling(labelW * 1.25 / _slot / 5.0) * 5);
        }
    }

    /// <summary>日期首尾必标；折线端点标签向内对齐，柱图保持居中。</summary>
    private void DrawDateTicks(DrawingContext context, IReadOnlyList<UsageDayBucket> days,
        IBrush brush, double plotH)
    {
        double slot = _slot;
        if (slot <= 0) return;

        int count = days.Count;
        for (int i = 0; i < count; i++)
        {
            bool show;
            if (_tickStride == 1)
            {
                show = true;
            }
            else
            {
                // 首、尾必标；中间项按步长对齐，且距离末尾保留防重叠保护
                show = (i == 0) || (i == count - 1)
                    || (i % _tickStride == 0 && i > 0 && i < count - Math.Max(2, _tickStride / 2));
            }
            if (!show) continue;

            string text = days[i].Date.ToString("M/d", CultureInfo.InvariantCulture);
            var ft = MakeText(text, brush, AxisFontSize);
            double x = DataX(i) - ft.Width / 2.0;
            if (IsAreaChart && count > 1)
            {
                if (i == 0) x = 0;
                else if (i == count - 1) x = Bounds.Width - ft.Width;
            }
            context.DrawText(ft, new Point(x, plotH + 3));
        }
    }

    private static void DrawCenteredText(DrawingContext context, string text, IBrush brush, double size,
        double w, double plotH)
    {
        var ft = MakeText(text, brush, size);
        context.DrawText(ft, new Point((w - ft.Width) / 2.0, (plotH - ft.Height) / 2.0));
    }

    private static FormattedText MakeText(string text, IBrush brush, double size)
    {
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush);
    }

    /// <summary>
    /// 主题感知取色：显式携带当前主题变体解析（无参 FindResource 不感知明暗，深色下会取到浅色值）。
    /// 解析失败返回透明——**刻意不给硬编码兜底色**（设计规范 §8.2）：
    /// Token 缺失应当在三态验证时一眼看出来，而不是被兜底色掩盖。
    /// </summary>
    private IBrush Resolve(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush) return brush;
        AppLogger.Warn("UsageChart: 未能解析主题资源 " + key);
        return Brushes.Transparent;
    }
}
