using System;
using System.Collections.Generic;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>一块可绘制部件的画法：填充闭合多边形、开放折线描边、闭合折线描边。</summary>
public enum ArrowPartKind
{
    Fill,
    Stroke,
    StrokeClosed,
}

/// <summary>
/// 箭头的一个部件（杆或头）。顶点已是<b>目标空间</b>坐标（预览＝DIP，导出＝物理像素），两侧只负责
/// "照点列填/描"，不再各自算几何。
/// <param name="Points">多边形或多段线顶点</param>
/// <param name="Width">描边宽度（<see cref="ArrowPartKind.Fill"/> 时无意义）</param>
/// <param name="RoundCap">描边端帽取圆头还是平头：头部的描边件是圆头（竞品大样张 #5 实测），
/// 杆一律平头——圆头会朝尖端多伸出半笔宽，实心圆点那一档因此会顶出一个小尾巴。</param>
/// </summary>
public readonly record struct ArrowPart(PointF[] Points, float Width, ArrowPartKind Kind, bool RoundCap);

/// <summary>
/// 箭头头型与杆端收口的<b>唯一算式</b>（设计稿《箭头样式全集与绘制方法》§3＋§4＋§8）。
/// <para>为什么收在 Core：预览侧 <c>AnnotationLayer</c> 与导出侧 <c>CaptureSession</c> 曾各抄一份模板映射，
/// 已经漂过一次；<see cref="AnnotationRenderOptions"/> 那套"算式上提、两侧消费"是同一问题的既有解法。</para>
/// <para>输入只有中心线折线＋笔宽＋比例尺，输出只有点列，因此 GDI+ 的 <c>AddPolygon</c> 与 Avalonia 的
/// <c>StreamGeometry</c> 拿到的是同一批顶点。弧边与圆都按多段线近似（12／24 段，半径 6 像素时最大偏差
/// 约 0.05 像素），两侧不可能分叉。</para>
/// <para>单位口径：<paramref name="unit"/>（下方各方法同名参数）＝"1 个单位像素在目标空间里多长"。
/// 预览侧 √scale×dipScale，导出侧 √scale，成品图上的引线是 1。头长下限、端点符号那 1 像素的探入量
/// 都乘它，笔宽则由调用方换算好再传进来。</para>
/// <para><c>doubleHead</c>（2026-09-19）＝"线的两端各挂一个头"。它<b>不是</b>一档新头型，而是同一档头型的
/// 两种挂法，且只对 <see cref="IsDoubleHeadStyle"/> 名单里那几档生效、只由<b>箭头标注工具</b>打开：序号引线的
/// 一头钉在圆点上、无处可挂，那一边一律单头。</para>
/// </summary>
public static class ArrowHeadGeometry
{
    /// <summary>头半宽／头长：定住的是"半角 26.6°"这条实测不变量，而不是头长对笔宽的比例。</summary>
    const float HalfToLength = 0.50f;
    /// <summary>常规头长＝kL·笔宽</summary>
    const float LengthPerWidth = 3.5f;
    /// <summary>燕尾两档（深凹口／弧边）要更长才撑得开凹口</summary>
    const float LengthPerWideHead = 4.9f;
    /// <summary>头长下限（单位像素）：再细的笔也给它一个看得见的头</summary>
    const float MinHeadLength = 11.0f;
    /// <summary>头长上限＝总长的 30%，咬住时正是"头快把杆吃干净"的场合</summary>
    const float HeadLengthCapRatio = 0.30f;
    /// <summary>描边类头型的笔宽＝1.0×杆宽</summary>
    const float StrokeToWidth = 1.0f;
    /// <summary>凹口深度占头长的比例：中档 0.12、深档 0.33（实测三档 0／0.10／0.23→取整）</summary>
    const float NotchMidDepth = 0.12f;
    const float NotchDeepDepth = 0.33f;
    const float DotRadius = 1.4f;
    const float RingRadius = 1.3f;
    /// <summary>菱形是端点符号，实测细长，不套箭头的半角</summary>
    const float DiamondLength = 4.8f;
    const float DiamondHalfWidth = 1.3f;
    /// <summary>端点符号那一档：杆探到尖端前 1 单位，藏在符号里不留缝</summary>
    const float Penetration = 1.0f;
    const int ArcSegments = 12;
    const int CircleSegments = 24;
    /// <summary>弯箭头中心线采样段数（与旧模板路径的 40 同一量级，够平滑）</summary>
    const int BentSamples = 48;
    /// <summary>笔锋杆面的纵向取样数</summary>
    const int TaperSamples = 32;
    /// <summary>杆与箭头底边接缝处那两枚凹角的圆角半径＝半个笔宽（再大就把两翼之间的缺口填平了）。</summary>
    const float FilletToWidth = 0.5f;
    /// <summary>圆角弧的分段数：四分之一圆，8 段在 3 像素半径下的最大偏差不到 0.01 像素。</summary>
    const int FilletSegments = 8;

