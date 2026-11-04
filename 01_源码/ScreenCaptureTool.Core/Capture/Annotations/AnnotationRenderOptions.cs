using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 标注渲染共用取值。截图内实时预览（Avalonia）与成品图导出（GDI+）两套渲染器必须引用同一份常量与映射，
/// 任何一侧单独硬编码都会造成"截图时看到的"与"截图后的"效果不一致。
/// </summary>
public static class AnnotationRenderOptions
{
    /// <summary>大虚线线型：单个划段的长度（相对笔宽的倍数）。</summary>
    public const double DashLargeOn = 4.0;

    /// <summary>大虚线线型：间隔的长度（相对笔宽的倍数）。</summary>
    public const double DashLargeOff = 3.0;

    /// <summary>大虚线线型的路径数组，供 Avalonia DashStyle 使用。</summary>
    public static readonly double[] DashLargePattern = { DashLargeOn, DashLargeOff };

    /// <summary>
    /// 大虚线划段长度的下限（屏幕物理像素）。笔宽很小时按笔宽倍数算出的虚线会短到看不见，
    /// 两侧都必须用同一个下限兜底，否则"截图时的虚线密度"与"成品图的虚线密度"不一致。
    /// </summary>
    public const double DashLargeMinOnPx = 10.0;

    /// <summary>大虚线间隔长度的下限（屏幕物理像素），口径同 <see cref="DashLargeMinOnPx"/>。</summary>
    public const double DashLargeMinOffPx = 7.0;

    /// <summary>序号数字默认加粗（预览与导出一致）。</summary>
    public const bool CounterFontIsBold = true;

    /// <summary>序号外圈描边的默认宽度（物理像素）。</summary>
    public const float CounterOutlineThickness = 2.0f;

    /// <summary>引线粗细的基准值（物理像素）与它对应的基准半径＝「大小」的默认档，两者一配对，
    /// 换算见 <see cref="GetCounterTailWidth"/>。基准取 <b>5.0</b> 而非钉死时代的 2.5：用户实测"再粗一倍
    /// 就没问题"（2026-09-19），故默认档整条引线（含由笔宽推出的箭头）比引线粗细能力出现之前粗一倍，
    /// ⚠ 历史贴图存档里的序号也会被重画成这个粗细——粗细是派生量、不落盘，没有"老档不变形"可言。</summary>
    public const float CounterTailWidthBase = 5.0f;
    public const float CounterTailWidthBaseRadius = 14.0f;

    /// <summary>引线粗细随半径的响应指数（0.75）。为什么不是开方：见 <see cref="GetCounterTailWidth"/> 注释。</summary>
    public const float CounterTailWidthExponent = 0.75f;

    /// <summary>引线粗细上下限（物理像素）：小圆点不至于看不见、大圆点不至于成粗绳。
    /// 上限取 18＝半径推到滑杆顶（80）时正好饱和；下限 3.2 略低于可达最小值（半径 8 → 3.29），
    /// 只作兜底、日常不咬合。</summary>
    public const float CounterTailWidthMin = 3.2f;
    public const float CounterTailWidthMax = 18.0f;

    /// <summary>
    /// 引线被打弯后，二次贝塞尔沿弧采样成折线的段数。三处消费方（预览/导出/命中）都是按折线画、
    /// 按折线判距离的，故"曲线"这件事只在这里出现一次。取 48＝与 <c>ArrowHeadGeometry</c> 里
    /// 弯箭头中心线（<c>BentSamples</c>）同一档，两处别各自调数，否则同样的弧度一边平滑一边有折角。
    /// </summary>
    public const int CounterTailCurveSegments = 48;

    /// <summary>
    /// 拉丁字符的每字符宽度 / 字号。Core 里没有字体度量，胶囊撑多长、多位数要不要降字号全靠这个估算，
    /// 所以预览侧与导出侧**必须**都用它来排版（各自量真实宽会让两侧对不上）。
    /// 取值比 Segoe UI 粗体的真实前进宽（约 0.58）略宽，留出的余量正好吸收字体差异。
    /// </summary>
    public const float CounterLatinAdvanceRatio = 0.68f;

    /// <summary>圆/方外形内，编号文字与边缘之间的内边距（相对半径）。</summary>
    public const float CounterGlyphPadRatio = 0.26f;

    /// <summary>圆角方外形档的圆角半径（相对边长）。</summary>
    public const float CounterRoundedSquareCornerRatio = 0.30f;

    /// <summary>
    /// 胶囊外形的圆角默认值，单位是<b>百分比</b>（0＝直角方头，100＝两端全圆）。
    /// <para>为什么按百分比而不是像矩形/聚光灯那样按像素存：胶囊的高度恒等于「大小」那一档的 2 倍，
    /// 存绝对像素会让"先把胶囊调圆、再把大小从 14 拉到 40"变成"角又方回去了"。与圆角方那一档同一口径
    /// （<see cref="CounterRoundedSquareCornerRatio"/> 也是相对边长的比例）。</para>
    /// </summary>
    public const float CounterCapsuleCornerDefault = 100.0f;

