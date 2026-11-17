using System;
using System.Collections.Generic;
using System.Drawing;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 文字块背景类别。判定不确定时一律落到更保守的一侧（<see cref="Complex"/>）：
/// 判错成 Complex 的代价是「多一块承托板」，判错成 Simple 的代价是「编出一片假背景」，后者不可接受。
/// </summary>
public enum BackgroundKind { Solid, Gradient, Texture, Complex }

/// <summary>背景判据。只含数字，可安全写日志（不含任何文字内容）。见 <see cref="ImageBackgroundModel.Analyze"/>。</summary>
public readonly record struct BackgroundMetrics(int FlatResidual, int FlatP95, int PlaneResidual, int PlaneP95,
    int Slope, int PeriodicPercent, int PeriodicAmplitude, int Period, int RingForeignPercent, double InkFraction);

/// <summary>一个文字块的背景画像：类别、取样矩形、前景／背景色、笔画掩膜与判据。</summary>
public sealed class BackgroundProfile
{
    /// <summary>取样与工作矩形（＝原文字框外扩一圈，已裁到图内）。<see cref="Mask"/> 按它逐行展开。</summary>
    public required Rectangle Region { get; init; }
    public required BackgroundKind Kind { get; init; }
    public required Color Base { get; init; }
    public required Color Ink { get; init; }
    /// <summary>笔画掩膜（255＝需抹除），长度＝Region.Width×Region.Height，已含一圈羽化外扩。</summary>
    public required byte[] Mask { get; init; }
    public required BackgroundMetrics Metrics { get; init; }
    /// <summary>掩膜与背景是否可信；不可信时调用方不得抹字。</summary>
    public required bool Trusted { get; init; }
    /// <summary>
    /// 取样环平面拟合系数，按环内局部坐标展开为三组 <c>c＋a·x＋b·y</c>（长度 9，R/G/B 依次排列）；仅渐变携带，其余为 null。
    /// 渐变上「干净像素」的参照色必须逐点算：整环中位色与环边缘本就差出几十级，
    /// 拿它当参照会把紧挨笔画的真背景判成脏，笔画一粗就整块补不出而白白跳过。
    /// </summary>
    public double[]? Plane { get; init; }

    public string Describe() =>
        $"类别={Kind} 平坦={Metrics.FlatResidual} 平坦P95={Metrics.FlatP95} 斜面残差={Metrics.PlaneResidual}" +
        $"/{Metrics.PlaneP95} 斜率={Metrics.Slope} 周期={Metrics.Period}({Metrics.PeriodicPercent}%×{Metrics.PeriodicAmplitude}) " +
        $"环外墨={Metrics.RingForeignPercent}% 墨占比={Metrics.InkFraction:F2} 可信={Trusted}";
}

