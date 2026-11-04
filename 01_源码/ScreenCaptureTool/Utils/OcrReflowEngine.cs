using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCaptureTool.Utils;

/// <summary>
/// 排版块类型：标题 / 段落 / 列表 / 独立行 / 代码块 / 引用块。
/// </summary>
public enum ReflowBlockType
{
    Title,
    Para,
    List,
    Line,
    /// <summary>代码块（文本保留原分行，Markdown 转代码围栏）。</summary>
    Code,
    /// <summary>引用块（Markdown 转 &gt; 前缀）。</summary>
    Quote
}

/// <summary>
/// 排版块：类型 + 行号引用（Sources 指向行项列表索引）+ 拼接文本 + 结构属性 + 列表项分组。
/// AI 排版路径只产骨架（Text 为空，由调用方按行项当前文本统一拼接）。
/// </summary>
public sealed class ReflowBlock
{
    public ReflowBlockType Type { get; }
    /// <summary>块内行号引用（指向行项列表，0 基）。</summary>
    public int[] Sources { get; }
    /// <summary>块文本（AI 路径由本地按行项当前文本拼接，非 LLM 输出）。</summary>
    public string Text { get; set; }
    /// <summary>大标题（行高 ≥ 1.22× 中位的 H1 级；AI 未给 level 时的几何回退证据）。</summary>
    public bool IsBigTitle { get; }
    /// <summary>标题层级 1/2/3（AI 判定；0 = 未分级，渲染时回退：IsBigTitle→1 否则→2）。</summary>
    public int Level { get; init; }
    /// <summary>有序列表（仅 List：true = 1. 编号式，false = 圆点式）。</summary>
    public bool Ordered { get; init; }
    /// <summary>列表嵌套深度 0/1/2（仅 List）。</summary>
    public int Depth { get; init; }
    /// <summary>代码语言标识（仅 Code；null = 未指定 → 无高亮围栏）。</summary>
    public string? Lang { get; init; }
    /// <summary>列表块的项分组：每项 = (行号引用, 项文本, 项级嵌套深度)；非列表块为 null。</summary>
    public List<(int[] Sources, string Text, int Depth)>? Items { get; }

    public ReflowBlock(ReflowBlockType type, IEnumerable<int> sources, string text, bool IsBigTitle)
        : this(type, sources, text, IsBigTitle, null)
    {
    }

    public ReflowBlock(ReflowBlockType type, IEnumerable<int> sources, string text, bool IsBigTitle,
        List<(int[] Sources, string Text, int Depth)>? items)
    {
        Type = type;
        Sources = sources?.ToArray() ?? Array.Empty<int>();
        Text = text ?? string.Empty;
        this.IsBigTitle = IsBigTitle;
        Items = items;
    }
}

/// <summary>
/// 排版结果：块序列（按文档顺序）。
/// 仅承载 AI 排版决策结果的骨架（规则引擎字段 Conservative/ParaThreshold/MedianHeight 已随规则引擎移除）。
/// </summary>
public sealed class ReflowResult
{
    public List<ReflowBlock> Blocks { get; }

    public ReflowResult(List<ReflowBlock> blocks)
    {
        Blocks = blocks ?? new List<ReflowBlock>();
    }
}

/// <summary>
/// OCR 智能排版共享工具：承载 AI 排版决策结果的数据模型（ReflowBlock/ReflowResult）
/// + 段内多行合并 JoinLines。AI 排版路径按行号引用拼接块文本时复用本类。
/// （原本地规则引擎分析链路已按"仅 AI"需求移除，详见 docs/CHANGELOG.md。）
/// </summary>
public static class OcrReflowEngine
{
    /// <summary>行首悬挂标点（断行掉落的闭合标点，需无缝回移上一行行尾）。</summary>
    private static readonly Regex ClosePunctRegex = new("^[，。！？；：、）》”』…]", RegexOptions.Compiled);

    /// <summary>连字符断词：累积文本以「字母-」结尾。</summary>
    private static readonly Regex HyphenTailRegex = new("[A-Za-z]-$", RegexOptions.Compiled);

    /// <summary>行首小写英文字母（连字符断词还原的下半段特征）。</summary>
    private static readonly Regex LowerHeadRegex = new("^[a-z]", RegexOptions.Compiled);

    /// <summary>中文-英文/数字间补空格（盘古之白）：中文字符后接英数符号。</summary>
    private static readonly Regex CjkToAlnumRegex = new("([\\u4e00-\\u9fff])([A-Za-z0-9@#$])", RegexOptions.Compiled);

    /// <summary>英文/数字后接中文：反向补空格。</summary>
    private static readonly Regex AlnumToCjkRegex = new("([A-Za-z0-9%)])([\\u4e00-\\u9fff])", RegexOptions.Compiled);

    /// <summary>中文字符判定（CJK 统一表意 + 中文标点区 + 全角符号 + 破折号省略号书名号引号）。</summary>
    private static bool IsCjk(char c) =>
        (c >= '\u4e00' && c <= '\u9fff') ||
        (c >= '\u3000' && c <= '\u303f') ||
        (c >= '\uff00' && c <= '\uffef') ||
        c == '—' || c == '…' || c == '《' || c == '》' ||
        c == '\u201C' || c == '\u201D' || c == '\u2018' || c == '\u2019';

