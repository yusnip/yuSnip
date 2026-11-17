using System;
using System.Collections.Generic;
using System.Linq;
using ScreenCaptureTool.Core.Scripting;

namespace ScreenCaptureTool.Core.Actions;

/// <summary>中文步骤目录；界面选项、指令生成和已有动作回读共用这一份定义。</summary>
public sealed record ActionStepOption(string Label, string Script, string Description);
public sealed record ActionStepOperation(string Name, string OptionLabel, ActionStepOption[] Options);
public sealed record ActionStepChoice(ActionStepOperation Operation, ActionStepOption Option, string? InputName = null, string? ResultName = null);
public sealed record ActionResultReference(string Name, ActionContentKind Kind, int Position)
{
    public string Label => $"{Position + 1}. {Name} · {(Kind == ActionContentKind.Image ? "图片" : "文字")}";
}

public static class ActionStepCatalog
{
    private static ActionStepOption Shot(string label, string arg, string description) =>
        new(label, string.IsNullOrEmpty(arg) ? "yuSnip.screenShot();" : $"yuSnip.screenShot('{arg}');", description);
    private static ActionStepOption Translate(string label, string arg, string description) =>
        new(label, $"yuSnip.translate('{arg}');", description);

    private static readonly ActionStepOperation[] LegacyOperations =
    [
        new("截图", "完成后", [
            Shot("复制到剪贴板", "Copy", "框选屏幕区域，完成后可直接粘贴。"),
            Shot("贴在屏幕上", "Pin", "框选屏幕区域，将截图固定在屏幕上。"),
            Shot("保存到文件", "Save", "截图保存到设置中的截图目录。"),
            Shot("打开标注工具", "", "框选后可以画线、写字，再自行选择保存或复制。")]),
        new("识别文字", "显示结果", [
            Shot("打开文字窗口", "Ocr", "识别选区中的文字，打开窗口查看和编辑。"),
            Shot("显示简短提示", "OcrToast", "识别选区中的文字，以简短提示展示结果。")]),
        new("AI 识图", "提问方式", [
            Shot("跟随当前设置", "Vision", "框选图片，使用设置中的识图方式。"),
            Shot("每次输入问题", "VisionPrompt", "框选图片后，输入你想问的问题。")]),
        new("抠图", "识别对象", [
            Shot("跟随当前设置", "Matting", "框选图片，结果去向使用设置中的抠图选项。"),
            Shot("通用主体", "MattingGeneral", "去掉图片背景，保留主要物体。"),
            Shot("人像", "MattingPortrait", "去掉人像背景，保留人物。")]),
        new("翻译", "翻译内容", [
            Translate("截图，跟随当前设置", "Screen", "框选屏幕内容，翻译方式跟随当前设置。"),
            Translate("截图，覆盖原图文字", "ImageToImage", "把选区中的文字翻译后贴回图片。"),
            Translate("截图，打开文本结果", "ImageToText", "识别并翻译选区中的文字，展示文本结果。"),
            Translate("截图，复制翻译结果", "Silent", "翻译选区内容，把译文放入剪贴板。"),
            Translate("剪贴板中的文字", "Clipboard", "翻译刚复制的文字，打开结果窗口。"),
            Translate("当前选中的贴图", "Sticker", "翻译当前获得焦点的贴图。")]),
        new("录屏", "开始方式", [
            new("先确认录制选项", "yuSnip.screenRecord();", "框选区域，确认录制选项后开始。录制结束后本步骤结束。"),
            new("框选后立即录制", "yuSnip.screenRecord('Quick');", "框选区域，按当前录屏设置立即开始录制。")]),
        new("贴图", "操作", [
            new("将剪贴板贴在屏幕上", "yuSnip.pinFromClipboard();", "把剪贴板里的图片或文字固定在屏幕上。单独使用时沿用快速贴图设置。"),
            new("恢复上次关闭的贴图", "yuSnip.restoreLastSticker();", "找回最近关闭的一张贴图。"),
            new("隐藏或显示所有贴图", "yuSnip.toggleAllStickers();", "统一切换现有贴图的显示状态。"),
            new("关闭所有贴图", "yuSnip.destroyAllPin();", "关闭当前所有贴图。")])
    ];

