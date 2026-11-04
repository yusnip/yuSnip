using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 「恢复上次关闭的贴图」。
///
/// 只有全局动作 / 脚本指令（<c>yuSnip.restoreLastSticker();</c>）一个入口，不设任何界面按钮
/// ——2026-09-06 拍板。恢复即销档，所以连按就能把上一张、上上一张依次拉回来；
/// 恢复出来的贴图若再次关闭，会以同一个历史 Id 重新写档。
///
/// 底图不另存：历史里那张 PNG 就是干净底图，标注是独立图元层、坐标锚在底图物理像素，
/// 恢复＝重放图元（见 <see cref="StickerWindow.ApplyArchiveState"/>）。
/// </summary>
public static class StickerRestoreService
{
    /// <summary>恢复一张。没有可恢复的存档时提示后返回（孤儿存档就地清掉，不算候选）。</summary>
    public static void RestoreLast()
    {
        try
        {
            List<StickerArchive> candidates = StickerArchiveStore.LoadAll();
            if (candidates.Count == 0) { NothingToRestore(); return; }

            var openHistoryIds = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.Windows.OfType<StickerWindow>()
                .Select(w => w.HistoryId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string?>(StringComparer.Ordinal);

            candidates.RemoveAll(a => openHistoryIds.Contains(a.HistoryId));
            if (candidates.Count == 0) { NothingToRestore(); return; }

            bool byCreatedAt = string.Equals(AppSettings.Load().StickerRestoreSortMode,
                "Created", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, DateTime>? createdById = byCreatedAt ? BuildCreatedById() : null;

            while (candidates.Count > 0)
            {
                StickerArchive target = candidates
                    .OrderByDescending(a => SortKey(a, createdById))
                    .First();
                candidates.Remove(target);

                string? png = CaptureHistoryStore.ResolveExistingImagePath(target.HistoryId);
                if (png == null)
                {
                    // 历史条目已被裁掉或删掉，存档成了孤儿：就地清掉，免得每次恢复都空转一轮
                    AppLogger.Info("StickerRestore: 存档对应的历史图片已不存在，清档 " + target.HistoryId);
                    StickerArchiveStore.TryDelete(target.HistoryId);
                    continue;
                }

                if (TryRestore(target, png)) return;
            }

            NothingToRestore();
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerRestore: 恢复贴图异常", ex);
            NothingToRestore();
        }
    }

    /// <summary>排序键：按创建时间模式取历史条目的创建时刻，取不到（条目已被裁）退回关闭时间。</summary>
    private static DateTime SortKey(StickerArchive a, Dictionary<string, DateTime>? createdById)
    {
        if (createdById != null
            && !string.IsNullOrEmpty(a.HistoryId)
            && createdById.TryGetValue(a.HistoryId, out DateTime created))
            return created;
        return a.ClosedAt;
    }

    private static Dictionary<string, DateTime> BuildCreatedById()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (CaptureHistoryItem it in CaptureHistoryStore.LoadAll())
        {
            if (!string.IsNullOrEmpty(it.Id)) map[it.Id] = it.CapturedAt;
        }
        return map;
    }

    private static void NothingToRestore()
    {
        AppLogger.Info("StickerRestore: 没有可恢复的贴图存档");
        StickerToastWindow.ShowAtTop("没有可恢复的贴图");
    }

    /// <summary>
    /// 启动时自动恢复重启或退出前屏幕上打开的贴图。
    /// </summary>
    public static int RestoreActiveSession()
    {
        try
        {
            var session = StickerArchiveStore.LoadActiveSession();
            // 先清理会话文件，避免单张贴图异常时导致下次启动死循环；
            // 恢复成功的贴图在后续就绪后会自然重新注册进活跃会话
            StickerArchiveStore.ClearActiveSession();

            if (session.Count == 0) return 0;

            int restored = 0;
            foreach (var a in session)
            {
                if (string.IsNullOrEmpty(a.HistoryId)) continue;
                string? png = CaptureHistoryStore.ResolveExistingImagePath(a.HistoryId);
                if (png == null)
                {
                    AppLogger.Info("StickerRestore.RestoreActiveSession: 历史底图已不存在，跳过 " + a.HistoryId);
                    continue;
                }

                if (TryRestore(a, png, recordUsage: false))
                {
                    restored++;
                }
            }

            AppLogger.Info($"StickerRestore: 成功恢复会话贴图 {restored}/{session.Count} 张");
            return restored;
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerRestore: 恢复活跃贴图会话异常", ex);
            return 0;
        }
    }

    /// <summary>建窗并回灌现场。位图所有权交给贴图窗口（它在 OnClosed 里释放），失败时才由本方法释放。</summary>
    private static bool TryRestore(StickerArchive a, string pngPath, bool recordUsage = true)
    {
        System.Drawing.Bitmap? bmp = null;
        System.Drawing.Bitmap? cleanBmp = null;
        try
        {
            string? cleanPath = CaptureHistoryStore.ResolveCleanImagePath(a.HistoryId);
            if (cleanPath != null)
            {
                cleanBmp = CaptureHistoryStore.LoadBitmapDetached(cleanPath);
            }

            bmp = CaptureHistoryStore.LoadBitmapDetached(pngPath);
            if (bmp == null) return false;

            var sticker = new StickerWindow(bmp,
                cleanAnnotationSource: cleanBmp,
                annotationShapes: a.Shapes,
                annotationTexts: a.Texts) { RecordUsageOnOpen = recordUsage };
            bmp = null;
            cleanBmp = null;

            // 外键沿用旧 Id：既不重复入历史，也让这张贴图再次关闭时把存档写回同一个文件
            sticker.SetHistoryId(a.HistoryId);

            // 底图外缘的圆角/描边/自带阴影带按存档登记，不必像「历史贴到屏幕」那样从像素回读
            if (a.Card.ContentRadiusPx > 0 || a.Card.StrokeWidthPx > 0 || a.Card.BakedShadow)
            {
                sticker.ApplyBakedCaptureEffects(new SelectionEffects.CardGeometry
                {
                    ContentRadiusPx = (float)a.Card.ContentRadiusPx,
                    StrokeWidthPx = (float)a.Card.StrokeWidthPx,
                }, a.Card.BakedShadow);
            }

            // 先按存档的窗口外框原点预置一次再 Show：否则新窗口先在默认位置出现，
            // 等 Loaded 回灌才挪过来，观感就是"贴图从别处飞过来"。
            sticker.Position = new Avalonia.PixelPoint(a.State.X, a.State.Y);
            sticker.Show();
            sticker.Activate();
            StickerFocusManager.SetFocus(sticker);

            // 现场回灌必须晚于 Opened：OnWindowOpened 会按默认设置覆写阴影开关并跑一次 ApplyScale
            Dispatcher.UIThread.Post(() => sticker.ApplyArchiveState(a), DispatcherPriority.Loaded);

            AppLogger.Info("StickerRestore: 已恢复贴图 " + a.HistoryId
                           + " kind=" + a.Kind + " 关闭于=" + a.ClosedAt.ToString("yyyy-MM-dd HH:mm:ss")
                           + " 底图=" + pngPath);
            return true;
        }
        catch (Exception ex)
        {
            try { cleanBmp?.Dispose(); } catch { }
            try { bmp?.Dispose(); } catch { }
            AppLogger.Error("StickerRestore: 创建贴图失败 " + a.HistoryId, ex);
            return false;
        }
    }
}