/// <summary>
/// 文字块背景判定与补图。批 1 交付判定器与手段甲（法向插值）；批 2 交付手段丙的配色（<see cref="PanelColors"/>），
/// 板几何与落笔在 <c>LocalImageTranslation</c> 的档④。自相似块拷贝（手段乙，Texture）属批 3，
/// 在此之前 Texture 与 Complex 都不抹字，一律由承托板就地盖住。
/// </summary>
public static class ImageBackgroundModel
{
    /// <summary>
    /// 判定阈值。每个数字都写定于 <c>SettingsThemeProbe --image-calibrate</c> 在 600 张真值已知合成图上的
    /// 实测分布（证据 <c>log/image-repair/calibration.json</c>，2026-10-27 那一次），本类之外的任何位置不得再出现同类数字。
    /// 换语料或改算法都要重跑标定并同步这里的注释读数。
    /// </summary>
    public static class Thresholds
    {
        /// <summary>环上像素对中位背景的最大可接受离散度，不超过即判纯色。纯色实测恒为 1。</summary>
        public const int SolidResidualMax = 3;
        /// <summary>
        /// 面拟合残差 P95 上限，不超过才敢按渐变补（中位残差不必单设门槛：P95 压得住，中位自然压得更低）。
        /// 渐变实测恒 0（它的真身就是这张面），复杂背景最低 18、纹理最低 27——所以这条判据读的是「环上有没有半个区域根本不贴面」。
        /// 取 8 而不是贴着 17：与纯色分支 <see cref="FlatP95Max"/> 同一口径，给真实图里的压缩噪声留一点余量，
        /// 宁可在照片渐变上判成复杂（跳过、等承托板）也不在条纹上判成渐变（编出假背景）。
        /// </summary>
        public const int PlaneP95Max = 8;
        /// <summary>残差 P95 上限：中位达标但长尾过大也不算纯色。纯色实测 max=1，渐变 max=14，纹理 min=35。</summary>
        public const int FlatP95Max = 8;
        /// <summary>
        /// 取样环上「不属于本块背景」的实墨占比上限（百分制）。少量邻居笔画／图标被剔出统计后背景仍算干净，
        /// 占比一大就说明这块环本身是双峰的（条纹、拼接块、压在另一块内容上），不许按纯色或渐变补。
        /// 25 是 2026-10-27 标定的实测分离点：语料里 100 张「旁边就有一根黑条」的纯色／渐变样本占比 8～22
        /// （不掺邻墨的 200 张恒为 0），被这条闸门挡下的纹理样本最低 26（中位 48）。即最多容忍环上四分之一是别人的内容。
        /// </summary>
        public const int RingForeignPercentMax = 25;
        /// <summary>列／行均值剖面去趋势后自相关峰强度（百分制）达到此值才可能判重复纹理。纯色实测 max=53，纹理 max=90。</summary>
        public const int PeriodicMinPercent = 55;
        /// <summary>
        /// 周期起伏的幅度下限（亮度级）。自相关只量「像不像重复」，量不出重复得看得见的程度：
        /// 渐变的量化台阶也是周期信号（幅度约 1），与纯色抖动同量级，只有幅度够大才真是纹理。
        /// </summary>
        public const int PeriodicAmplitudeMin = 6;
        public const int PeriodMin = 2;
        public const int PeriodMax = 24;
        /// <summary>背景均匀到何种程度，才允许启用低对比退路重扫笔画。纯色实测 max=1，渐变 p50=4。</summary>
        public const int UniformSpreadMax = 4;
        /// <summary>笔画最少像素数，低于此值视为没找到文字。</summary>
        public const int InkPixelsMin = 3;
        /// <summary>
        /// 墨像素占原框面积比上限，超过视为「被抹掉的不只是笔画」。
        /// 纯色／渐变实测 max=49%；条纹背景会被自己的另一条带当成墨而涨到 61% 以上（p50=76%）——
        /// 这条专补取样环的几何盲区：又宽又矮的框压在横条纹上时，环的上下两条长边可能整条都落在同一条带里，
        /// 环读起来平坦、周期也数不出一个完整波，只有墨占比会暴露真相（2026-10-27 标定实测 4 张漏网样本 61～66%）。
        /// </summary>
        public const double InkFractionMax = 0.55;
        /// <summary>对比阈值＝背景离散度×倍率，钳在 [Min,Max]。沿用改前口径，不重新发明。</summary>
        public const int ContrastMultiplier = 2;
        public const int ContrastMin = 22;
        public const int ContrastMax = 90;
        /// <summary>低对比退路使用的固定阈值。沿用改前口径。</summary>
        public const int LowContrast = 8;
        /// <summary>取样环外扩量（像素）。</summary>
        public const int RingPadding = 2;
        /// <summary>插值时纵向搜索的最大行数。纯色／渐变在该半径内实测补图最差通道误差 3／255（2026-10-27 标定整组 300 张上界）。</summary>
        public const int SearchReach = 24;
        /// <summary>
        /// 借用区内单像素与参照色的最大色差。4 卡在 2026-10-27 标定的实测漂移之上：环上真背景相对参照色的
        /// 长尾是纯色 P95 最大 2、渐变拟合面残差恒 0，再往外借到的仍是这条背景；笔画的对比度最低也有 22，
        /// 4 离两侧都还有一截，既不会把渐变边缘判脏、也不会把并排的字判成空白。
        /// 借不借的先过 <see cref="CanBorrowBlank"/> 的类别闸门，具体某一条带子干不干净由这里逐列判——
        /// 不再另设平坦度魔法数，多一个没标定的旋钮就多一处会说谎的余量。
        /// </summary>
        public const int BlankDriftMax = 4;
    }

