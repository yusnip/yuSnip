using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Services;
using Avalonia.Threading;
using System.Text;
using Annotations = ScreenCaptureTool.Core.Capture.Annotations;
using ToolMode = ScreenCaptureTool.Core.Capture.ToolMode;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图窗口：显示一张截图结果，支持拖动、滚轮缩放、复制和关闭。
/// </summary>
public partial class StickerWindow : Window, ScreenCaptureTool.Services.IAnnotationEditHost
{
    private ScreenCaptureTool.Services.StickerAnnotationSession? _annotationSession;
    private StickerAnnotationPaletteWindow? _annotationPalette;
    private bool _isAnnotating;
    private static readonly Cursor HiddenAnnotationCursor = new(StandardCursorType.None);
    private static readonly Cursor RectangleEraserCursor = new(StandardCursorType.Cross);
    private Avalonia.Point? _annotationPointerDip;

    private void UpdateAnnotationPointerFootprint()
    {
        if (!_isAnnotating || _annotationPointerDip is not Avalonia.Point position
            || _activeAnnotationTool is not (ToolMode.Brush or ToolMode.Eraser)
            || StickerAnnotationLayer == null)
        {
            StickerAnnotationLayer?.SetPointerFootprint(null, 0);
            return;
        }
        var toolbar = _annotationPalette?.ToolbarControl;
        double size = _activeAnnotationTool == ToolMode.Brush
            ? (toolbar?.StrokeThickness ?? 1)
            : (toolbar?.EraserDiameter ?? 1);
        StickerAnnotationLayer.SetPointerFootprint(position,
            size * StickerAnnotationLayer.ScreenToDipScale);
    }
    private ScreenCaptureTool.Core.Capture.ToolMode _activeAnnotationTool = ScreenCaptureTool.Core.Capture.ToolMode.None;
    /// <summary>当前选中工具的浮窗键名（与 AnnotationToolbar 的 ToolKey 同一口径），null=未选工具。</summary>
    private string? _activeAnnotationKey;
    /// <summary>进入标注前图片层/文本层的可见性，退出时按此原样恢复。</summary>
    private bool _annotationSavedImageVisible;
    private bool _annotationSavedTextVisible;
    /// <summary>本次标注会话恒定的干净底图（物理像素）：成品合成与渲染层的模糊/马赛克共用同一张，
    /// 每次提交只重画矢量图元、不把成品回灌成下一次底图。</summary>
    private System.Drawing.Bitmap? _annotationCleanSource;
    private System.Drawing.Bitmap? _initialAnnotationSource;
    private IReadOnlyList<Annotations.AnnotationShape>? _initialAnnotationShapes;
    private IReadOnlyList<TextLayerSnapshot>? _initialAnnotationTexts;
    /// <summary><see cref="_annotationCleanSource"/> 是否由本窗口持有（直接引用 _bitmap 时不得释放）。</summary>
    private bool _annotationCleanSourceOwned;
    /// <summary>标注期间顶替 <c>StickerImage.Source</c> 的干净底显示位图（文本/Markdown/颜色贴图与再次进入解烤时用）。</summary>
    private Avalonia.Media.Imaging.Bitmap? _annotationCleanDisplay;
    /// <summary>当前显示的位图是否为已烘焙成品：决定再次进入标注要不要先解烤回干净底图。</summary>
    private bool _annotationDisplayBaked;
    /// <summary><c>_source</c> 是否为某次退出时烤出的成品：成品的内容只在标注会话结束时有效，
    /// 会话内把图元全部撤销后必须让干净底图顶回来，否则已删除的笔迹会重新显形。</summary>
    private bool _annotationSourceBaked;

    /// <summary>当前标注会话中图元或文字是否被新增、修改、移动或删除。</summary>
    private bool _isAnnotationModified;
    /// <summary>当前是否正处于由存档重放标注的初始化阶段（重放不算用户修改）。</summary>
    private bool _isRestoringAnnotations;

    // ── 标注编辑期：全部胶水在共享会话里（Services/AnnotationEditSession*.cs）────────────
    // 点选/拖本体/拖手柄、方向键步进、滚轮八档、参数条回灌、两枚浮条、文字层路由，贴图与录屏共用那一份；
    // 本窗口只留宿主差异：坐标换算、成品烘焙、指针捕获、浮条承载面板。
    /// <summary>共享编辑会话（落笔控制器 + 形状编辑器 + 文本控制器 + 浮条 + 历史接线）。构造于
    /// <c>InitializeComponent</c> 之后，因为建它时要订阅图层与两枚浮条。</summary>
    private ScreenCaptureTool.Services.AnnotationEditSession? _anno;
    /// <summary>懒建：会话需要在浮条控件就位之后才建，而首个用到它的时机必然在那之后。</summary>
    private ScreenCaptureTool.Services.AnnotationEditSession Anno =>
        _anno ??= new ScreenCaptureTool.Services.AnnotationEditSession(this);

    private ScreenCaptureTool.Services.AnnotationInteractionController? _annotationController => _anno?.Controller;
    /// <summary>当前选中图元 Id：图层据此画高亮，命中测试据此放宽"已选中图元的外接框/端点"。</summary>
    private Guid? _annoSelectedId => _anno?.SelectedId;
    /// <summary>本次手势的指针，松手中途被键盘打断时也要靠它释放捕获。</summary>
    private IPointer? _annoPointer => _anno?.ActivePointer;

    public ScreenCaptureTool.Core.Capture.Annotations.AnnotationDocument AnnotationDocument =>
        _annotationSession?.Document ?? _fallbackAnnotationDocument;
    System.Collections.Generic.IReadOnlyList<Guid> IAnnotationHost.AnnotationTextIds
        => TextTool.TextLayers.Select(layer => layer.Id).ToArray();
    private readonly ScreenCaptureTool.Core.Capture.Annotations.AnnotationDocument _fallbackAnnotationDocument = new();

    public ScreenCaptureTool.Core.Capture.ToolMode ActiveAnnotationTool => _activeAnnotationTool;

    public Avalonia.Rect AnnotationContentBounds
    {
        get
        {
            var contentRect = GetFittedContentBoundsDip();
            return new Avalonia.Rect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);
        }
    }

    public bool TryGetAnnotationPoint(Avalonia.Point windowPoint, out Avalonia.Point contentPoint)
    {
        var bounds = AnnotationContentBounds;
        if (windowPoint.X < bounds.Left || windowPoint.X > bounds.Right ||
            windowPoint.Y < bounds.Top || windowPoint.Y > bounds.Bottom)
        {
            contentPoint = default;
            return false;
        }

        contentPoint = ToAnnotationContentPoint(windowPoint, bounds);
        return true;
    }

    /// <summary>
    /// 窗口 DIP → 内容物理像素（原底图 1:1 空间）。<paramref name="bounds"/> 由调用者取好，
    /// 免得一次手势里反复求信封矩形。
    /// <para>本方法**不做边界裁剪**：拖动已有图元时指针必然常甩出贴图外，外推出来的位移依旧正确
    /// （截图侧遮罩铺满整屏，等价于点永远在界内），故移动手势直接用这一版；落新笔仍走
    /// <see cref="TryGetAnnotationPoint"/> 的裁剪口径。</para>
    /// </summary>
    private Avalonia.Point ToAnnotationContentPoint(Avalonia.Point windowPoint, Avalonia.Rect bounds)
    {
        double s = SafeScaling;
        double contentScale = _scale;
        if (contentScale <= 0.0001) contentScale = 1.0;

        double relXDip = windowPoint.X - bounds.Left;
        double relYDip = windowPoint.Y - bounds.Top;
        return new Avalonia.Point(relXDip / contentScale * s, relYDip / contentScale * s);
    }

    public void CommitAnnotationChange()
    {
        if (_annotationSession == null) return;
        if (!_isRestoringAnnotations)
        {
            _isAnnotationModified = true;
            BringStickerToFront();
        }
        _annotationSession.Commit();
        // 与截图同一口径：成品只在退出标注与导出时烘焙，落笔期间显示恒为干净底图 + 图层矢量图元。
        // 早先这里每次提交都把合成图烤进 StickerImage，而图层仍在画同一批图元 → 每一笔都被渲染两遍。
        InvalidateAnnotationPreview();
        SyncAnnotationToolbarHistory();
    }

    /// <summary>
    /// 图层预览的唯一漏斗：两条预览来源（正在画的新笔 / 正在拖的既有图元）在共享会话里合流，
    /// 贴图与录屏走同一条，故这里只转发。
    /// </summary>
    public void InvalidateAnnotationPreview()
    {
        _anno?.RefreshLayerPreview();
        _anno?.Texts.ApplyErasureMasks(AnnotationDocument);
    }
    void IAnnotationHost.SetEraserRectanglePreview(System.Drawing.RectangleF? rect)
    {
        if (StickerAnnotationLayer != null) StickerAnnotationLayer.EraserRectanglePreview = rect;
    }
    double IAnnotationHost.AnnotationLayerRenderMs => StickerAnnotationLayer?.LastRenderMs ?? 0;

    /// <summary>
    /// 命中容差换算倍率。截图侧的容差常量（30/25/15/10/8）是**屏幕物理像素**口径，而贴图上一格内容像素
    /// 在屏幕上只有 <c>_scale</c> 物理像素，故按 1/_scale 放大回内容坐标系才能做到"点起来一样宽松"。
    /// 0.2 是滚轮缩放下限，同时把放大倍数封顶在 5×——否则缩到极小时任何一次点击都会命中一切。
    /// </summary>
    private float AnnotationHitTolerance => 1f / Math.Max(0.2f, (float)_scale);

    /// <summary>
    /// 聚光灯可拖到的范围＝整张干净底图（截图侧同一档位是选区矩形）。缺了它，缩放/步进聚光灯会一路拖出
    /// 画外，留下一圈再也抓不回来的图元。底图尚未就绪时返回 null＝不夹取。
    /// </summary>
    private System.Drawing.RectangleF? AnnotationSpotlightBounds()
        => _annotationCleanSource is { } src
            ? new System.Drawing.RectangleF(0, 0, src.Width, src.Height)
            : null;

    // ── 共享编辑会话的消费点：以下全部是一行转发，判据与算式都在 Services/AnnotationEditSession* ──

    /// <summary>编辑已有图元的唯一派发入口（转发给共享会话；手柄免 Ctrl、同工具免 Ctrl 点选、
    /// 跨工具要 Ctrl、本体顺序＝z 序、文本层插在序号与箭头之间，全部收在
    /// <c>AnnotationShapeEditor.TryBeginPress</c>，截图遮罩与录屏覆盖层调的是同一个入口）。</summary>
    private bool TryBeginAnnotationShapeEdit(Avalonia.Point windowPoint, PointerPressedEventArgs e, bool editExisting)
        => Anno.TryBeginShapeEdit(windowPoint, e, editExisting);

    /// <summary>图元拖动的合帧入口（闸门在会话里，渲染帧由本窗口供给）。</summary>
    private void RequestAnnotationShapeMove(Avalonia.Point windowPoint) => Anno.RequestShapeMove(windowPoint);

    /// <summary>松手收尾（转发）。</summary>
    private void EndAnnotationShapeMove(IPointer? pointer) => Anno.EndShapeEdit(pointer);

    /// <summary>结清方向键步进会话（转发）。</summary>
    private void FlushAnnotationNudgeSession() => Anno.FlushNudgeSession();

    /// <summary>方向键步进选中图元（转发）。</summary>
    private bool StepSelectedShape(int dx, int dy) => Anno.StepSelectedShape(dx, dy);

    /// <summary>选中/取消选中矢量图元（转发；参数条回显在会话里）。</summary>
    private void SelectAnnotationShape(Guid? id) => Anno.SelectShape(id);

    /// <summary>取消图元选中（转发）。</summary>
    private void ClearAnnotationSelection() => Anno.ClearSelection();

    /// <summary>取消全部标注选中：矢量与文字一起清（转发）。</summary>
    private void ClearAllAnnotationSelections() => Anno.ClearAllSelections();

    /// <summary>当前选中的矢量图元（转发）。</summary>
    private Annotations.AnnotationShape? GetSelectedAnnotationShape() => Anno.SelectedShape();

    /// <summary>参数条某一档被改过 → 落到当前选中图元（转发）。</summary>
    private void ApplyToolbarStyleToSelection(AnnotationStylePart parts) => Anno.ApplyToolbarStyleToSelection(parts);

    /// <summary>序号类型档回灌全部已有序号（转发）。</summary>
    private void ApplyCounterSerialKindToAll(Annotations.CounterSerialKind kind) => Anno.ApplyCounterSerialKindToAll(kind);

    /// <summary>改当前选中序号的编号（浮条 − / + 与键盘 + / − 共用；转发）。</summary>
    private void StepCounterNumber(int delta, string origin) => Anno.StepCounterNumber(delta, origin);

    /// <summary>删除当前选中的标注对象（转发）。</summary>
    private void DeleteSelectedAnnotation() => Anno.DeleteSelected();

    /// <summary>撤销一步标注（转发；浮窗撤销按钮与 <c>sticker.undo</c> 共用）。</summary>
    private void UndoAnnotation() => Anno.Undo();

    /// <summary>重做一步标注（转发）。</summary>
    private void RedoAnnotation() => Anno.Redo();

    /// <summary>把圆角浮条贴到选中图元正上方（转发）。</summary>
    private void RefreshSpotlightRadiusPill()
    {
        Anno.RefreshSpotlightRadiusPill();
        UpdateHitRegion();
    }

    /// <summary>收起圆角浮条（转发）。</summary>
    private void HideSpotlightRadiusPill()
    {
        Anno.HideSpotlightRadiusPill();
        UpdateHitRegion();
    }

    /// <summary>把序号浮条贴到选中序号外形盒正上方（转发）。</summary>
    private void RefreshCounterPill()
    {
        Anno.RefreshCounterPill();
        UpdateHitRegion();
    }

    /// <summary>收起序号浮条（转发）。</summary>
    private void HideCounterPill()
    {
        Anno.HideCounterPill();
        UpdateHitRegion();
    }

    /// <summary>⚠ 本窗口的指针事件是<b>隧道相位</b>注册的，没有这两道守卫，点浮条会被窗口当成
    /// "拖动贴图"或"起笔画图元"、在浮条上滚一下会缩放贴图。</summary>
    private bool IsFromSpotlightRadiusPill(object? source) => Anno.IsFromRadiusPill(source);

    private bool IsFromCounterPill(object? source) => Anno.IsFromCounterPill(source);

    /// <summary>退出标注前的三处显式落定（转发）。</summary>
    private void FlushSpotlightRadiusCommit() => Anno.FlushSpotlightRadiusCommit();

    private void FlushCounterCornerCommit() => Anno.FlushCounterCornerCommit();

    private void FlushToolbarStyleCommit() => Anno.FlushToolbarStyleCommit();

    // ──────────────────────────────────────────
    // IAnnotationEditHost：贴图宿主与共享会话之间的差异全收敛在这里
    // （继承自 IAnnotationHost 的那几个成员在本文件上面：AnnotationDocument / ActiveAnnotationTool /
    //  AnnotationContentBounds / TryGetAnnotationPoint / CommitAnnotationChange / InvalidateAnnotationPreview）
    // ──────────────────────────────────────────

    ScreenCaptureTool.Services.AnnotationEditHistory? IAnnotationEditHost.History => _annotationSession?.History;

    AnnotationLayer? IAnnotationEditHost.EditLayer => StickerAnnotationLayer;

    AnnotationToolbar? IAnnotationEditHost.EditToolbar => _annotationPalette?.ToolbarControl;

    SpotlightRadiusPill? IAnnotationEditHost.RadiusPill => SpotlightRadiusPillHost;

    CounterPill? IAnnotationEditHost.CounterPillView => CounterPillHost;

    Canvas IAnnotationEditHost.TextLayerCanvas => StickerTextHost!;
    Rect IAnnotationEditHost.ErasureToTextCanvasRect(System.Drawing.RectangleF mark)
    {
        double scale = StickerAnnotationLayer?.ScreenToDipScale ?? 1;
        return new Rect(mark.X * scale, mark.Y * scale, mark.Width * scale, mark.Height * scale);
    }

    IInputElement IAnnotationEditHost.PointerCaptureTarget => this;

    // 两枚浮条夹在承载面板（RootGrid）内：越界只夹不翻边，算式在会话里。
    Avalonia.Rect IAnnotationEditHost.PillClampBounds => RootGrid?.Bounds ?? new Avalonia.Rect(0, 0, 1, 1);

    float IAnnotationEditHost.HitTolerance => AnnotationHitTolerance;

    System.Drawing.RectangleF? IAnnotationEditHost.SpotlightContentBounds => AnnotationSpotlightBounds();

    Avalonia.Point IAnnotationEditHost.ToContentPoint(Avalonia.Point windowPoint)
        => ToAnnotationContentPoint(windowPoint, AnnotationContentBounds);

    Avalonia.Rect IAnnotationEditHost.GetTextValidRect() => GetStickerTextValidRect();

    // 贴图没有"选区"这回事：整张内容就是文本可活动的范围。
    bool IAnnotationEditHost.HasTextRegion => true;

    Avalonia.Rect IAnnotationEditHost.TextSelectionDip => GetStickerTextValidRect();

    bool IAnnotationEditHost.TryFindPanelResource(string key, out object? value)
        => this.TryFindResource(key, ActualThemeVariant, out value);

    ScreenCaptureTool.Services.AnnotationGestureSettings IAnnotationEditHost.CreateGestureSettings(float arrowScale)
        => CreateGestureSettings(arrowScale);

    void IAnnotationEditHost.ForceToolForShape(Annotations.AnnotationShapeKind kind)
        => ForceAnnotationToolForShape(kind);

    /// <summary>把浮窗切到文本工具（与 <see cref="ForceAnnotationToolForShape"/> 同一口径：
    /// 只激活、不带"再点即取消"的开关语义）。</summary>
    void IAnnotationEditHost.ForceToolForText()
    {
        if (_activeAnnotationKey == "text") return;
        _activeAnnotationKey = "text";
        _activeAnnotationTool = ToolMode.Text;
        _annotationPalette?.ToolbarControl.SetActiveTool("text");
    }

    // 图元能被拖出画板外，不捕获就收不到后续的 Moved/Released；捕获目标一律是窗口本身。
    void IAnnotationEditHost.CapturePointer(IPointer pointer)
    {
        try { pointer.Capture(this); } catch { }
    }

    void IAnnotationEditHost.ReleasePointer(IPointer pointer)
    {
        try { pointer.Capture(null); } catch { }
    }

    // 抓手柄时不改光标：截图侧也只在拖本体那一路给 SizeAll，方向光标留给以后的悬停档。
    void IAnnotationEditHost.SetMoveCursor(bool on)
        => Cursor = on ? new Cursor(StandardCursorType.SizeAll) : null;

    // 与本文件拖边（ScheduleEdgeResizeRaf）、拖角同一套渲染帧机制。
    void IAnnotationEditHost.PostRenderFrame(Action callback)
        => RequestAnimationFrame(_ => callback());

    /// <summary>文本层只压历史、不重烘成品（protection.md「贴图标注显示管线」⑨㈢：
    /// 一次 Commit 要克隆整张干净底图，而字号/透明度滑杆一次拖动就是几十次 tick）。</summary>
    void IAnnotationEditHost.CommitTextHistory()
    {
        if (_annotationSession == null) return;
        if (!_isRestoringAnnotations) _isAnnotationModified = true;
        _annotationSession.CommitHistoryOnly();
    }

    void IAnnotationEditHost.LogDiag(string message) => AnnoDiag(message);

    /// <summary>箭头整体缩放档与滚轮共用会话里那一份，兜底档（浮窗还没建）沿用贴图当前倍率。</summary>
    private ScreenCaptureTool.Services.AnnotationGestureSettings CreateGestureSettings(float arrowScale)
    {
        if (_annotationPalette == null)
        {
            // 兜底档（浮窗还没建）沿用贴图当前倍率；18 个实参的名单收在共享记录那一份里，与录屏同源
            return ScreenCaptureTool.Services.AnnotationGestureSettings.Fallback(arrowScale, (float)_scale);
        }

        // 18 档的取值算式收在共享参数条那一份里（录屏控制条走同一条），贴图只补自己的两个倍率
        return _annotationPalette.ToolbarControl.CreateGestureSettings(
            _activeAnnotationTool, arrowScale, (float)_scale);
    }

    private Avalonia.Rect GetFittedContentBoundsDip()
    {
        if (_wheelVisualOverride && _wheelImgWpx > 0 && _wheelImgHpx > 0)
        {
            double s = SafeScaling;
            var win = GetWindowOriginPx();
            return new Avalonia.Rect(
                (_wheelImgLpx - win.X) / s,
                (_wheelImgTpx - win.Y) / s,
                _wheelImgWpx / s,
                _wheelImgHpx / s);
        }

        // 非覆写态：直接读图片层的**真实布局**（Margin + 显式宽高）；静止贴合态 Margin 恒为 0。
        double cw, ch, mx, my;
        if (StickerImage != null && !double.IsNaN(StickerImage.Width) && StickerImage.Width > 0
            && !double.IsNaN(StickerImage.Height) && StickerImage.Height > 0)
        {
            cw = StickerImage.Width; ch = StickerImage.Height;
            mx = StickerImage.Margin.Left; my = StickerImage.Margin.Top;
        }
        else if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            cw = Bounds.Width - ShadowMargin * 2; ch = Bounds.Height - ShadowMargin * 2;
            mx = 0; my = 0;
        }
        else
        {
            cw = _sourcePixelWidth / SafeScaling; ch = _sourcePixelHeight / SafeScaling;
            mx = 0; my = 0;
        }
        return new Avalonia.Rect(ShadowMargin + mx, ShadowMargin + my,
                                 Math.Max(1.0, cw), Math.Max(1.0, ch));
    }

    /// <summary>退出标注时把成品烤成贴图自身的内容位图（此后图层隐藏，成品就是唯一一份像素）。</summary>
    private void BakeAnnotationCompositeToDisplay()
    {
        if (_annotationSession == null) return;
        // 文本以活可视树为准并入会话；缓存的成品位图可能停留在上一次重烘（CommitHistoryOnly 不重烘），
        // 故烤之前一律先 Rebuild，否则"编辑时看着有字、退出后字没了"。
        _annotationSession.SetTextSnapshots(TextTool.CreateTextSnapshot());
        _annotationSession.Rebuild();
        using var composite = _annotationSession.CopyComposite();
        if (composite == null) return;

        var oldSource = _source;
        _source = ToAvaloniaBitmap(composite);
        StickerImage.Source = _source;
        _annotationDisplayBaked = true;
        _annotationSourceBaked = true;
        // 图层此后不再参与显示：断开模型引用，避免隐藏期间还挂着 Document.Changed 白跑重绘
        if (StickerAnnotationLayer != null)
        {
            StickerAnnotationLayer.Document = null;
        }

        if (TextViewbox != null && TextViewbox.IsVisible)
        {
            TextViewbox.IsVisible = false;
            StickerImage.IsVisible = true;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { oldSource?.Dispose(); } catch { }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>释放标注期缓存：渲染层的 SourceBitmap 一并断开，否则它会悬空指向已释放的快照；
    /// 无标注退出时把显示从干净底图换回贴图自身的内容位图。</summary>
    private void ReleaseAnnotationSourceCache()
    {
        if (StickerAnnotationLayer != null) StickerAnnotationLayer.SourceBitmap = null;

        if (_annotationCleanDisplay != null)
        {
            var oldDisplay = _annotationCleanDisplay;
            if (ReferenceEquals(StickerImage.Source, oldDisplay)) StickerImage.Source = _source;
            _annotationCleanDisplay = null;
            // Avalonia 可能仍在渲染用着旧位图的那一帧
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { oldDisplay.Dispose(); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        if (_annotationCleanSourceOwned && _annotationCleanSource != null)
        {
            try { _annotationCleanSource.Dispose(); } catch { }
        }
        _annotationCleanSource = null;
        _annotationCleanSourceOwned = false;
        _annotationDisplayBaked = false;
        _annotationSourceBaked = false;
    }

    private System.Drawing.Bitmap CaptureCleanSourceBitmap()
    {
        // 会话期内底图必须恒定：文本/Markdown 层进入标注即被换成静态底图，此后重拍会拍到空壳，
        // 表现为"画第二笔时第一笔被抹掉"。缓存存在时一律从缓存克隆。
        if (_annotationCleanSource != null) return (System.Drawing.Bitmap)_annotationCleanSource.Clone();

        if (_initialAnnotationSource != null)
        {
            return (System.Drawing.Bitmap)_initialAnnotationSource.Clone();
        }

        if (_bitmap != null && !StickerColorSwatch.IsVisible)
        {
            return (System.Drawing.Bitmap)_bitmap.Clone();
        }

        var snap = SnapshotTextSticker();
        if (snap != null) return snap;

        int w = Math.Max(1, _sourcePixelWidth);
        int h = Math.Max(1, _sourcePixelHeight);
        var fallback = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(fallback))
        {
            g.Clear(System.Drawing.Color.White);
        }
        return fallback;
    }

    /// <summary>进入标注时确立整会话恒定的干净底图：图片贴图直接引用 <c>_bitmap</c>（不克隆、不持有），
    /// 文本 / Markdown / 颜色贴图趁活体层还在显示时拍一张快照并交由本窗口持有。</summary>
    private System.Drawing.Bitmap EnsureAnnotationCleanSource()
    {
        if (_annotationCleanSource != null) return _annotationCleanSource;

        if (_initialAnnotationSource != null)
        {
            // 只引用、不持有：_initialAnnotationSource 是窗口级常驻干净底图（关窗时才统一释放），
            // 这里若声明所有权，退出标注的 ReleaseAnnotationSourceCache 会把它 Dispose，而
            // _initialAnnotationSource 仍留着悬空引用 → 第二次进入标注取它的宽高清接抛
            // ArgumentException「Parameter is not valid.」。
            _annotationCleanSource = _initialAnnotationSource;
            _annotationCleanSourceOwned = false;
            return _annotationCleanSource;
        }

        if (_bitmap != null && !StickerColorSwatch.IsVisible)
        {
            _annotationCleanSource = _bitmap;
            _annotationCleanSourceOwned = false;
        }
        else
        {
            _annotationCleanSource = CaptureCleanSourceBitmap();
            _annotationCleanSourceOwned = true;
        }
        return _annotationCleanSource;
    }

    private void EnsureAnnotationSession()
    {
        if (_annotationSession != null) return;
        EnsureAnnotationCleanSource();
        _annotationSession = new ScreenCaptureTool.Services.StickerAnnotationSession(
            CaptureCleanSourceBitmap,
            (targetBmp, doc, texts) =>
            {
                ScreenCaptureTool.Services.CaptureSession.RenderAnnotationsToBitmap(
                    targetBmp,
                    new System.Drawing.Rectangle(0, 0, targetBmp.Width, targetBmp.Height),
                    new System.Drawing.Rectangle(0, 0, targetBmp.Width, targetBmp.Height),
                    doc);
                // 文本层与矢量图元共用同一次重烘：画布原点＝位图原点、无选区裁剪，
                // 唯一系数是 DIP→内容物理像素。它必须是上面 ScreenToDipScale（_scale / SafeScaling）的倒数，
                // 否则编辑期看着对、定稿后位置与字号一起错。
                double dipToPixel = SafeScaling / Math.Max(0.0001, _scale);
                ScreenCaptureTool.Services.TextLayerRenderer.RenderToBitmap(
                    targetBmp,
                    texts,
                    ScreenCaptureTool.Services.TextBakeContext.CreateStickerContext(dipToPixel), doc);
                if (texts != null && texts.Count > 0)
                {
                    AnnoDiag($"TextBake dipToBmp={dipToPixel:F4} texts={texts.Count} "
                             + $"位图={targetBmp.Width}x{targetBmp.Height} _scale={_scale:F3} DPI={SafeScaling:F2}");
                }
            });
        // 落笔控制器与形状编辑器都由共享会话持有（见 EnterAnnotationMode 之前的 Anno 构造）
        _annotationSession.Document.Changed += () =>
        {
            if (!_isRestoringAnnotations) _isAnnotationModified = true;
        };

        _isRestoringAnnotations = true;
        try
        {
            if (_initialAnnotationShapes != null)
                _annotationSession.Document.ReplaceAll(_initialAnnotationShapes);
            if (_initialAnnotationTexts != null)
            {
                _annotationSession.SetTextSnapshots(_initialAnnotationTexts);
                TextTool.RestoreTextSnapshot(_initialAnnotationTexts);
                TextTool.ApplyErasureMasks(AnnotationDocument);
            }
        }
        finally
        {
            _isRestoringAnnotations = false;
        }
    }

    public void ToggleAnnotationMode()
    {
        if (_isAnnotating)
        {
            ExitAnnotationMode();
        }
        else
        {
            EnterAnnotationMode();
        }
    }

    public void EnterAnnotationMode()
    {
        if (_isAnnotating || _isLocked || _isMousePenetration) return;
        EndStickerDrag("进入标注");
        if (_isCanvasMode) ExitCanvasMode();
        DisposeCanvasPictures();
        // 缩略图模式禁入标注：视口靠 TranslateTransform 平移 + 150×150 裁切，而标注层的坐标
        // 口径（GetFittedContentBoundsDip）只认"窗口矩形＝内容＋阴影边距"，进去笔迹必错位。
        if (_isThumbnailMode)
        {
            StickerToastWindow.ShowAtTop("缩略图模式下不可标注，请先退出缩略图 (Shift+R)");
            return;
        }
        _isAnnotating = true;
        _annotationSavedImageVisible = StickerImage.IsVisible;
        _annotationSavedTextVisible = TextViewbox?.IsVisible == true;
        EnsureAnnotationSession();

        var clean = EnsureAnnotationCleanSource();
        // 显示位图必须是干净底图：文本 / Markdown / 颜色贴图平时显示活体层（无位图），
        // 而上次退出标注时成品已被烤进 _source —— 两种情况下都要先顶回干净底图，
        // 否则图层里的矢量图元与图里烤好的同一批图元会同时显形（观感＝一笔变两笔）。
        // 由截图转入的贴图初始 _bitmap 是“带标注成品”，即使尚未进入过贴图标注模式，
        // 也不能把它当作干净底图，否则原标注像素会与可编辑图层重复显示。
        bool displayIsClean = _initialAnnotationSource == null
            && !_annotationDisplayBaked && _bitmap != null && !StickerColorSwatch.IsVisible;
        if (!displayIsClean && _annotationCleanDisplay == null)
            _annotationCleanDisplay = ToAvaloniaBitmap(clean);
        if (_annotationCleanDisplay != null)
        {
            StickerImage.Source = _annotationCleanDisplay;
            _annotationDisplayBaked = false;
        }
        StickerImage.IsVisible = true;
        if (TextViewbox != null) TextViewbox.IsVisible = false;
        // 进入标注＝文字层让位给图元层，划词浮条随之收起（UpdateSelectionToolbarPosition 里也有同一条判据）。
        // 译文气泡同收：它如今可以不经悬浮就常驻开着，留着就是一张半透明浮层压在标图画布上挡笔画。
        // 与 OnWindowDeactivated 同一条纪律——收气泡、**不**清翻译标记（回到正常态自然重画到位）。
        if (_bubbleShown) HideTranslateBubble();
        HideSelectionToolbar();

        if (StickerAnnotationLayer != null)
        {
            StickerAnnotationLayer.IsVisible = true;
            StickerAnnotationLayer.IsHitTestVisible = true;
            StickerAnnotationLayer.Document = _annotationSession?.Document;
            StickerAnnotationLayer.SourceBitmap = clean;
            // 图层永远与图片同 Margin：信封态下图片被 Margin 钉在信封内某处，静态态下为 0。
            // 进入瞬间以图片为准覆盖一次，任何未同步的遗留偏移都在此刻被抹掉（不再依赖各条
            // 几何通路都记得同步图层）。
            StickerAnnotationLayer.Margin = StickerImage.Margin;
            // 手柄绘制不再按宿主分档：贴图侧的"抓"与"拖"已接上共享编辑器，图层默认那份 8 向缩放 /
            // 箭头端点手柄两侧共用同一份代码（圆角手柄已于 2026-09-18 整体退役，改走元素上方的圆角浮条）。
            // 内容坐标系即这张干净底图的 0 起点物理像素；截图侧同一口径（其值为整屏物理矩形）。
            // 缺了 SourceScreenRect / SpotlightBoundsScreenRect，聚光灯的压暗蒙版会因包围盒宽高为 0
            // 而在 DrawSpotlightMask 开头直接 return —— 贴图上的聚光灯从来只剩一圈边框。
            StickerAnnotationLayer.SourceScreenRect = new System.Drawing.Rectangle(0, 0, clean.Width, clean.Height);
            StickerAnnotationLayer.SpotlightBoundsScreenRect =
                new System.Drawing.RectangleF(0, 0, clean.Width, clean.Height);
            StickerAnnotationLayer.ScreenOrigin = new Avalonia.PixelPoint(0, 0);
            // 内容物理像素 → 图层本地 DIP 必须与 ApplyScale 的贴合公式（DIP = 源px × _scale / DPI）互逆。
            // 旧写法 1/(_scale*DPI) 只在 _scale=1 时恰好相等，贴图一缩放笔迹就按 _scale² 倍错位浮起。
            StickerAnnotationLayer.ScreenToDipScale = _scale / SafeScaling;
        }
        // 文本宿主层与标注层成对钉几何（同一 DIP 口径），只在标注期可见
        SyncTextHostGeometry();
        if (StickerTextHost != null)
        {
            StickerTextHost.IsVisible = true;
            StickerTextHost.IsHitTestVisible = true;
        }

        if (_annotationPalette == null)
        {
            _annotationPalette = new StickerAnnotationPaletteWindow();
            _annotationPalette.InteractionRequested += BringStickerToFront;
            _annotationPalette.ToolbarControl.ToolRequested += OnAnnotationToolRequested;
            _annotationPalette.ToolbarControl.ParametersChanged += OnAnnotationParametersChanged;
            // 参数条上"能回改已画好图元"的九档：改一档报一档，落到当前选中的那一个图元上
            //（与截图侧 CaptureSession 那九条订阅同一张分派表）。上面那条 ParametersChanged 只管重绘"下一笔"的预览。
            _annotationPalette.ToolbarControl.StylePartChanged += ApplyToolbarStyleToSelection;
            // 序号类型档（123 / ABC / ⅠⅡⅢ / 一二三）：与其余样式档不同，它要回改图上已有序号（截图侧同一口径）
            _annotationPalette.ToolbarControl.CounterSerialKindChanged += ApplyCounterSerialKindToAll;
            // 参数条出现/消失会改变浮窗宽高，浮窗要重新以贴图居中
            _annotationPalette.ToolbarLayoutChanged += UpdateAnnotationPalettePosition;
            // 先注入再 ApplyDefaults：文本那 12 个 Text* 键由控制器读取，注入晚了浮窗起点就会与截图不一致
            _annotationPalette.ToolbarControl.TextController = TextTool;
            try
            {
                _annotationPalette.ToolbarControl.ApplyDefaults(AppSettings.Load());
            }
            catch (Exception ex)
            {
                AppLogger.Warn("贴图标注浮窗恢复标注参数失败: " + ex.Message);
            }
            // 参数面板控件抓取与事件绑定，一个浮窗一生只挂一次（重复绑定会让弹层开一次关一次）
            InitAnnotationTextTool();
            // 浮窗理应是"不激活窗"（WS_EX_NOACTIVATE）。该位一旦被 Avalonia 重算扩展样式时抹掉，
            // 点它就会真的把前台抢走——贴图从此收不到 Space/Esc。抢了就立刻要回来：
            // 延后一拍执行，避开与 Avalonia 自身激活切换打架。
            _annotationPalette.Deactivated += (_, _) =>
                Dispatcher.UIThread.Post(ReturnForegroundToSticker, DispatcherPriority.Background);
        }

        _annotationPalette.ToolbarControl.ApplyCustomLayout(AppSettings.Current);
        _annotationPalette.Show();
        // Avalonia 在 Show 过程中会按自身属性覆写 GWL_EXSTYLE，跑完布局再补挂一次"不激活"
        Dispatcher.UIThread.Post(_annotationPalette.EnsureNoActivateStyle, DispatcherPriority.Loaded);
        _annotationPalette.ToolbarControl.SetActiveTool(_activeAnnotationKey);
        SyncAnnotationToolbarHistory();
        UpdateAnnotationPalettePosition();
        UpdateHitRegion();
        // Show() 当帧浮窗还没跑布局，尺寸用的是兜底值 → 布局完成后再校准一次定位
        Dispatcher.UIThread.Post(UpdateAnnotationPalettePosition, DispatcherPriority.Loaded);
        AnnoDiag($"进入标注 浮窗IsVisible={_annotationPalette.IsVisible} 贴图IsActive={IsActive} "
                 + $"_scale={_scale:F3} DPI={SafeScaling:F2} 源px={_sourcePixelWidth}x{_sourcePixelHeight}"
                 + $" 干净底图={clean.Width}x{clean.Height}"
                 + $" 图层({(StickerAnnotationLayer?.Width ?? 0):F1}x{(StickerAnnotationLayer?.Height ?? 0):F1})"
                 + $"+margin({(StickerAnnotationLayer?.Margin.Left ?? 0):F1},{(StickerAnnotationLayer?.Margin.Top ?? 0):F1})"
                 + $" 图片margin({StickerImage.Margin.Left:F1},{StickerImage.Margin.Top:F1})"
                 + $" 覆写态={_wheelVisualOverride} 内容区={GetFittedContentBoundsDip()}"
                 + $" 图元数={_annotationSession?.Document.Count ?? 0}");
    }

    public void ExitAnnotationMode([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (!_isAnnotating) return;
        _isAnnotating = false;
        _annotationPointerDip = null;
        UpdateAnnotationPointerFootprint();
        if (ReferenceEquals(Cursor, HiddenAnnotationCursor)) Cursor = null;
        // caller 即退出来源（Space/Esc 键、失焦复核、清除焦点、关窗），用于定位误触发路径
        AnnoDiag("退出标注 来源=" + caller);
        // 浮条与参数条攒着的连续量改动先落定：下面是"只烘成品"的路径，攒着的改动若只停在文档里，
        // 成品会漏掉最后一次改值（浮条自身只做隐藏、不落定，理由见 HideSpotlightRadiusPill）。
        FlushSpotlightRadiusCommit();
        FlushCounterCornerCommit();
        FlushToolbarStyleCommit();
        // 中断路径兜底：按住左键拖动图元的同时按 Esc/Space 退出，状态机与指针捕获必须一并收干净
        if (Anno.IsEditingShape) EndAnnotationShapeMove(_annoPointer);
        // ⚠ 收起必须排在上面三句之后：落定与松手提交都会经文档 Changed 把浮条贴回来（EndAnnotationShapeMove
        // 更直接——它就是"松手把浮条贴回新位置"那一句），先藏后贴＝退出标注后留下一条挂在画面上的浮条。
        HideSpotlightRadiusPill();
        HideCounterPill();
        _annotationController?.Cancel();
        // 编辑中直接退出：先把在输入的字收进活树状态，随后 CreateTextSnapshot 才拿得到它
        TextTool.EndActiveTextEditing(caller);

        if (StickerAnnotationLayer != null)
        {
            StickerAnnotationLayer.IsVisible = false;
            StickerAnnotationLayer.IsHitTestVisible = false;
            StickerAnnotationLayer.ClearPreviewShape();
            // 文字选中态不随图层隐藏而消失，只清高亮会留下一条"看不见但仍被选中"的文字层；
            // 现在 Del 消费的是 SelectedText，下次进标注就会把它误删——退出时两侧一起清。
            ClearAllAnnotationSelections();
        }

        if (_annotationPalette != null)
        {
            // 三枚菜单是独立顶层窗，浮窗 Hide 不会连带关掉
            _annotationPalette.ToolbarControl.CloseMenus();
            try { _annotationPalette.Hide(); } catch { }
        }

        // 退出标注是全窗口唯一的烘焙时机：落笔期间显示恒为"干净底图 + 图层矢量图元"，成品只在此刻落成像素。
        // ⚠ 判据必须同时看矢量图元与文字：只看 Document.Count 会让"只写字没画笔"的贴图走下面那条
        //    "一笔未落"分支，退回来时把已写好的字整棵宿主层藏掉 → 观感是"打完字一退出手感没了"。
        if (_annotationSession != null)
        {
            if (HasAnyAnnotationContent())
            {
                BakeAnnotationCompositeToDisplay();
            }
            else
            {
                // 一笔未落 → 按进入前的图层可见性原样恢复。旧判据用 _textContent != null 认"这是文本贴图"，
                // 但图片贴图做过 Alt 选词后 _textContent 同样非空，于是退出时把图藏掉、显出空文本层，
                // 观感就是"贴图消失只剩一圈阴影的透明框"。
                StickerImage.IsVisible = _annotationSavedImageVisible;
                if (TextViewbox != null) TextViewbox.IsVisible = _annotationSavedTextVisible;
                if (ImageBackdrop != null) ImageBackdrop.IsVisible = !_isCutout && _annotationSavedImageVisible;
                // _source 可能是上一轮退出烤出的成品（含本轮已撤光的笔迹）：让干净底图接管显示，
                // 否则退出后这些笔迹会重新显形，且与导出路径（此时回落 _bitmap）不一致。
                if (_annotationSourceBaked && _bitmap != null && _annotationCleanDisplay != null)
                {
                    var staleBaked = _source;
                    _source = _annotationCleanDisplay;
                    _annotationCleanDisplay = null;
                    StickerImage.Source = _source;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try { staleBaked?.Dispose(); } catch { }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
                ReleaseAnnotationSourceCache();
            }
        }

        // 文本宿主层收起必须<b>排在烘焙之后</b>：CreateTextSnapshot 现读活控件的 Root.Bounds 与
        // TextBox.LineHeight，先收起再取快照就有量成 0 的风险。只收起、不清空——标注会话跨退出
        // 保留，重进标注时同一批文字要还在原处（干净底图接管显示，不会与烤进像素的那份叠加）。
        if (StickerTextHost != null)
        {
            StickerTextHost.IsVisible = false;
            StickerTextHost.IsHitTestVisible = false;
        }

        // 这里**不做**信封收口（2026-09-06 实测回退）：原先在尾部补一次 CommitWheelGesture，用户仍报"标注完成后闪"。
        // 计时日志显示那次整窗只隐匿了 8.8ms（不足一帧，不可感知），被看到的其实是揭开过早露出的旧帧鬼影。
        // 而"滚完轮什么都不做"时信封窗口本就是设计成可长期常驻（图片+阴影之外的区域走 HTTRANSPARENT 鼠标穿透），
        // 退出标注没有任何理由比它更急着缩窗。收口仍交给既有的非手势时机：窗口失活 / 其他快捷键 / 切缩略图 /
        // 适配屏幕 / 封顶态拖动起手——与不滚动时的行为完全一致。
        UpdateHitRegion();
        SaveArchiveSnapshot();
    }

    /// <summary>
    /// 会改变贴图几何或像素的操作（滚轮缩放 / 拖边缩放 / 缩略图切换 / 适配屏幕 / 原地换图 / 锁定）
    /// 动手前统一经此收口：标注模式只负责"画"，其余操作一律按"画完了"理解，就近自动定稿退出
    /// （成品在退出瞬间烘焙进贴图、图层收起），用户不必再按一次空格确认编辑状态。
    /// 纯移动窗口**不**在此列——标注层与内容同属一个窗口坐标系，随窗口整体平移不会错位，
    /// 而中途退出会把用户正准备继续画的那一轮笔迹的编辑态打断，反而违背直觉。
    /// </summary>
    private void FinalizeAnnotationForGeometryChange(string trigger)
    {
        if (!_isAnnotating) return;
        ExitAnnotationMode("自动定稿(" + trigger + ")");
    }

    /// <summary>浮窗抢走前台时把激活要回本贴图，保证 Space/Esc 仍能退出标注。</summary>
    private void ReturnForegroundToSticker()
    {
        if (!_isAnnotating || _isClosed || IsActive) return;
        AnnoDiag("浮窗抢走前台 → 贴图取回激活");
        try { this.Activate(); } catch { }
    }

    private void UpdateAnnotationPalettePosition()
    {
        if (!_isAnnotating || _annotationPalette == null || !_annotationPalette.IsVisible) return;
        double s = SafeScaling;
        var origin = GetWindowOriginPx();
        var content = GetFittedContentBoundsDip();
        var rect = new Avalonia.PixelRect(
            (int)Math.Round(origin.X + content.X * s),
            (int)Math.Round(origin.Y + content.Y * s),
            (int)Math.Round(content.Width * s),
            (int)Math.Round(content.Height * s));
        // 工具栏跟随屏上可见的贴图边缘。完整图片可能被视口裁掉或平移，不能把未显示的源图右／下缘当锚点。
        var viewport = GetViewportRectPx();
        var actualContent = GetContentRectPx(viewport);
        var visible = actualContent.Intersect(viewport);
        if (visible.Width > 0 && visible.Height > 0)
            rect = new Avalonia.PixelRect((int)Math.Round(visible.Left), (int)Math.Round(visible.Top),
                Math.Max(1, (int)Math.Round(visible.Right) - (int)Math.Round(visible.Left)),
                Math.Max(1, (int)Math.Round(visible.Bottom) - (int)Math.Round(visible.Top)));
        _annotationPalette.PositionNearSticker(rect, s);
    }

    private void OnAnnotationToolRequested(string toolKey)
    {
        // 撤销/橡皮擦/清除/换工具都可能让选中的那个图元消失或改几何：高亮与"放宽命中"一律作废
        ClearAnnotationSelection();
        // 点浮窗任何一处＝"这一笔写完了"：结束编辑但保留已写文字（截图侧 IsTextToolActive=false 同一语义）。
        // 放在最前面而不是各分支里，是因为撤销/橡皮擦都得先读到已落定的那几个字。
        TextTool.EndActiveTextEditing("ToolbarClick");
        TextTool.CloseTextPopups();

        // 键名口径由 AnnotationToolbar.axaml 的 ToolKey 定义（小写），两侧必须逐字一致
        switch (toolKey)
        {
            case "undo":
                // 与 Ctrl+Z 共用同一份收口（重绘 + 历史态同步 + 选中态作废），不要在此内联第二套
                UndoAnnotation();
                return;
            case "eraser":
                break;
            case "clearall":
                // 全清仍由原来的整批历史快照通路处理，与涂抹橡皮擦分开。
                Anno.ClearAllAnnotations();
                return;
            case "clearCurrent":
                ClearCurrentToolAnnotations();
                return;
        }

        // 与截图 SetAnnotationTool 同一交互：再点已选中的工具即取消选择、参数条收起
        if (_activeAnnotationKey == toolKey)
        {
            _activeAnnotationKey = null;
            _activeAnnotationTool = ScreenCaptureTool.Core.Capture.ToolMode.None;
            _annotationController?.Cancel();
            _annotationPalette?.ToolbarControl.SetActiveTool(null);
            _annotationPointerDip = null;
            UpdateAnnotationPointerFootprint();
            Cursor = null;
            return;
        }

        _activeAnnotationKey = toolKey;
        // 键名→工具的映射只留一份（在 AnnotationToolButton，键名词汇的权威所在），录屏覆盖层转调同一条
        _activeAnnotationTool = AnnotationToolButton.ModeOfToolKey(toolKey);
        _annotationPalette?.ToolbarControl.SetActiveTool(_activeAnnotationKey);
        _annotationPointerDip = null;
        UpdateAnnotationPointerFootprint();
    }

    /// <summary>
    /// Ctrl 点中图元后把浮窗切到该类型的工具上（截图侧 <c>ForceAnnotationTool</c> 同一口径：
    /// 点箭头就变成箭头工具）。刻意不复用 <see cref="OnAnnotationToolRequested"/>——那条链带
    /// "再点已选中的工具即取消"的开关语义，而 Ctrl 点选永远该是"选中并激活"，不该把当前工具关掉。
    /// </summary>
    private void ForceAnnotationToolForShape(Annotations.AnnotationShapeKind kind)
    {
        // kind→键名这张表与录屏共用一份（键名词汇的权威在 AnnotationToolButton，与图标 switch 同源）
        string? key = AnnotationToolButton.ToolKeyOfShapeKind(kind);
        if (key == null || _activeAnnotationKey == key) return;
        _activeAnnotationKey = key;
        _activeAnnotationTool = AnnotationToolButton.ModeOfToolKey(key);
        _annotationPalette?.ToolbarControl.SetActiveTool(key);
    }

    /// <summary>只清当前工具的图元（文本档清的是活文字层；判据与截图逐条一致，转发给共享会话）。</summary>
    private void ClearCurrentToolAnnotations() => Anno.ClearCurrentToolAnnotations();

    /// <summary>浮窗在另一个窗口里，撤销可用态只能由宿主主动回写（浮窗无重做按钮）。</summary>
    private void SyncAnnotationToolbarHistory() => Anno.SyncHistoryState();

    private void OnAnnotationParametersChanged()
    {
        InvalidateAnnotationPreview();
        UpdateAnnotationPointerFootprint();
    }

    private System.Drawing.Bitmap? _bitmap;
    private Avalonia.Media.Imaging.Bitmap? _source;
    private double _scale = 1.0;

    /// <summary>截图原始区域的物理像素宽度；贴图初次显示时按此尺寸还原。</summary>
    private int _sourcePixelWidth;
    /// <summary>截图原始区域的物理像素高度；贴图初次显示时按此尺寸还原。</summary>
    private int _sourcePixelHeight;
    /// <summary>是否已经完成一次按屏幕原始尺寸定位（Opened 之后才可靠读取 DPI）。</summary>
    private bool _initialSizeApplied;
    /// <summary>
    /// 调用方在 <c>Show()</c> **之前**交进来的 DPI 缩放（截图侧就是 <c>CapturedFrame.DpiScale</c>，截图那一刻已实测）。
    /// 窗口句柄创建前 <c>DesktopScaling</c> 读到 0，构造函数里那次 <c>ApplyScale()</c> 只能按 1.0 占位——
    /// 150% 屏上一张 1671×1096 的选区会先按 2554×1692 请求窗口，<c>Opened</c> 里再夹回，
    /// 这一来一回就是 issue #12 看到的"贴图冒出来又弹一下"。0 表示调用方没给，行为与旧版一致。
    /// </summary>
    private readonly double _preShowScaling;

/// <summary>Markdown 贴图标记：OnOpened 后需要捕获实际渲染高度。</summary>
private bool _needsMarkdownHeightCapture;

    /// <summary>阴影显示开关（Y 键切换）。true=显示阴影，false=隐藏。默认读取配置。</summary>
    private static IntPtr _mouseHook = IntPtr.Zero;
    private static ScreenCaptureTool.Platform.NativeMethods.LowLevelMouseProc? _mouseProc;

    /// <summary>
    /// 穿透态贴图登记簿（2026-09-17）。全局鼠标钩子**只为穿透态贴图的右键菜单**服务
    /// （回调只认 `_isMousePenetration` 的窗），所以钩子的存活窗口恒等于"本表非空"。
    /// ⚠ 旧实现在每个贴图的构造函数里就装钩子、只有"贴图数=0"才卸 ⇒ 桌面上留着一张普通贴图，
    /// 本软件就全天候挂在系统输入链上（低级钩子每慢 1ms，全系统鼠标就多等 1ms；别人装的钩子
    /// 卡住时我们还要陪着一起被摘）。**禁止改回按"贴图总数"判定存活**。
    /// 只在 UI 线程读写；钩子回调只读 <see cref="_penetrationHwnds"/> 快照，不碰本表。
    /// </summary>
    private static readonly List<StickerWindow> PenetrationStickers = new();

    /// <summary>
    /// 钩子回调唯一可见的输入：穿透态贴图的窗口句柄快照（发布时整体替换，读侧无锁无分配）。
    /// 回调路径禁止访问任何 Avalonia 对象——旧实现在回调里 `OfType&lt;StickerWindow&gt;()`
    /// 并读 `Position/Bounds`，既分配、又把 UI 线程的卡顿直接摊给全系统鼠标。
    /// </summary>
    private static volatile IntPtr[] _penetrationHwnds = Array.Empty<IntPtr>();

    /// <summary>当前处于穿透态的贴图数（供诊断哨兵读取）。</summary>
    public static int PenetrationCount => _penetrationHwnds.Length;

    /// <summary>全局鼠标钩子当前是否挂着（供诊断哨兵读取）。</summary>
    public static bool MouseHookInstalled => _mouseHook != IntPtr.Zero;

    /// <summary>
    /// 位图<b>自身带一圈烘焙好的阴影带</b>（剪贴板 / 历史的外来图，或勾了阴影的截图直接贴图——
    /// 2026-09-07 用户拍板后截图贴图也烘阴影入图）。只决定两件事：阴影带下垫透明棋盘格承托
    /// 那圈半透明阴影（<see cref="UpdateAlphaBackingLayer"/>）、贴图存档登记 BakedShadow。
    /// <b>不</b>关窗体阴影——窗体阴影是 UI 状态指示（焦点蓝 / 锁定橙 / 穿透绿、Y 键开关），
    /// 烘焙阴影是图片素材，两者不是同一个东西，必须并存（2026-09-07 用户拍板）。
    /// </summary>
    private bool _hasBakedShadow;
    /// <summary>位图有透明外缘（圆角 / 描边）→ 隐藏透明棋盘格，让桌面直接透出四角。
    /// ⚠ 不含阴影带：自带阴影带的图反而<b>要</b>露棋盘格（阴影要有对比底才看得见），
    /// 该分支在 <see cref="UpdateAlphaBackingLayer"/> 里先于本判据返回。</summary>
    private bool _hideAlphaCheckerboard;
    /// <summary>当前贴图是否为智能抠图生成的主体图。若为 true，背景由透明棋盘格承托且绝不注入采样底色。</summary>
    private bool _isCutout;
    /// <summary><see cref="_transparentProbeResult"/> 是针对哪一张位图求的值（按实例引用作键，换图即重算）。</summary>
    private System.Drawing.Bitmap? _transparentProbeSource;
    private bool _transparentProbeResult;
    /// <summary>
    /// 成品烘焙效果留下的<b>卡片几何</b>（位图物理像素，取自 <c>SelectionEffects.CardGeometryOf</c>）：
    /// 内容轮廓圆角 + 描边宽。窗体阴影的光晕基准线由它换算下发给 <c>ShadowBorder</c>。
    /// ⚠ 不能只记"外沿半径"一个数：卡片外沿是内容轮廓的真等距线，同族缩放在 45° 方向差
    /// 0.2312×描边宽，圆角与描边同时开时角部就会与卡片断开一条缝（2026-09-06 修）。
    /// 默认值（全 0）＝基准线就是位图显示矩形，与改版前一致。
    /// </summary>
    private SelectionEffects.CardGeometry _bakedCardGeometry;
    /// <summary>带烘焙阴影贴图的内容内缩物理边距（默认 0，带阴影为 37+描边等）。</summary>
    private int _bakedShadowPadPx;
    private int _originalBakedShadowPadPx;
    private bool _shadowVisible = AppSettings.Load().StickerDefaultShadow;

    /// <summary>
    /// **这张图在截图时「阴影」有没有被勾选**（2026-10-01，issue #50）。`null`＝无从得知
    /// （剪贴板 F3、截图历史、别的应用复制来的图、脚本贴图、文本/Markdown/颜色贴图）。
    ///
    /// <para>🔴 **与 <see cref="_hasBakedShadow"/> 是两件事，不许合并**：后者是"位图像素里确实有一圈
    /// 烘焙阴影带"的**物理事实**（决定垫不垫棋盘格），本字段是"用户认为这张图需要边界"的**意图**。
    /// 两者的生命周期不同——原地换图（翻译、抠图）会让物理事实失效并被归零，而意图必须留住：
    /// 用户勾阴影截下来的那张图，翻译成另一张白底图之后**更需要**边界。这正是 issue #50 的成因。</para>
    ///
    /// <para>只被 <see cref="ShouldPaintWindowShadow"/> 在 Auto 档读取；Off/On 两档完全不看它。</para>
    /// </summary>
    private bool? _captureShadowIntent;

    /// <summary>登记"这张图截图时阴影的勾选状态"。由 <c>CaptureSession.CreateSticker</c> 无条件调用
    /// （含"没勾选"这一取值），以及存档回灌时按存下来的值复原。</summary>
    internal void SetCaptureShadowIntent(bool checked_)
    {
        _captureShadowIntent = checked_;
        // 开窗前调用时 ShadowBorder 可能还没就位；ApplyShadow 自带早退，Opened 还会再刷一次
        ApplyShadow();
    }
    /// <summary>当前是否处于焦点状态（焦点时阴影为蓝色，否则灰黑）。</summary>
    private bool _isFocused;
    /// <summary>正在拖动窗口期间（忽略 Deactivated 误触发的失焦）。</summary>
    private bool _isDragging;
    private IPointer? _dragPointer;
    /// <summary>正在从边缘调整窗口大小期间（BeginResizeDrag 已调用）。</summary>
    private bool _isResizing;

    /// <summary>Ctrl+左键拖拽贴图导出临时文件状态</summary>
    private bool _isCtrlDragPending;
    private bool _isPerformingDragDrop;
    private Avalonia.Point _ctrlDragStartPos;
    private const double CtrlDragThreshold = 4.0;

    private string? _textContent;
    private TextLayoutInfo? _layoutInfo;
    private bool _isTextSelectable;

    // --- Thumbnail and state fields ---
    private bool _isThumbnailMode;
    private double _viewportX;
    private double _viewportY;
    private bool _isLocked;
    private bool _isMousePenetration;

    // 内置快捷键绑定（设置页可重新录制；窗口创建时一次性取出）
    private readonly System.Collections.Generic.Dictionary<string, Services.BuiltinShortcutsStore.BuiltinShortcut> _shortcuts =
        Services.BuiltinShortcutsStore.LoadGroupMap(Services.BuiltinShortcutsStore.GroupSticker);
    private int _shortcutsRevision = Services.BuiltinShortcutsStore.Revision;

    private KeyModifiers StickerGestureModifier(string id, KeyModifiers fallback)
    {
        if (_shortcutsRevision != Services.BuiltinShortcutsStore.Revision) RefreshCloudShortcuts();
        return _shortcuts.TryGetValue(id, out var binding) ? binding.Modifiers : fallback;
    }

    /// <summary>云配置应用后更新存活贴图的绑定，不重建窗口或改动贴图内容。</summary>
    internal void RefreshCloudShortcuts()
    {
        var current = Services.BuiltinShortcutsStore.LoadGroupMap(Services.BuiltinShortcutsStore.GroupSticker);
        _shortcuts.Clear();
        foreach (var binding in current) _shortcuts[binding.Key] = binding.Value;
        _shortcutsRevision = Services.BuiltinShortcutsStore.Revision;
        UpdateContextMenuState();
    }

    /// <summary>按键事件是否命中指定内置快捷键。</summary>
    private bool ShortcutMatches(string id, KeyEventArgs e)
    {
        var summon = StickerGestureModifier("sticker.pickhold", KeyModifiers.Shift);
        if (!_shortcuts.TryGetValue(id, out var sc)) return false;
        // 只在本窗处理按住的修饰键，不改变截图侧 Shift+字母工具键的精确匹配。
        // 保留原 Shift 让行；取色器可见期间，新的唤出修饰键也只在绑定未显式使用时让行。
        var ignored = KeyModifiers.Shift | (ColorPickerVisible ? summon : KeyModifiers.None);
        var mods = e.KeyModifiers & ~(ignored & ~sc.Modifiers);
        return Services.BuiltinShortcutsStore.Matches(sc, e.Key, mods);
    }

    private readonly TranslateTransform _imageTransform = new();
    private readonly TranslateTransform _textTransform = new();
    private readonly TranslateTransform _canvasTransform = new();

    // --- Rectangle selection state ---
    /// <summary>PaddleOCR 字级识别结果（每字一个 OcrWordRegion），用于矩形选择交互。</summary>
    private List<OcrWordRegion>? _wordRegions;
    /// <summary>当前选中的行索引集合。</summary>
    private readonly HashSet<int> _selectedWordIndices = new();
    /// <summary>按下的起始行索引（-1 表示无）。</summary>
    private int _dragStartWordIndex = -1;
    /// <summary>是否正在拖拽选择中。</summary>
    private bool _isWordSelecting;
    /// <summary>Canvas 上每行对应的 Rectangle 控件（索引并行）。</summary>
    private readonly List<Avalonia.Controls.Shapes.Rectangle> _wordRectangles = new();
    /// <summary>合并连续选中字的叠加矩形。</summary>
    private readonly List<Avalonia.Controls.Shapes.Rectangle> _selectionOverlays = new();
    // ── 行带认领（区域锚定）：按下／悬停光标／Alt 锚定共用同一判据，见 RebuildWordLineBands ──
    /// <summary>按 LineIndex 聚出的行带（几何单位＝源图像素）。</summary>
    private readonly List<WordLineBand> _wordLineBands = new();
    /// <summary>行带由哪一份 OCR 缓存建成的；缓存换过（旋转／裁切／重识别／预热补上）就在下一次认领前重建。</summary>
    private List<OcrWordRegion>? _wordBandsFrom;
    /// <summary>字下标 → 阅读顺序号（-1＝失联）。跨行拖选按这个序号取区间，不再用 OCR 的原始下标。</summary>
    private int[] _wordReadingSeq = Array.Empty<int>();
    /// <summary>阅读顺序号 → 字下标（与 <see cref="_wordReadingSeq"/> 成对）。</summary>
    private int[] _seqToWordIndex = Array.Empty<int>();
    /// <summary>
    /// 竖向够到最近那一行的余量，单位＝<b>屏幕 DIP</b>（不是源图像素）。
    /// 🔴 旧写法把容差写在源图像素上（<c>max(6, charH*0.5)</c> 一类），而指针永远在屏幕上：
    /// 贴图缩到 <c>_scale=0.2</c> 时 6 个源像素≈1.2 个物理像素，工单 #45 那句"需要很精确才行"就是这么来的。
    /// 改成 DIP 后由 <see cref="WordClaimCapPx"/> 每次按当时缩放现算，屏幕上手感恒定。
    /// ⚠ <b>只管竖向，横向零容差</b>（2026-10-01「退」）：横向外扩实测会把行末空白、两栏之间的空隙
    /// 都判成"够得着隔壁那个字"＝用户报的"选多字"。取值当日两轮：24（行缝也能抓）→ 10
    /// （用户回字"收"：宁可难点中，不要乱抓隔壁）。
    /// </summary>
    private const double WordClaimRadiusDip = 10;
    /// <summary>Alt 快速选词：临时启用 OCR 选词，不进入持久"文本可选择"模式。</summary>
    private bool _altQuickSelect;
    /// <summary>Alt 按下时的 Canvas 坐标（OCR 异步完成后作为选词锚点）。</summary>
    private Avalonia.Point _altPressCanvasPos;
    /// <summary>Alt 快速选词期间最近一次指针移动的 Canvas 坐标（OCR 完成后补建选区用）。</summary>
    private Avalonia.Point _altLastMoveCanvasPos;
    /// <summary>按下落在空白、锚点走边界锚定（<see cref="FindBlankAnchorIndexAt"/>）时置位：
    /// 选区要等指针真的移动才兑现。只点不动仍等于"点空白"＝取消选中，不凭空高亮一个字。</summary>
    private bool _blankAnchorPending;
    /// <summary>空白起笔那一下的坐标，与 <see cref="_blankAnchorPending"/> 成对（判"有没有真移动过"）。</summary>
    private Avalonia.Point _blankAnchorPressPos;
    /// <summary>Alt 临时选字（文本/Markdown 贴图）：按住 Alt 临时开启文本选择，不进入持久"文本可选择"模式。</summary>
    private bool _altTextStickerSelect;
    /// <summary>Alt+按下直接手动选字（文本贴图：按下瞬间命中测试尚未开启，原生选择拿不到按下事件，改为手动驱动选区）。</summary>
    private bool _altManualTextSelect;
    /// <summary>手动选字锚点（StickerTextBlock 文本位置索引）。</summary>
    private int _altManualAnchorIndex;
    /// <summary>
    /// 文本/Markdown 贴图的选中文本快照（鼠标释放时抓取）。
    /// 原生 SelectableTextBlock 失焦即 ClearSelection——焦点被右键菜单/其他窗口
    /// 夺走后实时选区可能已被清空，Ctrl+C / 右键"复制选中文本"以快照兜底。
    /// </summary>
    private string? _textSelSnapshot;
    /// <summary>窗口已关闭（异步回调守卫：OCR 预热完成后不再触碰已释放的位图/状态）。</summary>
    private bool _isClosed;

    private bool _isApplyingScale = false;
    private Avalonia.PixelPoint _dragStartWindowPosition;
    private Avalonia.PixelPoint _dragStartScreenPoint;

    // --- GPU 纹理上限保护 ---
    /// <summary>GPU 纹理安全上限（物理像素）。D3D11 限制 16384，取 16000 留余量。</summary>
    private const double MaxTexturePhysicalPx = 16000;

    /// <summary>最大缩放比例：确保图片物理尺寸不超过 GPU 纹理上限。
    /// ⚠ 分层呈现档（图片贴图）不受此限（2026-09-29 用户报"长图贴图放不大"）：那条上限按"显示尺寸 ≤ 16000px"
    /// 算，16000 高的长图上限恰好 1.0＝一格都放不上去。分层呈现只把视口那一块 CPU 画进 ≤ 一屏的 DIB，
    /// 显示尺寸再大也不产生对应尺寸的表面或纹理；Avalonia 侧四层此时不透明度为 0、合成器不绘制。</summary>
    private double GetMaxScale()
    {
        double maxDim = Math.Max(_sourcePixelWidth, _sourcePixelHeight);
        if (maxDim <= 0) return 5.0;
        if (LayeredZoomUncapped) return 5.0;
        return Math.Min(5.0, MaxTexturePhysicalPx / maxDim);
    }

    public StickerWindow()
        : this(CreateDesignTimeBitmap(), null, null)
    {
    }

    public int SourcePixelWidth => _sourcePixelWidth;
    public int SourcePixelHeight => _sourcePixelHeight;
    public bool IsMarkdownSticker => _bitmap == null;
    public bool IsClosed => _isClosed;

    // 临时预览与启动自动恢复不属于一次新的用户贴图调用，必须在 Show 前设置。
    internal bool RecordUsageOnOpen { get; set; } = true;
    private bool _usageRecorded;

    private void OnUsageOpened(object? sender, EventArgs e)
    {
        if (_usageRecorded || !RecordUsageOnOpen || _isClosed) return;
        _usageRecorded = true;
        try { UsageStatistics.RecordSticker(); }
        catch (Exception ex) { AppLogger.Warn("贴图使用次数记录失败：" + ex.Message); }
    }

    /// <summary>判重（<see cref="StickerDedup"/>）取内容用：图片贴图给它当前那张位图，其余种类给 null。
    /// 每次现读 <c>_bitmap</c>、不缓存指纹——译文与拖边重排都会换掉它，缓存会让老贴图冒充命中。</summary>
    internal System.Drawing.Bitmap? DedupBitmap => StickerKindTag == "image" ? _bitmap : null;

    /// <summary>判重取内容用：文本 / Markdown / 颜色贴图给源文本，图片贴图给 null
    /// （Alt 选词后 <c>_textContent</c> 对图片贴图同样非空，拿它判重会把两张不同的图判成重复）。</summary>
    internal string? DedupText => StickerKindTag == "image" ? null : _textContent;

    /// <summary>本贴图对应的截图历史条目 Id —— 识别文字回写与贴图存档 JSON 都挂在它上面。
    /// 位图贴图由创建方录入后 <see cref="SetHistoryId"/> 回填；文本 / Markdown 贴图平时只有活体
    /// 控件没有位图，要等排版完成拍快照，故由 <see cref="ScheduleHistoryRecord"/> 自行补录。</summary>
    public string? HistoryId { get; private set; }

    /// <summary>创建方已把这张图录进历史时回填外键，贴图自身不再重复录入。</summary>
    public void SetHistoryId(string? id)
    {
        if (!string.IsNullOrEmpty(id)) HistoryId = id;
    }

    /// <summary>把当前显示内容补录进截图历史。已录过、已关窗、排版尚未就绪时静默跳过。</summary>
    public void EnsureHistoryRecorded()
    {
        if (_isClosed || HistoryId != null) return;
        try
        {
            // 位图贴图（非颜色贴图）直接用现成的 _bitmap：它归本窗口持有，既不能重拍也不能释放。
            System.Drawing.Bitmap? snap = (_bitmap == null || StickerColorSwatch.IsVisible) ? SnapshotTextSticker() : null;
            System.Drawing.Bitmap? bmp = snap ?? _bitmap;
            if (bmp == null) return;
            try
            {
                // 可搜文本在录入时刻一起写：图片贴图留空（它的文字走识别回写），文本 / Markdown /
                // 颜色贴图存源文本。判据必须取 StickerKindTag 而不是 _textContent 是否非空 ——
                // 图片贴图做过 Alt 选词后该字段同样非空。
                string? text = StickerKindTag == "image" ? null : _textContent;
                HistoryId = CaptureHistoryStore.TryRecord(
                    bmp, CaptureHistorySource.Sticker, null, AppSettings.Current, text);
                if (HistoryId != null)
                {
                    AppLogger.Info("StickerWindow: 贴图已补录进历史 " + HistoryId
                                   + " 源=" + (snap != null ? "排版快照" : "位图")
                                   + " " + bmp.Width + "x" + bmp.Height
                                   + " 文字=" + (text?.Length ?? 0) + " 字");
                    SaveArchiveSnapshot();
                }
            }
            finally
            {
                if (snap != null) { try { snap.Dispose(); } catch { } }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 贴图补录历史失败（不影响贴图使用）。" + ex.Message);
        }
    }

    /// <summary>等排版完成后补录历史。<see cref="DispatcherPriority.Loaded"/> 排在 Avalonia 布局
    /// 之后，每轮重试都会让消息循环先走完一次布局；仍拿不到尺寸就放弃，留给关窗时兜底。</summary>
    public void ScheduleHistoryRecord(int attemptsLeft = 6)
    {
        if (_isClosed || HistoryId != null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed || HistoryId != null) return;
            EnsureHistoryRecorded();
            if (HistoryId == null && attemptsLeft > 1) ScheduleHistoryRecord(attemptsLeft - 1);
        }, DispatcherPriority.Loaded);
    }

    public static StickerWindow FromMarkdown(string markdown, int maxContentWidth, Avalonia.Media.Color? bgColor = null)
    {
        var win = new StickerWindow(null, markdown);
        win.TextViewbox.IsVisible = true;
        win.TextStickerContainer.IsVisible = true;
        win.StickerMarkdownViewer.IsVisible = true;
        win.StickerTextBlock.IsVisible = false;
        win.StickerImage.IsVisible = false;
        win.StickerColorSwatch.IsVisible = false;

        // bgColor == null 的语义是"剪贴板 HTML 没自带背景"，此时画布跟着应用主题走。
        // 主题派生的底色**不碰 RequestedThemeVariant**——窗口本来就是应用主题的延续，
        // 再强翻一档会让正文配色与界面明暗打架（此前无条件白底 + 强设 Light 就是"浅底白字"的成因）。
        bool bgFollowsTheme = !bgColor.HasValue;
        Avalonia.Media.Color finalBgColor = bgColor
            ?? App.ResolveThemeColor("SctStickerBgColor")
            ?? Avalonia.Media.Colors.White;
        var brush = new Avalonia.Media.SolidColorBrush(finalBgColor);
        // 底色只能给 TextStickerContainer（与内容严格同尺寸），绝不能给 RootBorder：
        // RootBorder 是 Stretch 铺满整个窗口内容区的，窗口一旦大于内容（信封态），
        // 这块不透明底色就溢出成"巨大的纯色画布"（同 protection.md 信封态视觉层红线）。
        win.TextStickerContainer.Background = brush;

        if (!bgFollowsTheme)
        {
            double luminance = 0.299 * finalBgColor.R + 0.587 * finalBgColor.G + 0.114 * finalBgColor.B;
            bool darkCanvas = luminance < 128;
            // 底色来自剪贴板 HTML（源程序捕获那一刻的颜色）。正文块在本窗口 Show 之前就建好了，
            // 那一刻控件的 ActualThemeVariant 还解析不出档位，环境着色会退到应用档——
            // 深色模式下复制一段自带白底的内容，就成了白画布配深色档近白字＝白底白字（用户实测）。
            // 故把画布所属档位显式交给正文，字色与画布同源，不再问环境。
            var canvasVariant = darkCanvas
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
            win.RequestedThemeVariant = canvasVariant;
            win.StickerMarkdownViewer.ForcedThemeVariant = canvasVariant;
            win.StickerMarkdownViewer.SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty,
                darkCanvas ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.Black);
        }
        else
        {
            // 底色与字色必须同档同时机取值，否则主题一切换就失对比：正文由
            // SelectableMarkdownTextBlock 订阅 ActualThemeVariantChanged 实时重算字色，
            // 而上面那块 brush 只是创建瞬间的快照。先浅色态钉住再切深色＝白底冻住、字变白。
            // 故这里同样订阅主题变化，把底色重取一次（HTML 自带背景那档已把窗口
            // RequestedThemeVariant 钉死，窗口 ActualThemeVariant 不随应用变，无需订阅）。
            win.ActualThemeVariantChanged += (s, e) =>
            {
                if (win._isClosed) return;
                Avalonia.Media.Color? refreshed = App.ResolveThemeColor("SctStickerBgColor");
                if (refreshed.HasValue)
                {
                    win.TextStickerContainer.Background = new Avalonia.Media.SolidColorBrush(refreshed.Value);
                }
            };
        }

        AppLogger.Info($"StickerWindow.FromMarkdown: 底色来源={(bgFollowsTheme ? "主题SctStickerBgColor" : "剪贴板HTML自带")}, bg=#{finalBgColor.R:X2}{finalBgColor.G:X2}{finalBgColor.B:X2}, 正文档={(win.StickerMarkdownViewer.ForcedThemeVariant?.ToString() ?? "跟随环境")}");

        // 隐藏文本块，避免占位
        if (win.StickerTextBlock != null) win.StickerTextBlock.IsVisible = false;

        // 禁用 Viewbox 缩放 —— Markdown 内容应按比例缩放，使用 Stretch="Uniform"
        win.TextViewbox.Stretch = Stretch.Uniform;

        win.StickerMarkdownViewer.Markdown = markdown;

        // 设置宽度约束（强制换行），但不设高度和外框固定宽度 —— 让窗口自动适应紧凑内容尺寸
        win.StickerMarkdownViewer.MaxWidth = maxContentWidth > 24 ? maxContentWidth - 24 : maxContentWidth;
        win.StickerMarkdownViewer.Margin = new Avalonia.Thickness(12);
        
        // ⚠ 物理像素口径：maxContentWidth 是 DIP 口径参数（上方 viewer.MaxWidth 同值），必须乘 DPI
        // 后入册。直写 DIP 数值＝150% 屏下宽度簿记少 1/3，捕获前首帧布局就把窗口缩到窄于内容，
        // 文本右侧被裁（缺陷 B"背景消失"的起点之一）。估测块与延迟捕获会随后精修。
        win._sourcePixelWidth = (int)Math.Round(maxContentWidth * win.SafeScaling);
        win._sourcePixelHeight = 0; // 占位，OnOpened 后捕获实际值

        // 标记需要在 OnOpened 后捕获实际高度
        win._needsMarkdownHeightCapture = true;

        // 窗口显示后，等待布局完成，捕获实际渲染尺寸
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 先用 Measure 获取一个估算值，以便 OnOpened 时的 ApplyScale 有合理初始值
                win.StickerMarkdownViewer.InvalidateMeasure();
                win.StickerMarkdownViewer.Measure(new Avalonia.Size(maxContentWidth, double.PositiveInfinity));
                var desired = win.StickerMarkdownViewer.DesiredSize;
                // ⚠ 口径修正：DesiredSize 是 Measure 的 DIP 结果，+12 也是 DIP 边距，入册前必须
                // 换算为物理像素（原直写＝150% 屏下高度少 1/3，首帧窗口矮于内容、底部被裁）。
                double sEst = win.SafeScaling;
                // 🔴 账本里只放实测值，禁止再夹"窗口最小尺寸"那类地板（2026-10-02 的右侧空白成因）：
                // _sourcePixel* 同时是阴影光晕的基准线与信封贴合的尺寸来源，把它钉成 120/60 地板
                // ＝窗口按地板长、卡片按实测画，两者之差整块落成右侧（Viewbox 左对齐）的透明带。
                // 实测为 0＝布局还没出结果，此时保持占位不动，而不是拿地板值顶上去。
                int estimatedHeight = Math.Max(0, (int)Math.Round((Math.Ceiling(desired.Height) + 12) * sEst));
                int estimatedWidth = Math.Max(0, (int)Math.Round(desired.Width * sEst));
                if (win._sourcePixelHeight == 0 && estimatedHeight > 0)
                {
                    win._sourcePixelHeight = estimatedHeight;
                }
                // 宽度同样按实测收紧（desired.Width＝自然内容与约束宽的较小者，比约束宽占位更准）
                if (estimatedWidth > 0 && Math.Abs(win._sourcePixelWidth - estimatedWidth) > 1)
                {
                    win._sourcePixelWidth = estimatedWidth;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StickerWindow: Markdown pre-open measure failed: " + ex.Message);
            }
        });

        // OnOpened 之后再次延迟捕获（此时布局已完成，尺寸准确）
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 等待两帧确保布局完全稳定
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        double s = win.SafeScaling;
                        double actualW = win.TextStickerContainer.Bounds.Width;
                        double actualH = win.TextStickerContainer.Bounds.Height;

                        // 两个轴都要实测到才收口（同上：0 读数是"还没排版完"，不是"内容真的小"）
                        if (actualW > 0 && actualH > 0)
                        {
                            int newW = (int)Math.Round(actualW * s);
                            int newH = (int)Math.Round(actualH * s);

                            // 死区由 3px 收紧为 1px：捕获收口必须把窗口表面对齐到 1px 级——3px 内的
                            // 错配足以让下一次手势起手的贴合矩形按估测尺寸现算、把窗口重摆一次（缺陷 B）。
                            bool changed = Math.Abs(newW - win._sourcePixelWidth) > 1 ||
                                           Math.Abs(newH - win._sourcePixelHeight) > 1;
                            AppLogger.Info($"StickerWindow: Markdown 捕获收口 ({win._sourcePixelWidth}x{win._sourcePixelHeight})→({newW}x{newH}) 重排收口={changed}");

                            win._sourcePixelWidth = newW;
                            win._sourcePixelHeight = newH;
                            win._needsMarkdownHeightCapture = false;

                            if (changed)
                            {
                                win.ApplyScale();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("StickerWindow: Markdown delayed height capture failed: " + ex.Message);
                    }

                    // 设置 SelectionBrush
                    try
                    {
                        var selectionBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#900078D7"));
                        win.ApplySelectionBrushToMarkdownChildren(selectionBrush);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("StickerWindow: ApplySelectionBrushToMarkdownChildren failed: " + ex.Message);
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StickerWindow: Markdown post-open capture failed: " + ex.Message);
            }
        });

        return win;
    }

    /// <summary>
    /// 遍历 MarkdownScrollViewer 的可视树，给所有 CTextBlock 设置 SelectionBrush。
    /// MarkdownScrollViewer 内部有自己的 Styles 集合，可能覆盖 Window 级别的样式，
    /// 所以需要在渲染完成后直接在控件实例上设置属性。
    /// </summary>
    private void ApplySelectionBrushToMarkdownChildren(IBrush brush)
    {
        if (StickerMarkdownViewer != null)
        {
            StickerMarkdownViewer.SelectionBrush = brush;
        }
    }

public StickerWindow(System.Drawing.Bitmap? bitmap, string? textContent = null, TextLayoutInfo? layoutInfo = null,
    System.Drawing.Bitmap? cleanAnnotationSource = null,
    IReadOnlyList<Annotations.AnnotationShape>? annotationShapes = null,
    IReadOnlyList<TextLayerSnapshot>? annotationTexts = null,
    double preShowScaling = 0)
{
_preShowScaling = preShowScaling;
_textContent = textContent;
_layoutInfo = layoutInfo;
_initialAnnotationSource = cleanAnnotationSource;
_initialAnnotationShapes = annotationShapes;
_initialAnnotationTexts = annotationTexts;

if (bitmap != null)
{
// 直接引用传入的 Bitmap，避免 new Bitmap(bitmap) 造成一次全帧拷贝。
// 调用方负责确保 bitmap 在 StickerWindow 生命周期内不被提前 Dispose。
_bitmap = bitmap;
_source = ToAvaloniaBitmap(_bitmap);
_sourcePixelWidth = _bitmap.Width;
_sourcePixelHeight = _bitmap.Height;
}

        InitializeComponent();

        if (StickerContextMenu != null)
        {
            StickerContextMenu.Opening += (_, _) => UpdateContextMenuState();
        }

        // 共享编辑会话（2026-09-22 上提）：两枚浮条的事件订阅、图层选中几何通知、落笔控制器与形状编辑器
        // 的构造全在里面，贴图与录屏消费同一份。必须在 InitializeComponent 之后建——它要订阅那三个控件。
        _anno = new ScreenCaptureTool.Services.AnnotationEditSession(this);

        if (!string.IsNullOrEmpty(_textContent) && ScreenCaptureTool.Services.ColorHelper.TryParseColor(_textContent, out var color, out var format))
        {
            TextViewbox.IsVisible = true;
            TextStickerContainer.IsVisible = true;
            StickerTextBlock.IsVisible = true;
            StickerImage.IsVisible = false;
            
            // Allow tight wrapping
            StickerTextBlock.Margin = new Avalonia.Thickness(12);

            StickerColorSwatch.IsVisible = true;
            StickerColorSwatch.Background = new Avalonia.Media.SolidColorBrush(color);

            StickerTextBlock.Text = ScreenCaptureTool.Services.ColorHelper.FormatColorInfo(color, format);
            StickerTextBlock.FontFamily = new Avalonia.Media.FontFamily("Consolas");
            // 底色走主题，前景按底色亮度取：浅色档仍是纯黑白底（与改动前逐像素一致），
            // 暗色档翻成白字深底，否则深底黑字读不出来。
            var colorCardBg = App.ResolveThemeColor("SctStickerBgColor") ?? Avalonia.Media.Colors.White;
            double colorCardLuminance = 0.299 * colorCardBg.R + 0.587 * colorCardBg.G + 0.114 * colorCardBg.B;
            StickerTextBlock.Foreground = colorCardLuminance < 128
                ? Avalonia.Media.Brushes.White
                : Avalonia.Media.Brushes.Black;
            TextStickerContainer.Background = new Avalonia.Media.SolidColorBrush(colorCardBg);

            StickerTextBlock.ContextMenu = RootBorder.ContextMenu;
            StickerTextBlock.ContextMenu = RootBorder.ContextMenu;
        }
        else if (!string.IsNullOrEmpty(_textContent) && _layoutInfo != null && _layoutInfo.HtmlLines != null && _layoutInfo.HtmlLines.Count > 0)
        {
            TextViewbox.IsVisible = true;
            TextStickerContainer.IsVisible = true;
            StickerTextBlock.IsVisible = true;
            StickerImage.IsVisible = false;
            
            // 版宽约束取「这次位图排版实际用的折行预算」换算成 DIP，与 GDI 排这几行时用的预算**同一个值**。
            // 🔴 旧写法取「位图宽 − GDI 内边距」＝拿 GDI 对自己那几行的实测行宽当约束（零余量），
            // Avalonia 排同样几个字只要略宽一点就**假折行**。日志实证（2026-10-02 12:39，DPI 1.00）：
            // 位图那侧是「单行 30px 宽 × 22px 高」＝62x54，活控件被 MaxWidth=62−32=30 卡住后排成
            // 三行、每行 1 个字＝42x70（宽 18+12×2 内边距、高 46+12×2）。账本(62x54)与卡片(42x70)
            // 两个轴同时分叉，差值整块落成右侧透明带＝用户报的「阴影比图片大、右边多一片空白」。
            // ⚠ 口径：Avalonia 的 MaxWidth 管的是**内容盒**，Margin 在其外再加（上面 42=18+24 即证），
            // 所以这里直接取预算值、不再减 12×2——减了等于又给 GDI 的预算凭空砍掉一截内边距。
            double wrapDip = (_layoutInfo?.WrapWidthPhysical ?? 0) / SafeScaling;
            StickerTextBlock.MaxWidth = wrapDip >= 24
                ? wrapDip
                : (_sourcePixelWidth > 32 ? _sourcePixelWidth - 32 : _sourcePixelWidth);
            StickerTextBlock.Margin = new Avalonia.Thickness(12);

            // Set native background
            SetNativeBackground();

            // Populate TextBlock Inlines from HtmlLines
            PopulateTextBlockInlines(forceTransparent: false);

            // Set font family and context menu
            bool isCode = TextStickerRenderer.IsCodeOrFormatted(_textContent);
            StickerTextBlock.FontFamily = new Avalonia.Media.FontFamily(isCode ? "Consolas" : "Microsoft YaHei UI");
            StickerTextBlock.ContextMenu = RootBorder.ContextMenu;

            // Tunneling handlers for text block pointer interactions removed
        }
        else
        {
            if (TextViewbox != null) TextViewbox.IsVisible = false;
            TextStickerContainer.IsVisible = false;
            StickerTextBlock.IsVisible = false;
            StickerImage.IsVisible = true;
            StickerImage.Source = _source;
            
            // 图片背后那一层（棋盘格 / 阴影带浅灰垫 / 什么都不露）的唯一写入口：
            // 判据与理由见 UpdateAlphaBackingLayer 与 HasTransparentPixels 的注释。
            UpdateAlphaBackingLayer();
        }

        // Expose text selection toggle in context menu for both text and image stickers
        UpdateSelectableTextMenuItem();

        if (TextViewbox != null && TextViewbox.IsVisible)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    try {
                        var card = TextStickerContainer.Bounds;
                        double s = SafeScaling;
                        if (card.Width > 0 && card.Height > 0) {
                            // 死区 3px→1px：账本同时是阴影光晕的基准线，3px 内的错配就是卡片与
                            // 光晕之间那一条缝（与 Markdown 捕获收口同一口径，见「捕获收口」那条日志）。
                            int newW = (int)Math.Round(card.Width * s);
                            int newH = (int)Math.Round(card.Height * s);
                            bool mismatch = Math.Abs(newW - _sourcePixelWidth) > 1 || Math.Abs(newH - _sourcePixelHeight) > 1;
                            AppLogger.Info($"[缩放诊断] 文本贴图账本收口 账本({_sourcePixelWidth}x{_sourcePixelHeight})"
                                           + $"→实测({newW}x{newH}) 落账={mismatch}"
                                           + $" 窗口内容区={Bounds.Width * s - ShadowMargin * 2 * s:F0}x{Bounds.Height * s - ShadowMargin * 2 * s:F0}"
                                           + $" SizeToContent={SizeToContent} MaxWidthDIP={StickerTextBlock.MaxWidth:F0}"
                                           + $" 折行预算物理={_layoutInfo?.WrapWidthPhysical ?? 0}px×DPI{s:F2}");
                            if (mismatch) {
                                _sourcePixelWidth = newW;
                                _sourcePixelHeight = newH;
                                ApplyScale();
                            }
                        }
                        else {
                            // 一次性两帧回写踩在布局未产出上＝整条收口作废（账本停在位图口径，右侧留空白带）
                            AppLogger.Warn($"[缩放诊断] 文本贴图账本收口跳过：卡片实测={card.Width:F1}x{card.Height:F1}（布局未产出，账本保持 {_sourcePixelWidth}x{_sourcePixelHeight}）");
                        }
                    } catch (Exception ex) {
                        AppLogger.Warn("StickerWindow: 文本贴图账本收口失败: " + ex.Message);
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            });
        }

        StickerImage.RenderTransform = _imageTransform;
        StickerTextBlock.RenderTransform = _textTransform;
        StickerCanvas.RenderTransform = _canvasTransform;

        // Register window-level pointer handlers in Tunneling phase to support drag-resize, move, and pan
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, e) =>
        {
            if (ReferenceEquals(e.Pointer, _dragPointer)) EndStickerDrag("指针捕获中断");
        });
        AddHandler(PointerExitedEvent, OnWindowPointerExited, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnRootPointerWheelChanged, RoutingStrategies.Tunnel);
        // 文本层按下的冒泡补做（截图侧同一相位，见 OnAnnotationTextPointerPressedBubble 注释）
        AddHandler(PointerPressedEvent, OnAnnotationTextPointerPressedBubble, RoutingStrategies.Bubble);

        // 窗口句柄创建前 DesktopScaling 不可靠：调用方给了 preShowScaling 就用它（首帧几何直接正确，
        // Opened 里不再补一次 resize），两者都缺时才按 1.0 占位、等 Opened 精确还原。
        ApplyScale();

        // 窗体阴影的圆角跟随图片实时尺寸。订阅单一入口而非在各个几何变更点各补一行：
        // 滚轮信封、拖边缩放、缩略图、换图任何一条通路改了图片尺寸都会自动跟上
        // （漏点＝"阴影跟错尺寸"，与文本宿主层成对同步那类同型风险）。
        StickerImage.SizeChanged += (_, _) => UpdateShadowCornerRadius();

        // 划词装饰层与文字同一个坐标系：拖宽/重排都会改行分布，块装饰只存字符锚点、
        // 矩形按当时的排版现算 ⇒ 订阅容器尺寸这一个入口，不在各条缩放通路各补一行（漏点＝笔迹跑位）。
        TextStickerContainer.SizeChanged += (_, _) => ScheduleMarkRedraw();
        // 图片贴图那一层的笔迹存的是 OCR 像素矩形，尺寸（＝缩放）一变就要按新的 _scale 重乘一遍；
        // 与上面那条同构：订阅尺寸这一个入口，不追 ApplyScale 里的每一条通路。
        StickerCanvas.SizeChanged += (_, _) => ScheduleMarkRedraw();

        Opened += OnWindowOpened;
        Opened += OnUsageOpened;
        Activated += (_, _) => BringStickerToFront();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        InitCanvasMode();
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        // 与截图同一挂载：Ctrl+Enter 提交文本的兜底（焦点短暂落在窗口而不是 TextBox 时仍能提交）+ Ctrl 状态跟踪
        AddHandler(KeyDownEvent, TextTool.OnTextEditingKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyUpEvent, TextTool.OnTextEditingKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        Deactivated += OnWindowDeactivated;
        PositionChanged += OnPositionChanged;
        Closing += OnWindowClosing;
        Closed += OnClosed;
        // 全局鼠标钩子**不在这里装**：它只为穿透态贴图的右键菜单服务，进出穿透态时才挂/卸
        // （见 PenetrationStickers 注释——按"贴图总数"装会让本软件全天候占用系统输入链）。
        WheelDiag("StickerWindow 构造完成 isImg=" + (StickerImage != null && _bitmap != null) + " hasBitmap=" + (_bitmap != null));
    }

    /// <summary>
    /// 图片是否**实际含有**透明/半透明像素（而非仅仅像素格式带 alpha 通道）。
    /// 用于决定是否显示棋盘格背景：格式带 alpha 但内容全不透明的图（如译文渲染结果）
    /// 不应露出棋盘格。
    ///
    /// 抽样而非全量扫描：大图逐像素读代价高，且透明区通常成片出现，
    /// 网格抽样足以判定；一旦发现透明像素立即返回。
    /// </summary>
    private static bool HasTransparentPixels(System.Drawing.Bitmap? bmp)
    {
        if (bmp == null) return false;
        if (!System.Drawing.Image.IsAlphaPixelFormat(bmp.PixelFormat)) return false;

        try
        {
            int stepX = Math.Max(1, bmp.Width / 64);
            int stepY = Math.Max(1, bmp.Height / 64);
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    if (bmp.GetPixel(x, y).A < 250) return true;
                }
            }

            // 边缘另查一圈：透明区常见于四周（如带圆角/异形裁剪的贴图）
            int w = bmp.Width - 1, h = bmp.Height - 1;
            for (int x = 0; x <= w; x += stepX)
            {
                if (bmp.GetPixel(x, 0).A < 250 || bmp.GetPixel(x, h).A < 250) return true;
            }
            for (int y = 0; y <= h; y += stepY)
            {
                if (bmp.GetPixel(0, y).A < 250 || bmp.GetPixel(w, y).A < 250) return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("HasTransparentPixels 检测失败，按不透明处理: " + ex.Message);
            return false;
        }

        return false;
    }

    private void SetNativeBackground()
    {
        try
        {
            if (_isCutout)
            {
                // 抠图贴图背景必须完全纯透明，严禁注入底色层
                if (ImageBackdrop != null)
                {
                    ImageBackdrop.IsVisible = false;
                    ImageBackdrop.Background = Avalonia.Media.Brushes.Transparent;
                }
                RootBorder.Background = Avalonia.Media.Brushes.Transparent;
                return;
            }

            if (_bitmap != null && _bitmap.Width > 8 && _bitmap.Height > 8)
            {
                System.Drawing.Color gdiColor = _bitmap.GetPixel(8, 8);
                var avaloniaColor = Avalonia.Media.Color.FromArgb(255, gdiColor.R, gdiColor.G, gdiColor.B);
                var solidBrush = new Avalonia.Media.SolidColorBrush(avaloniaColor);

                // 底色只能给到「与图片同尺寸」的 ImageBackdrop，绝不能给 RootBorder。
                //
                // RootBorder 是 Stretch 铺满**整个窗口内容区**的。而滚轮缩放走
                // 「零 resize 信封态」时窗口会被撑得远大于图片，此时 RootBorder 的
                // 不透明底色就填满整个信封区 —— 观感是「图片浮在一块巨大的纯色画布里」
                // （实测该海报 (8,8) 像素为纯白，故表现为巨大白色画布；用户实测反馈）。
                if (ImageBackdrop != null)
                {
                    ImageBackdrop.Background = solidBrush;
                    // 仅图片贴图需要底色垫底；文本贴图由 TextStickerContainer 自己承担
                    ImageBackdrop.IsVisible = StickerImage != null && StickerImage.IsVisible;
                }
                RootBorder.Background = Avalonia.Media.Brushes.Transparent;
                TextStickerContainer.Background = solidBrush;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 设置 RootBorder 背景颜色失败: " + ex.Message);
        }
    }

    private void PopulateTextBlockInlines(bool forceTransparent = false)
    {
        var textBlock = StickerTextBlock;
        if (textBlock == null || _layoutInfo == null || _layoutInfo.HtmlLines == null) return;

        textBlock.Inlines?.Clear();
        bool isFirstLine = true;
        foreach (var line in _layoutInfo.HtmlLines)
        {
            if (!isFirstLine)
            {
                textBlock.Inlines?.Add(new LineBreak());
            }
            isFirstLine = false;

            foreach (var run in line.Runs)
            {
                Avalonia.Media.IBrush brush = forceTransparent 
                    ? (Avalonia.Media.IBrush)Avalonia.Media.Brushes.Transparent 
                    : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(255, run.Color.R, run.Color.G, run.Color.B));

                var textRun = new Run
                {
                    Text = run.Text,
                    Foreground = brush,
                    FontWeight = run.IsBold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                    FontStyle = run.IsItalic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal
                };
                textBlock.Inlines?.Add(textRun);
            }
        }
    }

    /// <summary>
    /// 设置贴图 Image 左上角在屏幕的目标位置（物理像素）。
    /// 调用方传入"希望图片出现在哪"，本方法自动把窗口左上角左移/上移阴影边距，
    /// 使 Image 精确落在目标位置（窗口本身比 Image 大一圈容纳阴影）。
    /// 在 Opened 后调用最准确（此时 DesktopScaling 可靠）。
    /// </summary>
    public void SetStickerScreenPosition(Avalonia.PixelPoint imageTopLeft)
    {
        double s = SafeScaling;
        int marginPx = (int)Math.Round(ShadowMargin * s);
        // 窗口左上角 = Image 目标位置 - 阴影边距，使 Image 落在目标位置。
        var target = new PixelPoint(imageTopLeft.X - marginPx, imageTopLeft.Y - marginPx);
        if (_handleReady && IsVisible)
        {
            // 存档／历史定位沿用移动通路，位置和屏幕几何簿记一起落地。
            _lastAppliedDragPos = new PixelPoint(int.MinValue, int.MinValue);
            ApplyDragMove(target);
        }
        else Position = target;
    }

    /// <summary>窗口是否仍有效（未关闭）。供 StickerFocusManager 判断弱引用是否存活。</summary>
    internal static bool IsAlive(StickerWindow? window) => window != null;

    /// <summary>
    /// 把贴图图片区居中到鼠标所在显示器的可视区。供历史窗口/图片查看器这类
    /// 没有既定落点、也没有存档位置的重新贴图入口使用；须在 Show 之后调用
    /// （要按窗口实际尺寸折算图片区，此时 SafeScaling 也已可靠）。
    /// </summary>
    public void CenterStickerOnWorkArea()
    {
        if (_isClosed) return;
        try
        {
            // 鼠标所在屏的可视区（物理像素）：用户正看着查看器点贴图，落在他眼前的屏上最合理
            var monitor = ScreenCaptureTool.Platform.MonitorHelper.GetMonitorAtCursor();
            var work = monitor;
            foreach (var r in ScreenCaptureTool.Platform.MonitorHelper.GetWorkAreaRects())
            {
                if (r.IntersectsWith(monitor)) { work = r; break; }
            }
            if (work.Width <= 0 || work.Height <= 0) return;

            double s = SafeScaling;
            int marginPx = (int)Math.Round(ShadowMargin * s);
            // 窗口物理尺寸减去两侧阴影边距 = 画面尺寸（SetStickerScreenPosition 要的是画面左上角）
            int imgW = Math.Max(0, (int)Math.Round(Bounds.Width * s - marginPx * 2));
            int imgH = Math.Max(0, (int)Math.Round(Bounds.Height * s - marginPx * 2));
            int x = work.Left + Math.Max(0, (work.Width - imgW) / 2);
            int y = work.Top + Math.Max(0, (work.Height - imgH) / 2);
            SetStickerScreenPosition(new Avalonia.PixelPoint(x, y));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CenterStickerOnWorkArea 失败: " + ex.Message);
        }
    }

    /// <summary>由 StickerFocusManager 调用：本贴图获得焦点，阴影变蓝（若阴影开关开着）。</summary>
    internal void NotifyFocused()
    {
        _isFocused = true;
        ApplyShadow();
        UpdateAnnotationPalettePosition();
        try
        {
            this.Activate();
        }
        catch { }
    }

    /// <summary>由 StickerFocusManager 调用：本贴图失去焦点，阴影变灰黑（若阴影开关开着）。</summary>
    internal void NotifyUnfocused()
    {
        _isFocused = false;
        ApplyShadow();
        if (_isAnnotating)
        {
            // 文本编辑期失焦＝只提交文字、不退出标注：抢走前台的往往是输入法候选窗／取色窗这类
            // 同进程临时窗，照旧退出标注的观感就是"打了个字就掉出标注模式"。
            if (TextTool.HasActiveTextEditing)
            {
                TextTool.EndActiveTextEditing("Unfocused");
                return;
            }
            // 贴图失焦时退出标注模式
            ExitAnnotationMode();
        }
    }

    /// <summary>
    /// 声明"这张位图的外缘是截图效果烘焙出来的"：四角透明属于图形本身，不是"图没填满"。
    ///
    /// ⚠ 圆角与描边<b>不</b>关闭窗体阴影——过去把两件事混成一句 <c>_shadowVisible=false</c>，
    /// 结果是"只设圆角、没勾阴影"的贴图<b>一点阴影都没有</b>（2026-09-06 修）。
    /// 烘焙阴影带同样<b>不</b>关窗体阴影（2026-09-07 用户拍板）：窗体阴影是 UI 状态指示
    /// （焦点蓝 / 锁定橙 / 穿透绿、Y 键开关），烘焙阴影是图片素材，两个不是同一个东西，
    /// 必须并存。并存时的层次：棋盘格是窗体的背景板、图片（含其阴影带）铺在棋盘格上、
    /// 窗体光晕包在棋盘格<b>外面</b>——故带阴影带时光晕基准就是位图外矩形
    /// （<paramref name="card"/> 的圆角/描边只对无阴影带的图生效）。
    /// </summary>
    /// <param name="card">成品<b>卡片几何</b>（内容圆角半径 + 描边宽，位图物理像素），取自
    /// <c>SelectionEffects.CardGeometryOf</c>；无从得知参数的外来图只能取
    /// <c>CardGeometry { ContentRadiusPx = ProbeOuterRadiusPx(...), StrokeWidthPx = 0 }</c>。</param>
    /// <param name="hasBakedShadow">位图自身是否带一圈烘焙阴影带。</param>
    /// <param name="shadowPadPx">烘焙阴影带来的物理边距（含阴影+描边），为 0 时按默认口径反推。</param>
    public void ApplyBakedCaptureEffects(SelectionEffects.CardGeometry card, bool hasBakedShadow = false, int shadowPadPx = 0)
    {
        _bakedCardGeometry = new SelectionEffects.CardGeometry
        {
            ContentRadiusPx = Math.Max(0, card.ContentRadiusPx),
            StrokeWidthPx = Math.Max(0, card.StrokeWidthPx),
        };
        _bakedShadowPadPx = hasBakedShadow
            ? (shadowPadPx > 0 ? shadowPadPx : SelectionEffects.MarginOf(true, (int)Math.Ceiling(card.StrokeWidthPx)))
            : 0;
        _hideAlphaCheckerboard = true;
        _hasBakedShadow = hasBakedShadow;
        // 背后那一层由 ApplyShadow 开头的 UpdateAlphaBackingLayer 统一刷新（垫棋盘格 / 藏棋盘格）
        ApplyShadow();
        if (hasBakedShadow)
        {
            AppLogger.Info("StickerWindow: 检测到自带阴影带 → 阴影带下垫透明棋盘格（仅窗口显示，不进导出与复制）；"
                + "失焦时自动隐藏窗体阴影，聚焦时沿内容主体内缩轮廓高亮提示");
        }
    }

    /// <summary>
    /// 图片贴图背后那一层的<b>唯一</b>写入口，三态互斥：
    /// ① 棋盘格 —— 普通透明 PNG，且从未声明"外缘属于烘焙效果"；
    /// ② 棋盘格 —— 外圈真有一圈烘焙阴影带（<see cref="_hasBakedShadow"/>）：那圈阴影是按
    ///    "压在浅色页面上"烘进位图的半透明黑（峰值约 17%），直接铺在桌面上会跟壁纸混色、
    ///    深色壁纸下几乎看不见；垫上棋盘格给它一个与桌面无关的对比底，用户才能看见
    ///    "这张图带阴影"（2026-09-07 用户拍板：要的就是看得见的图片阴影，导出与复制
    ///    走位图本体、天然带真实透明阴影，本层只进窗口显示）。窗体阴影与棋盘格并存：
    ///    棋盘格是窗体的背景板，光晕包在棋盘格<b>外面</b>（基准＝位图外沿，见
    ///    ApplyBakedCaptureEffects），两种阴影一外一内、互不重叠；
    /// ③ 什么都不露 —— 自家截图转贴图（圆角/描边但未烘阴影），那圈透明本该透出桌面。
    ///
    /// ①②共用同一块棋盘格画刷、同一个 <c>CheckerboardBg</c> 层：该层的宽高与 Margin 已在
    /// <c>ApplyScale</c> / <c>SetWheelGestureVisual</c> / <c>ResetWheelVisualOverride</c> 三处与图片
    /// 严格同步（🔴 信封态禁用 Stretch，见 docs/protection.md），复用即零新增几何同步点。
    /// </summary>
    private void UpdateAlphaBackingLayer()
    {
        // 这一层是图片层的背衬。文本／Markdown 贴图永远不参与：本方法挂在 ApplyShadow 上，
        // 对文本贴图也会被调用，若无这道守卫，正文卡片位图一旦含透明像素就会在文字后面透出棋盘格。
        if (CheckerboardBg == null || StickerImage == null || !StickerImage.IsVisible) return;

        // 缩略图模式不垫：棋盘格层没有图片的视口 RenderTransform，垫了会与图片错位
        if (_isThumbnailMode)
        {
            CheckerboardBg.IsVisible = false;
            return;
        }

        // 用户选了"纯透明"（设置 → 贴图 → 透明图片背景，2026-09-29）：①② 两种格子一律不垫，
        // 透明处直接透出桌面。窗体光晕与本层无关、照常画。
        if (!AppSettings.Current.StickerTransparentCheckerboard)
        {
            CheckerboardBg.IsVisible = false;
            return;
        }

        // 自带烘焙阴影带 → 垫棋盘格（②）
        if (_hasBakedShadow)
        {
            CheckerboardBg.IsVisible = true;
            return;
        }

        // 透明像素探测按位图实例缓存：本方法挂在 ApplyShadow 开头，焦点／锁定／缩略图
        // 任何一次状态变化都会跑进来，而 HasTransparentPixels 是采样扫描。
        // 换图一律是新实例（ReplaceImage / 重排重拍都是整体替换），故引用不等即重算，无需别处失效。
        if (_hideAlphaCheckerboard)
        {
            CheckerboardBg.IsVisible = false;
        }
        else
        {
            if (!ReferenceEquals(_transparentProbeSource, _bitmap))
            {
                _transparentProbeSource = _bitmap;
                _transparentProbeResult = HasTransparentPixels(_bitmap);
            }
            CheckerboardBg.IsVisible = _transparentProbeResult;
        }
    }

    /// <summary>
    /// 窗体阴影的圆角跟随图片外轮廓。直角 <c>BoxShadow</c> 会从圆角图片后面露出四个方角尖
    /// （2026-09-04（六）的"贴图窗口直角阴影穿透"就是这个）；现由 <c>StickerShadowLayer</c> 按
    /// 同一族超椭圆画等距光晕，正圆弧"角部比图片缩进 0.231r、四角直接没阴影"的老毛病一并消失。
    ///
    /// 半径一律按<b>图片实时显示宽 ÷ 位图物理宽</b>换算：滚轮信封态与拖边缩放都把
    /// <c>ShadowBorder</c> 钉成与图片同矩形（<c>SetWheelGestureVisual</c>），故这个比值在两态下都成立，
    /// 不需要在每个几何变更点各补一行。
    /// </summary>
    private void UpdateShadowCornerRadius()
    {
        if (ShadowBorder == null || StickerImage == null) return;
        double shownW = double.IsNaN(StickerImage.Width) || StickerImage.Width <= 0
            ? StickerImage.Bounds.Width : StickerImage.Width;
        double ratio = _sourcePixelWidth > 0 && shownW > 0
            ? shownW / _sourcePixelWidth
            : _scale / SafeScaling;
        // 三项必须乘同一个 ratio：少一项基准线就偏，光晕与卡片之间出现的不是模糊而是实打实的缝。
        // 带烘焙阴影时外层窗体阴影基准线就是位图外矩形（直角）；无烘焙阴影时按卡片几何外扩
        var g = _hasBakedShadow ? default : _bakedCardGeometry;
        ShadowBorder.SetCardGeometry(g.ContentInsetPx * ratio,
            Math.Max(0, g.ContentRadiusPx) * ratio,
            Math.Max(0, g.StrokeWidthPx) * ratio);
    }

    /// <summary>
    /// 本贴图此刻该不该画窗体阴影（2026-10-01，issue #50）。
    ///
    /// <para>三档语义（<c>AppSettings.StickerShadowMode</c>）：<c>Off</c> 一律不画、<c>On</c> 一律画、
    /// <c>Auto</c>＝**跟随这张图截图时的「阴影」勾选意图反向**（<see cref="_captureShadowIntent"/>）：
    /// 截图未开阴影（无烘焙阴影）的贴图画窗体阴影以凸显边界；截图已开启阴影（已有烘焙阴影）的贴图不画窗体阴影避免叠层。
    /// 取不到勾选意图时（剪贴板 F3、截图历史、外来图、脚本贴图、文本/Markdown/颜色贴图）**回落为画**：
    /// 这些多是不透明内容，且"多一圈光晕"远比"看不清边界"轻。</para>
    ///
    /// <para>🔴 <b>Shift+Y 恒为最高优先级</b>：用户手动切过之后（<see cref="_shadowManualOverride"/>）
    /// 一律只听 <see cref="_shadowVisible"/>，档位不再参与。缺了这条，Auto 档下按 Shift+Y 会"按一下没反应"
    /// ——那正是 protection.md 点名禁止的"对某类贴图拒绝 Shift+Y"（2026-09-07 用户拍板，勿恢复）。</para>
    ///
    /// <para>⚠ 本方法只决定"画不画"，**不碰** <see cref="_hasBakedShadow"/> / <see cref="_hideAlphaCheckerboard"/>
    /// 与 <see cref="UpdateAlphaBackingLayer"/> 的三态——那是图片背衬那条独立的账，两者语义不许混。</para>
    /// </summary>
    private bool ShouldPaintWindowShadow()
    {
        // 手动切过：只认手动值（Shift+Y 必须永远有效）
        if (_shadowManualOverride) return _shadowVisible;

        string mode = AppSettings.NormalizeStickerShadowMode(AppSettings.Current.StickerShadowMode);
        if (mode == "Off") return false;
        if (mode == "On") return true;
        // Auto：截图开阴影（已有烘焙阴影）不画窗体阴影；截图关阴影（无烘焙阴影）画窗体阴影；无从得知则画
        return !(_captureShadowIntent ?? false);
    }

    /// <summary>用户是否用 Shift+Y 手动切过本贴图的阴影。一旦为 true，档位判据让位给
    /// <see cref="_shadowVisible"/>（见 <see cref="ShouldPaintWindowShadow"/>）。随存档保存恢复，
    /// 否则关窗再恢复会把用户的手动选择丢掉（2026-10-01 用户拍板"阴影状态要进存档"）。</summary>
    private bool _shadowManualOverride;

    /// <summary>
    /// 根据（阴影开关, 状态）重算窗体阴影。
    /// </summary>
    private void ApplyShadow()
    {
        if (_isCanvasMode) { ApplyCanvasShadow(); return; }
        // 图片背后那一层（棋盘格 / 什么都不露）与窗体阴影<b>同进退</b>：缩略图判据
        // （_isThumbnailMode）挂在这个入口就覆盖了进出缩略图／锁定／焦点／穿透／换图全部
        // 状态变化，不必在各点各补一行（与 UpdateShadowCornerRadius 只订阅 SizeChanged 单一入口
        // 同一道理）。必须排在下面的空判之前：这一层与 ShadowBorder 无关，不能被控件缺失连带跳过。
        UpdateAlphaBackingLayer();
        if (ShadowBorder == null) return;
        UpdateShadowCornerRadius();
        // ⚠ 自带烘焙阴影带不再关窗体阴影（2026-09-07 用户拍板）：窗体阴影是 UI 状态指示、
        // 烘焙阴影是图片素材，两种阴影并存。层次是"光晕 → 棋盘格背景板 → 图片"：带阴影带时
        // _bakedCardGeometry 归零，光晕基准就是棋盘格（位图）外沿，整个环都在位图范围之外，
        // 与图片零重叠，阴影层也因此在 z 序上留在内容层之下即可。
        if (_isThumbnailMode)
        {
            // 缩略图模式不显示阴影，改用加粗主题色描边标识
            ShadowBorder.IsShadowEnabled = false;
        }
        else
        {
            bool paintNormalShadow = ShouldPaintWindowShadow();
            // 在 Auto 模式下因截图开阴影而常态不画阴影的贴图，点击贴图（聚焦）时依然提供围绕高亮反馈
            bool isAutoShadowSuppressed = !_shadowManualOverride
                && AppSettings.NormalizeStickerShadowMode(AppSettings.Current.StickerShadowMode) == "Auto"
                && _captureShadowIntent == true;

            // 烘焙阴影贴图：高亮由 CardHighlightBorder 紧贴内容卡片（不含烘焙阴影）绘制，
            // 此时最外层 ShadowBorder 不在聚焦时开启，避免在 37px 阴影外侧再叠一层光晕
            if (_hasBakedShadow && _bakedShadowPadPx > 0)
            {
                // 仅当用户用 Shift+Y 手动开启常态阴影时，最外层才画常态阴影
                ShadowBorder.IsShadowEnabled = _shadowManualOverride && _shadowVisible;
                if (ShadowBorder.IsShadowEnabled)
                {
                    ShadowBorder.ShadowColor = _isFocused ? StickerStyling.FocusedShadowColor : StickerStyling.UnfocusedShadowColor;
                }
            }
            else if (_isMousePenetration)
            {
                ShadowBorder.IsShadowEnabled = paintNormalShadow || isAutoShadowSuppressed;
                if (ShadowBorder.IsShadowEnabled)
                {
                    ShadowBorder.ShadowColor = StickerStyling.PenetrationShadowColor;
                }
            }
            else if (_isLocked)
            {
                ShadowBorder.IsShadowEnabled = paintNormalShadow || isAutoShadowSuppressed;
                if (ShadowBorder.IsShadowEnabled)
                {
                    ShadowBorder.ShadowColor = StickerStyling.LockedShadowColor;
                }
            }
            else if (_isFocused)
            {
                // 普通贴图（无烘焙阴影）：点击贴图获得焦点，直接由 ShadowBorder 在贴图外沿高亮
                ShadowBorder.IsShadowEnabled = paintNormalShadow || isAutoShadowSuppressed;
                if (ShadowBorder.IsShadowEnabled)
                {
                    ShadowBorder.ShadowColor = StickerStyling.FocusedShadowColor;
                }
            }
            else
            {
                // 失焦常态：由 ShouldPaintWindowShadow 决定是否绘制灰黑色阴影
                ShadowBorder.IsShadowEnabled = paintNormalShadow;
                if (paintNormalShadow)
                {
                    ShadowBorder.ShadowColor = StickerStyling.UnfocusedShadowColor;
                }
            }
        }

        UpdateCardHighlightLayer();

        // 分层呈现：焦点/锁定/穿透/缩略图/换图的状态变化全部经过本入口，统一请一次重画
        //（呈现层据 PinEligible 决定接管还是交回 Avalonia）
        RequestPinSync();

        // 缩略图描边用独立覆盖层 ThumbIndicatorBorder：不占内容布局空间。
        // （此前用 RootBorder.BorderThickness=6，6px 描边会把内容区起点从 16 挤到 22 DIP，
        // 进入缩略图时图片整体偏右下 6×DPI 物理像素、退出反向偏——进入位置偏移的元凶之一。）
        // 描边颜色跟随状态：锁定橙、鼠标穿透绿（与阴影状态色一致），默认主题紫。
        if (ThumbIndicatorBorder != null)
        {
            ThumbIndicatorBorder.IsVisible = _isThumbnailMode;
            if (_isThumbnailMode)
            {
                string hex = _isLocked ? StickerStyling.LockedShadowColorHex
                           : _isMousePenetration ? StickerStyling.PenetrationShadowColorHex
                           : "#4219F2";
                ThumbIndicatorBorder.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(hex));
            }
        }
    }

    /// <summary>
    /// 更新贴图内容主体（不含烘焙阴影部分）的高亮层。
    /// 仅在带烘焙阴影且处于高亮状态（聚焦/锁定/穿透）时呈现。
    /// </summary>
    private void UpdateCardHighlightLayer()
    {
        if (CardHighlightBorder == null || StickerImage == null) return;
        bool shouldHighlight = _hasBakedShadow && _bakedShadowPadPx > 0
            && (_isFocused || _isLocked || _isMousePenetration) && !_isThumbnailMode;

        if (!shouldHighlight)
        {
            CardHighlightBorder.UpdateHighlight(false, default, 0, 0, Colors.Transparent);
            return;
        }

        double shownW = double.IsNaN(StickerImage.Width) || StickerImage.Width <= 0
            ? StickerImage.Bounds.Width : StickerImage.Width;
        double shownH = double.IsNaN(StickerImage.Height) || StickerImage.Height <= 0
            ? StickerImage.Bounds.Height : StickerImage.Height;
        if (shownW <= 0 || shownH <= 0 || _sourcePixelWidth <= 0) return;

        double ratio = shownW / _sourcePixelWidth;
        double padDip = _bakedShadowPadPx * ratio;
        double cardW = shownW - padDip * 2;
        double cardH = shownH - padDip * 2;
        if (cardW <= 0 || cardH <= 0) return;

        double rDip = _bakedCardGeometry.ContentRadiusPx * ratio;
        rDip = Math.Min(Math.Max(0, rDip), Math.Min(cardW, cardH) / 2.0);
        double strokeDip = _bakedCardGeometry.StrokeWidthPx * ratio;

        Avalonia.Media.Color hlColor = _isMousePenetration ? StickerStyling.PenetrationShadowColor
                      : _isLocked ? StickerStyling.LockedShadowColor
                      : StickerStyling.FocusedShadowColor;

        var im = StickerImage.Margin;
        Rect cardRect = new Rect(im.Left + _imageTransform.X + padDip, im.Top + _imageTransform.Y + padDip, cardW, cardH);
        CardHighlightBorder.UpdateHighlight(true, cardRect, rDip, strokeDip, hlColor);
    }

    // --- 缩略图拖角调整取景框尺寸 ---
    // 鼠标悬浮到缩略图内容区四角 → 显示斜向 resize 光标；按住拖拽以对角为固定点
    // 改变取景框尺寸——图片本身的渲染尺寸/缩放比/屏幕位置全程不变。
    //
    // 【零 resize 拖拽】拖角期间**完全不触碰原生窗口矩形**：
    //   每帧 SetWindowPos 改窗口尺寸后、合成器呈现新帧之前，DWM 会把上一帧内容
    //   **缩放贴合**到新窗口尺寸（连续 resize 的经典拉伸伪影）——即使图片位置
    //   已经冻结，视觉上仍处于"果冻拉扯"状态。
    //   方案：起拖时一次性把窗口放大到"冻结图片外扩一圈边距"（放大态），拖拽中
    //   只更新 RootBorder.Clip（内容裁剪）与 ThumbIndicatorBorder.Margin（描边位置）
    //   ——纯合成器场景更新，窗口矩形不动 → 零 DWM 缩放 → 图片逐像素静止；
    //   松手时一次性收缩回终态取景框。每个手势最多 2 次原子 resize（起拖/松手），
    //   且都在渲染帧内与内容同帧提交。

    private bool _isDraggingThumbCorner;
    private bool _thumbCornerRafScheduled;
    // 拖角调整过取景框后置位：ApplyScale 缩略图分支不得再把窗口打回 150 默认框
    // （否则拖角结束后任何 stray SizeChanged 都会让框弹回默认尺寸并伴随位置漂移）；
    // 重新进入缩略图时由 ToggleThumbnailMode 复位。
    private bool _thumbFrameCustomized;
    private WindowEdge _thumbCorner;
    // 冻结图片矩形（物理屏幕 px，拖拽全程不变）：取景框只在其内裁剪，图片纹丝不动
    private double _thumbImgL, _thumbImgT, _thumbImgR, _thumbImgB;
    // 抓取偏移（物理 px）：按下时鼠标相对被拖拽内容边缘的偏移，拖拽中边缘 = 鼠标 − 偏移（起拖无跳变）
    private double _thumbGrabX, _thumbGrabY;
    // 每轴最小内容边长（物理 px）：min(60, 图片边长) × DPI
    private double _thumbMinClientPxX, _thumbMinClientPxY;
    // 拖拽起始内容边缘（物理屏幕 px）：对角边缘全程锚死在这里
    private double _thumbStartCL, _thumbStartCT, _thumbStartCR, _thumbStartCB;
    // 当前取景框（物理屏幕 px）：每帧更新，松手时用于收缩窗口
    private double _thumbFrameL, _thumbFrameT, _thumbFrameR, _thumbFrameB;
    // 放大态窗口原点（物理 px）与是否已应用（未移动即松手时不放大也不收缩）
    private int _thumbWinX, _thumbWinY;
    private bool _thumbGrowApplied;
    private const double ThumbCornerGripSize = 18.0;   // 角部热区（DIP，内容区坐标）
    private const double ThumbClientMinSize = 60.0;    // 取景框最小边长（DIP）

    /// <summary>检测 pos（窗口坐标）是否落在缩略图内容区的某个角部热区内。</summary>
    private WindowEdge? GetThumbCornerAt(Avalonia.Point pos)
    {
        double x = pos.X - ShadowMargin;
        double y = pos.Y - ShadowMargin;
        double w = Bounds.Width - ShadowMargin * 2;
        double h = Bounds.Height - ShadowMargin * 2;
        if (w <= 0 || h <= 0) return null;

        bool left = x >= -2 && x < ThumbCornerGripSize;
        bool right = x <= w + 2 && x > w - ThumbCornerGripSize;
        bool top = y >= -2 && y < ThumbCornerGripSize;
        bool bottom = y <= h + 2 && y > h - ThumbCornerGripSize;

        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        return null;
    }

    /// <summary>角部光标样式。</summary>
    private static StandardCursorType ThumbCornerCursor(WindowEdge edge) => edge switch
    {
        WindowEdge.NorthWest or WindowEdge.SouthEast => StandardCursorType.TopLeftCorner,
        _ => StandardCursorType.TopRightCorner,
    };

    /// <summary>
    /// 开始拖角：冻结图片屏幕物理矩形（拖拽全程不变），记录起始内容边缘（对角锚点）
    /// 与抓取偏移。拖拽开始时先把视口规范化到合法域 [clientDim − imgDim, 0]
    /// （正常状态为恒等变换），保证"帧 ⊆ 图片"不变量成立——后续每帧的边缘钳制都依赖它。
    /// 窗口放大（放大态）延迟到首个渲染帧（ApplyThumbDragWindow）——未移动即松手时
    /// 完全不触碰窗口，零视觉扰动。
    /// </summary>
    private void StartThumbCornerDrag(WindowEdge edge, PointerPressedEventArgs e)
    {
        _isDraggingThumbCorner = true;
        _thumbFrameCustomized = true;
        _thumbCorner = edge;
        _thumbGrowApplied = false;

        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        // 起始窗口物理矩形（GetWindowRect，整数）与内容区（内缩阴影边距）
        int wl, wt, wr, wb;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            wl = rc.Left; wt = rc.Top; wr = rc.Right; wb = rc.Bottom;
        }
        else
        {
            wl = Position.X; wt = Position.Y;
            wr = Position.X + (int)(Width * s);
            wb = Position.Y + (int)(Height * s);
        }
        double cL = wl + mPx, cT = wt + mPx;
        double cR = wr - mPx, cB = wb - mPx;
        double cW = (cR - cL) / s, cH = (cB - cT) / s;   // 起始内容尺寸（DIP）

        double imgW = StickerImage.Width, imgH = StickerImage.Height;   // DIP，拖拽全程不变

        // 视口规范化（正常状态恒等；异常状态如缩略图内缩放过小帧越界时收敛一次）
        double vpX = imgW > cW ? Math.Clamp(_viewportX, cW - imgW, 0) : Math.Min(_viewportX, 0);
        double vpY = imgH > cH ? Math.Clamp(_viewportY, cH - imgH, 0) : Math.Min(_viewportY, 0);
        if (vpX != _viewportX || vpY != _viewportY)
        {
            _viewportX = vpX;
            _viewportY = vpY;
            UpdateTransforms();
        }

        // 冻结图片物理屏幕矩形：拖拽全程纹丝不动，取景框只在其内裁剪
        _thumbImgL = cL + vpX * s;
        _thumbImgT = cT + vpY * s;
        _thumbImgR = _thumbImgL + imgW * s;
        _thumbImgB = _thumbImgT + imgH * s;

        // 起始内容边缘 = 对角锚点（物理屏幕 px，全程不动）；初始取景框 = 起始内容区
        _thumbStartCL = cL; _thumbStartCT = cT;
        _thumbStartCR = cR; _thumbStartCB = cB;
        _thumbFrameL = cL; _thumbFrameT = cT;
        _thumbFrameR = cR; _thumbFrameB = cB;

        // 每轴最小内容边长（物理 px）
        _thumbMinClientPxX = Math.Min(ThumbClientMinSize, imgW) * s;
        _thumbMinClientPxY = Math.Min(ThumbClientMinSize, imgH) * s;

        // 抓取偏移：拖拽中"边缘 = 鼠标 − 偏移"，起始帧无跳变
        if (ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var pt))
        {
            bool west = edge is WindowEdge.NorthWest or WindowEdge.SouthWest;
            bool north = edge is WindowEdge.NorthWest or WindowEdge.NorthEast;
            _thumbGrabX = pt.X - (west ? cL : cR);
            _thumbGrabY = pt.Y - (north ? cT : cB);
        }
        else
        {
            _thumbGrabX = 0;
            _thumbGrabY = 0;
        }

        e.Pointer.Capture(this);
        e.Handled = true;

        ThumbDiag($"拖角开始：edge={edge} DPI={s} 起始框Px({cL:F1},{cT:F1})-({cR:F1},{cB:F1}) " +
                  $"冻结图片Px({_thumbImgL:F1},{_thumbImgT:F1})-({_thumbImgR:F1},{_thumbImgB:F1}) 视口=({vpX:F2},{vpY:F2})");
    }

    /// <summary>调度渲染帧回调：首个回调先应用放大态窗口，之后每帧只更新取景框视觉。</summary>
    private void ScheduleThumbCornerRaf()
    {
        if (_thumbCornerRafScheduled) return;
        _thumbCornerRafScheduled = true;
        RequestAnimationFrame(_ =>
        {
            _thumbCornerRafScheduled = false;
            if (!_isDraggingThumbCorner) return;
            if (!_thumbGrowApplied) ApplyThumbDragWindow();
            UpdateThumbFrame();
        });
    }

    /// <summary>
    /// 一次性应用"放大态"窗口：矩形 = 冻结图片外扩一圈阴影边距（四周多留 2 DIP
    /// 舍入余量，保证任意取景框 + 边距完整落在 RootBorder 内）。此后拖拽全程不再
    /// 触碰窗口矩形。视口按冻结矩形反解——图片逐物理像素钉死在原位。
    /// 在渲染帧回调内执行，与后续场景更新同帧提交（放大瞬间的 DWM 拉伸窗口最小化）。
    /// </summary>
    private void ApplyThumbDragWindow()
    {
        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        int xPx = (int)Math.Floor(_thumbImgL - mPx);
        int yPx = (int)Math.Floor(_thumbImgT - mPx);
        int wPx = Math.Max(1, SnapStablePx((_thumbImgR - _thumbImgL) / s + ShadowMargin * 2 + 2, s));
        int hPx = Math.Max(1, SnapStablePx((_thumbImgB - _thumbImgT) / s + ShadowMargin * 2 + 2, s));

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            // SWP_NOZORDER(0x0004) | SWP_NOACTIVATE(0x0010)：单次原子移动+缩放
            ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
        }
        ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);

        // 视口反解：图片屏幕左上角恒等于冻结矩形 (_thumbImgL, _thumbImgT)
        _viewportX = (_thumbImgL - (xPx + mPx)) / s;
        _viewportY = (_thumbImgT - (yPx + mPx)) / s;
        UpdateTransforms();

        _thumbWinX = xPx;
        _thumbWinY = yPx;
        _thumbGrowApplied = true;

        // 放大态下描边改由 Margin 定位（不再拉伸铺满整个窗口）
        ThumbIndicatorBorder.Margin = new Thickness(0);

        ThumbDiag($"拖角放大态：窗口({xPx},{yPx}) {wPx}x{hPx}，视口=({_viewportX:F2},{_viewportY:F2})");
    }

    /// <summary>
    /// 拖角中（每渲染帧）：图片屏幕物理矩形全程冻结（StartThumbCornerDrag 捕获），
    /// 取景框被拖拽边缘跟随鼠标、对角边缘锚死，且始终钳制在冻结图片矩形之内——
    /// 拖到图片边缘即止，不露白、图片永不滑动。
    /// 【关键】本方法只做纯视觉更新（RootBorder.Clip 裁剪 + 描边 Margin 定位），
    /// 完全不触碰窗口矩形/托管尺寸/视口——窗口无 resize 即无 DWM 拉伸伪影，
    /// 图片逐物理像素纹丝不动。全部量从拖拽起点常量计算，无逐帧累加。
    /// </summary>
    private void UpdateThumbFrame()
    {
        if (!ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var pt)) return;
        double s = SafeScaling;

        bool west = _thumbCorner is WindowEdge.NorthWest or WindowEdge.SouthWest;
        bool north = _thumbCorner is WindowEdge.NorthWest or WindowEdge.NorthEast;

        // --- X 轴：取景框 [cL, cR]（物理 px）。被拖拽边缘跟随鼠标，钳制在冻结图片内 ---
        double cL, cR;
        if (west)
        {
            cR = _thumbStartCR;                            // 对角（右）内容边缘锚死
            double hi = cR - _thumbMinClientPxX;           // 最小边长限制
            double lo = Math.Min(_thumbImgL, hi);          // 图片左缘（含退化保护）
            cL = Math.Clamp(pt.X - _thumbGrabX, lo, hi);
        }
        else
        {
            cL = _thumbStartCL;                            // 对角（左）内容边缘锚死
            double lo = cL + _thumbMinClientPxX;           // 最小边长限制
            double hi = Math.Max(_thumbImgR, lo);          // 图片右缘（含退化保护）
            cR = Math.Clamp(pt.X - _thumbGrabX, lo, hi);
        }

        // --- Y 轴同构 ---
        double cT, cB;
        if (north)
        {
            cB = _thumbStartCB;
            double hi = cB - _thumbMinClientPxY;
            double lo = Math.Min(_thumbImgT, hi);
            cT = Math.Clamp(pt.Y - _thumbGrabY, lo, hi);
        }
        else
        {
            cT = _thumbStartCT;
            double lo = cT + _thumbMinClientPxY;
            double hi = Math.Max(_thumbImgB, lo);
            cB = Math.Clamp(pt.Y - _thumbGrabY, lo, hi);
        }

        _thumbFrameL = cL; _thumbFrameT = cT;
        _thumbFrameR = cR; _thumbFrameB = cB;

        // 纯视觉更新：窗口 DIP 坐标系下的取景框矩形
        double fx = (cL - _thumbWinX) / s;
        double fy = (cT - _thumbWinY) / s;
        double fw = (cR - cL) / s;
        double fh = (cB - cT) / s;

        // RootBorder 本地原点 = 窗口 DIP (ShadowMargin, ShadowMargin)（RootGrid Margin）
        if (RootBorder != null)
            RootBorder.Clip = new RectangleGeometry(new Rect(
                fx - ShadowMargin, fy - ShadowMargin, fw, fh));

        // 描边（RootGrid 子元素，本地原点同 RootBorder）：Margin 定位到取景框
        if (ThumbIndicatorBorder != null)
        {
            double W = Bounds.Width, H = Bounds.Height;
            ThumbIndicatorBorder.Margin = new Thickness(
                fx - ShadowMargin, fy - ShadowMargin,
                (W - ShadowMargin) - (fx + fw), (H - ShadowMargin) - (fy + fh));
        }
    }

    /// <summary>
    /// 结束拖角：一次性收缩窗口回终态取景框（+ 边距），视口按冻结矩形反解——
    /// 收缩后图片仍钉在拖拽前的原位。还原视觉态：内容裁剪交回窗口边界
    /// （RootBorder.Clip 清空），描边回到拉伸铺满（Margin 清零）。
    /// 未放大即松手（按下未移动）时不做任何事——零视觉扰动。
    /// </summary>
    private void EndThumbCornerDrag(PointerEventArgs e)
    {
        e.Pointer.Capture(null);
        e.Handled = true;
        Cursor = new Cursor(StandardCursorType.Arrow);

        if (_thumbGrowApplied)
        {
            double s = SafeScaling;
            double mPx = ShadowMargin * s;

            int wPx = Math.Max(1, SnapStablePx((_thumbFrameR - _thumbFrameL) / s + ShadowMargin * 2, s));
            int hPx = Math.Max(1, SnapStablePx((_thumbFrameB - _thumbFrameT) / s + ShadowMargin * 2, s));
            int xPx = (int)Math.Round(_thumbFrameL - mPx);
            int yPx = (int)Math.Round(_thumbFrameT - mPx);
            int beforeX = xPx, beforeY = yPx;
            var imageRect = new Avalonia.Rect(_thumbImgL - xPx, _thumbImgT - yPx,
                _thumbImgR - _thumbImgL, _thumbImgB - _thumbImgT);
            ClampVisibleContentPosition(ref xPx, ref yPx,
                GetVisibleContentInWindow(wPx, hPx, s, imageRect), s, GetStickerDisplayRects());
            _thumbImgL += xPx - beforeX; _thumbImgR += xPx - beforeX;
            _thumbImgT += yPx - beforeY; _thumbImgB += yPx - beforeY;

            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                // SWP_NOZORDER(0x0004) | SWP_NOACTIVATE(0x0010)：单次原子移动+缩放
                ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
            }
            ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);

            // 视口反解：收缩后图片仍钉在冻结矩形（不动的最终校验依据）
            _viewportX = (_thumbImgL - (xPx + mPx)) / s;
            _viewportY = (_thumbImgT - (yPx + mPx)) / s;
            UpdateTransforms();

            // 还原视觉态：裁剪交回窗口边界（窗口内容区 = 终态取景框），描边铺满
            if (RootBorder != null)
                RootBorder.Clip = null;
            if (ThumbIndicatorBorder != null)
                ThumbIndicatorBorder.Margin = new Thickness(0);

            if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcEnd))
            {
                double imgSX = rcEnd.Left + mPx + _viewportX * s;
                double imgSY = rcEnd.Top + mPx + _viewportY * s;
                ThumbDiag($"拖角结束：窗口({rcEnd.Left},{rcEnd.Top}) {rcEnd.Width}x{rcEnd.Height}，" +
                          $"取景框=({_thumbFrameR - _thumbFrameL:F0}x{_thumbFrameB - _thumbFrameT:F0})px，" +
                          $"图片冻结偏差=({imgSX - _thumbImgL:F2},{imgSY - _thumbImgT:F2})px（应≈0），" +
                          $"视口=({_viewportX:F2},{_viewportY:F2})");
            }
        }

        _isDraggingThumbCorner = false;
        SaveArchiveSnapshot();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 句柄已创建：DPI 从此以框架值为准（SafeScaling 的预置值分支到此结束）
        _handleReady = true;

        // 句柄此刻已创建：先抬升窗口的最大可落定尺寸（必须早于任何可能超屏的 resize，
        // 也必须早于信封命中穿透子类化——两条 GWL_WNDPROC 链按后进先出还原）
        InstallMaxTrackSizeLift();

        // 去信封 P0 取证（2026-09-24）：本窗实际落到哪档合成模式——决定 resize"整窗消失再出现"
        // 能否从根上消。WS_EX_NOREDIRECTIONBITMAP(0x00200000) 置位＝WinUIComposition/DirectComposition
        // （无重定向位图，DWM 拿旧帧拉伸的伪影物理上不可能）；未置位＝RedirectionSurface 回退档
        // （resize 会拉伸旧帧/整窗重建⇒闪，强制走 WinUI 档很可能根治）。只读样式位、不改任何行为。
        try
        {
            IntPtr hProbe = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hProbe != IntPtr.Zero)
            {
                long exStyle = ScreenCaptureTool.Platform.NativeMethods.GetWindowLongPtr(
                    hProbe, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE).ToInt64();
                bool noRedir = (exStyle & 0x00200000) != 0;
                AppLogger.Info("[合成模式取证] GWL_EXSTYLE=0x" + exStyle.ToString("X")
                               + " NOREDIRECTIONBITMAP=" + (noRedir ? "是(WinUI/DirectComposition)" : "否(RedirectionSurface回退档)")
                               + " ⇒ resize闪" + (noRedir ? "属表面重分配(框架结构性)" : "或可强制WinUI档根治"));
            }
        }
        catch { }

        // 加载贴图默认设置
        var settings = AppSettings.Load();
        // ⚠ 手动覆盖位已置（存档回灌把用户 Shift+Y 的选择带回来了）时**不许覆写** `_shadowVisible`
        // ——否则恢复出来的贴图阴影一律跳回全局默认，用户的手动选择当场丢失（2026-10-01，issue #50）。
        // 非回灌路径（新建贴图）该位恒 false，行为与改版前逐位相同。
        if (!_shadowManualOverride) _shadowVisible = settings.StickerDefaultShadow;
        SetTextSelectable(settings.StickerTextSelectable);

        // 打开后窗口句柄已创建，DesktopScaling 可靠；按截图原始物理像素尺寸还原贴图，
        // 使其与屏幕上原截图区域完全重合（贴在原位置、保持同样大小）。
        if (!_initialSizeApplied)
        {
            _initialSizeApplied = true;
            // 超大图（如长截图）物理尺寸可能超过 GPU 纹理上限，首次显示前限制缩放比例
            double cappedScale = Math.Min(_scale, GetMaxScale());
            // 调用方在 Show 前就交进了正确 DPI（截图贴图通路）时，构造函数那次 ApplyScale 已把窗口尺寸、
            // 内容几何与阴影边距全算对，这里再跑一遍只是请求一次同值 resize 并重设 SizeToContent——
            // 这一来一回就是 issue #12 的"贴图冒出来又弹一下"。预置值缺失或与实测 DPI 不符
            // （窗口落到另一块缩放率不同的外接屏）时照旧补一次。
            bool dpiPreSet = _preShowScaling > 0 && DesktopScaling > 0
                && Math.Abs(DesktopScaling - _preShowScaling) <= 0.0001;
            if (!dpiPreSet || cappedScale != _scale)
            {
                _scale = cappedScale;
                ApplyScale();
            }
        }
        // 新建即超屏的贴图（长截图、超大图）在此收口进工作区（2026-09-22 阶段 4）。
        // 刻意**不改 ApplyScale 的 Manual 分支**：那条公式被构造 / Opened / SizeChanged / 换图 /
        // 存档恢复 / 缩略图等 7 条通路共用，在里面塞夹制会牵动全部创建时序。这里是"窗口已开、
        // 句柄与 DPI 均可靠、几何已定"的单一时点，收口一次即可，其余通路一行不动。
        ClampOversizedWindowIntoWorkArea();
        // 普通大小的贴图也可能由脚本或旧存档定位在屏间空洞，只修正最小可见区域。
        _lastAppliedDragPos = new PixelPoint(int.MinValue, int.MinValue);
        ApplyDragMove(GetWindowOriginPx());

        UpdateHitRegion();

        ApplyShadow();
        UpdateContextMenuState();

        // 解除 IME 关联：中文输入法激活时字母键不再被组字拦截，
        // Space/Y/Ctrl+L/Ctrl+C 等贴图快捷键 100% 响应。与截图侧同一处理：文字标注编辑时 TextBox
        // 获得焦点，Avalonia 会自动重建 IME 关联，中文输入不受影响（CaptureWindow.OnWindowOpened 同注释）。
        ScreenCaptureTool.Platform.WindowHelper.DisableIme(
            TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        // 贴图是长期驻留窗口，必须从 Alt+Tab 列表里摘掉，否则贴图越多、切窗口越难
        HideFromAltTab();

        // 图片贴图打开即在后台静默预热 OCR（无提示），Alt 快速选词零等待
        PreWarmStickerOcr();

        // 分层呈现（StickerStyling.LayeredPinSurface）：几何此刻已定稿，挂订阅并提交首帧
        InitLayeredPinSurface();
        BringStickerToFront();

        Avalonia.Threading.Dispatcher.UIThread.Post(() => SaveArchiveSnapshot(), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 新建即超出工作区的贴图（长截图、超大图）：把窗口夹进工作区，并切入"平移常驻态"
    /// （视觉覆写 + 各层 Margin 承载偏移），内容一个像素不丢、靠拖动平移查看。
    ///
    /// 为什么必须做（实测，见 docs/贴图视口平移方案.md §2）：绘制成本随**窗口表面面积**
    /// 线性增长约 0.6ms/MPx。长图贴图按"窗口＝内容+阴影"建出来是 1232×12032＝14.8MPx
    /// ＝7.4 个屏幕，每帧画整张表面、而屏幕上只看得见 2MPx，正是用户报的"长截图贴图卡"。
    /// 夹进工作区后表面恒 ≤ 一屏（~1.2ms/帧），且 Topmost 巨幅窗口与任务栏的 z 序争抢一并消失。
    ///
    /// 内容位置取"左上对齐"而非居中：长图/长截图的阅读起点在顶部，居中会让用户一打开就看到
    /// 中段、还得往上拖找开头。左上对齐即 offset=(0,0)，与改版前"窗口贴合内容、内容填满窗口"
    /// 在视觉起点上一致。
    /// 幂等：窗口本就装得下时零操作（绝大多数贴图走这条，创建路径一行不多跑）。
    /// </summary>
    private void ClampOversizedWindowIntoWorkArea()
    {
        if (_isClosed || _isThumbnailMode) return;
        if (_wheelVisualOverride) return;          // 已在覆写态（存档恢复等）：几何归那套簿记所有
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (!ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc)) return;

        var displayRects = GetStickerDisplayRects();
        var wa = WorkAreaBounds(displayRects);
        if (wa.Width <= 0 || wa.Height <= 0) return;
        if (rc.Width <= wa.Width && rc.Height <= wa.Height) return;   // 装得下：零操作
        if (UsesImageViewport)
        {
            double scale = SafeScaling, margin = ShadowMargin * scale;
            ActivateWheelEnvelope(rc.Left + margin, rc.Top + margin, rc.Width - 2 * margin, rc.Height - 2 * margin);
            _wheelImgLpx = rc.Left + margin; _wheelImgTpx = rc.Top + margin;
            _wheelImgWpx = StickerImage.Width * scale; _wheelImgHpx = StickerImage.Height * scale;
            ApplyFittedWindowRectCore(displayRects);
            _wheelGestureActive = false;
            return;
        }

        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        // 目标窗口矩形：尺寸夹进工作区，位置贴工作区左上（长图阅读起点在顶部）
        int wPx = Math.Max(1, SnapStablePhysical(Math.Min(rc.Width, wa.Width), s));
        int hPx = Math.Max(1, SnapStablePhysical(Math.Min(rc.Height, wa.Height), s));
        int xPx = rc.Left, yPx = rc.Top;
        ClampVisibleContentPosition(ref xPx, ref yPx,
            GetVisibleContentInWindow(wPx, hPx, s), s, GetStickerDisplayRects());
        if (wPx >= rc.Width && hPx >= rc.Height) return;

        // 内容当前屏幕矩形（此刻窗口＝内容+阴影，故内容原点＝窗口原点+阴影边距）
        double imgW = StickerImage.Width * s, imgH = StickerImage.Height * s;
        if (imgW <= 0 || imgH <= 0) return;

        WheelDiag($"新建超屏收口：窗口({rc.Width}x{rc.Height} = {rc.Width * (long)rc.Height / 1e6:F1}MPx)"
                  + $"→({xPx},{yPx}){wPx}x{hPx} 工作区({wa.Width}x{wa.Height}) "
                  + $"内容({imgW:F0}x{imgH:F0}) 保留全部像素，越出部分拖动平移查看");

        _isApplyingScale = true;
        try
        {
            // 先激活覆写态（视口簿记初始＝目标视口），再落窗口矩形，最后按左上对齐反解 Margin。
            // 顺序理由与滚轮同：簿记必须先就位，SetWheelGestureVisual 才有正确的视口原点可减。
            ActivateWheelEnvelope(xPx + mPx, yPx + mPx, wPx - mPx * 2, hPx - mPx * 2);
            _wheelImgWpx = imgW; _wheelImgHpx = imgH;
            _wheelImgLpx = xPx + mPx; _wheelImgTpx = yPx + mPx;   // 左上对齐＝offset(0,0)

            // SWP_NOZORDER(0x0004)|SWP_NOACTIVATE(0x0010)：单次原子改尺寸+位置
            ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
            if (ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcSet))
            {
                // 落地校验（与 ApplyFittedWindowRect 同口径）：被系统改写就按真实值重记簿记，
                // 否则 Margin 会按请求值反解、内容整体坐偏。
                xPx = rcSet.Left; yPx = rcSet.Top; wPx = rcSet.Width; hPx = rcSet.Height;
                _wheelEnvL = xPx + mPx; _wheelEnvT = yPx + mPx;
                _wheelEnvW = wPx - mPx * 2; _wheelEnvH = hPx - mPx * 2;
                _wheelImgLpx = _wheelEnvL; _wheelImgTpx = _wheelEnvT;
            }
            ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);
            SetWheelGestureVisual(0, 0, StickerImage.Width, StickerImage.Height);
            // 振荡采证：用户报"贴图的瞬间就跳过去了"，创建期这条通路必须也在账上
            TrackWindowPositionForOscillation(xPx, yPx, "新建超屏收口");
        }
        finally { _isApplyingScale = false; }
    }

    /// <summary>
    /// 把贴图窗口标成 Win32 工具窗口，使它不再出现在 Alt+Tab 任务切换列表里。
    /// Avalonia 的 ShowInTaskbar=False 只做到"挂到隐藏父窗 + 清 WS_EX_APPWINDOW"，
    /// 从不设 WS_EX_TOOLWINDOW；而系统的 Alt+Tab 枚举最后一步恰恰只看 WS_EX_TOOLWINDOW
    /// （隐藏父窗不算"可见的所有者"，贴图因此照样进列表）。与 AiPromptInputWindow 同一处理。
    /// </summary>
    private void HideFromAltTab()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            long style = ScreenCaptureTool.Platform.NativeMethods.GetWindowLongPtr(
                hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE).ToInt64();
            ScreenCaptureTool.Platform.NativeMethods.SetWindowLongPtr(
                hwnd,
                ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE,
                new IntPtr(style | ScreenCaptureTool.Platform.NativeMethods.WS_EX_TOOLWINDOW));
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow.HideFromAltTab failed: ", ex);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isCanvasMode) { EndCanvasPan(); UpdateCanvasResizeHints(false); }
        // 失焦同样是"这段步进结束"：先结清历史，再走下面那些可能提前 return 的分支
        FlushAnnotationNudgeSession();

        // 划词浮条跟着失焦收起（下面 ClearTextStickerSelection 那类通路各自会清快照，
        // 这里只保证"焦点已不在本贴图"时不再有一张可点的浮条留在原地）。
        HideSelectionToolbar();
        // 🔴 翻译标记是**持久**的：失活只收气泡，紫线与紫点一律留着（用户切去别处再回来，
        // 译好的那几段还在原地可读）。气泡要收是因为焦点一走指针必然已离开，留一张半透明浮层
        // 盖在原文上只会挡字；回到本贴图重新悬浮紫点即再展开。
        if (_bubbleShown) HideTranslateBubble();

        // 拖动期间系统可能短暂 Deactivate 本窗口：此时不提交收缩（拖动中收缩会让
        // 窗口跳回拖动轨迹之外）、也不清除焦点；信封由拖动结束后的下一次交互收尾
        if (_isDragging)
        {
            RootBorder.ContextMenu?.Close();
            return;
        }

        // 拖边缩放：失活即收口（松手事件可能因焦点被夺而收不到），再提交信封
        if (_isResizing) EndEdgeResize();

        // 滚轮信封手势：失活（如 Alt+Tab 切走）即提交收缩，避免信封态跨窗口失焦存留
        if (_wheelVisualOverride) CommitWheelGesture("窗口失活");

        // Close context menu when deactivated
        RootBorder.ContextMenu?.Close();

        // 标注模式：失活原因可能是"点了本程序的标注浮窗"（同进程窗口同样会抢走前台），
        // 照旧 ClearFocus 会让 NotifyUnfocused 立刻退出标注并把刚点的那一下吞掉，
        // 观感就是"工具栏根本点不了，一点就说退出标注"。故推迟一拍读真实前台窗再判归属。
        if (_isAnnotating)
        {
            Dispatcher.UIThread.Post(CheckAnnotationOwnership, DispatcherPriority.Background);
            return;
        }

        StickerFocusManager.ClearFocus(this);
    }

    /// <summary>
    /// 失活后复核：前台窗仍属本进程（标注浮窗、取色窗、提示窗…）→ 保持标注与焦点态；
    /// 已切到别的应用 → 按原设计清除焦点（进而在 NotifyUnfocused 里退出标注）。
    /// </summary>
    private void CheckAnnotationOwnership()
    {
        if (!_isAnnotating) return;
        if (IsActive) return; // 复核前又回到本贴图（如浮窗抢了激活即刻归还）

        IntPtr fg = ScreenCaptureTool.Platform.NativeMethods.GetForegroundWindow();
        uint fgPid = 0;
        if (fg != IntPtr.Zero)
            ScreenCaptureTool.Platform.NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
        bool sameProcess = fg != IntPtr.Zero && fgPid == (uint)Environment.ProcessId;

        AnnoDiag($"失活复核 fg={fg} pid={fgPid} 同进程={sameProcess} → {(sameProcess ? "保持标注" : "退出标注")}");
        if (sameProcess) return;

        StickerFocusManager.ClearFocus(this);
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        UpdateAnnotationPalettePosition();
        UpdateHitRegion();
    }

    /// <summary>当前 DPI 缩放的容错版本。<see cref="DesktopScaling"/> 在窗口句柄未创建时不可信
    /// （可能是 0，也可能是"当前那块屏"而不是贴图将要出现的那块屏），故 Show 之前一律用调用方
    /// 预置的 <see cref="_preShowScaling"/>，让首帧几何一次到位；Opened 之后照旧以框架值为准。</summary>
    private double SafeScaling
    {
        get
        {
            if (!_handleReady && _preShowScaling > 0) return _preShowScaling;
            return DesktopScaling > 0 ? DesktopScaling : 1.0;
        }
    }

    /// <summary>窗口句柄是否已创建（<c>Opened</c> 之后为 true）——即 <see cref="DesktopScaling"/> 是否开始可信。</summary>
    private bool _handleReady;

    /// <summary>RootGrid 的外边距（各方向像素），为阴影预留渲染空间。</summary>
    private const double ShadowMargin = 16;

    // --- 窗口物理像素"往返稳定"量化（所有窗口尺寸写入的唯一换算入口） ---
    // 背景：Avalonia Win32 对窗口尺寸有三条写入链路，换算语义并不一致：
    //   ① 我们的原生 SetWindowPos（整数物理像素）；
    //   ② WM_SIZE → Window.HandleResized 把 ClientSize（物理 ÷ RenderScaling）写回
    //      Width/Height，并同步 ExecuteLayoutPass；
    //   ③ 布局 WindowBase.ArrangeCore → Window.ArrangeSetBounds → PlatformImpl.Resize，
    //      其内部用 (int)(dip × RenderScaling) **截断**换算并经 SetWindowPlacement 落地。
    // 若窗口物理宽 n 不满足 (int)((n / s) × s) == n（double 除乘往返差 1 个 ulp，截断后
    // 少 1px；125%/150% DPI 下约一半的像素值如此，100%/200% 恒稳定），链路③每次布局都
    // 会把窗口再缩 1px，与链路①逐帧互相回打——这就是"拖角持续晃动"与"进入缩略图位置/
    // 尺寸被扰动"的共同根源。此前 Round 量化的假设"round/trunc 均不变"在非 2 的幂 DPI
    // 下不成立。吸附到稳定值后，三条链路换算结果逐像素一致，Resize 恒为空操作。
    /// <summary>把 DIP 尺寸换算为满足 (int)((n/s)×s)==n 的稳定物理像素整数（向小吸附，最多回退数像素）。</summary>
    private static int SnapStablePx(double dip, double s)
    {
        int n = (int)Math.Round(dip * s);
        for (int guard = 0; n > 1 && (int)((n / s) * s) != n && guard < 8; guard++)
            n--;
        return n;
    }

    // 普通贴图与画布共用：缩放坐标时保持指定锚点不动，每轴倍率允许包含像素取整差。
    private static double ScaleCoordinateAroundAnchor(double coordinate, double anchor, double ratio)
        => ratio * coordinate + (1 - ratio) * anchor;

    /// <summary>把物理像素整数吸附到"往返稳定"网格（(int)((n/s)×s)==n），只减不增。</summary>
    private static int SnapStablePhysical(int n, double s)
    {
        for (int guard = 0; n > 1 && (int)((n / s) * s) != n && guard < 8; guard++)
            n--;
        return n;
    }

    // =====================================================================
    // 工作区读数的"取大稳定化"（2026-10-01，issue #52 跟进：靠边缘跳位/来回跳十几次）
    // =====================================================================
    // 🔴 `MonitorHelper.GetWorkAreaUnion()` 每次都真枚举显示器，**读数会在同一次手势内瞬时变小**
    //    （本文件既有实测记录：同一手势内 3840x2088 → 2796x1218）。成因是 Shell 在任务栏/Appbar
    //    自动隐藏、全屏应用进出、显示器热插拔的瞬间会把 rcWork 报成中间值。
    // 它被 `ClampRectToWorkArea` 当作钳制边界用，于是"边界本身在抖"⇒ 同一个贴图位置每帧被夹到
    //    不同的地方＝用户报的「贴图瞬间跳一次、有时来回跳十几次」。
    // 这里只做一件事：**同一个并集在短窗口内取历史最大**（并集只会因为"少枚举到一块屏/Appbar 报大"
    //    而偏小，取大即过滤掉这类瞬时塌缩）。两条纪律：
    //   ① **取大而不是取最后一次**：偏大的读数最多让窗口暂时允许多出去一点（下一次钳制自然收回），
    //      偏小的读数会**主动把窗口搬走**——两种错的代价不对称，故一律偏向"不搬"。
    //   ② **有效期必须短**（`WorkAreaHoldMs`）：真实的分辨率/显示器变更必须能被认到，否则拔掉副屏后
    //      贴图会被按旧并集留在不存在的屏上。300ms 足以跨过一次手势内的抖动，又远短于人操作显示设置
    //      的时间尺度。
    // ⚠ 这里**不缓存"最后一次读数"**、也不做跨进程共享：只在越变越小时压住，其余一律透传。
    private const int WorkAreaHoldMs = 300;
    private static System.Drawing.Rectangle _waHold;
    private static long _waHoldStamp;
    private static IReadOnlyList<System.Drawing.Rectangle> _waHoldRects = Array.Empty<System.Drawing.Rectangle>();

    /// <summary>工作区并集的稳定读数（短窗口内取历史最大，过滤 Shell 的瞬时塌缩）。
    /// 一切**会据此搬动窗口**的钳制都必须走它；只用于"判断装不装得下"的地方走原始读数也无妨，
    /// 但统一走这里可免去两个口径打架（本文件既有教训：同一件几何两处算法必须同源）。</summary>
    private static System.Drawing.Rectangle GetStableWorkAreaUnion()
    {
        return WorkAreaBounds(GetStableWorkAreaRects());
    }

    private static System.Drawing.Rectangle WorkAreaBounds(IReadOnlyList<System.Drawing.Rectangle> rects)
    {
        var bounds = System.Drawing.Rectangle.Empty;
        foreach (var rect in rects)
            bounds = bounds.IsEmpty ? rect : System.Drawing.Rectangle.Union(bounds, rect);
        return bounds;
    }

    /// <summary>保留各屏工作区，短期塌缩时取旧、新矩形集合；不能把屏间空洞当作可见屏幕。</summary>
    private static IReadOnlyList<System.Drawing.Rectangle> GetStableWorkAreaRects()
    {
        var rects = ScreenCaptureTool.Platform.MonitorHelper.GetWorkAreaRects();
        var wa = WorkAreaBounds(rects);
        if (wa.Width <= 0 || wa.Height <= 0) return rects;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool fresh = _waHold.Width > 0 && _waHold.Height > 0
                     && System.Diagnostics.Stopwatch.GetElapsedTime(_waHoldStamp, now).TotalMilliseconds <= WorkAreaHoldMs;
        if (fresh && (wa.Width < _waHold.Width || wa.Height < _waHold.Height))
        {
            // 比持有值小＝疑似瞬时塌缩：取并集包围盒的"取大"版本，不更新时间戳
            // （否则连续的小读数会不断续期、把真正的变更也一直压着）
            return _waHoldRects.Concat(rects).ToArray();
        }
        _waHold = wa;
        _waHoldStamp = now;
        _waHoldRects = rects;
        return rects;
    }

    // 普通图片与长图使用同一份屏幕坐标。工作区只适用于文本排版/显式适配，不是图片的显示裁切边界。
    private bool UsesImageViewport => _bitmap != null && !_isThumbnailMode && !_isCanvasMode
        && StickerImage is { IsVisible: true, Source: Avalonia.Media.Imaging.Bitmap };

    private IReadOnlyList<System.Drawing.Rectangle> GetStickerDisplayRects()
    {
        if (!UsesImageViewport) return GetStableWorkAreaRects();
        var screens = Screens.All;
        if (screens.Count == 0) return ScreenCaptureTool.Platform.MonitorHelper.GetMonitorRects();
        return screens.Select(screen => new System.Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y,
            screen.Bounds.Width, screen.Bounds.Height)).ToArray();
    }

    /// <summary>图片整体只受最小可见内容限制。窗口裁切是输出，不能反过来改图片位置。</summary>
    private Avalonia.Vector GetImagePlacementShift(double left, double top, double width, double height,
        double s, IReadOnlyList<System.Drawing.Rectangle> screens)
    {
        var content = GetVisibleImageBounds(new Avalonia.Rect(0, 0, width, height));
        if (content.Width <= 0 || content.Height <= 0) content = new Avalonia.Rect(0, 0, width, height);
        int x = (int)Math.Round(left), y = (int)Math.Round(top), oldX = x, oldY = y;
        ClampVisibleContentPosition(ref x, ref y, content, s, screens);
        return new Avalonia.Vector(x - (double)oldX, y - (double)oldY);
    }

    private Avalonia.Rect GetVisibleImageBounds(Avalonia.Rect image)
    {
        if (StickerImage.Source is not Avalonia.Media.Imaging.Bitmap source) return image;
        var pixels = GetVisibleSourceBounds(source);
        if (pixels.Width <= 0 || pixels.Height <= 0) return default;
        double sx = image.Width / source.PixelSize.Width, sy = image.Height / source.PixelSize.Height;
        return new Avalonia.Rect(image.X + pixels.X * sx, image.Y + pixels.Y * sy, pixels.Width * sx, pixels.Height * sy);
    }

    /// <summary>统一图片→窗口的单轴投影：装得下保持贴合，超屏只生成屏内可见表面，含任务栏。</summary>
    private static void FitImageViewportAxis(double imageStart, double imageSize, double screenStart,
        double screenSize, double margin, out double viewportStart, out double viewportSize)
    {
        double start = imageStart - margin, end = imageStart + imageSize + margin;
        if (end - start > screenSize)
        {
            start = Math.Max(start, screenStart);
            end = Math.Min(end, screenStart + screenSize);
        }
        viewportStart = start + margin;
        viewportSize = Math.Max(1, end - start - 2 * margin);
    }

    private const double MinimumVisibleContentDip = 32;
    private Avalonia.Media.Imaging.Bitmap? _edgeBoundsSource;
    private int _edgeBoundsShadowPad = -1;
    private Avalonia.Rect _edgeSourceBounds;

    /// <summary>实际像素外接矩形：剔除烘焙阴影及全透明外边距，按当前显示位图缓存，拖动不重复读像素。</summary>
    private Avalonia.Rect GetVisibleSourceBounds(Avalonia.Media.Imaging.Bitmap source)
    {
        int pad = _hasBakedShadow ? _bakedShadowPadPx : 0;
        if (ReferenceEquals(source, _edgeBoundsSource) && pad == _edgeBoundsShadowPad) return _edgeSourceBounds;
        _edgeBoundsSource = source;
        _edgeBoundsShadowPad = pad;
        int w = source.PixelSize.Width, h = source.PixelSize.Height;
        pad = Math.Clamp(pad, 0, Math.Max(0, (Math.Min(w, h) - 1) / 2));
        int left = pad, top = pad, right = w - pad, bottom = h - pad;
        _edgeSourceBounds = new Avalonia.Rect(left, top, right - left, bottom - top);
        if (source.AlphaFormat == Avalonia.Platform.AlphaFormat.Opaque
            || source.Format is not { } format
            || (format != Avalonia.Platform.PixelFormat.Bgra8888 && format != Avalonia.Platform.PixelFormat.Rgba8888))
            return _edgeSourceBounds;
        try
        {
            // 只读外缘行／列，普通不透明图四次即结束；不用整图副本或逐帧像素扫描。
            var buffer = new byte[checked(Math.Max(w, h) * 4)];
            while (top < bottom && !StripHasVisiblePixels(source, new PixelRect(left, top, right - left, 1), buffer)) top++;
            if (top == bottom)
            {
                // 全透明图没有可保留的像素，退回可操作图框，避免留下无法找回的窗口。
                _edgeSourceBounds = default;
                return default;
            }
            while (bottom > top && !StripHasVisiblePixels(source, new PixelRect(left, bottom - 1, right - left, 1), buffer)) bottom--;
            while (left < right && !StripHasVisiblePixels(source, new PixelRect(left, top, 1, bottom - top), buffer)) left++;
            while (right > left && !StripHasVisiblePixels(source, new PixelRect(right - 1, top, 1, bottom - top), buffer)) right--;
            _edgeSourceBounds = new Avalonia.Rect(left, top, right - left, bottom - top);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 屏幕边界读取透明外缘失败，保留图框 " + ex.Message);
        }
        return _edgeSourceBounds;
    }

    private static unsafe bool StripHasVisiblePixels(Avalonia.Media.Imaging.Bitmap source, PixelRect rect, byte[] buffer)
    {
        fixed (byte* p = buffer)
            source.CopyPixels(rect, (IntPtr)p, buffer.Length, rect.Width * 4);
        int length = rect.Width * rect.Height * 4;
        for (int i = 3; i < length; i += 4)
            if (buffer[i] != 0) return true;
        return false;
    }

    /// <summary>物理窗口内真正能显示的内容；图像位置可由拟合中的几何传入，不能借旧窗口原点反算。</summary>
    private Avalonia.Rect GetVisibleContentInWindow(int wPx, int hPx, double s, Avalonia.Rect? imageRect = null)
    {
        double m = ShadowMargin * s;
        var viewport = new Avalonia.Rect(m, m, Math.Max(1, wPx - m * 2), Math.Max(1, hPx - m * 2));
        // 画布有可见棋盘底片，内容就是画布视口；文本／颜色贴图同样以其卡片为可操作内容。
        if (_isCanvasMode || StickerImage?.IsVisible != true) return viewport;
        var image = imageRect ?? GetFittedContentBoundsDip();
        if (!imageRect.HasValue)
        {
            image = new Avalonia.Rect(image.X * s, image.Y * s, image.Width * s, image.Height * s);
            if (_isThumbnailMode) image = image.Translate(new Avalonia.Vector(_viewportX * s, _viewportY * s));
        }
        image = GetVisibleImageBounds(image);
        if (image.Width <= 0 || image.Height <= 0) return viewport;
        var clipped = image.Intersect(viewport);
        return clipped.Width > 0 && clipped.Height > 0 ? clipped : viewport;
    }

    /// <summary>允许移出任一屏幕，但至少保留 32 DIP 的内容宽、高；小图保留自身尺寸，选最近合法落点。</summary>
    private static void ClampVisibleContentPosition(ref int xPx, ref int yPx, Avalonia.Rect content, double s,
        IReadOnlyList<System.Drawing.Rectangle> workAreas)
    {
        if (content.Width <= 0 || content.Height <= 0) return;
        int bestX = xPx, bestY = yPx;
        double bestDistance = double.PositiveInfinity;
        foreach (var work in workAreas)
        {
            if (work.Width <= 0 || work.Height <= 0) continue;
            double visibleW = Math.Min(MinimumVisibleContentDip * s, Math.Min(content.Width, work.Width));
            double visibleH = Math.Min(MinimumVisibleContentDip * s, Math.Min(content.Height, work.Height));
            int minX = (int)Math.Ceiling(work.Left + visibleW - content.Right);
            int maxX = (int)Math.Floor(work.Right - visibleW - content.Left);
            int minY = (int)Math.Ceiling(work.Top + visibleH - content.Bottom);
            int maxY = (int)Math.Floor(work.Bottom - visibleH - content.Top);
            if (minX > maxX || minY > maxY) continue;
            int nx = Math.Clamp(xPx, minX, maxX), ny = Math.Clamp(yPx, minY, maxY);
            double dx = nx - (double)xPx, dy = ny - (double)yPx;
            double distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestX = nx; bestY = ny; bestDistance = distance;
            if (distance == 0) break;
        }
        xPx = bestX; yPx = bestY;
    }

    /// <summary>表面尺寸仍封顶工作区包围盒，位置改用最小可见内容规则；两者必须分别记账。</summary>
    private void ClampRectToWorkArea(ref int xPx, ref int yPx, ref int wPx, ref int hPx, double s,
        out bool movedByClamp, Avalonia.Rect imageRect, IReadOnlyList<System.Drawing.Rectangle> workAreas)
    {
        var wa = WorkAreaBounds(workAreas);
        movedByClamp = false;
        if (wa.Width <= 0 || wa.Height <= 0) return;
        if (wPx > wa.Width) wPx = Math.Max(1, SnapStablePhysical(wa.Width, s));
        if (hPx > wa.Height) hPx = Math.Max(1, SnapStablePhysical(wa.Height, s));
        int beforeX = xPx, beforeY = yPx;
        var content = GetVisibleContentInWindow(wPx, hPx, s, imageRect);
        ClampVisibleContentPosition(ref xPx, ref yPx, content, s, workAreas);
        movedByClamp = xPx != beforeX || yPx != beforeY;
    }

    // --- 缩略图诊断日志（[贴图诊断] 前缀；Debug 构建落盘 log/app.log，用 开发运行.bat 复现后查看） ---
    private static void ThumbDiag(string message) => AppLogger.Debug("[贴图诊断] " + message);
    /// <summary>滚轮缩放手势诊断日志（临时用 INFO 级别确保落盘；定位后再改回 DEBUG）。</summary>
    private static void WheelDiag(string message) => AppLogger.Info("[缩放诊断] " + message);

    /// <summary>Win32 错误码文字化。现场报告里出现过 `SetWindowPos=False err=87`，光看数字读不出含义，
    /// 补一句人话省掉下一次现场判读的翻文档时间（2026-09-17 加）。</summary>
    private static string DescribeWin32Error(int err) => err switch
    {
        0 => "0（无错误：调用返回成功但几何仍被系统否决，见失活上下文那条）",
        87 => "87（ERROR_INVALID_PARAMETER——句柄无效或窗口正在销毁）",
        1400 => "1400（ERROR_INVALID_WINDOW_HANDLE——窗口已不存在）",
        _ => err.ToString(),
    };
    /// <summary>贴图标注诊断日志（临时用 INFO 级别确保落盘；浮窗抢前台问题定位后删除）。</summary>
    private static void AnnoDiag(string message) => AppLogger.Info("[标注诊断] " + message);
    /// <summary>图片冻结基准：进入缩略图前图片内容区左上角的屏幕物理位置（进入/退出后校验偏差，量化"位置偏移"）。</summary>
    private double _thumbFreezeBaseX, _thumbFreezeBaseY;

    /// <summary>某个缩放档对应的内容显示宽度（DIP）。<b>这条公式有两条消费通路，必须共用</b>：
    /// <see cref="ApplyScale"/>（真排版）与滚轮手势的"冻结栅格"通路（只算目标尺寸、不排版）。
    /// 抄第二份就会漂移，而漂移的表现是画面尺寸与窗口矩形各说各话（本文件反复记过的失效模式）。</summary>
    private double DisplayWidthDipAt(double scale) => Math.Max(20.0, Math.Round(_sourcePixelWidth * scale) / SafeScaling);

    /// <summary>见 <see cref="DisplayWidthDipAt"/>。</summary>
    private double DisplayHeightDipAt(double scale) => Math.Max(20.0, Math.Round(_sourcePixelHeight * scale) / SafeScaling);

    /// <summary>各层显式宽高当前对应的那一档 <c>_scale</c>（"栅格基准档"）。
    /// 滚轮手势走冻结栅格通路时，画面视觉尺寸＝<c>_scale</c>、布局尺寸＝这一档，两者之比就是挂在
    /// <c>RootGrid.RenderTransform</c> 上的倍率。<see cref="ApplyScale"/> 每次真排版都把它重立为
    /// 当前 <c>_scale</c>（那一步同时把变换归 1），故本字段只在"冻结期"与 <c>_scale</c> 不等。</summary>
    private double _gestureRasterScale = double.NaN;

    /// <summary>手势期挂在 <c>RootGrid.RenderTransform</c> 上的目标倍率（画面视觉尺寸 ÷ 已排版档尺寸）。
    /// 补帧循环把它从 <see cref="_zoomV"/> 匀滑推过来；凡"按布局尺寸算几何"的出口都要按它反解
    /// （见 <see cref="PinGestureLayers"/>），落真排版与退出覆写时归 1。</summary>
    private double _gestureV = 1.0;

    /// <summary><see cref="SettleZoomRaster"/> 的递归闸：ApplyScale 的覆写态分支会经
    /// <see cref="RepinWheelVisualForNewContentSize"/> → <see cref="StopZoomEase"/> 绕回落账，
    /// 不加这道闸就是无限递归（栈溢出前没有任何报错）。</summary>
    private bool _settlingRaster;

    /// <summary>把手势期的"冻结栅格"落回真尺寸：一次 <see cref="ApplyScale"/>（各层宽高追平当前
    /// <c>_scale</c>）＋按<b>目标画面矩形</b>重钉各层 Margin。
    /// ⚠ 刻意不走 <see cref="RepinWheelVisualForNewContentSize"/>（那是"原地换图、画面中心不动"的
    /// 规则）：这里画面中心<b>必须</b>跟着鼠标锚点算出的目标矩形走，用中心锚定等于把画面往回拽，
    /// 与 <see cref="HandleWheelZoomEnvelope"/> 里"刻意不按旧视口约束锚点"同一条道理。
    /// 调用时机＝一切"变换倍率不能留着"的时刻（补帧终止、平移/拖边起手、跨档重落），
    /// 以及每一格的收尾（见 <see cref="StopZoomEase"/>）。</summary>
    private void SettleZoomRaster()
    {
        if (_settlingRaster) return;   // ApplyScale → 覆写态分支 → StopZoomEase 会绕回来，见那边的递归闸
        if (double.IsNaN(_gestureRasterScale) || _gestureRasterScale == _scale) return;
        if (!_wheelVisualOverride) return;   // 未进手势：各层宽高本就由 ApplyScale 管着，无需落账

        // 目标画面矩形（鼠标锚点算出的那份）先存下来：下面 ApplyScale 在覆写态分支里会按
        // "画面中心不动"改写它，而这里画面中心必须回到锚点算出的位置。
        double keepL = _wheelImgLpx, keepT = _wheelImgTpx, keepW = _wheelImgWpx, keepH = _wheelImgHpx;
        _settlingRaster = true;
        try
        {
            ApplyScale(skipWindowResize: true);   // 其内部把 _gestureRasterScale 重立为 _scale、变换归 1
        }
        finally { _settlingRaster = false; }
        if (!_wheelVisualOverride) return;         // 通路中途已退出覆写（提交收口在自己做收尾）

        _wheelImgLpx = keepL; _wheelImgTpx = keepT; _wheelImgWpx = keepW; _wheelImgHpx = keepH;
        double mPx = ShadowMargin * SafeScaling;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            _wheelEnvL = rc.Left + mPx; _wheelEnvT = rc.Top + mPx;
            _wheelEnvW = rc.Right - rc.Left - mPx * 2; _wheelEnvH = rc.Bottom - rc.Top - mPx * 2;
        }
        PinGestureLayers();   // 此刻倍率已由 ApplyScale 归 1 ⇒ 就是"画面＝目标矩形"的直接落位
    }

    private void ApplyScale(bool skipWindowResize = false)
    {
        _isApplyingScale = true;
        try
        {
            // 截图位图是物理像素，窗口 Width/Height 是 DIP（设备无关像素）。
            // 物理像素 ÷ DPI 缩放 = DIP，这样贴图在屏幕上的物理尺寸与原截图区域一致。
            double s = SafeScaling;
            double width = DisplayWidthDipAt(_scale);
            double height = DisplayHeightDipAt(_scale);

            // 每次真排版都把"栅格基准档"重立到当前 _scale：ApplyScale 的定义就是"各层显式宽高
            // 追平 _scale"，此刻画面相对窗口不再需要任何补偿倍率（否则变换会与尺寸叠两次）。
            _gestureRasterScale = _scale;
            _gestureV = 1.0;
            if (_zoomFx != null && (_zoomV != 1.0 || _zoomEasing))
            {
                _zoomV = _zoomVTarget = 1.0;
                _zoomFx.ScaleX = _zoomFx.ScaleY = 1.0;
            }

            // 覆写态下原地换图要按"图片屏幕中心不动"重钉几何，故先留一份改写前的尺寸
            double prevImgW = StickerImage.Width, prevImgH = StickerImage.Height;

            // Image 控件始终使用完整缩放尺寸，超出窗口部分由 ClipToBounds 裁剪
            StickerImage.Width = width;
            StickerImage.Height = height;

            // 底色层与遮罩层都必须与图片严格同尺寸/同定位，
            // 信封区因此不会被底色或半透明遮罩填满
            if (ImageBackdrop != null)
            {
                ImageBackdrop.Width = width;
                ImageBackdrop.Height = height;
                ImageBackdrop.Margin = StickerImage.Margin;
            }
            if (TranslatingOverlay != null)
            {
                TranslatingOverlay.Width = width;
                TranslatingOverlay.Height = height;
                TranslatingOverlay.Margin = StickerImage.Margin;
            }
            if (CheckerboardBg != null)
            {
                CheckerboardBg.Width = width;
                CheckerboardBg.Height = height;
                CheckerboardBg.Margin = StickerImage.Margin;
            }

            if (TextViewbox != null)
            {
                TextViewbox.Width = width;
                // Markdown 贴图在高度捕获阶段不设固定高度，让内容自然展开
                if (!_needsMarkdownHeightCapture || !IsMarkdownSticker)
                {
                    TextViewbox.Height = height;
                }
            }

            // Canvas 必须与 Image 同尺寸，否则定位文本出现在可见区域外
            if (StickerCanvas != null)
            {
                StickerCanvas.Width = width;
                StickerCanvas.Height = height;
            }

            if (StickerAnnotationLayer != null)
            {
                StickerAnnotationLayer.Width = width;
                StickerAnnotationLayer.Height = height;
                StickerAnnotationLayer.ScreenToDipScale = _scale / s;
            }
            // 文本宿主层与标注层成对：缩放后不同步，文字会按旧 DIP 尺寸坐在原位（protection 条款③ 同型）
            SyncTextHostGeometry();

            // Rebuild word rectangle overlay when zoom changes
            if (StickerCanvas != null && StickerCanvas.IsVisible && _wordRegions != null && _wordRegions.Count > 0)
            {
                BuildWordOverlay();
            }

            if (_isThumbnailMode)
            {
                // 缩略图视口只在进入模式时由 ToggleThumbnailMode 一次性确定（鼠标位置或图片中心），之后保持固定。
                // 此处禁止改写 _viewportX/_viewportY：进入缩略图会改窗口尺寸并异步触发 SizeChanged →
                // OnWindowSizeChanged → ApplyScale，若在此重置视口会把"以鼠标为中心"的定位冲回图片中心。
                // 拖角自定义过取景框后禁止把窗口打回 150 默认框（_thumbFrameCustomized）。
                // 尺寸换算与 ApplyThumbnailStateImmediate 同走 SnapStablePx：两者结果一致 →
                // Width setter 无变化 → 不触发布局回打（否则会把 RAF 终态打偏）。
                if (!skipWindowResize && !_thumbFrameCustomized)
                {
                    double newWidth = Math.Min(150.0, width);
                    double newHeight = Math.Min(150.0, height);
                    SizeToContent = SizeToContent.Manual;
                    Width = SnapStablePx(newWidth + ShadowMargin * 2, s) / s;
                    Height = SnapStablePx(newHeight + ShadowMargin * 2, s) / s;
                }
            }
            else
            {
                // 窗口 = 图片尺寸 + 阴影边距，允许超出屏幕，用户拖动窗口查看
                _viewportX = 0;
                _viewportY = 0;
                UpdateTransforms();

                if (!skipWindowResize)
                {
                    // ⚠ 覆写态判别必须走在该链所有分支之前：覆写态下窗口尺寸归视口簿记
                    // 所有，而 StickerImage.Margin / TextViewbox.Margin 是内容在视口内的偏移
                    // （可达数百 DIP）。若先落进下面任一"贴合"分支——文本的
                    // SizeToContent=WidthAndHeight、Markdown 捕获期的 SizeToContent=Height、
                    // 图片的"窗口 = 图片 + 阴影"公式——布局会立刻把窗口拽回贴合内容的小尺寸
                    // 却留着这段偏移，内容整体坐偏、越出窗口的部分被 ClipToBounds 裁掉
                    // （观感是"局部缺失"），命中穿透矩形也对不上。
                    // 文本贴图也在覆写态内（见 ActivateWheelEnvelope），故同样要判。
                    // 原地换图（ReplaceImage）正走这条路径。
                    if (_wheelVisualOverride)
                    {
                        RepinWheelVisualForNewContentSize(prevImgW, prevImgH, width, height);
                    }
                    else if (StickerTextBlock != null && StickerTextBlock.IsVisible)
                    {
                        SizeToContent = SizeToContent.WidthAndHeight;
                    }
                    else if (_needsMarkdownHeightCapture && IsMarkdownSticker)
                    {
                        SizeToContent = SizeToContent.Height;
                        Width = width + ShadowMargin * 2;
                    }
                    else
                    {
                        SizeToContent = SizeToContent.Manual;
                        // 稳定值量化：窗口物理尺寸满足往返稳定条件，避免布局回打与初始定位的亚像素差
                        Width = SnapStablePx(width + ShadowMargin * 2, s) / s;
                        Height = SnapStablePx(height + ShadowMargin * 2, s) / s;
                    }
                }
            }
        }
        finally
        {
            _isApplyingScale = false;
        }

        // 确保使用完整源图（非裁剪模式）
        if (_source != null && StickerImage.Source != _source)
        {
            StickerImage.Source = _source;
            StickerImage.Stretch = Avalonia.Media.Stretch.Fill;
        }

        UpdateCardHighlightLayer();
    }

    private void ResizeWindowCentered(double clientW, double clientH, double centerX, double centerY)
    {
        double s = SafeScaling;
        double newWidthPx = (clientW + ShadowMargin * 2) * s;
        double newHeightPx = (clientH + ShadowMargin * 2) * s;

        int xPx = (int)Math.Round(centerX - newWidthPx / 2);
        int yPx = (int)Math.Round(centerY - newHeightPx / 2);
        int cxPx = (int)Math.Round(newWidthPx);
        int cyPx = (int)Math.Round(newHeightPx);

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            // Atomically resize and position window in a single native call to prevent layout stutter
            // SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010
            ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, cxPx, cyPx, StickerStyling.ResizeSwpFlags);
            
            // Sync properties to Avalonia immediately
            Width = clientW + ShadowMargin * 2;
            Height = clientH + ShadowMargin * 2;
            Position = new PixelPoint(xPx, yPx);
        }
        else
        {
            Width = clientW + ShadowMargin * 2;
            Height = clientH + ShadowMargin * 2;
            Position = new PixelPoint(xPx, yPx);
        }
    }

    private void ResizeWindowTopLeft(double clientW, double clientH)
    {
        double s = SafeScaling;
        // 吸附到"往返稳定"物理像素（滚轮缩放路径与拖角同机制，避免布局回打）
        int cxPx = SnapStablePx(clientW + ShadowMargin * 2, s);
        int cyPx = SnapStablePx(clientH + ShadowMargin * 2, s);
        var origin = GetWindowOriginPx();
        int xPx = origin.X, yPx = origin.Y;
        var imageRect = new Avalonia.Rect(ShadowMargin * s, ShadowMargin * s, clientW * s, clientH * s);
        ClampVisibleContentPosition(ref xPx, ref yPx,
            GetVisibleContentInWindow(cxPx, cyPx, s, imageRect), s, GetStickerDisplayRects());

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            // SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010 (0x0016)
            ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, cxPx, cyPx,
                xPx == origin.X && yPx == origin.Y ? 0x0016u : StickerStyling.ResizeSwpFlags);

            // Sync properties to Avalonia immediately
            Width = cxPx / s;
            Height = cyPx / s;
        }
        else
        {
            Width = clientW + ShadowMargin * 2;
            Height = clientH + ShadowMargin * 2;
        }
        if (xPx != origin.X || yPx != origin.Y) Position = new PixelPoint(xPx, yPx);
    }

    private void UpdateTransforms()
    {
        _imageTransform.X = _viewportX;
        _imageTransform.Y = _viewportY;
        _textTransform.X = _viewportX;
        _textTransform.Y = _viewportY;
        _canvasTransform.X = _viewportX;
        _canvasTransform.Y = _viewportY;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BringStickerToFront();
        if (HandleCanvasPressed(e)) return;
        // 滚轮信封手势期间的按下语义：**左/右/中键按下均不收缩**。信封态窗口在内容+阴影
        // 之外已 WM_NCHITTEST 穿透（不挡下层点击、也接不到那里的指针事件），收到的按下必然
        // 落在可见内容上，故边缘检测与拖动都直接在信封态进行（见 GetFittedClientRectDip）。
        // 按下即收缩曾经是这里唯一的闪烁来源：一次带 cloak 的整窗 resize + 该帧 Bounds 未更新
        // 导致边缘判不出（要点第二下才有锚点）。现在仅快捷键、缩略图切换与窗口失活才收缩。
        // ⚠ Avalonia 的 Properties.IsLeftButtonPressed 是事件前快照，PointerPressed 里对
        // 触控板/触摸可能仍未置 true，故分支判定不依赖"是否左键"的开头统一处理。
        //
        // 先落真排版：本方法下面每一处几何判据（边缘热区 `GetFittedClientRectDip`、选词命中、
        // 拖动/平移起手）都按"布局尺寸＝画面尺寸"来算，而冻结栅格期这两者差一个倍率
        // （画面还额外被 RootGrid 的变换放大过）。不停在这儿就会"看得见的那块点不准"。
        // 静止或已落过真排版时本调用是两次比较的空操作，不会带来 resize。
        StopZoomEase();

        // 分层呈现的几何对账（issue #52）：呈现窗画像素、本窗收点击，两者一旦分家就是
        // "看得见的点不到、原位置误触发"。用户感知错位的那一刻就是他按下的这一刻，
        // 在这里查一次、错了当场重对齐 ⇒ 最坏情况也只是第一下点空、第二下即正常。
        // 未启用分层呈现 / 未接管时是一次布尔判断的空操作。
        VerifyPinAlignmentOnInput();

        // 划词工具栏：本方法是 Tunnel 相位——落在浮条之外的按下＝上一段选区作废，先收起再走下面的分支；
        // 🔴 落在浮条上必须**整条早退**：下面每一处选字/选词/拖动分支都以 `e.Handled = true` 收尾，
        // 而 Tunnel 的 Handled 会截断路由，浮条里那枚按钮就永远收不到 Click（点了没反应的根因），
        // 顺带还把选区改掉、松手时浮条自己收起。早退不等于吞事件——命中测试已经把这一按下定在浮条上，
        // 后面的窗口逻辑本来就不该参与。
        if (_selectionToolbarShown)
        {
            if (IsVisualDescendant(SelectionToolbar, e.Source as Avalonia.Visual)) return;
            HideSelectionToolbar();
        }

        // 划词翻译的持久标记（2026-09-30 二次重设计）：紫点、气泡、气泡左上角红 ✕ 三件都吃命中，
        // 按下的早退纪律与浮条同一条——落在它们身上＝不是"点了贴图其他处"，别去起拖动、也别改选区。
        // 🔴 标记**不再**因为点了别处而退场：清除只有两条路（气泡红 ✕ 清一条、贴图关闭清全部）。
        if (IsVisualDescendant(TranslateDotLayer, e.Source as Avalonia.Visual)) return;
        if (IsVisualDescendant(TranslateBubble, e.Source as Avalonia.Visual)) return;
        if (IsVisualDescendant(TranslateBubbleClose, e.Source as Avalonia.Visual)) return;

        // 🔴 气泡收起的等价补偿。原先气泡只可能在"指针正悬在紫点或气泡上"时开着，故任何一次
        // 点别处都必然先触发紫点 PointerExited → 180ms 计时器把它收掉；「落笔即出气泡」打破了
        // 这条前提（点「翻译」直接展开，指针从未碰过紫点，那只计时器压根没启动），于是这里补上：
        // 按下落在紫点／气泡／红 ✕ 之外＝用户已转去做别的事，气泡让位。
        // 只收气泡，**不动标记**——紫线与紫点是持久的，清除仍只有红 ✕ 与关贴图两条路。
        if (_bubbleShown) HideTranslateBubble();

        // 缩略图模式下优先处理角部拖拽（调整取景框尺寸，按住 Ctrl 时豁免以允许拖出贴图文件）
        if (_isThumbnailMode && !_thumbApplyPending && !_isLocked &&
            !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var corner = GetThumbCornerAt(e.GetPosition(this));
            if (corner.HasValue)
            {
                StickerFocusManager.SetFocus(this);
                StartThumbCornerDrag(corner.Value, e);
                return;
            }
        }

        if (_isLocked || _isMousePenetration)
        {
            // If locked and right click, still show context menu so they can unlock
            PointerPoint pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsRightButtonPressed && _isLocked)
            {
                UpdateContextMenuState();
                RootBorder.ContextMenu?.Open(RootBorder);
                e.Handled = true;
            }
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);

        // 拖边缩放进行中：与信封提交同一时机收口（中键关闭 / 右键菜单都不与拖边并存），
        // 否则 _isResizing 与指针捕获会悬挂在半途，后续移动仍会改写窗口。
        if (_isResizing) EndEdgeResize();

        if (point.Properties.IsMiddleButtonPressed)
        {
            // 中键关闭：**不提交信封**——窗口马上要关，先收缩只会多一次带 cloak 的 resize
            // （＝点击闪烁）；命中穿透子类化由 OnClosed 统一卸载。
            e.Handled = true;
            Close();
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // 右键菜单：**不提交信封**。菜单按指针位置定位，而指针必然落在可见内容上
            // （内容之外已 HTTRANSPARENT 穿透，右键到不了本窗口），收缩对菜单毫无必要。
            // Handled here so context menu opens on release
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        // 浮窗三枚菜单关了 light dismiss（避免与"不激活浮窗"抢前台），故由宿主主动收。
        // 位次：排在两枚浮条的早退**之前**——此前只在"确实要起一笔"时才收，落在圆角/序号浮条上、
        // Ctrl 点图元、点一下却没起笔的那些按下都会把菜单支在原地不动。
        if (_isAnnotating) _annotationPalette?.ToolbarControl.CloseMenus();

        // 圆角浮条是贴在贴图上的一层 UI：落在它上面的左键按下归浮条自己（按住数值拖动微调），
        // 不参与"起笔 / 编辑图元 / 拖动贴图"。中键与右键的全局语义（关窗、菜单）不受影响。
        // 序号浮条（2026-09-18）同一条道理，两条守卫并排。
        if (IsFromSpotlightRadiusPill(e.Source)) return;
        if (IsFromCounterPill(e.Source)) return;

        // 标注模式：拦截左键按下，交给标注控制器
        if (_isAnnotating && _annotationController != null)
        {
            // 正在写序号说明时点到贴图表面上：这一笔只用来结字，吞掉它——否则同一下点击会既提交说明、
            // 又在落点盖一颗新序号（2026-09-19 实报）。截图侧由浮条自己在窗口根的 Tunnel 里吞（见
            // CounterPill.CanvasPressTest），本窗口的按下注册得比浮条早、等不到那里，只能起笔前自己收这一枪。
            if (CounterPillHost?.TryCommitNoteFromCanvasPress() == true)
            {
                e.Handled = true;
                return;
            }
            var winPos = e.GetPosition(this);
            bool textToolCurrent = _activeAnnotationKey == "text";
            bool ctrlHeld = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
            // ★ 相位修正：本窗口的按下走 Tunnel，会抢在文本层自己的旋转柄 / 删除柄 / TextBox 之前跑完，
            // 手柄点下去会变成"拖动文字"。截图侧的文本按下是在<b>冒泡</b>里做的（且 if (e.Handled) return），
            // 故手柄天然先吃掉那一下。这里凡"文本工具已激活且按下落在某个文本层内"一律不接管、也不置
            // Handled，交给 OnAnnotationTextPointerPressedBubble 在冒泡相位补做。
            if (textToolCurrent && TextTool.FindTextInfoFromSource(e.Source) != null) return;
            if (ctrlHeld)
            {
                if (_activeAnnotationTool == ToolMode.Eraser)
                {
                    Anno.RaiseTargetAboveEraser(winPos, e.GetPosition(StickerTextHost));
                    e.Handled = true;
                    return;
                }
                // Ctrl+点击＝编辑已有图元。"点中矢量本体 / 点中手柄 / 点中文本层"三级由共享派发按同一条
                // z 序一次判完（见 TryBeginAnnotationShapeEdit），与截图遮罩不可能漂移。
                // 未命中时内部已清掉选中并返回 false，继续往下照常起笔——Ctrl 点空白照样能画。
                if (_activeAnnotationTool != ToolMode.Eraser && TryBeginAnnotationShapeEdit(winPos, e, editExisting: true))
                {
                    e.Handled = true;
                    return;
                }
                // 文本工具下 Ctrl 点空：截图侧明确"没命中其他图形也不该创建新文本"，此处同样什么都不做
                if (textToolCurrent)
                {
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                // 免 Ctrl 的唯一例外＝已选中图元的手柄：它只在选中后才画出来，点中即明确的编辑意图。
                // 文本工具下连这个例外也没有——截图侧那条通路在文本工具分支里就先 return 了。
                if (!textToolCurrent && _activeAnnotationTool != ToolMode.Eraser
                    && TryBeginAnnotationShapeEdit(winPos, e, editExisting: false))
                {
                    e.Handled = true;
                    return;
                }
                if (textToolCurrent && TryHandleAnnotationTextPointerPressed(winPos, e))
                {
                    // 文本工具下的普通按下整体归共享文本状态机，不进矢量图笔状态机
                    e.Handled = true;
                    return;
                }
            }
            var settings = CreateGestureSettings(Anno.ArrowScale);
            if (_annotationController.Begin(winPos, settings))
            {
                e.Handled = true;
                return;
            }
        }

        // Close context menu if open when clicking elsewhere in the window
        RootBorder.ContextMenu?.Close();

        // Ctrl+左键：拖拽导出贴图为临时图片文件（必须在边缘拉伸、Alt 选词、OCR 取词与普通拖动前拦截）
        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            EndStickerDrag("切换文件拖放");
            StickerFocusManager.SetFocus(this);
            _isCtrlDragPending = true;
            _ctrlDragStartPos = e.GetPosition(this);
            _isDragging = false;
            e.Handled = true;
            return;
        }

        // 判边缘**不需要**先提交信封：<see cref="GetFittedClientRectDip"/> 直接给出内容在
        // 信封内的等效贴合矩形。旧写法在此处 CommitWheelGesture 有两个后果：① 那一次带 cloak
        // 的整窗 resize＝"每次点贴图闪一下"；② resize 是异步落地的，按下这一帧 Bounds 仍是信封
        // 大矩形，于是边缘照样判不出来，用户必须"先点一下、再拖"才有锚点。
        var pos = e.GetPosition(this);
        // 双击优先于边缘检测：小贴图（宽或高 < 40DIP）整个都在 20DIP 边缘热区内，
        // 若先判边缘，双击永远被拖边缩放拦截、到不了关闭分支。
        var edge = (_isThumbnailMode || e.ClickCount >= 2) ? null : GetWindowEdgeAt(pos, out _);

        if (edge.HasValue)
        {
            // 拖边缩放：接管本次按下。必须 return，否则继续落入下方拖动分支会同时挪窗口
            BeginEdgeResize(edge.Value, e);
            return;
        }
        else
        {

            // Alt+左键：图片贴图快速选词——不进入持久"文本可选择"模式，
            // OCR 结果缓存复用（首次识别后再次 Alt 拖选零等待）。
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)
                && StickerImage != null && StickerImage.IsVisible && _bitmap != null)
            {
                e.Handled = true;
                StickerFocusManager.SetFocus(this);
                BeginAltQuickSelect(e);
                return;
            }

            // Alt+左键：文本/Markdown 贴图临时选字——不进入持久"文本可选择"模式。
            // 仅在临时模式尚未开启时拦截（先悬停开启后再按下的场景走下方原生选择分支）。
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)
                && StickerImage != null && !StickerImage.IsVisible
                && !_isTextSelectable && !_altTextStickerSelect)
            {
                e.Handled = true;
                StickerFocusManager.SetFocus(this);
                BeginAltTextStickerSelect(e);
                return;
            }

            // 非 Alt 的普通按下：若 Alt 快速选词/临时选字仍挂着（上一次拖选后未收尾）且
            // 持久"文本可选择"模式未开启，先收尾恢复正常贴图交互，再继续常规
            // 流程（拖动/双击等）。持久模式开启时覆盖层归其管理，不可在此清除。
            // ⚠ 必须同时要求 Alt 未按下：按住 Alt 移动到贴图上（悬停已激活临时选字）
            // 再按下左键时，本收尾不能执行——否则命中测试被关回 false，下方原生
            // 选择分支失效，按下退化为拖动窗口（Alt 选字完全失效的根因）。
            bool altHeld = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt);
            if (_altQuickSelect && !_isTextSelectable && !altHeld)
            {
                EndAltQuickSelect();
            }
            if (_altTextStickerSelect && !_isTextSelectable && !altHeld)
            {
                EndAltTextStickerSelect();
            }

            // Inside window: check if we clicked on a selectable word region
            if (_isTextSelectable && _wordRegions != null && _wordRegions.Count > 0 &&
                StickerCanvas != null && StickerCanvas.IsVisible && StickerCanvas.IsHitTestVisible)
            {
                // Get position relative to the Canvas (not the window) for word hit-test
                var canvasPos = e.GetPosition(StickerCanvas);
                // 容差已收进行带认领（FindWordIndexAt → ClaimWordLineAt，单位＝屏幕 DIP），
                // 这里不再叠第二道源图像素阈值——旧的那道在贴图缩小时会把命中判成空白
                int wordIdx = FindWordIndexAt(canvasPos);
                if (wordIdx >= 0)
                {
                    // 按下文字区域：清除旧选中，记录起点。
                    if (_selectedWordIndices.Count > 0)
                    {
                        _selectedWordIndices.Clear();
                        ApplyWordSelection();
                    }
                    StickerFocusManager.SetFocus(this);
                    _isWordSelecting = true;
                    _dragStartWordIndex = wordIdx;
                    e.Handled = true;
                    return;
                }
                else
                {
                    // 点击空白区域 → 清除选中
                    if (_selectedWordIndices.Count > 0)
                    {
                        _selectedWordIndices.Clear();
                        ApplyWordSelection();
                    }
                }
            }

            // Check legacy TextBlock (text sticker with selectable text)
            if ((_isTextSelectable || _altTextStickerSelect) && StickerTextBlock != null && StickerTextBlock.IsVisible && StickerTextBlock.IsHitTestVisible)
            {
                if (IsVisualDescendant(StickerTextBlock, e.Source as Avalonia.Visual))
                {
                    // 双击执行贴图配置动作（即使在文本编辑模式下也生效）
                    if (e.ClickCount >= 2)
                    {
                        e.Handled = true;
                        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
                            ToggleThumbnailMode();
                        else
                            HandleDoubleClickAction();
                        return;
                    }
                    var textPos = e.GetPosition(StickerTextBlock);
                    bool isBlank = IsPointInBlankSpace(textPos.X, textPos.Y);
                    if (isBlank)
                    {
                        // 对齐图片贴图持久模式：点击空白清除选区（含复制快照）
                        ClearTextStickerSelection();
                        _isDragging = true;
                        _dragStartWindowPosition = Position;
                        _lastAppliedDragPos = _dragStartWindowPosition;
                        var dragPt = e.GetPosition(this);
                        double s = DesktopScaling;
                        _dragStartScreenPoint = new PixelPoint((int)Math.Round(dragPt.X * s + Position.X), (int)Math.Round(dragPt.Y * s + Position.Y));
                        BeginDragSemantics(_dragStartScreenPoint);
                        CaptureStickerDrag(e.Pointer);
                    }
                    else
                    {
                        // 与其他分支一致：先激活窗口（保证后续键盘事件可达），再聚焦文本块
                        StickerFocusManager.SetFocus(this);
                        StickerTextBlock.Focus();
                    }
                    return;
                }
            }
            else if ((_isTextSelectable || _altTextStickerSelect) && StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible && StickerMarkdownViewer.IsHitTestVisible)
            {
                if (IsVisualDescendant(StickerMarkdownViewer, e.Source as Avalonia.Visual))
                {
                    // 双击执行贴图配置动作（即使在文本编辑模式下也生效）
                    if (e.ClickCount >= 2)
                    {
                        e.Handled = true;
                        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
                            ToggleThumbnailMode();
                        else
                            HandleDoubleClickAction();
                        return;
                    }
                    // Always activate the window to allow child controls to get keyboard focus
                    StickerFocusManager.SetFocus(this);
                    // Do NOT set _isDragging. Markdown uses Borders and Panels heavily for tables and code blocks.
                    // Treating them as blank space ruins text selection.
                    return;
                }
            }

            if (e.ClickCount >= 2)
            {
                // 双击＝"贴图配置动作"（进文本编辑 / 关闭 / 缩略图），已不是画图：先定稿收起标注层，
                // 否则文字编辑层会与标注层同时显形。
                FinalizeAnnotationForGeometryChange("双击");
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
                {
                    ToggleThumbnailMode();
                }
                else
                {
                    HandleDoubleClickAction();
                }
                return;
            }

            // Click: set focus and start drag
            StickerFocusManager.SetFocus(this);
            e.Handled = true;
            _dragStartWindowPosition = Position;
            if (ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var pt))
            {
                _dragStartScreenPoint = new PixelPoint(pt.X, pt.Y);
            }
            else
            {
                var startPos = e.GetPosition(this);
                double s = SafeScaling;
                _dragStartScreenPoint = new PixelPoint(
                    (int)Math.Round(startPos.X * s + Position.X),
                    (int)Math.Round(startPos.Y * s + Position.Y)
                );
            }

            _isDragging = true;
            _lastAppliedDragPos = _dragStartWindowPosition;
            BeginDragSemantics(_dragStartScreenPoint);
            CaptureStickerDrag(e.Pointer);
        }
    }

    /// <summary>
    /// 文本层按下的<b>冒泡</b>补做（与截图侧同一相位）：文本层自己的旋转柄 / 删除柄 / TextBox 若已吃掉
    /// 这次按下会置 Handled，这里直接放过；没被吃掉才归共享文本状态机（点文字本体＝选中并进入拖动，
    /// 双击＝再编辑）。Tunnel 相位那条通路只负责"空白处新建文本"。
    /// </summary>
    private void OnAnnotationTextPointerPressedBubble(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !_isAnnotating) return;
        if (_activeAnnotationKey != "text") return;
        var pp = e.GetCurrentPoint(this);
        if (pp.Properties.PointerUpdateKind != Avalonia.Input.PointerUpdateKind.LeftButtonPressed) return;
        if (TextTool.FindTextInfoFromSource(e.Source) == null) return;
        var winPos = e.GetPosition(this);
        // 截图同一优先级：文本工具下按了 Ctrl，走同一条 z 序派发链（矢量本体 / 手柄 / 文本层顺次问一遍，
        // 文本层就是链上"序号之后、箭头之前"那一环）；两者都没命中也绝不借这次 Ctrl 新建文本。
        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            if (TryBeginAnnotationShapeEdit(winPos, e, editExisting: true)) e.Handled = true;
            return;
        }
        if (TryHandleAnnotationTextPointerPressed(winPos, e)) e.Handled = true;
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (HandleCanvasMoved(e)) return;
        if (_isAnnotating && (_activeAnnotationTool == ToolMode.Brush
            || _activeAnnotationTool == ToolMode.Eraser
                && _annotationPalette?.ToolbarControl.EraserMode == ScreenCaptureTool.Core.Capture.Annotations.EraserGestureMode.Brush)
            && StickerAnnotationLayer is { IsVisible: true } layer)
        {
            Avalonia.Point point = e.GetPosition(layer);
            _annotationPointerDip = point.X >= 0 && point.Y >= 0
                && point.X <= layer.Bounds.Width && point.Y <= layer.Bounds.Height
                ? point : null;
            UpdateAnnotationPointerFootprint();
            if (_annotationPointerDip.HasValue && !ReferenceEquals(Cursor, HiddenAnnotationCursor))
                Cursor = HiddenAnnotationCursor;
        }
        else if (_annotationPointerDip.HasValue)
        {
            _annotationPointerDip = null;
            UpdateAnnotationPointerFootprint();
        }
        // 圆角浮条上的移动（含被它捕获时的拖动）不归窗口：不拖贴图、不拖图元、不换光标。
        // 序号浮条同理（它没有拖动手势，但悬停光标与按下后的捕获期都走这一句）。
        if (IsFromSpotlightRadiusPill(e.Source)) return;
        if (IsFromCounterPill(e.Source)) return;

        // 文本层拖动 / 旋转中：与图元拖动同一优先级、同样先接管（截图侧双挂 OverlayCanvas+窗口的另一半）
        if (_isAnnotating && (TextTool.IsDraggingText || TextTool.IsRotatingText))
        {
            TextTool.TryHandleGlobalPointerMoved(sender, e);
            e.Handled = true;
            return;
        }

        // 图元编辑手势（拖本体 / 拖手柄）进行中：优先于控制器与一切悬浮光标逻辑，提前 return 才不会被下面覆盖
        if (Anno.IsEditingShape)
        {
            // 兜住"收不到 Released"：捕获被外部夺走（弹菜单/系统对话框）时若不判左键，
            // 就变成松手后鼠标一动、图元跟着一动。
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                RequestAnnotationShapeMove(e.GetPosition(this));
            else
                EndAnnotationShapeMove(_annoPointer);
            e.Handled = true;
            return;
        }

        if (_isAnnotating && _annotationController != null && _annotationController.IsDrawing)
        {
            _annotationController.Move(e.GetPosition(this), e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift));
            e.Handled = true;
            return;
        }
        // 缩略图拖角中：仅标记脏并调度渲染帧回调（不在事件里直接改窗口）
        if (_isDraggingThumbCorner)
        {
            // PointerMoved 由消息泵驱动、与渲染循环不同步：若在此直接 SetWindowPos，
            // DWM 会以新窗口尺寸呈现"旧内容变换"（合成器尚未提交新变换），每帧
            // 错位即持续晃动。改为 RAF 内统一应用——与进入/退出过渡动画同机制，
            // 窗口矩形与视口变换同帧原子提交。
            ScheduleThumbCornerRaf();
            e.Handled = true;
            return;
        }

        // 拖边缩放中：与缩略图拖角同一套合帧策略（消息泵与渲染循环不同步，事件里直接
        // 改窗口会让 DWM 用旧内容变换呈现新尺寸而晃动），故只标脏并调度 RAF 帧内应用。
        if (_isResizing)
        {
            _edgeEvtMoved++;   // v8 采证：拖边期实际收到的 PointerMoved 次数
            ScheduleEdgeResizeRaf();
            e.Handled = true;
            return;
        }

        // 滚轮信封手势：指针离开图片区域不再收缩——信封态窗口在图片+阴影之外
        // 已 WM_NCHITTEST 穿透（不挡下层交互），信封长期保留，"点击/移开→再滚"
        // 全程零 resize。收缩仅由右键/中键按下、快捷键、窗口失活触发。

        if (_isLocked || _isMousePenetration) return;

        // Ctrl+左键拖拽导出临时图片文件判定（优先于缩略图、常规拖窗与光标分支）
        if (_isCtrlDragPending)
        {
            var currentPos = e.GetPosition(this);
            double dx = currentPos.X - _ctrlDragStartPos.X;
            double dy = currentPos.Y - _ctrlDragStartPos.Y;
            if (dx * dx + dy * dy >= CtrlDragThreshold * CtrlDragThreshold)
            {
                _isCtrlDragPending = false;
                StartCtrlDragDropAsync(e);
                e.Handled = true;
                return;
            }
        }

        // 缩略图模式：仅内容区四角显示斜向 resize 光标（不走普通贴图的边缘 resize 逻辑）。
        // 必须豁免 _isDragging：按下非角部区域时 PointerPressed 已置 _isDragging 并期望
        // 拖动窗口位置，若此处提前 return 会把移动分支挡死（缩略图无法拖动）。
        if (_isThumbnailMode && !_thumbApplyPending && !_isDragging)
        {
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
                return;
            }
            var corner = GetThumbCornerAt(e.GetPosition(this));
            Cursor = corner.HasValue
                ? new Cursor(ThumbCornerCursor(corner.Value))
                : new Cursor(StandardCursorType.Arrow);
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            SampleDragEvent();   // 只读取时与计数，不改下面任何分支
            if (ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var pt))
            {
                // —— 内容平移优先（2026-09-22，见 docs/贴图视口平移方案.md）——
                // 本次按下起手时判定的语义：内容越出视口 ⇒ 拖动＝平移内容（窗口纹丝不动）；
                // 装得下 ⇒ 拖动＝移动贴图窗口（与改版前逐像素一致）。语义在**按下那一刻**就
                // 冻结进 _isPanningContent，手势期内不再重判——否则平移过程中内容一旦被钳到
                // 边界、越出判据翻假，同一次拖动会中途从"平移"变成"拖窗口"（观感＝手一抖整张
                // 贴图跟着飞走）。这与"拖动每帧从起手原始几何重算"是同一条既有纪律。
                if (_isPanningContent)
                {
                    // 平移量取自起手内容位置 + 光标位移（不累加上一帧结果，避免浮点漂移）
                    // 接入渲染帧合帧（2026-09-23）：高频鼠标位移与显示刷新节拍对齐，彻底消灭 4K 表面下的掉帧
                    ScheduleContentPanRaf(_panStartImgLpx + (pt.X - _panStartScreenPoint.X),
                                          _panStartImgTpx + (pt.Y - _panStartScreenPoint.Y));
                    e.Handled = true;
                    return;
                }

                // 信封时代这里还有一条"封顶态拖动起手先按贴合尺寸落定再拖窗口"的退路，已随阶段 2
                // 删除：窗口现在恒等于内容，越出视口的部分上面已由平移分支接管，不存在
                // "把窗口撑成超屏好把内容拖出来"这回事（那条退路的本来面目就是 14.8MPx 的巨幅表面）。

                // 覆写态保持拖动、**不再收缩**：
                // 采用指针事件直出 + Win32 move-only SetWindowPos，彻底消灭 DispatcherTimer 的 15ms 节流时延，
                // 在 120Hz/144Hz/240Hz/360Hz 高刷屏与高回报率鼠标下做到 0 延迟、完全跟手。
                // DWM 仅平移既有表面，无逐帧重渲染。
                var target = new PixelPoint(
                    _dragStartWindowPosition.X + pt.X - _dragStartScreenPoint.X,
                    _dragStartWindowPosition.Y + pt.Y - _dragStartScreenPoint.Y);
                ApplyDragMove(target);
            }
            e.Handled = true;
            return;
        }


        // Word selection drag
        if (_isWordSelecting && _wordRegions != null && StickerCanvas != null)
        {
            var canvasPos = e.GetPosition(StickerCanvas);
            UpdateSelectionTo(canvasPos);
            e.Handled = true;
            return;
        }

        // Alt 快速选词：OCR 尚未完成（_wordRegions 为空）时跟踪指针位置，
        // 识别完成后以按下点为锚点、此位置为活动点补建选区。
        if (_altQuickSelect && _isWordSelecting && StickerCanvas != null)
        {
            _altLastMoveCanvasPos = e.GetPosition(StickerCanvas);
        }

        // Alt+按下手动选字（文本贴图）：按下瞬间命中测试未开启，原生选择拿不到
        // 按下事件，这里手动驱动 StickerTextBlock 选区（锚点 → 当前指针位置）。
        if (_altManualTextSelect && StickerTextBlock != null && StickerTextBlock.IsVisible)
        {
            var textPos = e.GetPosition(StickerTextBlock);
            if (StickerTextBlock.TextLayout != null)
            {
                var hit = StickerTextBlock.TextLayout.HitTestPoint(textPos);
                int idx = hit.TextPosition;
                StickerTextBlock.SelectionStart = Math.Min(_altManualAnchorIndex, idx);
                StickerTextBlock.SelectionEnd = Math.Max(_altManualAnchorIndex, idx);
            }
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(this);
        if (_annotationPointerDip.HasValue) return;
        if (_isAnnotating && _activeAnnotationTool == ToolMode.Eraser)
        {
            Cursor = RectangleEraserCursor;
            return;
        }
        StandardCursorType cursorType = StandardCursorType.Arrow;
        var edge = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)
            ? null
            : GetWindowEdgeAt(pos, out cursorType);

        if (edge.HasValue)
        {
            Cursor = new Cursor(cursorType);
        }
        else
        {
            // 先刷新自定义取色键，再保留 Ctrl 导出／Alt 选词的光标优先级。
            // 先在窗外按键再移进来时没有 KeyDown，必须由指针路径补唤出。
            var summon = StickerGestureModifier("sticker.pickhold", KeyModifiers.Shift);
            if (e.KeyModifiers.HasFlag(summon) || ColorPickerVisible) RefreshColorPicker();
            // 按住 Ctrl：光标切换为 Hand，提示可直接拖拽导出贴图文件
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && !_isAnnotating)
            {
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
            }
            // 按住 Alt（图片贴图）：光标切换为文本选择 I-beam，提示可直接拖选文本。
            // 边缘仍优先显示 resize 光标（Alt+点击边缘的既有行为是调整窗口大小）。
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)
                && StickerImage != null && StickerImage.IsVisible && _bitmap != null)
            {
                Cursor = new Cursor(StandardCursorType.Ibeam);
                return;
            }
            if (e.KeyModifiers.HasFlag(summon)
                && StickerImage != null && StickerImage.IsVisible && _bitmap != null)
            {
                return;
            }

            // 按住 Alt（文本/Markdown 贴图）：开启临时选字（命中测试随开随用，
            // 后续按下/拖选走原生选择），光标切换 I-beam 提示可直接拖选。
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)
                && StickerImage != null && !StickerImage.IsVisible
                && !_isTextSelectable)
            {
                if (!_altTextStickerSelect)
                {
                    BeginAltTextStickerSelect(null);
                }
                Cursor = new Cursor(StandardCursorType.Ibeam);
                return;
            }

            bool onTextElement = false;

            bool handledCursor = false;

            // Check if hovering over a word region（持久"文本可选择"或 Alt 快速选词激活时）
            if ((_isTextSelectable || _altQuickSelect) && _wordRegions != null && _wordRegions.Count > 0 &&
                StickerCanvas != null && StickerCanvas.IsVisible && StickerCanvas.IsHitTestVisible)
            {
                var canvasPos = e.GetPosition(StickerCanvas);
                onTextElement = FindWordIndexAt(canvasPos) >= 0;
                Cursor = onTextElement ? new Cursor(StandardCursorType.Ibeam) : new Cursor(StandardCursorType.Arrow);
                handledCursor = true;
            }
            else if ((_isTextSelectable || _altTextStickerSelect) && StickerTextBlock != null && StickerTextBlock.IsVisible)
            {
                if (IsVisualDescendant(StickerTextBlock, e.Source as Avalonia.Visual))
                {
                    var textPos = e.GetPosition(StickerTextBlock);
                    bool isBlank = IsPointInBlankSpace(textPos.X, textPos.Y);
                    StickerTextBlock.Cursor = isBlank ? new Cursor(StandardCursorType.Arrow) : new Cursor(StandardCursorType.Ibeam);
                    Cursor = StickerTextBlock.Cursor;
                    handledCursor = true;
                }
                else
                {
                    StickerTextBlock.Cursor = new Cursor(StandardCursorType.Arrow);
                }
            }
            else if ((_isTextSelectable || _altTextStickerSelect) && StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible)
            {
                if (IsVisualDescendant(StickerMarkdownViewer, e.Source as Avalonia.Visual))
                {
                    Cursor = new Cursor(StandardCursorType.Ibeam);
                    handledCursor = true;
                }
            }

            if (!handledCursor)
            {
                // 内容越出视口 ⇒ 这一片拖动是"平移查看"而非"移动贴图"，用四向箭头把两种
                // 语义区分开（见 docs/贴图视口平移方案.md §4.1）。优先级刻意排在选词/选字
                // 之后：那几档有自己的 I-beam 语义，平移只接管"没别的事可做"的默认态。
                Cursor = CanPanContent() ? new Cursor(StandardCursorType.SizeAll) : null;
            }
        }
    }

    // --- 缩略图进入/退出切换（无动效，单帧原子应用） ---
    // 2026-08-26 起取消 150ms 过渡动画：逐帧同时变更原生窗口矩形与合成器内容
    // 两条链路难以严格同帧，动效既突兀又容易引入回打；改为在渲染帧回调内
    // 一次性应用终态（窗口矩形 + 视口同帧提交），切换干净直接。
    // 关键不变量：所有尺寸经 SnapStablePx 吸附到"往返稳定"物理像素
    // （满足 (int)((n/s)*s)==n），使 SetWindowPos、HandleResized 写回、布局
    // ArrangeSetBounds → Resize 三条链路换算结果逐像素一致——布局系统的
    // Resize 恒为空操作，从机制上杜绝窗口与布局系统互相回打。
    // 窗口位置取整误差折算进视口（亚像素 TranslateTransform），图片内容逐像素冻结。

    private bool _thumbApplyPending;
    private int _thumbApplyGeneration;

    /// <summary>在下一渲染帧内原子应用缩略图终态（窗口矩形 + 视口），单帧完成、无动效。</summary>
    private void ApplyThumbnailStateImmediate(PixelPoint endPos, double endW, double endH, double endVpX, double endVpY)
    {
        double s = SafeScaling;
        // 尺寸吸附到"往返稳定"物理像素网格（详见上方区块注释的不变量）
        int wPx = Math.Max(1, SnapStablePx(endW, s));
        int hPx = Math.Max(1, SnapStablePx(endH, s));
        double qW = wPx / s;
        double qH = hPx / s;
        int xPx = endPos.X;
        int yPx = endPos.Y;
        int gen = ++_thumbApplyGeneration;

        // 守卫保持到渲染帧应用完成，拦截期间的 SizeChanged → ApplyScale 回写
        _thumbApplyPending = true;
        _isApplyingScale = true;

        RequestAnimationFrame(_ =>
        {
            if (gen != _thumbApplyGeneration) return;

            _viewportX = endVpX;
            _viewportY = endVpY;
            UpdateTransforms();
            // 缩略图视口在本帧确定；先按终态内容检查可见面积，再原子落窗口矩形。
            ClampVisibleContentPosition(ref xPx, ref yPx,
                GetVisibleContentInWindow(wPx, hPx, s), s, GetStickerDisplayRects());

            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                // SWP_NOZORDER(0x0004) | SWP_NOACTIVATE(0x0010)：单次调用原子完成移动+缩放
                ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
            }
            // 与物理尺寸逐像素一致的托管值：HandleResized 写回相同值，布局 Resize 为空操作
            Width = qW;
            Height = qH;
            Position = new PixelPoint(xPx, yPx);

            // 诊断：目标 vs 实测 + 图片内容冻结偏差（应≈0；直接量化"进入位置偏移"）
            if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcEnd))
            {
                double imgLX = rcEnd.Left + (ShadowMargin + endVpX) * s;
                double imgLY = rcEnd.Top + (ShadowMargin + endVpY) * s;
                bool rectMatch = rcEnd.Left == xPx && rcEnd.Top == yPx &&
                                 rcEnd.Width == wPx && rcEnd.Height == hPx;
                ThumbDiag($"{(_isThumbnailMode ? "进入" : "退出")}应用：目标({xPx},{yPx}) {wPx}x{hPx}，" +
                          $"实测({rcEnd.Left},{rcEnd.Top}) {rcEnd.Width}x{rcEnd.Height}，匹配={(rectMatch ? "是" : "否")}，" +
                          $"图片冻结偏差=({imgLX - _thumbFreezeBaseX:F2},{imgLY - _thumbFreezeBaseY:F2})px，视口=({endVpX:F2},{endVpY:F2})");
            }

            _thumbApplyPending = false;
            _isApplyingScale = false;
            if (!_isThumbnailMode)
            {
                // 退出缩略图完成后恢复正常布局（视口归零、图片控件尺寸刷新）
                ApplyScale(skipWindowResize: true);
            }
        });
    }

    /// <summary>指针离开窗口：信封态保持（窗口图片+阴影之外已 WM_NCHITTEST 穿透，
    /// 指针在外层透明区活动不影响下层交互，信封无需收缩）。</summary>
    private void OnWindowPointerExited(object? sender, PointerEventArgs e)
    {
        _annotationPointerDip = null;
        UpdateAnnotationPointerFootprint();
        if (ReferenceEquals(Cursor, HiddenAnnotationCursor)) Cursor = null;
    }

    // —— 信封态鼠标穿透（WM_NCHITTEST 子类化）——
    // 信封窗口比图片大出一圈透明 fringe，透明背景默认仍命中本窗口、会挡住下层应用
    // 的鼠标点击——这是信封必须收缩回贴合窗口的唯一理由，也造成"点击→收缩（resize①）
    // →再滚→重新涨大（resize②）"两次 resize 前后脚发生＝"点击后再滚必闪"。
    // 解法：信封态下子类化窗口过程，WM_NCHITTEST 对"图片+阴影边距"以外的区域返回
    // HTTRANSPARENT（点击直接穿透到下层窗口）——信封可以长期保留，左键点击/拖动
    // 都在信封态进行（窗口移动＝图片跟随，语义正确），"点击→再滚"全程零 resize。
    // 命中穿透改用 Platform 的**每窗口**选择性穿透（WindowHelper.MakeSelectiveHitTestTransparent，
    // 录屏覆盖层同款、经实战验证）。旧实现是单一全局 owner（同时只挂一个窗口、收口就卸），而静止余量
    // 要求**每个**图片贴图常驻穿透（余量那圈点击落到下层窗口），单 owner 撑不住，故整体换成每窗口字典。
    // 穿透判据＝屏幕物理点是否落在本窗 _hitRectDip（画面＋阴影边距）内；余量/信封透明区在其外 ⇒ 穿透。
    // ⚠ 不要用 SetWindowRgn 做跨进程穿透：它裁的是**绘制像素**而非只有命中，且与 WinUI 合成不同帧——
    // 缩放期表现为阴影被切边、画面错位（2026-09-26 引入、2026-09-27 撤销）。穿透只有下面这一条路。
    private Avalonia.Rect _hitRectDip;             // 命中矩形：画面+阴影边距（窗口 DIP 坐标）
    private bool _hitTransparencyInstalled;        // 本窗穿透子类化是否在位（诊断/卸载用）
    private const int GWL_WNDPROC = -4;            // 尺寸上限子类化（InstallMaxTrackSizeLift）仍用

    /// <summary>
    /// 供 <see cref="Services.LeftClickSentinel"/>（底层鼠标钩子专用线程）无锁读取的线程安全命中测试快照。
    /// 严禁在 WH_MOUSE_LL 线程上直接读取 Avalonia UI 控件属性（如 SafeScaling、Bounds、IsVisible 等），
    /// 否则会抛出 <see cref="InvalidOperationException"/>（Call from invalid thread）。
    /// </summary>
    private sealed record WheelHitThreadSafeSnapshot(
        IntPtr Hwnd,
        bool HitTransparencyInstalled,
        bool WheelGestureActive,
        bool WheelVisualOverride,
        bool MousePenetration,
        bool IsClosed,
        bool IsVisible,
        double Scaling,
        int WinOriginX,
        int WinOriginY,
        Avalonia.Rect EffectiveHitRectDip,
        int ScreenHitLeftPx,
        int ScreenHitTopPx,
        int ScreenHitRightPx,
        int ScreenHitBottomPx);

    private volatile WheelHitThreadSafeSnapshot? _wheelHitSnapshot;

    /// <summary>
    /// 获取当前窗口真实生效的命中交互区域（窗口相对 DIP）。
    /// <para>基础区域为 <see cref="_hitRectDip"/>（画面＋阴影）。若处于标注模式且浮条（如圆角浮条、序号浮条）
    /// 存在越出画面顶部阴影边界的情况，将其外接盒联合并入，确保 Win32 物理裁剪区域覆盖全部可交互控件。</para>
    /// </summary>
    private Avalonia.Rect GetEffectiveHitRectDip()
    {
        var r = _hitRectDip;
        if (r.Width <= 0 || r.Height <= 0)
        {
            if (Bounds.Width > 0 && Bounds.Height > 0)
                r = new Avalonia.Rect(0, 0, Bounds.Width, Bounds.Height);
            else
                r = new Avalonia.Rect(0, 0, 1, 1);
        }

        if (_isAnnotating)
        {
            if (SpotlightRadiusPillHost is { IsVisible: true } rHost && rHost.Bounds.Width > 0 && rHost.Bounds.Height > 0)
            {
                var pt = rHost.TranslatePoint(new Avalonia.Point(0, 0), this);
                if (pt.HasValue)
                {
                    r = r.Union(new Avalonia.Rect(pt.Value.X, pt.Value.Y, rHost.Bounds.Width, rHost.Bounds.Height));
                }
            }

            if (CounterPillHost is { IsVisible: true } cHost && cHost.Bounds.Width > 0 && cHost.Bounds.Height > 0)
            {
                var pt = cHost.TranslatePoint(new Avalonia.Point(0, 0), this);
                if (pt.HasValue)
                {
                    r = r.Union(new Avalonia.Rect(pt.Value.X, pt.Value.Y, cHost.Bounds.Width, cHost.Bounds.Height));
                }
            }
        }

        return r;
    }

    /// <summary>
    /// 刷新命中判据快照（供 <see cref="Services.LeftClickSentinel"/> 与 <c>WM_NCHITTEST</c> 回调读取）。
    /// <para>命中区域只以快照形式存在，由 <see cref="ScreenCaptureTool.Platform.WindowHelper.MakeSelectiveHitTestTransparent"/>
    /// 逐坐标判定；不再下发 <c>SetWindowRgn</c> 物理裁剪（2026-09-27 撤销，见上方注释）。</para>
    /// </summary>
    private void UpdateHitRegion()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        double s = SafeScaling;
        var r = GetEffectiveHitRectDip();

        // 同步跨线程快照（供 LeftClickSentinel 在钩子线程无锁、无 UI 访问地安全判读）
        var win = GetWindowOriginPx();
        int sLeftPx = win.X + (int)Math.Floor(r.Left * s);
        int sTopPx = win.Y + (int)Math.Floor(r.Top * s);
        int sRightPx = win.X + (int)Math.Ceiling(r.Right * s);
        int sBottomPx = win.Y + (int)Math.Ceiling(r.Bottom * s);

        _wheelHitSnapshot = new WheelHitThreadSafeSnapshot(
            Hwnd: hwnd,
            HitTransparencyInstalled: _hitTransparencyInstalled,
            WheelGestureActive: _wheelGestureActive,
            WheelVisualOverride: _wheelVisualOverride,
            MousePenetration: _isMousePenetration,
            IsClosed: _isClosed,
            IsVisible: IsVisible,
            Scaling: s,
            WinOriginX: win.X,
            WinOriginY: win.Y,
            EffectiveHitRectDip: r,
            ScreenHitLeftPx: sLeftPx,
            ScreenHitTopPx: sTopPx,
            ScreenHitRightPx: sRightPx,
            ScreenHitBottomPx: sBottomPx);
    }

    /// <summary>本窗某**屏幕物理点**是否应正常响应（落在画面＋阴影及活跃浮条内）。穿透回调与诊断共用一份判据。</summary>
    private bool IsScreenPointInteractive(int screenX, int screenY)
    {
        double s = SafeScaling;
        var win = GetWindowOriginPx();
        double wx = (screenX - win.X) / s;
        double wy = (screenY - win.Y) / s;
        var r = GetEffectiveHitRectDip();
        return wx >= r.Left && wx <= r.Right && wy >= r.Top && wy <= r.Bottom;
    }

    /// <summary>安装本窗命中穿透（幂等）。静止余量贴图开窗即装、常驻；文本/缩略图等仅手势期装。</summary>
    private static void InstallWheelHitTransparency(StickerWindow owner)
    {
        IntPtr hwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            AppLogger.Warn("StickerWindow: 命中穿透未安装——取不到窗口句柄，透明区将吞掉鼠标点击");
            return;
        }
        bool ok = ScreenCaptureTool.Platform.WindowHelper.MakeSelectiveHitTestTransparent(
            hwnd, owner.IsScreenPointInteractive);
        owner._hitTransparencyInstalled = ok;
        owner.UpdateHitRegion();
        if (!ok)
            AppLogger.Warn($"StickerWindow: 命中穿透安装失败 hwnd={hwnd} 窗={owner.Bounds.Width:F0}x{owner.Bounds.Height:F0}DIP —— 透明区将吞掉鼠标点击");
    }

    /// <summary>卸载本窗命中穿透并还原原 WndProc（幂等）。窗口关闭、或无静止余量者收缩提交时调用。</summary>
    private static void UninstallWheelHitTransparency(StickerWindow owner)
    {
        IntPtr hwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            ScreenCaptureTool.Platform.WindowHelper.ReleaseSelectiveHitTestTransparent(hwnd);
        }
        owner._hitTransparencyInstalled = false;
        owner.UpdateHitRegion();
    }

    /// <summary>信封态命中穿透的此刻实况（供 <c>LeftClickSentinel</c> 判读，2026-09-17 加）。</summary>
    public enum WheelHitState
    {
        /// <summary>不在信封态：窗口矩形＝图片+阴影，没有透明区。</summary>
        NotInEnvelope,

        /// <summary>点在等效贴合矩形内：本窗口本该收到这次点击。</summary>
        Inside,

        /// <summary>点在矩形外、且穿透子类化**确实挂在本窗口上**：点击被正常放行给下层窗口，不是故障。</summary>
        PassThroughOk,

        /// <summary>点在矩形外、但穿透**没挂**：信封透明区正在吃点击＝正是要找的故障形态。</summary>
        PassThroughBroken,
    }

    /// <summary>
    /// 诊断用：给定屏幕物理坐标，报告本窗口此刻会不会把它放行给下层窗口。
    /// 供 <see cref="Services.LeftClickSentinel"/>（底层鼠标钩子专用线程）安全调用。
    /// ⚠ **线程安全**：本方法可能在非 UI 线程上被调用，严禁同步访问任何 Avalonia UI 控件属性，
    /// 所有几何与状态均从 <see cref="_wheelHitSnapshot"/> 及 Win32 API 读取。
    /// </summary>
    /// <param name="screenX">屏幕物理像素 X。</param>
    /// <param name="screenY">屏幕物理像素 Y。</param>
    /// <param name="detail">人类可读的实况描述（原样进现场报告）。</param>
    public WheelHitState DescribeWheelHit(int screenX, int screenY, out string detail)
    {
        var snap = _wheelHitSnapshot;
        if (snap == null)
        {
            detail = "快照未就绪（窗口尚未完成初始化）";
            return WheelHitState.NotInEnvelope;
        }

        int winX = snap.WinOriginX;
        int winY = snap.WinOriginY;
        if (snap.Hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(snap.Hwnd, out var rc))
        {
            winX = rc.Left;
            winY = rc.Top;
        }

        double s = snap.Scaling > 0 ? snap.Scaling : 1.0;
        double wx = (screenX - winX) / s;
        double wy = (screenY - winY) / s;
        var r = snap.EffectiveHitRectDip;
        bool inside = wx >= r.Left && wx <= r.Right && wy >= r.Top && wy <= r.Bottom;

        // 透明区存在的两种情形：手势/平移覆写态，或图片贴图的常驻静止余量/最大信封。
        bool transparentActive = snap.WheelVisualOverride;
        detail = "覆写态=" + (snap.WheelVisualOverride ? "是" : "否")
            + " 命中矩形=" + (transparentActive
                ? $"({r.Left:F0},{r.Top:F0}){r.Width:F0}x{r.Height:F0}DIP"
                : "不适用")
            + $" 该点窗口内DIP=({wx:F0},{wy:F0}) 在命中矩形内={(inside ? "是" : "否")}"
            + " 穿透子类化在位=" + (snap.HitTransparencyInstalled ? "是" : "否");

        if (!transparentActive) return WheelHitState.NotInEnvelope;
        if (inside) return WheelHitState.Inside;
        return snap.HitTransparencyInstalled ? WheelHitState.PassThroughOk : WheelHitState.PassThroughBroken;
    }

    // —— 贴图窗口尺寸上限（WM_GETMINMAXINFO 子类化）——
    // 内容大于屏幕时，"松手落定"要把窗口落到贴合尺寸（可超屏，见 CommitWheelGesture）。
    // 但贴图真实 HWND 的样式是 WS_CAPTION|WS_SYSMENU|WS_THICKFRAME|WS_MIN/MAXIMIZEBOX|
    // WS_CLIPCHILDREN|WS_CLIPSIBLINGS（Avalonia 一律建这一套、自己裁掉非客户区），
    // 该样式下系统按工作区算出 ptMaxTrackSize≈一屏高，SetWindowPos 请求 570x3049 会被
    // **静默夹回 570x1100**（一次性 Win32 探针实测：同套样式夹、抬起上限后完整生效），
    // 越出的近 2000px 压根不在绘制表面上＝真丢像素，且 XAML 的 MaxHeight=99999 压不过它。
    // 故常驻子类化把可落定的尺寸上限抬到既有纹理保护口径 MaxTexturePhysicalPx，
    // 结构体其余字段（最大化位置、最小跟踪尺寸）一律交回原窗口过程，不改。
    private const int WM_GETMINMAXINFO = 0x0024;
    /// <summary>MINMAXINFO 是 5 个 POINT（各 8 字节），最后一个 ptMaxTrackSize 在偏移 32。
    /// 只按偏移改这一项，其余（最大化位置/尺寸、最小跟踪尺寸）原样留给平台。</summary>
    private const int MmiMaxTrackSizeOffset = 32;
    private static readonly ScreenCaptureTool.Platform.NativeMethods.WndProcDelegate
        _maxTrackWndProc = LiftMaxTrackSizeWndProc;
    private static readonly Dictionary<IntPtr, IntPtr> _maxTrackPrevWndProcs = new();
    private IntPtr _maxTrackHwnd;   // 本窗口已装该子类化的句柄（还原按它记账，关窗后实时取会落空）

    /// <summary>安装尺寸上限子类化（幂等；须在句柄创建后调用）。必须装在信封命中穿透
    /// 子类化**之前**：两条 GWL_WNDPROC 链按后进先出还原，装反会还原成悬空指针。</summary>
    private void InstallMaxTrackSizeLift()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || _maxTrackHwnd == hwnd) return;
        IntPtr prev = ScreenCaptureTool.Platform.NativeMethods.SetWindowLongPtr(
            hwnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(_maxTrackWndProc));
        if (prev == IntPtr.Zero) return;
        _maxTrackPrevWndProcs[hwnd] = prev;
        _maxTrackHwnd = hwnd;
    }

    /// <summary>还原原窗口过程（幂等；窗口关闭时调用，防悬空窗口过程）。用记账句柄而非
    /// 实时 <c>TryGetPlatformHandle</c>：关窗时该调用可能已拿不到句柄，还原就会落空。</summary>
    private void UninstallMaxTrackSizeLift()
    {
        IntPtr hwnd = _maxTrackHwnd;
        if (hwnd == IntPtr.Zero) return;
        _maxTrackHwnd = IntPtr.Zero;
        if (_maxTrackPrevWndProcs.Remove(hwnd, out IntPtr prev) && prev != IntPtr.Zero)
            ScreenCaptureTool.Platform.NativeMethods.SetWindowLongPtr(hwnd, GWL_WNDPROC, prev);
    }

    private static IntPtr LiftMaxTrackSizeWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_maxTrackPrevWndProcs.TryGetValue(hwnd, out IntPtr prev) || prev == IntPtr.Zero)
            return IntPtr.Zero;
        if (msg == ScreenCaptureTool.Platform.NativeMethods.WM_WINDOWPOSCHANGING)
            KeepStickerBelowFloatingTools(lParam);
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            // 先让 Avalonia 按它自己的口径填满结构体，再只抬"最大跟踪尺寸"这一项
            IntPtr ret = ScreenCaptureTool.Platform.NativeMethods.CallWindowProc(
                prev, hwnd, msg, wParam, lParam);
            int limit = (int)MaxTexturePhysicalPx;
            if (Marshal.ReadInt32(lParam, MmiMaxTrackSizeOffset) < limit)
                Marshal.WriteInt32(lParam, MmiMaxTrackSizeOffset, limit);
            if (Marshal.ReadInt32(lParam, MmiMaxTrackSizeOffset + 4) < limit)
                Marshal.WriteInt32(lParam, MmiMaxTrackSizeOffset + 4, limit);
            return ret;
        }
        if (msg == 0x0047)   // WM_WINDOWPOSCHANGED：先交原窗口过程，再让呈现窗跟上 z 序与显隐
        {
            IntPtr r = ScreenCaptureTool.Platform.NativeMethods.CallWindowProc(prev, hwnd, msg, wParam, lParam);
            OnOwnerWindowPosChanged(hwnd, lParam);
            return r;
        }
        return ScreenCaptureTool.Platform.NativeMethods.CallWindowProc(prev, hwnd, msg, wParam, lParam);
    }

    // —— 拖动直出状态（指针事件直出，SetWindowPos 零延迟平移）——
    private PixelPoint _lastAppliedDragPos;

    /// <summary>应用拖动目标位置：单次 SetWindowPos 仅移动（SWP_NOSIZE|SWP_NOZORDER|
    /// SWP_NOACTIVATE = 0x0015，不触发布局/激活）。托管 Position 不手动赋值——
    /// WM_WINDOWPOSCHANGED 已让 Avalonia 同步内部状态，手动 setter 会再走一次
    /// 平台 SetWindowPos（每帧双次内核调用）。</summary>
    private void ApplyDragMove(PixelPoint target)
    {
        if (target == _lastAppliedDragPos) return;
        _lastAppliedDragPos = target;

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // 位移基准取实时窗口原点而非缓存 Position：本方法刻意不写 Position（见下方注释），
        // 缓存值恒滞后，拿它算出的 delta 会把 _wheelEnv/_wheelImg* 的屏幕坐标簿记带偏
        var before = GetWindowOriginPx();
        // 夹取要用当前**真实**宽高：`Bounds` 是 DIP 换算，与 SnapStablePx 的量化差一两个像素就会
        // 把工作区边界算错（边界上多夹一次＝多一次跳变）。
        ScreenCaptureTool.Platform.NativeMethods.RECT rc = default;
        if (hwnd != IntPtr.Zero) ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out rc);
        // 拖动与缩放共用最小可见内容规则，允许半隐藏，不能等下一格缩放再拽回完整工作区。
        int dxPx = target.X, dyPx = target.Y;
        if (rc.Width > 0 && rc.Height > 0)
        {
            double s = SafeScaling;
            ClampVisibleContentPosition(ref dxPx, ref dyPx,
                GetVisibleContentInWindow(rc.Width, rc.Height, s), s, GetStickerDisplayRects());
        }
        if (dxPx == before.X && dyPx == before.Y) return;   // 已停在线内（夹过之后没动）：零调用零簿记
        if (hwnd != IntPtr.Zero)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 分层呈现在位：交互窗与呈现窗同一批 DeferWindowPos 只改位置，呈现窗不重画
            if (!TryMoveWithPin(dxPx, dyPx, dxPx - before.X, dyPx - before.Y))
                ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    dxPx, dyPx, 0, 0, 0x0015);
            SampleDragWindowMove(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            // 系统可能在激活/Z序事务中吞掉移动请求；簿记必须跟回读的真实位置，不能跟请求值走。
            if (!ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var landed))
            {
                _lastAppliedDragPos = new PixelPoint(int.MinValue, int.MinValue);
                ForcePinResync();
                return;
            }
            if (landed.Left != dxPx || landed.Top != dyPx)
            {
                _lastAppliedDragPos = new PixelPoint(int.MinValue, int.MinValue);
                WheelDiag($"拖动落地不一致：请求({dxPx},{dyPx}) 实际({landed.Left},{landed.Top})，按实际记账并重对齐");
                ForcePinResync();
            }
            dxPx = landed.Left; dyPx = landed.Top;
        }
        else
        {
            Position = new PixelPoint(dxPx, dyPx);
        }

        // 覆写态拖动：窗口移动＝图片同步移动（Margin 为窗口内坐标），视口与图片的
        // 屏幕物理矩形簿记必须同移——ComputeFittedWindowRectPx 的贴合目标、ClampContentAxis
        // 的越出判定都依赖这些屏幕坐标，不同步会导致下次滚动误判越出/贴合位置跳变。
        // ⚠ 位移量只认**夹过之后真正落地**的值：把 `_wheelEnv*` 记到屏幕外，下一格又按工作区
        // 算回来 ⇒ 同一格反复"拽回来"（不变量 I2：簿记恒等于落地值）。
        double dX = dxPx - (double)before.X;
        double dY = dyPx - (double)before.Y;
        if (dX != 0 || dY != 0)
        {
            // 内容的屏幕矩形在任何态都随窗口同移：它（连同由它反解的 Margin）就是 offset 的
            // 绝对基准，静止态下滞后的旧值会让下一次平移起手读到过期位置。
            _wheelImgLpx += dX; _wheelImgTpx += dY;
            // 视口簿记只在覆写态被读，非该态不去动它（那组值本就归上一次落地所有）
            if (_wheelVisualOverride) { _wheelEnvL += dX; _wheelEnvT += dY; }
            UpdateHitRegion();
        }
    }

    /// <summary>
    /// 双击贴图时分发执行配置的动作（Close/Thumbnail/Copy/None）。
    /// </summary>
    private void HandleDoubleClickAction()
    {
        var settings = AppSettings.Load();
        string action = AppSettings.NormalizeStickerDoubleClickAction(settings.StickerDoubleClickAction);
        switch (action)
        {
            case "Thumbnail":
                ToggleThumbnailMode();
                break;
            case "Copy":
                OnCopyImageClick(null, new RoutedEventArgs());
                break;
            case "None":
                break;
            case "Close":
            default:
                Close();
                break;
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isCanvasMode)
        {
            if (IsCanvasTextInteraction(e.Source)) return;
            FinishCanvasResize(e);
            UpdateCanvasResizeHints(e.KeyModifiers.HasFlag(KeyModifiers.Control), e.GetPosition(this));
            _isWordSelecting = false;
            _blankAnchorPending = false;
            ScheduleSelectionToolbarUpdate();
            e.Handled = true;
            return;
        }
        // 圆角浮条上的松手同理：不结束任何图元/文本手势（那些手势本来就没被浮条起过）。
        // 序号浮条同理，另有一条要紧的：说明文字输入框的提交就挂在自己的失焦/按键上，窗口别抢。
        if (IsFromSpotlightRadiusPill(e.Source)) return;
        if (IsFromCounterPill(e.Source)) return;

        // 文本层拖动 / 旋转收尾：指针捕获挂在本窗，必须在此释放，否则松手后鼠标一动图层仍跟着走
        if (_isAnnotating && (TextTool.IsDraggingText || TextTool.IsRotatingText))
        {
            TextTool.TryHandleGlobalPointerReleased(sender, e);
            e.Handled = true;
            return;
        }

        // 图元编辑手势收尾（确有位移才由编辑器一次性写回文档、并据此提交一条撤销）
        if (Anno.IsEditingShape)
        {
            EndAnnotationShapeMove(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_isAnnotating && _annotationController != null && _annotationController.IsDrawing)
        {
            Annotations.AnnotationShape? committed = _annotationController.End(e.GetPosition(this),
                e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift));
            if (committed != null)
            {
                // 与截图侧每个 Finish* 收尾的同一动作：画完自动选中，手柄当场出现，
                // Delete / 方向键步进 / 拖手柄都无需再 Ctrl 点一次。null＝这一笔未达阈值，什么都没提交。
                ClearAllAnnotationSelections();
                SelectAnnotationShape(committed.Id);
            }
            e.Handled = true;
            return;
        }
        // 结束缩略图拖角
        if (_isDraggingThumbCorner)
        {
            EndThumbCornerDrag(e);
            return;
        }

        // 结束拖边缩放（EndEdgeResize 内部一并释放指针捕获）
        if (_isResizing)
        {
            // 重排被 ReflowMinStepPx 节流，终态可能比松手点少若干像素：这里补最后一帧落到光标
            // 实时位置。只在真实松手处补（不放 EndEdgeResize）——失活等旁路触发时光标早已跑远，
            // 补帧会凭空跳出一个尺寸。⚠ 必须早于 EndEdgeResize：离开拖边态后信封涨大通路易走
            // 居中公式，把刚钉死的对侧边挪走。
            if (_resizeReflows && !double.IsNaN(_reflowAppliedW)) ApplyEdgeResizeFrame();
            EndEdgeResize();
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            UpdateContextMenuState();
            RootBorder.ContextMenu?.Open(RootBorder);
            e.Handled = true;
            return;
        }

        if (_isCtrlDragPending)
        {
            _isCtrlDragPending = false;
        }

        if (_isDragging)
        {
            EndStickerDrag("松手");
            e.Handled = true;
        }

        if (_isWordSelecting)
        {
            _isWordSelecting = false;
            _blankAnchorPending = false;   // 空白起笔未兑现就松手＝这一次只是点空白
            e.Handled = true;
        }

        // Alt 手动选字结束：保留选区（供 Ctrl+C 复制），临时模式由下一次
        // 非 Alt 按下收尾（与图片贴图 Alt 快速选词的收尾时机一致）。
        if (_altManualTextSelect)
        {
            _altManualTextSelect = false;
            e.Handled = true;
        }

        // 左键释放：快照文本/Markdown 贴图的当前选区（原生 SelectableTextBlock
        // 失焦即 ClearSelection，后续焦点被夺走时实时选区可能已被清空，复制以快照兜底）。
        // 放在末尾——右键/关闭等提前 return 的分支不刷新快照，保留原选区的快照。
        if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left)
        {
            try
            {
                string? snap = null;
                if (StickerTextBlock != null && StickerTextBlock.IsVisible &&
                    !string.IsNullOrEmpty(StickerTextBlock.SelectedText))
                {
                    snap = StickerTextBlock.SelectedText;
                }
                else if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible &&
                         !string.IsNullOrEmpty(StickerMarkdownViewer.SelectedText))
                {
                    snap = StickerMarkdownViewer.SelectedText;
                }
                _textSelSnapshot = snap;
            }
            catch { }

            // 划词工具栏：左键松手＝这一段选区结束，此时浮条该出现（或该收起）
            ScheduleSelectionToolbarUpdate();
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isCanvasMode) return;
        // 诊断：缩略图相关状态下的所有 SizeChanged（含被守卫拦截的），观察是否有
        // 意外的窗口尺寸来源（回打会让这里出现一串与目标无关的尺寸跳变）
        if (_isThumbnailMode || _isDraggingThumbCorner || _thumbApplyPending)
        {
            string guard = _isApplyingScale ? "ApplyingScale" :
                           _thumbApplyPending ? "ApplyPending" :
                           _isDraggingThumbCorner ? "CornerDrag" : "无";
            ThumbDiag($"SizeChanged：{e.PreviousSize.Width:F2}x{e.PreviousSize.Height:F2} → " +
                      $"{e.NewSize.Width:F2}x{e.NewSize.Height:F2}（守卫:{guard}）");
        }

        // 缩略图切换/拖角逐帧改写窗口尺寸，滚轮信封手势（_wheelVisualOverride 覆盖手势期与
        // 提交前的长期保留态——此期窗口尺寸≠图片尺寸）期间窗口尺寸不代表图片，
        // 此期间禁止 ApplyScale 回写窗口/视口打架
        if (_isApplyingScale || _thumbApplyPending || _isDraggingThumbCorner ||
            _wheelGestureActive || _wheelVisualOverride || _isResizing) return;

        // 窗口尺寸变化时只同步 UI 布局到当前 _scale，不修改 _scale。
        // _scale 的修改途径只有两条：滚轮缩放 (OnRootPointerWheelChanged) 与拖边缩放
        // (ApplyEdgeResizeFrame)，两者都在改动前自行置好守卫标志，故此处不得反算。
        if (Bounds.Width > 0 && _sourcePixelWidth > 0 && _sourcePixelHeight > 0)
        {
            _isApplyingScale = true;
            try
            {
                ApplyScale();
            }
            finally
            {
                _isApplyingScale = false;
            }
        }
    }

    /// <summary>窗口左上角的屏幕物理像素坐标。优先 <c>GetWindowRect</c>：它比 Avalonia 缓存的
    /// <c>Position</c> 更实时（拖动/拖边每帧直接 <c>SetWindowPos</c> 挪窗口，缓存会滞后一帧）。</summary>
    private PixelPoint GetWindowOriginPx()
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero &&
            ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
            return new PixelPoint(rc.Left, rc.Top);
        return Position;
    }

    /// <summary>
    /// 「等效贴合矩形」：内容（图片 / 文字）可视矩形外扩一圈 <see cref="ShadowMargin"/>，窗口 DIP 坐标。
    /// 静止态它就是窗口矩形本身；信封态窗口被撑得远大于内容，该矩形＝"若此刻提交收缩，窗口会在
    /// 的位置与尺寸"。凡把 <c>Bounds</c> 当内容矩形使用的几何判断都改读这里——这样无需为了判边缘
    /// 而先在按下时收缩窗口（那一次带 cloak 的 resize 就是"每次点贴图闪一下"的根因）。
    /// </summary>
    private Avalonia.Rect GetFittedClientRectDip()
    {
        if (!_wheelVisualOverride || _wheelImgWpx <= 0 || _wheelImgHpx <= 0)
        {
            return new Avalonia.Rect(0, 0, Bounds.Width, Bounds.Height);
        }

        // 内容 Margin 是"信封内容区（窗口原点 + ShadowMargin）内坐标"，故窗口相对坐标 =
        // (图片屏幕物理位置 − 窗口物理原点) / DPI，再外扩一圈阴影即等效贴合矩形
        double s = SafeScaling;
        var win = GetWindowOriginPx();
        return new Avalonia.Rect(
            (_wheelImgLpx - win.X) / s - ShadowMargin,
            (_wheelImgTpx - win.Y) / s - ShadowMargin,
            _wheelImgWpx / s + ShadowMargin * 2,
            _wheelImgHpx / s + ShadowMargin * 2);
    }

    private WindowEdge? GetWindowEdgeAt(Avalonia.Point pos, out StandardCursorType cursor)
    {
        // 热区基准＝等效贴合矩形而非 Bounds：信封态下窗口边缘落在透明 fringe（HTTRANSPARENT，
        // 指针事件根本到不了本窗口），按 Bounds 判会让可见内容的四边永远取不到缩放把手
        var box = GetFittedClientRectDip();
        double x = pos.X - box.X;
        double y = pos.Y - box.Y;
        double w = box.Width;
        double h = box.Height;

        // Resize border hit thickness (DIP)
        const double border = 20.0;
        cursor = StandardCursorType.Arrow;

        if (x < 0 || y < 0 || x > w || y > h) return null;

        bool isLeft = x < border;
        bool isRight = x > w - border;
        bool isTop = y < border;
        bool isBottom = y > h - border;

        if (isTop && isLeft)
        {
            cursor = StandardCursorType.TopLeftCorner;
            return WindowEdge.NorthWest;
        }
        if (isTop && isRight)
        {
            cursor = StandardCursorType.TopRightCorner;
            return WindowEdge.NorthEast;
        }
        if (isBottom && isLeft)
        {
            cursor = StandardCursorType.BottomLeftCorner;
            return WindowEdge.SouthWest;
        }
        if (isBottom && isRight)
        {
            cursor = StandardCursorType.BottomRightCorner;
            return WindowEdge.SouthEast;
        }
        if (isTop)
        {
            cursor = StandardCursorType.SizeNorthSouth;
            return WindowEdge.North;
        }
        if (isBottom)
        {
            cursor = StandardCursorType.SizeNorthSouth;
            return WindowEdge.South;
        }
        if (isLeft)
        {
            cursor = StandardCursorType.SizeWestEast;
            return WindowEdge.West;
        }
        if (isRight)
        {
            cursor = StandardCursorType.SizeWestEast;
            return WindowEdge.East;
        }

        return null;
    }

    private bool IsPointInBlankSpace(double x, double y)
    {
        if (StickerTextBlock == null) return true;

        var textLayout = StickerTextBlock.TextLayout;
        if (textLayout == null) return false;

        var textLines = textLayout.TextLines;
        if (textLines == null || textLines.Count == 0) return false;

        double currentY = 0;
        foreach (var line in textLines)
        {
            double lineHeight = line.Height;
            if (y >= currentY && y < currentY + lineHeight)
            {
                if (x > line.WidthIncludingTrailingWhitespace)
                {
                    return true;
                }
                return false;
            }
            currentY += lineHeight;
        }

        if (y >= currentY) return true;

        return false;
    }

    private bool IsVisualDescendant(Avalonia.Visual? ancestor, Avalonia.Visual? child)
    {
        while (child != null)
        {
            if (child == ancestor) return true;
            child = child.GetVisualParent() as Avalonia.Visual;
        }
        return false;
    }

    /// <summary>标注期间的滚轮分派（转发给共享会话的那张八档表，截图/录屏共用同一份）：
    /// 先微调选中图元、再调工具默认，缩放永远排在最后。返回 true＝本次滚轮已被标注消费，
    /// 调用处就地 return；返回 false＝没选工具，交还给"自动定稿退出 + 缩放贴图"。</summary>
    private bool TryHandleAnnotationWheel(PointerWheelEventArgs e)
        => _isAnnotating && Anno.TryHandleWheel(e);


    private void OnRootPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (HandleCanvasWheel(e)) { BringStickerToFront(); return; }
        // 无条件首行日志：确认滚轮事件是否真的到达本窗口（不受下方状态拦截影响）
        WheelDiag("滚轮抵达 locked=" + _isLocked + " pen=" + _isMousePenetration + " thumb=" + _isThumbnailMode + " isImg=" + (StickerImage != null && StickerImage.IsVisible && _bitmap != null));
        if (_isLocked || _isMousePenetration) return;
        BringStickerToFront();
        // Ctrl+滚轮＝调贴图不透明度（2026-09-29）。缩略图下同样可调；标注中不接管——
        // 那里 Ctrl+滚轮属于工具参数分派表（AnnotationEditSession.TryHandleWheel），语义不能被抢。
        if (!_isAnnotating && TryHandleOpacityWheel(e)) return;
        if (_isThumbnailMode) return;
        // 圆角浮条上的滚轮＝调圆角（±1 档）。与下面文本层那条同理：本窗口滚轮走 Tunnel 相位，
        // 故这里必须显式让路——只 return 不置 Handled，事件继续走到浮条自身。
        if (IsFromSpotlightRadiusPill(e.Source)) return;
        // 序号浮条上没有滚轮语义，但指针压在条上滚一下不该把贴图缩放掉——让路即可，两侧都不消费。
        if (IsFromCounterPill(e.Source)) return;
        // 滚轮落在文本层上＝改字号，不是缩贴图。本窗口滚轮走 Tunnel 相位（截图侧是冒泡，图层自身的
        // root/TextBox 处理器天然先跑），故这里必须显式让路：只 return 不置 Handled，事件继续走到
        // 图层自身，由它按截图口径改 ±2 字号；否则"在文字上滚一下"会直接退出标注并缩放贴图。
        if (_isAnnotating && TextTool.FindTextInfoFromSource(e.Source) != null) return;
        // 标注中的滚轮先走截图同款分派表（微调选中图元 → 调当前工具参数）。命中一律就地消费：
        // 正在画一笔、或还选着工具时，滚轮既不作废笔迹、也不退出标注，更不跟着缩放贴图。
        if (TryHandleAnnotationWheel(e))
        {
            e.Handled = true;
            return;
        }
        // 没选工具了＝"画完了，去干别的"：先定稿退出（成品此刻烘焙进贴图、图层收起）再缩放。
        FinalizeAnnotationForGeometryChange("滚轮缩放");
        bool wheelIsImage = StickerImage != null && StickerImage.IsVisible && _bitmap != null;
        // 文本/Markdown 贴图同样走 Margin 覆写通路：文字层（TextViewbox）与图片层同构，且文字
        // 按最终变换矢量重画、放大不糊，也不丢选中态（见 HandleWheelZoomEnvelope 头注释第 5 点）。
        // 唯一例外——Markdown 的两帧高度捕获未完成：那期间窗口高度归布局所有、
        // TextViewbox.Height 刻意不设，进取覆写通路会钉住一个错误尺寸，故退回逐格 resize。
        bool wheelIsText = TextViewbox != null && TextViewbox.IsVisible;
        bool wheelEnvelope = wheelIsImage || (wheelIsText && !_needsMarkdownHeightCapture);
        WheelDiag("滚轮入口 路径=" + (wheelIsImage ? "图片覆写" : wheelEnvelope ? "文本覆写" : "文本resize")
                  + " scale=" + _scale.ToString("F3"));

        // —— 合帧入队（2026-09-16）——
        // 事件侧只累积缩放比与最新锚点，缩放落地挪进渲染帧回调（与拖边 ScheduleEdgeResizeRaf 同构）。
        // 高回报率滚轮一物理格连发 5~20 个事件，逐事件执行整条缩放链
        // （读矩形→改八层几何→算贴合窗口矩形→落两条日志）就是"卡"的主来源。
        e.Handled = true;
        if (e.Delta.Y == 0) return;
        _wheelPendingRatio *= WheelZoomRatio(e.Delta.Y, e.KeyModifiers);
        // —— 缩放锚点：小幅移动不追、明显挪开才换（2026-09-23 二次修正）——
        // 第一版把锚点"一次手势内完全冻结"，治好了"画面跟着指针跑"，却留出另一个洞：手势的
        // 结束只由失活/快捷键触发，于是鼠标挪开了、或者隔了一会儿再滚，锚点仍是很久以前那个点，
        // 画面就被硬拽去满足那个旧位置 ⇒ 用户报的"鼠标停在上面滚，经常偏移"。
        // 现在按屏幕物理像素比距离：10px 以内的抖动沿用旧锚点（不追指针），超过就认新的点。
        // 比较必须用屏幕坐标——缩放中窗口自己会挪，窗口相对坐标不可靠（同 ApplyDragMove 的簿记）。
        UpdateWheelGestureAnchor(e);
        _wheelPendingAnchorDip = e.GetPosition(this);
        _wheelPendingFallback = !wheelEnvelope;
        ScheduleWheelApplyRaf();
    }

    // 精细修饰键来自快捷键目录；Ctrl透明度已在入口优先分派，普通滚轮步长保持。
    // 画布原先按完整 Delta 缩放，继续保留；精细档也保留高精度滚轮的小数位移。
    private double WheelZoomRatio(double delta, KeyModifiers modifiers, bool continuous = false)
    {
        var fineModifier = StickerGestureModifier("sticker.finezoom", KeyModifiers.Alt);
        bool fine = modifiers.HasFlag(fineModifier)
            && (modifiers & ~(fineModifier | KeyModifiers.Control)) == 0;
        return Math.Pow(fine ? 1.01 : 1.1, fine || continuous ? delta : Math.Sign(delta));
    }

    // 普通贴图和画布共享屏幕锚点的小幅抖动过滤，窗口自己移动时不追相对坐标。
    private void UpdateWheelGestureAnchor(PointerWheelEventArgs e)
    {
        var originPx = GetWindowOriginPx();
        var dipNow = e.GetPosition(this);
        double anchorSx = originPx.X + dipNow.X * SafeScaling;
        double anchorSy = originPx.Y + dipNow.Y * SafeScaling;
        if (double.IsNaN(_wheelGestureAnchorPx.X) ||
            Math.Abs(anchorSx - _wheelGestureAnchorPx.X) + Math.Abs(anchorSy - _wheelGestureAnchorPx.Y) > AnchorRereadPx)
        {
            _wheelGestureAnchorPx = new Avalonia.Point(anchorSx, anchorSy);
            // 换了锚点，正在跑的补帧就没有正确的起点了（它的连续性是"绕同一个点"证出来的）
            // ⇒ 就地结束补帧、视觉对齐到终态，最多让人看到一次 ≤10% 的完成，不会看到乱窜。
            StopZoomEase();
        }
    }

    /// <summary>调度滚轮应用合帧：每渲染帧至多应用一次累积缩放。调度时快照 <c>_wheelGen</c>，
    /// 回调里比对——期间发生过提交收口（<see cref="CommitWheelGesture"/> 会 ++）则本帧作废，
    /// 过期事件不得把刚缩回的窗口再撑开。<see cref="ApplyWheelZoomAccumulated"/> 每次应用也 ++，
    /// 保证"应用之后新到达的事件"必然重新排帧，不会搭上空转的旧回调。</summary>
    private void ScheduleWheelApplyRaf()
    {
        if (_wheelApplyRafScheduled) return;
        _wheelApplyRafScheduled = true;
        int gen = _wheelGen;
        RequestAnimationFrame(_ =>
        {
            _wheelApplyRafScheduled = false;
            if (_isClosed || gen != _wheelGen) return;
            ApplyWheelZoomAccumulated();
        });
    }

    /// <summary>渲染帧内应用累积的滚轮缩放。先摘除累积器再执行（防应用过程中触发的
    /// 提交/换图等路径重入）。锁定/穿透/缩略图若是在入队之后才发生的，本帧直接丢弃累积——
    /// 与旧版"事件到达即被入口拦截"同语义。</summary>
    private void ApplyWheelZoomAccumulated()
    {
        double ratio = _wheelPendingRatio;
        var anchor = _wheelPendingAnchorDip;
        bool fallback = _wheelPendingFallback;
        _wheelPendingRatio = 1.0;
        _wheelGen++;
        if (ratio == 1.0) return;
        if (_isLocked || _isMousePenetration || _isThumbnailMode) return;

        if (_isCanvasMode)
        {
            var origin = GetWindowOriginPx();
            ZoomCanvasWindow(ratio, new Avalonia.Point((_wheelGestureAnchorPx.X - origin.X) / SafeScaling,
                (_wheelGestureAnchorPx.Y - origin.Y) / SafeScaling));
            return;
        }

        // —— 缩放帧耗时埋点（2026-09-22 立；2026-09-23 改采样点）——
        // 旧写法把秒表夹在"本格应用"的首尾，只量得到托管这一段（0.06ms 量级），**改窗口尺寸、
        // 布局、阴影 30 圈几何重画全在 RAF 回调之后的渲染阶段，落在表外**：于是 269 帧全部报
        // "阴影几何重建=0"，而阴影缓存键含宽高、缩放每格必失配，那 0 是假阴性不是命中。
        // 现在增量一律采在"相邻两格的边界"上（跨过整段渲染），并加记格与格的间隔＝真实帧节奏
        // （渲染卡住会被它抓到）与 gen2 回收次数（用来判定卡顿是不是 GC）。Skia 光栅化与 present
        // 仍在渲染线程，托管侧量不到。⚠ 仅诊断：Debug 构建才落盘（AppLogger.FileLoggingEnabled）。
        long nowStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        long shadowNow = Controls.StickerShadowLayer.GeometryRebuildCount;
        int gc2Now = System.GC.CollectionCount(2);
        double gapMs = -1, shadowDelta = 0;
        if (_wheelLastNotchStamp != 0)
        {
            gapMs = System.Diagnostics.Stopwatch.GetElapsedTime(_wheelLastNotchStamp, nowStamp).TotalMilliseconds;
            shadowDelta = shadowNow - _wheelShadowSample;
            _wheelGapMsTotal += gapMs;
            if (gapMs > _wheelGapMsMax) _wheelGapMsMax = gapMs;
            if (gapMs > 25.0) _wheelGapOver25ms++;   // 60Hz 一帧 16.7ms ⇒ >25ms 就是至少掉一帧
            _wheelShadowRebuilds += (long)shadowDelta;
            _wheelGc2Delta += gc2Now - _wheelGc2Sample;
        }
        _wheelLastNotchStamp = nowStamp;
        _wheelShadowSample = shadowNow;
        _wheelGc2Sample = gc2Now;

        if (fallback)
        {
            ApplyWheelResizeFallback(anchor, ratio);
        }
        else
        {
            HandleWheelZoomEnvelope(_wheelGestureAnchorPx.X, _wheelGestureAnchorPx.Y, ratio);
        }

        double frameMs = System.Diagnostics.Stopwatch.GetElapsedTime(nowStamp).TotalMilliseconds;
        _wheelFrameCount++;
        _wheelFrameMsTotal += frameMs;
        if (frameMs > _wheelFrameMsMax) _wheelFrameMsMax = frameMs;
        // ⚠ 每格必须自带「锚点＋内容矩形＋簿记视口」三件套（2026-09-23 补）：只记图片尺寸的话，
        // "画面随指针跑"与"锚点公式本来就该这么挪"在日志里长得一模一样，无从区分。
        // 视口取簿记值 `_wheelEnv*` 而不是再问一次系统——顺带验证 I2「只记真实落地的值」是否成立。
        WheelDiag($"缩放帧#{_wheelFrameCount} 应用={frameMs:F2}ms 距上格={(gapMs < 0 ? double.NaN : gapMs):F1}ms "
                  + $"阴影几何重建={shadowDelta:F0} "
                  + $"图片={StickerImage.Width:F0}x{StickerImage.Height:F0} 源图={_sourcePixelWidth}x{_sourcePixelHeight}"
                  + $" scale={_scale:F3} 栅格档={_gestureRasterScale:F3} 倍率={_gestureV:F3}"
                  + $" 通路={(fallback ? "resize兜底" : "覆写")}"
                  + $" 冻结锚点px=({_wheelGestureAnchorPx.X:F0},{_wheelGestureAnchorPx.Y:F0})"
                  + $" 内容({_wheelImgWpx:F0}x{_wheelImgHpx:F0})@({_wheelImgLpx:F0},{_wheelImgTpx:F0})"
                  + $" 视口({_wheelEnvW:F0}x{_wheelEnvH:F0})@({_wheelEnvL:F0},{_wheelEnvT:F0})");
        SchedulePinWheelSettle();   // 分层呈现档：滚轮停下即自动收口（每格重置计时）
    }

    // —— 缩放帧采证累计（每段手势结算时汇总落一条，见 FlushWheelFrameStats）——
    private int _wheelFrameCount;
    private double _wheelFrameMsTotal, _wheelFrameMsMax;
    private long _wheelShadowRebuilds;
    /// <summary>本段手势里"真排版"（<c>ApplyScale</c> 改各层宽高）落在第几格、共几次。
    /// 冻结栅格通路的目标是**每 2~3 格一次**；若它等于格数，说明冻结没生效或被某条通路每次都打断。</summary>
    private long _wheelRelayouts;
    // 跨格采样基准（0 / -1 ＝ 本段还没采过样，第一次只立基准、不计增量）
    private long _wheelLastNotchStamp, _wheelShadowSample = -1;
    private int _wheelGc2Sample = -1, _wheelGc2Delta, _wheelGapOver25ms;
    private double _wheelGapMsTotal, _wheelGapMsMax;
    /// <summary>补帧循环自己的**每帧节奏**采证（2026-09-23 加，"抖＝掉拍"这条判据的直接证据）。
    /// 为什么不能用"格间隔"：它量的是相邻两格滚轮的间隔＝**用户转滚轮的速度**，与掉帧无关（本文件已
    /// 因此废掉一个指标）。补帧的 RAF 回调由合成器出帧驱动 ⇒ **两次回调之间的 dt 就是这一帧的真实
    /// 节拍**：60Hz 下应恒在 ~16.7ms，跑到 33/50ms 就是那一帧没赶上＝用户看到的"跳着长大"。
    /// 只在 <see cref="TickZoomEase"/> 里累加，故 `ZoomEaseMs=0`（不开补帧）时这组数恒为 0。</summary>
    private int _easeFrameCount, _easeOver20ms;
    private double _easeMsTotal, _easeMsMax;

    /// <summary>把本段缩放手势的帧耗时采证汇总成一条日志并复位。由 <see cref="CommitWheelGesture"/>
    /// 在**所有**出口之前调用一次（含"矩形未变"那条早返回），故一段手势恰好汇总一次。
    /// 无帧数据时静默返回，不产生噪声行。</summary>
    private void FlushWheelFrameStats(string trigger)
    {
        if (_wheelFrameCount <= 0) return;
        int gaps = _wheelFrameCount - 1;
        WheelDiag($"缩放段采证[{trigger}]：帧数={_wheelFrameCount} 应用累计={_wheelFrameMsTotal:F1}ms "
                  + $"应用均值={_wheelFrameMsTotal / _wheelFrameCount:F2}ms 应用最大={_wheelFrameMsMax:F2}ms "
                  + $"｜格间隔均值={(gaps > 0 ? _wheelGapMsTotal / gaps : 0):F1}ms 最大={_wheelGapMsMax:F1}ms "
                  + $"格间隔>25ms={_wheelGapOver25ms}次 阴影几何重建={_wheelShadowRebuilds}次 gen2回收={_wheelGc2Delta}次 "
                  + $"｜补帧帧数={_easeFrameCount} 每帧dt均值={(_easeFrameCount > 0 ? _easeMsTotal / _easeFrameCount : 0):F1}ms "
                  + $"最大={_easeMsMax:F1}ms 超20ms(60Hz掉拍)={_easeOver20ms}次 "
                  + $"｜改窗={_fitResizes}次 SetWindowPos均值={(_fitResizes > 0 ? _fitSetPosMsTotal / _fitResizes : 0):F2}ms 最大={_fitSetPosMsMax:F2}ms 累计={_fitSetPosMsTotal:F1}ms "
                  + $"｜终态图片={StickerImage.Width:F0}x{StickerImage.Height:F0} "
                  + $"真排版={_wheelRelayouts}次/{_wheelFrameCount}格 终态倍率={_gestureV:F3}"
                  + TakePinStats());
        _wheelFrameCount = 0;
        _wheelFrameMsTotal = 0;
        _wheelFrameMsMax = 0;
        _wheelShadowRebuilds = 0;
        _wheelRelayouts = 0;
        _easeFrameCount = 0;
        _fitResizes = 0;
        _fitSetPosMsTotal = 0;
        _fitSetPosMsMax = 0;
        _easeOver20ms = 0;
        _easeMsTotal = 0;
        _easeMsMax = 0;
        _wheelLastNotchStamp = 0;
        _wheelShadowSample = _wheelGc2Sample = -1;
        _wheelGc2Delta = _wheelGapOver25ms = 0;
        _wheelGapMsTotal = 0;
        _wheelGapMsMax = 0;
    }

    /// <summary>兜底（Markdown 高度捕获期）：逐格原子 resize + cloak。合帧后每渲染帧至多一次，
    /// 与覆写通路的区别只在"尺寸怎么算"，不改变"逐格 resize + cloak"的既有语义。</summary>
    /// <param name="mousePos">入队时最后一格的窗口坐标（DIP）。</param>
    /// <param name="stepRatio">累积缩放比（合帧产物，等价于逐格 ×1.1 的连乘）。</param>
    private void ApplyWheelResizeFallback(Avalonia.Point mousePos, double stepRatio)
    {
        // 兜底通路（Markdown 高度捕获期）走的是"逐格真 resize + cloak"，与补帧互斥：
        // 若上一格还留着补帧状态，先断掉，否则 cloak 会盖住一个半途缩放的画面。
        StopZoomEase();
        // 以鼠标位置为缩放锚点：缩放后平移窗口，使指针下的图片内容点仍位于指针之下
        double oldScale = _scale;
        _scale = Math.Clamp(_scale * stepRatio, 0.2, GetMaxScale());
        if (_scale == oldScale) return;   // 已在缩放边界：尺寸不变，不做无谓的窗口平移

        _isApplyingScale = true;
        try
        {
            ApplyScale(skipWindowResize: true);

            double s = SafeScaling;
            double oldW = Math.Max(20.0, Math.Round(_sourcePixelWidth * oldScale) / s);
            double oldH = Math.Max(20.0, Math.Round(_sourcePixelHeight * oldScale) / s);
            double width = Math.Max(20.0, Math.Round(_sourcePixelWidth * _scale) / s);
            double height = Math.Max(20.0, Math.Round(_sourcePixelHeight * _scale) / s);

            // 指针相对当前图片内容的位置比例（内容区起点 = RootGrid Margin = ShadowMargin）。
            // 指针在阴影区（贴图外一圈）时钳制到最近边缘，退化为边缘锚点。
            double fx = Math.Clamp((mousePos.X - ShadowMargin) / oldW, 0, 1);
            double fy = Math.Clamp((mousePos.Y - ShadowMargin) / oldH, 0, 1);

            // 缩放后同一内容点的新窗口坐标；窗口平移量 = 指针位置 − 新内容点位置
            double dx = mousePos.X - (ShadowMargin + fx * width);
            double dy = mousePos.Y - (ShadowMargin + fy * height);

            // 尺寸吸附"往返稳定"物理像素（与 ResizeWindowTopLeft 同机制，杜绝布局回打）
            int cxPx = SnapStablePx(width + ShadowMargin * 2, s);
            int cyPx = SnapStablePx(height + ShadowMargin * 2, s);
            int xPx = Position.X + (int)Math.Round(dx * s);
            int yPx = Position.Y + (int)Math.Round(dy * s);
            ClampVisibleContentPosition(ref xPx, ref yPx,
                GetVisibleContentInWindow(cxPx, cyPx, s), s, GetStickerDisplayRects());

            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WheelDiag("文本resize(fallback): 窗口→" + cxPx + "x" + cyPx + " 位移(" + (xPx - Position.X) + "," + (yPx - Position.Y) + ")");
            if (hwnd != IntPtr.Zero)
            {
                // cloak：resize 瞬间 DWM 不把旧帧拉伸贴合新窗口尺寸（经典连续 resize 伪影）
                bool sizeChanged = !ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcCur)
                                   || rcCur.Width != cxPx || rcCur.Height != cyPx;
                if (sizeChanged) SetWheelResizeCloak(true);
                // 单次 SetWindowPos 原子完成"改尺寸+挪位置"。SWP_NOZORDER(0x0004)|SWP_NOACTIVATE(0x0010)
                ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, cxPx, cyPx, StickerStyling.ResizeSwpFlags);
                // 落地校验（2026-09-16，与 ApplyFittedWindowRect/ApplyCommitRect 同一口径）：
                // 被静默忽略时按真实矩形回写托管几何——否则 Width/Height/Position 记的是请求值，
                // 与真实窗口分叉，下一格的位移基准（Position.X/Y）就叠在错误起点上继续放大。
                if (ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcSet))
                {
                    xPx = rcSet.Left; yPx = rcSet.Top; cxPx = rcSet.Width; cyPx = rcSet.Height;
                }
                if (sizeChanged) ScheduleWheelUncloak();
                // 与物理尺寸逐像素一致的托管值：HandleResized 写回相同值，布局 Resize 为空操作
                Width = cxPx / s;
                Height = cyPx / s;
                Position = new Avalonia.PixelPoint(xPx, yPx);
            }
            else
            {
                ResizeWindowTopLeft(width, height);
            }
        }
        finally
        {
            _isApplyingScale = false;
        }

        SaveArchiveSnapshot();
    }

    // =====================================================================
    // 滚轮缩放／拖边手势：窗口＝内容（2026-09-23 去信封阶段 2，见 docs/贴图去信封方案.md）
    // =====================================================================
    // 本节原先是一整套"零 resize 信封"：手势期间窗口一次性涨到内容的 3 倍（9 倍面积、8/9 透明），
    // 靠"窗口矩形纹丝不动"绕开 DWM 的旧帧拉伸伪影。该前提已被本机实测**证伪**——
    // P0 取证（02_构建工具/自测/ResizeFlickerProbe + uia-diag/probe19_resize_ghost.py，~95fps 逐帧抓屏）
    // 在 6 组配置 × 20 次连续 resize 里没抓到一帧 STRETCH 鬼影，而"手工把旧帧拉伸铺进新矩形"的
    // 阳性对照照常命中判据（判据本身有效）。真实被看到的是旧画面晚一格（oldflat）与刚露出的一圈
    // 未绘制边缘（hole），两者与信封并存时同样发生、且与 CompositionMode 无关。
    // 信封省下的那次 resize 从来不是病，它留下的只有代价：表面涨 9 倍 ⇒ 按实测 0.6ms/MPx，
    // 4K 封顶态一帧 6.05ms（＝16.7ms 预算的 36%），正是用户报的"4K 下一卡一卡、缩小就流畅"。
    //
    // 现在的模型一句话：**图片多大，窗口就多大**。
    //   1. 每格滚轮／每帧拖边：内容尺寸归 ApplyScale，窗口矩形归 ComputeFittedWindowRectPx
    //      （内容 + 两侧 ShadowMargin），两者在同一渲染帧内一次原子 SetWindowPos 提交。
    //      逐格 resize 从"要规避的病"变成常态，前提就是上面那条实测结论。
    //   2. 图片统一使用完整屏幕边界（含任务栏）；文本排版继续使用工作区。图片窗口表面封顶屏幕尺寸，
    //      显示范围由统一图片矩形投影。夹小的部分**不丢像素**：内容位置是锚点算出的真值，窗口装不下时
    //      越出量由相机 offset（各层共用的 Margin）承载、拖动即可查看（阶段 1 已与信封解耦）。
    //      这是与旧模型最关键的分野——旧模型里"窗口矩形就是绘制表面"，夹小＝真丢，所以才需要
    //      ×3 预涨、封顶居中、以及把窗口落定成可超屏的贴合矩形；这三条现在全部作废。
    //   3. 手势每帧与松手提交共用**同一条**矩形公式 ⇒ 松手那一帧窗口本就在终态，
    //      "提交时整窗 resize（外加 cloak 隐匿）"这一类闪烁从结构上消失。
    //   4. 提交时机一字不改：悬停、按下任何键都不收口，仅快捷键／切缩略图／窗口失活／适配屏幕
    //      才提交；内容仍大于视口时保留覆写态（它承载的正是平移 offset）。
    //      ⚠ 需要边缘几何的场合一律读 GetFittedClientRectDip，不得退回"先 commit 再用 Bounds"
    //      （按下即收缩＝点击闪烁，且那一帧 Bounds 还是旧矩形，边缘照样判不出）。
    //   5. 文本/Markdown：窗口尺寸平时归布局所有（SizeToContent），手势期临时切 Manual 由本通路
    //      独占 Width/Height，提交时原档交还（Markdown 捕获完成后本就是 Manual，不需交还）。
    //      例外仅一条：Markdown 两帧高度捕获未完成时不进本通路，走 ApplyEdgeResizeTextFrame。
    // 鼠标锚定：图片内容点钉在指针下（每轴按实际显示比率外推；指针落在阴影区时退化为就近边缘锚点）。
    // 窗口被工作区夹住、装不下整块内容时，内容按**最小位移**收回视口（ClampContentIntoViewport），
    // 越出的那一侧靠平移查看——刻意不居中：居中会把鼠标下那个点整块挪走。
    private bool _wheelGestureActive;          // 手势进行中
    private bool _wheelVisualOverride;         // 手势视觉覆写已应用（几何由各层 Margin 驱动，含平移常驻态）
    private bool _wheelPosConstrainedLogged;   // "内容收回视口"诊断已落盘（上升沿去重）
    private int _wheelGen;                     // 手势代数（每滚一格/每次提交 +1，作废过期 RAF 提交；
                                               // 消费处见 ScheduleWheelApplyRaf，2026-09-16 起真正生效）
    // —— 滚轮合帧（2026-09-16）——
    // 高回报率滚轮（游戏滚轮/罗技无极一物理格可连发 5~20 个事件）逐事件执行整条缩放链
    // 会把 UI 线程打满（观感"卡"）。事件侧只累积缩放比与最新锚点，真正应用每渲染帧至多
    // 一次。数学等价：同一锚点下 ×k1、×k2 两格复合 ＝ 一次 ×(k1·k2)，且取整次数更少、
    // 量化误差单调不增。提交语义与触发集合完全不变（合帧只压应用频率，不引入空闲计时器——
    // 2026-09-02 红线"禁时间门"针对的是提交收口，与此无关）。
    private bool _wheelApplyRafScheduled;      // 应用合帧 RAF 已排（去重）
    private double _wheelPendingRatio = 1.0;   // 未应用的累计缩放比
    private Avalonia.Point _wheelPendingAnchorDip; // 最后一格的窗口相对 DIP 锚点（兜底通路用）
    /// <summary>本段缩放的锚点（**屏幕物理 px**）。NaN＝尚未采集，下一格滚轮即采。
    /// 采集/换点的规则写在 <see cref="OnRootPointerWheelChanged"/> 里那段注释。</summary>
    private Avalonia.Point _wheelGestureAnchorPx = new(double.NaN, double.NaN);
    /// <summary>锚点重采阈值（屏幕物理 px，曼哈顿距离）：鼠标在这范围内动，锚点不动（画面不追指针）；
    /// 超过就认新点。10px 在 150% 屏上约合 7 DIP——小于这个量级的抖动人是察觉不到的。</summary>
    private const double AnchorRereadPx = 10.0;
    private bool _wheelPendingFallback;        // 入队时判定的通路：true＝逐格 resize 兜底
    // 手势期间图片目标屏幕物理矩形（每格更新；窗口矩形由它算出，Margin 由它反解）
    private double _wheelImgLpx, _wheelImgTpx, _wheelImgWpx, _wheelImgHpx;
    /// <summary>视口（＝窗口内容区）当前**真实**落地的屏幕物理矩形。只由
    /// <see cref="ApplyFittedWindowRect"/> / <see cref="CommitWheelGesture"/> 在窗口矩形落地后写入，
    /// 别处不得改写（不变量 I2）。阶段 2 起不再有"期望尺寸"这一层：目标矩形每帧现算，
    /// 与真实矩形一致就不动窗口，不存在两套簿记可分叉。</summary>
    private double _wheelEnvL, _wheelEnvT, _wheelEnvW, _wheelEnvH;
    // =====================================================================
    // 视口平移（相机 offset）—— 2026-09-22 起，见 docs/贴图视口平移方案.md
    // =====================================================================
    // 背景：绘制成本随**窗口表面面积**线性增长（实测约 0.6ms/MPx，见方案 §2），而长图贴图
    // 的窗口实测 1232x12032＝14.8MPx＝7.4 个屏幕，每帧都在画整张表面、屏幕上只看得见 2MPx。
    // 治法不是裁剪源矩形（实测零收益，方案附表已否证），而是**让窗口不再超屏**，内容靠平移查看。
    //
    // ⚠ 本组不新增渲染机制：内容"在窗口内的偏移"本就由 SetWheelGestureVisual 一处写 8 层 Margin
    // （第 9 层 StickerTextHost 由 SyncTextHostGeometry 配对），平移＝继续用它。故既有的
    // 「信封态视觉层禁用 Stretch」「标注层与图片 Margin 成对」等不变量原样成立，零新增同步点。
    // ⚠ 也**不能**复用缩略图那套 TranslateTransform：它只挂了 3 层（见 _imageTransform 一族），
    // 棋盘格等层没挂，缩略图为此干脆把棋盘格藏掉（见 UpdateAlphaBackingLayer 注释）。
    //
    // 相机语义：scale 沿用 _scale；offset 即"内容左上角相对视口左上角的偏移"，而
    // _wheelImgLpx/_wheelImgTpx（内容屏幕物理矩形）已经是它的等价表示——手势期 Margin 正是由
    // 它反解出来的。故本阶段**不引入第二套坐标**，只补"按视口钳制 offset"与"是否可平移"两个判据，
    // 让后续阶段（平移手势 / 窗口不再超屏）都落在同一套簿记上。
    //
    // 2026-09-23 阶段 1（去信封，docs/贴图去信封方案.md §3）：这两个判据的**取数口径**改成活状态
    // 访问器 GetViewportRectPx / GetContentRectPx（视口＝窗口矩形内缩一圈阴影；内容＝排版出来的
    // 那一块的实际位置与尺寸），不再以 _wheelVisualOverride 为前提。offset 的载体一字未改（仍是
    // 各层共用的 Margin，仍由 SetWheelGestureVisual 一处写），故坐标仍是这一套、不新增同步点。

    /// <summary>拖动起手时冻结"本次拖动是平移内容还是移动窗口"，并留下平移基线。
    /// 两处 <c>_isDragging = true</c> 的起手点必须都调它，口径才不会分叉。
    /// <paramref name="screenPt"/> 为起手时的光标屏幕物理坐标（与 <c>_dragStartScreenPoint</c> 同源）。</summary>
    private void BeginDragSemantics(Avalonia.PixelPoint screenPt)
    {
        // 拖动用的是"Margin＝内容在窗口里的偏移"这套账，而冻结栅格期画面对它还有一个补偿倍率
        // （画面＝Margin 摆好后又被 RootGrid 的变换放大过）。留着它按下拖动，平移会按 v 倍速率走。
        // 起手先终止补帧并把各层落回真排版（顺带把画面推到本格目标位置，最多 1 格 10%，无回弹），
        // 之后整个拖动手势都在 v=1 的几何上算。
        StopZoomEase();
        _isPanningContent = CanPanContent();
        if (!_isPanningContent)
        {
            // 装得下的贴图＝拖窗口，不是平移内容。这条原先不打日志，于是"把贴图从 A 拖到 B 再缩放"
            // 这种复现在日志里完全查不到 A 点，只能靠猜（2026-09-23 排查"首次缩放在 A 点闪一下"时踩到）。
            var vpA = GetViewportRectPx();
            var ctA = GetContentRectPx(vpA);
            if (ctA.Width > 0)
                WheelDiag($"拖动起手（移窗口）：窗口({vpA.X - ShadowMargin * SafeScaling:F0}," +
                          $"{vpA.Y - ShadowMargin * SafeScaling:F0}) 内容({ctA.Width:F0}x{ctA.Height:F0})" +
                          $"@({ctA.X:F0},{ctA.Y:F0}) 视口({vpA.Width:F0}x{vpA.Height:F0})");
            return;
        }
        // 起手先把内容簿记按**活几何**重钉一遍：覆写态下二者恒等（Margin 就是由它反解出来的，
        // 此处为空操作），而静止态下 _wheelImg* 可能还停在上一轮手势的终值，不重钉会把过期屏幕
        // 位置烤进 Margin（观感＝一按下去画面跳走）。此后整个手势期都用这对冻结值，不再重判。
        var vp0 = GetViewportRectPx();
        var ct0 = GetContentRectPx(vp0);
        _wheelImgLpx = ct0.X; _wheelImgTpx = ct0.Y;
        _wheelImgWpx = ct0.Width; _wheelImgHpx = ct0.Height;
        _panRafScheduled = false;
        _panRafGen++;
        _panStartScreenPoint = screenPt;
        _panStartImgLpx = _wheelImgLpx;
        _panStartImgTpx = _wheelImgTpx;
        _panWinStart = GetWindowOriginPx();
        WheelDiag($"平移起手：内容({ct0.Width:F0}x{ct0.Height:F0}) 视口({vp0.Width:F0}x{vp0.Height:F0})"
                  + $"@({vp0.X:F0},{vp0.Y:F0}) "
                  + $"起点({ct0.X:F0},{ct0.Y:F0})");
    }

    /// <summary>本次拖动需要更新裁切视口，而非直接平移既有窗口。按下时冻结执行路径；
    /// 图片两条路径都移动同一份屏幕坐标，只是装得下时可复用原生表面快速搬运。</summary>
    private bool _isPanningContent;

    private void CaptureStickerDrag(IPointer pointer)
    {
        // 普通拖窗/内容平移也要捕获指针，避免离开可见区域后漏掉松手，留下悬挂的拖动态。
        _dragPointer = pointer;
        pointer.Capture(this);
    }

    private void EndStickerDrag(string reason)
    {
        if (!_isDragging && _dragPointer == null && !_isPanningContent) return;
        _isDragging = false;
        if (_isPanningContent)
        {
            if (!_isClosed) FlushPendingContentPan();
            else { _panRafGen++; _panRafScheduled = false; }
            _isPanningContent = false;
        }
        var pointer = _dragPointer;
        _dragPointer = null; // 先清引用，释放捕获引发的 CaptureLost 不再重复收尾。
        if (pointer != null && ReferenceEquals(pointer.Captured, this)) pointer.Capture(null);
        LogDragSegment(reason);
        if (_isClosed) return;
        SaveArchiveSnapshot();
        if (_wheelGestureActive) SchedulePinWheelSettle();
    }
    /// <summary>平移起手时的内容屏幕物理位置与光标位置（每帧从这对基线重算，不累加上一帧结果）。</summary>
    private double _panStartImgLpx, _panStartImgTpx;
    private Avalonia.PixelPoint _panStartScreenPoint;
    /// <summary>平移起手时的**窗口**原点。装得下的那一轴由窗口承载位移，其目标位置必须按
    /// "起手原点 + 光标总位移"绝对式算出 —— 增量式会与 <see cref="ApplyDragMove"/> 的簿记同移
    /// 叠成正反馈（实测窗口 x 飞到 −14972，请求位移仅 −300）。</summary>
    private Avalonia.PixelPoint _panWinStart;

    // —— 平移手势合帧（2026-09-23，彻底消灭 4K 高分辨率下高回报率鼠标的事件堆积与掉帧）——
    private bool _panRafScheduled;
    private double _panPendingTargetLpx, _panPendingTargetTpx;
    private int _panRafGen;

    /// <summary>调度内容平移合帧：高频 PointerMoved（500Hz~1000Hz）只更新最新目标坐标，
    /// 每渲染帧（VSync）至多在 RAF 回调中落地一次布局重排与渲染，彻底消灭 4K 表面下的事件积压与掉帧。</summary>
    private void ScheduleContentPanRaf(double targetLpx, double targetTpx)
    {
        _panPendingTargetLpx = targetLpx;
        _panPendingTargetTpx = targetTpx;
        if (_panRafScheduled) return;
        _panRafScheduled = true;
        int gen = _panRafGen;
        RequestAnimationFrame(_ =>
        {
            if (gen != _panRafGen) return; // 旧手势回调不能清掉新手势的待结算标记。
            _panRafScheduled = false;
            if (_isClosed || !_isPanningContent) return;
            ApplyContentPan(_panPendingTargetLpx, _panPendingTargetTpx);
        });
    }

    /// <summary>松手或手势中断时立即结算最后一帧未落地的平移坐标，防止松手瞬间最后微小位移丢失。</summary>
    private void FlushPendingContentPan()
    {
        _panRafGen++;
        if (_panRafScheduled)
        {
            _panRafScheduled = false;
            ApplyContentPan(_panPendingTargetLpx, _panPendingTargetTpx, isFinalFrame: true);
        }
        else
        {
            // 即便 RAF 恰好已执行完，手势结束终态也需全面同步一次所有图层几何
            var vp = GetViewportRectPx();
            if (vp.Width <= 0) return;
            double s = SafeScaling;
            SetWheelGestureVisual((_wheelImgLpx - vp.X) / s, (_wheelImgTpx - vp.Y) / s,
                                  StickerImage.Width, StickerImage.Height, forceAllLayers: true);
        }
    }

    /// <summary>当前**视口**（窗口内容区）的屏幕物理矩形＝窗口矩形内缩两侧 <see cref="ShadowMargin"/>。
    /// 两个真值源按"谁更新读谁"取，与"能不能平移"无关（判据本身不再看任何状态位）：
    ///   覆写（手势/平移常驻）态读 <c>_wheelEnv*</c>——它由落地处与 <c>SetWindowPos</c> 同批写，
    ///     比布局回打的 <c>Bounds</c> 早一帧；读 Bounds 会在信封涨大那一帧把视口读小，
    ///     把"装得下"误判成"可平移"（一帧四向箭头＝闪一下）；
    ///   静止态读 <c>Bounds</c>（免费）——此刻窗口就是贴合矩形，两组数等值。
    /// ⚠ 阶段 4 删信封时该分支塌成"只剩 Bounds 一条"，判据一字不改仍然成立。
    /// 窗口尚未布局（<c>Bounds</c> 为 0）时返回空矩形，调用方一律按"不可平移"处理。</summary>
    private Avalonia.Rect GetViewportRectPx()
    {
        double s = SafeScaling, mPx = ShadowMargin * s;
        if (_wheelVisualOverride && _wheelEnvW > 0 && _wheelEnvH > 0)
            return new Avalonia.Rect(_wheelEnvL, _wheelEnvT, _wheelEnvW, _wheelEnvH);

        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return default;
        var win = GetWindowOriginPx();
        return new Avalonia.Rect(win.X + mPx, win.Y + mPx,
                                 Math.Max(1.0, b.Width * s - mPx * 2),
                                 Math.Max(1.0, b.Height * s - mPx * 2));
    }

    /// <summary>当前**内容**（图片／文本层）的屏幕物理矩形。两个真值源同样按"谁更新读谁"取：
    ///   覆写（手势／平移常驻）态读 <c>_wheelImg*</c>——各层 Margin 正是由它反解出来的，取它即与
    ///     改版前逐字相同；
    ///   静止态读**已排版出来的**那一块（图片层 <c>StickerImage</c>，文本贴图取 <c>TextViewbox</c>）
    ///     的实际位置与尺寸。此态窗口矩形归布局所有，只有拿排版结果与窗口比才自洽：拿
    ///     <c>ApplyScale</c> 写的显式宽高去比，会在 markdown 两帧高度捕获期（显式高度还是估测值）
    ///     把"窗口跟着内容长"误判成"内容溢出"，文本贴图当场变成拖不动窗口。</summary>
    private Avalonia.Rect GetContentRectPx(Avalonia.Rect vp)
    {
        if (vp.Width <= 0) return default;
        if (_wheelVisualOverride && _wheelImgWpx > 0 && _wheelImgHpx > 0)
            return new Avalonia.Rect(_wheelImgLpx, _wheelImgTpx, _wheelImgWpx, _wheelImgHpx);

        Avalonia.Visual? layer = StickerImage != null && StickerImage.IsVisible
            ? StickerImage
            : TextViewbox;
        if (layer == null || layer.Bounds.Width <= 0 || layer.Bounds.Height <= 0) return default;
        double s = SafeScaling;
        var origin = layer.TranslatePoint(new Avalonia.Point(0, 0), this);
        if (origin == null) return default;
        // origin 是窗口相对 DIP，而 vp 的原点已内缩一圈 ShadowMargin ⇒ 换算回视口内坐标
        double mPx = ShadowMargin * s;
        return new Avalonia.Rect(vp.X + origin.Value.X * s - mPx,
                                 vp.Y + origin.Value.Y * s - mPx,
                                 layer.Bounds.Width * s, layer.Bounds.Height * s);
    }

    /// <summary>内容在该轴上是否越出视口，用于选择更新裁切视口或直接移窗的执行路径。
    /// 图片任一轴都按指针总位移移动；非图片保留原逐轴搬运方式。
    /// <paramref name="vp"/>/<paramref name="ct"/> 为视口与内容的屏幕物理矩形。</summary>
    private static void GetContentOverflow(Avalonia.Rect vp, Avalonia.Rect ct,
                                           out bool overflowX, out bool overflowY)
    {
        // 容差 1px：内容与视口逐像素贴合时不算越出（否则静止贴合态会被误判成可平移，
        // 与提交路径 keepOverrideForPan 的同一处 +1.0 容差同口径——符号写反会让贴合态每帧误判）。
        overflowX = ct.Width > vp.Width + 1.0;
        overflowY = ct.Height > vp.Height + 1.0;
    }

    /// <summary>当前是否处于"可平移"状态：内容任一轴越出视口、**或覆写态下视口在该轴比内容大**
    /// （窗口带一圈透明余量），且不在独占手势/禁用态中。
    /// 锁定与穿透按既有语义禁掉（锁定＝不可移动缩放；穿透＝窗口收不到鼠标）；缩略图自有一套
    /// 视口不掺和；标注期内几何不得变（protection 红线⑥）；拖边/选词各自独占手势。
    /// ⚠ 2026-09-23 阶段 1 起判据**只看内容与视口的实际大小**，不再以 <c>_wheelVisualOverride</c>
    /// 为前提——那是信封的状态位，而平移必须活到信封删除之后（否则删信封＝连带删掉视口平移，
    /// 长截图会退回"窗口就是 14.8MPx"，正是 09-22 花大力气治好的病）。静止态下"窗口＝贴合矩形"
    /// 这一提交口径让判据天然为假，故与改版前逐像素一致。
    /// ⚠ 2026-09-24 追加"视口比内容大"这一支（**且仅在覆写态**，静止态按上面的口径恒假）：
    /// 拖动的职责是**画面跟手**，窗口只是搬运工具，而窗口能搬的距离会被工作区夹住。手势期的
    /// "装得下就不动窗口"规则（`ComputeFittedWindowRectPx`）会让窗口停在比画面大好几圈的透明
    /// 余量上，此时窗口已无路可走而余量还多得很——只走"挪窗口"一条腿就表现为**拖不动**。
    /// 实测（`log/app.log` pid=40584 08:41）：画面 1152×1242 配视口 2109×2040（窗口 2157×2088
    /// ＝工作区全高），Y 轴的合法窗口位移区间是 [0,0] ⇒ 用户报「没办法自由平移，只有左右平移」。
    /// 剩下的差额由这圈余量以内容偏移（Margin）吸收，见 <see cref="ApplyContentPan"/> 的②。
    /// </summary>
    private bool CanPanContent()
    {
        if (_isLocked || _isMousePenetration || _isThumbnailMode) return false;
        if (_isAnnotating || _isResizing) return false;
        var vp = GetViewportRectPx();
        if (vp.Width <= 0) return false;
        var ct = GetContentRectPx(vp);
        GetContentOverflow(vp, ct, out bool ox, out bool oy);
        // 1px 容差与 GetContentOverflow 同口径：视口常因 SnapStablePx 量化比内容大 0.x px
        bool slack = _wheelVisualOverride &&
                     (ct.Width < vp.Width - 1.0 || ct.Height < vp.Height - 1.0);
        return ox || oy || slack;
    }

    /// <summary>本手势是否为文本/Markdown 贴图把 SizeToContent 临时改成了 Manual。
    /// 显式记账而非按 TextViewbox 可见性反推：手势中途内容可能被换掉（翻译把文本替换为
    /// 译文图片），反推会在该场景下把 SizeToContent 还原成错误的一档。</summary>
    private bool _wheelEnvelopeManualSizing;
    /// <summary>激活前的 SizeToContent 原档位，提交时原样交还。禁止硬编码 WidthAndHeight：
    /// 纯文本贴图平时是 WidthAndHeight，而 Markdown 贴图高度捕获完成后归 Manual，
    /// 两者由 ApplyScale 走不同分支决定。</summary>
    private SizeToContent _wheelEnvelopePrevSizeToContent;

    /// <summary>文本/Markdown 贴图起手错配态同步收口（滚轮、拖边、适配屏幕共用；图片贴图即时返回）。
    /// markdown 自然尺寸捕获延迟两帧，期间 _sourcePixel* 仍是估测占位；一旦窗口表面与文本实际布局
    /// 有出入，手势就从错基线起算：贴合矩形按估测尺寸现算，第一格滚轮即把窗口按偏大的尺寸重摆，
    /// 同时内容被 Margin 重定心跳位（缺陷 B"背景消失并跳跃"）。此处先把错配收口一次
    /// （按活容器实测写 _sourcePixel* + 清捕获标记 + 常规 ApplyScale 收口窗口 + UpdateLayout 就地
    /// 提交原生尺寸），手势再以真值起算，鼠标锚定从正确基线出发。
    /// ⚠ 必须只在覆写态激活前调用：手势态下簿记归 Margin 覆写所有，再收口会打架（守卫已挡）。</summary>
    private void SyncTextSurfaceBeforeGesture()
    {
        if (_bitmap != null) return;                       // 图片贴图无文本捕获态
        if (_isClosed) return;
        if (_wheelVisualOverride || _wheelGestureActive) return;
        if (TextViewbox == null || !TextViewbox.IsVisible) return;
        if (TextStickerContainer == null) return;
        double s = SafeScaling;
        var nat = TextStickerContainer.Bounds;
        if (nat.Width < 1 || nat.Height < 1) return;       // 布局尚未产出：交给延迟捕获照常收口
        // 🔴 这里禁止夹地板：账本一旦大于卡片实测，光晕基准线就与卡片分叉（右侧整块透明带），
        //    而本方法是每次手势起手都会重钉的入口，地板会在那里把创建期收好的正确尺寸又顶回去。
        int wantW = (int)Math.Round(nat.Width * s);
        int wantH = (int)Math.Round(nat.Height * s);
        bool staleCapture = _needsMarkdownHeightCapture;
        bool mismatch = Math.Abs(wantW - _sourcePixelWidth) > 1 || Math.Abs(wantH - _sourcePixelHeight) > 1;
        if (!staleCapture && !mismatch) return;
        WheelDiag($"文本贴图起手同步收口 ({_sourcePixelWidth}x{_sourcePixelHeight})→({wantW}x{wantH}) 捕获中={staleCapture}");
        _sourcePixelWidth = wantW;
        _sourcePixelHeight = wantH;
        _needsMarkdownHeightCapture = false;
        ApplyScale();   // 常规收口：Manual 分支对齐窗口表面（位置不动，只让表面追上内容）
        UpdateLayout(); // Avalonia 尺寸 setter 走布局提交——必须就地落账，视口簿记才能读到新表面
    }

    /// <summary>进入手势视觉态（滚轮与拖边共用；幂等）：视口簿记先对齐到当前真实视口，
    /// 此后几何归 Margin 所有。文本/Markdown 贴图额外把 SizeToContent 切到 Manual——否则窗口矩形
    /// 仍归布局所有，下一次 arrange 会把窗口拽回"贴合内容"尺寸、与本通路的 SetWindowPos 对撞。</summary>
    private void ActivateWheelEnvelope(double cL, double cT, double cW, double cH)
    {
        if (_wheelGestureActive) return;

        _wheelGestureActive = true;
        _wheelVisualOverride = true;
        _wheelEnvL = cL; _wheelEnvT = cT;
        _wheelEnvW = cW; _wheelEnvH = cH;
        // 倍率归 1：此刻布局尺寸就是画面尺寸（静止态由 ApplyScale 落好）。
        // ⚠ 刻意**不**在这里重立 `_gestureRasterScale`：进入本方法时 `_scale` 已经被上一行推进过一档，
        // 在这里写等于宣称"布局已经按新 _scale 排好"，而那是假的（日志实证：第一格 栅格档=1.100
        // 而 图片=899 仍是 1.000 档的尺寸）。错标的后果不是抖动而是**丢真排版**——
        // SettleZoomRaster 以为已落过就跳过，松手后各层还停在旧尺寸、变换却被清零＝画面缩一截。
        // 这个字段只由 `ApplyScale`（唯一真的改了布局尺寸的地方）写。
        _gestureV = 1.0;
        _wheelPosConstrainedLogged = false;
        if (TextViewbox != null && TextViewbox.IsVisible
            && SizeToContent != SizeToContent.Manual)
        {
            _wheelEnvelopePrevSizeToContent = SizeToContent;
            SizeToContent = SizeToContent.Manual;
            _wheelEnvelopeManualSizing = true;
        }
        // 覆写态命中穿透：内容+阴影之外的区域鼠标事件直接穿透到下层窗口。阶段 2 起窗口矩形就是
        // 贴合矩形，正常情况下"图片+阴影之外"这块区域已经不存在，这套穿透于是只剩两个作用：
        // ① <c>贴合落定被忽略</c>（SetWindowPos 被系统静默吞掉）时窗口仍大于内容，那条空白靠它兜底；
        // ② 收口失败、覆写态被撤销回手势态时（RestoreWheelEnvelopeAfterFailedCommit）同样靠它不吃点击。
        // 故暂不随信封一起删（见 docs/贴图去信封方案.md §5）。
        InstallWheelHitTransparency(this);
        WheelDiag("手势激活 视口初始=" + cW.ToString("F0") + "x" + cH.ToString("F0")
                  + (_wheelEnvelopeManualSizing ? "（文本：SizeToContent→Manual）" : ""));
    }

    /// <summary>交还窗口尺寸所有权：覆写期被临时改成 Manual 的文本贴图，在完整收口回
    /// 贴合窗口后还原成激活前的那一档（纯文本＝WidthAndHeight；Markdown 平时已是 Manual，
    /// 压根不会被改，故也不需还原）。此刻内容显式宽高已与贴合尺寸逐像素一致，布局重算
    /// 结果为空操作，不会再产生一次带 cloak 的 resize。内容已被换成图片时保持 Manual。</summary>
    private void ReleaseWheelEnvelopeSizing()
    {
        if (!_wheelEnvelopeManualSizing) return;
        _wheelEnvelopeManualSizing = false;
        SizeToContent = TextViewbox != null && TextViewbox.IsVisible
            ? _wheelEnvelopePrevSizeToContent
            : SizeToContent.Manual;
        WheelDiag("文本收口交还尺寸所有权：SizeToContent→" + SizeToContent);
    }

    /// <summary>
    /// 缩放手势的每格落地（图片 / 文本 / Markdown 共用）：以鼠标为锚点缩放内容，
    /// 同帧把窗口对齐到新的贴合矩形（2026-09-23 阶段 2 前这里是"窗口矩形全程不动、只在两端
    /// resize"的零 resize 信封）。覆写态长期保留：手势期的几何由 8 层共用 Margin 承载，
    /// 平移查看越出视口的部分也靠它（阶段 1 已与信封解耦）。
    /// 仅快捷键、切缩略图与窗口失活时收口（见 OnWindowKeyDown / ToggleThumbnailMode / OnWindowDeactivated）。
    /// </summary>
    /// <remarks>2026-09-16 起合帧：只由 <see cref="ApplyWheelZoomAccumulated"/> 在渲染帧内调用，
    /// stepRatio 为逐格 ×1.1 的累积乘积。<b>2026-09-23 改：锚点参数是"一次手势冻结的鼠标屏幕物理
    /// 像素"</b>（<c>_wheelGestureAnchorPx</c>），不再是最后一格的窗口相对 DIP——后者会让画面在
    /// 两格之间随鼠标挪动而平移（"画面跟着指针跑"）。</remarks>
    private void HandleWheelZoomEnvelope(double mouseXPx, double mouseYPx, double stepRatio)
    {
        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        // 缺陷 B：文本/Markdown 错配态必须在视口簿记读到 GetWindowRect 之前收口，否则第一格
        // 就按估测尺寸重摆窗口、内容重定心跳位
        SyncTextSurfaceBeforeGesture();

        // —— 当前窗口物理矩形（正常态＝贴合矩形；覆写态下已是上一格对齐后的尺寸） ——
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        double wl, wt, contW, contH;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            wl = rc.Left; wt = rc.Top;
            contW = rc.Right - rc.Left - mPx * 2;
            contH = rc.Bottom - rc.Top - mPx * 2;
        }
        else
        {
            wl = Position.X; wt = Position.Y;
            contW = Width * s - mPx * 2;
            contH = Height * s - mPx * 2;
        }

        // —— 上一格的**视觉**画面矩形：同一段手势内以簿记为准（冻结栅格期 Margin 反解出来的是
        // "已排版那一档"的位置，乘上变换之后才是屏幕上的画面），起手第一格两者逐像素相同 ——
        bool inGesture = _wheelGestureActive;
        double curW = double.IsNaN(StickerImage.Width)
            ? Math.Max(20.0, Math.Round(_sourcePixelWidth * _scale) / s) : StickerImage.Width;
        double curH = double.IsNaN(StickerImage.Height)
            ? Math.Max(20.0, Math.Round(_sourcePixelHeight * _scale) / s) : StickerImage.Height;
        double imgL = inGesture && _wheelImgWpx > 0
            ? _wheelImgLpx : wl + mPx + StickerImage.Margin.Left * s;
        double imgT = inGesture && _wheelImgHpx > 0
            ? _wheelImgTpx : wt + mPx + StickerImage.Margin.Top * s;
        if (inGesture && _wheelImgWpx > 0) { curW = _wheelImgWpx / s; curH = _wheelImgHpx / s; }

        // —— 缩放锚点：一次手势冻结的鼠标屏幕物理像素（整段滚动期间不变，见方法头 remarks） ——
        double mX = mouseXPx, mY = mouseYPx;

        // —— 新缩放：一步应用累积比（乘法链对边界单调，终值与逐格钳制一致；
        // 已在缩放边界则本帧跳过） ——
        double oldScale = _scale;
        _scale = Math.Clamp(_scale * stepRatio, 0.2, GetMaxScale());
        if (_scale == oldScale) return;

        // 视口簿记初始 = 当前窗口内容区（本格末尾由 EnsureFittedWindowRect 把它对齐到新尺寸）
        ActivateWheelEnvelope(wl + mPx, wt + mPx, contW, contH);
        _wheelGen++;

        // —— 内容尺寸：默认**不排版**，缩放只由 RootGrid 的倍率表达（成本依据与禁令见
        //    StickerStyling.ZoomFreezeRaster）；倍率离开 ±余量档 或 非图片贴图 时才真排版一次 ——
        double newW = DisplayWidthDipAt(_scale), newH = DisplayHeightDipAt(_scale);
        double rasterW = StickerImage.Width, rasterH = StickerImage.Height;
        double vBefore = rasterW > 0 ? newW / rasterW : 1.0;   // 本格"若不改排版"要落的倍率
        // ⚠ 必须在下面任何 ApplyScale 之前取走"此刻画面真实显示的倍率"：ApplyScale 顶部的重立段
        // 会把 _zoomV 清成 1.0（那是相对**新栅格**的正确值，但本格的连续性要用相对**旧栅格**的值）。
        // 少了这一行，重排版那一格就从"两格之前的大小"起跳再长回来＝用户实测的"第三格先缩小再放大"
        // （2026-09-23 日志实证：三格一段的三次复现，跳的正是 栅格档 变化的那一格）。
        double vShown = _zoomV;
        bool freezeRaster = StickerStyling.ZoomFreezeRaster && _bitmap != null && !_isThumbnailMode;
        bool reRastered = false;   // 本格是否真排过版（真排版＝栅格基准换了，起始倍率要换算）
        // ⚠ 重落真排版**只在"画面比栅格小"的方向设档**（2026-09-23 用户复测后改）。
        // 依据：他读到的阈值是"原始尺寸以下流畅、超出原始尺寸就闪几下抖几下"，而闪的那几下正好
        // 落在跨档那几格 ⇒ 贵的是"重排版"这个动作本身（9 层 Measure/Arrange＋阴影几何重建），
        // 不是像素。放大方向再排版也换不来清晰——原图没有那些像素，位图还是同一张，只是把目标框
        // 画大；所以那一侧的排版是**纯付钱无收益**，整段放大就停在起手那一档、全交给 GPU 采样，
        // 松手仍落一次真排版。缩小方向必须留档：没有 mipmap 的最近邻/双线性大幅缩样会闪花。
        if (freezeRaster && (rasterW <= 0 || rasterH <= 0 ||
                             vBefore < 1.0 / (1.0 + StickerStyling.ZoomGestureHeadroom)))
        {
            // 顶破余量档 ⇒ 落一次真排版（画面重新变清晰，这也正是窗口该跟着变尺寸的那一格）
            _isApplyingScale = true;
            try { ApplyScale(skipWindowResize: true); }
            finally { _isApplyingScale = false; }
            rasterW = StickerImage.Width; rasterH = StickerImage.Height;
            newW = rasterW; newH = rasterH;
            reRastered = true;
        }
        else if (!freezeRaster)
        {
            _isApplyingScale = true;
            try { ApplyScale(skipWindowResize: true); }
            finally { _isApplyingScale = false; }
            newW = StickerImage.Width; newH = StickerImage.Height;
            rasterW = newW; rasterH = newH;
            reRastered = true;
        }
        if (reRastered) _wheelRelayouts++;   // 本段手势"真排版"过几格＝冻结是否真的在生效

        // —— 鼠标锚定：内容点钉在指针下（每轴按实际显示比率外推，含取整差） ——
        double kx = curW > 0 ? newW / curW : 1.0;
        double ky = curH > 0 ? newH / curH : 1.0;
        _wheelImgLpx = ScaleCoordinateAroundAnchor(imgL, mX, kx);
        _wheelImgTpx = ScaleCoordinateAroundAnchor(imgT, mY, ky);
        _wheelImgWpx = newW * s;
        _wheelImgHpx = newH * s;

        // 内容位置到这里就定稿（锚点外推的真值），**不做任何"收进表面"的约束**：阶段 2 起窗口矩形
        // 由它算出，越出工作区的部分改由夹小后的视口＋平移 offset 承载，约束必须等窗口落地之后再按
        // **新**视口做一次（见 ApplyFittedWindowRect）。在这里按旧视口约束等于把锚点往回拽。

        // 视觉倍率：画面目标 ÷ 已排版档。落过真排版的格（含旧通路）恒为 1.0 ⇒ 变换是数学空操作。
        _gestureV = rasterW > 0 ? newW / rasterW : 1.0;

        // —— 内容层定位 Margin + 阴影/棋盘显式几何（先按当前视口原点反解）——
        PinGestureLayers();

        // —— 窗口对齐内容（与本格视觉同帧原子提交，中间态永不出帧）——
        // 补帧期间画面可能比终态大（缩小方向的起点＝上一格尺寸），不会被窗口边裁掉：手势期的矩形
        // 规则本身就把"现视口"并了进去（见 ComputeFittedWindowRectPx 的手势期分支）。
        EnsureFittedWindowRect();

        // 起始倍率：画面此刻在屏幕上的尺寸 ÷ **新栅格**尺寸。没换栅格的格（冻结通路）就是原值续接
        // ⇒ 画面连续长大、不存在"先弹回再长大"；换过栅格的格要把倍率换算到新基准上（旧通路每格都换，
        // 换算结果＝上一格显示宽÷本格终态宽，与改版前的起始值逐位相同）。
        double fromRatio = reRastered && vBefore > 0 ? vShown / vBefore : vShown;
        BeginZoomEase(fromRatio, _gestureV, mX, mY);
    }

    /// <summary>手势期把各层摆到"经 <see cref="_gestureV"/> 缩放之后正好落在目标画面矩形
    /// （<c>_wheelImg*</c>）"上，并把命中穿透矩形按<b>看得见</b>的那块刷新。
    /// ⚠ 冻结栅格通路里"布局尺寸"与"画面尺寸"不再相等，凡是拿 Margin 反解/落位几何的出口都必须走
    /// 本方法（窗口落地前后各一次——中间 <c>_wheelEnv*</c> 会变），别在调用点各写一遍除法。
    /// 倍率为 1（旧通路、文本贴图、落过真排版的那一格）时本方法与改版前逐像素相同。</summary>
    private void PinGestureLayers()
    {
        double s = SafeScaling;
        double mPx = ShadowMargin * s;
        double v = _gestureV > 0 ? _gestureV : 1.0;
        // 变换绕锚点做 ⇒ 布局位置要按锚点反解；锚点尚未采到（原地换图等通路）时按画面中心等效
        double aX = double.IsNaN(_wheelGestureAnchorPx.X) ? _wheelImgLpx + _wheelImgWpx / 2 : _wheelGestureAnchorPx.X;
        double aY = double.IsNaN(_wheelGestureAnchorPx.Y) ? _wheelImgTpx + _wheelImgHpx / 2 : _wheelGestureAnchorPx.Y;
        double layL = aX + (_wheelImgLpx - aX) / v;
        double layT = aY + (_wheelImgTpx - aY) / v;
        SetWheelGestureVisual((layL - _wheelEnvL) / s, (layT - _wheelEnvT) / s,
                              StickerImage.Width, StickerImage.Height);
        if (v == 1.0) return;
        // 命中穿透矩形跟**看得见**的画面走：它由 SetWheelGestureVisual 按布局尺寸算，画面比布局大的
        // 那几格里，越出布局矩形的部分照样是这张图的像素，按布局判会被 HTTRANSPARENT 漏给下层窗口。
        _hitRectDip = new Avalonia.Rect(
            (_wheelImgLpx - (_wheelEnvL - mPx)) / s, (_wheelImgTpx - (_wheelEnvT - mPx)) / s,
            _wheelImgWpx / s + ShadowMargin * 2, _wheelImgHpx / s + ShadowMargin * 2);
        UpdateHitRegion();
    }

    private static void SetMarginIfChanged(Avalonia.Controls.Control? ctrl, Avalonia.Thickness margin)
    {
        if (ctrl != null && ctrl.Margin != margin)
        {
            ctrl.Margin = margin;
        }
    }

    private static void SetExplicitBoundsIfChanged(Avalonia.Controls.Control? ctrl, Avalonia.Thickness margin, double w, double h)
    {
        if (ctrl == null) return;
        if (ctrl.HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Left)
            ctrl.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        if (ctrl.VerticalAlignment != Avalonia.Layout.VerticalAlignment.Top)
            ctrl.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        if (ctrl.Margin != margin)
            ctrl.Margin = margin;
        if (ctrl.Width != w)
            ctrl.Width = w;
        if (ctrl.Height != h)
            ctrl.Height = h;
    }

    /// <summary>应用手势视觉覆写：内容层（图片 / 文本 Viewbox）定位 Margin；阴影与棋盘
    /// 改为显式几何跟随内容（信封态窗口大于内容，拉伸铺满会把阴影画到内容外）。同时更新
    /// WM_NCHITTEST 命中矩形（内容+阴影边距，窗口 DIP 坐标）。
    /// <paramref name="forceAllLayers"/> 为 true 时强制同步所有层（松手收尾/显式提交），
    /// false 时仅同步当前可见图层，消除 4K 拖拽手势中未激活图层的无效排版与重绘消耗。</summary>
    private void SetWheelGestureVisual(double mx, double my, double w, double h, bool forceAllLayers = false)
    {
        var margin = new Thickness(mx, my, 0, 0);
        SetMarginIfChanged(StickerImage, margin);
        if (StickerCanvas != null && (forceAllLayers || StickerCanvas.IsVisible))
            SetMarginIfChanged(StickerCanvas, margin);
        if (StickerAnnotationLayer != null && (forceAllLayers || StickerAnnotationLayer.IsVisible))
        {
            SetMarginIfChanged(StickerAnnotationLayer, margin);
            // 文本宿主层永远与标注层同几何：这里不跟一次，信封态下文字会坐在上一轮的位置
            SyncTextHostGeometry();
        }
        // 文本贴图走同一条信封通路：TextViewbox 在 XAML 里已是 Left/Top 对齐 + 显式宽高
        // （尺寸由 ApplyScale 写入），故与 StickerImage 一样只需补一份 Margin 即可在信封内
        // 定位。两层互斥可见，各写各的不会产生叠加。
        if (TextViewbox != null && (forceAllLayers || TextViewbox.IsVisible))
            SetMarginIfChanged(TextViewbox, margin);
        // 底色层随图片一起偏移与缩放（否则信封态下底色会留在原位、露出错位色块）
        if (ImageBackdrop != null && (forceAllLayers || ImageBackdrop.IsVisible))
        {
            SetExplicitBoundsIfChanged(ImageBackdrop, margin, w, h);
        }
        // 遮罩层同理：信封态下若仍铺满窗口，层内的居中徽章会落在图片外
        if (TranslatingOverlay != null && (forceAllLayers || TranslatingOverlay.IsVisible))
        {
            SetExplicitBoundsIfChanged(TranslatingOverlay, margin, w, h);
        }
        // 命中穿透矩形＝**等效贴合窗口矩形**（内容外扩一圈阴影边距，阴影区可点击/拖动）。
        // ⚠ 坐标系：mx/my 是 RootGrid 内容区（原点＝窗口原点 + ShadowMargin）相对坐标，而
        // WM_NCHITTEST 比较的是窗口相对 DIP，故这里 X=mx、宽=w+2*ShadowMargin。早先写成
        // mx-ShadowMargin 会把交互区整体往左上错移 16（右/下侧阴影带点不到、左/上侧反而多吞
        // 16 空白），与 <see cref="GetFittedClientRectDip"/> 的口径也不一致（拖边 20DIP 热区
        // 必须落在真正可命中的范围内）。
        _hitRectDip = new Avalonia.Rect(
            mx, my, w + ShadowMargin * 2, h + ShadowMargin * 2);

        if (ShadowBorder != null)
        {
            SetExplicitBoundsIfChanged(ShadowBorder, margin, w, h);
        }
        if (CheckerboardBg != null && (forceAllLayers || CheckerboardBg.IsVisible))
        {
            SetExplicitBoundsIfChanged(CheckerboardBg, margin, w, h);
        }
        if (CardHighlightBorder != null && (forceAllLayers || CardHighlightBorder.IsVisible))
        {
            UpdateCardHighlightLayer();
        }
        UpdateHitRegion();
    }

    /// <summary>视觉覆写态（手势期／平移常驻态）下内容尺寸变化后的几何重钉：
    /// 以图片当前屏幕中心为锚点按新尺寸反算 Margin 并同步各层与命中矩形，再按
    /// <see cref="EnsureFittedWindowRect"/> 把窗口对齐到新尺寸。位置中心不变 ⇒ 零位置跳变。
    /// 供 <see cref="ApplyScale"/> 在覆写态替代"窗口贴合"公式调用（原地换图路径）。
    /// <paramref name="resizeWindow"/> 传 false 供「适配屏幕」复用同一套中心重钉公式而跳过对齐
    /// 窗口——那条通路紧接着就整次提交，提前对齐只会多一次同尺寸的 resize。</summary>
    private void RepinWheelVisualForNewContentSize(double prevW, double prevH, double newW, double newH,
                                                   bool resizeWindow = true)
    {
        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        // 当前窗口内容区原点（屏幕物理）：取真实窗口矩形，与 HandleWheelZoomEnvelope 同源；
        // 拿不到句柄（未 Opened）时退回视口簿记
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        double envL = _wheelEnvL, envT = _wheelEnvT;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            envL = rc.Left + mPx;
            envT = rc.Top + mPx;
        }

        // 覆写态下图片宽高必为 SetWheelGestureVisual 显式写过的有限值；NaN 兜底按源图当前缩放算
        if (double.IsNaN(prevW)) prevW = Math.Max(20.0, Math.Round(_sourcePixelWidth * _scale) / s);
        if (double.IsNaN(prevH)) prevH = Math.Max(20.0, Math.Round(_sourcePixelHeight * _scale) / s);

        double cx = envL + StickerImage.Margin.Left * s + prevW * s / 2;
        double cy = envT + StickerImage.Margin.Top * s + prevH * s / 2;

        _wheelImgWpx = newW * s;
        _wheelImgHpx = newH * s;
        _wheelImgLpx = cx - _wheelImgWpx / 2;
        _wheelImgTpx = cy - _wheelImgHpx / 2;

        // 窗口跟着内容走（下面 EnsureFittedWindowRect 负责对齐），故此处不把内容"收进当前视口"——
        // 那会把中心锚点往回拽，与滚轮通路同一条道理。
        SetWheelGestureVisual((_wheelImgLpx - envL) / s, (_wheelImgTpx - envT) / s, newW, newH);
        // 换图/改内容尺寸＝几何重新定基线：若此刻还在补帧中，那个"并集窗口"是按**旧内容**算的，
        // 继续用它给新内容算贴合会把一块已经无意义的透明区留在窗口里。先断掉补帧再对齐。
        StopZoomEase();
        if (resizeWindow) EnsureFittedWindowRect();
    }

    /// <summary>把窗口对齐到"图片多大窗口多大"的贴合矩形：与真实窗口矩形不同时**就地**执行
    /// <see cref="ApplyFittedWindowRect"/>。两矩形一致时零操作，故"停在同一倍率上反复触发"不会
    /// 多一次 SetWindowPos。
    /// ⚠ 刻意不再排第二次 RAF（2026-09-23 实测否证旧写法）：原先这里只"排一次回调"，下一帧才真
    /// 改窗口，于是每一格都存在"内容已放大、窗口还是旧的"的一帧。按日志逐帧核对：286 个可比对帧里
    /// **137 帧（48%）内容大于当时生效的窗口**＝那一帧画面被窗口边界裁掉一条，中位超出 7.3%（正是
    /// 一格 ×1.1 的比例）；"缩放帧→贴合对齐"实测间隔中位 18.0ms＝一整个 60Hz 帧。本方法的全部调用
    /// 点本来就已在某个 RAF 回调之内（滚轮 <see cref="ApplyWheelZoomAccumulated"/>、拖边
    /// <see cref="ApplyEnvelopeResizeFrame"/>、换图 <see cref="RepinWheelVisualForNewContentSize"/>），
    /// 就地执行即与本格内容几何同帧提交 ⇒ 慢半拍与那一帧裁切一起消失，且不新增任何同步点。</summary>
    private void EnsureFittedWindowRect()
    {
        // 门取"覆写态在位"而不是"手势进行中"：平移常驻态（提交后仍保留覆写以承载 offset）里
        // 换图/改标注会让内容尺寸变化并走到这里，用后者判会把它整个丢掉 ⇒ 窗口停在旧尺寸、内容
        // 坐在按旧视口反解的 Margin 上，越出的部分再无通路收回。覆写已解除时 `_wheelImg*` 不再
        // 权威（窗口可能被别处挪过），必须跳过。
        if (!_wheelVisualOverride) return;
        if (FittedWindowRectDiffers(SafeScaling)) ApplyFittedWindowRect();
    }

    // =====================================================================
    // 滚轮缩放的视觉过渡（2026-09-23 去信封阶段 3，docs/贴图去信封方案.md §4.2 方案 A）
    // =====================================================================
    // 口径是「**几何一次到位、视觉补齐中间帧**」：
    //   ① 本格照常算终态、把 9 层排到终态、把窗口改到终态（原路径，一字未改）；
    //   ② 只在 RootGrid 上挂一个 ScaleTransform，从"上一格此刻显示的样子"开始，逐帧回到 1.0。
    // 于是画面是连续长大的，而**中间帧不存在任何重新排版、重新采样、重新取整**——逐格取整的正负
    // 翻转正是实测到的抖动来源，补帧把它从"每格一次"压成"每次手势收尾一次"。
    // 为什么不用 Avalonia 的 Transitions：连续滚轮会在动画半路来新一格，过渡的目标被改写时它的
    //   起点仍按上一格的**名义终态**算，而此刻显示的是半程尺寸 ⇒ 会 pop 一下。自己驱动就能把起点
    //   取成"此刻真实的倍率"（见 BeginZoomEase 里 _zoomEasing 那一支），续接无损。
    // 为什么挂 RootGrid 而不是内容 Panel：RootGrid 同时含阴影层与内容层，一份变换两者天然同步
    //   （挂两处＝两份参数必须永远相等，是新的漂移源）。RootGrid 里的浮条会跟着缩放，但尺寸提示条
    //   只在拖边期显示（拖边不走本过渡）、滚动条只在平移常驻态显示且静止时倍率恒为 1.0 ⇒ 不可见。
    // 补帧期间画面比窗口大（缩小方向的起点＝上一格尺寸）会不会被窗口边裁掉：不会——手势期窗口矩形
    //   由 <see cref="ComputeFittedWindowRectPx"/> 一条规则给出（装得下"画面＋一档余量"就不动，
    //   装不下就长大到带顶<b>并并上现视口</b>），恒 ⊇ "上一格可见区 ∪ 本格终态"。原先为这件事单开的
    //   一套并集簿记（_zoomSweep*／BeginZoomSweep）已于 2026-09-23 删除：它和"按档长大"互相拮抗，
    //   开着补帧时每格都按并集算 ⇒ 每格真 resize、25% 那档永远够不着（实测 124 次 resize 宽度增量
    //   中位仅 +2.3%），一件几何算两遍正是这里的失效模式。
    private Avalonia.Media.ScaleTransform? _zoomFx;
    private bool _zoomEasing;              // 补帧 RAF 循环在跑
    private bool _zoomRafScheduled;        // 已挂的回调跨停止／重启复用，始终只保留一条补帧链
    private double _zoomV = 1.0;           // 当前视觉倍率（相对"栅格基准档"的放大倍数）
    private double _zoomVTarget = 1.0;     // 补帧要走向的倍率（冻结栅格通路＝本格目标 ÷ 已排版档）
    private long _zoomLastStamp;           // 上一补帧时刻（Stopwatch 原始 tick）

    /// <summary>过渡是否启用。<see cref="StickerStyling.ZoomEaseMs"/> 改 0 即整块退回"每格直接到位"。</summary>
    private static bool ZoomEaseEnabled => StickerStyling.ZoomEaseMs > 0;

    private Avalonia.Media.ScaleTransform EnsureZoomFx()
    {
        if (_zoomFx != null) return _zoomFx;
        _zoomFx = new Avalonia.Media.ScaleTransform(1, 1);
        // 分层呈现：补帧倍率每变一次＝呈现层画一帧（变换不是 RootGrid 自身的属性，单独订阅）
        _zoomFx.PropertyChanged += (_, _) =>
        {
            ShadowBorder?.SetParentScale(_zoomFx.ScaleX);
            RequestPinSync();
        };
        // ⚠ 变换原点是 **Visual（RootGrid）** 的属性，不是 Transform 的（Avalonia 与 WPF 同）；
        // 项目里已有的用法一律是 `控件.RenderTransformOrigin = new RelativePoint(...)`。
        RootGrid.RenderTransformOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);
        RootGrid.RenderTransform = _zoomFx;
        return _zoomFx;
    }

    /// <summary>启动/续接补帧：把视觉倍率从 <paramref name="fromRatio"/> 推向 <paramref name="targetRatio"/>。
    /// <paramref name="targetRatio"/>＝"画面视觉尺寸 ÷ 已排版的那一档尺寸"。冻结栅格通路（图片贴图）
    /// 每格只在 <c>1±余量档</c> 内动这个数、不重排版 ⇒ 终态倍率可以停在 1.13 这种"没落真尺寸"的值上；
    /// 旧通路（文本 / 关档）本格已 <c>ApplyScale</c> 落过真尺寸 ⇒ 目标恒为 1.0，起始值取
    /// "上一格显示宽 ÷ 本格终态宽"（<c>&lt;1</c>）再长大。<paramref name="anchorX/Y"/> 是鼠标的
    /// **屏幕物理像素**位置：变换绕它做 ⇒ 整个补帧期间"鼠标指着的那个点"一动不动（方案 §6-B 红线）。
    /// <paramref name="fromRatio"/> 由调用方从此刻真实倍率换算到本格栅格，连续滚轮只更新目标、
    /// 保留上一显示帧的时钟；跨档重排也必须使用换算后的起点。</summary>
    private void BeginZoomEase(double fromRatio, double targetRatio, double anchorX, double anchorY)
    {
        double s = SafeScaling;
        double vpW = _wheelEnvW / s, vpH = _wheelEnvH / s;      // RootGrid 的 DIP 尺寸＝视口
        if (vpW <= 0 || vpH <= 0) { _zoomVTarget = targetRatio; return; }

        var fx = EnsureZoomFx();
        // 锚点换算成 RootGrid 内的比例（AGENTS.md §7：一律用 RelativePoint 显式指定 Relative，
        // 绝不用字符串——"0.5,0.5" 这类写法会被当成绝对像素坐标，绕左上角缩放且无任何报错）
        RootGrid.RenderTransformOrigin = new Avalonia.RelativePoint(
            Math.Clamp((anchorX - _wheelEnvL) / s / vpW, 0.0, 1.0),
            Math.Clamp((anchorY - _wheelEnvT) / s / vpH, 0.0, 1.0),
            Avalonia.RelativeUnit.Relative);
        _zoomVTarget = targetRatio;

        // 调用方已经把当前视觉倍率换算到本格栅格；跨档时 ApplyScale 会清零 _zoomV，
        // 此处再读它会丢掉换算结果，缩小方向每跨一档就跳一次。
        double start = fromRatio;
        if (start < 0.05) start = 0.05;
        if (start > 20.0) start = 20.0;
        _zoomV = start;
        fx.ScaleX = fx.ScaleY = start;
        if (!ZoomEaseEnabled)
        {
            _zoomEasing = false;
            _zoomV = targetRatio;
            fx.ScaleX = fx.ScaleY = targetRatio;
            return;
        }
        // 连续输入只更新目标，不重置上一帧时钟，否则高回报率输入会不断吃掉 dt，
        // 补帧几乎停住，直到滚轮停下才追上终态。
        if (!_zoomEasing) _zoomLastStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _zoomEasing = true;
        ScheduleZoomEaseRaf();
    }

    private void ScheduleZoomEaseRaf()
    {
        if (_zoomRafScheduled || _isClosed) return;
        _zoomRafScheduled = true;
        RequestAnimationFrame(_ =>
        {
            _zoomRafScheduled = false;
            TickZoomEase();
        });
    }

    /// <summary>逐帧把视觉倍率推向 <see cref="_zoomVTarget"/>（指数趋近，时间常数 ZoomEaseMs/2.5 ⇒
    /// 约 ZoomEaseMs 内走完 95%）。到位后什么都不做：几何（Margin、窗口矩形）在本格就已经算到位了，
    /// 补帧只改那一个矩阵数。</summary>
    private void TickZoomEase()
    {
        if (!_zoomEasing) return;
        if (_isClosed) { _zoomEasing = false; return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = System.Diagnostics.Stopwatch.GetElapsedTime(_zoomLastStamp, now).TotalMilliseconds;
        _zoomLastStamp = now;
        _easeFrameCount++;
        _easeMsTotal += dt;
        if (dt > _easeMsMax) _easeMsMax = dt;
        if (dt > 20.0) _easeOver20ms++;   // 60Hz 下一帧 16.7ms，留一点余量：超 20ms 即这一帧没赶上
        double tau = Math.Max(1.0, StickerStyling.ZoomEaseMs / 2.5);
        _zoomV += (_zoomVTarget - _zoomV) * (1.0 - Math.Exp(-Math.Max(dt, 1.0) / tau));
        if (Math.Abs(_zoomV - _zoomVTarget) < 0.002 * Math.Max(1.0, Math.Abs(_zoomVTarget)))
            _zoomV = _zoomVTarget;
        if (_zoomFx != null) _zoomFx.ScaleX = _zoomFx.ScaleY = _zoomV;

        if (_zoomV != _zoomVTarget)
        {
            ScheduleZoomEaseRaf();
            return;
        }
        _zoomEasing = false;
    }

    /// <summary>立刻终止补帧、视觉强制等于终态，并把冻结的栅格落回真排版。一切"看到的必须就是最终
    /// 几何"的时机都要先调它：提交收口、拖边起手、平移起手、进标注/缩略图、换图、换锚点。</summary>
    private void StopZoomEase()
    {
        _zoomEasing = false;
        _zoomV = _zoomVTarget;
        if (_zoomFx != null) _zoomFx.ScaleX = _zoomFx.ScaleY = _zoomV;
        // 变换是"画面之外"的第二套坐标：留着它，任何按 Margin 算几何的通路都会差一个倍率，
        // 所以终止必须连带把各层宽高追平 _scale（ApplyScale 内部把倍率与 _gestureRasterScale 一起归位）。
        SettleZoomRaster();
    }

    /// <summary>「图片多大，窗口就多大」的**唯一**窗口矩形公式：内容屏幕物理矩形 <c>_wheelImg*</c>
    /// 外扩一圈 <see cref="ShadowMargin"/>，尺寸封顶工作区，位置保留最小可见内容。
    /// 手势每帧（<see cref="ApplyFittedWindowRect"/>）与松手提交（<see cref="CommitWheelGesture"/>）
    /// 共用本式 ⇒ 手势里看到的窗口矩形与松手后的恒等，"提交那一帧整窗 resize"这一类闪烁从结构上
    /// 消失。夹小的部分不丢像素：越出视口的内容由相机 offset 平移查看（阶段 1 已与信封解耦）。
    /// 拖边期（<c>_isResizing</c>）改用 <see cref="ClampRectKeepPinnedEdge"/>：对侧边逐像素钉死、
    /// 越界只削被拖的一侧——通用的平移式钳制会把钉死的边一起挪走（v7 实测上提 264px＝"跳一大跳"）。
    /// 位置算式不再为拖边单独写一份：内容左缘本就按"<c>_resizeAnchorX</c> − 内容宽"钉着对侧边，
    /// 与旧的 <c>锚点 + 阴影 − 窗口宽</c> 只差 SnapStablePx 的量化余量。</summary>
    private void ComputeFittedWindowRectPx(double s, out int xPx, out int yPx, out int wPx, out int hPx)
        => ComputeFittedWindowRectPx(s, out xPx, out yPx, out wPx, out hPx, out _);

    /// <summary><inheritdoc cref="ComputeFittedWindowRectPx(double, out int, out int, out int, out int)"/>
    /// <para><paramref name="movedByClamp"/>＝"位置被工作区钳制搬动过"，供
    /// <see cref="ApplyFittedWindowRect"/> 切断回灌闭环用（2026-10-01）。</para></summary>
    private void ComputeFittedWindowRectPx(double s, out int xPx, out int yPx, out int wPx, out int hPx, out bool movedByClamp)
        => ComputeFittedWindowRectPx(s, out xPx, out yPx, out wPx, out hPx, out movedByClamp, out _);

    /// <summary>拟合查询不修改图片簿记，边界引起的整体平移由真正提交窗口的调用方应用。</summary>
    private void ComputeFittedWindowRectPx(double s, out int xPx, out int yPx, out int wPx, out int hPx,
        out bool movedByClamp, out Avalonia.Vector boundaryShift)
        => ComputeFittedWindowRectCore(s, out xPx, out yPx, out wPx, out hPx, out movedByClamp, out boundaryShift,
            GetStickerDisplayRects());

    private void ComputeFittedWindowRectCore(double s, out int xPx, out int yPx, out int wPx, out int hPx,
        out bool movedByClamp, out Avalonia.Vector boundaryShift, IReadOnlyList<System.Drawing.Rectangle> workAreas)
    {
        movedByClamp = false;
        boundaryShift = default;
        double mPx = ShadowMargin * s;
        double cL = _wheelImgLpx, cT = _wheelImgTpx, cW = _wheelImgWpx, cH = _wheelImgHpx;
        var work = WorkAreaBounds(workAreas);

        if (UsesImageViewport && !_isResizing && work.Width > 0 && work.Height > 0)
        {
            // 创建、拖动、缩放与收尾只读同一份图片矩形；可见窗口由它投影，不保留旧裁切范围。
            boundaryShift = GetImagePlacementShift(cL, cT, cW, cH, s, workAreas);
            movedByClamp = boundaryShift != default;
            cL += boundaryShift.X; cT += boundaryShift.Y;
            if (_wheelGestureActive && !_isPanningContent && _wheelEnvW > 0 && _wheelEnvH > 0)
            {
                // 补帧尚在上一档尺寸时保留已显示范围；所有图片同用，收尾再贴合最终画面。
                double right = Math.Max(cL + cW, _wheelEnvL + _wheelEnvW);
                double bottom = Math.Max(cT + cH, _wheelEnvT + _wheelEnvH);
                cL = Math.Min(cL, _wheelEnvL); cT = Math.Min(cT, _wheelEnvT);
                cW = right - cL; cH = bottom - cT;
            }
            FitImageViewportAxis(cL, cW, work.Left, work.Width, mPx, out cL, out cW);
            FitImageViewportAxis(cT, cH, work.Top, work.Height, mPx, out cT, out cH);
            xPx = (int)Math.Round(cL - mPx); yPx = (int)Math.Round(cT - mPx);
            wPx = Math.Min(work.Width, Math.Max(1, SnapStablePx(cW / s + 2 * ShadowMargin, s)));
            hPx = Math.Min(work.Height, Math.Max(1, SnapStablePx(cH / s + 2 * ShadowMargin, s)));
            return;
        }

        // 以下为非图片与拖边兼容路径；普通图片和长图的常规查看已统一在上方出口。
        // —— 手势期窗口"装得下就不动，装不下才跳一档"（2026-09-23 治抖，依据见 StickerStyling 的
        //    ZoomGestureHeadroom）——
        // 单变量实验已确认：抖的是"改窗口尺寸"这个动作（每改一次＝合成表面重建一次，重建那一帧
        // 显示旧尺寸或漏画新边），而改位置、重排版、重采样画面都不抖。于是：
        //   · 现视口还装得下画面 → 窗口矩形**原样不动**（尺寸与位置都不改；画面靠 Margin 钉在锚点
        //     公式给出的屏幕位置上，所以动的是画面、不是窗口）。
        //   · 装不下 → 一次长到"画面绕**锚点**外扩一档"：左右两侧各按锚点在画面里的占比分余量，
        //     于是两缘逼近窗口边的速度相同，0.25 档在任意锚点位置下都够走 log(1.25)/log(1.1)≈2.2 格
        //     （若把余量一律堆在右下，鼠标停在画面左缘附近时下一格就会立刻再次越界＝每格改一次尺寸，
        //     等于没治）。滚轮每格 ×1.1 ⇒ 约每 2~3 格才真改一次尺寸。
        // 长大时还要**并上现视口**：补帧期间的画面可能比本格终态大（缩小方向的起点＝上一格尺寸），
        // 而上一格画面必在现视口内 ⇒ 并集罩得住，"上一格可见区 ∪ 本格终态"那套单独簿记（原
        // _zoomSweep*）随之作废——同一件几何只算一遍。
        // ⚠ 只判"装不下"、不判"太空" ⇒ 缩小方向一格尺寸都不改，窗口停在起手那一档（实测内容缩到
        //    657×464 时窗口仍停在 2455×1372＝约 12 倍面积），收口时还要来一次大 resize。这条是不是
        //    真缺陷尚未定（同日用户对照反而质疑"白边大小不决定卡不卡"），详见 StickerStyling
        //    .ZoomGestureHeadroom 的注释——别照着"白边太大"去动这里的判据。
        //    落定收口时 _wheelGestureActive 已为 false ⇒ 整块跳过，缩回严格贴合。
        if (_wheelGestureActive && !_isResizing)
        {
            // 分层呈现档：改窗口尺寸不再闪（位置/尺寸/像素一次 ULW 提交），余量的唯一用途"少改尺寸"失效，
            // 留着只会留下吞点击的透明圈 ⇒ 按 0 处理＝手势期窗口也严格贴合画面（"并上现视口"那条仍保留，
            // 缩小方向的补帧起点比终态大，窗口不先缩就不会把正在长回去的画面裁掉）。
            double h = PinLayeredActive ? 0 : StickerStyling.ZoomGestureHeadroom;
            if (_wheelEnvW >= cW && _wheelEnvH >= cH)
            {
                cL = _wheelEnvL; cT = _wheelEnvT;
                cW = _wheelEnvW; cH = _wheelEnvH;
            }
            else
            {
                // 锚点在画面里的占比（0＝左缘、1＝右缘）；锚点未采到（NaN）时按居中分
                double ax = cW > 0 ? Math.Clamp((_wheelGestureAnchorPx.X - cL) / cW, 0.0, 1.0) : 0.5;
                double ay = cH > 0 ? Math.Clamp((_wheelGestureAnchorPx.Y - cT) / cH, 0.0, 1.0) : 0.5;
                if (double.IsNaN(ax)) ax = 0.5;
                if (double.IsNaN(ay)) ay = 0.5;
                double wantL = cL - cW * h * ax, wantR = cL + cW * (1.0 + h * (1.0 - ax));
                double wantT = cT - cH * h * ay, wantB = cT + cH * (1.0 + h * (1.0 - ay));
                cL = Math.Min(wantL, _wheelEnvL);
                cT = Math.Min(wantT, _wheelEnvT);
                cW = Math.Max(wantR, _wheelEnvL + _wheelEnvW) - cL;
                cH = Math.Max(wantB, _wheelEnvT + _wheelEnvH) - cT;
            }
        }

        // 超长图的图片原点包含内部平移偏移，绝不能拿它当视口原点。
        // 静止/失活收尾保留已选视口。查看过程中，超过整屏的轴改取图片与工作区的可见交集：
        // 只固定旧原点会让 y=324 的视口一直裁掉其上方本该显示的图片，阴影还会掩盖这条裁切边。
        // 相机只跟随屏幕内可见部分，绝不跟随长图的屏外原点；图片屏幕坐标保持不变。
        if (_wheelVisualOverride && !_isResizing && work.Width > 0 && work.Height > 0)
        {
            double maxW = Math.Max(1, work.Width - mPx * 2), maxH = Math.Max(1, work.Height - mPx * 2);
            if (_wheelEnvW > 0 && (_wheelImgWpx > maxW + 1 || (!_wheelGestureActive && _wheelImgWpx > _wheelEnvW + 1)))
            { cL = _wheelEnvL; cW = Math.Min(_wheelGestureActive ? cW : _wheelEnvW, maxW); }
            if (_wheelEnvH > 0 && (_wheelImgHpx > maxH + 1 || (!_wheelGestureActive && _wheelImgHpx > _wheelEnvH + 1)))
            { cT = _wheelEnvT; cH = Math.Min(_wheelGestureActive ? cH : _wheelEnvH, maxH); }
            if (_wheelGestureActive || _isPanningContent)
            {
                if (_wheelImgWpx > maxW + 1)
                    FitVisibleOverflowAxis(_wheelImgLpx, _wheelImgWpx, work.Left + mPx, maxW, ref cL, ref cW);
                if (_wheelImgHpx > maxH + 1)
                    FitVisibleOverflowAxis(_wheelImgTpx, _wheelImgHpx, work.Top + mPx, maxH, ref cT, ref cH);
            }
        }

        xPx = (int)Math.Round(cL - mPx);
        yPx = (int)Math.Round(cT - mPx);
        wPx = Math.Max(1, SnapStablePx(cW / s + ShadowMargin * 2, s));
        hPx = Math.Max(1, SnapStablePx(cH / s + ShadowMargin * 2, s));

        if (_isResizing && _resizeEnvelope)
        {
            ClampRectKeepPinnedEdge(ref xPx, ref yPx, ref wPx, ref hPx,
                                    EdgeDragsWest(_resizeEdge), EdgeDragsNorth(_resizeEdge), work);
        }
        int beforeX = xPx, beforeY = yPx;
        var imageRect = new Avalonia.Rect(_wheelImgLpx - xPx, _wheelImgTpx - yPx, _wheelImgWpx, _wheelImgHpx);
        ClampRectToWorkArea(ref xPx, ref yPx, ref wPx, ref hPx, s, out movedByClamp, imageRect, workAreas);
        boundaryShift = new Avalonia.Vector(xPx - (double)beforeX, yPx - (double)beforeY);
    }

    /// <summary>只包住超屏图片在工作区内的可见段，不裁源图、不挪动内容，也不生成巨幅窗口。</summary>
    private static void FitVisibleOverflowAxis(double imageStart, double imageSize, double workStart,
        double workSize, ref double viewportStart, ref double viewportSize)
    {
        double start = Math.Max(imageStart, workStart);
        double end = Math.Min(imageStart + imageSize, workStart + workSize);
        if (end <= start) return; // 完全在屏外由原最小可见位置规则处理，不能追着屏外原点跑。
        viewportStart = start;
        viewportSize = end - start;
    }

    // =====================================================================
    // 位置振荡检测（2026-10-01，issue #52 跟进采证）
    // =====================================================================
    // 用户报「贴图瞬间跳过去，有时跳一次，有时来回跳十几次」，而本机未能复现。闭环已按假设切断，
    // 但"真因是不是这个环"需要现场数据定性，故留一道**低成本、只读**的检测：
    //   窗口位置每次真改都记一笔，在 `OscWindowMs` 窗口内统计不同落点的个数与改位次数。
    //   短时间内反复在少数几个落点间来回＝振荡（典型是 2 个落点来回），一次性搬到新位置则不是。
    // 触发阈值 `OscMinFlips` 取 4：正常操作里一次手势内连改 4 次位置且落点重复出现，已不可能是用户意图。
    // ⚠ 只打一条日志、**不做任何自动纠正**：真要在这里"自动停住"就会压掉合法的连续移动（拖动每帧都改位）。
    //    判读入口统一为日志关键字「位置振荡」。
    private const int OscWindowMs = 1200;
    private const int OscMinFlips = 4;
    private readonly List<(long Stamp, int X, int Y)> _oscTrail = new();
    private long _oscLastReportStamp;

    /// <summary>记一次"窗口位置真的改了"，并在检出振荡时留痕。只读统计，不改任何几何。</summary>
    private void TrackWindowPositionForOscillation(int xPx, int yPx, string trigger)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // 掉出时间窗的样本直接丢（列表恒很短，无需额外容量管理）
        for (int i = _oscTrail.Count - 1; i >= 0; i--)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_oscTrail[i].Stamp, now).TotalMilliseconds > OscWindowMs)
                _oscTrail.RemoveAt(i);
        }
        if (_oscTrail.Count > 0)
        {
            var last = _oscTrail[^1];
            if (last.X == xPx && last.Y == yPx) return;   // 位置没变：不是一次"改位"
        }
        _oscTrail.Add((now, xPx, yPx));
        if (_oscTrail.Count < OscMinFlips) return;

        // 落点去重：个数远小于改位次数＝在少数落点间来回＝振荡；个数≈改位次数＝正常移动
        var distinct = new HashSet<(int, int)>();
        foreach (var t in _oscTrail) distinct.Add((t.X, t.Y));
        if (distinct.Count > _oscTrail.Count / 2) return;   // 落点基本都不重复：正常连续移动

        // 同一段振荡只报一次（否则会把日志刷满，反而淹掉上下文）
        if (_oscLastReportStamp != 0 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_oscLastReportStamp, now).TotalMilliseconds < OscWindowMs) return;
        _oscLastReportStamp = now;

        var sb = new System.Text.StringBuilder();
        foreach (var t in _oscTrail) sb.Append($"({t.X},{t.Y})");
        WheelDiag($"位置振荡[{trigger}]：{OscWindowMs}ms 内改位 {_oscTrail.Count} 次、仅 {distinct.Count} 个不同落点 "
                  + $"轨迹={sb} 工作区={GetStableWorkAreaUnion()} "
                  + $"内容({_wheelImgWpx:F0}x{_wheelImgHpx:F0})@({_wheelImgLpx:F0},{_wheelImgTpx:F0}) "
                  + $"视口({_wheelEnvW:F0}x{_wheelEnvH:F0})@({_wheelEnvL:F0},{_wheelEnvT:F0}) "
                  + $"覆写={_wheelVisualOverride} 手势={_wheelGestureActive} 拖动={_isDragging} 拖边={_isResizing}");
    }

    /// <summary>贴合矩形与真实窗口矩形是否不同。拿不到句柄时一律判"不同"，
    /// 把决定权交给 RAF 里的 <see cref="ApplyFittedWindowRect"/>（它落地前还会再比一次）。</summary>
    private bool FittedWindowRectDiffers(double s)
    {
        ComputeFittedWindowRectPx(s, out int xPx, out int yPx, out int wPx, out int hPx,
            out _, out var boundaryShift);
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero ||
            !ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc)) return true;
        return rc.Left != xPx || rc.Top != yPx || rc.Width != wPx || rc.Height != hPx
            || boundaryShift != default;
    }

    /// <summary>拖边尺寸封顶时只削被拖一侧，对侧保持不动；位置越过屏边不再削尺寸。
    /// 之后仅在不足最小可见内容时整体移回，不恢复把整窗拉进工作区的旧规则。</summary>
    private static void ClampRectKeepPinnedEdge(ref int xPx, ref int yPx, ref int wPx, ref int hPx,
                                                bool keepRight, bool keepBottom, System.Drawing.Rectangle wa)
    {
        if (wa.Width <= 0 || wa.Height <= 0) return;
        if (keepRight)
        {
            int right = xPx + wPx;                    // 钉死的窗口右缘
            if (wPx > wa.Width) { wPx = Math.Max(1, wa.Width); xPx = right - wPx; }
        }
        else if (wPx > wa.Width) wPx = Math.Max(1, wa.Width);

        if (keepBottom)
        {
            int bottom = yPx + hPx;                   // 钉死的窗口下缘
            if (hPx > wa.Height) { hPx = Math.Max(1, wa.Height); yPx = bottom - hPx; }
        }
        else if (hPx > wa.Height) hPx = Math.Max(1, wa.Height);
    }

    /// <summary>工作区可显示内容尺寸上限（＝工作区尺寸减去两侧阴影边距各一次）；
    /// 拿不到合法工作区时返回无穷。2026-09-23 阶段 2 起窗口几何不再需要这个封顶（贴合矩形
    /// 直接由 <see cref="ClampRectToWorkArea"/> 夹），现存唯一消费处是文本贴图重排：
    /// 版宽可以排得比屏幕还宽，而排出去的那部分不产生像素＝"背景被裁掉一截"，
    /// 故拖到工作区边界就该停手（观感＝"已经放到最大"）。</summary>
    private static void WorkAreaSurfaceLimit(double s, out double limitW, out double limitH)
    {
        double mPx = ShadowMargin * s;
        var wa = ScreenCaptureTool.Platform.MonitorHelper.GetWorkAreaUnion();
        limitW = wa.Width > 0 ? Math.Max(1.0, wa.Width - mPx * 2) : double.PositiveInfinity;
        limitH = wa.Height > 0 ? Math.Max(1.0, wa.Height - mPx * 2) : double.PositiveInfinity;
    }

    /// <summary>单轴的内容位置约束：把内容按<b>最小位移</b>收进视口的合法区间。
    /// 合法区间两端分别是"内容左缘贴视口左缘"与"内容右缘贴视口右缘"，谁在前取决于内容
    /// 比视口小还是大，故用 min/max 一式覆盖两种情形：装得下→整块收进视口内；装不下→
    /// 只保证内容边缘不离开视口边缘（越出留在哪一侧由锚点决定，**不居中**——居中会把鼠标
    /// 下那个点整块挪走，也把拖边钉死的对侧边挪走）。内容本就合法时是空操作。</summary>
    private static double ClampContentAxis(double contentL, double envL, double envW, double contentW)
        => Math.Clamp(contentL, Math.Min(envL, envL + envW - contentW),
                            Math.Max(envL, envL + envW - contentW));

    /// <summary>越出轴按真正可见的屏幕区域取平移范围；装得下的轴仍由窗口移动承载。</summary>
    private Avalonia.Rect GetContentPanBounds(Avalonia.Rect viewport)
    {
        Avalonia.Rect visible = default;
        bool found = false;
        // 使用框架维护的屏幕读数，不在鼠标合帧中增加 Win32 屏幕枚举。
        foreach (var screen in Screens.All)
        {
            var work = screen.WorkingArea;
            var part = viewport.Intersect(new Avalonia.Rect(work.X, work.Y, work.Width, work.Height));
            if (part.Width <= 0 || part.Height <= 0) continue;
            visible = found ? visible.Union(part) : part;
            found = true;
        }
        if (!found) return viewport;
        return new Avalonia.Rect(
            _wheelImgWpx > viewport.Width + 1 ? visible.X : viewport.X,
            _wheelImgHpx > viewport.Height + 1 ? visible.Y : viewport.Y,
            _wheelImgWpx > viewport.Width + 1 ? visible.Width : viewport.Width,
            _wheelImgHpx > viewport.Height + 1 ? visible.Height : viewport.Height);
    }

    /// <summary>把内容位置按 <see cref="ClampContentAxis"/> 收回当前视口（<c>_wheelEnv*</c>）。
    /// 阶段 2 起这是内容位置**唯一**被改写的地方（平移通路除外），且只在窗口矩形落地之后调用：
    /// 窗口是按内容算的，只有被工作区夹小的那一侧才会把内容挤出视口。
    /// ⚠ 必须在 <see cref="SetWheelGestureVisual"/> 之前调用：各层 Margin 与命中矩形由这里的
    /// 输出位置反解，顺序颠倒会把未约束的中间态烤进 Margin（表现为内容整体坐偏、边缘被裁）。
    /// ⚠ **只能是最小位移，不能"钉到视口原点"**：2026-09-23 曾把装得下的轴改成"Margin 恒 0"，
    /// 用来赌"浮点画面对整数窗口逐格回弹"是抖动来源——实验否证（改完照抖，见治抖结论），而它有一个
    /// 实打实的破坏：平移常驻态下画面本来就合法地停在视口内某个偏移上（那是用户拖出来的视野），
    /// 一旦缩放让它装得进窗口就被拽回左上角 ⇒ 用户报的"平移以后再缩放会跑到左上方去"。已撤销。</summary>
    private void ClampContentIntoViewport()
    {
        if (!_wheelVisualOverride || _wheelEnvW <= 0 || _wheelEnvH <= 0) return;
        if (UsesImageViewport) return; // 图片位置已由统一放置规则约束；不能再用输出视口反向拉回图片。

        double oldL = _wheelImgLpx, oldT = _wheelImgTpx;
        var bounds = GetContentPanBounds(new Avalonia.Rect(_wheelEnvL, _wheelEnvT, _wheelEnvW, _wheelEnvH));
        _wheelImgLpx = ClampContentAxis(oldL, bounds.X, bounds.Width, _wheelImgWpx);
        _wheelImgTpx = ClampContentAxis(oldT, bounds.Y, bounds.Height, _wheelImgHpx);

        // "被收回"以 1px 为界：窗口 DIP 走 SnapStablePx 量化，视口常比内容大 0.x px，于是每格都会
        // 产生一次亚像素挪动。按"精确相等"判不变，日志里就会是 6 条中 5 条 (1207,650)→(1207,650)
        // 这种"打了却看不出动了"的行，把真正被挤出视口的那一条（Δ 几十 px）淹掉。
        if (Math.Abs(_wheelImgLpx - oldL) < 1.0 && Math.Abs(_wheelImgTpx - oldT) < 1.0)
        {
            _wheelPosConstrainedLogged = false;   // 已回到合法位置：标记复位，下次再约束还能打一条
            return;
        }
        if (_wheelPosConstrainedLogged) return;
        _wheelPosConstrainedLogged = true;
        WheelDiag($"内容收回视口：({oldL:F0},{oldT:F0})→({_wheelImgLpx:F0},{_wheelImgTpx:F0}) " +
                  $"内容({_wheelImgWpx:F0}x{_wheelImgHpx:F0}) " +
                  $"视口({_wheelEnvW:F0}x{_wheelEnvH:F0})@({_wheelEnvL:F0},{_wheelEnvT:F0})");
    }

    /// <summary>把内容移动到指定屏幕物理位置。图片统一用屏幕边界与最小可见规则，不按旧视口钳制；
    /// 以下逐轴钳制描述只供非图片兼容路径（相机 offset 的唯一写入口）。
    /// 两轴都用 <see cref="ClampContentAxis"/>（内容边缘最多贴到视口边缘，不许拖出留白）；
    /// 越出的轴由内容自己滑动承载，装得下的轴**先由窗口搬运**（见下面"逐轴分工"），
    /// 窗口被工作区夹住而没搬够的那一段差额，再由那一轴上窗口的透明余量吸收。
    /// 返回是否真的产生了位移（false ⇒ 本帧无需重画）。
    /// <paramref name="isFinalFrame"/> 为 true 时表示手势收尾结算帧，会同步所有图层几何并确保最终落定。
    /// 视口取 <see cref="GetViewportRectPx"/>（活状态），内容尺寸取起手冻结的 <c>_wheelImg*</c>
    /// 而非每帧重读——同一次拖动中途翻判据会让"平移"变成"拖窗口"（整张贴图飞走）。</summary>
    private bool ApplyContentPan(double targetLpx, double targetTpx, bool isFinalFrame = false)
    {
        var vp = GetViewportRectPx();
        if (vp.Width <= 0) return false;

        if (UsesImageViewport)
        {
            // 与普通拖窗相同：图片位置＝起手图片位置＋指针总位移。大小只决定是否需要重画可见表面。
            var screens = GetStickerDisplayRects();
            var shift = GetImagePlacementShift(targetLpx, targetTpx, _wheelImgWpx, _wheelImgHpx, SafeScaling, screens);
            double oldX = _wheelImgLpx, oldY = _wheelImgTpx;
            _wheelImgLpx = targetLpx + shift.X; _wheelImgTpx = targetTpx + shift.Y;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyFittedWindowRectCore(screens);
            vp = GetViewportRectPx();
            SetWheelGestureVisual((_wheelImgLpx - vp.X) / SafeScaling, (_wheelImgTpx - vp.Y) / SafeScaling,
                StickerImage.Width, StickerImage.Height, forceAllLayers: isFinalFrame);
            SampleDragPanFrame(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return oldX != _wheelImgLpx || oldY != _wheelImgTpx;
        }

        // —— 逐轴分工（2026-09-22 修「左右平移受阻」）——
        // 越出视口的轴：平移内容（内容在视口内滑动，窗口不动）。
        // **装得下的轴：改为平移窗口**，而不是把内容钳死在视口里。
        // 为什么：长图典型形态是"高越出、宽装得下"（实测 内容1699x1187 / 视口1888x1000），
        // 旧写法把 X 钳在 [envL, envL+envW-imgW] 这个极窄区间里 ⇒ 用户斜着拖时纵向跟手、
        // 横向被拽住，手感就是用户报的"左右平移会受阻、不能自由平移"。而装得下的轴本来就
        // 没有"内容内部可看的余量"，该轴上唯一有意义的动作正是"把贴图挪到屏幕别处"。
        // 于是：任何方向都拖得动 —— 越出轴看内容、装得下轴挪窗口，两者在同一次拖动里并存。
        bool panX = _wheelImgWpx > vp.Width + 1.0;
        bool panY = _wheelImgHpx > vp.Height + 1.0;

        // ① 先把"装得下那一轴"的位移交给窗口（move-only，DWM 只平移既有表面、不重绘）。
        // ⚠ 目标位置**必须**按"窗口起手原点 + 光标总位移"绝对式算，不得用"目标 − 当前"增量式：
        //    ApplyDragMove 会把 _wheelEnv*/_wheelImg* 一起同移，"当前值"下一帧已含上一帧位移，
        //    增量式会把它再算一次 ⇒ 正反馈自激（实测窗口 x 从 688 飞到 −14972，请求仅 −300）。
        // ⚠ 顺序也不可颠倒：窗口一移，视口原点随之变，②的钳制区间才是本帧的真值；
        //    先钳后移会让内容按上一帧的区间落位，斜拖时表现为越出轴轻微回弹。
        if (!panX || !panY)
        {
            var currentOrigin = GetWindowOriginPx();
            int wantX = panX ? currentOrigin.X
                             : _panWinStart.X + (int)Math.Round(targetLpx - _panStartImgLpx);
            int wantY = panY ? currentOrigin.Y
                             : _panWinStart.Y + (int)Math.Round(targetTpx - _panStartImgTpx);
            ApplyDragMove(new PixelPoint(wantX, wantY));
            vp = GetViewportRectPx();       // 挪过窗口 ⇒ 重取视口原点（尺寸不变）
            if (vp.Width <= 0) return false;
        }

        // ② 再按（可能已同移的）视口钳制内容位置——两轴同一条公式（`ClampContentAxis`，与
        //    ClampContentIntoViewport 同源），差别只在"这一轴的位移谁搬运"：
        //    · 越出轴：内容在视口内滑动，内容边缘最多贴到视口边缘（不许拖出留白）。
        //    · 装得下的轴：**吸收①没搬够的那一段**。窗口能搬的距离会被工作区夹住，而窗口比画面
        //      大时那一圈透明余量还没用上——2026-09-24 上午实测「没办法自由平移，只有左右平移」
        //      就是这里只搬窗口、余量白摆着：画面 1152×1242 配视口 2109×2040，窗口 2088 已顶满
        //      工作区全高 ⇒ Y 轴合法区间 [0,0] ⇒ 上下一点拖不动，而 X 轴还有 1683px 余量可搬。
        //    窗口搬到位时本式是空操作（画面目标本就落在视口内，Margin 原样续接），故不给静止态
        //    带来任何变化（静止态窗口＝画面＋阴影，两侧零余量，区间两端重合于当前值）。
        double oldL = _wheelImgLpx, oldT = _wheelImgTpx;
        var bounds = GetContentPanBounds(vp);
        bool fitVisibleOverflow = false;
        IReadOnlyList<System.Drawing.Rectangle>? panWorkAreas = null;
        if (_isPanningContent)
        {
            panWorkAreas = GetStableWorkAreaRects();
            var work = WorkAreaBounds(panWorkAreas);
            double margin = ShadowMargin * SafeScaling;
            double maxW = Math.Max(1, work.Width - 2 * margin), maxH = Math.Max(1, work.Height - 2 * margin);
            bool revealX = work.Width > 0 && _wheelImgWpx > maxW + 1;
            bool revealY = work.Height > 0 && _wheelImgHpx > maxH + 1;
            // 超屏长图按整屏范围限制首尾，再让相机包住本帧可见段。先按旧视口钳制会把待显示段删掉。
            // 首/尾本来就在屏内时保留起手位置，不能第一帧为填满屏幕把内容突然拉到屏边。
            double left = Math.Max(work.Left + margin, _panStartImgLpx);
            double top = Math.Max(work.Top + margin, _panStartImgTpx);
            double right = Math.Min(work.Right - margin, _panStartImgLpx + _wheelImgWpx);
            double bottom = Math.Min(work.Bottom - margin, _panStartImgTpx + _wheelImgHpx);
            bounds = new Avalonia.Rect(revealX ? left : bounds.X, revealY ? top : bounds.Y,
                revealX ? Math.Max(1, right - left) : bounds.Width, revealY ? Math.Max(1, bottom - top) : bounds.Height);
            fitVisibleOverflow = revealX || revealY;
        }
        _wheelImgLpx = ClampContentAxis(targetLpx, bounds.X, bounds.Width, _wheelImgWpx);
        _wheelImgTpx = ClampContentAxis(targetTpx, bounds.Y, bounds.Height, _wheelImgHpx);

        if (fitVisibleOverflow)
        {
            ApplyFittedWindowRectCore(panWorkAreas);
            vp = GetViewportRectPx();
        }

        if (_wheelImgLpx == oldL && _wheelImgTpx == oldT)
        {
            if (isFinalFrame)
            {
                double sFinal = SafeScaling;
                SetWheelGestureVisual((_wheelImgLpx - vp.X) / sFinal, (_wheelImgTpx - vp.Y) / sFinal,
                                      StickerImage.Width, StickerImage.Height, forceAllLayers: true);
            }
            return false;
        }

        // 复用手势视觉入口：Margin 由"内容屏幕位置 − 视口原点"反解，与滚轮/拖边同一条通路
        double s = SafeScaling;
        long panT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SetWheelGestureVisual((_wheelImgLpx - vp.X) / s, (_wheelImgTpx - vp.Y) / s,
                              StickerImage.Width, StickerImage.Height, forceAllLayers: isFinalFrame);
        // 只量到"写几何"这一句为止；随之而来的整窗重排＋整面重绘在渲染阶段，本表盖不住（用面积换算当参照）
        SampleDragPanFrame(System.Diagnostics.Stopwatch.GetElapsedTime(panT0).TotalMilliseconds);
        return true;
    }

    // =====================================================================
    // 每帧成本分解埋点（2026-09-27，纯只读日志、不改任何行为）
    // =====================================================================
    // 补这两块的直接原因：此前所有"卡"的归因都卡在**没有数**上——
    //   ① 缩放：托管段秒表实测 0.07ms/帧，但它按代码自己的注释就"盖不住改窗口/布局/阴影"，
    //      而台账里两种主嫌疑（"改窗口尺寸这个动作"vs"整面重绘随面积走"）分不出主次。
    //      ⇒ 这里把 `SetWindowPos` 那一句**单独掐表**（它是同步调用，贵就贵在这一行里），
    //        重绘那笔仍然量不到（在合成线程），只能用视口面积换算（0.6ms/MPx）当参照。
    //   ② 拖动：日志里**一条都没有**。而拖动有两条成本差一个量级的分支（挪窗＝1 次同步调用、
    //      零排版；挪内容＝每帧重写各层 Margin＝整窗重排＋整面重绘），必须分开记才能回答
    //      用户问的"一个纯移动为什么会占这么大"。
    // 汇总只在**松手／收口**时各打一行：每帧写日志会把被量的东西量歪（2026-09-23 的教训）。
    private int _dragEvents, _dragWindowMoves, _dragPanFrames;
    private double _dragEventGapMsTotal, _dragEventGapMsMax;
    private double _dragWindowMsTotal, _dragWindowMsMax;
    private double _dragPanMsTotal, _dragPanMsMax, _dragPanGapMsTotal;
    private long _dragLastEventStamp, _dragLastPanStamp;

    private int _fitResizes;
    private double _fitSetPosMsTotal, _fitSetPosMsMax;

    /// <summary>每个拖动的 PointerMoved 事件记一次节拍（事件间隔＝鼠标回报率与跟手感的代理量）。</summary>
    private void SampleDragEvent()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_dragLastEventStamp != 0)
        {
            double gap = System.Diagnostics.Stopwatch.GetElapsedTime(_dragLastEventStamp, now).TotalMilliseconds;
            _dragEventGapMsTotal += gap;
            if (gap > _dragEventGapMsMax) _dragEventGapMsMax = gap;
        }
        _dragLastEventStamp = now;
        _dragEvents++;
    }

    /// <summary>"挪窗"分支：一次 move-only <c>SetWindowPos</c> 的同步耗时。</summary>
    private void SampleDragWindowMove(double ms)
    {
        _dragWindowMoves++;
        _dragWindowMsTotal += ms;
        if (ms > _dragWindowMsMax) _dragWindowMsMax = ms;
    }

    /// <summary>"挪内容"分支：一帧重写各层 Margin 的托管耗时＋帧间隔（帧间隔＝实际出帧节奏）。</summary>
    private void SampleDragPanFrame(double ms)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_dragLastPanStamp != 0)
            _dragPanGapMsTotal += System.Diagnostics.Stopwatch.GetElapsedTime(_dragLastPanStamp, now).TotalMilliseconds;
        _dragLastPanStamp = now;
        _dragPanFrames++;
        _dragPanMsTotal += ms;
        if (ms > _dragPanMsMax) _dragPanMsMax = ms;
    }

    /// <summary>松手时打一行拖拽汇总并复位。两条分支的次数与耗时并列，谁在干活一眼可见。</summary>
    private void LogDragSegment(string reason)
    {
        if (_dragEvents == 0) return;
        double evAvg = _dragEvents > 1 ? _dragEventGapMsTotal / (_dragEvents - 1) : 0;
        double winAvg = _dragWindowMoves > 0 ? _dragWindowMsTotal / _dragWindowMoves : 0;
        double panAvg = _dragPanFrames > 0 ? _dragPanMsTotal / _dragPanFrames : 0;
        double panGap = _dragPanFrames > 1 ? _dragPanGapMsTotal / (_dragPanFrames - 1) : 0;
        WheelDiag($"拖拽段采证[{reason}]：事件={_dragEvents} 事件间隔均值={evAvg:F1}ms 最大={_dragEventGapMsMax:F1}ms "
                  + $"｜挪窗={_dragWindowMoves}次 均值={winAvg:F3}ms 最大={_dragWindowMsMax:F2}ms "
                  + $"｜挪内容={_dragPanFrames}帧 均值={panAvg:F3}ms 最大={_dragPanMsMax:F2}ms 帧间隔均值={panGap:F1}ms "
                  + $"｜画面={StickerImage.Width * SafeScaling:F0}x{StickerImage.Height * SafeScaling:F0}px "
                  + $"视口={_wheelEnvW:F0}x{_wheelEnvH:F0}px"
                  + TakePinStats());
        _dragEvents = 0; _dragWindowMoves = 0; _dragPanFrames = 0;
        _dragEventGapMsTotal = 0; _dragEventGapMsMax = 0;
        _dragWindowMsTotal = 0; _dragWindowMsMax = 0;
        _dragPanMsTotal = 0; _dragPanMsMax = 0; _dragPanGapMsTotal = 0;
        _dragLastEventStamp = 0; _dragLastPanStamp = 0;
    }

    private long _wheelCloakStamp;   // cloak 挂起时刻（Stopwatch 时间戳）；0＝当前未 cloak


    /// <summary>设置/解除 DWM cloak（DWMWA_CLOAK=13）：cloak 期间 DWM 完全不合成该窗口。
    /// 用于信封涨/收两次 resize 的瞬间——DWM 拿旧帧拉伸贴合新窗口矩形是 OS 固有行为
    /// （无法关闭），cloak 让这一两帧"无处出现"，下一渲染帧再揭开，从根上消除
    /// "缩小瞬间残影落在左上角"的鬼影帧。</summary>
    /// <remarks><b>2026-09-06 实测校正</b>：本方法原注释写着"窗口隐匿约 1 帧 ≈16ms，肉眼不可感知"，
    /// 该结论已被用户实测证伪——滚轮缩放后按空格进标注，紧跟这次 cloak 的"贴图整张消失一下"
    /// 清晰可辨（揭开靠下一渲染帧回调，与 resize 未必同帧，实际跨几帧一直没量过）。故此处补上
    /// 计时日志作为验收基线，并把"进入标注"这条路径上的缩窗整个挪走（见 <see cref="CommitWheelGesture"/>
    /// 的标注态守卫）；不要照旧注释认为隐匿不可见。</remarks>
    private void SetWheelResizeCloak(bool cloaked)
    {
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        int val = cloaked ? 1 : 0;
        ScreenCaptureTool.Platform.NativeMethods.DwmSetWindowAttributeInt(hwnd, 13, ref val, sizeof(int));

        if (cloaked)
        {
            _wheelCloakStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            return;
        }
        if (_wheelCloakStamp == 0) return;
        double hiddenMs = System.Diagnostics.Stopwatch.GetElapsedTime(_wheelCloakStamp).TotalMilliseconds;
        _wheelCloakStamp = 0;
        WheelDiag($"cloak 揭开：整窗隐匿 {hiddenMs:F1}ms（60Hz 下一帧 16.7ms，超 1 帧即可感知）");
    }

    /// <summary>调度渲染帧回调解除 cloak。<b>必须连排两格</b>：Avalonia 的 RAF 回调跑在该帧渲染
    /// <b>之前</b>，单格揭开时 DWM 手里仍然是"信封尺寸"那张旧帧（被拉伸铺到新窗口矩形＝左上角鬼影），
    /// 鬼影照样露出来。实测（2026-09-06，pid=55284）单次回调只在挂起后 <b>8.8ms</b> 触发，不足一帧，
    /// 而用户仍清楚看到"闪"——被看到的既不是整窗消失（半帧不可感知）就只能是这帧鬼影。
    /// 第二格回调时，第一格的新帧已完成 present，此时揭开画面已正确。</summary>
    private void ScheduleWheelUncloak()
    {
        // 强制渲染帧泵（修复 C 残留）：cloak 态下 resize 之后贴图窗往往已失活（失焦提交、
        // 后台滚轮都会），Avalonia 没有失效源就不出新帧，RAF 链可被挂到实测 76.6ms（≈4.6 帧，
        // v6 采证）——远超"两帧"预算。自请一次失效把下两帧钉住，隐匿时长回到 ~33ms 量级。
        InvalidateVisual();
        RequestAnimationFrame(_ =>
        {
            if (_isClosed) return;
            RequestAnimationFrame(__ =>
            {
                if (_isClosed) return;
                SetWheelResizeCloak(false);
            });
        });
    }

    /// <summary>把托管几何写回窗口，**顺序固定：先 <c>Position</c>，后 <c>Width</c>/<c>Height</c>**。
    ///
    /// <para>Avalonia 处理尺寸 setter 时，是按它<b>内部缓存的窗口位置</b>去下发原生 resize 的；而这个
    /// 缓存在"只用原生 <c>SetWindowPos</c> 挪窗"之后是滞后的——<see cref="ApplyDragMove"/> 就刻意不写
    /// <c>Position</c>（见该方法注释）。于是<b>先写尺寸</b>会让 Avalonia 用<b>挪窗前的旧位置</b>下发一次
    /// resize：窗口在旧位置按新尺寸出现一帧，紧接着我们那句 <c>Position</c> 才把它搬回新位置。观感＝
    /// 2026-09-23 用户报的「把贴图从 A 拖到 B，在 B 点第一格缩放时，A→B 路径上（A 端）冒出一个重影」。
    /// 只在第一格出现也是因为缓存：第一格之后 <c>Position</c> 已被写过一次，缓存追平，重影就没了。</para>
    ///
    /// <para>本方法是这条顺序的<b>唯一</b>实现，7 处通路（缩略图/滚轮/提交/还原/拖边/适配屏幕）一律走它，
    /// 免得逐处记"谁先谁后"——这正是本文件反复记过的"漏一处就跑位"失效模式。</para>
    /// 调用前提：原生 <c>SetWindowPos</c> 已经把窗口放到 <c>(xPx,yPx,wPx,hPx)</c>，这里的三写只是把托管
    /// 状态对齐到同一真值（逐像素一致 ⇒ 布局 Resize 为空操作）。</summary>
    private void ApplyWindowRectManaged(int xPx, int yPx, int wPx, int hPx, double s)
    {
        Position = new PixelPoint(xPx, yPx);
        Width = wPx / s;
        Height = hPx / s;
    }

    /// <summary>把窗口矩形一次性对齐到"内容 + 两侧阴影边距"的<b>贴合矩形</b>（单次原子
    /// <c>SetWindowPos</c>），随后按新窗口原点反解各层 <c>Margin</c>——图片屏幕位置仍钉在目标矩形。
    /// 在渲染帧回调内执行，与内容同帧提交。目标矩形与当前一致时零操作（正常情况下
    /// <see cref="EnsureFittedWindowRect"/> 已经拦下，这里是"调度之后几何又被别处追平"的兜底）。
    ///
    /// <para><b>2026-09-23 阶段 2：这里不挂 cloak。</b>信封时代的做法是"每次 resize 先 cloak 整窗、
    /// 两格 RAF 后揭开"，理由是 DWM 会把旧帧拉伸贴合新窗口矩形。该前提已被 P0 采证否证
    /// （<c>docs/贴图去信封方案.md</c> §1.3：本机 6 组配置 × 20 次真 resize、~95fps 抓屏共 896 帧，
    /// STRETCH 帧数 <b>0</b>，且判据带正对照校准——拉伸一发生就量得出）。真实出现的是
    /// "上一帧原尺寸残留 ≤1 帧"＋"新露出的那条 L 形边缘当帧未画"，两者都与 cloak 无关。
    /// 而 cloak 自身的代价是实测的：整窗隐匿 8.8 / 53 / 144.8 / 226.6 / 320.1 / 586.4 / 682.2 ms，
    /// 观感＝"贴图消失一下"（正是用户报的病 2）。逐格滚轮都 cloak 一次会把这份代价从偶发变成
    /// 每格一次，是明确的退化 ⇒ 手势期一律直接 resize。cloak 现存于
    /// <see cref="CommitWheelGesture"/> / <see cref="ApplyEdgeResizeTextFrame"/>，阶段 3 随动画过渡一并删。</para>
    ///
    /// <para><b>原"拖边期信封只增不减"守卫同步删除</b>：它守的是"工作区抖动算出更小矩形 ⇒ 表面一缩
    /// ⇒ 内容被'收进表面'牵走位置"，而"收进表面＝整块居中"这条机制已在阶段 2 换成
    /// <see cref="ClampContentAxis"/> 的最小位移约束（对侧钉死的边恰好落在合法区间端点上 ⇒ 空操作），
    /// 被守的成因不存在了；而"缩小时窗口不跟手"反而是必须修的——图片多大窗口多大。</para>
    /// </summary>
    private void ApplyFittedWindowRect()
        => ApplyFittedWindowRectCore(null);

    private void ApplyFittedWindowRectCore(IReadOnlyList<System.Drawing.Rectangle>? workAreas)
    {
        double s = SafeScaling;
        double mPx = ShadowMargin * s;
        ComputeFittedWindowRectCore(s, out int xPx, out int yPx, out int wPx, out int hPx,
            out bool movedByClamp, out var boundaryShift, workAreas ?? GetStickerDisplayRects());

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var rcCur = default(ScreenCaptureTool.Platform.NativeMethods.RECT);
        bool hasCur = hwnd != IntPtr.Zero &&
                      ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out rcCur);

        bool rectUnchanged = hasCur && rcCur.Left == xPx && rcCur.Top == yPx &&
            rcCur.Width == wPx && rcCur.Height == hPx;
        // 窗口不动时内容仍可能碰到最小可见边界，不能跳过这份整体平移。
        if (rectUnchanged && boundaryShift == default) return;

        _isApplyingScale = true;
        try
        {
            if (hwnd != IntPtr.Zero && !rectUnchanged)
            {
                // SWP_NOZORDER(0x0004)|SWP_NOACTIVATE(0x0010)：单次原子移动+缩放
                // ⚠ 这一句单独掐表（`改窗` 计数）：它是同步调用，"贵的到底是改尺寸这个动作、还是
                // 随之而来的整面重绘"这条老账此前分不出来，因为只有托管整段一个数（0.07ms/帧，
                // 且按本文件自己的注释"盖不住改窗口/布局/阴影"）。重绘那半仍然量不到（在合成线程），
                // 用视口面积 × 0.6ms/MPx 当参照换算。
                long fitT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
                _fitResizes++;
                double fitMs = System.Diagnostics.Stopwatch.GetElapsedTime(fitT0).TotalMilliseconds;
                _fitSetPosMsTotal += fitMs;
                if (fitMs > _fitSetPosMsMax) _fitSetPosMsMax = fitMs;

                // 落地校验（2026-09-16）——不变量 I2「_wheelEnv* 只记真实落地值」此前在这条通路是破的：
                // SetWindowPos 会被系统静默忽略（提交路径实测 29 次里 4 次返回成功、回读仍旧矩形），
                // 而这里若按**请求值**记簿就产生分叉 Δ：下一格 HandleWheelZoomEnvelope 从「真实原点 +
                // 按簿记原点反解的 Margin」读图片屏幕位置，误差 −Δ 被锚点公式逐格 ×1.1 放大——
                // 即用户实测的"放大到一定程度后位置向右下偏移、每格幅度越来越大"。
                // 分叉即按真实值改写四元组：簿记恒等于真实；下一格据真实尺寸重算贴合矩形，逐帧自然重试直至落地。
                if (ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcSet) &&
                    (rcSet.Left != xPx || rcSet.Top != yPx ||
                     rcSet.Width != wPx || rcSet.Height != hPx))
                {
                    WheelDiag($"贴合落定被忽略：请求({xPx},{yPx}){wPx}x{hPx} " +
                              $"真实({rcSet.Left},{rcSet.Top}){rcSet.Width}x{rcSet.Height} → 簿记改按真实值");
                    // 未落地的边界平移不能先加到图片簿记，下次拟合会按真实窗口重算。
                    boundaryShift = default;
                    movedByClamp = false;
                    xPx = rcSet.Left; yPx = rcSet.Top;
                    wPx = rcSet.Width; hPx = rcSet.Height;
                }
            }
            // 与物理尺寸逐像素一致的托管值：HandleResized 写回相同值，布局 Resize 为空操作
            ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);

            // 记录已落地的视口（＝窗口内容区物理矩形）。SnapStablePx 量化与工作区钳制都会让
            // 请求值与真实值差零点几 px，簿记只认落地的值。
            _wheelEnvL = xPx + mPx;
            _wheelEnvT = yPx + mPx;
            _wheelEnvW = wPx - mPx * 2;
            _wheelEnvH = hPx - mPx * 2;
            // 最小可见边界调整的是整张贴图，图片须随窗口同移；查询阶段不得提前改这份簿记。
            _wheelImgLpx += boundaryShift.X;
            _wheelImgTpx += boundaryShift.Y;

            WheelDiag($"贴合对齐：窗口→({xPx},{yPx}){wPx}x{hPx} "
                      + $"前={(hasCur ? $"({rcCur.Left},{rcCur.Top}){rcCur.Width}x{rcCur.Height}" : "取不到")} "
                      + $"视口({_wheelEnvW:F0}x{_wheelEnvH:F0}) " +
                      $"内容({_wheelImgWpx:F0}x{_wheelImgHpx:F0})@({_wheelImgLpx:F0},{_wheelImgTpx:F0})"
                      + (movedByClamp ? "（位置被工作区钳制搬动）" : ""));
            // 振荡采证：本通路是"每帧按内容反算窗口"的那一条，正是闭环所在，重点盯它
            TrackWindowPositionForOscillation(xPx, yPx, "贴合对齐");

            // 窗口原点已变：先按**落地后的真实视口**约束内容位置（被工作区夹小时内容会被挤出视口），
            // 再按新原点反解 Margin，图片屏幕位置仍钉在目标矩形。
            // ⚠ 顺序不可颠倒：约束必须早于各层落位，否则未约束的中间态会烤进 Margin。
            //
            // 🔴 **闭环切断（2026-10-01，issue #52 跟进「靠边缘跳位 / 来回跳十几次」）**：
            //   `ComputeFittedWindowRectPx` 按内容位置 `_wheelImg*` 算窗口 → 末尾被 `ClampRectToWorkArea`
            //   夹回工作区 → 这里 `ClampContentIntoViewport()` 又按夹后的视口**改回内容位置** →
            //   下一帧再按内容位置算窗口……靠边缘时（不靠边 Clamp 是空操作、环断开）就是个正反馈环，
            //   叠加工作区读数的瞬时抖动，每次收敛到不同落点 ⇒ 跳一次或连跳十几次。
            //   切法：**位置被钳制搬动过的那一帧不回灌**。依据是两者的语义本就不同——
            //     · 尺寸被夹小 ⇒ 视口真的变小了，内容必须收回（这条保留，是正确的）；
            //     · 位置被搬走 ⇒ 内容与窗口是**整体同移**的，相对关系没变，回灌纯属把"窗口被搬"
            //       误记成"内容越出视口"，而它写进 `_wheelImg*` 后又成为下一帧算窗口的输入 ⇒ 环闭合。
            //   ⚠ 不要改成"干脆不调 ClampContentIntoViewport"：夹小那一路靠它把内容收回视口，
            //     去掉会变成"内容越出新视口且再也拖不回"（本文件既有红线）。
            if (movedByClamp)
            {
                WheelDiag($"贴合对齐：位置被工作区钳制搬动（→{xPx},{yPx}），本帧跳过内容回灌以切断振荡环");
            }
            else
            {
                ClampContentIntoViewport();
            }
            PinGestureLayers();
        }
        finally { _isApplyingScale = false; }
    }

    /// <summary>
    /// 提交滚轮手势：视觉覆写归位、恢复拉伸铺满、卸载命中穿透子类化、尺寸所有权交还布局。
    /// 幂等（未覆写时直接返回）。触发时机：快捷键、切缩略图（ToggleThumbnailMode）、窗口失活
    /// ——**按下任何键都不收口**（左键点击/双击、右键菜单、中键关闭全部在手势态进行，
    /// 指针移动同理），保证"点击→再滚"与"拖动→再滚"全程零 resize、点击本身零闪烁。
    ///
    /// <para><b>2026-09-23 阶段 2 后这里的"收口"基本不再包含 resize</b>：窗口矩形每格已由
    /// <see cref="ApplyFittedWindowRect"/> 对齐到贴合矩形，提交走的是同一条
    /// <see cref="ComputeFittedWindowRectPx"/> ⇒ 下面 <c>rectUnchanged</c> 命中即零 resize、
    /// 零 cloak、零闪烁。信封时代"松手一次性把 9 倍大的窗口缩回贴合 + cloak"整段消失，
    /// 不是被优化掉了，是结构性不存在了。</para>
    ///
    /// <para>窗口表面尺寸封顶工作区，位置允许半隐藏（<see cref="ClampRectToWorkArea"/>，与手势每格同口径）：越出视口的
    /// 内容由 <see cref="ApplyContentPan"/> 平移查看，一个像素都不丢，故夹小不再等于"内容真丢"。
    /// 夹小后内容位置按 <see cref="ClampContentAxis"/> 最小位移收回视口（不居中，保住用户
    /// 正在看的那一段）；一次看全另有右键 <see cref="FitStickerToWorkArea"/>。</para>
    ///
    /// <para>终态：装得下的贴图＝"窗口＝贴合矩形、内容填满窗口"，覆写/穿透/尺寸所有权一律归位；
    /// 装不下的（长图、放大溢出）保留覆写态以承载平移 offset，见下面 <c>keepOverrideForPan</c>。</para>
    /// </summary>
    /// <param name="trigger">触发来源（仅用于诊断落盘，标明是哪条通路发出的提交）。</param>
    private void CommitWheelGesture(string trigger)
    {
        if (!_wheelVisualOverride) return;

        // 窗口正在关闭：此时 SetWindowPos 必然失败——2026-09-17 现场报告里那条
        // `提交落定被忽略[窗口失活]：… SetWindowPos=False err=87` 就是关闭路径上发出的
        // （87=ERROR_INVALID_PARAMETER，句柄正在销毁），重发又会撞上 `_isClosed` 静默退出，
        // 在日志里留下一段"被忽略之后凭空没有下文"的盲区。直接不提交。
        if (_isClosed) return;

        // ⚠ 标注期间一律"只记账、不收口"：收口若真的改窗口尺寸就会挂上 DWM cloak（见 SetWheelResizeCloak），
        // 而 cloak 自身"整窗隐匿 8.8~682ms"正是用户看见的"闪一下"——代价在 cloak 本身，不在 resize
        // （"resize 会露出被 DWM 拉伸的旧帧鬼影"这条旧解释已被 P0 采证否证，见 docs/贴图去信封方案.md §1.3）；
        // 红线⑥也要求标注期内几何不变。
        // 退出后同样不补做——覆写态本就设计成可长期常驻（被工作区夹小时多出的区域走 HTTRANSPARENT 穿透），
        // 收口交给既有的非手势时机（窗口失活 / 其他快捷键 / 切缩略图 / 适配屏幕）。
        // 促成这条守卫的实测：滚轮缩放后按空格进标注，"提交收口[快捷键]"与"进入标注"相隔 8ms（同一次按键），
        // 观感即用户报的"缩放之后进标注会闪一下"。覆写期内所有几何读取都已走内容框而非窗口框
        // （GetFittedContentBoundsDip / GetScreenRect / BeginEdgeResize 的手势簿记分支），跨标注期存活安全。
        if (_isAnnotating)
        {
            WheelDiag($"收口推迟（标注中，来源={trigger}）：窗口已逐格贴合，推迟的只是退出覆写与最终收口，" +
                      "交给下一次非手势时机落定");
            return;
        }

        _wheelGestureActive = false;
        _wheelGestureAnchorPx = new Avalonia.Point(double.NaN, double.NaN);   // 手势结束＝锚点作废，下次滚轮重采
        _wheelGen++;
        // 补帧必须在这里断掉：提交算的是**严格贴合**矩形，若还留着"窗口按并集放大"的状态，
        // 收口会按并集算出一个偏大的窗口；而半途的视觉倍率不清零，松手后画面还自己在动。
        StopZoomEase();
        // 缩放帧采证汇总：放在这里而非上面几条早返回之后 —— 那几条（未覆写 / 正在关闭 /
        // 标注中）都**不是**手势结算，标注期那条更是明确"推迟到下一次非手势时机"，
        // 在它们那里清零会把还没结束的这段手势数据丢掉。
        FlushWheelFrameStats(trigger);
        // 提交后过期累积作废：刚收口的窗口不该再被下一帧的滞留滚轮事件重新撑开/再缩放
        _wheelPendingRatio = 1.0;
        _wheelPendingFallback = false;

        double s = SafeScaling;
        double mPx = ShadowMargin * s;
        // 阶段 2：提交与手势每格走**同一条**窗口矩形公式（ComputeFittedWindowRectPx）。
        // 正常情况下滚轮收尾时窗口已经站在贴合矩形上 ⇒ 下面 rectUnchanged 命中、
        // 零 resize、零 cloak、零闪烁——"松手整窗收缩"这个动作不是被优化掉了，是结构性不存在了。
        ComputeFittedWindowRectPx(s, out int xPx, out int yPx, out int wPx, out int hPx,
            out _, out var boundaryShift);
        double newEnvW = wPx - mPx * 2, newEnvH = hPx - mPx * 2;
        _wheelImgLpx += boundaryShift.X;
        _wheelImgTpx += boundaryShift.Y;

        var wa = WorkAreaBounds(GetStickerDisplayRects());
        bool overSurface = (wa.Width > 0 && wPx > wa.Width) || (wa.Height > 0 && hPx > wa.Height);

        // —— 窗口恒钳进工作区（2026-09-22 立，阶段 2 起手势期与落定期同口径）——
        // 旧口径是"内容多大窗口就多大"，理由是"窗口矩形就是绘制表面，夹小＝内容真丢"。
        // 该理由自平移落地后**不再成立**：越出视口的内容由 ApplyContentPan 平移查看，一个像素都不丢。
        // 而超屏窗口的代价是实测的——绘制成本随表面面积线性增长约 0.6ms/MPx，长图窗口
        // 1232x12032＝14.8MPx＝7.4 个屏幕，每帧画整张、屏幕上只看得见 2MPx（其余算完即弃），
        // 正是用户报的"长截图贴图与放大超屏后卡顿"。夹进工作区后表面恒 ≤ 一屏（约 2MPx ⇒
        // ~1.2ms/帧），且 Topmost 巨幅窗口与任务栏的 z 序争抢一并消失。
        // ⚠ 夹小之后必须把内容位置按新视口重新钳制（下面 ClampContentAxis），否则 offset 会停在
        //    旧表面算出的值上、越出新视口 ⇒ 又变成"看不见且拖不回"。
        double oldImgL = _wheelImgLpx, oldImgT = _wheelImgTpx;
        if (!UsesImageViewport && newEnvW > 0 && newEnvH > 0)
        {
            var bounds = GetContentPanBounds(new Avalonia.Rect(xPx + mPx, yPx + mPx, newEnvW, newEnvH));
            _wheelImgLpx = ClampContentAxis(oldImgL, bounds.X, bounds.Width, _wheelImgWpx);
            _wheelImgTpx = ClampContentAxis(oldImgT, bounds.Y, bounds.Height, _wheelImgHpx);
            if (_wheelImgLpx != oldImgL || _wheelImgTpx != oldImgT)
                WheelDiag($"提交夹窗后内容收回视口：({oldImgL:F0},{oldImgT:F0})→"
                          + $"({_wheelImgLpx:F0},{_wheelImgTpx:F0}) 新视口({newEnvW:F0}x{newEnvH:F0})"
                          + $"@({xPx + mPx:F0},{yPx + mPx:F0})");
        }

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var rcCur = default(ScreenCaptureTool.Platform.NativeMethods.RECT);
        bool hasCur = hwnd != IntPtr.Zero &&
                      ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out rcCur);
        bool rectUnchanged = hasCur && rcCur.Left == xPx && rcCur.Top == yPx &&
                             rcCur.Width == wPx && rcCur.Height == hPx;

        // —— 内容大于视口 ⇒ 保持视觉覆写态（2026-09-22 阶段 4）——
        // 覆写态承载的正是"内容在视口内的偏移"（各层 Margin）。窗口既然已被夹进工作区、
        // 内容仍大于它，这份偏移就是用户当前看到的那一段，**必须留着**：
        //   ① ResetWheelVisualOverride 会把 8 层 Margin 归零 ⇒ 平移位置丢失、画面跳回左上角；
        //   ② CanPanContent 以 _wheelVisualOverride 为前提 ⇒ 一旦退出覆写就再也拖不动，
        //      而窗口已不能超屏，屏幕外的内容就真的看不到了（＝功能退化）。
        // 故这种情形下"提交"只做一件事：把窗口矩形落到夹后的尺寸，视觉覆写与命中穿透全程保留。
        // 装得下的贴图（绝大多数）判据为假，照旧完整退出覆写、行为逐像素不变。
        bool keepOverrideForPan = newEnvW > 0 && newEnvH > 0 &&
                                  (_wheelImgWpx > newEnvW + 1.0 || _wheelImgHpx > newEnvH + 1.0);

        if (!keepOverrideForPan) _wheelVisualOverride = false;
        // ⚠ 命中穿透子类化**刻意不在这里卸**：唯一卸载判据是"收缩已确实落地"，
        // 见 UninstallWheelHitTransparencyIfShrunk 的注释（2026-09-17 修）。
        WheelDiag($"提交收口[{trigger}]：图片物理({_wheelImgWpx:F0}x{_wheelImgHpx:F0}) 窗口→({xPx},{yPx}){wPx}x{hPx}" +
                  (overSurface ? "（内容大于工作区，窗口已夹进一屏；越出部分拖动平移查看）" : "") +
                  (keepOverrideForPan ? "（内容大于视口，保留覆写态以承载平移偏移）" : "") +
                  (rectUnchanged ? "（矩形未变，跳过 resize）" : ""));

        if (rectUnchanged)
        {
            if (keepOverrideForPan)
            {
                // 窗口没有变，内容仍可能被边界约束修正，必须同步 Margin/命中，不能只改簿记。
                _wheelEnvL = xPx + mPx; _wheelEnvT = yPx + mPx;
                _wheelEnvW = newEnvW; _wheelEnvH = newEnvH;
                SetWheelGestureVisual((_wheelImgLpx - _wheelEnvL) / s, (_wheelImgTpx - _wheelEnvT) / s,
                                      StickerImage.Width, StickerImage.Height);
                SaveArchiveSnapshot();
                return;
            }
            // 窗口本就是贴合矩形 ⇒ 表面之外没有空白区，可以安全卸穿透
            UninstallWheelHitTransparencyIfShrunk(hwnd, wPx, hPx);
            // 矩形已一致：只归位视觉，零 resize 零 cloak 零闪烁
            ResetWheelVisualOverride();
            ReleaseWheelEnvelopeSizing();
            SaveArchiveSnapshot();
            return;
        }

        // 尺寸不变＝move-only：无 DWM 旧帧拉伸伪影，无需 cloak
        bool sizeChanged = !hasCur || rcCur.Width != wPx || rcCur.Height != hPx;

        _isApplyingScale = true;
        try
        {
            // cloak：这一格整窗隐匿、两格 RAF 后揭开。阶段 2 后提交**通常走不到这里**
            // （每格已把窗口对齐到贴合矩形 ⇒ rectUnchanged 命中），剩下的只有"标注期推迟收口"
            // "工作区上限使手势与提交不同公式"等少数情形，故 cloak 频率比信封时代更低。
            // ⚠ 它自身"贴图消失一下 8.8~586ms"的代价仍在账上，阶段 3 随动画过渡一并删除。
            // 分层呈现在位：交互窗自身已是透明层，像素由呈现窗一次 ULW 落地，cloak 无对象可遮、
            // 反而会让交互窗在隐匿期间收不到点击 ⇒ 不挂。
            bool cloak = sizeChanged && !PinLayeredActive;
            if (cloak) SetWheelResizeCloak(true);
            if (hwnd != IntPtr.Zero) ApplyCommitRect(hwnd, xPx, yPx, wPx, hPx, 0, trigger);
            if (cloak) ScheduleWheelUncloak();

            if (keepOverrideForPan)
            {
                // 内容大于视口：簿记对齐到落地后的真实视口，再按同一偏移重写各层 Margin
                // （窗口原点变了，Margin 是"内容屏幕位置 − 视口原点"，不重算会整体坐偏）。
                // 刻意不调 ResetWheelVisualOverride —— 那会把偏移清零、平移位置丢失。
                _wheelEnvL = xPx + mPx; _wheelEnvT = yPx + mPx;
                _wheelEnvW = newEnvW; _wheelEnvH = newEnvH;
                SetWheelGestureVisual((_wheelImgLpx - _wheelEnvL) / s, (_wheelImgTpx - _wheelEnvT) / s,
                                      StickerImage.Width, StickerImage.Height);
            }
            else
            {
                ResetWheelVisualOverride();
            }

            ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);
        }
        finally { _isApplyingScale = false; }

        // 尺寸所有权交还布局：必须晚于上面 Width/Height/Position 落值，否则布局会拿
        // 旧的（信封）尺寸与自己算出的贴合尺寸对撞，多出一整次带 cloak 的 resize。
        // ⚠ 保留覆写态时**不交还**：交还后布局会按 SizeToContent 把窗口拽回贴合内容的
        //    尺寸（对长图＝又变成 12032 高的超屏窗口），阶段 4 的收益当场作废。
        if (!keepOverrideForPan) ReleaseWheelEnvelopeSizing();
        SaveArchiveSnapshot();
    }

    /// <summary>提交落定的那一次原子 resize，**带落地校验**：<c>SetWindowPos</c> 之后立刻回读
    /// 窗口矩形，尺寸不符就换到派发回调上下文重发（最多 3 次）。
    /// <b>为什么必须校验</b>（<c>log/app.log</c> pid=39044，23:31–23:33 段实证）：29 次提交里
    /// 4 次的 <c>SetWindowPos</c> 被系统**整个忽略**——请求 <c>421x163</c> 回读仍是信封的
    /// <c>1920x1032</c>（另三次 <c>444x1686</c>/<c>393x2626</c>/<c>644x2317</c> 同样回读为信封矩形），
    /// 故**不是**尺寸上限被夹（请求比屏幕小得多也照样被忽略）；而同一段代码从
    /// <c>DispatcherTimer</c> 回调里发出时 11/11 落地（含 3891/4323px 的超屏高度）。
    /// 四次失败的共同点是都发生在**窗口失活**上下文（用户点去别的应用复制下一段文本），
    /// 推断是系统在处理激活/Z 序事务期间吞掉了这次几何变更。派发回调运行在当前消息处理完之后、
    /// 已脱离该事务，且优先级 Normal 高于渲染（Avalonia 的 Render 优先级更低）→ 重发既落在
    /// 实测能成功的上下文里，也仍赶在揭开 cloak 的那个渲染帧之前，不会露出错误几何的一帧。
    /// <b>落定失败＝窗口停在信封视口而视觉覆写已归位</b>＝内容按贴合尺寸排版却裁在一屏表面外，
    /// 拖动同移窗口与内容，越出的部分永不出帧（用户复测的"缩放后再平移，部分内容依然渲染缺失"）。</summary>
    private void ApplyCommitRect(IntPtr hwnd, int xPx, int yPx, int wPx, int hPx, int attempt, string trigger)
    {
        bool ok = ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
        bool landed = ok &&
                      ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcSet) &&
                      rcSet.Width == wPx && rcSet.Height == hPx;
        if (landed)
        {
            if (attempt > 0)
                WheelDiag($"提交落定重发成功[{trigger}]：第 {attempt + 1} 次尝试 {wPx}x{hPx}");
            // 收缩确实落地 ⇒ 此刻才允许卸信封态命中穿透（异步重发的那几次也走这里）
            UninstallWheelHitTransparencyIfShrunk(hwnd, wPx, hPx);
            return;
        }

        int err = Marshal.GetLastWin32Error();
        if (attempt >= 2)
        {
            WheelDiag($"提交落定失败[{trigger}]：请求({xPx},{yPx}){wPx}x{hPx} SetWindowPos={ok} err={DescribeWin32Error(err)}" +
                      $" 已重发 {attempt} 次仍未落地（窗口停在信封视口，越出部分拿不到像素）");
            // 收缩确实没落地 ⇒ 撤销"退出信封"，让信封完整保留（见 RestoreWheelEnvelopeAfterFailedCommit）。
            // 这同时保证命中穿透不会在被卸掉的同时留下吃点击的挡板。
            RestoreWheelEnvelopeAfterFailedCommit(hwnd, trigger);
            return;
        }

        WheelDiag($"提交落定被忽略[{trigger}]：请求({xPx},{yPx}){wPx}x{hPx} SetWindowPos={ok} err={DescribeWin32Error(err)}" +
                  " → 下一消息循环重发");
        int next = attempt + 1;
        Dispatcher.UIThread.Post(() =>
        {
            // ⚠ 每个提前 return 都必须留痕（2026-09-17 补）：原来静默 return，现场日志里就只剩
            // "被忽略 → 重发"，重发的结果凭空消失，判读时根本无从知道到底是放弃了还是落地了
            // （首轮回传的报告 2 正是这个形态：17:01:16 之后 18 秒一行日志都没有）。
            if (_isClosed)
            {
                WheelDiag($"提交落定重发放弃[{trigger}]：窗口已关闭");
                return;
            }
            if (_wheelVisualOverride)
            {
                WheelDiag($"提交落定重发放弃[{trigger}]：新手势已接管窗口矩形");
                return;
            }
            IntPtr h = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (h == IntPtr.Zero)
            {
                WheelDiag($"提交落定重发放弃[{trigger}]：取不到窗口句柄");
                return;
            }
            ApplyCommitRect(h, xPx, yPx, wPx, hPx, next, trigger);
        });
    }

    /// <summary>
    /// 收缩落地确认后才卸信封态命中穿透子类化——**唯一卸载判据**（2026-09-17 立）。
    ///
    /// <para><b>为什么不能"提交时先卸、再 resize"</b>（原写法，已造成用户实报故障）：
    /// 命中穿透（`WM_NCHITTEST` 对图片+阴影外返回 `HTTRANSPARENT`）是信封态**唯一**的安全阀——
    /// 信封把窗口涨到近整屏，全靠它把透明区的点击/滚轮放给下层窗口。而 <see cref="ApplyCommitRect"/>
    /// 的实测记录是「29 次提交里 4 次 `SetWindowPos` 被系统整个忽略」（共同点是窗口失活上下文，
    /// 即用户正点去别的应用）。原顺序下这 4 次的结果是：**穿透已卸 + 窗口仍停在近整屏信封**
    /// ⇒ 那块透明区从"穿透"变成"HTCLIENT"，把全盘点击与滚轮都吃掉，且**没有任何自愈路径**
    /// （用户点不到别处、也滚不动，主观感受就是"鼠标左键被拦截、整个卡死"）。</para>
    ///
    /// <para>判据取"回读到的窗口尺寸已等于目标贴合尺寸"：尺寸相符即信封透明区不存在，
    /// 与位置无关（位置不同不影响有无透明区）。与 <see cref="ApplyCommitRect"/> 的落地判据
    /// 同一口径（同样只比宽高），避免出现"它算落地、这里不算"的分歧。</para>
    /// </summary>
    private void UninstallWheelHitTransparencyIfShrunk(IntPtr hwnd, int wPx, int hPx)
    {
        if (!_hitTransparencyInstalled || hwnd == IntPtr.Zero) return;
        if (!ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc)) return;
        if (rc.Width != wPx || rc.Height != hPx) return;
        UninstallWheelHitTransparency(this);
    }

    /// <summary>
    /// 收口 resize 确实没落地时的收尾（2026-09-17 立）：**撤销"退出手势态"这件事本身**，让手势态
    /// 连同视觉一起完整保留，而不是留一个"窗口还停在旧矩形、视觉却已按新贴合尺寸归零"的半残态。
    ///
    /// <para><b>为什么必须撤销，不能将错就错</b>：走到这里意味着连派发上下文里的两次重发都没落地
    /// （<see cref="ApplyCommitRect"/> 实测成因＝窗口失活期系统吞掉几何变更）。而
    /// <see cref="CommitWheelGesture"/> 在它返回后仍会照常执行 <c>ResetWheelVisualOverride()</c>
    /// 与 <c>Width/Height/Position</c> 回写，于是留下的是：窗口＝旧矩形、各层 <c>Margin</c> 已归零、
    /// <c>_wheelHitRect</c> 却仍是手势最后一帧的偏移值 ⇒
    /// <b>看得见的图片点不到、点得到的地方是空的</b>，用户连自救都做不到。甲/乙两案要选的正是
    /// 这件事，用户选乙＝宁可多留一格手势态，也绝不留错位。</para>
    ///
    /// <para><b>撤销之后是自愈的</b>：<c>_wheelVisualOverride</c> / <c>_wheelGestureActive</c>
    /// 复位为 true，下一次"窗口失活 / 快捷键 / 切缩略图"会再走一遍
    /// <see cref="CommitWheelGesture"/>——那时已脱离激活事务，<c>SetWindowPos</c> 按实测
    /// 11/11 落地，穿透随之正常卸掉。而手势期的 <c>HTTRANSPARENT</c> 全程在位，表面外区域的
    /// 点击与滚轮照旧放给下层窗口，不存在"挡板吃点击"。</para>
    ///
    /// <para><b>为什么把几何写回而不是先回读判断</b>：<c>Width/Height/Position</c> 的回写由
    /// Avalonia 布局在 <c>Render</c> 优先级落地，晚于本方法所在的 <c>Normal</c> 优先级派发，
    /// 故此刻回读到的窗口尺寸既可能是旧矩形、也可能马上要变成贴合尺寸——两种时序都真实存在。
    /// 直接再写一遍手势期簿记的矩形则两种时序都收敛到同一终态（后写者胜），且若这次写仍被系统
    /// 吞掉，窗口停在旧矩形、视觉也是旧矩形，依旧自洽。</para>
    /// </summary>
    /// <param name="hwnd">窗口句柄（由调用方透传，避免重取时窗口已换）。</param>
    /// <param name="trigger">原提交的触发来源，仅用于诊断落盘。</param>
    private void RestoreWheelEnvelopeAfterFailedCommit(IntPtr hwnd, string trigger)
    {
        if (_isClosed || _wheelVisualOverride) return;   // 已关窗，或新手势已接管窗口矩形
        if (hwnd == IntPtr.Zero) return;
        if (_wheelEnvW <= 0 || _wheelEnvH <= 0)
        {
            // 信封簿记不可用（不该发生）⇒ 无从撤销，只能如实记录，交给用户手动关掉重贴
            WheelDiag($"信封保留[{trigger}]：收缩失败且信封簿记为空（{_wheelEnvW:F0}x{_wheelEnvH:F0}），无法撤销退出信封");
            return;
        }

        double s = SafeScaling;
        double mPx = ShadowMargin * s;
        int wPx = (int)Math.Round(_wheelEnvW + mPx * 2);
        int hPx = (int)Math.Round(_wheelEnvH + mPx * 2);
        int xPx = (int)Math.Round(_wheelEnvL - mPx);
        int yPx = (int)Math.Round(_wheelEnvT - mPx);

        // ① 状态复位：重新进入信封态（提交时被置 false 的那两个标志）
        _wheelVisualOverride = true;
        _wheelGestureActive = true;
        _wheelGen++;
        _wheelPendingRatio = 1.0;
        _wheelPendingFallback = false;

        // ② 文本贴图重新接管尺寸所有权：调用方的 ReleaseWheelEnvelopeSizing 已把
        //    SizeToContent 还原，不重新置 Manual 的话布局会按内容尺寸把信封窗口缩下去，
        //    视觉覆写与窗口矩形又会分叉（与 ActivateWheelEnvelope 同口径）。
        if (TextViewbox != null && TextViewbox.IsVisible && SizeToContent != SizeToContent.Manual)
        {
            _wheelEnvelopePrevSizeToContent = SizeToContent;
            SizeToContent = SizeToContent.Manual;
            _wheelEnvelopeManualSizing = true;
        }

        // ③ 视觉覆写重新挂上：与 ApplyFittedWindowRect 尾部同一套公式（约束必须早于
        //    SetWheelGestureVisual），顺带把 _wheelHitRect 刷回手势期的正确值——它才是
        //    "看得见的图片点得到"的保证。
        ClampContentIntoViewport();
        SetWheelGestureVisual(
            (_wheelImgLpx - _wheelEnvL) / s,
            (_wheelImgTpx - _wheelEnvT) / s,
            StickerImage.Width, StickerImage.Height);

        // ④ 几何写回手势期簿记的矩形（后写者胜，压掉调用方那次贴合尺寸的回写）
        _isApplyingScale = true;
        try
        {
            ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);
        }
        finally { _isApplyingScale = false; }

        // ⑤ 命中穿透重新确认在位（幂等）。走到这里它本就没被卸（唯一卸载判据是"收缩已落地"），
        //    这一步是为了兜住"调用方的 Width/Height 回写其实晚一步落地、且恰好触发了卸载"的时序。
        InstallWheelHitTransparency(this);

        WheelDiag($"手势态保留[{trigger}]：收口 resize 未落地 → 撤销退出覆写（窗口回到 {wPx}x{hPx}，" +
                  "视觉覆写重新挂上）；命中穿透全程在位，点击/滚轮照旧放行；下一次非手势时机重试收口");
    }

    /// <summary>把整幅内容装进可用工作区所需的缩放系数（原始值，未做任何上下限钳制——
    /// 钳与不钳由调用方决定，见 <see cref="FitStickerToWorkArea"/>）。
    /// 工作区取 <c>MonitorHelper.GetWorkAreaUnion()</c>——与 <see cref="ClampRectToWorkArea"/>
    /// 同一个源，两处口径必须一致，否则"这里算出来装得下"和"那里判定装不下"会互相打架；
    /// 扣的两侧阴影边距也与贴合窗口公式（窗口＝内容 + 2×ShadowMargin）同口径。</summary>
    private bool TryComputeFitScaleToWorkArea(out double fit)
    {
        fit = 1.0;
        if (_sourcePixelWidth <= 0 || _sourcePixelHeight <= 0) return false;
        double mPx = ShadowMargin * SafeScaling;
        var wa = ScreenCaptureTool.Platform.MonitorHelper.GetWorkAreaUnion();
        double limitW = wa.Width - mPx * 2;
        double limitH = wa.Height - mPx * 2;
        if (limitW <= 0 || limitH <= 0) return false;
        fit = Math.Min(limitW / _sourcePixelWidth, limitH / _sourcePixelHeight);
        return true;
    }

    /// <summary>
    /// 右键「适配屏幕」：把超出屏幕的贴图一次缩到整幅落进可用工作区。
    /// 存在的原因：内容物理尺寸 &gt; 工作区时，滚轮缩放的 20% 下限足以让贴图仍大过屏幕，
    /// 落定后窗口被夹进一屏，于是有一部分落在视口外——那些像素是画好的、靠拖动平移可查看，
    /// 但一次看不全。
    /// 实现刻意不新写一套几何：走滚轮同一条通路——激活覆写态（幂等，兼管 SizeToContent
    /// 记账）→ <see cref="ApplyScale"/> 只改内容显式宽高（窗口不动）→ 以内容当前屏幕中心为锚
    /// 重钉各层 Margin 与命中矩形 → <see cref="CommitWheelGesture"/> 一次原子 SetWindowPos
    /// 同时完成"缩放＋归位＋卸载命中穿透＋交还尺寸所有权"。
    /// 目标尺寸**只封顶不封底**：用户拍板"整幅优先，必要时突破下限"，故此处不受滚轮那两条
    /// 边界里的 0.2 可读性下限约束（超长贴图实测 fit≈0.195，被 0.2 夹住就等于本动作失效）；
    /// 仅保留 MaxScale 上限与一个防退化到零尺寸的极小值。滚轮/拖边的 20% 下限不变。
    /// </summary>
    private void FitStickerToWorkArea()
    {
        if (_isThumbnailMode) return;                    // 缩略图自有一套视口，不在此掺和
        if (!TryComputeFitScaleToWorkArea(out double fit)) return;
        FinalizeAnnotationForGeometryChange("适配屏幕");
        if (_isResizing) EndEdgeResize();
        // 缺陷 B：目标尺寸按 _sourcePixel 反解，错配态先收口再算 fit（图片贴图零开销）
        SyncTextSurfaceBeforeGesture();

        double s = SafeScaling;
        double mPx = ShadowMargin * s;
        double target = Math.Max(0.01, Math.Min(fit, GetMaxScale()));

        // 改写内容尺寸前先留一份旧尺寸（Repin 要按它算当前中心）与当前窗口矩形
        double prevW = StickerImage.Width, prevH = StickerImage.Height;
        double wl, wt, contW, contH;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            wl = rc.Left; wt = rc.Top;
            contW = rc.Width - mPx * 2;
            contH = rc.Height - mPx * 2;
        }
        else
        {
            wl = Position.X; wt = Position.Y;
            contW = Width * s - mPx * 2;
            contH = Height * s - mPx * 2;
        }

        ActivateWheelEnvelope(wl + mPx, wt + mPx, contW, contH);
        _scale = target;
        ApplyScale(skipWindowResize: true);
        RepinWheelVisualForNewContentSize(prevW, prevH, StickerImage.Width, StickerImage.Height,
                                          resizeWindow: false);
        WheelDiag($"适配屏幕 scale={_scale:F3}（fit={fit:F3}）内容物理({_wheelImgWpx:F0}x{_wheelImgHpx:F0}) " +
                  $"源图={_sourcePixelWidth}x{_sourcePixelHeight}");
        CommitWheelGesture("适配屏幕");
    }

    /// <summary>视觉归位：Margin 清零 + 阴影/棋盘恢复拉伸铺满（窗口=图片+边距，语义
    /// 一致；原点取整的 ≤0.5px 误差由归零吸收，与正常态一致）。</summary>
    private void ResetWheelVisualOverride()
    {
        // 退出覆写态＝各层宽高交回 ApplyScale 按 _scale 落，冻结栅格与它的补偿倍率就此失效：
        // 不在这里归位，RootGrid 上会留着一个没人再维护的放大倍率（观感＝贴图整张比窗口大一号
        // 且再也缩不回去，因为此后没有任何通路会重算它）。
        _gestureRasterScale = _scale;
        _gestureV = 1.0;
        if (_zoomFx != null) _zoomFx.ScaleX = _zoomFx.ScaleY = 1.0;
        _zoomEasing = false;
        _zoomV = _zoomVTarget = 1.0;
        StickerImage.Margin = new Thickness(0);
        if (StickerCanvas != null) StickerCanvas.Margin = new Thickness(0);
        // 标注层必须与图片同步归零：信封交回贴合窗口后，图层若仍坐在上一轮手势的 Margin 上，
        // 再次进入标注时整批笔迹会相对图片平移（观感＝"缩小贴图后再按空格，笔迹自己跑位了"）。
        if (StickerAnnotationLayer != null) StickerAnnotationLayer.Margin = new Thickness(0);
        // 文本宿主层与标注层同一条 Margin 通路，跟着一起归零（尺寸由紧随的 ApplyScale 重设）
        SyncTextHostGeometry();
        // 文本层同样必须归零：信封态下它是被 Margin 钉在信封内某处的，不归零会整体坐偏、
        // 越出贴合窗口的部分被裁掉（与图片同一个失效模式）。
        if (TextViewbox != null) TextViewbox.Margin = new Thickness(0);
        // 底色层与遮罩层同步归位；尺寸交回 ApplyScale 按当前 _scale 重设
        if (ImageBackdrop != null) ImageBackdrop.Margin = new Thickness(0);
        if (TranslatingOverlay != null) TranslatingOverlay.Margin = new Thickness(0);
        if (ShadowBorder != null)
        {
            ShadowBorder.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            ShadowBorder.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            ShadowBorder.Margin = new Thickness(0);
            ShadowBorder.Width = double.NaN;
            ShadowBorder.Height = double.NaN;
        }
        if (CheckerboardBg != null)
        {
            // 归位保持 Left/Top —— 不可设回 Stretch，否则信封态下棋盘格会铺满信封区。
            // 尺寸交回 ApplyScale 按当前 _scale 重设。
            CheckerboardBg.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            CheckerboardBg.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            CheckerboardBg.Margin = new Thickness(0);
            // 不置 NaN：Left/Top 下 NaN 会让棋盘格塌成 0 尺寸。
            // 尺寸由紧随其后的 ApplyScale 按当前 _scale 显式重设。
        }
        // 贴合态：命中矩形＝整窗（内容+阴影铺满窗口、无透明区）。显式落定，免得穿透子类化
        // 仍在位时拿着手势期的陈旧 _hitRectDip 误判边缘点击。
        {
            double cw = double.IsNaN(StickerImage.Width) ? 0 : StickerImage.Width;
            double ch = double.IsNaN(StickerImage.Height) ? 0 : StickerImage.Height;
            _hitRectDip = new Avalonia.Rect(0, 0, cw + ShadowMargin * 2, ch + ShadowMargin * 2);
            UpdateHitRegion();
        }
    }

    // =====================================================================
    // 拖边缩放（四周 20DIP 隐形热区 → 真正可拖）
    // =====================================================================
    // 语义与滚轮缩放完全一致：唯一区别是 _scale 的来源从「×1.1」换成「鼠标位置」。
    // 因此**复用滚轮那条通路**，不新造第三种几何模型：
    //   图片 / 文本 / Markdown（含「调整文本贴图」重排）：每帧写内容几何 + 把窗口对齐到贴合矩形
    //         （ActivateWheelEnvelope + SetWheelGestureVisual + EnsureFittedWindowRect），
    //         与滚轮共用同一套簿记、同一条 ComputeFittedWindowRectPx 公式——2026-09-23 阶段 2
    //         起这条通路也不再"手势期窗口不动"，而是每格跟着内容 resize（图片多大窗口多大）；
    //         重排与非重排的差别只在"尺寸怎么算"（改版宽后重测活体排版 vs 统一比例），
    //         落地完全共用 ApplyEnvelopeResizeFrame；
    //   仅 Markdown 两帧高度捕获期：每帧一次原子 SetWindowPos(...,0x0014) + DWM cloak——
    //         那两帧窗口高度归布局所有，覆写态无从接管。
    // 锚点：冻结**对侧内容边**（拖 West 钉右缘、拖 North 钉下缘…），被拖边 = 鼠标 − 抓取
    //   偏移，故起拖那一帧零跳变；顶到 0.2 / MaxScale 边界后 _scale 被钳住而对侧边仍钉死，
    //   反向拖动立即跟手（每帧都从起手常量重算，无累加、无粘滞）。
    // 坐标一律用 GetCursorPos 的**物理屏幕像素**：这条通路每帧会挪窗口，窗口坐标系自身在动，
    //   用 e.GetPosition(this) 的 DIP 增量必然漂移；且窗口被工作区夹小时 Bounds 口径也不等于
    //   内容口径。

    private bool _resizeRafScheduled;
    private WindowEdge _resizeEdge;
    /// <summary>拖边手势已捕获指针：松手（含 Esc 中断后的那一次 button-up）时据此释放。</summary>
    private bool _resizeCaptured;
    /// <summary>捕获该指针的句柄：EndEdgeResize 在中断路径（键盘/失活，拿不到事件参数）下也要能释放捕获。</summary>
    private Avalonia.Input.IPointer? _resizePointer;
    /// <summary>true = 内容是位图图片（决定尺寸提示口径与是否可重排）。</summary>
    private bool _resizeIsImage;
    /// <summary>true = 本次手势走 Margin 覆写通路（图片、文本、Markdown 全都走，含重排模式），
    /// 与 <see cref="ApplyEdgeResizeTextFrame"/> 那条逐格 resize 兜底相对。
    /// 三者的差别只剩"尺寸怎么算"，而内容层由 <see cref="SetWheelGestureVisual"/> 一并定位，
    /// 故共用同一套手势簿记；仅 Markdown 两帧高度捕获期留在逐格 resize。
    /// ⚠ 名字里的 Envelope 是信封时代留下的（那时这条通路"手势期窗口不动"），阶段 4 改名。</summary>
    private bool _resizeEnvelope;
    /// <summary>true = 本次手势按「调整文本贴图」重排（只改版宽、<c>_scale</c> 不动）。</summary>
    private bool _resizeReflows;
    /// <summary>重排基线（**DIP**）：起手实测的自然排版宽，与「自然宽 − 当前 MaxWidth」的余量
    /// （纯文本＝<c>StickerTextBlock.Margin</c> 12×2，Markdown＝其自身内边距）。版宽约束
    /// <c>MaxWidth</c> 的单位是 DIP，而 <c>_sourcePixelWidth</c> 是物理像素，早先直接把像素值
    /// 写进 MaxWidth，1.5x 屏上每拖一帧版宽就多长 50%，Viewbox 的 Uniform 缩放随之被宽轴卡住、
    /// 内容高度追不上显式高度 → 窗口下半截变透明（"背景消失"）。余量起手势实测，不写死魔数。</summary>
    private double _resizeStartNatW, _resizeLayoutPadW;
    /// <summary>起手内容矩形（物理屏幕 px，= 窗口矩形内缩一圈阴影边距）。</summary>
    private double _resizeStartL, _resizeStartT, _resizeStartW, _resizeStartH;
    /// <summary>冻结的对侧内容边（物理屏幕 px）：拖 West 存右缘、拖 North 存下缘。</summary>
    private double _resizeAnchorX, _resizeAnchorY;
    /// <summary>抓取偏移（物理 px）：被拖边 = 鼠标 − 偏移。</summary>
    private double _resizeGrabX, _resizeGrabY;
    private double _resizeScale0;

    // 拖边帧计数（v8 采证：回答"光标动了 60 次、卡片只涨 3px"到底卡在事件投递、合帧还是重排耗时）
    private int _edgeEvtMoved, _edgeRafQueued, _edgeRafScheduled, _edgeFrames, _edgeReflowCalls;
    private int _edgeReflowBailSize, _edgeReflowBailNat, _edgeReflowBailLimit;
    private double _edgeReflowMs, _edgeReflowMsMax, _edgeMaxDelta;
    private double _reflowAppliedW = double.NaN;   // 上一次真正落地的目标显示宽（节流基准）

    /// <summary>拖边重排的最小落地步长（物理 px）：小于它就跳过本帧的重排。一次 180px 的拖动
    /// 在 60Hz 下会产生上百帧，逐帧重排会把 UI 线程顶出实测 500ms 的无响应（v7），卡片明显
    /// 落后光标。松手时由 <see cref="EndEdgeResize"/> 补最后一帧，终态仍精确跟到光标。</summary>
    private const double ReflowMinStepPx = 12.0;

    /// <summary>拖边命中的边是否含水平/垂直分量。</summary>
    private static bool EdgeDragsX(WindowEdge edge) =>
        edge is WindowEdge.West or WindowEdge.East or WindowEdge.NorthWest
            or WindowEdge.NorthEast or WindowEdge.SouthWest or WindowEdge.SouthEast;

    private static bool EdgeDragsY(WindowEdge edge) =>
        edge is WindowEdge.North or WindowEdge.South or WindowEdge.NorthWest
            or WindowEdge.NorthEast or WindowEdge.SouthWest or WindowEdge.SouthEast;

    private static bool EdgeDragsWest(WindowEdge edge) =>
        edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;

    private static bool EdgeDragsNorth(WindowEdge edge) =>
        edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;

    /// <summary>当前排版是否为 Markdown（其版宽约束挂在 StickerMarkdownViewer 上）。</summary>
    private bool IsMarkdownLayout => StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible;

    /// <summary>可重排的文本贴图：inline 文本 / Markdown 两类。颜色值卡片按内容自适应宽度、
    /// 没有初始版宽约束，重排对它无意义；图片贴图走信封通路，也不在此列。</summary>
    private bool IsReflowableTextSticker =>
        (StickerColorSwatch == null || !StickerColorSwatch.IsVisible)
        && ((StickerTextBlock != null && StickerTextBlock.IsVisible) || IsMarkdownLayout);

    /// <summary>
    /// 开始拖边缩放。起手内容矩形由 <see cref="GetFittedClientRectDip"/> 同一套簿记给出：
    /// 覆写态直接取手势记录的屏幕物理矩形，**不要求调用方先把窗口缩回贴合**（按下即改窗口
    /// ＝点击闪烁，且那一帧 Bounds 还没更新，边缘照样判不出来）。
    /// </summary>
    private void BeginEdgeResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        // 上一手势没收口就再次起手（v7 实测：PointerReleased 丢过一次，_isResizing 悬空近
        // 5 秒，期间滚轮/拖动全被 if (_isResizing) EndEdgeResize(); 那排守卫吃掉，且下面
        // ActivateWheelEnvelope 那一步会因 _wheelGestureActive 仍为真直接 return，
        // 视口簿记不再与本手势对齐）。先收口再起手：自洽、不丢状态、也不必拒绝本次拖边。
        if (_isResizing)
        {
            WheelDiag($"拖边起手前发现上一手势未收口（edge={_resizeEdge}）→ 先收口");
            EndEdgeResize();
        }

        // 拖边不走补帧（每一帧都由光标直接决定，没有"跳"可补）：起手先把滚轮那段的补帧断掉，
        // 否则半途的视觉倍率会让拖边按"终态几何"起算，笔迹与光标差一截。
        StopZoomEase();

        // 拖边改版先定稿：烘焙必须早于下方"图片还是文本层"的可见性判定与重排基线实测，
        // 否则手势会按退出前的图层几何起算，笔迹浮在旧尺寸上。
        FinalizeAnnotationForGeometryChange("拖边缩放");

        // 缺陷 B：拖边同样从真值起算——统一缩放通路不经重排守卫，捕获未完成/表面错配时
        // 首帧的贴合矩形就会按估测尺寸现算、把窗口重摆一次，起手先收口一次
        // （幂等，图片贴图零开销）。
        SyncTextSurfaceBeforeGesture();

        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // 窗口内容区（窗口矩形内缩一圈阴影）：视口簿记 _wheelEnv* 的原点始终以它为准
        double eL, eT, eW, eH;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            eL = rc.Left + mPx; eT = rc.Top + mPx;
            eW = rc.Right - rc.Left - mPx * 2; eH = rc.Bottom - rc.Top - mPx * 2;
        }
        else
        {
            eL = Position.X + mPx; eT = Position.Y + mPx;
            eW = Bounds.Width * s - mPx * 2; eH = Bounds.Height * s - mPx * 2;
        }
        // 内容实际矩形：静止贴合态与窗口内容区重合；
        // 覆写态（含内容超屏、窗口被工作区夹小）内容位置归手势簿记所有。
        double cL = eL, cT = eT, cW = eW, cH = eH;
        if (_wheelVisualOverride && _wheelImgWpx > 0 && _wheelImgHpx > 0)
        {
            cL = _wheelImgLpx; cT = _wheelImgTpx;
            cW = _wheelImgWpx; cH = _wheelImgHpx;
        }
        if (cW <= 0 || cH <= 0) return;

        bool west = EdgeDragsWest(edge);
        bool north = EdgeDragsNorth(edge);

        _isResizing = true;
        _resizeEdge = edge;
        _resizeIsImage = StickerImage != null && StickerImage.IsVisible && _bitmap != null;
        // 「调整文本贴图」：开关开启 + 文本类贴图 + 被拖边带水平分量 → 本手势改为重排
        // （只改版宽、_scale 不动）。纯 North/South 无水平分量，仍走统一缩放；开关关闭时
        // 文本贴图与图片贴图行为完全一致。Markdown 初始高度尚未捕获完成时禁用——那两帧
        // 延迟捕获会与本手势争夺 _sourcePixelWidth/Height。
        _resizeReflows = !_resizeIsImage && EdgeDragsX(edge) && IsReflowableTextSticker
                         && !_needsMarkdownHeightCapture
                         && AppSettings.Load().StickerResizeReflowsText;
        if (_resizeReflows)
        {
            // 起手势实测一次"自然排版宽 − 当前版宽约束"的余量，作为本手势的换算基准：
            // 每帧 MaxWidth = 目标自然宽 − 余量，起手帧恒等于当前值 → 零跳变，且不必猜
            // -32 / -24 这类魔数（它们只在 DPI=100% 时恰好等于余量）。
            double curMaxW = IsMarkdownLayout ? (StickerMarkdownViewer?.MaxWidth ?? double.NaN)
                                              : (StickerTextBlock?.MaxWidth ?? double.NaN);
            if (TextStickerContainer != null && curMaxW >= 1)
            {
                TextStickerContainer.InvalidateMeasure();
                TextStickerContainer.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
                var nat0 = TextStickerContainer.DesiredSize;
                if (nat0.Width >= 1 && nat0.Height >= 1)
                {
                    _resizeStartNatW = nat0.Width;
                    _resizeLayoutPadW = Math.Max(0.0, nat0.Width - curMaxW);
                }
                else _resizeReflows = false;
            }
            else _resizeReflows = false;   // 无初始版宽约束（NaN/无限/过小）→ 退回统一缩放
        }
        // Margin 覆写通路适用面：图片 + 文本/Markdown 全部走它。重排与否只决定"尺寸怎么算"
        // （统一比例 vs 改版宽后重测排版），不决定"几何由谁写"——两者都由
        // <see cref="SetWheelGestureVisual"/> 逐格写内容几何、窗口逐格对齐到贴合矩形。
        // 唯一留在逐格 resize 的是 Markdown 两帧高度捕获期：那两帧窗口高度归布局所有。
        _resizeEnvelope = _resizeIsImage
                          || (TextViewbox != null && TextViewbox.IsVisible
                              && !_needsMarkdownHeightCapture);
        _resizeStartL = cL; _resizeStartT = cT;
        _resizeStartW = cW; _resizeStartH = cH;
        // 对侧内容边：拖哪一侧就钉死另一侧，未拖的轴钉起始边
        _resizeAnchorX = west ? cL + cW : cL;
        _resizeAnchorY = north ? cT + cH : cT;
        _resizeScale0 = _scale;

        if (ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var pt))
        {
            _resizeGrabX = pt.X - (EdgeDragsX(edge) ? (west ? cL : cL + cW) : cL);
            _resizeGrabY = pt.Y - (EdgeDragsY(edge) ? (north ? cT : cT + cH) : cT);
        }
        else
        {
            _resizeGrabX = 0;
            _resizeGrabY = 0;
        }

        // v8 采证计数器复位（每手势一份，见字段声明处）
        _edgeEvtMoved = _edgeRafQueued = _edgeRafScheduled = _edgeFrames = _edgeReflowCalls = 0;
        _edgeReflowBailSize = _edgeReflowBailNat = _edgeReflowBailLimit = 0;
        _edgeReflowMs = _edgeReflowMsMax = _edgeMaxDelta = 0;
        _reflowAppliedW = double.NaN;

        // 激活滚轮那套簿记（视口簿记初始＝当前窗口内容区；已在覆写态时幂等）
        if (_resizeEnvelope) ActivateWheelEnvelope(eL, eT, eW, eH);

        // 起拖即摆好提示层并写出当前尺寸（提示层与内容层同坐标系，覆写态下须按内容偏移摆放），
        // 否则要等第一次真正改变尺寸的那一帧才有文字，按住不动会看到空白
        UpdateEdgeSizeIndicator((cL - eL) / s, (cT - eT) / s, cW / s, cH / s, FormatEdgeSizeText());
        e.Pointer.Capture(this);
        _resizePointer = e.Pointer;
        _resizeCaptured = true;
        e.Handled = true;

        WheelDiag($"拖边开始：edge={edge} 尺寸算法={(_resizeReflows ? "文本重排" : _resizeIsImage ? "图片" : "统一缩放")} " +
                  $"落地={(_resizeEnvelope ? "覆写通路" : "逐格resize")} DPI={s:F2} " +
                  $"内容({cL:F0},{cT:F0}){cW:F0}x{cH:F0} scale={_scale:F3}");
    }

    /// <summary>调度渲染帧回调：PointerMoved 只标脏，缩放一律在帧内应用
    /// （与缩略图拖角、滚轮信封同一套合帧机制，避免消息泵与渲染循环不同步导致晃动）。</summary>
    private void ScheduleEdgeResizeRaf()
    {
        _edgeRafQueued++;   // v8 采证：请求排帧次数（含被合帧吞掉的）
        if (_resizeRafScheduled) return;
        _edgeRafScheduled++;   // v8 采证：真正挂上 RAF 的次数
        _resizeRafScheduled = true;
        RequestAnimationFrame(_ =>
        {
            _resizeRafScheduled = false;
            if (_isResizing) ApplyEdgeResizeFrame();
        });
    }

    /// <summary>拖边中（每渲染帧）：由鼠标位置推出目标内容尺寸 → 折算成 _scale →
    /// 走该类型既有的滚轮通路落地。<b>不靠 ApplyScale 落地窗口几何</b>，只用它的
    /// skipWindowResize 入口刷新内容尺寸，尺寸与位置由本手势独家提交。</summary>
    private void ApplyEdgeResizeFrame()
    {
        if (!ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var pt)) return;

        WindowEdge edge = _resizeEdge;
        bool dragX = EdgeDragsX(edge);
        bool dragY = EdgeDragsY(edge);
        bool west = EdgeDragsWest(edge);
        bool north = EdgeDragsNorth(edge);

        // 被拖边跟随鼠标（减去起手抓取偏移 → 起拖帧零跳变），据此得到目标内容尺寸
        double targetW = _resizeStartW, targetH = _resizeStartH;
        if (dragX)
        {
            double draggedX = pt.X - _resizeGrabX;
            targetW = Math.Max(1.0, west ? _resizeAnchorX - draggedX : draggedX - _resizeAnchorX);
        }
        if (dragY)
        {
            double draggedY = pt.Y - _resizeGrabY;
            targetH = Math.Max(1.0, north ? _resizeAnchorY - draggedY : draggedY - _resizeAnchorY);
        }

        // v8 采证：本帧光标要求的目标增量。用它区分"卡片不跟手"卡在哪一环——若该值跟得上
        // 光标位移而源图没变，责任在重排通路；若它本身就停在起手值，责任在事件/合帧。
        _edgeFrames++;
        _edgeMaxDelta = Math.Max(_edgeMaxDelta,
            Math.Abs(targetW - _resizeStartW) + Math.Abs(targetH - _resizeStartH));

        // 重排模式（「调整文本贴图」开启 + 被拖边带水平分量）：_scale 一行不碰，
        // 只改版宽、高度由排版自然决定 → 字号不变、行数变。
        if (_resizeReflows)
        {
            // 重排每帧要量一次整篇排版，成本随面积超线性增长：v7 实测一次约 180px 的拖动把
            // UI 线程顶出 500ms 无响应，卡片明显落后光标（用户侧＝"拖不动/不跟手"）。按目标宽
            // 增量节流，并在松手时补最后一帧（见 EndEdgeResize），终态仍精确跟到光标。
            if (!double.IsNaN(_reflowAppliedW)
                && Math.Abs(targetW - _reflowAppliedW) < ReflowMinStepPx) return;
            if (!ReflowTextStickerTo(targetW)) return;
            _reflowAppliedW = targetW;
            _isApplyingScale = true;
            try
            {
                // 尺寸算法不同，落地通路完全相同：窗口矩形不动，只把新的内容尺寸与
                // Margin 交给 SetWheelGestureVisual（逐格 SetWindowPos 才是闪烁的来源）。
                ApplyScale(skipWindowResize: true);
                ApplyEnvelopeResizeFrame(west, north, FormatEdgeSizeText());
            }
            finally
            {
                _isApplyingScale = false;
            }
            return;
        }

        // 统一比例：两轴都拖时按对角线最小二乘投影（比取 max 更跟手且单调），单轴直接比值
        double k;
        if (dragX && dragY)
        {
            double sw = _resizeStartW, sh = _resizeStartH;
            k = (targetW * sw + targetH * sh) / (sw * sw + sh * sh);
        }
        else if (dragX) k = targetW / _resizeStartW;
        else k = targetH / _resizeStartH;

        double newScale = Math.Clamp(_resizeScale0 * k, 0.2, GetMaxScale());
        if (newScale == _scale) return;   // 已在缩放上/下界：尺寸不变，边缘不越界移动
        _scale = newScale;

        // 提示文案：重排模式报版宽，其余报「显示物理像素 + 百分比」（与 _scale 钳位口径一致）
        string text = FormatEdgeSizeText();

        _isApplyingScale = true;
        try
        {
            ApplyScale(skipWindowResize: true);

            if (_resizeEnvelope)
            {
                ApplyEnvelopeResizeFrame(west, north, text);
            }
            else
            {
                ApplyEdgeResizeTextFrame(text);
            }
        }
        finally
        {
            _isApplyingScale = false;
        }
    }

    /// <summary>覆写通路的每帧落地（统一缩放与文本重排共用，两者只在"尺寸怎么算"上不同）：
    /// 内容显式尺寸已由 <see cref="ApplyScale"/> 写好，这里按"对侧边逐像素钉死"反算内容的
    /// 屏幕矩形（含 ApplyScale 取整），换算成视口内 Margin 后同步各层与命中矩形，再让
    /// <see cref="EnsureFittedWindowRect"/> 把窗口对齐到新尺寸。
    /// ⚠ 这里**不再**把内容"收进当前表面"（阶段 2 删掉了 <c>ConstrainImagePositionToSurface</c>）：
    /// 窗口一移，视口就是新的那个，钳制由 <see cref="ApplyFittedWindowRect"/> 在落地后按
    /// <see cref="ClampContentAxis"/> 统一做——按旧视口钳会把对侧钉死的边拽离原位。
    /// 文本贴图同样读 <c>StickerImage.Width/Height</c>——ApplyScale 对两种内容类型都无条件
    /// 写这两个属性（与 TextViewbox 同值），图片层自身不可见故无副作用。</summary>
    private void ApplyEnvelopeResizeFrame(bool west, bool north, string text)
    {
        double s = SafeScaling;
        double imgWpx = StickerImage.Width * s, imgHpx = StickerImage.Height * s;
        _wheelImgWpx = imgWpx; _wheelImgHpx = imgHpx;
        _wheelImgLpx = west ? _resizeAnchorX - imgWpx : _resizeStartL;
        _wheelImgTpx = north ? _resizeAnchorY - imgHpx : _resizeStartT;
        double mx = (_wheelImgLpx - _wheelEnvL) / s;
        double my = (_wheelImgTpx - _wheelEnvT) / s;
        SetWheelGestureVisual(mx, my, StickerImage.Width, StickerImage.Height);
        EnsureFittedWindowRect();
        UpdateEdgeSizeIndicator(mx, my, StickerImage.Width, StickerImage.Height, text);
    }

    /// <summary>逐格 resize 兜底通路（仅 Markdown 两帧高度捕获未完成时）：与
    /// <see cref="OnRootPointerWheelChanged"/> 的文本兜底分支同构——同一套尺寸换算、同一次
    /// 原子 SetWindowPos（尺寸 + 位置一起提交），只把位移依据从「指针锚点」换成
    /// 「钉死的对侧边」。图片与已完成捕获的文本/Markdown（含重排）都不走这里，走
    /// <see cref="ApplyEnvelopeResizeFrame"/>。必须在 <see cref="_isApplyingScale"/> 置位期间调用。</summary>
    private void ApplyEdgeResizeTextFrame(string text)
    {
        double s = SafeScaling;
        double mPx = ShadowMargin * s;
        bool west = EdgeDragsWest(_resizeEdge);
        bool north = EdgeDragsNorth(_resizeEdge);

        double width = Math.Max(20.0, Math.Round(_sourcePixelWidth * _scale) / s);
        double height = Math.Max(20.0, Math.Round(_sourcePixelHeight * _scale) / s);
        int wPx = Math.Max(1, SnapStablePx(width + ShadowMargin * 2, s));
        int hPx = Math.Max(1, SnapStablePx(height + ShadowMargin * 2, s));
        double newContentL = west ? _resizeAnchorX - (wPx - mPx * 2) : _resizeStartL;
        double newContentT = north ? _resizeAnchorY - (hPx - mPx * 2) : _resizeStartT;
        int xPx = (int)Math.Round(newContentL - mPx);
        int yPx = (int)Math.Round(newContentT - mPx);
        ClampVisibleContentPosition(ref xPx, ref yPx,
            GetVisibleContentInWindow(wPx, hPx, s), s, GetStickerDisplayRects());

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            // cloak：resize 瞬间 DWM 不把旧帧拉伸贴合新窗口尺寸（与滚轮缩放同源伪影）
            bool sizeChanged = !ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rcCur)
                               || rcCur.Width != wPx || rcCur.Height != hPx;
            if (sizeChanged) SetWheelResizeCloak(true);
            // SWP_NOZORDER(0x0004)|SWP_NOACTIVATE(0x0010)：单次原子移动+缩放
            ScreenCaptureTool.Platform.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, wPx, hPx, StickerStyling.ResizeSwpFlags);
            if (sizeChanged) ScheduleWheelUncloak();
            // 与物理尺寸逐像素一致的托管值：HandleResized 写回相同值，布局 Resize 为空操作
            ApplyWindowRectManaged(xPx, yPx, wPx, hPx, s);
        }
        else
        {
            ResizeWindowTopLeft(width, height);
        }

        UpdateEdgeSizeIndicator(0, 0, width, height, text);
    }

    /// <summary>重排一帧：目标显示宽度（物理 px）→ 新版宽（DIP）→ 控件重排 → 基线尺寸取
    /// 实测自然尺寸 × DPI 回写。
    ///
    /// <b>单位是这条通路的关键</b>：<c>MaxWidth</c> 是 DIP 属性，<c>_sourcePixelWidth</c> 是
    /// 物理像素，创建末尾那段两帧回写（<c>_sourcePixel* = TextStickerContainer.Bounds * DPI</c>）
    /// 使二者恒满足 <c>源px = 自然DIP × DPI</c>。早先把手势算出的<b>像素</b>值直接写进
    /// <c>MaxWidth</c>，1.5x 屏上版宽每帧多长 50%，Viewbox 的 Uniform 缩放就被宽轴卡住
    /// （宽轴比值 &lt; 高轴比值），内容渲染高度追不上 <c>TextViewbox.Height</c>，多出来的那截
    /// 窗口没有背景可画 → 拖动时"贴图背景消失、下半截变透明"（且文本会跟着莫名变小）。
    /// 故这里全程在 DIP 上换算，回写值再乘回 DPI。
    ///
    /// 起手帧 <c>targetDisplayWPhysical == _resizeStartW</c> ⇒ 新自然宽 == 起手实测自然宽 ⇒
    /// 新源尺寸 == 当前值 ⇒ 零跳变（无需再靠"基线差量"绕开跳变）。版宽与高度都没变化时返回
    /// false，调用方跳过本帧落地。</summary>
    private bool ReflowTextStickerTo(double targetDisplayWPhysical)
    {
        if (TextStickerContainer == null) return false;
        double s = SafeScaling;

        // 显示宽 = 自然宽(DIP) × _scale × DPI ⇒ 拖动增量反算成 DIP 的除数是 _scale × DPI。
        // 下限 120 物理 px 与创建时一致；**上限取工作区可显示尺寸**（缺陷 B v7）：原先沿用
        // GPU 纹理口径（16000px），于是可以把卡片排得比屏幕还宽——多出来的版宽既没有排版意义
        // （一行字长过整屏），又落在视口外要靠平移才看得到。现在拖到工作区边界就停手，
        // 观感＝"已经放到最大"。
        double dipPerPx = 1.0 / (Math.Max(_scale, 0.01) * s);
        WorkAreaSurfaceLimit(s, out double limitW, out double limitH);
        double maxNatW = Math.Min(MaxTexturePhysicalPx, Math.Max(120.0, limitW)) * dipPerPx;
        double newNatW = Math.Clamp(
            _resizeStartNatW + (targetDisplayWPhysical - _resizeStartW) * dipPerPx,
            120.0 * dipPerPx, maxNatW);
        // 本帧改动前的版宽基线：重排是"改版宽→高度由排版决定"，宽度缩小时高度会长，
        // 高度顶到工作区上限时整帧回退（下面用）。贴图本就超屏时不参与回退（不新增限制）。
        double prevNatW = TextStickerContainer.Width;
        double prevMaxW = IsMarkdownLayout ? (StickerMarkdownViewer?.MaxWidth ?? double.NaN)
                                           : (StickerTextBlock?.MaxWidth ?? double.NaN);
        bool wasInsideLimit = _sourcePixelWidth <= limitW + 1.0 && _sourcePixelHeight <= limitH + 1.0;

        // 版宽约束 = 目标自然宽 − 起手实测余量（纯文本是 Margin 12×2，Markdown 是其内边距）
        double maxW = Math.Max(1.0, newNatW - _resizeLayoutPadW);
        if (IsMarkdownLayout)
        {
            if (StickerMarkdownViewer != null) StickerMarkdownViewer.MaxWidth = maxW;
        }
        else
        {
            if (StickerTextBlock != null) StickerTextBlock.MaxWidth = maxW;
        }
        // 卡片宽与版宽分开钉：MaxWidth 只管"在哪折行"，容器显式 Width 才决定卡片（连同
        // 背景）有多宽。不钉 Width 时，最长行短于版宽（ragged right）会让实测自然宽小于
        // 目标值，卡片右缘就缩回文字宽度、拖不动；钉了 Width 后容器实测宽恒等于 newNatW，
        // 于是 显式框宽 = 自然宽 × _scale 在两个轴上同时成立，Uniform 缩放恰好＝_scale，
        // 背景既不缺一截也不溢出。
        TextStickerContainer.Width = newNatW;

        // 同步量一次排版拿自然尺寸（Viewbox 的 MeasureCore 同样以无限宽量子件，
        // 故这里的结果与随后真实布局一致，不必等帧）
        // 必须走 MeasureNow 而不是容器自身的 InvalidateMeasure：上面改的 MaxWidth 在**孙节点**上，
        // 容器与中间那层 StackPanel 的 measure 缓存不会因此失效，直接量会拿到上一帧的折行高度。
        var swMeasure = System.Diagnostics.Stopwatch.StartNew();
        var nat = LayoutMetrics.MeasureNow(TextStickerContainer);
        _edgeReflowCalls++;
        double reflowMs = swMeasure.Elapsed.TotalMilliseconds;
        _edgeReflowMs += reflowMs;
        if (reflowMs > _edgeReflowMsMax) _edgeReflowMsMax = reflowMs;
        if (nat.Width < 1 || nat.Height < 1) { _edgeReflowBailNat++; return false; }

        int newSourceW = (int)Math.Round(nat.Width * s);
        int newSourceH = (int)Math.Round(nat.Height * s);
        if (newSourceW == _sourcePixelWidth && newSourceH == _sourcePixelHeight)
        {
            _edgeReflowBailSize++;
            return false;
        }
        // 重排后的卡片越过工作区上限＝视口装不下（窗口被钳在一屏内），多出来的那截要靠平移才看得到，
        // 而它本就不该存在（版宽长过整屏没有排版意义）。整帧回退到上一帧的版宽：卡片原地不动，
        // 观感＝"拖不动了"。本就超屏的贴图不参与回退（它早就处在裁切态，不该由本手势额外收紧）。
        if (wasInsideLimit && (newSourceW > limitW + 1.0 || newSourceH > limitH + 1.0))
        {
            _edgeReflowBailLimit++;
            if (IsMarkdownLayout)
            {
                if (StickerMarkdownViewer != null && prevMaxW >= 1) StickerMarkdownViewer.MaxWidth = prevMaxW;
            }
            else if (StickerTextBlock != null && prevMaxW >= 1) StickerTextBlock.MaxWidth = prevMaxW;
            TextStickerContainer.Width = prevNatW;
            return false;
        }
        _sourcePixelWidth = newSourceW;
        _sourcePixelHeight = newSourceH;
        return true;
    }

    /// <summary>拖边结束的收尾：只复位手势状态。覆写通路（图片 + 全部文本/Markdown，含重排）
    /// **刻意不在此退出手势态**——与滚轮同一策略（收口交给失活/快捷键/切缩略图等非手势时机），
    /// 松手即收口会多出一整次 cloak + resize 闪烁。逐格 resize 那条路窗口本就已在终态。
    /// 阶段 2 前这里还有一条例外"内容封顶时按贴合尺寸落定"（不落下就是真丢像素）；
    /// 现在窗口每格跟着内容走、越出视口的部分靠平移查看，那条前提不成立了 ⇒ 连同判据一起删除。</summary>
    private void EndEdgeResize()
    {
        if (!_isResizing) return;
        _isResizing = false;
        if (_resizeCaptured)
        {
            _resizePointer?.Capture(null);
            _resizePointer = null;
            _resizeCaptured = false;
        }
        if (EdgeSizeIndicator != null) EdgeSizeIndicator.IsVisible = false;
        if (_resizeReflows) RefreshTextStickerSnapshotAfterReflow();
        WheelDiag($"拖边结束：scale={_scale:F3} 尺寸算法={(_resizeReflows ? "已重排（改版宽）" : "统一比例")} "
                  + $"几何通路={(_resizeEnvelope ? "覆写（窗口已逐格贴合）" : "布局驱动")} 源图={_sourcePixelWidth}x{_sourcePixelHeight}");
        // v8 采证：输入→合帧→落地 的漏斗计数，逐级定位"卡片不跟手"卡在哪一环
        // （Moved 少＝事件没投进来；实挂RAF≪排帧请求＝合帧吞掉（正常）；帧数≪实挂RAF＝RAF 没跑；
        //  最大目标增量大而源图没跟上＝重排通路本身；单帧最大耗时＝重排成本）
        if (_resizeReflows)
            WheelDiag($"拖边采证：Moved={_edgeEvtMoved} 排帧请求={_edgeRafQueued} 实挂RAF={_edgeRafScheduled} " +
                      $"帧数={_edgeFrames} 最大目标增量={_edgeMaxDelta:F0}px 重排={_edgeReflowCalls}次 " +
                      $"累计{_edgeReflowMs:F0}ms 单帧最大{_edgeReflowMsMax:F1}ms " +
                      $"回退(尺寸未变{_edgeReflowBailSize}/实测非法{_edgeReflowBailNat}/越过上限{_edgeReflowBailLimit})");
        // 阶段 2 起窗口每格已跟着内容走，"松手必须落定一次"的前提（信封＝9 倍大窗口）消失了：
        // 这里再 commit 也拿不到任何几何变化，只会多走一次 cloak ⇒ 与滚轮同策略，收口交给
        // 非手势时机（窗口失活 / 快捷键 / 切缩略图）。
        SaveArchiveSnapshot();
        SchedulePinWheelSettle();   // 分层呈现档：拖边松手同样自动收口（窗口严格贴合）
    }

    /// <summary>重排结束后重拍位图快照。文本贴图的 <c>_bitmap</c> 只服务复制图像/另存为/
    /// OCR/翻译（显示走真实控件排版），版宽变了必须换新，否则这几项拿到的仍是旧版宽的图。
    /// <b>刻意不走 <see cref="ReplaceImage"/></b>：那会清 <c>_textContent</c>/
    /// <c>_layoutInfo</c> 并撤掉选词覆盖层，inline 文本分支立刻失效、变成"图字双份"。
    /// Markdown 贴图本就没有 <c>_bitmap</c>（这几项实时渲染容器），无需处理。</summary>
    private void RefreshTextStickerSnapshotAfterReflow()
    {
        if (_bitmap == null) return;
        try
        {
            System.Drawing.Bitmap? fresh = SnapshotTextSticker();
            if (fresh == null) return;
            var oldBitmap = _bitmap;
            _bitmap = fresh;
            ReleaseAnnotationSourceCache();
            // 显示用的是控件而非 _source（文本贴图 StickerImage 不可见），故不换 Avalonia 图源，
            // 只换这份"对外输出"的位图；旧图延迟释放（Avalonia 可能仍在渲染旧帧）。
            Dispatcher.UIThread.Post(() =>
            {
                try { oldBitmap.Dispose(); } catch { }
            }, DispatcherPriority.Background);
            // 单位自洽判据：快照尺寸应等于「活体容器 DIP 尺寸 × DPI」。两者不符就说明
            // 版宽约束又被写成了像素值（Viewbox 会因此 letterbox，观感＝背景缺一截）。
            var bounds = TextStickerContainer?.Bounds ?? default;
            AppLogger.Info("[缩放诊断] 重排后重拍快照 " + fresh.Width + "x" + fresh.Height
                           + " 容器DIP=" + bounds.Width.ToString("F1") + "x" + bounds.Height.ToString("F1")
                           + "×DPI" + SafeScaling.ToString("F2") + "="
                           + Math.Round(bounds.Width * SafeScaling) + "x" + Math.Round(bounds.Height * SafeScaling));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 重排后重拍快照失败: " + ex.Message);
        }
    }

    /// <summary>把当前文本/Markdown 排版拍成物理像素位图（显示区实时渲染，与 <c>_scale</c> 无关）。
    /// 返回 null = 排版尺寸尚不可用或渲染失败，调用方保留旧图。</summary>
    private System.Drawing.Bitmap? SnapshotTextSticker()
    {
        if (TextStickerContainer == null) return null;
        try
        {
            double scaling = SafeScaling;
            int width = _sourcePixelWidth, height = _sourcePixelHeight;
            if (StickerColorSwatch.IsVisible)
            {
                // 颜色卡没有可靠的源图尺寸，必须测量整个容器，包含色块与全部色值行。
                // 快照使用自然 DIP 尺寸，不把显示缩放或信封窗口大小带进导出。
                TextStickerContainer.InvalidateMeasure();
                TextStickerContainer.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = TextStickerContainer.DesiredSize;
                if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height)
                    || size.Width <= 0 || size.Height <= 0) return null;
                TextStickerContainer.Arrange(new Avalonia.Rect(size));
                var bounds = TextStickerContainer.Bounds;
                double pixelWidth = Math.Ceiling(bounds.Width * scaling);
                double pixelHeight = Math.Ceiling(bounds.Height * scaling);
                if (!double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight)
                    || pixelWidth <= 0 || pixelHeight <= 0
                    || pixelWidth > int.MaxValue || pixelHeight > int.MaxValue) return null;
                width = (int)pixelWidth;
                height = (int)pixelHeight;
            }
            if (width <= 0 || height <= 0) return null;
            double dpi = 96.0 * scaling;
            var pixelSize = new PixelSize(width, height);
            using var renderTarget = new RenderTargetBitmap(pixelSize, new Avalonia.Vector(dpi, dpi));
            renderTarget.Render(TextStickerContainer);

            using var ms = new MemoryStream();
            renderTarget.Save(ms);
            ms.Position = 0;
            // GDI+ 的 Bitmap(Stream) 在位图存活期间必须保持该流打开，而 ms 随本方法返回即释放：
            // 再拷一份内存位图脱离该流，否则调用方（重排后长期持有的 _bitmap）稍后取像素会抛异常。
            using var streamed = new System.Drawing.Bitmap(ms);
            return new System.Drawing.Bitmap(streamed);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 文本贴图快照失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>拖边提示文案的唯一换算入口：重排模式报版宽（字号不变、行数变，报缩放比
    /// 没有意义），其余报「显示物理像素 + 百分比」。</summary>
    private string FormatEdgeSizeText() => _resizeReflows
        ? "行宽 " + _sourcePixelWidth + " px"
        : (int)Math.Round(_sourcePixelWidth * _scale) + "×" + (int)Math.Round(_sourcePixelHeight * _scale)
          + " " + (_scale * 100).ToString("F0") + "%";

    /// <summary>尺寸提示层：x/y/w/h 均为**内容区 DIP**（与 SetWheelGestureVisual 同一坐标基），
    /// 逐帧跟随图片矩形，故信封态下提示不会留在透明区。刻意无可见背景（protection.md 的
    /// Stretch 红线只约束带背景的层），且整层不参与命中测试。</summary>
    private void UpdateEdgeSizeIndicator(double x, double y, double w, double h, string text)
    {
        if (EdgeSizeIndicator == null) return;
        // 可见性：本方法只负责"摆好并写字"，隐藏唯一入口是 EndEdgeResize
        EdgeSizeIndicator.IsVisible = true;
        EdgeSizeIndicator.Margin = new Thickness(x, y, 0, 0);
        EdgeSizeIndicator.Width = w;
        EdgeSizeIndicator.Height = h;
        if (EdgeSizeIndicatorText != null) EdgeSizeIndicatorText.Text = text;
    }



    /// <summary>「切换贴图标注模式」的条件表。<b>唯一一份</b>：下面的进入判定与按键分支都必须调它，
    /// 否则两处条件漂移会让"该不该推迟信封收口"判错，退回到闪一下的老毛病或多缩一次窗。
    /// 键位自 2026-09-29 起读目录（<c>sticker.annotate</c>，默认 Space），不再写死——此前设置页里查无此键。</summary>
    private bool IsAnnotationToggleKey(KeyEventArgs e)
        => ShortcutMatches("sticker.annotate", e) && !_isLocked && !_isMousePenetration && _activeAnnotationKey != "text";

    /// <summary>这一记按键是否会走「进入标注」分支（缩略图模式下 <see cref="EnterAnnotationMode"/>
    /// 会点名拒入、并不进模式，故那一格仍按老路子先提交收缩，不把信封留给缩略图）。</summary>
    private bool IsAnnotationEnterKey(KeyEventArgs e)
        => IsAnnotationToggleKey(e) && !_isAnnotating && !_isThumbnailMode;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        BringStickerToFront();
        if (HandleCanvasKey(e)) return;
        // 快捷键与拖边缩放不并存：先收手势再提交信封（与松手/中键/右键同一收口顺序）
        if (_isResizing) EndEdgeResize();

        // 滚轮缩放手势激活时先同步提交收缩（信封态窗口不跨快捷键语义）
        // ⚠ 唯一例外：这一记按键就是"进入标注"。此刻提交＝在同一次按键里先做一次带 cloak 的整窗
        // 缩窗（旧帧被 DWM 拉伸成左上角鬼影，只能整窗隐没来藏），紧贴进模式=用户实测的
        // "缩放后进标注闪一下"。改成让信封跨标注期存活，退出标注同样不缩窗，收口留给既有的非手势时机。
        if (_wheelVisualOverride && !IsAnnotationEnterKey(e)) CommitWheelGesture("快捷键");

        // 文本编辑期让路：本窗口的 KeyDown 走 Tunnel 相位，会抢在获得焦点的 TextBox 之前看到按键。
        // 不加这道守卫，编辑中敲空格＝退出标注（截图侧同一条判定：CaptureSession 的 HasActiveTextEditing），
        // Ctrl+C＝复制整张贴图而不是复制选中的字。Ctrl+Enter 提交由构造函数里挂的共享处理器负责。
        // 只放行"关闭贴图"：标注中它本就走 ExitAnnotationMode，其内部会先结束编辑把文字提交掉。
        if (TextTool.HasActiveTextEditing && !ShortcutMatches("sticker.close", e)) return;
        // 序号浮条的说明文字框（2026-09-18）同理，但连"关闭贴图"也不放行：Esc 在这格里的语义是
        // "取消这次输入"（由 CounterPill 自己在冒泡相位收掉），不是把贴图关掉。
        if (CounterPillHost.IsEditingNote) return;

        // 取色器：配置的修饰键按下即出，显示期间复制／切格式走同一改键目录。
        // 放在所有快捷键之前：取色器可见时那两枚键的语义是"复制色值/切格式"，不能被别的绑定吃掉。
        if (TryHandleColorPickerKey(e)) return;

        // 非方向键按下＝这段步进到此为止：下一次方向键另起一段、另算一条撤销记录
        if (e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Up && e.Key != Key.Down)
        {
            FlushAnnotationNudgeSession();
        }

        // 关闭贴图（默认 Esc，可在设置中改键）
        if (ShortcutMatches("sticker.close", e))
        {
            // 翻译气泡展开时，Esc 的语义是"收起这张气泡"，先于关贴图；再按一次才走关贴图——
            // 与标注态先退标注同一条两级出口。
            // 🔴 紫线与紫点是持久标记，Esc **不**清除它们（清除只有气泡红 ✕ 与关贴图两条路），
            // 否则用户一次误触就把译好的几段全丢了，与"留着看"的初衷相悖。
            if (_bubbleShown)
            {
                e.Handled = true;
                HideTranslateBubble();
                return;
            }
            if (_isAnnotating)
            {
                e.Handled = true;
                ExitAnnotationMode();
                return;
            }
            e.Handled = true;
            Close();
            return;
        }

        // 进出贴图标注模式（sticker.annotate，默认空格，设置页可改）
        // 文本工具激活时空格不让路退出（截图侧 CaptureSession 同一判定）：此刻空格是"正在写字"的一部分，
        // 不是"结束标注"的手势；编辑期那一路已由上面的 HasActiveTextEditing 守卫兜住。
        if (IsAnnotationToggleKey(e))
        {
            e.Handled = true;
            ToggleAnnotationMode();
            return;
        }

        // If locked, we don't allow any keys except unlocking and closing
        if (_isLocked)
        {
            if (ShortcutMatches("sticker.lock", e))
            {
                e.Handled = true;
                ToggleLock();
            }
            return;
        }

        // 标注编辑三键：撤销 / 重做 / 删除选中图元。
        // 位置两处硬约束：必须在上面的锁定早退之后（锁定的贴图不接受任何编辑），
        // 也必须在 HasActiveTextEditing 早退之后（正在打字时 Ctrl+Z 撤销的是文本框里的字，不是图元）。
        if (_isAnnotating)
        {
            if (ShortcutMatches("sticker.delete", e))
            {
                e.Handled = true;
                DeleteSelectedAnnotation();
                return;
            }
            if (ShortcutMatches("sticker.undo", e))
            {
                e.Handled = true;
                FlushAnnotationNudgeSession();
                UndoAnnotation();
                return;
            }
            if (ShortcutMatches("sticker.redo", e))
            {
                e.Handled = true;
                FlushAnnotationNudgeSession();
                RedoAnnotation();
                return;
            }

            // 方向键步进选中图元（issue #13 新增能力）：每次 1 内容像素。放在三键之后让位给用户绑定，
            // 文本编辑期那一路已在函数开头 return 掉；没有选中图元时不吞按键，交还给后面的通路。
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                int stepDx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int stepDy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (StepSelectedShape(stepDx, stepDy))
                {
                    e.Handled = true;
                    return;
                }
            }

            // 选中序号的键盘微调（与截图侧 CaptureSession 逐条一致）：+ / − 改**这一个**的编号，一步 1、下限 1。
            // 与浮条 − / + 共用 StepCounterNumber 一份落地；带任何修饰键（含 Shift）一律不接管，
            // 没选中序号时也不吞按键，交还给后面的通路。
            // ⚠ 这里没有 A：A 是贴图侧可改键的通用字母位，加说明文字的入口走浮条中间那一格。
            if (e.KeyModifiers == KeyModifiers.None
                && GetSelectedAnnotationShape() is Annotations.CounterShape)
            {
                int numberStep = e.Key switch
                {
                    Key.Add or Key.OemPlus => 1,
                    Key.Subtract or Key.OemMinus => -1,
                    _ => 0,
                };
                if (numberStep != 0)
                {
                    StepCounterNumber(numberStep, "键盘");
                    e.Handled = true;
                    return;
                }
            }
        }

        // 缩略图模式（默认 Shift+R）
        if (ShortcutMatches("sticker.thumbnail", e))
        {
            e.Handled = true;
            ToggleThumbnailMode();
            return;
        }

        // 锁定 / 解锁（默认 Ctrl+L）
        if (ShortcutMatches("sticker.lock", e))
        {
            e.Handled = true;
            ToggleLock();
            return;
        }

        // 鼠标穿透（默认 Ctrl+P）
        if (ShortcutMatches("sticker.penetrate", e))
        {
            e.Handled = true;
            ToggleMousePenetration();
            return;
        }

        // 贴图阴影（默认 Shift+Y，仅焦点贴图）。自带烘焙阴影带的贴图同样可切：
        // 切的是窗体阴影（UI 状态指示），图片里那圈烘焙阴影是素材、不受影响。
        if (ShortcutMatches("sticker.shadow", e) && _isFocused)
        {
            e.Handled = true;
            // 🔴 翻转基准取**此刻实际画着的状态**而不是 `_shadowVisible` 自身（2026-10-01，issue #50）：
            // Auto/Off/On 档下 `_shadowVisible` 可能与眼前所见不一致（它初值来自全局默认开关，
            // 而画不画由 ShouldPaintWindowShadow 定）。按旧写法翻它自己，用户会遇到"按一下没反应"
            // ——例如 Auto 档判出"不画"而 `_shadowVisible` 恰为 true 时，翻成 false 仍是不画。
            _shadowVisible = !ShouldPaintWindowShadow();
            // 手动切过即永久接管本贴图的判据（档位让位）；随存档保存，关窗恢复后仍是用户选的那样
            _shadowManualOverride = true;
            ApplyShadow();
            App.RaiseStickerShadowToast(_shadowVisible, this);
            SaveArchiveSnapshot();
            return;
        }

        // 复制贴图 / 复制选中文本（默认 Ctrl+C）
        if (ShortcutMatches("sticker.copy", e))
        {
            // 字级 OCR 选区（图片贴图"文本可选择"/Alt 快速选词）
            if (_selectedWordIndices.Count > 0)
            {
                e.Handled = true;
                CopySelectedText();
                return;
            }

            // 文本/Markdown 贴图：实时选区优先，快照兜底。不再依赖 IsFocused 交给
            // 原生 Ctrl+C——原生 SelectableTextBlock 失焦即 ClearSelection，焦点被
            // 右键菜单/其他窗口夺走后实时选区可能已被清空；直接读选区复制最可靠。
            bool isTextSticker = (StickerTextBlock != null && StickerTextBlock.IsVisible)
                              || (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible);
            if (isTextSticker)
            {
                string? textSel = null;
                if (StickerTextBlock != null && StickerTextBlock.IsVisible)
                {
                    textSel = StickerTextBlock.SelectedText;
                }
                if (string.IsNullOrEmpty(textSel) && StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible)
                {
                    textSel = StickerMarkdownViewer.SelectedText;
                }
                if (string.IsNullOrEmpty(textSel))
                {
                    textSel = _textSelSnapshot;
                }

                if (!string.IsNullOrEmpty(textSel))
                {
                    e.Handled = true;
                    ClipboardService.SetText(textSel);
                    StickerToastWindow.ShowAtTop("已复制 " + textSel.Length + " 个字符");
                    return;
                }

                AppLogger.Info($"[贴图诊断] Ctrl+C 无文本选区回退复制图像：focused={(StickerTextBlock?.IsFocused == true)} altTemp={_altTextStickerSelect} persistent={_isTextSelectable} snapNull={(_textSelSnapshot == null)} mdVis={(StickerMarkdownViewer?.IsVisible == true)}");
            }

            e.Handled = true;
            OnCopyImageClick(null, new Avalonia.Interactivity.RoutedEventArgs());
            return;
        }

        // 保存贴图（默认 Ctrl+S）
        if (ShortcutMatches("sticker.save", e))
        {
            e.Handled = true;
            OnSaveImageClick(null, new RoutedEventArgs());
            return;
        }

        // 贴图翻译（默认 Ctrl+Q）
        if (ShortcutMatches("sticker.translate", e))
        {
            e.Handled = true;
            OnTranslateClick(null, new RoutedEventArgs());
            return;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (IsColorPickerSummonKey(e.Key)) RefreshColorPicker();
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            if (_isCanvasMode) { EndCanvasPan(); UpdateCanvasResizeHints(false); }
            _isCtrlDragPending = false;
            if (!_isResizing && !_isDragging && !_isPerformingDragDrop)
            {
                Cursor = null;
            }
        }

        // 划词工具栏：Shift+方向键那类键盘扩选在抬起这一下成段（与鼠标松手同一时机）
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.A)
        {
            ScheduleSelectionToolbarUpdate();
        }
    }

/// <summary>对外暴露的翻译入口，供脚本或全局热键调用。</summary>
public void TriggerTranslate()
{
    OnTranslateClick(null, new RoutedEventArgs());
}

/// <summary>右键菜单"翻译"：对贴图图片或文本执行翻译（支持 Toggle 切换原文/译文）。</summary>
private void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Toggle 逻辑：如果已有译文，则在原图与译文之间切换
            if (_originalBeforeTranslate != null && _translatedImage != null)
            {
                if (_isShowingTranslation)
                {
                    // 当前显示译文，切换回原图
                    ReplaceImage(new System.Drawing.Bitmap(_originalBeforeTranslate),
                        isCutout: _isCutout,
                        restoredBakedCard: _bakedCardGeometry,
                        restoredHasBakedShadow: _hasBakedShadow,
                        restoredHideCheckerboard: _hideAlphaCheckerboard,
                        restoredShadowPadPx: _bakedShadowPadPx);
                    _isShowingTranslation = false;
                    StickerToastWindow.ShowAtTop("已恢复原图");
                }
                else
                {
                    // 当前显示原图，切换回译文。译文那张已经套过外壳（或本来就没有壳），
                    // 两项备份的图与当前画布同尺寸 ⇒ 外缘声明原样交回，不许在这一步归零：
                    // 归零会让自带阴影带的原图恢复后窗体阴影压在阴影带上（双层影）、垫底消失。
                    ReplaceImage(new System.Drawing.Bitmap(_translatedImage),
                        isCutout: _isCutout,
                        restoredBakedCard: _bakedCardGeometry,
                        restoredHasBakedShadow: _hasBakedShadow,
                        restoredHideCheckerboard: _hideAlphaCheckerboard,
                        restoredShadowPadPx: _bakedShadowPadPx);
                    _isShowingTranslation = true;
                    StickerToastWindow.ShowAtTop("已显示译文");
                }
                return;
            }

            // 翻译＝"拿这张图去干别的事"：先定稿收起标注层，再进入识别/换图流程。
            FinalizeAnnotationForGeometryChange("翻译");
            try { _originalUncutBitmap?.Dispose(); } catch { }
            _originalUncutBitmap = null;
            var settings = AppSettings.Load();
            var config = settings.ToTranslationConfig();
            if (!config.IsValid)
            {
                StickerToastWindow.ShowAtTop("请先在设置中配置翻译服务 API 密钥");
                return;
            }

            // 如果贴图包含文本内容（文本贴图），则走文本翻译
            if (!string.IsNullOrEmpty(_textContent) && StickerTextBlock != null && StickerTextBlock.IsVisible)
            {
                var win = TranslationResultWindow.ForText(_textContent, settings);
                win.Show();
                win.Activate();
                return;
            }

            // 否则走图片翻译
            if (_bitmap != null)
            {
                if (config.Mode == TranslationMode.ImageToImage)
                {
                    if (!TryBeginTranslate())
                    {
                        AppLogger.Info("[翻译] 贴图入口拒绝重入：该贴图已有进行中的流水线");
                        StickerToastWindow.ShowAtTop("正在翻译中，请稍候…");
                        return;
                    }
                    // 图片到图片：产商接口一次完成识别、擦除与译文绘制 → 直接替换贴图，不弹窗。
                    // 用贴图内的进行态提示而非 toast：进度就在贴图上，与截图翻译体验一致；
                    // 该提示不拦鼠标，等待期间仍可拖动/缩放本贴图。
                    ShowTranslatingOverlay("翻译中…");
                    _ = Task.Run(async () =>
                    {
                        System.Drawing.Bitmap? srcBitmap = null;
                        try
                        {
                            // 复制一份供后台编码使用，并作为原图备份
                            srcBitmap = new System.Drawing.Bitmap(_bitmap);
                            
                            // 备份原图（用于 Toggle）：如果还没备份过，则保存
                            if (_originalBeforeTranslate == null)
                            {
                                _originalBeforeTranslate = new System.Drawing.Bitmap(srcBitmap);
                            }

                            // ① 原图编 PNG 字节（产商只收 base64，容器格式由门面内部按需转 JPEG）。
                            //    寄出去之前先把"壳"剥掉：外圈那圈烘焙阴影带＋描边环＋圆角缺口是素材的
                            //    边界而不是内容，产商只认不透明图——透明区被它填成白/黑纯色，回传的 JPG
                            //    又没有 alpha，于是"圆角＋阴影"的贴图翻译完就成了一张直角白底图
                            //    （2026-10-01 用户报）。只寄内容矩形，回来再把原来那张壳套回去。
                            var contentRect = SelectionEffects.ContentRectOf(srcBitmap, _bakedCardGeometry);
                            bool cropped = contentRect.X > 0 || contentRect.Y > 0
                                || contentRect.Width < srcBitmap.Width || contentRect.Height < srcBitmap.Height;
                            // 有壳＝画布外缘不全是内容：要么裁掉了一圈壳，要么图里本来就有透明/半透明像素
                            bool hasShell = cropped || HasTransparentPixels(srcBitmap);
                            AppLogger.Info($"StickerWindow: 翻译剥壳 画布{srcBitmap.Width}x{srcBitmap.Height} " +
                                $"内容矩形({contentRect.X},{contentRect.Y},{contentRect.Width},{contentRect.Height}) " +
                                $"裁壳={cropped} 有壳={hasShell}");

                            byte[] pngBytes;
                            using (var ms = new MemoryStream())
                            {
                                if (cropped)
                                {
                                    using var crop = srcBitmap.Clone(contentRect,
                                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    crop.Save(ms, ImageFormat.Png);
                                }
                                else
                                {
                                    srcBitmap.Save(ms, ImageFormat.Png);
                                }
                                pngBytes = ms.ToArray();
                            }

                            // ② 产商图片翻译。走无语言参数重载：产商回传的图上文字语种
                            //    与目标语言同族时自动改翻次级目标语言
                            var translator = new TranslationService(config);
                var result = await TranslationFlow.TranslateImageAsync(translator, pngBytes).ConfigureAwait(false);

                            if (!result.Success || result.ImageBytes == null)
                            {
                                string message = result.UserMessage;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    HideTranslatingOverlay();
                                    StickerToastWindow.ShowAtTop(message);
                                });
                                return;
                            }

                            UsageStatistics.RecordTranslate(); // 统计：翻译成功一次

                            // ③ 成品图字节 → 位图。GDI+ 的 Image 依附于解码流，流一关便不可再绘制，
                            //    故读完立刻拷一份独立 Bitmap 再释放两者。
                            System.Drawing.Bitmap renderedBitmap;
                            using (var decoded = new MemoryStream(result.ImageBytes))
                            using (var image = System.Drawing.Image.FromStream(decoded))
                            {
                                renderedBitmap = new System.Drawing.Bitmap(image);
                            }

                            // ③′ 套壳：把译文贴回原来那张壳。内容矩形以外一个字节都不碰，矩形内只换
                            //     完全不透明的像素——阴影带、描边环与圆角弧的抗锯齿边缘逐字节保持原样，
                            //     成品画布尺寸也与原图相同（贴图的位置、缩放、窗体阴影几何因此都不用重算）。
                            bool shellRetained = false;
                            if (hasShell)
                            {
                                var wrapped = SelectionEffects.WrapContentIntoShell(srcBitmap, renderedBitmap, contentRect);
                                if (wrapped != null)
                                {
                                    renderedBitmap.Dispose();
                                    renderedBitmap = wrapped;
                                    shellRetained = true;
                                }
                                else
                                {
                                    AppLogger.Warn("StickerWindow: 译文套回原图外壳失败，本次按产商原样直角图落地");
                                }
                            }
                            
                            AppLogger.Info("StickerWindow: 译文套壳 " + (hasShell
                                ? (shellRetained
                                    ? $"成功，成品 {renderedBitmap.Width}x{renderedBitmap.Height}（与原图同画布）"
                                    : "失败，本次按产商直角图落地")
                                : "跳过（该图无外壳）"));

                            // 备份译文（用于 Toggle）
                            try { _translatedImage?.Dispose(); } catch { }
                            _translatedImage = new System.Drawing.Bitmap(renderedBitmap);
                            _isShowingTranslation = true;

                            // ④ UI 线程替换贴图
                            Dispatcher.UIThread.Post(() =>
                            {
                                // 套壳成功＝这张位图的外缘还是原来那圈烘焙效果 ⇒ 三项声明继续成立、
                                // 边距按当前值原样交回，禁止走 ReplaceImage 的归零分支（归零会让窗体
                                // 阴影带着旧内缩坐在译文后面、阴影带下的垫底消失）。
                                ReplaceImage(renderedBitmap,
                                    isCutout: _isCutout,
                                    restoredBakedCard: shellRetained ? _bakedCardGeometry : null,
                                    restoredHasBakedShadow: shellRetained ? _hasBakedShadow : null,
                                    restoredHideCheckerboard: shellRetained ? _hideAlphaCheckerboard : null,
                                    restoredShadowPadPx: shellRetained ? _bakedShadowPadPx : null);
                                StickerToastWindow.ShowAtTop(result.UserMessage + "（再次点击可切换原文/译文）");
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("贴图图片到图片翻译异常", ex);
                            Dispatcher.UIThread.Post(() =>
                            {
                                HideTranslatingOverlay();
                                StickerToastWindow.ShowAtTop("翻译失败: " + ex.Message);
                            });
                        }
                        finally
                        {
                            EndTranslate(); // 闸门必须在所有退出点归还，否则该贴图此后永久拒绝翻译
                            try { srcBitmap?.Dispose(); } catch { }
                        }
                    });
                    return;
                }

                // 图片到文本：OCR 出原文后分流——短文本出极简浮窗（锚定贴图），长文本直接开编辑窗口
                if (!TryBeginTranslate())
                {
                    AppLogger.Info("[翻译] 贴图入口拒绝重入（图片到文本）：该贴图已有进行中的流水线");
                    StickerToastWindow.ShowAtTop("正在翻译中，请稍候…");
                    return;
                }
                StickerToastWindow.ShowAtTop("识别中…");
                var ocrFlowTask = TranslationFlow.RunOcrAndDispatchAsync(_bitmap, settings, GetScreenRect());
                _ = ocrFlowTask.ContinueWith(_ => EndTranslate(), System.Threading.Tasks.TaskScheduler.Default);
                return;
            }

            // 如果贴图是渲染的文字贴图（无 _bitmap）
            if (!string.IsNullOrEmpty(_textContent))
            {
                var win = TranslationResultWindow.ForText(_textContent, settings);
                win.Show();
                win.Activate();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow.OnTranslateClick 异常", ex);
            // 同步段抛异常时闸门可能已占用（后台任务的 finally 不会执行），此处兜底释放，
            // 否则该贴图此后会永久拒绝翻译
            EndTranslate();
            StickerToastWindow.ShowAtTop("翻译失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 贴图窗口在屏幕上的物理像素矩形，用作翻译浮窗的定位锚点。
    /// 信封态（按下不再收缩窗口）下取<see cref="GetFittedClientRectDip"/>的等效贴合矩形——
    /// 窗口矩形此时含透明 fringe（远大于图片），而锚点要落在可见内容上，取值与"先提交收缩
    /// 再定位"逐像素一致。
    /// </summary>
    private PixelRect GetScreenRect()
    {
        double scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
        // 覆写态：窗口可能比内容大一圈，故取等效贴合矩形（内容+阴影），否则翻译锚点/浮窗定位会偏。
        if (_wheelVisualOverride)
        {
            var box = GetFittedClientRectDip();
            var win = GetWindowOriginPx();
            return new PixelRect(
                win.X + (int)Math.Round(box.X * scale),
                win.Y + (int)Math.Round(box.Y * scale),
                Math.Max(1, (int)Math.Round(box.Width * scale)),
                Math.Max(1, (int)Math.Round(box.Height * scale)));
        }
        int w = (int)Math.Round(Bounds.Width * scale);
        int h = (int)Math.Round(Bounds.Height * scale);
        return new PixelRect(Position.X, Position.Y, Math.Max(1, w), Math.Max(1, h));
    }

    // ── 翻译流水线闸门 ────────────────────────────────────────
    // 一张贴图同一时刻只允许一条「复制图 → OCR → 请求译文 → 绘制 → 换图」流水线。
    // 并发重入（等待期间又点一次「翻译」）会让两条线抢同一块进行态提示层：A 收尾撤提示
    // 时把 B 刚设上的「翻译中…」一起撤掉，或 B 把提示复活在已出译文的图上（观感＝永久卡住）；
    // 两条线还会互相覆盖译文，并在旧位图延迟释放的窗口里读到已释放的图（GDI+ generic error）。
    // 计数而非 bool：占用可能在 UI 线程、释放在后台线程，Interlocked 保证这一对操作原子。
    private int _translateInFlight;

    /// <summary>翻译原图备份（用于 Toggle 切换恢复）。</summary>
    private System.Drawing.Bitmap? _originalBeforeTranslate;
    
    /// <summary>译文图备份（用于 Toggle 切换）。</summary>
    private System.Drawing.Bitmap? _translatedImage;
    
    /// <summary>当前是否显示译文（true）还是原图（false）。</summary>
    private bool _isShowingTranslation;

    /// <summary>尝试占用本贴图的翻译流水线。已有进行中的翻译时返回 <c>false</c>，
    /// 调用方应直接提示用户而不要重入；成功后必须在 <c>finally</c> 里 <see cref="EndTranslate"/>。</summary>
    public bool TryBeginTranslate() => System.Threading.Interlocked.CompareExchange(ref _translateInFlight, 1, 0) == 0;

    /// <summary>释放翻译流水线占用（与 <see cref="TryBeginTranslate"/> 成对）。</summary>
    public void EndTranslate() => System.Threading.Interlocked.Exchange(ref _translateInFlight, 0);

    /// <summary>本贴图当前是否有翻译流水线在跑。</summary>
    public bool IsTranslateInFlight => System.Threading.Volatile.Read(ref _translateInFlight) != 0;

    /// <summary>
    /// 设置翻译原图和译文的备份，用于 Toggle 切换。供截图翻译流程调用。
    /// </summary>
    /// <param name="original">原图（调用方需传入独立副本，所有权转移给贴图窗口）。</param>
    /// <param name="translated">译文图（调用方需传入独立副本，所有权转移给贴图窗口）。</param>
    public void SetTranslationImages(System.Drawing.Bitmap original, System.Drawing.Bitmap translated)
    {
        try { _originalBeforeTranslate?.Dispose(); } catch { }
        try { _translatedImage?.Dispose(); } catch { }
        _originalBeforeTranslate = original;
        _translatedImage = translated;
        _isShowingTranslation = true; // 当前显示的是译文
    }

    // ── 智能抠图流水线闸门与原图备份 ──────────────────────────────
    private int _mattingInFlight;
    private System.Drawing.Bitmap? _originalUncutBitmap;
    private SelectionEffects.CardGeometry _originalBakedCardGeometry;
    private bool _originalHasBakedShadow;
    private bool _originalHideAlphaCheckerboard;

    public bool TryBeginMatting() => System.Threading.Interlocked.CompareExchange(ref _mattingInFlight, 1, 0) == 0;
    public void EndMatting() => System.Threading.Interlocked.Exchange(ref _mattingInFlight, 0);
    public bool IsMattingInFlight => System.Threading.Volatile.Read(ref _mattingInFlight) != 0;

    private void OnPortraitMattingClick(object? sender, RoutedEventArgs e)
    {
        ExecuteMatting(ScreenCaptureTool.Core.Matting.MattingModelType.Portrait);
    }

    private void OnGeneralMattingClick(object? sender, RoutedEventArgs e)
    {
        ExecuteMatting(ScreenCaptureTool.Core.Matting.MattingModelType.General);
    }

    /// <summary>
    /// 对当前贴图执行智能抠图。结果去向由设置项「抠图结果」决定（<see cref="ExecuteMatting"/> 的
    /// resultDestination 参数可显式覆盖）：剪贴板＝结果仅写入剪贴板、贴图保持原图；
    /// 贴图＝贴图替换为抠图结果、不动剪贴板；剪贴板并贴图＝两者兼得（历史行为）。
    /// </summary>
    /// <param name="type">模型；null 则跟随设置默认模型。</param>
    /// <param name="resultDestination">结果去向；null 则跟随设置。</param>
    /// <param name="closeWhenClipboardOnly">本贴图是否为截图工具栏/脚本流程**临时创建**的抠图载体：
    /// 为 true 且结果去向为「仅剪贴板」时，抠图完成后自动关闭贴图（桌面不留窗）。
    /// 贴图右键菜单等"贴图本来就在"的入口必须保持 false。</param>
    public async void ExecuteMatting(ScreenCaptureTool.Core.Matting.MattingModelType? type = null,
        ScreenCaptureTool.Core.Matting.MattingResultDestination? resultDestination = null,
        bool closeWhenClipboardOnly = false)
    {
        // 缺模型先居中开卡就地下载；卡关掉之后才轮到贴图上的「抠图中」胶囊出现（设计稿 05 节：
        // 胶囊只管"正在抠"这一件事，下载进度归进度卡）。取消或失败时进度卡自己停在失败态，这里静默退回。
        if (!await Services.DependencyGate.EnsureAsync(ScreenCaptureTool.Core.Deps.DepIds.Matting))
        {
            if (closeWhenClipboardOnly) Close();
            return;
        }

        var actualType = type ?? AppSettings.Load().GetMattingModelType();
        var destination = resultDestination ?? AppSettings.Load().GetMattingResultDestination();
        // 贴图替换＝「贴图」「剪贴板并贴图」两档；剪贴板写入＝「剪贴板」「剪贴板并贴图」两档
        bool replaceSticker = destination is ScreenCaptureTool.Core.Matting.MattingResultDestination.Sticker
            or ScreenCaptureTool.Core.Matting.MattingResultDestination.ClipboardAndSticker;
        bool writeToClipboard = destination is ScreenCaptureTool.Core.Matting.MattingResultDestination.Clipboard
            or ScreenCaptureTool.Core.Matting.MattingResultDestination.ClipboardAndSticker;
        try
        {
            if (_bitmap == null)
            {
                StickerToastWindow.ShowAtTop("当前贴图无可抠图的内容");
                return;
            }

            FinalizeAnnotationForGeometryChange("智能抠图");

            if (!TryBeginMatting())
            {
                StickerToastWindow.ShowAtTop("正在抠图中，请稍候…");
                return;
            }

            UpdateContextMenuState();

            string typeName = actualType == ScreenCaptureTool.Core.Matting.MattingModelType.Portrait ? "人像抠图" : "通用抠图";
            ShowTranslatingOverlay($"{typeName}中…");

            // 首次抠图前备份原图与当前卡片效果（供恢复原图撤销使用）；仅剪贴板模式不替换贴图内容，无需备份
            if (replaceSticker && _originalUncutBitmap == null)
            {
                try
                {
                    _originalUncutBitmap = new System.Drawing.Bitmap(_bitmap);
                    _originalBakedCardGeometry = _bakedCardGeometry;
                    _originalHasBakedShadow = _hasBakedShadow;
                    _originalBakedShadowPadPx = _bakedShadowPadPx;
                    _originalHideAlphaCheckerboard = _hideAlphaCheckerboard;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"备份未抠图原图失败: {ex.Message}");
                }
            }

            System.Drawing.Bitmap inputClone = new System.Drawing.Bitmap(_bitmap);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await ScreenCaptureTool.Core.Matting.MattingService.Instance
                        .CutoutAsync(inputClone, actualType)
                        .ConfigureAwait(false);

                    bool closeAfterClipboardOnly = false;
                    Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            HideTranslatingOverlay();
                            if (result.Success && result.ResultBitmap != null)
                            {
                                // 「贴图 / 剪贴板并贴图」：结果位图所有权交给贴图；「仅剪贴板」贴图保持原图
                                if (replaceSticker)
                                {
                                    ReplaceImage(result.ResultBitmap, isCutout: true);
                                }
                                if (writeToClipboard)
                                {
                                    try
                                    {
                                        // 已交给贴图显示的结果可随时被关闭或换图，复制须持独立快照。
                                        using var clipboardSnapshot = replaceSticker ? ClipboardService.DetachPixels(result.ResultBitmap) : null;
                                        if (await ClipboardService.SetImageAsync(clipboardSnapshot ?? result.ResultBitmap, preserveAlpha: true))
                                            StickerToastWindow.ShowAtTop($"{typeName}完成，已复制到剪贴板");
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.Warn("抠图结果写入剪贴板异常: " + ex.Message);
                                        StickerToastWindow.ShowAtTop($"{typeName}完成");
                                    }
                                }
                                else
                                {
                                    StickerToastWindow.ShowAtTop($"{typeName}完成");
                                }
                                if (!replaceSticker)
                                {
                                    // 仅剪贴板：结果位图无人接管，写入完成后即刻处置防泄漏
                                    try { result.ResultBitmap.Dispose(); } catch { }
                                    closeAfterClipboardOnly = closeWhenClipboardOnly;
                                }
                            }
                            else
                            {
                                string msg = !string.IsNullOrEmpty(result.ErrorMessage)
                                    ? result.ErrorMessage
                                    : $"{typeName}失败";
                                StickerToastWindow.ShowAtTop(msg);
                            }
                        }
                        finally
                        {
                            EndMatting();
                            UpdateContextMenuState();
                        }
                        // 临时贴图（截图工具栏/脚本流程创建）且结果仅入剪贴板：状态复位后再关闭，桌面不留窗
                        if (closeAfterClipboardOnly) Close();
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"智能抠图执行异常 [{type}]", ex);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            HideTranslatingOverlay();
                            StickerToastWindow.ShowAtTop($"{typeName}异常: {ex.Message}");
                        }
                        finally
                        {
                            EndMatting();
                            UpdateContextMenuState();
                        }
                    });
                }
                finally
                {
                    try { inputClone.Dispose(); } catch { }
                }
            });
        }
        catch (Exception ex)
        {
            EndMatting();
            HideTranslatingOverlay();
            UpdateContextMenuState();
            AppLogger.Error("StickerWindow.ExecuteMatting 异常", ex);
        }
    }

    private void OnRestoreOriginalImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_originalUncutBitmap == null)
            {
                StickerToastWindow.ShowAtTop("未找到可恢复的原图");
                return;
            }

            FinalizeAnnotationForGeometryChange("恢复原图");

            var restored = new System.Drawing.Bitmap(_originalUncutBitmap);
            var savedBakedCard = _originalBakedCardGeometry;
            var savedHasBakedShadow = _originalHasBakedShadow;
            var savedHideCheckerboard = _originalHideAlphaCheckerboard;
            try
            {
                _originalUncutBitmap.Dispose();
            }
            catch { }
            _originalUncutBitmap = null;

            ReplaceImage(restored, isCutout: false, restoredBakedCard: savedBakedCard, restoredHasBakedShadow: savedHasBakedShadow, restoredHideCheckerboard: savedHideCheckerboard);
            UpdateContextMenuState();
            StickerToastWindow.ShowAtTop("已恢复原图");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow.OnRestoreOriginalImageClick 异常", ex);
            StickerToastWindow.ShowAtTop("恢复原图失败");
        }
    }

    /// <summary>
    /// 显示「翻译中」进行态提示。用于「先贴原图、后台翻译、出结果再替换」的流程。
    ///
    /// 该层与图片严格同尺寸同定位（Left/Top + 代码同步几何，禁用 Stretch，见
    /// protection.md），只承载居中徽章，**不压暗底图**——等待期间用户
    /// 仍可看清原文并与译文对照；且 IsHitTestVisible=False 不拦鼠标，拖动、滚轮缩放、
    /// 右键菜单照常可用。图片翻译由产商一次完成，故只有一段「翻译中…」文案。
    /// </summary>
    public void ShowTranslatingOverlay(string? message = null)
    {
        if (TranslatingOverlay == null) return;
        if (TranslatingText != null && !string.IsNullOrEmpty(message))
            TranslatingText.Text = message;
        SetExplicitBoundsIfChanged(TranslatingOverlay, StickerImage.Margin, StickerImage.Width, StickerImage.Height);
        TranslatingOverlay.IsVisible = true;
    }

    /// <summary>隐藏「翻译中」遮罩（无论成功或失败都应调用，避免遮罩长留）。</summary>
    public void HideTranslatingOverlay()
    {
        if (TranslatingOverlay != null) TranslatingOverlay.IsVisible = false;
    }

    /// <summary>
    /// 用新位图替换当前贴图图片（图片到图片翻译后原地替换）。成品图尺寸可能小于原图
    /// （门面为压进产商体积上限会降采样），故像素宽高一律以新位图为准重算；
    /// 窗口位置、缩放比例与滚轮信封态簿记全部保持不变——几何交回 <see cref="ApplyScale"/>
    /// 的覆写态分支重钉，此处禁止直接改写窗口尺寸。
    /// </summary>
    public void ReplaceImage(
        System.Drawing.Bitmap newBitmap,
        bool isCutout = false,
        SelectionEffects.CardGeometry? restoredBakedCard = null,
        bool? restoredHasBakedShadow = null,
        bool? restoredHideCheckerboard = null,
        int? restoredShadowPadPx = null)
    {
        BringStickerToFront();
        if (!_canvasFlattening) DisposeCanvasPictures();
        // 原地换图＝整幅像素易主：先定稿收起图层，否则标注层仍按旧图的尺寸与坐标系显形。
        FinalizeAnnotationForGeometryChange("原地换图");
        // 翻译 / 抠图 / 恢复原图换进来的是一张新图：图像处理的原图基线随之作废（图像处理自己换图时跳过）
        ResetImageAdjustBaselineForExternalReplace();
        // 替换图源不重置 _scale，也不动窗口位置 —— 用户在等待期间做的
        // 缩放与移动因此得以保留（ApplyScale 会按当前 _scale 重算布局）。
        HideTranslatingOverlay();

        // 真正换图才作废字级笔迹；退出画布只是合成同一画面，字框和标记仍对应新图像素。
        if (!_canvasFlattening)
        {
            DropWordMarksForNewPicture();
            _wordRegions = null;
            _textContent = null;
        }
        ClearWordOverlay();
        _wordContentVersion++;
        _selectedWordIndices.Clear();

        // 释放旧资源
        var oldSource = _source;
        var oldBitmap = _bitmap;
        int oldPxW = _sourcePixelWidth, oldPxH = _sourcePixelHeight;

        // 旧版在此处给"纯圆角卡片"单开一条重烘半径的通路（2026-09-29），只认得圆角、认不出描边与
        // 阴影带，还要求译文与原图逐像素同尺寸。2026-10-01 起这件事由翻译流水线上的
        // SelectionEffects.WrapContentIntoShell 统一做（把译文套回原图那张壳，见 OnTranslateClick），
        // 三种外缘一并保住、也不再要求同尺寸，故这里不再保留第二套几何算式。

        // 设置新图（直接引用，避免全帧拷贝）
        _bitmap = newBitmap;
        _source = ToAvaloniaBitmap(_bitmap);
        _sourcePixelWidth = _bitmap.Width;
        _sourcePixelHeight = _bitmap.Height;

        // 刷新显示
        StickerImage.Source = _source;
        // 标注期的干净底图指向即将释放的旧 _bitmap，且尺寸随译文变了 → 一并作废
        ReleaseAnnotationSourceCache();
        // 换图＝换一张贴图：上一轮笔迹已随定稿烤进旧像素，会话历史与撤销链对新图毫无意义，
        // 留着会让重新进入标注时旧笔迹在译文上重影（＝"一笔变两笔"同型故障）。
        if (_annotationSession != null)
        {
            if (StickerAnnotationLayer != null) StickerAnnotationLayer.Document = null;
            _annotationSession.Dispose();
            _annotationSession = null;
        }
        // 文本层是活可视树，不随会话一起没：不清掉就会在译文上继续浮着，并在下次退出标注时被重烘进像素
        TextTool.ClearAllTexts();

        _isCutout = isCutout;
        if (isCutout)
        {
            // 抠图换图：重置原图卡片几何与烘焙阴影，开启透明棋盘格底板（与复制透明图贴图一致）
            _bakedCardGeometry = default;
            _hasBakedShadow = false;
            _bakedShadowPadPx = 0;
            _hideAlphaCheckerboard = false;
        }
        else if (restoredBakedCard.HasValue)
        {
            // 换图后外缘声明仍是原来那套（恢复原图／译文已套回原图外壳／旋转翻转）
            _bakedCardGeometry = restoredBakedCard.Value;
            _hasBakedShadow = restoredHasBakedShadow ?? false;
            _bakedShadowPadPx = _hasBakedShadow
                ? (restoredShadowPadPx ?? (_originalBakedShadowPadPx > 0
                    ? _originalBakedShadowPadPx
                    : SelectionEffects.ShadowMarginPx))
                : 0;
            _hideAlphaCheckerboard = restoredHideCheckerboard ?? true;
        }
        else
        {
            // 换图＝换一张**新渲染的直角不透明图**（如翻译）：上一轮的"外缘属于烘焙效果"三个声明一并作废。
            // 沿用圆角半径会让窗体阴影带着圆角坐在方图后面；沿用"自带阴影带"判定则图片背后继续
            // 垫棋盘格，观感就是翻译完之后贴图背后透出灰白格子（与 2026-09-06 修的同一条坑）。
            _bakedCardGeometry = default;
            _hasBakedShadow = false;
            _bakedShadowPadPx = 0;
            _hideAlphaCheckerboard = false;
        }

        // 🔴 **四个分支一律不碰 `_captureShadowIntent`**（2026-10-01，issue #50 的核心）：
        // 上面归零的三项都是"这张**位图**的外缘长什么样"——换了图它们当然失效；而意图位记的是
        // "**用户**认为这张贴图需不需要边界"，换图不改变这件事。恰恰相反：勾了阴影截下来的图
        // 翻译成一张不透明白底图之后**更需要**边界（原话「翻译没阴影有点看不清」）。
        // ⚠ 谁要在这儿顺手把它一起归零，就把本 issue 原样复现回来了。

        ApplyScale();
        // 三个声明归零后必须重算一次窗体阴影与背衬：ApplyScale 不碰阴影，而旧图的卡片几何
        // 不清掉的话，翻译完的贴图窗体阴影会带着旧圆角/旧内缩坐在方图后面。放在 ApplyScale
        // 之后以便半径读到新尺寸。
        // 同一句还负责把图片背后那一层按**新图**重算（棋盘格改判新图：
        // 译文图恒为 32bppArgb 但内容不透明，沿用旧图判定会让灰白格子透出来）——
        // 见 ApplyShadow 开头的 UpdateAlphaBackingLayer，探测缓存按位图实例作键，换新图必重算。
        ApplyShadow();

        if (isCutout)
        {
            if (ImageBackdrop != null)
            {
                ImageBackdrop.IsVisible = false;
                ImageBackdrop.Background = Avalonia.Media.Brushes.Transparent;
            }
            RootBorder.Background = Avalonia.Media.Brushes.Transparent;
        }
        else if (restoredBakedCard.HasValue)
        {
            if (restoredBakedCard.Value.ContentRadiusPx > 0 || restoredBakedCard.Value.StrokeWidthPx > 0 || restoredHasBakedShadow.GetValueOrDefault())
            {
                if (ImageBackdrop != null)
                {
                    ImageBackdrop.IsVisible = false;
                    ImageBackdrop.Background = Avalonia.Media.Brushes.Transparent;
                }
                RootBorder.Background = Avalonia.Media.Brushes.Transparent;
            }
            else
            {
                SetNativeBackground();
            }
        }
        else
        {
            SetNativeBackground();
        }

        // 翻译流程的预览贴图创建时刻意没入历史（那张原图马上就被译文顶掉），译文落地才是
        // 用户最终看到的内容，故在此补录。已有外键的贴图（正常换图）不受影响。
        if (HistoryId == null) ScheduleHistoryRecord();

        // 诊断：换图前后的几何（覆写态 + Margin + 窗口物理矩形）。用于实证核对
        // "译文落地后图片是否仍完整落在窗口内"（缺角＝Margin 未随窗口归位）
        IntPtr dbgHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        string dbgRect = dbgHwnd != IntPtr.Zero &&
                         ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(dbgHwnd, out var dbgRc)
            ? $"{dbgRc.Width}x{dbgRc.Height}" : "?";
        WheelDiag($"原地换图 源图 {oldPxW}x{oldPxH}→{_sourcePixelWidth}x{_sourcePixelHeight} " +
                  $"覆写态={_wheelVisualOverride} 图片={StickerImage.Width:F1}x{StickerImage.Height:F1} " +
                  $"Margin=({StickerImage.Margin.Left:F1},{StickerImage.Margin.Top:F1}) 窗口={dbgRect}");

        // 延迟释放旧资源（Avalonia 可能仍在渲染旧帧）
        Dispatcher.UIThread.Post(() =>
        {
            try { oldSource?.Dispose(); } catch { }
            try { oldBitmap?.Dispose(); } catch { }
        }, DispatcherPriority.Background);
    }

    private async void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // preserveAlpha 判据与截图写剪贴板同一口径：图里真有透明/半透明像素（烘焙阴影带、
            // 圆角、外来透明 PNG）就走 CF_DIB+DIBV5+PNG 三格式保 alpha，接收方才看得到那圈
            // 真实半透明阴影；全不透明图维持历史单 CF_DIB 路径（逐字节一致）。
            using (var annotated = AcquireAnnotationCompositeForExport())
            {
                if (annotated != null)
                {
                    // 划词装饰（荧光/虚线/波浪）补烘：标注成品是会话新产出的位图，可就地画
                    BakeImageSelectionMarks(annotated);
                    if (!await ClipboardService.SetImageAsync(annotated, preserveAlpha: HasTransparentPixels(annotated))) return;
                    AppLogger.Info("StickerWindow: 带有标注的贴图已复制到剪贴板");
                    StickerToastWindow.ShowAtTop("已复制图片到剪贴板");
                    return;
                }
            }

            if (_bitmap != null && !StickerColorSwatch.IsVisible)
            {
                // ⚠ `_bitmap` 是贴图正在显示的那张原图，**绝不能就地画**（会把装饰烤进贴图本体，
                // 且再复制一次就叠第二遍）。有装饰才克隆一份来烘，没有则维持原路径逐字节不变。
                if (HasImageSelectionMarks())
                {
                    using var withMarks = new System.Drawing.Bitmap(_bitmap);
                    BakeImageSelectionMarks(withMarks);
                    if (!await ClipboardService.SetImageAsync(withMarks, preserveAlpha: HasTransparentPixels(withMarks))) return;
                }
                else
                {
                    bool preserveAlpha = HasTransparentPixels(_bitmap);
                    // 贴图仍可继续编辑／换图／关闭；仅这条共享原图分支需要独立快照。
                    using var snapshot = ClipboardService.DetachPixels(_bitmap);
                    if (!await ClipboardService.SetImageAsync(snapshot, preserveAlpha)) return;
                }
            }
            else
            {
                // 文本/Markdown/色板：那几笔装饰画在 Viewbox 内的 TextDecorLayer 上，
                // 整棵树 render 时本就一起进图，无需补烘。
                using var snap = SnapshotTextSticker()
                    ?? throw new InvalidOperationException("文本贴图快照不可用");
                if (!await ClipboardService.SetImageAsync(snap)) return;
            }
            AppLogger.Info("StickerWindow: 贴图已复制到剪贴板");
            // 反馈提示（2026-10-01 补，issue #51）：此前本路径只写日志，用户按了 Ctrl+C 没有任何
            // 界面回应。文案与 HistoryImageViewerWindow / VideoStickerWindow 的复制提示同族。
            StickerToastWindow.ShowAtTop("已复制图片到剪贴板");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow: 复制贴图失败", ex);
            // 失败同样要出声：静默失败会让用户以为复制成功、粘出旧内容才发现（同族口径见录屏贴图）
            StickerToastWindow.ShowAtTop("复制图片失败");
        }
    }

    private async void OnSaveImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为贴图",
                SuggestedFileName = "Sticker_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("Bitmap Image") { Patterns = new[] { "*.bmp" } },
                },
            });

            string? path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                using (var annotated = AcquireAnnotationCompositeForExport())
                {
                    if (annotated != null)
                    {
                        // 划词装饰补烘：与复制同一口径（两条导出路不许一条有、一条没有）
                        BakeImageSelectionMarks(annotated);
                        ImageExportService.Save(annotated, path);
                        AppLogger.Info("StickerWindow: 带有标注的贴图已另存为 " + path);
                        return;
                    }
                }

                if (_bitmap != null && !StickerColorSwatch.IsVisible)
                {
                    // ⚠ 同复制那条：`_bitmap` 不可就地画，有装饰才克隆一份烘
                    if (HasImageSelectionMarks())
                    {
                        using var withMarks = new System.Drawing.Bitmap(_bitmap);
                        BakeImageSelectionMarks(withMarks);
                        ImageExportService.Save(withMarks, path);
                    }
                    else
                    {
                        ImageExportService.Save(_bitmap, path);
                    }
                }
                else
                {
                    using var snap = SnapshotTextSticker()
                        ?? throw new InvalidOperationException("文本贴图快照不可用");
                    ImageExportService.Save(snap, path);
                }
                AppLogger.Info("StickerWindow: 贴图已另存为 " + path);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow: 另存贴图失败", ex);
        }
    }

    // =========================================================================
    // Ctrl + 左键拖拽贴图导出临时图片文件 (OLE Drag & Drop)
    // =========================================================================

    /// <summary>
    /// 准备并导出贴图当前状态的高保真临时 PNG 文件（用于 OLE 拖放）。
    /// </summary>
    private string? CreateTemporaryExportFile()
    {
        try
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yuSnip", "DragDrop");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            // 清理超过 24 小时的旧拖放临时文件，避免长期运行磁盘堆积
            CleanOldDragDropTempFiles(tempDir);

            string fileName = $"Sticker_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            string filePath = System.IO.Path.Combine(tempDir, fileName);

            // 1. 若有标注内容，导出包含标注图元的合成图像
            using (var annotated = AcquireAnnotationCompositeForExport())
            {
                if (annotated != null)
                {
                    BakeImageSelectionMarks(annotated);
                    ImageExportService.Save(annotated, filePath);
                    return filePath;
                }
            }

            // 2. 若为常规图像贴图且非纯色板，导出 1:1 原始高清图
            if (_bitmap != null && !StickerColorSwatch.IsVisible)
            {
                // Ctrl 合并／文件拖出都携带划词笔迹，沿用复制和保存的副本烘焙，不改显示底图。
                if (HasImageSelectionMarks())
                {
                    using var withMarks = new System.Drawing.Bitmap(_bitmap);
                    BakeImageSelectionMarks(withMarks);
                    ImageExportService.Save(withMarks, filePath);
                }
                else ImageExportService.Save(_bitmap, filePath);
                return filePath;
            }

            // 3. 文本/Markdown/纯色板贴图：导出当前高清快照
            using var snap = SnapshotTextSticker();
            if (snap != null)
            {
                ImageExportService.Save(snap, filePath);
                return filePath;
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow: 创建拖放临时图片文件失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 清理超过 24 小时的旧拖放临时图片文件。
    /// </summary>
    private static void CleanOldDragDropTempFiles(string tempDir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(tempDir);
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var file in dirInfo.EnumerateFiles("Sticker_*.png"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // 静默忽略清理异常，不阻塞拖放主流程
        }
    }

    /// <summary>
    /// 触发贴图文件 OLE 拖放（Ctrl + 鼠标左键拖拽）。
    /// </summary>
    private async void StartCtrlDragDropAsync(PointerEventArgs e)
    {
        if (_isPerformingDragDrop) return;
        _isPerformingDragDrop = true;
        StickerDragAdorner? adorner = null;

        try
        {
            string? tempFilePath = CreateTemporaryExportFile();
            if (string.IsNullOrEmpty(tempFilePath) || !File.Exists(tempFilePath))
            {
                AppLogger.Warn("StickerWindow: 拖拽导出文件未能成功创建。");
                return;
            }

            var dataObject = new Avalonia.Input.DataObject();
            // 设置 Windows 标准 CF_HDROP 文件格式（由 Avalonia.Win32 映射为原生 DROPFILES）
            dataObject.Set(Avalonia.Input.DataFormats.FileNames, new[] { tempFilePath });
            _canvasDragSource = this;
            _canvasDragFile = tempFilePath;
            _canvasDropAccepted = false;

            // 兼容现代 Avalonia / UWP 存储项格式
            try
            {
                var storageFile = await StorageProvider.TryGetFileFromPathAsync(tempFilePath);
                if (storageFile != null)
                {
                    dataObject.Set(Avalonia.Input.DataFormats.Files, new[] { storageFile });
                }
            }
            catch
            {
                // 忽略非关键存储包装失败，DataFormats.FileNames 已满足所有 Windows 目标
            }

            // 启动跟随光标的半透明精致缩略图卡片（Win32 原生分层窗口，鼠标穿透 + 独立跟随线程）
            try
            {
                adorner = StickerDragAdorner.TryCreate(tempFilePath, SafeScaling);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"StickerWindow: 贴图拖拽缩略图创建失败: {ex.Message}");
            }

            AppLogger.Info($"StickerWindow: 启动 Ctrl 拖拽贴图文件: {tempFilePath}");
            var effect = await Avalonia.Input.DragDrop.DoDragDrop(e, dataObject, Avalonia.Input.DragDropEffects.Copy);
            AppLogger.Info($"StickerWindow: 贴图文件拖放完成，执行效果: {effect}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow: 贴图拖放执行异常", ex);
        }
        finally
        {
            adorner?.Dispose();
            if (_canvasDragSource == this) { _canvasDragSource = null; _canvasDragFile = null; }
            _isPerformingDragDrop = false;
            _isCtrlDragPending = false;
            _isDragging = false;
            if (!_isResizing)
            {
                Cursor = null;
            }
            if (_canvasDropAccepted) { _canvasDropAccepted = false; Close(); }
        }
    }

    private void OnSelectableTextClick(object? sender, RoutedEventArgs e)
    {
        if (!_isTextSelectable) FinalizeAnnotationForGeometryChange("文本可选择");
        SetTextSelectable(!_isTextSelectable);
    }

    // ================================================================
    //  Alt 快速选词（临时模式，不改变右键菜单"文本可选择"状态）
    // ================================================================

    /// <summary>
    /// Alt+左键按下：立即开始（或发起异步 OCR 后开始）字级拖选。
    /// 已有 OCR 缓存时零等待直接锚定；首次识别期间用户可继续拖动，
    /// 识别完成后以按下点为锚点、当前指针位置为活动点补建选区。
    /// </summary>
    private void BeginAltQuickSelect(PointerPressedEventArgs e)
    {
        _altQuickSelect = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        // 预热已完成但未识别到文本（空缓存）：直接提示，不再重复识别
        if (_wordRegions != null && _wordRegions.Count == 0)
        {
            StickerToastWindow.ShowAtTop("未识别到文本");
            EndAltQuickSelect();
            return;
        }

        var canvasPos = e.GetPosition(StickerCanvas);
        _altPressCanvasPos = canvasPos;
        _altLastMoveCanvasPos = canvasPos;

        // 已有 OCR 结果：直接锚定选词
        if (_wordRegions != null && _wordRegions.Count > 0)
        {
            EnterAltWordSelectVisual();
            // Alt+双击＝选中指针所在整行（2026-10-01 定稿的三层交互：单击/拖选按字、双击按行）。
            // 只在 Alt 态给：不带 Alt 的双击照旧走设置的"双击动作"（默认关闭贴图）；
            // 持久"文本可选择"态刻意不给（误触概率高），上面的 Alt 分支 return 得赶在双击判定之前。
            if (e.ClickCount >= 2 && SelectWholeLineAt(canvasPos)) return;
            TryAnchorAltSelectionAt(canvasPos);
            return;
        }

        // 首次：异步 OCR。悬置 _isWordSelecting，让拖动事件路由到选词分支
        StickerToastWindow.ShowAtTop("识别文本...");
        _isWordSelecting = true;
        _dragStartWordIndex = -1;
        _selectedWordIndices.Clear();
        ApplyWordSelection();

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                int version = _wordContentVersion;
                var lines = await RecognizeWordRegionsAsync();
                if (_isClosed || version != _wordContentVersion) return;
                // 空结果也缓存：下次 Alt 按下直接提示"未识别到文本"，不重复识别
                _wordRegions = lines ?? new List<OcrWordRegion>();
                if (lines == null || lines.Count == 0)
                {
                    StickerToastWindow.ShowAtTop("未识别到文本");
                    EndAltQuickSelect();
                    return;
                }

                UsageStatistics.RecordOcr(); // 统计：Alt 快速选词首次识别成功一次
                _textContent = BuildTextFromWordRegions(_wordRegions);
                _selectedWordIndices.Clear();
                EnterAltWordSelectVisual();

                if (_isWordSelecting && _altQuickSelect)
                {
                    // 识别期间用户仍在拖拽：以按下点为锚点，向最近指针位置补建选区
                    if (TryAnchorAltSelectionAt(_altPressCanvasPos))
                    {
                        UpdateSelectionTo(_altLastMoveCanvasPos);
                    }
                }
                else
                {
                    // 已松开（Alt+单击）：保留缓存，收尾等待下次 Alt 拖选
                    StickerToastWindow.ShowAtTop("已识别文本，按住 Alt 拖选");
                    EndAltQuickSelect();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("StickerWindow: Alt 快速选词 OCR 失败", ex);
                StickerToastWindow.ShowAtTop("OCR 识别失败");
                EndAltQuickSelect();
            }
        });
    }

    /// <summary>构建字级命中覆盖层并让 Canvas 参与交互（Alt 临时模式专用）。</summary>
    private void EnterAltWordSelectVisual()
    {
        BuildWordOverlay();
        StickerCanvas.IsVisible = true;
        StickerCanvas.IsHitTestVisible = true;
    }

    /// <summary>在指定 Canvas 坐标锚定 Alt 选词起点；命中文字或能边界锚定返回 true。</summary>
    private bool TryAnchorAltSelectionAt(Avalonia.Point canvasPos)
    {
        // 命中判据只有行带认领这一条（含屏幕 DIP 容差），不再叠第二道源图像素阈值
        int wordIdx = FindWordIndexAt(canvasPos);
        if (wordIdx >= 0)
        {
            _selectedWordIndices.Clear();
            _dragStartWordIndex = wordIdx;
            _isWordSelecting = true;
            _blankAnchorPending = false;
            UpdateSelectionTo(canvasPos);
            return true;
        }

        // 起笔在空白：改走边界锚定，拖选照样起手（2026-10-02 用户实报"从空白处按住左键拖动没办法选文本"）。
        // 首帧不落选区——只点不动仍等于点空白。
        if (TryBeginBlankAnchorAt(canvasPos)) return true;

        // 未命中文字：清空选中，结束拖选（不做窗口拖动，避免 Alt 语义混淆）
        _isWordSelecting = false;
        _dragStartWordIndex = -1;
        if (_selectedWordIndices.Count > 0)
        {
            _selectedWordIndices.Clear();
            ApplyWordSelection();
        }
        return false;
    }

    /// <summary>把空白起笔挂成"待兑现的边界锚点"：只设锚点与拖选态，不产生选区（移动一次才兑现）。</summary>
    private bool TryBeginBlankAnchorAt(Avalonia.Point canvasPos)
    {
        int idx = FindBlankAnchorIndexAt(canvasPos);
        if (idx < 0) return false;

        if (_selectedWordIndices.Count > 0)
        {
            _selectedWordIndices.Clear();
            ApplyWordSelection();
        }
        _dragStartWordIndex = idx;
        _isWordSelecting = true;
        _blankAnchorPending = true;
        _blankAnchorPressPos = canvasPos;
        return true;
    }

    /// <summary>
    /// 结束 Alt 快速选词：清除选区与覆盖层，恢复正常贴图交互。
    /// _wordRegions 保留缓存，下次 Alt 拖选零等待。
    /// </summary>
    private void EndAltQuickSelect()
    {
        _altQuickSelect = false;
        _isWordSelecting = false;
        _blankAnchorPending = false;
        _dragStartWordIndex = -1;
        if (_selectedWordIndices.Count > 0)
        {
            _selectedWordIndices.Clear();
            ApplyWordSelection();
        }
        ClearWordOverlay();
        // 原来是无条件收起整层：Alt 一松手，刚给那几行字标的荧光/虚线/波浪线跟着命中矩形一起消失。
        // 有笔迹时留住这一层（命中测试仍关），只清掉透明命中框。
        RetainWordMarkLayer();
        Cursor = null; // 恢复默认箭头（按住 Alt 移动指针会再次切回 I-beam）
    }

    /// <summary>
    /// 开始 Alt 临时选字（文本/Markdown 贴图）：开启文本控件命中测试并聚焦，
    /// 后续按下/拖选走原生选择。带按下事件时（命中测试尚未开启、原生选择拿不到
    /// 本次按下）对文本贴图改用 TextLayout 命中测试手动驱动选区。
    /// </summary>
    private void BeginAltTextStickerSelect(PointerPressedEventArgs? e)
    {
        _altTextStickerSelect = true;

        if (StickerTextBlock != null && StickerTextBlock.IsVisible)
        {
            StickerTextBlock.IsHitTestVisible = true;
        }
        if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible)
        {
            StickerMarkdownViewer.IsHitTestVisible = true;
        }

        if (e == null) return; // 悬停开启：无按下事件，等原生按下接管

        // 按下瞬间命中测试刚开启，本次按下事件不会路由进文本控件——
        // 文本贴图用 TextLayout 手动锚定选区起点，拖动时在移动事件中驱动选区。
        if (StickerTextBlock != null && StickerTextBlock.IsVisible)
        {
            StickerTextBlock.Focus();
            if (StickerTextBlock.TextLayout != null)
            {
                var hit = StickerTextBlock.TextLayout.HitTestPoint(e.GetPosition(StickerTextBlock));
                if (hit.IsInside)
                {
                    _altManualTextSelect = true;
                    _altManualAnchorIndex = hit.TextPosition;
                    StickerTextBlock.SelectionStart = hit.TextPosition;
                    StickerTextBlock.SelectionEnd = hit.TextPosition;
                }
            }
        }
    }

    /// <summary>
    /// 结束 Alt 临时选字：关闭文本控件命中测试，恢复正常贴图交互。
    /// 选区保留（供 Ctrl+C 复制），与持久"文本可选择"模式互不干扰。
    /// </summary>
    private void EndAltTextStickerSelect()
    {
        _altTextStickerSelect = false;
        _altManualTextSelect = false;
        // 对齐图片贴图 EndAltQuickSelect 的收尾语义：临时选字结束时取消选区
        // （点击任意处触发），同步作废复制快照——选区已取消，Ctrl+C 不应再
        // 复制到不可见的旧选区。注意时序：Alt 拖选松开鼠标后选区仍保留
        // （供 Ctrl+C），直到下一次非 Alt 按下才走到这里取消，与图片贴图一致。
        ClearTextStickerSelection();
        if (!_isTextSelectable)
        {
            if (StickerTextBlock != null) StickerTextBlock.IsHitTestVisible = false;
            if (StickerMarkdownViewer != null) StickerMarkdownViewer.IsHitTestVisible = false;
            Cursor = null;
        }
    }

    /// <summary>清除文本/Markdown 贴图的选区并作废复制快照（两条链路统一的取消入口）。</summary>
    private void ClearTextStickerSelection()
    {
        _textSelSnapshot = null;
        // 划词浮条的选区快照与浮条本体一起作废：留着会让"清除选区之后点浮条按钮"复制到已不存在的文字
        _selSnapshot.Clear();
        HideSelectionToolbar();
        if (StickerTextBlock != null && StickerTextBlock.SelectionStart != StickerTextBlock.SelectionEnd)
        {
            StickerTextBlock.SelectionStart = 0;
            StickerTextBlock.SelectionEnd = 0;
        }
        StickerMarkdownViewer?.ClearSelection();
    }

    /// <summary>把字级 OCR 区域拼接为整段文本（按行分组、行内按 X 排序，与选中复制同一算法）。</summary>
    private static string BuildTextFromWordRegions(IReadOnlyList<OcrWordRegion> regions)
    {
        return string.Join(Environment.NewLine,
            regions.GroupBy(r => r.LineIndex)
                   .OrderBy(g => g.Key)
                   .Select(g => string.Concat(g.OrderBy(r => r.Bounds.Left).Select(r => r.Text))));
    }

    /// <summary>
    /// 静默预热贴图 OCR：打开贴图后即在后台识别并缓存字级区域，
    /// 不做任何提示、不改变交互状态。Alt 快速选词与"文本可选择"
    /// 开启时直接复用缓存，按下即可拖选、零等待。
    /// 仅图片贴图需要（文本/Markdown/颜色贴图各有原生选择机制）。
    /// </summary>
    private void PreWarmStickerOcr()
    {
        // 持久"文本可选择"模式已自行触发识别（含提示流程），无需预热
        if (_isTextSelectable) return;
        // 已有缓存（含"无文本"的空结果）或非图片贴图，直接跳过
        if (_wordRegions != null) return;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_isClosed || _bitmap == null) return;
                if (StickerImage == null || !StickerImage.IsVisible) return;

                int version = _wordContentVersion;
                var lines = await RecognizeWordRegionsAsync();

                // 期间窗口已关闭，或持久模式/Alt 选词已填充缓存：放弃本次结果
                if (_isClosed || version != _wordContentVersion || _wordRegions != null) return;
                _wordRegions = lines ?? new List<OcrWordRegion>();
                if (_wordRegions.Count > 0)
                {
                    _textContent = BuildTextFromWordRegions(_wordRegions);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StickerWindow: 贴图 OCR 预热失败: " + ex.Message);
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ================================================================
    //  Rectangle-based word selection (QQ/WeChat style)
    // ================================================================

    /// <summary>
    /// 在 Canvas 上为每个 OCR 单词创建透明的命中测试矩形。
    /// 选中时填充蓝色半透明背景。
    /// </summary>
    private void BuildWordOverlay()
    {
        ClearWordOverlay();
        if (_wordRegions == null || StickerCanvas == null) return;
        if (_isCanvasMode)
        {
            StickerCanvas.Margin = new Thickness(0);
            StickerCanvas.Width = _canvasView!.Width;
            StickerCanvas.Height = _canvasView.Height;
        }
        else if (StickerCanvas.Margin != StickerImage.Margin)
            StickerCanvas.Margin = StickerImage.Margin;

        double s = SafeScaling;
        _wordRectangles.Capacity = _wordRegions.Count;

        for (int i = 0; i < _wordRegions.Count; i++)
        {
            var word = _wordRegions[i];

            // OCR pixel → Canvas DIP
            double x = word.Bounds.X * WordDisplayScale / s;
            double y = word.Bounds.Y * WordDisplayScale / s;
            double w = word.Bounds.Width * WordDisplayScale / s;
            double h = word.Bounds.Height * WordDisplayScale / s;

            var rect = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Math.Max(1.0, w),
                Height = Math.Max(1.0, h),
                Fill = Avalonia.Media.Brushes.Transparent,
                Stroke = Avalonia.Media.Brushes.Transparent,
                StrokeThickness = 0,
                IsHitTestVisible = true,
                Tag = i  // store word index for hit-test
            };

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);

            StickerCanvas.Children.Add(rect);
            _wordRectangles.Add(rect);
        }

        // 覆盖层与行带同源同寿命：每次重建覆盖层就重算一次行带（认领半径是屏幕量、不烘进几何，
        // 故滚轮/拖边缩放不必跟着重建）
        RebuildWordLineBands();

	}

    private void ClearWordOverlay()
    {
        if (StickerCanvas != null)
        {
            StickerCanvas.Children.Clear();
        }
        _wordRectangles.Clear();
        _selectionOverlays.Clear();
        // 这一层是"清整层子元素"的语义，划词的几笔（荧光/虚线/波浪线）也住在同一层上：
        // 只在这里补一次重画，就自动覆盖 建覆盖层 / Alt 收尾 / 关掉文本可选择 / 缩放重建 全部通路
        // （分开补＝漏一条就笔迹消失）。
        ScheduleMarkRedraw();
    }

    /// <summary>
    /// 在 Canvas DIP 坐标中查找指针位置对应的那个字；不在文字区（<see cref="WordClaimRadiusDip"/> 之外）返回 -1。
    /// 悬停光标（<c>OnWindowPointerMoved</c> 里那句 <c>FindWordIndexAt(...) &gt;= 0</c>）与按下锚定共用这一条，
    /// 于是光标形状就是"这一下点下去能不能选"的真话。
    /// </summary>
    private int FindWordIndexAt(Avalonia.Point canvasPos)
    {
        CanvasToOcrPx(canvasPos, out float px, out float py);
        var band = ClaimWordLineAt(px, py);
        if (band == null) return -1;
        return FindCharIndexInBand(band.Value, px);
    }

    // ================================================================
    //  行带认领（区域锚定）
    // ================================================================

    /// <summary>
    /// 一行文字的认领区。几何一律存<b>源图像素</b>；唯一的屏幕量（<see cref="WordClaimRadiusDip"/>）
    /// 不烘进来，由 <see cref="ClaimWordLineAt"/> 每次按当时 <c>_scale</c> 现算夹取
    /// ⇒ 滚轮缩放／拖边缩放都不必重建行带。
    /// 🔴 <b>只有竖向有认领区，横向零容差</b>（2026-10-01「退」收口）：横向一扩张，行首行尾的空白、
    /// 两栏之间的空隙都会把隔壁那个字硬塞给用户＝"选多字"。横向准不准这件事交给字框本身。
    /// </summary>
    private struct WordLineBand
    {
        public int LineIndex;
        /// <summary>本行真实字形范围＝行内各字框的并集。横向认领就用这一圈，不外扩。</summary>
        public float RealLeft, RealTop, RealRight, RealBottom;
        /// <summary>竖向认领边（未扣容差）。<c>MinValue/MaxValue</c>＝该方向没有竞争邻行，交给容差决定。</summary>
        public float BandTop, BandBottom;
        /// <summary>行内字下标，按左缘升序＝本行的阅读顺序。</summary>
        public List<int>? Indices;
    }

    /// <summary>Canvas DIP → 源图像素（与 <see cref="BuildWordOverlay"/> 的正变换互为逆运算，改一处必改两处）。</summary>
    private void CanvasToOcrPx(Avalonia.Point canvasPos, out float px, out float py)
    {
        double k = WordDisplayScale > 0 ? SafeScaling / WordDisplayScale : SafeScaling;
        px = (float)(canvasPos.X * k);
        py = (float)(canvasPos.Y * k);
    }

    /// <summary>竖向余量换算成源图像素。贴图缩到 0.2 倍时这里会放大 5 倍，屏幕上的手感恒定不变。</summary>
    private float WordClaimCapPx()
    {
        double k = WordDisplayScale > 0 ? SafeScaling / WordDisplayScale : SafeScaling;
        return (float)(WordClaimRadiusDip * k);
    }

    /// <summary>两个行带在横向上是否抢同一片版面（用于竖向分带时只跟"同列"的邻行平分）。</summary>
    private static bool BandsOverlapX(in WordLineBand a, in WordLineBand b)
        => a.RealLeft < b.RealRight && b.RealLeft < a.RealRight;

    /// <summary>两个行带在竖向上是否同一横排（用于横向垫区时只跟"同排"的邻行平分空隙）。</summary>
    private static bool BandsOverlapY(in WordLineBand a, in WordLineBand b)
        => a.RealTop < b.RealBottom && b.RealTop < a.RealBottom;

    /// <summary>
    /// 由 <see cref="_wordRegions"/> 重建行带。两条规则：
    /// ① <b>竖向只在横向有重叠的邻行之间按中点切带</b> ⇒ 同一视觉行上的"大标题＋小字"两个框横向不重叠、
    ///    互不裁切，<b>大框保住自己的完整高度</b>；行缝与段落缝则由上下邻行平分，不再无主。
    /// ② 排出<b>阅读顺序号</b>：OCR 行框顺序＝检测器输出顺序（只有竖排版面才重排），
    ///    旧写法拿原始下标取 min/max 当区间，跨行拖选会选进无关行、漏掉真正那几行。
    /// 🔴 <b>横向不认领</b>（2026-10-01「退」）：曾按"垫半个字宽＋与同排邻行平分空隙"扩张过一圈，
    ///    实测后果是行末空白与两栏之间的空隙都会硬塞给用户一个隔壁字＝"选多字"。已整块删除。
    /// </summary>
    private void RebuildWordLineBands()
    {
        _wordLineBands.Clear();
        _wordBandsFrom = _wordRegions;
        _wordReadingSeq = Array.Empty<int>();
        _seqToWordIndex = Array.Empty<int>();
        var regions = _wordRegions;
        if (regions == null || regions.Count == 0) return;

        _wordReadingSeq = new int[regions.Count];
        Array.Fill(_wordReadingSeq, -1);
        _seqToWordIndex = new int[regions.Count];
        Array.Fill(_seqToWordIndex, -1);

        // ── 聚行：同一 LineIndex 的字并成一行，行内按左缘升序，同时量出真实字形范围 ──
        var byLine = new Dictionary<int, List<int>>();
        for (int i = 0; i < regions.Count; i++)
        {
            int lineIdx = regions[i].LineIndex;
            if (!byLine.TryGetValue(lineIdx, out var lst))
            {
                lst = new List<int>();
                byLine[lineIdx] = lst;
            }
            lst.Add(i);
        }

        foreach (var kv in byLine)
        {
            var indices = kv.Value;
            indices.Sort((a, b) => regions[a].Bounds.Left.CompareTo(regions[b].Bounds.Left));
            var band = new WordLineBand
            {
                LineIndex = kv.Key,
                Indices = indices,
                RealLeft = float.MaxValue,
                RealTop = float.MaxValue,
                RealRight = float.MinValue,
                RealBottom = float.MinValue
            };
            foreach (int idx in indices)
            {
                var b = regions[idx].Bounds;
                if (b.Left < band.RealLeft) band.RealLeft = b.Left;
                if (b.Top < band.RealTop) band.RealTop = b.Top;
                if (b.Right > band.RealRight) band.RealRight = b.Right;
                if (b.Bottom > band.RealBottom) band.RealBottom = b.Bottom;
            }
            // 横向不设认领边＝零容差（见 WordLineBand 的 🔴 注）；竖向先不设界：
            // 没有竞争邻行时该留多远由容差决定，不烘进几何里（见 ClaimWordLineAt）
            band.BandTop = float.MinValue;
            band.BandBottom = float.MaxValue;
            _wordLineBands.Add(band);
        }

        // ── ① 竖向平分：只跟横向有重叠的邻行分 ──
        for (int i = 0; i < _wordLineBands.Count; i++)
        {
            var self = _wordLineBands[i];
            float aboveBottom = float.MinValue;   // 上方最近邻行的下缘
            float belowTop = float.MaxValue;      // 下方最近邻行的上缘
            for (int j = 0; j < _wordLineBands.Count; j++)
            {
                if (j == i) continue;
                var other = _wordLineBands[j];
                if (!CanvasBandsSharePicture(self, other)) continue;
                if (!BandsOverlapX(in self, in other)) continue;
                if (other.RealBottom <= self.RealTop && other.RealBottom > aboveBottom) aboveBottom = other.RealBottom;
                if (other.RealTop >= self.RealBottom && other.RealTop < belowTop) belowTop = other.RealTop;
            }
            if (aboveBottom > float.MinValue) self.BandTop = self.RealTop - (self.RealTop - aboveBottom) * 0.5f;
            if (belowTop < float.MaxValue) self.BandBottom = self.RealBottom + (belowTop - self.RealBottom) * 0.5f;
            _wordLineBands[i] = self;
        }

        // ── ② 阅读顺序：竖向相交的行归为同一横排，排间按上缘、排内按左缘 ──
        var pending = new List<int>();
        for (int i = 0; i < _wordLineBands.Count; i++) pending.Add(i);
        var rows = new List<List<int>>();
        while (pending.Count > 0)
        {
            var row = new List<int> { pending[0] };
            pending.RemoveAt(0);
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int k = pending.Count - 1; k >= 0; k--)
                {
                    int cand = pending[k];
                    var candBand = _wordLineBands[cand];
                    for (int r = 0; r < row.Count; r++)
                    {
                        var rowBand = _wordLineBands[row[r]];
                        if (BandsOverlapY(in rowBand, in candBand))
                        {
                            row.Add(cand);
                            pending.RemoveAt(k);
                            grew = true;
                            break;
                        }
                    }
                }
            }
            rows.Add(row);
        }
        rows.Sort((a, b) => RowTopOf(a).CompareTo(RowTopOf(b)));

        int seq = 0;
        foreach (var row in rows)
        {
            row.Sort((a, b) => _wordLineBands[a].RealLeft.CompareTo(_wordLineBands[b].RealLeft));
            foreach (int bandIdx in row)
            {
                var indices = _wordLineBands[bandIdx].Indices;
                if (indices == null) continue;
                foreach (int wordIdx in indices)
                {
                    _wordReadingSeq[wordIdx] = seq;
                    _seqToWordIndex[seq] = wordIdx;
                    seq++;
                }
            }
        }

        AppLogger.Info($"StickerWindow: 行带重建 {_wordLineBands.Count} 行 / {seq} 字，" +
                       $"竖向余量 {WordClaimRadiusDip}DIP＝当前 {WordClaimCapPx():F1} 源像素（缩放 {_scale:F2}，横向零容差）");
    }

    /// <summary>某一横排的上缘（聚簇后按它排行序）。</summary>
    private float RowTopOf(List<int> bandIndices)
    {
        float top = float.MaxValue;
        foreach (int i in bandIndices)
            if (_wordLineBands[i].RealTop < top) top = _wordLineBands[i].RealTop;
        return top;
    }

    /// <summary>
    /// 这个点（源图像素）归哪一行 —— 按下、悬停光标、Alt 锚定三处共用的<b>唯一</b>认领出口。
    /// 旧写法悬停只看"找不找得到最近的字"（无阈值，整张贴图哪都是 I 形光标），按下却另设一道
    /// 源图像素阈值，两者必然打架 ⇒ 工单 #45 那句"光标能识别到文本区域，但点下去选不出"。
    /// ① <b>横向必须落在本行的真实范围内，零容差</b>——横向一外扩，行末空白与两栏之间的空隙
    ///    都会硬塞给用户一个隔壁字（2026-10-01「退」实测就是这个把"选多字"放大的）；
    /// ② 竖向给 <see cref="WordClaimRadiusDip"/> 的屏幕余量够最近那一行，并被邻行中点分带夹住
    ///    —— 这一条是"贴图缩到多小都不用瞄"的全部来源，<b>不能跟着横向一起删</b>；
    /// ③ 任一不满足 → null＝这里不是文字区，让给"点空白取消选中／拖动窗口"。
    /// 多行同时够得着时：竖向实框真框住这个点的压倒性优先，其余按认领带<b>窄</b>的优先。
    /// </summary>
    private WordLineBand? ClaimWordLineAt(float px, float py)
    {
        var regions = _wordRegions;
        if (regions == null || regions.Count == 0) return null;
        // 缓存换过（旋转／裁切／重识别／预热补上）却没走 BuildWordOverlay 的通路也在这里兜住
        if (!ReferenceEquals(_wordBandsFrom, regions)) RebuildWordLineBands();
        if (_wordLineBands.Count == 0) return null;

        float cap = WordClaimCapPx();

        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < _wordLineBands.Count; i++)
        {
            var b = _wordLineBands[i];
            if (!CanvasBandAt(b, px, py)) continue;
            if (px < b.RealLeft || px > b.RealRight) continue;   // 横向零容差：绝不越出自己的行宽段
            float t = b.BandTop == float.MinValue ? b.RealTop - cap : Math.Max(b.BandTop, b.RealTop - cap);
            float bot = b.BandBottom == float.MaxValue ? b.RealBottom + cap : Math.Min(b.BandBottom, b.RealBottom + cap);
            if (py < t || py > bot) continue;

            bool insideReal = py >= b.RealTop && py <= b.RealBottom;
            // 落在实框里的那一行压倒性优先；否则认领带越窄越优先（免得大行的边缘把小行盖掉）
            float score = (bot - t) * (insideReal ? 0.01f : 1f);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best >= 0 ? _wordLineBands[best] : null;
    }

    /// <summary>认领到行之后，这个横向位置落在行内第几个字：先取真实字框包含它的那个，再退到中心最近的那个。</summary>
    private int FindCharIndexInBand(WordLineBand band, float px)
    {
        var regions = _wordRegions;
        var indices = band.Indices;
        if (regions == null || indices == null || indices.Count == 0) return -1;

        int best = -1;
        float bestDist = float.MaxValue;
        foreach (int idx in indices)
        {
            var b = regions[idx].Bounds;
            if (px >= b.Left && px <= b.Right) return idx;
            float dist = px < b.Left ? b.Left - px : px - b.Right;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = idx;
            }
        }
        return best;
    }

    /// <summary>
    /// 按下落在空白时给"拖选从哪儿起手"用的<b>边界锚定</b>：把起笔点归到最近那一行的行首／行尾／行内最近字
    /// （原生编辑器的同一条：在行首空白起笔＝插在行首，在两段之间起笔＝落到最近那一行的最近位置）。
    /// 🔴 与 <see cref="ClaimWordLineAt"/> 的分工必须看清：那条回答"<b>这一下点中了哪个字</b>"（横向零容差，
    /// 单击语义），本条只在它判为空时兜"<b>拖动从哪儿起手</b>"（拖选语义）。
    /// ⚠ 方向纪律：只允许"按下比悬停宽"，<b>禁止反向</b>——悬停比按下宽就是工单 #45 那句"光标认得出、点下去选不出"。
    /// ⚠ 本条<b>不</b>给单击结果：调用方拿到锚点后要等指针真的移动才落选区（见 <see cref="_blankAnchorPending"/>），
    ///    所以"点一下空白"照旧＝取消选中，不会凭空高亮一个字，也不会把两栏之间的空隙硬塞给隔壁字。
    /// 只在 <b>Alt</b> 态用：持久「文本可选择」态下空白处那一下仍归"取消选中＋拖动窗口"，那是它唯一的移窗手段。
    /// </summary>
    private int FindBlankAnchorIndexAt(Avalonia.Point canvasPos)
    {
        var regions = _wordRegions;
        if (regions == null || regions.Count == 0) return -1;
        if (!ReferenceEquals(_wordBandsFrom, regions)) RebuildWordLineBands();
        if (_wordLineBands.Count == 0) return -1;

        CanvasToOcrPx(canvasPos, out float px, out float py);
        float cap = WordClaimCapPx();

        int best = -1;
        float bestDy = float.MaxValue;
        for (int i = 0; i < _wordLineBands.Count; i++)
        {
            var b = _wordLineBands[i];
            if (!CanvasBandAt(b, px, py)) continue;
            if (b.Indices == null || b.Indices.Count == 0) continue;
            float dy = py < b.RealTop ? b.RealTop - py : (py > b.RealBottom ? py - b.RealBottom : 0f);
            // 够得着的门槛：在竖向容差内，或横向正落在这行的真实宽度里（＝显然给这一行，只是压在了行缝上）
            if (dy > cap && !(px >= b.RealLeft && px <= b.RealRight)) continue;
            if (dy < bestDy) { bestDy = dy; best = i; }
        }
        if (best < 0) return -1;

        var band = _wordLineBands[best];
        if (px < band.RealLeft) return band.Indices![0];        // 行首左侧的空白 → 本行第一个字
        if (px > band.RealRight) return band.Indices![^1];      // 行尾右侧的空白 → 本行最后一个字
        return FindCharIndexInBand(band, px);                   // 行缝／段间的空白 → 该行横向最近的那个字
    }

    /// <summary>
    /// Alt+双击：把指针所在<b>整行</b>的字选中（三层交互里的"双击按行"，只在 Alt 态给）。
    /// 返回 false＝这一下不在文字区，交回常规锚定。不带 Alt 的双击仍走设置的"双击动作"（默认关闭贴图），
    /// 持久"文本可选择"态刻意不给——用户判其误触概率高（2026-10-01 定稿）。
    /// </summary>
    private bool SelectWholeLineAt(Avalonia.Point canvasPos)
    {
        var regions = _wordRegions;
        if (regions == null || regions.Count == 0) return false;

        CanvasToOcrPx(canvasPos, out float px, out float py);
        var band = ClaimWordLineAt(px, py);
        var indices = band?.Indices;
        if (indices == null || indices.Count == 0) return false;

        _selectedWordIndices.Clear();
        foreach (int idx in indices) _selectedWordIndices.Add(idx);
        // 锚点取本行首字：双击后若继续拖动，就从本行向下游扩，而不是把刚选中的整行缩回一个点
        _dragStartWordIndex = indices[0];
        _isWordSelecting = true;
        ApplyWordSelection();
        AppLogger.Info($"StickerWindow: Alt+双击选中整行（行 {band!.Value.LineIndex}，{indices.Count} 字）");
        return true;
    }

	/// <summary>
	/// 真实文本选择：anchor 到 active 之间的所有字依次选中。
	/// 🔴 区间取在<b>阅读顺序号</b>上，不取 OCR 字下标——行框下标＝检测器的输出顺序（只有竖排才重排），
	/// 拿它取 min/max 会把无关行选进来、把真正那几行漏掉（工单 #45「选不出来」的另一半成因）。
	/// </summary>
	private void UpdateSelectionTo(Avalonia.Point canvasPos)
	{
		if (_wordRegions == null || _dragStartWordIndex < 0) return;

		// 空白起笔的那一下只挂锚点：指针没真挪过位置就不落选区（只点不动＝点空白＝取消选中）
		if (_blankAnchorPending)
		{
			if (canvasPos == _blankAnchorPressPos) return;
			_blankAnchorPending = false;
		}

		// 找到当前光标落在哪个字上
		int activeIdx = FindWordIndexAt(canvasPos);
		if (activeIdx < 0) return;

		int anchorSeq = SeqOfWord(_dragStartWordIndex);
		int activeSeq = SeqOfWord(activeIdx);
		if (anchorSeq < 0 || activeSeq < 0) return;

		int from = Math.Min(anchorSeq, activeSeq);
		int to = Math.Max(anchorSeq, activeSeq);

		var newSelected = new HashSet<int>();
		for (int s = from; s <= to; s++)
		{
			int wordIdx = WordIndexOfSeq(s);
			if (wordIdx >= 0) newSelected.Add(wordIdx);
		}
		if (newSelected.Count == 0) return;

		if (!_selectedWordIndices.SetEquals(newSelected))
		{
			_selectedWordIndices.Clear();
			_selectedWordIndices.UnionWith(newSelected);
		}

		ApplyWordSelection();
	}

    /// <summary>字下标 → 阅读顺序号；越界或失联返回 -1。</summary>
    private int SeqOfWord(int wordIndex)
        => wordIndex >= 0 && wordIndex < _wordReadingSeq.Length ? _wordReadingSeq[wordIndex] : -1;

    /// <summary>阅读顺序号 → 字下标；越界或失联返回 -1。</summary>
    private int WordIndexOfSeq(int seq)
        => seq >= 0 && seq < _seqToWordIndex.Length ? _seqToWordIndex[seq] : -1;

    /// <summary>
    /// 字级选框：同行相邻选中字合并成连续蓝条，行间自然断开。
    /// 选中永远落在完整字上，不可能停在字中间。
    /// </summary>
    private void ApplyWordSelection()
    {
        // 清除旧叠加层
        foreach (var r in _selectionOverlays)
            StickerCanvas.Children.Remove(r);
        _selectionOverlays.Clear();

        if (_selectedWordIndices.Count == 0 || _wordRegions == null) return;

        double s = SafeScaling;

        // 按 LineIndex 分组，行内按 X 排序
        var grouped = _selectedWordIndices
            .GroupBy(i => _wordRegions[i].LineIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(i => _wordRegions[i].Bounds.Left).ToList())
            .ToList();

        foreach (var lineWords in grouped)
        {
            // 同行相邻字（间距 ≤ 2px）合并为一个连续蓝条
            int runStart = 0;
            for (int j = 1; j <= lineWords.Count; j++)
            {
                bool endRun = (j == lineWords.Count);
                if (!endRun)
                {
                    var prev = _wordRegions[lineWords[j - 1]].Bounds;
                    var curr = _wordRegions[lineWords[j]].Bounds;
                    if (curr.Left - prev.Right > 2)
                        endRun = true;
                }

                if (endRun)
                {
                    float rLeft = float.MaxValue, rTop = float.MaxValue;
                    float rRight = float.MinValue, rBottom = float.MinValue;
                    for (int k = runStart; k < j; k++)
                    {
                        var b = _wordRegions[lineWords[k]].Bounds;
                        if (b.Left < rLeft) rLeft = b.Left;
                        if (b.Top < rTop) rTop = b.Top;
                        if (b.Right > rRight) rRight = b.Right;
                        if (b.Bottom > rBottom) rBottom = b.Bottom;
                    }

                    var overlay = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Width = Math.Max(1.0, (rRight - rLeft) * WordDisplayScale / s),
                        Height = Math.Max(1.0, (rBottom - rTop) * WordDisplayScale / s),
                        // 与文本/Markdown 贴图 SelectionBrush(#900078D7) 同色同透明度：
                        // 两条选词链路的选中高亮视觉完全一致，无割裂感
                        Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x90, 0x00, 0x78, 0xD7)),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(overlay, rLeft * WordDisplayScale / s);
                    Canvas.SetTop(overlay, rTop * WordDisplayScale / s);
                    StickerCanvas.Children.Add(overlay);
                    _selectionOverlays.Add(overlay);
                    runStart = j;
                }
            }
        }
    }

    /// <summary>
    /// 复制选中行的文本：按空间顺序排列，每行取完整文本。
    /// </summary>
    private void CopySelectedText()
    {
        if (_wordRegions != null && _selectedWordIndices.Count > 0)
        {
            // 按行分组，行内按 X 排序，拼接字文本
            var parts = _selectedWordIndices
                .GroupBy(i => _wordRegions[i].LineIndex)
                .OrderBy(g => g.Key)
                .Select(g => string.Concat(g.OrderBy(i => _wordRegions[i].Bounds.Left).Select(i => _wordRegions[i].Text)))
                .ToList();

            string result = string.Join(Environment.NewLine, parts);
            if (!string.IsNullOrWhiteSpace(result))
            {
                ClipboardService.SetText(result);
                StickerToastWindow.ShowAtTop("已复制 " + result.Length + " 个字符");
            }
            return;
        }

        if (StickerTextBlock != null && StickerTextBlock.IsVisible && !string.IsNullOrEmpty(StickerTextBlock.SelectedText))
        {
            ClipboardService.SetText(StickerTextBlock.SelectedText);
            StickerToastWindow.ShowAtTop("已复制 " + StickerTextBlock.SelectedText.Length + " 个字符");
            return;
        }

        if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible && !string.IsNullOrEmpty(StickerMarkdownViewer.SelectedText))
        {
            ClipboardService.SetText(StickerMarkdownViewer.SelectedText);
            StickerToastWindow.ShowAtTop("已复制 " + StickerMarkdownViewer.SelectedText.Length + " 个字符");
            return;
        }

        // 快照兜底：原生 SelectableTextBlock 失焦即 ClearSelection——右键菜单弹出
        // 会夺走焦点，此时实时选区可能已被清空，用释放鼠标时的快照完成复制。
        if (!string.IsNullOrEmpty(_textSelSnapshot))
        {
            ClipboardService.SetText(_textSelSnapshot);
            StickerToastWindow.ShowAtTop("已复制 " + _textSelSnapshot.Length + " 个字符");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // ==================== 贴图存档（关窗现场 → history/贴图存档/{历史Id}.json） ====================

    /// <summary><see cref="OnWindowClosing"/> 量到的窗口物理矩形。Closed 之后句柄已销毁、
    /// <c>Bounds</c> 已归零，位置与尺寸只能提前收。</summary>
    private Avalonia.PixelRect? _closingFrameRect;

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
                _closingFrameRect = new Avalonia.PixelRect(rc.Left, rc.Top, rc.Width, rc.Height);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 关窗前量窗口矩形失败，存档位置退回 Avalonia 值。" + ex.Message);
        }
    }

    /// <summary>贴图类型标记，决定恢复时能否再当文本 / Markdown 编辑。
    /// 判据取各层的可见性而不是 <c>_textContent</c>：图片贴图做过 Alt 选词后该字段同样非空。</summary>
    private string StickerKindTag
    {
        get
        {
            if (StickerColorSwatch != null && StickerColorSwatch.IsVisible) return "color";
            if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible) return "markdown";
            if (_layoutInfo != null) return "text";
            return "image";
        }
    }

    /// <summary>
    /// 将干净底图和修改后的标注合成位图刷新写回历史记录，确保存档与磁盘位图一致。
    /// </summary>
    public void FlushAnnotationImagesToHistory()
    {
        if (string.IsNullOrEmpty(HistoryId)) return;
        try
        {
            if (_annotationCleanSource != null || _initialAnnotationSource != null)
            {
                try
                {
                    CaptureHistoryStore.SaveCleanImage(HistoryId, _annotationCleanSource ?? _initialAnnotationSource!);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("StickerWindow.FlushAnnotationImagesToHistory 保存干净底图异常: " + ex.Message);
                }
            }

            if (_isAnnotationModified)
            {
                try
                {
                    System.Drawing.Bitmap? composite = null;
                    if (HasAnyAnnotationContent())
                    {
                        composite = AcquireAnnotationCompositeForExport();
                    }
                    else
                    {
                        var clean = _annotationCleanSource ?? _initialAnnotationSource ?? _bitmap;
                        if (clean != null)
                        {
                            composite = clean.Clone(
                                new System.Drawing.Rectangle(0, 0, clean.Width, clean.Height),
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        }
                    }

                    if (composite != null)
                    {
                        using (composite)
                        {
                            CaptureHistoryStore.TryUpdateImage(HistoryId, composite, AppSettings.Load());
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("StickerWindow.FlushAnnotationImagesToHistory 更新历史记录位图异常: " + ex.Message);
                }
            }
        }
        catch { }
    }

    /// <summary>主动将当前贴图的最新现场（位置、尺寸、缩放、状态、标注）持久化到存档。</summary>
    public void SaveArchiveSnapshot()
    {
        if (_isClosed) return;
        if (HistoryId == null) EnsureHistoryRecorded();
        if (string.IsNullOrEmpty(HistoryId)) return;
        try
        {
            FlushAnnotationImagesToHistory();
            var a = BuildArchiveSnapshot();
            if (a != null)
            {
                StickerArchiveStore.TryWrite(a);
                StickerArchiveStore.RequestSessionSync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow.SaveArchiveSnapshot 失败: " + ex.Message);
        }
    }

    /// <summary>汇总关窗现场。拿不到历史外键（贴图档位关着）就不产存档 —— 恢复的底图就是历史里那张 PNG。</summary>
    public StickerArchive? BuildArchiveSnapshot()
    {
        if (string.IsNullOrEmpty(HistoryId)) return null;

        var a = new StickerArchive
        {
            HistoryId = HistoryId!,
            Kind = StickerKindTag,
            Text = _textContent,
            ClosedAt = DateTime.Now,
        };
        a.Card.ContentRadiusPx = _bakedCardGeometry.ContentRadiusPx;
        a.Card.StrokeWidthPx = _bakedCardGeometry.StrokeWidthPx;
        a.Card.BakedShadow = _hasBakedShadow;
        a.State.Scale = _scale;
        a.State.Locked = _isLocked;
        a.State.Penetrate = _isMousePenetration;
        a.State.Shadow = _shadowVisible;
        // 阴影的两件新账（2026-10-01，issue #50）：手动覆盖位 + 截图勾选意图。
        // 不存前者，用户 Shift+Y 的选择关窗即丢；不存后者，恢复出来的贴图在 Auto 档下
        // 只能回落成"画"，勾过"不要阴影"的那些图会凭空多出一圈光晕。
        a.State.ShadowManual = _shadowManualOverride;
        a.State.CaptureShadowIntent = _captureShadowIntent;
        a.State.Thumbnail = _isThumbnailMode;
        if (_isThumbnailMode)
        {
            a.State.ThumbViewportX = _viewportX;
            a.State.ThumbViewportY = _viewportY;
        }

        double fs = SafeScaling;
        double mPx = ShadowMargin * fs;

        // ⚠ 覆写态的存档口径分两种（2026-09-22 阶段 4 起）：
        //   ㈠ 手势信封态（窗口被撑成信封、终态应为贴合尺寸）→ 存"内容 + 阴影"的贴合矩形，
        //      恢复时按它重建，与改版前一致；
        //   ㈡ 平移常驻态（内容大于视口、窗口已夹进工作区、覆写态刻意保留以承载偏移）
        //      → **必须存真实窗口矩形**。若照 ㈠ 存内容尺寸，长图会存成 1352x13232＝17.9MPx，
        //      重启恢复即重建出超屏窗口，阶段 4 的性能收益在"关掉再打开"后当场丢失
        //      （实测存档确实记成 13232 高，本条即为此修）。
        // 判据取"内容是否大于当前视口"，与 CommitWheelGesture 的 keepOverrideForPan 同一口径
        // （2026-09-23 阶段 1 起改读活几何，不再以覆写标记为前提）。
        var vpArc = GetViewportRectPx();
        GetContentOverflow(vpArc, GetContentRectPx(vpArc), out bool ovX, out bool ovY);
        bool panResident = vpArc.Width > 0 && (ovX || ovY);
        if (panResident)
        {
            IntPtr hwndPan = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwndPan != IntPtr.Zero &&
                ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwndPan, out var rcPan) &&
                rcPan.Width > 0 && rcPan.Height > 0)
            {
                a.State.X = rcPan.Left;
                a.State.Y = rcPan.Top;
                a.State.Width = rcPan.Width;
                a.State.Height = rcPan.Height;
            }
            else
            {
                a.State.X = (int)Math.Round(vpArc.X - mPx);
                a.State.Y = (int)Math.Round(vpArc.Y - mPx);
                a.State.Width = Math.Max(1, (int)Math.Round(vpArc.Width + mPx * 2));
                a.State.Height = Math.Max(1, (int)Math.Round(vpArc.Height + mPx * 2));
            }
        }
        else if (_wheelVisualOverride && _wheelImgWpx > 0 && _wheelImgHpx > 0)
        {
            a.State.X = (int)Math.Round(_wheelImgLpx - mPx);
            a.State.Y = (int)Math.Round(_wheelImgTpx - mPx);
            a.State.Width = Math.Max(1, SnapStablePx(_wheelImgWpx / fs + ShadowMargin * 2, fs));
            a.State.Height = Math.Max(1, SnapStablePx(_wheelImgHpx / fs + ShadowMargin * 2, fs));
        }
        else if (_closingFrameRect is { } r && r.Width > 0 && r.Height > 0)
        {
            a.State.X = r.X;
            a.State.Y = r.Y;
            a.State.Width = r.Width;
            a.State.Height = r.Height;
        }
        else
        {
            // 静止贴合态：窗口矩形＝画面＋两侧阴影边距（恢复侧按"State.X + 阴影边距 = 画面原点"反解）
            const int insetX = 0, insetY = 0;
            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc) &&
                rc.Width > 0 && rc.Height > 0)
            {
                a.State.X = rc.Left + insetX;
                a.State.Y = rc.Top + insetY;
                a.State.Width = Math.Max(1, rc.Width - insetX * 2);
                a.State.Height = Math.Max(1, rc.Height - insetY * 2);
            }
            else
            {
                // ⚠ Bounds 是 DIP，而 State.Width/Height 的口径是物理像素（主路径取 GetWindowRect）。
                //    漏乘 DPI 会让恢复侧再除一次，贴图按 1/DPI 缩小一档。
                a.State.X = Position.X + insetX;
                a.State.Y = Position.Y + insetY;
                a.State.Width = Math.Max(1, (int)Math.Round(Bounds.Width * fs) - insetX * 2);
                a.State.Height = Math.Max(1, (int)Math.Round(Bounds.Height * fs) - insetY * 2);
            }
        }
        // 退出标注只烘"显示"，图元仍留在会话 Document、文字层仍留在活树里（再次进入标注要原样复现）。
        // ⚠ 必须读会话而不是图层：烘焙那一刻 StickerAnnotationLayer.Document 已被置 null（图层
        //    不参与显示、断开模型引用），从图层取会把整批笔迹漏掉；会话到 OnClosed 里 TryWrite
        //    之后才 Dispose，是这里的唯一真值。
        var doc = _annotationSession?.Document ?? StickerAnnotationLayer?.Document;
        if (doc != null && doc.Shapes.Count > 0) a.Shapes.AddRange(doc.Shapes);
        else if (_initialAnnotationShapes != null) a.Shapes.AddRange(_initialAnnotationShapes);

        try
        {
            var texts = TextTool.CreateTextSnapshot();
            if (texts != null && texts.Count > 0) a.Texts.AddRange(texts);
            else if (_initialAnnotationTexts != null) a.Texts.AddRange(_initialAnnotationTexts);
        } catch { }
        if (_isCanvasMode)
        {
            // 存档仍沿用单图格式：保存当前视口合成图，恢复得到普通贴图。
            // 活窗口的独立图层保留在画布中，不把旧原图／旧标注写成恢复基线。
            using var canvas = SnapshotCanvas();
            if (canvas != null)
            {
                CaptureHistoryStore.SaveCleanImage(HistoryId!, canvas);
                CaptureHistoryStore.TryUpdateImage(HistoryId!, canvas, AppSettings.Current);
            }
            a.State.Scale = 1;
            a.Text = null;
            a.Card = new StickerArchiveCard();
            a.Shapes.Clear();
            a.Texts.Clear();
        }
        return a;
    }

    /// <summary>
    /// 把存档现场回灌到一张刚打开的贴图（「恢复上次关闭的贴图」用，见
    /// <see cref="Services.StickerRestoreService"/>）。
    ///
    /// 前置：底图已是历史里那张干净 PNG、<see cref="SetHistoryId"/> 与
    /// <c>ApplyBakedCaptureEffects</c> 已由调用方落定。必须晚于 Show/Opened——
    /// <see cref="OnWindowOpened"/> 会按默认设置覆写 <c>_shadowVisible</c> 并跑一次
    /// <c>ApplyScale</c>，抢在它前面等于白做；穿透还要原生句柄。
    /// </summary>
    public void ApplyArchiveState(StickerArchive? a)
    {
        if (_isClosed || a == null) return;
        try
        {
            double s = SafeScaling;

            // ① 缩放最先落：标注层 DIP 口径（ScreenToDipScale = _scale / DPI）与文字烘焙系数
            //    都由 _scale 推，晚一步重放，图元就按旧倍率的坐标系钉死。
            //    DPI 可能已与存档那轮不同，故按当前上限重新夹一次（越界会让窗口尺寸失控）。
            _scale = Math.Clamp(a.State.Scale <= 0 ? 1.0 : a.State.Scale, 0.2, GetMaxScale());
            ApplyScale();

            _shadowVisible = a.State.Shadow;
            // 阴影两件新账一并回灌（2026-10-01，issue #50）。⚠ 必须在 ApplyShadow() 之前落值——
            // 它们都是 ShouldPaintWindowShadow 的输入，晚一步这一帧就按默认档位画错。
            _shadowManualOverride = a.State.ShadowManual;
            _captureShadowIntent = a.State.CaptureShadowIntent;

            // ② 标注重放（图元坐标锚在底图物理像素，底图就是同一张 ⇒ 逐像素对得上，无需换算）
            RestoreAnnotationsFromArchive(a);

            // ③ 位置：存档记的是窗口外框物理矩形，SetStickerScreenPosition 要的是图片左上角。
            //    缩略态下图片被视口往窗口内偏移，故偏移量也要折算进去，否则退出缩略图会跳位。
            double vpX = a.State.Thumbnail ? a.State.ThumbViewportX : 0;
            double vpY = a.State.Thumbnail ? a.State.ThumbViewportY : 0;
            int imgX = a.State.X + (int)Math.Round((ShadowMargin + vpX) * s);
            int imgY = a.State.Y + (int)Math.Round((ShadowMargin + vpY) * s);

            // 半隐藏位置原样保留；屏幕移除／屏间空洞由统一移动通路修正到最近可见位置。
            SetStickerScreenPosition(new PixelPoint(imgX, imgY));

            // ④ 缩略态：走 ToggleThumbnailMode 会按当前鼠标位置重定取景框，而这里要照搬用户
            //    当初框好的那一片，故直接落到终态参数（外框尺寸取存档值，视口取存档偏移）。
            if (a.State.Thumbnail)
            {
                _isThumbnailMode = true;
                SizeToContent = SizeToContent.Manual;
                // 存档里的取景框就是定稿尺寸，禁止 ApplyScale 把它打回 150 默认框
                _thumbFrameCustomized = true;
                ApplyScale(skipWindowResize: true);
                ApplyThumbnailStateImmediate(
                    new PixelPoint(a.State.X, a.State.Y),
                    Math.Max(1.0, a.State.Width / s),
                    Math.Max(1.0, a.State.Height / s),
                    a.State.ThumbViewportX,
                    a.State.ThumbViewportY);
            }

            // ⑤ 锁定/穿透：绕开 Toggle* 包装，恢复不是用户手势，不该弹"贴图已锁定"这类提示
            if (a.State.Locked) _isLocked = true;
            if (a.State.Penetrate)
            {
                ApplyMousePenetration(true);
                // 与手动开穿透同一收尾：点不动的窗口不该占着"焦点贴图"身份
                StickerFocusManager.ClearFocus(this);
            }
            ApplyShadow();
            UpdateContextMenuState();

            AppLogger.Info("StickerRestore: 已回灌现场 " + a.HistoryId
                           + " scale=" + _scale.ToString("F3", CultureInfo.InvariantCulture)
                           + " 窗口=(" + a.State.X + "," + a.State.Y + ")" + a.State.Width + "x" + a.State.Height
                           + " 锁定=" + a.State.Locked + " 穿透=" + a.State.Penetrate
                           + " 阴影=" + a.State.Shadow + " 缩略=" + a.State.Thumbnail
                           + " 图元=" + a.Shapes.Count + " 文字层=" + a.Texts.Count);
            SaveArchiveSnapshot();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerRestore: 回灌存档失败（贴图以默认状态留在屏幕上）。" + ex.Message);
        }
    }

    /// <summary>
    /// 重放矢量图元与文字层，并立刻烤一次成品当作常态显示。
    ///
    /// 这与用户"标注后按空格退出"看到的完全是同一状态：显示像素只有成品这一份
    /// （图层与宿主层收起），而图元仍留在会话 Document、活文字树仍留在宿主层里，
    /// 再进标注可继续编辑——所以这里绝不 Dispose 会话。
    /// </summary>
    internal void RestoreAnnotationsFromArchive(StickerArchive a)
    {
        if (a.Shapes.Count == 0 && a.Texts.Count == 0) return;
        _isRestoringAnnotations = true;
        try
        {
            EnsureAnnotationSession();
            if (_annotationSession == null) return;

            // ReplaceAll 内部逐项 Clone，存档里解出来的那份图元归本方法持有，回灌后随意处置
            _annotationSession.Document.ReplaceAll(a.Shapes);
            // 文字层必须先建出活树再烘：BakeAnnotationCompositeToDisplay 现读 CreateTextSnapshot
            try
            {
                TextTool.RestoreTextSnapshot(a.Texts);
                TextTool.ApplyErasureMasks(_annotationSession.Document);
            }
            catch (Exception ex) { AppLogger.Warn("StickerRestore: 文字层重放失败，只恢复矢量图元。" + ex.Message); }
            BakeAnnotationCompositeToDisplay();
        }
        finally
        {
            _isRestoringAnnotations = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_isCanvasMode) ExitCanvasMode();
        DisposeCanvasPictures();
        // 关门前最后一次兜底补录历史：Markdown 排版若始终没就绪，过了这句就再没机会
        // （EnsureHistoryRecorded 以 _isClosed 为守卫，必须先于置位调用）。
        if (HistoryId == null) EnsureHistoryRecorded();
        _isClosed = true;
        EndStickerDrag("窗口关闭");
        DisposeLayeredPinSurface();
        ExitAnnotationMode();
        // 存档要趁标注会话与文字层还活着的时候取，故紧跟退出标注、早于下面任何释放
        StickerArchiveStore.TryWrite(BuildArchiveSnapshot());
        if (!string.IsNullOrEmpty(HistoryId) && (_annotationCleanSource != null || _initialAnnotationSource != null))
        {
            try
            {
                CaptureHistoryStore.SaveCleanImage(HistoryId, _annotationCleanSource ?? _initialAnnotationSource!);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StickerWindow.OnClosed 保存干净底图异常: " + ex.Message);
            }
        }

        // 若标注图元被修改过，将最新合成的烘焙位图更新写回历史记录全图与缩略图
        if (!string.IsNullOrEmpty(HistoryId) && _isAnnotationModified)
        {
            try
            {
                System.Drawing.Bitmap? composite = null;
                if (HasAnyAnnotationContent())
                {
                    composite = AcquireAnnotationCompositeForExport();
                }
                else
                {
                    var clean = _annotationCleanSource ?? _initialAnnotationSource ?? _bitmap;
                    if (clean != null)
                    {
                        composite = clean.Clone(
                            new System.Drawing.Rectangle(0, 0, clean.Width, clean.Height),
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    }
                }

                if (composite != null)
                {
                    using (composite)
                    {
                        CaptureHistoryStore.TryUpdateImage(HistoryId, composite, AppSettings.Load());
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StickerWindow.OnClosed 更新历史记录位图异常: " + ex.Message);
            }
        }
        // 文本类贴图录进历史后正文还可能被改：把索引里的可搜文本刷成关窗时的最终值。
        // 同值时 TrySetText 直接返回、不重写索引。
        if (StickerKindTag != "image")
        {
            CaptureHistoryStore.TrySetText(HistoryId, _textContent);
        }
        else if (_wordRegions != null && _wordRegions.Count > 0)
        {
            // 图片贴图的全文只能从 _wordRegions 重建：Alt 选词会把 _textContent 覆盖成"只选了几个词"，
            // 而字级区域缓存始终是整图识别结果。预热没跑完（缓存仍为 null）就留空，交给存量补识别。
            CaptureHistoryStore.TrySetText(HistoryId, BuildTextFromWordRegions(_wordRegions));
        }
        _annotationSession?.Dispose();
        _annotationSession = null;
        // 标注期额外持有的两份位图随窗口释放；引用 _bitmap 的那份由下方 _bitmap 统一释放，勿重复
        try { _annotationCleanDisplay?.Dispose(); } catch { }
        _annotationCleanDisplay = null;
        if (_annotationCleanSourceOwned) { try { _annotationCleanSource?.Dispose(); } catch { } }
        else if (_initialAnnotationSource != null && _initialAnnotationSource != _bitmap)
        {
            try { _initialAnnotationSource.Dispose(); } catch { }
        }
        _annotationCleanSource = null;
        _initialAnnotationSource = null;
        try { _annotationPalette?.Close(); } catch { }
        _annotationPalette = null;
        // 关闭时若命中穿透子类化仍挂在本窗口，先还原（防悬空窗口过程）；再还原尺寸上限子类化
        // （两条 GWL_WNDPROC 链按后进先出：穿透装在上限之后，故先卸穿透）
        UninstallWheelHitTransparency(this);
        UninstallMaxTrackSizeLift();
        // 关闭时若是焦点贴图，清除焦点管理器引用。
        StickerFocusManager.ClearFocus(this);
        try { _source?.Dispose(); } catch { }
        try { _bitmap?.Dispose(); } catch { }
        try { _originalUncutBitmap?.Dispose(); } catch { }
        _originalUncutBitmap = null;
        try { _originalBeforeTranslate?.Dispose(); } catch { }
        _originalBeforeTranslate = null;
        try { _translatedImage?.Dispose(); } catch { }
        _translatedImage = null;
        DisposeImageAdjustResources();
        _wordRegions = null;
        _selectedWordIndices.Clear();
        _wordRectangles.Clear();

        // 翻译标记收尾：掐掉在飞的翻译请求并停掉气泡收起延时。
        // 🔴 必须排在上面所有存档/历史回写之后：文本／Markdown 贴图的紫线画在 Viewbox 内的
        // TextDecorLayer，会被 SnapshotTextSticker 收进对外输出位图，先清数据会让存进历史的那张图
        // 丢掉用户当时看见的译文下划线（图片贴图那一路的紫线在 StickerCanvas、本就不进导出，
        // 但排序纪律按"会进导出的那一路"定，两路共用一处收尾不必分叉）。
        DisposeTranslateMarks();

        // 退出穿透登记簿：钩子的存活窗口＝登记簿非空，最后一个穿透态贴图关掉即摘钩（非穿透贴图无需钩子）
        SyncPenetrationRegistry(add: null, remove: this);

        // 贴图关闭后触发一次防抖同步，若已无活跃贴图则清理会话，若还有则同步当前剩余活跃贴图
        StickerArchiveStore.RequestSessionSync();
    }

    /// <summary>
    /// 同步穿透态登记簿并按需挂/卸全局鼠标钩子（**只在 UI 线程调用**）。
    /// 钩子的存活窗口恒等于"登记簿非空"：装早了、卸晚了，本软件就白挂在系统输入链上。
    /// </summary>
    private static void SyncPenetrationRegistry(StickerWindow? add, StickerWindow? remove)
    {
        if (remove != null) PenetrationStickers.Remove(remove);
        if (add != null && !PenetrationStickers.Contains(add)) PenetrationStickers.Add(add);

        // 句柄快照整体替换（回调读侧无锁）。关掉的窗口句柄要剔除，否则回调会对一个已销毁的句柄做命中测试。
        var hwnds = new IntPtr[PenetrationStickers.Count];
        int n = 0;
        for (int i = 0; i < PenetrationStickers.Count; i++)
        {
            IntPtr h = PenetrationStickers[i].TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (h != IntPtr.Zero) hwnds[n++] = h;
        }
        if (n != hwnds.Length) Array.Resize(ref hwnds, n);
        _penetrationHwnds = hwnds;

        if (n > 0) EnsureMouseHookInstalled();
        else UninstallMouseHookIfUnused();
    }

    private static void EnsureMouseHookInstalled()
    {
        if (_mouseHook != IntPtr.Zero) return;

        _mouseProc = MouseHookCallback;
        IntPtr hMod = ScreenCaptureTool.Platform.NativeMethods.GetModuleHandle(null);
        _mouseHook = ScreenCaptureTool.Platform.NativeMethods.SetWindowsHookEx(
            ScreenCaptureTool.Platform.NativeMethods.WH_MOUSE_LL,
            _mouseProc,
            hMod,
            0
        );

        if (_mouseHook != IntPtr.Zero)
        {
            AppLogger.Info("StickerWindow: 全局鼠标钩子已挂（仅穿透态贴图需要，穿透态数=" + _penetrationHwnds.Length + "）");
        }
        else
        {
            AppLogger.Error("StickerWindow: Failed to install global mouse hook. Error code: " + Marshal.GetLastWin32Error());
        }
    }

    /// <summary>登记簿已空即摘钩（幂等）。</summary>
    private static void UninstallMouseHookIfUnused()
    {
        if (_mouseHook == IntPtr.Zero) return;
        ScreenCaptureTool.Platform.NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
        AppLogger.Info("StickerWindow: 全局鼠标钩子已卸（已无穿透态贴图，本软件不再占用系统输入链）");
    }

    /// <summary>
    /// 全局低级鼠标钩子回调：只处理穿透态贴图上的右键（穿透窗自己收不到鼠标消息，只能在这里拦）。
    /// ⚠ 本回调**运行在安装它的线程上、且处在系统鼠标派发路径中**——它慢 1 毫秒，全系统鼠标就多等
    /// 1 毫秒；超过 `LowLevelHooksTimeout` 还会被系统直接摘钩（表现为整机鼠标发僵）。因此这里**只许**：
    /// 读 <see cref="_penetrationHwnds"/> 快照 → `GetWindowRect` 取矩形 → 整数比较。
    /// **禁止**访问 Avalonia 对象（旧实现 `OfType&lt;StickerWindow&gt;()` + 读 `Position/Bounds` 既分配
    /// 又把 UI 线程的卡顿摊给全系统）、禁止加锁、禁止 IO。
    /// </summary>
    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == 0x0204) // WM_RBUTTONDOWN
        {
            IntPtr[] hwnds = _penetrationHwnds;
            if (hwnds.Length > 0)
            {
                var msStruct = Marshal.PtrToStructure<ScreenCaptureTool.Platform.NativeMethods.MSLLHOOKSTRUCT>(lParam);
                int sx = msStruct.pt.X;
                int sy = msStruct.pt.Y;

                for (int i = 0; i < hwnds.Length; i++)
                {
                    IntPtr h = hwnds[i];
                    if (h == IntPtr.Zero) continue;
                    if (!ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(h, out var r)) continue;
                    if (sx < r.Left || sx >= r.Right || sy < r.Top || sy >= r.Bottom) continue;

                    // 命中：开菜单的动作派回 UI 线程（这里绝不同步碰 Avalonia）
                    PostPenetrationContextMenu(h, sx, sy);

                    // 吞掉这次右键，避免落到下层应用
                    return new IntPtr(1);
                }
            }
        }

        return ScreenCaptureTool.Platform.NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 把"在某张穿透态贴图上开右键菜单"派回 UI 线程执行（钩子回调路径不得同步碰 Avalonia）。
    /// 按句柄回查窗口：句柄是回调唯一携带的标识，且窗口可能在派发前已被关掉，故必须判空。
    /// </summary>
    private static void PostPenetrationContextMenu(IntPtr hwnd, int screenX, int screenY)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

                StickerWindow? window = null;
                foreach (var w in desktop.Windows.OfType<StickerWindow>())
                {
                    if (!w._isMousePenetration) continue;
                    if ((w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero) != hwnd) continue;
                    window = w;
                    break;
                }
                if (window == null) return;

                var contextMenu = window.RootBorder.ContextMenu;
                if (contextMenu == null) return;

                var localPt = window.PointToClient(new Avalonia.PixelPoint(screenX, screenY));
                contextMenu.PlacementRect = new Avalonia.Rect(localPt, new Avalonia.Size(1, 1));
                contextMenu.Placement = Avalonia.Controls.PlacementMode.Bottom;
                contextMenu.Open(window.RootBorder);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StickerWindow: 穿透态右键菜单派发失败: " + ex.Message);
            }
        });
    }

    private static System.Drawing.Bitmap CreateDesignTimeBitmap()
    {
        var bitmap = new System.Drawing.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, System.Drawing.Color.Transparent);
        return bitmap;
    }

    private static Avalonia.Media.Imaging.Bitmap ToAvaloniaBitmap(System.Drawing.Bitmap bitmap)
    {
        if (bitmap == null) return new Avalonia.Media.Imaging.Bitmap(new MemoryStream());
        return Utils.BitmapConversion.ToAvaloniaBitmap(bitmap)
            ?? new Avalonia.Media.Imaging.Bitmap(new MemoryStream());
    }

    private void SetTextSelectable(bool enabled)
    {
        _isTextSelectable = enabled;
        // 显式切换持久模式时接管选词状态：清除 Alt 临时标记，避免两种模式状态交叉。
        _altQuickSelect = false;
        _altTextStickerSelect = false;
        _altManualTextSelect = false;
        // 切换模式时旧选区作废，同步清空快照（防止复制到已不存在的选区文本）。
        _textSelSnapshot = null;
        _selSnapshot.Clear();
        HideSelectionToolbar();

        UpdateSelectableTextMenuItem();

        if (StickerTextBlock != null)
        {
            StickerTextBlock.IsHitTestVisible = _isTextSelectable;
            if (!_isTextSelectable)
            {
                StickerTextBlock.SelectionStart = 0;
                StickerTextBlock.SelectionEnd = 0;
            }
        }
        
        if (StickerMarkdownViewer != null)
        {
        StickerMarkdownViewer.IsHitTestVisible = _isTextSelectable;
        }

        if (_isTextSelectable)
        {
            // For image stickers: run PaddleOCR for line-level rectangle selection.
            // Even if _textContent is not empty, if it's rendered as an image (e.g. failed HTML/Markdown fallback), we MUST run OCR!
            if (_isCanvasMode || StickerImage != null && StickerImage.IsVisible)
            {
                // 复用已缓存的 OCR 结果（打开贴图时的静默预热 / Alt 快速选词已识别），
                // 直接构建选词覆盖层，免去重复识别与等待。
                if (_wordRegions != null)
                {
                    if (_wordRegions.Count == 0)
                    {
                        StickerToastWindow.ShowAtTop("未识别到文本");
                        _isTextSelectable = false;
                        UpdateSelectableTextMenuItem();
                        return;
                    }
                    _selectedWordIndices.Clear();
                    _wordRectangles.Clear();
                    BuildWordOverlay();
                    StickerCanvas.IsVisible = true;
                    StickerCanvas.IsHitTestVisible = true;
                    StickerToastWindow.ShowAtTop("请拖选要复制的文本");
                    return;
                }

                StickerToastWindow.ShowAtTop("识别文本...");
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                    if (_bitmap != null)
                    {
                        int version = _wordContentVersion;
                        var lines = await RecognizeWordRegionsAsync();
                        if (_isClosed || version != _wordContentVersion || !_isTextSelectable) return;
                        if (lines != null && lines.Count > 0)
                        {
                            UsageStatistics.RecordOcr(); // 统计：贴图文本提取识别成功一次
                            _wordRegions = lines;
                            _textContent = BuildTextFromWordRegions(_wordRegions);
                            _selectedWordIndices.Clear();
                            _wordRectangles.Clear();

                            // Build line rectangle overlay on Canvas
                            BuildWordOverlay();

                            StickerCanvas.IsVisible = true;
                            StickerCanvas.IsHitTestVisible = true;
                            StickerToastWindow.ShowAtTop("请拖选要复制的文本");
                        }
                        else
                        {
                            _wordRegions = lines;
                            StickerToastWindow.ShowAtTop("未识别到文本");
                            _isTextSelectable = false;
                            UpdateSelectableTextMenuItem();
                        }
                    }
                    else
                    {
                        StickerToastWindow.ShowAtTop("该贴图不支持提取文本");
                        _isTextSelectable = false;
                        UpdateSelectableTextMenuItem();
                    }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("StickerWindow: OCR 识别开启失败", ex);
                        StickerToastWindow.ShowAtTop("OCR 识别失败");
                        _isTextSelectable = false;
                        UpdateSelectableTextMenuItem();
                    }
                });
            }
            else if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible)
            {
                // Markdown 贴图：MarkdownViewer 已在上面设置为 IsHitTestVisible=true，
                // 不要显示 StickerTextBlock，否则 OnWindowPointerPressed 中的
                // if-else if 链会进入 TextBlock 分支而跳过 MarkdownViewer 分支，
                // 导致无法选中文本。
            }
            else
            {
                // For text stickers (clipboard text): use existing TextBlock
                if (StickerTextBlock != null) StickerTextBlock.IsVisible = true;
                if (StickerTextBlock != null) StickerTextBlock.IsHitTestVisible = true;
            }
        }
        else
        {
            // Disable text selection
            _isWordSelecting = false;
            _dragStartWordIndex = -1;
            _selectedWordIndices.Clear();
            _wordRectangles.Clear();

            if (StickerTextBlock != null) StickerTextBlock.ClearSelection();
            if (StickerTextBlock != null) StickerTextBlock.IsHitTestVisible = false;
            if (StickerImage.IsVisible)
            {
                if (StickerTextBlock != null) StickerTextBlock.IsVisible = false;
            }
            else if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible)
            {
                // Markdown 贴图关闭文本选择时，确保 StickerTextBlock 也被隐藏
                if (StickerTextBlock != null) StickerTextBlock.IsVisible = false;
            }

            ClearWordOverlay();
            RetainWordMarkLayer();   // 同上：关掉选词态不许把已落的几笔一起收走
            // 仅关闭选词交互，图片没有变化，保留识别缓存供再次开启/Alt 选词复用。
        }
    }

    private void ToggleThumbnailMode()
    {
        EndStickerDrag("切换缩略图");
        // 缩略图换掉的是整套视口几何，气泡那套"按紫点现算"的定位前提不成立：先收（标记本身留着，
        // 回到正常态由 RedrawTextDecorations 重画到位）。与 UpdateSelectionToolbarPosition 的模式判据同口径。
        if (_bubbleShown) HideTranslateBubble();
        HideSelectionToolbar();
        // 缩略图自有一套"窗口＝内容＋阴影边距"前提下的视口几何，标注层与之不成立：先定稿收起。
        FinalizeAnnotationForGeometryChange("缩略图切换");
        // 缩略图的视口/角部热区/描边一律以"窗口矩形＝内容＋阴影边距"为前提（GetThumbCornerAt 等），
        // 而按下已不再收缩信封（见 OnWindowPointerPressed）→ 切模式前自行收口一次。
        // 这一步本身就是整窗尺寸的大改变，那一次 resize 不构成额外观感代价。
        if (_wheelVisualOverride) CommitWheelGesture("缩略图切换");

        double s = SafeScaling;
        _isApplyingScale = true;
        try
        {
            double width = Math.Max(20.0, Math.Round(_sourcePixelWidth * _scale) / s);
            double height = Math.Max(20.0, Math.Round(_sourcePixelHeight * _scale) / s);

            if (_isThumbnailMode)
            {
                // 退出缩略图：窗口框放大、视口归零，单帧原子切换（无动效）。
                // 终态窗口位置取"图片冻结"解：左上角 = 当前缩略图左上角 + 当前视口偏移（DIP→物理像素），
                // 保证图片屏幕位置不动、精确还原进入前的原始窗口位置。
                _isThumbnailMode = false;
                SizeToContent = SizeToContent.Manual;

                double endW = width + ShadowMargin * 2;
                double endH = height + ShadowMargin * 2;
                var endPos = new PixelPoint(
                    (int)Math.Round(Position.X + _viewportX * s),
                    (int)Math.Round(Position.Y + _viewportY * s));
                ThumbDiag($"退出请求：DPI={s} 窗口Pos=({Position.X},{Position.Y}) 视口=({_viewportX:F2},{_viewportY:F2}) " +
                          $"endPos=({endPos.X},{endPos.Y}) 终态DIP={endW:F2}x{endH:F2}");
                ApplyThumbnailStateImmediate(endPos, endW, endH, 0, 0);

                StickerToastWindow.ShowAtTop("已退出缩略图模式");
            }
            else
            {
                // 进入缩略图：窗口框缩小、视口偏移，单帧原子切换（无动效）。
                _isThumbnailMode = true;
                _thumbFrameCustomized = false;   // 重新进入恢复 150 默认框，清除拖角自定义标记
                SizeToContent = SizeToContent.Manual;

                // 冻结基准：进入前图片内容区左上角的屏幕物理位置（普通模式视口为 0；
                // 进入/退出后以此校验图片内容是否纹丝不动，量化"位置偏移"）
                IntPtr hwndBase = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwndBase != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwndBase, out var rcBase))
                {
                    _thumbFreezeBaseX = rcBase.Left + ShadowMargin * s;
                    _thumbFreezeBaseY = rcBase.Top + ShadowMargin * s;
                }

                // 刷新图片控件尺寸（缩略图分支不动视口）
                ApplyScale(skipWindowResize: true);

                // 判断鼠标是否在（缩小前的）贴图窗口内：
                // 在窗口内 → 视口对准鼠标所指的图片位置；不在 → 视口对准图片中心
                bool mouseInside = false;
                double ratioX = 0.5, ratioY = 0.5;
                if (ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var cursorPt))
                {
                    // 鼠标相对于当前（缩小前）窗口的本地坐标
                    double localX = (cursorPt.X - Position.X) / s;
                    double localY = (cursorPt.Y - Position.Y) / s;
                    mouseInside = localX >= 0 && localY >= 0 &&
                                  localX <= Bounds.Width && localY <= Bounds.Height;

                    // 鼠标在图片内容区中的比例位置
                    double imgW = Math.Max(1.0, Bounds.Width - ShadowMargin * 2);
                    double imgH = Math.Max(1.0, Bounds.Height - ShadowMargin * 2);
                    ratioX = Math.Clamp((localX - ShadowMargin) / imgW, 0, 1);
                    ratioY = Math.Clamp((localY - ShadowMargin) / imgH, 0, 1);
                }
                if (!mouseInside)
                {
                    // 鼠标在贴图外：视口对准图片中心（ratio 不能留在被 clamp 的边缘值上）
                    ratioX = 0.5;
                    ratioY = 0.5;
                }

                double clientW = Math.Min(150.0, width);
                double clientH = Math.Min(150.0, height);

                // 终态视口（每轴独立）：先按"视口中心对准鼠标所指位置"取值，
                // 再 clamp 到 [clientDim - imgDim, 0] —— 视口完整落在图片内：
                // 鼠标靠近图片边缘时视口贴边显示（以图片边缘为准），不露出图片外空白。
                double endVpX = width > clientW ? Math.Clamp(clientW / 2 - ratioX * width, clientW - width, 0) : 0;
                double endVpY = height > clientH ? Math.Clamp(clientH / 2 - ratioY * height, clientH - height, 0) : 0;

                // 终态窗口位置 = 锚点冻结解（每轴独立）：endPos = startPos - endVp * s。
                // 任意图片像素的屏幕位置不变，且：
                //   - 图片不超 150 的轴：endVp = 0，窗口该轴保持不动（若以鼠标为中心会导致图片平移，是位置偏移的根源）；
                //   - 视口贴边时：缩略图窗口边缘与原窗口边缘对齐，窗口完整落在原贴图矩形内（不额外拓展）；
                //   - 鼠标在图内且视口未贴边时：等价于缩略图窗口以鼠标为中心原地收拢。
                double endW = clientW + ShadowMargin * 2;
                double endH = clientH + ShadowMargin * 2;
                // 位置取整到物理像素后，把取整误差折算进视口（亚像素变换），
                // 保证图片内容逐像素精确冻结，不因取整产生可见偏移
                double idealX = Position.X - endVpX * s;
                double idealY = Position.Y - endVpY * s;
                var endPos = new PixelPoint((int)Math.Round(idealX), (int)Math.Round(idealY));
                endVpX += (idealX - endPos.X) / s;
                endVpY += (idealY - endPos.Y) / s;

                ThumbDiag($"进入请求：DPI={s} 鼠标=({cursorPt.X},{cursorPt.Y}) 窗口Pos=({Position.X},{Position.Y}) " +
                          $"Bounds={Bounds.Width:F2}x{Bounds.Height:F2} 图片DIP={width:F2}x{height:F2} mouseInside={mouseInside} " +
                          $"ratio=({ratioX:F3},{ratioY:F3}) client={clientW:F2}x{clientH:F2} endVp=({endVpX:F2},{endVpY:F2}) " +
                          $"ideal=({idealX:F2},{idealY:F2}) endPos=({endPos.X},{endPos.Y})");

                ApplyThumbnailStateImmediate(endPos, endW, endH, endVpX, endVpY);

                StickerToastWindow.ShowAtTop("已进入缩略图模式");
            }
            ApplyShadow();
            UpdateContextMenuState();
            SaveArchiveSnapshot();
        }
        finally
        {
            // 渲染帧异步应用期间保持 _isApplyingScale = true（由 ApplyThumbnailStateImmediate
            // 的帧回调复位），阻止期间 SizeChanged 触发 ApplyScale 回写打架。
            if (!_thumbApplyPending)
                _isApplyingScale = false;
        }
    }

    private void ToggleLock()
    {
        EndStickerDrag("切换锁定");
        // 锁定后一切编辑手势都被禁，先定稿再锁；解锁方向 _isAnnotating 恒为假，helper 自行跳过。
        FinalizeAnnotationForGeometryChange("锁定");
        _isLocked = !_isLocked;

        ApplyShadow();
        UpdateContextMenuState();

        if (_isLocked)
        {
            StickerToastWindow.ShowAtTop("贴图已锁定");
        }
        else
        {
            StickerToastWindow.ShowAtTop("贴图已解锁");
        }

        SaveArchiveSnapshot();
    }

    private void ToggleMousePenetration()
    {
        ApplyMousePenetration(!_isMousePenetration);
        UpdateContextMenuState();

        if (_isMousePenetration)
        {
            StickerFocusManager.ClearFocus(this);
            StickerToastWindow.ShowAtTop("已开启鼠标穿透");
        }
        else
        {
            StickerToastWindow.ShowAtTop("已关闭鼠标穿透");
        }
    }

    public void ApplyMousePenetration(bool enabled)
    {
        if (enabled != _isMousePenetration) EndStickerDrag("切换穿透");
        _isMousePenetration = enabled;
        ApplyShadow();

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            // 取不到句柄：扩展样式改不了、穿透也没真正生效，登记簿不登记它（钩子自然也不会为它而挂）
            AppLogger.Warn("StickerWindow: 切换鼠标穿透时取不到窗口句柄，本次未生效");
            SyncPenetrationRegistry(add: null, remove: this);
            return;
        }

        try
        {
            IntPtr oldStyle = ScreenCaptureTool.Platform.NativeMethods.GetWindowLongPtr(hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE);
            long style = oldStyle.ToInt64();
            if (enabled)
            {
                style |= (ScreenCaptureTool.Platform.NativeMethods.WS_EX_TRANSPARENT | ScreenCaptureTool.Platform.NativeMethods.WS_EX_LAYERED);
            }
            else
            {
                style &= ~(ScreenCaptureTool.Platform.NativeMethods.WS_EX_TRANSPARENT);
            }
            ScreenCaptureTool.Platform.NativeMethods.SetWindowLongPtr(hwnd, ScreenCaptureTool.Platform.NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow.ApplyMousePenetration failed: ", ex);
        }

        // 穿透态登记：全局鼠标钩子只为穿透态贴图的右键菜单而挂，进出穿透态即挂/卸（见 PenetrationStickers 注释）
        SyncPenetrationRegistry(add: enabled ? this : null, remove: enabled ? null : this);

        SaveArchiveSnapshot();
    }

    public void CancelMousePenetration()
    {
        if (_isMousePenetration)
        {
            ApplyMousePenetration(false);
            UpdateContextMenuState();
            StickerToastWindow.ShowAtTop("已关闭鼠标穿透");
        }
    }

    /// <summary>把内置快捷键绑定写成菜单项右对齐快捷键列（显示文本由全局 MenuGestureConverter 统一渲染）。</summary>
    private void SetMenuGesture(MenuItem item, string shortcutId)
    {
        item.InputGesture = _shortcuts.TryGetValue(shortcutId, out var sc) && sc.Key != Key.None
            ? new KeyGesture(sc.Key, sc.Modifiers)
            : null;
    }

    /// <summary>「文本可选择」开关态：图标染品牌紫，不改文案。</summary>
    private void UpdateSelectableTextMenuItem()
    {
        if (MenuSelectableText != null) MenuSelectableText.Classes.Set("on", _isTextSelectable);
    }

    private void UpdateContextMenuState()
    {
        if (StickerContextMenu == null) return;

        // 「清除划词标注」有几笔才出现：右键菜单打开必经本方法，浮条那侧不必再管这一项的时机
        RefreshClearMarksMenuItem();

        // 文案保持纯净：开关态交给图标染品牌紫，快捷键按当前绑定写进独立的右对齐列（设置页可改键）
        if (MenuLock != null)
        {
            SetMenuGesture(MenuLock, "sticker.lock");
            MenuLock.Classes.Set("on", _isLocked);
        }
        if (MenuPenetrate != null)
        {
            SetMenuGesture(MenuPenetrate, "sticker.penetrate");
            MenuPenetrate.Classes.Set("on", _isMousePenetration);
        }
        if (MenuThumbnailMode != null)
        {
            SetMenuGesture(MenuThumbnailMode, "sticker.thumbnail");
            MenuThumbnailMode.Classes.Set("on", _isThumbnailMode);
        }
        if (MenuCopyImage != null) SetMenuGesture(MenuCopyImage, "sticker.copy");
        if (MenuTranslate != null)
        {
            SetMenuGesture(MenuTranslate, "sticker.translate");
            // 已有译文时显示为勾选状态（表示当前处于翻译态，可切换回原图）
            bool hasTranslation = _originalBeforeTranslate != null && _translatedImage != null;
            MenuTranslate.Classes.Set("on", hasTranslation && _isShowingTranslation);
        }
        if (MenuSaveImage != null) SetMenuGesture(MenuSaveImage, "sticker.save");
        if (MenuClose != null) SetMenuGesture(MenuClose, "sticker.close");
        UpdateSelectableTextMenuItem();
        UpdateExtendedMenuState();

        // 「适配屏幕」只在按当前缩放装不进整屏时可点：fit < 当前 scale ⟺ 有一部分内容落在
        // 屏幕外（像素照画，只能靠拖动查看），此动作把它一次收进屏幕内。
        // 单独算、不放进下面 _isLocked 的批量分支，免得被那两段 IsEnabled 赋值覆盖。
        if (MenuFitToScreen != null)
        {
            MenuFitToScreen.IsEnabled = !_isLocked && !_isThumbnailMode
                && !(_needsMarkdownHeightCapture && IsMarkdownSticker)
                && TryComputeFitScaleToWorkArea(out double fitScale)
                && fitScale < _scale - 0.001;
        }

        // 智能抠图菜单显隐与可用状态
        bool isImageSticker = _bitmap != null && (TextViewbox == null || !TextViewbox.IsVisible);
        bool hasUncutBackup = _originalUncutBitmap != null;
        bool canMatting = !_isLocked && isImageSticker && !IsMattingInFlight && !IsTranslateInFlight;
        if (MenuMatting != null)
        {
            MenuMatting.IsVisible = isImageSticker;
            MenuMatting.IsEnabled = canMatting;
            if (MenuRestoreOriginalImage != null)
            {
                MenuRestoreOriginalImage.IsVisible = hasUncutBackup;
                MenuRestoreOriginalImage.IsEnabled = canMatting && hasUncutBackup;
            }
            if (MenuMattingSeparator != null)
            {
                MenuMattingSeparator.IsVisible = hasUncutBackup;
            }
        }

        if (_isLocked)
        {
            if (MenuThumbnailMode != null) MenuThumbnailMode.IsEnabled = false;
            if (MenuLock != null) MenuLock.IsEnabled = true;
            if (MenuPenetrate != null) MenuPenetrate.IsEnabled = false;
            if (MenuSelectableText != null) MenuSelectableText.IsEnabled = false;
            if (MenuCopyImage != null) MenuCopyImage.IsEnabled = false;
            if (MenuMatting != null) MenuMatting.IsEnabled = false;
        }
        else
        {
            if (MenuThumbnailMode != null) MenuThumbnailMode.IsEnabled = true;
            if (MenuLock != null) MenuLock.IsEnabled = true;
            if (MenuPenetrate != null) MenuPenetrate.IsEnabled = true;
            if (MenuSelectableText != null) MenuSelectableText.IsEnabled = true;
            if (MenuCopyImage != null) MenuCopyImage.IsEnabled = true;
            if (MenuMatting != null) MenuMatting.IsEnabled = canMatting;
        }
        UpdateCanvasMenuState();
    }

    private void OnThumbnailModeClick(object? sender, RoutedEventArgs e)
    {
        ToggleThumbnailMode();
    }

    private void OnFitToScreenClick(object? sender, RoutedEventArgs e)
    {
        FitStickerToWorkArea();
    }

    private void OnLockClick(object? sender, RoutedEventArgs e)
    {
        ToggleLock();
    }

    private void OnPenetrateClick(object? sender, RoutedEventArgs e)
    {
        ToggleMousePenetration();
    }
}
