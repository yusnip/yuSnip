using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 截图辅助线几何算法（纯计算，不含 UI）。坐标系与 <see cref="SelectionState"/> 一致：屏幕物理像素。
    ///
    /// 由基准矩形算出竖/横参考线坐标：4 条边 +（尺寸足够时的）2 条中线。
    /// 未起框时基准是当前识别高亮的窗口/元素，起框后基准换成选区自身。
    /// </summary>
    public static class GuidelineEngine
    {
        /// <summary>基准矩形短边小于该值（物理像素）时省略中线，只画两条边线——中线与边线挤在一起没有意义。</summary>
        public const int MidlineMinSpanPx = 60;

        /// <summary>基准矩形的竖参考线：左、右（宽度足够时再加垂直中线）。</summary>
        public static int[] VerticalLines(Rectangle r)
        {
            int left = Math.Min(r.Left, r.Right);
            int right = Math.Max(r.Left, r.Right);
            int span = right - left;
            if (span < MidlineMinSpanPx) return new[] { left, right };
            return new[] { left, left + span / 2, right };
        }

        /// <summary>基准矩形的横参考线：顶、底（高度足够时再加水平中线）。</summary>
        public static int[] HorizontalLines(Rectangle r)
        {
            int top = Math.Min(r.Top, r.Bottom);
            int bottom = Math.Max(r.Top, r.Bottom);
            int span = bottom - top;
            if (span < MidlineMinSpanPx) return new[] { top, bottom };
            return new[] { top, top + span / 2, bottom };
        }
    }
}
