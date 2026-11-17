using System;
using Avalonia.Media;

namespace ScreenCaptureTool;

/// <summary>
/// 贴图阴影样式的固定值（本次按需求硬编码）。
/// 预留为可扩展数据结构：将来要做设置项时，把这些常量改成属性 + 设置 UI 即可。
///
/// 需求规格（经用户确认调整为四周均匀）：
/// - 偏移 (0, 0)，模糊半径 12px，不透明度 35%（柔和的四周环绕阴影）
/// - 焦点阴影色 #1E90FF（蓝），非焦点 #555555（灰黑）
///
/// 数值由 <see cref="Controls.StickerShadowLayer"/> 消费（自绘等距偏移环），**不是** <c>BoxShadow</c>：
/// 贴图的圆角是超椭圆，<c>BoxShadow</c> 只能画正圆弧，四角会缺一圈光晕（见该控件的类注释）。
/// </summary>
public static class StickerStyling
{
    public const double ShadowBlurRadius = 12;

    /// <summary>光晕峰值不透明度。2026-09-29 按用户要求加深（0.35 → 0.60）：原值在深色桌面上几乎看不出，
    /// 浅色桌面上也只是一层淡雾，状态色（焦点蓝/锁定橙/穿透绿）靠它表达，必须两种底色下都一眼可辨。
    /// 两条呈现通路（Avalonia 的 StickerShadowLayer、分层呈现的象限片）共用本值，改一处两处同步。</summary>
    public const double ShadowOpacity = 0.60;

    // 状态色同轮加深饱和（2026-09-29）：原色在浅底上偏淡、在深底上又与壁纸混色。
    // 取中高亮度、高饱和的一档——深底上靠"亮"跳出来，浅底上靠"饱和"压得住。
    public const string FocusedShadowColorHex = "#0A6CFF";
    public const string UnfocusedShadowColorHex = "#3A3A3A";
    public const string LockedShadowColorHex = "#FF8A00";
    public const string PenetrationShadowColorHex = "#1FBF3A";

    /// <summary>焦点状态阴影色（蓝色）。</summary>
    public static Color FocusedShadowColor { get; } = Color.Parse(FocusedShadowColorHex);

    /// <summary>非焦点状态阴影色（灰黑色）。</summary>
    public static Color UnfocusedShadowColor { get; } = Color.Parse(UnfocusedShadowColorHex);

    /// <summary>锁定状态阴影色（橙色）。</summary>
    public static Color LockedShadowColor { get; } = Color.Parse(LockedShadowColorHex);

    /// <summary>鼠标穿透状态阴影色（绿色）。</summary>
    public static Color PenetrationShadowColor { get; } = Color.Parse(PenetrationShadowColorHex);

    /// <summary>滚轮缩放的**视觉过渡时长**（毫秒，2026-09-23 去信封阶段 3）：一格的尺寸差用这段
    /// 时间匀滑补完，观感从"一格一跳"变成"连续长大"。补帧走 <c>RootGrid.RenderTransform</c> 的
    /// 合成器缩放，不重新排版、不重新采样 ⇒ 中间帧零成本（抖动的病根不在这里，见
    /// 2026-09-23 单变量实验的结论：是"改窗口尺寸"那一下）。
    /// ⚠ 改 <c>0</c> 即整块退回"每格直接到位"（本开关只关动画，不影响窗口＝内容的口径）。
    /// 2026-09-24：从 110 降到 50——110ms 时指数趋近走到 99% 实际要 ~167ms，用户报"缩放速度明显
    /// 下降"；50ms（τ=20ms，~76ms 走完）在保留补帧平滑（把改尺寸那一帧的缺陷藏进运动里）的同时
    /// 把每格的视觉滞后压回接近 instant 的手感。</summary>
    public const double ZoomEaseMs = 50;

