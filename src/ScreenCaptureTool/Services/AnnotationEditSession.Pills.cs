using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Services;

/// <summary>
/// <see cref="AnnotationEditSession"/> 的浮条半边：聚光灯圆角浮条与序号浮条的贴位、收起、改值与攒档。
///
/// <para>两枚都是与截图侧<b>同一个控件</b>（<see cref="SpotlightRadiusPill"/> / <see cref="CounterPill"/>），
/// 差别只在坐标换算与提交口径：贴位算式统一为"内容坐标 × 图层缩放系数 ＋ 图层 Margin"，宿主只交代
/// 承载面板的夹取矩形；提交口径上，贴图的一次 Commit 要克隆整张干净底图并重渲染全部图元，
/// 故连续量（圆角／胶囊圆角）攒到停手后落一次，录屏侧那份委托是空的、攒档同样成立。</para>
/// </summary>
internal sealed partial class AnnotationEditSession
{
    // ── 聚光灯圆角浮条（2026-09-18）──
    // 服务「矩形 + 圆角」这一族：矩形档聚光灯与矩形图元。两者的圆角在 Core 里都只是"矩形 + 一个半径"，
    // 判定与渲染都只看矩形本身，故浮条一视同仁；椭圆档没有圆角概念，直接收起。
    // 浮条是这一族圆角的**唯一入口**（四角手柄已于 2026-09-18 整体退役）。

    private bool _spotlightRadiusPending;
    private DispatcherTimer? _spotlightRadiusCommitTimer;

    /// <summary>
    /// 把圆角浮条贴到选中图元的<b>正上方</b>；无选中、或选中的图元不属于「矩形 + 圆角」这一族时收起。
    /// 几何取图层里<b>预览优先</b>的那一个（<c>GetSelectedShapeForOverlay</c>），与画布上那圈选中手柄
    /// 同源——拖动/缩放期间文档里还是旧几何，照文档算会让浮条落在图元后面一帧。
    /// </summary>
    public void RefreshSpotlightRadiusPill()
    {
        SpotlightRadiusPill? pill = _host.RadiusPill;
        AnnotationLayer? layer = _host.EditLayer;
        if (pill == null || layer == null) return;

        AnnotationShape? sel = layer.GetSelectedShapeForOverlay();
        System.Drawing.RectangleF? bounds = sel switch
        {
            SpotlightShape { ShapeKind: SpotlightShapeKind.Rectangle } sp => sp.Rect,
            RectangleShape { ShapeKind: ShapeAnnotationKind.Rectangle } rc => rc.Rect,
            _ => null,
        };
        if (bounds is not { } rect)
        {
            HideSpotlightRadiusPill();
            return;
        }

        // 内容坐标 → 窗口 DIP：系数与图层同一份（ScreenToDipScale），位置再叠上图层自己的 Margin
        // —— 缩放与信封态下它会被平移，不叠就是"图缩小了浮条还在老地方"。
        double scale = layer.ScreenToDipScale;
        double w = rect.Width * scale;
        double h = rect.Height * scale;
        if (scale <= 0 || w <= 0 || h <= 0)
        {
            HideSpotlightRadiusPill();
            return;
        }
        double left = layer.Margin.Left + rect.Left * scale;
        double top = layer.Margin.Top + rect.Top * scale;

        // 微调态下**不重算位置**（2026-09-18）：此刻指针被浮条捕获，图元与选区都不可能动，重算纯属白跑
        // （每次都要 MeasureNow 一次控件）。留着也不出错，但一次拖动几十帧、每帧一次强制量测，
        // 白白压在手指移动的帧上。
        if (pill.IsFineActive) return;

        // ⚠ 必须先置可见再量宽定位：不可见元素显式 Measure 得 0×0（同 LayoutMetrics 的坑 A）
        pill.SetRadius(CornerRadiusOf(sel));
        pill.IsVisible = true;
        Size size = LayoutMetrics.MeasureNow(pill);

        Rect clamp = _host.PillClampBounds;
        // 夹取可见黑条的左右边缘。控件两侧还留有透明的微调区，窄贴图也能把黑条对准图元。
        Rect bar = pill.PillBounds;
        double x = Math.Clamp(left + w / 2.0 - size.Width / 2.0,
            -bar.Left, Math.Max(-bar.Left, clamp.Width - bar.Right));
        double y = top - size.Height - PillGap;
        if (y < 0) y = top + h + PillGap;   // 贴到画板顶边时翻到元素下方
        y = Math.Max(0, Math.Min(y, Math.Max(0, clamp.Height - size.Height)));
        // Canvas 坐标与 DesiredSize 独立，改填充等不改变几何的操作会得出同一个位置。
        Canvas.SetLeft(pill, x);
        Canvas.SetTop(pill, y);
    }

