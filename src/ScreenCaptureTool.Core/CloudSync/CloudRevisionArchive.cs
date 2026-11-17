using System.Text;
using System.Text.Json;

namespace ScreenCaptureTool.Core.CloudSync;

/// <summary>不可变归档：先回读确认全部内容，再删除已收录的源文件；中断和并发只留下可合并的副本。</summary>
internal sealed class CloudRevisionArchive
{
    internal const int HistoryLimit = CloudRevision.HistoryLimit;
    // 普通修订由 Guid.NewGuid 生成 UUIDv4；UUIDv8 标记归档，旧客户端仍会列出它并因协议版本停止。
    internal static bool IsArchiveId(string id) => Guid.TryParseExact(id, "N", out _) && id[12] == '8';
    private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);
    private readonly List<CloudRemoteFile> _sources = [];
    private readonly HashSet<string> _covered = new(StringComparer.Ordinal);

    internal async Task<IReadOnlyList<CloudRemoteFile>> ExpandAsync(IReadOnlyList<CloudRemoteFile> sources,
        Func<CloudRemoteFile, CancellationToken, Task<string>> read, CancellationToken ct)
    {
        _contents.Clear(); _sources.Clear(); _covered.Clear();
        var files = new List<CloudRemoteFile>();
        foreach (var source in sources)
        {
            if (!source.IsArchive) { files.Add(source); _covered.Add(source.RevisionId); }
            else
            {
                foreach (var revision in Parse(await read(source, ct), source.RevisionId))
                {
                    string key = source.Key + "#" + revision.Id;
                    _contents.Add(key, revision.ToJson());
                    _covered.Add(revision.Id);
                    files.Add(new(key, revision.Id));
                }
            }
            _sources.Add(source);
        }
        return files;
    }

    internal bool TryRead(CloudRemoteFile file, out string json) => _contents.TryGetValue(file.Key, out json!);
    internal void Created(CloudRemoteFile file) { _sources.Add(file); _covered.Add(file.RevisionId); }

    private static CloudRevision[] Parse(string json, string id)
    {
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidDataException("同步归档超过 16 MB 限制。");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("app").GetString() != "yuSnip" || root.GetProperty("protocol").GetInt32() != 2
            || root.GetProperty("archive").GetInt32() != 1 || root.GetProperty("id").GetString() != id)
            throw new InvalidDataException("同步归档标识或格式无效。");
        var revisions = root.GetProperty("revisions").EnumerateArray().Select(r => CloudRevision.Parse(r.GetRawText())).ToArray();
        if (revisions.Length == 0 || revisions.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != revisions.Length)
            throw new InvalidDataException("同步归档为空或有重复修订。");
        return revisions;
    }

    internal async Task CompactAsync(IReadOnlyList<CloudRevision> revisions, Func<bool> isCurrent,
        Func<string, string, CancellationToken, Task<CloudRemoteFile>> create,
        Func<CloudRemoteFile, CancellationToken, Task<string>> read,
        Func<CloudRemoteFile, CancellationToken, Task> delete, CancellationToken ct)
    {
        var parents = revisions.SelectMany(r => r.Parents).ToHashSet(StringComparer.Ordinal);
        var heads = revisions.Where(r => !parents.Contains(r.Id)).ToArray();
        // 尚未解决的分叉不清理，任何一端的原文都保留。
        if (heads.Length != 1 || heads[0].IsPruned || !isCurrent()) return;
        int fullCount = revisions.Count(r => !r.IsPruned);
        bool hasArchive = _sources.Any(f => f.IsArchive);
        if (!hasArchive && fullCount <= HistoryLimit) return;
        if (hasArchive && _sources.Count == 1 && fullCount <= HistoryLimit) return;
        if (!_covered.SetEquals(revisions.Select(r => r.Id)))
            throw new InvalidDataException("归档输入未完整覆盖已列举的源修订，未清理任何文件。");
        var keep = revisions.Where(r => !r.IsPruned && r.Id != heads[0].Id)
            .OrderByDescending(r => r.PublishedAt).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Take(HistoryLimit - 1).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        keep.Add(heads[0].Id);
        var retained = revisions.OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => keep.Contains(r.Id) ? r : r.WithoutSnapshot()).ToArray();
        string randomId = Guid.NewGuid().ToString("N");
        string archiveId = randomId[..12] + "8" + randomId[13..];
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("app", "yuSnip"); writer.WriteNumber("protocol", 2);
            writer.WriteNumber("archive", 1); writer.WriteString("id", archiveId); writer.WriteStartArray("revisions");
            foreach (var revision in retained) writer.WriteRawValue(revision.ToJson());
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        if (stream.Length > 16 * 1024 * 1024) throw new IOException("同步历史归档超过 16 MB，未清理任何源文件。");
        string json = Encoding.UTF8.GetString(stream.ToArray());
        var sources = _sources.ToArray();
        if (!isCurrent()) return;
        var archive = await create(archiveId, json, ct);
        string readback = await read(archive, ct);
        // 验证字节及协议，不能仅凭创建成功或文件数量开始删除。
        if (readback != json || Parse(readback, archiveId).Length != retained.Length)
            throw new IOException("同步归档回读校验失败，未清理任何源文件。");
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return;
            await delete(source, ct);
        }
        _sources.Clear(); _sources.Add(archive);
    }
}
