using System;
using System.Drawing;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 参数条上「能回改到已选中图元」的分项开关：一次用户动作只点亮它真正改过的那一两档。
///
/// <para>为什么必须逐档点亮、不能整把塞进去：选中一个<b>虚线</b>矩形后去点「形状：矩形/圆形」，
/// 若连「线型」一并回灌，那条虚线就会被工具栏的记忆值悄悄改成实线——用户只改了形状，另一档凭空变了，
/// 本轮要修的正是这类"顺手抹档"。</para>
///
/// <para>「整包」两档（<see cref="BlurSettings"/> / <see cref="SpotlightSettings"/>）与上面那九档的粗度不同，
/// 是照抄截图侧既有的 <c>BlurSettingsChanged</c> / <c>SpotlightSettingsChanged</c>：马赛克与聚光灯的参数控件
/// 只在自己的工具档上出现，一次改动不可能顺手抹掉别档的样式，故不必逐格拆。</para>
///
/// <para>唯一不并进这张表的是截图侧聚光灯那条"没有选中时把暗度同步给全部图元"的环境光特例——它依赖遮罩压暗
/// 这个截图独有语义，写进通用件会污染"选中谁改谁"的主干；截图侧沿用自己的既有通路，贴图侧没有遮罩，
/// 未选中时暗度只成为下一颗的记忆值。</para>
/// </summary>
// ⚠ 本枚举是 public 而上面的分派表仍是 internal：贴图侧的共享控件 AnnotationToolbar（public）要把
// "用户刚改了哪一档"作为事件参数发出去，事件签名的可访问性不能低于参数类型。
[Flags]
public enum AnnotationStylePart
{
    Color = 1 << 0,
    Thickness = 1 << 1,
    ShapeKind = 1 << 2,
    ShapeLineStyle = 1 << 3,
    ShapeFill = 1 << 4,
    ArrowStyle = 1 << 5,
    CounterStyle = 1 << 6,
    CounterRadius = 1 << 7,
    CounterTailHead = 1 << 8,
    /// <summary>马赛克面板整包（模糊/马赛克模式 + 强度档位）。</summary>
    BlurSettings = 1 << 9,
    /// <summary>聚光灯面板整包（暗幕 + 形状档 + 描边色 + 描边粗细）。圆角不在包里，理由见 <see cref="ApplySpotlight"/>。</summary>
    SpotlightSettings = 1 << 10,
}

/// <summary>
/// 把「工具栏当前记忆值」落到<b>已选中的那一个</b>图元上——截图遮罩与贴图浮窗共用这一张分派表。
///
/// <para>两侧宿主各自的事件名、坐标口径与撤销粒度都不同，唯一能共用的就是"哪一档改哪个字段"这件事。
/// 分开放两份的话，任何一档补漏都要记得改两处，而贴图侧已经整整落后一轮（2026-09-19 审计）。</para>
///
/// <para>返回值语义：<b>null ＝ 什么都不必做</b>（这一档在该图元上没有对应字段，或逐档比下来确实没变）。
/// 宿主据此决定要不要压撤销快照——照 "先压快照再改" 的写法，一次点击会在撤销栈里留一条空记录。</para>
/// </summary>
internal static class AnnotationStyleApplier
{
    public static AnnotationShape? Apply(AnnotationShape selected, AnnotationGestureSettings s, AnnotationStylePart parts)
        => selected switch
        {
            ArrowShape arrow => ApplyArrow(arrow, s, parts),
            RectangleShape rect => ApplyRect(rect, s, parts),
            CounterShape counter => ApplyCounter(counter, s, parts),
            StrokeShape stroke => ApplyStroke(stroke, s, parts),
            BlurShape blur => ApplyBlur(blur, s, parts),
            SpotlightShape spot => ApplySpotlight(spot, s, parts),
            // 文本层是活可视树、不在图元名单里，那一档由两侧各自的文本通路负责
            _ => null,
        };

    static AnnotationShape? ApplyArrow(ArrowShape a, AnnotationGestureSettings s, AnnotationStylePart parts)
    {
        ArrowShape next = a;
        if (parts.HasFlag(AnnotationStylePart.Color) && a.Color != s.Color) next = next.WithColor(s.Color);
        if (parts.HasFlag(AnnotationStylePart.Thickness) && Math.Abs(a.Thickness - s.Thickness) > 0.001f)
            next = next.WithThickness(s.Thickness);
        if (parts.HasFlag(AnnotationStylePart.ArrowStyle) && a.Style != s.ArrowStyle) next = next.WithStyle(s.ArrowStyle);
        return ReferenceEquals(next, a) ? null : next;
    }

