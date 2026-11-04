using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ScreenCaptureTool.Core.CloudSync;

internal static class CloudHttp
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    internal static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    // 单请求覆盖发送、响应头和正文读取；迁移／清理的多次请求不共用一个 30 秒倒计时。
    internal static async Task<string> WithTimeoutAsync(string operation, Func<CancellationToken, Task<string>> action,
        CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        TimeSpan limit = requestTimeout ?? RequestTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try { return await action(timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new CloudRequestTimeoutException(operation, limit, ex); }
    }
    internal static Task<string> ReadAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct, bool missingAllowed = false,
        string operation = "云服务", TimeSpan? requestTimeout = null)
        => WithTimeoutAsync(operation, token => ReadResponseAsync(client, request, token, missingAllowed, operation), ct, requestTimeout);
    private static async Task<string> ReadResponseAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct, bool missingAllowed, string operation)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (missingAllowed && response.StatusCode == HttpStatusCode.NotFound) return "";
        if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new InvalidDataException("云服务响应超过 20 MB 限制。");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(block, ct)) != 0)
        {
            if (buffer.Length + count > 20 * 1024 * 1024) throw new InvalidDataException("云服务响应超过 20 MB 限制。");
            buffer.Write(block, 0, count);
        }
        string body = new UTF8Encoding(false, true).GetString(buffer.ToArray());
        if (!response.IsSuccessStatusCode)
        {
            bool authentication = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            string? reason = null;
            string detail = "";
            if (response.StatusCode == HttpStatusCode.Forbidden && request.Headers.Authorization?.Scheme == "Bearer")
            {
                // 只记录已知错误枚举，不输出云端正文、账号、URL 或文件 ID。
                authentication = false;
                try
                {
                    using var error = JsonDocument.Parse(body);
                    var reasons = error.RootElement.GetProperty("error").GetProperty("errors").EnumerateArray()
                        .Select(e => e.GetProperty("reason").GetString()).ToArray();
                    if (reasons.Any(r => r is "insufficientPermissions" or "authError"))
                    {
                        authentication = true; reason = "insufficientPermissions";
                        detail = "：未授予云盘文件同步权限，请重新登录并在 Google 授权页勾选 yuSnip 的云端文件权限。";
                    }
                    else if (reasons.Contains("insufficientFilePermissions"))
                    { reason = "insufficientFilePermissions"; detail = "：当前账号无权访问某个同步文件，请检查文件权限。"; }
                    else if (reasons.Any(r => r is "rateLimitExceeded" or "userRateLimitExceeded" or "dailyLimitExceeded" or "storageQuotaExceeded"))
                    { reason = "quotaExceeded"; detail = "：请求过于频繁或配额不足，请稍后重试。"; }
                }
                catch (JsonException) { }
                catch (KeyNotFoundException) { }
                catch (InvalidOperationException) { }
            }
            throw new CloudTransportException(operation + "返回 HTTP " + (int)response.StatusCode + detail,
                authentication, (int)response.StatusCode, reason);
        }
        return body;
    }
}

