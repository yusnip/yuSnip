using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 翻译编辑窗口（E 案：译文为主 · 原文抽屉）：右侧整篇译文优先排版，
/// 原文收进左侧默认 200px 的抽屉（分割线可拖拽调宽、线正中箭头点击收展）且展开即可编辑；
/// 源语言与目标语言都在语言行上可见可控。
/// 与极简浮窗 <see cref="TranslateMiniWindow"/> 共享 <see cref="TranslationResult"/>，
/// 由浮窗升级而来时直接进入结果态，不再重复请求。
/// </summary>
public partial class TranslationResultWindow : Window
{
    /// <summary>原文输入上限（字符）。</summary>
    private const int MaxInputChars = 8000;

    /// <summary>窗口宽度小于该值时：原文抽屉与译文改为上下堆叠，分割线同步转为横向可拖。</summary>
    private const double StackedWidthThreshold = 620;

    /// <summary>原文抽屉默认宽度（并排版式，可拖拽改，双击分割线复位到此值）。</summary>
    private const double DrawerWidthDefault = 200;

    /// <summary>原文抽屉可拖拽范围（并排版式）。</summary>
    private const double DrawerWidthMin = 120;
    private const double DrawerWidthMax = 420;

    /// <summary>译文栏最小宽度，防止把正文拖到没法读。</summary>
    private const double TargetWidthMin = 240;

    /// <summary>堆叠态下原文抽屉默认高度与可拖拽范围。</summary>
    private const double DrawerStackHeight = 96;
    private const double DrawerHeightMin = 56;
    private const double DrawerHeightMax = 240;

    /// <summary>分割带宽度：1px 发丝线居中，余量给拖拽命中与那枚箭头按钮。</summary>
    private const double SplitterWidth = 8;

    /// <summary>
    /// 收起态分割带右移量：抽屉归零后分割列会贴到窗口左缘，被系统缩放边框吃掉点击（表现为缩放箭头、点不动）。
    /// 用负右边距保持握条原宽，只把整条命中区让开这条边框。
    /// </summary>
    private const double CollapsedGripInset = 12;

    private AppSettings _settings = new();
    private TranslationConfig _config = new();
    private string _engineName = string.Empty;
    private string _currentSourceLang = TranslationLanguage.Auto;
    private string _currentTargetLang = TranslationLanguage.ChineseSimplified;
    private string? _detectedSourceLang;

    private CancellationTokenSource? _cts;
    private bool _stacked;
    private bool _drawerOpen = true;

    /// <summary>用户拖出的抽屉尺寸：并排态记宽、堆叠态记高，跨阈值重排时回填。</summary>
    private double _drawerWidth = DrawerWidthDefault;
    private double _drawerHeight = DrawerStackHeight;

    private bool _loading;
    private bool _suppressLangEvents;
    private bool _syncingScroll;
    private bool _dividerDragging;
    private double _dividerDragStart;
    private double _dividerDragStartSize;
    private bool _dividerDidReset;
    private bool _gripHover;

    /// <summary>窗口尺寸/抽屉尺寸的防抖落盘计时器（改完 500ms 无进一步变化再写盘，避免拖动过程狂写 ini）。</summary>
    private DispatcherTimer? _saveGeomTimer;

    /// <summary>首帧布局标志：SetupCommon 回填抽屉尺寸后置真，让紧随的首次 ApplyLayout 跳过 CaptureDrawerSize，否则会被栅格默认值覆盖。</summary>
    private bool _skipInitialCapture;

    /// <summary>鼠标所在栏为主滚动区，避免双向抖动。</summary>
    private ScrollViewer? _activeScroll;
    private IReadOnlyList<TranslationTextPair> _sentencePairs = Array.Empty<TranslationTextPair>();
    private string _pairedSource = "", _pairedTarget = "";
    private string _requestSource = "";
    private bool _syncingSentenceSelection;
    private int _selectionVersion;

    public TranslationResultWindow()
    {
        InitializeComponent();
        ApplyIcons();

        Closed += OnWindowClosed;
        Opened += OnWindowOpened;
        SizeChanged += OnSizeChanged;

        // 快捷键走隧道预览：确保原文框获得焦点时 Ctrl+Enter 等也不会被输入控件吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, true);

        // 分割带统一交互（拖拽 / 点击收展 / 双击复位）由 DividerGrip 的 XAML 事件属性直接绑定

        BuildLanguageCombos();
        TxtSource.PropertyChanged += OnSentenceSelectionChanged;
        TxtTarget.PropertyChanged += OnSentenceSelectionChanged;
        TxtSource.AddHandler(PointerReleasedEvent, OnSourceSentenceReleased, RoutingStrategies.Bubble, true);
        TxtTarget.AddHandler(PointerReleasedEvent, OnTargetSentenceReleased, RoutingStrategies.Bubble, true);
    }

    // ── 工厂方法 ──────────────────────────────────────────────

