using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>解析用户指定的 FFmpeg，或在未指定时使用应用管理的依赖包。</summary>
    public static class FfmpegLocator
    {
        private const string ExeName = "ffmpeg.exe";
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, VersionProbe> Versions = new(StringComparer.OrdinalIgnoreCase);

        private sealed record VersionProbe(DateTime ModifiedUtc, long Length, string Version);

        public static string? DepsExe => Deps.DependencyManager.FilePath(Deps.DepIds.Ffmpeg, ExeName);

        public static string DownloadedDir => Deps.DependencyCatalog.LegacyFfmpegDir;
        public static string DownloadedExe => Path.Combine(DownloadedDir, ExeName);

        /// <summary>接受包含 ffmpeg.exe 的目录、FFmpeg 解压根目录，以及旧配置中保存的完整文件路径。</summary>
        public static string? ResolveManualExecutable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim();
            if (!Path.IsPathFullyQualified(path)) return null;

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                    return string.Equals(Path.GetFileName(fullPath), ExeName, StringComparison.OrdinalIgnoreCase) ? fullPath : null;

                string direct = Path.Combine(fullPath, ExeName);
                if (File.Exists(direct)) return direct;
                string bin = Path.Combine(fullPath, "bin", ExeName);
                return File.Exists(bin) ? bin : null;
            }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>显式指定目录时只使用其可执行文件。未设置时走应用管理的组件。</summary>
        public static string? TryFind(string? manualPath = null)
        {
            string? exe = string.IsNullOrWhiteSpace(manualPath) ? DepsExe : ResolveManualExecutable(manualPath);
            return exe != null && IsReady(exe) ? exe : null;
        }

        public static string UnavailableMessage(string? manualPath) => string.IsNullOrWhiteSpace(manualPath)
            ? "找不到可用的 FFmpeg。请到「设置 → 依赖运行」下载组件或选择目录。"
            : "自定义 FFmpeg 不可用。请到「设置 → 依赖运行」重新选择目录或恢复默认。";

        public static void Invalidate()
        {
            lock (CacheLock) Versions.Clear();
            FfmpegProcess.InvalidateDdaCache();
        }

        /// <summary>在后台预热所选程序及其 ddagrab 输出表，避免录屏起步时重复探测。</summary>
        public static void WarmUp(string? manualPath)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (TryFind(manualPath) is { } exe) FfmpegProcess.DdaOutputs(exe);
                }
                catch { }
            });
        }

        public static bool IsReady(string exe) => VersionOf(exe).Length > 0;

        /// <summary>通过一次有超时的版本探测验证程序，缓存按路径及文件状态命中。</summary>
        public static string VersionOf(string exe)
        {
            try
            {
                var file = new FileInfo(exe);
                if (!file.Exists) return "";
                DateTime modified = file.LastWriteTimeUtc;
                long length = file.Length;
                lock (CacheLock)
                    if (Versions.TryGetValue(exe, out var cached) && cached.ModifiedUtc == modified && cached.Length == length)
                        return cached.Version;

                const string prefix = "ffmpeg version ";
                if (!FfmpegProcess.RunProbe(exe, "-hide_banner -version", out string output, requireSuccess: true)
                    || !output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
                string version = output[prefix.Length..].Split([' ', '\r', '\n', '\t'], 2)[0];
                if (version.Length == 0) return "";
                lock (CacheLock) Versions[exe] = new(modified, length, version);
                return version;
            }
            catch { return ""; }
        }
    }
}
