using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 画笔光栅化器：把笔迹的原始采样按笔刷画像"扫"成单通道覆盖率缓冲。
///
/// 落墨用**连续距离场**逐像素求值（路径加密后按段求点到线段的距离与段内插值半径），
/// 不是沿路径盖一个个圆印——圆印取并集会在外沿留下周期性起伏（细看就是"一个一个圆点"），
/// 距离场的包络在 max 合成下天然连续，结构上不会再出现那种起伏。
///
/// 预览（Avalonia）与成品（GDI+）两侧都只负责把同一份缓冲贴上去，不再各自用线条 API 画一遍——
/// 这是"截图时看到的"与"截图后的"必然一致的结构性保证，改任何笔刷参数都只需改这里。
/// </summary>
public static class BrushRasterizer
{
    /// <summary>
    /// 距离场渲染段长度的上限，相对基准笔宽。段是连续求值的，无需像旧盖章那样密；
    /// 加密路径与落墨骨架都以此为目标步长（骨架见 <c>BuildRenderSpine</c>）。
    /// </summary>
    private const float FieldStepRatio = 0.5f;

    /// <summary>
    /// 单个渲染段允许的最大转角（度），加密与归并共用这一门槛：急弯处软尾包络会鼓成"毛毛虫"，
    /// 把转角限住，弦对弧的矢高（≈ 段长 × 转角 / 8）就不会超出半像素观感。
    /// </summary>
    private const float MaxSegmentTurnDegrees = 12.0f;

    /// <summary>按转角细分的上限，防一次反向抖动把段数炸开。</summary>
    private const int MaxSegmentSubdivisions = 32;

    /// <summary>外沿抗锯齿带宽度（半像素），笔迹墨量在这一带内线性收到 0。</summary>
    private const float AntialiasPadPx = 0.5f;

    /// <summary>速度笔锋：瞬时速度向低通滤波靠拢的系数。越小越迟钝，越大越跟手。</summary>
    private const float VelocityEmaPrevious = 0.7f;

    /// <summary>速度归一化下限（物理像素/毫秒），慢于此速一律按最粗出墨。</summary>
    private const float SpeedSlowPxPerMs = 0.35f;

    /// <summary>速度归一化上限（物理像素/毫秒），快于此速一律按最细出墨。</summary>
    private const float SpeedFastPxPerMs = 3.5f;

    /// <summary>单个采样点允许的笔宽比例变化上限：不限速则一帧抖动就能在笔迹上咬出一个尖。</summary>
    private const float WidthRateLimitPerStep = 0.25f;

    /// <summary>收笔笔锋的弧长相对基准笔宽的倍数（强度 1 时收尖长度 = 本倍数 × 笔宽）。</summary>
    private const float TaperTailFactor = 3.0f;

    /// <summary>
    /// 笔尖支撑半径（墨水能到达的最远处，相对路径中心线）：半笔宽外扩半像素抗锯齿带。
    /// 包围盒、命中框与光栅化遍历范围都必须只由此处推出，不得各自换算。
    /// </summary>
    public static float GetSupportRadiusPx(float width, float hardness = 1.0f)
    {
        float radius = Math.Max(1.0f, width) * 0.5f;
        return radius + AntialiasPadPx;
    }

