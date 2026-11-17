using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenCaptureTool.LongScroll.Diagnostics;

namespace ScreenCaptureTool.LongScroll.Stitching;

/// <summary>
/// 长截图滚动位移估算器。
/// 迁移自旧 LongScrollCapture.cs 的非 OpenCV 灰度匹配算法。
/// </summary>
public static partial class ImageStitcher
{
    // 全局搜索只负责提出候选，最终是否接受由分块一致性和完整接缝验证决定。
    // 因此候选阈值允许覆盖平滑滚动中的抗锯齿/动画差异，避免正确高速位移在验证前被过早丢弃。
    private const double ProbeMatchErrorThreshold = 20.0;
    // Horizontal matching samples every other source column. An odd physical-pixel
    // shift changes the sampling phase and has a deliberately looser coarse error;
    // full-resolution seam refinement still has to validate the final candidate.
    private const double HorizontalProbeMatchErrorThreshold = 48.0;
    private const double FinalMatchErrorThreshold = 18.0;
    // 强行方向（滚轮方向提示）下的误差放宽上限 = 正常门槛的 2 倍：
    // 容忍滚动动画模糊，同时把可疑匹配挡在校验之前。
    private const double ForcedDirectionMatchErrorThreshold = 36.0;
    private const double MinMatchConfidence = 0.18;
    // 强行方向下的置信度下限。Confidence 是最佳与次佳候选的相对差距：
    // 错周期匹配总有一个"几乎一样好"的竞争候选，置信度低于此限即拒绝
    // （宁可停顿等特征鲜明的帧，也不乱接）。
    private const double ForcedDirectionMinMatchConfidence = 0.30;
    private const double DirectionAmbiguousScoreGap = 1.5;
    private const double OpenCvMinInlierRatio = 0.42;
    private const double OpenCvForcedDirectionMinInlierRatio = 0.35;


    private static string _algorithmName = "GrayGradientBlock";
    private static string _lastDeltaSource = "None";
    private static string _lastDeltaScore = string.Empty;
    private static int _stitchingCount;
    private static int _openCvInitialized;
    private static readonly object _stateLock = new();

    public static string AlgorithmName { get { lock (_stateLock) return _algorithmName; } }

    public static string LastDeltaSource { get { lock (_stateLock) return _lastDeltaSource; } }

    public static string LastDeltaScore { get { lock (_stateLock) return _lastDeltaScore; } }

    public static int StitchingCount { get { lock (_stateLock) return _stitchingCount; } }

    public static string GetAlgorithmName() { lock (_stateLock) return _algorithmName; }

    public static string GetLastDeltaSource() { lock (_stateLock) return _lastDeltaSource; }

    public static string GetLastDeltaScore() { lock (_stateLock) return _lastDeltaScore; }

    public static string GetAlgoCountersText() { lock (_stateLock) return "N:" + _stitchingCount; }

    private struct MatchResult
    {
        public int Y;
        public double Error;
        public double SecondError;
        public double Confidence;
        public bool Ambiguous;
        public bool Valid;
    }

    private readonly struct LocalBandCandidate
    {
        public LocalBandCandidate(MatchResult match, int previousY, int blockHeight, int delta)
        {
            Match = match;
            PreviousY = previousY;
            BlockHeight = blockHeight;
            Delta = delta;
        }

        public MatchResult Match { get; }
        public int PreviousY { get; }
        public int BlockHeight { get; }
        public int Delta { get; }
    }

    private readonly record struct HorizontalContourCandidate(
        ScrollEstimate Estimate,
        double CoarseError,
        double AlternateError,
        double NearbyError,
        int ValleySpan,
        double SeamError)
    {
        public double QualityCost => CoarseError + SeamError * 0.45;
    }

    /// <summary>
    /// 估算两帧之间的滚动位移。
    /// 返回约定沿用旧实现：正数表示向上滚动，负数表示向下滚动，0 表示未检测到可信位移。
    /// </summary>
    public static int EstimateScrollDelta(Bitmap previous, Bitmap current, Action<string>? logger = null)
    {
        return EstimateScrollDelta(previous, current, logger, out _, out _);
    }

    /// <summary>
    /// Estimates signed viewport movement on either scroll axis. A negative delta moves
    /// toward the leading edge (top/left); a positive delta moves toward the trailing
    /// edge (bottom/right). Unlike the legacy API, direction-locked calls keep the sign.
    /// </summary>
    /// <summary>
    /// v3.1 帧链快速路径：1D 行亮度投影 NCC + 2D 接缝窄带复核。
    /// 相邻帧间隔小（8~16ms）时位移仅 10~40px，全图 2D 模板匹配是大材小用：
    /// 先把每帧压缩成沿轴亮度剖面（横轴按 ~64 列采样），在预测窄窗内做 1D NCC，
    /// 取 top-2 峰做歧义检查，再用 24px 接缝窄带 2D 复核排除"亮度相同内容不同"。
    /// 整条路径亚毫秒级；任何一步不满足即返回 Unreliable，由调用方回退全引擎。
    /// </summary>
    public static ScrollEstimate EstimateFastPath(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        int maxDelta,
        AxisDirection? expectedDirection)
    {
        if (previous == null || current == null || maxDelta <= 0)
        {
            return ScrollEstimate.Unreliable("FastPathInvalidFrame");
        }

        int alongPrev = AxisGeometry.AlongLength(axis, previous);
        int alongCur = AxisGeometry.AlongLength(axis, current);
        int crossPrev = AxisGeometry.CrossLength(axis, previous);
        int crossCur = AxisGeometry.CrossLength(axis, current);
        if (alongPrev != alongCur || crossPrev != crossCur || alongPrev < 64 || crossPrev < 8)
        {
            return ScrollEstimate.Unreliable("FastPathSizeMismatch");
        }

        float[]? prevProfile = ExtractBrightnessProfile(previous, axis);
        float[]? curProfile = ExtractBrightnessProfile(current, axis);
        if (prevProfile == null || curProfile == null)
        {
            return ScrollEstimate.Unreliable("FastPathExtractFailed");
        }

        // 方向剪枝：滚轮/滚动条已给出方向时只搜对应半区间，省一半扫描。
        int dirSign = expectedDirection == AxisDirection.Leading ? -1
            : expectedDirection == AxisDirection.Trailing ? 1
            : 0;
        int minD = dirSign < 0 ? -maxDelta : (dirSign > 0 ? 1 : -maxDelta);
        int maxD = dirSign < 0 ? -1 : maxDelta;

        int bestD = 0;
        double bestN = -1.0;
        int secondD = 0;
        double secondN = -1.0;
        int minOverlap = Math.Max(32, alongPrev / 3);
        const int MinGap = 8;  // 排除同一相关峰的峰肩所需最小间隔（见下文注释）
        for (int d = minD; d <= maxD; d++)
        {
            if (d == 0) continue;
            double n = ProfileCrossCorrelation(prevProfile, curProfile, d, minOverlap);
            if (n > bestN)
            {
                secondN = bestN;
                secondD = bestD;
                bestN = n;
                bestD = d;
            }
            else if (n > secondN && Math.Abs(d - bestD) >= MinGap)
            {
                // 2026-10-01 补：只有与当前最优距离 ≥ MinGap 才接受为次优候选（排除峰肩）。
                // 理由：相关函数峰宽 ≈ 特征宽度（文字行高、UI 元素间距，常 10~30px），
                // 峰两侧各 ±3~4 个采样点仍属同一相关峰。原写法只要 n > secondN 就收，
                // 会把"最优 d=50、峰肩 d=49"选成 top-2（margin ≈ 0.001）→ 误判歧义
                // → 跳缺口 → 丢内容（用户 2026-10-01 图示形态之一）。minGap=8 约为
                // 典型峰宽的一半，足以排除肩、又不至于漏掉真实的次优峰。
                secondN = n;
                secondD = d;
            }
        }

        if (bestD == 0 || bestN < 0.55)
        {
            return ScrollEstimate.Unreliable("FastPathLowScore", score: Math.Max(0.0, bestN));
        }

        // 2026-10-01 补：最优候选落在搜索窗边界 ⇒ 真峰可能在窗外 ⇒ 判不可信。
        // 成因：maxDelta（搜索窗宽度）有上限（默认 alongPrev * 2.5 但不超 2000），
        // 用户快速滚动时真实位移可能超出窗口，此时窗内的"最优"其实是次优甚至误判。
        // 直接把它返回会让位移偏大（往尾部多拼）→ 丢失中间那截 → 行距被压缩、出接缝。
        // 落边界时返回 Unreliable，交给全引擎（模板匹配可扩展搜索范围、且有分块共识）。
        if (bestD == minD || bestD == maxD)
        {
            return ScrollEstimate.Unreliable("FastPathBoundary", score: bestN);
        }

        double margin = bestN - secondN;
        if (margin < 0.12)
        {
            // 周期性纹理会出现两个几乎等高的峰（差一个周期），1D 无从分辨，
            // 必须交给全引擎（模板匹配 + 区块共识）处理。
            return ScrollEstimate.Unreliable("FastPathAmbiguous", score: bestN);
        }

        // 窄带 + 全重叠双重复核（2026-10-01）：只过窄带会让"空白对空白"的错位移直通画布，
        // 正是偶发重复拼接的来源。详见 VerifyCandidateStrict 的注释。
        if (!VerifyCandidateStrict(previous, current, axis, bestD, 24, 12.0))
        {
            return ScrollEstimate.Unreliable("FastPathSeamReject", score: bestN);
        }

        // 同步全局 reason/score：会话层日志与状态提示读取全局静态状态，快速
        // 路径命中时若不写入，日志会残留上一次全引擎的结果（误导诊断）。
        SetLast("FastPath1D", "margin=" + margin.ToString("0.00"));
        return new ScrollEstimate(
            bestD,
            0,
            0,
            true,
            "FastPath1D",
            Math.Clamp(bestN, 0.0, 1.0),
            "margin=" + margin.ToString("0.00"));

    }

