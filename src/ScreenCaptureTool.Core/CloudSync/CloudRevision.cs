using System.Text;
using System.Text.Json;

namespace ScreenCaptureTool.Core.CloudSync;

/// <summary>唯一命名且不覆盖的修订；多个父修订表示用户明确解决分叉。</summary>
public sealed record CloudRevision(string Id, string[] Parents, string Device, DateTimeOffset PublishedAt, CloudSnapshot Snapshot, string? PrunedHash = null)
{
    public const int HistoryLimit = 30;
    // 清理只去掉配置正文，保留祖先身份和关系，离线设备仍能区分正常清理与意外删除。
    public bool IsPruned => PrunedHash != null;
    public CloudRevision WithoutSnapshot() => this with { Snapshot = PrunedPlaceholder, PrunedHash = PrunedHash ?? Snapshot.Hash };
    private static readonly CloudSnapshot PrunedPlaceholder = new(["ThemeMode=System\n", "", "", ""], false);
    public static bool ValidId(string id) => Guid.TryParseExact(id, "N", out _) || (id.StartsWith("legacy-", StringComparison.Ordinal) && id.Length == 71 && id[7..].All(Uri.IsHexDigit));
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("app", "yuSnip"); writer.WriteNumber("protocol", IsPruned ? 2 : 1);
            writer.WriteString("id", Id); writer.WriteString("device", Device); writer.WriteString("publishedAt", PublishedAt);
            writer.WriteStartArray("parents"); foreach (string parent in Parents.Order(StringComparer.Ordinal)) writer.WriteStringValue(parent); writer.WriteEndArray();
            writer.WriteString("hash", PrunedHash ?? Snapshot.Hash);
            if (IsPruned) writer.WriteBoolean("pruned", true);
            else { writer.WritePropertyName("snapshot"); Snapshot.Write(writer); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static CloudRevision Parse(string json)
    {
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("同步修订超过 16 MB 限制。");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int protocol = root.GetProperty("protocol").GetInt32();
        if (root.GetProperty("app").GetString() != "yuSnip" || protocol is not (1 or 2)) throw new InvalidDataException("同步协议不受支持，请更新 yuSnip。");
        string id = root.GetProperty("id").GetString() ?? "";
        string[] parents = root.GetProperty("parents").EnumerateArray().Select(p => p.GetString() ?? "").ToArray();
        if (!Guid.TryParseExact(id, "N", out _) || parents.Any(p => !ValidId(p) || p == id) || parents.Distinct().Count() != parents.Length)
            throw new InvalidDataException("同步修订或父修订标识无效。");
        string hash = root.GetProperty("hash").GetString() ?? "";
        bool pruned = protocol == 2 && root.GetProperty("pruned").GetBoolean();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || (protocol == 2 && !pruned)) throw new InvalidDataException("同步内容摘要无效。");
        var snapshot = pruned ? PrunedPlaceholder : CloudSnapshot.Read(root.GetProperty("snapshot"));
        if (!pruned && snapshot.Hash != hash) throw new InvalidDataException("同步内容摘要校验失败。");
        return new(id, parents, root.GetProperty("device").GetString() ?? "未知设备", root.GetProperty("publishedAt").GetDateTimeOffset(), snapshot, pruned ? hash : null);
    }
}

public sealed record CloudRemoteFile(string Key, string RevisionId, bool IsArchive = false);
public interface ICloudRevisionTransport
{
    string TargetIdentity { get; }
    Task<IReadOnlyList<CloudRemoteFile>> ListAsync(CancellationToken cancellationToken);
    Task<string> ReadAsync(CloudRemoteFile file, CancellationToken cancellationToken);
    Task CreateAsync(CloudRevision revision, CancellationToken cancellationToken);
    Task<string?> ReadLegacyAsync(CancellationToken cancellationToken);
}
public interface ICloudRevisionMaintenance
{
    Task PrepareAsync(Func<bool> isCurrent, CancellationToken cancellationToken);
    Task CompactAsync(IReadOnlyList<CloudRevision> revisions, Func<bool> isCurrent, CancellationToken cancellationToken);
}
public sealed class CloudTransportException(string message, bool authFailed = false, int? statusCode = null, string? reason = null) : IOException(message)
{
    public bool AuthFailed { get; } = authFailed;
    public int? StatusCode { get; } = statusCode;
    public string? Reason { get; } = reason;
}
public sealed class CloudRequestTimeoutException(string operation, TimeSpan timeout, Exception inner)
    : TimeoutException($"{operation}请求超时（{timeout.TotalSeconds:0.###} 秒）。", inner);

/// <summary>四份文件可恢复提交；崩溃后回滚，不将部分应用视作成功。</summary>
public static class CloudConfigTransaction
{
    private static string TransactionPath(string directory) => Path.Combine(directory, "cloud-sync", "apply-transaction");
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".new";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        ReplaceFile(temporary, path);
    }
    public static void ReplaceFile(string temporary, string target)
    {
        // Windows 文件监控/杀毒软件可短暂持有替换目标；有界重试不降级成直接覆盖写入。
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                return;
            }
            catch (Exception ex) when (attempt < 6 && (ex is IOException or UnauthorizedAccessException)
                && (ex.HResult & 0xFFFF) is 5 or 32 or 33 or 1175)
            { Thread.Sleep(10 * (attempt + 1)); }
        }
    }
    public static void Recover(string directory)
    {
        lock (CloudSnapshot.FileGate)
        {
            string transaction = TransactionPath(directory);
            if (!File.Exists(Path.Combine(transaction, "pending"))) return;
            for (int i = 0; i < 4; i++)
            {
                string target = Path.Combine(directory, CloudSnapshot.Files[i]);
                string before = Path.Combine(transaction, i + ".before");
                if (File.Exists(before)) File.Copy(before, target, true);
                else if (File.Exists(target)) File.Delete(target);
            }
            File.Delete(Path.Combine(transaction, "pending"));
        }
    }
    public static void Apply(string directory, CloudSnapshot snapshot, string expectedHash, Action<int>? afterReplace = null)
    {
        lock (CloudSnapshot.FileGate)
        {
            if (CloudSnapshot.Capture(directory, snapshot.IncludeSecrets).Hash != expectedHash) throw new IOException("本机配置已经变化，请重新同步。");
            Recover(directory);
            string[] content = snapshot.MergeLocal(directory);
            string transaction = TransactionPath(directory);
            Directory.CreateDirectory(transaction);
            for (int i = 0; i < 4; i++)
            {
                string source = Path.Combine(directory, CloudSnapshot.Files[i]);
                string before = Path.Combine(transaction, i + ".before");
                if (File.Exists(source)) File.Copy(source, before, true);
                else if (File.Exists(before)) File.Delete(before);
                AtomicWrite(Path.Combine(transaction, i + ".after"), content[i]);
            }
            AtomicWrite(Path.Combine(transaction, "pending"), "rollback");
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    File.Copy(Path.Combine(transaction, i + ".after"), Path.Combine(directory, CloudSnapshot.Files[i]) + ".sync", true);
                    ReplaceFile(Path.Combine(directory, CloudSnapshot.Files[i]) + ".sync", Path.Combine(directory, CloudSnapshot.Files[i]));
                    afterReplace?.Invoke(i);
                }
                File.Delete(Path.Combine(transaction, "pending"));
            }
            catch { Recover(directory); throw; }
        }
    }
}
