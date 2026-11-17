using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 文本标注共享实现：活 TextBox 可视树的建层 / 编辑 / 选中 / 拖动 / 旋转 / 滚轮字号 /
/// 删除 / 清空 / 快照，以及参数面板（字号·粗斜·描边·底色）的绑定与刷新。
///
/// 由 <c>CaptureWindow</c>（截图遮罩）的文本子系统**逐字搬来**，宿主差异全部走
/// <see cref="ITextAnnotationHost"/>。除下列替换外，算术与控制流一位未改：
/// <list type="bullet">
/// <item><c>OverlayCanvas</c> → <c>_host.TextLayerCanvas</c></item>
/// <item><c>_lastHasSelection</c> / <c>_lastRegionDip</c> → <c>_host.HasTextSelection</c> / <c>_host.TextSelectionDip</c></item>
/// <item><c>_activeToolKey == "Text"</c> → <c>_host.IsTextToolCurrent()</c></item>
/// <item><c>this.FindControl&lt;T&gt;</c> / <c>TryFindResource(k, ActualThemeVariant, out v)</c> → 宿主代理（贴图侧参数面板在浮窗命名域内）</item>
/// <item><c>UpdateToolOptionLabels()</c> → <c>_host.NotifyTextPanelNeedsRefresh()</c></item>
/// <item><c>SelectedAnnotationColor</c> 的写入 → <c>_host.SharedAnnotationColor</c>（全局标注色由宿主持有）</item>
/// </list>
/// 原方法在截图侧多为 <c>private</c>，此处放宽为 <c>internal</c> 以便宿主转发；访问级别不影响行为。
/// 日志前缀保留 <c>CaptureWindow.</c> 原文（同一份实现同一套日志串，贴图侧同源）。
/// </summary>
public sealed partial class TextAnnotationController
{
    private readonly ITextAnnotationHost _host;

    public TextAnnotationController(ITextAnnotationHost host)
    {
        _host = host;
    }

    // ==========================================
    // 图层状态机
    // ==========================================

    private bool _isTextToolActive; // CaptureSession 设置此标志来激活文本工具
    private double _currentTextSize = 24;
    private bool _isTextBold = false;
    private bool _isTextItalic = false;
    private bool _hasStroke = false;     // 新建文本默认不启用描边；用户开启后记忆当前状态
    private bool _hasBackground = false; // 新建文本默认不启用填充；用户开启后记忆当前状态
    private double _textStrokeThickness = 4.0;
    private string _currentTextFont = "Microsoft YaHei UI";
    private Color _textStrokeColor = Colors.White;
    private Color _textFillColor = Color.FromRgb(255, 204, 0);
    private Color _textForegroundColor = Color.FromRgb(51, 136, 255); // 与 SelectedAnnotationColor 默认值保持一致
    private double _textFillOpacity = 100;
    private double _textFillCornerRadius = 4;
    private double _textFillPadding = 4;
    private int _colorTargetMode = 0; // 0=Text, 1=Background, 2=Stroke
    private TextLayerInfo? _selectedText;
    private bool _isDraggingSelectedText;
    private bool _isRotatingText;
    private TextLayerInfo? _rotatingTextInfo;
    private Point _rotateStartPoint;
    private double _rotateStartAngle;
    private Point _textDragStartPoint;
    private Point _textDragStartPosition;
    private bool _textCtrlKeyDown;
    private List<TextLayerInfo> _textLayers = new();
    private bool _suppressTextSnapshotEvents;
    private bool _hasPendingTextEditSnapshot;

    // UI 控件引用（在 TextToolsPanel 初始化时填充）
    private Button? _btnTextBold;
    private Button? _btnTextItalic;
    private Button? _btnTextStroke;
    private Button? _btnTextBackground;
    private Button? _btnTargetStrokeColor;
    private Button? _btnTargetStrokeText;
    private Button? _btnTargetBackgroundColor;
    private Button? _btnTargetBackgroundText;
    private Popup? _textStrokePopup;
    private Popup? _textBackgroundPopup;
    private MenuFlyout? _textSizeMenu;
    private TextBlock? _textSizeValueText;
    private Grid? _textSizeHost;
    private TextBlock? _textStrokeSizeValue;
    private TextBlock? _textFillOpacityValue;
    private TextBlock? _textFillCornerValue;
    private TextBlock? _textFillPaddingValue;
    private CheckBox? _chkStrokeEnable;
    private CheckBox? _chkBackgroundEnable;
    private Slider? _sliderStrokeSize;
    private Slider? _sliderFillOpacity;
    private Slider? _sliderFillCorner;
    private Slider? _sliderFillPadding;
    private StackPanel? _textToolsPanel;

    // ==========================================
    // 文本工具实现
    // ==========================================

    internal void RequestTextSnapshot()
    {
        if (_suppressTextSnapshotEvents) return;
        _host.RequestTextSnapshot();
    }

    public IReadOnlyList<TextLayerSnapshot> CreateTextSnapshot()
    {
        // 字段映射统一走 TextLayerSnapshot.FromLayer，与导出烘焙链共用同一份读取式。
        return _textLayers.Select(info => TextLayerSnapshot.FromLayer(info)).ToList();
    }

    /// <summary>最近一次同步擦除区时用的那份文档，供"选中/取消选中"就地重算裁切（显形判据要跟着选中态变）。</summary>
    private AnnotationDocument? _erasureSource;
    private readonly Dictionary<Guid, ErasureClipCache> _textErasureCaches = new();