    /// <summary>说明文字字号相对半径的默认倍数（「大小」为 0 时按此自动）。</summary>
    public const float CounterNoteFontSizeRatio = 0.9f;

    /// <summary>说明文字的最小字号，避免小圆点配一行看不清的字。</summary>
    public const float CounterNoteMinFontSize = 12.0f;

    /// <summary>序号字体档 → 字体族名。取值必须在 GDI+ 与 Avalonia 两侧都能按同名解析。</summary>
    public static string GetCounterFontFamilyName(CounterFontFamily family) => family switch
    {
        CounterFontFamily.Yahei => "Microsoft YaHei",
        _ => "Segoe UI",
    };

    /// <summary>按 <see cref="CounterLatinAdvanceRatio"/> 估算一段文字的宽度（CJK 记全宽）。</summary>
    public static float GetCounterTextWidth(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0.0f;
        float units = 0.0f;
        foreach (char ch in text) units += ch > 0x2E7F ? 1.0f : CounterLatinAdvanceRatio;
        return Math.Max(0.0f, units * fontSize);
    }

    /// <summary>
    /// 编号文字字号：圆与圆角方以半径为准，但多位数（含罗马 XIII 这类长内容）自动降号，
    /// 把「撑大圆」这个副作用挡掉——半径是用户用滚轮定的。胶囊档反过来：字号不动，盒子撑长。
    /// </summary>
    public static float GetCounterNumberFontSize(CounterShape shape)
    {
        if (shape.Glyph == CounterGlyphShape.Capsule) return shape.Radius;
        float usable = shape.Radius * 2.0f - shape.Radius * CounterGlyphPadRatio * 2.0f;
        int chars = Math.Max(1, shape.Label.Length);
        return Math.Min(shape.Radius, usable / (chars * CounterLatinAdvanceRatio));
    }

    /// <summary>说明文字的实际字号：显式给值优先，否则随半径自动。</summary>
    public static float GetCounterNoteFontSize(CounterShape shape) => shape.NoteFontSize > 0.0f
        ? shape.NoteFontSize
        : Math.Max(CounterNoteMinFontSize, shape.Radius * CounterNoteFontSizeRatio);

    /// <summary>
    /// 引线粗细（物理像素）：随「大小」按 <paramref name="radius"/> 的 <see cref="CounterTailWidthExponent"/>
    /// 次幂联动（2026-09-19，此前恒为 2.5）。
    /// <para>为什么不用正比：正比下 80 半径那档会粗成缆绳、8 半径那档又细到几乎没有。</para>
    /// <para>为什么指数取 0.75 而不是开方：第一版按开方落地后用户回话"多大尺寸的序号都是同一个引线粗细"。
    /// 日志里他比的两档是半径 44 与 24，开方给出 4.43 与 3.28——差 1.15 像素，在带箭头的细杆上根本读不出来，
    /// 于是"联动"在他眼里等于没有。0.75 次幂让这两档拉开到 57%，滚轮每走一档都能看出粗细在变。</para>
    /// <para>基准随后又整体乘二（2.5 → 5.0，上下限同倍）：同一轮实测结论"线太细了，再粗一倍就没问题"。
    /// 现在同两档是 11.80 与 7.50。⚠ 粗细是<b>派生量</b>、不落盘，故默认档比钉死时代粗一倍＝历史序号贴图
    /// 重画时也会跟着变粗，这是他要的效果，不是漏迁移。</para>
    /// <para>头长不用另算：<see cref="ArrowHeadGeometry"/> 的头长本就由笔宽推出来（3.5×笔宽，另有下限），
    /// 故这里给对粗细，箭头便自动跟着大小一起长。</para>
    /// </summary>
    public static float GetCounterTailWidth(float radius)
        => Math.Clamp(CounterTailWidthBase
            * MathF.Pow(Math.Max(1.0f, radius) / CounterTailWidthBaseRadius, CounterTailWidthExponent),
            CounterTailWidthMin, CounterTailWidthMax);

    /// <summary>外形盒（物理像素内容坐标系）。</summary>
    public static RectangleF GetCounterGlyphRect(CounterShape shape)
    {
        float r = shape.Radius;
        if (shape.Glyph != CounterGlyphShape.Capsule)
            return new RectangleF(shape.Center.X - r, shape.Center.Y - r, r * 2.0f, r * 2.0f);

        float textWidth = GetCounterTextWidth(shape.Label, r);
        float width = Math.Max(r * 2.0f, textWidth + r * CounterGlyphPadRatio * 2.0f);
        return new RectangleF(shape.Center.X - width / 2.0f, shape.Center.Y - r, width, r * 2.0f);
    }

