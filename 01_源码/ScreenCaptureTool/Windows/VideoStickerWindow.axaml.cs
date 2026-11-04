using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Recording;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Services;
using DrawingRectangle = System.Drawing.Rectangle;
// Avalonia.Controls.Path 与 System.IO.Path 同名，不显式别名必撞 CS0104；控件侧一律写全限定名
using Path = System.IO.Path;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 录屏预览贴图窗（计划 §3.9，稿 3 D12 乙案）：录完即钉屏的轻量视频贴图，
/// StickerWindow 9343 行保护域不派生、只取同构交互（置顶 / 指针拖动 / 初始=选区原位）。
/// ⚠ 2026-09-22：原先照搬的「关闭必红确认」已按用户要求删除——关闭即放弃，不再二次追问
/// （那枚"放弃"钮与本窗 ✕ 作用完全重复，同一个出口摆两次）。
///
/// 2026-09-22 起本窗换成<b>无边框画面卡＋卡下控制条</b>，播放引擎换成自研的
/// <see cref="FfmpegPlayerSurface"/>（ffmpeg 子进程按母带原始尺寸供帧 ＋ 本窗自绘 ＋ winmm waveOut 出声）。
/// 原先那一路 LibVLC（借子 HWND 自己画）整块删除：它换来换去只替得掉"放像"这一件活，
/// 而放像恰好 ffmpeg 自己就会，代价是背上 101MB 分发与一条"本机播放组件不可用"的降级分支。
/// 引擎与窗体之间只隔 <see cref="IPlayerSurface"/> 一层，帧/时钟/出声都不上 UI 线程。
/// </summary>
public partial class VideoStickerWindow : Window
{
    public bool UseSystemPlayback { get; set; }
    /// <summary>
    /// 窗宽基准＝max(画面卡宽, <b>正常态</b>工具条实测宽)，SetupPreview 现量。
    /// 条子随内容收缩，窗宽下限本就是它自己的宽度；确认组／转码行换上来时条子可能比基准宽，
    /// 由 <see cref="RepinWidth"/> 临时撑宽、退回正常态即收回。
    /// </summary>
    private double _baseWidthDip;

    /// <summary>根 Grid 为光晕与条上间距预留的四条值，与 .axaml 里 RowDefinitions/ColumnDefinitions
    /// 逐字对应（单一来源＝那两行，这里只是把同一组数取个名字给摆位算式用）。</summary>
    private const double ShadowSide = 20, ShadowTop = 14, ShadowBottom = 26, BarGap = 12;

    /// <summary>圆角参数为 0 时画面卡兜底用的半径（DIP）。<b>刻意不掉回直角</b>：直角卡配一条带投影的
    /// 圆角外框是"预览窗无边框化"那一版就否决过的长相（2026-09-24 用户口径「统一＋不掉回直角」）。</summary>
    private const double PlateRadiusFallback = 11;

    /// <summary>大选区判据：选区任一边 ≥ 所在屏工作区的这一比例，就不再 1:1 原位预览（2026-09-29）。
    /// 0.85：1080p 屏上 ≥ 1632 宽或 ≥ 877 高（工作区 1032）即判大——到这个量级 1:1 摆出来卡加条几乎铺满屏，
    /// 用户视线要满屏找播放钮；再往下（如 1440×810）原位预览仍留得出四周空隙，维持"与选区同大同位"。
    /// 录屏控制条的收边判据共用这一比例（<see cref="RecordControlBarWindow"/>），两处口径一致。</summary>
    internal const double LargeRegionRatio = 0.85;

    /// <summary>大选区画面相对录制区域的显示比例：宽高各缩小 15%，整窗放不下时再按工作区收口。</summary>
    private const double LargePreviewScale = 0.85;

    /// <summary>摆位用的工作区：选区所在那块屏的 rcWork（物理像素）。</summary>
    private System.Drawing.Rectangle _placeWork;

    /// <summary>画面卡内层的圆角（DIP）。描边那一圈由它减去半个线宽推出来 ⇒ 描边外沿与卡片轮廓同心；
    /// 外层那张宿主再 +1（与 .axaml 的 12/11 那道内外差同值），那个 +1 只喂 <c>BoxShadow</c> 的光晕，
    /// 可见轮廓另有其人＝<see cref="_plateOutline"/>。原先这里是常量 11、与 .axaml 的
    /// <c>CornerRadius="11"</c> 同值两份账，2026-09-24 起改为<b>与截图成品圆角同读一个参数</b>
    /// （见 <see cref="ComputePlateRadius"/>），卡面／裁剪／描边／遮罩洞四处的半径全部由本值派生
    /// ⇒ 卡片与遮罩洞不可能各算各的；四处还共用同一条超椭圆曲线（<see cref="SelectionVisuals.BuildRoundedOutline"/>），
    /// 只统一数字不统一曲线仍然对不上。
    /// 另有一个 <c>PlateStroke=1</c>（描边宽度）曾参与卡的宽高算式，2026-09-22 外框改画在画面之上后彻底删除：
    /// 那个值与 .axaml 的 <c>BorderThickness</c> 是"同一个数写两遍"，且粗细如今跟着设置走，根本不再是常量。</summary>
    public double PlateRadiusDip { get; private set; } = PlateRadiusFallback;

    /// <summary>卡片轮廓几何（超椭圆，SetupPreview 里随卡尺寸与半径现算一次）：画面裁剪与卡面底板共用这一份，
    /// 采样一次、两处同源。<b>不</b>含描边那圈的向内半线宽（那份在 <see cref="ApplyPlateFrame"/> 里另算）。</summary>
    private Geometry? _plateOutline;

    private string _masterPath = "";
    private string _sessionDir = "";
    private RecordFormat _format = RecordFormat.Mp4;
    /// <summary>本次录制所用画质预设 Id（导出 GIF/WebP/烙进度条时按它取帧率与码率）。</summary>
    private string? _presetId;
    private double _dpiScale = 1.0;
    private TimeSpan _recordedDur;

    private IPlayerSurface? _player;
    private DispatcherTimer? _poll;
    private bool _noPlayback;   // 引擎起不来：播放那一组整组收掉，只留保存/复制那几条不依赖播放的链路
    private bool _seekDragging; // 进度条按住拖动期间不接引擎回读的位置，否则播放头把滑块拽回去
    private double _seekDragRatio;
    private bool _closed;
    private bool _saved;
    private bool _encoding;
    private CancellationTokenSource? _exportCancellation;
    private string? _lastDestPath;
    private double _fallbackDurSec;
    /// <summary>Opened 时缓存的句柄（置顶守卫按它停）。</summary>
    private IntPtr _openedHwnd;

    /// <summary>本卡对应的那一扇录制遮罩窗（预览暗纱长在它上面）。由 <c>RecordFlow.ShowPreviewSticker</c>
    /// 在 Show 之前挂进来——与它把遮罩退场订在卡的 <c>Closed</c> 上，认的是同一份引用。</summary>
    private RecordOverlayWindow? _previewOverlay;

    /// <summary>转码期"我们自己把卡收进任务栏"的那一次：只有这段状态里才处理唤回，
    /// 免得第三方改动窗口状态时跟着乱翻样式位。</summary>
    private bool _minimizedByButton;

    /// <summary>挂进本卡对应的那一扇遮罩窗（用途见 <see cref="_previewOverlay"/>）。</summary>
    public void AttachPreviewOverlay(RecordOverlayWindow? overlay) => _previewOverlay = overlay;

