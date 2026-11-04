using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Ai
{
    /// <summary>
    /// 大模型 AI 识图与模型服务 (NativeAOT 零反射兼容)
    /// </summary>
    public sealed class AiVisionService
    {
        private static readonly Lazy<AiVisionService> _instance = new Lazy<AiVisionService>(() => new AiVisionService());
        public static AiVisionService Instance => _instance.Value;

        private readonly HttpClient _httpClient;

        public AiVisionService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        /// <summary>
        /// 规范化 Base URL
        /// </summary>
        public static string NormalizeBaseUrl(string baseUrl, AiProtocolType protocol)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return protocol == AiProtocolType.Claude 
                    ? "https://api.anthropic.com/v1" 
                    : "https://api.openai.com/v1";
            }

            string clean = baseUrl.Trim().TrimEnd('/');
            if (protocol == AiProtocolType.OpenAI)
            {
                // 如果用户只输入了域名如 https://api.openai.com 或 https://api.deepseek.com，且不以 /v1 结尾
                // 大多数 OpenAI 兼容中转及官方 API 需要 /v1
                if (!clean.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Contains("/v1/", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Contains("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    clean += "/v1";
                }
            }
            else if (protocol == AiProtocolType.Claude)
            {
                if (!clean.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
                {
                    clean += "/v1";
                }
            }
            return clean;
        }

        private static string MaskApiKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "(empty)";
            if (key.Length <= 8) return "***";
            return key.Substring(0, 3) + "..." + key.Substring(key.Length - 4);
        }

        /// <summary>
        /// 测试连接并获取可用模型列表
        /// </summary>
        public async Task<AiConnectionTestResult> TestConnectionAndFetchModelsAsync(
            AiProtocolType protocol, string baseUrl, string apiKey, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string cleanUrl = NormalizeBaseUrl(baseUrl, protocol);
            var result = new AiConnectionTestResult();
            AppLogger.Info($"[AiVision] TestConnection started: Protocol={protocol}, BaseUrl='{cleanUrl}', KeyConfigured={!string.IsNullOrWhiteSpace(apiKey)}");

            try
            {
                if (protocol == AiProtocolType.OpenAI)
                {
                    string modelsUrl = cleanUrl + "/models";
                    using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    }

                    using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                    sw.Stop();
                    result.LatencyMs = sw.ElapsedMilliseconds;

                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var modelList = ParseOpenAiModels(body);
                        result.IsSuccess = true;
                        result.Models = modelList;
                        result.Message = $"连接成功 (耗时 {result.LatencyMs}ms)，获取到 {modelList.Count} 个模型";
                        AppLogger.Info($"[AiVision] TestConnection OpenAI succeeded: Latency={result.LatencyMs}ms, ModelsCount={modelList.Count}");
                    }
                    else
                    {
                        result.IsSuccess = false;
                        result.Message = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractErrorMessage(body)}";
                        AppLogger.Warn($"[AiVision] TestConnection OpenAI failed: HTTP {(int)resp.StatusCode}, 错误码={(int)resp.StatusCode}");
                    }
                }
                else // Claude
                {
                    // 尝试请求 /models
                    string modelsUrl = cleanUrl + "/models";
                    using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        req.Headers.Add("x-api-key", apiKey.Trim());
                    }
                    req.Headers.Add("anthropic-version", "2023-06-01");

                    using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                    sw.Stop();
                    result.LatencyMs = sw.ElapsedMilliseconds;

                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var modelList = ParseClaudeModels(body);
                        if (modelList.Count == 0)
                        {
                            modelList = GetDefaultClaudeModels();
                        }
                        result.IsSuccess = true;
                        result.Models = modelList;
                        result.Message = $"连接成功 (耗时 {result.LatencyMs}ms)，获取到 {modelList.Count} 个模型";
                        AppLogger.Info($"[AiVision] TestConnection Claude succeeded: Latency={result.LatencyMs}ms, ModelsCount={modelList.Count}");
                    }
                    else
                    {
                        // 部分 Anthropic 代理不支持 /models 接口，但支持实际的消息调用；提供默认列表并说明
                        var defaultModels = GetDefaultClaudeModels();
                        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            result.IsSuccess = true;
                            result.Models = defaultModels;
                            result.Message = $"服务在线 (耗时 {result.LatencyMs}ms，上游未开放 models 列表接口，已加载标准模型)";
                            AppLogger.Info($"[AiVision] TestConnection Claude 404 (endpoint unexposed, loaded defaults): Latency={result.LatencyMs}ms");
                        }
                        else
                        {
                            result.IsSuccess = false;
                            result.Message = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {ExtractErrorMessage(body)}";
                            AppLogger.Warn($"[AiVision] TestConnection Claude failed: HTTP {(int)resp.StatusCode}, 错误码={(int)resp.StatusCode}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.LatencyMs = sw.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.Message = "连接失败: " + ex.Message;
                AppLogger.Error($"[AiVision] TestConnection exception: {ex.GetType().Name}", ex);
            }

            return result;
        }

        /// <summary>
        /// 执行 AI 识图流式分析 (支持思考流与正文流分离)
        /// </summary>
        public async Task AnalyzeImageStreamAsync(
            AiVisionRequest request,
            Action<string> onChunkReceived,
            CancellationToken ct = default)
        {
            await AnalyzeImageStreamAsync(request, onChunkReceived, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行 AI 识图流式分析 (支持思考流与正文流分离)
        /// </summary>
        /// <param name="disableThinking">是否请求上游关闭深度思考（2026-10-01 加，issue #54）。
        /// 仅 OpenAI 兼容协议有效：Claude 默认就不思考（不开 <c>thinking</c> 块即不思考），故那一支不需要。</param>
        public async Task AnalyzeImageStreamAsync(
            AiVisionRequest request,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct = default,
            bool disableThinking = false)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ImageBase64))
            {
                throw new ArgumentException("图片数据为空");
            }

            string cleanUrl = NormalizeBaseUrl(request.BaseUrl, request.Protocol);
            string prompt = string.IsNullOrWhiteSpace(request.Prompt)
                ? AppSettings.DefaultAiCustomPrompt
                : request.Prompt.Trim();

            AppLogger.Info($"[AiVision] AnalyzeImageStreamAsync started: Protocol={request.Protocol}, Model='{request.Model}', PromptChars={prompt.Length}, ImageBytes={request.ImageBase64.Length} chars");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 3600)));
            var sw = Stopwatch.StartNew();
            try
            {
                if (request.Protocol == AiProtocolType.OpenAI)
                {
                    await StreamOpenAiVisionAsync(cleanUrl, request, prompt, onChunkReceived, onThinkingChunkReceived, timeout.Token, disableThinking).ConfigureAwait(false);
                }
                else
                {
                    // Claude：不写 thinking 块即默认不思考，无需额外字段（见 BuildClaudePayload）
                    await StreamClaudeVisionAsync(cleanUrl, request, prompt, onChunkReceived, onThinkingChunkReceived, timeout.Token).ConfigureAwait(false);
                }
                sw.Stop();
                AppLogger.Info($"[AiVision] AnalyzeImageStreamAsync finished successfully in {sw.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.Warn($"[AiVision] Image request timed out after {sw.ElapsedMilliseconds}ms");
                throw new TimeoutException("AI 请求超时，请重试或调整超时时间。");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                AppLogger.Warn($"[AiVision] AnalyzeImageStreamAsync cancelled by user after {sw.ElapsedMilliseconds}ms");
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppLogger.Error($"[AiVision] AnalyzeImageStreamAsync failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}", ex);
                throw;
            }
        }

        /// <summary>
        /// 执行纯文本流式对话（联网搜索补充回答等场景，无需图片）。
        /// </summary>
        public async Task AnalyzeTextStreamAsync(
            AiVisionRequest request,
            Action<string> onChunkReceived,
            CancellationToken ct = default)
        {
            await AnalyzeTextStreamAsync(request, onChunkReceived, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行纯文本流式对话（联网搜索补充回答等场景，无需图片）。
        /// </summary>
        public async Task AnalyzeTextStreamAsync(
            AiVisionRequest request,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct = default)
        {
            await AnalyzeTextStreamAsync(request, onChunkReceived, onThinkingChunkReceived, ct, disableThinking: false).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行纯文本流式对话（带"要快"档位的重载，2026-09-30 设计稿 2.3）。
        /// <paramref name="disableThinking"/>＝尽量不让模型进入深度思考：OpenAI 兼容协议一次带上
        /// 各家"关思考"约定字段（见 <see cref="BuildOpenAiTextPayload"/>），严格网关返回 400 时立即
        /// 用干净 payload 重试一次并记入 <see cref="FastFieldCache"/>（会话级，按 协议|BaseUrl|模型 记性，
        /// 之后不再发）；Claude 协议默认即关思考、只收敛 max_tokens。机制与具体模型无关——
        /// 认这些字段的服务商直接受益，忽略它们的网关无害，严格校验的走一次重试。
        /// </summary>
        public async Task AnalyzeTextStreamAsync(
            AiVisionRequest request,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct,
            bool disableThinking)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string cleanUrl = NormalizeBaseUrl(request.BaseUrl, request.Protocol);
            string prompt = string.IsNullOrWhiteSpace(request.Prompt) ? string.Empty : request.Prompt.Trim();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 3600)));
            var sw = Stopwatch.StartNew();
            try
            {
                if (request.Protocol == AiProtocolType.OpenAI)
                {
                    await StreamOpenAiTextAsync(cleanUrl, request, prompt, onChunkReceived, onThinkingChunkReceived, timeout.Token, disableThinking).ConfigureAwait(false);
                }
                else
                {
                    await StreamClaudeTextAsync(cleanUrl, request, prompt, onChunkReceived, onThinkingChunkReceived, timeout.Token, disableThinking).ConfigureAwait(false);
                }
                sw.Stop();
                AppLogger.Info($"[AiVision] AnalyzeTextStreamAsync finished in {sw.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.Warn($"[AiVision] Text request timed out after {sw.ElapsedMilliseconds}ms");
                throw new TimeoutException("AI 请求超时，请重试或调整超时时间。");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                AppLogger.Warn($"[AiVision] AnalyzeTextStreamAsync cancelled after {sw.ElapsedMilliseconds}ms");
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppLogger.Error($"[AiVision] AnalyzeTextStreamAsync failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}", ex);
                throw;
            }
        }

        /// <summary>
        /// 写入「关思考」扩展字段组合（OpenAI 兼容协议）。各家约定不一，一次全带上——
        /// 认的服务商直接关掉思考；忽略未知字段的网关无害；严格校验返回 400 的由调用方的干净重试兜底
        /// （并记入 <see cref="FastFieldCache"/> 本会话不再发）。
        /// <para>🔴 **本方法是这组字段的唯一真源**（2026-10-01 抽出，issue #54）：原先只长在
        /// <see cref="BuildOpenAiTextPayload"/> 里，识图路径（<see cref="BuildOpenAiPayload"/>）压根没有，
        /// 于是 AI 对话的主路径（带图）无论如何都关不掉思考。两处各写一份必然漂移，故收敛到这里。</para>
        /// </summary>
        private static void WriteNoThinkingFields(Utf8JsonWriter writer)
        {
            writer.WriteString("reasoning_effort", "low");            // OpenAI o 系 / gpt-5
            writer.WriteBoolean("enable_thinking", false);            // Qwen（DashScope/硅基）
            writer.WriteStartObject("thinking");                      // GLM-4.5+（智谱）、豆包（火山 Ark）
            writer.WriteString("type", "disabled");
            writer.WriteEndObject();
            writer.WriteStartObject("chat_template_kwargs");          // 自建 vLLM / SGLang
            writer.WriteBoolean("enable_thinking", false);
            writer.WriteEndObject();
        }

        private static string BuildOpenAiPayload(string model, string prompt, string mime, string base64,
                                                bool disableThinking = false)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("model", string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model.Trim());
                writer.WriteBoolean("stream", true);
                // 关思考时同样把上限收敛到 2048（与纯文本快速档同口径：要快就是要短答，
                // 顺带给"上游硬要思考"的最坏情形封个顶）
                writer.WriteNumber("max_tokens", disableThinking ? 2048 : 4096);
                if (disableThinking) WriteNoThinkingFields(writer);
                writer.WriteStartArray("messages");

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");

                // 文本提示词
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", prompt);
                writer.WriteEndObject();

                // 图像 Data URI
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", $"data:{mime};base64,{base64}");
                writer.WriteEndObject();
                writer.WriteEndObject();

                writer.WriteEndArray(); // content
                writer.WriteEndObject(); // user message

                writer.WriteEndArray(); // messages
                writer.WriteEndObject(); // root
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string BuildClaudePayload(string model, string prompt, string mime, string base64)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("model", string.IsNullOrWhiteSpace(model) ? "claude-3-5-sonnet-20241022" : model.Trim());
                writer.WriteNumber("max_tokens", 4096);
                writer.WriteBoolean("stream", true);
                writer.WriteStartArray("messages");

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");

                // 图像 Base64
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteStartObject("source");
                writer.WriteString("type", "base64");
                writer.WriteString("media_type", mime);
                writer.WriteString("data", base64);
                writer.WriteEndObject();
                writer.WriteEndObject();

                // 文本提示词
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", prompt);
                writer.WriteEndObject();

                writer.WriteEndArray(); // content
                writer.WriteEndObject(); // user message

                writer.WriteEndArray(); // messages
                writer.WriteEndObject(); // root
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>
        /// 关思考扩展字段的兼容闸门（会话级缓存）：键＝协议|BaseUrl|模型。
        /// true＝该上游接受"关思考"字段组合（直接沿用）；false＝曾返回 400（本会话不再发，走干净 payload）。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FastFieldCache = new();

        private static string BuildOpenAiTextPayload(string model, string prompt, bool withFastFields = false)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("model", string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model.Trim());
                writer.WriteBoolean("stream", true);
                // 快速档把上限收敛到 2048：要快的入口本就要求短答，
                // 顺带给"上游硬要思考"的最坏情形封个顶
                writer.WriteNumber("max_tokens", withFastFields ? 2048 : 4096);
                // 字段组合见 WriteNoThinkingFields（2026-10-01 抽出为唯一真源，识图路径共用同一份）
                if (withFastFields) WriteNoThinkingFields(writer);
                writer.WriteStartArray("messages");

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", prompt);
                writer.WriteEndObject();

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static string BuildClaudeTextPayload(string model, string prompt, int maxTokens = 4096)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("model", string.IsNullOrWhiteSpace(model) ? "claude-3-5-sonnet-20241022" : model.Trim());
                // Claude 协议思考默认关闭（不发 thinking 字段即不思考），快速档只收敛上限
                writer.WriteNumber("max_tokens", maxTokens);
                writer.WriteBoolean("stream", true);
                writer.WriteStartArray("messages");

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", prompt);
                writer.WriteEndObject();

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void ExtractOpenAiDelta(JsonElement root, out string? content, out string? reasoning)
        {
            content = null;
            reasoning = null;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return;

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return;

            if (delta.TryGetProperty("reasoning_content", out var rcElem) && rcElem.ValueKind == JsonValueKind.String)
            {
                reasoning = rcElem.GetString();
            }
            else if (delta.TryGetProperty("reasoning", out var rElem) && rElem.ValueKind == JsonValueKind.String)
            {
                reasoning = rElem.GetString();
            }
            else if (delta.TryGetProperty("thinking", out var tElem) && tElem.ValueKind == JsonValueKind.String)
            {
                reasoning = tElem.GetString();
            }

            if (delta.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.String)
            {
                content = contentElem.GetString();
            }
        }

        private static void ExtractClaudeDelta(JsonElement root, out string? content, out string? reasoning, out bool isStop)
        {
            content = null;
            reasoning = null;
            isStop = false;
            if (!root.TryGetProperty("type", out var typeElem) || typeElem.ValueKind != JsonValueKind.String)
                return;

            string? eventType = typeElem.GetString();
            if (eventType == "content_block_delta" &&
                root.TryGetProperty("delta", out var deltaElem) &&
                deltaElem.ValueKind == JsonValueKind.Object)
            {
                if (deltaElem.TryGetProperty("text", out var textElem) && textElem.ValueKind == JsonValueKind.String)
                {
                    content = textElem.GetString();
                }
                else if (deltaElem.TryGetProperty("thinking", out var thinkingElem) && thinkingElem.ValueKind == JsonValueKind.String)
                {
                    reasoning = thinkingElem.GetString();
                }
            }
            else if (eventType == "message_stop")
            {
                isStop = true;
            }
        }

        /// <summary>发一次识图请求（供关思考字段的 400 兼容重试复用；与纯文本侧
        /// <c>SendOpenAiTextOnceAsync</c> 同构）。</summary>
        private async Task<HttpResponseMessage> SendOpenAiVisionOnceAsync(
            string completionsUrl, AiVisionRequest request, string prompt, string mime,
            bool disableThinking, CancellationToken ct)
        {
            string json = BuildOpenAiPayload(request.Model, prompt, mime, request.ImageBase64, disableThinking);
            using var req = new HttpRequestMessage(HttpMethod.Post, completionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
            }
            return await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        private async Task StreamOpenAiVisionAsync(
            string cleanUrl,
            AiVisionRequest request,
            string prompt,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct,
            bool disableThinking = false)
        {
            string completionsUrl = cleanUrl + "/chat/completions";
            string mime = string.IsNullOrEmpty(request.ImageMimeType) ? "image/png" : request.ImageMimeType;

            // 关思考字段的兼容闸门：与纯文本侧共用同一个 FastFieldCache 与同一套"400 则记不可发并
            // 用干净 payload 立即重试一次"的口径（2026-10-01 补，issue #54）。
            // ⚠ 缓存键必须带上"vision"：同一个 BaseUrl+模型，识图与纯文本两条端点对扩展字段的
            //    严格程度可能不同，共用一个键会让其中一条的 400 把另一条也一起禁掉。
            string cacheKey = "openai|vision|" + cleanUrl + "|" + (request.Model ?? "").Trim();
            bool fastAllowed = disableThinking && FastFieldCache.GetOrAdd(cacheKey, true);

            HttpResponseMessage resp = await SendOpenAiVisionOnceAsync(
                completionsUrl, request, prompt, mime, fastAllowed, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && fastAllowed)
            {
                FastFieldCache[cacheKey] = false;
                AppLogger.Info($"[AiVision] 识图关思考字段被上游拒绝 (HTTP {(int)resp.StatusCode})，改用干净 payload 重试：{cacheKey}");
                resp.Dispose();
                resp = await SendOpenAiVisionOnceAsync(
                    completionsUrl, request, prompt, mime, disableThinking: false, ct).ConfigureAwait(false);
            }

            using var _respScope = resp;
            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"AI 接口请求失败 (HTTP {(int)resp.StatusCode}): {ExtractErrorMessage(errBody)}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            int chunkCount = 0;
            int totalChars = 0;
            int thinkingChars = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(":")) continue;

                if (line.StartsWith("data: "))
                {
                    string dataJson = line.Substring(6).Trim();
                    if (dataJson == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(dataJson);
                        ExtractOpenAiDelta(doc.RootElement, out string? text, out string? reasoning);
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            thinkingChars += reasoning.Length;
                            onThinkingChunkReceived?.Invoke(reasoning);
                        }
                        if (!string.IsNullOrEmpty(text))
                        {
                            chunkCount++;
                            totalChars += text.Length;
                            onChunkReceived(text);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Failed parsing OpenAI chunk: {ex.GetType().Name}");
                    }
                }
            }
            AppLogger.Info($"[AiVision] OpenAI SSE stream completed: Chunks={chunkCount}, TotalChars={totalChars}, ThinkingChars={thinkingChars}");
        }

        private async Task StreamClaudeVisionAsync(
            string cleanUrl,
            AiVisionRequest request,
            string prompt,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct)
        {
            string messagesUrl = cleanUrl + "/messages";
            string mime = string.IsNullOrEmpty(request.ImageMimeType) ? "image/png" : request.ImageMimeType;

            string json = BuildClaudePayload(request.Model, prompt, mime, request.ImageBase64);

            using var req = new HttpRequestMessage(HttpMethod.Post, messagesUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                req.Headers.Add("x-api-key", request.ApiKey.Trim());
            }
            req.Headers.Add("anthropic-version", "2023-06-01");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"Claude 接口请求失败 (HTTP {(int)resp.StatusCode}): {ExtractErrorMessage(errBody)}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            int chunkCount = 0;
            int totalChars = 0;
            int thinkingChars = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(":")) continue;

                if (line.StartsWith("data: "))
                {
                    string dataJson = line.Substring(6).Trim();
                    try
                    {
                        using var doc = JsonDocument.Parse(dataJson);
                        ExtractClaudeDelta(doc.RootElement, out string? text, out string? reasoning, out bool isStop);
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            thinkingChars += reasoning.Length;
                            onThinkingChunkReceived?.Invoke(reasoning);
                        }
                        if (!string.IsNullOrEmpty(text))
                        {
                            chunkCount++;
                            totalChars += text.Length;
                            onChunkReceived(text);
                        }
                        if (isStop) break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Failed parsing Claude chunk: {ex.GetType().Name}");
                    }
                }
            }
            AppLogger.Info($"[AiVision] Claude SSE stream completed: Chunks={chunkCount}, TotalChars={totalChars}, ThinkingChars={thinkingChars}");
        }

        private async Task StreamOpenAiTextAsync(
            string cleanUrl,
            AiVisionRequest request,
            string prompt,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct,
            bool disableThinking = false)
        {
            string completionsUrl = cleanUrl + "/chat/completions";

            // 关思考字段的兼容闸门：先按缓存决定发不发；400 则记"不可发"并用干净 payload 立即重试一次
            string cacheKey = "openai|" + cleanUrl + "|" + (request.Model ?? "").Trim();
            bool fastAllowed = disableThinking && FastFieldCache.GetOrAdd(cacheKey, true);

            HttpResponseMessage resp = await SendOpenAiTextOnceAsync(completionsUrl, request, prompt, fastAllowed, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && fastAllowed)
            {
                FastFieldCache[cacheKey] = false;
                AppLogger.Info($"[AiVision] Fast-mode fields rejected by upstream (HTTP {(int)resp.StatusCode}), retrying clean payload: {cacheKey}");
                resp.Dispose();
                resp = await SendOpenAiTextOnceAsync(completionsUrl, request, prompt, withFastFields: false, ct).ConfigureAwait(false);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new HttpRequestException($"AI 接口请求失败 (HTTP {(int)resp.StatusCode}): {ExtractErrorMessage(errBody)}");
                }

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) break;
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(":")) continue;

                    if (line.StartsWith("data: "))
                    {
                        string dataJson = line.Substring(6).Trim();
                        if (dataJson == "[DONE]") break;
                        try
                        {
                            using var doc = JsonDocument.Parse(dataJson);
                            ExtractOpenAiDelta(doc.RootElement, out string? text, out string? reasoning);
                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                onThinkingChunkReceived?.Invoke(reasoning);
                            }
                            if (!string.IsNullOrEmpty(text))
                            {
                                onChunkReceived(text);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug($"Failed parsing OpenAI chunk: {ex.GetType().Name}");
                        }
                    }
                }
            }
        }

        /// <summary>发一次 OpenAI 兼容文本补全（payload 按 <paramref name="withFastFields"/> 决定带不带关思考字段）。</summary>
        private async Task<HttpResponseMessage> SendOpenAiTextOnceAsync(
            string completionsUrl, AiVisionRequest request, string prompt, bool withFastFields, CancellationToken ct)
        {
            string json = BuildOpenAiTextPayload(request.Model, prompt, withFastFields);
            using var req = new HttpRequestMessage(HttpMethod.Post, completionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
            }

            return await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        private async Task StreamClaudeTextAsync(
            string cleanUrl,
            AiVisionRequest request,
            string prompt,
            Action<string> onChunkReceived,
            Action<string>? onThinkingChunkReceived,
            CancellationToken ct,
            bool disableThinking = false)
        {
            string messagesUrl = cleanUrl + "/messages";
            // Claude 思考默认关闭（不发 thinking 字段即不思考）；快速档只把上限收敛到 2048
            string json = BuildClaudeTextPayload(request.Model, prompt, disableThinking ? 2048 : 4096);

            using var req = new HttpRequestMessage(HttpMethod.Post, messagesUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                req.Headers.Add("x-api-key", request.ApiKey.Trim());
            }
            req.Headers.Add("anthropic-version", "2023-06-01");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"Claude 接口请求失败 (HTTP {(int)resp.StatusCode}): {ExtractErrorMessage(errBody)}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(":")) continue;

                if (line.StartsWith("data: "))
                {
                    string dataJson = line.Substring(6).Trim();
                    try
                    {
                        using var doc = JsonDocument.Parse(dataJson);
                        ExtractClaudeDelta(doc.RootElement, out string? text, out string? reasoning, out bool isStop);
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            onThinkingChunkReceived?.Invoke(reasoning);
                        }
                        if (!string.IsNullOrEmpty(text))
                        {
                            onChunkReceived(text);
                        }
                        if (isStop) break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Failed parsing Claude chunk: {ex.GetType().Name}");
                    }
                }
            }
        }

        private static List<AiModelItem> ParseOpenAiModels(string json)
        {
            var list = new List<AiModelItem>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElem))
                        {
                            string? id = idElem.GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                string ownedBy = item.TryGetProperty("owned_by", out var ob) ? ob.GetString() ?? "" : "";
                                list.Add(new AiModelItem
                                {
                                    Id = id,
                                    DisplayName = id,
                                    Description = ownedBy
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"ParseOpenAiModels failed: {ex.GetType().Name}");
            }

            // 上游（如 OneAPI）常按渠道添加顺序返回，先按型号自然排序让同系列相邻
            if (list.Count > 1)
                list.Sort((a, b) => NaturalModelCompare(a.Id, b.Id));

            return list;
        }

        private static List<AiModelItem> ParseClaudeModels(string json)
        {
            var list = new List<AiModelItem>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElem))
                        {
                            string? id = idElem.GetString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                string displayName = item.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? id : id;
                                list.Add(new AiModelItem
                                {
                                    Id = id,
                                    DisplayName = displayName
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            if (list.Count > 1)
                list.Sort((a, b) => NaturalModelCompare(a.Id, b.Id));

            return list;
        }

        /// <summary>
        /// 模型型号自然排序比较器：数字段按数值比较（gpt-4 &lt; gpt-4.1 &lt; gpt-4o），
        /// 字母不区分大小写，使同系列模型（gpt-* / gemini-* / claude-* …）彼此相邻。
        /// </summary>
        public static int NaturalModelCompare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int xi = i, yj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;

                    // 跳过前导零后按数值比较：长度不同则长的大，相同则逐字符比较
                    int xs = xi;
                    int ys = yj;
                    while (xs < i - 1 && x[xs] == '0') xs++;
                    while (ys < j - 1 && y[ys] == '0') ys++;

                    int xLen = i - xs, yLen = j - ys;
                    if (xLen != yLen) return xLen.CompareTo(yLen);
                    int cmp = string.CompareOrdinal(x, xs, y, ys, xLen);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (cmp != 0) return cmp;
                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }

        public static List<AiModelItem> GetDefaultClaudeModels()
        {
            return new List<AiModelItem>
            {
                new AiModelItem { Id = "claude-3-5-sonnet-20241022", DisplayName = "claude-3-5-sonnet-20241022 (推荐)" },
                new AiModelItem { Id = "claude-3-5-haiku-20241022", DisplayName = "claude-3-5-haiku-20241022 (极速)" },
                new AiModelItem { Id = "claude-3-opus-20240229", DisplayName = "claude-3-opus-20240229" },
                new AiModelItem { Id = "claude-3-sonnet-20240229", DisplayName = "claude-3-sonnet-20240229" }
            };
        }

        private static string ExtractErrorMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return "无响应内容";
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var errorElem))
                {
                    if (errorElem.ValueKind == JsonValueKind.String)
                    {
                        return errorElem.GetString() ?? responseBody;
                    }
                    if (errorElem.TryGetProperty("message", out var msgElem))
                    {
                        return msgElem.GetString() ?? responseBody;
                    }
                }
                if (root.TryGetProperty("message", out var directMsg))
                {
                    return directMsg.GetString() ?? responseBody;
                }
            }
            catch { }

            return responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody;
        }
    }
}
