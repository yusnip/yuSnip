using System.Drawing;

namespace ScreenCaptureTool.Ocr;

/// <summary>
/// OCR 识别出的文本区域。坐标约定为输入图像的物理像素坐标。
/// </summary>
public sealed record OcrTextRegion(RectangleF Bounds, string Text);
