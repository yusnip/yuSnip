using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace ScreenCaptureTool.Controls;

/// <summary>列表重排共用的指针捕获、起拖阈值与坐标换算；落位/合并由宿主决定。</summary>
public sealed class ListReorderBehavior
{
    private readonly double _threshold;
    private readonly bool _manhattan;
    private IPointer? _pointer;
    private Control? _source;
    private Visual? _relativeTo;
    private Point _start;

    public bool IsActive => _pointer != null;
    public bool IsDragging { get; private set; }
    public event Action? Canceled;

    public ListReorderBehavior(double threshold = 6, bool manhattan = false)
    {
        _threshold = threshold;
        _manhattan = manhattan;
    }

    public bool Begin(Control source, PointerPressedEventArgs e, Visual relativeTo)
    {
        if (!e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed) return false;
        Reset();
        _source = source;
        _pointer = e.Pointer;
        _relativeTo = relativeTo;
        _start = e.GetPosition(relativeTo);
        source.PointerCaptureLost += OnCaptureLost;
        e.Pointer.Capture(source);
        e.Handled = true;
        return true;
    }

    public bool Update(PointerEventArgs e)
    {
        if (_pointer != e.Pointer || _relativeTo == null) return false;
        if (!IsDragging)
        {
            Point position = e.GetPosition(_relativeTo);
            double dx = Math.Abs(position.X - _start.X), dy = Math.Abs(position.Y - _start.Y);
            if ((_manhattan ? dx + dy : Math.Sqrt(dx * dx + dy * dy)) < _threshold) return false;
            IsDragging = true;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>先清状态/退订，再释放捕获；宿主可在之后安全移动或重建控件。</summary>
    public void Reset()
    {
        var pointer = _pointer;
        var source = _source;
        _pointer = null;
        _source = null;
        _relativeTo = null;
        IsDragging = false;
        if (source != null) source.PointerCaptureLost -= OnCaptureLost;
        if (pointer != null && source != null && pointer.Captured == source) pointer.Capture(null);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!IsActive) return;
        Reset();
        Canceled?.Invoke();
    }

    public static bool TryGetBounds(Control control, Visual relativeTo, out Rect bounds)
    {
        if (control.TranslatePoint(default, relativeTo) is Point origin)
        {
            bounds = new Rect(origin, control.Bounds.Size);
            return true;
        }
        bounds = default;
        return false;
    }

    /// <summary>按各项中心计算槽位，排除占位框；指针留在占位框内时保持位置，避免来回振荡。</summary>
    public static int GetInsertionIndex(Panel panel, Point point, Orientation orientation, Control? placeholder)
    {
        if (placeholder?.Parent == panel && TryGetBounds(placeholder, panel, out var gap) && gap.Contains(point))
            return panel.Children.IndexOf(placeholder);
        int slot = 0;
        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child, placeholder)) continue;
            if (TryGetBounds(child, panel, out var bounds)
                && (orientation == Orientation.Vertical ? point.Y < bounds.Center.Y : point.X < bounds.Center.X)) return slot;
            slot++;
        }
        return slot;
    }

    /// <summary>工具栏与模块共用动态让位：原控件保留捕获，只移动独立占位框。</summary>
    public static void MovePlaceholder(Panel panel, Control placeholder, int slot)
    {
        if (placeholder.Parent == panel && panel.Children.IndexOf(placeholder) == slot) return;
        RemovePlaceholder(placeholder);
        panel.Children.Insert(Math.Clamp(slot, 0, panel.Children.Count), placeholder);
    }

    public static void RemovePlaceholder(Control? placeholder)
    {
        if (placeholder?.Parent is Panel panel) panel.Children.Remove(placeholder);
    }
}
