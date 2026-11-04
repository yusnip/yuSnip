using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图的划词工具栏（2026-09-29）。
///
/// 覆盖**一切能选中文字**的贴图形态，两套选区宿主各走各的取值口径：
/// - 文本／Markdown 贴图：原生 <see cref="SelectableTextBlock"/> 选区，几何现取 <c>TextLayout</c>；
/// - 图片贴图：Alt 快速选词与持久「文本可选择」共用的那套字级覆盖层（<see cref="_selectedWordIndices"/>
///   ＋ <see cref="_wordRegions"/>），它没有 TextLayout，几何＝OCR 框 × <c>WordDisplayScale / SafeScaling</c>，
///   拼文本与 <see cref="CopySelectedText"/> 逐字同一套（按行分组、行内按左排序）。
///
/// 外观直接抄截图工具栏那一套双浮层配方（2026-09-29 同日已上提为全局
/// <c>selectionFloatBar</c>／<c>selectionFloatBarPlate</c>，按钮与图标复用全局 <c>toolIcon</c>／<c>Path.icon</c>），
/// 按钮语义则按"贴图只读一段文字"重做：
/// 复制 / 荧光 / 虚线 / 波浪线 / 翻译——三档装饰平铺成三枚按钮。
/// 2026-09-30 按设计稿拍板落地：① 图标族二版（翻译逐字复用截图工具栏「文A」、三档装饰换
/// 「两行文字＋装饰基线」族）；② 内板 WrapPanel 垂直居中修对齐。
///
/// 🔴 同日两次重设计翻译交互，**以第二版为准**：
/// - 第一版「指向标记」（瞬态）已作废——点「翻译」铺品牌紫高亮块＋末行吸附「译」徽标，
///   点徽标才出气泡，且失活／点别处／Esc／重新划选四路都会整组退场。它有两个硬伤：
///   标记是瞬态的（用户一点击别处译文就没了，没法把译好的贴图留着看），且退场判据踩中
///   "点翻译那一次松手排队的重定位"竞态（点下去像没反应）。
/// - 第二版「持久标记」（现行）：点「翻译」＝原文每段下方画一道<b>品牌紫下划直线</b>、
///   右上角落一枚<b>紫点</b>（<see cref="RedrawTranslateMarks"/>），鼠标<b>悬浮</b>紫点展开译文气泡、
///   移开延时收起（<see cref="ShowTranslateBubble"/>），气泡左上角一枚<b>红色圆形 ✕</b> 清除该条
///   （<see cref="OnTranslateBubbleCloseClick"/>）。多段翻译可并存；失活、点贴图别处、Esc、
///   重新划选<b>都不退场</b>，只有点红 ✕（或贴图关闭）才清除。
///   紫线并进三档装饰那条重画通路，故跟着排版重排与缩放走、也会被对外输出的位图收进去；
///   竞态随之结构性消失——浮条重定位不再触碰翻译标记，无需再比对矩形身份。
///
/// 2026-09-30 另按需求下线划词「AI 解释／AI 联网搜索」两枚按钮及其整条调用链
/// （快答窗源码、提示词预制、快速模式设置项一并移除）。
///
/// 选区身份一律存"可重放的最小事实"而非控件引用或矩形：块选区存「块下标＋块内全文＋字符区间」
/// （Markdown 块会重排重建，原生选区失焦即清空），字级选区存「字下标＋当时拼出的文本」
/// （图像处理/旋转重建 OCR 缓存后下标语义就变了，靠文本对账把失效的装饰整条跳过）。
/// 几何永远从当时的排版现取，不缓存矩形。
/// </summary>
public partial class StickerWindow
{
    /// <summary>一段选区的稳定身份（<see cref="Block"/> 只有实时选区才填，快照里为空）。</summary>
    private sealed class StickerSelRange
    {
        public int BlockIndex;
        public string BlockText = "";
        public int Start;
        public int Length;
        public SelectableTextBlock? Block;
    }

    /// <summary>图片贴图字级选区的一次采集结果：下标、文本、以及浮条定位要的矩形。</summary>
    private sealed class StickerWordSel
    {
        /// <summary>按「行号升序 → 行内左缘升序」排好的字下标（与拼接文本的顺序一致）。</summary>
        public List<int> Indices = new();
        public string Text = "";
        /// <summary>目标坐标系里、同行相邻字已合并成连续段的矩形组。</summary>
        public List<Rect> Rects = new();
    }

    /// <summary>
    /// 一条划词装饰：<c>hl</c>＝荧光，<c>dash</c>＝虚线，<c>wave</c>＝波浪线（与 XAML 里按钮的 Tag 逐字一致）。
    /// <see cref="WordIndices"/> 非空＝图片贴图的字级装饰（画在 StickerCanvas 上）；
    /// 为空＝文本／Markdown 的块装饰（画在 TextDecorLayer 上，用 BlockIndex/Start/Length 定位）。
    /// </summary>
    private sealed class StickerSelMark
    {
        public string Kind = "hl";
        public int BlockIndex;
        public string BlockText = "";
        public int Start;
        public int Length;
        public List<int>? WordIndices;
        public string WordText = "";
        /// <summary>落笔时那几段连续字的外接框，单位＝<b>OCR 像素</b>（不是屏幕 DIP，见 <see cref="ApplyWordMark"/>）。</summary>
        public List<Rect>? WordRects;
    }

        /// <summary>
        /// 一条<b>持久</b>划词翻译标记（2026-09-30 二次重设计）。
        /// 身份口径与 <see cref="StickerSelMark"/> 逐字相同：块侧＝「块下标＋块内全文＋字符区间」，
        /// 图片侧＝「字下标＋当时拼出的文本＋OCR 像素外接框」；几何永远在重画时现取，
        /// 故排版重排、缩放、Markdown 重建都不会让紫线与紫点错位。<see cref="WordIndices"/> 非空＝图片贴图那一条。
        ///
        /// 🔴 译文与请求态挂在<b>标记自己身上</b>而不是窗口字段上——多段翻译并存时，
        /// 挂窗口上就会后到的结果覆盖先到的、气泡读错条。
        /// </summary>
        private sealed class StickerTransMark
        {
            /// <summary>块侧身份：一次划选可能跨多个 Markdown 块，故存一组区间。
            /// 只留"可重放的最小事实"，<see cref="StickerSelRange.Block"/> 恒为 null（控件会随重排重建）。</summary>
            public List<StickerSelRange> Blocks = new();
            /// <summary>图片侧身份：排好序的字下标（非空即图片贴图那条通路）。</summary>
            public List<int>? WordIndices;
            public string WordText = "";
            /// <summary>落笔时那几段连续字的外接框，单位＝<b>OCR 像素</b>（不是屏幕 DIP）。</summary>
            public List<Rect>? WordRects;
            /// <summary>送去翻译的原文，取值与「复制」逐字同一条通路（<see cref="GetSelectedTextForAction"/>），
            /// 存下来之后气泡与复制都不再回查控件——控件可能已随重排重建。</summary>
            public string SourceText = "";

            // ── 译文进行态 ──
            public string TransText = "";
            public bool Done;
            public bool IsError;
            /// <summary>语言对（气泡角标行「源 → 目标」）。</summary>
            public (string Src, string Tgt) Langs;
            public TranslationProvider Provider;
            /// <summary>本条自己的取消令牌：重译或清除时掐掉在飞的那一次，别让两条译文前后打架。</summary>
            public CancellationTokenSource? Cts;

            // ── 运行期图元（不入身份，随标记创建与销毁） ──
            /// <summary>右上角那枚紫点（16×16 透明命中盒 ⊃ 8 DIP 可见圆点）。必须吃命中才悬浮得到气泡。</summary>
            public Border? Dot;
            /// <summary>紫点命中盒在 RootGrid 坐标系里的左上角（气泡、小方角与红 ✕ 都按它摆）。</summary>
            public Point DotAnchor;
        }

    private bool _selectionToolbarShown;
    private readonly List<StickerSelRange> _selSnapshot = new();
    private readonly List<StickerSelMark> _selMarks = new();
    /// <summary>画在图片贴图 StickerCanvas 上的那几枚图元（清覆盖层／重画时按这份账回收）。</summary>
    private readonly List<Shape> _imageMarkShapes = new();
    /// <summary>浮条出现那一刻的字级选区快照（按下按钮后原生覆盖层可能已被收尾清掉）。</summary>
    private List<int> _selWordSnapshot = new();
    private string _selWordTextSnapshot = "";
    /// <summary>工具栏这一刻的矩形（窗口坐标系 DIP）。</summary>
    private Rect _selBarRect;
    // ── 划词翻译「持久标记」态（2026-09-30 二次重设计）：点「翻译」＝原文下划紫线＋右上角落紫点，
    //    悬浮紫点展开译文气泡、移开收起，气泡左上角红 ✕ 清除该条；多段并存，失活与点击别处都不退场 ──

