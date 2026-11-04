using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Ai
{
    /// <summary>
    /// 智能排版失败原因（Toast 提示分档）。
    /// </summary>
    public enum AiReflowFailReason
    {
        /// <summary>未失败 / 未执行。</summary>
        None,
        /// <summary>网络不可用（连接失败 / 超时 / 异常）。</summary>
        Network,
        /// <summary>AI 结果未通过校验（行号覆盖 / 有序性 / 枚举合法性）。</summary>
        Validation,
        /// <summary>AI 返回格式异常（无法提取 JSON）。</summary>
        BadFormat
    }

    /// <summary>
    /// AI 输入行：文本 + 可选几何（x/y/h）+ OCR 置信度。
    /// AI 排版语义优先（无几何行也能排），几何仅作行高/缩进/间距辅助证据。
    /// </summary>
    public sealed class AiReflowInputLine
    {
        public string Text { get; set; } = string.Empty;
        public bool HasGeometry { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double H { get; set; }
        public float Score { get; set; }
    }

    /// <summary>
    /// AI 决策块类型（LLM JSON 输出的 type 字段）。
    /// </summary>
    public enum AiReflowBlockType
    {
        Title,
        Para,
        List,
        Line,
        /// <summary>代码块（行内文本保留原样分行，Markdown 转代码围栏）。</summary>
        Code,
        /// <summary>引用块（Markdown 转 &gt; 前缀）。</summary>
        Quote
    }

    /// <summary>
    /// AI 决策块：类型 + 行号引用 + 可选结构属性（不携带任何正文——从结构上杜绝 LLM 改写/幻觉）。
    /// 结构属性全部是"看文本与几何即可判断的结构事实"，AI 不产出正文，只判结构。
    /// </summary>
    public sealed class AiReflowBlock
    {
        public AiReflowBlockType Type { get; set; }
        /// <summary>块内行号（0 基，指向输入行列表）。</summary>
        public List<int> Lines { get; set; } = new List<int>();

        // ══ 可选结构属性（软解析：缺失/非法一律取默认值，不致校验失败） ══

        /// <summary>标题层级 1/2/3（仅 title 块；0 = 未分级，由本地几何回退判定）。</summary>
        public int Level { get; set; }
        /// <summary>有序列表（仅 list 块；true = 1. 编号式，false = 圆点式）。</summary>
        public bool Ordered { get; set; }
        /// <summary>列表嵌套深度 0/1/2（仅 list 块；按 x 缩进差与语义判读，超界钳制到 2）。</summary>
        public int Depth { get; set; }
        /// <summary>代码语言标识（仅 code 块；如 csharp/python/json，未知为 null → 无高亮）。</summary>
        public string? Lang { get; set; }
    }

    /// <summary>
    /// 全文符号风格决策：LLM 只选风格枚举，文本变换由本地 OcrTextOps 确定性算子执行。
    /// </summary>
    public sealed class AiReflowStyleDecision
    {
        /// <summary>引号风格：straight / curly / corner / null（不确定）。</summary>
        public string? Quote { get; set; }
        /// <summary>括号风格：english / chinese / null（不确定）。</summary>
        public string? Paren { get; set; }
        /// <summary>标点风格：chinese / english / null（不确定）。</summary>
        public string? Punct { get; set; }
    }

    /// <summary>
    /// 润色修正项：原文片段 → 修正词 + 理由（仅登记 OCR 形近/近音错字）。
    /// 软校验（orig 逐字定位）由调用方执行，定位不到整项丢弃。
    /// </summary>
    public sealed class AiReflowEdit
    {
        public int BlockIndex { get; set; }
        public string Orig { get; set; } = string.Empty;
        public string Fix { get; set; } = string.Empty;
        public string Why { get; set; } = string.Empty;
    }

    /// <summary>
    /// AI 排版决策：块结构 + 符号风格 + 润色修正清单。
    /// </summary>
    public sealed class AiReflowDecision
    {
        public List<AiReflowBlock> Blocks { get; set; } = new List<AiReflowBlock>();
        public AiReflowStyleDecision? Styles { get; set; }
        public List<AiReflowEdit> Edits { get; set; } = new List<AiReflowEdit>();
    }

    /// <summary>
    /// 分析结果：Decision == null 表示失败（Reason 分档 + Message 具体原因透传到错误条与 Toast）。
    /// </summary>
    public sealed class AiReflowOutcome
    {
        public AiReflowDecision? Decision { get; }
        public AiReflowFailReason Reason { get; }
        public string Message { get; }

        public AiReflowOutcome(AiReflowDecision? decision, AiReflowFailReason reason, string message)
        {
            Decision = decision;
            Reason = reason;
            Message = message ?? string.Empty;
        }

        public static AiReflowOutcome Success(AiReflowDecision decision) =>
            new(decision, AiReflowFailReason.None, string.Empty);

        public static AiReflowOutcome Fail(AiReflowFailReason reason, string message) =>
            new(null, reason, message);
    }

    /// <summary>
    /// AI 智能排版服务（LLM 决策 + 校验兜底，NativeAOT 零反射）：
    /// 复用 AI 识图配置走纯文本流式通道（AiVisionService.AnalyzeTextStreamAsync）；
    /// LLM 只输出结构 JSON（块类型 + 行号引用 + 符号风格枚举 [+ 润色 edits]），不输出任何正文——
    /// 块文本由本地代码按行项当前文本拼接，从结构上杜绝改写/漏字/编造。
    /// 三道校验：JSON 提取容错（剥围栏/截取大括号）→ 类型与风格枚举合法 →
    /// 行号 0..N-1 全覆盖恰好一次 + 块内连续块间有序（禁止 LLM 重排内容）；
    /// 任何一关不过返回 Decision=null 的失败 Outcome，由调用方保持原样（绝不排错）。
    /// 网络异常直接向上抛出，由调用方按 Network 分档处理并透传异常消息。
    /// </summary>
    public sealed class AiReflowService
    {
        private static readonly Lazy<AiReflowService> _instance = new(() => new AiReflowService());
        public static AiReflowService Instance => _instance.Value;

        private AiReflowService() { }

        /// <summary>是否已配置 AI 服务（复用 AI 识图配置：Base URL + 有效 API Key）。</summary>
        public static bool IsConfigured()
        {
            var s = AppSettings.Current;
            return !string.IsNullOrWhiteSpace(s.AiBaseUrl)
                && !string.IsNullOrWhiteSpace(s.GetEffectiveApiKey());
        }

        /// <summary>
        /// 执行智能排版分析：构造 prompt（语义优先 + 几何/置信度辅助）→ 流式调用 LLM → 严格校验。
        /// polishLevel: 0=关闭, 1=Ⅰ 仅错字, 2=Ⅱ 含补全, 3=Ⅲ 深度语法修正。
        /// </summary>
        public async Task<AiReflowOutcome> AnalyzeAsync(IReadOnlyList<AiReflowInputLine> lines, int polishLevel)
        {
            if (lines == null || lines.Count == 0)
                return AiReflowOutcome.Fail(AiReflowFailReason.Validation, "无输入行");

            var s = AppSettings.Current;
            var request = new AiVisionRequest
            {
                Protocol = ParseProtocol(s.AiProtocol),
                BaseUrl = s.AiBaseUrl,
                ApiKey = s.GetEffectiveApiKey(),
                Model = s.AiModel,
                Prompt = BuildPrompt(lines, polishLevel),
                TimeoutSeconds = s.AiTimeoutSeconds,
            };

            int timeout = s.AiTimeoutSeconds > 0 ? s.AiTimeoutSeconds : 60;
            var sb = new StringBuilder(4096);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 网络异常/超时向上抛，由调用方按 Network 分档收尾并透传异常消息
            await AiVisionService.Instance.AnalyzeTextStreamAsync(
                request, chunk => sb.Append(chunk), cts.Token);

            sw.Stop();
            string raw = sb.ToString();
            AppLogger.Info($"[AiReflow] AnalyzeAsync finished in {sw.ElapsedMilliseconds}ms, response {raw.Length} chars");

            if (string.IsNullOrWhiteSpace(raw))
                return AiReflowOutcome.Fail(AiReflowFailReason.BadFormat, "AI 返回空响应");

            return ParseAndValidate(raw, lines.Count);
        }

        /// <summary>解析协议字符串（与 OcrWindow.CheckReflowConn 同一判定）。</summary>
        private static AiProtocolType ParseProtocol(string? protocol) =>
            string.Equals(protocol, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocol, "Claude", StringComparison.OrdinalIgnoreCase)
                ? AiProtocolType.Anthropic
                : AiProtocolType.OpenAI;

        // ═══════════ Prompt 构造 ═══════════

        /// <summary>
        /// 构造 LLM prompt：行清单（语义为主，几何/置信度辅助）+ 结构 JSON 输出契约。
        /// </summary>
        private static string BuildPrompt(IReadOnlyList<AiReflowInputLine> lines, int polishLevel)
        {
            var sb = new StringBuilder(2048 + lines.Count * 64);
            sb.Append("你是 OCR 文档结构分析引擎。把下面的 OCR 物理行重组为逻辑结构，只输出一个 JSON 对象。\n\n");
            sb.Append("【输入】每行格式：行号. 文本 | 可选几何证据(x,y,行高) 与置信度\n");
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(". ")
                  .Append(string.IsNullOrEmpty(l.Text) ? " " : l.Text);
                if (l.HasGeometry)
                {
                    sb.Append(" | x=").Append(l.X.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",y=").Append(l.Y.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",h=").Append(l.H.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(" conf=").Append(l.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }

            sb.Append("""
【输出 JSON 格式】
{"blocks":[{"type":"title","level":1,"lines":[0]},{"type":"para","lines":[1,2]},{"type":"list","ordered":false,"depth":0,"lines":[3,4]},{"type":"code","lang":"csharp","lines":[5,6]},{"type":"quote","lines":[7]},{"type":"line","lines":[8]}],"styles":{"quote":"straight","paren":"english","punct":"chinese"}}
""");
            if (polishLevel > 0)
            {
                sb.Append("""
【润色模式】额外输出 edits 数组（与 blocks 平级）：
{"edits":[{"block":0,"orig":"原文片段","fix":"修正词","why":"理由"}]}
""");
                // 三级权限：Ⅰ 仅错字（最保守）→ Ⅱ 含残缺补全 → Ⅲ 深度语法修正（最激进）
                switch (polishLevel)
                {
                    case 1:
                        sb.Append("- 仅登记 OCR 形近/近音错字（如“设记”→“设计”），不新增内容、不删改语序、不做风格改写；行首形近误识的列表标记字符删除（OCR 常把圆点 · 形近误识为 ） 或 。，如“）进入排版”→“进入排版”）；不确定就不登记\n");
                        break;
                    case 2:
                        sb.Append("- 登记四类修正：①形近/近音错字（如“设记”→“设计”；行首误识的列表标记字符删除——OCR 常把圆点 · 形近误识为 ） 或 。，如“）进入排版”→“进入排版”）；②唯一性强的残缺补全——原文明显缺字且补全方式唯一（如“上海再”→“上海再见”），无法唯一补全的残缺（如“的老友”缺首字）不登记；③冗余重复字删除；④破折号还原——OCR 常把破折号「——」误识为「一一」，语义连接处（解释引出/话题转折，如“真渲染器一一标题按层级”→“真渲染器——标题按层级”）登记还原。不删改语序、不做风格改写；不确定就不登记\n");
                        break;
                    case 3:
                        sb.Append("- 最激进模式：结合上下文积极推断，登记一切有语义证据的文字问题——①形近/近音/手写体误识错字（如“就包的”→“彩色的”、“生善活”→“生活”；行首误识的列表标记字符删除——OCR 常把圆点 · 形近误识为 ） 或 。，如“）进入排版”→“进入排版”）；②残缺补全（上下文可推断即补，不强求补全方式唯一）；③冗余重复删除；④语法搭配错误（的/地/得、量词、介词）；⑤明显语病（如“无满了”→“充满了”）。判断原则：结合前后文只要能给出明显更通顺的解释就登记，宁可多修不可漏修（修错的每处都会展示对照供人复核）；仅禁止整句风格改写与无中生有的内容，每处修正保持最小跨度\n");
                        break;
                }
                sb.Append("- orig 必须逐字来自对应块按行号顺序拼接的原文（硬性要求：保证修正可定位执行，凭空编造的 orig 会被丢弃）\n");
            }

            sb.Append("""

【规则】
1. type ∈ title（标题）/ para（段落）/ list（列表项）/ code（代码块）/ quote（引用块）/ line（独立行：对话、署名、页眉页脚、无法判断的行）
2. lines 为该块包含的行号数组；所有行号 0..N-1 必须恰好各出现一次
3. 块内行号必须连续递增，块之间必须按行号顺序排列（禁止重排内容）
4. 判定依据：标题=行高明显更大或独立成行的章节名/编号；列表=行首带 •/·/-/1./（一）等符号的连续行（· 中点是 OCR 最常见的圆点形态，标记后空格常丢失如「·项目」也算列表标记），**连同列表项因 OCR 断行掉落的无标记续行一起归入同一 list 块——整个列表区域（所有项+续行）只输出一个块，严禁按项拆成多个块、严禁把续行单独判成 para/line、严禁把带标记的连续行判成段落**；段落=连续正文行；代码=编程语言文本（花括号/分号/关键字/缩进结构，多行保持原分行）；引用=行首带 > 或引语式摘录
5. title 可选 level ∈ 1/2/3（1=文档主标题，2=章节，3=小节；按行高与编号语义分级，如"一、"大章 vs "1.1"小节）；未分级省略 level
6. list 可选 ordered（true=1. 编号式 / false=圆点式，按行首符号判断）与 depth ∈ 0/1/2（嵌套深度，按 x 缩进差与语义判断；嵌套子列表项可按缩进给更深的 depth）
7. code 可选 lang（语言标识如 csharp/python/json；不确定省略）
8. styles 判断全文符号风格：quote ∈ straight/curly/corner（引号）、paren ∈ english/chinese（括号）、punct ∈ chinese/english（标点）；不确定的字段填 null
9. 几何与置信度仅作辅助证据（行高、缩进、间距），语义优先；无几何的行同样参与判定
只输出 JSON，禁止输出任何其他文字。
""");
            return sb.ToString();
        }

        // ═══════════ 三道校验 ═══════════

        /// <summary>
        /// 解析并校验 LLM 输出：
        /// ① JSON 提取容错（剥 ``` 围栏 / 截取首个 { 到最后一个 }）；
        /// ② 类型与风格枚举合法；
        /// ③ 行号 0..N-1 全覆盖恰好一次 + 块内连续 + 块间有序（按块顺序展开必须严格等于 0,1,2,…）。
        /// </summary>
        internal static AiReflowOutcome ParseAndValidate(string raw, int lineCount)
        {
            // ① JSON 提取容错
            string? json = ExtractJson(raw);
            if (json is null)
                return AiReflowOutcome.Fail(AiReflowFailReason.BadFormat, "AI 返回中未找到 JSON 对象");

            AiReflowDecision decision;
            try
            {
                decision = ParseDecision(json);
            }
            catch (Exception ex)
            {
                return AiReflowOutcome.Fail(AiReflowFailReason.BadFormat, "AI 返回 JSON 解析失败：" + ex.Message);
            }

            if (decision.Blocks.Count == 0)
                return AiReflowOutcome.Fail(AiReflowFailReason.Validation, "AI 决策不含任何排版块");

            // ③ 行号全覆盖恰好一次 + 块内连续 + 块间有序：按块顺序展开必须严格等于 0,1,2,…,N-1
            int expected = 0;
            foreach (var b in decision.Blocks)
            {
                if (b.Lines.Count == 0)
                    return AiReflowOutcome.Fail(AiReflowFailReason.Validation, "存在行号为空的排版块");
                foreach (var ln in b.Lines)
                {
                    if (ln != expected)
                    {
                        return AiReflowOutcome.Fail(AiReflowFailReason.Validation,
                            $"行号校验失败：期望第 {expected} 行，实际引用第 {ln} 行（要求 0..{lineCount - 1} 全覆盖恰好一次且不重排）");
                    }
                    expected++;
                }
            }
            if (expected != lineCount)
                return AiReflowOutcome.Fail(AiReflowFailReason.Validation,
                    $"行号覆盖不完整：应覆盖 {lineCount} 行，实际仅 {expected} 行");

            return AiReflowOutcome.Success(decision);
        }

        /// <summary>剥 ``` 围栏并截取首个 { 到最后一个 } 的 JSON 主体；无则返回 null。</summary>
        private static string? ExtractJson(string raw)
        {
            string t = raw.Trim();
            // 剥离可能存在的 <think>...</think> 思考链，避免干扰 JSON 提取
            if (t.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            {
                t = System.Text.RegularExpressions.Regex.Replace(t, @"<think>[\s\S]*?</think>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                t = System.Text.RegularExpressions.Regex.Replace(t, @"<think>[\s\S]*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }

            // 剥 Markdown 代码围栏
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = t.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    t = t[(firstNewline + 1)..];
                    int fence = t.LastIndexOf("```", StringComparison.Ordinal);
                    if (fence >= 0) t = t[..fence];
                    t = t.Trim();
                }
            }
            int start = t.IndexOf('{');
            int end = t.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return t[start..(end + 1)];
        }

        /// <summary>JsonDocument 手动解析（零反射）；② 枚举合法性在此把关，非法即抛（由调用方归为 BadFormat/Validation）。</summary>
        private static AiReflowDecision ParseDecision(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var decision = new AiReflowDecision();
            if (!root.TryGetProperty("blocks", out var blocksEl) || blocksEl.ValueKind != JsonValueKind.Array)
                throw new FormatException("缺少 blocks 数组");

            foreach (var b in blocksEl.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) throw new FormatException("blocks 元素必须是对象");
                if (!b.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                    throw new FormatException("块缺少 type 字段");
                if (!b.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
                    throw new FormatException("块缺少 lines 数组");

                var block = new AiReflowBlock
                {
                    Type = typeEl.GetString() switch
                    {
                        "title" => AiReflowBlockType.Title,
                        "para" => AiReflowBlockType.Para,
                        "list" => AiReflowBlockType.List,
                        "line" => AiReflowBlockType.Line,
                        "code" => AiReflowBlockType.Code,
                        "quote" => AiReflowBlockType.Quote,
                        _ => throw new FormatException($"非法块类型：{typeEl.GetString()}"),
                    }
                };

                // 可选结构属性：软解析（缺失/类型不符/越界一律取安全默认值，不致整体失败）
                if (b.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number
                    && levelEl.TryGetInt32(out int lv) && lv >= 1 && lv <= 3)
                    block.Level = lv;
                if (b.TryGetProperty("ordered", out var ordEl) && ordEl.ValueKind == JsonValueKind.True)
                    block.Ordered = true;
                if (b.TryGetProperty("depth", out var depthEl) && depthEl.ValueKind == JsonValueKind.Number
                    && depthEl.TryGetInt32(out int dp))
                    block.Depth = Math.Clamp(dp, 0, 2);
                if (b.TryGetProperty("lang", out var langEl) && langEl.ValueKind == JsonValueKind.String)
                {
                    string lang = langEl.GetString() ?? string.Empty;
                    if (lang.Length > 0 && lang.Length <= 24) block.Lang = lang;
                }

                foreach (var ln in linesEl.EnumerateArray())
                {
                    if (ln.ValueKind != JsonValueKind.Number || !ln.TryGetInt32(out int v))
                        throw new FormatException("lines 数组元素必须是整数");
                    block.Lines.Add(v);
                }
                decision.Blocks.Add(block);
            }

            // styles（可选）：枚举合法校验，非法值按未指定（null）处理
            if (root.TryGetProperty("styles", out var stylesEl) && stylesEl.ValueKind == JsonValueKind.Object)
            {
                var styles = new AiReflowStyleDecision
                {
                    Quote = ReadStyleEnum(stylesEl, "quote", "straight", "curly", "corner"),
                    Paren = ReadStyleEnum(stylesEl, "paren", "english", "chinese"),
                    Punct = ReadStyleEnum(stylesEl, "punct", "chinese", "english"),
                };
                if (styles.Quote != null || styles.Paren != null || styles.Punct != null)
                    decision.Styles = styles;
            }

            // edits（可选，润色模式）：block 索引 + orig/fix/why；范围与 orig 定位软校验由调用方执行
            if (root.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in editsEl.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("block", out var blockEl) || !blockEl.TryGetInt32(out int bi)) continue;
                    string orig = e.TryGetProperty("orig", out var origEl) ? origEl.GetString() ?? "" : "";
                    string fix = e.TryGetProperty("fix", out var fixEl) ? fixEl.GetString() ?? "" : "";
                    if (orig.Length == 0 || fix.Length == 0) continue;
                    decision.Edits.Add(new AiReflowEdit
                    {
                        BlockIndex = bi,
                        Orig = orig,
                        Fix = fix,
                        Why = e.TryGetProperty("why", out var whyEl) ? whyEl.GetString() ?? "" : "",
                    });
                }
            }

            return decision;
        }

        /// <summary>读取风格枚举字段：值合法返回原值，null/"null"/缺失/非法统一返回 null（不确定）。</summary>
        private static string? ReadStyleEnum(JsonElement obj, string prop, params string[] allowed)
        {
            if (!obj.TryGetProperty(prop, out var el)) return null;
            if (el.ValueKind != JsonValueKind.String) return null;
            string? v = el.GetString();
            if (string.IsNullOrEmpty(v) || v == "null") return null;
            foreach (var a in allowed)
                if (string.Equals(v, a, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }
    }
}
