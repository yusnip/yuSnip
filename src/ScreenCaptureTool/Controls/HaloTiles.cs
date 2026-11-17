using System;
using ScreenCaptureTool.Core.Capture;
using SkiaSharp;
using DrawingPointF = System.Drawing.PointF;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 贴图窗体光晕的<b>象限片</b>构建器（2026-09-29 从 <c>LayeredPinSurface</c> 抽出，两条呈现通路共用）。
///
/// <para>一张 T×T 的左上角象限片：卡片外沿（超椭圆或直角）外面的 30 档高斯等距环，只画一次。
/// 四角镜像、四边取第 T−1 列/行（恰落在直边段）的剖面拉伸，就能拼出任意尺寸卡片的整圈光晕——
/// 分割线取在圆角与直边的切点上，超椭圆外法线在切点处与轴平行，拆分逐像素精确。
/// 每帧成本与贴图面积无关，只在半径/描边/颜色/DPI 变化时重建。</para>
///
/// <para>alpha 斜坡与 <see cref="StickerShadowLayer"/> 逐档现画的那一版<b>同一条式</b>：硬边经 σ 高斯模糊后
/// 距边 t 处的覆盖率 Φ(−t/σ)，取带中点；等距轮廓同式（圆角沿超椭圆外法线外推、直角＝矩形外扩且圆角只取 t）。</para>
/// </summary>
internal static class HaloTiles
{
    /// <summary>光晕相对卡片外沿的像素外扩（象限片的留边 P）。</summary>
    public static int PadPx(double stroke, double extent) => (int)Math.Ceiling(stroke + extent + 1.5) + 1;

    public readonly record struct Result(SKBitmap Bitmap, int P, int Rc, int T);

    /// <summary>按给定参数（全部为<b>目标设备像素</b>）烘一张象限片（预乘 BGRA）。调用方负责释放位图。</summary>
    public static Result Build(double radius, double stroke, double extent, double sigma, uint argb)
    {
        int P = PadPx(stroke, extent);
        int rc = (int)Math.Ceiling(Math.Max(0, radius));
        int T = P + rc + 1;           // 最后一列/行（T-1）恰好落在直边段，用作四条边的剖面
        float big = 2f * T + 8f;      // 虚拟卡片取得远大于象限片，其余三角都在片外

        var bmp = new SKBitmap(new SKImageInfo(T, T, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);

            DrawingPointF[]? pts = null, nrm = null;
            if (radius > 0.5)
                SelectionEffects.BuildOutlineWithNormals(new DrawingRectangleF(P, P, big, big), (float)radius, out pts, out nrm);

            var sc = new SKColor(argb);
            int bands = StickerShadowLayer.FullBandCount;
            double step = extent / bands;
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            for (int k = 0; k < bands; k++)
            {
                byte a = (byte)Math.Round(sc.Alpha * StickerStyling.ShadowOpacity
                    * StickerShadowLayer.NormalCdfShared(-(k + 0.5) * step / sigma));
                if (a == 0) break;
                paint.Color = new SKColor(sc.Red, sc.Green, sc.Blue, a);
                using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                AddContour(path, P, big, stroke, (k + 1) * step, pts, nrm);
                AddContour(path, P, big, stroke, k * step, pts, nrm);
                canvas.DrawPath(path, paint);
            }
        }
        return new Result(bmp, P, rc, T);
    }

    /// <summary>距卡片外沿 <paramref name="t"/> 的等距轮廓：圆角沿超椭圆外法线外推 <c>stroke+t</c>；
    /// 直角＝矩形外扩 <c>stroke+t</c>、圆角只取 <c>t</c>（尖角的 Minkowski 膨胀）。</summary>
    private static void AddContour(SKPath path, float origin, float size, double stroke, double t,
        DrawingPointF[]? pts, DrawingPointF[]? nrm)
    {
        double d = stroke + t;
        if (pts != null && nrm != null)
        {
            var poly = new SKPoint[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                poly[i] = new SKPoint((float)(pts[i].X + nrm[i].X * d), (float)(pts[i].Y + nrm[i].Y * d));
            path.AddPoly(poly, true);
            return;
        }
        var rect = new SKRect((float)(origin - d), (float)(origin - d), (float)(origin + size + d), (float)(origin + size + d));
        float rad = (float)Math.Min(t, Math.Min(rect.Width, rect.Height) / 2);
        if (rad <= 0.01f) path.AddRect(rect);
        else path.AddRoundRect(rect, rad, rad);
    }
}
