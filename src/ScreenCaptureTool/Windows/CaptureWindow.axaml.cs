using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture.Annotations;
using ScreenCaptureTool.LongScroll;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Services;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 全屏透明截图遮罩窗口。哑窗口：负责铺满虚拟屏幕、显示 SelectionLayer、提供坐标换算与轻量控制条。
/// 业务流程（自动检测、保存、热键）由 <see cref="Services.CaptureSession"/> 协调。
/// </summary>
// 颜色统一：所有标注工具（画笔/箭头/矩形/序号/文本前景/聚光灯描边）共享 SelectedAnnotationColor

public sealed class LongScrollRequestedEventArgs : EventArgs
{
    public LongScrollRequestedEventArgs(ScrollAxis axis) => Axis = axis;

    public ScrollAxis Axis { get; }
}

public enum ShortcutHintState
{
    IdleElementHover,   // A: 空闲·元素检测中
    DraggingNew,        // B: 拖动新选区中
    SelectionConfirmed, // C: 选区已确认（无工具激活）
    ToolActive,         // D: 标注工具激活中
}

public partial class CaptureWindow : Window
{
    private static WeakReference<CaptureWindow>? _activeOverlay;

    /// <summary>真实截图遮罩的层级边界；仅在 UI 线程查询，隐藏、模态降置顶和预备窗均不参与。</summary>
    internal static IntPtr ActiveOverlayHwnd =>
        _activeOverlay != null && _activeOverlay.TryGetTarget(out var window)
        && window.IsVisible && window.IsEnabled && window.Topmost && !window._preparingFirstCapture
            ? window.GetWindowHwnd() : IntPtr.Zero;

    private ShortcutHintState _shortcutHintState = ShortcutHintState.IdleElementHover;
    private Point _lastPointerDip = new Point(-9999, -9999);
    private Size _cachedShortcutHintSize = new Size(260, 220);
    private bool _shortcutHintMeasureDirty = true;
    /// <summary>浮层与选区之间的设计间距。<b>录屏控制条复用同一值</b>（用户要求"跟截图工具条一样"），勿各写一份。</summary>
    internal const double OverlayGap = 10;
    private const double OverlayMargin = 12;
    /// <summary>快捷键提示条离屏幕左下角的距离（比常规浮层留得更开，免得贴着任务栏）。</summary>
    private const double OverlayHintMargin = 16;

    private Rect _lastRegionDip;
    private bool _lastHasSelection;
    private bool _isAutoMosaicScanning;
    private long _autoMosaicScanStartedTick;
    private double _autoMosaicScanHeight;
    private DispatcherTimer? _autoMosaicScanTimer;
    private readonly TranslateTransform _autoMosaicScanTransform = new();
    private string? _activeToolKey;
    /// <summary>水印开关（工具栏那圈紫环的状态真源）。刻意<b>不落盘</b>：每次截图都从"关"开始，
    /// 而内容/位置/三枚参数照旧记住上次值——拍板第 2 条。每个会话新建一个遮罩窗口，故无需复位代码。</summary>
    private bool _watermarkOn;
    /// <summary>录屏参数条的默认档是否已铺过（本会话首次点开时从设置铺一次，之后再开保留用户刚改的值——
    /// 稿 4：参数条改动只作用本次录制，但同一会话内不收条就丢参数）。</summary>
    private bool _recordDefaultsApplied;
    private AnnotationLineStyle _selectedBrushLineStyle = AnnotationLineStyle.Solid;
    private BrushProfile _selectedBrushProfile = BrushProfile.Default;
    private AnnotationLineStyle _selectedShapeLineStyle = AnnotationLineStyle.Solid;
    private ArrowAnnotationStyle _selectedArrowStyle = ArrowAnnotationStyle.Designed;
    /// <summary>箭头样式下拉（条目由枚举生成，与序号引线共用那份表）。收参数栏、切工具时要顺手收起它。</summary>
    private readonly ArrowHeadStyleMenu _arrowStyleMenu;
    private ShapeAnnotationKind _selectedShapeKind = ShapeAnnotationKind.Rectangle;
    private SpotlightShapeKind _selectedSpotlightKind = SpotlightShapeKind.Rectangle;
    private BlurMode _selectedBlurMode = BlurMode.Blur;
    private bool _isAutoMosaicMode;
    private bool _suppressBlurSettingsEvent;
    private bool _suppressSpotlightSettingsEvent;
    /// <summary>回灌"选中图元自己的颜色/粗细"时的抑制位，与上面两条同一语义：摆控件不反手发用户变更事件。</summary>
    private bool _suppressCommonSettingsEvent;
    private bool _suppressCaptureEffectEvent;
    // 圆角记忆（物理像素 0-200）：滑块拆除后由字段承载（2026-09-12「圆角滑块退役」），
    // 选中图元 / 圆角浮条改值时经 Sync* 刷新，新画图元起笔取这里，会话收尾写回 settings。
    //（画布四角手柄已于 2026-09-18 退役，故"改圆角"现在只有浮条一个来路。）
    private const double CornerRadiusMax = 200.0;
    private double _shapeCornerRadius;
    private double _spotlightCornerRadius;
    // _selectedSpotlightStrokeColor 已移除：聚光灯描边统一使用 SelectedAnnotationColor

    public event EventHandler? BrushRequested;
    public event EventHandler? ArrowRequested;
    public event EventHandler? RectangleRequested;
    public event EventHandler? TextRequested;
    public event EventHandler? TextSnapshotRequested;
    public event EventHandler? MosaicRequested;
    public event EventHandler? ManualMosaicRequested;
    public event EventHandler? AutoMosaicRequested;
    public event EventHandler? BlurSettingsChanged;
    public event EventHandler? SpotlightSettingsChanged;
    /// <summary>参数条「取色」被用户改过：宿主只把<b>颜色</b>这一档落到当前选中的图元上。
    /// 原先它与粗细共用一支 <c>AnnotationCommonSettingsChanged</c>，于是"改个颜色"会顺手把选中图元的
    /// 粗细也换成工具栏记忆值——2026-09-19 拆成两支，各档各走各的。</summary>
    public event EventHandler? AnnotationColorChanged;
    /// <summary>参数条「粗细」被用户改过（拖滑杆或在滑杆上滚滚轮）：只落<b>粗细</b>一档。
    /// 画笔笔迹不吃这一档（protection.md 手势优先级 ④），分派表在 <c>AnnotationStyleApplier</c>。</summary>
    public event EventHandler? AnnotationThicknessChanged;
    /// <summary>形状档（矩形／圆形）那枚按钮被按过：只把 <c>ShapeKind</c> 落到选中的形状图元上。</summary>
    public event EventHandler? ShapeKindChanged;
    /// <summary>形状线型（实线／虚线）那枚按钮被按过：只落 <c>LineStyle</c>，本条是用户点名的缺陷来路。</summary>
    public event EventHandler? ShapeLineStyleChanged;
    /// <summary>箭头头型下拉被换过：只落选中箭头的 <c>Style</c>。</summary>
    public event EventHandler? ArrowStyleChanged;
    /// <summary>序号「样式」小样被换过（外形＋底色）：只落<b>当前选中的那一颗</b>，不波及图上其余序号。</summary>
    public event EventHandler? CounterStyleChanged;
    /// <summary>序号「大小」滑杆被拖过（或在滑杆上滚了滚轮）：只落那一颗的半径。</summary>
    public event EventHandler? CounterRadiusChanged;
    /// <summary>序号「引线箭头」下拉被换过：只落那一颗的引线头型。</summary>
    public event EventHandler? CounterTailHeadChanged;
    // 2026-09-12「圆角滑块退役」时本事件随滑块一并移除（当时没有任何"用户改圆角"入口，圆角只从画布手柄进来，
    // 经选中图元的回显写进字段）。2026-09-18 圆角浮条补上了那个入口，事件随之复活：语义仍是
    // "记忆字段 _shapeCornerRadius 刚被用户改过，请把它落到选中矩形上"。
    // ⚠ 同日稍后矩形的画布四角手柄也退役了，故现在**浮条是唯一入口**，这条通路是矩形圆角的唯一落地路径。
    public event EventHandler? ShapeRadiusChanged;
    /// <summary>形状填充勾选/浓淡刚被用户改过：语义与 <see cref="ShapeRadiusChanged"/> 一致——
    /// 记忆值已写进控件，请宿主把它落到当前选中的矩形上（没有选中矩形时它自然成为下一笔的默认值）。</summary>
    public event EventHandler? ShapeFillChanged;
    /// <summary>序号类型档（123 / ABC / ⅠⅡⅢ / 一二三）被用户换掉：宿主据此把图上已画出的序号整体换成新类型并补一次撤销。</summary>
    public event EventHandler<CounterSerialKind>? CounterSerialChanged;
    /// <summary>序号浮条按了 − / +：参数＝编号增量。宿主（CaptureSession）据此改**当前选中的那一个**序号。</summary>
    public event EventHandler<int>? CounterNumberStepRequested;
    /// <summary>序号浮条的说明文字落定（空串＝清除说明）。同上，只管当前选中的那一个。</summary>
    public event EventHandler<string>? CounterNoteRequested;
    /// <summary>序号浮条改了胶囊圆角（参数＝百分比 0–100）。同上，只管当前选中的那一个；
    /// 「下一颗胶囊沿用多少」由本窗口自己回灌进 <c>CounterOptions</c> 的记忆位，不绕宿主。</summary>
    public event EventHandler<int>? CounterCapsuleCornerRequested;
    /// <summary>序号浮条按了「→」：把当前选中那一颗的引线画出来／藏掉（引线数据本身不动）。
    /// 与圆角那一格同一分工——本窗口只转达意图，取反与落文档在宿主。</summary>
    public event EventHandler? CounterTailToggleRequested;
    /// <summary>序号浮条按了 × ：只删当前选中的这一个序号（区别于工具栏那枚"清空当前工具的全部标注"）。</summary>
    public event EventHandler? CounterDeleteRequested;
    public event EventHandler? SpotlightRequested;
    public event EventHandler? CounterRequested;
    /// <summary>水印格被点：本窗口已经把开关翻好、参数条也切过去了，宿主只需<b>收掉手里那支笔</b>
    /// 并重画一次遮罩预览（水印不落笔、不进撤销栈，跟圆角/描边/阴影同族）。</summary>
    public event EventHandler? WatermarkRequested;
    /// <summary>水印参数有变（内容／位置／不透明度／大小／间距任一档）：宿主当场回灌设置并重画预览。</summary>
    public event EventHandler? WatermarkChanged;
    /// <summary>录屏格被点（稿 1 D01/D02）：本窗口只发信号，宿主决定开/关参数条（与水印同一分工，
    /// 但录屏没有跨工具的常驻状态——条收起即等于退出录屏待命）。</summary>
    public event EventHandler? RecordRequested;
    /// <summary>参数条上点了「开始录制」（或 Enter/Space）：宿主拿 <c>RecordOptions.LastBuilt</c> 走转场。</summary>
    public event EventHandler? RecordStartRequested;
    public event EventHandler? UndoRequested;
    public event EventHandler? RedoRequested;
    public event EventHandler? ClearAllAnnotationsRequested;
    public event EventHandler? EraserRequested;
    public event EventHandler? EraserSizeChanged;
    public event EventHandler? EraserModeChanged;
    public event EventHandler<LongScrollRequestedEventArgs>? LongScrollRequested;
    public event EventHandler? OcrRequested;
    public event EventHandler? TranslateRequested;
    public event EventHandler? MattingRequested;
    public event EventHandler? AiVisionRequested;
    public event EventHandler? StickerRequested;
    public event EventHandler? ConfirmRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? QuickSaveRequested;
    public event EventHandler? SaveAsRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? ClearCurrentToolRequested;

    /// <summary>
    /// 对外触发"智能抠图"请求，与点击工具栏智能抠图按钮走完全相同的链路。
    /// 供截图会话快捷键触发。
    /// </summary>
    public void RaiseMattingRequested() => MattingRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 对外触发"贴图"请求，与点击贴图按钮走完全相同的链路。
    /// 供全局热键（如快速贴图 F3）在截图进行中把当前选区转化为贴图。
    /// 事件只能在声明类内部 invoke，故在此提供入口。
    /// </summary>
    public void RaiseStickerRequested() => StickerRequested?.Invoke(this, EventArgs.Empty);

    private const string DynamicToolbarDividerTag = "DynamicToolbarDivider";
    private const string ToolbarGroupHostTag = "ToolbarGroupHost";

    /// <summary>一级主栏与二级成员栏、三级参数条之间的级间距（与参数条既有的 6 DIP 同一档）。</summary>
    private const double OverlayBarGap = 6;

    /// <summary>二级成员栏自身吃掉的横向余量：外板 Padding 1×2 ＋ 内板 Padding 8×2 ＝ 18，
    /// 分隔线宽 1 含左右 margin 4×2 ＝ 9，设置钮 28 含左右 margin 4×2 ＝ 36（共 63，进位到 64）。</summary>
    private const double GroupBarChromeWidth = 64;

    /// <summary>当前布局里的组（成员含组头，顺序即工具栏上的顺序）。</summary>
    private readonly List<List<string>> _toolbarGroups = new List<List<string>>();
    /// <summary>规范 id → 主栏上的控件（组头与独立格都在内；成员控件在二级栏里，不在此表）。</summary>
    private readonly Dictionary<string, Control> _controlById = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
    /// <summary>控件 → 规范 id 的反表：按压事件只拿得到控件，靠它一步定位是哪枚工具。</summary>
    private readonly Dictionary<Control, string> _controlToId = new Dictionary<Control, string>();
    /// <summary>组头 id → 分裂格右半那枚展开钮（专职开合，永不触发工具）；展开态挂 "opened" 类表达浅底。</summary>
    private readonly Dictionary<string, Button> _groupChevronByHead = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
    /// <summary>组头 id → 展开钮内部的小三角 Path（换朝向即换 Data，不用 RenderTransform，规避旋转原点与 % 的坑）。</summary>
    private readonly Dictionary<string, ShapePath> _groupChevronIconByHead = new Dictionary<string, ShapePath>(StringComparer.OrdinalIgnoreCase);
    /// <summary>当前展开的组（内存态、不落盘；null ＝ 全部折叠）。</summary>
    private List<string>? _expandedToolbarGroup;

    private const string ChevronDownData = "M 0 0 L 6.4 0 L 3.2 4 Z";
    private const string ChevronUpData = "M 3.2 0 L 6.4 4 L 0 4 Z";

    /// <summary>
    /// 按 AppSettings.ToolbarLayout 重排主工具栏：调整按钮顺序与可见性，未包含的按钮隐藏；
    /// 组在主栏只占一格分裂按钮（左半＝第一位工具本体，右半＝展开钮），其余成员归二级成员栏，展开时才搬过去。
    /// 分隔线优先复用 XAML 内命名控件，超出 3 条时动态创建。布局测量由 UpdateOverlay 的 Measure 动态完成。
    /// </summary>
    private void ApplyToolbarLayout()
    {
        try
        {
            if (AppSettings.Current.MigrateToolbarLayout())
            {
                AppSettings.Current.Save();
            }
            // 主工具栏不再放独立的「清除全部标注」：把老布局里剩下那一格摘掉（全清入口在橡皮擦参数条里）。
            if (AppSettings.Current.MigrateClearAllAnnotationsToolbar()) AppSettings.Current.Save();
            List<string> tokens = ToolbarRegistry.Parse(AppSettings.Current.ToolbarLayout);

            Dictionary<string, Control> map = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
            {
                { "aivision", BtnAiVision },
                { "mosaic", BtnMosaic },
                { "spotlight", BtnSpotlight },
                { "longscroll", BtnLongScroll },
                { "record", BtnRecord },
                { "ocr", BtnOcr },
                { "translate", BtnTranslate },
                { "matting", BtnMatting },
                { "brush", BtnBrush },
                { "arrow", BtnArrow },
                { "text", BtnText },
                { "rectangle", BtnRectangle },
                { "counter", BtnCounter },
                { "watermark", BtnWatermark },
                { "eraser", BtnEraser },
                { "undo", BtnUndo },
                { "sticker", BtnSticker },
                { "quicksave", BtnQuickSave },
                { "saveas", BtnSaveAs },
                { "save", BtnQuickSave },
                { "cancel", BtnCancel },
                { "confirm", BtnConfirm },
            };

            List<Border> staticDividers = new List<Border> { ToolbarSep1, ToolbarSep2, ToolbarSep3 };

            // 先摘出旧组头，再一次清空主栏；新顺序在脱离视觉树的列表里组装。
            // 逐个 Remove/Insert 会反复触发逻辑树、样式与布局更新，构造窗口时也没有必要。
            foreach (Control oldChild in ToolbarItemsPanel.Children)
            {
                if (oldChild is StackPanel cell && cell.Tag as string == ToolbarGroupHostTag)
                {
                    Control head = cell.Children[0];
                    cell.Children.Remove(head);
                    head.Classes.Remove("tbGroupHead");
                }
            }
            ToolbarItemsPanel.Children.Clear();
            foreach (Border d in staticDividers) d.IsVisible = false;
            _toolbarGroups.Clear();
            _controlById.Clear();
            _controlToId.Clear();
            _groupChevronByHead.Clear();
            _groupChevronIconByHead.Clear();
            _expandedToolbarGroup = null;
            GroupItemsPanel.Children.Clear();
            GroupBar.IsVisible = false;

            // 按控件而非 token 判定可见性：同一控件可注册多个别名（save / quicksave），
            // 而 Parse 已把别名归一化为规范 Id，按 token 判断会让别名键永远未命中并覆盖掉显示状态。
            HashSet<Control> placedControls = new HashSet<Control>();
            List<Control> orderedControls = new List<Control>();
            int dividerUsed = 0;
            foreach (string token in tokens)
            {
                if (token == ToolbarRegistry.DividerToken)
                {
                    Border divider = dividerUsed < staticDividers.Count
                        ? staticDividers[dividerUsed++]
                        : CreateToolbarDivider();
                    divider.IsVisible = true;
                    orderedControls.Add(divider);
                    continue;
                }

                if (ToolbarRegistry.IsGroupToken(token))
                {
                    List<string> members = ToolbarRegistry.GroupMembers(token);
                    if (members.Count == 0 || !map.TryGetValue(members[0], out Control? head) || head == null) continue;

                    _toolbarGroups.Add(members);
                    orderedControls.Add(BuildGroupCell(members[0], head));
                    for (int i = 0; i < members.Count; i++)
                    {
                        if (!map.TryGetValue(members[i], out Control? mc) || mc == null) continue;
                        // 🔴 除组头（它已被装进分裂格）以外，成员必须先从当前面板里摘出来：XAML 里它们本来就是
                        // ToolbarItemsPanel 的静态子级，首遍布局不摘，既会在主栏上重复显示，又会让点箭头搬人时
                        // 抛"already has a visual parent"直接崩掉整个进程（2026-09-13 实测闪退即此）。
                        if (i > 0) DetachFromPanel(mc);
                        // 成员登记进 id↔控件表并算作"已上栏"（可见性恒为 true，摊不摊由二级栏整体决定）
                        RegisterToolbarControl(members[i], mc);
                        placedControls.Add(mc);
                    }
                    continue;
                }

                if (map.TryGetValue(token, out Control? control) && control != null)
                {
                    placedControls.Add(control);
                    RegisterToolbarControl(token, control);
                    orderedControls.Add(control);
                }
            }

            foreach (Control control in map.Values.Distinct())
            {
                control.IsVisible = placedControls.Contains(control);
                // 未入布局的命名按钮仍留在主栏，维持原名称查找与事件引用；只是不显示。
                if (!placedControls.Contains(control)) orderedControls.Add(control);
            }
            foreach (Border divider in staticDividers)
                if (!orderedControls.Contains(divider)) orderedControls.Add(divider);
            ToolbarItemsPanel.Children.AddRange(orderedControls);
            UpdateGroupChevronVisuals();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ApplyToolbarLayout 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 造一级那一格的分裂按钮：左半是组内第一位工具本体（点它＝用这个工具，与普通格逐像素一致），
    /// 右半是一枚只装得下三角的窄钮（点它才开／合这一组，永不触发工具）。
    /// 两半本就是两枚独立控件，故不需要任何"吞点击"的拦截逻辑。
    /// </summary>
    private Control BuildGroupCell(string headId, Control head)
    {
        head.Classes.Add("tbGroupHead");

        ShapePath icon = new ShapePath
        {
            Width = 6.4,
            Height = 4,
            Data = Geometry.Parse(ChevronDownData),
        };
        icon.Bind(ShapePath.FillProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctIconBrush"));
        Button chevron = new Button { Content = icon };
        chevron.Classes.Add("toolIcon");
        chevron.Classes.Add("tbGroupChevron");
        chevron.Click += (_, _) => ToggleToolbarGroup(headId);

        _groupChevronByHead[headId] = chevron;
        _groupChevronIconByHead[headId] = icon;

        StackPanel cell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = ToolbarGroupHostTag,
        };
        cell.Children.Add(head);
        cell.Children.Add(chevron);
        return cell;
    }

    private void RegisterToolbarControl(string id, Control control)
    {
        _controlById[id] = control;
        _controlToId[control] = id;
    }

    /// <summary>
    /// 把一枚控件从它当前所在的面板里摘出来。Avalonia 的 <c>Children.Add</c> 遇到已有父级是**直接抛异常**、
    /// 不会自动迁移，故凡"在几条栏之间搬同一枚控件"都必须先摘后加。
    /// </summary>
    private static void DetachFromPanel(Control control) => (control.Parent as Panel)?.Children.Remove(control);

    /// <summary>该工具所属的组；不在任何组里返回 null。</summary>
    private List<string>? GroupOf(string id)
    {
        foreach (List<string> g in _toolbarGroups)
        {
            if (g.Contains(id, StringComparer.OrdinalIgnoreCase)) return g;
        }
        return null;
    }

    /// <summary>点分裂格右半那枚窄钮：开／合这一组（同一时刻最多一组摊开）。</summary>
    private void ToggleToolbarGroup(string headId)
    {
        foreach (List<string> g in _toolbarGroups)
        {
            if (!string.Equals(g[0], headId, StringComparison.OrdinalIgnoreCase)) continue;
            if (ReferenceEquals(_expandedToolbarGroup, g)) CollapseToolbarGroup();
            else ExpandToolbarGroup(g);
            return;
        }
    }

    /// <summary>展开某一组：除第一位（它一直留在主栏那格里）以外，成员全部搬进二级成员栏。
    /// 🔴 组成员栏与参数栏互斥（同屏最多两条栏：主栏 + 一条副栏）——组一摊开，参数栏连同它的浮层菜单一并收起。</summary>
    private void ExpandToolbarGroup(List<string> group)
    {
        if (ReferenceEquals(_expandedToolbarGroup, group)) return;
        _expandedToolbarGroup = group;
        ToolOptionsBar.IsVisible = false;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
        CloseBrushMenus();
        _arrowStyleMenu.HideIfOpen();
        GroupItemsPanel.Children.Clear();
        for (int i = 1; i < group.Count; i++)
        {
            string id = group[i];
            if (string.Equals(id, ToolbarRegistry.DividerToken, StringComparison.Ordinal))
            {
                GroupItemsPanel.Children.Add(CreateToolbarDivider());
                continue;
            }
            if (!_controlById.TryGetValue(id, out Control? mc)) continue;
            DetachFromPanel(mc);   // 兜底：Add 不迁移已有父级，直接抛
            GroupItemsPanel.Children.Add(mc);
        }
        UpdateGroupChevronVisuals();
        LayoutOverlayStack();
    }

    /// <summary>收起当前摊开的组：成员退回不挂在任何栏上，等下一次展开再搬。
    /// 组一收，第二条栏的位置就空出来了——当前工具若带参数栏，参数条自动回到主栏下方补位。</summary>
    private void CollapseToolbarGroup(bool reflow = true)
    {
        if (_expandedToolbarGroup == null) return;
        _expandedToolbarGroup = null;
        GroupItemsPanel.Children.Clear();
        GroupBar.IsVisible = false;
        if (GroupBarArrow != null) GroupBarArrow.IsVisible = false;
        // 互斥规则的另一半：副栏空出来了，参数栏顶上（工具没换就一直显示它的参数）
        bool hasOptions = HasToolOptions(_activeToolKey) && ActionBar.IsVisible;
        ToolOptionsBar.IsVisible = hasOptions;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = hasOptions;
        UpdateGroupChevronVisuals();
        if (reflow) LayoutOverlayStack();
    }

    /// <summary>按当前展开态刷新每格的三角朝向、浅底与展开钮 tooltip（组名一律取组内第一位工具的名字）。</summary>
    private void UpdateGroupChevronVisuals()
    {
        foreach (List<string> g in _toolbarGroups)
        {
            if (!_groupChevronByHead.TryGetValue(g[0], out Button? chevron)
                || !_groupChevronIconByHead.TryGetValue(g[0], out ShapePath? icon)) continue;
            bool open = ReferenceEquals(g, _expandedToolbarGroup);
            icon.Data = Geometry.Parse(open ? ChevronUpData : ChevronDownData);
            if (open) chevron.Classes.Add("opened");
            else chevron.Classes.Remove("opened");
            string name = ToolbarRegistry.Find(g[0])?.Name ?? "";
            ToolTip.SetTip(chevron, open ? $"收起「{name}」组" : $"展开「{name}」组 · {g.Count} 项");
        }
    }

    /// <summary>
    /// 主栏按压的唯一观察点（Tunnel，只看不吞）：某组摊开时点了这一组之外的任一格就收回去；
    /// 点组内任一格（含这一格左半的工具本体）保持摊开，点在空隙或别组的展开钮上不作动作。
    /// </summary>
    private void OnToolbarPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (_expandedToolbarGroup == null) return;
        string id = "";
        for (Control? c = e.Source as Control; c != null; c = c.Parent as Control)
        {
            if (_controlToId.TryGetValue(c, out string? found)) { id = found; break; }
        }
        if (id.Length == 0) return;
        if (ReferenceEquals(GroupOf(id), _expandedToolbarGroup)) return;
        CollapseToolbarGroup();
    }

    /// <summary>
    /// 快捷键 / 脚本 / Ctrl 点选图元这些非点击入口选中了组内工具时，把该组摊开，让"手里是哪枚"看得见。
    /// 仅限**不带参数栏**的工具——带参数栏的走互斥规则（参数栏就是第二栏，见 SetActiveTool）。
    /// 组的第一位例外：它本来就摆在主栏那一格里，为它摊开反而多占一条栏。
    /// </summary>
    private void RevealToolbarGroupOf(string toolId)
    {
        List<string>? group = GroupOf(toolId);
        if (group == null || string.Equals(group[0], toolId, StringComparison.OrdinalIgnoreCase)) return;
        ExpandToolbarGroup(group);
    }

    /// <summary>
    /// 二级成员栏点击的收尾（气泡阶段、在按钮自身 Click 之后跑）：点组内非头位的工具＝把它提为组头，
    /// 主栏那一格从此显示它。按钮自身 Click 里 <see cref="SetActiveTool"/> 已同步跑完选工具链路：
    /// 参数栏打开（互斥规则顺手把组收掉）；这里再换头整栏重建，参数条经重摆后小三角改指主栏组头。
    /// 换头与设置页"拖到最左＝换组头"同一语义，且写回 ToolbarLayout 落盘，下次截图仍保留。
    /// </summary>
    private void OnGroupMemberClick(object? sender, RoutedEventArgs e)
    {
        string? id = null;
        for (Control? c = e.Source as Control; c != null; c = c.Parent as Control)
        {
            if (_controlToId.TryGetValue(c, out string? found)) { id = found; break; }
        }
        if (id == null) return;
        List<string>? group = GroupOf(id);
        // 🔴 不能拿 `_expandedToolbarGroup` 判"组是摊开的"：参数栏互斥规则（SetActiveTool → ShowToolOptions →
        // CollapseToolbarGroup）会在本观察点之前先把组收掉，守卫必失败、换头整条链路被跳过（2026-09-14（二）实机回归）。
        // 本观察点只挂在 GroupItemsPanel 上——能冒泡到这里的事件源，必然是二级成员栏里那枚成员钮。
        if (group == null || string.Equals(group[0], id, StringComparison.OrdinalIgnoreCase)) return;

        group.Remove(id);
        group.Insert(0, id);
        var cleaned = ToolbarRegistry.CleanGroup(group);
        group.Clear();
        group.AddRange(cleaned);
        PersistGroupHead(group);
        // 整栏重建重入安全：清掉二级栏、收起这一组（互斥规则多半已收，这里兜底）
        ApplyToolbarLayout();
        LayoutOverlayStack();
    }

    /// <summary>
    /// 二级成员栏最右侧「设置」按钮点击：退出当前截图会话，并打开设置窗口直达「工具栏与样式」分页。
    /// </summary>
    private void OnGroupSettingsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
        Dispatcher.UIThread.Post(() =>
        {
            (Application.Current as App)?.OpenSettingsWindow(initialPage: SettingsPage.Toolbar);
        });
    }

