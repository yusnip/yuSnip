#nullable enable

using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Translation;

/// <summary>
/// 有道智云翻译 API 封装。
///
/// 文本翻译接口：POST https://openapi.youdao.com/api
/// 图片翻译接口：POST https://openapi.youdao.com/ocrtransapi（type=1 收 base64，render=1 才返回成品图）
///
/// 签名规则（signType=v3）：
///   input = q (长度 ≤ 20) 或 q[:10] + len(q) + q[-10:] (长度 > 20)
///   sign  = SHA256( appKey + input + salt + curtime + appSecret )
///   ⚠ 签名用的 q 是**未 URL encode** 的原串，encode 只发生在表单序列化时
///
/// 官方文档：
///   文本翻译 https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html
///   图片翻译 https://ai.youdao.com/DOCSIRMA/html/learn/api/tpfy/index.html
///   错误码   https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html#section-9
/// </summary>
public sealed class YoudaoTranslationProvider : ITranslationProvider
{
    private const string TextApiUrl = "https://openapi.youdao.com/api";
    private const string ImageApiUrl = "https://openapi.youdao.com/ocrtransapi";

    private readonly HttpClient _httpClient;
    private readonly string _appKey;
    private readonly string _appSecret;
    private readonly int _timeoutMs;

    /// <summary>
    /// 创建有道翻译提供者。
    /// </summary>
    /// <param name="appKey">有道智云应用 ID。</param>
    /// <param name="appSecret">有道智云应用密钥。</param>
    /// <param name="httpClient">可选，外部传入的 HttpClient。</param>
    /// <param name="timeoutMs">请求超时毫秒数。</param>
    public YoudaoTranslationProvider(string appKey, string appSecret, HttpClient? httpClient = null, int timeoutMs = 30_000)
    {
        if (string.IsNullOrWhiteSpace(appKey)) throw new ArgumentException("appKey 不能为空", nameof(appKey));
        if (string.IsNullOrWhiteSpace(appSecret)) throw new ArgumentException("appSecret 不能为空", nameof(appSecret));

        _appKey = appKey.Trim();
        _appSecret = appSecret.Trim();
        _timeoutMs = timeoutMs;
        _httpClient = httpClient ?? new HttpClient();
        // Only set Timeout on HttpClient we own; a shared external HttpClient
        // locks its properties after the first request, causing InvalidOperationException.
        // 共享 client 的场景下超时改由 TranslateTextAsync 内按请求的 CancellationTokenSource 生效。
        if (httpClient == null)
            _httpClient.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
    }

    public TranslationProvider ProviderType => TranslationProvider.Youdao;

    // ── 公开接口 ──────────────────────────────────────────────

    public async Task<(bool ok, string? errorMessage)> ValidateAsync(CancellationToken ct = default)
    {
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
            string salt = GenerateSalt();
            string curtime = GetCurrentUnixTimeSeconds();
            string fromCode = TranslationLanguage.GetYoudaoCode(sourceLang);
            string toCode = TranslationLanguage.GetYoudaoCode(targetLang);
            string input = TruncateInput(text);
            string sign = ComputeSha256(_appKey + input + salt + curtime + _appSecret);

            var parameters = new System.Collections.Generic.Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = fromCode,
                ["to"] = toCode,
                ["appKey"] = _appKey,
                ["salt"] = salt,
                ["signType"] = "v3",
                ["curtime"] = curtime,
                ["sign"] = sign,
            };

            using var content = new FormUrlEncodedContent(parameters);

            // 超时按请求挂：门面注入的是共享 HttpClient，其 Timeout 属性在首个请求后
            // 就锁死不可改（见构造函数注释），若只靠它，配置里的超时完全是死代码，
            // 真实上限是 HttpClient 默认的 100 秒。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);

            string json;
            try
            {
                using var response = await _httpClient.PostAsync(TextApiUrl, content, cts.Token).ConfigureAwait(false);
                json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 走到这里说明只有内部计时器到点（外部取消归下方 rethrow 分支）
                return TextTranslationResult.Fail("TIMEOUT", $"翻译请求超时（{_timeoutMs / 1000} 秒），请检查网络后重试");
            }

