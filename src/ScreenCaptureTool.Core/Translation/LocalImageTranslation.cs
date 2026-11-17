using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>Index 专属本机成品图流程：OCR 稳定区域 → 编号翻译 → 背景判定与补图 → 实测字体回填。</summary>
public static class LocalImageTranslation
{
    public static async Task<ImageTranslationResult> TranslateAsync(byte[] bytes, TranslationService translator,
        Func<Bitmap, Task<List<OcrLineRegion>>> recognize, CancellationToken ct = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string stage = "解码";
        try
        {
            ct.ThrowIfCancellationRequested();
            using var input = new MemoryStream(bytes);
            using var decoded = (Bitmap)Image.FromStream(input);
            if ((long)decoded.Width * decoded.Height > 40_000_000)
                return Fail("图片像素过多，请分段翻译");
            using var image = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            int[] pixels = ReadPixels(decoded); WritePixels(image, pixels);
            long decodedMs = timer.ElapsedMilliseconds;
            stage = "OCR";
            var lines = await RecognizeTiledAsync(image, recognize, ct).ConfigureAwait(false);
            long recognizedMs = timer.ElapsedMilliseconds;
            if (lines.Count == 0) return ImageTranslationResult.Fail(ImageTranslationFailure.NoText);
            string source = string.Join('\n', lines.Select(line => line.Text));
            var (src, tgt, _, _) = LanguageDetector.ResolveLanguages(source, translator.Config.SourceLanguage,
                translator.Config.TargetLanguage, translator.Config.SecondaryTargetLanguage);
            // 同图相同原文只译一次，按原区域稳定映射回填，不缓存到磁盘或其他图片。
            var unique = lines.Select(line => line.Text).Distinct(StringComparer.Ordinal).ToArray();
            stage = "联网翻译";
            var result = await translator.TranslateSegmentsAsync(unique, src, tgt, ct).ConfigureAwait(false);
            long translatedMs = timer.ElapsedMilliseconds;
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                AppLogger.Warn($"[本机图片翻译] 翻译未完成 区域={lines.Count} 去重={unique.Length} OCR={recognizedMs - decodedMs}ms 联网={translatedMs - recognizedMs}ms");
                return Fail(result.ErrorMessage ?? "图片文字翻译失败");
            }
            var byText = unique.Select((text, index) => (text, index)).ToDictionary(item => item.text,
                item => result.Translations[item.index], StringComparer.Ordinal);
            stage = "本机回填";
            var (expanded, skipped) = Render(image, pixels, lines, lines.Select(line => byText[line.Text]).ToArray(),
                tgt, ct);
            ct.ThrowIfCancellationRequested();
            stage = "PNG编码";
            if (skipped == lines.Count)
            {
                // 逐块降级后不再有「异常」这一失败形态，整图失败要自己留下阶段与数量，否则日志无从定位。
                AppLogger.Warn($"[本机图片翻译] 失败 阶段=本机回填 区域={lines.Count} 全部放不下");
                return Fail("译文无法放入图片现有空白，请使用图片到文本");
            }
            using var output = new MemoryStream(); image.Save(output, ImageFormat.Png);
            AppLogger.Info($"[本机图片翻译] 完成 尺寸={image.Width}x{image.Height} 区域={lines.Count} 去重={unique.Length} 扩展排版={expanded} 跳过={skipped} 解码={decodedMs}ms OCR={recognizedMs - decodedMs}ms 联网={translatedMs - recognizedMs}ms 回填编码={timer.ElapsedMilliseconds - translatedMs}ms 总计={timer.ElapsedMilliseconds}ms");
            return ImageTranslationResult.Ok(output.ToArray(), src, timer.ElapsedMilliseconds, skipped);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Warn($"[本机图片翻译] 失败 阶段={stage} 类型={ex.GetType().Name} 总计={timer.ElapsedMilliseconds}ms");
            AppLogger.Warn("[本机图片翻译] 调用位置=" + ex.StackTrace);
            return Fail(ex is InvalidOperationException ? ex.Message : "本机图片处理失败，请重试或使用图片到文本");
        }
    }

    private static ImageTranslationResult Fail(string message) => ImageTranslationResult.Fail(ImageTranslationFailure.Unknown, message);

    private static async Task<List<OcrLineRegion>> RecognizeTiledAsync(Bitmap image,
        Func<Bitmap, Task<List<OcrLineRegion>>> recognize, CancellationToken ct)
    {
        var found = new List<OcrLineRegion>();
        int edge = image.Width <= 2048 && image.Height <= 3072 ? Math.Max(image.Width, image.Height) : 1600;
        int step = Math.Max(1, edge - 64);
        for (int y = 0; y < image.Height; y += step)
        {
            for (int x = 0; x < image.Width; x += step)
            {
                ct.ThrowIfCancellationRequested();
                var area = new Rectangle(x, y, Math.Min(edge, image.Width - x), Math.Min(edge, image.Height - y));
                // 普通尺寸只读原位图，避免再复制一张整图；大图仍按原规则创建局部切片。
                using var tile = area.Width == image.Width && area.Height == image.Height ? null : image.Clone(area, PixelFormat.Format32bppArgb);
                var lines = await recognize(tile ?? image).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (line.Score < .65f || string.IsNullOrWhiteSpace(line.Text)) continue;
                    var b = line.Bounds; b.Offset(x, y);
                    b = RectangleF.Intersect(b, new RectangleF(0, 0, image.Width, image.Height));
                    if (b.Width < 4 || b.Height < 4) continue;
                    bool duplicate = found.Any(other => other.Text == line.Text && Overlap(other.Bounds, b) > .5);
                    if (duplicate) continue;
                    // 多边形与逐字刀口是切片坐标系，必须与 Bounds 同步平移到整图坐标，否则掩膜会画到别处。
                    found.Add(new OcrLineRegion
                    {
                        Text = line.Text,
                        Bounds = b,
                        Score = line.Score,
                        LineIndex = found.Count,
                        Polygon = Shift(line.Polygon, x, y),
                        CharX = ShiftX(line.CharX, x),
                    });
                }
                if (x + edge >= image.Width) break;
            }
            if (y + edge >= image.Height) break;
        }
        ct.ThrowIfCancellationRequested();
        return found.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToList();
    }

    private static PointF[]? Shift(PointF[]? polygon, int dx, int dy)
        => polygon == null ? null : polygon.Select(p => new PointF(p.X + dx, p.Y + dy)).ToArray();

    private static float[]? ShiftX(float[]? charX, int dx)
        => charX == null ? null : charX.Select(v => v + dx).ToArray();

    private static double Overlap(RectangleF a, RectangleF b)
    {
        var intersection = RectangleF.Intersect(a, b);
        double union = a.Width * a.Height + b.Width * b.Height - intersection.Width * intersection.Height;
        return union > 0 ? intersection.Width * intersection.Height / union : 0;
    }

    /// <summary>排版与回填常量。⚠ 沿用改前口径的条目已注明，其余待 <c>--image-calibrate</c> 标定写定。</summary>
    static class Layout
    {
        /// <summary>字号搜索下限（沿用改前的 8px 口径，待标定）。</summary>
        public const float FontMinPx = 8f;
        /// <summary>字号相对原文行高的上限比例（沿用改前 0.88，待标定）。</summary>
        public const float FontSourceRatio = .88f;
        public const int FontSearchPasses = 8;
        /// <summary>横向借用空白的最大距离＝max(此值, 原框宽×倍数)（沿用改前 160/2 口径）。</summary>
        public const float GrowthPx = 160f;
        public const float GrowthRatio = 2f;
        /// <summary>纵向借用空白的最大距离＝max(此值, 原框高×倍数)（沿用改前 32/3 口径）。</summary>
        public const float VerticalGrowthPx = 32f;
        public const float VerticalGrowthRatio = 3f;
        /// <summary>与其他文字框保留的安全间距（沿用改前 2px）。</summary>
        public const float KeepOut = 2f;
        /// <summary>疑似竖排行的形状判据：按块跳过，不再整图拒绝（竖排回填属批 4）。</summary>
        public const int VerticalMinChars = 3;
        public const float VerticalAspect = 2f;
        /// <summary>重叠判据（沿用改前 IoU 0.5），超过则该块不回填。</summary>
        public const double OverlapMax = .5;
        /// <summary>
        /// 承托板外扩量，同时充当板内文字内距与圆角半径。4 不是审美取值，由既有几何推出：
        /// 墨区＝原框取整（最多 1px）外扩 <see cref="ImageBackgroundModel.Thresholds.RingPadding"/>=2，
        /// 掩膜再随 3×3 膨胀吃掉 1px，原笔画的羽化最远落在原框边外约 3px——板边必须盖过它才谈得上"盖住原文"。
        /// 圆角单设旋钮没有依据，就取这同一个内距值（短边不足时按短边一半收）。
        /// </summary>
        public const float PanelPaddingPx = 4f;
    }

    /// <summary>
    /// 一块译文的落笔计划。<c>Bounds</c> 是允许排版的框（①②③ 档就是原框或借出的空白框），
    /// <c>TextTop/TextRight</c> 是这一框里实测出来的真实墨迹位置与右边界（垂直居中的算法只在这里算一次），
    /// <c>Panel/PanelColor/Contrast</c> 只在档④ 出板时非空，记板几何、板底色与板底／字色的实测通道差。
    /// </summary>
    private sealed record DrawPlan(RectangleF Bounds, IReadOnlyList<string> Lines, Font Font, Color Color,
        float LineHeight, float TextTop, float TextRight, RectangleF? Panel, Color PanelColor, int Contrast)
    {
        /// <summary>
        /// 这一块在图上真正占用的矩形：出板就是板，没出板就是排版框。
        /// 借空白与出板一律按它避让，否则后画的板会压在先画好的译文上（排版框可比板小，只避让排版框拦不住板）。
        /// </summary>
        public RectangleF Occupied => Panel ?? Bounds;
    }

    /// <summary>
    /// 逐块定版面（档①②③，兜不住走档④出板）→ 逐块抹字补图（出板的块不必抹）→ 一次性落笔（板先字后）。
    /// 任一环节不成立只作废该块（计入 <paramref name="skipped"/>），不再让一块拖垮整图。
    /// </summary>
    private static (int Expanded, int Skipped) Render(Bitmap image, int[] pixels, IReadOnlyList<OcrLineRegion> lines,
        IReadOnlyList<string> targets, string targetLanguage, CancellationToken ct)
    {
        if (lines.Count != targets.Count) throw new InvalidOperationException("翻译区域对应关系不完整，请重试");
        var plans = new List<DrawPlan>();
        var skipped = new List<string>();
        int expanded = 0;
        string family = ResolveFontFamily(targetLanguage);
        using var measureImage = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(measureImage);
        using var format = NewTextFormat();

        try
        {
            // 第一段：只算版面，不动像素。版面定不下来就不该抹字，否则原文被抹掉而译文没画上来。
            for (int i = 0; i < lines.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var bounds = lines[i].Bounds;
                if (targets[i].Length >= Layout.VerticalMinChars && bounds.Height > bounds.Width * Layout.VerticalAspect)
                {
                    skipped.Add($"区域={i} 原因=竖排");
                    continue;
                }
                if (lines.Where((_, j) => j != i).Any(other => Overlap(bounds, other.Bounds) > Layout.OverlapMax))
                {
                    skipped.Add($"区域={i} 原因=重叠");
                    continue;
                }
                var profile = ImageBackgroundModel.Analyze(pixels, image.Width, image.Height, bounds,
                    lines[i].Polygon, lines[i].CharX);
                // 手段甲根本不碰纹理／复杂（不抹字，画在原框里等于压在原文上），这两类直接进档④；
                // 纯色／渐变先试①②③这三档"看不见板子"的排版，都失败了才兜一块板。
                var plan = profile.Kind is BackgroundKind.Texture or BackgroundKind.Complex || !profile.Trusted
                    ? LayoutPanel(measure, format, family, profile, targets[i], bounds, lines, i, plans,
                        image.Width, image.Height)
                    : Layout123(measure, format, family, profile, targets[i], bounds, lines, i, plans,
                        pixels, image.Width, image.Height)
                      ?? LayoutPanel(measure, format, family, profile, targets[i], bounds, lines, i, plans,
                        image.Width, image.Height);
                if (plan == null)
                {
                    skipped.Add($"区域={i} 原因=放不下 原框={bounds.Width:F1}x{bounds.Height:F1} 译文字符={targets[i].Length} 背景={profile.Kind}");
                    continue;
                }
                // 出板的块不必抹字：板几何包含原框，盖住即净，再卷积一次是白烧。
                if (plan.Panel == null && !ImageBackgroundModel.Repair(pixels, image.Width, image.Height, profile))
                {
                    plan.Font.Dispose();
                    plan = LayoutPanel(measure, format, family, profile, targets[i], bounds, lines, i, plans,
                        image.Width, image.Height);
                    if (plan == null)
                    {
                        skipped.Add($"区域={i} 原因=补图不可信且承托板放不下 背景={profile.Describe()}");
                        continue;
                    }
                }
                if (plan.Panel != null)
                    AppLogger.Info($"[本机图片翻译] 承托板 区域={i} 背景={profile.Kind} " +
                        $"板={plan.Panel.Value.X:F0},{plan.Panel.Value.Y:F0},{plan.Panel.Value.Width:F0}x{plan.Panel.Value.Height:F0} " +
                        $"底=#{plan.PanelColor.R:X2}{plan.PanelColor.G:X2}{plan.PanelColor.B:X2} " +
                        $"字=#{plan.Color.R:X2}{plan.Color.G:X2}{plan.Color.B:X2} 对比={plan.Contrast}");
                if (plan.Bounds != bounds) expanded++;
                plans.Add(plan);
            }
            foreach (var note in skipped) AppLogger.Warn($"[本机图片翻译] 回填跳过 {note}");
            if (plans.Count == 0) return (0, lines.Count);

            WritePixels(image, pixels);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                foreach (var plan in plans)
                {
                    ct.ThrowIfCancellationRequested();
                    var state = graphics.Save();
                    // 板子先落，字后落，同一块里不留中间态；裁剪区不做反走样，所以板边走 FillPath。
                    if (plan.Panel != null)
                    {
                        var panel = plan.Panel.Value;
                        using var path = new GraphicsPath();
                        SelectionEffects.AddRoundedRect(path, panel,
                            Math.Min(Layout.PanelPaddingPx, MathF.Min(panel.Width, panel.Height) / 2f));
                        var smooth = graphics.SmoothingMode;
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        using var back = new SolidBrush(plan.PanelColor);
                        graphics.FillPath(back, path);
                        graphics.SmoothingMode = smooth;
                    }
                    graphics.SetClip(plan.Bounds);
                    using var brush = new SolidBrush(plan.Color);
                    for (int k = 0; k < plan.Lines.Count; k++)
                        graphics.DrawString(plan.Lines[k], plan.Font, brush,
                            new PointF(plan.Bounds.Left, plan.TextTop + k * plan.LineHeight), format);
                    graphics.Restore(state);
                }
            }
            // GDI+ 绘字后逐行还原透明边缘及所有 Alpha，外壳与半透明 AA 像素不参与抹字或回填。
            RestoreAlpha(image, pixels);
            return (expanded, lines.Count - plans.Count);
        }
        finally { foreach (var plan in plans) plan.Font.Dispose(); }
    }

    /// <summary>
    /// 排版量算与落笔共用的文字格式。只此一份：探针另拼一遍 flags，量出来的折行就不是线上那一套。
    /// GenericTypographic 的 LineLimit 会因 GDI+ 行框比墨迹框略高而整行不绘制，外层按排版框裁剪。
    /// </summary>
    private static StringFormat NewTextFormat() => new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.None,
        FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.FitBlackBox | StringFormatFlags.NoWrap
    };

    /// <summary>
    /// 档①②③：同一字号先试原框、横向借空白与纵向换行，全部放不下后才降低字号。
    /// 借空白只在背景平坦到 <see cref="ImageBackgroundModel.CanBorrowBlank"/> 的程度时才做，
    /// 且逐列／逐行确认整条都是本块背景，绝不跨过其他文字或已经占用的排版框。
    /// 三档都不成立返回 null，由调用方接 <see cref="LayoutPanel"/>（档④承托板）兜底——档④ 不靠背景平坦，
    /// 所以纹理／复杂背景压根不进这里：那三类手段甲不抹字，画在原框里等于把译文压在原文上面。
    /// </summary>
    private static DrawPlan? Layout123(Graphics measure, StringFormat format, string family,
        BackgroundProfile profile, string text, RectangleF bounds, IReadOnlyList<OcrLineRegion> lines, int current,
        IReadOnlyList<DrawPlan> plans, int[] pixels, int width, int height)
    {
        // 字号搜索必须在空间搜索外层；反过来会在 8px 刚塞得下时提前返回，永远不看上下留白。
        List<RectangleF>? spaces = null;
        foreach (float size in FontSizes(bounds.Height))
        {
            // 以原行高能容纳的字号为基准，不能为追逐不可单行落笔的字号，把短标签也挤成竖条。
            using var heightProbe = new Font(family, Math.Max(1f, size), FontStyle.Regular, GraphicsUnit.Pixel);
            if (heightProbe.GetHeight(measure) > bounds.Height + .5f) continue;
            var fitted = FitAtSize(measure, format, family, text, bounds, size, profile.Ink);
            if (fitted != null) return fitted;
            // 普通短译文不需要扫描整片空白；需要借用时只计算一次，后续字号复用。
            if (spaces == null)
            {
                var right = BorrowHorizontally(pixels, width, height, profile, bounds, bounds, lines, current, plans, false);
                var both = BorrowHorizontally(pixels, width, height, profile, right, bounds, lines, current, plans, true);
                spaces = new List<RectangleF> { right, both };
                foreach (var box in new[] { bounds, right, both }.Distinct())
                    spaces.Add(BorrowVertically(pixels, width, height, profile, box, bounds, lines, current, plans));
                spaces = spaces.Where(box => box != bounds).Distinct().ToList();
            }
            foreach (var space in spaces)
            {
                fitted = FitAtSize(measure, format, family, text, space, size, profile.Ink);
                if (fitted != null) return PlaceNearSource(fitted, bounds);
            }
        }
        return null;
    }

    /// <summary>沿用原字号上下限；只改变尝试顺序，不通过继续压低字号来掩盖放不下的问题。</summary>
    private static IEnumerable<float> FontSizes(float sourceHeight)
    {
        float low = Math.Min(Layout.FontMinPx, sourceHeight * .6f);
        float high = Math.Max(low, sourceHeight * Layout.FontSourceRatio);
        for (int pass = 0; pass < Layout.FontSearchPasses; pass++)
            yield return high - (high - low) * pass / (Layout.FontSearchPasses - 1);
    }

    /// <summary>借来的是可用空间，不是居中容器。按实际字宽与行数收框，首行尽量留在原文起点。</summary>
    private static DrawPlan PlaceNearSource(DrawPlan plan, RectangleF source)
    {
        float inkWidth = plan.TextRight - plan.Bounds.Left;
        float textHeight = plan.Lines.Count * plan.LineHeight;
        float width = Math.Min(plan.Bounds.Width, Math.Max(source.Width, inkWidth));
        float height = Math.Min(plan.Bounds.Height, Math.Max(source.Height, textHeight));
        // 直接算 (X + Width) - width 会因 float 消去误差落到 X 左侧（如 0.1 → 0.099998474），
        // 使 Clamp 的上限小于下限并中止整图。改在非负剩余空间内夹取位移，满宽／满高时恒为 0。
        float left = plan.Bounds.Left + Math.Clamp(source.Left - plan.Bounds.Left, 0f, Math.Max(0f, plan.Bounds.Width - width));
        float top = plan.Bounds.Top + Math.Clamp(source.Top - plan.Bounds.Top, 0f, Math.Max(0f, plan.Bounds.Height - height));
        return plan with { Bounds = new RectangleF(left, top, width, height),
            TextTop = top + Math.Max(0f, (height - textHeight) / 2f), TextRight = left + inkWidth };
    }

    /// <summary>
    /// 在一个排版框内定字号与折行：先试单行，放不下再试缩字折行（即档①与档②）。
    /// 折行必须用与落笔同一个 <see cref="StringFormat"/>，且每个成品行都要重新整行实测——
    /// 逐字累加出来的宽度不等于整行实测宽度（实施计划 §10 第 3 条实测否定）。
    /// </summary>
    private static DrawPlan? Fit(Graphics measure, StringFormat format, string family, string text,
        RectangleF box, float sourceHeight, Color color)
    {
        foreach (float size in FontSizes(sourceHeight))
        {
            var fitted = FitAtSize(measure, format, family, text, box, size, color);
            if (fitted != null) return fitted;
        }
        return null;
    }

    /// <summary>固定字号只判断能否放下；换行与逐行复测仍复用唯一的 WrapFitted。</summary>
    private static DrawPlan? FitAtSize(Graphics measure, StringFormat format, string family, string text,
        RectangleF box, float size, Color color)
    {
        using var candidate = new Font(family, Math.Max(1f, size), FontStyle.Regular, GraphicsUnit.Pixel);
        float lineHeight = candidate.GetHeight(measure);
        var single = measure.MeasureString(text, candidate, PointF.Empty, format);
        List<string> drawn;
        float widest;
        if (single.Width <= box.Width + .5f && single.Height <= box.Height + .5f)
        {
            drawn = new List<string> { text };
            widest = single.Width;
        }
        else
        {
            var wrapped = WrapFitted(measure, format, candidate, text, box, lineHeight, out widest);
            if (wrapped == null) return null;
            drawn = wrapped;
        }
        // 垂直起点只在这里算：落笔与承托板都要读同一个值，两边各写一遍居中公式迟早对不上。
        float top = box.Top + Math.Max(0f, (box.Height - drawn.Count * lineHeight) / 2f);
        return new DrawPlan(box, drawn, (Font)candidate.Clone(), color, lineHeight, top,
            box.Left + widest, null, Color.Empty, 0);
    }

    /// <summary>
    /// 档②：按框宽折行，行高累计与整行宽度都复测通过才算放下，否则返回 null 交给下一档。
    /// 复测出来的最宽行宽一并带出，承托板靠它收右边界，不必再量第三遍。
    /// </summary>
    private static List<string>? WrapFitted(Graphics measure, StringFormat format, Font font, string text,
        RectangleF box, float lineHeight, out float widest)
    {
        widest = 0f;
        var wrapped = TextStickerRenderer.WrapText(measure, font, text, (int)Math.Floor(box.Width), format);
        // 只折出一行说明宽度确实无解，不是折行能救的。
        if (wrapped.Count < 2) return null;
        // 在不增加行数的前提下收紧换行宽度，避免贪心排出“满行、满行、末尾一个字”。
        // 硬换行是原文格式，保留其行宽选择，不跨段重新平衡。
        if (text.IndexOfAny(new[] { '\r', '\n' }) < 0)
        {
            int low = 1, high = (int)Math.Floor(box.Width), rowLimit = wrapped.Count;
            for (int pass = 0; pass < Layout.FontSearchPasses && low < high; pass++)
            {
                int middle = low + (high - low) / 2;
                var balanced = TextStickerRenderer.WrapText(measure, font, text, middle, format);
                if (balanced.Count <= rowLimit && balanced.All(line =>
                    measure.MeasureString(line, font, PointF.Empty, format).Width <= middle + .5f))
                {
                    wrapped = balanced;
                    high = middle;
                }
                else low = middle + 1;
            }
        }
        foreach (var line in wrapped)
        {
            float lineW = measure.MeasureString(line, font, PointF.Empty, format).Width;
            if (lineW > box.Width + .5f) return null;
            widest = Math.Max(widest, lineW);
        }
        return wrapped.Count * lineHeight <= box.Height + .5f ? wrapped : null;
    }

    /// <summary>
    /// 档④·承托板：①②③ 都放不下（或背景是纹理／复杂、手段甲压根不抹字）时的最后一档。
    /// 就地以原框为中心按实测所需扩出一块不透明板，板底取本块自己的背景色——
    /// 允许盖住本块原文（实施计划 §11 决策 1，2026-10-27），但绝不盖别块原文、不压已画好的译文、越不出图片边界。
    /// 候选框由小到大分步推进（与纵向借空白同一节奏，短译文不会被一把推到最远边），
    /// 字号与折行仍由 <see cref="Fit"/> 一次定死；板几何＝实测墨迹范围∪原框再外扩 <see cref="Layout.PanelPaddingPx"/>，
    /// 所以候选框分步的粗量化只影响字号与折行形状，不会把板子撑大。出板的块不再走抹字：原框整体在板内，盖住即净。
    /// </summary>
    private static DrawPlan? LayoutPanel(Graphics measure, StringFormat format, string family,
        BackgroundProfile profile, string text, RectangleF bounds, IReadOnlyList<OcrLineRegion> lines, int current,
        IReadOnlyList<DrawPlan> plans, int width, int height)
    {
        // 这一档不抹字，所以用不着手段甲那套「掩膜可信」闸门（纹理背景的环上大半都被当成异色，墨占比天然超上限）；
        // 它需要的只是取样环真的取到了背景中位色与笔画色——取样失败的哨兵值这两项都是 Color.Empty，板底无从谈起。
        if (profile.Mask.Length == 0) return null;
        var blocked = BlockedRects(lines, current, plans).ToList();
        // 原框本身已经贴到别块原文（或已定版面块的占用框）上时，板几何必然包含原框，也就必然盖住别人的原文。
        // 这一档只兜自己的原文（§11 决策 1），别人的一个像素都不盖 ⇒ 直接跳过本块。
        if (blocked.Any(other => RectangleF.Inflate(other, Layout.KeepOut, Layout.KeepOut).IntersectsWith(bounds)))
            return null;
        // 先为板的外扩留位置；只避让文字框、最后再外扩板会重新压进邻句。
        var room = PanelRoom(bounds, blocked.Select(other =>
            RectangleF.Inflate(other, Layout.PanelPaddingPx, Layout.PanelPaddingPx)).ToList(), width, height);
        foreach (float size in FontSizes(bounds.Height))
        {
            using var heightProbe = new Font(family, Math.Max(1f, size), FontStyle.Regular, GraphicsUnit.Pixel);
            if (heightProbe.GetHeight(measure) > bounds.Height + .5f) continue;
            for (int step = 1; step <= Layout.FontSearchPasses; step++)
            {
                float fraction = step / (float)Layout.FontSearchPasses;
                var box = RectangleF.FromLTRB(bounds.Left - (bounds.Left - room.Left) * fraction,
                    bounds.Top - (bounds.Top - room.Top) * fraction,
                    bounds.Right + (room.Right - bounds.Right) * fraction,
                    bounds.Bottom + (room.Bottom - bounds.Bottom) * fraction);
                // 同轴带判据只顶得住正左正右正上正下的邻框；斜角上的邻框要在这里逐条退出，
                // 板子宁可少扩一条边，也不压到别人的原文。只缩不涨，缩完不会再撞上前一条，一遍就够。
                bool blockedBox = false;
                foreach (var other in blocked)
                {
                    float padding = Layout.KeepOut + Layout.PanelPaddingPx;
                    var trimmed = TrimOutOf(box, RectangleF.Inflate(other, padding, padding), bounds);
                    if (trimmed == null || !trimmed.Value.Contains(bounds)) { blockedBox = true; break; }
                    box = trimmed.Value;
                }
                if (blockedBox) continue;
                var fitted = FitAtSize(measure, format, family, text, box, size, profile.Ink);
                if (fitted == null) continue;
                fitted = PlaceNearSource(fitted, bounds);
                // 板色按板心取（渐变在板心处最贴背景），且只在版面真的定下来之后取一次。
                var inked = RectangleF.FromLTRB(fitted.Bounds.Left, fitted.TextTop, fitted.TextRight,
                    fitted.TextTop + fitted.Lines.Count * fitted.LineHeight);
                var panel = RectangleF.Intersect(
                    RectangleF.Inflate(RectangleF.Union(bounds, inked), Layout.PanelPaddingPx, Layout.PanelPaddingPx),
                    new RectangleF(0, 0, width, height));
                if (panel.Width < 1 || panel.Height < 1 || blocked.Any(other =>
                    RectangleF.Inflate(other, Layout.KeepOut, Layout.KeepOut).IntersectsWith(panel)))
                {
                    fitted.Font.Dispose();
                    continue;
                }
                var (back, color, contrast) = ImageBackgroundModel.PanelColors(profile, (panel.Left + panel.Right) / 2f,
                    (panel.Top + panel.Bottom) / 2f);
                // 字色按板底重定：原文墨色与板底够不到对比下限时，PanelColors 会退到黑／白更远的一侧。
                // with 只换这几个字段，字体仍是 Fit 里那份克隆，由 plans 统一释放，绝不能在这里先 dispose。
                return fitted with { Color = color, Panel = panel, PanelColor = back, Contrast = contrast };
            }
        }
        return null;
    }

    /// <summary>
    /// 把候选框从某个避让项的外框里退出来：哪一边退出距离最短就退哪边（板子少扩一条边，比压住别人的原文便宜）。
    /// 退到框没了返回 null，交由调用方跳过本块。
    /// </summary>
    private static RectangleF? TrimOutOf(RectangleF box, RectangleF keep, RectangleF anchor)
    {
        if (!keep.IntersectsWith(box)) return box;
        // 最短退出方向可能把本块原框也裁走；只在仍包含原框的合法方向中挑最小退出距离。
        RectangleF? best = null;
        float nearest = float.MaxValue;
        foreach (var (candidate, distance) in new[]
        {
            (RectangleF.FromLTRB(box.Left, box.Top, keep.Left, box.Bottom), box.Right - keep.Left),
            (RectangleF.FromLTRB(keep.Right, box.Top, box.Right, box.Bottom), keep.Right - box.Left),
            (RectangleF.FromLTRB(box.Left, box.Top, box.Right, keep.Top), box.Bottom - keep.Top),
            (RectangleF.FromLTRB(box.Left, keep.Bottom, box.Right, box.Bottom), keep.Bottom - box.Top),
        })
        {
            if (candidate.Width < 1 || candidate.Height < 1 || !candidate.Contains(anchor) || distance >= nearest) continue;
            best = candidate;
            nearest = distance;
        }
        return best;
    }

    /// <summary>
    /// 板子的可扩范围＝图内矩形，四条边各自被与本框同轴带相交的避让项顶回来（留 <see cref="Layout.KeepOut"/>）。
    /// 只顶同轴带：斜角上的邻框不该挡住正方向的扩展，它由 <see cref="LayoutPanel"/> 的相交确认单独收口。
    /// </summary>
    private static RectangleF PanelRoom(RectangleF bounds, IReadOnlyList<RectangleF> blocked, int width, int height)
    {
        float left = 0, top = 0, right = width, bottom = height;
        foreach (var other in blocked)
        {
            if (other.Bottom > bounds.Top && other.Top < bounds.Bottom)
            {
                if (other.Right <= bounds.Left) left = Math.Max(left, other.Right + Layout.KeepOut);
                if (other.Left >= bounds.Right) right = Math.Min(right, other.Left - Layout.KeepOut);
            }
            if (other.Right > bounds.Left && other.Left < bounds.Right)
            {
                if (other.Bottom <= bounds.Top) top = Math.Max(top, other.Bottom + Layout.KeepOut);
                if (other.Top >= bounds.Bottom) bottom = Math.Min(bottom, other.Top - Layout.KeepOut);
            }
        }
        return RectangleF.FromLTRB(Math.Min(left, bounds.Left), Math.Min(top, bounds.Top),
            Math.Max(right, bounds.Right), Math.Max(bottom, bounds.Bottom));
    }

    /// <summary>
    /// 这一块的避让清单＝别块原文框＋已定版面块的占用框（出过板的那块读板，不读比板小的排版框）。
    /// 档③ 借空白与档④ 出板共用同一份枚举，两处各写一遍必然写漏。
    /// </summary>
    private static IEnumerable<RectangleF> BlockedRects(IReadOnlyList<OcrLineRegion> lines, int current,
        IReadOnlyList<DrawPlan> plans)
    {
        for (int i = 0; i < lines.Count; i++) if (i != current) yield return lines[i].Bounds;
        foreach (var plan in plans) yield return plan.Occupied;
    }

    /// <summary>横向借空白：仅在同一行带的连续纯背景上外扩，受类别闸门、逐条干净判据、借用上限与避让清单四重约束。</summary>
    private static RectangleF BorrowHorizontally(int[] pixels, int width, int height, BackgroundProfile profile,
        RectangleF box, RectangleF anchor, IReadOnlyList<OcrLineRegion> lines, int current,
        IReadOnlyList<DrawPlan> plans, bool left)
    {
        if (!ImageBackgroundModel.CanBorrowBlank(profile)) return box;
        float growth = Math.Max(Layout.GrowthPx, anchor.Width * Layout.GrowthRatio);
        float limit = left ? Math.Max(0, anchor.Left - growth) : Math.Min(width, anchor.Right + growth);
        void Avoid(RectangleF other)
        {
            if (other.Bottom <= box.Top || other.Top >= box.Bottom) return;
            if (left && other.Left < anchor.Left) limit = Math.Max(limit, Math.Min(anchor.Left, other.Right + Layout.KeepOut));
            if (!left && other.Right > anchor.Right) limit = Math.Min(limit, Math.Max(anchor.Right, other.Left - Layout.KeepOut));
        }
        foreach (var other in BlockedRects(lines, current, plans)) Avoid(other);

        int top = Math.Max(0, (int)Math.Floor(box.Top)), bottom = Math.Min(height, (int)Math.Ceiling(box.Bottom));
        if (left)
        {
            float edge = box.Left;
            for (int x = (int)Math.Floor(box.Left) - 1; x >= Math.Ceiling(limit); x--)
            {
                if (!ImageBackgroundModel.IsBlankStrip(pixels, width, height, profile, true, x, top, bottom)) break;
                edge = x;
            }
            return RectangleF.FromLTRB(edge, box.Top, box.Right, box.Bottom);
        }
        float right = box.Right;
        for (int x = (int)Math.Ceiling(box.Right); x < Math.Floor(limit); x++)
        {
            if (!ImageBackgroundModel.IsBlankStrip(pixels, width, height, profile, true, x, top, bottom)) break;
            right = x + 1;
        }
        return RectangleF.FromLTRB(box.Left, box.Top, right, box.Bottom);
    }

    /// <summary>纵向借空白：只在原框上下的连续均匀空带内扩展，返回可用的最大框。</summary>
    private static RectangleF BorrowVertically(int[] pixels, int width, int height, BackgroundProfile profile,
        RectangleF box, RectangleF anchor, IReadOnlyList<OcrLineRegion> lines, int current, IReadOnlyList<DrawPlan> plans)
    {
        if (!ImageBackgroundModel.CanBorrowBlank(profile)) return box;
        float reach = Math.Max(Layout.VerticalGrowthPx, anchor.Height * Layout.VerticalGrowthRatio);
        float topLimit = Math.Max(0, anchor.Top - reach), bottomLimit = Math.Min(height, anchor.Bottom + reach);
        void Avoid(RectangleF other)
        {
            if (other.Right <= box.Left || other.Left >= box.Right) return;
            if (other.Top < anchor.Top) topLimit = Math.Max(topLimit, Math.Min(anchor.Top, other.Bottom + Layout.KeepOut));
            if (other.Bottom > anchor.Bottom) bottomLimit = Math.Min(bottomLimit, Math.Max(anchor.Bottom, other.Top - Layout.KeepOut));
        }
        foreach (var other in BlockedRects(lines, current, plans)) Avoid(other);

        int left = Math.Max(0, (int)Math.Floor(box.Left)), right = Math.Min(width, (int)Math.Ceiling(box.Right));
        float top = box.Top, bottom = box.Bottom;
        for (int y = (int)Math.Ceiling(box.Bottom); y < Math.Floor(bottomLimit); y++)
        {
            if (!ImageBackgroundModel.IsBlankStrip(pixels, width, height, profile, false, y, left, right)) break;
            bottom = y + 1;
        }
        for (int y = (int)Math.Floor(box.Top) - 1; y >= Math.Ceiling(topLimit); y--)
        {
            if (!ImageBackgroundModel.IsBlankStrip(pixels, width, height, profile, false, y, left, right)) break;
            top = y;
        }
        return RectangleF.FromLTRB(box.Left, top, box.Right, bottom);
    }

    private static void RestoreAlpha(Bitmap image, int[] pixels)
    {
        var locked = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                var address = IntPtr.Add(locked.Scan0, y * locked.Stride);
                Marshal.Copy(address, row, 0, row.Length);
                for (int x = 0; x < row.Length; x++)
                {
                    int original = pixels[y * image.Width + x];
                    row[x] = (uint)original >> 24 < 255 ? original : (row[x] & 0x00ffffff) | (original & unchecked((int)0xff000000));
                }
                Marshal.Copy(row, 0, address, row.Length);
            }
        }
        finally { image.UnlockBits(locked); }
    }

    /// <summary>按目标语言挑字体族；只校验是否已安装，缺字形的逐字回退本批未做，不宣称已支持全部语种。</summary>
    private static string ResolveFontFamily(string targetLanguage)
    {
        string[] candidates = targetLanguage switch
        {
            "ja" => new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic" },
            "ko" => new[] { "Malgun Gothic", "Batang" },
            "ru" or "uk" or "bg" or "sr" => new[] { "Segoe UI", "Arial" },
            "ar" or "he" or "fa" or "ur" => new[] { "Segoe UI", "Tahoma" },
            "th" => new[] { "Leelawadee UI", "Tahoma" },
            "zh" or "cht" => new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" },
            _ => new[] { "Microsoft YaHei UI", "Segoe UI", "Arial" }
        };
        using var installed = new InstalledFontCollection();
        var available = installed.Families.ToDictionary(f => f.Name, _ => true, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
            if (available.ContainsKey(candidate)) return candidate;
        return "Microsoft YaHei UI";
    }

    private static int[] ReadPixels(Bitmap image)
    {
        var pixels = new int[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < image.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * image.Width, image.Width); }
        finally { image.UnlockBits(data); }
        return pixels;
    }

    private static void WritePixels(Bitmap image, int[] pixels)
    {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < image.Height; y++) Marshal.Copy(pixels, y * image.Width, IntPtr.Add(data.Scan0, y * data.Stride), image.Width); }
        finally { image.UnlockBits(data); }
    }
}
