#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScreenCaptureTool.Core.Ai;

namespace ScreenCaptureTool.Core
{
    /// <summary>
    /// 应用设置。从 <c>%APPDATA%/yuSnip/settings.ini</c> 读写。
    /// 阶段 2 搬迁自旧项目，按 D009 改为 <c>public</c>，命名空间改为 <c>ScreenCaptureTool.Core</c>。
    /// </summary>
    public sealed class AppSettings
    {
        // 保存
        public string SaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        public string FileNameTemplate = "Screenshot_{yyyyMMdd_HHmmss}";
        /// <summary>已废弃：截图统一保存为 PNG（以保留元数据）。保留字段仅为旧配置文件兼容，值恒为 "PNG"。</summary>
        public string ImageFormat = "PNG";
        /// <summary>已废弃：随 JPG 格式移除而失效，保留字段仅为旧配置文件兼容。</summary>
        public int JpegQuality = 92;       // 1..100

        // 截图行为
        public string AutoDetectMode = "仅窗口"; // 不检测 | 仅窗口 | 检测元素
        /// <summary>「检测元素」模式：鼠标停稳判定时长（毫秒，0 = 停止即检测，范围 0-100）。</summary>
        public int ElementDwellMs = 10;
        /// <summary>「检测元素」模式：移动幅度半径（像素，小于该距离的移动不重置停稳计时，范围 0-200）。</summary>
        public int ElementDwellDeadbandPx = 20;
        public bool CopyToClipboardAfterSave = true;
        public bool ShowSaveNotification = true;
        public bool PlaySaveSound = false;
        /// <summary>长截图过程中是否显示实时预览窗（默认 true 开启）。</summary>
        public bool ShowLongScrollPreview = true;
        /// <summary>截图参考线模式：关闭 | 开启（按住 Alt 显示） | 始终显示。只管紫色参考线，十字准星见 <see cref="CrosshairMode"/>。</summary>
        public string GuidelineMode = "开启"; // 关闭 | 开启 | 始终显示
        /// <summary>十字准星（跟随光标的贯穿整屏十字线 + 自绘瞄准光标）模式：关闭 | 开启（按住 Alt 显示） | 始终显示。</summary>
        public string CrosshairMode = "开启"; // 关闭 | 开启 | 始终显示
        /// <summary>settings.ini 里是否出现过 CrosshairMode 键。仅加载期使用，不参与序列化。</summary>
        private bool crosshairModeInFile = false;
        /// <summary>工具栏是否已完成包含智能抠图（matting）等新功能的一次性平滑迁移。落盘标记，避免重复将用户主动移至工具池的项插回。</summary>
        public bool ToolbarMigrated = false;
        /// <summary>settings.ini 里是否出现过 ToolbarMigrated 键。仅加载期使用，不参与序列化。</summary>
        private bool toolbarMigratedInFile = false;
        /// <summary>工具栏是否已完成「水印（watermark）」按键的一次性平滑迁移。落盘标记，用户事后主动丢回工具池则不再插回。</summary>
        public bool WatermarkMigrated = false;
        /// <summary>settings.ini 里是否出现过 WatermarkMigrated 键。仅加载期使用，不参与序列化。</summary>
        private bool watermarkMigratedInFile = false;
        /// <summary>截图过程中是否在左下角显示截图功能与快捷键提示（默认 true 开启）。</summary>
        public bool ShowCaptureHints = true;
        /// <summary>截图成品的圆角参数档位（0-50，0 = 直角，每档折算 4 物理像素，最大 200px）。全局记忆，改动入口在截图遮罩右上角提示条。</summary>
        public int CaptureCornerRadius = 0;
        /// <summary>截图成品是否带投影。开启时成品画布四周各外扩 <c>SelectionEffects.ShadowMarginPx</c> 像素（内容不缩放）。全局记忆。</summary>
        public bool CaptureShadow = false;
        /// <summary>截图成品的选区描边宽度（物理像素 0-20，0 = 不描边）。色带整体画在内容边界<b>以外</b>（另有 1px 向内交叉防缝），故成品尺寸 = 选区尺寸 + 2 × <c>SelectionEffects.MarginOf</c>，内容本身不缩放。全局记忆，改动入口在截图遮罩右上角参数条。</summary>
        public int CaptureStrokeWidth = 0;
        /// <summary>截图成品选区描边的颜色（ARGB 整数，与标注色同一种存法）。默认取 <c>SelectionEffects.DefaultStrokeArgb</c>。全局记忆，改动入口在截图遮罩参数条上描边滑杆左侧的色块。</summary>
        public int CaptureStrokeColor = Capture.SelectionEffects.DefaultStrokeArgb;
        public const string SelectionBorderStyleDashed = "Dashed";
        public const string SelectionBorderStyleSolid = "Solid";
        /// <summary>截图选框边框颜色的出厂默认值：纯白（<c>#FFFFFF</c>）。白色在任何底色上都不会与截图内容"撞色"，
        /// 且不像品牌紫那样与元素检测框／参考线同色混淆。旧版默认为品牌紫 <c>#4219F2</c>。</summary>
        public const int DefaultSelectionBorderColorArgb = unchecked((int)0xFFFFFFFF);
        public const int DefaultSelectionBorderThickness = 2;
        public const int MinSelectionBorderThickness = 0;
        public const int MaxSelectionBorderThickness = 5;
        /// <summary>截图选框边框线型："Dashed"（虚线，默认）| "Solid"（实线）。全局记忆，改动入口在设置页「工具栏与样式」。</summary>
        public string SelectionBorderStyle = SelectionBorderStyleDashed;
        /// <summary>截图选框边框颜色（ARGB 整数）。出厂默认纯白 <c>#FFFFFF</c>。全局记忆，改动入口在设置页「工具栏与样式」。</summary>
        public int SelectionBorderColorArgb = DefaultSelectionBorderColorArgb;
        /// <summary>截图选框边框粗细（像素，0~5，步进 1，默认 2px；0px 表示不画选框）。全局记忆，改动入口在设置页「工具栏与样式」。</summary>
        public int SelectionBorderThickness = DefaultSelectionBorderThickness;
        /// <summary>截图时是否自动选用上次使用的标注工具（默认 false 关闭）。</summary>
        public bool ReuseLastAnnotationTool = false;
        /// <summary>上次使用的标注工具标识（如 "Brush", "Arrow", "Rectangle", "Text", "Mosaic", "Spotlight", "Counter"）。</summary>
        public string LastAnnotationTool = "";
        /// <summary>截图与主工具栏智能抠图默认使用的模型：General 通用 (U²-NetP) | Portrait 人像 (MODNet)。</summary>
        public string MattingDefaultModel = MattingModelGeneral;
        /// <summary>智能抠图结果的去向：Clipboard 写入剪贴板 | Sticker 生成贴图 | ClipboardAndSticker 剪贴板并贴图（默认）。对截图工具栏/快捷键、贴图右键菜单与脚本指令的所有抠图入口生效。</summary>
        public string MattingResultDestination = MattingResultBoth;

        // 外观
        public string ThemeMode = "跟随系统"; // 浅色 | 暗色 | 跟随系统
        /// <summary>截图主工具栏布局：逗号分隔的按钮 ID 列表，"|" 表示分隔线。</summary>
        public string ToolbarLayout = ToolbarRegistry.DefaultLayout;
        /// <summary>
        /// 未上栏的组（组库）：若干 <c>[a+b+c]</c> 以逗号分隔。这些组已在设置页建好但还没拖上一级工具栏，
        /// 因此不占任何一格；截图侧只读 <see cref="ToolbarLayout"/>，完全不认这个键（旧版读到也只是忽略）。
        /// </summary>
        public string ToolbarGroups = "";
        public string StickerAnnotationToolbarLayout = ToolbarScenes.Get(ToolbarScene.StickerAnnotation).DefaultLayout;
        public string RecordPreparationToolbarLayout = ToolbarScenes.Get(ToolbarScene.RecordPreparation).DefaultLayout;
        public string RecordControlToolbarLayout = ToolbarScenes.Get(ToolbarScene.RecordControl).DefaultLayout;
        public string VideoPreviewToolbarLayout = ToolbarScenes.Get(ToolbarScene.VideoPreview).DefaultLayout;
        public string LongScrollToolbarLayout = ToolbarScenes.Get(ToolbarScene.LongScroll).DefaultLayout;
        /// <summary>长截图默认栏新增贴图的升级标记，用户修改布局后不再自动补回。</summary>
        public bool LongScrollStickerMigrated = false;
        public string StickerAnnotationToolbarGroups = "";
        public string RecordPreparationToolbarGroups = "";
        public string RecordControlToolbarGroups = "";
        public string VideoPreviewToolbarGroups = "";
        public string LongScrollToolbarGroups = "";

        // 启动
        public bool RunOnStartup = false;
        /// <summary>管理员模式：开启后软件始终以管理员权限运行（含开机自启，自启改走计划任务免 UAC 提权）。</summary>
        public bool RunAsAdmin = false;
        public bool ShowStartupNotification = true;
        /// <summary>启动时是否静默检查更新（有新版本才弹窗提示，默认开）。</summary>
        public bool CheckUpdateOnStartup = true;

        // 热键
        public uint HotKeyModifiers = 0x0002 | 0x0001; // Ctrl + Alt
        public uint HotKeyVirtualKey = 0x41;           // A
        /// <summary>快速贴图热键的修饰键（默认 0 = 无修饰，配合 F3 单键触发）。</summary>
        public uint StickerHotKeyModifiers = 0;
        /// <summary>快速贴图热键的主键（默认 VK_F3 = 0x72）。</summary>
        public uint StickerHotKeyVirtualKey = 0x72;
        /// <summary>贴图双击默认动作：Close 关闭（默认）/ Thumbnail 折叠为缩略图 / Copy 复制图像 / None 无操作。</summary>
        public string StickerDoubleClickAction = "Close";
        /// <summary>贴图新建时是否默认显示阴影（默认开）。
        /// ⚠ 自 2026-10-01 起这一项只是 <see cref="StickerShadowMode"/> 为 <c>On</c>/<c>Off</c> 时的等价表示，
        /// **保留它是为了兼容旧 `settings.ini` 与云同步**（老版本只认这个键）。新代码一律读
        /// <see cref="StickerShadowMode"/>，写入时两者同步维护。</summary>
        public bool StickerDefaultShadow = true;
        /// <summary>**贴图窗体阴影档位**（2026-10-01 新增，issue #50）：<c>Off</c> 一律不画 /
        /// <c>On</c> 一律画 / <c>Auto</c>＝**跟随这张图截图时「阴影」有没有勾选**（默认 Auto）。
        ///
        /// <para>🔴 Auto 的判据是**用户在参数条上的勾选意图**，不是"图里有没有透明像素"也不是
        /// "图里有没有烘焙阴影带"（2026-10-01 用户拍板）。理由：勾选阴影＝用户认为"这张图需要边界"，
        /// 这正是翻译贴图（不透明白底、看不清轮廓）与透明 PNG（本就干净）该被区别对待的那个信息；
        /// 而透明像素只是图的物理属性，按它判会把"用户想要边界的白底图"和"不想要的"混为一谈。</para>
        ///
        /// <para>⚠ 取不到勾选意图的贴图（剪贴板 F3、截图历史、别的应用复制来的图、脚本贴图、
        /// 文本/Markdown/颜色贴图）在 Auto 档下**回落为画阴影**：它们多是不透明内容，
        /// 且"多一圈光晕"远比"看不清边界"轻。</para></summary>
        public string StickerShadowMode = "Auto";
        /// <summary>透明图片贴图的背景：true＝垫透明格子（默认），false＝纯透明（透出桌面）。
        /// 只影响窗口显示，不进复制/另存/导出；窗口阴影不受影响。</summary>
        public bool StickerTransparentCheckerboard = true;
        /// <summary>贴图文本是否默认可选择（默认关）。</summary>
        public bool StickerTextSelectable = false;
        /// <summary>
        /// 选中文本贴图（默认关）：开启后按贴图热键会先向当前前台窗口模拟一次 Ctrl+C，
        /// 抓到的选中文字直接生成文本贴图，原剪贴板内容随后还原（用户无需手动复制）。
        /// </summary>
        public bool StickerGrabSelectedText = false;
        /// <summary>
        /// 调整文本贴图（默认关）：开启后拖拽文本贴图边缘改的是**版宽**（字号不变、行数随宽度重排），
        /// 关闭时与图片贴图一致——拖边等同滚轮缩放，只改字号不改行数。
        /// </summary>
        public bool StickerResizeReflowsText = false;
        /// <summary>
        /// 贴图取色器（默认<b>关</b>）：开启后，图片贴图上按住 <c>Shift</c> 悬停即在指针旁出放大镜与色值，
        /// 显示期间可按「取色器·复制颜色」「取色器·切换格式」两条改键项所绑的键复制色值 / 切换 HEX·RGB。
        /// 2026-09-29 用户要求增设开关并默认关闭；唤出键同日由 Alt 改为 Shift。
        /// </summary>
        public bool StickerColorPickerEnabled = false;
        /// <summary>
        /// 划词工具栏（默认<b>开</b>）：文本/Markdown 贴图上选中文字时，选区下方浮出一条工具栏
        /// （复制 / 标注 / 翻译 / AI 解释 / AI 联网搜索）。关掉后仍可用 Ctrl+C 复制选区、右键菜单翻译。
        /// 2026-09-29 随贴图划词工具栏一同新增。
        /// </summary>
        public bool StickerSelectionToolbar = true;
        /// <summary>
        /// 「恢复上次关闭的贴图」取哪一张：ClosedAt 按关闭时间（默认，最近关掉的那张）/
        /// Created 按贴图当初创建的时间（最近创建的那张，与它何时被关掉无关）。
        /// </summary>
        public string StickerRestoreSortMode = "ClosedAt";

        // 截图历史
        /// <summary>
        /// 截图历史收集开关：Off 关闭（默认）/ On 开启。
        /// 开启后全量留存产出图片的本机副本（含普通截图、贴图、长截图）。
        /// 默认关闭——历史收集会往 %APPDATA% 写入图片副本，属磁盘写入行为，不擅自默认开启。
        /// </summary>
        public string CaptureHistoryMode = "Off";
        /// <summary>截图历史保留条数上限（默认 200，合法区间 20~2000）。超限从最旧删起。</summary>
        public int CaptureHistoryMaxCount = 200;
        /// <summary>
        /// 截图历史保留总容量上限（MB，默认 800，合法区间 100~8000）。
        /// 与条数上限构成**双闸**：任一超限即裁剪——仅按条数裁剪时，4K 全屏 PNG 单张 5~10MB，
        /// 200 张最坏可达 1~2GB，对 %APPDATA% 不可接受。
        /// </summary>
        public int CaptureHistoryMaxMegabytes = 800;
        /// <summary>截图历史记录窗口记忆宽度（默认 1000）。</summary>
        public double CaptureHistoryWindowWidth = 1000;
        /// <summary>截图历史记录窗口记忆高度（默认 680）。</summary>
        public double CaptureHistoryWindowHeight = 680;
        /// <summary>
        /// 截图选区历史（2026-09-29「R 键恢复上一次截图框」）：最近若干<b>截图成功</b>那一次的矩形，最新在前。
        /// 序列化在 <see cref="Capture.SelectionHistory"/> 里做，本字段只当字符串载体，格式对 AppSettings 不透明。
        /// 存的是物理像素矩形＋落盘那一刻的虚拟屏指纹（换显示器/改分辨率后老坐标可能整块落在屏外，光存矩形没法判）。
        /// 与截图历史（图片副本）是两回事：这里只有几十字节的坐标，不写任何文件、不参与图片容量闸。
        /// </summary>
        public string CaptureRectHistory = "";
        /// <summary>
        /// 截图自动识别文字：截图**保存**、**复制到剪贴板**、**长截图**产出后，后台静默跑一次 OCR 并回写历史索引，
        /// 供关键字检索。
        /// 默认关闭——OCR 要加载 ONNX 模型并占一次 CPU，属"额外后台占用"，不擅自默认开启。
        /// 只作用于截图侧；贴图侧的自动识别与存量补识别是另一条通路，与本开关互不影响。
        /// </summary>
        public bool CaptureAutoOcr = false;

        // 翻译
        /// <summary>OCR 输出方式：Window 打开编辑窗口（默认）/ Clipboard 静默复制到剪贴板并提示。</summary>
        public string OcrOutputMode = "Window";
        /// <summary>OCR 静默复制提示窗位置：BottomCenter 中下（默认）/ TopCenter 中上 / TopRight 右上 / BottomRight 右下 / None 无；兼容 Center 居中。</summary>
        public string OcrToastPosition = "BottomCenter";
        /// <summary>OCR 静默复制提示窗等待秒数：3 / 5（默认）/ 10。鼠标悬停期间倒计时暂停，不计入该时长。</summary>
        public int OcrToastSeconds = 5;
        /// <summary>翻译服务商：BilibiliIndex（默认免费）/ None / Baidu / Youdao。</summary>
        public string TranslateProvider = "BilibiliIndex";
        private bool _translateProviderDefaultMigrated;
        /// <summary>翻译 API 应用 ID（百度 APP ID / 有道应用 ID）。兼容旧配置，新配置使用分平台字段。</summary>
        public string TranslateAppId = "";
        /// <summary>翻译 API 密钥（百度密钥 / 有道应用密钥）。兼容旧配置，新配置使用分平台字段。</summary>
        public string TranslateSecretKey = "";
        /// <summary>百度翻译 APP ID（独立存储）。</summary>
        public string BaiduAppId = "";
        /// <summary>百度翻译 API Key（独立存储）。</summary>
        public string BaiduSecretKey = "";
        /// <summary>有道智云应用 ID（独立存储）。</summary>
        public string YoudaoAppId = "";
        /// <summary>有道智云应用密钥（独立存储）。</summary>
        public string YoudaoSecretKey = "";
        /// <summary>翻译源语言（内部代码，默认 auto 自动检测）。</summary>
        public string TranslateSourceLanguage = "auto";
        /// <summary>翻译目标语言（内部代码，默认 zh 简体中文）。</summary>
        public string TranslateTargetLanguage = "zh";
        /// <summary>翻译次级目标语言（内部代码，默认 en 英语）。当检测到源语言与目标语言相同时自动切换为此语言。</summary>
        public string TranslateSecondaryTargetLanguage = "en";
        /// <summary>翻译模式：ImageToText / ImageToImage（默认 ImageToText）。</summary>
        public string TranslateMode = "ImageToText";
        /// <summary>图片到文本按段流式翻译，逐段展示原译文对照（默认关闭，旧配置缺键也关闭）。</summary>
        public bool TranslateStreamEnabled = false;
        /// <summary>极简翻译浮窗位置：FollowSelection（跟随鼠标，在光标右下角弹出，默认）/ CenterScreen（屏幕居中）。值名沿用旧配置不改。</summary>
        public string TranslateMiniWindowPosition = "FollowSelection";
        /// <summary>极简翻译浮窗是否在失去焦点后自动关闭（默认 true）。关掉后便签常驻，直到按 Esc 或升级到编辑窗。</summary>
        public bool TranslateMiniWindowAutoClose = true;
        /// <summary>译文便签弹出时是否自动把译文写入剪贴板（默认 true）。关掉后需手动点「复制」，以免顶掉剪贴板里的原有内容。</summary>
        public bool TranslateMiniWindowAutoCopy = true;

