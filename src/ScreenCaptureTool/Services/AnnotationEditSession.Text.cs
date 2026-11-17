using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Services;

/// <summary>
/// <see cref="AnnotationEditSession"/> 的文本半边：实现 <see cref="ITextAnnotationHost"/>，
/// 把"坐标口径、参数面板作用域、历史并入"这三件宿主差异交给与截图遮罩**同一份**
/// <see cref="TextAnnotationController"/>；本文件不含任何文本排版/烘焙算术。
///
/// <para>之所以能整份共用：<see cref="TextLayerInfo"/> 一棵活 <see cref="TextBox"/> 可视树长在宿主画布上，
/// 截图、贴图、录屏三处唯一的区别就是"画布是谁、DIP 怎么换算、改完往哪儿提交"，
/// 而那三样本来就是 <see cref="IAnnotationEditHost"/> 的成员。</para>
/// </summary>
internal sealed partial class AnnotationEditSession
{
    /// <summary>共享文本状态机（与截图遮罩同一份代码、同一套行为）。</summary>
    private readonly TextAnnotationController _texts;

    /// <summary>宿主在自己的指针/滚轮/键盘路由里直接用它转发（moved / released / 字号档 / 命中让路）。</summary>
    public TextAnnotationController Texts => _texts;

    // ──────────────────────────────────────────
    // ITextAnnotationHost：宿主差异全收敛在这十几行
    // ──────────────────────────────────────────

    Canvas ITextAnnotationHost.TextLayerCanvas => _host.TextLayerCanvas;
    Rect ITextAnnotationHost.ErasureToTextCanvasRect(System.Drawing.RectangleF mark)
        => _host.ErasureToTextCanvasRect(mark);

    // 与截图同口径：拖动 / 旋转期间捕获的是窗口本身，不是画布（换目标＝换事件路由链）。
    IInputElement ITextAnnotationHost.TextPointerCaptureTarget => _host.PointerCaptureTarget;

    // 参数面板长在另一扇窗（贴图浮窗／录屏控制条）的 UserControl 命名域里，宿主窗口的 FindControl 取不到。
    TControl ITextAnnotationHost.FindTextPanelControl<TControl>(string name)
        => _host.EditToolbar!.FindControl<TControl>(name)!;

    bool ITextAnnotationHost.TryFindTextPanelResource(string key, out object? value)
        => _host.TryFindPanelResource(key, out value);

    Rect ITextAnnotationHost.GetTextValidRect() => _host.GetTextValidRect();

    bool ITextAnnotationHost.HasTextSelection => _host.HasTextRegion;

    Rect ITextAnnotationHost.TextSelectionDip => _host.TextSelectionDip;

    bool ITextAnnotationHost.IsTextToolCurrent() => _host.ActiveAnnotationTool == ToolMode.Text;

    void ITextAnnotationHost.RequestTextSnapshot() => CommitTextChange();

    void ITextAnnotationHost.NotifyTextPanelNeedsRefresh()
        => _host.EditToolbar?.UpdateColorPreviewForTarget();

    /// <summary>
    /// 全局标注色归参数条持有（与其他标注工具共用同一份状态），故读写都代理过去。
    /// setter 里刻意比 getter 严格：参数条还没建时不写，避免"取色目标被回写进一个不存在的工具栏"。
    /// </summary>
    System.Drawing.Color ITextAnnotationHost.SharedAnnotationColor
    {
        get
        {
            var toolbar = _host.EditToolbar;
            if (toolbar == null) return System.Drawing.Color.Red;
            var c = toolbar.SelectedColor;
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        set
        {
            var toolbar = _host.EditToolbar;
            if (toolbar == null) return;
            toolbar.SetColor(Avalonia.Media.Color.FromArgb(value.A, value.R, value.G, value.B));
        }
    }

    // ──────────────────────────────────────────
    // 文本层的指针路由（截图侧同一相位、同一时序）
    // ──────────────────────────────────────────

    /// <summary>
    /// 文本工具下的左键按下：交给共享状态机（命中已有文本＝选中 / 双击改字 / 拖动；空白＝新建）。
    /// 返回 false＝点在内容区之外，本次按下归宿主自己的窗口逻辑。
    ///
    /// 刻意不借道 <c>AnnotationInteractionController</c> 的 <c>TextRequested</c>：那条链送的是
    /// <b>内容物理像素</b>，而控制器整棵树按画布 DIP 排布，两套单位混用必错位；且 Text 分支不落
    /// <c>_settings</c>，<c>IsDrawing</c> 恒 false，后续 Moved/Released 的重入保护也形同虚设。
    /// 截图侧同样是在指针按下里先处理文本、不进气笔状态机（<c>CaptureSession</c> 的文本特例分支）。
    /// </summary>
    public bool TryHandleTextPointerPressed(Point windowPoint, PointerPressedEventArgs e)
    {
        var canvas = _host.TextLayerCanvas;
        if (!_host.TryGetAnnotationPoint(windowPoint, out _)) return false;

        var dip = e.GetPosition(canvas);
        if (!_texts.TryHandleTextToolInteraction(dip, e.ClickCount))
        {
            // 与截图同一时序：先压"改动前"的状态再动手改（会话的 Commit 压的是上一份已提交状态）
            CommitTextChange();
            _texts.AddTextAt(dip);
            _host.LogDiag($"新建文本层 画布DIP=({dip.X:F1},{dip.Y:F1}) 有效区={_host.GetTextValidRect()} "
                          + $"文本层数={_texts.TextLayers.Count}");
        }
        // 选中互斥：点中文本层时矢量高亮一并作废，否则 Delete 的目标不唯一
        if (_texts.SelectedText != null) ClearSelection();
        // 拖动 / 旋转的连续性靠窗口级 Moved/Released 续跑，先把指针捕获到窗口（拖得出内容边界才收得到）
        if (_texts.IsDraggingText || _texts.IsRotatingText)
        {
            try { e.Pointer.Capture(_host.PointerCaptureTarget); } catch { }
        }
        return true;
    }

    /// <summary>
    /// Ctrl+点击且<b>未命中矢量图元</b>时再试文本层：截图侧"无工具 + Ctrl + 点文本图层 → 激活文本工具并选中"
    /// 同一动作（<c>CaptureSession</c> 的 <c>FindTextAtPoint</c> 支路）。命中即选中并进入拖动。
    /// </summary>
    private bool TryBeginTextMove(Point windowPoint, PointerPressedEventArgs e)
    {
        var canvas = _host.TextLayerCanvas;
        if (!_host.TryGetAnnotationPoint(windowPoint, out _)) return false;

        var dip = e.GetPosition(canvas);
        if (_texts.FindTextAtPoint(dip) == null) return false;

        _host.ForceToolForText();
        _texts.TryHandleTextToolInteraction(dip, e.ClickCount);
        // 选中互斥：Ctrl 点中文本层时矢量高亮一并作废，否则 Delete 的目标不唯一
        if (_texts.SelectedText != null) ClearSelection();
        if (_texts.IsDraggingText || _texts.IsRotatingText)
        {
            try { e.Pointer.Capture(_host.PointerCaptureTarget); } catch { }
        }
        return true;
    }

    /// <summary>当前有没有"看得见字"的文本层。空文本层不计入——点了一下没打字，退出后画面该原样退回。</summary>
    public bool HasAnnotationText()
    {
        foreach (var layer in _texts.TextLayers)
        {
            if (!string.IsNullOrEmpty(layer.TextBox?.Text)) return true;
        }
        return false;
    }
}
