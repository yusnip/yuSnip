#nullable enable

using System;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 翻译服务门面。根据 <see cref="TranslationConfig"/> 创建对应的翻译提供者并路由请求。
/// 同时负责管理共享的 <see cref="HttpClient"/> 实例和提供者的生命周期。
/// 图片翻译由产商接口完成（百度图片翻译 V2.0 / 有道 OCR 翻译），本地不做任何文字渲染。
/// </summary>
public sealed partial class TranslationService
{
    private static readonly HttpClient SharedHttpClient = new HttpClient();

    private readonly TranslationConfig _config;
    private readonly ITranslationProvider? _provider;

    /// <summary>
    /// 根据配置创建翻译服务实例。
    /// 若配置无效（未选择服务商或凭证为空），则 <see cref="IsAvailable"/> 返回 false，
    /// 所有翻译方法将直接返回失败结果。
    /// </summary>
    public TranslationService(TranslationConfig config)
    {
        _config = config;
        _provider = CreateProvider(config);
    }

    /// <summary>当前翻译服务是否可用（已配置有效的服务商和凭证）。</summary>
    public bool IsAvailable => _provider != null;

    /// <summary>当前服务商类型。</summary>
    public TranslationProvider ProviderType => _provider?.ProviderType ?? TranslationProvider.None;

    /// <summary>底层翻译提供者实例（可能为 null）。</summary>
    public ITranslationProvider? Provider => _provider;

    /// <summary>当前翻译配置。</summary>
    public TranslationConfig Config => _config;

    // ── 便捷工厂方法 ──────────────────────────────────────────

    /// <summary>从 AppSettings 创建翻译服务。</summary>
    public static TranslationService FromSettings(AppSettings settings)
    {
        return new TranslationService(settings.ToTranslationConfig());
    }

    // ── 翻译接口 ──────────────────────────────────────────────

    /// <summary>验证当前配置的 API 凭证是否有效。</summary>
    public async Task<(bool ok, string? errorMessage)> ValidateAsync(CancellationToken ct = default)
    {
        if (_provider == null)
            return (false, "请在设置中启用 bilibili-index，或配置所选翻译服务的密钥");
        return await _provider.ValidateAsync(ct);
    }

    /// <summary>
    /// 文本翻译。使用配置中的默认源/目标语言，并在源语言与目标语言相同时自动回退至次级目标语言。
    /// </summary>
    public Task<TextTranslationResult> TranslateTextAsync(string text, CancellationToken ct = default)
    {
        var (effSrc, effTgt, _, _) = LanguageDetector.ResolveLanguages(
            text, _config.SourceLanguage, _config.TargetLanguage, _config.SecondaryTargetLanguage);
        return TranslateTextAsync(text, effSrc, effTgt, ct);
    }

    /// <summary>
    /// 文本翻译（指定语言）。所有翻译入口都经此方法，故在此统一落一条结果日志：
    /// 失败只弹 toast 会让"是服务商返回错误码还是我们本地异常"事后无从判定。
    /// 日志只记字符数与错误码，不记原文与译文内容（用户屏幕上的文字不入日志）。
    /// </summary>
    public async Task<TextTranslationResult> TranslateTextAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (_provider == null)
        {
            AppLogger.Warn($"[翻译] 未发起：服务商未配置（字符 {(text?.Length ?? 0)}）");
            return TextTranslationResult.Fail("NOT_CONFIGURED", "未配置翻译服务商");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _provider.TranslateTextAsync(text, sourceLang, targetLang, ct).ConfigureAwait(false);
        sw.Stop();

        if (result.Success)
            AppLogger.Info($"[翻译] 成功 provider={ProviderType} 字符={(text?.Length ?? 0)} " +
                           $"译文={(result.TranslatedText?.Length ?? 0)} 耗时={sw.ElapsedMilliseconds}ms");
        else
            AppLogger.Warn($"[翻译] 失败 provider={ProviderType} 字符={(text?.Length ?? 0)} " +
                           $"耗时={sw.ElapsedMilliseconds}ms 错误码={result.ErrorCode ?? "-"} 信息=[已脱敏]");
        return result;
    }

    // ── 图片翻译（产商接口） ──────────────────────────────────

    /// <summary>
    /// 上传体积上限。两家都是「base64 后 ≤ 5M」（百度文档另有一处写「原图 5M」，口径自相矛盾，按更严的算）。
    /// base64 膨胀 4/3，故原始字节上限取 5M × 3/4。
    /// </summary>
    private const long MaxUploadRawBytes = 5_000_000L * 3 / 4;