    /// <summary>把组内新顺序写回 ToolbarLayout 里同成员的那枚组 token（与设置页共用同一份串）。</summary>
    private void PersistGroupHead(List<string> group)
    {
        try
        {
            List<string> tokens = ToolbarRegistry.Parse(AppSettings.Current.ToolbarLayout);
            var groupTools = group.Where(x => !string.Equals(x, ToolbarRegistry.DividerToken, StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!ToolbarRegistry.IsGroupToken(tokens[i])) continue;
                List<string> members = ToolbarRegistry.GroupMembers(tokens[i]);
                var memTools = members.Where(x => !string.Equals(x, ToolbarRegistry.DividerToken, StringComparison.Ordinal)).ToList();
                if (memTools.Count == groupTools.Count && memTools.All(groupTools.Contains))
                {
                    tokens[i] = ToolbarRegistry.MakeGroupToken(group);
                    AppSettings.Current.ToolbarLayout = string.Join(",", tokens);
                    AppSettings.Current.Save();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PersistGroupHead 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 覆盖层整叠摆位的唯一入口：一级主栏 →（二级成员栏）→（三级参数条），级间恒 <see cref="OverlayBarGap"/>。
    /// 优先放在选区下方；空间不足时选择可容纳整叠或余量更大的一侧，小三角方向随之反转。
    /// 选区上下都放不下时，按主栏实际落点选择子栏展开方向，再将整叠一起夹回屏内，禁止逐条夹边。
    /// 🔴 二级与三级的内容会在同一帧里被换掉（开组、切工具）⇒ 重量必须走 <see cref="LayoutMetrics.MeasureNow"/>；
    /// 一级内容不随选区变化，维持裸 Measure（protection.md「覆盖层同帧量宽」的排除判据）。
    /// </summary>
    private void LayoutOverlayStack()
    {
        if (GroupBarArrow != null) GroupBarArrow.IsVisible = false;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
        if (!_lastHasSelection || !ActionBar.IsVisible)
        {
            GroupBar.IsVisible = false;
            return;
        }

        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);

        Size actionSize = LayoutMetrics.MeasureNow(ActionBar);
        bool groupOpen = _expandedToolbarGroup != null && GroupItemsPanel.Children.Count > 0;
        GroupBar.IsVisible = groupOpen;
        // 组内枚数不限（2026-09-20）：成员行换成 WrapPanel 后要有界宽才会换行，而这条栏走的是
        // LayoutMetrics.MeasureNow 的 Size.Infinity 量重，故先把可用宽度钉在 MaxWidth 上。
        // 减掉的 GroupBarChromeWidth 是这条栏自身吃掉、不该算进成员区的横向余量。
        GroupItemsPanel.MaxWidth = Math.Max(120, screenW - 2 * OverlayMargin - GroupBarChromeWidth);
        Size groupSize = groupOpen ? LayoutMetrics.MeasureNow(GroupBar) : default;
        bool optionsOn = ToolOptionsBar.IsVisible;
        // 🔴 铁律背书：同屏最多两条栏（主栏 + 组成员栏/参数条二者取一）。各互斥入口万一有遗漏，在这里兜底掐掉参数条
        if (groupOpen && optionsOn)
        {
            optionsOn = false;
            ToolOptionsBar.IsVisible = false;
            if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
        }
        Size optionsSize = optionsOn ? LayoutMetrics.MeasureNow(ToolOptionsBar) : default;

        int levels = 1 + (groupOpen ? 1 : 0) + (optionsOn ? 1 : 0);
        double total = actionSize.Height + groupSize.Height + optionsSize.Height + (levels - 1) * OverlayBarGap;
        double belowRoom = screenH - OverlayMargin - _lastRegionDip.Bottom - OverlayGap;
        double aboveRoom = _lastRegionDip.Top - OverlayGap - OverlayMargin;
        bool flip = belowRoom < total && (aboveRoom >= total || aboveRoom > belowRoom);
        double stackTop = flip ? _lastRegionDip.Top - OverlayGap - total : _lastRegionDip.Bottom + OverlayGap;
        if (levels > 1 && belowRoom < total && aboveRoom < total)
        {
            // 选区外放不下整叠时，先求主栏独立落点，再检查它上下的真实余量。
            // 全屏／大选区的主栏已贴近屏幕底部，子栏应向上展开，避免为向下展开而把主栏挤走。
            bool mainAbove = belowRoom < actionSize.Height &&
                (aboveRoom >= actionSize.Height || aboveRoom > belowRoom);
            double mainY = ClampBarY(mainAbove
                ? _lastRegionDip.Top - OverlayGap - actionSize.Height
                : _lastRegionDip.Bottom + OverlayGap, actionSize.Height, screenH);
            double childHeight = total - actionSize.Height;
            double roomAbove = mainY - OverlayMargin;
            double roomBelow = screenH - OverlayMargin - mainY - actionSize.Height;
            flip = mainAbove
                ? roomAbove >= childHeight || roomAbove >= roomBelow
                : roomBelow < childHeight && (roomAbove >= childHeight || roomAbove > roomBelow);
            stackTop = flip ? mainY - childHeight : mainY;
        }
        stackTop = ClampBarY(stackTop, total, screenH);

        // 一级：右缘钉住选区边框右外沿（动态跟随选框粗细，保证垂直视觉完全平齐对齐）
        double selectionBorderVisualOutset = SelectionLayer.GetSelectionOuterOffset(Layer.SelectionBorderThickness);
        double targetRight = _lastRegionDip.Right + selectionBorderVisualOutset;
        double actionX = ClampBarX(targetRight - actionSize.Width, actionSize.Width, screenW);
        double actionY = flip ? stackTop + total - actionSize.Height : stackTop;
        PlaceOverlayBar(ActionBar, actionX, actionY, actionSize);
        Rect stackBounds = new Rect(actionX, stackTop, actionSize.Width, total);
        // 下一级摆哪儿：往下叠＝上一级"底" + 级间距；往上叠（翻转）＝"下一级的底"贴上一级"顶"再退级间距。
        // 🔴 翻转分支必须再减去下一级自己的高度——此前只退了级间距，整叠翻转时二/三级全部压回主栏上、
        // 彼此只错开 6px（选区贴近屏幕底部即触发，2026-09-14（一）修复；用户回报"参数栏出现在别的地方"即此）。
        double y = flip ? actionY - OverlayBarGap - groupSize.Height : actionY + actionSize.Height + OverlayBarGap;

        // 二级：小三角指准这一组的展开钮
        double groupX = actionX;
        if (groupOpen && _groupChevronByHead.TryGetValue(_expandedToolbarGroup![0], out Button? chevronBtn))
        {
            double center = ButtonCenterX(chevronBtn, ActionBar, actionX, actionSize.Width);
            groupX = ClampBarX(center - groupSize.Width / 2.0, groupSize.Width, screenW);
            double groupY = y;
            PlaceOverlayBar(GroupBar, groupX, groupY, groupSize);
            stackBounds = stackBounds.Union(new Rect(groupX, groupY, groupSize.Width, groupSize.Height));
            PositionOverlayArrow(GroupBarArrow, groupX, groupY, groupSize, center, !flip);
            y = flip ? groupY - OverlayBarGap - optionsSize.Height : groupY + groupSize.Height + OverlayBarGap;
        }
        else if (flip)
        {
            // 翻转且二级栏不在：三级直接悬在主栏上方（同样要退参数条自己的高度）
            y = actionY - OverlayBarGap - optionsSize.Height;
        }

        // 三级：小三角指准当前工具所在的那一行（成员栏摊开时是成员栏，否则是主栏）
        if (optionsOn)
        {
            Button? activeBtn = GetActiveToolButton();
            bool inGroupBar = activeBtn != null && ReferenceEquals(activeBtn.Parent, GroupItemsPanel);
            if (!inGroupBar && activeBtn != null && !IsOnMainBar(activeBtn)
                && _controlToId.TryGetValue(activeBtn, out string? activeId)
                && GroupOf(activeId) is List<string> ownedGroup && ownedGroup.Count > 0
                && _controlById.TryGetValue(ownedGroup[0], out Control? headControl))
            {
                // 收着组、手里还拿着组内工具：参数条改指主栏上那一格的组头（它跟工具同属一组，位置看得见）
                activeBtn = headControl as Button;
            }
            double center = ButtonCenterX(activeBtn, inGroupBar ? GroupBar : ActionBar,
                inGroupBar ? groupX : actionX, inGroupBar ? groupSize.Width : actionSize.Width);
            double optionsX = ClampBarX(center - optionsSize.Width / 2.0, optionsSize.Width, screenW);
            double optionsY = y;
            PlaceOverlayBar(ToolOptionsBar, optionsX, optionsY, optionsSize);
            stackBounds = stackBounds.Union(new Rect(optionsX, optionsY, optionsSize.Width, optionsSize.Height));
            PositionOverlayArrow(ToolOptionsArrow, optionsX, optionsY, optionsSize, center, !flip);
        }

        // 尺寸／圆角／阴影条也贴选区上缘：整叠翻到上方或移入选区后，按本帧落点避让。
        ShowInfoPillAt(_lastRegionDip, stackBounds);
    }

    /// <summary>这枚控件此刻是否真挂在一级主栏上（直接做子级，或在某个组格分裂按钮里）。</summary>
    private bool IsOnMainBar(Control control) =>
        ReferenceEquals(control.Parent, ToolbarItemsPanel)
        || (control.Parent as StackPanel)?.Tag as string == ToolbarGroupHostTag;

    private static double ClampBarX(double x, double width, double screenW) =>
        Clamp(x, OverlayMargin, Math.Max(OverlayMargin, screenW - width - OverlayMargin));

    private static double ClampBarY(double y, double height, double screenH) =>
        Clamp(y, OverlayMargin, Math.Max(OverlayMargin, screenH - height - OverlayMargin));

    private void PlaceOverlayBar(Control bar, double x, double y, Size size)
    {
        Canvas.SetLeft(bar, x);
        Canvas.SetTop(bar, y);
        bar.Arrange(new Rect(x, y, size.Width, size.Height));
    }

    /// <summary>
    /// 取某枚按钮的水平中心（窗口坐标）。btn 必须住在 hostBar 这条栏里；未布局时退回该栏中心，
    /// 宁可三角指在栏正中也不指着上一帧的残留位置。
    /// </summary>
    private static double ButtonCenterX(Control? btn, Visual hostBar, double barX, double barWidth)
    {
        if (btn == null) return barX + barWidth / 2.0;
        double btnWidth = btn.Bounds.Width > 0 ? btn.Bounds.Width : 28.0;
        Point? pos = btn.TranslatePoint(new Point(btnWidth / 2.0, 0), hostBar);
        return pos.HasValue && !double.IsNaN(pos.Value.X) ? barX + pos.Value.X : barX + barWidth / 2.0;
    }

    private Border CreateToolbarDivider()
    {
        Border divider = new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0), Tag = DynamicToolbarDividerTag };
        divider.Classes.Add("tbDivider");
        return divider;
    }

    public CaptureWindow()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gc0Before = GC.CollectionCount(0), gc1Before = GC.CollectionCount(1), gc2Before = GC.CollectionCount(2);
        // 菜单仍在 XAML 初始化前构造；移入方法体只是让其成本可单独观察。
        _arrowStyleMenu = new ArrowHeadStyleMenu(legacyPath: true, doubleHead: true);
        long menusCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        long xamlAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        InitializeComponent();
        long xamlCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        long xamlAllocated = GC.GetAllocatedBytesForCurrentThread() - xamlAllocatedBefore;
        SliderMosaicBlockSize.ValueChanged += (_, _) =>
        {
            UpdateToolOptionLabels();
            if (!_suppressBlurSettingsEvent)
            {
                BlurSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        SliderSpotlightDarkness.ValueChanged += (_, _) =>
        {
            UpdateToolOptionLabels();
            RaiseSpotlightSettingsChanged();
        };
        SliderSpotlightStrokeThickness.ValueChanged += (_, _) =>
        {
            UpdateToolOptionLabels();
            RaiseSpotlightSettingsChanged();
        };
        // 圆角滑块已拆除（2026-09-12「圆角滑块退役」），画布四角手柄也于 2026-09-18 退役：
        // 圆角如今只从**元素上方的圆角浮条**进来（见下方 SyncPill 那一段：直接写记忆字段再发 ShapeRadiusChanged），
        // 此处不再挂滑杆事件。
        SliderBrushOpacity.ValueChanged += (_, _) => RebuildBrushProfile();
        // 粗细按钮与滚轮共用一个事件出口。回显选中图元时抑制样式事件。
        SliderStrokeThickness.ValueChanged += (_, _) => RaiseAnnotationThicknessChanged();
        // 形状填充（勾选 + 浓淡）：记忆值就住在共享控件 ShapeFillOptions 里，这里只把用户的改动转发给
        // CaptureSession 落到选中矩形——与圆角浮条那一路（ShapeRadiusChanged）同一套路。
        ShapeFillOptions.FillChanged += () => ShapeFillChanged?.Invoke(this, EventArgs.Empty);
        // 序号、水印与录屏的事件在各参数条首次创建时订阅，不为框选构造隐藏面板。
        // 箭头样式下拉（2026-09-19）：条目由 ArrowAnnotationStyle 生成，与序号「引线箭头」同源。
        // 挂在 Button.Flyout 上而不是原来那块手写 Border：浮层是独立 PopupRoot，不会被遮罩画布判成
        // "在画布上按下"，故 IsOverlayVisual / IsOverlayPoint 那两条命中白名单可以一并撤掉。
        _arrowStyleMenu.Picked += style =>
        {
            _selectedArrowStyle = style;
            UpdateStyleOptionVisuals();
            // 用户点选才走到这里（回显用的 Sync 不发事件），故直接报给宿主落到选中箭头
            ArrowStyleChanged?.Invoke(this, EventArgs.Empty);
        };
        _arrowStyleMenu.Flyout.Opened += (_, _) =>
        {
            CloseBrushMenus();
        };
        BtnArrowStyle.Flyout = _arrowStyleMenu.Flyout;
        SharedColorOptions.ColorSelected += c =>
            ApplyColorToCurrentTarget(System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B));
        SharedColorOptions.CustomColorRequested += OnSharedCustomColorRequested;
        SliderBrushTaper.ValueChanged += (_, _) => RebuildBrushProfile();
        SliderBrushSmoothing.ValueChanged += (_, _) => RebuildBrushProfile();
        SliderBrushMinWidth.ValueChanged += (_, _) => RebuildBrushProfile();
        // 参数条三项参数的「按住数值 → 就地浮起 → 左右拖动微调」：长按 120ms、40px/档、
        // 拖满 700ms 转 15px/档、松手 450ms 收回。状态机与浮起层定位全在控制器里，这里只把两个数值槽接上。
        _pillFine = new PillValueFineController(PillContent, ValueFineLayer, FineM2, FineM1, FineCur, FineP1, FineP2);
        _radiusSlot = _pillFine.Attach(GroupRadius, TxtCaptureRadius, CaptureRadiusMin, CaptureRadiusMax,
            () => _captureRadiusParam, SetCaptureRadiusParam);
        _strokeSlot = _pillFine.Attach(GroupStroke, TxtCaptureStroke, CaptureStrokeMin, CaptureStrokeMax,
            () => _captureStrokeParam, SetCaptureStrokeParam);

        // 圆角浮条（2026-09-18）：改值 → 先写进对应的**圆角记忆**，再走既有的设置回灌通路写进选中图元。
        // 两条分支各自复用既有管道，都不新开一条落地路径：
        //   · 聚光灯 → RaiseSpotlightSettingsChanged（CaptureSession.ApplySpotlightStyleToSelection 会重建图元并进撤销）；
        //   · 矩形图元 → ShapeRadiusChanged（CaptureSession.ApplyShapeRadiusToSelection 同一套路）。
        // 顺带的结果是"调完这一颗，下一颗新画的也是这个圆角"——因为记忆字段就是新建图元的取值来源。
        SpotlightRadiusPillHost.RadiusChanged += radius =>
        {
            if (AnnotationLayer.GetSelectedShapeForOverlay() is RectangleShape)
            {
                _shapeCornerRadius = ClampCornerRadius(radius);
                ShapeRadiusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            _spotlightCornerRadius = ClampCornerRadius(radius);
            RaiseSpotlightSettingsChanged();
        };
        // 选中几何一变就把浮条挪过去。两条来路：选中/取消（`SelectedShapeId` 换人）、文档变化
        //（撤销/删除/方向键步进/松手提交）。⚠ **不含拖动与 8 向缩放的预览帧**——那些帧只写图层预览、
        // 不碰文档，故浮条不会跟手；2026-09-18 的口径是起手处主动收起、松手提交后按新几何贴回。
        AnnotationLayer.SelectionGeometryChanged += RefreshSpotlightRadiusPill;

        // 序号浮条（2026-09-18）：与圆角浮条同一条分工——控件只报意图，压快照/写图元/回显全在 CaptureSession。
        CounterPillHost.NumberStepped += delta => CounterNumberStepRequested?.Invoke(this, delta);
        CounterPillHost.NoteCommitted += note => CounterNoteRequested?.Invoke(this, note);
        CounterPillHost.DeleteRequested += () => CounterDeleteRequested?.Invoke(this, EventArgs.Empty);
        // 胶囊圆角（2026-09-19）：与圆角浮条那一路同一分工——先写记忆位（下一颗胶囊沿用），再报给宿主改手上这颗。
        CounterPillHost.CapsuleCornerChanged += corner =>
        {
            CounterOptions.SetCapsuleCorner(corner);
            CounterCapsuleCornerRequested?.Invoke(this, corner);
        };
        // 引线开关（2026-09-19）：这一格不写记忆位——"新建的那颗要不要画引线"只取决于起笔有没有拖出去，
        // 藏/显是**这一颗**的临时观感，不该渗进下一笔。
        CounterPillHost.TailToggled += () => CounterTailToggleRequested?.Invoke(this, EventArgs.Empty);
        // 说明编辑途中"点空白处＝只结字"的判据（2026-09-19 实报：那一下还会顺手落一颗新序号）。
        // 浮层清单只有本窗口认得，故把判据交给浮条、由它在窗口根的 Tunnel 里吞掉那一笔；
        // 与 CaptureSession 起笔处用的正是同两条判据，两边不会判成两种结果。
        CounterPillHost.CanvasPressTest = e =>
        {
            var dip = e.GetPosition(this);
            return !(IsOverlayPoint(dip) || IsOverlayHit(e.Source));
        };
        AnnotationLayer.SelectionGeometryChanged += RefreshCounterPill;

        ChkCaptureShadow.IsCheckedChanged += (_, _) =>
        {
            RefreshCaptureEffectLabels();
            RaiseCaptureEffectChanged();
        };
        // 参数条收起时悬浮态一并收掉：控件是从指针底下凭空消失的，Avalonia 不会补发 PointerExited，
        // 不主动清就会把描边/阴影预览永久挂在选区上——正是这组闸门要避免的"常驻"。
        // 浮起层是 InfoPill 的兄弟节点、不会跟着它一起隐藏，所以这里必须显式收回，否则数字残留在屏上。
        InfoPill.PropertyChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Property, IsVisibleProperty) || InfoPill.IsVisible) return;
            SetCaptureEffectHover(false);
            _pillFine.Collapse();
        };
        long bindingsCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        AutoMosaicScanFill.RenderTransform = _autoMosaicScanTransform;
        UpdateMosaicModeVisuals();
        RebuildBrushProfile();
        UpdateToolOptionLabels();
        long labelsCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        long toolbarAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ApplyToolbarLayout();
        long toolbarCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        long toolbarAllocated = GC.GetAllocatedBytesForCurrentThread() - toolbarAllocatedBefore;
        // 组的展开/收起入口是分裂格里那枚独立的展开钮（自己的 Click）；这里只留一个观察点：点了组外任一格就收起
        ActionBar.AddHandler(InputElement.PointerPressedEvent, OnToolbarPointerPressedTunnel, RoutingStrategies.Tunnel);
        // 笔迹两枚菜单（线型／笔锋）是画布上的普通 Border，不是 Popup/Flyout ⇒ 框架侧没有任何自动收起通路。
        // 在窗口根的 Tunnel 相位补"点外面就收"：早于画布与 CaptureSession 看到这一笔，但**不吞事件**——
        // 吞掉会改动画布起笔语义（见 docs/protection.md「标注画布鼠标手势优先级」），这一笔照旧各干各的。
        AddHandler(InputElement.PointerPressedEvent, OnBrushMenusDismissPressed, RoutingStrategies.Tunnel);
        // 二级成员栏：点组内非头位的工具＝把它提为组头并收起二级栏（见 OnGroupMemberClick）。
        // 🔴 第 4 参 handledEventsToo 必须为 true——各工具按钮自己的 Click 处理器（OnBrushClick 等）一律
        // e.Handled=true，不声明的话这个气泡观察点永远收不到事件，换组头整条链路就是死代码。
        GroupItemsPanel.AddHandler(Button.ClickEvent, OnGroupMemberClick, RoutingStrategies.Bubble, handledEventsToo: true);

