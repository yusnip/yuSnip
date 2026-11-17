using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 贴图判重（2026-09-29 用户口径：「同一张图片已经贴过，就不要重复贴图」）。
/// 命中时不新建窗口，改为把屏幕上那张显示出来、置顶激活（<see cref="Reveal"/>）。
///
/// <para>判据取<b>内容</b>而不是 <c>HistoryId</c>：HistoryId 只认同一条历史条目，而剪贴板每贴一次
/// 都会 <c>TryRecord</c> 出一条新的历史，两次贴同一张图的两个 Id 天然不同——按 Id 判等于没判。
/// 图片走逐像素比对（尺寸先筛，不等就早退，所以常态开销是几次字段读取而不是整帧扫描）；
/// 文本 / Markdown / 颜色贴图按源文本判重。</para>
///
/// <para>⚠ 位图比对的对象是贴图自己持有的 <c>_bitmap</c>，<b>每次现读不缓存指纹</b>：译文替换
/// （<c>ReplaceImage</c>）与拖边重排（<c>RefreshTextStickerSnapshotAfterReflow</c>）都会把它换成另一张，
/// 缓存指纹会让"内容已经变了的老贴图"冒充命中，判成假重复。</para>
///
/// <para>刻意不接入的路径：<c>StickerRestoreService</c>（恢复贴图）已有自己的 <c>openHistoryIds</c> 守卫，
/// 属另一件事；智能抠图（<c>autoMatting</c>）那张窗口是抠图工作台而非"贴一张图"，判重会吞掉整次抠图。</para>
/// </summary>
internal static class StickerDedup
{
    /// <summary>命中后统一执行的"定位"动作：从隐藏态拉回来、置顶激活、抢焦点（阴影变蓝就是落点），并提示一句。</summary>
    public static void Reveal(StickerWindow sticker, string from)
    {
        AppLogger.Info($"StickerDedup: {from} 判重命中已有贴图，跳过新建 → "
                       + $"HistoryId={sticker.HistoryId ?? "-"} {sticker.SourcePixelWidth}x{sticker.SourcePixelHeight}");
        try
        {
            // 「全部贴图隐藏」（ToggleAllStickers 走 Hide()）期间那张是隐身的，不 Show 回来用户只会觉得没反应
            if (!sticker.IsVisible) sticker.Show();
            sticker.Activate();
            StickerFocusManager.SetFocus(sticker);
            StickerToastWindow.ShowAtTop("这张已贴过，已定位");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerDedup.Reveal 异常: " + ex.Message);
        }
    }

    /// <summary>同一条历史条目已经贴着的贴图（历史窗口 / 大图查看器那条链的天然键）。</summary>
    public static StickerWindow? FindSameHistory(string? historyId)
    {
        if (string.IsNullOrEmpty(historyId)) return null;
        return OpenStickers().FirstOrDefault(w => w.HistoryId == historyId);
    }

    /// <summary>屏幕上已有逐像素相同的那张图片贴图；没有返回 null。</summary>
    public static StickerWindow? FindSameImage(Bitmap candidate)
    {
        foreach (var w in OpenStickers())
        {
            Bitmap? live = w.DedupBitmap;
            if (live == null) continue;
            if (SamePixels(live, candidate)) return w;
        }
        return null;
    }

    /// <summary>屏幕上已有同样源文本的文本 / Markdown / 颜色贴图。</summary>
    public static StickerWindow? FindSameText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return OpenStickers().FirstOrDefault(w => string.Equals(w.DedupText, text, StringComparison.Ordinal));
    }

    private static IEnumerable<StickerWindow> OpenStickers()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return Array.Empty<StickerWindow>();
        return desktop.Windows.OfType<StickerWindow>().Where(w => !w.IsClosed).ToList();
    }

    /// <summary>
    /// 逐像素比对（统一锁成 32bppArgb，源格式不同也能比）。先比尺寸，再逐行拷贝比较：
    /// 内容不同的两张图通常在第一行就早退，只有真的相同才走完整帧。
    /// 任何异常（位图已释放、锁定失败）一律按"不重复"处理——判重失效的代价只是多贴一张，
    /// 反过来误判会让用户的第二次贴图凭空消失。
    /// </summary>
    private static bool SamePixels(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        int w = a.Width, h = a.Height;
        if (w <= 0 || h <= 0) return false;

        BitmapData? da = null, db = null;
        int[] rowA = new int[w], rowB = new int[w];
        try
        {
            var rect = new Rectangle(0, 0, w, h);
            da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                IntPtr offA = IntPtr.Add(da.Scan0, y * da.Stride);
                IntPtr offB = IntPtr.Add(db.Scan0, y * db.Stride);
                Marshal.Copy(offA, rowA, 0, w);
                Marshal.Copy(offB, rowB, 0, w);
                for (int x = 0; x < w; x++)
                    if (rowA[x] != rowB[x]) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerDedup: 像素比对失败，按不重复处理。" + ex.Message);
            return false;
        }
        finally
        {
            try { if (da != null) a.UnlockBits(da); } catch { }
            try { if (db != null) b.UnlockBits(db); } catch { }
        }
    }
}
