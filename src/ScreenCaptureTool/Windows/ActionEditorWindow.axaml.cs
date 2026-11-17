using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Layout;
using Avalonia.Media;
using ScreenCaptureTool.Core.Scripting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Controls;

namespace ScreenCaptureTool.Windows;

public partial class ActionEditorWindow : Window
{
    // 键盘辅助键身份与名称由 ActionKeyboardBinding 共用；原生录制避免控件抢先处理 Tab／Space。

    private readonly bool _isNew;
    private readonly ActionDefinition _originalAction;
    private uint _modifiers;
    private uint _virtualKey;
    private bool _isRecording;
    private string _actionId;
    private bool _isBuiltin;
    private bool _isUpdatingPresetUi = true;
    private bool _updatingSteps;
    private List<ActionStepChoice> _steps = [];
    private readonly List<(ComboBox Picker, int Position)> _inputPickers = [];
    private readonly List<(TextBlock Title, TextBlock Summary, int Position)> _moduleHeaders = [];
    private int _expandedStep = -1;
    private readonly ListReorderBehavior _moduleDrag = new();
    private ContinuousBorder? _draggedModuleRow;
    private Control? _draggedModuleParameters;
    private ContinuousBorder? _moduleDropPlaceholder;
    private SizeToContent? _moduleDragSizeToContent;
    private double _moduleDragWindowHeight;
    private int _moduleInsertIndex = -1;
    private string _customName = "我的动作";
    private string _customScript = ActionStepCatalog.DefaultScript;
    private bool _customAdvanced;
    private ContextMenu? _scriptInsertMenu;
    private readonly System.Collections.Generic.IReadOnlyList<ActionDefinition>? _otherActions;
    private readonly System.Collections.Generic.IReadOnlyList<Services.BuiltinShortcutsStore.BuiltinShortcut> _otherShortcuts;
    private bool _updatingMouseUi;
    private bool _ownsHotkeySuspension;
    private ActionMouseKey _mousePrefixKey;
    private Services.MousePrefixRecorder? _prefixRecorder;
    private int _prefixRecordingGeneration;
    private Services.KeyboardShortcutRecorder? _keyboardRecorder;
    private int _keyboardRecordingGeneration;

    /// <summary>保存成功的动作对象</summary>
    public ActionDefinition? ResultAction { get; private set; }

    public ActionEditorWindow() : this(null)
    {
    }

