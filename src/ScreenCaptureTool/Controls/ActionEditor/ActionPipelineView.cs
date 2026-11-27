using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Core.Scripting;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Controls.ActionEditor;

/// <summary>
/// 极简规整的流程画布：
/// 整条卡片顶栏支持按住拖动整张模块卡片实时重排（兄弟卡片平滑让位），
/// 右侧保留极简复制/删除图标，输出名称独立位于「更多选项」下方，
/// 以 64px 统一标签列、2 列对称网格和极淡竖向时间轴呈现清晰流程。
/// </summary>
internal sealed class ActionPipelineView : Grid
{
    private const double LabelColWidth = 64;
    private const double ControlHeight = 30;
    private const double CardPadH = 16;
    private const double ConnectorHeight = 14;

    private readonly ScrollViewer _scroll;
    private readonly Grid _scrollContent;
    private readonly ContinuousBorder _landingSlotIndicator;
    private readonly StackPanel _cardsPanel;
    private readonly ListReorderBehavior _drag = new(threshold: 4);
    private readonly HashSet<int> _expandedAdvanced = [];
    private readonly List<StackPanel> _stepWrappers = [];
    private readonly List<Border?> _stepConnectors = [];
    private List<ActionStepNode> _steps = [];
    private bool _suppressUpdates;
    private Window? _hostWindow;
    private ContinuousBorder? _draggedCard;
    private bool _stepDragVisualActive;
    private int _dragFromIndex = -1;
    private int _dragTargetIndex = -1;
    private double _dragStartPanelY;
    private Point _lastScrollPoint;
    private double[] _slotTops = [];
    private double[] _cardHeights = [];
    private double[] _landingTops = [];
    private double _dragShift;
    private double _totalContentHeight;
    private DispatcherTimer? _longPressTimer;
    private DispatcherTimer? _autoScrollTimer;

    private ContinuousBorder? _externalDropPlaceholder;
    private int _externalInsertIndex = -1;

    public event Action<List<ActionStepNode>>? StepsChanged;
    public event Action<string>? QuickAddModuleRequested;
    public event Action? OpenTemplatesRequested;

    public IReadOnlyList<ActionStepNode> Steps => _steps;

