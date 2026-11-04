using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Services;

/// <summary>一次标注手势的不可变参数；由共享参数控件读取，不读取全局界面状态。</summary>
internal sealed record AnnotationGestureSettings(
    Color Color,
    float Thickness,
    BrushProfile Brush,
    AnnotationLineStyle BrushLineStyle,
    ArrowAnnotationStyle ArrowStyle,
    float ArrowScale,
    ShapeAnnotationKind ShapeKind,
    AnnotationLineStyle ShapeLineStyle,
    BlurMode BlurMode,
    int BlurLevel,
    float Darkness,
    SpotlightShapeKind SpotlightKind,
    float SpotlightThickness,
    /// <summary>圆角半径，单位＝图元坐标系（截图＝屏幕物理像素，贴图＝内容像素）。</summary>
    /// <remarks>聚光灯与矩形共用这一格：同一时刻只会用到其中一档工具，两档的取值范围与语义一致，
    /// 分开两格等于在三个宿主里各抄一遍同值的实参。</remarks>
    float CornerRadius,
    /// <summary>形状填充不透明度 0～1；0＝不填充。填充色恒等于 <see cref="Color"/>，故这里只有一格浓淡。</summary>
    float ShapeFillOpacity,
    float CounterRadius,
    CounterStyleSettings Counter,
    float EraserDiameter,
    // 宿主当前显示倍率（截图侧恒为 1）：内容像素→屏幕像素的换算比，只用于归一画笔速度，不参与任何几何
    float VelocityScale,
    EraserGestureMode EraserMode = EraserGestureMode.Rectangle)
{
    /// <summary>参数条已经不在时起笔的兜底档（画板与参数条同生共死，正常路径走不到这里）。
    /// 三个宿主共用这一份，免得各自内联一遍 18 个实参、日后改一档就漏两处。</summary>
    internal static AnnotationGestureSettings Fallback(float arrowScale = 1f, float velocityScale = 1f) => new(
        Color.Red, 4f, BrushProfile.Default, AnnotationLineStyle.Solid,
        ArrowAnnotationStyle.Designed, arrowScale, ShapeAnnotationKind.Rectangle, AnnotationLineStyle.Solid,
        BlurMode.Mosaic, 5, 0.5f, SpotlightShapeKind.Rectangle, 3f, 0f, 0f, 12f,
        CounterStyleSettings.Default, 28f, velocityScale);
}

/// <summary>
/// 标注交互控制器。宿主负责坐标映射，本类负责参数冻结、临时预览与一次操作提交。
/// 截图先接入静态图元工厂，原采样和选区状态机保留，贴图通过实例手势入口接入。
/// </summary>
internal sealed class AnnotationInteractionController
{
    private readonly IAnnotationHost _host;
    private readonly List<StrokePoint> _samples = new();
    private readonly BrushInputSmoother _smoother = new();
    private AnnotationGestureSettings? _settings;
    private ToolMode _tool;
    private PointF _start;
    private PointF _end;
    private double _timeBaseMs;
    private double _timeScale = 1.0;
    private long _lastPreviewTick;
    private ErasureShape? _activeErasure;
    private readonly List<ErasureMark> _erasureMarks = new();
    // 擦除发布的合帧闸门读数与被压住标记，口径与截图侧同一份 CaptureSession.PreviewFrameGateMs
    private double _lastErasurePublishMs;
    private bool _erasurePublishPending;

    /// <summary>
    /// 本帧是否按住 Shift。<b>刻意不进 <see cref="AnnotationGestureSettings"/></b>：那份是起笔时冻结的档位
    /// （冻结才有"起笔后滚轮不改这一笔"的语义），而 Shift 的语义正是"起笔之后随时可按可放"，
    /// 故由宿主每帧现读传入，与截图侧每次读 <c>e.KeyModifiers</c> 同一口径。
    /// </summary>
    private bool _shiftLock;

