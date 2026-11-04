#nullable enable

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 产商图片翻译的失败类别。调用点只按此枚举出提示，禁止各自拼字符串——
/// 截图与贴图两条流水线的文案必须同源。
/// </summary>
public enum ImageTranslationFailure
{
    /// <summary>未失败。</summary>
    None,

    /// <summary>未选择服务商，或 APP ID / 密钥（百度侧即 API Key）为空。</summary>
    NotConfigured,

    /// <summary>转 JPEG 并降采样到底后仍超过产商体积上限。</summary>
    TooLarge,

    /// <summary>产商没在图上认出可翻译的文字。</summary>
    NoText,

    /// <summary>鉴权失败（凭证错、账号无效、应用未绑定服务实例）。</summary>
    AuthFailed,

    /// <summary>凭证有效但该账户未开通图片翻译服务。</summary>
    ServiceNotEnabled,

    /// <summary>账户余额不足。</summary>
    Quota,

    /// <summary>瞬时 QPS 超限（已按 <c>TranslationService</c> 的口径重试过一次）。</summary>
    RateLimited,

    /// <summary>请求超时。</summary>
    Timeout,

    /// <summary>该服务商的图片翻译接口不支持所选语种。</summary>
    LanguageUnsupported,

    /// <summary>产商处理图片自身出错。</summary>
    Upstream,

    /// <summary>产商服务端错误。</summary>
    Server,

    /// <summary>网络不可达 / DNS / TLS 等传输层失败。</summary>
    Network,

    /// <summary>未识别的错误码或响应格式异常。</summary>
    Unknown,
    /// <summary>仅提供文字译文，不提供渲染后的成品图。</summary>
    OutputUnsupported,
}

/// <summary>
/// 图片翻译结果。百度／有道返回产商成品图，Index 由独立本机流程生成 PNG。
/// </summary>
public sealed class ImageTranslationResult
{
    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>成品图字节（PNG 或 JPEG；本机流程输出 PNG）。失败时为 <c>null</c>。</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>产商识别并回传的源语言，已归一为内部语言代码（见 <see cref="TranslationLanguage.ToInternalCode"/>）；可能为空。</summary>
    public string? DetectedSourceLanguage { get; init; }

    /// <summary>失败类别。成功时为 <see cref="ImageTranslationFailure.None"/>。</summary>
    public ImageTranslationFailure FailureReason { get; init; } = ImageTranslationFailure.None;

    /// <summary>
    /// 明细错误信息（含产商原始错误码与文案），供日志排障。
    /// 界面提示一律读 <see cref="UserMessage"/>，不读这个字段。
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>本次请求耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// 本机回填跳过的文字块数（译文放不下或补图不可信）。
    /// 只有 Index 的本机成品图流程会填；百度／有道返回产商整图，恒为 0。
    /// </summary>
    public int SkippedRegions { get; init; }

    /// <summary>给用户看的一句话。文案只写在这里，两个调用点共用。</summary>
    public string UserMessage => FailureReason switch
    {
        ImageTranslationFailure.None => SkippedRegions > 0
            ? $"翻译完成，{SkippedRegions} 处未能回填"
            : "翻译完成",
        ImageTranslationFailure.OutputUnsupported => "当前服务不返回译文图片，请使用「图片到文本」模式",
        ImageTranslationFailure.NotConfigured => "请先在设置中配置翻译凭证",
        ImageTranslationFailure.TooLarge => "图片过大，无法翻译，请缩小选区后重试",
        ImageTranslationFailure.NoText => "未识别到可翻译的文字",
        ImageTranslationFailure.AuthFailed => "翻译服务鉴权失败，请检查设置中的 APP ID 与密钥",
        ImageTranslationFailure.ServiceNotEnabled => "该账户未开通图片翻译服务，请到产商控制台开通",
        ImageTranslationFailure.Quota => "翻译账户余额不足",
        ImageTranslationFailure.RateLimited => "翻译请求过于频繁，请稍后再试",
        ImageTranslationFailure.Timeout => ErrorMessage ?? "翻译请求超时，请检查网络后重试",
        ImageTranslationFailure.LanguageUnsupported => "该语种不支持图片翻译，请改用「文本翻译」模式",
        ImageTranslationFailure.Upstream => "翻译服务处理图片时出错，请稍后重试",
        ImageTranslationFailure.Server => "翻译服务暂时不可用，请稍后重试",
        ImageTranslationFailure.Network => "翻译请求失败：网络不可达",
        _ => "翻译失败: " + (ErrorMessage ?? "未知错误"),
    };

    public static ImageTranslationResult Ok(
        byte[] imageBytes,
        string? detectedSource = null,
        long elapsedMs = 0,
        int skippedRegions = 0) =>
        new()
        {
            Success = true,
            ImageBytes = imageBytes,
            DetectedSourceLanguage = detectedSource,
            ElapsedMs = elapsedMs,
            SkippedRegions = skippedRegions,
        };

    public static ImageTranslationResult Fail(
        ImageTranslationFailure reason,
        string? errorMessage = null,
        long elapsedMs = 0) =>
        new()
        {
            Success = false,
            FailureReason = reason,
            ErrorMessage = errorMessage,
            ElapsedMs = elapsedMs,
        };

    public override string ToString() =>
        Success ? $"图片翻译成功 {ImageBytes?.Length ?? 0}B/{ElapsedMs}ms"
                : $"[{FailureReason}] {ErrorMessage ?? UserMessage}";
}
