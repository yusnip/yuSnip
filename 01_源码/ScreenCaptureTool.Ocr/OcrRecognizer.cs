using System.Drawing;

#if SCT_ENABLE_WINRT_OCR
using Windows.Media.Ocr;
#endif

namespace ScreenCaptureTool.Ocr;

/// <summary>
/// OCR 识别器骨架。默认离线构建不直接引用 WinRT；定义 SCT_ENABLE_WINRT_OCR 后可验证 OcrEngine。
/// </summary>
public sealed class OcrRecognizer
{
    public bool IsAvailable()
    {
#if SCT_ENABLE_WINRT_OCR
        return OcrEngine.TryCreateFromUserProfileLanguages() != null;
#else
        return false;
#endif
    }

    public uint? GetMaxImageDimension()
    {
#if SCT_ENABLE_WINRT_OCR
        return OcrEngine.MaxImageDimension;
#else
        return null;
#endif
    }

    public Task<OcrRecognitionResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
        cancellationToken.ThrowIfCancellationRequested();

        // T7.4b 在 WinRT SDK 可用后迁移 Bitmap/byte[] -> SoftwareBitmap -> RecognizeAsync 的完整逻辑。
        return Task.FromResult(new OcrRecognitionResult(Array.Empty<OcrTextRegion>(), null));
    }
}
