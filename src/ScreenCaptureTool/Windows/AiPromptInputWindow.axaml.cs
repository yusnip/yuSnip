using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows
{
    /// <summary>
    /// 极简 AI 识图提问弹窗：支持输入提示词、切换大模型、位置跟随鼠标/屏幕居中、失焦自动关闭及 Esc 取消。
    /// </summary>
    public partial class AiPromptInputWindow : Window
    {
        public string? ResultPrompt { get; private set; }
        public bool IsConfirmed { get; private set; }
        public string? SelectedModel { get; private set; }
        public string? SelectedConfigId { get; private set; }

        private DispatcherTimer? _shimmerTimer;
        private long _shimmerStartTick;
        private bool _isClosingOrClosed;
        private bool _deactivatedPendingClose;

        // @ 预设提示词提及下拉（复用控件，与识图结果窗口共用同一交互）
        private AiPresetMentionPopup? _mentionPopup;

        public AiPromptInputWindow(string? initialPrompt = null)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(initialPrompt))
            {
                TxtPromptInput.Text = initialPrompt;
                TxtPromptInput.CaretIndex = initialPrompt.Length;
            }

            InitModelList();
            ConfigureComboBoxContainerStyles();
            _mentionPopup = new AiPresetMentionPopup(this, TxtPromptInput);
            _mentionPopup.RefreshPresets();

            // 捕获键盘按键（Tunnel 策略先于 TextBox 默认行为处理：Enter 发送、Ctrl+Enter 换行、Esc 退出）
            AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
            TxtPromptInput.AddHandler(KeyDownEvent, OnPromptInputKeyDown, RoutingStrategies.Tunnel);

            Opened += OnWindowOpened;
            Closed += OnWindowClosed;
        }

        private void OnWindowOpened(object? sender, EventArgs e)
        {
            PositionWindow();
            TxtPromptInput.Focus();
            StartShimmerAnimation();

            // 设置 Win32 WS_EX_TOOLWINDOW 扩展样式，避免窗口出现在系统 Alt+Tab 列表中
            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    IntPtr oldStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
                    long style = oldStyle.ToInt64() | NativeMethods.WS_EX_TOOLWINDOW;
                    NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("[AiPromptInputWindow] 设置 WS_EX_TOOLWINDOW 异常: " + ex.Message);
                }
            }

            // 监听窗口激活与失去焦点（用户点击窗口外部立即关闭）
            Deactivated += OnWindowDeactivated;
            Activated += OnWindowActivated;
            if (CmbModelSelect != null)
            {
                CmbModelSelect.DropDownClosed += OnModelSelectDropDownClosed;
                CmbModelSelect.DropDownOpened += OnModelSelectDropDownOpened;
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            StopDropDownScrollTimer();
            Deactivated -= OnWindowDeactivated;
            Activated -= OnWindowActivated;
            if (CmbModelSelect != null)
            {
                CmbModelSelect.DropDownClosed -= OnModelSelectDropDownClosed;
                CmbModelSelect.DropDownOpened -= OnModelSelectDropDownOpened;
            }
            _mentionPopup?.Hide();

            _shimmerTimer?.Stop();
            _shimmerTimer = null;
        }

        private void OnWindowActivated(object? sender, EventArgs e)
        {
            _deactivatedPendingClose = false;
        }

        /// <summary>
        /// 失去焦点时自动关闭窗口（若正在操作 ComboBox 下拉列表则延迟到下拉列表关闭后检查）。
        /// </summary>
        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            if (_isClosingOrClosed || IsConfirmed) return;

            // 当 ComboBox 的下拉菜单（Popup）打开时，Avalonia 会触发 Window Deactivated
            // 标记待处理关闭状态，避免在弹窗展开时误杀窗口，切出应用时等待 DropDownClosed 再结算
            if (CmbModelSelect != null && CmbModelSelect.IsDropDownOpen)
            {
                _deactivatedPendingClose = true;
                return;
            }

            CancelAndClose();
        }

        private void OnModelSelectDropDownClosed(object? sender, EventArgs e)
        {
            if (_isClosingOrClosed || IsConfirmed) return;

            // 下拉菜单收起时，若之前曾触发失焦且当前窗口非激活状态，说明用户已切出应用，立即关闭
            if (_deactivatedPendingClose && !IsActive)
            {
                CancelAndClose();
            }
        }

        private void OnModelSelectDropDownOpened(object? sender, EventArgs e)
        {
            ScheduleScrollToSelectedModel();
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
                AppLogger.Warn("[AiPromptInputWindow] 下拉选中项滚动定位异常: " + ex.Message);
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

        /// <summary>
        /// 根据用户设置（跟随鼠标 / 屏幕居中）定位窗口，并严密限制在屏幕工作区内部以防溢出。
        /// </summary>
        private void PositionWindow()
        {
            try
            {
                var settings = AppSettings.Current;
                string posMode = AppSettings.NormalizeAiPromptWindowPosition(settings.AiPromptWindowPosition);

                PixelPoint cursorPt = new PixelPoint(0, 0);
                bool hasCursor = NativeMethods.GetCursorPos(out var pt);
                if (hasCursor)
                {
                    cursorPt = new PixelPoint(pt.X, pt.Y);
                }

                var screen = (hasCursor ? Screens.ScreenFromPoint(cursorPt) : null)
                             ?? Screens.Primary
                             ?? Screens.All.FirstOrDefault();

                PixelRect area;
                if (screen != null)
                {
                    area = screen.WorkingArea;
                    if (area.Width <= 0 || area.Height <= 0)
                    {
                        area = screen.Bounds;
                    }
                }
                else
                {
                    area = new PixelRect(0, 0, 1920, 1080);
                }

                double scale = (screen != null && screen.Scaling > 0)
                    ? screen.Scaling
                    : (DesktopScaling > 0 ? DesktopScaling : 1.0);

                int pixelWidth = (int)Math.Round((Bounds.Width > 0 ? Bounds.Width : Width) * scale);
                int pixelHeight = (int)Math.Round((Bounds.Height > 0 ? Bounds.Height : 160) * scale);
                if (pixelWidth <= 0) pixelWidth = (int)Math.Round(528 * scale);
                if (pixelHeight <= 0) pixelHeight = (int)Math.Round(160 * scale);

                int x;
                int y;

                if (posMode == "CenterScreen")
                {
                    x = area.X + Math.Max(0, (area.Width - pixelWidth) / 2);
                    y = area.Y + Math.Max(0, (area.Height - pixelHeight) / 2);
                }
                else // FollowMouse
                {
                    if (hasCursor)
                    {
                        // 窗口水平居中于鼠标，垂直偏上对准输入区域，保证视觉舒服且鼠标落在卡片上
                        x = pt.X - pixelWidth / 2;
                        y = pt.Y - pixelHeight / 3;
                    }
                    else
                    {
                        x = area.X + Math.Max(0, (area.Width - pixelWidth) / 2);
                        y = area.Y + Math.Max(0, (area.Height - pixelHeight) / 2);
                    }
                }

                // 严格限制在当前显示器 WorkingArea 内，预留 12px 物理安全边距
                // 底部预留空间按"浮层能从输入框下方完整展开"取：模型下拉 MaxDropDownHeight 已提到 320，
                // @ 预设提示词浮层最高约 300px（头部 26 + 列表 260 + 内边距 12 + 偏移 4）。
                // 原先只留 170px，两者都超出，位置器只能把弹出层往上顶或按可用空间压缩 —— 观感就是"显示不全"。
                const int margin = 12;
                int minX = area.X + margin;
                int maxX = Math.Max(minX, area.X + area.Width - pixelWidth - margin);
                int minY = area.Y + margin;
                int reservedBottomSpace = (int)Math.Round(340 * scale);
                int maxY = Math.Max(minY, area.Y + area.Height - pixelHeight - reservedBottomSpace);

                x = Math.Clamp(x, minX, maxX);
                y = Math.Clamp(y, minY, maxY);

                Position = new PixelPoint(x, y);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiPromptInputWindow] PositionWindow 定位异常: " + ex.Message);
            }
        }

        private void ConfigureComboBoxContainerStyles()
        {
            if (CmbModelSelect == null) return;

            CmbModelSelect.ContainerPrepared += (_, e) =>
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
        }

        private void InitModelList()
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

            string target = (settings.AiModel ?? "").Trim();
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
                SelectedModel = selected.ModelId;
                SelectedConfigId = selected.ConfigId;
            }
        }

        private TranslateTransform? _shimmerTransform;

        private void StartShimmerAnimation()
        {
            if (ShimmerSweepOverlay == null) return;

            if (_shimmerTransform == null)
            {
                _shimmerTransform = new TranslateTransform(-530, 0);
                ShimmerSweepOverlay.RenderTransform = _shimmerTransform;
            }

            ShimmerSweepOverlay.Opacity = 0.0;
            ShimmerSweepOverlay.IsVisible = true;
            _shimmerStartTick = Environment.TickCount64;

            _shimmerTimer?.Stop();
            _shimmerTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _shimmerTimer.Tick += (_, _) =>
            {
                double elapsed = Math.Max(0, Environment.TickCount64 - _shimmerStartTick);
                double progress = Math.Clamp(elapsed / 750.0, 0.0, 1.0);
                double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);

                double cardWidth = Math.Max(528.0, Bounds.Width);
                _shimmerTransform.X = -cardWidth + (cardWidth * 2.2) * eased;
                ShimmerSweepOverlay.Opacity = Math.Sin(progress * Math.PI) * 0.45;

                if (progress >= 1.0)
                {
                    _shimmerTimer.Stop();
                    ShimmerSweepOverlay.IsVisible = false;
                }
            };
            _shimmerTimer.Start();
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                // @ 预设下拉打开时，Esc 仅关闭下拉而不退出窗口
                if (_mentionPopup != null && _mentionPopup.HandleKeyDown(e))
                {
                    return;
                }
                CancelAndClose();
            }
        }

        private void OnPromptInputKeyDown(object? sender, KeyEventArgs e)
        {
            // @ 预设下拉打开时：↑↓ 移动高亮、Enter/Tab 选中、Esc 关闭，均优先于发送/退出
            if (_mentionPopup != null && _mentionPopup.HandleKeyDown(e))
            {
                e.Handled = true;
                return;
            }

            // 粘贴：框架那条 Ctrl+V 键位在本窗不生效，这里显式接管。置 Handled 是必需的——
            // 否则本窗这一次与 TextBox 内部那一次会叠成双份粘贴。
            if (AiPromptPaste.IsPasteGesture(e))
            {
                e.Handled = true;
                _ = AiPromptPaste.PasteWithFeedbackAsync(TxtPromptInput);
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelAndClose();
            }
            else if (e.Key == Key.Enter)
            {
                if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0)
                {
                    e.Handled = true;
                    InsertNewlineInPromptInput();
                }
                else
                {
                    e.Handled = true;
                    ConfirmAndClose();
                }
            }
        }

        private void InsertNewlineInPromptInput()
        {
            if (TxtPromptInput == null) return;

            try
            {
                int start = Math.Min(TxtPromptInput.SelectionStart, TxtPromptInput.SelectionEnd);
                int end = Math.Max(TxtPromptInput.SelectionStart, TxtPromptInput.SelectionEnd);
                string text = TxtPromptInput.Text ?? string.Empty;

                if (start < 0) start = text.Length;
                if (end < 0) end = text.Length;
                start = Math.Clamp(start, 0, text.Length);
                end = Math.Clamp(end, start, text.Length);

                string newText = text.Remove(start, end - start).Insert(start, "\n");
                TxtPromptInput.Text = newText;
                int newCaret = start + 1;
                TxtPromptInput.CaretIndex = newCaret;
                TxtPromptInput.SelectionStart = newCaret;
                TxtPromptInput.SelectionEnd = newCaret;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiPromptInputWindow] 换行插入异常: " + ex.Message);
            }
        }

        private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Handled) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // 向上遍历可视化树：若点击发生在输入框、下拉框、按钮等交互控件内部，绝不拦截拖拽
                if (!IsInteractiveControl(e.Source as Visual))
                {
                    try { BeginMoveDrag(e); } catch { }
                }
            }
        }

        /// <summary>
        /// 检查指定 Visual 是否位于交互控件内部（TextBox, ComboBox, Button, ComboBoxItem 等）
        /// </summary>
        private static bool IsInteractiveControl(Visual? visual)
        {
            if (visual == null) return false;
            return visual.FindAncestorOfType<TextBox>(includeSelf: true) != null
                || visual.FindAncestorOfType<ComboBox>(includeSelf: true) != null
                || visual.FindAncestorOfType<Button>(includeSelf: true) != null
                || visual.FindAncestorOfType<ComboBoxItem>(includeSelf: true) != null;
        }

        private void OnModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (CmbModelSelect?.SelectedItem is AiModelDropDownItem item && item.IsModel && !string.IsNullOrWhiteSpace(item.ModelId))
            {
                SelectedModel = item.ModelId.Trim();
                SelectedConfigId = item.ConfigId;

                var settings = AppSettings.Current;
                settings.AiModel = SelectedModel;
                if (!string.IsNullOrWhiteSpace(SelectedConfigId) && SelectedConfigId != "builtin")
                {
                    settings.AiActiveConfigId = SelectedConfigId;
                }
            }
        }

        private void OnSendPromptClick(object? sender, RoutedEventArgs e)
        {
            ConfirmAndClose();
        }

        private void ConfirmAndClose()
        {
            if (_isClosingOrClosed) return;
            _isClosingOrClosed = true;

            ResultPrompt = (TxtPromptInput.Text ?? string.Empty).Trim();
            IsConfirmed = true;

            try
            {
                var settings = AppSettings.Current;
                if (!string.IsNullOrWhiteSpace(SelectedModel))
                {
                    settings.AiModel = SelectedModel;
                }
                if (!string.IsNullOrWhiteSpace(SelectedConfigId) && SelectedConfigId != "builtin")
                {
                    settings.AiActiveConfigId = SelectedConfigId;
                }
                settings.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiPromptInputWindow] 同步模型设置失败: {ex.Message}");
            }

            Close();
        }

        private void CancelAndClose()
        {
            if (_isClosingOrClosed) return;
            _isClosingOrClosed = true;

            IsConfirmed = false;
            Close();
        }
    }
}