    /// <summary>弹系统对话框期间暂停置顶（守卫＋Topmost 属性），Dispose 时按原样恢复。</summary>
    private IDisposable SuspendTopmostForDialog()
    {
        bool hadGuard = WindowHelper.IsEnforceTopmostActive(_openedHwnd);
        if (hadGuard) WindowHelper.StopEnforceTopmost(_openedHwnd);
        bool wasTop = Topmost;
        if (wasTop) Topmost = false;
        return new ActionOnDispose(() =>
        {
            if (_closed) return;
            if (wasTop) Topmost = true;
            if (hadGuard) WindowHelper.EnforceTopmost(_openedHwnd);
        });
    }

    private sealed class ActionOnDispose : IDisposable
    {
        private Action? _a;
        public ActionOnDispose(Action a) => _a = a;
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }

    public VideoStickerWindow()
    {
        InitializeComponent();
        ApplyCustomToolbar();

        // C 甲：时间读数去 Consolas 之后，必须按"本读数最宽那一档"预留宽度，否则 00:08→10:00、
        // 9%→100% 一进位整条就左右跳。宽度是现量出来的（度量禁手算），采样文字即最宽那一档。
        TxtPos.MinWidth = TxtDur.MinWidth = WidthOf(TxtPos, "00:00");
        TxtEncPct.MinWidth = WidthOf(TxtEncPct, "100%");
    }

    /// <summary>临时换上采样文字量一次宽再还原。走 <see cref="LayoutMetrics"/> 是为了拿到本帧的值——
    /// 直接 Measure 会吃到度量缓存（见该类的用途说明）。</summary>
    private static double WidthOf(TextBlock target, string sample)
    {
        string? keep = target.Text;
        target.Text = sample;
        double w = LayoutMetrics.MeasureNow(target).Width;
        target.Text = keep;
        return w;
    }

