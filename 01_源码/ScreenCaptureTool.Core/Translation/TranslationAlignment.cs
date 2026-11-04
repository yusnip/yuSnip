using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>相对于完整原文／译文的 UTF-16 范围；只由同次翻译响应中的可靠对应信息建立。</summary>
public sealed record TranslationTextPair(int SourceStart, int SourceLength, int TargetStart, int TargetLength);
public readonly record struct TranslationSourceSpan(int Start, int Length);

public sealed class TextSegmentsTranslationResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Translations { get; init; } = Array.Empty<string>();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public static TextSegmentsTranslationResult Fail(string? code, string? message) => new() { ErrorCode = code, ErrorMessage = message };
}

public sealed partial class TranslationService
{
    /// <summary>按句末标点及原始换行切分；保留偏移，不把小数、常用缩写或 UTF-16 代理对拆开。</summary>
    public static IReadOnlyList<TranslationSourceSpan> SplitSentences(string text)
    {
        var result = new List<TranslationSourceSpan>();
        int start = 0;
        void Add(int end)
        {
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > start) result.Add(new(start, end - start));
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n') { Add(i); start = i + 1; continue; }
            if ("。！？!?；;.".IndexOf(c) < 0) continue;
            if (c == '.')
            {
                if (i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
                int word = i - 1;
                while (word >= start && char.IsLetter(text[word])) word--;
                string token = text.Substring(word + 1, i - word - 1);
                if (new[] { "Mr", "Mrs", "Ms", "Dr", "Prof", "vs", "etc" }.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;
                if (i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])) continue;
                if (token.Length == 1 && char.IsUpper(token[0])) continue;
            }
            int end = i + 1;
            while (end < text.Length && "。！？!?\"'”’」』)）]】".IndexOf(text[end]) >= 0) end++;
            Add(end); start = end; i = end - 1;
        }
        Add(text.Length);
        return result;
    }

    /// <summary>图片区域按 ID 合批，Index 每批最多 64 项／4000 字符；文本整篇流程不调用本方法。</summary>
    public async Task<TextSegmentsTranslationResult> TranslateSegmentsAsync(IReadOnlyList<string> segments,
        string sourceLang, string targetLang, CancellationToken ct = default)
    {
        if (_provider == null) return TextSegmentsTranslationResult.Fail("NOT_CONFIGURED", "未启用翻译服务");
        var results = new List<string>();
        if (_provider is BilibiliIndexTranslationProvider index)
        {
            for (int offset = 0; offset < segments.Count;)
            {
                ct.ThrowIfCancellationRequested();
                var batch = new List<string>(); int length = 0;
                while (offset + batch.Count < segments.Count && batch.Count < 64)
                {
                    string part = segments[offset + batch.Count];
                    if (batch.Count > 0 && length + part.Length > 4000) break;
                    batch.Add(part); length += part.Length;
                }
                var translated = await index.TranslateSegmentsAsync(batch, sourceLang, targetLang, ct).ConfigureAwait(false);
                if (!translated.Success && translated.ErrorCode == "RATE_LIMITED")
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    translated = await index.TranslateSegmentsAsync(batch, sourceLang, targetLang, ct).ConfigureAwait(false);
                }
                if (!translated.Success) return translated;
                results.AddRange(translated.Translations); offset += batch.Count;
            }
        }
        else
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0) await Task.Delay(1100, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var translated = await TranslateTextAsync(segments[i], sourceLang, targetLang, ct).ConfigureAwait(false);
                if (!translated.Success && translated.ErrorCode is "411" or "54003" or "59004" or "RATE_LIMITED")
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    translated = await TranslateTextAsync(segments[i], sourceLang, targetLang, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                if (!translated.Success || string.IsNullOrWhiteSpace(translated.TranslatedText))
                    return TextSegmentsTranslationResult.Fail(translated.ErrorCode ?? "EMPTY_RESULT", translated.ErrorMessage ?? "翻译服务未返回对应译文");
                results.Add(translated.TranslatedText);
            }
        }
        return new() { Success = true, Translations = results };
    }

    public Task<TextTranslationResult> TranslateTextAlignedAsync(string source, string sourceLang,
        string targetLang, CancellationToken ct = default) => TranslateDocumentAsync(source, sourceLang, targetLang, null, ct);
}