        // AI 大模型 / 识图
        /// <summary>AI 识图是否使用默认提示词（true 直接分析，false 弹出提示词输入窗口）。默认 true。</summary>
        public bool AiUseDefaultPrompt = true;
        /// <summary>AI 提问极简弹窗展示位置：FollowMouse（跟随鼠标）/ CenterScreen（屏幕居中）。默认 FollowMouse。</summary>
        public string AiPromptWindowPosition = "FollowMouse";
        /// <summary>当前选中的 AI API 配置 ID。</summary>
        public string AiActiveConfigId = "";
        /// <summary>多 API 配置列表。</summary>
        public List<AiApiConfig> AiConfigs = new List<AiApiConfig>();

        /// <summary>AI 协议：OpenAI / Claude（默认 OpenAI）。</summary>
        public string AiProtocol = "OpenAI";
        /// <summary>AI 服务 Base URL（默认 https://api.openai.com/v1）。</summary>
        public string AiBaseUrl = "https://api.openai.com/v1";
        /// <summary>AI API 密钥。</summary>
        public string AiApiKey = "";
        /// <summary>选中的 AI 模型名称（默认空，由用户指定或在线获取）。</summary>
        public string AiModel = "";
        /// <summary>AI 识图默认提示词（简洁清晰、结论先行、短句分点）。</summary>
        public const string DefaultAiCustomPrompt =
            "你是专业的图像分析助手。请简洁清晰地分析这张图片：先用一两句话概括核心内容；再以短句分点列出关键信息；完整提取图中出现的文字（按原文简洁列出）。如涉及代码、图表或界面，简要说明其结构与用途。只输出有价值的信息，避免长段落、空话与重复。使用 Markdown 输出。";
        /// <summary>AI 识图提示词。</summary>
        public string AiCustomPrompt = DefaultAiCustomPrompt;
        /// <summary>AI 识图预设提示词列表（每条带技能命令名，AI 输入框 @ 唤起；其中一条可标记为「直接识图」默认提示词）。</summary>
        public List<AiPromptPreset> AiPromptPresets = new List<AiPromptPreset>();
        /// <summary>AI 识图联网搜索开关。</summary>
        public bool AiSearchEnabled = false;
        /// <summary>AI 对话「深度思考」开关（默认开＝保持历来行为）。关掉后请求体带上各家的"关思考"字段
        /// （<c>reasoning_effort</c>/<c>enable_thinking</c>/<c>thinking.type</c>/<c>chat_template_kwargs</c>），
        /// 认这些字段的服务商直接快答；不认的网关忽略即可，严格校验返回 400 的会自动用干净请求重试一次。
        /// 窗口内那枚芯片是本项的会话级覆盖并写回这里（与联网搜索同一范式）。</summary>
        public bool AiThinkingEnabled = true;
        /// <summary>联网搜索端点（留空自动使用 DuckDuckGo/Bing；可填自建 SearXNG 实例地址）。</summary>
        public string AiSearchEndpoint = "";
        /// <summary>AI 请求超时秒数（默认 60）。</summary>
        public int AiTimeoutSeconds = 60;
        /// <summary>AI 识图结果窗口记忆宽度（默认 480，即最小初始宽度）。</summary>
        public double AiVisionWindowWidth = 480;
        /// <summary>AI 识图结果窗口记忆高度（默认 640，即最小初始高度）。</summary>
        public double AiVisionWindowHeight = 640;
        /// <summary>翻译编辑窗口记忆宽度（默认 760）。</summary>
        public double TranslationEditWindowWidth = 760;
        /// <summary>翻译编辑窗口记忆高度（默认 520）。</summary>
        public double TranslationEditWindowHeight = 520;
        /// <summary>翻译编辑窗口原文抽屉记忆宽度（并排态，默认 200，实际由窗口按 120–420 夹取）。</summary>
        public double TranslationEditDrawerWidth = 200;
        /// <summary>翻译编辑窗口原文抽屉记忆高度（堆叠态，默认 96，实际由窗口按 56–240 夹取）。</summary>
        public double TranslationEditDrawerHeight = 96;

        /// <summary>规范化 AI 提问极简弹窗位置代码（仅允许 FollowMouse / CenterScreen，其余回退 FollowMouse）。</summary>
        public static string NormalizeAiPromptWindowPosition(string value)
        {
            if (string.Equals(value, "CenterScreen", StringComparison.OrdinalIgnoreCase))
                return "CenterScreen";
            return "FollowMouse";
        }

        /// <summary>规范化极简翻译浮窗位置代码（仅允许 FollowSelection / CenterScreen / BottomCenter，其余回退 FollowSelection）。</summary>
        public static string NormalizeTranslateMiniWindowPosition(string value)
        {
            if (string.Equals(value, "CenterScreen", StringComparison.OrdinalIgnoreCase))
                return "CenterScreen";
            if (string.Equals(value, "BottomCenter", StringComparison.OrdinalIgnoreCase))
                return "BottomCenter";
            return "FollowSelection";
        }

        /// <summary>
        /// 取实际生效的 API Key："dp:" 前缀为已移除的 Beta 测试通道遗留密文，视为未配置。
        /// </summary>
        public string GetEffectiveApiKey()
        {
            string stored = AiApiKey ?? "";
            if (stored.StartsWith("dp:", StringComparison.Ordinal))
                return "";
            return stored;
        }

        /// <summary>
        /// 获取当前生效的 AI API 配置（优先根据 activeId 或 model 查询已启用的配置；如无则回退第一个启用的配置；如仍无则返回 null）
        /// </summary>
        public AiApiConfig GetEffectiveApiConfig(string model = null)
        {
            EnsureDefaultAiConfigs();
            var enabledList = AiConfigs.FindAll(c => c.IsEnabled);
            if (enabledList.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(model))
            {
                var matchByModel = enabledList.Find(c =>
                    string.Equals(c.DefaultModel, model, StringComparison.OrdinalIgnoreCase) ||
                    (c.CustomModels != null && c.CustomModels.Exists(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase))));
                if (matchByModel != null) return matchByModel;
            }

            if (!string.IsNullOrWhiteSpace(AiActiveConfigId))
            {
                var matchById = enabledList.Find(c => string.Equals(c.Id, AiActiveConfigId, StringComparison.OrdinalIgnoreCase));
                if (matchById != null) return matchById;
            }

            return enabledList[0];
        }

        /// <summary>
        /// 确保预设提示词列表已初始化：列表为空时用旧版 AiCustomPrompt（或内置默认）播种一条默认预设，
        /// 并保证全局恰有一条 IsDefault（无任何标记时取第一条）。
        /// </summary>
        public void EnsureDefaultAiPrompts()
        {
            if (AiPromptPresets == null) AiPromptPresets = new List<AiPromptPreset>();
            if (AiPromptPresets.Count == 0)
            {
                // 旧配置迁移：沿用用户此前自定义的单条提示词作为默认预设内容
                string content = string.IsNullOrWhiteSpace(AiCustomPrompt) ? DefaultAiCustomPrompt : AiCustomPrompt;
                AiPromptPresets.Add(new AiPromptPreset
                {
                    Id = "default-prompt",
                    Name = "通用识图",
                    Content = content,
                    IsDefault = true
                });
            }
            if (!AiPromptPresets.Any(p => p.IsDefault))
            {
                AiPromptPresets[0].IsDefault = true;
            }
        }

        /// <summary>获取「直接识图」使用的默认提示词内容（取 IsDefault 预设，异常兜底内置默认）。</summary>
        public string GetDefaultAiPrompt()
        {
            EnsureDefaultAiPrompts();
            var def = AiPromptPresets.FirstOrDefault(p => p.IsDefault);
            if (def != null && !string.IsNullOrWhiteSpace(def.Content)) return def.Content;
            return DefaultAiCustomPrompt;
        }

        public void EnsureDefaultAiConfigs()
        {
            if (AiConfigs == null) AiConfigs = new List<AiApiConfig>();
            if (AiConfigs.Count == 0)
            {
                var def = new AiApiConfig
                {
                    Id = "default",
                    Name = "默认配置",
                    IsEnabled = true,
                    Protocol = string.IsNullOrWhiteSpace(AiProtocol) ? "OpenAI" : AiProtocol,
                    BaseUrl = string.IsNullOrWhiteSpace(AiBaseUrl) ? "https://api.openai.com/v1" : AiBaseUrl,
                    ApiKey = AiApiKey ?? "",
                    DefaultModel = AiModel ?? "",
                    CustomModels = !string.IsNullOrWhiteSpace(AiModel) ? new List<string> { AiModel } : new List<string>(),
                    CachedModels = !string.IsNullOrWhiteSpace(AiModel) ? new List<string> { AiModel } : new List<string>()
                };
                AiConfigs.Add(def);
                AiActiveConfigId = "default";
            }

            foreach (var cfg in AiConfigs)
            {
                if (cfg.CustomModels == null) cfg.CustomModels = new List<string>();
                if (cfg.CachedModels == null) cfg.CachedModels = new List<string>();
                if (cfg.CachedModels.Count == 0 && cfg.CustomModels.Count > 0)
                {
                    cfg.CachedModels = new List<string>(cfg.CustomModels);
                }
            }
        }

        // 标注工具状态（跨会话记忆）
        /// <summary>标注颜色 ARGB（默认 #FF3388FF）。画笔/箭头/矩形/序号/聚光灯描边/文本前景色共享。</summary>
        public int AnnotationColor = unchecked((int)0xFF3388FF);
        /// <summary>画笔/箭头/矩形描边粗细（默认 15）。</summary>
        public double StrokeThickness = 15;
        /// <summary>画笔线型：Solid / DashLarge（默认 Solid）。</summary>
        public string BrushLineStyle = "Solid";
        /// <summary>画笔整笔不透明度百分比 0-100（默认 100，自交处不叠加变深）。</summary>
        public double BrushOpacity = 100;
        /// <summary>画笔笔尖硬度百分比 0-100（默认 100 = 实边无渐变，越小软边越宽）。</summary>
        public double BrushHardness = 100;
        /// <summary>画笔笔锋驱动方式：None / Velocity / Pressure（默认 Velocity）。</summary>
        public string BrushTaperMode = "Velocity";
        /// <summary>画笔笔锋强度百分比 0-100（默认 35，0 = 等宽）。</summary>
        public double BrushTaperStrength = 35;
        /// <summary>画笔输入平滑百分比 0-100（默认 40，越大越圆滑但跟手越迟）。</summary>
        public double BrushSmoothing = 40;
        /// <summary>画笔笔锋最细处相对基准粗细的百分比 5-100（默认 25）。</summary>
        public double BrushMinWidthRatio = 25;
        /// <summary>箭头样式：Designed / Sharp（默认 Designed）。</summary>
        public string ArrowStyle = "Designed";
        /// <summary>矩形形状类型：Rectangle / Ellipse（默认 Rectangle）。</summary>
        public string ShapeKind = "Rectangle";
        /// <summary>矩形线型：Solid / DashLarge（默认 Solid）。</summary>
        public string ShapeLineStyle = "Solid";
        /// <summary>矩形圆角半径，物理像素 0-200（默认 0 = 直角）。单位与图元几何同坐标系（截图＝屏幕物理像素，贴图＝内容像素）。</summary>
        public double ShapeCornerRadius = 0;
        /// <summary>形状是否填充内部（默认 false = 只描边）。填充色不单独记——恒等于线条颜色。</summary>
        public bool ShapeFill = false;
        /// <summary>形状填充浓淡百分比 5-100（默认 30）。仅在 <see cref="ShapeFill"/> 为真时生效。</summary>
        public double ShapeFillOpacity = 30;
        /// <summary>模糊/马赛克模式：Blur / Mosaic（默认 Blur）。</summary>
        public string BlurMode = "Blur";
        /// <summary>模糊/马赛克强度 UI 等级 1-30（默认 10）。</summary>
        public int BlurUiLevel = 10;
        /// <summary>聚光灯暗幕透明度百分比 0-100（默认 55）。</summary>
        public double SpotlightDarkness = 55;
        /// <summary>聚光灯描边粗细（默认 3）。</summary>
        public double SpotlightStrokeThickness = 3;
        /// <summary>聚光灯圆角半径，物理像素 0-200（默认 0 = 直角）。单个图元仍可拖角手柄单独改，改完会回灌此记忆值。</summary>
        public double SpotlightCornerRadius = 0;
        /// <summary>序号半径 8-80（默认 14）。</summary>
        public double CounterRadius = 14;
        /// <summary>胶囊外形的圆角百分比 0-100（默认 100 = 两端全圆，即升级前的样子）。
        /// 入口是选中序号后那条浮条上的「圆角」一格，改完即回灌此记忆值，下一颗胶囊沿用。</summary>
        public double CounterCapsuleCorner = 100;
        // 序号标注的样式记忆参数（2026-09-18 对标 PixPin 序列号标注那一轮加，2026-09-19 做减法）。
        // 「起始值」刻意**不在**这一族里：它是会话内每建一个就自动前进的临时值，
        // 落盘会让下一次截图从上一次停下的号开始，而不是从 1 开始。
        // 描边宽、字体、引线粗细、说明字号这几项**不再有设置键**：界面上已收掉，
        // 一律走 AnnotationRenderOptions 里的固定常量（描边 2 / Segoe 粗体 / 引线粗 2.5 / 说明随半径自动）。
        /// <summary>序号类型档：Number / Letter / Roman（默认 Number）。</summary>
        public string CounterSerial = "Number";
        /// <summary>序号外形档：Circle / RoundedSquare / Capsule（默认 Circle）。</summary>
        public string CounterGlyph = "Circle";
        /// <summary>序号底色档：Solid / Outline / None（默认 Solid）。无底档外形恒为 Circle。</summary>
        public string CounterFill = "Solid";
        /// <summary>序号引线箭头的头型档，存 ArrowAnnotationStyle 的成员名（默认 Sharp＝引线一直画的那颗实心三角）。
        /// 2026-09-19 起它与箭头标注工具共用同一份头型列表与同一个下拉，只是入口在序号参数条上。</summary>
        public string CounterTailHead = "Sharp";
        /// <summary>删掉中间某个序号时是否把后面的编号整体前移（默认 true＝升级前的固定行为）。</summary>
        public bool CounterRenumberOnDelete = true;
        /// <summary>是否记住上次的序号类型/外形/底色（默认 true；关掉则每次回到各自默认档）。</summary>
        public bool CounterRememberStyle = true;
        // 文本工具状态
        /// <summary>文本字体大小（默认 24）。</summary>
        public double TextFontSize = 24;
        /// <summary>文本字体名称（默认 Microsoft YaHei UI）。</summary>
        public string TextFontFamily = "Microsoft YaHei UI";
        /// <summary>文本是否粗体（默认 false）。</summary>
        public bool TextIsBold = false;
        /// <summary>文本是否斜体（默认 false）。</summary>
        public bool TextIsItalic = false;
        /// <summary>文本是否启用描边（默认 false）。</summary>
        public bool TextHasStroke = false;
        /// <summary>文本是否启用背景填充（默认 false）。</summary>
        public bool TextHasBackground = false;
        /// <summary>文本描边粗细（默认 4）。</summary>
        public double TextStrokeThickness = 4;
        /// <summary>文本描边颜色 ARGB（默认 #FFFFFFFF 白色）。</summary>
        public int TextStrokeColor = unchecked((int)0xFFFFFFFF);
        /// <summary>文本背景填充颜色 ARGB（默认 #FFFFCC00 黄色）。</summary>
        public int TextFillColor = unchecked((int)0xFFFFCC00);
        /// <summary>文本背景填充透明度百分比 0-100（默认 100）。</summary>
        public double TextFillOpacity = 100;
        /// <summary>文本背景圆角半径（默认 4）。</summary>
        public double TextFillCornerRadius = 4;
        /// <summary>文本背景内边距（默认 4）。</summary>
        public double TextFillPadding = 4;

