using System;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Avalonia;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool.Services;

/// <summary>
/// 「图片到文本」的落地分流：OCR 得到原文后统一呼出极简浮窗，开启流式时逐段展示原译文对照。
/// 用户需要完整对照或编辑时，可双击浮窗空白处或点击右上角图标升级到编辑窗口。
/// </summary>
public static class TranslationFlow
{
    /// <summary>免费服务在本机生成译文图；付费服务继续使用各自产商图片接口。</summary>
    public static async Task<ImageTranslationResult> TranslateImageAsync(TranslationService translator, byte[] png,
        CancellationToken ct = default)
    {
        if (translator.Config.Provider != TranslationProvider.BilibiliIndex)
            return await translator.TranslateImageAsync(png, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!await DependencyGate.EnsureAsync(Core.Deps.DepIds.Ocr).ConfigureAwait(false))
            return ImageTranslationResult.Fail(ImageTranslationFailure.Unknown, "OCR 组件尚未就绪，已取消图片翻译");
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => LocalImageTranslation.TranslateAsync(png, translator,
            bitmap => CaptureSession.GetSharedOcrService().ExtractLinesWithBoxesAsync(bitmap), ct), ct).ConfigureAwait(false);
    }

    /// <summary>历史分流常量（已废弃）：已取消超过 400 字强制开编辑主窗口的限制，所有文本均统一出浮窗。</summary>
    [Obsolete("已取消 400 字限制，文本统一出极简浮窗并写入剪贴板，由用户自主决定是否升级到编辑窗口。")]
    public const int MiniWindowMaxChars = 400;

    /// <summary>引擎显示名（浮窗与编辑窗口共用，避免两处各写一份）。</summary>
    public static string GetEngineName(TranslationConfig config)
    {
        return config.Provider switch
        {
            TranslationProvider.Baidu => "百度翻译",
            TranslationProvider.Youdao => "有道翻译",
            TranslationProvider.BilibiliIndex => "bilibili-index",
            _ => string.Empty
        };
    }

    /// <summary>
    /// OCR 行级结果拼接为完整原文（多行以换行分隔）。
    /// </summary>
    public static string JoinOcrLines(System.Collections.Generic.List<OcrLineRegion> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 分流入口（UI 线程调用）：统一出极简浮窗（锚定截图选区或贴图），
    /// 按设置逐段显示对照，全部完成后自动写入剪贴板；完成后可通过浮窗升级到编辑主窗口。
    /// </summary>
    public static void DispatchOcrText(string? sourceText, AppSettings settings, PixelRect anchor)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                StickerToastWindow.ShowAtTop("未识别到文字");
                return;
            }

            // 前置校验：没配密钥就别开窗，两条入口统一只弹一句提示（开窗只会让用户看见一个报错的空壳）
            if (!settings.ToTranslationConfig().IsValid)
            {
                StickerToastWindow.ShowAtTop("请在设置中启用 bilibili-index，或配置所选翻译服务的密钥");
                return;
            }

            TranslateMiniWindow.ShowForText(sourceText, settings, anchor);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TranslationFlow.DispatchOcrText 异常", ex);
            StickerToastWindow.ShowAtTop("翻译失败：" + ex.Message);
        }
    }

    /// <summary>直接打开编辑窗口并翻译（长文本，或有原文无译文的升级场景）。</summary>
    public static void OpenEditor(string sourceText, AppSettings settings)
    {
        var win = TranslationResultWindow.ForText(sourceText, settings);
        win.Show();
        win.Activate();
    }

    /// <summary>
    /// 后台执行 OCR 后分流，供截图会话与贴图窗口共用（需在非 UI 线程调用）。
    /// </summary>
    /// <param name="bitmap">待识别位图，所有权归调用方（本方法内部会自行复制一份）。</param>
    /// <param name="settings">应用设置。</param>
    /// <param name="anchor">定位锚点：截图选区或贴图的屏幕像素矩形。</param>
    public static Task RunOcrAndDispatchAsync(System.Drawing.Bitmap bitmap, AppSettings settings, PixelRect anchor)
    {
        return Task.Run(async () =>
        {
            System.Drawing.Bitmap? copy = null;
            try
            {
                // 复制一份供后台使用，避免与 UI 线程持有的位图争用
                copy = new System.Drawing.Bitmap(bitmap);
                // 缺 OCR 模型：居中开卡就地下载，下完自动续跑翻译（取消/失败时卡自己停在失败态，这里静默退回）
                if (!await DependencyGate.EnsureAsync(Core.Deps.DepIds.Ocr).ConfigureAwait(false)) return;
                var ocrLines = await CaptureSession.GetSharedOcrService()
                    .ExtractLinesWithBoxesAsync(copy).ConfigureAwait(false);

                string sourceText = ocrLines.Count == 0 ? string.Empty : JoinOcrLines(ocrLines);
                await Dispatcher.UIThread.InvokeAsync(() => DispatchOcrText(sourceText, settings, anchor));
            }
            catch (Exception ex)
            {
                AppLogger.Error("TranslationFlow 后台 OCR 异常", ex);
                await Dispatcher.UIThread.InvokeAsync(() => StickerToastWindow.ShowAtTop("识别失败：" + ex.Message));
            }
            finally
            {
                try { copy?.Dispose(); } catch { }
            }
        });
    }
}
