#nullable enable

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 翻译服务配置。由 <c>AppSettings</c> 填充，传入各翻译提供者。
/// </summary>
public sealed class TranslationConfig
{
    /// <summary>翻译服务商。设为 <see cref="TranslationProvider.None"/> 时禁用翻译功能。</summary>
    public TranslationProvider Provider { get; set; } = TranslationProvider.None;

    /// <summary>应用 ID（百度 APP ID / 有道应用 ID）。</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 第二把凭证。<b>按服务商含义不同，字段名 <c>SecretKey</c> 只是历史命名</b>：
    /// 有道＝智云控制台的「应用密钥」（参与 sha256 签名）；
    /// 百度＝翻译开放平台「APIKey 管理」签发的 <b>API Key</b>（作 <c>Authorization: Bearer</c>，文本与图片翻译共用这一把）。
    /// 两家的文本翻译与图片翻译都复用同一个字段，无需额外凭证。
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>源语言代码（内部格式，默认 auto 自动检测）。</summary>
    public string SourceLanguage { get; set; } = TranslationLanguage.Auto;

    /// <summary>目标语言代码（内部格式，默认简体中文）。</summary>
    public string TargetLanguage { get; set; } = TranslationLanguage.ChineseSimplified;

    /// <summary>次级目标语言代码（内部格式，默认英语）。当检测到源语言与目标语言相同时自动切换为此语言。</summary>
    public string SecondaryTargetLanguage { get; set; } = TranslationLanguage.English;

    /// <summary>图片翻译的结果呈现模式。</summary>
    public TranslationMode Mode { get; set; } = TranslationMode.ImageToText;

    /// <summary>HTTP 请求超时（毫秒），默认 30 秒。</summary>
    public int TimeoutMs { get; set; } = 30_000;

    /// <summary>免费 Index 无需凭证；百度与有道仍要求各自的完整凭证。</summary>
    public bool IsValid =>
        Provider == TranslationProvider.BilibiliIndex || (Provider is TranslationProvider.Baidu or TranslationProvider.Youdao
        && !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(SecretKey));
}