    /// <summary>
    /// 「矩形 + 圆角」这一族的圆角读数。非该族返回 0——两个调用点都在此之前按族过滤过，
    /// 故这个 0 只是兜底，不是"椭圆也能调圆角"。
    /// </summary>
    private static double CornerRadiusOf(AnnotationShape? shape) => shape switch
    {
        SpotlightShape sp => sp.CornerRadius,
        RectangleShape rc => rc.CornerRadius,
        _ => 0,
    };

    /// <summary>
    /// 收起圆角浮条。
    /// <para>⚠ 这里**刻意不落定**攒着的圆角改动：落定会走一次 Commit，而 Commit 会重绘图层、
    /// 进而触发一次选中几何通知，把浮条在"拖动/缩放选区"这条路上重新显示回来。
    /// 攒着的那次改动由定时器兜底（停手 260ms 必然落一次），退出标注前另有显式落定。</para>
    /// </summary>
    public void HideSpotlightRadiusPill()
    {
        SpotlightRadiusPill? pill = _host.RadiusPill;
        if (pill == null || !pill.IsVisible) return;
        pill.Collapse();
        pill.IsVisible = false;
    }

    /// <summary>浮条改值：改选中图元的圆角。拖动期间<b>只改文档 + 重绘图层</b>，历史与成品攒到停手后再落一次。</summary>
    private void OnSpotlightRadiusPillChanged(double radius)
    {
        var history = _host.History;
        if (history == null) return;

        AnnotationShape? next = SelectedShape() switch
        {
            SpotlightShape sp => sp.WithCornerRadius((float)radius),
            RectangleShape rc => rc.WithCornerRadius((float)radius),
            _ => null,
        };
        if (next == null) return;

        history.Document.ReplaceShape(next);
        _host.InvalidateAnnotationPreview();
        // 圆角记忆回灌参数条：调完这颗，下一颗新画的同族图元也是这个圆角
        //（与截图侧 SyncSpotlightToolState / SyncShapeCornerRadius 的圆角回显同一意图）。
        if (_host.EditToolbar is { } tb)
        {
            if (next is RectangleShape) tb.SyncShapeCornerRadius(radius);
            else tb.SyncSpotlightCornerRadius(radius);
        }
        ScheduleSpotlightRadiusCommit();
    }

    /// <summary>把圆角改动攒到停手后落一次（拖动与滚轮共用；连续改值会把上一次的定时器推后）。</summary>
    private void ScheduleSpotlightRadiusCommit()
    {
        _spotlightRadiusPending = true;
        _spotlightRadiusCommitTimer ??= CreateSpotlightRadiusCommitTimer();
        _spotlightRadiusCommitTimer.Stop();
        _spotlightRadiusCommitTimer.Start();
    }

