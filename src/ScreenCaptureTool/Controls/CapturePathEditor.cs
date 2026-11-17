using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ScreenCaptureTool.Core.Capture;
using P = System.Drawing.PointF;

namespace ScreenCaptureTool.Controls;

/// <summary>路径输入和节点预览。坐标始终是屏幕物理像素，只有绘制／命中容差转换为 DIP。</summary>
internal sealed class CapturePathEditor : Control
{
    private readonly List<CapturePathNode> _nodes = new();
    private readonly List<(CapturePathNode[] Nodes, bool Closed)> _undo = new();
    private int _dragNode = -1, _dragHandle;
    private P _press, _pointer;
    private bool _moving, _dragging;
    private CapturePathNode[]? _dragStart;
    private CaptureSelectionPath? _snapshot;
    private double _freehandTolerance = .6;
    public CaptureSelectionMode Mode { get; private set; }
    public bool IsClosed { get; private set; }
    public CaptureSelectionPath? Path => IsClosed && _snapshot is { IsCollinear: false } ? _snapshot : null;
    public IReadOnlyList<CapturePathNode> Nodes => _nodes;
    public Func<Point, P> ToScreen { get; set; } = p => new((float)p.X, (float)p.Y);
    public Func<P, Point> ToDip { get; set; } = p => new(p.X, p.Y);
    public event Action? Changed;
    public event Action? PreviewChanged;
    public bool IsDrawing => Mode != CaptureSelectionMode.Rectangle && !IsClosed;
    public CapturePathEditor() { IsHitTestVisible = false; Focusable = true; }