    /// <summary>对单个文字块取样、建笔画掩膜并判类。多边形与逐字刀口为可选值，缺失时回退轴对齐原框。</summary>
    public static BackgroundProfile Analyze(int[] pixels, int width, int height, RectangleF text,
        PointF[]? polygon, float[]? charX)
    {
        var ring = Rectangle.Inflate(Rectangle.Ceiling(text), Thresholds.RingPadding, Thresholds.RingPadding);
        ring = Rectangle.Intersect(ring, new Rectangle(0, 0, width, height));
        if (ring.Width < 3 || ring.Height < 3) return Untrusted(ring);

        var border = CollectBorder(pixels, width, ring);
        if (border.Count < 8) return Untrusted(ring);

        var background = MedianColor(border);
        var deviation = new int[256];
        var deltas = new int[border.Count];
        for (int i = 0; i < border.Count; i++)
        {
            deltas[i] = Difference(border[i].Color, background);
            deviation[deltas[i]]++;
        }
        int flat = PercentileBucket(deviation, border.Count, 50);
        // 环上混进的邻居实墨（并排的另一行字、图标、分隔线）不是背景样本：先按与建掩膜同一档的对比阈值把它们剔出统计。
        // 不剔的话几根黑条就能把整环拟合成一张歪斜面，把干净的白底判成复杂背景而整块跳过（2026-10-27 自测第④组实测）。
        int inkThreshold = Math.Clamp(flat * Thresholds.ContrastMultiplier, Thresholds.ContrastMin, Thresholds.ContrastMax);
        var inlier = new List<BorderSample>(border.Count);
        var inlierDeviation = new int[256];
        int foreign = 0;
        for (int i = 0; i < border.Count; i++)
        {
            if (deltas[i] > inkThreshold) { foreign++; continue; }
            inlier.Add(border[i]);
            inlierDeviation[deltas[i]]++;
        }
        if (inlier.Count < 8) return Untrusted(ring);
        int ringForeignPercent = (int)Math.Round(foreign * 100.0 / border.Count);
        int flatP95 = PercentileBucket(inlierDeviation, inlier.Count, 95);

        var mask = BuildMask(pixels, width, ring, text, polygon, charX, background, flat, out int inkCount, out var ink);
        double inkFraction = text.Width * text.Height > 0 ? inkCount / (text.Width * text.Height) : 1;

        var (periodic, period, amplitude) = DetectPeriodicity(pixels, width, ring, mask);
        var (planeResidual, planeP95, slope, planeCoefficients) = FitPlane(inlier, ring);

        var kind = periodic >= Thresholds.PeriodicMinPercent && amplitude >= Thresholds.PeriodicAmplitudeMin
                && period >= Thresholds.PeriodMin && period <= Thresholds.PeriodMax
            ? BackgroundKind.Texture
            : ringForeignPercent > Thresholds.RingForeignPercentMax ? BackgroundKind.Complex
            : flat <= Thresholds.SolidResidualMax && flatP95 <= Thresholds.FlatP95Max ? BackgroundKind.Solid
            : planeP95 <= Thresholds.PlaneP95Max ? BackgroundKind.Gradient
            : BackgroundKind.Complex;

        return new BackgroundProfile
        {
            Region = ring,
            Kind = kind,
            Base = background,
            Ink = ink,
            Mask = mask,
            Plane = kind == BackgroundKind.Gradient ? planeCoefficients : null,
            Metrics = new BackgroundMetrics(flat, flatP95, planeResidual, planeP95, slope, periodic, amplitude,
                period, ringForeignPercent, inkFraction),
            Trusted = inkCount >= Thresholds.InkPixelsMin && inkFraction <= Thresholds.InkFractionMax,
        };
    }

    /// <summary>
    /// 手段甲·法向插值：掩膜像素按「同列上下最近的干净像素」反比插值填掉，纵向够不到再退同列左右，
    /// 都够不到才用背景中位色兜。笔画是细线，其下方像素由紧邻直接决定，所以渐变与阴影带能被连续接上，
    /// 不像各向同性扩散那样把渐变糊成斑；纯色时该算法自然退化为填背景色，故不再单设纯色路径。
    /// 返回 false 表示本块补不出可信结果，调用方须降级到下一手段。
    /// </summary>
    public static bool Repair(int[] pixels, int width, int height, BackgroundProfile profile)
    {
        if (!profile.Trusted || profile.Mask.Length == 0) return false;
        // 手段甲只服务纯色与渐变。纹理／复杂背景上「最近的干净像素」属于另一个相位或另一块内容，
        // 反比插值会把条纹接错位、把噪声抹成色斑（2026-10-27 标定实测最差通道误差最高到 195／255），
        // 这两类一律不抹字，由调用方的档④承托板就地盖住；手段乙（自相似块拷贝）要等批 3。
        if (profile.Kind is BackgroundKind.Texture or BackgroundKind.Complex) return false;
        var r = profile.Region;
        int failed = 0, filled = 0;
        // 先确认整块都能补，再提交像素；失败后调用方还要尝试承托板，不能留下半块擦除的原文。
        var pending = new List<(int Index, int Pixel)>();

        for (int y = 0; y < r.Height; y++)
        {
            int row = r.Y + y, column = r.X;
            for (int x = 0; x < r.Width; x++, column++)
            {
                int maskIndex = y * r.Width + x;
                if (profile.Mask[maskIndex] == 0) continue;
                int target = row * width + column;
                // 半透明与透明像素不参与抹除与补图（保持原图 Alpha 声明）。
                if ((uint)pixels[target] >> 24 != 255) continue;

                Color? value = Interpolate(pixels, width, height, profile, column, row, r, true)
                    ?? Interpolate(pixels, width, height, profile, column, row, r, false);
                if (value == null)
                {
                    // 纵向横向都够不到干净像素，只有背景确实均匀时才敢用中位色填。
                    if (profile.Kind != BackgroundKind.Solid) { failed++; continue; }
                    value = profile.Base;
                }
                pending.Add((target, unchecked((int)0xff000000) | (value.Value.ToArgb() & 0x00ffffff)));
                filled++;
            }
        }
        if (filled == 0 || failed != 0) return false;
        foreach (var (index, pixel) in pending) pixels[index] = pixel;
        return true;
    }