    static AnnotationShape? ApplyRect(RectangleShape r, AnnotationGestureSettings s, AnnotationStylePart parts)
    {
        RectangleShape next = r;
        // 填充色不单列字段、恒取描边色的 RGB，故换色那一档已经把填充一起带走（RectangleShape.FillOpacity 的口径）
        if (parts.HasFlag(AnnotationStylePart.Color) && r.StrokeColor != s.Color) next = next.WithColor(s.Color);
        if (parts.HasFlag(AnnotationStylePart.Thickness) && Math.Abs(r.StrokeThickness - s.Thickness) > 0.001f)
            next = next.WithThickness(s.Thickness);
        if (parts.HasFlag(AnnotationStylePart.ShapeKind) && r.ShapeKind != s.ShapeKind) next = next.WithShapeKind(s.ShapeKind);
        if (parts.HasFlag(AnnotationStylePart.ShapeLineStyle) && r.LineStyle != s.ShapeLineStyle)
            next = next.WithLineStyle(s.ShapeLineStyle);
        if (parts.HasFlag(AnnotationStylePart.ShapeFill) && Math.Abs(r.FillOpacity - s.ShapeFillOpacity) > 0.001f)
            next = next.WithFill(s.ShapeFillOpacity);
        return ReferenceEquals(next, r) ? null : next;
    }

    static AnnotationShape? ApplyCounter(CounterShape c, AnnotationGestureSettings s, AnnotationStylePart parts)
    {
        CounterShape next = c;
        // 序号的「大小 / 外形·底色 / 引线箭头」三档各点各的：滚轮改大小若把参数条的样式记忆一并盖下去，
        // 用户上一颗调好的外形就在拖动中途被换了（2026-09-19 拍板：只改当前选中的那一颗，且只改被点那一档）
        if (parts.HasFlag(AnnotationStylePart.Color) && c.FillColor != s.Color) next = next.WithAnnotationColor(s.Color);
        if (parts.HasFlag(AnnotationStylePart.CounterStyle)
            && (c.Glyph != s.Counter.Glyph || c.Fill != s.Counter.Fill))
            next = next.WithStyle(s.Counter.Glyph, s.Counter.Fill);
        if (parts.HasFlag(AnnotationStylePart.CounterRadius) && Math.Abs(c.Radius - s.CounterRadius) > 0.001f)
            next = next.WithRadius(s.CounterRadius);
        if (parts.HasFlag(AnnotationStylePart.CounterTailHead) && c.TailHead != s.Counter.TailHead)
            next = next.WithTailHead(s.Counter.TailHead);
        return ReferenceEquals(next, c) ? null : next;
    }

    static AnnotationShape? ApplyStroke(StrokeShape stroke, AnnotationGestureSettings s, AnnotationStylePart parts)
    {
        // 只吃颜色，刻意**不**吃粗细/线型/笔锋：那三档属于"这一笔"的手感，参数条改动只当下一笔的记忆
        // （protection.md「标注画布鼠标手势优先级」④）。
        if (parts.HasFlag(AnnotationStylePart.Color) && stroke.Color != s.Color) return stroke.WithColor(s.Color);
        return null;
    }

    static AnnotationShape? ApplyBlur(BlurShape blur, AnnotationGestureSettings s, AnnotationStylePart parts)
    {
        if (!parts.HasFlag(AnnotationStylePart.BlurSettings)) return null;
        BlurShape next = blur;
        if (next.Mode != s.BlurMode) next = next.WithMode(s.BlurMode);
        if (next.UiLevel != s.BlurLevel) next = next.WithUiLevel(s.BlurLevel);
        return ReferenceEquals(next, blur) ? null : next;
    }

    /// <summary>
    /// 聚光灯：吃「面板整包」（暗幕／形状档／描边色／描边粗细），另外单独认「颜色」那一档——取色是全局档，
    /// 聚光灯的描边色跟着它走（截图侧选中聚光灯改颜色此前同样落不下去，同一批缺陷）。
    ///
    /// <para>⚠ 圆角刻意<b>不</b>进这一包：<c>AnnotationGestureSettings.CornerRadius</c> 是"按当前工具取格"的
    /// 共用字段（矩形档取矩形记忆、聚光档取聚光记忆），选中一颗聚光灯而工具条切到矩形档时，包里带上它就会
    /// 把矩形的圆角记忆盖到这颗聚光灯上——正是本轮要消灭的"顺手抹档"。两侧聚光灯圆角的唯一入口都是元素
    /// 上方那条圆角浮条，走各自的专用通路，故这里 <c>WithStyle</c> 少传圆角＝保留原值。</para>
    /// </summary>
    static AnnotationShape? ApplySpotlight(SpotlightShape sp, AnnotationGestureSettings s, AnnotationStylePart parts)
    {
        bool bundle = parts.HasFlag(AnnotationStylePart.SpotlightSettings);
        bool color = parts.HasFlag(AnnotationStylePart.Color);
        if (!bundle && !color) return null;

        float darkness = bundle ? s.Darkness : sp.Darkness;
        SpotlightShapeKind kind = bundle ? s.SpotlightKind : sp.ShapeKind;
        Color strokeColor = (bundle || color) ? s.Color : sp.StrokeColor;
        float thickness = bundle ? s.SpotlightThickness : sp.StrokeThickness;
        if (Math.Abs(darkness - sp.Darkness) <= 0.001f
            && kind == sp.ShapeKind
            && strokeColor == sp.StrokeColor
            && Math.Abs(thickness - sp.StrokeThickness) <= 0.001f) return null;
        return sp.WithStyle(darkness, kind, strokeColor, thickness);
    }
}
