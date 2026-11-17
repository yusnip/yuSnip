using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// <c>StickerWindow</c> 的文本标注半边。
///
/// <para>2026-09-22 的「标注编辑会话上提」把这里原有的 <see cref="ITextAnnotationHost"/> 实现、
/// 文本指针路由与历史并入全部搬进了共享的 <see cref="AnnotationEditSession"/>（贴图与录屏共用那一份），
/// 本文件因此只剩贴图宿主独有的两件事：<b>文本画布的几何口径</b>与<b>成品烘焙</b>。</para>
///
/// <para>坐标口径（全链路唯一，改一处必须全链路同步）：
/// <list type="bullet">
/// <item>文本画布＝<c>StickerTextHost</c>，其 DIP 宽高与 <c>StickerAnnotationLayer</c> 逐值相同
/// （<c>DIP = 内容物理像素 × _scale / SafeScaling</c>），原点与内容左上角重合。</item>
/// <item>因此控制器拿到的指针点用 <c>e.GetPosition(StickerTextHost)</c> 即画布 DIP，无需换算。</item>
/// <item>定稿烘焙比例必须是上面那个系数的倒数（<c>SafeScaling / _scale</c>），否则"编辑期看着对、
/// 退出后位置与字号都错"。</item>
/// </list>
/// </summary>
public partial class StickerWindow
{
    /// <summary>文本标注共享实现（与截图遮罩、录屏覆盖层同一份代码、同一套行为）。</summary>
    private TextAnnotationController TextTool => Anno.Texts;

    /// <summary>文本可用范围（画布 DIP）：原点即内容左上角，宽高取宿主层与标注层同步好的那份 DIP 尺寸。</summary>
    private Rect GetStickerTextValidRect()
    {
        if (StickerTextHost == null) return new Rect(0, 0, 1, 1);
        // Avalonia 的 Width/Height 是 double（未显式赋值时为 NaN），不是可空类型
        double w = StickerTextHost.Width;
        double h = StickerTextHost.Height;
        if (double.IsNaN(w) || w <= 0) w = StickerTextHost.Bounds.Width;
        if (double.IsNaN(h) || h <= 0) h = StickerTextHost.Bounds.Height;
        return new Rect(0, 0, Math.Max(1.0, w), Math.Max(1.0, h));
    }

    /// <summary>
    /// 文本宿主层与标注图层<b>成对</b>同步几何：宽 / 高 / Margin 一律以 <c>StickerAnnotationLayer</c> 为准
    /// （它自己已按 <c>ApplyScale</c> 的 DIP 公式与图片同 Margin 钉好），文本层不另起一套算法。
    /// ⚠ 该方法的每个调用点都必须紧跟标注层的同一批赋值：漏一处就是
    /// docs/protection.md「贴图标注显示管线」条款③ 记录过的"缩小后笔迹跑位"同型事故。
    /// </summary>
    private void SyncTextHostGeometry()
    {
        if (StickerTextHost == null || StickerAnnotationLayer == null) return;
        if (StickerTextHost.Width != StickerAnnotationLayer.Width)
            StickerTextHost.Width = StickerAnnotationLayer.Width;
        if (StickerTextHost.Height != StickerAnnotationLayer.Height)
            StickerTextHost.Height = StickerAnnotationLayer.Height;
        if (StickerTextHost.Margin != StickerAnnotationLayer.Margin)
            StickerTextHost.Margin = StickerAnnotationLayer.Margin;
        // 聚光灯圆角浮条挂在图层几何上（内容像素 × 缩放系数 + 图层 Margin），故与文本宿主层一样，
        // 每次图层几何变化都要跟着重算。本方法正是"图层几何变了"的统一下游（ApplyScale / 滚轮信封 /
        // 初始化四处都汇到这里），挂这儿比在四个调用点各补一行可靠。
        RefreshSpotlightRadiusPill();
    }

    /// <summary>
    /// 浮窗首次创建后调用一次：把共享文本参数面板挂进会话（注入控制器 + 抓取控件并绑事件）。
    /// 一个浮窗一生只挂一次——重复绑定会让"描边 Popup 点一次开、再点一次仍开"这类开关逻辑互相抵消。
    /// </summary>
    private void InitAnnotationTextTool()
    {
        if (_annotationPalette == null) return;
        Anno.AttachTextPanel(_annotationPalette.ToolbarControl);
    }

    /// <summary>
    /// 文本工具下的左键按下（转发给共享会话）：命中已有文本＝选中 / 双击改字 / 拖动；空白＝新建。
    /// 返回 false＝点在贴图内容区之外，本次按下归回贴图的拖动 / 拖边逻辑。
    /// </summary>
    private bool TryHandleAnnotationTextPointerPressed(Avalonia.Point windowPoint, PointerPressedEventArgs e)
        => Anno.TryHandleTextPointerPressed(windowPoint, e);

    /// <summary>当前有没有"看得见字"的文本层。空文本层不计入——点了一下没打字，退出后贴图该原样退回。</summary>
    private bool HasAnnotationText() => Anno.HasAnnotationText();

    /// <summary>
    /// "有没有标注"的统一判据：矢量图元<b>或</b>文字。
    /// ⚠ 三处导出（退出定稿 / 复制 / 保存）历史上只看 <c>Document.Count</c>，
    /// 结果"只写字不画笔"的贴图会静默导出一张没有字的图——本轮把判据统一收到这里。
    /// </summary>
    private bool HasAnyAnnotationContent()
        => _annotationSession != null && (_annotationSession.Document.Count > 0 || HasAnnotationText());

    /// <summary>
    /// 取标注成品位图用于复制 / 保存。返回 null＝当前没有标注内容，调用方回落到贴图自身位图。
    /// ⚠ 必须先 Rebuild 再 CopyComposite：文本层走 CommitHistoryOnly 不重烘成品，缓存的成品很可能是旧的。
    /// </summary>
    private System.Drawing.Bitmap? AcquireAnnotationCompositeForExport()
    {
        if (_isCanvasMode) return SnapshotCanvas();
        if (!HasAnyAnnotationContent() || _annotationSession == null) return null;
        _annotationSession.SetTextSnapshots(TextTool.CreateTextSnapshot());
        _annotationSession.Rebuild();
        return _annotationSession.CopyComposite();
    }
}