    /// <summary>本贴图上所有已落笔的翻译标记（多段并存，逐条独立请求、独立紫点、独立译文）。</summary>
    private readonly List<StickerTransMark> _transMarks = new();
    /// <summary>图片贴图上那几道紫线的图元账本（画在 StickerCanvas 上，重画时按这份回收）。
    /// 与 <see cref="_imageMarkShapes"/> 分开记：那一层还住着字级覆盖层与三档装饰，不能整层清。</summary>
    private readonly List<Shape> _imageTransShapes = new();
    /// <summary>气泡当前服务的那一条标记。气泡、小方角与红 ✕ 全场只有一套，按它渲染与定位；
    /// 收气泡即置 null，故 <c>_activeTransMark == null</c> 也就是"气泡没开"。</summary>
    private StickerTransMark? _activeTransMark;
    /// <summary>气泡显隐态与是否翻到紫点上方。</summary>
    private bool _bubbleShown;
    private bool _bubbleAbove;
    /// <summary>紫点命中盒边长（与 XAML 里 <c>Border.transDotHit</c> 的宽高逐字一致，改一处必改两处）。</summary>
    private const double TransDotBox = 16;
    /// <summary>红 ✕ 边长（与 <c>Button.transBubbleClose</c> 一致）。</summary>
    private const double TransCloseSize = 16;
    /// <summary>
    /// 气泡收起延时器。紫点与气泡之间隔着 6 DIP 的空隙，指针走过去必然先触发紫点的 PointerExited；
    /// 不延时就会在过渡途中闪闭，气泡里的「复制」与红 ✕ 永远点不到。180ms 够走完这段路，
    /// 又短到"移开就收"不显粘滞。🔴 延时期间若指针进了气泡（或红 ✕），计时必须被取消。
    /// </summary>
    private DispatcherTimer? _bubbleHideTimer;

    /// <summary>
    /// 🔴 一次性抑制：吞掉「下一次」划词浮条重定位回调。
    ///
    /// 只服务点「翻译」那一刻的竞态：触发 Button.Click 的那次松手**先**走窗级 PointerReleased
    /// （Tunnel，StickerWindow.axaml.cs:4012 只判左键，两套宿主都走），其末尾排下的
    /// UpdateSelectionToolbarPosition 以 Background 优先级**晚于** Click 执行；而浮条按钮
    /// Focusable=False、点它既不清原生选区也不清字级选区 ⇒ 回调按「选区还在」把刚收起的浮条
    /// 重新摆回来（与「（三）浮条被排队回调顶回来」同族，成因同为 Tunnel 抬手 + Background 晚到）。
    ///
    /// 之所以不去清选区：字级那侧的规范收尾是 EndAltQuickSelect()，它连带把 _altQuickSelect
    /// 模式与光标一起收掉；块级那侧 ClearTextStickerSelection() 会抹掉用户眼前的蓝色选区——
    /// 两者都是超出「收浮条 + 出气泡」这一诉求的状态变更。浮条该不该显示本由「有没有活选区」
    /// 驱动，这里不篡改判据，只挡掉那一个由陈旧松手排下的回调；**只吞一次**，其后合法的重定位
    /// （再划选、方向键扩选、Alt 收尾）一概照常。
    /// </summary>
    private bool _suppressNextToolbarUpdate;

    /// <summary>设置项「贴图 → 划词工具栏」。</summary>
    private bool IsSelectionToolbarEnabled => AppSettings.Current.StickerSelectionToolbar;

    /// <summary>
    /// 每次选区可能变化后调用：Background 优先级排到本轮布局之后，避免读到尚未重排的
    /// <c>TextLayout</c>（与贴图浮条其余几何收口的时机一致）。
    /// </summary>
    private void ScheduleSelectionToolbarUpdate()
    {
        if (_isClosed || SelectionToolbar == null) return;
        Dispatcher.UIThread.Post(UpdateSelectionToolbarPosition, DispatcherPriority.Background);
    }

    /// <summary>重排／换版宽后重画装饰层（矩形不存，全部按新的排版现算）。</summary>
    private void ScheduleMarkRedraw()
    {
        if (_isClosed) return;
        Dispatcher.UIThread.Post(RedrawTextDecorations, DispatcherPriority.Background);
    }

    private void UpdateSelectionToolbarPosition()
    {
        if (_isClosed || SelectionToolbar == null || RootGrid == null) return;

        // 🔴 点「翻译」那一次松手排下的陈旧回调走到这里：它晚于 Click 执行，按「选区还在」
        // 会把刚收起的浮条重新摆回来，而此刻屏幕上该出现的正是译文气泡。吞掉这一次即返回。
        // 放在模式判据之前——标注/缩略图/穿透/锁定态下浮条本就收起，气泡由 EnterAnnotationMode
        // 等入口各自负责，这里只关心浮条，不去动气泡（气泡正是这次落笔要展开的东西）。
        if (_suppressNextToolbarUpdate)
        {
            _suppressNextToolbarUpdate = false;
            HideSelectionToolbar();
            return;
        }

        // 标注模式/缩略图/穿透/锁定态下文字层本身就不参与划词；设置关掉时同样直接收起。
        if (!IsSelectionToolbarEnabled || _isAnnotating || _isThumbnailMode || _isMousePenetration || _isLocked)
        {
            // 🔴 只收气泡，**不**清翻译标记：气泡是跟着指针走的浮层，这些态下指针早已不在紫点上，
            // 留一张半透明浮层盖在原文上只会挡字；而紫线与紫点与三档装饰同层同纪律
            // （下面这一支同样没有清 _selMarks），几何由 RedrawTextDecorations 每次按当时排版现算，
            // 回到正常态自然重画到位。清除只有气泡红 ✕ 与关贴图两条路。
            if (_bubbleShown) HideTranslateBubble();
            HideSelectionToolbar();
            return;
        }

        var rects = new List<Rect>();
        var ranges = CollectLiveSelectionRanges();
        StickerWordSel? wordSel = null;
        if (ranges.Count > 0)
        {
            foreach (var range in ranges)
            {
                if (range.Block != null) rects.AddRange(GetRectsIn(range.Block, range.Start, range.Length, RootGrid));
            }
        }
        else
        {
            // 块选区为空＝这不是文本/Markdown 贴图（或那一层没选东西），再问一次字级覆盖层
            wordSel = CollectLiveWordSelection(RootGrid);
            if (wordSel != null) rects.AddRange(wordSel.Rects);
        }
        if (rects.Count == 0)
        {
            if (ranges.Count == 0 && wordSel == null) HideSelectionToolbar();
            // 选区在、几何还没算出来（排版尚未就绪）：保持现状，不闪一下收起
            return;
        }

        // 记下这一刻的选区身份：点下面的按钮后原生选区可能已被清掉，标注/AI/翻译都靠它兜底
        _selSnapshot.Clear();
        foreach (var range in ranges)
        {
            _selSnapshot.Add(new StickerSelRange
            {
                BlockIndex = range.BlockIndex,
                BlockText = range.BlockText,
                Start = range.Start,
                Length = range.Length
            });
        }
        _selWordSnapshot = wordSel?.Indices ?? new List<int>();
        _selWordTextSnapshot = wordSel?.Text ?? "";

        RefreshClearMarksMenuItem();

        double hostW = RootGrid.Bounds.Width;
        double hostH = RootGrid.Bounds.Height;
        // 先按可用宽度封顶再量：贴图常比整条浮条窄，WrapPanel 装不下就折行，
        // 不封顶的话量到的单行宽会把浮条推出窗口、右边几枚永远点不到。
        SelectionToolbar.MaxWidth = Math.Max(60, hostW - 8);
        // 控件常驻可视树（Opacity 控显隐）。量宽前必须先清零旧定位 Margin：
        // DesiredSize 含 Margin，否则旧坐标混进宽高，工具栏会逐次漂移（OCR 同一坑）
        SelectionToolbar.Margin = default;
        SelectionToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fw = SelectionToolbar.DesiredSize.Width;
        double fh = SelectionToolbar.DesiredSize.Height;
        if (fw <= 0 || fh <= 0) return;

        double cx = (rects.Min(r => r.Left) + rects.Max(r => r.Right)) / 2;
        double bottom = rects.Max(r => r.Bottom);
        double x = Math.Clamp(cx - fw / 2, 4, Math.Max(4, hostW - fw - 4));
        // 恒定浮在选区下方；贴底时只把它夹回窗内，不翻到上方（贴图浮条既有口径：只夹不翻边）
        double y = Math.Min(bottom + 8, Math.Max(4, hostH - fh - 4));
        SelectionToolbar.Margin = new Thickness(x, y, 0, 0);
        _selBarRect = new Rect(x, y, fw, fh);
        ShowSelectionToolbar();
    }

    private void ShowSelectionToolbar()
    {
        if (SelectionToolbar == null) return;
        _selectionToolbarShown = true;
        SelectionToolbar.Opacity = 1;
        SelectionToolbar.IsHitTestVisible = true;
    }

    private void HideSelectionToolbar()
    {
        if (!_selectionToolbarShown || SelectionToolbar == null) return;
        _selectionToolbarShown = false;
        SelectionToolbar.Opacity = 0;
        SelectionToolbar.IsHitTestVisible = false;
    }

    // ══════════════════ 选区采集 ══════════════════

    /// <summary>当前可见文字层里所有"有选区"的块（纯文本贴图只有一块，Markdown 逐块）。</summary>
    private List<StickerSelRange> CollectLiveSelectionRanges()
    {
        var list = new List<StickerSelRange>();
        if (StickerTextBlock != null && StickerTextBlock.IsVisible)
        {
            AddLiveRange(list, StickerTextBlock, 0);
            return list;
        }
        if (StickerMarkdownViewer != null && StickerMarkdownViewer.IsVisible)
        {
            int index = 0;
            foreach (var block in SelectableMarkdownTextBlock.EnumerateTextBlocks(StickerMarkdownViewer))
            {
                AddLiveRange(list, block, index);
                index++;
            }
        }
        return list;
    }

    private static void AddLiveRange(List<StickerSelRange> list, SelectableTextBlock block, int index)
    {
        int start = Math.Min(block.SelectionStart, block.SelectionEnd);
        int length = Math.Abs(block.SelectionEnd - block.SelectionStart);
        if (length <= 0) return;
        list.Add(new StickerSelRange
        {
            BlockIndex = index,
            BlockText = GetBlockPlainText(block),
            Start = start,
            Length = length,
            Block = block
        });
    }

