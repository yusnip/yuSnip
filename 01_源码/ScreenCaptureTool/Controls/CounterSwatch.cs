using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ScreenCaptureTool.Core.Capture.Annotations;
using GdiColor = System.Drawing.Color;
using GdiPointF = System.Drawing.PointF;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 序号样式的小样：把一颗序号按 <see cref="CounterPainter"/>（与画布同一份画法）画进控件自身，
/// 供参数条「样式」按钮与样式工具栏上那一排格子使用。
///
/// <para>为什么不让 UI 侧另画一遍简化的圆点：这一格的唯一职责就是"下一个号画出来长什么样"，
/// 自己拼几何迟早和画布分叉（同一颗号在条上是一个样、画在图上是另一个样）。所以这里只造一个
/// 无引线、无说明的 <see cref="CounterShape"/>，坐标按 DIP 直进直出，比例尺恒 1。</para>
///
/// <para>编号恒取 1 号（字母档即 A、罗马档即 Ⅰ）：小样是"样式"的样张，不是"下一个号"的预告，
/// 那由「起始」那一格说话；固定单位数也让胶囊档的小样不会随输入长短跳动。</para>
/// </summary>
public sealed class CounterSwatch : Control
{
    /// <summary>小样只画单位数：胶囊档按编号宽度撑长，多位数会把小样顶出格子。</summary>
    private const int SampleNumber = 1;

    /// <summary><see cref="CounterShape"/> 的半径下限，低于它图元自己会抬到 8，小样画出来就对不上。</summary>
    private const double MinRadius = 8.0;

    private CounterGlyphShape _glyph = CounterGlyphShape.Circle;
    private CounterFillMode _fill = CounterFillMode.Solid;
    private CounterSerialKind _serial = CounterSerialKind.Number;
    private Color _annotationColor = Color.FromRgb(51, 136, 255);
    private double _radius = MinRadius;

    /// <summary>外形档（圆 / 圆角方 / 胶囊）。</summary>
    public CounterGlyphShape Glyph
    {
        get => _glyph;
        set { if (_glyph == value) return; _glyph = value; InvalidateVisual(); }
    }

    /// <summary>底色档（实心 / 描边 / 无底）。实心档的小样用白字，与画布同一规则。</summary>
    public CounterFillMode Fill
    {
        get => _fill;
        set { if (_fill == value) return; _fill = value; InvalidateVisual(); }
    }

    /// <summary>序号类型档：决定小样上写的是 1 / A / Ⅰ。</summary>
    public CounterSerialKind Serial
    {
        get => _serial;
        set { if (_serial == value) return; _serial = value; InvalidateVisual(); }
    }

    /// <summary>当前标注色。小样要跟着它变，否则"改了颜色，条上看不出来"。</summary>
    public Color AnnotationColor
    {
        get => _annotationColor;
        set { if (_annotationColor == value) return; _annotationColor = value; InvalidateVisual(); }
    }

    /// <summary>
    /// 半径（DIP）。⚠ 图元有下限 8，故小样最小只能到 8：要 18 见方就取 8（描边 2 居中压边，外沿 18）。
    /// </summary>
    public double Radius
    {
        get => _radius;
        set { double v = Math.Max(MinRadius, value); if (Math.Abs(_radius - v) < 0.01) return; _radius = v; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var color = GdiColor.FromArgb(_annotationColor.A, _annotationColor.R, _annotationColor.G, _annotationColor.B);
        // 与 CounterStyleSettings.TextColorFor 同一判据：实心档压白字，描边与无底档字色即标注色
        var textColor = _fill == CounterFillMode.Solid ? GdiColor.White : color;
        var shape = new CounterShape(new GdiPointF((float)(Bounds.Width / 2.0), (float)(Bounds.Height / 2.0)),
            SampleNumber, (float)_radius, color, textColor,
            serial: _serial, glyph: _glyph, fill: _fill);
        // 小样自己的坐标系就是 DIP：比例尺 1、不换原点
        CounterPainter.Paint(context, shape, 1.0, p => new Point(p.X, p.Y));
    }
}
