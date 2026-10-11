using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// Win32 剪贴板原生封装。Core/主项目应通过更高层的 ClipboardService 间接使用。
    ///
    /// 设计目的：避免引入 System.Windows.Forms.Clipboard（D001 禁止 WinForms），
    /// 同时绕开 Avalonia 11.x IClipboard 对图像支持不完整的问题。
    /// </summary>
    public static class Win32Clipboard
    {
        /// <summary>
        /// 把一段 DIB（device-independent bitmap）字节序列写入剪贴板，格式为 CF_DIB。
        /// 字节内容应该是从 BMP 文件去掉 14 字节 BITMAPFILEHEADER 之后的部分（即 BITMAPINFOHEADER + 像素数据）。
        ///
        /// 内部带 3 次重试 + 80ms 间隔，应对其他程序短时占用剪贴板的常见情况。
        /// </summary>
        /// <param name="dibBytes">DIB 数据（不含 BITMAPFILEHEADER）。</param>
        /// <exception cref="InvalidOperationException">所有重试都失败时抛出。</exception>
        public static void SetDib(byte[] dibBytes)
        {
            if (dibBytes == null) throw new ArgumentNullException(nameof(dibBytes));
            if (dibBytes.Length == 0) throw new ArgumentException("DIB 字节数组为空。", nameof(dibBytes));

            Exception? lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    SetDibCore(dibBytes);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 3) Thread.Sleep(80);
                }
            }

            throw new InvalidOperationException(
                "Win32Clipboard.SetDib 失败：" + (lastError?.Message ?? "未知错误"), lastError);
        }

        public static void SetText(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            Exception? lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    SetTextCore(text);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 3) Thread.Sleep(80);
                }
            }

            throw new InvalidOperationException(
                "Win32Clipboard.SetText 失败：" + (lastError?.Message ?? "未知错误"), lastError);
        }

        /// <summary>
        /// 尝试从剪贴板读取图片（CF_DIB 格式）。返回完整的 DIB 字节
        /// （BITMAPINFOHEADER + 像素数据，不含 14 字节 BITMAPFILEHEADER）；
        /// 无图片返回 null。
        ///
        /// 刻意返回原始字节而非 <c>System.Drawing.Bitmap</c>：本 Platform 层不引用
        /// System.Drawing.Common 的位图类型，Bitmap 构造交给上层（<see cref="ClipboardService"/>）。
        /// </summary>
        public static byte[]? TryGetDib()
        {
            try
            {
                byte[]? dib = GetClipboardBytes(NativeMethods.CF_DIB);
                if (dib == null || dib.Length < 4) return null;
                return dib;
            }
            catch
            {
                // Platform 层不记日志；上层 ClipboardService 会记录。
                return null;
            }
        }

        /// <summary>尝试从剪贴板读取文本（CF_UNICODETEXT）。无文本返回 null。</summary>
        public static string? TryGetText()
        {
            try
            {
                byte[]? raw = GetClipboardBytes(NativeMethods.CF_UNICODETEXT);
                if (raw == null || raw.Length < 2) return null;

                // CF_UNICODETEXT 是 UTF-16LE，以 null 结尾。
                int len = raw.Length;
                // 去掉末尾的 \0 填充
                while (len >= 2 && (raw[len - 1] == 0 && raw[len - 2] == 0)) len -= 2;
                if (len < 2) return null;
                return Encoding.Unicode.GetString(raw, 0, len);
            }
            catch
            {
                // Platform 层不记日志；上层 ClipboardService 会记录。
                return null;
            }
        }

        private static uint _htmlFormatId = 0;
        /// <summary>尝试从剪贴板读取 HTML 格式数据。无 HTML 返回 null。</summary>
        public static string? TryGetHtml()
        {
            try
            {
                if (_htmlFormatId == 0)
                {
                    _htmlFormatId = NativeMethods.RegisterClipboardFormatW("HTML Format");
                }
                if (_htmlFormatId == 0) return null;

                byte[]? raw = GetClipboardBytes(_htmlFormatId);
                if (raw == null || raw.Length == 0) return null;

                // Windows HTML clipboard format is UTF-8 encoded
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteUInt32LittleEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>
        /// 读取指定剪贴板格式的全部字节。不可用或为空返回 null。
        /// 不拥有返回的 HGLOBAL 所有权（剪贴板数据由系统管理，不能 GlobalFree）。
        /// </summary>
        private static byte[]? GetClipboardBytes(uint format)
        {
            if (!NativeMethods.IsClipboardFormatAvailable(format)) return null;

            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                // Platform 层不记日志；OpenClipboard 失败一般是别的程序占用，返回 null 让上层判断。
                return null;
            }

            try
            {
                IntPtr handle = NativeMethods.GetClipboardData(format);
                if (handle == IntPtr.Zero) return null;

                UIntPtr sizePtr = NativeMethods.GlobalSize(handle);
                int size = (int)(sizePtr.ToUInt64() & 0x7FFFFFFF);
                if (size <= 0) return null;

                IntPtr ptr = NativeMethods.GlobalLock(handle);
                if (ptr == IntPtr.Zero) return null;

                try
                {
                    byte[] bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, size);
                    return bytes;
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                try { NativeMethods.CloseClipboard(); } catch { }
            }
        }


        private static void SetDibCore(byte[] dib)
        {
            SetClipboardBytes(dib, NativeMethods.CF_DIB);
        }

        private static void SetTextCore(string text)
        {
            byte[] textBytes = Encoding.Unicode.GetBytes(text + "\0");
            SetClipboardBytes(textBytes, NativeMethods.CF_UNICODETEXT);
        }

        private static void SetClipboardBytes(byte[] bytes, uint format)
        {
            IntPtr hGlobal = IntPtr.Zero;
            bool clipboardOpened = false;

            try
            {
                hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero)
                {
                    throw new InvalidOperationException("GlobalAlloc 失败，错误码：" + Marshal.GetLastWin32Error());
                }

                IntPtr ptr = NativeMethods.GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("GlobalLock 失败，错误码：" + Marshal.GetLastWin32Error());
                }

                try
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(hGlobal);
                }

                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    throw new InvalidOperationException("OpenClipboard 失败，错误码：" + Marshal.GetLastWin32Error());
                }
                clipboardOpened = true;

                if (!NativeMethods.EmptyClipboard())
                {
                    throw new InvalidOperationException("EmptyClipboard 失败，错误码：" + Marshal.GetLastWin32Error());
                }

                IntPtr setResult = NativeMethods.SetClipboardData(format, hGlobal);
                if (setResult == IntPtr.Zero)
                {
                    throw new InvalidOperationException("SetClipboardData 失败，错误码：" + Marshal.GetLastWin32Error());
                }

                // 所有权转交给系统，不能再 GlobalFree
                hGlobal = IntPtr.Zero;
            }
            finally
            {
                if (clipboardOpened)
                {
                    try { NativeMethods.CloseClipboard(); } catch { }
                }
                if (hGlobal != IntPtr.Zero)
                {
                    try { NativeMethods.GlobalFree(hGlobal); } catch { }
                }
            }
        }
    }
}