    /// <summary>用已有原文打开（剪贴板翻译 / 贴图文本翻译），打开后自动翻译。</summary>
    public static TranslationResultWindow ForText(string text, AppSettings settings)
    {
        var win = new TranslationResultWindow();
        win.SetupCommon(settings);
        win.TxtSource.Text = text ?? string.Empty;

        var (effSrc, effTgt, switched, _) = LanguageDetector.ResolveLanguages(
            text, win._currentSourceLang, win._currentTargetLang, win._config.SecondaryTargetLanguage);
        if (switched)
        {
            win._currentTargetLang = effTgt;
            win.SyncLanguageCombos();
        }

        win.UpdateCounts();
        win.UpdateEmptyState();
        _ = win.DoTranslateAsync();
        return win;
    }

    /// <summary>
    /// 用浮窗已有的结果打开（升级路径）：直接展示，不重复请求；
    /// 若结果尚无译文（浮窗仍在加载或失败），则由本窗口补一次翻译。
    /// </summary>
    public static TranslationResultWindow FromResult(TranslationResult result, AppSettings settings)
    {
        var win = new TranslationResultWindow();
        win.SetupCommon(settings);

        // 结果的语言对优先于设置，保证与浮窗看到的一致
        if (!string.IsNullOrEmpty(result.SourceLanguage)) win._currentSourceLang = result.SourceLanguage;
        if (!string.IsNullOrEmpty(result.TargetLanguage)) win._currentTargetLang = result.TargetLanguage;
        win._detectedSourceLang = result.DetectedSourceLanguage;
        win.SyncLanguageCombos();

        win.TxtSource.Text = result.SourceText ?? string.Empty;
        win.TxtTarget.Text = result.TargetText ?? string.Empty;
        win._sentencePairs = result.Pairs;
        win._pairedSource = win.TxtSource.Text ?? ""; win._pairedTarget = win.TxtTarget.Text ?? "";
        win.UpdateCounts();
        win.UpdateEmptyState();

        if (!string.IsNullOrEmpty(result.TargetText))
        {
            win.SetStatus(result.ElapsedSeconds > 0
                ? $"翻译完成 · {result.ElapsedSeconds:F1}s"
                : "翻译完成");
        }
        else
        {
            _ = win.DoTranslateAsync();
        }

        return win;
    }

    // ── 初始化 ────────────────────────────────────────────────

    private void SetupCommon(AppSettings settings)
    {
        _settings = settings;

        _config = settings.ToTranslationConfig();
        _currentSourceLang = _config.SourceLanguage;
        _currentTargetLang = _config.TargetLanguage;
        _engineName = TranslationFlow.GetEngineName(_config);

        bool hasEngine = !string.IsNullOrEmpty(_engineName);
        if (hasEngine)
        {
            TxtStatusEngine.Text = _engineName;
            StatusEngineBadge.IsVisible = true;
        }

        SyncLanguageCombos();
        RestoreWindowGeometry();
        ApplyLayout();
    }

    /// <summary>从设置回填上次记住的窗口尺寸与抽屉尺寸；非法或越界则回默认，并置首帧标志避免被栅格默认值覆盖。</summary>
    private void RestoreWindowGeometry()
    {
        double w = _settings.TranslationEditWindowWidth;
        double h = _settings.TranslationEditWindowHeight;
        if (w >= MinWidth) Width = w;
        if (h >= MinHeight) Height = h;

        double dw = _settings.TranslationEditDrawerWidth;
        _drawerWidth = dw >= DrawerWidthMin && dw <= DrawerWidthMax ? dw : DrawerWidthDefault;
        double dh = _settings.TranslationEditDrawerHeight;
        _drawerHeight = dh >= DrawerHeightMin && dh <= DrawerHeightMax ? dh : DrawerStackHeight;

        _skipInitialCapture = true;
    }

    /// <summary>把几何常量挂到 XAML 里的图标 Path（几何数据集中在 TranslateIconGeometry）。</summary>
    private void ApplyIcons()
    {
        IconTitle.Data = StreamGeometry.Parse(TranslateIconGeometry.Translate);
        // 标题栏与设置页共用细横线，控件盒按实墨高度 1 DIP 居中，避免 14×14 空盒把横线顶到上方。
        IconMinimize.Data = StreamGeometry.Parse(WorkspaceIconGeometry.Minimize);
        IconMaximize.Data = StreamGeometry.Parse(TranslateIconGeometry.Maximize);
        IconClose.Data = StreamGeometry.Parse(WorkspaceIconGeometry.Close);
        IconSwap.Data = StreamGeometry.Parse(TranslateIconGeometry.Swap);
        IconStatusEngineSpark.Data = StreamGeometry.Parse(TranslateIconGeometry.Spark);
        IconRefresh.Data = StreamGeometry.Parse(TranslateIconGeometry.Refresh);
        IconEmpty.Data = StreamGeometry.Parse(TranslateIconGeometry.Document);
        UpdateDividerGripCursor();
    }

