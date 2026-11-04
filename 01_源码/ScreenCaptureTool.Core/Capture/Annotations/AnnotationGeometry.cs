using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 标注图元的纯几何换算："拖到哪里＝图形变成什么样"两侧共用一份解。
/// 全部在图元自身几何所在的内容坐标系里计算，不含屏幕、DPI 与缩放假设，故一律 <see langword="static"/> 无状态。
/// 与 <see cref="AnnotationHitTest"/> 的分工：这里回答"变成什么"，那里回答"点到了什么"。
/// </summary>
public static class AnnotationGeometry
{
    /// <summary>由对角两点构造规整矩形。拖拽方向任意（向左上拖也成立），不产出负宽高。</summary>
    public static RectangleF NormalizeRect(PointF start, PointF end)
    {
        float left = Math.Min(start.X, end.X);
        float top = Math.Min(start.Y, end.Y);
        float right = Math.Max(start.X, end.X);
        float bottom = Math.Max(start.Y, end.Y);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    /// <summary>
    /// 按住 Shift 的对角拖拽锁成 1:1：边长取两向位移绝对值的较小者（锁成"能画下的那个正方形"，而不是溢出的一边），
    /// 四个拖拽象限各自保留方向；<paramref name="bounds"/> 非空时边长再被"起点到该方向的那点余量"夹一次，
    /// 于是贴边起笔也画得出正方形、只是画不满。截图侧 bounds＝选区，贴图侧＝干净底图的内容矩形。
    /// </summary>
    public static RectangleF SquareFromDrag(PointF start, PointF end, RectangleF? bounds)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float signX = dx < 0 ? -1.0f : 1.0f;
        float signY = dy < 0 ? -1.0f : 1.0f;
        float side = Math.Min(Math.Abs(dx), Math.Abs(dy));

        if (bounds is RectangleF b)
        {
            float availableX = signX >= 0 ? b.Right - start.X : start.X - b.Left;
            float availableY = signY >= 0 ? b.Bottom - start.Y : start.Y - b.Top;
            side = Math.Min(side, Math.Max(0.0f, availableX));
            side = Math.Min(side, Math.Max(0.0f, availableY));
        }

        return NormalizeRect(start, new PointF(start.X + signX * side, start.Y + signY * side));
    }

    /// <summary>
    /// 按住 Shift 拉直线时的角度吸附：吸附至最接近的 45° 步进倍数（水平 0°/180°、垂直 90°/270°、
    /// 对角线 45°/135°/225°/315°），并在几何上强制消除浮点抖动（水平 dy=0、垂直 dx=0、对角线 |dx|==|dy|）。
    /// 画笔、箭头与序号引线三支共用这一份，两侧不得各写一份（2026-09-20 自截图侧上提）。
    /// </summary>
    public static PointF SnapStraightLine(PointF start, PointF current)
    {
        float dx = current.X - start.X;
        float dy = current.Y - start.Y;
        double dist = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (dist < 1.0) return current;

        double angleDeg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
        const double stepDeg = 45.0;
        double snappedDeg = Math.Round(angleDeg / stepDeg) * stepDeg;
        int normDeg = ((int)Math.Round(snappedDeg) % 360 + 360) % 360;

        if (normDeg == 0 || normDeg == 180)
        {
            // 严格水平线：dy 严格为 0
            float targetX = normDeg == 0 ? (float)(start.X + dist) : (float)(start.X - dist);
            return new PointF(targetX, start.Y);
        }
        if (normDeg == 90 || normDeg == 270)
        {
            // 严格垂直线：dx 严格为 0
            float targetY = normDeg == 90 ? (float)(start.Y + dist) : (float)(start.Y - dist);
            return new PointF(start.X, targetY);
        }
        if (normDeg == 45 || normDeg == 135 || normDeg == 225 || normDeg == 315)
        {
            // 严格 45° 对角线：水平与垂直偏移绝对值严格相等 (|dx| == |dy|)
            float offset = (float)(dist / Math.Sqrt(2.0));
            float diagX = (normDeg == 45 || normDeg == 315) ? (start.X + offset) : (start.X - offset);
            float diagY = (normDeg == 45 || normDeg == 135) ? (start.Y + offset) : (start.Y - offset);
            return new PointF(diagX, diagY);
        }

        double rad = snappedDeg * (Math.PI / 180.0);
        return new PointF(
            (float)(start.X + dist * Math.Cos(rad)),
            (float)(start.Y + dist * Math.Sin(rad)));
    }

