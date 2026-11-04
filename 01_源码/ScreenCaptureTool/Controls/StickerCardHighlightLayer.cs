using System;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using ScreenCaptureTool.Core.Capture;
using SkiaSharp;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 贴图内容主体（不含烘焙阴影部分）的高亮层。
/// 当截图开启阴影并贴出后，高亮层紧贴实际截图内容卡片外沿（内缩烘焙边距），
/// 采用与截图成品完全一致的超椭圆（n=5）R角与等距描边几何，
/// 在点击获得焦点时绘制高亮轮廓线与柔和辉光，失焦时隐藏。
/// </summary>
public sealed class StickerCardHighlightLayer : Control
{
    private bool _isHighlightVisible;
    private Rect _cardRect;
    private double _cornerRadius;
    private double _strokeWidth;
    private Avalonia.Media.Color _highlightColor = Colors.Transparent;

    public void UpdateHighlight(bool visible, Rect cardRect, double cornerRadius, double strokeWidth, Avalonia.Media.Color color)
    {
        if (_isHighlightVisible == visible
            && _cardRect == cardRect
            && Math.Abs(_cornerRadius - cornerRadius) < 0.01
            && Math.Abs(_strokeWidth - strokeWidth) < 0.01
            && _highlightColor == color) return;

        _isHighlightVisible = visible;
        _cardRect = cardRect;
        _cornerRadius = cornerRadius;
        _strokeWidth = strokeWidth;
        _highlightColor = color;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_isHighlightVisible || _cardRect.Width <= 0 || _cardRect.Height <= 0) return;

        double dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (!(dpi > 0)) dpi = 1.0;

        context.Custom(new DrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), _cardRect, _cornerRadius, _strokeWidth, _highlightColor, dpi));
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly Rect _cardRect;
        private readonly double _radius;
        private readonly double _stroke;
        private readonly Avalonia.Media.Color _color;
        private readonly double _dpi;

        public DrawOp(Rect bounds, Rect cardRect, double radius, double stroke, Avalonia.Media.Color color, double dpi)
        {
            Bounds = bounds;
            _cardRect = cardRect;
            _radius = radius;
            _stroke = stroke;
            _color = color;
            _dpi = dpi;
        }

        public Rect Bounds { get; }
        public void Dispose() { }
        public bool HitTest(Avalonia.Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null) return;
            using var api = lease.Lease();
            var c = api.SkCanvas;

            float l = (float)(_cardRect.X * _dpi);
            float t = (float)(_cardRect.Y * _dpi);
            float w = (float)(_cardRect.Width * _dpi);
            float h = (float)(_cardRect.Height * _dpi);
            float r = (float)(_radius * _dpi);
            float stroke = (float)(_stroke * _dpi);

            c.Save();
            float inv = (float)(1.0 / _dpi);
            c.Scale(inv, inv);

            var skColor = new SKColor(_color.R, _color.G, _color.B, _color.A);
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(3.5 * _dpi));
            using var glowPaint = new SKPaint
            {
                Color = skColor.WithAlpha(160),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(3.0 * _dpi),
                IsAntialias = true,
                MaskFilter = blur
            };
            using var linePaint = new SKPaint
            {
                Color = skColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(1.5 * _dpi),
                IsAntialias = true
            };

            var rectPx = new RectangleF(l, t, w, h);
            PointF[] pts;
            // 采用与截图完全一致的超椭圆（n=5）R角及真等距线，杜绝正圆弧导致的 0.231r 角部错位
            if (stroke > 0f)
            {
                var outer = SelectionEffects.BuildStrokeOuterOutline(rectPx, r, stroke);
                pts = outer.Length >= 3 ? outer : SelectionEffects.BuildOutline(rectPx, r);
            }
            else
            {
                pts = SelectionEffects.BuildOutline(rectPx, r);
            }

            using var path = new SKPath();
            if (pts.Length >= 3)
            {
                path.MoveTo(pts[0].X, pts[0].Y);
                for (int i = 1; i < pts.Length; i++)
                {
                    path.LineTo(pts[i].X, pts[i].Y);
                }
                path.Close();
                c.DrawPath(path, glowPaint);
                c.DrawPath(path, linePaint);
            }
            c.Restore();
        }
    }
}