    private DispatcherTimer CreateSpotlightRadiusCommitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ContinuousCommitDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 🔴 手指还按着就不落定——这是"越调越闪、越调越卡"的正解，不是优化：
            // 本定时器只按"最后一次改值后 260ms"计时，**并不知道手指还在不在**。慢慢拖或拖到一半
            // 停手端详时，每一步之间轻易超过 260ms，于是整图重绘就压在手指移动的帧上。
            // 松手后没有任何改值了，260ms 后必然落定一次。
            if (_host.RadiusPill?.IsFineActive == true) { timer.Start(); return; }
            FlushSpotlightRadiusCommit();
        };
        return timer;
    }

    /// <summary>立即落定攒着的圆角改动（停手、浮条收起、退出标注前都会调到）。幂等。</summary>
    public void FlushSpotlightRadiusCommit()
    {
        _spotlightRadiusCommitTimer?.Stop();
        if (!_spotlightRadiusPending) return;
        _spotlightRadiusPending = false;
        Core.AppLogger.Debug("[SpotPill] 落定 Commit");
        CommitChange();
    }

    // ── 序号浮条（2026-09-18）──
    // − / + 改编号、〔胶囊时〕圆角、中间那格写说明、→ 藏引线、× 删掉这一个。
    // 提交口径分两类：编号/说明/删除都是"一次点击＝一次改动"，当场落历史；
    // 而「圆角」是逐帧拖的连续量，与上面圆角浮条同病，攒到停手后落一次。

    private bool _counterCornerPending;
    private DispatcherTimer? _counterCornerCommitTimer;

    /// <summary>
    /// 把序号浮条贴到选中序号<b>外形盒</b>的正上方；选中的不是序号就收起。
    /// 居中基准取外形盒而非"圆点＋说明文字"整块：说明可以很长，按整块居中会把条推到画面外，
    /// 而用户点的是圆点，条对齐圆点才对得上视线（与截图侧同一条）。
    /// </summary>
    public void RefreshCounterPill()
    {
        CounterPill? pill = _host.CounterPillView;
        AnnotationLayer? layer = _host.EditLayer;
        if (pill == null || layer == null) return;

        if (layer.GetSelectedShapeForOverlay() is not CounterShape counter)
        {
            HideCounterPill();
            return;
        }

        // showTail：只有这一颗拖出过引线，那一格才有意义；tailShown：按钮明暗只读图元真相，不自留状态
        pill.SetState(counter.Label, counter.Note, (int)Math.Round(counter.CapsuleCorner),
            counter.Glyph == CounterGlyphShape.Capsule, counter.Tail is not null, counter.TailShown);
        // 正在输入说明文字时不重摆位置：宿主每改一次文档都会回灌一次，位置跟着跳一下就把光标打断了
        if (pill.IsEditingNote) return;
        // 微调态下也不重摆（与圆角浮条同一处理）：此刻指针被浮条捕获、图元不会动，重算纯属白跑一次
        // 强制量测；更要紧的是黑条正被控制器撑宽，位置跟着量测结果漂会抖。
        if (pill.IsFineActive) return;

        System.Drawing.RectangleF glyph = counter.GlyphRect;
        double scale = layer.ScreenToDipScale;
        double w = glyph.Width * scale;
        double h = glyph.Height * scale;
        if (scale <= 0 || w <= 0 || h <= 0)
        {
            HideCounterPill();
            return;
        }
        double left = layer.Margin.Left + glyph.Left * scale;
        double top = layer.Margin.Top + glyph.Top * scale;

        // 同屏只留一条浮条：这颗号被选中要浮出浮条了，参数条「样式」下面那排小样先收（截图侧同一口径）
        _host.EditToolbar?.CloseCounterStyleBar();
        // ⚠ 必须先置可见再量宽定位：不可见元素显式 Measure 得 0×0（同 LayoutMetrics 的坑 A）
        pill.IsVisible = true;
        Size size = LayoutMetrics.MeasureNow(pill);

        Rect clamp = _host.PillClampBounds;
        double x = left + w / 2.0 - size.Width / 2.0;
        double y = top - size.Height - PillGap;
        if (y < 0) y = top + h + PillGap;   // 贴到画板顶边时翻到元素下方
        x = Math.Max(0, Math.Min(x, Math.Max(0, clamp.Width - size.Width)));
        y = Math.Max(0, Math.Min(y, Math.Max(0, clamp.Height - size.Height)));
        pill.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>收起序号浮条（手势起手、退出标注、选中的不是序号都走这里）。
    /// ⚠ 与 <see cref="HideSpotlightRadiusPill"/> 同一条：刻意不落定攒着的胶囊圆角改动。</summary>
    public void HideCounterPill()
    {
        CounterPill? pill = _host.CounterPillView;
        if (pill == null || !pill.IsVisible) return;
        pill.Collapse();
        pill.IsVisible = false;
    }

    private void OnCounterPillNumberStepped(int delta) => StepCounterNumber(delta, "浮条");

    /// <summary>改<b>这一个</b>序号的编号（下限 1）。撞号只提示不阻止，与截图侧同一口径。
    /// 浮条的 − / + 与键盘 + / − 共用这一份落地，只在日志里区分来路。</summary>
    public void StepCounterNumber(int delta, string origin)
    {
        var history = _host.History;
        if (history == null) return;
        if (SelectedShape() is not CounterShape counter) return;
        int nextNumber = Math.Max(1, counter.Number + delta);
        if (nextNumber == counter.Number) return;
        ReplaceSelectedCounter(counter.WithNumber(nextNumber));
        _host.LogDiag(origin + "改编号 " + counter.Number + "→" + nextNumber + " id=" + counter.Id.ToString("N"));
        if (history.Document.Shapes.Any(s =>
                s is CounterShape other && other.Id != counter.Id && other.Number == nextNumber))
            Core.AppLogger.Info("AnnotationEditSession.CounterStep: 编号 " + nextNumber + " 已被另一个序号占用（按口径允许，仅提示）");
    }

    /// <summary>浮条那格说明文字提交（空串＝清除）。值没变就整个跳过，免得一次失焦在历史里留一条空记录。</summary>
    private void OnCounterPillNoteCommitted(string note)
    {
        if (SelectedShape() is not CounterShape counter) return;
        if (counter.Note == note) return;
        ReplaceSelectedCounter(counter.WithNote(note));
        _host.LogDiag("浮条说明文字 " + (note.Length == 0 ? "（已清除）" : "\"" + note + "\"")
            + " id=" + counter.Id.ToString("N"));
    }

    /// <summary>× 与 Delete 键删的是同一个目标，走同一条通路（内部已带"按设置项决定要不要重编号"）。</summary>
    private void OnCounterPillDeleteRequested() => DeleteSelected();

    /// <summary>
    /// 浮条「圆角」那一格改值（百分比）：只作用于当前选中的这一颗胶囊。
    /// 与 <see cref="OnSpotlightRadiusPillChanged"/> 同一条口径——拖动期间只改文档 + 重绘预览，
    /// 历史与成品攒到停手后落一次。
    /// </summary>
    private void OnCounterPillCapsuleCornerChanged(int corner)
    {
        var history = _host.History;
        if (history == null) return;
        if (SelectedShape() is not CounterShape { Glyph: CounterGlyphShape.Capsule } counter) return;
        float next = Math.Clamp((float)corner, 0f, 100f);
        if (Math.Abs(counter.CapsuleCorner - next) < 0.01f) return;

        history.Document.ReplaceShape(counter.WithCapsuleCorner(next));
        _host.InvalidateAnnotationPreview();
        // 记忆回灌参数条：调完这颗，下一颗新画的胶囊也是这个圆角（与圆角浮条那一路同一意图）
        _host.EditToolbar?.SyncCounterCapsuleCorner(next);
        ScheduleCounterCornerCommit();
    }

    /// <summary>
    /// 浮条「→」：藏掉／画出这一颗的<b>整条</b>引线。图元里的引线端点原样留着，只是不画、也点不中，
    /// 再点一次按原样回来（藏不等于删——用户拖出来的那个方向不该被一次误点吃掉）。
    /// 与编号/说明同一类：单击是离散量，直接 <see cref="ReplaceSelectedCounter"/> 当场落一次历史。
    /// </summary>
    private void OnCounterPillTailToggled()
    {
        if (SelectedShape() is not CounterShape counter || counter.Tail is null) return;
        bool nextShown = !counter.TailShown;
        ReplaceSelectedCounter(counter.WithTailShown(nextShown));
        _host.LogDiag("浮条引线 " + counter.Label + " →" + (nextShown ? "显示" : "藏起")
            + " id=" + counter.Id.ToString("N"));
    }

    /// <summary>把胶囊圆角改动攒到停手后落一次（拖动与滚轮共用；连续改值会把上一次的定时器推后）。</summary>
    private void ScheduleCounterCornerCommit()
    {
        _counterCornerPending = true;
        _counterCornerCommitTimer ??= CreateCounterCornerCommitTimer();
        _counterCornerCommitTimer.Stop();
        _counterCornerCommitTimer.Start();
    }

    private DispatcherTimer CreateCounterCornerCommitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ContinuousCommitDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 🔴 手指还按着就不落定（与圆角浮条那一段同一条红线）
            if (_host.CounterPillView?.IsFineActive == true) { timer.Start(); return; }
            FlushCounterCornerCommit();
        };
        return timer;
    }

    /// <summary>立即落定攒着的胶囊圆角改动（停手、退出标注前都会调到）。幂等。</summary>
    public void FlushCounterCornerCommit()
    {
        _counterCornerCommitTimer?.Stop();
        if (!_counterCornerPending) return;
        _counterCornerPending = false;
        Core.AppLogger.Debug("[CounterPill] 落定胶囊圆角 Commit");
        CommitChange();
    }

    /// <summary>
    /// 换上一个改过字段的序号图元并当场落一次历史。
    /// <c>With*</c> 系列保留原 id ⇒ 选中态不丢，浮条不会因"没有选中图元"而当场收起。
    /// </summary>
    private void ReplaceSelectedCounter(CounterShape next)
    {
        var history = _host.History;
        if (history == null) return;
        history.Document.ReplaceShape(next);
        _host.InvalidateAnnotationPreview();
        CommitChange();
    }

    /// <summary>
    /// 类型档（123 / ABC / ⅠⅡⅢ / 一二三）改的是图上<b>已有</b>的全部序号——与外形/底色那几档"只影响下一笔"不同：
    /// 同一个编号换类型，留一半旧样式就等于一张图上两套编号体系（截图侧同一条）。
    /// 先确认确实有图元要改再提交，否则一次点击会在历史里留一条空记录。
    /// </summary>
    public void ApplyCounterSerialKindToAll(CounterSerialKind kind)
    {
        var history = _host.History;
        if (history == null) return;
        AnnotationDocument document = history.Document;
        if (!document.Shapes.OfType<CounterShape>().Any(c => c.Serial != kind)) return;
        int changed = document.ApplyCounterSerialKind(kind);
        _host.InvalidateAnnotationPreview();
        CommitChange();
        _host.LogDiag("类型档已回灌 " + changed + " 个序号 → " + kind);
    }
}
