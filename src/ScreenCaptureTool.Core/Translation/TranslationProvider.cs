#nullable enable

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 翻译服务提供者。
/// </summary>
public enum TranslationProvider
{
    /// <summary>未配置（禁用翻译功能）。</summary>
    None = 0,

    /// <summary>百度翻译开放平台。</summary>
    Baidu = 1,

    /// <summary>有道智云。</summary>
    Youdao = 2,
    /// <summary>bilibili Index-Translate 官方免费公网接口，无需密钥。</summary>
    BilibiliIndex = 3,
}
