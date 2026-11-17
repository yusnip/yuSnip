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
        private static readonly Lazy<(string Id, string Secret)> BuiltInCredentials = new(LoadBuiltInCredentials);

        // Desktop app 类型无需在 Console 注册 redirect URI，Google 自动允许 127.0.0.1 任意端口。
        // 使用 38439 端口，与其他使用同一凭据的软件错开，避免同时登录时的端口占用。
        private const string RedirectUri = "http://127.0.0.1:38439/oauth/callback";
        private const int CallbackPort = 38439;
        private const string Scope = "openid email profile https://www.googleapis.com/auth/drive.file";
        private const string ConfigFileName = "yuSnip-config-v1.json";

        private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
        private const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";
        private const string DriveFilesUrl = "https://www.googleapis.com/drive/v3/files";
        private const string DriveUploadUrl = "https://www.googleapis.com/upload/drive/v3/files";

        private static readonly HttpClient Http = new HttpClient();

        private string? _oauthCodeVerifier;
        private string? _oauthState;
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

        public GoogleDriveSyncService(AppSettings settings, HttpClient? http = null)
        {
            _settings = settings;
            _http = http ?? Http;
        }

        // ================================================================
        // 凭据
        // ================================================================

        private string ClientId => CredentialsFor(_settings).Id;
        private string ClientSecret => CredentialsFor(_settings).Secret;

        public bool HasOAuthCredentials => CredentialsFor(_settings).Id.Length != 0;

        private static (string Id, string Secret) CredentialsFor(AppSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.GoogleDriveClientId) && !string.IsNullOrWhiteSpace(settings.GoogleDriveClientSecret)
                ? (settings.GoogleDriveClientId, settings.GoogleDriveClientSecret)
                : BuiltInCredentials.Value;

        private static (string Id, string Secret) LoadBuiltInCredentials()
        {
            using var resource = typeof(GoogleDriveSyncService).Assembly.GetManifestResourceStream("YuSnip.GoogleDrive.DesktopOAuth.json");
            if (resource != null)
            {
                try
                {
                    if (ReadDesktopCredentials(resource) is { } credentials)
                        return credentials;
                }
                catch (JsonException) { }
            }

            return ("", "");
        }

        private const string MissingCredentialsMessage = "请先在云存储设置中导入 Google Drive 桌面 OAuth 凭据。";

        public string? ImportDesktopCredentials(string path)
        {
            const string invalidFile = "请选择从 Google Cloud 下载的「桌面应用」OAuth 凭据 JSON 文件。";
            try
            {
                using var file = File.OpenRead(path);
                if (file.Length > 64 * 1024 || ReadDesktopCredentials(file) is not { } credentials)
                    return invalidFile;

                lock (CloudSnapshot.FileGate)
                {
                    if (!string.IsNullOrWhiteSpace(_settings.GoogleDriveRefreshToken))
                        return "请先退出 Google Drive 登录，再导入新的凭据。";

                    string previousId = _settings.GoogleDriveClientId;
                    string previousSecret = _settings.GoogleDriveClientSecret;
                    _settings.GoogleDriveClientId = credentials.Id;
                    _settings.GoogleDriveClientSecret = credentials.Secret;
                    if (_settings.TrySave())
                        return null;

                    _settings.GoogleDriveClientId = previousId;
                    _settings.GoogleDriveClientSecret = previousSecret;
                    return "无法保存 OAuth 凭据，请检查本机配置目录的写入权限。";
                }
            }
            catch (JsonException)
            {
                return invalidFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "无法读取 OAuth 凭据文件，请检查文件是否可以打开。";
            }
        }

        private static (string Id, string Secret)? ReadDesktopCredentials(Stream stream)
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Object)
                return null;

            string? id = ReadCredential(installed, "client_id");
            string? secret = ReadCredential(installed, "client_secret");
            return id != null && id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) && secret != null
                ? (id, secret)
                : null;
        }

        private static string? ReadCredential(JsonElement installed, string key)
        {
            if (!installed.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String)
                return null;

            string? value = field.GetString();
            return !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                ? value
                : null;
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
        public string? StartLogin()
        {
            if (!HasOAuthCredentials)
                return MissingCredentialsMessage;

            try
            {
                _loginCts?.Cancel();
                _loginCts = new CancellationTokenSource();

                _oauthCodeVerifier = GenerateRandomString(64);
                _oauthState = GenerateRandomString(24);
                string challenge = Sha256Base64Url(_oauthCodeVerifier);

                var url = AuthUrl +
                    "?client_id=" + Uri.EscapeDataString(ClientId) +
                    "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                    "&response_type=code" +
                    "&scope=" + Uri.EscapeDataString(Scope) +
                    "&access_type=offline" +
                    "&prompt=consent" +
                    "&code_challenge=" + Uri.EscapeDataString(challenge) +
                    "&code_challenge_method=S256" +
                    "&state=" + Uri.EscapeDataString(_oauthState);

                // 重置登录结果状态，进入"等待浏览器回调"
                _loginPhase = 1;
                _loginError = null;

                // 启动回调监听
                StartCallbackListener();

                // 打开系统浏览器
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveSyncService.StartLogin 失败：" + ex.GetType().Name);
                return "无法启动 Google 登录：" + ex.Message;
            }
        }

        /// <summary>
        /// 启动 TCP 监听器接收 OAuth 回调。
        /// 回调到达后自动交换 Token 并保存。
        /// </summary>
        private void StartCallbackListener()
        {
            try
            {
                _callbackListener?.Stop();
                _callbackListener = new TcpListener(IPAddress.Loopback, CallbackPort);
                _callbackListener.Start();

                var cts = _loginCts;
                if (cts == null) return;
                Task.Run(async () =>
                {
                    try
                    {
                        using var client = await _callbackListener.AcceptTcpClientAsync(cts.Token);
                        using var ns = client.GetStream();

                        // 读取 HTTP 请求
                        var buf = new byte[8192];
                        int total = 0;
                        int headerEnd;
                        while (total < buf.Length)
                        {
                            int n = await ns.ReadAsync(buf.AsMemory(total), cts.Token);
                            if (n <= 0) break;
                            total += n;
                            string soFar = Encoding.ASCII.GetString(buf, 0, total);
                            headerEnd = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (headerEnd >= 0) break;
                        }

                        string rawReq = Encoding.ASCII.GetString(buf, 0, total);
                        // 解析请求行: GET /oauth/callback?code=xxx&state=xxx HTTP/1.1
                        int firstSpace = rawReq.IndexOf(' ');
                        int secondSpace = rawReq.IndexOf(' ', firstSpace + 1);
                        string pathAndQuery = rawReq.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
                        int qMark = pathAndQuery.IndexOf('?');
                        string query = qMark >= 0 ? pathAndQuery.Substring(qMark + 1) : "";

                        var map = ParseQuery(query);
                        string html;
                        string? failReason = null;

                        // 检查错误
                        if (map.TryGetValue("error", out var err))
                        {
                            failReason = "Google 返回错误：" + err;
                        }
                        else if (!map.TryGetValue("state", out var state) || state != _oauthState)
                        {
                            failReason = "状态校验失败，请重新发起登录";
                        }
                        else if (!map.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                        {
                            failReason = "未收到授权码";
                        }
                        else if (string.IsNullOrWhiteSpace(_oauthCodeVerifier))
                        {
                            failReason = "授权会话已失效，请重新发起登录";
                        }
                        else
                        {
                            bool ok = await ExchangeCodeAsync(code, _oauthCodeVerifier);
                            if (!ok)
                                failReason = string.IsNullOrWhiteSpace(_lastAuthError)
                                    ? "无法换取长期凭据（网络异常或 Client 配置问题），详见日志" : _lastAuthError;
                        }

                        html = CallbackHtml(failReason == null
                            ? "Google Drive 登录成功，可以关闭此页面。"
                            : "Google 登录失败：" + failReason);

                        // 记录登录结果，供设置窗口轮询立即感知成败
                        SetLoginOutcome(failReason == null, failReason);

                        // 清理 PKCE 临时变量
                        _oauthCodeVerifier = null;
                        _oauthState = null;

                        // 发送 HTTP 响应
                        string response = "HTTP/1.1 200 OK\r\n" +
                                          "Content-Type: text/html; charset=utf-8\r\n" +
                                          "Content-Length: " + Encoding.UTF8.GetByteCount(html) + "\r\n" +
                                          "Connection: close\r\n\r\n" + html;
                        byte[] respBytes = Encoding.UTF8.GetBytes(response);
                        await ns.WriteAsync(respBytes, cts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("GoogleDriveSyncService OAuth 回调处理失败：" + ex.GetType().Name);
                        SetLoginOutcome(false, "回调处理异常：" + ex.Message);
                    }
                    finally
                    {
                        try { _callbackListener?.Stop(); } catch { }
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveSyncService.StartCallbackListener 失败：" + ex.GetType().Name);
                // 常见原因：回调端口 38439 被占用，浏览器授权完成后本应用收不到授权码
                SetLoginOutcome(false, "本地回调监听启动失败（端口 " + CallbackPort + " 可能被占用）：" + ex.Message);
            }
        }

        /// <summary>
        /// 用授权码换取 Token 并保存。
        /// </summary>
        private async Task<bool> ExchangeCodeAsync(string code, string verifier)
        {
            _lastAuthError = "";
            try
            {
                var form = new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("client_secret", ClientSecret),
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("code_verifier", verifier),
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                };

                using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
                var body = await resp.Content.ReadAsStringAsync();

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
                    await CloudHttp.ReadAsync(_http, permission, default, operation: "Google Drive 检查登录权限");
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
            var (requestClientId, requestClientSecret) = CredentialsFor(_settings);
            string requestEmail = _settings.GoogleDriveUserEmail ?? "";
            if (string.IsNullOrWhiteSpace(refresh))
            {
                _lastAuthError = "Google Drive 未登录，请先登录";
                _lastAuthFailure = true;
                return "";
            }

            if (string.IsNullOrWhiteSpace(requestClientId) || string.IsNullOrWhiteSpace(requestClientSecret))
            {
                _lastAuthError = MissingCredentialsMessage;
                _lastAuthFailure = true;
                return "";
            }

            try
            {
                var form = new[]
                {
                    new KeyValuePair<string, string>("client_id", requestClientId),
                    new KeyValuePair<string, string>("client_secret", requestClientSecret),
                    new KeyValuePair<string, string>("refresh_token", refresh),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                };

                using var formContent = new FormUrlEncodedContent(form);
                using var resp = await _http.PostAsync(TokenUrl, formContent, cancellationToken);
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
                var (currentClientId, currentClientSecret) = CredentialsFor(current);
                if (current.GoogleDriveRefreshToken != refresh || current.GoogleDriveUserEmail != requestEmail
                    || currentClientId != requestClientId || currentClientSecret != requestClientSecret)
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
            _loginCts?.Cancel();
            try { _callbackListener?.Stop(); } catch { }
            _oauthCodeVerifier = null;
            _oauthState = null;
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
                   "<h2>" + message + "</h2><p>请返回应用。</p></body>";
        }

        private static string Truncate(string s, int max) =>
            s.Length > max ? s[..max] : s;

        public void Dispose()
        {
            CancelLogin();
        }
    }
}
