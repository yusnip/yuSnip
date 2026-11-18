using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.CloudSync
{
    /// <summary>
    /// Google Drive 配置同步服务。
    /// 实现 OAuth2 PKCE 登录流程、Token 自动刷新、Drive API 文件 CRUD。
    /// Token 存储在 AppSettings (INI) 中，与现有配置一致。
    /// </summary>
    public sealed class GoogleDriveSyncService : IDisposable
    {
        // ================================================================
        // Google OAuth 凭据
        // ================================================================
        private const string CallbackPath = "/oauth/callback";
        private const string Scope = "openid email profile https://www.googleapis.com/auth/drive.file";
        private const string ConfigFileName = "yuSnip-config-v1.json";

        private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string OfficialOAuthUrl = "https://yusnip.top/yusnip/oauth";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
        private const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";
        private const string DriveFilesUrl = "https://www.googleapis.com/drive/v3/files";
        private const string DriveUploadUrl = "https://www.googleapis.com/upload/drive/v3/files";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly HttpClient OfficialOAuthHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private TcpListener? _callbackListener;
        private CancellationTokenSource? _loginCts;

        // 登录回调结果：0=无进行中的登录, 1=等待浏览器回调, 2=成功, 3=失败
        private volatile int _loginPhase;
        private string? _loginError;

        // 最近一次获取访问令牌失败的原因（供上传/下载报错时展示）
        private string _lastAuthError = "";
        private bool _lastAuthFailure;

        private readonly AppSettings _settings;
        private readonly HttpClient _http;
        private readonly HttpClient _officialOAuthHttp;

        public GoogleDriveSyncService(AppSettings settings, HttpClient? http = null)
        {
            _settings = settings;
            _http = http ?? Http;
            _officialOAuthHttp = http ?? OfficialOAuthHttp;
        }

        // ================================================================
        // 凭据
        // ================================================================

        private static (string Id, string Secret) LocalClient(AppSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.GoogleDriveClientId) && !string.IsNullOrWhiteSpace(settings.GoogleDriveClientSecret)
                ? (settings.GoogleDriveClientId, settings.GoogleDriveClientSecret)
                : ("", "");

        private async Task<string> GetOfficialClientIdAsync(CancellationToken cancellationToken)
        {
            using var response = await _officialOAuthHttp.GetAsync(OfficialOAuthUrl + "/client", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            string? id = document.RootElement.GetProperty("client_id").GetString();
            if (string.IsNullOrWhiteSpace(id) || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
                throw new InvalidDataException("yuSnip OAuth 配置不可用。");

            return id;
        }

        private async Task<HttpResponseMessage> RequestTokenAsync(
            (string Id, string Secret) client, Dictionary<string, string> form, CancellationToken cancellationToken = default)
        {
            if (client.Id.Length == 0)
            {
                using var content = new FormUrlEncodedContent(form);
                return await _officialOAuthHttp.PostAsync(OfficialOAuthUrl + "/token", content, cancellationToken);
            }

            form["client_id"] = client.Id;
            form["client_secret"] = client.Secret;
            using var directContent = new FormUrlEncodedContent(form);
            return await _http.PostAsync(TokenUrl, directContent, cancellationToken);
        }

        // ================================================================
        // 状态查询
        // ================================================================

        public GoogleDriveStatus GetStatus()
        {
            return new GoogleDriveStatus
            {
                Connected = !string.IsNullOrWhiteSpace(_settings.GoogleDriveRefreshToken),
                UserEmail = _settings.GoogleDriveUserEmail ?? "",
                UserAvatar = _settings.GoogleDriveUserAvatar ?? "",
                FileId = _settings.GoogleDriveFileId ?? "",
                LastSyncUnixMs = _settings.GoogleDriveLastSync,
            };
        }

        /// <summary>
        /// 查询当前登录流程结果。finished=false 表示仍在等待浏览器回调。
        /// </summary>
        public (bool finished, bool success, string? error) GetLoginOutcome()
        {
            int phase = _loginPhase;
            if (phase == 2) return (true, true, null);
            if (phase == 3) return (true, false, _loginError ?? "未知错误");
            return (false, false, null);
        }

        private void SetLoginOutcome(bool success, string? error)
        {
            _loginError = error;
            _loginPhase = success ? 2 : 3;
        }

        // ================================================================
        // OAuth2 PKCE 登录流程
        // ================================================================

        /// <summary>
        /// 启动 Google OAuth2 登录：生成 PKCE 参数、打开浏览器、监听回调。
        /// 返回 null 表示成功启动，返回错误字符串表示失败。
        /// </summary>
        public Task<string?> StartLoginAsync() =>
            StartLoginAsync(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));

        internal async Task<string?> StartLoginAsync(Action<string> openBrowser)
        {
            CancelLogin();
            var login = new CancellationTokenSource();
            _loginCts = login;
            CancellationToken cancellationToken = login.Token;

            try
            {
                var client = LocalClient(_settings);
                string clientId = client.Id.Length == 0
                    ? await GetOfficialClientIdAsync(cancellationToken)
                    : client.Id;
                cancellationToken.ThrowIfCancellationRequested();

                string verifier = GenerateRandomString(64);
                string state = GenerateRandomString(24);
                string challenge = Sha256Base64Url(verifier);

                // 由系统分配空闲端口。先绑定并保持监听，再打开授权页。
                var listener = new TcpListener(IPAddress.Loopback, 0);
                _callbackListener = listener;
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirectUri = FormattableString.Invariant($"http://127.0.0.1:{port}{CallbackPath}");
                var url = AuthUrl +
                    "?client_id=" + Uri.EscapeDataString(clientId) +
                    "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                    "&response_type=code" +
                    "&scope=" + Uri.EscapeDataString(Scope) +
                    "&access_type=offline" +
                    "&prompt=consent" +
                    "&code_challenge=" + Uri.EscapeDataString(challenge) +
                    "&code_challenge_method=S256" +
                    "&state=" + Uri.EscapeDataString(state);

                _loginError = null;
                _loginPhase = 1;
                _ = Task.Run(() => WaitForCallbackAsync(listener, client, redirectUri, state, verifier, cancellationToken));
                openBrowser(url);
                return null;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_loginCts, login))
                    CancelLogin();

                AppLogger.Warn("GoogleDriveSyncService.StartLogin 失败：" + ex.GetType().Name);
                return ex is SocketException
                    ? "无法启动本地 Google 登录回调，请检查安全软件是否允许 yuSnip 使用本机连接。"
                    : "暂时无法打开 Google 登录，请检查网络后重试。";
            }
        }

        private async Task WaitForCallbackAsync(TcpListener listener, (string Id, string Secret) client,
            string redirectUri, string state, string verifier, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                    using var stream = connection.GetStream();
                    Dictionary<string, string>? query = await ReadCallbackAsync(stream, cancellationToken);
                    if (query == null || !query.TryGetValue("state", out var receivedState) || receivedState != state)
                        continue;

                    string? failure = null;
                    if (query.TryGetValue("error", out var error))
                        failure = "Google 返回错误：" + error;
                    else if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                        failure = "未收到授权码";
                    else if (!await ExchangeCodeAsync(client, code, verifier, redirectUri, cancellationToken))
                        failure = string.IsNullOrWhiteSpace(_lastAuthError)
                            ? "无法换取长期凭据，请检查网络后重试。" : _lastAuthError;

                    cancellationToken.ThrowIfCancellationRequested();
                    SetLoginOutcome(failure == null, failure);

                    string html = CallbackHtml(failure == null
                        ? "Google Drive 登录成功，可以关闭此页面。"
                        : "Google 登录失败：" + failure);
                    string response = "HTTP/1.1 200 OK\r\n" +
                                      "Content-Type: text/html; charset=utf-8\r\n" +
                                      "Cache-Control: no-store\r\n" +
                                      "Content-Length: " + Encoding.UTF8.GetByteCount(html) + "\r\n" +
                                      "Connection: close\r\n\r\n" + html;
                    try
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken);
                    }
                    catch (IOException) { } // 浏览器可能已经关闭页面，保留应用内登录结果。
                    return;
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveSyncService OAuth 回调处理失败：" + ex.GetType().Name);
                SetLoginOutcome(false, "无法处理本地 Google 登录回调，请重新登录。");
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task<Dictionary<string, string>?> ReadCallbackAsync(NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var buffer = new byte[8192];
                int total = 0;
                while (total < buffer.Length)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(total), readTimeout.Token);
                    if (count == 0) return null;
                    total += count;
                    string request = Encoding.ASCII.GetString(buffer, 0, total);
                    if (!request.Contains("\r\n\r\n", StringComparison.Ordinal))
                        continue;

                    const string prefix = "GET " + CallbackPath + "?";
                    if (!request.StartsWith(prefix, StringComparison.Ordinal))
                        return null;
                    int end = request.IndexOf(' ', prefix.Length);
                    return end > prefix.Length ? ParseQuery(request[prefix.Length..end]) : null;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }

            return null;
        }

        /// <summary>
        /// 用授权码换取 Token 并保存。
        /// </summary>
        private async Task<bool> ExchangeCodeAsync((string Id, string Secret) client, string code, string verifier,
            string redirectUri, CancellationToken cancellationToken)
        {
            _lastAuthError = "";
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                };

                using var resp = await RequestTokenAsync(client, form, cancellationToken);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("GoogleDriveSyncService.ExchangeCodeAsync 失败：" + "[响应正文已脱敏]");
                    return false;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string access = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
                string refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                int expires = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;

                // Google 允许只授予部分权限；拿到头像／邮箱不代表拥有云盘同步权限。
                if (root.TryGetProperty("scope", out var granted)
                    && !(granted.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(scope => scope is "https://www.googleapis.com/auth/drive.file" or "https://www.googleapis.com/auth/drive"))
                {
                    _lastAuthError = "未授予 Google Drive 文件同步权限，请重新登录，并在 Google 授权页勾选 yuSnip 的云端文件权限。";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(access)) return false;
                // scope 可按 OAuth 规则省略；保存前始终用只读请求确认 Drive 权限，不创建或修改文件。
                using (var permission = new HttpRequestMessage(HttpMethod.Get, DriveFilesUrl + "?pageSize=1&fields=files(id)"))
                {
                    permission.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                    await CloudHttp.ReadAsync(_http, permission, cancellationToken, operation: "Google Drive 检查登录权限");
                }

                if (string.IsNullOrWhiteSpace(refresh) && string.IsNullOrWhiteSpace(_settings.GoogleDriveRefreshToken))
                    return false;

                if (!string.IsNullOrWhiteSpace(refresh))
                    _settings.GoogleDriveRefreshToken = refresh;
                _settings.GoogleDriveAccessToken = access;
                _settings.GoogleDriveTokenExpiry = NowMs() + Math.Max(60, expires - 60) * 1000L;

                await FillUserInfoAsync(access);
                _settings.Save();
                return true;
            }
            catch (Exception ex)
            {
                if (ex is CloudTransportException) _lastAuthError = ex.Message;
                AppLogger.Warn("GoogleDriveSyncService.ExchangeCodeAsync 异常：" + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 获取用户信息（邮箱、头像）。
        /// </summary>
        private async Task FillUserInfoAsync(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("GoogleDriveSyncService.FillUserInfoAsync 失败 (HTTP " + (int)resp.StatusCode + ")：" + "[响应正文已脱敏]");
                    return;
                }
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("email", out var email))
                    _settings.GoogleDriveUserEmail = email.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("picture", out var picture))
                    _settings.GoogleDriveUserAvatar = picture.GetString() ?? "";
                AppLogger.Info("GoogleDriveSyncService.FillUserInfoAsync 成功：email=" + _settings.GoogleDriveUserEmail + ", avatar=" + (_settings.GoogleDriveUserAvatar.Length > 0 ? "yes" : "no"));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveSyncService.FillUserInfoAsync 异常：" + ex.GetType().Name);
            }
        }

        // ================================================================
        // Token 自动刷新
        // ================================================================

        public async Task<string> GetAutomaticSyncTokenAsync(CancellationToken cancellationToken)
        {
            string token = await EnsureAccessTokenAsync(cancellationToken);
            if (token.Length == 0) throw new CloudTransportException(_lastAuthError, _lastAuthFailure);
            return token;
        }

        private async Task<string> EnsureAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            _lastAuthError = "";
            _lastAuthFailure = false;
            string access = _settings.GoogleDriveAccessToken ?? "";

            if (!string.IsNullOrWhiteSpace(access) &&
                _settings.GoogleDriveTokenExpiry > NowMs() + 2 * 60 * 1000)
                return access;

            string refresh = _settings.GoogleDriveRefreshToken ?? "";
            var client = LocalClient(_settings);
            string requestEmail = _settings.GoogleDriveUserEmail ?? "";
            if (string.IsNullOrWhiteSpace(refresh))
            {
                _lastAuthError = "Google Drive 未登录，请先登录";
                _lastAuthFailure = true;
                return "";
            }

            try
            {
                var form = new Dictionary<string, string>
                {
                    ["refresh_token"] = refresh,
                    ["grant_type"] = "refresh_token"
                };

                using var resp = await RequestTokenAsync(client, form, cancellationToken);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("GoogleDriveSyncService.EnsureAccessTokenAsync 刷新令牌失败 (HTTP " + (int)resp.StatusCode + ")：" + "[响应正文已脱敏]");
                    _lastAuthFailure = body.Contains("invalid_grant", StringComparison.Ordinal) || body.Contains("invalid_client", StringComparison.Ordinal)
                        || resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                    if (_lastAuthFailure)
                        _lastAuthError = "Google 授权已失效（可能被撤销或长期未使用），请重新登录";
                    else
                        _lastAuthError = $"刷新 Google 授权暂时失败 (HTTP {(int)resp.StatusCode})，稍后重试";
                    return "";
                }

                using var doc = JsonDocument.Parse(body);
                access = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("Google 授权响应缺少 access_token。");
                int expires = doc.RootElement.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;

                // 等待期间可能开过设置页（Reload 换实例）或切换账号，只更新当前实例的登录态。
                var current = AppSettings.Current;
                if (current.GoogleDriveRefreshToken != refresh || current.GoogleDriveUserEmail != requestEmail
                    || LocalClient(current) != client)
                {
                    _lastAuthError = "Google 登录信息已变化，等待重新检查";
                    return "";
                }
                _settings.GoogleDriveAccessToken = current.GoogleDriveAccessToken = access;
                _settings.GoogleDriveTokenExpiry = current.GoogleDriveTokenExpiry = NowMs() + Math.Max(60, expires - 60) * 1000L;
                current.Save();
                return access;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveSyncService.EnsureAccessTokenAsync 异常（多半是网络无法访问 Google）：" + ex.GetType().Name);
                _lastAuthError = "无法连接 Google 服务，请检查网络或代理设置";
                return "";
            }
        }

        // ================================================================
        // Drive API 文件操作
        // ================================================================

        /// <summary>
        /// 在 Drive 上查找配置文件，返回 fileId 或 null。
        /// </summary>
        private async Task<string?> FindConfigFileAsync(string accessToken)
        {
            try
            {
                string q = Uri.EscapeDataString($"name='{ConfigFileName}' and trashed=false");
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    DriveFilesUrl + "?q=" + q + "&fields=files(id,name)");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("GoogleDriveSyncService.FindConfigFileAsync 失败 (HTTP " + (int)resp.StatusCode + ")：" + "[响应正文已脱敏]");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                var files = doc.RootElement.GetProperty("files");
                if (files.GetArrayLength() > 0)
                    return files[0].GetProperty("id").GetString();
            }
            catch { }
            return null;
        }

        // ================================================================
        // 授权失效处理
        // ================================================================

        /// <summary>
        /// 判断 403 响应是否为 access_token 权限范围不足（旧版 refresh_token 未含 drive.file scope，
        /// Google 刷新令牌只继承首次授权的 scope，无法通过刷新补齐，必须重新登录）。
        /// </summary>
        private static bool IsScopeInsufficient(string body)
        {
            return body.Contains("insufficientPermissions", StringComparison.Ordinal)
                || body.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.Ordinal)
                || body.Contains("Request had insufficient authentication scopes", StringComparison.Ordinal);
        }

        /// <summary>
        /// 清除本地保存的 Google 授权（refresh_token / access_token / fileId），
        /// 使 UI 回到未登录状态，引导用户重新登录以获取完整权限。
        /// </summary>
        private void ClearAuthState()
        {
            _settings.GoogleDriveRefreshToken = "";
            _settings.GoogleDriveAccessToken = "";
            _settings.GoogleDriveTokenExpiry = 0;
            _settings.GoogleDriveFileId = "";
            _settings.Save();
            AppLogger.Warn("GoogleDriveSyncService: 检测到授权权限范围不足（旧版授权缺少 drive.file），已清除本地授权，请重新登录");
        }

        /// <summary>
        /// 上传配置 JSON 到 Google Drive。
        /// </summary>
        public async Task<CloudSyncResult> UploadAsync(string json)
        {
            var token = await EnsureAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return CloudSyncResult.Fail(
                    string.IsNullOrWhiteSpace(_lastAuthError) ? "Google Drive 未登录或授权已失效" : _lastAuthError,
                    authFailed: true);

            try
            {
                // 查找已有文件
                string? fileId = !string.IsNullOrWhiteSpace(_settings.GoogleDriveFileId)
                    ? _settings.GoogleDriveFileId
                    : await FindConfigFileAsync(token);

                HttpRequestMessage req;
                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    // 更新已有文件
                    req = new HttpRequestMessage(new HttpMethod("PATCH"),
                        DriveUploadUrl + "/" + fileId + "?uploadType=media");
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                else
                {
                    // 创建新文件（multipart 上传）
                    string boundary = "yusnip_boundary_" + NowMs();
                    string multipart = "--" + boundary +
                        "\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n" +
                        "{\"name\":\"" + ConfigFileName + "\"}" +
                        "\r\n--" + boundary +
                        "\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n" +
                        json +
                        "\r\n--" + boundary + "--";

                    req = new HttpRequestMessage(HttpMethod.Post, DriveUploadUrl + "?uploadType=multipart");
                    req.Content = new StringContent(multipart, Encoding.UTF8);
                    req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/related; boundary=" + boundary);
                }

                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("GoogleDriveSyncService.UploadAsync 失败 (HTTP " + (int)resp.StatusCode + ")：" + "[响应正文已脱敏]");
                    if ((int)resp.StatusCode == 403 && IsScopeInsufficient(body))
                    {
                        ClearAuthState();
                        return CloudSyncResult.Fail("当前登录授权缺少 Drive 权限（旧版授权所致），已自动退出登录，请重新登录后再上传。", authFailed: true);
                    }
                    return CloudSyncResult.Fail($"上传失败 (HTTP {(int)resp.StatusCode})：{Truncate(body, 200)}");
                }

                // 保存 fileId
                if (string.IsNullOrWhiteSpace(fileId))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        _settings.GoogleDriveFileId = doc.RootElement.GetProperty("id").GetString() ?? "";
                    }
                    catch { }
                }
                else
                {
                    _settings.GoogleDriveFileId = fileId;
                }

                _settings.GoogleDriveLastSync = NowMs();
                _settings.Save();
                return CloudSyncResult.Success();
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("上传异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 从 Google Drive 下载配置 JSON。
        /// </summary>
        public async Task<CloudSyncResult> DownloadAsync()
        {
            var token = await EnsureAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return CloudSyncResult.Fail(
                    string.IsNullOrWhiteSpace(_lastAuthError) ? "Google Drive 未登录或授权已失效" : _lastAuthError,
                    authFailed: true);

            try
            {
                string? fileId = !string.IsNullOrWhiteSpace(_settings.GoogleDriveFileId)
                    ? _settings.GoogleDriveFileId
                    : await FindConfigFileAsync(token);

                if (string.IsNullOrWhiteSpace(fileId))
                    return CloudSyncResult.Fail("云端暂无配置文件，请先上传", notFound: true);

                _settings.GoogleDriveFileId = fileId;

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    DriveFilesUrl + "/" + fileId + "?alt=media");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("GoogleDriveSyncService.DownloadAsync 失败 (HTTP " + (int)resp.StatusCode + ")：" + "[响应正文已脱敏]");
                    if ((int)resp.StatusCode == 403 && IsScopeInsufficient(body))
                    {
                        ClearAuthState();
                        return CloudSyncResult.Fail("当前登录授权缺少 Drive 权限（旧版授权所致），已自动退出登录，请重新登录后再恢复。", authFailed: true);
                    }
                    return CloudSyncResult.Fail($"下载失败 (HTTP {(int)resp.StatusCode})：{Truncate(body, 200)}");
                }

                _settings.GoogleDriveLastSync = NowMs();
                _settings.Save();
                return CloudSyncResult.Success(body);
            }
            catch (Exception ex)
            {
                return CloudSyncResult.Fail("下载异常：" + ex.Message);
            }
        }

        // ================================================================
        // 登出
        // ================================================================

        public async Task<CloudSyncResult> LogoutAsync()
        {
            var token = await EnsureAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    await _http.PostAsync(RevokeUrl,
                        new StringContent("token=" + Uri.EscapeDataString(token),
                            Encoding.UTF8, "application/x-www-form-urlencoded"));
                }
                catch { }
            }

            _settings.GoogleDriveRefreshToken = "";
            _settings.GoogleDriveAccessToken = "";
            _settings.GoogleDriveTokenExpiry = 0;
            _settings.GoogleDriveFileId = "";
            _settings.GoogleDriveUserEmail = "";
            _settings.GoogleDriveUserAvatar = "";
            _settings.GoogleDriveLastSync = 0;
            _settings.Save();

            return CloudSyncResult.Success();
        }

        /// <summary>
        /// 取消正在进行的登录流程。
        /// </summary>
        public void CancelLogin()
        {
            var login = _loginCts;
            _loginCts = null;
            login?.Cancel();
            login?.Dispose();
            _callbackListener?.Stop();
            _callbackListener = null;
        }

        // ================================================================
        // 辅助方法
        // ================================================================

        private static string GenerateRandomString(int bytes)
        {
            var data = RandomNumberGenerator.GetBytes(bytes);
            return Base64Url(data);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Sha256Base64Url(string text)
        {
            return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(text)));
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>();
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq >= 0)
                    dict[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return dict;
        }

        private static string CallbackHtml(string message)
        {
            return "<!doctype html><meta charset=\"utf-8\"><title>Google 登录</title>" +
                   "<body style=\"font-family:system-ui;padding:32px;line-height:1.6\">" +
                   "<h2>" + WebUtility.HtmlEncode(message) + "</h2><p>请返回应用。</p></body>";
        }

        private static string Truncate(string s, int max) =>
            s.Length > max ? s[..max] : s;

        public void Dispose()
        {
            CancelLogin();
        }
    }
}
