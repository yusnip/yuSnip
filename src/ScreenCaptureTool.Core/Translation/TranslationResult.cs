#nullable enable

using System;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 一次翻译的完整结果。极简浮窗与编辑主窗口共享同一份实例，
/// 避免两窗口各自持有一份数据导致升级时译文对不上。
/// </summary>
public sealed class TranslationResult
{
    /// <summary>原文（完整，不受浮窗单行截断影响）。</summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>译文；未成功时为空。</summary>
    public string TargetText { get; set; } = string.Empty;
    /// <summary>本次翻译响应自带的对应范围，浮窗升级时原样复用，开关联动不请求翻译。</summary>
    public System.Collections.Generic.IReadOnlyList<TranslationTextPair> Pairs { get; set; } = System.Array.Empty<TranslationTextPair>();

    /// <summary>源语言内部代码（auto 表示自动检测）。</summary>
    public string SourceLanguage { get; set; } = TranslationLanguage.Auto;

    /// <summary>目标语言内部代码。</summary>
    public string TargetLanguage { get; set; } = TranslationLanguage.ChineseSimplified;

    /// <summary>API 实际检测到的源语言代码（用于回显）。</summary>
    public string? DetectedSourceLanguage { get; set; }

    /// <summary>引擎显示名（如「百度翻译」「有道翻译」）。</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>翻译耗时（秒）；用于状态栏回显。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>结果产生时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>错误码；为空表示成功。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>错误描述。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>是否翻译成功。</summary>
    public bool Success => string.IsNullOrEmpty(ErrorCode);

    /// <summary>构造一次成功的结果。</summary>
    public static TranslationResult Ok(string sourceText, string targetText, string sourceLang,
        string targetLang, string? detectedLang, string engine, double elapsedSeconds,
        System.Collections.Generic.IReadOnlyList<TranslationTextPair>? pairs = null) => new()
    {
        SourceText = sourceText,
        TargetText = targetText,
        Pairs = pairs ?? System.Array.Empty<TranslationTextPair>(),
        SourceLanguage = sourceLang,
        TargetLanguage = targetLang,
        DetectedSourceLanguage = detectedLang,
        Engine = engine,
        ElapsedSeconds = elapsedSeconds,
        CreatedAt = DateTime.Now
    };

    /// <summary>构造一次失败的结果（原文保留，便于重试）。</summary>
    public static TranslationResult Fail(string sourceText, string sourceLang, string targetLang,
        string engine, string errorCode, string errorMessage) => new()
    {
        SourceText = sourceText,
        SourceLanguage = sourceLang,
        TargetLanguage = targetLang,
        Engine = engine,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage,
        CreatedAt = DateTime.Now
    };
}