        // 水印标注状态（2026-09-20 那一轮，量程与默认值以 WatermarkEffect 里的常量为唯一出处）
        // ⚠ 水印的「开/关」刻意不在这一族里：它是每次截图都回到"关"的临时状态，落盘会让下一次截图
        //    莫名其妙带着上次的水印（第一轮拍板第 2 条：参数持久、开关不持久）。
        // ⚠ 颜色也不在这里：水印复用全工具共用的 AnnotationColor，不为它开第二份取色设置。
        /// <summary>水印文字内容（默认空＝不盖章）。空串时按钮照样能点亮、参数照样能调，只是成品上没有字。</summary>
        public string WatermarkText = "";
        /// <summary>水印落点档，存 <c>WatermarkPosition</c> 的成员名（默认 Tile＝平铺）。</summary>
        public string WatermarkPosition = "Tile";
        /// <summary>水印不透明度 5-100（%，默认 30）。最低 5 而不是 0，避免盖出完全看不见的东西。</summary>
        public int WatermarkOpacity = 30;
        /// <summary>水印字高 12-80，单位是<b>成品图像像素</b>而非屏幕 DIP（默认 24）。</summary>
        public int WatermarkSize = 24;
        /// <summary>平铺间距 100-1000（%，步进 10，默认 300）。100%＝一格正好装下一个水印；只作用在平铺档。</summary>
        public int WatermarkSpacing = 300;

        // 录屏默认参数（2026-09-21 录屏轮，稿 4「录屏」分区 4 卡的初值仓库）。
        // 🔴 2026-09-24 语义收口：这一族从此<b>只当默认值</b>用。
        //    预录制参数条与预览贴图窗上的改动**不再覆写它**，改写下面的 RecLast*「上次使用」族。
        //    此前两侧共用同一族键，现象＝在预览窗换过一次 GIF，设置页的「默认格式」就跟着变了
        //    （用户口径：「修改跟设置里面要区别开来，设置里面是默认设置，后续可能还会修改」）。
        //    例外是条/窗上**独占入口**的项（进度条勾选、预览循环、REC 角标）：本来就没有"默认／上次"之分，仍是一族单键。
        // 🔴 同日「录屏栏瘦身」再收一步：下面带 RecLast* 对应项的七条（画质／格式／延迟／系统声音／麦克风／
        //    键帽／涟漪）在设置页的控件已全部删除——参数条与预览窗是它们的唯一入口，设置页里这几条从此只是
        //    <b>出厂种子</b>（老 ini 里已有的值继续生效，但界面上改不动）。判据与截图圆角/阴影/描边同一条：
        //    参数条已有的入口，不在设置页放第二份控件。
        /// <summary>默认画质，存 <c>RecordQuality</c> 成员名（Standard＝标准，稿 1 D05 推荐档）。</summary>
        public string RecQuality = "Standard";
        /// <summary>默认导出格式，存 <c>RecordFormat</c> 成员名（Mp4；GIF/WebP 为保存期转码，录制引擎本体始终录 MP4）。</summary>
        public string RecFormat = "Mp4";
        /// <summary>默认录制延迟秒数，档位表见 <c>Recording.RecordCountdown</c>（0＝无延迟 / 1 / 2 / 3 / 5 / 10）。</summary>
        public int RecCountdownSec = Recording.RecordCountdown.DefaultSec;
        /// <summary>默认采集系统声音（进程环回 Loopback，稿 1 D19 双路之一）。</summary>
        public bool RecSystemAudio = true;
        /// <summary>默认采集麦克风（默认录音设备，稿 1 D19 双路之二）。</summary>
        public bool RecMicAudio = true;
        /// <summary>双开时的混音方式：Mixed＝自动混音（默认）/ Split＝分离双轨（出两条 AAC 轨）。单开时本项无意义。</summary>
        public string RecAudioMix = "Mixed";
        /// <summary>混音时麦克风占比 20-100（%，默认 40，稿 4 卡 2）。实现按滑杆走（稿 4 明示 compactSlider），分段档位只是静态示意。</summary>
        public int RecMicVolume = 40;
        /// <summary>可视化三路独立开关（稿 1 D03 推荐形态：系统硬件光标入画＝ffmpeg draw_mouse）。</summary>
        public bool RecVisCursor = true;
        /// <summary>点击涟漪（全局低级鼠标钩子驱动，RecordOverlayWindow 效果层）。</summary>
        public bool RecVisRipple = false;
        /// <summary>按键显示（键帽药丸，选区底缘内侧居中，2s 淡出）。</summary>
        public bool RecVisKeycap = false;
        /// <summary>录制中右上角 REC 时长角标开关（稿 2 D07 ✓丙，默认可见）。</summary>
        public bool RecShowBadge = true;
        /// <summary>预览贴图自动循环播放（稿 3 D21，推荐开）。</summary>
        public bool RecPreviewLoop = true;
        /// <summary>导出时是否添加进度条（稿 3 D18，推荐<b>关</b>——成品默认干净）。设置页无此控件，预览窗是唯一入口，
        /// 故它不属于"默认／上次"分层的对象。</summary>
        public bool RecBurnProgressBar = false;
        /// <summary>手动指定的 ffmpeg.exe 路径（空＝走探测链：程序目录 → PATH → 已下载目录，稿 4 卡 3）。</summary>
        public string RecFfmpegPath = "";
        /// <summary>画面采集方式，存 <c>RecordCaptureSource</c> 成员名。默认 Auto＝优先 ddagrab（桌面复制），
        /// 打不开再回落 gdigrab；GdiOnly 是留给"DDA 反而出不来"时的退路（2026-09-24 录屏冻结排查）。</summary>
        public string RecCaptureSource = "Auto";
        /// <summary>录制引擎：Auto 优先 Windows 系统编码，Windows 强制系统，Ffmpeg 使用运行组件。</summary>
        public string RecEngine = "Auto";
        /// <summary>视频保存目录（空＝默认 Videos\yuSnip 录屏\，不存在自动建，计划 §3.11）。</summary>
        public string RecSaveDirectory = "";
        /// <summary>一次性迁移标记：工具栏布局是否已补进「录屏」格（同 WatermarkMigrated 口径）。</summary>
        public bool RecMigrated = false;
        /// <summary>兼容旧配置的迁移标记：工具栏布局是否已补进「清除全部标注」格。</summary>
        public bool ClearAllAnnotationsMigrated = false;

        // ===== 「上次使用」族（2026-09-24）=====
        // 与上面那族"默认值"一一对应。分层判据只有一条：<b>设置页与参数条／预览窗都有入口的项</b>才需要两份，
        // 只有单侧入口的项保持单键（进度条勾选、预览循环、REC 角标、麦克风音量、光标入画……都不在此列）。
        // 读取一律走 <see cref="Pick(string,string)"/> 那一组重载（上次使用优先、默认兜底），这一条判据只许有一份实现。
        // 落盘：条／窗那侧只写本族，<b>永不写默认族</b>。
        //       曾经反向也有一条"设置页改默认值时顺手把对应项刷成同一个值"（防"我在设置里改了默认却没生效"），
        //       2026-09-24 录屏栏瘦身后设置页对这七项已无控件 ⇒ 那段回填机器整体删除，两族再无交叉写入点。
        // 未记录的哨兵＝null／空串：老 settings.ini 里没有这些键 ⇒ 首次读取自然回落到默认值，升级无感。
        /// <summary>上次在预录制参数条上选的画质（空＝未记录，回落 <see cref="RecQuality"/>）。</summary>
        public string RecLastQuality = "";
        /// <summary>上次在预览窗上选的导出格式（空＝未记录，回落 <see cref="RecFormat"/>）。</summary>
        public string RecLastFormat = "";
        /// <summary>上次选的录制延迟秒数（null＝未记录，回落 <see cref="RecCountdownSec"/>）。</summary>
        public int? RecLastCountdownSec;
        /// <summary>上次勾的系统声音（null＝未记录，回落 <see cref="RecSystemAudio"/>）。</summary>
        public bool? RecLastSystemAudio;
        /// <summary>上次勾的麦克风（null＝未记录，回落 <see cref="RecMicAudio"/>）。</summary>
        public bool? RecLastMicAudio;
        /// <summary>上次勾的按键显示（null＝未记录，回落 <see cref="RecVisKeycap"/>）。</summary>
        public bool? RecLastVisKeycap;
        /// <summary>上次勾的点击涟漪（null＝未记录，回落 <see cref="RecVisRipple"/>）。</summary>
        public bool? RecLastVisRipple;

        /// <summary>「上次使用」优先、「默认」兜底（字符串族：空串＝未记录）。</summary>
        public static string Pick(string last, string def) => string.IsNullOrEmpty(last) ? def : last;
        /// <summary>见 <see cref="Pick(string,string)"/>。</summary>
        public static int Pick(int? last, int def) => last ?? def;
        /// <summary>见 <see cref="Pick(string,string)"/>。</summary>
        public static bool Pick(bool? last, bool def) => last ?? def;
        /// <summary>settings.ini 里是否出现过 RecMigrated 键。仅加载期使用，不参与序列化。</summary>
        private bool recMigratedInFile = false;
        private bool clearAllAnnotationsMigratedInFile = false;

        // 云同步 — 通用
        /// <summary>云存储服务商：none / gdrive / webdav。</summary>
        public string CloudSyncProvider = "none";
        /// <summary>AI／翻译密钥是否共享；自动发送与设置接收均执行本机策略，连接凭据恒留本机。本地备份不受此开关影响。</summary>
        public bool CloudSyncSecrets = true;
        /// <summary>自动双向同步开关，属于本机连接状态，默认关闭。</summary>
        public bool CloudAutoSync = false;

        // 云同步 — WebDAV
        /// <summary>WebDAV 服务地址（如 https://example.com/dav/path）。</summary>
        public string WebDavUrl = "";
        /// <summary>WebDAV 用户名。</summary>
        public string WebDavUsername = "";
        /// <summary>WebDAV 密码或 Token。</summary>
        public string WebDavPassword = "";
        /// <summary>WebDAV 配置文件名（默认 yuSnip-config-v1.json）。</summary>
        public string WebDavFileName = "yuSnip-config-v1.json";

        // 云同步 — Google Drive
        /// <summary>Google OAuth Client ID（可选，用户自行填入以覆盖内置凭据）。</summary>
        public string GoogleDriveClientId = "";
        /// <summary>Google OAuth Client Secret（可选）。</summary>
        public string GoogleDriveClientSecret = "";
        /// <summary>Google Drive OAuth refresh_token（长期有效）。</summary>
        public string GoogleDriveRefreshToken = "";
        /// <summary>Google Drive OAuth access_token（约 1 小时有效）。</summary>
        public string GoogleDriveAccessToken = "";
        /// <summary>access_token 过期时间（Unix 毫秒）。</summary>
        public long GoogleDriveTokenExpiry = 0;
        /// <summary>Google Drive 上配置文件的 ID。</summary>
        public string GoogleDriveFileId = "";
        /// <summary>已登录的 Google 用户邮箱。</summary>
        public string GoogleDriveUserEmail = "";
        /// <summary>已登录的 Google 用户头像 URL。</summary>
        public string GoogleDriveUserAvatar = "";
        /// <summary>上次同步时间（Unix 毫秒，0 表示未同步）。</summary>
        public long GoogleDriveLastSync = 0;

        private static AppSettings _cached;
        private static readonly object _cacheLock = new();

        /// <summary>
        /// 获取当前内存缓存的配置实例（0 耗时，无磁盘 I/O）。
        /// 若尚未加载则执行首次加载。
        /// </summary>
        public static AppSettings Current
        {
            get
            {
                if (_cached != null) return _cached;
                // 读盘可能触发迁移保存，先完成 I/O 再取缓存锁，避免与配置事务反向加锁。
                var loaded = Load();
                lock (_cacheLock)
                {
                    return _cached ??= loaded;
                }
            }
        }

        /// <summary>强制从磁盘重新加载配置并更新内存缓存。</summary>
        public static AppSettings Reload()
        {
            var loaded = Load();
            lock (_cacheLock)
            {
                _cached = loaded;
                return _cached;
            }
        }

        /// <summary>后台云同步原地刷新共享实例；保持既有 Reload 的换实例语义。</summary>
        public static AppSettings ReloadInPlace()
        {
            var loaded = Load();
            lock (_cacheLock)
            {
                if (_cached == null) _cached = loaded;
                else _cached.CopyFrom(loaded);
                return _cached;
            }
        }

        /// <summary>
        /// 把外部（AdminModeCoordinator 独立落盘）改写的 RunAsAdmin 同步进内存缓存。
        /// 设置窗口与截图会话的 _settings 都引用 Current 缓存实例：协调器改盘后若不回写缓存，
        /// 这些实例下一次整体 Save()（设置窗口实时保存 / 会话结束保存标注参数）会把旧 RunAsAdmin
        /// 写回磁盘，造成"托盘已提权、设置页仍显示关闭"的状态脱节。
        /// </summary>
        public static void SyncRunAsAdmin(bool value)
        {
            lock (_cacheLock)
            {
                if (_cached != null) _cached.RunAsAdmin = value;
            }
        }

        private static string _isolatedSettingsDirectory;

        /// <summary>友元自测在首次加载前固定独立目录；进程结束前不恢复，异步收尾也不得写回真实配置。</summary>
        internal static void SetIsolatedDirectoryForTests(string directory)
        {
            string fullPath = Path.GetFullPath(directory);
            lock (_cacheLock)
            {
                if (_cached != null || _isolatedSettingsDirectory != null)
                    throw new InvalidOperationException("自测目录必须在首次加载设置前固定，不能中途切换。");
                _isolatedSettingsDirectory = fullPath;
            }
        }

        public static string SettingsDirectory
        {
            get { return _isolatedSettingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuSnip"); }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(SettingsDirectory, "settings.ini"); }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    s.Save();   // 首份配置落盘；缓存由 Save 负责建立（见方法末尾那段身份稳定性说明）
                    return s;
                }

                string[] lines;
                lock (CloudSync.CloudSnapshot.FileGate) lines = File.ReadAllLines(SettingsPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int p = line.IndexOf('=');
                    if (p <= 0) continue;
                    string key = line.Substring(0, p).Trim();
                    string value = line.Substring(p + 1).Trim();
                    s.Apply(key, value);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load settings.", ex);
            }

            // 一次性迁移：旧版通用翻译密钥 → 分服务商独立字段，清空旧字段防止"幽灵密钥"
            bool translationMigrated = s.MigrateLegacyTranslationKeys();
            translationMigrated |= s.MigrateDefaultTranslationProvider();
            if (translationMigrated)
            {
                s.Save(); // 持久化迁移结果
            }

            // 一次性迁移：已移除的 Beta 测试通道遗留配置 → 还原为默认 AI 配置（用户自填 API）
            if (s.MigrateLegacyBetaChannel())
            {
                s.Save();
            }

            // 平滑迁移：旧配置中的工具栏布局若缺少「智能抠图（matting）」，自动插入到翻译按钮右侧
            if (s.MigrateToolbarLayout())
            {
                s.Save();
            }

            // 平滑迁移：同上，把新增的「水印（watermark）」按键补进老布局（序号右侧）
            if (s.MigrateWatermarkToolbar())
            {
                s.Save();
            }

            // 平滑迁移：同上，把新增的「录屏（record）」按键补进老布局（长截图右侧）
            if (s.MigrateRecordToolbar())
            {
                s.Save();
            }

            if (s.MigrateClearAllAnnotationsToolbar()) s.Save();
            if (s.MigrateLongScrollStickerToolbar()) s.Save();

            // 一次性迁移：十字准星拆为独立设置项，老配置缺键时沿用原「截图辅助线」档位
            if (s.MigrateCrosshairMode())
            {
                s.Save();
            }

            // 确保多 API 配置初始化与旧配置平滑迁移
            s.EnsureDefaultAiConfigs();
            // 确保预设提示词列表初始化（旧版单条提示词自动迁移为默认预设）
            s.EnsureDefaultAiPrompts();
            if (string.IsNullOrWhiteSpace(s.AiActiveConfigId) && s.AiConfigs.Count > 0)
            {
                s.AiActiveConfigId = s.AiConfigs[0].Id;
            }

            // ⚠ 这里**不再**把读出来的实例装进进程缓存（原 `lock(_cacheLock) _cached = s;`）。
            // 缓存身份一旦被一次 Load() 换掉，所有此前从 Current 拿到引用的宿主（设置窗口、截图会话）
            // 就同时变成"带旧值的孤本"，它们各自收尾时的整表 Save() 会把中途别人写好的键回滚掉——
            // 这正是"录屏参数条勾了记录键盘/鼠标，下次打开又是空白"的成因（2026-09-26 查 log 定案：
            // 勾选当刻已落盘，同一拍会话结束的 Save() 把它抹掉）。首次初始化由 Current 的 `??=` 负责，
            // 需要"重读磁盘并让全应用改看新值"的场合（开窗、云端恢复）一律走 Reload()。
            return s;
        }

        /// <summary>
        /// 将旧版通用翻译密钥（TranslateAppId / TranslateSecretKey）迁移到当前服务商的独立字段，
        /// 然后清空旧字段。返回 true 表示发生了迁移。
        /// </summary>
        public bool MigrateLegacyTranslationKeys()
        {
            if (string.IsNullOrWhiteSpace(TranslateAppId) && string.IsNullOrWhiteSpace(TranslateSecretKey))
                return false; // 旧字段已为空，无需迁移

            var provider = ParseTranslateProvider(TranslateProvider);

            // 根据当前服务商迁移；若服务商为 None，根据 AppId 格式猜测（纯数字 → 百度）
            if (provider == Translation.TranslationProvider.Baidu ||
                (provider == Translation.TranslationProvider.None &&
                 !string.IsNullOrWhiteSpace(TranslateAppId) &&
                 TranslateAppId.All(char.IsDigit)))
            {
                if (string.IsNullOrWhiteSpace(BaiduAppId)) BaiduAppId = TranslateAppId ?? "";
                if (string.IsNullOrWhiteSpace(BaiduSecretKey)) BaiduSecretKey = TranslateSecretKey ?? "";
            }
            else if (provider == Translation.TranslationProvider.Youdao)
            {
                if (string.IsNullOrWhiteSpace(YoudaoAppId)) YoudaoAppId = TranslateAppId ?? "";
                if (string.IsNullOrWhiteSpace(YoudaoSecretKey)) YoudaoSecretKey = TranslateSecretKey ?? "";
            }

            AppLogger.Info("AppSettings: 已将旧版通用翻译密钥迁移到分服务商独立字段，旧字段已清空。");

            // 清空旧字段，彻底消除"幽灵密钥"
            TranslateAppId = "";
            TranslateSecretKey = "";
            return true;
        }

