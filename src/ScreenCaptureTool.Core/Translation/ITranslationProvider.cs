#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 翻译提供者接口。各翻译服务商（百度、有道等）实现此接口。
/// 文本翻译走 <see cref="TranslateTextAsync"/>；图片到图片走 <see cref="TranslateImageAsync"/>，
/// 由产商自己的图片翻译接口完成 OCR + 擦除 + 译文绘制，**本地不再实现任何渲染**。
/// </summary>
public interface ITranslationProvider
{
    /// <summary>提供者类型。</summary>
    TranslationProvider ProviderType { get; }

    /// <summary>
    /// 验证 API 凭证是否有效。
    /// 通常翻译一个简单单词（如 "Hello"）来确认密钥可用。
    /// </summary>
    /// <returns>验证是否通过；失败时返回错误信息。</returns>
    Task<(bool ok, string? errorMessage)> ValidateAsync(CancellationToken ct = default);

    /// <summary>
    /// 文本翻译。
    /// </summary>
    /// <param name="text">待翻译的文本。</param>
    /// <param name="sourceLang">源语言代码（内部格式，auto 表示自动检测）。</param>
    /// <param name="targetLang">目标语言代码（内部格式）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<TextTranslationResult> TranslateTextAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default);

    /// <summary>
    /// 图片到图片翻译（产商图片翻译接口）。产商返回的成品图已擦除原文并画上译文，
    /// 调用方直接展示即可，不要再自行绘制。
    /// </summary>
    /// <param name="imageBytes">待翻译图片的原始字节（PNG 或 JPEG，内部按 base64 上传）。</param>
    /// <param name="sourceLang">源语言代码（内部格式，auto 表示自动检测）。</param>
    /// <param name="targetLang">目标语言代码（内部格式）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ImageTranslationResult> TranslateImageAsync(
        byte[] imageBytes,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default);
}
