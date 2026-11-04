using System;
using System.IO;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// FFmpeg 定位与就绪探测（计划 §3.5）。单一入口 <see cref="TryFind"/>。
    /// 仅使用应用统一管理的依赖包目录（%APPDATA%\yuSnip\deps\ffmpeg\<version>\ffmpeg.exe），
    /// 不再隐式回退程序目录、系统 PATH 或未收编的外部文件，确保与设置页组件状态 100% 一致。
    /// 命中者跑一次 `ffmpeg -version` 可执行才算就绪；结果按路径文件 mtime 缓存。
    /// </summary>
    public static class FfmpegLocator
    {
        private static string? _cachedPath;
        private static DateTime _cachedMtime;
        private static string? _cachedManual;

        /// <summary>依赖包落位目录（收编或按需下载后的正式位置）；未就绪返回 null。</summary>
        public static string? DepsExe => Deps.DependencyManager.FilePath(Deps.DepIds.Ffmpeg, "ffmpeg.exe");

        /// <summary>旧「一键下载 FFmpeg」落位处，现为收编来源与离线兜底搜索位。</summary>
        public static string DownloadedDir => Deps.DependencyCatalog.LegacyFfmpegDir;

        public static string DownloadedExe => Path.Combine(DownloadedDir, "ffmpeg.exe");

        /// <summary>
        /// 探测就绪的 ffmpeg.exe 绝对路径；找不到返回 null。
        /// <paramref name="manualPath"/> 变化会使缓存失效（对应设置项改动）。
        /// </summary>
        public static string? TryFind(string? manualPath = null)
        {
            bool manualChanged = !string.Equals(manualPath ?? "", _cachedManual ?? "", StringComparison.Ordinal);
            if (!manualChanged && _cachedPath != null && IsCachedFresh())
                return _cachedPath;

            string? found = Search(manualPath);
            _cachedManual = manualPath ?? "";
            if (found != null && File.Exists(found))
            {
                _cachedPath = found;
                try { _cachedMtime = File.GetLastWriteTimeUtc(found); } catch { _cachedMtime = default; }
            }
            else
            {
                _cachedPath = null;
            }
            return found;
        }

        /// <summary>手动路径设置变更时清缓存。</summary>
        public static void Invalidate()
        {
            _cachedPath = null; _cachedManual = null;
            lock (PeekLock) { _peekPath = null; _peekManual = null; }
            // 换 ffmpeg 就等于换了一套 ddagrab 序号表，探测缓存一并作废（本方法是"ffmpeg 变了"的唯一钩子）
            FfmpegProcess.InvalidateDdaCache();
        }

        // ================= 缓存快检（M4 三级漏斗：开始录制前不阻塞倒计时） =================

        private static readonly object PeekLock = new();
        private static string? _peekPath;
        private static string? _peekManual;
        private static DateTime _peekAt;

        /// <summary>后台静默探测一次并缓存结果（应用启动与缓存过期时调用；探测要跑子进程，绝不放 UI 线程）。</summary>
        public static void WarmUp(string? manualPath)
        {
            string manual = manualPath ?? "";
            System.Threading.Tasks.Task.Run(() =>
            {
                string? found = null;
                try
                {
                    found = TryFind(manual);
                    // 顺带把 ddagrab 输出表也探出来：探测要在"开始录制"那一拍同步跑的话，
                    // 会连着起 1~3 个 ffmpeg 子进程、把 UI 线程堵上千把毫秒（正是起跑那一刻的卡顿）。
                    if (found != null) FfmpegProcess.DdaOutputs(found);
                }
                catch { }
                lock (PeekLock) { _peekPath = found; _peekManual = manual; _peekAt = DateTime.UtcNow; }
            });
        }

        /// <summary>读最近一次预热结果（毫秒级返回）。手动路径变了或缓存超 10 分钟会顺手补一发后台预热，但本次仍返回旧值。</summary>
        public static string? PeekCached(string? manualPath)
        {
            string manual = manualPath ?? "";
            bool stale;
            string? current;
            lock (PeekLock)
            {
                current = _peekPath;
                stale = _peekManual != manual || (DateTime.UtcNow - _peekAt) > TimeSpan.FromMinutes(10);
            }
            if (stale) WarmUp(manual);
            return current;
        }

        private static bool IsCachedFresh()
        {
            if (_cachedPath == null) return false;
            try
            {
                if (!File.Exists(_cachedPath)) return false;
                return File.GetLastWriteTimeUtc(_cachedPath) == _cachedMtime;
            }
            catch { return false; }
        }

        private static string? Search(string? manualPath)
        {
            // 仅使用应用统一管理的依赖包目录（按需下载／收编落位处）
            _ = manualPath;
            string? depsExe = DepsExe;
            if (depsExe != null && IsReady(depsExe)) return depsExe;

            return null;
        }

        /// <summary>真正跑一次 `-version` 判可执行（比 File.Exists 严格，能排除损坏/架构不符）。</summary>
        public static bool IsReady(string exe)
        {
            try
            {
                if (!File.Exists(exe)) return false;
                return FfmpegProcess.RunProbe(exe, "-hide_banner -version", out _, 4000);
            }
            catch { return false; }
        }

        /// <summary>取 ffmpeg 版本号短串（"n7.1-2024-01" → "7.1"）；解析失败返回空串。设置卡状态行用。</summary>
        public static string VersionOf(string exe)
        {
            try
            {
                if (!FfmpegProcess.RunProbe(exe, "-hide_banner -version", out string output, 4000)) return "";
                string first = (output ?? "").Split('\n')[0];
                int idx = first.IndexOf("version ", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return "";
                string token = first.Substring(idx + 8).TrimStart('n', 'N');
                int cut = token.IndexOfAny(new[] { ' ', '-', ',' });
                if (cut > 0) token = token.Substring(0, cut);
                return token;
            }
            catch { return ""; }
        }
    }
}
