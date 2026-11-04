using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Capture.Annotations;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 标注渲染层。坐标输入使用截图/屏幕物理像素，渲染时换算为窗口 DIP。
///
/// 阶段 11：增加 PreviewShape，用于当前正在绘制的画笔/箭头/矩形实时预览。
/// PreviewShape 不进入正式 AnnotationDocument，不参与撤销历史，避免鼠标移动时反复提交文档。
/// </summary>
public sealed class AnnotationLayer : Control
{
    private AnnotationDocument? _document;
    private AnnotationShape? _previewShape;
    private Point? _pointerFootprint;
    private double _pointerFootprintDiameter;
    private readonly PointerFootprintVisual _pointerVisual;
    private DrawingRectangleF? _eraserRectanglePreview;

    public AnnotationLayer()
    {
        // 光标是独立的小合成节点：移动它不应重录整屏标注／聚光灯／擦除裁切。
        _pointerVisual = new PointerFootprintVisual(this) { IsHitTestVisible = false, IsVisible = false };
        VisualChildren.Add(_pointerVisual);
        LogicalChildren.Add(_pointerVisual);
    }

    private sealed class PointerFootprintVisual(AnnotationLayer owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            // 保持原绘制坐标、线宽与主题取色，只把原点换到小节点的局部坐标。
            using var translated = context.PushTransform(Matrix.CreateTranslation(-Bounds.X, -Bounds.Y));
            owner.DrawPointerFootprint(context);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangePointerFootprint();
        return finalSize;
    }

    private void ArrangePointerFootprint()
    {
        if (_pointerFootprint is Point center && _pointerFootprintDiameter > 0)
        {
            double radius = _pointerFootprintDiameter / 2.0;
            // 白色外圈半宽 1.5 DIP，再留抗锯齿边缘，防止小节点的损伤区域截断圆圈。
            double extent = radius + 2;
            _pointerVisual.Arrange(new Rect(center.X - extent, center.Y - extent, extent * 2, extent * 2));
        }
    }

    public DrawingRectangleF? EraserRectanglePreview
    {
        get => _eraserRectanglePreview;
        set
        {
            if (_eraserRectanglePreview == value) return;
            _eraserRectanglePreview = value;
            InvalidateVisual();
        }
    }
    private IReadOnlyList<DrawingRectangleF> _autoMosaicHighlights = Array.Empty<DrawingRectangleF>();

    /// <summary>
    /// 按文档版本缓存图元需要的裁切批次列表，实际平台几何由独立的批次缓存跨版本复用。
    /// 键带图元包围盒，因为同 id 的预览图元会在拖动中换位置。
    /// </summary>
    private readonly Dictionary<ErasureClipKey, Geometry[]> _erasureClipCache = new();
    private readonly ErasureClipCache _erasureChunks = new();
    private ErasureShape[] _erasures = Array.Empty<ErasureShape>();
    private ulong _erasureClipRevision;
    private (double Scale, long OriginX, long OriginY, double Width, double Height) _erasureClipCanvas;

    private readonly record struct ErasureClipKey(Guid ShapeId, float Left, float Top, float Right, float Bottom);

    /// <summary>本次重绘里的"被选中图元"（预览优先），供 <see cref="IsErasureRevealed"/> 判临时显形。</summary>
    private AnnotationShape? _renderSelectedShape;

    /// <summary>
    /// 编辑期的<b>临时显形</b>判据：被选中的那个图元跳过擦除裁切，让人看清它原本画了什么、被擦了哪一块；
    /// 取消选中即回到擦除后的样子。<b>只影响屏上预览</b>——导出与贴图存档一律不读这里，成品仍以擦除为准。
    /// 聚光灯的暗幕是"同组并集一次画成"（拆不出单颗，见 2026-09-29 的重叠合并），故组内任一颗被选中＝整组不裁，
    /// 免得出现"边框显形而暗幕仍被擦"的半截状态。
    /// </summary>
    private bool IsErasureRevealed(AnnotationShape shape)
    {
        if (_renderSelectedShape is not { } selected) return false;
        return shape.Id == selected.Id || (shape is SpotlightShape && selected is SpotlightShape);
    }

    /// <summary>最近一次整层重绘的实际耗时（毫秒，常驻读数、与取证开关无关）。
    /// 擦除手势的合帧闸门据此放宽：重绘越贵，洞少刷几帧，先把输入线程让出来。</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>
    /// 取证计数器（与 <see cref="Services.CaptureSession.EraserProbe"/> 同一开关，2026-10-06 定位"画笔涂抹仍卡"）：
    /// 一次擦除手势的整层重绘次数/耗时，以及其中"取洞几何＋推裁切"占掉多少。手势结束由会话侧打一行汇总并清零。
    /// </summary>
    internal static long ProbeRenderTicks;
    internal static long ProbeRenderMaxTicks;
    internal static long ProbeClipTicks;
    /// <summary>"筛落点＋拼几何"那一段（布尔运算尚未交给平台）。</summary>
    internal static long ProbeClipBuildTicks;
    /// <summary><c>PushGeometryClip</c> 那一下（平台真正建裁切）。</summary>
    internal static long ProbeClipPushTicks;
    internal static int ProbeRenderCount;
    internal static int ProbeClipCount;

    internal static void ResetEraserProbe()
    {
        ProbeRenderTicks = 0;
        ProbeRenderMaxTicks = 0;
        ProbeClipTicks = 0;
        ProbeClipBuildTicks = 0;
        ProbeClipPushTicks = 0;
        ProbeRenderCount = 0;
        ProbeClipCount = 0;
    }

    /// <summary>遮罩预览用的水印（见 <see cref="Watermark"/>）与其落点矩形（见 <see cref="WatermarkRegion"/>）。</summary>
    private WatermarkConfig? _watermark;
    private Rect _watermarkRegion;
    private readonly Dictionary<int, Bitmap> _mosaicDownscaledCache = new Dictionary<int, Bitmap>();
    private readonly Dictionary<int, Bitmap> _blurDownscaledCache = new Dictionary<int, Bitmap>();

    /// <summary>笔迹预览位图缓存的总字节预算（Bgra8888）。</summary>
    private const int StrokeViewBudgetBytes = 64 * 1024 * 1024;

    /// <summary>笔迹预览位图缓存的条数上限。</summary>
    private const int StrokeViewMaxEntries = 48;

    private readonly Dictionary<StrokeKey, StrokeView> _strokeViewCache = new Dictionary<StrokeKey, StrokeView>();
    private readonly Queue<StrokeKey> _strokeViewOrder = new Queue<StrokeKey>();
    private long _strokeViewBytes;

    /// <summary>正在拖（或正在落笔）的那条笔迹：位图单独留一份，不进字典、不受预算淘汰。</summary>
    private StrokeShape? _liveSource;
    private StrokeView? _liveView;
    private long _liveBake;

    /// <summary>判定"整条只差一个位移"的容差：只用来吃掉浮点平移的舍入噪声，任何真实形变都远大于它。</summary>
    private const float TranslationEpsPx = 0.01f;

    private DrawingBitmap? _sourceBitmap;

    public DrawingBitmap? SourceBitmap
    {
        get => _sourceBitmap;
        set
        {
            if (ReferenceEquals(_sourceBitmap, value)) return;
            _sourceBitmap = value;
            ClearEffectCaches();
            InvalidateVisual();
        }
    }

    public DrawingRectangle SourceScreenRect { get; set; }

    private DrawingRectangleF? _spotlightBoundsScreenRect;

    public DrawingRectangleF? SpotlightBoundsScreenRect
    {
        get => _spotlightBoundsScreenRect;
        set
        {
            if (_spotlightBoundsScreenRect == value) return;
            _spotlightBoundsScreenRect = value;
            // 选区缩放／平移不会修改标注文档，必须主动重绘暗幕，避免保留旧范围的画面。
            InvalidateVisual();
        }
    }

    private Guid? _selectedShapeId;

    public Guid? SelectedShapeId
    {
        get => _selectedShapeId;
        set
        {
            if (_selectedShapeId == value) return;
            _selectedShapeId = value;
            RaiseSelectionGeometryChanged();
        }
    }

    /// <summary>
    /// 选中图元（**预览优先**，与选中手柄同一口径）的几何发生变化时触发。宿主据此把「跟随图元的浮层」
    /// ——目前只有聚光灯圆角浮条——重新贴到图元上方。
    ///
    /// <para><b>两个</b>发点：<see cref="SelectedShapeId"/> 换人（选中/取消）、<see cref="Document"/> 的
    /// Changed（撤销/删除/方向键步进/松手提交）。<b>不含</b> <see cref="PreviewShape"/> 换帧——拖动与
    /// 8 向缩放走预览、全程不经文档，故那些帧上这里一个通知都不会发。浮层因此**不会**在拖动途中自己跟上：
    /// 宿主的口径是"手势起手即收起、松手提交后按新几何贴回"（2026-09-18，见
    /// <c>CaptureSession.BeginShapeEdit</c> 与 <c>StickerWindow.BeginAnnotationShapeEdit</c>），
    /// 而不是照预览逐帧重算位置。</para>
    ///
    /// <para>⚠ 不要在 <c>Render</c> 里发这个通知：宿主收到后要写 <c>Canvas.Left/Top</c>，那是在渲染过程中
    /// 改可视树布局。两个发点都在渲染之外，所以这里是直接 invoke、不做 Dispatcher 转发。</para>
    /// </summary>
    public event Action? SelectionGeometryChanged;

