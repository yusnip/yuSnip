using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Deps;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Core.Matting;
using ScreenCaptureTool.Core.Scripting;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Windows;
using Bitmap = System.Drawing.Bitmap;

namespace ScreenCaptureTool.Services;

/// <summary>一次动作的命名结果库。模块只读取输入并添加结果，运行结束统一释放图片。</summary>
internal sealed class ActionWorkflowContext : IDisposable
{
    private readonly App _app;
    private readonly MouseActionGesture? _gesture;
    private readonly Dictionary<string, WorkflowResult> _results = new(StringComparer.Ordinal);
    private string? _current;
    private int _sequence;
    private bool _gestureConsumed;

    private sealed record WorkflowResult(Bitmap? Image = null, string? Text = null, Rectangle? Region = null,
        TranslationResult? Translation = null)
    {
        public Bitmap RequireImage() => Image ?? throw new InvalidOperationException("所选输入不是图片。");
        public string RequireText() => !string.IsNullOrWhiteSpace(Text) ? Text : throw new InvalidOperationException("所选输入没有文字。");
    }

    public ActionWorkflowContext(App app, MouseActionGesture? gesture) { _app = app; _gesture = gesture; }

    public async Task<bool> RunAsync(string method, string[] args)
    {
        var call = new ScriptCommand(method, args);
        string output = call.ResultName ?? "#result" + ++_sequence;
        var input = call.NeedsInput ? GetResult(call.InputName ?? _current) : null;
        switch (method)
        {
            case "captureimage":
                var gesture = _gestureConsumed ? null : _gesture;
                _gestureConsumed = true;
                bool completed = await _app.RunCaptureAsync("Workflow", gesture, (image, region) =>
                    AddResult(output, new(Image: image, Region: region)));
                return completed && _results.ContainsKey(output);
            case "readclipboard":
                if (args[0].Equals("Image", StringComparison.OrdinalIgnoreCase))
                {
                    var image = ClipboardService.TryGetImageFromClipboardFile() ?? ClipboardService.TryGetImage();
                    if (image == null) throw new InvalidOperationException("剪贴板中没有图片。");
                    AddResult(output, new(Image: image));
                }
                else
                {
                    string? text = ClipboardService.TryGetText();
                    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("剪贴板中没有文字。");
                    AddResult(output, new(Text: text));
                }
                return true;
            case "recognizeresult":
                if (!await DependencyGate.EnsureAsync(DepIds.Ocr)) return false;
                using (var pending = new PendingNotice("正在识别本次图片..."))
                {
                    string text = await CaptureSession.GetSharedOcrService().ExtractTextAsync(input!.RequireImage());
                    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("这张图片没有识别到文字。");
                    AddResult(output, new(Text: text));
                    UsageStatistics.RecordOcr();
                }
                return true;
            case "translateresult": return await TranslateAsync(args[0], input!, output);
            case "mattingresult":
                if (!await DependencyGate.EnsureAsync(DepIds.Matting)) return false;
                using (var pending = new PendingNotice("正在处理本次图片背景..."))
                {
                    var model = args[0].ToLowerInvariant() switch
                    {
                        "general" => MattingModelType.General,
                        "portrait" => MattingModelType.Portrait,
                        _ => AppSettings.Current.GetMattingModelType()
                    };
                    var result = await MattingService.Instance.CutoutAsync(input!.RequireImage(), model);
                    if (!result.Success || result.ResultBitmap == null) throw new InvalidOperationException(result.ErrorMessage ?? "抠图失败。");
                    AddResult(output, new(Image: result.ResultBitmap, Region: input.Region));
                }
                return true;
            case "copyresult":
                if (input!.Image != null)
                {
                    if (!await ClipboardService.SetImageAutoAlphaAsync(input.Image)) return false;
                    CaptureHistoryStore.TryRecord(input.Image, CaptureHistorySource.Clipboard, null, AppSettings.Current);
                }
                else ClipboardService.SetText(input.RequireText());
                StickerToastWindow.ShowAtTop("已复制：" + (call.InputName ?? _current));
                return true;
            case "pinresult":
                if (input!.Image != null)
                {
                    var owned = (Bitmap)input.Image.Clone();
                    var sticker = new StickerWindow(owned);
                    sticker.SetHistoryId(CaptureHistoryStore.TryRecord(input.Image, CaptureHistorySource.Sticker, null, AppSettings.Current));
                    if (input.Region is { } region) sticker.SetStickerScreenPosition(new PixelPoint(region.X, region.Y));
                    sticker.Show();
                    sticker.Activate();
                    StickerFocusManager.SetFocus(sticker);
                }
                else _app.ShowStickerFromClipboardText(input.RequireText());
                return true;
            case "saveresult":
                var settings = AppSettings.Current;
                string path = CaptureSession.ResolveActionSavePath(settings, input!.Image != null ? ".png" : ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // 不覆盖同一秒内已有的截图或文字结果。
                string stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
                string extension = Path.GetExtension(path);
                for (int i = 1; File.Exists(path); i++) path = stem + "_" + i + extension;
                if (input.Image != null)
                {
                    ImageExportService.Save(input.Image, path);
                    CaptureHistoryStore.TryRecord(input.Image, CaptureHistorySource.Save, path, settings);
                }
                else await File.WriteAllTextAsync(path, input.RequireText(), new UTF8Encoding(false));
                StickerToastWindow.ShowAtTop("已保存动作结果：" + Path.GetFileName(path));
                return true;
            case "showresult":
                if (input!.Translation != null)
                {
                    var window = TranslationResultWindow.FromResult(input.Translation, AppSettings.Current);
                    window.Show(); window.Activate();
                }
                else OcrWindow.ShowResult(input.RequireText(), null);
                return true;
            case "visionresult":
                string? prompt = null;
                if (args[0].Equals("Prompt", StringComparison.OrdinalIgnoreCase) || !AppSettings.Current.AiUseDefaultPrompt)
                {
                    var promptInput = new AiPromptInputWindow();
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    promptInput.Closed += (_, _) => completion.TrySetResult(promptInput.IsConfirmed);
                    promptInput.Show(); promptInput.Activate();
                    if (!await completion.Task) return false;
                    prompt = promptInput.ResultPrompt;
                }
                var ai = new AiVisionResultWindow();
                ai.SetTargetImage(input!.RequireImage(), prompt);
                ai.Show(); ai.Activate();
                return true;
            default: throw new InvalidOperationException("无法识别这一步操作。");
        }
    }

    private async Task<bool> TranslateAsync(string format, WorkflowResult input, string outputName)
    {
        var settings = AppSettings.Current;
        var translator = TranslationService.FromSettings(settings);
        if (!translator.IsAvailable) throw new InvalidOperationException("请先在设置中配置翻译服务。");
        if (format.Equals("Image", StringComparison.OrdinalIgnoreCase))
        {
            using var pending = new PendingNotice("正在翻译本次图片...");
            using var stream = new MemoryStream();
            input.RequireImage().Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            var result = await TranslationFlow.TranslateImageAsync(translator, stream.ToArray());
            if (!result.Success || result.ImageBytes == null) throw new InvalidOperationException(result.UserMessage);
            using var output = new MemoryStream(result.ImageBytes);
            using var loaded = new Bitmap(output);
            AddResult(outputName, new(Image: (Bitmap)loaded.Clone(), Region: input.Region));
        }
        else
        {
            if (input.Image != null && !await DependencyGate.EnsureAsync(DepIds.Ocr)) return false;
            using var pending = new PendingNotice("正在翻译本次内容...");
            string source = input.Image != null
                ? await CaptureSession.GetSharedOcrService().ExtractTextAsync(input.Image) : input.RequireText();
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("本次图片没有识别到可翻译的文字。");
            var result = await translator.TranslateDocumentAsync(source);
            if (!result.Success || string.IsNullOrWhiteSpace(result.TranslatedText))
                throw new InvalidOperationException(result.ErrorMessage ?? "没有得到译文。");
            var (sourceLanguage, targetLanguage, _, _) = LanguageDetector.ResolveLanguages(source,
                translator.Config.SourceLanguage, translator.Config.TargetLanguage, translator.Config.SecondaryTargetLanguage);
            var translation = TranslationResult.Ok(source, result.TranslatedText, sourceLanguage, targetLanguage,
                result.DetectedSourceLanguage, translator.ProviderType.ToString(), 0, result.Pairs);
            AddResult(outputName, new(Text: result.TranslatedText, Translation: translation));
        }
        UsageStatistics.RecordTranslate();
        return true;
    }

    private WorkflowResult GetResult(string? name) => name != null && _results.TryGetValue(name, out var result)
        ? result : throw new InvalidOperationException("找不到输入结果：" + name);

    private void AddResult(string name, WorkflowResult result)
    {
        // 名称不允许覆盖，确保后续引用的是先前那一份结果。
        if (!_results.TryAdd(name, result))
        {
            result.Image?.Dispose();
            throw new InvalidOperationException("结果名称已使用：" + name);
        }
        _current = name;
    }
    public void Dispose()
    {
        foreach (var result in _results.Values) result.Image?.Dispose();
        _results.Clear(); _current = null;
    }

    private sealed class PendingNotice : IDisposable
    {
        private readonly StickerToastWindow? _window;
        public PendingNotice(string message) => _window = StickerToastWindow.ShowPendingAtTop(message);
        public void Dispose() => _window?.Close();
    }
}