    /// <summary>把文档里的擦除区域同步到活文字控件，拖动中逐帧生效。
    /// <b>当前被选中的那一层一律不裁</b>：与标注图元同一套"编辑期临时显形"口径（取消选中即回缩），
    /// 导出走 <see cref="TextLayerRenderer"/>，不读这里，成品仍以擦除为准。</summary>
    public void ApplyErasureMasks(AnnotationDocument document)
    {
        _erasureSource = document;
        ErasureShape[] erasures = document.Shapes.OfType<ErasureShape>().ToArray();
        var textIds = _textLayers.Select(info => info.Id).ToHashSet();
        foreach (Guid id in _textErasureCaches.Keys.Where(id => !textIds.Contains(id)).ToArray())
            _textErasureCaches.Remove(id);
        if (erasures.Length == 0) _textErasureCaches.Clear();
        foreach (TextLayerInfo info in _textLayers)
        {
            if (ReferenceEquals(info, _selectedText))
            {
                info.Root.Clip = null;
                continue;
            }
            if (!erasures.Any(erase => erase.AffectsText(info.Id) && erase.Marks.Count > 0))
            {
                info.Root.Clip = null;
                _textErasureCaches.Remove(info.Id);
                continue;
            }
            double left = Canvas.GetLeft(info.Root), top = Canvas.GetTop(info.Root);
            double layerWidth = Math.Max(1, info.Root.Bounds.Width), layerHeight = Math.Max(1, info.Root.Bounds.Height);
            var layerRect = new Rect(0, 0, layerWidth, layerHeight);
            // 未布局/刚收起时 Bounds 是空的（项目已知陷阱：量到 0×0），这时按 1×1 裁剪会把所有洞都筛掉、
            // 结果等于没擦。尺寸可疑就不裁剪，照旧全量建洞。
            bool cull = layerWidth > 1 && layerHeight > 1;
            // 宿主的坐标换算均为缩放＋平移，从单位矩形取得同一变换，再叠本层的逆旋转。
            Rect unit = _host.ErasureToTextCanvasRect(new System.Drawing.RectangleF(0, 0, 1, 1));
            var transform = new Matrix(unit.Width, 0, 0, unit.Height, unit.X - left, unit.Y - top);
            if (Math.Abs(info.RotationAngle) > 0.01)
                transform *= new RotateTransform(-info.RotationAngle, layerWidth / 2.0, layerHeight / 2.0).Value;
            if (!_textErasureCaches.TryGetValue(info.Id, out ErasureClipCache? cache))
                _textErasureCaches[info.Id] = cache = new ErasureClipCache();
            cache.Configure(layerRect, transform);
            cache.Retain(erasures);
            Geometry[] clips = cache.GetClips(erasures, erase => erase.AffectsText(info.Id), cull ? layerRect : null);
            // Control.Clip 只接一份几何；没影响本层的新增落点不会重建这份交集。
            info.Root.Clip = cache.Combine(clips);
        }
    }

    public void RestoreTextSnapshot(IReadOnlyList<TextLayerSnapshot>? snapshots)
    {
        _suppressTextSnapshotEvents = true;
        try
        {
            foreach (var info in _textLayers.ToList())
            {
                if (info.Root != null && _host.TextLayerCanvas.Children.Contains(info.Root))
                    _host.TextLayerCanvas.Children.Remove(info.Root);
            }
            _textLayers.Clear();
            _selectedText = null;
            _isDraggingSelectedText = false;
            _isRotatingText = false;
            _rotatingTextInfo = null;

            if (snapshots == null) return;
            foreach (var snapshot in snapshots)
            {
                var info = CreateTextLayer(restoredText: snapshot.Text);
                info.Id = snapshot.Id;
                info.TextBox.Text = snapshot.Text;
                Canvas.SetLeft(info.Root, snapshot.Left);
                Canvas.SetTop(info.Root, snapshot.Top);
                if (snapshot.MaxWidth > 0) info.TextBox.MaxWidth = snapshot.MaxWidth;
                if (snapshot.LineHeight > 0) info.TextBox.LineHeight = snapshot.LineHeight;
                info.FontSize = snapshot.FontSize;
                info.IsBold = snapshot.IsBold;
                info.IsItalic = snapshot.IsItalic;
                info.HasStroke = snapshot.HasStroke;
                info.HasBackground = snapshot.HasBackground;
                info.FontFamily = snapshot.FontFamily;
                info.Color = snapshot.Color;
                info.FillColor = snapshot.FillColor;
                info.StrokeColor = snapshot.StrokeColor;
                info.StrokeThickness = snapshot.StrokeThickness;
                info.FillOpacity = snapshot.FillOpacity;
                info.FillCornerRadius = snapshot.FillCornerRadius;
                info.FillPadding = snapshot.FillPadding;
                info.RotationAngle = snapshot.RotationAngle;
                ApplyTextRotation(info);
                UpdateTextView(info);
                _host.TextLayerCanvas.Children.Add(info.Root);
            }

            // 🔴 重建完整排一次布局（2026-09-29）：撤销／重做走的是"把文字层全拆掉再按快照重建"，新根节点在**被量到之前**
            // `Bounds` 恒为 0×0，而取图烘焙往往就在同一轮 dispatcher 里紧跟着跑（用户实报「没确认文字就双击完成截图，
            // 成品只剩第一个字」——那次存档里 `text="哈哈哈哈"` 完好，但 `measuredWidth=0`、`measuredHeight=0`）。
            // 烘焙读的就是这个实测宽高（`TextLayerSnapshot.FromLayer` ← `Root.Bounds`），0 会被
            // `TextLayerRenderer` 兜成 1 像素宽的排版框 ⇒ 一行只塞得下一个字。截图／贴图／录屏三处共用本方法，一处收口三处都得。
            // 与编辑期 `TextChanged` 里那三句同一个配方（失效→失效→立刻量）。
            var canvas = _host.TextLayerCanvas;
            canvas.InvalidateMeasure();
            canvas.InvalidateArrange();
            canvas.UpdateLayout();
        }
        finally
        {
            _suppressTextSnapshotEvents = false;
        }
    }

    public bool DeleteSelectedText()
    {
        if (_selectedText == null) return false;
        DeleteTextLayer(_selectedText);
        return true;
    }

    /// <summary>CaptureSession 设置文本工具是否激活</summary>
    public bool IsTextToolActive
    {
        get => _isTextToolActive;
        set
        {
            _isTextToolActive = value;
            if (!value)
            {
                CloseTextPopups();
                EndActiveTextEditing("ToolDeactivated");
            }
        }
    }

    /// <summary>处理文本工具点击交互。返回 true 表示已处理（无需创建新文本）。</summary>
    public bool TryHandleTextToolInteraction(Point dip, int clickCount)
    {
        // 在点击位置查找已有文本
        // 注意：这里不能直接用 FindTextInfoFromSource，因为事件源来自 CaptureSession
        // 需要通过坐标查找
        TextLayerInfo? hit = FindTextAtPoint(dip);
        if (hit != null)
        {
            if (!_host.IsTextToolCurrent() || _selectedText != hit)
            {
                SelectText(hit);
            }

            if (clickCount >= 2 && hit.TextBox != null)
            {
                // 双击 → 进入编辑模式
                if (!_hasPendingTextEditSnapshot)
                {
                    RequestTextSnapshot();
                    _hasPendingTextEditSnapshot = true;
                }
                BeginTextEditing(hit);
            }
            else
            {
                // 旧版语义：双击才编辑，单击永远移动图层。
                // 如果当前 TextBox 正在编辑，单击移动前先结束编辑，避免 TextBox 抢输入焦点/选择逻辑。
                if (hit.TextBox != null)
                {
                    hit.TextBox.IsHitTestVisible = false;
                    _hasPendingTextEditSnapshot = false;
                    _host.TextLayerCanvas.Focus();
                }

                // 单击 → 开始拖拽。后续移动只做纯平移，不再改文本 MaxWidth。
                RequestTextSnapshot();
                _isDraggingSelectedText = true;
                _textDragStartPoint = dip;
                double startLeft = Canvas.GetLeft(hit.Root);
                double startTop = Canvas.GetTop(hit.Root);
                if (double.IsNaN(startLeft)) startLeft = 0;
                if (double.IsNaN(startTop)) startTop = 0;
                _textDragStartPosition = new Point(startLeft, startTop);
            }
            return true; // 已处理
        }
        return false; // 空白区域，让 CaptureSession 创建新文本
    }