    /// <summary>按块下标回查活着的块实例（快照重放用）。</summary>
    private SelectableTextBlock? GetBlockByIndex(int index)
    {
        if (StickerTextBlock != null && StickerTextBlock.IsVisible)
            return index == 0 ? StickerTextBlock : null;
        if (StickerMarkdownViewer == null || !StickerMarkdownViewer.IsVisible) return null;

        int i = 0;
        foreach (var block in SelectableMarkdownTextBlock.EnumerateTextBlocks(StickerMarkdownViewer))
        {
            if (i++ == index) return block;
        }
        return null;
    }

    /// <summary>
    /// 按钮动作用的选区：优先实时选区，选区已被清则回落到浮条出现那一刻的快照，
    /// 并按「块全文仍是同一段」校验——块内容变了（换文本、翻译覆盖）就当作无选区。
    /// </summary>
    private List<StickerSelRange> ResolveRangesForAction()
    {
        var live = CollectLiveSelectionRanges();
        if (live.Count > 0) return live;

        var restored = new List<StickerSelRange>();
        foreach (var snap in _selSnapshot)
        {
            var block = GetBlockByIndex(snap.BlockIndex);
            if (block == null) continue;
            if (GetBlockPlainText(block) != snap.BlockText) continue;
            restored.Add(new StickerSelRange
            {
                BlockIndex = snap.BlockIndex,
                BlockText = snap.BlockText,
                Start = snap.Start,
                Length = snap.Length,
                Block = block
            });
        }
        return restored;
    }

    /// <summary>块内可见全文：Markdown 块的文本挂在 Inlines 上（<c>Text</c> 为空），逐 Run 拼。</summary>
    private static string GetBlockPlainText(SelectableTextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text;
        var inlines = block.Inlines;
        if (inlines == null) return "";
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is Run run && !string.IsNullOrEmpty(run.Text)) sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>选区在目标坐标系里的矩形组（Viewbox 缩放、块内偏移都由 TranslatePoint 一并算掉）。</summary>
    private static List<Rect> GetRectsIn(SelectableTextBlock block, int start, int length, Visual target)
    {
        var result = new List<Rect>();
        if (target == null || block.TextLayout == null) return result;

        string full = GetBlockPlainText(block);
        int safeStart = Math.Clamp(start, 0, full.Length);
        int safeLength = Math.Clamp(length, 0, full.Length - safeStart);
        if (safeLength <= 0) return result;

        try
        {
            foreach (var rect in block.TextLayout.HitTestTextRange(safeStart, safeLength))
            {
                var topLeft = block.TranslatePoint(new Point(rect.X, rect.Y), target);
                var bottomRight = block.TranslatePoint(new Point(rect.X + rect.Width, rect.Y + rect.Height), target);
                if (topLeft == null || bottomRight == null) continue;
                if (bottomRight.Value.X <= topLeft.Value.X || bottomRight.Value.Y <= topLeft.Value.Y) continue;
                result.Add(new Rect(topLeft.Value.X, topLeft.Value.Y,
                                    bottomRight.Value.X - topLeft.Value.X,
                                    bottomRight.Value.Y - topLeft.Value.Y));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 划词选区几何计算失败: " + ex.Message);
        }
        return result;
    }

    // ══════════════════ 选区采集：图片贴图的字级覆盖层 ══════════════════

    /// <summary>
    /// 图片贴图 Alt 快速选词／持久「文本可选择」的当前选区；不是图片贴图或没选中字时返回 null。
    /// 拼文本的口径与 <see cref="CopySelectedText"/> 逐字相同（按 LineIndex 分组、行内按左缘排序、行间换行），
    /// 否则浮条上"复制"与翻译／AI 那三项取到的文本会不一样。
    /// </summary>
    private StickerWordSel? CollectLiveWordSelection(Visual target)
    {
        var ordered = OrderWordIndices(_selectedWordIndices);
        if (ordered.Count == 0) return null;

        var rects = BuildWordRunRects(ordered, target);
        if (rects.Count == 0) return null;

        return new StickerWordSel
        {
            Indices = ordered,
            Text = JoinWordText(ordered),
            Rects = rects
        };
    }

    /// <summary>
    /// 把被选中的字下标排成「行号升序 → 行内左缘升序」。
    /// 越界下标自动丢弃：OCR 缓存被重建（旋转、裁切、恢复原图）后同一序号已是另一个字，
    /// 留着只会把笔迹画到别处字上——文本对账再把整条装饰跳过，两道闸缺一不可。
    /// </summary>
    private List<int> OrderWordIndices(IEnumerable<int> selected)
    {
        var regions = _wordRegions;
        if (regions == null || regions.Count == 0 || selected == null) return new List<int>();

        return selected
            .Where(i => i >= 0 && i < regions.Count)
            .Distinct()
            .GroupBy(i => regions[i].LineIndex)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(i => regions[i].Bounds.Left))
            .ToList();
    }

    /// <summary>按已排好序的下标拼出选中文本（行间换行，与 CopySelectedText 同一口径）。</summary>
    private string JoinWordText(List<int> orderedIndices)
    {
        var regions = _wordRegions;
        if (regions == null) return "";

        var sb = new StringBuilder();
        int lastLine = int.MinValue;
        foreach (int i in orderedIndices)
        {
            if (i < 0 || i >= regions.Count) continue;
            if (lastLine != int.MinValue && regions[i].LineIndex != lastLine) sb.AppendLine();
            sb.Append(regions[i].Text);
            lastLine = regions[i].LineIndex;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 字级选区的矩形组（目标坐标系）：先把选中的字并成连续段，再乘当前缩放落进 Canvas，
    /// 最后 TranslatePoint 换算。合并判据与 <see cref="ApplyWordSelection"/> 画那条蓝底逐字相同
    /// （OCR 像素间距 ≤ 2）——两处判据不同，浮条就会对不上用户眼前看到的那几段。
    /// </summary>
    private List<Rect> BuildWordRunRects(List<int> orderedIndices, Visual target)
    {
        var result = new List<Rect>();
        if (StickerCanvas == null || target == null) return result;

        double k = WordDisplayScale / SafeScaling;
        foreach (var run in MergeWordRunsInOcrPixels(orderedIndices))
        {
            AddRunRect(result, target, k,
                (float)run.Left, (float)run.Top, (float)run.Right, (float)run.Bottom);
        }
        return result;
    }

    /// <summary>
    /// 把已排序的字下标并成连续段，返回 <b>OCR 像素</b> 坐标的外接框。
    /// 存这个单位而不是屏幕矩形：缩放只改 <c>_scale</c>，重画时乘回去即可，不必在每一处尺寸变化里
    /// 追改缓存；旋转/翻转由 Canvas 自己的 <c>_canvasTransform</c> 带着笔迹一起转，那一笔永远贴着落笔时的像素。
    /// </summary>
    private List<Rect> MergeWordRunsInOcrPixels(List<int> orderedIndices)
    {
        var result = new List<Rect>();
        var regions = _wordRegions;
        if (regions == null) return result;

        bool open = false;
        int curLine = int.MinValue;
        float left = 0, top = 0, right = 0, bottom = 0;

        foreach (int i in orderedIndices)
        {
            if (i < 0 || i >= regions.Count) continue;
            var word = regions[i];
            var b = word.Bounds;

            // 换行，或与已累积段的右缘断开超过 2 OCR 像素 ⇒ 另起一段
            if (!open || word.LineIndex != curLine || b.Left - right > 2f)
            {
                if (open) result.Add(new Rect(left, top, right - left, bottom - top));
                curLine = word.LineIndex;
                left = b.Left; top = b.Top; right = b.Right; bottom = b.Bottom;
                open = true;
            }
            else
            {
                if (b.Left < left) left = b.Left;
                if (b.Top < top) top = b.Top;
                if (b.Right > right) right = b.Right;
                if (b.Bottom > bottom) bottom = b.Bottom;
            }
        }
        if (open) result.Add(new Rect(left, top, right - left, bottom - top));
        return result;
    }

    /// <summary>一段连续字的外接框：Canvas DIP → 目标坐标系（TranslatePoint 把缩放与画布偏移一并算掉）。</summary>
    private void AddRunRect(List<Rect> result, Visual target, double k,
                            float left, float top, float right, float bottom)
    {
        if (StickerCanvas == null) return;
        var a = StickerCanvas.TranslatePoint(new Point(left * k, top * k), target);
        var c = StickerCanvas.TranslatePoint(new Point(right * k, bottom * k), target);
        if (a == null || c == null) return;
        if (c.Value.X <= a.Value.X || c.Value.Y <= a.Value.Y) return;
        result.Add(new Rect(a.Value.X, a.Value.Y, c.Value.X - a.Value.X, c.Value.Y - a.Value.Y));
    }

    /// <summary>动作取值用的字级下标：优先实时覆盖层，已被收尾清掉则回落到浮条那一刻的快照。</summary>
    private List<int> ResolveWordIndicesForAction()
    {
        var live = OrderWordIndices(_selectedWordIndices);
        if (live.Count > 0) return live;
        return OrderWordIndices(_selWordSnapshot);
    }

    // ══════════════════ 按钮动作 ══════════════════

    private void OnStTtCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 与 Ctrl+C 同一条通路（内部已含快照兜底与"已复制 N 个字符"提示）
        CopySelectedText();
    }

    /// <summary>把当前选区文字取出来（三档装饰以外的四项都靠它）。</summary>
    private string GetSelectedTextForAction()
    {
        // 字级选区（图片贴图）优先：实时覆盖层还在就用它，已被收尾清掉就用浮条那一刻的字级快照。
        // 两套宿主的选区互斥（图片贴图没有 SelectableTextBlock，反之没有 _selectedWordIndices），
        // 先后顺序不影响结果，写成"先字级后块级"只是为了与采集侧同一读法。
        var liveWords = OrderWordIndices(_selectedWordIndices);
        if (liveWords.Count > 0)
        {
            string liveText = JoinWordText(liveWords);
            if (!string.IsNullOrEmpty(liveText)) return liveText;
        }
        if (_selWordSnapshot.Count > 0)
        {
            var snapWords = OrderWordIndices(_selWordSnapshot);
            string snapText = snapWords.Count > 0 ? JoinWordText(snapWords) : _selWordTextSnapshot;
            if (!string.IsNullOrEmpty(snapText)) return snapText;
        }

        var ranges = ResolveRangesForAction();
        if (ranges.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var range in ranges)
            {
                if (range.Block == null) continue;
                string full = GetBlockPlainText(range.Block);
                int start = Math.Clamp(range.Start, 0, full.Length);
                int length = Math.Clamp(range.Length, 0, full.Length - start);
                if (length <= 0) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(full.Substring(start, length));
            }
            if (sb.Length > 0) return sb.ToString();
        }
        return _textSelSnapshot ?? "";
    }

