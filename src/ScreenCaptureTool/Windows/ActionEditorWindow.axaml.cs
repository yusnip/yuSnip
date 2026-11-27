using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Controls.ActionEditor;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Core.Scripting;
using NativeMethods = ScreenCaptureTool.Platform.NativeMethods;

namespace ScreenCaptureTool.Windows;

public partial class ActionEditorWindow : Window
{
    private const double PresetWindowWidth = 560;
    private const double PresetWindowHeight = 496;
    private const double CustomWindowWidth = 860;
    private const double CustomWindowHeight = 660;

    private readonly bool _isNew;
    private readonly ActionDefinition _originalAction;
    private readonly IReadOnlyList<ActionDefinition>? _otherActions;
    private readonly IReadOnlyList<Services.BuiltinShortcutsStore.BuiltinShortcut> _otherShortcuts;

    private string _actionId;
    private bool _isBuiltin;
    private string _currentScript;
    private string _customName = "我的动作";
    private string _customScript = ActionModuleRegistry.DefaultScript;
    private bool _customScriptView;

    private bool _isUpdatingUi = true;
    private bool _updatingMouseUi;
    private bool _hasOpened;

    private uint _modifiers;
    private uint _virtualKey;
    private bool _isRecording;
    private bool _ownsHotkeySuspension;
    private ActionMouseKey _mousePrefixKey;
    private Services.MousePrefixRecorder? _prefixRecorder;
    private int _prefixRecordingGeneration;
    private Services.KeyboardShortcutRecorder? _keyboardRecorder;
    private int _keyboardRecordingGeneration;

    private double _savedCustomWidth = CustomWindowWidth;
    private double _savedCustomHeight = CustomWindowHeight;
    private Control? _paletteDragGhost;
    private bool _triggerDrawerExpanded;

    /// <summary>保存成功的动作对象</summary>
    public ActionDefinition? ResultAction { get; private set; }

    public ActionEditorWindow() : this(null)
    {
    }

    public ActionEditorWindow(
        ActionDefinition? actionToEdit,
        IReadOnlyList<ActionDefinition>? otherActions = null,
        IReadOnlyList<Services.BuiltinShortcutsStore.BuiltinShortcut>? otherShortcuts = null)
    {
        InitializeComponent();

        _isNew = actionToEdit == null;
        _otherActions = otherActions;
        _otherShortcuts = otherShortcuts ?? Services.BuiltinShortcutsStore.Load();
        _originalAction = actionToEdit ?? new ActionDefinition
        {
            Id = ActionDefinition.NewCustomId(),
            Name = "我的动作",
            Script = ActionModuleRegistry.DefaultScript,
            IsBuiltin = false,
            Enabled = true,
        };

        _actionId = _originalAction.Id;
        _isBuiltin = _originalAction.IsBuiltin;
        _currentScript = _originalAction.Script;
        if (!_isBuiltin)
        {
            _customName = _originalAction.Name;
            _customScript = _originalAction.Script;
        }
        _modifiers = _originalAction.HotKeyModifiers;
        _virtualKey = _originalAction.HotKeyVirtualKey;
        _mousePrefixKey = _originalAction.MousePrefixKey;

        TxtWindowTitle.Text = _isNew ? "新增动作" : $"编辑动作 · {_originalAction.Name}";
        BtnSave.Content = _isNew ? "添加" : "保存";

        PalettePanel.AttachHostWindow(this);
        PipelineView.AttachHostWindow(this);
        WireWorkbenchEvents();
        PopulatePresets();
        PopulateTemplatesMenu();
        LoadInitialState();

        // 新增动作默认进入预设入口，可一键绑键或转为自定义工作台
        if (_isNew)
        {
            SwitchToPresetMode(selectCurrentPreset: true, resizeCentered: false);
        }

        // 在 Opened 之前先设好初始尺寸，让 WindowStartupLocation="CenterOwner" 按真实初始尺寸居中显示
        Width = _isBuiltin ? PresetWindowWidth : CustomWindowWidth;
        Height = _isBuiltin ? PresetWindowHeight : CustomWindowHeight;
        MinWidth = _isBuiltin ? 520 : 720;
        MinHeight = _isBuiltin ? 430 : 520;

        Opened += (_, _) =>
        {
            _hasOpened = true;
            ApplyWindowSizingForMode(_isBuiltin, recenterOnCurrentCenter: false);
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressedTunnel, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            if (_isRecording) StopRecording(false);
            StopMousePrefixRecording();
            ScriptEditorPanel.CloseMenus();
            PalettePanel.CancelActiveDrag();
            PipelineView.CancelActiveDrag();
            ClearDragGhost();
            RestoreEditorHotkeys();
        };
        Deactivated += (_, _) =>
        {
            if (_isRecording) StopRecording(false);
            StopMousePrefixRecording();
            PalettePanel.CancelActiveDrag();
            PipelineView.CancelActiveDrag();
            ClearDragGhost();
        };
        MousePickPopup.Closed += (_, _) => StopMousePrefixRecording();
    }

