#nullable enable

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 文本翻译结果。
/// </summary>
public sealed class TextTranslationResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>翻译后的文本。失败时为 <c>null</c>。</summary>
    public string? TranslatedText { get; init; }

    /// <summary>检测到的源语言代码（服务商返回，可能为空）。</summary>
    public string? DetectedSourceLanguage { get; init; }

    /// <summary>错误消息。成功时为 <c>null</c>。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>服务商返回的错误码。成功时为 <c>null</c>。</summary>
    public string? ErrorCode { get; init; }
    public System.Collections.Generic.IReadOnlyList<TranslationTextPair> Pairs { get; init; } = System.Array.Empty<TranslationTextPair>();

    public static TextTranslationResult Ok(string translatedText, string? detectedSource = null,
        System.Collections.Generic.IReadOnlyList<TranslationTextPair>? pairs = null) =>
        new()
        {
            Success = true,
            TranslatedText = translatedText,
            DetectedSourceLanguage = detectedSource,
            Pairs = pairs ?? System.Array.Empty<TranslationTextPair>()
        };

    public static TextTranslationResult Fail(string errorCode, string errorMessage) =>
        new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };

    public override string ToString() =>
        Success ? TranslatedText ?? "" : $"[{ErrorCode}] {ErrorMessage}";
}
