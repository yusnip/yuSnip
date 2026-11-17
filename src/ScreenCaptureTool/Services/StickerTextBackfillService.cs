using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.History;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 存量贴图历史的「补做文字识别」。
///
/// 阶段③的搜索只认索引里的可搜文本，而本次更新之前入库的贴图记录没有这段文字（新贴图已由
/// <c>StickerWindow</c> 在录入与关窗时写全）。这里在程序空闲时把缺文字的旧贴图逐条补跑一次 OCR，
/// 补完就能搜到。自动跑、不设开关、不弹进度（2026-09-06 拍板）。
///
/// 只补「贴图」：截图与长截图本轮不做识别（设计稿 §08 排期口径）。
///
/// 每条只跑一次：跑过的 id 记进 <c>ocr_tried.txt</c>。<see cref="CaptureHistoryStore"/> 把
/// "没识别过"与"识别出空白"归一成同一个空串，没有这份名单，识别不出字的贴图每次开机都会被重跑。
/// </summary>
internal static class StickerTextBackfillService
{
    /// <summary>启动后先等这么久再动手：避让开机自启恢复贴图、更新检查这些前台动作。</summary>
    private const int StartupDelaySeconds = 30;

    /// <summary>两次补跑之间的间隔，给用户自己的识别请求留出插队的空档。</summary>
    private const int PerItemIntervalMs = 1500;

    /// <summary>判"安静"的采样窗口：连续这么久没人碰共享 OCR 才下手。</summary>
    private const int QuietWindowMs = 3000;

    /// <summary>单轮上限。超出的留到下次启动，避免一轮跑几十分钟。</summary>
    private const int MaxItemsPerRun = 200;

    private static string TriedPath => Path.Combine(CaptureHistoryStore.HistoryDirectory, "ocr_tried.txt");

    /// <summary>
    /// 延后启动一轮补识别。fire-and-forget 调用，任何异常都在内部吞掉，不影响主流程。
    /// </summary>
    public static async Task RunScheduledAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds)).ConfigureAwait(false);

            // 用户已关掉贴图收集就别在后台跑识别：这条走的是既有开关，不是新增设置项
            if (!CaptureHistoryStore.IsEnabledFor(AppSettings.Current, CaptureHistorySource.Sticker))
            {
                AppLogger.Info("StickerTextBackfill: 贴图历史未开启收集，跳过补识别。");
                return;
            }

            var tried = LoadTriedIds();
            var pending = new List<string>();
            foreach (CaptureHistoryItem item in CaptureHistoryStore.LoadAll())
            {
                if (item.Source != CaptureHistorySource.Sticker) continue;
                if (!string.IsNullOrEmpty(item.Text)) continue;
                if (tried.Contains(item.Id)) continue;
                pending.Add(item.Id);
                if (pending.Count >= MaxItemsPerRun) break;
            }
            PruneTried(tried);

            if (pending.Count == 0)
            {
                AppLogger.Info("StickerTextBackfill: 没有待补识别的贴图记录。");
                return;
            }

            AppLogger.Info("StickerTextBackfill: 待补贴图 " + pending.Count + " 条，开始空闲补跑。");
            int withText = 0;
            int blank = 0;
            foreach (string id in pending)
            {
                string? path = CaptureHistoryStore.ResolveExistingImagePath(id);
                if (path == null)
                {
                    // 图片文件已丢失：标记掉，别每轮白跑一次
                    MarkTried(id);
                    blank++;
                    continue;
                }

                await WaitUntilOcrQuietAsync().ConfigureAwait(false);
                string text = "";
                try
                {
                    using var bmp = new System.Drawing.Bitmap(path);
                    text = await CaptureSession.GetSharedOcrService().ExtractTextAsync(bmp).ConfigureAwait(false) ?? "";
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("StickerTextBackfill: 单条识别失败 " + id + " — " + ex.Message);
                }

                MarkTried(id);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    CaptureHistoryStore.TrySetText(id, text);
                    withText++;
                }
                else blank++;

                await Task.Delay(PerItemIntervalMs).ConfigureAwait(false);
            }
            AppLogger.Info("StickerTextBackfill: 本轮结束，补出文字 " + withText + " 条 / 无文字或失败 " + blank + " 条。");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerTextBackfill 异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 等共享 OCR 安静下来。判据是"最近一个采样窗口内没人请求过 OCR"——
    /// 推理整段持有 <c>PaddleOcrService._lock</c>，抢进去会让用户自己的识别多等 0.3–1.2 秒。
    /// </summary>
    private static async Task WaitUntilOcrQuietAsync()
    {
        while (true)
        {
            DateTime seen = CaptureSession.LastOcrRequestUtc;
            await Task.Delay(QuietWindowMs).ConfigureAwait(false);
            if (CaptureSession.LastOcrRequestUtc == seen) return;
        }
    }

    /// <summary>读已试过的 id 名单。文件按当前索引剪枝，只留仍然存在的记录。</summary>
    private static HashSet<string> LoadTriedIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(TriedPath)) return set;
            foreach (string line in File.ReadAllLines(TriedPath))
            {
                string id = line.Trim();
                if (id.Length > 0) set.Add(id);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerTextBackfill: 读取补跑名单失败，本轮按空名单处理 — " + ex.Message);
        }
        return set;
    }

    /// <summary>名单里已被删除或裁剪掉的 id 顺手清一次，避免文件只涨不落。</summary>
    private static void PruneTried(HashSet<string> loaded)
    {
        try
        {
            var alive = new HashSet<string>(StringComparer.Ordinal);
            foreach (CaptureHistoryItem item in CaptureHistoryStore.LoadAll()) alive.Add(item.Id);

            var keep = new List<string>(loaded.Count);
            foreach (string id in loaded) if (alive.Contains(id)) keep.Add(id);
            if (keep.Count == loaded.Count) return;

            var sb = new StringBuilder(keep.Count * 26);
            for (int i = 0; i < keep.Count; i++) sb.Append(keep[i]).Append('\n');
            File.WriteAllText(TriedPath, sb.ToString());
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerTextBackfill: 剪枝补跑名单失败（不影响本轮）— " + ex.Message);
        }
    }

    private static readonly object _writeLock = new object();

    private static void MarkTried(string id)
    {
        try
        {
            lock (_writeLock) File.AppendAllText(TriedPath, id + "\n");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerTextBackfill: 写补跑名单失败 " + id + " — " + ex.Message);
        }
    }
}
