using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 标注图元命中测试。截图遮罩与贴图标注共用这一份实现，两侧"Ctrl 点选哪个图元"的判定
/// 因此永远一致；新增图元类型时只需改这里，不必在两个宿主各写一遍。
/// **手柄命中同样只在这里**（8 向缩放、箭头端点/弯折），任何一侧都不再另存一份手柄表。
/// （圆角手柄那一档已于 2026-09-18 整体退役，圆角改由元素上方的浮条直接给值，见 <c>Controls/SpotlightRadiusPill</c>。）
///
/// <para>坐标系：全部以**图元自身几何所在的内容坐标系**（截图＝选区位图、贴图＝干净底图，均 0 起点物理像素）
/// 传入 <paramref name="pt"/>，故各查找函数里的**几何量**（thickness、radius、Rect）天然同单位。</para>
///
/// <para><paramref name="tol"/>：屏幕像素口径的点击容差换算倍率。截图侧遮罩与位图 1:1，传默认 1f；
/// 贴图侧 1 内容像素在屏幕上只有 <c>scale</c> 物理像素，放大倍数须由宿主换算成 <c>1/scale</c> 传入。
/// 容差**只乘在字面常量上**（30/25/15/10/8 与手柄的 7/10/18 这些"多少个屏幕像素"的手感值），
/// 几何派生量本身已在调用者单位里、不再乘，否则等于把图元画粗了一倍。</para>
/// </summary>
public static class AnnotationHitTest
{
    /// <summary>点是否落在矩形内（含边界）。无容差，截图与贴图同一口径。</summary>
    public static bool Contains(RectangleF rect, PointF pt)
    {
        return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    /// <summary>
    /// 箭头手柄命中：起点＝<see cref="HandleDirection.Left"/>、终点＝<see cref="HandleDirection.Right"/>、
    /// 弯曲控制点＝<see cref="HandleDirection.Center"/>。三处各 10 个屏幕像素的手感半径，容差只乘在这个字面常量上。
    /// </summary>
    public static HandleDirection HitTestArrowHandle(ArrowShape arrow, PointF pt, float tol = 1.0f)
    {
        const float HandleRadiusPx = 10.0f;
        float radius = HandleRadiusPx * tol;
        float r2 = radius * radius;

        float dx = pt.X - arrow.Start.X;
        float dy = pt.Y - arrow.Start.Y;
        if (dx * dx + dy * dy <= r2) return HandleDirection.Left; // 起点手柄

        dx = pt.X - arrow.End.X;
        dy = pt.Y - arrow.End.Y;
        if (dx * dx + dy * dy <= r2) return HandleDirection.Right; // 终点手柄

        // 中间控制端点（弯折）
        float mdx = pt.X - arrow.ControlPoint.X;
        float mdy = pt.Y - arrow.ControlPoint.Y;
        if (mdx * mdx + mdy * mdy <= r2) return HandleDirection.Center;

        return HandleDirection.None;
    }

    /// <summary>是否点中箭头的贝塞尔控制点（弯曲中点）。</summary>
    public static bool IsArrowMidpointHit(ArrowShape arrow, PointF pt, float tol = 1.0f)
        => HitTestArrowHandle(arrow, pt, tol) == HandleDirection.Center;

    /// <summary>是否点中箭头的起点/终点/中点任一端点。与截图侧手柄命中同一组判定条件。</summary>
    public static bool IsArrowHandleHit(ArrowShape arrow, PointF pt, float tol = 1.0f)
        => HitTestArrowHandle(arrow, pt, tol) != HandleDirection.None;

    /// <summary>
    /// 8 向缩放手柄：四个角 + 四条边中点，命中半径 7 个屏幕像素（同样只乘字面常量）。
    /// 马赛克/模糊、矩形、聚光灯三种图元共用这张手柄表——绘制侧 `AnnotationLayer` 也是按同一组位置画的，
    /// 改这里必须同时改那头的绘制，否则就是"看得见抓不住"的手柄。
    /// </summary>
    public static HandleDirection HitTestBlurHandle(RectangleF rect, PointF pt, float tol = 1.0f)
    {
        const float HandleRadiusPx = 7.0f;
        if (rect.Width <= 0 || rect.Height <= 0) return HandleDirection.None;
        (HandleDirection Dir, PointF Pt)[] handles =
        {
            (HandleDirection.TopLeft, new PointF(rect.Left, rect.Top)),
            (HandleDirection.Top, new PointF(rect.Left + rect.Width / 2.0f, rect.Top)),
            (HandleDirection.TopRight, new PointF(rect.Right, rect.Top)),
            (HandleDirection.Right, new PointF(rect.Right, rect.Top + rect.Height / 2.0f)),
            (HandleDirection.BottomRight, new PointF(rect.Right, rect.Bottom)),
            (HandleDirection.Bottom, new PointF(rect.Left + rect.Width / 2.0f, rect.Bottom)),
            (HandleDirection.BottomLeft, new PointF(rect.Left, rect.Bottom)),
            (HandleDirection.Left, new PointF(rect.Left, rect.Top + rect.Height / 2.0f)),
        };

        float radius = HandleRadiusPx * tol;
        float r2 = radius * radius;
        foreach ((HandleDirection dir, PointF hp) in handles)
        {
            float dx = pt.X - hp.X;
            float dy = pt.Y - hp.Y;
            if (dx * dx + dy * dy <= r2) return dir;
        }
        return HandleDirection.None;
    }

    /// <summary>
    /// 抓到已有图元的哪个把手——两侧唯一的派发入口，先问手柄、再落本体。
    /// 前提：<paramref name="shape"/> 已由 <see cref="FindTopmost"/> 之类的本体命中判定选出，故落不到手柄时
    /// 一律按"拖走整个图元"返回。<paramref name="isSelected"/>＝该图元当下是否处于选中态：
    /// 手柄只在选中后才给抓（截图与贴图同一条"先点选、再拖把"规则），本体任何时候都能直接拖。
    /// <para>⚠ 曾经的圆角手柄那一档已整体退役（2026-09-18）：聚光灯先摘、矩形同日跟上，
    /// 故"矩形 + 圆角"这一族如今只剩 8 向缩放与本体拖动两条路。圆角改由元素上方浮出的圆角浮条直接给值。</para>
    /// </summary>
    public static AnnotationEditTarget HitTestEditTarget(AnnotationShape shape, PointF pt,
        float tol = 1.0f, bool isSelected = true)
    {
        var move = new AnnotationEditTarget(AnnotationEditKind.Move);
        if (!isSelected) return move;

        switch (shape)
        {
            case ArrowShape arrow:
                return HitTestArrowHandle(arrow, pt, tol) switch
                {
                    HandleDirection.Left => new AnnotationEditTarget(AnnotationEditKind.ArrowStart),
                    HandleDirection.Right => new AnnotationEditTarget(AnnotationEditKind.ArrowEnd),
                    HandleDirection.Center => new AnnotationEditTarget(AnnotationEditKind.ArrowBend),
                    _ => move,
                };
            case SpotlightShape spotlight:
                // 聚光灯没有手柄档（2026-09-18 四角圆角手柄退役）：只剩 8 向缩放与本体拖动。
                return ResizeOrMove(spotlight.Rect, pt, tol, move);
            case BlurShape blur:
                return ResizeOrMove(blur.Rect, pt, tol, move);
            case RectangleShape rectangle:
                // 矩形图元同样没有手柄档（2026-09-18 同日跟聚光灯一起摘）：圆角改走元素上方浮条，
                // 画布上只剩 8 向缩放与本体拖动。椭圆档本来就没有圆角概念，两者现在完全同一条路。
                // ⚠ 手柄位置恒按外接矩形取点，与"未填充时点选只认线条"是两回事：手柄只在选中后才画出来，
                //   四条边中点那 4 枚本就落在线条上，四角与边中点连成的带也在容差带内。
                return ResizeOrMove(rectangle.Rect, pt, tol, move);
            case CounterShape counter:
                // 端点先判、中点后判：引线很短时两枚手柄的 10 像素带会重叠，而"改指向"比"改弧度"语义更强。
                if (HitTestCounterTailHandle(counter, pt, tol))
                    return new AnnotationEditTarget(AnnotationEditKind.CounterTail);
                if (HitTestCounterTailBendHandle(counter, pt, tol))
                    return new AnnotationEditTarget(AnnotationEditKind.CounterTailBend);
                return move;
        }
        // 画笔本来就没有手柄，任何情况下都是整体拖动
        return move;
    }

    static AnnotationEditTarget ResizeOrMove(RectangleF rect, PointF pt, float tol, AnnotationEditTarget move)
    {
        HandleDirection dir = HitTestBlurHandle(rect, pt, tol);
        return dir == HandleDirection.None ? move : AnnotationEditTarget.Resize(dir);
    }

    /// <summary>模糊/马赛克：矩形体内命中即算，取最上层（后画的胜出）。</summary>
    public static BlurShape? FindBlurShapeAt(AnnotationDocument doc, PointF pt)
    {
        for (int i = doc.Shapes.Count - 1; i >= 0; i--)
        {
            if (doc.Shapes[i] is BlurShape blur && Contains(blur.Rect, pt)
                && !IsErasedAt(doc, blur.Id, pt)) return blur;
        }
        return null;
    }

    /// <summary>聚光灯：矩形体内命中即算，取最上层。</summary>
    public static SpotlightShape? FindSpotlightShapeAt(AnnotationDocument doc, PointF pt)
    {
        for (int i = doc.Shapes.Count - 1; i >= 0; i--)
        {
            if (doc.Shapes[i] is SpotlightShape spotlight && Contains(spotlight.Rect, pt)
                && !IsErasedAt(doc, spotlight.Id, pt)) return spotlight;
        }
        return null;
    }

    /// <summary>画笔：沿折线逐段投影求距离；粗笔按 thickness 加宽；已选中者额外允许点外接框边缘。</summary>
    public static StrokeShape? FindStrokeShapeAt(AnnotationDocument doc, PointF pt, Guid? selectedId, float tol = 1.0f)
    {
        const float BaseHitRadius = 30.0f;
        const float ThickBoostPx = 15.0f;
        const float OutlineTolerance = 8.0f;
        for (int i = doc.Shapes.Count - 1; i >= 0; i--)
        {
            if (doc.Shapes[i] is not StrokeShape stroke) continue;
            if (IsErasedAt(doc, stroke.Id, pt)) continue;
            if (stroke.Samples.Count == 0) continue;

            float hitRadius = Math.Max(BaseHitRadius * tol, stroke.Thickness * 0.5f + ThickBoostPx * tol);
            float hitRadiusSq = hitRadius * hitRadius;

            // 快速 AABB 粗筛
            RectangleF bounds = stroke.Bounds;
            if (pt.X < bounds.Left - hitRadius || pt.X > bounds.Right + hitRadius ||
                pt.Y < bounds.Top - hitRadius || pt.Y > bounds.Bottom + hitRadius)
            {
                continue;
            }

            // 若已选中该画笔，点击在外接框边缘附近也能命中
            if (selectedId == stroke.Id)
            {
                float outlineTolerance = OutlineTolerance * tol;
                if (Math.Abs(pt.X - bounds.Left) <= outlineTolerance ||
                    Math.Abs(pt.X - bounds.Right) <= outlineTolerance ||
                    Math.Abs(pt.Y - bounds.Top) <= outlineTolerance ||
                    Math.Abs(pt.Y - bounds.Bottom) <= outlineTolerance)
                {
                    AppLogger.Info($"FindStrokeShapeAt: 命中已选中画笔外接框 id={stroke.Id} at ({pt.X}, {pt.Y})");
                    return stroke;
                }
            }

            // 单点单击圆斑
            if (stroke.Samples.Count == 1)
            {
                StrokePoint single = stroke.Samples[0];
                float dx = pt.X - single.X;
                float dy = pt.Y - single.Y;
                if (dx * dx + dy * dy <= hitRadiusSq)
                {
                    AppLogger.Info($"FindStrokeShapeAt: 找到单点画笔 id={stroke.Id} at ({pt.X}, {pt.Y})");
                    return stroke;
                }
                continue;
            }

            // 多点线段连续投影距离判定
            for (int j = 0; j < stroke.Samples.Count - 1; j++)
            {
                StrokePoint a = stroke.Samples[j];
                StrokePoint b = stroke.Samples[j + 1];
                float abx = b.X - a.X;
                float aby = b.Y - a.Y;
                float apx = pt.X - a.X;
                float apy = pt.Y - a.Y;
                float abLenSq = abx * abx + aby * aby;
                float distSq;
                if (abLenSq <= 1e-6f)
                {
                    distSq = apx * apx + apy * apy;
                }
                else
                {
                    float t = Math.Clamp((apx * abx + apy * aby) / abLenSq, 0.0f, 1.0f);
                    float qx = a.X + t * abx;
                    float qy = a.Y + t * aby;
                    float dx = pt.X - qx;
                    float dy = pt.Y - qy;
                    distSq = dx * dx + dy * dy;
                }

                if (distSq <= hitRadiusSq)
                {
                    AppLogger.Info($"FindStrokeShapeAt: 找到画笔线段 id={stroke.Id} at ({pt.X}, {pt.Y})");
                    return stroke;
                }
            }
        }
        AppLogger.Info($"FindStrokeShapeAt: 未找到画笔 at ({pt.X}, {pt.Y})");
        return null;
    }

    /// <summary>箭头：按 10 段折线逼近二次贝塞尔求最近距离；已选中者允许点端点/弯曲控制点。</summary>
    public static ArrowShape? FindArrowShapeAt(AnnotationDocument doc, PointF pt, Guid? selectedId, float tol = 1.0f)
    {
        const float HitRadiusPx = 30.0f;
        for (int i = doc.Shapes.Count - 1; i >= 0; i--)
        {
            if (doc.Shapes[i] is not ArrowShape arrow) continue;
            if (IsErasedAt(doc, arrow.Id, pt)) continue;

            // 优先检查是否点中了当前选中箭头的控制点（即使控制点偏离了箭身）
            // 中点判定在端点判定里已被覆盖，截图侧原本就两调用并列，此处照搬不改行为。
            if (selectedId == arrow.Id)
            {
                if (IsArrowHandleHit(arrow, pt, tol) || IsArrowMidpointHit(arrow, pt, tol))
                {
                    return arrow;
                }
            }

            float hitRadius = HitRadiusPx * tol;
            float minDist2 = float.MaxValue;
            PointF p0 = arrow.Start;
            int segments = 10;
            for (int j = 1; j <= segments; j++)
            {
                float t = j / (float)segments;
                float u = 1 - t;
                float w0 = u * u;
                float w1 = 2 * u * t;
                float w2 = t * t;
                PointF p1 = new PointF(
                    w0 * arrow.Start.X + w1 * arrow.ControlPoint.X + w2 * arrow.End.X,
                    w0 * arrow.Start.Y + w1 * arrow.ControlPoint.Y + w2 * arrow.End.Y
                );

                // 计算点到线段 p0-p1 的距离平方
                float dx = p1.X - p0.X;
                float dy = p1.Y - p0.Y;
                float len2 = dx * dx + dy * dy;
                if (len2 < 0.0001f)
                {
                    float ddx = pt.X - p0.X;
                    float ddy = pt.Y - p0.Y;
                    minDist2 = Math.Min(minDist2, ddx * ddx + ddy * ddy);
                }
                else
                {
                    float tProj = Math.Clamp(((pt.X - p0.X) * dx + (pt.Y - p0.Y) * dy) / len2, 0.0f, 1.0f);
                    float projX = p0.X + tProj * dx;
                    float projY = p0.Y + tProj * dy;
                    float ddx = pt.X - projX;
                    float ddy = pt.Y - projY;
                    minDist2 = Math.Min(minDist2, ddx * ddx + ddy * ddy);
                }
                p0 = p1;
            }

            if (minDist2 <= hitRadius * hitRadius)
            {
                AppLogger.Info($"FindArrowShapeAt: 找到箭头 id={arrow.Id} at ({pt.X}, {pt.Y})");
                return arrow;
            }
        }
        AppLogger.Info($"FindArrowShapeAt: 未找到箭头 at ({pt.X}, {pt.Y})");
        return null;
    }

    /// <summary>
    /// 矩形/椭圆：只有<b>边框线条那一圈</b>（含 <c>HitRadiusPx</c> 个屏幕像素的手感容差带）算命中，
    /// 体内空白点不动——不填充时那里根本没有图元的像素，"框内即命中"会让画过的区域再也画不进第二笔。
    /// 唯一例外是<b>已填充</b>（<see cref="RectangleShape.FillOpacity"/> &gt; 0）：整块有色＝整块有内容，
    /// 点内部自然算命中，判据与眼睛看到的一致。
    /// </summary>
    public static RectangleShape? FindRectangleShapeAt(AnnotationDocument doc, PointF pt, float tol = 1.0f)
    {
        const float HitRadiusPx = 25.0f;
        for (int i = doc.Shapes.Count - 1; i >= 0; i--)
        {
            if (doc.Shapes[i] is not RectangleShape rectShape) continue;
            if (IsErasedAt(doc, rectShape.Id, pt)) continue;

            RectangleF r = rectShape.Rect;
            float hitRadius = HitRadiusPx * tol;

            // 边框附近（四条边各自的容差带）
            bool nearLeft = Math.Abs(pt.X - r.Left) <= hitRadius && pt.Y >= r.Top - hitRadius && pt.Y <= r.Bottom + hitRadius;
            bool nearRight = Math.Abs(pt.X - r.Right) <= hitRadius && pt.Y >= r.Top - hitRadius && pt.Y <= r.Bottom + hitRadius;
            bool nearTop = Math.Abs(pt.Y - r.Top) <= hitRadius && pt.X >= r.Left - hitRadius && pt.X <= r.Right + hitRadius;
            bool nearBottom = Math.Abs(pt.Y - r.Bottom) <= hitRadius && pt.X >= r.Left - hitRadius && pt.X <= r.Right + hitRadius;
            bool insideFilled = rectShape.FillOpacity > 0.0f
                && pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;

            if (nearLeft || nearRight || nearTop || nearBottom || insideFilled)
            {
                AppLogger.Info($"FindRectangleShapeAt: 找到矩形 id={rectShape.Id} at ({pt.X}, {pt.Y}), rect={r}");
                return rectShape;
            }
        }
        AppLogger.Info($"FindRectangleShapeAt: 未找到矩形 at ({pt.X}, {pt.Y})");
        return null;
    }

    /// <summary>
    /// 序号：圆内或圆外 15px 环带内命中；带说明文字时文字盒也算本体（整块一起拖是这次的设计口径）；
    /// 带引线时到引线折线的距离在容差内也算——引线只有两三像素宽，不给容差就几乎点不中。
    /// </summary>
    public static CounterShape? FindCounterShapeAt(AnnotationDocument doc, PointF pt, float tol = 1.0f)
    {
        const float TolerancePx = 15.0f;
        const float TailTolerancePx = 8.0f;
        for (int i = doc.Shapes.Count - 1; i >= 0; i--)
        {
            if (doc.Shapes[i] is not CounterShape counter) continue;
            if (IsErasedAt(doc, counter.Id, pt)) continue;

            float dx = pt.X - counter.Center.X;
            float dy = pt.Y - counter.Center.Y;
            float r = counter.Radius + TolerancePx * tol; // 增加容差，宽松点击

            if (dx * dx + dy * dy <= r * r
                || Contains(counter.NoteRect, pt)
                || DistanceToPolyline(AnnotationRenderOptions.GetCounterTailPoints(counter), pt) <= TailTolerancePx * tol)
            {
                AppLogger.Info($"FindCounterShapeAt: 找到序号 id={counter.Id} at ({pt.X}, {pt.Y})");
                return counter;
            }
        }
        AppLogger.Info($"FindCounterShapeAt: 未找到序号 at ({pt.X}, {pt.Y})");
        return null;
    }

    /// <summary>序号引线端点手柄：10 个屏幕像素的手感半径，与箭头端点同一档。
    /// ⚠ 浮条上把引线关掉时这里一并判不中——手柄不画，抓住一个看不见的把手比抓不住更糟。</summary>
    public static bool HitTestCounterTailHandle(CounterShape counter, PointF pt, float tol = 1.0f)
    {
        if (counter.Tail is not PointF tail || !counter.TailShown) return false;
        const float HandleRadiusPx = 10.0f;
        float radius = HandleRadiusPx * tol;
        float dx = pt.X - tail.X;
        float dy = pt.Y - tail.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>序号引线<b>中间弯折点</b>手柄：与端点手柄同一条路——10 个屏幕像素手感半径、
    /// 引线关掉即判不中。位置由 <see cref="AnnotationRenderOptions.GetCounterTailControlPoint"/> 给
    /// （未打弯＝直线中点），故绘制侧与命中侧读的是同一个数，不会画在一处、点中在另一处。</summary>
    public static bool HitTestCounterTailBendHandle(CounterShape counter, PointF pt, float tol = 1.0f)
    {
        if (AnnotationRenderOptions.GetCounterTailControlPoint(counter) is not PointF control) return false;
        const float HandleRadiusPx = 10.0f;
        float radius = HandleRadiusPx * tol;
        float dx = pt.X - control.X;
        float dy = pt.Y - control.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>点到一条折线（&lt;2 点视为不存在）的最短距离。</summary>
    static float DistanceToPolyline(PointF[] points, PointF pt)
    {
        if (points.Length < 2) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 0; i + 1 < points.Length; i++)
        {
            float d = DistanceToSegment(points[i], points[i + 1], pt);
            if (d < best) best = d;
        }
        return best;
    }

    static float DistanceToSegment(PointF a, PointF b, PointF pt)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float len2 = vx * vx + vy * vy;
        if (len2 <= 0.0001f) return MathF.Sqrt((pt.X - a.X) * (pt.X - a.X) + (pt.Y - a.Y) * (pt.Y - a.Y));
        float t = Math.Clamp(((pt.X - a.X) * vx + (pt.Y - a.Y) * vy) / len2, 0.0f, 1.0f);
        float px = a.X + vx * t, py = a.Y + vy * t;
        return MathF.Sqrt((pt.X - px) * (pt.X - px) + (pt.Y - py) * (pt.Y - py));
    }

    /// <summary>
    /// z 序与截图侧同一份：聚光灯 &gt; 模糊 &gt; 序号 &gt; 箭头 &gt; 矩形 &gt; 画笔。
    /// 这里只做"图元本体"的命中；手柄命中是同文件的 <see cref="HitTestBlurHandle"/> /
    /// <see cref="HitTestArrowHandle"/>，两侧共用。
    /// "先问手柄还是先问图元"这段**派发顺序**仍由宿主决定（截图＝选中后免 Ctrl 直接抓手柄，见红线「标注画布鼠标手势优先级」）。
    /// </summary>
    public static AnnotationShape? FindTopmost(AnnotationDocument doc, PointF pt, Guid? selectedId, float tol = 1.0f)
    {
        if (doc == null) return null;

        SpotlightShape? spotlight = FindSpotlightShapeAt(doc, pt);
        if (spotlight != null) return spotlight;

        BlurShape? blur = FindBlurShapeAt(doc, pt);
        if (blur != null) return blur;

        CounterShape? counter = FindCounterShapeAt(doc, pt, tol);
        if (counter != null) return counter;

        ArrowShape? arrow = FindArrowShapeAt(doc, pt, selectedId, tol);
        if (arrow != null) return arrow;

        RectangleShape? rectangle = FindRectangleShapeAt(doc, pt, tol);
        if (rectangle != null) return rectangle;

        return FindStrokeShapeAt(doc, pt, selectedId, tol);
    }

    private static bool IsErasedAt(AnnotationDocument doc, Guid shapeId, PointF point)
    {
        foreach (AnnotationShape shape in doc.Shapes)
        {
            if (shape is not ErasureShape erase || !erase.AffectsShape(shapeId)) continue;
            foreach (ErasureMark mark in erase.Marks)
            {
                RectangleF rect = mark.Bounds;
                if (!rect.Contains(point)) continue;
                if (!mark.IsCircle) return true;
                float dx = (point.X - (rect.Left + rect.Right) / 2f) / Math.Max(1f, rect.Width / 2f);
                float dy = (point.Y - (rect.Top + rect.Bottom) / 2f) / Math.Max(1f, rect.Height / 2f);
                if (dx * dx + dy * dy <= 1f) return true;
            }
        }
        return false;
    }
}
