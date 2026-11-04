#nullable enable

using System;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 文本源语言快速检测与目标语言智能回退协调器。
/// 依据 Unicode 字符区间与统计特征就地检测输入文本的语言族，
/// 当识别到的源语言与设置的「目标语言」相同时，自动切换至「次级目标语言」，
/// 避免同语言翻译产生的冗余或别扭结果。
/// </summary>
public static class LanguageDetector
{
    /// <summary>
    /// 检测输入文本的主要语言代码（返回内部统一代码，如 zh, en, ja, ko, ru, ar, th 等；无法断定时返回 auto）。
    /// </summary>
    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TranslationLanguage.Auto;

        int cjk = 0;
        int kana = 0;
        int hangul = 0;
        int cyrillic = 0;
        int arabic = 0;
        int thai = 0;
        int latin = 0;
        int viet = 0;

        foreach (char ch in text)
        {
            int cp = ch;
            if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0xF900 && cp <= 0xFAFF))
            {
                cjk++;
            }
            else if ((cp >= 0x3040 && cp <= 0x309F) || (cp >= 0x30A0 && cp <= 0x30FF) || (cp >= 0x31F0 && cp <= 0x31FF))
            {
                kana++;
            }
            else if ((cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0x3130 && cp <= 0x318F))
            {
                hangul++;
            }
            else if (cp >= 0x0400 && cp <= 0x04FF)
            {
                cyrillic++;
            }
            else if ((cp >= 0x0600 && cp <= 0x06FF) || (cp >= 0x0750 && cp <= 0x077F) || (cp >= 0x08A0 && cp <= 0x08FF))
            {
                arabic++;
            }
            else if (cp >= 0x0E00 && cp <= 0x0E7F)
            {
                thai++;
            }
            else if (cp >= 0x1EA0 && cp <= 0x1EF9)
            {
                viet++;
            }
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
            {
                latin++;
            }
        }

        // 判定优先级：
        // 1. 韩文特有音节/字母
        if (hangul > 0 && hangul * 2 >= cjk)
            return TranslationLanguage.Korean;

        // 2. 日文假名（日文中常混用汉字，只要出现假名即判定为日文）
        if (kana > 0)
            return TranslationLanguage.Japanese;

        // 3. 中文汉字：一个汉字的信息量相当于2~3个英文字母，混合场景（如"yuSnip 截图工具"）以中文为主
        if (cjk > 0 && cjk * 2 >= latin)
            return TranslationLanguage.ChineseSimplified;

        // 4. 西里尔字母（俄文等）
        if (cyrillic > 0 && cyrillic >= latin)
            return TranslationLanguage.Russian;

        // 5. 阿拉伯文
        if (arabic > 0 && arabic >= latin)
            return TranslationLanguage.Arabic;

        // 6. 泰文
        if (thai > 0 && thai >= latin)
            return TranslationLanguage.Thai;

        // 7. 越南文（具有独特的声调扩展字符）
        if (viet >= 2)
            return TranslationLanguage.Vietnamese;

        // 8. 拉丁字母（默认归为英语）
        if (latin > 0)
            return TranslationLanguage.English;

        // 9. 纯汉字无英文
        if (cjk > 0)
            return TranslationLanguage.ChineseSimplified;

        return TranslationLanguage.Auto;
    }

    /// <summary>
    /// 判断两种语言代码是否属于同一种语言族（例如 zh、zh-CHT、zh-CHS 均属于中文）。
    /// </summary>
    public static bool IsSameLanguage(string? langA, string? langB)
    {
        if (string.IsNullOrWhiteSpace(langA) || string.IsNullOrWhiteSpace(langB))
            return false;

        if (string.Equals(langA, langB, StringComparison.OrdinalIgnoreCase))
            return true;

        bool isZhA = langA.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        bool isZhB = langB.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (isZhA && isZhB)
            return true;

        return false;
    }

    /// <summary>
    /// 解析执行翻译时实际生效的源语言与目标语言：
    /// 首先识别输入文本的源语言；若检测到源语言与当前设置的目标语言相同，
    /// 则自动将目标语言切换为次级目标语言。
    /// </summary>
    /// <param name="text">待翻译原文。</param>
    /// <param name="configuredSource">配置中的源语言（默认 auto）。</param>
    /// <param name="primaryTarget">配置中的主目标语言（默认 zh）。</param>
    /// <param name="secondaryTarget">配置中的次级目标语言（默认 en）。</param>
    /// <returns>
    /// (effectiveSource: 实际生效的源语言,
    ///  effectiveTarget: 实际生效的目标语言,
    ///  switchedToSecondary: 是否触发了自动切换至次级目标语言,
    ///  detectedSource: 本地检测到的源语言)
    /// </returns>
    public static (string effectiveSource, string effectiveTarget, bool switchedToSecondary, string detectedSource) ResolveLanguages(
        string? text,
        string? configuredSource,
        string? primaryTarget,
        string? secondaryTarget)
    {
        string cfgSrc = string.IsNullOrWhiteSpace(configuredSource) ? TranslationLanguage.Auto : configuredSource;
        string priTgt = string.IsNullOrWhiteSpace(primaryTarget) ? TranslationLanguage.ChineseSimplified : primaryTarget;
        string secTgt = string.IsNullOrWhiteSpace(secondaryTarget) ? TranslationLanguage.English : secondaryTarget;

        string detected = Detect(text);

        // 如果用户未锁定具体源语言（处于 auto 自动检测），则优先使用本地检测到的语言来比对
        string checkSource = (cfgSrc == TranslationLanguage.Auto)
            ? detected
            : cfgSrc;

        // 若源语言与主目标语言相同，且次级目标语言与源语言不同，则切换为次级目标语言
        if (!string.IsNullOrWhiteSpace(checkSource) &&
            checkSource != TranslationLanguage.Auto &&
            IsSameLanguage(checkSource, priTgt) &&
            !IsSameLanguage(checkSource, secTgt))
        {
            // 切换为次级目标语言，源语言可明确指定为检测结果（或保持 auto）
            return (checkSource, secTgt, true, detected);
        }

        // 正常路径：保持主目标语言
        return (cfgSrc, priTgt, false, detected);
    }
}
