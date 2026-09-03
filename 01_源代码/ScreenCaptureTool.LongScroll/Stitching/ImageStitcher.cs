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
public static class ImageStitcher
{
    private const double ProbeMatchErrorThreshold = 20.0;
    private const double FinalMatchErrorThreshold = 18.0;
    private const double ForcedDirectionMatchErrorThreshold = 24.0;
    private const double MinMatchConfidence = 0.18;
    private const double ForcedDirectionMinMatchConfidence = 0.12;
    private const double DirectionAmbiguousScoreGap = 1.5;
    private const double OpenCvMinInlierRatio = 0.42;
    private const double OpenCvForcedDirectionMinInlierRatio = 0.35;

    private static string _algorithmName = "Hybrid-NoOpenCv";
    private static string _lastDeltaSource = "None";
    private static string _lastDeltaScore = string.Empty;
    private static int _stitchingCount;
    private static int _openCvInitialized;

    public static string AlgorithmName => _algorithmName;

    public static string LastDeltaSource => _lastDeltaSource;

    public static string LastDeltaScore => _lastDeltaScore;

    public static int StitchingCount => _stitchingCount;

    public static string GetAlgorithmName() => _algorithmName;

    public static string GetLastDeltaSource() => _lastDeltaSource;

    public static string GetLastDeltaScore() => _lastDeltaScore;

    public static string GetAlgoCountersText() => "N:" + _stitchingCount;

    private struct MatchResult
    {
        public int Y;
        public double Error;
        public double SecondError;
        public double Confidence;
        public bool Ambiguous;
        public bool Valid;
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
        staticTop = 0;
        staticBottom = 0;