    /// <summary>
    /// 按 8 向手柄的位移求新矩形。<paramref name="dx"/>/<paramref name="dy"/> 为相对上一次的位置增量，
    /// 只推动该方向对应的边；拖过对边时靠 <see cref="NormalizeRect"/> 归正＝手柄穿越，行为与截图侧一致。
    /// </summary>
    public static RectangleF ResizeRect(RectangleF start, HandleDirection direction, float dx, float dy)
    {
        float left = start.Left;
        float top = start.Top;
        float right = start.Right;
        float bottom = start.Bottom;
        switch (direction)
        {
            case HandleDirection.TopLeft: left += dx; top += dy; break;
            case HandleDirection.Top: top += dy; break;
            case HandleDirection.TopRight: right += dx; top += dy; break;
            case HandleDirection.Right: right += dx; break;
            case HandleDirection.BottomRight: right += dx; bottom += dy; break;
            case HandleDirection.Bottom: bottom += dy; break;
            case HandleDirection.BottomLeft: left += dx; bottom += dy; break;
            case HandleDirection.Left: left += dx; break;
        }
        return NormalizeRect(new PointF(left, top), new PointF(right, bottom));
    }

    /// <summary>
    /// 聚光灯的矩形夹取：把四条边各自夹进 <paramref name="bounds"/>。截图侧 bounds＝选区，
    /// 贴图侧＝干净底图的内容矩形；传 null＝不夹（截图无选区时的既有行为）。
    /// </summary>
    public static RectangleF ClampRect(RectangleF rect, RectangleF bounds)
    {
        float left = Math.Clamp(rect.Left, bounds.Left, bounds.Right);
        float top = Math.Clamp(rect.Top, bounds.Top, bounds.Bottom);
        float right = Math.Clamp(rect.Right, bounds.Left, bounds.Right);
        float bottom = Math.Clamp(rect.Bottom, bounds.Top, bounds.Bottom);
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    /// <summary>聚光灯整体平移：宽高不变，被 bounds 挡住时贴着边停下（比"拖不动"更准的是"拖到边就停"）。</summary>
    public static RectangleF TranslateAndClampRect(RectangleF start, float dx, float dy, RectangleF? bounds)
    {
        if (bounds is not RectangleF b) return new RectangleF(start.X + dx, start.Y + dy, start.Width, start.Height);
        float x = start.X + dx;
        float y = start.Y + dy;
        if (start.Width <= b.Width) x = Math.Clamp(x, b.Left, b.Right - start.Width);
        else x = b.Left;
        if (start.Height <= b.Height) y = Math.Clamp(y, b.Top, b.Bottom - start.Height);
        else y = b.Top;
        return new RectangleF(x, y, Math.Min(start.Width, b.Width), Math.Min(start.Height, b.Height));
    }

    /// <summary>
    /// 聚光灯 8 向缩放：先按手柄求新矩形再夹进 bounds；<paramref name="minSide"/> 之下整次作废
    /// （回到起始矩形），否则继续拖会出现一根线一样细的聚光灯。
    /// </summary>
    public static RectangleF ResizeAndClampRect(RectangleF start, HandleDirection direction, float dx, float dy,
        RectangleF? bounds, float minSide = 10.0f)
    {
        RectangleF resized = ResizeRect(start, direction, dx, dy);
        if (bounds is RectangleF b) resized = ClampRect(resized, b);
        if (resized.Width < minSide || resized.Height < minSide) return start;
        return resized;
    }

    /// <summary>
    /// 一段拖动手势的解："从 <paramref name="startShape"/> 的几何出发，抓着 <paramref name="target"/> 这个把手
    /// 从 <paramref name="start"/> 拖到 <paramref name="current"/>，图元变成什么"。截图遮罩与贴图标注共用这一份，
    /// 两侧"拖箭头端点改长度角度""拖 8 向手柄缩放"从此是同一个算式。
    /// <para>⚠ 曾经的第三档"拖四角手柄改圆角"已整体退役（2026-09-18）：圆角改由元素上方的圆角浮条直接给值，
    /// 不经位移换算，故本方法里没有、也不该有圆角档。</para>
    ///
    /// <para>几何取自 <paramref name="startShape"/>（按下那一刻的克隆，故不做增量累加、不会漂），
    /// 其余属性一律取自 <paramref name="currentShape"/>（文档现值）——拖动途中滚轮改过的粗细、缩放档、
    /// 序号半径不能被回退。预览与提交都调本方法，差别只在宿主拿到结果写图层还是写文档。</para>
    /// </summary>
    public static AnnotationShape? EditShape(AnnotationShape startShape, AnnotationShape currentShape,
        AnnotationEditTarget target, PointF start, PointF current, RectangleF? spotlightBounds = null)
    {
        float dx = current.X - start.X;
        float dy = current.Y - start.Y;

        switch (startShape)
        {
            case ArrowShape sa when currentShape is ArrowShape ca:
            {
                PointF s = sa.Start, e = sa.End, c = sa.ControlPoint;
                switch (target.Kind)
                {
                    case AnnotationEditKind.ArrowStart:
                        s = Offset(s, dx, dy);
                        c = Offset(c, dx / 2f, dy / 2f);   // 弯折点跟着起点走一半，弧线不会翻面
                        break;
                    case AnnotationEditKind.ArrowEnd:
                        e = Offset(e, dx, dy);
                        c = Offset(c, dx / 2f, dy / 2f);
                        break;
                    case AnnotationEditKind.ArrowBend:
                        c = Offset(c, dx, dy);
                        break;
                    default:
                        s = Offset(s, dx, dy);
                        e = Offset(e, dx, dy);
                        c = Offset(c, dx, dy);
                        break;
                }
                return new ArrowShape(s, e, ca.Color, ca.Thickness, ca.Style, ca.Scale, ca.Id, c);
            }
            case RectangleShape sr when currentShape is RectangleShape cr:
            {
                // 只剩平移 / 8 向缩放两档（2026-09-18 圆角手柄退役后，命中侧不再产出圆角 target）。
                // 圆角半径独立于几何，故一律取自 currentShape＝文档现值，与粗细/颜色同一口径：
                // 缩放与平移都不该把它抹掉；改圆角走元素上方的圆角浮条，不经这里。填充档同理独立于几何。
                return new RectangleShape(EditedRect(sr.Rect, target, dx, dy), cr.StrokeColor, cr.StrokeThickness,
                    cr.ShapeKind, cr.LineStyle, cr.CornerRadius, cr.FillOpacity, cr.Id);
            }
            case BlurShape br when currentShape is BlurShape cb:
                return cb.WithRect(EditedRect(br.Rect, target, dx, dy));
            case CounterShape sc when currentShape is CounterShape cc:
            {
                if (target.Kind == AnnotationEditKind.CounterTail)
                {
                    PointF? t = sc.Tail.HasValue
                        ? Offset(sc.Tail.Value, dx, dy)
                        : Offset(sc.Center, dx, dy);
                    // 弯折点跟着端点走一半，与箭头端点那一句同一条（2026-09-19）：只挪端点会把
                    // 原本弯着的引线拉直、甚至反着拧过去。
                    return cc.WithTail(t, sc.TailControl is PointF k ? Offset(k, dx / 2f, dy / 2f) : null);
                }
                if (target.Kind == AnnotationEditKind.CounterTailBend)
                {
                    // 基准取"手柄该显示在哪"而不是存下来的控制点：没打弯时那个位置是直线中点，
                    // 于是第一次拖中间这枚手柄就自然生出一个控制点，不必先判"有没有弯"。
                    return cc.WithTailControl(
                        AnnotationRenderOptions.GetCounterTailControlPoint(sc) is PointF b
                            ? Offset(b, dx, dy) : null);
                }
                // Translated 而不是只挪圆心：引线与说明文字是同一块图元的一部分，
                // 只挪 Center 会在拖动途中把引线拉断（端点留在原地）。
                return cc.Translated(dx, dy);
            }
            case StrokeShape ss when currentShape is StrokeShape cs:
                return new StrokeShape(ss.Samples, cs.Color, cs.Thickness, cs.Profile, cs.LineStyle, cs.Id)
                    .Translate(dx, dy);
            case SpotlightShape sp when currentShape is SpotlightShape csp:
            {
                // 聚光灯没有 CornerRadius 档（2026-09-18 摘除四角圆角手柄后，命中侧已不再产出该 target）：
                // 圆角改由「选中后元素上方浮条」直接给值，不经拖动位移换算，故这里只剩平移与 8 向缩放。
                RectangleF baseRect = spotlightBounds is RectangleF sb ? ClampRect(sp.Rect, sb) : sp.Rect;
                RectangleF next = target.Kind == AnnotationEditKind.Resize
                    ? ResizeAndClampRect(baseRect, target.Direction, dx, dy, spotlightBounds)
                    : TranslateAndClampRect(baseRect, dx, dy, spotlightBounds);
                return csp.WithRect(next);
            }
        }
        return null;
    }

    /// <summary>
    /// 整体平移一个图元（方向键步进与拖动本体共用）。聚光灯传 <paramref name="spotlightBounds"/> 时
    /// 与拖动手势走同一条夹取，故"步进到边缘"和"拖到边缘"结果一致。
    /// </summary>
    public static AnnotationShape? Translate(AnnotationShape shape, float dx, float dy,
        RectangleF? spotlightBounds = null)
        => EditShape(shape, shape, new AnnotationEditTarget(AnnotationEditKind.Move),
            PointF.Empty, new PointF(dx, dy), spotlightBounds);

    static RectangleF EditedRect(RectangleF start, AnnotationEditTarget target, float dx, float dy)
        => target.Kind == AnnotationEditKind.Resize
            ? ResizeRect(start, target.Direction, dx, dy)
            : new RectangleF(start.X + dx, start.Y + dy, start.Width, start.Height);

    static PointF Offset(PointF p, float dx, float dy) => new(p.X + dx, p.Y + dy);
}
