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
        /// <summary>位图这次排版<b>实际使用的折行预算</b>（物理像素，已扣掉位图内边距）。
        /// 活控件那侧的 <c>MaxWidth</c> 必须取它换算成 DIP，而不是取"位图宽 − 内边距"：
        /// 后者＝GDI 对自己那几行的实测宽，Avalonia 排同样几个字通常略宽一点，
        /// 拿它当约束＝逼活控件假折行（卡片被挤窄挤高，与窗口账本对不上＝阴影比图片大一圈）。</summary>
        public int WrapWidthPhysical { get; set; }
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

    /// <summary>
    /// 文本贴图配色（由 App 按当前主题解析 SctSticker*Color Token 后传入）。
    ///
    /// 刻意做成"全可空"而不是必填：任一字段为 null 时渲染器沿用本类原有的浅色字面量，
    /// 因此调用方还没接线的路径输出与改动前逐字节相同。
    /// </summary>
    public class TextStickerPalette
    {
        public Color? Text { get; set; }
        public Color? Back { get; set; }
        public Color? CodeBack { get; set; }
        public Color? CodeBorder { get; set; }
    }

    public class HtmlRun
    {
        public string Text { get; set; }
        public Color Color { get; set; }
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public string FontName { get; set; }
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
    /// - 文字色 #333333，背景 #FFFFFF，内边距 16px（此为浅色档默认；深色档由调用方传 TextStickerPalette）
    /// - 最大宽度 ≤ 屏幕宽度的 60%，超出自动换行
    /// </summary>
    public static class TextStickerRenderer
    {
        private const string FontName = "Microsoft YaHei UI";
        private const float FontSizePx = 18f;
        private const int Padding = 16;
        private static readonly Color TextColor = ColorTranslator.FromHtml("#333333");
        private static readonly Color BackColor = ColorTranslator.FromHtml("#FFFFFF");

        /// <summary>只比 RGB、忽略 alpha（与原先内联比较同口径）。</summary>
        private static bool SameColor(Color a, Color b)
            => a.R == b.R && a.G == b.G && a.B == b.B;

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
        /// <param name="palette">主题配色；null 或其中任一字段为 null 时沿用浅色默认字面量。</param>
        public static TextStickerResult Render(string text, int maxWidthPhysical, TextStickerPalette palette = null)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("文本为空。", nameof(text));
            if (maxWidthPhysical <= Padding * 2) maxWidthPhysical = Padding * 2 + 1;

            int availableWidth = maxWidthPhysical - Padding * 2;

            Color textColor = palette?.Text ?? TextColor;
            bool isCode = IsCodeOrFormatted(text);
            string fontName = isCode ? "Consolas" : FontName;
            Color backColor = isCode
                ? (palette?.CodeBack ?? ColorTranslator.FromHtml("#F8F9FA"))
                : (palette?.Back ?? BackColor);
            Color borderColor = isCode
                ? (palette?.CodeBorder ?? ColorTranslator.FromHtml("#E9ECEF"))
                : Color.Transparent;

            AppLogger.Info($"TextSticker.Render: isCode={isCode}, backColor=#{backColor.R:X2}{backColor.G:X2}{backColor.B:X2}, textColor=#{textColor.R:X2}{textColor.G:X2}{textColor.B:X2}, palette.Back={( palette?.Back.HasValue == true ? $"#{palette.Back.Value.R:X2}{palette.Back.Value.G:X2}{palette.Back.Value.B:X2}" : "null" )}, palette.Text={( palette?.Text.HasValue == true ? $"#{palette.Text.Value.R:X2}{palette.Text.Value.G:X2}{palette.Text.Value.B:X2}" : "null" )}, palette.CodeBack={( palette?.CodeBack.HasValue == true ? $"#{palette.CodeBack.Value.R:X2}{palette.CodeBack.Value.G:X2}{palette.CodeBack.Value.B:X2}" : "null" )}");

            using var font = new Font(fontName, FontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            // 先用一个临时 Graphics 测量；TextRenderer 在 GDI 下测量更准。
            using (var measureG = Graphics.FromHwnd(IntPtr.Zero))
            {
                measureG.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                measureG.SmoothingMode = SmoothingMode.AntiAlias;

                var wrapped = WrapText(measureG, font, text, availableWidth);
                // 使用 GenericTypographic + NoWrap，与 WrapText 的测量一致，避免 DrawString 再次换行
                var fmt = StringFormat.GenericTypographic;
                fmt = (StringFormat)fmt.Clone();
                fmt.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;

                // 测量所有行合成尺寸
                float lineHeight = font.GetHeight(measureG);
                float maxLineWidth = 0;
                var lineWidths = new System.Collections.Generic.List<float>();
                foreach (var line in wrapped)
                {
                    SizeF s = measureG.MeasureString(line, font, int.MaxValue, fmt);
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
                    using var brush = new SolidBrush(textColor);
                    g.DrawString(string.Join(Environment.NewLine, wrapped), font, brush, layout, fmt);
                }

                // 活控件用原始段落（硬换行），软换行交给 Avalonia 的 TextWrapping。
                // 位图仍用 wrapped 画（逐像素不变），拖边改 MaxWidth 时文字自然重排。
                var htmlLines = new System.Collections.Generic.List<HtmlLine>();
                string[] originalParagraphs = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                foreach (var paraStr in originalParagraphs)
                {
                    var run = new HtmlRun
                    {
                        Text = paraStr,
                        Color = textColor,
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
                    HtmlLines = htmlLines,
                    WrapWidthPhysical = availableWidth
                };

                return new TextStickerResult
                {
                    Bitmap = bmp,
                    LayoutInfo = layoutInfo
                };
            }
        }

        #nullable enable
        /// <summary>
        /// 按可用宽度做贪心换行，支持 CJK 字符逐字断行，保留行首缩进。
        /// </summary>
        /// <param name="format">
        /// 测量用的 <see cref="StringFormat"/>。缺省用裸 GenericTypographic（文字贴图原行为）。
        /// ⚠ 图片翻译回填必须传入自己那份（带 <c>NoClip|FitBlackBox</c>）：两套格式的测量结果不同，
        /// 用折行时的格式和落笔时的格式不一致就会出现「量得下、画不下」。
        /// 折行按逐词累加估宽，调用端仍须对成品行整行复测。
        /// </param>
        public static System.Collections.Generic.List<string> WrapText(Graphics g, Font font, string text, int maxWidth,
            StringFormat? format = null)
        {
            var result = new System.Collections.Generic.List<string>();
            var sf = format ?? StringFormat.GenericTypographic;
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

                // 测量缩进宽度
                float indentWidth = 0;
                if (leadingWhitespace.Length > 0)
                {
                    indentWidth = g.MeasureString(leadingWhitespace, font, PointF.Empty, sf).Width;
                }

                float currentWidth = indentWidth;

                foreach (var token in tokens)
                {
                    float tokenWidth = g.MeasureString(token, font, PointF.Empty, sf).Width;

                    if (currentWidth + tokenWidth > maxWidth && line.Length > leadingWhitespace.Length)
                    {
                        // 当前行已内容 + 新 token 超宽 → 换行
                        result.Add(line.ToString().TrimEnd());
                        line.Clear();
                        line.Append(leadingWhitespace);
                        // 换行后的 token 去掉前导空格
                        string trimmedToken = token.TrimStart(' ');
                        if (!string.IsNullOrEmpty(trimmedToken))
                        {
                            line.Append(trimmedToken);
                            currentWidth = indentWidth + g.MeasureString(trimmedToken, font, PointF.Empty, sf).Width;
                        }
                        else
                        {
                            currentWidth = indentWidth;
                        }
                    }
                    else
                    {
                        line.Append(token);
                        currentWidth += tokenWidth;
                    }
                }
                if (line.Length > leadingWhitespace.Length)
                {
                    result.Add(line.ToString().TrimEnd());
                }
                else if (line.Length > 0)
                {
                    result.Add(line.ToString());
                }
            }
            return result;
        }
        #nullable disable


        private class TextStyle
        {
            public Color Color { get; set; } = ColorTranslator.FromHtml("#333333");
            public string FontName { get; set; } = null;
            public bool IsBold { get; set; }
            public bool IsItalic { get; set; }
        }

        /// <summary>
        /// 标签栈的一帧：样式 + 该元素自带的背景色 + 嵌套深度。
        /// 背景色只有在其子树真的出现文字后才是画布候选，且越靠外者优先（见 ParseHtmlToLines）。
        /// </summary>
        private class StyleFrame
        {
            public TextStyle Style { get; set; } = new TextStyle();
            public Color? Background { get; set; }
            public int Depth { get; set; }
            public bool BackgroundClaimed { get; set; }
        }

        public static TextStickerResult RenderHtml(string htmlText, string plainTextFallback, int maxWidthPhysical, TextStickerPalette palette = null)
        {
            if (string.IsNullOrEmpty(htmlText))
            {
                return Render(plainTextFallback, maxWidthPhysical, palette);
            }

            if (maxWidthPhysical <= Padding * 2) maxWidthPhysical = Padding * 2 + 1;
            int availableWidth = maxWidthPhysical - Padding * 2;

            Color? overallBgColor = null;
            var parsedLines = ParseHtmlToLines(htmlText, out overallBgColor);
            AppLogger.Info($"TextSticker.RenderHtml: HTML 解析完成, parsedLines={parsedLines?.Count ?? 0}, overallBgColor={( overallBgColor.HasValue ? $"#{overallBgColor.Value.R:X2}{overallBgColor.Value.G:X2}{overallBgColor.Value.B:X2}" : "null" )}");
            if (parsedLines == null || parsedLines.Count == 0)
            {
                AppLogger.Warn("TextSticker.RenderHtml: 解析结果为空，fallback 到 Render(plainText)");
                return Render(plainTextFallback, maxWidthPhysical, palette);
            }

            bool isCode = IsCodeOrFormatted(plainTextFallback) || overallBgColor.HasValue || HasMultipleColors(parsedLines);
            string fontName = isCode ? "Consolas" : FontName;

            // 底色优先级（一条链，不许旁逸）：剪贴板 HTML 自带背景 > 调用方传入的主题色 > 浅色默认。
            // 从深色界面复制"无背景色"的文本时走第二档——此前这里恒为白，
            // 于是浅字色配白底，就是用户看到的"浅底白字"。
            string bgSource;
            Color backColor;
            if (overallBgColor.HasValue)
            {
                backColor = overallBgColor.Value;
                bgSource = "HTML自带背景";
            }
            else if (isCode)
            {
                backColor = palette?.CodeBack ?? ColorTranslator.FromHtml("#F8F9FA");
                bgSource = palette?.CodeBack.HasValue == true ? "palette.CodeBack" : "浅色默认";
            }
            else
            {
                backColor = palette?.Back ?? BackColor;
                bgSource = palette?.Back.HasValue == true ? "palette.Back" : "浅色默认";
            }

            // 剪贴板给的半透明背景先叠到主题底色上合成成一块实心画布：贴图要钉在桌面上，
            // 留着透明度等于让正文直接压在壁纸上；而旧写法"一律按不透明解读"会把
            // rgba(0,0,0,0.05) 这类淡层变成纯黑画布。合成既保住源色的明暗倾向，又保证画布实心可读。
            if (backColor.A != 255)
            {
                backColor = CompositeOver(backColor, palette?.Back ?? BackColor);
                bgSource += "+半透明合成";
            }

            double luminance = 0.299 * backColor.R + 0.587 * backColor.G + 0.114 * backColor.B;
            AppLogger.Info($"TextSticker.RenderHtml: 底色来源={bgSource}, backColor=#{backColor.R:X2}{backColor.G:X2}{backColor.B:X2}, luminance={luminance:F1}, isDark={luminance < 128}, isCode={isCode}, palette.Back={( palette?.Back.HasValue == true ? $"#{palette.Back.Value.R:X2}{palette.Back.Value.G:X2}{palette.Back.Value.B:X2}" : "null" )}, palette.Text={( palette?.Text.HasValue == true ? $"#{palette.Text.Value.R:X2}{palette.Text.Value.G:X2}{palette.Text.Value.B:X2}" : "null" )}");

            // 逐 run 对比度兜底：每个 run 的亮度若与底色同明同暗（都 <128 或都 ≥128），
            // 就翻到对面——浅底浅字和深底深字两个方向一起收口。
            // 富文本里跨亮度边界的颜色（红/蓝/绿等）不受影响，保留色系。
            Color defaultTextClr = luminance < 128
                ? ColorTranslator.FromHtml("#D4D4D4")
                : ColorTranslator.FromHtml("#333333");

            int totalRuns = 0, flippedRuns = 0;
            foreach (var line in parsedLines)
            {
                foreach (var run in line.Runs)
                {
                    totalRuns++;
                    double runLum = 0.299 * run.Color.R + 0.587 * run.Color.G + 0.114 * run.Color.B;
                    bool bgDark = luminance < 128;
                    bool runDark = runLum < 128;
                    if (bgDark == runDark)
                    {
                        flippedRuns++;
                        run.Color = defaultTextClr;
                    }
                }
            }
            AppLogger.Info($"TextSticker.RenderHtml: 对比度兜底完成, totalRuns={totalRuns}, flippedRuns={flippedRuns}, defaultTextClr=#{defaultTextClr.R:X2}{defaultTextClr.G:X2}{defaultTextClr.B:X2}");

            Color borderColor = Color.Transparent;
            if (isCode)
            {
                // 画布来自 HTML 时沿用按亮度推导的描边（浅色模式取值不变），
                // 画布来自主题时描边也走主题，两者不能串档。
                borderColor = overallBgColor.HasValue
                    ? (luminance < 128 ? Color.FromArgb(45, 45, 45) : Color.FromArgb(233, 236, 239))
                    : (palette?.CodeBorder ?? ColorTranslator.FromHtml("#E9ECEF"));
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
                    AppLogger.Warn("TextSticker.RenderHtml: 包裹后无行，fallback 到 Render(plainText)");
                    return Render(plainTextFallback, maxWidthPhysical, palette);
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

                        using var f = new Font(run.FontName ?? fontName, FontSizePx, style, GraphicsUnit.Pixel);
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

                            using var f = new Font(run.FontName ?? fontName, FontSizePx, style, GraphicsUnit.Pixel);
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
                    // 活控件用原始段落（硬换行），软换行交给 Avalonia 的 TextWrapping。
                    // 位图仍用 wrappedLines 画（逐像素不变），拖边改 MaxWidth 时文字自然重排。
                    HtmlLines = parsedLines,
                    WrapWidthPhysical = availableWidth
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

            var sf = StringFormat.GenericTypographic;
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
                indentWidth = g.MeasureString(leadingWhitespace, f, PointF.Empty, sf).Width;
            }

            // Apply indent to first line
            bool needIndent = indentWidth > 0;

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
                    float tokenWidth = g.MeasureString(token, f, PointF.Empty, sf).Width;

                    // Apply indentation at the start of a wrapped line
                    if (needIndent && currentX == 0 && indentWidth > 0)
                    {
                        currentWrapped.Runs.Add(new HtmlRun
                        {
                            Text = leadingWhitespace,
                            Color = run.Color,
                            IsBold = false,
                            IsItalic = false
                        });
                        currentX = indentWidth;
                        needIndent = false;

                        // Strip leading whitespace from token to avoid double indent
                        string modifiedToken = token;
                        if (modifiedToken.StartsWith(leadingWhitespace))
                        {
                            modifiedToken = modifiedToken.Substring(leadingWhitespace.Length);
                            if (string.IsNullOrEmpty(modifiedToken)) continue;
                            tokenWidth = g.MeasureString(modifiedToken, f, PointF.Empty, sf).Width;
                        }
                        else if (modifiedToken.TrimStart(' ').Length != modifiedToken.Length)
                        {
                            modifiedToken = modifiedToken.TrimStart(' ');
                            tokenWidth = g.MeasureString(modifiedToken, f, PointF.Empty, sf).Width;
                        }

                        // Check if even this single token exceeds maxWidth
                        if (currentX + tokenWidth > maxWidth)
                        {
                            // Token alone is too wide — start new line without indent
                            currentWrapped = new HtmlLine();
                            result.Add(currentWrapped);
                            currentX = 0;
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

                    // Normal wrap check: if adding this token exceeds maxWidth and line has content
                    if (currentX + tokenWidth > maxWidth && currentWrapped.Runs.Count > 0)
                    {
                        // Start new wrapped line
                        currentWrapped = new HtmlLine();
                        result.Add(currentWrapped);
                        currentX = 0;
                        needIndent = indentWidth > 0;

                        // Trim leading spaces for the wrapped token
                        string trimmedToken = token.TrimStart(' ');
                        if (string.IsNullOrEmpty(trimmedToken)) continue;

                        float trimmedWidth = g.MeasureString(trimmedToken, f, PointF.Empty, sf).Width;

                        currentWrapped.Runs.Add(new HtmlRun
                        {
                            Text = trimmedToken,
                            Color = run.Color,
                            IsBold = run.IsBold,
                            IsItalic = run.IsItalic
                        });
                        currentX = trimmedWidth;
                        needIndent = false;
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

            var styleStack = new System.Collections.Generic.Stack<StyleFrame>();
            styleStack.Push(new StyleFrame { Depth = 0 });

            // 画布色候选：每遇到一段文字，就把"从当前元素到最外层"这条链上尚未计入的祖先背景各计入一次，
            // 最终取深度最小（最靠外）的那个。这样正文容器的底色会赢过高亮块 / 行内徽章一类深处的背景，
            // 而"整段 HTML 里第一个 background 声明"这种踩法不再成立。
            Color? canvasBg = null;
            int canvasBgDepth = int.MaxValue;

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
                            ClaimCanvasBackgrounds(styleStack, ref canvasBg, ref canvasBgDepth);
                            var currentStyle = styleStack.Peek().Style;
                            currentLine.Runs.Add(new HtmlRun
                            {
                                Text = text,
                                Color = currentStyle.Color,
                                IsBold = currentStyle.IsBold,
                                IsItalic = currentStyle.IsItalic,
                                FontName = currentStyle.FontName
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
                                Color? frameBg = null;
                                if (styleStack.Count > 0)
                                {
                                    var parent = styleStack.Peek().Style;
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
                                            ParseCss(styleStr, nextStyle, out frameBg);
                                        }
                                    }
                                }

                                if (tagName == "b" || tagName == "strong") nextStyle.IsBold = true;
                                if (tagName == "i" || tagName == "em") nextStyle.IsItalic = true;
                                if (tagName == "code") nextStyle.FontName = "Consolas";

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
                                    styleStack.Push(new StyleFrame
                                    {
                                        Style = nextStyle,
                                        Background = frameBg,
                                        Depth = styleStack.Count
                                    });
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
                            ClaimCanvasBackgrounds(styleStack, ref canvasBg, ref canvasBgDepth);
                            var currentStyle = styleStack.Peek().Style;
                            currentLine.Runs.Add(new HtmlRun
                            {
                                Text = text,
                                Color = currentStyle.Color,
                                IsBold = currentStyle.IsBold,
                                IsItalic = currentStyle.IsItalic,
                                FontName = currentStyle.FontName
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
                    ClaimCanvasBackgrounds(styleStack, ref canvasBg, ref canvasBgDepth);
                    var currentStyle = styleStack.Peek().Style;
                    currentLine.Runs.Add(new HtmlRun
                    {
                        Text = text,
                        Color = currentStyle.Color,
                        IsBold = currentStyle.IsBold,
                        IsItalic = currentStyle.IsItalic,
                        FontName = currentStyle.FontName
                    });
                }
            }

            overallBgColor = canvasBg;

            while (lines.Count > 1 && lines[lines.Count - 1].Runs.Count == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        /// <summary>
        /// 出现文字时，把从当前元素到最外层这条链上尚未计入的背景色登记为画布候选，越靠外者优先。
        /// 只在"其子树真的有文字"的元素里选，避免没装正文的外层壳（页眉、分隔块）把画布色带偏。
        /// </summary>
        private static void ClaimCanvasBackgrounds(
            System.Collections.Generic.Stack<StyleFrame> stack, ref Color? best, ref int bestDepth)
        {
            foreach (var frame in stack)
            {
                // 每次登记都走完整条链，故某个帧已登记时它的所有祖先也都登记了，可以直接停。
                if (frame.BackgroundClaimed) break;
                frame.BackgroundClaimed = true;

                if (frame.Background.HasValue && frame.Depth < bestDepth)
                {
                    best = frame.Background;
                    bestDepth = frame.Depth;
                }
            }
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

        private static void ParseCss(string styleStr, TextStyle style, out Color? background)
        {
            background = null;
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
                    // A==0（transparent / rgba(...,0)）的语义是"这块没有背景"，不能当画布候选：
                    // 旧写法把它连同透明度一起按不透明解读，深色画布就凭空冒出来。
                    var c = ParseColor(val);
                    if (c.HasValue && c.Value.A > 0 && !background.HasValue)
                    {
                        background = c.Value;
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
                else if (key == "font-family")
                {
                    string cleanFont = val.Trim().Trim('\'', '"');
                    if (!string.IsNullOrEmpty(cleanFont))
                    {
                        style.FontName = cleanFont;
                    }
                }
            }
        }

        /// <summary>
        /// 半透明色按 src-over 叠到底色上，返回不透明的合成色。文本贴图两条路由共用同一口径：
        /// 钉在桌面上的画布必须是实心表面，所以半透明源色一律合成而非强制不透明或直接留透明。
        /// </summary>
        public static Color CompositeOver(Color src, Color dst)
        {
            double a = src.A / 255.0;
            return Color.FromArgb(255,
                (int)Math.Round(src.R * a + dst.R * (1 - a)),
                (int)Math.Round(src.G * a + dst.G * (1 - a)),
                (int)Math.Round(src.B * a + dst.B * (1 - a)));
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
                    // CSS 的 8 位是 #RRGGBBAA，而 ColorTranslator 按 GDI 的 #AARRGGBB 解析，
                    // 通道顺序相反——不自己拆的话透明度会被当成红通道。
                    if (colorStr.Length == 9)
                    {
                        int rr = Convert.ToInt32(colorStr.Substring(1, 2), 16);
                        int gg = Convert.ToInt32(colorStr.Substring(3, 2), 16);
                        int bb = Convert.ToInt32(colorStr.Substring(5, 2), 16);
                        int aa = Convert.ToInt32(colorStr.Substring(7, 2), 16);
                        return Color.FromArgb(aa, rr, gg, bb);
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
                            // rgba() 的第四个参数是 0~1 的透明度，此前被整个丢掉＝一律不透明。
                            int alpha = 255;
                            if (parts.Length >= 4 &&
                                double.TryParse(parts[3].Trim().TrimStart('/'), out double a) && a >= 0 && a <= 1)
                            {
                                alpha = (int)Math.Round(a * 255.0);
                            }
                            return Color.FromArgb(alpha, r, g, b);
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

        /// <summary>
        /// 文本分词：CJK 字符逐字拆分（每个字可独立换行），ASCII 空格分词，标点跟随前一个词。
        /// 这样中文文本可以在任意字符处断行，英文文本在空格处断行。
        /// </summary>
        private static System.Collections.Generic.List<string> TokenizeText(string text)
        {
            var tokens = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;

            int i = 0;
            int len = text.Length;
            var current = new System.Text.StringBuilder();

            while (i < len)
            {
                char c = text[i];

                if (c == ' ' || c == '\t')
                {
                    // 空格/制表符：先输出已累积的词，再输出空格串
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    while (i < len && (text[i] == ' ' || text[i] == '\t'))
                    {
                        current.Append(text[i]);
                        i++;
                    }
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                else if (IsCjkCharacter(c))
                {
                    // CJK 字符：先输出已累积的 ASCII 词，再逐字输出
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    tokens.Add(c.ToString());
                    i++;
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

        /// <summary>判断字符是否为 CJK（中日韩）字符，这类字符可在任意位置断行。</summary>
        private static bool IsCjkCharacter(char c)
        {
            // CJK 统一表意文字（常用汉字、日文汉字等）
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            // CJK 扩展 A
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            // CJK 兼容表意文字
            if (c >= 0xF900 && c <= 0xFAFF) return true;
            // 日文平假名
            if (c >= 0x3040 && c <= 0x309F) return true;
            // 日文片假名
            if (c >= 0x30A0 && c <= 0x30FF) return true;
            // 韩文音节
            if (c >= 0xAC00 && c <= 0xD7AF) return true;
            // 全角标点（、。！？等）
            if (c >= 0x3000 && c <= 0x303F) return true;
            // 全角字符（！＂＃等）
            if (c >= 0xFF00 && c <= 0xFFEF) return true;
            return false;
        }

        public static bool HasMultipleColors(System.Collections.Generic.List<HtmlLine> lines)
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

#nullable enable
        /// <summary>
        /// 快速检测文本是否包含 Markdown 语法。
        /// </summary>
        public static bool IsMarkdown([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            // 检查常见的 Markdown 行内标记
            if (text.Contains("**") || text.Contains("__")) return true;
            if (text.Contains("```") || (text.Contains("`") && text.IndexOf('`') != text.LastIndexOf('`'))) return true;
            // 检查 Markdown 表格标记
            if (text.Contains("|---") || text.Contains("| ---") || text.Contains("|:---") || text.Contains("---|")) return true;
            
            // 检查行首的 Markdown 标记
            foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("# ") || trimmed.StartsWith("## ") || trimmed.StartsWith("### ") ||
                    trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("> ")) return true;
            }
            return false;
        }
#nullable disable

        /// <summary>
        /// 将 Markdown 文本转换为带内联样式的 HTML，供 RenderHtml 使用。
        /// 支持：**粗体**、*斜体*、`行内代码`、标题 (#)
        /// </summary>
        public static string ConvertMarkdownToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return string.Empty;

            var sb = new System.Text.StringBuilder();
            string[] lines = markdown.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            bool inParagraph = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                // 空行 → 段落结束/分隔
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    if (inParagraph)
                    {
                        sb.Append("</div>");
                        inParagraph = false;
                    }
                    continue;
                }

                // 标题： # 标题（自成一段）
                if (trimmed.StartsWith("### ") || trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
                {
                    if (inParagraph)
                    {
                        sb.Append("</div>");
                        inParagraph = false;
                    }
                    string prefix = trimmed.StartsWith("### ") ? "### " :
                                    trimmed.StartsWith("## ") ? "## " : "# ";
                    string content = ProcessInlineMarkdown(trimmed.Substring(prefix.Length));
                    sb.Append("<div style=\"font-weight: bold;\">").Append(content).Append("</div>");
                    continue;
                }

                // 代码块 ```...```
                if (trimmed.StartsWith("```"))
                {
                    if (inParagraph)
                    {
                        sb.Append("</div>");
                        inParagraph = false;
                    }
                    int endIdx = i + 1;
                    while (endIdx < lines.Length && !lines[endIdx].TrimStart().StartsWith("```"))
                        endIdx++;
                    sb.Append("<pre>");
                    for (int j = i + 1; j < endIdx; j++)
                    {
                        if (j > i + 1) sb.Append("\n");
                        sb.Append(System.Net.WebUtility.HtmlEncode(lines[j]));
                    }
                    sb.Append("</pre>");
                    i = endIdx;
                    continue;
                }

                // 普通行：放入段落（同一段内用 <br /> 换行）
                if (!inParagraph)
                {
                    sb.Append("<div>");
                    inParagraph = true;
                }
                else
                {
                    sb.Append("<br />");
                }
                sb.Append(ProcessInlineMarkdown(line));
            }

            // 关闭最后一个段落
            if (inParagraph)
                sb.Append("</div>");

            return sb.ToString();
        }

        /// <summary>
        /// 处理行内 Markdown：**粗体** *斜体* `行内代码`
        /// </summary>
        private static string ProcessInlineMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 先转义 HTML 特殊字符
            string escaped = System.Net.WebUtility.HtmlEncode(text);

            // 处理 ***粗斜体*** (bold + italic)
            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, @"\*\*\*(.+?)\*\*\*",
                "<span style=\"font-weight: bold; font-style: italic;\">$1</span>");

            // 处理 **粗体**
            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, @"\*\*(.+?)\*\*",
                "<span style=\"font-weight: bold;\">$1</span>");

            // 处理 *斜体* (只在左右都有非字母或边界时匹配，避免与普通星号混淆)
            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, @"(?<!\w)\*(.+?)\*(?!\w)",
                "<span style=\"font-style: italic;\">$1</span>");

            // 处理 `行内代码`
            escaped = System.Text.RegularExpressions.Regex.Replace(
                escaped, @"`(.+?)`",
                "<code>$1</code>");

            return escaped;
        }
    }
}