    private void WireWorkbenchEvents()
    {
        PalettePanel.ModuleSelected += module =>
        {
            PipelineView.AddModule(module.Id);
            ClearRunStatus();
        };

        PalettePanel.ModuleDragStarted += (module, winPt) =>
        {
            ShowDragGhost(module, winPt);
            PipelineView.UpdateExternalDrop(winPt, module.Name);
        };

        PalettePanel.ModuleDragMoved += (module, winPt) =>
        {
            MoveDragGhost(winPt);
            PipelineView.UpdateExternalDrop(winPt, module.Name);
        };

        PalettePanel.ModuleDragDropped += (module, winPt) =>
        {
            ClearDragGhost();
            if (PipelineView.CommitExternalDrop(module.Id, winPt, module.Name))
                ClearRunStatus();
        };

        PalettePanel.ModuleDragCanceled += () =>
        {
            ClearDragGhost();
            PipelineView.ClearExternalDrop();
        };

        PipelineView.StepsChanged += steps =>
        {
            _currentScript = ActionModuleRegistry.BuildScript(steps);
            _customScript = _currentScript;
            ScriptEditorPanel.ScriptText = _currentScript;
            RefreshCliTooltip();
            RefreshMouseCapabilities();
        };

        PipelineView.QuickAddModuleRequested += moduleId => PipelineView.AddModule(moduleId);
        PipelineView.OpenTemplatesRequested += () => BtnTemplates.Flyout?.ShowAt(BtnTemplates);

        ScriptEditorPanel.ScriptChanged += script =>
        {
            _currentScript = script;
            _customScript = script;
            RefreshCliTooltip();
            RefreshMouseCapabilities();
        };
    }

    private void ShowDragGhost(ActionModuleDescriptor module, Point windowPoint) =>
        ShowDragGhostText(module.Name, windowPoint);