    /// <summary>
    /// 箭头标注工具的分派判据：<see cref="ArrowAnnotationStyle.Designed"/> 与
    /// <see cref="ArrowAnnotationStyle.Sharp"/> 仍走升级前的 0–100 模板路径（历史存档不变形），其余头型走本类。
    /// 预览与导出必须用同一个判据，故收在这里而不是两侧各写一遍。
    /// <para>⚠ 序号引线**不**走这条分派：引线那条细杆从来没过模板，这两档在引线上也按本类算。</para>
    /// </summary>
    public static bool IsLegacyStyle(ArrowAnnotationStyle style)
        => style is ArrowAnnotationStyle.Designed or ArrowAnnotationStyle.Sharp;

    /// <summary>
    /// 哪些档在<b>箭头标注工具</b>上两端各挂一个头（2026-09-19）：只有 V 形家族那三档——凹口、弧边燕尾、
    /// 开口 V。圆点／菱形／空心三角那几档是"端点符号"，两头各顶一颗会读出两个端点；两档旧模板更不许动。
    /// ⚠ <see cref="ArrowAnnotationStyle.Taper"/>（笔锋）<b>首轮在这份名单里，同日按用户实测退回单头</b>
    /// （渐粗杆面本来就指向尖端，另一头再挂一个会把"笔锋"读成"两头对插"）——别再给它加回来。
    /// 判据收在 Core：预览、导出、参数条小样与下拉小样四处共用，名单只能有一处真相。
    /// </summary>
    public static bool IsDoubleHeadStyle(ArrowAnnotationStyle style)
        => style is ArrowAnnotationStyle.Notch or ArrowAnnotationStyle.Curve
            or ArrowAnnotationStyle.Chevron;

    /// <summary>直线箭头。</summary>
    public static List<ArrowPart> BuildStraight(PointF start, PointF end, float width, float unit,
        ArrowAnnotationStyle style, bool doubleHead = false)
        => Build(new[] { start, end }, width, unit, style, doubleHead);

    /// <summary>弯箭头：中心线取二次贝塞尔 start→control→end 的采样折线，头挂在终点切线上。</summary>
    public static List<ArrowPart> BuildBent(PointF start, PointF control, PointF end, float width, float unit,
        ArrowAnnotationStyle style, bool doubleHead = false)
    {
        var spine = new PointF[BentSamples];
        for (int i = 0; i < BentSamples; i++)
        {
            float t = i / (float)(BentSamples - 1);
            float u = 1f - t;
            spine[i] = new PointF(
                u * u * start.X + 2f * u * t * control.X + t * t * end.X,
                u * u * start.Y + 2f * u * t * control.Y + t * t * end.Y);
        }
        return Build(spine, width, unit, style, doubleHead);
    }

