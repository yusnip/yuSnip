using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Services;
using ScreenCaptureTool.Utils;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 拖拽放置目标状态
/// </summary>
public enum DropTargetPosition
{
    None,
    Top,
    Bottom,
    Merge
}

/// <summary>
/// 智能排版润色修正项（UI 视图模型）：原文片段 → 修正词 + 理由，Applied=false 表示已被用户恢复。
/// 文本始终以 ReflowPlain（未润色基准）+ Edits 状态重建，恢复/重渲染零歧义。
/// </summary>
public sealed class ReflowEditView
{
    public string Orig { get; init; } = string.Empty;
    public string Fix { get; init; } = string.Empty;
    public string Why { get; init; } = string.Empty;
    public bool Applied { get; set; } = true;
}

/// <summary>
/// 单行 OCR 文本数据项 ViewModel：严格按照原型交互规范与状态管理设计。
/// 支持未选/悬停/选中激活/已修改/单行双击编辑/全局编辑模式/拖拽排序合并全状态。
/// </summary>
public sealed class OcrLineItem : INotifyPropertyChanged
{
    private readonly OcrWindow _parent;
    private int _index;
    private readonly int _boxIndex;
    private readonly int[] _sourceBoxIndices;
    private string _originalText;
    private string _currentText;
    private bool _isSelected;
    private bool _isHovered;
    private bool _isGlobalEditMode;
    private bool _isSingleEditing;
    private DropTargetPosition _dropState = DropTargetPosition.None;

    public event PropertyChangedEventHandler? PropertyChanged;

    public OcrLineItem(OcrWindow parent, int index, string text)
        : this(parent, index, text, text, index)
    {
    }

    /// <summary>
    /// 指定原始文本与当前文本的构造（用于合并视图整段编辑后重建行卡片，保留"已改"判定基准）。
    /// </summary>
    public OcrLineItem(OcrWindow parent, int index, string text, string originalText)
        : this(parent, index, text, originalText, index)
    {
    }

    /// <summary>
    /// 完整构造（显式指定原图框索引；sourceBoxIndices 为智能排版块卡片对应的全部源框，默认仅自身）。
    /// </summary>
    public OcrLineItem(OcrWindow parent, int index, string text, string originalText, int boxIndex,
        int[]? sourceBoxIndices = null)
    {
        _parent = parent;
        _index = index;
        _boxIndex = boxIndex;
        _sourceBoxIndices = sourceBoxIndices ?? new[] { boxIndex };
        _originalText = originalText ?? string.Empty;
        _currentText = text ?? string.Empty;
    }

    public int Index
    {
        get => _index;
        set
        {
            if (_index != value)
            {
                _index = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IndexNumber));
            }
        }
    }

    /// <summary>
    /// 对应左侧原图识别框（HighlightOverlay.Boxes）的稳定索引。
    /// 与 <see cref="Index"/>（列表位置，删除/排序后会重编号）不同：
    /// BoxIndex 在构建时绑定、永不变化，删除行/拖拽排序/拖拽合并后图文联动依然精准对应。
    /// -1 表示无对应原图框（如合并视图增行后重建），联动安全降级为不高亮。
    /// </summary>
    public int BoxIndex => _boxIndex;

    /// <summary>
    /// 该卡片对应的全部原图框索引（悬停联动用 BoxIndex 首框；「已改」高亮用全集）。
    /// 智能排版的段落/列表块卡片由多行合并而成 → 整块源框全部点亮。
    /// </summary>
    public IReadOnlyList<int> SourceBoxIndices => _sourceBoxIndices;

    /// <summary>
    /// 智能排版块角色：'\0' 普通行卡 / 'T' 标题块 / 'P' 段落块 / 'L' 列表项 / 'O' 保守模式原始行 /
    /// 'C' 代码块 / 'Q' 引用块。
    /// 用于排版态下分行 → 合并视图时按块类型重建结构化文本（标题/段落后空行、列表项连续）。
    /// </summary>
    public char ReflowRole { get; set; }

    /// <summary>标题层级 1/2/3（仅 'T' 角色；AI 判定，0=未分级→渲染按 2 处理）。</summary>
    public int ReflowTitleLevel { get; set; }

    /// <summary>列表嵌套深度 0/1/2（仅 'L' 角色）。</summary>
    public int ReflowListDepth { get; set; }

    /// <summary>有序列表（仅 'L' 角色：true = 编号式）。</summary>
    public bool ReflowListOrdered { get; set; }

    /// <summary>有序列表块内 1 基编号（仅 'L' 且 Ordered；0 = 无序）。</summary>
    public int ReflowListOrdinal { get; set; }

    /// <summary>代码语言标识（仅 'C' 角色；null = 未指定）。</summary>
    public string? ReflowCodeLang { get; set; }

    /// <summary>排版块的未润色基准文本（润色修正前；null = 非排版块或未开启润色）。</summary>
    public string? ReflowPlain { get; set; }

    /// <summary>排版块的润色修正清单（橙色标注 + 可逐处恢复）。</summary>
    public List<ReflowEditView>? ReflowEdits { get; set; }

    public int IndexNumber => _index + 1;
    public string OriginalText => _originalText;

    public string CurrentText
    {
        get => _currentText;
        set
        {
            if (_currentText != value)
            {
                _currentText = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEdited));
                RefreshVisualStyles();
                _parent.OnLineItemEdited(this);
            }
        }
    }

    public bool IsEdited => _currentText != _originalText;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
                RefreshVisualStyles();
            }
        }
    }

    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered != value)
            {
                _isHovered = value;
                OnPropertyChanged();
                RefreshVisualStyles();
            }
        }
    }

    public bool IsEditMode => _isGlobalEditMode || _isSingleEditing;
    public bool IsDisplayMode => !IsEditMode;
    public bool IsSingleEditing => _isSingleEditing;

    public void SetGlobalEditMode(bool globalEdit)
    {
        _isGlobalEditMode = globalEdit;
        _isSingleEditing = false;
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsDisplayMode));
        OnPropertyChanged(nameof(IsSingleEditing));
        OnPropertyChanged(nameof(CardCursor));
        RefreshVisualStyles();
    }

    public void SetSingleEdit(bool singleEdit)
    {
        if (_isGlobalEditMode) return;
        _isSingleEditing = singleEdit;
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsDisplayMode));
        OnPropertyChanged(nameof(IsSingleEditing));
        OnPropertyChanged(nameof(CardCursor));
        RefreshVisualStyles();
    }

    public DropTargetPosition DropState
    {
        get => _dropState;
        set
        {
            if (_dropState != value)
            {
                _dropState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDropTop));
                OnPropertyChanged(nameof(IsDropBottom));
                OnPropertyChanged(nameof(IsDropMerge));
                RefreshVisualStyles();
            }
        }
    }

    public bool IsDropTop => _dropState == DropTargetPosition.Top;
    public bool IsDropBottom => _dropState == DropTargetPosition.Bottom;
    public bool IsDropMerge => _dropState == DropTargetPosition.Merge;

    private bool _isDragged;

    /// <summary>当前是否正被拖拽（半透明浮起 + 拖动光标视觉）。</summary>
    public bool IsDragged
    {
        get => _isDragged;
        set
        {
            if (_isDragged != value)
            {
                _isDragged = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CardOpacity));
                OnPropertyChanged(nameof(CardCursor));
                RefreshVisualStyles();
            }
        }
    }

    /// <summary>被拖拽时整卡半透明，明确"正拿着这一行"。</summary>
    public double CardOpacity => IsDragged ? 0.55 : 1.0;

    public void Reset()
    {
        CurrentText = _originalText;
    }

    public Cursor CardCursor => IsDragged ? CursorDragMove : (IsEditMode ? Cursor.Default : CursorHand);

    private static readonly Cursor CursorHand = new(StandardCursorType.Hand);
    private static readonly Cursor CursorDragMove = new(StandardCursorType.DragMove);

    public void RefreshVisualStyles()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsHovered));
        OnPropertyChanged(nameof(IsEdited));
        OnPropertyChanged(nameof(IsDragged));
        OnPropertyChanged(nameof(IsDropMerge));
        OnPropertyChanged(nameof(CardCursor));
        OnPropertyChanged(nameof(CardOpacity));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// OCR 识别结果窗口（按 HTML 原型 1:1 精确实现）：
/// - 顶栏 Header (54px)：Logo、标题、Meta (来源/引擎/耗时)、关闭；
/// - 工具栏 Toolbar (46px)：重新识别、复制、导出下拉 (TXT/MD/DOCX/PDF)、联动高亮 Toggle、文本编辑 Toggle、还原按钮；
/// - 主体 Body：左栏原图 (47%) + 右栏文本卡片 (53%)，支持双向图文联动高亮、单行双击/全局编辑、拖拽排序与合并；
/// - 底部状态栏 StatusBar (30px)：共 X 行、已选择 Y 区域、已修改 Z 行、识别完成；
/// - 悬浮 Toast (10px 圆角 / 8px 24px 阴影)。
/// </summary>
public partial class OcrWindow : Window
{
    private static OcrWindow? _instance;

    private WriteableBitmap? _image;
    private System.Drawing.Bitmap? _cachedGdiBitmap;
    private int _imgW, _imgH;
    private double _scale = 1.0;
    private bool _fitMode = true;

    // 拖拽平移状态
    private bool _isPanning;
    private bool _panCandidate;
    private Point _panStartPoint;
    private Vector _panStartOffset;

    // 行拖拽重排与合并状态
    private readonly ListReorderBehavior _lineDrag = new(10);
    private bool _isPointerDownOnCard;
    private OcrLineItem? _draggedItem;
    private bool _isDraggingLine;

    // 原始识别数据备份（用于完全还原）
    private readonly List<string> _initialLines = new();
    private IReadOnlyList<OcrResultBox>? _initialBoxes;

    // 识别多边形框与行列表
    private IReadOnlyList<OcrResultBox>? _boxes;
    private readonly ObservableCollection<OcrLineItem> _lineItems = new();
    private int _selectedLineIndex = -1;
    private bool _linkageEnabled = true;
    private bool _isEditMode = false;
    private bool _isMergedMode = false;   // 合并视图：整段文本编辑（false = 分行卡片视图）

    // 智能排版状态（AI 排版见 Core/Ai/AiReflowService.cs）
    // 架构：分行/合并只编辑 _lineItems（原始 OCR 行，单一状态机）；
    // 排版结果独立存放 _reflowItems（块项，排版视图专用）——排版不改写行列表，
    // 行编辑也不破坏排版结果（仅标记「原文已修改」过期提示）。
    private bool _reflowApplied;                            // 排版已应用
    private readonly List<OcrLineItem> _reflowItems = new();        // 排版结果块项（独立于行列表）
    private List<string>? _reflowSourceTexts;               // 排版时行文本快照（检测行编辑导致的排版过期）
    private string? _mergedTextAtSwitch;                    // 切入合并视图时的文本（切回分行时判断是否被编辑过）
    private Dictionary<int, List<int>>? _blockToItems;      // 排版块索引 → _reflowItems 索引（润色修正定位用）

    // ═ 智能排版视图（第三段）状态 ═
    private enum ReflowStage { Idle, Running, Done, Fail }
    private ReflowStage _reflowStage = ReflowStage.Idle;    // 智能排版视图当前阶段
    private bool _isReflowView;                             // 当前处于智能排版视图
    private bool _viewSyncBusy;                             // 视图控件程序化同步中（事件回声早退）
    private int _polishLevel;                               // 润色级别：0=关, 1=Ⅰ 仅错字, 2=Ⅱ 含补全, 3=Ⅲ 深度修正
    private bool PolishOn => _polishLevel > 0;
    private string PolishLevelRoman() => _polishLevel switch { 1 => "Ⅰ", 2 => "Ⅱ", 3 => "Ⅲ", _ => "" };
    private string PolishLevelName() => _polishLevel switch { 1 => "轻度", 2 => "中度", 3 => "深度", _ => "关闭" };
    private bool _mdOn;                                     // MD 排版开关（开启 = 完成态显示/复制 Markdown 源码）
    private bool _connOk;                                   // 大模型连通性（绿灯）
    private bool _connChecking;
    private string _connFailMsg = "";
    private int _undoFixCount;                               // 已恢复的润色修正数
    private int _preReflowLineCount;                         // 排版前行数（统计用）
    private System.Diagnostics.Stopwatch? _reflowSw;         // 排版计时
    private Avalonia.Threading.DispatcherTimer? _reflowClock;// 计时刷新（0.1s）
    private ProgressBar? _rfProg;
    private TextBlock? _rfTime;
    private readonly List<Border> _rfStepChips = new();
    private int _rfCurrentStep = -1;
    private string? _reflowFailMsg;                           // 排版失败具体原因（来自服务层，直示用户）

    // ═ 初始/修改对照（工具栏图标开关 BtnInitMod）═
    /// <summary>暂存一行修改态（含排版块结构与润色标注），供 初始⇄修改 无损互切。</summary>
    private sealed record StashedLineState(string Current, string Original, int BoxIndex, char Role, string? Plain,
        List<ReflowEditView>? Edits, int TitleLevel, int ListDepth, bool ListOrdered, int ListOrdinal, string? CodeLang);
    private bool _viewingInitial;                              // 正在查看初始识别结果（只读）
    private bool _initModBusy;                                 // 初始/修改图标开关程序化拨动中（防事件回声重入）
    private List<StashedLineState>? _stashedModified;          // 修改态暂存
    private bool _stashReflowApplied;

