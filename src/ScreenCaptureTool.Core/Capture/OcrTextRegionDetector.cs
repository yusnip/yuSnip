using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Capture;

public static class OcrTextRegionDetector
{
    public static Task<IReadOnlyList<RectangleF>> DetectTextRegionsAsync(Bitmap bitmap, double dpiScale = 1.0, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DetectTextRegions(bitmap, dpiScale), cancellationToken);
    }

    public static IReadOnlyList<RectangleF> DetectTextRegions(Bitmap bitmap, double dpiScale = 1.0)
    {
        var results = new List<RectangleF>();
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return results;
        dpiScale = dpiScale > 0.0 ? dpiScale : 1.0;

        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        using (var work = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(work))
            {
                g.DrawImage(bitmap, new Rectangle(0, 0, width, height));
            }

            BitmapData? data = null;
            try
            {
                data = work.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
                }
            }
            finally
            {
                if (data != null) work.UnlockBits(data);
            }
        }

        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        try
        {
            var softwareBitmap = new Windows.Graphics.Imaging.SoftwareBitmap(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                width,
                height,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(pixels);
            Windows.Storage.Streams.IBuffer buffer = writer.DetachBuffer();
            softwareBitmap.CopyFromBuffer(buffer);

            Windows.Media.Ocr.OcrEngine? ocrEngine = null;
            try
            {
                var lang = new Windows.Globalization.Language("zh-Hans-CN");
                if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang))
                {
                    ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                }
            }
            catch { }

            if (ocrEngine == null)
            {
                try
                {
                    var lang = new Windows.Globalization.Language("en-US");
                    if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang))
                    {
                        ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                    }
                }
                catch { }
            }

            ocrEngine ??= Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null) return results;

            Windows.Media.Ocr.OcrResult ocrResult = AwaitWinRt(ocrEngine.RecognizeAsync(softwareBitmap), 10000);
            foreach (Windows.Media.Ocr.OcrLine line in ocrResult.Lines)
            {
                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;
                foreach (Windows.Media.Ocr.OcrWord word in line.Words)
                {
                    Windows.Foundation.Rect r = word.BoundingRect;
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                    if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                }

                if (minX == double.MaxValue || minY == double.MaxValue) continue;
                const double padding = 2.0;
                results.Add(RectangleF.FromLTRB(
                    (float)((minX - padding) / dpiScale),
                    (float)((minY - padding) / dpiScale),
                    (float)((maxX + padding) / dpiScale),
                    (float)((maxY + padding) / dpiScale)));
            }
        }
        catch
        {
            throw;
        }

        return results;
    }

    private static T AwaitWinRt<T>(Windows.Foundation.IAsyncOperation<T> operation, int timeoutMs)
    {
        using var waitHandle = new AutoResetEvent(false);
        T? result = default;
        Exception? error = null;
        operation.Completed = (asyncInfo, asyncStatus) =>
        {
            try
            {
                if (asyncStatus == Windows.Foundation.AsyncStatus.Completed) result = asyncInfo.GetResults();
                else if (asyncStatus == Windows.Foundation.AsyncStatus.Error) error = asyncInfo.ErrorCode;
                else if (asyncStatus == Windows.Foundation.AsyncStatus.Canceled) error = new OperationCanceledException();
            }
            catch (Exception ex) { error = ex; }
            finally { waitHandle.Set(); }
        };

        if (!waitHandle.WaitOne(timeoutMs))
        {
            try { operation.Cancel(); } catch { }
            throw new TimeoutException("OCR operation timed out");
        }
        if (error != null) throw error;
        return result!;
    }

    /// <summary>
    /// 使用 Windows.Media.Ocr 识别图片中的文本行，返回每行的文本及其像素坐标边界框。
    /// 坐标相对于输入位图左上角（物理像素），无需额外转换即可直接在对应位置绘制覆盖层。
    /// </summary>
    public static Task<List<OcrTextLine>> RecognizeTextLinesAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RecognizeTextLines(bitmap), cancellationToken);
    }

    public static List<OcrTextLine> RecognizeTextLines(Bitmap bitmap)
    {
        var results = new List<OcrTextLine>();
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return results;

        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        using (var work = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(work))
            {
                g.DrawImage(bitmap, new Rectangle(0, 0, width, height));
            }

            BitmapData? data = null;
            try
            {
                data = work.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
                }
            }
            finally
            {
                if (data != null) work.UnlockBits(data);
            }
        }

        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        try
        {
            var softwareBitmap = new Windows.Graphics.Imaging.SoftwareBitmap(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                width,
                height,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(pixels);
            Windows.Storage.Streams.IBuffer buffer = writer.DetachBuffer();
            softwareBitmap.CopyFromBuffer(buffer);

            Windows.Media.Ocr.OcrEngine? ocrEngine = null;
            try
            {
                var lang = new Windows.Globalization.Language("zh-Hans-CN");
                if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang))
                {
                    ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                }
            }
            catch { }

            if (ocrEngine == null)
            {
                try
                {
                    var lang = new Windows.Globalization.Language("en-US");
                    if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang))
                    {
                        ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                    }
                }
                catch { }
            }

            ocrEngine ??= Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null) return results;

            Windows.Media.Ocr.OcrResult ocrResult = AwaitWinRt(ocrEngine.RecognizeAsync(softwareBitmap), 10000);
            foreach (Windows.Media.Ocr.OcrLine line in ocrResult.Lines)
            {
                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;
                foreach (Windows.Media.Ocr.OcrWord word in line.Words)
                {
                    Windows.Foundation.Rect r = word.BoundingRect;
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                    if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                }

                if (minX == double.MaxValue || minY == double.MaxValue) continue;
                const double padding = 1.0;
                results.Add(new OcrTextLine
                {
                    Text = line.Text,
                    Bounds = RectangleF.FromLTRB(
                        (float)(minX - padding),
                        (float)(minY - padding),
                        (float)(maxX + padding),
                        (float)(maxY + padding))
                });
            }
        }
        catch
        {
            throw;
        }

        return results;
    }

    /// <summary>
    /// 使用 Windows.Media.Ocr 识别图片中的单词，返回每个单词的文本及其像素坐标边界框。
    /// 坐标相对于输入位图左上角（物理像素）。用于矩形选择交互（QQ/微信风格）。
    /// </summary>
    public static Task<List<OcrWordRegion>> RecognizeWordsAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RecognizeWords(bitmap), cancellationToken);
    }

    public static List<OcrWordRegion> RecognizeWords(Bitmap bitmap)
    {
        var results = new List<OcrWordRegion>();
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return results;

        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        using (var work = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(work))
            {
                g.DrawImage(bitmap, new Rectangle(0, 0, width, height));
            }

            BitmapData? data = null;
            try
            {
                data = work.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
                }
            }
            finally
            {
                if (data != null) work.UnlockBits(data);
            }
        }

        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        try
        {
            var softwareBitmap = new Windows.Graphics.Imaging.SoftwareBitmap(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                width,
                height,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(pixels);
            Windows.Storage.Streams.IBuffer buffer = writer.DetachBuffer();
            softwareBitmap.CopyFromBuffer(buffer);

            Windows.Media.Ocr.OcrEngine? ocrEngine = null;
            try
            {
                var lang = new Windows.Globalization.Language("zh-Hans-CN");
                if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang))
                {
                    ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                }
            }
            catch { }

            if (ocrEngine == null)
            {
                try
                {
                    var lang = new Windows.Globalization.Language("en-US");
                    if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang))
                    {
                        ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                    }
                }
                catch { }
            }

            ocrEngine ??= Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null) return results;

            int lineIndex = 0;
            const double padding = 1.0;
            Windows.Media.Ocr.OcrResult ocrResult = AwaitWinRt(ocrEngine.RecognizeAsync(softwareBitmap), 10000);
            foreach (Windows.Media.Ocr.OcrLine line in ocrResult.Lines)
            {
                foreach (Windows.Media.Ocr.OcrWord word in line.Words)
                {
                    string text = word.Text;
                    if (string.IsNullOrEmpty(text)) continue;

                    var r = word.BoundingRect;
                    
                    List<string> characters = new List<string>();
                    for (int c = 0; c < text.Length; c++)
                    {
                        if (c < text.Length - 1 && char.IsHighSurrogate(text[c]) && char.IsLowSurrogate(text[c + 1]))
                        {
                            characters.Add(text.Substring(c, 2));
                            c++;
                        }
                        else
                        {
                            characters.Add(text[c].ToString());
                        }
                    }

                    int N = characters.Count;
                    if (N > 0)
                    {
                        bool isVertical = r.Height > r.Width * 1.5;
                        if (isVertical)
                        {
                            double charHeight = r.Height / N;
                            for (int charIdx = 0; charIdx < N; charIdx++)
                            {
                                results.Add(new OcrWordRegion
                                {
                                    Text = characters[charIdx],
                                    Bounds = RectangleF.FromLTRB(
                                        (float)(r.X - padding),
                                        (float)(r.Y + charIdx * charHeight - padding),
                                        (float)(r.X + r.Width + padding),
                                        (float)(r.Y + (charIdx + 1) * charHeight + padding)),
                                    LineIndex = lineIndex
                                });
                            }
                        }
                        else
                        {
                            double charWidth = r.Width / N;
                            for (int charIdx = 0; charIdx < N; charIdx++)
                            {
                                results.Add(new OcrWordRegion
                                {
                                    Text = characters[charIdx],
                                    Bounds = RectangleF.FromLTRB(
                                        (float)(r.X + charIdx * charWidth - padding),
                                        (float)(r.Y - padding),
                                        (float)(r.X + (charIdx + 1) * charWidth + padding),
                                        (float)(r.Y + r.Height + padding)),
                                    LineIndex = lineIndex
                                });
                            }
                        }
                    }
                }
                lineIndex++;
            }
        }
        catch
        {
            throw;
        }

        return results;
    }
}