public sealed class WebDavRevisionTransport : ICloudRevisionTransport, ICloudRevisionMaintenance
{
    private readonly CloudRevisionArchive _archive = new();
    private readonly Uri _root;
    private readonly string _authorization, _legacyName, _prefix;
    private readonly HttpClient _http;
    public string TargetIdentity { get; }
    public WebDavRevisionTransport(string url, string username, string password, string fileName, HttpClient? http = null)
    {
        _root = new Uri(url.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        if (_root.Scheme is not ("http" or "https") || _root.UserInfo.Length != 0 || _root.Query.Length != 0 || _root.Fragment.Length != 0)
            throw new ArgumentException("WebDAV 地址须为无账号、查询参数的 HTTP/HTTPS 文件夹地址。");
        _legacyName = string.IsNullOrWhiteSpace(fileName) ? "yuSnip-config-v1.json" : fileName.Trim();
        _prefix = "yusnip-sync-v1-" + CloudSnapshot.Digest(_legacyName)[..16] + "-";
        _authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        TargetIdentity = "webdav\n" + _root.AbsoluteUri + "\n" + username + "\n" + _legacyName;
        _http = http ?? CloudHttp.Client;
    }
    private HttpRequestMessage Request(HttpMethod method, string? name = null)
    {
        var request = new HttpRequestMessage(method, name == null ? _root : new Uri(_root.AbsoluteUri + Uri.EscapeDataString(name)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
        return request;
    }
    public async Task<IReadOnlyList<CloudRemoteFile>> ListAsync(CancellationToken ct)
    {
        using var request = Request(new HttpMethod("PROPFIND"));
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        string xml = await CloudHttp.ReadAsync(_http, request, ct, operation: "WebDAV 列举同步版本");
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader); XNamespace dav = "DAV:";
        if (document.Root?.Name != dav + "multistatus") throw new InvalidDataException("WebDAV 未返回目录清单。");
        var result = new List<CloudRemoteFile>();
        foreach (var entry in document.Descendants(dav + "response"))
        {
            string? href = entry.Element(dav + "href")?.Value;
            if (href == null || !Uri.TryCreate(_root, href, out var uri)) continue;
            if (uri.Scheme != _root.Scheme || uri.Authority != _root.Authority) continue;
            string path = Uri.UnescapeDataString(uri.AbsolutePath), rootPath = Uri.UnescapeDataString(_root.AbsolutePath);
            if (!path.StartsWith(rootPath, StringComparison.Ordinal)) continue;
            string relative = path[rootPath.Length..];
            if (relative.Contains('/')) continue;
            string name = relative;
            if (!name.StartsWith(_prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            string id = name[_prefix.Length..^5];
            bool archive = CloudRevisionArchive.IsArchiveId(id);
            if (Guid.TryParseExact(id, "N", out _)) result.Add(new(name, id, archive));
        }
        return await _archive.ExpandAsync(result, ReadRawAsync, ct);
    }
    public async Task<string> ReadAsync(CloudRemoteFile file, CancellationToken ct)
        => _archive.TryRead(file, out string json) ? json : await ReadRawAsync(file, ct);
    private async Task<string> ReadRawAsync(CloudRemoteFile file, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, file.Key);
        return await CloudHttp.ReadAsync(_http, request, ct, operation: file.IsArchive ? "WebDAV 读取历史归档" : "WebDAV 读取同步版本");
    }
    public async Task CreateAsync(CloudRevision revision, CancellationToken ct)
    {
        string name = _prefix + revision.Id + ".json";
        using var request = Request(HttpMethod.Put, name);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Content = new StringContent(revision.ToJson(), Encoding.UTF8, "application/json");
        await CloudHttp.ReadAsync(_http, request, ct, operation: "WebDAV 上传同步版本");
        var readback = CloudRevision.Parse(await ReadAsync(new(name, revision.Id), ct));
        if (readback.ToJson() != revision.ToJson()) throw new IOException("WebDAV 修订回读校验失败。");
        _archive.Created(new(name, revision.Id));
    }
    public Task PrepareAsync(Func<bool> isCurrent, CancellationToken ct) => Task.CompletedTask;
    public Task CompactAsync(IReadOnlyList<CloudRevision> revisions, Func<bool> isCurrent, CancellationToken ct)
        => _archive.CompactAsync(revisions, isCurrent, async (id, json, token) =>
        {
            string name = _prefix + id + ".json";
            using var request = Request(HttpMethod.Put, name);
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            await CloudHttp.ReadAsync(_http, request, token, operation: "WebDAV 上传历史归档");
            return new CloudRemoteFile(name, id, true);
        }, ReadRawAsync, async (file, token) =>
        {
            using var request = Request(HttpMethod.Delete, file.Key);
            await CloudHttp.ReadAsync(_http, request, token, true, "WebDAV 清理历史文件");
        }, ct);
    public async Task<string?> ReadLegacyAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, _legacyName);
        string json = await CloudHttp.ReadAsync(_http, request, ct, true, "WebDAV 读取旧备份");
        return json.Length == 0 ? null : json;
    }
}

public sealed class GoogleDriveRevisionTransport : ICloudRevisionTransport, ICloudRevisionMaintenance
{
    private const string Tag = "yusnipConfigV1";
    private const string FolderTag = "yusnipConfigFolderV1";
    private readonly CloudRevisionArchive _archive = new();
    private string? _folder;
    private readonly Func<CancellationToken, Task<string>> _getToken;
    private readonly HttpClient _http;
    private readonly string _api, _upload;
    public string TargetIdentity { get; }
    public GoogleDriveRevisionTransport(string account, string clientId, Func<CancellationToken, Task<string>> getToken,
        HttpClient? http = null, string api = "https://www.googleapis.com/drive/v3/files", string upload = "https://www.googleapis.com/upload/drive/v3/files")
    {
        if (string.IsNullOrWhiteSpace(account)) throw new CloudTransportException("请重新登录 Google Drive 以确认同步账号。", true);
        TargetIdentity = "gdrive\n" + account.Trim().ToLowerInvariant() + "\n" + clientId;
        _getToken = getToken; _http = http ?? CloudHttp.Client; _api = api; _upload = upload;
    }
    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        string token = await CloudHttp.WithTimeoutAsync("Google Drive 刷新授权", _getToken, ct);
        if (string.IsNullOrEmpty(token)) throw new CloudTransportException("Google Drive 授权已失效，请重新连接。", true);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private sealed record DriveEntry(CloudRemoteFile File, string[] Parents);
    private async Task<List<DriveEntry>> ListQueryAsync(string query, bool revisions, CancellationToken ct)
    {
        var result = new List<DriveEntry>(); string? page = null;
        var seen = new HashSet<string>();
        do
        {
            string url = _api + "?spaces=drive&q=" + Uri.EscapeDataString(query) + "&fields=" + Uri.EscapeDataString("nextPageToken,files(id,name,parents,appProperties)") + "&pageSize=1000";
            if (page != null) url += "&pageToken=" + Uri.EscapeDataString(page);
            using var request = await RequestAsync(HttpMethod.Get, url, ct);
            using var doc = JsonDocument.Parse(await CloudHttp.ReadAsync(_http, request, ct, operation: revisions ? "Google Drive 列举同步版本" : "Google Drive 查找同步目录或旧备份"));
            foreach (var item in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                string key = item.GetProperty("id").GetString() ?? "";
                string id = revisions ? item.GetProperty("appProperties").GetProperty("revision").GetString() ?? "" : "";
                if (key.Length == 0 || (revisions && !Guid.TryParseExact(id, "N", out _))) throw new InvalidDataException("Drive 修订列表无效。");
                bool archive = revisions && item.GetProperty("appProperties").TryGetProperty("kind", out var kind) && kind.GetString() == "archive";
                string[] parents = item.TryGetProperty("parents", out var parentsValue) ? parentsValue.EnumerateArray().Select(p => p.GetString()!).ToArray() : [];
                result.Add(new(new(key, id, archive), parents));
            }
            page = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (page != null && !seen.Add(page)) throw new InvalidDataException("Drive 分页出现重复游标。");
        } while (!string.IsNullOrEmpty(page));
        return result;
    }
    private static string RevisionQuery => "trashed = false and appProperties has { key='sync' and value='" + Tag + "' }";
    public async Task<IReadOnlyList<CloudRemoteFile>> ListAsync(CancellationToken ct)
        => await _archive.ExpandAsync((await ListQueryAsync(RevisionQuery, true, ct)).Select(e => e.File).ToArray(), ReadRawAsync, ct);
    public async Task<string> ReadAsync(CloudRemoteFile file, CancellationToken ct)
        => _archive.TryRead(file, out string json) ? json : await ReadRawAsync(file, ct);
    private async Task<string> ReadRawAsync(CloudRemoteFile file, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Get, _api + "/" + Uri.EscapeDataString(file.Key) + "?alt=media", ct);
        return await CloudHttp.ReadAsync(_http, request, ct, operation: file.IsArchive ? "Google Drive 读取历史归档" : "Google Drive 读取同步版本");
    }
    public async Task CreateAsync(CloudRevision revision, CancellationToken ct)
    {
        await EnsureFolderAsync(ct);
        var file = await CreateFileAsync(revision.Id, revision.ToJson(), false, ct);
        var readback = CloudRevision.Parse(await ReadRawAsync(file, ct));
        if (readback.ToJson() != revision.ToJson()) throw new IOException("Drive 修订回读校验失败。");
        _archive.Created(file);
    }
    private async Task<CloudRemoteFile> CreateFileAsync(string id, string json, bool archive, CancellationToken ct)
    {
        using var metadata = new MemoryStream();
        using (var writer = new Utf8JsonWriter(metadata))
        {
            writer.WriteStartObject(); writer.WriteString("name", (archive ? "yusnip-sync-archive-v1-" : "yusnip-sync-v1-") + id + ".json");
            writer.WriteStartArray("parents"); writer.WriteStringValue(_folder!); writer.WriteEndArray();
            writer.WriteStartObject("appProperties"); writer.WriteString("sync", Tag); writer.WriteString("revision", id);
            if (archive) writer.WriteString("kind", "archive");
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var request = await RequestAsync(HttpMethod.Post, _upload + "?uploadType=multipart&fields=id", ct);
        var content = new MultipartContent("related");
        content.Add(new StringContent(Encoding.UTF8.GetString(metadata.ToArray()), Encoding.UTF8, "application/json"));
        content.Add(new StringContent(json, Encoding.UTF8, "application/json")); request.Content = content;
        using var doc = JsonDocument.Parse(await CloudHttp.ReadAsync(_http, request, ct, operation: archive ? "Google Drive 上传历史归档" : "Google Drive 上传同步版本"));
        string key = doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException("Drive 未返回新修订标识。");
        return new(key, id, archive);
    }
    private async Task<List<DriveEntry>> FindFoldersAsync(CancellationToken ct)
        => await ListQueryAsync("trashed = false and mimeType = 'application/vnd.google-apps.folder' and appProperties has { key='sync' and value='" + FolderTag + "' }", false, ct);
    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        if (_folder != null) return;
        var folders = await FindFoldersAsync(ct);
        if (folders.Count == 0)
        {
            using var request = await RequestAsync(HttpMethod.Post, _api + "?fields=id", ct);
            request.Content = new StringContent("{\"name\":\"yuSnip\",\"mimeType\":\"application/vnd.google-apps.folder\",\"appProperties\":{\"sync\":\"" + FolderTag + "\"}}", Encoding.UTF8, "application/json");
            using var doc = JsonDocument.Parse(await CloudHttp.ReadAsync(_http, request, ct, operation: "Google Drive 创建同步文件夹"));
            string created = doc.RootElement.GetProperty("id").GetString() ?? "";
            if (created.Length == 0) throw new InvalidDataException("Drive 未返回同步文件夹标识。");
            // 双机同时首次同步可能各建一个目录，按稳定身份选择，下轮归并文件。
            folders = await FindFoldersAsync(ct);
            folders.Add(new(new(created, ""), []));
        }
        _folder = folders.Select(f => f.File.Key).Order(StringComparer.Ordinal).First();
    }
    public async Task PrepareAsync(Func<bool> isCurrent, CancellationToken ct)
    {
        if (!isCurrent()) return;
        await EnsureFolderAsync(ct);
        foreach (var entry in await ListQueryAsync(RevisionQuery, true, ct))
        {
            if (!isCurrent()) return;
            if (entry.Parents.Length == 1 && entry.Parents[0] == _folder) continue;
            string url = _api + "/" + Uri.EscapeDataString(entry.File.Key) + "?addParents=" + Uri.EscapeDataString(_folder!) + "&fields=id,parents";
            if (entry.Parents.Length > 0) url += "&removeParents=" + Uri.EscapeDataString(string.Join(",", entry.Parents.Where(p => p != _folder)));
            using var request = await RequestAsync(HttpMethod.Patch, url, ct);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var doc = JsonDocument.Parse(await CloudHttp.ReadAsync(_http, request, ct, operation: "Google Drive 迁移同步文件"));
            if (!doc.RootElement.GetProperty("parents").EnumerateArray().Any(p => p.GetString() == _folder))
                throw new IOException("Drive 同步文件迁移未完成，将在下次同步重试。");
        }
    }
    private async Task DeleteRawAsync(CloudRemoteFile file, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Delete, _api + "/" + Uri.EscapeDataString(file.Key), ct);
        await CloudHttp.ReadAsync(_http, request, ct, true, "Google Drive 清理历史文件");
    }
    public async Task CompactAsync(IReadOnlyList<CloudRevision> revisions, Func<bool> isCurrent, CancellationToken ct)
    {
        await EnsureFolderAsync(ct);
        await _archive.CompactAsync(revisions, isCurrent, (id, json, token) => CreateFileAsync(id, json, true, token), ReadRawAsync, DeleteRawAsync, ct);
    }
    public async Task<string?> ReadLegacyAsync(CancellationToken ct)
    {
        var files = await ListQueryAsync("trashed = false and name = 'yuSnip-config-v1.json'", false, ct);
        if (files.Count > 1) throw new IOException("Drive 有多个同名旧备份，请先整理重复备份。");
        return files.Count == 0 ? null : await ReadRawAsync(files[0].File, ct);
    }
}