    /// <summary>通过坐标查找文本图层（供 CaptureSession 在其他标注工具下 Ctrl+Click 切换使用）</summary>
    public TextLayerInfo? FindTextAtPoint(Point dip)
    {
        // 从后往前遍历（后添加的在上面），找到命中测试通过的文本
        for (int i = _textLayers.Count - 1; i >= 0; i--)
        {
            var info = _textLayers[i];
            if (info.Root == null) continue;
            double left = Canvas.GetLeft(info.Root);
            double top = Canvas.GetTop(info.Root);
            double w = info.Root.Bounds.Width;
            double h = info.Root.Bounds.Height;
            if (w <= 0) w = 80;  // fallback for unmeasured
            if (h <= 0) h = 30;
            Point local = ToTextLocalPoint(info, dip, left, top, w, h);
            if (local.X >= 0 && local.X <= w && local.Y >= 0 && local.Y <= h)
            {
                return info;
            }
        }
        return null;
    }

    internal static Point ToTextLocalPoint(TextLayerInfo info, Point dip, double left, double top, double width, double height)
    {
        double x = dip.X - left;
        double y = dip.Y - top;
        double angle = info.RotationAngle;
        if (Math.Abs(angle) < 0.01) return new Point(x, y);

        double cx = width / 2.0;
        double cy = height / 2.0;
        double radians = -angle * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double dx = x - cx;
        double dy = y - cy;
        return new Point(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    internal void ApplyTextRotation(TextLayerInfo info)
    {
        double w = Math.Max(1, info.Root.Bounds.Width);
        double h = Math.Max(1, info.Root.Bounds.Height);
        info.Root.RenderTransform = new RotateTransform(info.RotationAngle, w / 2.0, h / 2.0);
        info.Root.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
    }

    internal void MoveSelectedTextWithinBounds(TextLayerInfo info, Point currentPos)
    {
        if (info.Root == null || info.TextBox == null) return;

        Rect valid = _host.GetTextValidRect();
        double dx = currentPos.X - _textDragStartPoint.X;
        double dy = currentPos.Y - _textDragStartPoint.Y;
        double candidateLeft = _textDragStartPosition.X + dx;
        double candidateTop = _textDragStartPosition.Y + dy;

        // 移动文本必须是纯平移：不能在拖动过程中修改 MaxWidth。
        // 否则 TextBox 会重新排版，Root.Bounds / 命中框 / 选中框都会变化，表现为拖动时文本框变大或跳动。
        info.Root.UpdateLayout();
        double width = info.Root.Bounds.Width;
        double height = info.Root.Bounds.Height;
        if (width <= 0) width = Math.Max(info.TextBox.Bounds.Width, 40.0);
        if (height <= 0) height = Math.Max(info.TextBox.Bounds.Height, info.FontSize * 1.35);

        if (width <= valid.Width)
            candidateLeft = Math.Clamp(candidateLeft, valid.Left, valid.Right - width);
        else
            candidateLeft = valid.Left;

        if (height <= valid.Height)
            candidateTop = Math.Clamp(candidateTop, valid.Top, valid.Bottom - height);
        else
            candidateTop = valid.Top;

        Canvas.SetLeft(info.Root, candidateLeft);
        Canvas.SetTop(info.Root, candidateTop);
        UpdateTextSelectionFrame(info);
    }

    // ──────────────────────────────────────────
    // 全局指针事件（仅处理拖拽/旋转的连续性）
    // ──────────────────────────────────────────

    internal void OnTextEditingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            _textCtrlKeyDown = true;
            return;
        }

        bool isEnter = e.Key == Key.Enter || e.Key == Key.Return;
        if (!isEnter) return;

        bool ctrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control) || _textCtrlKeyDown;
        if (!HasActiveTextEditing)
        {
            AppLogger.Info("CaptureWindow.TextKeyDown: Enter ignored, no active text editing, ctrl=" + ctrlPressed + ", keyModifiers=" + e.KeyModifiers + ", handled=" + e.Handled);
            return;
        }

        AppLogger.Info("CaptureWindow.TextKeyDown: Enter while editing, ctrl=" + ctrlPressed + ", keyModifiers=" + e.KeyModifiers + ", handled=" + e.Handled + ", selectedText=" + (_selectedText != null) + ", textLayers=" + _textLayers.Count);
        if (!ctrlPressed) return;