    /// <summary>
    /// 展示（或复用已有窗口更新）识别结果。
    /// </summary>
    public static void ShowResult(string text, System.Drawing.Bitmap? gdiBitmap, IReadOnlyList<OcrResultBox>? boxes = null, double elapsedSeconds = 0.0)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ShowResultCore(text, gdiBitmap, boxes, elapsedSeconds);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowResultCore(text, gdiBitmap, boxes, elapsedSeconds));
        }
    }

    /// <summary>
    /// 展示完整 OCR 结果数据包（含文本与多边形框）。
    /// </summary>
    public static void ShowResult(OcrResultData resultData, System.Drawing.Bitmap? gdiBitmap, double elapsedSeconds = 0.0)
    {
        ShowResult(resultData.Text, gdiBitmap, resultData.Boxes, elapsedSeconds);
    }

    private static void ShowResultCore(string text, System.Drawing.Bitmap? gdiBitmap, IReadOnlyList<OcrResultBox>? boxes, double elapsedSeconds)
    {
        if (_instance != null)
        {
            _instance.SetContent(text, gdiBitmap, boxes, elapsedSeconds);
            _instance.Activate();
            return;
        }
        _instance = new OcrWindow(text, gdiBitmap, boxes, elapsedSeconds);
        _instance.Show();
    }

    private OcrWindow(string text, System.Drawing.Bitmap? gdiBitmap, IReadOnlyList<OcrResultBox>? boxes, double elapsedSeconds)
    {
        InitializeComponent();

        // 生长菜单动画 Transform 缓存（XAML 中嵌于 RenderTransform 内的 x:Name 不生成字段）
        _growTransformGroup = (TransformGroup)GrowMenuHost.RenderTransform!;
        _growMenuScale = (ScaleTransform)_growTransformGroup.Children[0];
        _growMenuTranslate = (TranslateTransform)_growTransformGroup.Children[1];

        ItemsTextLines.ItemsSource = _lineItems;

        // 构建「清除 / 校对」右键子菜单（合并后 8 项；浮动工具栏按钮使用生长菜单）
        BuildContextMenuOps();

        // 生长菜单：菜单为可视化树内常驻 Border（非 Popup，不抢焦点），
        // 选区与工具栏全程保持，仅在打开期间切换为灰调交互态。
        // 清除/校对按钮的 Click 已在 XAML 绑定 OnTtClearMenuClick/OnTtProofMenuClick → ToggleGrowMenu，
        // 此处不可重复注册，否则一次点击 Toggle 两次（开→关），菜单闪失。
        AddHandler(InputElement.PointerPressedEvent, OnGrowMenuGlobalPointerPressed, RoutingStrategies.Tunnel);

        // 右键菜单交互的焦点管理：弹窗会抢焦点导致 TextBox 失焦、选区状态丢失，
        // 菜单关闭后恢复焦点与选区高亮、按新选区重定位工具栏。
        TxtAllLines.ContextMenu!.Closed += (_, _) => OnTextMenuClosed();

        // TextBox 无 SelectionChanged 事件，监听选区 styled 属性变化
        TxtAllLines.PropertyChanged += OnMergedBoxPropertyChanged;

        // 鼠标拖选/键盘选区时 TextBox 未必触发属性变化，隧道订阅指针与键盘事件兜底
        TxtAllLines.AddHandler(InputElement.PointerPressedEvent, OnMergedBoxTunnelPointer, RoutingStrategies.Tunnel);
        TxtAllLines.AddHandler(InputElement.PointerMovedEvent, OnMergedBoxTunnelPointer, RoutingStrategies.Tunnel);
        TxtAllLines.AddHandler(InputElement.PointerReleasedEvent, OnMergedBoxTunnelPointer, RoutingStrategies.Tunnel);
        TxtAllLines.AddHandler(InputElement.KeyUpEvent, OnMergedBoxTunnelKeyUp, RoutingStrategies.Tunnel);

        // 注册高亮层联动事件
        HighlightOverlay.BoxHoverChanged += OnOverlayBoxHoverChanged;
        HighlightOverlay.BoxClicked += OnOverlayBoxClicked;

        KeyDown += OnWindowKeyDown;

        // 注册窗口级指针隧道事件：统一管理编辑状态的焦点转移与点击空白/外部/按钮时退出编辑
        AddHandler(InputElement.PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        PropertyChanged += (s, e) =>
        {
            if (e.Property == ActualThemeVariantProperty)
            {
                UpdateAppIcons();
            }
        };

        UpdateAppIcons();
        SetContent(text, gdiBitmap, boxes, elapsedSeconds);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 生长菜单打开时 Esc 只收起菜单，不关闭窗口
            if (_growMenuOpen)
            {
                CloseGrowMenu();
                e.Handled = true;
                return;
            }
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || !(FocusManager?.GetFocusedElement() is TextBox)))
        {
            OnCopyAllClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            // F2：编辑当前选中行（与行右键菜单「编辑本行」同源）
            if (TryHandleLineShortcut(edit: true)) e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            // Del：删除当前选中行（焦点在 TextBox 内时不劫持，让给正常文本删除）
            if (TryHandleLineShortcut(edit: false)) e.Handled = true;
        }
    }

    /// <summary>
    /// 行级快捷键（F2 编辑 / Del 删除）作用于当前选中行：
    /// 仅在分行视图、非全局/单行编辑状态下生效；Del 额外要求焦点不在任何 TextBox 中。
    /// </summary>
    private bool TryHandleLineShortcut(bool edit)
    {
        if (_isMergedMode || _isEditMode || _selectedLineIndex < 0 || _selectedLineIndex >= _lineItems.Count) return false;
        if (_lineItems.Any(l => l.IsSingleEditing)) return false;
        if (!edit && FocusManager?.GetFocusedElement() is TextBox) return false;

        var item = _lineItems[_selectedLineIndex];
        if (edit)
        {
            if (item.IsEditMode) return false;
            EnterLineEdit(item);
        }
        else
        {
            DeleteLine(item);
        }
        return true;
    }

    /// <summary>
    /// 更新文本、图片与多边形高亮框
    /// </summary>
    private void SetContent(string text, System.Drawing.Bitmap? gdiBitmap, IReadOnlyList<OcrResultBox>? boxes, double elapsedSeconds = 0.0)
    {
        UpdateAppIcons();
        _boxes = boxes;
        _initialBoxes = boxes;

        // 新内容 → 重置智能排版状态（润色 / MD 灯熄灭；初始/修改对照复位）
        ResetReflowResult();
        _mergedTextAtSwitch = null;
        _reflowStage = ReflowStage.Idle;
        _undoFixCount = 0;
        if (_polishLevel != 0) { _polishLevel = 0; UpdatePolishPill(); }
        if (_mdOn) { _mdOn = false; if (LampMd != null) LampMd.Classes.Set("on", false); }
        if (TxtStartReflow != null) TxtStartReflow.Text = "开始排版";
        if (BtnStartReflow != null && !_connChecking) BtnStartReflow.IsEnabled = _connOk;
        // 初始/修改复位：先禁用图标开关（程序化拨动不触发状态机），行列表重建后统一收口
        _viewingInitial = false;
        _initModBusy = false;
        _stashedModified = null;
        _stashReflowApplied = false;
        if (BtnInitMod != null) BtnInitMod.IsEnabled = false;
        SetInitModToggle(false);
        TxtAllLines.IsReadOnly = false;
        if (PanelReflow != null && PanelReflow.IsVisible) RenderReflowStage();

        // 更新耗时
        if (TxtMetaTime != null)
        {
            TxtMetaTime.Text = elapsedSeconds > 0 ? $"{elapsedSeconds:0.00}s" : "0.32s";
        }

        // 1. 缓存原图用于重新识别与转换
        var oldGdi = _cachedGdiBitmap;
        try { oldGdi?.Dispose(); } catch { }
        _cachedGdiBitmap = gdiBitmap != null ? new System.Drawing.Bitmap(gdiBitmap) : null;

        var oldBmp = _image;
        try { oldBmp?.Dispose(); } catch { }
        _image = null;

        try
        {
            _image = BitmapConversion.ToAvaloniaBitmap(gdiBitmap);
            _imgW = gdiBitmap?.Width ?? 0;
            _imgH = gdiBitmap?.Height ?? 0;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OcrWindow.SetContent 转换图片异常: " + ex.Message);
        }
        finally
        {
            try { gdiBitmap?.Dispose(); } catch { }
        }

        bool hasImage = _image != null && _imgW > 0 && _imgH > 0;
        ImgPreview.Source = _image;
        PanelNoImage.IsVisible = !hasImage;
        HighlightOverlay.SetBoxes(boxes);

        // 按图片宽高比自适应主体分栏（竖图左右分 / 横图上下分）
        ApplyAdaptiveLayout();

        // 2. 构建文本行列表并备份
        _lineItems.Clear();
        _initialLines.Clear();

        string[] rawLines;
        if (boxes != null && boxes.Count > 0)
        {
            rawLines = boxes.Select(b => b.Text ?? string.Empty).ToArray();
        }
        else
        {
            rawLines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        }

        for (int i = 0; i < rawLines.Length; i++)
        {
            _initialLines.Add(rawLines[i]);
            // BoxIndex 绑定原图框索引（boxes 缺失时 -1，联动降级）
            var item = new OcrLineItem(this, i, rawLines[i], rawLines[i], boxes != null && i < boxes.Count ? i : -1);
            item.SetGlobalEditMode(_isEditMode);
            _lineItems.Add(item);
        }

        // 合并视图下同步整段文本
        if (_isMergedMode)
        {
            TxtAllLines.Text = string.Join("\n", _lineItems.Select(l => l.CurrentText));
        }

        // 3. 刷新状态栏统计
        RefreshStatusBar();

        // 4. 初始自适应缩放并重置选中状态
        _fitMode = true;
        _selectedLineIndex = -1;
        HighlightOverlay.SelectedIndex = -1;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ApplyFitScale();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
        try { _image?.Dispose(); } catch { }
        try { _cachedGdiBitmap?.Dispose(); } catch { }
        _image = null;
        _cachedGdiBitmap = null;
    }

    /// <summary>
    /// Logo 与窗口图标随主题切换（与设置页/托盘一致：深色用 Black.ico / logo_dark.ico，浅色用 White.ico / logo_light.ico）。
    /// </summary>
    private void UpdateAppIcons()
    {
        try
        {
            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            Uri uri;
            if (isDark)
            {
                uri = Avalonia.Platform.AssetLoader.Exists(new Uri("avares://yuSnip/Assets/logo_dark.ico"))
                    ? new Uri("avares://yuSnip/Assets/logo_dark.ico")
                    : new Uri("avares://yuSnip/Assets/Black.ico");
            }
            else
            {
                uri = Avalonia.Platform.AssetLoader.Exists(new Uri("avares://yuSnip/Assets/logo_light.ico"))
                    ? new Uri("avares://yuSnip/Assets/logo_light.ico")
                    : new Uri("avares://yuSnip/Assets/White.ico");
            }

            Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(uri));
            if (ImgAppLogo != null)
            {
                ImgAppLogo.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(uri));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OcrWindow.UpdateAppIcons 失败：" + ex.Message);
        }
    }

    #region 顶栏操作

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region 工具栏交互

    /// <summary>
    /// 重新识别当前原图
    /// </summary>
    private async void OnReOcrClick(object? sender, RoutedEventArgs e)
    {
        if (_cachedGdiBitmap == null)
        {
            ShowToast("未找到可识别的原图");
            return;
        }

        try
        {
            BtnReOcr.IsEnabled = false;
            BtnReOcr.Content = "识别中...";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ocrResult = await Task.Run(async () =>
            {
                return await CaptureSession.GetSharedOcrService().ExtractFullResultAsync(_cachedGdiBitmap);
            });
            sw.Stop();

            if (ocrResult != null && !string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                UsageStatistics.RecordOcr(); // 统计：重新识别成功一次
                var clonedBmp = new System.Drawing.Bitmap(_cachedGdiBitmap);
                SetContent(ocrResult.Text, clonedBmp, ocrResult.Boxes, sw.Elapsed.TotalSeconds);
                ShowToast("重新识别完成");
            }
            else if (ocrResult != null && !string.IsNullOrWhiteSpace(ocrResult.Error))
            {
                ShowToast("识别失败: " + ocrResult.Error);
            }
            else
            {
                ShowToast("未识别到文字");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("重新识别异常", ex);
            ShowToast("识别失败: " + ex.Message);
        }
        finally
        {
            BtnReOcr.IsEnabled = true;
            BtnReOcr.Content = "重新识别";
        }
    }

    #region 智能排版（仅 AI：决策 + 本地执行，见 Core/Ai/AiReflowService.cs）

    /// <summary>
    /// 智能排版（仅 AI，不做本地规则引擎回退）：
    /// LLM 只输出结构决策（块类型 + 行号引用 + 符号风格 [+ 润色 edits]），不输出整段正文——
    /// 文本内容 100% 来自 OCR 原文，从结构上杜绝改写/幻觉；决策经三道校验
    /// （行号全覆盖恰好一次 / 块内连续块间有序 / 枚举合法），未配置 AI 或
    /// 任何一关不过即放弃排版保持原样（Toast 明示原因），绝不排错。
    /// 入口：智能排版视图浮动工具栏「开始排版」按钮，带计时进度与润色修正标注。
    /// </summary>
    private async void OnStartReflowClick(object? sender, RoutedEventArgs e)
    {
        if (_reflowStage == ReflowStage.Running) return;
        if (_viewingInitial) { ShowToast("请先切回「修改」再排版"); return; }

        if (!AiReflowService.IsConfigured())
        {
            _connOk = false;
            _connFailMsg = "未配置 AI 服务";
            UpdateConnLampUI();
            ShowToast("未配置 AI 服务，请先在设置中配置 AI 识图");
            return;
        }
        if (!_connOk)
        {
            ShowToast("AI 未连接，点击状态灯重新检测");
            CheckReflowConn();
            return;
        }

        // 合并视图下的整段编辑先同步回行列表
        SyncMergedTextIfActive();

        // 重新排版：清旧块结果（行列表始终是原始 OCR 行，无需恢复快照）
        ResetReflowResult();

        if (_lineItems.Count < 3)
        {
            ShowToast("行数太少，无需排版");
            return;
        }

        _preReflowLineCount = _lineItems.Count;
        _undoFixCount = 0;
        _reflowStage = ReflowStage.Running;
        TxtStartReflow.Text = "排版中…";
        BtnStartReflow.IsEnabled = false;
        if (BtnInitMod != null) BtnInitMod.IsEnabled = false;   // 排版期间锁定初始/修改切换（防异步应用时行列表被切换重建）
        RenderReflowStage();

        _reflowSw = System.Diagnostics.Stopwatch.StartNew();
        StartReflowClock();
        AppLogger.Info($"[RfProg] 排版开始：行数={_lineItems.Count} 润色级别={_polishLevel} AI预期时长={_rfAiExpectedSec:0.0}s（历史学习值） exe={System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}");

        // 步骤 0：收集行数据
        SetReflowStep(0);
        SetReflowProgress(8);
        var input = BuildAiInputLines();
        await Task.Delay(150);

        // 步骤 1：AI 推理（耗时主体，进度条按预期时长缓出曲线推进，见 StartReflowClock）
        SetReflowStep(1);
        SetReflowProgress(16);
        _rfAiStartSec = _reflowSw.Elapsed.TotalSeconds;
        double aiExpectedAtCall = _rfAiExpectedSec;   // 留存本次调用时的预期（返回时对比实际值）
        AppLogger.Info($"[RfProg] AI 调用开始：t={_rfAiStartSec:0.0}s 预期={aiExpectedAtCall:0.0}s");
        AiReflowOutcome outcome;
        try
        {
            outcome = await AiReflowService.Instance.AnalyzeAsync(input, _polishLevel);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("智能排版异常: " + ex.Message);
            _reflowFailMsg = ex.Message;
            await Task.Delay(450);   // 失败切换前留缓冲，避免进度条戛然而止
            FinishReflowFail(AiReflowFailReason.Network);
            return;
        }

        // AI 实际耗时回写预期值（下次排版曲线按本次真实节奏走，越用越匀）
        double aiActual = _reflowSw.Elapsed.TotalSeconds - _rfAiStartSec;
        AppLogger.Info($"[RfProg] AI 调用返回：实际耗时={aiActual:0.0}s（本次按预期 {aiExpectedAtCall:0.0}s 走） 返回时进度={(_rfProg?.Value ?? -1):0.0} tick总数={_rfTickTotal}");
        _rfAiExpectedSec = Math.Clamp(aiActual, 3, 90);

        // 步骤 2：校验决策
        SetReflowStep(2);
        SetReflowProgress(84);
        await Task.Delay(400);   // 后段里程碑逐级停顿拉匀，结尾不"一下子冲完"
        if (outcome.Decision == null)
        {
            _reflowFailMsg = outcome.Message;
            await Task.Delay(450);   // 失败切换前留缓冲，避免进度条戛然而止
            FinishReflowFail(outcome.Reason);
            return;
        }

        // 步骤 3：应用排版（符号风格算子 → 独立块结果 → 润色修正挂载；行列表不动）
        SetReflowStep(3);
        SetReflowProgress(92);
        var result = ConvertAiDecision(outcome.Decision, input);
        var styles = outcome.Decision.Styles;

        ApplyReflowToItems(result, BuildStyleOp(styles));
        if (PolishOn) ApplyPolishEditsToItems(result, outcome.Decision.Edits);

        _reflowApplied = true;

        ClearSelection();
        SyncEditedIndicesToOverlay();

        await Task.Delay(400);   // 让 92% 过渡可见

        SetReflowProgress(100);
        SetReflowStep(4);
        StopReflowClock();
        // 等进度条 0.55s 宽度过渡播完（视觉走到底）再切换完成视图，避免"中途就结束"的突兀感
        await Task.Delay(650);
        double elapsed = _reflowSw?.Elapsed.TotalSeconds ?? 0;

        _reflowStage = ReflowStage.Done;
        TxtStartReflow.Text = "重新排版";
        BtnStartReflow.IsEnabled = true;
        // 阶段落定后再刷新：排版中调用会被 UpdateInitModToggle 的 Running 守卫跳过，
        // 导致完成后 初始/修改 分段永远停在禁用态（灰色不可点）
        RefreshStatusBar();
        RenderReflowStage();

        int nTitle = result.Blocks.Count(b => b.Type == ReflowBlockType.Title);
        int nPara = result.Blocks.Count(b => b.Type == ReflowBlockType.Para);
        int nList = result.Blocks.Count(b => b.Type == ReflowBlockType.List);
        int nFix = _reflowItems.Sum(l => l.ReflowEdits?.Count(ed => ed.Applied) ?? 0);
        bool hasStyle = styles != null && (styles.Quote != null || styles.Paren != null || styles.Punct != null);
        ShowToast($"智能排版完成 · {elapsed:0.0}s · {_preReflowLineCount} 行 → {result.Blocks.Count} 块（标题 {nTitle} · 段落 {nPara} · 列表 {nList}）" +
                  $"{(hasStyle ? " · 符号风格已统一" : "")}{(nFix > 0 ? $" · 修正 {nFix} 处（点击橙色修正可恢复）" : "")}");
    }

    /// <summary>排版失败收尾：复位按钮态、渲染错误条并提示原因（保持原样，宁可不排也不错排）。</summary>
    private void FinishReflowFail(AiReflowFailReason reason)
    {
        StopReflowClock();
        _reflowStage = ReflowStage.Fail;
        TxtStartReflow.Text = "重新排版";
        BtnStartReflow.IsEnabled = true;
        RefreshStatusBar();   // 阶段落定后刷新（排版期间锁定的 初始/修改 分段按需恢复可用）
        RenderReflowStage();
        ShowToast(reason switch
        {
            AiReflowFailReason.Network => "AI 网络不可用，已保持原样",
            AiReflowFailReason.Validation => "AI 结果未通过校验，已保持原样",
            AiReflowFailReason.BadFormat => "AI 返回格式异常，已保持原样",
            _ => "AI 排版未执行，已保持原样"
        });
    }

    // ═══ 智能排版视图渲染（Idle / Running / Done / Fail 四阶段） ═══

    /// <summary>排版面板原文卡的文本：恒为行列表当前文本（初态查看期间行列表已被换为初始行）。</summary>
    private string GetReflowRawText() =>
        string.Join("\n", _lineItems.Select(l => l.CurrentText));

    /// <summary>排版后行文本是否已被编辑（排版结果基于旧文本 → 过期，建议重新排版）。</summary>
    private bool ReflowSourcesStale =>
        _reflowSourceTexts == null
        || _reflowSourceTexts.Count != _lineItems.Count
        || _lineItems.Where((l, i) => l.CurrentText != _reflowSourceTexts[i]).Any();

    /// <summary>
    /// 排版视图统一状态条（rfStateBar）：状态点 + 主状态词 + 说明 + 右侧操作按钮——
    /// 初态查看（灰）/ 未排版（紫）/ 排版完成（绿）/ 失败（红）四态共用一套视觉语言，
    /// 替代原先各自为政的 rfTip 提示行 / Idle 横排 / Done 统计 chips / Fail 红条。
    /// </summary>
    private ContinuousBorder BuildReflowStateBar(string dotBrushKey, string title, string titleBrushKey, string desc,
        params (string Text, System.EventHandler<RoutedEventArgs> Click)[] actions)
    {
        var bar = new ContinuousBorder { Classes = { "rfStateBar" } };
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,*,Auto") };

        // 状态点（色随状态：灰=只读初态 / 紫=待排版 / 绿=完成 / 红=失败）
        var dot = new Ellipse { Classes = { "rfStateDot" }, Fill = ResBrush(dotBrushKey), Margin = new(0, 0, 9, 0) };
        grid.Children.Add(dot);

        // 主状态词（粗体，色随状态）
        grid.Children.Add(new TextBlock
        {
            Classes = { "rfStateTitle" },
            Text = title,
            Foreground = ResBrush(titleBrushKey)
        });
        Grid.SetColumn(grid.Children[^1], 1);

        // 说明（可空：仅状态词时不占位）
        if (!string.IsNullOrEmpty(desc))
        {
            grid.Children.Add(new TextBlock
            {
                Classes = { "rfStateDesc" },
                Text = desc,
                Margin = new(10, 1, 0, 0)
            });
            Grid.SetColumn(grid.Children[^1], 2);
        }

        // 右侧操作按钮组（rfChip 小胶囊）
        if (actions.Length > 0)
        {
            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            foreach (var (text, click) in actions)
            {
                var btn = new Button { Content = text, Classes = { "rfChip" } };
                btn.Click += (s, e) => click(s, e);
                sp.Children.Add(btn);
            }
            grid.Children.Add(sp);
            Grid.SetColumn(sp, 4);
        }

        bar.Child = grid;
        return bar;
    }

    /// <summary>
    /// 状态条装入底部宿主（ReflowStateHost，dock 在工具栏上方）——常驻可见不随内容滚动；
    /// 运行态注入进度卡（进度卡即该阶段的状态表达）；null = 清空。
    /// </summary>
    private void SetReflowStateBar(ContinuousBorder? bar)
    {
        if (ReflowStateHost == null) return;
        ReflowStateHost.Children.Clear();
        if (bar != null)
        {
            bar.Margin = new Thickness(16, 0, 16, 10);
            ReflowStateHost.Children.Add(bar);
        }
    }

    /// <summary>按阶段渲染智能排版视图主体（状态条统一注入底部宿主，内容区只放卡片）。</summary>
    private void RenderReflowStage()
    {
        if (TxtReflowBox == null || ReflowRichHost == null) return;
        SetReflowStateBar(null);
        _rfStepChips.Clear();
        _rfProg = null;
        _rfTime = null;

        // 初始/修改对照：初态查看中只渲染状态条 + 原文文本框（行列表即初始行）
        if (_viewingInitial)
        {
            SetReflowStateBar(BuildReflowStateBar(
                "OcrTextMutedBrush", "正在查看初始识别", "OcrTextSecondaryBrush",
                "只读对照 · 点击右侧「返回修改」回到编辑结果",
                ("返回修改", (s, e) => ExitInitialState())));
            ShowReflowText(GetReflowRawText(), opacity: 1.0);
            return;
        }

        switch (_reflowStage)
        {
            case ReflowStage.Running: RenderReflowRunning(); break;
            case ReflowStage.Done: RenderReflowDone(); break;
            case ReflowStage.Fail: RenderReflowFail(); break;
            default: RenderReflowIdle(); break;
        }
    }

    /// <summary>
    /// 将排版/原文文本装载入智能排版专用的只读 TextBox（与合并模式同一样式和原生交互）。
    /// </summary>
    private void ShowReflowText(string text, double opacity = 1.0)
    {
        ReflowRichHost.IsVisible = false;
        ReflowRichHost.Children.Clear();
        TxtReflowBox.IsVisible = true;
        TxtReflowBox.Text = text;
        TxtReflowBox.Opacity = opacity;
    }

    /// <summary>排版只读框右键菜单：全选（无选区时全选并提示）。</summary>
    private void OnRfSelectAllClick(object? sender, RoutedEventArgs e)
    {
        TxtReflowBox.Focus();
        TxtReflowBox.SelectAll();
    }

    /// <summary>排版只读框右键菜单：复制（无选区时复制全部并提示）。</summary>
    private void OnRfCopyClick(object? sender, RoutedEventArgs e)
    {
        TxtReflowBox.Focus();
        if (Math.Abs(TxtReflowBox.SelectionEnd - TxtReflowBox.SelectionStart) > 0)
        {
            TxtReflowBox.Copy();
        }
        else
        {
            TxtReflowBox.SelectAll();
            TxtReflowBox.Copy();
            ShowToast("无选区，已复制全部文本");
        }
    }

    /// <summary>未排版：状态条 + 只读原文。</summary>
    private void RenderReflowIdle()
    {
        SetReflowStateBar(BuildReflowStateBar(
            "OcrAccentBrush", "尚未排版", "OcrAccentTextBrush",
            "上方为 OCR 原始识别结果 · 点击右下角「开始排版」由 AI 治理断句 / 空格 / 符号 / 段落"));

        ShowReflowText(GetReflowRawText(), opacity: 1.0);
    }

    /// <summary>排版中：进度卡（计时 + 渐变进度条 + 四步骤芯片）入底部宿主 + 半透明原文文本框。</summary>
    private void RenderReflowRunning()
    {
        var card = new ContinuousBorder { Classes = { "rfProgCard" } };
        var sp = new StackPanel { Spacing = 10 };

        // 头行：标题 + 计时
        var head = new Grid { ColumnDefinitions = new("Auto,Auto,*") };
        head.Children.Add(new TextBlock
        {
            Text = "AI 智能排版中",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = ResBrush("OcrTextPrimaryBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
        });
        _rfTime = new TextBlock
        {
            Text = "0.0s",
            FontFamily = new FontFamily("Consolas,Cascadia Mono,monospace"),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = ResBrush("OcrAccentTextBrush"),
            Margin = new(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
        };
        Grid.SetColumn(_rfTime, 1);
        head.Children.Add(_rfTime);
        sp.Children.Add(head);

        // 进度条
        _rfProg = new ProgressBar { Classes = { "rfProg" }, Value = 0 };
        sp.Children.Add(_rfProg);

        // 步骤芯片（圆点指示 + 文案，对齐原型 .pstep-chip::before）
        var chips = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        string[] labels = { "收集行数据", PolishOn ? $"AI 推理 · 润色{PolishLevelRoman()}" : "AI 推理", "校验决策", PolishOn ? "应用 · 标注修正" : "应用排版" };
        foreach (var label in labels)
        {
            var chip = new Border { Classes = { "rfStep" } };
            var inner = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            inner.Children.Add(new Ellipse { Classes = { "rfStepDot" }, Width = 5, Height = 5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            inner.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            chip.Child = inner;
            _rfStepChips.Add(chip);
            chips.Children.Add(chip);
        }
        sp.Children.Add(chips);

        card.Child = sp;
        SetReflowStateBar(card);   // 进度卡入底部宿主，与状态条同位常驻可见

        ShowReflowText(GetReflowRawText(), opacity: 0.55);
    }

    /// <summary>完成：状态条（统计整合进说明）+ 整篇文档（或 MD 预览）。</summary>
    private void RenderReflowDone()
    {
        int nTitle = _reflowItems.Count(l => l.ReflowRole == 'T');
        int nPara = _reflowItems.Count(l => l.ReflowRole == 'P');
        int nList = _reflowItems.Count(l => l.ReflowRole == 'L');
        int nLine = _reflowItems.Count(l => l.ReflowRole == 'O');
        int nCode = _reflowItems.Count(l => l.ReflowRole == 'C');
        int nQuote = _reflowItems.Count(l => l.ReflowRole == 'Q');
        int nFixTotal = _reflowItems.Sum(l => l.ReflowEdits?.Count ?? 0);
        int nFixOn = _reflowItems.Sum(l => l.ReflowEdits?.Count(ed => ed.Applied) ?? 0);

        // 说明：块统计 + 润色修正 + 原文过期提示（一次读全，避免状态条内多段拼接遗漏）
        var desc = $"{_preReflowLineCount} 行 → {_reflowItems.Count} 块 · 标题 {nTitle} · 段落 {nPara} · 列表 {nList} · 独立行 {nLine}";
        if (nCode > 0) desc += $" · 代码 {nCode}";
        if (nQuote > 0) desc += $" · 引用 {nQuote}";
        if (ReflowSourcesStale) desc += " · 原文已修改，建议重新排版";
        if (PolishOn)
            desc += nFixTotal > 0
                ? $" · 润色{PolishLevelRoman()} 修正 {nFixOn} 处{(((nFixTotal - nFixOn) > 0) ? $" · 已恢复 {nFixTotal - nFixOn}" : "")}"
                : $" · 润色{PolishLevelRoman()} 未发现错字";

        // 状态条入底部宿主
        SetReflowStateBar(BuildReflowStateBar("OcrOkBrush", "排版完成", "OcrOkBrush", desc));

        if (_mdOn)
        {
            TxtReflowBox.IsVisible = false;
            ReflowRichHost.IsVisible = true;
            ReflowRichHost.Children.Clear();
            var card = new ContinuousBorder { Classes = { "ocrPaperCard", "rfDocView" } };
            var md = new SelectableMarkdownTextBlock
            {
                Markdown = BuildMarkdownFromItems(),
                BaseFontSize = 13,
                Focusable = true,
                SelectionBrush = TextSelectionBrush
            };
            var sv = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 5, 5),
                Content = md
            };
            card.Child = sv;

            // 挂载跨块多段文本连续选区控制器到 ScrollViewer 所在的 card 上
            var mdController = new CrossBlockSelectionController(card, () => md.GetVisualDescendants().OfType<SelectableTextBlock>())
            {
                SelectionBrush = TextSelectionBrush
            };

            ReflowRichHost.Children.Add(card);
            return;
        }

        // 统一展示富排版文档（富文本块 + 橙色修正胶囊 + 恢复气泡 + 跨块连续划词）
        ShowReflowDocumentView();
    }

    /// <summary>
    /// 完成态排版文档视图：排版文档（BuildReflowDocument 富排版 + 橙色修正胶囊 + 恢复气泡 + 跨块自由划词控制器）
    /// 装入纸张卡 + 内层滚动，挂 ReflowRichHost 覆盖文本框。
    /// 无论是否有润色修正，均支持整篇跨段落、跨标题自由连续划词选择与 Ctrl+C 复制。
    /// </summary>
    private void ShowReflowDocumentView()
    {
        TxtReflowBox.IsVisible = false;
        ReflowRichHost.IsVisible = true;
        ReflowRichHost.Children.Clear();

        var docContent = BuildReflowDocument();
        var card = new ContinuousBorder { Classes = { "ocrPaperCard", "rfDocView" } };
        var sv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 5, 5),
            Content = docContent
        };
        card.Child = sv;

        // 挂载跨块多段文本连续选区控制器：统一驱动容器内所有 SelectableTextBlock
        var controller = new CrossBlockSelectionController(card, () => docContent.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            SelectionBrush = TextSelectionBrush
        };

        ReflowRichHost.Children.Add(card);
    }

    /// <summary>失败：状态条（红）+ 原文文本框。</summary>
    private void RenderReflowFail()
    {
        SetReflowStateBar(BuildReflowStateBar(
            "OcrDangerBrush", "排版未执行", "OcrDangerBrush",
            "网络不可用 / 结果未通过校验 · 已保持原样（宁可不排版，也不排错）",
            ("重试", (s, e) => OnStartReflowClick(s, e))));

        ShowReflowText(GetReflowRawText(), opacity: 1.0);
    }

    /// <summary>
    /// 完成态排版文档内容（块结构流）：标题按层级分号 / 段落正文 / 列表项有序编号或
    /// 圆点前缀并按深度嵌套缩进 / 代码块等宽卡片 / 引用块左竖线弱化 / 独立行次色；润色修正为
    /// 橙色内联胶囊（一个字 / 一个词 / 一句话均按修正跨度高亮），每个修正词独立交互——悬停仅
    /// 加深底色提示可点击、点击仅弹出该词恢复气泡，段落级不整段弹全部修正。
    /// 全局使用 SelectableTextBlock 渲染，外层挂载 CrossBlockSelectionController 实现整篇无缝跨块连续划词。
    /// </summary>
    private StackPanel BuildReflowDocument()
    {
        var sp = new StackPanel();

        foreach (var item in _reflowItems)
        {
            var body = BuildBlockText(item);
            switch (item.ReflowRole)
            {
                case 'T':
                    body.Classes.Add($"rfDocTitle{Math.Clamp(item.ReflowTitleLevel <= 0 ? 2 : item.ReflowTitleLevel, 1, 3)}");
                    sp.Children.Add(body);
                    break;
                case 'P':
                    body.Classes.Add("rfDocPara");
                    sp.Children.Add(body);
                    break;
                case 'L':
                {
                    // 列表项：有序编号 / 圆点前缀 + 段落正文（标记用弱化色，不占用「橙色 = AI 修正」的信号色）
                    bool ordered = item.ReflowListOrdered;
                    body.Classes.Add("rfDocPara");
                    body.Margin = new Thickness(0, 0, 0, 5);
                    var g = new Grid { ColumnDefinitions = new(ordered ? "22,*" : "14,*") };
                    var marker = new TextBlock
                    {
                        Text = ordered ? $"{item.ReflowListOrdinal}." : "•",
                        FontSize = ordered ? 12.5 : 14,
                        FontWeight = ordered ? FontWeight.SemiBold : FontWeight.Bold,
                        Foreground = ResBrush("OcrTextMutedBrush"),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                        Margin = new(0, ordered ? 2 : 1, 6, 0)
                    };
                    g.Children.Add(marker);
                    Grid.SetColumn(body, 1);
                    g.Children.Add(body);
                    // 嵌套列表：整行按深度左移缩进（每层 18px）
                    if (item.ReflowListDepth > 0)
                        g.Margin = new Thickness(Math.Clamp(item.ReflowListDepth, 0, 2) * 18, 0, 0, 0);
                    sp.Children.Add(g);
                    break;
                }
                case 'C':
                {
                    // 代码块：面板底色圆角卡片 + 等宽字体，保留原始分行
                    body.Classes.Add("rfDocCode");
                    var box = new ContinuousBorder { Classes = { "rfDocCodeBox" }, Child = body };
                    sp.Children.Add(box);
                    break;
                }
                case 'Q':
                {
                    // 引用块：左竖线 + 次色文本
                    body.Classes.Add("rfDocQuoteText");
                    var q = new Border { Classes = { "rfDocQuote" }, Child = body };
                    sp.Children.Add(q);
                    break;
                }
                default:
                    body.Classes.Add("rfDocLine");
                    sp.Children.Add(body);
                    break;
            }
        }

        return sp;
    }

    /// <summary>
    /// 块文本 SelectableTextBlock：普通 Run + 润色修正橙色胶囊（InlineUIContainer 内嵌，每个修正词独立交互），
    /// 定位与文本重建共用一套贪心算法（LocateEdits）。
    /// 原生 SelectableTextBlock 支持选区渲染，配合跨块选区控制器实现整篇连续划词。
    /// </summary>
    private SelectableTextBlock BuildBlockText(OcrLineItem item)
    {
        var tb = new SelectableTextBlock
        {
            SelectionBrush = TextSelectionBrush
        };
        string? plain = item.ReflowPlain;
        if (plain == null || item.ReflowEdits is not { Count: > 0 })
        {
            tb.Text = item.CurrentText;
            return tb;
        }

        var located = LocateEdits(plain, item.ReflowEdits);
        // 修正词字号跟随所在块类型（标题 16 / 段落与列表 13.5 / 独立行 12.5）
        double fixFontSize = item.ReflowRole switch { 'T' => 16, 'P' or 'L' or 'Q' => 13.5, _ => 12.5 };
        int cursor = 0;
        tb.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        foreach (var (pos, edit) in located)
        {
            if (pos > cursor)
                tb.Inlines.Add(new Avalonia.Controls.Documents.Run(plain[cursor..pos]));
            // 修正词：独立内嵌胶囊——悬停加深底色，点击弹出该词恢复气泡
            tb.Inlines.Add(new Avalonia.Controls.Documents.InlineUIContainer
            {
                BaselineAlignment = Avalonia.Media.BaselineAlignment.Baseline,
                Child = BuildFixWordPresenter(item, edit, fixFontSize)
            });
            cursor = pos + edit.Orig.Length;
        }
        if (cursor < plain.Length)
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(plain[cursor..]));
        return tb;
    }

    /// <summary>
    /// 样式化 Flyout：Avalonia 11.2.8 无 FlyoutPresenterStyle 属性——覆写 CreatePresenter，
    /// 在代码里直接配置 FlyoutPresenter（底色/描边/圆角/内边距），外观不依赖主题样式。
    /// </summary>
    private sealed class StyledFlyout : Flyout
    {
        private readonly Action<FlyoutPresenter> _configure;
        public StyledFlyout(Action<FlyoutPresenter> configure) => _configure = configure;

        protected override Control CreatePresenter()
        {
            var p = (FlyoutPresenter)base.CreatePresenter();
            _configure(p);
            return p;
        }
    }

    /// <summary>
    /// 修正词胶囊：橙字加粗 + 橙软底圆角胶囊（无角标，纯文字居中）。
    /// 悬停加深底色提示可交互；点击弹出该词的恢复气泡（原文 → 修正 + 理由 + 恢复）。
    /// 注意：旧版基于 XAML 样式类的实现在运行中背景丢失、字体回退，改为全部外观
    /// 走代码显式属性（含显式继承窗口字体与所在块字号），标准 / NativeAOT
    /// 构建下均经像素级验证稳定渲染——与 BuildFixPopContent 保持同一策略。
    /// </summary>
    private Control BuildFixWordPresenter(OcrLineItem item, ReflowEditView edit, double fontSize)
    {
        var fixBrush = EditBrush;
        var pillBrush = EditSoftBrush;
        var hoverBrush = EditHoverBrush;

        // 行高压缩档（16 标题 / 12.5 独立行，自然行高）：胶囊内 TextBlock 压 LineHeight
        // 消除行盒空隙——胶囊变矮后墨迹中心自然与胶囊中心、正文墨迹中心重合（像素实测
        // 三项偏差 ≤1.2px）；13.5 段落档正文行高锁定 LH25，胶囊高被行高拉住，压缩无收益。
        double? innerLh = fontSize >= 15 ? 16 : fontSize <= 13 ? 12 : null;
        var txt = new TextBlock
        {
            Text = edit.Fix,
            FontSize = fontSize,
            FontFamily = FontFamily,           // 显式继承窗口字体，避免内嵌控件字体回退
            FontWeight = FontWeight.Bold,
            Foreground = fixBrush,
            LineHeight = innerLh ?? double.NaN
        };

        // 胶囊外壳：软底 + 小圆角 + 手型光标，全部代码显式设置（悬停/离开切换底色）。
        // 对齐说明：InlineUIContainer 会把内嵌件裁剪到其布局盒——任何 RenderTransform/
        // 负 Margin 下沉越界的部分都会被裁掉（底部圆角消失）。因此胶囊底边贴正文基线
        // （Baseline 对齐）且不下沉，垂直位置全靠内部行高压缩 + padding 校准（见上）。
        var host = new ContinuousBorder
        {
            CornerRadius = ResRadius("SctRadiusSmall"),
            Padding = innerLh is null ? new(6, 2) : new(6, 3, 6, 1),
            Background = pillBrush,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = txt,
            Classes = { "rfFixWord" },
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromSeconds(0.12) }
            }
        };
        host.PointerEntered += (_, _) => host.Background = hoverBrush;
        host.PointerExited += (_, _) => host.Background = pillBrush;
        host.PointerPressed += (_, e) =>
        {
            if (!edit.Applied) return;
            // 单项气泡：只传该词的修正（BuildFixPopContent 单条时不显示"共 N 处"标题）；
            // 内容自带实底卡片外壳，presenter 须为裸壳（透明底 + 无边框）避免双重边框。
            // Padding 不是视觉留白：PopupRoot（独立弹窗）边界=内容边界，卡片 BoxShadow
            // 画到边界外的部分会被窗口硬裁（阴影变硬边）——四周留 ≥ 阴影扩散半径的空间供其渲染
            var flyout = new StyledFlyout(p =>
            {
                p.Background = Brushes.Transparent;
                p.BorderThickness = new Thickness(0);
                p.Padding = new Thickness(16, 14, 16, 20);
            })
            { Placement = PlacementMode.Bottom };
            flyout.Content = BuildFixPopContent(item, [edit], flyout);
            flyout.ShowAt(host);
            e.Handled = true;
        };
        return host;
    }

    /// <summary>润色修正定位（贪心顺序匹配，重叠丢弃）——渲染与文本重建共用，保证两视图一致。</summary>
    private static List<(int Pos, ReflowEditView Edit)> LocateEdits(string plain, IEnumerable<ReflowEditView> edits)
    {
        var result = new List<(int Pos, ReflowEditView Edit)>();
        foreach (var e in edits.Where(x => x.Applied))
        {
            int from = result.Count > 0 ? result[^1].Pos + result[^1].Edit.Orig.Length : 0;
            int pos = plain.IndexOf(e.Orig, from, StringComparison.Ordinal);
            if (pos >= from) result.Add((pos, e));
        }
        return result;
    }

    /// <summary>润色基准文本 + applied 修正 → 当前文本（恢复后重建亦走此方法）。</summary>
    private static string RebuildPolishedText(OcrLineItem item)
    {
        string plain = item.ReflowPlain ?? item.CurrentText;
        if (item.ReflowEdits is not { Count: > 0 }) return item.CurrentText;
        var sb = new System.Text.StringBuilder(plain.Length + 32);
        int cursor = 0;
        foreach (var (pos, edit) in LocateEdits(plain, item.ReflowEdits))
        {
            sb.Append(plain, cursor, pos - cursor).Append(edit.Fix);
            cursor = pos + edit.Orig.Length;
        }
        sb.Append(plain, cursor, plain.Length - cursor);
        return sb.ToString();
    }

    /// <summary>
    /// 修正对照气泡内容（紧凑版）：每处修正两行——
    /// 第一行「原文（红删除线胶囊）→ 修正（绿加粗胶囊）+ 右侧轻量恢复按钮」，
    /// 第二行理由（10.5px muted）；单项修正不显示标题（点击橙色修正词弹出，上下文已明确），
    /// 多项时顶部一行精简标题「✎ AI 修正对照 · 共 N 处」+ 各项之间细分隔线。
    /// 外壳为实底卡片（面板底 + 橙描边 + 投影）。气泡在 PopupRoot 内收不到窗口样式，
    /// 全部外观走代码显式属性，不依赖 XAML 样式类。
    /// </summary>
    private Control BuildFixPopContent(OcrLineItem item, List<ReflowEditView> applied, Flyout flyout)
    {
        var sp = new StackPanel { Spacing = 6 };

        // 标题行：仅多处修正时显示（单项省去，紧凑优先）
        if (applied.Count > 1)
        {
            var head = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            head.Children.Add(new TextBlock
            {
                Text = "✎",
                FontSize = 10.5,
                Foreground = EditBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            head.Children.Add(new TextBlock
            {
                Text = $"AI 修正对照 · 共 {applied.Count} 处",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = EditBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            sp.Children.Add(head);
        }

        for (int i = 0; i < applied.Count; i++)
        {
            if (i > 0)
                sp.Children.Add(new Border { Height = 1, Margin = new(0, -3), Background = ResBrush("OcrBorderBrush") });

            var edit = applied[i];

            // 第一行：对照区（左，可占满）+ 恢复按钮（右，固定宽）
            var row = new Grid { ColumnDefinitions = new("*,Auto") };

            // 对照区：原文胶囊 → 箭头 → 修正胶囊；长句在各自胶囊内换行，不撑破气泡
            var cmp = new Grid { ColumnDefinitions = new("Auto,Auto,*") };
            cmp.Children.Add(new ContinuousBorder
            {
                Background = ResBrush("OcrDangerSoftBrush"),
                CornerRadius = ResRadius("SctRadiusSmall"),
                Padding = new(6, 1),
                MaxWidth = 150,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = edit.Orig,
                    FontSize = 12,
                    Foreground = ResBrush("OcrDangerBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    TextDecorations = Avalonia.Media.TextDecorations.Strikethrough
                }
            });
            var arrow = new TextBlock
            {
                Text = "▶",
                FontSize = 8,
                Foreground = ResBrush("OcrTextMutedBrush"),
                Margin = new(5, 1, 5, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(arrow, 1);
            cmp.Children.Add(arrow);
            var fixPill = new ContinuousBorder
            {
                Background = ResBrush("OcrOkSoftBrush"),
                CornerRadius = ResRadius("SctRadiusSmall"),
                Padding = new(6, 1),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = edit.Fix,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Foreground = ResBrush("OcrOkBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            Grid.SetColumn(fixPill, 2);
            cmp.Children.Add(fixPill);
            row.Children.Add(cmp);

            // 恢复按钮：橙描边胶囊按钮（悬停橙实底白字，视觉醒目但仍轻量），点击仅恢复本处
            var undoBtn = new Button
            {
                Content = "↺ 恢复原文",
                Height = 24,
                Padding = new(10, 0),
                Margin = new(10, 0, 0, 0),
                CornerRadius = ResRadius("SctRadiusControl"),
                Background = Brushes.Transparent,
                BorderBrush = EditBrush,
                BorderThickness = new(1),
                Foreground = EditBrush,
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            undoBtn.PointerEntered += (_, _) =>
            {
                undoBtn.Background = EditBrush;
                undoBtn.Foreground = ResBrush("OcrPanelBrush");
            };
            undoBtn.PointerExited += (_, _) =>
            {
                undoBtn.Background = Brushes.Transparent;
                undoBtn.Foreground = EditBrush;
            };
            undoBtn.Click += (_, _) =>
            {
                edit.Applied = false;
                item.CurrentText = RebuildPolishedText(item);
                _undoFixCount++;
                flyout.Hide();
                RenderReflowStage();
                SyncEditedIndicesToOverlay();
                RefreshStatusBar();
                ShowToast("已恢复原文");
            };
            Grid.SetColumn(undoBtn, 1);
            row.Children.Add(undoBtn);

            sp.Children.Add(row);

            // 第二行：理由（10.5px muted，空理由跳过不占行）
            if (!string.IsNullOrWhiteSpace(edit.Why))
                sp.Children.Add(new TextBlock
                {
                    Text = edit.Why,
                    FontSize = 10.5,
                    LineHeight = 15,
                    Foreground = ResBrush("OcrTextMutedBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
        }

        // 气泡外壳：实底卡片（面板底 + 橙描边 + 圆角 + 投影），显式属性不依赖窗口样式。
        // 阴影偏移/模糊 ≤ presenter Padding 预留空间（下 4+14=18≤20，上/左右 14≤14/16），
        // 否则被 PopupRoot 窗口边界硬裁成硬边
        return new ContinuousBorder
        {
            Background = ResBrush("OcrPanelBrush"),
            BorderBrush = EditBrush,
            BorderThickness = new(1),
            CornerRadius = ResRadius("SctRadiusControl"),
            Padding = new(12, 10),
            MinWidth = 208,
            MaxWidth = 350,
            BoxShadow = this.TryFindResource("SctOcrFixShadow", ActualThemeVariant, out var shadow) && shadow is BoxShadows shadows ? shadows : default,
            Child = sp
        };
    }

    // ═══ 润色修正挂载（AI edits → 块卡片） ═══

    /// <summary>
    /// 把 AI 润色修正挂载到块卡片：ReflowPlain 记录未润色基准，ReflowEdits 保存修正清单，
    /// CurrentText 重建为含修正的文本（复制/导出自然携带）。定位不到的修正静默丢弃（软校验）。
    /// </summary>
    private void ApplyPolishEditsToItems(ReflowResult result, List<AiReflowEdit> aiEdits)
    {
        if (aiEdits == null || aiEdits.Count == 0) return;

        foreach (var ed in aiEdits)
        {
            if (ed.BlockIndex < 0 || ed.BlockIndex >= result.Blocks.Count) continue;
            if (_blockToItems is null || !_blockToItems.TryGetValue(ed.BlockIndex, out var itemIdxs)) continue;

            foreach (var ii in itemIdxs)
            {
                var it = _reflowItems[ii];
                it.ReflowPlain ??= it.CurrentText;
                // 软校验：定位不到 → 丢弃；列表项行首标记已剥离，AI orig 若含原标记（如「• 设计」）剥后再试一次
                string orig = ed.Orig;
                if (it.ReflowPlain.IndexOf(orig, StringComparison.Ordinal) < 0)
                {
                    orig = OcrReflowEngine.StripListMarker(orig);
                    if (orig.Length == 0 || it.ReflowPlain.IndexOf(orig, StringComparison.Ordinal) < 0) continue;
                }
                it.ReflowEdits ??= new List<ReflowEditView>();
                it.ReflowEdits.Add(new ReflowEditView { Orig = orig, Fix = ed.Fix, Why = ed.Why });
                break;   // 一个修正只归属第一个包含它的卡片
            }
        }

        foreach (var it in _reflowItems)
        {
            if (it.ReflowEdits is { Count: > 0 })
                it.CurrentText = RebuildPolishedText(it);
        }
    }

    // ═══ 进度与计时 ═══

    private double _rfAiExpectedSec = 6;   // AI 推理预期时长（秒），从上次实际耗时学习，首次默认 6
    private double _rfAiStartSec;          // AI 调用开始时的秒表读数（曲线归一化起点）

    // 排查日志状态（[RfProg] 系列，临时）：tick 计数/间隔用于检测计时器饿死
    private long _rfTickTotal;
    private long _rfTickLastMs = -1;
    private long _rfTickMaxGapMs;
    private int _rfTickLogSeq;             // tick 节流序号（每 5 个 tick ≈0.5s 记一条）
    private Avalonia.Controls.Border? _rfIndicator;   // PART_Indicator 惰性缓存（渲染层测量）
    private Avalonia.Controls.Shapes.Rectangle? _rfShimmer;  // 流光（渲染时钟探针）

    private void StartReflowClock()
    {
        StopReflowClock();
        _rfTickTotal = 0;
        _rfTickLastMs = -1;
        _rfTickMaxGapMs = 0;
        _rfTickLogSeq = 0;
        // 优先级必须 ≥ Render：进度条流光是 INFINITE 动画（动画时钟持续派发 Render 级作业），
        // AI 流式回调也以 Normal 级回流——默认 Background 级的 DispatcherTimer 会被彻底饿死，
        // 整个 AI 期间一次都不 tick（进度条钉死在 16%、计时不动），AI 返回后才恢复。
        // 同 SelectionLayer.StartAnimationTimer 的先例：动画并行场景计时器显式提 Render 级。
        _reflowClock = new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            Avalonia.Threading.DispatcherPriority.Render,
            (_, _) =>
            {
                // 饿死检测：tick 实际间隔 > 300ms（名义 100ms）即调度异常，立即 WARN
                long nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
                if (_rfTickLastMs >= 0)
                {
                    long gap = nowMs - _rfTickLastMs;
                    if (gap > _rfTickMaxGapMs) _rfTickMaxGapMs = gap;
                    if (gap > 300)
                        AppLogger.Warn($"[RfProg] 计时器饿死嫌疑：tick 间隔 {gap}ms（名义 100ms），累计 tick={_rfTickTotal + 1}");
                }
                _rfTickLastMs = nowMs;
                _rfTickTotal++;

                if (_rfTime != null && _reflowSw != null)
                    _rfTime.Text = _reflowSw.Elapsed.TotalSeconds.ToString("0.0") + "s";
                // AI 推理阶段：进度按「预期时长归一化的二次缓出曲线」推进（16 → 80）——
                // AI 快返回时同样走完大半行程（不再只爬一小段），AI 慢时曲线到点后
                // 以 0.4%/s 缓爬至 88 封顶（长等待不停滞、也不越过后续里程碑太多）
                if (_rfCurrentStep == 1 && _rfProg != null && _reflowSw != null)
                {
                    double t = _reflowSw.Elapsed.TotalSeconds - _rfAiStartSec;
                    double v = t < _rfAiExpectedSec
                        ? 16 + 64 * (1 - (1 - t / _rfAiExpectedSec) * (1 - t / _rfAiExpectedSec))
                        : Math.Min(88, 80 + (t - _rfAiExpectedSec) * 0.4);
                    if (v > _rfProg.Value) _rfProg.Value = v;

                    // tick 节流日志（每 5 tick ≈0.5s 一条）：逻辑层(Value) + 渲染层(指示条宽度/流光位置)对照。
                    // 渲染层测量用于定位"Value 爬升但视觉冻结"类问题：
                    //   indW=indicator.Width 属性值（应≈Value%×轨道宽） | indB=Bounds 渲染宽度
                    //   shimmer=流光 TranslateX（-80~260 循环，若不动=渲染时钟死）
                    if (++_rfTickLogSeq % 5 == 0)
                    {
                        EnsureRfProbeRefs();
                        double trackW = _rfProg.Bounds.Width;
                        double indW = _rfIndicator?.Width ?? -1;
                        double indB = _rfIndicator?.Bounds.Width ?? -1;
                        double shim = (_rfShimmer?.RenderTransform as Avalonia.Media.TranslateTransform)?.X ?? -999;
                        AppLogger.Info($"[RfProg] tick#{_rfTickTotal} AI阶段 t={t:0.0}s 预期={_rfAiExpectedSec:0.0}s 曲线值={v:0.0} Value={_rfProg.Value:0.0} | 轨道={trackW:0}px indW={indW:0}px indB={indB:0}px 流光X={shim:0}");
                    }
                }
            });
        _reflowClock.Start();
        AppLogger.Info($"[RfProg] 计时器启动：间隔100ms 优先级=Render（tick 计数从 0 开始）");
    }

    /// <summary>惰性捕获进度条模板内部引用（[RfProg] 渲染层测量用，临时排查代码）。</summary>
    private void EnsureRfProbeRefs()
    {
        if (_rfIndicator != null && _rfShimmer != null) return;
        if (_rfProg == null) return;
        foreach (var d in _rfProg.GetVisualDescendants())
        {
            if (_rfIndicator == null && d is Avalonia.Controls.Border b && b.Name == "PART_Indicator")
                _rfIndicator = b;
            else if (_rfShimmer == null && _rfIndicator != null && d is Avalonia.Controls.Shapes.Rectangle r)
                _rfShimmer = r;   // 指示条内首个 Rectangle 即流光
        }
    }

    private void StopReflowClock()
    {
        if (_reflowClock == null) return;
        _reflowClock.Stop();
        _reflowClock = null;
        // 停止摘要：tick 总数 / 最大间隔 / 最终进度——若 maxGap 远超 100ms 或总数远低于
        // 时长÷0.1s，说明期间出现过饿死（旧 Background 级问题的特征指纹）
        AppLogger.Info($"[RfProg] 计时器停止：tick总数={_rfTickTotal} 最大间隔={_rfTickMaxGapMs}ms 最终进度={(_rfProg?.Value ?? -1):0.0}");
    }

    private void SetReflowProgress(double value)
    {
        // 单调推进：AI 阶段缓爬值可能已越过后续里程碑设值，绝不回退
        double from = _rfProg?.Value ?? -1;
        if (_rfProg != null && value > _rfProg.Value) _rfProg.Value = value;
        AppLogger.Info($"[RfProg] 设值 {from:0.0} → {value:0.0}{(value <= from ? "（被单调守卫跳过）" : "")}");
    }

    /// <summary>推进步骤芯片：index=0..3 进行中，4=全部完成。</summary>
    private void SetReflowStep(int step)
    {
        _rfCurrentStep = step;
        string[] names = { "收集行数据", "AI 推理", "校验决策", "应用排版", "完成" };
        AppLogger.Info($"[RfProg] 步骤切换 → {step}({names[Math.Clamp(step, 0, 4)]})");
        for (int i = 0; i < _rfStepChips.Count; i++)
        {
            _rfStepChips[i].Classes.Set("doing", i == step);
            _rfStepChips[i].Classes.Set("done", i < step);
        }
    }

    // ═══ 浮动工具栏：连通检测 / pill 开关 ═══

    /// <summary>连通状态灯点击：手动重新检测。</summary>
    private void OnConnLampClick(object? sender, PointerPressedEventArgs e)
    {
        CheckReflowConn();
        e.Handled = true;
    }

    /// <summary>轻量 ping 大模型（GET /models，8s 超时）：绿灯=已连接（模型名+延迟），红灯=未连接（禁用开始按钮）。</summary>
    private async void CheckReflowConn()
    {
        if (_connChecking) return;
        _connChecking = true;
        UpdateConnLampUI(checking: true);

        if (!AiReflowService.IsConfigured())
        {
            _connOk = false;
            _connFailMsg = "检查设置 → AI 识图";
            _connChecking = false;
            UpdateConnLampUI();
            return;
        }

        var s = AppSettings.Current;
        var protocol = (string.Equals(s.AiProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.AiProtocol, "Claude", StringComparison.OrdinalIgnoreCase))
            ? AiProtocolType.Anthropic
            : AiProtocolType.OpenAI;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var r = await AiVisionService.Instance.TestConnectionAndFetchModelsAsync(protocol, s.AiBaseUrl, s.GetEffectiveApiKey(), cts.Token);
            _connOk = r.IsSuccess;
            _connLatencyMs = (int)Math.Min(int.MaxValue, r.LatencyMs);
            _connFailMsg = r.IsSuccess ? "" : r.Message;
        }
        catch (Exception ex)
        {
            _connOk = false;
            _connFailMsg = ex.Message;
        }
        _connChecking = false;
        UpdateConnLampUI();
    }

    private int _connLatencyMs;

    /// <summary>更新连通灯 UI（呼吸绿 / 红 / 检测中灰）与开始按钮可用性。</summary>
    private void UpdateConnLampUI(bool checking = false)
    {
        if (ConnLamp == null || TxtConnState == null || TxtConnSub == null) return;

        string lampCls = checking ? "lampChecking" : _connOk ? "lampOk" : "lampBad";
        ConnLamp.Classes.Set("lampChecking", lampCls == "lampChecking");
        ConnLamp.Classes.Set("lampOk", lampCls == "lampOk");
        ConnLamp.Classes.Set("lampBad", lampCls == "lampBad");

        if (checking)
        {
            TxtConnState.Text = "检测中…";
            TxtConnSub.Text = "AI 智能排版服务";
            return;
        }

        if (_connOk)
        {
            TxtConnState.Text = "AI 已连接";
            TxtConnSub.Text = $"{AppSettings.Current.AiModel} · {_connLatencyMs}ms";
        }
        else
        {
            TxtConnState.Text = "AI 未连接";
            string msg = _connFailMsg ?? "";
            if (msg.Length > 26) msg = msg[..26] + "…";
            TxtConnSub.Text = msg.Length > 0 ? msg : "点击灯重新检测";
        }

        if (BtnStartReflow != null && _reflowStage != ReflowStage.Running)
            BtnStartReflow.IsEnabled = _connOk;
    }

    /// <summary>润色药丸：点击弹出级别菜单（关闭 / Ⅰ 仅错字 / Ⅱ 含补全 / Ⅲ 深度修正），一级比一级激进。</summary>
    private void OnPolishLampClick(object? sender, PointerPressedEventArgs e)
    {
        // 同修正气泡（BuildFixPopContent）策略：PopupRoot 内样式类与 Button 模板不可靠——
        // 选项用 Border + 指针事件实现（无模板裁剪），字号全部取整（小数字号在部分 DPI
        // 下行盒小于渲染高度会裁半行）。内容栈固定宽度防弹层无限宽测量。
        // 极简单行结构：圆点 + 级别名 + 右对齐微描述，无描述第二行/徽章/底注。
        var flyout = new StyledFlyout(p =>
        {
            p.Background = ResBrush("SctCardFillBrush");
            p.BorderBrush = ResBrush("SctSoftBorderBrush");
            p.BorderThickness = new Thickness(1);
            p.CornerRadius = ResRadius("SctRadiusControl");
            p.Padding = new Thickness(6);
        })
        { Placement = PlacementMode.Top };
        var sp = new StackPanel { Width = 212, Spacing = 1 };

        // 微标题
        sp.Children.Add(new TextBlock
        {
            Text = "润色级别",
            FontSize = 10,
            Foreground = ResBrush("SctTextMutedBrush"),
            Margin = new(9, 3, 9, 4)
        });

        // 单行选项：选中=软紫底 + 实心紫点 + 紫色级别名；未选=透明底 + 空心灰环
        void Opt(int level, string title, string desc)
        {
            var cur = level == _polishLevel;
            var card = new ContinuousBorder
            {
                Background = cur ? ResBrush("SctBadgeBgBrush") : Brushes.Transparent,
                CornerRadius = ResRadius("SctRadiusControl"),
                Padding = new(9, 7),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            card.PointerEntered += (_, _) => card.Background = ResBrush("SctBadgeBgBrush");
            card.PointerExited += (_, _) => card.Background = cur ? ResBrush("SctBadgeBgBrush") : Brushes.Transparent;

            var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto") };

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new(0, 0, 9, 0),
                Fill = cur ? ResBrush("SctAccentBrush") : null,
                Stroke = cur ? null : ResBrush("SctSoftBorderBrush"),
                StrokeThickness = 1.2
            };
            row.Children.Add(dot);

            var name = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = ResBrush(cur ? "SctBadgeTextBrush" : "SctTextPrimaryBrush")
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var briefTb = new TextBlock
            {
                Text = desc,
                FontSize = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = ResBrush("SctTextMutedBrush")
            };
            Grid.SetColumn(briefTb, 3);
            row.Children.Add(briefTb);

            card.Child = row;
            card.Tapped += (_, _) =>
            {
                if (_polishLevel == level) { flyout.Hide(); return; }
                _polishLevel = level;
                UpdatePolishPill();
                flyout.Hide();
                if (_reflowStage == ReflowStage.Done && _reflowApplied)
                    ShowToast($"润色已设为{PolishLevelName()} · 重新排版生效");
            };
            sp.Children.Add(card);
        }

        Opt(0, "关闭", "仅排版");
        sp.Children.Add(new Rectangle { Height = 1, Fill = ResBrush("SctSoftBorderBrush"), Margin = new(8, 2, 8, 2) });
        Opt(1, "轻度", "仅修错字");
        Opt(2, "中度", "错字 + 补全");
        Opt(3, "深度", "全面治理");

        flyout.Content = sp;
        flyout.ShowAt(LampPolish);
        e.Handled = true;
    }

    /// <summary>刷新润色药丸视觉：灭=灰；亮=紫 + 级别罗马数字。</summary>
    private void UpdatePolishPill()
    {
        if (LampPolish == null || TxtPolishLabel == null) return;
        bool on = PolishOn;
        LampPolish.Classes.Set("on", on);
        TxtPolishLabel.Text = on ? $"润色 {PolishLevelRoman()}" : "润色";
    }

    /// <summary>MD 排版状态灯：点击切换亮/灭；开启 = 完成态文本框显示 Markdown 源码、复制按钮输出 MD 语法（与显示同源）。</summary>
    private void OnMdLampClick(object? sender, PointerPressedEventArgs e)
    {
        _mdOn = !_mdOn;
        if (LampMd != null) LampMd.Classes.Set("on", _mdOn);
        if (_reflowStage == ReflowStage.Done) RenderReflowStage();
    }

    /// <summary>AI 输入行：文本 + 可选几何（BoxIndex 有效）+ OCR 置信度。AI 不依赖几何也能排（语义优先）。</summary>
    private List<AiReflowInputLine> BuildAiInputLines()
    {
        var list = new List<AiReflowInputLine>(_lineItems.Count);
        foreach (var l in _lineItems)
        {
            var line = new AiReflowInputLine { Text = l.CurrentText };
            if (_boxes != null && l.BoxIndex >= 0 && l.BoxIndex < _boxes.Count)
            {
                var b = _boxes[l.BoxIndex];
                line.HasGeometry = true;
                line.X = b.Bounds.X;
                line.Y = b.Bounds.Y;
                line.H = b.Bounds.Height;
                line.Score = b.Score;
            }
            list.Add(line);
        }
        return list;
    }

    /// <summary>
    /// AI 决策 → 排版块骨架（Sources 行号引用；文本由 ApplyReflowToItems 统一按行项当前文本拼接，
    /// 保证块内容 100% 来自 OCR 原文而非 LLM 输出）。
    /// 列表块本地二次分组：① 相邻 List 块（ordered 一致）合并为一个列表——AI 常把同一列表按项切成
    /// 多块，逐块重置序号会排出 1-5/1-5/1-3 的乱跳编号；② 按行首标记分组——带标记行开启新项，
    /// 无标记行视为上一项的 OCR 断行续行并入当前项（防续行被凭空编成独立列表项）；③ 整块无任何
    /// 标记行 → AI 列表证据不足，降级段落（宁可不排错）；④ Para 结构兜底——≥2 行行首带标记的
    /// 段落强制升级列表（AI 漏判 · 中点等 OCR 圆点标记最常见，本地标记证据确定性兜底）。
    /// </summary>
    private static ReflowResult ConvertAiDecision(AiReflowDecision decision, IReadOnlyList<AiReflowInputLine> input)
    {
        double medH = MedianHeight(input.Where(l => l.HasGeometry).Select(l => l.H));
        var blocks = new List<ReflowBlock>();

        // 列表块构建：pool（行号+深度）→ 按行首标记分组项；带标记行开新项、无标记续行并入当前项、
        // 列表前无标记孤儿行独立成段；全无标记整块降级段落（返回的单块 Para 列表）。
        List<ReflowBlock> BuildList(List<(int Line, int Depth)> pool, bool ordered, int blockDepth)
        {
            var result = new List<ReflowBlock>();
            var items = new List<(int[] Sources, string Text, int Depth)>();
            List<int>? cur = null;
            int curDepth = 0;
            var lead = new List<int>();
            foreach (var (ln, depth) in pool)
            {
                if (OcrReflowEngine.HasListMarker(input[ln].Text))
                {
                    if (cur != null) items.Add((cur.ToArray(), string.Empty, curDepth));
                    cur = new List<int> { ln };
                    curDepth = depth;
                }
                else if (cur != null)
                {
                    cur.Add(ln);
                }
                else
                {
                    lead.Add(ln);
                }
            }
            if (cur != null) items.Add((cur.ToArray(), string.Empty, curDepth));

            if (items.Count == 0)
                return new List<ReflowBlock> { new(ReflowBlockType.Para, pool.Select(p => p.Line),
                    string.Empty, IsBigTitle: false) };
            if (lead.Count > 0)
                result.Add(new ReflowBlock(ReflowBlockType.Para, lead, string.Empty, IsBigTitle: false));
            result.Add(new ReflowBlock(ReflowBlockType.List, pool.Select(p => p.Line), string.Empty,
                IsBigTitle: false, items)
                { Ordered = ordered, Depth = blockDepth });
            return result;
        }

        for (int bi = 0; bi < decision.Blocks.Count; bi++)
        {
            var b = decision.Blocks[bi];
            switch (b.Type)
            {
                case AiReflowBlockType.Title:
                {
                    double h = input[b.Lines[0]].HasGeometry ? input[b.Lines[0]].H : 0;
                    blocks.Add(new ReflowBlock(ReflowBlockType.Title, b.Lines, string.Empty, h >= medH * 1.22)
                        { Level = b.Level });
                    break;
                }
                case AiReflowBlockType.Para:
                {
                    // 结构兜底：AI 把带标记的连续行误判成段落（OCR 圆点 · 不在提示词示例里最常见）——
                    // ≥2 行行首带标记 = 确定性列表证据，不依赖 AI 眼力，强制升级列表（有序/无序按标记多数表决）
                    int marked = b.Lines.Count(ln => OcrReflowEngine.HasListMarker(input[ln].Text));
                    if (marked >= 2)
                    {
                        int orderedCnt = b.Lines.Count(ln => OcrReflowEngine.HasOrderedMarker(input[ln].Text));
                        blocks.AddRange(BuildList(b.Lines.Select(ln => (ln, 0)).ToList(),
                            orderedCnt > marked - orderedCnt, 0));
                        break;
                    }
                    blocks.Add(new ReflowBlock(ReflowBlockType.Para, b.Lines, string.Empty, IsBigTitle: false));
                    break;
                }
                case AiReflowBlockType.List:
                {
                    // ① 相邻 List 块合并（项级深度保留来源块 depth——嵌套列表 = AI 判 depth 0/1 两个块相邻）
                    var pool = new List<(int Line, int Depth)>();
                    for (int k = bi; k < decision.Blocks.Count
                         && decision.Blocks[k].Type == AiReflowBlockType.List
                         && decision.Blocks[k].Ordered == b.Ordered; k++)
                    {
                        foreach (var ln in decision.Blocks[k].Lines)
                            pool.Add((ln, decision.Blocks[k].Depth));
                        bi = k;
                    }

                    // ② 按行首标记分组构建（含降级段与孤儿段处理）
                    blocks.AddRange(BuildList(pool, b.Ordered, b.Depth));
                    break;
                }
                case AiReflowBlockType.Code:
                    blocks.Add(new ReflowBlock(ReflowBlockType.Code, b.Lines, string.Empty, IsBigTitle: false)
                        { Lang = b.Lang });
                    break;
                case AiReflowBlockType.Quote:
                    blocks.Add(new ReflowBlock(ReflowBlockType.Quote, b.Lines, string.Empty, IsBigTitle: false));
                    break;
                default:
                    blocks.Add(new ReflowBlock(ReflowBlockType.Line, b.Lines, string.Empty, IsBigTitle: false));
                    break;
            }
        }
        return new ReflowResult(blocks);
    }

    /// <summary>
    /// 符号风格算子：LLM 只选风格枚举，文本变换由本地 OcrTextOps 确定性算子组合执行。
    /// 作用于排版块文本（拼接后整体变换，引号配对等上下文感知更准）——行列表保持原始 OCR 不动。
    /// </summary>
    private static Func<string, string>? BuildStyleOp(AiReflowStyleDecision? styles)
    {
        if (styles == null) return null;

        Func<string, string>? op = null;
        void Add(Func<string, TextOpResult> f)
        {
            Func<string, string>? prev = op;
            op = prev == null ? x => f(x).Text : x => f(prev(x)).Text;
        }

        if (styles.Quote == "straight") Add(OcrTextOps.ToStraightQuotes);
        if (styles.Quote == "curly") Add(OcrTextOps.ToCurlyQuotes);
        if (styles.Quote == "corner") Add(OcrTextOps.ToCornerQuotes);
        if (styles.Paren == "english") Add(OcrTextOps.ToEnglishParentheses);
        if (styles.Paren == "chinese") Add(OcrTextOps.ToChineseParentheses);
        if (styles.Punct == "chinese") Add(OcrTextOps.ToChinesePunctuation);
        if (styles.Punct == "english") Add(OcrTextOps.ToEnglishPunctuation);

        return op;
    }

    /// <summary>清空排版结果并复位状态（重新排版 / 新识别时调用；行列表不受影响）。</summary>
    private void ResetReflowResult()
    {
        _reflowItems.Clear();
        _blockToItems = null;
        _reflowSourceTexts = null;
        _reflowApplied = false;
    }

    /// <summary>
    /// 把排版块构建进独立结果列表 _reflowItems（行列表不动——分行/合并始终编辑原始 OCR 行）。
    /// 块文本统一按行项当前文本拼接（符号风格算子在拼接后整体执行）——保证块内容
    /// 100% 来自 OCR 原文而非任何引擎输出；OriginalText 用「原始行按同规则拼接」
    /// → 已改橙色高亮仅反映真实编辑。
    /// </summary>
    private void ApplyReflowToItems(ReflowResult result, Func<string, string>? styleOp)
    {
        // 源文本快照：排版后行编辑 → 状态条「原文已修改」过期提示
        _reflowSourceTexts = _lineItems.Select(l => l.CurrentText).ToList();

        var curTexts = _lineItems.Select(l => styleOp != null ? styleOp(l.CurrentText) : l.CurrentText).ToArray();
        var orgTexts = _lineItems.Select(l => styleOp != null ? styleOp(l.OriginalText) : l.OriginalText).ToArray();
        var boxIdx = _lineItems.Select(l => l.BoxIndex).ToArray();

        _reflowItems.Clear();
        _blockToItems = new Dictionary<int, List<int>>();
        int idx = 0;
        for (int bi = 0; bi < result.Blocks.Count; bi++)
        {
            var b = result.Blocks[bi];
            var items = new List<int>();
            switch (b.Type)
            {
                case ReflowBlockType.Title:
                {
                    items.Add(idx);
                    var it = AddBlockCard(idx++, 'T', curTexts[b.Sources[0]], orgTexts[b.Sources[0]], b.Sources, boxIdx);
                    it.ReflowTitleLevel = OcrReflowEngine.TitleLevelOf(b.Level, b.IsBigTitle);
                    break;
                }
                case ReflowBlockType.Para:
                {
                    items.Add(idx);
                    string text = OcrReflowEngine.JoinLines(b.Sources.Select(i => curTexts[i]));
                    string original = OcrReflowEngine.JoinLines(b.Sources.Select(i => orgTexts[i]));
                    AddBlockCard(idx++, 'P', text, original, b.Sources, boxIdx);
                    break;
                }
                case ReflowBlockType.List when b.Items != null:
                {
                    int ordinal = 0;
                    foreach (var (sources, _, depth) in b.Items)
                    {
                        items.Add(idx);
                        string text = sources.Length == 1 ? curTexts[sources[0]]
                            : OcrReflowEngine.JoinLines(sources.Select(i => curTexts[i]));
                        string original = sources.Length == 1 ? orgTexts[sources[0]]
                            : OcrReflowEngine.JoinLines(sources.Select(i => orgTexts[i]));
                        // 剥离行首 OCR 列表标记：列表语义由标记前缀（•/1.）统一呈现，防「- - 项」双写
                        text = OcrReflowEngine.StripListMarker(text);
                        original = OcrReflowEngine.StripListMarker(original);
                        var it = AddBlockCard(idx++, 'L', text, original, sources, boxIdx);
                        it.ReflowListOrdered = b.Ordered;
                        it.ReflowListDepth = depth;   // 项级深度（嵌套列表 = 相邻块合并后各自保留来源深度）
                        if (b.Ordered) it.ReflowListOrdinal = ++ordinal;   // 有序：合并后整列表统一 1 基编号
                    }
                    break;
                }
                case ReflowBlockType.Code:
                {
                    // 代码块：按原始分行拼接（\n）——保留代码的换行/缩进语义，
                    // 不做 JoinLines 的盘古之白/断词合并（那会破坏代码）
                    items.Add(idx);
                    string text = string.Join("\n", b.Sources.Select(i => curTexts[i]));
                    string original = string.Join("\n", b.Sources.Select(i => orgTexts[i]));
                    AddBlockCard(idx++, 'C', text, original, b.Sources, boxIdx).ReflowCodeLang = b.Lang;
                    break;
                }
                case ReflowBlockType.Quote:
                {
                    items.Add(idx);
                    string text = OcrReflowEngine.JoinLines(b.Sources.Select(i => curTexts[i]));
                    string original = OcrReflowEngine.JoinLines(b.Sources.Select(i => orgTexts[i]));
                    AddBlockCard(idx++, 'Q', text, original, b.Sources, boxIdx);
                    break;
                }
                case ReflowBlockType.Line:
                    items.Add(idx);
                    AddBlockCard(idx++, 'O', curTexts[b.Sources[0]], orgTexts[b.Sources[0]], b.Sources, boxIdx);
                    break;
            }
            _blockToItems[bi] = items;   // AI edits 的 blockIndex → 块卡片行项索引
        }
    }

    /// <summary>新增一张排版块卡片（SourceBoxIndices=块内全部源框，BoxIndex=首个有效源框）。</summary>
    private OcrLineItem AddBlockCard(int index, char role, string text, string original, IReadOnlyList<int> sources, int[] boxIdx)
    {
        var boxIndices = sources
            .Where(i => i >= 0 && i < boxIdx.Length && boxIdx[i] >= 0)
            .Select(i => boxIdx[i])
            .ToArray();
        int firstBox = boxIndices.Length > 0 ? boxIndices[0] : -1;

        var item = new OcrLineItem(this, index, text, original, firstBox, boxIndices.Length > 0 ? boxIndices : null)
        {
            ReflowRole = role
        };
        item.SetGlobalEditMode(_isEditMode);
        _reflowItems.Add(item);
        return item;
    }

    private static double MedianHeight(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        if (s.Length == 0) return 20;
        int m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }

    /// <summary>
    /// 输出用全文（复制/导出）：排版态下若开启 Markdown 排版则输出标准 Markdown 语法，
    /// 否则排版态为结构化纯文本（标题/段落后空行），其余为普通行拼接。
    /// </summary>
    private string GetAllTextForOutput()
    {
        SyncMergedTextIfActive();
        if (ReflowOutputActive && _mdOn)
        {
            return BuildMarkdownFromItems().Replace("\n", Environment.NewLine);
        }
        return GetAllTextForOutputPlain().Replace("\n", Environment.NewLine);
    }

    /// <summary>
    /// 排版态 Markdown 真渲染器：按块角色生成标准语法——
    /// 标题 #/##/### 按 AI 层级 / 段落与引用空行分隔 / 列表转有序编号或 - 圆点并按深度缩进嵌套
    /// （行首 OCR 原有标记先剥离，防「- - 项」「1. 1. 项」双写）/ 代码块转围栏（带语言标注）/ 引用转 &gt; 前缀。
    /// </summary>
    private string BuildMarkdownFromItems()
    {
        var sb = new System.Text.StringBuilder();
        char prevRole = '\0';
        foreach (var item in _reflowItems)
        {
            string text = item.CurrentText;
            switch (item.ReflowRole)
            {
                case 'T':
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    int lv = Math.Clamp(item.ReflowTitleLevel <= 0 ? 2 : item.ReflowTitleLevel, 1, 3);
                    sb.Append('#', lv).Append(' ').Append(text);
                    break;
                }
                case 'L':
                {
                    // 相邻列表项单换行续写成一个列表；列表块与其他块之间空行分隔。
                    // 嵌套缩进每层 3 空格：CommonMark 下同时满足 - 父项(≥2)与 1. 父项(≥3)
                    if (sb.Length > 0) sb.Append(prevRole == 'L' ? '\n' : "\n\n");
                    sb.Append(' ', Math.Clamp(item.ReflowListDepth, 0, 2) * 3)
                      .Append(item.ReflowListOrdered ? $"{item.ReflowListOrdinal}. " : "- ")
                      .Append(OcrReflowEngine.StripListMarker(text));
                    break;
                }
                case 'C':
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append("```").Append(item.ReflowCodeLang ?? string.Empty).Append('\n')
                      .Append(text.Replace("\r\n", "\n"))
                      .Append("\n```");
                    break;
                }
                case 'Q':
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append("> ").Append(text.Replace("\r\n", "\n").Replace("\n", "\n> "));
                    break;
                }
                default:
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(text);
                    break;
                }
            }
            prevRole = item.ReflowRole;
        }
        return sb.ToString();
    }

    #endregion

    /// <summary>
    /// 合并视图激活时，把整段编辑结果同步回行列表，保证复制/导出/智能排版取到最新文本。
    /// 文本未改动时跳过重建（保留行卡片的 BoxIndex 联动与已改基准）；
    /// 改动只影响行列表——排版结果独立保留（状态条提示「原文已修改」）。
    /// </summary>
    private void SyncMergedTextIfActive()
    {
        if (_isMergedMode && TxtAllLines != null)
        {
            if (TxtAllLines.Text == _mergedTextAtSwitch) return;

            RebuildLineItemsFromText(TxtAllLines.Text);
            // 同步后基准前移：后续切回分行不再重复重建（行列表已与文本一致）
            _mergedTextAtSwitch = TxtAllLines.Text;
            SyncEditedIndicesToOverlay();
            RefreshStatusBar();
        }
    }

    /// <summary>
    /// 复制全部文本（含修改 / 随 Markdown 排版开关自适应）
    /// </summary>
    private async void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        SyncMergedTextIfActive();
        if (_lineItems.Count == 0) return;
        string allText = GetAllTextForOutput();
        try
        {
            ClipboardService.SetText(allText);
            // 单位随实际输出形态：排版视图完成态 = 块，其余（分行/合并/初态）= 行
            bool useRef = ReflowOutputActive;
            bool hasEdited = useRef ? _reflowItems.Any(l => l.IsEdited) : _lineItems.Any(l => l.IsEdited);
            string unit = useRef ? "块" : "行";
            int count = useRef ? _reflowItems.Count : _lineItems.Count;
            string mdHint = (useRef && _mdOn) ? "（Markdown格式）" : (hasEdited ? "（含修正）" : "");
            ShowToast($"已复制全部 {count} {unit}文本{mdHint}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OcrWindow.OnCopyAllClick 异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 导出纯文本 TXT
    /// </summary>
    private async void OnExportTxtClick(object? sender, RoutedEventArgs e)
    {
        SyncMergedTextIfActive();
        if (_lineItems.Count == 0) return;
        string content = GetAllTextForOutput();

        try
        {
            var options = new FilePickerSaveOptions
            {
                Title = "导出纯文本",
                SuggestedFileName = "ocr-result.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("纯文本文件 (*.txt)") { Patterns = new[] { "*.txt" } }
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                // UTF-8 无 BOM：Encoding.UTF8 属性会写 BOM（EF BB BF），
                // Mark Text 等编辑器对 BOM 文件解析异常（n.splice 报错打不开）
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync(content);
                bool hasEdited = _lineItems.Any(l => l.IsEdited);
                ShowToast($"已导出 TXT 文件{(hasEdited ? "（含修正）" : "")}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("导出 TXT 异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 导出 Markdown
    /// </summary>
    private async void OnExportMdClick(object? sender, RoutedEventArgs e)
    {
        SyncMergedTextIfActive();
        if (_lineItems.Count == 0) return;
        // 排版视图完成态：标准语法（标题分级/列表/代码围栏/引用）；
        // 其余（分行/合并/初态）：每行一段（不加硬编码 H1）
        string content = ReflowOutputActive
            ? BuildMarkdownFromItems()
            : string.Join("\n\n", _lineItems.Select(l => l.CurrentText));

        try
        {
            var options = new FilePickerSaveOptions
            {
                Title = "导出 Markdown",
                SuggestedFileName = "ocr-result.md",
                DefaultExtension = "md",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Markdown 文件 (*.md)") { Patterns = new[] { "*.md" } }
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                // UTF-8 无 BOM：Encoding.UTF8 属性会写 BOM（EF BB BF），
                // Mark Text 等编辑器对 BOM 文件解析异常（n.splice 报错打不开）
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync(content);
                bool hasEdited = _lineItems.Any(l => l.IsEdited);
                ShowToast($"已导出 Markdown 文件{(hasEdited ? "（含修正）" : "")}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("导出 Markdown 异常: " + ex.Message);
        }
    }

    private void OnExportDocxClick(object? sender, RoutedEventArgs e)
    {
        ShowToast("「Word」为原型占位，真实导出需接入后端");
    }

    private void OnExportPdfClick(object? sender, RoutedEventArgs e)
    {
        ShowToast("「PDF」为原型占位，真实导出需接入后端");
    }

    /// <summary>
    /// 联动高亮滑块开关切换
    /// </summary>
    private void OnLinkageSwitchChanged(object? sender, RoutedEventArgs e)
    {
        if (TglLinkage == null) return;

        bool enabled = TglLinkage.IsChecked == true;
        if (enabled == _linkageEnabled) return;   // 程序化拨动时去重

        _linkageEnabled = enabled;
        HighlightOverlay.LinkageEnabled = _linkageEnabled;
        if (!_linkageEnabled)
        {
            ClearSelection();
        }
        ShowToast(_linkageEnabled ? "已开启原图-文本联动" : "已关闭联动（纯展示模式）");
    }

    /// <summary>
    /// 文本编辑滑块开关切换
    /// </summary>
    private void OnEditSwitchChanged(object? sender, RoutedEventArgs e)
    {
        if (TglEdit == null) return;

        bool editMode = TglEdit.IsChecked == true;
        if (editMode == _isEditMode) return;

        _isEditMode = editMode;
        foreach (var line in _lineItems)
        {
            line.SetGlobalEditMode(_isEditMode);
        }

        if (_isEditMode)
        {
            ClearSelection();
            ShowToast("编辑模式：点右侧任意一行即可改字");
        }
        else
        {
            ShowToast("已退出编辑模式");
        }
    }

    /// <summary>
    /// 分行专属工具（联动高亮 / 文本编辑）的互斥反馈：
    /// 两开关保持独立（无分组容器），仅分行视图全亮；
    /// 合并 / 智能排版视图下禁用并整体降至 50% 透明度——开关归属与可用性一眼可辨。
    /// </summary>
    private void UpdateLineToolsGroup()
    {
        bool active = !_isMergedMode && !_isReflowView;
        if (TglLinkage != null) TglLinkage.Opacity = active ? 1.0 : 0.5;
        if (TglEdit != null) TglEdit.Opacity = active ? 1.0 : 0.5;
    }

    /// <summary>
    /// 视图模式二段选择器切换（分行 / 合并）：
    /// </summary>
    private void OnViewModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_viewSyncBusy) return;
        if (RadViewLines == null || RadViewMerged == null) return;

        if (_isReflowView)
        {
            if (BtnReflowView != null) BtnReflowView.IsChecked = false;
            _isReflowView = false;
            PanelReflow.IsVisible = false;
            ApplyTextMode(RadViewMerged.IsChecked == true);
            return;
        }

        bool merged = RadViewMerged.IsChecked == true;
        if (merged == _isMergedMode) return;

        ApplyTextMode(merged);
    }

    /// <summary>
    /// 智能排版独立选择型按钮切换：
    /// </summary>
    private void OnReflowViewToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_viewSyncBusy) return;
        if (BtnReflowView == null || PanelReflow == null) return;

        if (BtnReflowView.IsChecked == true)
        {
            if (_isReflowView) return;
            _isReflowView = true;
            if (_growMenuOpen) CloseGrowMenu();
            HideTextToolbar();

            TglLinkage.IsEnabled = false;
            TglEdit.IsEnabled = false;
            UpdateLineToolsGroup();

            _viewSyncBusy = true;
            RadViewLines.IsChecked = false;
            RadViewMerged.IsChecked = false;
            _viewSyncBusy = false;

            ScrollTxt.IsVisible = false;
            PanelMergedEditor.IsVisible = false;
            PanelReflow.IsVisible = true;
            // 智能排版共享分行/合并状态：从合并视图直接进入时，先把整段编辑结果同步回行列表，
            // 排版原文恒为最新编辑文本（否则未同步的编辑既进不了排版、返回合并时还会被旧文本覆盖）
            SyncMergedTextIfActive();
            CheckReflowConn();
            RenderReflowStage();
            return;
        }

        if (!_isReflowView) return;
        ExitReflowView(_isMergedMode);
    }

    /// <summary>
    /// 退出排版视图：关面板、复位开关与两段选中态（按目标视图）、恢复联动/编辑开关可用性。
    /// </summary>
    private void ExitReflowView(bool targetMerged)
    {
        _isReflowView = false;
        PanelReflow!.IsVisible = false;

        _viewSyncBusy = true;
        if (BtnReflowView != null) BtnReflowView.IsChecked = false;
        if (targetMerged) RadViewMerged!.IsChecked = true;
        else RadViewLines!.IsChecked = true;
        _viewSyncBusy = false;

        TglLinkage.IsEnabled = !targetMerged;
        TglEdit.IsEnabled = !targetMerged;
        UpdateLineToolsGroup();
        ApplyTextMode(targetMerged);
    }

    /// <summary>
    /// 应用分行/合并视图模式：
    /// - 合并：所有行以换行拼接进整段编辑框（自由编辑）；
    /// - 分行：整段编辑结果按行拆回卡片列表（保留与原始行的"已改"比对）。
    /// </summary>
    private void ApplyTextMode(bool merged)
    {
        _isMergedMode = merged;

        // 切换视图时收起打开中的生长菜单（菜单挂常驻树，不关会悬空残留）
        if (_growMenuOpen) CloseGrowMenu();
        HideTextToolbar();

        // 合并视图下整段可编辑、无行卡片可联动 → 禁用联动与编辑开关
        TglLinkage.IsEnabled = !merged;
        TglEdit.IsEnabled = !merged;
        UpdateLineToolsGroup();

        if (merged)
        {
            // 合并视图始终装载行列表文本（分行/合并编辑的都是原始 OCR 行）
            TxtAllLines.Text = string.Join("\n", _lineItems.Select(l => l.CurrentText));
            _mergedTextAtSwitch = TxtAllLines.Text;
            ScrollTxt.IsVisible = false;
            PanelMergedEditor.IsVisible = true;
            ClearSelection();
        }
        else
        {
            // 复用判定：从未进过合并视图（_mergedTextAtSwitch==null）或文本未被改动
            // → 行卡片仍在 _lineItems，直接复用（保留联动源框与已改基准）；
            // 改过 → 按新文本重建行卡片（排版结果独立保留，状态条提示「原文已修改」）
            if (_mergedTextAtSwitch != null && TxtAllLines.Text != _mergedTextAtSwitch)
                RebuildLineItemsFromText(TxtAllLines.Text);
            ScrollTxt.IsVisible = true;
            PanelMergedEditor.IsVisible = false;
            HideTextToolbar();
            // 初态查看中：编辑开关保持禁用（只读对照），联动不受影响
            if (_viewingInitial) TglEdit.IsEnabled = false;
            SyncEditedIndicesToOverlay();
            RefreshStatusBar();
        }
    }

    // ═══════════════ 初始/修改对照（顶栏分段）═══════════════

    /// <summary>是否存在修改态（用户编辑 / 删行 / 智能排版任一发生即 true）。</summary>
    private bool HasModifications() =>
        _reflowApplied
        || _lineItems.Count != _initialLines.Count
        || _lineItems.Any(l => l.IsEdited);

    /// <summary>
    /// 刷新 初始/修改 图标开关可用性（挂 RefreshStatusBar，全编辑路径统一收口）：
    /// 无修改 → 禁用并复位未选中（默认即初始态）；有修改 → 启用并停在「修改」（选中态）。
    /// </summary>
    private void UpdateInitModToggle()
    {
        if (BtnInitMod == null) return;
        if (_viewingInitial || _reflowStage == ReflowStage.Running) return;   // 初态只读期间 / 排版进行中锁定

        bool has = HasModifications();
        BtnInitMod.IsEnabled = has;
        if (has) SetInitModToggle(true);      // 选中=查看修改后结果
        else
        {
            _stashedModified = null;
            SetInitModToggle(false);
        }
    }

    /// <summary>
    /// 程序化拨动 初始/修改 图标开关：选中=查看修改后结果，未选中=查看初始识别；
    /// 忙标志防 IsCheckedChanged 事件回声重入，并同步更新 tooltip 对照提示。
    /// </summary>
    private void SetInitModToggle(bool viewingModified)
    {
        if (BtnInitMod == null) return;
        _initModBusy = true;
        try
        {
            BtnInitMod.IsChecked = viewingModified;
            Avalonia.Controls.ToolTip.SetTip(BtnInitMod, viewingModified
                ? "对照查看：当前显示修改后结果（点击查看初始识别）"
                : "对照查看：当前显示初始识别（点击返回修改结果）");
        }
        finally { _initModBusy = false; }
    }

    /// <summary>初始/修改图标开关切换（程序化拨动期间的事件回声与禁用态复位均不触发）。</summary>
    private void OnInitModToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_initModBusy) return;                                             // 程序化拨动的回声事件
        if (BtnInitMod == null || !BtnInitMod.IsEnabled) return;
        bool wantInitial = BtnInitMod.IsChecked != true;                      // 未选中=查看初始识别
        if (wantInitial == _viewingInitial) return;
        if (wantInitial) EnterInitialState();
        else ExitInitialState();
    }

    /// <summary>
    /// 切入初始态：暂存当前行列表（分行/合并编辑的原始 OCR 行），行列表重建为初始识别行（只读对照）。
    /// 排版结果在独立 _reflowItems 中不受影响，切回修改态按视图原样恢复。
    /// </summary>
    private void EnterInitialState()
    {
        if (_reflowStage == ReflowStage.Running)
        {
            ShowToast("排版进行中，请稍候");
            SetInitModToggle(true);   // 拨回「修改」选中态
            return;
        }

        _stashedModified = _lineItems.Select(l =>
            new StashedLineState(l.CurrentText, l.OriginalText, l.BoxIndex, l.ReflowRole, l.ReflowPlain, l.ReflowEdits,
                l.ReflowTitleLevel, l.ReflowListDepth, l.ReflowListOrdered, l.ReflowListOrdinal, l.ReflowCodeLang)).ToList();
        _stashReflowApplied = _reflowApplied;
        _viewingInitial = true;

        // 退出全局编辑模式（初态只读，防 CurrentText 修改器破坏暂存语义）
        if (_isEditMode) TglEdit.IsChecked = false;

        // 重建为初始行：original == current → 无「已改」标注，联动框精准对位
        _lineItems.Clear();
        for (int i = 0; i < _initialLines.Count; i++)
        {
            var item = new OcrLineItem(this, i, _initialLines[i], _initialLines[i], i < _initialBoxes?.Count ? i : -1);
            item.SetGlobalEditMode(false);
            _lineItems.Add(item);
        }

        ClearSelection();
        SyncEditedIndicesToOverlay();
        HighlightOverlay.SetEditedIndices(null);

        if (_isMergedMode)
        {
            TxtAllLines.Text = string.Join("\n", _lineItems.Select(l => l.CurrentText));
            _mergedTextAtSwitch = TxtAllLines.Text;
            TxtAllLines.IsReadOnly = true;
        }
        if (!_isReflowView) TglEdit.IsEnabled = false;
        if (BtnStartReflow != null) BtnStartReflow.IsEnabled = false;

        RefreshStatusBar();
        if (_isReflowView) RenderReflowStage();
        ShowToast("查看初始识别结果（只读）");
    }

    /// <summary>
    /// 切回修改态：从暂存完整恢复行列表（含排版块结构、润色标注、编辑态标志）。
    /// </summary>
    private void ExitInitialState()
    {
        _viewingInitial = false;
        _reflowApplied = _stashReflowApplied;

        if (_stashedModified != null)
        {
            _lineItems.Clear();
            for (int i = 0; i < _stashedModified.Count; i++)
            {
                var s = _stashedModified[i];
                var item = new OcrLineItem(this, i, s.Current, s.Original, s.BoxIndex)
                {
                    ReflowRole = s.Role,
                    ReflowPlain = s.Plain,
                    ReflowEdits = s.Edits,
                    ReflowTitleLevel = s.TitleLevel,
                    ReflowListDepth = s.ListDepth,
                    ReflowListOrdered = s.ListOrdered,
                    ReflowListOrdinal = s.ListOrdinal,
                    ReflowCodeLang = s.CodeLang
                };
                item.SetGlobalEditMode(_isEditMode);
                _lineItems.Add(item);
            }
            _stashedModified = null;
        }

        ClearSelection();
        SyncEditedIndicesToOverlay();

        TxtAllLines.IsReadOnly = false;
        if (_isMergedMode)
        {
            // 合并视图始终装载行列表文本（分行/合并编辑的都是原始 OCR 行）
            TxtAllLines.Text = string.Join("\n", _lineItems.Select(l => l.CurrentText));
            _mergedTextAtSwitch = TxtAllLines.Text;
        }
        if (!_isReflowView) TglEdit.IsEnabled = !_isMergedMode;
        if (BtnStartReflow != null && !_connChecking) BtnStartReflow.IsEnabled = _connOk || _reflowStage == ReflowStage.Done;

        RefreshStatusBar();
        if (_isReflowView) RenderReflowStage();
        ShowToast("返回修改结果");
    }

    /// <summary>
    /// 输出取文开关：排版视图完成态（非初态查看）→ 取排版块结构化文本；
    /// 其余（分行/合并/初态查看）→ 取行列表文本。复制/导出按「所见内容」输出。
    /// </summary>
    private bool ReflowOutputActive =>
        _reflowApplied && _isReflowView && !_viewingInitial && _reflowItems.Count > 0;

    /// <summary>
    /// 复制/导出用全文（\n 换行版）：排版视图完成态为块结构化文本
    /// （标题/段落/代码/引用后空行分隔，列表项连续单行成组），其余为行文本拼接。
    /// </summary>
    private string GetAllTextForOutputPlain()
    {
        if (!ReflowOutputActive)
        {
            if (_initialBoxes != null && _initialBoxes.Count == _lineItems.Count)
            {
                var currentBoxes = new List<ScreenCaptureTool.Core.Capture.OcrResultBox>(_initialBoxes.Count);
                for (int i = 0; i < _initialBoxes.Count; i++)
                {
                    currentBoxes.Add(new ScreenCaptureTool.Core.Capture.OcrResultBox
                    {
                        Text = _lineItems[i].CurrentText,
                        Bounds = _initialBoxes[i].Bounds,
                        Polygon = _initialBoxes[i].Polygon,
                        Score = _initialBoxes[i].Score,
                        LineIndex = _initialBoxes[i].LineIndex
                    });
                }
                string natural = ScreenCaptureTool.Core.Capture.OcrNaturalFlow.FormatNaturalText(currentBoxes);
                if (!string.IsNullOrEmpty(natural)) return natural;
            }
            return ScreenCaptureTool.Core.Capture.OcrNaturalFlow.JoinParagraphLines(_lineItems.Select(l => l.CurrentText));
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _reflowItems.Count; i++)
        {
            sb.Append(_reflowItems[i].CurrentText).Append('\n');
            // 标题/段落/代码/引用后空行分隔；列表项连续单行（保持列表成组）
            if (_reflowItems[i].ReflowRole is 'T' or 'P' or 'C' or 'Q' && i + 1 < _reflowItems.Count) sb.Append('\n');
        }
        while (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// 依据整段文本重建分行卡片（合并视图切回分行视图时调用）。
    /// 与原始行文本对齐的行保留"已改"判定基准，新增行以自身为基准。
    /// </summary>
    private void RebuildLineItemsFromText(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        _lineItems.Clear();
        for (int i = 0; i < lines.Length; i++)
        {
            string original = i < _initialLines.Count ? _initialLines[i] : lines[i];
            // 行数与原图框可能不一致（合并视图增删行）：超出部分 BoxIndex=-1 联动降级
            var item = new OcrLineItem(this, i, lines[i], original, i < _initialBoxes?.Count ? i : -1);
            item.SetGlobalEditMode(_isEditMode);
            _lineItems.Add(item);
        }
    }

    #endregion

    #region 图文双向联动与编辑事件

    public void OnLineItemEdited(OcrLineItem item)
    {
        RefreshStatusBar();
        SyncEditedIndicesToOverlay();
    }

    private void SyncEditedIndicesToOverlay()
    {
        // 用稳定 BoxIndex 映射原图框（列表 Index 在删除/排序后会重编号，会导致高亮错位）；
        // 行项与排版块项（独立 _reflowItems，SourceBoxIndices 全集 → 整块源框同时点亮）取并集；
        // 初态查看期间排除块项（纯初始对照，不显示润色修正高亮）
        IEnumerable<OcrLineItem> source =
            _reflowApplied && !_viewingInitial ? _lineItems.Concat(_reflowItems) : _lineItems;
        var editedSet = new HashSet<int>(
            source.Where(l => l.IsEdited && l.BoxIndex >= 0)
                  .SelectMany(l => l.SourceBoxIndices.Where(i => i >= 0)));
        HighlightOverlay.SetEditedIndices(editedSet.Count > 0 ? editedSet : null);
    }

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isEditMode) return;

        var activeEditingItem = _lineItems.FirstOrDefault(l => l.IsSingleEditing);
        if (activeEditingItem == null) return;

        if (e.Source is Visual sourceVisual)
        {
            var clickedCard = sourceVisual.FindAncestorOfType<Control>();
            while (clickedCard != null && clickedCard.Name != "LineCard")
            {
                clickedCard = clickedCard.FindAncestorOfType<Control>();
            }

            if (clickedCard != null && clickedCard.DataContext is OcrLineItem clickedItem)
            {
                if (clickedItem == activeEditingItem)
                {
                    // 点击在当前编辑行的内部（例如输入框）：继续正常编辑
                    return;
                }
                else
                {
                    // 点击在其他行卡片：退出当前编辑行，将编辑焦点转移到被点击的行
                    activeEditingItem.SetSingleEdit(false);
                    ClearSelection();
                    clickedItem.SetSingleEdit(true);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        FocusLineEditBox(clickedItem);
                    }, Avalonia.Threading.DispatcherPriority.Input);

                    return;
                }
            }
        }

        // 点击在卡片之外的任何地方（例如：空白区域、所有按钮、左侧原图、顶栏、底栏等）：立即退出编辑模式
        activeEditingItem.SetSingleEdit(false);
        ClearSelection();
    }

    private void OnLineCardDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is OcrLineItem item)
        {
            if (!item.IsEditMode)
            {
                // 退出其他正在单行编辑的行
                foreach (var line in _lineItems)
                {
                    if (line != item && line.IsSingleEditing)
                    {
                        line.SetSingleEdit(false);
                    }
                }

                // 进入编辑状态时清除展示高亮（编辑态与高亮态互斥）
                ClearSelection();
                item.SetSingleEdit(true);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var tb = c.FindControl<TextBox>("EditBox") ?? c.FindDescendantOfType<TextBox>();
                    if (tb != null)
                    {
                        tb.Focus();
                        tb.SelectAll();
                    }
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
        }
    }

    private void OnLineCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is OcrLineItem item)
        {
            var cp = e.GetCurrentPoint(this);
            if (cp.Properties.IsLeftButtonPressed)
            {
                if (_isEditMode)
                {
                    // 全局编辑模式：点击卡片任意区域直接聚焦当前行的 EditBox
                    var tb = c.FindControl<TextBox>("EditBox") ?? c.FindDescendantOfType<TextBox>();
                    tb?.Focus();
                    return;
                }

                // 纯展示模式：支持拖拽排序/合并与点击联动高亮
                if (!item.IsEditMode)
                {
                    _isPointerDownOnCard = true;
                    _draggedItem = item;
                    _lineDrag.Begin(c, e, this);
                    // 关键：标记已处理，阻止外层 ScrollViewer 抢捕获把拖动当成滚动手势
                    e.Handled = true;

                    if (_linkageEnabled)
                    {
                        if (_selectedLineIndex >= 0 && _selectedLineIndex < _lineItems.Count
                            && ReferenceEquals(_lineItems[_selectedLineIndex], item))
                        {
                            ClearSelection();
                        }
                        else
                        {
                            SelectLine(item, scrollRight: false);
                        }
                    }
                }
            }
        }
    }

    private void OnLineCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPointerDownOnCard && _draggedItem != null && !_draggedItem.IsEditMode)
        {
            var curPos = e.GetPosition(this);

            if (!_isDraggingLine)
            {
                if (!_lineDrag.Update(e)) return;

                _isDraggingLine = true;
                _draggedItem.IsDragged = true;   // 被拖行半透明 + 拖动光标，明确"正拿着这一行"
            }

            UpdateDragDropTarget(curPos);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 依据指针窗口坐标几何计算当前拖放目标行与落点区域（上缘/中部/下缘）。
    /// 不使用 InputHitTest：指针被卡片捕获期间命中测试结果不可靠，
    /// 改为遍历行卡片容器并经 TranslatePoint 换算窗口坐标矩形判定（与设置页工具栏拖拽同款算法）。
    /// </summary>
    private void UpdateDragDropTarget(Point windowPos)
    {
        OcrLineItem? targetItem = null;
        Rect targetRect = default;

        foreach (var line in _lineItems)
        {
            if (line == _draggedItem) continue;
            if (ItemsTextLines.ContainerFromItem(line) is not Control container) continue;
            if (!ListReorderBehavior.TryGetBounds(container, this, out var rect)) continue;
            if (rect.Contains(windowPos))
            {
                targetItem = line;
                targetRect = rect;
                break;
            }
        }

        foreach (var line in _lineItems)
        {
            if (line != targetItem)
            {
                line.DropState = DropTargetPosition.None;
            }
        }

        if (targetItem != null && targetRect.Height > 0)
        {
            // 上缘 25% → 插入到该行之前；下缘 25% → 插入到该行之后；中部 50% → 合并
            double ratio = Math.Clamp((windowPos.Y - targetRect.Y) / targetRect.Height, 0.0, 1.0);
            targetItem.DropState = ratio < 0.25 ? DropTargetPosition.Top
                                 : ratio > 0.75 ? DropTargetPosition.Bottom
                                 : DropTargetPosition.Merge;
        }
    }

    private void OnLineCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;

        // 注意顺序：必须先完成拖放结果处理，再清理拖拽状态。
        // 释放指针捕获会同步触发 PointerCaptureLost（其处理器会清空 _draggedItem 与 DropState），
        // 若在处理前释放，合并/排序分支将永远无法执行。
        if (_isDraggingLine && _draggedItem != null)
        {
            var draggedItem = _draggedItem;
            var targetItem = _lineItems.FirstOrDefault(l => l.DropState != DropTargetPosition.None && l != draggedItem);

            if (targetItem != null)
            {
                ClearSelection();

                if (targetItem.DropState == DropTargetPosition.Merge)
                {
                    int draggedIdx = draggedItem.IndexNumber;
                    int targetIdx = targetItem.IndexNumber;
                    targetItem.CurrentText = MergeLineText(draggedItem, targetItem);
                    _lineItems.Remove(draggedItem);
                    ReIndexLines();
                    SyncEditedIndicesToOverlay();
                    RefreshStatusBar();
                    ShowToast($"已将第 {draggedIdx} 行合并至第 {targetIdx} 行");
                }
                else
                {
                    // 上/下缘 → 插入到目标行之前/之后
                    int fromIdx = _lineItems.IndexOf(draggedItem);
                    int toIdx = _lineItems.IndexOf(targetItem);
                    if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                    {
                        bool insertAfter = targetItem.DropState == DropTargetPosition.Bottom;
                        // ObservableCollection.Move 为先移除再插入：向下移动时目标索引需按移除后的列表换算
                        int insertIdx = insertAfter
                            ? (fromIdx < toIdx ? toIdx : toIdx + 1)
                            : (fromIdx < toIdx ? toIdx - 1 : toIdx);
                        _lineItems.Move(fromIdx, insertIdx);
                        ReIndexLines();
                        SyncEditedIndicesToOverlay();
                        RefreshStatusBar();
                        ShowToast("已调整行顺序");
                    }
                }
            }
            // 拖到空白处/原位松手：视为取消本次拖拽，不改动内容，也保留按下时的选中状态
        }

        EndLineDrag();
    }

    /// <summary>
    /// 统一清理行拖拽状态（释放处理器与捕获丢失处理器共用，可安全重复调用）。
    /// </summary>
    private void EndLineDrag()
    {
        _draggedItem?.IsDragged = false;
        _draggedItem = null;
        _isPointerDownOnCard = false;
        _isDraggingLine = false;
        foreach (var line in _lineItems)
        {
            line.DropState = DropTargetPosition.None;
        }
        _lineDrag.Reset();
    }

    /// <summary>
    /// 合并两行文本：保持拖拽前的原始上下顺序并分行显示（不拼成一段）。
    /// 上行拖到下行合并 → 上行文本在上；下行拖到上行合并 → 下行文本在下。
    /// </summary>
    private static string MergeLineText(OcrLineItem dragged, OcrLineItem target)
    {
        // 以合并前两者在列表中的先后关系决定上下顺序，保证阅读顺序与原图一致
        bool draggedAbove = dragged.Index < target.Index;
        string upper = draggedAbove ? dragged.CurrentText : target.CurrentText;
        string lower = draggedAbove ? target.CurrentText : dragged.CurrentText;

        var parts = new[] { upper, lower }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return parts.Count == 0 ? string.Empty : string.Join("\n", parts);
    }

    private void OnLineCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndLineDrag();
    }

    private void ReIndexLines()
    {
        for (int i = 0; i < _lineItems.Count; i++)
        {
            _lineItems[i].Index = i;
        }
    }

    private void OnLineCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_linkageEnabled || _isEditMode || _isDraggingLine || _lineItems.Any(l => l.IsSingleEditing)) return;
        if (sender is Control b && b.DataContext is OcrLineItem item)
        {
            item.IsHovered = true;
            if (item.BoxIndex >= 0 && _selectedLineIndex != item.Index)
            {
                HighlightOverlay.HoverIndex = item.BoxIndex;
            }
        }
    }

    private void OnLineCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control b && b.DataContext is OcrLineItem item)
        {
            item.IsHovered = false;
            if (HighlightOverlay.HoverIndex == item.BoxIndex)
            {
                HighlightOverlay.HoverIndex = -1;
            }
        }
    }

    private void OnLineTextBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        // 单独编辑状态下不触发原图高亮与选中色（高亮仅在纯展示点击时触发）
    }

    private void OnLineTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is OcrLineItem item)
        {
            if (item.IsSingleEditing && !_isEditMode)
            {
                // 延迟至下一 UI 周期，检查焦点是否落在了其他行的 EditBox 上
                // 若焦点移到了外部非编辑组件，则退出单行编辑
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var focused = FocusManager?.GetFocusedElement();
                    if (focused is TextBox focusedTb && focusedTb.DataContext is OcrLineItem focusedItem)
                    {
                        if (focusedItem != item)
                        {
                            item.SetSingleEdit(false);
                        }
                    }
                    else
                    {
                        item.SetSingleEdit(false);
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }
    }

    private void OnLineTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is OcrLineItem item)
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                if (item.IsSingleEditing && !_isEditMode)
                {
                    item.SetSingleEdit(false);
                    e.Handled = true;
                    ScrollTxt.Focus();
                }
            }
            else if (e.Key == Key.Tab && !_isEditMode)
            {
                // Tab / Shift+Tab 在单行编辑模式下在各行之间平滑转移编辑焦点
                int targetIdx = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? item.Index - 1 : item.Index + 1;
                if (targetIdx >= 0 && targetIdx < _lineItems.Count)
                {
                    item.SetSingleEdit(false);
                    var targetItem = _lineItems[targetIdx];
                    targetItem.SetSingleEdit(true);
                    e.Handled = true;

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        FocusLineEditBox(targetItem);
                    }, Avalonia.Threading.DispatcherPriority.Input);
                }
            }
        }
    }

    private void OnScrollTxtPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isEditMode) return;

        // 点击滚动区域空白处（非卡片内部）时退出单行编辑
        if (e.Source == ScrollTxt || e.Source is Panel)
        {
            foreach (var line in _lineItems)
            {
                if (line.IsSingleEditing)
                {
                    line.SetSingleEdit(false);
                }
            }
        }
    }

    private void FocusLineEditBox(OcrLineItem targetItem)
    {
        var container = ItemsTextLines.ContainerFromItem(targetItem);
        if (container != null)
        {
            var tb = container.FindControl<TextBox>("EditBox") ?? container.FindDescendantOfType<TextBox>();
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }
    }

    private void OnLineTextBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        // 绑定已自动更新 CurrentText
    }

    private void OnOverlayBoxHoverChanged(int hoverIndex)
    {
        // [OcrHover] 临时排查：窗口侧回调门控快照（有 overlay 日志但无本日志=事件未挂接；命中门控=对应状态卡死）
        AppLogger.Info($"[OcrHover] 窗口侧回调 hoverIndex={hoverIndex} 门控: linkage={_linkageEnabled} editMode={_isEditMode} dragging={_isDraggingLine} 单行编辑={_lineItems.Count(l => l.IsSingleEditing)} | 行数={_lineItems.Count} 可映射行={_lineItems.Count(l => l.BoxIndex >= 0)} 视图=初始{_viewingInitial}/合并{_isMergedMode}/排版{_isReflowView}");
        if (!_linkageEnabled || _isEditMode || _isDraggingLine || _lineItems.Any(l => l.IsSingleEditing)) return;
        foreach (var line in _lineItems)
        {
            line.IsHovered = (line.BoxIndex == hoverIndex && line.BoxIndex >= 0 && line.Index != _selectedLineIndex);
        }

        if (hoverIndex >= 0)
        {
            // 悬停在左侧原图框时，自动平滑滚动定位右侧对应卡片至视野内（按稳定 BoxIndex 反查）
            var targetItem = _lineItems.FirstOrDefault(l => l.BoxIndex == hoverIndex);
            if (targetItem != null)
            {
                var container = ItemsTextLines.ContainerFromItem(targetItem) as Control;
                if (container != null)
                {
                    container.BringIntoView();
                }
            }
        }
    }

    private void OnOverlayBoxClicked(int boxIndex)
    {
        if (!_linkageEnabled) return;

        // 点击原图多边形框时退出所有单行编辑状态
        foreach (var line in _lineItems)
        {
            if (line.IsSingleEditing)
            {
                line.SetSingleEdit(false);
            }
        }

        // 按稳定 BoxIndex 反查对应行（boxIndex 可能指向已被删除行的遗留框 → 无匹配则清除选中）
        var hitItem = boxIndex >= 0 ? _lineItems.FirstOrDefault(l => l.BoxIndex == boxIndex) : null;
        if (hitItem != null)
        {
            SelectLine(hitItem, scrollRight: true);
        }
        else
        {
            ClearSelection();
        }
    }

    private void SelectLine(OcrLineItem item, bool scrollRight)
    {
        // 如果处于全局编辑模式或单行编辑中，不触发联动高亮选中
        if (_isEditMode || _lineItems.Any(l => l.IsSingleEditing)) return;

        _selectedLineIndex = _lineItems.IndexOf(item);
        HighlightOverlay.SelectedIndex = item.BoxIndex;

        foreach (var line in _lineItems)
        {
            line.IsSelected = ReferenceEquals(line, item);
            if (!ReferenceEquals(line, item) && line.IsSingleEditing)
            {
                line.SetSingleEdit(false);
            }
        }

        TxtSelectedCount.Text = "1";

        if (scrollRight)
        {
            // 可视滚动定位至右侧卡片
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var container = ItemsTextLines.ContainerFromItem(item) as Control;
                if (container != null)
                {
                    container.BringIntoView();
                }
                else
                {
                    double targetY = Math.Max(0, _selectedLineIndex * 42.0 - 50.0);
                    ScrollTxt.Offset = new Vector(0, targetY);
                }
            });
        }
    }

    private void ClearSelection()
    {
        _selectedLineIndex = -1;
        HighlightOverlay.SelectedIndex = -1;
        foreach (var line in _lineItems)
        {
            line.IsSelected = false;
            if (line.IsSingleEditing)
            {
                line.SetSingleEdit(false);
            }
        }
        TxtSelectedCount.Text = "0";
    }

    private void RefreshStatusBar()
    {
        int total = _lineItems.Count;
        int edited = _lineItems.Count(l => l.IsEdited);

        TxtTotalLines.Text = total.ToString();
        TxtEditedLines.Text = edited.ToString();
        PanelEditedCount.IsVisible = edited > 0;

        UpdateInitModToggle();
    }

    #endregion

    #region 合并视图：选中文本浮动工具栏与清除/校对算子

    /// <summary>风格类算子的二级选项（生长菜单药丸 chips / 右键二级子菜单共用）。</summary>
    private sealed record StyleOp(string Name, string Label, Func<string, TextOpResult> Op);

    /// <summary>算子元数据：显示名、效果提示、生长菜单字符徽标、右键菜单矢量图标、直接算子或风格选项组（13 项归并为 8 项）。</summary>
    private sealed record TextOpMeta(string Label, string Hint, string Badge, string Icon, Func<string, TextOpResult>? DirectOp, StyleOp[]? Styles);

    private static readonly TextOpMeta[] ClearOpMetas =
    {
        new("清除空格", "你 好 → 你好", "␣", OcrIconGeometry.LetterSpacing, OcrTextOps.ClearSpaces, null),
        new("整理空白字符", "删除空格与制表符，并合并连续空行", "¶", OcrIconGeometry.LineSpacing, OcrTextOps.ClearWhitespace, null),
        new("清除隐藏字符", "移除看不见的零宽字符", "∅", OcrIconGeometry.NullSet, OcrTextOps.ClearZeroWidth, null),
        new("删除括号及内容", "括号和其中的文字一起删除", "（）", OcrIconGeometry.BackspaceKey, OcrTextOps.ClearParenthesizedContent, null),
    };

    private static readonly TextOpMeta[] ProofOpMetas =
    {
        new("还原数字", "带圈、带括号数字转为普通数字，例如 ①⑵ → 12", "①", OcrIconGeometry.DigitBox, OcrTextOps.RestoreDigits, null),
        new("统一引号", "选择一种引号样式", "“”", OcrIconGeometry.QuoteStyle, null, new[]
        {
            new StyleOp("统一为直引号", "直引号  \"…\"", OcrTextOps.ToStraightQuotes),
            new StyleOp("统一为弯引号", "弯引号  “…”", OcrTextOps.ToCurlyQuotes),
            new StyleOp("统一为直角引号", "直角引号  「…」", OcrTextOps.ToCornerQuotes),
        }),
        new("统一括号", "选择中文或英文括号", "（）", OcrIconGeometry.ParenStyle, null, new[]
        {
            new StyleOp("统一为英文括号", "英文括号  ( )", OcrTextOps.ToEnglishParentheses),
            new StyleOp("统一为中文括号", "中文括号  （ ）", OcrTextOps.ToChineseParentheses),
        }),
        new("统一标点", "转换中英文标点，保留小数点", "，。", OcrIconGeometry.PunctuationBox, null, new[]
        {
            new StyleOp("统一为中文标点", "中文标点  ，。", OcrTextOps.ToChinesePunctuation),
            new StyleOp("统一为英文标点", "英文标点  , .", OcrTextOps.ToEnglishPunctuation),
        }),
    };

    /// <summary>构建算子条目的双行文本（名称 + 效果提示）；算子图标另走 <see cref="MenuItem.Icon"/> 图标列。</summary>
    private Control BuildOpEntryHeader(TextOpMeta meta)
    {
        var texts = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = meta.Label, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            Foreground = ResBrush("OcrTextPrimaryBrush"),
        });
        texts.Children.Add(new TextBlock
        {
            Text = meta.Hint, FontSize = 10.5,
            Foreground = ResBrush("OcrTextMutedBrush"),
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            TextWrapping = TextWrapping.NoWrap,
        });

        return texts;
    }

    /// <summary>构建合并视图右键菜单的「清除/校对」子菜单（8 项；风格类展开为二级子菜单）。</summary>
    private void BuildContextMenuOps()
    {
        MnuCtxClear.Items.Clear();
        MnuCtxProof.Items.Clear();
        foreach (var meta in ClearOpMetas) MnuCtxClear.Items.Add(BuildContextOpItem(meta));
        foreach (var meta in ProofOpMetas) MnuCtxProof.Items.Add(BuildContextOpItem(meta));
    }

    private MenuItem BuildContextOpItem(TextOpMeta meta)
    {
        var item = new MenuItem
        {
            // 矢量几何进图标列，与其他右键菜单的 Path.menuIcon 同一条样式、同 16×16 槽位同色。
            // 不用字符徽标：␣ ¶ ∅ 之类字符的墨迹只占 em 框约六成，紧包围盒缩放后仍撑不满槽位，肉眼就是"太小"
            Icon = OcrIconGeometry.CreateIcon(meta.Icon, "menuIcon"),
            Header = BuildOpEntryHeader(meta),
            MinWidth = 250,
        };

        if (meta.Styles is { Length: > 0 })
        {
            // 风格类：二级子菜单逐风格执行（与生长菜单 chips 等价）
            foreach (var style in meta.Styles)
            {
                var captured = style;
                item.Items.Add(new MenuItem
                {
                    Header = $"{captured.Name}（{captured.Label}）",
                });
                ((MenuItem)item.Items[^1]!).Click += (_, _) => ApplyTextOp(captured.Op, captured.Name);
            }
        }
        else
        {
            var captured = meta;
            item.Click += (_, _) => ApplyTextOp(captured.DirectOp!, captured.Label);
        }
        return item;
    }

    // —— 生长式菜单（从工具栏「清除/校对」按钮长出：茎部连接 + 点亮按钮 + 展开动画） ——

    private bool _growMenuOpen;
    private bool _growMenuIsAbove;
    private Button? _growMenuSource;
    private readonly List<(ContinuousBorder Item, StackPanel Chips, Panel Chev)> _growExpandables = new();

    // 生长菜单展开动画 Transform（构造时从 GrowMenuHost.RenderTransform 缓存）
    private readonly TransformGroup _growTransformGroup = null!;
    private readonly ScaleTransform _growMenuScale = null!;
    private readonly TranslateTransform _growMenuTranslate = null!;

    private void OnTtClearMenuClick(object? sender, RoutedEventArgs e) => ToggleGrowMenu(BtnTtClear, isClear: true);

    private void OnTtProofMenuClick(object? sender, RoutedEventArgs e) => ToggleGrowMenu(BtnTtProof, isClear: false);

    /// <summary>再次点击源按钮收起；点击另一按钮切换。</summary>
    private void ToggleGrowMenu(Button source, bool isClear)
    {
        if (_growMenuOpen && ReferenceEquals(_growMenuSource, source)) CloseGrowMenu();
        else OpenGrowMenu(source, isClear);
    }

    private void OpenGrowMenu(Button source, bool isClear)
    {
        if (!_isMergedMode) return;
        CloseGrowMenuCore();

        _growMenuSource = source;
        _textToolbarPinned = true;
        SnapshotMergedSelection();
        BuildGrowMenuContent(isClear);
        GrowMenuScroll.Offset = Vector.Zero;   // 每次打开重置滚动位置到顶部
        UpdateGrowMenuPosition();

        _growMenuTranslate.Y = _growMenuIsAbove ? 8 : -8;
        GrowMenuHost.IsHitTestVisible = true;
        GrowMenuHost.Opacity = 1;
        _growMenuScale.ScaleX = 1;
        _growMenuScale.ScaleY = 1;
        _growMenuTranslate.Y = 0;
        source.Classes.Add("active");
        SetMergedSelectionInteractive(interactive: true);
        _growMenuOpen = true;

        LogGrowCaret("打开后即时");
        LogGrowCaretDelayed("打开后稳定");
    }

    private void CloseGrowMenu()
    {
        if (!_growMenuOpen) return;
        CloseGrowMenuCore();
        SetMergedSelectionInteractive(interactive: false);
        _textToolbarPinned = false;
        _lastTextFlyoutClosedAt = DateTime.UtcNow;
        RestoreMergedSelectionAfterMenu();
    }

    private void CloseGrowMenuCore()
    {
        _growMenuOpen = false;
        LogGrowCaret("关闭时");
        LogGrowCaretDelayed("关闭后稳定");
        GrowMenuHost.Opacity = 0;
        GrowMenuHost.IsHitTestVisible = false;
        _growMenuScale.ScaleX = 0.88;
        _growMenuScale.ScaleY = 0.88;
        _growMenuTranslate.Y = _growMenuIsAbove ? 8 : -8;
        _growMenuSource?.Classes.Remove("active");
        _growMenuSource = null;
        _growExpandables.Clear();
        GrowMenuBody.Children.Clear();
    }

    /// <summary>
    /// 全量诊断：记录「清除/校对」按钮下拉箭头一切可能影响位置/角度的因素。
    /// 每次同时记录两个箭头互为对照：
    /// - panel布局：布局系统边界（不含 RenderTransform）
    /// - 渲染(TransformedBounds)：含 RenderTransform 的真实渲染边界 = 肉眼所见
    /// - 原点/变换：RenderTransformOrigin 原始值 + 当前变换与角度
    /// - path几何：Panel 内部 Path 的几何边界（验证箭头图形是否居中于 16×16）
    /// - 相对按钮：箭头原点相对按钮的位置
    /// 同时打出运行目录，用于排查 Debug/Release 构建不一致。
    /// </summary>
    private void LogGrowCaret(string action)
    {
        try
        {
            AppLogger.Info(
                $"[Caret-{action}] 运行目录={AppContext.BaseDirectory} " +
                $"工具栏=({ToolbarTextTools.Bounds.X:F0},{ToolbarTextTools.Bounds.Y:F0} {ToolbarTextTools.Bounds.Width:F0}x{ToolbarTextTools.Bounds.Height:F0})");
            LogOneCaret("清除", TxtGrowCaretClear, BtnTtClear);
            LogOneCaret("校对", TxtGrowCaretProof, BtnTtProof);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Caret-{action}] 记录失败: {ex.Message}");
        }
    }

    private void LogOneCaret(string name, Panel caret, Button btn)
    {
        var b = caret.Bounds;                     // 布局边界（不含 RenderTransform）
        var tb = ComputeRenderedBounds(caret);    // 推算渲染边界（绕原点变换后）= 肉眼所见
        var o = caret.RenderTransformOrigin;      // 变换原点原始值
        string tf = caret.RenderTransform switch
        {
            RotateTransform r => $"Rotate({r.Angle:F0})",
            null => "null",
            var t => t.GetType().Name,
        };
        var path = caret.Children.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault();
        var pb = path?.Bounds;
        var relBtn = caret.TranslatePoint(new Point(0, 0), btn);

        AppLogger.Info(
            $"[Caret-{name}] active={btn.Classes.Contains("active")} " +
            $"panel布局=({b.X:F1},{b.Y:F1} {b.Width:F1}x{b.Height:F1}) " +
            $"渲染推算=({tb.X:F1},{tb.Y:F1} {tb.Width:F1}x{tb.Height:F1}) " +
            $"原点=({o.Point.X:F2},{o.Point.Y:F2} {o.Unit}) 变换={tf} " +
            $"path几何=({pb?.X ?? double.NaN:F1},{pb?.Y ?? double.NaN:F1} {pb?.Width ?? double.NaN:F1}x{pb?.Height ?? double.NaN:F1}) " +
            $"相对按钮=({relBtn?.X ?? double.NaN:F1},{relBtn?.Y ?? double.NaN:F1}) " +
            $"按钮={btn.Bounds.Width:F0}x{btn.Bounds.Height:F0}");
    }

    /// <summary>推算控件经 RenderTransform 绕 RenderTransformOrigin 变换后的包围盒（= 肉眼所见位置）。</summary>
    private static Rect ComputeRenderedBounds(Avalonia.Controls.Control c)
    {
        var b = c.Bounds;
        var t = c.RenderTransform;
        if (t == null) return b;
        var o = c.RenderTransformOrigin;
        double px = b.X, py = b.Y;
        if (o.Unit == RelativeUnit.Relative) { px += o.Point.X * b.Width; py += o.Point.Y * b.Height; }
        else { px = o.Point.X; py = o.Point.Y; }

        Point T(Point p)
        {
            var q = new Point(p.X - px, p.Y - py);
            var r = t.Value.Transform(q);
            return new Point(r.X + px, r.Y + py);
        }

        var corners = new[] { T(b.TopLeft), T(b.TopRight), T(b.BottomLeft), T(b.BottomRight) };
        double x1 = corners.Min(p => p.X), y1 = corners.Min(p => p.Y);
        double x2 = corners.Max(p => p.X), y2 = corners.Max(p => p.Y);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>延时补记一次稳定态（等待动画/布局完成后），捕捉与即时态的差异。</summary>
    private void LogGrowCaretDelayed(string action, int delayMs = 350)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(delayMs);
                LogGrowCaret(action);
            }
            catch { /* 忽略诊断日志异常 */ }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>构建单行清除／校对菜单；风格组选项纵向展开，细节只放悬浮提示。</summary>
    private void BuildGrowMenuContent(bool isClear)
    {
        GrowMenuBody.Children.Clear();
        _growExpandables.Clear();

        foreach (var meta in isClear ? ClearOpMetas : ProofOpMetas)
        {
            // 数字还原是直接操作，符号风格需再选一种，分隔一次即可区分两类动作。
            if (!isClear && ReferenceEquals(meta, ProofOpMetas[1]))
            {
                var separator = new Border { Height = 1, Margin = new Thickness(8, 4) };
                separator.Bind(Border.BackgroundProperty,
                    new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctMenuSeparatorBrush"));
                GrowMenuBody.Children.Add(separator);
            }
            var item = new ContinuousBorder { Classes = { "ocrGrowItem" } };
            ToolTip.SetTip(item, meta.Hint);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = meta.Label, Classes = { "ocrGrowText" } });
            item.Child = row;

            if (meta.Styles is { Length: > 0 })
            {
                var chev = new Panel
                {
                    Width = 16, Height = 16,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                    Children =
                    {
                        new Avalonia.Controls.Shapes.Path
                        {
                            Classes = { "icon" },
                            Data = Geometry.Parse("M4,6 L8,10 L12,6"),
                            Stretch = Stretch.None,
                        },
                    },
                };
                Grid.SetColumn(chev, 1);
                row.Children.Add(chev);

                // 纵向文字列表替代并排药丸，保留同一时间只展开一组的交互。
                var chips = new StackPanel { Margin = new Thickness(12, 0, 0, 4), IsVisible = false };
                foreach (var style in meta.Styles)
                {
                    var captured = style;
                    var choice = new Button
                    {
                        Content = new TextBlock { Text = captured.Label, Classes = { "ocrGrowText" } },
                        Classes = { "tool" }, Focusable = false,
                        // 内容决定高度，沿用全局按钮交互，仅增加样式列表的上下留白。
                        Height = double.NaN, MinHeight = 0, Padding = new Thickness(12, 8),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Margin = new Thickness(0),
                    };
                    choice.Click += (_, _) =>
                    {
                        ApplyTextOp(captured.Op, captured.Name);
                        CloseGrowMenu();
                    };
                    chips.Children.Add(choice);
                }
                item.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    ToggleGrowChips(item, chips, chev);
                };
                _growExpandables.Add((item, chips, chev));
                GrowMenuBody.Children.Add(item);
                GrowMenuBody.Children.Add(chips);
            }
            else
            {
                var captured = meta;
                item.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    ApplyTextOp(captured.DirectOp!, captured.Label);
                    CloseGrowMenu();
                };
                GrowMenuBody.Children.Add(item);
            }
        }
    }

    /// <summary>手风琴互斥：同一时间仅展开一组 chips；展开后按新尺寸重新定位（贴住按钮）。</summary>
    private void ToggleGrowChips(ContinuousBorder item, StackPanel chips, Panel chev)
    {
        bool opening = !chips.IsVisible;
        foreach (var (_, otherChips, otherChev) in _growExpandables)
        {
            otherChips.IsVisible = false;
            otherChev.RenderTransform = null;
        }
        if (opening)
        {
            chips.IsVisible = true;
            chev.RenderTransform = new RotateTransform(180);
        }
        UpdateGrowMenuPosition();
    }

    /// <summary>
    /// 生长菜单定位：
    /// 优先在工具栏正下方展开（间距 7px）；若下方空间不足且上方空间更宽裕，则向上展开翻转。
    /// 无论向上还是向下，当可用空间不足以完整容纳菜单时，动态限制 GrowMenuScroll.MaxHeight 并启用内部滚动，
    /// 配合 X/Y 坐标边界钳制，确保菜单绝不会被 TxtContentHost 容器裁剪或溢出可视区。
    /// 茎部随展开方向动态切换到菜单顶部或底部，并水平对齐源按钮中心。
    /// </summary>
    private void UpdateGrowMenuPosition()
    {
        if (_growMenuSource == null) return;

        // 关键：测量前清零旧定位 Margin 与高度上限。DesiredSize 包含 Margin，
        // 带旧值测量会把旧位置混入宽高（虚高数百 px）→ x 被钳到边缘、茎部错位。
        GrowMenuHost.Margin = default;
        // 连接角的旧水平偏移不参与内容量宽，避免短菜单被上一轮位置撑大。
        GrowMenuStem.Margin = default;
        GrowMenuScroll.MaxHeight = double.PositiveInfinity;
        // 同帧刚重建过菜单条目（BuildGrowMenuContent）或刚翻过条目内 chips 的可见性（ToggleGrowChips），
        // 中间容器的 measure 缓存不会自己失效 ⇒ 必须走 MeasureNow 递归失效，否则量到上一帧宽高。
        Size menuSize = LayoutMetrics.MeasureNow(GrowMenuHost);
        double mw = Math.Max(menuSize.Width, 1);
        double mh = menuSize.Height;

        // 工具栏整体相对内容区的位置
        Point? pBarTopLeft = ToolbarTextTools.TranslatePoint(new Point(0, 0), TxtContentHost);
        Point? pBtnTopLeft = _growMenuSource.TranslatePoint(new Point(0, 0), TxtContentHost);
        if (pBarTopLeft == null || pBtnTopLeft == null) return;

        double hostW = TxtContentHost.Bounds.Width;
        double hostH = TxtContentHost.Bounds.Height;
        double barTop = pBarTopLeft.Value.Y;
        double barBottom = pBarTopLeft.Value.Y + ToolbarTextTools.Bounds.Height;

        const double pad = 6.0;
        const double gap = 7.0;

        // 计算工具栏上方与下方的可用净空间（排除安全边距 pad 与间距 gap）
        double availBelow = Math.Max(0, hostH - barBottom - gap - pad);
        double availAbove = Math.Max(0, barTop - gap - pad);

        // 决定向上还是向下展开：
        // 1. 若下方空间足以放下菜单（availBelow >= mh），向下展开；
        // 2. 若下方空间不足且上方空间比下方更充裕，向上展开；
        // 3. 否则保持向下展开并限高滚动。
        bool placeAbove = availBelow < mh && availAbove > availBelow;
        _growMenuIsAbove = placeAbove;
        double availSpace = placeAbove ? availAbove : availBelow;

        // 若所选方向空间仍不足以完全展示菜单，限制 ScrollViewer 最大高度并重新测量
        if (mh > availSpace && availSpace > 0)
        {
            // 菜单内边距及阴影边框总留白约 16px
            GrowMenuScroll.MaxHeight = Math.Max(60, availSpace - 16);
            // MaxHeight 改在子节点上、这里原本连 InvalidateMeasure 都没调 ⇒ 裸 Measure 直接命中缓存，
            // 拿到的是限高前的旧高度，翻转/钳制随即按错值算。
            menuSize = LayoutMetrics.MeasureNow(GrowMenuHost);
            mw = Math.Max(menuSize.Width, 1);
            mh = menuSize.Height;
        }

        // 水平位置：以按钮为基准，并在内容区宽度内进行钳制
        double x = Math.Clamp(pBtnTopLeft.Value.X, pad, Math.Max(pad, hostW - mw - pad));

        // 垂直位置计算与边界保护
        double y;
        if (placeAbove)
        {
            y = barTop - gap - mh;
            if (y < pad) y = pad;
        }
        else
        {
            y = barBottom + gap;
            if (y + mh > hostH - pad)
            {
                y = Math.Max(pad, hostH - mh - pad);
            }
        }

        GrowMenuHost.Margin = new Thickness(x, y, 0, 0);

        // 茎部：水平对准源按钮中心，垂直方向依展开方向位于顶部（角朝上）或底部（角朝下）
        double stemCx = pBtnTopLeft.Value.X + _growMenuSource.Bounds.Width / 2 - x;
        double stemX = Math.Clamp(stemCx - GrowMenuHost.BorderThickness.Left - GrowMenuStem.Width / 2,
            6, Math.Max(6, mw - GrowMenuHost.BorderThickness.Left - GrowMenuHost.BorderThickness.Right - GrowMenuStem.Width - 6));

        if (placeAbove)
        {
            // 向上展开：茎部在菜单底部，旋转 45° 后「右+下」边框角朝下指向工具栏按钮
            GrowMenuStem.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            GrowMenuStem.BorderThickness = new Thickness(0, 0, 1, 1);
            GrowMenuStem.Margin = new Thickness(stemX, 0, 0, -6);
            GrowMenuHost.RenderTransformOrigin = new RelativePoint(
                Math.Clamp(stemCx / mw, 0.05, 0.95), 1.0, RelativeUnit.Relative);
        }
        else
        {
            // 向下展开：茎部在菜单顶部，旋转 45° 后「左+上」边框角朝上指向工具栏按钮
            GrowMenuStem.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            GrowMenuStem.BorderThickness = new Thickness(1, 1, 0, 0);
            GrowMenuStem.Margin = new Thickness(stemX, -6, 0, 0);
            GrowMenuHost.RenderTransformOrigin = new RelativePoint(
                Math.Clamp(stemCx / mw, 0.05, 0.95), 0.0, RelativeUnit.Relative);
        }
    }

    /// <summary>窗口级隧道：点击菜单与源按钮以外区域时收起生长菜单。</summary>
    private void OnGrowMenuGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_growMenuOpen || e.Source is not Visual src) return;

        bool inMenu = GrowMenuHost.IsVisualAncestorOf(src);
        bool onSource = _growMenuSource != null && _growMenuSource.IsVisualAncestorOf(src);
        if (!inMenu && !onSource)
        {
            CloseGrowMenu();
        }
    }

    /// <summary>选区双态切换：菜单交互期灰调可见（用户始终知道操作作用在哪段文字）。</summary>
    private void SetMergedSelectionInteractive(bool interactive)
    {
        TxtAllLines.SelectionBrush = interactive ? TextSelectionMutedBrush : TextSelectionBrush;
    }

    private CornerRadius ResRadius(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is CornerRadius radius ? radius : default;

    // ═══ 全窗口统一高亮色（复用同一套，禁止各处再自定） ═══
    // 已改/修正橙：与分行卡片 lineCard.edited、原图叠层 BrushEdit 同一语义同一色。
    // 此前排版修正胶囊走 ResBrush("OcrEditBrush") 被映射成品牌紫，出现"排版紫 / 分行橙"两套高亮。
    private IBrush EditBrush => ResBrush("SctOcrEditBrush");          // #E8862B 修正文字/描边
    private IBrush EditSoftBrush => ResBrush("SctOcrEditSoftBrush");  // #1FE8862B 胶囊软底
    private IBrush EditHoverBrush => ResBrush("SctOcrEditHoverBrush"); // #3DE8862B 悬停加深底

    // 文本选区紫：合并编辑框 / 排版 MD 预览共用。此前 MD 预览自带深紫 #8C4219F2、
    // 合并框在菜单交互后因 OcrSel* 键缺失被 ResBrush 兜底成灰——两套选区样式。
    private IBrush TextSelectionBrush => ResBrush("SctOcrSelectionBrush");    // #80797EFE
    private IBrush TextSelectionMutedBrush => ResBrush("SctOcrSelectionMutedBrush"); // 菜单交互期灰调

    /// <summary>
    /// 按当前主题（ActualThemeVariant）解析 Sct* 主题资源，兼容旧 Ocr* 键名自动映射。
    /// </summary>
    private IBrush ResBrush(string key)
    {
        string mappedKey = key switch
        {
            "OcrBgBrush" or "OcrPanelBrush" => "SctCardFillBrush",
            "OcrPanel2Brush" or "OcrDocBgBrush" or "OcrCanvasBgBrush" => "SctSubtleBrush",
            "OcrBorderBrush" or "OcrBorderStrongBrush" or "OcrDocBorderBrush" => "SctSoftBorderBrush",
            "OcrTextPrimaryBrush" => "SctTextPrimaryBrush",
            "OcrTextSecondaryBrush" => "SctTextSecondaryBrush",
            "OcrTextMutedBrush" => "SctTextMutedBrush",
            "OcrAccentBrush" or "OcrAccentLineBrush" => "SctAccentBrush",
            "OcrAccentSoftBrush" or "OcrActiveSoftBrush" or "OcrDangerSoftBrush" or "OcrOkSoftBrush" => "SctBadgeBgBrush",
            "OcrAccentTextBrush" => "SctBadgeTextBrush",
            "OcrDangerBrush" => "SctDangerBrush",
            // 注意：OcrEdit*（已改/修正橙）已收口为静态 EditBrush/EditSoftBrush/EditHoverBrush，
            // 不再走本映射（旧映射会错误落成品牌紫，造成"排版紫 / 分行橙"两套高亮）
            "OcrSegItemHoverBrush" => "SctButtonHoverBrush",
            "OcrToastBgBrush" or "OcrMenuBgColor" => "SctMenuBackgroundBrush",
            _ => key
        };

        if (this.TryFindResource(mappedKey, ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        if (this.TryFindResource(key, ActualThemeVariant, out var fallback) && fallback is IBrush fb)
            return fb;
        return Brushes.Gray;
    }

    /// <summary>对当前选区执行清除/校对算子；处理 0 处时保持原文并提示。</summary>
    private void ApplyTextOp(Func<string, TextOpResult> op, string label)
    {
        if (!_isMergedMode) return;
        if (_viewingInitial) { ShowToast("初始结果为只读，切回「修改」可处理"); return; }

        string? fullText = TxtAllLines.Text;
        int start = Math.Min(TxtAllLines.SelectionStart, TxtAllLines.SelectionEnd);
        int len = Math.Abs(TxtAllLines.SelectionEnd - TxtAllLines.SelectionStart);
        if (len == 0 && _pinnedSelLen > 0 && !string.IsNullOrEmpty(fullText))
        {
            // 弹窗抢焦点期间选区状态被清空 → 使用按下按钮时的快照
            start = Math.Min(_pinnedSelStart, Math.Max(0, fullText.Length - _pinnedSelLen));
            len = _pinnedSelLen;
        }
        if (len == 0 || string.IsNullOrEmpty(fullText))
        {
            ShowToast("请先选中文本");
            return;
        }

        string selected = fullText.Substring(start, len);
        var result = op(selected);
        if (result.Count == 0)
        {
            ShowToast($"未发现可处理的「{label}」内容");
            return;
        }

        // 替换选区文本并重新选中结果，支持链式连续处理
        TxtAllLines.Text = fullText.Remove(start, len).Insert(start, result.Text);
        TxtAllLines.SelectionStart = start;
        TxtAllLines.SelectionEnd = start + result.Text.Length;
        TxtAllLines.Focus();                 // 从子菜单收回焦点，避免失焦逻辑收起工具栏
        _isPointerSelecting = false;
        ScheduleTextToolbarUpdate();         // 立即按新选区重新定位（保留链式处理入口）
        ShowToast($"{label} · {result.Count} 处");
    }

    // —— 浮动工具栏显隐与定位 ——

    private void HideTextToolbar()
    {
        ToolbarTextTools.Opacity = 0;
        ToolbarTextTools.IsHitTestVisible = false;
    }

    private void ShowTextToolbar()
    {
        ToolbarTextTools.Opacity = 1;
        ToolbarTextTools.IsHitTestVisible = true;
    }

    private bool IsTextToolbarVisible => ToolbarTextTools.Opacity > 0;

    private bool _toolbarUpdatePending;
    private bool _isPointerSelecting;            // 鼠标左键按住选词中：期间工具栏保持隐藏
    private bool _textToolbarPinned;             // 菜单交互钉住：弹窗抢焦点期间不收起工具栏
    private int _pinnedSelStart, _pinnedSelLen;  // 按下按钮时的选区快照（失焦可能丢失选区状态，用快照兜底）
    private DateTime _lastTextFlyoutClosedAt = DateTime.MinValue;   // 子菜单/右键菜单最近关闭时刻

    /// <summary>
    /// 合并调度一次工具栏定位刷新（Background 优先级：等选区与文本布局稳定后再计算，多次触发自动合并）。
    /// 鼠标按住选词期间不刷新（松手才显示）。
    /// </summary>
    private void ScheduleTextToolbarUpdate()
    {
        if (!_isMergedMode || _toolbarUpdatePending || _isPointerSelecting) return;
        _toolbarUpdatePending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _toolbarUpdatePending = false;
            UpdateTextToolbarPosition();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>内容区尺寸变化（窗口 resize / 分栏切换）：打开中的生长菜单立即重定位，可见工具栏调度重定位。</summary>
    private void OnTxtContentHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_growMenuOpen) UpdateGrowMenuPosition();
        else if (IsTextToolbarVisible) ScheduleTextToolbarUpdate();
    }

    /// <summary>选区属性变化（程序化选中等场景）：调度工具栏定位刷新。</summary>
    private void OnMergedBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_isMergedMode)
        {
            HideTextToolbar();
            return;
        }
        if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
        {
            ScheduleTextToolbarUpdate();
        }
    }

    // —— 鼠标拖选 / 键盘选区兜底触发（隧道订阅） ——

    private void OnMergedBoxTunnelPointer(object? sender, PointerEventArgs e)
    {
        if (!_isMergedMode) return;

        if (e is PointerPressedEventArgs pressed)
        {
            var props = pressed.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                // 右键即将打开上下文菜单：钉住工具栏并快照选区（弹窗会抢焦点）
                _textToolbarPinned = true;
                SnapshotMergedSelection();
                return;
            }
            if (props.IsLeftButtonPressed)
            {
                // 左键开始新的选择：立即隐藏，松手后才按最终选区重新显示
                _isPointerSelecting = true;
                _textToolbarPinned = false;
                HideTextToolbar();
                return;
            }
        }

        if (e is PointerReleasedEventArgs)
        {
            _isPointerSelecting = false;
            ScheduleTextToolbarUpdate();
        }
        // 拖选过程中（按住左键移动）：保持隐藏，不做任何响应
    }

    private void OnMergedBoxTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        if (_isPointerSelecting) return;
        ScheduleTextToolbarUpdate();
    }

    // —— 菜单交互的焦点与选区保护 ——

    /// <summary>按下「清除 / 校对」按钮：先于 Click/Flyout 执行，钉住工具栏并快照选区。</summary>
    private void OnToolbarFlyoutButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        _textToolbarPinned = true;
        SnapshotMergedSelection();
    }

    private void SnapshotMergedSelection()
    {
        _pinnedSelStart = Math.Min(TxtAllLines.SelectionStart, TxtAllLines.SelectionEnd);
        _pinnedSelLen = Math.Abs(TxtAllLines.SelectionEnd - TxtAllLines.SelectionStart);
    }

    /// <summary>子菜单/右键菜单关闭：解除钉住，恢复焦点与选区高亮并重定位工具栏。</summary>
    private void OnTextMenuClosed()
    {
        _textToolbarPinned = false;
        _lastTextFlyoutClosedAt = DateTime.UtcNow;
        RestoreMergedSelectionAfterMenu();
    }

    private void RestoreMergedSelectionAfterMenu()
    {
        if (!_isMergedMode) return;

        int start = Math.Min(TxtAllLines.SelectionStart, TxtAllLines.SelectionEnd);
        int len = Math.Abs(TxtAllLines.SelectionEnd - TxtAllLines.SelectionStart);
        if (len == 0 && _pinnedSelLen > 0)
        {
            // 失焦期间选区状态被清空 → 用按下时的快照恢复
            start = _pinnedSelStart;
            len = _pinnedSelLen;
        }
        _pinnedSelLen = 0;

        if (len == 0)
        {
            HideTextToolbar();
            return;
        }

        TxtAllLines.Focus();
        // 折叠再展开强制触发选区属性变化，恢复高亮重绘并重定位工具栏
        TxtAllLines.SelectionStart = start;
        TxtAllLines.SelectionEnd = start;
        TxtAllLines.SelectionStart = start;
        TxtAllLines.SelectionEnd = start + len;
    }

    private void UpdateTextToolbarPosition()
    {
        string? fullText = TxtAllLines.Text;
        int len = Math.Abs(TxtAllLines.SelectionEnd - TxtAllLines.SelectionStart);
        if (len == 0 || string.IsNullOrEmpty(fullText))
        {
            // 菜单交互钉住期间（弹窗抢焦点可能瞬间清空选区属性）不收起
            if (!_textToolbarPinned) HideTextToolbar();
            return;
        }

        int start = Math.Min(TxtAllLines.SelectionStart, TxtAllLines.SelectionEnd);
        var presenter = TxtAllLines.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter?.TextLayout == null)
        {
            return;
        }

        var rects = presenter.TextLayout.HitTestTextRange(start, len).ToList();
        if (rects.Count == 0)
        {
            if (!_textToolbarPinned) HideTextToolbar();
            return;
        }

        double centerX = (rects.Min(r => r.Left) + rects.Max(r => r.Right)) / 2;
        double bottom = rects.Max(r => r.Bottom);
        Point? pBottom = presenter.TranslatePoint(new Point(centerX, bottom), TxtContentHost);
        if (pBottom == null)
        {
            return;
        }

        // 控件常驻可视化树（Opacity 控制显隐）。与生长菜单同理：
        // DesiredSize 包含 Margin，必须先清零旧定位再测量，否则宽高混入旧坐标导致工具栏逐次漂移
        ToolbarTextTools.Margin = default;
        ToolbarTextTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fw = ToolbarTextTools.DesiredSize.Width;
        double fh = ToolbarTextTools.DesiredSize.Height;
        if (fw <= 0 || fh <= 0) return;

        double hostW = TxtContentHost.Bounds.Width;
        double hostH = TxtContentHost.Bounds.Height;
        double x = Math.Clamp(pBottom!.Value.X - fw / 2, 4, Math.Max(4, hostW - fw - 4));
        // 固定显示在选区下方 8px（不向上翻转）；选区贴近面板底部时上移贴边防止越出
        double y = Math.Min(pBottom.Value.Y + 8, Math.Max(2, hostH - fh - 4));
        ToolbarTextTools.Margin = new Thickness(x, y, 0, 0);
        ShowTextToolbar();
    }

    /// <summary>
    /// 失焦收起：菜单交互钉住中、子菜单/右键菜单打开、指针悬停工具栏、菜单刚关闭（450ms 宽限）时保留，否则隐藏。
    /// </summary>
    private void OnMergedBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // 工具栏显示时 IsHitTestVisible=true，IsPointerOver 有效
            if (_textToolbarPinned
                || (IsTextToolbarVisible && ToolbarTextTools.IsPointerOver)
                || AnyTextFlyoutOpen()
                || (DateTime.UtcNow - _lastTextFlyoutClosedAt).TotalMilliseconds < 450)
            {
                return;
            }
            HideTextToolbar();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private bool AnyTextFlyoutOpen() =>
        _growMenuOpen
        || MnuCtxClear.IsSubMenuOpen
        || MnuCtxProof.IsSubMenuOpen
        || TxtAllLines.ContextMenu?.IsOpen == true;

    private void OnMergedBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_growMenuOpen)
            {
                // 菜单打开时 Esc 只收起菜单（工具栏与选区保持），并拦截冒泡避免误关窗口
                CloseGrowMenu();
                e.Handled = true;
                return;
            }
            HideTextToolbar();
        }
    }

    // —— 基础编辑与全选 ——

    private void OnTtCutClick(object? sender, RoutedEventArgs e) => TxtAllLines.Cut();

    private void OnTtCopyClick(object? sender, RoutedEventArgs e) => TxtAllLines.Copy();

    private void OnTtPasteClick(object? sender, RoutedEventArgs e) => TxtAllLines.Paste();

    private void OnSelectAllTextClick(object? sender, RoutedEventArgs e)
    {
        TxtAllLines.Focus();
        TxtAllLines.SelectAll();
    }

    #endregion

    #region 分行视图：行卡片右键菜单

    private void OnCtxCopyLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OcrLineItem item })
        {
            ClipboardService.SetText(item.CurrentText);
            ShowToast($"已复制第 {item.Index + 1} 行");
        }
    }

    private void OnCtxCopyAllClick(object? sender, RoutedEventArgs e) => OnCopyAllClick(sender, e);

    private void OnCtxEditLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OcrLineItem item })
        {
            EnterLineEdit(item);
        }
    }

    /// <summary>进入单行编辑：置为编辑态并聚焦对应 EditBox（右键菜单与 F2 共用）。</summary>
    private void EnterLineEdit(OcrLineItem item)
    {
        if (_viewingInitial) { ShowToast("初始结果为只读，切回「修改」可编辑"); return; }
        if (item.IsEditMode) return;
        item.SetSingleEdit(true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var tb = ItemsTextLines.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => ReferenceEquals(t.DataContext, item));
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnCtxHighlightLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OcrLineItem item })
        {
            if (item.IsEditMode || _isEditMode)
            {
                ShowToast("编辑模式下不可联动高亮");
                return;
            }
            if (!_linkageEnabled)
            {
                ShowToast("联动高亮已关闭");
                return;
            }
            SelectLine(item, scrollRight: false);
        }
    }

    /// <summary>右键删除行：删除当前行并重排序号、联动与状态栏。</summary>
    private void OnCtxDeleteLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OcrLineItem item })
        {
            DeleteLine(item);
        }
    }

    /// <summary>删除指定行并同步状态（右键菜单与 Del 共用）。</summary>
    private void DeleteLine(OcrLineItem item)
    {
        if (_viewingInitial) { ShowToast("初始结果为只读"); return; }
        int removedNumber = item.IndexNumber;
        _lineItems.Remove(item);
        ClearSelection();
        ReIndexLines();
        SyncEditedIndicesToOverlay();
        RefreshStatusBar();
        ShowToast(_lineItems.Count > 0
            ? $"已删除第 {removedNumber} 行，剩余 {_lineItems.Count} 行"
            : "已删除最后一行");
    }

    #endregion

    #region 图片预览缩放与平移

    /// <summary>
    /// 按图片宽高比自适应主体分栏（每次内容更新均完整重应用，幂等）：
    /// 宽 &gt; 高×1.5（明显横图）→ 上下分栏（原图在上、文本在下，原图高度占比按图比例自适应 45%~60%，图越扁占比越小）；
    /// 其余（竖图 / 方图 / 轻横图）→ 左右分栏（原图左 47%、文本右 53%）。
    /// </summary>
    private void ApplyAdaptiveLayout()
    {
        if (_imgW <= 0 || _imgH <= 0) return;

        // 仅明显横图（宽比高多 50% 以上）才转上下分栏，接近方形的图保持左右分栏
        bool portrait = _imgW <= _imgH * 1.5;

        if (portrait)
        {
            // 竖图/方图/轻横图：左右分栏（XAML 默认布局）
            BodyGrid.ColumnDefinitions = new ColumnDefinitions("4.7*,1,5.3*");
            BodyGrid.RowDefinitions.Clear();
            Grid.SetRow(ImgPane, 0);
            Grid.SetColumn(ImgPane, 0);
            Grid.SetRow(BodySplitter, 0);
            Grid.SetColumn(BodySplitter, 1);
            Grid.SetRow(TxtPane, 0);
            Grid.SetColumn(TxtPane, 2);
            BodySplitter.Width = 1;
            BodySplitter.Height = double.NaN;
        }
        else
        {
            // 明显横图：上下分栏，原图高度占比 = 0.6 × 高宽比，限制 45%~60%
            double frac = Math.Clamp(0.6 * _imgH / _imgW, 0.45, 0.60);
            int top = (int)Math.Round(frac * 100);
            BodyGrid.RowDefinitions = new RowDefinitions($"{top}*,1,{100 - top}*");
            BodyGrid.ColumnDefinitions.Clear();
            Grid.SetRow(ImgPane, 0);
            Grid.SetColumn(ImgPane, 0);
            Grid.SetRow(BodySplitter, 1);
            Grid.SetColumn(BodySplitter, 0);
            Grid.SetRow(TxtPane, 2);
            Grid.SetColumn(TxtPane, 0);
            BodySplitter.Height = 1;
            BodySplitter.Width = double.NaN;
        }

        // 分栏变化后重新自适应图片缩放
        if (_fitMode && _image != null)
        {
            ApplyFitScale();
        }
    }

    private double ComputeFitScale()
    {
        if (_imgW <= 0 || _imgH <= 0) return 1.0;
        double availW = Math.Max(ScrollImg.Bounds.Width - 52, 20);
        double availH = Math.Max(ScrollImg.Bounds.Height - 52, 20);
        double scaleW = availW / _imgW;
        double scaleH = availH / _imgH;
        return Math.Min(scaleW, scaleH);
    }

    private void ApplyFitScale()
    {
        if (_imgW <= 0 || _imgH <= 0) return;
        double fitScale = ComputeFitScale();
        ApplyScale(fitScale);
    }

    private void ApplyScale(double scale)
    {
        _scale = Math.Clamp(scale, 0.05, 10.0);
        double w = Math.Max(1, Math.Round(_imgW * _scale));
        double h = Math.Max(1, Math.Round(_imgH * _scale));

        ImgContainer.Width = w;
        ImgContainer.Height = h;
        ImgPreview.Width = w;
        ImgPreview.Height = h;
        HighlightOverlay.Width = w;
        HighlightOverlay.Height = h;
        HighlightOverlay.Scale = _scale;
    }

    private void OnCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_fitMode && _image != null)
        {
            ApplyFitScale();
        }
    }

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_image == null || _imgW <= 0 || _imgH <= 0) return;
        e.Handled = true;

        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        double newScale = Math.Clamp(_scale * factor, 0.05, 10.0);
        if (Math.Abs(newScale - _scale) < 0.0001) return;

        var vp = e.GetPosition(ScrollImg);
        double contentX = (ScrollImg.Offset.X + vp.X) / _scale;
        double contentY = (ScrollImg.Offset.Y + vp.Y) / _scale;

        _fitMode = false;
        ApplyScale(newScale);

        ScrollImg.Offset = new Vector(
            Math.Max(0, contentX * _scale - vp.X),
            Math.Max(0, contentY * _scale - vp.Y));
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_image == null) return;
        var cp = e.GetCurrentPoint(CanvasHost);
        if (cp.Properties.IsLeftButtonPressed)
        {
            _panCandidate = true;
            _isPanning = false;
            _panStartPoint = e.GetPosition(this);
            _panStartOffset = ScrollImg.Offset;
            // 注意：不要在此处直接 Capture(CanvasHost)，否则子控件（HighlightOverlay）无法收到 PointerReleased 判定点击
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_panCandidate)
        {
            var cur = e.GetPosition(this);
            var delta = cur - _panStartPoint;
            double dist = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

            if (dist > 5.0)
            {
                if (!_isPanning)
                {
                    _isPanning = true;
                    _fitMode = false;
                    e.Pointer.Capture(CanvasHost);
                    CanvasHost.Cursor = new Cursor(StandardCursorType.Hand);
                }
                ScrollImg.Offset = new Vector(
                    Math.Max(0, _panStartOffset.X - delta.X),
                    Math.Max(0, _panStartOffset.Y - delta.Y));
            }
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            e.Pointer.Capture(null);
            CanvasHost.Cursor = Cursor.Default;
        }
        _panCandidate = false;
        _isPanning = false;
    }

    private void OnCanvasPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _panCandidate = false;
        _isPanning = false;
        CanvasHost.Cursor = Cursor.Default;
    }

    private void OnCanvasDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        _fitMode = true;
        ApplyFitScale();
        ScrollImg.Offset = new Vector(0, 0);
    }

    #endregion

    #region 悬浮 Toast 提示

    private int _toastToken = 0;

    private async void ShowToast(string message)
    {
        try
        {
            if (ToastPanel == null || TxtToast == null) return;
            TxtToast.Text = message;
            ToastPanel.IsVisible = true;
            ToastPanel.Opacity = 1.0;

            int currentToken = ++_toastToken;
            await Task.Delay(1800);
            if (_toastToken == currentToken)
            {
                ToastPanel.IsVisible = false;
                ToastPanel.Opacity = 0.0;
            }
        }
        catch { }
    }

    #endregion
}