    // ══════════════════ 划词翻译：持久标记（紫线＋紫点＋悬浮气泡＋红 ✕ 清除） ══════════════════

    /// <summary>
    /// 点浮条「翻译」：把当前选区落成一条<b>持久</b>标记——原文每段下方一道品牌紫实线、右上角一枚紫点，
    /// 浮条收起，译文<b>此刻就发请求</b>预取（悬浮紫点时往往已就绪，气泡立即出结果）。
    /// 同一段上再点一次＝重译：旧那条整条撤掉（含在飞的请求），不会叠出两枚紫点、两份互相打架的译文。
    /// </summary>
    private void OnStTtTranslateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var config = AppSettings.Load().ToTranslationConfig();
        if (!config.IsValid)
        {
            StickerToastWindow.ShowAtTop("请先在设置中配置翻译服务 API 密钥");
            return;
        }

        var mark = BuildTransMarkFromSelection();
        if (mark == null)
        {
            StickerToastWindow.ShowAtTop("选区已失效，请重新划选文字");
            return;
        }

        RemoveTranslateMark(FindSameTransMark(mark));   // 重译：同身份的旧标记先撤（没有就是 null，直接返回）

        _transMarks.Add(mark);
        // 点「翻译」＝这段选区已被消费成一条持久标记，浮条当即收起。但**这一次收起会被顶回来**：
        // 触发 Click 的那次松手先走窗级 PointerReleased（Tunnel），其末尾排下的重定位回调以
        // Background 优先级晚于 Click 执行，按「选区还在」又把浮条摆出（详见字段注释）。
        // 挂一次性抑制，让那个陈旧回调只收不收。
        _suppressNextToolbarUpdate = true;
        HideSelectionToolbar();
        RedrawTextDecorations();
        RetainWordMarkLayer();
        AppLogger.Info("[划词翻译] 落一条持久标记（" + mark.SourceText.Length + " 字），累计 " + _transMarks.Count + " 条");

        // 🔴 落笔即出气泡——不必等用户再去悬浮紫点。上面那次重画是**同步**的，紫点已按当时排版
        // 摆好、mark.DotAnchor 已填实，故这里直接展开就能定位准确。判 IsVisible 而非无条件展开：
        // 块级选区落在可视区外或字级下标全部失联时紫点不摆，此时展开就是一张悬空的浮层。
        // 请求此刻才发出，故气泡先呈进行态（呼吸点），结果回来由 RunSelectionTranslationAsync
        // 就地翻面；后续任何重画若挪动紫点，PlaceTransDot 会带着这张开着的气泡一起重定位。
        if (mark.Dot?.IsVisible == true) ShowTranslateBubble(mark);