    public void SetMode(CaptureSelectionMode mode)
    {
        Mode = mode; _nodes.Clear(); _undo.Clear(); IsClosed = false; EndDrag(); Publish();
    }
    private void Remember()
    {
        if (_undo.Count >= 128) _undo.RemoveAt(0);
        _undo.Add((_nodes.ToArray(), IsClosed));
    }
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var state = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _nodes.Clear(); _nodes.AddRange(state.Nodes); IsClosed = state.Closed; EndDrag(); Publish(); return true;
    }
    public bool ClosePath()
    {
        if (IsClosed) return true;
        if (Mode == CaptureSelectionMode.Freehand && _dragging) return false;
        if (_nodes.Count < 3) return false;
        var path = new CaptureSelectionPath(_nodes);
        if (path.Bounds.Width < 2 || path.Bounds.Height < 2 || path.IsCollinear) return false;
        if (Mode != CaptureSelectionMode.Freehand) Remember();
        IsClosed = true; Publish(); return true;
    }
    public void Reopen() { if (IsClosed) { Remember(); IsClosed = false; Publish(); } }
    public void CancelDraft() { _nodes.Clear(); _undo.Clear(); IsClosed = false; EndDrag(); Publish(); }
    public void Translate(float dx, float dy)
    {
        if (!IsClosed) return;
        Remember();
        for (int i = 0; i < _nodes.Count; i++) _nodes[i] = _nodes[i].Translate(dx, dy);
        Publish();
    }

    // 返回 false 表示未命中节点／内部，应交给已有标注链处理。
    public bool Press(Point dip)
    {
        if (Mode == CaptureSelectionMode.Rectangle) return false;
        P p = ToScreen(dip); _pointer = _press = p; _dragNode = -1; _dragHandle = 0; _moving = false;
        if (IsClosed)
        {
            for (int i = Mode == CaptureSelectionMode.Freehand ? -1 : _nodes.Count - 1; i >= 0; i--)
            {
                if (Near(_nodes[i].Position, dip, 8)) { _dragNode = i; break; }
                if (Mode == CaptureSelectionMode.Pen && _nodes[i].Incoming != _nodes[i].Position && Near(_nodes[i].Incoming, dip, 7)) { _dragNode = i; _dragHandle = -1; break; }
                if (Mode == CaptureSelectionMode.Pen && _nodes[i].Outgoing != _nodes[i].Position && Near(_nodes[i].Outgoing, dip, 7)) { _dragNode = i; _dragHandle = 1; break; }
            }
            if (_dragNode < 0 && !(Path?.Contains(p) ?? false)) return false;
            _moving = _dragNode < 0;
        }
        else
        {
            if (Mode == CaptureSelectionMode.Freehand)
            {
                Remember(); _nodes.Clear(); _nodes.Add(new(p)); _freehandTolerance = .6;
                _dragging = true; Publish(); return true;
            }
            if (_nodes.Count >= 3 && Near(_nodes[0].Position, dip, 9)) { ClosePath(); return true; }
            if (_nodes.Count >= 2048) return true;
            Remember(); _nodes.Add(new(p)); _dragNode = _nodes.Count - 1;
        }
        if (IsClosed) Remember();
        _dragStart = _nodes.ToArray(); _dragging = true; Publish(); return true;
    }
    public void Move(Point dip)
    {
        _pointer = ToScreen(dip);
        if (Mode == CaptureSelectionMode.Freehand && _dragging && !IsClosed)
        {
            if (!Near(_nodes[^1].Position, dip, 1.5))
            {
                AppendFreehand(_pointer); Publish();
            }
            return;
        }
        if (_dragging && _dragStart != null)
        {
            float dx = _pointer.X - _press.X, dy = _pointer.Y - _press.Y;
            if (_moving)
                for (int i = 0; i < _nodes.Count; i++) _nodes[i] = _dragStart[i].Translate(dx, dy);
            else if (_dragNode >= 0)
            {
                var start = _dragStart[_dragNode];
                if (!IsClosed && Mode == CaptureSelectionMode.Pen)
                    _nodes[_dragNode] = new(start.Position, new(start.Position.X - dx, start.Position.Y - dy), new(start.Position.X + dx, start.Position.Y + dy));
                else if (IsClosed && _dragHandle != 0)
                    _nodes[_dragNode] = _dragHandle < 0 ? start with { Incoming = _pointer } : start with { Outgoing = _pointer };
                else if (IsClosed) _nodes[_dragNode] = start.Translate(dx, dy);
            }
            Publish();
        }
        else { InvalidateVisual(); if (!IsClosed) PreviewChanged?.Invoke(); }
    }
    public void Release(bool commit = true)
    {
        if (Mode == CaptureSelectionMode.Freehand && _dragging && !IsClosed)
        {
            if (!commit) { Undo(); return; }
            AppendFreehand(_pointer); SimplifyFreehand(); EndDrag();
            // 轻点、直线等无面积手势不留下半条路径；整笔仅占一个撤销步骤。
            if (!ClosePath()) Undo();
            return;
        }
        EndDrag(); Publish();
    }
    private void AppendFreehand(P point)
    {
        if (_nodes[^1].Position == point) return;
        if (_nodes.Count >= 2048)
        {
            // 极长轨迹有界简化，保留首尾并继续接收末段，不能达到上限后直接截断。
            do { SimplifyFreehand(); _freehandTolerance *= 2; } while (_nodes.Count >= 2048);
        }
        _nodes.Add(new(point));
    }
    private void SimplifyFreehand()
    {
        if (_nodes.Count < 3) return;
        // 以 DIP 量度误差，迭代 RDP 避免长轨迹递归溢出；预览与成图共享简化后的节点。
        var points = _nodes.Select(n => ToDip(n.Position)).ToArray();
        var keep = new bool[points.Length]; keep[0] = keep[^1] = true;
        var pending = new Stack<(int First, int Last)>(); pending.Push((0, points.Length - 1));
        while (pending.TryPop(out var segment))
        {
            var a = points[segment.First]; var b = points[segment.Last]; var v = b - a;
            double length = v.X * v.X + v.Y * v.Y, max = _freehandTolerance * _freehandTolerance;
            int split = -1;
            for (int i = segment.First + 1; i < segment.Last; i++)
            {
                var w = points[i] - a;
                double t = length == 0 ? 0 : Math.Clamp((w.X * v.X + w.Y * v.Y) / length, 0, 1);
                var d = points[i] - (a + v * t); double distance = d.X * d.X + d.Y * d.Y;
                if (distance > max) { max = distance; split = i; }
            }
            if (split < 0) continue;
            keep[split] = true; pending.Push((segment.First, split)); pending.Push((split, segment.Last));
        }
        int write = 0;
        for (int i = 0; i < keep.Length; i++) if (keep[i]) _nodes[write++] = _nodes[i];
        _nodes.RemoveRange(write, _nodes.Count - write);
    }
    public void EndDrag() { _dragging = false; _dragStart = null; _dragNode = -1; }
    public bool HitEditor(Point dip) => IsClosed && ((Mode != CaptureSelectionMode.Freehand && _nodes.Any(n => Near(n.Position, dip, 8)
        || (Mode == CaptureSelectionMode.Pen && (Near(n.Incoming, dip, 7) || Near(n.Outgoing, dip, 7))))) || (Path?.Contains(ToScreen(dip)) ?? false));
    private bool Near(P p, Point dip, double radius) { var v = ToDip(p) - dip; return v.X * v.X + v.Y * v.Y <= radius * radius; }
    private void Publish()
    {
        _snapshot = IsClosed ? new CaptureSelectionPath(_nodes) : null;
        InvalidateVisual(); Changed?.Invoke();
    }

    public StreamGeometry Geometry(bool mask = false, Size screenSize = default)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.SetFillRule(FillRule.EvenOdd);
        if (mask)
        {
            ctx.BeginFigure(new Point(0, 0), true); ctx.LineTo(new Point(screenSize.Width, 0));
            ctx.LineTo(new Point(screenSize.Width, screenSize.Height)); ctx.LineTo(new Point(0, screenSize.Height)); ctx.EndFigure(true);
        }
        if (_nodes.Count == 0) return geometry;
        // 临时闭合仅用于亮区预览；Path 仍须真实闭合后才可用于输出。
        ctx.BeginFigure(ToDip(_nodes[0].Position), mask || IsClosed);
        for (int i = 1; i < _nodes.Count; i++)
            ctx.CubicBezierTo(ToDip(_nodes[i - 1].Outgoing), ToDip(_nodes[i].Incoming), ToDip(_nodes[i].Position));
        if (IsClosed)
            ctx.CubicBezierTo(ToDip(_nodes[^1].Outgoing), ToDip(_nodes[0].Incoming), ToDip(_nodes[0].Position));
        else if (!_dragging && Mode != CaptureSelectionMode.Freehand) ctx.LineTo(ToDip(_pointer));
        ctx.EndFigure(mask || IsClosed); return geometry;
    }
    public override void Render(DrawingContext context)
    {
        if (Mode == CaptureSelectionMode.Rectangle || _nodes.Count == 0) return;
        var brush = this.TryFindResource("SctAccentBrush", ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.White;
        var pen = new Pen(brush, 1.5);
        if (!IsClosed) context.DrawGeometry(null, pen, Geometry());
        if (Mode == CaptureSelectionMode.Freehand) return;
        for (int i = 0; i < _nodes.Count; i++)
        {
            var n = _nodes[i]; Point p = ToDip(n.Position);
            // 仅在拖动当前端点时显示辅助柄，松手不保留；曲线控制点数据不变。
            if (Mode == CaptureSelectionMode.Pen && _dragging && _dragNode == i)
                foreach (var handle in new[] { n.Incoming, n.Outgoing })
                    if (handle != n.Position)
                    {
                        Point h = ToDip(handle); context.DrawLine(pen, p, h);
                        context.DrawEllipse(brush, null, h, 3, 3);
                    }
            context.DrawEllipse(Brushes.White, pen, p, i == 0 && !IsClosed ? 5 : 4, i == 0 && !IsClosed ? 5 : 4);
        }
    }
}
