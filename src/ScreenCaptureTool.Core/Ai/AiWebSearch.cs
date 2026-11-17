using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Ai
{
    /// <summary>
    /// 搜索结果条目
    /// </summary>
    public sealed class AiSearchResult
    {
        public string Title = "";
        public string Url = "";
        public string Content = "";
        public string Engine = "";
        public string Thumbnail = "";
    }

    /// <summary>
    /// 联网搜索服务（多后端自动切换，NativeAOT 兼容，零反射解析）：
    /// 1. 用户配置的 SearXNG 端点（留空则跳过）
    /// 2. DuckDuckGo HTML（无需 JSON API，稳定）
    /// 3. Bing HTML（兜底）
    /// 说明：公共 SearXNG 实例大多开启了反爬/关闭 format=json，实测返回 HTML 挑战页，
    /// 故默认路径走 DuckDuckGo/Bing，避免联网搜索常年取不到结果。
    /// </summary>
    public sealed class AiWebSearchService
    {
        private static readonly Lazy<AiWebSearchService> _instance = new Lazy<AiWebSearchService>(() => new AiWebSearchService());
        public static AiWebSearchService Instance => _instance.Value;

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly HttpClient _httpClient;

        public AiWebSearchService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (s, cert, chain, errors) => true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        }

        /// <summary>
        /// 执行联网搜索：按 SearXNG(自建) → DuckDuckGo → Bing 顺序尝试，返回第一个有结果的来源。
        /// </summary>
        public async Task<List<AiSearchResult>> SearchAsync(string? endpoint, string query, int maxResults = 6, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<AiSearchResult>();

            // 1. 用户自建/自配 SearXNG 端点（非空才尝试）
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                try
                {
                    var r = await SearchSearxJsonAsync(endpoint.Trim().TrimEnd('/'), query, maxResults, ct).ConfigureAwait(false);
                    if (r.Count > 0)
                    {
                        AppLogger.Info($"[AiSearch] SearXNG success via '{endpoint}', results={r.Count}");
                        return r;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[AiSearch] SearXNG '{endpoint}' failed: {ex.GetType().Name}");
                }
            }

            // 2. DuckDuckGo HTML
            try
            {
                var r = await SearchDuckDuckGoHtmlAsync(query, maxResults, ct).ConfigureAwait(false);
                if (r.Count > 0)
                {
                    AppLogger.Info($"[AiSearch] DuckDuckGo success, results={r.Count}");
                    return r;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiSearch] DuckDuckGo failed: {ex.GetType().Name}");
            }

            // 3. Bing HTML 兜底
            try
            {
                var r = await SearchBingHtmlAsync(query, maxResults, ct).ConfigureAwait(false);
                if (r.Count > 0)
                {
                    AppLogger.Info($"[AiSearch] Bing success, results={r.Count}");
                    return r;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiSearch] Bing failed: {ex.GetType().Name}");
            }

            AppLogger.Warn($"[AiSearch] All search backends returned no results for query chars={query.Length}");
            return new List<AiSearchResult>();
        }

        /// <summary>
        /// 并行抓取每个结果的 og:image 作为网页缩略图（无则回退站点 favicon）。不阻塞主流程、失败静默。
        /// </summary>
        public async Task EnrichWithThumbnailsAsync(List<AiSearchResult> results, CancellationToken ct = default)
        {
            if (results == null || results.Count == 0) return;

            using var sem = new SemaphoreSlim(4);
            var tasks = new List<Task>();
            foreach (var r in results)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        r.Thumbnail = await FetchThumbnailAsync(r.Url, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 静默：缩略图失败不影响主流程
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // 静默
            }
        }

        private async Task<string> FetchThumbnailAsync(string url, CancellationToken ct)
        {
            string domain = GetDomain(url);
            string favicon = string.IsNullOrEmpty(domain)
                ? ""
                : "https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(domain) + "&sz=64";

            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return favicon;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return favicon;

                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                const int maxBytes = 300 * 1024;
                byte[] buffer = new byte[maxBytes];
                int read = 0;
                while (read < maxBytes)
                {
                    int chunk = await stream.ReadAsync(buffer, read, maxBytes - read, cts.Token).ConfigureAwait(false);
                    if (chunk <= 0) break;
                    read += chunk;
                }
                string html = Encoding.UTF8.GetString(buffer, 0, read);

                var og = Regex.Match(html,
                    @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                    RegexOptions.IgnoreCase);
                if (!og.Success)
                {
                    og = Regex.Match(html,
                        @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                        RegexOptions.IgnoreCase);
                }
                if (!og.Success)
                {
                    og = Regex.Match(html,
                        @"<link[^>]+rel=[""']image_src[""'][^>]+href=[""']([^""']+)[""']",
                        RegexOptions.IgnoreCase);
                }

                if (og.Success)
                {
                    string img = og.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(img))
                    {
                        if (img.StartsWith("//", StringComparison.Ordinal)) img = "https:" + img;
                        else if (img.StartsWith("/", StringComparison.Ordinal) && !string.IsNullOrEmpty(domain)) img = "https://" + domain + img;
                        return img;
                    }
                }
                return favicon;
            }
            catch
            {
                return favicon;
            }
        }

        private static string GetDomain(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return "";
            }
        }

        // ---------- SearXNG (JSON API) ----------

        private async Task<List<AiSearchResult>> SearchSearxJsonAsync(string endpoint, string query, int maxResults, CancellationToken ct)
        {
            string url = endpoint + "/search?q=" + Uri.EscapeDataString(query) +
                         "&format=json&language=zh-CN&safesearch=1&categories=general";

            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseSearxJson(body, maxResults);
        }

        private static List<AiSearchResult> ParseSearxJson(string json, int maxResults)
        {
            var list = new List<AiSearchResult>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        string url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(url)) continue;

                        string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        string content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        string engine = item.TryGetProperty("engine", out var e) ? e.GetString() ?? "" : "";

                        list.Add(new AiSearchResult { Title = title, Url = url, Content = content, Engine = engine });
                        if (list.Count >= maxResults) break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiSearch] ParseSearxJson failed: {ex.GetType().Name}");
            }
            return list;
        }

        // ---------- DuckDuckGo (HTML) ----------

        private async Task<List<AiSearchResult>> SearchDuckDuckGoHtmlAsync(string query, int maxResults, CancellationToken ct)
        {
            string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            }
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseDuckDuckGoHtml(body, maxResults);
        }

        private static List<AiSearchResult> ParseDuckDuckGoHtml(string html, int maxResults)
        {
            var list = new List<AiSearchResult>();
            try
            {
                var links = Regex.Matches(
                    html,
                    "<a rel=\"nofollow\" class=\"result__a\" href=\"([^\"]+)\"[^>]*>(.*?)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var snippets = Regex.Matches(
                    html,
                    "<a class=\"result__snippet\"[^>]*>(.*?)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                for (int i = 0; i < links.Count && list.Count < maxResults; i++)
                {
                    string href = links[i].Groups[1].Value;
                    string title = CleanText(links[i].Groups[2].Value);
                    string content = i < snippets.Count ? CleanText(snippets[i].Groups[1].Value) : "";
                    string url = DecodeDdgUrl(href);
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    list.Add(new AiSearchResult { Title = title, Url = url, Content = content, Engine = "duckduckgo" });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiSearch] ParseDuckDuckGoHtml failed: {ex.GetType().Name}");
            }
            return list;
        }

        private static string DecodeDdgUrl(string href)
        {
            try
            {
                int idx = href.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string v = href.Substring(idx + 5);
                    int amp = v.IndexOf('&');
                    if (amp >= 0) v = v.Substring(0, amp);
                    string decoded = Uri.UnescapeDataString(v);
                    if (decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return decoded;
                    }
                    return "";
                }
            }
            catch
            {
                return "";
            }
            return "";
        }

        // ---------- Bing (HTML) ----------

        private async Task<List<AiSearchResult>> SearchBingHtmlAsync(string query, int maxResults, CancellationToken ct)
        {
            string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            }
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseBingHtml(body, maxResults);
        }

        private static List<AiSearchResult> ParseBingHtml(string html, int maxResults)
        {
            var list = new List<AiSearchResult>();
            try
            {
                var blocks = Regex.Matches(
                    html,
                    "<li class=\"b_algo\"[^>]*>(.*?)</li>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                foreach (Match block in blocks)
                {
                    if (list.Count >= maxResults) break;
                    string inner = block.Groups[1].Value;

                    var h2 = Regex.Match(
                        inner,
                        "<h2[^>]*><a[^>]+href=\"([^\"]+)\"[^>]*>(.*?)</a></h2>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (!h2.Success) continue;

                    string href = h2.Groups[1].Value;
                    string title = CleanText(h2.Groups[2].Value);

                    var p = Regex.Match(inner, "<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    string content = p.Success ? CleanText(p.Groups[1].Value) : "";

                    string url = DecodeBingUrl(href);
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    list.Add(new AiSearchResult { Title = title, Url = url, Content = content, Engine = "bing" });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[AiSearch] ParseBingHtml failed: {ex.GetType().Name}");
            }
            return list;
        }

        private static string DecodeBingUrl(string href)
        {
            try
            {
                // Bing 跳转链接：https://www.bing.com/ck/a?...&u=a1<base64url>
                int idx = href.IndexOf("u=a1", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string b64 = href.Substring(idx + 4);
                    int amp = b64.IndexOf('&');
                    if (amp >= 0) b64 = b64.Substring(0, amp);

                    b64 = b64.Replace('-', '+').Replace('_', '/');
                    switch (b64.Length % 4)
                    {
                        case 2: b64 += "=="; break;
                        case 3: b64 += "="; break;
                    }
                    byte[] data = Convert.FromBase64String(b64);
                    return Encoding.UTF8.GetString(data);
                }

                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return href;
                }
            }
            catch
            {
                return "";
            }
            return "";
        }

        // ---------- 公共工具 ----------

        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = Regex.Replace(s, "\\s+", " ").Trim();
            return s;
        }

        /// <summary>
        /// 将搜索结果拼接为可直接追加到 Prompt 的上下文。
        /// </summary>
        public static string BuildSearchContextPrompt(List<AiSearchResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("以下是从搜索引擎检索到的相关网页结果（用于辅助回答，引用时请在对应句末标注来源编号 [1][2] 等）：");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"[{i + 1}] {r.Title}");
                sb.AppendLine($"URL: {r.Url}");
                string snippet = r.Content.Length > 200 ? r.Content.Substring(0, 200) + "..." : r.Content;
                sb.AppendLine($"摘要: {snippet}");
            }
            return sb.ToString();
        }
    }
}