    /// <summary>栅格化一条笔迹。采样坐标为屏幕物理像素，返回的缓冲原点同样是物理像素。</summary>
    public static BrushCoverage Build(StrokeShape stroke)
    {
        IReadOnlyList<StrokePoint> samples = stroke.Samples;
        if (samples.Count == 0)
        {
            return new BrushCoverage(Array.Empty<byte>(), 0, 0, 0, 0, ComputeKey(stroke));
        }

        float baseThickness = Math.Max(1.0f, stroke.Thickness);

        // 先按笔刷画像算出每个采样的出墨宽度，再随路径一起加密插值
        float fieldStep = Math.Max(1.0f, baseThickness * FieldStepRatio);
        float[] sampleWidths = ComputeWidths(samples, baseThickness, stroke.Profile);
        List<PathPoint> path = BuildSmoothedPath(samples, sampleWidths, baseThickness, fieldStep);

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        for (int i = 0; i < path.Count; i++)
        {
            PathPoint p = path[i];
            float reach = GetSupportRadiusPx(p.Width);
            if (p.X - reach < minX) minX = p.X - reach;
            if (p.Y - reach < minY) minY = p.Y - reach;
            if (p.X + reach > maxX) maxX = p.X + reach;
            if (p.Y + reach > maxY) maxY = p.Y + reach;
        }

        int originX = (int)MathF.Floor(minX);
        int originY = (int)MathF.Floor(minY);
        int width = (int)MathF.Ceiling(maxX) - originX + 1;
        int height = (int)MathF.Ceiling(maxY) - originY + 1;

        var data = new byte[width * height];

        // 落墨只走归并后的骨架：加密路径是"算准形状"用的，逐点落墨会让同一批像素被相邻几段重复求值
        List<PathPoint> spine = BuildRenderSpine(path, fieldStep);

        bool dashed = stroke.LineStyle == AnnotationLineStyle.DashLarge;
        float dashOn = dashed
            ? Math.Max((float)AnnotationRenderOptions.DashLargeMinOnPx, baseThickness * (float)AnnotationRenderOptions.DashLargeOn)
            : 0.0f;
        float dashOff = dashed
            ? Math.Max((float)AnnotationRenderOptions.DashLargeMinOffPx, baseThickness * (float)AnnotationRenderOptions.DashLargeOff)
            : 0.0f;

        float travel = 0.0f;
        for (int i = 1; i < spine.Count; i++)
        {
            PathPoint from = spine[i - 1];
            PathPoint to = spine[i];
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float segment = (float)Math.Sqrt((double)dx * dx + (double)dy * dy);

            if (segment > 0.0f)
            {
                if (dashed) PaintDashedSegment(data, width, height, originX, originY, from, to, segment,
                    travel, dashOn, dashOff);
                else PaintSegmentField(data, width, height, originX, originY, from, to);

                travel += segment;
            }
        }

        // 只有一个采样（或两点重合）时没有任何段可扫，补一个退化段：单击落笔要留下一个实心圆斑
        if (travel <= 0.0f)
        {
            PathPoint dot = spine[spine.Count - 1];
            PaintSegmentField(data, width, height, originX, originY, dot, dot);
        }

        return new BrushCoverage(data, width, height, originX, originY, ComputeKey(stroke));
    }

    /// <summary>
    /// 虚线段：按弧长划段边界把一条渲染段切成"有墨"的子段再逐段求值。
    /// 划段与间隔的长度沿用 <see cref="AnnotationRenderOptions"/> 的共用口径（预览与成品同源）。
    /// </summary>
    private static void PaintDashedSegment(byte[] data, int width, int height, int originX, int originY,
        PathPoint from, PathPoint to, float segment, float travel, float dashOn, float dashOff)
    {
        float period = dashOn + dashOff;
        float segEnd = travel + segment;
        float cursor = travel;

        while (cursor < segEnd)
        {
            float phase = cursor % period;
            if (phase >= dashOn)
            {
                cursor += period - phase;          // 落在间隔里：跳到下一个划段起点
                continue;
            }

            float onEnd = Math.Min(segEnd, cursor + (dashOn - phase));
            PaintSegmentField(data, width, height, originX, originY,
                LerpPathPoint(from, to, (cursor - travel) / segment),
                LerpPathPoint(from, to, (onEnd - travel) / segment));

            cursor = onEnd >= segEnd ? segEnd : onEnd + dashOff;
        }
    }

    private static PathPoint LerpPathPoint(PathPoint a, PathPoint b, float t)
    {
        return new PathPoint(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Width + (b.Width - a.Width) * t);
    }