    /// <summary>RecordFlow 在录制 Completed 后调用（UI 线程、Show 之前）。
    /// ⚠ <paramref name="format"/> 目前**恒为 MP4**且已不被采用（导出格式改从设置里的 RecFormat 取，
    /// 理由见方法内注释）；形参先留着不删——<see cref="Services.RecordFlow"/> 那条调用链上它是
    /// "本次录制的容器格式"的语义位置，日后若真支持录 GIF master，这里就是接回去的地方。
    /// 用 _ = format 显式丢弃，免得编译器报未使用参数。
    /// <param name="masterW">母带画面物理宽（<c>RecordingSession.MasterSizePx</c> 一路传下来）。
    /// 画面卡的宽高比按它算，不按 <paramref name="regionPhys"/> 算——后者只用于摆位，
    /// 两者比例不等就是"预览底下一条白线"的成因，详见方法内那条注释。</param>
    /// <param name="masterH">母带画面物理高。<=0 视为没探到，本窗回落到选区尺寸。</param></summary>
    public void SetupPreview(string masterMp4, string sessionDir, DrawingRectangle regionPhys,
        RecordFormat format, string? presetId, TimeSpan recordedDuration, double dpiScale,
        int masterW, int masterH)
    {
        _ = format;
        _masterPath = masterMp4;
        _sessionDir = sessionDir;
        // 🟠 传进来的 format 目前**恒为 MP4**（RecordOptionsControl.BuildOptions 写死，
        //    录制引擎本体也始终录 MP4 master），真正决定默认展示哪一档的是设置里的导出格式。
        //    2026-09-24 分层：这里读的是「上次使用」（本窗那枚格式下拉写的就是它），没记过才回落
        //    到设置页的「默认格式」——否则设置页那项形同虚设（用户实报"很多设置下次打开还是默认状态"的一条）。
        _format = ParseFormat(AppSettings.Pick(AppSettings.Current.RecLastFormat, AppSettings.Current.RecFormat));
        _presetId = presetId;
        _recordedDur = recordedDuration;
        _fallbackDurSec = Math.Max(recordedDuration.TotalSeconds, 0.1);
        _dpiScale = dpiScale > 0 ? dpiScale : 1.0;

        ChkBurn.IsChecked = AppSettings.Current.RecBurnProgressBar;
        UpdateFormatUi();

        double scale = _dpiScale;
        // 画面卡尺寸＝**母带**物理尺寸 ÷ DPI，不是选区尺寸。两者可以不等：录制侧把 crop 取偶
        // （1429→1428），非"原画"档还会把高度降到 1080 等比缩放（1539×1249 → 1330×1080）。
        // 一旦拿选区摆卡，卡与画面的宽高比就差一点点 ⇒ Uniform 必然留一条 letterbox 缝，
        // 缝里露的是卡自己的浅底色 #FAFAFA ⇒ 现象就是"预览下面一条白线，有时候在上面"
        // （上下还是左右、有没有，全看那零点几像素落在哪一轴、往哪边取整——2026-09-24 逐段实测算过）。
        // 同比例还顺带消掉"右下角闪"：卡×RenderScaling 正好等于位图物理像素 ⇒ 不再每帧做非整数倍重采样，
        // 圆角那条反锯齿边的采样相位就不会逐帧漂。
        bool fromMaster = masterW > 0 && masterH > 0;
        if (!fromMaster)
            AppLogger.Warn("VideoStickerWindow: 母带尺寸没探到，画面卡回落到选区尺寸" +
                           "（可能露出一条底色缝）");
        // 🔴 卡的<b>尺寸</b>跟选区、<b>宽高比</b>跟母带（2026-09-29 用户实报"超过一定尺寸预览窗就变小了"）：
        // 旧写法尺寸也按母带取，而非"原画"档母带会等比降到 1080 高 ⇒ 选区一高过 1080 物理像素，卡就比选区小一圈。
        // 现在以选区宽为准、高按母带宽高比反推：卡与选区同大同位，比例又与母带逐像素一致（不露 letterbox 缝）。
        // 母带降采样的那一档只是预览时 GPU 放大一点，导出与复制照旧用母带原始像素。
        double aspect = fromMaster
            ? masterW / (double)masterH
            : Math.Max(1, regionPhys.Width) / (double)Math.Max(1, regionPhys.Height);
        double videoW = Math.Max(regionPhys.Width / scale, 120);
        double videoH = videoW / aspect;
        if (videoH < 90) { videoH = 90; videoW = videoH * aspect; }

        // 条子的高度由它自己给，先量出来才能算出画面卡还剩多少地方。
        Size bar = LayoutMetrics.MeasureNow(ToolbarOuter);
        // 按<b>选区所在那块屏</b>的工作区摆（扣掉任务栏，缩完工具条才真点得到）；并集包围盒在多屏下会把
        // "放得下"判宽松，副屏上的大选区照样撑出屏外。
        var (_, work) = MonitorHelper.GetMonitorAndWorkAreaForRegion(regionPhys);
        // 大选区判据（2026-09-29 用户口径"全屏录屏不要全屏预览，窗口大到体验变差就要缩"）：
        // 选区任一边 ≥ 所在屏工作区的 LargeRegionRatio ⇒ 不再 1:1 摆原位，画面按选区的 85% 居中显示。
        // 2026-10-11：比例作用于画面，不再先把整窗压进工作区的 62%，以免扣掉条子后画面只剩约五成。
        // 取"任一边"而不是面积：1900×600 这种横条，宽已顶满屏，1:1 摆出来卡与条仍横穿整屏。
        bool large = regionPhys.Width >= work.Width * LargeRegionRatio
                     || regionPhys.Height >= work.Height * LargeRegionRatio;
        double availW = (work.Width - ShadowSide * 2 * scale) / scale;
        double availH = (work.Height - (ShadowTop + BarGap + ShadowBottom) * scale) / scale - bar.Height;
        double k = Math.Min(large ? LargePreviewScale : 1.0, Math.Min(availW / videoW, availH / videoH));
        if (k > 0.05 && k < 1.0)
        {
            videoW *= k; videoH *= k;
            AppLogger.Info($"VideoStickerWindow: 选区 {regionPhys.Width}×{regionPhys.Height}" +
                           (large ? " 为大选区，预览缩到工作区中央" : " 放不下") +
                           $"，显示尺寸 {k:F2}×（母带仍按原始尺寸解码）");
        }
        _placeWork = work;

        // 卡的宽高＝画面宽高，一分不加（描边已挪到画面之上的独立一层，不再占卡片的布局尺寸）。
        double plateW = videoW, plateH = videoH;
        VideoPlate.Width = plateW;
        VideoPlate.Height = plateH;
        // 半径跟「截图圆角」设置现算，必须在卡尺寸定了之后、外框与裁剪之前算这一次。
        // .axaml 里那两枚字面值（12/11）只是设计期占位，运行时一律由这里覆盖。
        // ⚠ 两枚 Border 的 CornerRadius 如今**只喂 BoxShadow 那圈光晕**（正圆弧，投影是软的、差不到看出来）；
        //    卡片的<b>可见轮廓</b>由下面那枚超椭圆几何说了算（_plateOutline）——曲线必须与遮罩洞、截图选框
        //    同源，否则同一个半径摆出两套弧度（2026-09-24 用户实报"描边没跟随截图选区圆角"）。
        PlateRadiusDip = ComputePlateRadius(plateW, plateH);
        VideoPlate.CornerRadius = new CornerRadius(PreviewPlateRadiusDip);   // 外层宿主＝内层 + 1（内外圈固有差）
        VideoPlateInner.CornerRadius = new CornerRadius(PlateRadiusDip);
        _plateOutline = SelectionVisuals.BuildRoundedOutline(new Rect(0, 0, plateW, plateH), PlateRadiusDip);
        PlateBackdrop.Data = _plateOutline;
        ApplyPlateFrame();
        // 圆角必须由这里显式给一枚几何：矩形画面自己不会弯，四个方角会探出那道弧（用户实报"视频凸在
        // 圆角外"）。贴图侧之所以没这现象，是因为它把圆角烘进了位图；本窗的位图是每帧改写的解码帧，
        // 不能照抄那条路。
        // ⚠ 几何与卡面底板共用 _plateOutline 那一份 ⇒ 采样一次，两处不可能各算各的；
        //    且必须在卡尺寸定下来之后重给一次——几何是按局部坐标写死的，不会跟着控件尺寸走。
        // 🔴 这一枚是<b>全窗唯一</b>的轮廓裁剪：内层 Border 上不再挂 ClipToBounds（2026-09-25 根因，
        //    详见 .axaml 同处说明）——那枚 Border 的裁剪认 CornerRadius、且只按正圆弧算，会在超椭圆
        //    之上再削一刀，把画面与描边一起缩回正圆 ⇒ 卡是圆的、洞是超椭圆的、描边被削掉外半截，三条线。
        VideoArea.Clip = _plateOutline;

        // 窗宽窗高全部现量：光晕的四条余量写在根 Grid 的行列上。
        _baseWidthDip = Math.Max(plateW, bar.Width);
        Width = _baseWidthDip + ShadowSide * 2;
        Height = ShadowTop + plateH + BarGap + bar.Height + ShadowBottom;

        if (large)
        {
            // 大选区：整窗居中在选区所在屏的工作区里（卡＋条作为一个整体居中，不是只居中画面）
            int winW = (int)Math.Round(Width * scale), winH = (int)Math.Round(Height * scale);
            Position = new PixelPoint(work.X + (work.Width - winW) / 2, work.Y + (work.Height - winH) / 2);
        }
        else
        {
            // 初始位置=选区原位：窄画面会被工具条撑宽的窗体居中，必须扣掉画面真实偏移，
            // 不能只扣阴影边距；Position 为物理像素，布局偏移为 DIP。
            var offset = GetPlateOffset();
            Position = new PixelPoint(
                regionPhys.X - (int)Math.Round(offset.X * scale),
                regionPhys.Y - (int)Math.Round(offset.Y * scale));
        }
        ClampIntoWorkArea();

        Opened += OnOpened;
        // 从任务栏点回来的那一刻只有这一处能知道（本仓其余窗都靠 SizeChanged 反推）：订 WindowState，
        // 用于把置顶、守卫与预览暗纱一起恢复原样。
        PropertyChanged += OnStickerPropertyChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _exportCancellation?.Cancel();
            WindowHelper.StopEnforceTopmost(_openedHwnd);
            ReleasePlayer();
        };
    }

    /// <summary>
    /// 画面卡圆角（DIP）＝<b>与截图成品圆角同读一个参数</b>（<c>AppSettings.CaptureCornerRadius</c>，
    /// 2026-09-24 用户口径「统一＋不掉回直角」）：档位 → 物理像素（×4）→ 除以本窗 DPI，再钳到短边一半，
    /// 与截图层 <c>SelectionLayer.EffectRadiusDip</c> 是同一条算式，故"截图预览里看到的弧度"与"录完看到的卡片"
    /// 是同一个弧度。
    /// <para>⚠ 参数为 0 <b>不</b>回落到直角，回落 <see cref="PlateRadiusFallback"/>＝11：直角卡配一条带投影的
    /// 圆角外框是上一版已否决的长相，而"关掉圆角"在录屏这边的语义只是"别按设置里那个数加圆"，不是"必须见方角"。</para>
    /// </summary>
    private double ComputePlateRadius(double plateW, double plateH)
    {
        int param = SelectionEffects.NormalizeParam(AppSettings.Current.CaptureCornerRadius);
        double half = Math.Min(plateW, plateH) / 2;
        if (param == 0) return Math.Min(PlateRadiusFallback, half);
        return Math.Min(SelectionEffects.ParamToRadiusPx(param) / _dpiScale, half);
    }

    /// <summary>
    /// 画面卡外框：线型／颜色／粗细三项全部取「截图选框样式」设置，与截图选框共用
    /// <see cref="SelectionVisuals"/> 那一份构造器（改虚线段长只改那一处）。画在画面<b>之上</b>的独立一层，
    /// 所以圆角处不会再被矩形画面盖掉（用户实报"白线框只在 R 角缺"就是旧内层描边被自己的子元素压住）。
    /// ⚠ 粗细＝0 ⇒ 整层收起，与截图侧"0＝不画框"同一语义；半径仍取卡片自己的 <see cref="PlateRadiusDip"/>，
    /// 不吃「截图选框样式」那一项（外框是卡片的边，不是选区的边——半径跟卡片才不会脱离轮廓）。
    /// 🔴 但**曲线**必须与卡片同一条：本件与卡面底板、画面裁剪、遮罩洞四处一律走
    /// <see cref="SelectionVisuals.BuildRoundedOutline"/> 那枚超椭圆，一处换成 Rectangle/CornerRadius
    /// 就是"同一个半径两套弧度"（2026-09-24 用户实报即此）。
    /// </summary>
    private void ApplyPlateFrame()
    {
        var s = AppSettings.Current;
        int t = s.SelectionBorderThickness;
        IPen? pen = SelectionVisuals.BuildBorderPen(s.SelectionBorderStyle, s.SelectionBorderColorArgb, t);
        if (pen == null)
        {
            FrameOverlay.IsVisible = false;
            return;
        }

        FrameOverlay.IsVisible = true;
        FrameOverlay.Stroke = pen.Brush;
        FrameOverlay.StrokeThickness = t;
        FrameOverlay.StrokeDashArray = SelectionVisuals.BuildBorderDash(s.SelectionBorderStyle, t);
        // 路径矩形 inward 半个线宽：描边居中落在路径上 ⇒ 正好铺满最外 [0, t] 这一圈，一像素都不溢到卡外；
        // 半径同减半个线宽 ⇒ 描边外沿回到卡片自己的 PlateRadiusDip（§5.2 的内外同心档）。
        // 🔴 几何走 SelectionVisuals.BuildRoundedOutline（超椭圆），**不再**是 Rectangle 的 RadiusX/RadiusY
        // （正圆弧）：那与卡面底板／画面裁剪／遮罩洞那三处的曲线不是一条，同一个数并排看就是两套弧度。
        FrameOverlay.Margin = new Thickness(t / 2);
        FrameOverlay.Data = SelectionVisuals.BuildRoundedOutline(
            new Rect(0, 0, Math.Max(0, VideoPlate.Width - t), Math.Max(0, VideoPlate.Height - t)),
            Math.Max(0, PlateRadiusDip - t / 2));
    }

    /// <summary>画面在透明宿主内居中后的布局偏移，初始定位和换工具条时共用。</summary>
    private Point GetPlateOffset() => new((Width - VideoPlate.Width) / 2, ShadowTop);

    /// <summary>
    /// 把画面和工具条夹回<b>工作区</b>内，透明阴影可以越界，避免贴边选区被阴影推离原位。
    /// ⚠ 不能夹到虚拟屏：SetupPreview 是按工作区高度决定缩放的，夹屏会把"刚好放得下"的窗子
    /// 一路推到盖住任务栏（实测 1600×1076 母带缩到 0.87× 后底边仍落在 1080 &gt; 工作区 1032，工具条点不到）。
    /// </summary>
    private void ClampIntoWorkArea()
    {
        double scale = _dpiScale;
        var plateOffset = GetPlateOffset();
        double barWidth = LayoutMetrics.MeasureNow(ToolbarOuter).Width;
        // 工具条右对齐、画面居中：两者的并集才是需要留在工作区里的可操作内容。
        double contentLeft = Math.Min(plateOffset.X, Width - ShadowSide - barWidth);
        int left = (int)Math.Round(contentLeft * scale);
        int right = (int)Math.Round((Width - ShadowSide) * scale);
        int top = (int)Math.Round(ShadowTop * scale);
        int bottom = (int)Math.Round((Height - ShadowBottom) * scale);
        // 与 SetupPreview 算缩放用的是同一块工作区（选区所在那块屏）；没算过时退回并集
        var work = _placeWork.Width > 0 ? _placeWork : MonitorHelper.GetWorkAreaUnion();
        Position = new PixelPoint(
            Math.Clamp(Position.X, work.X - left, Math.Max(work.X - left, work.Right - right)),
            Math.Clamp(Position.Y, work.Y - top, Math.Max(work.Y - top, work.Bottom - bottom)));
    }

    /// <summary>
    /// 两态（正常／转码进度）内容可能比正常态宽：条子随内容收缩，就得跟着内容撑窗。
    /// 正常态一律收回基准宽，免得转一次码窗子永久变宽。
    /// ⚠ 2026-09-22：原「放弃确认」那一态已删（关闭即放弃），故这里只剩"动作组在场"与"转码行在场"两种。
    /// </summary>
    private void RepinWidth()
    {
        double need = ActionGroup.IsVisible
            ? _baseWidthDip
            : Math.Max(_baseWidthDip, LayoutMetrics.MeasureNow(ToolbarOuter).Width);
        if (Math.Abs(need - (Width - ShadowSide * 2)) < 0.5) return;
        var oldOffset = GetPlateOffset();
        Width = need + ShadowSide * 2;
        var newOffset = GetPlateOffset();
        // 转码／返回正常态改变宿主宽度时，补偿居中偏移，让画面留在原来的屏幕位置。
        Position = new PixelPoint(
            Position.X + (int)Math.Round(oldOffset.X * _dpiScale) - (int)Math.Round(newOffset.X * _dpiScale),
            Position.Y);
        ClampIntoWorkArea();
    }

    /// <summary>
    /// 动作组 ⇄ 转码行 的唯一切换口：换完按新内容实测宽度重钉窗宽。
    /// 正常态那三件（播放组／导出选择组／其后那根发丝线）跟着动作组一起进出：
    /// 转码是"这一条只说一件事"的时机，留着它们＝点不动的下拉加两条并排的轨。
    /// 播放组另加一道 <see cref="_noPlayback"/> 闸：引擎起不来时它本来就该一直收着。
    /// </summary>
    private void ShowActionGroup(bool show)
    {
        ActionGroup.IsVisible = show;
        PlayGroup.IsVisible = show && !_noPlayback;
        ExportGroup.IsVisible = show;
        SepActions.IsVisible = show;
        if (_customToolbar != null)
        {
            foreach (var control in _customToolbarControls)
                control.IsVisible = show && (!_customToolbarIds.TryGetValue(control, out string? id) || _customToolbar.Includes(id))
                    && (!ReferenceEquals(control, PlayGroup) || !_noPlayback);
            _customToolbar.SetPresented(show, id => id != "playback" || !_noPlayback);
        }
        RepinWidth();
    }

    private ToolbarLayoutView? _customToolbar;
    private Control[] _customToolbarControls = [];
    private readonly Dictionary<Control, string> _customToolbarIds = new();
    private void ApplyCustomToolbar()
    {
        var definition = Core.ToolbarScenes.Get(Core.ToolbarScene.VideoPreview);
        if (definition.Normalize(Core.AppSettings.Current.VideoPreviewToolbarLayout) == definition.DefaultLayout) return;
        var tools = (StackPanel)ActionGroup.Parent!;
        var map = new Dictionary<string, Control[]>
        {
            ["playback"] = [PlayGroup], ["export"] = [ExportGroup], ["copy"] = [BtnCopy],
            ["quicksave"] = [BtnQuickSave], ["saveas"] = [BtnSaveAs], ["close"] = [BtnClose]
        };
        ActionGroup.Children.Clear();
        tools.Children.Remove(ActionGroup); tools.Children.Remove(EncRow);
        if (PlayGroup.Children.LastOrDefault() is Border playbackDivider)
        {
            PlayGroup.Children.Remove(playbackDivider);
            tools.Children.Insert(1, playbackDivider);
        }
        _customToolbar = new ToolbarLayoutView(tools, Core.ToolbarScene.VideoPreview, map);
        _customToolbar.Apply(Core.AppSettings.Current);
        _customToolbarControls = tools.Children.ToArray();
        foreach (var pair in map) foreach (var control in pair.Value) _customToolbarIds[control] = pair.Key;
        // 空的命名组保留原状态哨兵；转码行不进入可编辑布局。
        tools.Children.Add(ActionGroup); tools.Children.Add(EncRow);
    }

    // ================= 生命周期 =================

    /// <summary>RecordFlow 经 <see cref="WindowPresentationHelper.ShowSmoothly"/> 的 gateReveal 交下来的
    /// "真正揭开"动作；null＝本窗没挂闸门（不是预览态拉起的那一路），<see cref="RevealNow"/> 直接返回。</summary>
    private Action? _revealGate;
    private bool _revealQueued;
    private bool _revealed;
    /// <summary>Opened 那一拍的 TickCount64：白闪这条链要的是"Show→内容就绪→揭开"三段间距，见 <see cref="RevealAfterFrame"/>。</summary>
    private long _showTick;

    /// <summary>ShowSmoothly 在 Show() 与 cloak 之后<b>同步</b>调用：把揭开动作寄存下来，
    /// 由本窗在首帧真的合成上屏之后再执行。交给我们的这一刻，窗已经盖着，什么都看不见。</summary>
    public void ArmRevealGate(Action reveal) => _revealGate = reveal;

    /// <summary>预览态那层暗纱要对准的<b>画面卡本体</b>（RecordFlow 转交遮罩窗，遮罩只读它的 Bounds
    /// 与所在窗 Position，不再自己算一遍卡的摆位＝一份算式）。取外层 <c>VideoPlate</c>：它就是卡的轮廓
    /// 那一圈（带投影），内层那张是它的子元素、尺寸相同。
    /// <para>⚠ 洞的半径必须同读 <see cref="PlateRadiusDip"/>：洞是直角而卡是圆角，四个角缝里就会漏出
    /// 桌面，用户报的正是这个（"预览下遮罩是直角的，能不能跟截图的圆角统一"）。</para></summary>
    public Control PreviewPlate => VideoPlate;

    /// <summary>外层 <c>VideoPlate</c> 那一圈的圆角（DIP）＝内层 <see cref="PlateRadiusDip"/> + 1（那道内外差）。
    /// <b>只喂 <c>BoxShadow</c> 那圈光晕</b>——光晕是软的，正圆弧与超椭圆之差看不出来。
    /// <para>🔴 暗纱的洞<b>不</b>读这一枚，读 <see cref="PlateRadiusDip"/>：洞与卡同矩形时，"半径 +1"
    /// 不是往外扩而是<b>往里切得更多</b>（超椭圆角部离矩形角点的距离＝0.1295×半径，半径越大越远离角点）
    /// ⇒ 洞反而比卡小 0.13 DIP。直边贴平、四角啃进卡身，是"同族缩放"那一类错（同 2026-09-06 成品阴影
    /// 的病根）。要真给暗纱留余量得把洞的<b>矩形</b>一起外扩，而外扩会在卡的直边外露出一圈没压暗的桌面
    /// ⇒ 不要余量，洞与卡同一条曲线。</para></summary>
    public double PreviewPlateRadiusDip => PlateRadiusDip + 1;

    /// <summary>揭开一次即失效：失败分支、关窗、保险定时器与首帧这四路谁先到都算数，后到的空转。</summary>
    private void RevealNow()
    {
        if (_revealed) return;
        _revealed = true;
        Action? gate = _revealGate;
        _revealGate = null;
        gate?.Invoke();
    }

    /// <summary>首帧贴上屏后再揭开。<c>InvalidateVisual()</c> 只是"请求重画"，还得等下一帧真的合成完；
    /// 不等这一格就是"揭开时画面卡还是空的"，白闪照旧。每帧都会调到这里，第二帧起直接返回。</summary>
    private void RevealAfterFrame()
    {
        if (_revealed || _revealQueued) return;
        _revealQueued = true;
        // 三个时刻的间距（完成＝RecordFlow 那条、内容就绪与揭开＝这两条）是这条链唯一的现场证据：
        // 白闪到底是"引擎慢"还是"揭开没等引擎"，看这两个数就分得开。TickCount64 本机步进约 15.6ms，够用。
        AppLogger.Info("VideoStickerWindow: 内容就绪，距 Show " + SinceShow() + "ms，再排一帧后揭开");
        RequestAnimationFrame(_ =>
        {
            RevealNow();
            AppLogger.Info("VideoStickerWindow: 已揭开 cloak，距 Show " + SinceShow() + "ms");
        });
    }

    private long SinceShow() => Environment.TickCount64 - _showTick;

    private async void OnOpened(object? sender, EventArgs e)
    {
        _showTick = Environment.TickCount64;
        // 持久置顶（2026-09-29）：预览卡连同遮罩窗的暗纱都要压在第三方置顶窗之上，否则被顶下去后
        // 暗纱照样盖着整屏、卡却被别的窗挡住。句柄在这里缓存，Closed 按它停（实时取多半已是 0）。
        _openedHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_openedHwnd != IntPtr.Zero) WindowHelper.EnforceTopmost(_openedHwnd);
        var player = new FfmpegPlayerSurface(UseSystemPlayback);
        _player = player;
        bool ok = false;
        try
        {
            ok = await player.InitAsync(_masterPath, AppSettings.Current.RecPreviewLoop);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("VideoStickerWindow: 预览引擎异常：" + ex.Message);
        }
        if (_closed) return;   // 用户在探测途中就把窗关了：别往下挂图、别起轮询

        if (!ok || player.Output == null)
        {
            ShowNoPlayback(player.LastError ?? "未知原因");
            // 没有首帧可等：那一层"不可用"说明本身就是内容，等它画完这一格就揭开，别压到保险那一下
            RevealAfterFrame();
            return;
        }
        PlayerHost.Source = player.Output;
        // 帧缓冲就地改写、Source 不换实例，Avalonia 便不再重传纹理 ⇒ 屏上一片黑。每贴一帧重画一次（同长截图预览窗）
        player.FramePresented += () =>
        {
            PlayerHost.InvalidateVisual();
            RevealAfterFrame();
        };
        player.Play();

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _poll.Tick += (_, _) => PollTick();
        _poll.Start();
    }

    /// <summary>引擎起不来：整条播放组收起（按钮＋读数＋进度轨一起消失，不是四枚各藏一遍，
    /// 藏完还在条上留一根没有对家的发丝线）。如实说明原因。保存/复制/另存为不依赖播放，一律留着。</summary>
    private void ShowNoPlayback(string reason)
    {
        _noPlayback = true;
        PlayGroup.IsVisible = false;
        TxtUnavailable.Text = reason + "（文件仍在，可直接保存或复制）";
        UnavailablePanel.IsVisible = true;
        AppLogger.Warn("VideoStickerWindow: 预览不可用——" + reason);
        RepinWidth();
    }

    private void PollTick()
    {
        IPlayerSurface? p = _player;
        if (p == null || _closed || _noPlayback || _seekDragging) return;
        try
        {
            long tMs = p.GetTimeMs();
            long lenMs = p.GetLengthMs();
            double durSec = lenMs > 0 ? lenMs / 1000.0 : _fallbackDurSec;
            double posSec = Math.Max(tMs / 1000.0, 0);
            TxtPos.Text = FormatClock(posSec);
            TxtDur.Text = FormatClock(durSec);
            DrawSeek(durSec > 0 ? Math.Clamp(posSec / durSec, 0, 1) : 0);
            bool playing = p.IsPlaying;
            IcoPlay.IsVisible = !playing;
            IcoPause.IsVisible = playing;
        }
        catch { /* 对面是原生句柄与子进程，任何异常都不该掀翻窗体 */ }
    }

    /// <summary>画进度条的填充与滑块。宽度只读控件自身：首拍 Bounds 还是 0 时这一拍不画，
    /// 200ms 后的下一拍自然补上（原先这里写死按 180 兜底，等于把 XAML 的值又抄一遍）。</summary>
    private void DrawSeek(double ratio)
    {
        double trackW = SeekTrack.Bounds.Width;
        if (trackW <= 0) return;
        SeekFill.Width = ratio * trackW;
        SeekThumb.Margin = new Thickness(ratio * trackW - SeekThumb.Bounds.Width / 2, 0, 0, 0);
    }

    /// <summary>分秒一律零填充：读数恒为 5 字符，与构造里预留的 MinWidth 同宽，进位时条子不抖。</summary>
    private static string FormatClock(double sec)
    {
        int s = (int)Math.Round(sec);
        return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    // ================= 播放控制 =================

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => _player?.Toggle();

    /// <summary>
    /// 拖动只认"松手那一下"再跳：实时 seek 会让 ffmpeg 每挪一像素换一次进程（实测一次 95~125ms），
    /// 拖一次＝几十次换进程。按下与移动只画预览，松手才真跳。
    /// ⚠ 必须 e.Handled：本窗没有标题栏了，视频区按下＝拖窗（见 OnPointerPressed），
    /// 进度条在工具条里，事件一冒泡就到窗上——不截住的话"拖进度条"变成"拖窗口"。
    /// </summary>
    private void OnSeekPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_player == null || _noPlayback || _encoding) return;
        _seekDragging = true;
        e.Pointer.Capture(SeekTrack);
        e.Handled = true;
        UpdateSeekPreview(e);
    }

    private void OnSeekMoved(object? sender, PointerEventArgs e)
    {
        if (!_seekDragging) return;
        UpdateSeekPreview(e);
    }

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        e.Pointer.Capture(null);
        CommitSeek();
    }

    /// <summary>指针被别处抢走（弹出菜单／失焦等）＝放弃这次拖动，不跳到半道上的位置。</summary>
    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        PollTick();
    }

    private void UpdateSeekPreview(PointerEventArgs e)
    {
        double trackW = SeekTrack.Bounds.Width;
        if (trackW <= 0) return;
        _seekDragRatio = Math.Clamp(e.GetPosition(SeekTrack).X / trackW, 0, 1);
        double durSec = DurationSec;
        TxtPos.Text = FormatClock(_seekDragRatio * durSec);
        DrawSeek(_seekDragRatio);
    }

    private double DurationSec
    {
        get
        {
            long lenMs = _player?.GetLengthMs() ?? 0;
            return lenMs > 0 ? lenMs / 1000.0 : _fallbackDurSec;
        }
    }

    private void CommitSeek()
    {
        if (_player == null || _noPlayback) return;
        _player.SeekTo((long)(_seekDragRatio * DurationSec * 1000));
        PollTick();
    }

    // ================= 格式下拉（D16） =================

    /// <summary>
    /// 把当前格式铺到按钮文字与菜单条目：当前档只挂 <c>MenuItem.current</c> 染品牌紫（§6.12 禁止拼 ✓），
    /// 超 30 秒时给 GIF 那档挂 <c>MenuItem.warn</c> 让右侧提示列变危险红。
    /// </summary>
    private void UpdateFormatUi()
    {
        TxtFormat.Text = _format switch { RecordFormat.Gif => "GIF", RecordFormat.WebP => "WebP", _ => "MP4" };
        SetCurrentMenu(MiFmtMp4, _format == RecordFormat.Mp4);
        SetCurrentMenu(MiFmtGif, _format == RecordFormat.Gif);
        SetCurrentMenu(MiFmtWebP, _format == RecordFormat.WebP);
        // 首版无裁剪工具，>30s 选 GIF 只警示不禁用（计划 §3.11）
        bool gifOver = _recordedDur.TotalSeconds > 30;
        TxtFmtGifSub.Text = gifOver ? "超30秒 · 建议裁剪" : "≤30s · 无声";
        if (gifOver) MiFmtGif.Classes.Add("warn"); else MiFmtGif.Classes.Remove("warn");
        // 进度条勾选三种格式都吃（2026-09-24 用户要求）：烙条链在 ExportService 里与 GIF/WebP 的
        // 调色板链串成同一条滤镜图一遍跑完。此前这里按 _format 禁掉这枚勾选，因为那时只有 MP4 分支
        // 认它——"能点但什么都不发生"确实是条子上最脏的一种展示，但解法是让它真的生效，不是长期禁用。
    }

    private static void SetCurrentMenu(MenuItem item, bool current)
    {
        if (current) item.Classes.Add("current"); else item.Classes.Remove("current");
    }

    /// <summary>设置里的格式字符串 → 枚举（认不出的值一律回 MP4，与设置页载入时的容错口径一致）。</summary>
    private static RecordFormat ParseFormat(string? name) => name switch
    {
        "Gif" => RecordFormat.Gif,
        "WebP" => RecordFormat.WebP,
        _ => RecordFormat.Mp4,
    };

    private void SetFormat(RecordFormat f)
    {
        _format = f;
        UpdateFormatUi();
    }

    /// <summary>
    /// 格式选择回写设置，写的是<b>上次使用</b>族 <c>RecLastFormat</c>（2026-09-24 分层）。
    /// 此前这一句写的是 <c>RecFormat</c>，也就是设置页那枚「默认格式」本身 ⇒ 在预览窗换过一次 GIF，
    /// 设置页的默认档就跟着变了，两条入口共用一个孔（用户口径：「修改跟设置里面要区别开来，
    /// 设置里面是默认设置，后续可能还会修改」）。现在预览窗只记"上次用哪个"，默认值归设置页独占。
    /// 失败只记日志：存不下设置不该拦着用户选格式。
    /// </summary>
    private static void PersistFormat(RecordFormat f)
    {
        try
        {
            var s = AppSettings.Current;
            string name = f switch { RecordFormat.Gif => "Gif", RecordFormat.WebP => "WebP", _ => "Mp4" };
            if (s.RecLastFormat == name) return;   // 铺初值不走这里，但比对留着免得白盘整份 ini
            s.RecLastFormat = name;
            s.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("VideoStickerWindow: 保存导出格式失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 进度条勾选回写设置。<c>RecBurnProgressBar</c> 同样是**只读不写**：预览窗开窗时读它铺初值，
    /// 用户勾了以后从不落盘 ⇒ 下次开窗又回到设置页那个值（设置页连这个键的控件都没有）。
    /// 本键的定位就是"跨会话记忆"（AppSettings 里写明了），所以勾动即落盘。
    /// 🟠 注意别被 <c>SetupPreview</c> 里那次赋初值带偏：赋初值会触发 Checked/Unchecked，
    /// 故 <see cref="PersistBurn"/> 内部先比对再写，且值没变就直接返回（不产生多余落盘）。
    /// </summary>
    private void OnBurnChanged(object? sender, RoutedEventArgs e)
        => PersistBurn(ChkBurn.IsChecked == true);

    private static void PersistBurn(bool value)
    {
        try
        {
            var s = AppSettings.Current;
            if (s.RecBurnProgressBar == value) return;
            s.RecBurnProgressBar = value;
            s.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("VideoStickerWindow: 保存进度条勾选失败：" + ex.Message);
        }
    }

    private void OnFormatItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse<RecordFormat>(name, true, out var f))
        {
            SetFormat(f);
            PersistFormat(f);
        }
    }

    // 🔴 2026-09-22 第三轮整改：主／次按钮互换（ApplyPrimarySwap / MakeIcon）整段删除——
    //    用户实报"不需要紫色的按钮，请直接复用（截图工具栏的快速保存/另存为）"。
    //    动作组改为四枚平级 toolIcon（复制 / 快速保存 / 另存为 / ✕），几何在 XAML 里静态写死、
    //    随主题上色，不再有任何 code-behind 换图标/换提示的动态皮。设置页"预览主按钮"
    //    互换选项与 RecPreviewPrimarySave 键同一轮删除。

    // ================= 保存 / 另存为 / 复制 =================

    private async void OnQuickSaveClick(object? sender, RoutedEventArgs e) => await QuickSaveAsync();

    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopyAsync();

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    /// <summary>快速保存的落盘目录：生效值由 <c>AppSettings.ResolveRecordSaveDir</c> 那一份算式给（留空／目录已不在＝用本机默认 视频\yuSnip 录屏，稿3/计划 §3.11），本窗只补"缺目录就建出来"这一步。</summary>
    private static string DefaultSaveDir()
    {
        string dir = AppSettings.ResolveRecordSaveDir(AppSettings.Current.RecSaveDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SuggestedName(RecordFormat format)
        => "录屏_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ExportService.ExtensionFor(format);

    private async Task QuickSaveAsync()
    {
        if (_encoding) return;
        string dest = Path.Combine(DefaultSaveDir(), SuggestedName(_format));
        await SaveCoreAsync(dest, notify: true);
    }

    private async Task SaveAsAsync()
    {
        if (_encoding) return;
        try
        {
            var sp = StorageProvider;
            if (sp == null) return;
            // 系统另存为对话框不是置顶窗：守卫挂着时它的 WM_WINDOWPOSCHANGING 拦截会把卡抢回对话框之上，
            // 对话框被卡片和暗纱压住、点不到。对话框期间先停守卫、降 Topmost（走属性，不裸调 NOTOPMOST，
            // 见 protection.md 置顶条），结束后按原样恢复。
            using var _ = SuspendTopmostForDialog();
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "录屏另存为",
                SuggestedFileName = SuggestedName(_format),
                FileTypeChoices = new[]
                {
                    // 参考项目 Filter 文案原样继承（稿 3 D13）
                    new FilePickerFileType("MP4 视频") { Patterns = new[] { "*.mp4" } },
                    new FilePickerFileType("GIF 动图") { Patterns = new[] { "*.gif" } },
                    new FilePickerFileType("WebP 动图") { Patterns = new[] { "*.webp" } },
                },
            });
            string? path = file?.Path.LocalPath;
            if (string.IsNullOrEmpty(path)) return;
            if (!path.EndsWith(ExportService.ExtensionFor(_format), StringComparison.OrdinalIgnoreCase))
            {
                // 用户在对话框里改了扩展名不跟随格式时，以当前格式为准补正确扩展名（转码产物名实相符）
                path = Path.ChangeExtension(path, ExportService.ExtensionFor(_format).TrimStart('.'));
            }
            await SaveCoreAsync(path, notify: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("VideoStickerWindow: 另存为失败：" + ex.Message);
        }
    }

    /// <summary>统一保存出口：MP4 无烙印 = 母带直拷秒存；GIF/WebP/烙印 MP4 = D17 工具条内进度转码。</summary>
    private async Task SaveCoreAsync(string dest, bool notify)
    {
        if (_encoding) return;
        _lastDestPath = dest;

        if (ExportService.IsPassthroughMp4(_format, ChkBurn.IsChecked == true))
        {
            try
            {
                File.Copy(_masterPath, dest, true);
                OnSaved(dest, notify);
            }
            catch (Exception ex)
            {
                StickerToastWindow.Show("保存失败：" + ex.Message);
            }
            return;
        }

        string label = _format == RecordFormat.Gif ? "正在转换为 GIF…"
                     : _format == RecordFormat.WebP ? "正在转换为 WebP…"
                     : "正在添加进度条…";
        EnterEncodingMode(label);
        // Windows 母带的 GIF（含烙条）走系统编码；WebP 等转换按需下载 FFmpeg。
        if (ExportService.RequiresFfmpeg(_format, ChkBurn.IsChecked == true, UseSystemPlayback)
            && FfmpegLocator.TryFind(AppSettings.Current.RecFfmpegPath) == null)
        {
            bool ready;
            try
            {
                using var suspended = SuspendTopmostForDialog();
                ready = await DependencyGate.EnsureAsync(ScreenCaptureTool.Core.Deps.DepIds.Ffmpeg);
            }
            catch (Exception ex)
            {
                ExitEncodingMode(); ShowEncodingFailure("FFmpeg 组件准备失败：" + ex.Message); return;
            }
            if (!ready || _closed) { if (!_closed) ExitEncodingMode(); return; }
        }
        var progress = new Progress<double>(r =>
        {
            if (_closed) return;
            TxtEncPct.Text = (int)Math.Round(r * 100) + "%";
            // 填充比例换成实测轨宽：原先乘的是 XAML 里那枚 150，两处各写一份必然对不上
            double trackW = EncTrack.Bounds.Width;
            if (trackW > 0) EncFill.Width = Math.Clamp(r, 0, 1) * trackW;
        });
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        string? err;
        try
        {
            err = await ExportService.ExportAsync(_masterPath, dest, _format,
                ChkBurn.IsChecked == true, _presetId, progress, AppSettings.Current.RecFfmpegPath,
                cancellation.Token, useWindowsGif: UseSystemPlayback);
        }
        finally { _exportCancellation = null; }
        if (_closed) return;
        ExitEncodingMode();
        if (err != null)
        {
            ShowEncodingFailure(err);
            return;
        }
        OnSaved(dest, notify);
    }

    private void OnSaved(string dest, bool notify)
    {
        _saved = true;
        AppLogger.Info("VideoStickerWindow: 已保存 " + dest);
        try { TempSpace.DeleteDelayed(_sessionDir); } catch { } // D22：保存成功 30s 后清临时目录
        if (notify)
        {
            try { new SavedNotificationWindow(dest, "视频已保存").Show(); }
            catch (Exception ex) { AppLogger.Warn("VideoStickerWindow: 保存通知弹出失败：" + ex.Message); }
        }
        // 2026-09-24 用户要求：复制／快速保存／另存为 三条链路落定即结束预览，不用再手动点 ✕。
        // 三个动作都汇到这一个出口，所以退场只写在这里一次。走 Close() 而不走 OnCloseClick：
        // _saved 已为真，语义是"已保存的退场"，既不该再判 _encoding，也不该 DeleteNow
        // （延时清理上面那句已经排过，立刻删会把刚落盘的文件所在的临时目录连锅端）。
        // ⚠ 转码失败不到这一句（SaveCoreAsync 里 ShowEncodingFailure 后直接 return），窗体留着给重试。
        Close();
    }

    // ================= 复制（CF_HDROP，§3.10） =================

    private async Task CopyAsync()
    {
        if (_encoding) return;
        // 复制到临时「剪贴板工作区」再挂文件列表；残留由启动清扫（rec\* 超 24h）兜底
        string dir = Path.Combine(TempSpace.Root, "clipboard");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, SuggestedName(_format));
        await SaveCoreAsync(dest, notify: false);
        if (_lastDestPath != dest || !_saved) return; // 保存失败／取消不复制不存在的文件。

        try
        {
            Win32Clipboard.WriteFileDrop(new[] { dest });
            StickerToastWindow.Show("视频文件已复制到剪贴板");
        }
        catch (Exception ex)
        {
            StickerToastWindow.Show("复制失败：" + ex.Message); // 计划：失败 toast，不静默
        }
    }

    // ================= D17 转码进度态 =================

    private void EnterEncodingMode(string label)
    {
        _encoding = true;
        TxtEncLabel.Text = label;
        TxtEncPct.Text = "0%";
        EncFill.Width = 0;
        EncFailRow.IsVisible = false;
        EncRow.IsVisible = true;
        ShowActionGroup(false);   // 收动作组放在新行亮出来之后：RepinWidth 量的才是换完以后的条宽
        // 画面冻结当前帧（稿3 D17：先 Pause 再转）。其余控件不靠"禁用"，整组已随 ShowActionGroup 收起
        try { _player?.Pause(); } catch { }
    }

    private void ExitEncodingMode()
    {
        _encoding = false;
        EncRow.IsVisible = false;
        ShowActionGroup(true);
    }

    private void ShowEncodingFailure(string error)
    {
        // 失败不走静默：留在工具条内红字 + [重试] [改存 MP4]（稿3 D23）。
        // 卡要是被收在任务栏里，那些钮按不到 ⇒ 先把窗叫回台前再摆红字。
        RestoreFromTaskbar();
        TxtEncLabel.Text = "";
        TxtEncPct.Text = "";
        EncFailRow.IsVisible = true;
        TxtEncErr.Text = "转换失败：" + (error.Length > 120 ? error.Substring(0, 120) + "…" : error);
        EncRow.IsVisible = true;
        ShowActionGroup(false);
    }

    private void OnEncRetryClick(object? sender, RoutedEventArgs e)
    {
        string dest = _lastDestPath ?? Path.Combine(DefaultSaveDir(), SuggestedName(_format));
        ExitEncodingModeSilently();
        _ = SaveCoreAsync(dest, notify: true);
    }

    private void OnEncMp4Click(object? sender, RoutedEventArgs e)
    {
        string dest = (_lastDestPath ?? Path.Combine(DefaultSaveDir(), SuggestedName(_format)));
        dest = Path.ChangeExtension(dest, "mp4");
        ExitEncodingModeSilently();
        _format = RecordFormat.Mp4;
        UpdateFormatUi();
        PersistFormat(RecordFormat.Mp4);   // 改存 MP4 同样更新默认格式（用户明显不再想要那个转码格式）
        _ = SaveCoreAsync(dest, notify: true); // 母带直拷 = 秒存（MP4 高画质档快速封装，计划 §3.11）
    }

    private void ExitEncodingModeSilently()
    {
        // 「重试」「改存 MP4」都从这条走：卡若在任务栏里，先回台前再重跑，否则用户看不到新一次的进度
        RestoreFromTaskbar();
        _encoding = false;
        EncFailRow.IsVisible = false;
        ExitEncodingMode();
    }

    // ================= 转码期最小化到任务栏（2026-09-29） =================

    /// <summary>
    /// 把这张无边框卡收进任务栏：停置顶守卫 → 摘 <c>Topmost</c> → 走系统最小化。任务栏那枚图标是<b>常驻</b>的
    /// （<c>axaml</c> 根上 <c>ShowInTaskbar="True"</c>），点它就回到台前。
    /// <para>🔴 别再改回"临时补 <c>WS_EX_APPWINDOW</c> 放按钮"：<c>ShowInTaskbar=False</c> 在 Avalonia 里是
    /// "挂到一个隐藏父窗 ＋ 清这一枚样式位"<b>两件事</b>（本仓 <c>StickerWindow</c> 早就量过），只补样式位
    /// 并不把窗从那个隐藏父窗上摘下来，2026-09-29 实测就是"按钮点得动、任务栏没有图标"＝收进没有出口的后台，
    /// 无边框卡又没有标题栏和托盘入口，用户只能等转码自己跑完。要按钮就必须在框架侧改 <c>ShowInTaskbar</c>。</para>
    /// <para>窗<b>不关也不 Hide</b>：关了会连带触发 <c>RecordFlow</c> 订在卡的 <c>Closed</c> 上收遮罩那条链，
    /// 等于把整段预览生命周期结束掉；转码任务照旧在后台跑完。</para>
    /// <para>预览暗纱与那台每 500ms 的 Z 序定时器<b>跟着一起收</b>：暗纱长在遮罩窗上而不是卡上，
    /// 卡都不见了还留着它，屏幕上就是一圈压暗加中间一块没有窗的亮矩形。⚠ "收"这一步必须由遮罩窗那边
    /// <b>连闸门一起落</b>（见 <c>RecordOverlayWindow.SetPreviewMaskVisible</c>）——只把可见性置 <c>false</c>
    /// 会被最小化那一刻卡的位置变更触发的跟随钩子重新点回来，2026-09-29 用户实报<b>「点击最小化，遮罩没有消失」</b>就是这个。</para>
    /// </summary>
    private void OnEncMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_closed || _minimizedByButton) return;
        if (WindowState == WindowState.Minimized) return;

        WindowHelper.StopEnforceTopmost(_openedHwnd);   // 它有 1 秒兜底重断言，不停就会把最小化的窗拽回来
        Topmost = false;
        _minimizedByButton = true;
        _previewOverlay?.SetPreviewMaskVisible(false);
        WindowState = WindowState.Minimized;
        AppLogger.Info("VideoStickerWindow: 转码期已最小化到任务栏（hwnd=0x" + _openedHwnd.ToInt64().ToString("X") + "）");
    }

    /// <summary>从任务栏回到台前：恢复置顶与守卫、暗纱按<b>当前</b>几何重算再亮回来。
    /// 手动点任务栏、转码失败、点重试／改存 MP4 四条路都汇到这一个方法。</summary>
    private void RestoreFromTaskbar()
    {
        if (!_minimizedByButton) return;
        _minimizedByButton = false;
        if (_closed) return;

        Topmost = true;
        WindowHelper.EnforceTopmost(_openedHwnd);
        _previewOverlay?.SetPreviewMaskVisible(true);
        Activate();
        AppLogger.Info("VideoStickerWindow: 已从任务栏回到台前");
    }

    private void OnStickerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;
        if (_minimizedByButton && WindowState == WindowState.Normal) RestoreFromTaskbar();
    }

    // ================= 关闭（＝放弃，D10 简化） =================

    /// <summary>
    /// 关闭本窗。<b>关闭即放弃</b>——不再有第二枚"放弃录制"钮、也不再有"放弃后已录内容将不保存"的确认条
    /// （2026-09-22 按用户要求收口：两个按钮作用完全重复，同一个出口摆两次；确认那一问也一并去掉）。
    ///
    /// <para>清理口径跟着简化：<b>只有"已保存过"才让临时目录随 <c>TempSpace.DeleteDelayed</c> 走延时清理，
    /// 其余一律立即清掉</b>。此前是"点了确认条上的放弃才立即清"，现在没有确认这一步，
    /// 关窗＝＝放弃，所以未保存就关＝立即清。已保存那次在 OnSaved 里已经安排过延时清理，
    /// 这里不重复删（否则刚钉出的预览窗一关，用户还没去拿那个文件就没了）。</para>
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_encoding) return; // 转码中不给退，防止半截产物
        if (!_saved)
        {
            try { TempSpace.DeleteNow(_sessionDir); } catch { } // 未保存 = 放弃，立即清（D22）
        }
        Close();
    }

    // ================= 键盘 =================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Escape:
                OnCloseClick(null, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Space when !_noPlayback:
                _player?.Toggle();
                e.Handled = true;
                break;
            case Key.S when ctrl && shift:
                _ = SaveAsAsync();
                e.Handled = true;
                break;
            case Key.S when ctrl:
                _ = QuickSaveAsync();
                e.Handled = true;
                break;
            case Key.C when ctrl && shift:
                _ = CopyAsync();
                e.Handled = true;
                break;
            // Ctrl+Del 与 Esc 现在同一条路（都是"关闭＝放弃"）。保留这个键位是因为老用户可能习惯它。
            case Key.Delete when ctrl:
                OnCloseClick(null, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    // ================= 拖动（贴图心智：无边框后整张画面卡就是拖动手柄） =================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // 条上的按钮各自把按下事件吃掉，冒不到这里；进度条那枚在 OnSeekPressed 里显式 Handled，
        // 否则"拖进度条"会变成"拖窗口"。
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
        base.OnPointerPressed(e);
    }

    // ================= 资源释放 =================

    private void ReleasePlayer()
    {
        _poll?.Stop();
        _poll = null;
        try { _player?.Dispose(); } catch { }
        _player = null;
    }
}