    public ActionEditorWindow(ActionDefinition? actionToEdit, System.Collections.Generic.IReadOnlyList<ActionDefinition>? otherActions = null,
        System.Collections.Generic.IReadOnlyList<Services.BuiltinShortcutsStore.BuiltinShortcut>? otherShortcuts = null)
    {
        InitializeComponent();
        _moduleDrag.Canceled += ClearModuleDragVisuals;
        TxtScript.AddHandler(TextInputEvent, OnScriptTextInput, RoutingStrategies.Tunnel);

        _isNew = actionToEdit == null;
        _otherActions = otherActions;
        _otherShortcuts = otherShortcuts ?? Services.BuiltinShortcutsStore.Load();
        _originalAction = actionToEdit ?? new ActionDefinition
        {
            Id = ActionDefinition.NewCustomId(),
            Name = "我的动作",
            Script = ActionStepCatalog.DefaultScript,
            IsBuiltin = false,
            Enabled = true,
        };

        _actionId = _originalAction.Id;
        _isBuiltin = _originalAction.IsBuiltin;
        if (!_isBuiltin) { _customName = _originalAction.Name; _customScript = _originalAction.Script; }
        _modifiers = _originalAction.HotKeyModifiers;
        _virtualKey = _originalAction.HotKeyVirtualKey;
        _mousePrefixKey = _originalAction.MousePrefixKey;

        TxtWindowTitle.Text = _isNew ? "新增动作" : (_isBuiltin ? $"编辑动作 - {_originalAction.Name}（内置）" : $"编辑动作 - {_originalAction.Name}");
        BtnSave.Content = _isNew ? "添加" : "保存";

        PopulatePresets();
        LoadActionData();
        _updatingMouseUi = true;
        ChkMouseShift.IsChecked = (_originalAction.MouseModifiers & 4) != 0;
        ChkMouseCtrl.IsChecked = (_originalAction.MouseModifiers & 2) != 0;
        ChkMouseAlt.IsChecked = (_originalAction.MouseModifiers & 1) != 0;
        ChkMouseWin.IsChecked = (_originalAction.MouseModifiers & 8) != 0;
        foreach (var control in MouseButtonOptions.Children)
            if (control is RadioButton radio)
                radio.IsChecked = int.TryParse(radio.Tag?.ToString(), out int button)
                    && button == (_originalAction.HasMouseBinding ? (int)_originalAction.MouseButton : 1);
        _updatingMouseUi = false;
        RefreshMouseCapabilities(_originalAction.HasMouseBinding ? _originalAction.MouseTrigger : null);
        // 新增默认走预设入口，同时同步选中预设的 ID、名称、指令与推荐快捷键。
        if (_isNew) OnActionTypeChanged(RadPresetType, new RoutedEventArgs());
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen != null) MouseFormScroll.MaxHeight = Math.Max(220, Math.Min(620, screen.WorkingArea.Height / screen.Scaling - 160));
        };

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => { if (_isRecording) StopRecording(false); StopMousePrefixRecording(); _scriptInsertMenu?.Close(); _moduleDrag.Reset(); ClearModuleDragVisuals(); RestoreEditorHotkeys(); };
        Deactivated += (_, _) => { if (_isRecording) StopRecording(false); StopMousePrefixRecording(); };
        MousePickPopup.Closed += (_, _) => StopMousePrefixRecording();
    }

    private void PopulatePresets()
    {
        CmbPreset.Items.Clear();

        // 按功能分类加载全部预设目录（截图 / 录屏 / 贴图 / OCR 与识图 / 翻译）
        foreach (var (category, items) in ActionsStore.CategorizedBuiltins)
        {
            var header = new ComboBoxItem
            {
                Content = category,
                IsEnabled = false,
                Focusable = false,
                IsHitTestVisible = false,
            };
            header.Classes.Add("groupHeader");
            CmbPreset.Items.Add(header);

            foreach (var (id, name, script) in items)
            {
                var actionItem = new ComboBoxItem
                {
                    Content = name,
                    Tag = id
                };
                actionItem.Classes.Add("presetAction");
                CmbPreset.Items.Add(actionItem);
            }
        }
    }

    private void LoadActionData()
    {
        _isUpdatingPresetUi = true;
        try
        {
            TxtName.Text = _originalAction.Name;
            TxtScript.Text = _originalAction.Script;
            UpdateCommandPreview();

            // 匹配预设下拉（跳过 groupHeader 分类标题项）
            int selectedIndex = 1;
            if (_originalAction.IsBuiltin && !string.IsNullOrEmpty(_originalAction.Id))
            {
                for (int i = 0; i < CmbPreset.Items.Count; i++)
                {
                    if (CmbPreset.Items[i] is ComboBoxItem item &&
                        !item.Classes.Contains("groupHeader") &&
                        string.Equals(item.Tag?.ToString(), _originalAction.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            CmbPreset.SelectedIndex = selectedIndex;
            UpdateHotKeyDisplay();

            RadPresetType.IsChecked = _isBuiltin;
            RadCustomType.IsChecked = !_isBuiltin;
            UpdateActionFields(_isBuiltin);
            ReadStepsFromScript();
        }
        finally
        {
            _isUpdatingPresetUi = false;
        }
    }

    /// <summary>脚本内容变化时刷新完整命令行预览。</summary>
    private void OnScriptTextChanged(object? sender, TextChangedEventArgs e)
    {
        _scriptInsertMenu?.Close();
        UpdateCommandPreview();
        if (!_isUpdatingPresetUi) RefreshMouseCapabilities();
    }

    /// <summary>@ 是菜单入口，不写进脚本；取消选择也不会留下无效字符。</summary>
    private void OnScriptTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text != "@" || TxtScript.IsReadOnly || !AdvancedScriptPanel.IsVisible || _isRecording) return;
        string text = TxtScript.Text ?? "";
        int start = Math.Clamp(Math.Min(TxtScript.SelectionStart, TxtScript.SelectionEnd), 0, text.Length);
        int end = Math.Clamp(Math.Max(TxtScript.SelectionStart, TxtScript.SelectionEnd), start, text.Length);
        if (start == end) start = end = Math.Clamp(TxtScript.CaretIndex, 0, text.Length);
        // 引号中的邮箱、提示词和 // 备注仍可正常输入 @。
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
        // 光标在已有调用内部时，新操作放到该行之前，避免将原调用切成两半。
        if (start == end && !string.IsNullOrWhiteSpace(text[lineStart..start])
            && !ScriptExecutor.TryParse(text[lineStart..start], out _, out _, validateOrder: false)) start = end = lineStart;
        e.Handled = true;
        _scriptInsertMenu?.Close();
        var originalContextMenu = TxtScript.ContextMenu;
        var menu = BuildScriptInsertMenu(text, start, end);
        _scriptInsertMenu = menu;
        TxtScript.ContextMenu = menu;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.VerticalOffset = 4;
        // 使用文本布局的真实光标矩形，换行、滚动和 DPI 均由输入框处理。
        var presenter = TxtScript.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter != null)
        {
            var caret = presenter.TextLayout.HitTestTextPosition(Math.Clamp(TxtScript.CaretIndex, 0, text.Length));
            if (presenter.TranslatePoint(caret.TopLeft, TxtScript) is { } point)
                menu.PlacementRect = new Rect(point, new Size(1, caret.Height));
        }
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_scriptInsertMenu, menu))
            {
                _scriptInsertMenu = null;
                TxtScript.ContextMenu = originalContextMenu;
            }
        };
        menu.Open(TxtScript);
    }

    private ContextMenu BuildScriptInsertMenu(string text, int start, int end)
    {
        var menu = new ContextMenu();
        var results = new List<ActionResultReference>();
        // 只给出光标之前确实存在的命名结果；不把回读旧指令时自动生成的名字当成真实结果。
        if (ScriptExecutor.TryParse(text[..start], out var earlier, out _))
        {
            var kinds = new Dictionary<string, ActionContentKind>(StringComparer.Ordinal);
            var currentKind = ActionContentKind.None;
            for (int i = 0; i < earlier.Count; i++)
            {
                var call = earlier[i];
                var inputKind = call.InputName != null && kinds.TryGetValue(call.InputName, out var kind) ? kind : currentKind;
                if (!call.ProducesResult) continue;
                currentKind = ScriptExecutor.ContentAfter(call, inputKind);
                if (call.ResultName == null) continue;
                kinds[call.ResultName] = currentKind;
                results.Add(new(call.ResultName, currentKind, i));
            }
        }
        // 即使后面的指令尚未写完，也要避开其中已经使用的结果名。
        var reserved = new List<ActionStepChoice>();
        var remaining = text.Remove(start, end - start);
        var existing = new List<ScriptCommand>();
        foreach (string line in remaining.ReplaceLineEndings("\n").Split('\n'))
            if (ScriptExecutor.TryParse(line, out var calls, out _, validateOrder: false)) existing.AddRange(calls);
        foreach (var call in existing)
            if (call.ResultName != null) reserved.Add(ActionStepCatalog.Default with { ResultName = call.ResultName });

        MenuItem InsertItem(string label, string script, string description)
        {
            var item = new MenuItem { Header = label };
            ToolTip.SetTip(item, description);
            item.Click += (_, _) => InsertScriptCommand(menu, text, start, end, script);
            return item;
        }

        bool hasIndependent = existing.Any(c => !c.UsesWorkflow && c.Method is not ("toggleallstickers" or "destroyallpin" or "restorelaststicker"));
        foreach (var operation in ActionStepCatalog.Operations)
        {
            var group = new MenuItem { Header = operation.Name };
            foreach (var option in operation.Options)
            {
                var command = ActionStepCatalog.Command(option);
                var step = ActionStepCatalog.Create(operation, option, reserved, 0);
                var compatible = ActionStepCatalog.InputsFor(option, results);
                MenuItem item;
                if (command.NeedsInput)
                {
                    item = new MenuItem { Header = option.Label, IsEnabled = compatible.Count > 0 && !hasIndependent };
                    foreach (var input in compatible)
                        item.Items.Add(InsertItem(input.Label, ActionStepCatalog.BuildScript([step with { InputName = input.Name }]), option.Description));
                    ToolTip.SetTip(item, hasIndependent ? "独立动作不能与内容模块混用。" : compatible.Count == 0
                        ? "请先在前面获取兼容内容，并填写结果名。" : option.Description);
                }
                else
                {
                    item = InsertItem(option.Label, ActionStepCatalog.BuildScript([step]), option.Description);
                    if (command.UsesWorkflow && hasIndependent)
                    {
                        item.IsEnabled = false;
                        ToolTip.SetTip(item, "独立动作不能与内容模块混用。");
                    }
                }
                group.Items.Add(item);
            }
            // 单一操作省去一层菜单，直接选择其输入结果。
            if (operation.Options.Length == 1)
            {
                var only = (MenuItem)group.Items[0]!;
                only.Header = operation.Name;
                group.Items.Clear();
                menu.Items.Add(only);
            }
            else menu.Items.Add(group);
        }
        menu.Items.Add(new Separator());
        var independent = new MenuItem { Header = "独立动作", IsEnabled = !existing.Any(c => c.UsesWorkflow) };
        ToolTip.SetTip(independent, "截图、录屏等预设指令；不能与内容模块混用。复用截图结果请使用上面的模块。");
        foreach (var (category, actions) in ActionsStore.CategorizedBuiltins)
        {
            var group = new MenuItem { Header = category };
            foreach (var (_, name, script) in actions) group.Items.Add(InsertItem(name, script, "插入独立预设指令"));
            independent.Items.Add(group);
        }
        menu.Items.Add(independent);
        return menu;
    }

    private void InsertScriptCommand(ContextMenu menu, string original, int start, int end, string command)
    {
        // 菜单打开后若脚本已被其他入口改动，不再按旧光标位置覆盖新内容。
        if ((TxtScript.Text ?? "") != original || TxtScript.IsReadOnly) { menu.Close(); return; }
        menu.Close();
        string before = original[..start], after = original[end..];
        string prefix = before.Length > 0 && before[^1] is not ('\r' or '\n') && !string.IsNullOrWhiteSpace(before[(before.LastIndexOf('\n') + 1)..])
            ? Environment.NewLine : "";
        string suffix = after.Length > 0 && after[0] is not ('\r' or '\n') ? Environment.NewLine : "";
        TxtScript.Focus();
        TxtScript.SelectionStart = start;
        TxtScript.SelectionEnd = end;
        // 走 TextBox 编辑通路，保留撤销／重做历史。
        TxtScript.SelectedText = prefix + command + suffix;
        int caret = start + prefix.Length + command.Length + suffix.Length;
        TxtScript.CaretIndex = TxtScript.SelectionStart = TxtScript.SelectionEnd = caret;
    }

    private void UpdateActionFields(bool preset)
    {
        PresetField.IsVisible = preset;
        NameField.IsVisible = !preset;
        StepModeRow.IsVisible = !preset;
        StepEditorPanel.IsVisible = !preset && RadStepMode.IsChecked == true;
        AdvancedScriptPanel.IsVisible = preset || RadScriptMode.IsChecked == true;
        TxtName.IsReadOnly = TxtScript.IsReadOnly = preset;
        TxtScript.MinHeight = preset ? 60 : 120;
        TxtName.Classes.Set("locked", preset);
        TxtScript.Classes.Set("locked", preset);
        ScriptHelpBorder.IsVisible = CommandPreviewBorder.IsVisible = !preset;
        TxtScriptInsertHint.IsVisible = !preset;
        ScriptHelpContent.IsVisible = false;
        ScriptHelpBorder.Content = "格式说明";
    }

    private void OnActionTypeChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingPresetUi || sender is not RadioButton) return;
        bool presetSelected = ReferenceEquals(sender, RadPresetType);
        RadPresetType.IsChecked = presetSelected;
        RadCustomType.IsChecked = !presetSelected;
        if (presetSelected == _isBuiltin) return;
        if (ReferenceEquals(sender, RadPresetType))
        {
            _customAdvanced = RadScriptMode.IsChecked == true;
            _customName = TxtName.Text ?? "我的动作";
            _customScript = TxtScript.Text ?? "";
            OnPresetSelectionChanged(null, null);
        }
        else
        {
            _isBuiltin = false;
            TxtName.Text = _customName;
            TxtScript.Text = _customScript;
            if (_customAdvanced) { RadScriptMode.IsChecked = true; RadStepMode.IsChecked = false; }
            else ReadStepsFromScript();
            UpdateActionFields(false);
        }
    }

    private void ReadStepsFromScript()
    {
        _expandedStep = -1;
        _updatingSteps = true;
        bool readable;
        if (string.IsNullOrWhiteSpace(TxtScript.Text)) { _steps = []; readable = true; }
        else readable = ActionStepCatalog.TryRead(TxtScript.Text, out _steps);
        RadStepMode.IsChecked = readable;
        RadScriptMode.IsChecked = !readable;
        _updatingSteps = false;
        if (!_isBuiltin && readable && _steps.Count > 0) TxtScript.Text = ActionStepCatalog.BuildScript(_steps);
        RenderSteps();
        if (!_isBuiltin) UpdateActionFields(false);
    }

    private void OnStepModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingPresetUi || _updatingSteps || sender is not RadioButton) return;
        bool wantSteps = ReferenceEquals(sender, RadStepMode);
        if (StepEditorPanel.IsVisible == wantSteps) return;
        _updatingSteps = true;
        RadStepMode.IsChecked = wantSteps;
        RadScriptMode.IsChecked = !wantSteps;
        _updatingSteps = false;
        if (wantSteps && string.IsNullOrWhiteSpace(TxtScript.Text))
        {
            _steps = [];
            SetStepError("");
            RenderSteps();
            UpdateActionFields(_isBuiltin);
            return;
        }
        if (wantSteps && !ActionStepCatalog.TryRead(TxtScript.Text ?? "", out _steps))
        {
            _updatingSteps = true;
            RadScriptMode.IsChecked = true;
            RadStepMode.IsChecked = false;
            _updatingSteps = false;
            SetStepError("这段指令暂不能转换为步骤，原指令已保留。请继续使用高级指令编辑。");
        }
        else
        {
            SetStepError("");
            if (wantSteps)
            {
                _expandedStep = -1;
                TxtScript.Text = ActionStepCatalog.BuildScript(_steps);
                RenderSteps();
            }
        }
        UpdateActionFields(_isBuiltin);
    }

    private void SetStepError(string error)
    {
        TxtStepError.Text = error;
        TxtStepError.IsVisible = !string.IsNullOrEmpty(error);
    }

    private void RenderSteps(bool preserveRows = false)
    {
        if (StepListPanel == null) return;
        _updatingSteps = true;
        if (!preserveRows) StepListPanel.Children.Clear();
        _inputPickers.Clear();
        _moduleHeaders.Clear();
        for (int index = 0; index < _steps.Count; index++)
        {
            int position = index;
            var step = _steps[index];
            var rowContent = new StackPanel();
            var row = preserveRows ? (ContinuousBorder)StepListPanel.Children[index] : new ContinuousBorder { Classes = { "actionModuleRow" } };
            row.Child = rowContent;
            rowContent.Children.Add(BuildModuleHeader(position, row));
            row.Classes.Set("expanded", _expandedStep == position);
            if (!preserveRows) StepListPanel.Children.Add(row);
            if (_expandedStep != position) continue;
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 4, 8, 10) };
            rowContent.Children.Add(panel);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var operationLabel = new TextBlock { Text = "模块", Classes = { "fieldLabel" }, FontSize = 13.5, FontWeight = FontWeight.Normal, Margin = new Thickness(0, 0, 8, 0) };
            head.Children.Add(operationLabel);
            var operationPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 400 };
            var availableResults = ActionStepCatalog.ResultsBefore(_steps, position);
            var operations = ActionStepCatalog.Operations.AsEnumerable();
            if (!operations.Contains(step.Operation)) operations = operations.Append(step.Operation);
            foreach (var operation in operations)
                foreach (var option in operation.Options)
                    operationPicker.Items.Add(new ComboBoxItem
                    {
                        Content = operation.Name + " · " + option.Label,
                        Tag = new ActionStepChoice(operation, option),
                        IsEnabled = ActionStepCatalog.OptionAvailable(option, availableResults),
                    });
            operationPicker.SelectedItem = operationPicker.Items.OfType<ComboBoxItem>().First(item =>
                item.Tag is ActionStepChoice choice && choice.Operation == step.Operation && choice.Option == step.Option);
            operationPicker.SelectionChanged += (_, _) =>
            {
                if (_updatingSteps || operationPicker.SelectedItem is not ComboBoxItem { Tag: ActionStepChoice choice, IsEnabled: true }) return;
                ChangeStepOption(position, choice.Operation, choice.Option);
                CommitSteps();
            };
            Grid.SetColumn(operationPicker, 1); head.Children.Add(operationPicker);
            AddModuleParameter(panel, head);
            var call = ActionStepCatalog.Command(step.Option);
            if (call.NeedsInput)
            {
                var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var inputLabel = new TextBlock { Text = "输入", Classes = { "fieldLabel" }, FontSize = 13.5, FontWeight = FontWeight.Normal, Margin = new Thickness(0, 0, 8, 0) };
                inputRow.Children.Add(inputLabel);
                var inputs = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择此前模块的结果" };
                PopulateInputPicker(inputs, position);
                inputs.SelectionChanged += (_, _) =>
                {
                    if (_updatingSteps || inputs.SelectedItem is not ComboBoxItem { Tag: string name }) return;
                    _steps[position] = _steps[position] with { InputName = name };
                    CommitSteps();
                };
                _inputPickers.Add((inputs, position));
                Grid.SetColumn(inputs, 1); inputRow.Children.Add(inputs); AddModuleParameter(panel, inputRow);
            }
            if (call.ProducesResult)
            {
                var resultRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var resultLabel = new TextBlock { Text = "结果名", Classes = { "fieldLabel" }, FontSize = 13.5, FontWeight = FontWeight.Normal, Margin = new Thickness(0, 0, 8, 0) };
                resultRow.Children.Add(resultLabel);
                var resultName = new TextBox { Text = step.ResultName, Watermark = "结果名称，供后面的模块引用" };
                string? referenceName = step.ResultName;
                resultName.TextChanged += (_, _) =>
                {
                    if (_updatingSteps) return;
                    string name = resultName.Text ?? "";
                    _steps[position] = _steps[position] with { ResultName = name };
                    // 合法且唯一的改名同步所有引用；重复名不会偷偷更改其他模块的输入。
                    if (referenceName != null && ScriptExecutor.IsResultName(name) && !_steps.Where((_, i) => i != position).Any(s => s.ResultName == name))
                    {
                        for (int i = position + 1; i < _steps.Count; i++)
                            if (_steps[i].InputName == referenceName) _steps[i] = _steps[i] with { InputName = name };
                        referenceName = name;
                    }
                    _updatingSteps = true;
                    foreach (var (picker, sourcePosition) in _inputPickers) PopulateInputPicker(picker, sourcePosition);
                    _updatingSteps = false;
                    UpdateStepScript();
                };
                Grid.SetColumn(resultName, 1); resultRow.Children.Add(resultName); AddModuleParameter(panel, resultRow);
            }
            var description = new TextBlock { Text = step.Option.Description, Classes = { "fieldHint" }, LineHeight = double.NaN };
            AddModuleParameter(panel, description);
        }
        if (_steps.Count == 0) StepListPanel.Children.Add(new TextBlock { Text = "还没有模块，点击「添加模块」开始。", Classes = { "fieldHint" }, LineHeight = double.NaN });
        UpdateAddStepMenu();
        _updatingSteps = false;
    }

    /// <summary>所有字段共用一张 Auto 列表格，首次布局即对齐；说明跨两列。</summary>
    private static void AddModuleParameter(Grid panel, Control field)
    {
        int row = panel.RowDefinitions.Count;
        panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (field is Grid fields)
        {
            var children = fields.Children.ToArray();
            fields.Children.Clear();
            foreach (var child in children)
            {
                if (row > 0) child.Margin = new Thickness(child.Margin.Left, child.Margin.Top + 8, child.Margin.Right, child.Margin.Bottom);
                Grid.SetRow(child, row);
                panel.Children.Add(child);
            }
        }
        else
        {
            if (row > 0) field.Margin = new Thickness(field.Margin.Left, field.Margin.Top + 8, field.Margin.Right, field.Margin.Bottom);
            Grid.SetRow(field, row);
            Grid.SetColumnSpan(field, 2);
            panel.Children.Add(field);
        }
    }

    private Control BuildModuleHeader(int position, ContinuousBorder row)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var icon = CreateModuleIcon(_steps[position]);
        icon.Margin = new Thickness(0, 0, 8, 0);
        content.Children.Add(icon);
        var title = new TextBlock { Text = ModuleTitle(_steps[position]), Classes = { "fieldLabel" }, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(title, 1); content.Children.Add(title);
        var summary = new TextBlock { Text = ModuleSummary(_steps[position]), Classes = { "fieldHint" }, LineHeight = double.NaN,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(summary, 2); content.Children.Add(summary);
        _moduleHeaders.Add((title, summary, position));
        var handle = new Border { Child = content, Background = Brushes.Transparent, Padding = new Thickness(8, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Hand) };
        ToolTip.SetTip(handle, ModuleSummary(_steps[position]) + "\n" + _steps[position].Option.Description);
        handle.PointerPressed += (_, e) =>
        {
            if (_moduleDrag.Begin(handle, e, StepListPanel)) _draggedModuleRow = row;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!_moduleDrag.Update(e)) return;
            if (_moduleDragSizeToContent == null)
            {
                _moduleDragSizeToContent = SizeToContent;
                _moduleDragWindowHeight = Height;
                // 动态让位只改变列表布局，不让 SizeToContent 窗口在拖动期间增高跳动。
                SizeToContent = SizeToContent.Manual;
                Height = Bounds.Height;
            }
            row.Classes.Add("dragging");
            if (_draggedModuleParameters == null && row.Child is StackPanel { Children.Count: > 1 } rowContent)
            {
                _draggedModuleParameters = rowContent.Children[1];
                _draggedModuleParameters.IsVisible = false;
            }
            UpdateModuleInsertion(e.GetPosition(StepListPanel));
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!_moduleDrag.IsActive) return;
            bool dragging = _moduleDrag.IsDragging;
            if (dragging) UpdateModuleInsertion(e.GetPosition(StepListPanel));
            int insertion = _moduleInsertIndex;
            // 捕获先释放，随后才改树；CaptureLost 不得清掉待提交的落位信息。
            _moduleDrag.Reset();
            ClearModuleDragVisuals();
            e.Handled = true;
            if (!dragging) ToggleModule(position);
            else if (insertion >= 0) MoveModule(position, insertion);
        };
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 30, Margin = new Thickness(0, 0, 4, 0) };
        actions.Children.Add(handle);
        var arrow = OcrIconGeometry.CreateIcon(OcrIconGeometry.CaretDown, "actionModuleStroke");
        // 扁箭头先按真实宽高比居中，旋转方形槽位；不旋转含上方空白的 Path 盒。
        arrow.Height = 14 * arrow.Data!.Bounds.Height / arrow.Data.Bounds.Width;
        var arrowSlot = new Grid { Width = 14, Height = 14, RenderTransform = new RotateTransform(_expandedStep == position ? 0 : -90),
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
        arrowSlot.Children.Add(arrow);
        var toggle = new Button { Classes = { "sctIconBtn", "actionModuleButton" }, Content = arrowSlot, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(toggle, _expandedStep == position ? "收起参数" : "展开参数");
        toggle.Click += (_, _) => ToggleModule(position);
        var menu = new ContextMenu();
        var editItem = new MenuItem { Header = _expandedStep == position ? "收起参数" : "展开参数" };
        editItem.Click += (_, _) => ToggleModule(position);
        menu.Items.Add(editItem);
        foreach (var (label, offset) in new[] { ("上移", -1), ("下移", 1) })
        {
            int move = offset;
            var item = new MenuItem { Header = label, IsEnabled = position + move >= 0 && position + move < _steps.Count };
            item.Click += (_, _) =>
            {
                MoveModule(position, move < 0 ? position - 1 : position + 2);
            };
            menu.Items.Add(item);
        }
        actions.ContextMenu = menu;
        Grid.SetColumn(toggle, 1); actions.Children.Add(toggle);
        var delete = new Button { Classes = { "sctIconBtn", "actionModuleButton" }, Content = OcrIconGeometry.CreateIcon(OcrIconGeometry.TrashStroke, "actionModuleStroke"), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(delete, "删除模块");
        delete.Click += (_, _) =>
        {
            _steps.RemoveAt(position);
            if (_expandedStep == position) _expandedStep = -1;
            else if (_expandedStep > position) _expandedStep--;
            CommitSteps();
        };
        Grid.SetColumn(delete, 2); actions.Children.Add(delete);
        return actions;
    }

    private void ToggleModule(int position)
    {
        _expandedStep = _expandedStep == position ? -1 : position;
        RenderSteps();
    }

    private void UpdateModuleInsertion(Point point)
    {
        _moduleInsertIndex = -1;
        if (StepListPanel.TranslatePoint(point, MouseFormScroll) is not Point visiblePoint
            || !new Rect(MouseFormScroll.Bounds.Size).Contains(visiblePoint)
            || point.X < 0 || point.X > StepListPanel.Bounds.Width || point.Y < -6 || point.Y > StepListPanel.Bounds.Height + 6)
        {
            ListReorderBehavior.RemovePlaceholder(_moduleDropPlaceholder);
            return;
        }
        _moduleDropPlaceholder ??= new ContinuousBorder
            { Classes = { "actionModuleDropPlaceholder" }, Height = 32 };
        _moduleInsertIndex = ListReorderBehavior.GetInsertionIndex(StepListPanel, point, Orientation.Vertical, _moduleDropPlaceholder);
        ListReorderBehavior.MovePlaceholder(StepListPanel, _moduleDropPlaceholder, _moduleInsertIndex);
    }

    private void ClearModuleDragVisuals()
    {
        _draggedModuleRow?.Classes.Remove("dragging");
        if (_draggedModuleParameters != null) _draggedModuleParameters.IsVisible = true;
        _draggedModuleParameters = null;
        _draggedModuleRow = null;
        _moduleInsertIndex = -1;
        ListReorderBehavior.RemovePlaceholder(_moduleDropPlaceholder);
        _moduleDropPlaceholder = null;
        if (_moduleDragSizeToContent is { } originalSizeToContent)
        {
            Height = _moduleDragWindowHeight;
            SizeToContent = originalSizeToContent;
            _moduleDragSizeToContent = null;
        }
    }

    private void MoveModule(int from, int insertion)
    {
        int destination = insertion > from ? insertion - 1 : insertion;
        if (destination == from || destination < 0 || destination >= _steps.Count) return;
        var step = _steps[from];
        var expanded = _expandedStep >= 0 ? _steps[_expandedStep] : null;
        _steps.RemoveAt(from);
        _steps.Insert(destination, step);
        _expandedStep = expanded == null ? -1 : _steps.FindIndex(item => ReferenceEquals(item, expanded));
        var row = StepListPanel.Children[from];
        StepListPanel.Children.RemoveAt(from);
        StepListPanel.Children.Insert(destination, row);
        // 保留各模块的行容器；只刷新参数引用与交互索引，不整表清空。
        RenderSteps(preserveRows: true);
        UpdateStepScript();
    }

    private static string ModuleTitle(ActionStepChoice step) => step.Operation.Name;

    private static string ModuleSummary(ActionStepChoice step) => step.Option.Label
        + (step.InputName != null ? " · 输入 " + step.InputName : "")
        + (step.ResultName != null ? " → " + step.ResultName : "");

    private static Avalonia.Controls.Shapes.Path CreateModuleIcon(ActionStepChoice step)
    {
        string? toolId = step.Operation.Name switch
        {
            "识别文字" => "ocr",
            "翻译" => "translate",
            "抠图" => "matting",
            "AI 识图" => "aivision",
            "贴图管理" or "贴图" => "sticker",
            "输出结果" => ActionStepCatalog.Command(step.Option).Method switch
            {
                "pinresult" => "sticker",
                "saveresult" => "saveas",
                "showresult" => "ocr",
                _ => null,
            },
            _ => null,
        };
        // 直接引用截图工具栏注册表，沿用填充/描边与尺寸比例，禁止抄路径造变体。
        if (toolId != null && ToolbarRegistry.Buttons.FirstOrDefault(tool => tool.Id == toolId) is { } tool)
        {
            var icon = OcrIconGeometry.CreateIcon(tool.IconPath, tool.IsFill ? "sctIcon" : "actionModuleStroke");
            icon.Width = icon.Height = 14 * tool.IconScale;
            return icon;
        }
        return step.Operation.Name == "输出结果"
            ? OcrIconGeometry.CreateIcon(OcrIconGeometry.CopyOutline, "actionModuleStroke")
            : OcrIconGeometry.CreateIcon(OcrIconGeometry.Image);
    }

    private void CommitSteps()
    {
        RenderSteps();
        UpdateStepScript();
    }

    private void UpdateStepScript()
    {
        // 改名期间只更新一行摘要，不重建输入框，以免打断输入焦点。
        foreach (var (title, summary, position) in _moduleHeaders)
        {
            title.Text = ModuleTitle(_steps[position]);
            summary.Text = ModuleSummary(_steps[position]);
            ToolTip.SetTip(summary, summary.Text);
        }
        TxtScript.Text = ActionStepCatalog.BuildScript(_steps);
        ScriptExecutor.TryParse(TxtScript.Text ?? "", out _, out string error);
        SetStepError(error);
    }

    private void ChangeStepOption(int position, ActionStepOperation operation, ActionStepOption option)
    {
        var previous = _steps[position];
        var replacement = ActionStepCatalog.Create(operation, option, _steps, position, previous.InputName);
        if (previous.ResultName != null && replacement.ResultName != null) replacement = replacement with { ResultName = previous.ResultName };
        _steps[position] = replacement;
    }

    private void PopulateInputPicker(ComboBox picker, int position)
    {
        picker.Items.Clear();
        var step = _steps[position];
        foreach (var result in ActionStepCatalog.InputsFor(step.Option, ActionStepCatalog.ResultsBefore(_steps, position)))
            picker.Items.Add(new ComboBoxItem { Content = result.Label, Tag = result.Name });
        var selected = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, step.InputName));
        if (selected == null && step.InputName != null)
        {
            selected = new ComboBoxItem { Content = step.InputName + " · 引用不可用", Tag = step.InputName, IsEnabled = false };
            picker.Items.Add(selected);
        }
        picker.SelectedItem = selected;
    }

    private void UpdateAddStepMenu()
    {
        int position = _steps.Count;
        var results = ActionStepCatalog.ResultsBefore(_steps, position);
        var menu = new MenuFlyout();
        foreach (var operation in ActionStepCatalog.Operations)
        {
            var option = operation.Options.FirstOrDefault(option => ActionStepCatalog.OptionAvailable(option, results));
            if (option == null) continue;
            var item = new MenuItem { Header = operation.Name };
            item.Click += (_, _) => { _steps.Add(ActionStepCatalog.Create(operation, option, _steps, position)); _expandedStep = position; CommitSteps(); };
            menu.Items.Add(item);
        }
        BtnAddStep.Flyout = menu;
    }

    private void OnUseExampleClick(object? sender, RoutedEventArgs e)
    {
        string? key = (sender as MenuItem)?.Tag?.ToString();
        string script = key switch
        {
            "hide-shot" => "yuSnip.toggleAllStickers();\nyuSnip.captureImage('原图');\nyuSnip.copyResult('原图');",
            "translate" => "yuSnip.captureImage('原图');\nyuSnip.translateResult('Text', '原图', '译文');\nyuSnip.copyResult('译文');",
            "ocr-translate" => "yuSnip.captureImage('原图');\nyuSnip.recognizeResult('原图', '识别文字');\nyuSnip.translateResult('Text', '识别文字', '译文');\nyuSnip.copyResult('译文');",
            "matting" => "yuSnip.captureImage('原图');\nyuSnip.mattingResult('Default', '原图', '抠图结果');\nyuSnip.pinResult('抠图结果');",
            "branch" => "yuSnip.captureImage('原图');\nyuSnip.translateResult('Text', '原图', '译文');\nyuSnip.recognizeResult('原图', '识别文字');\nyuSnip.copyResult('原图');\nyuSnip.copyResult('译文');\nyuSnip.showResult('识别文字');",
            _ => "yuSnip.captureImage('原图');\nyuSnip.copyResult('原图');\nyuSnip.pinResult('原图');"
        };
        TxtName.Text = key switch
        {
            "hide-shot" => "切换贴图显示后截图",
            "translate" => "截图翻译并复制译文",
            "ocr-translate" => "识别后翻译并复制",
            "matting" => "截图抠图并贴图",
            "branch" => "截图、翻译与 OCR 分别输出",
            _ => "截图复制并贴图"
        };
        ActionStepCatalog.TryRead(script, out _steps);
        _expandedStep = -1;
        CommitSteps();
    }

    private void OnScriptHelpClick(object? sender, RoutedEventArgs e)
    {
        ScriptHelpContent.IsVisible = !ScriptHelpContent.IsVisible;
        ScriptHelpBorder.Content = ScriptHelpContent.IsVisible ? "收起说明" : "格式说明";
    }

    /// <summary>按脚本能力决定鼠标入口与触发方式选项；只有一种触发可用时不摆出触发方式那组（键帽文案已带该字段）。</summary>
    private void RefreshMouseCapabilities(ActionMouseTrigger? preferred = null)
    {
        if (RadMouseDrag == null || RadMouseClick == null || TxtMouseTip == null) return;
        string script = TxtScript.Text ?? "";
        bool click = ActionMouseBinding.SupportsClick(script);
        bool drag = ActionMouseBinding.SupportsDrag(script);
        preferred ??= RadMouseDrag.IsChecked == true ? ActionMouseTrigger.Drag
            : RadMouseClick.IsChecked == true ? ActionMouseTrigger.Click
            : drag ? ActionMouseTrigger.Drag : ActionMouseTrigger.Click;
        _updatingMouseUi = true;
        RadMouseDrag.IsEnabled = drag;
        RadMouseDrag.IsVisible = drag;
        RadMouseClick.IsEnabled = click;
        RadMouseClick.IsVisible = click;
        RadMouseDrag.IsChecked = drag && (preferred == ActionMouseTrigger.Drag || !click);
        RadMouseClick.IsChecked = click && RadMouseDrag.IsChecked != true;
        TriggerRow.IsVisible = click && drag;
        _updatingMouseUi = false;
        BtnMouseBinding.IsEnabled = click || drag;
        SetMouseTip(click || drag ? "" : "此脚本不支持鼠标");
        UpdateMouseDisplay();
    }

    /// <summary>把当前浮层控件状态读成动作对象；未勾选辅助键即未绑定（判据只有 Core 的 HasMouseBinding 一份）。</summary>
    private ActionDefinition ReadMouseBinding()
    {
        ActionMouseButton button = ActionMouseButton.Left;
        foreach (var control in MouseButtonOptions.Children)
            if (control is RadioButton { IsChecked: true } radio && int.TryParse(radio.Tag?.ToString(), out int value))
                button = (ActionMouseButton)value;
        uint modifiers = (ChkMouseShift.IsChecked == true ? 4u : 0) | (ChkMouseCtrl.IsChecked == true ? 2u : 0)
            | (ChkMouseAlt.IsChecked == true ? 1u : 0) | (ChkMouseWin.IsChecked == true ? 8u : 0);
        return new ActionDefinition
        {
            MouseModifiers = modifiers,
            MouseButton = modifiers == 0 && !_mousePrefixKey.IsValid ? ActionMouseButton.None : button,
            MouseTrigger = RadMouseDrag.IsChecked == true ? ActionMouseTrigger.Drag : ActionMouseTrigger.Click,
            MousePrefixKey = _mousePrefixKey,
        };
    }

    /// <summary>键帽文案现算自 Core 的统一格式化出口，与设置列表那枚鼠标胶囊同一份文本。</summary>
    private void UpdateMouseDisplay()
    {
        if (BtnMouseBinding == null || BtnClearMouse == null) return;
        ActionDefinition current = ReadMouseBinding();
        BtnMouseBinding.Content = current.HasMouseBinding ? ActionMouseBinding.Format(current) : "未绑定 · 点击设置";
        BtnClearMouse.IsEnabled = current.HasMouseBinding;
        if (BtnMousePrefix != null && _prefixRecorder == null)
            BtnMousePrefix.Content = _mousePrefixKey.IsValid ? _mousePrefixKey.ToString() : "点击录制任意键";
        if (BtnClearMousePrefix != null) BtnClearMousePrefix.IsEnabled = _mousePrefixKey.IsValid;
        string conflict = MouseBindingConflict(current);
        if (conflict.Length != 0) SetMouseTip(conflict);
        else if (ActionMouseBinding.SupportsClick(TxtScript.Text ?? "")) SetMouseTip("");
    }

    private void SetMouseTip(string text)
    {
        TxtMouseTip.Text = text;
        TxtMouseTip.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>点键帽展开浮层；浮层开着时对键帽的点击被遮罩吃掉，不需要自己再判一次开合。</summary>
    private void OnMouseBindingClick(object? sender, RoutedEventArgs e)
    {
        if (BtnMouseBinding.IsEnabled) MousePickPopup.IsOpen = true;
    }

    private void OnClearMouseClick(object? sender, RoutedEventArgs e)
    {
        StopMousePrefixRecording();
        _mousePrefixKey = default;
        MousePickPopup.IsOpen = false;
        _updatingMouseUi = true;
        ChkMouseShift.IsChecked = ChkMouseCtrl.IsChecked = ChkMouseAlt.IsChecked = ChkMouseWin.IsChecked = false;
        foreach (var control in MouseButtonOptions.Children)
            if (control is RadioButton radio) radio.IsChecked = radio.Tag?.ToString() == "1";
        _updatingMouseUi = false;
        RefreshMouseCapabilities();
    }

    private void OnMouseModifierChanged(object? sender, RoutedEventArgs e)
    {
        if (!_updatingMouseUi) UpdateMouseDisplay();
    }

    private void OnRecordMousePrefixClick(object? sender, RoutedEventArgs e)
    {
        if (_prefixRecorder != null) { StopMousePrefixRecording(); return; }
        if (_isRecording) StopRecording(false);
        if (Application.Current is App app && !app.GlobalHotkeysSuspended)
        { app.SuspendGlobalHotkeys(); _ownsHotkeySuspension = true; }
        int generation = ++_prefixRecordingGeneration;
        try
        {
            nint handle = this.TryGetPlatformHandle()?.Handle ?? 0;
            if (handle == 0) throw new InvalidOperationException("录制窗口尚未就绪");
            _prefixRecorder = new Services.MousePrefixRecorder(handle, key => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (generation != _prefixRecordingGeneration) return;
                _prefixRecorder = null; RestoreEditorHotkeys();
                if (key.IsValid) AcceptMousePrefixKey(key);
                else { UpdateMouseDisplay(); SetMousePrefixTip("录制已取消，原绑定保留。"); }
            }));
            BtnMousePrefix.Content = "取消录制";
            SetMousePrefixTip("按下并松开一个键；Esc 也可录制，点击上方按钮取消。");
        }
        catch (Exception ex)
        { RestoreEditorHotkeys(); SetMousePrefixTip("无法开始录制：" + ex.Message); }
    }

    private void AcceptMousePrefixKey(ActionMouseKey key)
    {
        if (!key.IsValid) return;
        _mousePrefixKey = key;
        _updatingMouseUi = true;
        if (key.ModifierMask == 4) ChkMouseShift.IsChecked = false;
        if (key.ModifierMask == 2) ChkMouseCtrl.IsChecked = false;
        if (key.ModifierMask == 1) ChkMouseAlt.IsChecked = false;
        if (key.ModifierMask == 8) ChkMouseWin.IsChecked = false;
        _updatingMouseUi = false;
        UpdateMouseDisplay();
        string conflict = MouseBindingConflict(ReadMouseBinding());
        SetMousePrefixTip(conflict.Length == 0 ? "已录制 " + key + "。" : conflict);
    }

    private string MouseBindingConflict(ActionDefinition mouse)
    {
        if (!mouse.HasMouseBinding) return "";
        if (_otherActions != null)
            foreach (var other in _otherActions)
            {
                if (ReferenceEquals(other, _originalAction)) continue;
                if (ActionMouseBinding.Conflicts(other, mouse)) return "此组合已绑定「" + other.Name + "」";
                if (mouse.MousePrefixKey.IsValid && other.HasHotKey && other.HotKeyVirtualKey == mouse.MousePrefixKey.VirtualKey
                    && other.HotKeyModifiers == ActionMouseBinding.EffectiveModifiers(mouse)) return "引导键与「" + other.Name + "」的键盘快捷键冲突";
            }
        if (mouse.MousePrefixKey.IsValid && _virtualKey == mouse.MousePrefixKey.VirtualKey
            && _modifiers == ActionMouseBinding.EffectiveModifiers(mouse)) return "引导键与当前动作的键盘快捷键冲突";
        return "";
    }

    private void SetMousePrefixTip(string text)
    { TxtMousePrefixTip.Text = text; TxtMousePrefixTip.IsVisible = text.Length != 0; }

    private void StopMousePrefixRecording()
    {
        if (_prefixRecorder == null) return;
        ++_prefixRecordingGeneration;
        var recorder = _prefixRecorder; _prefixRecorder = null; recorder.Dispose();
        RestoreEditorHotkeys(); UpdateMouseDisplay(); SetMousePrefixTip("录制已取消，原绑定保留。");
    }

    private void OnClearMousePrefixClick(object? sender, RoutedEventArgs e)
    { StopMousePrefixRecording(); _mousePrefixKey = default; SetMousePrefixTip(""); UpdateMouseDisplay(); }

    /// <summary>生成当前脚本对应的完整命令行（内嵌双引号转单引号避免破坏命令行引号结构）。</summary>
    private string BuildCommandString()
    {
        string script = TxtScript.Text?.Trim() ?? "";
        if (script.Length == 0) return "yuSnip.exe -r \"\"";
        return "yuSnip.exe -r \"" + script.Replace("\"", "'") + "\"";
    }

    /// <summary>刷新命令行预览文本（单行展示，超长省略）。</summary>
    private void UpdateCommandPreview()
    {
        if (TxtCommandPreview == null) return;
        string cmd = BuildCommandString().Replace("\r", " ").Replace("\n", " ");
        TxtCommandPreview.Text = cmd;
    }

    /// <summary>点击命令行预览：复制完整命令到剪贴板。</summary>
    private async void OnCommandPreviewClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        try
        {
            string cmd = BuildCommandString();
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(cmd);
                TxtCommandPreview.Text = "已复制：" + cmd;
                await Task.Delay(1200);
                UpdateCommandPreview();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ActionEditorWindow.OnCommandPreviewClick 复制失败: " + ex.Message);
        }
    }

    private void OnPresetSelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (_isUpdatingPresetUi) return;
        if (CmbPreset.SelectedItem is not ComboBoxItem item) return;
        if (item.Classes.Contains("groupHeader") || !item.IsEnabled) return;

        string? presetId = item.Tag?.ToString();
        if (string.IsNullOrEmpty(presetId)) return;

        var preset = ActionsStore.FindPresetById(presetId);
        if (preset == null) return;

        var (id, name, script) = preset.Value;
        TxtName.Text = name;
        TxtScript.Text = script;
        _actionId = id;
        _isBuiltin = true;
        UpdateActionFields(true);
        SetStepError("");
        // 如果尚未录制快捷键，且该预设存在推荐热键，则自动带出
        if (_modifiers == 0 && _virtualKey == 0)
        {
            if (ActionsStore.TryGetBuiltinDefaultHotKey(id, out uint defMods, out uint defVk))
            {
                _modifiers = defMods;
                _virtualKey = defVk;
                UpdateHotKeyDisplay();
                SetHotKeyTip("已填入推荐快捷键。");
            }
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnRecordHotKeyClick(object? sender, RoutedEventArgs e)
    {
        StopMousePrefixRecording();
        if (_isRecording)
        {
            StopRecording(false);
            return;
        }

        _isRecording = true;
        if (Application.Current is App app && !app.GlobalHotkeysSuspended)
        {
            app.SuspendGlobalHotkeys();
            _ownsHotkeySuspension = true;
        }
        TxtHotKey.Text = "请按下快捷键…";
        SetHotKeyTip("请按下快捷键（Esc 取消）。");

        HotKeyDisplayBorder.Classes.Add("recording");

        Focus();
        int generation = ++_keyboardRecordingGeneration;
        try
        {
            nint handle = this.TryGetPlatformHandle()?.Handle ?? 0;
            if (handle == 0) throw new InvalidOperationException("录制窗口尚未就绪");
            _keyboardRecorder = new Services.KeyboardShortcutRecorder(handle,
                (modifiers, key) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _keyboardRecordingGeneration) return;
                    _keyboardRecorder = null;
                    if (key == 0) StopRecording(false);
                    else AcceptKeyboardShortcut(modifiers, key);
                }),
                () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _keyboardRecordingGeneration)
                        SetHotKeyTip("全局热键需包含辅助键或使用 F1–F12 功能键。");
                }));
        }
        catch (Exception ex) { StopRecording(false); SetHotKeyTip("无法开始录制：" + ex.Message); }
    }

    private void AcceptKeyboardShortcut(uint modifiers, uint key)
    {
        if (!ActionKeyboardBinding.IsValid(modifiers, key)) return;
        string? conflict = Services.BuiltinShortcutsStore.FindActionConflict(modifiers, key,
            _otherActions ?? Array.Empty<ActionDefinition>(), _originalAction, _otherShortcuts);
        if (conflict != null)
        {
            StopRecording(false);
            SetHotKeyTip(FormatHotKey(modifiers, key) + " 已被" + conflict + "占用，请重新录制。", true);
            return;
        }
        _modifiers = modifiers; _virtualKey = key;
        StopRecording(true);
        SetHotKeyTip("快捷键录制成功。");
    }

    private void StopRecording(bool keepNew)
    {
        _isRecording = false;
        ++_keyboardRecordingGeneration;
        var recorder = _keyboardRecorder; _keyboardRecorder = null; recorder?.Dispose();
        RestoreEditorHotkeys();
        UpdateHotKeyDisplay();

        HotKeyDisplayBorder.Classes.Remove("recording");

        if (!keepNew)
        {
            UpdateHotKeyDisplay();
            SetHotKeyTip(string.Empty);
        }
    }

    private void RestoreEditorHotkeys()
    {
        if (!_ownsHotkeySuspension) return;
        _ownsHotkeySuspension = false;
        if (Application.Current is App app) app.ResumeGlobalHotkeys();
    }

    private void OnClearHotKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_isRecording) StopRecording(false);
        _modifiers = 0;
        _virtualKey = 0;
        UpdateHotKeyDisplay();
        SetHotKeyTip("已清除快捷键绑定。");
    }

    private void UpdateHotKeyDisplay()
    {
        TxtHotKey.Text = (_virtualKey != 0) ? FormatHotKey(_modifiers, _virtualKey) : "未绑定 · 点击录制";
    }

    private void SetHotKeyTip(string text, bool conflict = false)
    {
        TxtHotKeyTip.Text = text;
        TxtHotKeyTip.IsVisible = !string.IsNullOrEmpty(text);
        TxtHotKeyTip.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(conflict ? "SctDangerBrush" : "SctTextMutedBrush"));
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        string script = (TxtScript.Text ?? "").Trim();
        if (string.IsNullOrEmpty(script))
        {
            SetStepError("请先添加一个操作步骤。");
            return;
        }

        try
        {
            if (Application.Current is App app)
            {
                if (!ScriptExecutor.TryParse(script, out _, out string error)) { SetStepError(error); return; }
                BtnTest.IsEnabled = false;
                SetStepError("");
                SetHotKeyTip("已开始试运行，请完成屏幕上的操作。");
                bool completed = await app.RunScriptAsync(script);
                SetHotKeyTip(completed ? "动作已完成，所有模块已按顺序执行。" : "动作已停止，后续模块没有执行。");
            }
        }
        catch (Exception ex)
        {
            SetStepError("试运行未完成：" + ex.Message);
        }
        finally { BtnTest.IsEnabled = true; }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        StopMousePrefixRecording();
        if (_isRecording) StopRecording(false);

        string name = (TxtName.Text ?? "").Trim();
        string script = (TxtScript.Text ?? "").Trim();

        if (string.IsNullOrEmpty(name))
        {
            SetHotKeyTip("请输入动作名称。");
            TxtName.Focus();
            return;
        }

        string? keyboardConflict = Services.BuiltinShortcutsStore.FindActionConflict(_modifiers, _virtualKey,
            _otherActions ?? Array.Empty<ActionDefinition>(), _originalAction, _otherShortcuts);
        if (keyboardConflict != null)
        { SetHotKeyTip("此组合已被" + keyboardConflict + "占用，请重新录制。", true); return; }

        if (string.IsNullOrEmpty(script))
        {
            SetStepError("请至少添加一个操作步骤。");
            TxtScript.Focus();
            return;
        }

        if (!ScriptExecutor.TryParse(script, out _, out string scriptError))
        {
            SetStepError(scriptError);
            return;
        }

        var mouse = ReadMouseBinding();
        string mouseConflict = MouseBindingConflict(mouse);
        if (mouseConflict.Length != 0) { SetMouseTip(mouseConflict); return; }
        if (mouse.HasMouseBinding && !(mouse.MouseTrigger == ActionMouseTrigger.Drag
                ? ActionMouseBinding.SupportsDrag(script) : ActionMouseBinding.SupportsClick(script)))
        {
            SetMouseTip("此动作不支持该触发方式，请重选");
            return;
        }
        if (mouse.HasMouseBinding && _otherActions != null)
        {
            foreach (var other in _otherActions)
            {
                if (ReferenceEquals(other, _originalAction) || !other.HasMouseBinding) continue;
                if (ActionMouseBinding.Conflicts(other, mouse))
                {
                    SetMouseTip("此组合已绑定「" + other.Name + "」");
                    return;
                }
            }
        }

        // 如果用户选择了“自定义”，且原来不是预设 ID，确保生成 custom. 前缀
        if (!_isBuiltin && (_isNew || _actionId.StartsWith("builtin.")))
        {
            _actionId = ActionDefinition.NewCustomId();
            _isBuiltin = false;
            UpdateActionFields(false);
        }

        ResultAction = new ActionDefinition
        {
            Id = _actionId,
            Name = name,
            Script = script,
            IsBuiltin = _isBuiltin,
            HotKeyModifiers = _modifiers,
            HotKeyVirtualKey = _virtualKey,
            MouseModifiers = mouse.MouseModifiers,
            MouseButton = mouse.MouseButton,
            MouseTrigger = mouse.MouseTrigger,
            MousePrefixKey = mouse.MousePrefixKey,
            // 动作存在于列表即生效，删除即停用（docs/configuration.md 的 v2.1 口径），窗内不再放第二份启用状态
            Enabled = true,
        };

        // 预设类型与实例身份分开：同一预设可以绑定多组键，但每条动作必须有唯一 ID。
        if (ResultAction != null && _otherActions != null)
            ActionsStore.EnsureUniqueIdentity(ResultAction, _otherActions, _originalAction);
        Close(true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prefixRecorder != null) { e.Handled = true; return; }
        if (e.Key == Key.Escape && _moduleDrag.IsActive)
        {
            _moduleDrag.Reset();
            ClearModuleDragVisuals();
            e.Handled = true;
            return;
        }
        if (!_isRecording) return;
        e.Handled = true;
        // 原生录制器配对拦截所有待录键，避免 Tab／Space 驱动窗口默认操作。
    }

    private static string FormatHotKey(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        ActionKeyboardBinding.AddModifierNames(parts, modifiers);
        parts.Add(FormatVirtualKey(vk));
        return string.Join(" + ", parts);
    }

    private static string FormatVirtualKey(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);

        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x14 => "CapsLock",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0xC0 => "~",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => "Key" + vk
        };
    }
}