    /// <summary>
    /// 逐采样算出墨宽度（物理像素）。宽度只由"原始采样 + 笔刷画像"决定，
    /// 所以预览与成品、实时帧与最终提交帧都必然得到同一条笔迹的同一个宽度序列。
    /// 速度笔锋驱动：画得越快越细，慢速饱满；强度为 0 时全笔等宽。
    /// </summary>
    private static float[] ComputeWidths(IReadOnlyList<StrokePoint> samples, float baseThickness, BrushProfile profile)
    {
        var widths = new float[samples.Count];
        float minThickness = baseThickness * profile.MinWidthRatio;

        if (profile.TaperStrength <= 0.0f)
        {
            for (int i = 0; i < widths.Length; i++) widths[i] = baseThickness;
            return widths;
        }

        float initialVelocity = 0.0f;
        if (samples.Count > 1)
        {
            double dt0 = samples[1].TimestampMs - samples[0].TimestampMs;
            if (dt0 > 0.0)
            {
                float dx0 = samples[1].X - samples[0].X;
                float dy0 = samples[1].Y - samples[0].Y;
                initialVelocity = (float)(Math.Sqrt((double)dx0 * dx0 + (double)dy0 * dy0) / dt0);
            }
        }

        float velocity = initialVelocity;
        float speedSpan = SpeedFastPxPerMs - SpeedSlowPxPerMs;
        float initialFast = Math.Clamp((initialVelocity - SpeedSlowPxPerMs) / speedSpan, 0.0f, 1.0f);
        float previousRatio = 1.0f - initialFast;

        for (int i = 0; i < samples.Count; i++)
        {
            StrokePoint s = samples[i];

            // 速度笔锋：把移动速度换算成出墨量，慢=粗、快=细
            if (i > 0)
            {
                StrokePoint prev = samples[i - 1];
                double dt = s.TimestampMs - prev.TimestampMs;
                float instant = dt > 0.0
                    ? (float)(Math.Sqrt((double)(s.X - prev.X) * (s.X - prev.X) + (double)(s.Y - prev.Y) * (s.Y - prev.Y)) / dt)
                    : velocity;
                velocity = VelocityEmaPrevious * velocity + (1.0f - VelocityEmaPrevious) * instant;
            }

            float fast = Math.Clamp((velocity - SpeedSlowPxPerMs) / speedSpan, 0.0f, 1.0f);
            float ratio = 1.0f - fast;

            // 限速：单个采样点不许让笔宽从最粗直接跳到最细，否则一帧抖动就会咬出一个尖
            ratio = Math.Min(ratio, previousRatio + WidthRateLimitPerStep);

            ratio = 1.0f - profile.TaperStrength * (1.0f - ratio);
            widths[i] = minThickness + (baseThickness - minThickness) * ratio;
            previousRatio = ratio;
        }

        ApplyEndTaper(samples, widths, baseThickness, profile.TaperStrength);
        return widths;
    }

    /// <summary>
    /// 收笔笔锋：以收笔点为笔尖，按"距收笔点的弧长"把宽度从满笔宽线性收到 0，甩出去就是一个尖。
    /// 收尖长度按笔锋强度与基准笔宽缩放，强度 0 时不产生笔尖；且收尖长度不超过总笔长的 40%，防止短笔画整笔被缩成圆点。
    /// </summary>
    private static void ApplyEndTaper(IReadOnlyList<StrokePoint> samples, float[] widths, float baseThickness, float taperStrength)
    {
        int count = samples.Count;
        if (count < 2 || taperStrength <= 0.0f) return;

        float totalLength = 0.0f;
        for (int i = 1; i < count; i++)
        {
            float segX = samples[i].X - samples[i - 1].X;
            float segY = samples[i].Y - samples[i - 1].Y;
            totalLength += MathF.Sqrt(segX * segX + segY * segY);
        }

        float tailLength = Math.Min(totalLength * 0.45f, taperStrength * TaperTailFactor * Math.Max(1.0f, baseThickness));
        if (tailLength <= 0.0f) return;

        float fromTail = 0.0f;
        for (int i = count - 1; i >= 0; i--)
        {
            widths[i] *= Math.Clamp(fromTail / tailLength, 0.0f, 1.0f);

            if (i > 0)
            {
                StrokePoint from = samples[i - 1];
                StrokePoint to = samples[i];
                fromTail += (float)Math.Sqrt((double)(to.X - from.X) * (to.X - from.X) + (double)(to.Y - from.Y) * (to.Y - from.Y));
            }
        }
    }