        /// <summary>只迁移一次无凭证的旧默认；此后手动选择「不启用」不得在重启时被反向打开。</summary>
        internal bool MigrateDefaultTranslationProvider()
        {
            if (_translateProviderDefaultMigrated) return false;
            _translateProviderDefaultMigrated = true;
            bool paidConfigured = (!string.IsNullOrWhiteSpace(BaiduAppId) && !string.IsNullOrWhiteSpace(BaiduSecretKey))
                || (!string.IsNullOrWhiteSpace(YoudaoAppId) && !string.IsNullOrWhiteSpace(YoudaoSecretKey));
            if (!paidConfigured) TranslateProvider = "BilibiliIndex";
            return true;
        }

        /// <summary>
        /// 一次性迁移：仅清除 Beta 测试通道遗留的 "dp:" 机器绑定密文
        /// （保护类已删除、无法解密，等同无效密钥）。
        /// 注意：用户自行填写的地址/模型/明文密钥（含指向自建中转的配置）属于正常配置，一律不触碰。
        /// </summary>
        public bool MigrateLegacyBetaChannel()
        {
            bool migrated = false;
            string key = AiApiKey ?? "";
            if (key.StartsWith("dp:", StringComparison.Ordinal))
            {
                AiApiKey = "";
                migrated = true;
            }
            if (AiConfigs != null)
            {
                foreach (var cfg in AiConfigs)
                {
                    if (cfg.ApiKey != null && cfg.ApiKey.StartsWith("dp:", StringComparison.Ordinal))
                    {
                        cfg.ApiKey = "";
                        migrated = true;
                    }
                }
            }
            if (migrated)
            {
                AppLogger.Info("AppSettings: 已清除 Beta 通道遗留 dp: 密文（机器绑定密钥，已不可解密）。");
            }
            return migrated;
        }

        /// <summary>
        /// 平滑迁移工具栏布局：仅针对老版本升级场景（INI 中尚未记录 ToolbarMigrated 且布局未包含智能抠图）。
        /// 首次升级时自动将其插入到「翻译」右侧，之后标记 ToolbarMigrated=True 并落盘。
        /// 一旦迁移完成或用户处于新版本，完全尊重用户的自由配置（包括将抠图退回工具池），绝不再重复注入。
        /// 返回 true 表示发生了迁移并需要保存。
        /// </summary>
        public bool MigrateToolbarLayout()
        {
            if (toolbarMigratedInFile) return false;

            bool inTokens = ToolbarRegistry.HasId(ToolbarRegistry.Parse(ToolbarLayout), "matting");
            bool inGroups = !string.IsNullOrWhiteSpace(ToolbarGroups) &&
                            ToolbarRegistry.HasId(ToolbarRegistry.ParseLibrary(ToolbarGroups), "matting");
            if (inTokens || inGroups)
            {
                ToolbarMigrated = true;
                return true;
            }

            if (string.IsNullOrWhiteSpace(ToolbarLayout))
            {
                ToolbarLayout = ToolbarRegistry.DefaultLayout;
                ToolbarMigrated = true;
                return true;
            }

            List<string> tokens = ToolbarRegistry.Parse(ToolbarLayout);
            if (ToolbarRegistry.MigrateTokens(tokens))
            {
                ToolbarLayout = ToolbarRegistry.Normalize(string.Join(",", tokens));
                AppLogger.Info($"AppSettings: 老版本升级，已自动将「智能抠图」融入工具栏布局: {ToolbarLayout}");
            }
            ToolbarMigrated = true;
            return true;
        }

        /// <summary>
        /// 平滑迁移工具栏布局：新增「水印（watermark）」按键对老配置的补位——老布局串里没这一格时，
        /// 按出厂默认的相对位置插到「序号」右侧（序号也被收进组时插在那一组右侧）。
        /// 判据与 matting 那次严格同构：<b>只在 INI 中从未出现过 <c>WatermarkMigrated</c> 键时执行一次</b>，
        /// 之后用户主动把水印拖回工具池就不会再被插回。返回 true 表示需要落盘。
        /// </summary>
        public bool MigrateWatermarkToolbar()
        {
            if (watermarkMigratedInFile) return false;

            if (string.IsNullOrWhiteSpace(ToolbarLayout))
            {
                ToolbarLayout = ToolbarRegistry.DefaultLayout;
            }
            else
            {
                List<string> tokens = ToolbarRegistry.Parse(ToolbarLayout);
                if (ToolbarRegistry.InsertTokenAfter(tokens, "watermark", "counter", "rectangle", "text", "arrow", "brush"))
                {
                    ToolbarLayout = ToolbarRegistry.Normalize(string.Join(",", tokens));
                    AppLogger.Info($"AppSettings: 老版本升级，已自动将「水印」融入工具栏布局: {ToolbarLayout}");
                }
            }
            WatermarkMigrated = true;
            return true;
        }

        /// <summary>
        /// 平滑迁移工具栏布局：新增「录屏（record）」按键对老配置的补位——老布局串里没这一格时，
        /// 按出厂默认的相对位置插到「长截图」右侧（长截图也被收进组时插在那一组右侧）。
        /// 判据与 watermark 那次严格同构：<b>只在 INI 中从未出现过 <c>RecMigrated</c> 键时执行一次</b>，
        /// 之后用户主动把录屏格拖回工具池就不会再被插回。返回 true 表示需要落盘。
        /// </summary>
        public bool MigrateRecordToolbar()
        {
            if (recMigratedInFile) return false;

            if (string.IsNullOrWhiteSpace(ToolbarLayout))
            {
                ToolbarLayout = ToolbarRegistry.DefaultLayout;
            }
            else
            {
                List<string> tokens = ToolbarRegistry.Parse(ToolbarLayout);
                if (ToolbarRegistry.InsertTokenAfter(tokens, "record", "longscroll", "spotlight", "mosaic"))
                {
                    ToolbarLayout = ToolbarRegistry.Normalize(string.Join(",", tokens));
                    AppLogger.Info($"AppSettings: 老版本升级，已自动将「录屏」融入工具栏布局: {ToolbarLayout}");
                }
            }
            RecMigrated = true;
            return true;
        }

        /// <summary>
        /// 一次性迁移（2026-10-06 反向）：主工具栏不再放独立的「清除全部标注」按钮——全清入口收在橡皮擦参数条里，
        /// 主栏那一枚与它重复。这里把老布局里剩下的这一格摘掉（留着也不崩：未知 token 会被布局跳过，
        /// 但设置页的排序列表会多出一格谁也点不到的空白）。
        /// </summary>
        public bool MigrateClearAllAnnotationsToolbar()
        {
            List<string> tokens = string.IsNullOrWhiteSpace(ToolbarLayout)
                ? ToolbarRegistry.Parse(ToolbarRegistry.DefaultLayout)
                : ToolbarRegistry.Parse(ToolbarLayout);
            bool removed = tokens.RemoveAll(t => string.Equals(t, "clearall", StringComparison.OrdinalIgnoreCase)) > 0;
            bool needsSave = removed || !clearAllAnnotationsMigratedInFile || !ClearAllAnnotationsMigrated;
            if (removed || string.IsNullOrWhiteSpace(ToolbarLayout))
                ToolbarLayout = ToolbarRegistry.Normalize(string.Join(",", tokens));
            if (removed) AppLogger.Info("AppSettings: 已从截图主工具栏布局摘掉独立的「清除全部标注」格（入口收进橡皮擦参数条）。");
            ClearAllAnnotationsMigrated = true;
            clearAllAnnotationsMigratedInFile = true;
            return needsSave;
        }

        public bool MigrateLongScrollStickerToolbar()
        {
            if (LongScrollStickerMigrated) return false;
            const string previousDefault = "axis,autoscroll,|,trim,quicksave,saveas,cancel,finish";
            // 只升级未自定义的旧默认栏；自定义排序、显隐及备用组均保留。
            if (LongScrollToolbarLayout == previousDefault && string.IsNullOrWhiteSpace(LongScrollToolbarGroups))
                LongScrollToolbarLayout = ToolbarScenes.Get(ToolbarScene.LongScroll).DefaultLayout;
            LongScrollStickerMigrated = true;
            return true;
        }

        /// <summary>
        /// 一次性迁移：十字准星从「截图辅助线」拆出为独立设置项后，老配置缺 <c>CrosshairMode</c> 键。
        /// 拆分前参考线与准星由同一个开关同时控制，故缺键时直接抄 <see cref="GuidelineMode"/> 的当前值，
        /// 升级后的观感与升级前逐像素一致。返回 true 表示发生了迁移并需要保存。
        /// </summary>
        public bool MigrateCrosshairMode()
        {
            if (crosshairModeInFile) return false;
            CrosshairMode = NormalizeGuidelineMode(GuidelineMode);
            AppLogger.Info($"AppSettings: 十字准星已拆为独立设置项，沿用原「截图辅助线」档位「{CrosshairMode}」。");
            return true;
        }

        public void Save()
        {
            TrySave();
        }

        internal bool TrySave()
        {
            // 包住取值、写盘和缓存回填，事务应用期间不允许排队的旧快照随后整表写回。
            lock (CloudSync.CloudSnapshot.FileGate) return SaveLocked();
        }