    /// <summary>降采样步长与短边下限：短边低于此值再缩也不会更小，判为图片过大。</summary>
    private const float DownscaleStep = 0.8f;
    private const int MinShortEdge = 30;
    private const long JpegQuality = 85L;

    /// <summary>
    /// 限流重试口径：等 1.5 秒、只试一次。
    /// 实测两家都有瞬时 QPS 限制且**秒回不计费**（有道 61ms 返回 411、百度 210ms 返回 59004），
    /// 隔 6 秒重发同一张就成功。不做队列、不做并发数限制——用户连点本来就该看到"稍后再试"。
    /// </summary>
    private const int RateLimitRetryDelayMs = 1500;
    private const int MaxImageAttempts = 2;

    /// <summary>JPEG 编码器（按 MimeType 取，跨系统稳定）。</summary>
    private static readonly System.Drawing.Imaging.ImageCodecInfo? JpegCodec =
        System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 图片到图片翻译，语言取配置默认值。
    ///
    /// 「源语言与目标语言相同 → 自动改翻次级目标语言」这条文本翻译早就有的回退，图片侧只能**事后**判：
    /// 图上文字由产商识别，我们发请求前拿不到文本，所以读产商回传的 <c>from</c>，命中同族就换语言重发一次。
    /// 少了这一段，用户截一张中文图而目标语言设成中文时会拿到一张"没变"的成品图。
    /// </summary>
    public async Task<ImageTranslationResult> TranslateImageAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        var first = await TranslateImageAsync(imageBytes, _config.SourceLanguage, _config.TargetLanguage, ct)
            .ConfigureAwait(false);

        string? detected = first.DetectedSourceLanguage;
        if (first.Success && !string.IsNullOrEmpty(detected) &&
            LanguageDetector.IsSameLanguage(detected, _config.TargetLanguage) &&
            !LanguageDetector.IsSameLanguage(detected, _config.SecondaryTargetLanguage))
        {
            AppLogger.Info($"[翻译] 图片：图上文字属 {detected}，与目标语言同族，改用次级目标语言 " +
                           $"{_config.SecondaryTargetLanguage} 重发");
            var retry = await TranslateImageAsync(imageBytes, detected!, _config.SecondaryTargetLanguage, ct)
                .ConfigureAwait(false);
            if (retry.Success) return retry;
        }

