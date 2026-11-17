using System.Drawing;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 长截图预览窗静态布局求解器：复刻 Quicker 的候选位置级联算法。
/// 纯函数、无状态，可单测；纵向 2 个底边对齐候选，横向 2 个右缘对齐候选。
/// 每个候选先钳制到屏幕边界，再与选区（外扩 Margin）/工具条（外扩 ToolbarClearance）
/// 做冲突检测，全部冲突时回退到候选 0 的钳制结果。
/// </summary>
public static class LongScrollPreviewPlacer
{
    /// <summary>预览窗与选区之间的净距（Quicker Margin=10）。</summary>
    public const int Margin = 10;

    /// <summary>预览窗与工具条之间的净距（Quicker ToolbarClearance=8）。</summary>
    public const int ToolbarClearance = 8;

    /// <summary>纵向预览最大宽度（物理像素，Quicker MaxWidth=200）。</summary>
    public const int MaxVerticalWidthPx = 200;

    /// <summary>纵向预览最大高度（物理像素，Quicker MaxHeight=700）。</summary>
    public const int MaxVerticalHeightPx = 700;

    /// <summary>横向预览最大宽度（物理像素，Quicker MaxWidth=700）。</summary>
    public const int MaxHorizontalWidthPx = 700;

    /// <summary>横向预览最大高度（物理像素，Quicker MaxHeight=200）。</summary>
    public const int MaxHorizontalHeightPx = 200;

    public readonly record struct PlacementResult(Rectangle Rect, int CandidateIndex, bool Fallback);

    /// <summary>
    /// 求解预览窗在屏幕上的物理像素位置。
    /// 纵向：[(选区右侧, 底边对齐), (选区左侧, 底边对齐)]，preferLeft 时顺序对调。
    /// 横向：右缘对齐选区右缘（随内容向左生长），preferBottom 时下方优先、否则上方优先。
    /// </summary>
    public static PlacementResult Solve(
        Rectangle capture,
        Rectangle? toolbar,
        ScrollAxis axis,
        int thumbWidth,
        int thumbHeight,
        Rectangle bounds,
        bool preferLeft = false,
        bool preferBottom = true)
        => SolveCore(capture, toolbar, axis, thumbWidth, thumbHeight, bounds, preferLeft, preferBottom, false);

    /// <summary>同一次预览可只求解已选侧，保留原屏幕钳制与冲突标记。</summary>
    internal static PlacementResult SolveCore(Rectangle capture, Rectangle? toolbar, ScrollAxis axis,
        int thumbWidth, int thumbHeight, Rectangle bounds, bool preferLeft, bool preferBottom, bool stayOnPreferredSide)
    {
        if (thumbWidth < 1) thumbWidth = 1;
        if (thumbHeight < 1) thumbHeight = 1;

        Rectangle[] candidates = BuildCandidates(capture, axis, thumbWidth, thumbHeight, preferLeft, preferBottom);

        Rectangle captureClearance = Inflate(capture, Margin);
        Rectangle toolbarClearance = toolbar.HasValue
            ? Inflate(toolbar.Value, ToolbarClearance)
            : Rectangle.Empty;

        int candidateCount = stayOnPreferredSide ? 1 : candidates.Length;
        for (int i = 0; i < candidateCount; i++)
        {
            Rectangle clamped = ClampRect(candidates[i], bounds);
            if (clamped.IntersectsWith(captureClearance)) continue;
            if (!toolbarClearance.IsEmpty && clamped.IntersectsWith(toolbarClearance)) continue;
            return new PlacementResult(clamped, i, Fallback: false);
        }

        // 全部候选冲突：回退候选 0（与 Quicker 行为一致，宁可重叠也不消失）。
        Rectangle fallback = ClampRect(candidates[0], bounds);
        return new PlacementResult(fallback, 0, Fallback: true);
    }

    private static Rectangle[] BuildCandidates(Rectangle capture, ScrollAxis axis, int w, int h, bool preferLeft, bool preferBottom)
    {
        if (axis == ScrollAxis.Horizontal)
        {
            // 右缘锚定选区右缘：随宽度 w 增大向左生长。preferBottom 决定下方/上方谁优先。
            int rx = capture.Right - w;
            Rectangle below = new(rx, capture.Bottom + Margin, w, h);
            Rectangle above = new(rx, capture.Top - Margin - h, w, h);
            return preferBottom ? new[] { below, above } : new[] { above, below };
        }

        Rectangle right = new(capture.Right + Margin, capture.Bottom - h, w, h);
        Rectangle left = new(capture.Left - Margin - w, capture.Bottom - h, w, h);
        return preferLeft ? new[] { left, right } : new[] { right, left };
    }

    private static Rectangle ClampRect(Rectangle rect, Rectangle bounds)
    {
        int x = Math.Clamp(rect.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - rect.Width));
        int y = Math.Clamp(rect.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - rect.Height));
        return new Rectangle(x, y, rect.Width, rect.Height);
    }

    private static Rectangle Inflate(Rectangle rect, int pad)
    {
        return Rectangle.Inflate(rect, pad, pad);
    }
}