    /// <summary>手势期窗口的**预留余量**（比例，2026-09-23 阶段 3 的治抖手段）。
    ///
    /// <para>依据即 2026-09-23 那次"手势期不改窗口尺寸"的单变量实验：窗口尺寸每改一次，合成表面就
    /// 重建一次，而重建那一帧显示的是旧尺寸或漏画新边（P0 探针早就采到过：每 20 次 resize 有 7~10 帧
    /// "旧画布原尺寸残留"＋2~6 帧"新扩出的边当帧没画"）。改位置、重排版、重采样都不抖。</para>
    ///
    /// <para>所以手势期窗口<b>只按档长大</b>：现窗口装得下画面就<b>一寸尺寸都不改</b>（位置每格照改，
    /// 锚点不破），装不下了才一次长到 <c>画面 × (1 + 本值)</c>。滚轮每格 ×1.1 ⇒ 取 0.25 时约每 3 格
    /// 才真改一次尺寸。落定收口仍缩回严格贴合。</para>
    ///
    /// <para>⚠ 本规则目前<b>只管"装不下"，不管"太空"</b>：缩小方向因此一格尺寸都不改，窗口停在
    /// 手势起手的那一档不动（2026-09-23 实测 pid=42984：内容缩到 657×464 时窗口仍停在
    /// 2455×1372＝约 12 倍面积），收口时还要来一次大 resize。
    /// ⚠ 但"白边大到 12 倍"这件事<b>同日就被用户的对照质疑掉了</b>：小图（画面几万像素、窗口表面
    /// 2~3MPx）缩放<b>一点不卡</b>，大图（画面 1~2MPx）才卡 ⇒ 代价看着不随<b>窗口表面</b>走、
    /// 而随<b>每帧重绘的画面像素量</b>走。所以这条<b>不是</b>已确认的性能缺陷，别照着它去优化表面；
    /// 真要收的只有"收口那一次大 resize"。判据仍需复测（那次对照同时变了两个量）。</para>
    ///
    /// <para>⚠ 这<b>不是</b>"画面在窗口里移动"：窗口原点恒按 <c>画面左上角 − 阴影边距</c> 反算，
    /// 所以多出来那一档落在右下两侧，画面相对窗口基本不动（内容大过工作区被夹小时才由
    /// 平移通路的最小位移钳制把它推回视口，那是平移态、不是缩放态）。
    /// 取 <c>0</c> ＝退回"每格严格贴合"＝抖动的现状。</summary>
    /// 值越大越不抖（改尺寸越少）但表面越大，每 MPx 约 0.6ms/帧；0.25 ≈ 最多多出 56% 面积。</summary>
    public const double ZoomGestureHeadroom = 0.25;

    /// <summary><b>改窗口尺寸时丢弃旧客户端像素</b>（2026-09-27，判决实验，默认开）。
    ///
    /// <para>来历：竞品 Snow Shot 1.0.0-beta（Qt 6.11 C++ 实现）在同一条"每格真改窗口尺寸"的路上
    /// 不闪，它的做法是在 <c>WM_WINDOWPOSCHANGING</c> 里给尺寸变化加 <c>SWP_NOCOPYBITS</c>，并在
    /// <c>WM_WINDOWPOSCHANGED</c> 里同帧强制重画。它源码里的注释就是我们这个现象的原话——"让 USER
    /// 保留/复制旧客户端位，就会在旧画面和被清空的新一帧之间来回交替"
    /// （<c>screenshotpinnedwindow.cpp:1292-1297</c>、<c>screenshotpinnedwindownative.cpp:34-53</c>；
    /// 只取思路，GPL 代码不抄）。</para>
    ///
    /// <para>为什么这条便宜又决定性：默认行为下系统 resize 会把<b>旧表面的像素复制进新窗口矩形</b>，
    /// 于是新窗口原点处会先显示一帧"按新原点摆放的旧画面"——正是用户报的"整张图跑到左上角又弹回"。
    /// 打上本标志＝告诉系统别复制，那块直接等我们画。⇒ <b>闪没了＝假说成立且这一行就是治法</b>
    /// ；<b>闪还在＝假说作废</b>。
    /// ⚠ 已知风险（与 Qt 的差距，实测前无法排除）：Qt 能 <c>RDW_UPDATENOW</c> <b>同步</b>补画，
    /// Avalonia 的画面是异步合成提交的 ⇒ 我们可能把"跳一下"换成"空一帧"（那一帧该区域是透明的）。
    /// 出现"缩放时整块画面消失半帧"就是这条路走不通的信号，一行关档退回。</para>
    ///
    /// <para>作用范围＝<b>所有</b>改尺寸的落定出口（滚轮/拖边/换图/提交/还原/适配屏幕共 9 处，
    /// 一律用 <see cref="ResizeSwpFlags"/>，不逐处记名单）；纯移动窗口的 <c>0x0015</c> 那几处不受影响。</para>
    public const bool WindowDiscardsStaleClientBits = true;

    /// <summary>改窗口尺寸的统一 <c>SetWindowPos</c> 标志位：<c>SWP_NOZORDER|SWP_NOACTIVATE</c>（0x0014），
    /// 叠加可选的 <c>SWP_NOCOPYBITS</c>（0x0020）。收成一个常量的理由同 <c>ApplyWindowRectManaged</c>：
    /// 这类"9 处必须一致、漏一处就跑位"的口径不能靠逐处记忆。</summary>
    public const uint ResizeSwpFlags = 0x0014 | (WindowDiscardsStaleClientBits ? 0x0020u : 0u);

