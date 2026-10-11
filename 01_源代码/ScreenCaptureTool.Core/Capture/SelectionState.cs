using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture
{
    /// <summary>
    /// 选区调整时的命中方向。<see cref="None"/> 表示未命中任何手柄（点击在选区外或选区内空白）。
    /// 顺序按"顺时针从左上角"排布，便于 UI 计算光标。
    /// </summary>
    public enum HandleDirection
    {
        None = 0,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
    }

    /// <summary>
    /// 选区状态（不含 UI）。物理像素，坐标系与 <see cref="CapturedFrame.VirtualScreenRect"/> 一致。
    ///
    /// 设计原则：
    /// - 本类只持有"当前是什么"和"怎么变到下一帧"，不持有 UI 控件
    /// - 所有 mutation 通过显式方法暴露，不暴露可写属性，便于跟踪状态变迁
    /// - 通过 <see cref="Changed"/> 事件让视觉层订阅
    /// - 不做最小尺寸约束 / 边界裁剪 / Shift 等比缩放 —— 这些是 UI 层职责
    ///
    /// Resize 内部自动处理"翻转"：拖左手柄过了右手柄时矩形自动反向，
    /// 始终保持 Width/Height ≥ 0，外部不需要特判。
    /// </summary>
    public sealed class SelectionState
    {
        /// <summary>当前选区。<see cref="Rectangle.IsEmpty"/> 为 true 表示无选区。</summary>
        public Rectangle Region { get; private set; }

        /// <summary>是否有"非空"选区（宽高都 &gt; 0）。</summary>
        public bool HasSelection => Region.Width > 0 && Region.Height > 0;

        /// <summary>状态变化通知（任何会改 <see cref="Region"/> 的方法都会触发）。</summary>
        public event Action<SelectionState>? Changed;

        // ---------- 状态变更 API ----------

        /// <summary>清空选区。当前已经空就不重复触发事件。</summary>
        public void Clear()
        {
            if (Region.IsEmpty) return;
            Region = Rectangle.Empty;
            RaiseChanged();
        }

        /// <summary>
        /// 用绝对矩形覆盖当前选区（自动检测命中、初始拖框完成后的批量设置都走这里）。
        /// 与当前相同就不触发；负宽/负高会被翻正。
        /// </summary>
        public void SetRegion(Rectangle region)
        {
            Rectangle normalized = Normalize(region);
            if (normalized == Region) return;
            Region = normalized;
            RaiseChanged();
        }

        /// <summary>整体平移选区（不改大小）。dx=dy=0 不触发事件。</summary>
        public void Translate(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            if (Region.IsEmpty) return;
            Region = new Rectangle(Region.X + dx, Region.Y + dy, Region.Width, Region.Height);
            RaiseChanged();
        }

        /// <summary>
        /// 根据起始矩形 + 拖动方向 + 当前指针偏移量，重算选区（缩放/翻转）。
        ///
        /// startRect 是用户按下手柄那一刻的选区快照；dx/dy 是从那时到现在的累计指针偏移。
        /// 内部按方向算出新左/上/右/下，再用 Min/Max 修正翻转。
        /// </summary>
        /// <param name="startRect">按下时的选区</param>
        /// <param name="direction">命中的手柄方向</param>
        /// <param name="dx">指针 X 偏移（屏幕物理像素）</param>
        /// <param name="dy">指针 Y 偏移（屏幕物理像素）</param>
        public void Resize(Rectangle startRect, HandleDirection direction, int dx, int dy)
        {
            if (direction == HandleDirection.None) return;

            int left = startRect.Left;
            int top = startRect.Top;
            int right = startRect.Right;
            int bottom = startRect.Bottom;

            switch (direction)
            {
                case HandleDirection.TopLeft:     left   += dx; top    += dy; break;
                case HandleDirection.Top:                       top    += dy; break;
                case HandleDirection.TopRight:    right  += dx; top    += dy; break;
                case HandleDirection.Right:       right  += dx;               break;
                case HandleDirection.BottomRight: right  += dx; bottom += dy; break;
                case HandleDirection.Bottom:                    bottom += dy; break;
                case HandleDirection.BottomLeft:  left   += dx; bottom += dy; break;
                case HandleDirection.Left:        left   += dx;               break;
            }

            // 翻转修正：若 left > right 或 top > bottom，交换以保持矩形规范
            int newLeft = Math.Min(left, right);
            int newRight = Math.Max(left, right);
            int newTop = Math.Min(top, bottom);
            int newBottom = Math.Max(top, bottom);

            Rectangle next = new Rectangle(newLeft, newTop, newRight - newLeft, newBottom - newTop);
            if (next == Region) return;
            Region = next;
            RaiseChanged();
        }

        // ---------- 命中测试 ----------

        /// <summary>
        /// 点击点对 8 个手柄的命中测试（屏幕物理像素）。无选区时永远返回 None。
        ///
        /// 每个手柄是以"角点/边中点"为中心、边长 2*hitRadius 的正方形。
        /// 角点优先于边中点（重叠时返回角点方向）。
        /// </summary>
        /// <param name="point">屏幕物理像素坐标</param>
        /// <param name="hitRadius">命中容差（半径，像素）。常用值：角 9、边 11</param>
        public HandleDirection HitTestHandle(Point point, int hitRadius)
        {
            if (!HasSelection) return HandleDirection.None;
            if (hitRadius < 1) hitRadius = 1;

            int l = Region.Left;
            int t = Region.Top;
            int r = Region.Right;
            int b = Region.Bottom;
            int cx = l + Region.Width / 2;
            int cy = t + Region.Height / 2;

            // 角点优先：先测 4 个角
            if (HitsHandle(point, l, t, hitRadius)) return HandleDirection.TopLeft;
            if (HitsHandle(point, r, t, hitRadius)) return HandleDirection.TopRight;
            if (HitsHandle(point, r, b, hitRadius)) return HandleDirection.BottomRight;
            if (HitsHandle(point, l, b, hitRadius)) return HandleDirection.BottomLeft;

            // 4 条边中点
            if (HitsHandle(point, cx, t, hitRadius)) return HandleDirection.Top;
            if (HitsHandle(point, r, cy, hitRadius)) return HandleDirection.Right;
            if (HitsHandle(point, cx, b, hitRadius)) return HandleDirection.Bottom;
            if (HitsHandle(point, l, cy, hitRadius)) return HandleDirection.Left;

            return HandleDirection.None;
        }

        /// <summary>
        /// 选区缩放命中测试。先测 8 个手柄；未命中时再测四条边附近，提升边框拖拽体验。
        /// </summary>
        public HandleDirection HitTestResizeDirection(Point point, int handleRadius, int borderGrip)
        {
            HandleDirection handle = HitTestHandle(point, handleRadius);
            if (handle != HandleDirection.None) return handle;
            if (!HasSelection) return HandleDirection.None;
            if (borderGrip < 1) borderGrip = 1;

            int l = Region.Left;
            int t = Region.Top;
            int r = Region.Right;
            int b = Region.Bottom;

            Rectangle extended = Rectangle.FromLTRB(l - borderGrip, t - borderGrip, r + borderGrip, b + borderGrip);
            if (!extended.Contains(point)) return HandleDirection.None;

            bool nearLeft = Math.Abs(point.X - l) <= borderGrip;
            bool nearRight = Math.Abs(point.X - r) <= borderGrip;
            bool nearTop = Math.Abs(point.Y - t) <= borderGrip;
            bool nearBottom = Math.Abs(point.Y - b) <= borderGrip;

            if (nearTop && nearLeft) return HandleDirection.TopLeft;
            if (nearTop && nearRight) return HandleDirection.TopRight;
            if (nearBottom && nearRight) return HandleDirection.BottomRight;
            if (nearBottom && nearLeft) return HandleDirection.BottomLeft;
            if (nearTop) return HandleDirection.Top;
            if (nearRight) return HandleDirection.Right;
            if (nearBottom) return HandleDirection.Bottom;
            if (nearLeft) return HandleDirection.Left;

            return HandleDirection.None;
        }

        // ---------- 内部辅助 ----------

        /// <summary>把可能负宽/负高的矩形翻正成规范形式。</summary>
        private static Rectangle Normalize(Rectangle r)
        {
            int x = r.X;
            int y = r.Y;
            int w = r.Width;
            int h = r.Height;
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            return new Rectangle(x, y, w, h);
        }

        /// <summary>点 (px,py) 是否落在以 (cx,cy) 为中心、半径 radius 的正方形内。</summary>
        private static bool HitsHandle(Point pt, int cx, int cy, int radius)
        {
            return pt.X >= cx - radius && pt.X <= cx + radius
                && pt.Y >= cy - radius && pt.Y <= cy + radius;
        }

        /// <summary>统一触发 <see cref="Changed"/>，避免每个 mutation 方法重复写。</summary>
        private void RaiseChanged() => Changed?.Invoke(this);
    }
}
