using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using ScreenCaptureTool.Core.Deps;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// OCR／抠图／录屏三处功能入口共用的闸口：缺件就地居中开卡，并等到它就绪。
/// 下载由 <see cref="DependencyManager"/> 自己跑，本类只管把那张卡开在对的线程上——
/// 卡关掉下载照跑，再点同一功能重开即接上当前读数。
/// </summary>
public static class DependencyGate
{
    private static readonly Dictionary<string, DepProgressWindow> Cards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>依赖的中文显示名（进度卡标题、设置页行名共用这一份）。</summary>
    public static string TitleOf(string id) => id switch
    {
        DepIds.Ocr => "OCR 识别引擎",
        DepIds.Matting => "智能抠图模型",
        DepIds.Ffmpeg => "FFmpeg 录屏引擎",
        _ => "运行组件",
    };

    /// <summary>功能入口用：已就绪立刻返回 true；缺件则开卡并等它落位（取消或失败返回 false）。</summary>
    public static async Task<bool> EnsureAsync(string id)
    {
        if (DependencyManager.IsReady(id)) return true;
        ShowCard(id);
        return await DependencyManager.EnsureReadyAsync(id).ConfigureAwait(false);
    }

    /// <summary>同步入口的兜底：开卡并把下载排上队，但不等——调用方本次照常拿到「未就绪」那句人话。</summary>
    public static void KickOff(string id)
    {
        ShowCard(id);
        _ = DependencyManager.EnsureReadyAsync(id);
    }

    /// <summary>开卡（同 id 已开着就只把它带到前台）。调用处可能不在 UI 线程，这里自己转。</summary>
    public static void ShowCard(string id)
    {
        void Open()
        {
            if (Cards.TryGetValue(id, out var existing))
            {
                try { existing.Activate(); } catch { }
                return;
            }
            var card = new DepProgressWindow(id);
            card.Closed += (_, _) => Cards.Remove(id);
            Cards[id] = card;
            card.Show();
        }

        if (Dispatcher.UIThread.CheckAccess()) Open();
        else Dispatcher.UIThread.Post(Open);
    }
}