        if (previous == null || current == null)
        {
            SetLast("InvalidFrame", string.Empty);
            return 0;
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

                    int cvDelta = EstimateScrollDeltaOpenCv(previous, current, forceUpward, forceDownward);
                    if (cvDelta != 0) return cvDelta;
                    logger?.Invoke("ImageStitcher: 非 OpenCV 匹配未找到可信位移，OpenCV 回退当前降级为不可用。 ");
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

                if (deltaUp > 0 && IsTrustedMatch(matchUp, finalThreshold, minConfidence))
                {
                    MarkStitched("Upward", $"err={matchUp.Error:F1} conf={matchUp.Confidence:F2} sec={matchUp.SecondError:F1}");
                    return deltaUp;
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

                if (deltaDown > 0 && IsTrustedMatch(matchDown, finalThreshold, minConfidence))
                {
                    MarkStitched("Downward", $"err={matchDown.Error:F1} conf={matchDown.Confidence:F2} sec={matchDown.SecondError:F1}");
                    return !forceUpward && !forceDownward ? -deltaDown : deltaDown;
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
            long score = 0;
            int count = 0;
            bool bad = false;
            long currentMax = bestScore == long.MaxValue ? long.MaxValue : bestScore;

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
        double secondAverage = secondScore == long.MaxValue ? 999.0 : Math.Sqrt((double)secondScore / bestCount);
        double gap = secondAverage >= 999.0 ? 999.0 : secondAverage - bestAverage;
        double ratioConfidence = secondAverage >= 999.0 ? 1.0 : secondAverage / Math.Max(0.1, bestAverage) - 1.0;
        double gapConfidence = gap >= 999.0 ? 1.0 : gap / Math.Max(1.0, bestAverage);
        double confidence = Math.Max(ratioConfidence, gapConfidence);
        bool ambiguous = secondAverage < 999.0 && (confidence < 0.18 || gap < 1.2);

        result.Y = bestY;
        result.Error = bestAverage;
        result.SecondError = secondAverage;
        result.Confidence = confidence;
        result.Ambiguous = ambiguous;
        result.Valid = true;
        return result;
    }

    private static void EnsureOpenCvCpuOnly()
    {
        if (Interlocked.Exchange(ref _openCvInitialized, 1) == 1) return;

        LongScrollMemoryDiagnostics.Log("OpenCV.BeforeFirstUse");
        try { OpenCvSharp.Cv2.SetNumThreads(1); } catch { }
        try { OpenCvSharp.Cv2.SetUseOptimized(true); } catch { }
        TryDisableOpenCvOpenCl();
        LongScrollMemoryDiagnostics.Log("OpenCV.AfterFirstUse");
    }

    private static void TryDisableOpenCvOpenCl()
    {
        // OpenCvSharp4 4.10 没有公开的 SetUseOpenCL / UseOpenCL API（经程序集元数据探测确认），
        // 原来用反射遍历 OpenCvSharp.Cv2 程序集查找 SetUseOpenCL 的写法在该版本下实际找不到
        // 任何匹配方法（永远静默失败），属于无效代码，还会在 NativeAOT 裁剪分析时触发
        // IL2026/IL2065 警告。这里移除反射扫描，行为与原来完全一致。
        //
        // 若将来升级 OpenCvSharp 版本且需要禁用 OpenCL，请改用该版本公开的静态 API，
        // 例如 OpenCvSharp.Cv2.SetUseOpenCL(false)（如存在），不要再用反射。
    }

    private static int EstimateScrollDeltaOpenCv(Bitmap previous, Bitmap current, bool forceUpward, bool forceDownward)
    {
        try
        {
            EnsureOpenCvCpuOnly();
            if (previous == null || current == null) return 0;
            int width = Math.Min(previous.Width, current.Width);
            int height = Math.Min(previous.Height, current.Height);
            if (width < 80 || height < 80) return 0;

            const int sample = 2;
            using OpenCvSharp.Mat previousMat = CreateGrayMatForOpenCv(previous, width, height, sample);
            using OpenCvSharp.Mat currentMat = CreateGrayMatForOpenCv(current, width, height, sample);
            using OpenCvSharp.ORB orb = OpenCvSharp.ORB.Create(900);
            using OpenCvSharp.Mat previousDesc = new();
            using OpenCvSharp.Mat currentDesc = new();

            if (previousMat == null || currentMat == null || previousMat.Empty() || currentMat.Empty()) return 0;

            orb.DetectAndCompute(previousMat, null, out OpenCvSharp.KeyPoint[] previousKeyPoints, previousDesc);
            orb.DetectAndCompute(currentMat, null, out OpenCvSharp.KeyPoint[] currentKeyPoints, currentDesc);
            if (previousKeyPoints == null || currentKeyPoints == null || previousKeyPoints.Length < 24 || currentKeyPoints.Length < 24) return 0;
            if (previousDesc.Empty() || currentDesc.Empty()) return 0;

            OpenCvSharp.DMatch[] matches;
            using (OpenCvSharp.BFMatcher matcher = new(OpenCvSharp.NormTypes.Hamming, true))
            {
                matches = matcher.Match(previousDesc, currentDesc);
            }
            if (matches == null || matches.Length < 18) return 0;

            Array.Sort(matches, static (a, b) => a.Distance.CompareTo(b.Distance));
            int keep = Math.Min(matches.Length, 180);
            List<double> dyList = new();
            for (int i = 0; i < keep; i++)
            {
                OpenCvSharp.DMatch match = matches[i];
                if (match.QueryIdx < 0 || match.QueryIdx >= previousKeyPoints.Length) continue;
                if (match.TrainIdx < 0 || match.TrainIdx >= currentKeyPoints.Length) continue;
                if (match.Distance > 80) continue;

                double dx = currentKeyPoints[match.TrainIdx].Pt.X - previousKeyPoints[match.QueryIdx].Pt.X;
                double dy = currentKeyPoints[match.TrainIdx].Pt.Y - previousKeyPoints[match.QueryIdx].Pt.Y;
                if (Math.Abs(dx) > 18.0) continue;
                if (Math.Abs(dy) < 1.5) continue;
                dyList.Add(dy);
            }

            if (dyList.Count < 12) return 0;
            dyList.Sort();
            double median = dyList[dyList.Count / 2];
            if (forceUpward && median <= 1.5) return 0;
            if (forceDownward && median >= -1.5) return 0;
            if (!forceUpward && !forceDownward && Math.Abs(median) < 1.5) return 0;

            int inliers = 0;
            double sum = 0.0;
            double tolerance = Math.Max(3.0, Math.Abs(median) * 0.12);
            for (int i = 0; i < dyList.Count; i++)
            {
                if (Math.Abs(dyList[i] - median) <= tolerance)
                {
                    inliers++;
                    sum += dyList[i];
                }
            }

            if (inliers < 10) return 0;
            double inlierRatio = (double)inliers / dyList.Count;
            bool forcedDirection = forceUpward || forceDownward;
            double minInlierRatio = forcedDirection ? OpenCvForcedDirectionMinInlierRatio : OpenCvMinInlierRatio;
            if (inlierRatio < minInlierRatio) return 0;

            double averageDy = sum / inliers;
            int delta = (int)Math.Round(Math.Abs(averageDy) * sample);
            if (delta < 2 || delta >= height) return 0;

            bool upward = averageDy > 0;
            _algorithmName = "Hybrid+OpenCv";
            MarkStitched(upward ? "OpenCV-Upward" : "OpenCV-Downward", $"dy={averageDy * sample:F1} in={inliers}/{dyList.Count}");

            if (forceUpward || forceDownward) return delta;
            return upward ? delta : -delta;
        }
        catch (Exception ex)
        {
            SetLast("OpenCV-Failed", ex.GetType().Name);
            return 0;
        }
    }

    private static OpenCvSharp.Mat CreateGrayMatForOpenCv(Bitmap bitmap, int width, int height, int sample)
    {
        if (sample < 1) sample = 1;
        int sampledWidth = (width + sample - 1) / sample;
        int sampledHeight = (height + sample - 1) / sample;
        if (sampledWidth <= 0 || sampledHeight <= 0) return new OpenCvSharp.Mat();

        Bitmap? work = null;
        BitmapData? data = null;
        try
        {
            work = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(work))
            {
                graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height), new Rectangle(0, 0, width, height), GraphicsUnit.Pixel);
            }

            data = work.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int absoluteStride = Math.Abs(stride);
            byte[] source = new byte[absoluteStride * height];
            if (stride > 0)
            {
                Marshal.Copy(data.Scan0, source, 0, source.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr src = IntPtr.Add(data.Scan0, y * stride);
                    Marshal.Copy(src, source, y * absoluteStride, absoluteStride);
                }
            }

            byte[] gray = new byte[sampledWidth * sampledHeight];
            int grayIndex = 0;
            for (int y = 0; y < height && grayIndex < gray.Length; y += sample)
            {
                int row = y * absoluteStride;
                for (int x = 0; x < width && grayIndex < gray.Length; x += sample)
                {
                    int offset = row + x * 4;
                    if (offset + 2 >= 0 && offset + 2 < source.Length)
                    {
                        int b = source[offset];
                        int g = source[offset + 1];
                        int r = source[offset + 2];
                        gray[grayIndex++] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    }
                    else
                    {
                        gray[grayIndex++] = 0;
                    }
                }
            }

            OpenCvSharp.Mat mat = new(sampledHeight, sampledWidth, OpenCvSharp.MatType.CV_8UC1);
            Marshal.Copy(gray, 0, mat.Data, gray.Length);
            return mat;
        }
        finally
        {
            try
            {
                if (data != null && work != null) work.UnlockBits(data);
            }
            catch { }
            try { work?.Dispose(); } catch { }
        }
    }

