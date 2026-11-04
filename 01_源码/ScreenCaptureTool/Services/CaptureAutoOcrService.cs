using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.History;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 截图产出后的后台静默文字识别（设置项 <c>CaptureAutoOcr</c>，默认关闭）。
///
/// 订阅 <see cref="CaptureHistoryStore.ItemRecorded"/>——该回调已在**后台线程**、且在全图与缩略图
/// 落盘与索引写入之后，因此这里可以直接按 id 回读图片文件，不必再等任何 IO。
/// 识别出的文字经 <see cref="CaptureHistoryStore.TrySetText"/> 写回索引，历史窗口的关键字检索即刻可用。
///
/// 与贴图侧的两条通路互不影响：贴图的自动识别在 <c>StickerWindow</c> 关窗时写全，存量补识别在
/// <see cref="StickerTextBackfillService"/>；贴图来源在本服务里直接跳过。
///
/// 刻意简化的三处：
/// - **不排队并发**：<c>PaddleOcrService</c> 整段持一把内部锁，多开 worker 无收益。
/// - **失败不记名单、不写空串**：本服务只在录入那一刻跑一次，不会每轮重启重跑；而空串在库里已同时
///   表示"没识别过"与"识别出空白"，写进去没有额外信息，反而丢掉了后续补识别的机会。
/// - **不做进度提示**：需求明确要求后台静默。
/// </summary>
internal static class CaptureAutoOcrService
{
    /// <summary>待识别条目（id + 来源）。来源决定走整图还是长截图分段通路。</summary>
    private readonly record struct Job(string Id, CaptureHistorySource Source);

    private static readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();

    /// <summary>0/1 标志：保证同一时刻只有一个 worker 在跑（串行消费，不并发推理）。</summary>
    private static int _running;

    /// <summary>
    /// 长截图分段的重叠像素。判据不是估算而是实测：探针（2026-09-07）量得 720/1200/2560 三档宽度、
    /// 五种段高下**最长行高 19.2~25.8px**，取 80px ≈ 3 倍最长行高留余量。
    /// 重叠只需 ≥ 单行字高即可保证"每条行文字都能被某一段完整看到"，再大只是多花钱。
    /// </summary>
    private const int StripOverlapPx = 80;

    /// <summary>
    /// 单张长截图的段数硬上限。实测一段（1200 宽 × 段高）推理 2.8~6.2 秒，6 段最坏约 30 秒，
    /// 覆盖约 8000 像素高 ≈ 5 个 1080 屏；而索引只留前 <see cref="CaptureHistoryStore.MaxTextChars"/> 字，
    /// 实测这 1000 字在密排文字页上约 700 像素就吃满了——所以走到本上限的只会是"文字很稀疏"的页面。
    /// </summary>
    private const int MaxStrips = 6;

    /// <summary>连续这么多段一个字都没有就收手：长截图是连续页面，开头两屏无字说明这页没文字可搜。</summary>
    private const int MaxEmptyStripsInARow = 2;

    /// <summary>段与段之间让前台识别插队的安静窗口（贴图补识别用的是 3000ms，那里是批量、这里是单张）。</summary>
    private const int ForegroundQuietMs = 2500;

    /// <summary>
    /// 历史新增条目 → 按需排队识别。全程不上抛：调用方是历史写盘的收尾路径，抛出去会污染它的日志。
    /// </summary>
    public static void OnItemRecorded(CaptureHistoryItem item)
    {
        try
        {
            // 每次现读，不缓存：设置改完立即生效，不必重启
            if (!AppSettings.Current.CaptureAutoOcr) return;
            if (item == null || string.IsNullOrEmpty(item.Id)) return;

            // 贴图已由既有通路写全，不在本服务范围
            if (item.Source == CaptureHistorySource.Sticker) return;

            // 录入时就带文字的（文本/颜色贴图、手动识别过的）不重跑
            if (!string.IsNullOrWhiteSpace(item.Text)) return;

            _queue.Enqueue(new Job(item.Id, item.Source));
            EnsureWorker();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureAutoOcr: 入队失败（不影响截图流程）—" + ex.Message);
        }
    }

