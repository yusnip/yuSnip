using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Update
{
    /// <summary>版本清单数据（与服务器 /yusnip/version.json 字段一一对应，NativeAOT 零反射手动解析）。</summary>
    public sealed class UpdateInfo
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public int Build { get; set; }
        public string DownloadUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string Changelog { get; set; } = "";
        public bool ForceUpdate { get; set; }
    }

    /// <summary>
    /// 自动更新服务：按构建时选定的发行渠道检查版本并流式下载安装包。
    /// 版本比较基于 build 号（major*10000 + minor*100 + patch），与服务端 deploy 脚本计算规则一致。
    /// </summary>
    public static class UpdateService
    {
        public const string VersionJsonUrl = ReleaseChannel.VersionManifest;

        private static readonly Lazy<string> _versionStr = new(GetVersionString);
        private static readonly Lazy<int> _buildNum = new(GetBuildNumber);

        /// <summary>当前程序版本（如 "1.0.0"）。</summary>
        public static string CurrentVersionStr => _versionStr.Value;

        /// <summary>当前程序 build 号（major*10000 + minor*100 + patch）。</summary>
        public static int CurrentBuildNum => _buildNum.Value;

        private static string GetVersionString()
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { /* AOT/宿主异常时回退 0.0.0 */ }
            return "0.0.0";
        }

        private static int GetBuildNumber()
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                if (v != null) return v.Major * 10000 + v.Minor * 100 + v.Build;
            }
            catch { }
            return 0;
        }

        /// <summary>服务器是否提供比当前更新的版本。</summary>
        public static bool HasNewer(UpdateInfo info) => info != null && info.Build > CurrentBuildNum;

        /// <summary>
        /// 拉取并解析服务器版本清单。网络失败/HTTP 非 2xx/JSON 非法时抛出异常（调用方决定是否静默）。
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            using var hc = ReleaseChannel.CreateClient(TimeSpan.FromSeconds(10));
            using var resp = await hc.GetAsync(VersionJsonUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("版本服务器返回 HTTP " + (int)resp.StatusCode);
            }
            string json = await resp.Content.ReadAsStringAsync(ct);
            return ParseInfo(json);
        }

        private static UpdateInfo ParseInfo(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var info = new UpdateInfo();
            if (root.TryGetProperty("name", out var p)) info.Name = p.GetString() ?? "";
            if (root.TryGetProperty("version", out p)) info.Version = p.GetString() ?? "";
            if (root.TryGetProperty("build", out p) && p.ValueKind == JsonValueKind.Number) info.Build = p.GetInt32();
            if (root.TryGetProperty("download_url", out p)) info.DownloadUrl = p.GetString() ?? "";
            if (root.TryGetProperty("sha256", out p)) info.Sha256 = p.GetString() ?? "";
            if (root.TryGetProperty("updated_at", out p)) info.UpdatedAt = p.GetString() ?? "";
            if (root.TryGetProperty("changelog", out p)) info.Changelog = p.GetString() ?? "";
            if (root.TryGetProperty("force_update", out p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                info.ForceUpdate = p.GetBoolean();
            }
            return info;
        }

        /// <summary>
        /// 流式下载安装包到目标路径，progress 汇报（已下载字节, 总字节或 -1）。
        /// </summary>
        public static async Task DownloadAsync(
            UpdateInfo info, string destPath, IProgress<(long read, long total)>? progress,
            CancellationToken ct = default)
        {
            string url = ReleaseChannel.UpdateDownload(info.DownloadUrl);
            bool verifyHash = ReleaseChannel.IsGitHub || info.Sha256.Length > 0;
            if (verifyHash && (info.Sha256.Length != 64 || !info.Sha256.All(char.IsAsciiHexDigit)))
                throw new InvalidDataException("更新清单缺少有效的 SHA-256 校验值。");
            using var hc = ReleaseChannel.CreateClient(TimeSpan.FromMinutes(10));
            using var resp = await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException("下载失败：服务器返回 HTTP " + (int)resp.StatusCode);
            }

            long total = resp.Content.Headers.ContentLength ?? -1;
            string dir = Path.GetDirectoryName(destPath) ?? "";
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hash = verifyHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            byte[] buffer = new byte[81920];
            long readTotal = 0;
            int read;
            long lastReport = 0;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                hash?.AppendData(buffer, 0, read);
                readTotal += read;
                if (progress != null && (readTotal - lastReport >= 512 * 1024 || readTotal == total))
                {
                    lastReport = readTotal;
                    progress.Report((readTotal, total));
                }
            }
            if (total >= 0 && readTotal != total)
                throw new InvalidDataException("更新下载未完成。");
            if (hash != null && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件的 SHA-256 校验失败。");
            progress?.Report((readTotal, total));
        }

        /// <summary>
        /// 下载完成后启动隐藏 PowerShell 等待本进程退出、将 .new 覆盖为正式 exe 并重启。
        /// </summary>
        public static void LaunchSelfReplace(string exePath)
        {
            string newExePath = exePath + ".new";
            string psScript =
                "Start-Sleep -Seconds 1; " +
                $"while (Test-Path '{newExePath}') {{ try {{ Move-Item -Path '{newExePath}' -Destination '{exePath}' -Force -ErrorAction Stop }} catch {{ Start-Sleep -Seconds 1 }} }}; " +
                $"Start-Process -FilePath '{exePath}'";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + psScript + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
        }
    }
}
