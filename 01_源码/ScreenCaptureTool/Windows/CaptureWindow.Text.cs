using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// <c>CaptureWindow</c> 的文本标注半边：实现 <see cref="ITextAnnotationHost"/> 并把窗口原有的
/// 公开文本成员一行转发给共享实现 <see cref="TextAnnotationController"/>。
///
/// 2026-09-05 的「贴图文本标注」工程第一阶段：截图侧约 1500 行文本子系统逐字搬进
/// <c>Services/TextAnnotationController.cs</c>（图层状态机）与
/// <c>Services/TextAnnotationController.TextPanel.cs</c>（参数面板），本文件只留宿主适配与转发，
/// <b>不含任何业务算术</b>。转发一律保住原名与原签名，所以 <c>Services/CaptureSession.cs</c>
/// 的 25 处文本触点一行未动。
/// </summary>
public partial class CaptureWindow : ITextAnnotationHost
{
    private TextAnnotationController? _textController;

    /// <summary>文本标注共享实现（懒建：构造早期参数面板控件尚未命名注册，建早了只会拿到一堆 null）。</summary>
    private TextAnnotationController Text => _textController ??= new TextAnnotationController(this);

    // ──────────────────────────────────────────
    // ITextAnnotationHost：宿主差异全收敛在这几行
    // ──────────────────────────────────────────

    Canvas ITextAnnotationHost.TextLayerCanvas => OverlayCanvas;
    Rect ITextAnnotationHost.ErasureToTextCanvasRect(System.Drawing.RectangleF mark)
    {
        double scale = AnnotationLayer.ScreenToDipScale;
        return new Rect((mark.X - AnnotationLayer.ScreenOrigin.X) * scale,
            (mark.Y - AnnotationLayer.ScreenOrigin.Y) * scale,
            mark.Width * scale, mark.Height * scale);
    }

    public void ApplyAnnotationErasureMasks(AnnotationDocument document)
        => Text.ApplyErasureMasks(document);

    // 截图侧遮罩铺满虚拟屏幕，指针捕获目标是窗口本身（不是画布）——换目标等于换事件路由链，故单独给出。
    IInputElement ITextAnnotationHost.TextPointerCaptureTarget => this;

    TControl ITextAnnotationHost.FindTextPanelControl<TControl>(string name)
        => this.FindControl<TControl>(name)!;

    bool ITextAnnotationHost.TryFindTextPanelResource(string key, out object? value)
        => this.TryFindResource(key, ActualThemeVariant, out value);

    Rect ITextAnnotationHost.GetTextValidRect() => GetTextValidRect();

    bool ITextAnnotationHost.HasTextSelection => _lastHasSelection;

    Rect ITextAnnotationHost.TextSelectionDip => _lastRegionDip;

    bool ITextAnnotationHost.IsTextToolCurrent() => _activeToolKey == "Text";

    // 抑制标志归控制器所有，这里只负责把事件抛给 CaptureSession。
    void ITextAnnotationHost.RequestTextSnapshot() => TextSnapshotRequested?.Invoke(this, EventArgs.Empty);

    void ITextAnnotationHost.NotifyTextPanelNeedsRefresh() => UpdateToolOptionLabels();

    System.Drawing.Color ITextAnnotationHost.SharedAnnotationColor
    {
        get => SelectedAnnotationColor;
        set => SelectedAnnotationColor = value;
    }

    // ──────────────────────────────────────────
    // 原公开成员转发（供 CaptureSession 使用，名字与签名与搬迁前一致）
    // ──────────────────────────────────────────

    /// <summary>初始化文本工具所有 UI 控件引用并绑定事件（参数面板长在窗口 XAML 里，故入口留宿主）。</summary>
    private bool _textToolControlsInitialized;
    public void InitTextToolControls()
    {
        if (_textToolControlsInitialized) return;
        Text.InitTextToolControls();
        _textToolControlsInitialized = true;
    }

