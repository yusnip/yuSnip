using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;

namespace ScreenCaptureTool.Controls
{
    /// <summary>
    /// AI 预设提示词 @ 提及下拉浮层（可复用控件）：
    /// 挂接到任意提问输入框，输入 @（后接命令名实时筛选）在输入框下方弹出预设列表，
    /// 支持 ↑↓ 高亮移动、Enter/Tab 选中、Esc 关闭与鼠标点击；选中后把 @ 词元整体替换为预设内容。
    /// 使用方：AiPromptInputWindow（前置提问弹窗）、AiVisionResultWindow（识图结果追问框）。
    /// </summary>
    public sealed class AiPresetMentionPopup
    {
        // 条目实测高 ≈46DIP（标题行 12.5px + 摘要行 11.5px + 上下 padding 12）、
        // 头部 ≈26DIP（11px 行 + Margin 6/4）、外层 Border 上下 padding 合计 12DIP。
        // 这四个数只用于给浮层算"兜底最小高度"：布局正常时内容自会量得更准，兜底只在
        // 内容被量成 0（或宿主被压扁）时接管，避免条目被裁成"只剩标题和半个字"。
        private const double ItemMinHeight = 46.0;
        private const double ItemSpacing = 2.0;
        private const double HeaderHeightEstimate = 26.0;
        private const double BorderPaddingEstimate = 12.0;
        private const double MaxListHeight = 260.0;

        private readonly bool _workspace;
        private readonly Window _owner;
        private readonly TextBox _input;
        private readonly Popup _popup;
        private readonly StackPanel _listPanel;
        private readonly ScrollViewer _listScroll;

        private List<AiPromptPreset> _presets = new();
        private List<AiPromptPreset> _filtered = new();
        private readonly List<Button> _itemButtons = new();
        private int _highlightIndex;
        private int _atTokenStart = -1;

        public bool IsOpen => _popup.IsOpen;

        public AiPresetMentionPopup(Window owner, TextBox input)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _workspace = owner.TryFindResource("SctWorkspaceAiScope", owner.ActualThemeVariant, out var scope) && scope is true;

            _listPanel = new StackPanel { Spacing = ItemSpacing };

            var header = new TextBlock
            {
                Text = "预设提示词（↑↓ 选择，Enter 确认，Esc 关闭）",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(8, 6, 8, 4)
            };
            header.Bind(TextBlock.ForegroundProperty, owner.GetResourceObservable("SctCardTitleBrush"));

            _popup = new Popup
            {
                IsLightDismissEnabled = false,
                Placement = PlacementMode.Bottom,
                PlacementTarget = input,
                // Avalonia 默认值是 FlipX|FlipY|SlideX|SlideY|ResizeX|ResizeY，其中 ResizeY 的含义
                // 是"把弹窗垂直改小，直到它完全装进可用空间"。外层 Border 是 ClipToBounds=true，
                // 被改小的部分会整块消失（既不滚动也不压缩），表现就是"条目只剩标题和半个字"。
                // 去掉 ResizeX/ResizeY：空间不够时改为翻转（向上展开）与滑动，尺寸始终由内容决定。
                PlacementConstraintAdjustment =
                    PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.FlipY |
                    PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY,
                MinWidth = 360,
                MaxWidth = 480,
                HorizontalOffset = 0,
                VerticalOffset = 4,
                ZIndex = 100,
                Child = new Border
                {
                    Background = GetBrush("SctCardBrush") ?? Brushes.White,
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = GetBrush("SctBorderBrush") ?? Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    BoxShadow = BoxShadows.Parse("0 8 24 0 #1E000000"),
                    Padding = new Thickness(6),
                    ClipToBounds = true,
                    Child = new StackPanel { Spacing = 2 }
                }
            };

            // 列表内容容器（头部 + 滚动列表）。176px 只放得下 4 条，预设一多就必须盲滚，
            // 与"下拉菜单显示不全"是同一类观感；放到 260px（约 6 条）后常用预设基本一眼看全。
            _listScroll = new ScrollViewer { MaxHeight = MaxListHeight, Content = _listPanel };
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(header);
            content.Children.Add(_listScroll);
            if (_workspace && _popup.Child is Border original)
            {
                _popup.Child = new ContinuousBorder
                {
                    Background = original.Background, BorderBrush = original.BorderBrush,
                    BorderThickness = original.BorderThickness, Padding = original.Padding,
                    CornerRadius = owner.TryFindResource("SctRadiusControl", owner.ActualThemeVariant, out var radius) && radius is CornerRadius r ? r : default,
                    BoxShadow = owner.TryFindResource("SctAiMentionShadow", owner.ActualThemeVariant, out var shadow) && shadow is BoxShadows shadows ? shadows : default
                };
            }
            ((Decorator)_popup.Child!).Child = content;

            // 浮层挂到输入框的可视父级（OverlayPopupHost 之外的普通视觉树亦可）
            var host = input.Parent as Panel ?? FindRootPanel(owner);
            host?.Children.Add(_popup);

