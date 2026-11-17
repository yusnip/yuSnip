using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
// 洞的轮廓点沿用 Core/截图层那套 System.Drawing 口径（与 SelectionLayer 同一别名写法，避免与 Avalonia.Point 撞名）
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 选框两件套视觉口径的<b>唯一</b>实现处：<b>①</b>「截图选框样式」三键（线型／ARGB 色／粗细）→ 描边 Pen，
/// <b>②</b>选区外暗化遮罩的浓度与挖洞几何。两处消费方：<see cref="Controls.SelectionLayer"/>（截图遮罩的
/// 选框与暗窗）与 <see cref="Windows.VideoStickerWindow"/>（录屏预览的画面卡外框）、
/// <see cref="Windows.RecordOverlayWindow"/>（录制中的选区外暗化）。
/// <para>为什么上提到这一位阶（与 <see cref="LayoutMetrics"/> 同类的"现量／现算工具"）：这三项设置原本
/// 只被 <c>SelectionLayer.GetBorderPen</c> 消费，逻辑写在该控件私有成员里。录屏预览窗要「跟截图选框样式
/// 保持一致」（2026-09-22 用户原话），窗口自己再写一份 <c>if (style=="Dashed")</c> 就是两份实现——
/// 下次改虚线段长或加一档线型必漏一处。色值同理：录制遮罩要的"选区外压暗"与截图那层必须是同一浓度，
/// 调一档两处一起变。</para>
/// <para>⚠ 本类不含任何"选区在哪"的知识：坐标一律由调用方按自己的口径算好再传（截图层是窗口 DIP，
/// 录制遮罩的预览态暗纱是把 hole 对齐到<b>预览卡</b>的真实原点，见 <see cref="Windows.RecordOverlayWindow"/> 的 BuildPreviewMask）。</para>
/// </summary>
public static class SelectionVisuals
{
    /// <summary>选区外暗化浓度：黑 96/255 ≈ 38%。钉死不随明暗主题变——这一层永远压在任意应用内容上，
    /// 与截图遮罩共用同一档，两处的"暗"才是同一种暗。</summary>
    public static readonly IBrush MaskBrush = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0));

    /// <summary>选框虚线的绝对像素档：段 12 / 间隔 6（截图选框与预览外框同一条）。</summary>
    public const double BorderDashPx = 12.0;
    public const double BorderGapPx = 6.0;

    /// <summary>绝对像素的段长/间隔 → Avalonia 要的「线宽倍数」两元素（它的 DashStyle 数值按线宽计，
    /// 不除就随线宽变粗而变长）。全软件只有这一处做那次除法，<see cref="DashPx"/> 与
    /// <see cref="BuildBorderDash"/> 都从这里取。</summary>
    internal static AvaloniaList<double> DashMultiples(double dashPx, double gapPx, double thickness)
        => new() { dashPx / thickness, gapPx / thickness };

    internal static DashStyle DashPx(double dashPx, double gapPx, double thickness)
        => new DashStyle(DashMultiples(dashPx, gapPx, thickness), 0);

    /// <summary>
    /// 「截图选框样式」三键 → 描边 Pen。<paramref name="thickness"/> ≤ 0 表示"不画框"，返回 null
    /// （调用方跳过绘制，与截图侧同一语义）；<paramref name="colorArgb"/> 的 Alpha 为 0 时兜底成不透明，
    /// 免得历史上存过 0x00RRGGBB 的配置项把框画成隐形。
    /// </summary>
    public static IPen? BuildBorderPen(string? style, int colorArgb, int thickness)
    {
        if (thickness <= 0) return null;

        byte a = (byte)((colorArgb >> 24) & 0xFF);
        if (a == 0) a = 0xFF;
        byte r = (byte)((colorArgb >> 16) & 0xFF);
        byte g = (byte)((colorArgb >> 8) & 0xFF);
        byte b = (byte)(colorArgb & 0xFF);

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(a, r, g, b)), thickness);
        AvaloniaList<double>? dash = BuildBorderDash(style, thickness);
        if (dash != null) pen.DashStyle = new DashStyle(dash, 0);
        return pen;
    }

    /// <summary>虚线档的 <c>StrokeDashArray</c> 形态（元素＝线宽倍数）：Avalonia 的 <c>Shape</c> 控件
    /// （预览外框那枚 <c>Rectangle</c>）不吃 <c>IPen</c>，只吃这个数组，故与 <see cref="BuildBorderPen"/>
    /// 共用同一对常数与同一次除法 ⇒ 不可能出现"截图选框是虚线、预览外框是实线"。实线返回 null。
    /// 返回 <see cref="AvaloniaList{T}"/> 是 <c>Shape.StrokeDashArray</c> 的属性类型所定，不是随手挑的集合。</summary>
    public static AvaloniaList<double>? BuildBorderDash(string? style, int thickness)
        => string.Equals(style, AppSettings.SelectionBorderStyleSolid, StringComparison.OrdinalIgnoreCase)
            ? null
            : DashMultiples(BorderDashPx, BorderGapPx, thickness);

    /// <summary>整屏矩形 + 洞轮廓（点集为窗口 DIP 绝对坐标）：一条路径两个子闭图，EvenOdd 填充规则天然表达"挖掉"。
    /// 洞走点集而非矩形，是为了贴合截图选框的圆角；录屏侧传四点矩形即可。</summary>
    public static Geometry BuildMaskGeometry(PointF[] hole, double ox, double oy, double screenW, double screenH)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.LineTo(new Point(screenW, 0));
            ctx.LineTo(new Point(screenW, screenH));
            ctx.LineTo(new Point(0, screenH));
            ctx.EndFigure(true);

            if (hole.Length >= 3)
            {
                ctx.BeginFigure(new Point(hole[0].X + ox, hole[0].Y + oy), true);
                for (int i = 1; i < hole.Length; i++) ctx.LineTo(new Point(hole[i].X + ox, hole[i].Y + oy));
                ctx.EndFigure(true);
            }
        }
        return geo;
    }

    /// <summary>矩形洞版：<see cref="BuildMaskGeometry"/> 的洞轮廓由这里现算，调用方不必自己铺点
    /// （两条路径共用同一份 EvenOdd 实现，不存在"暗窗画法有两种"）。
    /// <para><paramref name="radiusDip"/>＝洞的圆角半径（<b>窗口 DIP</b>，与截图层 <c>EffectRadiusDip</c> 同口径），
    /// 走的是截图成品那条 <see cref="SelectionEffects.BuildOutline"/> 超椭圆轮廓 ⇒ 与截图遮罩同一个弧度、
    /// 同一套采样步数。传 0（默认）＝四角直角，历史行为不变；半径超过短边一半由调用方先钳
    /// （截图层同一条：见 <c>SelectionLayer.EffectRadiusDip</c>）。
    /// ⚠ 别图省事把这里换成 <c>RectangleGeometry</c>：洞一旦与卡片各自的圆角算法不同，四角就会漏桌面。</para></summary>
    public static Geometry BuildRectMaskGeometry(Rect hole, double screenW, double screenH, double radiusDip = 0)
        => BuildMaskGeometry(SelectionEffects.BuildOutline(
            new RectangleF((float)hole.X, (float)hole.Y, (float)hole.Width, (float)hole.Height),
            (float)radiusDip), 0, 0, screenW, screenH);

    /// <summary>一枚超椭圆圆角矩形的<b>闭合轮廓</b>（点集同样出自 <see cref="SelectionEffects.BuildOutline"/>）：
    /// 供"卡片自己那圈长相"用——预览画面卡的底、画面裁剪、描边外框三件都从这里取，
    /// 于是卡片轮廓与 <see cref="BuildRectMaskGeometry"/> 挖出来的洞、与截图选框是**同一条曲线**。
    /// <para>🔴 为什么不能用 <c>Rectangle.RadiusX</c>／<c>RectangleGeometry</c>／<c>Border.CornerRadius</c>：
    /// 那三样只会画**正圆弧**，而本项目的圆角是连续曲率超椭圆（n=5，比圆弧更"方"）。沿 45° 对角线量，两道
    /// 弧的尖端离角点的距离分别是 <c>0.4142r</c> 与 <c>0.1831r</c> ⇒ <b>差恒为 0.2311×半径</b>（按本方法
    /// 实际采样步数现量：r=11 DIP 差 2.54、r=40 差 9.25、r=100 差 23.11）。同一个数并排摆出来，肉眼读到的
    /// 是"两套弧度"（2026-09-24 用户实报：「遮罩是跟随截图选区了，但是预览的那个描边没有跟随截图选区圆角吗」
    /// ——病根正是遮罩这侧已换成超椭圆、卡片那侧还是正圆弧）。</para>
    /// <paramref name="radiusDip"/> ≤ 0.5 时退化成四点矩形，与遮罩那侧同一判据。</summary>
    public static Geometry BuildRoundedOutline(Rect rect, double radiusDip)
    {
        var pts = SelectionEffects.BuildOutline(
            new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height),
            (float)radiusDip);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(new Point(pts[i].X, pts[i].Y));
            ctx.EndFigure(true);
        }
        return geo;
    }
}