    public static readonly ActionStepOperation[] Operations =
    [
        new("获取内容", "来源", [
            new("截图选区", "yuSnip.captureImage();", "获取一张新截图，保存为独立结果，不自动复制或贴图。"),
            new("剪贴板图片", "yuSnip.readClipboard('Image');", "读取此刻的剪贴板图片，保存为独立结果。"),
            new("剪贴板文字", "yuSnip.readClipboard('Text');", "读取此刻的剪贴板文字，保存为独立结果。")]),
        new("识别文字", "", [new("提取图片中的文字", "yuSnip.recognizeResult();", "识别所选图片，原图与识别文字分别保留。")]),
        new("翻译", "结果", [
            new("译文文字", "yuSnip.translateResult('Text');", "翻译所选图片或文字；图片先识别文字。输入与译文分别保留。"),
            new("译文图片", "yuSnip.translateResult('Image');", "翻译所选图片，保存带译文的新图片；需要图片翻译服务。")]),
        new("抠图", "识别对象", [
            new("跟随设置", "yuSnip.mattingResult('Default');", "使用当前设置的模型抠图，原图与透明图片分别保留。"),
            new("通用主体", "yuSnip.mattingResult('General');", "去掉所选图片的背景，保存为新的透明图片。"),
            new("人像", "yuSnip.mattingResult('Portrait');", "保留所选图片中的人物，保存为新的透明图片。")]),
        new("输出结果", "去向", [
            new("复制到剪贴板", "yuSnip.copyResult();", "复制所选结果。多次复制按顺序覆盖，剪贴板最终保留最后一次。"),
            new("贴在屏幕上", "yuSnip.pinResult();", "将所选图片或文字贴在屏幕上，原结果仍可继续引用。"),
            new("保存到文件", "yuSnip.saveResult();", "保存所选结果到截图目录：图片为 PNG，文字为 TXT。"),
            new("打开文字窗口", "yuSnip.showResult();", "弹窗显示所选文字或译文，不再次识别或翻译；后续模块继续执行。")]),
        new("AI 识图", "提问方式", [
            new("跟随当前设置", "yuSnip.visionResult('Default');", "把所选图片送到 AI 窗口；后续模块可继续，AI 回复暂不能引用。"),
            new("每次输入问题", "yuSnip.visionResult('Prompt');", "对所选图片提问，不重新框选；AI 回复暂不能作为模块输入。")]),
        new("贴图管理", "操作", [
            LegacyOperations[6].Options[1] with { Label = "恢复上次" },
            LegacyOperations[6].Options[2] with { Label = "显示／隐藏" },
            LegacyOperations[6].Options[3] with { Label = "关闭全部" }])
    ];

    public const string DefaultScript = "yuSnip.captureImage('原图');\nyuSnip.copyResult('原图');";
    public static ActionStepChoice Default => new(Operations[0], Operations[0].Options[0], ResultName: "原图");
    public static string BuildScript(IEnumerable<ActionStepChoice> steps) => string.Join(Environment.NewLine, steps.Select(BuildCall));

    public static ScriptCommand Command(ActionStepOption option)
    {
        ScriptExecutor.TryParse(option.Script, out var calls, out _, validateOrder: false);
        return calls[0];
    }

    private static string BuildCall(ActionStepChoice step)
    {
        var call = Command(step.Option);
        var args = call.Arguments.ToList();
        if (call.NeedsInput) args.Add(step.InputName ?? "");
        if (call.ProducesResult && step.ResultName != null) args.Add(step.ResultName);
        // 名称经过统一校验；不用转义来偷偷更改用户填写的引用。
        string method = step.Option.Script[..step.Option.Script.IndexOf('(')];
        return method + "(" + string.Join(", ", args.Select(arg => "'" + arg + "'")) + ");";
    }

