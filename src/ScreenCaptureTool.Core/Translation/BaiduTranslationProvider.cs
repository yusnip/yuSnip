#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 百度翻译开放平台 API 封装（大模型文本翻译 API + API Key 鉴权）。
///
/// 文本翻译接口：POST https://fanyi-api.baidu.com/ait/api/aiTextTranslate
/// 图片翻译接口：POST https://fanyi-api.baidu.com/ait/api/picture/translate
///
/// 鉴权规则（两个接口**完全相同**，同一把 API Key 通吃）：
///   Header: Authorization: Bearer {apiKey}
///   Body: JSON
///
/// ⚠ 图片翻译接的是 **V2.0**。旧 V1.0（<c>api/trans/sdk/picture</c>，multipart + appid+密钥 MD5 签名）
/// 是另一套端点与鉴权，交叉使用必然 <c>54001 Invalid Sign</c>；本次不写、不留 V1 代码。
///
/// 官方文档：
///   大模型文本翻译 https://api.fanyi.baidu.com/doc/21
///   图片翻译 V2.0 https://fanyi-api.baidu.com/product/233
///   API Key 管理  https://api.fanyi.baidu.com/manage/apiKey
///   错误码大全    https://api.fanyi.baidu.com/doc/26
/// </summary>
public sealed class BaiduTranslationProvider : ITranslationProvider
{
    private const string TextApiUrl = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";
    private const string ImageApiUrl = "https://fanyi-api.baidu.com/ait/api/picture/translate";

    // 图片翻译 V2.0 的固定取值，按用户口径全部写死不开放配置（计划 §3.1）：
    // paste=1 整图贴合——不传就没有成品图；view_type=1 高精擦除（官方 demo 同值）；
    // model_type 固定 nmt。⚠ 实测 view_type=0 与 1 在浅底 UI 上字节相同、暗底小字上 1 少翻一行，
    // 若验收发现擦除或漏行问题，改这一处常量即可，不要加设置项。
    private const int ImagePaste = 1;
    private const int ImageViewType = 1;
    private const int ImageNeedIntervene = 0;
    private const string ImageModelType = "nmt";

    private readonly HttpClient _httpClient;
    private readonly string _appId;
    private readonly string _apiKey;
    private readonly int _timeoutMs;

    /// <summary>
    /// 创建百度翻译提供者。
    /// </summary>
    /// <param name="appId">百度翻译 APP ID。</param>
    /// <param name="apiKey">百度翻译 API Key。</param>
    /// <param name="httpClient">可选，外部传入的 HttpClient（便于复用连接池和单元测试）。</param>
    /// <param name="timeoutMs">请求超时毫秒数。</param>
    public BaiduTranslationProvider(string appId, string apiKey, HttpClient? httpClient = null, int timeoutMs = 30_000)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("appId 不能为空", nameof(appId));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey 不能为空", nameof(apiKey));

