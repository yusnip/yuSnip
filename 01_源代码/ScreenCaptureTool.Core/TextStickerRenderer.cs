#nullable disable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ScreenCaptureTool.Core
{
    public class TextLayoutInfo
    {
        public string PlainText { get; set; }
        public float LineHeight { get; set; }
        public System.Collections.Generic.List<float> LineWidths { get; set; } = new System.Collections.Generic.List<float>();
        public System.Collections.Generic.List<HtmlLine> HtmlLines { get; set; } = new System.Collections.Generic.List<HtmlLine>();
    }

    public class TextStickerResult : IDisposable
    {
        public Bitmap Bitmap { get; set; }
        public TextLayoutInfo LayoutInfo { get; set; }

        public void Dispose()
        {
            Bitmap?.Dispose();
        }
    }

    public class HtmlRun
    {
        public string Text { get; set; }
        public Color Color { get; set; }
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
    }

    public class HtmlLine
    {
        public System.Collections.Generic.List<HtmlRun> Runs { get; set; } = new System.Collections.Generic.List<HtmlRun>();
    }

    /// <summary>
    /// 把文本渲染成贴图位图（用于剪贴板文本 → 贴图）。
    ///
    /// 默认样式（需求规格，后续可扩展为设置项）：
    /// - 字体：微软雅黑 UI（Windows 系统默认）
    /// - 字号：18px（物理像素）
    /// - 文字色 #333333，背景 #FFFFFF，内边距 16px
    /// - 最大宽度 ≤ 屏幕宽度的 60%，超出自动换行
    /// </summary>
    public static class TextStickerRenderer
    {
        private const string FontName = "Microsoft YaHei UI";
        private const float FontSizePx = 18f;
        private const int Padding = 16;
        private static readonly Color TextColor = ColorTranslator.FromHtml("#333333");
        private static readonly Color BackColor = ColorTranslator.FromHtml("#FFFFFF");

        public static bool IsCodeOrFormatted(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Contains('\t')) return true;

            string[] lines = text.Split('\n');
            if (lines.Length > 1)
            {
                foreach (var line in lines)
                {
                    string trimmed = line.TrimEnd('\r');
                    if (trimmed.StartsWith(" ") || trimmed.StartsWith("    "))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 把文本渲染为位图。
        /// </summary>
        /// <param name="text">要渲染的文本（不为空）。</param>
        /// <param name="maxWidthPhysical">内容区最大宽度（物理像素，已扣除内边距的可用宽度）。</param>
        public static TextStickerResult Render(string text, int maxWidthPhysical)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("文本为空。", nameof(text));
            if (maxWidthPhysical <= Padding * 2) maxWidthPhysical = Padding * 2 + 1;

            int availableWidth = maxWidthPhysical - Padding * 2;

            bool isCode = IsCodeOrFormatted(text);
            string fontName = isCode ? "Consolas" : FontName;
            Color backColor = isCode ? ColorTranslator.FromHtml("#F8F9FA") : BackColor;
            Color borderColor = isCode ? ColorTranslator.FromHtml("#E9ECEF") : Color.Transparent;

            using var font = new Font(fontName, FontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            // 先用一个临时 Graphics 测量；TextRenderer 在 GDI 下测量更准。
            using (var measureG = Graphics.FromHwnd(IntPtr.Zero))
            {
                measureG.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                measureG.SmoothingMode = SmoothingMode.AntiAlias;

                var wrapped = WrapText(measureG, font, text, availableWidth);
                var fmt = new StringFormat(StringFormatFlags.NoClip)
                {
                    Trimming = StringTrimming.Word,
                };

                // 测量所有行合成尺寸
                float lineHeight = font.GetHeight(measureG);
                float maxLineWidth = 0;
                var lineWidths = new System.Collections.Generic.List<float>();
                foreach (var line in wrapped)
                {
                    SizeF s = measureG.MeasureString(line, font, availableWidth, fmt);
                    lineWidths.Add(s.Width);
                    if (s.Width > maxLineWidth) maxLineWidth = s.Width;
                }

                int bmpWidth = (int)Math.Ceiling(maxLineWidth) + Padding * 2;
                int bmpHeight = (int)Math.Ceiling(wrapped.Count * lineHeight) + Padding * 2;

                var bmp = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(backColor);

                    if (isCode)
                    {
                        using var borderPen = new Pen(borderColor, 1);
                        g.DrawRectangle(borderPen, 0, 0, bmpWidth - 1, bmpHeight - 1);
                    }

                    var layout = new RectangleF(Padding, Padding, maxLineWidth, wrapped.Count * lineHeight);
                    using var brush = new SolidBrush(TextColor);
                    g.DrawString(string.Join(Environment.NewLine, wrapped), font, brush, layout, fmt);
                }

                var htmlLines = new System.Collections.Generic.List<HtmlLine>();
                foreach (var lineStr in wrapped)
                {
                    var run = new HtmlRun
                    {
                        Text = lineStr,
                        Color = TextColor,
                        IsBold = false,
                        IsItalic = false
                    };
                    var htmlLine = new HtmlLine();
                    htmlLine.Runs.Add(run);
                    htmlLines.Add(htmlLine);
                }

                var layoutInfo = new TextLayoutInfo
                {
                    PlainText = string.Join(Environment.NewLine, wrapped),
                    LineHeight = lineHeight,
                    LineWidths = lineWidths,
                    HtmlLines = htmlLines
                };

                return new TextStickerResult
                {
                    Bitmap = bmp,
                    LayoutInfo = layoutInfo
                };
            }
        }

        /// <summary>按可用宽度做简单的贪心换行，并保留行首的缩进（空格或制表符）。</summary>
        private static System.Collections.Generic.List<string> WrapText(Graphics g, Font font, string text, int maxWidth)
        {
            var result = new System.Collections.Generic.List<string>();
            string[] paragraphs = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var para in paragraphs)
            {
                if (string.IsNullOrEmpty(para))
                {
                    result.Add(string.Empty);
                    continue;
                }

                // 提取行首空白字符以实现缩进保留
                string leadingWhitespace = "";
                foreach (char c in para)
                {
                    if (c == ' ' || c == '\t') leadingWhitespace += c;
                    else break;
                }

                string remaining = para.Substring(leadingWhitespace.Length);
                if (string.IsNullOrEmpty(remaining))
                {
                    result.Add(leadingWhitespace);
                    continue;
                }

                var tokens = TokenizeText(remaining);
                var line = new System.Text.StringBuilder();
                line.Append(leadingWhitespace);

                bool isFirstToken = true;
                foreach (var token in tokens)
                {
                    string candidate = line.ToString() + token;
                    SizeF size = g.MeasureString(candidate, font);

                    if (size.Width > maxWidth && !isFirstToken)
                    {
                        result.Add(line.ToString());
                        line.Clear();
                        line.Append(leadingWhitespace);
                        line.Append(token.TrimStart(' '));
                        isFirstToken = true;
                    }
                    else
                    {
                        line.Clear();
                        line.Append(candidate);
                        isFirstToken = false;
                    }
                }
                if (line.Length > leadingWhitespace.Length)
                {
                    result.Add(line.ToString());
                }
            }
            return result;
        }


        private class TextStyle
        {
            public Color Color { get; set; } = ColorTranslator.FromHtml("#333333");
            public string FontName { get; set; } = null;
            public bool IsBold { get; set; }
            public bool IsItalic { get; set; }
        }

        public static TextStickerResult RenderHtml(string htmlText, string plainTextFallback, int maxWidthPhysical)
        {
            if (string.IsNullOrEmpty(htmlText))
            {
                return Render(plainTextFallback, maxWidthPhysical);
            }

            if (maxWidthPhysical <= Padding * 2) maxWidthPhysical = Padding * 2 + 1;
            int availableWidth = maxWidthPhysical - Padding * 2;

            Color? overallBgColor = null;
            var parsedLines = ParseHtmlToLines(htmlText, out overallBgColor);
            if (parsedLines == null || parsedLines.Count == 0)
            {
                return Render(plainTextFallback, maxWidthPhysical);
            }

            bool isCode = IsCodeOrFormatted(plainTextFallback) || overallBgColor.HasValue || HasMultipleColors(parsedLines);
            string fontName = isCode ? "Consolas" : FontName;
            Color backColor = overallBgColor ?? (isCode ? ColorTranslator.FromHtml("#F8F9FA") : BackColor);

            Color defaultTextClr = TextColor;
            double luminance = 0.299 * backColor.R + 0.587 * backColor.G + 0.114 * backColor.B;
            if (luminance < 128)
            {
                defaultTextClr = ColorTranslator.FromHtml("#D4D4D4");
            }

            // Adjust default colors for dark backgrounds
            foreach (var line in parsedLines)
            {
                foreach (var run in line.Runs)
                {
                    if (run.Color.R == TextColor.R && run.Color.G == TextColor.G && run.Color.B == TextColor.B)
                    {
                        run.Color = defaultTextClr;
                    }
                }
            }

            Color borderColor = Color.Transparent;
            if (isCode)
            {
                borderColor = luminance < 128
                    ? Color.FromArgb(45, 45, 45)
                    : Color.FromArgb(233, 236, 239);
            }

            // Create temporary graphics for measurements
            using (var measureG = Graphics.FromHwnd(IntPtr.Zero))
            {
                measureG.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                measureG.SmoothingMode = SmoothingMode.AntiAlias;

                // Wrap lines
                var wrappedLines = new System.Collections.Generic.List<HtmlLine>();
                foreach (var line in parsedLines)
                {
                    var wrapped = WrapHtmlLine(line, measureG, fontName, FontSizePx, availableWidth);
                    wrappedLines.AddRange(wrapped);
                }

                if (wrappedLines.Count == 0)
                {
                    return Render(plainTextFallback, maxWidthPhysical);
                }

                // Measure line height and width
                float lineHeight = 0;
                using (var defaultFont = new Font(fontName, FontSizePx, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    lineHeight = defaultFont.GetHeight(measureG);
                }

                float maxLineWidth = 0;
                var sf = StringFormat.GenericTypographic;
                var lineWidths = new System.Collections.Generic.List<float>();

                foreach (var line in wrappedLines)
                {
                    float lineWidth = 0;
                    foreach (var run in line.Runs)
                    {
                        FontStyle style = FontStyle.Regular;
                        if (run.IsBold) style |= FontStyle.Bold;
                        if (run.IsItalic) style |= FontStyle.Italic;

                        using var f = new Font(fontName, FontSizePx, style, GraphicsUnit.Pixel);
                        lineWidth += measureG.MeasureString(run.Text, f, PointF.Empty, sf).Width;
                    }
                    lineWidths.Add(lineWidth);
                    if (lineWidth > maxLineWidth) maxLineWidth = lineWidth;
                }

                int bmpWidth = (int)Math.Ceiling(maxLineWidth) + Padding * 2;
                int bmpHeight = (int)Math.Ceiling(wrappedLines.Count * lineHeight) + Padding * 2;

                if (bmpWidth < 40) bmpWidth = 40;
                if (bmpHeight < 40) bmpHeight = 40;

                var bmp = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(backColor);

                    if (borderColor != Color.Transparent)
                    {
                        using var borderPen = new Pen(borderColor, 1);
                        g.DrawRectangle(borderPen, 0, 0, bmpWidth - 1, bmpHeight - 1);
                    }

                    float currentY = Padding;
                    foreach (var line in wrappedLines)
                    {
                        float currentX = Padding;
                        foreach (var run in line.Runs)
                        {
                            FontStyle style = FontStyle.Regular;
                            if (run.IsBold) style |= FontStyle.Bold;
                            if (run.IsItalic) style |= FontStyle.Italic;

                            using var f = new Font(fontName, FontSizePx, style, GraphicsUnit.Pixel);
                            using var brush = new SolidBrush(run.Color);

                            g.DrawString(run.Text, f, brush, currentX, currentY, sf);
                            currentX += g.MeasureString(run.Text, f, PointF.Empty, sf).Width;
                        }
                        currentY += lineHeight;
                    }
                }

                var plainLines = new System.Collections.Generic.List<string>();
                foreach (var line in wrappedLines)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var run in line.Runs)
                    {
                        sb.Append(run.Text);
                    }
                    plainLines.Add(sb.ToString().TrimEnd());
                }

                var layoutInfo = new TextLayoutInfo
                {
                    PlainText = string.Join(Environment.NewLine, plainLines),
                    LineHeight = lineHeight,
                    LineWidths = lineWidths,
                    HtmlLines = wrappedLines
                };

                return new TextStickerResult
                {
                    Bitmap = bmp,
                    LayoutInfo = layoutInfo
                };
            }
        }

        private static System.Collections.Generic.List<HtmlLine> WrapHtmlLine(HtmlLine line, Graphics g, string fontName, float fontSize, float maxWidth)
        {
            var result = new System.Collections.Generic.List<HtmlLine>();
            var currentWrapped = new HtmlLine();
            result.Add(currentWrapped);

            float currentX = 0;

            // Extract leading whitespace as indent
            string leadingWhitespace = "";
            bool foundNonSpace = false;
            foreach (var run in line.Runs)
            {
                if (foundNonSpace) break;
                foreach (char c in run.Text)
                {
                    if (c == ' ' || c == '\t') leadingWhitespace += c;
                    else
                    {
                        foundNonSpace = true;
                        break;
                    }
                }
            }

            // Measure indent width
            float indentWidth = 0;
            if (leadingWhitespace.Length > 0)
            {
                using var f = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                indentWidth = g.MeasureString(leadingWhitespace, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            }

            bool isStartOfWrappedLine = true;

            foreach (var run in line.Runs)
            {
                string textToProcess = run.Text;
                if (string.IsNullOrEmpty(textToProcess)) continue;

                var tokens = TokenizeText(textToProcess);
                
                foreach (var token in tokens)
                {
                    FontStyle fontStyle = FontStyle.Regular;
                    if (run.IsBold) fontStyle |= FontStyle.Bold;
                    if (run.IsItalic) fontStyle |= FontStyle.Italic;

                    using var f = new Font(fontName, fontSize, fontStyle, GraphicsUnit.Pixel);
                    float tokenWidth = g.MeasureString(token, f, PointF.Empty, StringFormat.GenericTypographic).Width;

                    // Apply indentation if we are at the start of a wrapped line
                    if (isStartOfWrappedLine && currentX == 0 && indentWidth > 0 && leadingWhitespace.Length > 0)
                    {
                        currentWrapped.Runs.Add(new HtmlRun
                        {
                            Text = leadingWhitespace,
                            Color = run.Color,
                            IsBold = false,
                            IsItalic = false
                        });
                        currentX = indentWidth;
                        isStartOfWrappedLine = false;

                        // If the token starts with leadingWhitespace, strip it to avoid double indentation
                        string modifiedToken = token;
                        if (modifiedToken.StartsWith(leadingWhitespace))
                        {
                            modifiedToken = modifiedToken.Substring(leadingWhitespace.Length);
                            tokenWidth = g.MeasureString(modifiedToken, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                            if (string.IsNullOrEmpty(modifiedToken)) continue;
                        }
                        
                        // Proceed with modified token
                        if (currentX + tokenWidth > maxWidth && currentWrapped.Runs.Count > 0)
                        {
                            currentWrapped = new HtmlLine();
                            result.Add(currentWrapped);
                            currentX = 0;
                            isStartOfWrappedLine = true;
                        }
                        
                        currentWrapped.Runs.Add(new HtmlRun
                        {
                            Text = modifiedToken,
                            Color = run.Color,
                            IsBold = run.IsBold,
                            IsItalic = run.IsItalic
                        });
                        currentX += tokenWidth;
                        continue;
                    }

                    if (currentX + tokenWidth > maxWidth && currentWrapped.Runs.Count > 0 && !isStartOfWrappedLine)
                    {
                        // Wrap line
                        currentWrapped = new HtmlLine();
                        result.Add(currentWrapped);
                        currentX = 0;
                        isStartOfWrappedLine = true;

                        // Trim leading spaces for the wrapped token
                        string trimmedToken = token.TrimStart(' ');
                        if (string.IsNullOrEmpty(trimmedToken)) continue;

                        float trimmedWidth = g.MeasureString(trimmedToken, f, PointF.Empty, StringFormat.GenericTypographic).Width;

                        currentWrapped.Runs.Add(new HtmlRun
                        {
                            Text = trimmedToken,
                            Color = run.Color,
                            IsBold = run.IsBold,
                            IsItalic = run.IsItalic
                        });
                        currentX = trimmedWidth;
                        isStartOfWrappedLine = false;
                    }
                    else
                    {
                        currentWrapped.Runs.Add(new HtmlRun
                        {
                            Text = token,
                            Color = run.Color,
                            IsBold = run.IsBold,
                            IsItalic = run.IsItalic
                        });
                        currentX += tokenWidth;
                        isStartOfWrappedLine = false;
                    }
                }
            }

            return result;
        }

        public static System.Collections.Generic.List<HtmlLine> ParseHtmlToLines(string html, out Color? overallBgColor)
        {
            overallBgColor = null;
            var lines = new System.Collections.Generic.List<HtmlLine>();
            var currentLine = new HtmlLine();
            lines.Add(currentLine);

            var styleStack = new System.Collections.Generic.Stack<TextStyle>();
            var baseStyle = new TextStyle();
            styleStack.Push(baseStyle);

            int startFragment = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int endFragment = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            string content = html;
            if (startFragment >= 0 && endFragment > startFragment)
            {
                content = html.Substring(startFragment + "<!--StartFragment-->".Length, endFragment - startFragment - "<!--StartFragment-->".Length);
            }

            int i = 0;
            int len = content.Length;
            var textBuffer = new System.Text.StringBuilder();

            while (i < len)
            {
                char c = content[i];
                if (c == '<')
                {
                    if (textBuffer.Length > 0)
                    {
                        string text = DecodeHtmlEntities(textBuffer.ToString());
                        textBuffer.Clear();

                        if (!string.IsNullOrEmpty(text))
                        {
                            var currentStyle = styleStack.Peek();
                            currentLine.Runs.Add(new HtmlRun
                            {
                                Text = text,
                                Color = currentStyle.Color,
                                IsBold = currentStyle.IsBold,
                                IsItalic = currentStyle.IsItalic
                            });
                        }
                    }

                    int closeTagIdx = content.IndexOf('>', i);
                    if (closeTagIdx > i)
                    {
                        string tagContent = content.Substring(i + 1, closeTagIdx - i - 1).Trim();
                        i = closeTagIdx + 1;

                        if (tagContent.StartsWith("/"))
                        {
                            string tagName = tagContent.Substring(1).Trim().ToLower();
                            if (styleStack.Count > 1)
                            {
                                styleStack.Pop();
                            }
                            if (tagName == "div" || tagName == "p" || tagName == "pre" || tagName == "tr" || tagName == "li")
                            {
                                if (currentLine.Runs.Count > 0)
                                {
                                    currentLine = new HtmlLine();
                                    lines.Add(currentLine);
                                }
                            }
                        }
                        else
                        {
                            bool isSelfClosing = tagContent.EndsWith("/");
                            string tagBody = isSelfClosing ? tagContent.Substring(0, tagContent.Length - 1).Trim() : tagContent;

                            int firstSpace = tagBody.IndexOf(' ');
                            string tagName = firstSpace > 0 ? tagBody.Substring(0, firstSpace).ToLower() : tagBody.ToLower();

                            if (tagName == "br")
                            {
                                currentLine = new HtmlLine();
                                lines.Add(currentLine);
                            }
                            else
                            {
                                var nextStyle = new TextStyle();
                                if (styleStack.Count > 0)
                                {
                                    var parent = styleStack.Peek();
                                    nextStyle.Color = parent.Color;
                                    nextStyle.FontName = parent.FontName;
                                    nextStyle.IsBold = parent.IsBold;
                                    nextStyle.IsItalic = parent.IsItalic;
                                }

                                int styleAttrIdx = tagBody.IndexOf("style=", StringComparison.OrdinalIgnoreCase);
                                if (styleAttrIdx >= 0)
                                {
                                    char quote = '\"';
                                    int quoteStart = tagBody.IndexOf('\"', styleAttrIdx);
                                    int quoteStartSingle = tagBody.IndexOf('\'', styleAttrIdx);
                                    if (quoteStartSingle >= 0 && (quoteStart < 0 || quoteStartSingle < quoteStart))
                                    {
                                        quote = '\'';
                                        quoteStart = quoteStartSingle;
                                    }

                                    if (quoteStart >= 0)
                                    {
                                        int quoteEnd = tagBody.IndexOf(quote, quoteStart + 1);
                                        if (quoteEnd > quoteStart)
                                        {
                                            string styleStr = tagBody.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                                            ParseCss(styleStr, nextStyle, ref overallBgColor);
                                        }
                                    }
                                }

                                if (tagName == "div" || tagName == "p" || tagName == "pre" || tagName == "tr" || tagName == "li")
                                {
                                    if (currentLine.Runs.Count > 0)
                                    {
                                        currentLine = new HtmlLine();
                                        lines.Add(currentLine);
                                    }
                                }

                                if (!isSelfClosing)
                                {
                                    styleStack.Push(nextStyle);
                                }
                            }
                        }
                    }
                    else
                    {
                        textBuffer.Append(c);
                        i++;
                    }
                }
                else if (c == '\n' || c == '\r')
                {
                    if (textBuffer.Length > 0)
                    {
                        string text = DecodeHtmlEntities(textBuffer.ToString());
                        textBuffer.Clear();

                        if (!string.IsNullOrEmpty(text))
                        {
                            var currentStyle = styleStack.Peek();
                            currentLine.Runs.Add(new HtmlRun
                            {
                                Text = text,
                                Color = currentStyle.Color,
                                IsBold = currentStyle.IsBold,
                                IsItalic = currentStyle.IsItalic
                            });
                        }
                    }

                    if (c == '\n')
                    {
                        currentLine = new HtmlLine();
                        lines.Add(currentLine);
                    }
                    i++;
                }
                else
                {
                    textBuffer.Append(c);
                    i++;
                }
            }

            if (textBuffer.Length > 0)
            {
                string text = DecodeHtmlEntities(textBuffer.ToString());
                if (!string.IsNullOrEmpty(text))
                {
                    var currentStyle = styleStack.Peek();
                    currentLine.Runs.Add(new HtmlRun
                    {
                        Text = text,
                        Color = currentStyle.Color,
                        IsBold = currentStyle.IsBold,
                        IsItalic = currentStyle.IsItalic
                    });
                }
            }

            while (lines.Count > 1 && lines[lines.Count - 1].Runs.Count == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        private static string DecodeHtmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Robustly clean up any variations of non-breaking space entities
            string cleaned = text
                .Replace("&#160;", " ")
                .Replace("&#160；", " ")
                .Replace("&#160", " ")
                .Replace("&nbsp;", " ")
                .Replace("&nbsp；", " ");

            string decoded = System.Net.WebUtility.HtmlDecode(cleaned);
            return decoded
                .Replace('\u00A0', ' ')
                .Replace("\t", "    ");
        }

        private static void ParseCss(string styleStr, TextStyle style, ref Color? overallBgColor)
        {
            if (string.IsNullOrEmpty(styleStr)) return;
            var declarations = styleStr.Split(';');
            foreach (var decl in declarations)
            {
                var parts = decl.Split(':');
                if (parts.Length < 2) continue;
                string key = parts[0].Trim().ToLower();
                string val = parts[1].Trim();

                if (key == "color")
                {
                    var c = ParseColor(val);
                    if (c.HasValue) style.Color = c.Value;
                }
                else if (key == "background-color" || key == "background")
                {
                    var c = ParseColor(val);
                    if (c.HasValue && !overallBgColor.HasValue)
                    {
                        overallBgColor = c.Value;
                    }
                }
                else if (key == "font-weight")
                {
                    if (val.ToLower() == "bold" || val.ToLower() == "bolder")
                    {
                        style.IsBold = true;
                    }
                }
                else if (key == "font-style")
                {
                    if (val.ToLower() == "italic")
                    {
                        style.IsItalic = true;
                    }
                }
            }
        }

        private static Color? ParseColor(string colorStr)
        {
            colorStr = colorStr.Trim();
            if (colorStr.StartsWith("#"))
            {
                try
                {
                    if (colorStr.Length == 4)
                    {
                        string r = new string(colorStr[1], 2);
                        string g = new string(colorStr[2], 2);
                        string b = new string(colorStr[3], 2);
                        return ColorTranslator.FromHtml("#" + r + g + b);
                    }
                    return ColorTranslator.FromHtml(colorStr);
                }
                catch { return null; }
            }
            else if (colorStr.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    int openBrace = colorStr.IndexOf('(');
                    int closeBrace = colorStr.IndexOf(')');
                    if (openBrace >= 0 && closeBrace > openBrace)
                    {
                        string content = colorStr.Substring(openBrace + 1, closeBrace - openBrace - 1);
                        string[] parts = content.Split(',');
                        if (parts.Length >= 3)
                        {
                            int r = int.Parse(parts[0].Trim());
                            int g = int.Parse(parts[1].Trim());
                            int b = int.Parse(parts[2].Trim());
                            return Color.FromArgb(r, g, b);
                        }
                    }
                }
                catch { return null; }
            }
            else
            {
                try
                {
                    return Color.FromName(colorStr);
                }
                catch { return null; }
            }
            return null;
        }

        private static System.Collections.Generic.List<string> TokenizeText(string text)
        {
            var tokens = new System.Collections.Generic.List<string>();
            int i = 0;
            int len = text.Length;
            var current = new System.Text.StringBuilder();

            while (i < len)
            {
                char c = text[i];
                if (c == ' ')
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    while (i < len && text[i] == ' ')
                    {
                        current.Append(' ');
                        i++;
                    }
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }
            return tokens;
        }

        private static bool HasMultipleColors(System.Collections.Generic.List<HtmlLine> lines)
        {
            Color? firstColor = null;
            foreach (var line in lines)
            {
                foreach (var run in line.Runs)
                {
                    if (firstColor == null)
                    {
                        firstColor = run.Color;
                    }
                    else if (run.Color != firstColor.Value)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
