using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 界面共用的连续 R 角。每角两段三次贝塞尔，直边接点曲率为零，角中点切线与曲率连续（G2）。
/// R 表示直边到转角的过渡长度；四角统一按边长归一化，窄控件不会发生路径交叉。
/// </summary>
public static class ContinuousCorners
{
    public static StreamGeometry Create(Rect rect, CornerRadius radius)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        Write(context, rect, radius);
        return geometry;
    }

    internal static void Write(StreamGeometryContext context, Rect rect, CornerRadius radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        double tl = Math.Max(0, radius.TopLeft), tr = Math.Max(0, radius.TopRight);
        double br = Math.Max(0, radius.BottomRight), bl = Math.Max(0, radius.BottomLeft);
        double scale = Math.Min(1, Math.Min(Math.Min(Ratio(rect.Width, tl + tr), Ratio(rect.Width, bl + br)),
            Math.Min(Ratio(rect.Height, tl + bl), Ratio(rect.Height, tr + br))));
        tl *= scale; tr *= scale; br *= scale; bl *= scale;
        double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
        context.BeginFigure(new Point(l + tl, t), true);
        context.LineTo(new Point(r - tr, t));
        Corner(context, new Point(r - tr, t), new Vector(1, 0), new Vector(0, 1), tr);
        context.LineTo(new Point(r, b - br));
        Corner(context, new Point(r, b - br), new Vector(0, 1), new Vector(-1, 0), br);
        context.LineTo(new Point(l + bl, b));
        Corner(context, new Point(l + bl, b), new Vector(-1, 0), new Vector(0, -1), bl);
        context.LineTo(new Point(l, t + tl));
        Corner(context, new Point(l, t + tl), new Vector(0, -1), new Vector(1, 0), tl);
        context.EndFigure(true);
    }

    private static double Ratio(double length, double sum) => sum > 0 ? length / sum : 1;

    private static void Corner(StreamGeometryContext context, Point start, Vector along, Vector inward, double r)
    {
        // 局部坐标从 (0,0) 转至 (R,R)：两端控制点与直边共线，中点关于角平分线对称。
        Point P(double x, double y) => start + along * (x * r) + inward * (y * r);
        context.CubicBezierTo(P(.3, 0), P(.6, 0), P(.8, .2));
        context.CubicBezierTo(P(1, .4), P(1, .7), P(1, 1));
    }
}

/// <summary>共享绘制缓存：填色、描边与内容裁剪使用同一轮廓；仅尺寸、R 或线宽变化时重建。</summary>
internal sealed class ContinuousCornerRenderer
{
    private Size _size;
    private CornerRadius _radius;
    private Thickness _thickness;
    private StreamGeometry? _outline;
    private StreamGeometry? _ring;

    public StreamGeometry Outline(Size size, CornerRadius radius, Thickness thickness)
    {
        if (_outline != null && _size == size && _radius == radius && _thickness == thickness) return _outline;
        _size = size; _radius = radius; _thickness = thickness;
        var rect = new Rect(size);
        _outline = ContinuousCorners.Create(rect, radius);
        _ring = new StreamGeometry();
        using var context = _ring.Open();
        context.SetFillRule(FillRule.EvenOdd);
        ContinuousCorners.Write(context, rect, radius);
        var inner = rect.Deflate(thickness);
        if (inner.Width > 0 && inner.Height > 0)
        {
            var innerRadius = new CornerRadius(
                Math.Max(0, radius.TopLeft - Math.Max(thickness.Left, thickness.Top)),
                Math.Max(0, radius.TopRight - Math.Max(thickness.Right, thickness.Top)),
                Math.Max(0, radius.BottomRight - Math.Max(thickness.Right, thickness.Bottom)),
                Math.Max(0, radius.BottomLeft - Math.Max(thickness.Left, thickness.Bottom)));
            ContinuousCorners.Write(context, inner, innerRadius);
        }
        return _outline;
    }

    public void Render(DrawingContext context, Size size, CornerRadius radius, Thickness thickness, IBrush? background, IBrush? border)
    {
        var outline = Outline(size, radius, thickness);
        context.DrawGeometry(background, null, outline);
        if (thickness != default && border != null) context.DrawGeometry(border, null, _ring!);
    }
}