    /// <summary>
    /// 本块背景是否允许向外借空白：只认手段甲也认的那两类（纯色／渐变），且掩膜必须可信。
    /// 不再单设平坦度阈值——条纹背景的环上中位色差可以到 0（一条带子内部确实平），判不出差别；
    /// 类别闸门复用标定好的判类结果，具体某一条带子干不干净交给 <see cref="IsBlankStrip"/> 逐列验。
    /// </summary>
    public static bool CanBorrowBlank(BackgroundProfile profile) =>
        profile.Trusted && profile.Kind is BackgroundKind.Solid or BackgroundKind.Gradient;

    /// <summary>
    /// 手段丙·承托板的配色。板底取本块自己的背景（纯色／纹理／复杂用环中位色，渐变按拟合面在板心逐点取），
    /// 板子贴回原处因此不像多摆了一块东西，也不用先做一次邻域卷积。
    /// 字色优先沿用实测笔画色，条件是它与板底的通道差不低于 <see cref="Thresholds.ContrastMin"/>——
    /// 也就是"译文至少要和原文一样看得见"；够不到（低对比退路重扫出的笔画可以只差 8）才退到黑／白中更远的一侧，
    /// 那一侧的通道差恒不小于 128，是这一档唯一需要的兜底，不再另设阈值。
    /// 返回的对比度就是这一对颜色的实测通道差：落笔日志与探针直接引用它，调用方不得再自行算一遍色差。
    /// </summary>
    public static (Color Back, Color Text, int Contrast) PanelColors(BackgroundProfile profile, float x, float y)
    {
        var back = ReferenceColor(profile, profile.Region, (int)Math.Round(x), (int)Math.Round(y));
        int inkContrast = Difference(profile.Ink, back);
        if (inkContrast >= Thresholds.ContrastMin) return (back, profile.Ink, inkContrast);
        var text = Difference(Color.Black, back) >= Difference(Color.White, back) ? Color.Black : Color.White;
        return (back, text, Difference(text, back));
    }

    /// <summary>
    /// 借用区干净判据：一条 1px 宽的横带／竖带是否整条都是本块背景。
    /// 干净＝不透明且与该位置的参照色相差不超过 <see cref="Thresholds.BlankDriftMax"/>。
    /// 参照色与抹字补图用同一份（渐变按拟合面逐点算），否则渐变上借空白会拿整环中位色去比，
    /// 后半段必然超差、借不出去；补图却按拟合面判干净——同一块背景两套标准互相打脸。
    /// 只判读、不改像素，扩框的方向与步进归调用方，避免出现第二份取色与色差实现。
    /// </summary>
    public static bool IsBlankStrip(int[] pixels, int width, int height, BackgroundProfile profile, bool vertical,
        int fixedCoordinate, int from, int to)
    {
        int lo = Math.Max(0, from), hi = Math.Min(vertical ? height : width, to);
        for (int i = lo; i < hi; i++)
        {
            int x = vertical ? fixedCoordinate : i, y = vertical ? i : fixedCoordinate;
            if (x < 0 || x >= width || y < 0 || y >= height) return false;
            var color = Color.FromArgb(pixels[y * width + x]);
            if (color.A != 255 || Difference(color, ReferenceColor(profile, profile.Region, x, y)) > Thresholds.BlankDriftMax)
                return false;
        }
        return true;
    }

    /// <summary>沿单轴两侧找最近的干净像素并反比混合；<paramref name="vertical"/> 为 false 时沿横轴。</summary>
    private static Color? Interpolate(int[] pixels, int width, int height, BackgroundProfile profile,
        int column, int row, Rectangle r, bool vertical)
    {
        int origin = vertical ? row : column;
        int near = Nearest(pixels, width, height, profile, column, row, r, vertical, -1);
        int far = Nearest(pixels, width, height, profile, column, row, r, vertical, 1);
        if (near < 0 && far < 0) return null;
        Func<int, Color> at = vertical
            ? y => Color.FromArgb(pixels[y * width + column])
            : x => Color.FromArgb(pixels[row * width + x]);
        if (near < 0) return at(far);
        if (far < 0) return at(near);
        return Blend(at(near), at(far), origin - near, far - origin);
    }