    /// <summary>外形圆角半径：圆＝半高（画成正圆），圆角方＝边长的固定比例，胶囊＝半高 × 圆角档（100 即两端全圆）。</summary>
    public static float GetCounterGlyphCornerRadius(CounterShape shape) => shape.Glyph switch
    {
        CounterGlyphShape.RoundedSquare => GetCounterGlyphRect(shape).Width * CounterRoundedSquareCornerRatio,
        CounterGlyphShape.Capsule => shape.Radius * Math.Clamp(shape.CapsuleCorner, 0.0f, 100.0f) / 100.0f,
        _ => shape.Radius,
    };

    /// <summary>
    /// 引线的路径取点（内容坐标系）：无引线返回空数组，未打弯返回 2 点（直线），
    /// 打弯后返回 <see cref="CounterTailCurveSegments"/> + 1 个二次贝塞尔采样点。
    /// 起点不取圆心而取"圆边缘朝引线方向的那个点"，否则线会从色底下面钻出来一截。
    /// 箭头头型不在这里：引线自 2026-09-19 起与箭头标注工具共用 <see cref="ArrowHeadGeometry"/> 那一份算式。
    /// <para>⚠ 浮条上把引线关掉（<see cref="CounterShape.TailShown"/> 为 false、但引线点仍留着）这里也返回
    /// 空数组：预览、导出、命中测试三处都只认这一个出口，故一处判定就三处一起没引线，不必各写一遍。</para>
    /// <para>⚠ 引线会弯（<see cref="CounterShape.TailControl"/>）也只改这一个出口：三处消费方都是按折线画的，
    /// 于是采样点一多，预览与成品当场同步变弯，不需要任何一处单独支持曲线。</para>
    /// </summary>
    public static PointF[] GetCounterTailPoints(CounterShape shape)
    {
        if (shape.Tail is not PointF tail || !shape.TailShown) return Array.Empty<PointF>();

        PointF? control = shape.TailControl;
        // 有弯时朝控制点出射：贝塞尔在起点的切线方向就是"起点→控制点"，朝终点出射会让线在出口处拐一下。
        PointF toward = control ?? tail;
        float dx = toward.X - shape.Center.X;
        float dy = toward.Y - shape.Center.Y;
        // 胶囊朝长轴方向要按半宽出射，按圆半径出射会让起点落在盒子内部
        RectangleF glyph = GetCounterGlyphRect(shape);
        float ex = Math.Max(glyph.Width, glyph.Height) / 2.0f;
        float ey = glyph.Height / 2.0f;

        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0.001f) return Array.Empty<PointF>();
        float ux = dx / len, uy = dy / len;
        PointF start = new(shape.Center.X + ux * ex, shape.Center.Y + uy * ey);
        if (control is not PointF c) return new[] { start, tail };

        var pts = new PointF[CounterTailCurveSegments + 1];
        for (int i = 0; i <= CounterTailCurveSegments; i++)
        {
            float t = i / (float)CounterTailCurveSegments;
            float u = 1 - t;
            pts[i] = new PointF(
                u * u * start.X + 2 * u * t * c.X + t * t * tail.X,
                u * u * start.Y + 2 * u * t * c.Y + t * t * tail.Y);
        }
        return pts;
    }

    /// <summary>
    /// 引线"中间那枚手柄"的位置（内容坐标系）：已打弯就是存下的控制点，没打弯取直线段中点——
    /// 手柄得先看得见，才谈得上拖着它把线拉弯。<br/>
    /// 注意它是<b>控制点</b>不是<b>曲线上的点</b>：二次贝塞尔只被它"带弯"、并不经过它，
    /// 故手柄画在弧线外侧是正确现象，与箭头标注工具的弯折点同一套。
    /// 无引线或引线被关掉时返回 null。
    /// </summary>
    public static PointF? GetCounterTailControlPoint(CounterShape shape)
    {
        // 先过那个唯一出口：没有引线（或已被浮条藏掉）就没有"这条线"可言，
        // 不能让存过的控制点绕过判定——那是"图上看不见却点得中"的起手式。
        PointF[] path = GetCounterTailPoints(shape);
        if (path.Length < 2) return null;
        if (shape.TailControl is PointF stored) return stored;
        return new PointF(
            (path[0].X + path[1].X) / 2.0f,
            (path[0].Y + path[1].Y) / 2.0f);
    }

    /// <summary>
    /// 无底/描边档与说明文字那圈白色 halo 的粗细。Core 里没有字体度量，故按字号定比例，
    /// 两侧同一算式，否则预览有描边、成品没有。
    /// </summary>
    public static double GetCounterHaloThickness(double fontSize) => Math.Max(1.0, fontSize * 0.16);

    /// <summary>
    /// 文本描边的实际渲染粗细：小值适度放大、并按字号封顶，预览与导出共用同一映射。
    /// </summary>
    public static double GetTextStrokeRenderThickness(double strokeThickness, double fontSize)
    {
        if (strokeThickness <= 0.0) return 0.0;

        double maxByFont = Math.Max(1.0, fontSize * 0.22);
        double softened = strokeThickness <= 1.0
            ? strokeThickness * 1.45
            : 1.45 + (strokeThickness - 1.0) * 1.15;
        return Math.Clamp(softened, 0.75, maxByFont);
    }
}