    /// <summary>滚轮缩放是否走<b>"冻结栅格"</b>通路（2026-09-23 深度改，默认开；改 <c>false</c>
    /// 整块退回"每格重排版重采样"的旧通路，留作单变量对照）。
    ///
    /// <para><b>依据（用户三轮单变量实验，判据＝眼睛）</b>：很小的画面＋1~2MPx 的大窗口<b>不抖不卡</b>；
    /// 画面 400×300 不抖不卡、2592×1458 又抖又卡；把窗口收到与画面一样大（2.1MPx）<b>照旧卡</b>。
    /// ⇒ 代价既不在窗口表面、也不在"改窗口尺寸"那一下，而是<b>每帧要重绘的画面像素量</b>。
    /// 旧通路每格都调 <c>ApplyScale</c>（重排 8 层＋让位图按新尺寸重采样），这正是那份逐格成本；
    /// 而 2026-09-09 那次"改成每渲染帧只应用一次"的合帧只去掉了<b>重复</b>帧、没让<b>单帧</b>变便宜，
    /// 所以当时就是"小图好了、大图仍有问题"。</para>
    ///
    /// <para><b>本档的做法</b>：手势期间各层的显式宽高<b>停在上一档真排版尺寸</b>上不动，缩放全部由
    /// <c>RootGrid.RenderTransform</c> 的倍率表达（纯合成器矩阵，不排版、不重采样、不重建阴影几何）；
    /// 倍率离开 <c>1 ± ZoomGestureHeadroom</c> 那一档时才真排版一次（画面重新变清晰，顺带也就是
    /// 窗口该长大/缩小的那一格）；松手收口、拖边起手、平移起手、原地换图一律先落一次真排版，
    /// 因为"变换倍率"是画面之外的第二套坐标，留着它任何按 Margin 算几何的通路都会差一个倍率。</para>
    ///
    /// <para>⚠ <b>只对图片贴图生效</b>：文本 / Markdown 贴图必须继续逐格真排版——把活体文字按比例
    /// 放大＝文字发糊，而 2026-09-02「临时换位图走信封」正是因此被整体撤除（protection.md
    /// 「文本/Markdown 贴图禁用临时换位图走信封」条，勿重加）。文本贴图本身像素量也远小于图片，
    /// 不在这条成本曲线上。</para>
    /// <para>⚠ 代价（已知且刻意）：手势期间画面是"上一次真排版的位图放大 ≤25%"，
    /// <b>会有可察的软</b>，每次跨档与松手时恢复清晰。若判据回来是"还是卡"，说明成本在合成器
    /// 上屏那一侧（每帧 push 变大的画面），此档救不了，届时按"只换渲染层"另立方案。</para>
    public const bool ZoomFreezeRaster = true;

    /// <summary><b>图片贴图的像素改由原生分层窗呈现</b>（2026-09-28，贴图呈现层替换第 1 步，默认开）。
    ///
    /// <para>开：图片、阴影、棋盘格、底色由 <c>LayeredPinSurface</c> 每帧 CPU 画进 DIB、一次
    /// <c>UpdateLayeredWindow</c> 同时提交位置、尺寸与像素 ⇒ 缩放/拖边改窗口时"几何与像素错帧"结构性不存在；
    /// 阴影改为象限片镜像/拉伸，成本与贴图面积无关。Avalonia 窗口留作叠在上面的透明交互层。</para>
    ///
    /// <para>只对<b>图片贴图</b>、且不在缩略图模式时生效；文本/Markdown、缩略图与源图像素格式不认识的情形
    /// 自动退回 Avalonia 呈现（旧通路逐字未改）。<c>false</c> ＝整块退回旧通路，回退成本一行。</para>
    ///
    /// <para>本档下手势期窗口改为<b>严格贴合</b>（<see cref="ZoomGestureHeadroom"/> 按 0 处理：余量是为了"少改
    /// 窗口尺寸"，而分层呈现改尺寸不再闪，留着余量只会留下吞点击的透明圈），滚轮停下
    /// <see cref="LayeredWheelSettleMs"/> 后自动收口。</para></summary>
    public const bool LayeredPinSurface = true;

    /// <summary>分层呈现档下，滚轮停下多久自动收口手势（毫秒）。收口＝退出手势态、各层落回真排版、
    /// 静止窗严格贴合——拖动随之回到"只挪窗口"的最快通路，交互窗也不再比画面大。</summary>
    public const int LayeredWheelSettleMs = 260;
}
