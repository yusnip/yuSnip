#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 剪贴板服务。阶段 2 改造自旧 <c>ScreenCaptureStandalone.ClipboardService</c>。
    ///
    /// 改造要点（去 WinForms）：
    /// - 旧实现用 <c>System.Windows.Forms.Clipboard.SetImage</c>，违反 D001（禁止 WinForms）；
    /// - 常用 32 位图直接构造 DIB；其余格式沿用 GDI+ BMP 编码器；
    /// - 真正的写入交给 <see cref="Win32Clipboard.SetClipboardFormats"/>，原生 Win32 API，
    ///   底层和 WinForms.Clipboard / WPF Clipboard 是同一套；
    /// - 重试策略下沉到 Platform，本类只负责格式转换。
    /// </summary>
    public static class ClipboardService
    {
        /// <summary>最后一次向剪贴板写入图片时是否声明保留了透明 Alpha 通道。</summary>
        public static bool LastWritePreservedAlpha { get; private set; }
        private static readonly SemaphoreSlim ImageWriteQueue = new(1, 1);
        private static readonly object CommitLock = new();
        private static long _writeRequest;
        private static uint _lastImageSequence;

        /// <summary>
        /// 后台准备并写入图片。调用方须独占位图、保持到 await 完成，不可并行绘制或释放。
        /// 返回 false 表示已被本进程更新的复制请求取代，调用方不应提示成功或回写旧截图记忆。
        /// </summary>
        public static Task<bool> SetImageAsync(Bitmap bitmap, bool preserveAlpha = false)
            => SetImageAsyncCore(bitmap, preserveAlpha);

        public static Task<bool> SetImageAutoAlphaAsync(Bitmap bitmap) => SetImageAsyncCore(bitmap, null);

        private static async Task<bool> SetImageAsyncCore(Bitmap bitmap, bool? preserveAlpha)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            long request = Interlocked.Increment(ref _writeRequest);
            uint sequence = NativeMethods.GetClipboardSequenceNumber();
            await ImageWriteQueue.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    if (Volatile.Read(ref _writeRequest) != request) return false;
                    return WriteImage(bitmap, preserveAlpha ?? HasSemiTransparentPixels(bitmap), request, sequence);
                }).ConfigureAwait(false);
            }
            finally { ImageWriteQueue.Release(); }
        }

        public static void SetImage(Bitmap bitmap)
        {
            SetImage(bitmap, preserveAlpha: false);
        }

        /// <summary>
        /// 按位图<b>实际内容</b>自动选择写入路径：含透明/半透明像素（烘焙阴影带、圆角、透明 PNG）
        /// 走保 alpha 三格式，全不透明走历史单 CF_DIB（逐字节一致）。网格抽样判定，一次性操作代价可忽略。
        /// 截图主链路（<c>CaptureSession</c>）仍显式传 preserveAlpha——那里手头就有效果参数，不必探测。
        /// </summary>
        public static void SetImageAutoAlpha(Bitmap bitmap)
        {
            SetImage(bitmap, preserveAlpha: HasSemiTransparentPixels(bitmap));
        }

        /// <summary>位图是否实际含有透明/半透明像素（而非仅仅像素格式带 alpha 通道）。网格抽样 + 边缘一圈，发现即返回。</summary>
        private static bool HasSemiTransparentPixels(Bitmap bitmap)
        {
            if (bitmap == null || !Image.IsAlphaPixelFormat(bitmap.PixelFormat)) return false;
            try
            {
                int stepX = Math.Max(1, bitmap.Width / 64);
                int stepY = Math.Max(1, bitmap.Height / 64);
                for (int y = 0; y < bitmap.Height; y += stepY)
                {
                    for (int x = 0; x < bitmap.Width; x += stepX)
                    {
                        if (bitmap.GetPixel(x, y).A < 250) return true;
                    }
                }
                int w = bitmap.Width - 1, h = bitmap.Height - 1;
                for (int x = 0; x <= w; x += stepX)
                {
                    if (bitmap.GetPixel(x, 0).A < 250 || bitmap.GetPixel(x, h).A < 250) return true;
                }
                for (int y = 0; y <= h; y += stepY)
                {
                    if (bitmap.GetPixel(0, y).A < 250 || bitmap.GetPixel(w, y).A < 250) return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClipboardService.HasSemiTransparentPixels 检测失败，按不透明处理: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 写入剪贴板。
        ///
        /// <paramref name="preserveAlpha"/> 为 false 时行为与历史完全一致：只写 CF_DIB。
        /// 为 true 时（圆角截图，四角是透明像素）改为一次原子写三种格式：
        /// - CF_DIB：兜底给只认老格式的程序，内容与旧实现逐字节相同；
        /// - CF_DIBV5：用 BI_BITFIELDS + alpha 掩码显式声明 32 位里的高字节是 alpha，
        ///   Photoshop 这类走 GDI 的程序据此才不会把四角填黑；
        /// - 已注册格式 "PNG"：Office、浏览器、多数聊天软件优先取它，天然带透明通道。
        /// 三者描述的是同一张图，接收方挑自己认识的那个，故附加不会影响既有粘贴目标。
        /// </summary>
        /// <param name="preserveAlpha">位图是否含有效透明像素（圆角/投影产生的透明边）。</param>
        public static void SetImage(Bitmap bitmap, bool preserveAlpha)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            WriteImage(bitmap, preserveAlpha, Interlocked.Increment(ref _writeRequest));
        }

        private static bool WriteImage(Bitmap bitmap, bool preserveAlpha, long request, uint? requestedSequence = null)
        {
            using (PerformanceTimer.Measure("ClipboardService.SetImage"))
            {
                byte[] dib = ConvertBitmapToDib(bitmap);
                try
                {
                    var entries = new List<Win32Clipboard.ClipboardFormatEntry>
                    {
                        new() { Format = NativeMethods.CF_DIB, Data = dib }
                    };
                    if (preserveAlpha)
                    {
                        entries.Add(new() { Format = NativeMethods.CF_DIBV5, Data = ConvertBitmapToDibV5(bitmap) });
                        byte[] png = ConvertBitmapToPng(bitmap);
                        if (png.Length > 0) entries.Add(new() { Name = "PNG", Data = png });
                    }
                    lock (CommitLock)
                    {
                        if (Volatile.Read(ref _writeRequest) != request) return false;
                        uint? published = Win32Clipboard.SetClipboardFormats(entries, () =>
                        {
                            // 打开后、清空前再判定：编码／退避期间别的程序或文件复制可能已更新剪贴板。
                            if (Volatile.Read(ref _writeRequest) != request) return false;
                            uint current = NativeMethods.GetClipboardSequenceNumber();
                            return !requestedSequence.HasValue || current == requestedSequence.Value || current == _lastImageSequence;
                        });
                        if (!published.HasValue) return false;
                        _lastImageSequence = published.Value;
                        LastWritePreservedAlpha = preserveAlpha;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("ClipboardService.SetImage 失败。", ex);
                    throw;
                }
            }
        }

        /// <summary>BITMAPFILEHEADER 字节数固定为 14 字节。</summary>
        private const int BmpFileHeaderSize = 14;

        /// <summary>BITMAPV5HEADER 字节数：40（BITMAPINFOHEADER）+ 4×4 掩码 + 4 色彩空间 + 36 端点 + 4×6 其余。</summary>
        private const int BitmapV5HeaderSize = 124;

        /// <summary>BI_BITFIELDS：颜色分量由掩码指定，32 位图靠它宣告 alpha。</summary>
        private const int BiBitfields = 3;

        public static void SetText(string text)
        {
            using (PerformanceTimer.Measure("ClipboardService.SetText"))
            {
                if (text == null) throw new ArgumentNullException(nameof(text));

                try
                {
                    lock (CommitLock)
                    {
                        Interlocked.Increment(ref _writeRequest);
                        Win32Clipboard.SetText(text);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("ClipboardService.SetText 失败。", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// 尝试从剪贴板读取图片并构造 <see cref="Bitmap"/>。
        /// 采取三级读取策略以保真透明 Alpha 通道：
        /// 1. 注册格式 "PNG"：天然保真透明度；
        /// 2. CF_DIBV5：声明了 BI_BITFIELDS + Alpha 掩码时以 32bppArgb 直解；
        /// 3. CF_DIB：兜底老格式（兼容他源复制）。
        /// </summary>
        public static Bitmap TryGetImage()
        {
            using (PerformanceTimer.Measure("ClipboardService.TryGetImage"))
            {
                // 1. 注册格式 "PNG"（最高优先级，天然保留 Alpha 通道）
                try
                {
                    byte[] pngBytes = Win32Clipboard.TryGetRegisteredFormatBytes("PNG");
                    if (pngBytes != null && pngBytes.Length > 0)
                    {
                        var pngBmp = TryDecodePng(pngBytes);
                        if (pngBmp != null) return pngBmp;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ClipboardService.TryGetImage: PNG 解码异常：" + ex.Message);
                }

                // 2. CF_DIBV5（仅认 32bpp + BI_BITFIELDS + 0xFF000000 掩码）
                try
                {
                    byte[] dibV5 = Win32Clipboard.TryGetDibV5();
                    if (dibV5 != null && dibV5.Length >= BitmapV5HeaderSize)
                    {
                        var v5Bmp = TryDecodeDibV5(dibV5);
                        if (v5Bmp != null) return v5Bmp;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ClipboardService.TryGetImage: DIBV5 解码异常：" + ex.Message);
                }

                // 3. CF_DIB 兜底（普通图片无 PNG/DIBV5 时的历史口径）
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

        private static Bitmap TryDecodePng(byte[] png)
        {
            if (png == null || png.Length < 8) return null;
            try
            {
                using (var ms = new MemoryStream(png))
                using (var decoded = new Bitmap(ms))
                {
                    var owned = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(owned))
                    {
                        g.DrawImage(decoded, 0, 0, owned.Width, owned.Height);
                    }
                    return owned;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClipboardService.TryDecodePng 失败：" + ex.Message);
                return null;
            }
        }

        private static Bitmap TryDecodeDibV5(byte[] dibV5)
        {
            if (dibV5 == null || dibV5.Length < BitmapV5HeaderSize) return null;
            try
            {
                uint headerSize = BitConverter.ToUInt32(dibV5, 0);
                if (headerSize < BitmapV5HeaderSize) return null;

                int w = BitConverter.ToInt32(dibV5, 4);
                int h = BitConverter.ToInt32(dibV5, 8);
                ushort bitCount = BitConverter.ToUInt16(dibV5, 14);
                uint compression = BitConverter.ToUInt32(dibV5, 16);
                uint alphaMask = BitConverter.ToUInt32(dibV5, 52);

                if (bitCount != 32 || compression != BiBitfields || alphaMask != 0xFF000000)
                    return null;

                bool bottomUp = h > 0;
                int absH = Math.Abs(h);
                if (w <= 0 || absH <= 0) return null;

                int rowBytes = w * 4;
                int pixelOffset = (int)headerSize;
                if (dibV5.Length < pixelOffset + rowBytes * absH) return null;

                var bmp = new Bitmap(w, absH, PixelFormat.Format32bppArgb);
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, absH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < absH; y++)
                    {
                        int targetY = bottomUp ? (absH - 1 - y) : y;
                        IntPtr destRow = data.Scan0 + targetY * data.Stride;
                        Marshal.Copy(dibV5, pixelOffset + y * rowBytes, destRow, rowBytes);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                return bmp;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClipboardService.TryDecodeDibV5 失败：" + ex.Message);
                return null;
            }
        }

        private static readonly string[] ImageFileExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp" };

        /// <summary>
        /// 剪贴板为"复制的图片文件"（如资源管理器 Ctrl+C 文件）时，加载文件列表中的首个图片为 Bitmap。
        /// 非文件剪贴板、列表中无图片文件或加载失败（如系统不支持的格式）返回 null。
        /// </summary>
        public static Bitmap TryGetImageFromClipboardFile()
        {
            using (PerformanceTimer.Measure("ClipboardService.TryGetImageFromClipboardFile"))
            {
                var files = Win32Clipboard.TryGetFileDropList();
                if (files == null) return null;

                foreach (var file in files)
                {
                    string ext = System.IO.Path.GetExtension(file ?? string.Empty)?.ToLowerInvariant() ?? string.Empty;
                    if (Array.IndexOf(ImageFileExtensions, ext) < 0) continue;
                    if (!System.IO.File.Exists(file)) continue;

                    try
                    {
                        // GDI+ 按文件路径加载是「保住文件句柄 + 延迟解码」：位图活多久，源文件句柄就保活多久，
                        // 像素要等到第一次真正读取时才解码。而这张位图会被多条链路长期共用——贴图窗口持有它，
                        // 历史记录把它 Clone 后交给线程池画缩略图（CaptureHistoryStore.SaveThumbnail →
                        // Graphics.DrawImage），OCR 与取色也读它。解码一旦被推迟到源文件已被删除/覆盖、
                        // 句柄已失效之后，GDI+ 就拿着 INVALID_HANDLE_VALUE(-1) 继续读 → 0xC0000005 访问违例，
                        // 且崩在 GdiPlus 内部、托管 try/catch 接不住：正式版转储正是 GdiPlus.dll 处 AV READ
                        // 0xFFFFFFFFFFFFFFFF，Debug 转储的崩溃线程栈顶则是这条链上的 GdipDrawImageRectI。
                        // 故在 UI 线程、句柄必然有效的当下就完成物化，让后台线程只接触自持内存。
                        // 物化刻意走 LockBits 整块搬运而非 new Bitmap(img)/DrawImage：后两者会在
                        // GDI+ 内部预乘格式上再做一次「预乘↔反预乘」往返，半透明像素每通道 ±1 漂移
                        // （实测本图 202 字节不等，取色器会读出与文件不同的值）。此处直接搬已解码好的
                        // 缓冲，像素逐字节等同当前线上行为，零回归。
                        using var fileBacked = new Bitmap(file);
                        var bmp = DetachPixels(fileBacked);
                        AppLogger.Info($"ClipboardService.TryGetImageFromClipboardFile: 文件贴图 {file}（{bmp.Width}x{bmp.Height}）");
                        return bmp;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("ClipboardService.TryGetImageFromClipboardFile: 加载失败 " + file + "：" + ex.Message);
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// 把文件/流支撑的位图当场解码，并整块搬进一张自持存储的新位图后返回。
        /// 逐字节拷贝 GDI+ 已解码好的预乘缓冲，因此像素与直接读文件完全一致（不像
        /// new Bitmap(img)/DrawImage 会再做一次预乘往返而让半透明像素每通道漂移 ±1）。
        /// </summary>
        public static Bitmap DetachPixels(Bitmap source)
        {
            int w = source.Width, h = source.Height;
            var full = new Rectangle(0, 0, w, h);
            var owned = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData src = source.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dst = owned.LockBits(full, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = Math.Min(src.Stride, dst.Stride);
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(src.Scan0, y * src.Stride), row, 0, rowBytes);
                    System.Runtime.InteropServices.Marshal.Copy(
                        row, 0, IntPtr.Add(dst.Scan0, y * dst.Stride), rowBytes);
                }
            }
            finally
            {
                source.UnlockBits(src);
                owned.UnlockBits(dst);
            }
            return owned;
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

            // GDI+ 的 Bitmap(Stream) 不拷贝像素，只持流引用按需解码：流一释放，位图就只剩
            // 头信息——Width/Height 照常，但任何 DrawImage / Clone 都抛 "A generic error
            // occurred in GDI+"（曾经导致剪贴板贴图整个失败且只留一行日志）。
            // 因此必须在流存活期间把像素拷进自持存储的位图再返回，顺带统一成 32bppArgb。
            using (var ms = new MemoryStream(bmp))
            using (var decoded = new Bitmap(ms))
            {
                var owned = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(owned))
                {
                    g.DrawImage(decoded, 0, 0, owned.Width, owned.Height);
                }
                return owned;
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
            if (bitmap.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppRgb)
            {
                int imageSize = checked(bitmap.Width * 4 * bitmap.Height);
                byte[] dib = new byte[checked(40 + imageSize)];
                WriteUInt32LittleEndian(dib, 0, 40);
                WriteUInt32LittleEndian(dib, 4, (uint)bitmap.Width);
                WriteUInt32LittleEndian(dib, 8, (uint)bitmap.Height);
                WriteUInt16LittleEndian(dib, 12, 1);
                WriteUInt16LittleEndian(dib, 14, 32);
                // BI_RGB 的 SizeImage 允许为 0，与原 BMP 编码器头保持一致。
                WriteUInt32LittleEndian(dib, 24, DotsPerMeter(bitmap.HorizontalResolution));
                WriteUInt32LittleEndian(dib, 28, DotsPerMeter(bitmap.VerticalResolution));
                CopyBottomUpRows(bitmap, bitmap.PixelFormat, dib, 40);
                return dib;
            }
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Bmp);
                if (ms.Length <= BmpFileHeaderSize)
                {
                    throw new InvalidOperationException("Bitmap 序列化失败，BMP 数据过短。");
                }

                int dibLen = checked((int)ms.Length - BmpFileHeaderSize);
                byte[] dib = new byte[dibLen];
                Buffer.BlockCopy(ms.GetBuffer(), BmpFileHeaderSize, dib, 0, dibLen);
                return dib;
            }
        }

        private static uint DotsPerMeter(float dpi) => float.IsFinite(dpi) && dpi > 0 ? checked((uint)Math.Round(dpi / 0.0254)) : 0;

        private static void CopyBottomUpRows(Bitmap bitmap, PixelFormat format, byte[] destination, int offset)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, format);
            try
            {
                int row = checked(bitmap.Width * 4);
                for (int y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(data.Scan0 + (bitmap.Height - 1 - y) * data.Stride, destination, offset + y * row, row);
            }
            finally { bitmap.UnlockBits(data); }
        }

        /// <summary>
        /// 手工拼装 CF_DIBV5 数据：124 字节 <c>BITMAPV5HEADER</c> + 32 位 BGRA 像素。
        ///
        /// 不能沿用 <see cref="ConvertBitmapToDib"/> 的 BMP 字节：GDI+ 的 BMP 编码器写的是
        /// 40 字节头 + BI_RGB，而 BI_RGB 的 32 位图按规范"高字节不使用"，接收方有权忽略 alpha，
        /// 四角就被填成黑色。只有换成 BI_BITFIELDS 并把 alpha 掩码写进头里，才是明确宣告
        /// "这一字节是 alpha"。像素本身两种头完全一致，所以直接 LockBits 取原始 BGRA 即可。
        /// </summary>
        private static byte[] ConvertBitmapToDibV5(Bitmap bitmap)
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            if (w <= 0 || h <= 0) throw new InvalidOperationException("位图尺寸为空，无法构造 DIBV5。");

            using (var converted = bitmap.PixelFormat == PixelFormat.Format32bppArgb ? null
                : bitmap.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb))
            {
                var argb = converted ?? bitmap;
                // DIBV5 是紧凑的 32 位行；源位图可能有额外行距，不能把源 padding 写进成品。
                int stride = checked(w * 4);
                int imageSize = checked(stride * h);
                byte[] buf = new byte[checked(BitmapV5HeaderSize + imageSize)];

                WriteUInt32LittleEndian(buf, 0, (uint)BitmapV5HeaderSize);   // bV5Size
                WriteUInt32LittleEndian(buf, 4, (uint)w);                   // bV5Width
                WriteUInt32LittleEndian(buf, 8, (uint)h);                   // bV5Height（正＝自下而上）
                WriteUInt16LittleEndian(buf, 12, 1);                        // bV5Planes
                WriteUInt16LittleEndian(buf, 14, 32);                       // bV5BitCount
                WriteUInt32LittleEndian(buf, 16, (uint)BiBitfields);        // bV5Compression
                WriteUInt32LittleEndian(buf, 20, (uint)imageSize);          // bV5SizeImage
                WriteUInt32LittleEndian(buf, 40, 0x00FF0000);               // bV5RedMask
                WriteUInt32LittleEndian(buf, 44, 0x0000FF00);               // bV5GreenMask
                WriteUInt32LittleEndian(buf, 48, 0x000000FF);               // bV5BlueMask
                WriteUInt32LittleEndian(buf, 52, 0xFF000000);               // bV5AlphaMask
                WriteUInt32LittleEndian(buf, 56, 0x53524742);               // bV5CSType = LCS_sRGB
                WriteUInt32LittleEndian(buf, 108, 0x00000008);              // bV5Intent = LCS_GM_IMAGES
                // 其余字段（分辨率/调色板/端点/gamma/profile/reserved）留 0。

                // Scan0 是逻辑首行，Scan0 + y * Stride 是原图第 y 行，带符号行距也必须保留。
                // 正 bV5Height 要求成品首行是原图底行；不能把 Stride 正号当作 DIB 自下而上。
                // CF_DIB 的 BMP 编码器和 PNG 编码器各自处理行序，只有手拼的 V5 在此倒序写行。
                CopyBottomUpRows(argb, PixelFormat.Format32bppArgb, buf, BitmapV5HeaderSize);
                return buf;
            }
        }

        /// <summary>把位图编成 PNG 字节（已注册格式 "PNG" 用）。失败返回空数组，调用方据此跳过该格式。</summary>
        private static byte[] ConvertBitmapToPng(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClipboardService.ConvertBitmapToPng 失败，跳过 PNG 格式：" + ex.Message);
                return Array.Empty<byte>();
            }
        }

        private static void WriteUInt16LittleEndian(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
