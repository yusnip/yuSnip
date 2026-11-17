using System;
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
/// 贴图窗体阴影层：按图片**真实外轮廓**（超椭圆 n=5）画一圈高斯衰减光晕。
///
/// <b>为什么不用 <c>Border.BoxShadow</c></b>：那个 API 的圆角只能表达**正圆弧**，而贴图圆角是超椭圆——
/// 同参数下半径 r 的超椭圆在 45° 方向比正圆弧多出 0.231r 的材料（r=50 DIP 时 11.6px，已经把
/// 12px 的模糊整段吃光）。表现就是用户报的"直边有阴影、四个角是断的"。直边与角部要同时贴合，
/// 只能自绘等距偏移环；改 <c>CornerRadius</c> 或加 <c>Spread</c> 都无解（前者改不了形状族，后者会把
/// 直边的光晕整体外推、留下一圈无缝的空白）。
///
/// 偏移环用的是<b>真等距线</b>，两条口径：
/// - 直角（半径 0）：外扩 d 的轮廓是「圆角 = d 的<b>正圆</b>角矩形」，也就是矩形的真 Minkowski 膨胀，
///   与 <c>BoxShadow</c> 在直角图上画的形状一致 → <b>直角贴图剖面与改版前等价</b>。
/// - 圆角：把 <see cref="SelectionEffects.BuildOutlineWithNormals"/> 给出的轮廓点沿各自外法线整体外推 d。
///   超椭圆（n=5）与四条直边拼出来的是 C¹ 连续的凸曲线，法线偏移就是它的精确等距线，与成品侧
///   <c>WriteShadow</c> 的逐像素真距离场同一口径，两端看到的阴影必然同形。
///
/// ⚠ 别改回"矩形外扩 d 且半径同步改成 r+d"那种同族缩放：它对正圆弧恰好成立（√2−1=0.4142），
/// 对超椭圆不成立——45° 方向会多推 23%，15 DIP 的光晕在角部顶到 18.5 DIP，超出窗口预留边距被裁。
///
/// ⚠ 光晕一律画在自身边界**以外**（不含内侧填充）：贴图的圆角材料由图片自己提供，内侧再铺一层
/// 就会在透明 PNG 贴图里透出一张色膜。
/// ⚠ 绘制范围必须 ≤ 窗口为阴影预留的透明边距（<c>StickerWindow.ShadowMargin = 16</c>），超出即被窗口裁掉。
///
/// <b>基准线口径</b>：本控件与图片同矩形，铺的是<b>整张位图</b>的显示范围，而截图成品四周可能带一圈
/// 透明边距（描边外扩占的 <c>stroke+1</c>）。故光晕最内一圈的基准是「内容轮廓（= 控件边界内缩
/// <see cref="ContentInset"/>）沿外法线再外扩 <see cref="StrokeWidth"/>」这条<b>真等距线</b>，
/// 而不是控件边界本身。三值口径与 <c>SelectionEffects.CardGeometry</c> 一一对应，只由
/// <c>StickerWindow.UpdateShadowCornerRadius</c> 按同一缩放比换算下发——少传一项，圆角与描边同时开时
/// 角部就会与卡片断开一条 0.2312×描边宽的缝（同族缩放的固有误差，见 <c>SelectionEffects</c> 的不变量）。
/// 描边为 0 时内缩与外扩都是 0，基准线退回控件边界，与改版前逐像素等价。
/// </summary>
public sealed class StickerShadowLayer : Control
{
    /// <summary>高斯标准差：<c>BoxShadow</c> 的 Blur 是"扩散直径"口径，σ = Blur / 2。
    /// 沿用同一换算，直角贴图上的剖面才不会比改版前更浓或更淡。</summary>
    private static readonly double HaloSigma = StickerStyling.ShadowBlurRadius / 2.0;

    /// <summary>光晕截止距离（2.5σ，再往外已低于 1/255）。⚠ 必须 ≤ 窗口透明边距 16。</summary>
    private static readonly double HaloExtent = StickerStyling.ShadowBlurRadius * 1.25;

    /// <summary>偏移环圈数：半像素一档，环与环共用同一条边界线，抗锯齿会把相邻两档混成连续斜坡。</summary>
    public const int FullBandCount = 30;

    /// <summary>三值口径 / 颜色 / 开关任一变化（这几项不是 Avalonia 属性，没有 PropertyChanged）——
    /// 分层呈现窗据此重画一帧。</summary>
    internal event Action? Changed;