    public ActionPipelineView()
    {
        _drag.Canceled += ClearDragVisuals;
        _landingSlotIndicator = new ContinuousBorder
        {
            Classes = { "actionLandingSlot" },
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ZIndex = 0,
        };
        _cardsPanel = new StackPanel
        {
            Spacing = 0,
            ClipToBounds = false,
            ZIndex = 1,
        };
        _scrollContent = new Grid
        {
            Margin = new Thickness(16, 14, 16, 16),
            ClipToBounds = false,
        };
        _scrollContent.Children.Add(_landingSlotIndicator);
        _scrollContent.Children.Add(_cardsPanel);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _scrollContent,
        };
        Children.Add(_scroll);
    }

    public void AttachHostWindow(Window window) => _hostWindow = window;

    public void CancelActiveDrag()
    {
        ClearExternalDrop();
        if (!_drag.IsActive) return;
        _drag.Reset();
        ClearDragVisuals();
    }

    public bool IsDragging => _drag.IsActive;

    public void SetSteps(IEnumerable<ActionStepNode> steps)
    {
        ClearExternalDrop();
        _steps = steps.ToList();
        _expandedAdvanced.Clear();
        RebuildCards();
    }

    public void AddModule(string moduleId, int? insertAt = null)
    {
        ClearExternalDrop();
        int pos = insertAt.HasValue ? Math.Clamp(insertAt.Value, 0, _steps.Count) : _steps.Count;
        var created = ActionModuleRegistry.CreateStep(moduleId, _steps, pos);
        _steps.Insert(pos, created);
        ShiftExpandedAfterInsert(pos);
        AutoReconnectMissingInputs();
        NotifyAndRebuild();
    }

    // ════════════════ 外部模块拖入定位与落位 ════════════════

    public void UpdateExternalDrop(Point windowPoint, string moduleName)
    {
        if (_hostWindow == null) return;
        if (_hostWindow.TranslatePoint(windowPoint, _scroll) is not Point inScroll
            || !new Rect(_scroll.Bounds.Size).Contains(inScroll))
        {
            ClearExternalDrop();
            return;
        }

        if (_hostWindow.TranslatePoint(windowPoint, _cardsPanel) is not Point inCards)
        {
            ClearExternalDrop();
            return;
        }

        if (_externalDropPlaceholder == null)
        {
            var label = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctWorkspaceBrandTextBrush"));
            _externalDropPlaceholder = new ContinuousBorder
            {
                Classes = { "actionModuleDropPlaceholder" },
                Height = 36,
                Margin = new Thickness(0, 6),
                Child = label,
            };
        }

        if (_externalDropPlaceholder.Child is TextBlock tb)
            tb.Text = $"松手将「{moduleName}」插入此处";

        if (_steps.Count == 0)
        {
            _externalInsertIndex = 0;
            if (!_cardsPanel.Children.Contains(_externalDropPlaceholder))
                _cardsPanel.Children.Insert(0, _externalDropPlaceholder);
            return;
        }

        _externalInsertIndex = ListReorderBehavior.GetInsertionIndex(_cardsPanel, inCards, Orientation.Vertical, _externalDropPlaceholder);
        ListReorderBehavior.MovePlaceholder(_cardsPanel, _externalDropPlaceholder, _externalInsertIndex);
    }

    public bool CommitExternalDrop(string moduleId, Point windowPoint, string moduleName)
    {
        UpdateExternalDrop(windowPoint, moduleName);
        int target = _externalInsertIndex;
        ClearExternalDrop();
        if (target < 0) return false;
        AddModule(moduleId, target);
        return true;
    }

    public void ClearExternalDrop()
    {
        _externalInsertIndex = -1;
        ListReorderBehavior.RemovePlaceholder(_externalDropPlaceholder);
        _externalDropPlaceholder = null;
    }

    // ════════════════ 内部状态同步 ════════════════

    private void ShiftExpandedAfterInsert(int index)
    {
        var shifted = _expandedAdvanced.Select(i => i >= index ? i + 1 : i).ToList();
        _expandedAdvanced.Clear();
        foreach (int i in shifted) _expandedAdvanced.Add(i);
    }

    private void NotifyAndRebuild()
    {
        RebuildCards();
        StepsChanged?.Invoke(_steps.ToList());
    }

    private void NotifyWithoutRebuild()
    {
        StepsChanged?.Invoke(_steps.ToList());
    }

    private void AutoReconnectMissingInputs()
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var desc = ActionModuleRegistry.Find(step.ModuleId);
            if (desc == null || !desc.NeedsInput) continue;
            var compatible = ActionModuleRegistry.CompatibleInputs(step, ActionModuleRegistry.ResultsBefore(_steps, i));
            if (string.IsNullOrWhiteSpace(step.InputName) && compatible.Count > 0)
                _steps[i] = step with { InputName = compatible[^1].Name };
        }
    }

    private void RebuildCards()
    {
        _suppressUpdates = true;
        _cardsPanel.Children.Clear();
        _stepWrappers.Clear();
        _stepConnectors.Clear();

        if (_steps.Count == 0)
        {
            _cardsPanel.Children.Add(BuildEmptyState());
            _suppressUpdates = false;
            return;
        }

        var diagnostics = ActionModuleRegistry.ValidateSteps(_steps)
            .GroupBy(d => d.StepIndex)
            .ToDictionary(g => g.Key, g => g.First());

        for (int i = 0; i < _steps.Count; i++)
        {
            int idx = i;
            var step = _steps[i];
            diagnostics.TryGetValue(idx, out var diag);
            _cardsPanel.Children.Add(BuildStepWrapper(idx, step, diag, isLast: idx == _steps.Count - 1));
        }
        _suppressUpdates = false;
    }

    private Control BuildEmptyState()
    {
        var box = new ContinuousBorder
        {
            Classes = { "actionEmptyCard" },
            Padding = new Thickness(24, 32),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var sp = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = "点击或拖入左侧模块开始编排",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var hint = new TextBlock
        {
            Text = "步骤按从上到下的顺序执行，前一步产出的图片或文字可直接供后续步骤使用。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360,
        };
        hint.Classes.Add("muted");
        sp.Children.Add(hint);

        var quickRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        quickRow.Children.Add(MakeQuickButton("区域截图", () => QuickAddModuleRequested?.Invoke("capture.region")));
        quickRow.Children.Add(MakeQuickButton("剪贴板图片", () => QuickAddModuleRequested?.Invoke("input.clipboard.image")));
        quickRow.Children.Add(MakeQuickButton("从模板开始", () => OpenTemplatesRequested?.Invoke()));
        sp.Children.Add(quickRow);

        box.Child = sp;
        return box;
    }

    private static Button MakeQuickButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Classes = { "secondary" },
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(12, 0),
            FontSize = 12.5,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private Control BuildStepWrapper(int position, ActionStepNode step, WorkflowStepDiagnostic? diagnostic, bool isLast)
    {
        var wrapper = new StackPanel
        {
            Classes = { "actionStepWrapper" },
            Spacing = 0,
            ZIndex = 0,
            ClipToBounds = false,
        };
        wrapper.Children.Add(BuildStepCard(position, step, diagnostic, wrapper));
        Border? connector = null;
        if (!isLast)
        {
            connector = BuildConnector();
            wrapper.Children.Add(connector);
        }
        _stepWrappers.Add(wrapper);
        _stepConnectors.Add(connector);
        return wrapper;
    }

    /// <summary>安静克制的细竖线步骤连接器（去除杂乱箭头）。</summary>
    private static Border BuildConnector()
    {
        var line = new Border
        {
            Width = 1.5,
            Height = ConnectorHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.75,
        };
        line.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctCardBorderBrush"));
        return line;
    }

    private Control BuildStepCard(int position, ActionStepNode step, WorkflowStepDiagnostic? diagnostic, StackPanel wrapper)
    {
        var desc = ActionModuleRegistry.GetRequired(step.ModuleId);
        var card = new ContinuousBorder { Classes = { "actionStepCard" } };
        if (diagnostic != null) card.Classes.Add("hasError");

        var mainStack = new StackPanel { Spacing = 0 };

        // ════ 1. 极简卡片头部（整条顶栏按住可拖动整块排序 | 右侧极简复制与删除图标）════
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var dragZone = new Border
        {
            Background = Brushes.Transparent,
            Cursor = _steps.Count > 1 ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Padding = new Thickness(CardPadH, 10, 8, 10),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTip.SetTip(dragZone, _steps.Count > 1
            ? "按住拖动可调整步骤顺序\n" + desc.ShortDescription
            : desc.ShortDescription);

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var numText = new TextBlock
        {
            Text = (position + 1).ToString("D2", CultureInfo.InvariantCulture),
            FontFamily = new FontFamily("Consolas, 'Cascadia Code', monospace"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.55,
        };
        numText.Classes.Add("muted");
        titleStack.Children.Add(numText);

        titleStack.Children.Add(new TextBlock
        {
            Text = desc.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        dragZone.Child = titleStack;
        AttachDragHandlers(card, position);
        headerGrid.Children.Add(dragZone);

        var ops = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, CardPadH - 6, 0),
        };

        var dupBtn = new Button
        {
            Classes = { "actionCardIconBtn" },
            Content = ActionModuleIconHelper.CreateVectorStrokeIcon(OcrIconGeometry.CopyOutline, size: 13.5, thickness: 1.45),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(dupBtn, "复制步骤");
        AutomationProperties.SetName(dupBtn, "复制步骤");
        dupBtn.Click += (_, _) => DuplicateStep(position);
        ops.Children.Add(dupBtn);

        var delBtn = new Button
        {
            Classes = { "actionCardIconBtn", "danger" },
            Content = ActionModuleIconHelper.CreateVectorStrokeIcon(OcrIconGeometry.TrashStroke, size: 13.5, thickness: 1.45),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(delBtn, "删除步骤");
        AutomationProperties.SetName(delBtn, "删除步骤");
        delBtn.Click += (_, _) => RemoveStep(position);
        ops.Children.Add(delBtn);

        Grid.SetColumn(ops, 1);
        headerGrid.Children.Add(ops);
        mainStack.Children.Add(headerGrid);

        // ════ 2. 核心参数区（统一左右 16px，标签固定 64px）════
        var coreParams = desc.Parameters
            .Where(p => !p.IsAdvanced && ActionModuleDescriptor.IsParamVisible(p, step))
            .ToList();
        var advParams = desc.Parameters
            .Where(p => p.IsAdvanced && ActionModuleDescriptor.IsParamVisible(p, step))
            .ToList();
        bool hasAdvancedSection = advParams.Count > 0;

        bool hasCore = desc.NeedsInput || coreParams.Count > 0;
        if (hasCore)
        {
            mainStack.Children.Add(BuildHairlineDivider());

            var coreWrap = new Border { Padding = new Thickness(CardPadH, 10, CardPadH, (hasAdvancedSection || desc.ProducesResult) ? 10 : 12) };
            var coreGrid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelColWidth},*") };
            int rowIdx = 0;

            if (desc.NeedsInput)
                AddAlignedRow(coreGrid, rowIdx++, "输入内容", BuildInputSelector(position, step, desc));

            foreach (var param in coreParams)
                AddAlignedRow(coreGrid, rowIdx++, param.Label, BuildParamEditor(position, step, param, uniformDropdown: false));

            coreWrap.Child = coreGrid;
            mainStack.Children.Add(coreWrap);
        }

        // ════ 3. 进阶选项区（扁平全宽分段 + 2 列对称网格）════
        if (hasAdvancedSection)
        {
            mainStack.Children.Add(BuildHairlineDivider());
            mainStack.Children.Add(BuildAdvancedSection(position, step, desc, advParams));
        }

        // ════ 4. 输出结果区（位于「更多选项」下方，紧邻流出连线，与上方 64px 标签列严格对齐）════
        if (desc.ProducesResult)
        {
            mainStack.Children.Add(BuildHairlineDivider());
            mainStack.Children.Add(BuildOutputFooterSection(position, step, desc));
        }

        // ════ 5. 卡片级即时诊断条 ════
        if (diagnostic != null)
        {
            mainStack.Children.Add(BuildDiagnosticBar(position, diagnostic));
        }

        card.Child = mainStack;
        return card;
    }

    private static Border BuildHairlineDivider()
    {
        var sep = new Border { Height = 1, Opacity = 0.7 };
        sep.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctSoftBorderBrush"));
        return sep;
    }

    private Control BuildOutputFooterSection(int position, ActionStepNode step, ActionModuleDescriptor desc)
    {
        var wrap = new Border { Padding = new Thickness(CardPadH, 9, CardPadH, 10) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelColWidth},*") };
        AddAlignedRow(grid, 0, "输出名称", BuildResultNameEditor(position, step, desc));
        wrap.Child = grid;
        return wrap;
    }

    private Control BuildAdvancedSection(int position, ActionStepNode step, ActionModuleDescriptor desc, List<ActionModuleParamSpec> advParams)
    {
        bool expanded = _expandedAdvanced.Contains(position);
        var advBadges = desc.GetAdvancedBadges(step);

        var sectionStack = new StackPanel { Spacing = 0 };

        // ── 进阶区整行可点击展开栏（同卡统一表面 + 清晰文字层级 + 右侧展开/收起指令与可选还原默认）──
        var headerBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var innerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var titleText = new TextBlock
        {
            Text = "更多选项",
            FontSize = 12.5,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleText.Classes.Add("secondary");
        innerRow.Children.Add(titleText);

        if (!expanded && advBadges.Count > 0)
        {
            var summary = new TextBlock
            {
                Text = string.Join(" · ", advBadges),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 8, 0),
            };
            summary.Classes.Add("muted");
            Grid.SetColumn(summary, 1);
            innerRow.Children.Add(summary);
        }

        var actionLabel = new TextBlock
        {
            Text = expanded ? "收起" : "展开",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionLabel.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctWorkspaceBrandTextBrush"));
        Grid.SetColumn(actionLabel, 2);
        innerRow.Children.Add(actionLabel);

        var toggleRowBtn = new Button
        {
            Classes = { "actionAdvRowBtn" },
            Padding = new Thickness(CardPadH, 0),
            Content = innerRow,
        };
        toggleRowBtn.Click += (_, _) =>
        {
            if (!_expandedAdvanced.Add(position)) _expandedAdvanced.Remove(position);
            RebuildCards();
        };
        headerBar.Children.Add(toggleRowBtn);

        if (advBadges.Count > 0)
        {
            var resetBtn = new Button
            {
                Classes = { "actionSubtleLink" },
                Content = "还原默认",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(6, 0),
                FontSize = 11.5,
                Margin = new Thickness(0, 0, CardPadH - 4, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
            };
            resetBtn.Click += (_, _) =>
            {
                var updated = _steps[position];
                foreach (var p in advParams) updated = updated.WithParam(p.Key, p.DefaultValue);
                _steps[position] = updated;
                NotifyAndRebuild();
            };
            Grid.SetColumn(resetBtn, 1);
            headerBar.Children.Add(resetBtn);
        }

        sectionStack.Children.Add(headerBar);

        if (expanded)
        {
            var twoColGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{LabelColWidth},*,16,{LabelColWidth},*"),
            };

            int gridRow = 0;
            int slotInRow = 0;

            void EnsureRow(int r)
            {
                while (twoColGrid.RowDefinitions.Count <= r)
                    twoColGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            foreach (var param in advParams)
            {
                bool fullSpan = param.Kind is ActionParamKind.Directory or ActionParamKind.Text or ActionParamKind.Region;
                if (fullSpan && slotInRow == 1)
                {
                    gridRow++;
                    slotInRow = 0;
                }

                EnsureRow(gridRow);
                double topPad = gridRow > 0 ? 8 : 0;

                var editor = BuildParamEditor(position, step, param, uniformDropdown: true);
                var label = new TextBlock
                {
                    Text = param.Label,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, topPad, 8, 0),
                };
                label.Classes.Add("secondary");

                if (topPad > 0)
                    editor.Margin = new Thickness(editor.Margin.Left, editor.Margin.Top + topPad, editor.Margin.Right, editor.Margin.Bottom);

                if (fullSpan)
                {
                    Grid.SetRow(label, gridRow);
                    Grid.SetColumn(label, 0);
                    Grid.SetRow(editor, gridRow);
                    Grid.SetColumn(editor, 1);
                    Grid.SetColumnSpan(editor, 4);
                    twoColGrid.Children.Add(label);
                    twoColGrid.Children.Add(editor);
                    gridRow++;
                    slotInRow = 0;
                }
                else
                {
                    int colBase = slotInRow == 0 ? 0 : 3;
                    Grid.SetRow(label, gridRow);
                    Grid.SetColumn(label, colBase);
                    Grid.SetRow(editor, gridRow);
                    Grid.SetColumn(editor, colBase + 1);
                    twoColGrid.Children.Add(label);
                    twoColGrid.Children.Add(editor);

                    if (slotInRow == 0) slotInRow = 1;
                    else { slotInRow = 0; gridRow++; }
                }
            }

            sectionStack.Children.Add(new Border
            {
                Padding = new Thickness(CardPadH, 4, CardPadH, 12),
                Child = twoColGrid,
            });
        }

        return sectionStack;
    }

    /// <summary>位于「更多选项」下方的输出变量名编辑框（回车或失焦提交，并自动同步后续引用）。</summary>
    private Control BuildResultNameEditor(int position, ActionStepNode step, ActionModuleDescriptor desc)
    {
        var outKind = desc.OutputKind(step);
        string kindText = outKind == ActionContentKind.Image ? "图片" : "文字";
        string sample = desc.DefaultResultLabel ?? desc.Name;

        var editBox = new TextBox
        {
            Text = step.ResultName ?? "",
            FontSize = 12.5,
            Height = ControlHeight,
            MinHeight = ControlHeight,
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Watermark = $"输出{kindText}名称（如：{sample}），供后续步骤引用",
        };
        AutomationProperties.SetName(editBox, "结果名称");
        ToolTip.SetTip(editBox, $"本步骤产出的{kindText}变量名；修改后后续步骤中的引用会自动同步");

        void CommitRename()
        {
            if (_suppressUpdates) return;
            string oldName = _steps[position].ResultName ?? "";
            string newName = (editBox.Text ?? "").Trim();
            if (newName == oldName) return;
            _steps[position] = _steps[position] with { ResultName = newName };
            if (!string.IsNullOrEmpty(oldName) && ScriptExecutor.IsResultName(newName)
                && !_steps.Where((_, i) => i != position).Any(s => string.Equals(s.ResultName, newName, StringComparison.Ordinal)))
            {
                for (int j = position + 1; j < _steps.Count; j++)
                    if (string.Equals(_steps[j].InputName, oldName, StringComparison.Ordinal))
                        _steps[j] = _steps[j] with { InputName = newName };
            }
            NotifyWithoutRebuild();
            Dispatcher.UIThread.Post(() =>
            {
                if (!_suppressUpdates) RebuildCards();
            });
        }

        editBox.LostFocus += (_, _) => CommitRename();
        editBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitRename(); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                editBox.Text = _steps[position].ResultName ?? "";
            }
        };
        return editBox;
    }

    private Control BuildInputSelector(int position, ActionStepNode step, ActionModuleDescriptor desc)
    {
        var prior = ActionModuleRegistry.ResultsBefore(_steps, position);
        var compatible = ActionModuleRegistry.CompatibleInputs(step, prior);
        var (acceptImg, acceptTxt) = desc.AcceptedInputs(step);
        string needText = (acceptImg, acceptTxt) switch { (true, false) => "图片", (false, true) => "文字", _ => "图片或文字" };

        var picker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = ControlHeight,
            MinHeight = ControlHeight,
            FontSize = 12.5,
            Padding = new Thickness(10, 0, 8, 0),
            PlaceholderText = compatible.Count > 0 ? $"选择前置{needText}结果" : $"暂无可用{needText}结果",
        };
        AutomationProperties.SetName(picker, "输入结果");

        foreach (var item in compatible)
            picker.Items.Add(new ComboBoxItem { Content = item.DisplayLabel, Tag = item.Name });
        picker.IsEnabled = compatible.Count > 0;

        var selected = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(it => string.Equals(it.Tag?.ToString(), step.InputName, StringComparison.Ordinal));
        if (selected == null && !string.IsNullOrEmpty(step.InputName))
        {
            selected = new ComboBoxItem { Content = $"{step.InputName}（不可用）", Tag = step.InputName, IsEnabled = false };
            picker.Items.Add(selected);
        }
        picker.SelectedItem = selected;

        picker.SelectionChanged += (_, _) =>
        {
            if (_suppressUpdates || picker.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            _steps[position] = _steps[position] with { InputName = name };
            NotifyAndRebuild();
        };
        return picker;
    }

    private Control BuildParamEditor(int position, ActionStepNode step, ActionModuleParamSpec param, bool uniformDropdown)
    {
        if (param.Kind == ActionParamKind.Choice && param.Choices != null)
        {
            string current = step.GetParam(param.Key, param.DefaultValue);
            bool hasCustomOutsideChoices = !param.Choices.Any(c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase));

            if (!uniformDropdown && param.Choices.Length <= 3 && !hasCustomOutsideChoices)
            {
                var group = new ContinuousBorder
                {
                    Classes = { "segmentedGroup" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = ControlHeight,
                    MinHeight = ControlHeight,
                    MaxHeight = ControlHeight,
                };
                var eqGrid = new Grid();
                for (int c = 0; c < param.Choices.Length; c++)
                    eqGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

                string groupName = $"StepParam_{position}_{param.Key}_{Guid.NewGuid():N}";
                for (int c = 0; c < param.Choices.Length; c++)
                {
                    var choice = param.Choices[c];
                    var rad = new RadioButton
                    {
                        GroupName = groupName,
                        Classes = { "segmented" },
                        Content = choice.Label,
                        IsChecked = string.Equals(current, choice.Value, StringComparison.OrdinalIgnoreCase),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        FontSize = 12,
                        MinHeight = 26,
                        Padding = new Thickness(8, 2),
                    };
                    if (!string.IsNullOrEmpty(param.Tooltip)) ToolTip.SetTip(rad, param.Tooltip);
                    string chosenValue = choice.Value;
                    rad.IsCheckedChanged += (_, _) =>
                    {
                        if (_suppressUpdates || rad.IsChecked != true) return;
                        _steps[position] = _steps[position].WithParam(param.Key, chosenValue);
                        NotifyAndRebuild();
                    };
                    Grid.SetColumn(rad, c);
                    eqGrid.Children.Add(rad);
                }
                group.Child = eqGrid;
                return group;
            }

            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = ControlHeight,
                MinHeight = ControlHeight,
                FontSize = 12.5,
                Padding = new Thickness(10, 0, 8, 0),
            };
            if (!string.IsNullOrEmpty(param.Tooltip)) ToolTip.SetTip(combo, param.Tooltip);
            foreach (var choice in param.Choices)
                combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
            if (hasCustomOutsideChoices && !string.IsNullOrEmpty(current))
                combo.Items.Add(new ComboBoxItem { Content = $"自定义 ({current})", Tag = current });
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(it => string.Equals(it.Tag?.ToString(), current, StringComparison.OrdinalIgnoreCase))
                ?? combo.Items[0];
            combo.SelectionChanged += (_, _) =>
            {
                if (_suppressUpdates || combo.SelectedItem is not ComboBoxItem { Tag: string v }) return;
                _steps[position] = _steps[position].WithParam(param.Key, v);
                NotifyAndRebuild();
            };
            return combo;
        }

        if (param.Kind is ActionParamKind.Int or ActionParamKind.NullableInt)
        {
            bool allowEmpty = param.Kind == ActionParamKind.NullableInt;
            string current = step.GetParam(param.Key, param.DefaultValue);
            int stepStart = param.Key == "opacity" ? 80 : 0;

            var numBox = new ActionNumberInput(
                initialValue: current,
                min: param.Min,
                max: param.Max,
                step: param.Step,
                unit: param.Unit,
                watermark: param.Watermark,
                allowEmpty: allowEmpty,
                emptyToken: param.DefaultValue,
                defaultWhenSteppedFromEmpty: stepStart,
                tooltip: param.Tooltip);

            numBox.ValueCommitted += val =>
            {
                if (_suppressUpdates) return;
                _steps[position] = _steps[position].WithParam(param.Key, val);
                NotifyWithoutRebuild();
            };

            if (param.QuickPresets == null || param.QuickPresets.Length == 0)
            {
                numBox.HorizontalAlignment = HorizontalAlignment.Stretch;
                return numBox;
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            numBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Children.Add(numBox);

            var presets = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (int presetVal in param.QuickPresets)
            {
                int target = presetVal;
                string label = target >= 1000 && target % 1000 == 0 ? $"{target / 1000} 秒" : $"{target} ms";
                var chip = new Button
                {
                    Content = label,
                    Classes = { "secondary" },
                    Height = ControlHeight,
                    MinHeight = ControlHeight,
                    Padding = new Thickness(10, 0),
                    FontSize = 12,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                chip.Click += (_, _) => numBox.SetNumericValue(target);
                presets.Children.Add(chip);
            }
            Grid.SetColumn(presets, 1);
            row.Children.Add(presets);
            return row;
        }

        if (param.Kind == ActionParamKind.Text)
        {
            var box = new TextBox
            {
                Text = step.GetParam(param.Key, param.DefaultValue),
                Watermark = param.Watermark ?? "",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 56,
                MaxHeight = 110,
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(10, 6),
            };
            if (!string.IsNullOrEmpty(param.Tooltip)) ToolTip.SetTip(box, param.Tooltip);
            box.TextChanged += (_, _) =>
            {
                if (_suppressUpdates) return;
                _steps[position] = _steps[position].WithParam(param.Key, box.Text ?? "");
                NotifyWithoutRebuild();
            };
            return box;
        }

        if (param.Kind == ActionParamKind.Directory)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var box = new TextBox
            {
                Text = step.GetParam(param.Key, param.DefaultValue),
                Watermark = param.Watermark ?? "留空则使用全局默认目录",
                Height = ControlHeight,
                MinHeight = ControlHeight,
                FontSize = 12.5,
                Padding = new Thickness(10, 4),
            };
            if (!string.IsNullOrEmpty(param.Tooltip)) ToolTip.SetTip(box, param.Tooltip);
            box.TextChanged += (_, _) =>
            {
                if (_suppressUpdates) return;
                _steps[position] = _steps[position].WithParam(param.Key, (box.Text ?? "").Trim());
                NotifyWithoutRebuild();
            };
            g.Children.Add(box);

            var browse = new Button
            {
                Content = "浏览",
                Classes = { "secondary" },
                Height = ControlHeight,
                MinHeight = ControlHeight,
                Padding = new Thickness(12, 0),
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            browse.Click += async (_, _) =>
            {
                if (_hostWindow == null) return;
                var picked = await _hostWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择此动作的专用保存目录",
                    AllowMultiple = false,
                });
                string? folder = picked.FirstOrDefault()?.TryGetLocalPath();
                if (!string.IsNullOrEmpty(folder))
                {
                    box.Text = folder;
                    _steps[position] = _steps[position].WithParam(param.Key, folder);
                    NotifyAndRebuild();
                }
            };
            Grid.SetColumn(browse, 1);
            g.Children.Add(browse);
            return g;
        }

        // ActionParamKind.Region：单框逗号分隔 + 简洁格式说明 + 框选拾取
        var (rx, ry, rw, rh) = ActionModuleRegistry.ParseRegion(step.GetParam(param.Key, param.DefaultValue));
        var regStack = new StackPanel { Spacing = 4 };
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var regionBox = new TextBox
        {
            Text = ActionModuleRegistry.FormatRegion(rx, ry, rw, rh),
            Watermark = "X, Y, 宽, 高（如 0, 0, 800, 600）",
            Height = ControlHeight,
            MinHeight = ControlHeight,
            FontSize = 12.5,
            Padding = new Thickness(10, 4),
        };
        AutomationProperties.SetName(regionBox, "屏幕坐标，逗号分隔");
        if (!string.IsNullOrEmpty(param.Tooltip)) ToolTip.SetTip(regionBox, param.Tooltip);

        regionBox.TextChanged += (_, _) =>
        {
            if (_suppressUpdates) return;
            string raw = (regionBox.Text ?? "").Replace('，', ',');
            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0
                && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h > 0)
            {
                _steps[position] = _steps[position].WithParam(param.Key, ActionModuleRegistry.FormatRegion(x, y, w, h));
                NotifyWithoutRebuild();
            }
        };
        regionBox.LostFocus += (_, _) =>
        {
            var (fx, fy, fw, fh) = ActionModuleRegistry.ParseRegion(regionBox.Text);
            string normalized = ActionModuleRegistry.FormatRegion(fx, fy, fw, fh);
            _suppressUpdates = true;
            regionBox.Text = normalized;
            _suppressUpdates = false;
            _steps[position] = _steps[position].WithParam(param.Key, normalized);
            NotifyWithoutRebuild();
        };
        topRow.Children.Add(regionBox);

        var pickBtn = new Button
        {
            Content = "框选拾取",
            Classes = { "secondary" },
            Height = ControlHeight,
            MinHeight = ControlHeight,
            Padding = new Thickness(12, 0),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(pickBtn, "在屏幕上直接拖框拾取像素坐标，自动回填");
        pickBtn.Click += async (_, _) =>
        {
            if (Application.Current is not App app) return;
            pickBtn.IsEnabled = false;
            System.Drawing.Rectangle? pickedRect = null;
            try
            {
                await app.RunCaptureAsync("Workflow", null, (bmp, region) =>
                {
                    pickedRect = region;
                    bmp.Dispose();
                }, suppressCaptureStroke: true,
                captureOverrides: new ActionCaptureOptions(CornerRadiusOverride: 0, StrokeWidthOverride: 0, ShadowOverride: false));
            }
            finally
            {
                pickBtn.IsEnabled = true;
            }
            if (pickedRect is { Width: > 0, Height: > 0 } r)
            {
                _steps[position] = _steps[position].WithParam(param.Key, ActionModuleRegistry.FormatRegion(r.X, r.Y, r.Width, r.Height));
                NotifyAndRebuild();
            }
        };
        Grid.SetColumn(pickBtn, 1);
        topRow.Children.Add(pickBtn);
        regStack.Children.Add(topRow);

        regStack.Children.Add(new TextBlock
        {
            Text = "格式：X, Y, 宽, 高（屏幕像素坐标，用逗号分隔）",
            FontSize = 11,
            Classes = { "muted" },
        });
        return regStack;
    }

    private static void AddAlignedRow(Grid grid, int rowIndex, string labelText, Control editor)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        double topMargin = rowIndex > 0 ? 8 : 0;
        var label = new TextBlock
        {
            Text = labelText,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, topMargin, 8, 0),
        };
        label.Classes.Add("secondary");
        if (topMargin > 0)
            editor.Margin = new Thickness(editor.Margin.Left, editor.Margin.Top + topMargin, editor.Margin.Right, editor.Margin.Bottom);
        Grid.SetRow(label, rowIndex);
        Grid.SetRow(editor, rowIndex);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(label);
        grid.Children.Add(editor);
    }

    private Control BuildDiagnosticBar(int position, WorkflowStepDiagnostic diagnostic)
    {
        var bar = new Border
        {
            Padding = new Thickness(CardPadH, 8),
            Background = new SolidColorBrush(Color.FromArgb(18, 220, 38, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(48, 220, 38, 38)),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var msg = new TextBlock
        {
            Text = diagnostic.Message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        msg.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctDangerBrush"));
        g.Children.Add(msg);

        if (diagnostic.FixKind != DiagnosticFixKind.None && !string.IsNullOrEmpty(diagnostic.FixLabel))
        {
            var fixBtn = new Button
            {
                Content = diagnostic.FixLabel,
                Classes = { "secondary" },
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            fixBtn.Click += (_, _) => ApplyQuickFix(position, diagnostic);
            Grid.SetColumn(fixBtn, 1);
            g.Children.Add(fixBtn);
        }
        bar.Child = g;
        return bar;
    }

    private void ApplyQuickFix(int position, WorkflowStepDiagnostic diagnostic)
    {
        switch (diagnostic.FixKind)
        {
            case DiagnosticFixKind.InsertCaptureRegion:
                AddModule("capture.region", position);
                break;
            case DiagnosticFixKind.InsertClipboardText:
                AddModule("input.clipboard.text", position);
                break;
            case DiagnosticFixKind.SelectAvailableInput when !string.IsNullOrEmpty(diagnostic.SuggestedValue):
                _steps[position] = _steps[position] with { InputName = diagnostic.SuggestedValue };
                NotifyAndRebuild();
                break;
            case DiagnosticFixKind.FixDuplicateResultName when !string.IsNullOrEmpty(diagnostic.SuggestedValue):
                _steps[position] = _steps[position] with { ResultName = diagnostic.SuggestedValue };
                NotifyAndRebuild();
                break;
        }
    }

    private void MoveStepToIndex(int from, int dest)
    {
        if (dest == from || dest < 0 || dest >= _steps.Count) return;
        var expandedNodes = _expandedAdvanced.Where(i => i >= 0 && i < _steps.Count).Select(i => _steps[i]).ToList();
        var item = _steps[from];
        _steps.RemoveAt(from);
        _steps.Insert(dest, item);
        _expandedAdvanced.Clear();
        foreach (var node in expandedNodes)
        {
            int idx = _steps.FindIndex(s => ReferenceEquals(s, node));
            if (idx >= 0) _expandedAdvanced.Add(idx);
        }
        NotifyAndRebuild();
    }

    private void DuplicateStep(int position)
    {
        var orig = _steps[position];
        var desc = ActionModuleRegistry.GetRequired(orig.ModuleId);
        string? newResult = desc.ProducesResult
            ? ActionModuleRegistry.UniqueResultName(desc.DefaultResultLabel ?? desc.Name, _steps)
            : null;
        var copy = orig with
        {
            ResultName = newResult,
            Params = orig.Params != null ? new Dictionary<string, string>(orig.Params, StringComparer.OrdinalIgnoreCase) : null
        };
        _steps.Insert(position + 1, copy);
        ShiftExpandedAfterInsert(position + 1);
        NotifyAndRebuild();
    }

    private void RemoveStep(int position)
    {
        if (position < 0 || position >= _steps.Count) return;
        _steps.RemoveAt(position);
        var updated = _expandedAdvanced.Where(i => i != position).Select(i => i > position ? i - 1 : i).ToList();
        _expandedAdvanced.Clear();
        foreach (int i in updated) _expandedAdvanced.Add(i);
        NotifyAndRebuild();
    }

    private static bool IsInsideInteractiveControl(Visual? source, Visual stopAt)
    {
        for (Visual? cur = source; cur != null && !ReferenceEquals(cur, stopAt); cur = cur.GetVisualParent())
        {
            if (cur is TextBox or ComboBox or Button or RadioButton or ToggleButton or ActionNumberInput or ScrollBar)
                return true;
        }
        return false;
    }

    private void AttachDragHandlers(ContinuousBorder card, int position)
    {
        card.PointerPressed += (_, e) =>
        {
            if (IsInsideInteractiveControl(e.Source as Visual, card)) return;
            if (_steps.Count <= 1 || _hostWindow == null || !_drag.Begin(card, e, _hostWindow)) return;
            _dragFromIndex = position;
            _dragTargetIndex = position;
            _draggedCard = card;
            _dragStartPanelY = e.GetPosition(_cardsPanel).Y;
            _lastScrollPoint = e.GetPosition(_scroll);
            StartLongPressTimer();
        };
        card.PointerMoved += (_, e) =>
        {
            if (!_drag.IsActive || _hostWindow == null) return;
            _lastScrollPoint = e.GetPosition(_scroll);

            if (_stepDragVisualActive)
            {
                UpdateWholeCardDrag();
                e.Handled = true;
                return;
            }

            if (!_drag.Update(e)) return;
            StopLongPressTimer();
            ActivateStepDragVisual();
            UpdateWholeCardDrag();
        };
        card.PointerReleased += (_, e) =>
        {
            if (!_drag.IsActive) return;
            StopLongPressTimer();
            bool dragging = _stepDragVisualActive || _drag.IsDragging;
            if (dragging) UpdateWholeCardDrag();
            int from = _dragFromIndex;
            int target = _dragTargetIndex;
            _drag.Reset();
            ClearDragVisuals();
            e.Handled = true;
            if (dragging && from >= 0 && target >= 0 && target != from)
                MoveStepToIndex(from, target);
        };
    }

    private void StartLongPressTimer()
    {
        StopLongPressTimer();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _longPressTimer.Tick += (_, _) =>
        {
            StopLongPressTimer();
            if (!_drag.IsActive || _stepDragVisualActive) return;
            ActivateStepDragVisual();
            UpdateWholeCardDrag();
        };
        _longPressTimer.Start();
    }

    private void StopLongPressTimer()
    {
        if (_longPressTimer == null) return;
        _longPressTimer.Stop();
        _longPressTimer = null;
    }

    private void StartAutoScrollTimer()
    {
        StopAutoScrollTimer();
        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += (_, _) =>
        {
            if (!_stepDragVisualActive) return;
            const double edgeZone = 36;
            const double stepPx = 10;
            double viewH = _scroll.Bounds.Height;
            if (viewH <= 0) return;

            double offset = _scroll.Offset.Y;
            double maxOffset = _scroll.ScrollBarMaximum.Y;
            double newOffset = offset;

            if (_lastScrollPoint.Y < edgeZone && offset > 0)
                newOffset = Math.Max(0, offset - stepPx);
            else if (_lastScrollPoint.Y > viewH - edgeZone && offset < maxOffset)
                newOffset = Math.Min(maxOffset, offset + stepPx);

            if (Math.Abs(newOffset - offset) > 0.1)
            {
                _scroll.Offset = new Vector(_scroll.Offset.X, newOffset);
                UpdateWholeCardDrag();
            }
        };
        _autoScrollTimer.Start();
    }

    private void StopAutoScrollTimer()
    {
        if (_autoScrollTimer == null) return;
        _autoScrollTimer.Stop();
        _autoScrollTimer = null;
    }

    private void ActivateStepDragVisual()
    {
        if (_stepDragVisualActive || _dragFromIndex < 0 || _dragFromIndex >= _stepWrappers.Count) return;

        int count = _stepWrappers.Count;
        _slotTops = new double[count];
        _cardHeights = new double[count];

        for (int i = 0; i < count; i++)
        {
            var wrapper = _stepWrappers[i];
            double top = wrapper.Bounds.Top;
            double cardH = wrapper.Children.FirstOrDefault()?.Bounds.Height ?? wrapper.Bounds.Height;
            _slotTops[i] = top;
            _cardHeights[i] = cardH;
        }

        _landingTops = new double[count];
        double acc = 0;
        int k = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == _dragFromIndex) continue;
            _landingTops[k++] = acc;
            acc += _cardHeights[i] + ConnectorHeight;
        }
        _landingTops[count - 1] = acc;

        _dragShift = _cardHeights[_dragFromIndex] + ConnectorHeight;
        _totalContentHeight = _cardsPanel.Bounds.Height;
        _stepDragVisualActive = true;

        var draggedWrapper = _stepWrappers[_dragFromIndex];
        draggedWrapper.Transitions = null;
        draggedWrapper.Classes.Add("dragging");
        draggedWrapper.ZIndex = 100;
        _draggedCard?.Classes.Add("dragging");

        for (int i = 0; i < _stepConnectors.Count; i++)
        {
            if (_stepConnectors[i] is { } conn)
                conn.Opacity = i == _dragFromIndex ? 0.0 : 0.2;
        }

        _landingSlotIndicator.Height = _cardHeights[_dragFromIndex];
        _landingSlotIndicator.RenderTransform = MakeTranslateY(_slotTops[_dragFromIndex]);
        _landingSlotIndicator.IsVisible = true;

        StartAutoScrollTimer();
    }

    private void UpdateWholeCardDrag()
    {
        if (!_stepDragVisualActive || _dragFromIndex < 0 || _dragFromIndex >= _stepWrappers.Count) return;

        double currentPanelY = _lastScrollPoint.Y + _scroll.Offset.Y - _scrollContent.Margin.Top;
        double rawDelta = currentPanelY - _dragStartPanelY;
        double minDelta = -_slotTops[_dragFromIndex] - 24;
        double maxDelta = Math.Max(0, _totalContentHeight - (_slotTops[_dragFromIndex] + _cardHeights[_dragFromIndex])) + 24;
        double deltaY = Math.Clamp(rawDelta, minDelta, maxDelta);

        _stepWrappers[_dragFromIndex].RenderTransform = MakeTranslateY(deltaY);

        double dragTop = _slotTops[_dragFromIndex] + deltaY;
        int target = _dragFromIndex;
        double bestDist = Math.Abs(dragTop - _landingTops[_dragFromIndex]);
        for (int idx = 0; idx < _landingTops.Length; idx++)
        {
            double dist = Math.Abs(dragTop - _landingTops[idx]);
            if (dist < bestDist)
            {
                bestDist = dist;
                target = idx;
            }
        }

        if (target != _dragTargetIndex)
        {
            _dragTargetIndex = target;
            for (int j = 0; j < _stepWrappers.Count; j++)
            {
                if (j == _dragFromIndex) continue;
                double shift = 0;
                if (target > _dragFromIndex && j > _dragFromIndex && j <= target)
                    shift = -_dragShift;
                else if (target < _dragFromIndex && j >= target && j < _dragFromIndex)
                    shift = _dragShift;
                _stepWrappers[j].RenderTransform = MakeTranslateY(shift);
            }

            _landingSlotIndicator.RenderTransform = MakeTranslateY(_landingTops[target]);
        }
    }

    private static ITransform MakeTranslateY(double y) =>
        TransformOperations.Parse($"translate(0px, {y.ToString("0.##", CultureInfo.InvariantCulture)}px)");

    private void ClearDragVisuals()
    {
        StopLongPressTimer();
        StopAutoScrollTimer();
        _landingSlotIndicator.IsVisible = false;

        var identity = MakeTranslateY(0);
        for (int i = 0; i < _stepWrappers.Count; i++)
        {
            var wrapper = _stepWrappers[i];
            wrapper.Classes.Remove("dragging");
            wrapper.ClearValue(TransitionsProperty);
            wrapper.ZIndex = 0;
            wrapper.RenderTransform = identity;
            if (i < _stepConnectors.Count && _stepConnectors[i] is { } conn)
                conn.Opacity = 0.75;
        }

        _draggedCard?.Classes.Remove("dragging");
        _stepDragVisualActive = false;
        _draggedCard = null;
        _dragFromIndex = -1;
        _dragTargetIndex = -1;
    }
}