        private bool SaveLocked()
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory)) Directory.CreateDirectory(SettingsDirectory);
                List<string> lines = new List<string>();
                lines.Add("# yuSnip settings");
                lines.Add("SaveDirectory=" + (SaveDirectory ?? ""));
                lines.Add("FileNameTemplate=" + (FileNameTemplate ?? ""));
                lines.Add("ImageFormat=PNG"); // 强制 PNG，忽略废弃字段
                lines.Add("JpegQuality=" + JpegQuality.ToString());
                lines.Add("AutoDetectMode=" + NormalizeAutoDetectMode(AutoDetectMode));
                lines.Add("ElementDwellMs=" + ElementDwellMs.ToString());
                lines.Add("ElementDwellDeadbandPx=" + ElementDwellDeadbandPx.ToString());
                lines.Add("CopyToClipboardAfterSave=" + CopyToClipboardAfterSave.ToString());
                lines.Add("ShowSaveNotification=" + ShowSaveNotification.ToString());
                lines.Add("PlaySaveSound=" + PlaySaveSound.ToString());
                lines.Add("ShowLongScrollPreview=" + ShowLongScrollPreview.ToString());
                lines.Add("GuidelineMode=" + NormalizeGuidelineMode(GuidelineMode));
                lines.Add("CrosshairMode=" + NormalizeGuidelineMode(CrosshairMode));
                lines.Add("ShowCaptureHints=" + ShowCaptureHints.ToString());
                lines.Add("CaptureCornerRadius=" + Capture.SelectionEffects.NormalizeParam(CaptureCornerRadius));
                lines.Add("CaptureShadow=" + CaptureShadow.ToString());
                lines.Add("CaptureStrokeWidth=" + Capture.SelectionEffects.NormalizeStrokeParam(CaptureStrokeWidth));
                lines.Add("CaptureStrokeColor=" + CaptureStrokeColor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("SelectionBorderStyle=" + NormalizeSelectionBorderStyle(SelectionBorderStyle));
                lines.Add("SelectionBorderColorArgb=" + SelectionBorderColorArgb.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("SelectionBorderThickness=" + NormalizeSelectionBorderThickness(SelectionBorderThickness).ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("ReuseLastAnnotationTool=" + ReuseLastAnnotationTool.ToString());
                lines.Add("LastAnnotationTool=" + (LastAnnotationTool ?? ""));
                lines.Add("MattingDefaultModel=" + NormalizeMattingDefaultModel(MattingDefaultModel));
                lines.Add("ThemeMode=" + (ThemeMode ?? ""));
                lines.Add("ToolbarLayout=" + ToolbarRegistry.Normalize(ToolbarLayout));
                lines.Add("ToolbarGroups=" + ToolbarRegistry.SerializeLibrary(ToolbarRegistry.ParseLibrary(ToolbarGroups)));
                foreach (ToolbarScene scene in new[] { ToolbarScene.StickerAnnotation, ToolbarScene.RecordPreparation, ToolbarScene.RecordControl, ToolbarScene.VideoPreview, ToolbarScene.LongScroll })
                {
                    lines.Add(ToolbarScenes.SettingKey(scene) + "=" + ToolbarScenes.Get(scene).Normalize(ToolbarScenes.Read(this, scene)));
                    lines.Add(ToolbarScenes.GroupsKey(scene) + "=" + string.Join(",", ToolbarScenes.Get(scene).ParseLibrary(ToolbarScenes.ReadGroups(this, scene), ToolbarScenes.Get(scene).Parse(ToolbarScenes.Read(this, scene)))));
                }
                lines.Add("ToolbarMigrated=" + ToolbarMigrated.ToString());
                lines.Add("WatermarkMigrated=" + WatermarkMigrated.ToString());
                lines.Add("RecMigrated=" + RecMigrated.ToString());
                lines.Add("ClearAllAnnotationsMigrated=" + ClearAllAnnotationsMigrated.ToString());
                lines.Add("LongScrollStickerMigrated=" + LongScrollStickerMigrated.ToString());
            lines.Add("RunOnStartup=" + RunOnStartup.ToString());
            lines.Add("RunAsAdmin=" + RunAsAdmin.ToString());
            lines.Add("ShowStartupNotification=" + ShowStartupNotification.ToString());
                lines.Add("HotKeyModifiers=" + HotKeyModifiers.ToString());
                lines.Add("HotKeyVirtualKey=" + HotKeyVirtualKey.ToString());
                lines.Add("StickerHotKeyModifiers=" + StickerHotKeyModifiers.ToString());
                lines.Add("StickerHotKeyVirtualKey=" + StickerHotKeyVirtualKey.ToString());
                lines.Add("StickerDoubleClickAction=" + NormalizeStickerDoubleClickAction(StickerDoubleClickAction));
                lines.Add("StickerDefaultShadow=" + StickerDefaultShadow.ToString());
                lines.Add("StickerShadowMode=" + NormalizeStickerShadowMode(StickerShadowMode));
                lines.Add("StickerTransparentCheckerboard=" + StickerTransparentCheckerboard.ToString());
                lines.Add("StickerTextSelectable=" + StickerTextSelectable.ToString());
                lines.Add("StickerGrabSelectedText=" + StickerGrabSelectedText.ToString());
                lines.Add("StickerResizeReflowsText=" + StickerResizeReflowsText.ToString());
                lines.Add("StickerColorPickerEnabled=" + StickerColorPickerEnabled.ToString());
                lines.Add("StickerSelectionToolbar=" + StickerSelectionToolbar.ToString());
                lines.Add("StickerRestoreSortMode=" + NormalizeStickerRestoreSortMode(StickerRestoreSortMode));
                lines.Add("TranslateProvider=" + NormalizeTranslateProvider(TranslateProvider));
                lines.Add("TranslateProviderDefaultVersion=1");
                lines.Add("TranslateAppId="); // 已废弃，保留空行仅为旧版兼容
                lines.Add("TranslateSecretKey=");
                lines.Add("BaiduAppId=" + (BaiduAppId ?? ""));
                lines.Add("BaiduSecretKey=" + (BaiduSecretKey ?? ""));
                lines.Add("YoudaoAppId=" + (YoudaoAppId ?? ""));
                lines.Add("YoudaoSecretKey=" + (YoudaoSecretKey ?? ""));
                lines.Add("TranslateSourceLanguage=" + (TranslateSourceLanguage ?? "auto"));
                lines.Add("TranslateTargetLanguage=" + (TranslateTargetLanguage ?? "zh"));
                lines.Add("TranslateSecondaryTargetLanguage=" + (TranslateSecondaryTargetLanguage ?? "en"));
            lines.Add("TranslateMode=" + NormalizeTranslateMode(TranslateMode));
            lines.Add("TranslateStreamEnabled=" + TranslateStreamEnabled.ToString());
            lines.Add("TranslateMiniWindowPosition=" + NormalizeTranslateMiniWindowPosition(TranslateMiniWindowPosition));
            lines.Add("TranslateMiniWindowAutoClose=" + TranslateMiniWindowAutoClose.ToString());
            lines.Add("TranslateMiniWindowAutoCopy=" + TranslateMiniWindowAutoCopy.ToString());
            lines.Add("TranslationEditWindowWidth=" + TranslationEditWindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TranslationEditWindowHeight=" + TranslationEditWindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TranslationEditDrawerWidth=" + TranslationEditDrawerWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TranslationEditDrawerHeight=" + TranslationEditDrawerHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("OcrOutputMode=" + NormalizeOcrOutputMode(OcrOutputMode));
            lines.Add("OcrToastPosition=" + NormalizeOcrToastPosition(OcrToastPosition));
            lines.Add("OcrToastSeconds=" + NormalizeOcrToastSeconds(OcrToastSeconds));
            // 截图历史
            lines.Add("CaptureHistoryMode=" + NormalizeCaptureHistoryMode(CaptureHistoryMode));
            lines.Add("CaptureHistoryMaxCount=" + NormalizeCaptureHistoryMaxCount(CaptureHistoryMaxCount));
            lines.Add("CaptureHistoryMaxMegabytes=" + NormalizeCaptureHistoryMaxMegabytes(CaptureHistoryMaxMegabytes));
            lines.Add("CaptureHistoryWindowWidth=" + (CaptureHistoryWindowWidth > 0 ? CaptureHistoryWindowWidth : 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("CaptureHistoryWindowHeight=" + (CaptureHistoryWindowHeight > 0 ? CaptureHistoryWindowHeight : 680).ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("CaptureRectHistory=" + (CaptureRectHistory ?? ""));
            lines.Add("CaptureAutoOcr=" + CaptureAutoOcr.ToString());
            // AI 大模型 / 识图
            lines.Add("AiUseDefaultPrompt=" + AiUseDefaultPrompt.ToString());
            lines.Add("AiPromptWindowPosition=" + NormalizeAiPromptWindowPosition(AiPromptWindowPosition));
            lines.Add("AiActiveConfigId=" + (AiActiveConfigId ?? ""));
            lines.Add("AiConfigsCount=" + (AiConfigs?.Count ?? 0).ToString());
            if (AiConfigs != null)
            {
                for (int i = 0; i < AiConfigs.Count; i++)
                {
                    var cfg = AiConfigs[i];
                    string prefix = "AiProfile_" + i + "_";
                    lines.Add(prefix + "Id=" + (cfg.Id ?? ""));
                    lines.Add(prefix + "Name=" + (cfg.Name ?? ""));
                    lines.Add(prefix + "IsEnabled=" + cfg.IsEnabled.ToString());
                    lines.Add(prefix + "Protocol=" + (cfg.Protocol ?? "OpenAI"));
                    lines.Add(prefix + "BaseUrl=" + (cfg.BaseUrl ?? ""));
                    lines.Add(prefix + "ApiKey=" + (cfg.ApiKey ?? ""));
                    lines.Add(prefix + "DefaultModel=" + (cfg.DefaultModel ?? ""));
                    lines.Add(prefix + "CustomModels=" + (cfg.CustomModels != null ? string.Join(",", cfg.CustomModels) : ""));
                    lines.Add(prefix + "CachedModels=" + (cfg.CachedModels != null ? string.Join(",", cfg.CachedModels) : ""));
                }
            }
            lines.Add("AiProtocol=" + (AiProtocol ?? "OpenAI"));
            lines.Add("AiBaseUrl=" + (AiBaseUrl ?? "https://api.openai.com/v1"));
            lines.Add("AiApiKey=" + (AiApiKey ?? ""));
            lines.Add("AiModel=" + (AiModel ?? ""));
            lines.Add("AiCustomPrompt=" + (AiCustomPrompt ?? DefaultAiCustomPrompt));
            // 预设提示词列表（多预设 + 技能命令 + 默认标记），内容做换行转义保证单行格式
            lines.Add("AiPromptPresetsCount=" + (AiPromptPresets?.Count ?? 0).ToString());
            if (AiPromptPresets != null)
            {
                for (int i = 0; i < AiPromptPresets.Count; i++)
                {
                    var pre = AiPromptPresets[i];
                    string prefix = "AiPrompt_" + i + "_";
                    lines.Add(prefix + "Id=" + (pre.Id ?? ""));
                    lines.Add(prefix + "Name=" + (pre.Name ?? ""));
                    lines.Add(prefix + "Content=" + EncodeMultiline(pre.Content ?? ""));
                    lines.Add(prefix + "IsDefault=" + pre.IsDefault.ToString());
                }
            }
            lines.Add("AiSearchEnabled=" + AiSearchEnabled.ToString());
            lines.Add("AiThinkingEnabled=" + AiThinkingEnabled.ToString());
            lines.Add("AiSearchEndpoint=" + (AiSearchEndpoint ?? "https://searx.be"));
            lines.Add("AiTimeoutSeconds=" + AiTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("AiVisionWindowWidth=" + (AiVisionWindowWidth > 0 ? AiVisionWindowWidth : 480).ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("AiVisionWindowHeight=" + (AiVisionWindowHeight > 0 ? AiVisionWindowHeight : 640).ToString(System.Globalization.CultureInfo.InvariantCulture));
            // 标注工具状态
            lines.Add("AnnotationColor=" + AnnotationColor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("StrokeThickness=" + StrokeThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("BrushLineStyle=" + (BrushLineStyle ?? "Solid"));
            lines.Add("BrushOpacity=" + BrushOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("BrushHardness=" + BrushHardness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("BrushTaperMode=" + (BrushTaperMode ?? "Velocity"));
            lines.Add("BrushTaperStrength=" + BrushTaperStrength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("BrushSmoothing=" + BrushSmoothing.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("BrushMinWidthRatio=" + BrushMinWidthRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("ArrowStyle=" + (ArrowStyle ?? "Designed"));
            lines.Add("ShapeKind=" + (ShapeKind ?? "Rectangle"));
            lines.Add("ShapeLineStyle=" + (ShapeLineStyle ?? "Solid"));
            lines.Add("ShapeCornerRadius=" + ShapeCornerRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("ShapeFill=" + ShapeFill.ToString());
            lines.Add("ShapeFillOpacity=" + ShapeFillOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("BlurMode=" + (BlurMode ?? "Blur"));
            lines.Add("BlurUiLevel=" + BlurUiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("SpotlightDarkness=" + SpotlightDarkness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("SpotlightStrokeThickness=" + SpotlightStrokeThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("SpotlightCornerRadius=" + SpotlightCornerRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("CounterRadius=" + CounterRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("CounterCapsuleCorner=" + CounterCapsuleCorner.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("CounterSerial=" + (CounterSerial ?? "Number"));
            lines.Add("CounterGlyph=" + (CounterGlyph ?? "Circle"));
            lines.Add("CounterFill=" + (CounterFill ?? "Solid"));
            lines.Add("CounterTailHead=" + (CounterTailHead ?? "Sharp"));
            lines.Add("CounterRenumberOnDelete=" + CounterRenumberOnDelete.ToString());
            lines.Add("CounterRememberStyle=" + CounterRememberStyle.ToString());
            lines.Add("TextFontSize=" + TextFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TextFontFamily=" + (TextFontFamily ?? "Microsoft YaHei UI"));
            lines.Add("TextIsBold=" + TextIsBold.ToString());
            lines.Add("TextIsItalic=" + TextIsItalic.ToString());
            lines.Add("TextHasStroke=" + TextHasStroke.ToString());
            lines.Add("TextHasBackground=" + TextHasBackground.ToString());
            lines.Add("TextStrokeThickness=" + TextStrokeThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TextStrokeColor=" + TextStrokeColor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TextFillColor=" + TextFillColor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TextFillOpacity=" + TextFillOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TextFillCornerRadius=" + TextFillCornerRadius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("TextFillPadding=" + TextFillPadding.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // 水印标注状态
            lines.Add("WatermarkText=" + (WatermarkText ?? ""));
            lines.Add("WatermarkPosition=" + (string.IsNullOrEmpty(WatermarkPosition) ? "Tile" : WatermarkPosition));
            lines.Add("WatermarkOpacity=" + WatermarkOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("WatermarkSize=" + WatermarkSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("WatermarkSpacing=" + WatermarkSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("RecQuality=" + RecQuality);
            lines.Add("RecFormat=" + RecFormat);
            lines.Add("RecCountdownSec=" + RecCountdownSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("RecSystemAudio=" + RecSystemAudio.ToString());
            lines.Add("RecMicAudio=" + RecMicAudio.ToString());
            lines.Add("RecAudioMix=" + RecAudioMix);
            lines.Add("RecMicVolume=" + RecMicVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("RecVisCursor=" + RecVisCursor.ToString());
            lines.Add("RecVisRipple=" + RecVisRipple.ToString());
            lines.Add("RecVisKeycap=" + RecVisKeycap.ToString());
            lines.Add("RecShowBadge=" + RecShowBadge.ToString());
            lines.Add("RecPreviewLoop=" + RecPreviewLoop.ToString());
            lines.Add("RecBurnProgressBar=" + RecBurnProgressBar.ToString());
            lines.Add("RecFfmpegPath=" + RecFfmpegPath);
            lines.Add("RecCaptureSource=" + RecCaptureSource);
            lines.Add("RecEngine=" + RecEngine);
            // 「上次使用」族：未记录（哨兵）就不写这一行，保持 ini 干净（族说明见声明处）
            if (!string.IsNullOrEmpty(RecLastQuality)) lines.Add("RecLastQuality=" + RecLastQuality);
            if (!string.IsNullOrEmpty(RecLastFormat)) lines.Add("RecLastFormat=" + RecLastFormat);
            if (RecLastCountdownSec.HasValue) lines.Add("RecLastCountdownSec=" + RecLastCountdownSec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (RecLastSystemAudio.HasValue) lines.Add("RecLastSystemAudio=" + RecLastSystemAudio.Value.ToString());
            if (RecLastMicAudio.HasValue) lines.Add("RecLastMicAudio=" + RecLastMicAudio.Value.ToString());
            if (RecLastVisKeycap.HasValue) lines.Add("RecLastVisKeycap=" + RecLastVisKeycap.Value.ToString());
            if (RecLastVisRipple.HasValue) lines.Add("RecLastVisRipple=" + RecLastVisRipple.Value.ToString());
            lines.Add("RecSaveDirectory=" + RecSaveDirectory);
            // 云同步 — 通用
            lines.Add("CloudSyncProvider=" + (CloudSyncProvider ?? "none"));
            lines.Add("CloudSyncSecrets=" + CloudSyncSecrets.ToString());
            lines.Add("CloudAutoSync=" + CloudAutoSync.ToString());
            // 云同步 — WebDAV
            lines.Add("WebDavUrl=" + (WebDavUrl ?? ""));
            lines.Add("WebDavUsername=" + (WebDavUsername ?? ""));
            lines.Add("WebDavPassword=" + (WebDavPassword ?? ""));
            lines.Add("WebDavFileName=" + (WebDavFileName ?? "yuSnip-config-v1.json"));
            // 云同步 — Google Drive
            lines.Add("GoogleDriveClientId=" + (GoogleDriveClientId ?? ""));
            lines.Add("GoogleDriveClientSecret=" + (GoogleDriveClientSecret ?? ""));
            lines.Add("GoogleDriveRefreshToken=" + (GoogleDriveRefreshToken ?? ""));
            lines.Add("GoogleDriveAccessToken=" + (GoogleDriveAccessToken ?? ""));
            lines.Add("GoogleDriveTokenExpiry=" + GoogleDriveTokenExpiry.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lines.Add("GoogleDriveFileId=" + (GoogleDriveFileId ?? ""));
            lines.Add("GoogleDriveUserEmail=" + (GoogleDriveUserEmail ?? ""));
            lines.Add("GoogleDriveUserAvatar=" + (GoogleDriveUserAvatar ?? ""));
            lines.Add("GoogleDriveLastSync=" + GoogleDriveLastSync.ToString(System.Globalization.CultureInfo.InvariantCulture));
            CloudSync.CloudConfigTransaction.AtomicWrite(SettingsPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            _translateProviderDefaultMigrated = true;
            // 缓存对象身份必须稳定：这里原先是 `_cached = this.Clone()`，等于"谁最后存盘，谁就成了全进程的当前配置"。
            // 而设置窗口与截图会话都长期攥着自己拿到的那一份引用（开窗时 / 起跑时），中途只要有别处存过盘，
            // 它们手里那份就变成"带旧值的孤本"，收尾再 Save() 一次，就把别人刚写进去的键整批回滚成旧值。
            // 实测现象：预录制参数条勾「记录键盘操作」→ 当刻已落盘 → 同一次截图会话结束时 _settings.Save() 把它抹掉
            // → 下次开条又是空白（2026-09-24「上次使用」族上线后一直存在，麦克风音量等同理）。
            // 改成"内容并进当前缓存、不换对象"，各宿主引用的自始至终是同一个实例，写就不会互相覆盖。
            lock (_cacheLock)
            {
                if (_cached == null) _cached = this;
                else if (!ReferenceEquals(_cached, this)) _cached.CopyFrom(this);
            }
            return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save settings.", ex);
                return false;
            }
        }

        /// <summary>多行文本转义为单行（settings.ini 逐行解析，换行必须转义）。</summary>
        private static string EncodeMultiline(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>还原 EncodeMultiline 转义的多行文本。</summary>
        private static string DecodeMultiline(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == '\\') { sb.Append('\\'); i++; continue; }
                    if (next == 'r') { sb.Append('\r'); i++; continue; }
                    if (next == 'n') { sb.Append('\n'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public void CopyFrom(AppSettings other)
        {
            if (other == null) return;
            SaveDirectory = other.SaveDirectory;
            FileNameTemplate = other.FileNameTemplate;
            ImageFormat = "PNG"; // 强制 PNG，忽略源对象可能的废弃值
            JpegQuality = other.JpegQuality;
            AutoDetectMode = NormalizeAutoDetectMode(other.AutoDetectMode);
            ElementDwellMs = Math.Min(100, Math.Max(0, other.ElementDwellMs));
            ElementDwellDeadbandPx = Math.Min(100, Math.Max(0, other.ElementDwellDeadbandPx));
            CopyToClipboardAfterSave = other.CopyToClipboardAfterSave;
            ShowSaveNotification = other.ShowSaveNotification;
            PlaySaveSound = other.PlaySaveSound;
            ShowLongScrollPreview = other.ShowLongScrollPreview;
            GuidelineMode = NormalizeGuidelineMode(other.GuidelineMode);
            CrosshairMode = NormalizeGuidelineMode(other.CrosshairMode);
            ShowCaptureHints = other.ShowCaptureHints;
            CaptureCornerRadius = Capture.SelectionEffects.NormalizeParam(other.CaptureCornerRadius);
            CaptureShadow = other.CaptureShadow;
            CaptureStrokeWidth = Capture.SelectionEffects.NormalizeStrokeParam(other.CaptureStrokeWidth);
            CaptureStrokeColor = other.CaptureStrokeColor;
            SelectionBorderStyle = NormalizeSelectionBorderStyle(other.SelectionBorderStyle);
            SelectionBorderColorArgb = other.SelectionBorderColorArgb;
            SelectionBorderThickness = NormalizeSelectionBorderThickness(other.SelectionBorderThickness);
            ReuseLastAnnotationTool = other.ReuseLastAnnotationTool;
            LastAnnotationTool = other.LastAnnotationTool ?? "";
            MattingDefaultModel = NormalizeMattingDefaultModel(other.MattingDefaultModel);
            ThemeMode = other.ThemeMode;
            ToolbarLayout = other.ToolbarLayout;
            ClearAllAnnotationsMigrated = other.ClearAllAnnotationsMigrated;
            ToolbarGroups = other.ToolbarGroups ?? "";
            foreach (ToolbarScene scene in new[] { ToolbarScene.StickerAnnotation, ToolbarScene.RecordPreparation, ToolbarScene.RecordControl, ToolbarScene.VideoPreview, ToolbarScene.LongScroll })
            {
                ToolbarScenes.Write(this, scene, ToolbarScenes.Read(other, scene));
                ToolbarScenes.WriteGroups(this, scene, ToolbarScenes.ReadGroups(other, scene));
            }
            LongScrollStickerMigrated = other.LongScrollStickerMigrated;
            RunOnStartup = other.RunOnStartup;
            RunAsAdmin = other.RunAsAdmin;
            ShowStartupNotification = other.ShowStartupNotification;
            HotKeyModifiers = other.HotKeyModifiers;
            HotKeyVirtualKey = other.HotKeyVirtualKey;
            StickerHotKeyModifiers = other.StickerHotKeyModifiers;
            StickerHotKeyVirtualKey = other.StickerHotKeyVirtualKey;
            StickerDoubleClickAction = NormalizeStickerDoubleClickAction(other.StickerDoubleClickAction);
            StickerDefaultShadow = other.StickerDefaultShadow;
            StickerShadowMode = other.StickerShadowMode;
            StickerTransparentCheckerboard = other.StickerTransparentCheckerboard;
            StickerTextSelectable = other.StickerTextSelectable;
            StickerGrabSelectedText = other.StickerGrabSelectedText;
            StickerResizeReflowsText = other.StickerResizeReflowsText;
            StickerColorPickerEnabled = other.StickerColorPickerEnabled;
            StickerSelectionToolbar = other.StickerSelectionToolbar;
            StickerRestoreSortMode = NormalizeStickerRestoreSortMode(other.StickerRestoreSortMode);
            TranslateProvider = NormalizeTranslateProvider(other.TranslateProvider);
            _translateProviderDefaultMigrated = other._translateProviderDefaultMigrated;
            TranslateAppId = ""; // 已废弃，不再复制
            TranslateSecretKey = "";
            BaiduAppId = other.BaiduAppId ?? "";
            BaiduSecretKey = other.BaiduSecretKey ?? "";
            YoudaoAppId = other.YoudaoAppId ?? "";
            YoudaoSecretKey = other.YoudaoSecretKey ?? "";
            TranslateSourceLanguage = other.TranslateSourceLanguage;
            TranslateTargetLanguage = other.TranslateTargetLanguage;
            TranslateSecondaryTargetLanguage = other.TranslateSecondaryTargetLanguage;
            TranslateMode = NormalizeTranslateMode(other.TranslateMode);
            TranslateStreamEnabled = other.TranslateStreamEnabled;
            TranslateMiniWindowPosition = NormalizeTranslateMiniWindowPosition(other.TranslateMiniWindowPosition);
            TranslateMiniWindowAutoClose = other.TranslateMiniWindowAutoClose;
            TranslateMiniWindowAutoCopy = other.TranslateMiniWindowAutoCopy;
            TranslationEditWindowWidth = other.TranslationEditWindowWidth > 0 ? other.TranslationEditWindowWidth : 760;
            TranslationEditWindowHeight = other.TranslationEditWindowHeight > 0 ? other.TranslationEditWindowHeight : 520;
            TranslationEditDrawerWidth = other.TranslationEditDrawerWidth > 0 ? other.TranslationEditDrawerWidth : 200;
            TranslationEditDrawerHeight = other.TranslationEditDrawerHeight > 0 ? other.TranslationEditDrawerHeight : 96;
            OcrOutputMode = NormalizeOcrOutputMode(other.OcrOutputMode);
            OcrToastPosition = NormalizeOcrToastPosition(other.OcrToastPosition);
            OcrToastSeconds = NormalizeOcrToastSeconds(other.OcrToastSeconds);
            // 截图历史
            CaptureHistoryMode = NormalizeCaptureHistoryMode(other.CaptureHistoryMode);
            CaptureHistoryMaxCount = NormalizeCaptureHistoryMaxCount(other.CaptureHistoryMaxCount);
            CaptureHistoryMaxMegabytes = NormalizeCaptureHistoryMaxMegabytes(other.CaptureHistoryMaxMegabytes);
            CaptureHistoryWindowWidth = other.CaptureHistoryWindowWidth > 0 ? other.CaptureHistoryWindowWidth : 1000;
            CaptureHistoryWindowHeight = other.CaptureHistoryWindowHeight > 0 ? other.CaptureHistoryWindowHeight : 680;
            CaptureRectHistory = other.CaptureRectHistory ?? "";
            CaptureAutoOcr = other.CaptureAutoOcr;
            // AI 大模型 / 识图
            AiUseDefaultPrompt = other.AiUseDefaultPrompt;
            AiPromptWindowPosition = NormalizeAiPromptWindowPosition(other.AiPromptWindowPosition);
            AiActiveConfigId = other.AiActiveConfigId ?? "";
            AiConfigs = other.AiConfigs != null 
                ? other.AiConfigs.Select(c => c.Clone()).ToList() 
                : new List<AiApiConfig>();
            AiProtocol = other.AiProtocol ?? "OpenAI";
            AiBaseUrl = other.AiBaseUrl ?? "https://api.openai.com/v1";
            AiApiKey = other.AiApiKey ?? "";
            AiModel = other.AiModel ?? "";
            AiCustomPrompt = other.AiCustomPrompt ?? DefaultAiCustomPrompt;
            AiPromptPresets = other.AiPromptPresets != null
                ? other.AiPromptPresets.Select(p => p.Clone()).ToList()
                : new List<AiPromptPreset>();
            AiSearchEnabled = other.AiSearchEnabled;
            AiThinkingEnabled = other.AiThinkingEnabled;
            AiSearchEndpoint = string.IsNullOrWhiteSpace(other.AiSearchEndpoint) ? "https://searx.be" : other.AiSearchEndpoint;
            AiTimeoutSeconds = other.AiTimeoutSeconds > 0 ? other.AiTimeoutSeconds : 60;
            AiVisionWindowWidth = other.AiVisionWindowWidth > 0 ? other.AiVisionWindowWidth : 480;
            AiVisionWindowHeight = other.AiVisionWindowHeight > 0 ? other.AiVisionWindowHeight : 640;
            // 标注工具状态
            AnnotationColor = other.AnnotationColor;
            StrokeThickness = other.StrokeThickness;
            BrushLineStyle = other.BrushLineStyle;
            BrushOpacity = other.BrushOpacity;
            BrushHardness = other.BrushHardness;
            BrushTaperMode = other.BrushTaperMode;
            BrushTaperStrength = other.BrushTaperStrength;
            BrushSmoothing = other.BrushSmoothing;
            BrushMinWidthRatio = other.BrushMinWidthRatio;
            ArrowStyle = other.ArrowStyle;
            ShapeKind = other.ShapeKind;
            ShapeLineStyle = other.ShapeLineStyle;
            ShapeCornerRadius = other.ShapeCornerRadius;
            ShapeFill = other.ShapeFill;
            ShapeFillOpacity = other.ShapeFillOpacity;
            BlurMode = other.BlurMode;
            BlurUiLevel = other.BlurUiLevel;
            SpotlightDarkness = other.SpotlightDarkness;
            SpotlightStrokeThickness = other.SpotlightStrokeThickness;
            // 聚光灯圆角必须一起复制。加一个设置键有**三处各自独立**的落点：字段声明 / Save 写出口 /
            // Load 解析分支，而 CopyFrom 是第四处、最容易被忘的那个——云恢复与 Save() 的"外部实例落盘后把内容
            // 并回进程缓存"都走它（见 Save 末尾那段），这里漏掉就等于"盘上存了、缓存里却是 0"：
            // 下一次截图 AppSettings.Current 拿到的仍是 0，滑杆回零，会话结束时再把 0 写回盘，记忆被抹平。
            SpotlightCornerRadius = other.SpotlightCornerRadius;
            CounterRadius = other.CounterRadius;
            CounterCapsuleCorner = other.CounterCapsuleCorner;
            // ⚠ 这一族必须逐个复制：漏一个就是"盘上存了、缓存里却是默认值"（见上方 SpotlightCornerRadius 那段红线）
            CounterSerial = other.CounterSerial;
            CounterGlyph = other.CounterGlyph;
            CounterFill = other.CounterFill;
            CounterTailHead = other.CounterTailHead;
            CounterRenumberOnDelete = other.CounterRenumberOnDelete;
            CounterRememberStyle = other.CounterRememberStyle;
            TextFontSize = other.TextFontSize;
            TextFontFamily = other.TextFontFamily;
            TextIsBold = other.TextIsBold;
            TextIsItalic = other.TextIsItalic;
            TextHasStroke = other.TextHasStroke;
            TextHasBackground = other.TextHasBackground;
            TextStrokeThickness = other.TextStrokeThickness;
            TextStrokeColor = other.TextStrokeColor;
            TextFillColor = other.TextFillColor;
            TextFillOpacity = other.TextFillOpacity;
            TextFillCornerRadius = other.TextFillCornerRadius;
            TextFillPadding = other.TextFillPadding;
            WatermarkText = other.WatermarkText;
            WatermarkPosition = other.WatermarkPosition;
            WatermarkOpacity = other.WatermarkOpacity;
            WatermarkSize = other.WatermarkSize;
            WatermarkSpacing = other.WatermarkSpacing;
            RecQuality = other.RecQuality;
            RecFormat = other.RecFormat;
            RecCountdownSec = other.RecCountdownSec;
            RecSystemAudio = other.RecSystemAudio;
            RecMicAudio = other.RecMicAudio;
            RecAudioMix = other.RecAudioMix;
            RecMicVolume = other.RecMicVolume;
            RecVisCursor = other.RecVisCursor;
            RecVisRipple = other.RecVisRipple;
            RecVisKeycap = other.RecVisKeycap;
            RecShowBadge = other.RecShowBadge;
            RecPreviewLoop = other.RecPreviewLoop;
            RecBurnProgressBar = other.RecBurnProgressBar;
            RecFfmpegPath = other.RecFfmpegPath;
            RecCaptureSource = other.RecCaptureSource;
            RecEngine = other.RecEngine;
            RecLastQuality = other.RecLastQuality;
            RecLastFormat = other.RecLastFormat;
            RecLastCountdownSec = other.RecLastCountdownSec;
            RecLastSystemAudio = other.RecLastSystemAudio;
            RecLastMicAudio = other.RecLastMicAudio;
            RecLastVisKeycap = other.RecLastVisKeycap;
            RecLastVisRipple = other.RecLastVisRipple;
            RecSaveDirectory = other.RecSaveDirectory;
            // 云同步
            CloudSyncProvider = other.CloudSyncProvider ?? "none";
            CloudSyncSecrets = other.CloudSyncSecrets;
            CloudAutoSync = other.CloudAutoSync;
            WebDavUrl = other.WebDavUrl ?? "";
            WebDavUsername = other.WebDavUsername ?? "";
            WebDavPassword = other.WebDavPassword ?? "";
            WebDavFileName = other.WebDavFileName ?? "yuSnip-config-v1.json";
            GoogleDriveClientId = other.GoogleDriveClientId ?? "";
            GoogleDriveClientSecret = other.GoogleDriveClientSecret ?? "";
            GoogleDriveRefreshToken = other.GoogleDriveRefreshToken ?? "";
            GoogleDriveAccessToken = other.GoogleDriveAccessToken ?? "";
            GoogleDriveTokenExpiry = other.GoogleDriveTokenExpiry;
            GoogleDriveFileId = other.GoogleDriveFileId ?? "";
            GoogleDriveUserEmail = other.GoogleDriveUserEmail ?? "";
            GoogleDriveUserAvatar = other.GoogleDriveUserAvatar ?? "";
            GoogleDriveLastSync = other.GoogleDriveLastSync;
        }

        public AppSettings Clone()
        {
            AppSettings s = new AppSettings();
            s.CopyFrom(this);
            return s;
        }

        private void Apply(string key, string value)
        {
            foreach (ToolbarScene scene in new[] { ToolbarScene.StickerAnnotation, ToolbarScene.RecordPreparation, ToolbarScene.RecordControl, ToolbarScene.VideoPreview, ToolbarScene.LongScroll })
            {
                if (string.Equals(key, ToolbarScenes.GroupsKey(scene), StringComparison.OrdinalIgnoreCase))
                {
                    ToolbarScenes.WriteGroups(this, scene, value, filterOnBar: false);
                    return;
                }
                if (!string.Equals(key, ToolbarScenes.SettingKey(scene), StringComparison.OrdinalIgnoreCase)) continue;
                ToolbarScenes.Write(this, scene, value);
                return;
            }
            if (string.Equals(key, "SaveDirectory", StringComparison.OrdinalIgnoreCase)) SaveDirectory = value;
            else if (string.Equals(key, "FileNameTemplate", StringComparison.OrdinalIgnoreCase)) FileNameTemplate = value;
            else if (string.Equals(key, "ImageFormat", StringComparison.OrdinalIgnoreCase)) ImageFormat = "PNG"; // 强制 PNG，兼容旧配置文件里的 JPG/BMP
            else if (string.Equals(key, "JpegQuality", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(value, out v)) JpegQuality = Math.Min(100, Math.Max(1, v));
            }
            else if (string.Equals(key, "AutoDetectMode", StringComparison.OrdinalIgnoreCase)) AutoDetectMode = NormalizeAutoDetectMode(value);
            else if (string.Equals(key, "ElementDwellMs", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out int dwell)) ElementDwellMs = Math.Min(100, Math.Max(0, dwell));
            }
            else if (string.Equals(key, "ElementDwellDeadbandPx", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out int dead)) ElementDwellDeadbandPx = Math.Min(100, Math.Max(0, dead));
            }
            else if (string.Equals(key, "CopyToClipboardAfterSave", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) CopyToClipboardAfterSave = v;
            }
            else if (string.Equals(key, "ShowSaveNotification", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowSaveNotification = v;
            }
            else if (string.Equals(key, "PlaySaveSound", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) PlaySaveSound = v;
            }
            else if (string.Equals(key, "ShowLongScrollPreview", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowLongScrollPreview = v;
            }
            else if (string.Equals(key, "GuidelineMode", StringComparison.OrdinalIgnoreCase)) GuidelineMode = NormalizeGuidelineMode(value);
            else if (string.Equals(key, "CrosshairMode", StringComparison.OrdinalIgnoreCase))
            {
                CrosshairMode = NormalizeGuidelineMode(value);
                crosshairModeInFile = true;
            }
            else if (string.Equals(key, "ShowCaptureHints", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowCaptureHints = v;
            }
            else if (string.Equals(key, "CaptureCornerRadius", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(value, out v)) CaptureCornerRadius = Capture.SelectionEffects.NormalizeParam(v);
            }
            else if (string.Equals(key, "CaptureShadow", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) CaptureShadow = v;
            }
            else if (string.Equals(key, "CaptureStrokeWidth", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(value, out v)) CaptureStrokeWidth = Capture.SelectionEffects.NormalizeStrokeParam(v);
            }
            else if (string.Equals(key, "CaptureStrokeColor", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) CaptureStrokeColor = v;
            }
            else if (string.Equals(key, "SelectionBorderStyle", StringComparison.OrdinalIgnoreCase))
            {
                SelectionBorderStyle = NormalizeSelectionBorderStyle(value);
            }
            else if (string.Equals(key, "SelectionBorderColorArgb", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) SelectionBorderColorArgb = v;
            }
            else if (string.Equals(key, "SelectionBorderThickness", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) SelectionBorderThickness = NormalizeSelectionBorderThickness(v);
            }
            else if (string.Equals(key, "ReuseLastAnnotationTool", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ReuseLastAnnotationTool = v;
            }
            else if (string.Equals(key, "LastAnnotationTool", StringComparison.OrdinalIgnoreCase))
            {
                LastAnnotationTool = value ?? "";
            }
            else if (string.Equals(key, "MattingDefaultModel", StringComparison.OrdinalIgnoreCase))
            {
                MattingDefaultModel = NormalizeMattingDefaultModel(value);
            }
            else if (string.Equals(key, "ThemeMode", StringComparison.OrdinalIgnoreCase))
            {
                ThemeMode = value;
            }
            else if (string.Equals(key, "ToolbarLayout", StringComparison.OrdinalIgnoreCase))
            {
                ToolbarLayout = ToolbarRegistry.Normalize(value);
            }
            else if (string.Equals(key, "ToolbarGroups", StringComparison.OrdinalIgnoreCase))
            {
                ToolbarGroups = ToolbarRegistry.SerializeLibrary(ToolbarRegistry.ParseLibrary(value));
            }
            else if (string.Equals(key, "ToolbarMigrated", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ToolbarMigrated = v;
                toolbarMigratedInFile = true;
            }
            else if (string.Equals(key, "WatermarkMigrated", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) WatermarkMigrated = v;
                watermarkMigratedInFile = true;
            }
            else if (string.Equals(key, "RunOnStartup", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) RunOnStartup = v;
            }
            else if (string.Equals(key, "RunAsAdmin", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) RunAsAdmin = v;
            }
            else if (string.Equals(key, "ShowStartupNotification", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ShowStartupNotification = v;
            }
            else if (string.Equals(key, "HotKeyModifiers", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) HotKeyModifiers = v;
            }
            else if (string.Equals(key, "HotKeyVirtualKey", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) HotKeyVirtualKey = v;
            }
            else if (string.Equals(key, "StickerHotKeyModifiers", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) StickerHotKeyModifiers = v;
            }
            else if (string.Equals(key, "StickerHotKeyVirtualKey", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(value, out v)) StickerHotKeyVirtualKey = v;
            }
            else if (string.Equals(key, "StickerDoubleClickAction", StringComparison.OrdinalIgnoreCase))
            {
                StickerDoubleClickAction = NormalizeStickerDoubleClickAction(value);
            }
            else if (string.Equals(key, "StickerDefaultShadow", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v))
                {
                    StickerDefaultShadow = v;
                    // 🔴 **旧配置保守迁移**（2026-10-01，issue #50）：老 ini 只有这个布尔键、没有
                    // `StickerShadowMode`。此时把档位按原值定成 On/Off 而**不是**新装的默认 Auto——
                    // 直接跳 Auto 会静默改变老用户现有的阴影行为（他若从不勾选截图阴影，所有贴图会突然
                    // 一起失去光晕），那是用户没要求过的变更。新装用户才拿 Auto（字段初值）。
                    // ⚠ 本分支必须早于 `StickerShadowMode` 分支生效：ini 里后者写在后面，存在时会覆盖这里，
                    // 故"显式设过档位"的配置不受本迁移影响（保存时两键一起写，之后就恒有后者）。
                    StickerShadowMode = v ? "On" : "Off";
                }
            }
            else if (string.Equals(key, "StickerShadowMode", StringComparison.OrdinalIgnoreCase))
            {
                StickerShadowMode = NormalizeStickerShadowMode(value);
                // 档位是真源，布尔键只作兼容镜像（Auto 档在老版本里无从表达，按"画"落）
                StickerDefaultShadow = StickerShadowMode != "Off";
            }
            else if (string.Equals(key, "StickerTransparentCheckerboard", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) StickerTransparentCheckerboard = v;
            }
            else if (string.Equals(key, "StickerTextSelectable", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) StickerTextSelectable = v;
            }
            else if (string.Equals(key, "StickerGrabSelectedText", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) StickerGrabSelectedText = v;
            }
            else if (string.Equals(key, "StickerResizeReflowsText", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) StickerResizeReflowsText = v;
            }
            else if (string.Equals(key, "StickerColorPickerEnabled", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) StickerColorPickerEnabled = v;
            }
            else if (string.Equals(key, "StickerSelectionToolbar", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) StickerSelectionToolbar = v;
            }
            else if (string.Equals(key, "StickerRestoreSortMode", StringComparison.OrdinalIgnoreCase)) StickerRestoreSortMode = NormalizeStickerRestoreSortMode(value);
            else if (string.Equals(key, "TranslateProvider", StringComparison.OrdinalIgnoreCase)) TranslateProvider = NormalizeTranslateProvider(value);
            else if (string.Equals(key, "TranslateProviderDefaultVersion", StringComparison.OrdinalIgnoreCase))
                _translateProviderDefaultMigrated = int.TryParse(value, out int version) && version >= 1;
            else if (string.Equals(key, "TranslateAppId", StringComparison.OrdinalIgnoreCase)) TranslateAppId = value;
            else if (string.Equals(key, "TranslateSecretKey", StringComparison.OrdinalIgnoreCase)) TranslateSecretKey = value;
            else if (string.Equals(key, "BaiduAppId", StringComparison.OrdinalIgnoreCase)) BaiduAppId = value;
            else if (string.Equals(key, "BaiduSecretKey", StringComparison.OrdinalIgnoreCase)) BaiduSecretKey = value;
            else if (string.Equals(key, "YoudaoAppId", StringComparison.OrdinalIgnoreCase)) YoudaoAppId = value;
            else if (string.Equals(key, "YoudaoSecretKey", StringComparison.OrdinalIgnoreCase)) YoudaoSecretKey = value;
            else if (string.Equals(key, "TranslateSourceLanguage", StringComparison.OrdinalIgnoreCase)) TranslateSourceLanguage = string.IsNullOrWhiteSpace(value) ? "auto" : value;
            else if (string.Equals(key, "TranslateTargetLanguage", StringComparison.OrdinalIgnoreCase)) TranslateTargetLanguage = string.IsNullOrWhiteSpace(value) ? "zh" : value;
            else if (string.Equals(key, "TranslateSecondaryTargetLanguage", StringComparison.OrdinalIgnoreCase)) TranslateSecondaryTargetLanguage = string.IsNullOrWhiteSpace(value) ? "en" : value;
            else if (string.Equals(key, "TranslateMode", StringComparison.OrdinalIgnoreCase)) TranslateMode = NormalizeTranslateMode(value);
            else if (string.Equals(key, "TranslateStreamEnabled", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out bool enabled)) TranslateStreamEnabled = enabled;
            }
            else if (string.Equals(key, "TranslateMiniWindowPosition", StringComparison.OrdinalIgnoreCase)) TranslateMiniWindowPosition = NormalizeTranslateMiniWindowPosition(value);
            else if (string.Equals(key, "TranslateMiniWindowAutoClose", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) TranslateMiniWindowAutoClose = v;
            }
            else if (string.Equals(key, "TranslateMiniWindowAutoCopy", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) TranslateMiniWindowAutoCopy = v;
            }
            else if (string.Equals(key, "TranslationEditWindowWidth", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TranslationEditWindowWidth = v;
            }
            else if (string.Equals(key, "TranslationEditWindowHeight", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TranslationEditWindowHeight = v;
            }
            else if (string.Equals(key, "TranslationEditDrawerWidth", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TranslationEditDrawerWidth = v;
            }
            else if (string.Equals(key, "TranslationEditDrawerHeight", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TranslationEditDrawerHeight = v;
            }
            else if (string.Equals(key, "OcrOutputMode", StringComparison.OrdinalIgnoreCase)) OcrOutputMode = NormalizeOcrOutputMode(value);
            else if (string.Equals(key, "OcrToastPosition", StringComparison.OrdinalIgnoreCase)) OcrToastPosition = NormalizeOcrToastPosition(value);
            else if (string.Equals(key, "OcrToastSeconds", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out int toastSec)) OcrToastSeconds = NormalizeOcrToastSeconds(toastSec);
            }
            // 截图历史
            else if (string.Equals(key, "CaptureHistoryMode", StringComparison.OrdinalIgnoreCase)) CaptureHistoryMode = NormalizeCaptureHistoryMode(value);
            else if (string.Equals(key, "CaptureHistoryMaxCount", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out int histMax)) CaptureHistoryMaxCount = NormalizeCaptureHistoryMaxCount(histMax);
            }
            else if (string.Equals(key, "CaptureHistoryMaxMegabytes", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out int histMb)) CaptureHistoryMaxMegabytes = NormalizeCaptureHistoryMaxMegabytes(histMb);
            }
            else if (string.Equals(key, "CaptureHistoryWindowWidth", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) CaptureHistoryWindowWidth = v;
            }
            // 截图选区历史：原值收下即可，解析与量程都在 SelectionHistory 里
            else if (string.Equals(key, "CaptureRectHistory", StringComparison.OrdinalIgnoreCase)) CaptureRectHistory = value ?? "";
            else if (string.Equals(key, "CaptureHistoryWindowHeight", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) CaptureHistoryWindowHeight = v;
            }
            else if (string.Equals(key, "CaptureAutoOcr", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) CaptureAutoOcr = v;
            }
            // AI 大模型 / 识图
            else if (string.Equals(key, "AiUseDefaultPrompt", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) AiUseDefaultPrompt = v;
            }
            else if (string.Equals(key, "AiPromptWindowPosition", StringComparison.OrdinalIgnoreCase)) AiPromptWindowPosition = NormalizeAiPromptWindowPosition(value);
            else if (string.Equals(key, "AiActiveConfigId", StringComparison.OrdinalIgnoreCase)) AiActiveConfigId = value;
            else if (key.StartsWith("AiProfile_", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = key.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int idx) && idx >= 0)
                {
                    if (AiConfigs == null) AiConfigs = new List<AiApiConfig>();
                    while (AiConfigs.Count <= idx)
                    {
                        AiConfigs.Add(new AiApiConfig());
                    }
                    var cfg = AiConfigs[idx];
                    string subKey = parts[2];
                    if (string.Equals(subKey, "Id", StringComparison.OrdinalIgnoreCase)) cfg.Id = value;
                    else if (string.Equals(subKey, "Name", StringComparison.OrdinalIgnoreCase)) cfg.Name = value;
                    else if (string.Equals(subKey, "IsEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(value, out bool en)) cfg.IsEnabled = en;
                    }
                    else if (string.Equals(subKey, "Protocol", StringComparison.OrdinalIgnoreCase)) cfg.Protocol = value;
                    else if (string.Equals(subKey, "BaseUrl", StringComparison.OrdinalIgnoreCase)) cfg.BaseUrl = value;
                    else if (string.Equals(subKey, "ApiKey", StringComparison.OrdinalIgnoreCase)) cfg.ApiKey = value;
                    else if (string.Equals(subKey, "DefaultModel", StringComparison.OrdinalIgnoreCase)) cfg.DefaultModel = value;
                    else if (string.Equals(subKey, "CustomModels", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.CustomModels = string.IsNullOrWhiteSpace(value) 
                            ? new List<string>() 
                            : new List<string>(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).Where(m => !string.IsNullOrEmpty(m)));
                    }
                    else if (string.Equals(subKey, "CachedModels", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.CachedModels = string.IsNullOrWhiteSpace(value)
                            ? new List<string>()
                            : new List<string>(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).Where(m => !string.IsNullOrEmpty(m)));
                    }
                }
            }
            else if (string.Equals(key, "AiProtocol", StringComparison.OrdinalIgnoreCase)) AiProtocol = string.IsNullOrWhiteSpace(value) ? "OpenAI" : value;
            else if (string.Equals(key, "AiBaseUrl", StringComparison.OrdinalIgnoreCase)) AiBaseUrl = value;
            else if (string.Equals(key, "AiApiKey", StringComparison.OrdinalIgnoreCase)) AiApiKey = value;
            else if (string.Equals(key, "AiModel", StringComparison.OrdinalIgnoreCase)) AiModel = value;
            else if (string.Equals(key, "AiCustomPrompt", StringComparison.OrdinalIgnoreCase)) AiCustomPrompt = value;
            else if (key.StartsWith("AiPrompt_", StringComparison.OrdinalIgnoreCase))
            {
                // 预设提示词列表项：AiPrompt_{i}_{Id|Name|Content|IsDefault}
                string[] parts = key.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int pIdx) && pIdx >= 0)
                {
                    if (AiPromptPresets == null) AiPromptPresets = new List<AiPromptPreset>();
                    while (AiPromptPresets.Count <= pIdx)
                    {
                        AiPromptPresets.Add(new AiPromptPreset());
                    }
                    var pre = AiPromptPresets[pIdx];
                    string subKey = parts[2];
                    if (string.Equals(subKey, "Id", StringComparison.OrdinalIgnoreCase)) pre.Id = value;
                    else if (string.Equals(subKey, "Name", StringComparison.OrdinalIgnoreCase)) pre.Name = value;
                    else if (string.Equals(subKey, "Content", StringComparison.OrdinalIgnoreCase)) pre.Content = DecodeMultiline(value);
                    else if (string.Equals(subKey, "IsDefault", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(value, out bool def)) pre.IsDefault = def;
                    }
                }
            }
            else if (string.Equals(key, "AiSearchEnabled", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) AiSearchEnabled = v;
            }
            else if (string.Equals(key, "AiThinkingEnabled", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) AiThinkingEnabled = v;
            }
            else if (string.Equals(key, "AiSearchEndpoint", StringComparison.OrdinalIgnoreCase)) AiSearchEndpoint = string.IsNullOrWhiteSpace(value) ? "https://searx.be" : value;
            else if (string.Equals(key, "AiTimeoutSeconds", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) AiTimeoutSeconds = v;
            }
            else if (string.Equals(key, "AiVisionWindowWidth", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) AiVisionWindowWidth = v;
            }
            else if (string.Equals(key, "AiVisionWindowHeight", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) AiVisionWindowHeight = v;
            }
            // 标注工具状态
            else if (string.Equals(key, "AnnotationColor", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) AnnotationColor = v;
            }
            else if (string.Equals(key, "StrokeThickness", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) StrokeThickness = v;
            }
            else if (string.Equals(key, "BrushLineStyle", StringComparison.OrdinalIgnoreCase)) BrushLineStyle = value;
            else if (string.Equals(key, "BrushOpacity", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) BrushOpacity = v;
            }
            else if (string.Equals(key, "BrushHardness", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) BrushHardness = v;
            }
            else if (string.Equals(key, "BrushTaperMode", StringComparison.OrdinalIgnoreCase)) BrushTaperMode = value;
            else if (string.Equals(key, "BrushTaperStrength", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) BrushTaperStrength = v;
            }
            else if (string.Equals(key, "BrushSmoothing", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) BrushSmoothing = v;
            }
            else if (string.Equals(key, "BrushMinWidthRatio", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) BrushMinWidthRatio = v;
            }
            else if (string.Equals(key, "ArrowStyle", StringComparison.OrdinalIgnoreCase)) ArrowStyle = value;
            else if (string.Equals(key, "ShapeKind", StringComparison.OrdinalIgnoreCase)) ShapeKind = value;
            else if (string.Equals(key, "ShapeLineStyle", StringComparison.OrdinalIgnoreCase)) ShapeLineStyle = value;
            else if (string.Equals(key, "ShapeCornerRadius", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) ShapeCornerRadius = v;
            }
            else if (string.Equals(key, "ShapeFill", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) ShapeFill = v;
            }
            else if (string.Equals(key, "ShapeFillOpacity", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) ShapeFillOpacity = v;
            }
            else if (string.Equals(key, "BlurMode", StringComparison.OrdinalIgnoreCase)) BlurMode = value;
            else if (string.Equals(key, "BlurUiLevel", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) BlurUiLevel = v;
            }
            else if (string.Equals(key, "SpotlightDarkness", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) SpotlightDarkness = v;
            }
            else if (string.Equals(key, "SpotlightStrokeThickness", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) SpotlightStrokeThickness = v;
            }
            else if (string.Equals(key, "SpotlightCornerRadius", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) SpotlightCornerRadius = v;
            }
            else if (string.Equals(key, "CounterRadius", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) CounterRadius = v;
            }
            else if (string.Equals(key, "CounterCapsuleCorner", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) CounterCapsuleCorner = v;
            }
            else if (string.Equals(key, "CounterSerial", StringComparison.OrdinalIgnoreCase))
            {
                CounterSerial = value;
            }
            else if (string.Equals(key, "CounterGlyph", StringComparison.OrdinalIgnoreCase))
            {
                CounterGlyph = value;
            }
            else if (string.Equals(key, "CounterFill", StringComparison.OrdinalIgnoreCase))
            {
                CounterFill = value;
            }
            else if (string.Equals(key, "CounterTailHead", StringComparison.OrdinalIgnoreCase))
            {
                CounterTailHead = value;
            }
            else if (string.Equals(key, "CounterRenumberOnDelete", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) CounterRenumberOnDelete = v;
            }
            else if (string.Equals(key, "CounterRememberStyle", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) CounterRememberStyle = v;
            }
            else if (string.Equals(key, "TextFontSize", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TextFontSize = v;
            }
            else if (string.Equals(key, "TextFontFamily", StringComparison.OrdinalIgnoreCase)) TextFontFamily = value;
            else if (string.Equals(key, "TextIsBold", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) TextIsBold = v;
            }
            else if (string.Equals(key, "TextIsItalic", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) TextIsItalic = v;
            }
            else if (string.Equals(key, "TextHasStroke", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) TextHasStroke = v;
            }
            else if (string.Equals(key, "TextHasBackground", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) TextHasBackground = v;
            }
            else if (string.Equals(key, "TextStrokeThickness", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TextStrokeThickness = v;
            }
            else if (string.Equals(key, "TextStrokeColor", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) TextStrokeColor = v;
            }
            else if (string.Equals(key, "TextFillColor", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) TextFillColor = v;
            }
            else if (string.Equals(key, "TextFillOpacity", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TextFillOpacity = v;
            }
            else if (string.Equals(key, "TextFillCornerRadius", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TextFillCornerRadius = v;
            }
            else if (string.Equals(key, "TextFillPadding", StringComparison.OrdinalIgnoreCase))
            {
                double v; if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) TextFillPadding = v;
            }
            // 水印标注状态（此处只解析原值；量程夹取在消费方铺控件那一刻做一次，见 WatermarkOptionsControl.ApplyDefaults）
            else if (string.Equals(key, "WatermarkText", StringComparison.OrdinalIgnoreCase)) WatermarkText = value;
            else if (string.Equals(key, "WatermarkPosition", StringComparison.OrdinalIgnoreCase)) WatermarkPosition = string.IsNullOrWhiteSpace(value) ? "Tile" : value;
            else if (string.Equals(key, "WatermarkOpacity", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) WatermarkOpacity = v;
            }
            else if (string.Equals(key, "WatermarkSize", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) WatermarkSize = v;
            }
            else if (string.Equals(key, "WatermarkSpacing", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) WatermarkSpacing = v;
            }
            // 录屏默认参数（字段族说明见声明处：默认值仓库，参数条改动不回写）
            else if (string.Equals(key, "RecQuality", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) RecQuality = value;
            }
            else if (string.Equals(key, "RecFormat", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) RecFormat = value;
            }
            else if (string.Equals(key, "RecCountdownSec", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) RecCountdownSec = Recording.RecordCountdown.Normalize(v);
            }
            else if (string.Equals(key, "RecSystemAudio", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecSystemAudio = v;
            }
            else if (string.Equals(key, "RecMicAudio", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecMicAudio = v;
            }
            else if (string.Equals(key, "RecAudioMix", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) RecAudioMix = value;
            }
            else if (string.Equals(key, "RecMicVolume", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) RecMicVolume = Math.Clamp(v, 20, 100);
            }
            else if (string.Equals(key, "RecVisCursor", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecVisCursor = v;
            }
            else if (string.Equals(key, "RecVisRipple", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecVisRipple = v;
            }
            else if (string.Equals(key, "RecVisKeycap", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecVisKeycap = v;
            }
            else if (string.Equals(key, "RecShowBadge", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecShowBadge = v;
            }
            else if (string.Equals(key, "RecPreviewLoop", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecPreviewLoop = v;
            }
            // RecPreviewPrimarySave 已随 2026-09-22 预览窗第三轮整改删除（旧配置文件里残留的
            // 该键落入未知键分支被静默忽略，无兼容问题）
            else if (string.Equals(key, "RecBurnProgressBar", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecBurnProgressBar = v;
            }
            else if (string.Equals(key, "RecFfmpegPath", StringComparison.OrdinalIgnoreCase))
            {
                RecFfmpegPath = value;
            }
            else if (string.Equals(key, "RecEngine", StringComparison.OrdinalIgnoreCase))
            {
                RecEngine = Recording.RecordEngines.Normalize(value).ToString();
            }
            else if (string.Equals(key, "RecCaptureSource", StringComparison.OrdinalIgnoreCase))
            {
                RecCaptureSource = Recording.RecordCaptureSources.ToSetting(Recording.RecordCaptureSources.Normalize(value));
            }
            // 「上次使用」族（族说明见声明处）。容错口径与默认族一致：认不出的值一律保持"未记录"，
            // 让读取端自然回落到默认值，而不是替用户猜一个档。
            else if (string.Equals(key, "RecLastQuality", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) RecLastQuality = value;
            }
            else if (string.Equals(key, "RecLastFormat", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) RecLastFormat = value;
            }
            else if (string.Equals(key, "RecLastCountdownSec", StringComparison.OrdinalIgnoreCase))
            {
                int v; if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) RecLastCountdownSec = Recording.RecordCountdown.Normalize(v);
            }
            else if (string.Equals(key, "RecLastSystemAudio", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecLastSystemAudio = v;
            }
            else if (string.Equals(key, "RecLastMicAudio", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecLastMicAudio = v;
            }
            else if (string.Equals(key, "RecLastVisKeycap", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecLastVisKeycap = v;
            }
            else if (string.Equals(key, "RecLastVisRipple", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) RecLastVisRipple = v;
            }
            else if (string.Equals(key, "RecSaveDirectory", StringComparison.OrdinalIgnoreCase))
            {
                RecSaveDirectory = value;
            }
            else if (string.Equals(key, "RecMigrated", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) RecMigrated = v;
                recMigratedInFile = true;
            }
            else if (string.Equals(key, "ClearAllAnnotationsMigrated", StringComparison.OrdinalIgnoreCase))
            {
                bool v;
                if (bool.TryParse(value, out v)) ClearAllAnnotationsMigrated = v;
                clearAllAnnotationsMigratedInFile = true;
            }
            else if (string.Equals(key, "LongScrollStickerMigrated", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out bool migrated)) LongScrollStickerMigrated = migrated;
            }
            // 云同步 — 通用
            else if (string.Equals(key, "CloudSyncProvider", StringComparison.OrdinalIgnoreCase)) CloudSyncProvider = string.IsNullOrWhiteSpace(value) ? "none" : value;
            else if (string.Equals(key, "CloudAutoSync", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out bool enabled)) CloudAutoSync = enabled;
            }
            else if (string.Equals(key, "CloudSyncSecrets", StringComparison.OrdinalIgnoreCase))
            {
                bool v; if (bool.TryParse(value, out v)) CloudSyncSecrets = v;
            }
            // 云同步 — WebDAV
            else if (string.Equals(key, "WebDavUrl", StringComparison.OrdinalIgnoreCase)) WebDavUrl = value;
            else if (string.Equals(key, "WebDavUsername", StringComparison.OrdinalIgnoreCase)) WebDavUsername = value;
            else if (string.Equals(key, "WebDavPassword", StringComparison.OrdinalIgnoreCase)) WebDavPassword = value;
            else if (string.Equals(key, "WebDavFileName", StringComparison.OrdinalIgnoreCase)) WebDavFileName = string.IsNullOrWhiteSpace(value) ? "yuSnip-config-v1.json" : value;
            // 云同步 — Google Drive
            else if (string.Equals(key, "GoogleDriveClientId", StringComparison.OrdinalIgnoreCase)) GoogleDriveClientId = value;
            else if (string.Equals(key, "GoogleDriveClientSecret", StringComparison.OrdinalIgnoreCase)) GoogleDriveClientSecret = value;
            else if (string.Equals(key, "GoogleDriveRefreshToken", StringComparison.OrdinalIgnoreCase)) GoogleDriveRefreshToken = value;
            else if (string.Equals(key, "GoogleDriveAccessToken", StringComparison.OrdinalIgnoreCase)) GoogleDriveAccessToken = value;
            else if (string.Equals(key, "GoogleDriveTokenExpiry", StringComparison.OrdinalIgnoreCase))
            {
                long v; if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) GoogleDriveTokenExpiry = v;
            }
            else if (string.Equals(key, "GoogleDriveFileId", StringComparison.OrdinalIgnoreCase)) GoogleDriveFileId = value;
            else if (string.Equals(key, "GoogleDriveUserEmail", StringComparison.OrdinalIgnoreCase)) GoogleDriveUserEmail = value;
            else if (string.Equals(key, "GoogleDriveUserAvatar", StringComparison.OrdinalIgnoreCase)) GoogleDriveUserAvatar = value;
            else if (string.Equals(key, "GoogleDriveLastSync", StringComparison.OrdinalIgnoreCase))
            {
                long v; if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) GoogleDriveLastSync = v;
            }
        }

        public static string NormalizeAutoDetectMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "仅窗口";
            string v = value.Trim();
            if (string.Equals(v, "None", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "不检测", StringComparison.OrdinalIgnoreCase)) return "不检测";
            if (string.Equals(v, "Window", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "仅窗口", StringComparison.OrdinalIgnoreCase)) return "仅窗口";
            if (string.Equals(v, "Element", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "检测元素", StringComparison.OrdinalIgnoreCase) || v.StartsWith("检测元", StringComparison.OrdinalIgnoreCase))
            {
                if (v.StartsWith("检测元", StringComparison.OrdinalIgnoreCase) && !string.Equals(v, "检测元素", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"AppSettings: Recovered corrupted auto-detect mode '{v}' to '检测元素'");
                }
                return "检测元素";
            }
            if (string.Equals(v, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "自动", StringComparison.OrdinalIgnoreCase)) return "自动";
            
            AppLogger.Warn($"AppSettings: Unknown auto-detect mode '{v}', falling back to '仅窗口'");
            return "仅窗口";
        }

        /// <summary>辅助线三态：关闭。</summary>
        public const string GuidelineModeOff = "关闭";
        /// <summary>辅助线三态：开启（截图时按住 Alt 才显示）。</summary>
        public const string GuidelineModeHoldAlt = "开启";
        /// <summary>辅助线三态：始终显示（无需按键）。</summary>
        public const string GuidelineModeAlways = "始终显示";

        public static string NormalizeGuidelineMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return GuidelineModeHoldAlt;
            string v = value.Trim();
            if (string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "None", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("关", StringComparison.OrdinalIgnoreCase)) return GuidelineModeOff;
            if (string.Equals(v, "Always", StringComparison.OrdinalIgnoreCase) || v.StartsWith("始终", StringComparison.OrdinalIgnoreCase)) return GuidelineModeAlways;
            if (string.Equals(v, "On", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "HoldAlt", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("开", StringComparison.OrdinalIgnoreCase) || v.StartsWith("按", StringComparison.OrdinalIgnoreCase)) return GuidelineModeHoldAlt;

            AppLogger.Warn($"AppSettings: Unknown guideline mode '{v}', falling back to '开启'");
            return GuidelineModeHoldAlt;
        }

        /// <summary>智能抠图模型：通用主体显著抠图 (U²-NetP)。</summary>
        public const string MattingModelGeneral = "General";
        /// <summary>智能抠图模型：人像精细抠图 (MODNet)。</summary>
        public const string MattingModelPortrait = "Portrait";

        /// <summary>规范化智能抠图默认模型（General 通用 / Portrait 人像）。</summary>
        public static string NormalizeMattingDefaultModel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return MattingModelGeneral;
            string v = value.Trim();
            if (string.Equals(v, MattingModelPortrait, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "人像", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "人物肖像", StringComparison.OrdinalIgnoreCase))
            {
                return MattingModelPortrait;
            }
            if (string.Equals(v, MattingModelGeneral, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "通用", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "通用主体", StringComparison.OrdinalIgnoreCase))
            {
                return MattingModelGeneral;
            }
            AppLogger.Warn($"AppSettings: 未知的抠图模型「{v}」，已回落 General。");
            return MattingModelGeneral;
        }

        /// <summary>录屏落盘目录的<b>生效值</b>：设置里留空、或那个目录已经不在了 ⇒ 用本机默认 <c>视频\yuSnip 录屏</c>。</summary>
        /// <remarks>
        /// <b>只算，不建目录</b>——建不建由调用方定：快速保存必建、设置页「打开目录」也建，而水印那处只是把路径显示出来，
        /// 开一次设置页就顺手造一个空目录是脏的。
        /// 🔴 这条算式原先在 <c>VideoStickerWindow.DefaultSaveDir</c> 与设置页「打开目录」各写一份（2026-09-29 上收成这一份）。
        /// 与截图侧的 <see cref="SaveDirectory"/> 取向相反是<b>有意</b>的：那条把默认路径当场写进 ini，云同步换机器就带着
        /// 别人的用户名跑（现靠运行时"目录不可写就回落我的图片"兜底）；这条存空串、用时现算，天然跟着当前机器走。
        /// </remarks>
        public static string ResolveRecordSaveDir(string configured)
        {
            string dir = configured ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "yuSnip 录屏");
            }
            return dir;
        }

        /// <summary>获取当前配置对应的智能抠图模型枚举值。</summary>
        public Matting.MattingModelType GetMattingModelType()
        {
            return string.Equals(NormalizeMattingDefaultModel(MattingDefaultModel), MattingModelPortrait, StringComparison.OrdinalIgnoreCase)
                ? Matting.MattingModelType.Portrait
                : Matting.MattingModelType.General;
        }

        /// <summary>抠图结果去向：仅写入剪贴板。</summary>
        public const string MattingResultClipboard = "Clipboard";
        /// <summary>抠图结果去向：仅生成贴图。</summary>
        public const string MattingResultSticker = "Sticker";
        /// <summary>抠图结果去向：写入剪贴板并生成贴图（默认，即历史行为）。</summary>
        public const string MattingResultBoth = "ClipboardAndSticker";

        /// <summary>规范化抠图结果去向（Clipboard / Sticker / ClipboardAndSticker）。</summary>
        public static string NormalizeMattingResult(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return MattingResultBoth;
            string v = value.Trim();
            if (string.Equals(v, MattingResultClipboard, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "剪贴板", StringComparison.OrdinalIgnoreCase))
            {
                return MattingResultClipboard;
            }
            if (string.Equals(v, MattingResultSticker, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "贴图", StringComparison.OrdinalIgnoreCase))
            {
                return MattingResultSticker;
            }
            if (string.Equals(v, MattingResultBoth, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "剪贴板并贴图", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "两者", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "都", StringComparison.OrdinalIgnoreCase))
            {
                return MattingResultBoth;
            }
            AppLogger.Warn($"AppSettings: 未知的抠图结果去向「{v}」，已回落 ClipboardAndSticker。");
            return MattingResultBoth;
        }

        /// <summary>获取当前配置对应的抠图结果去向枚举值。</summary>
        public Matting.MattingResultDestination GetMattingResultDestination()
        {
            string v = NormalizeMattingResult(MattingResultDestination);
            if (v == MattingResultClipboard) return Matting.MattingResultDestination.Clipboard;
            if (v == MattingResultSticker) return Matting.MattingResultDestination.Sticker;
            return Matting.MattingResultDestination.ClipboardAndSticker;
        }

        public CaptureOptions ToCaptureOptions()
        {
            return new CaptureOptions
            {
                SaveDirectory = SaveDirectory,
                FileNameTemplate = FileNameTemplate,
                ImageFormat = ImageFormat,
                JpegQuality = JpegQuality,
                AutoDetectMode = NormalizeAutoDetectMode(AutoDetectMode),
                CopyToClipboardAfterSave = CopyToClipboardAfterSave,
                ShowSaveNotification = ShowSaveNotification,
                PlaySaveSound = PlaySaveSound,
                ThemeMode = ThemeMode,
                HotKeyModifiers = HotKeyModifiers,
                HotKeyVirtualKey = HotKeyVirtualKey
            };
        }

        public Dictionary<string, object> ToCaptureVars()
        {
            return ToCaptureOptions().ToLegacyVars();
        }

        /// <summary>将翻译设置转换为 <see cref="Translation.TranslationConfig"/>。</summary>
        public Translation.TranslationConfig ToTranslationConfig()
        {
            var provider = ParseTranslateProvider(TranslateProvider);

            // 仅使用分服务商独立字段，不再回退到旧版通用字段
            string appId, secretKey;
            if (provider == Translation.TranslationProvider.Baidu)
            {
                appId = BaiduAppId ?? "";
                secretKey = BaiduSecretKey ?? "";
            }
            else if (provider == Translation.TranslationProvider.Youdao)
            {
                appId = YoudaoAppId ?? "";
                secretKey = YoudaoSecretKey ?? "";
            }
            else
            {
                appId = "";
                secretKey = "";
            }

            var cfg = new Translation.TranslationConfig
            {
                Provider = provider,
                AppId = appId ?? "",
                SecretKey = secretKey ?? "",
                SourceLanguage = string.IsNullOrWhiteSpace(TranslateSourceLanguage) ? "auto" : TranslateSourceLanguage,
                TargetLanguage = string.IsNullOrWhiteSpace(TranslateTargetLanguage) ? "zh" : TranslateTargetLanguage,
                SecondaryTargetLanguage = string.IsNullOrWhiteSpace(TranslateSecondaryTargetLanguage) ? "en" : TranslateSecondaryTargetLanguage,
                Mode = string.Equals(NormalizeTranslateMode(TranslateMode), "ImageToImage", StringComparison.OrdinalIgnoreCase)
                    ? Translation.TranslationMode.ImageToImage
                    : Translation.TranslationMode.ImageToText,
            };

            return cfg;
        }

        /// <summary>规范化翻译服务商字符串。</summary>
        public static string NormalizeTranslateProvider(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "None";
            string v = value.Trim();
            if (string.Equals(v, "BilibiliIndex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "bilibili-index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "Index-Translate", StringComparison.OrdinalIgnoreCase)) return "BilibiliIndex";
            if (string.Equals(v, "Baidu", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "百度", StringComparison.OrdinalIgnoreCase)) return "Baidu";
            if (string.Equals(v, "Youdao", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "有道", StringComparison.OrdinalIgnoreCase)) return "Youdao";
            return "None";
        }

        /// <summary>将翻译服务商字符串解析为枚举。</summary>
        public static Translation.TranslationProvider ParseTranslateProvider(string value)
        {
            string v = NormalizeTranslateProvider(value);
            return v switch
            {
                "Baidu" => Translation.TranslationProvider.Baidu,
                "BilibiliIndex" => Translation.TranslationProvider.BilibiliIndex,
                "Youdao" => Translation.TranslationProvider.Youdao,
                _ => Translation.TranslationProvider.None,
            };
        }

        /// <summary>规范化翻译模式字符串。</summary>
        public static string NormalizeTranslateMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "ImageToText";
            string v = value.Trim();
            if (string.Equals(v, "ImageToImage", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "图片到图片", StringComparison.OrdinalIgnoreCase)) return "ImageToImage";
            if (string.Equals(v, "ImageToText", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "图片到文本", StringComparison.OrdinalIgnoreCase)) return "ImageToText";
            return "ImageToText";
        }

        /// <summary>规范化贴图双击动作字符串（Close 关闭 / Thumbnail 折叠为缩略图 / Copy 复制图像 / None 无操作）。</summary>
        public static string NormalizeStickerDoubleClickAction(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Close";
            string v = value.Trim();
            if (string.Equals(v, "Close", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "关闭", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "关闭贴图", StringComparison.OrdinalIgnoreCase)) return "Close";
            if (string.Equals(v, "Thumbnail", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "缩略图", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "折叠为缩略图", StringComparison.OrdinalIgnoreCase)) return "Thumbnail";
            if (string.Equals(v, "Copy", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "复制", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "复制图像", StringComparison.OrdinalIgnoreCase)) return "Copy";
            if (string.Equals(v, "None", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "无", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "无操作", StringComparison.OrdinalIgnoreCase)) return "None";
            return "Close";
        }

        /// <summary>规范化贴图阴影档位（<c>Off</c> / <c>On</c> / <c>Auto</c>，缺省 <c>Auto</c>）。
        /// 兼容中文写法与旧布尔字符串（<c>True</c>/<c>False</c>——老 ini 升级上来可能是这一档）。</summary>
        public static string NormalizeStickerShadowMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Auto";
            string v = value.Trim();
            if (string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase) || v == "关闭" || v == "关"
                || string.Equals(v, "False", StringComparison.OrdinalIgnoreCase)) return "Off";
            if (string.Equals(v, "On", StringComparison.OrdinalIgnoreCase) || v == "开启" || v == "开"
                || string.Equals(v, "True", StringComparison.OrdinalIgnoreCase)) return "On";
            if (string.Equals(v, "Auto", StringComparison.OrdinalIgnoreCase) || v == "自动") return "Auto";
            return "Auto";
        }

        /// <summary>规范化「恢复上次关闭的贴图」排序模式（ClosedAt 按关闭时间 / Created 按创建时间）。</summary>
        public static string NormalizeStickerRestoreSortMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "ClosedAt";
            string v = value.Trim();
            if (string.Equals(v, "Created", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "创建时间", StringComparison.OrdinalIgnoreCase)) return "Created";
            return "ClosedAt";
        }

        /// <summary>规范化 OCR 输出方式字符串（Window 打开编辑窗口 / Clipboard 静默复制）。</summary>
        public static string NormalizeOcrOutputMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Window";
            string v = value.Trim();
            if (string.Equals(v, "Clipboard", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "静默复制", StringComparison.OrdinalIgnoreCase)) return "Clipboard";
            return "Window";
        }

        /// <summary>规范化 OCR 提示窗位置字符串（None 无 / BottomCenter 中下 / TopCenter 中上 / TopRight 右上 / BottomRight 右下 / Center 居中）。</summary>
        public static string NormalizeOcrToastPosition(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "BottomCenter";
            string v = value.Trim();
            if (string.Equals(v, "None", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "无", StringComparison.OrdinalIgnoreCase)) return "None";
            if (string.Equals(v, "BottomCenter", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "中下", StringComparison.OrdinalIgnoreCase)) return "BottomCenter";
            if (string.Equals(v, "TopCenter", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "中上", StringComparison.OrdinalIgnoreCase)) return "TopCenter";
            if (string.Equals(v, "TopRight", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "右上", StringComparison.OrdinalIgnoreCase)) return "TopRight";
            if (string.Equals(v, "BottomRight", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "右下", StringComparison.OrdinalIgnoreCase)) return "BottomRight";
            if (string.Equals(v, "Center", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "居中", StringComparison.OrdinalIgnoreCase)) return "Center";
            return "BottomCenter";
        }

        /// <summary>
        /// 规范化 OCR 提示窗等待秒数：吸附到 3 / 5 / 10 三档，上限 10 秒。
        /// 非正值与过小值一律抬到最低的 3 秒；是否显示由 OcrToastPosition 决定。
        /// </summary>
        public static int NormalizeOcrToastSeconds(int value)
        {
            if (value <= 4) return 3;
            if (value <= 7) return 5;
            return 10;
        }

        /// <summary>
        /// 规范化截图选框边框线型（Dashed 虚线 / Solid 实线）。
        /// </summary>
        public static string NormalizeSelectionBorderStyle(string value)
        {
            if (string.Equals(value, SelectionBorderStyleSolid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "实线", StringComparison.OrdinalIgnoreCase))
                return SelectionBorderStyleSolid;
            return SelectionBorderStyleDashed;
        }

        /// <summary>
        /// 规范化截图选框边框粗细（0~5 像素，步进 1，默认 2px，0px 表示不绘制选框）。
        /// </summary>
        public static int NormalizeSelectionBorderThickness(int value)
        {
            return Math.Clamp(value, MinSelectionBorderThickness, MaxSelectionBorderThickness);
        }

        /// <summary>
        /// 规范化截图历史收集状态（Off 关闭 / On 开启）。
        /// 兼容旧版 All / StickerOnly 等多档位值并平滑迁移为 On，未知值一律回落 Off。
        /// </summary>
        public static string NormalizeCaptureHistoryMode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Off";
            string v = value.Trim();
            if (string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "关闭", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "不收集", StringComparison.OrdinalIgnoreCase))
                return "Off";
            if (string.Equals(v, "On", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "开启", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "All", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "全部", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "截图", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "全部截图", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "StickerOnly", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "Sticker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "贴图", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "仅贴图", StringComparison.OrdinalIgnoreCase))
                return "On";
            AppLogger.Warn("AppSettings: 未知的截图历史模式「" + v + "」，已回落 Off。");
            return "Off";
        }

        /// <summary>规范化截图历史保留条数上限：夹取到 20~2000，非正值回默认 200。</summary>
        public static int NormalizeCaptureHistoryMaxCount(int value)
        {
            if (value <= 0) return 200;
            if (value < 20) return 20;
            if (value > 2000) return 2000;
            return value;
        }

        /// <summary>规范化截图历史保留总容量上限（MB）：夹取到 100~8000，非正值回默认 800。</summary>
        public static int NormalizeCaptureHistoryMaxMegabytes(int value)
        {
            if (value <= 0) return 800;
            if (value < 100) return 100;
            if (value > 8000) return 8000;
            return value;
        }
    }
}