            return ParseTextResponse(json, text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("YoudaoTranslationProvider.TranslateTextAsync 异常", ex);
            return TextTranslationResult.Fail("NETWORK", "网络请求失败: " + ex.Message);
        }
    }

    // ── 图片翻译 ──────────────────────────────────────────────

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
            // type=1 → q 收图片 base64，且不能带 data:image/...;base64, 前缀（带了产商解不出来）
            string q = Convert.ToBase64String(imageBytes);
            string salt = GenerateSalt();
            string curtime = GetCurrentUnixTimeSeconds();
            string input = TruncateInput(q);
            string sign = ComputeSha256(_appKey + input + salt + curtime + _appSecret);

            var parameters = new System.Collections.Generic.Dictionary<string, string>
            {
                ["type"] = "1",
                ["q"] = q,
                ["from"] = TranslationLanguage.GetYoudaoCode(sourceLang),
                ["to"] = TranslationLanguage.GetYoudaoCode(targetLang),
                ["appKey"] = _appKey,
                ["salt"] = salt,
                ["curtime"] = curtime,
                ["signType"] = "v3",
                ["sign"] = sign,
                ["docType"] = "json",
                // render=1 才回成品图（render_image）。少了这个字段响应里就只有识别结果、没有图
                ["render"] = "1",
            };

            using var content = new FormUrlEncodedContent(parameters);

            // 超时按请求挂：门面注入的是共享 HttpClient，其 Timeout 在首个请求后就锁死不可改
            // （见构造函数注释），只靠它会让配置里的超时成为死代码。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);

            string json;
            try
            {
                using var response = await _httpClient.PostAsync(ImageApiUrl, content, cts.Token).ConfigureAwait(false);
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
            AppLogger.Error("YoudaoTranslationProvider.TranslateImageAsync 异常", ex);
            return ImageTranslationResult.Fail(ImageTranslationFailure.Network, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static ImageTranslationResult ParseImageResponse(string json, long elapsedMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("errorCode", out var codeProp))
            {
                string code = codeProp.ValueKind == JsonValueKind.Number
                    ? codeProp.GetInt32().ToString()
                    : codeProp.GetString() ?? "UNKNOWN";
                if (code != "0")
                {
                    string raw = root.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    return ImageTranslationResult.Fail(MapImageFailure(code),
                        $"[{code}] {raw}", elapsedMs);
                }
            }

            string? detected = root.TryGetProperty("lanFrom", out var lanProp) ? lanProp.GetString() : null;
            // 归一成内部语言码：门面要靠它判断"图上文字已属目标语"从而切次级目标语
            if (!string.IsNullOrEmpty(detected))
                detected = TranslationLanguage.ToInternalCode(detected, TranslationProvider.Youdao);

            string? rendered = null;
            if (root.TryGetProperty("render_image", out var imgProp) &&
                imgProp.ValueKind == JsonValueKind.String)
                rendered = imgProp.GetString();

            if (!string.IsNullOrEmpty(rendered))
            {
                // 成品图按产商原样解码，不重编码：重编码只会额外糊一层压缩噪声
                return ImageTranslationResult.Ok(Convert.FromBase64String(rendered), detected, elapsedMs);
            }

            // 成功码但没成品图：图上没文字时产商给的就是空结果，报"未识别到文字"比报"格式异常"更有用
            int regions = root.TryGetProperty("resRegions", out var regionsProp) &&
                          regionsProp.ValueKind == JsonValueKind.Array
                ? regionsProp.GetArrayLength() : 0;
            return regions == 0
                ? ImageTranslationResult.Fail(ImageTranslationFailure.NoText, "errorCode=0 且无识别区域", elapsedMs)
                : ImageTranslationResult.Fail(ImageTranslationFailure.Unknown, "响应中缺少 render_image", elapsedMs);
        }
        catch (Exception ex)
        {
            return ImageTranslationResult.Fail(ImageTranslationFailure.Unknown,
                "解析图片翻译响应失败: " + ex.Message, elapsedMs);
        }
    }

    /// <summary>把有道错误码归到统一的失败类别。未收录的码走 Unknown 并保留原始信息。</summary>
    private static ImageTranslationFailure MapImageFailure(string code) => code switch
    {
        "102" => ImageTranslationFailure.LanguageUnsupported,
        // 110 = 应用没绑定该服务的有效实例（本轮实测踩过：凭证与签名都对，缺的是控制台绑定）
        "108" or "111" or "112" => ImageTranslationFailure.AuthFailed,
        "110" or "304" => ImageTranslationFailure.ServiceNotEnabled,
        "401" => ImageTranslationFailure.Quota,
        "411" or "1411" or "5411" => ImageTranslationFailure.RateLimited,
        "113" => ImageTranslationFailure.NoText,
        "500" or "503" or "504" => ImageTranslationFailure.Server,
        _ => ImageTranslationFailure.Unknown,
    };

    // ── 响应解析 ──────────────────────────────────────────────

    private static TextTranslationResult ParseTextResponse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 有道错误码: { "errorCode": "202" }
            if (root.TryGetProperty("errorCode", out var codeProp))
            {
                string errorCode = codeProp.GetString() ?? "UNKNOWN";
                if (errorCode != "0")
                    return TextTranslationResult.Fail(errorCode, FormatYoudaoError(errorCode));
            }

            // 正常响应: { "translation": ["你好"], "query": "Hello", "l": "en-2-zh" }
            if (root.TryGetProperty("translation", out var transProp) && transProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in transProp.EnumerateArray())
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(item.GetString());
                }

                // 尝试获取检测到的源语言
                string? detected = null;
                if (root.TryGetProperty("l", out var lProp))
                {
                    string? l = lProp.GetString();
                    if (!string.IsNullOrEmpty(l) && l.Contains('-'))
                        detected = l.Split('-')[0];
                }

                string target = sb.ToString();
                // 此接口没有句子级位置；仅单句的完整 query 可以精确配对，多句整篇不猜行号。
                var pairs = System.Array.Empty<TranslationTextPair>();
                if (root.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String && query.GetString() == source
                    && TranslationService.SplitSentences(source).Count == 1 && TranslationService.SplitSentences(target).Count == 1)
                    pairs = new[] { new TranslationTextPair(0, source.Length, 0, target.Length) };
                return TextTranslationResult.Ok(target, detected, pairs);
            }

            return TextTranslationResult.Fail("PARSE_ERROR", "有道翻译响应格式异常: " + json);
        }
        catch (Exception ex)
        {
            return TextTranslationResult.Fail("PARSE_ERROR", "解析有道翻译响应失败: " + ex.Message);
        }
    }

    // ── 签名与工具方法 ────────────────────────────────────────

    /// <summary>
    /// 有道签名截断规则：当文本长度 > 20 时，
    /// input = q[:10] + len(q) + q[-10:]；否则 input = q。
    /// </summary>
    private static string TruncateInput(string text)
    {
        if (text.Length <= 20)
            return text;
        return text.Substring(0, 10) + text.Length.ToString() + text.Substring(text.Length - 10);
    }

    private static string GenerateSalt()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string GetCurrentUnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    }

    private static string ComputeSha256(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>将有道错误码映射为用户友好的中文提示。</summary>
    private static string FormatYoudaoError(string errorCode)
    {
        return errorCode switch
        {
            "101" => "缺少必填参数，请检查 APP ID 和密钥",
            "102" => "不支持的语言类型",
            "103" => "翻译文本过长",
            "104" => "不支持的 API 类型",
            "105" => "不支持的签名类型",
            "106" => "不支持的响应类型",
            "107" => "不支持的传输加密类型",
            "108" => "应用 ID 无效，请检查 APP ID 是否正确",
            "109" => "BatchRequest 格式不正确",
            "110" => "无相关服务的有效实例",
            "111" => "开发者账号无效",
            "112" => "请求服务无效",
            "113" => "查询为空",
            "201" => "解密失败，可能是密钥错误",
            "202" => "签名检验失败，请检查 APP ID 和密钥是否正确",
            "203" => "访问 IP 地址不在可访问 IP 列表中",
            "205" => "请求的接口与平台的接入方式不一致",
            "206" => "请求时间不在有效期内",
            "207" => "重放请求",
            "301" => "辞典查询失败",
            "302" => "小语种查询失败",
            "303" => "服务的并发数超过限制",
            "304" => "服务未开通，请在有道智云开通翻译服务",
            "401" => "账户已经欠费",
            "402" => "offlinesign 不可用",
            "411" => "访问频率受限，请稍后重试",
            "412" => "超过最大请求字符数",
            _ => $"有道翻译错误 [{errorCode}]",
        };
    }
}