            _input.TextChanged += (_, _) => Update();
        }

        /// <summary>从设置重新加载预设列表（设置保存后调用）。</summary>
        public void RefreshPresets()
        {
            var settings = AppSettings.Current;
            settings.EnsureDefaultAiPrompts();
            _presets = (settings.AiPromptPresets ?? new List<AiPromptPreset>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Content))
                .ToList();
            if (!IsOpen) return;
            Update();
        }

        /// <summary>
        /// 处理输入框按键。返回 true 表示已消费（调用方不应再处理，如 Enter 发送）。
        /// </summary>
        public bool HandleKeyDown(KeyEventArgs e)
        {
            if (!_popup.IsOpen) return false;

            if (e.Key == Key.Down) { MoveHighlight(1); return true; }
            if (e.Key == Key.Up) { MoveHighlight(-1); return true; }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (_filtered.Count > 0) ApplyPicked(_filtered[_highlightIndex]);
                return true;
            }
            if (e.Key == Key.Escape) { Hide(); return true; }
            return false;
        }

        public void Hide()
        {
            _popup.IsOpen = false;
            _filtered.Clear();
            _itemButtons.Clear();
            _atTokenStart = -1;
        }

        /// <summary>检测光标处是否处于合法的 @ 技能命令词元（@ 前不能是字母/数字，词元内不能有空白），按词元过滤并展开/收起浮层。</summary>
        private void Update()
        {
            if (_presets.Count == 0 || _input.Text == null && !_popup.IsOpen) { Hide(); return; }

            string text = _input.Text ?? string.Empty;
            int caret = Math.Clamp(_input.CaretIndex, 0, text.Length);

            int at = -1;
            for (int i = caret - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '@') { at = i; break; }
                if (char.IsWhiteSpace(c)) break;
            }

            bool tokenValid = at >= 0 && (at == 0 || !char.IsLetterOrDigit(text[at - 1]));
            if (!tokenValid) { Hide(); return; }

            string token = text.Substring(at + 1, caret - at - 1);
            var filtered = _presets
                .Where(p => string.IsNullOrEmpty(token)
                            || (p.Name ?? "").Contains(token, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filtered.Count == 0) { Hide(); return; }

            _atTokenStart = at;
            Show(filtered);
        }

        private void Show(List<AiPromptPreset> presets)
        {
            _filtered = presets;
            _highlightIndex = 0;
            _itemButtons.Clear();
            _listPanel.Children.Clear();

            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                var btn = BuildItem(preset);
                if (i == 0) btn.Classes.Add("hl");
                btn.Click += (_, _) => ApplyPicked(preset);
                _itemButtons.Add(btn);
                _listPanel.Children.Add(btn);
            }

            // 按条目数给"列表区"和"宿主窗口"各钉一个高度下限。
            // 原先只有上限（MaxHeight），于是任何一种把高度量小的情况都没有东西托底：
            // ① 首帧测量把 ScrollViewer 量成 0 高 → 条目被裁成一条；
            // ② 定位器按可用空间压扁 PopupRoot → 整个浮层只剩一条。
            // 钉下限即可免疫：内容量得更大时走内容，量小了由这两处 MinHeight 接管。
            // 注意列表高度要和条目高度下限同源（N × 条目高 + (N-1) × 间距），
            // 否则会出现"列表比内容矮 2px"这种刚好把最后一行边角切掉的缝隙。
            double listHeight = Math.Min(
                presets.Count * ItemMinHeight + Math.Max(0, presets.Count - 1) * ItemSpacing,
                MaxListHeight);
            _listScroll.MinHeight = listHeight;
            _popup.MinHeight = HeaderHeightEstimate + listHeight + BorderPaddingEstimate;

            _popup.IsOpen = true;
            // 展开后把高亮条（恒为第 0 条）拉进可视区：筛选词变化会整批换掉条目，而 ScrollViewer 的
            // 滚动偏移会停在上一批的位置，高亮条就可能落在可视区之外。等下拉宿主走完首帧布局再拉。
            Dispatcher.UIThread.Post(BringHighlightIntoView, DispatcherPriority.Loaded);
            // 诊断：首帧布局后把"期望尺寸 / 实际尺寸"记一笔。浮层若再出现显示不全，
            // 对照这几个数就能立刻区分是"内容没量准"还是"窗口被外部压扁"。
            Dispatcher.UIThread.Post(LogPopupMetrics, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(LogPopupMetrics, DispatcherPriority.Background);
        }

        /// <summary>把当前高亮条目滚进可视区（列表重排后修正滚动偏移用）。</summary>
        private void BringHighlightIntoView()
        {
            if (!_popup.IsOpen) return;
            if (_highlightIndex < 0 || _highlightIndex >= _itemButtons.Count) return;
            try
            {
                _itemButtons[_highlightIndex].BringIntoView();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiPresetMentionPopup] 高亮条目滚动定位异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 记录浮层首帧布局后的实际尺寸，用于诊断"显示不全"。
        /// 期望值（DesiredSize）与实际分配（Bounds）不一致 ⇒ 窗口被外部压扁；
        /// 两者一致但都偏小 ⇒ 是内容测量本身的问题。二者的修法完全不同，所以先记数再判断。
        /// </summary>
        private void LogPopupMetrics()
        {
            if (!_popup.IsOpen) return;
            try
            {
                var border = _popup.Child;
                double itemHeight = _itemButtons.Count > 0 ? _itemButtons[0].Bounds.Height : 0;
                AppLogger.Info(
                    "[AiPresetMentionPopup] 尺寸诊断 期望高=" + (_popup.MinHeight.ToString("F0"))
                    + " 条目数=" + _itemButtons.Count
                    + " 外层期望=" + (border != null ? border.DesiredSize.Height.ToString("F1") : "-")
                    + " 外层实际=" + (border != null ? border.Bounds.Height.ToString("F1") : "-")
                    + " 列表期望=" + _listScroll.DesiredSize.Height.ToString("F1")
                    + " 列表实际=" + _listScroll.Bounds.Height.ToString("F1")
                    + " 内容期望=" + _listPanel.DesiredSize.Height.ToString("F1")
                    + " 内容实际=" + _listPanel.Bounds.Height.ToString("F1")
                    + " 首条实际=" + itemHeight.ToString("F1"));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[AiPresetMentionPopup] 尺寸诊断异常: " + ex.Message);
            }
        }

        /// <summary>构建浮层单条条目：@命令名（品牌紫）+ 默认徽标 + 内容首行摘要。</summary>
        private Button BuildItem(AiPromptPreset preset)
        {
            var titleBrush = GetBrush("SctCardTitleBrush");
            var mutedBrush = GetBrush("SctTextMutedBrush");
            var hoverBrush = GetBrush("SctCardFillHoverBrush");
            // 走主题 Token 而非写死色值：两个宿主窗口都在私有主题字典里把 SctAccentBrush
            // 覆盖成品牌紫，取色结果与原硬编码一致，但换色时徽标会跟着走而不是单独漂移
            var accent = GetBrush("SctAccentBrush");

            var stack = new StackPanel { Spacing = 2 };

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text = "@" + preset.Name,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (titleBrush != null) nameText.Foreground = titleBrush;
            titleRow.Children.Add(nameText);

            if (preset.IsDefault)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(7, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "默认",
                        FontSize = 10,
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                if (accent != null) badge.Background = accent;
                titleRow.Children.Add(badge);
            }
            stack.Children.Add(titleRow);

            string preview = (preset.Content ?? string.Empty).ReplaceLineEndings(" ").Trim();
            if (preview.Length > 42) preview = preview.Substring(0, 42) + "…";
            var previewText = new TextBlock
            {
                Text = preview,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (mutedBrush != null) previewText.Foreground = mutedBrush;
            stack.Children.Add(previewText);

            var btn = new Button
            {
                Content = stack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // 条目自带高度下限：无论被放进什么高度受限的容器，都不会被压成"半行"
                MinHeight = ItemMinHeight
            };
            btn.Classes.Add("aiPresetMentionItem");
            return btn;
        }

        private void MoveHighlight(int delta)
        {
            if (_itemButtons.Count == 0) return;
            _highlightIndex = (_highlightIndex + delta + _itemButtons.Count) % _itemButtons.Count;

            for (int i = 0; i < _itemButtons.Count; i++)
            {
                var btn = _itemButtons[i];
                bool hl = i == _highlightIndex;
                if (hl && !btn.Classes.Contains("hl")) btn.Classes.Add("hl");
                else if (!hl) btn.Classes.Remove("hl");
            }
            _itemButtons[_highlightIndex].BringIntoView();
        }

        /// <summary>选中预设：把输入框中光标处的 @ 词元整体替换为该预设的提示词内容。</summary>
        private void ApplyPicked(AiPromptPreset preset)
        {
            string text = _input.Text ?? string.Empty;
            int caret = Math.Clamp(_input.CaretIndex, 0, text.Length);
            int start = _atTokenStart >= 0 ? Math.Min(_atTokenStart, caret) : caret;
            string content = (preset.Content ?? string.Empty).Trim();

            string newText = text.Remove(start, caret - start).Insert(start, content);
            _input.Text = newText;
            int newCaret = start + content.Length;
            _input.CaretIndex = newCaret;
            _input.SelectionStart = newCaret;
            _input.SelectionEnd = newCaret;

            Hide();
            _input.Focus();
        }

        private IBrush? GetBrush(string key)
        {
            var theme = _owner.RequestedThemeVariant ?? _owner.ActualThemeVariant;
            return _owner.TryFindResource(key, theme, out var value) ? value as IBrush : null;
        }

        private static Panel? FindRootPanel(Visual root)
        {
            // 从窗口内容向下找第一个 Panel（浮层宿主）
            if (root is Panel p) return p;
            foreach (var child in root.GetVisualChildren())
            {
                var found = FindRootPanel(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
