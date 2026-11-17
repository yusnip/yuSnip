using System.Text;
using System.Text.Json;

namespace ScreenCaptureTool.Core.CloudSync;

public enum CloudSyncState { Disabled, Checking, Synced, PendingUpload, PendingApply, Conflict, Retry, AuthenticationRequired }
public sealed record CloudSyncReport(CloudSyncState State, string Message, DateTimeOffset? LastSuccess = null,
    IReadOnlyList<CloudRevision>? Choices = null, string ConflictToken = "");
public sealed record CloudSyncChoice(string ConflictToken, string? RevisionId);
public sealed record CloudSyncHistory(CloudSnapshot Local, IReadOnlyList<CloudRevision> Revisions,
    IReadOnlyList<string> Heads, string ChoiceToken);

/// <summary>双向同步引擎。基准按目标隔离；时间戳仅供展示，不参与胜负判断。</summary>
public sealed class CloudSyncEngine
{
    private readonly string _directory, _device;
    public CloudSyncEngine(string configurationDirectory)
    {
        _directory = configurationDirectory;
        string devicePath = Path.Combine(configurationDirectory, "cloud-sync", "device-id");
        string deviceId = File.Exists(devicePath) ? File.ReadAllText(devicePath).Trim() : "";
        if (!Guid.TryParseExact(deviceId, "N", out _))
        {
            deviceId = Guid.NewGuid().ToString("N");
            CloudConfigTransaction.AtomicWrite(devicePath, deviceId);
        }
        _device = Environment.MachineName + " · " + deviceId[..8];
    }
    private sealed record Baseline(string LocalHash, string[] Heads, DateTimeOffset Success);
    private string StorePath(ICloudRevisionTransport transport) => Path.Combine(_directory, "cloud-sync", CloudSnapshot.Digest(transport.TargetIdentity));
    /// <summary>只读取当前目标的成功时间；开关、认证状态和历史查询不改变账本。</summary>
    public DateTimeOffset? ReadLastSuccess(string targetIdentity)
        => LoadBaseline(Path.Combine(_directory, "cloud-sync", CloudSnapshot.Digest(targetIdentity)))?.Success;
    private static Baseline? LoadBaseline(string directory)
    {
        string path = Path.Combine(directory, "baseline.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
        return new(root.GetProperty("localHash").GetString()!, root.GetProperty("heads").EnumerateArray().Select(p => p.GetString()!).ToArray(), root.GetProperty("success").GetDateTimeOffset());
    }
    private static void SaveBaseline(string directory, string localHash, IEnumerable<string> heads, DateTimeOffset success)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("localHash", localHash); writer.WriteString("success", success);
            writer.WriteStartArray("heads"); foreach (string head in heads.Order(StringComparer.Ordinal)) writer.WriteStringValue(head); writer.WriteEndArray(); writer.WriteEndObject();
        }
        CloudConfigTransaction.AtomicWrite(Path.Combine(directory, "baseline.json"), Encoding.UTF8.GetString(stream.ToArray()));
    }
    private async Task<List<CloudRevision>> ReadHeadsAsync(ICloudRevisionTransport transport, string store, CloudSnapshot local, HashSet<string> known, CancellationToken ct,
        List<CloudRevision>? history = null, List<CloudRevision>? graph = null)
    {
        var files = await transport.ListAsync(ct);
        var revisions = new Dictionary<string, CloudRevision>(StringComparer.Ordinal);
        var activeCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string path = Path.Combine(store, "revisions", CloudSnapshot.Digest(file.Key) + ".json");
            activeCache.Add(path);
            string json = File.Exists(path) ? File.ReadAllText(path) : await transport.ReadAsync(file, ct);
            CloudRevision revision = CloudRevision.Parse(json);
            if (revision.Id != file.RevisionId) throw new InvalidDataException("修订文件名与内容标识不一致。");
            if (revisions.TryGetValue(revision.Id, out var duplicate))
            {
                if (duplicate.WithoutSnapshot().ToJson() != revision.WithoutSnapshot().ToJson()
                    || (!duplicate.IsPruned && !revision.IsPruned && duplicate.ToJson() != revision.ToJson()))
                    throw new InvalidDataException("同一修订存在不同内容。");
                // 清理中断或双机同时归档会留下重叠副本，正文优先于同身份的精简记录。
                if (duplicate.IsPruned) revisions[revision.Id] = revision;
            }
            else revisions[revision.Id] = revision;
            if (!File.Exists(path)) CloudConfigTransaction.AtomicWrite(path, json);
        }
        if (revisions.Count == 0)
        {
            string? legacy = await transport.ReadLegacyAsync(ct);
            if (legacy == null) return [];
            var snapshot = CloudSnapshot.ReadLegacy(legacy, local);
            // 老信封缺少的段回填本机只用于应用，不能让本机动作变化变成“云端也变化”。
            var identityFallback = new CloudSnapshot([local.Content[0], "", "", ""], true);
            string legacyId = "legacy-" + CloudSnapshot.ReadLegacy(legacy, identityFallback).Hash;
            known.Add(legacyId);
            var old = new CloudRevision(legacyId, [], "旧版手动备份", DateTimeOffset.MinValue, snapshot);
            history?.Add(old);
            return [old];
        }
        // 不接受缺失祖先或循环，避免把损坏目录误判成新目标。
        var counts = new Dictionary<string, int>();
        var children = new Dictionary<string, List<string>>();
        foreach (var revision in revisions.Values)
        {
            known.Add(revision.Id);
            counts[revision.Id] = 0;
            foreach (string parent in revision.Parents)
            {
                if (parent.StartsWith("legacy-", StringComparison.Ordinal)) { known.Add(parent); continue; }
                if (!revisions.ContainsKey(parent)) throw new InvalidDataException("云端修订缺失祖先，请恢复被删除的修订文件。");
                counts[revision.Id]++;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                list.Add(revision.Id);
            }
        }
        var queue = new Queue<string>(counts.Where(p => p.Value == 0).Select(p => p.Key)); int visited = 0;
        while (queue.TryDequeue(out string? id))
        {
            visited++;
            if (children.TryGetValue(id, out var list)) foreach (string child in list) if (--counts[child] == 0) queue.Enqueue(child);
        }
        if (visited != revisions.Count) throw new InvalidDataException("云端修订关系存在循环。");
        history?.AddRange(revisions.Values.Where(r => !r.IsPruned).OrderByDescending(r => r.PublishedAt).ThenBy(r => r.Id, StringComparer.Ordinal));
        graph?.AddRange(revisions.Values);
        var parents = revisions.Values.SelectMany(r => r.Parents).ToHashSet();
        var heads = revisions.Values.Where(r => !parents.Contains(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        if (heads.Any(r => r.IsPruned)) throw new InvalidDataException("云端当前分支缺少配置正文，拒绝同步。");
        // 归档使用新的不可变文件身份，旧缓存不能随每次归档成倍累积。
        string cacheDirectory = Path.Combine(store, "revisions");
        if (Directory.Exists(cacheDirectory))
            foreach (string path in Directory.EnumerateFiles(cacheDirectory, "*.json"))
                if (!activeCache.Contains(path)) File.Delete(path);
        return heads;
    }
    private static string ChoiceToken(ICloudRevisionTransport transport, IEnumerable<CloudRevision> heads, CloudSnapshot local)
        => CloudSnapshot.Digest(transport.TargetIdentity + "\n" + string.Join("\n", heads.Select(r => r.Id).Order(StringComparer.Ordinal)) + "\n" + local.Hash);

    /// <summary>只读查询保留历史，不发布、不应用、不推进同步基准。</summary>
    public async Task<CloudSyncHistory> ReadHistoryAsync(ICloudRevisionTransport transport, Func<CloudSnapshot> capture,
        Func<bool> isCurrent, CancellationToken ct)
    {
        await CloudSnapshot.OperationGate.WaitAsync(ct);
        try
        {
            var local = capture(); var history = new List<CloudRevision>();
            var heads = await ReadHeadsAsync(transport, StorePath(transport), local, new HashSet<string>(), ct, history);
            if (!isCurrent() || capture().Hash != local.Hash) throw new IOException("同步目标或本机配置已变化，请刷新版本列表。");
            return new(local, history, heads.Select(r => r.Id).ToArray(), ChoiceToken(transport, heads, local));
        }
        finally { CloudSnapshot.OperationGate.Release(); }
    }
    public async Task<CloudSyncReport> RunAsync(ICloudRevisionTransport transport, Func<CloudSnapshot> capture,
        Func<CloudSnapshot, string, bool, Task<bool>> apply, Func<bool> isCurrent, CancellationToken ct,
        CloudSyncChoice? choice = null)
    {
        await CloudSnapshot.OperationGate.WaitAsync(ct);
        try
        {
            if (!isCurrent()) return new(CloudSyncState.Disabled, "同步目标已变化。");
            if (transport is ICloudRevisionMaintenance maintenance) await maintenance.PrepareAsync(isCurrent, ct);
            var local = capture(); string store = StorePath(transport); var baseline = LoadBaseline(store);
            var known = new HashSet<string>();
            var history = new List<CloudRevision>();
            var graph = new List<CloudRevision>();
            var heads = await ReadHeadsAsync(transport, store, local, known, ct, history, graph);
            if (!isCurrent()) return new(CloudSyncState.Disabled, "同步目标或范围已变化，等待重新检查。");
            if (capture().Hash != local.Hash) return new(CloudSyncState.PendingUpload, "检查期间本机配置变化，等待同步最新配置。", baseline?.Success);
            string token = ChoiceToken(transport, heads, local);
            bool localChanged = baseline == null || baseline.LocalHash != local.Hash;
            bool remoteChanged = baseline == null || !baseline.Heads.Order().SequenceEqual(heads.Select(r => r.Id).Order());
            bool equal = heads.Count > 0 && heads.All(r => r.Snapshot.ForPolicy(local.IncludeSecrets).Hash == local.Hash);
            bool fork = heads.Select(r => r.Snapshot.ForPolicy(local.IncludeSecrets).Hash).Distinct().Count() > 1;
            bool cloudLost = baseline != null && baseline.Heads.Any(h => !known.Contains(h));
            bool conflict = fork || cloudLost || (!equal && localChanged && remoteChanged && heads.Count > 0);
            if (choice != null && choice.ConflictToken != token)
                return Conflict("配置已再次变化，请查看更新后的版本再选择。");
            CloudSyncReport Conflict(string message)
            {
                var saved = new CloudRevision(Guid.NewGuid().ToString("N"), [], _device, DateTimeOffset.UtcNow, local);
                string localBackup = Path.Combine(store, "conflicts", "local-" + local.Hash + ".json");
                if (!File.Exists(localBackup)) CloudConfigTransaction.AtomicWrite(localBackup, saved.ToJson());
                foreach (var head in heads)
                {
                    string remoteBackup = Path.Combine(store, "conflicts", head.Id + ".json");
                    if (!File.Exists(remoteBackup)) CloudConfigTransaction.AtomicWrite(remoteBackup, head.ToJson());
                }
                return new(CloudSyncState.Conflict, message, baseline?.Success, heads, token);
            }
            if (choice == null && conflict) return Conflict(baseline == null ? "云端已有不同配置，请选择首次同步使用的版本。" : "本机与云端存在不同修改，请选择保留的版本。");
            if (choice?.RevisionId != null)
            {
                var selected = history.FirstOrDefault(r => r.Id == choice.RevisionId);
                if (selected == null) return Conflict("所选云端修订已变化，请重新选择。");
                var selectedSnapshot = selected.Snapshot.ForPolicy(local.IncludeSecrets && selected.Snapshot.IncludeSecrets);
                // 先安全应用，下一步发布包含全部父节点的修订；发布失败基准不推进，仍保留各分支。
                if (!await apply(selectedSnapshot, local.Hash, true)) return Conflict("当前有截图、录屏或动作会话，请结束后再选择应用。");
                if (!isCurrent()) return new(CloudSyncState.Disabled, "同步目标已变化。");
                local = capture();
                await PublishAsync();
                return await FinishAsync(Published("已使用所选云端版本并发布新修订。"));
            }
            if (choice != null || heads.Count == 0 || (localChanged && !remoteChanged))
            {
                if (!isCurrent()) return new(CloudSyncState.Disabled, "同步目标已变化。");
                await PublishAsync();
                return await FinishAsync(Published("本机配置已发布。"));
            }
            if (equal || (!localChanged && !remoteChanged))
            {
                // 不共享密钥时发布已去密钥的当前修订；保留窗口中的旧版本仍可能含密钥。
                if (!local.IncludeSecrets && heads.Any(h => h.Snapshot.IncludeSecrets))
                {
                    await PublishAsync(); return await FinishAsync(Published("当前修订已移除共享密钥。"));
                }
                else if (baseline == null || localChanged || remoteChanged)
                    SaveBaseline(store, local.Hash, heads.Select(h => h.Id), DateTimeOffset.UtcNow);
                return await FinishAsync(new(CloudSyncState.Synced, "配置已同步。", baseline != null && !localChanged && !remoteChanged ? baseline.Success : DateTimeOffset.UtcNow));
            }
            if (remoteChanged && !localChanged)
            {
                var incoming = heads[0].Snapshot.ForPolicy(local.IncludeSecrets && heads[0].Snapshot.IncludeSecrets);
                if (!await apply(incoming, local.Hash, false)) return new(CloudSyncState.PendingApply, "云端更新已暂存，等待关闭设置页并结束当前会话。", baseline?.Success);
                if (!isCurrent()) return new(CloudSyncState.Disabled, "同步目标已变化。");
                SaveBaseline(store, capture().Hash, heads.Select(h => h.Id), DateTimeOffset.UtcNow);
                return await FinishAsync(new(CloudSyncState.Synced, "云端配置已应用。", DateTimeOffset.UtcNow));
            }
            return Conflict("配置与同步基准不一致，请选择保留的版本。");

            async Task PublishAsync()
            {
                var revision = new CloudRevision(Guid.NewGuid().ToString("N"), heads.Select(h => h.Id).ToArray(), _device, DateTimeOffset.UtcNow, local);
                await transport.CreateAsync(revision, ct);
                graph.Add(revision);
                if (isCurrent()) SaveBaseline(store, local.Hash, [revision.Id], DateTimeOffset.UtcNow);
            }
            CloudSyncReport Published(string message)
            {
                if (!isCurrent()) return new(CloudSyncState.Disabled, "同步目标或范围已变化，等待重新检查。");
                return capture().Hash != local.Hash ? new(CloudSyncState.PendingUpload, "本次快照已发布，新的本机修改仍待同步。", DateTimeOffset.UtcNow)
                    : new(CloudSyncState.Synced, message, DateTimeOffset.UtcNow);
            }
            async Task<CloudSyncReport> FinishAsync(CloudSyncReport report)
            {
                if (report.State is CloudSyncState.Synced or CloudSyncState.PendingUpload && isCurrent()
                    && transport is ICloudRevisionMaintenance cleanup)
                    await cleanup.CompactAsync(graph, isCurrent, ct);
                return report;
            }
        }
        finally { CloudSnapshot.OperationGate.Release(); }
    }
}
