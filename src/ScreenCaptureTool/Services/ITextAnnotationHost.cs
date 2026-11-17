using Avalonia;
using Avalonia.Controls;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 文本标注共享实现（<see cref="TextAnnotationController"/>）的宿主接口。
///
/// 文本标注不是矢量图元：它是一棵活 <see cref="TextBox"/> 可视树，长在宿主画布上，
/// 只在导出时用 GDI+ 烘进成品。截图遮罩（<c>CaptureWindow</c>）与贴图标注浮窗
/// （<c>StickerWindow</c>）共用同一份实现，两侧的差异全部收敛到本接口的成员上，
/// 共享代码里**禁止**猜坐标单位或各自发明口径。
///
/// 与 Core 里的 `AnnotationHitTest` / `BrushInputSmoother` 同一先例：
/// 截图侧算术逐位不变，贴图侧接上同一份实现。
/// </summary>
public interface ITextAnnotationHost
{
    /// <summary>文本图层挂载的画布。截图＝<c>OverlayCanvas</c>（铺满整屏，故窗口坐标即画布坐标）；贴图＝贴图专属宿主层。</summary>
    Canvas TextLayerCanvas { get; }
    Rect ErasureToTextCanvasRect(System.Drawing.RectangleF mark);

    /// <summary>
    /// 拖动 / 旋转期间指针捕获的目标。截图侧一直是<b>窗口本身</b>而不是画布（换了捕获目标就换了事件路由链），
    /// 故单列一个成员，不由 <see cref="TextLayerCanvas"/> 推断。贴图侧给贴图窗口。
    /// </summary>
    Avalonia.Input.IInputElement TextPointerCaptureTarget { get; }

    /// <summary>
    /// 在宿主的参数面板作用域内按名取控件。截图＝窗口 XAML 的 <c>TextToolsPanel</c> 命名域；贴图＝标注浮窗内的同名复刻面板。
    /// 返回类型不带 <c>?</c> 是必需的：泛型参数只有 <c>class</c> 约束时才允许写 <c>T?</c>，而 Avalonia 的
    /// <c>FindControl&lt;T&gt;</c> 要求 <c>T : Control</c>，两者不能并列。取不到时仍返回 null，
    /// 调用方一律接进可空字段并判空（与搬迁前同一写法）。
    /// </summary>
    TControl FindTextPanelControl<TControl>(string name) where TControl : Control;

    /// <summary>按当前主题态取资源（面板自建 Popup 的底色/描边用）。必须走 <c>ActualThemeVariant</c>，否则深色下解析成浅色。</summary>
    bool TryFindTextPanelResource(string key, out object? value);

    /// <summary>文本可用范围（画布 DIP）。截图＝当前选区矩形，无选区时退化为整张画布；贴图＝整张内容矩形。</summary>
    Rect GetTextValidRect();

    /// <summary>是否存在有效选区。贴图侧恒为 <see langword="true"/>（有效范围就是内容矩形）。</summary>
    bool HasTextSelection { get; }

    /// <summary>选区矩形（画布 DIP）。<see cref="HasTextSelection"/> 为假时不会被读取。</summary>
    Rect TextSelectionDip { get; }

    /// <summary>当前激活工具是否为文本工具。</summary>
    bool IsTextToolCurrent();

    /// <summary>请求把当前文本层状态压入撤销历史（截图侧抛 <c>TextSnapshotRequested</c>，贴图侧走标注会话提交链）。</summary>
    void RequestTextSnapshot();

    /// <summary>参数面板的取色按钮等标签需要重绘（截图侧＝<c>UpdateToolOptionLabels</c>）。</summary>
    void NotifyTextPanelNeedsRefresh();

    /// <summary>
    /// 全局标注色（与其他标注工具共享）。文本工具选中「文字」目标时读写它，
    /// 与其他工具改色走同一份状态，因此由宿主持有而不是控制器持有。
    /// </summary>
    System.Drawing.Color SharedAnnotationColor { get; set; }
}