    public static List<ActionResultReference> ResultsBefore(IReadOnlyList<ActionStepChoice> steps, int position)
    {
        var results = new List<ActionResultReference>();
        for (int i = 0; i < position; i++)
        {
            var step = steps[i];
            var call = Command(step.Option);
            if (!call.ProducesResult || step.ResultName == null) continue;
            var input = results.LastOrDefault(r => r.Name == step.InputName);
            results.Add(new(step.ResultName, ScriptExecutor.ContentAfter(call, input?.Kind ?? ActionContentKind.None), i));
        }
        return results;
    }

    public static List<ActionResultReference> InputsFor(ActionStepOption option, IEnumerable<ActionResultReference> results)
    {
        var call = Command(option);
        return call.NeedsInput ? results.Where(r => ScriptExecutor.AcceptsInput(call, r.Kind)).ToList() : [];
    }

    public static bool OptionAvailable(ActionStepOption option, IEnumerable<ActionResultReference> results) =>
        !Command(option).NeedsInput || InputsFor(option, results).Count > 0;

    public static ActionStepChoice Create(ActionStepOperation operation, ActionStepOption option,
        IReadOnlyList<ActionStepChoice> steps, int position, string? preferredInput = null)
    {
        var call = Command(option);
        var inputs = InputsFor(option, ResultsBefore(steps, position));
        string? input = inputs.LastOrDefault(r => r.Name == preferredInput)?.Name ?? inputs.LastOrDefault()?.Name;
        string? result = call.ProducesResult ? UniqueResultName(ResultLabel(call), steps) : null;
        return new(operation, option, input, result);
    }

