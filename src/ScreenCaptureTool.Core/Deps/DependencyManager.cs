using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenCaptureTool.Core.Deps
{
    /// <summary>某个依赖的当前读数快照（UI 只读这个，不自行推断状态）。</summary>
    public sealed class DepSnapshot
    {
        public string Id { get; init; } = "";
        public DepState State { get; init; }
        public string Version { get; init; } = "";
        public long Done { get; init; }
        /// <summary>总字节；0 表示总数未知（进度条走不确定态）。</summary>
        public long Total { get; init; }
        public double SpeedBps { get; init; }
        public string Error { get; init; } = "";
        /// <summary>已落盘体积（就绪态读数）。</summary>
        public long OnDiskBytes { get; init; }

        public int Percent => Total > 0 ? (int)Math.Clamp(Done * 100 / Total, 0, 100) : 0;

        /// <summary>剩余时间不足 2 秒就不显示（读数口径：宁可不报，不报跳动的数）。</summary>
        public long EtaSeconds => SpeedBps > 1 && Total > Done ? (long)((Total - Done) / SpeedBps) : 0;
        public bool ShowEta => EtaSeconds >= 2;
    }

    /// <summary>
    /// 三个外置依赖（OCR 模型／抠图模型／FFmpeg）的状态真值唯一来源。
    /// 下载器不挂任何窗口生命周期：关掉进度窗与设置窗，下载照跑（设计稿 05 节口径）。
    /// </summary>
    public static class DependencyManager
    {
        /// <summary>服务器清单地址（与 version.json 同目录同域名）。</summary>
        public const string ManifestUrl = ReleaseChannel.DependencyManifest;

        /// <summary>状态跃迁时触发（下载中不算，进度另有 <see cref="ProgressTick"/>）。</summary>
        public static event Action<string>? Changed;
        /// <summary>下载进度节拍，最快 250ms 一次。</summary>
        public static event Action<string>? ProgressTick;

        private sealed class Runtime
        {
            public DepState State = DepState.Absent;
            public long Done, Total, OnDisk;
            public double Speed;
            public string Error = "", Version = "";
            public CancellationTokenSource? Cts;
            public Task<bool>? Worker;
            /// <summary>本趟要不要在「新包完整到货之后」撤掉旧落盘件（只有「重新下载」置它）。普通下载为 false ⇒ 旧件一根手指都不碰。</summary>
            public bool ReplaceExisting;
            public readonly Queue<(DateTime At, long Bytes)> Samples = new();
            public DateTime LastTickUtc;
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<string, Runtime> _rt = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static DepManifest? _manifest;
        private static Dictionary<string, DepLockEntry> _lock = DependencyCatalog.LoadLock();
        private static Task? _adoption;
        private static DateTime _manifestFetchedUtc = DateTime.MinValue;

        private static Runtime Rt(string id)
        {
            lock (Sync)
            {
                if (!_rt.TryGetValue(id, out var r)) { r = new Runtime(); _rt[id] = r; }
                return r;
            }
        }

        /// <summary>取当前读数；清单里有的依赖会顺带把版本号带出来。</summary>
        public static DepSnapshot Get(string id)
        {
            var r = Rt(id);
            var pkg = CurrentManifest()?.Find(id);
            lock (Sync)
            {
                return new DepSnapshot
                {
                    Id = id,
                    State = r.State,
                    Version = r.Version.Length > 0 ? r.Version : (pkg?.Version ?? ""),
                    Done = r.Done,
                    Total = r.Total,
                    SpeedBps = r.Speed,
                    Error = r.Error,
                    OnDiskBytes = r.OnDisk,
                };
            }
        }

        /// <summary>是否已就绪（廉价判断：登记在案＋体积吻合，不重算全量哈希）。</summary>
        public static bool IsReady(string id)
        {
            var m = CurrentManifest();
            if (m == null) return false;
            var p = m.Find(id);
            if (p == null) return false;
            lock (Sync) return DependencyCatalog.IsReady(m, _lock, p);
        }

        /// <summary>就绪依赖里某个文件的绝对路径；未就绪返回 null。</summary>
        public static string? FilePath(string id, string name)
        {
            var m = CurrentManifest();
            var p = m?.Find(id);
            if (p == null || !IsReady(id)) return null;
            string path = DependencyCatalog.FilePath(p, name);
            return File.Exists(path) ? path : null;
        }

        /// <summary>就绪依赖的落盘目录；未就绪返回 null。FfmpegLocator 的「已下载目录」即指向这里。</summary>
        public static string? PackageDir(string id)
        {
            var p = CurrentManifest()?.Find(id);
            if (p == null || !IsReady(id)) return null;
            return DependencyCatalog.PackageDir(p);
        }

        /// <summary>当前清单（内存 → 磁盘缓存）。不触发网络。</summary>
        public static DepManifest? CurrentManifest()
        {
            lock (Sync)
            {
                _manifest ??= DependencyCatalog.LoadCachedManifest();
                if (_manifest != null) RefreshStatesFromLockLocked();
                return _manifest;
            }
        }

        /// <summary>
        /// 启动时后台跑一次：拉清单 → 按文件级哈希收编老位置上的现成文件。
        /// 老用户升级后三行直接就是「已就绪」，一个字节都不重下（设计稿 07 节）。
        /// </summary>
        public static Task StartAdoption()
        {
            lock (Sync)
            {
                if (_adoption != null) return _adoption;
                _adoption = Task.Run(AdoptionCoreAsync);
                return _adoption;
            }
        }

        private static async Task AdoptionCoreAsync()
        {
            try
            {
                await RefreshManifestAsync(force: false).ConfigureAwait(false);
                var m = CurrentManifest();
                if (m == null) return;

                var adopted = new List<string>();
                foreach (var p in m.Packages)
                {
                    if (IsReady(p.Id)) continue;
                    if (await AdoptPackageAsync(p).ConfigureAwait(false)) adopted.Add(p.Id);
                }
                lock (Sync) RefreshStatesFromLockLocked();
                if (adopted.Count > 0)
                {
                    AppLogger.Info($"Deps: 老组件收编完成，{adopted.Count} 个包零字节落位");
                    foreach (string id in adopted) { PostReady(id); Changed?.Invoke(id); }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Deps: 收编流程异常（不影响按需下载）：" + ex.Message);
            }
        }

        /// <summary>把一个包里能对得上哈希的文件从老位置同盘改名搬进新目录。全中才算就绪。</summary>
        private static async Task<bool> AdoptPackageAsync(DepPackageDef p)
        {
            string[] legacyDirs = LegacyDirsFor(p.Id);
            if (legacyDirs.Length == 0) return false;
            string targetDir = DependencyCatalog.PackageDir(p);

            foreach (var f in p.Files)
            {
                string target = Path.Combine(targetDir, f.Name);
                if (File.Exists(target)) continue;

                foreach (string dir in legacyDirs)
                {
                    string cand = Path.Combine(dir, f.Name);
                    try
                    {
                        var fi = new FileInfo(cand);
                        if (!fi.Exists || fi.Length != f.Size) continue;
                        long len = fi.Length;
                        string sha = await Task.Run(() => DependencyCatalog.Sha256File(cand)).ConfigureAwait(false);
                        if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase)) continue;

                        Directory.CreateDirectory(targetDir);
                        File.Move(cand, target);
                        AppLogger.Info($"Deps: 收编 {f.Name} {len / (1024 * 1024)} MB ← {cand}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Deps: 收编 {f.Name} 失败：{ex.Message}");
                    }
                }
            }

            if (!DependencyCatalog.AllFilesPresentWithSize(p)) return false;
            bool ok = await Task.Run(() => DependencyCatalog.VerifyFilesByHash(p)).ConfigureAwait(false);
            if (!ok) return false;

            lock (Sync)
            {
                DependencyCatalog.WriteLock(p.Id, p, "adoption");
                _lock = DependencyCatalog.LoadLock();
            }
            return true;
        }

        private static string[] LegacyDirsFor(string id) => id switch
        {
            DepIds.Ocr => new[] { Path.Combine(AppSettings.SettingsDirectory, "ocr") },
            DepIds.Matting => new[] { Path.Combine(AppSettings.SettingsDirectory, "matting") },
            DepIds.Ffmpeg => new[] { DependencyCatalog.LegacyFfmpegDir },
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// 拉服务器清单并落缓存。<paramref name="force"/>＝false 时 10 分钟内不重复请求。
        /// 失败不抛：沿用磁盘上的旧缓存，返回 false 让调用方决定要不要提示。
        /// </summary>
        public static async Task<bool> RefreshManifestAsync(bool force)
        {
            lock (Sync)
            {
                if (!force && _manifest != null && (DateTime.UtcNow - _manifestFetchedUtc) < TimeSpan.FromMinutes(10))
                    return true;
            }
            try
            {
                using var hc = ReleaseChannel.CreateClient(TimeSpan.FromSeconds(10));
                string json = await hc.GetStringAsync(ManifestUrl).ConfigureAwait(false);
                if (!DepManifest.TryParse(json, out var parsed) || parsed == null)
                {
                    AppLogger.Warn("Deps: 清单解析失败（结构不合法或 schema 不支持），沿用本地缓存");
                    return false;
                }
                foreach (var package in parsed.Packages)
                    _ = ReleaseChannel.DependencyDownload(package.Id, package.FileName, package.DownloadUrl);
                DependencyCatalog.SaveManifestCache(json);
                lock (Sync)
                {
                    _manifest = parsed;
                    _manifestFetchedUtc = DateTime.UtcNow;
                    _lock = DependencyCatalog.LoadLock();
                    RefreshStatesFromLockLocked();
                }
                AppLogger.Info($"Deps: 清单已更新（{parsed.Packages.Count} 个包，generated={parsed.Generated}）");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Deps: 拉取清单失败：" + ex.Message);
                lock (Sync)
                {
                    if (_manifest == null) _manifest = DependencyCatalog.LoadCachedManifest();
                    return _manifest != null;
                }
            }
        }

        /// <summary>按磁盘登记刷新所有包的状态；从 Absent↔Ready 变化时发通知。</summary>
        private static void RefreshStatesFromLockLocked()
        {
            if (_manifest == null) return;
            foreach (var p in _manifest.Packages)
            {
                if (!_rt.TryGetValue(p.Id, out var r))
                {
                    r = new Runtime();
                    _rt[p.Id] = r;
                }
                if (r.State is DepState.Downloading or DepState.Verifying or DepState.Queued) continue;

                bool ready = DependencyCatalog.IsReady(_manifest, _lock, p);
                var next = ready ? DepState.Ready : (r.State == DepState.Failed ? DepState.Failed : DepState.Absent);
                r.State = next;
                r.Version = p.Version;
                if (ready) r.OnDisk = p.Files.Sum(f => f.Size);
            }
        }

        /// <summary>
        /// 功能入口用：缺件即排队下载并等它就绪。<paramref name="beginDownload"/>＝false 时只等不催（已在下就用现成的）。
        /// 返回是否就绪。UI 侧只负责把进度窗开出来，不驱动下载。
        /// </summary>
        public static Task<bool> EnsureReadyAsync(string id, CancellationToken ct = default)
        {
            var r = Rt(id);
            lock (Sync)
            {
                if (_manifest != null && DependencyCatalog.IsReady(_manifest, _lock, FindOrThrow(id)))
                    return Task.FromResult(true);
                if (r.Worker is { IsCompleted: false })
                    return WaitAsync(r.Worker, ct);
            }
            var worker = Task.Run(() => RunAsync(id, ct));
            lock (Sync) r.Worker = worker;
            return WaitAsync(worker, ct);
        }

        private static async Task<bool> WaitAsync(Task<bool> worker, CancellationToken ct)
        {
            try { return await worker.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        private static DepPackageDef FindOrThrow(string id) =>
            (_manifest ?? throw new InvalidOperationException("清单未就绪")).Find(id)
            ?? throw new InvalidOperationException("清单里没有该依赖");

        /// <summary>用户在进度窗点「取消下载」：只中止本包，已下的 .part 留着续传。</summary>
        public static void Cancel(string id)
        {
            CancellationTokenSource? cts;
            lock (Sync) cts = Rt(id).Cts;
            try { cts?.Cancel(); } catch { }
        }

        /// <summary>
        /// 「重新下载」：走同一条下载链，但旧落盘件**等新包完整到货之后**才撤（见 <see cref="ReplaceOldPackageDir"/>）。
        /// 原先是进门先把目录删掉再下——点一下然后取消，一整个已就绪的包就白丢了（ffmpeg 实测 100 MB）。
        /// </summary>
        public static Task<bool> RedownloadAsync(string id, CancellationToken ct = default)
        {
            var p = CurrentManifest()?.Find(id);
            if (p == null) return Task.FromResult(false);

            var r = Rt(id);
            Task<bool> worker;
            lock (Sync)
            {
                // 已经在下这一项了就不另起一趟：那趟自己会下完，重复起只会让两个 worker 抢同一个目录
                if (r.Worker is { IsCompleted: false } running) return WaitAsync(running, ct);
                r.ReplaceExisting = true;
                // 不经 EnsureReadyAsync：它对「已就绪」是立即返回 true，而重新下载要的恰恰是"明明就绪也再下一趟"
                worker = Task.Run(() => RunAsync(id, ct));
                r.Worker = worker;
            }
            return WaitAsync(worker, ct);
        }

        // ================= 下载主流程 =================

        private static async Task<bool> RunAsync(string id, CancellationToken ct)
        {
            var r = Rt(id);
            bool arrived = false;   // 整包是否已经落到本机（决定取消时那句提示怎么说）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (Sync) { r.Cts = cts; r.Error = ""; r.Done = 0; r.Total = 0; r.Speed = 0; }

            try
            {
                // 单飞队列：同时只跑一个包，多入口一起点名也不至于把带宽和磁盘搅乱
                SetState(r, id, DepState.Queued);
                await Gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    SetState(r, id, DepState.Downloading);
                    if (CurrentManifest() == null && !await RefreshManifestAsync(force: false).ConfigureAwait(false))
                        throw new DependencyFailedException("无法获取组件清单：网络连接失败");

                    var p = CurrentManifest()?.Find(id)
                            ?? throw new DependencyFailedException("组件清单里没有这一项，请更新程序后重试");

                    long need = p.Files.Sum(f => f.Size);
                    // 撤旧下完之前新旧并存，空间闸门得把旧那份也算进去（先删后下的年代不用算）
                    EnsureFreeSpace(need + p.Size + OldBytesIfReplacing(r, p));

                    string zipPath = await DownloadZipAsync(p, r, id, cts.Token).ConfigureAwait(false);
                    arrived = true;

                    SetState(r, id, DepState.Verifying);
                    // 整包 SHA-256 已在 DownloadZipAsync 里对上，此刻才撤旧：取消／断网／关机都还有原来那份可用
                    ReplaceOldPackageDir(r, p);
                    Extract(p, zipPath, cts.Token);
                    if (!DependencyCatalog.VerifyFilesByHash(p))
                        throw new DependencyFailedException("文件校验不通过：组件包可能已损坏，请重试");

                    try { File.Delete(zipPath); } catch { }
                    lock (Sync)
                    {
                        DependencyCatalog.WriteLock(id, p, _lock.ContainsKey(id) ? "redownload" : "download");
                        _lock = DependencyCatalog.LoadLock();
                        r.OnDisk = need;
                    }
                    SetState(r, id, DepState.Ready);
                    AppLogger.Info($"Deps: {id} {p.Version} 已就绪（落盘 {need / (1024 * 1024)} MB）");
                    PostReady(id);
                    return true;
                }
                finally { Gate.Release(); }
            }
            catch (OperationCanceledException)
            {
                SetState(r, id, DepState.Absent);
                // 到货之后才取消 ⇒ 旧件已撤、整包在本机：说清"再点一次就装回来"，别让人以为白下了
                AppLogger.Info(arrived
                    ? $"Deps: {id} 在解包阶段被取消（整包已在本机，再点一次「下载」即刻装回）"
                    : $"Deps: {id} 下载已取消（旧落盘件没动，下一次点「下载」接着续传）");
                return false;
            }
            catch (DependencyFailedException ex)
            {
                Fail(r, id, ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Fail(r, id, "下载失败：" + ShortOuter(ex));
                return false;
            }
            finally
            {
                // 一次点击只兑一次撤旧：下载半路取消或失败，意图就此作废，别留到下一趟普通下载
                lock (Sync) { r.Cts = null; r.ReplaceExisting = false; }
            }
        }

        /// <summary>落位后叫醒各功能自己的缓存：FFmpeg 换文件等于换一套 ddagrab 序号表。</summary>
        private static void PostReady(string id)
        {
            if (id == DepIds.Ffmpeg) RefreshFfmpegProbe();
        }

        /// <summary>重跑一遍 FFmpeg 定位与探测：就绪、卸载两条路都要它，缓存里那份路径才算数。</summary>
        private static void RefreshFfmpegProbe()
        {
            try
            {
                Recording.FfmpegLocator.Invalidate();
                Recording.FfmpegLocator.WarmUp(AppSettings.Current.RecFfmpegPath);
            }
            catch (Exception ex) { AppLogger.Warn("Deps: 刷新 FFmpeg 探测失败：" + ex.Message); }
        }

        /// <summary>这一趟要不要撤旧，顺带报旧落盘件占多少（不给「重新下载」时用）。</summary>
        private static long OldBytesIfReplacing(Runtime r, DepPackageDef p)
        {
            bool replacing;
            lock (Sync) replacing = r.ReplaceExisting;
            return replacing ? DirBytes(DependencyCatalog.PackageDir(p), null) : 0;
        }

        /// <summary>
        /// 兑「重新下载」的撤旧：新包已完整到货并整包哈希对上，删掉旧目录再解包才安全。
        /// 必须真删——<see cref="Extract"/> 对"已在位且哈希吻合"的文件是跳过的，不删就成了空转一趟。
        /// 删不动（被占用）只降级成"旧件留原地、按名覆盖"，不拦这次落位。
        /// </summary>
        private static void ReplaceOldPackageDir(Runtime r, DepPackageDef p)
        {
            bool replacing;
            lock (Sync) { replacing = r.ReplaceExisting; r.ReplaceExisting = false; }
            if (!replacing) return;

            string dir = DependencyCatalog.PackageDir(p);
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, true);
                AppLogger.Info($"Deps: {p.Id} 新包已到货，旧落盘件已撤，开始解包");
            }
            catch (Exception ex) { AppLogger.Warn($"Deps: 清理 {p.Id} 旧文件失败：{ex.Message}"); }
        }

        private static void EnsureFreeSpace(long need)
        {
            try
            {
                string root = Path.GetPathRoot(DependencyCatalog.RootDir) ?? "";
                if (root.Length == 0) return;
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free < need + (64L << 20))
                    throw new DependencyFailedException(
                        $"磁盘剩余空间不足（需 {FormatMb(need + (64L << 20))}），请清理后重试");
            }
            catch (DependencyFailedException) { throw; }
            catch { /* 查不到容量就不拦，让下载自己撞真实错误 */ }
        }

        private static async Task<string> DownloadZipAsync(DepPackageDef p, Runtime r, string id, CancellationToken ct)
        {
            Directory.CreateDirectory(DependencyCatalog.DownloadDir);
            string zipPath = Path.Combine(DependencyCatalog.DownloadDir, p.FileName);
            string partPath = zipPath + ".part";

            // 上一趟整包已到货、只是解包前被打断：本地这份哈希对得上就直接用，同一包字节不必付两次
            if (File.Exists(zipPath) && new FileInfo(zipPath).Length == p.Size
                && string.Equals(await Task.Run(() => DependencyCatalog.Sha256File(zipPath), ct).ConfigureAwait(false),
                    p.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info($"Deps: {id} 本机已有完整组件包，跳过下载直接解包");
                return zipPath;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await FetchOnceAsync(p, r, id, zipPath, partPath, ct).ConfigureAwait(false);
                    string sha = await Task.Run(() => DependencyCatalog.Sha256File(partPath), ct).ConfigureAwait(false);
                    if (!string.Equals(sha, p.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new DependencyFailedException("组件包校验不通过（下载内容不完整），正在重试");

                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    File.Move(partPath, zipPath);
                    return zipPath;
                }
                catch (DependencyFailedException) when (attempt == 0)
                {
                    TryDelete(partPath);   // 残包没救，整包重来
                    SetState(r, id, DepState.Downloading);
                }
            }
            throw new DependencyFailedException("下载失败：网络中断");
        }

        private static async Task FetchOnceAsync(DepPackageDef p, Runtime r, string id,
            string zipPath, string partPath, CancellationToken ct)
        {
            // 两趟：第一趟带 Range 续传；服务器说范围不满足（远端换包了）就删残件从头再来一趟
            for (int pass = 0; ; pass++)
            {
                long existing = pass == 0 ? (File.Exists(partPath) ? new FileInfo(partPath).Length : 0) : 0;
                if (existing > p.Size) { TryDelete(partPath); existing = 0; }

                using var hc = ReleaseChannel.CreateClient(TimeSpan.FromMinutes(30));
                using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseChannel.DependencyDownload(p.Id, p.FileName, p.DownloadUrl));
                if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

                using var resp = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDelete(partPath);
                    if (pass == 0) continue;
                    throw new DependencyFailedException("组件包已更新，请重试");
                }
                if (!resp.IsSuccessStatusCode)
                    throw new DependencyFailedException($"服务器返回 HTTP {(int)resp.StatusCode}");

                bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
                long done = resumed ? existing : 0;
                long header = resp.Content.Headers.ContentLength ?? 0;
                long total = resumed ? existing + header : (header > 0 ? header : p.Size);

                lock (Sync) { r.Total = total; r.Done = done; r.Samples.Clear(); r.Samples.Enqueue((DateTime.UtcNow, done)); }

                var mode = resumed ? FileMode.Append : FileMode.Create;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, 1 << 17, true);

                var buf = new byte[1 << 17];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    ReportProgress(r, id, done, total);
                }
                if (done < total) throw new DependencyFailedException("连接中断");
                return;
            }
        }

        /// <summary>速度取近 3 秒滑动均值；250ms 节拍往上抛一次。</summary>
        private static void ReportProgress(Runtime r, string id, long done, long total)
        {
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                r.Done = done;
                r.Total = total;
                r.Samples.Enqueue((now, done));
                while (r.Samples.Count > 2 && (now - r.Samples.Peek().At).TotalSeconds > 3.0) r.Samples.Dequeue();
                var head = r.Samples.Peek();
                r.Speed = (now - head.At).TotalSeconds > 0.2 ? (done - head.Bytes) / (now - head.At).TotalSeconds : 0;

                if ((now - r.LastTickUtc).TotalMilliseconds < 250) return;
                r.LastTickUtc = now;
            }
            ProgressTick?.Invoke(id);
        }

        private static void Extract(DepPackageDef p, string zipPath, CancellationToken ct)
        {
            string dir = DependencyCatalog.PackageDir(p);
            Directory.CreateDirectory(dir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var f in p.Files)
            {
                ct.ThrowIfCancellationRequested();
                string target = Path.Combine(dir, f.Name);
                // 收编已在位的件不重复落盘：部分命中时只补差集
                if (File.Exists(target) && new FileInfo(target).Length == f.Size
                    && string.Equals(DependencyCatalog.Sha256File(target), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entry = zip.GetEntry(f.ArchiveEntry)
                    ?? throw new DependencyFailedException($"组件包内缺少 {f.Name}");
                entry.ExtractToFile(target, true);
            }
        }

        private static void SetState(Runtime r, string id, DepState s)
        {
            lock (Sync)
            {
                r.State = s;
                if (s == DepState.Ready || s == DepState.Absent) { r.Error = ""; r.Done = 0; r.Speed = 0; }
            }
            Changed?.Invoke(id);
        }

        private static void Fail(Runtime r, string id, string msg)
        {
            lock (Sync) { r.Error = msg; }
            AppLogger.Warn($"Deps: {id} 下载失败：{msg}");
            SetState(r, id, DepState.Failed);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>异常外因短串：只写用户看得懂的那半句，不塞堆栈。</summary>
        private static string ShortOuter(Exception ex) => ex switch
        {
            HttpRequestException h => h.Message.Contains("SSL") || h.Message.Contains("certificate")
                ? "安全连接建立失败" : "网络不可达",
            TaskCanceledException => "连接超时",
            IOException => "磁盘写入失败",
            _ => ex.Message.Length > 80 ? ex.Message.Substring(0, 80) : ex.Message,
        };

        // ================= 存储读数 =================

        /// <summary>deps 目录已占用字节（不含 _download 临时区）。</summary>
        public static long DepsBytes() => DirBytes(DependencyCatalog.RootDir, "_download");

        private static long DirBytes(string root, string? excludeTop)
        {
            long sum = 0;
            try
            {
                if (!Directory.Exists(root)) return 0;
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (excludeTop != null && f.Contains(Path.DirectorySeparatorChar + excludeTop + Path.DirectorySeparatorChar))
                        continue;
                    try { sum += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return sum;
        }

        public static string FormatMb(long bytes) =>
            (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";

        // ================= 卸载 =================

        /// <summary>
        /// 一个依赖在本机留下的全部痕迹：deps 下该 id 的目录（含所有版本）、临时区里该包的残件、老位置目录。
        /// 清点与删除共用这一份名单，确认浮层上说的和真正删的不会两套。
        /// </summary>
        private static List<string> UninstallPaths(string id)
        {
            var list = new List<string>();
            var p = CurrentManifest()?.Find(id);
            if (p == null) return list;

            string idDir = Path.Combine(DependencyCatalog.RootDir, id);
            if (Directory.Exists(idDir)) list.Add(idDir);

            string zip = Path.Combine(DependencyCatalog.DownloadDir, p.FileName);
            foreach (string t in new[] { zip, zip + ".part" })
                if (File.Exists(t)) list.Add(t);

            list.AddRange(LegacyDirsFor(id).Where(Directory.Exists));
            return list;
        }

        private static long PathBytes(string path) =>
            Directory.Exists(path) ? DirBytes(path, null) : SafeLen(path);

        /// <summary>卸载会带走多少字节（进确认浮层给用户看数目）。</summary>
        public static long UninstallBytes(string id) => UninstallPaths(id).Sum(PathBytes);

        /// <summary>
        /// 「卸载」：删掉这个依赖在本机的全部文件并抹掉就绪登记，这一行回到未下载。
        /// 文件被占用（正在识别/抠图/录制）时不静默跳过，把失败的那几件回给调用方，让他知道为什么没删干净。
        /// </summary>
        public static Task<(long Freed, string Error)> UninstallAsync(string id) => Task.Run(() =>
        {
            // 先把自家进程里握着模型文件的会话放掉，否则下面删的是「正被占用」的 .onnx
            if (id == DepIds.Matting) Matting.MattingService.Instance.UnloadModels();

            long freed = 0;
            var failed = new List<string>();
            foreach (string path in UninstallPaths(id))
            {
                long bytes = PathBytes(path);
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else File.Delete(path);
                    freed += bytes;
                }
                catch (Exception ex)
                {
                    failed.Add(Path.GetFileName(path) + "：" + ex.Message);
                    AppLogger.Warn($"Deps: 卸载 {id} 跳过 {path}：{ex.Message}");
                }
            }

            lock (Sync)
            {
                // 一件都没删掉（全被占用）时留着就绪登记，别让界面说「未下载」而文件明明在位
                var pkg = _manifest?.Find(id);
                if (pkg != null && !DependencyCatalog.IsReady(_manifest!, _lock, pkg))
                    DependencyCatalog.RemoveLock(id);
                _lock = DependencyCatalog.LoadLock();
                RefreshStatesFromLockLocked();
            }
            AppLogger.Info($"Deps: {id} 已卸载，释放 {FormatMb(freed)}");
            if (id == DepIds.Ffmpeg) RefreshFfmpegProbe();
            Changed?.Invoke(id);
            return (freed, failed.Count == 0 ? "" : string.Join("；", failed));
        });

        private static long SafeLen(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        // ================= 离线导入 =================

        /// <summary>
        /// 网络不便时的退路：用户手上有组件包压缩包，按整包 SHA-256 认牌后解到正式位置并登记。
        /// 认不出（哈希对不上清单任何一项）就一个字都不解。返回空串＝成功，否则是给用户看的原因。
        /// </summary>
        public static async Task<string> ImportPackageAsync(string zipPath)
        {
            try
            {
                if (!File.Exists(zipPath)) return "文件不存在或已被移走。";
                await RefreshManifestAsync(force: false).ConfigureAwait(false);
                var m = CurrentManifest();
                if (m == null) return "还没有拿到组件清单，先联网打开这一页再导入。";

                string sha = await Task.Run(() => DependencyCatalog.Sha256File(zipPath)).ConfigureAwait(false);
                var pkg = m.Packages.FirstOrDefault(p =>
                    string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase));
                if (pkg == null) return "这个压缩包不是本程序的组件包（校验值与清单对不上）。";

                var files = pkg.Files;
                string dir = DependencyCatalog.PackageDir(pkg);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(dir);
                    using var zip = ZipFile.OpenRead(zipPath);
                    foreach (var f in files)
                    {
                        var entry = zip.GetEntry(f.ArchiveEntry)
                            ?? throw new DependencyFailedException($"压缩包内缺少 {f.Name}");
                        entry.ExtractToFile(Path.Combine(dir, f.Name), true);
                    }
                }).ConfigureAwait(false);

                if (!await Task.Run(() => DependencyCatalog.VerifyFilesByHash(pkg)).ConfigureAwait(false))
                {
                    try { Directory.Delete(dir, true); } catch { }
                    return "导入后逐文件校验不通过，这个压缩包内容不完整。";
                }

                lock (Sync)
                {
                    DependencyCatalog.WriteLock(pkg.Id, pkg, "import");
                    _lock = DependencyCatalog.LoadLock();
                    RefreshStatesFromLockLocked();
                }
                AppLogger.Info($"Deps: 离线导入 {pkg.Id} {pkg.Version} ← {zipPath}");
                PostReady(pkg.Id);
                Changed?.Invoke(pkg.Id);
                return "";
            }
            catch (DependencyFailedException ex) { return ex.Message; }
            catch (Exception ex)
            {
                AppLogger.Error("Deps: 离线导入异常", ex);
                return "导入失败：" + ex.Message;
            }
        }
    }

    /// <summary>可展示给用户的外因（不弹模态，只进进度窗失败态与日志）。</summary>
    public sealed class DependencyFailedException : Exception
    {
        public DependencyFailedException(string message) : base(message) { }
    }
}
