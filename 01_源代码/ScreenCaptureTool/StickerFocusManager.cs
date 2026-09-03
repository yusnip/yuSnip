using System;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool;

/// <summary>
/// 贴图焦点协调器：保证同一时间只有一个贴图处于"焦点"状态。
///
/// 设计要点：
/// - 用 <see cref="WeakReference{T}"/> 持有焦点贴图，贴图窗口被关闭/GC 时不被本管理器阻止回收。
/// - <see cref="SetFocus"/> 会先通知旧焦点失焦（阴影变灰），再通知新焦点获焦（阴影变蓝）。
/// - 焦点切换只改贴图的"焦点态"显示，不动其"阴影开关"（阴影开关由 Y 键单独管理）。
///
/// 非线程安全：所有调用约定在 UI 线程（贴图窗口事件、热键回调都在 UI 线程）。
/// </summary>
internal static class StickerFocusManager
{
    private static WeakReference<StickerWindow>? _focusedRef;

    /// <summary>当前焦点贴图；若已被回收或关闭返回 null。</summary>
    public static StickerWindow? Focused
    {
        get
        {
            if (_focusedRef == null) return null;
            return _focusedRef.TryGetTarget(out StickerWindow? w) && StickerWindow.IsAlive(w) ? w : null;
        }
    }

    /// <summary>
    /// 让指定贴图获得焦点。旧焦点（若有）自动失焦。
    /// 若 <paramref name="window"/> 本就已是焦点，直接返回。
    /// </summary>
    public static void SetFocus(StickerWindow window)
    {
        if (window == null) return;

        StickerWindow? current = Focused;
        if (ReferenceEquals(current, window)) return;

        if (current != null) current.NotifyUnfocused();

        _focusedRef = new WeakReference<StickerWindow>(window);
        window.NotifyFocused();
    }

    /// <summary>
    /// 清除焦点。仅当 <paramref name="window"/> 是当前焦点时生效（避免误清别人）。
    /// </summary>
    public static void ClearFocus(StickerWindow window)
    {
        if (window == null) return;
        StickerWindow? current = Focused;
        if (!ReferenceEquals(current, window)) return;

        window.NotifyUnfocused();
        _focusedRef = null;
    }
}
