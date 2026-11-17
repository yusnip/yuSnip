using System;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 编号标注。Center 使用截图/屏幕物理像素坐标。
/// <para>序号的显示文本不落字段，一律由 <see cref="Number"/> + <see cref="Serial"/> 现算
/// （<see cref="CounterLabel.Format"/>），故删除后的重编号只需改 <see cref="Number"/>，
/// 不会出现"数字与字母混排"的两份真相。</para>
/// <para>说明文字与引线都是本图元的一部分（不是独立图元），所以拖动圆点＝整块一起走，
/// 撤销一个序号＝编号、文字、引线一起回去。</para>
/// </summary>
public sealed class CounterShape : AnnotationShape
{
    /// <summary>说明文字与圆点之间的固定间距（物理像素），预览与导出共用。</summary>
    public const float NoteGap = 9.0f;

    public CounterShape(PointF center, int number, float radius, Color fillColor, Color textColor,
        Guid id = default,
        CounterSerialKind serial = CounterSerialKind.Number,
        CounterGlyphShape glyph = CounterGlyphShape.Circle,
        CounterFillMode fill = CounterFillMode.Solid,
        float outlineWidth = AnnotationRenderOptions.CounterOutlineThickness,
        bool fontBold = AnnotationRenderOptions.CounterFontIsBold,
        CounterFontFamily fontFamily = CounterFontFamily.Segoe,
        string note = "",
        float noteFontSize = 0.0f,
        PointF? tail = null,
        ArrowAnnotationStyle tailHead = ArrowAnnotationStyle.Sharp,
        bool tailShown = true,
        float capsuleCorner = AnnotationRenderOptions.CounterCapsuleCornerDefault,
        PointF? tailControl = null)
        : base(id)
    {
        Center = center;
        Number = Math.Max(1, number);
        Radius = Math.Max(8.0f, radius);
        FillColor = fillColor;
        TextColor = textColor;
        Serial = serial;
        Glyph = glyph;
        Fill = fill;
        OutlineWidth = Math.Clamp(outlineWidth, 0.0f, 12.0f);
        FontBold = fontBold;
        FontFamily = fontFamily;
        Note = note ?? string.Empty;
        // 0＝随半径自动（渲染侧按 CounterNoteFontSize 换算），故不设下限
        NoteFontSize = Math.Max(0.0f, noteFontSize);
        Tail = tail;
        // 没有引线终点就没有"这条线"可言，控制点单独留着是一份读不出来的残留状态
        TailControl = tail.HasValue ? tailControl : null;
        TailHead = tailHead;
        TailShown = tailShown;
        CapsuleCorner = Math.Clamp(capsuleCorner, 0.0f, 100.0f);
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Counter;

    public PointF Center { get; }

    public int Number { get; }

    /// <summary>序号类型（数字 / 字母 / 罗马数字），决定 <see cref="Number"/> 怎么显示。</summary>
    public CounterSerialKind Serial { get; }

    /// <summary>外形档（圆 / 圆角方 / 胶囊）。</summary>
    public CounterGlyphShape Glyph { get; }

    /// <summary>底色档（实心 / 描边 / 无底）。</summary>
    public CounterFillMode Fill { get; }

    public float Radius { get; }

    /// <summary>实心档的底色 / 描边档的边框色（取自全局标注色）。</summary>
    public Color FillColor { get; }

    /// <summary>编号文字色：实心档为白，描边与无底档跟随标注色。</summary>
    public Color TextColor { get; }

    /// <summary>描边宽度：实心档＝外圈白边，描边档＝同色边框，无底档不用。</summary>
    public float OutlineWidth { get; }

    public bool FontBold { get; }

    public CounterFontFamily FontFamily { get; }

    /// <summary>随圆点的那句说明；空串＝没有说明。入口是选中后浮条中间那一格。</summary>
    public string Note { get; }

    /// <summary>说明文字字号；0＝随半径自动。</summary>
    public float NoteFontSize { get; }

    /// <summary>引线终点（按住拖动拖出来的那头）；null＝没有引线。</summary>
    public PointF? Tail { get; }

    /// <summary>
    /// 引线的中间控制点（选中后拖中间那枚手柄得来），语义与箭头标注工具的弯折点同一条：
    /// 它是二次贝塞尔的控制点，<b>曲线不经过它</b>，它只把线往那个方向带弯。
    /// null＝引线是两点直线，与"从没拖过弯"的历史图元完全同形。
    /// </summary>
    public PointF? TailControl { get; }

    /// <summary>引线箭头头型：与箭头标注工具共用同一份 <see cref="ArrowAnnotationStyle"/> 列表。
    /// ⚠ 引线恒<b>单头</b>——双头只服务箭头标注工具（那一头钉在圆点上，没地方挂第二个头）。</summary>
    public ArrowAnnotationStyle TailHead { get; }

    /// <summary>
    /// 引线是否画出来。false＝选中后浮条上那枚「箭头」按钮把它关掉了：引线点仍留在 <see cref="Tail"/> 里，
    /// 只是不画、也点不中（渲染与命中共用 <see cref="AnnotationRenderOptions.GetCounterTailPoints"/>
    /// 那一个出口），再点一次按钮原样回来。
    /// </summary>
    public bool TailShown { get; }

    /// <summary>
    /// 引线粗细（物理像素）：<b>不是字段</b>，恒由「大小」现算（见
    /// <see cref="AnnotationRenderOptions.GetCounterTailWidth"/>），故拖动「大小」滑杆时引线当场跟着变，
    /// 不需要任何回写。
    /// </summary>
    public float TailWidth => AnnotationRenderOptions.GetCounterTailWidth(Radius);

    /// <summary>
    /// 胶囊档的圆角，单位<b>百分比</b>：0＝方头，100＝两端全圆（默认）。
    /// 只服务胶囊——圆档恒正圆，圆角方档另有 <see cref="AnnotationRenderOptions.CounterRoundedSquareCornerRatio"/>。
    /// </summary>
    public float CapsuleCorner { get; }

    public float TailLength => Tail is PointF t
        ? MathF.Sqrt((t.X - Center.X) * (t.X - Center.X) + (t.Y - Center.Y) * (t.Y - Center.Y))
        : 0.0f;

    /// <summary>显示用的编号文本（数字/字母/罗马由类型档现算）。</summary>
    public string Label => CounterLabel.Format(Number, Serial);

    /// <summary>外形盒：圆与圆角方是 2r 正方，胶囊按编号文字宽度撑长（算式两侧渲染共用）。</summary>
    public RectangleF GlyphRect => AnnotationRenderOptions.GetCounterGlyphRect(this);

    /// <summary>编号文字的字号（多位数会自动降号，不撑大圆）。</summary>
    public float NumberFontSize => AnnotationRenderOptions.GetCounterNumberFontSize(this);

    /// <summary>说明文字的实际字号（0 表示随半径自动）。</summary>
    public float NoteFontSizeEffective => AnnotationRenderOptions.GetCounterNoteFontSize(this);

    /// <summary>说明文字的排版盒（无说明时为空盒，不参与包围盒并集）。</summary>
    public RectangleF NoteRect => Note.Length == 0
        ? RectangleF.Empty
        : new RectangleF(
            GlyphRect.Right + NoteGap,
            Center.Y - NoteFontSizeEffective * 0.75f,
            AnnotationRenderOptions.GetCounterTextWidth(Note, NoteFontSizeEffective),
            NoteFontSizeEffective * 1.5f);

    public override RectangleF Bounds
    {
        get
        {
            RectangleF b = GlyphRect;
            if (Note.Length > 0) b = RectangleF.Union(b, NoteRect);
            // 关掉的引线不算进包围盒：图上没有的东西不该占位（导出加边距、整块拖动都读这里）
            if (Tail is PointF t && TailShown)
            {
                float pad = Radius + TailWidth * 3.0f;
                b = RectangleF.Union(b, RectangleF.FromLTRB(
                    Math.Min(b.Left, t.X - pad),
                    Math.Min(b.Top, t.Y - pad),
                    Math.Max(b.Right, t.X + pad),
                    Math.Max(b.Bottom, t.Y + pad)));
                // 打弯后的曲线整段落在「圆点—控制点—终点」的凸包里，故只要再把控制点框进来就够了，
                // 不必去逐点采样求包壳（漏掉这一项，弯出终点头外侧的那截会在导出加边距时被裁掉）。
                if (TailControl is PointF c)
                    b = RectangleF.Union(b, RectangleF.FromLTRB(
                        Math.Min(b.Left, c.X - pad),
                        Math.Min(b.Top, c.Y - pad),
                        Math.Max(b.Right, c.X + pad),
                        Math.Max(b.Bottom, c.Y + pad)));
            }
            return b;
        }
    }

    public override AnnotationShape Clone() => Copy();

    /// <summary>原样克隆（改字段用下面的 With* 系列，避免十几个参数抄一遍抄错）。</summary>
    public CounterShape Copy() => Build(Center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    public CounterShape WithNumber(int number) => Build(Center, number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>换序号类型档（数字/字母/罗马/汉字）；编号本身不变，只是换一种写法显示。</summary>
    public CounterShape WithSerial(CounterSerialKind serial) => Build(Center, Number, Radius, FillColor, TextColor, Id,
        serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    public CounterShape WithCenter(PointF center) => Build(center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>整体平移：圆点、引线终点与弯折点一起走，引线不会在拖动中被拉断或改了弧度。</summary>
    public CounterShape Translated(float dx, float dy) => Build(
        new PointF(Center.X + dx, Center.Y + dy), Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize,
        Tail is PointF t ? new PointF(t.X + dx, t.Y + dy) : null,
        TailHead, TailShown, CapsuleCorner,
        TailControl is PointF c ? new PointF(c.X + dx, c.Y + dy) : null);

    /// <summary>只挪引线终点，<b>保留</b>已有的弯（拖端点不该把线拉直）。</summary>
    public CounterShape WithTail(PointF? tail) => Build(Center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>同时改引线终点与弯折点（拖端点时弯折点要跟着走一半，见 <c>AnnotationGeometry.EditShape</c>）。</summary>
    public CounterShape WithTail(PointF? tail, PointF? tailControl) => Build(Center, Number, Radius,
        FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, tail, TailHead, TailShown,
        CapsuleCorner, tailControl);

    /// <summary>拖中间那枚手柄改引线的弧度——箭头标注工具弯折点的同一件事。</summary>
    public CounterShape WithTailControl(PointF? tailControl) => Build(Center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, tailControl);

    /// <summary>显示／藏掉整条引线——选中后浮条上那枚「箭头」按钮的落地入口。引线点不动，只改画不画。</summary>
    public CounterShape WithTailShown(bool shown) => Build(Center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, shown,
        CapsuleCorner, TailControl);

    public CounterShape WithRadius(float radius) => Build(Center, Number, radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    public CounterShape WithNote(string note) => Build(Center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>改胶囊圆角（百分比）——选中后浮条「圆角」那一格的落地入口。</summary>
    public CounterShape WithCapsuleCorner(float corner) => Build(Center, Number, Radius, FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        corner, TailControl);

    /// <summary>换外形／底色档（参数条「样式」小样的去处）。⚠ 文字色必须按<b>新的</b>底色档重算：
    /// 从实心改成描边若不同时把白字换回标注色，那颗号在浅底上就成了白字白底。</summary>
    public CounterShape WithStyle(CounterGlyphShape glyph, CounterFillMode fill) => Build(Center, Number, Radius,
        FillColor, TextColorFor(fill, FillColor), Id,
        Serial, glyph, fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>换引线箭头头型（参数条末段那格下拉的去处）。</summary>
    public CounterShape WithTailHead(ArrowAnnotationStyle tailHead) => Build(Center, Number, Radius,
        FillColor, TextColor, Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, tailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>换标注色：底色档为实心时它是色底，否则它是字色；文字色随之按当前底色档派生。</summary>
    public CounterShape WithAnnotationColor(Color annotationColor) => Build(Center, Number, Radius,
        annotationColor, TextColorFor(Fill, annotationColor), Id,
        Serial, Glyph, Fill, OutlineWidth, FontBold, FontFamily, Note, NoteFontSize, Tail, TailHead, TailShown,
        CapsuleCorner, TailControl);

    /// <summary>
    /// 编号文字色的<b>全仓唯一</b>派生：实心档压在实底色上只能是白，描边与无底档跟随标注色。
    /// 新建（<see cref="CounterStyleSettings.TextColorFor"/>）与回改（<see cref="WithStyle"/> /
    /// <see cref="WithAnnotationColor"/>）两侧都读这一处，写第二份必然随档位调整而分叉。
    /// </summary>
    public static Color TextColorFor(CounterFillMode fill, Color annotationColor)
        => fill == CounterFillMode.Solid ? Color.White : annotationColor;

    static CounterShape Build(PointF center, int number, float radius, Color fillColor, Color textColor, Guid id,
        CounterSerialKind serial, CounterGlyphShape glyph, CounterFillMode fill, float outlineWidth,
        bool fontBold, CounterFontFamily fontFamily, string note, float noteFontSize,
        PointF? tail, ArrowAnnotationStyle tailHead, bool tailShown, float capsuleCorner, PointF? tailControl)
        => new(center, number, radius, fillColor, textColor, id, serial, glyph, fill, outlineWidth,
            fontBold, fontFamily, note, noteFontSize, tail, tailHead, tailShown, capsuleCorner, tailControl);
}
