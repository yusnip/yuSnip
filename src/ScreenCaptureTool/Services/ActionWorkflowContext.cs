using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Actions;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Deps;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Core.Matting;
using ScreenCaptureTool.Core.Scripting;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Platform;
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
        if (call.IsWait) return await WaitAsync(call.WaitMilliseconds);
        string output = call.ResultName ?? "#result" + ++_sequence;
        var input = call.NeedsInput ? GetResult(call.InputName ?? _current) : null;
        switch (method)
        {
            case "captureimage":
                var capture = call.CaptureOptions;
                if (capture.DelayMilliseconds > 0 && !await WaitAsync(capture.DelayMilliseconds)) return false;
                var gesture = _gestureConsumed ? null : _gesture;
                _gestureConsumed = true;
                bool completed = await _app.RunCaptureAsync("Workflow", gesture, (image, region) =>
                    AddResult(output, new(Image: image, Region: region)),
                    suppressCaptureStroke: capture.WithoutStroke,
                    captureOverrides: capture);
                return completed && _results.ContainsKey(output);

            case "capturescreen":
                ActionCaptureOptions.TryParseKeyValues(args, 1, out var screenKv);
                string scope = screenKv.TryGetValue("scope", out var sc) ? sc.ToLowerInvariant() : "all";
                using (var fullFrame = new ScreenCapturer().CaptureVirtualScreen())
                {
                    Rectangle targetRect = fullFrame.VirtualScreenRect;
                    if (scope == "current")
                    {
                        var curMon = MonitorHelper.GetMonitorAtCursor();
                        if (curMon.Width > 0 && curMon.Height > 0) targetRect = curMon;
                    }
                    else if (scope == "primary")
                    {
                        var primaryInfo = MonitorHelper.GetMonitorInfoAtPoint(0, 0);
                        if (primaryInfo.HasValue && primaryInfo.Value.rect.Width > 0 && primaryInfo.Value.rect.Height > 0)
                            targetRect = primaryInfo.Value.rect;
                    }
                    var local = new Rectangle(
                        targetRect.X - fullFrame.VirtualScreenRect.X,
                        targetRect.Y - fullFrame.VirtualScreenRect.Y,
                        targetRect.Width, targetRect.Height);
                    local.Intersect(new Rectangle(0, 0, fullFrame.FullScreen.Width, fullFrame.FullScreen.Height));
                    if (local.Width <= 0 || local.Height <= 0)
                        local = new Rectangle(0, 0, fullFrame.FullScreen.Width, fullFrame.FullScreen.Height);
                    var bmp = fullFrame.FullScreen.Clone(local, fullFrame.FullScreen.PixelFormat);
                    AddResult(output, new(Image: bmp, Region: targetRect));
                }
                return true;

            case "captureregion":
                int rx = int.Parse(args[0], CultureInfo.InvariantCulture);
                int ry = int.Parse(args[1], CultureInfo.InvariantCulture);
                int rw = int.Parse(args[2], CultureInfo.InvariantCulture);
                int rh = int.Parse(args[3], CultureInfo.InvariantCulture);
                var regOpt = call.CaptureOptions;
                using (var regFrame = new ScreenCapturer().CaptureVirtualScreen())
                {
                    var rect = new Rectangle(rx - regFrame.VirtualScreenRect.X, ry - regFrame.VirtualScreenRect.Y, rw, rh);
                    rect.Intersect(new Rectangle(0, 0, regFrame.FullScreen.Width, regFrame.FullScreen.Height));
                    if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("固定截图区域不在当前屏幕范围内。");
                    Bitmap cropped = regFrame.FullScreen.Clone(rect, regFrame.FullScreen.PixelFormat);
                    var regionOut = new Rectangle(rx, ry, rect.Width, rect.Height);

                    int radiusParam = regOpt.CornerRadiusOverride ?? 0;
                    int strokePx = regOpt.StrokeWidthOverride ?? 0;
                    bool shadow = regOpt.ShadowOverride == true;
                    if (radiusParam > 0 || strokePx > 0 || shadow)
                    {
                        int radiusPx = SelectionEffects.ParamToRadiusPx(radiusParam);
                        var styled = SelectionEffects.Create(cropped, radiusPx, shadow, strokePx, AppSettings.Current.CaptureStrokeColor);
                        if (styled != null)
                        {
                            cropped.Dispose();
                            cropped = styled;
                            int pad = SelectionEffects.MarginOf(shadow, strokePx);
                            regionOut = Rectangle.Inflate(regionOut, pad, pad);
                        }
                    }
                    AddResult(output, new(Image: cropped, Region: regionOut));
                }
                return true;

            case "readsticker":
                var focused = StickerFocusManager.Focused;
                if (focused == null) throw new InvalidOperationException("当前没有获得焦点的贴图。");
                var stickerBmp = focused.TryCloneCurrentBitmap();
                if (stickerBmp == null) throw new InvalidOperationException("当前焦点贴图不是图片贴图。");
                AddResult(output, new(Image: stickerBmp));
                return true;

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
                ActionCaptureOptions.TryParseKeyValues(args, 2, out var ocrKv);
                string cleanup = ocrKv.TryGetValue("cleanup", out var cl) ? cl.ToLowerInvariant() : "none";
                using (var pending = new PendingNotice("正在识别本次图片..."))
                {
                    string text = await CaptureSession.GetSharedOcrService().ExtractTextAsync(input!.RequireImage());
                    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("这张图片没有识别到文字。");
                    text = ApplyOcrCleanup(text, cleanup);
                    AddResult(output, new(Text: text));
                    UsageStatistics.RecordOcr();
                }
                return true;

            case "translateresult":
                ActionCaptureOptions.TryParseKeyValues(args, 3, out var trKv);
                string? targetLang = trKv.TryGetValue("to", out var tl) && tl.ToLowerInvariant() is "zh" or "en" or "ja" or "ko"
                    ? tl.ToLowerInvariant() : null;
                return await TranslateAsync(args[0], input!, output, targetLang);

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
                ActionCaptureOptions.TryParseKeyValues(args, 1, out var pinKv);
                bool? pinShadow = pinKv.TryGetValue("shadow", out var psh)
                    ? (psh.Equals("on", StringComparison.OrdinalIgnoreCase) ? true : psh.Equals("off", StringComparison.OrdinalIgnoreCase) ? false : null)
                    : null;
                bool pinLock = pinKv.TryGetValue("lock", out var plk) && plk.ToLowerInvariant() is "on" or "true";
                bool pinPass = pinKv.TryGetValue("pass", out var pps) && pps.ToLowerInvariant() is "on" or "true";
                double? pinOpacity = pinKv.TryGetValue("opacity", out var pop) && int.TryParse(pop, out int opPct) && opPct is >= 20 and <= 100
                    ? opPct / 100.0 : null;

                if (input!.Image != null)
                {
                    var owned = (Bitmap)input.Image.Clone();
                    var sticker = new StickerWindow(owned);
                    sticker.SetHistoryId(CaptureHistoryStore.TryRecord(input.Image, CaptureHistorySource.Sticker, null, AppSettings.Current));
                    if (input.Region is { } region) sticker.SetStickerScreenPosition(new PixelPoint(region.X, region.Y));
                    sticker.ApplyInitialActionOptions(pinShadow, pinLock, pinPass, pinOpacity);
                    sticker.Show();
                    if (!pinPass)
                    {
                        sticker.Activate();
                        StickerFocusManager.SetFocus(sticker);
                    }
                }
                else
                {
                    _app.ShowStickerFromClipboardText(input.RequireText());
                }
                return true;

            case "saveresult":
                return await SaveResultAsync(input!, args);

            case "showresult":
                if (input!.Translation != null)
                {
                    var window = TranslationResultWindow.FromResult(input.Translation, AppSettings.Current);
                    window.Show(); window.Activate();
                }
                else OcrWindow.ShowResult(input.RequireText(), null);
                return true;

            case "visionresult":
                ActionCaptureOptions.TryParseKeyValues(args, 2, out var visKv);
                string? prompt = null;
                if (args[0].Equals("Custom", StringComparison.OrdinalIgnoreCase)
                    && visKv.TryGetValue("prompt", out var customPromptRaw)
                    && !string.IsNullOrWhiteSpace(customPromptRaw))
                {
                    prompt = ActionModuleRegistry.UnescapeArgValue(customPromptRaw);
                }
                else if (args[0].Equals("Prompt", StringComparison.OrdinalIgnoreCase) || !AppSettings.Current.AiUseDefaultPrompt)
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

    private async Task<bool> SaveResultAsync(WorkflowResult input, string[] args)
    {
        ActionCaptureOptions.TryParseKeyValues(args, 1, out var kv);
        bool isDialog = kv.TryGetValue("mode", out var m) && m.Equals("dialog", StringComparison.OrdinalIgnoreCase);
        bool isJpg = kv.TryGetValue("format", out var f) && f.ToLowerInvariant() is "jpg" or "jpeg";
        string customDir = kv.TryGetValue("dir", out var d) ? d.Trim() : "";

        var settings = AppSettings.Current;
        string ext = input.Image != null ? (isJpg ? ".jpg" : ".png") : ".txt";
        string defaultPath = CaptureSession.ResolveActionSavePath(settings, ext);
        if (!string.IsNullOrEmpty(customDir))
        {
            defaultPath = Path.Combine(customDir, Path.GetFileName(defaultPath));
        }

        string path = defaultPath;
        if (isDialog)
        {
            string? picked = await PickSavePathAsync(defaultPath, input.Image != null, isJpg);
            if (string.IsNullOrEmpty(picked)) return false;
            path = picked;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            string extension = Path.GetExtension(path);
            for (int i = 1; File.Exists(path); i++) path = stem + "_" + i + extension;
        }

        if (input.Image != null)
        {
            ImageExportService.Save(input.Image, path);
            CaptureHistoryStore.TryRecord(input.Image, CaptureHistorySource.Save, path, settings);
        }
        else
        {
            await File.WriteAllTextAsync(path, input.RequireText(), new UTF8Encoding(false));
        }
        StickerToastWindow.ShowAtTop("已保存动作结果：" + Path.GetFileName(path));
        return true;
    }

    private async Task<string?> PickSavePathAsync(string suggestedPath, bool isImage, bool preferJpg)
    {
        Window? owner = null;
        Window? tempHost = null;
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            owner = desktop.Windows.FirstOrDefault(w => w.IsVisible && w.IsActive) ?? desktop.Windows.FirstOrDefault(w => w.IsVisible);

        if (owner == null)
        {
            tempHost = new Window { Width = 1, Height = 1, Opacity = 0, ShowInTaskbar = false, SystemDecorations = SystemDecorations.None };
            tempHost.Show();
            owner = tempHost;
        }

        try
        {
            var sp = owner.StorageProvider;
            var dir = Path.GetDirectoryName(suggestedPath);
            IStorageFolder? startFolder = !string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                ? await sp.TryGetFolderFromPathAsync(dir) : null;

            var fileTypes = isImage
                ? (preferJpg
                    ? new[] { new FilePickerFileType("JPEG 图片") { Patterns = ["*.jpg", "*.jpeg"] }, new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] } }
                    : new[] { new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }, new FilePickerFileType("JPEG 图片") { Patterns = ["*.jpg", "*.jpeg"] } })
                : new[] { new FilePickerFileType("文本文件") { Patterns = ["*.txt"] } };

            var picked = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存动作结果",
                SuggestedFileName = Path.GetFileName(suggestedPath),
                SuggestedStartLocation = startFolder,
                DefaultExtension = Path.GetExtension(suggestedPath).TrimStart('.'),
                FileTypeChoices = fileTypes,
            });
            return picked?.TryGetLocalPath();
        }
        finally
        {
            tempHost?.Close();
        }
    }

    private static string ApplyOcrCleanup(string text, string mode)
    {
        if (mode == "none") return text;
        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (mode == "compact")
        {
            string joined = Regex.Replace(normalized, @"\s+", " ").Trim();
            return Regex.Replace(joined, @"(?<=[\u4e00-\u9fff，。！？；：、（）《》])\s+(?=[\u4e00-\u9fff，。！？；：、（）《》])", "");
        }
        // merge：保留空行作为段落分隔，合并段内软换行
        var paragraphs = Regex.Split(normalized.Trim(), @"\n\s*\n");
        var merged = new List<string>(paragraphs.Length);
        foreach (string para in paragraphs)
        {
            var lines = para.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length == 0) continue;
            var sb = new StringBuilder(lines[0]);
            for (int i = 1; i < lines.Length; i++)
            {
                char prev = sb[^1], next = lines[i][0];
                bool bothAscii = prev < 128 && next < 128 && !char.IsWhiteSpace(prev);
                if (bothAscii) sb.Append(' ');
                sb.Append(lines[i]);
            }
            merged.Add(sb.ToString());
        }
        return string.Join(Environment.NewLine + Environment.NewLine, merged);
    }

    private async Task<bool> WaitAsync(int milliseconds)
    {
        bool CanContinue() => !App.IsShuttingDown && (_gestureConsumed || _gesture?.Read().Cancelled != true);
        if (!CanContinue()) return false;
        if (milliseconds > 0) await Task.Delay(milliseconds);
        return CanContinue();
    }

    private async Task<bool> TranslateAsync(string format, WorkflowResult input, string outputName, string? targetLangOverride)
    {
        var settings = AppSettings.Current;
        var config = settings.ToTranslationConfig();
        if (!string.IsNullOrEmpty(targetLangOverride))
        {
            config.TargetLanguage = targetLangOverride;
        }
        var translator = new TranslationService(config);
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

            TextTranslationResult result;
            string sourceLanguage, targetLanguage;
            if (!string.IsNullOrEmpty(targetLangOverride))
            {
                sourceLanguage = "auto";
                targetLanguage = targetLangOverride;
                result = await translator.TranslateDocumentAsync(source, sourceLanguage, targetLanguage, null);
            }
            else
            {
                result = await translator.TranslateDocumentAsync(source);
                (sourceLanguage, targetLanguage, _, _) = LanguageDetector.ResolveLanguages(source,
                    translator.Config.SourceLanguage, translator.Config.TargetLanguage, translator.Config.SecondaryTargetLanguage);
            }
            if (!result.Success || string.IsNullOrWhiteSpace(result.TranslatedText))
                throw new InvalidOperationException(result.ErrorMessage ?? "没有得到译文。");
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
