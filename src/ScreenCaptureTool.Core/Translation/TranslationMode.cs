#nullable enable

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 翻译结果的呈现模式。
/// </summary>
public enum TranslationMode
{
    /// <summary>
    /// 图片→文本：先 OCR 识别图片文字，再翻译为纯文本输出。
    /// 适合阅读外文长句段落等场景。
    /// </summary>
    ImageToText = 0,

    /// <summary>
    /// 图片→图片：将译文直接覆盖回原图，保留原始版式。
    /// 适合需要在图片上直接查看译文的场景。
    /// </summary>
    ImageToImage = 1,
}
