using System;
using System.Drawing;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool;

/// <summary>
/// 记住"最近一次截图"的屏幕区域，供 F3 快速贴图还原位置用。
///
/// 背景：剪贴板里的图是裸 bitmap，不带位置信息（PNG 元数据写在文件里，不在剪贴板）。
/// 但截图流程内部完全知道 screenRect——截图完成（复制/保存）时把区域记下来，
/// F3 贴图时若剪贴板图尺寸与记忆匹配且未过期，就还原原位置；否则居中。
///
/// 非线程安全：调用约定在 UI 线程。静态状态，随进程存活，进程退出即失效。
/// </summary>
internal static class LastCaptureMemory
{
    /// <summary>记忆有效期：超过此时长视为陈旧，F3 不再还原位置。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>上次截图区域的物理像素尺寸（用于和剪贴板图尺寸匹配判断）。</summary>
    public static Size? LastImageSize { get; private set; }

    /// <summary>上次截图区域左上角（屏幕物理像素，多屏可为负）。</summary>
    public static Point LastTopLeft { get; private set; }

    /// <summary>上次截图的 DPI 缩放。</summary>
    public static double LastDpiScale { get; private set; }

    /// <summary>
    /// 上次截图成品的<b>卡片几何</b>（内容轮廓圆角半径 + 描边宽，位图物理像素），供 F3 贴图还原
    /// 窗体阴影的光晕基准线用。剪贴板裸图同样不带这个信息，F3 贴图时窗体阴影要靠它才与卡片外沿贴合；
    /// 只在 <see cref="TryMatch"/> 命中时才有意义（外来图无从得知，保持默认值＝基准线即位图矩形）。
    /// ⚠ 存不得只存一个"外沿半径"：卡片外沿是内容轮廓外扩描边宽的真等距线，单值半径表达不了，
    /// 按同族缩放还原会在角部留一条 0.2312×描边宽的缝。
    /// </summary>
    public static SelectionEffects.CardGeometry LastCardGeometry { get; private set; }

    private static DateTime _recordedAtUtc = DateTime.MinValue;

    /// <summary>记录最近一次截图。</summary>
    /// <param name="screenRect">截图区域（屏幕物理像素）。</param>
    /// <param name="imageSize">裁剪后位图的实际尺寸（物理像素，通常 = screenRect.Size）。</param>
    /// <param name="dpiScale">截屏 DPI 缩放。</param>
    /// <param name="card">成品卡片几何，见 <see cref="LastCardGeometry"/>。</param>
    public static void Record(Rectangle screenRect, Size imageSize, double dpiScale,
        SelectionEffects.CardGeometry card)
    {
        LastTopLeft = new Point(screenRect.X, screenRect.Y);
        LastImageSize = imageSize;
        LastDpiScale = dpiScale > 0 ? dpiScale : 1.0;
        LastCardGeometry = card;
        _recordedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 尝试匹配：若当前剪贴板图尺寸与记忆一致、且未过期，返回原位置；否则 null。
    /// 尺寸按宽度+高度精确匹配（截图→复制→F3 是连贯操作，尺寸必然一致）。
    /// </summary>
    public static Point? TryMatch(int imageWidth, int imageHeight)
    {
        if (LastImageSize is not { } size) return null;
        if (DateTime.UtcNow - _recordedAtUtc > MaxAge) return null;
        if (size.Width != imageWidth || size.Height != imageHeight) return null;
        return LastTopLeft;
    }
}
