using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Controls
{
    /// <summary>
    /// 自研极轻量级原生 Markdown 富文本控件（块级布局）。
    /// 采用「块级渲染」模型：标题/段落/列表/引用/代码块/表格/分割线各自渲染为独立块级子控件，
    /// 观感对齐 ChatGPT / Gemini / Claude 等主流 AI 对话渲染。
    /// 每个文本块内部使用原生 SelectableTextBlock，保留划词选区与 Ctrl+C 自由复制；
    /// 流式渲染时仅重建发生变化的尾部块，已完成块保持稳定（不闪跳、不丢选区）。
    /// 零第三方依赖、100% 兼容 NativeAOT。
    /// </summary>
    public class SelectableMarkdownTextBlock : StackPanel
    {
        public static readonly StyledProperty<string?> MarkdownProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, string?>(nameof(Markdown), defaultValue: null);

        public static readonly StyledProperty<double> BaseFontSizeProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, double>(nameof(BaseFontSize), defaultValue: 14.0);

        public static readonly StyledProperty<FontFamily> CodeFontFamilyProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, FontFamily>(
                nameof(CodeFontFamily),
                defaultValue: new FontFamily("Cascadia Code, Consolas, Courier New, monospace"));

        public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, IBrush?>(
                nameof(SelectionBrush),
                defaultValue: null);

        /// <summary>
        /// 紧凑排版模式（用于「复制生成图片」）：统一行高与字体层级、
        /// 去掉各块的参差边距与代码块工具条，输出整齐一致的打印级排版。
        /// </summary>
        public static readonly StyledProperty<bool> CompactLayoutProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, bool>(nameof(CompactLayout), defaultValue: false);

        /// <summary>
        /// 正文（段落）行高倍率，0＝沿用本控件既有档位（紧凑 1.6 / 常规 1.55）。
        /// 2026-09-30 为 AI 快答窗加：设计稿②「正文 12.5/1.7」要求 1.7 倍行高，而 1.55/1.6
        /// 是识图结果窗、OCR 窗、设置窗、更新窗、贴图 Markdown 共同依赖的既有观感，
        /// 🔴 不得直接改那两个常量——一律由宿主按需 opt-in，默认值保持全仓现状不变。
        /// </summary>
        public static readonly StyledProperty<double> LineHeightRatioProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, double>(nameof(LineHeightRatio), defaultValue: 0.0);

        /// <summary>仅已迁移的屏幕正文启用；成片、贴图等消费方默认保持既有呈现。</summary>
        public static readonly StyledProperty<bool> UseWorkspaceAppearanceProperty =
            AvaloniaProperty.Register<SelectableMarkdownTextBlock, bool>(nameof(UseWorkspaceAppearance), defaultValue: false);

        public bool UseWorkspaceAppearance
        {
            get => GetValue(UseWorkspaceAppearanceProperty);
            set => SetValue(UseWorkspaceAppearanceProperty, value);
        }

        private readonly List<string> _signatures = new List<string>();

        /// <summary>
        /// 点击超链接时触发（由宿主决定如何打开 URL）。
        /// </summary>
        public event Action<string>? LinkClicked;

        private IPointer? _pressPointer;
        private Point _linkPressPos;

        static SelectableMarkdownTextBlock()
        {
            MarkdownProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((x, e) => x.OnMarkdownChanged(e));
            BaseFontSizeProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((x, e) => x.RebuildAll());
            SelectionBrushProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((x, e) => x.ApplySelectionBrushToAll());
            UseWorkspaceAppearanceProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((x, e) => x.RebuildAll());
            CompactLayoutProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((x, e) => x.RebuildAll());
            LineHeightRatioProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((x, e) => x.RebuildAll());
        }

        private CrossBlockSelectionController? _selectionController;

        public SelectableMarkdownTextBlock()
        {
            Orientation = Orientation.Vertical;
            Spacing = 6;
            ActualThemeVariantChanged += (s, e) => RebuildAll();

            AddHandler(InputElement.PointerPressedEvent, OnLinkPointerPressed, RoutingStrategies.Bubble, true);
            AddHandler(InputElement.PointerReleasedEvent, OnLinkPointerReleased, RoutingStrategies.Bubble, true);

            _selectionController = new CrossBlockSelectionController(this, () => EnumerateTextBlocks(this));
        }

        public string? Markdown
        {
            get => GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        /// <summary>
        /// 文本内容（Markdown 属性的便捷别名）。
        /// </summary>
        public string? Text
        {
            get => Markdown;
            set => Markdown = value;
        }

        public double BaseFontSize
        {
            get => GetValue(BaseFontSizeProperty);
            set => SetValue(BaseFontSizeProperty, value);
        }

        public FontFamily CodeFontFamily
        {
            get => GetValue(CodeFontFamilyProperty);
            set => SetValue(CodeFontFamilyProperty, value);
        }

        public IBrush? SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        public bool CompactLayout
        {
            get => GetValue(CompactLayoutProperty);
            set => SetValue(CompactLayoutProperty, value);
        }

        public double LineHeightRatio
        {
            get => GetValue(LineHeightRatioProperty);
            set => SetValue(LineHeightRatioProperty, value);
        }

        private Avalonia.Styling.ThemeVariant? _forcedThemeVariant;

        /// <summary>
        /// 强制按指定主题档着色（null＝跟随环境主题，即原有行为）。
        /// 贴图窗口的画布底色必须在 <c>Show()</c> 之前就定下来，而那一刻本控件的
        /// <c>ActualThemeVariant</c> 还没解析出档位，环境着色会退到应用档——于是
        /// 「剪贴板 HTML 自带白底」配「应用深色档近白字」＝白底白字。
        /// 底色既已显式决定，字色就必须显式跟同一档，不能再问环境。
        /// </summary>
        public Avalonia.Styling.ThemeVariant? ForcedThemeVariant
        {
            get => _forcedThemeVariant;
            set
            {
                if (_forcedThemeVariant == value) return;
                _forcedThemeVariant = value;
                RebuildAll();
            }
        }

        /// <summary>实际用于取色的档位：显式指定优先，否则用控件解析到的环境档位。</summary>
        private Avalonia.Styling.ThemeVariant? EffectiveThemeVariant => _forcedThemeVariant ?? ActualThemeVariant;

        /// <summary>
        /// 聚合当前所有文本块中已选中的文字（兼容贴图窗口的复制选中文本）。
        /// </summary>
        public string SelectedText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var stb in EnumerateTextBlocks(this))
                {
                    if (!string.IsNullOrEmpty(stb.SelectedText))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(stb.SelectedText);
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 清除所有文本块的选区（与单块 SelectableTextBlock.ClearSelection 语义一致，
        /// 供贴图窗口在取消选择时统一调用）。
        /// </summary>
        public void ClearSelection()
        {
            foreach (var stb in EnumerateTextBlocks(this))
            {
                stb.ClearSelection();
            }
        }

        private void OnMarkdownChanged(AvaloniaPropertyChangedEventArgs e)
        {
            UpdateInlines(e.NewValue as string);
        }

        private void OnFormattingChanged()
        {
            UpdateInlines(Markdown);
        }

        private bool IsDarkTheme()
        {
            var variant = EffectiveThemeVariant;
            if (variant == Avalonia.Styling.ThemeVariant.Dark) return true;
            if (variant == Avalonia.Styling.ThemeVariant.Light) return false;
            if (Application.Current != null && Application.Current.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark) return true;
            return false;
        }

        private void RebuildAll()
        {
            _signatures.Clear();
            Children.Clear();
            UpdateInlines(Markdown);
        }

        /// <summary>
        /// 增量解析并更新块级子控件：已完成块保持稳定复用，仅重建发生变化的尾部。
        /// </summary>
        public void UpdateInlines(string? markdown)
        {
            string md = markdown ?? string.Empty;

            bool isDark = IsDarkTheme();
            var ctx = new MarkdownRenderContext(this, isDark, EffectiveThemeVariant, BaseFontSize > 0 ? BaseFontSize : 14.0, CodeFontFamily, CompactLayout, LineHeightRatio, UseWorkspaceAppearance);

            var blocks = MarkdownInlineEngine.ParseBlocks(md);

            int i = 0;
            while (i < blocks.Count && i < _signatures.Count && i < Children.Count &&
                   string.Equals(blocks[i].Signature, _signatures[i], StringComparison.Ordinal))
            {
                i++;
            }

            while (Children.Count > i)
            {
                Children.RemoveAt(Children.Count - 1);
            }

            for (int k = i; k < blocks.Count; k++)
            {
                Children.Add(MarkdownInlineEngine.CreateBlockControl(blocks[k], ctx, SelectionBrush));
            }

            _signatures.Clear();
            _signatures.AddRange(blocks.Select(b => b.Signature));
        }

        private void OnLinkPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pressPointer = e.Pointer;
            _linkPressPos = e.GetPosition(this);
        }

        private void OnLinkPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_pressPointer == null || !ReferenceEquals(_pressPointer, e.Pointer)) return;
            _pressPointer = null;

            var delta = e.GetPosition(this) - _linkPressPos;
            if (Math.Abs(delta.X) > 8.0 || Math.Abs(delta.Y) > 8.0) return;

            var tb = e.Source as SelectableTextBlock;
            if (tb == null && e.Source is Control src)
            {
                var cur = src;
                while (cur != null && cur != this)
                {
                    if (cur is SelectableTextBlock s) { tb = s; break; }
                    cur = cur.Parent as Control;
                }
            }
            if (tb == null || tb.Tag is not List<MdInlineLink> links || links.Count == 0) return;
            if (tb.TextLayout == null) return;

            var hit = tb.TextLayout.HitTestPoint(e.GetPosition(tb));
            if (!hit.IsInside) return;

            foreach (var link in links)
            {
                if (hit.TextPosition >= link.Start && hit.TextPosition < link.End)
                {
                    if (LinkClicked != null)
                    {
                        LinkClicked.Invoke(link.Url);
                    }
                    else if (!string.IsNullOrWhiteSpace(link.Url) &&
                             (link.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                              link.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = link.Url, UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("SelectableMarkdownTextBlock: 打开超链接失败: " + ex.Message);
                        }
                    }
                    break;
                }
            }
        }

        private void ApplySelectionBrushToAll()
        {
            if (_selectionController != null)
            {
                _selectionController.SelectionBrush = SelectionBrush;
            }
            foreach (var stb in EnumerateTextBlocks(this))
            {
                stb.SelectionBrush = SelectionBrush;
            }
        }

        /// <summary>
        /// 深度遍历本控件可视内容树，取出全部 <see cref="SelectableTextBlock"/>（标题/正文/表格单元/代码块……）。
        /// 公开给贴图侧划词工具栏用：它要按"块 + 块内偏移"记选区与装饰，而块只有在这里才数得清。
        /// ⚠ 顺序＝子节点声明顺序，重建 Markdown 后同一处文字的下标可能变——故外部不得把
        /// 这个下标当稳定身份存，必须连带块内全文一起存（见 StickerWindow 的 TextMark）。
        /// </summary>
        public static IEnumerable<SelectableTextBlock> EnumerateTextBlocks(Control root)
        {
            if (root is SelectableTextBlock stb)
            {
                yield return stb;
            }

            switch (root)
            {
                case Panel panel:
                    foreach (var child in panel.Children)
                    {
                        foreach (var r in EnumerateTextBlocks(child)) yield return r;
                    }
                    break;
                case Decorator decorator when decorator.Child is Control inner:
                    foreach (var r in EnumerateTextBlocks(inner)) yield return r;
                    break;
                case ContentControl content when content.Content is Control inner:
                    foreach (var r in EnumerateTextBlocks(inner)) yield return r;
                    break;
            }
        }
    }

    /// <summary>
    /// Markdown 渲染上下文与主题配色感知
    /// </summary>
    internal class MarkdownRenderContext
    {
        public Control HostControl { get; }
        public bool IsDark { get; }
        public bool Compact { get; }
        public bool Workspace { get; }
        public double BaseFontSize { get; }
        public FontFamily CodeFontFamily { get; }

        /// <summary>宿主指定的正文行高倍率；0＝未指定，走本控件既有档位。</summary>
        public double LineHeightRatio { get; }

        /// <summary>
        /// 段落实际采用的行高倍率：宿主 opt-in 优先，否则沿用既有档位（紧凑 1.6 / 常规 1.55）。
        /// </summary>
        public double ParagraphLineRatio => LineHeightRatio > 0 ? LineHeightRatio : (Compact ? 1.6 : 1.55);

        public IBrush PrimaryTextBrush { get; }
        public IBrush SecondaryTextBrush { get; }
        public IBrush MutedTextBrush { get; }
        public IBrush AccentBrush { get; }
        public IBrush InlineCodeBackground { get; }
        public IBrush CodeBlockBackground { get; }
        public IBrush CodeBlockBorder { get; }
        public IBrush TableBorderBrush { get; }
        public IBrush TableHeaderBackground { get; }
        public IBrush TableAltRowBackground { get; }
        public IBrush ThematicBreakBrush { get; }

        /// <summary>与 <see cref="IsDark"/> 同源的取色档位；null 表示按宿主控件的环境档位取。</summary>
        private readonly Avalonia.Styling.ThemeVariant? _themeVariant;

        public MarkdownRenderContext(Control host, bool isDark, Avalonia.Styling.ThemeVariant? themeVariant, double baseFontSize, FontFamily codeFontFamily, bool compact, double lineHeightRatio = 0, bool workspace = false)
        {
            HostControl = host;
            IsDark = isDark;
            _themeVariant = themeVariant;
            Compact = compact;
            Workspace = workspace;
            BaseFontSize = baseFontSize;
            CodeFontFamily = codeFontFamily;
            LineHeightRatio = lineHeightRatio;

            // 只能查 Sct*Color（分档字典内的色值），不能查 Sct*Brush：后者声明在主题字典之外、
            // 全应用共用一个实例，Color 用 {DynamicResource} 跟随环境档位，传入显式 variant 也拿不到
            // 指定档的值（深色应用里永远返回近白画刷 → 白画布白字）。故这里取色后自建画刷实例。
            PrimaryTextBrush = TryFindThemedBrush("SctTextPrimaryColor") ?? (isDark ? new SolidColorBrush(Color.Parse("#F1F5F9")) : new SolidColorBrush(Color.Parse("#0F172A")));
            SecondaryTextBrush = TryFindThemedBrush("SctTextSecondaryColor") ?? (isDark ? new SolidColorBrush(Color.Parse("#94A3B8")) : new SolidColorBrush(Color.Parse("#475569")));
            MutedTextBrush = TryFindThemedBrush("SctTextMutedColor") ?? (isDark ? new SolidColorBrush(Color.Parse("#64748B")) : new SolidColorBrush(Color.Parse("#94A3B8")));
            AccentBrush = TryFindThemedBrush("SctCardTitleColor") ?? TryFindThemedBrush("SctAccentColor") ?? (isDark ? new SolidColorBrush(Color.Parse("#A08BF8")) : new SolidColorBrush(Color.Parse("#4219F2")));

            InlineCodeBackground = isDark ? new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
            CodeBlockBackground = isDark ? new SolidColorBrush(Color.Parse("#161622")) : new SolidColorBrush(Color.Parse("#F8FAFC"));
            CodeBlockBorder = isDark ? new SolidColorBrush(Color.Parse("#2E2E40")) : new SolidColorBrush(Color.Parse("#E2E8F0"));

            TableBorderBrush = isDark ? new SolidColorBrush(Color.Parse("#2E2E40")) : new SolidColorBrush(Color.Parse("#E2E8F0"));
            TableHeaderBackground = isDark ? new SolidColorBrush(Color.Parse("#232332")) : new SolidColorBrush(Color.Parse("#EEF2F6"));
            TableAltRowBackground = isDark ? new SolidColorBrush(Color.Parse("#1A1A26")) : new SolidColorBrush(Color.Parse("#F9FAFB"));
            ThematicBreakBrush = isDark ? new SolidColorBrush(Color.Parse("#2E2E40")) : new SolidColorBrush(Color.Parse("#E2E8F0"));
            if (Workspace)
            {
                PrimaryTextBrush = TryFindThemedBrush("SctWorkspaceTextColor") ?? PrimaryTextBrush;
                SecondaryTextBrush = TryFindThemedBrush("SctWorkspaceSecondaryColor") ?? SecondaryTextBrush;
                MutedTextBrush = TryFindThemedBrush("SctWorkspaceMutedColor") ?? MutedTextBrush;
                AccentBrush = TryFindThemedBrush("SctWorkspaceBrandTextColor") ?? AccentBrush;
                InlineCodeBackground = TryFindThemedBrush("SctWorkspaceSelectedColor") ?? InlineCodeBackground;
                CodeBlockBackground = TryFindThemedBrush("SctWorkspaceFieldColor") ?? CodeBlockBackground;
                CodeBlockBorder = TryFindThemedBrush("SctWorkspaceBorderColor") ?? CodeBlockBorder;
                TableBorderBrush = CodeBlockBorder;
                TableHeaderBackground = TryFindThemedBrush("SctWorkspaceHoverColor") ?? TableHeaderBackground;
                TableAltRowBackground = TryFindThemedBrush("SctWorkspaceFieldColor") ?? TableAltRowBackground;
                ThematicBreakBrush = TryFindThemedBrush("SctWorkspaceDividerColor") ?? ThematicBreakBrush;
            }
        }

        // 只替换既有表面的绘制层；不改变字号、行高、表格列宽、内外距与默认输出路径。
        public Control Surface(Border original, bool quote = false)
        {
            if (!Workspace) return original;
            var radius = HostControl.TryFindResource("SctRadiusControl", _themeVariant ?? HostControl.ActualThemeVariant, out var value) && value is CornerRadius r ? r : default;
            var child = original.Child;
            original.Child = null;
            return new ContinuousBorder
            {
                Background = original.Background, BorderBrush = original.BorderBrush,
                BorderThickness = original.BorderThickness, Padding = original.Padding, Margin = original.Margin,
                HorizontalAlignment = original.HorizontalAlignment, VerticalAlignment = original.VerticalAlignment,
                CornerRadius = quote ? new CornerRadius(0, radius.TopRight, radius.BottomRight, 0) : radius,
                Child = child
            };
        }

        private SolidColorBrush? TryFindThemedBrush(string colorResourceKey)
        {
            var variant = _themeVariant ?? HostControl.ActualThemeVariant;
            if (HostControl.TryFindResource(colorResourceKey, variant, out var res) && res is Color color)
            {
                return new SolidColorBrush(color);
            }
            if (Application.Current != null &&
                Application.Current.TryFindResource(colorResourceKey, variant, out var appRes) &&
                appRes is Color appColor)
            {
                return new SolidColorBrush(appColor);
            }
            return null;
        }
    }

    /// <summary>
    /// 超轻量级 Markdown 块解析与块级控件生成器
    /// </summary>
    public static class MarkdownInlineEngine
    {
        internal static List<MdBlock> ParseBlocks(string markdown)
        {
            var blocks = new List<MdBlock>();
            if (string.IsNullOrEmpty(markdown)) return blocks;

            string normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            int n = lines.Length;
            int i = 0;

            while (i < n)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    i++;
                    continue;
                }

                // 1. 代码块 (```lang ... ```)
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    string lang = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                    i++;
                    var code = new StringBuilder();
                    while (i < n)
                    {
                        string cur = lines[i];
                        if (cur.Trim().StartsWith("```", StringComparison.Ordinal))
                        {
                            i++;
                            break;
                        }
                        code.AppendLine(cur);
                        i++;
                    }
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Code, Lang = lang, Code = code.ToString().TrimEnd('\n') });
                    continue;
                }

                // 2. 表格 (| a | b |  + 分隔行)
                if (IsTableRow(trimmed) && i + 1 < n && IsTableSeparator(lines[i + 1].Trim()))
                {
                    var tableLines = new List<string> { trimmed, lines[i + 1].Trim() };
                    i += 2;
                    while (i < n && IsTableRow(lines[i].Trim()))
                    {
                        tableLines.Add(lines[i].Trim());
                        i++;
                    }
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Table, TableLines = tableLines });
                    continue;
                }

                // 3. 水平分割线
                if (IsHorizontalRule(trimmed))
                {
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Rule });
                    i++;
                    continue;
                }

                // 4. 标题
                var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (headingMatch.Success)
                {
                    blocks.Add(new MdBlock
                    {
                        Kind = MdBlockKind.Heading,
                        Level = headingMatch.Groups[1].Length,
                        Text = headingMatch.Groups[2].Value.Trim()
                    });
                    i++;
                    continue;
                }

                // 5. 引用块（连续 > 行合并）
                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    var parts = new List<string>();
                    while (i < n)
                    {
                        string t = lines[i].Trim();
                        if (!t.StartsWith(">", StringComparison.Ordinal)) break;
                        parts.Add(Regex.Replace(t, @"^>\s?", ""));
                        i++;
                    }
                    blocks.Add(new MdBlock { Kind = MdBlockKind.Quote, Text = string.Join("\n", parts) });
                    continue;
                }

                // 6. 列表（连续列表行合并为一个块）
                if (IsListLine(line))
                {
                    var items = new List<MdListItem>();
                    while (i < n && IsListLine(lines[i]))
                    {
                        var m = Regex.Match(lines[i], @"^(\s*)([\*\-\+]|\d+\.)\s+(\[[\sxX]\]\s+)?(.+)$");
                        string indentSpaces = m.Groups[1].Value;
                        string bulletOrNumber = m.Groups[2].Value;
                        string taskGroup = m.Groups[3].Value;
                        string itemText = m.Groups[4].Value;

                        int indentLevel = indentSpaces.Length / 2;
                        string bullet;
                        if (!string.IsNullOrEmpty(taskGroup))
                        {
                            bool isChecked = taskGroup.Contains('x') || taskGroup.Contains('X');
                            bullet = isChecked ? "☑ " : "☐ ";
                        }
                        else if (char.IsDigit(bulletOrNumber[0]))
                        {
                            bullet = bulletOrNumber + " ";
                        }
                        else
                        {
                            bullet = indentLevel switch
                            {
                                0 => "• ",
                                1 => "◦ ",
                                _ => "▪ "
                            };
                        }

                        items.Add(new MdListItem { Bullet = bullet, Text = itemText, IndentLevel = indentLevel });
                        i++;
                    }
                    blocks.Add(new MdBlock { Kind = MdBlockKind.List, Items = items });
                    continue;
                }

                // 7. 普通段落（合并到空行或新的块级标记前）
                var para = new StringBuilder();
                while (i < n)
                {
                    string l = lines[i];
                    string t = l.Trim();
                    if (string.IsNullOrWhiteSpace(t)) break;
                    if (t.StartsWith("```", StringComparison.Ordinal)) break;
                    if (IsTableRow(t) && i + 1 < n && IsTableSeparator(lines[i + 1].Trim())) break;
                    if (IsHorizontalRule(t)) break;
                    if (Regex.IsMatch(l, @"^(#{1,6})\s+")) break;
                    if (t.StartsWith(">", StringComparison.Ordinal)) break;
                    if (IsListLine(l)) break;
                    if (para.Length > 0) para.Append(' ');
                    para.Append(l.Trim());
                    i++;
                }
                blocks.Add(new MdBlock { Kind = MdBlockKind.Paragraph, Text = para.ToString() });
            }

            return blocks;
        }

        private static bool IsListLine(string line)
        {
            return Regex.IsMatch(line, @"^(\s*)([\*\-\+]|\d+\.)\s+(\[[\sxX]\]\s+)?(.+)$");
        }

        private static bool IsHorizontalRule(string line)
        {
            if (line.Length < 3) return false;
            string clean = line.Replace(" ", "");
            if (clean.Length < 3) return false;
            return clean.All(c => c == '-') || clean.All(c => c == '*') || clean.All(c => c == '_');
        }

        private static bool IsTableRow(string line)
        {
            return line.StartsWith("|", StringComparison.Ordinal) && line.EndsWith("|", StringComparison.Ordinal);
        }

        private static bool IsTableSeparator(string line)
        {
            if (!IsTableRow(line)) return false;
            string inner = line.Trim('|').Trim();
            string[] cols = inner.Split('|');
            if (cols.Length == 0) return false;
            return cols.All(c =>
            {
                string s = c.Trim().Replace(":", "").Replace("-", "");
                return string.IsNullOrEmpty(s);
            });
        }

        /// <summary>
        /// 根据块类型生成块级子控件
        /// </summary>
        internal static Control CreateBlockControl(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            switch (block.Kind)
            {
                case MdBlockKind.Heading: return CreateHeading(block, ctx, selectionBrush);
                case MdBlockKind.List: return CreateList(block, ctx, selectionBrush);
                case MdBlockKind.Quote: return CreateQuote(block, ctx, selectionBrush);
                case MdBlockKind.Code: return CreateCodeBlock(block, ctx, selectionBrush);
                case MdBlockKind.Table: return CreateTable(block, ctx, selectionBrush);
                case MdBlockKind.Rule: return CreateRule(ctx);
                default: return CreateParagraph(block, ctx, selectionBrush);
            }
        }

        private static IBrush ResolveSelection(IBrush? selectionBrush)
        {
            return selectionBrush ?? new SolidColorBrush(Color.FromArgb(140, 66, 25, 242));
        }

        private static InlineCollection GetInlines(SelectableTextBlock tb)
        {
            var inlines = tb.Inlines;
            if (inlines == null)
            {
                inlines = new InlineCollection();
                tb.Inlines = inlines;
            }
            return inlines;
        }

        private static SelectableTextBlock CreateParagraph(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = ctx.BaseFontSize,
                LineHeight = Math.Round(ctx.BaseFontSize * ctx.ParagraphLineRatio),
                SelectionBrush = ResolveSelection(selectionBrush),
                Margin = ctx.Compact ? new Thickness(0) : new Thickness(0, 0, 0, 4)
            };
            var links = new List<MdInlineLink>();
            ParseInlineFormatting(block.Text, GetInlines(tb), ctx, ctx.BaseFontSize, FontWeight.Normal, FontStyle.Normal, ctx.PrimaryTextBrush, links);
            if (links.Count > 0) tb.Tag = links;
            return tb;
        }

        private static SelectableTextBlock CreateHeading(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            double fontSize = ctx.Compact
                ? (block.Level <= 2 ? Math.Round(ctx.BaseFontSize * 1.1) : ctx.BaseFontSize)
                : block.Level switch
                {
                    1 => ctx.BaseFontSize * 1.42,
                    2 => ctx.BaseFontSize * 1.25,
                    3 => ctx.BaseFontSize * 1.12,
                    4 => ctx.BaseFontSize * 1.05,
                    _ => ctx.BaseFontSize
                };
            FontWeight weight = block.Level <= 3 ? FontWeight.Bold : FontWeight.SemiBold;
            IBrush foreground = !ctx.Compact && block.Level <= 2 ? ctx.AccentBrush : ctx.PrimaryTextBrush;

            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = fontSize,
                LineHeight = ctx.Compact ? Math.Round(fontSize * 1.55) : Math.Round(fontSize * 1.4),
                SelectionBrush = ResolveSelection(selectionBrush),
                Margin = ctx.Compact ? new Thickness(0, 8, 0, 2) : new Thickness(0, 8, 0, 4)
            };
            ParseInlineFormatting(block.Text, GetInlines(tb), ctx, fontSize, weight, FontStyle.Normal, foreground);
            return tb;
        }

        private static StackPanel CreateList(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            var sp = new StackPanel
            {
                Spacing = ctx.Compact ? 4 : 3,
                Margin = ctx.Compact ? new Thickness(0, 2, 0, 4) : new Thickness(0, 0, 0, 4)
            };

            double lineHeight = ctx.Compact ? Math.Round(ctx.BaseFontSize * 1.62) : Math.Round(ctx.BaseFontSize * 1.55);

            foreach (var item in block.Items)
            {
                var itemGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(item.IndentLevel * 18, 1, 0, 1)
                };

                var bulletTb = new TextBlock
                {
                    Text = item.Bullet,
                    FontSize = ctx.BaseFontSize,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ctx.Compact ? ctx.PrimaryTextBrush : ctx.AccentBrush,
                    LineHeight = lineHeight,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(bulletTb, 0);
                itemGrid.Children.Add(bulletTb);

                var tb = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = ctx.BaseFontSize,
                    LineHeight = lineHeight,
                    SelectionBrush = ResolveSelection(selectionBrush),
                    VerticalAlignment = VerticalAlignment.Top
                };
                var inlines = GetInlines(tb);
                var itemLinks = new List<MdInlineLink>();
                ParseInlineFormatting(item.Text, inlines, ctx, ctx.BaseFontSize, FontWeight.Normal, FontStyle.Normal, ctx.PrimaryTextBrush, itemLinks);
                if (itemLinks.Count > 0) tb.Tag = itemLinks;

                Grid.SetColumn(tb, 1);
                itemGrid.Children.Add(tb);

                sp.Children.Add(itemGrid);
            }
            return sp;
        }

        private static Control CreateQuote(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            var border = new Border
            {
                Background = ctx.Workspace ? ctx.InlineCodeBackground : ctx.IsDark ? new SolidColorBrush(Color.FromArgb(32, 160, 139, 248)) : new SolidColorBrush(Color.FromArgb(14, 66, 25, 242)),
                BorderBrush = ctx.AccentBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                Padding = ctx.Compact ? new Thickness(8, 2) : new Thickness(10, 4),
                Margin = ctx.Compact ? new Thickness(0, 2, 0, 4) : new Thickness(0, 2, 0, 6)
            };
            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = ctx.BaseFontSize,
                LineHeight = Math.Round(ctx.BaseFontSize * ctx.ParagraphLineRatio),
                SelectionBrush = ResolveSelection(selectionBrush)
            };
            var quoteLinks = new List<MdInlineLink>();
            ParseInlineFormatting(block.Text, GetInlines(tb), ctx, ctx.BaseFontSize, FontWeight.Normal, FontStyle.Italic, ctx.SecondaryTextBrush, quoteLinks);
            if (quoteLinks.Count > 0) tb.Tag = quoteLinks;
            border.Child = tb;
            return ctx.Surface(border, true);
        }

        private static Border CreateRule(MarkdownRenderContext ctx)
        {
            return new Border
            {
                Height = 1,
                Background = ctx.ThematicBreakBrush,
                Margin = ctx.Compact ? new Thickness(0, 4, 0, 4) : new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        private static Control CreateCodeBlock(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            var border = new Border
            {
                Background = ctx.CodeBlockBackground,
                BorderBrush = ctx.CodeBlockBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = ctx.Compact ? new Thickness(0, 2, 0, 4) : new Thickness(0, 4, 0, 8),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // 紧凑排版模式：去掉语言标签与「复制」工具条，只保留纯代码块
            if (ctx.Compact)
            {
                var codeOnly = new SelectableTextBlock
                {
                    Text = block.Code,
                    FontFamily = ctx.CodeFontFamily,
                    FontSize = 12.5,
                    LineHeight = 20.0,
                    Foreground = ctx.PrimaryTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(12, 10),
                    SelectionBrush = ResolveSelection(selectionBrush)
                };
                border.Child = codeOnly;
                return ctx.Surface(border);
            }

            var mainGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*")
            };

            var headerBorder = new Border
            {
                Background = ctx.Workspace ? ctx.TableHeaderBackground : ctx.IsDark ? new SolidColorBrush(Color.Parse("#20202E")) : new SolidColorBrush(Color.Parse("#F1F5F9")),
                BorderBrush = ctx.CodeBlockBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 4, 8, 4)
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            string displayLang = string.IsNullOrWhiteSpace(block.Lang) ? "CODE" : block.Lang.ToUpperInvariant();
            var langLabel = new TextBlock
            {
                Text = displayLang,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = ctx.SecondaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(langLabel, 0);
            headerGrid.Children.Add(langLabel);

            var copyBtn = new Button
            {
                Content = ctx.Workspace ? "复制" : "📋 复制",
                FontSize = 11,
                Padding = new Thickness(8, 2),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ctx.SecondaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            copyBtn.Click += async (s, e) =>
            {
                ClipboardService.SetText(block.Code);
                copyBtn.Content = "✓ 已复制";
                copyBtn.Foreground = ctx.AccentBrush;
                await Task.Delay(1500);
                copyBtn.Content = ctx.Workspace ? "复制" : "📋 复制";
                copyBtn.Foreground = ctx.SecondaryTextBrush;
            };
            Grid.SetColumn(copyBtn, 1);
            headerGrid.Children.Add(copyBtn);
            headerBorder.Child = headerGrid;

            Grid.SetRow(headerBorder, 0);
            mainGrid.Children.Add(headerBorder);

            var codeText = new SelectableTextBlock
            {
                Text = block.Code,
                FontFamily = ctx.CodeFontFamily,
                FontSize = 12.5,
                LineHeight = 19.0,
                Foreground = ctx.PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(12, 10),
                SelectionBrush = ResolveSelection(selectionBrush)
            };
            Grid.SetRow(codeText, 1);
            mainGrid.Children.Add(codeText);

            border.Child = mainGrid;
            return ctx.Surface(border);
        }

        private static Control CreateTable(MdBlock block, MarkdownRenderContext ctx, IBrush? selectionBrush)
        {
            var border = new Border
            {
                BorderBrush = ctx.TableBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = ctx.Compact ? new Thickness(0, 2, 0, 4) : new Thickness(0, 6, 0, 8),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            string headerLine = block.TableLines[0].Trim('|');
            string[] headerCols = headerLine.Split('|').Select(c => c.Trim()).ToArray();
            int colCount = headerCols.Length;

            string separatorLine = block.TableLines[1].Trim('|');
            string[] sepCols = separatorLine.Split('|').Select(c => c.Trim()).ToArray();
            var alignments = new TextAlignment[colCount];
            for (int i = 0; i < colCount; i++)
            {
                if (i < sepCols.Length)
                {
                    string s = sepCols[i];
                    if (s.StartsWith(":", StringComparison.Ordinal) && s.EndsWith(":", StringComparison.Ordinal)) alignments[i] = TextAlignment.Center;
                    else if (s.EndsWith(":", StringComparison.Ordinal)) alignments[i] = TextAlignment.Right;
                    else alignments[i] = TextAlignment.Left;
                }
                else
                {
                    alignments[i] = TextAlignment.Left;
                }
            }

            var grid = new Grid();
            for (int i = 0; i < colCount; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            }

            int rowIdx = 0;

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < colCount; c++)
            {
                var cellBorder = new Border
                {
                    Background = ctx.TableHeaderBackground,
                    BorderBrush = ctx.TableBorderBrush,
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1),
                    Padding = new Thickness(10, 6)
                };
                var headerText = new TextBlock
                {
                    Text = headerCols[c],
                    FontWeight = FontWeight.Bold,
                    FontSize = ctx.Compact ? ctx.BaseFontSize : ctx.BaseFontSize - 0.5,
                    Foreground = ctx.PrimaryTextBrush,
                    TextAlignment = alignments[c]
                };
                cellBorder.Child = headerText;
                Grid.SetRow(cellBorder, rowIdx);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
            rowIdx++;

            for (int r = 2; r < block.TableLines.Count; r++)
            {
                string rowLine = block.TableLines[r].Trim('|');
                string[] rowCols = rowLine.Split('|').Select(c => c.Trim()).ToArray();
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                IBrush rowBg = (r % 2 == 0) ? ctx.TableAltRowBackground : Brushes.Transparent;

                for (int c = 0; c < colCount; c++)
                {
                    string cellContent = c < rowCols.Length ? rowCols[c] : "";
                    var cellBorder = new Border
                    {
                        Background = rowBg,
                        BorderBrush = ctx.TableBorderBrush,
                        BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, r < block.TableLines.Count - 1 ? 1 : 0),
                        Padding = new Thickness(10, 6)
                    };

                    var cellBlock = new SelectableTextBlock
                    {
                        Text = cellContent,
                        FontSize = ctx.Compact ? ctx.BaseFontSize : ctx.BaseFontSize - 0.5,
                        Foreground = ctx.PrimaryTextBrush,
                        TextAlignment = alignments[c],
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = ctx.Compact ? Math.Round(ctx.BaseFontSize * 1.5) : double.NaN,
                        SelectionBrush = ResolveSelection(selectionBrush)
                    };

                    cellBorder.Child = cellBlock;
                    Grid.SetRow(cellBorder, rowIdx);
                    Grid.SetColumn(cellBorder, c);
                    grid.Children.Add(cellBorder);
                }
                rowIdx++;
            }

            border.Child = grid;
            return ctx.Surface(border);
        }

        /// <summary>
        /// 行内富文本解析：粗体、斜体、粗斜体、删除线、行内代码、超链接。
        /// 对未配对的特殊字符按普通字符处理，绝不进入死循环。
        /// </summary>
        private static void ParseInlineFormatting(
            string text,
            InlineCollection inlines,
            MarkdownRenderContext ctx,
            double fontSize,
            FontWeight defaultWeight,
            FontStyle defaultStyle,
            IBrush defaultForeground,
            List<MdInlineLink>? links = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            text = DecodeHtmlEntities(text);

            int renderedCount = 0;
            if (links != null)
            {
                foreach (var inline in inlines)
                {
                    if (inline is Run r) renderedCount += (r.Text ?? "").Length;
                    else if (inline is LineBreak) renderedCount += 1;
                }
            }

            var literal = new StringBuilder();
            void Flush()
            {
                if (literal.Length == 0) return;
                inlines.Add(new Run(literal.ToString())
                {
                    FontSize = fontSize,
                    FontWeight = defaultWeight,
                    FontStyle = defaultStyle,
                    Foreground = defaultForeground
                });
                renderedCount += literal.Length;
                literal.Clear();
            }

            int pos = 0;
            int length = text.Length;

            while (pos < length)
            {
                char c = text[pos];

                if (c == '\n')
                {
                    Flush();
                    inlines.Add(new LineBreak());
                    renderedCount += 1;
                    pos++;
                    continue;
                }

                // 1. 行内代码 `code`
                if (c == '`')
                {
                    int end = text.IndexOf('`', pos + 1);
                    if (end > pos)
                    {
                        Flush();
                        string codeText = text.Substring(pos + 1, end - pos - 1);
                        inlines.Add(new Run(codeText)
                        {
                            FontFamily = ctx.CodeFontFamily,
                            FontSize = Math.Max(11.0, fontSize - 1.0),
                            FontWeight = FontWeight.Medium,
                            Foreground = ctx.AccentBrush,
                            Background = ctx.InlineCodeBackground
                        });
                        renderedCount += codeText.Length;
                        pos = end + 1;
                        continue;
                    }
                }

                // 2. 图片语法 ![alt](url)：按链接文字渲染（离屏导出不加载远程图片），避免残留感叹号
                if (c == '!' && pos + 1 < length && text[pos + 1] == '[')
                {
                    var imgPeek = Regex.Match(text.Substring(pos + 1), @"^\[([^\]]+)\]\(([^)]+)\)");
                    if (imgPeek.Success)
                    {
                        pos++;
                        continue;
                    }
                }

                // 2b. 超链接 [text](url)
                if (c == '[')
                {
                    var linkMatch = Regex.Match(text.Substring(pos), @"^\[([^\]]+)\]\(([^)]+)\)");
                    if (linkMatch.Success)
                    {
                        Flush();
                        string linkText = linkMatch.Groups[1].Value;
                        string linkUrl = linkMatch.Groups[2].Value;
                        inlines.Add(new Run(linkText)
                        {
                            FontSize = fontSize,
                            FontWeight = FontWeight.Medium,
                            Foreground = ctx.AccentBrush,
                            TextDecorations = TextDecorations.Underline
                        });
                        if (links != null)
                        {
                            links.Add(new MdInlineLink
                            {
                                Start = renderedCount,
                                End = renderedCount + linkText.Length,
                                Url = linkUrl
                            });
                        }
                        renderedCount += linkText.Length;
                        pos += linkMatch.Length;
                        continue;
                    }
                }

                // 3. 粗斜体 ***text***
                if (pos + 2 < length && ((c == '*' && text[pos + 1] == '*' && text[pos + 2] == '*') ||
                                         (c == '_' && text[pos + 1] == '_' && text[pos + 2] == '_')))
                {
                    string delim = text.Substring(pos, 3);
                    int end = text.IndexOf(delim, pos + 3, StringComparison.Ordinal);
                    if (end > pos)
                    {
                        Flush();
                        inlines.Add(new Run(text.Substring(pos + 3, end - pos - 3))
                        {
                            FontSize = fontSize,
                            FontWeight = FontWeight.Bold,
                            FontStyle = FontStyle.Italic,
                            Foreground = defaultForeground
                        });
                        renderedCount += end - pos - 3;
                        pos = end + 3;
                        continue;
                    }
                }

                // 4. 粗体 **text**
                if (pos + 1 < length && ((c == '*' && text[pos + 1] == '*') ||
                                         (c == '_' && text[pos + 1] == '_')))
                {
                    string delim = text.Substring(pos, 2);
                    int end = text.IndexOf(delim, pos + 2, StringComparison.Ordinal);
                    if (end > pos)
                    {
                        Flush();
                        inlines.Add(new Run(text.Substring(pos + 2, end - pos - 2))
                        {
                            FontSize = fontSize,
                            FontWeight = FontWeight.Bold,
                            FontStyle = defaultStyle,
                            Foreground = defaultForeground
                        });
                        renderedCount += end - pos - 2;
                        pos = end + 2;
                        continue;
                    }
                }

                // 5. 斜体 *text*
                if (c == '*' || c == '_')
                {
                    int end = text.IndexOf(c, pos + 1);
                    if (end > pos)
                    {
                        Flush();
                        inlines.Add(new Run(text.Substring(pos + 1, end - pos - 1))
                        {
                            FontSize = fontSize,
                            FontWeight = defaultWeight,
                            FontStyle = FontStyle.Italic,
                            Foreground = defaultForeground
                        });
                        renderedCount += end - pos - 1;
                        pos = end + 1;
                        continue;
                    }
                }

                // 6. 删除线 ~~text~~
                if (pos + 1 < length && c == '~' && text[pos + 1] == '~')
                {
                    int end = text.IndexOf("~~", pos + 2, StringComparison.Ordinal);
                    if (end > pos)
                    {
                        Flush();
                        inlines.Add(new Run(text.Substring(pos + 2, end - pos - 2))
                        {
                            FontSize = fontSize,
                            FontWeight = defaultWeight,
                            FontStyle = defaultStyle,
                            Foreground = ctx.MutedTextBrush,
                            TextDecorations = TextDecorations.Strikethrough
                        });
                        renderedCount += end - pos - 2;
                        pos = end + 2;
                        continue;
                    }
                }

                // 无任何匹配 → 按普通字符推进，防止死循环
                literal.Append(c);
                pos++;
            }

            Flush();
        }

        private static string DecodeHtmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&nbsp;", " ");
        }

        /// <summary>
        /// 纯文本转换（剥离 Markdown 格式标记）
        /// </summary>
        public static string StripMarkdown(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

            string text = markdown;
            text = Regex.Replace(text, @"```[a-zA-Z0-9_-]*\r?\n([\s\S]*?)```", "$1");
            text = Regex.Replace(text, @"```([\s\S]*?)```", "$1");
            text = Regex.Replace(text, @"(?m)^#{1,6}\s+", "");
            text = Regex.Replace(text, @"(\*\*|__)(.*?)\1", "$2");
            text = Regex.Replace(text, @"(\*|_)(.*?)\1", "$2");
            text = Regex.Replace(text, @"`([^`]+)`", "$1");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
            text = Regex.Replace(text, @"(?m)^[\s]*([\*\-\+]|\d+\.)\s+", "");
            text = Regex.Replace(text, @"(?m)^[\s]*>\s?", "");
            text = Regex.Replace(text, @"\r?\n{3,}", "\n\n");

            return text.Trim();
        }
    }

    internal enum MdBlockKind
    {
        Paragraph,
        Heading,
        List,
        Quote,
        Code,
        Table,
        Rule
    }

    /// <summary>
    /// 行内超链接在文本块中的字符区间（用于点击命中检测）。
    /// </summary>
    internal sealed class MdInlineLink
    {
        public int Start;
        public int End;
        public string Url = "";
    }

    internal sealed class MdBlock
    {
        public MdBlockKind Kind;
        public int Level;
        public string Lang = "";
        public string Text = "";
        public string Code = "";
        public List<MdListItem> Items = new List<MdListItem>();
        public List<string> TableLines = new List<string>();

        public string Signature
        {
            get
            {
                switch (Kind)
                {
                    case MdBlockKind.Heading: return "H" + Level + "|" + Text;
                    case MdBlockKind.List: return "L|" + string.Join("\u0001", Items.Select(i => i.Bullet + i.Text));
                    case MdBlockKind.Quote: return "Q|" + Text;
                    case MdBlockKind.Code: return "C|" + Lang + "|" + Code;
                    case MdBlockKind.Table: return "T|" + string.Join("\u0001", TableLines);
                    case MdBlockKind.Rule: return "R";
                    default: return "P|" + Text;
                }
            }
        }
    }

    internal sealed class MdListItem
    {
        public string Bullet = "";
        public string Text = "";
        public int IndentLevel;
    }
}