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
}
