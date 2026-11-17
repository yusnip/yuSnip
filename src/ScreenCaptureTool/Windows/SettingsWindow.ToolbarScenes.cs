using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.LongScroll.Windows;

namespace ScreenCaptureTool.Windows;

public partial class SettingsWindow
{
    private ToolbarScene _toolbarScene;
    private ToolbarSceneDefinition CurrentToolbarDefinition => ToolbarScenes.Get(_toolbarScene);
    private ToolbarButtonInfo? FindToolbarTool(string id) => _toolbarScene == ToolbarScene.Capture ? ToolbarRegistry.Find(id) : CurrentToolbarDefinition.Find(id);
    private List<string> ToolbarGroupMembers(string? token) => _toolbarScene == ToolbarScene.Capture ? ToolbarRegistry.GroupMembers(token) : CurrentToolbarDefinition.GroupMembers(token);
    private string MakeToolbarGroupToken(IEnumerable<string> members) => _toolbarScene == ToolbarScene.Capture ? ToolbarRegistry.MakeGroupToken(members) : CurrentToolbarDefinition.MakeGroupToken(members);
    private List<string> CleanToolbarGroup(IEnumerable<string> members) => ToolbarRegistry.CleanGroup(members, FindToolbarTool);
    private bool IsToolbarNoGroupId(string id) => !CurrentToolbarDefinition.CanGroup(id);
    private readonly Dictionary<ToolbarScene, Dictionary<string, Control>> _scenePreviews = new();