        // Opened 之后再设置位置和尺寸：窗口未创建底层句柄时 DesktopScaling 不可靠
        Opened += OnWindowOpened;

        // 文本拖拽和旋转通过 CaptureSession→TryHandleTextToolInteraction 统一处理
        // 全局 PointerPressed/Moved/Released 只处理拖拽和旋转的连续性
        OverlayCanvas.Focusable = true;
        OverlayCanvas.PointerMoved += OnOverlayCanvasPointerMoved;
        OverlayCanvas.PointerReleased += OnOverlayCanvasPointerReleased;
        PointerMoved += OnOverlayCanvasPointerMoved;
        PointerReleased += OnOverlayCanvasPointerReleased;
        AddHandler(KeyDownEvent, Text.OnTextEditingKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyUpEvent, Text.OnTextEditingKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

            SelectionLayer.PropertyChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Property, Controls.SelectionLayer.RegionProperty))
                {
                    // 全局拖框由同一帧在指针和选区均更新后统一避让，避免同帧测量两次。
                    if (!GlobalMouseDragActive) EvaluateShortcutHintVisibility();
                    // 选区一动，水印的落点框就得跟着动：平铺铺多少块、角落档贴哪条边全由这一块定。
                    // 配置对象本身没变，故只换矩形那一格（标注层的 setter 自带重绘）。
                    if (_watermarkOn) AnnotationLayer.WatermarkRegion = Layer.Region;
                }
            };

            SizeChanged += (_, _) => EvaluateShortcutHintVisibility();
            ScalingChanged += (_, _) => _shortcutHintMeasureDirty = true;
            PropertyChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Property, ActualThemeVariantProperty) ||
                    ReferenceEquals(e.Property, FontSizeProperty) || ReferenceEquals(e.Property, FontFamilyProperty))
                    _shortcutHintMeasureDirty = true;
            };

            // 工具栏 Tooltip 与放大镜提示按当前快捷键绑定动态生成（用户可在设置中改键）
        long inputCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyShortcutHints();
        long completed = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (AppLogger.DetailedLoggingEnabled) _constructorDiagnostics = $"PERF CaptureWindow.Constructor.Phases "
            + $"menus={(menusCompleted - started) * ms:F3}ms xaml={(xamlCompleted - menusCompleted) * ms:F3}ms "
            + $"bindings={(bindingsCompleted - xamlCompleted) * ms:F3}ms labels={(labelsCompleted - bindingsCompleted) * ms:F3}ms "
            + $"toolbar={(toolbarCompleted - labelsCompleted) * ms:F3}ms input={(inputCompleted - toolbarCompleted) * ms:F3}ms "
            + $"hints={(completed - inputCompleted) * ms:F3}ms body={(completed - started) * ms:F3}ms "
            + $"threadAllocated={(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1048576.0:F3}MB "
            + $"xamlAllocated={xamlAllocated / 1048576.0:F3}MB toolbarAllocated={toolbarAllocated / 1048576.0:F3}MB "
            + $"gc0={GC.CollectionCount(0) - gc0Before} gc1={GC.CollectionCount(1) - gc1Before} gc2={GC.CollectionCount(2) - gc2Before} "
            + "(方法体不含基类构造；GC计数为进程级，分配为当前线程托管内存)";
    }

    private string? _constructorDiagnostics;

    private AppSettings? _annotationPanelDefaults;
    private CounterOptionsControl? _counterOptions;
    private WatermarkOptionsControl? _watermarkOptions;
    private RecordOptionsControl? _recordOptions;

    /// <summary>保留共享控件与原取值入口，仅延迟截图侧的首次实例化；每个窗口只创建一次。</summary>
    public CounterOptionsControl CounterOptions
    {
        get
        {
            if (_counterOptions != null) return _counterOptions;
            using var timing = PerformanceTimer.Measure("CaptureWindow.CreateCounterOptions");
            var options = new CounterOptionsControl();
            // 先恢复记忆，再订阅宿主事件，初始化不能被当成用户修改落到图元上。
            options.ApplyDefaults(_annotationPanelDefaults ?? AppSettings.Current);
            var c = SelectedAnnotationColor;
            options.SetAnnotationColor(Color.FromArgb(c.A, c.R, c.G, c.B));
            options.Slider.ValueChanged += (_, _) => RaiseCounterRadiusChanged();
            options.SerialKindChanged += kind => CounterSerialChanged?.Invoke(this, kind);
            options.StyleChanged += () => CounterStyleChanged?.Invoke(this, EventArgs.Empty);
            options.TailHeadChanged += () => CounterTailHeadChanged?.Invoke(this, EventArgs.Empty);
            _counterOptions = options;
            CounterOptionsHost.Content = options;
            return options;
        }
    }

    public WatermarkOptionsControl WatermarkOptions
    {
        get
        {
            if (_watermarkOptions != null) return _watermarkOptions;
            using var timing = PerformanceTimer.Measure("CaptureWindow.CreateWatermarkOptions");
            var options = new WatermarkOptionsControl();
            options.ApplyDefaults(_annotationPanelDefaults ?? AppSettings.Current);
            options.Changed += () => WatermarkChanged?.Invoke(this, EventArgs.Empty);
            _watermarkOptions = options;
            WatermarkOptionsHost.Content = options;
            return options;
        }
    }

    public RecordOptionsControl RecordOptions
    {
        get
        {
            if (_recordOptions != null) return _recordOptions;
            using var timing = PerformanceTimer.Measure("CaptureWindow.CreateRecordOptions");
            var options = new RecordOptionsControl();
            options.StartRequested += () => RecordStartRequested?.Invoke(this, EventArgs.Empty);
            _recordOptions = options;
            RecordOptionsHost.Content = options;
            return options;
        }
    }

    internal void FlushConstructionDiagnostics(string entry)
    {
        if (_constructorDiagnostics is not { } summary) return;
        _constructorDiagnostics = null;
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug(summary + " entry=" + entry);
    }

    /// <summary>按 BuiltinShortcutsStore 当前绑定刷新工具栏 Tooltip 与放大镜提示文案。
    /// 🔴 这里是工具栏键名的<b>唯一出口</b>：下面那张表列全了每一枚带键的按钮，新增按钮只往表里加一行。
    /// 旧写法是"谁想起来谁 SetTip"，结果 8 枚漏在外面（录屏／文字识别／长截图／贴图／AI 识图／水印／
    /// 快速保存／另存为），XAML 里又各写死一份键名 ⇒ 用户在设置页改了键，工具栏还挂着旧键。</summary>
    public void ApplyShortcutHints()
    {
        try
        {
            var map = Services.BuiltinShortcutsStore.LoadGroupMap(Services.BuiltinShortcutsStore.GroupCapture);
            void Tip(Avalonia.Controls.Control? c, string id, string label)
            {
                if (c == null) return;
                string key = map.TryGetValue(id, out var sc) ? Services.BuiltinShortcutsStore.Format(sc) : "";
                Avalonia.Controls.ToolTip.SetTip(c, string.IsNullOrEmpty(key) ? label : $"{label} ({key})");
            }

            // 标注工具
            Tip(BtnMosaic, "capture.mosaic", "马赛克");
            Tip(BtnSpotlight, "capture.spotlight", "聚光灯");
            Tip(BtnBrush, "capture.brush", "画笔");
            Tip(BtnArrow, "capture.arrow", "箭头");
            Tip(BtnText, "capture.text", "文本");
            Tip(BtnRectangle, "capture.shape", "形状");
            Tip(BtnCounter, "capture.counter", "序号");
            Tip(BtnEraser, "capture.eraser", "橡皮擦：局部擦除标注");
            Tip(BtnUndo, "capture.undo", "撤销");
            // 动作类
            Tip(BtnTranslate, "capture.translate", "翻译");
            Tip(BtnMatting, "capture.matting", "智能抠图");
            Tip(BtnLongScroll, "capture.longscroll", "长截图");
            Tip(BtnOcr, "capture.ocr", "文字识别（OCR）");
            Tip(BtnRecord, "capture.record", "录屏");
            Tip(BtnSticker, "capture.sticker", "贴图");
            Tip(BtnAiVision, "capture.aivision", "AI 识图（智能视觉分析）");
            Tip(BtnWatermark, "capture.watermark", "水印（再点一次关闭）");
            Tip(BtnQuickSave, "capture.quicksave", "快速保存");
            Tip(BtnSaveAs, "capture.saveas", "另存为");
            Tip(BtnConfirm, "capture.confirm", "完成/保存");
            Tip(BtnCancel, "capture.cancel", "取消");

            string colorPickerKey = map.TryGetValue("capture.colorpicker", out var cp) ? Services.BuiltinShortcutsStore.Format(cp) : "";
            MagnifierHintText.Text = string.IsNullOrEmpty(colorPickerKey) ? "Shift切换格式。" : $"{colorPickerKey}复制。Shift切换格式。";
            UpdateShortcutHintText();
        }
        catch (Exception ex)
        {
            Core.AppLogger.Warn("CaptureWindow.ApplyShortcutHints 失败：" + ex.Message);
        }
    }

    public void SetShortcutHintState(ShortcutHintState state)
    {
        if (_shortcutHintState == state) return;
        _shortcutHintState = state;
        UpdateShortcutHintText();
    }

    private void UpdateShortcutHintText()
    {
        if (ShortcutHintPill == null || ShortcutHintGrid == null) return;
        _shortcutHintMeasureDirty = true;
        var map = Services.BuiltinShortcutsStore.LoadGroupMap(Services.BuiltinShortcutsStore.GroupCapture);
        string F(string id, string fallback) => map.TryGetValue(id, out var sc) ? Services.BuiltinShortcutsStore.Format(sc) : fallback;

        string confirmKey = F("capture.confirm", "Enter");
        string cancelKey = F("capture.cancel", "Esc");

        ShortcutHintGrid.Children.Clear();
        ShortcutHintGrid.RowDefinitions.Clear();

        // 一行一个操作：左列键位（可改键的实时读绑定表）、右列说明，两列各自对齐成一张小抄。
        void AddRow(string key, string desc)
        {
            int row = ShortcutHintGrid.RowDefinitions.Count;
            ShortcutHintGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var keyText = new TextBlock
            {
                Text = key,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 3, 14, 3),
            };
            var descText = new TextBlock
            {
                Text = desc,
                FontSize = 14,
                Foreground = Brush.Parse("#B8FFFFFF"),
                Margin = new Thickness(0, 3, 0, 3),
            };

            Grid.SetRow(keyText, row);
            Grid.SetColumn(keyText, 0);
            Grid.SetRow(descText, row);
            Grid.SetColumn(descText, 1);
            ShortcutHintGrid.Children.Add(keyText);
            ShortcutHintGrid.Children.Add(descText);
        }

        if (PathEditor is { Mode: not ScreenCaptureTool.Core.Capture.CaptureSelectionMode.Rectangle } editor && !HasAnnotationTool)
        {
            if (!editor.IsClosed)
            {
                if (editor.Mode == ScreenCaptureTool.Core.Capture.CaptureSelectionMode.Freehand)
                {
                    AddRow("按住左键拖动", "自由描绘选区轮廓");
                    AddRow("松开左键", "自动闭合选区");
                }
                else
                {
                AddRow("单击", "添加选区节点");
                if (editor.Mode == ScreenCaptureTool.Core.Capture.CaptureSelectionMode.Pen) AddRow("按住拖动", "调整曲线控制柄");
                AddRow("点击起点 / Enter", "闭合选区");
                }
                AddRow("右键 / Backspace", "撤回上一步");
            }
            else
            {
                if (editor.Mode != ScreenCaptureTool.Core.Capture.CaptureSelectionMode.Freehand)
                    AddRow("拖动节点 / 控制柄", "微调选区轮廓");
                AddRow("拖动内部 / 方向键", "移动选区");
                AddRow("Ctrl Z", "撤回选区编辑");
                AddRow(confirmKey, "完成截图");
                AddRow("中键", "贴到屏幕");
            }
            AddRow("Esc", "取消截图");
            AddRow("R", "切回矩形选区");
            AddRow(F("capture.colorpicker", "C"), "复制光标处颜色");
            AddRow("Shift", "切换取色格式（HEX / RGB）");
        }
        else switch (_shortcutHintState)
        {
            case ShortcutHintState.IdleElementHover:
                AddRow("左键拖框", "框选区域");
                AddRow("单击", "选中当前检测到的元素");
                AddRow(F("capture.colorpicker", "C"), "复制光标处颜色");
                AddRow("Shift", "切换取色格式（HEX / RGB）");
                AddRow("W / A / S / D", "微移光标（1px）");
                AddRow("滚轮", "切换嵌套元素层级");
                AddRow("按住 Alt", "显示参考线 / 十字准星");
                AddRow("中键", "把检测到的元素直接贴图");
                AddRow(F("capture.restore", "R"), "恢复上一次截图框");
                AddRow(confirmKey, "完成截图");
                AddRow(cancelKey, "取消截图");
                break;

            case ShortcutHintState.DraggingNew:
                AddRow("松开左键", "确认选区");
                AddRow("按住 空格", "平移选区");
                AddRow("中键", "不必松手，直接贴图");
                AddRow(confirmKey, "不必松手，直接完成截图");
                AddRow(cancelKey, "取消截图");
                break;

            case ShortcutHintState.SelectionConfirmed:
                AddRow("拖动选区", "移动位置");
                AddRow("拖四周手柄", "调整大小");
                AddRow("方向键", "微移选区（1px）");
                AddRow("滚轮", "扩缩选区");
                AddRow("Space", "切换上次使用的工具");
                AddRow("双击", "复制并完成截图");
                AddRow("中键", "贴到屏幕");
                AddRow(F("capture.restore", "R"), "恢复上一次截图框");
                AddRow(F("capture.translate", "Ctrl+Q"), "翻译选区");
                AddRow(F("capture.saveas", "Ctrl+S"), "另存为");
                AddRow(F("capture.quicksave", "Ctrl+Shift+S"), "快速保存到预设目录");
                AddRow(confirmKey, "完成截图");
                AddRow(cancelKey, "取消截图");
                break;

            case ShortcutHintState.ToolActive:
                AddRow("拖动", "绘制当前工具");
                AddRow("Ctrl + 点击", "选中 / 拖动已画图形");
                AddRow("滚轮", "调整当前工具参数");
                AddRow(F("capture.delete", "Delete"), "删除选中标注");
                AddRow("Space", "退出当前工具");
                AddRow(F("capture.undo", "Ctrl+Z"), "撤销");
                AddRow(F("capture.redo", "Ctrl+Y"), "重做");
                AddRow("双击", "复制并完成截图");
                AddRow("中键", "贴到屏幕");
                AddRow(confirmKey, "完成截图");
                AddRow(cancelKey, "取消截图");
                break;
        }

        // 上一行刚 AddRow 完，直接 Measure 会读到中间层面板缓存里的上一档行宽
        Size gridDesired = LayoutMetrics.MeasureNow(ShortcutHintGrid);
        if (gridDesired.Width > 0 && gridDesired.Height > 0)
        {
            // ShortcutHintPill Padding="14,11"，水平留 28，垂直留 22
            _cachedShortcutHintSize = new Size(
                Math.Max(120, gridDesired.Width + 28),
                Math.Max(60, gridDesired.Height + 22));
        }

        EvaluateShortcutHintVisibility();
    }

    public void EvaluateShortcutHintVisibility()
    {
        if (ShortcutHintPill == null) return;

        if (!AppSettings.Current.ShowCaptureHints)
        {
            ShortcutHintPill.IsVisible = false;
            return;
        }

        double screenH = Bounds.Height > 0 ? Bounds.Height : (!double.IsNaN(Height) && Height > 0 ? Height : 0);
        if (screenH <= 0)
        {
            ShortcutHintPill.IsVisible = false;
            return;
        }

        // 拖框时提示内容固定，只在内容／主题／DPI 变化后实测；不可见时不能量成 0。
        if (ShortcutHintPill.IsVisible && (!GlobalMouseDragActive || _shortcutHintMeasureDirty))
        {
            // 行内容刚被 SetShortcutHintState 整批换过 ⇒ 递归失效后才量得到本帧尺寸
            long start = _captureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Size m = LayoutMetrics.MeasureNow(ShortcutHintPill);
            if (_captureDiagnostics)
            {
                long took = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                _hintMeasureCount++;
                _hintMeasureTotalTicks += took;
                _hintMeasureMaxTicks = Math.Max(_hintMeasureMaxTicks, took);
            }
            if (m.Width > 0 && m.Height > 0)
            {
                _cachedShortcutHintSize = m;
                _shortcutHintMeasureDirty = false;
            }
        }

        Size desired = _cachedShortcutHintSize;
        double pillX = OverlayHintMargin;
        double pillY = Math.Max(OverlayMargin, screenH - desired.Height - OverlayHintMargin);

        Rect pillRect = new Rect(pillX, pillY, desired.Width, desired.Height);

        // 1. 指针靠近避让（带迟滞防抖：显示时 6px 触发避让，隐藏后需移至 16px 之外才恢复，防止边缘光标微颤震荡）
        double hitMargin = ShortcutHintPill.IsVisible ? 6 : 16;
        Rect pillPointerAvoidRect = pillRect.Inflate(hitMargin);
        if (pillPointerAvoidRect.Contains(_lastPointerDip))
        {
            ShortcutHintPill.IsVisible = false;
            return;
        }

        // 2. 真实选区相交避让（仅当用户拉出或确认了真实选区时才避让）
        Rect region = Layer.Region;
        if (region.Width > 0 && region.Height > 0)
        {
            if (pillRect.Inflate(8).Intersects(region))
            {
                ShortcutHintPill.IsVisible = false;
                return;
            }
        }

        // 注意：不再对 Layer.AutoHoverRegion 进行相交避让！
        // 自动探测元素（尤其是全屏/最大化的大窗口）在鼠标移动时会在整窗大框与局部子控件小框之间频繁跳变，
        // 将大窗口高亮框作为避让源会导致提示窗频繁显隐而疯狂闪烁。
        // 用户若需查看或操作左下角区域，光标移向左下角时已由上方「指针靠近避让」平滑隐藏，无需 AutoHoverRegion 参与。

        ShortcutHintPill.IsVisible = true;
    }

    /// <summary>对外暴露 SelectionLayer，让 CaptureSession 直接给 Region 赋值。</summary>
    public SelectionLayer Layer => SelectionLayer;

    /// <summary>对外暴露 AnnotationLayer，让 CaptureSession 绑定标注文档。</summary>
    public AnnotationLayer Annotation => AnnotationLayer;

    // 保留原有参数访问路径，让画像重建、设置恢复和保存继续使用同一控件。
    private Slider SliderBrushOpacity => BrushOpacityOptions.Slider;
    private TextBlock TxtBrushOpacity => BrushOpacityOptions.ValueText;
    private Border CurrentColorPreview => SharedColorOptions.Preview;
    internal Slider SliderSpotlightDarkness => SharedSpotlightOptions.Darkness;
    internal Slider SliderSpotlightStrokeThickness => SharedSpotlightOptions.Stroke;
    private TextBlock TxtSpotlightDarkness => SharedSpotlightOptions.DarknessValue;
    private TextBlock TextSpotlightStrokeValue => SharedSpotlightOptions.StrokeValue;
    private Avalonia.Controls.Shapes.Ellipse SpotlightStrokeCircle => SharedSpotlightOptions.StrokePreview;
    internal Slider SliderCounterRadius => CounterOptions.Slider;
    private TextBlock TxtCounterRadius => CounterOptions.ValueText;
    internal Slider SliderMosaicBlockSize => BlurIntensityOptions.Slider;
    private TextBlock TxtMosaicBlockSize => BlurIntensityOptions.ValueText;
    internal Slider SliderStrokeThickness => StrokeThicknessOptions.Slider;

    /// <summary>设置截图选择界面的冻结背景，避免透明全屏窗口显示到 DWM 的过期桌面缓存。</summary>
    public void SetFrozenScreenBitmap(System.Drawing.Bitmap bitmap)
    {
        FrozenScreenImage.Source = Utils.BitmapConversion.ToAvaloniaBitmap(bitmap);
    }

    /// <summary>设置截图选择界面的冻结背景（直接消费后台线程已转换好的 Avalonia 位图，消除 UI 线程转换开销）。</summary>
    public void SetFrozenScreenBitmap(Avalonia.Media.Imaging.Bitmap? avaloniaBitmap)
    {
        FrozenScreenImage.Source = avaloniaBitmap;
    }

    /// <summary>真实冻结图在渲染线程完成本批绘制后才揭开；不以 RAF 回调冒充图片已经呈现。</summary>
    internal async Task ShowCaptureAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (FrozenScreenImage.Source == null)
            throw new InvalidOperationException("截图冻结图不可用，取消显示遮罩");
        bool cloaked = WindowPresentationHelper.TrySetCloaked(this, true);
        try
        {
            Show();
            if (!cloaked) return;
            var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this);
            if (visual == null) throw new InvalidOperationException("截图合成器不可用");
            var rendered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 布局与绑定更新之后提交，等待 Rendered 而不是仅代表反序列化完成的 Processed。
            visual.Compositor.RequestCompositionUpdate(() =>
            {
                try
                {
                    rendered.TrySetResult(IsVisible
                        ? visual.Compositor.RequestCompositionBatchCommitAsync().Rendered
                        : Task.CompletedTask);
                }
                catch (Exception ex) { rendered.TrySetException(ex); }
            });
            var rendering = rendered.Task.Unwrap();
            if (await Task.WhenAny(rendering, Task.Delay(1000)) != rendering)
                throw new TimeoutException("截图首帧绘制超时");
            await rendering;
        }
        catch
        {
            Hide(); // 失败先收回，不能揭开旧纹理；由会话原有异常出口释放资源。
            throw;
        }
        finally
        {
            if (cloaked && !WindowPresentationHelper.TrySetCloaked(this, false) && IsVisible)
            {
                Hide();
                throw new InvalidOperationException("截图首帧无法解除隐匿");
            }
        }
    }

    /// <summary>窗口在屏幕坐标系中的原点（物理像素）。</summary>
    public PixelPoint ScreenOrigin => Position;

    public System.Drawing.Color SelectedAnnotationColor { get; private set; } = System.Drawing.Color.FromArgb(255, 51, 136, 255);

    public AnnotationLineStyle SelectedBrushLineStyle => _selectedBrushLineStyle;

    /// <summary>画笔笔刷画像（透明度 / 硬度 / 笔锋 / 平滑），由画笔参数栏驱动。</summary>
    public BrushProfile SelectedBrushProfile => _selectedBrushProfile;

    public AnnotationLineStyle SelectedShapeLineStyle => _selectedShapeLineStyle;

    public ArrowAnnotationStyle SelectedArrowStyle => _selectedArrowStyle;

    public ShapeAnnotationKind SelectedShapeKind => _selectedShapeKind;

    public string? ActiveToolKey => _activeToolKey;

    /// <summary>Enter/Space 快捷开始录制（稿 1 D14）。参数条未亮时返回 false，让全局 Enter 确认截图照常走。</summary>
    public bool TryStartRecordByKeyboard() =>
        _activeToolKey == "Record" && RecordOptionsPanel.IsVisible && RecordOptions.TryStartByKeyboard();

    /// <summary>Esc 收录屏参数条回框选态（Esc 分级第一级）。条未亮时返回 false，Esc 继续走会话取消。</summary>
    public bool TryCancelRecordByKeyboard()
    {
        if (_activeToolKey != "Record" || !RecordOptionsPanel.IsVisible) return false;
        SetActiveTool(null);
        return true;
    }

    /// <summary>会话侧取当前参数条的录制参数（Region 由会话以最终选区覆写）。若快速录制尚未展开参数条，先铺一次默认档/上次使用值。</summary>
    public ScreenCaptureTool.Core.Recording.RecordingOptions BuildRecordOptions()
    {
        if (!_recordDefaultsApplied)
        {
            RecordOptions.ApplyDefaults();
            _recordDefaultsApplied = true;
        }
        return RecordOptions.BuildOptions();
    }

    /// <summary>当前录屏格式（GIF / WebP 转码在 M3 消费此值）。</summary>
    public ScreenCaptureTool.Core.Recording.RecordFormat RecordFormatChoice => RecordOptions.Format;

    /// <summary>水印开关（工具栏那圈紫环即此值）。不落盘，每次截图从关开始。</summary>
    public bool WatermarkEnabled => _watermarkOn;

    /// <summary>
    /// 这一次要盖的水印快照；<b>未开启</b>或<b>内容为空</b>都返回 null——「留空不盖章、也不弹提示」那条口径
    /// 就收在这一个判据里。遮罩预览与复制/保存/贴图三路成品读的都是同一份，不给两侧各写一次判据的机会。
    /// 字色取全局标注色（水印没有自己的颜色键）。
    /// </summary>
    public ScreenCaptureTool.Core.Capture.WatermarkConfig? CurrentWatermark =>
        _watermarkOn && WatermarkOptions.HasContent
            ? WatermarkOptions.BuildConfig(SelectedAnnotationColor.ToArgb())
            : null;

    /// <summary>
    /// 把当前水印连同落点框一起下发给标注层，做遮罩上的实时预览。
    /// 开关、参数改动、换色、选区变动四个来源统统汇到这一个动作——预览"盖不盖、盖成什么样"的判据只此一处。
    /// 关闭时下发的是 null，属性自己短路，不会有额外的重绘。
    /// </summary>
    public void ApplyWatermarkPreview()
    {
        AnnotationLayer.WatermarkRegion = Layer.Region;
        AnnotationLayer.Watermark = CurrentWatermark;
    }

    public float SelectedStrokeThickness => (float)Math.Max(1.0, SliderStrokeThickness.Value);
    public float SelectedEraserDiameter => (float)SliderEraserSize.Value;
    public EraserGestureMode SelectedEraserMode { get; private set; } = EraserGestureMode.Rectangle;

    public BlurMode SelectedBlurMode => _selectedBlurMode;

    public int SelectedBlurUiLevel => BlurIntensityMapper.ClampUiLevel((int)Math.Round(SliderMosaicBlockSize.Value));

    public bool IsAutoMosaicMode => _isAutoMosaicMode;

    public void SyncBlurToolState(BlurMode mode, int uiLevel)
    {
        _suppressBlurSettingsEvent = true;
        try
        {
            _selectedBlurMode = mode;
            SliderMosaicBlockSize.Value = BlurIntensityMapper.ClampUiLevel(uiLevel);
            UpdateMosaicModeVisuals();
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressBlurSettingsEvent = false;
        }
    }

    public float SelectedSpotlightDarkness => (float)Math.Clamp(SliderSpotlightDarkness.Value / 100.0, 0.0, 1.0);

    public SpotlightShapeKind SelectedSpotlightKind => _selectedSpotlightKind;

    public float SelectedSpotlightStrokeThickness => (float)Math.Max(0.0, SliderSpotlightStrokeThickness.Value);

    /// <summary>聚光灯圆角半径（物理像素 0-200）：新画聚光灯的取值；选中图元 / 圆角浮条改值后经
    /// <see cref="SyncSpotlightToolState"/> 回写记忆（滑块与画布手柄均已退役，字段即记忆本体）。</summary>
    public float SelectedSpotlightCornerRadius => (float)Math.Max(0.0, _spotlightCornerRadius);

    /// <summary>矩形圆角半径（物理像素 0-200）：新画矩形的取值；选中图元（<see cref="SyncShapeToolState"/>）
    /// 与圆角浮条改值两条来路都直接回写这个字段（滑块与画布手柄均已退役，字段即记忆本体）。</summary>
    public float SelectedShapeCornerRadius => (float)Math.Max(0.0, _shapeCornerRadius);

    private static double ClampCornerRadius(double radius) => Math.Clamp(radius, 0.0, CornerRadiusMax);

    /// <summary>矩形圆角记忆的公开写口，当前只剩设置恢复（<c>ApplyDefaults</c>）一条来路：选中图元的回显已并入
    /// <see cref="SyncShapeToolState"/>、浮条改值直接写字段。只写字段，不触发任何回灌。</summary>
    public void SyncShapeCornerRadius(double radius) => _shapeCornerRadius = ClampCornerRadius(radius);

    /// <summary>形状是否填充（工具栏勾选态）。取值真源在共享控件 <c>ShapeFillOptions</c>，
    /// 与圆角那一档"字段即记忆本体"同一口径。</summary>
    public bool SelectedShapeFill => ShapeFillOptions.Filled;

    /// <summary>形状填充不透明度 0～1；未勾选＝0＝不填充。新画形状与回灌选中图元都取这里。</summary>
    public float SelectedShapeFillOpacity => ShapeFillOptions.FillOpacity;

    /// <summary>回显填充档（选中图元时把它自己的填充摆回工具栏）：只摆控件，不触发 <see cref="ShapeFillChanged"/>。</summary>
    public void SyncShapeFill(bool filled, double opacityPercent) => ShapeFillOptions.SetState(filled, opacityPercent);

    /// <summary>
    /// 回显<b>选中那颗形状图元</b>自己的样式（CaptureSession 选中变化时调用）：颜色、粗细、形状档、线型、
    /// 圆角、填充六格一并摆回参数条——于是条上读到的就是这一颗，下一笔也沿用它的样式（与圆角那一档同一口径）。
    /// 全程套 <c>_suppressCommonSettingsEvent</c>：这里写的是滑杆与颜色字段，而它们各自挂着向宿主报点的出口，
    /// 不挡住就会"选中一个图元"反手触发一次回灌、在撤销栈里留一条空记录。
    /// </summary>
    public void SyncShapeToolState(RectangleShape rect)
    {
        _suppressCommonSettingsEvent = true;
        try
        {
            SelectedAnnotationColor = rect.StrokeColor;
            SliderStrokeThickness.Value = ClampSliderValue(SliderStrokeThickness, rect.StrokeThickness);
            _selectedShapeKind = rect.ShapeKind;
            _selectedShapeLineStyle = rect.LineStyle;
            _shapeCornerRadius = ClampCornerRadius(rect.CornerRadius);
            ShapeFillOptions.SetState(rect.FillOpacity > 0.0f, rect.FillOpacity * 100.0);
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressCommonSettingsEvent = false;
        }
    }

    /// <summary>回显<b>选中那支箭头</b>自己的样式：颜色、粗细、箭头头型。整体缩放（滚轮那一档）不在参数条上，
    /// 故这里不回灌它。</summary>
    public void SyncArrowToolState(ArrowShape arrow)
    {
        _suppressCommonSettingsEvent = true;
        try
        {
            SelectedAnnotationColor = arrow.Color;
            SliderStrokeThickness.Value = ClampSliderValue(SliderStrokeThickness, arrow.Thickness);
            _selectedArrowStyle = arrow.Style;
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressCommonSettingsEvent = false;
        }
    }

    /// <summary>回显<b>选中那颗序号</b>自己的样式：标注色、大小滑杆、样式小样（外形＋底色）、引线箭头头型。
    /// 「类型」档刻意不动——它作用的是全图所有序号，不是这一颗的私有样式，摆上去会让条上读数与其余序号对不上。</summary>
    public void SyncCounterToolState(CounterShape counter)
    {
        _suppressCommonSettingsEvent = true;
        try
        {
            SelectedAnnotationColor = counter.FillColor;
            SyncCounterRadiusSlider(counter.Radius);
            CounterOptions.SyncStyleFromShape(counter.Glyph, counter.Fill, counter.TailHead);
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressCommonSettingsEvent = false;
        }
    }

    /// <summary>回显<b>选中那条笔迹</b>的颜色——只有颜色这一档能改到已画好的笔迹上（粗细/线型/笔锋属于"这一笔"，
    /// 见 protection.md「标注画布鼠标手势优先级」④），故条上也只摆这一格。</summary>
    public void SyncStrokeToolColor(System.Drawing.Color color)
    {
        SelectedAnnotationColor = color;
        UpdateToolOptionLabels();
    }

    /// <summary>把图元值摆进滑杆：四舍五入到刻度并夹进量程，越界的记忆值不会把滑杆拖出可显示范围。</summary>
    private static double ClampSliderValue(Slider slider, float value)
        => Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);

    public void SyncSpotlightToolState(SpotlightShape shape)
    {
        _suppressSpotlightSettingsEvent = true;
        try
        {
            _selectedSpotlightKind = shape.ShapeKind;
            SelectedAnnotationColor = shape.StrokeColor;
            SliderSpotlightDarkness.Value = Math.Clamp(Math.Round(shape.Darkness * 100.0f), SliderSpotlightDarkness.Minimum, SliderSpotlightDarkness.Maximum);
            SliderSpotlightStrokeThickness.Value = Math.Clamp(Math.Round(shape.StrokeThickness), SliderSpotlightStrokeThickness.Minimum, SliderSpotlightStrokeThickness.Maximum);
            // 圆角一并回显：拖角手柄改出来的半径要变成"下一颗聚光灯的默认值"，这就是"圆角参数记忆"
            // （此前这里不回填、且手势参数里圆角恒为 0，于是每画一颗都退回直角）。滑块已拆除，字段即记忆本体。
            _spotlightCornerRadius = ClampCornerRadius(shape.CornerRadius);
            UpdateSpotlightKindVisuals();
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressSpotlightSettingsEvent = false;
        }
    }

    public void SetSpotlightDarknessPercent(double percent, bool raiseChanged = true)
    {
        if (SliderSpotlightDarkness == null) return;
        double next = Math.Clamp(Math.Round(percent), SliderSpotlightDarkness.Minimum, SliderSpotlightDarkness.Maximum);
        bool changed = Math.Abs(SliderSpotlightDarkness.Value - next) > 0.001;
        _suppressSpotlightSettingsEvent = true;
        try
        {
            SliderSpotlightDarkness.Value = next;
            UpdateToolOptionLabels();
        }
        finally
        {
            _suppressSpotlightSettingsEvent = false;
        }
        if (raiseChanged && changed)
        {
            RaiseSpotlightSettingsChanged();
        }
    }

    private void RaiseSpotlightSettingsChanged()
    {
        if (_suppressSpotlightSettingsEvent) return;
        SpotlightSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取色落位口（<c>ApplyColorToCurrentTarget</c>）专用：颜色是字段直赋、不会自己反手发事件，故无需抑制位。</summary>
    private void RaiseAnnotationColorChanged() => AnnotationColorChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>将粗细参数的用户改动转发给截图会话。</summary>
    private void RaiseAnnotationThicknessChanged()
    {
        if (_suppressCommonSettingsEvent) return;
        AnnotationThicknessChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>序号「大小」报点，与上一条同一抑制位（<see cref="SyncCounterRadiusSlider"/> 摆滑杆时也走 ValueChanged）。</summary>
    private void RaiseCounterRadiusChanged()
    {
        if (_suppressCommonSettingsEvent) return;
        CounterRadiusChanged?.Invoke(this, EventArgs.Empty);
    }

    public float SelectedCounterRadius => (float)Math.Max(8.0, SliderCounterRadius.Value);

    /// <summary>下一个序号的整套样式档（类型 / 外形 / 底色 / 描边 / 字体 / 引线 / 说明字号 / 起始值）。
    /// 真相源只有共享控件那一份，截图侧与贴图侧读的是同一套算式。</summary>
    public CounterStyleSettings SelectedCounterStyle => CounterOptions.Current;

    /// <summary>共享手势参数快照：从现有控件读取真实值，不另建一套默认参数。</summary>
    internal ScreenCaptureTool.Services.AnnotationGestureSettings GetAnnotationGestureSettings(float arrowScale = 1.0f)
    {
        // 圆角一格两用：矩形档取矩形记忆值、聚光灯档取聚光记忆值（两档不会同时生效）。
        float cornerRadius = _activeToolKey == "Rectangle" ? SelectedShapeCornerRadius : SelectedSpotlightCornerRadius;
        return new(SelectedAnnotationColor, SelectedStrokeThickness, SelectedBrushProfile,
            SelectedBrushLineStyle, SelectedArrowStyle, arrowScale, SelectedShapeKind,
            SelectedShapeLineStyle, SelectedBlurMode, SelectedBlurUiLevel,
            SelectedSpotlightDarkness, SelectedSpotlightKind, SelectedSpotlightStrokeThickness,
            cornerRadius, SelectedShapeFillOpacity, SelectedCounterRadius, SelectedCounterStyle,
            SelectedEraserDiameter,
            1f); // 遮罩与选区位图 1:1，画笔速度本就是屏幕像素口径
    }

    /// <summary>参数条上的圆角/描边/阴影/描边色被用户改动。业务侧回读 <see cref="CaptureRadiusParam"/> 、
    /// <see cref="CaptureStrokeParam"/>、<see cref="CaptureShadowChecked"/> 与 <see cref="CaptureStrokeColorArgb"/>。</summary>
    public event EventHandler? CaptureEffectChanged;

    // 参数条的量程。原先由两枚滑杆的 Minimum/Maximum 兼作来源，2026-09-17 滑杆退役后搬成常量——
    // 取值与当时逐位一致，对外行为不变（SelectionEffects.ParamToRadiusPx / ParamToStrokePx 的换算口径同此）。
    private const int CaptureRadiusMin = 0;
    private const int CaptureRadiusMax = 50;
    private const int CaptureStrokeMin = 0;
    private const int CaptureStrokeMax = 20;

    private int _captureRadiusParam;
    private int _captureStrokeParam;

    /// <summary>数值微调状态机（长按浮起 + 拖动步进 + 浮起层定位）。</summary>
    private readonly PillValueFineController _pillFine;
    private readonly PillValueFineController.Slot _radiusSlot;
    private readonly PillValueFineController.Slot _strokeSlot;

    /// <summary>参数条是否处于"拖动选区"的精简态：内容不可命中，只有文案。</summary>
    private bool _pillDragMode;

    /// <summary>选区参数条上的圆角参数档位（0-50 档）。</summary>
    public int CaptureRadiusParam => _captureRadiusParam;

    /// <summary>实际生效的物理像素圆角半径（0-200px）：参数值 × 4。</summary>
    public int CaptureRadiusPx => ScreenCaptureTool.Core.Capture.SelectionEffects.ParamToRadiusPx(CaptureRadiusParam);

    /// <summary>选区参数条上的描边档位（0-20，0 = 不描边）。</summary>
    public int CaptureStrokeParam => _captureStrokeParam;

    /// <summary>实际生效的物理像素描边宽度：档位 1:1，画在内容边界以内。</summary>
    public int CaptureStrokePx => ScreenCaptureTool.Core.Capture.SelectionEffects.ParamToStrokePx(CaptureStrokeParam);

    public bool CaptureShadowChecked => ChkCaptureShadow.IsChecked == true;

    private int _captureStrokeColorArgb = ScreenCaptureTool.Core.Capture.SelectionEffects.DefaultStrokeArgb;

    /// <summary>参数条色块当前选定的描边颜色（ARGB）。取色器改一次即随 <see cref="CaptureEffectChanged"/> 上报。</summary>
    public int CaptureStrokeColorArgb => _captureStrokeColorArgb;

    /// <summary>用记忆值铺一次参数条，不触发 <see cref="CaptureEffectChanged"/>。</summary>
    public void SyncCaptureEffectState(int radiusParam, bool shadow, int strokePx, int strokeArgb, bool suppressStroke = false)
    {
        _suppressCaptureEffectEvent = true;
        try
        {
            _captureRadiusParam = Math.Clamp(radiusParam, CaptureRadiusMin, CaptureRadiusMax);
            _captureStrokeParam = Math.Clamp(strokePx, CaptureStrokeMin, CaptureStrokeMax);
            GroupStroke.IsEnabled = !suppressStroke;
            GroupStroke.Opacity = suppressStroke ? 0.55 : 1;
            ChkCaptureShadow.IsChecked = shadow;
            RefreshCaptureEffectLabels();
            _captureStrokeColorArgb = strokeArgb;
            ApplyStrokeColorSwatch();
        }
        finally
        {
            _suppressCaptureEffectEvent = false;
        }
    }

    /// <summary>
    /// 三个参数的文案统一出口。拖动精简态与常态用的是同一套文案（2026-09-17 合并 DragInfoPill 后），
    /// 所以只有这一个刷新点，不会再出现两处措辞不一致。
    /// </summary>
    private void RefreshCaptureEffectLabels()
    {
        TxtCaptureRadius.Text = _captureRadiusParam.ToString();
        TxtCaptureStroke.Text = _captureStrokeParam.ToString();
        ChkCaptureShadow.Content = CaptureShadowChecked ? "阴影 开" : "阴影 关";
    }

    internal void CaptureStrokeDefaults(AppSettings settings)
    {
        if (!GroupStroke.IsEnabled) return;
        settings.CaptureStrokeWidth = CaptureStrokeParam;
        settings.CaptureStrokeColor = CaptureStrokeColorArgb;
    }

    /// <summary>改圆角档位：夹量程 → 刷数值 → 上报。拖动、滚轮、记忆值三条路都汇到这里。</summary>
    private void SetCaptureRadiusParam(int value)
    {
        int v = Math.Clamp(value, CaptureRadiusMin, CaptureRadiusMax);
        if (v == _captureRadiusParam) return;
        _captureRadiusParam = v;
        TxtCaptureRadius.Text = v.ToString();
        RaiseCaptureEffectChanged();
    }

    /// <summary>改描边档位。与 <see cref="SetCaptureRadiusParam"/> 同构。</summary>
    private void SetCaptureStrokeParam(int value)
    {
        int v = Math.Clamp(value, CaptureStrokeMin, CaptureStrokeMax);
        if (v == _captureStrokeParam) return;
        _captureStrokeParam = v;
        TxtCaptureStroke.Text = v.ToString();
        RaiseCaptureEffectChanged();
    }

    /// <summary>色块底色跟着记忆/新选的颜色走（参数条底色恒为不透明黑，故只刷填充、不碰主题资源）。</summary>
    private void ApplyStrokeColorSwatch() => StrokeColorSwatch.Background = new SolidColorBrush(ArgbToColor(_captureStrokeColorArgb));

    /// <summary>ARGB 整数 → Avalonia 颜色。设置项与成品侧都用 int，只有界面这一端需要拆分量。</summary>
    private static Color ArgbToColor(int argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private async void OnStrokeColorClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Color? picked = await ColorPickerWindow.PickAsync(this, ArgbToColor(_captureStrokeColorArgb), BtnStrokeColor);
        if (!picked.HasValue) return;

        Color c = picked.Value;
        _captureStrokeColorArgb = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
        ApplyStrokeColorSwatch();
        RaiseCaptureEffectChanged();
    }

    private void RaiseCaptureEffectChanged()
    {
        if (_suppressCaptureEffectEvent) return;
        CaptureEffectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>描边/阴影预览的悬浮闸门被打开或关闭。业务侧回读 <see cref="CaptureEffectHover"/> 决定是否把
    /// 这两样推进绘制层——与 <see cref="CaptureEffectChanged"/> 分开，因为那条事件的含义是"设置值变了要落盘"，
    /// 悬浮不该写进设置。</summary>
    public event EventHandler? CaptureEffectHoverChanged;

    private bool _captureEffectHover;

    /// <summary>鼠标是否正停在参数条的描边/阴影控件上（或刚从它们上面松手拖拽）。</summary>
    public bool CaptureEffectHover => _captureEffectHover;

    private void SetCaptureEffectHover(bool value)
    {
        if (_captureEffectHover == value) return;
        _captureEffectHover = value;
        CaptureEffectHoverChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEffectGroupEntered(object? sender, PointerEventArgs e) => SetCaptureEffectHover(true);

    private void OnEffectGroupExited(object? sender, PointerEventArgs e) => SetCaptureEffectHover(false);

    /// <summary>
    /// 滑杆拖拽松手：拖出参数条以外时不会有任何 PointerExited（拖拽期间指针被滑杆捕获，进出判定是停的），
    /// 故在这里按指针当前位置补一次判定，否则预览会一直挂在选区上变成"常驻"。
    /// </summary>
    private void OnInfoPillCaptureLost(object? sender, RoutedEventArgs e)
    {
        if (e is not PointerEventArgs pe) return;
        Point p = pe.GetPosition(InfoPill);
        SetCaptureEffectHover(InfoPill.Bounds.Contains(p));
    }

    /// <summary>
    /// 参数条数值的滚轮微调：每格 1 档。走控制器的 <see cref="PillValueFineController.Bump"/> 而不是直接改字段
    /// ——微调态正开着时它还要顺带刷新浮起数字并顺延收回计时，两处各写一份必然会漏。
    /// <c>e.Handled</c> 必须置上：窗口的滚轮缩放选区虽然对本控件所在浮层做了豁免，
    /// 但豁免判据是命中白名单，滚轮事件继续冒泡上去仍可能被别处的滚轮逻辑接走。
    /// </summary>
    private void OnCaptureRadiusWheel(object? sender, PointerWheelEventArgs e)
    {
        _pillFine.Bump(_radiusSlot, e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnCaptureStrokeWheel(object? sender, PointerWheelEventArgs e)
    {
        _pillFine.Bump(_strokeSlot, e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    /// <summary>同步序号大小滑块的数值（供 CaptureSession 滚轮/选中时调用）。</summary>
    public void SyncCounterRadiusSlider(float radius)
    {
        if (SliderCounterRadius == null) return;
        double clamped = Math.Clamp(radius, SliderCounterRadius.Minimum, SliderCounterRadius.Maximum);
        // 抑制位：滑杆现已挂着"改大小 → 落进选中那颗序号"的报点出口，摆控件这一路不能再反手发一次。
        // ⚠ 存/还原而非直接清位——本方法也会被 SyncCounterToolState 在抑制已生效时嵌套调用。
        bool priorSuppressed = _suppressCommonSettingsEvent;
        _suppressCommonSettingsEvent = true;
        try
        {
            SliderCounterRadius.Value = clamped;
        }
        finally
        {
            _suppressCommonSettingsEvent = priorSuppressed;
        }
        if (TxtCounterRadius != null) TxtCounterRadius.Text = Math.Round(clamped).ToString();
    }

    /// <summary>当前 DPI 缩放，<see cref="DesktopScaling"/> 的容错版本。</summary>
    public double SafeScaling
    {
        get
        {
            double s = DesktopScaling;
            return s > 0 ? s : 1.0;
        }
    }

    /// <summary>
    /// 在 Show() 之前预置窗口的虚拟屏幕物理位置与 DIP 尺寸，
    /// 消除窗口默认尺寸弹出后在 Opened 触发的二次布局与重排闪烁。
    /// </summary>
    public void PreLayoutVirtualScreen(System.Drawing.Rectangle virt, double dpiScale)
    {
        double scale = dpiScale > 0 ? dpiScale : SafeScaling;
        Position = new PixelPoint(virt.X, virt.Y);
        Width = virt.Width / scale;
        Height = virt.Height / scale;
        EvaluateShortcutHintVisibility();
    }

    /// <summary>
    /// 屏幕物理像素矩形 → 窗口 DIP 矩形（用于把 AutoDetectController 的高亮区给 SelectionLayer）。
    /// </summary>
    public Rect ScreenRectToWindowDip(System.Drawing.Rectangle screenRect)
    {
        double s = SafeScaling;
        double x = (screenRect.X - Position.X) / s;
        double y = (screenRect.Y - Position.Y) / s;
        double w = screenRect.Width / s;
        double h = screenRect.Height / s;
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 窗口 DIP 点 → 屏幕物理像素点（用于把鼠标事件位置喂给 AutoDetectController）。
    /// </summary>
    public System.Drawing.Point WindowDipToScreenPoint(Point windowDip)
    {
        double s = SafeScaling;
        int x = (int)Math.Round(windowDip.X * s + Position.X);
        int y = (int)Math.Round(windowDip.Y * s + Position.Y);
        return new System.Drawing.Point(x, y);
    }

    public bool IsOverlayHit(object? source)
    {
        if (source is not Visual visual) return false;
        return IsOverlayVisual(visual) || visual.GetVisualAncestors().Any(IsOverlayVisual);
    }

    /// <summary>
    /// **滚轮专用**的覆盖层豁免判据。与 <see cref="IsOverlayHit"/> 只差一处：参数条里的「尺寸」读数
    /// （<see cref="TxtSelectionSize"/>）不算覆盖层。
    ///
    /// 缘由：该读数是只读的、自身没有任何滚轮处理，而 `CaptureSession` 的窗口级滚轮处理器开头就是
    /// `if (window.IsOverlayHit(e.Source)) return;` ⇒ 鼠标停在尺寸上滚轮**完全没反应**。
    /// 用户口径是"鼠标停在这里也能滚轮缩放选区尺寸，跟窗口其余区域一样"，故对滚轮放行，
    /// 让事件继续冒泡到那个处理器的第 (f) 段「滚轮缩放选区」（每格四边各扩/缩 1px）。
    ///
    /// ⚠ **不能靠改 <see cref="IsOverlayVisual"/> 来达到这个目的**：那个判据同时被点击路径使用
    /// （"落在覆盖层上不起新框"的排除判据），放行会让"点尺寸"被当成"起新框"。
    /// </summary>
    public bool IsOverlayHitForWheel(object? source)
    {
        if (source is not Visual visual) return false;
        if (IsSelectionSizeReadout(visual)) return false;
        return IsOverlayVisual(visual) || visual.GetVisualAncestors().Any(IsOverlayVisual);
    }

    /// <summary>
    /// 是否落在参数条「尺寸」读数（或其子级）上。向上找时以 <see cref="InfoPill"/> 为界，
    /// 免得"滚轮落在参数条空白处"也被当成尺寸。
    /// </summary>
    private bool IsSelectionSizeReadout(Visual visual)
    {
        if (ReferenceEquals(visual, TxtSelectionSize)) return true;
        return visual.GetVisualAncestors()
            .TakeWhile(a => !ReferenceEquals(a, InfoPill))
            .Any(a => ReferenceEquals(a, TxtSelectionSize));
    }

    private bool IsOverlayVisual(Visual visual)
    {
        return ReferenceEquals(visual, SelectionModeBar)
            || ReferenceEquals(visual, ActionBar)
            || ReferenceEquals(visual, ToolOptionsBar)
            || ReferenceEquals(visual, BrushLineStyleMenu)
            || ReferenceEquals(visual, BrushAdvancedMenu)
            || ReferenceEquals(visual, InfoPill)
            // 圆角浮条与参数条同理：它的不透明底色必须进命中白名单，否则按住数值拖动微调时
            // 指针一离手就会被判成"在画布上按下"、另起一个选区。
            || ReferenceEquals(visual, SpotlightRadiusPillHost)
            // 序号浮条同理（也带浮起层的可绘制区，故覆盖区判定见 IsOverCounterPill）
            || ReferenceEquals(visual, CounterPillHost);
    }

    public bool IsOverlayPoint(Point windowDip)
    {
        return ContainsWindowPoint(SelectionModeBar, windowDip)
            || ContainsWindowPoint(ActionBar, windowDip)
            || ContainsWindowPoint(ToolOptionsBar, windowDip)
            || ContainsWindowPoint(BrushLineStyleMenu, windowDip)
            || ContainsWindowPoint(BrushAdvancedMenu, windowDip)
            || ContainsWindowPoint(InfoPill, windowDip)
            || IsOverSpotlightRadiusPill(windowDip)
            || IsOverCounterPill(windowDip);
    }

    /// <summary>
    /// 指针是否落在序号浮条的**可见黑条**上。与 <see cref="IsOverSpotlightRadiusPill"/> 同一条理由：
    /// 本控件的可绘制区比黑条宽（给浮起层那 5 档刻度留的余量，最少 260），照控件矩形判会把黑条两侧的
    /// 透明带也算成覆盖层 ⇒ 在那里点画布被无声吞掉。事件源那条路径（<see cref="IsOverlayVisual"/>）
    /// 本来不受影响（透明带无背景、不参与命中），两道路径因此口径一致。
    /// </summary>
    private bool IsOverCounterPill(Point windowDip)
    {
        if (CounterPillHost == null || !CounterPillHost.IsVisible) return false;
        Point? local = this.TranslatePoint(windowDip, CounterPillHost);
        return local.HasValue && CounterPillHost.PillBounds.Contains(local.Value);
    }

    /// <summary>
    /// 指针是否落在圆角浮条的**可见黑条**上（不是控件矩形，见下）。
    ///
    /// <para>⚠ 不能拿控件的矩形直接判：浮条控件的可绘制区被刻意做得比黑条宽（约 210 vs 66），
    /// 为的是让浮起层那 5 档刻度有地方横向展开、不被裁掉（见 <c>SpotlightRadiusPill.axaml</c>）。
    /// 照控件矩形判，黑条左右各约 70px 的**透明带**也会算作覆盖层 ⇒ 在那里点画布会被无声吞掉。</para>
    ///
    /// <para>点击路径另有一道 <see cref="IsOverlayHit"/>（按视觉树判事件源）：透明带没有背景、
    /// 不参与命中测试，事件源会落到下层元素上，本来就判不出覆盖层——两道路径口径因此一致。</para>
    /// </summary>
    private bool IsOverSpotlightRadiusPill(Point windowDip)
    {
        if (SpotlightRadiusPillHost == null || !SpotlightRadiusPillHost.IsVisible) return false;
        Point? local = this.TranslatePoint(windowDip, SpotlightRadiusPillHost);
        return local.HasValue && SpotlightRadiusPillHost.PillBounds.Contains(local.Value);
    }

    private bool ContainsWindowPoint(Control control, Point windowDip)
    {
        if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        Point? topLeft = control.TranslatePoint(new Point(0, 0), this);
        if (!topLeft.HasValue) return false;
        return new Rect(topLeft.Value, control.Bounds.Size).Contains(windowDip);
    }

    public void UpdateAnnotationTransform()
    {
        AnnotationLayer.ScreenOrigin = Position;
        AnnotationLayer.ScreenToDipScale = 1.0 / SafeScaling;
        AnnotationLayer.InvalidateVisual();
    }

    public void PrepareForLongScrollTransition()
    {
        SetActiveTool(null);
        AnnotationLayer.ClearPreviewShape();
        ActionBar.IsVisible = false;
        ToolOptionsBar.IsVisible = false;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
        CollapseToolbarGroup(false);
        CloseBrushMenus();
        _arrowStyleMenu.HideIfOpen();
    }

    internal event EventHandler? ActiveToolChanged;

    public void SetActiveTool(string? toolKey)
    {
        ClearActiveTool(BtnBrush);
        ClearActiveTool(BtnArrow);
        ClearActiveTool(BtnRectangle);
        ClearActiveTool(BtnText);
        ClearActiveTool(BtnMosaic);
        ClearActiveTool(BtnSpotlight);
        ClearActiveTool(BtnCounter);
        ClearActiveTool(BtnEraser);

        Button? active = toolKey switch
        {
            "Brush" => BtnBrush,
            "Arrow" => BtnArrow,
            "Rectangle" => BtnRectangle,
            "Text" => BtnText,
            "Mosaic" => BtnMosaic,
            "Spotlight" => BtnSpotlight,
            "Counter" => BtnCounter,
            "Eraser" => BtnEraser,
            _ => null,
        };

        if (active != null && !active.Classes.Contains("active"))
        {
            active.Classes.Add("active");
        }

        _activeToolKey = toolKey;
        ActiveToolChanged?.Invoke(this, EventArgs.Empty);
        // 录屏格的灯是"参数条归属"状态灯（稿 1：激活底与 hover 同色、不吃紫环，故用私有 .recOn 而非 .active）；
        // 与水印那盏跨工具常驻的开关灯不同——切去任何别的工具它就灭，录屏没有留在身后的生效态。
        if (toolKey == "Record") BtnRecord.Classes.Add("recOn");
        else BtnRecord.Classes.Remove("recOn");
        ShowToolOptions(toolKey);
        // 快捷键 / 脚本 / Ctrl 点选图元命中组内工具的反馈：
        // 带参数栏的工具 ⇒ 参数栏就是第二栏（互斥规则），不再摊组；无参数栏的工具（如 OCR）⇒ 摊开该组亮出"手里是哪枚"
        if (!string.IsNullOrEmpty(toolKey) && !HasToolOptions(toolKey) && toolKey != "Eraser") RevealToolbarGroupOf(toolKey!);
        if (_shortcutHintState != ShortcutHintState.DraggingNew)
        {
            SetShortcutHintState(!string.IsNullOrEmpty(toolKey) ? ShortcutHintState.ToolActive : (_lastHasSelection ? ShortcutHintState.SelectionConfirmed : ShortcutHintState.IdleElementHover));
        }
    }

    private static void ClearActiveTool(Button button)
    {
        button.Classes.Remove("active");
    }

    /// <summary>该工具是否带参数栏（参数栏与组成员栏互斥，同屏最多两条栏的判据统一走这里）。
    /// 水印也在列内——它不落笔，但确实占一条参数条，"有副栏就别再摊组"这条规则对它同样成立。
    /// 录屏同理：两行参数条（稿 1 D02），不落笔但占一条副栏。</summary>
    private static bool HasToolOptions(string? toolKey) =>
        toolKey is "Brush" or "Arrow" or "Rectangle" or "Text" or "Mosaic" or "Spotlight" or "Counter" or "Watermark" or "Record" or "Eraser";

    private void ShowToolOptions(string? toolKey)
    {
        bool wasTextOptionsVisible = Text.TextPanel?.IsVisible == true;

        BrushOptionsPanel.IsVisible = false;
        ArrowOptionsPanel.IsVisible = false;
        RectangleOptionsPanel.IsVisible = false;
        if (Text.TextPanel != null) Text.TextPanel.IsVisible = false;
        MosaicOptionsPanel.IsVisible = false;
        SpotlightOptionsPanel.IsVisible = false;
        CounterOptionsPanel.IsVisible = false;
        WatermarkOptionsPanel.IsVisible = false;
        RecordOptionsPanel.IsVisible = false;
        CommonThicknessPanel.IsVisible = false;
        EraserOptionsPanel.IsVisible = false;
        CommonColorPanel.IsVisible = false;
        SepAfterStyle.IsVisible = false;
        SepAfterSize.IsVisible = false;
        SepBeforeClear.IsVisible = false;
        BtnClearCurrentTool.IsVisible = false;

        CloseBrushMenus();
        _arrowStyleMenu.HideIfOpen();

        if (toolKey != "Text" && wasTextOptionsVisible)
            Text.CloseTextPopups();
        // 「样式」面板与上面两枚文本 Popup 同为"点击才弹"的浮层：切走序号还把它支在屏幕上，
        // 就是设计稿点名要避免的那种常驻浮窗。
        if (toolKey != "Counter") _counterOptions?.CloseStylePopup();
        // 同理：水印的下挂「大小／间距」面板与位置八档菜单也是点击才出的浮层，切走即收。
        if (toolKey != "Watermark") _watermarkOptions?.ClosePopups();
        // 录屏参数条那两枚图标钮（声音／键鼠操作）下挂的也是点击才出的 Popup，切走即收。
        if (toolKey != "Record") _recordOptions?.ClosePopups();

        bool hasOptions = HasToolOptions(toolKey);
        bool showOptions = hasOptions && ActionBar.IsVisible;
        // 🔴 同屏最多两条栏（主栏 + 一条副栏）：参数栏要出现，组成员栏就得先收（与 ExpandToolbarGroup 互为镜像）
        if (showOptions) CollapseToolbarGroup(false);
        ToolOptionsBar.IsVisible = showOptions;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = showOptions;

        if (toolKey == "Brush")
        {
            BrushOptionsPanel.IsVisible = true;
            CommonThicknessPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有画笔");
        }
        else if (toolKey == "Eraser")
        {
            EraserOptionsPanel.IsVisible = true;
        }
        else if (toolKey == "Arrow")
        {
            ArrowOptionsPanel.IsVisible = true;
            CommonThicknessPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有箭头");
        }
        else if (toolKey == "Rectangle")
        {
            RectangleOptionsPanel.IsVisible = true;
            CommonThicknessPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有形状");
        }
        else if (toolKey == "Text")
        {
            Text.ForegroundTextColor = Color.FromArgb(SelectedAnnotationColor.A, SelectedAnnotationColor.R, SelectedAnnotationColor.G, SelectedAnnotationColor.B);
            Text.ColorTargetMode = 0;
            if (Text.TextPanel != null) Text.TextPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            Text.UpdateTextPanelUi();
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有文本");
        }
        else if (toolKey == "Mosaic")
        {
            MosaicOptionsPanel.IsVisible = true;
            UpdateMosaicModeVisuals();
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有马赛克/模糊效果");
        }
        else if (toolKey == "Spotlight")
        {
            SpotlightOptionsPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有聚光灯");
        }
        else if (toolKey == "Counter")
        {
            _ = CounterOptions;
            CounterOptionsPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
            ToolTip.SetTip(BtnClearCurrentTool, "清除所有序号");
        }
        else if (toolKey == "Watermark")
        {
            _ = WatermarkOptions;
            // 条末那八枚是参数条共用的标注色（设计稿乙条的末段），水印不另开颜色键，故直接复用这一段。
            WatermarkOptionsPanel.IsVisible = true;
            CommonColorPanel.IsVisible = true;
        }
        else if (toolKey == "Record")
        {
            // 录屏参数条自带全部两行内容，公共色/粗细/清除钮一概让位（稿 1：条上没有标注色那一段）。
            // 默认档只在本会话首次弹出时铺一次（读"上次使用"、没记过才回落设置页默认值）——收起再点开
            // 保留用户刚改的值；各项改动自 2026-09-24 起选择即落盘进"上次使用"族，不再需要按「开始录制」才记住。
            RecordOptionsPanel.IsVisible = true;
            if (!_recordDefaultsApplied)
            {
                RecordOptions.ApplyDefaults();
                _recordDefaultsApplied = true;
            }
        }

        UpdateSpotlightKindVisuals();

        bool hasToolSpecific = BrushOptionsPanel.IsVisible || ArrowOptionsPanel.IsVisible || RectangleOptionsPanel.IsVisible || (Text.TextPanel?.IsVisible ?? false) || MosaicOptionsPanel.IsVisible || SpotlightOptionsPanel.IsVisible || CounterOptionsPanel.IsVisible || WatermarkOptionsPanel.IsVisible || RecordOptionsPanel.IsVisible || EraserOptionsPanel.IsVisible;
        SepAfterStyle.IsVisible = hasToolSpecific && (CommonThicknessPanel.IsVisible || CommonColorPanel.IsVisible);
        SepAfterSize.IsVisible = CommonThicknessPanel.IsVisible && CommonColorPanel.IsVisible;
        // 水印不落笔、不进撤销栈，压根没有"当前工具的标注"可清，故那枚清除钮与它前面的分隔线一起让位；录屏同理。
        bool showClear = hasOptions && toolKey != "Watermark" && toolKey != "Record" && toolKey != "Eraser";
        SepBeforeClear.IsVisible = showClear;
        BtnClearCurrentTool.IsVisible = showClear;

        UpdateToolOptionLabels();
        LayoutOverlayStack();
    }

    private System.Drawing.Color GetCurrentTargetColor()
    {
        // 文本语境（文本工具激活或已选中某个文本层）：三色目标状态归文本共享实现所有
        if (Text.TryGetTextTargetColor(out System.Drawing.Color textTarget)) return textTarget;

        return SelectedAnnotationColor;
    }

    private async void OnSharedCustomColorRequested(Control anchor)
    {
        System.Drawing.Color current = GetCurrentTargetColor();
        Color? picked = await ColorPickerWindow.PickAsync(this, Color.FromArgb(current.A, current.R, current.G, current.B), anchor);
        if (picked.HasValue)
        {
            Color c = picked.Value;
            ApplyColorToCurrentTarget(System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B));
        }
    }

    private void OnBrushLineStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BrushAdvancedMenu.IsVisible = false;
        ToggleMenuNearButton(BrushLineStyleMenu, BtnBrushLineStyle);
    }

    private void OnBrushAdvancedClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BrushLineStyleMenu.IsVisible = false;
        ToggleMenuNearButton(BrushAdvancedMenu, BtnBrushAdvanced);
    }

    /// <summary>
    /// 由参数栏当前取值重算笔刷画像：透明度常显，笔锋强度/平滑/最细取自带层滑杆。
    /// 硬度固定为实心抗锯齿，笔锋驱动统一为速度驱动（强度为 0 时即等宽无笔锋）。
    /// 画像是"下一条笔迹"的参数，不回改已画好的笔迹（与画笔线型同一口径）。
    /// </summary>
    private void RebuildBrushProfile()
    {
        _selectedBrushProfile = new BrushProfile(
            (float)(SliderBrushOpacity.Value / 100.0),
            (float)(SliderBrushTaper.Value / 100.0),
            (float)(SliderBrushSmoothing.Value / 100.0),
            (float)(SliderBrushMinWidth.Value / 100.0));
        UpdateToolOptionLabels();
    }

    private void OnBrushSolidItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedBrushLineStyle = AnnotationLineStyle.Solid;
        BrushLineStyleMenu.IsVisible = false;
        UpdateStyleOptionVisuals();
    }

    private void OnBrushDashItemClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedBrushLineStyle = AnnotationLineStyle.DashLarge;
        BrushLineStyleMenu.IsVisible = false;
        UpdateStyleOptionVisuals();
    }

    /// <summary>收起笔迹的两枚菜单（线型／笔锋）。散落各处的"成对两行"收这一个出口。</summary>
    private void CloseBrushMenus()
    {
        BrushLineStyleMenu.IsVisible = false;
        BrushAdvancedMenu.IsVisible = false;
    }

    /// <summary>窗口根 Tunnel 按下：落在两枚菜单之外就收起（它们不是 Popup，框架不会自动关）。
    /// 只收起、不吞事件——吞掉会改动画布起笔语义。</summary>
    private void OnBrushMenusDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!BrushLineStyleMenu.IsVisible && !BrushAdvancedMenu.IsVisible) return;
        if (e.Source is not Visual source) return;
        if (IsBrushMenuOrToggle(source)) return;
        CloseBrushMenus();
    }

    /// <summary>
    /// 该可视元素是否落在两枚菜单本体、或那两枚开关钮（含其子级）上。
    /// 🔴 开关钮必须算"自己人"：否则按下去的这一笔会先把菜单关掉，紧接着的 Click 事件里
    /// <see cref="ToggleMenuNearButton"/> 读到的 <c>IsVisible</c> 已是 false ⇒ 判定"该显示"又把菜单弹回来，
    /// 表现为"那枚按钮点两下也关不掉菜单"。
    /// </summary>
    private bool IsBrushMenuOrToggle(Visual visual)
    {
        return ReferenceEquals(visual, BrushLineStyleMenu) || ReferenceEquals(visual, BrushAdvancedMenu)
            || ReferenceEquals(visual, BtnBrushLineStyle) || ReferenceEquals(visual, BtnBrushAdvanced)
            || visual.GetVisualAncestors().Any(a =>
                ReferenceEquals(a, BrushLineStyleMenu) || ReferenceEquals(a, BrushAdvancedMenu)
                || ReferenceEquals(a, BtnBrushLineStyle) || ReferenceEquals(a, BtnBrushAdvanced));
    }

    private void ToggleMenuNearButton(Control menu, Control anchor)
    {
        bool show = !menu.IsVisible;
        CloseBrushMenus();
        if (!show) return;

        menu.IsVisible = true;
        // 刚翻完可见性就要用宽度换算锚点与上下翻转 ⇒ 必须读到本帧尺寸
        Size menuSize = LayoutMetrics.MeasureNow(menu);
        Point? pt = anchor.TranslatePoint(new Point(anchor.Bounds.Width - menuSize.Width, anchor.Bounds.Height + 4), OverlayCanvas);
        if (!pt.HasValue)
        {
            pt = new Point(Canvas.GetLeft(ToolOptionsBar), Canvas.GetTop(ToolOptionsBar) + ToolOptionsBar.Bounds.Height + 4);
        }

        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double left = Clamp(pt.Value.X, OverlayMargin, Math.Max(OverlayMargin, screenW - menuSize.Width - OverlayMargin));
        double top = pt.Value.Y;
        if (top + menuSize.Height + OverlayMargin > screenH)
        {
            Point? above = anchor.TranslatePoint(new Point(anchor.Bounds.Width - menuSize.Width, -menuSize.Height - 4), OverlayCanvas);
            if (above.HasValue) top = above.Value.Y;
        }
        top = Clamp(top, OverlayMargin, Math.Max(OverlayMargin, screenH - menuSize.Height - OverlayMargin));
        Canvas.SetLeft(menu, left);
        Canvas.SetTop(menu, top);
    }

    private void OnShapeKindClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedShapeKind = _selectedShapeKind == ShapeAnnotationKind.Rectangle ? ShapeAnnotationKind.Ellipse : ShapeAnnotationKind.Rectangle;
        UpdateStyleOptionVisuals();
        // 只报形状档这一档：与线型那枚分开走，换形状时才不会顺手把选中图元的虚线改回实线
        ShapeKindChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnShapeLineStyleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _selectedShapeLineStyle = _selectedShapeLineStyle == AnnotationLineStyle.Solid ? AnnotationLineStyle.DashLarge : AnnotationLineStyle.Solid;
        UpdateStyleOptionVisuals();
        // 本条通路即用户点名的缺陷："已经画好的形状点虚线/实线切换不了"——此前这里只写记忆字段，从不回灌图元
        ShapeLineStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ToggleActive(Button button)
    {
        if (button.Classes.Contains("active")) button.Classes.Remove("active");
        else button.Classes.Add("active");
    }

    private IBrush GetCurrentTargetBrush()
    {
        System.Drawing.Color c = GetCurrentTargetColor();
        return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private void ApplyColorToCurrentTarget(System.Drawing.Color color)
    {
        if (Text.TryApplyColorToTextTarget(color)) return;

        // 所有非文本工具（画笔/箭头/矩形/序号/聚光灯）统一设置 SelectedAnnotationColor
        SelectedAnnotationColor = color;
        RaiseAnnotationColorChanged();
        UpdateToolOptionLabels();
        ApplyWatermarkPreview();   // 水印吃的就是这一枚颜色：不重下发的话"改了色，预览还是旧的"
        if (_activeToolKey == "Spotlight")
        {
            RaiseSpotlightSettingsChanged();
        }
    }

    private void OnMosaicManualClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideAutoMosaicScanAnimation(true);
        SetMosaicAutoMode(false);
        ManualMosaicRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMosaicAutoClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetMosaicAutoMode(true);
        AutoMosaicRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSharedBlurModeChanged(object? sender, EventArgs e)
    {
        _selectedBlurMode = BlurIntensityOptions.Mode;
        UpdateMosaicModeVisuals();
        BlurSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutoMosaicBusy(bool busy)
    {
        if (BtnMosaicAuto == null || TxtMosaicAuto == null) return;
        BtnMosaicAuto.IsEnabled = !busy;
        TxtMosaicAuto.Text = busy ? "识别中" : "自动";
        if (busy) ShowAutoMosaicScanAnimation();
        else HideAutoMosaicScanAnimation();
    }

    private void ShowAutoMosaicScanAnimation()
    {
        if (!_lastHasSelection || _lastRegionDip.Width <= 0 || _lastRegionDip.Height <= 0) return;

        _isAutoMosaicScanning = true;
        _autoMosaicScanHeight = _lastRegionDip.Height;
        _autoMosaicScanStartedTick = Environment.TickCount64;
        AutoMosaicScanOverlay.Width = _lastRegionDip.Width;
        AutoMosaicScanOverlay.Height = _lastRegionDip.Height;
        AutoMosaicScanFill.Width = _lastRegionDip.Width;
        AutoMosaicScanFill.Height = _lastRegionDip.Height;
        AutoMosaicScanFill.RenderTransform = _autoMosaicScanTransform;
        AutoMosaicScanOverlay.Opacity = 1;
        AutoMosaicScanOverlay.IsVisible = true;
        _autoMosaicScanTransform.Y = -_autoMosaicScanHeight;
        Canvas.SetLeft(AutoMosaicScanOverlay, _lastRegionDip.X);
        Canvas.SetTop(AutoMosaicScanOverlay, _lastRegionDip.Y);
        AutoMosaicScanOverlay.ZIndex = 900;
        EnsureAutoMosaicScanTimer();
        _autoMosaicScanTimer?.Start();
    }

    private void EnsureAutoMosaicScanTimer()
    {
        if (_autoMosaicScanTimer != null) return;
        _autoMosaicScanTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _autoMosaicScanTimer.Tick += (_, _) =>
        {
            if (!_isAutoMosaicScanning || !AutoMosaicScanOverlay.IsVisible)
            {
                _autoMosaicScanTimer.Stop();
                return;
            }

            double elapsed = Math.Max(0, Environment.TickCount64 - _autoMosaicScanStartedTick);
            double progress = Math.Clamp(elapsed / 800.0, 0.0, 1.0);
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            _autoMosaicScanTransform.Y = -_autoMosaicScanHeight * (1.0 - eased);
            if (progress >= 1.0)
            {
                _autoMosaicScanTransform.Y = 0;
                _autoMosaicScanTimer.Stop();
            }
        };
    }

    private async void HideAutoMosaicScanAnimation(bool immediate = false)
    {
        _isAutoMosaicScanning = false;
        _autoMosaicScanTimer?.Stop();
        if (AutoMosaicScanOverlay == null || !AutoMosaicScanOverlay.IsVisible) return;

        if (immediate)
        {
            AutoMosaicScanOverlay.IsVisible = false;
            AutoMosaicScanOverlay.Opacity = 1;
            _autoMosaicScanTransform.Y = 0;
            return;
        }

        double startOpacity = AutoMosaicScanOverlay.Opacity;
        long fadeStarted = Environment.TickCount64;
        while (!_isAutoMosaicScanning && AutoMosaicScanOverlay.IsVisible)
        {
            double progress = Math.Clamp((Environment.TickCount64 - fadeStarted) / 200.0, 0.0, 1.0);
            AutoMosaicScanOverlay.Opacity = startOpacity * (1.0 - progress);
            if (progress >= 1.0) break;
            await Task.Delay(16);
        }

        if (!_isAutoMosaicScanning)
        {
            AutoMosaicScanOverlay.IsVisible = false;
            AutoMosaicScanOverlay.Opacity = 1;
            _autoMosaicScanTransform.Y = 0;
        }
    }

    private void SetMosaicAutoMode(bool isAuto)
    {
        _isAutoMosaicMode = isAuto;
        UpdateMosaicModeVisuals();
    }

    private void UpdateMosaicModeVisuals()
    {
        if (BtnMosaicManual == null || BtnMosaicAuto == null || BlurIntensityOptions == null) return;

        if (_isAutoMosaicMode)
        {
            BtnMosaicAuto.Classes.Add("active");
            BtnMosaicManual.Classes.Remove("active");
        }
        else
        {
            BtnMosaicManual.Classes.Add("active");
            BtnMosaicAuto.Classes.Remove("active");
        }

        BlurIntensityOptions.SyncMode(_selectedBlurMode);
    }

    private void OnSharedSpotlightKindChanged(object? sender, EventArgs e)
    {
        _selectedSpotlightKind = SharedSpotlightOptions.Kind;
        UpdateSpotlightKindVisuals();
        RaiseSpotlightSettingsChanged();
    }

    private void UpdateSpotlightKindVisuals()
    {
        SharedSpotlightOptions?.SyncKind(_selectedSpotlightKind);
    }

    private void OnSharedSpotlightStrokeAdjusted(object? sender, EventArgs e)
    {
        UpdateToolOptionLabels();
        RaiseSpotlightSettingsChanged();
    }

    private void OnClearCurrentToolClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearCurrentToolRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ForceTopmostSafely(string reason)
    {
        try
        {
            // 延迟回调不能把已隐藏的遮罩或正在为模态对话框让路的窗口重新提升。
            if (_preparingFirstCapture || !IsVisible || !Topmost || !IsEnabled) return;
            IntPtr hwnd = GetWindowHwnd();
            if (hwnd == IntPtr.Zero) return;

            if (!WindowHelper.ForceTopmost(hwnd))
            {
                AppLogger.Warn("CaptureWindow.ForceTopmostSafely: 置顶失败，reason=" + reason);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.ForceTopmostSafely: 置顶异常，reason=" + reason + "，" + ex.Message);
        }
    }

    private void OnBrushClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        BrushRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnArrowClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ArrowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRectangleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RectangleRequested?.Invoke(this, EventArgs.Empty);
    }

    // ==========================================
    // 文本工具选项按钮事件将在 Phase 2 重建
    // ==========================================

    private void OnMosaicClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        MosaicRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpotlightClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SpotlightRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCounterClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CounterRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 水印格：那圈紫环是<b>开关状态灯</b>而不是"当前工具"标记（故 <c>SetActiveTool</c> 既不清它也不管它，
    /// 切去画笔时紫环照旧留着，水印依然生效）。点击刻意留了个两段式——已开启但参数条被别的工具抢走时，
    /// 这一枚只把条切回水印（水印仍开着），再点一次才真关。
    /// 与其余工具按钮同一分工：本窗口只翻开关，参数条归谁、手里那支笔怎么收，由宿主在收到事件后定。
    /// </summary>
    private void OnWatermarkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestWatermark();
    }

    // —— 工具栏点击与快捷键共用的出口（2026-09-29）——
    // 这六枚"动作类"按钮此前只有鼠标一条路，键盘要触发就只能把逻辑再抄一份到 CaptureSession。
    // 现在点按钮与按快捷键走的是同一个方法，宿主仍只收同一个事件（一份实现）。
    // ⚠ 可用性判定留在这里、不下放到调用方：按钮被隐藏或禁用时（选区还没成形、工具栏收起），
    //    键盘不该触发一枚屏幕上根本点不到的按钮。

    /// <summary>按钮可见且启用才发信号；返回是否真的触发了（给快捷键分发用，未触发就不吃掉这次按键）。</summary>
    private bool FireIfUsable(Avalonia.Controls.Button b, EventHandler? handler)
    {
        if (!b.IsVisible || !b.IsEnabled) return false;
        handler?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>水印开关（再点一次关闭）。灯与参数条的收口仍由宿主在收到事件后决定。</summary>
    public bool RequestWatermark()
    {
        if (!BtnWatermark.IsVisible || !BtnWatermark.IsEnabled) return false;

        if (_watermarkOn && _activeToolKey == "Watermark")
        {
            _watermarkOn = false;
            BtnWatermark.Classes.Remove("active");
        }
        else
        {
            _watermarkOn = true;
            BtnWatermark.Classes.Add("active");
        }

        WatermarkRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>录屏：只发信号，开/关由宿主统一走 <c>SetActiveTool("Record"/null)</c>。</summary>
    public bool RequestRecord() => FireIfUsable(BtnRecord, RecordRequested);

    public bool RequestOcr() => FireIfUsable(BtnOcr, OcrRequested);

    public bool RequestAiVision() => FireIfUsable(BtnAiVision, AiVisionRequested);

    public bool RequestSticker() => FireIfUsable(BtnSticker, StickerRequested);

    /// <summary>长截图（竖向）：事件带轴参数，故不走 <see cref="FireIfUsable"/>。</summary>
    public bool RequestLongScroll()
    {
        if (!BtnLongScroll.IsVisible || !BtnLongScroll.IsEnabled) return false;
        LongScrollRequested?.Invoke(this, new LongScrollRequestedEventArgs(ScrollAxis.Vertical));
        return true;
    }

    /// <summary>
    /// 录屏格（稿 1 D01/D02）：只发信号，开/关由宿主统一走 <c>SetActiveTool("Record"/null)</c>——
    /// 灯与参数条归属全由 SetActiveTool 派生（不像水印那盏开关灯有跨工具常驻态），
    /// 所以这里不做水印那种两段式，再点一次就是收条。
    /// </summary>
    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestRecord();
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRedoClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RedoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearAllAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearAllAnnotationsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEraserClick(object? sender, RoutedEventArgs e)
    {
        EraserRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEraserSizeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        EraserSizeValue.Text = Math.Round(e.NewValue).ToString();
        EraserSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEraserRectangleClick(object? sender, RoutedEventArgs e)
        => SetEraserMode(EraserGestureMode.Rectangle);

    private void OnEraserBrushClick(object? sender, RoutedEventArgs e)
        => SetEraserMode(EraserGestureMode.Brush);

    private void SetEraserMode(EraserGestureMode mode)
    {
        if (SelectedEraserMode == mode) return;
        SelectedEraserMode = mode;
        BtnEraserRectangle.Classes.Set("active", mode == EraserGestureMode.Rectangle);
        BtnEraserBrush.Classes.Set("active", mode == EraserGestureMode.Brush);
        EraserBrushSizePanel.IsVisible = mode == EraserGestureMode.Brush;
        LayoutOverlayStack();
        EraserModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLongScrollClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestLongScroll();
    }

    private void OnHorizontalLongScrollClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        LongScrollRequested?.Invoke(this, new LongScrollRequestedEventArgs(ScrollAxis.Horizontal));
    }

    private void OnOcrClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestOcr();
    }

    private void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TranslateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMattingClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        MattingRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAiVisionClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestAiVision();
    }

    private void OnStickerClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestSticker();
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ConfirmRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnQuickSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        QuickSaveRequested?.Invoke(this, EventArgs.Empty);
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SaveAsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        QuickSaveRequested?.Invoke(this, EventArgs.Empty);
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>更新撤销按钮的可用状态：无可撤销历史时置灰不可点。</summary>
    public void SetUndoEnabled(bool enabled)
    {
        if (BtnUndo != null) BtnUndo.IsEnabled = enabled;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateToolOptionLabels()
    {
        if (SliderMosaicBlockSize == null || SliderSpotlightDarkness == null
            || SliderBrushOpacity == null) return;
        TxtMosaicBlockSize.Text = SelectedBlurUiLevel.ToString();
        TxtSpotlightDarkness.Text = Math.Round(SliderSpotlightDarkness.Value).ToString();
        if (_counterOptions != null) _counterOptions.ValueText.Text = Math.Round(_counterOptions.Slider.Value).ToString();
        TxtBrushOpacity.Text = Math.Round(SliderBrushOpacity.Value).ToString();
        TxtBrushTaper.Text = Math.Round(SliderBrushTaper.Value).ToString();
        TxtBrushSmoothing.Text = Math.Round(SliderBrushSmoothing.Value).ToString();
        TxtBrushMinWidth.Text = Math.Round(SliderBrushMinWidth.Value).ToString();
        if (TextSpotlightStrokeValue != null) TextSpotlightStrokeValue.Text = Math.Round(SliderSpotlightStrokeThickness.Value).ToString();

        var c = SelectedAnnotationColor;
        // 序号那排小样画的就是这颗色：不在这里回灌，改了标注色后条上仍是旧色，"样式"按钮就白做了
        _counterOptions?.SetAnnotationColor(Color.FromArgb(c.A, c.R, c.G, c.B));
        if (SpotlightStrokeCircle != null) SpotlightStrokeCircle.Fill = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        CurrentColorPreview.Background = GetCurrentTargetBrush();
        UpdateStyleOptionVisuals();
    }

    private static void HighlightTargetButton(Button? btn, bool active)
    {
        if (btn == null) return;
        if (active)
        {
            if (!btn.Classes.Contains("active")) btn.Classes.Add("active");
        }
        else
        {
            btn.Classes.Remove("active");
        }
    }

    private void UpdateStyleOptionVisuals()
    {
        if (BrushLineStylePreview != null)
        {
            BrushLineStylePreview.StrokeDashArray = _selectedBrushLineStyle == AnnotationLineStyle.DashLarge ? new AvaloniaList<double> { 2, 2 } : null;
            ToolTip.SetTip(BtnBrushLineStyle, _selectedBrushLineStyle == AnnotationLineStyle.DashLarge ? "画笔线型：虚线" : "画笔线型：实线");
        }
        if (BtnBrushAdvanced != null)
        {
            bool tapered = SliderBrushTaper.Value > 0;
            ToolTip.SetTip(BtnBrushAdvanced, tapered
                ? $"笔迹：速度笔锋 / 强度 {Math.Round(SliderBrushTaper.Value)} / 平滑 {Math.Round(SliderBrushSmoothing.Value)}"
                : "笔迹：等宽（笔锋关闭）");
        }
        if (ShapeLineStylePreview != null)
        {
            ShapeLineStylePreview.StrokeDashArray = _selectedShapeLineStyle == AnnotationLineStyle.DashLarge ? new AvaloniaList<double> { 2, 2 } : null;
            ToolTip.SetTip(BtnShapeLineStyle, _selectedShapeLineStyle == AnnotationLineStyle.DashLarge ? "形状线型：虚线" : "形状线型：实线");
        }
        // 箭头档：按钮上那颗小样与下拉里的当前档一起跟上（同一份画法，不再另描一遍轮廓）
        ArrowStyleSwatch.Style = _selectedArrowStyle;
        _arrowStyleMenu.Sync(_selectedArrowStyle);
        ToolTip.SetTip(BtnArrowStyle, "箭头样式：" + ArrowHeadStyleMenu.DisplayName(_selectedArrowStyle));
        if (ShapeKindPreview != null)
        {
            ShapeKindPreview.Data = _selectedShapeKind == ShapeAnnotationKind.Ellipse
                ? Geometry.Parse("M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z")
                : Geometry.Parse("M7,4 H17 A3,3 0 0 1 20,7 V17 A3,3 0 0 1 17,20 H7 A3,3 0 0 1 4,17 V7 A3,3 0 0 1 7,4 Z");
            ToolTip.SetTip(BtnShapeKind, _selectedShapeKind == ShapeAnnotationKind.Ellipse ? "形状：圆形" : "形状：矩形");
        }
    }

    /// <summary>
    /// 拖动/缩放选区期间显示同一条参数条：文案与常态完全一致，只是内容不接交互。
    ///
    /// 为什么不再单独维护一个精简条（2026-09-17 合并）：滑杆退役后参数条本身就是"纯文本 + 数值"，
    /// 与精简条的规格完全重合；而两个控件必须逐值同步圆角/内边距/高度/措辞，差一点就会在拖动起止那一帧弹一下。
    /// 合并后少一个控件、少一套同步义务。方法签名保持不变，故 CaptureSession 侧一行不改。
    /// </summary>
    public void ShowDragInfoPill(Rect regionDip, System.Drawing.Rectangle screenRect)
    {
        long start = _captureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            TxtSelectionSize.Text = screenRect.Width + " × " + screenRect.Height;
            RefreshCaptureEffectLabels();
            SetPillInteractive(false);
            ShowInfoPillAt(regionDip);
        }
        finally
        {
            if (_captureDiagnostics)
            {
                long took = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                _dragPillCount++;
                _dragPillTotalTicks += took;
                _dragPillMaxTicks = Math.Max(_dragPillMaxTicks, took);
            }
        }
    }

    /// <summary>
    /// 参数条贴选区右缘定位：右缘与选区的视觉外缘平齐（描边画在内容边界以内，故要补上视觉外扩量），
    /// 上方放不下就翻到选区下方，四周留 <c>OverlayMargin</c>。
    /// 宽度一律走实测：数值 8 → 10 那一档条宽会变，拿实测宽度反推才能自动抵消。
    /// </summary>
    private void ShowInfoPillAt(Rect regionDip, Rect? toolbarStack = null)
    {
        InfoPill.IsVisible = true;
        long measureStart = _captureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Size infoSize = LayoutMetrics.MeasureNow(InfoPill);
        if (_captureDiagnostics)
        {
            long took = System.Diagnostics.Stopwatch.GetTimestamp() - measureStart;
            _pillMeasureCount++;
            _pillMeasureTotalTicks += took;
            _pillMeasureMaxTicks = Math.Max(_pillMeasureMaxTicks, took);
        }
        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double selectionBorderVisualOutset = SelectionLayer.GetSelectionOuterOffset(Layer.SelectionBorderThickness);
        double infoX = Clamp(regionDip.Right + selectionBorderVisualOutset - infoSize.Width, OverlayMargin, Math.Max(OverlayMargin, screenW - infoSize.Width - OverlayMargin));
        double infoY = regionDip.Y - infoSize.Height - OverlayGap;
        if (infoY < OverlayMargin) infoY = regionDip.Y + OverlayGap;
        infoY = Clamp(infoY, OverlayMargin, Math.Max(OverlayMargin, screenH - infoSize.Height - OverlayMargin));
        if (toolbarStack is Rect stack
            && new Rect(infoX, infoY, infoSize.Width, infoSize.Height).Intersects(
                new Rect(stack.X - OverlayBarGap, stack.Y - OverlayBarGap,
                    stack.Width + 2 * OverlayBarGap, stack.Height + 2 * OverlayBarGap)))
        {
            double above = stack.Top - OverlayBarGap - infoSize.Height;
            double below = stack.Bottom + OverlayBarGap;
            bool aboveFits = above >= OverlayMargin;
            bool belowFits = below + infoSize.Height <= screenH - OverlayMargin;
            if (aboveFits && (!belowFits || Math.Abs(above - infoY) <= Math.Abs(below - infoY))) infoY = above;
            else if (belowFits) infoY = below;
            else if (stack.Left - OverlayBarGap - infoSize.Width >= OverlayMargin)
                infoX = stack.Left - OverlayBarGap - infoSize.Width;
            else if (stack.Right + OverlayBarGap + infoSize.Width <= screenW - OverlayMargin)
                infoX = stack.Right + OverlayBarGap;
        }
        Canvas.SetLeft(InfoPill, infoX);
        Canvas.SetTop(InfoPill, infoY);
    }

    /// <summary>
    /// 把圆角浮条贴到选中图元的**正上方**；无选中、或选中的图元不属于「矩形 + 圆角」这一族时收起。
    ///
    /// <para>这一族有两个成员：**矩形档聚光灯**与**矩形图元**（矩形工具画出来的那个）。两者的圆角在 Core 里
    /// 都只是"矩形 + 一个半径"，判定与渲染都只看矩形本身，故浮条一视同仁；椭圆档的聚光灯与矩形图元
    /// 都没有圆角概念，直接收起。浮条是这一族圆角的**唯一入口**（四角手柄已于 2026-09-18 整体退役）。</para>
    ///
    /// <para>几何取自图层里**预览优先**的那一个图元（<see cref="AnnotationLayer.GetSelectedShapeForOverlay"/>），
    /// 与画布上那圈选中手柄同源——拖动/缩放期间文档里还是旧几何，照文档算会让浮条落在图元后面一帧。</para>
    ///
    /// <para>定位口径与 <see cref="ShowInfoPillAt"/> 一致：上方放不下就翻到元素下方，四周留
    /// <c>OverlayMargin</c>；宽度一律走实测（1 位 → 3 位数字会让条宽变），拿实测宽度反推才能自动抵消。</para>
    /// </summary>
    public void RefreshSpotlightRadiusPill()
    {
        AnnotationShape? selected = _lastHasSelection ? AnnotationLayer.GetSelectedShapeForOverlay() : null;
        System.Drawing.RectangleF? bounds = selected switch
        {
            SpotlightShape { ShapeKind: SpotlightShapeKind.Rectangle } sp => sp.Rect,
            RectangleShape { ShapeKind: ShapeAnnotationKind.Rectangle } rc => rc.Rect,
            _ => null,
        };
        if (bounds is not { } shapeRect)
        {
            HideSpotlightRadiusPill();
            return;
        }

        // 屏幕物理像素 → 窗口 DIP：与 AnnotationLayer.ToDip 同一算式（ScreenOrigin 就是窗口 Position）。
        double s = SafeScaling;
        Rect rect = new Rect(
            (shapeRect.Left - Position.X) / s,
            (shapeRect.Top - Position.Y) / s,
            shapeRect.Width / s,
            shapeRect.Height / s);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            HideSpotlightRadiusPill();
            return;
        }

        // 微调态下**不重算位置**（2026-09-18）：此刻指针被浮条捕获，图元与选区都不可能动，重算纯属白跑；
        // 更要紧的是黑条这时是**横向撑宽的**（浮起层 5 档刻度必须压在它上面，见 PillValueFineController
        // 的"底片"那段），而下面那个"与参数条让位"的判据用的正是可见黑条的矩形 ⇒ 拿撑宽后的矩形去判，
        // 会在原本不重叠的情形下突然判成重叠、把浮条从元素上方翻到元素下方——手指还在拖，位置却跳一下。
        // 让位判定该基于**静置时的落点**，故整段跳过。（代价：撑宽的黑条可能暂时压住参数条一角，
        // 松手即恢复；比"拖到一半位置跳"好得多。）
        if (SpotlightRadiusPillHost.IsFineActive) return;

        // ⚠ 必须先置可见再量宽定位：不可见元素显式 Measure 得 0×0（同 LayoutMetrics 类型注释里的坑 A）
        SpotlightRadiusPillHost.SetRadius(CornerRadiusOf(selected));
        SpotlightRadiusPillHost.IsVisible = true;
        Size size = LayoutMetrics.MeasureNow(SpotlightRadiusPillHost);

        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double x = Clamp(rect.Center.X - size.Width / 2.0, OverlayMargin,
            Math.Max(OverlayMargin, screenW - size.Width - OverlayMargin));
        double y = rect.Y - size.Height - OverlayGap;
        if (y < OverlayMargin) y = rect.Bottom + OverlayGap;
        // 让位：参数条是**常驻**的（有选区就在），且同样贴着选区上缘放；聚光灯贴到选区上缘时两者必然
        // 压在一起，而浮条声明在后、会盖住参数条。故上方位置与参数条相交就翻到元素下方。
        // 读 Canvas.GetLeft/Top 而不是 InfoPill.Bounds：Bounds 反映的是**上一次布局**的结果，
        // 而 ShowInfoPillAt 刚刚才写过新位置，拿 Bounds 判断会滞后一帧、避让偶发不生效。
        if (InfoPill.IsVisible)
        {
            double pillLeft = Canvas.GetLeft(InfoPill);
            double pillTop = Canvas.GetTop(InfoPill);
            // 比的是**黑条**矩形而不是控件矩形：控件的可绘制区比黑条宽（给浮起层 5 档刻度留的余量），
            // 照控件矩形判会让"其实还差几十像素才挨上"的情形也翻到元素下方。
            Rect bar = SpotlightRadiusPillHost.PillBounds;
            if (!double.IsNaN(pillLeft) && !double.IsNaN(pillTop)
                && new Rect(pillLeft, pillTop, InfoPill.Bounds.Width, InfoPill.Bounds.Height)
                    .Intersects(new Rect(x + bar.X, y, bar.Width, size.Height)))
            {
                y = rect.Bottom + OverlayGap;
            }
        }
        y = Clamp(y, OverlayMargin, Math.Max(OverlayMargin, screenH - size.Height - OverlayMargin));

        Canvas.SetLeft(SpotlightRadiusPillHost, x);
        Canvas.SetTop(SpotlightRadiusPillHost, y);
    }

    /// <summary>
    /// 收起圆角浮条。先让控件收回浮起层再藏——浮起层是浮条内部的兄弟节点，藏浮条虽然会连它一起不可见，
    /// 但状态机仍停在"微调中"，下次选中别的聚光灯时会带着半收的缩放/透明值重新浮出来。
    ///
    /// <para>除"选中的图元不属于该族"之外，另一条来路是<b>拖动手势起手</b>（<c>CaptureSession.BeginShapeEdit</c>
    /// 与 <see cref="BeginSelectionTransform"/>）：浮条的位置只在几何提交时重算，拖动途中留在原地＝一条跟着
    /// 不动的假浮条，故手上这段手势期间一律收起，松手后由 <see cref="RefreshSpotlightRadiusPill"/> 按新几何贴出。</para>
    /// </summary>
    public void HideSpotlightRadiusPill()
    {
        if (!SpotlightRadiusPillHost.IsVisible) return;
        SpotlightRadiusPillHost.Collapse();
        SpotlightRadiusPillHost.IsVisible = false;
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
    /// 把序号浮条贴到选中序号的**正上方**；选中的不是序号就收起。
    ///
    /// <para>居中基准取**外形盒**而不是"圆点＋说明文字"整块：说明可以很长，按整块居中会把条推到画布外，
    /// 而且用户点的是圆点，条对齐圆点才对得上视线。</para>
    ///
    /// <para>定位口径与 <see cref="RefreshSpotlightRadiusPill"/> 逐条一致（屏幕物理像素→DIP、
    /// 先置可见再量宽、上方放不下翻到下方、与常驻参数条让位）。几何同样读图层里"预览优先"的那一个，
    /// 与画布上那圈选中手柄同源。</para>
    /// </summary>
    public void RefreshCounterPill()
    {
        AnnotationShape? selected = _lastHasSelection ? AnnotationLayer.GetSelectedShapeForOverlay() : null;
        if (selected is not CounterShape counter)
        {
            HideCounterPill();
            return;
        }

        // showTail：只有这一颗拖出过引线，那一格才有意义；tailShown：按钮明暗只读图元真相，不自留状态
        CounterPillHost.SetState(counter.Label, counter.Note, (int)Math.Round(counter.CapsuleCorner),
            counter.Glyph == CounterGlyphShape.Capsule, counter.Tail is not null, counter.TailShown);
        // 正在输入说明文字时不重摆位置：宿主每改一次文档都会回灌一次，位置跟着跳一下就把光标打断了
        if (CounterPillHost.IsEditingNote) return;
        // 微调态下同样不重摆（与圆角浮条那一处同一条理由）：黑条此刻被控制器撑宽去装浮起的刻度，
        // 拿撑宽后的矩形判"与参数条是否相交"会把本来不重叠的情形判成重叠，手指还在拖、位置却跳一下。
        if (CounterPillHost.IsFineActive) return;

        double s = SafeScaling;
        System.Drawing.RectangleF glyph = counter.GlyphRect;
        Rect box = new(
            (glyph.Left - Position.X) / s,
            (glyph.Top - Position.Y) / s,
            glyph.Width / s,
            glyph.Height / s);
        if (box.Width <= 0 || box.Height <= 0)
        {
            HideCounterPill();
            return;
        }

        // 同屏只留一条浮条：选中序号会浮出这颗号自己的浮条（贴在圆点上方），挂在参数条「样式」下面的
        // 那排样式小样先收掉——两条栏同时压在选区上正是要做减法的反方向。
        _counterOptions?.CloseStylePopup();
        // ⚠ 必须先置可见再量宽：不可见元素显式 Measure 得 0×0（同 LayoutMetrics 类型注释里的坑 A）
        CounterPillHost.IsVisible = true;
        Size size = LayoutMetrics.MeasureNow(CounterPillHost);

        double screenW = Math.Max(Bounds.Width, 1);
        double screenH = Math.Max(Bounds.Height, 1);
        double x = Clamp(box.Center.X - size.Width / 2.0, OverlayMargin,
            Math.Max(OverlayMargin, screenW - size.Width - OverlayMargin));
        double y = box.Y - size.Height - OverlayGap;
        if (y < OverlayMargin) y = box.Bottom + OverlayGap;
        // 与常驻参数条让位（判据与圆角浮条那一处相同）：两者都贴着选区上缘放，压在一条上就翻到下方。
        // 比的是**黑条**矩形而不是控件矩形：本控件现在也给了浮起层刻度留可绘制区（MinWidth 260），
        // 照控件矩形判会让"其实还差几十像素才挨上"的情形也翻到元素下方。
        if (InfoPill.IsVisible)
        {
            double pillLeft = Canvas.GetLeft(InfoPill);
            double pillTop = Canvas.GetTop(InfoPill);
            Rect bar = CounterPillHost.PillBounds;
            if (!double.IsNaN(pillLeft) && !double.IsNaN(pillTop)
                && new Rect(pillLeft, pillTop, InfoPill.Bounds.Width, InfoPill.Bounds.Height)
                    .Intersects(new Rect(x + bar.X, y, bar.Width, size.Height)))
            {
                y = box.Bottom + OverlayGap;
            }
        }
        y = Clamp(y, OverlayMargin, Math.Max(OverlayMargin, screenH - size.Height - OverlayMargin));

        Canvas.SetLeft(CounterPillHost, x);
        Canvas.SetTop(CounterPillHost, y);
    }

    /// <summary>
    /// 收起序号浮条。除"选中的不是序号"外，另一条来路是<b>拖动手势起手</b>（含新建时起手那段拖引线）：
    /// 浮条只在几何提交时重算位置，手势途中留在原地就是一条不跟手的假浮条。
    /// </summary>
    public void HideCounterPill()
    {
        if (!CounterPillHost.IsVisible) return;
        // 先收回浮起层再藏（与 HideSpotlightRadiusPill 同一条理由：状态机停在"微调中"会带半收的缩放值复活）
        CounterPillHost.Collapse();
        CounterPillHost.IsVisible = false;
    }

    /// <summary>
    /// 切换参数条内容的可命中性。拖动期间要关掉：取色器、阴影勾选、数值微调都不该在拖选区时被触发。
    /// ⚠ 只关内容、不关 <c>InfoPill</c> 本身——它的不透明底色仍在遮罩的命中白名单里，指针落在条上不会误起新框；
    /// 若把整条置为不可命中，指针会穿透到窗口，拖动期间正好压在条上就会另起一个选区。
    /// </summary>
    private void SetPillInteractive(bool value)
    {
        if (_pillDragMode == !value) return;
        _pillDragMode = !value;
        PillContent.IsHitTestVisible = value;
        if (!value) _pillFine.Collapse();
    }

    public void BeginSelectionTransform(bool keepDragPill = false)
    {
        // 拖动与缩放选区的每一帧都不该带上描边/阴影预览（那是预览层唯一的重活）。参数条隐藏那一路
        // 会顺带关掉闸门，但那是巧合不是约束——参数条本来就是隐藏状态时闸门可能还挂着，这里显式收。
        // 参数条本身的隐藏顺带会把数值微调浮起层收回（挂在该控件的 PropertyChanged 上）。
        SetCaptureEffectHover(false);
        if (!keepDragPill) InfoPill.IsVisible = false;
        ActionBar.IsVisible = false;
        ToolOptionsBar.IsVisible = false;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
        // 圆角浮条贴在选区上缘那一族浮层里：选区一动它就陈旧，同参数条一起收起，松手由 UpdateOverlay 贴回
        HideSpotlightRadiusPill();
        HideCounterPill();
        CollapseToolbarGroup(false);
        SetShortcutHintState(ShortcutHintState.DraggingNew);
    }

    public void EndSelectionTransform(Rect regionDip, System.Drawing.Rectangle screenRect)
    {
        UpdateOverlay(regionDip, screenRect, true, true);
    }

    public void UpdateOverlay(Rect regionDip, System.Drawing.Rectangle? screenRect, bool hasSelection, bool showActionBar = true)
    {
        // 圆角/阴影预览的换算系数：绘制层拿的是 DIP，而这两个参数按物理像素记忆，必须现算现给。
        Layer.ScreenToDipScale = 1.0 / SafeScaling;
        _lastRegionDip = regionDip;
        _lastHasSelection = hasSelection && regionDip.Width > 0 && regionDip.Height > 0 && screenRect != null;
        if (!_lastHasSelection)
        {
            HideAutoMosaicScanAnimation(true);
            InfoPill.IsVisible = false;
            ActionBar.IsVisible = false;
            ToolOptionsBar.IsVisible = false;
            if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
            GroupBar.IsVisible = false;
            if (GroupBarArrow != null) GroupBarArrow.IsVisible = false;
            HideSpotlightRadiusPill();
            HideCounterPill();
            SetShortcutHintState(ShortcutHintState.IdleElementHover);
            EvaluateShortcutHintVisibility();
            return;
        }

        if (!showActionBar)
        {
            ActionBar.IsVisible = false;
            ToolOptionsBar.IsVisible = false;
            if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = false;
            GroupBar.IsVisible = false;
            if (GroupBarArrow != null) GroupBarArrow.IsVisible = false;
            ShowDragInfoPill(regionDip, screenRect!.Value);
            EvaluateShortcutHintVisibility();
            return;
        }

        SetPillInteractive(true);
        TxtSelectionSize.Text = screenRect!.Value.Width + " × " + screenRect.Value.Height;
        RefreshCaptureEffectLabels();
        ActionBar.IsVisible = true;
        // 参数栏与组成员栏互斥（同屏最多两条栏）：组摊着的时候参数栏让位，组一收由 CollapseToolbarGroup 补位
        bool hasActiveToolOptions = HasToolOptions(_activeToolKey) && _expandedToolbarGroup == null;
        ToolOptionsBar.IsVisible = hasActiveToolOptions;
        if (ToolOptionsArrow != null) ToolOptionsArrow.IsVisible = hasActiveToolOptions;

        // 整叠先落位，再按本帧矩形重测并避让 InfoPill，切工具／开组时沿用同一入口。
        LayoutOverlayStack();
        if (PathEditor is { Mode: not ScreenCaptureTool.Core.Capture.CaptureSelectionMode.Rectangle })
            InfoPill.IsVisible = false;
        // 选区一动，贴在元素上方的圆角浮条也要重算（选中聚光灯本身没变，但它在窗口里的位置变了）。
        RefreshSpotlightRadiusPill();
        RefreshCounterPill();
        SetShortcutHintState(!string.IsNullOrEmpty(_activeToolKey) ? ShortcutHintState.ToolActive : ShortcutHintState.SelectionConfirmed);
        EvaluateShortcutHintVisibility();
    }

    private Button? GetActiveToolButton()
    {
        return _activeToolKey switch
        {
            "Brush" => BtnBrush,
            "Arrow" => BtnArrow,
            "Rectangle" => BtnRectangle,
            "Text" => BtnText,
            "Mosaic" => BtnMosaic,
            "Spotlight" => BtnSpotlight,
            "Counter" => BtnCounter,
            "Watermark" => BtnWatermark,
            "Record" => BtnRecord,
            "Eraser" => BtnEraser,
            _ => null,
        };
    }

    /// <summary>把一枚 14×7 小三角钉在某条栏的边缘、水平指准上一级那枚格子的中心（二级与三级共用）。</summary>
    private void PositionOverlayArrow(ShapePath? arrow, double barX, double barY, Size barSize, double targetCenterX, bool arrowOnTop)
    {
        if (arrow == null) return;

        // 箭头水平居中于目标按钮
        double arrowCenterX = Clamp(targetCenterX, barX + 7, barX + barSize.Width - 7);
        double arrowX = arrowCenterX - 7; // 箭头宽 14px

        double arrowY;
        if (arrowOnTop)
        {
            // 箭头在栏上方，尖端朝上
            // 外层 Border 有 Padding="1"，内层面板从 barY+1 开始。
            // 箭头底部深入内层面板 2px，彻底消除抗锯齿缝隙。
            arrowY = barY - 4;
            arrow.Data = Geometry.Parse("M0,7 L7,0 L14,7 Z");
        }
        else
        {
            // 箭头在栏下方，尖端朝下
            // 内层面板底部在 barY+height-1 处，箭头顶部深入面板 2px。
            arrowY = barY + barSize.Height - 5;
            arrow.Data = Geometry.Parse("M0,0 L7,7 L14,0 Z");
        }

        Canvas.SetLeft(arrow, arrowX);
        Canvas.SetTop(arrow, arrowY);
        arrow.IsVisible = true;
    }

    /// <summary>
    /// 会话中途把遮罩提前撤掉的时刻（UTC），由 <c>CaptureSession.HideCaptureWindowForCompletion</c> 写、
    /// <c>RestoreCaptureWindowForCompletion</c> 清空。"遮罩已撤、会话却还悬着"本质是过渡态，
    /// 停留过久即说明某条完成路径漏了收尾，看门狗据此判定该把窗口彻底撤掉。
    /// </summary>
    public DateTime? LastMaskHiddenUtc { get; set; }

    /// <summary>Opened 时缓存的原生句柄：OnClosed 阶段窗口多已销毁，届时再取会拿到 0，
    /// 持久置顶守卫就再也停不掉（1 秒定时器与 WndProc 子类化泄漏，并继续对已被系统复用的句柄强制置顶）。</summary>
    private IntPtr _openedHwnd = IntPtr.Zero;
    private bool _captureDiagnostics;
    private string _captureDiagnosticEntry = "";
    private string? _openedDiagnostics;
    private string? _wireUpDiagnostics;
    internal bool GlobalMouseDragActive { get; set; }

    internal void SetGlobalMousePointerPosition(System.Drawing.Point screenPoint)
    {
        double scale = SafeScaling;
        _lastPointerDip = new Point((screenPoint.X - Position.X) / scale, (screenPoint.Y - Position.Y) / scale);
    }
    private int _dragPillCount, _pillMeasureCount, _hintMeasureCount, _rawMoveCallbacks, _rawMoveBypasses;
    private long _dragPillTotalTicks, _dragPillMaxTicks, _pillMeasureTotalTicks, _pillMeasureMaxTicks;
    private long _hintMeasureTotalTicks, _hintMeasureMaxTicks;

    internal void BeginCaptureDiagnostics(long startTimestamp, bool globalMouse)
    {
        _captureDiagnostics = AppLogger.DetailedLoggingEnabled;
        _captureDiagnosticEntry = globalMouse ? "GlobalMouse" : "Normal";
        _openedDiagnostics = null;
        _wireUpDiagnostics = null;
        _dragPillCount = _pillMeasureCount = _hintMeasureCount = _rawMoveCallbacks = _rawMoveBypasses = 0;
        _dragPillTotalTicks = _dragPillMaxTicks = _pillMeasureTotalTicks = _pillMeasureMaxTicks = 0;
        _hintMeasureTotalTicks = _hintMeasureMaxTicks = 0;
        Layer.BeginRenderDiagnostics(startTimestamp, _captureDiagnosticEntry);
    }

    internal void FlushOpenedDiagnostics()
    {
        if (_wireUpDiagnostics is { } wireUp)
        {
            _wireUpDiagnostics = null;
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug(wireUp);
        }
        if (_openedDiagnostics is { } opened)
        {
            _openedDiagnostics = null;
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug(opened);
        }
    }

    internal void RecordWireUpDiagnostics(string summary) => _wireUpDiagnostics = summary;

    private void FinishCaptureDiagnostics()
    {
        if (!_captureDiagnostics) return;
        _captureDiagnostics = false;
        FlushOpenedDiagnostics();
        Layer.FinishRenderDiagnostics();
        double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PERF CaptureOverlay.Summary entry={_captureDiagnosticEntry} dragPills={_dragPillCount} "
            + $"dragPillAvg={_dragPillTotalTicks * ms / Math.Max(1, _dragPillCount):F3}ms dragPillMax={_dragPillMaxTicks * ms:F3}ms "
            + $"measures={_pillMeasureCount} measureAvg={_pillMeasureTotalTicks * ms / Math.Max(1, _pillMeasureCount):F3}ms "
            + $"measureMax={_pillMeasureMaxTicks * ms:F3}ms hintMeasures={_hintMeasureCount} "
            + $"hintMeasureAvg={_hintMeasureTotalTicks * ms / Math.Max(1, _hintMeasureCount):F3}ms "
            + $"hintMeasureMax={_hintMeasureMaxTicks * ms:F3}ms rawMoveCallbacks={_rawMoveCallbacks} rawMoveBypasses={_rawMoveBypasses}");
    }

    private bool _preparingFirstCapture;
    private bool _firstCapturePreparationCloaked;
    private TaskCompletionSource? _firstCapturePreparation;
    private IDisposable? _firstCapturePreparationTimeout;
    private string? _preparedToolbarLayout;
    private Avalonia.Media.Imaging.Bitmap? _firstCapturePreparationBitmap;

    /// <summary>
    /// 在虚拟屏幕之外首次显示，让同一窗口完成原生呈现与控件布局；不激活、不置顶。
    /// 临时背景只用于预热真实图片呈现，收回即释放；不挂会话交互、UIA 或持久置顶守卫。
    /// </summary>
    internal Task PrepareForFirstCaptureAsync(Avalonia.Media.Imaging.Bitmap? warmupBitmap = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _firstCapturePreparationBitmap = warmupBitmap;
        _preparingFirstCapture = true;
        _firstCapturePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparedToolbarLayout = AppSettings.Current.ToolbarLayout;
        var virtualScreen = MonitorHelper.GetVirtualScreenRect();
        ShowActivated = false;
        Topmost = false;
        IsHitTestVisible = false;
        Width = virtualScreen.Width / SafeScaling;
        Height = virtualScreen.Height / SafeScaling;
        Position = new PixelPoint(checked(virtualScreen.Right + 64), virtualScreen.Y);
        var completion = _firstCapturePreparation.Task;
        // 必须先隐匿再 Show；失败时只保留未显示的预备窗，不冒险出现一张启动遮罩。
        if (!WindowPresentationHelper.TrySetCloaked(this, true))
        {
            _preparingFirstCapture = false;
            ShowActivated = true;
            Topmost = true;
            IsHitTestVisible = true;
            ReleaseFirstCapturePreparationBitmap();
            _firstCapturePreparation.TrySetResult();
            AppLogger.Warn("CaptureWindow: 原生隐匿不可用，仅保留构造预热");
            return completion;
        }
        _firstCapturePreparationCloaked = true;
        SetFrozenScreenBitmap(warmupBitmap);
        using (PerformanceTimer.Measure("CaptureWindow.StartupInteraction"))
        {
            // 真实拖框第一次才显示的参数条与文字，也在同一个隐匿窗口中走模板、布局及绘制。
            // 无会话、无检测、无参数保存；首用仍按当前设置重新恢复。
            ApplyAnnotationDefaults(AppSettings.Current);
            SyncCaptureEffectState(AppSettings.Current.CaptureCornerRadius, AppSettings.Current.CaptureShadow,
                AppSettings.Current.CaptureStrokeWidth, AppSettings.Current.CaptureStrokeColor);
            Layer.ScreenToDipScale = 1.0 / SafeScaling;
            Layer.EffectRadiusPx = Core.Capture.SelectionEffects.ParamToRadiusPx(AppSettings.Current.CaptureCornerRadius);
            Layer.EffectStrokePx = AppSettings.Current.CaptureStrokeWidth;
            Layer.EffectStrokeArgb = AppSettings.Current.CaptureStrokeColor;
            Layer.EffectShadow = AppSettings.Current.CaptureShadow;
            Layer.EffectsVisible = false;
            int sampleWidth = Math.Max(1, Math.Min(320, virtualScreen.Width - 2));
            int sampleHeight = Math.Max(1, Math.Min(240, virtualScreen.Height - 2));
            var sampleScreen = new System.Drawing.Rectangle(virtualScreen.X + 1, virtualScreen.Y + 1, sampleWidth, sampleHeight);
            var sampleDip = new Rect(1 / SafeScaling, 1 / SafeScaling, sampleWidth / SafeScaling, sampleHeight / SafeScaling);
            BeginSelectionTransform();
            Layer.Region = sampleDip;
            ShowDragInfoPill(sampleDip, sampleScreen);
        }
        using (PerformanceTimer.Measure("CaptureWindow.StartupShow")) Show();
        // 第一格提交渲染，下一格收回屏幕外的窗口；用户先触发时可立即接手，不等这两格。
        RequestAnimationFrame(_ =>
        {
            if (!_preparingFirstCapture) return;
            RequestAnimationFrame(__ => CompleteFirstCapturePreparationSafely("显示帧回调"));
        });
        _firstCapturePreparationTimeout = DispatcherTimer.RunOnce(
            () => CompleteFirstCapturePreparationSafely("超时收回"), TimeSpan.FromSeconds(1));
        return completion;
    }

    private void CompleteFirstCapturePreparationSafely(string reason)
    {
        try { FinishFirstCapturePreparation(reason); }
        catch (Exception ex) { _firstCapturePreparation?.TrySetException(ex); }
    }

    /// <summary>同时是首用、超时和退出的收口；旧 RAF 回调不得隐藏已接手的真实截图。</summary>
    internal void FinishFirstCapturePreparation(string reason = "首用接手")
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_preparingFirstCapture) return;
        _firstCapturePreparationTimeout?.Dispose();
        _firstCapturePreparationTimeout = null;
        Hide();
        // 清除预备选区、拖动提示和只读态，不能把样例带进真实会话。
        Layer.Region = default;
        UpdateOverlay(default, null, false, false);
        SetPillInteractive(true);
        ReleaseFirstCapturePreparationBitmap();
        if (_firstCapturePreparationCloaked && !WindowPresentationHelper.TrySetCloaked(this, false))
            throw new InvalidOperationException("启动窗口无法解除原生隐匿");
        _firstCapturePreparationCloaked = false;
        _preparingFirstCapture = false;
        ShowActivated = true;
        Topmost = true;
        IsHitTestVisible = true;
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug("CaptureWindow: 首次截图准备结束，原因=" + reason);
        _firstCapturePreparation?.TrySetResult();
    }

    private void ReleaseFirstCapturePreparationBitmap()
    {
        var bitmap = _firstCapturePreparationBitmap;
        _firstCapturePreparationBitmap = null;
        if (bitmap == null) return;
        // 只清理预热背景，不能碰首用已经设置的正式冻结图。
        if (ReferenceEquals(FrozenScreenImage.Source, bitmap)) FrozenScreenImage.Source = null;
        bitmap.Dispose();
    }

    /// <summary>备用期间设置可能被修改；仅布局实际变化时重排，快捷键说明按当前绑定刷新。</summary>
    internal void RefreshPreparedCaptureSettings()
    {
        if (!string.Equals(_preparedToolbarLayout, AppSettings.Current.ToolbarLayout, StringComparison.Ordinal))
            ApplyToolbarLayout();
        ApplyShortcutHints();
        EvaluateShortcutHintVisibility();
    }

    public IntPtr GetWindowHwnd()
    {
        try
        {
            var handle = TryGetPlatformHandle();
            return handle?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private async Task ForceTopmostAgainAsync()
    {
        try
        {
            await Task.Delay(50);
            await Dispatcher.UIThread.InvokeAsync(() => ForceTopmostSafely("Delayed"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.ForceTopmostAgainAsync: 延迟置顶异常：" + ex.Message);
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_preparingFirstCapture) return;
        _activeOverlay = new WeakReference<CaptureWindow>(this);
        StickerWindow.HideColorPickersForCapture();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long layoutTicks = 0, topmostTicks = 0, imeTicks = 0, hintTicks = 0;
        try
        {
            System.Drawing.Rectangle virt = MonitorHelper.GetVirtualScreenRect();
            double scale = SafeScaling;

            // Position 是物理像素（PixelPoint），Width/Height 是 DIP
            long stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Position = new PixelPoint(virt.X, virt.Y);
            Width = virt.Width / scale;
            Height = virt.Height / scale;
            UpdateAnnotationTransform();
            layoutTicks = System.Diagnostics.Stopwatch.GetTimestamp() - stageStarted;
            IntPtr hwnd = GetWindowHwnd();
            _openedHwnd = hwnd;
            stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            ForceTopmostSafely("Opened");
            _ = ForceTopmostAgainAsync();
            // 启用持久置顶：防止被其他前台窗口挤到后面。
            try { WindowHelper.EnforceTopmost(hwnd); } catch { }
            topmostTicks = System.Diagnostics.Stopwatch.GetTimestamp() - stageStarted;

            // 解除 IME 关联：中文输入法激活时字母键不再被组字拦截，
            // Shift+B/A/R/M/S/C/T、Esc、Ctrl+Z 等截图快捷键 100% 响应。
            // 文字标注编辑时 TextBox 获得焦点，Avalonia 会自动重建 IME 关联，中文输入不受影响。
            stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try { WindowHelper.DisableIme(hwnd); } catch { }
            imeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - stageStarted;

            stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            EvaluateShortcutHintVisibility();
            hintTicks = System.Diagnostics.Stopwatch.GetTimestamp() - stageStarted;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CaptureWindow.OnWindowOpened: 铺满虚拟屏幕失败：" + ex.Message);
        }
        finally
        {
            if (_captureDiagnostics)
            {
                // 日志同步加锁并刷新文件；阶段内只取时钟，首读后再汇总落盘。
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _openedDiagnostics = $"PERF CaptureWindow.Opened.Phases entry={_captureDiagnosticEntry} "
                    + $"total={(System.Diagnostics.Stopwatch.GetTimestamp() - started) * ms:F3}ms "
                    + $"layout={layoutTicks * ms:F3}ms topmost={topmostTicks * ms:F3}ms ime={imeTicks * ms:F3}ms hints={hintTicks * ms:F3}ms";
            }
        }
    }

    private Rect GetTextValidRect()
    {
        if (_lastHasSelection && _lastRegionDip.Width > 1 && _lastRegionDip.Height > 1)
            return _lastRegionDip;
        return new Rect(0, 0, Math.Max(OverlayCanvas.Bounds.Width, 1), Math.Max(OverlayCanvas.Bounds.Height, 1));
    }

    private void OnOverlayCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        // 全局拖框没有文本编辑，坐标与提示避让由同一显示帧消费。
        // 画布事件还会冒泡到窗口，不能在这两站按鼠标回报率反复递归测量提示条。
        if (_captureDiagnostics) _rawMoveCallbacks++;
        if (GlobalMouseDragActive)
        {
            if (_captureDiagnostics) _rawMoveBypasses++;
            return;
        }
        _lastPointerDip = e.GetPosition(this);
        EvaluateShortcutHintVisibility();
        Text.TryHandleGlobalPointerMoved(sender, e);
    }

    private void OnOverlayCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Text.TryHandleGlobalPointerReleased(sender, e);
    }

    // ──────────────────────────────────────────
    // 文本工具按钮事件
    // ──────────────────────────────────────────

    private void OnTextClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TextRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>测量文本在给定宽度下的高度</summary>
    public double MeasureTextHeightForWidth(TextLayerInfo info, double maxWidth)
    {
        if (info?.TextBox == null) return 0;
        double saved = info.TextBox.MaxWidth;
        try
        {
            info.TextBox.MaxWidth = maxWidth > 0 ? maxWidth : double.PositiveInfinity;
            info.TextBox.InvalidateMeasure();
            info.TextBox.InvalidateArrange();
            info.Root.InvalidateMeasure();
            info.Root.InvalidateArrange();
            info.Root.UpdateLayout();
            return info.Root.Bounds.Height;
        }
        finally
        {
            info.TextBox.MaxWidth = saved;
        }
    }

    // ──────────────────────────────────────────
    // 标注工具状态持久化（跨会话记忆）
    // ──────────────────────────────────────────

    /// <summary>从 AppSettings 恢复上次使用的标注工具参数。</summary>
    public void ApplyAnnotationDefaults(AppSettings s)
    {
        if (s == null) return;

        // 通用：颜色 + 粗细
        SelectedAnnotationColor = System.Drawing.Color.FromArgb(s.AnnotationColor);
        Text.ForegroundTextColor = Color.FromArgb(SelectedAnnotationColor.A, SelectedAnnotationColor.R, SelectedAnnotationColor.G, SelectedAnnotationColor.B);
        if (SliderStrokeThickness != null)
            SliderStrokeThickness.Value = Math.Clamp(s.StrokeThickness, SliderStrokeThickness.Minimum, SliderStrokeThickness.Maximum);

        // 画笔线型
        if (Enum.TryParse<AnnotationLineStyle>(s.BrushLineStyle, true, out var bls))
            _selectedBrushLineStyle = bls;

        // 画笔笔刷画像：透明度 / 笔锋强度 / 平滑 / 最细比例（硬度固定实心抗锯齿，笔锋统一速度驱动）
        if (SliderBrushOpacity != null)
            SliderBrushOpacity.Value = Math.Clamp(s.BrushOpacity, SliderBrushOpacity.Minimum, SliderBrushOpacity.Maximum);
        if (SliderBrushTaper != null)
            SliderBrushTaper.Value = Math.Clamp(s.BrushTaperStrength, SliderBrushTaper.Minimum, SliderBrushTaper.Maximum);
        if (SliderBrushSmoothing != null)
            SliderBrushSmoothing.Value = Math.Clamp(s.BrushSmoothing, SliderBrushSmoothing.Minimum, SliderBrushSmoothing.Maximum);
        if (SliderBrushMinWidth != null)
            SliderBrushMinWidth.Value = Math.Clamp(s.BrushMinWidthRatio, SliderBrushMinWidth.Minimum, SliderBrushMinWidth.Maximum);

        // 箭头样式
        if (Enum.TryParse<ArrowAnnotationStyle>(s.ArrowStyle, true, out var aas))
            _selectedArrowStyle = aas;

        // 矩形形状 + 线型
        if (Enum.TryParse<ShapeAnnotationKind>(s.ShapeKind, true, out var sk))
            _selectedShapeKind = sk;
        if (Enum.TryParse<AnnotationLineStyle>(s.ShapeLineStyle, true, out var sls))
            _selectedShapeLineStyle = sls;

        // 模糊/马赛克
        if (Enum.TryParse<BlurMode>(s.BlurMode, true, out var bm))
            _selectedBlurMode = bm;
        if (SliderMosaicBlockSize != null)
            SliderMosaicBlockSize.Value = Math.Clamp(s.BlurUiLevel, SliderMosaicBlockSize.Minimum, SliderMosaicBlockSize.Maximum);

        // 聚光灯
        if (SliderSpotlightDarkness != null)
            SliderSpotlightDarkness.Value = Math.Clamp(s.SpotlightDarkness, SliderSpotlightDarkness.Minimum, SliderSpotlightDarkness.Maximum);
        if (SliderSpotlightStrokeThickness != null)
            SliderSpotlightStrokeThickness.Value = Math.Clamp(s.SpotlightStrokeThickness, SliderSpotlightStrokeThickness.Minimum, SliderSpotlightStrokeThickness.Maximum);
        // 聚光灯圆角记忆：恢复字段值（滑块已拆除，字段即记忆本体）
        _spotlightCornerRadius = ClampCornerRadius(s.SpotlightCornerRadius);

        // 矩形圆角记忆：恢复字段值，成为下一笔矩形的默认圆角
        SyncShapeCornerRadius(s.ShapeCornerRadius);
        // 形状填充记忆：勾选态与浓淡一并摆回共享控件（它是取值真源，摆上即成为下一笔的默认值）
        SyncShapeFill(s.ShapeFill, s.ShapeFillOpacity);

        // 序号：整套样式档交给共享控件（含「记住上次的类型/外形/底色」这条开关的判据）
        _annotationPanelDefaults = s;
        _counterOptions?.ApplyDefaults(s);
        // 水印：五项记忆值铺上参数条；开关不在这份里，每次截图一律从"关"开始（拍板第 2 条）
        _watermarkOptions?.ApplyDefaults(s);

        // 文本工具（12 个 Text* 键的读写已上提到共享实现，键名与顺序未变）
        Text.ApplyDefaults(s);

        // 刷新 UI
        RebuildBrushProfile();
        UpdateToolOptionLabels();
        Text.UpdateTextPanelUi();
    }

    /// <summary>将当前标注工具参数写入 AppSettings，供下次会话恢复。</summary>
    public void CaptureAnnotationDefaults(AppSettings s, bool includeCaptureEffects = true)
    {
        if (s == null) return;

        s.AnnotationColor = SelectedAnnotationColor.ToArgb();
        s.StrokeThickness = SliderStrokeThickness?.Value ?? 15;
        s.BrushLineStyle = _selectedBrushLineStyle.ToString();
        s.BrushOpacity = SliderBrushOpacity?.Value ?? 100;
        s.BrushHardness = 100;
        s.BrushTaperMode = "Velocity";
        s.BrushTaperStrength = SliderBrushTaper?.Value ?? 35;
        s.BrushSmoothing = SliderBrushSmoothing?.Value ?? 40;
        s.BrushMinWidthRatio = SliderBrushMinWidth?.Value ?? 25;
        s.ArrowStyle = _selectedArrowStyle.ToString();
        s.ShapeKind = _selectedShapeKind.ToString();
        s.ShapeLineStyle = _selectedShapeLineStyle.ToString();
        s.BlurMode = _selectedBlurMode.ToString();
        s.BlurUiLevel = SelectedBlurUiLevel;
        s.SpotlightDarkness = SliderSpotlightDarkness?.Value ?? 55;
        s.SpotlightStrokeThickness = SliderSpotlightStrokeThickness?.Value ?? 3;
        s.SpotlightCornerRadius = SelectedSpotlightCornerRadius;
        s.ShapeCornerRadius = SelectedShapeCornerRadius;
        s.ShapeFill = SelectedShapeFill;
        // 未勾选时不写浓淡：勾掉只是"这次不填"，不该把用户调好的浓淡抹成 0，下次勾回来还是他那个值
        s.ShapeFillOpacity = SelectedShapeFill ? SelectedShapeFillOpacity * 100 : s.ShapeFillOpacity;
        // 未使用的面板没有用户修改，不为收尾创建控件或覆盖其记忆值。
        _counterOptions?.CaptureDefaults(s);   // 起始值刻意不落盘，见 CounterOptionsControl
        _watermarkOptions?.CaptureDefaults(s); // 开关不落盘
        if (includeCaptureEffects)
        {
            s.CaptureCornerRadius = CaptureRadiusParam;
            CaptureStrokeDefaults(s);
            s.CaptureShadow = CaptureShadowChecked;
        }
        Text.CaptureDefaults(s);   // 12 个 Text* 键

        if (!string.IsNullOrEmpty(_activeToolKey) && _activeToolKey is "Brush" or "Arrow" or "Rectangle" or "Text" or "Mosaic" or "Spotlight" or "Counter")
        {
            s.LastAnnotationTool = _activeToolKey;
        }
    }

    public async System.Threading.Tasks.Task PlayOcrSuccessAnimation()
    {
        if (IconOcr == null) return;
        var origData = IconOcr.Data;
        
        IconOcr.Data = Avalonia.Media.Geometry.Parse("M 5 12 L 10 17 L 19 6");
        IconOcr.SetValue(Avalonia.Controls.Shapes.Shape.StrokeProperty, Avalonia.Media.Brushes.LimeGreen);

        await System.Threading.Tasks.Task.Delay(1000);

        IconOcr.Data = origData;
        IconOcr.ClearValue(Avalonia.Controls.Shapes.Shape.StrokeProperty);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_activeOverlay != null && _activeOverlay.TryGetTarget(out var active) && ReferenceEquals(active, this))
            _activeOverlay = null;
        FinishCaptureDiagnostics();
        ReleaseFirstCapturePreparationBitmap();
        _firstCapturePreparationTimeout?.Dispose();
        _firstCapturePreparationTimeout = null;
        _preparingFirstCapture = false;
        _firstCapturePreparation?.TrySetCanceled();
        // 先停止持久置顶，还原 WndProc。用 Opened 时缓存的句柄：此刻实时句柄多半已随窗口销毁变成 0，
        // 拿 0 去停会直接早退，守卫表里那条记录连同 1 秒定时器和子类化委托就永久留着了。
        IntPtr guardHwnd = _openedHwnd != IntPtr.Zero ? _openedHwnd : GetWindowHwnd();
        if (guardHwnd == IntPtr.Zero)
        {
            AppLogger.Warn("CaptureWindow.OnClosed: 无可用句柄，持久置顶守卫可能未被停止（TopmostGuards 泄漏）");
        }
        try { WindowHelper.StopEnforceTopmost(guardHwnd); } catch { }

        base.OnClosed(e);
        try
        {
            if (FrozenScreenImage.Source is IDisposable disposableSource)
            {
                disposableSource.Dispose();
            }
            FrozenScreenImage.Source = null;
        }
        catch { }

        try
        {
            Annotation.SourceBitmap = null;
        }
        catch { }
    }
}