    /// <summary>
    /// 从 (column,row) 沿轴向单侧找第一个可信背景像素，返回其该轴坐标，找不到返回 -1。
    /// 可信＝不透明、不在掩膜内、与该位置的参照色相差不超过本块允许的漂移量。
    /// 参照色：渐变按拟合面逐点算，其余用整环中位色。
    /// 吃不下这条判据的像素一律跳过而不是中止——它多半是并排的图标、分隔线或别人的笔画，
    /// 它另一侧仍是本块的背景；中止会让紧邻一根黑条的字整块补不出（2026-10-27 标定实测这样丢掉 2/100 张掺邻墨样本）。
    /// 搜索半径 <see cref="Thresholds.SearchReach"/> 已经把「跳过别人的内容去远处取色」的射程封住。
    /// </summary>
    private static int Nearest(int[] pixels, int width, int height, BackgroundProfile profile, int column, int row,
        Rectangle r, bool vertical, int step)
    {
        int limit = vertical ? height : width;
        for (int d = 1; d <= Thresholds.SearchReach; d++)
        {
            int position = (vertical ? row : column) + step * d;
            if (position < 0 || position >= limit) return -1;
            int x = vertical ? column : position, y = vertical ? position : row;
            var color = Color.FromArgb(pixels[y * width + x]);
            if (color.A != 255) continue;
            int maskX = x - r.X, maskY = y - r.Y;
            bool inside = maskX >= 0 && maskY >= 0 && maskX < r.Width && maskY < r.Height;
            if (inside && profile.Mask[maskY * r.Width + maskX] != 0) continue;
            if (Difference(color, ReferenceColor(profile, r, x, y)) <= AllowedDrift(profile)) return position;
        }
        return -1;
    }

    /// <summary>
    /// 某个位置的背景参照色：渐变按拟合面逐点算（斜面本身有走向，拿整环中位色当参照会把渐变后半段全判成"不像背景"），
    /// 其余类别用整环中位色。<paramref name="r"/> 是取样环，拟合面的自变量是环内局部坐标，必须同样减掉原点。
    /// </summary>
    private static Color ReferenceColor(BackgroundProfile profile, Rectangle r, int x, int y)
    {
        if (profile.Kind != BackgroundKind.Gradient || profile.Plane == null) return profile.Base;
        var c = profile.Plane;
        int localX = x - r.X, localY = y - r.Y;
        return Color.FromArgb(
            (int)Math.Clamp(c[0] + c[1] * localX + c[2] * localY, 0, 255),
            (int)Math.Clamp(c[3] + c[4] * localX + c[5] * localY, 0, 255),
            (int)Math.Clamp(c[6] + c[7] * localX + c[8] * localY, 0, 255));
    }

    /// <summary>与参照色之间允许的最大偏差：纯色取环上离散度长尾，渐变取拟合面残差长尾，其余按离散度两倍。</summary>
    private static int AllowedDrift(BackgroundProfile profile) => profile.Kind switch
    {
        BackgroundKind.Solid => Math.Max(1, profile.Metrics.FlatP95),
        BackgroundKind.Gradient => Math.Max(2, profile.Metrics.PlaneP95),
        _ => Math.Max(2, profile.Metrics.FlatP95 * 2),
    };

    private static Color Blend(Color near, Color far, int nearDistance, int farDistance)
    {
        int total = Math.Max(1, nearDistance + farDistance);
        return Color.FromArgb(
            (far.R * nearDistance + near.R * farDistance) / total,
            (far.G * nearDistance + near.G * farDistance) / total,
            (far.B * nearDistance + near.B * farDistance) / total);
    }

    private static BackgroundProfile Untrusted(Rectangle ring) => new()
    {
        Region = ring,
        Kind = BackgroundKind.Complex,
        Base = Color.Empty,
        Ink = Color.Empty,
        Mask = Array.Empty<byte>(),
        Metrics = new BackgroundMetrics(255, 255, 255, 255, 0, 0, 0, 0, 100, 1),
        Trusted = false,
    };

    // ---------------- 取样与度量 ----------------

    /// <summary>取样环上的一个样本：图内绝对坐标与不透明颜色。位置必须随颜色一起带着，平面拟合才有自变量可谈。</summary>
    private readonly record struct BorderSample(int X, int Y, Color Color);

    private static List<BorderSample> CollectBorder(int[] pixels, int width, Rectangle ring)
    {
        var list = new List<BorderSample>(2 * (ring.Width + ring.Height));
        void Add(int x, int y)
        {
            var c = Color.FromArgb(pixels[y * width + x]);
            if (c.A == 255) list.Add(new BorderSample(x, y, c));
        }
        for (int x = 0; x < ring.Width; x++)
        {
            Add(ring.X + x, ring.Y);
            if (ring.Height > 1) Add(ring.X + x, ring.Bottom - 1);
        }
        for (int y = 1; y < ring.Height - 1; y++)
        {
            Add(ring.X, ring.Y + y);
            if (ring.Width > 1) Add(ring.Right - 1, ring.Y + y);
        }
        return list;
    }