    /// <summary>
    /// 由中心线折线（末点＝尖端）算出该画的全部部件。<paramref name="width"/> 与
    /// <paramref name="unit"/> 已在同一目标空间内（见类型注释）。
    /// <paramref name="doubleHead"/> 见类型注释：为 true 且该档在 <see cref="IsDoubleHeadStyle"/> 名单里时，
    /// 起点那头再挂一个同尺寸的镜像头。
    /// </summary>
    public static List<ArrowPart> Build(PointF[] centerline, float width, float unit,
        ArrowAnnotationStyle style, bool doubleHead = false)
    {
        var parts = new List<ArrowPart>();
        if (centerline.Length < 2 || unit <= 0f) return parts;

        float len = PolylineLength(centerline);
        if (len <= 0.001f) return parts;
        if (!TryTipDirection(centerline, out float ux, out float uy)) return parts;

        PointF tip = centerline[^1];
        // 局部坐标：behind＝离"那一头尖端"的距离（沿 −u），lateral＝侧向偏移（沿 n）。
        // 做成能当参数传的委托而不是局部函数：双头时同一份头型算式还要在起点那头再套一次，
        // 差别只有那个坐标系（尖端位置与朝向都反过来）。
        Func<float, float, PointF> P = Frame(tip, ux, uy);

        float w = Math.Clamp(width, 0.5f, 45f);
        float stroke = Math.Max(0.75f, w * StrokeToWidth);
        bool wideHead = style is ArrowAnnotationStyle.Designed or ArrowAnnotationStyle.Curve;
        float head = Math.Max(MinHeadLength * unit,
            Math.Min((wideHead ? LengthPerWideHead : LengthPerWidth) * w, HeadLengthCapRatio * len));
        float half = HalfToLength * head;
        float notch = style switch
        {
            ArrowAnnotationStyle.Designed or ArrowAnnotationStyle.Curve => NotchDeepDepth * head,
            ArrowAnnotationStyle.Notch => NotchMidDepth * head,
            _ => 0f,
        };
        if (style is ArrowAnnotationStyle.Diamond or ArrowAnnotationStyle.OpenDiamond)
        {
            head = DiamondLength * w;
            half = DiamondHalfWidth * w;
        }
        float dotRadius = DotRadius * w;
        float ringRadius = RingRadius * w;
        float ringCenter = ringRadius + stroke / 2.0f;   // 使环的外沿正齐尖端

        // 杆端收口：跟着头型轮廓走。写错的症状是"杆与两翼之间空一段"或"杆从空心圆中间穿过去"。
        float shaftEnd = style switch
        {
            ArrowAnnotationStyle.Chevron => 0f,
            ArrowAnnotationStyle.Dot => Penetration * unit,
            ArrowAnnotationStyle.Ring => 2f * ringCenter - Penetration * unit,
            _ => head - notch,
        };

        // 双头（2026-09-19）：起点那头挂<b>同一份</b>头型（头长／半宽／凹深全取同一组数，故两头严格对称），
        // 杆则两端一起往里收。起点方向解不出来（整条线重合成一个点）就退回单头，不硬画。
        bool bothEnds = false;
        Func<float, float, PointF>? headAtStart = null;
        if (doubleHead && IsDoubleHeadStyle(style)
            && TryStartDirection(centerline, out float sux, out float suy))
        {
            bothEnds = true;
            headAtStart = Frame(centerline[0], -sux, -suy);
        }

        PointF[] shaft = TrimRange(centerline, bothEnds ? shaftEnd : 0f, len - shaftEnd);
        if (PolylineLength(shaft) > 0.5f * unit)
        {
            if (style == ArrowAnnotationStyle.Taper)
                parts.Add(new ArrowPart(TaperRibbon(shaft, w), 0f, ArrowPartKind.Fill, false));
            else
                parts.Add(new ArrowPart(shaft, w, ArrowPartKind.Stroke, false));
        }

        AddHead(P);
        if (bothEnds) AddHead(headAtStart!);

        return parts;

        // 头型本体：尖端那一头与（双头时）起点那一头共用这一段，差别只是传进来的局部坐标系。
        void AddHead(Func<float, float, PointF> p)
        {
            switch (style)
            {
                case ArrowAnnotationStyle.Chevron:
                    parts.Add(new ArrowPart(new[] { p(head, half), p(0f, 0f), p(head, -half) },
                        stroke, ArrowPartKind.Stroke, true));
                    break;

                case ArrowAnnotationStyle.OpenTriangle:
                    parts.Add(new ArrowPart(new[] { p(0f, 0f), p(head, half), p(head, -half) },
                        stroke, ArrowPartKind.StrokeClosed, true));
                    // 底边是描边，杆顶到的是它的<b>靠杆一侧</b>外沿，故接缝比底边线再让出半个描边宽
                    AddNeckFillets(parts, p, head + stroke / 2.0f, w / 2.0f, half, len);
                    break;

                case ArrowAnnotationStyle.Diamond:
                case ArrowAnnotationStyle.OpenDiamond:
                {
                    PointF[] diamond =
                    {
                        p(0f, 0f), p(head / 2.0f, half), p(head, 0f), p(head / 2.0f, -half),
                    };
                    parts.Add(style == ArrowAnnotationStyle.Diamond
                        ? new ArrowPart(diamond, 0f, ArrowPartKind.Fill, false)
                        : new ArrowPart(diamond, stroke, ArrowPartKind.StrokeClosed, true));
                    break;
                }

                case ArrowAnnotationStyle.Dot:
                    parts.Add(new ArrowPart(Circle(p(dotRadius, 0f), dotRadius), 0f, ArrowPartKind.Fill, false));
                    break;

                case ArrowAnnotationStyle.Ring:
                    parts.Add(new ArrowPart(Circle(p(ringCenter, 0f), ringRadius), stroke,
                        ArrowPartKind.StrokeClosed, true));
                    break;

                case ArrowAnnotationStyle.Curve:
                    parts.Add(new ArrowPart(CurvedHead(p, head, half, notch), 0f, ArrowPartKind.Fill, false));
                    break;

                default:
                    // 实心三角／凹口两档／笔锋的头：凹深为 0 时凹口点与底边中点重合，填充结果就是普通三角
                    parts.Add(new ArrowPart(new[]
                    {
                        p(0f, 0f), p(head, half), p(head - notch, 0f), p(head, -half),
                    }, 0f, ArrowPartKind.Fill, false));
                    // 只有平底（凹深 0：引线上的尖锐箭头、笔锋的头）才在杆两侧留下 90° 台阶；
                    // 带凹口的那几档底边本来就是斜的，接缝处没有直角可圆。
                    if (notch <= 0f) AddNeckFillets(parts, p, head, w / 2.0f, half, len);
                    break;
            }
        }
    }

