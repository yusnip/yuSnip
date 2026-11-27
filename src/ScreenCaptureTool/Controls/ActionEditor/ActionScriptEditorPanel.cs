using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Core.Scripting;

namespace ScreenCaptureTool.Controls.ActionEditor;

/// <summary>
/// 高级脚本源码编辑器：快捷插入工具栏 + @ 光标菜单 + 实时语法诊断条 + 可点击插入的指令速查表。
/// </summary>
internal sealed class ActionScriptEditorPanel : Grid
{
    private readonly TextBox _editor;
    private readonly ContinuousBorder _statusBanner;
    private readonly TextBlock _statusText;
    private readonly ContinuousBorder _cheatSheetPanel;
    private readonly Button _toggleCheatBtn;
    private ContextMenu? _atMenu;
    private bool _suppressChange;

    public event Action<string>? ScriptChanged;

    public string ScriptText
    {
        get => _editor.Text ?? "";
        set
        {
            if (string.Equals(_editor.Text ?? "", value ?? "", StringComparison.Ordinal)) return;
            _suppressChange = true;
            _editor.Text = value ?? "";
            _suppressChange = false;
            RefreshValidation();
        }
    }

    public ActionScriptEditorPanel()
    {
        RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto");
        Margin = new Thickness(16, 12, 16, 12);

        // ── 顶部快捷插入栏 ──
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var leftBar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var cat in new[] { ActionModuleCategory.Input, ActionModuleCategory.Process, ActionModuleCategory.Output, ActionModuleCategory.Control })
        {
            leftBar.Children.Add(BuildCategoryInsertButton(cat));
        }
        toolbar.Children.Add(leftBar);