    private bool _isShadowEnabled = true;
    private Color _shadowColor = Colors.Black;
    private double _contentInset, _contentRadius, _strokeWidth;
    private double _parentScale = 1.0;

    /// <summary>补偿父层的图片缩放：仅羽化／扩散反向折算，卡片轮廓仍跟随图片。</summary>
    internal void SetParentScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
        if (_parentScale == scale) return;
        _parentScale = scale;
        InvalidateVisual();
    }

    /// <summary>象限片缓存（键＝设备像素下的半径/描边/截止/σ/颜色）。只在这些量变化时重建——
    /// 缩放、拖边、补帧都不改它（半径跟着显示倍率变时按 1/4 像素量化，补帧期间最多重建寥寥几次）。</summary>
    private SKImage? _tile;
    private (float R, float S, float E, float G, uint C, double Dpi) _tileKey;
    private int _tileP, _tileRc, _tileT;

    /// <summary>象限片重建累计次数（诊断用：并进"缩放段采证"的"阴影几何重建"那一格）。</summary>
    internal static long GeometryRebuildCount;

    /// <summary>阴影总开关（缩略图模式不画，改用加粗主题色描边标识）。自带烘焙阴影带的贴图
    /// 照常画——两种阴影并存（2026-09-07 用户拍板）：那种贴图的光晕基准是棋盘格背景板
    /// （＝位图）的外沿，整个光环都在位图范围之外，与图片零重叠，z 序无需特别安排。</summary>
    public bool IsShadowEnabled
    {
        get => _isShadowEnabled;
        set
        {
            if (_isShadowEnabled == value) return;
            _isShadowEnabled = value;
            InvalidateVisual();
            Changed?.Invoke();
        }
    }

    /// <summary>阴影颜色（不透明度由 <see cref="StickerStyling.ShadowOpacity"/> 折算进每一档环）。</summary>
    public Color ShadowColor
    {
        get => _shadowColor;
        set
        {
            if (_shadowColor == value) return;
            _shadowColor = value;
            InvalidateVisual();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 下发光晕基准线的三值口径（均为本控件坐标系 DIP）：<paramref name="contentInset"/> 是位图显示矩形
    /// 边缘到<b>内容轮廓</b>的透明边距，<paramref name="contentRadius"/> 是内容圆角半径（0＝直角），
    /// <paramref name="strokeWidth"/> 是描边宽度＝卡片外沿相对内容轮廓的外扩量。三者与
    /// <c>SelectionEffects.CardGeometry</c> 逐项对应；无烘焙效果的普通贴图为 <c>0, r, 0</c>。
    /// </summary>
    public void SetCardGeometry(double contentInset, double contentRadius, double strokeWidth)
    {
        double inset = Math.Max(0, contentInset);
        double radius = Math.Max(0, contentRadius);
        double stroke = Math.Max(0, strokeWidth);
        if (Math.Abs(_contentInset - inset) < 0.01
            && Math.Abs(_contentRadius - radius) < 0.01
            && Math.Abs(_strokeWidth - stroke) < 0.01) return;
        _contentInset = inset; _contentRadius = radius; _strokeWidth = stroke;
        InvalidateVisual();
        Changed?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_isShadowEnabled) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 象限片按**设备像素**烘（与分层呈现同一份 HaloTiles），画的时候反缩放回 DIP：
        // 静止态 1:1 像素贴图；补帧仅反向补偿羽化／扩散宽度，象限片成本与贴图面积无关。
        double dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (!(dpi > 0)) dpi = 1.0;
        double inset = _contentInset * dpi;
        double cardW = (w - _contentInset * 2) * dpi, cardH = (h - _contentInset * 2) * dpi;
        if (cardW <= 0 || cardH <= 0) return;
        double r = Math.Min(Math.Max(0, _contentRadius * dpi), Math.Min(cardW, cardH) / 2.0);
        var key = (Quarter(r), Quarter(_strokeWidth * dpi), (float)(HaloExtent * dpi / _parentScale), (float)(HaloSigma * dpi / _parentScale),
                   _shadowColor.ToUInt32(), dpi);
        if (_tile == null || key != _tileKey)
        {
            _tile?.Dispose();
            var built = HaloTiles.Build(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5);
            using (built.Bitmap) _tile = SKImage.FromBitmap(built.Bitmap);
            _tileKey = key; _tileP = built.P; _tileRc = built.Rc; _tileT = built.T;
            GeometryRebuildCount++;
        }

        // 卡片外沿（设备像素、本控件局部坐标）＝控件矩形内缩 inset；光晕画在它外面
        var card = new SKRect((float)inset, (float)inset, (float)(inset + cardW), (float)(inset + cardH));
        context.Custom(new HaloDraw(new Rect(-_tileP / dpi, -_tileP / dpi, w + 2 * _tileP / dpi, h + 2 * _tileP / dpi),
            _tile, card, _tileP, _tileRc, _tileT, dpi));
    }

    private static float Quarter(double v) => (float)(Math.Round(v * 4.0) / 4.0);

    /// <summary>把象限片拼成整圈光晕（四角镜像、四边剖面拉伸），在 Skia 画布上直接画。</summary>
    private sealed class HaloDraw : ICustomDrawOperation
    {
        private readonly SKImage _img;
        private readonly SKRect _card;
        private readonly int _p, _rc, _t;
        private readonly double _dpi;

        public HaloDraw(Rect bounds, SKImage img, SKRect card, int p, int rc, int t, double dpi)
        {
            Bounds = bounds; _img = img; _card = card; _p = p; _rc = rc; _t = t; _dpi = dpi;
        }

        public Rect Bounds { get; }
        public void Dispose() { }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null) return;
            using var api = lease.Lease();
            var c = api.SkCanvas;
            c.Save();
            float inv = (float)(1.0 / _dpi);
            c.Scale(inv, inv);   // 以下全部按设备像素画

            float L = _card.Left, T = _card.Top, R = _card.Right, B = _card.Bottom;
            float midX = (L + R) / 2f, midY = (T + B) / 2f;
            int P = _p, rc = _rc, cs = _t - 1;
            var src = new SKRect(0, 0, cs, cs);
            using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Low };

            void Corner(float cl, float ct, float cr, float cb, float ox, float oy, float sx, float sy)
            {
                c.Save();
                c.ClipRect(new SKRect(cl, ct, cr, cb));
                c.Translate(ox, oy);
                c.Scale(sx, sy);
                c.DrawImage(_img, src, src, paint);
                c.Restore();
            }
            Corner(L - P - 1, T - P - 1, midX, midY, L - P, T - P, 1, 1);
            Corner(midX, T - P - 1, R + P + 1, midY, R + P, T - P, -1, 1);
            Corner(L - P - 1, midY, midX, B + P + 1, L - P, B + P, 1, -1);
            Corner(midX, midY, R + P + 1, B + P + 1, R + P, B + P, -1, -1);

            float x0 = L + rc, x1 = R - rc;
            if (x1 > x0)
            {
                var col = new SKRect(_t - 1, 0, _t, P);
                c.DrawImage(_img, col, new SKRect(x0, T - P, x1, T), paint);
                c.Save(); c.Translate(0, B + P); c.Scale(1, -1);
                c.DrawImage(_img, col, new SKRect(x0, 0, x1, P), paint); c.Restore();
            }
            float y0 = T + rc, y1 = B - rc;
            if (y1 > y0)
            {
                var row = new SKRect(0, _t - 1, P, _t);
                c.DrawImage(_img, row, new SKRect(L - P, y0, L, y1), paint);
                c.Save(); c.Translate(R + P, 0); c.Scale(-1, 1);
                c.DrawImage(_img, row, new SKRect(0, y0, P, y1), paint); c.Restore();
            }
            c.Restore();
        }
    }

    /// <summary>标准正态分布累积概率 Φ(x)。</summary>
    private static double NormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

    /// <summary>同一条 Φ(x) 供分层呈现窗（<c>LayeredPinSurface</c>）烘阴影象限片用——两处必须同一公式，
    /// 否则切换呈现通路时光晕浓淡会跳一下。</summary>
    internal static double NormalCdfShared(double x) => NormalCdf(x);

    /// <summary>光晕高斯 σ（DIP），分层呈现窗按 DPI 换成物理像素后使用。</summary>
    internal static double HaloSigmaDip => HaloSigma;

    /// <summary>光晕截止距离（DIP）。</summary>
    internal static double HaloExtentDip => HaloExtent;

    /// <summary>当前阴影三值口径（DIP，本控件坐标系）：交给分层呈现窗逐项换算。</summary>
    internal (double Inset, double Radius, double Stroke) CardGeometryDip => (_contentInset, _contentRadius, _strokeWidth);

    /// <summary>erfc 近似（Abramowitz–Stegun 7.1.26，绝对误差 ≤1.5e−7，对 8bit alpha 足够）。</summary>
    private static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.5 * z);
        double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418
            + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587
            + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0.0 ? ans : 2.0 - ans;
    }
}
