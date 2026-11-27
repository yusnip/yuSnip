using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ScreenCaptureTool.Core.Actions;

namespace ScreenCaptureTool.Controls.ActionEditor;

/// <summary>
/// 极简安静的左侧模块库：无冗余色点、无彩色图标底盒、无右侧小胶囊，
/// 仅保留清晰分组标题、单色克制图标与模块名称；支持单击追加与直接拖入右侧落位。
/// </summary>
internal sealed class ActionPalettePanel : ContinuousBorder
{
    private const double DragThreshold = 5.0;

    private readonly TextBox _searchBox;
    private readonly StackPanel _listPanel;
    private Window? _hostWindow;
    private ActionModuleDescriptor? _pressedModule;
    private ContinuousBorder? _pressedTile;
    private Point _pressWindowPoint;
    private bool _isDragging;

    public event Action<ActionModuleDescriptor>? ModuleSelected;
    public event Action<ActionModuleDescriptor, Point>? ModuleDragStarted;
    public event Action<ActionModuleDescriptor, Point>? ModuleDragMoved;
    public event Action<ActionModuleDescriptor, Point>? ModuleDragDropped;
    public event Action? ModuleDragCanceled;

    public bool IsDragging => _isDragging;

    public ActionPalettePanel()
    {
        Classes.Add("actionPaletteSidebar");
        Width = 224;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        var searchWrap = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        _searchBox = new TextBox
        {
            Watermark = "搜索模块",
            FontSize = 12.5,
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(10, 4, 10, 4),
        };
        _searchBox.TextChanged += (_, _) => RebuildList();
        searchWrap.Children.Add(_searchBox);
        root.Children.Add(searchWrap);

        _listPanel = new StackPanel { Spacing = 12, Margin = new Thickness(10, 2, 10, 14) };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _listPanel,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Child = root;
        RebuildList();
    }

    public void AttachHostWindow(Window window) => _hostWindow = window;

    public void CancelActiveDrag()
    {
        if (_pressedModule == null) return;
        bool wasDragging = _isDragging;
        if (_pressedTile != null) _pressedTile.Opacity = 1.0;
        _pressedModule = null;
        _pressedTile = null;
        _isDragging = false;
        if (wasDragging) ModuleDragCanceled?.Invoke();
    }

    private void RebuildList()
    {
        _listPanel.Children.Clear();
        string query = (_searchBox.Text ?? "").Trim();
        var categories = new[]
        {
            ActionModuleCategory.Input,
            ActionModuleCategory.Process,
            ActionModuleCategory.Output,
            ActionModuleCategory.Control
        };

        int totalMatched = 0;
        foreach (var cat in categories)
        {
            var matched = ActionModuleRegistry.Modules
                .Where(m => m.Category == cat && Matches(m, query))
                .ToList();
            if (matched.Count == 0) continue;
            totalMatched += matched.Count;

            var section = new StackPanel { Spacing = 2 };
            var catText = new TextBlock
            {
                Text = ActionModuleRegistry.CategoryTitle(cat),
                FontSize = 11.5,
                FontWeight = FontWeight.Medium,
                Margin = new Thickness(8, 0, 8, 4),
                Opacity = 0.72,
            };
            catText.Classes.Add("muted");
            section.Children.Add(catText);

            foreach (var module in matched)
            {
                section.Children.Add(BuildModuleTile(module));
            }
            _listPanel.Children.Add(section);
        }

        if (totalMatched == 0)
        {
            var empty = new TextBlock
            {
                Text = "没有匹配的模块",
                FontSize = 12,
                Margin = new Thickness(8, 12),
            };
            empty.Classes.Add("muted");
            _listPanel.Children.Add(empty);
        }
    }

    private Control BuildModuleTile(ActionModuleDescriptor module)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var icon = ActionModuleIconHelper.CreateIcon(module.IconKey, accentOverride: null, size: 13.5);
        icon.Margin = new Thickness(0, 0, 9, 0);
        icon.Opacity = 0.72;
        row.Children.Add(icon);

        var nameText = new TextBlock
        {
            Text = module.Name,
            FontSize = 12.5,
            FontWeight = FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);

        var tile = new ContinuousBorder
        {
            Classes = { "actionPaletteTile" },
            Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 32,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };
        ToolTip.SetTip(tile, module.ShortDescription + "\n单击追加，或按住拖入右侧指定位置");

        tile.PointerPressed += (_, e) =>
        {
            if (_hostWindow == null || !e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            _pressedModule = module;
            _pressedTile = tile;
            _isDragging = false;
            _pressWindowPoint = e.GetPosition(_hostWindow);
            e.Pointer.Capture(tile);
            e.Handled = true;
        };

        tile.PointerMoved += (_, e) =>
        {
            if (_hostWindow == null || !ReferenceEquals(_pressedTile, tile) || _pressedModule == null) return;
            var cur = e.GetPosition(_hostWindow);
            if (!_isDragging)
            {
                double dx = cur.X - _pressWindowPoint.X;
                double dy = cur.Y - _pressWindowPoint.Y;
                if (dx * dx + dy * dy < DragThreshold * DragThreshold) return;
                _isDragging = true;
                tile.Opacity = 0.45;
                ModuleDragStarted?.Invoke(_pressedModule, cur);
            }
            ModuleDragMoved?.Invoke(_pressedModule, cur);
            e.Handled = true;
        };

        tile.PointerReleased += (_, e) =>
        {
            if (!ReferenceEquals(_pressedTile, tile) || _pressedModule == null) return;
            var mod = _pressedModule;
            bool wasDragging = _isDragging;
            var dropPt = _hostWindow != null ? e.GetPosition(_hostWindow) : default;
            tile.Opacity = 1.0;
            _pressedModule = null;
            _pressedTile = null;
            _isDragging = false;
            if (ReferenceEquals(e.Pointer.Captured, tile)) e.Pointer.Capture(null);
            e.Handled = true;

            if (wasDragging) ModuleDragDropped?.Invoke(mod, dropPt);
            else ModuleSelected?.Invoke(mod);
        };

        tile.PointerCaptureLost += (_, _) =>
        {
            if (ReferenceEquals(_pressedTile, tile)) CancelActiveDrag();
        };

        return tile;
    }

    private static bool Matches(ActionModuleDescriptor module, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        return module.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || module.ShortDescription.Contains(query, StringComparison.OrdinalIgnoreCase)
            || module.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ActionModuleRegistry.CategoryTitle(module.Category).Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
