using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Scripting
{
    public interface IScriptEnv
    {
        void ScreenShot(string? action);
        void ScreenShotWithOptions(ActionCaptureOptions options) => ScreenShot(null);
        void DirectScreenShot(int x, int y, int w, int h, string? action);
        void PinFromClipboard();
        void DestroyAllPin();
        void ToggleAllStickers();
        void RestoreLastSticker();
        void Translate(string? action);
        void Matting(string? action);
        void ScreenRecord(string? action = null);
        void LongScroll(string? axis = null);
    }

    /// <summary>界面执行环境在操作完成或取消后结束当前步骤。</summary>
    public interface IAsyncScriptEnv : IScriptEnv
    {
        Task<bool> CompleteStepAsync();
    }

    /// <summary>动作内的数据流操作；结果保存在本次执行环境中。</summary>
    public interface IActionWorkflowEnv : IAsyncScriptEnv
    {
        void RunWorkflowStep(string method, string[] arguments);
    }

    public enum ActionContentKind { None, Image, Text }

    public sealed record ScriptCommand(string Method, string[] Arguments)
    {
        public bool IsWait => Method == "wait";
        public int WaitMilliseconds => IsWait && Arguments.Length == 1
            && ScriptExecutor.TryReadWaitMilliseconds(Arguments[0], out int duration) ? duration : 0;
        // 每个模块独立产出命名结果，旧无名称写法仍默认读取最近一次处理结果。
        public bool UsesWorkflow => Method is "captureimage" or "capturescreen" or "captureregion" or "readclipboard" or "readsticker"
            or "recognizeresult" or "translateresult" or "mattingresult"
            or "copyresult" or "pinresult" or "saveresult" or "showresult" or "visionresult";
        public bool IsOutput => Method is "copyresult" or "pinresult" or "saveresult" or "showresult" or "visionresult";
        public bool ProducesResult => Method is "captureimage" or "capturescreen" or "captureregion" or "readclipboard" or "readsticker"
            or "recognizeresult" or "translateresult" or "mattingresult";
        public bool NeedsInput => UsesWorkflow && Method is not ("captureimage" or "capturescreen" or "captureregion" or "readclipboard" or "readsticker");
        public string? InputName => !NeedsInput ? null : Method switch
        {
            "translateresult" or "mattingresult" or "visionresult" => Arguments.Length >= 2 ? Arguments[1] : null,
            _ => Arguments.Length >= 1 ? Arguments[0] : null
        };
        public string? ResultName => !ProducesResult ? null : Method switch
        {
            "captureimage" or "capturescreen" or "readsticker" => Arguments.Length >= 1 ? Arguments[0] : null,
            "captureregion" => Arguments.Length >= 5 ? Arguments[4] : null,
            "readclipboard" => Arguments.Length >= 2 ? Arguments[1] : null,
            "recognizeresult" => Arguments.Length >= 2 ? Arguments[1] : null,
            _ => Arguments.Length >= 3 ? Arguments[2] : null
        };
        public ActionCaptureOptions CaptureOptions => Method switch
        {
            "captureimage" when ActionCaptureOptions.TryRead(Arguments, out var opt, 1) => opt,
            "captureregion" when ActionCaptureOptions.TryRead(Arguments, out var opt, 5) => opt,
            "screenshot" when Arguments.Length > 0 && Arguments[0].Contains('=') && ActionCaptureOptions.TryRead(Arguments, out var opt, 0) => opt,
            _ => default
        };
        public bool IsInteractiveScreenShot => Method == "screenshot"
            && (Arguments.Length == 0 || string.IsNullOrEmpty(Arguments[0]) || Arguments[0].Contains('='));
        public string ResultKey(int position) => ResultName ?? $"#step{position + 1}";
        public bool CanContinue => IsWait || UsesWorkflow
            || Method is "pinfromclipboard" or "destroyallpin" or "toggleallstickers" or "restorelaststicker" or "screenrecord" or "longscroll"
            || IsInteractiveScreenShot
            || Method == "screenshot" && Arguments.Length == 1 && Arguments[0].ToLowerInvariant() is "copy" or "pin" or "save"
            || Method == "directscreenshot" && (Arguments.Length == 4 || Arguments[4].ToLowerInvariant() is "" or "copy" or "pin" or "save");
    }

    public static class ScriptExecutor
    {
        public const int MaximumWaitMilliseconds = 60_000;

        internal static bool TryReadWaitMilliseconds(string value, out int duration) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out duration) && duration <= MaximumWaitMilliseconds;

        /// <summary>先检查整段指令，再按完成顺序执行；取消或失败不继续下一步。</summary>
        public static async Task<bool> ExecuteAsync(string script, IAsyncScriptEnv env)
        {
            if (!TryParse(script, out var calls, out string error)) throw new ArgumentException(error);
            for (int i = 0; i < calls.Count; i++)
            {
                try
                {
                    if (calls[i].UsesWorkflow || calls[i].IsWait)
                    {
                        if (env is not IActionWorkflowEnv workflow) throw new InvalidOperationException("当前环境不支持组合动作。");
                        workflow.RunWorkflowStep(calls[i].Method, calls[i].Arguments);
                    }
                    else ExecuteMethod(calls[i].Method, calls[i].Arguments, env);
                    if (!await env.CompleteStepAsync()) return false;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"第 {i + 1} 步未完成：{ex.Message}", ex);
                }
            }
            return true;
        }

        public static bool TryParse(string script, out List<ScriptCommand> calls, out string error, bool validateOrder = true)
        {
            calls = [];
            error = "";
            int lineNumber = 0;
            foreach (string raw in script.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("//")) continue;
                if (!TryGetSingleCall(raw, out string method, out var args))
                {
                    error = $"第 {lineNumber} 行格式不正确，请每行写一个 yuSnip 操作。";
                    return false;
                }
                bool quick = method == "quickrecord";
                method = method switch
                {
                    "screenshotandedit" => "screenshot",
                    "closeallstickers" => "destroyallpin",
                    "toggleallpin" => "toggleallstickers",
                    "quickrecord" => "screenrecord",
                    _ => method
                };
                if (quick && args.Length == 0) args = ["Quick"];
                // 仅归一化传统动作参数，命名引用保持原字符串与大小写。
                if (method is not ("wait" or "captureimage" or "capturescreen" or "captureregion" or "readclipboard" or "readsticker"
                    or "recognizeresult" or "translateresult" or "mattingresult"
                    or "copyresult" or "pinresult" or "saveresult" or "showresult" or "visionresult"))
                    for (int i = 0; i < args.Length; i++) args[i] = ParseActionArg(args[i]);
                string arg = args.Length == 0 ? "" : args[0].ToLowerInvariant();
                if (method == "captureimage" && !ActionCaptureOptions.TryRead(args, out _, 1))
                {
                    error = $"第 {lineNumber} 行的截图参数无效（支持 toolbar/radius/stroke/shadow/detect）。";
                    return false;
                }
                if (method == "wait" && (args.Length != 1 || !TryReadWaitMilliseconds(args[0], out _)))
                {
                    error = $"第 {lineNumber} 行的等待时间须为 0～{MaximumWaitMilliseconds} 毫秒的整数。";
                    return false;
                }
                bool valid = method switch
                {
                    "captureimage" or "wait" => true,
                    "capturescreen" => ValidateCaptureScreen(args),
                    "readsticker" => args.Length <= 1,
                    "captureregion" => args.Length >= 4
                        && int.TryParse(args[0], out _) && int.TryParse(args[1], out _)
                        && int.TryParse(args[2], out int rw) && rw > 0
                        && int.TryParse(args[3], out int rh) && rh > 0
                        && (args.Length <= 5 || ActionCaptureOptions.TryRead(args, out _, 5)),
                    "copyresult" or "showresult" => args.Length <= 1,
                    "pinresult" => ValidatePinResult(args),
                    "saveresult" => ValidateSaveResult(args),
                    "recognizeresult" => ValidateRecognizeResult(args),
                    "readclipboard" => args.Length is 1 or 2 && arg is "image" or "text",
                    "translateresult" => ValidateTranslateResult(args, arg),
                    "mattingresult" => args.Length is >= 1 and <= 3 && arg is "default" or "general" or "portrait",
                    "visionresult" => ValidateVisionResult(args, arg),
                    "screenshot" => (args.Length > 0 && args[0].Contains('='))
                        ? ActionCaptureOptions.TryRead(args, out _, 0)
                        : args.Length <= 1 && arg is "" or "copy" or "pin" or "sticker" or "save" or "ocr" or "ocrtoast"
                            or "vision" or "visionprompt" or "matting" or "mattinggeneral" or "mattingportrait" or "translate",
                    "translate" => args.Length <= 1 && arg is "" or "screen" or "capture" or "imagetoimage" or "image" or "imagetotext"
                        or "silent" or "clipboardsilent" or "clipboard" or "text" or "sticker" or "pin",
                    "matting" => args.Length <= 1 && arg is "" or "general" or "portrait" or "mattinggeneral" or "mattingportrait" or "sticker" or "pin",
                    "screenrecord" => args.Length <= 1 && arg is "" or "quick" or "fast" or "recordquick" or "quickrecord",
                    "longscroll" => args.Length == 0 || (args.Length == 1 && arg is "vertical" or "horizontal"),
                    "pinfromclipboard" or "destroyallpin" or "toggleallstickers" or "restorelaststicker" => args.Length == 0,
                    "directscreenshot" => args.Length is 4 or 5 && int.TryParse(args[0], out _) && int.TryParse(args[1], out _)
                        && int.TryParse(args[2], out int width) && width > 0 && int.TryParse(args[3], out int height) && height > 0
                        && (args.Length == 4 || args[4].ToLowerInvariant() is "" or "copy" or "pin" or "sticker" or "save" or "matting" or "mattinggeneral" or "mattingportrait"),
                    _ => false
                };
                if (!valid)
                {
                    error = $"第 {lineNumber} 行的操作或选项无法识别，请使用步骤编辑选择操作。";
                    return false;
                }
                // 同义参数归一化，便于旧指令回读成可视化步骤。
                if (method == "screenshot" && arg == "sticker") args[0] = "Pin";
                if (method == "translate")
                {
                    if (args.Length == 0) args = ["Screen"];
                    else args[0] = arg switch { "" or "capture" => "Screen", "image" => "ImageToImage", "clipboardsilent" => "Silent", "text" => "Clipboard", "pin" => "Sticker", _ => args[0] };
                }
                var command = new ScriptCommand(method, args);
                if (command.UsesWorkflow && ((command.InputName is { Length: > 0 } && !IsResultName(command.InputName))
                    || (command.ResultName != null && !IsResultName(command.ResultName))))
                {
                    error = $"第 {lineNumber} 行的结果名称不能为空，且不能包含引号、逗号、换行或 #。";
                    return false;
                }
                calls.Add(command);
            }
            if (calls.Count == 0) { error = "请至少添加一个操作步骤。"; return false; }
            if (validateOrder && calls.Exists(call => call.UsesWorkflow) && !ValidateWorkflow(calls, out error)) return false;
            if (validateOrder)
                for (int i = 0; i < calls.Count - 1; i++)
                    if (!calls[i].CanContinue)
                    {
                        error = $"第 {i + 1} 步会打开结果窗口或启动后台处理，请将它放在最后一步。";
                        return false;
                    }
            return true;
        }

        public static ActionContentKind ContentAfter(ScriptCommand call, ActionContentKind current) => call.Method switch
        {
            "captureimage" or "capturescreen" or "captureregion" or "readsticker" => ActionContentKind.Image,
            "readclipboard" => call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase) ? ActionContentKind.Image : ActionContentKind.Text,
            "recognizeresult" => ActionContentKind.Text,
            "translateresult" => call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase) ? ActionContentKind.Image : ActionContentKind.Text,
            _ => current
        };

        public static bool IsResultName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64
            && name == name.Trim() && name.IndexOfAny(['\'', '"', ',', '\r', '\n', '#']) < 0;

        public static bool AcceptsInput(ScriptCommand call, ActionContentKind kind)
        {
            if (!call.NeedsInput) return true;
            if (kind == ActionContentKind.None) return false;
            if (call.Method is "recognizeresult" or "mattingresult" or "visionresult"
                || call.Method == "translateresult" && call.Arguments[0].Equals("Image", StringComparison.OrdinalIgnoreCase))
                return kind == ActionContentKind.Image;
            return call.Method != "showresult" || kind == ActionContentKind.Text;
        }

        internal static bool ValidateWorkflow(List<ScriptCommand> calls, out string error)
        {
            error = "";
            var results = new Dictionary<string, ActionContentKind>(StringComparer.Ordinal);
            string? current = null;
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                string? problem = null;
                if (!call.UsesWorkflow)
                {
                    bool allowedStandalone = call.IsWait
                        || call.Method is "toggleallstickers" or "destroyallpin" or "restorelaststicker" or "screenrecord" or "longscroll"
                        || call.IsInteractiveScreenShot;
                    if (!allowedStandalone)
                        problem = "此操作会单独启动一项功能，不能混入内容流程。请用流程编排选择对应模块。";
                }
                else
                {
                    var inputKind = ActionContentKind.None;
                    if (call.NeedsInput)
                    {
                        string? input = call.InputName ?? current;
                        if (string.IsNullOrEmpty(input))
                            problem = "请添加或上移一个能提供输入的模块，再选择它的结果。";
                        else if (!results.TryGetValue(input, out inputKind))
                            problem = $"找不到结果「{input}」，请引用此前模块已产生的结果。";
                        else if (!AcceptsInput(call, inputKind))
                            problem = $"输入「{input}」是{(inputKind == ActionContentKind.Image ? "图片" : "文字")}，与此模块所需类型不匹配。";
                    }
                    if (problem == null && call.ProducesResult)
                    {
                        string key = call.ResultKey(i);
                        if (results.ContainsKey(key)) problem = $"结果名称「{key}」已使用，请为每个结果起不同名称。";
                        else { results.Add(key, ContentAfter(call, inputKind)); current = key; }
                    }
                }
                if (problem != null) { error = $"第 {i + 1} 步：{problem}"; return false; }
            }
            return true;
        }

        public static void Execute(string script, IScriptEnv env)
        {
            if (string.IsNullOrWhiteSpace(script)) return;

            string[] lines = script.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;

                if (line.EndsWith(";"))
                {
                    line = line.Substring(0, line.Length - 1).Trim();
                }

                int prefixLen = 0;
                if (line.StartsWith("yuSnip.", StringComparison.OrdinalIgnoreCase))
                {
                    prefixLen = 7;
                }

                if (prefixLen > 0)
                {
                    string call = line.Substring(prefixLen).Trim();
                    int openParen = call.IndexOf('(');
                    int closeParen = call.LastIndexOf(')');
                    if (openParen > 0 && closeParen > openParen)
                    {
                        string methodName = call.Substring(0, openParen).Trim();
                        string argsStr = call.Substring(openParen + 1, closeParen - openParen - 1).Trim();

                        string[] args = ParseArgs(argsStr);
                        ExecuteMethod(methodName, args, env);
                    }
                }
            }
        }

        /// <summary>鼠标能力判定复用脚本解析口径；只接受单条有效调用。</summary>
        public static bool TryGetSingleCall(string script, out string method, out string[] args)
        {
            method = "";
            args = Array.Empty<string>();
            string? call = null;
            foreach (string raw in script.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                if (call != null) return false;
                call = line.EndsWith(';') ? line[..^1].Trim() : line;
            }
            if (call == null) return false;
            int prefix = call.StartsWith("yuSnip.", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
            if (prefix == 0 || !call.EndsWith(')')) return false;
            int open = call.IndexOf('(', prefix);
            if (open <= prefix) return false;
            method = call[prefix..open].Trim().ToLowerInvariant();
            char quote = '\0';
            for (int i = open + 1; i < call.Length - 1; i++)
            {
                char c = call[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '\'' or '"') quote = c;
                else if (c is '(' or ')' or ';') return false;
            }
            if (quote != '\0') return false;
            args = ParseArgs(call[(open + 1)..^1]);
            return true;
        }

        private static string[] ParseArgs(string argsStr)
        {
            if (string.IsNullOrWhiteSpace(argsStr)) return Array.Empty<string>();
            var list = new List<string>();
            char quote = '\0';
            var sb = new System.Text.StringBuilder();
            void Add()
            {
                string arg = sb.ToString().Trim();
                if (arg.Length >= 2 && arg[0] is '\'' or '"' && arg[^1] == arg[0]) arg = arg[1..^1];
                list.Add(arg);
                sb.Clear();
            }
            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];
                if (c == ',' && quote == '\0') Add();
                else
                {
                    if (quote == '\0' && c is '\'' or '"') quote = c;
                    else if (c == quote) quote = '\0';
                    sb.Append(c);
                }
            }
            Add();
            return list.ToArray();
        }

        private static void ExecuteMethod(string name, string[] args, IScriptEnv env)
        {
            if (string.Equals(name, "screenShot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "screenShotAndEdit", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length > 0 && args[0].Contains('=') && ActionCaptureOptions.TryRead(args, out var capOpt, 0))
                {
                    env.ScreenShotWithOptions(capOpt);
                }
                else
                {
                    string? action = args.Length > 0 ? ParseActionArg(args[0]) : null;
                    env.ScreenShot(action);
                }
            }
            else if (string.Equals(name, "directScreenShot", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length >= 4)
                {
                    int.TryParse(args[0], out int x);
                    int.TryParse(args[1], out int y);
                    int.TryParse(args[2], out int w);
                    int.TryParse(args[3], out int h);
                    string? action = args.Length > 4 ? ParseActionArg(args[4]) : null;
                    env.DirectScreenShot(x, y, w, h, action);
                }
            }
            else if (string.Equals(name, "pinFromClipboard", StringComparison.OrdinalIgnoreCase))
            {
                env.PinFromClipboard();
            }
            else if (string.Equals(name, "destroyAllPin", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "closeAllStickers", StringComparison.OrdinalIgnoreCase))
            {
                env.DestroyAllPin();
            }
            else if (string.Equals(name, "toggleAllStickers", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "toggleAllPin", StringComparison.OrdinalIgnoreCase))
            {
                env.ToggleAllStickers();
            }
            else if (string.Equals(name, "restoreLastSticker", StringComparison.OrdinalIgnoreCase))
            {
                env.RestoreLastSticker();
            }
            else if (string.Equals(name, "translate", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args.Length > 0 ? ParseActionArg(args[0]) : null;
                env.Translate(action);
            }
            else if (string.Equals(name, "matting", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args.Length > 0 ? ParseActionArg(args[0]) : null;
                env.Matting(action);
            }
            else if (string.Equals(name, "screenRecord", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args.Length > 0 ? ParseActionArg(args[0]) : null;
                env.ScreenRecord(action);
            }
            else if (string.Equals(name, "quickRecord", StringComparison.OrdinalIgnoreCase))
            {
                env.ScreenRecord("Quick");
            }
            else if (string.Equals(name, "longScroll", StringComparison.OrdinalIgnoreCase))
            {
                string? axis = args.Length > 0 ? ParseActionArg(args[0]) : null;
                env.LongScroll(axis);
            }
        }

        private static bool ValidateCaptureScreen(string[] args)
        {
            if (args.Length <= 1) return true;
            if (!ActionCaptureOptions.TryParseKeyValues(args, 1, out var kv)) return false;
            foreach (var (k, v) in kv)
            {
                if (k.Equals("scope", StringComparison.OrdinalIgnoreCase))
                {
                    if (v.ToLowerInvariant() is not ("all" or "current" or "primary")) return false;
                }
                else return false;
            }
            return true;
        }

        private static bool ValidatePinResult(string[] args)
        {
            if (args.Length <= 1) return true;
            if (!ActionCaptureOptions.TryParseKeyValues(args, 1, out var kv)) return false;
            foreach (var (k, v) in kv)
            {
                string vl = v.ToLowerInvariant();
                switch (k.ToLowerInvariant())
                {
                    case "shadow":
                        if (vl is not ("default" or "on" or "off")) return false;
                        break;
                    case "lock":
                    case "pass":
                        if (vl is not ("on" or "off" or "true" or "false")) return false;
                        break;
                    case "opacity":
                        if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int op) || op < 20 || op > 100) return false;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static bool ValidateSaveResult(string[] args)
        {
            if (args.Length <= 1) return true;
            if (!ActionCaptureOptions.TryParseKeyValues(args, 1, out var kv)) return false;
            foreach (var (k, v) in kv)
            {
                string vl = v.ToLowerInvariant();
                switch (k.ToLowerInvariant())
                {
                    case "mode":
                        if (vl is not ("quick" or "dialog")) return false;
                        break;
                    case "format":
                        if (vl is not ("png" or "jpg" or "jpeg")) return false;
                        break;
                    case "dir":
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static bool ValidateRecognizeResult(string[] args)
        {
            if (args.Length <= 2) return true;
            if (!ActionCaptureOptions.TryParseKeyValues(args, 2, out var kv)) return false;
            foreach (var (k, v) in kv)
            {
                if (k.Equals("cleanup", StringComparison.OrdinalIgnoreCase))
                {
                    if (v.ToLowerInvariant() is not ("none" or "merge" or "compact")) return false;
                }
                else return false;
            }
            return true;
        }

        private static bool ValidateTranslateResult(string[] args, string arg0)
        {
            if (args.Length < 1 || arg0 is not ("text" or "image")) return false;
            if (args.Length <= 3) return true;
            if (!ActionCaptureOptions.TryParseKeyValues(args, 3, out var kv)) return false;
            foreach (var (k, v) in kv)
            {
                if (k.Equals("to", StringComparison.OrdinalIgnoreCase))
                {
                    if (v.ToLowerInvariant() is not ("auto" or "zh" or "en" or "ja" or "ko")) return false;
                }
                else return false;
            }
            return true;
        }

        private static bool ValidateVisionResult(string[] args, string arg0)
        {
            if (args.Length < 1 || arg0 is not ("default" or "prompt" or "custom")) return false;
            if (args.Length <= 2) return true;
            if (!ActionCaptureOptions.TryParseKeyValues(args, 2, out var kv)) return false;
            foreach (var (k, _) in kv)
            {
                if (!k.Equals("prompt", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static string ParseActionArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return "";
            string a = arg.Trim();
            // Resolve ShotAction.Copy or ShotAction.Pin constants
            if (a.StartsWith("ShotAction.", StringComparison.OrdinalIgnoreCase))
            {
                return a.Substring(11);
            }
            if (a.StartsWith("RecordAction.", StringComparison.OrdinalIgnoreCase))
            {
                return a.Substring(13);
            }
            return a;
        }
    }
}