/// <summary>可复用连续曲率容器；填色、描边与内容裁剪共享同一轮廓。</summary>
public class ContinuousBorder : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<ContinuousBorder>();
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = Border.BorderBrushProperty.AddOwner<ContinuousBorder>();
    public static readonly StyledProperty<Thickness> BorderThicknessProperty = Border.BorderThicknessProperty.AddOwner<ContinuousBorder>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<ContinuousBorder>();
    public static readonly StyledProperty<BoxShadows> BoxShadowProperty = Border.BoxShadowProperty.AddOwner<ContinuousBorder>();
    private ContinuousCornerRenderer? _renderer;
    private StreamGeometry? _contentClip;
    private Rect _contentClipRect;
    private CornerRadius _contentClipRadius;
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Thickness BorderThickness { get => GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public BoxShadows BoxShadow { get => GetValue(BoxShadowProperty); set => SetValue(BoxShadowProperty, value); }
    protected override Type StyleKeyOverride => typeof(Border);

    static ContinuousBorder() => AffectsRender<ContinuousBorder>(BackgroundProperty, BorderBrushProperty, BorderThicknessProperty, CornerRadiusProperty, BoxShadowProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 滚动／动画只改变位置时，局部轮廓与内容布局不变，不再额外排一次布局。
        bool sizeChanged = change.Property == BoundsProperty
            && change.GetOldValue<Rect>().Size != change.GetNewValue<Rect>().Size;
        if (sizeChanged || change.Property == CornerRadiusProperty || change.Property == BorderThicknessProperty || change.Property == BoxShadowProperty)
        {
            Clip = BoxShadow.Count == 0 ? (_renderer ??= new()).Outline(Bounds.Size, CornerRadius, BorderThickness) : null;
            InvalidateArrange();
        }
        if (change.Property == BorderThicknessProperty) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var insets = Padding + BorderThickness;
        Child?.Measure(availableSize.Deflate(insets));
        return (Child?.DesiredSize ?? default).Inflate(insets);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize).Deflate(Padding + BorderThickness));
        // 只裁剪内容，保留容器外侧的柔和投影；内容坐标与轮廓坐标显式换算。
        if (Child is { } child)
        {
            var rect = new Rect(-child.Bounds.X, -child.Bounds.Y, finalSize.Width, finalSize.Height);
            // 相同几何沿用同一个平台路径；重建并赋值 Clip 会使内容与 GPU 裁剪缓存一起失效。
            if (_contentClip == null || _contentClipRect != rect || _contentClipRadius != CornerRadius)
            {
                _contentClipRect = rect;
                _contentClipRadius = CornerRadius;
                _contentClip = ContinuousCorners.Create(rect, CornerRadius);
            }
            if (!ReferenceEquals(child.Clip, _contentClip)) child.Clip = _contentClip;
        }
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (BoxShadow.Count > 0)
            context.DrawRectangle(Brushes.Transparent, null, new RoundedRect(new Rect(Bounds.Size), CornerRadius), BoxShadow);
        (_renderer ??= new()).Render(context, Bounds.Size, CornerRadius, BorderThickness, Background, BorderBrush);
    }
}

/// <summary>按钮的连续曲率绘制层。内容交给原生 Presenter，保留现有状态选择器与文字继承。</summary>
public sealed class ContinuousContentPresenter : ContinuousBorder
{
    public static readonly StyledProperty<object?> ContentProperty = Avalonia.Controls.Presenters.ContentPresenter.ContentProperty.AddOwner<ContinuousContentPresenter>();
    public static readonly StyledProperty<Avalonia.Controls.Templates.IDataTemplate?> ContentTemplateProperty = Avalonia.Controls.Presenters.ContentPresenter.ContentTemplateProperty.AddOwner<ContinuousContentPresenter>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty.AddOwner<ContinuousContentPresenter>();
    public static readonly StyledProperty<Avalonia.Layout.HorizontalAlignment> HorizontalContentAlignmentProperty = Avalonia.Controls.Presenters.ContentPresenter.HorizontalContentAlignmentProperty.AddOwner<ContinuousContentPresenter>();
    public static readonly StyledProperty<Avalonia.Layout.VerticalAlignment> VerticalContentAlignmentProperty = Avalonia.Controls.Presenters.ContentPresenter.VerticalContentAlignmentProperty.AddOwner<ContinuousContentPresenter>();
    public static readonly StyledProperty<bool> RecognizesAccessKeyProperty = Avalonia.Controls.Presenters.ContentPresenter.RecognizesAccessKeyProperty.AddOwner<ContinuousContentPresenter>();
    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    public Avalonia.Controls.Templates.IDataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Avalonia.Layout.HorizontalAlignment HorizontalContentAlignment { get => GetValue(HorizontalContentAlignmentProperty); set => SetValue(HorizontalContentAlignmentProperty, value); }
    public Avalonia.Layout.VerticalAlignment VerticalContentAlignment { get => GetValue(VerticalContentAlignmentProperty); set => SetValue(VerticalContentAlignmentProperty, value); }
    public bool RecognizesAccessKey { get => GetValue(RecognizesAccessKeyProperty); set => SetValue(RecognizesAccessKeyProperty, value); }
    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.Presenters.ContentPresenter);

    public ContinuousContentPresenter()
    {
        var presenter = new Avalonia.Controls.Presenters.ContentPresenter();
        presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.ContentProperty, this.GetObservable(ContentProperty));
        presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.ContentTemplateProperty, this.GetObservable(ContentTemplateProperty));
        presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, this.GetObservable(ForegroundProperty));
        presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.HorizontalContentAlignmentProperty, this.GetObservable(HorizontalContentAlignmentProperty));
        presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.VerticalContentAlignmentProperty, this.GetObservable(VerticalContentAlignmentProperty));
        presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.RecognizesAccessKeyProperty, this.GetObservable(RecognizesAccessKeyProperty));
        Child = presenter;
    }
}
