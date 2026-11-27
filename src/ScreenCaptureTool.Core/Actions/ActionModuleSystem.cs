using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ScreenCaptureTool.Core.Scripting;

namespace ScreenCaptureTool.Core.Actions;

public enum ActionModuleCategory
{
    Input,
    Process,
    Output,
    Control
}

public enum ActionParamKind
{
    Choice,
    Int,
    NullableInt,
    Region,
    Text,
    Directory
}

public sealed record ActionParamChoice(string Value, string Label);

public sealed record ActionModuleParamSpec(
    string Key,
    string Label,
    ActionParamKind Kind,
    string DefaultValue,
    ActionParamChoice[]? Choices = null,
    int Min = 0,
    int Max = 60000,
    string? Unit = null,
    int[]? QuickPresets = null,
    string? Tooltip = null,
    bool IsAdvanced = false,
    string? Watermark = null,
    string? VisibleWhenParamKey = null,
    string? VisibleWhenParamValue = null,
    int Step = 1);

public sealed record ActionStepNode(
    string ModuleId,
    string? InputName = null,
    string? ResultName = null,
    IReadOnlyDictionary<string, string>? Params = null)
{
    public string GetParam(string key, string fallback = "") =>
        Params != null && Params.TryGetValue(key, out var value) ? value : fallback;

    public int GetIntParam(string key, int fallback) =>
        int.TryParse(GetParam(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public ActionStepNode WithParam(string key, string value)
    {
        var copy = Params != null
            ? new Dictionary<string, string>(Params, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        copy[key] = value;
        return this with { Params = copy };
    }
}

public sealed record ActionDataRef(string Name, ActionContentKind Kind, int StepIndex)
{
    public string KindLabel => Kind == ActionContentKind.Image ? "图片" : "文字";
    public string DisplayLabel => $"{StepIndex + 1}. {Name}（{KindLabel}）";
}

public enum DiagnosticFixKind
{
    None,
    InsertCaptureRegion,
    InsertClipboardText,
    SelectAvailableInput,
    FixDuplicateResultName
}

public sealed record WorkflowStepDiagnostic(
    int StepIndex,
    string Message,
    DiagnosticFixKind FixKind = DiagnosticFixKind.None,
    string? FixLabel = null,
    string? SuggestedValue = null);

public sealed record ActionTemplateItem(string Key, string Name, string Description, string Script);

public sealed class ActionModuleDescriptor
{
    public required string Id { get; init; }
    public required ActionModuleCategory Category { get; init; }
    public required string Name { get; init; }
    public required string ShortDescription { get; init; }
    public required string Keywords { get; init; }
    public required string IconKey { get; init; }
    public string? DefaultResultLabel { get; init; }
    public bool NeedsInput { get; init; }
    public bool ProducesResult { get; init; }
    public ActionModuleParamSpec[] Parameters { get; init; } = [];
    public required Func<ActionStepNode, (bool Image, bool Text)> AcceptedInputs { get; init; }
    public required Func<ActionStepNode, ActionContentKind> OutputKind { get; init; }
    public required Func<ActionStepNode, string> ToScript { get; init; }
    public required Func<ActionStepNode, string> Describe { get; init; }

    public bool HasAdvancedParameters => Parameters.Any(p => p.IsAdvanced);

    public string InputBadge
    {
        get
        {
            if (!NeedsInput) return "";
            var (img, txt) = AcceptedInputs(new ActionStepNode(Id));
            return (img, txt) switch
            {
                (true, true) => "图片/文字",
                (true, false) => "图片",
                (false, true) => "文字",
                _ => ""
            };
        }
    }

    public string OutputBadge
    {
        get
        {
            if (!ProducesResult) return "";
            return OutputKind(new ActionStepNode(Id)) == ActionContentKind.Image ? "图片" : "文字";
        }
    }

    /// <summary>收集当前步骤中所有被修改为非默认值的“更多选项”紧凑徽章，收起抽屉时依然一目了然。</summary>
    public List<string> GetAdvancedBadges(ActionStepNode step)
    {
        var badges = new List<string>();
        foreach (var p in Parameters)
        {
            if (!p.IsAdvanced) continue;
            if (!IsParamVisible(p, step)) continue;
            string val = step.GetParam(p.Key, p.DefaultValue);
            if (string.Equals(val, p.DefaultValue, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(val)) continue;

            if (p.Kind == ActionParamKind.Choice && p.Choices != null)
            {
                var found = p.Choices.FirstOrDefault(c => string.Equals(c.Value, val, StringComparison.OrdinalIgnoreCase));
                string label = found?.Label ?? val;
                badges.Add(p.Key switch
                {
                    "toolbar" => label,
                    "shadow" => $"阴影:{label}",
                    "detect" => $"检测:{label}",
                    "to" => $"译至:{label}",
                    "opacity" => $"透明:{label}",
                    "lock" => "初始锁定",
                    "pass" => "鼠标穿透",
                    "mode" when Id == "output.save" => label,
                    "format" => label,
                    _ => label
                });
            }
            else if (p.Kind == ActionParamKind.NullableInt)
            {
                if (p.Key == "radius") badges.Add(val == "0" ? "无圆角" : $"圆角 {val} 档");
                else if (p.Key == "stroke") badges.Add(val == "0" ? "无描边" : $"描边 {val}px");
                else if (p.Key == "opacity") badges.Add($"透明 {val}%");
                else badges.Add($"{p.Label}:{val}{p.Unit}");
            }
            else if (p.Kind == ActionParamKind.Directory)
            {
                string folder = System.IO.Path.GetFileName(val.TrimEnd('\\', '/'));
                badges.Add("目录:" + (string.IsNullOrEmpty(folder) ? val : folder));
            }
            else if (p.Kind == ActionParamKind.Text)
            {
                badges.Add("自定义提示词");
            }
            else
            {
                badges.Add($"{p.Label}:{val}");
            }
        }
        return badges;
    }

    public static bool IsParamVisible(ActionModuleParamSpec param, ActionStepNode step)
    {
        if (string.IsNullOrEmpty(param.VisibleWhenParamKey)) return true;
        string actual = step.GetParam(param.VisibleWhenParamKey, "");
        return string.Equals(actual, param.VisibleWhenParamValue, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 统一动作模块注册表：界面模块库、两级渐进参数卡片、逐步骤校验与脚本往返解析全部由这一份 Schema 驱动。
/// </summary>
public static class ActionModuleRegistry
{
    public const string DefaultScript = "yuSnip.captureImage('原图');\nyuSnip.copyResult('原图');";

    public static string CategoryTitle(ActionModuleCategory category) => category switch
    {
        ActionModuleCategory.Input => "获取内容",
        ActionModuleCategory.Process => "智能处理",
        ActionModuleCategory.Output => "结果输出",
        _ => "互动与控制"
    };

    private static readonly ActionParamChoice[] ShadowChoices =
    [
        new("default", "跟随设置"),
        new("on", "开启"),
        new("off", "关闭")
    ];

    private static readonly ActionParamChoice[] DetectChoices =
    [
        new("default", "跟随设置"),
        new("none", "不检测"),
        new("window", "仅窗口"),
        new("element", "检测元素"),
        new("auto", "自动")
    ];

    private static readonly ActionModuleParamSpec ParamRadius = new(
        "radius", "选区圆角", ActionParamKind.NullableInt, "default",
        Min: 0, Max: 50, Unit: "档", Step: 2, IsAdvanced: true,
        Watermark: "跟随设置 (0–50)",
        Tooltip: "留空跟随全局设置；填 0 为无圆角，可自由输入 0–50 任意档位。");

    private static readonly ActionModuleParamSpec ParamStroke = new(
        "stroke", "截图描边", ActionParamKind.NullableInt, "default",
        Min: 0, Max: 20, Unit: "px", Step: 1, IsAdvanced: true,
        Watermark: "跟随设置 (0–20)",
        Tooltip: "留空跟随全局设置；填 0 为无描边，可自由输入 0–20 像素。");

    private static readonly ActionModuleParamSpec ParamShadow = new(
        "shadow", "选区阴影", ActionParamKind.Choice, "default", Choices: ShadowChoices, IsAdvanced: true,
        Tooltip: "单次控制是否为成品烘焙四周悬浮阴影。");

    private static readonly ActionModuleParamSpec ParamDetect = new(
        "detect", "智能检测", ActionParamKind.Choice, "default", Choices: DetectChoices, IsAdvanced: true,
        Tooltip: "单次覆盖悬停高亮窗口/元素的检测精度。");

    private static void AppendCaptureOptionArgs(List<string> args, ActionStepNode step, bool includeToolbar, bool includeDetect)
    {
        if (includeToolbar && step.GetParam("toolbar", "hide").Equals("show", StringComparison.OrdinalIgnoreCase))
            args.Add("toolbar=show");

        string radius = step.GetParam("radius", "default");
        if (!radius.Equals("default", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(radius))
            args.Add("radius=" + radius);

        string stroke = step.GetParam("stroke", "default");
        if (stroke.Equals("NoStroke", StringComparison.OrdinalIgnoreCase)) stroke = "0";
        if (!stroke.Equals("default", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(stroke))
            args.Add("stroke=" + stroke);

        string shadow = step.GetParam("shadow", "default");
        if (!shadow.Equals("default", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(shadow))
            args.Add("shadow=" + shadow.ToLowerInvariant());

        if (includeDetect)
        {
            string detect = step.GetParam("detect", "default");
            if (!detect.Equals("default", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(detect))
                args.Add("detect=" + detect.ToLowerInvariant());
        }
    }

    private static string QuoteArg(string raw) =>
        "'" + raw.Replace("'", "’").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "") + "'";

    public static string UnescapeArgValue(string raw) =>
        raw.Replace("\\n", Environment.NewLine);

    public static readonly ActionModuleDescriptor[] Modules =
    [
        // ════ 1. 获取内容 ════
        new ActionModuleDescriptor
        {
            Id = "capture.region",
            Category = ActionModuleCategory.Input,
            Name = "区域截图",
            ShortDescription = "框选屏幕区域并产出图片结果；可在更多选项中开启工具栏标注，或覆盖圆角、描边、阴影与智能检测。",
            Keywords = "截图 区域 框选 选区 工具栏 圆角 描边 阴影 capture screenshot image",
            IconKey = "image",
            DefaultResultLabel = "原图",
            ProducesResult = true,
            Parameters =
            [
                new ActionModuleParamSpec(
                    "toolbar", "交互方式", ActionParamKind.Choice, "hide",
                    Choices: [new("hide", "松手直接继续"), new("show", "显示工具栏标注")],
                    Tooltip: "选择「显示工具栏标注」时，框选后可先画箭头、打马赛克，确认后再自动流转到后续步骤。"),
                ParamRadius,
                ParamStroke,
                ParamShadow,
                ParamDetect
            ],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.Image,
            ToScript = step =>
            {
                var args = new List<string> { step.ResultName ?? "原图" };
                AppendCaptureOptionArgs(args, step, includeToolbar: true, includeDetect: true);
                return "yuSnip.captureImage(" + string.Join(", ", args.Select(QuoteArg)) + ");";
            },
            Describe = step =>
            {
                var tags = new List<string>();
                if (step.GetParam("toolbar", "hide").Equals("show", StringComparison.OrdinalIgnoreCase)) tags.Add("带工具栏");
                if (step.GetParam("stroke", "default") is "0" or "NoStroke") tags.Add("无描边");
                if (step.GetParam("shadow", "default").Equals("on", StringComparison.OrdinalIgnoreCase)) tags.Add("带阴影");
                return tags.Count > 0 ? "区域截图（" + string.Join("·", tags) + "）" : "区域截图";
            }
        },
        new ActionModuleDescriptor
        {
            Id = "capture.fullscreen",
            Category = ActionModuleCategory.Input,
            Name = "全屏截图",
            ShortDescription = "无遮罩直接截取全屏画面，支持全部屏幕、当前鼠标所在屏或主显示器。",
            Keywords = "全屏 截屏 整屏 多屏 显示器 fullscreen screen capture",
            IconKey = "fullscreen",
            DefaultResultLabel = "全屏截图",
            ProducesResult = true,
            Parameters =
            [
                new ActionModuleParamSpec(
                    "scope", "截取范围", ActionParamKind.Choice, "all",
                    Choices: [new("all", "全部屏幕"), new("current", "当前屏"), new("primary", "主显示器")],
                    Tooltip: "多显示器环境下可只截取鼠标所在屏幕或主显示器。")
            ],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.Image,
            ToScript = step =>
            {
                string name = step.ResultName ?? "全屏截图";
                string scope = step.GetParam("scope", "all").ToLowerInvariant();
                return scope is "current" or "primary"
                    ? $"yuSnip.captureScreen({QuoteArg(name)}, 'scope={scope}');"
                    : $"yuSnip.captureScreen({QuoteArg(name)});";
            },
            Describe = step => step.GetParam("scope", "all").ToLowerInvariant() switch
            {
                "current" => "全屏截图（当前屏）",
                "primary" => "全屏截图（主屏）",
                _ => "全屏截图"
            }
        },
        new ActionModuleDescriptor
        {
            Id = "capture.fixed",
            Category = ActionModuleCategory.Input,
            Name = "固定区域截图",
            ShortDescription = "无界面直接截取指定屏幕坐标区域，支持一键框选拾取坐标与外观效果覆盖。",
            Keywords = "固定 坐标 区域 定时 拾取 direct captureRegion rect",
            IconKey = "region",
            DefaultResultLabel = "区域截图",
            ProducesResult = true,
            Parameters =
            [
                new ActionModuleParamSpec("region", "屏幕坐标", ActionParamKind.Region, "0,0,800,600",
                    Tooltip: "虚拟屏幕坐标 X、Y 以及宽度 W、高度 H（像素），可点右侧按钮直接框选拾取。"),
                ParamRadius,
                ParamStroke,
                ParamShadow
            ],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.Image,
            ToScript = step =>
            {
                var (x, y, w, h) = ParseRegion(step.GetParam("region", "0,0,800,600"));
                var extra = new List<string>();
                AppendCaptureOptionArgs(extra, step, includeToolbar: false, includeDetect: false);
                string tail = extra.Count > 0 ? ", " + string.Join(", ", extra.Select(QuoteArg)) : "";
                return $"yuSnip.captureRegion({x}, {y}, {w}, {h}, {QuoteArg(step.ResultName ?? "区域截图")}{tail});";
            },
            Describe = step =>
            {
                var (x, y, w, h) = ParseRegion(step.GetParam("region", "0,0,800,600"));
                return $"固定区域截图 ({x},{y} {w}×{h})";
            }
        },
        new ActionModuleDescriptor
        {
            Id = "input.clipboard.image",
            Category = ActionModuleCategory.Input,
            Name = "剪贴板图片",
            ShortDescription = "读取当前剪贴板中的图片或图片文件。",
            Keywords = "剪贴板 粘贴 图片 clipboard image",
            IconKey = "paste",
            DefaultResultLabel = "剪贴板图片",
            ProducesResult = true,
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.Image,
            ToScript = step => $"yuSnip.readClipboard('Image', {QuoteArg(step.ResultName ?? "剪贴板图片")});",
            Describe = _ => "读取剪贴板图片"
        },
        new ActionModuleDescriptor
        {
            Id = "input.clipboard.text",
            Category = ActionModuleCategory.Input,
            Name = "剪贴板文字",
            ShortDescription = "读取当前剪贴板中的纯文本内容。",
            Keywords = "剪贴板 粘贴 文字 文本 clipboard text",
            IconKey = "text",
            DefaultResultLabel = "剪贴板文字",
            ProducesResult = true,
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.Text,
            ToScript = step => $"yuSnip.readClipboard('Text', {QuoteArg(step.ResultName ?? "剪贴板文字")});",
            Describe = _ => "读取剪贴板文字"
        },
        new ActionModuleDescriptor
        {
            Id = "input.sticker",
            Category = ActionModuleCategory.Input,
            Name = "焦点贴图",
            ShortDescription = "读取当前获得焦点的图片贴图，供识别、翻译、抠图或导出。",
            Keywords = "贴图 焦点 当前 sticker pin image",
            IconKey = "sticker",
            DefaultResultLabel = "焦点贴图",
            ProducesResult = true,
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.Image,
            ToScript = step => $"yuSnip.readSticker({QuoteArg(step.ResultName ?? "焦点贴图")});",
            Describe = _ => "读取焦点贴图"
        },

        // ════ 2. 智能处理 ════
        new ActionModuleDescriptor
        {
            Id = "process.ocr",
            Category = ActionModuleCategory.Process,
            Name = "OCR 文字识别",
            ShortDescription = "离线提取图片中的文字，支持自动合并断行或清理多余空格。",
            Keywords = "ocr 识别 文字 提取 段落 清理 text recognize",
            IconKey = "ocr",
            DefaultResultLabel = "识别文字",
            NeedsInput = true,
            ProducesResult = true,
            Parameters =
            [
                new ActionModuleParamSpec(
                    "cleanup", "文本整理", ActionParamKind.Choice, "none",
                    Choices: [new("none", "原样保留"), new("merge", "合并换行为段落"), new("compact", "去换行与多余空格")],
                    IsAdvanced: true,
                    Tooltip: "对 OCR 识别结果进行自动排版清理，方便直接粘贴。")
            ],
            AcceptedInputs = _ => (true, false),
            OutputKind = _ => ActionContentKind.Text,
            ToScript = step =>
            {
                string cleanup = step.GetParam("cleanup", "none").ToLowerInvariant();
                string tail = cleanup is "merge" or "compact" ? $", 'cleanup={cleanup}'" : "";
                return $"yuSnip.recognizeResult({QuoteArg(step.InputName ?? "")}, {QuoteArg(step.ResultName ?? "识别文字")}{tail});";
            },
            Describe = step => step.GetParam("cleanup", "none").ToLowerInvariant() switch
            {
                "merge" => "OCR 文字识别（合并段落）",
                "compact" => "OCR 文字识别（紧凑清理）",
                _ => "OCR 文字识别"
            }
        },
        new ActionModuleDescriptor
        {
            Id = "process.translate",
            Category = ActionModuleCategory.Process,
            Name = "内容翻译",
            ShortDescription = "将图片或文字翻译为译文文字或覆盖译文图片，可单次覆盖目标语言。",
            Keywords = "翻译 译文 外语 英文 日文 韩文 语言 translate",
            IconKey = "translate",
            DefaultResultLabel = "译文",
            NeedsInput = true,
            ProducesResult = true,
            Parameters =
            [
                new ActionModuleParamSpec(
                    "mode", "输出形式", ActionParamKind.Choice, "Text",
                    Choices: [new("Text", "译文文字"), new("Image", "译文图片")],
                    Tooltip: "译文文字支持图片或文字输入；译文图片需图片输入并依赖图片翻译服务。"),
                new ActionModuleParamSpec(
                    "to", "目标语言", ActionParamKind.Choice, "auto",
                    Choices: [new("auto", "跟随设置"), new("zh", "中文"), new("en", "英文"), new("ja", "日文"), new("ko", "韩文")],
                    IsAdvanced: true,
                    Tooltip: "单次覆盖翻译目标语言，不影响全局翻译设置。")
            ],
            AcceptedInputs = step => step.GetParam("mode", "Text").Equals("Image", StringComparison.OrdinalIgnoreCase)
                ? (true, false) : (true, true),
            OutputKind = step => step.GetParam("mode", "Text").Equals("Image", StringComparison.OrdinalIgnoreCase)
                ? ActionContentKind.Image : ActionContentKind.Text,
            ToScript = step =>
            {
                string mode = step.GetParam("mode", "Text").Equals("Image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Text";
                string fallback = mode == "Image" ? "译文图片" : "译文";
                string to = step.GetParam("to", "auto").ToLowerInvariant();
                string tail = to is "zh" or "en" or "ja" or "ko" ? $", 'to={to}'" : "";
                return $"yuSnip.translateResult('{mode}', {QuoteArg(step.InputName ?? "")}, {QuoteArg(step.ResultName ?? fallback)}{tail});";
            },
            Describe = step =>
            {
                string baseDesc = step.GetParam("mode", "Text").Equals("Image", StringComparison.OrdinalIgnoreCase)
                    ? "内容翻译 · 译文图片" : "内容翻译 · 译文文字";
                string to = step.GetParam("to", "auto").ToLowerInvariant();
                string lang = to switch { "zh" => "中文", "en" => "英文", "ja" => "日文", "ko" => "韩文", _ => "" };
                return string.IsNullOrEmpty(lang) ? baseDesc : $"{baseDesc}（→{lang}）";
            }
        },
        new ActionModuleDescriptor
        {
            Id = "process.matting",
            Category = ActionModuleCategory.Process,
            Name = "智能抠图",
            ShortDescription = "离线去除图片背景并提取主体，生成透明底新图片。",
            Keywords = "抠图 去背景 主体 人像 透明 matting cutout",
            IconKey = "matting",
            DefaultResultLabel = "抠图结果",
            NeedsInput = true,
            ProducesResult = true,
            Parameters =
            [
                new ActionModuleParamSpec(
                    "model", "识别模型", ActionParamKind.Choice, "Default",
                    Choices: [new("Default", "跟随设置"), new("General", "通用主体"), new("Portrait", "人像精细")],
                    Tooltip: "选择抠图使用的离线 AI 模型。")
            ],
            AcceptedInputs = _ => (true, false),
            OutputKind = _ => ActionContentKind.Image,
            ToScript = step =>
            {
                string model = step.GetParam("model", "Default");
                if (model is not ("Default" or "General" or "Portrait")) model = "Default";
                return $"yuSnip.mattingResult('{model}', {QuoteArg(step.InputName ?? "")}, {QuoteArg(step.ResultName ?? "抠图结果")});";
            },
            Describe = step => step.GetParam("model", "Default") switch
            {
                "General" => "智能抠图 · 通用主体",
                "Portrait" => "智能抠图 · 人像精细",
                _ => "智能抠图 · 跟随设置"
            }
        },

        // ════ 3. 结果输出 ════
        new ActionModuleDescriptor
        {
            Id = "output.copy",
            Category = ActionModuleCategory.Output,
            Name = "复制到剪贴板",
            ShortDescription = "将所选图片或文字复制到系统剪贴板。",
            Keywords = "复制 剪贴板 copy clipboard",
            IconKey = "copy",
            NeedsInput = true,
            AcceptedInputs = _ => (true, true),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step => $"yuSnip.copyResult({QuoteArg(step.InputName ?? "")});",
            Describe = _ => "复制到剪贴板"
        },
        new ActionModuleDescriptor
        {
            Id = "output.pin",
            Category = ActionModuleCategory.Output,
            Name = "生成贴图",
            ShortDescription = "将所选图片或文字钉在桌面上；可在更多选项中配置初始阴影、锁定、穿透与透明度。",
            Keywords = "贴图 钉屏 置顶 锁定 穿透 透明 阴影 pin sticker",
            IconKey = "sticker",
            NeedsInput = true,
            Parameters =
            [
                new ActionModuleParamSpec("shadow", "贴图阴影", ActionParamKind.Choice, "default", Choices: ShadowChoices, IsAdvanced: true,
                    Tooltip: "单次覆盖该贴图窗口是否显示外阴影。"),
                new ActionModuleParamSpec("opacity", "初始透明", ActionParamKind.NullableInt, "100",
                    Min: 20, Max: 100, Unit: "%", Step: 5, IsAdvanced: true,
                    Watermark: "默认 100 (20–100)",
                    Tooltip: "创建贴图时的初始不透明度（20%–100%），留空为 100%。"),
                new ActionModuleParamSpec("lock", "初始锁定", ActionParamKind.Choice, "off",
                    Choices: [new("off", "不锁定"), new("on", "创建即锁定")], IsAdvanced: true,
                    Tooltip: "开启后贴图创建完成即处于锁定态，防误拖。"),
                new ActionModuleParamSpec("pass", "鼠标穿透", ActionParamKind.Choice, "off",
                    Choices: [new("off", "关闭"), new("on", "创建即穿透")], IsAdvanced: true,
                    Tooltip: "开启后鼠标点击将直接穿透贴图作用于下方窗口。")
            ],
            AcceptedInputs = _ => (true, true),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step =>
            {
                var args = new List<string> { step.InputName ?? "" };
                string shadow = step.GetParam("shadow", "default").ToLowerInvariant();
                if (shadow is "on" or "off") args.Add("shadow=" + shadow);
                string opacity = step.GetParam("opacity", "100");
                if (int.TryParse(opacity, out int op) && op >= 20 && op < 100) args.Add("opacity=" + op);
                if (step.GetParam("lock", "off").Equals("on", StringComparison.OrdinalIgnoreCase)) args.Add("lock=on");
                if (step.GetParam("pass", "off").Equals("on", StringComparison.OrdinalIgnoreCase)) args.Add("pass=on");
                return "yuSnip.pinResult(" + string.Join(", ", args.Select(QuoteArg)) + ");";
            },
            Describe = step =>
            {
                var tags = new List<string>();
                if (step.GetParam("pass", "off").Equals("on", StringComparison.OrdinalIgnoreCase)) tags.Add("穿透");
                if (step.GetParam("lock", "off").Equals("on", StringComparison.OrdinalIgnoreCase)) tags.Add("锁定");
                if (int.TryParse(step.GetParam("opacity", "100"), out int op) && op < 100) tags.Add($"{op}%");
                return tags.Count > 0 ? "生成贴图（" + string.Join("·", tags) + "）" : "生成贴图";
            }
        },
        new ActionModuleDescriptor
        {
            Id = "output.save",
            Category = ActionModuleCategory.Output,
            Name = "保存到文件",
            ShortDescription = "将所选图片或文字落盘；可切换为“另存为”弹窗、指定保存目录或图片格式。",
            Keywords = "保存 另存为 目录 文件 导出 png jpg save file",
            IconKey = "saveas",
            NeedsInput = true,
            Parameters =
            [
                new ActionModuleParamSpec("mode", "保存方式", ActionParamKind.Choice, "quick",
                    Choices: [new("quick", "快速保存"), new("dialog", "弹出另存为")], IsAdvanced: true,
                    Tooltip: "快速保存自动落盘；弹出另存为可每次自选文件名与路径。"),
                new ActionModuleParamSpec("format", "图片格式", ActionParamKind.Choice, "png",
                    Choices: [new("png", "PNG（无损透明）"), new("jpg", "JPG（体积更小）")], IsAdvanced: true,
                    Tooltip: "仅对图片结果生效；文字结果固定保存为 UTF-8 TXT。"),
                new ActionModuleParamSpec("dir", "保存目录", ActionParamKind.Directory, "", IsAdvanced: true,
                    Watermark: "留空则使用全局截图保存目录",
                    Tooltip: "可为此动作指定独立归档文件夹，不影响全局默认目录。")
            ],
            AcceptedInputs = _ => (true, true),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step =>
            {
                var args = new List<string> { step.InputName ?? "" };
                if (step.GetParam("mode", "quick").Equals("dialog", StringComparison.OrdinalIgnoreCase)) args.Add("mode=dialog");
                if (step.GetParam("format", "png").Equals("jpg", StringComparison.OrdinalIgnoreCase)) args.Add("format=jpg");
                string dir = step.GetParam("dir", "").Trim();
                if (!string.IsNullOrEmpty(dir)) args.Add("dir=" + dir);
                return "yuSnip.saveResult(" + string.Join(", ", args.Select(QuoteArg)) + ");";
            },
            Describe = step => step.GetParam("mode", "quick").Equals("dialog", StringComparison.OrdinalIgnoreCase)
                ? "另存为文件" : "保存到文件"
        },
        new ActionModuleDescriptor
        {
            Id = "output.show",
            Category = ActionModuleCategory.Output,
            Name = "弹窗查看文字",
            ShortDescription = "在 OCR 或翻译结果窗口中打开文字，便于编辑与导出。",
            Keywords = "窗口 查看 展示 弹窗 文字 show window ocr",
            IconKey = "ocr",
            NeedsInput = true,
            AcceptedInputs = _ => (false, true),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step => $"yuSnip.showResult({QuoteArg(step.InputName ?? "")});",
            Describe = _ => "弹窗查看文字"
        },
        new ActionModuleDescriptor
        {
            Id = "output.vision",
            Category = ActionModuleCategory.Output,
            Name = "AI 识图",
            ShortDescription = "将所选图片发送至 AI 识图窗口，支持跟随设置、每次提问或固定专属提示词。",
            Keywords = "ai 识图 大模型 提问 提示词 prompt 分析 vision",
            IconKey = "aivision",
            NeedsInput = true,
            Parameters =
            [
                new ActionModuleParamSpec(
                    "promptMode", "提问方式", ActionParamKind.Choice, "Default",
                    Choices: [new("Default", "跟随设置"), new("Prompt", "每次提问"), new("Custom", "固定提示词")],
                    Tooltip: "选择「固定提示词」可打造专属一键分析动作（如提取表格、解释报错）。"),
                new ActionModuleParamSpec(
                    "customPrompt", "提示词", ActionParamKind.Text, "",
                    Watermark: "例如：请将图中的表格完整提取为 Markdown 表格",
                    VisibleWhenParamKey: "promptMode", VisibleWhenParamValue: "Custom",
                    Tooltip: "每次触发本动作时直接以此提示词向 AI 提问。")
            ],
            AcceptedInputs = _ => (true, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step =>
            {
                string mode = step.GetParam("promptMode", "Default");
                if (mode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                {
                    string prompt = step.GetParam("customPrompt", "").Trim();
                    return string.IsNullOrEmpty(prompt)
                        ? $"yuSnip.visionResult('Default', {QuoteArg(step.InputName ?? "")});"
                        : $"yuSnip.visionResult('Custom', {QuoteArg(step.InputName ?? "")}, {QuoteArg("prompt=" + prompt)});";
                }
                string m = mode.Equals("Prompt", StringComparison.OrdinalIgnoreCase) ? "Prompt" : "Default";
                return $"yuSnip.visionResult('{m}', {QuoteArg(step.InputName ?? "")});";
            },
            Describe = step => step.GetParam("promptMode", "Default") switch
            {
                "Prompt" => "AI 识图 · 每次提问",
                "Custom" => "AI 识图 · 固定提示词",
                _ => "AI 识图"
            }
        },

        // ════ 4. 互动与控制 ════
        new ActionModuleDescriptor
        {
            Id = "control.wait",
            Category = ActionModuleCategory.Control,
            Name = "等待延时",
            ShortDescription = "暂停指定毫秒后再继续执行后续步骤（如等待下拉菜单展开）。",
            Keywords = "等待 延时 延迟 暂停 定时 wait delay sleep",
            IconKey = "wait",
            Parameters =
            [
                new ActionModuleParamSpec(
                    "ms", "等待时长", ActionParamKind.Int, "1000",
                    Min: 0, Max: ScriptExecutor.MaximumWaitMilliseconds, Unit: "ms",
                    QuickPresets: [500, 1000, 3000], Step: 500,
                    Watermark: "0–60000",
                    Tooltip: $"0 毫秒立即继续，最长 {ScriptExecutor.MaximumWaitMilliseconds / 1000} 秒。")
            ],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step => $"yuSnip.wait({Math.Clamp(step.GetIntParam("ms", 1000), 0, ScriptExecutor.MaximumWaitMilliseconds).ToString(CultureInfo.InvariantCulture)});",
            Describe = step => $"等待 {step.GetIntParam("ms", 1000)} 毫秒"
        },
        new ActionModuleDescriptor
        {
            Id = "tool.annotate",
            Category = ActionModuleCategory.Control,
            Name = "截图并标注",
            ShortDescription = "框选区域后停留在标注界面，可单次覆盖圆角、描边、阴影与检测模式。",
            Keywords = "截图 标注 画笔 箭头 圆角 阴影 描边 编辑 annotate screenshot",
            IconKey = "brush",
            Parameters = [ParamRadius, ParamStroke, ParamShadow, ParamDetect],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step =>
            {
                var extra = new List<string>();
                AppendCaptureOptionArgs(extra, step, includeToolbar: false, includeDetect: true);
                return extra.Count == 0
                    ? "yuSnip.screenShot();"
                    : "yuSnip.screenShot(" + string.Join(", ", extra.Select(QuoteArg)) + ");";
            },
            Describe = _ => "截图并标注"
        },
        new ActionModuleDescriptor
        {
            Id = "tool.record",
            Category = ActionModuleCategory.Control,
            Name = "屏幕录制",
            ShortDescription = "框选区域录制视频或 GIF；录制中再次触发可结束录制。",
            Keywords = "录屏 录制 视频 gif record video",
            IconKey = "record",
            Parameters =
            [
                new ActionModuleParamSpec(
                    "startMode", "开始方式", ActionParamKind.Choice, "Confirm",
                    Choices: [new("Confirm", "先确认录制选项"), new("Quick", "框选后立即录制")],
                    Tooltip: "立即录制会跳过预录制参数条，按当前设置直接开录。")
            ],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step => step.GetParam("startMode", "Confirm").Equals("Quick", StringComparison.OrdinalIgnoreCase)
                ? "yuSnip.screenRecord('Quick');" : "yuSnip.screenRecord();",
            Describe = step => step.GetParam("startMode", "Confirm").Equals("Quick", StringComparison.OrdinalIgnoreCase)
                ? "屏幕录制 · 框选即录" : "屏幕录制"
        },
        new ActionModuleDescriptor
        {
            Id = "tool.longscroll",
            Category = ActionModuleCategory.Control,
            Name = "长截图",
            ShortDescription = "框选滚动区域直接进入长截图拼接，支持纵向或横向滚动。",
            Keywords = "长截图 滚动 拼接 横向 纵向 longscroll scroll",
            IconKey = "longscroll",
            Parameters =
            [
                new ActionModuleParamSpec(
                    "axis", "滚动方向", ActionParamKind.Choice, "Vertical",
                    Choices: [new("Vertical", "纵向滚动"), new("Horizontal", "横向滚动")],
                    Tooltip: "选择长截图的拼接滚动方向。")
            ],
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = step => step.GetParam("axis", "Vertical").Equals("Horizontal", StringComparison.OrdinalIgnoreCase)
                ? "yuSnip.longScroll('Horizontal');" : "yuSnip.longScroll();",
            Describe = step => step.GetParam("axis", "Vertical").Equals("Horizontal", StringComparison.OrdinalIgnoreCase)
                ? "长截图 · 横向" : "长截图"
        },
        new ActionModuleDescriptor
        {
            Id = "window.toggleStickers",
            Category = ActionModuleCategory.Control,
            Name = "显隐全部贴图",
            ShortDescription = "统一隐藏或重新显示当前桌面上的所有贴图。",
            Keywords = "隐藏 显示 贴图 切换 toggle sticker",
            IconKey = "sticker",
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = _ => "yuSnip.toggleAllStickers();",
            Describe = _ => "显隐全部贴图"
        },
        new ActionModuleDescriptor
        {
            Id = "window.restoreSticker",
            Category = ActionModuleCategory.Control,
            Name = "恢复上次贴图",
            ShortDescription = "恢复最近关闭的一张贴图（包含位置与标注）。",
            Keywords = "恢复 贴图 撤销 关闭 restore sticker",
            IconKey = "undo",
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = _ => "yuSnip.restoreLastSticker();",
            Describe = _ => "恢复上次贴图"
        },
        new ActionModuleDescriptor
        {
            Id = "window.closeStickers",
            Category = ActionModuleCategory.Control,
            Name = "关闭全部贴图",
            ShortDescription = "一键关闭当前屏幕上的所有贴图窗口。",
            Keywords = "关闭 清空 销毁 贴图 close destroy sticker",
            IconKey = "close",
            AcceptedInputs = _ => (false, false),
            OutputKind = _ => ActionContentKind.None,
            ToScript = _ => "yuSnip.destroyAllPin();",
            Describe = _ => "关闭全部贴图"
        }
    ];

    public static readonly ActionTemplateItem[] Templates =
    [
        new("annotate-copy-save", "标注后同时复制与保存", "先显示工具栏画箭头打码，点确认后自动复制并存盘。",
            "yuSnip.captureImage('原图', 'toolbar=show');\nyuSnip.copyResult('原图');\nyuSnip.saveResult('原图');"),
        new("doc-card", "文档精致卡片截图", "强制开启圆角、描边与柔和阴影，同时复制并保存。",
            "yuSnip.captureImage('卡片图', 'toolbar=show', 'radius=12', 'stroke=2', 'shadow=on');\nyuSnip.copyResult('卡片图');\nyuSnip.saveResult('卡片图');"),
        new("ghost-ref", "半透明穿透参考贴图", "框选参考图后以 70% 透明与鼠标穿透状态钉在屏幕上。",
            "yuSnip.captureImage('参考图', 'radius=0', 'stroke=0', 'shadow=off');\nyuSnip.pinResult('参考图', 'opacity=70', 'pass=on');"),
        new("delay-clean-shot", "延时 2 秒无描边截图", "等待下拉菜单展开后再无描边截图并复制。",
            "yuSnip.wait(2000);\nyuSnip.captureImage('原图', 'stroke=0');\nyuSnip.copyResult('原图');"),
        new("ai-table", "截图提取表格为 Markdown", "框选表格直接用固定提示词交给 AI 提取为 Markdown。",
            "yuSnip.captureImage('表格图', 'radius=0', 'stroke=0', 'shadow=off');\nyuSnip.visionResult('Custom', '表格图', 'prompt=请将图中的表格完整提取为标准的 Markdown 表格，不要遗漏数据。');"),
        new("ocr-clean-copy", "OCR 识别并整理段落复制", "框选文字识别后自动合并换行并复制到剪贴板。",
            "yuSnip.captureImage('原图', 'radius=0', 'stroke=0', 'shadow=off');\nyuSnip.recognizeResult('原图', '识别文字', 'cleanup=merge');\nyuSnip.copyResult('识别文字');"),
        new("translate-en", "截图翻译为英文并复制", "单次覆盖目标语言为英文并静默写入剪贴板。",
            "yuSnip.captureImage('原图');\nyuSnip.translateResult('Text', '原图', '英文译文', 'to=en');\nyuSnip.copyResult('英文译文');"),
        new("sticker-matting", "抠图焦点贴图并重新贴出", "对当前焦点贴图离线去背景并贴回桌面。",
            "yuSnip.readSticker('焦点贴图');\nyuSnip.mattingResult('Default', '焦点贴图', '抠图结果');\nyuSnip.pinResult('抠图结果');"),
        new("branch", "一次截图多路输出", "同一张截图同时翻译、OCR 并分别输出。",
            "yuSnip.captureImage('原图');\nyuSnip.translateResult('Text', '原图', '译文');\nyuSnip.recognizeResult('原图', '识别文字');\nyuSnip.copyResult('译文');\nyuSnip.showResult('识别文字');")
    ];

    private static readonly Dictionary<string, ActionModuleDescriptor> ById =
        Modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

    public static ActionModuleDescriptor? Find(string id) =>
        ById.TryGetValue(id, out var d) ? d : null;

    public static ActionModuleDescriptor GetRequired(string id) =>
        ById.TryGetValue(id, out var d) ? d : Modules[0];

    public static (int X, int Y, int W, int H) ParseRegion(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var normalized = raw.Replace('，', ',');
            var parts = normalized.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0
                && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h > 0)
                return (x, y, w, h);
        }
        return (0, 0, 800, 600);
    }

    public static string FormatRegion(int x, int y, int w, int h) =>
        FormattableString.Invariant($"{x}, {y}, {Math.Max(1, w)}, {Math.Max(1, h)}");

    public static List<ActionDataRef> ResultsBefore(IReadOnlyList<ActionStepNode> steps, int position)
    {
        var results = new List<ActionDataRef>();
        for (int i = 0; i < position && i < steps.Count; i++)
        {
            var step = steps[i];
            var desc = Find(step.ModuleId);
            if (desc == null || !desc.ProducesResult || string.IsNullOrWhiteSpace(step.ResultName)) continue;
            results.Add(new(step.ResultName, desc.OutputKind(step), i));
        }
        return results;
    }

    public static List<ActionDataRef> CompatibleInputs(ActionStepNode step, IEnumerable<ActionDataRef> prior)
    {
        var desc = Find(step.ModuleId);
        if (desc == null || !desc.NeedsInput) return [];
        var (acceptImg, acceptTxt) = desc.AcceptedInputs(step);
        return prior.Where(r => (r.Kind == ActionContentKind.Image && acceptImg) || (r.Kind == ActionContentKind.Text && acceptTxt)).ToList();
    }

    public static string UniqueResultName(string baseLabel, IReadOnlyList<ActionStepNode> steps, ISet<string>? reserved = null)
    {
        string name = baseLabel;
        for (int suffix = 2; steps.Any(s => string.Equals(s.ResultName, name, StringComparison.Ordinal))
            || reserved?.Contains(name) == true; suffix++)
            name = baseLabel + suffix;
        return name;
    }

    public static ActionStepNode CreateStep(string moduleId, IReadOnlyList<ActionStepNode> existingSteps, int insertPosition, string? preferredInput = null)
    {
        var desc = GetRequired(moduleId);
        var paramDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in desc.Parameters) paramDict[p.Key] = p.DefaultValue;

        var draft = new ActionStepNode(desc.Id, Params: paramDict);
        string? input = null;
        if (desc.NeedsInput)
        {
            var compatible = CompatibleInputs(draft, ResultsBefore(existingSteps, insertPosition));
            input = compatible.LastOrDefault(r => r.Name == preferredInput)?.Name
                ?? compatible.LastOrDefault()?.Name;
        }
        string? result = desc.ProducesResult
            ? UniqueResultName(desc.DefaultResultLabel ?? desc.Name, existingSteps)
            : null;
        return draft with { InputName = input, ResultName = result };
    }

    public static string BuildScript(IEnumerable<ActionStepNode> steps) =>
        string.Join(Environment.NewLine, steps.Select(s => GetRequired(s.ModuleId).ToScript(s)));

    public static string DescribeStep(ActionStepNode step)
    {
        var desc = Find(step.ModuleId);
        if (desc == null) return step.ModuleId;
        string core = desc.Describe(step);
        if (!string.IsNullOrEmpty(step.InputName)) core += $"（{step.InputName}）";
        if (!string.IsNullOrEmpty(step.ResultName)) core += $" ⇒ {step.ResultName}";
        return core;
    }

    public static List<WorkflowStepDiagnostic> ValidateSteps(IReadOnlyList<ActionStepNode> steps)
    {
        var diags = new List<WorkflowStepDiagnostic>();
        if (steps.Count == 0)
        {
            diags.Add(new(-1, "请至少添加一个模块。", DiagnosticFixKind.InsertCaptureRegion, "添加「区域截图」"));
            return diags;
        }

        var produced = new Dictionary<string, ActionContentKind>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var desc = Find(step.ModuleId);
            if (desc == null)
            {
                diags.Add(new(i, "未知模块，请删除后重新添加。"));
                continue;
            }

            if (desc.NeedsInput)
            {
                var prior = ResultsBefore(steps, i);
                var compatible = CompatibleInputs(step, prior);
                var (needImg, needTxt) = desc.AcceptedInputs(step);
                if (string.IsNullOrWhiteSpace(step.InputName))
                {
                    if (compatible.Count > 0)
                    {
                        diags.Add(new(i, $"尚未选择输入内容，可选用前置结果「{compatible[^1].Name}」。",
                            DiagnosticFixKind.SelectAvailableInput, $"选用「{compatible[^1].Name}」", compatible[^1].Name));
                    }
                    else if (needImg)
                    {
                        diags.Add(new(i, "缺少图片输入，请先在上方添加截图或图片读取模块。",
                            DiagnosticFixKind.InsertCaptureRegion, "上方插入「区域截图」"));
                    }
                    else
                    {
                        diags.Add(new(i, "缺少文字输入，请先在上方添加 OCR、翻译或剪贴板文字模块。",
                            DiagnosticFixKind.InsertClipboardText, "上方插入「剪贴板文字」"));
                    }
                }
                else if (!produced.TryGetValue(step.InputName, out var actualKind))
                {
                    if (compatible.Count > 0)
                    {
                        diags.Add(new(i, $"前置步骤中找不到结果「{step.InputName}」。",
                            DiagnosticFixKind.SelectAvailableInput, $"改为「{compatible[^1].Name}」", compatible[^1].Name));
                    }
                    else
                    {
                        diags.Add(new(i, $"找不到结果「{step.InputName}」，请添加或上移产出该结果的模块。",
                            needImg ? DiagnosticFixKind.InsertCaptureRegion : DiagnosticFixKind.InsertClipboardText,
                            needImg ? "上方插入「区域截图」" : "上方插入「剪贴板文字」"));
                    }
                }
                else if (!((actualKind == ActionContentKind.Image && needImg) || (actualKind == ActionContentKind.Text && needTxt)))
                {
                    string expected = (needImg, needTxt) switch { (true, false) => "图片", (false, true) => "文字", _ => "图片或文字" };
                    string got = actualKind == ActionContentKind.Image ? "图片" : "文字";
                    if (compatible.Count > 0)
                    {
                        diags.Add(new(i, $"输入「{step.InputName}」是{got}，本模块需要{expected}。",
                            DiagnosticFixKind.SelectAvailableInput, $"改为「{compatible[^1].Name}」", compatible[^1].Name));
                    }
                    else
                    {
                        diags.Add(new(i, $"输入「{step.InputName}」是{got}，本模块需要{expected}。",
                            needImg ? DiagnosticFixKind.InsertCaptureRegion : DiagnosticFixKind.InsertClipboardText,
                            needImg ? "上方插入「区域截图」" : "上方插入「剪贴板文字」"));
                    }
                }
            }

            if (desc.ProducesResult)
            {
                string name = step.ResultName ?? "";
                if (!ScriptExecutor.IsResultName(name))
                {
                    string suggestion = UniqueResultName(desc.DefaultResultLabel ?? desc.Name, steps);
                    diags.Add(new(i, "结果名称不能为空，且不能包含引号、逗号、换行或 #。",
                        DiagnosticFixKind.FixDuplicateResultName, $"命名为「{suggestion}」", suggestion));
                }
                else if (produced.ContainsKey(name))
                {
                    string suggestion = UniqueResultName(desc.DefaultResultLabel ?? desc.Name, steps);
                    diags.Add(new(i, $"结果名称「{name}」与前面重复，请使用不同名称。",
                        DiagnosticFixKind.FixDuplicateResultName, $"改名为「{suggestion}」", suggestion));
                }
                else
                {
                    produced[name] = desc.OutputKind(step);
                }
            }
        }
        return diags;
    }

    private static Dictionary<string, string> ReadCaptureParams(ActionCaptureOptions opt, bool includeToolbar, bool includeDetect)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (includeToolbar) dict["toolbar"] = opt.ShowToolbar == true ? "show" : "hide";
        dict["radius"] = opt.CornerRadiusOverride.HasValue ? opt.CornerRadiusOverride.Value.ToString(CultureInfo.InvariantCulture) : "default";
        dict["stroke"] = opt.StrokeWidthOverride.HasValue
            ? opt.StrokeWidthOverride.Value.ToString(CultureInfo.InvariantCulture)
            : (opt.WithoutStroke ? "0" : "default");
        dict["shadow"] = opt.ShadowOverride.HasValue ? (opt.ShadowOverride.Value ? "on" : "off") : "default";
        if (includeDetect) dict["detect"] = ActionCaptureOptions.NormalizeDetectKey(opt.DetectModeOverride);
        return dict;
    }

    public static bool TryParseSteps(string script, out List<ActionStepNode> steps)
    {
        steps = [];
        if (string.IsNullOrWhiteSpace(script)) return true;
        if (!ScriptExecutor.TryParse(script, out var calls, out _, validateOrder: false)) return false;
        if (!ExpandLegacyCalls(calls, out var expanded)) return false;

        var reserved = expanded.Where(c => c.ResultName != null).Select(c => c.ResultName!).ToHashSet(StringComparer.Ordinal);
        string? current = null;
        foreach (var call in expanded)
        {
            if (call.Method == "captureimage")
            {
                var opt = call.CaptureOptions;
                if (opt.DelayMilliseconds > 0)
                {
                    steps.Add(new ActionStepNode("control.wait", Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ms"] = opt.DelayMilliseconds.ToString(CultureInfo.InvariantCulture)
                    }));
                }
                string resName = call.ResultName ?? UniqueResultName("原图", steps, reserved);
                steps.Add(new ActionStepNode("capture.region", ResultName: resName,
                    Params: ReadCaptureParams(opt, includeToolbar: true, includeDetect: true)));
                current = resName;
                continue;
            }

            ActionStepNode? mapped = call.Method switch
            {
                "capturescreen" => MakeCaptureScreen(call, steps, reserved),
                "captureregion" => MakeRegionProducer(call, steps, reserved),
                "readclipboard" => MakeProducer(
                    call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase) ? "input.clipboard.image" : "input.clipboard.text",
                    call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase) ? "剪贴板图片" : "剪贴板文字",
                    call.ResultName, steps, reserved),
                "readsticker" => MakeProducer("input.sticker", "焦点贴图", call.ResultName, steps, reserved),
                "recognizeresult" => MakeRecognize(call, current, steps, reserved),
                "translateresult" => MakeTranslate(call, current, steps, reserved),
                "mattingresult" => MakeMatting(call, current, steps, reserved),
                "copyresult" => new ActionStepNode("output.copy", InputName: call.InputName ?? current),
                "pinresult" => MakePinResult(call, current),
                "saveresult" => MakeSaveResult(call, current),
                "showresult" => new ActionStepNode("output.show", InputName: call.InputName ?? current),
                "visionresult" => MakeVisionResult(call, current),
                "wait" => new ActionStepNode("control.wait",
                    Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ms"] = call.WaitMilliseconds.ToString(CultureInfo.InvariantCulture)
                    }),
                "screenshot" when call.IsInteractiveScreenShot => new ActionStepNode("tool.annotate",
                    Params: ReadCaptureParams(call.CaptureOptions, includeToolbar: false, includeDetect: true)),
                "screenrecord" => new ActionStepNode("tool.record",
                    Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["startMode"] = call.Arguments.Length > 0 && call.Arguments[0].ToLowerInvariant() is "quick" or "fast" or "recordquick" or "quickrecord"
                            ? "Quick" : "Confirm"
                    }),
                "longscroll" => new ActionStepNode("tool.longscroll",
                    Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["axis"] = call.Arguments.Length > 0 && call.Arguments[0].Equals("Horizontal", StringComparison.OrdinalIgnoreCase)
                            ? "Horizontal" : "Vertical"
                    }),
                "toggleallstickers" => new ActionStepNode("window.toggleStickers"),
                "restorelaststicker" => new ActionStepNode("window.restoreSticker"),
                "destroyallpin" => new ActionStepNode("window.closeStickers"),
                _ => null
            };

            if (mapped == null)
            {
                steps.Clear();
                return false;
            }

            steps.Add(mapped);
            if (mapped.ResultName != null) current = mapped.ResultName;
        }
        return steps.Count > 0;
    }

    private static ActionStepNode MakeProducer(string moduleId, string defaultLabel, string? explicitName,
        List<ActionStepNode> steps, HashSet<string> reserved) =>
        new(moduleId, ResultName: explicitName ?? UniqueResultName(defaultLabel, steps, reserved));

    private static ActionStepNode MakeCaptureScreen(ScriptCommand call, List<ActionStepNode> steps, HashSet<string> reserved)
    {
        ActionCaptureOptions.TryParseKeyValues(call.Arguments, 1, out var kv);
        string scope = kv.TryGetValue("scope", out var s) ? s.ToLowerInvariant() : "all";
        if (scope is not ("all" or "current" or "primary")) scope = "all";
        return new ActionStepNode("capture.fullscreen",
            ResultName: call.ResultName ?? UniqueResultName("全屏截图", steps, reserved),
            Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["scope"] = scope });
    }

    private static ActionStepNode MakeRegionProducer(ScriptCommand call, List<ActionStepNode> steps, HashSet<string> reserved)
    {
        int x = int.Parse(call.Arguments[0], CultureInfo.InvariantCulture);
        int y = int.Parse(call.Arguments[1], CultureInfo.InvariantCulture);
        int w = int.Parse(call.Arguments[2], CultureInfo.InvariantCulture);
        int h = int.Parse(call.Arguments[3], CultureInfo.InvariantCulture);
        string name = call.ResultName ?? UniqueResultName("区域截图", steps, reserved);
        var dict = ReadCaptureParams(call.CaptureOptions, includeToolbar: false, includeDetect: false);
        dict["region"] = FormatRegion(x, y, w, h);
        return new ActionStepNode("capture.fixed", ResultName: name, Params: dict);
    }

    private static ActionStepNode MakeRecognize(ScriptCommand call, string? current, List<ActionStepNode> steps, HashSet<string> reserved)
    {
        ActionCaptureOptions.TryParseKeyValues(call.Arguments, 2, out var kv);
        string cleanup = kv.TryGetValue("cleanup", out var c) ? c.ToLowerInvariant() : "none";
        if (cleanup is not ("none" or "merge" or "compact")) cleanup = "none";
        return new ActionStepNode("process.ocr",
            InputName: call.InputName ?? current,
            ResultName: call.ResultName ?? UniqueResultName("识别文字", steps, reserved),
            Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cleanup"] = cleanup });
    }

    private static ActionStepNode MakeTranslate(ScriptCommand call, string? current, List<ActionStepNode> steps, HashSet<string> reserved)
    {
        bool isImage = call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase);
        string mode = isImage ? "Image" : "Text";
        string name = call.ResultName ?? UniqueResultName(isImage ? "译文图片" : "译文", steps, reserved);
        ActionCaptureOptions.TryParseKeyValues(call.Arguments, 3, out var kv);
        string to = kv.TryGetValue("to", out var lang) ? lang.ToLowerInvariant() : "auto";
        if (to is not ("auto" or "zh" or "en" or "ja" or "ko")) to = "auto";
        return new ActionStepNode("process.translate", InputName: call.InputName ?? current, ResultName: name,
            Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mode"] = mode, ["to"] = to });
    }

    private static ActionStepNode MakeMatting(ScriptCommand call, string? current, List<ActionStepNode> steps, HashSet<string> reserved)
    {
        string arg = call.Arguments[0].ToLowerInvariant();
        string model = arg == "portrait" ? "Portrait" : arg == "general" ? "General" : "Default";
        string name = call.ResultName ?? UniqueResultName("抠图结果", steps, reserved);
        return new ActionStepNode("process.matting", InputName: call.InputName ?? current, ResultName: name,
            Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["model"] = model });
    }

    private static ActionStepNode MakePinResult(ScriptCommand call, string? current)
    {
        ActionCaptureOptions.TryParseKeyValues(call.Arguments, 1, out var kv);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shadow"] = kv.TryGetValue("shadow", out var sh) && sh.ToLowerInvariant() is "on" or "off" ? sh.ToLowerInvariant() : "default",
            ["opacity"] = kv.TryGetValue("opacity", out var op) && int.TryParse(op, out int opv) && opv is >= 20 and <= 100
                ? opv.ToString(CultureInfo.InvariantCulture) : "100",
            ["lock"] = kv.TryGetValue("lock", out var lk) && lk.ToLowerInvariant() is "on" or "true" ? "on" : "off",
            ["pass"] = kv.TryGetValue("pass", out var ps) && ps.ToLowerInvariant() is "on" or "true" ? "on" : "off",
        };
        return new ActionStepNode("output.pin", InputName: call.InputName ?? current, Params: dict);
    }

    private static ActionStepNode MakeSaveResult(ScriptCommand call, string? current)
    {
        ActionCaptureOptions.TryParseKeyValues(call.Arguments, 1, out var kv);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = kv.TryGetValue("mode", out var m) && m.Equals("dialog", StringComparison.OrdinalIgnoreCase) ? "dialog" : "quick",
            ["format"] = kv.TryGetValue("format", out var f) && f.ToLowerInvariant() is "jpg" or "jpeg" ? "jpg" : "png",
            ["dir"] = kv.TryGetValue("dir", out var d) ? d.Trim() : "",
        };
        return new ActionStepNode("output.save", InputName: call.InputName ?? current, Params: dict);
    }

    private static ActionStepNode MakeVisionResult(ScriptCommand call, string? current)
    {
        string arg0 = call.Arguments[0].ToLowerInvariant();
        ActionCaptureOptions.TryParseKeyValues(call.Arguments, 2, out var kv);
        string customPrompt = kv.TryGetValue("prompt", out var p) ? UnescapeArgValue(p) : "";
        string mode = arg0 == "custom" ? "Custom" : arg0 == "prompt" ? "Prompt" : "Default";
        return new ActionStepNode("output.vision", InputName: call.InputName ?? current,
            Params: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["promptMode"] = mode,
                ["customPrompt"] = customPrompt
            });
    }

    private static bool ExpandLegacyCalls(List<ScriptCommand> original, out List<ScriptCommand> expanded)
    {
        var result = new List<ScriptCommand>();
        expanded = result;
        bool hasContent = false;
        void Add(string method, params string[] args) => result.Add(new(method, args));
        void EnsureImage()
        {
            if (!hasContent) { Add("captureimage"); hasContent = true; }
        }

        foreach (var call in original)
        {
            if (call.UsesWorkflow || call.IsWait
                || call.Method is "toggleallstickers" or "destroyallpin" or "restorelaststicker" or "screenrecord" or "longscroll"
                || call.IsInteractiveScreenShot)
            {
                result.Add(call);
                if (call.ProducesResult) hasContent = true;
                continue;
            }

            string arg = call.Arguments.Length == 0 ? "" : call.Arguments[0].ToLowerInvariant();
            switch (call.Method)
            {
                case "screenshot" when arg is "copy" or "pin" or "save":
                    if (hasContent) return false;
                    EnsureImage();
                    Add(arg == "copy" ? "copyresult" : arg == "pin" ? "pinresult" : "saveresult");
                    break;
                case "screenshot" when arg is "ocr" or "ocrtoast":
                    EnsureImage();
                    Add("recognizeresult");
                    Add(arg == "ocr" ? "showresult" : "copyresult");
                    break;
                case "screenshot" when arg is "vision" or "visionprompt":
                    EnsureImage();
                    Add("visionresult", arg == "vision" ? "Default" : "Prompt");
                    break;
                case "translate" when arg is "" or "screen":
                case "screenshot" when arg == "translate":
                    EnsureImage();
                    bool imgMode = AppSettings.Current.TranslateMode.Equals("ImageToImage", StringComparison.OrdinalIgnoreCase);
                    Add("translateresult", imgMode ? "Image" : "Text");
                    Add(imgMode ? "pinresult" : "showresult");
                    break;
                case "translate" when arg is "imagetoimage" or "imagetotext" or "silent":
                    EnsureImage();
                    Add("translateresult", arg == "imagetoimage" ? "Image" : "Text");
                    Add(arg == "imagetoimage" ? "pinresult" : arg == "silent" ? "copyresult" : "showresult");
                    break;
                case "translate" when arg == "clipboard":
                    if (!hasContent) { Add("readclipboard", "Text"); hasContent = true; }
                    Add("translateresult", "Text");
                    Add("showresult");
                    break;
                case "translate" when arg == "sticker":
                    if (!hasContent) { Add("readsticker"); hasContent = true; }
                    bool stickerImg = AppSettings.Current.TranslateMode.Equals("ImageToImage", StringComparison.OrdinalIgnoreCase);
                    Add("translateresult", stickerImg ? "Image" : "Text");
                    Add(stickerImg ? "pinresult" : "showresult");
                    break;
                case "screenshot" when arg is "matting" or "mattinggeneral" or "mattingportrait":
                case "matting" when arg is "" or "general" or "portrait" or "mattinggeneral" or "mattingportrait":
                    EnsureImage();
                    Add("mattingresult", arg is "portrait" or "mattingportrait" ? "Portrait" : arg is "general" or "mattinggeneral" ? "General" : "Default");
                    string dest = AppSettings.Current.MattingResultDestination;
                    if (!dest.Equals("Sticker", StringComparison.OrdinalIgnoreCase)) Add("copyresult");
                    if (!dest.Equals("Clipboard", StringComparison.OrdinalIgnoreCase)) Add("pinresult");
                    break;
                case "matting" when arg is "sticker" or "pin":
                    if (!hasContent) { Add("readsticker"); hasContent = true; }
                    Add("mattingresult", "Default");
                    string sDest = AppSettings.Current.MattingResultDestination;
                    if (!sDest.Equals("Sticker", StringComparison.OrdinalIgnoreCase)) Add("copyresult");
                    if (!sDest.Equals("Clipboard", StringComparison.OrdinalIgnoreCase)) Add("pinresult");
                    break;
                case "directscreenshot":
                    Add("captureregion", call.Arguments[0], call.Arguments[1], call.Arguments[2], call.Arguments[3]);
                    hasContent = true;
                    string da = call.Arguments.Length > 4 ? call.Arguments[4].ToLowerInvariant() : "copy";
                    if (da is "pin" or "sticker") Add("pinresult");
                    else if (da == "save") Add("saveresult");
                    else if (da is "matting" or "mattinggeneral" or "mattingportrait")
                    {
                        Add("mattingresult", da == "mattingportrait" ? "Portrait" : da == "mattinggeneral" ? "General" : "Default");
                        Add("pinresult");
                    }
                    else Add("copyresult");
                    break;
                case "pinfromclipboard":
                    if (!hasContent) { Add("readclipboard", "Image"); hasContent = true; }
                    Add("pinresult");
                    break;
                default:
                    return false;
            }
        }
        expanded = result;
        return !expanded.Exists(c => c.UsesWorkflow) || ScriptExecutor.ValidateWorkflow(expanded, out _);
    }

    public static string DescribePreset(string presetId) => presetId switch
    {
        "builtin.capture" => "框选屏幕区域后进入标注工具栏，支持画笔、箭头、马赛克、圆角阴影等，完成后再自行复制、保存或贴图。",
        "builtin.captureCopy" => "框选屏幕区域后直接写入剪贴板，适合快速粘贴发送。",
        "builtin.captureSave" => "框选屏幕区域后直接保存为图片到设置中的截图目录。",
        "builtin.capturePin" => "框选屏幕区域后直接生成桌面置顶贴图。",
        "builtin.longScroll" => "框选需要滚动的区域，自动向下拼接生成超长截图。",
        "builtin.pin" => "将剪贴板中的图片、文字或色号直接钉在屏幕上成为贴图。",
        "builtin.restoreLastSticker" => "恢复最近一次关闭的贴图（保留位置、尺寸与标注）。",
        "builtin.toggleStickers" => "一键隐藏或重新显示当前桌面上的全部贴图。",
        "builtin.destroyAllPins" => "立即关闭当前桌面上的所有贴图窗口。",
        "builtin.captureOcr" => "框选屏幕区域并离线识别文字，在编辑窗口中查看、排版和导出。",
        "builtin.captureOcrToast" => "框选屏幕区域并离线识别文字，结果直接写入剪贴板并弹出轻提示。",
        "builtin.captureVision" => "框选屏幕区域并发送到 AI 识图窗口（使用默认提示词或跟随设置）。",
        "builtin.captureVisionPrompt" => "框选屏幕区域后先弹出提问输入框，再发送给 AI 大模型分析。",
        "builtin.captureMattingGeneral" => "框选区域离线去除背景，保留通用物体主体（透明通道）。",
        "builtin.captureMattingPortrait" => "框选区域离线去除背景，精细提取人像主体（透明通道）。",
        "builtin.translateDefault" => "框选屏幕区域进行翻译，输出方式跟随设置中的翻译模式。",
        "builtin.translateImage" => "框选区域翻译并直接覆盖原图文字，以贴图形式呈现。",
        "builtin.translateText" => "框选区域识别并翻译文字，在双栏对照窗口中查看与复制。",
        "builtin.translateSilent" => "框选区域静默翻译，译文直接复制到剪贴板，不弹窗打扰。",
        "builtin.translateClipboard" => "翻译当前剪贴板中的文字并打开双栏结果窗口。",
        "builtin.translateSticker" => "翻译当前获得焦点的贴图内容，支持在原图与译文间切换。",
        "builtin.record" => "框选录制区域，先展示录屏参数条确认画质与声音后再开始录制。",
        "builtin.recordQuick" => "框选录制区域后跳过确认条，按当前设置立即开始录屏。",
        _ => "内置快捷动作，支持绑定键盘快捷键、全局鼠标或外部命令行调用。"
    };
}
