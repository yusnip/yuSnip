using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;

namespace ScreenCaptureTool.Windows
{
    public partial class AiVisionResultWindow : Window
    {
        private System.Drawing.Bitmap? _sourceBitmap;
        private Bitmap? _avaloniaBitmap;
        private string _imageBase64 = string.Empty;
        /// <summary>纯文本模式下被询问的那段文字；null＝图片模式。仅作"本轮有没有内容"的判据与分岔开关。</summary>
        private string? _textContentTarget;
        /// <summary>AI 回复卡片左上角的模式标签：图片模式为「AI 识图」，文本模式由调用方给（「AI 解释」/「AI 搜索」）。</summary>
        private string _bubbleLabel = "AI 识图";
        private CancellationTokenSource? _analysisCts;
        private int _imagePreparationVersion;
        private bool _isPreparingImage;
        private bool _closed;
        private bool _scrollToEndQueued;
        private EventHandler? _streamTickHandler;
        private readonly object _streamBufferLock = new object();
        private readonly StringBuilder _latestAiBuilder = new StringBuilder();
        private readonly StringBuilder _thinkingBuilder = new StringBuilder();
        private volatile bool _hasPendingStreamUpdate;
        private DispatcherTimer? _streamThrottleTimer;
        private string _lastPrompt = string.Empty;
        private SelectableMarkdownTextBlock? _activeMarkdownViewer;
        private StackPanel? _activeGeneratingIndicator;
        private ContinuousBorder? _activeThinkingCard;
        private TextBlock? _activeThinkingHeaderLabel;
        private TextBlock? _activeThinkingArrowLabel;
        private Border? _activeThinkingBody;
        private SelectableMarkdownTextBlock? _activeThinkingViewer;
        private bool _isThinkingExpanded = true;
        private bool _userToggledThinking;
        private bool _isThinkingCompleted;
        private double _thinkingElapsedSec;
        private readonly List<(string Role, string Content)> _chatHistory = new List<(string, string)>();
        private bool _searchEnabled;
        /// <summary>本窗「深度思考」开关（issue #54）。true＝保持模型自身思考行为（历来默认）；
        /// false＝请求上游跳过思考直接作答。与联网搜索同一范式：开窗时从全局设置初始化，
        /// 用户在窗口里切换则写回全局。</summary>
        private bool _thinkingEnabled = true;
        // @ 预设提示词提及下拉（复用控件，与前置提问弹窗共用同一交互）
        private Controls.AiPresetMentionPopup? _mentionPopup;
        private DispatcherTimer? _thinkingTimer;
        private TextBlock? _activeThinkingText;
        private readonly Stopwatch _thinkingStopwatch = new Stopwatch();
        private StackPanel? _activeMessageStack;
        private CancellationTokenSource? _toastCts;
        private DispatcherTimer? _saveSizeDebounceTimer;

        private static readonly HttpClient ThumbHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        /// <summary>
        /// 主题感知取色：显式携带当前主题变体解析主题资源；解析失败时按当前深浅色取对应兜底色。
        /// </summary>
        private IBrush ThemeBrush(string key, string lightHex, string darkHex)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
                return brush;
            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            return new SolidColorBrush(Color.Parse(isDark ? darkHex : lightHex));
        }

        /// <summary>
        /// 代码构建的对话气泡取到的是快照画刷，主题切换后不会自更新；
        /// 这里登记「控件 + 属性 + Token」，切换时统一重刷（XAML 侧走 DynamicResource 无需登记）。
        /// </summary>
        private readonly List<(AvaloniaObject Target, StyledProperty<IBrush?> Property, string Key, string LightHex, string DarkHex)> _themedBrushes
            = new List<(AvaloniaObject, StyledProperty<IBrush?>, string, string, string)>();

        private void SetThemeBrush(AvaloniaObject target, StyledProperty<IBrush?> property, string key, string lightHex, string darkHex)
        {
            target.SetValue(property, ThemeBrush(key, lightHex, darkHex));
            _themedBrushes.Add((target, property, key, lightHex, darkHex));
        }

        private void RefreshThemeBrushes()
        {
            for (int i = 0; i < _themedBrushes.Count; i++)
            {
                var entry = _themedBrushes[i];
                entry.Target.SetValue(entry.Property, ThemeBrush(entry.Key, entry.LightHex, entry.DarkHex));
            }
        }

