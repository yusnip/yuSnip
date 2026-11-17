using System;
using System.Collections.Generic;
using System.Drawing;

namespace ScreenCaptureTool.Core.Capture.Annotations;

/// <summary>
/// 聚光灯重叠边框合并的<b>判据</b>（预览侧 AnnotationLayer 与成品侧 CaptureSession 共用这一份）。
///
/// <para>要解决的问题：暗幕的洞口一直是"全部图元的并集"，边框却是"逐颗各描一圈完整环带"，
/// 于是重叠处留下交叉边线，且那些线根本不落在暗幕洞口上——用户看到的"杂乱交叉"就是这么来的。</para>
///
/// <para>方案：逐颗描边不变，但描第 i 颗之前，把画笔裁到"其余<b>同色同粗</b>图元并集之外"。
/// 落在别人洞里的边段被剪掉，剩下的正好是那组的外轮廓；非重叠处仍是居中满宽描边，
/// 物理像素口径、叠放顺序、居中满宽环带三条都不动（protection「标注双渲染器共用度量口径」）。
/// 两侧用<b>同一套裁剪判据</b>而非"一侧求并集路径描边、另一侧求区域差"，是为了让预览与成品
/// 在交叠接缝处必然逐像素一致——两种算法在十字接缝处各有半字宽的出入，各算各的就会对不上。</para>
///
/// <para>本类只出判据与候选集合，不出几何：几何一边是 Avalonia 的 <c>Geometry</c>、
/// 一边是 GDI+ 的 <c>GraphicsPath</c>，强行统一反而要引第三方几何库。
/// 也不带 Pen/Brush（设计规范「C# 无画刷」）。</para>
/// </summary>
public static class SpotlightOutlineMerge
{
    /// <summary>线宽比对容差：同一根滑杆出来的值是同一个浮点，这里只是防缩放换算带来的末位漂移。</summary>
    private const float ThicknessEpsilon = 0.01f;

    /// <summary>参与合并的重叠颗数上限，超了整组退回"逐颗各描一圈"（＝本改动之前的行为）。
    /// <para>依据是实测不是估计：离屏探针 <c>02_构建工具/自测/SpotlightMergeCostProbe</c>（本机 150% 缩放、
    /// 3px 线宽、含椭圆）量出每帧多花的耗时随重叠颗数<b>平方</b>增长——
    /// n=2 +0.05ms、n=3 +0.13ms、n=5 +0.34ms、n=6 +0.52ms、n=12 +2.72ms、n=20 +9.72ms，
    /// 计划定的 1.5ms/帧预算在 n≈8 处越线。拖拽期间每一帧都要重算（图元本身在动，缓存救不了），
    /// 所以只能给颗数封顶。判据放在共用件里：预览与成品要么都合、要么都不合，
    /// 不会出现"预览合了、存出来的图没合"。</para></summary>
    private const int MaxMergeGroupSize = 8;

    /// <summary>同色同粗＝同一样式，只有同一样式的图元之间才互相裁剪。
    /// 不同色是用户有意把两块分开，替他合并会把他的意图抹掉。</summary>
    public static bool SameStyle(SpotlightShape a, SpotlightShape b)
    {
        return a.StrokeColor.ToArgb() == b.StrokeColor.ToArgb()
            && Math.Abs(a.StrokeThickness - b.StrokeThickness) < ThicknessEpsilon;
    }

    /// <summary>
    /// 第 <paramref name="self"/> 这颗描边时要裁掉的候选：同组、且包围盒与 self 的<b>描边带</b>相交的其它图元。
    /// <para>粗筛只按包围盒（self 外扩半个线宽），宁可多算不可漏算——椭圆/圆角的实际墨区比包围盒小，
    /// 多出来的候选求并进裁剪几何后剪不掉任何像素，但漏掉一个就会留下一条交叉线。</para>
    /// <para>返回空表＝这颗与同组谁都不挨着，调用方直接按老路子整圈描边，一次布尔都不做
    /// （绝大多数场景就两三颗互不重叠的框，合并开销恒为零）。</para>
    /// </summary>
    public static List<SpotlightShape> ClipSources(IReadOnlyList<SpotlightShape> all, SpotlightShape self)
    {
        var result = new List<SpotlightShape>();
        if (all.Count < 2) return result;

        // 颗数封顶（见 MaxMergeGroupSize 的实测依据）：这一组人太多就整体不合并，回到各描各的整圈
        int sameStyleCount = 0;
        foreach (SpotlightShape s in all)
        {
            if (s.StrokeThickness > 0.0f && SameStyle(self, s)) sameStyleCount++;
        }
        if (sameStyleCount > MaxMergeGroupSize) return result;

        float half = Math.Max(1.0f, self.StrokeThickness) / 2.0f;
        RectangleF band = RectangleF.Inflate(self.Rect, half, half);

        foreach (SpotlightShape other in all)
        {
            if (ReferenceEquals(other, self) || other.Id == self.Id) continue;
            if (other.StrokeThickness <= 0.0f) continue;
            if (!SameStyle(self, other)) continue;
            if (!band.IntersectsWith(other.Rect)) continue;
            result.Add(other);
        }
        return result;
    }
}
