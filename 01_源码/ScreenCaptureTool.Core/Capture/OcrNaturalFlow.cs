using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCaptureTool.Core.Capture;

/// <summary>
/// OCR 启发式自然语句流与智能段落换行重排器。
/// </summary>
public static class OcrNaturalFlow
{
    private static readonly Regex ClosePunctHeadRegex = new(@"^[，。！？；：、）》”』】\],.!?;:\)]", RegexOptions.Compiled);
    private static readonly Regex BulletMarkerHeadRegex = new(@"^\s*(?:[•·▪‣◦●]\s*|[-–—*]\s+)", RegexOptions.Compiled);
    private static readonly Regex OrderedMarkerHeadRegex = new(@"^\s*(?:\d{1,3}[.)、。．](?!\d)|[（(][一二三四五六七八九十百\d]+[）)]|[一二三四五六七八九十]+[、.．]|\[\d{1,3}\]|【\d{1,3}】|[①-⑳])\s*", RegexOptions.Compiled);
    private static readonly Regex HyphenTailRegex = new(@"[A-Za-z]-$", RegexOptions.Compiled);
    private static readonly Regex LowerHeadRegex = new(@"^[a-z]", RegexOptions.Compiled);
    private static readonly Regex CjkToAlnumRegex = new(@"([一-龥])([A-Za-z0-9@#$])", RegexOptions.Compiled);
    private static readonly Regex AlnumToCjkRegex = new(@"([A-Za-z0-9%)])([一-龥])", RegexOptions.Compiled);
    private static readonly HashSet<string> CommonAbbrs = new(StringComparer.OrdinalIgnoreCase) { "mr.", "mrs.", "ms.", "dr.", "prof.", "vs.", "etc.", "e.g.", "i.e.", "no.", "fig.", "inc.", "co.", "ltd." };

    public static string FormatNaturalText(IReadOnlyList<OcrResultBox>? boxes)
    {
        if (boxes == null || boxes.Count == 0) return string.Empty;
        var validBoxes = new List<OcrResultBox>(boxes.Count);
        foreach (var b in boxes) if (!string.IsNullOrWhiteSpace(b.Text)) validBoxes.Add(b);
        if (validBoxes.Count == 0) return string.Empty;
        if (validBoxes.Count == 1) return JoinParagraphLines(new[] { validBoxes[0].Text });

        float medH = CalculateMedianHeight(validBoxes);
        float medGap = CalculateMedianGap(validBoxes);
        var paragraphs = GroupBoxesIntoParagraphs(validBoxes, medH, medGap);

        var sb = new StringBuilder();
        foreach (var para in paragraphs)
        {
            if (para.Count == 0) continue;
            string paraText = JoinParagraphLines(para.Select(b => b.Text));
            if (string.IsNullOrWhiteSpace(paraText)) continue;
            if (sb.Length > 0) sb.Append((char)10);
            sb.Append(paraText);
        }
        return sb.ToString();
    }

    private static List<List<OcrResultBox>> GroupBoxesIntoParagraphs(List<OcrResultBox> boxes, float medH, float medGap)
    {
        var paragraphs = new List<List<OcrResultBox>>();
        var currentPara = new List<OcrResultBox> { boxes[0] };
        for (int i = 1; i < boxes.Count; i++)
        {
            var prev = boxes[i - 1];
            var curr = boxes[i];
            if (ShouldBreakParagraph(prev, curr, medH, medGap))
            {
                paragraphs.Add(currentPara);
                currentPara = new List<OcrResultBox> { curr };
            }
            else
            {
                currentPara.Add(curr);
            }
        }
        if (currentPara.Count > 0) paragraphs.Add(currentPara);
        return paragraphs;
    }

    private static bool ShouldBreakParagraph(OcrResultBox prev, OcrResultBox curr, float medH, float medGap)
    {
        string prevText = prev.Text.Trim();
        string currText = curr.Text.Trim();
        if (prevText.Length == 0 || currText.Length == 0) return true;
        if (ClosePunctHeadRegex.IsMatch(currText)) return false;
        if (HasListMarker(currText)) return true;

        float gapY = curr.Bounds.Top - prev.Bounds.Bottom;
        float maxAllowedGap = Math.Max(medH * 1.35f, Math.Max(medGap * 2.2f, 18f));
        if (gapY > maxAllowedGap) return true;

        float prevLeft = prev.Bounds.Left, prevRight = prev.Bounds.Right;
        float currLeft = curr.Bounds.Left, currRight = curr.Bounds.Right;
        float overlapX = Math.Max(0f, Math.Min(prevRight, currRight) - Math.Max(prevLeft, currLeft));
        float minW = Math.Min(prev.Bounds.Width, curr.Bounds.Width);
        if (minW > 20f && overlapX / minW < 0.12f && gapY < medH * 0.5f) return true;

        if (IsLikelyHeading(prev, curr, prevText, currText, medH)) return true;
        if (IsSentenceEnd(prevText))
        {
            if (gapY > Math.Max(medGap * 1.15f, 6f) || curr.Bounds.Left > prev.Bounds.Left + medH * 0.8f) return true;
            char nextFirst = currText[0];
            if (IsCjk(nextFirst) || char.IsUpper(nextFirst)) return true;
        }
        return false;
    }