    /// <summary>
    /// 列／行均值剖面的去趋势自相关，取最强峰所在那一轴作为周期强度（百分制）、周期长度与起伏幅度（亮度级）。
    /// 掩膜像素（文字本身）不参与统计，否则会把笔画的疏密当成背景纹理。
    /// </summary>
    private static (int Percent, int Period, int Amplitude) DetectPeriodicity(int[] pixels, int width, Rectangle r, byte[] mask)
    {
        var columns = new double[r.Width]; var columnCount = new int[r.Width];
        var rows = new double[r.Height]; var rowCount = new int[r.Height];
        for (int y = 0; y < r.Height; y++) for (int x = 0; x < r.Width; x++)
            {
                if (mask.Length == r.Width * r.Height && mask[y * r.Width + x] != 0) continue;
                double luma = Luma(Color.FromArgb(pixels[(r.Y + y) * width + r.X + x]));
                columns[x] += luma; columnCount[x]++;
                rows[y] += luma; rowCount[y]++;
            }
        for (int x = 0; x < columns.Length; x++) columns[x] = columnCount[x] > 0 ? columns[x] / columnCount[x] : -1;
        for (int y = 0; y < rows.Length; y++) rows[y] = rowCount[y] > 0 ? rows[y] / rowCount[y] : -1;
        var vertical = StrongestPeak(columns);
        var horizontal = StrongestPeak(rows);
        return vertical.Percent >= horizontal.Percent ? vertical : horizontal;
    }

    /// <summary>
    /// 先去线性趋势、再对残差做自相关。直接自相关量的是「平滑」而不是「重复」，
    /// 单调渐变去掉趋势后什么都不剩，才不会 150/150 被误判成周期纹理（2026-10-27 标定实测）。
    /// 幅度＝残差绝对值的中位数：量化台阶与真条纹同样重复，差别只在重复得看得见的程度。
    /// </summary>
    private static (int Percent, int Lag, int Amplitude) StrongestPeak(double[] signal)
    {
        int n = 0; double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (int i = 0; i < signal.Length; i++) if (signal[i] >= 0)
            { n++; sumX += i; sumY += signal[i]; sumXX += (double)i * i; sumXY += i * signal[i]; }
        if (n < 5) return (0, 0, 0);
        double determinant = n * sumXX - sumX * sumX;
        if (Math.Abs(determinant) < 1e-9) return (0, 0, 0);
        double slope = (n * sumXY - sumX * sumY) / determinant;
        double intercept = (sumY - slope * sumX) / n;

        var residual = new double[signal.Length];
        var magnitude = new double[n];
        double variance = 0;
        for (int i = 0, taken = 0; i < signal.Length; i++)
        {
            if (signal[i] < 0) continue;
            residual[i] = signal[i] - (intercept + slope * i);
            variance += residual[i] * residual[i];
            magnitude[taken++] = Math.Abs(residual[i]);
        }
        if (variance <= 1e-6) return (0, 0, 0);
        Array.Sort(magnitude);
        int amplitude = (int)Math.Round(magnitude[n / 2]);

        int best = 0, bestLag = 0; double bestValue = 0;
        int maxLag = Math.Min(Thresholds.PeriodMax, signal.Length / 2);
        for (int lag = Thresholds.PeriodMin; lag <= maxLag; lag++)
        {
            double sum = 0; int count = 0;
            for (int i = 0; i + lag < signal.Length; i++)
            {
                if (signal[i] < 0 || signal[i + lag] < 0) continue;
                sum += residual[i] * residual[i + lag]; count++;
            }
            if (count < 5) continue;
            double normalized = sum / variance;
            if (normalized > bestValue)
            {
                bestValue = normalized;
                best = (int)Math.Clamp(normalized * 100, 0, 100);
                bestLag = lag;
            }
        }
        return (best, bestLag, amplitude);
    }

