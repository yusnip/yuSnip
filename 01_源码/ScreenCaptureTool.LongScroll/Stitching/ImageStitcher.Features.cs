using System.Drawing;
using OpenCvSharp;

namespace ScreenCaptureTool.LongScroll.Stitching;

public static partial class ImageStitcher
{
    private readonly record struct FeatureShift(float Along, float Cross, Point2f Previous);

    // 仅由一次拼接线程持有。帧按捕获代数及裁剪边界识别，不能按会被池化重写的 Bitmap 身份缓存。
    internal sealed class FeatureTranslationCache : IDisposable
    {
        private FeatureFrame? _previous, _current;
        public ScrollEstimate Estimate(Bitmap previous, Bitmap current, ScrollAxis axis, AxisDirection? direction,
            long previousVersion, long currentVersion, int leading = 0, int trailing = 0)
        {
            if (previous.Width != current.Width || previous.Height != current.Height) return ScrollEstimate.Unreliable("FeatureSizeMismatch");
            try
            {
                EnsureOpenCvCpuOnly();
                var previousKey = (previousVersion, leading, trailing, previous.Width, previous.Height);
                var currentKey = (currentVersion, leading, trailing, current.Width, current.Height);
                if (_current?.Key == previousKey) (_previous, _current) = (_current, _previous);
                if (_previous == null || _previous.Key != previousKey)
                { _previous?.Dispose(); _previous = null; _previous = new FeatureFrame(previous, previousKey); }
                if (_current == null || _current.Key != currentKey)
                { _current?.Dispose(); _current = null; _current = new FeatureFrame(current, currentKey); }
                ScrollEstimate result = EstimateFeatureTranslationCore(_previous, _current, axis, direction);
                // 临时原生/取像素失败不能成为被长期复用的坏基线，下次仍须能重新提取。
                if (result.Reason is "FeatureInvalidFrame" or "FeatureUnavailable") Dispose();
                return result;
            }
            catch (Exception ex)
            { Dispose(); return ScrollEstimate.Unreliable("FeatureUnavailable", diagnostics: ex.GetType().Name); }
        }
        public void Dispose()
        { _previous?.Dispose(); _current?.Dispose(); _previous = _current = null; }
    }

    private sealed class FeatureFrame : IDisposable
    {
        public readonly (long, int, int, int, int) Key;
        public readonly Mat Gray, Descriptors;
        public readonly KeyPoint[] Points;
        public readonly double Scale;
        public FeatureFrame(Bitmap source, (long, int, int, int, int) key)
        {
            Key = key; Gray = CreateGrayMatForOpenCv(source, source.Width, source.Height); Descriptors = new Mat();
            try
            {
                if (Gray.Empty()) { Points = []; Scale = 1; return; }
                Scale = Math.Min(1.0, 960.0 / Math.Max(Gray.Width, Gray.Height));
                using Mat small = new();
                Cv2.Resize(Gray, small, new OpenCvSharp.Size(), Scale, Scale, InterpolationFlags.Area);
                using var orb = ORB.Create(nFeatures: 1200, nLevels: 4, edgeThreshold: 16, patchSize: 21, fastThreshold: 10);
                orb.DetectAndCompute(small, null, out KeyPoint[] points, Descriptors);
                Points = points;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Gray.Dispose(); Descriptors.Dispose(); }
    }

    /// <summary>
    /// 二维局部特征提供独立位移证据：动画不参与投票，重复描述子因无唯一匹配被剔除。
    /// 只接受横向漂移近零、跨多个区域达成共识且原分辨率像素复核通过的平移。
    /// </summary>
    public static ScrollEstimate EstimateFeatureTranslation(Bitmap previous, Bitmap current, ScrollAxis axis,
        AxisDirection? direction = null)
    {
        if (previous == null || current == null || previous.Width != current.Width || previous.Height != current.Height) return ScrollEstimate.Unreliable("FeatureSizeMismatch");
        using var cache = new FeatureTranslationCache();
        return cache.Estimate(previous, current, axis, direction, 0, 1);
    }

