using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 自由画笔笔迹：原始采样序列 + 笔刷画像 + 标注颜色/粗细/线型。
/// 实际画出来的形状由栅格化器按画像现算，本类型只保存"笔迹本身"。
/// </summary>
public sealed class StrokeShape : AnnotationShape
{
    private readonly List<StrokePoint> _samples;

    /// <param name="samples">原始采样点（物理像素）。<paramref name="profile"/> 无默认值，见 <see cref="BrushProfile.Default"/>。</param>
    public StrokeShape(
        IEnumerable<StrokePoint> samples,
        Color color,
        float thickness,
        BrushProfile profile,
        AnnotationLineStyle lineStyle = AnnotationLineStyle.Solid,
        Guid id = default)
        : base(id)
    {
        _samples = samples?.ToList() ?? new List<StrokePoint>();
        Color = color;
        Thickness = Math.Max(1.0f, thickness);
        Profile = profile;
        LineStyle = lineStyle;
    }

    public override AnnotationShapeKind Kind => AnnotationShapeKind.Stroke;

    /// <summary>原始采样点序列，坐标为屏幕物理像素。</summary>
    public IReadOnlyList<StrokePoint> Samples => _samples;

    public Color Color { get; }

    public float Thickness { get; }

    /// <summary>笔刷画像（透明度/硬度/笔锋/平滑）。</summary>
    public BrushProfile Profile { get; }

    public AnnotationLineStyle LineStyle { get; }

    /// <summary>笔迹外接框（含笔尖支撑半径的余量，随硬度变化），坐标为屏幕物理像素。</summary>
    public override RectangleF Bounds
    {
        get
        {
            if (_samples.Count == 0) return RectangleF.Empty;

            float minX = _samples[0].X;
            float minY = _samples[0].Y;
            float maxX = _samples[0].X;
            float maxY = _samples[0].Y;

            for (int i = 1; i < _samples.Count; i++)
            {
                StrokePoint p = _samples[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            float pad = BrushRasterizer.GetSupportRadiusPx(Thickness, Profile.Hardness);
            return RectangleF.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
        }
    }

    public override AnnotationShape Clone()
    {
        return new StrokeShape(_samples.ToArray(), Color, Thickness, Profile, LineStyle, Id);
    }

    /// <summary>换描边色（选中笔迹后在参数条取色的去处）。粗细/画像/线型一律原样带走：
    /// 那几档属于"这一笔"，参数条改动只作用于下一笔（protection.md 手势优先级 ④）。</summary>
    public StrokeShape WithColor(Color color)
    {
        return new StrokeShape(_samples.ToArray(), color, Thickness, Profile, LineStyle, Id);
    }

    /// <summary>平移整条笔迹的所有采样点，保持原有采样时间戳与画像不变。</summary>
    public StrokeShape Translate(float dx, float dy)
    {
        if (dx == 0f && dy == 0f) return this;

        var translated = new StrokePoint[_samples.Count];
        for (int i = 0; i < _samples.Count; i++)
        {
            StrokePoint p = _samples[i];
            translated[i] = new StrokePoint(p.X + dx, p.Y + dy, p.TimestampMs);
        }

        return new StrokeShape(translated, Color, Thickness, Profile, LineStyle, Id);
    }
}