    /// <summary>某个尖端的局部坐标系：<paramref name="ux"/>/<paramref name="uy"/> 是该头朝外的单位方向。</summary>
    static Func<float, float, PointF> Frame(PointF tip, float ux, float uy)
    {
        float nx = -uy, ny = ux;
        return (behind, lateral) => new PointF(
            tip.X - ux * behind + nx * lateral,
            tip.Y - uy * behind + ny * lateral);
    }

    /// <summary>
    /// 杆与箭头底边接缝处的两枚凹角圆角（2026-09-19 用户「引线/箭头上面如果是直角的话，改成圆角的，
    /// 不要出现那么呆的直角，如果是尖角就不管」）。
    /// <para>为什么是<b>补</b>两片填充而不是去改头的多边形：平底头与杆的并集在
    /// (<paramref name="seam"/>, ±<paramref name="neckHalf"/>) 处各留一个 90° 凹角，圆它要<b>添</b>材料
    /// （把杆的侧边以一段圆弧过渡到箭头的底边），而头本身是独立一件、两翼的尖角必须原样保留，
    /// 改头多边形会把两翼一起拖走。两片各画各的，与既有的杆/头重叠区同色实心填充，画面无接缝。</para>
    /// <para>半径被三处余量夹住：杆的半宽（再大就把杆本身吞了）、翼根到杆侧的横向余量、以及接缝到杆尾的
    /// 纵向余量（短粗箭头那头会被头长上限 30% 挤扁）。夹完不足 0.5 像素就不画——圆角比像素还小不如不圆。</para>
    /// </summary>
    static void AddNeckFillets(List<ArrowPart> parts, Func<float, float, PointF> p,
        float seam, float neckHalf, float wingHalf, float shaftLen)
    {
        float r = Math.Min(FilletToWidth * 2f * neckHalf, Math.Min(wingHalf - neckHalf, shaftLen - seam));
        if (r < 0.5f) return;
        AddFilletPart(parts, p, seam, neckHalf, r, 1f);
        AddFilletPart(parts, p, seam, neckHalf, r, -1f);
    }