    private static ScrollEstimate EstimateFeatureTranslationCore(FeatureFrame previous, FeatureFrame current, ScrollAxis axis, AxisDirection? direction)
    {
        try
        {
            Mat first = previous.Gray, second = current.Gray;
            if (first.Empty() || second.Empty()) return ScrollEstimate.Unreliable("FeatureInvalidFrame");
            double scale = previous.Scale;
            Mat descriptorsFirst = previous.Descriptors, descriptorsSecond = current.Descriptors;
            KeyPoint[] pointsFirst = previous.Points, pointsSecond = current.Points;
            if (descriptorsFirst.Rows < 6 || descriptorsSecond.Rows < 6) return ScrollEstimate.Unreliable("FeatureLowTexture");
            using var matcher = new BFMatcher(NormTypes.Hamming);
            DMatch[][] forward = matcher.KnnMatch(descriptorsFirst, descriptorsSecond, 2);
            // 只有通过正向比值检验的描述子会查反向一致性；其余反向查询不会影响任何判定。
            int[] reverseIndex = new int[descriptorsSecond.Rows]; Array.Fill(reverseIndex, -1);
            var reverseRows = new List<int>();
            foreach (var pair in forward)
            {
                if (pair.Length < 2 || pair[0].Distance >= pair[1].Distance * 0.72 || pair[0].Distance > 48) continue;
                int row = pair[0].TrainIdx;
                if (reverseIndex[row] >= 0) continue;
                reverseIndex[row] = reverseRows.Count; reverseRows.Add(row);
            }
            if (reverseRows.Count < 6) return ScrollEstimate.Unreliable("FeatureFewMatches");
            using Mat reverseDescriptors = new(reverseRows.Count, descriptorsSecond.Cols, descriptorsSecond.Type());
            for (int i = 0; i < reverseRows.Count; i++)
            {
                using Mat sourceRow = descriptorsSecond.Row(reverseRows[i]);
                using Mat targetRow = reverseDescriptors.Row(i);
                sourceRow.CopyTo(targetRow);
            }
            DMatch[][] backward = matcher.KnnMatch(reverseDescriptors, descriptorsFirst, 2);
            var shifts = new List<FeatureShift>();
            int alongLength = axis == ScrollAxis.Vertical ? first.Height : first.Width;
            int crossLength = axis == ScrollAxis.Vertical ? first.Width : first.Height;
            foreach (var pair in forward)
            {
                if (pair.Length < 2 || pair[0].Distance >= pair[1].Distance * 0.72 || pair[0].Distance > 48) continue;
                var match = pair[0]; var reverse = backward[reverseIndex[match.TrainIdx]];
                if (reverse.Length < 2 || reverse[0].TrainIdx != match.QueryIdx || reverse[0].Distance >= reverse[1].Distance * 0.72) continue;
                Point2f rawP = pointsFirst[match.QueryIdx].Pt, rawQ = pointsSecond[match.TrainIdx].Pt;
                Point2f p = new(rawP.X / (float)scale, rawP.Y / (float)scale);
                Point2f q = new(rawQ.X / (float)scale, rawQ.Y / (float)scale);
                float along = axis == ScrollAxis.Vertical ? p.Y - q.Y : p.X - q.X;
                float cross = axis == ScrollAxis.Vertical ? p.X - q.X : p.Y - q.Y;
                if (Math.Abs(cross) > 2.0 / scale || Math.Abs(along) < 0.5 || Math.Abs(along) > alongLength - 96) continue;
                if (direction == AxisDirection.Leading && along >= 0 || direction == AxisDirection.Trailing && along <= 0) continue;
                shifts.Add(new FeatureShift(along, cross, p));
            }
            if (shifts.Count < 6) return ScrollEstimate.Unreliable("FeatureFewMatches");
            double tolerance = 2.0 / scale;
            var remaining = new List<FeatureShift>(shifts);
            var accepted = new List<ScrollEstimate>();
            for (int mode = 0; mode < 5 && remaining.Count >= 6; mode++)
            {
                var winner = FindFeatureMode(remaining, tolerance);
                var inliers = remaining.Where(s => Math.Abs(s.Along - winner.Along) <= tolerance).ToArray();
                remaining.RemoveAll(s => Math.Abs(s.Along - winner.Along) <= tolerance * 3);
                if (inliers.Length < 6) break;
                float AlongPoint(FeatureShift s) => axis == ScrollAxis.Vertical ? s.Previous.Y : s.Previous.X;
                float CrossPoint(FeatureShift s) => axis == ScrollAxis.Vertical ? s.Previous.X : s.Previous.Y;
                double overlap = alongLength - Math.Abs(winner.Along);
                // 动画自身也可能有一致运动；只在一小片区域出现的投票不能代表整页滚动。
                if (inliers.Max(AlongPoint) - inliers.Min(AlongPoint) < Math.Min(192, overlap * 0.5)
                    || inliers.Max(CrossPoint) - inliers.Min(CrossPoint) < Math.Min(80, crossLength * 0.15)) continue;
                int center = (int)Math.Round(inliers.Select(s => s.Along).Order().ElementAt(inliers.Length / 2));
                int bestDelta = 0, verified = 0;
                double bestError = double.PositiveInfinity;
                for (int delta = center - (int)Math.Ceiling(tolerance); delta <= center + (int)Math.Ceiling(tolerance); delta++)
                {
                    if (delta == 0 || Math.Sign(delta) != Math.Sign(center)) continue;
                    var errors = inliers.Select(s => FeaturePatchError(first, second, s.Previous, axis, delta)).Where(double.IsFinite).Order().ToArray();
                    if (errors.Length < 6) continue;
                    double median = errors[errors.Length / 2];
                    int good = errors.Count(e => e <= 18);
                    if (median <= 12 && good >= 6 && good >= errors.Length * 0.7 && median < bestError)
                    { bestError = median; bestDelta = delta; verified = good; }
                }
                if (bestDelta == 0 || !VerifyFeatureOverlap(first, second, axis, bestDelta, out string overlapDiagnostics)) continue;
                string diagnostics = $"votes={inliers.Length}/{shifts.Count} verified={verified} patchMad={bestError:F2} {overlapDiagnostics}";
                accepted.Add(new ScrollEstimate(bestDelta, 0, 0, true, "FeatureTranslation", (double)inliers.Length / shifts.Count, diagnostics));
            }
            // 多个周期均能解释整个重叠区时，图像本身没有唯一答案，不能靠票数强行选一个。
            if (accepted.Count != 1) return ScrollEstimate.Unreliable("FeatureAmbiguous", diagnostics: $"matches={shifts.Count} verifiedModes={accepted.Count}");
            SetLast("FeatureTranslation", accepted[0].Diagnostics);
            return accepted[0];
        }
        catch (Exception ex)
        {
            return ScrollEstimate.Unreliable("FeatureUnavailable", diagnostics: ex.GetType().Name);
        }
    }