    private static void DetectStaticBounds(byte[] previous, byte[] current, int width, int height, out int topY, out int bottomY)
    {
        topY = 0;
        bottomY = 0;

        for (int y = 0; y < height / 2; y++)
        {
            if (!IsRowMatch(previous, current, width, y, y)) break;
            topY++;
        }

        for (int y = 0; y < height / 2; y++)
        {
            int rowY = height - 1 - y;
            if (!IsRowMatch(previous, current, width, rowY, rowY)) break;
            bottomY++;
        }
    }

    private static bool IsRowMatch(byte[] previous, byte[] current, int width, int previousY, int currentY)
    {
        int previousBase = previousY * width;
        int currentBase = currentY * width;
        int diffSum = 0;

        for (int x = 0; x < width; x += 4)
        {
            int diff = previous[previousBase + x] - current[currentBase + x];
            if (diff < -5 || diff > 5) return false;
            diffSum += diff < 0 ? -diff : diff;
        }

        return diffSum < width;
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
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int width = bitmap.Width;
            int height = bitmap.Height;
            int stride = data.Stride;
            int absoluteStride = stride > 0 ? stride : -stride;

            byte[] rowBuffer = ByteArrayPool.Rent(absoluteStride);
            try
            {
                for (int sampledY = 0; sampledY < sampledHeight; sampledY++)
                {
                    int sourceY = sampledY * step;
                    if (sourceY >= height) break;

                    IntPtr sourceRow = IntPtr.Add(data.Scan0, stride > 0 ? sourceY * stride : (height - 1 - sourceY) * absoluteStride);
                    Marshal.Copy(sourceRow, rowBuffer, 0, absoluteStride);

                    int destinationBase = sampledY * sampledWidth;
                    for (int sampledX = 0; sampledX < sampledWidth; sampledX++)
                    {
                        int sourceX = sampledX * step;
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
                ByteArrayPool.Return(rowBuffer);
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

    internal static void MarkStitched(string source, string score)
    {
        _stitchingCount++;
        SetLast(source, score);
    }

    private static void SetLast(string source, string score)
    {
        _lastDeltaSource = source;
        _lastDeltaScore = score;
    }

    private static int Abs(int value)
    {
        return value < 0 ? -value : value;
    }
}
