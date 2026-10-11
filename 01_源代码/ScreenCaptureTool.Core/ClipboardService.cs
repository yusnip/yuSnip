#nullable disable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 剪贴板服务。阶段 2 改造自旧 <c>ScreenCaptureStandalone.ClipboardService</c>。
    ///
    /// 改造要点（去 WinForms）：
    /// - 旧实现用 <c>System.Windows.Forms.Clipboard.SetImage</c>，违反 D001（禁止 WinForms）；
    /// - 新实现把 <see cref="Bitmap"/> 序列化成 BMP 字节流，去掉 14 字节 BITMAPFILEHEADER，
    ///   剩下 BITMAPINFOHEADER+像素数据 就是 CF_DIB 需要的格式；
    /// - 真正的写入交给 <see cref="Win32Clipboard.SetDib"/>，原生 Win32 API，
    ///   底层和 WinForms.Clipboard / WPF Clipboard 是同一套；
    /// - 重试策略下沉到 Platform，本类只负责格式转换。
    /// </summary>
    public static class ClipboardService
    {
        /// <summary>BMP 文件头（BITMAPFILEHEADER）固定 14 字节。</summary>
        private const int BmpFileHeaderSize = 14;

        public static void SetImage(Bitmap bitmap)
        {
            using (PerformanceTimer.Measure("ClipboardService.SetImage"))
            {
                if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));

                byte[] dib = ConvertBitmapToDib(bitmap);

                try
                {
                    Win32Clipboard.SetDib(dib);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("ClipboardService.SetImage 失败。", ex);
                    throw;
                }
            }
        }

        public static void SetText(string text)
        {
            using (PerformanceTimer.Measure("ClipboardService.SetText"))
            {
                if (text == null) throw new ArgumentNullException(nameof(text));

                try
                {
                    Win32Clipboard.SetText(text);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("ClipboardService.SetText 失败。", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// 尝试从剪贴板读取图片并构造 <see cref="Bitmap"/>。无图片或解析失败返回 null。
        /// </summary>
        public static Bitmap TryGetImage()
        {
            using (PerformanceTimer.Measure("ClipboardService.TryGetImage"))
            {
                byte[] dib = Win32Clipboard.TryGetDib();
                if (dib == null || dib.Length < 4) return null;

                try
                {
                    return DibToBitmap(dib);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ClipboardService.TryGetImage: DIB → Bitmap 失败：" + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>尝试从剪贴板读取文本。无文本返回 null。</summary>
        public static string TryGetText()
        {
            using (PerformanceTimer.Measure("ClipboardService.TryGetText"))
            {
                return Win32Clipboard.TryGetText();
            }
        }

        /// <summary>尝试从剪贴板读取 HTML 文本。无 HTML 返回 null。</summary>
        public static string TryGetHtml()
        {
            using (PerformanceTimer.Measure("ClipboardService.TryGetHtml"))
            {
                return Win32Clipboard.TryGetHtml();
            }
        }

        /// <summary>
        /// 把 CF_DIB 数据（BITMAPINFOHEADER + 像素）拼回完整 BMP 流并构造 Bitmap。
        /// </summary>
        private static Bitmap DibToBitmap(byte[] dib)
        {
            // BITMAPINFOHEADER 的 biSize 在前 4 字节。
            uint headerSize = BitConverter.ToUInt32(dib, 0);
            int pixelOffset = (int)headerSize; // 兜底：标准 BITMAPINFOHEADER
            // 处理带调色板的情形。
            if (headerSize >= 40 && dib.Length >= 36)
            {
                int bitCount = BitConverter.ToUInt16(dib, 14);
                uint clrUsed = BitConverter.ToUInt32(dib, 32);
                int paletteColors = clrUsed > 0 ? (int)clrUsed
                    : (bitCount <= 8 ? (1 << bitCount) : 0);
                pixelOffset = (int)headerSize + paletteColors * 4;
            }

            byte[] bmp = new byte[BmpFileHeaderSize + dib.Length];
            // BITMAPFILEHEADER
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            WriteUInt32LittleEndian(bmp, 2, (uint)bmp.Length);
            WriteUInt32LittleEndian(bmp, 6, 0u);                                   // reserved
            WriteUInt32LittleEndian(bmp, 10, (uint)(BmpFileHeaderSize + pixelOffset)); // bfOffBits
            Buffer.BlockCopy(dib, 0, bmp, BmpFileHeaderSize, dib.Length);

            using (var ms = new MemoryStream(bmp))
            {
                return new Bitmap(ms);
            }
        }

        private static void WriteUInt32LittleEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static byte[] ConvertBitmapToDib(Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Bmp);
                byte[] full = ms.ToArray();
                if (full.Length <= BmpFileHeaderSize)
                {
                    throw new InvalidOperationException("Bitmap 序列化失败，BMP 数据过短。");
                }

                int dibLen = full.Length - BmpFileHeaderSize;
                byte[] dib = new byte[dibLen];
                Buffer.BlockCopy(full, BmpFileHeaderSize, dib, 0, dibLen);
                return dib;
            }
        }
    }
}
