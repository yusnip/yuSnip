using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;

#if LONGSCROLL_SELECTION_LAYER
namespace ScreenCaptureTool.LongScroll.Windows;
#else
namespace ScreenCaptureTool.Services;
#endif

/// <summary>只重排宿主原控件；默认布局完全保留原树，复合单元的内部显隐仍由宿主管。</summary>
internal sealed class ToolbarLayoutView
{
    private const string DividerTag = "toolbarSceneDivider";
    private readonly Panel _panel;
    private readonly ToolbarScene _scene;
    private readonly Dictionary<string, Control[]> _map;
    private readonly Control[] _prefix;
    private readonly Control[] _dividers;
    private readonly Dictionary<Control, bool> _initialVisibility;
    private HashSet<string> _included = new();
    private string? _applied;
    private readonly List<GroupView> _groups = new();
    private Window? _owner;
    private readonly List<Visual> _visibilityHosts = new();
    public IEnumerable<Control> Controls => _map.Values.SelectMany(c => c).Distinct();
    public bool HasGroups => _groups.Count > 0;

    public ToolbarLayoutView(Panel panel, ToolbarScene scene, Dictionary<string, Control[]> map, params Control[] prefix)
    {
        _panel = panel; _scene = scene; _map = map; _prefix = prefix;
        _dividers = panel.Children.Where(c => c is Border && !prefix.Contains(c) && !map.Values.Any(a => a.Contains(c))).ToArray();
        _initialVisibility = map.Values.SelectMany(a => a).Distinct().ToDictionary(c => c, c => c.IsVisible);
        _panel.AttachedToVisualTree += (_, _) =>
        {
            _owner = TopLevel.GetTopLevel(_panel) as Window;
            foreach (var host in _panel.GetVisualAncestors())
            { _visibilityHosts.Add(host); host.PropertyChanged += OnHostChanged; }
        };
        _panel.DetachedFromVisualTree += (_, _) =>
        {
            CloseGroups();
            foreach (var host in _visibilityHosts) host.PropertyChanged -= OnHostChanged;
            _visibilityHosts.Clear(); _owner = null;
        };
        _panel.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && !_panel.IsVisible
                || e.Property == InputElement.IsEnabledProperty && !_panel.IsEnabled) CloseGroups();
        };
    }

    private void OnHostChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 参数栏可能只隐藏外层容器而不关窗口（例如截图切离录屏工具），独立 Popup 也要收起。
        if (e.Property == Visual.IsVisibleProperty && sender is Visual { IsVisible: false }
            || e.Property == InputElement.IsEnabledProperty && sender is InputElement { IsEnabled: false }) CloseGroups();
    }
    public void CloseGroups() { foreach (var group in _groups) { CloseNested(group); group.Popup.IsOpen = false; } }
    private void CloseNested(GroupView group)
    {
        foreach (var id in group.Members.Where(id => id != "|"))
        foreach (var control in _map[id])
        foreach (var popup in control.GetLogicalDescendants().OfType<Popup>()) popup.IsOpen = false;
    }
    public Control? Anchor(string id) => _groups.FirstOrDefault(g => g.Members.Contains(id))?.Cell ?? _map.GetValueOrDefault(id)?.FirstOrDefault(c => c.IsVisible);
    public void SelectTool(string? id)
    {
        if (id == null) return;
        var group = _groups.FirstOrDefault(g => g.Members.Contains(id));
        if (group == null) return;
        if (group.HeadId != id) { group.HeadId = id; FillGroup(group); _panel.InvalidateMeasure(); }
        CloseGroups();
    }
    public void SetPresented(bool visible, Func<string, bool> allowed)
    {
        if (!visible) CloseGroups();
        foreach (var item in _map)
            foreach (var control in item.Value) control.IsVisible = visible && Includes(item.Key) && allowed(item.Key) && _initialVisibility[control];
        foreach (var group in _groups)
        {
            group.Cell.IsVisible = visible;
            if (!_map[group.HeadId].Any(c => c.IsVisible))
            {
                string? next = group.Members.FirstOrDefault(id => id != "|" && _map[id].Any(c => c.IsVisible));
                if (next != null) { group.HeadId = next; FillGroup(group); }
                else group.Cell.IsVisible = false;
            }
        }
    }

    public bool Includes(string id) => _included.Contains(id);
    public void Apply(AppSettings settings)
    {
        var definition = ToolbarScenes.Get(_scene);
        string value = definition.Normalize(ToolbarScenes.Read(settings, _scene));
        var tokens = definition.Parse(value);
        _included = definition.ToolIds(tokens).ToHashSet();
        if (value == _applied || (_applied == null && value == definition.DefaultLayout)) return;
        _applied = value;
        CloseGroups();
        foreach (var control in Controls)
        { Detach(control); control.Classes.Remove("tbGroupHead"); control.Classes.Remove("toolbarGroupedTool"); control.IsVisible = Includes(_map.First(p => p.Value.Contains(control)).Key) && _initialVisibility[control]; }
        _groups.Clear(); _panel.Children.Clear();
        foreach (var prefix in _prefix) _panel.Children.Add(prefix);
        int divider = 0;
        foreach (string token in tokens)
        {
            if (token == "|") _panel.Children.Add(divider < _dividers.Length ? _dividers[divider++] : CreateDivider());
            else if (ToolbarRegistry.IsGroupToken(token)) _panel.Children.Add(BuildGroup(definition.GroupMembers(token)));
            else foreach (var control in _map[token]) _panel.Children.Add(control);
        }
        foreach (var item in _map.Where(p => !Includes(p.Key))) foreach (var control in item.Value) _panel.Children.Add(control);
        _panel.InvalidateMeasure();
    }

    private static void Detach(Control control) { if (control.Parent is Panel panel) panel.Children.Remove(control); }
    private sealed class GroupView
    {
        public required List<string> Members;
        public required string HeadId;
        public required StackPanel Cell, Head, Row;
        public required Button Chevron;
        public required Popup Popup;
    }
    private Control BuildGroup(List<string> members)
    {
        var cell = new StackPanel { Tag = "toolbarSceneGroup", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 0 0 L 6.4 0 L 3.2 4 Z"), Width = 6.4, Height = 4 };
        icon.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("SctIconBrush"));
        var chevron = new Button { Content = icon }; chevron.Classes.Add("toolIcon"); chevron.Classes.Add("tbGroupChevron");
        Avalonia.Automation.AutomationProperties.SetName(chevron, "展开工具组");
        var scroll = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = 900 };
        var surface = new ContentControl { Content = scroll, Padding = new Thickness(8, 6), BorderThickness = new Thickness(1), MinHeight = 44 };
        surface.Bind(Control.ThemeProperty, new DynamicResourceExtension("SctContinuousToolbarSurfaceTheme"));
        surface.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("SctPanelBrush"));
        surface.Bind(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("SctBorderBrush"));
        surface.Bind(TemplatedControl.CornerRadiusProperty, new DynamicResourceExtension("SctRadiusToolbar"));
        // 横向展开保持与主栏同一高度，录制中不会把副栏翻进选区上方。
        var popup = new Popup { Tag = "toolbarSceneGroupPopup", Child = surface, PlacementTarget = chevron, Placement = PlacementMode.Right, HorizontalOffset = 6, IsLightDismissEnabled = true };
        var group = new GroupView { Members = members, HeadId = members[0], Cell = cell, Head = head, Row = row, Chevron = chevron, Popup = popup };
        chevron.Classes.Add("toolbarGroupedTool");
        foreach (string id in members.Where(id => id != "|"))
        foreach (var control in _map[id])
        foreach (var button in (control is Button b ? new[] { b } : control.GetLogicalDescendants().OfType<Button>())
            .Where(b => b.Classes.Contains("tool") || b.Classes.Contains("toolIcon")))
            button.Classes.Add("toolbarGroupedTool");
        chevron.Click += (_, _) =>
        {
            bool open = !popup.IsOpen; CloseGroups(); popup.IsOpen = open;
        };
        popup.Opened += (_, _) =>
        {
            chevron.Classes.Add("opened");
            if (_owner?.Screens.ScreenFromWindow(_owner) is { } screen)
                scroll.MaxWidth = Math.Max(1, screen.WorkingArea.Width / _owner.RenderScaling - 50);
        };
        popup.Closed += (_, _) => { chevron.Classes.Remove("opened"); CloseNested(group); };
        surface.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        cell.Children.Add(head); cell.Children.Add(chevron); cell.Children.Add(popup);
        _groups.Add(group); FillGroup(group); return cell;
    }
    private void FillGroup(GroupView group)
    {
        foreach (string id in group.Members.Where(id => id != "|")) foreach (var control in _map[id])
        { Detach(control); control.Classes.Remove("tbGroupHead"); }
        group.Head.Children.Clear(); group.Row.Children.Clear();
        foreach (var control in _map[group.HeadId]) { control.Classes.Add("tbGroupHead"); group.Head.Children.Add(control); }
        var pending = new List<Control>();
        foreach (string id in group.Members)
        {
            if (id == group.HeadId) continue;
            if (id == "|") { if (pending.Count > 0 && !Equals(pending[^1].Tag, DividerTag)) pending.Add(CreateDivider()); }
            else pending.AddRange(_map[id]);
        }
        if (pending.LastOrDefault() is { } divider && Equals(divider.Tag, DividerTag)) pending.Remove(divider);
        foreach (var control in pending) group.Row.Children.Add(control);
    }

    private Control CreateDivider()
    {
        var sample = _dividers.OfType<Border>().FirstOrDefault();
        var border = new Border { Tag = DividerTag, Width = sample?.Width ?? 1, Height = sample?.Height ?? 20,
            Margin = sample?.Margin ?? new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("SctSeparatorBrush"));
        return border;
    }
}
