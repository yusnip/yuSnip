using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Controls
{
    /// <summary>
    /// 跨块多段文本连续选区控制器：
    /// 统一驱动容器内所有子 <see cref="SelectableTextBlock"/> 的选区状态，
    /// 彻底解决 Avalonia 原生多块文本控件无法跨段落/跨标题划选的技术瓶颈。
    /// 
    /// 核心特性：
    /// 1. 双向感知 Inlines（Run + InlineUIContainer）的实际文字索引与排版布局坐标，100% 确保【所划即所选，所选即所复制】；
    /// 2. 智能避让修正胶囊点击事件，彻底消除胶囊点不中/概率性失效问题；
    /// 3. 直接读取 SelectableTextBlock.TextLayout 内部真实的排版矩阵进行 HitTestPoint 精确测量，彻底解决选区偏移错位；
    /// 4. 区分从左往右（正向）与从右往左（反向）拖选，保证选区边缘像素级跟手；
    /// 5. 统一代理 Ctrl+C 复制事件，按文本块逻辑顺序拼装换行后的完整纯文本。
    /// </summary>
    public class CrossBlockSelectionController
    {
        private readonly Control _host;
        private readonly Func<IEnumerable<SelectableTextBlock>> _textBlocksProvider;
        private IBrush? _selectionBrush;

        private bool _isSelecting;
        private SelectableTextBlock? _anchorBlock;
        private int _anchorRawOffset;

        private SelectableTextBlock? _activeBlock;
        private int _activeRawOffset;

        public CrossBlockSelectionController(Control host, Func<IEnumerable<SelectableTextBlock>> textBlocksProvider)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _textBlocksProvider = textBlocksProvider ?? throw new ArgumentNullException(nameof(textBlocksProvider));

            AttachEvents();
        }

        public IBrush? SelectionBrush
        {
            get => _selectionBrush;
            set
            {
                _selectionBrush = value;
                ApplySelectionBrushToAll();
            }
        }

        public bool HasSelection
        {
            get
            {
                var blocks = _textBlocksProvider().ToList();
                return blocks.Any(b => !string.IsNullOrEmpty(b.SelectedText));
            }
        }

        public string SelectedText
        {
            get
            {
                var sb = new StringBuilder();
                var blocks = _textBlocksProvider().ToList();
                foreach (var b in blocks)
                {
                    string text = ExtractBlockSelectedText(b);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(text);
                    }
                }
                return sb.ToString();
            }
        }

        public void ClearSelection()
        {
            _isSelecting = false;
            _anchorBlock = null;
            _activeBlock = null;

            foreach (var b in _textBlocksProvider())
            {
                b.ClearSelection();
            }
        }

        public void SelectAll()
        {
            var blocks = _textBlocksProvider().ToList();
            if (blocks.Count == 0) return;

            foreach (var b in blocks)
            {
                b.SelectAll();
            }
        }

        private void AttachEvents()
        {
            // 必须使用 RoutingStrategies.Tunnel 才能在 SelectableTextBlock 捕获指针前拦截按下事件，
            // 否则从行内文字按下时，原生 SelectableTextBlock 会调用 Pointer.Capture 强行锁定单块划选，
            // 导致鼠标拖到块外时外部容器完全收不到 PointerMoved，造成无法跨行跨块划选。
            _host.AddHandler(InputElement.PointerPressedEvent, OnHostPointerPressed, RoutingStrategies.Tunnel);
            _host.AddHandler(InputElement.PointerMovedEvent, OnHostPointerMoved, RoutingStrategies.Tunnel);
            _host.AddHandler(InputElement.PointerReleasedEvent, OnHostPointerReleased, RoutingStrategies.Tunnel);
            _host.AddHandler(InputElement.KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        }

        private void ApplySelectionBrushToAll()
        {
            if (_selectionBrush == null) return;
            foreach (var b in _textBlocksProvider())
            {
                b.SelectionBrush = _selectionBrush;
            }
        }

        private void OnHostPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var prop = e.GetCurrentPoint(_host).Properties;
            if (!prop.IsLeftButtonPressed) return;

            // 如果点击的是修正胶囊或按钮内部，完全让给胶囊处理弹窗，不劫持
            if (e.Source is Visual visual)
            {
                var border = visual.FindAncestorOfType<Border>();
                while (border != null)
                {
                    if (border.Cursor != null && border.Cursor == Cursor.Parse("Hand"))
                    {
                        return;
                    }
                    if (border.Classes.Contains("rfFixWord"))
                    {
                        return;
                    }
                    border = border.FindAncestorOfType<Border>();
                }

                if (visual.FindAncestorOfType<Button>() != null)
                {
                    return;
                }
            }

            var blocks = _textBlocksProvider().ToList();
            if (blocks.Count == 0) return;

            var posInHost = e.GetPosition(_host);
            var hit = HitTestBlock(blocks, posInHost);

            if (hit != null)
            {
                _isSelecting = true;
                _anchorBlock = hit.Block;
                _anchorRawOffset = hit.RawOffset;
                _activeBlock = hit.Block;
                _activeRawOffset = hit.RawOffset;

                foreach (var b in blocks)
                {
                    if (b != _anchorBlock) b.ClearSelection();
                }

                _anchorBlock.SelectionStart = _anchorRawOffset;
                _anchorBlock.SelectionEnd = _anchorRawOffset;
            }
            else
            {
                ClearSelection();
            }
        }

        private void OnHostPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isSelecting || _anchorBlock == null) return;

            var prop = e.GetCurrentPoint(_host).Properties;
            if (!prop.IsLeftButtonPressed)
            {
                _isSelecting = false;
                return;
            }

            var blocks = _textBlocksProvider().ToList();
            if (blocks.Count == 0) return;

            var posInHost = e.GetPosition(_host);
            var hit = HitTestBlock(blocks, posInHost);
            if (hit == null) return;

            _activeBlock = hit.Block;
            _activeRawOffset = hit.RawOffset;

            UpdateBlockSelections(blocks, _anchorBlock, _anchorRawOffset, _activeBlock, _activeRawOffset);
            e.Handled = true;
        }

        private void OnHostPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isSelecting)
            {
                _isSelecting = false;
            }
        }

        private void OnHostKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
            {
                string text = SelectedText;
                if (!string.IsNullOrEmpty(text))
                {
                    ClipboardService.SetText(text);
                    e.Handled = true;
                }
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
            {
                SelectAll();
                e.Handled = true;
            }
        }

        private static void UpdateBlockSelections(List<SelectableTextBlock> blocks,
            SelectableTextBlock anchorBlock, int anchorRawOffset,
            SelectableTextBlock activeBlock, int activeRawOffset)
        {
            int anchorIdx = blocks.IndexOf(anchorBlock);
            int activeIdx = blocks.IndexOf(activeBlock);

            if (anchorIdx < 0 || activeIdx < 0) return;

            if (anchorIdx == activeIdx)
            {
                int start = Math.Min(anchorRawOffset, activeRawOffset);
                int end = Math.Max(anchorRawOffset, activeRawOffset);

                for (int i = 0; i < blocks.Count; i++)
                {
                    if (i == anchorIdx)
                    {
                        blocks[i].SelectionStart = start;
                        blocks[i].SelectionEnd = end;
                    }
                    else
                    {
                        blocks[i].ClearSelection();
                    }
                }
            }
            else
            {
                int minIdx = Math.Min(anchorIdx, activeIdx);
                int maxIdx = Math.Max(anchorIdx, activeIdx);
                bool forward = anchorIdx < activeIdx;

                for (int i = 0; i < blocks.Count; i++)
                {
                    if (i < minIdx || i > maxIdx)
                    {
                        blocks[i].ClearSelection();
                    }
                    else if (i == minIdx)
                    {
                        int len = GetBlockRawLength(blocks[i]);
                        if (forward)
                        {
                            blocks[i].SelectionStart = Math.Clamp(anchorRawOffset, 0, len);
                            blocks[i].SelectionEnd = len;
                        }
                        else
                        {
                            blocks[i].SelectionStart = Math.Clamp(activeRawOffset, 0, len);
                            blocks[i].SelectionEnd = len;
                        }
                    }
                    else if (i == maxIdx)
                    {
                        int len = GetBlockRawLength(blocks[i]);
                        if (forward)
                        {
                            blocks[i].SelectionStart = 0;
                            blocks[i].SelectionEnd = Math.Clamp(activeRawOffset, 0, len);
                        }
                        else
                        {
                            blocks[i].SelectionStart = 0;
                            blocks[i].SelectionEnd = Math.Clamp(anchorRawOffset, 0, len);
                        }
                    }
                    else
                    {
                        blocks[i].SelectAll();
                    }
                }
            }
        }

        private static int GetBlockRawLength(SelectableTextBlock block)
        {
            if (!string.IsNullOrEmpty(block.Text)) return block.Text.Length;
            if (block.Inlines is { Count: > 0 })
            {
                int sum = 0;
                foreach (var inline in block.Inlines)
                {
                    if (inline is Avalonia.Controls.Documents.Run run && !string.IsNullOrEmpty(run.Text))
                        sum += run.Text.Length;
                    else if (inline is Avalonia.Controls.Documents.InlineUIContainer)
                        sum += 1;
                }
                return sum;
            }
            return 0;
        }

        /// <summary>
        /// 完整提取 SelectableTextBlock 的纯文本（包含 Inlines 中 Run 与 InlineUIContainer 胶囊内部的文字）
        /// </summary>
        public static string GetBlockFullText(SelectableTextBlock block)
        {
            if (!string.IsNullOrEmpty(block.Text)) return block.Text;
            if (block.Inlines is { Count: > 0 })
            {
                var sb = new StringBuilder();
                foreach (var inline in block.Inlines)
                {
                    if (inline is Avalonia.Controls.Documents.Run run && !string.IsNullOrEmpty(run.Text))
                    {
                        sb.Append(run.Text);
                    }
                    else if (inline is Avalonia.Controls.Documents.InlineUIContainer container)
                    {
                        string containerText = ExtractContainerText(container.Child);
                        sb.Append(containerText);
                    }
                }
                return sb.ToString();
            }
            return string.Empty;
        }

        /// <summary>
        /// 递归提取 InlineUIContainer 内嵌子树中的文字（如胶囊内的 TextBlock）
        /// </summary>
        private static string ExtractContainerText(Control? child)
        {
            if (child == null) return string.Empty;
            if (child is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) return tb.Text;
            if (child is SelectableTextBlock stb && !string.IsNullOrEmpty(stb.Text)) return stb.Text;

            var sb = new StringBuilder();
            foreach (var descendant in child.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!string.IsNullOrEmpty(descendant.Text))
                    sb.Append(descendant.Text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 提取块当前真实选中的纯文本：根据 Inlines 的分段结构，精准切出 SelectionStart 到 SelectionEnd 对应的文字
        /// </summary>
        private static string ExtractBlockSelectedText(SelectableTextBlock block)
        {
            int start = Math.Min(block.SelectionStart, block.SelectionEnd);
            int end = Math.Max(block.SelectionStart, block.SelectionEnd);
            int len = end - start;
            if (len <= 0) return string.Empty;

            if (!string.IsNullOrEmpty(block.Text))
            {
                if (start >= 0 && end <= block.Text.Length)
                {
                    return block.Text.Substring(start, len);
                }
                return block.SelectedText ?? string.Empty;
            }

            if (block.Inlines is { Count: > 0 })
            {
                var sb = new StringBuilder();
                int currentRawPos = 0;

                foreach (var inline in block.Inlines)
                {
                    string inlineText = string.Empty;
                    int inlineRawLen = 0;

                    if (inline is Avalonia.Controls.Documents.Run run && !string.IsNullOrEmpty(run.Text))
                    {
                        inlineText = run.Text;
                        inlineRawLen = run.Text.Length;
                    }
                    else if (inline is Avalonia.Controls.Documents.InlineUIContainer container)
                    {
                        inlineText = ExtractContainerText(container.Child);
                        inlineRawLen = 1; // InlineUIContainer 在 TextLayout/Selection 中占 1 个字符偏移
                    }

                    int inlineRawStart = currentRawPos;
                    int inlineRawEnd = currentRawPos + inlineRawLen;

                    // 计算当前 inline 与选区 [start, end) 的重叠区间
                    int overlapStart = Math.Max(start, inlineRawStart);
                    int overlapEnd = Math.Min(end, inlineRawEnd);

                    if (overlapStart < overlapEnd)
                    {
                        if (inline is Avalonia.Controls.Documents.InlineUIContainer)
                        {
                            // 选中了胶囊，整段拼接胶囊内的修正词
                            sb.Append(inlineText);
                        }
                        else
                        {
                            // Run 文本：切出实际选中的局部字符串
                            int localStart = overlapStart - inlineRawStart;
                            int localLen = overlapEnd - overlapStart;
                            if (localStart >= 0 && localStart + localLen <= inlineText.Length)
                            {
                                sb.Append(inlineText.Substring(localStart, localLen));
                            }
                        }
                    }

                    currentRawPos = inlineRawEnd;
                }

                return sb.ToString();
            }

            return block.SelectedText ?? string.Empty;
        }

        private sealed record HitResult(SelectableTextBlock Block, int RawOffset);

        private HitResult? HitTestBlock(List<SelectableTextBlock> blocks, Point hostPos)
        {
            SelectableTextBlock? closestBlock = null;
            double minDist = double.MaxValue;

            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (!block.IsVisible) continue;
                var bPoint = _host.TranslatePoint(hostPos, block);
                if (bPoint.HasValue)
                {
                    var p = bPoint.Value;
                    var bounds = block.Bounds;

                    if (p.Y >= 0 && p.Y <= bounds.Height)
                    {
                        int offset = CalculateExactRawOffset(block, p);
                        return new HitResult(block, offset);
                    }

                    double distY = p.Y < 0 ? -p.Y : p.Y - bounds.Height;
                    if (distY < minDist)
                    {
                        minDist = distY;
                        closestBlock = block;
                    }
                }
            }

            if (closestBlock != null)
            {
                var bPoint = _host.TranslatePoint(hostPos, closestBlock);
                if (bPoint.HasValue)
                {
                    int offset = CalculateExactRawOffset(closestBlock, bPoint.Value);
                    return new HitResult(closestBlock, offset);
                }
            }

            return null;
        }

        /// <summary>
        /// 计算指针在 SelectableTextBlock 内的真实原始字符偏移（RawOffset）：
        /// 确保该偏移与 TextLayout 以及 Inlines 的内部排版位置 100% 对齐。
        /// </summary>
        private static int CalculateExactRawOffset(SelectableTextBlock block, Point posInBlock)
        {
            int rawLen = GetBlockRawLength(block);
            if (rawLen <= 0) return 0;

            if (posInBlock.Y < 0) return 0;
            if (posInBlock.Y > block.Bounds.Height) return rawLen;

            if (block.TextLayout != null)
            {
                try
                {
                    var hit = block.TextLayout.HitTestPoint(posInBlock);
                    int charIdx = hit.TextPosition;
                    if (hit.IsTrailing)
                    {
                        charIdx = Math.Min(rawLen, charIdx + 1);
                    }
                    return Math.Clamp(charIdx, 0, rawLen);
                }
                catch
                {
                    // 降级兜底
                }
            }

            double fontSize = block.FontSize > 0 ? block.FontSize : 13.5;
            double lineHeight = !double.IsNaN(block.LineHeight) && block.LineHeight > 0 ? block.LineHeight : fontSize * 1.6;
            double availableWidth = Math.Max(10, block.Bounds.Width);

            int estimatedLine = Math.Max(0, (int)(posInBlock.Y / Math.Max(1, lineHeight)));
            int charsPerLine = Math.Max(1, (int)(availableWidth / Math.Max(1, fontSize * 0.65)));
            int charInLine = Math.Max(0, (int)(posInBlock.X / Math.Max(1, fontSize * 0.65)));

            int offset = estimatedLine * charsPerLine + charInLine;
            return Math.Clamp(offset, 0, rawLen);
        }
    }
}
