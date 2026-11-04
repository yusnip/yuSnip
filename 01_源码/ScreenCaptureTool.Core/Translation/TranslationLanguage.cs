#nullable enable

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 翻译目标语言。内部使用统一的语言标识符，
/// 各提供者实现中通过 <see cref="GetBaiduCode"/> / <see cref="GetYoudaoCode"/>
/// 映射为服务商专属语言代码。
/// </summary>
public static class TranslationLanguage
{
    /// <summary>自动检测源语言。</summary>
    public const string Auto = "auto";

    /// <summary>简体中文</summary>
    public const string ChineseSimplified = "zh";

    /// <summary>繁体中文</summary>
    public const string ChineseTraditional = "zh-CHT";

    /// <summary>英语</summary>
    public const string English = "en";

    /// <summary>日语</summary>
    public const string Japanese = "ja";

    /// <summary>韩语</summary>
    public const string Korean = "ko";

    /// <summary>法语</summary>
    public const string French = "fr";

    /// <summary>德语</summary>
    public const string German = "de";

    /// <summary>西班牙语</summary>
    public const string Spanish = "es";

    /// <summary>葡萄牙语</summary>
    public const string Portuguese = "pt";

    /// <summary>印尼语</summary>
    public const string Indonesian = "id";

    /// <summary>马来语</summary>
    public const string Malay = "ms";

    /// <summary>意大利语</summary>
    public const string Italian = "it";

    /// <summary>俄语</summary>
    public const string Russian = "ru";

    /// <summary>泰语</summary>
    public const string Thai = "th";

    /// <summary>越南语</summary>
    public const string Vietnamese = "vi";

    /// <summary>阿拉伯语</summary>
    public const string Arabic = "ar";

    /// <summary>土耳其语</summary>
    public const string Turkish = "tr";

    // ── 显示名称列表（供 UI 下拉菜单使用）──────────────────────

    /// <summary>
    /// 供设置界面下拉菜单使用的语言列表。
    /// 每项为 (显示名称, 内部代码)。
    /// </summary>
    public static IReadOnlyList<(string DisplayName, string Code)> SupportedTargetLanguages { get; } =
        new[]
        {
            ("简体中文", ChineseSimplified),
            ("繁体中文", ChineseTraditional),
            ("英语", English),
            ("日语", Japanese),
            ("韩语", Korean),
            ("法语", French),
            ("德语", German),
            ("西班牙语", Spanish),
            ("葡萄牙语", Portuguese),
            ("印尼语", Indonesian),
            ("马来语", Malay),
            ("意大利语", Italian),
            ("俄语", Russian),
            ("泰语", Thai),
            ("越南语", Vietnamese),
            ("阿拉伯语", Arabic),
            ("土耳其语", Turkish),
        };

    /// <summary>
    /// 供设置界面源语言下拉菜单使用的语言列表。
    /// 第一项为"自动检测"。
    /// </summary>
    public static IReadOnlyList<(string DisplayName, string Code)> SupportedSourceLanguages { get; } =
        new[]
        {
            ("自动检测", Auto),
            ("简体中文", ChineseSimplified),
            ("繁体中文", ChineseTraditional),
            ("英语", English),
            ("日语", Japanese),
            ("韩语", Korean),
            ("法语", French),
            ("德语", German),
            ("西班牙语", Spanish),
            ("葡萄牙语", Portuguese),
            ("印尼语", Indonesian),
            ("马来语", Malay),
            ("意大利语", Italian),
            ("俄语", Russian),
            ("泰语", Thai),
            ("越南语", Vietnamese),
            ("阿拉伯语", Arabic),
            ("土耳其语", Turkish),
        };

    // ── 百度翻译语言代码映射 ──────────────────────────────────

    /// <summary>将内部语言代码映射为百度翻译 API 所需的语言代码。</summary>
    public static string GetBaiduCode(string internalCode)
    {
        return internalCode switch
        {
            Auto => "auto",
            ChineseSimplified => "zh",
            ChineseTraditional => "cht",
            English => "en",
            Japanese => "jp",
            Korean => "kor",
            French => "fra",
            German => "de",
            Spanish => "spa",
            Portuguese => "pt",
            Indonesian => "id",
            Malay => "may",
            Italian => "it",
            Russian => "ru",
            Thai => "th",
            Vietnamese => "vie",
            Arabic => "ara",
            Turkish => "tr",
            _ => "auto",
        };
    }

    // ── 有道翻译语言代码映射 ──────────────────────────────────

    /// <summary>将内部语言代码映射为有道智云 API 所需的语言代码。</summary>
    public static string GetYoudaoCode(string internalCode)
    {
        return internalCode switch
        {
            Auto => "auto",
            ChineseSimplified => "zh-CHS",
            ChineseTraditional => "zh-CHT",
            English => "en",
            Japanese => "ja",
            Korean => "ko",
            French => "fr",
            German => "de",
            Spanish => "es",
            Portuguese => "pt",
            Indonesian => "id",
            Malay => "ms",
            Italian => "it",
            Russian => "ru",
            Thai => "th",
            Vietnamese => "vi",
            Arabic => "ar",
            Turkish => "tr",
            _ => "auto",
        };
    }

