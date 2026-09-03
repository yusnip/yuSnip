namespace ScreenCaptureTool.Ocr;

/// <summary>
/// OCR 识别结果。
/// </summary>
public sealed class OcrRecognitionResult
{
    public OcrRecognitionResult(IReadOnlyList<OcrTextRegion> regions, string? languageTag)
    {
        Regions = regions ?? Array.Empty<OcrTextRegion>();
        LanguageTag = languageTag;
    }

    public IReadOnlyList<OcrTextRegion> Regions { get; }

    public string? LanguageTag { get; }
}
