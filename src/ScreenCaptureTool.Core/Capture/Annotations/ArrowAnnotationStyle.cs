namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 箭头头型。箭头标注工具与序号引线共用这一份列表（同一批小样、同一个下拉）。
/// <para>⚠ 前两档 <see cref="Designed"/> 与 <see cref="Sharp"/> 是升级前就有的两档，走的是旧的
/// 0–100 模板路径（<c>AnnotationLayer.CreateLegacyArrowGeometry</c> /
/// <c>CaptureSession.CreateLegacyArrowPaths</c>），**画法一字未改**——历史截图与历史贴图里的
/// 这两档不会因本轮新增头型而变形。其余档位全部走 <see cref="ArrowHeadGeometry"/> 那一份新算式。</para>
/// <para>序号引线从来没有套过模板路径（引线那条杆比箭头细得多，套模板没有意义），故它在引线上
/// 表示的是同一件几何：Designed＝深凹口、Sharp＝实心三角，与今日引线的样子几乎重合。</para>
/// <para>枚举成员只追加、不重排：贴图存档落盘的是成员名字符串。</para>
/// </summary>
public enum ArrowAnnotationStyle
{
    /// <summary>现状档：深凹口（旧模板路径）</summary>
    Designed,
    /// <summary>现状档：实心三角（旧模板路径）</summary>
    Sharp,
    /// <summary>中等凹口（凹深 0.12 头长）</summary>
    Notch,
    /// <summary>弧边燕尾：两条回边改成二次弧，翼尖到凹口是外凸的弧线</summary>
    Curve,
    /// <summary>笔锋：实心三角头 + 尾端 0 → 头根 w 的渐粗杆</summary>
    Taper,
    /// <summary>开口 V：只有两条描边翼，杆一直画到尖端</summary>
    Chevron,
    /// <summary>空心三角</summary>
    OpenTriangle,
    /// <summary>实心菱形（端点符号尺寸，细长）</summary>
    Diamond,
    /// <summary>空心菱形</summary>
    OpenDiamond,
    /// <summary>实心圆点</summary>
    Dot,
    /// <summary>空心圆：外沿正齐尖端</summary>
    Ring,
}