    /// <summary>
    /// 画笔的最终不透明度：笔刷画像的透明度再乘标注颜色自带的 Alpha。两侧共用这一口径，
    /// 且只在贴图时乘一次，所以整条笔迹只有一层墨，自交处不会叠加变深。
    /// </summary>
    public static float GetInkOpacity(StrokeShape stroke)
        => Math.Clamp(stroke.Profile.Opacity * (stroke.Color.A / 255.0f), 0.0f, 1.0f);

    /// <summary>
    /// 烘图键：把决定贴图字节的颜色与不透明度压成一个 long，与 <see cref="ComputeKey"/> 得到的
    /// 几何指纹拼在一起，就是各侧位图视图的缓存键（改颜色/改透明度只换视图，不重栅格化）。
    /// </summary>
    public static long BakeKey(Color color, float opacity)
    {
        ulong argb = (uint)color.ToArgb();
        ulong alpha = (uint)BitConverter.SingleToInt32Bits(Math.Clamp(opacity, 0.0f, 1.0f));
        return (long)((argb << 32) | (alpha & 0xFFFF_FFFFUL));
    }

    /// <summary>
    /// 把覆盖率缓冲连同颜色与不透明度一次性烘成预乘 BGRA 位图（GDI+ 的 Format32bppPArgb 预乘像素格式）。
    /// 必须使用 Format32bppPArgb：若设为 Format32bppArgb，在 Avalonia (ToAvaloniaBitmap) 与 GDI+ 合成时
    /// 会把预乘数据当成直通直彩再次乘以 Alpha，产生二次预乘（Double Premul）导致边缘抗锯齿暗边/黑脏边。
    ///
    /// 这是全套画笔渲染里唯一一处做"覆盖率 → 像素"换算的地方：成品侧 1:1 直接贴它，
    /// 预览侧把它零拷贝转成 WriteableBitmap 后绘制。两侧因此从同一批字节出发，
    /// 不可能分叉出"截图时看到的"与"截图后的"不一致。
    /// </summary>
    /// <returns>位图左上角对应 (<see cref="BrushCoverage.OriginX"/>, <see cref="BrushCoverage.OriginY"/>) 屏幕物理像素；无墨时返回 null。</returns>
    public static unsafe Bitmap? BakeBitmap(BrushCoverage coverage, Color color, float opacity)
    {
        int alphaScale = (int)(Math.Clamp(opacity, 0.0f, 1.0f) * 255.0f + 0.5f);
        if (coverage.Width <= 0 || coverage.Height <= 0 || alphaScale <= 0) return null;

        var palette = new byte[256 * 4];
        var alphaLut = new byte[256];
        BuildPalette(color, alphaScale, palette, alphaLut);

        try
        {
            var layer = new Bitmap(coverage.Width, coverage.Height, PixelFormat.Format32bppPArgb);
            BitmapData data = layer.LockBits(
                new Rectangle(0, 0, coverage.Width, coverage.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                byte* dst0 = (byte*)data.Scan0;
                fixed (byte* pCov = coverage.Data)
                fixed (byte* pAlpha = alphaLut)
                fixed (byte* pPal = palette)
                {
                    for (int y = 0; y < coverage.Height; y++)
                    {
                        byte* dst = dst0 + (long)y * data.Stride;
                        byte* cov = pCov + (long)y * coverage.Width;
                        for (int x = 0; x < coverage.Width; x++)
                        {
                            *(int*)dst = *(int*)(pPal + pAlpha[*cov] * 4);
                            dst += 4;
                            cov++;
                        }
                    }
                }
            }
            finally
            {
                layer.UnlockBits(data);
            }

            return layer;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("BrushRasterizer.BakeBitmap 失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 生成两张换算表：覆盖率 → 最终 alpha、alpha → 预乘 BGRA 四字节。
    /// 逐像素因此只剩一次查表，且整条笔迹的透明度只在这两张表里乘一次。
    /// </summary>
    private static void BuildPalette(Color color, int alphaScale, byte[] palette, byte[] alphaLut)
    {
        for (int a = 0; a < 256; a++)
        {
            int offset = a * 4;
            palette[offset] = (byte)((color.B * a + 127) / 255);
            palette[offset + 1] = (byte)((color.G * a + 127) / 255);
            palette[offset + 2] = (byte)((color.R * a + 127) / 255);
            palette[offset + 3] = (byte)a;
        }

        for (int v = 0; v < 256; v++)
        {
            alphaLut[v] = (byte)((v * alphaScale + 127) / 255);
        }
    }

    /// <summary>
    /// 缓存键：覆盖笔迹几何与全部笔刷参数，但刻意不含 <see cref="StrokeShape.Color"/>——
    /// 颜色只参与贴图那一乘，改颜色不必重新栅格化。
    /// </summary>
    public static long ComputeKey(StrokeShape stroke)
    {
        const ulong OffsetBasis = 14695981039346656037;

        ulong hash = OffsetBasis;
        hash = Mix(hash, (ulong)(uint)stroke.Samples.Count);
        hash = Mix(hash, Bits(stroke.Thickness));
        hash = Mix(hash, (ulong)(uint)stroke.LineStyle);

        foreach (StrokePoint s in stroke.Samples)
        {
            hash = Mix(hash, Bits(s.X));
            hash = Mix(hash, Bits(s.Y));
            hash = Mix(hash, Bits(s.Pressure));
            hash = Mix(hash, (ulong)s.TimestampMs);
        }

        BrushProfile p = stroke.Profile;
        hash = Mix(hash, Bits(p.Opacity));
        hash = Mix(hash, Bits(p.Hardness));
        hash = Mix(hash, (ulong)(uint)p.TaperMode);
        hash = Mix(hash, Bits(p.TaperStrength));
        hash = Mix(hash, Bits(p.Smoothing));
        hash = Mix(hash, Bits(p.MinWidthRatio));

        return (long)hash;
    }

    /// <summary>
    /// 连续距离场落墨：对一段"两端各带笔宽"的笔迹，逐像素求它到该段的距离与该处的笔尖半径，
    /// 由胶囊体紧凑 AABB 逐像素求距离与外沿抗锯齿得到墨量，再与已有墨量取大（合成律不变，整笔只有一层墨）。
    /// 相邻段的包络在 max 下天然连续，彻底根除端点球形鼓包与段身掏空导致的"糖葫芦"圆点粗糙感。
    /// </summary>
    private static unsafe void PaintSegmentField(byte[] data, int width, int height, int originX, int originY,
        PathPoint a, PathPoint b, float hardness = 1.0f)
    {
        float ax = a.X - originX;
        float ay = a.Y - originY;
        float bx = b.X - originX;
        float by = b.Y - originY;

        float radiusA = a.Width * 0.5f;
        float radiusB = b.Width * 0.5f;
        float reachA = radiusA + AntialiasPadPx;
        float reachB = radiusB + AntialiasPadPx;
        float reachMax = Math.Max(reachA, reachB);
        if (reachMax <= 0.0f) return;

        float vx = bx - ax;
        float vy = by - ay;
        float len2 = vx * vx + vy * vy;
        float invLen2 = len2 > 1e-6f ? 1.0f / len2 : 0.0f;
        float deltaRadius = radiusB - radiusA;

        float minX = Math.Min(ax - reachA, bx - reachB);
        float maxX = Math.Max(ax + reachA, bx + reachB);
        float minY = Math.Min(ay - reachA, by - reachB);
        float maxY = Math.Max(ay + reachA, by + reachB);

        int y0 = Math.Max(0, (int)MathF.Floor(minY));
        int y1 = Math.Min(height - 1, (int)MathF.Ceiling(maxY));
        int x0 = Math.Max(0, (int)MathF.Floor(minX));
        int x1 = Math.Min(width - 1, (int)MathF.Ceiling(maxX));
        if (y0 > y1 || x0 > x1) return;

        fixed (byte* p = data)
        {
            for (int py = y0; py <= y1; py++)
            {
                float rowY = py + 0.5f;
                float dy = rowY - ay;
                float dotY = dy * vy;
                byte* row = p + (long)py * width;

                for (int px = x0; px <= x1; px++)
                {
                    float dx = px + 0.5f - ax;
                    float t = invLen2 > 0.0f ? Math.Clamp((dx * vx + dotY) * invLen2, 0.0f, 1.0f) : 0.0f;
                    float ex = dx - vx * t;
                    float ey = dy - vy * t;
                    float dist2 = ex * ex + ey * ey;

                    float radius = radiusA + deltaRadius * t;
                    float reach = radius + AntialiasPadPx;
                    if (dist2 >= reach * reach) continue;

                    float dist = MathF.Sqrt(dist2);
                    float edge = reach - dist; // 0 ~ 1.0 (2 * AntialiasPadPx)
                    float alpha = edge >= 1.0f ? 1.0f : (edge <= 0.0f ? 0.0f : edge);

                    byte value = (byte)(alpha * 255.0f);
                    if (value > row[px]) row[px] = value;
                }
            }
        }
    }

    /// <summary>
    /// 用 Catmull-Rom 把采样序列加密成折线。端点按重端点外推，两点笔迹因此退化成直线。
    /// 细分判据有两个：弧长（<paramref name="targetStep"/>）与转角。距离场是整段整段求值的，
    /// 急弯处一段的转角会在软尾外沿留下 ≈ r·θ²/8 的"毛毛虫"起伏，只按弧长加密压不住它。
    /// </summary>
    private static List<PathPoint> BuildSmoothedPath(
        IReadOnlyList<StrokePoint> samples, float[] sampleWidths, float baseThickness, float targetStep)
    {
        var path = new List<PathPoint>(samples.Count * 2)
        {
            new PathPoint(samples[0].X, samples[0].Y, sampleWidths[0])
        };

        for (int i = 0; i < samples.Count - 1; i++)
        {
            int i0 = ClampedIndex(samples, i - 1);
            int i1 = ClampedIndex(samples, i);
            int i2 = ClampedIndex(samples, i + 1);
            int i3 = ClampedIndex(samples, i + 2);
            StrokePoint s0 = samples[i0];
            StrokePoint s1 = samples[i1];
            StrokePoint s2 = samples[i2];
            StrokePoint s3 = samples[i3];

            float dx = s2.X - s1.X;
            float dy = s2.Y - s1.Y;
            float segment = (float)Math.Sqrt((double)dx * dx + (double)dy * dy);
            int steps = Math.Max(1, (int)MathF.Ceiling(segment / Math.Max(0.5f, targetStep)));

            // 转角判据与弧长无关：快画的大弧每段弧长可能已经够短，但转角照样咬出棱
            float turn = TurnDegrees(s2.X - s0.X, s2.Y - s0.Y, s3.X - s1.X, s3.Y - s1.Y);
            if (turn > MaxSegmentTurnDegrees)
            {
                int byAngle = (int)MathF.Ceiling(turn / MaxSegmentTurnDegrees);
                steps = Math.Max(steps, Math.Min(MaxSegmentSubdivisions, byAngle));
            }

            for (int k = 1; k <= steps; k++)
            {
                float t = k / (float)steps;
                float t2 = t * t;
                float t3 = t2 * t;
                path.Add(new PathPoint(
                    Evaluate(s0.X, s1.X, s2.X, s3.X, t, t2, t3),
                    Evaluate(s0.Y, s1.Y, s2.Y, s3.Y, t, t2, t3),
                    // 宽度与坐标共用同一组基函数：笔锋的收尖才会顺着曲线渐变而不是在采样点之间台阶跳变
                    Math.Clamp(Evaluate(sampleWidths[i0], sampleWidths[i1], sampleWidths[i2], sampleWidths[i3], t, t2, t3),
                        0.0f, baseThickness)));
            }
        }

        return path;
    }

    /// <summary>
    /// 把加密路径归并成落墨骨架：骨架点是加密路径的子集，段更长但形状误差仍由同一个转角门槛约束
    /// （弦对弧的矢高 ≈ 段长 × 转角 / 8）。
    ///
    /// 这一步只为成本：原始采样比目标步长还密时，逐点落墨会让同一片像素被相邻几段重复求值，
    /// 粗笔长轨迹的耗时按"采样数 × 笔尖面积"涨，归并后只与"轨迹长度 × 笔尖面积"成正比。
    /// 合成律仍是逐段取大，所以归并不改变墨量，只改变求值次数。
    /// </summary>
    private static List<PathPoint> BuildRenderSpine(List<PathPoint> path, float maxStep)
    {
        var spine = new List<PathPoint>(path.Count) { path[0] };
        int anchor = 0;

        for (int i = 1; i < path.Count; i++)
        {
            PathPoint cur = path[i];
            if (i == path.Count - 1)
            {
                spine.Add(cur);          // 收笔点必须留在骨架上，否则笔锋的尖端会被截短
                break;
            }

            PathPoint a = path[anchor];
            PathPoint prev = path[i - 1];
            if (i > anchor + 1
                && TurnDegrees(prev.X - a.X, prev.Y - a.Y, cur.X - prev.X, cur.Y - prev.Y) > MaxSegmentTurnDegrees)
            {
                spine.Add(prev);
                anchor = i - 1;
                i = anchor;              // 当前点留到下一轮与新锚点一起判
                continue;
            }

            float dx = cur.X - a.X;
            float dy = cur.Y - a.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) >= maxStep)
            {
                spine.Add(cur);
                anchor = i;
            }
        }

        return spine;
    }

    /// <summary>两条向量的夹角（度）。任一向量退化成长度 0 时返回 0，防 NaN 传进步数与归并判定。</summary>
    private static float TurnDegrees(float ax, float ay, float bx, float by)
    {
        double lenA = Math.Sqrt((double)ax * ax + (double)ay * ay);
        double lenB = Math.Sqrt((double)bx * bx + (double)by * by);
        if (lenA < 1e-6 || lenB < 1e-6) return 0.0f;

        double cos = ((double)ax * bx + (double)ay * by) / (lenA * lenB);
        return (float)(Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI);
    }

    /// <summary>标准 Catmull-Rom（均匀参数、张力 0.5）单轴插值。</summary>
    private static float Evaluate(float p0, float p1, float p2, float p3, float t, float t2, float t3)
    {
        return 0.5f * (2.0f * p1
            + (-p0 + p2) * t
            + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2
            + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);
    }

    private static int ClampedIndex(IReadOnlyList<StrokePoint> samples, int index)
    {
        if (index < 0) return 0;
        return index >= samples.Count ? samples.Count - 1 : index;
    }

    /// <summary>加密后路径上的一点：坐标与出墨宽度都是屏幕物理像素。</summary>
    private readonly struct PathPoint
    {
        public PathPoint(float x, float y, float width)
        {
            X = x;
            Y = y;
            Width = width;
        }

        public float X { get; }

        public float Y { get; }

        public float Width { get; }
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211;
        return hash;
    }

    private static ulong Bits(float value) => (ulong)(uint)BitConverter.SingleToInt32Bits(value);
}