    /// <summary>提取沿轴亮度剖面：沿轴每行(列)的平均亮度，横轴按 ~64 个采样列降成本。</summary>
    private static unsafe float[]? ExtractBrightnessProfile(Bitmap bmp, ScrollAxis axis)
    {
        bool vertical = axis == ScrollAxis.Vertical;
        int w = bmp.Width;
        int h = bmp.Height;
        int profileLen = vertical ? h : w;
        int crossLen = vertical ? w : h;
        if (profileLen < 16 || crossLen < 8) return null;

        float[] profile = new float[profileLen];
        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int step = Math.Max(1, crossLen / 64);
            byte* basePtr = (byte*)data.Scan0;
            if (vertical)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = basePtr + y * data.Stride;
                    int sum = 0;
                    int cnt = 0;
                    for (int x = 0; x < w; x += step)
                    {
                        byte* p = row + x * 4;
                        sum += (p[0] * 114 + p[1] * 587 + p[2] * 299) / 1000;
                        cnt++;
                    }
                    profile[y] = cnt > 0 ? (float)sum / cnt : 0f;
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    int cnt = 0;
                    for (int y = 0; y < h; y += step)
                    {
                        byte* p = basePtr + y * data.Stride + x * 4;
                        sum += (p[0] * 114 + p[1] * 587 + p[2] * 299) / 1000;
                        cnt++;
                    }
                    profile[x] = cnt > 0 ? (float)sum / cnt : 0f;
                }
            }
            return profile;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>1D 归一化互相关：位移 d 处重叠区的去均值 NCC，返回 [-1,1]。</summary>
    private static double ProfileCrossCorrelation(float[] a, float[] b, int d, int minOverlap)
    {
        int n = a.Length;
        int s = d >= 0 ? d : 0;
        int e = d >= 0 ? n : n + d;
        int len = e - s;
        if (len < minOverlap) return -1.0;

        double ma = 0.0;
        double mb = 0.0;
        for (int i = 0; i < len; i++)
        {
            ma += a[s + i];
            mb += b[s + i - d];
        }
        ma /= len;
        mb /= len;

        double num = 0.0;
        double da = 0.0;
        double db = 0.0;
        for (int i = 0; i < len; i++)
        {
            double xa = a[s + i] - ma;
            double xb = b[s + i - d] - mb;
            num += xa * xb;
            da += xa * xa;
            db += xb * xb;
        }
        if (da <= 1e-9 || db <= 1e-9) return -1.0;
        return num / Math.Sqrt(da * db);
    }

    /// <summary>2D 接缝窄带平均绝对差（亮度尺度 0~255）；重叠不足返回 +∞。双候选对比用。</summary>
    private static unsafe double SeamBandMad(Bitmap previous, Bitmap current, ScrollAxis axis, int delta, int band)
    {
        bool vertical = axis == ScrollAxis.Vertical;
        BitmapData pd = previous.LockBits(
            new Rectangle(0, 0, previous.Width, previous.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        BitmapData cd = current.LockBits(
            new Rectangle(0, 0, current.Width, current.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int alongLen = vertical ? previous.Height : previous.Width;
            int overlapStart = Math.Max(0, delta);
            int overlapEnd = Math.Min(alongLen, alongLen + Math.Min(0, delta));
            if (overlapEnd - overlapStart < band * 2) return double.PositiveInfinity;
            int mid = (overlapStart + overlapEnd) / 2;
            int bandStart = mid - band / 2;

            long madSum = 0;
            long sampleCount = 0;
            if (vertical)
            {
                int w = Math.Min(previous.Width, current.Width);
                int step = Math.Max(1, w / 48);
                for (int y = bandStart; y < bandStart + band; y++)
                {
                    byte* pa = (byte*)pd.Scan0 + y * pd.Stride;
                    byte* pb = (byte*)cd.Scan0 + (y - delta) * cd.Stride;
                    for (int x = 0; x < w; x += step)
                    {
                        byte* a = pa + x * 4;
                        byte* b = pb + x * 4;
                        int la = (a[0] * 114 + a[1] * 587 + a[2] * 299) / 1000;
                        int lb = (b[0] * 114 + b[1] * 587 + b[2] * 299) / 1000;
                        madSum += Math.Abs(la - lb);
                        sampleCount++;
                    }
                }
            }
            else
            {
                int h = Math.Min(previous.Height, current.Height);
                int step = Math.Max(1, h / 48);
                for (int x = bandStart; x < bandStart + band; x++)
                {
                    for (int y = 0; y < h; y += step)
                    {
                        byte* a = (byte*)pd.Scan0 + y * pd.Stride + x * 4;
                        byte* b = (byte*)cd.Scan0 + y * cd.Stride + (x - delta) * 4;
                        int la = (a[0] * 114 + a[1] * 587 + a[2] * 299) / 1000;
                        int lb = (b[0] * 114 + b[1] * 587 + b[2] * 299) / 1000;
                        madSum += Math.Abs(la - lb);
                        sampleCount++;
                    }
                }
            }
            if (sampleCount == 0) return double.PositiveInfinity;
            return madSum / (double)sampleCount;
        }
        finally
        {
            previous.UnlockBits(pd);
            current.UnlockBits(cd);
        }
    }

    /// <summary>
    /// 全重叠区均值绝对差（亮度 0~255）。与 SeamBandMad 只采样重叠区中部 64px 窄带
    /// 不同，这里覆盖整个重叠区（沿轴采样至多 180 行、横轴至多 80 列）。稀疏/周期
    /// 纹理（聊天气泡、正文行、大段空白）上，窄带极易整体落在空白区，导致“空白对
    /// 空白”在多个错位都得到 MAD≈0，无法区分真实位移；全重叠 MAD 则让气泡/文字等
    /// 非空内容参与判分——只有真实对齐时整个重叠区像素级一致（MAD≈0~几），任何错
    /// 一个周期的偏移都会因气泡错位把 MAD 抬到十几以上，从而可靠区分。
    /// </summary>
    private static unsafe double FullOverlapMad(Bitmap previous, Bitmap current, ScrollAxis axis, int delta)
    {
        bool vertical = axis == ScrollAxis.Vertical;
        BitmapData pd = previous.LockBits(
            new Rectangle(0, 0, previous.Width, previous.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        BitmapData cd = current.LockBits(
            new Rectangle(0, 0, current.Width, current.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int alongLen = vertical ? previous.Height : previous.Width;
            int crossLen = vertical ? previous.Width : previous.Height;
            int overlapStart = Math.Max(0, delta);
            int overlapEnd = Math.Min(alongLen, alongLen + Math.Min(0, delta));
            int overlapLen = overlapEnd - overlapStart;
            if (overlapLen < 32) return double.PositiveInfinity;

            int stepAlong = Math.Max(1, overlapLen / 180);
            int stepCross = Math.Max(1, crossLen / 80);

            long madSum = 0;
            long sampleCount = 0;
            if (vertical)
            {
                for (int y = overlapStart; y < overlapEnd; y += stepAlong)
                {
                    byte* pa = (byte*)pd.Scan0 + y * pd.Stride;
                    byte* pb = (byte*)cd.Scan0 + (y - delta) * cd.Stride;
                    for (int x = 0; x < crossLen; x += stepCross)
                    {
                        byte* a = pa + x * 4;
                        byte* b = pb + x * 4;
                        int la = (a[0] * 114 + a[1] * 587 + a[2] * 299) / 1000;
                        int lb = (b[0] * 114 + b[1] * 587 + b[2] * 299) / 1000;
                        madSum += Math.Abs(la - lb);
                        sampleCount++;
                    }
                }
            }
            else
            {
                for (int x = overlapStart; x < overlapEnd; x += stepAlong)
                {
                    byte* pa = (byte*)pd.Scan0 + x * 4;
                    byte* pb = (byte*)cd.Scan0 + (x - delta) * 4;
                    for (int y = 0; y < crossLen; y += stepCross)
                    {
                        byte* a = pa + y * pd.Stride;
                        byte* b = pb + y * cd.Stride;
                        int la = (a[0] * 114 + a[1] * 587 + a[2] * 299) / 1000;
                        int lb = (b[0] * 114 + b[1] * 587 + b[2] * 299) / 1000;
                        madSum += Math.Abs(la - lb);
                        sampleCount++;
                    }
                }
            }
            if (sampleCount == 0) return double.PositiveInfinity;
            return madSum / (double)sampleCount;
        }
        finally
        {
            previous.UnlockBits(pd);
            current.UnlockBits(cd);
        }
    }

    /// <summary>2D 接缝窄带复核（原 VerifySeamBand 语义）：MAD 不超过阈值即通过。</summary>
    private static bool VerifySeamBand(Bitmap previous, Bitmap current, ScrollAxis axis, int delta, int band, double maxMad)
    {
        return SeamBandMad(previous, current, axis, delta, band) <= maxMad;
    }

    /// <summary>
    /// 候选位移的**双重**像素复核（2026-10-01 加，修"长截图偶发重复拼接"）：窄带 MAD **与**
    /// 全重叠 MAD 都必须过关。
    ///
    /// <para>🔴 为什么不能只用窄带（原 L1/L2 的写法）：<see cref="SeamBandMad"/> 只采样重叠区
    /// **中部 24px**，<see cref="FullOverlapMad"/> 的注释早已论证过——稀疏/周期纹理（聊天气泡、
    /// 正文行、大段空白、渐变背景、网格底纹）上那条窄带极易整体落在空白区，于是"空白对空白"
    /// 在**多个错位**都得到 MAD≈0，根本区分不出真实位移。那条认识当时只落到 L3，而 L1/L2 才是
    /// 绝大多数帧实际走的主路径 ⇒ 错位移被判成"可信"、每帧追加一小条，成品里同一段内容重复
    /// 多次且每次只偏一点（用户 2026-10-01 实报的形态）。</para>
    ///
    /// <para>全重叠阈值给得比窄带松（<paramref name="maxFullMad"/> 默认 16 vs 窄带 12）：它覆盖
    /// 整个重叠区、天然包含更多噪声与亚像素渲染差异，按窄带的尺度卡会把正常帧也拒掉
    /// （那会退化成"滚几下就停住"——本文件记过的反向教训）。真实对齐时全重叠 MAD 恒在 0~几，
    /// 错一个周期则被气泡/文字错位抬到十几以上，16 这一档足以分开两者。</para>
    /// </summary>
    private static bool VerifyCandidateStrict(
        Bitmap previous, Bitmap current, ScrollAxis axis, int delta,
        int band, double maxSeamMad, double maxFullMad = 16.0)
    {
        if (SeamBandMad(previous, current, axis, delta, band) > maxSeamMad) return false;
        return FullOverlapMad(previous, current, axis, delta) <= maxFullMad;
    }

    /// <summary>
    /// 追加边缘的局部误差。向上追加时检查重叠区起点，向下追加时检查终点；
    /// 速度先验兜底不能只凭全区均值越过真正写入画布的接缝。
    /// </summary>
    private static unsafe double AppendEdgeMad(Bitmap previous, Bitmap current, ScrollAxis axis, int delta, int band)
    {
        bool vertical = axis == ScrollAxis.Vertical;
        int alongLen = AxisGeometry.AlongLength(axis, previous);
        int crossLen = AxisGeometry.CrossLength(axis, previous);
        int overlapStart = Math.Max(0, delta);
        int overlapEnd = Math.Min(alongLen, alongLen + Math.Min(0, delta));
        if (overlapEnd - overlapStart < band || crossLen != AxisGeometry.CrossLength(axis, current))
            return double.PositiveInfinity;

        int bandStart = delta < 0 ? overlapStart : overlapEnd - band;
        BitmapData pd = previous.LockBits(new Rectangle(0, 0, previous.Width, previous.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        BitmapData cd = current.LockBits(new Rectangle(0, 0, current.Width, current.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            long difference = 0;
            long samples = 0;
            int crossStep = Math.Max(1, crossLen / 48);
            for (int along = bandStart; along < bandStart + band; along++)
            {
                for (int cross = 0; cross < crossLen; cross += crossStep)
                {
                    byte* a = vertical
                        ? (byte*)pd.Scan0 + along * pd.Stride + cross * 4
                        : (byte*)pd.Scan0 + cross * pd.Stride + along * 4;
                    byte* b = vertical
                        ? (byte*)cd.Scan0 + (along - delta) * cd.Stride + cross * 4
                        : (byte*)cd.Scan0 + cross * cd.Stride + (along - delta) * 4;
                    int la = (a[0] * 114 + a[1] * 587 + a[2] * 299) / 1000;
                    int lb = (b[0] * 114 + b[1] * 587 + b[2] * 299) / 1000;
                    difference += Math.Abs(la - lb);
                    samples++;
                }
            }
            return difference / (double)samples;
        }
        finally
        {
            previous.UnlockBits(pd);
            current.UnlockBits(cd);
        }
    }

    /// <summary>
    /// v3.1 L2 宽窗复核：宽窗 1D 搜索取 top-2 候选，对两候选分别做 2D 接缝带
    /// MAD 对比。周期纹理上 1D 双峰无法分辨（差一个周期亮度剖面相同），但 2D
    /// 像素在横向细节（文字/图标差异）上可分辨；胜者显著更优（MAD ≤12 且不劣
    /// 于胜者 1.3 倍）则接受，仍并列则报 WideAmbiguous——数学歧义区的确定性
    /// 签名，交由会话层 GAP 跳缺口。单一显著峰（margin ≥0.12）只做一次 2D
    /// 复核即接受。
    /// </summary>
    public static ScrollEstimate EstimateWidePath(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        int maxDelta,
        AxisDirection? expectedDirection,
        out int candidateA,
        out int candidateB)
    {
        candidateA = 0;
        candidateB = 0;
        if (previous == null || current == null || maxDelta <= 0)
        {
            return ScrollEstimate.Unreliable("WideInvalidFrame");
        }

        int alongPrev = AxisGeometry.AlongLength(axis, previous);
        int alongCur = AxisGeometry.AlongLength(axis, current);
        int crossPrev = AxisGeometry.CrossLength(axis, previous);
        int crossCur = AxisGeometry.CrossLength(axis, current);
        if (alongPrev != alongCur || crossPrev != crossCur || alongPrev < 64 || crossPrev < 8)
        {
            return ScrollEstimate.Unreliable("WideSizeMismatch");
        }

        float[]? prevProfile = ExtractBrightnessProfile(previous, axis);
        float[]? curProfile = ExtractBrightnessProfile(current, axis);
        if (prevProfile == null || curProfile == null)
        {
            return ScrollEstimate.Unreliable("WideExtractFailed");
        }

        int dirSign = expectedDirection == AxisDirection.Leading ? -1
            : expectedDirection == AxisDirection.Trailing ? 1
            : 0;
        int minD = dirSign < 0 ? -maxDelta : (dirSign > 0 ? 1 : -maxDelta);
        int maxD = dirSign < 0 ? -1 : maxDelta;

        int bestD = 0;
        int secondD = 0;
        double bestN = -1.0;
        double secondN = -1.0;
        int minOverlap = Math.Max(32, alongPrev / 3);
        const int MinGap = 8;  // 排除同一相关峰的峰肩所需最小间隔（与 L1 同口径）
        for (int d = minD; d <= maxD; d++)
        {
            if (d == 0) continue;
            double n = ProfileCrossCorrelation(prevProfile, curProfile, d, minOverlap);
            if (n > bestN)
            {
                secondD = bestD;
                secondN = bestN;
                bestD = d;
                bestN = n;
            }
            else if (n > secondN && Math.Abs(d - bestD) >= MinGap)
            {
                // 2026-10-01 补：与 L1 同一判据——只有与当前最优距离 ≥ MinGap 才接受为次优。
                // 原写法只要 n > secondN 就收 ⇒ 把峰肩选成 top-2（margin ≈0）⇒ 误判歧义。
                secondD = d;
                secondN = n;
            }
        }

        if (bestD == 0 || bestN < 0.50)
        {
            return ScrollEstimate.Unreliable("WideLowScore", score: Math.Max(0.0, bestN));
        }

        // 2026-10-01 补：最优候选落在搜索窗边界 ⇒ 真峰可能在窗外 ⇒ 判不可信（与 L1 同一判据）。
        if (bestD == minD || bestD == maxD)
        {
            return ScrollEstimate.Unreliable("WideBoundary", score: bestN);
        }

        candidateA = bestD;
        candidateB = secondD;

        // 单一显著峰：一次 2D 复核即可接受。
        // 复核改为窄带 + 全重叠双重（2026-10-01）：窄带单独判在渐变/网格/空白区会把错位移判成可信，
        // 见 VerifyCandidateStrict。"单峰"只说明 1D 剖面上没有竞争者，不等于 2D 像素真的对齐。
        if (secondD == 0 || bestN - secondN >= 0.12)
        {
            if (!VerifyCandidateStrict(previous, current, axis, bestD, 24, 12.0))
            {
                return ScrollEstimate.Unreliable("WideSeamReject", score: bestN);
            }
            SetLast("Wide1D", "n=" + bestN.ToString("0.00"));
            return new ScrollEstimate(
                bestD,
                0,
                0,
                true,
                "Wide1D",
                Math.Clamp(bestN, 0.0, 1.0),
                "margin=" + (bestN - secondN).ToString("0.00"));
        }

        // 双峰：两候选分别算 MAD，显著更优者胜出。
        // 🔴 裁决量改用**全重叠** MAD（2026-10-01）：这里要分辨的恰恰是"差一个周期"的两个候选，
        // 而窄带 24px 在周期纹理上对这两者给出的分数几乎相同（空白对空白恒 ≈0），
        // 等于拿一把分不出差别的尺子去裁决——`FullOverlapMad` 的注释正是为这件事写的。
        double madA = FullOverlapMad(previous, current, axis, bestD);
        double madB = FullOverlapMad(previous, current, axis, secondD);
        double win = Math.Min(madA, madB);
        double lose = Math.Max(madA, madB);
        int winD = madA <= madB ? bestD : secondD;
        // 阈值随判据一起放到全重叠的尺度（16，与 VerifyCandidateStrict 同档）；
        // 相对优势仍要求 1.3 倍，"两者都好"一律按歧义交 GAP，宁可停顿等特征鲜明的帧也不乱接。
        // 两个候选都为零误差时，0 >= 0 * 1.3 也成立，但完全相同的周期没有胜者。
        // 必须确实优于另一候选，才允许相对优势判据通过。
        if (win <= 16.0 && lose > win && lose >= win * 1.3)
        {
            // 胜者还要再过一次窄带：全重叠已把错周期筛掉，窄带负责接缝处的局部细节
            // （两者各管一段，缺一面都曾被实测绕过）。
            if (SeamBandMad(previous, current, axis, winD, 24) <= 12.0)
            {
                SetLast("Wide2D", "mad=" + win.ToString("0.0"));
                return new ScrollEstimate(
                    winD,
                    0,
                    0,
                    true,
                    "Wide2D",
                    Math.Clamp(bestN, 0.0, 1.0),
                    "mad=" + win.ToString("0.0") + "/" + lose.ToString("0.0"));
            }
            return ScrollEstimate.Unreliable("WideSeamReject", score: bestN,
                diagnostics: "winD=" + winD + " fullMad=" + win.ToString("0.0"));
        }
        return ScrollEstimate.Unreliable("WideAmbiguous", score: bestN,
            diagnostics: "cand=" + bestD + "/" + secondD
                + " fullMad=" + madA.ToString("0.0") + "/" + madB.ToString("0.0"));
    }

    /// <summary>
    /// v3.1 L3 周期纹理解歧义兜底：常规估计（FastPath/WidePath/全引擎）在重复纹理上
    /// 会陷入「±一个行周期」的方向/幅度歧义而返回不可靠(delta=0)。本方法以调用方提供
    /// 的「上一次已确认位移」（带方向）作为先验，在其 ±searchRadius 邻域内用全重叠区
    /// MAD（覆盖气泡/文字等非空内容，抗大段空白误判）取最小 MAD；只有 MAD ≤ maxMad 才接受，
    /// 否则返回不可靠。调用方须保证帧确已发生滚动（已通过 AreImagesIdentical +
    /// IsMotionScrollLike 门控），本方法不重复判断静止。
    /// </summary>
    public static ScrollEstimate EstimateByDeltaHint(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        int hintDelta,
        int searchRadius = 24,
        double maxMad = 18.0)
    {
        if (previous == null || current == null || hintDelta == 0 || searchRadius < 0)
        {
            return ScrollEstimate.Unreliable("HintInvalid");
        }

        int alongPrev = AxisGeometry.AlongLength(axis, previous);
        int alongCur = AxisGeometry.AlongLength(axis, current);
        int crossPrev = AxisGeometry.CrossLength(axis, previous);
        int crossCur = AxisGeometry.CrossLength(axis, current);
        if (alongPrev != alongCur || crossPrev != crossCur || alongPrev <= 128 || crossPrev < 8)
        {
            return ScrollEstimate.Unreliable("HintSizeMismatch");
        }

        int sign = Math.Sign(hintDelta);
        int absHint = Math.Abs(hintDelta);
        int from = Math.Max(8, absHint - searchRadius);
        int to = Math.Min(alongPrev - 128, absHint + searchRadius);
        if (to < from)
        {
            return ScrollEstimate.Unreliable("HintOutOfRange",
                diagnostics: "hint=" + hintDelta + " from=" + from + " to=" + to);
        }

        int bestD = 0;
        double bestMad = double.PositiveInfinity;
        for (int m = from; m <= to; m++)
        {
            int d = sign * m;
            double mad = FullOverlapMad(previous, current, axis, d);
            if (mad < bestMad)
            {
                bestMad = mad;
                bestD = d;
                if (bestMad <= 1.5) break; // 已高度确定，提前退出
            }
        }

        // 噪声地板：在明显偏离先验的方向采样一组「错位候选」的最小接缝 MAD。
        // 周期纹理（聊天气泡/正文行/大段空白）上，正确对接绝对 MAD 可能只比错位
        // 低一点点（如 15 vs 17），单靠绝对阈值无法区分；用「真实接缝必须显著优于
        // 最接近的错位噪声」这一相对条件来判真，避免把陈旧先验导致的错位接进画布。
        double noiseFloor = double.PositiveInfinity;
        int span = Math.Max(1, searchRadius);
        int checkStart = Math.Max(8, absHint + span + 8);
        int checkEnd = Math.Min(alongPrev - 128, absHint + span * 4);
        if (checkEnd > checkStart)
        {
            int step = Math.Max(1, span);
            for (int m = checkStart; m <= checkEnd; m += step)
            {
                int d = sign * m;
                double mad = FullOverlapMad(previous, current, axis, d);
                if (mad < noiseFloor) noiseFloor = mad;
            }
        }

        // 接受条件：绝对 MAD 不超过上限，且（存在噪声地板时）要明显优于该地板。
        // 旧版用固定比例(0.75×noise)判「明显优于」，但在周期纹理中噪声地板偏高时
        // 会把真实接缝误杀；改用绝对余量：低噪声更严、高噪声更宽，更贴合物理且不放大错周期。
        bool reliable = bestD != 0 && bestMad <= maxMad;
        if (reliable && double.IsFinite(noiseFloor) && bestMad >= noiseFloor - 2.0)
        {
            reliable = false;
        }
        // 速度先验只用全区均值会把局部撕裂平均掉；真正追加的边缘也必须连续。
        double seamMad = reliable ? SeamBandMad(previous, current, axis, bestD, 24) : double.PositiveInfinity;
        double edgeMad = reliable ? AppendEdgeMad(previous, current, axis, bestD, 24) : double.PositiveInfinity;
        bool localSeamRejected = reliable && (seamMad > 12.0 || edgeMad > 12.0);
        if (localSeamRejected) reliable = false;

        if (!reliable)
        {
            string reason = localSeamRejected ? "HintLocalSeamReject" : "HintUnreliable";
            SetLast(reason, $"hint={hintDelta} mad={bestMad:F1} noise={noiseFloor:F1} seam={seamMad:F1} edge={edgeMad:F1}");
            return ScrollEstimate.Unreliable(reason,
                score: 0.0,
                diagnostics: "hint=" + hintDelta + " mad=" + bestMad.ToString("F1")
                    + " noise=" + (double.IsFinite(noiseFloor) ? noiseFloor.ToString("F1") : "n/a")
                    + " seam=" + seamMad.ToString("F1") + " edge=" + edgeMad.ToString("F1"));
        }

        SetLast("HintSeam", $"hint={hintDelta} mad={bestMad:F1} noise={noiseFloor:F1} seam={seamMad:F1} edge={edgeMad:F1}");
        return new ScrollEstimate(
            bestD,
            0,
            0,
            true,
            "HintSeam",
            Math.Clamp(1.0 - bestMad / 24.0, 0.0, 1.0),
            "hint=" + hintDelta + " mad=" + bestMad.ToString("F1") + " delta=" + bestD);
    }

    /// <summary>
    /// v3.1 L3 冷启动方向兜底：会话刚启动（尚无 lastAcceptedDelta）但方向证据已到位时，
    /// 常规估计器在周期纹理（聊天气泡/正文行/大段空白）上陷入「±一个周期」的方向/幅度
    /// 歧义而返回 delta=0，导致永远建立不了速度先验 → 后续 L3 delta-hint 兜底永不触发
    /// （先有鸡还是先有蛋的死锁）。本方法只依赖方向符号：沿该方向指定范围粗扫最小全重叠
    /// 区 MAD（覆盖气泡/文字等非空内容，抗大段空白误判），再精化，并用「错位噪声地板」判真——
    /// 真实接缝必须显著优于最接近的错位候选才接受，否则回退 REJECT/GAP，不把错周期
    /// 位移接进画布。未指定范围时搜索从 1px 到最大可保留 128px 重叠的位置；快速路径复核会给出候选附近的范围。
    /// </summary>
    public static ScrollEstimate EstimateByDirection(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        int directionSign,
        double maxMad = 18.0,
        int maxDelta = 0,
        int minDelta = 1)
    {
        if (previous == null || current == null || directionSign == 0)
        {
            return ScrollEstimate.Unreliable("DirectionHintInvalid");
        }

        int alongPrev = AxisGeometry.AlongLength(axis, previous);
        int alongCur = AxisGeometry.AlongLength(axis, current);
        int crossPrev = AxisGeometry.CrossLength(axis, previous);
        int crossCur = AxisGeometry.CrossLength(axis, current);
        if (alongPrev != alongCur || crossPrev != crossCur || alongPrev <= 128 || crossPrev < 8)
        {
            return ScrollEstimate.Unreliable("DirectionHintSizeMismatch");
        }

        // 搜索至少保留 128px 重叠；1px 起步，避免把慢速滚动硬设成不可识别。
        int from = Math.Clamp(minDelta, 1, alongPrev - 128);
        int to = Math.Min(alongPrev - 128, maxDelta > 0 ? maxDelta : alongPrev - 128);
        if (to < from)
        {
            return ScrollEstimate.Unreliable("DirectionHintOutOfRange");
        }

        // 粗扫：步长 4。
        int bestD = 0;
        double bestMad = double.PositiveInfinity;
        for (int m = from; m <= to; m += 4)
        {
            int d = directionSign * m;
            double mad = FullOverlapMad(previous, current, axis, d);
            if (mad < bestMad)
            {
                bestMad = mad;
                bestD = d;
            }
        }

        // 精化：bestD 邻域逐点。
        if (bestD != 0)
        {
            int bestAbs = Math.Abs(bestD);
            int refineFrom = Math.Max(from, bestAbs - 4);
            int refineTo = Math.Min(to, bestAbs + 4);
            for (int m = refineFrom; m <= refineTo; m++)
            {
                int d = directionSign * m;
                double mad = FullOverlapMad(previous, current, axis, d);
                if (mad < bestMad)
                {
                    bestMad = mad;
                    bestD = d;
                }
            }
        }

        // 错位噪声地板：同方向、避开 bestD 邻域粗采样最小 MAD。周期纹理若所有
        // 位移 MAD 相近（无清晰接缝），bestD 不会显著更优 → 判假，安全回退。
        double noiseFloor = double.PositiveInfinity;
        for (int m = from; m <= to; m += 16)
        {
            if (Math.Abs(directionSign * m - bestD) <= 8) continue;
            double mad = FullOverlapMad(previous, current, axis, directionSign * m);
            if (mad < noiseFloor) noiseFloor = mad;
        }

        bool reliable = bestD != 0 && bestMad <= maxMad;
        if (reliable && double.IsFinite(noiseFloor) && bestMad >= noiseFloor - 2.0)
        {
            reliable = false;
        }
        double seamMad = reliable ? SeamBandMad(previous, current, axis, bestD, 24) : double.PositiveInfinity;
        double edgeMad = reliable ? AppendEdgeMad(previous, current, axis, bestD, 24) : double.PositiveInfinity;
        if (seamMad > 12.0 || edgeMad > 12.0) reliable = false;

        if (!reliable)
        {
            SetLast("DirectionHintUnreliable", $"dir={directionSign} mad={bestMad:F1} noise={noiseFloor:F1} seam={seamMad:F1} edge={edgeMad:F1}");
            return ScrollEstimate.Unreliable("DirectionHintUnreliable",
                score: 0.0,
                diagnostics: "dir=" + directionSign + " mad=" + bestMad.ToString("F1")
                    + " noise=" + (double.IsFinite(noiseFloor) ? noiseFloor.ToString("F1") : "n/a")
                    + " seam=" + seamMad.ToString("F1") + " edge=" + edgeMad.ToString("F1"));
        }

        SetLast("DirectionHintSeam", $"dir={directionSign} mad={bestMad:F1} noise={noiseFloor:F1} seam={seamMad:F1} edge={edgeMad:F1}");
        return new ScrollEstimate(
            bestD,
            0,
            0,
            true,
            "DirectionHintSeam",
            Math.Clamp(1.0 - bestMad / 24.0, 0.0, 1.0),
            "dir=" + directionSign + " mad=" + bestMad.ToString("F1") + " delta=" + bestD);
    }

    public static ScrollEstimate Estimate(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        AxisDirection? expectedViewportDirection,
        int preferredMaxDelta,
        Action<string>? logger = null,
        int preferredDelta = 0)
    {
        if (previous == null || current == null)
        {
            SetLast("InvalidFrame", string.Empty);
            return ScrollEstimate.Unreliable("InvalidFrame");
        }

        bool forceLeading = expectedViewportDirection == AxisDirection.Leading;
        bool forceTrailing = expectedViewportDirection == AxisDirection.Trailing;

        // 统一优先使用 OpenCV 模板匹配引擎（纵向与横向通用）。搜索窗即模板
        // 匹配的扫描范围：
        //   横向：使用会话提供的窗口（有方向证据时紧凑、恢复时放宽），并启用
        //     更严格的歧义检查——横向页面多为周期性网格；
        //   纵向：与会话层绝对上限（0.60×视口）对齐。窗口小于上限时，落在
        //     差值区间的真实位移会绕道 GrayBlock 兜底（双引擎都跑反而更慢）；
        //     错周期乱接由会话层 DeltaOverCap 与接缝校验共同拦截。
        //     金字塔匹配已把大窗口的单帧成本降低约 75%，放宽窗口不再昂贵。
        int openCvAlongLength = axis == ScrollAxis.Horizontal
            ? Math.Min(previous.Width, current.Width)
            : Math.Min(previous.Height, current.Height);
        int openCvMaxDelta = axis == ScrollAxis.Horizontal
            ? Math.Max(0, preferredMaxDelta)
            : Math.Max(64, (int)Math.Round(openCvAlongLength * 0.60));
        bool openCvAggressive = axis == ScrollAxis.Horizontal;
        int openCvDelta = EstimateScrollDeltaOpenCv(
            previous,
            current,
            axis,
            forceLeading,
            forceTrailing,
            out int staticLeading,
            out int staticTrailing,
            openCvMaxDelta,
            openCvAggressive);

        if (openCvDelta != 0)
        {
            int viewportDelta = expectedViewportDirection switch
            {
                AxisDirection.Leading => openCvDelta == 0 ? 0 : -openCvDelta,
                AxisDirection.Trailing => openCvDelta,
                _ => openCvDelta,
            };

            string source = GetLastDeltaSource();
            string diagnostics = GetLastDeltaScore();
            return new ScrollEstimate(
                viewportDelta,
                staticLeading,
                staticTrailing,
                true,
                "MatchAccepted",
                1.0,
                $"source={source} {diagnostics}".TrimEnd());
        }

        if (axis == ScrollAxis.Horizontal)
        {
            return EstimateHorizontal(
                previous,
                current,
                expectedViewportDirection,
                preferredMaxDelta,
                preferredDelta,
                logger);
        }

        int legacyDelta = EstimateScrollDelta(
            previous,
            current,
            logger,
            out staticLeading,
            out staticTrailing,
            forceLeading,
            forceTrailing,
            preferredMaxDelta);

        int legacyViewportDelta = expectedViewportDirection switch
        {
            AxisDirection.Leading => legacyDelta == 0 ? 0 : -legacyDelta,
            AxisDirection.Trailing => legacyDelta,
            _ => legacyDelta,
        };

        string legacySource = GetLastDeltaSource();
        string legacyDiagnostics = GetLastDeltaScore();
        if (legacyViewportDelta == 0)
        {
            return ScrollEstimate.Unreliable(
                NormalizeFailureReason(legacySource),
                staticLeading,
                staticTrailing,
                diagnostics: legacyDiagnostics);
        }

        return new ScrollEstimate(
            legacyViewportDelta,
            staticLeading,
            staticTrailing,
            true,
            legacySource == "FeatureTranslation" ? "FeatureTranslation" : "MatchAccepted",
            1.0,
            $"source={legacySource} {legacyDiagnostics}".TrimEnd());
    }

    /// <summary>
    /// 二维位移接口。当前纵向算法的结果映射到 Y；未来横向算法将映射到 X。
    /// 未启用的轴返回零向量，避免调用方把纵向 delta 错当作横向坐标。
    /// </summary>
    public static ScrollVector EstimateScrollVector(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        Action<string>? logger = null)
    {
        if (axis == ScrollAxis.Horizontal)
        {
            ScrollEstimate estimate = Estimate(previous, current, axis, null, 0, logger);
            // Preserve the legacy vector convention: content displacement, the inverse
            // of the viewport displacement exposed by the new Estimate API.
            return new ScrollVector(-estimate.ViewportDeltaAlong, 0);
        }

        int delta = EstimateScrollDelta(previous, current, logger, out _, out _);
        return new ScrollVector(0, delta);
    }

    private static ScrollEstimate EstimateHorizontal(
        Bitmap previous,
        Bitmap current,
        AxisDirection? expectedViewportDirection,
        int preferredMaxDelta,
        int preferredDelta,
        Action<string>? logger)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            SetLast("FrameSizeMismatch", $"prev={previous.Width}x{previous.Height} current={current.Width}x{current.Height}");
            return ScrollEstimate.Unreliable("FrameSizeMismatch", diagnostics: GetLastDeltaScore());
        }

        int alongLength = previous.Width;
        int crossLength = previous.Height;
        if (alongLength < 24 || crossLength < 12)
        {
            SetLast("InvalidSize", $"along={alongLength} cross={crossLength}");
            return ScrollEstimate.Unreliable("InvalidSize", diagnostics: GetLastDeltaScore());
        }

        const int sampleStep = 2;
        int sampledAlong = (alongLength + sampleStep - 1) / sampleStep;
        int sampledCross = (crossLength + sampleStep - 1) / sampleStep;
        int bufferSize = checked(sampledAlong * sampledCross);
        byte[] previousGray = ByteArrayPool.Rent(bufferSize);
        byte[] currentGray = ByteArrayPool.Rent(bufferSize);

        try
        {
            if (!ExtractGrayAlongAxis(
                    previous,
                    previousGray,
                    ScrollAxis.Horizontal,
                    sampledAlong,
                    sampledCross,
                    sampleStep,
                    sampleStep))
            {
                SetLast("ExtractPrevFailed", string.Empty);
                return ScrollEstimate.Unreliable("ExtractPrevFailed");
            }

            if (!ExtractGrayAlongAxis(
                    current,
                    currentGray,
                    ScrollAxis.Horizontal,
                    sampledAlong,
                    sampledCross,
                    sampleStep,
                    sampleStep))
            {
                SetLast("ExtractCurFailed", string.Empty);
                return ScrollEstimate.Unreliable("ExtractCurFailed");
            }

            DetectStaticBounds(
                previousGray,
                currentGray,
                sampledCross,
                sampledAlong,
                out int staticLeadingSampled,
                out int staticTrailingSampled);

            int maxStatic = sampledAlong / 3;
            staticLeadingSampled = Math.Min(staticLeadingSampled, maxStatic);
            staticTrailingSampled = Math.Min(staticTrailingSampled, maxStatic);
            if (staticLeadingSampled + staticTrailingSampled > sampledAlong / 2)
            {
                staticLeadingSampled = Math.Min(staticLeadingSampled, sampledAlong / 4);
                staticTrailingSampled = Math.Min(staticTrailingSampled, sampledAlong / 4);
            }

            int staticLeading = Math.Min(alongLength, staticLeadingSampled * sampleStep);
            int staticTrailing = Math.Min(alongLength - staticLeading, staticTrailingSampled * sampleStep);
            int dynamicLeading = staticLeadingSampled;
            int dynamicTrailing = sampledAlong - staticTrailingSampled;
            int effectiveAlong = dynamicTrailing - dynamicLeading;
            if (effectiveAlong < 12)
            {
                SetLast("TooSmallEffectiveArea", $"effective={effectiveAlong * sampleStep}");
                return ScrollEstimate.Unreliable(
                    "TooSmallEffectiveArea",
                    staticLeading,
                    staticTrailing,
                    diagnostics: GetLastDeltaScore());
            }

            int maxScroll = Math.Max(1, (int)(effectiveAlong * 0.78));
            int blockLength = Math.Clamp(
                (int)(effectiveAlong * 0.32),
                Math.Min(40, effectiveAlong),
                effectiveAlong);

            bool searchLeading = expectedViewportDirection != AxisDirection.Trailing;
            bool searchTrailing = expectedViewportDirection != AxisDirection.Leading;
            bool directionLocked = expectedViewportDirection.HasValue;
            double threshold = directionLocked
                ? ForcedDirectionMatchErrorThreshold
                : HorizontalProbeMatchErrorThreshold;
            double minConfidence = directionLocked
                ? ForcedDirectionMinMatchConfidence
                : MinMatchConfidence;

            MatchResult leadingMatch = new() { Error = 999.0, SecondError = 999.0 };
            MatchResult trailingMatch = new() { Error = 999.0, SecondError = 999.0 };
            int leadingDelta = 0;
            int trailingDelta = 0;

            // The normal path is direction-locked by the wheel hook. Use the last
            // accepted physical delta as a narrow, verified first pass. This avoids
            // both the expensive full-range search and the tendency of repeated cards
            // or table columns to win a global search at a different period.
            if (directionLocked)
            {
                int preferredLimit = preferredMaxDelta > 0
                    ? Math.Clamp((preferredMaxDelta + sampleStep - 1) / sampleStep, 1, maxScroll)
                    : maxScroll;
                int preferredCenter = preferredDelta > 0
                    ? Math.Clamp((preferredDelta + sampleStep - 1) / sampleStep, 1, maxScroll)
                    : 0;
                int radius = preferredCenter > 0
                    ? Math.Max(8, preferredCenter / 3)
                    : preferredLimit;

                bool localLeading = expectedViewportDirection == AxisDirection.Leading;
                int localPreviousStart = localLeading
                    ? dynamicLeading
                    : Math.Max(dynamicLeading, dynamicTrailing - blockLength);
                int localMin;
                int localMax;
                if (localLeading)
                {
                    localMin = preferredCenter > 0
                        ? Math.Max(localPreviousStart + 1, localPreviousStart + preferredCenter - radius)
                        : localPreviousStart;
                    localMax = Math.Min(
                        dynamicTrailing - blockLength,
                        preferredCenter > 0
                            ? localPreviousStart + preferredCenter + radius
                            : localPreviousStart + preferredLimit);
                }
                else
                {
                    localMin = Math.Max(
                        dynamicLeading,
                        preferredCenter > 0
                            ? localPreviousStart - preferredCenter - radius
                            : localPreviousStart - preferredLimit);
                    localMax = preferredCenter > 0
                        ? Math.Min(localPreviousStart - 1, localPreviousStart - preferredCenter + radius)
                        : localPreviousStart;
                }

                if (localMax >= localMin)
                {
                    MatchResult localMatch = localLeading
                        ? FastRowMatchUpwardEx(previousGray, currentGray, sampledCross, sampledAlong,
                            localPreviousStart, blockLength, localMin, localMax)
                        : FastRowMatchEx(previousGray, currentGray, sampledCross, sampledAlong,
                            localPreviousStart, blockLength, localMin, localMax);
                    int localDelta = localLeading
                        ? localMatch.Y - localPreviousStart
                        : localPreviousStart - localMatch.Y;

                    if (localDelta > 0
                        && TryBuildHorizontalEstimate(
                            previous, current, previousGray, currentGray,
                            sampledCross, sampledAlong,
                            staticLeading, staticTrailing,
                            localPreviousStart, blockLength,
                            localLeading, localDelta, localMatch,
                            expectedViewportDirection, threshold, minConfidence,
                            out ScrollEstimate localEstimate))
                    {
                        MarkStitched("GrayBlock-Horizontal-Local", localEstimate.Diagnostics);
                        return localEstimate;
                    }
                }
            }

            if (searchLeading)
            {
                int previousStart = dynamicLeading;
                int searchEnd = Math.Min(
                    dynamicTrailing - blockLength,
                    previousStart + maxScroll);
                leadingMatch = FastRowMatchUpwardEx(
                    previousGray,
                    currentGray,
                    sampledCross,
                    sampledAlong,
                    previousStart,
                    blockLength,
                    previousStart,
                    Math.Max(previousStart, searchEnd));
                if (IsTrustedMatch(leadingMatch, threshold, minConfidence))
                {
                    leadingDelta = leadingMatch.Y - previousStart;
                }
            }

            if (searchTrailing)
            {
                int previousStart = Math.Max(dynamicLeading, dynamicTrailing - blockLength);
                int searchStart = Math.Max(
                    dynamicLeading,
                    previousStart - maxScroll);
                trailingMatch = FastRowMatchEx(
                    previousGray,
                    currentGray,
                    sampledCross,
                    sampledAlong,
                    previousStart,
                    blockLength,
                    searchStart,
                    previousStart);
                if (IsTrustedMatch(trailingMatch, threshold, minConfidence))
                {
                    trailingDelta = previousStart - trailingMatch.Y;
                }
            }

            bool chooseLeading;
            if (expectedViewportDirection == AxisDirection.Leading)
            {
                chooseLeading = true;
            }
            else if (expectedViewportDirection == AxisDirection.Trailing)
            {
                chooseLeading = false;
            }
            else if (leadingDelta > 0 && trailingDelta == 0)
            {
                chooseLeading = true;
            }
            else if (trailingDelta > 0 && leadingDelta == 0)
            {
                chooseLeading = false;
            }
            else if (leadingDelta > 0 && trailingDelta > 0)
            {
                double leadingScore = leadingMatch.Error / Math.Max(0.05, leadingMatch.Confidence);
                double trailingScore = trailingMatch.Error / Math.Max(0.05, trailingMatch.Confidence);
                if (Math.Abs(leadingScore - trailingScore) < DirectionAmbiguousScoreGap)
                {
                    string detail = $"leading={leadingMatch.Error:F1}/{leadingMatch.Confidence:F2} trailing={trailingMatch.Error:F1}/{trailingMatch.Confidence:F2}";
                    SetLast("AmbiguousDirection", detail);
                    return ScrollEstimate.Unreliable(
                        "AmbiguousDirection",
                        staticLeading,
                        staticTrailing,
                        diagnostics: detail);
                }

                chooseLeading = leadingScore < trailingScore;
            }
            else
            {
                if (TryEstimateHorizontalContourFallback(
                        previous, current, previousGray, currentGray,
                        sampledCross, sampledAlong,
                        staticLeading, staticTrailing,
                        expectedViewportDirection, preferredMaxDelta, preferredDelta,
                        out ScrollEstimate contourEstimate))
                {
                    MarkStitched("Horizontal-Contour", contourEstimate.Diagnostics);
                    return contourEstimate;
                }

                string reason = (leadingMatch.Valid && leadingMatch.Ambiguous)
                    || (trailingMatch.Valid && trailingMatch.Ambiguous)
                    ? "RepeatedTexture"
                    : "NoStableMatch";
                string detail = $"leading={leadingMatch.Error:F1}/{leadingMatch.Confidence:F2} trailing={trailingMatch.Error:F1}/{trailingMatch.Confidence:F2}";
                SetLast(reason, detail);
                logger?.Invoke($"ImageStitcher: horizontal match rejected ({reason}).");
                return ScrollEstimate.Unreliable(
                    reason,
                    staticLeading,
                    staticTrailing,
                    diagnostics: detail);
            }

            MatchResult selected = chooseLeading ? leadingMatch : trailingMatch;
            int sampledDelta = chooseLeading ? leadingDelta : trailingDelta;
            int previousStartAlong = chooseLeading
                ? dynamicLeading
                : Math.Max(dynamicLeading, dynamicTrailing - blockLength);
            int currentStartAlong = selected.Y;
            if (sampledDelta <= 0 || !IsTrustedMatch(selected, threshold, minConfidence))
            {
                if (TryEstimateHorizontalContourFallback(
                        previous, current, previousGray, currentGray,
                        sampledCross, sampledAlong,
                        staticLeading, staticTrailing,
                        expectedViewportDirection, preferredMaxDelta, preferredDelta,
                        out ScrollEstimate contourEstimate))
                {
                    MarkStitched("Horizontal-Contour", contourEstimate.Diagnostics);
                    return contourEstimate;
                }

                string reason = selected.Valid && selected.Ambiguous ? "RepeatedTexture" : "LowConfidence";
                string detail = $"error={selected.Error:F1} confidence={selected.Confidence:F2}";
                SetLast(reason, detail);
                return ScrollEstimate.Unreliable(
                    reason,
                    staticLeading,
                    staticTrailing,
                    diagnostics: detail);
            }

            if (IsLowVariance(
                    previousGray,
                    sampledCross,
                    sampledAlong,
                    previousStartAlong,
                    blockLength))
            {
                if (TryEstimateHorizontalContourFallback(
                        previous, current, previousGray, currentGray,
                        sampledCross, sampledAlong,
                        staticLeading, staticTrailing,
                        expectedViewportDirection, preferredMaxDelta, preferredDelta,
                        out ScrollEstimate contourEstimate))
                {
                    MarkStitched("Horizontal-Contour", contourEstimate.Diagnostics);
                    return contourEstimate;
                }

                string detail = $"error={selected.Error:F1} confidence={selected.Confidence:F2}";
                SetLast("LowTexture", detail);
                return ScrollEstimate.Unreliable(
                    "LowTexture",
                    staticLeading,
                    staticTrailing,
                    diagnostics: detail);
            }

            if (!ValidateBlockConsensus(
                    previousGray,
                    currentGray,
                    sampledCross,
                    sampledAlong,
                    previousStartAlong,
                    currentStartAlong,
                    blockLength,
                    out double inlierRatio,
                    out double blockError))
            {
                if (TryEstimateHorizontalContourFallback(
                        previous, current, previousGray, currentGray,
                        sampledCross, sampledAlong,
                        staticLeading, staticTrailing,
                        expectedViewportDirection, preferredMaxDelta, preferredDelta,
                        out ScrollEstimate contourEstimate))
                {
                    MarkStitched("Horizontal-Contour", contourEstimate.Diagnostics);
                    return contourEstimate;
                }

                string detail = $"inliers={inlierRatio:F2} error={blockError:F1}";
                SetLast("BlockConsensusFailed", detail);
                return ScrollEstimate.Unreliable(
                    "BlockConsensusFailed",
                    staticLeading,
                    staticTrailing,
                    diagnostics: detail);
            }

            int initialContentShift = chooseLeading
                ? sampledDelta * sampleStep
                : -sampledDelta * sampleStep;
            if (!TryRefineFullResolutionSeamAlong(
                    previous,
                    current,
                    ScrollAxis.Horizontal,
                    initialContentShift,
                    staticLeading,
                    staticTrailing,
                    out int refinedContentShift,
                    out double seamError,
                    out double overlapRatio))
            {
                if (TryEstimateHorizontalContourFallback(
                        previous, current, previousGray, currentGray,
                        sampledCross, sampledAlong,
                        staticLeading, staticTrailing,
                        expectedViewportDirection, preferredMaxDelta, preferredDelta,
                        out ScrollEstimate contourEstimate))
                {
                    MarkStitched("Horizontal-Contour", contourEstimate.Diagnostics);
                    return contourEstimate;
                }

                string detail = $"seam={seamError:F1} overlap={overlapRatio:F2}";
                SetLast("SeamValidationFailed", detail);
                return ScrollEstimate.Unreliable(
                    "SeamValidationFailed",
                    staticLeading,
                    staticTrailing,
                    diagnostics: detail);
            }

            int viewportDelta = -refinedContentShift;
            if (viewportDelta == 0
                || (expectedViewportDirection == AxisDirection.Leading && viewportDelta >= 0)
                || (expectedViewportDirection == AxisDirection.Trailing && viewportDelta <= 0))
            {
                SetLast("DirectionMismatch", $"delta={viewportDelta}");
                return ScrollEstimate.Unreliable(
                    "DirectionMismatch",
                    staticLeading,
                    staticTrailing,
                    diagnostics: GetLastDeltaScore());
            }

            double score = Math.Clamp(
                selected.Confidence * 0.45
                + inlierRatio * 0.30
                + overlapRatio * 0.15
                + Math.Max(0.0, 1.0 - seamError / 18.0) * 0.10,
                0.0,
                1.0);
            string diagnostics = $"error={selected.Error:F1} confidence={selected.Confidence:F2} blocks={inlierRatio:F2}/{blockError:F1} seam={seamError:F1}/{overlapRatio:F2}";
            MarkStitched("GrayBlock-Horizontal", diagnostics);
            return new ScrollEstimate(
                viewportDelta,
                staticLeading,
                staticTrailing,
                true,
                "MatchAccepted",
                score,
                diagnostics);
        }
        finally
        {
            ByteArrayPool.Return(previousGray);
            ByteArrayPool.Return(currentGray);
        }
    }

    /// <summary>
    /// Verifies a direction-locked horizontal candidate before allowing the local fast
    /// path to commit it. A local search is only an optimisation; all the same texture,
    /// consensus and full-resolution seam checks still apply, so it cannot turn a
    /// repeated pattern into a guessed stitch.
    /// </summary>
    private static bool TryBuildHorizontalEstimate(
        Bitmap previous,
        Bitmap current,
        byte[] previousGray,
        byte[] currentGray,
        int sampledCross,
        int sampledAlong,
        int staticLeading,
        int staticTrailing,
        int previousStartAlong,
        int blockLength,
        bool chooseLeading,
        int sampledDelta,
        MatchResult selected,
        AxisDirection? expectedViewportDirection,
        double threshold,
        double minConfidence,
        out ScrollEstimate estimate)
    {
        estimate = default;
        if (sampledDelta <= 0 || !IsTrustedMatch(selected, threshold, minConfidence)) return false;
        if (IsLowVariance(previousGray, sampledCross, sampledAlong, previousStartAlong, blockLength)) return false;
        if (!ValidateBlockConsensus(
                previousGray, currentGray, sampledCross, sampledAlong,
                previousStartAlong, selected.Y, blockLength,
                out double inlierRatio, out double blockError)) return false;

        int initialContentShift = chooseLeading ? sampledDelta * 2 : -sampledDelta * 2;
        if (!TryRefineFullResolutionSeamAlong(
                previous, current, ScrollAxis.Horizontal,
                initialContentShift, staticLeading, staticTrailing,
                out int refinedContentShift, out double seamError, out double overlapRatio)) return false;

        int viewportDelta = -refinedContentShift;
        if (viewportDelta == 0
            || (expectedViewportDirection == AxisDirection.Leading && viewportDelta >= 0)
            || (expectedViewportDirection == AxisDirection.Trailing && viewportDelta <= 0)) return false;

        double score = Math.Clamp(
            selected.Confidence * 0.45
            + inlierRatio * 0.30
            + overlapRatio * 0.15
            + Math.Max(0.0, 1.0 - seamError / 18.0) * 0.10,
            0.0,
            1.0);
        string diagnostics = $"local error={selected.Error:F1} confidence={selected.Confidence:F2} blocks={inlierRatio:F2}/{blockError:F1} seam={seamError:F1}/{overlapRatio:F2}";
        estimate = new ScrollEstimate(
            viewportDelta, staticLeading, staticTrailing,
            true, "MatchAccepted", score, diagnostics);
        return true;
    }

    /// <summary>
    /// A deliberately narrow fallback for horizontal captures whose only usable cue is
    /// a broad luminance contour (for example a soft diagonal shadow or gradient).
    /// The normal block matcher correctly rejects those frames because they contain
    /// too few sharp, independently voting blocks. Here we require a unique local
    /// minimum, a unique non-local displacement and a second full-resolution seam
    /// check. When no input direction is available (for example a Photoshop scrollbar
    /// drag or a missing low-level hook), both directions are evaluated independently;
    /// only one reliable side, or a side that wins by a clear error margin, is accepted.
    /// A blank, broad error valley or periodic surface remains rejected.
    /// </summary>
    private static bool TryEstimateHorizontalContourFallback(
        Bitmap previous,
        Bitmap current,
        byte[] previousGray,
        byte[] currentGray,
        int sampledCross,
        int sampledAlong,
        int staticLeading,
        int staticTrailing,
        AxisDirection? expectedViewportDirection,
        int preferredMaxDelta,
        int preferredDelta,
        out ScrollEstimate estimate)
    {
        estimate = default;

        if (expectedViewportDirection is AxisDirection lockedDirection)
        {
            if (!TryEstimateHorizontalContourDirection(
                    previous, current, previousGray, currentGray,
                    sampledCross, sampledAlong,
                    staticLeading, staticTrailing,
                    lockedDirection, preferredMaxDelta, preferredDelta,
                    out HorizontalContourCandidate lockedCandidate))
            {
                return false;
            }

            estimate = lockedCandidate.Estimate;
            return true;
        }

        bool hasLeading = TryEstimateHorizontalContourDirection(
            previous, current, previousGray, currentGray,
            sampledCross, sampledAlong,
            staticLeading, staticTrailing,
            AxisDirection.Leading, preferredMaxDelta, preferredDelta,
            out HorizontalContourCandidate leading);
        bool hasTrailing = TryEstimateHorizontalContourDirection(
            previous, current, previousGray, currentGray,
            sampledCross, sampledAlong,
            staticLeading, staticTrailing,
            AxisDirection.Trailing, preferredMaxDelta, preferredDelta,
            out HorizontalContourCandidate trailing);

        if (hasLeading && !hasTrailing)
        {
            estimate = leading.Estimate with
            {
                Diagnostics = leading.Estimate.Diagnostics + " direction=leading opposing=rejected",
            };
            return true;
        }

        if (hasTrailing && !hasLeading)
        {
            estimate = trailing.Estimate with
            {
                Diagnostics = trailing.Estimate.Diagnostics + " direction=trailing opposing=rejected",
            };
            return true;
        }

        if (!hasLeading || !hasTrailing)
        {
            return false;
        }

        HorizontalContourCandidate better = leading.QualityCost <= trailing.QualityCost
            ? leading
            : trailing;
        HorizontalContourCandidate worse = leading.QualityCost <= trailing.QualityCost
            ? trailing
            : leading;
        double qualityGap = worse.QualityCost - better.QualityCost;
        double requiredGap = Math.Max(0.30, better.QualityCost * 0.30);
        double coarseGap = worse.CoarseError - better.CoarseError;
        double requiredCoarseGap = Math.Max(0.12, better.CoarseError * 0.20);
        if (qualityGap < requiredGap || coarseGap < requiredCoarseGap)
        {
            return false;
        }

        string selectedDirection = better.Estimate.ViewportDeltaAlong < 0 ? "leading" : "trailing";
        estimate = better.Estimate with
        {
            Diagnostics = better.Estimate.Diagnostics
                + $" direction={selectedDirection} opposingCost={worse.QualityCost:F2} gap={qualityGap:F2}",
        };
        return true;
    }

    private static bool TryEstimateHorizontalContourDirection(
        Bitmap previous,
        Bitmap current,
        byte[] previousGray,
        byte[] currentGray,
        int sampledCross,
        int sampledAlong,
        int staticLeading,
        int staticTrailing,
        AxisDirection direction,
        int preferredMaxDelta,
        int preferredDelta,
        out HorizontalContourCandidate candidate)
    {
        candidate = default;

        int maxMagnitude = preferredMaxDelta > 0
            ? Math.Clamp((preferredMaxDelta + 1) / 2, 1, (int)(sampledAlong * 0.78))
            : Math.Max(1, (int)(sampledAlong * 0.78));
        int minMagnitude = 1;
        if (preferredDelta > 0)
        {
            int center = Math.Clamp((preferredDelta + 1) / 2, 1, maxMagnitude);
            int radius = Math.Max(12, (int)Math.Ceiling(center * 0.55));
            minMagnitude = Math.Max(1, center - radius);
            maxMagnitude = Math.Min(maxMagnitude, center + radius);
        }

        List<(int Magnitude, double Error, double EdgeEnergy)> candidates = new();
        for (int magnitude = minMagnitude; magnitude <= maxMagnitude; magnitude++)
        {
            int contentShift = direction == AxisDirection.Trailing
                ? -magnitude
                : magnitude;
            if (TryMeasureHorizontalContour(
                    previousGray, currentGray, sampledAlong, sampledCross, contentShift,
                    staticLeading / 2, staticTrailing / 2,
                    alongStep: 4, crossStep: 8,
                    out double error, out double edgeEnergy))
            {
                candidates.Add((magnitude, error, edgeEnergy));
            }
        }

        if (candidates.Count == 0) return false;

        candidates.Sort(static (left, right) => left.Error.CompareTo(right.Error));
        (int Magnitude, double Error, double EdgeEnergy) best = candidates[0];
        double alternateError = double.PositiveInfinity;
        double nearbyError = double.PositiveInfinity;
        double valleyTolerance = Math.Max(0.08, best.Error * 0.20);
        int valleyMinimum = best.Magnitude;
        int valleyMaximum = best.Magnitude;
        for (int i = 1; i < candidates.Count; i++)
        {
            int distance = Math.Abs(candidates[i].Magnitude - best.Magnitude);
            if (distance >= 3 && candidates[i].Error < nearbyError)
            {
                nearbyError = candidates[i].Error;
            }
            if (distance >= 12 && candidates[i].Error < alternateError)
            {
                alternateError = candidates[i].Error;
            }
            if (candidates[i].Error <= best.Error + valleyTolerance)
            {
                valleyMinimum = Math.Min(valleyMinimum, candidates[i].Magnitude);
                valleyMaximum = Math.Max(valleyMaximum, candidates[i].Magnitude);
            }
        }

        int valleySpan = valleyMaximum - valleyMinimum;
        double nearbyGap = nearbyError - best.Error;
        double minimumNearbyGap = Math.Max(0.015, best.Error * 0.04);

        if (best.Error > 6.0
            // A quantised Photoshop gradient can change only once every few dozen
            // pixels.  It still carries a directional brightness ramp, whereas a
            // truly flat surface has zero edge energy and no unique alternate score.
            || best.EdgeEnergy < 0.005
            || !double.IsFinite(nearbyError)
            || nearbyGap < minimumNearbyGap
            || valleySpan > 10
            || !double.IsFinite(alternateError)
            || alternateError - best.Error < Math.Max(0.03, best.Error * 0.15))
        {
            return false;
        }

        int initialContentShift = direction == AxisDirection.Trailing
            ? -best.Magnitude * 2
            : best.Magnitude * 2;
        if (!TryRefineHorizontalContourSeam(
                previous, current, initialContentShift,
                staticLeading, staticTrailing,
                out int refinedContentShift,
                out double seamError,
                out double seamEdgeEnergy))
        {
            return false;
        }

        int viewportDelta = -refinedContentShift;
        if (viewportDelta == 0
            || (direction == AxisDirection.Leading && viewportDelta >= 0)
            || (direction == AxisDirection.Trailing && viewportDelta <= 0))
        {
            return false;
        }

        double score = Math.Clamp(
            0.58
            + Math.Min(0.18, seamEdgeEnergy / 12.0)
            + Math.Min(0.16, (alternateError - best.Error) / 12.0)
            + Math.Max(0.0, 0.08 - seamError / 120.0),
            0.0,
            0.90);
        string diagnostics = $"contour coarse={best.Error:F2}/{nearbyError:F2}/{alternateError:F2} valley={valleySpan} edge={best.EdgeEnergy:F2} seam={seamError:F2}/{seamEdgeEnergy:F2}";
        ScrollEstimate estimate = new(
            viewportDelta, staticLeading, staticTrailing,
            true, "MatchAccepted", score, diagnostics);
        candidate = new HorizontalContourCandidate(
            estimate,
            best.Error,
            alternateError,
            nearbyError,
            valleySpan,
            seamError);
        return true;
    }

    private static bool TryRefineHorizontalContourSeam(
        Bitmap previous,
        Bitmap current,
        int initialContentShift,
        int staticLeading,
        int staticTrailing,
        out int refinedContentShift,
        out double seamError,
        out double seamEdgeEnergy)
    {
        refinedContentShift = initialContentShift;
        seamError = double.PositiveInfinity;
        seamEdgeEnergy = 0.0;
        int alongLength = Math.Min(previous.Width, current.Width);
        int crossLength = Math.Min(previous.Height, current.Height);
        if (alongLength < 24 || crossLength < 12) return false;

        int sampledCross = (crossLength + 1) / 2;
        int bufferSize = checked(alongLength * sampledCross);
        byte[] previousGray = ByteArrayPool.Rent(bufferSize);
        byte[] currentGray = ByteArrayPool.Rent(bufferSize);
        try
        {
            if (!ExtractGrayAlongAxis(previous, previousGray, ScrollAxis.Horizontal,
                    alongLength, sampledCross, alongStep: 1, crossStep: 2)
                || !ExtractGrayAlongAxis(current, currentGray, ScrollAxis.Horizontal,
                    alongLength, sampledCross, alongStep: 1, crossStep: 2))
            {
                return false;
            }

            for (int offset = -4; offset <= 4; offset++)
            {
                int candidateShift = initialContentShift + offset;
                if (candidateShift == 0) continue;
                if (TryMeasureHorizontalContour(
                        previousGray, currentGray, alongLength, sampledCross, candidateShift,
                        staticLeading, staticTrailing,
                        alongStep: 2, crossStep: 4,
                        out double error, out double edgeEnergy)
                    && error < seamError)
                {
                    refinedContentShift = candidateShift;
                    seamError = error;
                    seamEdgeEnergy = edgeEnergy;
                }
            }
        }
        finally
        {
            ByteArrayPool.Return(previousGray);
            ByteArrayPool.Return(currentGray);
        }

        return seamError <= 7.0 && seamEdgeEnergy >= 0.003;
    }

    private static bool TryMeasureHorizontalContour(
        byte[] previous,
        byte[] current,
        int alongLength,
        int crossLength,
        int currentMinusPreviousAlong,
        int staticLeading,
        int staticTrailing,
        int alongStep,
        int crossStep,
        out double error,
        out double edgeEnergy)
    {
        error = double.PositiveInfinity;
        edgeEnergy = 0.0;
        int dynamicLeading = Math.Clamp(staticLeading, 0, alongLength);
        int dynamicTrailing = Math.Clamp(alongLength - staticTrailing, dynamicLeading, alongLength);
        int previousStart = Math.Max(dynamicLeading, dynamicLeading - currentMinusPreviousAlong);
        int previousEnd = Math.Min(dynamicTrailing, dynamicTrailing - currentMinusPreviousAlong);
        if (previousEnd - previousStart < (dynamicTrailing - dynamicLeading) * 0.22) return false;

        long valueDifferenceSum = 0;
        long edgeDifferenceSum = 0;
        long edgeSum = 0;
        int count = 0;
        for (int along = Math.Max(previousStart + 1, 1); along < previousEnd; along += alongStep)
        {
            int currentAlong = along + currentMinusPreviousAlong;
            int previousBase = along * crossLength;
            int previousBeforeBase = (along - 1) * crossLength;
            int currentBase = currentAlong * crossLength;
            int currentBeforeBase = (currentAlong - 1) * crossLength;
            for (int cross = 0; cross < crossLength; cross += crossStep)
            {
                int previousValue = previous[previousBase + cross];
                int currentValue = current[currentBase + cross];
                int previousEdge = Abs(previousValue - previous[previousBeforeBase + cross]);
                int currentEdge = Abs(currentValue - current[currentBeforeBase + cross]);
                valueDifferenceSum += Abs(previousValue - currentValue);
                edgeDifferenceSum += Abs(previousEdge - currentEdge);
                edgeSum += previousEdge + currentEdge;
                count++;
            }
        }

        if (count < 96) return false;
        error = (valueDifferenceSum + edgeDifferenceSum * 1.2) / count;
        edgeEnergy = (double)edgeSum / (count * 2);
        return true;
    }

    /// <summary>
    /// 估算两帧之间的滚动位移，并输出固定顶部/底部区域高度。
    /// </summary>
    public static int EstimateScrollDelta(Bitmap previous, Bitmap current, Action<string>? logger, out int staticTop, out int staticBottom)
    {
        return EstimateScrollDelta(previous, current, logger, out staticTop, out staticBottom, false, false);
    }

    /// <summary>
    /// 估算两帧之间的滚动位移。
    /// Probing 模式：正数=向上滚动，负数=向下滚动，0=无效。
    /// Locked 模式：forceUpward/forceDownward 任一为 true 时，正数=滚动幅度。
    /// </summary>
    public static int EstimateScrollDelta(
        Bitmap previous,
        Bitmap current,
        Action<string>? logger,
        out int staticTop,
        out int staticBottom,
        bool forceUpward,
        bool forceDownward)
    {
        return EstimateScrollDelta(previous, current, logger, out staticTop, out staticBottom,
            forceUpward, forceDownward, preferredMaxDelta: 0);
    }

    /// <summary>
    /// 带连续帧局部搜索提示的位移估算。preferredMaxDelta 只负责优先提出局部候选；
    /// 局部候选不存在或验证失败时仍回退全局搜索，不会截断真正的大跨度滚动。
    /// </summary>
    public static int EstimateScrollDelta(
        Bitmap previous,
        Bitmap current,
        Action<string>? logger,
        out int staticTop,
        out int staticBottom,
        bool forceUpward,
        bool forceDownward,
        int preferredMaxDelta)
    {
        int delta = EstimateScrollDeltaLegacy(previous, current, logger, out staticTop, out staticBottom,
            forceUpward, forceDownward, preferredMaxDelta);
        if (delta != 0) return delta;
        AxisDirection? direction = forceUpward ? AxisDirection.Leading : forceDownward ? AxisDirection.Trailing : null;
        var feature = EstimateFeatureTranslation(previous, current, ScrollAxis.Vertical, direction);
        return feature.IsReliable ? (direction.HasValue ? Math.Abs(feature.ViewportDeltaAlong) : feature.ViewportDeltaAlong) : 0;
    }

    private static int EstimateScrollDeltaLegacy(
        Bitmap previous,
        Bitmap current,
        Action<string>? logger,
        out int staticTop,
        out int staticBottom,
        bool forceUpward,
        bool forceDownward,
        int preferredMaxDelta)
    {
        staticTop = 0;
        staticBottom = 0;

        if (previous == null || current == null)
        {
            SetLast("InvalidFrame", string.Empty);
            return 0;
        }

        // 🎯 优先使用极速、抗重复纹理的 OpenCV 模板匹配算法
        int openCvDelta = EstimateScrollDeltaOpenCv(previous, current, forceUpward, forceDownward, out staticTop, out staticBottom);
        if (openCvDelta != 0)
        {
            return openCvDelta;
        }

        int width = Math.Min(previous.Width, current.Width);
        int height = Math.Min(previous.Height, current.Height);
        if (width <= 0 || height <= 0)
        {
            SetLast("InvalidSize", string.Empty);
            return 0;
        }

        const int sampleStep = 2;
        int sampledWidth = (width + sampleStep - 1) / sampleStep;
        int sampledHeight = (height + sampleStep - 1) / sampleStep;
        int bufferSize = sampledWidth * sampledHeight;

        byte[]? previousGray = null;
        byte[]? currentGray = null;

        try
        {
            previousGray = ByteArrayPool.Rent(bufferSize);
            currentGray = ByteArrayPool.Rent(bufferSize);

            if (!ExtractGrayToArray(previous, previousGray, sampledWidth, sampledHeight, sampleStep))
            {
                SetLast("ExtractPrevFailed", string.Empty);
                return 0;
            }
            if (!ExtractGrayToArray(current, currentGray, sampledWidth, sampledHeight, sampleStep))
            {
                SetLast("ExtractCurFailed", string.Empty);
                return 0;
            }

            DetectStaticBounds(previousGray, currentGray, sampledWidth, sampledHeight, out int staticTopSampled, out int staticBottomSampled);
            int maxStatic = sampledHeight / 3;
            if (staticTopSampled > maxStatic) staticTopSampled = maxStatic;
            if (staticBottomSampled > maxStatic) staticBottomSampled = maxStatic;
            if (staticTopSampled + staticBottomSampled > sampledHeight / 2)
            {
                staticTopSampled = Math.Min(staticTopSampled, sampledHeight / 4);
                staticBottomSampled = Math.Min(staticBottomSampled, sampledHeight / 4);
            }

            staticTop = staticTopSampled * sampleStep;
            staticBottom = staticBottomSampled * sampleStep;

            int effectiveHeight = sampledHeight - staticTopSampled - staticBottomSampled;
            if (effectiveHeight < 10)
            {
                SetLast("TooSmallEffectiveArea", string.Empty);
                return 0;
            }

            int maxScroll = (int)(effectiveHeight * 0.85);
            int preferredMaxScroll = preferredMaxDelta > 0
                ? Math.Clamp((preferredMaxDelta + sampleStep - 1) / sampleStep, 1, maxScroll)
                : 0;
            int searchHeight = (int)(effectiveHeight * 0.3);
            if (searchHeight < 80) searchHeight = 80;
            if (searchHeight > effectiveHeight) searchHeight = effectiveHeight;

            bool forcedDirection = forceUpward || forceDownward;
            double probeThreshold = forcedDirection ? ForcedDirectionMatchErrorThreshold : ProbeMatchErrorThreshold;
            double finalThreshold = forcedDirection ? ForcedDirectionMatchErrorThreshold : FinalMatchErrorThreshold;
            double minConfidence = forcedDirection ? ForcedDirectionMinMatchConfidence : MinMatchConfidence;

            int deltaUp = 0;
            int deltaDown = 0;
            MatchResult matchUp = new() { Error = 999.0, SecondError = 999.0 };
            MatchResult matchDown = new() { Error = 999.0, SecondError = 999.0 };
            MatchResult preferredMatchUp = new() { Error = 999.0, SecondError = 999.0 };
            MatchResult preferredMatchDown = new() { Error = 999.0, SecondError = 999.0 };
            int preferredDeltaUp = 0;
            int preferredDeltaDown = 0;

            if (forceUpward || !forceDownward)
            {
                int previousStartY = staticTopSampled;
                int searchStartInCurrent = previousStartY;
                int searchEndInCurrent = previousStartY + maxScroll;
                int dynamicBottom = sampledHeight - staticBottomSampled;
                if (searchEndInCurrent + searchHeight > dynamicBottom)
                {
                    searchEndInCurrent = dynamicBottom - searchHeight;
                }
                if (searchEndInCurrent < searchStartInCurrent)
                {
                    searchEndInCurrent = searchStartInCurrent;
                }

                if (forceUpward && preferredMaxScroll > 0)
                {
                    int preferredSearchEnd = Math.Min(searchEndInCurrent, previousStartY + preferredMaxScroll);
                    preferredMatchUp = FastRowMatchUpwardEx(
                        previousGray,
                        currentGray,
                        sampledWidth,
                        sampledHeight,
                        previousStartY,
                        searchHeight,
                        searchStartInCurrent,
                        preferredSearchEnd);
                    if (preferredMatchUp.Valid && IsTrustedMatch(preferredMatchUp, probeThreshold, minConfidence))
                    {
                        preferredDeltaUp = (preferredMatchUp.Y - previousStartY) * sampleStep;
                    }
                }

                matchUp = FastRowMatchUpwardEx(
                    previousGray,
                    currentGray,
                    sampledWidth,
                    sampledHeight,
                    previousStartY,
                    searchHeight,
                    searchStartInCurrent,
                    searchEndInCurrent);

                if (matchUp.Valid && IsTrustedMatch(matchUp, probeThreshold, minConfidence))
                {
                    deltaUp = (matchUp.Y - previousStartY) * sampleStep;
                }
            }

            if (forceDownward || !forceUpward)
            {
                int previousStartY = sampledHeight - staticBottomSampled - searchHeight;
                if (previousStartY < staticTopSampled)
                {
                    previousStartY = staticTopSampled;
                }

                int searchStartInCurrent = previousStartY - maxScroll;
                if (searchStartInCurrent < staticTopSampled)
                {
                    searchStartInCurrent = staticTopSampled;
                }
                int searchEndInCurrent = previousStartY;

                if (forceDownward && preferredMaxScroll > 0)
                {
                    int preferredSearchStart = Math.Max(searchStartInCurrent, previousStartY - preferredMaxScroll);
                    preferredMatchDown = FastRowMatchEx(
                        previousGray,
                        currentGray,
                        sampledWidth,
                        sampledHeight,
                        previousStartY,
                        searchHeight,
                        preferredSearchStart,
                        searchEndInCurrent);
                    if (preferredMatchDown.Valid && IsTrustedMatch(preferredMatchDown, probeThreshold, minConfidence))
                    {
                        preferredDeltaDown = (previousStartY - preferredMatchDown.Y) * sampleStep;
                    }
                }

                matchDown = FastRowMatchEx(
                    previousGray,
                    currentGray,
                    sampledWidth,
                    sampledHeight,
                    previousStartY,
                    searchHeight,
                    searchStartInCurrent,
                    searchEndInCurrent);

                if (matchDown.Valid && IsTrustedMatch(matchDown, probeThreshold, minConfidence))
                {
                    deltaDown = (previousStartY - matchDown.Y) * sampleStep;
                }
            }

            bool pickUp;
            if (forceUpward)
            {
                pickUp = true;
            }
            else if (forceDownward)
            {
                pickUp = false;
            }
            else
            {
                if (deltaUp > 0 && deltaDown == 0)
                {
                    pickUp = true;
                }
                else if (deltaDown > 0 && deltaUp == 0)
                {
                    pickUp = false;
                }
                else if (deltaUp > 0 && deltaDown > 0)
                {
                    double upScore = matchUp.Error / Math.Max(0.05, matchUp.Confidence);
                    double downScore = matchDown.Error / Math.Max(0.05, matchDown.Confidence);
                    if (Math.Abs(upScore - downScore) < DirectionAmbiguousScoreGap)
                    {
                        SetLast("AmbiguousDirection", $"up={matchUp.Error:F1}/{matchUp.Confidence:F2} down={matchDown.Error:F1}/{matchDown.Confidence:F2}");
                        return 0;
                    }

                    pickUp = upScore < downScore;
                }
                else
                {
                    if ((matchUp.Valid && matchUp.Ambiguous) || (matchDown.Valid && matchDown.Ambiguous))
                    {
                        SetLast("RepeatedTexture", $"up={matchUp.Error:F1}/{matchUp.Confidence:F2} down={matchDown.Error:F1}/{matchDown.Confidence:F2}");
                    }
                    else
                    {
                        SetLast("NoStableMatch", $"up={matchUp.Error:F1}/{matchUp.Confidence:F2} down={matchDown.Error:F1}/{matchDown.Confidence:F2}");
                    }

                    logger?.Invoke("ImageStitcher: 灰度梯度分块匹配未找到可信位移，跳过当前帧。");
                    return 0;
                }
            }

            if (pickUp)
            {
                bool isLowVariance = IsLowVariance(previousGray, sampledWidth, sampledHeight, staticTopSampled, searchHeight);
                if (isLowVariance)
                {
                    SetLast("LowTexture", $"err={matchUp.Error:F1} conf={matchUp.Confidence:F2}");
                    return 0;
                }

                if (forceUpward
                    && preferredDeltaUp > 0
                    && IsTrustedMatch(preferredMatchUp, finalThreshold, minConfidence))
                {
                    bool preferredBlockAccepted = ValidateBlockConsensus(previousGray, currentGray, sampledWidth, sampledHeight,
                        staticTopSampled, preferredMatchUp.Y, searchHeight, out double preferredInlierRatio, out double preferredBlockError);
                    bool strongBlockEvidence = preferredBlockAccepted
                        && preferredInlierRatio >= 0.80
                        && preferredBlockError <= 16.0;
                    bool blockOnlyEligible = strongBlockEvidence
                        && preferredDeltaUp >= Math.Max(24, (int)Math.Round(preferredMaxDelta * 0.60));
                    if (blockOnlyEligible)
                    {
                        MarkStitched("GrayBlock-Upward-LocalBlocks", $"err={preferredMatchUp.Error:F1} blocks={preferredInlierRatio:F2}/{preferredBlockError:F1} refined={preferredDeltaUp}");
                        return preferredDeltaUp;
                    }

                    int preferredShiftY = preferredDeltaUp;
                    bool preferredSeamAccepted = TryRefineFullResolutionSeam(previous, current,
                        preferredShiftY, staticTop, staticBottom,
                        out int refinedShiftY, out double preferredSeamError, out double preferredOverlapRatio);
                    bool strongFullFrameEvidence = preferredSeamAccepted
                        && (preferredBlockAccepted
                            || (preferredMatchUp.Confidence >= 0.45 && preferredSeamError <= 10.0));
                    if (strongFullFrameEvidence)
                    {
                        int refinedDelta = Math.Abs(refinedShiftY);
                        MarkStitched("GrayBlock-Upward-Local", $"err={preferredMatchUp.Error:F1} blocks={preferredInlierRatio:F2}/{preferredBlockError:F1} seam={preferredSeamError:F1}/{preferredOverlapRatio:F2} refined={refinedDelta}");
                        return refinedDelta;
                    }
                }

                if (forceUpward && preferredMaxScroll > 0 && preferredDeltaUp == 0)
                {
                    const int microSearchCenter = 16;
                    if (TryRefineFullResolutionSeam(previous, current,
                            initialShiftY: microSearchCenter,
                            staticTop: staticTop,
                            staticBottom: staticBottom,
                            out int microShiftY, out double microSeamError, out double microOverlapRatio,
                            refineRadius: microSearchCenter)
                        && microSeamError <= 8.0)
                    {
                        int microDelta = Math.Abs(microShiftY);
                        if (microDelta > 0 && microDelta <= microSearchCenter * 2)
                        {
                            MarkStitched("GrayBlock-Upward-Micro", $"seam={microSeamError:F1}/{microOverlapRatio:F2} refined={microDelta}");
                            return microDelta;
                        }
                    }
                }

                if (deltaUp > 0 && IsTrustedMatch(matchUp, finalThreshold, minConfidence))
                {
                    if (!ValidateBlockConsensus(previousGray, currentGray, sampledWidth, sampledHeight,
                            staticTopSampled, matchUp.Y, searchHeight, out double inlierRatio, out double blockError))
                    {
                        SetLast("BlockConsensusFailed", $"in={inlierRatio:F2} err={blockError:F1}");
                        return 0;
                    }

                    int shiftY = matchUp.Y - staticTopSampled;
                    if (!ValidateFullOverlapSeam(previousGray, currentGray, sampledWidth, sampledHeight,
                            shiftY, staticTopSampled, staticBottomSampled, out double seamError, out double overlapRatio))
                    {
                        SetLast("SeamValidationFailed", $"seam={seamError:F1} overlap={overlapRatio:F2}");
                        return 0;
                    }

                    MarkStitched("GrayBlock-Upward", $"err={matchUp.Error:F1} blocks={inlierRatio:F2}/{blockError:F1} seam={seamError:F1}/{overlapRatio:F2}");
                    return !forceUpward && !forceDownward ? -deltaUp : deltaUp;
                }

                if (matchUp.Valid)
                {
                    SetLast(matchUp.Ambiguous ? "RepeatedTexture" : "LowConfidence", $"err={matchUp.Error:F1} conf={matchUp.Confidence:F2} sec={matchUp.SecondError:F1}");
                }
            }
            else
            {
                int previousStartY = sampledHeight - staticBottomSampled - searchHeight;
                if (previousStartY < staticTopSampled) previousStartY = staticTopSampled;

                bool isLowVariance = IsLowVariance(previousGray, sampledWidth, sampledHeight, previousStartY, searchHeight);
                if (isLowVariance)
                {
                    SetLast("LowTexture", $"err={matchDown.Error:F1} conf={matchDown.Confidence:F2}");
                    return 0;
                }

                if (forceDownward && preferredMaxScroll > 0)
                {
                    List<LocalBandCandidate> alternateCandidates = FindLocalBandCandidates(
                        previousGray, currentGray, sampledWidth, sampledHeight,
                        staticTopSampled, staticBottomSampled, preferredMaxScroll,
                        upward: false,
                        threshold: finalThreshold,
                        minConfidence: minConfidence);
                    foreach (LocalBandCandidate candidate in alternateCandidates)
                    {
                        if (preferredDeltaDown > 0 && candidate.Delta >= preferredDeltaDown - 4) continue;

                        bool blockAccepted = ValidateBlockConsensus(previousGray, currentGray, sampledWidth, sampledHeight,
                            candidate.PreviousY, candidate.Match.Y, candidate.BlockHeight,
                            out double inlierRatio, out double blockError);
                        bool seamAccepted = TryRefineFullResolutionSeam(previous, current,
                            initialShiftY: -candidate.Delta,
                            staticTop: staticTop,
                            staticBottom: staticBottom,
                            out int refinedShiftY, out double seamError, out double overlapRatio);
                        if (seamAccepted
                            && (blockAccepted || (candidate.Match.Confidence >= 0.45 && seamError <= 10.0)))
                        {
                            int refinedDelta = Math.Abs(refinedShiftY);
                            MarkStitched("GrayBlock-Downward-Bands", $"err={candidate.Match.Error:F1} blocks={inlierRatio:F2}/{blockError:F1} seam={seamError:F1}/{overlapRatio:F2} refined={refinedDelta}");
                            return refinedDelta;
                        }
                    }
                }

                bool preferredBlockAccepted = false;
                bool preferredSeamAccepted = false;
                double preferredInlierRatio = 0.0;
                double preferredBlockError = 999.0;
                double preferredSeamError = 999.0;
                double preferredOverlapRatio = 0.0;
                if (forceDownward
                    && preferredDeltaDown > 0
                    && IsTrustedMatch(preferredMatchDown, finalThreshold, minConfidence))
                {
                    preferredBlockAccepted = ValidateBlockConsensus(previousGray, currentGray, sampledWidth, sampledHeight,
                        previousStartY, preferredMatchDown.Y, searchHeight, out preferredInlierRatio, out preferredBlockError);
                    bool strongBlockEvidence = preferredBlockAccepted
                        && preferredInlierRatio >= 0.80
                        && preferredBlockError <= 16.0;
                    bool blockOnlyEligible = strongBlockEvidence
                        && preferredDeltaDown >= Math.Max(24, (int)Math.Round(preferredMaxDelta * 0.60));
                    if (blockOnlyEligible)
                    {
                        MarkStitched("GrayBlock-Downward-LocalBlocks", $"err={preferredMatchDown.Error:F1} blocks={preferredInlierRatio:F2}/{preferredBlockError:F1} refined={preferredDeltaDown}");
                        return preferredDeltaDown;
                    }

                    int preferredShiftY = -preferredDeltaDown;
                    preferredSeamAccepted = TryRefineFullResolutionSeam(previous, current,
                        preferredShiftY, staticTop, staticBottom,
                        out int refinedShiftY, out preferredSeamError, out preferredOverlapRatio);
                    bool strongFullFrameEvidence = preferredSeamAccepted
                        && (preferredBlockAccepted
                            || (preferredMatchDown.Confidence >= 0.45 && preferredSeamError <= 10.0));
                    if (strongFullFrameEvidence)
                    {
                        int refinedDelta = Math.Abs(refinedShiftY);
                        MarkStitched("GrayBlock-Downward-Local", $"err={preferredMatchDown.Error:F1} blocks={preferredInlierRatio:F2}/{preferredBlockError:F1} seam={preferredSeamError:F1}/{preferredOverlapRatio:F2} refined={refinedDelta}");
                        return refinedDelta;
                    }
                }

                if (forceDownward && preferredMaxScroll > 0 && preferredDeltaDown == 0)
                {
                    const int microSearchCenter = 16;
                    if (TryRefineFullResolutionSeam(previous, current,
                            initialShiftY: -microSearchCenter,
                            staticTop: staticTop,
                            staticBottom: staticBottom,
                            out int microShiftY, out double microSeamError, out double microOverlapRatio,
                            refineRadius: microSearchCenter)
                        && microSeamError <= 8.0)
                    {
                        int microDelta = Math.Abs(microShiftY);
                        if (microDelta > 0 && microDelta <= microSearchCenter * 2)
                        {
                            MarkStitched("GrayBlock-Downward-Micro", $"seam={microSeamError:F1}/{microOverlapRatio:F2} refined={microDelta}");
                            return microDelta;
                        }
                    }
                }

                if (deltaDown > 0 && IsTrustedMatch(matchDown, finalThreshold, minConfidence))
                {
                    if (!ValidateBlockConsensus(previousGray, currentGray, sampledWidth, sampledHeight,
                            previousStartY, matchDown.Y, searchHeight, out double inlierRatio, out double blockError))
                    {
                        SetLast("BlockConsensusFailed", $"in={inlierRatio:F2} err={blockError:F1}");
                        return 0;
                    }

                    int shiftY = matchDown.Y - previousStartY;
                    if (!ValidateFullOverlapSeam(previousGray, currentGray, sampledWidth, sampledHeight,
                            shiftY, staticTopSampled, staticBottomSampled, out double seamError, out double overlapRatio))
                    {
                        SetLast("SeamValidationFailed", $"seam={seamError:F1} overlap={overlapRatio:F2}");
                        return 0;
                    }

                    string localDetail = preferredMatchDown.Valid
                        ? $" local={preferredDeltaDown}px/{preferredMatchDown.Error:F1}/{preferredMatchDown.Confidence:F2} block={preferredBlockAccepted}:{preferredInlierRatio:F2}/{preferredBlockError:F1} seam={preferredSeamAccepted}:{preferredSeamError:F1}/{preferredOverlapRatio:F2}"
                        : string.Empty;
                    MarkStitched("GrayBlock-Downward", $"err={matchDown.Error:F1} blocks={inlierRatio:F2}/{blockError:F1} seam={seamError:F1}/{overlapRatio:F2}{localDetail}");
                    return deltaDown;
                }

                if (matchDown.Valid)
                {
                    SetLast(matchDown.Ambiguous ? "RepeatedTexture" : "LowConfidence", $"err={matchDown.Error:F1} conf={matchDown.Confidence:F2} sec={matchDown.SecondError:F1}");
                }
            }

            return 0;
        }
        finally
        {
            if (previousGray != null) ByteArrayPool.Return(previousGray);
            if (currentGray != null) ByteArrayPool.Return(currentGray);
        }
    }

    private static MatchResult FastRowMatchUpwardEx(byte[] previous, byte[] current, int width, int height, int previousY, int blockHeight, int minCurrentY, int maxCurrentY)
    {
        return FastRowMatchCore(previous, current, width, height, previousY, blockHeight, minCurrentY, maxCurrentY, true);
    }

    private static MatchResult FastRowMatchEx(byte[] previous, byte[] current, int width, int height, int previousY, int blockHeight, int minCurrentY, int maxCurrentY)
    {
        return FastRowMatchCore(previous, current, width, height, previousY, blockHeight, minCurrentY, maxCurrentY, false);
    }

    private static List<LocalBandCandidate> FindLocalBandCandidates(
        byte[] previous,
        byte[] current,
        int width,
        int height,
        int staticTop,
        int staticBottom,
        int preferredMaxScroll,
        bool upward,
        double threshold,
        double minConfidence)
    {
        List<LocalBandCandidate> candidates = new(3);
        int dynamicTop = Math.Clamp(staticTop, 0, height);
        int dynamicBottom = Math.Clamp(height - staticBottom, dynamicTop, height);
        int effectiveHeight = dynamicBottom - dynamicTop;
        if (effectiveHeight < 40 || preferredMaxScroll <= 0) return candidates;

        int bandHeight = Math.Clamp(effectiveHeight / 5, 40, Math.Max(40, effectiveHeight / 3));
        int lastStart = Math.Max(dynamicTop, dynamicBottom - bandHeight);
        int middleStart = dynamicTop + Math.Max(0, (effectiveHeight - bandHeight) / 2);
        int[] starts = [dynamicTop, middleStart, lastStart];

        foreach (int previousY in starts.Distinct())
        {
            MatchResult match;
            int delta;
            if (upward)
            {
                int searchEnd = Math.Min(lastStart, previousY + preferredMaxScroll);
                match = FastRowMatchUpwardEx(previous, current, width, height,
                    previousY, bandHeight, previousY, searchEnd);
                delta = (match.Y - previousY) * 2;
            }
            else
            {
                int searchStart = Math.Max(dynamicTop, previousY - preferredMaxScroll);
                match = FastRowMatchEx(previous, current, width, height,
                    previousY, bandHeight, searchStart, previousY);
                delta = (previousY - match.Y) * 2;
            }

            if (delta <= 0 || !IsTrustedMatch(match, threshold, minConfidence)) continue;
            int existing = candidates.FindIndex(candidate => Math.Abs(candidate.Delta - delta) <= 4);
            if (existing >= 0)
            {
                if (match.Error < candidates[existing].Match.Error)
                {
                    candidates[existing] = new LocalBandCandidate(match, previousY, bandHeight, delta);
                }
            }
            else
            {
                candidates.Add(new LocalBandCandidate(match, previousY, bandHeight, delta));
            }
        }

        candidates.Sort((left, right) => left.Delta.CompareTo(right.Delta));
        return candidates;
    }

    private static bool IsTrustedMatch(MatchResult match, double threshold, double minConfidence)
    {
        if (!match.Valid) return false;
        if (match.Error >= threshold) return false;
        if (match.Ambiguous) return false;
        if (match.Confidence < minConfidence) return false;
        return true;
    }

    private static MatchResult FastRowMatchCore(byte[] previous, byte[] current, int width, int height, int previousY, int blockHeight, int minCurrentY, int maxCurrentY, bool upward)
    {
        MatchResult result = new()
        {
            Y = upward ? minCurrentY : maxCurrentY,
            Error = 999.0,
            SecondError = 999.0,
            Confidence = 0.0,
            Ambiguous = true,
            Valid = false,
        };

        if (previous == null || current == null || width <= 0 || height <= 0 || blockHeight <= 0) return result;
        if (minCurrentY < 0) minCurrentY = 0;
        if (maxCurrentY < minCurrentY) maxCurrentY = minCurrentY;
        if (maxCurrentY + blockHeight > height) maxCurrentY = height - blockHeight;
        if (minCurrentY < 0 || maxCurrentY < minCurrentY) return result;
        if (previousY < 0 || previousY + blockHeight > height) return result;

        // 行哈希预筛选：计算参考区域若干行的简易哈希，跳过明显不匹配的 Y 位置，
        // 避免对每个候选位置都做全宽像素比较。对于搜索范围大的场景可显著降低计算量。
        int hashRows = Math.Min(4, blockHeight);
        int hashStep = Math.Max(1, width / 8);
        long refHash = 0;
        for (int row = 0; row < hashRows; row += 2)
        {
            int rowIdx = (previousY + row) * width;
            for (int x = 0; x < width; x += hashStep)
            {
                refHash += previous[rowIdx + x];
            }
        }
        long refHashAvg = refHash / Math.Max(1, (hashRows / 2) * ((width + hashStep - 1) / hashStep));
        long hashTolerance = Math.Max(8, refHashAvg / 3);

        long bestScore = long.MaxValue;
        long secondScore = long.MaxValue;
        int bestY = upward ? minCurrentY : maxCurrentY;
        int bestCount = 0;
        int separation = Math.Max(4, blockHeight / 12);

        const int stepX = 2;
        const int stepY = 2;
        int start = upward ? minCurrentY : maxCurrentY;
        int end = upward ? maxCurrentY : minCurrentY;
        int increment = upward ? 1 : -1;

        for (int currentY = start; upward ? currentY <= end : currentY >= end; currentY += increment)
        {
            // 行哈希快速过滤
            long curHash = 0;
            for (int row = 0; row < hashRows; row += 2)
            {
                int rowIdx = (currentY + row) * width;
                for (int x = 0; x < width; x += hashStep)
                {
                    curHash += current[rowIdx + x];
                }
            }
            long curHashAvg = curHash / Math.Max(1, (hashRows / 2) * ((width + hashStep - 1) / hashStep));
            if (Math.Abs(curHashAvg - refHashAvg) > hashTolerance) continue;

            long score = 0;
            int count = 0;
            bool bad = false;
            bool canCompeteAsSecond = bestScore != long.MaxValue
                && Math.Abs(currentY - bestY) >= separation;
            long currentMax = bestScore == long.MaxValue
                ? long.MaxValue
                : canCompeteAsSecond
                    ? secondScore
                    : bestScore;

            for (int row = 0; row < blockHeight; row += stepY)
            {
                int previousRowY = previousY + row;
                int currentRowY = currentY + row;
                if (previousRowY <= 0 || currentRowY <= 0 || previousRowY >= height || currentRowY >= height) continue;

                int previousIndex = previousRowY * width;
                int currentIndex = currentRowY * width;
                int previousPrevRowIndex = (previousRowY - 1) * width;
                int currentPrevRowIndex = (currentRowY - 1) * width;

                for (int x = 1; x < width; x += stepX)
                {
                    int previousValue = previous[previousIndex + x];
                    int currentValue = current[currentIndex + x];
                    int valueDiff = Abs(previousValue - currentValue);

                    int previousEdgeX = Abs(previousValue - previous[previousIndex + x - 1]);
                    int currentEdgeX = Abs(currentValue - current[currentIndex + x - 1]);
                    int edgeXDiff = Abs(previousEdgeX - currentEdgeX);

                    int previousEdgeY = Abs(previousValue - previous[previousPrevRowIndex + x]);
                    int currentEdgeY = Abs(currentValue - current[currentPrevRowIndex + x]);
                    int edgeYDiff = Abs(previousEdgeY - currentEdgeY);

                    score += (long)valueDiff * valueDiff + (long)edgeXDiff * edgeXDiff / 2 + (long)edgeYDiff * edgeYDiff / 2;
                    count++;
                }

                if (score >= currentMax && currentMax != long.MaxValue)
                {
                    bad = true;
                    break;
                }
            }

            if (bad || count == 0) continue;

            if (score < bestScore)
            {
                if (Math.Abs(currentY - bestY) >= separation)
                {
                    secondScore = bestScore;
                }

                bestScore = score;
                bestY = currentY;
                bestCount = count;
            }
            else if (Math.Abs(currentY - bestY) >= separation && score < secondScore)
            {
                secondScore = score;
            }
        }

        if (bestScore == long.MaxValue || bestCount == 0) return result;

        double bestAverage = Math.Sqrt((double)bestScore / bestCount);
        bool hasIndependentSecond = secondScore != long.MaxValue;
        double secondAverage = hasIndependentSecond ? Math.Sqrt((double)secondScore / bestCount) : 999.0;
        double gap = hasIndependentSecond ? secondAverage - bestAverage : 0.0;
        double ratioConfidence = hasIndependentSecond
            ? secondAverage / Math.Max(0.1, bestAverage) - 1.0
            : 0.0;
        double gapConfidence = hasIndependentSecond
            ? gap / Math.Max(1.0, bestAverage)
            : 0.0;
        double confidence = Math.Max(ratioConfidence, gapConfidence);
        // A missing independent runner-up is absence of uniqueness evidence, not
        // perfect confidence. This commonly happened after the hash prefilter on a
        // smooth gradient and turned an arbitrary surviving offset into confidence=1.
        bool ambiguous = !hasIndependentSecond || confidence < 0.18 || gap < 1.2;

        result.Y = bestY;
        result.Error = bestAverage;
        result.SecondError = secondAverage;
        result.Confidence = confidence;
        result.Ambiguous = ambiguous;
        result.Valid = true;
        return result;
    }

    /// <summary>
    /// 使用同一套灰度+梯度评分对全局候选做分块一致性验证。
    /// 每个块只在候选附近小范围搜索；动态控件、滚动条和重复局部会成为离群块。
    /// </summary>
    private static bool ValidateBlockConsensus(
        byte[] previous,
        byte[] current,
        int width,
        int height,
        int previousY,
        int currentY,
        int blockHeight,
        out double inlierRatio,
        out double averageError)
    {
        inlierRatio = 0.0;
        averageError = 999.0;
        if (width < 12 || height < 12 || blockHeight < 8) return false;
        if (previousY < 0 || previousY + blockHeight > height) return false;
        if (currentY < 0 || currentY + blockHeight > height) return false;

        int marginX = Math.Max(2, width / 20);
        int usableWidth = width - marginX * 2;
        if (usableWidth < 8) return false;

        const int columns = 6;
        const int rows = 2;
        int blockWidth = Math.Max(4, usableWidth / columns);
        int blockPartHeight = Math.Max(4, blockHeight / rows);
        int texturedBlocks = 0;
        int stableBlocks = 0;
        int inlierBlocks = 0;
        int inlierColumnMask = 0;
        double inlierErrorSum = 0.0;

        for (int by = 0; by < rows; by++)
        {
            int localY = by * blockPartHeight;
            int localHeight = by == rows - 1 ? blockHeight - localY : blockPartHeight;
            if (localHeight < 4) continue;

            for (int bx = 0; bx < columns; bx++)
            {
                int startX = marginX + bx * blockWidth;
                int endX = bx == columns - 1 ? width - marginX : Math.Min(width - marginX, startX + blockWidth);
                if (endX - startX < 4) continue;

                // 低纹理块不参与投票。
                long gradientSum = 0;
                int gradientCount = 0;
                for (int y = 0; y < localHeight; y += 3)
                {
                    int rowIndex = (previousY + localY + y) * width;
                    for (int x = startX + 1; x < endX; x += 3)
                    {
                        gradientSum += Abs(previous[rowIndex + x] - previous[rowIndex + x - 1]);
                        gradientCount++;
                    }
                }
                if (gradientCount == 0 || (double)gradientSum / gradientCount < 2.0) continue;

                texturedBlocks++;
                int bestOffset = 0;
                double bestError = double.MaxValue;
                for (int offset = -2; offset <= 2; offset++)
                {
                    int candidateY = currentY + offset;
                    if (candidateY < 1 || candidateY + localY + localHeight > height) continue;

                    long score = 0;
                    int count = 0;
                    for (int y = 0; y < localHeight; y += 2)
                    {
                        int prevRow = (previousY + localY + y) * width;
                        int curRow = (candidateY + localY + y) * width;
                        for (int x = startX + 1; x < endX; x += 2)
                        {
                            int pv = previous[prevRow + x];
                            int cv = current[curRow + x];
                            int valueDiff = Abs(pv - cv);
                            int prevEdge = Abs(pv - previous[prevRow + x - 1]);
                            int curEdge = Abs(cv - current[curRow + x - 1]);
                            int edgeDiff = Abs(prevEdge - curEdge);
                            score += valueDiff + edgeDiff;
                            count++;
                        }
                    }

                    if (count == 0) continue;
                    double error = (double)score / count;
                    if (error < bestError)
                    {
                        bestError = error;
                        bestOffset = offset;
                    }
                }

                if (bestError == double.MaxValue) continue;

                // GIF、视频、头像动画等区域在相邻帧中会产生很高误差。它们不是滚动位移证据，
                // 因此不应拉低静态文字/边框块的共识比例，只作为动态离群块忽略。
                if (bestError <= 48.0)
                {
                    stableBlocks++;
                }

                if (Math.Abs(bestOffset) <= 1 && bestError <= 30.0)
                {
                    inlierBlocks++;
                    inlierColumnMask |= 1 << bx;
                    inlierErrorSum += bestError;
                }
            }
        }

        if (texturedBlocks < 3 || stableBlocks < 3 || inlierBlocks < 3) return false;

        int coveredColumns = 0;
        for (int mask = inlierColumnMask; mask != 0; mask >>= 1)
        {
            coveredColumns += mask & 1;
        }
        if (coveredColumns < 2) return false;

        inlierRatio = (double)inlierBlocks / stableBlocks;
        averageError = inlierErrorSum / inlierBlocks;
        return inlierRatio >= 0.55 && averageError <= 24.0;
    }

    /// <summary>
    /// 在候选位移应用后，对两帧完整重叠区域进行独立接缝校验。
    /// 使用分块误差的截尾均值，允许少量动画/光标块存在，但要求大部分文字和图像边缘连续。
    /// </summary>
    private static bool ValidateFullOverlapSeam(
        byte[] previous,
        byte[] current,
        int width,
        int height,
        int currentMinusPreviousY,
        int staticTop,
        int staticBottom,
        out double seamError,
        out double overlapRatio)
    {
        seamError = 999.0;
        overlapRatio = 0.0;
        if (width < 12 || height < 12 || currentMinusPreviousY == 0) return false;

        int dynamicTop = Math.Clamp(staticTop, 0, height);
        int dynamicBottom = Math.Clamp(height - staticBottom, dynamicTop, height);
        int dynamicHeight = dynamicBottom - dynamicTop;
        if (dynamicHeight < 12) return false;

        int previousStartY = Math.Max(dynamicTop, dynamicTop - currentMinusPreviousY);
        int previousEndY = Math.Min(dynamicBottom, dynamicBottom - currentMinusPreviousY);
        int overlapHeight = previousEndY - previousStartY;
        if (overlapHeight <= 0) return false;
        overlapRatio = (double)overlapHeight / dynamicHeight;
        if (overlapRatio < 0.22) return false;

        int marginX = Math.Max(2, width / 20);
        int usableWidth = width - marginX * 2;
        if (usableWidth < 8) return false;

        const int columns = 6;
        const int rows = 4;
        int tileWidth = Math.Max(4, usableWidth / columns);
        int tileHeight = Math.Max(4, overlapHeight / rows);
        List<double> tileErrors = new(columns * rows);

        for (int ty = 0; ty < rows; ty++)
        {
            int localY = ty * tileHeight;
            int partHeight = ty == rows - 1 ? overlapHeight - localY : tileHeight;
            if (partHeight < 4) continue;

            for (int tx = 0; tx < columns; tx++)
            {
                int startX = marginX + tx * tileWidth;
                int endX = tx == columns - 1 ? width - marginX : Math.Min(width - marginX, startX + tileWidth);
                if (endX - startX < 4) continue;

                long score = 0;
                long texture = 0;
                int count = 0;
                for (int y = 0; y < partHeight; y += 2)
                {
                    int prevY = previousStartY + localY + y;
                    int curY = prevY + currentMinusPreviousY;
                    if (prevY <= 0 || curY <= 0 || prevY >= height || curY >= height) continue;
                    int prevRow = prevY * width;
                    int curRow = curY * width;

                    for (int x = startX + 1; x < endX; x += 2)
                    {
                        int pv = previous[prevRow + x];
                        int cv = current[curRow + x];
                        int valueDiff = Abs(pv - cv);
                        int prevEdgeX = Abs(pv - previous[prevRow + x - 1]);
                        int curEdgeX = Abs(cv - current[curRow + x - 1]);
                        int prevEdgeY = Abs(pv - previous[prevRow - width + x]);
                        int curEdgeY = Abs(cv - current[curRow - width + x]);
                        int edgeActivity = Math.Max(Math.Max(prevEdgeX, curEdgeX), Math.Max(prevEdgeY, curEdgeY));
                        if (edgeActivity < 4) continue;

                        int edgeDiff = Abs(prevEdgeX - curEdgeX) + Abs(prevEdgeY - curEdgeY);
                        score += valueDiff + edgeDiff;
                        texture += prevEdgeX + prevEdgeY;
                        count++;
                    }
                }

                // 接缝只由具有结构信息的文字/边框/图像边缘裁决，避免大面积同色背景
                // 把重复页面的错误位移稀释成看似很低的平均误差。
                if (count < 8 || (double)texture / count < 4.0) continue;
                tileErrors.Add((double)score / count);
            }
        }

        if (tileErrors.Count < 6) return false;
        tileErrors.Sort();
        int keep = Math.Max(4, (int)Math.Ceiling(tileErrors.Count * 0.70));
        double sum = 0.0;
        for (int i = 0; i < keep; i++) sum += tileErrors[i];
        seamError = sum / keep;
        return seamError <= 18.0;
    }

    private static bool TryRefineFullResolutionSeam(
        Bitmap previous,
        Bitmap current,
        int initialShiftY,
        int staticTop,
        int staticBottom,
        out int refinedShiftY,
        out double seamError,
        out double overlapRatio,
        int refineRadius = 4)
    {
        refinedShiftY = initialShiftY;
        seamError = 999.0;
        overlapRatio = 0.0;

        int width = Math.Min(previous.Width, current.Width);
        int height = Math.Min(previous.Height, current.Height);
        if (width < 12 || height < 12) return false;

        // 全局候选按 2x2 采样提出，只能返回偶数物理位移。真实浏览器平滑滚动经常产生
        // 117px 之类的奇数位移；用半分辨率候选直接做边缘接缝会留下 1px 错位并被误拒。
        // 这里仅把水平方向降采样，保留完整纵向分辨率，在候选附近做轻量精修。
        int sampledWidth = (width + 1) / 2;
        int bufferSize = sampledWidth * height;
        byte[] previousGray = ByteArrayPool.Rent(bufferSize);
        byte[] currentGray = ByteArrayPool.Rent(bufferSize);
        try
        {
            if (!ExtractGrayToArray(previous, previousGray, sampledWidth, height, stepX: 2, stepY: 1)
                || !ExtractGrayToArray(current, currentGray, sampledWidth, height, stepX: 2, stepY: 1))
            {
                return false;
            }

            refineRadius = Math.Clamp(refineRadius, 1, 32);
            for (int offset = -refineRadius; offset <= refineRadius; offset++)
            {
                int candidateShift = initialShiftY + offset;
                if (candidateShift == 0) continue;

                _ = ValidateFullOverlapSeam(previousGray, currentGray, sampledWidth, height,
                    candidateShift, staticTop, staticBottom,
                    out double candidateError, out double candidateOverlapRatio);
                if (candidateError < seamError)
                {
                    refinedShiftY = candidateShift;
                    seamError = candidateError;
                    overlapRatio = candidateOverlapRatio;
                }
            }
        }
        finally
        {
            ByteArrayPool.Return(previousGray);
            ByteArrayPool.Return(currentGray);
        }

        return seamError <= 18.0 && overlapRatio >= 0.22;
    }

    private static bool TryRefineFullResolutionSeamAlong(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        int initialContentShift,
        int staticLeading,
        int staticTrailing,
        out int refinedContentShift,
        out double seamError,
        out double overlapRatio,
        int refineRadius = 4)
    {
        refinedContentShift = initialContentShift;
        seamError = 999.0;
        overlapRatio = 0.0;

        int alongLength = axis == ScrollAxis.Horizontal
            ? Math.Min(previous.Width, current.Width)
            : Math.Min(previous.Height, current.Height);
        int crossLength = axis == ScrollAxis.Horizontal
            ? Math.Min(previous.Height, current.Height)
            : Math.Min(previous.Width, current.Width);
        if (alongLength < 12 || crossLength < 12) return false;

        // Preserve every along-axis pixel so odd smooth-scroll deltas can be refined.
        // Only the cross-axis is sampled. Horizontal frames are written directly into
        // transposed grayscale storage; no temporary rotated bitmap is allocated.
        int sampledCross = (crossLength + 1) / 2;
        int bufferSize = checked(alongLength * sampledCross);
        byte[] previousGray = ByteArrayPool.Rent(bufferSize);
        byte[] currentGray = ByteArrayPool.Rent(bufferSize);
        try
        {
            if (!ExtractGrayAlongAxis(
                    previous,
                    previousGray,
                    axis,
                    alongLength,
                    sampledCross,
                    alongStep: 1,
                    crossStep: 2)
                || !ExtractGrayAlongAxis(
                    current,
                    currentGray,
                    axis,
                    alongLength,
                    sampledCross,
                    alongStep: 1,
                    crossStep: 2))
            {
                return false;
            }

            refineRadius = Math.Clamp(refineRadius, 1, 32);
            for (int offset = -refineRadius; offset <= refineRadius; offset++)
            {
                int candidateShift = initialContentShift + offset;
                if (candidateShift == 0) continue;

                _ = ValidateFullOverlapSeam(
                    previousGray,
                    currentGray,
                    sampledCross,
                    alongLength,
                    candidateShift,
                    staticLeading,
                    staticTrailing,
                    out double candidateError,
                    out double candidateOverlapRatio);
                if (candidateError < seamError)
                {
                    refinedContentShift = candidateShift;
                    seamError = candidateError;
                    overlapRatio = candidateOverlapRatio;
                }
            }
        }
        finally
        {
            ByteArrayPool.Return(previousGray);
            ByteArrayPool.Return(currentGray);
        }

        return seamError <= 18.0 && overlapRatio >= 0.22;
    }

    private static void EnsureOpenCvCpuOnly()
    {
        if (Interlocked.Exchange(ref _openCvInitialized, 1) == 1) return;

        LongScrollMemoryDiagnostics.Log("OpenCV.BeforeFirstUse");
        try { OpenCvSharp.Cv2.SetNumThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 2)); } catch { }
        try { OpenCvSharp.Cv2.SetUseOptimized(true); } catch { }
        TryDisableOpenCvOpenCl();
        LongScrollMemoryDiagnostics.Log("OpenCV.AfterFirstUse");
    }

    private static void TryDisableOpenCvOpenCl()
    {
        // OpenCvSharp4 4.10 没有公开的 SetUseOpenCL / UseOpenCL API（经程序集元数据探测确认）。
        // 不要用反射扫描 Cv2 查找该方法：该版本下永远找不到，还会触发
        // NativeAOT 裁剪分析警告（IL2026/IL2065）。
        //
        // 若将来升级 OpenCvSharp 版本且需要禁用 OpenCL，请改用该版本公开的静态 API，
        // 例如 OpenCvSharp.Cv2.SetUseOpenCL(false)（如存在），不要再用反射。
    }

    /// <summary>
    /// 独立于位移估算的固定区检测入口：对相邻两帧计算顶部/底部（纵向）或
    /// 左/右（横向）的固定边界，并应用与全引擎一致的比例钳制。
    /// FastPath/WidePath 不负责检测固定区时会回填 0，由会话层用本方法兜底，
    /// 以保证自动剥离不再依赖"恰好命中全引擎"。
    /// </summary>
    public static void DetectStaticBoundsForPair(Bitmap previous, Bitmap current, ScrollAxis axis, out int staticLeading, out int staticTrailing)
    {
        staticLeading = 0;
        staticTrailing = 0;
        if (previous == null || current == null) return;
        try
        {
            EnsureOpenCvCpuOnly();
            int width = Math.Min(previous.Width, current.Width);
            int height = Math.Min(previous.Height, current.Height);
            if (width < 80 || height < 80) return;

            using OpenCvSharp.Mat prevMat = CreateGrayMatForOpenCv(previous, width, height);
            using OpenCvSharp.Mat currMat = CreateGrayMatForOpenCv(current, width, height);
            if (prevMat.Empty() || currMat.Empty()) return;

            if (axis == ScrollAxis.Horizontal)
            {
                DetectStaticBoundsHorizontal(prevMat, currMat, out int detectedLeft, out int detectedRight);
                int maxStatic = width / 3;
                int halfW = width / 2;
                if (detectedLeft > maxStatic) detectedLeft = maxStatic;
                if (detectedRight > maxStatic) detectedRight = maxStatic;
                if (detectedLeft + detectedRight > halfW)
                {
                    detectedLeft = Math.Min(detectedLeft, width / 4);
                    detectedRight = Math.Min(detectedRight, width / 4);
                }
                staticLeading = detectedLeft;
                staticTrailing = detectedRight;
            }
            else
            {
                DetectStaticBounds(prevMat, currMat, out int detectedTop, out int detectedBottom);
                int maxStatic = height / 3;
                int halfH = height / 2;
                if (detectedTop > maxStatic) detectedTop = maxStatic;
                if (detectedBottom > maxStatic) detectedBottom = maxStatic;
                if (detectedTop + detectedBottom > halfH)
                {
                    detectedTop = Math.Min(detectedTop, height / 4);
                    detectedBottom = Math.Min(detectedBottom, height / 4);
                }
                staticLeading = detectedTop;
                staticTrailing = detectedBottom;
            }
        }
        catch
        {
            staticLeading = 0;
            staticTrailing = 0;
        }
    }

    private static int EstimateScrollDeltaOpenCv(
        Bitmap previous,
        Bitmap current,
        ScrollAxis axis,
        bool forceLeading,
        bool forceTrailing,
        out int staticLeading,
        out int staticTrailing,
        int maxDeltaHint = 0,
        bool aggressiveChecks = false)
    {
        staticLeading = 0;
        staticTrailing = 0;
        try
        {
            EnsureOpenCvCpuOnly();
            if (previous == null || current == null) return 0;
            int width = Math.Min(previous.Width, current.Width);
            int height = Math.Min(previous.Height, current.Height);
            if (width < 80 || height < 80) return 0;

            using OpenCvSharp.Mat prevMat = CreateGrayMatForOpenCv(previous, width, height);
            using OpenCvSharp.Mat currMat = CreateGrayMatForOpenCv(current, width, height);
            if (prevMat.Empty() || currMat.Empty()) return 0;

            // 🎯 在模板匹配前计算静态边界 (固定层)，避免固定栏干扰匹配
            if (axis == ScrollAxis.Horizontal)
            {
                DetectStaticBoundsHorizontal(prevMat, currMat, out int detectedLeft, out int detectedRight);
                int maxStatic = width / 3;
                if (detectedLeft > maxStatic) detectedLeft = maxStatic;
                if (detectedRight > maxStatic) detectedRight = maxStatic;
                if (detectedLeft + detectedRight > width / 2)
                {
                    detectedLeft = Math.Min(detectedLeft, width / 4);
                    detectedRight = Math.Min(detectedRight, width / 4);
                }
                staticLeading = detectedLeft;
                staticTrailing = detectedRight;
            }
            else
            {
                DetectStaticBounds(prevMat, currMat, out int detectedTop, out int detectedBottom);
                int maxStatic = height / 3;
                if (detectedTop > maxStatic) detectedTop = maxStatic;
                if (detectedBottom > maxStatic) detectedBottom = maxStatic;
                if (detectedTop + detectedBottom > height / 2)
                {
                    detectedTop = Math.Min(detectedTop, height / 4);
                    detectedBottom = Math.Min(detectedBottom, height / 4);
                }
                staticLeading = detectedTop;
                staticTrailing = detectedBottom;
            }

            // 调用 OpenCV 模板匹配求沿轴有符号位移（rawDelta > 0 = 向后/下/右，
            // < 0 = 向前/上/左）。方向剪枝：滚轮/滚动条已给出方向提示时只跑
            // 对应候选，省一半模板匹配。
            // 金字塔两段式：先 1/2 分辨率粗匹配——模板匹配成本 ∝ 像素数×搜索
            // 范围，半分辨率把这块最大开销降低约 75%；失败（低纹理/细小文字）
            // 再回退全分辨率兜底，最坏成本与单段式相同。匹配器内部全部使用
            // 相对比例（模板高度=有效高度 20%、左右裁边 15%），对降采样天然
            // 免疫；半分辨率位移 ×2 的 ±1px 舍入误差由随后的全分辨率接缝精修
            // （纵向 ±2px / 横向 ±4px 邻域）纠正。
            int preferredDir = forceTrailing ? 1 : (forceLeading ? -1 : 0);
            int rawDelta = 0;
            if (width >= 320 && height >= 320)
            {
                using OpenCvSharp.Mat prevHalf = new OpenCvSharp.Mat();
                using OpenCvSharp.Mat currHalf = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.Resize(prevMat, prevHalf, new OpenCvSharp.Size(0, 0), 0.5, 0.5, OpenCvSharp.InterpolationFlags.Area);
                OpenCvSharp.Cv2.Resize(currMat, currHalf, new OpenCvSharp.Size(0, 0), 0.5, 0.5, OpenCvSharp.InterpolationFlags.Area);
                int halfDelta = TemplateMatchStitcher.CalculateScrollOffset(
                    prevHalf, currHalf, axis, staticLeading / 2, staticTrailing / 2,
                    preferredDirection: preferredDir,
                    maxDelta: Math.Max(8, maxDeltaHint / 2),
                    aggressiveChecks: aggressiveChecks);
                if (halfDelta != 0) rawDelta = halfDelta * 2;
            }
            if (rawDelta == 0)
            {
                rawDelta = TemplateMatchStitcher.CalculateScrollOffset(
                    prevMat, currMat, axis, staticLeading, staticTrailing,
                    preferredDirection: preferredDir,
                    maxDelta: maxDeltaHint,
                    aggressiveChecks: aggressiveChecks);
            }

            if (rawDelta == 0) return 0;

            // 🎯 接缝与全重叠区校验：防止在水平单调渐变/无纹理列上误匹配
            if (axis == ScrollAxis.Horizontal)
            {
                int contentShiftX = -rawDelta;
                if (!TryRefineHorizontalContourSeam(previous, current, contentShiftX, staticLeading, staticTrailing,
                        out int refinedShiftX, out double seamError, out double seamEdgeEnergy))
                {
                    return 0; // 接缝或纹理验证失败，放弃候选
                }
                rawDelta = -refinedShiftX;
            }
            else
            {
                int contentShiftY = -rawDelta;
                if (TryRefineFullResolutionSeam(previous, current, contentShiftY, staticLeading, staticTrailing,
                        out int refinedShiftY, out double seamError, out double overlapRatio, refineRadius: 2))
                {
                    rawDelta = -refinedShiftY;
                }
                else
                {
                    // 与横向同款硬校验：纵向模板匹配在重复纹理（列表/卡片）上容易
                    // 锁定错周期，接缝/重叠校验（seamError<=18 且 overlap>=0.22）
                    // 失败时必须放弃候选，不能让未经验证的位移直通画布。
                    return 0;
                }
            }

            // 遵循 LegacyLongScrollSession 的核心正负号契约：
            // 1. 正数 (signedViewportDelta > 0) ➔ 往后滚动页面 (frameDirUp = false)
            // 2. 负数 (signedViewportDelta < 0) ➔ 往前滚动页面 (frameDirUp = true)

            if (forceLeading)
            {
                if (rawDelta < 0)
                {
                    lock (_stateLock) _algorithmName = axis == ScrollAxis.Horizontal ? "OpenCV-MatchTemplate-Left" : "OpenCV-MatchTemplate-Up";
                    MarkStitched(axis == ScrollAxis.Horizontal ? "OpenCV-Left" : "OpenCV-Upward", $"delta={rawDelta}");
                    return Math.Abs(rawDelta);
                }
                return 0;
            }
            if (forceTrailing)
            {
                if (rawDelta > 0)
                {
                    lock (_stateLock) _algorithmName = axis == ScrollAxis.Horizontal ? "OpenCV-MatchTemplate-Right" : "OpenCV-MatchTemplate-Down";
                    MarkStitched(axis == ScrollAxis.Horizontal ? "OpenCV-Right" : "OpenCV-Downward", $"delta={rawDelta}");
                    return Math.Abs(rawDelta);
                }
                return 0;
            }

            if (rawDelta > 0)
            {
                // 往后/下/右滚动页面 ➔ 返回正数，使 frameDirUp = false (向尾部追加像素)
                lock (_stateLock) _algorithmName = axis == ScrollAxis.Horizontal ? "OpenCV-MatchTemplate-Right" : "OpenCV-MatchTemplate-Down";
                MarkStitched(axis == ScrollAxis.Horizontal ? "OpenCV-Right" : "OpenCV-Downward", $"delta={rawDelta}");
                return rawDelta;
            }
            else
            {
                // 往前/上/左滚动页面 ➔ 返回负数，使 frameDirUp = true (向首部延伸像素)
                lock (_stateLock) _algorithmName = axis == ScrollAxis.Horizontal ? "OpenCV-MatchTemplate-Left" : "OpenCV-MatchTemplate-Up";
                MarkStitched(axis == ScrollAxis.Horizontal ? "OpenCV-Left" : "OpenCV-Upward", $"delta={rawDelta}");
                return rawDelta;
            }
        }
        catch (Exception ex)
        {
            SetLast("OpenCV-Failed", ex.GetType().Name);
            return 0;
        }
    }

    private static int EstimateScrollDeltaOpenCv(
        Bitmap previous,
        Bitmap current,
        bool forceUpward,
        bool forceDownward,
        out int staticTop,
        out int staticBottom)
    {
        return EstimateScrollDeltaOpenCv(
            previous,
            current,
            ScrollAxis.Vertical,
            forceUpward,
            forceDownward,
            out staticTop,
            out staticBottom);
    }

    /// <summary>
    /// LockBits 锁定格式：捕获帧为 32bpp 系时直接用源格式，避免 GDI+ 隐式整帧格式转换拷贝；
    /// 其他罕见格式仍回退 32bppArgb 以保证按 4 字节 BGRX 解析的代码布局正确。
    /// </summary>
    private static PixelFormat ResolveLockFormat(Bitmap bitmap)
    {
        PixelFormat f = bitmap.PixelFormat;
        if (f == PixelFormat.Format32bppRgb || f == PixelFormat.Format32bppArgb || f == PixelFormat.Format32bppPArgb)
            return f;
        return PixelFormat.Format32bppArgb;
    }

    private static OpenCvSharp.Mat CreateGrayMatForOpenCv(Bitmap bitmap, int width, int height)
    {
        if (bitmap == null) return new OpenCvSharp.Mat();

        BitmapData? data = null;
        try
        {
            int w = Math.Min(width, bitmap.Width);
            int h = Math.Min(height, bitmap.Height);
            if (w <= 0 || h <= 0) return new OpenCvSharp.Mat();

            data = bitmap.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                ResolveLockFormat(bitmap));

            using var bgraMat = OpenCvSharp.Mat.FromPixelData(h, w, OpenCvSharp.MatType.CV_8UC4, data.Scan0, data.Stride);
            OpenCvSharp.Mat grayMat = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(bgraMat, grayMat, OpenCvSharp.ColorConversionCodes.BGRA2GRAY);
            return grayMat;
        }
        catch
        {
            return new OpenCvSharp.Mat();
        }
        finally
        {
            if (data != null)
            {
                try { bitmap.UnlockBits(data); } catch { }
            }
        }
    }

    // 固定区抗噪容错：允许固定条带内部最多连续 NonMatchLineTolerance 行不匹配
    //（动画、悬停高亮、阴影、滚动条移动等），连续超过即视为离开固定区。
    // 这样顶部/底部固定导航栏不会因个别行的抗锯齿/动画差异而被判为 0。
    private const int NonMatchLineTolerance = 4;

    private static void DetectStaticBounds(byte[] previous, byte[] current, int width, int height, out int topY, out int bottomY)
    {
        topY = 0;
        bottomY = 0;
        int halfH = height / 2;

        topY = ScanFixedLeading(previous, current, width, height, halfH);
        bottomY = ScanFixedTrailing(previous, current, width, height, halfH);
    }

    // 从顶部扫描固定区：topY 等于最后一个匹配行之后的行号。
    // 允许固定区内部零星失配行（动画/高亮/阴影），但不将它们计入高度；
    // 连续失配超过 NonMatchLineTolerance 判定离开固定区。
    private static int ScanFixedLeading(byte[] previous, byte[] current, int width, int height, int halfH)
    {
        int lastMatched = -1;
        int badStreak = 0;
        for (int y = 0; y < halfH; y++)
        {
            if (IsRowMatch(previous, current, width, y, y))
            {
                lastMatched = y;
                badStreak = 0;
            }
            else if (lastMatched >= 0)
            {
                if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                break;
            }
            else
            {
                break;
            }
        }
        return lastMatched + 1;
    }

    private static int ScanFixedTrailing(byte[] previous, byte[] current, int width, int height, int halfH)
    {
        int lastMatched = -1;
        int badStreak = 0;
        for (int y = 0; y < halfH; y++)
        {
            int rowY = height - 1 - y;
            if (IsRowMatch(previous, current, width, rowY, rowY))
            {
                lastMatched = rowY;
                badStreak = 0;
            }
            else if (lastMatched >= 0)
            {
                if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                break;
            }
            else
            {
                break;
            }
        }
        return lastMatched < 0 ? 0 : (height - 1 - lastMatched + 1);
    }

    private static bool IsRowMatch(byte[] previous, byte[] current, int width, int previousY, int currentY)
    {
        int previousBase = previousY * width;
        int currentBase = currentY * width;
        int diffSum = 0;
        int matched = 0;
        int sampled = 0;

        for (int x = 0; x < width; x += 4)
        {
            int diff = Abs(previous[previousBase + x] - current[currentBase + x]);
            diffSum += Math.Min(diff, 32);
            if (diff <= 8) matched++;
            sampled++;
        }

        if (sampled == 0) return false;
        double matchedRatio = (double)matched / sampled;
        double averageDiff = (double)diffSum / sampled;
        return matchedRatio >= 0.85 && averageDiff <= 10.0;
    }

    private static void DetectStaticBounds(OpenCvSharp.Mat prevMat, OpenCvSharp.Mat currMat, out int topY, out int bottomY)
    {
        topY = 0;
        bottomY = 0;
        if (prevMat == null || currMat == null || prevMat.Empty() || currMat.Empty()) return;

        int width = prevMat.Width;
        int height = prevMat.Height;
        int halfH = height / 2;

        unsafe
        {
            byte* prevPtr = (byte*)prevMat.Data.ToPointer();
            byte* currPtr = (byte*)currMat.Data.ToPointer();
            long prevStep = prevMat.Step();
            long currStep = currMat.Step();

            int lastMatchedTop = -1;
            int badStreak = 0;
            for (int y = 0; y < halfH; y++)
            {
                byte* pRow = prevPtr + y * prevStep;
                byte* cRow = currPtr + y * currStep;
                if (IsRowMatchFast(pRow, cRow, width))
                {
                    lastMatchedTop = y;
                    badStreak = 0;
                }
                else if (lastMatchedTop >= 0)
                {
                    if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                    break;
                }
                else
                {
                    break;
                }
            }
            topY = lastMatchedTop + 1;

            int lastMatchedBottom = -1;
            badStreak = 0;
            for (int y = 0; y < halfH; y++)
            {
                int rowY = height - 1 - y;
                byte* pRow = prevPtr + rowY * prevStep;
                byte* cRow = currPtr + rowY * currStep;
                if (IsRowMatchFast(pRow, cRow, width))
                {
                    lastMatchedBottom = rowY;
                    badStreak = 0;
                }
                else if (lastMatchedBottom >= 0)
                {
                    if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                    break;
                }
                else
                {
                    break;
                }
            }
            bottomY = lastMatchedBottom < 0 ? 0 : (height - 1 - lastMatchedBottom + 1);
        }
    }

    private static unsafe bool IsRowMatchFast(byte* prevRow, byte* currRow, int width)
    {
        int diffSum = 0;
        int matched = 0;
        int sampled = 0;

        for (int x = 0; x < width; x += 4)
        {
            int diff = Math.Abs(prevRow[x] - currRow[x]);
            diffSum += Math.Min(diff, 32);
            if (diff <= 8) matched++;
            sampled++;
        }

        if (sampled == 0) return false;
        double matchedRatio = (double)matched / sampled;
        double averageDiff = (double)diffSum / sampled;
        return matchedRatio >= 0.85 && averageDiff <= 10.0;
    }

    private static void DetectStaticBoundsHorizontal(OpenCvSharp.Mat prevMat, OpenCvSharp.Mat currMat, out int leftX, out int rightX)
    {
        leftX = 0;
        rightX = 0;
        if (prevMat == null || currMat == null || prevMat.Empty() || currMat.Empty()) return;

        int width = prevMat.Width;
        int height = prevMat.Height;
        int halfW = width / 2;

        unsafe
        {
            byte* prevPtr = (byte*)prevMat.Data.ToPointer();
            byte* currPtr = (byte*)currMat.Data.ToPointer();
            long prevStep = prevMat.Step();
            long currStep = currMat.Step();

            int lastMatchedLeft = -1;
            int badStreak = 0;
            for (int x = 0; x < halfW; x++)
            {
                if (IsColMatchFast(prevPtr, currPtr, prevStep, currStep, height, x))
                {
                    lastMatchedLeft = x;
                    badStreak = 0;
                }
                else if (lastMatchedLeft >= 0)
                {
                    if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                    break;
                }
                else
                {
                    break;
                }
            }
            leftX = lastMatchedLeft + 1;

            int lastMatchedRight = -1;
            badStreak = 0;
            for (int x = 0; x < halfW; x++)
            {
                int colX = width - 1 - x;
                if (IsColMatchFast(prevPtr, currPtr, prevStep, currStep, height, colX))
                {
                    lastMatchedRight = colX;
                    badStreak = 0;
                }
                else if (lastMatchedRight >= 0)
                {
                    if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                    break;
                }
                else
                {
                    break;
                }
            }
            rightX = lastMatchedRight < 0 ? 0 : (width - 1 - lastMatchedRight + 1);
        }
    }

    private static unsafe bool IsColMatchFast(byte* prevPtr, byte* currPtr, long prevStep, long currStep, int height, int x)
    {
        int diffSum = 0;
        int matched = 0;
        int sampled = 0;

        for (int y = 0; y < height; y += 4)
        {
            int pVal = prevPtr[y * prevStep + x];
            int cVal = currPtr[y * currStep + x];
            int diff = Math.Abs(pVal - cVal);
            diffSum += Math.Min(diff, 32);
            if (diff <= 8) matched++;
            sampled++;
        }

        if (sampled == 0) return false;
        double matchedRatio = (double)matched / sampled;
        double averageDiff = (double)diffSum / sampled;
        return matchedRatio >= 0.85 && averageDiff <= 10.0;
    }

    private static void DetectStaticBoundsHorizontal(byte[] previous, byte[] current, int width, int height, out int leftX, out int rightX)
    {
        leftX = 0;
        rightX = 0;
        int halfW = width / 2;

        int lastMatchedLeft = -1;
        int badStreak = 0;
        for (int x = 0; x < halfW; x++)
        {
            if (IsColumnMatch(previous, current, width, height, x, x))
            {
                lastMatchedLeft = x;
                badStreak = 0;
            }
            else if (lastMatchedLeft >= 0)
            {
                if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                break;
            }
            else
            {
                break;
            }
        }
        leftX = lastMatchedLeft + 1;

        int lastMatchedRight = -1;
        badStreak = 0;
        for (int x = 0; x < halfW; x++)
        {
            int colX = width - 1 - x;
            if (IsColumnMatch(previous, current, width, height, colX, colX))
            {
                lastMatchedRight = colX;
                badStreak = 0;
            }
            else if (lastMatchedRight >= 0)
            {
                if (badStreak < NonMatchLineTolerance) { badStreak++; continue; }
                break;
            }
            else
            {
                break;
            }
        }
        rightX = lastMatchedRight < 0 ? 0 : (width - 1 - lastMatchedRight + 1);
    }

    private static bool IsColumnMatch(byte[] previous, byte[] current, int width, int height, int previousX, int currentX)
    {
        int diffSum = 0;
        int matched = 0;
        int sampled = 0;

        for (int y = 0; y < height; y += 4)
        {
            int prevIndex = y * width + previousX;
            int currIndex = y * width + currentX;
            int diff = Abs(previous[prevIndex] - current[currIndex]);
            diffSum += Math.Min(diff, 32);
            if (diff <= 8) matched++;
            sampled++;
        }

        if (sampled == 0) return false;
        double matchedRatio = (double)matched / sampled;
        double averageDiff = (double)diffSum / sampled;
        return matchedRatio >= 0.85 && averageDiff <= 10.0;
    }

    private static bool IsLowVariance(byte[] gray, int width, int height, int startY, int blockHeight)
    {
        if (startY < 0) startY = 0;
        if (blockHeight <= 0) return true;

        long sum = 0;
        long squareSum = 0;
        int count = 0;
        long gradientSum = 0;
        int gradientCount = 0;

        int endY = startY + blockHeight;
        if (endY > height) endY = height;

        for (int y = startY; y < endY; y += 2)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x += 2)
            {
                int value = gray[rowBase + x];
                sum += value;
                squareSum += value * value;
                count++;

                if (x + 2 < width)
                {
                    int gradient = Abs(value - gray[rowBase + x + 2]);
                    gradientSum += gradient;
                    gradientCount++;
                }
            }
        }

        if (count == 0) return true;

        double mean = (double)sum / count;
        double variance = (double)squareSum / count - mean * mean;
        double averageGradient = gradientCount > 0 ? (double)gradientSum / gradientCount : 0.0;

        return variance < 6.0 && averageGradient < 3.0;
    }

    private static bool ExtractGrayToArray(Bitmap bitmap, byte[] destination, int sampledWidth, int sampledHeight, int step)
    {
        return ExtractGrayToArray(bitmap, destination, sampledWidth, sampledHeight, step, step);
    }

    private static bool ExtractGrayToArray(Bitmap bitmap, byte[] destination, int sampledWidth, int sampledHeight, int stepX, int stepY)
    {
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                ResolveLockFormat(bitmap));

            int width = bitmap.Width;
            int height = bitmap.Height;
            int stride = data.Stride;
            int absoluteStride = stride > 0 ? stride : -stride;

            byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(absoluteStride);
            try
            {
                for (int sampledY = 0; sampledY < sampledHeight; sampledY++)
                {
                    int sourceY = sampledY * stepY;
                    if (sourceY >= height) break;

                    IntPtr sourceRow = IntPtr.Add(data.Scan0, stride > 0 ? sourceY * stride : (height - 1 - sourceY) * absoluteStride);
                    Marshal.Copy(sourceRow, rowBuffer, 0, absoluteStride);

                    int destinationBase = sampledY * sampledWidth;
                    for (int sampledX = 0; sampledX < sampledWidth; sampledX++)
                    {
                        int sourceX = sampledX * stepX;
                        if (sourceX >= width) break;

                        int offset = sourceX * 4;
                        if (offset + 2 >= absoluteStride) break;

                        int gray = (rowBuffer[offset + 2] * 38 + rowBuffer[offset + 1] * 75 + rowBuffer[offset] * 15) >> 7;
                        destination[destinationBase + sampledX] = (byte)gray;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rowBuffer);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (data != null)
            {
                bitmap.UnlockBits(data);
            }
        }
    }

    private static bool ExtractGrayAlongAxis(
        Bitmap bitmap,
        byte[] destination,
        ScrollAxis axis,
        int sampledAlong,
        int sampledCross,
        int alongStep,
        int crossStep)
    {
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                ResolveLockFormat(bitmap));

            int stride = data.Stride;
            int absoluteStride = Math.Abs(stride);
            byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(absoluteStride);
            try
            {
                if (axis == ScrollAxis.Vertical)
                {
                    for (int along = 0; along < sampledAlong; along++)
                    {
                        int sourceY = along * alongStep;
                        if (sourceY >= bitmap.Height) break;
                        IntPtr sourceRow = IntPtr.Add(
                            data.Scan0,
                            stride > 0
                                ? sourceY * stride
                                : (bitmap.Height - 1 - sourceY) * absoluteStride);
                        Marshal.Copy(sourceRow, rowBuffer, 0, absoluteStride);

                        int destinationBase = along * sampledCross;
                        for (int cross = 0; cross < sampledCross; cross++)
                        {
                            int sourceX = cross * crossStep;
                            if (sourceX >= bitmap.Width) break;
                            int offset = sourceX * 4;
                            destination[destinationBase + cross] = ToGray(rowBuffer, offset);
                        }
                    }
                }
                else
                {
                    // A horizontal along-axis row corresponds to one source X coordinate.
                    // Read each source scanline once and scatter it into the axis-major
                    // buffer, keeping every matching row contiguous for FastRowMatchCore.
                    for (int cross = 0; cross < sampledCross; cross++)
                    {
                        int sourceY = cross * crossStep;
                        if (sourceY >= bitmap.Height) break;
                        IntPtr sourceRow = IntPtr.Add(
                            data.Scan0,
                            stride > 0
                                ? sourceY * stride
                                : (bitmap.Height - 1 - sourceY) * absoluteStride);
                        Marshal.Copy(sourceRow, rowBuffer, 0, absoluteStride);

                        for (int along = 0; along < sampledAlong; along++)
                        {
                            int sourceX = along * alongStep;
                            if (sourceX >= bitmap.Width) break;
                            int offset = sourceX * 4;
                            destination[along * sampledCross + cross] = ToGray(rowBuffer, offset);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rowBuffer);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (data != null)
            {
                bitmap.UnlockBits(data);
            }
        }
    }

    private static byte ToGray(byte[] bgraRow, int offset)
    {
        int gray = (bgraRow[offset + 2] * 38
            + bgraRow[offset + 1] * 75
            + bgraRow[offset] * 15) >> 7;
        return (byte)gray;
    }

    internal static void MarkStitched(string source, string score)
    {
        lock (_stateLock)
        {
            _stitchingCount++;
            SetLast(source, score);
        }
    }

    private static void SetLast(string source, string score)
    {
        lock (_stateLock)
        {
            _lastDeltaSource = source;
            _lastDeltaScore = score;
        }
    }

    private static string NormalizeFailureReason(string source)
    {
        return string.IsNullOrWhiteSpace(source) || source == "None"
            ? "NoStableMatch"
            : source;
    }

    private static int Abs(int value)
    {
        return value < 0 ? -value : value;
    }
}
