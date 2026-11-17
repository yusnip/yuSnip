using System.Net.Http;

namespace ScreenCaptureTool.Core;

public static class ReleaseChannel
{
    private const string WebsiteDownloads = "https://yusnip.top/yusnip/";
    private const string GitHubLatest = "https://github.com/yusnip/yuSnip/releases/latest/download/";

#if YUSNIP_WEBSITE_CHANNEL
    public const string Name = "website";
    private const string ManifestBase = WebsiteDownloads;
#else
    public const string Name = "github";
    private const string ManifestBase = GitHubLatest;
#endif
    public static bool IsGitHub => Name == "github";

    public const string VersionManifest = ManifestBase + "version.json";
    public const string DependencyManifest = ManifestBase + "deps.json";

    internal static string DependencyDownload(string id, string fileName, string url) =>
        IsGitHub ? GitHubArtifact(url, allowFfmpeg: id == "ffmpeg") : WebsiteDownloads + Uri.EscapeDataString(fileName);

    internal static string UpdateDownload(string url) => IsGitHub ? GitHubArtifact(url, allowFfmpeg: false) : url;

    private static bool IsHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static bool IsGitHubRelease(Uri uri, bool allowFfmpeg) =>
        IsHttps(uri) && uri.Host == "github.com"
        && (uri.AbsolutePath.StartsWith("/yusnip/yuSnip/releases/", StringComparison.OrdinalIgnoreCase)
            || (allowFfmpeg && uri.AbsolutePath.StartsWith("/GyanD/codexffmpeg/releases/download/", StringComparison.OrdinalIgnoreCase)));

    private static string GitHubArtifact(string url, bool allowFfmpeg)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsGitHubRelease(uri, allowFfmpeg))
            throw new InvalidDataException("下载地址不属于此发行渠道。");
        return uri.AbsoluteUri;
    }

    internal static HttpClient CreateClient(TimeSpan timeout) =>
        IsGitHub ? new HttpClient(new GitHubReleaseRedirects()) { Timeout = timeout } : new HttpClient { Timeout = timeout };

    // GitHub 的 Release 会跳转到资源 CDN。每一跳都先检查目标，避免跨渠道请求。
    private sealed class GitHubReleaseRedirects() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            for (int hop = 0; hop < 8; hop++)
            {
                using var next = new HttpRequestMessage(HttpMethod.Get, uri);
                next.Headers.Range = request.Headers.Range;
                var response = await base.SendAsync(next, ct).ConfigureAwait(false);
                var location = response.Headers.Location;
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || location == null)
                    return response;

                response.Dispose();
                uri = new Uri(uri, location);
                if (!IsGitHubRelease(uri, allowFfmpeg: true)
                    && !(IsHttps(uri) && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new HttpRequestException("GitHub 下载跳转到了此渠道以外的地址。");
            }
            throw new HttpRequestException("GitHub 下载跳转次数过多。");
        }
    }
}
