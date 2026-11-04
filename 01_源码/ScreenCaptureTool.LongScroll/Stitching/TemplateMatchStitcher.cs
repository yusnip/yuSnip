using System;
using OpenCvSharp;

namespace ScreenCaptureTool.LongScroll.Stitching
{
    /// <summary>
    /// 基于 OpenCV 模板匹配的高性能长截图垂直位移计算器
    /// 专为用户手动滚动（随滚随截）设计，采用双向自适应特征匹配、双区域一致性验证与周期性纹理防重检测，
    /// 无论向上或向下滚动、大跨度滚动还是包含固定层/重复卡片，均能准确判定真实位移。
    /// </summary>
    public static class TemplateMatchStitcher
    {
        /// <summary>
        /// 统一计算当前帧相对于上一帧的沿轴滚动位移（支持纵向和横向长截图）
        /// </summary>
        /// <param name="prevMat">上一帧图像 (Mat)</param>
        /// <param name="currMat">当前帧图像 (Mat)</param>
        /// <param name="axis">滚动轴向（Vertical 或 Horizontal）</param>
        /// <param name="staticLeading">首端静态固定层长度（纵向=顶部，横向=左侧）</param>
        /// <param name="staticTrailing">尾端静态固定层长度（纵向=底部，横向=右侧）</param>
        /// <param name="minDelta">最小有效位移 (例如 3 像素，避开微小抖动)</param>
        /// <param name="maxDelta">最大有效位移 (0 表示按有效区域 85% 自动计算)</param>
        /// <param name="preferredDirection">方向剪枝提示：1=倾向向后/向下滚动，-1=倾向向前/向上滚动，0=未知方向双向全搜</param>
        /// <returns>返回沿轴位移像素值 (>0 表示向后滚动，<0 表示向前滚动，0 表示未移动/非法)</returns>
        public static int CalculateScrollOffset(
            Mat prevMat,
            Mat currMat,
            ScrollAxis axis = ScrollAxis.Vertical,
            int staticLeading = 0,
            int staticTrailing = 0,
            int minDelta = 3,
            int maxDelta = 0,
            int preferredDirection = 0,
            bool aggressiveChecks = false)
        {
            if (prevMat == null || currMat == null || prevMat.Empty() || currMat.Empty())
                return 0;

            if (axis == ScrollAxis.Horizontal)
            {
                // 横向长截图：利用 OpenCV SIMD 极速转置（Transpose），将水平轴映射为垂直轴，
                // 100% 复用纵向模板匹配、双区域校验与周期性防重算法。
                using Mat transposedPrev = new Mat();
                using Mat transposedCurr = new Mat();
                Cv2.Transpose(prevMat, transposedPrev);
                Cv2.Transpose(currMat, transposedCurr);

                return CalculateVerticalScrollOffset(
                    transposedPrev,
                    transposedCurr,
                    staticTop: staticLeading,
                    staticBottom: staticTrailing,
                    minDeltaY: minDelta,
                    maxDeltaY: maxDelta,
                    preferredDirection: preferredDirection,
                    aggressiveChecks: aggressiveChecks);
            }

            return CalculateVerticalScrollOffset(
                prevMat,
                currMat,
                staticTop: staticLeading,
                staticBottom: staticTrailing,
                minDeltaY: minDelta,
                maxDeltaY: maxDelta,
                preferredDirection: preferredDirection,
                aggressiveChecks: aggressiveChecks);
        }