    /// <summary>单侧那一片：<paramref name="sign"/> 定横侧，点列＝凹角→沿底边让出 r→四分之一圆弧→沿杆侧回到凹角。</summary>
    static void AddFilletPart(List<ArrowPart> parts, Func<float, float, PointF> p,
        float seam, float neckHalf, float r, float sign)
    {
        float lateral = sign * neckHalf;
        var pts = new List<PointF>(FilletSegments + 2)
        {
            p(seam, lateral),
            p(seam, lateral + sign * r),
        };
        // 圆心在凹角朝杆内一侧偏移 r 处，弧从该圆心正左方扫到正上／正下方（四分之一圆）
        float centerBehind = seam + r, centerLateral = lateral + sign * r;
        for (int i = 1; i <= FilletSegments; i++)
        {
            double a = Math.PI + sign * Math.PI / 2.0 * i / FilletSegments;
            pts.Add(p(centerBehind + r * (float)Math.Cos(a), centerLateral + r * (float)Math.Sin(a)));
        }
        parts.Add(new ArrowPart(pts.ToArray(), 0f, ArrowPartKind.Fill, false));
    }

    /// <summary>
    /// 弧边燕尾：翼尖→凹口那两条回边改二次弧，控制点取 (0.62 头长, ±0.60 半宽)——比凹口更朝尖端
    /// 探出一点，凹口因此是"外凸的勺形"而不是两条直线拼出的尖角。
    /// </summary>
    static PointF[] CurvedHead(Func<float, float, PointF> p, float head, float half, float notch)
    {
        float root = head - notch;
        var pts = new List<PointF> { p(0f, 0f), p(head, half) };
        AddQuadraticArc(pts, p, head, half, head * 0.62f, half * 0.60f, root, 0f);
        AddQuadraticArc(pts, p, root, 0f, head * 0.62f, -half * 0.60f, head, -half);
        return pts.ToArray();
    }

    /// <summary>把一段二次贝塞尔采样进点列（起点已在列里，故从第 1 个内分点开始追加）。</summary>
    static void AddQuadraticArc(List<PointF> pts, Func<float, float, PointF> p,
        float x0, float y0, float xc, float yc, float x1, float y1)
    {
        for (int i = 1; i <= ArcSegments; i++)
        {
            float t = i / (float)ArcSegments;
            float u = 1f - t;
            pts.Add(p(
                u * u * x0 + 2f * u * t * xc + t * t * x1,
                u * u * y0 + 2f * u * t * yc + t * t * y1));
        }
    }

    static PointF[] Circle(PointF center, float radius)
    {
        var pts = new PointF[CircleSegments];
        for (int i = 0; i < CircleSegments; i++)
        {
            double a = i / (double)CircleSegments * Math.PI * 2.0;
            pts[i] = new PointF(center.X + radius * (float)Math.Cos(a), center.Y + radius * (float)Math.Sin(a));
        }
        return pts;
    }

    /// <summary>笔锋杆面：尾端宽 0 → 头根宽 w 沿弧长线性张开（大样张第 3 条实测方向），头根不留台阶。</summary>
    static PointF[] TaperRibbon(PointF[] spine, float width)
    {
        float total = PolylineLength(spine);
        var ribbon = new PointF[TaperSamples * 2];
        for (int i = 0; i < TaperSamples; i++)
        {
            float t = i / (float)(TaperSamples - 1);
            (PointF pos, float dx, float dy) = SampleWithDirection(spine, total, t);
            float l = MathF.Sqrt(dx * dx + dy * dy);
            if (l <= 0.001f) { ribbon[i] = pos; ribbon[ribbon.Length - 1 - i] = pos; continue; }
            float px = -dy / l, py = dx / l;
            float half = width * t / 2.0f;
            ribbon[i] = new PointF(pos.X + px * half, pos.Y + py * half);
            ribbon[ribbon.Length - 1 - i] = new PointF(pos.X - px * half, pos.Y - py * half);
        }
        return ribbon;
    }

