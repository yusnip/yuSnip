using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Translation;

public sealed partial class TranslationService
{
    internal TranslationService(TranslationConfig config, ITranslationProvider provider)
    { _config = config; _provider = provider; }

    public bool SupportsDocumentStream => _provider is BilibiliIndexTranslationProvider;

    public Task<TextTranslationResult> TranslateDocumentAsync(string source, CancellationToken ct = default)
    {
        var (src, tgt, _, _) = LanguageDetector.ResolveLanguages(source, _config.SourceLanguage,
            _config.TargetLanguage, _config.SecondaryTargetLanguage);
        return TranslateDocumentAsync(source, src, tgt, null, ct);
    }

    /// <summary>原文整篇一次请求；Index 在同次响应中保存句对，其他接口只消费产商实际返回的映射。</summary>
    public async Task<TextTranslationResult> TranslateDocumentAsync(string source, string sourceLang, string targetLang,
        Func<TextTranslationResult, Task>? onProgress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_provider == null) return TextTranslationResult.Fail("NOT_CONFIGURED", "未启用翻译服务");
        if (string.IsNullOrWhiteSpace(source)) return TextTranslationResult.Ok("");
        bool delivered = false;
        async Task Deliver(TextTranslationResult snapshot)
        {
            ct.ThrowIfCancellationRequested(); delivered = true;
            if (onProgress != null) await onProgress(snapshot).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        async Task<TextTranslationResult> Request() => _provider is BilibiliIndexTranslationProvider index
            ? await index.TranslateDocumentAsync(source, sourceLang, targetLang, onProgress == null ? null : Deliver, ct).ConfigureAwait(false)
            : await TranslateTextAsync(source, sourceLang, targetLang, ct).ConfigureAwait(false);
        var result = await Request().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // 仅尚未交付任何部分结果时允许限流重试；绝不把整篇拆成更多请求。
        if (!result.Success && !delivered && result.ErrorCode is "54003" or "59004" or "411" or "1411" or "5411" or "RATE_LIMITED")
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            result = await Request().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        }
        if (result.Success && string.IsNullOrWhiteSpace(result.TranslatedText))
            return TextTranslationResult.Fail("EMPTY_RESULT", "翻译服务未返回译文");
        if (result.Success && onProgress != null && !SupportsDocumentStream) await Deliver(result).ConfigureAwait(false);
        return result;
    }

    /// <summary>服务支持 SSE 时实时消费同一响应；不支持时整篇返回一次，再在本地组织对照。</summary>
    public Task<TextTranslationResult> TranslateTextStreamAsync(string source, string sourceLang, string targetLang,
        Func<TextTranslationResult, Task> onProgress, CancellationToken ct = default) =>
        TranslateDocumentAsync(source, sourceLang, targetLang, onProgress, ct);
}