        /// <summary>
        /// 计算当前帧相对于上一帧的垂直位移 Delta Y
        /// </summary>
        /// <param name="preferredDirection">方向剪枝提示：1=先试下滚候选且验证通过即返回，-1=先试上滚候选，0=双向全搜取最优</param>
        /// <returns>返回垂直位移像素值 DeltaY (>0 表示向下滚动，<0 表示向上滚动，0 表示未移动/非法)</returns>
        public static int CalculateVerticalScrollOffset(
            Mat prevMat,
            Mat currMat,
            int staticTop = 0,
            int staticBottom = 0,
            int minDeltaY = 3,
            int maxDeltaY = 0,
            int preferredDirection = 0,
            bool aggressiveChecks = false)
        {
            if (prevMat == null || currMat == null || prevMat.Empty() || currMat.Empty())
                return 0;

            int width = prevMat.Width;
            int height = prevMat.Height;

            if (width < 80 || height < 80)
                return 0;

            int effectiveTop = Math.Clamp(staticTop, 0, height / 3);
            int effectiveBottom = Math.Clamp(staticBottom, 0, height / 3);
            int effectiveHeight = height - effectiveTop - effectiveBottom;
            if (effectiveHeight < 60) return 0;

            if (maxDeltaY <= 0)
                maxDeltaY = (int)(effectiveHeight * 0.85);

            // 1. 转为灰度图加速计算（已是灰度图则直接复用，避免整幅图像冗余拷贝）
            bool prevNeedsDispose = false;
            bool currNeedsDispose = false;
            Mat prevGray;
            Mat currGray;
            if (prevMat.Channels() > 1)
            {
                prevGray = new Mat();
                Cv2.CvtColor(prevMat, prevGray, ColorConversionCodes.BGRA2GRAY);
                prevNeedsDispose = true;
            }
            else
            {
                prevGray = prevMat;
            }

            if (currMat.Channels() > 1)
            {
                currGray = new Mat();
                Cv2.CvtColor(currMat, currGray, ColorConversionCodes.BGRA2GRAY);
                currNeedsDispose = true;
            }
            else
            {
                currGray = currMat;
            }

            try
            {

            // 左右留边距 (避开左右滚动条或边框)
            int cropX = (int)(width * 0.15);
            int cropWidth = Math.Max(20, width - cropX * 2);

            // 模板高度：取有效滚动高度的 18% ~ 25%
            int templateHeight = Math.Clamp((int)(effectiveHeight * 0.20), 24, 140);
            if (templateHeight >= effectiveHeight) templateHeight = effectiveHeight / 2;
            if (templateHeight <= 0) return 0;

            // 搜索区域覆盖 current 的整个有效滚动区域
            Rect searchRect = new Rect(cropX, effectiveTop, cropWidth, effectiveHeight);
            using Mat searchArea = new Mat(currGray, searchRect);

            // 候选 1：向下滚动（浏览下方新内容）
            // 上一帧的特征点在底部（因为向下滚动后上一帧底部内容会移入当前帧上部）
            int? TryMatchDown(out double score)
            {
                score = double.MaxValue;
                int downCropY = height - effectiveBottom - templateHeight;
                if (downCropY < effectiveTop) return null;
                Rect downTemplateRect = new Rect(cropX, downCropY, cropWidth, templateHeight);
                using Mat downTemplateTile = new Mat(prevGray, downTemplateRect);
                using Mat downMatchResult = new Mat();
                Cv2.MatchTemplate(searchArea, downTemplateTile, downMatchResult, TemplateMatchModes.SqDiffNormed);
                Cv2.MinMaxLoc(downMatchResult, out double downMinVal, out _, out Point downMinLoc, out _);

                int matchedAbsoluteY = effectiveTop + downMinLoc.Y;
                int rawDeltaDown = downCropY - matchedAbsoluteY;
                if (downMinVal > 0.03 || rawDeltaDown < minDeltaY || rawDeltaDown > maxDeltaY) return null;

                // 周期性/多重峰防重检测
                if (HasAmbiguousMinima(downMatchResult, downMinVal, downMinLoc.Y,
                        minGap: aggressiveChecks ? 24 : 40,
                        cutoffFloor: aggressiveChecks ? 0.012 : 0.02)) return null;

                // 双区域一致性校验：检查上一帧中部的第二特征块是否在当前帧相同位移处吻合
                // Keep the verification block well separated from the template.
                // On periodic grids an adjacent block matches equally well at a wrong
                // period multiple, making the check vacuous; pull it toward the middle
                // of the frame so a wrong period must survive two distant checks.
                int verifyGap = aggressiveChecks
                    ? Math.Min(downCropY - effectiveTop - templateHeight, Math.Max(60, effectiveHeight / 2))
                    : templateHeight + 16;
                if (verifyGap < templateHeight + 16) verifyGap = templateHeight + 16;
                int verifyY = downCropY - verifyGap;
                int curVerifyY = verifyY - rawDeltaDown;
                if (verifyY >= effectiveTop && curVerifyY >= effectiveTop && curVerifyY + templateHeight <= height - effectiveBottom)
                {
                    Rect vPrevRect = new Rect(cropX, verifyY, cropWidth, templateHeight);
                    Rect vCurrRect = new Rect(cropX, curVerifyY, cropWidth, templateHeight);
                    using Mat vPrevTile = new Mat(prevGray, vPrevRect);
                    using Mat vCurrTile = new Mat(currGray, vCurrRect);
                    using Mat vResult = new Mat();
                    Cv2.MatchTemplate(vCurrTile, vPrevTile, vResult, TemplateMatchModes.SqDiffNormed);
                    Cv2.MinMaxLoc(vResult, out double vMinVal, out _, out _, out _);
                    if (vMinVal > 0.035) return null;
                }

                score = downMinVal;
                return rawDeltaDown;
            }

            // 候选 2：向上滚动（浏览上方新内容）
            // 上一帧的特征点在顶部（因为向上滚动后上一帧顶部内容会移入当前帧下部）
            int? TryMatchUp(out double score)
            {
                score = double.MaxValue;
                int upCropY = effectiveTop;
                if (upCropY + templateHeight > height - effectiveBottom) return null;
                Rect upTemplateRect = new Rect(cropX, upCropY, cropWidth, templateHeight);
                using Mat upTemplateTile = new Mat(prevGray, upTemplateRect);
                using Mat upMatchResult = new Mat();
                Cv2.MatchTemplate(searchArea, upTemplateTile, upMatchResult, TemplateMatchModes.SqDiffNormed);
                Cv2.MinMaxLoc(upMatchResult, out double upMinVal, out _, out Point upMinLoc, out _);

                int matchedAbsoluteY = effectiveTop + upMinLoc.Y;
                int rawDeltaUp = -(matchedAbsoluteY - upCropY); // 负数表示向上滚动
                int absDeltaUp = Math.Abs(rawDeltaUp);
                if (upMinVal > 0.03 || absDeltaUp < minDeltaY || absDeltaUp > maxDeltaY) return null;

                // 周期性/多重峰防重检测
                if (HasAmbiguousMinima(upMatchResult, upMinVal, upMinLoc.Y,
                        minGap: aggressiveChecks ? 24 : 40,
                        cutoffFloor: aggressiveChecks ? 0.012 : 0.02)) return null;

                // 双区域一致性校验：检查上一帧下方的第二特征块是否在当前帧相同位移处吻合
                int verifyGapUp = aggressiveChecks
                    ? Math.Min(height - effectiveBottom - upCropY - templateHeight * 2, Math.Max(60, effectiveHeight / 2))
                    : 16;
                if (verifyGapUp < 16) verifyGapUp = 16;
                int verifyY = upCropY + templateHeight + verifyGapUp;
                int curVerifyY = verifyY - rawDeltaUp; // rawDeltaUp 为负数，所以 curVerifyY = verifyY + absDeltaUp
                if (verifyY + templateHeight <= height - effectiveBottom && curVerifyY >= effectiveTop && curVerifyY + templateHeight <= height - effectiveBottom)
                {
                    Rect vPrevRect = new Rect(cropX, verifyY, cropWidth, templateHeight);
                    Rect vCurrRect = new Rect(cropX, curVerifyY, cropWidth, templateHeight);
                    using Mat vPrevTile = new Mat(prevGray, vPrevRect);
                    using Mat vCurrTile = new Mat(currGray, vCurrRect);
                    using Mat vResult = new Mat();
                    Cv2.MatchTemplate(vCurrTile, vPrevTile, vResult, TemplateMatchModes.SqDiffNormed);
                    Cv2.MinMaxLoc(vResult, out double vMinVal, out _, out _, out _);
                    if (vMinVal > 0.035) return null;
                }

                score = upMinVal;
                return rawDeltaUp;
            }

            // 方向剪枝：滚动方向已知（滚轮/滚动条提示）时先跑对应候选，
            // 验证通过即返回，省掉另一方向的全幅 MatchTemplate（约一半匹配开销）。
            // 主方向失败时仍回落到下方双向全搜，保证方向提示错误时不漏配。
            if (preferredDirection > 0)
            {
                int? fastDown = TryMatchDown(out _);
                if (fastDown.HasValue) return fastDown.Value;
            }
            else if (preferredDirection < 0)
            {
                int? fastUp = TryMatchUp(out _);
                if (fastUp.HasValue) return fastUp.Value;
            }

            double bestScore = 1.0;
            int bestDelta = 0;

            int? downMatch = TryMatchDown(out double downScore);
            if (downMatch.HasValue && downScore < bestScore)
            {
                bestScore = downScore;
                bestDelta = downMatch.Value;
            }

            int? upMatch = TryMatchUp(out double upScore);
            if (upMatch.HasValue && upScore < bestScore)
            {
                bestScore = upScore;
                bestDelta = upMatch.Value;
            }

                return bestDelta;
            }
            finally
            {
                if (prevNeedsDispose) { try { prevGray.Dispose(); } catch { } }
                if (currNeedsDispose) { try { currGray.Dispose(); } catch { } }
            }
        }

        private static bool HasAmbiguousMinima(Mat matchResult, double minVal, int bestY, int minGap = 40, double cutoffFloor = 0.02)
        {
            if (matchResult == null || matchResult.Empty()) return false;
            int h = matchResult.Height;
            if (h < 3) return false;

            float[] resultValues = new float[h];
            var indexer = matchResult.GetGenericIndexer<float>();
            for (int y = 0; y < h; y++)
            {
                resultValues[y] = indexer[y, 0];
            }

            double cutoff = Math.Max(minVal * 1.8, cutoffFloor);
            for (int y = 1; y < h - 1; y++)
            {
                if (Math.Abs(y - bestY) < minGap) continue;
                float v = resultValues[y];
                if (v <= cutoff && v <= resultValues[y - 1] && v <= resultValues[y + 1])
                {
                    return true; // 发现除最佳匹配点外的另一个独立局部极小值（周期性/重复卡片纹理）
                }
            }
            return false;
        }
    }
}