    /// <summary>
    /// 把产商返回的语言代码归一为内部代码（按服务商选反查表；认不出的原样返回）。
    /// 图片翻译接口由产商自己识别源语言，回传给调用方的必须是内部代码，
    /// 否则门面无法与配置里的目标语言做同族比对。
    /// </summary>
    public static string ToInternalCode(string? vendorCode, TranslationProvider provider)
    {
        if (string.IsNullOrWhiteSpace(vendorCode)) return vendorCode ?? string.Empty;
        string code = vendorCode.Trim();

        // ① 内部代码直通：两家与内部同名的值占多数（en/fr/es/pt/id/de/ru/tr…）
        foreach (var (_, internalCode) in SupportedTargetLanguages)
            if (string.Equals(code, internalCode, StringComparison.OrdinalIgnoreCase))
                return internalCode;

        // ② 整串按服务商反查（有道的 "zh-CHS" 这类带后缀的值就落在表里，不可先切分）
        string resolved = provider switch
        {
            TranslationProvider.Baidu => GetInternalFromBaiduCode(code),
            TranslationProvider.Youdao => GetInternalFromYoudaoCode(code),
            _ => code,
        };
        if (resolved != code) return resolved;

        // ③ 有道语向串 "en-2-zh" → 取源语言段再查
        int dash = code.IndexOf('-');
        return dash > 0 ? ToInternalCode(code.Substring(0, dash), provider) : code;
    }

    /// <summary>根据内部代码获取显示名称（找不到时返回代码本身）。</summary>
    public static string GetDisplayName(string internalCode)
    {
        if (string.IsNullOrEmpty(internalCode)) return "自动检测";
        if (internalCode == Auto) return "自动检测";
        foreach (var (name, code) in SupportedTargetLanguages)
        {
            if (code == internalCode) return name;
        }
        return internalCode;
    }

    /// <summary>
    /// 将 API 返回的源语言代码映射为显示名称。
    /// 支持百度 API 代码（如 "en"、"zh"）和有道 API 代码（如 "en"、"zh-CHS"）。
    /// </summary>
    public static string GetDetectedLanguageDisplayName(string? detectedCode)
    {
        if (string.IsNullOrWhiteSpace(detectedCode)) return "自动检测";

        string code = detectedCode.Trim();

        // 尝试直接匹配内部代码
        foreach (var (name, internalCode) in SupportedTargetLanguages)
        {
            if (string.Equals(code, internalCode, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // 尝试百度代码反向映射
        string internalFromBaidu = GetInternalFromBaiduCode(code);
        if (internalFromBaidu != code)
            return GetDisplayName(internalFromBaidu);

        // 尝试有道代码反向映射
        string internalFromYoudao = GetInternalFromYoudaoCode(code);
        if (internalFromYoudao != code)
            return GetDisplayName(internalFromYoudao);

        // 处理有道格式 "en-2-zh" → "en"
        if (code.Contains('-'))
        {
            string firstPart = code.Split('-')[0];
            return GetDisplayName(firstPart) != firstPart ? GetDisplayName(firstPart) : firstPart;
        }

        return code;
    }

    /// <summary>将百度 API 语言代码映射回内部代码。</summary>
    private static string GetInternalFromBaiduCode(string baiduCode)
    {
        return baiduCode switch
        {
            "auto" => Auto,
            "zh" => ChineseSimplified,
            "cht" => ChineseTraditional,
            "en" => English,
            "jp" => Japanese,
            "kor" => Korean,
            "fra" => French,
            "de" => German,
            "spa" => Spanish,
            "pt" => Portuguese,
            "id" => Indonesian,
            "may" => Malay,
            "it" => Italian,
            "ru" => Russian,
            "th" => Thai,
            "vie" => Vietnamese,
            "ara" => Arabic,
            "tr" => Turkish,
            _ => baiduCode,
        };
    }

    /// <summary>将有道 API 语言代码映射回内部代码。</summary>
    private static string GetInternalFromYoudaoCode(string youdaoCode)
    {
        return youdaoCode switch
        {
            "auto" => Auto,
            "zh-CHS" => ChineseSimplified,
            "zh-CHT" => ChineseTraditional,
            "en" => English,
            "ja" => Japanese,
            "ko" => Korean,
            "fr" => French,
            "de" => German,
            "es" => Spanish,
            "pt" => Portuguese,
            "id" => Indonesian,
            "ms" => Malay,
            "it" => Italian,
            "ru" => Russian,
            "th" => Thai,
            "vi" => Vietnamese,
            "ar" => Arabic,
            "tr" => Turkish,
            _ => youdaoCode,
        };
    }
}