    /// <summary>Stopwatch 计数到毫秒的换算系数；与截图侧 CaptureSession 同一口径。</summary>
    private static readonly double StopwatchToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// 采样时刻（毫秒，微秒级分辨率）。速度笔锋靠相邻点的 dt 算像素/毫秒，
    /// 用 TickCount 的整毫秒粒度会把 dt 量化成 0 或几毫秒，笔宽随之抖动出坑洼。
    /// </summary>
    private static double GetPreciseTimeMs() => Stopwatch.GetTimestamp() * StopwatchToMs;

    /// <summary>
    /// 本笔迹的时间轴读数：贴图 1 内容像素＝<c>scale</c> 个屏幕像素，位移天然被除以 scale，
    /// 于是同手速在缩小档会算出数倍速度、笔锋莫名变细。把 dt 按 <c>1/scale</c> 拉伸即可抵消，
    /// 让滤波与笔锋都工作在截图同款的"屏幕像素/毫秒"口径。倍率为 1 时逐位等于真实毫秒。
    /// </summary>
    private double StrokeTimeMs() => _timeBaseMs + (GetPreciseTimeMs() - _timeBaseMs) / _timeScale;

    public AnnotationShape? Preview { get; private set; }
    public bool IsDrawing => _settings != null;
    public event Action<PointF>? TextRequested;