        _toggleCheatBtn = new Button
        {
            Content = "指令速查",
            Classes = { "secondary" },
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0),
            FontSize = 12,
        };
        _toggleCheatBtn.Click += (_, _) =>
        {
            if (_cheatSheetPanel == null) return;
            _cheatSheetPanel.IsVisible = !_cheatSheetPanel.IsVisible;
            _toggleCheatBtn.Content = _cheatSheetPanel.IsVisible ? "收起速查" : "指令速查";
        };
        Grid.SetColumn(_toggleCheatBtn, 1);
        toolbar.Children.Add(_toggleCheatBtn);
        Children.Add(toolbar);

        // ── 代码输入区 ──
        _editor = new TextBox
        {
            FontFamily = new FontFamily("Consolas, 'Cascadia Code', monospace"),
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Watermark = "一行一条 yuSnip 指令；可点击上方快捷插入或输入 @ 选择模块与变量",
        };
        _editor.AddHandler(TextInputEvent, OnEditorTextInput, RoutingStrategies.Tunnel);
        _editor.TextChanged += (_, _) =>
        {
            _atMenu?.Close();
            RefreshValidation();
            if (!_suppressChange) ScriptChanged?.Invoke(_editor.Text ?? "");
        };
        Grid.SetRow(_editor, 1);
        Children.Add(_editor);

        // ── 实时语法校验条 ──
        _statusText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _statusBanner = new ContinuousBorder
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Child = _statusText,
        };
        Grid.SetRow(_statusBanner, 2);
        Children.Add(_statusBanner);

        // ── 可点击插入的指令速查表 ──
        _cheatSheetPanel = BuildCheatSheet();
        _cheatSheetPanel.Margin = new Thickness(0, 8, 0, 0);
        _cheatSheetPanel.IsVisible = false;
        Grid.SetRow(_cheatSheetPanel, 3);
        Children.Add(_cheatSheetPanel);

        RefreshValidation();
    }

    public void FocusEditor() => _editor.Focus();
    public void CloseMenus() => _atMenu?.Close();

    private Button BuildCategoryInsertButton(ActionModuleCategory category)
    {
        var btn = new Button
        {
            Content = ActionModuleRegistry.CategoryTitle(category),
            Classes = { "secondary" },
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            var (start, end) = GetSelectionRange();
            var priorResults = CollectPriorResults(ScriptText, start);
            foreach (var mod in ActionModuleRegistry.Modules.Where(m => m.Category == category))
            {
                if (mod.NeedsInput)
                {
                    var sub = new MenuItem { Header = mod.Name };
                    ToolTip.SetTip(sub, mod.ShortDescription);
                    var draft = ActionModuleRegistry.CreateStep(mod.Id, [], 0);
                    var compat = ActionModuleRegistry.CompatibleInputs(draft, priorResults);
                    if (compat.Count > 0)
                    {
                        foreach (var r in compat)
                        {
                            string cmd = mod.ToScript(draft with { InputName = r.Name });
                            var mi = new MenuItem { Header = r.DisplayLabel };
                            mi.Click += (_, _) => InsertAtSelection(cmd);
                            sub.Items.Add(mi);
                        }
                    }
                    else
                    {
                        string cmd = mod.ToScript(draft);
                        var mi = new MenuItem { Header = "直接插入模板（需补输入名）" };
                        mi.Click += (_, _) => InsertAtSelection(cmd);
                        sub.Items.Add(mi);
                    }
                    menu.Items.Add(sub);
                }
                else
                {
                    var mi = new MenuItem { Header = mod.Name };
                    ToolTip.SetTip(mi, mod.ShortDescription);
                    var draft = CreateNonCollidingStep(mod.Id, ScriptText);
                    string cmd = mod.ToScript(draft);
                    mi.Click += (_, _) => InsertAtSelection(cmd);
                    menu.Items.Add(mi);
                }
            }
            menu.ShowAt(btn);
        };
        return btn;
    }

    private ContinuousBorder BuildCheatSheet()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        (string Label, string Snippet, string Tip)[] quickSnippets =
        [
            ("区域截图", "yuSnip.captureImage('原图');", "框选并保存图片结果"),
            ("标注后再继续", "yuSnip.captureImage('原图', 'toolbar=show');", "先显示工具栏标注，确认后走后续步骤"),
            ("精致卡片截图", "yuSnip.captureImage('原图', 'radius=12', 'stroke=2', 'shadow=on');", "单次覆盖圆角、描边与阴影"),
            ("纯净截图", "yuSnip.captureImage('原图', 'radius=0', 'stroke=0', 'shadow=off');", "强制无圆角无描边无阴影"),
            ("当前屏截图", "yuSnip.captureScreen('当前屏', 'scope=current');", "直接截取鼠标所在显示器"),
            ("固定坐标截图", "yuSnip.captureRegion(0, 0, 800, 600, '区域截图');", "无界面截取固定区域"),
            ("焦点贴图", "yuSnip.readSticker('焦点贴图');", "读取当前焦点图片贴图"),
            ("OCR 合并段落", "yuSnip.recognizeResult('原图', '识别文字', 'cleanup=merge');", "离线 OCR 并自动合并换行"),
            ("翻译为英文", "yuSnip.translateResult('Text', '原图', '译文', 'to=en');", "单次覆盖目标语言为英文"),
            ("智能抠图", "yuSnip.mattingResult('Default', '原图', '抠图结果');", "离线去背景提取主体"),
            ("穿透参考贴图", "yuSnip.pinResult('原图', 'opacity=70', 'pass=on');", "半透明鼠标穿透贴图"),
            ("另存为 JPG", "yuSnip.saveResult('原图', 'mode=dialog', 'format=jpg');", "弹出另存为对话框存为 JPG"),
            ("AI 固定提示词", "yuSnip.visionResult('Custom', '原图', 'prompt=请总结图中重点');", "使用固定提示词向 AI 识图提问"),
            ("等待延时", "yuSnip.wait(1000);", "暂停 1000 毫秒"),
            ("横向长截图", "yuSnip.longScroll('Horizontal');", "启动横向长截图拼接"),
            ("屏幕录制", "yuSnip.screenRecord();", "启动录屏"),
            ("// 备注", "// 在这里写备注说明", "整行注释")
        ];

        foreach (var (label, snippet, tip) in quickSnippets)
        {
            var chip = new Button
            {
                Content = label,
                Classes = { "secondary" },
                Height = 25,
                MinHeight = 25,
                Padding = new Thickness(8, 0),
                Margin = new Thickness(0, 0, 5, 5),
                FontSize = 11.5,
            };
            ToolTip.SetTip(chip, $"{tip}\n点击插入：{snippet}");
            string toInsert = snippet;
            chip.Click += (_, _) => InsertAtSelection(toInsert);
            wrap.Children.Add(chip);
        }

        var box = new ContinuousBorder
        {
            Classes = { "actionStepCard" },
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "点击任意指令标签即可直接插入光标处（支持 // 行注释与 @ 快捷呼出）：", FontSize = 11.5, Classes = { "muted" } },
                    wrap
                }
            }
        };
        return box;
    }

    private void RefreshValidation()
    {
        string text = _editor.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _statusText.Text = "暂无指令：点击上方按钮插入模块，或直接输入 yuSnip 指令。";
            _statusText.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctTextMutedBrush"));
            _statusBanner.Background = new SolidColorBrush(Color.FromArgb(14, 128, 128, 128));
            return;
        }

        if (ScriptExecutor.TryParse(text, out var calls, out string error))
        {
            bool visualOk = ActionModuleRegistry.TryParseSteps(text, out _);
            _statusText.Text = visualOk
                ? $"语法有效（共 {calls.Count} 步，可切回流程编排）"
                : $"语法有效（共 {calls.Count} 步，含纯脚本指令，请在本视图编辑）";
            _statusText.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctOkBrush"));
            _statusBanner.Background = new SolidColorBrush(Color.FromArgb(22, 31, 122, 56));
        }
        else
        {
            _statusText.Text = error;
            _statusText.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctDangerBrush"));
            _statusBanner.Background = new SolidColorBrush(Color.FromArgb(24, 220, 38, 38));
        }
    }

    private void OnEditorTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text != "@" || _editor.IsReadOnly) return;
        string text = _editor.Text ?? "";
        var (start, end) = GetSelectionRange();
        int lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] is not ('\r' or '\n')) lineStart--;
        char quote = '\0';
        for (int i = lineStart; i < start; i++)
        {
            if (quote != '\0') { if (text[i] == quote) quote = '\0'; }
            else if (text[i] is '\'' or '"') quote = text[i];
            else if (text[i] == '/' && i + 1 < start && text[i + 1] == '/') return;
        }
        if (quote != '\0') return;
        if (start == end && !string.IsNullOrWhiteSpace(text[lineStart..start])
            && !ScriptExecutor.TryParse(text[lineStart..start], out _, out _, validateOrder: false))
            start = end = lineStart;

        e.Handled = true;
        _atMenu?.Close();
        var originalCtx = _editor.ContextMenu;
        var menu = BuildAtContextMenu(text, start, end);
        _atMenu = menu;
        _editor.ContextMenu = menu;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.VerticalOffset = 4;
        var presenter = _editor.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter != null)
        {
            var caret = presenter.TextLayout.HitTestTextPosition(Math.Clamp(_editor.CaretIndex, 0, text.Length));
            if (presenter.TranslatePoint(caret.TopLeft, _editor) is { } pt)
                menu.PlacementRect = new Rect(pt, new Size(1, caret.Height));
        }
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_atMenu, menu))
            {
                _atMenu = null;
                _editor.ContextMenu = originalCtx;
            }
        };
        menu.Open(_editor);
    }

    private ContextMenu BuildAtContextMenu(string text, int start, int end)
    {
        var menu = new ContextMenu();
        var prior = CollectPriorResults(text, start);
        foreach (var cat in new[] { ActionModuleCategory.Input, ActionModuleCategory.Process, ActionModuleCategory.Output, ActionModuleCategory.Control })
        {
            var group = new MenuItem { Header = ActionModuleRegistry.CategoryTitle(cat) };
            foreach (var mod in ActionModuleRegistry.Modules.Where(m => m.Category == cat))
            {
                var draft = CreateNonCollidingStep(mod.Id, text);
                if (mod.NeedsInput)
                {
                    var compat = ActionModuleRegistry.CompatibleInputs(draft, prior);
                    var item = new MenuItem { Header = mod.Name, IsEnabled = compat.Count > 0 };
                    foreach (var r in compat)
                    {
                        string cmd = mod.ToScript(draft with { InputName = r.Name });
                        var sub = new MenuItem { Header = r.DisplayLabel };
                        sub.Click += (_, _) => InsertRange(text, start, end, cmd);
                        item.Items.Add(sub);
                    }
                    ToolTip.SetTip(item, compat.Count == 0 ? "请先在上方插入能产出兼容内容的模块" : mod.ShortDescription);
                    group.Items.Add(item);
                }
                else
                {
                    string cmd = mod.ToScript(draft);
                    var item = new MenuItem { Header = mod.Name };
                    ToolTip.SetTip(item, mod.ShortDescription);
                    item.Click += (_, _) => InsertRange(text, start, end, cmd);
                    group.Items.Add(item);
                }
            }
            menu.Items.Add(group);
        }
        return menu;
    }

    private (int Start, int End) GetSelectionRange()
    {
        string text = _editor.Text ?? "";
        int s = Math.Clamp(Math.Min(_editor.SelectionStart, _editor.SelectionEnd), 0, text.Length);
        int e = Math.Clamp(Math.Max(_editor.SelectionStart, _editor.SelectionEnd), s, text.Length);
        if (s == e) s = e = Math.Clamp(_editor.CaretIndex, 0, text.Length);
        return (s, e);
    }

    private void InsertAtSelection(string command)
    {
        var (start, end) = GetSelectionRange();
        InsertRange(_editor.Text ?? "", start, end, command);
    }

    private void InsertRange(string original, int start, int end, string command)
    {
        if ((_editor.Text ?? "") != original || _editor.IsReadOnly) return;
        _atMenu?.Close();
        string before = original[..start], after = original[end..];
        string prefix = before.Length > 0 && before[^1] is not ('\r' or '\n') && !string.IsNullOrWhiteSpace(before[(before.LastIndexOf('\n') + 1)..])
            ? Environment.NewLine : "";
        string suffix = after.Length > 0 && after[0] is not ('\r' or '\n') ? Environment.NewLine : "";
        _editor.Focus();
        _editor.SelectionStart = start;
        _editor.SelectionEnd = end;
        _editor.SelectedText = prefix + command + suffix;
        int caret = start + prefix.Length + command.Length + suffix.Length;
        _editor.CaretIndex = _editor.SelectionStart = _editor.SelectionEnd = caret;
    }

    private static List<ActionDataRef> CollectPriorResults(string text, int upToIndex)
    {
        var list = new List<ActionDataRef>();
        if (!ScriptExecutor.TryParse(text[..Math.Clamp(upToIndex, 0, text.Length)], out var earlier, out _, validateOrder: false))
            return list;
        var kinds = new Dictionary<string, ActionContentKind>(StringComparer.Ordinal);
        var cur = ActionContentKind.None;
        for (int i = 0; i < earlier.Count; i++)
        {
            var call = earlier[i];
            var inputKind = call.InputName != null && kinds.TryGetValue(call.InputName, out var k) ? k : cur;
            if (!call.ProducesResult) continue;
            cur = ScriptExecutor.ContentAfter(call, inputKind);
            if (call.ResultName == null) continue;
            kinds[call.ResultName] = cur;
            list.Add(new(call.ResultName, cur, i));
        }
        return list;
    }

    private static ActionStepNode CreateNonCollidingStep(string moduleId, string fullText)
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in fullText.ReplaceLineEndings("\n").Split('\n'))
        {
            if (ScriptExecutor.TryParse(line, out var calls, out _, validateOrder: false))
                foreach (var c in calls)
                    if (c.ResultName != null) reserved.Add(c.ResultName);
        }
        var desc = ActionModuleRegistry.GetRequired(moduleId);
        var step = ActionModuleRegistry.CreateStep(moduleId, [], 0);
        if (desc.ProducesResult)
            step = step with { ResultName = ActionModuleRegistry.UniqueResultName(desc.DefaultResultLabel ?? desc.Name, [], reserved) };
        return step;
    }
}
