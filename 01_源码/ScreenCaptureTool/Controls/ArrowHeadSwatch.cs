using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 箭头头型的小样：把一根水平箭头按 <see cref="AnnotationLayer.DrawArrowGeometry"/>（与画布同一份画法）
/// 画进控件自身，供箭头参数条按钮、样式下拉与序号的「引线箭头」下拉共用。
///
/// <para>为什么不让 UI 侧另描一遍轮廓：这一格的唯一职责是"这一档箭头长什么样"。2026-09-19 之前这里是
/// 两条写死的 Path，头型加了也不会出现在按钮上，正是自拼几何和画布分叉的现成例子。</para>
///
/// <para>⚠ 比例尺<b>分族</b>取：旧模板两档的头长恒为 50 单位，新算式各档的头长约 3.5×笔宽，两族差着一个
/// 量级；若整列共用一个比例尺，小头档会缩成一个点、模板档会把样片顶爆。故这里只按"头占样片高 62%、
/// 杆约占 3 DIP"给每一族各自反解比例尺——形状仍完全由 Core 那一份算式给。<b>小样是"形"的样张，
/// 绝对大小由画布上的「大小」说话。</b></para>
/// </summary>
public sealed class ArrowHeadSwatch : Control
{
    /// <summary>旧模板档的头长（单位像素）：预览与导出两侧模板都写死 50，这里按同一个数反解比例尺。</summary>
    private const double LegacyHeadUnits = 50.0;

    /// <summary>头高占样片高的比例，以及新算式档的笔宽下限（再细的杆也画不出 3 DIP 以外的形）。</summary>
    private const double HeadFill = 0.62;
    private const double MinWidthDip = 3.0;

    /// <summary>左右留白：描边档的圆头端帽会朝尖端外伸半笔宽，不留白就会被样片边切掉。</summary>
    private const double EndPadding = 3.0;

    private ArrowAnnotationStyle _style = ArrowAnnotationStyle.Designed;
    private bool _legacyPath = true;

    /// <summary>画哪一档头型。</summary>
    public ArrowAnnotationStyle Style
    {
        get => _style;
        set { if (_style == value) return; _style = value; InvalidateVisual(); }
    }

    /// <summary>
    /// Designed / Sharp 两档是否按箭头标注工具的模板路径出样。序号引线从来没套过模板，这两档在引线上
    /// 走的是新算式（深凹口与实心三角），形状与箭头工具上的并不相同——故引线那版下拉把这里关掉。
    /// </summary>
    public bool LegacyPath
    {
        get => _legacyPath;
        set { if (_legacyPath == value) return; _legacyPath = value; InvalidateVisual(); }
    }

    private bool _doubleHead;

    /// <summary>
    /// V 形家族那四档是否按"两端各挂一个头"出样。箭头标注工具的两处按钮（截图与贴图）要 true，
    /// 序号引线的两处要 false——样片必须与画布同一种挂法，否则下拉里看到的双头、画出来是单头。
    /// 名单在 <see cref="ArrowHeadGeometry.IsDoubleHeadStyle"/>，不在本控件里另抄一份。
    /// </summary>
    public bool DoubleHead
    {
        get => _doubleHead;
        set { if (_doubleHead == value) return; _doubleHead = value; InvalidateVisual(); }
    }

    private bool _highlight;

    /// <summary>
    /// 是否为"当前档"：下拉里已按用户要求去掉档名文字，故选中态只能由这一格自己染品牌紫来表达
    /// （§6.12：颜色是唯一的状态表达，不拼 ✓）。
    /// </summary>
    public bool Highlight
    {
        get => _highlight;
        set { if (_highlight == value) return; _highlight = value; InvalidateVisual(); }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 取色发生在 Render 里，主题换档时不重绘就会一直顶着旧主题的图标色
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double h = Bounds.Height;
        double len = Bounds.Width - EndPadding * 2.0;
        if (h < 6.0 || len < 8.0) return;

        double target = h * HeadFill;
        bool legacy = _legacyPath && ArrowHeadGeometry.IsLegacyStyle(_style);
        // 旧模板：头长＝50×比例尺、杆半宽＝笔宽×(50×比例尺)/100，两式联立反解
        double unit = legacy ? target / LegacyHeadUnits : 1.0;
        double thickness = legacy ? MinWidthDip / unit : Math.Max(MinWidthDip, target / 3.5);

        double midY = h / 2.0;
        var start = new Point(EndPadding, midY);
        var end = new Point(EndPadding + len, midY);
        AnnotationLayer.DrawArrowGeometry(context, ResolveBrush(), start, end,
            new Point(start.X + len / 2.0, midY), thickness, _style, unit, double.MaxValue, _legacyPath, _doubleHead);
    }

    /// <summary>
    /// 主题感知取色（无参 FindResource 不感知明暗）。刻意不给硬编码兜底色：Token 缺失应在三态验证时
    /// 一眼看出来，而不是被兜底色掩盖（设计规范 §8.2）。
    /// </summary>
    private IBrush ResolveBrush()
    {
        string key = _highlight ? "SctAccentBrush" : "SctIconBrush";
        return this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush : Brushes.Transparent;
    }
}