    public static bool HasListMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return BulletMarkerHeadRegex.IsMatch(text) || OrderedMarkerHeadRegex.IsMatch(text);
    }

    private static bool IsSentenceEnd(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        char last = text[^1];
        if (last == '。' || last == '！' || last == '？' || last == '…') return true;
        if ((last == '”' || last == '’' || last == '」' || last == '』' || last == '）' || last == ')' || last == '"') && text.Length >= 2)
        {
            char prevChar = text[^2];
            if (prevChar == '。' || prevChar == '！' || prevChar == '？' || prevChar == '…' || prevChar == '.' || prevChar == '!' || prevChar == '?') return true;
        }
        if (last == '!' || last == '?') return true;
        if (last == '.')
        {
            if (text.Length >= 2 && char.IsDigit(text[^2])) return false;
            int spaceIdx = text.LastIndexOf(' ');
            string lastWord = spaceIdx >= 0 ? text[(spaceIdx + 1)..] : text;
            if (CommonAbbrs.Contains(lastWord)) return false;
            if (lastWord.StartsWith("http", StringComparison.OrdinalIgnoreCase) || lastWord.Contains("/") || lastWord.Contains((char)92)) return false;
            return true;
        }
        return false;
    }

    private static bool IsLikelyHeading(OcrResultBox prev, OcrResultBox curr, string prevText, string currText, float medH)
    {
        if (prevText.Length > 16) return false;
        char last = prevText[^1];
        if (last == '，' || last == '、' || last == ',' || last == '-' || last == '—' || last == '：' || last == ':') return false;
        if (prev.Bounds.Height >= medH * 1.22f) return true;
        bool hasPunct = prevText.Any(c => char.IsPunctuation(c) || char.IsSymbol(c));
        if (!hasPunct && prevText.Length <= 10 && currText.Length >= 15) return true;
        return false;
    }

    public static string JoinParagraphLines(IEnumerable<string> lines)
    {
        if (lines == null) return string.Empty;
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        if (list.Count == 0) return string.Empty;
        if (list.Count == 1) return ApplySpacing(list[0]);

        var acc = new StringBuilder(list[0]);
        for (int k = 1; k < list.Count; k++)
        {
            string s = list[k];
            if (s.Length == 0) continue;
            if (ClosePunctHeadRegex.IsMatch(s)) { acc.Append(s); continue; }

            char last = acc.Length > 0 ? acc[^1] : (char)0;
            char first = s[0];
            if (last != (char)0 && IsCjk(last) && IsCjk(first)) acc.Append(s);
            else if (last != (char)0 && HyphenTailRegex.IsMatch(acc.ToString()) && LowerHeadRegex.IsMatch(s)) { acc.Length -= 1; acc.Append(s); }
            else if (last == ' ') acc.Append(s);
            else if (last != (char)0 && (IsCjkPunct(last) || IsCjkPunct(first))) acc.Append(s);
            else acc.Append(' ').Append(s);
        }
        return ApplySpacing(acc.ToString());
    }

    private static bool IsCjk(char c) =>
        (c >= '一' && c <= '鿿') ||
        (c >= '㐀' && c <= '䶿') ||
        (c >= '　' && c <= '〿') ||
        (c >= '＀' && c <= '￯') ||
        c == '—' || c == '…' || c == '《' || c == '》' ||
        c == '“' || c == '”' || c == '‘' || c == '’';

    private static bool IsCjkPunct(char c) =>
        c == '，' || c == '。' || c == '！' || c == '？' || c == '；' || c == '：' ||
        c == '、' || c == '（' || c == '）' || c == '《' || c == '》' || c == '【' ||
        c == '】' || c == '“' || c == '”' || c == '‘' || c == '’' || c == '「' ||
        c == '」' || c == '『' || c == '』' || c == '—' || c == '…';

    private static string ApplySpacing(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 只在两侧字符之间插入空格，不能把整个匹配（含识别出的两个字符）替换掉。
        return CjkToAlnumRegex.Replace(AlnumToCjkRegex.Replace(text, "$1 $2"), "$1 $2");
    }

    private static float CalculateMedianHeight(List<OcrResultBox> boxes)
    { 
        var heights = boxes.Select(b => b.Bounds.Height).Where(h => h > 2f).OrderBy(h => h).ToList();
        if (heights.Count == 0) return 20f;
        return heights[heights.Count / 2];
    }

    private static float CalculateMedianGap(List<OcrResultBox> boxes)
    {
        var gaps = new List<float>();
        for (int i = 1; i < boxes.Count; i++)
        {
            float diff = boxes[i].Bounds.Top - boxes[i - 1].Bounds.Bottom;
            if (diff >= 0f && diff < 100f) gaps.Add(diff);
        }
        if (gaps.Count == 0) return 6f;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }
}