    private static void EnsureWorker()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_queue.TryDequeue(out Job job)) await ProcessAsync(job).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
            // 清零之后、上面那次 TryDequeue 失败之前若有新条目入队，入队方看到的是"worker 还在跑"
            // 就不会再启动；这里补看一眼，否则那条要等到下次录入才被动 drain。
            if (!_queue.IsEmpty) EnsureWorker();
        });
    }

    private static async Task ProcessAsync(Job job)
    {
        string? path = CaptureHistoryStore.ResolveExistingImagePath(job.Id);
        if (path == null)
        {
            AppLogger.Info("CaptureAutoOcr: 图片文件还没落地或已被裁剪，跳过 " + job.Id);
            return;
        }

            var sw = Stopwatch.StartNew();
            try
            {
                using var bmp = new Bitmap(path);
                var ocr = CaptureSession.GetSharedOcrService();

                string text;
                List<OcrLineRegion>? boxes = null;
                if (job.Source == CaptureHistorySource.LongScroll && bmp.Height > LongStripBase(bmp.Width))
                {
                    // 长截图分段识别：逐段拼文字的同时收集行框（段内坐标平移到整图坐标系）
                    (text, boxes) = await RecognizeLongScrollAsync(ocr, bmp, job.Id).ConfigureAwait(false);
                }
                else
                {
                    // 普通截图：直接走行框接口（与旧 ExtractTextAsync 同一遍推理），文字与框一次拿全
                    var lines = await ocr.ExtractLinesWithBoxesAsync(bmp).ConfigureAwait(false);
                    boxes = lines;
                    var sb = new StringBuilder();
                    foreach (var line in lines)
                    {
                        string t = line.Text.Trim();
                        if (t.Length == 0) continue;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(t);
                    }
                    text = sb.ToString();
                }

                sw.Stop();
                text = text.Trim();
                if (text.Length == 0)
                {
                    AppLogger.Info("CaptureAutoOcr: 未识别到文字 " + job.Id + " " + bmp.Width + "×" + bmp.Height
                        + " 耗时=" + sw.ElapsedMilliseconds + "ms");
                    return;
                }
                CaptureHistoryStore.TrySetText(job.Id, text);
                // 行框落盘：图片查看器的高亮定位据此免二次识别（0 毫秒秒出）
                if (boxes != null && boxes.Count > 0)
                    CaptureHistoryStore.TrySetOcrBoxes(job.Id, boxes);
                AppLogger.Info("CaptureAutoOcr: 已写入可搜文本 " + job.Id + " 来源=" + job.Source
                    + " 字数=" + text.Length + " 行框=" + (boxes?.Count ?? 0)
                    + " 耗时=" + sw.ElapsedMilliseconds + "ms");
            }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Warn("CaptureAutoOcr: 单条识别失败 " + job.Id + "（耗时=" + sw.ElapsedMilliseconds + "ms）— " + ex.Message);
        }
    }

    /// <summary>
    /// 段高：<c>max(960, 宽)</c>。检测阶段把输入的**长边一律缩放到 960**（<c>PaddleOcrService.DetMaxSideLen</c>），
    /// 一张 1200×14560 的长截图整图直送会被压成 99×960，实测只出 6 行全垃圾（见 CHANGELOG）。段高取到
    /// "宽"这一档时，长边就是宽本身——再高就会让高度成为长边、把字压得更小，更低只是多花段数。
    /// 4096 与精度无关，只是给超宽屏（5K 等）封顶单段克隆位图的内存：段高低于宽度时长边仍是宽度，精度不变。
    /// </summary>
    private static int LongStripBase(int width) => Math.Max(960, Math.Min(width, 4096));

    /// <summary>
    /// 长截图分段识别：按段高切条逐段送引擎，按"行顶落在本段独占区"决定每条行的归属（重叠区因此不会
    /// 把同一句写两遍），拼到索引上限就收手。行框同步收集（段内坐标 + 段顶偏移 = 整图坐标）供 sidecar 落盘。
    /// </summary>
    private static async Task<(string Text, List<OcrLineRegion> Boxes)> RecognizeLongScrollAsync(PaddleOcrService ocr, Bitmap full, string id)
    {
        int stripH = LongStripBase(full.Width);
        int stride = stripH - StripOverlapPx;
        var sb = new StringBuilder();
        var allBoxes = new List<OcrLineRegion>();
        int strips = 0;
        int emptyRun = 0;
        // 前台请求 OCR 时都会经 GetSharedOcrService 刷新这个时间戳；段间发现它变了就说明有人插队过
        DateTime stamp = CaptureSession.LastOcrRequestUtc;

        for (int top = 0; top < full.Height; top += stride)
        {
            int h = Math.Min(stripH, full.Height - top);
            if (h <= 0) break;
            strips++;

            using var strip = full.Clone(new Rectangle(0, top, full.Width, h), PixelFormat.Format32bppArgb);
            var lines = await ocr.ExtractLinesWithBoxesAsync(strip).ConfigureAwait(false);

            int zoneEnd = top + stride;
            int added = 0;
            foreach (var line in lines)
            {
                // 行顶落进下一段的独占区 → 这条归下一段（下一段带着 80px 重叠，一定能完整看到它）
                if (top + line.Bounds.Top >= zoneEnd) continue;
                string t = line.Text.Trim();
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
                added += t.Length + 1;
            }
            emptyRun = added == 0 ? emptyRun + 1 : 0;

            // 够了就停：索引只留前 1000 字，再往下识别的部分根本进不了库
            if (sb.Length >= CaptureHistoryStore.MaxTextChars) break;
            if (emptyRun >= MaxEmptyStripsInARow) break;
            if (strips >= MaxStrips)
            {
                AppLogger.Info("CaptureAutoOcr: 长截图已达段数上限提前收手 " + id + " 段数=" + strips
                    + " 已覆盖高度=" + (top + h) + "px / 总高=" + full.Height + "px");
                break;
            }

            // 让路：整段推理持锁 3~6 秒，段与段之间锁是放开的，但 .NET 的 lock 不保证排队公平，
            // 所以显式等前台安静下来再续下一段，而不是赌下一段抢得过用户。
            if (CaptureSession.LastOcrRequestUtc != stamp) await WaitUntilOcrQuietAsync().ConfigureAwait(false);
            stamp = CaptureSession.LastOcrRequestUtc;
        }

        AppLogger.Info("CaptureAutoOcr: 长截图分段完成 " + id + " " + full.Width + "×" + full.Height
            + " 段高=" + stripH + " 重叠=" + StripOverlapPx + " 用了 " + strips + " 段 / 拼出 " + sb.Length + " 字"
            + " / 行框 " + allBoxes.Count);
        return (sb.ToString(), allBoxes);
    }

    /// <summary>等共享 OCR 安静：连续 <see cref="ForegroundQuietMs"/> 内没人再请求才继续。</summary>
    private static async Task WaitUntilOcrQuietAsync()
    {
        while (true)
        {
            DateTime seen = CaptureSession.LastOcrRequestUtc;
            await Task.Delay(ForegroundQuietMs).ConfigureAwait(false);
            if (CaptureSession.LastOcrRequestUtc == seen) return;
        }
    }
}