/// <summary>
/// OCR 识别结果：一行文本及其在图片中的物理像素边界框。
/// </summary>
public sealed class OcrTextLine
{
    public string Text { get; init; } = string.Empty;
    public RectangleF Bounds { get; init; }
}

/// <summary>
/// OCR 识别结果：一个单词（或单个汉字）及其在图片中的物理像素边界框。
/// LineIndex 用于跨行选择时区分不同行。
/// </summary>
public sealed class OcrWordRegion
{
    public string Text { get; init; } = string.Empty;
    public RectangleF Bounds { get; init; }
    public int LineIndex { get; init; }
}

/// <summary>
/// OCR 行级识别结果：一行文本及其在原图中的像素边界框，附带行号。
/// 用于翻译功能的 OCR → 文本翻译 → 译文覆盖渲染流程。
/// </summary>
public sealed class OcrLineRegion
{
    /// <summary>该行原文文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>该行在原图中的边界框（像素坐标，已还原预处理缩放）。</summary>
    public RectangleF Bounds { get; init; }

    /// <summary>行号（从 0 开始）。</summary>
    public int LineIndex { get; init; }

    /// <summary>
    /// 识别置信度（0.0~1.0）。译文覆盖渲染据此过滤低置信噪声行——
    /// 装饰线条、图标、纹理常被误识别成孤立单字（「米」「张」「a88」之类），
    /// 若不过滤会被当成正文翻译并画到图上。
    /// </summary>
    public float Score { get; init; } = 1.0f;

    /// <summary>
    /// 该行的四点旋转多边形（原图像素坐标），供图片翻译做精确掩膜与倾斜判断。
    /// ⚠ 可选值：历史 sidecar 只存轴对齐矩形，读回的行框与此处为 <c>null</c>，
    /// 消费方必须回退 <see cref="Bounds"/>，不得假定非空。
    /// </summary>
    public PointF[]? Polygon { get; init; }

    /// <summary>
    /// 逐字刀口的原图像素横坐标（升序，长度＝字数+1），供图片翻译把抹字范围收到真正有墨的列。
    /// 竖排行、帧位不可用或字数与解码不一致时为 <c>null</c>。
    /// </summary>
    public float[]? CharX { get; init; }
}