    private void ShowDragGhostText(string text, Point windowPoint)
    {
        ClearDragGhost();
        _paletteDragGhost = new ContinuousBorder
        {
            Classes = { "actionDragGhost" },
            Padding = new Thickness(12, 7),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DragOverlayCanvas.Children.Add(_paletteDragGhost);
        MoveDragGhost(windowPoint);
    }

    private void MoveDragGhost(Point windowPoint)
    {
        if (_paletteDragGhost == null) return;
        Canvas.SetLeft(_paletteDragGhost, windowPoint.X + 12);
        Canvas.SetTop(_paletteDragGhost, windowPoint.Y - 16);
    }

    private void ClearDragGhost()
    {
        if (_paletteDragGhost == null) return;
        DragOverlayCanvas.Children.Remove(_paletteDragGhost);
        _paletteDragGhost = null;
    }

    private void PopulatePresets()
    {
        CmbPreset.Items.Clear();
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

            foreach (var (id, name, _) in items)
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

    private void PopulateTemplatesMenu()
    {
        var menu = new MenuFlyout();
        foreach (var tpl in ActionModuleRegistry.Templates)
        {
            var item = new MenuItem { Header = tpl.Name };
            ToolTip.SetTip(item, tpl.Description);
            var captured = tpl;
            item.Click += (_, _) => ApplyTemplate(captured);
            menu.Items.Add(item);
        }
        BtnTemplates.Flyout = menu;
    }

    private void ApplyTemplate(ActionTemplateItem tpl)
    {
        TxtName.Text = tpl.Name;
        _customName = tpl.Name;
        _currentScript = tpl.Script;
        _customScript = tpl.Script;
        ScriptEditorPanel.ScriptText = tpl.Script;
        if (ActionModuleRegistry.TryParseSteps(tpl.Script, out var steps))
        {
            PipelineView.SetSteps(steps);
            SetCustomViewMode(scriptMode: false);
        }
        RefreshCliTooltip();
        RefreshMouseCapabilities();
        SetRunStatus($"已套用模板「{tpl.Name}」，可继续增删或调整步骤。", isError: false);
    }

    private void LoadInitialState()
    {
        _isUpdatingUi = true;
        try
        {
            TxtName.Text = _originalAction.Name;
            ScriptEditorPanel.ScriptText = _originalAction.Script;

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

            UpdateHotKeyDisplay();

            RadPresetType.IsChecked = _isBuiltin;
            RadCustomType.IsChecked = !_isBuiltin;
            PresetWorkspace.IsVisible = _isBuiltin;
            CustomWorkspace.IsVisible = !_isBuiltin;
            CustomHeaderTools.IsVisible = !_isBuiltin;
            MountTriggerPanel(_isBuiltin);

            if (_isBuiltin)
            {
                UpdatePresetInfoCard(_actionId);
            }
            else
            {
                SyncStepsFromCurrentScript();
            }

            RefreshMouseCapabilities(_originalAction.HasMouseBinding ? _originalAction.MouseTrigger : null);
            UpdateTriggerSummary();
            RefreshCliTooltip();
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void SyncStepsFromCurrentScript()
    {
        if (string.IsNullOrWhiteSpace(_currentScript))
        {
            PipelineView.SetSteps([]);
            SetCustomViewMode(scriptMode: false);
            return;
        }

        if (ActionModuleRegistry.TryParseSteps(_currentScript, out var steps))
        {
            _currentScript = ActionModuleRegistry.BuildScript(steps);
            _customScript = _currentScript;
            ScriptEditorPanel.ScriptText = _currentScript;
            PipelineView.SetSteps(steps);
            SetCustomViewMode(scriptMode: false);
        }
        else
        {
            ScriptEditorPanel.ScriptText = _currentScript;
            SetCustomViewMode(scriptMode: true);
        }
    }

    private void SetCustomViewMode(bool scriptMode)
    {
        _customScriptView = scriptMode;
        RadStepMode.IsChecked = !scriptMode;
        RadScriptMode.IsChecked = scriptMode;
        PalettePanel.IsVisible = !scriptMode;
        PipelineView.IsVisible = !scriptMode;
        ScriptEditorPanel.IsVisible = scriptMode;
    }

    /// <summary>
    /// 切换模式时以当前窗口中心点为锚点平滑缩放并限制在屏幕工作区内，不再从左上角向外弹开。
    /// </summary>
    private void ApplyWindowSizingForMode(bool presetMode, bool recenterOnCurrentCenter)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double scale = screen?.Scaling ?? RenderScaling;
        if (scale <= 0) scale = 1.0;

        double prevDipW = Bounds.Width > 0 ? Bounds.Width : Width;
        double prevDipH = Bounds.Height > 0 ? Bounds.Height : Height;
        double centerX = Position.X + prevDipW * scale / 2.0;
        double centerY = Position.Y + prevDipH * scale / 2.0;

        double targetW = presetMode ? PresetWindowWidth : _savedCustomWidth;
        double targetH = presetMode ? PresetWindowHeight : _savedCustomHeight;

        if (screen != null)
        {
            double maxW = Math.Max(540, screen.WorkingArea.Width / scale - 60);
            double maxH = Math.Max(440, screen.WorkingArea.Height / scale - 60);
            targetW = Math.Min(targetW, maxW);
            targetH = Math.Min(targetH, maxH);
        }

        MinWidth = presetMode ? 520 : 720;
        MinHeight = presetMode ? 430 : 520;
        Width = targetW;
        Height = targetH;

        if (recenterOnCurrentCenter && _hasOpened)
        {
            double newPhysW = targetW * scale;
            double newPhysH = targetH * scale;
            int newX = (int)Math.Round(centerX - newPhysW / 2.0);
            int newY = (int)Math.Round(centerY - newPhysH / 2.0);

            if (screen != null)
            {
                var work = screen.WorkingArea;
                int maxX = Math.Max(work.X, work.Right - (int)Math.Round(newPhysW));
                int maxY = Math.Max(work.Y, work.Bottom - (int)Math.Round(newPhysH));
                newX = Math.Clamp(newX, work.X, maxX);
                newY = Math.Clamp(newY, work.Y, maxY);
            }
            Position = new PixelPoint(newX, newY);
        }
    }

    // ════════════════ 预设 / 自定义模式切换 ════════════════

    private void OnActionTypeChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || sender is not RadioButton) return;
        bool wantPreset = ReferenceEquals(sender, RadPresetType);
        if (wantPreset == _isBuiltin) return;

        if (wantPreset)
        {
            if (!_isBuiltin && !double.IsNaN(Width) && Width >= 700)
            {
                _savedCustomWidth = Bounds.Width > 0 ? Bounds.Width : Width;
                _savedCustomHeight = Bounds.Height > 0 ? Bounds.Height : Height;
            }
            _customName = string.IsNullOrWhiteSpace(TxtName.Text) ? "我的动作" : TxtName.Text.Trim();
            _customScript = _currentScript;
            SwitchToPresetMode(selectCurrentPreset: true, resizeCentered: true);
        }
        else
        {
            SwitchToCustomMode(_customName, _customScript, preferScriptView: _customScriptView);
        }
    }

    private void SwitchToPresetMode(bool selectCurrentPreset, bool resizeCentered)
    {
        _isUpdatingUi = true;
        try
        {
            _isBuiltin = true;
            RadPresetType.IsChecked = true;
            RadCustomType.IsChecked = false;
            PresetWorkspace.IsVisible = true;
            CustomWorkspace.IsVisible = false;
            CustomHeaderTools.IsVisible = false;
            MountTriggerPanel(presetMode: true);
            ClearRunStatus();
        }
        finally
        {
            _isUpdatingUi = false;
        }

        if (selectCurrentPreset) OnPresetSelectionChanged(null, null);
        if (resizeCentered) ApplyWindowSizingForMode(presetMode: true, recenterOnCurrentCenter: true);
    }

    private void SwitchToCustomMode(string name, string script, bool preferScriptView)
    {
        _isUpdatingUi = true;
        try
        {
            _isBuiltin = false;
            if (_isNew || _actionId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase))
                _actionId = ActionDefinition.NewCustomId();
            RadPresetType.IsChecked = false;
            RadCustomType.IsChecked = true;
            PresetWorkspace.IsVisible = false;
            CustomWorkspace.IsVisible = true;
            CustomHeaderTools.IsVisible = true;
            MountTriggerPanel(presetMode: false);

            TxtName.Text = name;
            _currentScript = script;
            _customScript = script;
            ScriptEditorPanel.ScriptText = script;

            if (preferScriptView) SetCustomViewMode(scriptMode: true);
            else SyncStepsFromCurrentScript();

            RefreshMouseCapabilities();
            UpdateTriggerSummary();
            RefreshCliTooltip();
            ClearRunStatus();
        }
        finally
        {
            _isUpdatingUi = false;
        }
        ApplyWindowSizingForMode(presetMode: false, recenterOnCurrentCenter: true);
    }