    private void RaiseSelectionGeometryChanged() => SelectionGeometryChanged?.Invoke();

    /// <summary>
    /// 当前选中的图元，**预览优先**：拖动/缩放期间文档里还是旧几何、预览里才是手上那一个，
    /// 与 <see cref="DrawSelectedShapeHandles"/> 取的是同一个表达式。无选中/图元已被撤销掉时为 null。
    /// </summary>
    public AnnotationShape? GetSelectedShapeForOverlay()
    {
        if (!SelectedShapeId.HasValue || Document == null) return null;
        return _previewShape != null && _previewShape.Id == SelectedShapeId.Value
            ? _previewShape
            : Document.Shapes.FirstOrDefault(x => x.Id == SelectedShapeId.Value);
    }

    public DrawingRectangleF? HoverAutoMosaicHighlight { get; set; }

    public AnnotationDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document != null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document != null) _document.Changed += OnDocumentChanged;
            // 换文档＝换一份内容，两份文档的 Revision 可能恰好同号，缓存必须显式作废
            _erasureClipCache.Clear();
            _erasureChunks.Clear();
            _erasures = Array.Empty<ErasureShape>();
            _erasureClipRevision = ulong.MaxValue;
            InvalidateVisual();
        }
    }

    public AnnotationShape? PreviewShape
    {
        get => _previewShape;
        set
        {
            // 不克隆：图元全部不可变，写入方（共享编辑器的 EditShape 结果、控制器每帧新建的 shape）
            // 交来的本就是当帧新实例，图层也只在本帧 Render 内读它。这里再 Clone 一次＝每帧白拷一条笔迹。
            _previewShape = value;
            if (value == null) ReleaseLiveStroke();   // 这段手势结束了，复用的那张位图没有下一个读者
            InvalidateVisual();
        }
    }

    public PixelPoint ScreenOrigin { get; set; }

    public double ScreenToDipScale { get; set; } = 1.0;

    /// <summary>
    /// 遮罩预览用的水印快照，null＝这一张不盖。水印是<b>效果</b>不是图元：不进文档、不进撤销栈，
    /// 由宿主（截图会话）在开关与参数改动当场整份换一个新实例下来。
    /// 贴图侧的标注层永远是 null——水印在成品位图里已经烘好了，贴图拿到的干净底图同样含它。
    /// </summary>
    public WatermarkConfig? Watermark
    {
        get => _watermark;
        set
        {
            if (ReferenceEquals(_watermark, value)) return;
            _watermark = value;
            InvalidateVisual();
        }
    }

    /// <summary><see cref="Watermark"/> 要盖的那块内容矩形（本控件的 DIP 坐标＝当前选区）。与配置同帧下发。</summary>
    public Rect WatermarkRegion
    {
        get => _watermarkRegion;
        set
        {
            if (_watermarkRegion == value) return;
            _watermarkRegion = value;
            InvalidateVisual();   // 选区挪动/改尺寸时配置对象可以没变，但落点必须跟着走
        }
    }

    public void SetAutoMosaicHighlights(IReadOnlyList<DrawingRectangleF>? highlights)
    {
        _autoMosaicHighlights = highlights ?? Array.Empty<DrawingRectangleF>();
        InvalidateVisual();
    }

    public void ClearAutoMosaicHighlights()
    {
        SetAutoMosaicHighlights(null);
        HoverAutoMosaicHighlight = null;
    }

    public void SetPreviewShape(AnnotationShape? shape)
    {
        PreviewShape = shape;
    }

    public void ClearPreviewShape()
    {
        PreviewShape = null;
    }

    /// <summary>画笔与橡皮擦的鼠标笔触，位置和直径均为本图层 DIP；仅供屏幕预览。</summary>
    public void SetPointerFootprint(Point? position, double diameter)
    {
        if (_pointerFootprint == position && _pointerFootprintDiameter == diameter) return;
        _pointerFootprint = position;
        _pointerFootprintDiameter = diameter;
        _pointerVisual.IsVisible = position.HasValue && diameter > 0;
        // 不使父图层进入 Arrange：Avalonia 的父级布局失效也会触发父级绘制记录更新。
        ArrangePointerFootprint();
        _pointerVisual.InvalidateVisual();
    }

    private void ClearEffectCaches()
    {
        ReleaseLiveStroke();

        foreach (Bitmap bitmap in _mosaicDownscaledCache.Values)
        {
            try { bitmap.Dispose(); } catch { }
        }
        _mosaicDownscaledCache.Clear();

        foreach (Bitmap bitmap in _blurDownscaledCache.Values)
        {
            try { bitmap.Dispose(); } catch { }
        }
        _blurDownscaledCache.Clear();

        foreach (StrokeView view in _strokeViewCache.Values)
        {
            try { view.Bitmap.Dispose(); } catch { }
        }
        _strokeViewCache.Clear();
        _strokeViewOrder.Clear();
        _strokeViewBytes = 0;
    }

    public override void Render(DrawingContext context)
    {
        // 无条件计时（每帧两次 GetTimestamp，成本可忽略）：写文档那一下几乎不花时间，真正压住鼠标的是这次排在
        // UI 线程上的整层重绘。橡皮擦的合帧闸门只认这个读数，见 CaptureSession.EraserPublishGateMs。
        long renderStartStamp = Stopwatch.GetTimestamp();
        base.Render(context);

        // 水印排在所有图元之前，落实"底图之上、所有标注之下"那条拍板：马赛克/模糊是对底图做像素处理，
        // 水印若盖在它们之上，就会出现"打了码的区域里还看得见字"。成品侧同一个顺序（Clone 之后、标注烘焙之前）。
        DrawWatermarkPreview(context);

        AnnotationDocument? document = Document;
        _renderSelectedShape = GetSelectedShapeForOverlay();
        var spotlights = new List<SpotlightShape>();
        var foregroundShapes = new List<AnnotationShape>();
        bool previewReplacedDocumentShape = false;
        if (document != null && document.Count > 0)
        {
            foreach (AnnotationShape shape in document.Shapes)
            {
                // 预览按 id **就地**顶替文档里的那一个，绘制顺序不变。旧写法只认模糊/聚光灯两类、
                // 其余预览一律追加到末尾，于是箭头/矩形改用预览语义后会先跳到最上层、松手再回跳。
                AnnotationShape drawn = shape;
                if (_previewShape != null && shape.Id == _previewShape.Id)
                {
                    drawn = _previewShape;
                    previewReplacedDocumentShape = true;
                }
                if (drawn is BlurShape blur)
                {
                    using (PushErasureClip(context, blur)) DrawBlurPreview(context, blur);
                    continue;
                }
                if (drawn is SpotlightShape spotlight)
                {
                    spotlights.Add(spotlight);
                    continue;
                }
                if (drawn is not ErasureShape) foregroundShapes.Add(drawn);
            }
        }

        // id 不在文档里＝还没落进文档的那一笔（起笔中、或新起的拖动预览），照旧画在最上层
        if (_previewShape != null && !previewReplacedDocumentShape)
        {
            if (_previewShape is BlurShape previewBlur)
            {
                DrawBlurPreview(context, previewBlur);
            }
            else if (_previewShape is SpotlightShape previewSpotlight)
            {
                spotlights.Add(previewSpotlight);
            }
            else
            {
                foregroundShapes.Add(_previewShape);
            }
        }

        using (PushErasureClip(context, spotlights))
            DrawSpotlightMask(context, spotlights);
        DrawSpotlightBorders(context, spotlights);
        foreach (AnnotationShape shape in foregroundShapes)
        {
            using (PushErasureClip(context, shape)) DrawShape(context, shape);
        }
        DrawAutoMosaicHighlights(context);
        DrawSelectedShapeHandles(context);
        DrawEraserRectanglePreview(context);

        long renderTook = Stopwatch.GetTimestamp() - renderStartStamp;
        LastRenderMs = renderTook * 1000.0 / Stopwatch.Frequency;
        if (Services.CaptureSession.EraserProbe)
        {
            ProbeRenderTicks += renderTook;
            ProbeRenderCount++;
            if (renderTook > ProbeRenderMaxTicks) ProbeRenderMaxTicks = renderTook;
        }
    }

    private void DrawEraserRectanglePreview(DrawingContext context)
    {
        if (_eraserRectanglePreview is not DrawingRectangleF rect || rect.Width <= 0 || rect.Height <= 0) return;
        if (!this.TryFindResource("SctAccentBrush", ActualThemeVariant, out object? accentValue)
            || accentValue is not IBrush accent) return;
        IBrush? fill = this.TryFindResource("SctAccentSoftBrush", ActualThemeVariant, out object? fillValue)
            ? fillValue as IBrush : null;
        context.DrawRectangle(fill, new Pen(accent, 1.5), ToDip(rect));
    }

    private IDisposable? PushErasureClip(DrawingContext context, AnnotationShape shape)
    {
        if (Document is not { } document) return null;
        if (IsErasureRevealed(shape)) return null;   // 选中的图元编辑期整块显形，不看洞
        long probeClipStart = Services.CaptureSession.EraserProbe ? Stopwatch.GetTimestamp() : 0;
        DrawingRectangleF bounds = shape.Bounds;
        EnsureErasureClipCacheCurrent(document);
        var key = new ErasureClipKey(shape.Id, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        if (!_erasureClipCache.TryGetValue(key, out Geometry[]? clips))
        {
            clips = _erasureChunks.GetClips(_erasures, erase => erase.AffectsShape(shape.Id), ToDip(bounds));
            // 这里只清批次列表；已经完成的洞裁切仍留在 _erasureChunks，不能被文档版本一起作废。
            if (_erasureClipCache.Count > 64) _erasureClipCache.Clear();
            _erasureClipCache[key] = clips;
        }
        // 取证分岔：到这里为止是"攒路径"（筛落点＋拼几何，布尔运算还没跑），
        // PushGeometryClip 那一下才是平台真正建裁切（Skia 的 Op + clip 安装）——两段分开才看得出余下的钱花在哪。
        long probeBuiltStamp = Services.CaptureSession.EraserProbe ? Stopwatch.GetTimestamp() : 0;
        IDisposable? pushed = ErasureClipCache.Push(context, clips);
        if (Services.CaptureSession.EraserProbe && probeClipStart != 0)
        {
            long probeEndStamp = Stopwatch.GetTimestamp();
            ProbeClipBuildTicks += probeBuiltStamp - probeClipStart;
            ProbeClipPushTicks += probeEndStamp - probeBuiltStamp;
            ProbeClipTicks += probeEndStamp - probeClipStart;
            ProbeClipCount++;
        }
        return pushed;
    }
    /// <summary>聚光灯暗幕铺满整个取景区，故洞只能按取景区筛选；与前景图元共用跨版本的批次裁切。</summary>
    private IDisposable? PushErasureClip(DrawingContext context, IReadOnlyList<SpotlightShape> spotlights)
    {
        if (spotlights.Count == 0 || Document is not { } document) return null;
        if (spotlights.Any(IsErasureRevealed)) return null;   // 组内任一颗被选中＝整组临时显形，见 IsErasureRevealed
        DrawingRectangleF overlay = SpotlightBoundsScreenRect
            ?? new DrawingRectangleF(SourceScreenRect.X, SourceScreenRect.Y, SourceScreenRect.Width, SourceScreenRect.Height);
        EnsureErasureClipCacheCurrent(document);
        Geometry[] clips = _erasureChunks.GetClips(_erasures,
            erase => spotlights.Any(spotlight => erase.AffectsShape(spotlight.Id)), ToDip(overlay));
        return ErasureClipCache.Push(context, clips);
    }

    /// <summary>缓存作废判据：文档内容动过（版本号），或洞几何所在的 DIP 画布口径动过
    /// （缩放倍率、窗口原点、图层尺寸——这三项改动不会 bump 文档版本）。</summary>
    private void EnsureErasureClipCacheCurrent(AnnotationDocument document)
    {
        var canvas = (ScreenToDipScale, ScreenOrigin.X, ScreenOrigin.Y, Bounds.Width, Bounds.Height);
        if (_erasureClipRevision == document.Revision && _erasureClipCanvas == canvas) return;
        _erasureClipCache.Clear();
        _erasureClipRevision = document.Revision;
        _erasureClipCanvas = canvas;
        _erasures = document.Shapes.OfType<ErasureShape>().ToArray();
        _erasureChunks.Configure(new Rect(Bounds.Size), new Matrix(ScreenToDipScale, 0, 0,
            ScreenToDipScale, -ScreenOrigin.X * ScreenToDipScale, -ScreenOrigin.Y * ScreenToDipScale));
        _erasureChunks.Retain(_erasures);
    }

    private void DrawPointerFootprint(DrawingContext context)
    {
        if (_pointerFootprint is not Point center || _pointerFootprintDiameter <= 0) return;
        double radius = _pointerFootprintDiameter / 2.0;
        // 外圈与主题强调色叠绘，明暗底图上都能辨认实际笔触边界。
        context.DrawEllipse(null, new Pen(Brushes.White, 3), center, radius, radius);
        if (this.TryFindResource("SctAccentBrush", ActualThemeVariant, out object? resource)
            && resource is IBrush accent)
            context.DrawEllipse(null, new Pen(accent, 1.5), center, radius, radius);
    }

    /// <summary>
    /// 水印的遮罩预览：几何与成品侧<b>同源</b>——落点走 <see cref="WatermarkEffect.AnchorOriginOf"/>、
    /// 平铺排布走 <see cref="WatermarkEffect.TileCenters"/>、旋转矩阵走 <see cref="WatermarkEffect.RotationMatrix"/>，
    /// 本方法只多做一件 Avalonia 才需要的事：把「成品图像像素」的 SizePx 乘 <see cref="ScreenToDipScale"/> 换成 DIP。
    /// ⚠ 文字宽度只能在这里量一次（Avalonia 排版引擎），与 GDI+ 那次测量差 1-2 像素属预期，勿加补偿常数。
    /// </summary>
    private void DrawWatermarkPreview(DrawingContext context)
    {
        WatermarkConfig? cfg = Watermark;
        Rect region = WatermarkRegion;
        if (cfg == null || !cfg.HasContent || region.Width <= 0 || region.Height <= 0) return;

        double scale = ScreenToDipScale > 0 ? ScreenToDipScale : 1.0;
        double fontSize = Math.Max(1.0, cfg.SizePx * scale);

        // 标注色 × 不透明度：与成品侧 Draw 里那句 alpha 计算逐字同式（先乘字色自带 alpha，再按百分比衰减）
        int argb = cfg.ColorArgb;
        byte alpha = (byte)Math.Round((argb >> 24 & 0xFF)
            * Math.Clamp(cfg.OpacityPercent, WatermarkConfig.MinOpacity, WatermarkConfig.MaxOpacity) / 100.0);
        var brush = new SolidColorBrush(Color.FromArgb(alpha,
            (byte)(argb >> 16 & 0xFF), (byte)(argb >> 8 & 0xFF), (byte)(argb & 0xFF)));

        var typeface = new Typeface(new FontFamily(WatermarkConfig.FontFamilyName), FontStyle.Normal,
            WatermarkConfig.FontBold ? FontWeight.Bold : FontWeight.Normal);
        var text = new FormattedText(cfg.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, brush);
        double textW = text.Width;
        double textH = fontSize;   // 不变量「字高＝大小」，与成品侧 textHeight=config.SizePx 同一条

        using (context.PushClip(region))
        {
            if (cfg.Position != WatermarkPosition.Tile)
            {
                var origin = WatermarkEffect.AnchorOriginOf(cfg.Position,
                    (float)region.Width, (float)region.Height, (float)textW, (float)textH);
                context.DrawText(text, new Point(region.X + origin.X, region.Y + origin.Y));
                return;
            }

            foreach (var center in WatermarkEffect.TileCenters(
                (float)region.Width, (float)region.Height, (float)textW, (float)textH, cfg.SpacingPercent))
            {
                double cx = region.X + center.X;
                double cy = region.Y + center.Y;
                WatermarkEffect.RotationMatrix(WatermarkEffect.TileAngleDegrees, cx, cy,
                    out double m11, out double m12, out double m21, out double m22, out double offX, out double offY);
                using (context.PushTransform(new Matrix(m11, m12, m21, m22, offX, offY)))
                {
                    // 旋转系里以中心为原点摆盒：左上角取 ±半宽高，与成品侧 (-textW/2, -textH/2) 同一式
                    context.DrawText(text, new Point(cx - textW / 2.0, cy - textH / 2.0));
                }
            }
        }
    }

    private void DrawShape(DrawingContext context, AnnotationShape shape)
    {
        if (shape is StrokeShape stroke)
        {
            DrawStroke(context, stroke);
        }
        else if (shape is ArrowShape arrow)
        {
            DrawArrow(context, arrow);
        }
        else if (shape is RectangleShape rect)
        {
            DrawAnnotationRectangle(context, rect);
        }
        else if (shape is CounterShape counter)
        {
            DrawCounter(context, counter);
        }
    }

    private void DrawStroke(DrawingContext context, StrokeShape stroke)
    {
        if (stroke.Samples.Count == 0) return;

        float opacity = BrushRasterizer.GetInkOpacity(stroke);
        if (opacity <= 0.0f) return;

        long bake = BrushRasterizer.BakeKey(stroke.Color, opacity);
        StrokeView? view;
        float shiftX = 0f, shiftY = 0f;
        if (_previewShape != null && _previewShape.Id == stroke.Id)
        {
            // 本帧被预览就地顶替＝正在拖、或正在落笔的那一条。栅格化对整体平移是等变的（出墨宽度看速度、
            // 笔锋看弧长、虚线看行进距离，全都不看绝对坐标），所以一条笔迹被整体挪动时不必重画：复用上次
            // 栅格化的位图、整条挪同样的差值即可（实测 600 点笔迹拖动一帧 1.62ms→0.32ms，与静止同一档；
            // 每帧 221KB 托管分配降到 33KB）。代价是拖动期间落点吸附到整像素＝最多偏半个像素，
            // 逐像素对照已证明吸附量本身为 0 时与新鲜栅格化完全一致（探针表 6）。
            // 复用与否由 IsPureTranslation 逐点实证，不靠哈希；松手写进文档后走下面文档图元那条绝对指纹路径，
            // 按真实绝对几何重建一次，成品逐像素精确的口径没有动过。
            if (!TryTakeLiveStroke(stroke, bake, out view, out shiftX, out shiftY))
            {
                view = BuildStrokeView(stroke);
                SetLiveStroke(stroke, view, bake);
            }
        }
        else
        {
            // 键 = 几何指纹 + 烘图键：改颜色或透明度只换视图，改笔迹才重新栅格化
            view = GetOrBuildStrokeView(stroke, new StrokeKey(BrushRasterizer.ComputeKey(stroke), bake));
        }
        if (view == null) return;

        // 取整到整像素＝保住 1:1 档"整数贴图、零滤波"的口径，代价是拖动中的预览最多偏半个像素，松手即归位
        Point origin = ToDip(new System.Drawing.PointF(
            view.OriginX + MathF.Round(shiftX), view.OriginY + MathF.Round(shiftY)));
        double destW = view.Width * ScreenToDipScale;
        double destH = view.Height * ScreenToDipScale;
        var dest = new Rect(origin.X, origin.Y, destW, destH);
        if (IsInkPixelPerfect)
        {
            // 截图侧口径：位图与目标逐像素相等，None 即精确 1:1 贴图，不加任何滤波
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
            context.DrawImage(view.Bitmap, dest);
            return;
        }

        // 贴图侧 _scale≠1 时目标尺寸≠位图尺寸。沿用 None＝最近邻重采样，笔迹会被放大成一格格的硬方块、
        // 缩小时逐像素抖动（用户实测"画出来的笔触坑坑洼洼有锯齿"）；而退出标注后成品走 Image 控件的
        // 高质量缩放，两者观感不一致。只对笔迹这一处改档，不影响同图层刻意要硬边的马赛克。
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        context.DrawImage(view.Bitmap, dest);
    }

    /// <summary>
    /// 笔迹预览位图是否与其目标绘制尺寸逐像素相等：一格位图在屏幕上占 <c>ScreenToDipScale × RenderScaling</c>
    /// 个物理像素，截图侧该乘积恒为 1（底图 1:1 显示），贴图侧等于 <c>_scale</c>。
    /// </summary>
    private bool IsInkPixelPerfect
    {
        get
        {
            double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            return Math.Abs(ScreenToDipScale * renderScaling - 1.0) < 0.01;
        }
    }

    /// <summary>
    /// 取（或按需栅格化并烘出）一条笔迹的预览位图。像素由 Core 的 <see cref="BrushRasterizer"/> 产出，
    /// 与成品图用的是同一批字节，预览侧只做一次零拷贝格式转换与 1:1 贴图。
    /// </summary>
    private StrokeView? GetOrBuildStrokeView(StrokeShape stroke, StrokeKey key)
    {
        if (_strokeViewCache.TryGetValue(key, out StrokeView? cached)) return cached;

        StrokeView? view = BuildStrokeView(stroke);
        if (view == null) return null;

        StoreStrokeView(key, view);
        return view;
    }

    /// <summary>按这条笔迹的真实绝对几何栅格化并烘一张位图；不进任何缓存。</summary>
    private static StrokeView? BuildStrokeView(StrokeShape stroke)
    {
        BrushCoverage coverage = BrushRasterizer.Build(stroke);
        if (coverage.Width <= 0 || coverage.Height <= 0) return null;

        using DrawingBitmap? baked = BrushRasterizer.BakeBitmap(
            coverage, stroke.Color, BrushRasterizer.GetInkOpacity(stroke));
        if (baked == null) return null;

        Bitmap? bitmap = Utils.BitmapConversion.ToAvaloniaBitmap(baked);
        if (bitmap == null) return null;

        return new StrokeView(bitmap, coverage.OriginX, coverage.OriginY, coverage.Width, coverage.Height);
    }

    /// <summary>
    /// 拖动中的复用判据：位图源与当前这一帧的预览<b>逐点</b>只差同一个位移（且笔宽/线型/画像/出墨字节键都没动），
    /// 才允许拿那张已栅格化的位图整条挪着用。任何形变（手柄缩放、继续落笔加采样点）都会在这里被判假而重画。
    /// 容差只用来吃掉浮点平移的舍入噪声（万分之一像素量级），真实形变远大于它。
    /// </summary>
    private static bool IsPureTranslation(StrokeShape source, StrokeShape target, out float dx, out float dy)
    {
        dx = 0f;
        dy = 0f;
        if (source.Thickness != target.Thickness || source.LineStyle != target.LineStyle) return false;
        if (!source.Profile.Equals(target.Profile)) return false;

        IReadOnlyList<StrokePoint> a = source.Samples;
        IReadOnlyList<StrokePoint> b = target.Samples;
        if (a.Count == 0 || a.Count != b.Count) return false;

        dx = b[0].X - a[0].X;
        dy = b[0].Y - a[0].Y;
        for (int i = 1; i < b.Count; i++)
        {
            if (b[i].Pressure != a[i].Pressure || b[i].TimestampMs != a[i].TimestampMs) return false;
            if (Math.Abs(b[i].X - a[i].X - dx) > TranslationEpsPx) return false;
            if (Math.Abs(b[i].Y - a[i].Y - dy) > TranslationEpsPx) return false;
        }
        return true;
    }

    private bool TryTakeLiveStroke(StrokeShape stroke, long bake,
        out StrokeView? view, out float shiftX, out float shiftY)
    {
        view = null;
        shiftX = 0f;
        shiftY = 0f;
        if (_liveView == null || _liveSource == null || bake != _liveBake) return false;
        if (!IsPureTranslation(_liveSource, stroke, out shiftX, out shiftY)) return false;
        view = _liveView;
        return true;
    }

    /// <summary>换掉那条正在拖的笔迹：旧位图当场释放（memo 只留一条，且不进字典，字典的淘汰不会碰它）。</summary>
    private void SetLiveStroke(StrokeShape stroke, StrokeView? view, long bake)
    {
        ReleaseLiveStroke();
        if (view == null) return;
        _liveSource = stroke;
        _liveView = view;
        _liveBake = bake;
    }

    private void ReleaseLiveStroke()
    {
        if (_liveView != null)
        {
            try { _liveView.Bitmap.Dispose(); } catch { }
            _liveView = null;
        }
        _liveSource = null;
        _liveBake = 0;
    }

    private void StoreStrokeView(StrokeKey key, StrokeView view)
    {
        _strokeViewCache[key] = view;
        _strokeViewOrder.Enqueue(key);
        _strokeViewBytes += view.Bytes;

        // 正在绘制的每一帧都是新指纹，不设上限会一路涨到内存耗尽；超预算就按放入顺序丢弃最早的视图。
        // 至少保留刚放进的这一条，否则超大笔迹会每帧重建又每帧被踢。
        while (_strokeViewOrder.Count > 1
            && (_strokeViewBytes > StrokeViewBudgetBytes || _strokeViewOrder.Count > StrokeViewMaxEntries))
        {
            StrokeKey oldest = _strokeViewOrder.Dequeue();
            if (_strokeViewCache.Remove(oldest, out StrokeView? evicted))
            {
                _strokeViewBytes -= evicted.Bytes;
                try { evicted.Bitmap.Dispose(); } catch { }
            }
        }
    }

    /// <summary>笔迹视图缓存键：几何指纹（含笔刷画像与线型）+ 烘图键（颜色与不透明度）。</summary>
    private readonly struct StrokeKey
    {
        public StrokeKey(long geometry, long bake)
        {
            Geometry = geometry;
            Bake = bake;
        }

        public long Geometry { get; }

        public long Bake { get; }

        public override int GetHashCode() => Geometry.GetHashCode() * 397 ^ Bake.GetHashCode();

        public override bool Equals(object? obj)
            => obj is StrokeKey other && other.Geometry == Geometry && other.Bake == Bake;
    }

    /// <summary>一条笔迹烘好的预览位图及其在屏幕物理像素下的落点。</summary>
    private sealed class StrokeView
    {
        public StrokeView(Bitmap bitmap, int originX, int originY, int width, int height)
        {
            Bitmap = bitmap;
            OriginX = originX;
            OriginY = originY;
            Width = width;
            Height = height;
            Bytes = width * height * 4;
        }

        public Bitmap Bitmap { get; }

        public int OriginX { get; }

        public int OriginY { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>位图占用的字节数（Bgra8888），用于缓存预算。</summary>
        public int Bytes { get; }
    }

    private void DrawArrow(DrawingContext context, ArrowShape arrow)
    {
        var brush = new SolidColorBrush(Color.FromArgb(arrow.Color.A, arrow.Color.R, arrow.Color.G, arrow.Color.B));
        // 箭头尺寸一律按物理像素口径取值，与画笔/形状/聚光灯的粗细单位一致
        double dipScale = ScreenToDipScale;
        DrawArrowGeometry(context, brush, ToDip(arrow.Start), ToDip(arrow.End), ToDip(arrow.ControlPoint),
            arrow.Thickness, arrow.Style, Math.Sqrt(Math.Max(arrow.Scale, 0.01f)) * dipScale, 2.0 * dipScale);
    }

    /// <summary>
    /// 箭头的画法本体：画布预览、参数条小样（<see cref="ArrowHeadSwatch"/>）与样式下拉共用这一份，
    /// "菜单里看到的形状"与"画在图上的形状"因此不可能分叉。
    /// </summary>
    /// <param name="thickness">笔宽（物理像素口径），内部按现状口径夹到 1~45。</param>
    /// <param name="visualScale">DIP 空间里"1 个单位像素"有多长＝√scale × dipScale（小样恒 1）。</param>
    /// <param name="bendThreshold">控制点离中点小于它就按直线箭头画（小样传 <see cref="double.MaxValue"/>，恒画直）。</param>
    /// <param name="legacyPath">Designed / Sharp 两档是否套旧模板。箭头工具与历史存档要它；
    /// 序号引线那条细杆从来没没过模板，那一边传 false＝这两档按新算式画成深凹口与实心三角。</param>
    /// <param name="doubleHead">V 形家族那四档（凹口／弧边燕尾／笔锋／开口 V）是否两端各挂一个头。
    /// 箭头标注工具与其小样要它（默认 true），序号引线那头钉在圆点上、恒单头。</param>
    internal static void DrawArrowGeometry(DrawingContext context, IBrush brush, Point start, Point end, Point control,
        double thickness, ArrowAnnotationStyle style, double visualScale, double bendThreshold, bool legacyPath = true,
        bool doubleHead = true)
    {
        double dxLen = end.X - start.X; double dyLen = end.Y - start.Y; double len = Math.Sqrt(dxLen * dxLen + dyLen * dyLen);
        if (len < 1.0) return;
        thickness = Math.Clamp(Math.Max(1.0, thickness), 1.0, 45.0);

        // 判断是否为直线箭头（控制点接近中点的阈值由调用方给）
        double midX = (start.X + end.X) / 2.0;
        double midY = (start.Y + end.Y) / 2.0;
        double ctrlOff = Math.Sqrt((control.X - midX) * (control.X - midX) + (control.Y - midY) * (control.Y - midY));
        bool straight = ctrlOff < bendThreshold;

        if (legacyPath && ArrowHeadGeometry.IsLegacyStyle(style))
        {
            LegacyArrowGeometry arrowGeometry = straight
                // 直线箭头 — 原始样式
                ? CreateLegacyArrowGeometry(start, end, thickness, style, visualScale)
                // 弯曲箭头 — 原始样式沿贝塞尔曲线
                : CreateBentArrowGeometry(start, end, control, thickness, style, visualScale);
            context.DrawGeometry(brush, null, arrowGeometry.Shaft);
            context.DrawGeometry(brush, null, arrowGeometry.Head);
            return;
        }

        // 新增头型：几何全在 Core 那一份算式里，预览只照点列填/描（导出侧 RenderArrow 消费同一份）
        float unit = (float)visualScale;
        float worldWidth = (float)thickness * unit;
        var parts = straight
            ? ArrowHeadGeometry.BuildStraight(ToGdi(start), ToGdi(end), worldWidth, unit, style, doubleHead)
            : ArrowHeadGeometry.BuildBent(ToGdi(start), ToGdi(control), ToGdi(end), worldWidth, unit, style, doubleHead);
        foreach (ArrowPart part in parts) DrawArrowPart(context, brush, part);
    }

    /// <summary>预览坐标系本身就是 DIP，故这里的"换算"只换类型不缩放。</summary>
    private static System.Drawing.PointF ToGdi(Point p) => new((float)p.X, (float)p.Y);

    /// <summary>箭头部件：填充件直接压多边形，描边件用圆角连接的开放/闭合折线（与导出侧 DrawArrowPart 同一套）。</summary>
    internal static void DrawArrowPart(DrawingContext context, IBrush brush, ArrowPart part)
    {
        if (part.Points.Length < 2) return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext gc = geometry.Open())
        {
            gc.BeginFigure(new Point(part.Points[0].X, part.Points[0].Y), part.Kind == ArrowPartKind.Fill);
            for (int i = 1; i < part.Points.Length; i++)
                gc.LineTo(new Point(part.Points[i].X, part.Points[i].Y));
            gc.EndFigure(part.Kind != ArrowPartKind.Stroke);
        }

        if (part.Kind == ArrowPartKind.Fill)
        {
            context.DrawGeometry(brush, null, geometry);
            return;
        }
        context.DrawGeometry(null, new Pen(brush, part.Width)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = part.RoundCap ? PenLineCap.Round : PenLineCap.Flat,
        }, geometry);
    }

    private void DrawAnnotationRectangle(DrawingContext context, RectangleShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var brush = new SolidColorBrush(Color.FromArgb(shape.StrokeColor.A, shape.StrokeColor.R, shape.StrokeColor.G, shape.StrokeColor.B));
        var pen = CreateLinePen(brush, Math.Max(1.0, shape.StrokeThickness * ScreenToDipScale), shape.LineStyle);
        // 填充色不单存字段：RGB 恒取线条色，只把 alpha 换成浓淡档（线条自身的 alpha 不参与，
        // 否则"半透明描边"会连带把填充也一起变淡，两件事在图元上是两个独立档）。
        IBrush? fillBrush = shape.FillOpacity > 0.0f
            ? new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(shape.FillOpacity * 255.0f, 0.0f, 255.0f),
                shape.StrokeColor.R, shape.StrokeColor.G, shape.StrokeColor.B))
            : null;
        if (shape.ShapeKind == ShapeAnnotationKind.Ellipse)
        {
            context.DrawEllipse(fillBrush, pen, rect.Center, rect.Width / 2.0, rect.Height / 2.0);
        }
        else
        {
            // 圆角半径与图元几何同坐标系，故与 Rect 一起走 ToDip 折算；上限＝短边一半，
            // 与 SpotlightShape 同一口径（不会自交，也不会因半径过大而退化成椭圆）。
            double radius = shape.CornerRadius > 0
                ? Math.Clamp(shape.CornerRadius * ScreenToDipScale, 0.0, Math.Min(rect.Width, rect.Height) / 2.0)
                : 0.0;
            context.DrawRectangle(fillBrush, pen, rect, radius, radius);
        }
    }

    private static Pen CreateLinePen(IBrush brush, double thickness, AnnotationLineStyle lineStyle)
    {
        var pen = new Pen(brush, thickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (lineStyle == AnnotationLineStyle.DashLarge)
        {
            pen.DashStyle = new DashStyle(AnnotationRenderOptions.DashLargePattern, 0);
        }

        return pen;
    }

    private sealed class LegacyArrowGeometry
    {
        public LegacyArrowGeometry(Geometry shaft, Geometry head)
        {
            Shaft = shaft;
            Head = head;
        }

        public Geometry Shaft { get; }

        public Geometry Head { get; }
    }

    /// <summary>
    /// Original template-based arrow geometry (straight).
    /// </summary>
    private static LegacyArrowGeometry CreateLegacyArrowGeometry(Point start, Point end, double thickness,
        ArrowAnnotationStyle style, double visualScale)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0) return new LegacyArrowGeometry(new StreamGeometry(), new StreamGeometry());

        double ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
        double templateHeight = 50.0 * visualScale;
        double headSize = 50.0 * visualScale;
        double headOverlap = 25.0 * visualScale;
        double shaftSlotWidth = Math.Max(1.0, len - headOverlap);
        double shaftXScale = shaftSlotWidth / 100.0;
        double shaftYScale = templateHeight / 100.0;
        double headX = len - headSize;
        double headScale = headSize / 100.0;
        double mappedThickness = Math.Clamp(thickness, 0.5, 45.0);
        double ctrlYTop = 50.0 - mappedThickness;
        double ctrlYBottom = 50.0 + mappedThickness;

        Point T(double x, double y) {
            double lx = x * shaftXScale, ly = (y - 50.0) * shaftYScale;
            return new Point(start.X + ux*lx + nx*ly, start.Y + uy*lx + ny*ly);
        }
        Point H(double x, double y) {
            double lx = headX + x * headScale, ly = (y - 50.0) * headScale;
            return new Point(start.X + ux*lx + nx*ly, start.Y + uy*lx + ny*ly);
        }

        var shaftGeom = new StreamGeometry();
        using (var ctx = shaftGeom.Open()) {
            if (style == ArrowAnnotationStyle.Sharp) {
                ctx.BeginFigure(T(0, 40), true);
                ctx.QuadraticBezierTo(T(50, ctrlYTop), T(100, 35));
                ctx.LineTo(T(100, 65));
                ctx.QuadraticBezierTo(T(50, ctrlYBottom), T(0, 60));
            } else {
                ctx.BeginFigure(T(0, 50), true);
                ctx.QuadraticBezierTo(T(50, ctrlYTop), T(100, 35));
                ctx.LineTo(T(100, 65));
                ctx.QuadraticBezierTo(T(50, ctrlYBottom), T(0, 50));
            }
            ctx.EndFigure(true);
        }

        var headGeom = new StreamGeometry();
        using (var ctx = headGeom.Open()) {
            if (style == ArrowAnnotationStyle.Sharp) {
                ctx.BeginFigure(H(20, 20), true);
                ctx.LineTo(H(100, 50)); ctx.LineTo(H(20, 80));
            } else {
                ctx.BeginFigure(H(50, 50), true);
                ctx.LineTo(H(20, 20)); ctx.LineTo(H(100, 50)); ctx.LineTo(H(20, 80));
            }
            ctx.EndFigure(true);
        }
        return new LegacyArrowGeometry(shaftGeom, headGeom);
    }

    /// <summary>
    /// Curved arrow - original template swept along bezier.
    /// </summary>
    private static LegacyArrowGeometry CreateBentArrowGeometry(Point start, Point end, Point control, double thickness,
        ArrowAnnotationStyle style, double visualScale)
    {
        double th = 50.0 * visualScale, hs = 50.0 * visualScale, ho = 25.0 * visualScale;
        double mt2 = Math.Clamp(thickness, 0.5, 45.0);
        double cYt = 50.0 - mt2, cYb = 50.0 + mt2;
        double ch1 = Math.Sqrt((control.X-start.X)*(control.X-start.X)+(control.Y-start.Y)*(control.Y-start.Y));
        double ch2 = Math.Sqrt((end.X-control.X)*(end.X-control.X)+(end.Y-control.Y)*(end.Y-control.Y));
        double aLen = ch1 + ch2;
        double ssw = Math.Max(1.0, aLen - ho);
        double sYs = th / 100.0;
        const int N = 40;
        var up = new Point[N]; var lp = new Point[N];
        for (int i = 0; i < N; i++) {
            double t = i / (double)(N - 1);
            double uy, ly;
            if (style == ArrowAnnotationStyle.Sharp) {
                uy = (1-t)*(1-t)*40.0 + 2*(1-t)*t*cYt + t*t*35.0;
                ly = (1-t)*(1-t)*60.0 + 2*(1-t)*t*cYb + t*t*65.0;
            } else {
                uy = (1-t)*(1-t)*50.0 + 2*(1-t)*t*cYt + t*t*35.0;
                ly = (1-t)*(1-t)*50.0 + 2*(1-t)*t*cYb + t*t*65.0;
            }
            double mt = 1.0 - t;
            double cx = mt*mt*start.X + 2*mt*t*control.X + t*t*end.X;
            double cy = mt*mt*start.Y + 2*mt*t*control.Y + t*t*end.Y;
            double tx = 2*mt*(control.X-start.X) + 2*t*(end.X-control.X);
            double ty = 2*mt*(control.Y-start.Y) + 2*t*(end.Y-control.Y);
            double tl = Math.Sqrt(tx*tx + ty*ty);
            double nx = 0, ny = 0;
            if (tl > 0.001) { nx = -ty/tl; ny = tx/tl; }
            double yo = (uy - 50.0) * sYs; up[i] = new Point(cx + nx*yo, cy + ny*yo);
            yo = (ly - 50.0) * sYs; lp[i] = new Point(cx + nx*yo, cy + ny*yo);
        }
        double eTx = 2.0*(end.X-control.X), eTy = 2.0*(end.Y-control.Y);
        double eTl = Math.Sqrt(eTx*eTx + eTy*eTy);
        if (eTl < 0.001) {
            eTx = end.X-start.X; eTy = end.Y-start.Y;
            eTl = Math.Sqrt(eTx*eTx + eTy*eTy);
            if (eTl < 0.001) return new LegacyArrowGeometry(new StreamGeometry(), new StreamGeometry());
        }
        double eUx = eTx/eTl, eUy = eTy/eTl, eNx = -eUy, eNy = eUx;
        double hSc = hs/100.0, hbO = ho - hs;
        Point H(double hx, double hy) {
            double lx = hbO + hx*hSc, ly = (hy-50.0)*hSc;
            return new Point(end.X + eUx*lx + eNx*ly, end.Y + eUy*lx + eNy*ly);
        }
        var sh = new StreamGeometry();
        using (var ctx = sh.Open()) {
            ctx.BeginFigure(up[0], true);
            for (int i = 1; i < N; i++) ctx.LineTo(up[i]);
            for (int i = N-1; i >= 0; i--) ctx.LineTo(lp[i]);
            ctx.EndFigure(true);
        }
        var hd = new StreamGeometry();
        using (var ctx = hd.Open()) {
            if (style == ArrowAnnotationStyle.Sharp) {
                ctx.BeginFigure(H(20, 20), true);
                ctx.LineTo(H(100, 50)); ctx.LineTo(H(20, 80));
            } else {
                ctx.BeginFigure(H(50, 50), true);
                ctx.LineTo(H(20, 20)); ctx.LineTo(H(100, 50)); ctx.LineTo(H(20, 80));
            }
            ctx.EndFigure(true);
        }
        return new LegacyArrowGeometry(sh, hd);
    }

    private void DrawBlurPreview(DrawingContext context, BlurShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (shape.UiLevel <= 0)
        {
            return;
        }

        if (shape.Mode == BlurMode.Mosaic)
        {
            DrawMosaicPreview(context, shape);
            return;
        }

        DrawBlurApproxPreview(context, shape);
    }

    private void DrawMosaicPreview(DrawingContext context, BlurShape shape)
    {
        DrawingBitmap? source = SourceBitmap;
        if (source == null || source.Width <= 0 || source.Height <= 0)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Mosaic");
            return;
        }

        int block = Math.Max(2, shape.MosaicBlockSize);
        Bitmap? downscaled = GetOrBuildMosaicDownscaled(source, block);
        if (downscaled == null)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Mosaic");
            return;
        }

        DrawingRectangleF r = shape.Rect;
        double localX = r.Left - SourceScreenRect.X;
        double localY = r.Top - SourceScreenRect.Y;
        double localRight = r.Right - SourceScreenRect.X;
        double localBottom = r.Bottom - SourceScreenRect.Y;
        if (localRight <= localX || localBottom <= localY) return;

        double vx = Math.Floor(localX / block);
        double vy = Math.Floor(localY / block);
        double vx2 = Math.Ceiling(localRight / block);
        double vy2 = Math.Ceiling(localBottom / block);
        double vw = Math.Max(1.0, vx2 - vx);
        double vh = Math.Max(1.0, vy2 - vy);

        if (vx < 0) { vw += vx; vx = 0; }
        if (vy < 0) { vh += vy; vy = 0; }
        if (vx + vw > downscaled.PixelSize.Width) vw = downscaled.PixelSize.Width - vx;
        if (vy + vh > downscaled.PixelSize.Height) vh = downscaled.PixelSize.Height - vy;
        if (vw <= 0.5 || vh <= 0.5) return;

        double alignedLeft = SourceScreenRect.X + vx * block;
        double alignedTop = SourceScreenRect.Y + vy * block;
        double alignedRight = SourceScreenRect.X + (vx + vw) * block;
        double alignedBottom = SourceScreenRect.Y + (vy + vh) * block;

        using (context.PushClip(ToDip(r)))
        {
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
            context.DrawImage(
                downscaled,
                new Rect(vx, vy, vw, vh),
                ToDip(DrawingRectangleF.FromLTRB((float)alignedLeft, (float)alignedTop, (float)alignedRight, (float)alignedBottom)));
        }
    }

    private Bitmap? GetOrBuildMosaicDownscaled(DrawingBitmap source, int block)
    {
        if (_mosaicDownscaledCache.TryGetValue(block, out Bitmap? cached)) return cached;

        int downW = Math.Max(1, (source.Width + block - 1) / block);
        int downH = Math.Max(1, (source.Height + block - 1) / block);
        using var down = new DrawingBitmap(downW, downH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(down))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.DrawImage(source, new System.Drawing.Rectangle(0, 0, downW, downH));
        }

        Bitmap? bitmap = ToAvaloniaBitmap(down);
        if (bitmap != null) _mosaicDownscaledCache[block] = bitmap;
        return bitmap;
    }

    private static Avalonia.Media.Imaging.Bitmap? ToAvaloniaBitmap(DrawingBitmap bitmap)
    {
        return Utils.BitmapConversion.ToAvaloniaBitmap(bitmap);
    }

    private void DrawBlurApproxPreview(DrawingContext context, BlurShape shape)
    {
        DrawingBitmap? source = SourceBitmap;
        if (source == null || source.Width <= 0 || source.Height <= 0)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Blur");
            return;
        }

        Bitmap? blurredBitmap = GetOrBuildBlurDownscaled(source, shape.UiLevel);
        if (blurredBitmap == null)
        {
            DrawEffectFallback(context, ToDip(shape.Rect), "Blur");
            return;
        }

        DrawingRectangleF r = shape.Rect;
        double localX = r.Left - SourceScreenRect.X;
        double localY = r.Top - SourceScreenRect.Y;
        double localRight = r.Right - SourceScreenRect.X;
        double localBottom = r.Bottom - SourceScreenRect.Y;
        if (localRight <= localX || localBottom <= localY) return;

        double scaleX = (double)blurredBitmap.PixelSize.Width / source.Width;
        double scaleY = (double)blurredBitmap.PixelSize.Height / source.Height;

        double srcLeft = localX * scaleX;
        double srcTop = localY * scaleY;
        double srcRight = localRight * scaleX;
        double srcBottom = localBottom * scaleY;

        double clampLeft = Math.Clamp(srcLeft, 0, blurredBitmap.PixelSize.Width);
        double clampTop = Math.Clamp(srcTop, 0, blurredBitmap.PixelSize.Height);
        double clampRight = Math.Clamp(srcRight, 0, blurredBitmap.PixelSize.Width);
        double clampBottom = Math.Clamp(srcBottom, 0, blurredBitmap.PixelSize.Height);
        if (clampRight <= clampLeft || clampBottom <= clampTop) return;

        double alignedLeft = SourceScreenRect.X + clampLeft / scaleX;
        double alignedTop = SourceScreenRect.Y + clampTop / scaleY;
        double alignedRight = SourceScreenRect.X + clampRight / scaleX;
        double alignedBottom = SourceScreenRect.Y + clampBottom / scaleY;

        using (context.PushClip(ToDip(r)))
        {
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
            context.DrawImage(
                blurredBitmap,
                new Rect(clampLeft, clampTop, clampRight - clampLeft, clampBottom - clampTop),
                ToDip(DrawingRectangleF.FromLTRB((float)alignedLeft, (float)alignedTop, (float)alignedRight, (float)alignedBottom)));
        }
    }

    private Bitmap? GetOrBuildBlurDownscaled(DrawingBitmap source, int uiLevel)
    {
        if (_blurDownscaledCache.TryGetValue(uiLevel, out Bitmap? cached)) return cached;

        double intensity = BlurIntensityMapper.UiToPhysical(uiLevel);
        if (intensity <= 0.0) return null;

        // 统一使用全图 2x 降采样的高性能滤镜图层（一次生成，全屏毫秒级复用，60/144FPS 零开销拖拽跟随）
        using DrawingBitmap blurredLayer = GaussianBlurHelper.CreateBlurredLayer(source, intensity, downsampleFactor: 2);
        Bitmap? bitmap = ToAvaloniaBitmap(blurredLayer);
        if (bitmap != null)
        {
            _blurDownscaledCache[uiLevel] = bitmap;
        }
        return bitmap;
    }

    private void DrawEffectFallback(DrawingContext context, Rect rect, string label)
    {
        var fill = new SolidColorBrush(Color.FromArgb(72, 51, 136, 255));
        var stroke = new SolidColorBrush(Color.FromArgb(220, 51, 136, 255));
        var pen = new Pen(stroke, 2.0);
        context.DrawRectangle(fill, pen, rect);
    }

    private void DrawSpotlightMask(DrawingContext context, IReadOnlyList<SpotlightShape> spotlights)
    {
        if (spotlights.Count == 0) return;

        DrawingRectangleF boundsScreen = SpotlightBoundsScreenRect ?? new DrawingRectangleF(SourceScreenRect.X, SourceScreenRect.Y, SourceScreenRect.Width, SourceScreenRect.Height);
        if (boundsScreen.Width <= 0 || boundsScreen.Height <= 0) return;

        byte alpha = (byte)Math.Round(Math.Clamp(spotlights[0].Darkness, 0.0f, 1.0f) * 255.0f);
        if (alpha == 0) return;

        Geometry? unionHoles = CreateSpotlightUnionGeometry(spotlights);
        if (unionHoles == null) return;

        var overlay = new RectangleGeometry(ToDip(boundsScreen));
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude, overlay, unionHoles);
        var fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        context.DrawGeometry(fill, null, mask);
    }

    private void DrawSpotlightBorders(DrawingContext context, IReadOnlyList<SpotlightShape> spotlights)
    {
        if (spotlights.Count == 0) return;
        foreach (SpotlightShape shape in spotlights)
        {
            using var erased = PushErasureClip(context, shape);
            if (shape.StrokeThickness <= 0.0f) continue;

            var stroke = new SolidColorBrush(Color.FromArgb(shape.StrokeColor.A, shape.StrokeColor.R, shape.StrokeColor.G, shape.StrokeColor.B));

            // 重叠合并（判据见 SpotlightOutlineMerge）：把这一圈挖到"其余同色同粗图元并集之外"，
            // 与暗幕同一口径——洞口是并集，边框也只剩并集的外轮廓，重叠处不再交叉。
            List<SpotlightShape> clipSources = SpotlightOutlineMerge.ClipSources(spotlights, shape);
            Geometry? othersUnion = clipSources.Count > 0 ? CreateSpotlightUnionGeometry(clipSources) : null;
            if (othersUnion == null)
            {
                // 与同组谁都不挨着（绝大多数情况）：整圈居中描边，与合并改动前逐像素相同，一次布尔都不做
                Geometry? geom = CreateSpotlightGeometry(shape);
                if (geom == null) continue;
                var pen = new Pen(stroke, Math.Max(1.0, shape.StrokeThickness * ScreenToDipScale));
                context.DrawGeometry(null, pen, geom);
                continue;
            }

            Geometry? ring = CreateMergedSpotlightRing(shape, othersUnion);
            if (ring != null) context.DrawGeometry(stroke, null, ring);
        }
    }

    /// <summary>
    /// 重叠时改用的"环带填充"几何：外缘＝洞口外扩 t/2、内缘＝洞口内缩 t/2，再挖掉其余图元的并集，
    /// 与成品侧 <c>CreateSpotlightLocalPath(±half)</c> 同一构造（保护条：边框＝以洞口边界居中的满宽环带）。
    /// <para>为什么不用裁剪：Avalonia 11 的 <c>DrawingContext.PushClip</c> 只收 Rect／RoundedRect，
    /// 不吃 Geometry，所以"把描边裁到并集之外"只能在几何上求差；求差后的环带正好只剩并集的外轮廓那一段。</para>
    /// </summary>
    private Geometry? CreateMergedSpotlightRing(SpotlightShape shape, Geometry othersUnion)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        double half = Math.Max(1.0, shape.StrokeThickness * ScreenToDipScale) / 2.0;
        double radius = Math.Clamp(shape.CornerRadius * ScreenToDipScale, 0.0, Math.Min(rect.Width, rect.Height) / 2.0);

        Geometry? outer = CreateSpotlightOffsetGeometry(rect, radius, shape.ShapeKind, half);
        Geometry? inner = CreateSpotlightOffsetGeometry(rect, radius, shape.ShapeKind, -half);
        if (outer == null) return null;

        Geometry ring = inner == null
            ? outer                                    // 缩到没面积了（极小图元）：整块填，与成品侧同一退化
            : new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        return new CombinedGeometry(GeometryCombineMode.Exclude, ring, othersUnion);
    }

    /// <summary>洞口沿法向偏移 <paramref name="inflate"/>（负值＝内缩）后的几何；缩到零面积返回 null。
    /// 圆角半径同步偏移，与成品侧 <c>CreateRoundedRectanglePath(local, CornerRadius + inflate)</c> 同一口径。</summary>
    private static Geometry? CreateSpotlightOffsetGeometry(Rect rect, double radius, SpotlightShapeKind kind, double inflate)
    {
        var local = new Rect(rect.X - inflate, rect.Y - inflate, rect.Width + inflate * 2, rect.Height + inflate * 2);
        if (local.Width <= Math.Abs(inflate) * 2 || local.Height <= Math.Abs(inflate) * 2) return null;

        if (kind == SpotlightShapeKind.Ellipse) return new EllipseGeometry(local);

        double max = Math.Min(local.Width, local.Height) / 2.0;
        double r = Math.Min(Math.Max(0.0, radius + inflate), max);
        return new RectangleGeometry(local, r, r);
    }

    private Geometry? CreateSpotlightUnionGeometry(IReadOnlyList<SpotlightShape> spotlights)
    {
        Geometry? union = null;
        foreach (SpotlightShape shape in spotlights)
        {
            Geometry? next = CreateSpotlightGeometry(shape);
            if (next == null) continue;
            union = union == null ? next : new CombinedGeometry(GeometryCombineMode.Union, union, next);
        }
        return union;
    }

    private Geometry? CreateSpotlightGeometry(SpotlightShape shape)
    {
        Rect rect = ToDip(shape.Rect);
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (shape.ShapeKind == SpotlightShapeKind.Ellipse)
        {
            return new EllipseGeometry(rect);
        }

        double radius = Math.Clamp(shape.CornerRadius * ScreenToDipScale, 0.0, Math.Min(rect.Width, rect.Height) / 2.0);
        return new RectangleGeometry(rect, radius, radius);
    }

    // 画法本体在 CounterPainter：工具栏上的样式小样要走同一份，否则"条上看到的"和"画布上的"会分叉
    private void DrawCounter(DrawingContext context, CounterShape shape)
        => CounterPainter.Paint(context, shape, ScreenToDipScale, ToDip);

    private void DrawAutoMosaicHighlights(DrawingContext context)
    {
        if (_autoMosaicHighlights.Count == 0) return;
        var stroke = new SolidColorBrush(Color.FromArgb(180, 76, 194, 255));
        var hoverStroke = new SolidColorBrush(Color.FromArgb(230, 76, 194, 255));
        foreach (DrawingRectangleF r in _autoMosaicHighlights)
        {
            Rect rect = ToDip(r);
            bool hover = HoverAutoMosaicHighlight.HasValue && NearlySameRect(HoverAutoMosaicHighlight.Value, r);
            context.DrawRectangle(null, new Pen(hover ? hoverStroke : stroke, hover ? 2.0 : 1.5), rect, 3, 3);
        }
    }

    private void DrawSelectedShapeHandles(DrawingContext context)
    {
        if (!SelectedShapeId.HasValue || Document == null) return;
        AnnotationShape? selected = _previewShape != null && _previewShape.Id == SelectedShapeId.Value
            ? _previewShape
            : Document.Shapes.FirstOrDefault(x => x.Id == SelectedShapeId.Value);

        if (selected == null) return;

        // 根据不同形状类型绘制选中状态
        switch (selected)
        {
            case BlurShape blur:
                DrawBlurSelectedHandles(context, blur);
                break;
            case SpotlightShape spotlight:
                DrawSpotlightSelectedHandles(context, spotlight);
                break;
            case ArrowShape arrow:
                DrawArrowSelectedHandles(context, arrow);
                break;
            case RectangleShape rectangle:
                DrawRectangleSelectedHandles(context, rectangle);
                break;
            case CounterShape counter:
                DrawCounterSelectedHandles(context, counter);
                break;
            case StrokeShape:
                DrawShapeSelectionOutline(context, selected);
                break;
        }
    }

    /// <summary>
    /// 选中反馈的通用形态：绘制图元外接包围盒细框。六类图元都实现了 <c>Bounds</c>
    /// （箭头含控制点与箭头尺寸留白、序号为圆的包围盒），故一份实现覆盖全部类型。
    /// 画笔在截图侧本来就只给框不给手柄（平移可拖、不提供缩放），此处即其原实现。
    /// </summary>
    private void DrawShapeSelectionOutline(DrawingContext context, AnnotationShape shape)
    {
        DrawingRectangleF bounds = shape.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var outline = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        context.DrawRectangle(null, new Pen(outline, 1.0), rect);
    }

    private void DrawBlurSelectedHandles(DrawingContext context, BlurShape blur)
    {
        DrawingRectangleF bounds = blur.Rect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.0);
        context.DrawRectangle(null, pen, rect);

        var fill = Brushes.White;
        double size = 10.0;
        double half = size / 2.0;
        Point[] points =
        {
            new(rect.Left, rect.Top),
            new(rect.Left + rect.Width / 2.0, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2.0),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2.0, rect.Bottom),
            new(rect.Left, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2.0),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, half, half);
        }
    }

    private void DrawSpotlightSelectedHandles(DrawingContext context, SpotlightShape spotlight)
    {
        DrawingRectangleF bounds = spotlight.Rect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        Rect selectionRect = rect.Inflate(3.0);
        var stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x88, 0xFF));
        var pen = new Pen(stroke, 1.0);
        context.DrawRectangle(null, pen, selectionRect);

        var fill = Brushes.White;
        double size = 10.0;
        double half = size / 2.0;
        Point[] points =
        {
            new(rect.Left, rect.Top),
            new(rect.Left + rect.Width / 2.0, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2.0),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2.0, rect.Bottom),
            new(rect.Left, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2.0),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, half, half);
        }

        // 聚光灯的四角圆角手柄已摘除（2026-09-18）：圆角改由「选中后元素上方的浮条」调整，
        // 画布上不再有第五类把手，选中态只留 8 向缩放手柄。
        //（矩形图元那档同日稍后跟上，见 DrawRectangleSelectedHandles——现在两侧完全同一套。）
    }

    private void DrawArrowSelectedHandles(DrawingContext context, ArrowShape arrow)
    {
        Point start = ToDip(arrow.Start);
        Point end = ToDip(arrow.End);
        Point control = ToDip(arrow.ControlPoint);

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.0);
        var fill = Brushes.White;
        double size = 12.0;
        double half = size / 2.0;

        // 绘制起点和终点锚点
        context.DrawEllipse(fill, pen, start, half, half);
        context.DrawEllipse(fill, pen, end, half, half);

        // 绘制中间控制端点（稍大，带蓝色填充，表示可拖动弯曲）
        double midSize = 14.0;
        double midHalf = midSize / 2.0;
        var midFill = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var midPen = new Pen(Brushes.White, 2.0);
        context.DrawEllipse(midFill, midPen, control, midHalf, midHalf);
        // 内部白色小点
        var innerFill = Brushes.White;
        context.DrawEllipse(innerFill, null, control, midHalf * 0.35, midHalf * 0.35);

        // 绘制弯曲辅助弧线（虚线贝塞尔参考线）
        var dashedPen = new Pen(stroke, 1.0) { DashStyle = new DashStyle(new double[] { 4.0, 4.0 }, 0) };
        context.DrawLine(dashedPen, start, control);
        context.DrawLine(dashedPen, control, end);
    }

    private void DrawRectangleSelectedHandles(DrawingContext context, RectangleShape rectangle)
    {
        DrawingRectangleF bounds = rectangle.Rect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        Rect rect = ToDip(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.0);
        context.DrawRectangle(null, pen, rect);

        var fill = Brushes.White;
        double size = 10.0;
        double half = size / 2.0;
        Point[] points =
        {
            new(rect.Left, rect.Top),
            new(rect.Left + rect.Width / 2.0, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2.0),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2.0, rect.Bottom),
            new(rect.Left, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2.0),
        };

        foreach (Point p in points)
        {
            context.DrawEllipse(fill, pen, p, half, half);
        }

        // 矩形档的四角圆角手柄已于 2026-09-18 摘除（与聚光灯同期）：圆角改由「选中后元素上方浮出的圆角浮条」
        // 调整，画布上只留这 8 枚缩放手柄。别把 DrawCornerRadiusHandles 接回来——命中侧已不再产出圆角 target，
        // 接回来就是"看得见抓不住"。
    }

    private void DrawCounterSelectedHandles(DrawingContext context, CounterShape counter)
    {
        var stroke = new SolidColorBrush(Color.FromRgb(76, 194, 255));
        var pen = new Pen(stroke, 1.5);
        double s = ScreenToDipScale;

        // 选中框贴合当前外形档：圆档圈成圆、胶囊档圈成长条，画成正圆会和图元对不上
        Rect glyph = ToDip(counter.GlyphRect).Inflate(3.0);
        double corner = Math.Clamp(AnnotationRenderOptions.GetCounterGlyphCornerRadius(counter) * s + 3.0,
            0.0, Math.Min(glyph.Width, glyph.Height) / 2.0);
        context.DrawRectangle(null, pen, glyph, corner, corner);

        // 有说明文字时把文字盒也圈上：整块是一个图元，只圈圆点会让人以为文字是另一层
        if (counter.Note.Length > 0)
            context.DrawRectangle(null, new Pen(stroke, 1.0), ToDip(counter.NoteRect).Inflate(2.0));

        // 引线端点手柄：命中侧是 AnnotationHitTest.HitTestCounterTailHandle（半径 10 屏幕像素），
        // 改这里必须同时改那头的半径，否则就是"看得见抓不住"。浮条上关掉引线的同一判据也管这里。
        if (counter.Tail is System.Drawing.PointF tail && counter.TailShown)
        {
            double radius = 6.0;
            Point p = ToDip(tail);
            context.DrawEllipse(Brushes.White, new Pen(stroke, 1.5), p, radius, radius);

            // 中间弯折点手柄：逐值照抄上面 DrawArrowSelectedHandles 的那一枚（14 的蓝底白边大点＋内部白点
            // ＋两条虚线参考），用户的口径就是"引线中间给个端点，跟箭头标注工具一致"。
            // 命中侧是 AnnotationHitTest.HitTestCounterTailBendHandle，同样 10 屏幕像素，两侧一起改。
            var tailPath = AnnotationRenderOptions.GetCounterTailPoints(counter);
            if (tailPath.Length >= 2
                && AnnotationRenderOptions.GetCounterTailControlPoint(counter) is System.Drawing.PointF control)
            {
                Point c = ToDip(control);
                double midHalf = 14.0 / 2.0;
                var dashedPen = new Pen(stroke, 1.0) { DashStyle = new DashStyle(new double[] { 4.0, 4.0 }, 0) };
                context.DrawLine(dashedPen, ToDip(tailPath[0]), c);
                context.DrawLine(dashedPen, c, p);
                context.DrawEllipse(stroke, new Pen(Brushes.White, 2.0), c, midHalf, midHalf);
                context.DrawEllipse(Brushes.White, null, c, midHalf * 0.35, midHalf * 0.35);
            }
        }
    }

    private static bool NearlySameRect(DrawingRectangleF a, DrawingRectangleF b)
    {
        return Math.Abs(a.Left - b.Left) < 0.5f
            && Math.Abs(a.Top - b.Top) < 0.5f
            && Math.Abs(a.Width - b.Width) < 0.5f
            && Math.Abs(a.Height - b.Height) < 0.5f;
    }

    private Rect ToDip(System.Drawing.RectangleF rect)
    {
        Point topLeft = ToDip(new System.Drawing.PointF(rect.Left, rect.Top));
        Point bottomRight = ToDip(new System.Drawing.PointF(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Point ToDip(System.Drawing.PointF point)
    {
        return new Point(
            (point.X - ScreenOrigin.X) * ScreenToDipScale,
            (point.Y - ScreenOrigin.Y) * ScreenToDipScale);
    }

    private void OnDocumentChanged()
    {
        InvalidateVisual();
        // 撤销/删除/方向键步进/松手提交都只动文档：选中图元的几何可能已经换了地方，浮层要跟上。
        RaiseSelectionGeometryChanged();
    }

}