    private static FeatureShift FindFeatureMode(List<FeatureShift> shifts, double tolerance)
    {
        // 排序后用滑窗统计每个候选的邻域，替代每个候选都重新扫描所有票的 O(n²)。
        // 同票数仍选原列表最早候选，浮点减法/容差保持，不能改变周期歧义判定。
        var sorted = shifts.Select((shift, index) => (shift, index)).OrderBy(item => item.shift.Along).ToArray();
        int first = 0, last = 0, bestCount = -1, bestIndex = int.MaxValue;
        foreach (var item in sorted)
        {
            while (first < sorted.Length && item.shift.Along - sorted[first].shift.Along > tolerance) first++;
            while (last < sorted.Length && sorted[last].shift.Along - item.shift.Along <= tolerance) last++;
            int count = last - first;
            if (count > bestCount || (count == bestCount && item.index < bestIndex))
            { bestCount = count; bestIndex = item.index; }
        }
        return shifts[bestIndex];
    }

    /// <summary>全重叠按网格验证有效边缘；仅允许局部动态离群区，不能把整列/整页文字错位平均掉。</summary>
    private static unsafe bool VerifyFeatureOverlap(Mat first, Mat second, ScrollAxis axis, int delta, out string diagnostics)
    {
        bool vertical = axis == ScrollAxis.Vertical;
        int along = vertical ? first.Height : first.Width, cross = vertical ? first.Width : first.Height;
        int start = Math.Max(0, delta), length = along - Math.Abs(delta);
        const int rows = 8, columns = 6;
        int textured = 0, good = 0, bad = 0, firstBad = rows, lastBad = -1;
        int goodRowMask = 0, goodColumnMask = 0;
        // Mat 的属性跨原生边界取值；本次复核不改 Mat，在循环外读取一次。
        byte* firstPixels = first.DataPointer;
        byte* secondPixels = second.DataPointer;
        long firstStride = first.Step(), secondStride = second.Step();
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            long error = 0; int count = 0;
            for (int y = start + row * length / rows + 1; y < start + (row + 1) * length / rows; y += 2)
            for (int x = column * cross / columns + 1; x < (column + 1) * cross / columns; x += 2)
            {
                int px = vertical ? x : y, py = vertical ? y : x;
                int qx = vertical ? x : y - delta, qy = vertical ? y - delta : x;
                byte* a = firstPixels + py * firstStride + px;
                byte* b = secondPixels + qy * secondStride + qx;
                int edgeA = Math.Max(Math.Abs(a[0] - a[-1]), Math.Abs(a[0] - a[-firstStride]));
                int edgeB = Math.Max(Math.Abs(b[0] - b[-1]), Math.Abs(b[0] - b[-secondStride]));
                if (Math.Max(edgeA, edgeB) < 16) continue;
                error += Math.Abs(a[0] - b[0]); count++;
            }
            if (count < 12) continue;
            textured++;
            if (error / (double)count <= 18)
            { good++; goodRowMask |= 1 << row; goodColumnMask |= 1 << column; }
            else { bad++; firstBad = Math.Min(firstBad, row); lastBad = Math.Max(lastBad, row); }
        }
        diagnostics = $"tiles={good}/{textured} dynamic={bad}";
        return good >= 6 && good >= textured * 0.65
            && System.Numerics.BitOperations.PopCount((uint)goodRowMask) >= 4
            && System.Numerics.BitOperations.PopCount((uint)goodColumnMask) >= 2
            && (bad == 0 || lastBad - firstBad < 6);
    }

    private static unsafe double FeaturePatchError(Mat first, Mat second, Point2f point, ScrollAxis axis, int delta)
    {
        int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        int qx = x - (axis == ScrollAxis.Horizontal ? delta : 0);
        int qy = y - (axis == ScrollAxis.Vertical ? delta : 0);
        const int radius = 6;
        if (x < radius || y < radius || qx < radius || qy < radius
            || x + radius >= first.Width || y + radius >= first.Height || qx + radius >= second.Width || qy + radius >= second.Height)
            return double.PositiveInfinity;
        long sum = 0;
        byte* firstPixels = first.DataPointer;
        byte* secondPixels = second.DataPointer;
        long firstStride = first.Step(), secondStride = second.Step();
        for (int row = -radius; row <= radius; row++)
        {
            byte* a = firstPixels + (y + row) * firstStride + x;
            byte* b = secondPixels + (qy + row) * secondStride + qx;
            for (int column = -radius; column <= radius; column++) sum += Math.Abs(a[column] - b[column]);
        }
        return sum / (double)((radius * 2 + 1) * (radius * 2 + 1));
    }
}