    /// <summary>分割带游标：并排态左右拖、堆叠态上下拖，收起后变手型（此时它只是"点开恢复原文"的按钮）。</summary>
    private void UpdateDividerGripCursor()
    {
        if (!_drawerOpen)
        {
            DividerGrip.Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }
        DividerGrip.Cursor = new Cursor(_stacked
            ? StandardCursorType.SizeNorthSouth
            : StandardCursorType.SizeWestEast);
    }

    /// <summary>
    /// 握条尺寸：悬浮时放大一档，给出"这是一枚可点/可拖的把手"的明确affordance（用户嫌原来的细线太难命中）。
    /// 纵向（并排）3×28 ↔ 5×40，横向（堆叠）28×3 ↔ 40×5，只改可见握条，命中列宽不变。
    /// </summary>
    private void UpdateDividerGripVisual()
    {
        if (_stacked)
        {
            DividerGripBar.Width = _gripHover ? 40 : 28;
            DividerGripBar.Height = _gripHover ? 5 : 3;
        }
        else
        {
            DividerGripBar.Width = _gripHover ? 5 : 3;
            DividerGripBar.Height = _gripHover ? 40 : 28;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // 关窗前把挂起的尺寸变化落盘，避免"拖完立刻关掉"丢最后一帧
        _saveGeomTimer?.Stop();
        SaveCurrentGeometry();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ── 语言下拉 ──────────────────────────────────────────────

    private void BuildLanguageCombos()
    {
        _suppressLangEvents = true;
        foreach (var (name, code) in TranslationLanguage.SupportedSourceLanguages)
        {
            CmbSourceLang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        foreach (var (name, code) in TranslationLanguage.SupportedTargetLanguages)
        {
            CmbTargetLang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        _suppressLangEvents = false;
    }

    /// <summary>把当前语言写回下拉框选中项与两栏头部标签。</summary>
    private void SyncLanguageCombos()
    {
        _suppressLangEvents = true;
        CmbSourceLang.SelectedIndex = IndexOfLanguage(CmbSourceLang, _currentSourceLang);
        CmbTargetLang.SelectedIndex = IndexOfLanguage(CmbTargetLang, _currentTargetLang);
        _suppressLangEvents = false;

        UpdateLabels();
    }

    /// <summary>两栏头部纯文字标签：语言与字数就近显示，不再用徽标。</summary>
    private void UpdateLabels()
    {
        string sourceLang = _currentSourceLang == TranslationLanguage.Auto && _detectedSourceLang != null
            ? "检测到" + TranslationLanguage.GetDetectedLanguageDisplayName(_detectedSourceLang)
            : TranslationLanguage.GetDisplayName(_currentSourceLang);
        string targetLang = TranslationLanguage.GetDisplayName(_currentTargetLang);

        int srcLen = TxtSource.Text?.Length ?? 0;
        int tgtLen = TxtTarget.Text?.Length ?? 0;

        TxtSourceLab.Text = srcLen > 0 ? $"原文 · {sourceLang} · {srcLen} 字" : $"原文 · {sourceLang}";
        TxtTargetLab.Text = tgtLen > 0 ? $"译文 · {targetLang} · {tgtLen} 字" : $"译文 · {targetLang}";
    }

    private static int IndexOfLanguage(ComboBox combo, string code)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    private void OnSourceLangChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLangEvents) return;
        if (CmbSourceLang.SelectedItem is ComboBoxItem item)
        {
            _currentSourceLang = item.Tag?.ToString() ?? TranslationLanguage.Auto;
            SyncLanguageCombos();
            _ = DoTranslateAsync();
        }
    }

    private void OnTargetLangChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLangEvents) return;
        if (CmbTargetLang.SelectedItem is ComboBoxItem item)
        {
            _currentTargetLang = item.Tag?.ToString() ?? TranslationLanguage.ChineseSimplified;
            SyncLanguageCombos();
            _ = DoTranslateAsync();
        }
    }

    /// <summary>交换语言：互换源/目标语言，并交换两栏文本。</summary>
    private void OnSwapClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string newSource = _currentTargetLang;
        string newTarget = _currentSourceLang;

        // 源语言为 auto 时，用检测到的语言作为新的目标语言
        if (newTarget == TranslationLanguage.Auto)
        {
            newTarget = !string.IsNullOrEmpty(_detectedSourceLang)
                ? TranslateDetectedToInternal(_detectedSourceLang)
                : TranslationLanguage.ChineseSimplified;
        }

        _currentSourceLang = newSource;
        _currentTargetLang = newTarget;
        SyncLanguageCombos();

        string oldSourceText = TxtSource.Text ?? string.Empty;
        TxtSource.Text = TxtTarget.Text ?? string.Empty;
        TxtTarget.Text = oldSourceText;
        UpdateCounts();
        UpdateEmptyState();