    private void OnToolbarSceneChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_toolbarLoaded || _isLoading || CmbToolbarScene.SelectedIndex < 0) return;
        var next = (ToolbarScene)CmbToolbarScene.SelectedIndex;
        if (next == _toolbarScene) return;
        ResetToolbarDrag();
        AutoSaveSettings();
        _toolbarScene = next;
        _toolbarDraft = CurrentToolbarDefinition.Parse(ToolbarScenes.Read(_settings, next));
        RebuildGroupCards();
        _groupRows.Clear(); _groupCardBorders.Clear();
        RenderToolbarPage();
    }

    private ContextMenu CreateToolbarCellMenu(string token, int index)
    {
        var menu = new ContextMenu();
        void Add(string label, int target, bool enabled)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => MoveToolbarCell(index, target);
            menu.Items.Add(item);
        }
        Add("前移", index - 1, index > 0);
        Add("后移", index + 1, index + 1 < _toolbarDraft.Count);
        Add("移到开头", 0, index > 0);
        Add("移到末尾", _toolbarDraft.Count - 1, index + 1 < _toolbarDraft.Count);
        var hide = new MenuItem { Header = "隐藏", IsEnabled = CurrentToolbarDefinition.CanHide(token, _toolbarDraft) };
        if (!hide.IsEnabled) ToolTip.SetTip(hide, "此项为必要入口，不能隐藏。");
        hide.Click += (_, _) =>
        {
            if (index >= _toolbarDraft.Count || !CurrentToolbarDefinition.CanHide(token, _toolbarDraft)) return;
            _toolbarDraft.RemoveAt(index); RenderToolbarPage(); AutoSaveSettings();
        };
        menu.Items.Add(hide);
        return menu;
    }

    private void MoveToolbarCell(int index, int target)
    {
        if (index < 0 || index >= _toolbarDraft.Count) return;
        string moved = _toolbarDraft[index]; _toolbarDraft.RemoveAt(index);
        _toolbarDraft.Insert(Math.Clamp(target, 0, _toolbarDraft.Count), moved);
        RenderToolbarPage(); AutoSaveSettings();
    }

    /// <summary>预览复用真实宿主控件，不把文字按钮／播放进度等复合单元画成统一小图标。</summary>
    private Control TakeScenePreview(string id)
    {
        if (!_scenePreviews.TryGetValue(_toolbarScene, out var controls))
        {
            controls = CreateScenePreviews(_toolbarScene);
            _scenePreviews.Add(_toolbarScene, controls);
        }
        Control control = controls[id];
        if (control.Parent is Panel parent) parent.Children.Remove(control);
        control.Classes.Remove("tbGroupHead"); control.IsVisible = true; control.IsHitTestVisible = false;
        return control;
    }

    private Control? CreateScenePoolIcon(string id, double size)
    {
        // 文字操作没有主图标；下拉／方向指示不应被当成工具图标，也不留空占位。
        if ((_toolbarScene, id) is (ToolbarScene.RecordPreparation, "quality" or "countdown" or "start")
            or (ToolbarScene.RecordControl, "finish") or (ToolbarScene.VideoPreview, "export")
            or (ToolbarScene.LongScroll, "autoscroll")) return null;
        if (ToolbarScenes.Get(ToolbarScene.StickerAnnotation).Find(id) != null && AnnotationToolButton.CreateIcon(id) is { } annotation)
        {
            annotation.Width = annotation.Height = size;
            return annotation;
        }
        if (!_scenePreviews.TryGetValue(_toolbarScene, out var controls))
            _scenePreviews[_toolbarScene] = controls = CreateScenePreviews(_toolbarScene);
        Control source = controls[id];
        string? buttonName = id switch { "audio" => "BtnAudio", "keymouse" => "BtnKeyMouse", "fx" => "BtnFx",
            "pause" => "BtnPause", "playback" => "BtnPlay", _ => null };
        if (buttonName != null) source = source.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == buttonName);
        // 只读主按钮内容，不遍历弹层；叠放图标的所有可见图层必须在同一原始画板缩放。
        source = source switch { Button b => (Control)b.Content!, Border b => b.Child!, _ => source };
        var paths = (source is Avalonia.Controls.Shapes.Path single ? new[] { single }
            : source.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.IsVisible).ToArray());
        if (paths.Length == 0) return null;
        var canvas = new Panel { Width = paths.Max(p => double.IsFinite(p.Width) ? p.Width : 16),
            Height = paths.Max(p => double.IsFinite(p.Height) ? p.Height : 16) };
        foreach (var path in paths)
        {
            var icon = new Avalonia.Controls.Shapes.Path { Data = path.Data,
                Width = double.IsFinite(path.Width) ? path.Width : 16, Height = double.IsFinite(path.Height) ? path.Height : 16,
                Stretch = id is "keymouse" or "fx" ? Stretch.None : Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (string style in path.Classes) icon.Classes.Add(style);
            // 有 icon 类的描边／填充走共享样式，不能把尚未挂树时的默认值盖回样式。
            if (!path.Classes.Contains("icon"))
            {
                icon.StrokeThickness = path.StrokeThickness; icon.StrokeLineCap = path.StrokeLineCap; icon.StrokeJoin = path.StrokeJoin;
                // 未上栏的原控件尚未解析 DynamicResource，不能用此时的 null 反推它没有填充／描边。
                if (id is "axis" or "trim" || path.Stroke != null)
                    icon.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty, new DynamicResourceExtension("SctIconBrush"));
                if (id is "axis" or "trim" || path.Fill is ISolidColorBrush fill && fill.Color.A == 0) icon.Fill = Brushes.Transparent;
                else if (id == "saveas" || path.Fill != null)
                    icon.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("SctIconBrush"));
            }
            canvas.Children.Add(icon);
        }
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }

    private Control CreateSceneDivider()
    {
        if (!_scenePreviews.TryGetValue(_toolbarScene, out var controls))
            _scenePreviews[_toolbarScene] = controls = CreateScenePreviews(_toolbarScene);
        var sample = controls.GetValueOrDefault("|") as Border;
        var border = new Border { Width = sample?.Width ?? 1,
            Height = sample?.Height ?? (_toolbarScene is ToolbarScene.StickerAnnotation or ToolbarScene.LongScroll ? 20 : 16),
            Margin = sample?.Margin ?? new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("SctSeparatorBrush"));
        return border;
    }

    private static Dictionary<string, Control> CreateScenePreviews(ToolbarScene scene)
    {
        var result = new Dictionary<string, Control>();
        Control Detach(Control control)
        {
            if (control.Parent is Panel panel) panel.Children.Remove(control);
            return control;
        }
        Control Join(params Control[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var child in children) panel.Children.Add(Detach(child));
            return panel;
        }
        if (scene == ToolbarScene.StickerAnnotation)
        {
            foreach (var info in ToolbarScenes.Get(scene).Buttons)
                result[info.Id] = new AnnotationToolButton { ToolKey = info.Id };
        }
        else if (scene == ToolbarScene.RecordPreparation)
        {
            var bar = new RecordOptionsControl();
            // 当前配置可以没有分隔线；控件映射仍须能用于预览和工具池。
            if (((StackPanel)bar.Content!).Children.OfType<Border>().FirstOrDefault() is { } separator) result["|"] = separator;
            result["quality"] = bar.FindControl<Button>("BtnQuality")!;
            result["countdown"] = bar.FindControl<Button>("BtnCountdown")!;
            result["audio"] = (Control)bar.FindControl<Button>("BtnAudio")!.Parent!;
            result["keymouse"] = (Control)bar.FindControl<Button>("BtnKeyMouse")!.Parent!;
            result["start"] = bar.FindControl<Button>("BtnStart")!;
            bar.StopPresetUpdates();
        }
        else if (scene == ToolbarScene.RecordControl)
        {
            var bar = new RecordControlBarWindow();
            result["|"] = bar.FindControl<StackPanel>("NormalRow")!.Children.OfType<Border>().First();
            foreach (var button in bar.AnnotationButtons) result[button.ToolKey] = button;
            result["fx"] = (Control)bar.FindControl<Button>("BtnFx")!.Parent!;
            result["pause"] = Join(bar.FindControl<Button>("BtnPause")!, bar.FindControl<Button>("BtnResume")!);
            result["restart"] = bar.FindControl<Button>("BtnRestart")!;
            result["finish"] = bar.FindControl<Button>("BtnComplete")!;
            foreach (var value in result.Values) Detach(value);
            bar.Close();
        }
        else if (scene == ToolbarScene.VideoPreview)
        {
            var bar = new VideoStickerWindow();
            result["playback"] = bar.FindControl<StackPanel>("PlayGroup")!;
            if (((StackPanel)result["playback"]).Children.LastOrDefault() is Border divider)
            {
                ((StackPanel)result["playback"]).Children.Remove(divider);
                result["|"] = divider;
            }
            result["export"] = bar.FindControl<StackPanel>("ExportGroup")!;
            result["copy"] = bar.FindControl<Button>("BtnCopy")!;
            result["quicksave"] = bar.FindControl<Button>("BtnQuickSave")!;
            result["saveas"] = bar.FindControl<Button>("BtnSaveAs")!;
            result["close"] = bar.FindControl<Button>("BtnClose")!;
            foreach (var value in result.Values) Detach(value);
            bar.Close();
        }
        else
        {
            var bar = new LongScrollControlWindow();
            result["axis"] = bar.FindControl<Button>("BtnToggleAxis")!;
            result["autoscroll"] = Join(bar.FindControl<Button>("BtnAutoScroll")!, bar.FindControl<Control>("AutoScrollFreqBox")!, bar.FindControl<Control>("AutoScrollDirectionBox")!);
            result["trim"] = bar.FindControl<Control>("BtnManualOffset")!;
            result["sticker"] = bar.FindControl<Button>("BtnSticker")!;
            result["quicksave"] = bar.FindControl<Button>("BtnQuickSave")!;
            result["saveas"] = bar.FindControl<Button>("BtnSaveAs")!;
            result["cancel"] = bar.FindControl<Button>("BtnStop")!;
            result["finish"] = bar.FindControl<Button>("BtnFinish")!;
            foreach (var value in result.Values) Detach(value);
            bar.Close();
        }
        return result;
    }
}