        _ = RunSelectionTranslationAsync(mark, config);
    }

    /// <summary>
    /// 按当前选区攒一条标记的<b>身份</b>与原文。两套宿主互斥，分流口径与三档装饰逐字相同
    /// （<see cref="ApplySelectionMark"/>）：图片贴图走字级下标，文本／Markdown 走块级区间。
    /// 身份只存"可重放的最小事实"（块下标＋块内全文＋字符区间／字下标＋拼出文本＋OCR 像素框），
    /// 不留控件引用与屏幕矩形——块会随重排重建，矩形会随缩放作废。返回 null＝选区已失效。
    /// </summary>
    private StickerTransMark? BuildTransMarkFromSelection()
    {
        var words = ResolveWordIndicesForAction();
        if (words.Count > 0)
        {
            string wordText = JoinWordText(words);
            if (string.IsNullOrEmpty(wordText)) return null;
            var rects = MergeWordRunsInOcrPixels(words);
            if (rects.Count == 0) return null;
            return new StickerTransMark
            {
                WordIndices = words,
                WordText = wordText,
                WordRects = rects,
                // 原文走与「复制」同一条取值通路，保证"译的就是能复制出来的那一段"
                SourceText = GetSelectedTextForAction()
            };
        }

        var ranges = ResolveRangesForAction();
        if (ranges.Count == 0) return null;

        var mark = new StickerTransMark { SourceText = GetSelectedTextForAction() };
        foreach (var range in ranges)
        {
            if (range.Block == null) continue;
            string full = GetBlockPlainText(range.Block);
            int start = Math.Clamp(range.Start, 0, full.Length);
            int length = Math.Clamp(range.Length, 0, full.Length - start);
            if (length <= 0) continue;
            mark.Blocks.Add(new StickerSelRange
            {
                BlockIndex = range.BlockIndex,
                BlockText = full,     // 对账用：块内容变了（换文本／翻译覆盖）这条标记就该整条跳过
                Start = start,
                Length = length
            });
        }
        if (mark.Blocks.Count == 0 || string.IsNullOrWhiteSpace(mark.SourceText)) return null;
        return mark;
    }

    /// <summary>找身份相同的既有标记：块侧逐段比「块下标＋块全文＋区间」，图片侧比「字下标序列＋拼出文本」。</summary>
    private StickerTransMark? FindSameTransMark(StickerTransMark candidate)
    {
        foreach (var m in _transMarks)
        {
            if (m.WordIndices != null || candidate.WordIndices != null)
            {
                if (m.WordIndices == null || candidate.WordIndices == null) continue;
                if (m.WordText == candidate.WordText && m.WordIndices.SequenceEqual(candidate.WordIndices)) return m;
                continue;
            }
            if (m.Blocks.Count != candidate.Blocks.Count) continue;
            bool same = true;
            for (int i = 0; i < m.Blocks.Count; i++)
            {
                var a = m.Blocks[i];
                var b = candidate.Blocks[i];
                if (a.BlockIndex != b.BlockIndex || a.Start != b.Start
                    || a.Length != b.Length || a.BlockText != b.BlockText) { same = false; break; }
            }
            if (same) return m;
        }
        return null;
    }

    /// <summary>
    /// 清除一条标记（红 ✕、重译、换图、关窗都走这里）：掐掉在飞的请求、摘掉紫点、重画掉紫线。
    /// 传 null 直接返回——"没有同身份的旧标记"是重译路径的常态，不该让每个调用方先判一遍。
    /// </summary>
    private void RemoveTranslateMark(StickerTransMark? mark)
    {
        if (mark == null) return;

        mark.Cts?.Cancel();
        mark.Cts?.Dispose();
        mark.Cts = null;
        if (mark.Dot != null)
        {
            TranslateDotLayer?.Children.Remove(mark.Dot);
            mark.Dot = null;
        }
        _transMarks.Remove(mark);

        // 气泡正指着这一条：先收气泡再重画，别让它悬在一段已经没有紫线的原文上
        if (_activeTransMark == mark) HideTranslateBubble();

        RedrawTextDecorations();
        RetainWordMarkLayer();
    }

    /// <summary>贴图关闭：掐掉所有在飞的请求、停掉收起延时（标记数据随窗口一起没，图元不必逐个摘）。
    /// 由 <c>OnClosed</c> 调。</summary>
    private void DisposeTranslateMarks()
    {
        CancelBubbleHide();
        foreach (var mark in _transMarks)
        {
            mark.Cts?.Cancel();
            mark.Cts?.Dispose();
            mark.Cts = null;
        }
        _transMarks.Clear();
        _imageTransShapes.Clear();
        _activeTransMark = null;
        _bubbleShown = false;
    }

    // ── 绘制：紫线进装饰层（跟缩放、进对外快照），紫点进 RootGrid 层（要吃命中才悬浮得到） ──

    /// <summary>
    /// 重画所有翻译标记。<b>必须在 <see cref="RedrawBlockMarks"/> 之后调</b>——那一步会整层清空
    /// TextDecorLayer，紫线画早了就被它抹掉。
    /// 身份对不上的那条（块内容变了／图换了／几何还没算出来）整条跳过：与三档装饰同一条对账纪律，
    /// 位置画错比标记消失更难解释。
    /// </summary>
    private void RedrawTranslateMarks()
    {
        if (_isClosed) return;

        // 图片贴图那层的紫线按账本回收（那一层还住着字级覆盖层与三档装饰，不能整层清）
        if (StickerCanvas != null)
        {
            foreach (var shape in _imageTransShapes) StickerCanvas.Children.Remove(shape);
        }
        _imageTransShapes.Clear();

        double k = WordDisplayScale / SafeScaling;
        foreach (var mark in _transMarks)
        {
            var rootRects = new List<Rect>();

            if (mark.WordIndices != null)
            {
                // ── 图片贴图：字级。紫线按 OCR 像素 × 缩放落进 StickerCanvas ──
                var rects = mark.WordRects;
                if (rects == null || !IsWordTransMarkStillValid(mark)) { HideTransDot(mark); continue; }
                if (StickerCanvas != null)
                {
                    foreach (var r in rects)
                    {
                        var line = DrawTransUnderline(
                            new Rect(r.X * k, r.Y * k, r.Width * k, r.Height * k), StickerCanvas);
                        if (line != null) _imageTransShapes.Add(line);
                    }
                }
                // 紫点住在 RootGrid 层，几何另取一份该坐标系的
                if (RootGrid != null) rootRects.AddRange(BuildWordRunRects(OrderWordIndices(mark.WordIndices), RootGrid));
            }
            else
            {
                // ── 文本／Markdown：块级。紫线画在 Viewbox 内部的 TextDecorLayer（未缩放的内容坐标，
                //    跟着 Viewbox 一起缩放，并被 SnapshotTextSticker 一并收进对外输出的位图）──
                foreach (var id in mark.Blocks)
                {
                    var block = GetBlockByIndex(id.BlockIndex);
                    if (block == null) continue;
                    if (GetBlockPlainText(block) != id.BlockText) continue;
                    if (TextDecorLayer != null)
                    {
                        foreach (var r in GetRectsIn(block, id.Start, id.Length, TextDecorLayer))
                            DrawTransUnderline(r, TextDecorLayer);
                    }
                    if (RootGrid != null) rootRects.AddRange(GetRectsIn(block, id.Start, id.Length, RootGrid));
                }
            }

            PlaceTransDot(mark, rootRects);
        }
    }

    /// <summary>字级翻译标记的失效对账：与 <see cref="IsWordMarkStillValid"/> 同一判据，只是读翻译标记自己的字段。</summary>
    private bool IsWordTransMarkStillValid(StickerTransMark mark)
    {
        if (_wordRegions == null) return true;      // 只是退出选词态，图上像素没变，紫线照旧留着
        if (mark.WordIndices == null) return false;
        var ordered = OrderWordIndices(mark.WordIndices);
        return ordered.Count > 0 && JoinWordText(ordered) == mark.WordText;
    }

    /// <summary>画一道紫色下划直线：贴着这段文字的底边，不吃命中（不挡下一轮划词、按住拖动与滚轮缩放）。</summary>
    private static Line? DrawTransUnderline(Rect rect, Canvas layer)
    {
        if (layer == null || rect.Width <= 0) return null;
        var line = new Line
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(rect.Width, 0),
            IsHitTestVisible = false
        };
        line.Classes.Add("stTransLine");
        Canvas.SetLeft(line, rect.X);
        Canvas.SetTop(line, rect.Bottom - 1);   // 压住底边 1 DIP：与虚线档同一贴法，读作"这段的基线"
        layer.Children.Add(line);
        return line;
    }

    /// <summary>
    /// 把紫点摆到原文<b>右上角</b>：首行（Top 最小 ±0.5）里最靠右的那段，点落在它右端外沿、与该行上沿齐平——
    /// 读作"这段文字有个注脚"，且不去盖住任何一个字。
    /// 紫点是<b>常驻图元</b>：同一条标记重画时复用同一个控件，否则每次重排都会把悬浮中的气泡甩掉。
    /// </summary>
    private void PlaceTransDot(StickerTransMark mark, List<Rect> rootRects)
    {
        if (TranslateDotLayer == null || RootGrid == null || rootRects.Count == 0) { HideTransDot(mark); return; }

        mark.Dot ??= CreateTransDot(mark);
        if (mark.Dot == null) return;

        double top = rootRects.Min(r => r.Top);
        var anchor = rootRects.Where(r => r.Top <= top + 0.5).OrderByDescending(r => r.Right).First();

        // 可见圆点在 16×16 命中盒正中，故按"想让圆心落在哪儿"反推盒的左上角
        const double outX = 5;    // 圆心超出原文右缘 5 DIP
        const double outY = 1;    // 圆心比原文上沿低 1 DIP（贴着首行，读作右上角）
        double x = anchor.Right + outX - TransDotBox / 2;
        double y = anchor.Top + outY - TransDotBox / 2;

        double hostW = RootGrid.Bounds.Width;
        double hostH = RootGrid.Bounds.Height;
        if (hostW > 0) x = Math.Clamp(x, 2, Math.Max(2, hostW - TransDotBox - 2));
        if (hostH > 0) y = Math.Clamp(y, 2, Math.Max(2, hostH - TransDotBox - 2));

        mark.Dot.Margin = new Thickness(x, y, 0, 0);
        mark.Dot.IsVisible = true;
        mark.DotAnchor = new Point(x, y);

        // 气泡正开在这一条上：紫点挪了，气泡、小方角与红 ✕ 都得跟着挪。
        // 🔴 走 PositionTranslateBubble 而**不是** ShowTranslateBubble：这里只是重摆，不是重新展开。
        // 后者会顺手 CancelBubbleHide()，而重画常由滚轮/缩放触发——那时指针多半已离开紫点、
        // 180ms 收起计时正在跑，一取消气泡就再也收不起来了（得等失活或点别处才收）。
        if (_bubbleShown && _activeTransMark == mark) PositionTranslateBubble(mark);
    }

    /// <summary>造一枚紫点并挂上悬浮事件。<see cref="Control.Tag"/> 带上标记本身，事件回来时不必反查"这是哪一条"。</summary>
    private Border? CreateTransDot(StickerTransMark mark)
    {
        if (TranslateDotLayer == null) return null;
        var dot = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Tag = mark
        };
        dot.Classes.Add("transDotHit");
        var core = new Ellipse();
        core.Classes.Add("transDot");
        dot.Child = core;
        dot.PointerEntered += OnTransDotPointerEntered;
        dot.PointerExited += OnTransDotPointerExited;
        TranslateDotLayer.Children.Add(dot);
        return dot;
    }

    /// <summary>身份对不上时把紫点收起来：标记数据保留，等对得上的那一轮重画再显——与紫线"跳过这一段"
    /// 同一条口径，两件事必须同步，否则会出现"有点无线"。</summary>
    private void HideTransDot(StickerTransMark mark)
    {
        if (mark.Dot != null) mark.Dot.IsVisible = false;
    }

    /// <summary>有没有图片贴图那一路的翻译标记（<see cref="RetainWordMarkLayer"/> 靠它决定 StickerCanvas 收不收）。</summary>
    private bool HasWordTransMarks()
    {
        for (int i = 0; i < _transMarks.Count; i++)
        {
            if (_transMarks[i].WordIndices != null) return true;
        }
        return false;
    }

    // ── 悬浮展开 / 延时收起 ──

    private void OnTransDotPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        CancelBubbleHide();
        if (sender is Border dot && dot.Tag is StickerTransMark mark) ShowTranslateBubble(mark);
    }

    private void OnTransDotPointerExited(object? sender, Avalonia.Input.PointerEventArgs e) => ScheduleBubbleHide();

    /// <summary>气泡与红 ✕ 共用这一对：指针进了其中任一件就取消收起计时，离开才重新起表。
    /// 🔴 红 ✕ 有一半在气泡外，不挂这对事件的话指针移过去会先触发气泡的 Exited 把整组收掉。</summary>
    private void OnTranslateBubblePointerEntered(object? sender, Avalonia.Input.PointerEventArgs e) => CancelBubbleHide();

    private void OnTranslateBubblePointerExited(object? sender, Avalonia.Input.PointerEventArgs e) => ScheduleBubbleHide();

    private void ScheduleBubbleHide()
    {
        if (_isClosed) return;
        if (_bubbleHideTimer == null)
        {
            _bubbleHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _bubbleHideTimer.Tick += (_, _) => { _bubbleHideTimer?.Stop(); HideTranslateBubble(); };
        }
        _bubbleHideTimer.Stop();
        _bubbleHideTimer.Start();
    }

    private void CancelBubbleHide() => _bubbleHideTimer?.Stop();

    /// <summary>
    /// 展开译文气泡（<paramref name="mark"/> 即这条气泡服务的标记）：横向以紫点为中心、整体钳回窗内；
    /// 纵向贴紫点下方 6 DIP，放不下翻到紫点上方——顶缘小方角随之换边、继续指向紫点。
    /// 红 ✕ 骑在气泡左上角上（各压一半），与气泡同批定位。
    /// 皮与浮条同一对（外环 ⊃ 内板），先按可用宽度封顶再量（Measure 前清 Margin，同工具栏那条）。
    /// </summary>
    private void ShowTranslateBubble(StickerTransMark mark)
    {
        if (_isClosed || TranslateBubble == null || RootGrid == null || TranslateBubbleCaret == null) return;

        _activeTransMark = mark;
        _bubbleShown = true;
        CancelBubbleHide();
        UpdateTranslateBubbleContent();
        PositionTranslateBubble(mark);
    }

    /// <summary>
    /// 只按紫点现算气泡（含小方角与红 ✕）的落位，**不碰**显隐态与收起计时器。
    ///
    /// 🔴 与 <see cref="ShowTranslateBubble"/> 拆开是必要的：译文回填时气泡可能正处于
    /// 「指针已离开、180ms 收起计时中」，那时若走 ShowTranslateBubble 会顺手 CancelBubbleHide()
    /// 把计时器取消掉，气泡就再也收不起来了。回填只需要"重摆"，不需要"重新展开"。
    ///
    /// 🔴 完成态**必须**重摆：进行态只有一行呼吸点，气泡很小；译文一到尺寸大变，而气泡是
    /// Left/Top 对齐＋Margin 定位（StickerWindow.axaml:509-510），只会朝右下长——不重算就会
    /// 越过窗口下缘／右缘；更要紧的是"下方放不下就翻到紫点上方"那次判定，是按进行态的小高度做的。
    /// 「落笔即出气泡」之后，进行态→完成态成了每次翻译的必经路径，故这条不是边角而是主干。
    /// </summary>
    private void PositionTranslateBubble(StickerTransMark mark)
    {
        if (_isClosed || TranslateBubble == null || RootGrid == null || TranslateBubbleCaret == null) return;

        double hostW = RootGrid.Bounds.Width;
        double hostH = RootGrid.Bounds.Height;
        TranslateBubble.MaxWidth = Math.Max(120, hostW - 8);
        TranslateBubble.Margin = default;
        TranslateBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = TranslateBubble.DesiredSize.Width;
        double bh = TranslateBubble.DesiredSize.Height;
        if (bw <= 0 || bh <= 0) return;

        double dotCx = mark.DotAnchor.X + TransDotBox / 2;
        double bx = Math.Clamp(dotCx - bw / 2, 4, Math.Max(4, hostW - bw - 4));
        const double gap = 6;
        double dotBottom = mark.DotAnchor.Y + TransDotBox;
        double by = dotBottom + gap;
        _bubbleAbove = false;
        if (by + bh > hostH - 4)
        {
            by = mark.DotAnchor.Y - gap - bh;
            _bubbleAbove = true;
            if (by < 4)
            {
                by = Math.Min(dotBottom + gap, Math.Max(4, hostH - bh - 4));
                _bubbleAbove = false;
            }
        }
        TranslateBubble.Margin = new Thickness(bx, by, 0, 0);
        TranslateBubble.Opacity = 1;
        TranslateBubble.IsHitTestVisible = true;

        // 小方角骑在气泡贴紫点那一侧的边缘上：横向对准紫点中心，钳在气泡宽度内。
        // 🔴 下限取 10 而不是 0：红 ✕ 占着左上角 bx−8…bx+8，方角至少让到 bx+10 才不与它叠在一起。
        double caretX = Math.Clamp(dotCx - 4.5 - bx, 10, Math.Max(10, bw - 19));
        TranslateBubbleCaret.Margin = new Thickness(bx + caretX, _bubbleAbove ? by + bh - 4.5 : by - 4.5, 0, 0);
        TranslateBubbleCaret.Opacity = 1;

        if (TranslateBubbleClose != null)
        {
            TranslateBubbleClose.Margin = new Thickness(bx - TransCloseSize / 2, by - TransCloseSize / 2, 0, 0);
            TranslateBubbleClose.Opacity = 1;
            TranslateBubbleClose.IsHitTestVisible = true;
        }
    }

    private void HideTranslateBubble()
    {
        CancelBubbleHide();
        _bubbleShown = false;
        _activeTransMark = null;
        if (TranslateBubble != null)
        {
            TranslateBubble.Opacity = 0;
            TranslateBubble.IsHitTestVisible = false;
        }
        if (TranslateBubbleCaret != null) TranslateBubbleCaret.Opacity = 0;
        if (TranslateBubbleClose != null)
        {
            TranslateBubbleClose.Opacity = 0;
            TranslateBubbleClose.IsHitTestVisible = false;
        }
    }

    /// <summary>气泡左上角红 ✕：清除<b>这一条</b>翻译标记（紫线、紫点、气泡与在飞的请求一并收），其余各条照旧留着。</summary>
    private void OnTranslateBubbleCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        RemoveTranslateMark(_activeTransMark);      // 内部会收气泡（这一条正是气泡指着的那条）
        AppLogger.Info("[划词翻译] 红 ✕ 清除一条，剩余 " + _transMarks.Count + " 条");
    }

    /// <summary>按气泡指着那条标记的译文进行态渲染：请求中＝呼吸点行；完成＝译文＋角标行，
    /// 失败整句染危险色（颜色由窗口私有样式类承担，元素上不写死）。
    /// 🔴 一律读 <see cref="_activeTransMark"/> 而不是窗口字段——多段并存时窗口字段会串条。</summary>
    private void UpdateTranslateBubbleContent()
    {
        if (TranslateLoadingRow == null || TranslateBubbleText == null || TranslateMetaRow == null) return;
        var mark = _activeTransMark;
        if (mark == null) return;

        bool busy = !mark.Done;
        TranslateLoadingRow.IsVisible = busy;
        TranslateBubbleText.IsVisible = !busy;
        TranslateMetaRow.IsVisible = !busy && !mark.IsError;
        if (busy) return;

        TranslateBubbleText.Text = mark.TransText;
        if (mark.IsError) TranslateBubbleText.Classes.Add("translationBarError");
        else TranslateBubbleText.Classes.Remove("translationBarError");
        // 🔴 角标行在这里写，不在请求回调里写。气泡与角标行全场只有一套，而请求回来时气泡可能
        // 已经收掉了（失活／点了别处）——那时仍往共享控件里写，就会把这一条的语言对留给下一次
        // 展开的**另一条**标记（串条）。跟着"当前渲染的是哪一条"走才不会串。
        WriteTranslateMeta(mark);
    }

    /// <summary>气泡角标行文案：语言对（常用语种取短名，其余回落原码）＋服务商名。</summary>
    private void WriteTranslateMeta(StickerTransMark mark)
    {
        if (TranslateMetaText == null) return;
        TranslateMetaText.Text =
            $"{LangName(mark.Langs.Src)} → {LangName(mark.Langs.Tgt)} · {ProviderName(mark.Provider)}";
    }

    private static string LangName(string code) => code switch
    {
        TranslationLanguage.Auto => "自动",
        TranslationLanguage.ChineseSimplified => "中",
        TranslationLanguage.ChineseTraditional => "繁",
        TranslationLanguage.English => "英",
        TranslationLanguage.Japanese => "日",
        TranslationLanguage.Korean => "韩",
        TranslationLanguage.French => "法",
        TranslationLanguage.German => "德",
        TranslationLanguage.Russian => "俄",
        TranslationLanguage.Spanish => "西",
        _ => code
    };

    private static string ProviderName(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Baidu => "百度翻译",
        TranslationProvider.Youdao => "有道智云",
        TranslationProvider.BilibiliIndex => "bilibili-index",
        _ => "翻译"
    };

    /// <summary>
    /// 划词翻译请求（预取）：语言对与 <c>TranslateMiniWindow</c> 同一套算法（<see cref="LanguageDetector"/> 现判，
    /// 源＝目标时自动退到次级目标语言）。结果落在<b>这条标记自己的</b>字段上，气泡指着它时才渲染；
    /// 重译或清除会把在飞的那一次掐掉（每条一个取消令牌，故多段并存互不干扰）。
    /// 窗口已关闭或本条已被清除时结果直接丢弃，不回写任何控件。
    /// </summary>
    private async Task RunSelectionTranslationAsync(StickerTransMark mark, TranslationConfig config)
    {
        mark.Cts?.Cancel();
        mark.Cts?.Dispose();
        mark.Cts = new CancellationTokenSource();
        var ct = mark.Cts.Token;

        mark.Done = false;
        mark.IsError = false;
        mark.TransText = "";
        mark.Provider = config.Provider;
        if (_activeTransMark == mark) UpdateTranslateBubbleContent();   // 气泡正开着就立刻翻回加载态
        try
        {
            var (src, tgt, _, _) = LanguageDetector.ResolveLanguages(
                mark.SourceText, config.SourceLanguage, config.TargetLanguage, config.SecondaryTargetLanguage);
            var result = await new TranslationService(config)
                .TranslateTextAsync(mark.SourceText, src, tgt, ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isClosed || ct.IsCancellationRequested) return;
                if (result.Success)
                {
                    UsageStatistics.RecordTranslate(); // 统计：翻译成功一次
                    mark.TransText = string.IsNullOrWhiteSpace(result.TranslatedText)
                        ? "没有返回译文" : result.TranslatedText!;
                }
                else
                {
                    mark.IsError = true;
                    mark.TransText = "翻译失败：" + (result.ErrorMessage ?? "未知错误");
                }
                mark.Langs = (src, tgt);
                mark.Done = true;
                // 气泡若正开着且指着这一条：刷完内容必须**重摆**。进行态只有一行呼吸点、气泡很小，
                // 译文一到尺寸大变，而气泡是 Left/Top＋Margin 定位、只会朝右下长（详见
                // PositionTranslateBubble 注释）。角标行由 UpdateTranslateBubbleContent 内部写。
                if (_bubbleShown && _activeTransMark == mark)
                {
                    UpdateTranslateBubbleContent();
                    PositionTranslateBubble(mark);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("贴图划词翻译异常", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isClosed || ct.IsCancellationRequested) return;
                mark.IsError = true;
                mark.TransText = "翻译失败：" + ex.Message;
                mark.Done = true;
                // 与成功路径同口径：失败文案通常也远高于一行呼吸点，气泡开着就得重摆。
                if (_bubbleShown && _activeTransMark == mark)
                {
                    UpdateTranslateBubbleContent();
                    PositionTranslateBubble(mark);
                }
            });
        }
    }

    /// <summary>复制译文到剪贴板：角标行「复制」轻点即走，成功在角标行原地反馈 1.2 秒。</summary>
    private async void OnTranslateCopyTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var mark = _activeTransMark;
        if (mark == null || string.IsNullOrEmpty(mark.TransText) || mark.IsError) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(mark.TransText);
            if (TranslateMetaText == null) return;
            var original = TranslateMetaText.Text;
            TranslateMetaText.Text = "已复制";
            await Task.Delay(1200);
            if (TranslateMetaText != null && TranslateMetaText.Text == "已复制") TranslateMetaText.Text = original;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("划词译文复制失败: " + ex.Message);
        }
    }

    // ══════════════════ 装饰层：荧光 / 虚线 / 波浪线 ══════════════════

    private void OnStTtMarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 三档装饰是浮条上平铺的三枚按钮，档位写在按钮 Tag 上（XAML 里逐字给出）
        if (sender is Button btn && btn.Tag is string kind && !string.IsNullOrEmpty(kind))
        {
            ApplySelectionMark(kind);
        }
    }

    private void OnStTtClearMarksClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selMarks.Count == 0) return;
        _selMarks.Clear();
        RedrawTextDecorations();
        RetainWordMarkLayer();
        StickerToastWindow.ShowAtTop("已清除划词标注");
    }

    /// <summary>给当前选区加一条装饰；同一段上重复点同一种标注＝撤掉（可当场反悔，不必找清除项）。</summary>
    private void ApplySelectionMark(string kind)
    {
        // 两套宿主先分流：图片贴图没有 TextLayout，那几笔落在字级覆盖层的坐标上，走另一条通路
        var words = ResolveWordIndicesForAction();
        if (words.Count > 0)
        {
            ApplyWordMark(kind, words);
            return;
        }

        var ranges = ResolveRangesForAction();
        if (ranges.Count == 0)
        {
            StickerToastWindow.ShowAtTop("选区已失效，请重新划选文字");
            return;
        }

        int added = 0;
        foreach (var range in ranges)
        {
            if (range.Block == null) continue;
            string full = GetBlockPlainText(range.Block);
            int start = Math.Clamp(range.Start, 0, full.Length);
            int length = Math.Clamp(range.Length, 0, full.Length - start);
            if (length <= 0) continue;

            bool exists = _selMarks.Any(m => m.Kind == kind && m.BlockIndex == range.BlockIndex
                                             && m.Start == start && m.Length == length);
            if (exists)
            {
                _selMarks.RemoveAll(m => m.Kind == kind && m.BlockIndex == range.BlockIndex
                                         && m.Start == start && m.Length == length);
                continue;
            }

            _selMarks.Add(new StickerSelMark
            {
                Kind = kind,
                BlockIndex = range.BlockIndex,
                BlockText = full,
                Start = start,
                Length = length
            });
            added++;
        }

        AppLogger.Info("[划词标注] kind=" + kind + " 新增 " + added + " 段，累计 " + _selMarks.Count + " 条");
        RedrawTextDecorations();
        RefreshClearMarksMenuItem();
    }

    /// <summary>
    /// 图片贴图上的字级装饰：身份存「字下标＋落笔时拼出的文本」，几何存连续段的 OCR 像素外接框。
    /// 下标只在"图还是那张图"时才有意义（旋转、裁切、换图后同一序号已是另一个字），所以重画前
    /// 先拿文本对账，对不上就整条跳过——位置画错比笔迹消失更难解释。
    /// </summary>
    private void ApplyWordMark(string kind, List<int> orderedIndices)
    {
        string text = JoinWordText(orderedIndices);
        if (string.IsNullOrEmpty(text))
        {
            StickerToastWindow.ShowAtTop("选区已失效，请重新划选文字");
            return;
        }

        var match = _selMarks.FirstOrDefault(m => SameWordMark(m, kind, orderedIndices, text));
        if (match != null)
        {
            _selMarks.Remove(match);
            AppLogger.Info("[划词标注] kind=" + kind + " 图片贴图撤除一段，累计 " + _selMarks.Count + " 条");
            RedrawTextDecorations();
            RetainWordMarkLayer();
            RefreshClearMarksMenuItem();
            return;
        }

        var rects = MergeWordRunsInOcrPixels(orderedIndices);
        if (rects.Count == 0)
        {
            StickerToastWindow.ShowAtTop("选区已失效，请重新划选文字");
            return;
        }

        _selMarks.Add(new StickerSelMark
        {
            Kind = kind,
            WordIndices = orderedIndices,
            WordText = text,
            WordRects = rects
        });
        AppLogger.Info("[划词标注] kind=" + kind + " 图片贴图新增 " + rects.Count + " 段，累计 " + _selMarks.Count + " 条");
        RedrawTextDecorations();
        RetainWordMarkLayer();
        RefreshClearMarksMenuItem();
    }

    private static bool SameWordMark(StickerSelMark m, string kind, List<int> indices, string text)
        => m.WordIndices != null && m.Kind == kind && m.WordText == text && m.WordIndices.SequenceEqual(indices);

    private bool HasWordMarks()
    {
        for (int i = 0; i < _selMarks.Count; i++)
        {
            if (_selMarks[i].WordIndices != null) return true;
        }
        return false;
    }

    private void RefreshClearMarksMenuItem()
    {
        if (MenuClearSelectionMarks != null) MenuClearSelectionMarks.IsVisible = _selMarks.Count > 0;
    }

    /// <summary>
    /// 图片贴图的这几笔画在 StickerCanvas 上，而 Alt 快速选词的收尾与关掉「文本可选择」都会把
    /// 这一整层收起 ⇒ 有笔迹时不许收，否则"刚标完、松手那一瞬字上的笔迹就没了"。
    /// 只把命中测试关掉即可：笔迹不吃指针，也不挡下一轮选词。
    /// </summary>
    private void RetainWordMarkLayer()
    {
        if (_isClosed || StickerCanvas == null) return;
        if (!_isCanvasMode && (StickerImage == null || !StickerImage.IsVisible)) return; // 文本／Markdown 贴图没有这一层

        bool selecting = _isTextSelectable || _altQuickSelect;
        // 翻译标记的紫线也住在这一层：只有它、没有三档装饰时同样不许收
        if (selecting || HasWordMarks() || HasWordTransMarks())
        {
            StickerCanvas.IsVisible = true;
            StickerCanvas.IsHitTestVisible = selecting;
        }
        else
        {
            StickerCanvas.IsVisible = false;
            StickerCanvas.IsHitTestVisible = false;
        }
    }

    /// <summary>原地换图（翻译／抠图／恢复原图）＝整幅像素易主，贴在旧图上的那几笔失去意义：整条作废。
    /// 图片贴图上的<b>翻译标记</b>同理——那道紫线与那枚紫点钉的是旧图的像素，一并撤掉
    /// （<see cref="RemoveTranslateMark"/> 自带重画与在飞请求的取消，故直接复用、不另写一份）。</summary>
    private void DropWordMarksForNewPicture()
    {
        foreach (var m in _transMarks.Where(m => m.WordIndices != null).ToList()) RemoveTranslateMark(m);

        if (_selMarks.All(m => m.WordIndices == null)) return;
        _selMarks.RemoveAll(m => m.WordIndices != null);
        AppLogger.Info("[划词标注] 图片已换，字级装饰作废，剩余 " + _selMarks.Count + " 条");
        RedrawWordMarks();
        RefreshClearMarksMenuItem();
    }

    /// <summary>
    /// 按 <see cref="_selMarks"/> 重画两层装饰。两套宿主各画各的层，各自跟着自己的缩放走：
    /// - 文本／Markdown：画在 Viewbox <b>内部</b>的 TextDecorLayer，坐标＝未缩放的内容坐标，跟着 Viewbox
    ///   一起缩放，并被 SnapshotTextSticker 一并收进对外输出的位图（复制图像／另存为的图上就有这几笔）；
    /// - 图片贴图：画在 StickerCanvas 上，与那张图同尺寸同变换，缩放按 <c>WordDisplayScale / SafeScaling</c> 现算。
    /// 块下标会随重排失效、字下标会随重识别失效，故逐条对账，对不上就跳过这一段。
    /// </summary>
    private void RedrawTextDecorations()
    {
        RedrawBlockMarks();
        RedrawWordMarks();
        // 🔴 必须排在 RedrawBlockMarks 之后：那一步会整层清空 TextDecorLayer，紫线画早了就被它抹掉。
        // 翻译标记的紫线与三档装饰同层同纪律（身份对账、几何现取），故并进这一条重画通路，
        // 自动覆盖它全部触发点（换版宽、缩放重建、清覆盖层、Alt 收尾、关掉文本可选择）。
        RedrawTranslateMarks();

        // 文本贴图的 _bitmap 是"对外输出"的那一份：装饰变了必须换新，否则复制图像/另存为
        // 拿到的是没有这几笔的旧图（Markdown 贴图本就没有 _bitmap，那几项实时渲染容器，自带这几笔）
        if (TextViewbox != null && TextViewbox.IsVisible)
        {
            RefreshTextStickerSnapshotAfterReflow();
        }
    }

    private void RedrawBlockMarks()
    {
        if (_isClosed || TextDecorLayer == null) return;
        TextDecorLayer.Children.Clear();

        foreach (var mark in _selMarks)
        {
            if (mark.WordIndices != null) continue;
            var block = GetBlockByIndex(mark.BlockIndex);
            if (block == null) continue;
            if (GetBlockPlainText(block) != mark.BlockText) continue;

            foreach (var rect in GetRectsIn(block, mark.Start, mark.Length, TextDecorLayer))
            {
                DrawMarkShape(mark.Kind, rect, TextDecorLayer);
            }
        }
    }

    private void RedrawWordMarks()
    {
        if (_isClosed || StickerCanvas == null) return;

        foreach (var shape in _imageMarkShapes)
            StickerCanvas.Children.Remove(shape);
        _imageMarkShapes.Clear();

        double k = WordDisplayScale / SafeScaling;
        foreach (var mark in _selMarks)
        {
            var rects = mark.WordRects;
            if (mark.WordIndices == null || rects == null) continue;
            if (!IsWordMarkStillValid(mark)) continue;

            foreach (var r in rects)
            {
                var shape = DrawMarkShape(mark.Kind,
                    new Rect(r.X * k, r.Y * k, r.Width * k, r.Height * k), StickerCanvas);
                if (shape != null) _imageMarkShapes.Add(shape);
            }
        }
    }

    /// <summary>
    /// 字级装饰的失效对账：下标仍在当前 OCR 缓存范围内、且那串字拼出来还是落笔时的文本才算数。
    /// <c>_wordRegions</c> 为空＝只是退出选词态（关掉「文本可选择」），图上像素没变，笔迹照旧留着。
    /// </summary>
    private bool IsWordMarkStillValid(StickerSelMark mark)
    {
        if (_wordRegions == null) return true;
        var ordered = OrderWordIndices(mark.WordIndices!);
        return ordered.Count > 0 && JoinWordText(ordered) == mark.WordText;
    }

    // =====================================================================
    // 图片贴图的划词装饰烘焙（2026-10-01，issue #51）
    // =====================================================================
    // 🔴 为什么单独写一份 GDI+ 烘焙、**不能**塞进 `CaptureSession.RenderAnnotationsToBitmap`：
    //    那条是截图/贴图/录屏三方共用的矢量图元（`AnnotationDocument`）单一真源，而划词装饰
    //    **不是**图元——它是 `_selMarks` 里的一条记录、以 Avalonia Shape 控件活在覆盖层上，
    //    截图侧压根没有划词工具栏。往共享层里加档位＝污染三方共用的真源（protection.md ⑧㈠ 同族纪律）。
    // 为什么只补"图片贴图"这一路：文本/Markdown 贴图那几笔画在 Viewbox 内的 `TextDecorLayer`，
    //    `SnapshotTextSticker()` 把整棵树 render 掉，**本来就进导出**；图片贴图那几笔画在
    //    `StickerCanvas`（Viewbox 外），导出读的是 `_bitmap`，于是只有这一路丢。
    // 几何直接可用、无需换算：`StickerSelMark.WordRects` 的单位就是 **OCR 像素**＝源位图像素坐标系
    //    （见该字段注释），而导出底图正是源位图。
    // ⚠ 配色按**屏幕所见**取（主题感知 `TryFindResource(key, ActualThemeVariant, …)`）：用户看见黄荧光
    //    就该导出黄荧光；深浅两档的荧光透明度本就不同（浅 30% / 深 35%），取当前档即"所见即所得"。

    /// <summary>本贴图此刻有没有"画在 StickerCanvas 上、且不进 `_bitmap`"的划词装饰（图片贴图那一路）。
    /// 文本/Markdown 的块装饰不算（它们随 `SnapshotTextSticker` 自然进导出）。</summary>
    private bool HasImageSelectionMarks()
    {
        if (!_isCanvasMode && StickerImage?.IsVisible != true) return false;   // 非图片贴图：那几笔走文本快照，不由本路负责
        foreach (var m in _selMarks)
        {
            if (m.WordIndices is { Count: > 0 } && m.WordRects is { Count: > 0 }) return true;
        }
        return false;
    }

    /// <summary>把图片贴图上的划词装饰（荧光/虚线/波浪）按 OCR 像素矩形烘到 <paramref name="target"/> 上。
    /// 就地修改传入位图，调用方须保证那是一份**可写副本**而不是 <c>_bitmap</c> 本身。
    /// 几何与 <see cref="DrawMarkShape"/> 逐项对齐（荧光＝填充整框、虚线＝底边上 1.5px 处横线、
    /// 波浪＝底边上 3px 处逐半波二次曲线），改一处必须同步改两处。</summary>
    private void BakeImageSelectionMarks(System.Drawing.Bitmap target)
    {
        if (!HasImageSelectionMarks()) return;

        // 配色取当前主题档（与屏幕所见一致）
        Color hlColor = ThemeColor("SctMarkHlColor", Color.FromArgb(0x4D, 0xFA, 0xCC, 0x15));
        Color lineColor = ThemeColor("SctDangerColor", Color.FromRgb(0xDC, 0x26, 0x26));

        using var g = System.Drawing.Graphics.FromImage(target);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var hlBrush = new System.Drawing.SolidBrush(
            System.Drawing.Color.FromArgb(hlColor.A, hlColor.R, hlColor.G, hlColor.B));
        var gdiLine = System.Drawing.Color.FromArgb(lineColor.A, lineColor.R, lineColor.G, lineColor.B);

        int baked = 0;
        foreach (var m in _selMarks)
        {
            if (m.WordIndices is not { Count: > 0 } || m.WordRects is not { Count: > 0 }) continue;
            foreach (var r in m.WordRects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                switch (m.Kind)
                {
                    case "hl":
                        g.FillRectangle(hlBrush, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
                        break;
                    case "dash":
                    {
                        // StrokeDashArray="3,2" + 圆头，与 XAML 的 Line.stMarkDash 对齐
                        using var pen = new System.Drawing.Pen(gdiLine, 1.4f)
                        {
                            DashStyle = System.Drawing.Drawing2D.DashStyle.Custom,
                            DashPattern = new[] { 3f, 2f },
                            StartCap = System.Drawing.Drawing2D.LineCap.Round,
                            EndCap = System.Drawing.Drawing2D.LineCap.Round,
                        };
                        float y = (float)(r.Bottom - 1.5);
                        g.DrawLine(pen, (float)r.X, y, (float)r.Right, y);
                        break;
                    }
                    case "wave":
                    {
                        using var pen = new System.Drawing.Pen(gdiLine, 1.2f)
                        {
                            StartCap = System.Drawing.Drawing2D.LineCap.Round,
                            EndCap = System.Drawing.Drawing2D.LineCap.Round,
                        };
                        DrawWavyGdi(g, pen, (float)r.X, (float)(r.Bottom - 3), (float)r.Width);
                        break;
                    }
                    default:
                        continue;   // 档位不认识：跳过，不画出一个意义不明的形状
                }
                baked++;
            }
        }
        if (baked > 0)
            AppLogger.Info($"StickerWindow: 已把 {baked} 段划词装饰烘进导出位图（图片贴图路径）");
    }

    /// <summary>波浪线的 GDI+ 版本：参数与 <see cref="BuildWavyPathData"/> 逐值一致
    /// （半波 3.5px、振幅 1.8px、以 <paramref name="baseY"/> 为轴交替），避免两套画法观感不同。</summary>
    private static void DrawWavyGdi(System.Drawing.Graphics g, System.Drawing.Pen pen,
                                    float x0, float baseY, float width)
    {
        const float halfWave = 3.5f;
        const float amplitude = 1.8f;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        float x = 0;
        bool up = true;
        while (x < width - 0.01f)
        {
            float seg = Math.Min(halfWave, width - x);
            // 二次贝塞尔用三次表示：控制点取 2/3 处（数学等价，GraphicsPath 只有三次曲线）
            float cx = x + seg / 2f, cy = baseY + (up ? -amplitude : amplitude);
            float sx = x0 + x, ex = x0 + x + seg;
            float c1x = sx + (cx - x) * 2f / 3f, c1y = baseY + (cy - baseY) * 2f / 3f;
            float c2x = ex + (cx - (x + seg)) * 2f / 3f, c2y = c1y;
            path.AddBezier(sx, baseY, c1x, c1y, c2x, c2y, ex, baseY);
            x += seg;
            up = !up;
        }
        g.DrawPath(pen, path);
    }

    /// <summary>画一枚装饰图元到指定层；返回null＝档位不认识。图片贴图那几枚必须不吃命中测试，否则笔迹挡在字级命中矩形上面，选词就点不动了。</summary>
    private static Shape? DrawMarkShape(string kind, Rect rect, Canvas layer)
    {
        switch (kind)
        {
            case "hl":
            {
                var hl = new Rectangle
                {
                    Width = Math.Max(1, rect.Width),
                    Height = Math.Max(1, rect.Height),
                    IsHitTestVisible = false
                };
                hl.Classes.Add("stMarkHl");
                Canvas.SetLeft(hl, rect.X);
                Canvas.SetTop(hl, rect.Y);
                layer.Children.Add(hl);
                return hl;
            }
            case "dash":
            {
                var dash = new Line
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(Math.Max(1, rect.Width), 0),
                    IsHitTestVisible = false
                };
                dash.Classes.Add("stMarkDash");
                Canvas.SetLeft(dash, rect.X);
                Canvas.SetTop(dash, rect.Bottom - 1.5);
                layer.Children.Add(dash);
                return dash;
            }
            case "wave":
            {
                var wave = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(BuildWavyPathData(Math.Max(1, rect.Width))),
                    IsHitTestVisible = false
                };
                wave.Classes.Add("stMarkWave");
                Canvas.SetLeft(wave, rect.X);
                Canvas.SetTop(wave, rect.Bottom - 3);
                layer.Children.Add(wave);
                return wave;
            }
        }
        return null;
    }

    /// <summary>波浪线：以基线为轴逐半波交替的二次曲线（相对坐标 q，避免逐点绝对值累加误差）。</summary>
    private static string BuildWavyPathData(double width)
    {
        const double halfWave = 3.5;
        const double amplitude = 1.8;

        var sb = new StringBuilder("M 0 0");
        double x = 0;
        bool up = true;
        while (x < width - 0.01)
        {
            double seg = Math.Min(halfWave, width - x);
            sb.Append(" q ")
              .Append(Fmt(seg / 2)).Append(' ').Append(Fmt(up ? -amplitude : amplitude))
              .Append(' ').Append(Fmt(seg)).Append(" 0");
            x += seg;
            up = !up;
        }
        return sb.ToString();
    }

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
