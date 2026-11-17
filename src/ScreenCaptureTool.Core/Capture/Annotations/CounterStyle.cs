namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>序号类型档：决定同一个数值显示成 1 / A / I。</summary>
public enum CounterSerialKind
{
    /// <summary>阿拉伯数字 1,2,3…</summary>
    Number = 0,
    /// <summary>字母 A,B,…,Z,AA,AB…（Excel 式进位，不复用单字母）</summary>
    Letter = 1,
    /// <summary>罗马数字 I,II,…,MMMCMXCIX；超出经典表上限（3999）后回退成阿拉伯数字</summary>
    Roman = 2,
    /// <summary>汉字数字 一,二,…,十,十一,…,一百零五…；超出 9999 后回退成阿拉伯数字（同罗马档的兜底口径）</summary>
    Chinese = 3,
}

/// <summary>序号外形档。</summary>
public enum CounterGlyphShape
{
    /// <summary>圆形（默认，与升级前一致）</summary>
    Circle = 0,
    /// <summary>圆角方形</summary>
    RoundedSquare = 1,
    /// <summary>胶囊：随编号文字宽度横向撑长，两位数与 XIII 这类长内容不被切字</summary>
    Capsule = 2,
}

/// <summary>序号底色档。</summary>
public enum CounterFillMode
{
    /// <summary>实心：色底 + 白描边 + 白字（升级前唯一的一档）</summary>
    Solid = 0,
    /// <summary>描边：透明底 + 同色边框 + 同色字</summary>
    Outline = 1,
    /// <summary>无底：只有字，靠文字白描边保证压深色图也可读</summary>
    None = 2,
}

/// <summary>序号字体档。取值必须在 GDI+ 与 Avalonia 两侧都能按同名解析。</summary>
public enum CounterFontFamily
{
    Segoe = 0,
    Yahei = 1,
}

/// <summary>
/// 序号的样式档＋起始值，打包成一次起笔时冻结的快照。截图遮罩、贴图标注与贴图存档
/// 搬运的都是同一个类型，故"新建序号长什么样"只有一处定义。
/// <para>2026-09-19 样式减法：界面上只剩这四档可设。描边宽、字体（字重与字族）、引线（开关与
/// 粗细）、说明字号这六维一律固定为 <see cref="AnnotationRenderOptions"/> 里的那组常量
/// ——由 <see cref="CounterShape"/> 构造函数的默认参数供给，不再作为快照字段在各侧流动。</para>
/// <para>同一天晚些时候补的 <c>CapsuleCorner</c>（胶囊圆角）不在这四档里：它的入口不在参数条，
/// 而是选中某个序号后浮在它上方的那条浮条，故"下一个胶囊的圆角"也要经这份快照流动。</para>
/// <para>引线固定为"拖出"：新建序号是否带引线只取决于起笔那一下有没有拖出去（<c>tail</c> 是否为 null）。</para>
/// <para>同日再加的 <c>TailHead</c>（引线箭头头型）也不在那四档里：按用户要求，它做成与箭头标注工具
/// **同一个下拉、同一份 <see cref="ArrowAnnotationStyle"/> 列表**，只是入口在序号参数条上。</para>
/// </summary>
public sealed record CounterStyleSettings(
    CounterSerialKind Serial,
    CounterGlyphShape Glyph,
    CounterFillMode Fill,
    int StartValue,
    // 胶囊档的圆角（百分比 0–100）：只服务胶囊，其余外形档不读它；入口是选中后浮条上那一格
    float CapsuleCorner = AnnotationRenderOptions.CounterCapsuleCornerDefault,
    // 引线箭头头型：与箭头工具共用同一份列表；默认实心三角＝加这一档之前引线的样子
    ArrowAnnotationStyle TailHead = ArrowAnnotationStyle.Sharp)
{
    /// <summary>升级前的样子：数字 + 圆 + 实心。</summary>
    public static CounterStyleSettings Default { get; } = new(
        CounterSerialKind.Number,
        CounterGlyphShape.Circle,
        CounterFillMode.Solid,
        1);

    /// <summary>新建序号的编号：起始值与「现有最大值 + 1」取较大者（详见 <see cref="AnnotationDocument.NextCounterNumber"/>）。</summary>
    public int NextNumber(AnnotationDocument doc) => doc.NextCounterNumber(StartValue);

    /// <summary>编号文字色：转发 <see cref="CounterShape.TextColorFor"/>——新建与回改读同一份派生，不在这里再写一遍。</summary>
    public System.Drawing.Color TextColorFor(System.Drawing.Color annotationColor)
        => CounterShape.TextColorFor(Fill, annotationColor);

    /// <summary>按当前样式档造一个序号图元（<paramref name="tail"/>＝null 即原地按一下，没有引线）。</summary>
    public CounterShape NewShape(System.Drawing.PointF center, int number, float radius,
        System.Drawing.Color fillColor, System.Drawing.PointF? tail = null)
        => new(center, number, radius, fillColor, TextColorFor(fillColor),
            serial: Serial, glyph: Glyph, fill: Fill, tail: tail, capsuleCorner: CapsuleCorner,
            tailHead: TailHead);
}