    private void OnPresetSelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (_isUpdatingUi) return;
        if (CmbPreset.SelectedItem is not ComboBoxItem item) return;
        if (item.Classes.Contains("groupHeader") || !item.IsEnabled) return;

        string? presetId = item.Tag?.ToString();
        if (string.IsNullOrEmpty(presetId)) return;

        var preset = ActionsStore.FindPresetById(presetId);
        if (preset == null) return;

        var (id, name, script) = preset.Value;
        _actionId = id;
        _isBuiltin = true;
        _currentScript = script;
        TxtName.Text = name;
        UpdatePresetInfoCard(id);
        RefreshMouseCapabilities();
        RefreshCliTooltip();

        if (_modifiers == 0 && _virtualKey == 0 && ActionsStore.TryGetBuiltinDefaultHotKey(id, out uint defMods, out uint defVk))
        {
            _modifiers = defMods;
            _virtualKey = defVk;
            UpdateHotKeyDisplay();
            SetHotKeyTip("已自动填入推荐快捷键。");
        }
    }

    private void UpdatePresetInfoCard(string presetId)
    {
        var preset = ActionsStore.FindPresetById(presetId);
        string title = preset?.Name ?? _originalAction.Name;
        string category = "内置预设";
        foreach (var (cat, items) in ActionsStore.CategorizedBuiltins)
        {
            if (items.Any(it => string.Equals(it.Id, presetId, StringComparison.OrdinalIgnoreCase)))
            {
                category = "内置预设 · " + cat;
                break;
            }
        }

        TxtPresetTitle.Text = title;
        TxtPresetCategory.Text = category;
        TxtPresetDesc.Text = ActionModuleRegistry.DescribePreset(presetId);

        var (iconKey, catEnum) = presetId switch
        {
            "builtin.capture" => ("brush", ActionModuleCategory.Control),
            "builtin.longScroll" => ("longscroll", ActionModuleCategory.Control),
            "builtin.record" or "builtin.recordQuick" => ("record", ActionModuleCategory.Control),
            "builtin.captureOcr" or "builtin.captureOcrToast" => ("ocr", ActionModuleCategory.Process),
            "builtin.captureVision" or "builtin.captureVisionPrompt" => ("aivision", ActionModuleCategory.Output),
            "builtin.captureMattingGeneral" or "builtin.captureMattingPortrait" => ("matting", ActionModuleCategory.Process),
            var id when id.StartsWith("builtin.translate", StringComparison.OrdinalIgnoreCase) => ("translate", ActionModuleCategory.Process),
            var id when id.Contains("Sticker", StringComparison.OrdinalIgnoreCase) || id.Contains("pin", StringComparison.OrdinalIgnoreCase) => ("sticker", ActionModuleCategory.Control),
            _ => ("image", ActionModuleCategory.Input)
        };
        PresetIconHost.Background = ActionModuleIconHelper.CategorySoftFillBrush(catEnum);
        PresetIconHost.Child = ActionModuleIconHelper.CreateIcon(iconKey, ActionModuleIconHelper.CategoryAccentBrush(catEnum), 17);
    }

    private void OnConvertPresetToCustomClick(object? sender, RoutedEventArgs e)
    {
        var preset = ActionsStore.FindPresetById(_actionId);
        string baseName = preset?.Name ?? (TxtName.Text ?? "自定义动作");
        string convertedScript = _currentScript;
        if (ActionModuleRegistry.TryParseSteps(_currentScript, out var steps) && steps.Count > 0)
        {
            convertedScript = ActionModuleRegistry.BuildScript(steps);
        }
        SwitchToCustomMode($"{baseName}（自定义）", convertedScript, preferScriptView: false);
        SetRunStatus("已将预设拆解为自定义流程，可继续添加或调整模块。", isError: false);
    }

    private void OnStepModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || sender is not RadioButton) return;
        bool wantScript = ReferenceEquals(sender, RadScriptMode);
        if (wantScript == _customScriptView) return;

        if (wantScript)
        {
            ScriptEditorPanel.ScriptText = _currentScript;
            SetCustomViewMode(scriptMode: true);
            ClearRunStatus();
            return;
        }

        string text = ScriptEditorPanel.ScriptText;
        if (string.IsNullOrWhiteSpace(text))
        {
            _currentScript = "";
            PipelineView.SetSteps([]);
            SetCustomViewMode(scriptMode: false);
            ClearRunStatus();
            return;
        }

        if (!ActionModuleRegistry.TryParseSteps(text, out var steps))
        {
            _isUpdatingUi = true;
            RadScriptMode.IsChecked = true;
            RadStepMode.IsChecked = false;
            _isUpdatingUi = false;
            SetRunStatus("当前脚本包含无法转为可视化模块的语法，已保留在脚本源码视图。", isError: true);
            return;
        }

        _currentScript = ActionModuleRegistry.BuildScript(steps);
        _customScript = _currentScript;
        ScriptEditorPanel.ScriptText = _currentScript;
        PipelineView.SetSteps(steps);
        SetCustomViewMode(scriptMode: false);
        ClearRunStatus();
    }

    // ════════════════ 外部命令复制 ════════════════

    private string BuildCommandString()
    {
        string script = (_currentScript ?? "").Trim();
        if (script.Length == 0) return "yuSnip.exe -r \"\"";
        return "yuSnip.exe -r \"" + script.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private void RefreshCliTooltip()
    {
        if (BtnCopyCli == null) return;
        ToolTip.SetTip(BtnCopyCli, "点击复制外部调用命令：\n" + BuildCommandString());
    }

    private async void OnCopyCommandClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string cmd = BuildCommandString();
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(cmd);
                BtnCopyCli.Content = "已复制";
                SetRunStatus("已复制命令行到剪贴板，可用于 Quicker / 脚本调用。", isError: false);
                await Task.Delay(1200);
                BtnCopyCli.Content = "复制命令";
            }
        }
        catch (Exception ex)
        {
            SetRunStatus("复制命令失败：" + ex.Message, isError: true);
        }
    }

    // ════════════════ 底部运行状态反馈 ════════════════

    private void SetRunStatus(string text, bool isError)
    {
        TxtRunStatus.Text = text;
        TxtRunStatus.IsVisible = !string.IsNullOrEmpty(text);
        TxtRunStatus.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(isError ? "SctDangerBrush" : "SctTextMutedBrush"));
    }

    private void ClearRunStatus()
    {
        TxtRunStatus.Text = "";
        TxtRunStatus.IsVisible = false;
    }

    // ════════════════ 试运行与保存 ════════════════

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        string script = (_currentScript ?? "").Trim();
        if (string.IsNullOrEmpty(script))
        {
            SetRunStatus("请先在流程中添加至少一个模块。", isError: true);
            return;
        }

        if (!ScriptExecutor.TryParse(script, out _, out string error))
        {
            SetRunStatus(error, isError: true);
            return;
        }

        try
        {
            if (Application.Current is App app)
            {
                BtnTest.IsEnabled = false;
                SetRunStatus("正在试运行，请在屏幕上完成对应操作…", isError: false);
                bool completed = await app.RunScriptAsync(script);
                SetRunStatus(completed ? "试运行完成，所有步骤已按顺序执行。" : "试运行已中止，后续步骤未执行。", isError: !completed);
            }
        }
        catch (Exception ex)
        {
            SetRunStatus("试运行失败：" + ex.Message, isError: true);
        }
        finally
        {
            BtnTest.IsEnabled = true;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        StopMousePrefixRecording();
        if (_isRecording) StopRecording(false);

        string name = (TxtName.Text ?? "").Trim();
        string script = (_currentScript ?? "").Trim();

        if (string.IsNullOrEmpty(name))
        {
            SetRunStatus("请输入动作名称。", isError: true);
            TxtName.Focus();
            return;
        }

        string? keyboardConflict = Services.BuiltinShortcutsStore.FindActionConflict(_modifiers, _virtualKey,
            _otherActions ?? Array.Empty<ActionDefinition>(), _originalAction, _otherShortcuts);
        if (keyboardConflict != null)
        {
            if (!_isBuiltin) SetTriggerDrawerExpanded(true);
            SetHotKeyTip("已被" + keyboardConflict + "占用，请重录。", true);
            SetRunStatus("键盘快捷键存在冲突，请重新录制。", isError: true);
            return;
        }

        if (string.IsNullOrEmpty(script))
        {
            SetRunStatus("请至少添加一个操作模块。", isError: true);
            return;
        }

        if (!ScriptExecutor.TryParse(script, out _, out string scriptError))
        {
            SetRunStatus(scriptError, isError: true);
            return;
        }

        var mouse = ReadMouseBinding();
        string mouseConflict = MouseBindingConflict(mouse);
        if (mouseConflict.Length != 0)
        {
            if (!_isBuiltin) SetTriggerDrawerExpanded(true);
            SetMouseTip(mouseConflict, isConflict: true);
            SetRunStatus(mouseConflict, isError: true);
            return;
        }

        if (mouse.HasMouseBinding && !(mouse.MouseTrigger == ActionMouseTrigger.Drag
            ? ActionMouseBinding.SupportsDrag(script) : ActionMouseBinding.SupportsClick(script)))
        {
            if (!_isBuiltin) SetTriggerDrawerExpanded(true);
            SetMouseTip("此动作不支持该鼠标触发方式，请重选。");
            SetRunStatus("当前动作不支持所选鼠标触发方式。", isError: true);
            return;
        }

        if (!_isBuiltin && (_isNew || _actionId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase)))
        {
            _actionId = ActionDefinition.NewCustomId();
            _isBuiltin = false;
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
            Enabled = true,
        };

        if (_otherActions != null)
            ActionsStore.EnsureUniqueIdentity(ResultAction, _otherActions, _originalAction);

        Close(true);
    }

    private void OnWindowPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is not TextBox) return;
        for (Visual? cur = e.Source as Visual; cur != null; cur = cur.GetVisualParent())
        {
            if (cur is TextBox or ActionNumberInput) return;
        }
        FocusManager?.ClearFocus();
    }

    // ════════════════ 键盘与全局鼠标录制（方案 B：预设竖排 / 自定义折叠抽屉）════════════════

    private void MountTriggerPanel(bool presetMode)
    {
        if (TriggerFormPanel == null || PresetTriggerSlot == null || CustomTriggerDrawerSlot == null) return;
        MousePickPopup.IsOpen = false;
        StopMousePrefixRecording();
        if (_isRecording) StopRecording(false);

        if (TriggerFormPanel.Parent is Panel oldParent)
            oldParent.Children.Remove(TriggerFormPanel);

        ApplyTriggerFormLayout(presetMode);

        if (presetMode)
        {
            PresetTriggerSlot.Children.Add(TriggerFormPanel);
        }
        else
        {
            CustomTriggerDrawerSlot.Children.Add(TriggerFormPanel);
            SetTriggerDrawerExpanded(_triggerDrawerExpanded);
        }
    }

    private void ApplyTriggerFormLayout(bool presetMode)
    {
        TriggerFormPanel.Spacing = presetMode ? 12 : 8;
        MousePickPopup.Placement = presetMode ? PlacementMode.Top : PlacementMode.Bottom;
        MousePickPopup.VerticalOffset = presetMode ? 10 : -10;

        static void ConfigureRow(Grid grid, TextBlock label, string title, Grid host, TextBlock tip, bool vertical)
        {
            if (vertical)
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
                label.Text = title;
                label.Classes.Set("secondary", false);
                label.FontWeight = FontWeight.Medium;
                label.Margin = new Thickness(0, 0, 0, 5);
                Grid.SetRow(label, 0);
                Grid.SetColumn(label, 0);
                Grid.SetRow(host, 1);
                Grid.SetColumn(host, 0);
                Grid.SetRow(tip, 2);
                Grid.SetColumn(tip, 0);
            }
            else
            {
                grid.ColumnDefinitions = new ColumnDefinitions("64,*");
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                label.Text = title;
                label.Classes.Set("secondary", true);
                label.FontWeight = FontWeight.Normal;
                label.Margin = new Thickness(0);
                Grid.SetRow(label, 0);
                Grid.SetColumn(label, 0);
                Grid.SetRow(host, 0);
                Grid.SetColumn(host, 1);
                Grid.SetRow(tip, 1);
                Grid.SetColumn(tip, 1);
            }
        }

        ConfigureRow(HotKeyRowGrid, LblHotKey, "键盘快捷键", HotKeyControlHost, TxtHotKeyTip, presetMode);
        ConfigureRow(MouseRowGrid, LblMouse, "全局鼠标", MouseControlHost, TxtMouseTip, presetMode);
    }

    private void OnTriggerDrawerToggleClick(object? sender, RoutedEventArgs e) =>
        SetTriggerDrawerExpanded(!_triggerDrawerExpanded);

    private void SetTriggerDrawerExpanded(bool expanded)
    {
        _triggerDrawerExpanded = expanded;
        if (CustomTriggerDrawer != null) CustomTriggerDrawer.IsVisible = expanded;
        if (TxtTriggerToggleLabel != null) TxtTriggerToggleLabel.Text = expanded ? "收起" : "设置";
        if (!expanded)
        {
            MousePickPopup.IsOpen = false;
            StopMousePrefixRecording();
            if (_isRecording) StopRecording(false);
        }
    }

    private void UpdateTriggerSummary()
    {
        if (TxtTriggerSummary == null) return;
        string? hkConflict = _virtualKey != 0
            ? Services.BuiltinShortcutsStore.FindActionConflict(_modifiers, _virtualKey,
                _otherActions ?? Array.Empty<ActionDefinition>(), _originalAction, _otherShortcuts)
            : null;
        ActionDefinition currentMouse = ReadMouseBinding();
        string mouseConflict = MouseBindingConflict(currentMouse);

        if (hkConflict != null)
        {
            TxtTriggerSummary.Text = FormatHotKey(_modifiers, _virtualKey) + "（与" + hkConflict + "冲突）";
            TxtTriggerSummary.FontWeight = FontWeight.Medium;
            TxtTriggerSummary.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctDangerBrush"));
            return;
        }
        if (mouseConflict.Length != 0)
        {
            TxtTriggerSummary.Text = ActionMouseBinding.Format(currentMouse) + "（" + mouseConflict + "）";
            TxtTriggerSummary.FontWeight = FontWeight.Medium;
            TxtTriggerSummary.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctDangerBrush"));
            return;
        }

        bool hasKey = _virtualKey != 0;
        bool hasMouse = currentMouse.HasMouseBinding;
        string hkText = hasKey ? FormatHotKey(_modifiers, _virtualKey) : "快捷键未绑定";
        string mouseText = !BtnMouseBinding.IsEnabled
            ? "全局鼠标不支持"
            : (hasMouse ? ActionMouseBinding.Format(currentMouse) : "全局鼠标未绑定");

        TxtTriggerSummary.Text = (hasKey || hasMouse)
            ? $"{hkText}  ·  {mouseText}"
            : "未绑定快捷键或全局鼠标";
        TxtTriggerSummary.FontWeight = (hasKey || hasMouse) ? FontWeight.Medium : FontWeight.Normal;
        TxtTriggerSummary.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                (hasKey || hasMouse) ? "SctTextPrimaryBrush" : "SctTextMutedBrush"));
    }

    private void RefreshMouseCapabilities(ActionMouseTrigger? preferred = null)
    {
        if (RadMouseDrag == null || RadMouseClick == null || TxtMouseTip == null) return;
        string script = _currentScript ?? "";
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
        SetMouseTip(click || drag ? "" : "当前动作不支持全局鼠标");
        UpdateMouseDisplay();
    }

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

    private void UpdateMouseDisplay()
    {
        if (BtnMouseBinding == null || BtnClearMouse == null || TxtMouseBinding == null) return;
        ActionDefinition current = ReadMouseBinding();
        bool hasMouse = current.HasMouseBinding;
        string conflict = MouseBindingConflict(current);
        bool hasConflict = conflict.Length != 0;

        if (!BtnMouseBinding.IsEnabled)
        {
            TxtMouseBinding.Text = "当前动作不支持全局鼠标";
            BtnMouseBinding.Classes.Set("unbound", true);
            BtnMouseBinding.Classes.Set("conflict", false);
        }
        else
        {
            TxtMouseBinding.Text = hasMouse ? ActionMouseBinding.Format(current) : "未绑定 · 点击设置";
            BtnMouseBinding.Classes.Set("unbound", !hasMouse);
            BtnMouseBinding.Classes.Set("conflict", hasConflict);
        }

        BtnClearMouse.IsVisible = hasMouse;

        if (BtnMousePrefix != null && TxtMousePrefix != null && _prefixRecorder == null)
        {
            bool prefixBound = _mousePrefixKey.IsValid;
            TxtMousePrefix.Text = prefixBound ? _mousePrefixKey.ToString() : "可选 · 点击录制";
            BtnMousePrefix.Classes.Set("unbound", !prefixBound);
            BtnMousePrefix.Classes.Set("recording", false);
        }
        if (BtnClearMousePrefix != null)
            BtnClearMousePrefix.IsVisible = _mousePrefixKey.IsValid && _prefixRecorder == null;

        if (TxtMousePopupPreview != null)
        {
            if (hasConflict)
            {
                TxtMousePopupPreview.Text = conflict;
                TxtMousePopupPreview.Bind(TextBlock.ForegroundProperty,
                    new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctDangerBrush"));
            }
            else if (hasMouse)
            {
                TxtMousePopupPreview.Text = ActionMouseBinding.Format(current);
                TxtMousePopupPreview.Bind(TextBlock.ForegroundProperty,
                    new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctWorkspaceBrandTextBrush"));
            }
            else
            {
                TxtMousePopupPreview.Text = "请选辅助键或录制引导键";
                TxtMousePopupPreview.Bind(TextBlock.ForegroundProperty,
                    new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctTextMutedBrush"));
            }
        }
        if (BtnMousePopupClear != null)
            BtnMousePopupClear.IsVisible = hasMouse || _mousePrefixKey.IsValid;

        if (hasConflict) SetMouseTip(conflict, isConflict: true);
        else if (ActionMouseBinding.SupportsClick(_currentScript ?? "")) SetMouseTip("");
        UpdateTriggerSummary();
    }

    private void SetMouseTip(string text, bool isConflict = false)
    {
        TxtMouseTip.Text = text;
        TxtMouseTip.IsVisible = !string.IsNullOrEmpty(text);
        TxtMouseTip.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(isConflict ? "SctDangerBrush" : "SctTextMutedBrush"));
    }

    private void OnMouseBindingClick(object? sender, RoutedEventArgs e)
    {
        if (!BtnMouseBinding.IsEnabled) return;
        if (_isRecording) StopRecording(false);
        MousePickPopup.IsOpen = !MousePickPopup.IsOpen;
    }

    private void OnMouseBindingPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && ReadMouseBinding().HasMouseBinding)
        {
            OnClearMouseClick(sender, e);
            e.Handled = true;
        }
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
            _prefixRecorder = new Services.MousePrefixRecorder(handle, key => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _prefixRecordingGeneration) return;
                _prefixRecorder = null; RestoreEditorHotkeys();
                if (key.IsValid) AcceptMousePrefixKey(key);
                else { UpdateMouseDisplay(); SetMousePrefixTip("录制已取消，原绑定保留。"); }
            }));
            BtnMousePrefix.Classes.Set("unbound", false);
            BtnMousePrefix.Classes.Set("recording", true);
            BtnClearMousePrefix.IsVisible = false;
            TxtMousePrefix.Text = "请按任意键…";
            SetMousePrefixTip("按下并松开一个键；Esc 也可录制，再次点击按钮可取消。");
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
                    && other.HotKeyModifiers == ActionMouseBinding.EffectiveModifiers(mouse)) return "引导键与「" + other.Name + "」的快捷键冲突";
            }
        if (mouse.MousePrefixKey.IsValid && _virtualKey == mouse.MousePrefixKey.VirtualKey
            && _modifiers == ActionMouseBinding.EffectiveModifiers(mouse)) return "引导键与当前动作快捷键冲突";
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

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnRecordHotKeyClick(object? sender, RoutedEventArgs e)
    {
        StopMousePrefixRecording();
        MousePickPopup.IsOpen = false;
        if (_isRecording) { StopRecording(false); return; }

        _isRecording = true;
        if (Application.Current is App app && !app.GlobalHotkeysSuspended)
        {
            app.SuspendGlobalHotkeys();
            _ownsHotkeySuspension = true;
        }
        TxtHotKey.Text = "按下组合键…";
        SetHotKeyTip("支持 Ctrl/Alt/Shift/Win 组合，或单按 F1–F12、PrtSc（Esc 取消）。");
        HotKeyDisplayBorder.Classes.Set("unbound", false);
        HotKeyDisplayBorder.Classes.Set("conflict", false);
        HotKeyDisplayBorder.Classes.Set("recording", true);
        BtnClearHotKey.IsVisible = false;

        Focus();
        int generation = ++_keyboardRecordingGeneration;
        try
        {
            nint handle = this.TryGetPlatformHandle()?.Handle ?? 0;
            if (handle == 0) throw new InvalidOperationException("录制窗口尚未就绪");
            _keyboardRecorder = new Services.KeyboardShortcutRecorder(handle,
                (modifiers, key) => Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _keyboardRecordingGeneration) return;
                    _keyboardRecorder = null;
                    if (key == 0) StopRecording(false);
                    else AcceptKeyboardShortcut(modifiers, key);
                }),
                () => Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _keyboardRecordingGeneration)
                        SetHotKeyTip("请使用带辅助键的组合，或单按 F1–F12、PrtSc。");
                }));
        }
        catch (Exception ex) { StopRecording(false); SetHotKeyTip("无法开始录制：" + ex.Message); }
    }

    private void OnHotKeyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && (_virtualKey != 0 || _isRecording))
        {
            OnClearHotKeyClick(sender, e);
            e.Handled = true;
        }
    }

    private void AcceptKeyboardShortcut(uint modifiers, uint key)
    {
        if (!ActionKeyboardBinding.IsValid(modifiers, key)) return;
        string? conflict = Services.BuiltinShortcutsStore.FindActionConflict(modifiers, key,
            _otherActions ?? Array.Empty<ActionDefinition>(), _originalAction, _otherShortcuts);
        if (conflict != null)
        {
            StopRecording(false);
            SetHotKeyTip(FormatHotKey(modifiers, key) + " 已被" + conflict + "占用，请重录。", true);
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
        bool bound = _virtualKey != 0;
        TxtHotKey.Text = bound ? FormatHotKey(_modifiers, _virtualKey) : "未绑定 · 点击录制";
        HotKeyDisplayBorder.Classes.Set("unbound", !bound && !_isRecording);
        BtnClearHotKey.IsVisible = bound && !_isRecording;

        string? conflict = bound
            ? Services.BuiltinShortcutsStore.FindActionConflict(_modifiers, _virtualKey,
                _otherActions ?? Array.Empty<ActionDefinition>(), _originalAction, _otherShortcuts)
            : null;
        HotKeyDisplayBorder.Classes.Set("conflict", conflict != null);
        UpdateTriggerSummary();
    }

    private void SetHotKeyTip(string text, bool conflict = false)
    {
        TxtHotKeyTip.Text = text;
        TxtHotKeyTip.IsVisible = !string.IsNullOrEmpty(text);
        TxtHotKeyTip.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(conflict ? "SctDangerBrush" : "SctTextMutedBrush"));
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prefixRecorder != null) { e.Handled = true; return; }
        if (e.Key == Key.Escape && (PipelineView.IsDragging || PalettePanel.IsDragging))
        {
            PalettePanel.CancelActiveDrag();
            PipelineView.CancelActiveDrag();
            ClearDragGhost();
            e.Handled = true;
            return;
        }
        if (!_isRecording) return;
        e.Handled = true;
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
            NativeMethods.VK_SNAPSHOT => "PrtSc",
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
