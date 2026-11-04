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

            SetClipboardFormats(new[] { new ClipboardFormatEntry { Format = NativeMethods.CF_DIB, Data = dibBytes } });
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
        /// 把一组文件以 CF_HDROP 写入剪贴板（资源管理器 Ctrl+C 同款形态），
        /// 微信/QQ 等应用可直接 Ctrl+V 发送文件（计划 §3.10，录屏预览「复制」用）。
        /// 内部与 SetDib 同款 3 次重试，全失败抛 <see cref="InvalidOperationException"/>（调用方 toast，不静默）。
        /// </summary>
        public static void WriteFileDrop(System.Collections.Generic.IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                throw new ArgumentException("文件列表为空。", nameof(filePaths));

            Exception? lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    SetFileDropCore(filePaths);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 3) Thread.Sleep(80);
                }
            }

            throw new InvalidOperationException(
                "Win32Clipboard.WriteFileDrop 失败：" + (lastError?.Message ?? "未知错误"), lastError);
        }

        private static void SetFileDropCore(System.Collections.Generic.IReadOnlyList<string> filePaths)
        {
            // DROPFILES{pFiles=20; POINT pt{0,0}; BOOL fNC=0; BOOL fWide=1} + 每条路径 UTF16\0 + 末尾再一个 \0（双零收尾）
            using var ms = new System.IO.MemoryStream();
            using (var bw = new System.IO.BinaryWriter(ms, System.Text.Encoding.Unicode, leaveOpen: true))
            {
                bw.Write((uint)20);            // pFiles：文件列表起始偏移 = sizeof(DROPFILES)
                bw.Write((int)0); bw.Write((int)0); // pt
                bw.Write((uint)0);             // fNC
                bw.Write((uint)1);             // fWide = Unicode
            }
            foreach (string path in filePaths)
            {
                byte[] b = System.Text.Encoding.Unicode.GetBytes(path + "\0");
                ms.Write(b, 0, b.Length);
            }
            ms.Write(new byte[2], 0, 2);

            SetClipboardBytes(ms.ToArray(), CF_HDROP);
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

        /// <summary>
        /// 尝试从剪贴板读取 CF_DIBV5 格式数据。返回完整的 DIBV5 字节
        /// （BITMAPV5HEADER + 掩码 + 像素数据，不含 14 字节 BITMAPFILEHEADER）；
        /// 无数据返回 null。
        /// </summary>
        public static byte[]? TryGetDibV5()
        {
            try
            {
                byte[]? dib = GetClipboardBytes(NativeMethods.CF_DIBV5);
                if (dib == null || dib.Length < 4) return null;
                return dib;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 尝试从剪贴板读取指定注册格式名称的原始字节数据（如 "PNG"）。无数据返回 null。
        /// </summary>
        public static byte[]? TryGetRegisteredFormatBytes(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                uint format = NativeMethods.RegisterClipboardFormatW(name);
                if (format == 0) return null;
                byte[]? bytes = GetClipboardBytes(format);
                if (bytes == null || bytes.Length == 0) return null;
                return bytes;
            }
            catch
            {
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

        private const uint CF_HDROP = 15;

        /// <summary>
        /// 尝试读取剪贴板文件列表（CF_HDROP，如资源管理器里 Ctrl+C 复制的文件）。
        /// 无文件列表或解析失败返回 null。
        /// </summary>
        public static List<string>? TryGetFileDropList()
        {
            try
            {
                byte[]? raw = GetClipboardBytes(CF_HDROP);
                if (raw == null || raw.Length < 20) return null;

                // DROPFILES: { DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; }
                // pFiles = 文件列表在块内的偏移；fWide = 1 表示路径为 UTF-16 编码
                uint offset = BitConverter.ToUInt32(raw, 0);
                bool isWide = BitConverter.ToInt32(raw, 16) != 0;
                if (offset >= raw.Length) return null;

                string all = isWide
                    ? Encoding.Unicode.GetString(raw, (int)offset, raw.Length - (int)offset)
                    : Encoding.Default.GetString(raw, (int)offset, raw.Length - (int)offset);

                var files = new List<string>();
                foreach (var f in all.Split('\0'))
                {
                    if (!string.IsNullOrEmpty(f)) files.Add(f);
                }
                return files.Count > 0 ? files : null;
            }
            catch
            {
                return null;
            }
        }

        // -------------------- 全格式快照 / 原子多格式写入（「选中文本贴图」用） --------------------

        /// <summary>句柄型标准格式：剪贴板里存的是 GDI 句柄而非内存块，既读不出字节也写不回去。</summary>
        private static readonly uint[] HandleFormats =
        {
            NativeMethods.CF_BITMAP,
            NativeMethods.CF_METAFILEPICT,
            NativeMethods.CF_ENHMETAFILE,
            NativeMethods.CF_PALETTE,
            NativeMethods.CF_OWNERDISPLAY,
        };

        /// <summary>快照总体积上限：超过就放弃还原（Excel/CDR 等应用会挂上几十 MB 的私有渲染格式）。</summary>
        private const long MaxSnapshotBytes = 64L * 1024 * 1024;

        /// <summary>剪贴板单格式条目。<see cref="Name"/> 仅对已注册格式（id ≥ 0xC000）有意义。</summary>
        public sealed class ClipboardFormatEntry
        {
            public uint Format;
            public string? Name;
            public byte[] Data = Array.Empty<byte>();
        }

        /// <summary>一次全格式剪贴板快照的结果。还原前必须确认 <see cref="CanRestore"/>。</summary>
        public sealed class ClipboardSnapshot
        {
            /// <summary>成功读出字节的格式条目（顺序即写回顺序）。</summary>
            public List<ClipboardFormatEntry> Entries { get; } = new();

            /// <summary>快照里出现过的句柄型/超大格式：存在即代表还原必然不完整。</summary>
            public bool CanRestore { get; set; } = true;

            /// <summary>OpenClipboard / 枚举是否成功完成。false 时条目表为空，不得据此还原。</summary>
            public bool Captured { get; set; }

            /// <summary>不可还原的原因（诊断用，可空）。</summary>
            public string? Failure { get; set; }
        }

        /// <summary>
        /// 一次性快照当前剪贴板的<b>全部</b>格式（供 Ctrl+C 之后原样写回）。
        ///
        /// 只做「读」，不改剪贴板；OpenClipboard 失败时返回 <c>Captured=false</c> 的空快照，
        /// 调用方据此放弃还原。句柄型格式（CF_BITMAP 等）无法按字节复制，只登记不读取，
        /// 并把 <see cref="ClipboardSnapshot.CanRestore"/> 置 false——
        /// <b>半还原比留着新内容不管破坏性更大</b>，所以调用方见到 false 必须放弃还原。
        /// </summary>
        public static ClipboardSnapshot CaptureAllFormats()
        {
            var snap = new ClipboardSnapshot();

            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                snap.Failure = "OpenClipboard 失败，错误码：" + Marshal.GetLastWin32Error();
                return snap;
            }

            try
            {
                long total = 0;
                uint format = 0;
                int guard = 0;
                while (true)
                {
                    format = NativeMethods.EnumClipboardFormats(format);
                    if (format == 0) break;
                    // EnumClipboardFormats 正常不会成环；这层保险防的是第三方应用的病态枚举行为。
                    if (++guard > 256) { snap.CanRestore = false; snap.Failure ??= "格式枚举数量异常"; break; }

                    if (Array.IndexOf(HandleFormats, format) >= 0)
                    {
                        snap.CanRestore = false;
                        snap.Failure ??= "含句柄型格式 " + format;
                        continue;
                    }

                    byte[]? bytes = ReadFormatBytes(format);
                    if (bytes == null)
                    {
                        // 读不出字节通常意味着它是延迟渲染/自绘格式，同样不可能完整还原。
                        snap.CanRestore = false;
                        snap.Failure ??= "格式 " + format + " 数据不可读";
                        continue;
                    }

                    total += bytes.Length;
                    if (total > MaxSnapshotBytes)
                    {
                        snap.CanRestore = false;
                        snap.Failure = "快照体积超上限";
                        snap.Entries.Clear();
                        break;
                    }

                    string? name = null;
                    if (format >= NativeMethods.CF_REGISTERED)
                    {
                        var sb = new StringBuilder(256);
                        if (NativeMethods.GetClipboardFormatNameW(format, sb, sb.Capacity) > 0) name = sb.ToString();
                    }

                    snap.Entries.Add(new ClipboardFormatEntry { Format = format, Name = name, Data = bytes });
                }

                snap.Captured = true;
                // 空快照（剪贴板本来就没有可字节化的格式）无需还原——写回空条目表既无意义，
                // 又会把「本来为空」误做成一次真实的清空，故直接标记为不可还原。
                if (snap.Entries.Count == 0 && snap.CanRestore) snap.CanRestore = false;
                return snap;
            }
            catch (Exception ex)
            {
                snap.Captured = false;
                snap.CanRestore = false;
                snap.Failure = "快照异常：" + ex.Message;
                snap.Entries.Clear();
                return snap;
            }
            finally
            {
                try { NativeMethods.CloseClipboard(); } catch { }
            }
        }

        private static bool IsHandleFormat(uint format)
        {
            foreach (uint h in HandleFormats) if (h == format) return true;
            return false;
        }

        /// <summary>
        /// 原子写入多格式：<b>一次 Open、一次 Empty、多次 Set</b>。
        ///
        /// 为什么不能循环调 <see cref="SetText"/>/<see cref="SetDib"/>：那两个各自都会 EmptyClipboard，
        /// 逐个调用等于每次都把前面写好的格式清空，最终只剩最后一个格式。
        /// 这里先把所有 HGLOBAL 分配好（分配阶段失败则完全没碰剪贴板），再一次性提交。
        /// 只在打开被占用时重试 3 次、间隔 80ms，复用准备好的内存。
        /// </summary>
        /// <exception cref="InvalidOperationException">所有重试都失败时抛出。</exception>
        public static uint? SetClipboardFormats(IReadOnlyList<ClipboardFormatEntry> entries, Func<bool>? canPublish = null)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (entries.Count == 0) throw new ArgumentException("剪贴板格式条目为空。", nameof(entries));

            try { return SetClipboardFormatsCore(entries, canPublish); }
            catch (Exception ex) { throw new InvalidOperationException("Win32Clipboard.SetClipboardFormats 失败：" + ex.Message, ex); }
        }

        private static void OpenPreparedClipboard()
        {
            // 只重试占用，不重建已经准备好的大图缓冲；最后一次失败后不再等待。
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero)) return;
                int error = Marshal.GetLastWin32Error();
                if (attempt == 3) throw new InvalidOperationException("OpenClipboard 失败，错误码：" + error);
                Thread.Sleep(80);
            }
        }

        private static uint? SetClipboardFormatsCore(IReadOnlyList<ClipboardFormatEntry> entries, Func<bool>? canPublish)
        {
            // 分配阶段：任何一块失败都在「还没 Open 剪贴板」之前抛出，用户内容原封不动。
            var handles = new IntPtr[entries.Count];
            var formats = new uint[entries.Count];
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    formats[i] = ResolveFormatId(entries[i]);
                    byte[] data = entries[i].Data;
                    if (data == null || data.Length == 0)
                    {
                        throw new InvalidOperationException("第 " + i + " 个格式数据为空，放弃还原。");
                    }

                    IntPtr hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)data.Length);
                    if (hGlobal == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("GlobalAlloc 失败，错误码：" + Marshal.GetLastWin32Error());
                    }
                    handles[i] = hGlobal;

                    IntPtr ptr = NativeMethods.GlobalLock(hGlobal);
                    if (ptr == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("GlobalLock 失败，错误码：" + Marshal.GetLastWin32Error());
                    }
                    try
                    {
                        Marshal.Copy(data, 0, ptr, data.Length);
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(hGlobal);
                    }
                }

                OpenPreparedClipboard();
                try
                {
                    if (canPublish != null && !canPublish()) return null;
                    if (!NativeMethods.EmptyClipboard())
                    {
                        throw new InvalidOperationException("EmptyClipboard 失败，错误码：" + Marshal.GetLastWin32Error());
                    }

                    for (int i = 0; i < entries.Count; i++)
                    {
                        uint fmt = formats[i];
                        if (fmt == 0) continue; // 已注册格式名重新注册失败 → 跳过该格式（其余照常）

                        if (NativeMethods.SetClipboardData(fmt, handles[i]) == IntPtr.Zero)
                        {
                            throw new InvalidOperationException("SetClipboardData 失败（格式 " + fmt + "），错误码："
                                + Marshal.GetLastWin32Error());
                        }
                        // 所有权已转交系统，不能再 GlobalFree
                        handles[i] = IntPtr.Zero;
                    }
                    // 在仍持有剪贴板时取序号，不能把 Close 后别人的写入误记为本次发布。
                    return NativeMethods.GetClipboardSequenceNumber();
                }
                finally
                {
                    try { NativeMethods.CloseClipboard(); } catch { }
                    // 走到这里还没交出去的块（含 Set 中途失败时后面的那些）统一释放，避免泄漏
                    {
                        for (int i = 0; i < handles.Length; i++)
                        {
                            if (handles[i] != IntPtr.Zero)
                            {
                                try { NativeMethods.GlobalFree(handles[i]); } catch { }
                                handles[i] = IntPtr.Zero;
                            }
                        }
                    }
                }
            }
            catch
            {
                for (int i = 0; i < handles.Length; i++)
                {
                    if (handles[i] != IntPtr.Zero)
                    {
                        try { NativeMethods.GlobalFree(handles[i]); } catch { }
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// 写回时重新解析格式 id：已注册格式按名字重取（跨进程/跨会话 id 不保证一致），
        /// 取不到名字或注册失败返回 0，调用方跳过该格式。
        /// </summary>
        private static uint ResolveFormatId(ClipboardFormatEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.Name))
            {
                uint id = NativeMethods.RegisterClipboardFormatW(entry.Name!);
                if (id != 0) return id;
            }
            return entry.Format < NativeMethods.CF_REGISTERED ? entry.Format : 0u;
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
                return ReadFormatBytes(format);
            }
            finally
            {
                try { NativeMethods.CloseClipboard(); } catch { }
            }
        }

        /// <summary>
        /// 在<b>已打开</b>的剪贴板上读取指定格式的字节。空/不可用返回 null。
        /// 供 <see cref="GetClipboardBytes"/> 与 <see cref="CaptureAllFormats"/> 共用，
        /// 避免第二份 GlobalSize/GlobalLock/Copy 实现。
        /// </summary>
        private static byte[]? ReadFormatBytes(uint format)
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