        _appId = appId.Trim();
        _apiKey = apiKey.Trim();
        _timeoutMs = timeoutMs;
        _httpClient = httpClient ?? new HttpClient();
        // Only set Timeout on HttpClient we own; a shared external HttpClient
        // locks its properties after the first request, causing InvalidOperationException.
        // 共享 client 的场景下超时改由 TranslateTextAsync 内按请求的 CancellationTokenSource 生效。
        if (httpClient == null)
            _httpClient.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
    }

    public TranslationProvider ProviderType => TranslationProvider.Baidu;

    // ── 公开接口 ──────────────────────────────────────────────

    public async Task<(bool ok, string? errorMessage)> ValidateAsync(CancellationToken ct = default)
    {
        // 翻译一个简单单词验证凭证可用性
        var result = await TranslateTextAsync("Hello", TranslationLanguage.Auto, TranslationLanguage.ChineseSimplified, ct);
        if (result.Success)
            return (true, null);
        return (false, result.ErrorMessage ?? "未知错误");
    }

    public async Task<TextTranslationResult> TranslateTextAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return TextTranslationResult.Ok(string.Empty);

        try
        {
            string fromCode = TranslationLanguage.GetBaiduCode(sourceLang);
            string toCode = TranslationLanguage.GetBaiduCode(targetLang);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("appid", _appId);
                writer.WriteString("q", text);
                writer.WriteString("from", fromCode);
                writer.WriteString("to", toCode);
                writer.WriteString("model_type", "llm");
                writer.WriteEndObject();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TextApiUrl)
            {
                Content = new ByteArrayContent(ms.ToArray())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            // 超时按请求挂：门面注入的是共享 HttpClient，其 Timeout 属性在首个请求后
            // 就锁死不可改（见构造函数注释），若只靠它，配置里的超时完全是死代码，
            // 真实上限是 HttpClient 默认的 100 秒——观感是"翻译中…"卡近两分钟才报错。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);

            string json;
            try
            {
                using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 走到这里说明只有内部计时器到点（外部取消归下一节的 rethrow 分支）
                return TextTranslationResult.Fail("TIMEOUT", $"翻译请求超时（{_timeoutMs / 1000} 秒），请检查网络后重试");
            }

            return ParseTextResponse(json, text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("BaiduTranslationProvider.TranslateTextAsync 异常", ex);
            return TextTranslationResult.Fail("NETWORK", "网络请求失败: " + ex.Message);
        }
    }

    // ── 图片翻译（V2.0）──────────────────────────────────────

    public async Task<ImageTranslationResult> TranslateImageAsync(
        byte[] imageBytes,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return ImageTranslationResult.Fail(ImageTranslationFailure.Unknown, "图片数据为空");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string fromCode = TranslationLanguage.GetBaiduCode(sourceLang);
            string toCode = TranslationLanguage.GetBaiduCode(targetLang);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("from", fromCode);
                writer.WriteString("to", toCode);
                writer.WriteString("appid", _appId);
                // content = 图片 base64，不含 data:image/...;base64, 前缀
                writer.WriteString("content", Convert.ToBase64String(imageBytes));
                writer.WriteNumber("paste", ImagePaste);
                writer.WriteNumber("need_intervene", ImageNeedIntervene);
                writer.WriteNumber("view_type", ImageViewType);
                writer.WriteString("model_type", ImageModelType);
                writer.WriteEndObject();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, ImageApiUrl)
            {
                Content = new ByteArrayContent(ms.ToArray())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            // 超时按请求挂，理由同 TranslateTextAsync 的注释。
            // 实测 4K 全屏 8.8s（普通图 1.0~1.2s），默认 30s 够用，故不单独放宽。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);

            string json;
            try
            {
                using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ImageTranslationResult.Fail(ImageTranslationFailure.Timeout,
                    $"翻译请求超时（{_timeoutMs / 1000} 秒），请检查网络后重试", sw.ElapsedMilliseconds);
            }

            return ParseImageResponse(json, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("BaiduTranslationProvider.TranslateImageAsync 异常", ex);
            return ImageTranslationResult.Fail(ImageTranslationFailure.Network, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 解析图片翻译 V2.0 响应。
    /// 🔴 成败**只看 body 的 error_code，不看 HTTP 状态码**：实测产商连 <c>59004 qps limit</c>
    /// 都回 HTTP 200（210ms 即回），按"非 2xx 才算失败"的写法会静默吞掉失败。
    /// </summary>
    private static ImageTranslationResult ParseImageResponse(string json, long elapsedMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 实测 V2 的 error_code 是**数字**（59004），V1 与文本接口是字符串（"0"）→ 两种都兼容
            if (root.TryGetProperty("error_code", out var codeProp))
            {
                string code = codeProp.ValueKind == JsonValueKind.Number
                    ? codeProp.GetInt32().ToString()
                    : codeProp.GetString() ?? "UNKNOWN";
                if (code != "0")
                {
                    string raw = root.TryGetProperty("error_msg", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    return ImageTranslationResult.Fail(MapImageFailure(code), $"[{code}] {raw}", elapsedMs);
                }
            }

            string? detected = root.TryGetProperty("from", out var fromProp) ? fromProp.GetString() : null;
            // 归一成内部语言码：门面要靠它判断"图上文字已属目标语"从而切次级目标语
            if (!string.IsNullOrEmpty(detected))
                detected = TranslationLanguage.ToInternalCode(detected, TranslationProvider.Baidu);

            string? pasteImg = null;
            if (root.TryGetProperty("paste_img", out var imgProp) &&
                imgProp.ValueKind == JsonValueKind.String)
                pasteImg = imgProp.GetString();

            if (!string.IsNullOrEmpty(pasteImg))
            {
                // 成品图按产商原样解码，不重编码：重编码只会额外糊一层压缩噪声
                return ImageTranslationResult.Ok(Convert.FromBase64String(pasteImg), detected, elapsedMs);
            }

            // 无 paste_img：图上没文字时产商返回的就是空 contents，报"未识别到文字"比"格式异常"有用
            int blocks = root.TryGetProperty("contents", out var contentsProp) &&
                         contentsProp.ValueKind == JsonValueKind.Array
                ? contentsProp.GetArrayLength() : 0;
            return blocks == 0
                ? ImageTranslationResult.Fail(ImageTranslationFailure.NoText, "error_code=0 且 contents 为空", elapsedMs)
                : ImageTranslationResult.Fail(ImageTranslationFailure.Unknown, "响应中缺少 paste_img", elapsedMs);
        }
        catch (Exception ex)
        {
            return ImageTranslationResult.Fail(ImageTranslationFailure.Unknown,
                "解析图片翻译响应失败: " + ex.Message, elapsedMs);
        }
    }

    /// <summary>
    /// 把图片翻译 V2.0 错误码归到统一的失败类别。
    /// 码表取自产商文档页（本轮 59004/54001/52003 为实测）；未收录的码走 Unknown 并保留原始 error_msg。
    /// </summary>
    private static ImageTranslationFailure MapImageFailure(string code) => code switch
    {
        "54001" or "55002" or "55007" => ImageTranslationFailure.AuthFailed,
        "55006" => ImageTranslationFailure.ServiceNotEnabled,
        "54004" => ImageTranslationFailure.Quota,
        "55003" => ImageTranslationFailure.NoText,
        "55005" => ImageTranslationFailure.TooLarge,
        "55001" => ImageTranslationFailure.LanguageUnsupported,
        "55004" => ImageTranslationFailure.Upstream,
        "59004" or "54003" => ImageTranslationFailure.RateLimited,
        "53001" or "52003" => ImageTranslationFailure.Server,
        _ => ImageTranslationFailure.Unknown,
    };

    // ── 响应解析 ──────────────────────────────────────────────

    private static TextTranslationResult ParseTextResponse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 百度错误响应格式: { "error_code": "54001", "error_msg": "Invalid Sign" }
            // error_code 可能是字符串也可能是数字，两种都兼容
            if (root.TryGetProperty("error_code", out var errorCodeProp))
            {
                string errorCode = errorCodeProp.ValueKind == JsonValueKind.Number
                    ? errorCodeProp.GetInt32().ToString()
                    : errorCodeProp.GetString() ?? "UNKNOWN";
                string errorMsg = root.TryGetProperty("error_msg", out var msgProp)
                    ? msgProp.GetString() ?? "百度翻译返回错误"
                    : "百度翻译返回错误";
                return TextTranslationResult.Fail(errorCode, FormatBaiduError(errorCode, errorMsg));
            }
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
            {
                string errorCode = errorProp.GetString() ?? "UNKNOWN";
                string errorMsg = root.TryGetProperty("error_msg", out var msgProp)
                    ? msgProp.GetString() ?? "百度翻译返回错误"
                    : "百度翻译返回错误";
                return TextTranslationResult.Fail(errorCode, FormatBaiduError(errorCode, errorMsg));
            }

            // 正常响应: { "from": "en", "to": "zh", "trans_result": [{ "src": "Hello", "dst": "你好" }] }
            string? from = root.TryGetProperty("from", out var fromProp) ? fromProp.GetString() : null;

            if (root.TryGetProperty("trans_result", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                var pairs = new System.Collections.Generic.List<TranslationTextPair>(); int sourceOffset = 0;
                foreach (var item in resultsProp.EnumerateArray())
                {
                    if (!item.TryGetProperty("dst", out var dstProp)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    string target = dstProp.GetString() ?? string.Empty;
                    if (item.TryGetProperty("src", out var srcProp) && srcProp.ValueKind == JsonValueKind.String)
                    {
                        string original = srcProp.GetString() ?? "";
                        int start = original.Length == 0 ? -1 : source.IndexOf(original, sourceOffset, StringComparison.Ordinal);
                        if (start >= 0 && !string.IsNullOrWhiteSpace(target))
                        { pairs.Add(new(start, original.Length, sb.Length, target.Length)); sourceOffset = start + original.Length; }
                    }
                    sb.Append(target);
                }
                return TextTranslationResult.Ok(sb.ToString(), from, pairs);
            }

            return TextTranslationResult.Fail("PARSE_ERROR", "百度翻译响应格式异常: " + json);
        }
        catch (Exception ex)
        {
            return TextTranslationResult.Fail("PARSE_ERROR", "解析百度翻译响应失败: " + ex.Message);
        }
    }

    // ── 工具方法 ──────────────────────────────────────────────

    /// <summary>将百度错误码映射为用户友好的中文提示。</summary>
    private static string FormatBaiduError(string errorCode, string rawMessage)
    {
        return errorCode switch
        {
            "54000" => "必填参数为空，请检查 APP ID 和翻译内容",
            "54001" => "API Key 鉴权失败，请检查 API Key 和 APP ID 是否正确匹配",
            "54002" => "请检查 APP ID 是否正确",
            "54003" => "访问频率受限，请稍后重试",
            "54004" => "账户余额不足",
            "54005" => "请求文本过长（单次最大 6000 字符）",
            "52001" => "请求超时，请重试",
            "52002" => "系统错误，请重试",
            "52003" => "未授权用户，请确保已在百度开放平台开通「大模型文本翻译」服务",
            "13000001" => "服务异常，请稍后重试",
            _ => $"百度翻译错误 [{errorCode}]: {rawMessage}",
        };
    }
}