    /// <summary>
    /// 对取样环做逐通道平面拟合 z＝c＋a·x＋b·y（3×3 法方程），坐标是环内局部值，与 <see cref="ReferenceColor"/> 同一套。
    /// 残差＝各像素三通道最大误差的中位数与 P95；斜率＝三通道 |a|＋|b| 的最大值。
    /// 环是矩形边框，x 与 y 不构成共线，平面项可解；退化时返回最大残差。
    /// </summary>
    private static (int Residual, int ResidualP95, int Slope, double[] Coefficients) FitPlane(
        List<BorderSample> border, Rectangle ring)
    {
        int n = border.Count;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double x = border[i].X - ring.X, y = border[i].Y - ring.Y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
        }
        var baseMatrix = new double[3, 3] { { n, sx, sy }, { sx, sxx, sxy }, { sy, sxy, syy } };
        var coefficients = new double[9];
        var errors = new int[n];
        int slope = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            var rhs = new double[3];
            for (int i = 0; i < n; i++)
            {
                double v = Channel(border[i].Color, channel);
                rhs[0] += v;
                rhs[1] += (border[i].X - ring.X) * v;
                rhs[2] += (border[i].Y - ring.Y) * v;
            }
            // 消元就地改系数矩阵，每个通道都要从原始矩阵重新拷贝一份。
            var m = new double[3, 4];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++) m[row, col] = baseMatrix[row, col];
                m[row, 3] = rhs[row];
            }
            if (!Solve3x3(m, out double c, out double a, out double b)) return (255, 255, 0, Array.Empty<double>());
            coefficients[channel * 3] = c; coefficients[channel * 3 + 1] = a; coefficients[channel * 3 + 2] = b;
            slope = Math.Max(slope, (int)Math.Clamp(Math.Abs(a) + Math.Abs(b), 0, 255));
            for (int i = 0; i < n; i++)
            {
                double fitted = c + a * (border[i].X - ring.X) + b * (border[i].Y - ring.Y);
                errors[i] = Math.Max(errors[i], (int)Math.Abs(Channel(border[i].Color, channel) - fitted));
            }
        }
        Array.Sort(errors);
        return (errors[n / 2], errors[Math.Min(n - 1, (int)Math.Ceiling(n * .95) - 1)], slope, coefficients);
    }

    private static double Channel(Color c, int index) => index == 0 ? c.R : index == 1 ? c.G : c.B;

    /// <summary>3×3 线性方程组列主元高斯消元；系数矩阵奇异时返回 false。</summary>
    private static bool Solve3x3(double[,] m, out double x0, out double x1, out double x2)
    {
        x0 = x1 = x2 = 0;
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 3; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-9) return false;
            if (pivot != col) for (int k = col; k < 4; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (int row = col + 1; row < 3; row++)
            {
                double factor = m[row, col] / m[col, col];
                for (int k = col; k < 4; k++) m[row, k] -= factor * m[col, k];
            }
        }
        x2 = m[2, 3] / m[2, 2];
        x1 = (m[1, 3] - m[1, 2] * x2) / m[1, 1];
        x0 = (m[0, 3] - m[0, 1] * x1 - m[0, 2] * x2) / m[0, 0];
        return true;
    }

    // ---------------- 笔画掩膜 ----------------

    private static byte[] BuildMask(int[] pixels, int width, Rectangle ring, RectangleF text, PointF[]? polygon,
        float[]? charX, Color background, int spread, out int inkCount, out Color ink)
    {
        int threshold = Math.Clamp(spread * Thresholds.ContrastMultiplier, Thresholds.ContrastMin, Thresholds.ContrastMax);
        inkCount = Scan(pixels, width, ring, text, polygon, charX, background, threshold, out var mask);
        // 均匀底上的低对比文字可能整体落在阈值内：仅在首次几乎没找到笔画且背景确实均匀时降一档重扫，
        // 仍以原框为界；纹理背景不走这条退路，免得把背景细节当成文字。
        if (inkCount < Thresholds.InkPixelsMin && spread <= Thresholds.UniformSpreadMax)
            inkCount = Scan(pixels, width, ring, text, polygon, charX, background, Thresholds.LowContrast, out mask);
        ink = inkCount > 0 ? StrokeColor(pixels, width, ring, mask, background) : background;
        return mask;
    }

    /// <summary>
    /// 笔画色＝掩膜像素里「离背景最远」那一截的逐通道中位。
    /// 掩膜含一圈羽化外扩，小字号下这些半覆盖像素反而占多数：对全体掩膜像素取中位，实测 14px 的 "Go" 只差背景 60／255，
    /// 而原文差 218——四个排版档都照这个 ink 重画译文，出来就是洗过一遍的灰字。
    /// 也不按通道各取极端分位：截图上的字多是 ClearType 次像素渲染，红蓝边缘各有各的极值，逐通道取极值会拼出
    /// 原图里并不存在的紫色（实测 #61286A）。所以按整像素的通道差定档，取最远的十分之一，再对这一截取中位。
    /// 定档用「通道差 0～255 分桶」两趟直方图：这一支每块都要走，排序版（预分配＝整环长度，外加 O(n log n)）
    /// 把 64 行成品图的单次回填从 70ms 顶到 126ms，几十 MB 临时对象全算在回填头上。
    /// 同一通道差整档一起收（一成是下界，不把同颜色的笔画按任意顺序切断），取出的中位比截断更稳。
    /// </summary>
    private static Color StrokeColor(int[] pixels, int width, Rectangle ring, byte[] mask, Color background)
    {
        int Distance(int x, int y)
        {
            int argb = pixels[(ring.Y + y) * width + ring.X + x];
            // 半透明像素不算字的颜色：Alpha<255 的通道值是预乘过的，离背景远近只代表边缘。
            return (uint)argb >> 24 != 255 ? 0 : Difference(Color.FromArgb(argb), background);
        }

        var byDistance = new int[256];
        int ranked = 0;
        for (int y = 0; y < ring.Height; y++) for (int x = 0; x < ring.Width; x++)
            {
                if (mask[y * ring.Width + x] == 0) continue;
                int step = Distance(x, y);
                if (step == 0) continue;
                byDistance[step]++; ranked++;
            }
        if (ranked == 0) return background;
        int keep = Math.Max(1, ranked / 10), cutoff = 1;
        for (int step = 255; step >= 1; step--)
            if ((keep -= byDistance[step]) <= 0) { cutoff = step; break; }

        var counts = new int[3 * 256];
        int taken = 0;
        for (int y = 0; y < ring.Height; y++) for (int x = 0; x < ring.Width; x++)
            {
                if (mask[y * ring.Width + x] == 0 || Distance(x, y) < cutoff) continue;
                var color = Color.FromArgb(pixels[(ring.Y + y) * width + ring.X + x]);
                counts[color.R]++; counts[256 + color.G]++; counts[512 + color.B]++; taken++;
            }
        return Color.FromArgb(PercentileAt(counts, 0, taken, 50), PercentileAt(counts, 256, taken, 50),
            PercentileAt(counts, 512, taken, 50));
    }

    private static int Scan(int[] pixels, int width, Rectangle ring, RectangleF text, PointF[]? polygon, float[]? charX,
        Color background, int threshold, out byte[] mask)
    {
        mask = new byte[ring.Width * ring.Height];
        int count = 0;
        for (int y = 0; y < ring.Height; y++) for (int x = 0; x < ring.Width; x++)
            {
                int px = ring.X + x, py = ring.Y + y;
                if (!InsideGlyph(px, py, text, polygon, charX)) continue;
                var color = Color.FromArgb(pixels[py * width + px]);
                if (color.A != 255 || Difference(color, background) <= threshold) continue;
                mask[y * ring.Width + x] = 255;
                count++;
            }
        // 外扩一圈吞掉抗锯齿羽化，与改前的 3×3 膨胀同口径。
        var grown = new byte[mask.Length];
        Array.Copy(mask, grown, mask.Length);
        for (int y = 0; y < ring.Height; y++) for (int x = 0; x < ring.Width; x++)
            {
                if (mask[y * ring.Width + x] == 0) continue;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(ring.Height - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(ring.Width - 1, x + 1); xx++)
                        grown[yy * ring.Width + xx] = 255;
            }
        mask = grown;
        return count;
    }

    /// <summary>
    /// 像素是否落在真正可能有墨的位置：原框内 ＋ 多边形内（有则用）＋ 逐字刀口张成的列区间内（有则用）。
    /// 多边形与刀口都是可选值，缺失时回退轴对齐原框——历史缓存读回的行框与探针构造的行框就没有它们。
    /// </summary>
    private static bool InsideGlyph(int x, int y, RectangleF text, PointF[]? polygon, float[]? charX)
    {
        if (x < text.Left - 1 || x >= text.Right + 1 || y < text.Top - 1 || y >= text.Bottom + 1) return false;
        if (polygon is { Length: 4 } && !InQuad(x + .5f, y + .5f, polygon)) return false;
        if (charX is { Length: > 1 })
        {
            bool hit = false;
            for (int i = 0; i + 1 < charX.Length; i++)
                if (x >= charX[i] - 1 && x < charX[i + 1] + 1) { hit = true; break; }
            if (!hit) return false;
        }
        return true;
    }

    /// <summary>四边形内外判定，按顶点环绕方向自适应（OCR 侧顺逆时针都出现过）。</summary>
    private static bool InQuad(float x, float y, PointF[] q)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) % 4];
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (Math.Abs(cross) < 1e-9) continue;
            int current = cross > 0 ? 1 : -1;
            if (sign == 0) sign = current;
            else if (sign != current) return false;
        }
        return true;
    }

    // ---------------- 颜色统计 ----------------

    private static int Difference(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static Color MedianColor(List<BorderSample> border)
    {
        var r = new int[256]; var g = new int[256]; var b = new int[256];
        foreach (var sample in border) { r[sample.Color.R]++; g[sample.Color.G]++; b[sample.Color.B]++; }
        return Color.FromArgb(PercentileBucket(r, border.Count, 50), PercentileBucket(g, border.Count, 50),
            PercentileBucket(b, border.Count, 50));
    }

    private static int PercentileBucket(int[] counts, int total, int percent) => PercentileAt(counts, 0, total, percent);

    /// <summary>在 <paramref name="counts"/> 的 256 段直方图上取分位桶；<paramref name="offset"/> 供多通道共用一个数组。</summary>
    private static int PercentileAt(int[] counts, int offset, int total, int percent)
    {
        if (total <= 0) return 0;
        int target = (int)Math.Ceiling(total * percent / 100.0);
        for (int i = 0, sum = 0; i < 256; i++)
        {
            sum += counts[offset + i];
            if (sum >= target) return i;
        }
        return 0;
    }
}
