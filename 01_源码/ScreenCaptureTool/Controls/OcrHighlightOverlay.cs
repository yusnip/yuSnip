using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// OCR 文本多边形高亮控件：叠加在原图上方，按 RapidOCR 4 顶点多边形绘制各识别框的可交互高亮。
/// - 缩放自适应：Polygon 为原图像素坐标，渲染/命中时乘 <see cref="Scale"/>（由 OcrWindow.ApplyScale 同步设置）；
/// - 状态分层（优先级）：选中（品牌紫实底描边）＞ 悬停（品牌紫半透明）＞ 已改（编辑橙）＞ 低置信度（警告红虚线）；
///   常规无状态框不绘制（原图纯净），仅 LinkageEnabled 时响应悬停；
/// - 命中检测：点在多边形内（射线法），指针坐标 / Scale 还原到原图像素空间；
/// - 交互：PointerPressed 不拦截（让 CanvasHost 冒泡启动平移候选）；PointerReleased 时
///   位移小于阈值且命中判定为点击 → 触发 <see cref="BoxClicked"/>（与拖拽平移互不干扰）。
/// - 命中测试：实现 <see cref="ICustomHitTest"/> 按识别框多边形精确命中——
///   Avalonia 组合渲染器默认「无绘制内容的控件不可命中」，而本控件常规无状态框不绘制（空 DrawList），
///   若不接管命中判定，鼠标悬停原图时事件全部落到下层 Image，悬停/点击联动将完全失效
///   （仅在恰好存在低置信度/已改等绘制内容时短暂可用，形成时好时坏的假象）。
/// </summary>
public sealed class OcrHighlightOverlay : Control, ICustomHitTest
{
    // 原型配色（与 OcrWindow/OcrLineItem 保持一致）
    private static readonly IBrush BrushAccent = new SolidColorBrush(Color.FromRgb(66, 25, 242));             // #4219F2
    private static readonly IBrush BrushAccentLine = new SolidColorBrush(Color.FromArgb(140, 66, 25, 242));    // rgba(66,25,242,.55)
    private static readonly IBrush BrushAccentSoft = new SolidColorBrush(Color.FromArgb(36, 66, 25, 242));    // rgba(66,25,242,.14) 点击选中
    private static readonly IBrush BrushAccentHoverSoft = new SolidColorBrush(Color.FromArgb(16, 66, 25, 242)); // rgba(66,25,242,.06) 仅悬浮（轻于选中，区分点击/未点击）
    private static readonly IBrush BrushEdit = new SolidColorBrush(Color.FromRgb(232, 134, 43));             // #E8862B
    private static readonly IBrush BrushEditSoft = new SolidColorBrush(Color.FromArgb(30, 232, 134, 43));    // rgba(232,134,43,.12)
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.FromArgb(170, 214, 79, 79));        // 低置信度警告红

    private static readonly DashStyle WarnDash = new DashStyle([3, 2], 0);
    private static readonly Cursor CursorHand = new(StandardCursorType.Hand);

    /// <summary>低置信度阈值（低于此值绘制警告虚线框）。</summary>
    private const float LowScoreThreshold = 0.6f;

    /// <summary>按下→松开位移超过该值视为拖拽平移而非点击。</summary>
    private const double ClickMoveThreshold = 5.0;

    private IReadOnlyList<OcrResultBox>? _boxes;
    private HashSet<int>? _editedIndices;
    private int _hoverIndex = -1;
    private int _selectedIndex = -1;
    private bool _linkageEnabled = true;
    private bool _pressCandidate;
    private Point _pressPos;

    /// <summary>显示缩放比例（原图像素 → 控件本地坐标）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>悬停命中的框索引（-1 = 无）。</summary>
    public int HoverIndex
    {
        get => _hoverIndex;
        set
        {
            if (_hoverIndex == value) return;
            _hoverIndex = value;
            InvalidateVisual();
        }
    }

    /// <summary>选中的框索引（-1 = 无；由右侧卡片选中联动驱动）。</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            InvalidateVisual();
        }
    }

    /// <summary>联动高亮开关（关闭时不绘制悬停态且不响应命中交互）。</summary>
    public bool LinkageEnabled
    {
        get => _linkageEnabled;
        set
        {
            if (_linkageEnabled == value) return;
            _linkageEnabled = value;
            if (!value) HoverIndex = -1;
            InvalidateVisual();
        }
    }

    /// <summary>悬停框变化（参数为框索引，-1 = 离开）。</summary>
    public event Action<int>? BoxHoverChanged;

    /// <summary>点击框（参数为框索引；仅在未拖拽平移时触发）。</summary>
    public event Action<int>? BoxClicked;

    /// <summary>设置识别框列表（新 OCR 内容到达时整体替换，并复位交互状态）。</summary>
    public void SetBoxes(IReadOnlyList<OcrResultBox>? boxes)
    {
        _boxes = boxes;
        _editedIndices = null;
        _hoverIndex = -1;
        _selectedIndex = -1;
        InvalidateVisual();
        // [OcrHover] 临时排查：boxes 送达快照（悬停完全无日志时区分「事件未到达」与「无识别框」）
        ScreenCaptureTool.Core.AppLogger.Info($"[OcrHover] SetBoxes count={boxes?.Count ?? 0}");
    }

    /// <summary>设置已改行的高亮索引集合（null = 清空；编辑橙整块点亮）。</summary>
    public void SetEditedIndices(HashSet<int>? indices)
    {
        _editedIndices = indices;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_boxes is not { Count: > 0 }) return;

        int hit = HitTestIndex(e.GetPosition(this));
        if (hit != _hoverIndex)
        {
            _hoverIndex = hit;
            if (_linkageEnabled) BoxHoverChanged?.Invoke(hit);
            InvalidateVisual();
            // [OcrHover] 临时排查：悬停命中变化（无任何日志=事件未到达本控件；hit 恒 -1=Scale/命中测试错位）
            var pos = e.GetPosition(this);
            ScreenCaptureTool.Core.AppLogger.Info(
                $"[OcrHover] overlay悬停变化 hit={hit} boxes={_boxes.Count} scale={Scale:0.###} pos=({pos.X:0},{pos.Y:0}) linkage={_linkageEnabled} size=({Bounds.Width:0}x{Bounds.Height:0})");
        }
        Cursor = hit >= 0 && _linkageEnabled ? CursorHand : Cursor.Default;
        e.Handled = false;   // 不拦截，滚动平移手势继续冒泡
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 不设置 Handled：让 CanvasHost 冒泡收到 Pressed 启动平移候选（拖拽平移依赖此路径）
        _pressCandidate = true;
        _pressPos = e.GetPosition(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressCandidate) return;
        _pressCandidate = false;

        // 位移超过阈值 = 拖拽平移，不算点击
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _pressPos.X) > ClickMoveThreshold ||
            Math.Abs(pos.Y - _pressPos.Y) > ClickMoveThreshold) return;

        int hit = HitTestIndex(pos);
        if (hit >= 0)
        {
            if (_linkageEnabled)
            {
                BoxClicked?.Invoke(hit);
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            if (_linkageEnabled) BoxHoverChanged?.Invoke(-1);
            InvalidateVisual();
        }
        Cursor = Cursor.Default;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_boxes is not { Count: > 0 }) return;
        double scale = Scale > 0 ? Scale : 1.0;

        for (int i = 0; i < _boxes.Count; i++)
        {
            var box = _boxes[i];
            if (box.Polygon is not { Length: >= 3 }) continue;

            bool selected = i == _selectedIndex;
            bool hovered = i == _hoverIndex && _linkageEnabled;
            bool edited = _editedIndices != null && _editedIndices.Contains(i);
            bool lowScore = box.Score < LowScoreThreshold;

            // 常规无状态框不绘制（原图纯净）；状态优先级：选中 > 悬停 > 已改 > 低置信度
            if (!selected && !hovered && !edited && !lowScore) continue;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                ctx.BeginFigure(new Point(box.Polygon[0].X * scale, box.Polygon[0].Y * scale), true);
                for (int k = 1; k < box.Polygon.Length; k++)
                    ctx.LineTo(new Point(box.Polygon[k].X * scale, box.Polygon[k].Y * scale));
                ctx.EndFigure(true);
            }

            if (selected)
            {
                context.DrawGeometry(BrushAccentSoft, new Pen(BrushAccent, 2.0), geo);
            }
            else if (hovered)
            {
                // 悬浮：填充明显淡于点击选中（6% vs 14%），描边半透明细线——未点击状态一眼可辨
                context.DrawGeometry(BrushAccentHoverSoft, new Pen(BrushAccentLine, 1.5), geo);
            }
            else if (edited)
            {
                context.DrawGeometry(BrushEditSoft, new Pen(BrushEdit, 1.5), geo);
            }
            else
            {
                // 低置信度警告：无填充 + 红色虚线描边
                context.DrawGeometry(null, new Pen(BrushWarn, 1.2) { DashStyle = WarnDash }, geo);
            }
        }
    }

    /// <summary>
    /// ICustomHitTest 接口实现：按识别框多边形精确命中（射线法）。
    /// 渲染器命中测试树遍历时传入的 point 已换算为本控件本地坐标（TryTransformTo 结果），
    /// 直接复用多边形命中索引判定；返回 false 的区域（非文字框）事件穿透到下层 Image。
    /// </summary>
    bool ICustomHitTest.HitTest(Point point) => HitTestIndex(point) >= 0;

    /// <summary>点在多边形内测试（射线法）：控件本地坐标还原到原图像素空间后判定。</summary>
    private int HitTestIndex(Point localPos)
    {
        if (_boxes is not { Count: > 0 }) return -1;
        double scale = Scale > 0 ? Scale : 1.0;
        double px = localPos.X / scale, py = localPos.Y / scale;

        // 逆序遍历：后绘制的框在上层，优先命中
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            var poly = _boxes[i].Polygon;
            if (poly is not { Length: >= 3 }) continue;
            if (IsPointInPolygon(px, py, poly)) return i;
        }
        return -1;
    }

    private static bool IsPointInPolygon(double x, double y, System.Drawing.PointF[] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i].X, yi = poly[i].Y;
            double xj = poly[j].X, yj = poly[j].Y;

            if ((yi > y) != (yj > y) &&
                x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