    public AnnotationInteractionController(IAnnotationHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>起笔前先映射坐标并冻结参数，后续滚轮变化不回写到当前笔迹。</summary>
    public bool Begin(Avalonia.Point windowPoint, AnnotationGestureSettings settings)
    {
        if (IsDrawing || !_host.TryGetAnnotationPoint(windowPoint, out var point)) return false;
        _tool = _host.ActiveAnnotationTool;
        if (_tool == ToolMode.None) return false;
        _start = _end = new PointF((float)point.X, (float)point.Y);
        if (_tool == ToolMode.Text)
        {
            TextRequested?.Invoke(_start);
            return true;
        }
        _settings = settings;
        _timeBaseMs = GetPreciseTimeMs();
        _timeScale = settings.VelocityScale > 0.0001f ? settings.VelocityScale : 1.0f;
        _lastPreviewTick = 0;
        _shiftLock = false;
        _smoother.Reset();
        _samples.Clear();
        _samples.Add(new StrokePoint(_start.X, _start.Y, StrokeTimeMs()));
        if (_tool == ToolMode.Eraser)
        {
            _activeErasure = new ErasureShape(
                _host.AnnotationDocument.Shapes.Where(shape => shape is not ErasureShape).Select(shape => shape.Id),
                _host.AnnotationTextIds, Array.Empty<ErasureMark>());
            _erasureMarks.Clear();
            // 起手这一枚不闸：闸门读数清零，落笔当下立刻出一个洞，别让人觉得"点下去没反应"
            _lastErasurePublishMs = 0;
            _erasurePublishPending = false;
            if (settings.EraserMode == EraserGestureMode.Brush)
                UpdateErasure(new ErasureMark(new RectangleF(
                    _start.X - settings.EraserDiameter / 2f, _start.Y - settings.EraserDiameter / 2f,
                    settings.EraserDiameter, settings.EraserDiameter), true));
        }
        UpdatePreview();
        return true;
    }

    /// <summary>
    /// 移动。<paramref name="shiftLock"/> 由宿主每帧现读鼠标按下期间的 Shift 键态传入（同截图侧读
    /// <c>e.KeyModifiers</c>）。<see cref="_end"/> 存的仍是<b>原始</b>点：锁形只在 <see cref="UpdatePreview"/>
    /// 生效，中途松开 Shift 立刻回到自由形状，不会留下一枚被量化过的终点。
    /// </summary>
    public void Move(Avalonia.Point windowPoint, bool shiftLock)
    {
        _shiftLock = shiftLock;
        if (!IsDrawing || !_host.TryGetAnnotationPoint(windowPoint, out var point)) return;
        var raw = new PointF((float)point.X, (float)point.Y);
        // 画笔一律把原始点交给共享整形器，靠"最小点距"去重（截图侧同一顺序），不做提前相等剔除
        if (_tool == ToolMode.Eraser)
        {
            var next = new PointF((float)point.X, (float)point.Y);
            if (_settings?.EraserMode == EraserGestureMode.Rectangle)
            {
                _end = next;
                var rect = RectangleF.FromLTRB(Math.Min(_start.X, next.X), Math.Min(_start.Y, next.Y),
                    Math.Max(_start.X, next.X), Math.Max(_start.Y, next.Y));
                _host.SetEraserRectanglePreview(rect);
                if (rect.Width >= 1f && rect.Height >= 1f)
                {
                    _erasureMarks.Clear();
                    UpdateErasure(new ErasureMark(rect, false));
                }
                else if (_activeErasure != null)
                {
                    _erasureMarks.Clear();
                    _host.AnnotationDocument.RemoveShape(_activeErasure.Id);
                    _host.InvalidateAnnotationPreview();
                }
                return;
            }
            float dx = next.X - _end.X, dy = next.Y - _end.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float step = Math.Max(2f, (_settings?.EraserDiameter ?? 24f) / 4f);
            int count = Math.Max(1, (int)MathF.Ceiling(distance / step));
            PointF from = _end;
            for (int i = 1; i <= count; i++)
            {
                float t = i / (float)count;
                var sample = new PointF(from.X + dx * t, from.Y + dy * t);
                float diameter = _settings?.EraserDiameter ?? 24f;
                _erasureMarks.Add(new ErasureMark(new RectangleF(sample.X - diameter / 2f,
                    sample.Y - diameter / 2f, diameter, diameter), true));
            }
            PublishErasure();
            _end = next;
            return;
        }
        if (_tool == ToolMode.Brush)
        {
            AppendBrushSample(raw);
            return;
        }
        if (raw == _end) return;
        _end = raw;
        UpdatePreview();
    }

    /// <summary>落一个画笔采样：输入平滑 → 最小点距去重 → 合帧刷新预览。</summary>
    private void AppendBrushSample(PointF raw)
    {
        if (_settings is not { } settings) return;
        double nowMs = StrokeTimeMs();
        PointF smoothed = _smoother.Push(raw, nowMs, settings.Brush.Smoothing);
        if (!BrushInputSmoother.ShouldAddPoint(_samples, smoothed)) return;
        _samples.Add(new StrokePoint(smoothed.X, smoothed.Y, nowMs));
        _end = smoothed;
        SetPreviewThrottled();
    }

    /// <summary>与截图侧 SetBrushPreviewThrottled 同口径：栅格化整条笔迹开销随点数增长，合帧即可。</summary>
    private void SetPreviewThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastPreviewTick < 16) return;
        _lastPreviewTick = now;
        UpdatePreview();
    }

    /// <summary>
    /// 收笔采样：绕过输入平滑把光标点直接入库（滤波天然落后原始点约一个时间常数，否则笔尾停在光标后面），
    /// 并无视节流强制刷新预览——被节流压住的最后几个点必须进本次提交的形状。
    /// </summary>
    private void FinishBrushSample(Avalonia.Point windowPoint)
    {
        if (_host.TryGetAnnotationPoint(windowPoint, out var point))
        {
            var endPt = new PointF((float)point.X, (float)point.Y);
            if (BrushInputSmoother.ShouldAddPoint(_samples, endPt))
                _samples.Add(new StrokePoint(endPt.X, endPt.Y, StrokeTimeMs()));
            _end = endPt;
        }
        UpdatePreview();
    }

    /// <summary>收笔并提交。返回<b>已写进文档的那个图元</b>（宿主据此把它设为选中态，对齐截图侧
    /// 每个 <c>Finish*</c> 收尾的"绘制完成自动选中"）；未达阈值、退化成一笔没有的＝返回 null。
    /// <paramref name="shiftLock"/> 与 <see cref="Move"/> 同一口径：提交的就是那一帧的预览，判据必须跟着同一帧的键态走。</summary>
    public AnnotationShape? End(Avalonia.Point windowPoint, bool shiftLock)
    {
        if (!IsDrawing) return null;
        if (_tool == ToolMode.Eraser)
        {
            Move(windowPoint, shiftLock);
            // 收笔必须把被闸门压住的最后一段补落：否则洞缺尾，提交进文档的擦除区与松手前所见不一致
            if (_erasurePublishPending) PublishErasure(true);
            bool changed = _activeErasure != null && _erasureMarks.Count > 0;
            _activeErasure = null;
            if (changed) _host.CommitAnnotationChange();
            Cancel();
            return null;
        }
        if (_tool == ToolMode.Brush)
            FinishBrushSample(windowPoint);
        else
            Move(windowPoint, shiftLock);
        var shape = Preview;
        // 与截图默认拖动阈值一样，面工具不能提交退化矩形；点画笔和序号允许单击。
        // 这里拿未量化的 _start/_end 判阈值即为等价：锁方向只转不伸长（两点距离不变），
        // 锁 1:1 取短边（两边同缩到 min，"|dx|且|dy| ≥ 阈值"这条判据原样成立）。
        bool valid = shape != null && (_tool is ToolMode.Brush or ToolMode.Counter
            || (_tool == ToolMode.Arrow
                ? Math.Sqrt(Math.Pow(_end.X - _start.X, 2) + Math.Pow(_end.Y - _start.Y, 2)) >= CaptureSession.DragThresholdPx
                : Math.Abs(_end.X - _start.X) >= CaptureSession.DragThresholdPx && Math.Abs(_end.Y - _start.Y) >= CaptureSession.DragThresholdPx));
        Cancel();
        if (!valid || shape == null) return null;
        var previous = _host.AnnotationDocument.CreateSnapshot();
        try
        {
            _host.AnnotationDocument.AddShape(shape);
            _host.CommitAnnotationChange();
            return shape;
        }
        catch
        {
            _host.AnnotationDocument.ReplaceAll(previous);
            _host.InvalidateAnnotationPreview();
            throw;
        }
    }

    public void Cancel()
    {
        if (_activeErasure != null) _host.AnnotationDocument.RemoveShape(_activeErasure.Id);
        _activeErasure = null;
        _erasureMarks.Clear();
        _lastErasurePublishMs = 0;
        _erasurePublishPending = false;
        _host.SetEraserRectanglePreview(null);
        _settings = null;
        _samples.Clear();
        Preview = null;
        _host.InvalidateAnnotationPreview();
    }

    private void UpdateErasure(ErasureMark mark)
    {
        if (_activeErasure == null) return;
        _erasureMarks.Add(mark);
        PublishErasure();
    }

    private void PublishErasure(bool force = false)
    {
        if (_activeErasure == null) return;
        double nowMs = GetPreciseTimeMs();
        if (!force && _lastErasurePublishMs != 0 && nowMs - _lastErasurePublishMs < EraserPublishGateMs())
        {
            _erasurePublishPending = true;
            return;
        }
        _lastErasurePublishMs = nowMs;
        _erasurePublishPending = false;
        _activeErasure = _activeErasure.WithMarks(_erasureMarks);
        if (!_host.AnnotationDocument.ReplaceShape(_activeErasure))
            _host.AnnotationDocument.AddShape(_activeErasure);
        _host.InvalidateAnnotationPreview();
    }

    /// <summary>与截图侧 <c>CaptureSession.PublishEraserMarks</c> 同一条自适应闸门：常态 16 毫秒，
    /// 上一次<b>整层重绘</b>实测超过半格就按它的 1.6 倍放宽、最多 80 毫秒，免得昂贵帧把鼠标事件堆成队列（光标不跟手）。
    /// 读数必须取重绘耗时，取发布耗时（写文档＋标脏，实测≈0）会让闸门永不放宽。</summary>
    private double EraserPublishGateMs()
    {
        double renderMs = _host.AnnotationLayerRenderMs;
        return renderMs > CaptureSession.PreviewFrameGateMs * 0.5
            ? Math.Min(CaptureSession.EraserPublishGateMaxMs, renderMs * 1.6)
            : CaptureSession.PreviewFrameGateMs;
    }

    private void UpdatePreview()
    {
        if (_settings is not { } settings) return;
        // Shift 的两种锁法各管一族工具，判据与截图侧同一份 Core 算式（见 AnnotationGeometry）：
        // 面工具锁 1:1（椭圆吃同一矩形，故天然锁成正圆），箭头/序号引线锁八向直线。
        // bounds 传 null＝不夹：贴图侧起笔与移动都被 TryGetAnnotationPoint 夹在内容矩形内，
        // 两点都在框内则其包壳必在框内，再夹一次是空转。
        bool square = _shiftLock && _tool is ToolMode.Rect or ToolMode.Blur or ToolMode.Spotlight;
        bool straight = _shiftLock && _tool is ToolMode.Arrow or ToolMode.Counter;
        var end = straight ? AnnotationGeometry.SnapStraightLine(_start, _end) : _end;
        var rect = square ? AnnotationGeometry.SquareFromDrag(_start, _end, null)
            : RectangleF.FromLTRB(Math.Min(_start.X, _end.X), Math.Min(_start.Y, _end.Y),
                Math.Max(_start.X, _end.X), Math.Max(_start.Y, _end.Y));
        Preview = _tool switch
        {
            ToolMode.Brush => CreateStroke(_samples, settings.Color, settings.Thickness,
                settings.Brush, settings.BrushLineStyle),
            ToolMode.Arrow => CreateArrow(_start, end, settings.Color, settings.Thickness,
                settings.ArrowStyle, settings.ArrowScale),
            ToolMode.Rect => CreateShape(rect, settings.Color, settings.Thickness,
                settings.ShapeKind, settings.ShapeLineStyle, settings.CornerRadius, settings.ShapeFillOpacity),
            ToolMode.Blur => CreateBlur(rect, settings.BlurMode, settings.BlurLevel),
            ToolMode.Spotlight => CreateSpotlight(rect, settings.Darkness, settings.SpotlightKind,
                settings.Color, settings.SpotlightThickness, settings.CornerRadius),
            ToolMode.Counter => settings.Counter.NewShape(_start,
                settings.Counter.NextNumber(_host.AnnotationDocument), settings.CounterRadius,
                settings.Color, TailIfDragged(end) ? end : null),
            _ => null
        };
        _host.InvalidateAnnotationPreview();
    }

    public static StrokeShape CreateStroke(
        IReadOnlyList<StrokePoint> points,
        Color color,
        float thickness,
        BrushProfile? profile = null,
        AnnotationLineStyle lineStyle = AnnotationLineStyle.Solid)
    {
        if (points == null) throw new ArgumentNullException(nameof(points));
        return new StrokeShape(points, color, thickness, profile ?? BrushProfile.Default, lineStyle);
    }

    public static ArrowShape CreateArrow(PointF start, PointF end, Color color, float thickness,
        ArrowAnnotationStyle style = ArrowAnnotationStyle.Designed, float scale = 1.0f)
        => new(start, end, color, thickness, style, scale);

    public static RectangleShape CreateShape(RectangleF rect, Color color, float thickness,
        ShapeAnnotationKind kind = ShapeAnnotationKind.Rectangle,
        AnnotationLineStyle lineStyle = AnnotationLineStyle.Solid,
        float cornerRadius = 0.0f, float fillOpacity = 0.0f)
        => new(rect, color, thickness, kind, lineStyle, cornerRadius, fillOpacity);

    public static BlurShape CreateBlur(RectangleF rect, BlurMode mode, int level)
        => new(rect, mode, level);

    public static SpotlightShape CreateSpotlight(RectangleF rect, float darkness,
        SpotlightShapeKind kind = SpotlightShapeKind.Rectangle, Color? stroke = null,
        float thickness = 3.0f, float cornerRadius = 0.0f)
        => new(rect, darkness, kind, stroke, thickness, cornerRadius);

    public static CounterShape CreateCounter(PointF center, int number, float radius,
        Color color, CounterStyleSettings style)
        => style.NewShape(center, number, radius, color);

    /// <summary>这一笔是否拖出了引线：判据与箭头同一道阈值，没拖出去就还是"单击建一个号、不带引线"。
    /// 吃的是<b>量化后</b>的端点（与预览、提交同一个点）；锁方向不改长度，故阈值不受影响。</summary>
    private bool TailIfDragged(PointF end)
        => MathF.Abs(end.X - _start.X) >= CaptureSession.DragThresholdPx
            || MathF.Abs(end.Y - _start.Y) >= CaptureSession.DragThresholdPx;
}
