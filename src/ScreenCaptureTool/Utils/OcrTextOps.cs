using System;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCaptureTool.Utils;

/// <summary>
/// 文本算子结果：处理后的新文本 + 实际处理处数（0 处表示原文无匹配，调用方保持原文并提示）。
/// </summary>
public sealed class TextOpResult
{
    public string Text { get; }
    public int Count { get; }
    public TextOpResult(string text, int count)
    {
        Text = text;
        Count = count;
    }
}

/// <summary>
/// OCR 文本清除/校对纯函数算子集（与 ocr-text-tools.html 原型逐条对应）。
/// 全部为"输入文本 → 新文本 + 处理处数"的确定性纯函数：相同输入必产生相同输出，
/// 供合并视图浮动工具栏（清除/校对菜单）与智能排版符号风格统一共用。
/// </summary>
public static class OcrTextOps
{
    // ═══════════ 清除类（4 项） ═══════════

    /// <summary>清除空格：半角/不间断/窄空格/全角空格 → 移除。</summary>
    public static TextOpResult ClearSpaces(string text) =>
        Replace(text, new Regex("[\\u0020\\u00A0\\u202F\\u3000]"), string.Empty);

    /// <summary>清除空白字符：空格/制表（含全角）删除 + 连续空行折叠为单个换行。</summary>
    public static TextOpResult ClearWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int count = 0;
        // 空格/制表/全角空格逐个删除
        var noSpaces = new Regex("[ \\t\\u00A0\\u202F\\u3000]").Replace(text, _ =>
        {
            count++;
            return string.Empty;
        });
        // 连续换行（空行）折叠为单个换行
        var folded = new Regex("(?:\\r\\n|\\r|\\n)+").Replace(noSpaces, m =>
        {
            if (m.Value.Length > 1) count++;
            return "\n";
        });
        return new TextOpResult(folded, count);
    }

    /// <summary>清除零宽字符：U+200B~200D / U+2060 / U+FEFF / U+180E → 移除。</summary>
    public static TextOpResult ClearZeroWidth(string text) =>
        Replace(text, new Regex("[\\u200B-\\u200D\\u2060\\uFEFF\\u180E]"), string.Empty);

    /// <summary>清除括号内容：（）「」『』() 及其中内容整体删除（不含嵌套）。</summary>
    public static TextOpResult ClearParenthesizedContent(string text) =>
        Replace(text, new Regex("（[^（）]*）|\\([^()（）]*\\)|「[^「」]*」|『[^『』]*』"), string.Empty);

    // ═══════════ 校对类（4 组） ═══════════

    /// <summary>
    /// 数字样式还原：⓪①~⑳㉑~㊿（带圈）与 ⑴~⑳（带括号）→ 半角数字。
    /// （原「清除带圈数字 / 清除带括号数字」两项合并：识别为数字而非删除）。
    /// </summary>
    public static TextOpResult RestoreDigits(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int count = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            int n = ch switch
            {
                >= '\u24EA' and <= '\u24EA' => ch - 0x24EA,                     // ⓪ → 0
                >= '\u2460' and <= '\u2473' => ch - 0x2460 + 1,                 // ①~⑳ → 1~20
                >= '\u3251' and <= '\u325F' => ch - 0x3251 + 21,                // ㉑~㉟ → 21~35
                >= '\u32B1' and <= '\u32BF' => ch - 0x32B1 + 36,                // ㊱~㊿ → 36~50
                >= '\u2474' and <= '\u2487' => ch - 0x2474 + 1,                 // ⑴~⑳ → 1~20
                _ => -1,
            };
            if (n >= 0)
            {
                count++;
                sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(ch);
            }
        }
        return new TextOpResult(sb.ToString(), count);
    }

    /// <summary>引号风格：统一为直引号（"…" '…' → "…" '…'）。</summary>
    public static TextOpResult ToStraightQuotes(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u201C':   // "
                case '\u201D':   // "
                case '"':
                    c++; sb.Append('"'); break;
                case '\u2018':   // '
                case '\u2019':   // '
                    c++; sb.Append('\''); break;
                default:
                    sb.Append(ch); break;
            }
        }
        return new TextOpResult(sb.ToString(), c);
    }

    /// <summary>引号风格：统一为弯引号（配对交替 "… " '… '）。</summary>
    public static TextOpResult ToCurlyQuotes(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0, dq = 0, sq = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u201C':
                case '\u201D':
                case '"':
                    c++; sb.Append(dq++ % 2 == 0 ? '\u201C' : '\u201D'); break;
                case '\u2018':
                case '\u2019':
                    c++; sb.Append(sq++ % 2 == 0 ? '\u2018' : '\u2019'); break;
                default:
                    sb.Append(ch); break;
            }
        }
        return new TextOpResult(sb.ToString(), c);
    }

    /// <summary>引号风格：统一为直角引号（"…" → 「…」、'…' → 『…』，配对交替）。</summary>
    public static TextOpResult ToCornerQuotes(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0, dq = 0, sq = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u201C':
                case '\u201D':
                case '"':
                    c++; sb.Append(dq++ % 2 == 0 ? '\u300C' : '\u300D'); break;   // 「 」
                case '\u2018':
                case '\u2019':
                    c++; sb.Append(sq++ % 2 == 0 ? '\u300E' : '\u300F'); break;   // 『 』
                default:
                    sb.Append(ch); break;
            }
        }
        return new TextOpResult(sb.ToString(), c);
    }

    /// <summary>括号风格：（） → ()。</summary>
    public static TextOpResult ToEnglishParentheses(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\uFF08') { c++; sb.Append('('); }
            else if (ch == '\uFF09') { c++; sb.Append(')'); }
            else sb.Append(ch);
        }
        return new TextOpResult(sb.ToString(), c);
    }

    /// <summary>括号风格：() → （）（全角括号与相邻中文之间吞掉多余空格，英文侧保留）。</summary>
    public static TextOpResult ToChineseParentheses(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '(')
            {
                c++;
                // 括号前是「中文 + 空格」→ 吞空格（OCR 半角括号常带多余空隙：层 （此前）
                if (sb.Length >= 2 && sb[^1] == ' ' && IsCjkChar(sb[^2])) sb.Length--;
                sb.Append('\uFF08');
            }
            else if (ch == ')')
            {
                c++;
                sb.Append('\uFF09');
                // 括号后是「空格 + 中文」→ 吞空格
                if (i + 2 < text.Length && text[i + 1] == ' ' && IsCjkChar(text[i + 2])) i++;
            }
            else sb.Append(ch);
        }
        return new TextOpResult(sb.ToString(), c);
    }

    /// <summary>CJK 字符（表意文字 + 全角符号区），用于全角括号贴紧判定。</summary>
    private static bool IsCjkChar(char c) =>
        (c >= '\u4e00' && c <= '\u9fff') || (c >= '\u3000' && c <= '\u303f') || (c >= '\uff00' && c <= '\uffef');

    /// <summary>标点风格：, ; : ! ? → ，；：！？，句末 . → 。（小数点保护）。</summary>
    public static TextOpResult ToChinesePunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0;
        // 先保护小数点（数字.数字），避免 3.14 被误改
        var protectedText = new Regex("(\\d)\\.(\\d)").Replace(text, "$1\u0001$2");
        var sb = new StringBuilder(protectedText.Length);
        for (int i = 0; i < protectedText.Length; i++)
        {
            var ch = protectedText[i];
            switch (ch)
            {
                case ',': c++; sb.Append('\uFF0C'); break;
                case ';': c++; sb.Append('\uFF1B'); break;
                case ':': c++; sb.Append('\uFF1A'); break;
                case '!': c++; sb.Append('\uFF01'); break;
                case '?': c++; sb.Append('\uFF1F'); break;
                case '.':
                    // 句末点（后跟空白或行尾）→ 。
                    if (i == protectedText.Length - 1 || char.IsWhiteSpace(protectedText[i + 1]))
                    {
                        c++; sb.Append('\u3002');
                    }
                    else sb.Append(ch);
                    break;
                default: sb.Append(ch); break;
            }
        }
        return new TextOpResult(sb.ToString().Replace('\u0001', '.'), c);
    }

    /// <summary>标点风格：，；：！？。 → , ; : ! ? .（千分位逗号保护）。</summary>
    public static TextOpResult ToEnglishPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int c = 0;
        // 先保护数字千分位逗号（1,234），避免被改回半角时误伤（本就直接半角，仅示意保护位）
        var protectedText = new Regex("(\\d)\uFF0C(\\d)").Replace(text, "$1\u0001$2");
        var sb = new StringBuilder(protectedText.Length);
        foreach (var ch in protectedText)
        {
            switch (ch)
            {
                case '\uFF0C': c++; sb.Append(','); break;
                case '\uFF1B': c++; sb.Append(';'); break;
                case '\uFF1A': c++; sb.Append(':'); break;
                case '\uFF01': c++; sb.Append('!'); break;
                case '\uFF1F': c++; sb.Append('?'); break;
                case '\u3002': c++; sb.Append('.'); break;
                default: sb.Append(ch); break;
            }
        }
        return new TextOpResult(sb.ToString().Replace('\u0001', ','), c);
    }

    // ═══════════ 内部工具 ═══════════

    /// <summary>正则替换并统计替换处数。</summary>
    private static TextOpResult Replace(string text, Regex regex, string replacement)
    {
        if (string.IsNullOrEmpty(text)) return new TextOpResult(text, 0);
        int count = 0;
        var result = regex.Replace(text, _ =>
        {
            count++;
            return replacement;
        });
        return new TextOpResult(result, count);
    }
}