        EndActiveTextEditing("WindowKeyDown");
        e.Handled = true;
    }

    internal void OnTextEditingKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            _textCtrlKeyDown = false;
    }

    // ──────────────────────────────────────────
    // 文本图层核心操作
    // ──────────────────────────────────────────

    /// <summary>创建文本 UI 树 — 完全对齐旧版 CreateTextLayer</summary>
    internal TextLayerInfo CreateTextLayer(string? restoredText = null)
    {
        Grid root = new Grid
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = true
        };

        Border bg = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var strokePath = new Avalonia.Controls.Shapes.Path
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        TextBox tb = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            // 标注版面按文字行高排布，不继承普通输入框的 32 DIP 最小高度与垂直居中。
            MinHeight = 0,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top,
            AcceptsReturn = true,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.Wrap,
            ContextMenu = null,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        tb.Classes.Add("captureTextLayer");
        tb.Resources["TextControlBorderBrush"] = Brushes.Transparent;
        tb.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
        tb.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent;
        tb.Resources["TextControlBackground"] = Brushes.Transparent;
        tb.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        tb.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;

        // 文本框自身事件：右键完成编辑；左键单击按旧版语义移动图层，双击才编辑。
        // Ctrl+点击时允许事件冒泡，以便 CaptureSession 处理工具切换。
        tb.PointerPressed += (s, args) =>
        {
            var point = args.GetCurrentPoint(_host.TextLayerCanvas);
            if (point.Properties.IsRightButtonPressed)
            {
                tb.IsHitTestVisible = false;
                _hasPendingTextEditSnapshot = false;
                _host.TextLayerCanvas.Focus();
                args.Handled = true;
                return;
            }

            // 如果按下 Ctrl，不拦截事件，让 CaptureSession 处理工具切换
            if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return;
            }

            if (point.Properties.IsLeftButtonPressed && args.ClickCount < 2)
            {
                Point dip = args.GetPosition(_host.TextLayerCanvas);
                TryHandleTextToolInteraction(dip, args.ClickCount);
                args.Pointer.Capture(_host.TextPointerCaptureTarget);
                args.Handled = true;
            }
        };

        // 选中虚线框
        Border selBorder = new Border
        {
            BorderThickness = new Thickness(0),
            Background = null,
            IsHitTestVisible = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(-2, -2)
        };

        var selRect = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(30, 144, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 }
        };
        selBorder.Child = selRect;

        // 装饰层
        Grid adornerLayer = new Grid
        {
            IsVisible = false,
            IsHitTestVisible = true
        };

        // 旋转手柄 (左上)
        Ellipse rotateHandle = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(-6, -6, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "RotateHandle"
        };
        ToolTip.SetTip(rotateHandle, "旋转");

        // 删除手柄 (右下)
        Border deleteHandle = new Border
        {
            Width = 16,
            Height = 16,
            Background = new SolidColorBrush(Color.FromRgb(255, 85, 85)),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -8, -8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "DeleteHandle"
        };
        ToolTip.SetTip(deleteHandle, "删除");

        var xIcon = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        deleteHandle.Child = xIcon;

        adornerLayer.Children.Add(rotateHandle);
        adornerLayer.Children.Add(deleteHandle);

        root.Children.Add(bg);
        root.Children.Add(strokePath);
        root.Children.Add(tb);
        root.Children.Add(selBorder);
        root.Children.Add(adornerLayer);

        // ★ 关键：在注册任何事件之前先创建 info 并设置 Tag
        TextLayerInfo info = new TextLayerInfo();
        info.Root = root;
        info.TextBox = tb;
        info.BackgroundBorder = bg;
        info.StrokePath = strokePath;
        info.SelectionBorder = selBorder;
        info.AdornerLayer = adornerLayer;
        info.RotateHandle = rotateHandle;
        info.DeleteHandle = deleteHandle;

        root.Tag = info; // ★ 必须在事件注册前设置，因为事件内部需要用到

        // 失焦结束编辑
        tb.LostFocus += (s, args) =>
        {
            tb.IsHitTestVisible = false;
            _hasPendingTextEditSnapshot = false;
        };

        tb.GotFocus += (s, args) =>
        {
            if (!_hasPendingTextEditSnapshot)
            {
                RequestTextSnapshot();
                _hasPendingTextEditSnapshot = true;
            }
        };

        // Ctrl+Enter 完成编辑。用 Tunnel 优先级拦截，避免 TextBox 的 AcceptsReturn 先把 Enter 当换行吃掉。
        tb.AddHandler(InputElement.KeyDownEvent, (s, args) =>
        {
            if ((args.Key == Key.Enter || args.Key == Key.Return) && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                AppLogger.Info("CaptureWindow.TextCtrlEnter: TextBox 提交文本编辑，selectedText=" + (_selectedText != null) + ", textLayers=" + _textLayers.Count);
                EndActiveTextEditing("TextBoxKeyDown");
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        // TextChanged 可延后派发，不能只靠 RestoreTextSnapshot 的同步抑制标记。
        // 恢复值的首次通知只做排版；若文字已被用户改过，照常执行防越界。
        bool initialRestoreTextChange = restoredText != null;
        // 文本变化 → 先刷新布局，再更新描边/背景/选中框，避免最后一个字超出输入框
        tb.TextChanged += (s, args) =>
        {
            bool restoredValue = initialRestoreTextChange && tb.Text == restoredText;
            initialRestoreTextChange = false;
            if (root != null)
            {
                root.InvalidateMeasure();
                root.InvalidateArrange();
                root.UpdateLayout();
            }

            UpdateTextGeometry(info);
            UpdateTextBackgroundFrame(info);
            UpdateTextSelectionFrame(info);

            if (!restoredValue && !_suppressTextSnapshotEvents && root != null && _host.TextLayerCanvas != null)
            {
                double ct = Canvas.GetTop(root);
                double ch = root.Bounds.Height;
                double maxY = _host.TextLayerCanvas.Bounds.Height;
                if (maxY > 0 && ch > 0 && ct + ch > maxY)
                {
                    double nt = maxY - ch;
                    if (nt < 0) nt = 0;
                    Canvas.SetTop(root, nt);
                }
            }
        };

        // 旋转手柄事件
        rotateHandle.PointerPressed += (s, args) =>
        {
            RequestTextSnapshot();
            _isRotatingText = true;
            _rotatingTextInfo = info;
            _rotateStartPoint = args.GetPosition(_host.TextLayerCanvas);
            _rotateStartAngle = info.RotationAngle;
            try { args.Pointer.Capture(_host.TextPointerCaptureTarget); } catch { }
            args.Handled = true;
        };

        // 删除手柄事件
        deleteHandle.PointerPressed += (s, args) =>
        {
            RequestTextSnapshot();
            DeleteTextLayer(info);
            args.Handled = true;
        };

        // 每个文本元素的滚轮缩放（含越界保护）— 对齐旧版 root.PreviewMouseWheel。
        // 同时挂到 TextBox，本体编辑态时滚轮可能被 TextBox 先接收，不能只依赖 root 冒泡。
        root.PointerWheelChanged += (s, args) => AdjustTextFontSizeByWheel(info, args, "RootWheel");
        tb.PointerWheelChanged += (s, args) => AdjustTextFontSizeByWheel(info, args, "TextBoxWheel");

        // 快照恢复必须保留原位置：隐藏画布首次展开时 Bounds.Height 可能还是 0，
        // 第一轮尺寸变化只是排版，不能当成用户改字／改字号后的底部越界。
        bool preserveInitialPosition = restoredText != null;
        // 尺寸变化 → 同步输入框视觉范围，并自动上推防溢出（对齐旧版 root.SizeChanged）
        root.SizeChanged += (s, args) =>
        {
            UpdateTextBackgroundFrame(info);
            UpdateTextSelectionFrame(info);

            if (preserveInitialPosition)
            {
                preserveInitialPosition = false;
                return;
            }
            if (!_suppressTextSnapshotEvents && root != null && _host.TextLayerCanvas != null)
            {
                double ct = Canvas.GetTop(root);
                double ch = root.Bounds.Height;
                double maxY = _host.TextLayerCanvas.Bounds.Height;
                if (maxY > 0 && ch > 0 && ct + ch > maxY)
                {
                    double nt = maxY - ch;
                    if (nt < 0) nt = 0;
                    Canvas.SetTop(root, nt);
                }
            }
        };

        // 应用默认属性
        info.FontSize = _currentTextSize;
        info.IsBold = _isTextBold;
        info.IsItalic = _isTextItalic;
        info.FontFamily = _currentTextFont;
        info.HasStroke = _hasStroke;
        info.HasBackground = _hasBackground;

        if (!_hasStroke && !_hasBackground)
            info.Color = _textForegroundColor;
        else
            info.Color = _textForegroundColor;

        info.FillColor = _textFillColor;
        info.StrokeColor = _textStrokeColor;
        info.StrokeThickness = _textStrokeThickness;
        info.FillOpacity = _textFillOpacity;
        info.FillCornerRadius = _textFillCornerRadius;
        info.FillPadding = _textFillPadding;

        _textLayers.Add(info);

        return info;
    }

    internal static double GetTextRightSafetyMargin(TextLayerInfo info)
    {
        double margin = 10.0;
        if (info.HasStroke) margin += Math.Max(0.0, info.StrokeThickness);
        if (info.HasBackground) margin += Math.Max(0.0, info.FillPadding);
        if (info.IsItalic) margin += 10.0;
        return margin;
    }

    /// <summary>在指定屏幕位置添加新文本</summary>
    public TextLayerInfo AddTextAt(Point dipPoint)
    {
        TextLayerInfo info = CreateTextLayer();
        Canvas.SetLeft(info.Root, dipPoint.X);
        Canvas.SetTop(info.Root, dipPoint.Y);
        _host.TextLayerCanvas.Children.Add(info.Root);
        _host.TextLayerCanvas.ZIndex = 1000;

        // ★ 关键：限制 MaxWidth 到当前选区边界，实现自动换行
        double rightSafetyMargin = GetTextRightSafetyMargin(info);
        if (_host.HasTextSelection)
        {
            double regionRight = _host.TextSelectionDip.X + _host.TextSelectionDip.Width;
            double maxWidth = regionRight - dipPoint.X - rightSafetyMargin;
            if (maxWidth < 20) maxWidth = 20;
            info.TextBox.MaxWidth = maxWidth;
        }
        else
        {
            double regionRight = Math.Max(_host.TextLayerCanvas.Bounds.Width, 100);
            double maxWidth = regionRight - dipPoint.X - rightSafetyMargin;
            if (maxWidth < 20) maxWidth = 20;
            info.TextBox.MaxWidth = maxWidth;
        }

        UpdateTextView(info);

        // 自动进入编辑模式。聚焦延迟到布局后执行，避免连续创建文本时焦点仍停在旧 TextBox/窗口上。
        SelectText(info);
        BeginTextEditing(info);

        return info;
    }

    /// <summary>更新文本 UI — 完全对齐旧版 UpdateTextView</summary>
    internal void UpdateTextView(TextLayerInfo info)
    {
        if (info == null || info.TextBox == null) return;

        info.TextBox.FontSize = info.FontSize;
        info.TextBox.FontWeight = info.IsBold ? FontWeight.Bold : FontWeight.Normal;
        info.TextBox.FontStyle = info.IsItalic ? FontStyle.Italic : FontStyle.Normal;

        if (!string.IsNullOrEmpty(info.FontFamily))
        {
            try { info.TextBox.FontFamily = new FontFamily(info.FontFamily); } catch { }
        }

        double lineHeight = info.FontSize * 1.35;
        info.TextBox.LineHeight = lineHeight;

        info.TextBox.Foreground = new SolidColorBrush(info.Color);

        if (info.BackgroundBorder != null)
        {
            if (info.HasBackground)
            {
                var bgBrush = new SolidColorBrush(info.FillColor);
                bgBrush.Opacity = info.FillOpacity / 100.0;
                info.BackgroundBorder.Background = bgBrush;
                info.BackgroundBorder.Padding = new Thickness(0);
                info.BackgroundBorder.CornerRadius = new CornerRadius(info.FillCornerRadius);

                var margin = new Thickness(info.FillPadding);
                info.TextBox.Margin = margin;
                info.TextBox.Padding = info.IsItalic
                    ? new Thickness(0, 0, 10, 0)
                    : new Thickness(0);

                if (info.StrokePath != null)
                    info.StrokePath.Margin = margin;
            }
            else
            {
                info.BackgroundBorder.Background = Brushes.Transparent;
                info.BackgroundBorder.Padding = new Thickness(0);
                info.BackgroundBorder.CornerRadius = new CornerRadius(0);
                info.TextBox.Margin = new Thickness(0);
                info.TextBox.Padding = info.IsItalic
                    ? new Thickness(0, 0, 10, 0)
                    : new Thickness(0);

                if (info.StrokePath != null)
                    info.StrokePath.Margin = new Thickness(0);
            }
        }

        if (info.HasStroke && info.StrokePath != null)
        {
            Color sc = info.StrokeColor;

            info.StrokePath.IsVisible = true;
            info.StrokePath.Stroke = new SolidColorBrush(sc);
            info.StrokePath.StrokeThickness = AnnotationRenderOptions.GetTextStrokeRenderThickness(info.StrokeThickness, info.FontSize);
            info.StrokePath.StrokeLineCap = PenLineCap.Round;
            info.StrokePath.StrokeJoin = PenLineJoin.Round;

            UpdateTextGeometry(info);
        }
        else if (info.StrokePath != null)
        {
            info.StrokePath.IsVisible = false;
        }

        try { UpdateTextBackgroundFrame(info); } catch { }
        try { UpdateTextSelectionFrame(info); } catch { }
    }

    internal void UpdateTextBackgroundFrame(TextLayerInfo info)
    {
        if (info?.BackgroundBorder == null || info.TextBox == null) return;

        Thickness margin = info.TextBox.Margin;
        double w = info.TextBox.Bounds.Width;
        double h = info.TextBox.Bounds.Height;
        if (w <= 0) w = info.TextBox.DesiredSize.Width;
        if (h <= 0) h = info.TextBox.DesiredSize.Height;
        if (w <= 0) w = Math.Max(40.0, info.FontSize * 2.0);
        if (h <= 0) h = Math.Max(20.0, info.FontSize * 1.35);

        info.BackgroundBorder.Width = Math.Max(1.0, w + margin.Left + margin.Right);
        info.BackgroundBorder.Height = Math.Max(1.0, h + margin.Top + margin.Bottom);
    }

    /// <summary>更新描边路径几何体 — 使用 Avalonia FormattedText</summary>
    internal void UpdateTextGeometry(TextLayerInfo info)
    {
        if (info == null || info.TextBox == null || info.StrokePath == null) return;

        try
        {
            string text = info.TextBox.Text ?? "";
            if (string.IsNullOrEmpty(text))
            {
                info.StrokePath.Data = null;
                return;
            }

            double effectiveMaxWidth = 0;
            if (info.TextBox.MaxWidth > 0 && !double.IsInfinity(info.TextBox.MaxWidth))
            {
                double hPadding = info.TextBox.Padding.Left + info.TextBox.Padding.Right;
                double hBorder = info.TextBox.BorderThickness.Left + info.TextBox.BorderThickness.Right;
                effectiveMaxWidth = info.TextBox.MaxWidth - hPadding - hBorder;
                if (effectiveMaxWidth < 0) effectiveMaxWidth = 0;
            }

            double offsetX = info.TextBox.Padding.Left + info.TextBox.BorderThickness.Left;
            double offsetY = info.TextBox.Padding.Top + info.TextBox.BorderThickness.Top;

            var fontFamily = FontFamily.Parse(info.FontFamily ?? "Microsoft YaHei UI");
            var fontWeight = info.IsBold ? FontWeight.Bold : FontWeight.Normal;
            var fontStyle = info.IsItalic ? FontStyle.Italic : FontStyle.Normal;
            var typeface = new Typeface(fontFamily, fontStyle, fontWeight);

            var formattedText = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                info.FontSize,
                Brushes.Black);

            if (effectiveMaxWidth > 0)
                formattedText.MaxTextWidth = effectiveMaxWidth;

            formattedText.TextAlignment = TextAlignment.Left;
            formattedText.Trimming = TextTrimming.None;
            formattedText.LineHeight = info.TextBox.LineHeight > 0 ? info.TextBox.LineHeight : info.FontSize * 1.35;

            var geometry = formattedText.BuildGeometry(new Avalonia.Point(offsetX, offsetY));

            info.StrokePath.Data = geometry;

            try { UpdateTextSelectionFrame(info); } catch { }
        }
        catch { }
    }

    /// <summary>更新选中框位置</summary>
    internal void UpdateTextSelectionFrame(TextLayerInfo info)
    {
        if (info == null || info.SelectionBorder == null || info.TextBox == null) return;

        try
        {
            if (!info.SelectionBorder.IsVisible) return;

            // 不能用 Root.Bounds：SelectionBorder 自己也是 Root 的子元素。
            // 如果用 Root.Bounds 再设置 SelectionBorder.Width = w + 4，会每次更新都反向撑大 Root，导致虚线框无限放大。
            Thickness margin = info.TextBox.Margin;
            double w = info.TextBox.Bounds.Width;
            double h = info.TextBox.Bounds.Height;
            if (w <= 0) w = info.TextBox.DesiredSize.Width;
            if (h <= 0) h = info.TextBox.DesiredSize.Height;
            if (w <= 0) w = Math.Max(40.0, info.FontSize * 2.0);
            if (h <= 0) h = Math.Max(20.0, info.FontSize * 1.35);

            w += margin.Left + margin.Right;
            h += margin.Top + margin.Bottom;

            double frameWidth = Math.Max(1.0, w + 4.0);
            double frameHeight = Math.Max(1.0, h + 4.0);

            info.SelectionBorder.Width = frameWidth;
            info.SelectionBorder.Height = frameHeight;

            if (info.SelectionBorder.Child is Avalonia.Controls.Shapes.Rectangle rect)
            {
                rect.Width = frameWidth;
                rect.Height = frameHeight;
            }
        }
        catch { }
    }

    /// <summary>选中文本 — 同步全局状态到 UI（对齐旧版）</summary>
    public void SelectText(TextLayerInfo info)
    {
        if (info == null) return;
        DeselectText();

        _selectedText = info;

        if (_selectedText.SelectionBorder != null)
            _selectedText.SelectionBorder.IsVisible = true;

        if (_selectedText.AdornerLayer != null)
        {
            if (string.IsNullOrEmpty(_selectedText.TextBox?.Text))
                _selectedText.AdornerLayer.IsVisible = false;
            else
                _selectedText.AdornerLayer.IsVisible = true;
        }

        // 同步全局状态
        _currentTextSize = info.FontSize;
        _isTextBold = info.IsBold;
        _isTextItalic = info.IsItalic;
        _hasStroke = info.HasStroke;
        _hasBackground = info.HasBackground;
        _textStrokeThickness = info.StrokeThickness;
        _textFillOpacity = info.FillOpacity;
        _textFillCornerRadius = info.FillCornerRadius;
        _textFillPadding = info.FillPadding;

        if (!string.IsNullOrEmpty(info.FontFamily)) _currentTextFont = info.FontFamily;

        if (_colorTargetMode == 0)
        {
            _textForegroundColor = info.Color;
            _host.SharedAnnotationColor = System.Drawing.Color.FromArgb(info.Color.A, info.Color.R, info.Color.G, info.Color.B);
        }
        else if (_colorTargetMode == 1) _textFillColor = info.FillColor;
        else if (_colorTargetMode == 2) _textStrokeColor = info.StrokeColor;

        _textFillColor = info.FillColor;
        _textStrokeColor = info.StrokeColor;

        UpdateTextPanelUi();
        // 选中态一变，"哪一层临时显形"就变了：就地重算一次裁切（不依赖下一次擦除发布）
        RefreshErasureMasks();
    }

    /// <summary>用最近一次同步过的文档重算各文字层的擦除裁切；没同步过就什么都不做（还没有擦除可言）。</summary>
    private void RefreshErasureMasks()
    {
        if (_erasureSource is { } source) ApplyErasureMasks(source);
    }

    /// <summary>取消选中文本（供 CaptureSession.ClearAllSelections 调用）</summary>
    public void DeselectText()
    {
        if (_selectedText != null)
        {
            if (_selectedText.SelectionBorder != null)
                _selectedText.SelectionBorder.IsVisible = false;
            if (_selectedText.AdornerLayer != null)
                _selectedText.AdornerLayer.IsVisible = false;
            _selectedText = null;
            RefreshErasureMasks();   // 取消选中＝这一层回到擦除后的样子
        }
    }

    /// <summary>删除指定文本图层</summary>
    internal void DeleteTextLayer(TextLayerInfo info)
    {
        if (info == null) return;
        if (_selectedText == info) DeselectText();
        if (info.Root != null && _host.TextLayerCanvas.Children.Contains(info.Root))
            _host.TextLayerCanvas.Children.Remove(info.Root);
        _textLayers.Remove(info);
    }

    /// <summary>清除所有文本</summary>
    public void ClearAllTexts()
    {
        foreach (var info in _textLayers.ToList())
        {
            if (info.Root != null && _host.TextLayerCanvas.Children.Contains(info.Root))
                _host.TextLayerCanvas.Children.Remove(info.Root);
        }
        _textLayers.Clear();
        _selectedText = null;
    }

    internal void BeginTextEditing(TextLayerInfo info)
    {
        if (info?.TextBox == null) return;

        foreach (var layer in _textLayers)
        {
            if (!ReferenceEquals(layer, info) && layer.TextBox != null)
                layer.TextBox.IsHitTestVisible = false;
        }

        info.TextBox.IsHitTestVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (info.TextBox == null || !info.TextBox.IsHitTestVisible) return;
            info.TextBox.Focus();
            info.TextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public bool HasActiveTextEditing => _textLayers.Any(info => info.TextBox?.IsHitTestVisible == true);

    /// <summary>结束当前文本编辑</summary>
    public void EndActiveTextEditing(string reason = "Unknown")
    {
        int ended = 0;
        foreach (var info in _textLayers)
        {
            if (info.TextBox != null && info.TextBox.IsHitTestVisible)
            {
                info.TextBox.IsHitTestVisible = false;
                ended++;
            }
        }
        _hasPendingTextEditSnapshot = false;
        _textCtrlKeyDown = false;

        bool focused = false;
        try { focused = _host.TextLayerCanvas.Focus(); } catch { }
        AppLogger.Info("CaptureWindow.EndActiveTextEditing: reason=" + reason + ", ended=" + ended + ", selectedText=" + (_selectedText != null) + ", textLayers=" + _textLayers.Count + ", overlayFocus=" + focused);
    }

    /// <summary>从 Avalonia 控件树查找 TextLayerInfo</summary>
    public TextLayerInfo? FindTextInfoFromSource(object? source)
    {
        if (source is not StyledElement element) return null;
        var current = element;
        while (current != null)
        {
            if (current is Grid grid && grid.Tag is TextLayerInfo info)
                return info;
            current = current.Parent;
        }
        return null;
    }

    internal void AdjustTextFontSizeByWheel(TextLayerInfo info, PointerWheelEventArgs args, string source)
    {
        if (info?.Root == null || info.TextBox == null) return;
        RequestTextSnapshot();

        double delta = args.Delta.Y > 0 ? 2 : -2;
        double newSize = Math.Clamp(info.FontSize + delta, 8.0, 200.0);
        double oldSize = info.FontSize;
        double oldMaxWidth = info.TextBox.MaxWidth;
        double oldTop = Canvas.GetTop(info.Root);
        if (double.IsNaN(oldTop)) oldTop = 0;

        // 1. 计算有效选区
        Rect validRect = _host.HasTextSelection
            ? _host.TextSelectionDip
            : new Rect(0, 0, Math.Max(_host.TextLayerCanvas.Bounds.Width, 1), Math.Max(_host.TextLayerCanvas.Bounds.Height, 1));

        // 2. 按当前位置计算可用宽度
        double currentLeft = Canvas.GetLeft(info.Root);
        if (double.IsNaN(currentLeft)) currentLeft = 0;
        double safetyMargin = GetTextRightSafetyMargin(info);
        double availableWidth = validRect.Right - currentLeft - safetyMargin;
        if (availableWidth < 50) availableWidth = 50;

        // 3. 应用新字号和宽度，强制布局
        info.FontSize = newSize;
        info.TextBox.FontSize = newSize;
        info.TextBox.LineHeight = newSize * 1.35;
        info.TextBox.MaxWidth = availableWidth;
        info.Root.InvalidateMeasure();
        info.Root.InvalidateArrange();
        info.Root.UpdateLayout();

        double newHeight = info.Root.Bounds.Height;
        if (newHeight <= 0) newHeight = newSize * 1.35;

        // 4. 检查是否超出选区总高度 → 完全回滚
        if (newHeight > validRect.Height)
        {
            info.FontSize = oldSize;
            info.TextBox.FontSize = oldSize;
            info.TextBox.LineHeight = oldSize * 1.35;
            info.TextBox.MaxWidth = oldMaxWidth;
            AppLogger.Info($"CaptureWindow.TextWheel: 拒绝字号调整 source={source}, newHeight={newHeight:0}, validH={validRect.Height:0}");
            args.Handled = true;
            return;
        }

        // 5. 底部越界 → 上推
        double currentTop = oldTop;
        if (currentTop + newHeight > validRect.Bottom)
        {
            double newTop = validRect.Bottom - newHeight;
            if (newTop < validRect.Top)
            {
                // 上推后仍撞顶 → 回滚
                info.FontSize = oldSize;
                info.TextBox.FontSize = oldSize;
                info.TextBox.LineHeight = oldSize * 1.35;
                info.TextBox.MaxWidth = oldMaxWidth;
                Canvas.SetTop(info.Root, oldTop);
                AppLogger.Info($"CaptureWindow.TextWheel: 拒绝字号调整 source={source}, 上推后撞顶 newTop={newTop:0}, validTop={validRect.Top:0}");
                args.Handled = true;
                return;
            }
            Canvas.SetTop(info.Root, newTop);
        }

        // 6. 顶部越界 → 下拉
        currentTop = Canvas.GetTop(info.Root);
        if (double.IsNaN(currentTop)) currentTop = 0;
        if (currentTop < validRect.Top)
        {
            Canvas.SetTop(info.Root, validRect.Top);
        }

        // 7. 完成
        UpdateTextView(info);
        _currentTextSize = newSize;
        if (_textSizeValueText != null) _textSizeValueText.Text = ((int)newSize).ToString();
        AppLogger.Info($"CaptureWindow.TextWheel: 调整文本图层字号 source={source}, old={oldSize:0}, next={newSize:0}, editing={info.TextBox.IsHitTestVisible}");
        args.Handled = true;
    }

    // ──────────────────────────────────────────
    // 公开属性（供 CaptureSession 使用）
    // ──────────────────────────────────────────

    public TextLayerInfo? SelectedText => _selectedText;

    public double CurrentTextSize
    {
        get => _currentTextSize;
        set
        {
            _currentTextSize = value;
            if (_selectedText != null)
            {
                _selectedText.FontSize = value;
                UpdateTextView(_selectedText);
            }
            if (_textSizeValueText != null)
                _textSizeValueText.Text = $"{_currentTextSize:F0}";
        }
    }

    public double TextStrokeThickness
    {
        get => _textStrokeThickness;
        set
        {
            _textStrokeThickness = value;
            if (_selectedText != null)
            {
                _selectedText.StrokeThickness = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public double TextFillOpacity
    {
        get => _textFillOpacity;
        set
        {
            _textFillOpacity = value;
            if (_selectedText != null)
            {
                _selectedText.FillOpacity = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public double TextFillCornerRadius
    {
        get => _textFillCornerRadius;
        set
        {
            _textFillCornerRadius = value;
            if (_selectedText != null)
            {
                _selectedText.FillCornerRadius = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public double TextFillPadding
    {
        get => _textFillPadding;
        set
        {
            _textFillPadding = value;
            if (_selectedText != null)
            {
                _selectedText.FillPadding = value;
                UpdateTextView(_selectedText);
            }
        }
    }

    public bool IsDraggingText => _isDraggingSelectedText;
    public bool IsRotatingText => _isRotatingText;

    public IReadOnlyList<TextLayerInfo> TextLayers => _textLayers;

    public Color ForegroundTextColor
    {
        get => _textForegroundColor;
        set => _textForegroundColor = value;
    }
    public Color TextFillColor => _textFillColor;
    public Color TextStrokeColor => _textStrokeColor;
    public int ColorTargetMode
    {
        get => _colorTargetMode;
        set => _colorTargetMode = value;
    }
    public bool HasStroke => _hasStroke;
    public bool HasBackground => _hasBackground;

    // ──────────────────────────────────────────
    // 全局指针事件半边（宿主在窗口级与画布级各挂一次，与截图侧原有双挂同口径）
    // ──────────────────────────────────────────

    /// <summary>拖动 / 旋转中的连续性处理。截图侧原为 CaptureWindow.OnOverlayCanvasPointerMoved 去掉头两行窗口专属逻辑后的剩余部分。</summary>
    internal void TryHandleGlobalPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingSelectedText && _selectedText != null)
        {
            var currentPos = e.GetPosition(_host.TextLayerCanvas);
            MoveSelectedTextWithinBounds(_selectedText, currentPos);
            e.Handled = true;
            return;
        }

        if (_isRotatingText && _rotatingTextInfo != null)
        {
            var currentPos = e.GetPosition(_host.TextLayerCanvas);
            double left = Canvas.GetLeft(_rotatingTextInfo.Root);
            double top = Canvas.GetTop(_rotatingTextInfo.Root);
            double cx = left + _rotatingTextInfo.Root.Bounds.Width / 2;
            double cy = top + _rotatingTextInfo.Root.Bounds.Height / 2;
            double startAngle = Math.Atan2(_rotateStartPoint.Y - cy, _rotateStartPoint.X - cx) * 180.0 / Math.PI;
            double currentAngle = Math.Atan2(currentPos.Y - cy, currentPos.X - cx) * 180.0 / Math.PI;
            _rotatingTextInfo.RotationAngle = _rotateStartAngle + currentAngle - startAngle;
            ApplyTextRotation(_rotatingTextInfo);
            e.Handled = true;
        }
    }

    /// <summary>截图侧原为 CaptureWindow.OnOverlayCanvasPointerReleased，整段搬来（该函数本来就只处理文本拖拽/旋转收尾）。</summary>
    internal void TryHandleGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingSelectedText || _isRotatingText)
        {
            try { e.Pointer.Capture(null); } catch { }
            e.Handled = true;
        }
        _isDraggingSelectedText = false;
        _isRotatingText = false;
        _rotatingTextInfo = null;
    }

    // ──────────────────────────────────────────
    // 宿主要读的几样状态（参数面板容器 / 全局色目标 / 持久化）
    // ──────────────────────────────────────────

    /// <summary>文本参数面板容器。截图侧是窗口 XAML 的 TextToolsPanel，贴图侧是标注浮窗内的复刻面板；可见性由宿主的工具条布局代码决定。</summary>
    public StackPanel? TextPanel => _textToolsPanel;

    /// <summary>
    /// 参数栏当前取色目标的实际颜色（逐字搬自 CaptureWindow.GetCurrentTargetColor 的文本分支）。
    /// 返回 false 表示当前不是文本语境，宿主按自己的全局标注色处理。
    /// </summary>
    internal bool TryGetTextTargetColor(out System.Drawing.Color color)
    {
        color = default;
        if (!_host.IsTextToolCurrent() && _selectedText == null) return false;

        Color c = _colorTargetMode switch
        {
            1 => _textFillColor,
            2 => _textStrokeColor,
            _ => Color.FromArgb(_host.SharedAnnotationColor.A, _host.SharedAnnotationColor.R, _host.SharedAnnotationColor.G, _host.SharedAnnotationColor.B),
        };
        color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        return true;
    }

    /// <summary>逐字搬自 CaptureWindow.ApplyColorToCurrentTarget 的文本分支；返回 true 表示已消费（宿主不再走非文本通路）。</summary>
    internal bool TryApplyColorToTextTarget(System.Drawing.Color color)
    {
        if (!_host.IsTextToolCurrent() && _selectedText == null) return false;

        var textColor = Color.FromArgb(color.A, color.R, color.G, color.B);
        if (_colorTargetMode == 0)
        {
            // 文本前景色：同步到全局 SelectedAnnotationColor
            _textForegroundColor = textColor;
            _host.SharedAnnotationColor = color;
        }
        else if (_colorTargetMode == 1) _textFillColor = textColor;
        else if (_colorTargetMode == 2) _textStrokeColor = textColor;
        RequestTextSnapshot();
        ApplyTextSettingsToSelected();
        _host.NotifyTextPanelNeedsRefresh();
        UpdateTextPanelUi();
        return true;
    }

    /// <summary>从 AppSettings 恢复文本工具参数（逐字搬自 CaptureWindow.ApplyAnnotationDefaults 的「文本工具」段，键与顺序未变）。</summary>
    internal void ApplyDefaults(AppSettings s)
    {
        _currentTextSize = Math.Clamp(s.TextFontSize, 8, 200);
        _currentTextFont = s.TextFontFamily ?? "Microsoft YaHei UI";
        _isTextBold = s.TextIsBold;
        _isTextItalic = s.TextIsItalic;
        _hasStroke = s.TextHasStroke;
        _hasBackground = s.TextHasBackground;
        _textStrokeThickness = s.TextStrokeThickness;
        _textStrokeColor = Color.FromArgb(
            (byte)((s.TextStrokeColor >> 24) & 0xFF),
            (byte)((s.TextStrokeColor >> 16) & 0xFF),
            (byte)((s.TextStrokeColor >> 8) & 0xFF),
            (byte)(s.TextStrokeColor & 0xFF));
        _textFillColor = Color.FromArgb(
            (byte)((s.TextFillColor >> 24) & 0xFF),
            (byte)((s.TextFillColor >> 16) & 0xFF),
            (byte)((s.TextFillColor >> 8) & 0xFF),
            (byte)(s.TextFillColor & 0xFF));
        _textFillOpacity = s.TextFillOpacity;
        _textFillCornerRadius = s.TextFillCornerRadius;
        _textFillPadding = s.TextFillPadding;
    }

    /// <summary>把文本工具参数写入 AppSettings（逐字搬自 CaptureWindow.CaptureAnnotationDefaults 的 Text* 段）。</summary>
    internal void CaptureDefaults(AppSettings s)
    {
        s.TextFontSize = _currentTextSize;
        s.TextFontFamily = _currentTextFont ?? "Microsoft YaHei UI";
        s.TextIsBold = _isTextBold;
        s.TextIsItalic = _isTextItalic;
        s.TextHasStroke = _hasStroke;
        s.TextHasBackground = _hasBackground;
        s.TextStrokeThickness = _textStrokeThickness;
        s.TextStrokeColor = (_textStrokeColor.A << 24) | (_textStrokeColor.R << 16) | (_textStrokeColor.G << 8) | _textStrokeColor.B;
        s.TextFillColor = (_textFillColor.A << 24) | (_textFillColor.R << 16) | (_textFillColor.G << 8) | _textFillColor.B;
        s.TextFillOpacity = _textFillOpacity;
        s.TextFillCornerRadius = _textFillCornerRadius;
        s.TextFillPadding = _textFillPadding;
    }
}