    private static string ResultLabel(ScriptCommand call) => call.Method switch
    {
        "captureimage" => "原图", "readclipboard" => call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase) ? "剪贴板图片" : "剪贴板文字",
        "recognizeresult" => "识别文字", "translateresult" => call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase) ? "译文图片" : "译文",
        _ => "抠图结果"
    };

    private static string UniqueResultName(string label, IReadOnlyList<ActionStepChoice> steps, ISet<string>? reserved = null)
    {
        string name = label;
        for (int suffix = 2; steps.Any(s => s.ResultName == name) || reserved?.Contains(name) == true; suffix++) name = label + suffix;
        return name;
    }

    /// <summary>能完整回读的旧自定义组合动作升级为共享内容流程；预设与无法转换的高级指令保持原样。</summary>
    public static string ExecutionScript(ActionDefinition action) => !action.IsBuiltin
        && ScriptExecutor.TryParse(action.Script, out var calls, out _, validateOrder: false) && calls.Count > 1
        && TryRead(action.Script, out var steps) ? BuildScript(steps) : action.Script;

    public static bool TryRead(string script, out List<ActionStepChoice> steps)
    {
        steps = [];
        if (!ScriptExecutor.TryParse(script, out var calls, out _, validateOrder: false)) return false;
        if (!calls.Exists(c => c.UsesWorkflow) && !ConvertLegacy(calls, out calls)) return false;
        // 自动命名避开整段中所有显式名称，混用新旧写法时也不产生重名。
        var reserved = calls.Where(c => c.ResultName != null).Select(c => c.ResultName!).ToHashSet(StringComparer.Ordinal);
        string? current = null;
        foreach (var call in calls)
        {
            ActionStepChoice? match = null;
            foreach (var operation in Operations)
            foreach (var option in operation.Options)
                if (ScriptExecutor.TryParse(option.Script, out var candidate, out _, validateOrder: false)
                    && candidate[0].Method == call.Method
                    && call.Arguments.Take(candidate[0].Arguments.Length).SequenceEqual(candidate[0].Arguments, StringComparer.OrdinalIgnoreCase))
                    match = new(operation, option);
            if (match == null) { steps.Clear(); return false; }
            match = match with { InputName = call.NeedsInput ? call.InputName ?? current : null,
                ResultName = call.ProducesResult ? call.ResultName ?? UniqueResultName(ResultLabel(call), steps, reserved) : null };
            steps.Add(match);
            if (call.ProducesResult) current = match.ResultName;
        }
        return steps.Count > 0;
    }

    private static bool ConvertLegacy(List<ScriptCommand> original, out List<ScriptCommand> converted)
    {
        var result = new List<ScriptCommand>();
        converted = result;
        bool hasContent = false;
        void Add(string method, params string[] args) => result.Add(new(method, args));
        void Image()
        {
            if (!hasContent) { Add("captureimage"); hasContent = true; }
        }
        foreach (var call in original)
        {
            string arg = call.Arguments.Length == 0 ? "" : call.Arguments[0].ToLowerInvariant();
            switch (call.Method)
            {
                case "screenshot" when arg is "copy" or "pin" or "save":
                    if (hasContent) return false;
                    Image(); Add(arg == "copy" ? "copyresult" : arg == "pin" ? "pinresult" : "saveresult"); break;
                case "screenshot" when arg is "ocr" or "ocrtoast":
                    Image(); Add("recognizeresult"); Add(arg == "ocr" ? "showresult" : "copyresult"); break;
                case "screenshot" when arg is "vision" or "visionprompt":
                    Image(); Add("visionresult", arg == "vision" ? "Default" : "Prompt"); break;
                case "translate" when arg == "screen":
                case "screenshot" when arg == "translate":
                    Image();
                    bool imageTranslation = AppSettings.Current.TranslateMode.Equals("ImageToImage", StringComparison.OrdinalIgnoreCase);
                    Add("translateresult", imageTranslation ? "Image" : "Text");
                    Add(imageTranslation ? "pinresult" : "showresult"); break;
                case "screenshot" when arg is "matting" or "mattinggeneral" or "mattingportrait":
                case "matting" when arg is "" or "general" or "portrait" or "mattinggeneral" or "mattingportrait":
                    Image();
                    Add("mattingresult", arg is "portrait" or "mattingportrait" ? "Portrait" : arg is "general" or "mattinggeneral" ? "General" : "Default");
                    string destination = AppSettings.Current.MattingResultDestination;
                    if (!destination.Equals("Sticker", StringComparison.OrdinalIgnoreCase)) Add("copyresult");
                    if (!destination.Equals("Clipboard", StringComparison.OrdinalIgnoreCase)) Add("pinresult");
                    break;
                case "translate" when arg == "clipboard":
                    if (!hasContent) { Add("readclipboard", "Text"); hasContent = true; }
                    Add("translateresult", "Text"); Add("showresult"); break;
                case "translate" when arg is "imagetoimage" or "imagetotext" or "silent":
                    Image(); Add("translateresult", arg == "imagetoimage" ? "Image" : "Text");
                    Add(arg == "imagetoimage" ? "pinresult" : arg == "silent" ? "copyresult" : "showresult"); break;
                case "pinfromclipboard":
                    if (!hasContent) return false;
                    Add("pinresult"); break;
                case "toggleallstickers": case "destroyallpin": case "restorelaststicker": case "screenrecord":
                    result.Add(call); break;
                default: return false;
            }
        }
        // 输出按用户原顺序执行，不把早期结果的输出挪到处理之后。
        converted = result;
        return !converted.Exists(call => call.UsesWorkflow) || ScriptExecutor.ValidateWorkflow(converted, out _);
    }

    public static string Describe(ActionStepChoice step) => step.Operation.Name + " · " + step.Option.Label
        + (step.InputName != null ? "（" + step.InputName + "）" : "") + (step.ResultName != null ? " ⇒ " + step.ResultName : "");
    public static string Summary(string script)
    {
        if (TryRead(script, out var steps)) return string.Join(" → ", steps.Select(Describe));
        if (!ScriptExecutor.TryParse(script, out var calls, out _, validateOrder: false)) return "高级指令";
        var names = new List<string>();
        foreach (var call in calls)
            foreach (var operation in LegacyOperations)
            foreach (var option in operation.Options)
                if (ScriptExecutor.TryParse(option.Script, out var candidate, out _, validateOrder: false)
                    && candidate[0].Method == call.Method && candidate[0].Arguments.SequenceEqual(call.Arguments, StringComparer.OrdinalIgnoreCase))
                    names.Add(operation.Name + " · " + option.Label);
        return names.Count == calls.Count ? string.Join(" → ", names) : "高级指令";
    }
}