        return first;
    }

    /// <summary>
    /// 图片到图片翻译（指定语言）。产商一次完成识别、擦除与译文绘制，本地不参与任何渲染，
    /// 所以这里只有"预检 → 上传归一 → 发一次（限流则补一次）→ 落日志"四步。
    /// </summary>
    public async Task<ImageTranslationResult> TranslateImageAsync(
        byte[] imageBytes,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (_provider == null)
        {
            AppLogger.Warn("[翻译] 图片未发起：服务商未配置");
            return ImageTranslationResult.Fail(ImageTranslationFailure.NotConfigured, "未配置翻译服务商");
        }
        if (imageBytes == null || imageBytes.Length == 0)
            return ImageTranslationResult.Fail(ImageTranslationFailure.Unknown, "图片数据为空");
        if (_provider.ProviderType == TranslationProvider.BilibiliIndex)
            return await _provider.TranslateImageAsync(imageBytes, sourceLang, targetLang, ct).ConfigureAwait(false);

        // 百度图片翻译 V2.0 的语种表比文本接口窄（少繁体中文/泰/越/阿）。
        // 提前点名，否则吃到的是一个语义含混的 55001「参数校验失败」。
        if (_provider.ProviderType == TranslationProvider.Baidu && IsBaiduImageUnsupported(targetLang))
            return ImageTranslationResult.Fail(ImageTranslationFailure.LanguageUnsupported,
                $"百度图片翻译不支持目标语言 {TranslationLanguage.GetDisplayName(targetLang)}");

        byte[]? uploadBytes = PrepareUploadBytes(imageBytes);
        if (uploadBytes == null)
            return ImageTranslationResult.Fail(ImageTranslationFailure.TooLarge,
                $"降采样后仍超过产商上限（原图 {imageBytes.Length / 1024}KB）");

        ImageTranslationResult result;
        int attempts = 0;
        while (true)
        {
            attempts++;
            result = await _provider.TranslateImageAsync(uploadBytes, sourceLang, targetLang, ct).ConfigureAwait(false);
            if (result.FailureReason != ImageTranslationFailure.RateLimited || attempts >= MaxImageAttempts) break;

            AppLogger.Warn($"[翻译] 图片被限流，{RateLimitRetryDelayMs}ms 后重试一次");
            await Task.Delay(RateLimitRetryDelayMs, ct).ConfigureAwait(false);
        }

        // 只记字节数、语种与错误信息，不记图片内容
        if (result.Success)
            AppLogger.Info($"[翻译] 图片成功 provider={ProviderType} " +
                           $"上传={uploadBytes.Length / 1024}KB" +
                           (uploadBytes.Length == imageBytes.Length ? "" : $"（原图 {imageBytes.Length / 1024}KB）") +
                           $" 成品={(result.ImageBytes?.Length ?? 0) / 1024}KB " +
                           $"语种={result.DetectedSourceLanguage ?? "-"} 尝试={attempts} 耗时={result.ElapsedMs}ms");
        else
            AppLogger.Warn($"[翻译] 图片失败 provider={ProviderType} 类别={result.FailureReason} 尝试={attempts} " +
                           $"上传={uploadBytes.Length / 1024}KB 耗时={result.ElapsedMs}ms " +
                           $"信息=[已脱敏]");
        return result;
    }

    /// <summary>百度图片翻译 V2.0 语种表里没有的四个目标语（文本接口有，图片接口没有）。</summary>
    private static bool IsBaiduImageUnsupported(string? internalLang) =>
        internalLang is TranslationLanguage.ChineseTraditional
            or TranslationLanguage.Thai
            or TranslationLanguage.Vietnamese
            or TranslationLanguage.Arabic;

    /// <summary>
    /// 上传前的字节归一：**只有"字节"这一维**——两家对尺寸与长宽比都不设限
    /// （实测 1200×5000、5000×1000、3840×2160 均原尺寸返回成品图）。
    /// 超过上限先转 JPEG（质量 85），仍超就等比降采样，反复到短边下限仍超则判 <see cref="ImageTranslationFailure.TooLarge"/>。
    /// </summary>
    /// <returns>可直接上传的字节；无法压进上限时返回 <c>null</c>。</returns>
    private static byte[]? PrepareUploadBytes(byte[] imageBytes)
    {
        if (imageBytes.Length <= MaxUploadRawBytes) return imageBytes;

        try
        {
            using var src = new MemoryStream(imageBytes);
            using var decoded = Image.FromStream(src);
            // 再拷一份：解码得到的 Image 在底层流关闭后不可再绘制
            using var source = new Bitmap(decoded);

            for (float scale = 1f; ; scale *= DownscaleStep)
            {
                byte[] jpeg = EncodeJpeg(source, scale);
                if (jpeg.Length <= MaxUploadRawBytes)
                {
                    AppLogger.Info($"[翻译] 图片超产商上限，已转 JPEG 上传：" +
                                   $"{source.Width}×{source.Height} → {jpeg.Length / 1024}KB（缩放 {scale:F2}）");
                    return jpeg;
                }
                if (Math.Min(source.Width, source.Height) * scale < MinShortEdge) return null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("TranslationService.PrepareUploadBytes 重编码失败", ex);
            return null;
        }
    }

    /// <summary>把位图缩放后编成 JPEG 字节。画到 24bpp 白底画布：既避开带 alpha 的编码问题，也让透明区落到白底而非黑底。</summary>
    private static byte[] EncodeJpeg(Bitmap source, float scale)
    {
        if (JpegCodec == null) throw new InvalidOperationException("系统缺少 JPEG 编码器");

        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var canvas = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, w, h);
        }

        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, JpegQuality);

        using var outMs = new MemoryStream();
        canvas.Save(outMs, JpegCodec, ep);
        return outMs.ToArray();
    }

    // ── 提供者工厂 ────────────────────────────────────────────

    private static ITranslationProvider? CreateProvider(TranslationConfig config)
    {
        if (!config.IsValid) return null;

        try
        {
            return config.Provider switch
            {
                TranslationProvider.BilibiliIndex => new BilibiliIndexTranslationProvider(SharedHttpClient, config.TimeoutMs),
                TranslationProvider.Baidu => new BaiduTranslationProvider(
                    config.AppId, config.SecretKey, SharedHttpClient, config.TimeoutMs),

                TranslationProvider.Youdao => new YoudaoTranslationProvider(
                    config.AppId, config.SecretKey, SharedHttpClient, config.TimeoutMs),

                _ => null,
            };
        }
        catch (System.Exception ex)
        {
            AppLogger.Error("TranslationService.CreateProvider 异常", ex);
            return null;
        }
    }
}
