using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Core.History
{
    /// <summary>
    /// 截图历史存储（<c>%APPDATA%/yuSnip/history/</c>）。
    ///
    /// 目录布局（图片按来源分文件夹，方便用户直接进目录浏览）：
    /// <code>
    /// history/
    /// ├── index.ini             索引（制表符分隔行，仿 ActionsStore 格式 + 原子替换写入；
    /// │                         列为 id/时间/宽高/来源/原路径/字节数 + 第 9 列可搜文本，
    /// │                         第 9 列 2026-09-07 v2 才加：旧行照读（按空串起算），
    /// │                         旧版程序读新行也只会忽略多出来的一列，双向兼容）
    /// ├── 截图/{id}.png         全图原始 PNG（Save 与 Clipboard 两档同归）
    /// ├── 长截图/{id}.png       ┐
    /// ├── 贴图/{id}.png         ┘ 按 CaptureHistorySource 分目录
    /// └── thumbs/{截图|长截图|贴图}/{id}.jpg  320px 长边缩略图（JPEG q82，缩略图无需无损）
    /// </code>
    ///
    /// 索引里**只存 id 与来源、不存路径**：图片位置一律由 <see cref="GetFullImagePath"/> 按
    /// <c>id + source</c> 现推，故改目录布局无需动索引格式。代价是来源→目录的映射发布后即冻结
    /// （改映射等于把已有记录全变成找不到文件的孤儿），详见 docs/protection.md。
    ///
    /// 刻意独立于 <see cref="AppSettings"/> 与云同步：历史是本机行为痕迹且是大文件，
    /// 与 <see cref="UsageStatistics"/> 同理，不进 settings.ini、不进配置信封、不随导出走。
    ///
    /// 容量为**双闸**：条数（<see cref="AppSettings.CaptureHistoryMaxCount"/>）与
    /// 总字节（<see cref="AppSettings.CaptureHistoryMaxMegabytes"/>）任一超限即从最旧删起。
    /// 仅按条数裁剪时，4K 全屏 PNG 单张 5~10MB，最坏可堆到 1~2GB。
    ///
    /// 线程模型：内存列表为唯一真相（<see cref="_lock"/> 保护），图片编码与写盘一律在后台线程完成；
    /// 调用方传入的位图会被**同步克隆**一份再交给后台，因此调用方可以照常在 finally 里释放原图。
    /// 全部公开方法都不抛异常（失败只记 Warn）——历史收集是旁路功能，绝不允许影响截图主流程。
    /// </summary>
    public static partial class CaptureHistoryStore
    {
        /// <summary>缩略图长边像素。</summary>
        private const int ThumbMaxEdge = 320;

        /// <summary>缩略图 JPEG 质量。</summary>
        private const long ThumbJpegQuality = 82L;

        private static readonly object _lock = new object();

        /// <summary>是否已从索引文件加载过（懒加载）。</summary>
        private static bool _loaded;

        /// <summary>本进程是否已把子目录化之前平铺在根目录的图片搬进类型子目录（一次性）。</summary>
        private static bool _layoutMigrated;

        /// <summary>内存索引，按时间**升序**（最旧在前，裁剪从头删）。</summary>
        private static readonly List<CaptureHistoryItem> _items = new List<CaptureHistoryItem>();

        /// <summary>id 后缀去重用的递增序号（同一毫秒内多次记录不撞名）。</summary>
        private static int _idSeq;

        /// <summary>
        /// 新记录提交完成（全图与缩略图已落盘、索引已写入）后触发。
        ///
        /// 在**后台线程**回调，订阅方须自行切回 UI 线程；索引读写锁此时已释放，
        /// 订阅方可安全调用本类任何公开方法。本类不订阅、也不依赖任何订阅方。
        /// </summary>
        public static event Action<CaptureHistoryItem>? ItemRecorded;

        /// <summary>
        /// 记录被二次编辑并更新（全图位图、缩略图已重新落盘、索引已刷新）后触发。
        /// 
        /// 在**后台线程**回调，订阅方须自行切回 UI 线程。
        /// </summary>
        public static event Action<CaptureHistoryItem>? ItemUpdated;

        // -------------------- 路径 --------------------

        /// <summary>历史根目录（<c>%APPDATA%/yuSnip/history</c>）。</summary>
        public static string HistoryDirectory
        {
            get { return Path.Combine(AppSettings.SettingsDirectory, "history"); }
        }

        /// <summary>缩略图子目录（其下再按来源分层）。</summary>
        public static string ThumbDirectory
        {
            get { return Path.Combine(HistoryDirectory, "thumbs"); }
        }

        /// <summary>
        /// 贴图存档子目录：一张贴图一个 <c>{id}.json</c>（关窗时写、"恢复上次关闭的贴图"时读）。
        ///
        /// 本类只认这个目录名与"随条目一起删除"这一件事，JSON 格式由贴图侧
        /// （<c>Services/StickerArchiveStore.cs</c>）定义 —— Core 不引用主工程，格式不能下沉到这里。
        /// </summary>
        public static string StickerArchiveDirectory
        {
            get { return Path.Combine(HistoryDirectory, "贴图存档"); }
        }

        /// <summary>干净底图子目录（带标注的截图在被烘焙前留存的未标注底图）。</summary>
        public static string CleanDirectory
        {
            get { return Path.Combine(HistoryDirectory, "clean"); }
        }

        /// <summary>
        /// OCR 行框 sidecar 子目录：一条记录一个 <c>{id}.json</c>（行文本 + 原图像素框）。
        /// 自动识别（CaptureAutoOcrService）跑完即落盘，图片查看器的高亮定位据此**零 OCR** 秒出；
        /// 旧记录没有 sidecar 时调用方回退到现场识别，识别完顺手补写（新记录自此一直命中）。
        /// </summary>
        public static string OcrBoxDirectory
        {
            get { return Path.Combine(HistoryDirectory, "ocr"); }
        }

        /// <summary>索引文件路径。</summary>
        public static string IndexPath
        {
            get { return Path.Combine(HistoryDirectory, "index.ini"); }
        }

        /// <summary>类型子目录名：装 Save 与 Clipboard 两档。</summary>
        private const string SubdirScreenshots = "截图";
        /// <summary>类型子目录名：装 LongScroll。</summary>
        private const string SubdirLong = "长截图";
        /// <summary>类型子目录名：装 Sticker。</summary>
        private const string SubdirSticker = "贴图";

        /// <summary>
        /// 该来源的图片放哪个子目录。
        ///
        /// 🔴 **映射一经发布即冻结**：图片路径由 <c>id + source</c> 现推（索引不存路径），改这里
        /// 等于把已有记录全变成找不到文件的孤儿，并会被 <see cref="LoadAll"/> 的自愈当成僵尸删掉。
        /// 新增来源只能**新增**目录。
        /// </summary>
        private static string SubdirOf(CaptureHistorySource source)
        {
            switch (source)
            {
                case CaptureHistorySource.Sticker: return SubdirSticker;
                case CaptureHistorySource.LongScroll: return SubdirLong;
                // Save 与 Clipboard 同归「截图」；default 同时兜住 ParseSource 认不出的脏值
                default: return SubdirScreenshots;
            }
        }

        /// <summary>指定记录全图 PNG 的**写入**路径（按来源派生类型子目录）。读取请用 <see cref="ResolveExistingImagePath"/>。</summary>
        public static string GetFullImagePath(string id, CaptureHistorySource source)
        {
            return Path.Combine(HistoryDirectory, SubdirOf(source), (id ?? "") + ".png");
        }

        /// <summary>指定记录缩略图的**写入**路径。读取请用 <see cref="ResolveExistingThumbPath"/>。</summary>
        public static string GetThumbPath(string id, CaptureHistorySource source)
        {
            return Path.Combine(ThumbDirectory, SubdirOf(source), (id ?? "") + ".jpg");
        }

        /// <summary>
        /// 找一条记录**已存在**的全图路径，三处类型目录与根目录都试过仍没有则返回 null。
        ///
        /// 刻意不做成"只认派生路径"：<see cref="ParseSource"/> 会把任何认不出的来源字符串兜底成
        /// <c>Save</c>，一旦记录实际落在别的目录（旧版程序回退、映射被改、子目录化前的平铺文件），
        /// 单一判断就会让 <see cref="LoadAll"/> 的自愈把整条索引删掉——那是本类唯一会**丢用户数据**
        /// 的通道，且全类只记 Warn 不抛异常，没有第二种报警口径。
        /// </summary>
        public static string? ResolveExistingImagePath(string id)
        {
            return FirstExisting(FullImageCandidates(id));
        }

        /// <summary>找一条记录已存在的缩略图路径，没有则返回 null（历史窗口据此退化为占位图）。</summary>
        public static string? ResolveExistingThumbPath(string id)
        {
            return FirstExisting(ThumbCandidates(id));
        }

        /// <summary>找一条记录已存在的干净底图路径，没有则返回 null。</summary>
        public static string? ResolveCleanImagePath(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            string path = Path.Combine(CleanDirectory, id + ".png");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// 安全加载 GDI+ 位图并立即脱离文件占用（避免 Windows GDI+ 锁定底层文件导致后续无法覆盖写入）。
        /// </summary>
        public static Bitmap? LoadBitmapDetached(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var temp = new Bitmap(fs);
                return new Bitmap(temp);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.LoadBitmapDetached 失败 " + filePath + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 保存未标注干净底图到 history/clean/{id}.png。
        /// </summary>
        public static string? SaveCleanImage(string id, Bitmap cleanBitmap)
        {
            if (string.IsNullOrWhiteSpace(id) || cleanBitmap == null) return null;
            try
            {
                string dir = CleanDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, id + ".png");
                string tmp = path + ".tmp";
                cleanBitmap.Save(tmp, ImageFormat.Png);
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.SaveCleanImage 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>全图可能所在的候选位置（含子目录化之前的根目录平铺位置）。</summary>
        private static IEnumerable<string> FullImageCandidates(string id)
        {
            return CandidatePaths(HistoryDirectory, id, ".png");
        }

        /// <summary>缩略图可能所在的候选位置。</summary>
        private static IEnumerable<string> ThumbCandidates(string id)
        {
            return CandidatePaths(ThumbDirectory, id, ".jpg");
        }

        private static IEnumerable<string> CandidatePaths(string root, string id, string extension)
        {
            string name = (id ?? "") + extension;
            yield return Path.Combine(root, SubdirScreenshots, name);
            yield return Path.Combine(root, SubdirLong, name);
            yield return Path.Combine(root, SubdirSticker, name);
            yield return Path.Combine(root, name); // 旧布局：直接平铺在根目录
        }

        private static string? FirstExisting(IEnumerable<string> candidates)
        {
            foreach (string path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // -------------------- 查询 --------------------

        /// <summary>当前历史条数。</summary>
        public static int Count
        {
            get { EnsureLoaded(); lock (_lock) return _items.Count; }
        }

        /// <summary>历史全图占用总字节数（不含缩略图，缩略图量级可忽略）。</summary>
        public static long TotalBytes
        {
            get
            {
                EnsureLoaded();
                lock (_lock)
                {
                    long sum = 0;
                    for (int i = 0; i < _items.Count; i++) sum += _items[i].FileBytes;
                    return sum;
                }
            }
        }

        /// <summary>
        /// 读取全部历史，**按时间倒序**（最新在前，历史窗口直接照此顺序铺卡片）。
        /// 顺带自愈：索引里指向的全图已不存在时跳过该条并重写索引。
        /// </summary>
        public static IReadOnlyList<CaptureHistoryItem> LoadAll()
        {
            EnsureLoaded();
            var result = new List<CaptureHistoryItem>();
            bool needRewrite = false;
            lock (_lock)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    CaptureHistoryItem it = _items[i];
                    // 用户手工清理过历史目录：索引自愈，去掉这条僵尸记录。
                    // 判存必须走多候选回退（见 ResolveExistingImagePath），单一路径判断会在这里误删记录。
                    if (ResolveExistingImagePath(it.Id) == null)
                    {
                        _items.RemoveAt(i);
                        needRewrite = true;
                        continue;
                    }
                    result.Add(it);
                }
                if (needRewrite) SaveIndexNoLock();
            }
            if (needRewrite) AppLogger.Info("CaptureHistoryStore: 索引自愈，已剔除图片缺失的记录。");
            return result;
        }

        // -------------------- 档位判定 --------------------

        /// <summary>
        /// 判断当前截图历史是否开启（档位判定的唯一出口，埋点处一律调用本方法）。
        /// 开启时全量收集全部来源（普通截图、贴图、长截图）；关闭时不写入任何历史记录。
        /// </summary>
        public static bool IsEnabledFor(AppSettings? settings, CaptureHistorySource source)
        {
            if (settings == null) return false;
            string mode = AppSettings.NormalizeCaptureHistoryMode(settings.CaptureHistoryMode);
            return string.Equals(mode, "On", StringComparison.Ordinal);
        }

        // -------------------- 录入 --------------------

        /// <summary>
        /// 记录一条截图历史（位图入口）。
        ///
        /// 位图会被**同步克隆**后交给后台线程编码写盘，因此调用方可以照常在 finally 里 Dispose 原图，
        /// 也不受"位图所有权已转移给贴图窗口"的影响。本方法不抛异常。
        /// </summary>
        /// <param name="bitmap">成品位图（已叠标注）。</param>
        /// <param name="source">来源类型。</param>
        /// <param name="savedPath">原始保存路径（仅保存类来源有值，可为空）。</param>
        /// <param name="settings">当前设置（用于档位判定与容量上限）。</param>
        /// <param name="text">可搜文本。文本 / Markdown / 颜色贴图在这里就把**源文本**一并交进来
        /// （录入时刻已知，走 <see cref="TrySetText"/> 事后回写会撞上"后台还没把条目加进列表"的窗口期）；
        /// 图片贴图留空，由 OCR 侧事后回写。</param>
        /// <returns>本条记录的 Id；档位关闭、尺寸非法或克隆失败时返回 <c>null</c>。
        /// 返回 Id 只代表**已受理**（Id 在同步阶段生成），真正落盘完成由 <see cref="ItemRecorded"/> 告知；
        /// 调用方（贴图窗口）拿它当外键回写识别文字与存档，故后台写盘失败时这些回写会静默落空。</returns>
        public static string? TryRecord(Bitmap? bitmap, CaptureHistorySource source, string? savedPath,
            AppSettings? settings, string? text = null)
        {
            try
            {
                if (bitmap == null || settings == null) return null;
                if (!IsEnabledFor(settings, source)) return null;
                if (bitmap.Width <= 0 || bitmap.Height <= 0) return null;

                int w = bitmap.Width;
                int h = bitmap.Height;
                string id = NextId();

                // 同步克隆：几十毫秒内的内存拷贝，换来后台线程独占位图所有权，
                // 彻底规避"调用方 finally 已 Dispose"与"所有权已交给贴图窗口"两种竞态。
                Bitmap clone;
                try
                {
                    clone = bitmap.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureHistoryStore.TryRecord: 克隆位图失败，本条历史跳过。" + ex.Message);
                    return null;
                }

                int maxCount = AppSettings.NormalizeCaptureHistoryMaxCount(settings.CaptureHistoryMaxCount);
                long maxBytes = (long)AppSettings.NormalizeCaptureHistoryMaxMegabytes(settings.CaptureHistoryMaxMegabytes) * 1024L * 1024L;
                string pathCopy = savedPath ?? "";
                string textCopy = ClampText(text);

                Task.Run(() =>
                {
                    try
                    {
                        WriteEntry(clone, id, source, pathCopy, w, h, maxCount, maxBytes, textCopy);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("CaptureHistoryStore: 后台写入历史失败。" + ex.Message);
                    }
                    finally
                    {
                        try { clone.Dispose(); } catch { }
                    }
                });

                return id;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.TryRecord 异常（已忽略，不影响截图流程）。" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 从已落盘的图片文件导入一条历史（文件入口，长截图专用）。
        ///
        /// 长截图的成品位图在会话内就随 <c>using</c> 释放了，结果对象只带回文件路径，
        /// 故这里**直接拷贝文件**而不重新编码（超长图重编码代价过高），读一次图仅为取宽高与生成缩略图。
        /// </summary>
        public static void TryImportFile(string? sourcePath, CaptureHistorySource source, AppSettings? settings)
        {
            try
            {
                if (settings == null) return;
                if (string.IsNullOrWhiteSpace(sourcePath)) return;
                if (!IsEnabledFor(settings, source)) return;
                if (!File.Exists(sourcePath)) return;

                string id = NextId();
                int maxCount = AppSettings.NormalizeCaptureHistoryMaxCount(settings.CaptureHistoryMaxCount);
                long maxBytes = (long)AppSettings.NormalizeCaptureHistoryMaxMegabytes(settings.CaptureHistoryMaxMegabytes) * 1024L * 1024L;
                string src = sourcePath!;

                Task.Run(() =>
                {
                    try
                    {
                        EnsureDirectories();
                        string dest = GetFullImagePath(id, source);
                        // File.Copy 与 GDI+ 的 Save 都不会自建目录，父目录必须显式保证
                        EnsureParentDirectory(dest);
                        File.Copy(src, dest, true);

                        int w = 0, h = 0;
                        try
                        {
                            using var img = new Bitmap(dest);
                            w = img.Width;
                            h = img.Height;
                            string thumbPath = GetThumbPath(id, source);
                            EnsureParentDirectory(thumbPath);
                            SaveThumbnail(img, thumbPath);
                        }
                        catch (Exception ex)
                        {
                            // 缩略图失败不致命：历史窗口会退化为占位图
                            AppLogger.Warn("CaptureHistoryStore.TryImportFile: 生成缩略图失败。" + ex.Message);
                        }

                        long bytes = 0;
                        try { bytes = new FileInfo(dest).Length; } catch { }

                        var item = new CaptureHistoryItem
                        {
                            Id = id,
                            CapturedAt = DateTime.Now,
                            Width = w,
                            Height = h,
                            Source = source,
                            SavedPath = src,
                            FileBytes = bytes,
                        };

                        lock (_lock)
                        {
                            EnsureLoadedNoLock();
                            _items.Add(item);
                            TrimNoLock(maxCount, maxBytes);
                            SaveIndexNoLock();
                        }
                        AppLogger.Info("CaptureHistoryStore: 已导入历史（" + item.SourceDisplayName + "）→ " + id);
                        RaiseItemRecorded(item);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("CaptureHistoryStore: 后台导入历史文件失败。" + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.TryImportFile 异常（已忽略）。" + ex.Message);
            }
        }

        // -------------------- 更新位图与缩略图 --------------------

        /// <summary>
        /// 更新已有截图历史的全图位图与缩略图（用于贴图二次标注编辑后关窗回写最新合成图）。
        /// 
        /// 位图会被同步克隆一份交给后台线程编码写盘，因此调用方可立即释放原图。
        /// 写盘完成后更新索引中的尺寸与文件大小，并在后台线程触发 <see cref="ItemUpdated"/>。
        /// </summary>
        public static bool TryUpdateImage(string? id, Bitmap? compositeBitmap, AppSettings? settings)
        {
            if (string.IsNullOrWhiteSpace(id) || compositeBitmap == null) return false;
            if (compositeBitmap.Width <= 0 || compositeBitmap.Height <= 0) return false;

            int w = compositeBitmap.Width;
            int h = compositeBitmap.Height;

            Bitmap clone;
            try
            {
                clone = compositeBitmap.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.TryUpdateImage: 克隆位图失败。" + ex.Message);
                return false;
            }

            Task.Run(() =>
            {
                try
                {
                    EnsureDirectories();

                    CaptureHistorySource source = CaptureHistorySource.Save;
                    string? existingPng = ResolveExistingImagePath(id);
                    string? existingThumb = ResolveExistingThumbPath(id);

                    lock (_lock)
                    {
                        EnsureLoadedNoLock();
                        for (int i = 0; i < _items.Count; i++)
                        {
                            if (string.Equals(_items[i].Id, id, StringComparison.Ordinal))
                            {
                                source = _items[i].Source;
                                break;
                            }
                        }
                    }

                    string destPng = existingPng ?? GetFullImagePath(id, source);
                    EnsureParentDirectory(destPng);
                    string tmpPng = destPng + ".tmp";
                    clone.Save(tmpPng, ImageFormat.Png);
                    File.Move(tmpPng, destPng, overwrite: true);

                    string destThumb = existingThumb ?? GetThumbPath(id, source);
                    try
                    {
                        EnsureParentDirectory(destThumb);
                        string tmpThumb = destThumb + ".tmp";
                        SaveThumbnail(clone, tmpThumb);
                        File.Move(tmpThumb, destThumb, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("CaptureHistoryStore.TryUpdateImage: 生成缩略图失败。" + ex.Message);
                    }

                    long bytes = 0;
                    try { bytes = new FileInfo(destPng).Length; } catch { }

                    CaptureHistoryItem? updatedItem = null;
                    lock (_lock)
                    {
                        EnsureLoadedNoLock();
                        for (int i = 0; i < _items.Count; i++)
                        {
                            if (string.Equals(_items[i].Id, id, StringComparison.Ordinal))
                            {
                                var it = _items[i];
                                it.Width = w;
                                it.Height = h;
                                it.FileBytes = bytes;
                                updatedItem = it;
                                SaveIndexNoLock();
                                break;
                            }
                        }
                    }

                    AppLogger.Info($"CaptureHistoryStore: 已更新历史图片 {id} ({w}×{h}，{(bytes / 1024L)} KB)");
                    if (updatedItem != null)
                    {
                        RaiseItemUpdated(updatedItem);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureHistoryStore.TryUpdateImage 后台写盘异常: " + ex.Message);
                }
                finally
                {
                    try { clone.Dispose(); } catch { }
                }
            });

            return true;
        }

        // -------------------- 可搜文本回写 --------------------

        /// <summary>索引里可搜文本的长度上限（字符）。OCR 一整屏能出几千字，全存进来会让
        /// 每次写盘都要重写整表；截断后最坏 200 条 × 1000 字，量级仍可忽略。
        /// ⚠ 也是 <c>CaptureAutoOcrService</c> 长截图分段的"够了就停"判据——改这个值等于同时改那条上限。</summary>
        public const int MaxTextChars = 1000;

        /// <summary>
        /// 把识别文字回写到已有条目（图片贴图走 OCR、阶段④给老历史补识别都从这里进）。
        ///
        /// 返回 false 只有一种含义：**这条 Id 不在表里**（后台还没落盘、已被容量裁剪、或从未录入）。
        /// 文本与现值相同则直接返回 true 且**不重写索引**——回填扫描会反复回读同一批条目，
        /// 每次都整表原子替换纯属浪费。
        /// </summary>
        public static bool TrySetText(string? id, string? text)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string value = ClampText(text);
            CaptureHistoryItem? updated = null;
            try
            {
                EnsureLoaded();
                lock (_lock)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (!string.Equals(_items[i].Id, id, StringComparison.Ordinal)) continue;
                        if (_items[i].Text == value) return true;
                        _items[i].Text = value;
                        SaveIndexNoLock();
                        updated = _items[i];
                        break;
                    }
                }
                if (updated == null)
                {
                    AppLogger.Info("CaptureHistoryStore: 回写识别文字落空（条目不在索引里，可能尚未落盘或已被裁剪）" + id);
                    return false;
                }
                AppLogger.Info("CaptureHistoryStore: 已回写识别文字 " + id + " 字数=" + value.Length);
                // 锁外通知，与 TryUpdateImage 同一口径：识别文字回写后，已经开着的历史窗口要跟着换摘要、
                // 重跑筛选。此前这条路径一个事件都不发，卡片就永远停在"尚未识别"。
                RaiseItemUpdated(updated);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.TrySetText 失败。" + ex.Message);
                return false;
            }
        }

        /// <summary>截断到 <see cref="MaxTextChars"/>；null 与全空白一律归一成空串（"没识别过"与"识别出空白"同一个值）。</summary>
        private static string ClampText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Trim();
            return s.Length <= MaxTextChars ? s : s.Substring(0, MaxTextChars);
        }

        // -------------------- OCR 行框 sidecar --------------------

        /// <summary>sidecar JSON 的行条目：t=行文本，x/y/w/h=原图像素框。</summary>
        internal sealed class OcrBoxDto
        {
            public string? t { get; set; }
            public float x { get; set; }
            public float y { get; set; }
            public float w { get; set; }
            public float h { get; set; }
        }

        /// <summary>sidecar JSON 根对象。</summary>
        internal sealed class OcrBoxFile
        {
            public int v { get; set; } = 1;
            public List<OcrBoxDto>? lines { get; set; }
        }

        [JsonSerializable(typeof(OcrBoxFile))]
        [JsonSourceGenerationOptions(WriteIndented = false)]
        internal partial class OcrBoxJsonContext : JsonSerializerContext
        {
        }

        /// <summary>一条记录的行框 sidecar 路径。</summary>
        public static string GetOcrBoxPath(string? id)
        {
            return Path.Combine(OcrBoxDirectory, (id ?? "") + ".json");
        }

        /// <summary>
        /// 把一次识别得到的行框持久化到 sidecar（临时文件 + 原子替换，防读到半截 JSON）。
        /// 失败静默返回 false：sidecar 只是加速手段，丢了无非退化成现场识别。
        /// </summary>
        public static bool TrySetOcrBoxes(string? id, IReadOnlyList<OcrLineRegion> lines)
        {
            if (string.IsNullOrWhiteSpace(id) || lines == null || lines.Count == 0) return false;
            try
            {
                var file = new OcrBoxFile
                {
                    lines = lines.Select(l => new OcrBoxDto
                    {
                        t = l.Text,
                        x = l.Bounds.X,
                        y = l.Bounds.Y,
                        w = l.Bounds.Width,
                        h = l.Bounds.Height,
                    }).ToList(),
                };
                string path = GetOcrBoxPath(id);
                EnsureParentDirectory(path);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, OcrBoxJsonContext.Default.OcrBoxFile));
                File.Move(tmp, path, true);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.TrySetOcrBoxes 失败 " + id + "：" + ex.Message);
                return false;
            }
        }

        /// <summary>读回持久化的行框；没有 sidecar 或解析失败返回 null（调用方回退到现场识别）。</summary>
        public static List<OcrLineRegion>? TryGetOcrBoxes(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try
            {
                string path = GetOcrBoxPath(id);
                if (!File.Exists(path)) return null;
                var file = JsonSerializer.Deserialize(File.ReadAllText(path), OcrBoxJsonContext.Default.OcrBoxFile);
                if (file?.lines == null || file.lines.Count == 0) return null;
                var result = new List<OcrLineRegion>(file.lines.Count);
                int idx = 0;
                foreach (var d in file.lines)
                {
                    result.Add(new OcrLineRegion
                    {
                        Text = d.t ?? "",
                        Bounds = new RectangleF(d.x, d.y, d.w, d.h),
                        LineIndex = idx++,
                    });
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        // -------------------- 删除 --------------------

        /// <summary>删除单条历史（图片 + 缩略图 + 索引行）。返回是否真的删掉了一条。</summary>
        public static bool Delete(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            try
            {
                EnsureLoaded();
                lock (_lock)
                {
                    int idx = -1;
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (string.Equals(_items[i].Id, id, StringComparison.Ordinal)) { idx = i; break; }
                    }
                    if (idx < 0) return false;
                    DeleteFilesNoLock(_items[idx]);
                    _items.RemoveAt(idx);
                    SaveIndexNoLock();
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.Delete 失败。" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 清空全部历史（删除所有图片、缩略图与索引）。类型子目录会留成空壳，属预期——
        /// 与旧版不删 <c>thumbs/</c> 本身同理，空目录不占空间且下次写文件要复用。
        /// </summary>
        public static void ClearAll()
        {
            try
            {
                lock (_lock)
                {
                    EnsureLoadedNoLock();
                    for (int i = 0; i < _items.Count; i++) DeleteFilesNoLock(_items[i]);
                    _items.Clear();
                    SaveIndexNoLock();
                }
                AppLogger.Info("CaptureHistoryStore: 已清空截图历史。");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore.ClearAll 失败。" + ex.Message);
            }
        }

        // -------------------- 内部：写入 --------------------

        /// <summary>后台线程：编码全图 PNG 与缩略图、写索引、按双闸裁剪。<c>text</c> 已由调用侧 ClampText 截断。</summary>
        private static void WriteEntry(Bitmap image, string id, CaptureHistorySource source, string savedPath,
            int width, int height, int maxCount, long maxBytes, string text)
        {
            EnsureDirectories();

            string pngPath = GetFullImagePath(id, source);
            EnsureParentDirectory(pngPath);
            image.Save(pngPath, ImageFormat.Png);

            try
            {
                string thumbPath = GetThumbPath(id, source);
                EnsureParentDirectory(thumbPath);
                SaveThumbnail(image, thumbPath);
            }
            catch (Exception ex) { AppLogger.Warn("CaptureHistoryStore: 生成缩略图失败。" + ex.Message); }

            long bytes = 0;
            try { bytes = new FileInfo(pngPath).Length; } catch { }

            var item = new CaptureHistoryItem
            {
                Id = id,
                CapturedAt = DateTime.Now,
                Width = width,
                Height = height,
                Source = source,
                SavedPath = savedPath,
                FileBytes = bytes,
                Text = text,
            };

            lock (_lock)
            {
                EnsureLoadedNoLock();
                _items.Add(item);
                TrimNoLock(maxCount, maxBytes);
                SaveIndexNoLock();
            }
            AppLogger.Info("CaptureHistoryStore: 已记录历史（" + item.SourceDisplayName + "）→ " + id
                + "（" + width + "×" + height + "，" + (bytes / 1024L) + " KB）");
            RaiseItemRecorded(item);
        }

        /// <summary>锁外通知订阅方。历史是旁路功能，订阅方（UI 侧）抛异常绝不允许影响写盘。</summary>
        private static void RaiseItemRecorded(CaptureHistoryItem item)
        {
            try { ItemRecorded?.Invoke(item); }
            catch (Exception ex) { AppLogger.Warn("CaptureHistoryStore: 历史新增通知失败。" + ex.Message); }
        }

        /// <summary>锁外通知订阅方。条目更新后通知历史窗口刷新缩略图与尺寸信息。</summary>
        private static void RaiseItemUpdated(CaptureHistoryItem item)
        {
            try { ItemUpdated?.Invoke(item); }
            catch (Exception ex) { AppLogger.Warn("CaptureHistoryStore: 历史更新通知失败。" + ex.Message); }
        }

        /// <summary>把位图按长边 <see cref="ThumbMaxEdge"/> 等比缩放存为 JPEG。</summary>
        private static void SaveThumbnail(Bitmap source, string destPath)
        {
            int w = source.Width;
            int h = source.Height;
            double scale = (double)ThumbMaxEdge / Math.Max(w, h);
            if (scale > 1.0) scale = 1.0; // 小图不放大
            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));

            using var thumb = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                // JPEG 无 alpha：透明图先铺白底，否则透明区会编码成黑块
                g.Clear(Color.White);
                g.DrawImage(source, new Rectangle(0, 0, tw, th));
            }

            ImageCodecInfo? jpegCodec = GetJpegCodec();
            if (jpegCodec == null)
            {
                thumb.Save(destPath, ImageFormat.Jpeg);
                return;
            }
            using var epArr = new EncoderParameters(1);
            // Encoder 必须完全限定：System.Text 与 System.Drawing.Imaging 各有一个同名类型
            using var ep = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, ThumbJpegQuality);
            epArr.Param[0] = ep;
            thumb.Save(destPath, jpegCodec, epArr);
        }

        private static ImageCodecInfo? GetJpegCodec()
        {
            try
            {
                ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
                for (int i = 0; i < codecs.Length; i++)
                {
                    if (codecs[i].FormatID == ImageFormat.Jpeg.Guid) return codecs[i];
                }
            }
            catch { }
            return null;
        }

        /// <summary>只保证历史根目录存在（供 index.ini 落地）；类型子目录由写文件前的 <see cref="EnsureParentDirectory"/> 负责。</summary>
        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(HistoryDirectory);
        }

        /// <summary>
        /// 自建目标文件的父目录。必须紧贴每个写点调用：GDI+ 的 <c>Bitmap.Save</c> 与 <c>File.Copy</c>
        /// 都不建目录，漏一处就是整条记录静默丢失（png）或永久无缩略图且无重建入口（jpg）。
        /// </summary>
        private static void EnsureParentDirectory(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>生成时间有序且不重名的 id。</summary>
        private static string NextId()
        {
            int seq;
            lock (_lock) { seq = (_idSeq = (_idSeq + 1) % 1000); }
            return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + seq.ToString("D3", CultureInfo.InvariantCulture);
        }

        // -------------------- 内部：裁剪 --------------------

        /// <summary>
        /// 双闸裁剪（须在 <see cref="_lock"/> 内调用）：条数或总字节任一超限就从最旧删起。
        /// **保底留住最新一条**——单张图本身就超字节上限时不能把历史裁成空的。
        /// </summary>
        private static void TrimNoLock(int maxCount, long maxBytes)
        {
            while (_items.Count > 1)
            {
                long total = 0;
                for (int i = 0; i < _items.Count; i++) total += _items[i].FileBytes;
                if (_items.Count <= maxCount && total <= maxBytes) break;

                CaptureHistoryItem oldest = _items[0];
                DeleteFilesNoLock(oldest);
                _items.RemoveAt(0);
                AppLogger.Info("CaptureHistoryStore: 历史超限，已裁剪最旧记录 " + oldest.Id);
            }
        }

        /// <summary>
        /// 删除一条记录的图片文件（须在 <see cref="_lock"/> 内调用）。
        /// 全图与缩略图的**所有候选位置**都删一遍：搬迁半途失败时新旧两处可能同时有文件，只删一处会留孤儿。
        /// </summary>
        private static void DeleteFilesNoLock(CaptureHistoryItem item)
        {
            foreach (string p in FullImageCandidates(item.Id)) TryDeleteFile(p);
            foreach (string t in ThumbCandidates(item.Id)) TryDeleteFile(t);
            // 存档必须跟着条目一起走：条目没了存档还在，"恢复上次关闭的贴图"就会恢复出
            // 一张找不到底图的孤儿（贴图侧按 id 去 history/贴图存档/ 取 JSON，不校验 PNG 是否存在）。
            TryDeleteFile(Path.Combine(StickerArchiveDirectory, (item.Id ?? "") + ".json"));
            TryDeleteFile(Path.Combine(CleanDirectory, (item.Id ?? "") + ".png"));
            // OCR 行框 sidecar 同理：条目没了还留着，只会是永远读不到的孤儿文件
            TryDeleteFile(Path.Combine(OcrBoxDirectory, (item.Id ?? "") + ".json"));
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // -------------------- 内部：索引读写 --------------------

        /// <summary>
        /// 读取侧的加载闸（公开入口专用）：解析索引 + 一次性把旧布局的平铺文件搬进类型子目录。
        ///
        /// 搬迁刻意留在**锁内**而不是丢后台线程：历史窗口的读点全是"先判存在再打开"，
        /// 判存与打开之间文件被搬走就会抛 FileNotFoundException。代价是这段磁盘元数据 I/O 期间
        /// <see cref="NextId"/> 会等锁——同卷 rename 不复制数据、每进程只跑一次，量级可忽略。
        /// 写入侧（<see cref="WriteEntry"/> / <see cref="TryImportFile"/>）走
        /// <see cref="EnsureLoadedNoLock"/>，不参与搬迁。
        /// </summary>
        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                EnsureLoadedNoLock();
                MigrateLegacyLayoutNoLock();
            }
        }

        /// <summary>须在 <see cref="_lock"/> 内调用。把子目录化之前平铺的图片按来源搬进类型子目录。</summary>
        private static void MigrateLegacyLayoutNoLock()
        {
            if (_layoutMigrated) return;
            _layoutMigrated = true; // 失败也不重试：读取侧的多候选回退保证搬不动的记录照样可用

            int moved = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                CaptureHistoryItem item = _items[i];
                // 一条一个 try：某张图被预览窗口/杀软占用时只跳过它，不影响其余记录
                try
                {
                    if (MoveIntoPlace(Path.Combine(HistoryDirectory, item.Id + ".png"),
                            GetFullImagePath(item.Id, item.Source))) moved++;
                    if (MoveIntoPlace(Path.Combine(ThumbDirectory, item.Id + ".jpg"),
                            GetThumbPath(item.Id, item.Source))) moved++;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CaptureHistoryStore: 历史图片搬迁失败，留在原位。" + item.Id + " — " + ex.Message);
                }
            }

            if (moved > 0) AppLogger.Info("CaptureHistoryStore: 已把 " + moved + " 个历史图片文件搬进类型子目录。");
        }

        /// <summary>
        /// 把旧位置的文件搬进新位置，返回是否真的搬了。
        /// 旧位置不存在、或新位置已有一份（上一轮搬迁半途而废的残留）都不动——
        /// 后者留给删除侧的"候选位置全删一遍"处理，避免搬覆盖搬。
        /// </summary>
        private static bool MoveIntoPlace(string legacyPath, string destPath)
        {
            if (!File.Exists(legacyPath)) return false;
            if (File.Exists(destPath)) return false;
            EnsureParentDirectory(destPath);
            File.Move(legacyPath, destPath);
            return true;
        }

        /// <summary>须在 <see cref="_lock"/> 内调用。读取失败按空表起算，不反复重试。</summary>
        private static void EnsureLoadedNoLock()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(IndexPath)) return;
                string[] lines = File.ReadAllLines(IndexPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                    string[] parts = line.Split('\t');
                    // item \t id \t unixMs \t w \t h \t source \t savedPath \t bytes [\t text]
                    // 第 9 列 text 是 v2 才加的：只有前 8 列的旧行照样能读，按"没识别过"起算
                    if (parts.Length < 8) continue;
                    if (!string.Equals(parts[0], "item", StringComparison.Ordinal)) continue;

                    string id = Unescape(parts[1]);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixMs)) continue;
                    int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w);
                    int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
                    long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes);

                    _items.Add(new CaptureHistoryItem
                    {
                        Id = id,
                        CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().DateTime,
                        Width = w,
                        Height = h,
                        Source = ParseSource(parts[5]),
                        SavedPath = Unescape(parts[6]),
                        FileBytes = bytes,
                        // 第 9 列可能不存在（v1 旧行）；读侧也夹一次，防手工编辑出的超长列污染内存表
                        Text = parts.Length > 8 ? ClampText(Unescape(parts[8])) : "",
                    });
                }
                // 索引理应已按时间升序，但手工编辑或异常写入可能乱序，统一兜一次
                _items.Sort((a, b) => a.CapturedAt.CompareTo(b.CapturedAt));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore: 读取历史索引失败，按空表起算。" + ex.Message);
            }
        }

        /// <summary>须在 <see cref="_lock"/> 内调用。UTF-8 无 BOM，原子替换写入。</summary>
        private static void SaveIndexNoLock()
        {
            try
            {
                EnsureDirectories();
                var sb = new StringBuilder();
                sb.Append("# yuSnip capture history v2 — 截图历史索引（本机数据，不参与云同步与导出）。请勿手工编辑。\n");
                sb.Append("# 列为 id/时间/宽高/来源/原路径/字节数；v2 新增第 9 列可搜文本（文本类贴图＝源文本，图片贴图＝OCR 识别文字，制表符与换行已转义）。\n");
                for (int i = 0; i < _items.Count; i++)
                {
                    CaptureHistoryItem it = _items[i];
                    sb.Append("item\t")
                        .Append(Escape(it.Id)).Append('\t')
                        .Append(new DateTimeOffset(it.CapturedAt).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(it.Width.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(it.Height.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(it.Source.ToString()).Append('\t')
                        .Append(Escape(it.SavedPath)).Append('\t')
                        .Append(it.FileBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(Escape(it.Text)).Append('\n');
                }

                string tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(IndexPath)) File.Delete(IndexPath);
                File.Move(tmp, IndexPath);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CaptureHistoryStore: 写入历史索引失败。" + ex.Message);
            }
        }

        private static CaptureHistorySource ParseSource(string s)
        {
            if (string.Equals(s, "Clipboard", StringComparison.OrdinalIgnoreCase)) return CaptureHistorySource.Clipboard;
            if (string.Equals(s, "Sticker", StringComparison.OrdinalIgnoreCase)) return CaptureHistorySource.Sticker;
            if (string.Equals(s, "LongScroll", StringComparison.OrdinalIgnoreCase)) return CaptureHistorySource.LongScroll;
            return CaptureHistorySource.Save;
        }

        private static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string? s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n switch
                    {
                        't' => '\t',
                        'r' => '\r',
                        'n' => '\n',
                        '\\' => '\\',
                        _ => n,
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