        /// <summary>
        /// 主题切换响应：窗口不固定 RequestedThemeVariant（继承 Application 主题作用域），
        /// 故只需重刷代码构建部分的取色快照。
        /// </summary>
        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ActualThemeVariantProperty) return;
            AppLogger.Info($"[AiVisionWindow] 主题切换为 {ActualThemeVariant?.Key}，重刷 {_themedBrushes.Count} 处代码取色");
            RefreshThemeBrushes();
        }

        /// <summary>
        /// 悬浮轻量 Toast 反馈提示（带淡入淡出动效与图标）。
        /// </summary>
        public void ShowToast(string message, bool isSuccess = true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ToastPanel == null || ToastText == null) return;

                _toastCts?.Cancel();
                _toastCts = new CancellationTokenSource();
                var token = _toastCts.Token;

                ToastText.Text = message;
                if (ToastIcon != null)
                {
                    ToastIcon.Data = isSuccess
                        ? PathGeometry.Parse("M 12 2 C 6.48 2 2 6.48 2 12 C 2 17.52 6.48 22 12 22 C 17.52 22 22 17.52 22 12 C 22 6.48 17.52 2 12 2 Z M 10 17 L 5 12 L 6.41 10.59 L 10 14.17 L 17.59 6.58 L 19 8 L 10 17 Z")
                        : PathGeometry.Parse("M 12 2 C 6.48 2 2 6.48 2 12 C 2 17.52 6.48 22 12 22 C 17.52 22 22 17.52 22 12 C 22 6.48 17.52 2 12 2 Z M 13 17 H 11 V 15 H 13 V 17 Z M 13 13 H 11 V 7 H 13 V 13 Z");
                    ToastIcon.Fill = isSuccess
                        ? new SolidColorBrush(Color.Parse("#4ADE80"))
                        : new SolidColorBrush(Color.Parse("#F87171"));
                }

                ToastPanel.Margin = new Thickness(0, 16, 0, 0);
                ToastPanel.Opacity = 1.0;

                Task.Delay(2200, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ToastPanel != null)
                            {
                                ToastPanel.Opacity = 0.0;
                                ToastPanel.Margin = new Thickness(0, 8, 0, 0);
                            }
                        });
                    }
                });
            });
        }

        /// <summary>
        /// 构造复制图片按钮的内容面板。成功态不做换色/换图标，
        /// 仅文字切换为「已复制」并加粗，保持品牌紫胶囊的原貌。
        /// </summary>
        private StackPanel CreateCopyImgButtonContent(string text, bool isSuccess)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new Avalonia.Controls.Shapes.Path
            {
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                Data = PathGeometry.Parse("M 3 5 A 2 2 0 0 1 5 3 H 19 A 2 2 0 0 1 21 5 V 19 A 2 2 0 0 1 19 21 H 5 A 2 2 0 0 1 3 19 Z M 5 17 L 9 12 L 12 15 L 16 10 L 19 14 V 5 H 5 Z")
            };
            SetThemeBrush(icon, Avalonia.Controls.Shapes.Shape.FillProperty, "SctCardTitleBrush", "#797EFE", "#A08BF8");
            panel.Children.Add(icon);

            var label = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = isSuccess ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(label, TextBlock.ForegroundProperty, "SctCardTitleBrush", "#797EFE", "#A08BF8");
            panel.Children.Add(label);

            return panel;
        }

        public AiVisionResultWindow()
        {
            InitializeComponent();
            var settings = AppSettings.Current;
            if (settings.AiVisionWindowWidth >= MinWidth) Width = settings.AiVisionWindowWidth;
            else Width = MinWidth;
            if (settings.AiVisionWindowHeight >= MinHeight) Height = settings.AiVisionWindowHeight;
            else Height = MinHeight;

            SizeChanged += OnWindowSizeChanged;
            KeyDown += OnWindowKeyDown;
            UpdateEmptyState();
            PropertyChanged += OnWindowPropertyChanged;
            _mentionPopup = new Controls.AiPresetMentionPopup(this, TxtPromptInput);
            _mentionPopup.RefreshPresets();
            // 追问框按键走 Tunnel（XAML 里的 KeyDown= 已移到这一行）：粘贴必须赶在 TextBox 内部
            // 那条路径之前接管并置 Handled，否则本窗这一次会与其内部那一次叠成双份粘贴。
            // 与 AiPromptInputWindow 同一形态，两个输入框共用同一份语义。
            TxtPromptInput.AddHandler(KeyDownEvent, OnPromptInputKeyDown, RoutingStrategies.Tunnel);
            _searchEnabled = settings.AiSearchEnabled;
            if (BtnToggleSearch != null) BtnToggleSearch.IsChecked = _searchEnabled;
            // ⚠ 同下面 OnToggleThinkingChanged 的注释：必须先改字段再写 IsChecked，
            // 否则这一下程序赋值会被事件处理当成用户操作、把设置又写一遍
            _thinkingEnabled = settings.AiThinkingEnabled;
            if (BtnToggleThinking != null) BtnToggleThinking.IsChecked = _thinkingEnabled;

            if (CmbModelSelect != null)
            {
                CmbModelSelect.ContainerPrepared += (s, e) =>
                {
                    if (e.Container is ComboBoxItem cbi && cbi.DataContext is AiModelDropDownItem item)
                    {
                        if (item.IsHeader)
                        {
                            cbi.IsEnabled = false;
                            cbi.Focusable = false;
                            cbi.IsHitTestVisible = false;
                            cbi.Padding = new Thickness(6, 4, 6, 2);
                        }
                        else if (item.IsSeparator)
                        {
                            cbi.IsEnabled = false;
                            cbi.Focusable = false;
                            cbi.IsHitTestVisible = false;
                            cbi.Padding = new Thickness(0);
                            cbi.MinHeight = 1;
                            cbi.Height = 7;
                        }
                        else
                        {
                            cbi.IsEnabled = true;
                            cbi.Focusable = true;
                            cbi.IsHitTestVisible = true;
                            cbi.Padding = new Thickness(8, 4);
                        }
                    }
                };

                // 展开后把选中模型滚入可视区（框架首次展开时不会做，默认模型会落在 160px 可视区之外）
                CmbModelSelect.DropDownOpened += (_, _) => ScheduleScrollToSelectedModel();
            }
        }

        /// <summary>下拉展开后补滚动的重试上限：16ms 一帧，12 帧≈190ms，足够任何下拉宿主走完首帧布局。</summary>
        private const int DropDownScrollMaxTicks = 12;
        /// <summary>滚动"确实生效"的确认次数：滚一次不够——晚到的布局可能把偏移又重置回顶部。</summary>
        private const int DropDownScrollMaxApplied = 3;

        private DispatcherTimer? _dropDownScrollTimer;
        private int _dropDownScrollTicks;
        private int _dropDownScrollApplied;

        /// <summary>
        /// 展开模型下拉后，把当前选中的模型滚进可视区。
        /// 框架（Avalonia 11.2 <c>ComboBox.TryFocusSelectedItem</c>）只在「选中项容器尚未生成」时才调一次
        /// ScrollIntoView，而首次展开时虚拟化面板的 <c>_realizedElements</c> 还是 null，该调用会被
        /// VirtualizingStackPanel 直接丢弃 —— 列表于是停在最顶部；默认配置的模型常排在服务商分组标题之后，
        /// 正好落在可视区之外，观感就是"看不到、被缩进去了"。
        /// 补滚动<b>不能只挑一个时机</b>：下拉宿主是独立 TopLevel，它的首帧布局落在哪一次 Dispatcher 循环
        /// 并不确定——"Loaded 补一次 + Background 再补一次"两次都没赶上时整段失效。这里改为按帧重试：
        /// 每 16ms 调一次 ScrollIntoView，直到确认滚动真的落下去（或试满上限）才收工，与布局时机解耦。
        /// </summary>
        private void ScheduleScrollToSelectedModel()
        {
            if (CmbModelSelect == null) return;

            _dropDownScrollTicks = 0;
            _dropDownScrollApplied = 0;
            _dropDownScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Background, (_, _) => ScrollToSelectedModel());
            _dropDownScrollTimer.Stop();
            _dropDownScrollTimer.Start();

            ScrollToSelectedModel(); // 面板已就绪时当帧即到位，用户看不到跳动
        }

        private void ScrollToSelectedModel()
        {
            var combo = CmbModelSelect;
            if (combo == null || !combo.IsDropDownOpen)
            {
                StopDropDownScrollTimer();
                return;
            }

            int index = combo.SelectedIndex;
            if (index < 0)
            {
                StopDropDownScrollTimer();
                return;
            }

            // 面板已实现出容器 ⇒ 本帧下拉已完成布局，这一次 ScrollIntoView 不会再被框架丢掉
            bool panelReady = combo.GetRealizedContainers().Any();
            try
            {
                combo.ScrollIntoView(index);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiVisionWindow] 下拉选中项滚动定位异常: " + ex.GetType().Name);
                StopDropDownScrollTimer();
                return;
            }
            if (panelReady) _dropDownScrollApplied++;

            _dropDownScrollTicks++;
            if (_dropDownScrollApplied >= DropDownScrollMaxApplied || _dropDownScrollTicks >= DropDownScrollMaxTicks)
            {
                StopDropDownScrollTimer();
            }
        }

        private void StopDropDownScrollTimer()
        {
            _dropDownScrollTicks = 0;
            _dropDownScrollApplied = 0;
            _dropDownScrollTimer?.Stop();
        }

        public void SetTargetImage(System.Drawing.Bitmap bitmap, string? initialPrompt = null)
        {
            _analysisCts?.Cancel();
            _streamThrottleTimer?.Stop();
            StopThinkingTimer();
            int version = ++_imagePreparationVersion;
            _isPreparingImage = true;
            _imageBase64 = string.Empty;
            // 先清空旧对话（避免缩略图引用即将被释放的旧位图），再切换图片
            _chatHistory.Clear();
            ChatMessagesContainer.Children.Clear();
            _themedBrushes.Clear();
            _textContentTarget = null;
            _bubbleLabel = "AI 识图";
            Title = "AI 识图 - yuSnip";

            _sourceBitmap?.Dispose();
            _sourceBitmap = null;

            if (_avaloniaBitmap != null)
            {
                _avaloniaBitmap.Dispose();
                _avaloniaBitmap = null;
            }

            TxtStatus.Text = "正在准备图片...";
            // 调用方返回后即可释放输入；仅快照复制留在入口，编码／Base64／预览解码全部移到后台。
            System.Drawing.Bitmap snapshot;
            try { snapshot = (System.Drawing.Bitmap)bitmap.Clone(); }
            catch { _isPreparingImage = false; TxtStatus.Text = "图片准备失败，请重新截图后重试"; throw; }
            _ = PrepareTargetImageAsync(snapshot, initialPrompt, version);
        }

        private async Task PrepareTargetImageAsync(System.Drawing.Bitmap snapshot, string? initialPrompt, int version)
        {
            var preparation = Stopwatch.StartNew();
            Bitmap? preview = null;
            bool transferred = false;
            try
            {
                var prepared = await Task.Run(() =>
                {
                    using var ms = new MemoryStream();
                    snapshot.Save(ms, ImageFormat.Png);
                    string base64 = Convert.ToBase64String(ms.GetBuffer(), 0, checked((int)ms.Length));
                    ms.Position = 0;
                    return (Base64: base64, Preview: new Bitmap(ms));
                }).ConfigureAwait(false);
                preview = prepared.Preview;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_closed || version != _imagePreparationVersion) return;
                    _sourceBitmap = snapshot;
                    _avaloniaBitmap = preview;
                    _imageBase64 = prepared.Base64;
                    transferred = true;
                    _isPreparingImage = false;
                    var settings = AppSettings.Current;
                    string currentModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o" : settings.AiModel.Trim();
                    TxtModelBadge.Text = currentModel;
                    InitModelList(currentModel);

                    string prompt = string.IsNullOrWhiteSpace(initialPrompt)
                        ? settings.GetDefaultAiPrompt()
                        : initialPrompt;

                    AppLogger.Info($"[AiVisionWindow] SetTargetImage: size={_sourceBitmap.Width}x{_sourceBitmap.Height}, base64Chars={_imageBase64.Length}, preparationMs={preparation.ElapsedMilliseconds}");
                    StartAnalysis(prompt, withImageThumb: true);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("AI 图片准备失败", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_closed || version != _imagePreparationVersion) return;
                    _isPreparingImage = false;
                    TxtStatus.Text = "图片准备失败，请重新截图后重试";
                });
            }
            finally { if (!transferred) { preview?.Dispose(); snapshot.Dispose(); } }
        }

        /// <summary>
        /// 纯文本模式入口：不带图片，把给定文本当作本轮对话的目标内容。
        ///
        /// <para>⚠ 2026-09-30 起贴图划词的「AI 解释／AI 联网搜索」两项功能已整体下线，
        /// 本入口**已无调用方**、保留作公共 API（多轮追问、流式气泡、思考折叠卡、联网四阶段、
        /// 来源区、复制成长图仍只有这一份全量实现）。</para>
        ///
        /// <para><paramref name="enableSearch"/> 是<b>会话级</b>覆盖，不写回 <c>AiSearchEnabled</c>：
        /// 解释与搜索对联网的诉求是功能定义的一部分（解释靠已有知识、搜索靠网页），
        /// 不该因为点了一次解释就把用户的全局开关改掉。用户若在窗口里手动拨动开关，那一下照常落盘。</para>
        /// </summary>
        public void SetTargetText(string text, string prompt, bool enableSearch, string bubbleLabel)
        {
            ++_imagePreparationVersion;
            _isPreparingImage = false;
            _analysisCts?.Cancel();
            _chatHistory.Clear();
            ChatMessagesContainer.Children.Clear();
            _themedBrushes.Clear();

            _sourceBitmap?.Dispose();
            _sourceBitmap = null;
            if (_avaloniaBitmap != null)
            {
                _avaloniaBitmap.Dispose();
                _avaloniaBitmap = null;
            }
            _imageBase64 = string.Empty;
            _textContentTarget = text ?? string.Empty;
            _bubbleLabel = string.IsNullOrWhiteSpace(bubbleLabel) ? "AI" : bubbleLabel;
            Title = _bubbleLabel + " - yuSnip";

            // 联网开关跟随本次动作：必须在写 IsChecked 之前先改 _searchEnabled，
            // 否则这一下程序赋值会被 OnToggleSearchChanged 当成用户操作而误写全局设置。
            _searchEnabled = enableSearch;
            if (BtnToggleSearch != null) BtnToggleSearch.IsChecked = enableSearch;

            var settings = AppSettings.Current;
            string currentModel = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o" : settings.AiModel.Trim();
            TxtModelBadge.Text = currentModel;
            InitModelList(currentModel);

            AppLogger.Info($"[AiVisionWindow] SetTargetText: chars={_textContentTarget.Length}, search={enableSearch}, label={_bubbleLabel}");
            StartAnalysis(prompt, withImageThumb: false);
        }

        private void InitModelList(string? currentModel)
        {
            if (CmbModelSelect == null) return;

            var items = new List<AiModelDropDownItem>();
            var settings = AppSettings.Current;
            settings.EnsureDefaultAiConfigs();

            var enabledConfigs = (settings.AiConfigs != null)
                ? settings.AiConfigs.Where(c => c.IsEnabled).ToList()
                : new List<AiApiConfig>();

            bool addedAnyGroup = false;

            foreach (var cfg in enabledConfigs)
            {
                var cfgModels = new List<string>();
                if (!string.IsNullOrWhiteSpace(cfg.DefaultModel))
                {
                    cfgModels.Add(cfg.DefaultModel.Trim());
                }
                if (cfg.CustomModels != null)
                {
                    foreach (var m in cfg.CustomModels)
                    {
                        if (!string.IsNullOrWhiteSpace(m) && !cfgModels.Any(existing => string.Equals(existing, m.Trim(), StringComparison.OrdinalIgnoreCase)))
                        {
                            cfgModels.Add(m.Trim());
                        }
                    }
                }

                if (cfgModels.Count > 0)
                {
                    if (addedAnyGroup)
                    {
                        items.Add(AiModelDropDownItem.CreateSeparator());
                    }
                    items.Add(AiModelDropDownItem.CreateHeader(string.IsNullOrWhiteSpace(cfg.Name) ? "API 服务商" : cfg.Name, cfg.Id));
                    foreach (var m in cfgModels)
                    {
                        items.Add(AiModelDropDownItem.CreateModel(m, cfg.Id, cfg.Name));
                    }
                    addedAnyGroup = true;
                }
            }

            // 如果没有任何启用的配置带有模型，加载预设模型兜底
            if (items.Count == 0 || !items.Any(i => i.IsModel))
            {
                items.Clear();
                items.Add(AiModelDropDownItem.CreateHeader("预设模型", "builtin"));
                var presets = new[]
                {
                    "gpt-4o",
                    "gpt-4o-mini",
                    "chatgpt-4o-latest",
                    "claude-3-5-sonnet-20241022",
                    "claude-3-5-haiku-20241022",
                    "gemini-2.0-flash",
                    "gemini-1.5-pro",
                    "deepseek-chat",
                    "qwen-vl-max"
                };
                foreach (var p in presets)
                {
                    items.Add(AiModelDropDownItem.CreateModel(p, "builtin", "预设模型"));
                }
            }

            // 如果当前指定模型不在列表中，将其插入到当前激活服务商下方或最前
            string target = !string.IsNullOrWhiteSpace(currentModel) ? currentModel.Trim() : (settings.AiModel ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(target) && !items.Any(i => i.IsModel && string.Equals(i.ModelId, target, StringComparison.OrdinalIgnoreCase)))
            {
                var firstHeader = items.FirstOrDefault(i => i.IsHeader);
                int insertIdx = (firstHeader != null) ? items.IndexOf(firstHeader) + 1 : 0;
                items.Insert(insertIdx, AiModelDropDownItem.CreateModel(target, settings.AiActiveConfigId ?? "", "自定义模型"));
            }

            // 宽度交给控件按内容自扩（模型名长则胶囊长），不再手算文本宽 + 装饰常量写死 Width
            CmbModelSelect.ItemsSource = null;
            CmbModelSelect.ItemsSource = items;

            var selected = items.FirstOrDefault(i => i.IsModel && string.Equals(i.ModelId, target, StringComparison.OrdinalIgnoreCase))
                        ?? items.FirstOrDefault(i => i.IsModel);

            if (selected != null)
            {
                CmbModelSelect.SelectedItem = selected;
                TxtModelBadge.Text = selected.ModelId;
            }
        }

        private void OnModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (CmbModelSelect?.SelectedItem is AiModelDropDownItem item && item.IsModel && !string.IsNullOrWhiteSpace(item.ModelId))
            {
                var settings = AppSettings.Current;
                settings.AiModel = item.ModelId.Trim();
                if (!string.IsNullOrWhiteSpace(item.ConfigId) && item.ConfigId != "builtin")
                {
                    settings.AiActiveConfigId = item.ConfigId;
                }
                else
                {
                    var cfg = settings.GetEffectiveApiConfig(item.ModelId.Trim());
                    if (cfg != null)
                    {
                        settings.AiActiveConfigId = cfg.Id;
                    }
                }
                settings.Save();
                TxtModelBadge.Text = item.ModelId.Trim();
            }
        }

        public void StartAnalysis(string prompt, bool withImageThumb = false)
        {
            if (_closed || _isPreparingImage) return;
            // 两种模式各有一份"本轮内容"：图片模式看 base64，纯文本模式看选中文本。
            // 两者都空才真的无可分析的对象——文本模式若沿用下面那句"无有效图片数据"，
            // 用户会以为是自己没截图，而实际原因是提示词里没带上文字。
            if (string.IsNullOrWhiteSpace(_imageBase64) && string.IsNullOrWhiteSpace(_textContentTarget))
            {
                TxtStatus.Text = "无有效内容";
                AppLogger.Warn("[AiVisionWindow] StartAnalysis aborted: both image base64 and text target are empty.");
                return;
            }
            bool textOnly = string.IsNullOrWhiteSpace(_imageBase64);

            _analysisCts?.Cancel();

            _analysisCts = new CancellationTokenSource();
            var ct = _analysisCts.Token;

            _lastPrompt = prompt;
            lock (_streamBufferLock)
            {
                _latestAiBuilder.Clear();
                _thinkingBuilder.Clear();
                _hasPendingStreamUpdate = false;
            }
            _streamThrottleTimer?.Stop();
            if (_streamTickHandler != null && _streamThrottleTimer != null)
                _streamThrottleTimer.Tick -= _streamTickHandler;
            _isThinkingExpanded = true;
            _userToggledThinking = false;
            _isThinkingCompleted = false;
            _thinkingElapsedSec = 0;

            var settings = AppSettings.Current;
            string model = CmbModelSelect?.SelectedItem is AiModelDropDownItem dropItem && dropItem.IsModel && !string.IsNullOrWhiteSpace(dropItem.ModelId)
                ? dropItem.ModelId.Trim()
                : (CmbModelSelect?.SelectedItem is string m && !string.IsNullOrWhiteSpace(m)
                    ? m.Trim()
                    : (string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o" : settings.AiModel.Trim()));

            TxtModelBadge.Text = model;

            // 1. 添加用户提问气泡（首条带图片缩略图）
            AddUserMessageBubble(prompt, withImageThumb ? _avaloniaBitmap : null);

            // 2. 检查是否有启用的 AI 配置及有效的 API Key
            var activeConfig = settings.GetEffectiveApiConfig(model);
            string effectiveApiKey = activeConfig?.GetEffectiveApiKey() ?? "";

            // 3. 添加 AI 回复卡片气泡（基于自研 SelectableMarkdownTextBlock 原生富文本引擎）
            var (mdViewer, generatingIndicator) = AddAiMessageBubble(model, _searchEnabled ? _bubbleLabel + " · 联网" : _bubbleLabel);
            _activeMarkdownViewer = mdViewer;
            _activeGeneratingIndicator = generatingIndicator;

            if (activeConfig == null || string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                generatingIndicator.IsVisible = false;
                StopThinkingTimer();
                TxtStatus.Text = "请先配置 AI 服务";
                _latestAiBuilder.AppendLine("> 💡 **提示：尚未配置或启用有效的 AI API 服务**\n>\n> 请前往 **「设置」->「AI 识图」** 检查并启用您的 API 服务商，或填写有效的 API Key 后即可开始使用。");
                mdViewer.Markdown = _latestAiBuilder.ToString();
                ShowToast("请先在设置中配置并启用 AI API 服务", isSuccess: false);
                return;
            }

            AiProtocolType protocol = (string.Equals(activeConfig.Protocol, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(activeConfig.Protocol, "Claude", StringComparison.OrdinalIgnoreCase))
                ? AiProtocolType.Anthropic
                : AiProtocolType.OpenAI;

            // 如果已有历史对话，构造组合 Prompt 承接上下文
            string actualPrompt = prompt;
            if (_chatHistory.Count > 0)
            {
                var sbPrompt = new StringBuilder();
                sbPrompt.AppendLine(textOnly ? "以下是针对这段文字的历史对话上下文："
                                             : "以下是针对此图片的历史对话上下文：");
                foreach (var (role, content) in _chatHistory.TakeLast(6))
                {
                    string roleLabel = role == "user" ? "用户" : "AI";
                    string snippet = content.Length > 240 ? content.Substring(0, 240) + "..." : content;
                    sbPrompt.AppendLine($"[{roleLabel}]: {snippet}");
                }
                sbPrompt.AppendLine(textOnly ? "\n请参考上述上下文，回答用户的最新追问："
                                             : "\n请参考上述上下文和图片，回答用户的最新追问：");
                sbPrompt.AppendLine(prompt);
                actualPrompt = sbPrompt.ToString();
            }

            var req = new AiVisionRequest
            {
                Protocol = protocol,
                BaseUrl = activeConfig.BaseUrl,
                ApiKey = effectiveApiKey,
                Model = model,
                Prompt = actualPrompt,
                ImageBase64 = _imageBase64,
                ImageMimeType = "image/png",
                TimeoutSeconds = settings.AiTimeoutSeconds
            };

            var sw = Stopwatch.StartNew();
            StartThinkingTimer();

            AppLogger.Info($"[AiVisionWindow] StartAnalysis: Protocol={protocol}, Model='{model}', PromptChars={prompt.Length}, SearchEnabled={_searchEnabled}");

            // 判据只看开关：原来那条 `&& !string.IsNullOrWhiteSpace(_imageBase64)` 是被本函数开头的
            // 空内容早退完全覆盖的重复条件，留着还会让文本模式（base64 恒空）永远进不了联网分支。
            if (_searchEnabled)
            {
                // 先联网后回答：提炼检索词 → 联网检索 → 一次成文（带内联引用 + 末尾来源）
                TxtStatus.Text = "正在提炼检索关键词并联网检索...";
                RunSearchThenAnswerAsync(req, prompt, model, protocol, activeConfig, sw, generatingIndicator, mdViewer, ct);
            }
            else
            {
                TxtStatus.Text = "正在向 AI 发送请求...";
                RunStreamingAnswerAsync(req, prompt, sw, generatingIndicator, mdViewer, ct, null);
            }
        }

        private void AddUserMessageBubble(string text, Bitmap? thumbnail = null)
        {
            AppLogger.Info($"[AiVisionWindow] AddUserMessageBubble: textLength={text?.Length ?? 0}, hasThumbnail={thumbnail != null}");

            bool hasText = !string.IsNullOrWhiteSpace(text);
            SelectableTextBlock? selectableText = null;

            if (hasText)
            {
                selectableText = new SelectableTextBlock
                {
                    Text = text,
                    FontSize = 13.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                    Focusable = true,
                    IsHitTestVisible = true,
                    SelectionBrush = ThemeBrush("SctOcrSelectionBrush", "#80797EFE", "#80797EFE")
                };
                SetThemeBrush(selectableText, TextBlock.ForegroundProperty, "SctChatBubbleUserText", "#222222", "#FFFFFF");

                selectableText.PointerPressed += (s, e) =>
                {
                    selectableText.Focus();
                };
            }

            var bubbleBorder = new ContinuousBorder
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ScreenRadius("SctRadiusControl"), ScreenRadius("SctRadiusControl"), ScreenRadius("SctRadiusSmall"), ScreenRadius("SctRadiusControl")),
                Margin = new Thickness(40, 4, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            SetThemeBrush(bubbleBorder, Border.BackgroundProperty, "SctChatBubbleUserBg", "#1F797EFE", "#40797EFE");
            SetThemeBrush(bubbleBorder, Border.BorderBrushProperty, "SctChatBubbleUserBorder", "#33797EFE", "#59797EFE");

            if (thumbnail != null)
            {
                bubbleBorder.Padding = new Thickness(10);

                var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

                var thumbImage = new Image
                {
                    Source = thumbnail,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 220,
                    MaxHeight = 180,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                var thumbContainer = new ContinuousBorder
                {
                    CornerRadius = new CornerRadius(ScreenRadius("SctRadiusControl")),
                    ClipToBounds = true,
                    BorderThickness = new Thickness(1),
                    Child = thumbImage
                };
                SetThemeBrush(thumbContainer, Border.BackgroundProperty, "SctImagePreviewBg", "#F5F5F5", "#2B2B2B");
                SetThemeBrush(thumbContainer, Border.BorderBrushProperty, "SctChatBubbleUserBorder", "#33797EFE", "#59797EFE");

                stack.Children.Add(thumbContainer);

                if (selectableText != null)
                {
                    selectableText.Margin = new Thickness(4, 2, 4, 2);
                    stack.Children.Add(selectableText);
                }

                bubbleBorder.Child = stack;
            }
            else
            {
                bubbleBorder.Padding = new Thickness(14, 10);
                bubbleBorder.Child = selectableText;
            }

            // 用户气泡右键菜单
            var userMenu = new ContextMenu();

            if (hasText)
            {
                var copyItem = new MenuItem { Header = "复制提问文本" };
                copyItem.Click += (s, e) =>
                {
                    ClipboardService.SetText(text);
                    ShowToast("已复制提问文本到剪贴板");
                    AppLogger.Info("[AiVisionWindow] ContextMenu: Copied user prompt text.");
                };
                userMenu.Items.Add(copyItem);
            }

            if (thumbnail != null && _sourceBitmap != null)
            {
                var copyImgItem = new MenuItem { Header = "复制图片" };
                copyImgItem.Click += async (s, e) =>
                {
                    try
                    {
                        using var clone = (System.Drawing.Bitmap)_sourceBitmap.Clone();
                        if (!await ClipboardService.SetImageAsync(clone)) return;
                        ShowToast("已复制图片到剪贴板");
                        AppLogger.Info("[AiVisionWindow] ContextMenu: Copied user thumbnail image.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("[AiVisionWindow] ContextMenu: Failed to copy thumbnail image.", ex);
                    }
                };
                userMenu.Items.Add(copyImgItem);
            }

            if (hasText)
            {
                // 设计稿：提问气泡菜单保持无图标，仅在"重新发送"前分段
                if (userMenu.Items.Count > 0) userMenu.Items.Add(new Separator());

                var retryItem = new MenuItem { Header = "重新发送提问" };
                retryItem.Click += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        AppLogger.Info("[AiVisionWindow] ContextMenu: Retrying user prompt.");
                        StartAnalysis(text);
                    }
                };
                userMenu.Items.Add(retryItem);
            }

            bubbleBorder.ContextMenu = userMenu;

            ChatMessagesContainer.Children.Add(bubbleBorder);
            UpdateEmptyState();
            ScrollChatToEnd();
        }

        // ---------- 思考计时器 ----------

        private void EnsureThinkingTimer()
        {
            if (_thinkingTimer != null) return;
            _thinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
            _thinkingTimer.Tick += (s, e) =>
            {
                if (_activeThinkingText != null)
                {
                    _activeThinkingText.Text = $"AI 正在思考并输出中 · {_thinkingStopwatch.Elapsed.TotalSeconds:F1}s";
                }
            };
        }

        private void StartThinkingTimer()
        {
            EnsureThinkingTimer();
            _thinkingStopwatch.Restart();
            _thinkingTimer?.Start();
        }

        private void StopThinkingTimer()
        {
            _thinkingTimer?.Stop();
            if (_activeThinkingText != null)
            {
                _activeThinkingText.Text = "AI 正在思考并输出中...";
            }
        }

        private (SelectableMarkdownTextBlock mdViewer, StackPanel generatingIndicator) AddAiMessageBubble(string model, string badgeText = "AI 识图")
        {
            // 消息行：品牌头像 + 右侧内容列
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                MaxWidth = 600
            };

            var avatar = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    Fill = Brushes.White,
                    Data = PathGeometry.Parse("M 12 2 C 12 7.52 7.52 12 2 12 C 7.52 12 12 16.48 12 22 C 12 16.48 16.48 12 22 12 C 16.48 12 12 7.52 12 2 Z"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            SetThemeBrush(avatar, Border.BackgroundProperty, "SctAccentBrush", "#797EFE", "#797EFE");
            Grid.SetColumn(avatar, 0);
            row.Children.Add(avatar);

            var right = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(right, 1);
            row.Children.Add(right);

            // 署名行：名称 + 模型 + 时间
            var nameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            var nameLabel = new TextBlock
            {
                Text = "yuSnip AI",
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(nameLabel, TextBlock.ForegroundProperty, "SctTextPrimaryBrush", "#222222", "#FFFFFF");
            nameRow.Children.Add(nameLabel);

            var modelLabel = new TextBlock
            {
                Text = model,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(modelLabel, TextBlock.ForegroundProperty, "SctTextMutedBrush", "#6E6E73", "#9F9FA1");
            nameRow.Children.Add(modelLabel);

            var timeLabel = new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(timeLabel, TextBlock.ForegroundProperty, "SctTextMutedBrush", "#6E6E73", "#9F9FA1");
            nameRow.Children.Add(timeLabel);
            right.Children.Add(nameRow);

            // 思考折叠卡片（仅当模型输出思考过程时显示）
            var thinkingCard = new ContinuousBorder
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ScreenRadius("SctRadiusControl")),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                IsVisible = false
            };
            SetThemeBrush(thinkingCard, Border.BackgroundProperty, "SctInputBg", "#F5F5F5", "#2B2B2B");
            SetThemeBrush(thinkingCard, Border.BorderBrushProperty, "SctSoftBorderBrush", "#DCDCDC", "#444444");

            var thinkingInnerStack = new StackPanel { Spacing = 6 };

            // 头部折叠操作行
            var thinkingHeader = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            var thinkingIcon = new Avalonia.Controls.Shapes.Path
            {
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Data = PathGeometry.Parse("M 12 2 C 8.13 2 5 5.13 5 9 C 5 11.38 6.19 13.47 8 14.74 V 17 C 8 17.55 8.45 18 9 18 H 15 C 15.55 18 16 17.55 16 17 V 14.74 C 17.81 13.47 19 11.38 19 9 C 19 5.13 15.87 2 12 2 Z M 9 21 C 9 21.55 9.45 22 10 22 H 14 C 14.55 22 15 21.55 15 21 V 20 H 9 V 21 Z")
            };
            SetThemeBrush(thinkingIcon, Avalonia.Controls.Shapes.Shape.FillProperty, "SctCardTitleBrush", "#797EFE", "#A08BF8");
            Grid.SetColumn(thinkingIcon, 0);
            thinkingHeader.Children.Add(thinkingIcon);

            var thinkingHeaderLabel = new TextBlock
            {
                Text = "深度思考中...",
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(thinkingHeaderLabel, TextBlock.ForegroundProperty, "SctTextSecondaryBrush", "#222222", "#FFFFFF");
            Grid.SetColumn(thinkingHeaderLabel, 1);
            thinkingHeader.Children.Add(thinkingHeaderLabel);

            var thinkingArrowLabel = new TextBlock
            {
                Text = "⌃ 收起",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(thinkingArrowLabel, TextBlock.ForegroundProperty, "SctTextMutedBrush", "#6E6E73", "#9F9FA1");
            Grid.SetColumn(thinkingArrowLabel, 2);
            thinkingHeader.Children.Add(thinkingArrowLabel);

            thinkingInnerStack.Children.Add(thinkingHeader);

            // 思考过程内容区（左竖线引用块）
            var thinkingBody = new Border
            {
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(10, 4, 2, 4),
                Margin = new Thickness(2, 4, 0, 2),
                IsVisible = true
            };
            SetThemeBrush(thinkingBody, Border.BorderBrushProperty, "SctChatBubbleUserBorder", "#33797EFE", "#59797EFE");

            var thinkingMdViewer = new SelectableMarkdownTextBlock
            {
                UseWorkspaceAppearance = true,
                Markdown = string.Empty,
                BaseFontSize = 12.5,
                CompactLayout = true,
                Spacing = 4,
                Opacity = 0.88,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Focusable = true,
                IsHitTestVisible = true
            };
            thinkingMdViewer.LinkClicked += url => OpenUrl(url);
            thinkingBody.Child = thinkingMdViewer;
            thinkingInnerStack.Children.Add(thinkingBody);
            thinkingCard.Child = thinkingInnerStack;

            // 展开/收起点击联动
            var localCard = thinkingCard;
            var localBody = thinkingBody;
            var localArrow = thinkingArrowLabel;
            thinkingHeader.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(thinkingHeader).Properties.IsLeftButtonPressed)
                {
                    bool nextExpanded = !localBody.IsVisible;
                    localBody.IsVisible = nextExpanded;
                    localArrow.Text = nextExpanded ? "⌃ 收起" : "⌄ 展开";
                    if (ReferenceEquals(localCard, _activeThinkingCard))
                    {
                        _isThinkingExpanded = nextExpanded;
                        _userToggledThinking = true;
                    }
                }
            };

            _activeThinkingCard = thinkingCard;
            _activeThinkingHeaderLabel = thinkingHeaderLabel;
            _activeThinkingArrowLabel = thinkingArrowLabel;
            _activeThinkingBody = thinkingBody;
            _activeThinkingViewer = thinkingMdViewer;

            // 原生 Markdown 富文本内容（支持任意划词选区与 Ctrl+C 自由复制）
            var mdViewer = new SelectableMarkdownTextBlock
            {
                UseWorkspaceAppearance = true,
                Markdown = string.Empty,
                BaseFontSize = 13.5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Focusable = true,
                IsHitTestVisible = true
            };
            mdViewer.LinkClicked += url => OpenUrl(url);

            // 生成思考中指示器（卡片内、正文下方）
            var indicator = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
                IsVisible = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dot = new Border
            {
                Width = 6,
                Height = 14,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(dot, Border.BackgroundProperty, "SctAccentBrush", "#797EFE", "#797EFE");
            indicator.Children.Add(dot);

            var thinkingText = new TextBlock
            {
                Text = "AI 正在思考并输出中...",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetThemeBrush(thinkingText, TextBlock.ForegroundProperty, "SctTextSecondaryBrush", "#222222", "#FFFFFF");
            _activeThinkingText = thinkingText;
            indicator.Children.Add(thinkingText);

            var contentStack = new StackPanel { Spacing = 4 };
            contentStack.Children.Add(thinkingCard);
            contentStack.Children.Add(mdViewer);
            contentStack.Children.Add(indicator);

            var cardBorder = new ContinuousBorder
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ScreenRadius("SctRadiusSmall"), ScreenRadius("SctRadiusControl"), ScreenRadius("SctRadiusControl"), ScreenRadius("SctRadiusControl")),
                Padding = new Thickness(14, 11),
                Child = contentStack
            };
            SetThemeBrush(cardBorder, Border.BackgroundProperty, "SctChatBubbleAiBg", "#EAEAEA", "#333333");
            SetThemeBrush(cardBorder, Border.BorderBrushProperty, "SctSoftBorderBrush", "#DCDCDC", "#444444");

            // AI 回复卡片右键菜单
            var aiMenu = new ContextMenu();

            var menuCopyText = new MenuItem
            {
                Header = "复制纯文本",
                Icon = OcrIconGeometry.CreateIcon(OcrIconGeometry.Copy, "menuIcon")
            };
            menuCopyText.Click += (s, e) =>
            {
                string md = mdViewer.Markdown ?? string.Empty;
                if (!string.IsNullOrEmpty(md))
                {
                    ClipboardService.SetText(MarkdownInlineEngine.StripMarkdown(md));
                    ShowToast("已复制纯文本到剪贴板");
                    AppLogger.Info("[AiVisionWindow] ContextMenu: Copied AI response plain text.");
                }
            };
            aiMenu.Items.Add(menuCopyText);

            var menuCopyMd = new MenuItem
            {
                Header = "复制 Markdown 源码",
                Icon = OcrIconGeometry.CreateIcon(OcrIconGeometry.FileDoc, "menuIcon")
            };
            menuCopyMd.Click += (s, e) =>
            {
                string md = mdViewer.Markdown ?? string.Empty;
                if (!string.IsNullOrEmpty(md))
                {
                    ClipboardService.SetText(md);
                    ShowToast("已复制 Markdown 源码到剪贴板");
                    AppLogger.Info("[AiVisionWindow] ContextMenu: Copied AI response markdown source.");
                }
            };
            aiMenu.Items.Add(menuCopyMd);

            var menuCopyThinking = new MenuItem
            {
                Header = "复制思考过程",
                Icon = OcrIconGeometry.CreateIcon(OcrIconGeometry.Copy, "menuIcon")
            };
            menuCopyThinking.Click += (s, e) =>
            {
                string thinking = thinkingMdViewer.Markdown ?? string.Empty;
                if (!string.IsNullOrEmpty(thinking))
                {
                    ClipboardService.SetText(thinking);
                    ShowToast("已复制思考过程到剪贴板");
                    AppLogger.Info("[AiVisionWindow] ContextMenu: Copied AI thinking process.");
                }
                else
                {
                    ShowToast("此条回复暂无思考过程", isSuccess: false);
                }
            };
            aiMenu.Items.Add(menuCopyThinking);

            var menuCopyImg = new MenuItem
            {
                Header = "复制生成图片",
                Icon = OcrIconGeometry.CreateIcon(OcrIconGeometry.Image, "menuIcon")
            };
            aiMenu.Items.Add(menuCopyImg);

            cardBorder.ContextMenu = aiMenu;
            right.Children.Add(cardBorder);
            _activeMessageStack = contentStack;

            // 卡片下方操作胶囊行：停止生成 / 复制 / 复制图片（品牌高亮）
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var stopBtn = new Button
            {
                Content = "停止生成",
                Classes = { "chipGhost" },
                IsVisible = true
            };
            stopBtn.Click += OnStopGeneratingClick;
            actions.Children.Add(stopBtn);

            var copyBtn = new Button
            {
                Content = "复制",
                Classes = { "chipGhost" }
            };
            copyBtn.Click += (s, e) =>
            {
                string text = mdViewer.Markdown ?? string.Empty;
                if (!string.IsNullOrEmpty(text))
                {
                    ClipboardService.SetText(MarkdownInlineEngine.StripMarkdown(text));
                    copyBtn.Content = "✓ 已复制";
                    ShowToast("已复制此条回复纯文本到剪贴板");
                    AppLogger.Info("[AiVisionWindow] HeaderCopyButton: Copied AI response plain text.");
                    DispatcherTimer.RunOnce(() =>
                    {
                        copyBtn.Content = "复制";
                    }, TimeSpan.FromSeconds(2));
                }
            };
            actions.Children.Add(copyBtn);

            var copyImgBtn = new Button
            {
                Content = CreateCopyImgButtonContent("复制图片", false),
                Classes = { "chipAccent" },
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(copyImgBtn, "将 AI 分析排版渲染为一张品牌长图并复制到剪贴板");
            copyImgBtn.Click += (s, e) =>
            {
                CopyAnswerAsImage(mdViewer.Markdown, model, badgeText, copyImgBtn);
            };
            actions.Children.Add(copyImgBtn);

            menuCopyImg.Click += (s, e) =>
            {
                CopyAnswerAsImage(mdViewer.Markdown, model, badgeText, copyImgBtn);
            };

            // 联动：指示器隐藏（生成结束）时，停止按钮同步隐去
            indicator.PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(Visual.IsVisible))
                {
                    stopBtn.IsVisible = indicator.IsVisible;
                }
            };

            right.Children.Add(actions);

            ChatMessagesContainer.Children.Add(row);
            return (mdViewer, indicator);
        }

        private void UpdateEmptyState()
        {
            bool empty = ChatMessagesContainer.Children.Count == 0;
            if (EmptyStatePanel != null) EmptyStatePanel.IsVisible = empty;
            if (QuickPillsRow != null) QuickPillsRow.IsVisible = !empty;
        }

        private void ScrollChatToEnd()
        {
            if (_closed || _scrollToEndQueued) return;
            _scrollToEndQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollToEndQueued = false;
                if (_closed) return;
                ChatScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }

        private void ScrollChatToTop()
        {
            Dispatcher.UIThread.Post(() =>
            {
                ChatScrollViewer?.ScrollToHome();
                Dispatcher.UIThread.Post(() => ChatScrollViewer?.ScrollToHome(), DispatcherPriority.Background);
            }, DispatcherPriority.Normal);
        }

        private void OnToggleSearchChanged(object? sender, RoutedEventArgs e)
        {
            bool newValue = BtnToggleSearch.IsChecked == true;
            if (newValue == _searchEnabled) return;
            _searchEnabled = newValue;
            var settings = AppSettings.Current;
            settings.AiSearchEnabled = _searchEnabled;
            settings.Save();
            TxtStatus.Text = _searchEnabled ? "已开启联网搜索" : "已关闭联网搜索";
            AppLogger.Info($"[AiVisionWindow] Search toggle changed to {_searchEnabled}");
        }

        /// <summary>深度思考开关（issue #54）：与联网搜索同一范式，窗口内切换即写回全局设置
        /// （用户的偏好通常是稳定的，下次开窗延续；要单次临时改也只需再点一下）。
        /// <para>开头那条 <c>newValue == _thinkingEnabled</c> 早退同时承担"程序赋值不误触发写盘"的职责
        /// （初始化时先改字段再写 <c>IsChecked</c>，于是这里判等成立、直接返回）。</para></summary>
        private void OnToggleThinkingChanged(object? sender, RoutedEventArgs e)
        {
            bool newValue = BtnToggleThinking.IsChecked == true;
            if (newValue == _thinkingEnabled) return;
            _thinkingEnabled = newValue;
            var settings = AppSettings.Current;
            settings.AiThinkingEnabled = _thinkingEnabled;
            settings.Save();
            TxtStatus.Text = _thinkingEnabled ? "已开启深度思考" : "已关闭深度思考（直接作答）";
            AppLogger.Info($"[AiVisionWindow] Thinking toggle changed to {_thinkingEnabled}");
        }

        private string GetCurrentModel()
        {
            if (CmbModelSelect?.SelectedItem is AiModelDropDownItem item && item.IsModel && !string.IsNullOrWhiteSpace(item.ModelId))
            {
                return item.ModelId.Trim();
            }
            if (CmbModelSelect?.SelectedItem is string m && !string.IsNullOrWhiteSpace(m))
            {
                return m.Trim();
            }
            var s = AppSettings.Current;
            return string.IsNullOrWhiteSpace(s.AiModel) ? "gpt-4o" : s.AiModel.Trim();
        }

        private AiProtocolType GetCurrentProtocol()
        {
            var settings = AppSettings.Current;
            return (string.Equals(settings.AiProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(settings.AiProtocol, "Claude", StringComparison.OrdinalIgnoreCase))
                ? AiProtocolType.Anthropic
                : AiProtocolType.OpenAI;
        }

        /// <summary>
        /// 搜索优先四阶段（Google AI 搜索式）：
        /// ① 策略规划——只输出搜索词，剥夺大模型回答权；
        /// ② 执行检索——代码调用搜索引擎，不经大模型；
        /// ③ 事实注入——拼终极 Prompt（用户问题 + 图片 + 检索内容 + 严格指令）；
        /// ④ 基于事实的生成与引用——一次流式输出 grounded 答案（逐句引用 + 末尾来源）。
        /// </summary>
        private void RunSearchThenAnswerAsync(
            AiVisionRequest baseReq,
            string userPrompt,
            string model,
            AiProtocolType protocol,
            AiApiConfig activeConfig,
            Stopwatch sw,
            StackPanel generatingIndicator,
            SelectableMarkdownTextBlock mdViewer,
            CancellationToken ct)
        {
            var settings = AppSettings.Current;

            Task.Run(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // ① 阶段一：策略规划（只输出搜索词，禁止抢答/分析）
                    await Dispatcher.UIThread.InvokeAsync(() => { if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return; TxtStatus.Text = "正在规划检索策略..."; });
                    string keywords = await GenerateSearchQueryAsync(baseReq.ImageBase64, _textContentTarget,
                        protocol, model, activeConfig, settings, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                    {
                        HandleCancelled(generatingIndicator, sw, ct);
                        return;
                    }

                    // ② 阶段二：执行检索（代码，不经大模型）
                    await Dispatcher.UIThread.InvokeAsync(() => { if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return; TxtStatus.Text = "正在联网检索相关网页..."; });
                    var results = await AiWebSearchService.Instance.SearchAsync(
                        settings.AiSearchEndpoint, keywords, 5, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                    {
                        HandleCancelled(generatingIndicator, sw, ct);
                        return;
                    }
                    AppLogger.Info($"[AiVisionWindow] Search results: {results.Count}");

                    if (results.Count == 0)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return; TxtStatus.Text =
                            string.IsNullOrWhiteSpace(baseReq.ImageBase64) ? "未检索到相关网页，仅依据这段文字分析"
                                                                    : "未检索到相关网页，仅依据图片分析"; });
                        RunStreamingAnswerAsync(baseReq, userPrompt, sw, generatingIndicator, mdViewer, ct, null);
                        return;
                    }

                    // ③ 阶段三：事实注入——组装终极 Prompt（用户问题 + 待答内容 + 检索内容 + 严格指令）
                    string context = AiWebSearchService.BuildSearchContextPrompt(results);
                    string groundedPrompt = BuildGroundedPrompt(userPrompt, context, hasImage: !string.IsNullOrWhiteSpace(baseReq.ImageBase64));

                    var groundedReq = new AiVisionRequest
                    {
                        Protocol = protocol,
                        BaseUrl = activeConfig.BaseUrl,
                        ApiKey = activeConfig.GetEffectiveApiKey(),
                        Model = model,
                        Prompt = groundedPrompt,
                        ImageBase64 = baseReq.ImageBase64,
                        ImageMimeType = baseReq.ImageMimeType,
                        TimeoutSeconds = settings.AiTimeoutSeconds
                    };

                    // ④ 阶段四：基于事实的生成与引用——流式输出 grounded 答案，完成后追加来源区
                    List<AiSearchResult> cached = results;
                    RunStreamingAnswerAsync(groundedReq, userPrompt, sw, generatingIndicator, mdViewer, ct,
                        () => _ = AppendSourcesSectionAsync(cached, ct));
                }
                catch (OperationCanceledException)
                {
                    HandleCancelled(generatingIndicator, sw, ct);
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        HandleCancelled(generatingIndicator, sw, ct);
                        return;
                    }
                    AppLogger.Error($"[AiVisionWindow] Search flow failed: {ex.GetType().Name}", ex);
                    await Dispatcher.UIThread.InvokeAsync(() => { if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return; TxtStatus.Text = "联网流程失败，回退普通分析"; });
                    RunStreamingAnswerAsync(baseReq, userPrompt, sw, generatingIndicator, mdViewer, ct, null);
                }
            });
        }

        private bool IsCurrentAnalysis(CancellationToken ct) => !_closed && !_isPreparingImage && _analysisCts != null && _analysisCts.Token == ct;

        private void HandleCancelled(StackPanel generatingIndicator, Stopwatch sw, CancellationToken ct)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentAnalysis(ct)) return;
                _streamThrottleTimer?.Stop();
                sw.Stop();
                generatingIndicator.IsVisible = false;
                StopThinkingTimer();
                TxtStatus.Text = "已停止生成";
                if (_latestAiBuilder.Length == 0 && _activeMarkdownViewer != null)
                {
                    _latestAiBuilder.Append("*（已停止生成）*");
                    _activeMarkdownViewer.Markdown = _latestAiBuilder.ToString();
                }
                AppLogger.Info($"[AiVisionWindow] Generation cleanly cancelled after {sw.ElapsedMilliseconds}ms");
            });
        }

        /// <summary>
        /// 阶段一：策略规划。让 AI 只看待答内容提取检索词，禁止抢答/分析、禁止输出正文。
        /// 两种内容源共用这一个方法：给了 <paramref name="imageBase64"/> 就走视觉请求，
        /// 否则把 <paramref name="textSource"/> 写进提问词走纯文本请求——两条只是"喂给模型的东西"不同，
        /// 输出契约（一行空格分隔的检索词 + 同一套清洗）完全一样，故不复制成两个方法。
        /// </summary>
        private async Task<string> GenerateSearchQueryAsync(
            string imageBase64,
            string? textSource,
            AiProtocolType protocol,
            string model,
            AiApiConfig activeConfig,
            AppSettings settings,
            CancellationToken ct)
        {
            bool textOnly = string.IsNullOrWhiteSpace(imageBase64);
            try
            {
                string genPrompt = textOnly
                    ? "你是搜索引擎查询词生成器。下面是用户选中的一段文字，你的唯一任务是“规划搜索策略”。\n" +
                      "【禁止】解释这段文字、回答任何问题、输出任何分析或正文。\n" +
                      "只输出 1 条用于网络搜索的中文关键词查询，用来检索与该文字主题（产品或品牌）相关的网页。\n" +
                      "要求：只输出查询词本身，不要任何解释、引号或标点结尾；3-8 个关键词，用空格分隔；\n" +
                      "从中提取品牌名、产品名、标语、专有名词等可检索信息，并把品牌名与产品类型/中文语境组合起来" +
                      "（例如“IN LIGHT 松茸 超级食物 罐装”），避免只搜单个英文单词或通用词（如 light）；" +
                      "避免“这、是、一种、显示”等口语化填充词。\n\n" +
                      "用户选中的文字：\n" + TrimForQuery(textSource)
                    : "你是搜索引擎查询词生成器。现在给你一张图片，你的唯一任务是“规划搜索策略”。\n" +
                      "【禁止】分析图片内容、禁止回答任何问题、禁止输出任何解释或正文。\n" +
                      "只输出 1 条用于网络搜索的中文关键词查询，用来检索与该图片主题（产品或品牌）相关的网页。\n" +
                      "要求：只输出查询词本身，不要任何解释、引号或标点结尾；3-8 个关键词，用空格分隔；\n" +
                      "从图片中提取品牌名、产品名、标语、专有名词等可检索信息，并把品牌名与产品类型/中文语境组合起来" +
                      "（例如“IN LIGHT 松茸 超级食物 罐装”），避免只搜单个英文单词或通用词（如 light）；" +
                      "避免“这、是、一种、显示”等口语化填充词。";

                var req = new AiVisionRequest
                {
                    Protocol = protocol,
                    BaseUrl = activeConfig.BaseUrl,
                    ApiKey = activeConfig.GetEffectiveApiKey(),
                    Model = model,
                    Prompt = genPrompt,
                    ImageBase64 = imageBase64,
                    ImageMimeType = "image/png",
                    TimeoutSeconds = settings.AiTimeoutSeconds
                };

                var sb = new StringBuilder();
                // 🔴 这一步是"提炼检索关键词"的内部调用，产出只有几个词、用户永远看不到它的思考过程，
                // 故**无条件**关思考（不看 _thinkingEnabled）：它是联网那条路上纯粹的前置延迟，
                // 让推理模型在这里思考十几秒纯属浪费。下方本就有一段正则硬剥 <think> 的兜底，
                // 说明"这里的思考毫无用处"早有共识，本轮把它前推到请求侧省掉那段等待。
                if (textOnly)
                {
                    await AiVisionService.Instance.AnalyzeTextStreamAsync(
                        req,
                        chunk => sb.Append(chunk),
                        onThinkingChunkReceived: null,
                        ct: ct,
                        disableThinking: true).ConfigureAwait(false);
                }
                else
                {
                    await AiVisionService.Instance.AnalyzeImageStreamAsync(
                        req,
                        chunk => sb.Append(chunk),
                        onThinkingChunkReceived: null,
                        ct: ct,
                        disableThinking: true).ConfigureAwait(false);
                }

                string q = sb.ToString();
                if (q.Contains("<think>", StringComparison.OrdinalIgnoreCase))
                {
                    q = Regex.Replace(q, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
                    q = Regex.Replace(q, @"<think>[\s\S]*", "", RegexOptions.IgnoreCase);
                }
                q = Regex.Replace(q, @"[\r\n\t""'“”‘’、，。；：·？?！!【】\[\]（）()]", " ");
                q = Regex.Replace(q, @"\s+", " ").Trim();
                if (q.Length > 80) q = q.Substring(0, 80);
                if (string.IsNullOrWhiteSpace(q)) q = textOnly ? TrimForQuery(textSource) : "图片";
                if (string.IsNullOrWhiteSpace(q)) q = "相关内容";
                AppLogger.Info($"[AiVisionWindow] Generated search query chars={q.Length}");
                return q;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                AppLogger.Warn($"[AiVisionWindow] Query generation failed: {ex.GetType().Name}");
                // 兜底检索词不能写死"图片"：文本模式下那会搜出一堆无关结果，退回用原文前若干字检索。
                string fallback = textOnly ? TrimForQuery(textSource) : "图片";
                return string.IsNullOrWhiteSpace(fallback) ? "相关内容" : fallback;
            }
        }

        /// <summary>提词阶段只把选中文本的前 300 字喂给模型：检索词由主题词决定，超长原文只会稀释它。</summary>
        private static string TrimForQuery(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            return t.Length <= 300 ? t : t.Substring(0, 300);
        }

        /// <summary>
        /// 阶段三：事实注入。组装终极 Prompt = 用户问题 + 检索到的网页内容 + 严格系统指令。
        /// 强制只依据提供的网页内容回答；未提及的信息必须明说“未找到相关信息”，逐处标注来源编号。
        /// <paramref name="hasImage"/> 决定指令里那句"结合图片组织表达"要不要出现——文本模式下
        /// 模型手里根本没有图，留着这句只会让它去描述一张不存在的图。
        /// </summary>
        private static string BuildGroundedPrompt(string userPrompt, string context, bool hasImage)
        {
            return
                "用户的问题/需求：\n" + userPrompt + "\n\n" +
                "以下是联网检索到的相关网页内容（唯一的事实来源）：\n" +
                context + "\n\n" +
                "回答规则（必须严格遵守）：\n" +
                "- 你必须只根据上面提供的网页内容来回答用户的问题" + (hasImage ? "；同时结合图片内容组织表达。" : "。") + "\n" +
                "- 如果网页内容中没有提及用户想知道的信息，请明确回答“未找到相关信息”，不要臆测。\n" +
                "- 引用网页内容时，只在对应句末用 [1] [2] 等编号标注来源，禁止在正文中粘贴或显示 URL，禁止使用 [[n]](url) 这种链接写法。\n" +
                "- 不要编造来源、不要引用未提供的网页" + (hasImage ? "；与图片主题无关的内容不要展开。" : "。") + "\n" +
                "- 输出要简洁清晰：优先用短句与分点，避免长段落、空话与重复；不要在回答末尾自行输出「参考来源」列表，界面会自动展示。";
        }

        private void EnsureStreamThrottleTimer()
        {
            if (_streamThrottleTimer != null) return;
            _streamThrottleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
        }

        /// <summary>
        /// 批量节流缓冲刷入 UI 线程：将思考流与正文流安全分流更新，降低高频 Markdown 重排对界面响应的影响。
        /// </summary>
        private void FlushStreamBufferToUi(
            Stopwatch sw,
            SelectableMarkdownTextBlock mdViewer,
            bool isFinal)
        {
            string currentThinking;
            string currentAnswer;
            lock (_streamBufferLock)
            {
                if (!_hasPendingStreamUpdate && !isFinal) return;
                _hasPendingStreamUpdate = false;
                currentThinking = _thinkingBuilder.ToString();
                currentAnswer = _latestAiBuilder.ToString();
            }

            // 1. 处理深度思考内容
            if (currentThinking.Length > 0 && _activeThinkingCard != null && _activeThinkingViewer != null)
            {
                if (!_activeThinkingCard.IsVisible)
                {
                    _activeThinkingCard.IsVisible = true;
                }

                if ((_isThinkingExpanded || isFinal) && _activeThinkingViewer.Markdown != currentThinking)
                {
                    _activeThinkingViewer.Markdown = currentThinking;
                }

                if (currentAnswer.Length == 0 && !isFinal)
                {
                    _thinkingElapsedSec = _thinkingStopwatch.Elapsed.TotalSeconds;
                    if (_activeThinkingHeaderLabel != null)
                    {
                        _activeThinkingHeaderLabel.Text = $"深度思考中 · {_thinkingElapsedSec:F1}s";
                    }
                    TxtStatus.Text = $"深度思考中 · {sw.Elapsed.TotalSeconds:F1}s";
                }
                else
                {
                    if (!_isThinkingCompleted)
                    {
                        _isThinkingCompleted = true;
                        _thinkingElapsedSec = _thinkingStopwatch.Elapsed.TotalSeconds;
                        if (_activeThinkingHeaderLabel != null)
                        {
                            _activeThinkingHeaderLabel.Text = $"已深度思考 (耗时 {_thinkingElapsedSec:F1}s)";
                        }
                        if (!_userToggledThinking && currentAnswer.Length > 0 && _activeThinkingBody != null && _activeThinkingArrowLabel != null)
                        {
                            _isThinkingExpanded = false;
                            _activeThinkingBody.IsVisible = false;
                            _activeThinkingArrowLabel.Text = "⌄ 展开";
                        }
                    }
                }
            }

            // 2. 处理正文内容
            if (currentAnswer.Length > 0)
            {
                if (mdViewer.Markdown != currentAnswer)
                {
                    mdViewer.Markdown = currentAnswer;
                }
                if (!isFinal)
                {
                    TxtStatus.Text = $"生成中 · {sw.Elapsed.TotalSeconds:F1}s";
                }
            }

            ScrollChatToEnd();
        }

        /// <summary>
        /// 通用流式回答：流式写入唯一气泡，思考与正文分离、节流调度，处理完成/取消/异常与历史记录。
        /// </summary>
        private void RunStreamingAnswerAsync(
            AiVisionRequest req,
            string userPrompt,
            Stopwatch sw,
            StackPanel generatingIndicator,
            SelectableMarkdownTextBlock mdViewer,
            CancellationToken ct,
            Action? onCompleted)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => RunStreamingAnswerAsync(req, userPrompt, sw, generatingIndicator, mdViewer, ct, onCompleted));
                return;
            }
            if (!IsCurrentAnalysis(ct)) return;
            if (ct.IsCancellationRequested) { HandleCancelled(generatingIndicator, sw, ct); return; }
            EnsureStreamThrottleTimer();
            EventHandler onTick = (s, e) => { if (IsCurrentAnalysis(ct)) FlushStreamBufferToUi(sw, mdViewer, isFinal: false); };
            _streamTickHandler = onTick;
            _streamThrottleTimer!.Tick += onTick;
            _streamThrottleTimer.Start();

            var thinkingFilter = new StreamingThinkingFilter(
                onContentChunk: contentChunk =>
                {
                    lock (_streamBufferLock)
                    {
                        if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return;
                        _latestAiBuilder.Append(contentChunk);
                        _hasPendingStreamUpdate = true;
                    }
                },
                onThinkingChunk: thinkChunk =>
                {
                    lock (_streamBufferLock)
                    {
                        if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return;
                        _thinkingBuilder.Append(thinkChunk);
                        _hasPendingStreamUpdate = true;
                    }
                });

            Task.Run(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // 按请求里有没有图分派：AnalyzeImageStreamAsync 对空 ImageBase64 是直接抛
                    // ArgumentException("图片数据为空") 的，文本模式必须走 AnalyzeTextStreamAsync 那条。
                    // 判据取 req 而非窗口字段——联网分支带回来的 groundedReq 是另拼的一份，
                    // 只有它自己的 ImageBase64 才代表"这一次请求真正含不含图"。
                    if (string.IsNullOrWhiteSpace(req.ImageBase64))
                    {
                        await AiVisionService.Instance.AnalyzeTextStreamAsync(
                            req,
                            onChunkReceived: chunk => thinkingFilter.Feed(chunk),
                            onThinkingChunkReceived: thinkChunk =>
                            {
                                lock (_streamBufferLock)
                                {
                                    if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return;
                                    _thinkingBuilder.Append(thinkChunk);
                                    _hasPendingStreamUpdate = true;
                                }
                            },
                            ct: ct,
                            disableThinking: !_thinkingEnabled).ConfigureAwait(false);
                    }
                    else
                    {
                        await AiVisionService.Instance.AnalyzeImageStreamAsync(
                            req,
                            onChunkReceived: chunk => thinkingFilter.Feed(chunk),
                            onThinkingChunkReceived: thinkChunk =>
                            {
                                lock (_streamBufferLock)
                                {
                                    if (!IsCurrentAnalysis(ct) || ct.IsCancellationRequested) return;
                                    _thinkingBuilder.Append(thinkChunk);
                                    _hasPendingStreamUpdate = true;
                                }
                            },
                            ct: ct,
                            disableThinking: !_thinkingEnabled).ConfigureAwait(false);
                    }

                    thinkingFilter.Flush();

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!IsCurrentAnalysis(ct)) return;
                        _streamThrottleTimer.Stop();
                        _streamThrottleTimer.Tick -= onTick;

                        sw.Stop();
                        generatingIndicator.IsVisible = false;
                        StopThinkingTimer();

                        FlushStreamBufferToUi(sw, mdViewer, isFinal: true);
                        TxtStatus.Text = $"耗时 {sw.ElapsedMilliseconds}ms 完成";

                        string aiReply = _latestAiBuilder.ToString();
                        if (!string.IsNullOrWhiteSpace(aiReply))
                        {
                            // 统计：AI 对话成功一次（识图与追问各计一次；
                            // 联网搜索的内部查询词生成走 GenerateSearchQueryAsync，不在此路径，不计）
                            UsageStatistics.RecordAiChat();
                            _chatHistory.Add(("user", userPrompt));
                            _chatHistory.Add(("assistant", aiReply));
                            AppLogger.Info($"[AiVisionWindow] User prompt chars={userPrompt.Length}");
                            AppLogger.Info($"[AiVisionWindow] AI reply chars={aiReply.Length}");
                        }

                        AppLogger.Info($"[AiVisionWindow] Analysis completed in {sw.ElapsedMilliseconds}ms, outputChars={aiReply.Length}");
                        onCompleted?.Invoke();
                    });
                }
                catch (OperationCanceledException)
                {
                    thinkingFilter.Flush();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!IsCurrentAnalysis(ct)) return;
                        _streamThrottleTimer.Stop();
                        _streamThrottleTimer.Tick -= onTick;

                        sw.Stop();
                        generatingIndicator.IsVisible = false;
                        StopThinkingTimer();

                        FlushStreamBufferToUi(sw, mdViewer, isFinal: true);
                        TxtStatus.Text = "已停止生成";
                        if (_latestAiBuilder.Length == 0 && _thinkingBuilder.Length == 0)
                        {
                            _latestAiBuilder.Append("*（已停止生成）*");
                            mdViewer.Markdown = _latestAiBuilder.ToString();
                        }
                        AppLogger.Warn($"[AiVisionWindow] Analysis cancelled after {sw.ElapsedMilliseconds}ms");
                    });
                }
                catch (Exception ex)
                {
                    thinkingFilter.Flush();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!IsCurrentAnalysis(ct)) return;
                        _streamThrottleTimer.Stop();
                        _streamThrottleTimer.Tick -= onTick;

                        sw.Stop();
                        generatingIndicator.IsVisible = false;
                        StopThinkingTimer();

                        FlushStreamBufferToUi(sw, mdViewer, isFinal: true);
                        TxtStatus.Text = "生成失败";
                        _latestAiBuilder.AppendLine("\n\n> ⚠️ **请求发生错误**:\n> " + ex.Message);
                        mdViewer.Markdown = _latestAiBuilder.ToString();
                        AppLogger.Error($"[AiVisionWindow] Analysis error after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}", ex);
                    });
                }
            });
        }

        /// <summary>
        /// 回答结束后，在当前 AI 气泡内追加「参考来源」区：水平缩略图条 + 编号列表（均可点击）。
        /// 缩略图异步抓取 og:image，不阻塞回答展示。
        /// </summary>
        private async Task AppendSourcesSectionAsync(List<AiSearchResult> results, CancellationToken ct)
        {
            if (results == null || results.Count == 0 || _activeMessageStack == null) return;

            var sourcesPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };

            var divider = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 0, 8)
            };
            SetThemeBrush(divider, Border.BackgroundProperty, "SctSoftBorderBrush", "#DCDCDC", "#444444");
            sourcesPanel.Children.Add(divider);

            var sourcesTitle = new TextBlock
            {
                Text = $"🔗 参考来源 · {results.Count} 个网页",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold
            };
            SetThemeBrush(sourcesTitle, TextBlock.ForegroundProperty, "SctCardTitleBrush", "#797EFE", "#A08BF8");
            sourcesPanel.Children.Add(sourcesTitle);

            // 水平缩略图条（Google AI 概述风格：相关图片）
            var thumbPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var thumbStrip = new ScrollViewer
            {
                Content = thumbPanel,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Height = 78
            };
            sourcesPanel.Children.Add(thumbStrip);

            var thumbImages = new List<Image>();
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var img = new Image
                {
                    Width = 116,
                    Height = 68,
                    Stretch = Stretch.UniformToFill,
                    ClipToBounds = true,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                string u = r.Url;
                img.PointerPressed += (s, e) => OpenUrl(u);
                thumbPanel.Children.Add(img);
                thumbImages.Add(img);
            }

            // 编号来源列表
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var row = new StackPanel { Spacing = 2, Cursor = new Cursor(StandardCursorType.Hand) };
                var titleText = new TextBlock
                {
                    Text = $"{i + 1}. {r.Title}",
                    FontSize = 13,
                    FontWeight = FontWeight.Medium,
                    TextWrapping = TextWrapping.Wrap,
                    TextDecorations = TextDecorations.Underline
                };
                SetThemeBrush(titleText, TextBlock.ForegroundProperty, "SctTextPrimaryBrush", "#222222", "#FFFFFF");
                row.Children.Add(titleText);
                string snippet = string.IsNullOrWhiteSpace(r.Content) ? r.Url : r.Content;
                if (snippet.Length > 140) snippet = snippet.Substring(0, 140) + "...";
                var snippetText = new TextBlock
                {
                    Text = snippet,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                SetThemeBrush(snippetText, TextBlock.ForegroundProperty, "SctTextMutedBrush", "#6E6E73", "#9F9FA1");
                row.Children.Add(snippetText);
                string url = r.Url;
                row.PointerPressed += (s, e) => OpenUrl(url);
                sourcesPanel.Children.Add(row);
            }

            _activeMessageStack.Children.Add(sourcesPanel);

            // 异步抓取缩略图（og:image，失败回退 favicon）
            await AiWebSearchService.Instance.EnrichWithThumbnailsAsync(results, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (int i = 0; i < results.Count && i < thumbImages.Count; i++)
                {
                    string th = results[i].Thumbnail;
                    if (string.IsNullOrWhiteSpace(th)) continue;
                    LoadImageAsync(th, thumbImages[i]);
                }
            });
        }

        private void LoadImageAsync(string url, Image target)
        {
            Task.Run(async () =>
            {
                try
                {
                    byte[] bytes = await ThumbHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (bytes == null || bytes.Length == 0) return;
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    Dispatcher.UIThread.Post(() =>
                    {
                        target.Source = bmp;
                    });
                }
                catch
                {
                    // 静默：缩略图加载失败不打扰用户
                }
            });
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[AiVisionWindow] OpenUrl failed: {ex.GetType().Name}", ex);
                TxtStatus.Text = "无法打开链接";
            }
        }

        private void OnSendPromptClick(object? sender, RoutedEventArgs e)
        {
            string p = TxtPromptInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(p)) return;
            AppLogger.Info($"[AiVisionWindow] User submitted prompt chars={p.Length}");
            TxtPromptInput.Text = string.Empty;
            StartAnalysis(p);
        }

        private void OnPromptInputKeyDown(object? sender, KeyEventArgs e)
        {
            // @ 预设下拉打开时：↑↓ 移动高亮、Enter/Tab 选中、Esc 关闭，均优先于发送
            if (_mentionPopup != null && _mentionPopup.HandleKeyDown(e))
            {
                e.Handled = true;
                return;
            }

            // 粘贴：与提问弹窗同一条显式接管（框架那条键位在此不可靠），置 Handled 防双份粘贴。
            if (AiPromptPaste.IsPasteGesture(e))
            {
                e.Handled = true;
                _ = AiPromptPaste.PasteWithFeedbackAsync(TxtPromptInput);
                return;
            }

            if (e.Key == Key.Enter)
            {
                OnSendPromptClick(sender, e);
                e.Handled = true;
            }
        }

        private void OnQuickPromptClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                AppLogger.Info("[AiVisionWindow] Quick prompt clicked");
                StartAnalysis(tag);
            }
        }


        private void OnStopGeneratingClick(object? sender, RoutedEventArgs e)
        {
            if (e != null) e.Handled = true;
            AppLogger.Info("[AiVisionWindow] User clicked Stop Generating button.");
            try
            {
                _analysisCts?.Cancel();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiVisionWindow] Cancel error: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 将 AI 识图结果渲染为适配图文卡片长图并写入剪贴板（支持 Toast 与按钮动效反馈）。
        /// </summary>
        // 屏幕消息容器只消费共享半径，成片导出继续沿用原独立几何。
        private double ScreenRadius(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is CornerRadius radius ? radius.TopLeft : 0;

        private async void CopyAnswerAsImage(string? markdown, string model, string badgeText, Button? triggerBtn = null)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                ShowToast("内容为空，无法生成图片", isSuccess: false);
                return;
            }

            try
            {
                // 1. 清洗正文 Markdown
                string cleanMd = CleanMarkdownForImage(markdown);
                if (string.IsNullOrWhiteSpace(cleanMd))
                {
                    cleanMd = "（无内容）";
                }

                bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

                // 2. 判断是否有原图及原图宽高比（参考 OCR 窗口自适应机制）：
                //    - 宽 > 高（横图）：上下栏排版（原图居中在上，水平分隔线，Markdown 文本在下）
                //    - 高 >= 宽（竖图/方图）：左右栏排版（原图在左，竖分隔线，Markdown 文本在右）
                //    - 无图：单栏纯文本排版
                bool hasImage = _avaloniaBitmap != null && _avaloniaBitmap.PixelSize.Width > 0 && _avaloniaBitmap.PixelSize.Height > 0;
                int bmpW = hasImage ? _avaloniaBitmap!.PixelSize.Width : 0;
                int bmpH = hasImage ? _avaloniaBitmap!.PixelSize.Height : 0;
                bool isLandscape = hasImage && (bmpW > bmpH);

                const double lrTextWidth = 440.0;
                const double lrImageColWidth = 320.0;
                const double lrImageMaxH = 480.0;
                const double lrGap = 24.0;

                const double tbContentWidth = 640.0;
                const double tbImageMaxH = 420.0;

                const double noImgContentWidth = 520.0;
                const double baseFontSize = 14.0;

                var innerPadding = new Thickness(24, 20);
                var outerPadding = new Thickness(18, 22);

                double contentRowWidth;
                double mdWidth;

                if (!hasImage)
                {
                    contentRowWidth = noImgContentWidth;
                    mdWidth = noImgContentWidth;
                }
                else if (isLandscape)
                {
                    contentRowWidth = tbContentWidth;
                    mdWidth = tbContentWidth;
                }
                else
                {
                    contentRowWidth = lrImageColWidth + lrGap + lrTextWidth;
                    mdWidth = lrTextWidth;
                }

                double innerCardWidth = contentRowWidth + innerPadding.Left + innerPadding.Right;
                double outerCanvasWidth = innerCardWidth + outerPadding.Left + outerPadding.Right;

                // 本卡片要用的主题档：底色、字色、图标一律跟它，**不许再问环境**（见下面 ForcedThemeVariant 注释）
                var cardVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;

                // 3. 文字排版组：宽度固定精排，高度自适应
                var mdRender = new SelectableMarkdownTextBlock
                {
                    Width = mdWidth,
                    Markdown = cleanMd,
                    BaseFontSize = baseFontSize,
                    CompactLayout = true,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = isLandscape || !hasImage ? VerticalAlignment.Top : VerticalAlignment.Center,
                    // 🔴 必须显式钉档（2026-10-01 修 issue #53「深色下导出卡片深底深字」）：
                    // 本控件树是裸 new 出来给 RenderTargetBitmap 用的，**从不插入可视树** ⇒ 它的
                    // ActualThemeVariant 解析不出档位，内部 IsDarkTheme() 只能落到 Application 兜底，
                    // 而正文字色走 MarkdownRenderContext 的 TryFindThemedBrush("SctTextPrimaryColor")
                    // 拿着未定档的 variant 去解析 ⇒ 解析出**浅色档的深色文字**。
                    // 而卡片底色是按窗口 ActualThemeVariant（解析正确）定的深色 ⇒ 深底 + 深字。
                    // 这与 ForcedThemeVariant 自身注释里记载的贴图「白底白字」是同一个失效模式：
                    // **底色既已显式决定，字色就必须显式跟同一档**。
                    ForcedThemeVariant = cardVariant,
                };

                // 外层环境画板：规范窗口底色（浅 #F2F2F2 / 深 #282828），与主界面主题一致
                var outerCanvas = new Border
                {
                    Width = outerCanvasWidth,
                    Padding = outerPadding,
                    Background = new SolidColorBrush(Color.Parse(isDark ? "#282828" : "#F2F2F2")),
                };

                var outerStack = new StackPanel { Spacing = 12 };

                // 内层主卡片：圆角 16px 纯色无阴影（浅 #FFFFFF / 深 #282828）
                var innerCard = new Border
                {
                    Width = innerCardWidth,
                    CornerRadius = new CornerRadius(16),
                    Padding = innerPadding,
                    Background = new SolidColorBrush(Color.Parse(isDark ? "#282828" : "#FFFFFF")),
                    BorderBrush = new SolidColorBrush(Color.Parse(isDark ? "#555555" : "#DCDCDC")),
                    BorderThickness = new Thickness(1)
                };

                var cardContentStack = new StackPanel { Spacing = 12 };

                // 头部：主标题 + 模型徽标 + 日期
                var headerStack = new StackPanel { Spacing = 6 };

                var titleRow = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
                };

                var mainTitle = new TextBlock
                {
                    // 文本模式导出的长图不该顶着"识图"两个字——它里面没有任何图。
                    Text = _textContentTarget != null ? "AI 文本解读" : "AI 识图分析",
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(isDark ? "#A08BF8" : "#797EFE")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(mainTitle, 1);
                titleRow.Children.Add(mainTitle);

                var dateBadge = new TextBlock
                {
                    Text = DateTime.Now.ToString("yyyy/MM/dd"),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.Parse(isDark ? "#9F9FA1" : "#6E6E73")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dateBadge, 2);
                titleRow.Children.Add(dateBadge);
                headerStack.Children.Add(titleRow);

                var subRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0)
                };

                var modelBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#797EFE")),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2.5),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(model) ? "AI" : model,
                        FontSize = 10.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#FFFFFF"))
                    }
                };
                subRow.Children.Add(modelBadge);

                var subText = new TextBlock
                {
                    Text = "AI 生成内容 · 仅供参考",
                    FontSize = 11.5,
                    // 与同卡片内日期那行同档取色（原先固定 #6E6E73，深色 #282828 底上偏暗）
                    Foreground = new SolidColorBrush(Color.Parse(isDark ? "#9F9FA1" : "#6E6E73")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                subRow.Children.Add(subText);
                headerStack.Children.Add(subRow);

                cardContentStack.Children.Add(headerStack);

                // 细分割线
                cardContentStack.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.Parse(isDark ? "#555555" : "#DCDCDC")),
                    Margin = new Thickness(0, 2, 0, 4)
                });

                // 内容区：按原图宽高比自适应排版
                if (hasImage && _avaloniaBitmap != null)
                {
                    if (isLandscape)
                    {
                        // 宽 > 高（横图）：上下栏排版（原图居中在上 + 水平分隔线 + Markdown 文本在下）
                        double imgSc = Math.Min(Math.Min(tbContentWidth / bmpW, tbImageMaxH / bmpH), 1.5);
                        double imgW = Math.Floor(bmpW * imgSc);
                        double imgH = Math.Floor(bmpH * imgSc);

                        var imageFrame = new Border
                        {
                            Width = imgW,
                            Height = imgH,
                            CornerRadius = new CornerRadius(10),
                            BorderThickness = new Thickness(1),
                            BorderBrush = new SolidColorBrush(Color.Parse(isDark ? "#555555" : "#DCDCDC")),
                            Background = new SolidColorBrush(Color.Parse(isDark ? "#2B2B2B" : "#F5F5F5")),
                            Clip = new RectangleGeometry(new Rect(0, 0, imgW, imgH), 10, 10),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 2, 0, 6),
                            Child = new Image { Source = _avaloniaBitmap, Width = imgW, Height = imgH }
                        };
                        cardContentStack.Children.Add(imageFrame);

                        var hSep = new Border
                        {
                            Height = 1,
                            Background = new SolidColorBrush(Color.Parse(isDark ? "#555555" : "#DCDCDC")),
                            Margin = new Thickness(0, 4, 0, 8),
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };
                        cardContentStack.Children.Add(hSep);
                        cardContentStack.Children.Add(mdRender);
                    }
                    else
                    {
                        // 高 >= 宽（竖图/方图）：左右分栏（左原图缩略 + 竖分隔线 + 右文本）
                        double imgSc = Math.Min(Math.Min(lrImageColWidth / bmpW, lrImageMaxH / bmpH), 1.5);
                        double imgW = Math.Floor(bmpW * imgSc);
                        double imgH = Math.Floor(bmpH * imgSc);

                        var imageFrame = new Border
                        {
                            Width = imgW,
                            Height = imgH,
                            CornerRadius = new CornerRadius(10),
                            BorderThickness = new Thickness(1),
                            BorderBrush = new SolidColorBrush(Color.Parse(isDark ? "#555555" : "#DCDCDC")),
                            Background = new SolidColorBrush(Color.Parse(isDark ? "#2B2B2B" : "#F5F5F5")),
                            Clip = new RectangleGeometry(new Rect(0, 0, imgW, imgH), 10, 10),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new Image { Source = _avaloniaBitmap, Width = imgW, Height = imgH }
                        };

                        var splitRow = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions($"{lrImageColWidth:0},{lrGap:0},{lrTextWidth:0}")
                        };
                        Grid.SetColumn(imageFrame, 0);
                        splitRow.Children.Add(imageFrame);

                        var vSep = new Border
                        {
                            Width = 1,
                            Background = new SolidColorBrush(Color.Parse(isDark ? "#555555" : "#DCDCDC")),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Stretch
                        };
                        Grid.SetColumn(vSep, 1);
                        splitRow.Children.Add(vSep);

                        Grid.SetColumn(mdRender, 2);
                        splitRow.Children.Add(mdRender);
                        cardContentStack.Children.Add(splitRow);
                    }
                }
                else
                {
                    cardContentStack.Children.Add(mdRender);
                }
                innerCard.Child = cardContentStack;
                outerStack.Children.Add(innerCard);

                // 底部署名栏
                var bottomFooter = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(4, 0, 4, 0)
                };

                var brandTag = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var footerIcon = TryLoadBrandIcon(isDark ? "White" : "Black", 14);
                if (footerIcon != null)
                {
                    brandTag.Children.Add(footerIcon);
                }

                var brandLabel = new TextBlock
                {
                    Text = "yuSnip 智能截图",
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(isDark ? "#A08BF8" : "#797EFE")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                brandTag.Children.Add(brandLabel);
                Grid.SetColumn(brandTag, 0);
                bottomFooter.Children.Add(brandTag);

                outerStack.Children.Add(bottomFooter);
                outerCanvas.Child = outerStack;

                // 整棵离树子树统一钉档（2026-10-01，issue #53）：`ThemeVariantScope` 是 Avalonia 里
                // "给一段子树指定主题档"的那个容器（`RequestedThemeVariant` 只长在它与 TopLevel 上，
                // 不在 Border 上）。上面 mdRender 的 ForcedThemeVariant 治的是"控件自己判明暗"那一路，
                // 这一层治的是"按 variant 查主题字典"那一路——树内任何读主题资源的元素（含将来新增的）
                // 都解析到同一档，不必逐个硬编码双档色。渲染根随之改成它。
                var themeScope = new ThemeVariantScope
                {
                    RequestedThemeVariant = cardVariant,
                    Child = outerCanvas,
                };

                // 5. 自适应整卡测量与超采样高清渲染 (2.0x Retina)
                themeScope.Measure(new Size(outerCanvasWidth, double.PositiveInfinity));
                themeScope.Arrange(new Rect(0, 0, outerCanvasWidth, themeScope.DesiredSize.Height));

                double targetW = outerCanvasWidth;
                double targetH = Math.Ceiling(themeScope.DesiredSize.Height);

                double scale = 2.0;
                // 超长内容自适应降采样：位图单边不超过 8000px，避免超出栅格化/剪贴板上限
                if (targetH * scale > 8000) scale = Math.Max(1.0, 8000.0 / targetH);
                var pixelSize = new PixelSize((int)(targetW * scale), (int)(targetH * scale));
                var dpiVector = new Vector(96.0 * scale, 96.0 * scale);

                using var rtb = new RenderTargetBitmap(pixelSize, dpiVector);
                rtb.Render(themeScope);

                using var ms = new MemoryStream();
                rtb.Save(ms);
                ms.Position = 0;

                using var sysBmp = new System.Drawing.Bitmap(ms);
                if (!await ClipboardService.SetImageAsync(sysBmp)) return;

                ShowToast("已生成图片，粘贴即可使用", isSuccess: true);
                if (triggerBtn != null)
                {
                    triggerBtn.Content = CreateCopyImgButtonContent("已复制", true);
                    DispatcherTimer.RunOnce(() =>
                    {
                        triggerBtn.Content = CreateCopyImgButtonContent("复制图片", false);
                    }, TimeSpan.FromSeconds(2));
                }

                string layoutName = !hasImage ? "TextOnly" : (isLandscape ? "TopBottom" : "LeftRight");
                AppLogger.Info($"[AiVisionWindow] CopyAnswerAsImage: Rendered adaptive template card ({layoutName}) {targetW}x{targetH} (scaled {pixelSize.Width}x{pixelSize.Height}) to clipboard.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[AiVisionWindow] CopyAnswerAsImage failed: {ex.GetType().Name}", ex);
                TxtStatus.Text = "生成图片失败: " + ex.Message;
            }
        }

        private static Image? TryLoadBrandIcon(string variant, double size)
        {
            try
            {
                return new Image
                {
                    Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/" + variant + ".ico"))),
                    Width = size,
                    Height = size,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            catch
            {
                return null;
            }
        }

        private static string CleanMarkdownForImage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            // 行级清理：只剥离“参考来源”相关内容，正文（含 --- 分节线）全部保留。
            // 注意不能用 "\n---" 之类子串截断——真实回复正文中间就有 --- 分节线，会把大段正文误切掉。
            string s = raw.Replace("\r\n", "\n");
            string[] lines = s.Split('\n');

            // 1) 命中“参考来源类”独立标题行（## / ** / 【】 / 纯文本 / 英文 Sources 等）：从该行起整段截断
            var srcHeader = new Regex(
                @"^\s*(?:#{1,6}\s+|\*\*|【|\d+[.、)]\s+)?\s*(参考来源|参考链接|参考资料|参考文献|引用来源|数据来源|消息来源|内容来源|相关链接|延伸阅读|来源|引用|Sources?|References?|Citations?|Bibliography)\s*[:：]?\s*(?:\*\*|】)?\s*$",
                RegexOptions.IgnoreCase);
            int cut = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (srcHeader.IsMatch(lines[i])) { cut = i; break; }
            }
            var keep = new List<string>(cut >= 0 ? lines.Take(cut) : lines);

            // 2) 尾部连续链接块剥离（处理没有标题的来源列表）：
            //    从末尾往前，连续的 (空行 | 裸URL行 | [n] 开头链接条目 | 纯链接行 | 水平线) 一并去掉
            var bareUrl = new Regex(@"^\s*(?:https?|ftp)://\S+\s*$");
            var citeEntry = new Regex(@"^\s*\[\^?\d+\]\s*\S");
            var linkItem = new Regex(@"^\s*(?:[-*•]|\d+[.、)])?\s*\[[^\]]*\]\(https?://\S+\)\s*$");
            var hrLine = new Regex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$");
            while (keep.Count > 0)
            {
                string last = keep[keep.Count - 1];
                if (last.Trim().Length == 0 || bareUrl.IsMatch(last) || citeEntry.IsMatch(last) || linkItem.IsMatch(last) || hrLine.IsMatch(last))
                {
                    keep.RemoveAt(keep.Count - 1);
                }
                else break;
            }

            s = string.Join("\n", keep);

            // 3) 剥离常见 HTML 标签（AI 偶尔混出 <div>/<br> 等；限定字母开头标签，避免误伤 "a < b" 类正文）
            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</p\s*>", "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</?[a-zA-Z][a-zA-Z0-9]*(?:\s+[^<>]*)?/?>", string.Empty);

            // 4) 内联引用标记清理（放在行级处理之后，避免 [n] 前缀影响行结构判断）
            s = Regex.Replace(s, @"\[\[\d+\]\]\([^)]+\)", string.Empty);
            s = Regex.Replace(s, @"^\s*\[\^?\d+\]\s*:\s*https?://\S+\s*$", string.Empty, RegexOptions.Multiline);
            s = Regex.Replace(s, @"\[\^?\d+\]", string.Empty);

            // 清理连续 3 个及以上的空行，保持段落间距均匀
            s = Regex.Replace(s, @"\n{3,}", "\n\n");

            return s.Trim();
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            _analysisCts?.Cancel();
            Close();
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                OnCloseClick(sender, e);
                e.Handled = true;
            }
        }

        private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (WindowState == WindowState.Normal && Bounds.Width >= MinWidth && Bounds.Height >= MinHeight)
            {
                ScheduleSaveWindowSize();
            }
        }

        private void ScheduleSaveWindowSize()
        {
            if (_saveSizeDebounceTimer == null)
            {
                _saveSizeDebounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _saveSizeDebounceTimer.Tick += (s, e) =>
                {
                    _saveSizeDebounceTimer?.Stop();
                    SaveCurrentWindowSize();
                };
            }
            _saveSizeDebounceTimer.Stop();
            _saveSizeDebounceTimer.Start();
        }

        private void SaveCurrentWindowSize()
        {
            try
            {
                if (WindowState != WindowState.Normal) return;
                double w = Bounds.Width > 0 ? Bounds.Width : Width;
                double h = Bounds.Height > 0 ? Bounds.Height : Height;
                if (w >= MinWidth && h >= MinHeight)
                {
                    var settings = AppSettings.Current;
                    if (Math.Abs(settings.AiVisionWindowWidth - w) > 1 || Math.Abs(settings.AiVisionWindowHeight - h) > 1)
                    {
                        settings.AiVisionWindowWidth = Math.Round(w);
                        settings.AiVisionWindowHeight = Math.Round(h);
                        settings.Save();
                        AppLogger.Info($"[AiVisionWindow] Saved window size: {settings.AiVisionWindowWidth}x{settings.AiVisionWindowHeight}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiVisionWindow] SaveCurrentWindowSize error: " + ex.GetType().Name);
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            EnsureWindowSizeWithinScreen();
        }

        /// <summary>
        /// 确保窗口尺寸在当前所在屏幕工作区范围内安全展示，避免跨分辨率或插拔显示器时超出屏幕。
        /// </summary>
        private void EnsureWindowSizeWithinScreen()
        {
            try
            {
                var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
                if (screen == null) return;

                double scale = screen.Scaling;
                if (scale <= 0) scale = 1.0;
                double waH = screen.WorkingArea.Height / scale;
                double waW = screen.WorkingArea.Width / scale;
                if (waH <= 0 || waW <= 0) return;

                if (Width > waW) Width = Math.Max(MinWidth, waW);
                if (Height > waH) Height = Math.Max(MinHeight, waH);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiVisionWindow] EnsureWindowSizeWithinScreen: " + ex.GetType().Name);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            ++_imagePreparationVersion;
            _saveSizeDebounceTimer?.Stop();
            _streamThrottleTimer?.Stop();
            StopDropDownScrollTimer();
            SaveCurrentWindowSize();

            base.OnClosed(e);
            StopThinkingTimer();
            _analysisCts?.Cancel();
            _sourceBitmap?.Dispose();
            _sourceBitmap = null;

            if (_avaloniaBitmap != null)
            {
                _avaloniaBitmap.Dispose();
                _avaloniaBitmap = null;
            }
        }
    }
}