    // 参数面板那五枚按钮在 CaptureWindow.axaml 里仍写死 Click="On…Click"，XAML 只认本窗口上的同名方法，故留转发。
    private void OnTextBoldClick(object? sender, RoutedEventArgs e) => Text.OnTextBoldClick(sender, e);
    private void OnTextItalicClick(object? sender, RoutedEventArgs e) => Text.OnTextItalicClick(sender, e);
    private void OnTextStrokeClick(object? sender, RoutedEventArgs e) => Text.OnTextStrokeClick(sender, e);
    private void OnTextBackgroundClick(object? sender, RoutedEventArgs e) => Text.OnTextBackgroundClick(sender, e);
    private void OnColorTargetButtonClick(object? sender, RoutedEventArgs e) => Text.OnColorTargetButtonClick(sender, e);

    public IReadOnlyList<TextLayerSnapshot> CreateTextSnapshot() => Text.CreateTextSnapshot();

    public void RestoreTextSnapshot(IReadOnlyList<TextLayerSnapshot>? snapshots) => Text.RestoreTextSnapshot(snapshots);

    public bool DeleteSelectedText() => Text.DeleteSelectedText();

    /// <summary>CaptureSession 设置文本工具是否激活</summary>
    public bool IsTextToolActive
    {
        get => Text.IsTextToolActive;
        set => Text.IsTextToolActive = value;
    }

    /// <summary>处理文本工具点击交互。返回 true 表示已处理（无需创建新文本）。</summary>
    public bool TryHandleTextToolInteraction(Point dip, int clickCount) => Text.TryHandleTextToolInteraction(dip, clickCount);

    public TextLayerInfo? FindTextAtPoint(Point dip) => Text.FindTextAtPoint(dip);


    public TextLayerInfo AddTextAt(Point dipPoint) => Text.AddTextAt(dipPoint);

    public void SelectText(TextLayerInfo info) => Text.SelectText(info);

    public void DeselectText() => Text.DeselectText();

    public void ClearAllTexts() => Text.ClearAllTexts();

    /// <summary>
    /// 「窗口里正在打字」的总闸。会话侧那一票键盘短路（Space 切工具、方向键微移、WSAD 挪光标、+/− 改编号、
    /// Delete 删图元）全读这一个属性，故任何**新出现的输入框**都必须并进来——否则在框里敲一个空格就顺手把工具切了。
    /// 第二个成员是序号浮条的说明文字框（2026-09-18），第三个是参数条上常驻的起始值输入框——
    /// 它就在工具栏条上、不是一条浮层，最容易被漏掉（框里敲一个空格照样会被遮罩当成快捷键）。
    /// 第四位是水印的内容框（2026-09-20），形态与起始值那格一模一样：常驻条上、直接打字。
    /// </summary>
    public bool HasActiveTextEditing => Text.HasActiveTextEditing || CounterPillHost.IsEditingNote
        || (_counterOptions?.IsEditingStartValue ?? false) || (_watermarkOptions?.IsEditingContent ?? false);

    public void EndActiveTextEditing(string reason = "Unknown") => Text.EndActiveTextEditing(reason);

    public TextLayerInfo? FindTextInfoFromSource(object? source) => Text.FindTextInfoFromSource(source);

    public TextLayerInfo? SelectedText => Text.SelectedText;

    public double CurrentTextSize
    {
        get => Text.CurrentTextSize;
        set => Text.CurrentTextSize = value;
    }

    public double TextStrokeThickness
    {
        get => Text.TextStrokeThickness;
        set => Text.TextStrokeThickness = value;
    }

    public double TextFillOpacity
    {
        get => Text.TextFillOpacity;
        set => Text.TextFillOpacity = value;
    }

    public double TextFillCornerRadius
    {
        get => Text.TextFillCornerRadius;
        set => Text.TextFillCornerRadius = value;
    }

    public double TextFillPadding
    {
        get => Text.TextFillPadding;
        set => Text.TextFillPadding = value;
    }

    public bool IsDraggingText => Text.IsDraggingText;
    public bool IsRotatingText => Text.IsRotatingText;

    public IReadOnlyList<TextLayerInfo> TextLayers => Text.TextLayers;

    public Color ForegroundTextColor
    {
        get => Text.ForegroundTextColor;
        set => Text.ForegroundTextColor = value;
    }

    public Color TextFillColor => Text.TextFillColor;
    public Color TextStrokeColor => Text.TextStrokeColor;

    public int ColorTargetMode
    {
        get => Text.ColorTargetMode;
        set => Text.ColorTargetMode = value;
    }

    public bool HasStroke => Text.HasStroke;
    public bool HasBackground => Text.HasBackground;
}