    /// <summary>
    /// 段内多行合并：悬挂标点回移（行首闭合标点无缝接到上一行行尾）+ 中文无缝拼接 /
    /// 英文侧单空格 + 连字符断词还原 + 中英文间补空格（盘古之白）。
    /// 与 ocr-smart-reflow.html 原型 joinLines 逐条对应。
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        // 行规范化 Trim：OCR 行首尾的噪声空白去除（尾随空格会残留进拼接文本造成「完成/ 失败」类脏空格）
        var list = lines?.Where(l => l != null).Select(l => l.Trim()).ToList();
        if (list == null || list.Count == 0) return string.Empty;
        if (list.Count == 1) return ApplySpacing(list[0]);

        var acc = new StringBuilder(list[0]);
        for (int k = 1; k < list.Count; k++)
        {
            string s = list[k];
            if (s.Length == 0) continue;

            // 行首悬挂标点 → 无缝回移（OCR 断行时闭合标点掉到下一行行首）
            if (ClosePunctRegex.IsMatch(s))
            {
                acc.Append(s);
                continue;
            }

            char last = acc.Length > 0 ? acc[^1] : '\0';
            char first = s[0];
            if (last != '\0' && IsCjk(last) && IsCjk(first))
            {
                acc.Append(s);                                  // 中文 + 中文 → 无缝
            }
            else if (last != '\0' && HyphenTailRegex.IsMatch(acc.ToString()) && LowerHeadRegex.IsMatch(s))
            {
                acc.Length -= 1;                                // 连字符断词还原（去掉行尾 -）
                acc.Append(s);
            }
            else if (last == ' ')
            {
                acc.Append(s);                                  // 行尾已是空格 → 不重复补
            }
            else if (last == '\0')
            {
                acc.Append(s);
            }
            else if (last != '\0' && (last != ' ' && !char.IsLetterOrDigit(last) ||
                                     first != ' ' && !char.IsLetterOrDigit(first)))
            {
                // 符号边界无缝：断行落在符号处（行尾/行首为非字母数字符号）时中文排版两侧不补空格——
                // 「完成/│失败」「阶段│(初态」「（│Ocr」补空格会拆散中文词/日期/路径；
                // 例外：英文句中/句末标点（. ! ? , ;）后接字母 = 句子边界，仍补单空格
                if ((last == '.' || last == '!' || last == '?' || last == ',' || last == ';') && char.IsLetter(first))
                    acc.Append(' ').Append(s);
                else
                    acc.Append(s);
            }
            else
            {
                acc.Append(' ').Append(s);                      // 英文/数字侧 → 单空格
            }
        }

        return ApplySpacing(acc.ToString());
    }

    /// <summary>中英文间距：中文与英文/数字之间各补一个空格（对短文本直接逐对替换）。</summary>
    private static string ApplySpacing(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return CjkToAlnumRegex.Replace(
            AlnumToCjkRegex.Replace(text, "$1 $2"), "$1 $2");
    }

    // ═══════════ 行首列表标记剥离（MD 渲染防双写） ═══════════

    /// <summary>
    /// 行首无序列表标记：圆点族（• · ▪ ‣ ◦）后空格可丢（OCR 常丢标记后空格，如「·项目」）；
    /// 连字符族（- – — *）须后接空白——防误剥行首破折号「——」与负号「-5」。
    /// </summary>
    private static readonly Regex BulletHeadRegex = new(@"^\s*(?:[•·▪‣◦]\s*|[-–—*]\s+)", RegexOptions.Compiled);

    /// <summary>行首有序列表标记：1. / 1。 / 1) / 1、 / （一） / 一、 等编号前缀（数字编号后跟数字视为小数不剥）。</summary>
    private static readonly Regex OrderedHeadRegex = new(
        @"^\s*(\d{1,3}[.)、。．](?!\d)|（[一二三四五六七八九十百]+）|\([一二三四五六七八九十百]+\)|[一二三四五六七八九十]+[、.．])\s*", RegexOptions.Compiled);

    /// <summary>
    /// 剥离行首列表标记：转 Markdown 列表语法前先把 OCR 原有的 •/-/1./（一） 等标记去掉，
    /// 防止「- - 项目」「1. 1. 项目」双写。非列表标记开头（如普通正文、标题编号）原样返回。
    /// </summary>
    public static string StripListMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        string t = BulletHeadRegex.Replace(text, string.Empty);
        if (t.Length != text.Length) return t;   // 无序标记已剥，不再碰有序
        return OrderedHeadRegex.Replace(text, string.Empty);
    }

    /// <summary>
    /// 行首是否带列表标记（•/-/1./（一） 等）：ConvertAiDecision 按此分组列表项——
    /// 带标记行开启新项，无标记行视为上一项的 OCR 断行续行并入当前项。
    /// </summary>
    public static bool HasListMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return BulletHeadRegex.IsMatch(text) || OrderedHeadRegex.IsMatch(text);
    }

    /// <summary>行首是否有序列表标记（编号式）：Para 结构兜底升级列表时做有序/无序多数表决。</summary>
    public static bool HasOrderedMarker(string text)
    {
        return !string.IsNullOrEmpty(text) && OrderedHeadRegex.IsMatch(text);
    }

    /// <summary>标题有效层级：AI level（1-3）优先，未分级时按几何大标题回退（大→1，否则→2）。</summary>
    public static int TitleLevelOf(int level, bool isBigTitle) =>
        level is >= 1 and <= 3 ? level : (isBigTitle ? 1 : 2);
}
