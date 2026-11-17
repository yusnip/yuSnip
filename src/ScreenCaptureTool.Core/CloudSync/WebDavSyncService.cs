using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.CloudSync
{
    /// <summary>
    /// WebDAV 配置同步服务。
    /// 使用标准 WebDAV 协议（PUT/GET/PROPFIND）上传和下载配置文件。
    /// 认证方式：HTTP Basic Auth（用户名 + 密码/Token）。
    /// </summary>
    public sealed class WebDavSyncService : IDisposable
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly string _baseUrl;
        private readonly string _username;
        private readonly string _password;

        /// <summary>
        /// 创建 WebDAV 同步服务实例。
        /// </summary>
        /// <param name="baseUrl">WebDAV 根地址（如 https://example.com/dav/path）</param>
        /// <param name="username">WebDAV 用户名</param>
        /// <param name="password">WebDAV 密码或 Token</param>
        public WebDavSyncService(string baseUrl, string username, string password)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _username = username ?? "";
            _password = password ?? "";
        }

        /// <summary>
        /// 构造配置文件的完整 URL。
        /// </summary>
        private string GetFileUrl(string fileName)
        {
            string name = Uri.EscapeDataString(fileName ?? "yuSnip-config-v1.json");
            if (_baseUrl.EndsWith("/"))
                return _baseUrl + name;
            return _baseUrl + "/" + name;
        }

        /// <summary>
        /// 创建带 Basic Auth 的 HTTP 请求消息。
        /// </summary>
        private HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(_username) || !string.IsNullOrEmpty(_password))
            {
                string credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(_username + ":" + _password));
                req.Headers.TryAddWithoutValidation("Authorization", "Basic " + credentials);
            }
            return req;
        }

        /// <summary>
        /// 测试 WebDAV 连接是否正常。
        /// 使用 PROPFIND 请求根目录，返回状态码 207 (Multi-Status) 表示连接成功。
        /// </summary>
        public async Task<CloudSyncResult> TestConnectionAsync()
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return CloudSyncResult.Fail("WebDAV 地址不能为空");

            try
            {
                using var req = CreateRequest(new HttpMethod("PROPFIND"), _baseUrl);
                req.Headers.Add("Depth", "0");
                req.Headers.Add("Accept", "application/xml");

                // 使用较短的超时时间（10 秒）
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // 207 = Multi-Status (WebDAV 标准)，200/204 也算正常
                if (resp.StatusCode == HttpStatusCode.MultiStatus ||
                    resp.StatusCode == HttpStatusCode.OK ||
                    resp.StatusCode == HttpStatusCode.NoContent)
                {
                    return CloudSyncResult.Success();
                }

                // 401 = 未授权
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return CloudSyncResult.Fail("认证失败：用户名或密码/Token 不正确");

                // 403 = 禁止访问
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    return CloudSyncResult.Fail("服务器拒绝访问 (403 Forbidden)，请检查权限设置");

                // 404 = 路径不存在
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return CloudSyncResult.Fail("路径不存在 (404)，请检查 WebDAV 地址是否正确");

                string body = await resp.Content.ReadAsStringAsync();
                return CloudSyncResult.Fail($"连接测试失败 (HTTP {(int)resp.StatusCode})：{Truncate(body, 200)}");
            }
            catch (TaskCanceledException)
            {
                return CloudSyncResult.Fail("连接超时，请检查地址是否正确或网络是否通畅");
            }
            catch (HttpRequestException ex)
            {
                return CloudSyncResult.Fail("网络请求失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("连接测试异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 上传配置 JSON 到 WebDAV。
        /// 使用 PUT 方法覆盖写入文件。
        /// </summary>
        public async Task<CloudSyncResult> UploadAsync(string json, string fileName)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return CloudSyncResult.Fail("WebDAV 地址不能为空");
            if (string.IsNullOrWhiteSpace(fileName))
                return CloudSyncResult.Fail("文件名不能为空");

            string url = GetFileUrl(fileName);

            try
            {
                using var req = CreateRequest(HttpMethod.Put, url);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await Http.SendAsync(req, cts.Token);
                var body = await resp.Content.ReadAsStringAsync();

                // 200, 201, 204 都表示成功
                if (resp.StatusCode == HttpStatusCode.OK ||
                    resp.StatusCode == HttpStatusCode.Created ||
                    resp.StatusCode == HttpStatusCode.NoContent)
                {
                    return CloudSyncResult.Success();
                }

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return CloudSyncResult.Fail("认证失败：用户名或密码/Token 不正确", authFailed: true);

                if (resp.StatusCode == HttpStatusCode.Conflict)
                    return CloudSyncResult.Fail("上传失败 (409 Conflict)：目标路径不存在，请确保 WebDAV 地址指向有效目录");

                if (resp.StatusCode == HttpStatusCode.InsufficientStorage)
                    return CloudSyncResult.Fail("服务器存储空间不足 (507)");

                return CloudSyncResult.Fail($"上传失败 (HTTP {(int)resp.StatusCode})：{Truncate(body, 200)}");
            }
            catch (TaskCanceledException)
            {
                return CloudSyncResult.Fail("上传超时，请检查网络连接");
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("上传异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 从 WebDAV 下载配置 JSON。
        /// </summary>
        public async Task<CloudSyncResult> DownloadAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return CloudSyncResult.Fail("WebDAV 地址不能为空");
            if (string.IsNullOrWhiteSpace(fileName))
                return CloudSyncResult.Fail("文件名不能为空");

            string url = GetFileUrl(fileName);

            try
            {
                using var req = CreateRequest(HttpMethod.Get, url);

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await Http.SendAsync(req, cts.Token);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return CloudSyncResult.Fail("云端暂无配置文件，请先上传", notFound: true);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return CloudSyncResult.Fail("认证失败：用户名或密码/Token 不正确", authFailed: true);

                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = await resp.Content.ReadAsStringAsync();
                    return CloudSyncResult.Fail($"下载失败 (HTTP {(int)resp.StatusCode})：{Truncate(errBody, 200)}");
                }

                string content = await resp.Content.ReadAsStringAsync();
                return CloudSyncResult.Success(content);
            }
            catch (TaskCanceledException)
            {
                return CloudSyncResult.Fail("下载超时，请检查网络连接");
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("下载异常：" + ex.Message);
            }
        }

        private static string Truncate(string s, int max) =>
            s.Length > max ? s[..max] : s;

        public void Dispose()
        {
            // HttpClient 是静态共享的，无需释放
        }
    }
}