        if (!string.IsNullOrWhiteSpace(TxtSource.Text))
        {
            _ = DoTranslateAsync();
        }
    }

    /// <summary>将 API 返回的检测语言代码转换为内部代码。</summary>
    private static string TranslateDetectedToInternal(string detected)
    {
        string code = detected.Trim();
        if (code.Contains('-')) code = code.Split('-')[0];

        foreach (var (_, internalCode) in TranslationLanguage.SupportedTargetLanguages)
        {
            if (string.Equals(code, internalCode, StringComparison.OrdinalIgnoreCase))
                return internalCode;
        }

        return code switch
        {
            "jp" => TranslationLanguage.Japanese,
            "kor" => TranslationLanguage.Korean,
            "fra" => TranslationLanguage.French,
            "spa" => TranslationLanguage.Spanish,
            "may" => TranslationLanguage.Malay,
            "vie" => TranslationLanguage.Vietnamese,
            "ara" => TranslationLanguage.Arabic,
            "cht" => TranslationLanguage.ChineseTraditional,
            _ => TranslationLanguage.English,
        };
    }

    // ── 翻译执行 ──────────────────────────────────────────────

    private Task DoTranslateAsync() => TranslateAsync(new TranslationService(_config));

    internal async Task TranslateAsync(TranslationService translator)
    {
        string text = TxtSource.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("");
            return;
        }

        if (text.Length > MaxInputChars)
        {
            SetStatus($"原文超出 {MaxInputChars} 字符上限，请精简后再翻译", true);
            return;
        }

        if (!_config.IsValid)
        {
            SetStatus("请在设置中启用 bilibili-index，或配置所选翻译服务的密钥", true);
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string sourceLang = _currentSourceLang, targetLang = _currentTargetLang;
        _requestSource = text;
        InvalidateSentencePairs();

        SetLoading(true);
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await translator.TranslateDocumentAsync(text, sourceLang, targetLang, null, ct).ConfigureAwait(false);
            sw.Stop();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || text != TxtSource.Text) return;
                if (result.Success)
                {
                    UsageStatistics.RecordTranslate(); // 统计：翻译成功一次
                    TxtTarget.Text = result.TranslatedText ?? string.Empty;
                    _sentencePairs = result.Pairs;
                    _pairedSource = text; _pairedTarget = TxtTarget.Text;
                    _detectedSourceLang = result.DetectedSourceLanguage;
                    SyncLanguageCombos();
                    UpdateCounts();
                    SetStatus($"翻译完成 · {sw.Elapsed.TotalSeconds:F1}s");
                }
                else
                {
                    SetStatus($"翻译失败：{result.ErrorMessage}", true);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("TranslationResultWindow.DoTranslateAsync 异常", ex);
            await Dispatcher.UIThread.InvokeAsync(() => { if (!ct.IsCancellationRequested) SetStatus("翻译失败：" + ex.Message, true); });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (!ct.IsCancellationRequested) { SetLoading(false); UpdateStatusBarHint(); } });
        }
    }

    // ── UI 状态 ───────────────────────────────────────────────

    private void SetLoading(bool loading)
    {
        _loading = loading;
        SetStatusClass(StatusDot, "busy", loading);
        BtnTranslate.IsEnabled = !loading;
        BtnReTranslate.IsEnabled = !loading;
        if (loading) SetStatus("翻译中…");
        UpdateCounts();
        UpdateEmptyState();
    }

    private void SetStatus(string text, bool isWarning = false)
    {
        TxtStatus.Text = text;
        SetStatusClass(TxtStatus, "warn", isWarning);
    }

    /// <summary>状态色只切换修饰 class，颜色由样式里的 DynamicResource 随主题解析。</summary>
    private static void SetStatusClass(Avalonia.Controls.Control target, string stateClass, bool on)
    {
        if (on) target.Classes.Add(stateClass);
        else target.Classes.Remove(stateClass);
    }

    private void UpdateCounts()
    {
        int srcLen = TxtSource.Text?.Length ?? 0;
        bool over = srcLen > MaxInputChars;

        UpdateLabels();
        SetStatusClass(TxtSourceLab, "warn", over);

        bool canTranslate = srcLen > 0 && !over && !_loading;
        BtnTranslate.IsEnabled = canTranslate;
        BtnReTranslate.IsEnabled = canTranslate;
    }

    /// <summary>空态 / 骨架屏 / 译文三者互斥：原文为空时只留一行提示，并保证抽屉展开可输入。</summary>
    private void UpdateEmptyState()
    {
        bool empty = string.IsNullOrWhiteSpace(TxtSource.Text);

        EmptyPanel.IsVisible = empty;
        SkeletonPanel.IsVisible = _loading && !empty;
        TxtTarget.IsVisible = !empty && !_loading;

        if (empty && !_drawerOpen) SetDrawerOpen(true);
    }

    // ── 版式：抽屉收展、堆叠降级与同步滚动 ───────────────────

    /// <summary>按当前宽度重排主体：并排（原文 ‖ 分割带 ‖ 译文）↔ 上下堆叠，分割带始终与抽屉同轴可拖。</summary>
    private void ApplyLayout()
    {
        // 回填尺寸后的首帧布局不能被栅格默认值反向覆盖，跳过一次回读
        if (_skipInitialCapture) _skipInitialCapture = false;
        else CaptureDrawerSize();
        _stacked = CurrentWidth() < StackedWidthThreshold;

        if (_stacked)
        {
            BodyGrid.ColumnDefinitions.Clear();
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            BodyGrid.RowDefinitions.Clear();
            BodyGrid.RowDefinitions.Add(new RowDefinition { MinHeight = DrawerHeightMin, MaxHeight = DrawerHeightMax });
            BodyGrid.RowDefinitions.Add(new RowDefinition(SplitterWidth, GridUnitType.Pixel));
            BodyGrid.RowDefinitions.Add(new RowDefinition { MinHeight = TargetWidthMin });

            // 堆叠态握条转横向（尺寸统一交给 UpdateDividerGripVisual 按悬浮态算）

            // 堆叠态只收窄留白，字号一律沿用样式里的规范值 13.5
            SrcHost.Margin = new Thickness(14, 8, 14, 8);
            TgtPane.Margin = new Thickness(14, 9, 14, 10);

            PlaceBody(SrcPane, 0, 0);
            PlaceBody(TgtPane, 0, 2);
            PlaceBody(DividerGrip, 0, 1);
        }
        else
        {
            BodyGrid.RowDefinitions.Clear();
            BodyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            BodyGrid.ColumnDefinitions.Clear();
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = DrawerWidthMin, MaxWidth = DrawerWidthMax });
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = TargetWidthMin });

            // 并排态握条保持纵向（尺寸统一交给 UpdateDividerGripVisual 按悬浮态算）

            SrcHost.Margin = new Thickness(12, 10, 12, 12);
            TgtPane.Margin = new Thickness(18, 12, 18, 14);

            PlaceBody(SrcPane, 0, 0);
            PlaceBody(TgtPane, 2, 0);
            PlaceBody(DividerGrip, 1, 0);
        }

        UpdateDividerGripCursor();
        UpdateDividerGripVisual();
        ApplyDrawerSize();
        UpdateStatusBarHint();
    }

    /// <summary>摆主体元素：Grid 的附加属性只能用静态调用，写成实例方法会 CS0176。</summary>
    private static void PlaceBody(Control child, int column, int row)
    {
        Grid.SetColumn(child, column);
        Grid.SetRow(child, row);
    }

    private double CurrentWidth() => Bounds.Width > 0 ? Bounds.Width : Width;

    /// <summary>把格子里的实时尺寸（可能已被 GridSplitter 拖改）记回字段，供收起后再展开或跨阈值重排时回填。</summary>
    private void CaptureDrawerSize()
    {
        if (_stacked)
        {
            if (BodyGrid.RowDefinitions.Count != 3) return;
            var height = BodyGrid.RowDefinitions[0].Height;
            if (height.GridUnitType == GridUnitType.Pixel && height.Value > 0) _drawerHeight = height.Value;
        }
        else if (BodyGrid.ColumnDefinitions.Count == 3)
        {
            var width = BodyGrid.ColumnDefinitions[0].Width;
            if (width.GridUnitType == GridUnitType.Pixel && width.Value > 0) _drawerWidth = width.Value;
        }
    }

    /// <summary>
    /// 抽屉收展＝主栏那一格归 0 或回填记录尺寸。收起时必须同时清掉最小尺寸——
    /// Avalonia 里 MinWidth/MinHeight 压过显式尺寸，不清就收不干净。
    /// </summary>
    private void ApplyDrawerSize()
    {
        if (_stacked)
        {
            if (BodyGrid.RowDefinitions.Count == 3)
            {
                var def = BodyGrid.RowDefinitions[0];
                def.MinHeight = _drawerOpen ? DrawerHeightMin : 0;
                def.Height = new GridLength(_drawerOpen
                    ? Math.Clamp(_drawerHeight, DrawerHeightMin, DrawerHeightMax)
                    : 0);
            }
        }
        else if (BodyGrid.ColumnDefinitions.Count == 3)
        {
            var def = BodyGrid.ColumnDefinitions[0];
            def.MinWidth = _drawerOpen ? DrawerWidthMin : 0;
            def.Width = new GridLength(_drawerOpen
                ? Math.Clamp(_drawerWidth, DrawerWidthMin, DrawerWidthMax)
                : 0);
        }

        // 收起态分割带贴窗口左缘会被系统缩放边框吃掉点击（点不回来），并排态把它整体右移让开这条边框；
        // 负右边距保证命中区仍是 SplitterWidth 宽，只是平移。堆叠态分割带在正文中部、不贴窗口边，无需让位。
        DividerGrip.Margin = (!_stacked && !_drawerOpen)
            ? new Thickness(CollapsedGripInset, 0, -CollapsedGripInset, 0)
            : new Thickness(0);
    }

    private void SetDrawerOpen(bool open)
    {
        if (_drawerOpen == open) return;
        if (!open) CaptureDrawerSize();
        _drawerOpen = open;

        UpdateDividerGripCursor();
        ApplyDrawerSize();
        UpdateStatusBarHint();
        if (open) TxtSource.Focus();
    }

    /// <summary>状态栏右侧只留一句当前最用得上的提示。</summary>
    private void UpdateStatusBarHint()
    {
        TxtSyncScroll.Text = ChkSentenceLink.IsChecked == true
            ? (_sentencePairs.Count > 0 ? "点选句子定位" : _loading ? "翻译中" : "结果无精确句对")
            : (_drawerOpen ? "同步滚动 开" : "同步滚动 关");
        TxtHint.Text = _drawerOpen ? "Ctrl+Enter 翻译" : @"Ctrl+\ 展开原文";
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if ((CurrentWidth() < StackedWidthThreshold) != _stacked) ApplyLayout();
        if (WindowState == WindowState.Normal) ScheduleSaveGeometry();
    }

    // ── 尺寸记忆：窗口宽高 + 抽屉宽高，防抖落盘、开屏夹取 ──────

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        EnsureWindowSizeWithinScreen();
    }

    /// <summary>改完 500ms 无进一步变化再写盘，避免拖窗 / 拖分割线过程中逐帧狂写 ini。</summary>
    private void ScheduleSaveGeometry()
    {
        if (_saveGeomTimer == null)
        {
            _saveGeomTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveGeomTimer.Tick += (s, e) =>
            {
                _saveGeomTimer?.Stop();
                SaveCurrentGeometry();
            };
        }
        _saveGeomTimer.Stop();
        _saveGeomTimer.Start();
    }

    /// <summary>把当前窗口尺寸与抽屉尺寸写回设置；最大化态不记（记了下次会开成超屏）、无变化不写。</summary>
    private void SaveCurrentGeometry()
    {
        try
        {
            if (WindowState != WindowState.Normal) return;

            double w = Bounds.Width > 0 ? Bounds.Width : Width;
            double h = Bounds.Height > 0 ? Bounds.Height : Height;
            if (w < MinWidth || h < MinHeight) return;

            var s = AppSettings.Current;
            bool changed = false;
            if (Math.Abs(s.TranslationEditWindowWidth - w) > 1) { s.TranslationEditWindowWidth = Math.Round(w); changed = true; }
            if (Math.Abs(s.TranslationEditWindowHeight - h) > 1) { s.TranslationEditWindowHeight = Math.Round(h); changed = true; }
            if (Math.Abs(s.TranslationEditDrawerWidth - _drawerWidth) > 1) { s.TranslationEditDrawerWidth = Math.Round(_drawerWidth); changed = true; }
            if (Math.Abs(s.TranslationEditDrawerHeight - _drawerHeight) > 1) { s.TranslationEditDrawerHeight = Math.Round(_drawerHeight); changed = true; }
            if (changed) s.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TranslationResultWindow.SaveCurrentGeometry 异常：" + ex.Message);
        }
    }

    /// <summary>记住的尺寸可能大于当前屏幕（换分辨率 / 插拔显示器），开屏时夹回主屏工作区，避免窗口开在屏外。</summary>
    private void EnsureWindowSizeWithinScreen()
    {
        try
        {
            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen == null) return;

            double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            double waW = screen.WorkingArea.Width / scale;
            double waH = screen.WorkingArea.Height / scale;
            if (waW <= 0 || waH <= 0) return;

            if (Width > waW) Width = Math.Max(MinWidth, waW);
            if (Height > waH) Height = Math.Max(MinHeight, waH);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TranslationResultWindow.EnsureWindowSizeWithinScreen 异常：" + ex.Message);
        }
    }

    // ── 分割带统一交互：拖拽改宽 / 点击收展 / 双击复位 ────────

    private void OnDividerGripPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;

        // 双击复位只对展开态有意义；收起态点它是为了重新展开，走 Released 的点击分支
        if (e.ClickCount == 2)
        {
            if (!_drawerOpen) return;
            _dividerDidReset = true;
            _drawerWidth = DrawerWidthDefault;
            _drawerHeight = DrawerStackHeight;
            ApplyDrawerSize();
            ScheduleSaveGeometry();
            return;
        }

        _dividerDragging = true;
        var pt = e.GetPosition(this);
        _dividerDragStart = _stacked ? pt.Y : pt.X;
        _dividerDragStartSize = _stacked ? _drawerHeight : _drawerWidth;
    }

    private void OnDividerGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_dividerDragging || !_drawerOpen) return;

        var pt = e.GetPosition(this);
        var current = _stacked ? pt.Y : pt.X;
        var delta = current - _dividerDragStart;

        if (_stacked)
        {
            _drawerHeight = Math.Clamp(_dividerDragStartSize + delta, DrawerHeightMin, DrawerHeightMax);
            if (BodyGrid.RowDefinitions.Count == 3)
                BodyGrid.RowDefinitions[0].Height = new GridLength(_drawerHeight);
        }
        else
        {
            _drawerWidth = Math.Clamp(_dividerDragStartSize + delta, DrawerWidthMin, DrawerWidthMax);
            if (BodyGrid.ColumnDefinitions.Count == 3)
                BodyGrid.ColumnDefinitions[0].Width = new GridLength(_drawerWidth);
        }
    }

    private void OnDividerGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dividerDidReset)
        {
            _dividerDidReset = false;
            _dividerDragging = false;
            return;
        }

        if (_dividerDragging)
        {
            var pt = e.GetPosition(this);
            var end = _stacked ? pt.Y : pt.X;
            if (Math.Abs(end - _dividerDragStart) < 3)
                SetDrawerOpen(!_drawerOpen);
            else
                ScheduleSaveGeometry();
        }

        _dividerDragging = false;
    }

    private void OnDividerGripPointerEntered(object? sender, PointerEventArgs e)
    {
        _gripHover = true;
        UpdateDividerGripVisual();
    }

    private void OnDividerGripExited(object? sender, PointerEventArgs e)
    {
        _dividerDragging = false;
        _gripHover = false;
        UpdateDividerGripVisual();
    }

    private void OnSourcePanePointerEntered(object? sender, PointerEventArgs e)
    {
        _activeScroll = SrcScroll;
    }

    private void OnTargetPanePointerEntered(object? sender, PointerEventArgs e)
    {
        _activeScroll = TgtScroll;
    }

    private void OnSourceScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 抽屉收起时原文栏宽度为 0，残留的滚动事件会把译文栏拽走，直接不生效
        if (!_drawerOpen) return;
        if (_syncingScroll) return;
        if (_activeScroll != null && _activeScroll != SrcScroll) return;
        SyncScroll(SrcScroll, TgtScroll);
    }

    private void OnTargetScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_drawerOpen) return;
        if (_syncingScroll) return;
        if (_activeScroll != null && _activeScroll != TgtScroll) return;
        SyncScroll(TgtScroll, SrcScroll);
    }

    /// <summary>按可滚动范围的比例同步另一栏，避免按像素同步导致的参差。</summary>
    private void SyncScroll(ScrollViewer from, ScrollViewer to)
    {
        if (ChkSentenceLink.IsChecked == true) return;
        double fromMax = from.Extent.Height - from.Viewport.Height;
        double toMax = to.Extent.Height - to.Viewport.Height;
        if (fromMax <= 0.5 || toMax <= 0.5) return;

        double ratio = Math.Clamp(from.Offset.Y / fromMax, 0, 1);
        _syncingScroll = true;
        try
        {
            to.Offset = new Vector(to.Offset.X, ratio * toMax);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    // ── 事件处理 ──────────────────────────────────────────────

    private void OnTranslateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = DoTranslateAsync();
    }

    private void OnReTranslateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = DoTranslateAsync();
    }

    private void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string selected = TxtTarget.SelectedText;
        string text = string.IsNullOrEmpty(selected) ? TxtTarget.Text ?? string.Empty : selected;
        if (string.IsNullOrEmpty(text)) return;

        ClipboardService.SetText(text);
        SetStatus("译文已复制到剪贴板");
    }

    private void OnSourceTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading && TxtSource.Text != _requestSource) { _cts?.Cancel(); SetLoading(false); }
        if (TxtSource.Text != _pairedSource) InvalidateSentencePairs();
        UpdateCounts();
        UpdateEmptyState();
    }

    private void InvalidateSentencePairs()
    {
        bool hadPairs = _sentencePairs.Count > 0;
        _sentencePairs = Array.Empty<TranslationTextPair>();
        _selectionVersion++;
        if (hadPairs)
        {
            _syncingSentenceSelection = true;
            try { TxtTarget.SelectionStart = 0; TxtTarget.SelectionEnd = 0; }
            finally { _syncingSentenceSelection = false; }
        }
        if (ChkSentenceLink != null) UpdateStatusBarHint();
    }

    private void OnSentenceLinkChanged(object? sender, RoutedEventArgs e)
    {
        if (TxtSource == null || TxtTarget == null) return;
        // 仅切换已有结果的定位高亮，不重翻、不取消正在进行的请求，也不丢弃缓存映射。
        UpdateStatusBarHint();
        if (ChkSentenceLink.IsChecked == true)
        {
            if (_sentencePairs.Count > 0) SelectSentence(true, TxtSource.SelectionStart, TxtSource.SelectionEnd);
            else if (!_loading && !string.IsNullOrEmpty(TxtTarget.Text)) SetStatus("当前结果没有精确句对，原译文保持不变", true);
        }
        else
        {
            _selectionVersion++; _syncingSentenceSelection = true;
            try { TxtTarget.SelectionStart = 0; TxtTarget.SelectionEnd = 0; }
            finally { _syncingSentenceSelection = false; }
        }
    }

    private void OnSentenceSelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncingSentenceSelection) return;
        if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.SelectionStartProperty
            || e.Property == TextBox.SelectionEndProperty || e.Property == SelectableTextBlock.SelectionStartProperty
            || e.Property == SelectableTextBlock.SelectionEndProperty)
        {
            bool source = ReferenceEquals(sender, TxtSource);
            SelectSentence(source, source ? TxtSource.SelectionStart : TxtTarget.SelectionStart,
                source ? TxtSource.SelectionEnd : TxtTarget.SelectionEnd);
        }
    }

    private void OnSourceSentenceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            SelectSentence(true, TxtSource.SelectionStart, TxtSource.SelectionEnd);
    }

    private void OnTargetSentenceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        // 控件已计算包含滚动偏移的字符位置；再次按指针坐标命中会重复应用偏移，长文中会选错句。
        SelectSentence(false, TxtTarget.SelectionStart, TxtTarget.SelectionEnd);
    }

    /// <summary>只消费独立翻译建立的范围；跨句选择覆盖对应句段，任何编辑都会使旧范围失效。</summary>
    private void SelectSentence(bool source, int start, int end)
    {
        if (_syncingSentenceSelection || _loading || ChkSentenceLink.IsChecked != true || _sentencePairs.Count == 0
            || _pairedSource != TxtSource.Text || _pairedTarget != TxtTarget.Text) return;
        int lo = Math.Min(start, end), hi = Math.Max(start, end);
        var matches = _sentencePairs.Where(pair =>
        {
            int a = source ? pair.SourceStart : pair.TargetStart, length = source ? pair.SourceLength : pair.TargetLength;
            return hi == lo ? lo >= a && lo < a + length : lo < a + length && hi > a;
        }).ToArray();
        if (matches.Length == 0) return;
        int first = source ? matches[0].TargetStart : matches[0].SourceStart;
        var last = matches[^1];
        int limit = source ? last.TargetStart + last.TargetLength : last.SourceStart + last.SourceLength;
        _syncingSentenceSelection = true;
        try
        {
            if (source) { TxtTarget.SelectionStart = first; TxtTarget.SelectionEnd = limit; }
            else { SetDrawerOpen(true); TxtSource.SelectionStart = first; TxtSource.SelectionEnd = limit; }
        }
        finally { _syncingSentenceSelection = false; }
        int version = ++_selectionVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _selectionVersion || _loading) return;
            var scroll = source ? TgtScroll : SrcScroll;
            var presenter = source ? null : TxtSource.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            var textVisual = source ? (Visual)TxtTarget : presenter;
            if (textVisual == null) return;
            var rect = source ? TxtTarget.TextLayout.HitTestTextPosition(first) : presenter!.TextLayout.HitTestTextPosition(first);
            var origin = textVisual.TranslatePoint(default, (Visual)scroll.Content!);
            if (origin == null) return;
            double y = origin.Value.Y + rect.Y;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(y - scroll.Viewport.Height / 3, 0,
                Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }, DispatcherPriority.Loaded);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { BeginMoveDrag(e); } catch { }
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 快捷键：Ctrl+Enter 翻译 / Ctrl+S 交换语言 / Ctrl+Shift+C 复制译文 / Ctrl+\ 收起展开原文
    /// / Ctrl+R 重新翻译 / Esc 就近取消。
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        bool ctrl = (modifiers & KeyModifiers.Control) != 0;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;

        if (ctrl && e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = DoTranslateAsync();
            return;
        }

        if (ctrl && !shift && e.Key == Key.S)
        {
            e.Handled = true;
            OnSwapClick(null, new RoutedEventArgs());
            return;
        }

        if (ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            OnCopyClick(null, new RoutedEventArgs());
            return;
        }

        if (ctrl && !shift && e.Key is Key.OemBackslash or Key.OemPipe)
        {
            e.Handled = true;
            SetDrawerOpen(!_drawerOpen);
            return;
        }

        if (ctrl && !shift && e.Key == Key.R)
        {
            e.Handled = true;
            _ = DoTranslateAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            // 语言下拉还开着，Esc 先还给下拉自己收起
            if (CmbSourceLang.IsDropDownOpen || CmbTargetLang.IsDropDownOpen) return;

            e.Handled = true;
            if (_drawerOpen) SetDrawerOpen(false);
            else if (_loading) _cts?.Cancel();
            else Close();
        }
    }
}