    /// <summary>按归一化弧长取点，同时给出该点所在采样段的方向。</summary>
    static (PointF Pos, float Dx, float Dy) SampleWithDirection(PointF[] spine, float total, float t)
    {
        float target = total * t;
        float walked = 0f;
        for (int i = 1; i < spine.Length; i++)
        {
            float seg = Dist(spine[i - 1], spine[i]);
            if (walked + seg >= target)
            {
                float k = seg <= 0.001f ? 1f : (target - walked) / seg;
                return (new PointF(spine[i - 1].X + (spine[i].X - spine[i - 1].X) * k,
                    spine[i - 1].Y + (spine[i].Y - spine[i - 1].Y) * k),
                    spine[i].X - spine[i - 1].X, spine[i].Y - spine[i - 1].Y);
            }
            walked += seg;
        }
        return (spine[^1], 0f, 0f);
    }

    /// <summary>尖端处的前进方向：从末点往前找到第一段不重合的采样段。</summary>
    static bool TryTipDirection(PointF[] line, out float ux, out float uy)
    {
        ux = uy = 0f;
        for (int i = line.Length - 1; i > 0; i--)
        {
            float dx = line[i].X - line[i - 1].X;
            float dy = line[i].Y - line[i - 1].Y;
            float l = MathF.Sqrt(dx * dx + dy * dy);
            if (l <= 0.001f) continue;
            ux = dx / l;
            uy = dy / l;
            return true;
        }
        return false;
    }

    /// <summary>起点处的前进方向：从首点往后找到第一段不重合的采样段（双头时那头朝外＝取反）。</summary>
    static bool TryStartDirection(PointF[] line, out float ux, out float uy)
    {
        ux = uy = 0f;
        for (int i = 1; i < line.Length; i++)
        {
            float dx = line[i].X - line[i - 1].X;
            float dy = line[i].Y - line[i - 1].Y;
            float l = MathF.Sqrt(dx * dx + dy * dy);
            if (l <= 0.001f) continue;
            ux = dx / l;
            uy = dy / l;
            return true;
        }
        return false;
    }

    static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    static float PolylineLength(PointF[] line)
    {
        float sum = 0f;
        for (int i = 1; i < line.Length; i++) sum += Dist(line[i - 1], line[i]);
        return sum;
    }

    static PointF PointOn(PointF a, PointF b, float t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>
    /// 只保留弧长 <paramref name="from"/> 到 <paramref name="to"/> 之间那一段（剪口落在采样段上就内插，
    /// 不会把最后一段丢掉）。单头＝from 传 0；双头＝两端各让出一个头长，症状写在调用处注释里。
    /// </summary>
    static PointF[] TrimRange(PointF[] line, float from, float to)
    {
        var kept = new List<PointF>(line.Length + 2);
        if (from >= to) return Array.Empty<PointF>();

        float walked = 0f;
        for (int i = 1; i < line.Length; i++)
        {
            float seg = Dist(line[i - 1], line[i]);
            float end = walked + seg;
            float k(float target) => seg <= 0.001f ? 1f : (target - walked) / seg;

            if (kept.Count == 0)
            {
                if (end < from) { walked = end; continue; }
                kept.Add(from <= walked ? line[i - 1] : PointOn(line[i - 1], line[i], k(from)));
            }
            if (end <= to)
            {
                kept.Add(line[i]);
                walked = end;
                continue;
            }
            if (to > walked) kept.Add(PointOn(line[i - 1], line[i], k(to)));
            return kept.ToArray();
        }
        return kept.Count > 0 ? kept.ToArray() : Array.Empty<PointF>();
    }
}
