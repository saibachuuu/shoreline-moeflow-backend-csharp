using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// Complete project label snapshots and the raw BSON format shared with the
/// Python service. Snapshot chunks are never converted to JSON: legacy field
/// order, numeric widths and ObjectIds survive a round trip byte for byte.
/// </summary>
public sealed partial class LabelSnapshotService(
    MongoStore store,
    TimeProvider clock,
    IdentityPermissions permissions,
    ProjectLabelLock? labelLock = null,
    LabelRequestContext? requestContext = null,
    Func<string, CancellationToken, Task>? operationFault = null)
{
    public const int Version = 1;
    public const int ChunkBytes = 4 * 1024 * 1024;

    public static readonly string[] Kinds = ["files", "targets", "sources", "translations", "tips"];

    private static readonly string[] FileFingerprint = ["_id", "sa", "md", "t", "v"];
    private static readonly string[] TargetFingerprint = ["_id", "l"];

    private readonly ProjectLabelLock labels = labelLock ?? new ProjectLabelLock(store, clock);
    private readonly LabelRequestContext requestScope = requestContext ?? new LabelRequestContext();

    private IMongoCollection<BsonDocument> Snapshots => store.Collection("project_label_snapshot");
    private IMongoCollection<BsonDocument> Chunks => store.Collection("project_label_snapshot_chunk");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> Sources => store.Collection("source");
    private IMongoCollection<BsonDocument> Translations => store.Collection("translation");
    private IMongoCollection<BsonDocument> Tips => store.Collection("tip");
    // FileTargetCache has no MongoEngine collection override; the default
    // CamelCase->snake_case mapping is file_target_cache (same as D1/G2).
    private IMongoCollection<BsonDocument> FileTargetCaches => store.Collection("file_target_cache");

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates the snapshot indexes without touching MongoStore.InitializeAsync.
    /// The integration owner may call this from startup after the label service.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await Snapshots.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument
            {
                ["project"] = 1,
                ["created"] = -1,
                ["_id"] = -1
            }), cancellationToken: ct);
        await Chunks.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { ["snapshot"] = 1, ["index"] = 1 },
            new CreateIndexOptions { Unique = true }), cancellationToken: ct);
    }

    private Task<LabelSession> EnterAsync(ObjectId projectId, bool recovery, long? expectedRevision,
        CancellationToken ct) =>
        labels.EnterAsync(requestScope, projectId, recovery, expectedRevision, ct);

    // ------------------------------------------------------------------
    // Canonical label data and raw BSON digest
    // ------------------------------------------------------------------

    public sealed class LabelData
    {
        private readonly Dictionary<string, List<BsonDocument>> rows;

        internal LabelData(Dictionary<string, List<BsonDocument>> rows)
        {
            this.rows = rows;
        }

        public IReadOnlyList<BsonDocument> this[string kind] => rows[kind];

        public IReadOnlyDictionary<string, int> Counts =>
            Kinds.ToDictionary(kind => kind, kind => rows[kind].Count, StringComparer.Ordinal);

        internal Dictionary<string, List<BsonDocument>> Rows => rows;
    }

    /// <summary>
    /// Mirrors label_snapshot.label_data. Files and targets keep only their
    /// restore fingerprint; source/translation/tip rows are raw BSON with the
    /// bookkeeping label_epoch removed.
    /// </summary>
    public async Task<LabelData> LoadLabelDataAsync(ObjectId projectId, CancellationToken ct = default)
    {
        var fileRows = await ReadAllAsync(Files, new BsonDocument("p", projectId), "files", ct);
        var files = fileRows.Select(row => Fingerprint(row, FileFingerprint)).ToList();
        var fileIds = files.Select(IdOf).ToList();

        var targetRows = await ReadAllAsync(Targets, new BsonDocument("t", projectId), "targets", ct);
        var targets = targetRows.Select(row => Fingerprint(row, TargetFingerprint)).ToList();

        // $in is split into MutationBatch-sized queries, then the combined rows
        // are sorted by _id as one set. Sorting each batch independently and
        // concatenating would change the raw BSON digest order across batches.
        var sourceRows = fileIds.Count == 0
            ? new List<BsonDocument>()
            : await ReadAllInAsync(Sources, "f", fileIds, ct);
        var sources = sourceRows.Select(RemoveEpoch).ToList();
        var sourceIds = sources.Select(IdOf).ToList();

        var translationRows = sourceIds.Count == 0
            ? new List<BsonDocument>()
            : await ReadAllInAsync(Translations, "o", sourceIds, ct);
        var translations = translationRows.Select(RemoveEpoch).ToList();

        var tipRows = sourceIds.Count == 0
            ? new List<BsonDocument>()
            : await ReadAllInAsync(Tips, "o", sourceIds, ct);
        var tips = tipRows.Select(RemoveEpoch).ToList();

        return new LabelData(new Dictionary<string, List<BsonDocument>>(StringComparer.Ordinal)
        {
            ["files"] = files,
            ["targets"] = targets,
            ["sources"] = sources,
            ["translations"] = translations,
            ["tips"] = tips
        });
    }

    public static string DataDigest(LabelData data) =>
        DataDigestOf(data.Rows.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<BsonDocument>)pair.Value, StringComparer.Ordinal));

    /// <summary>
    /// Digest helper shared with the compatibility fixture tests. Kinds are
    /// always hashed in the canonical order, including when a kind is empty.
    /// </summary>
    public static string DataDigestOf(IReadOnlyDictionary<string, IReadOnlyList<BsonDocument>> rows)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var kind in Kinds)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(kind));
            if (!rows.TryGetValue(kind, out var kindRows)) continue;
            foreach (var row in kindRows) sha.AppendData(row.ToBson());
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static BsonDocument Fingerprint(BsonDocument row, IReadOnlyList<string> fields)
    {
        var result = new BsonDocument();
        foreach (var field in fields)
            result[field] = row.TryGetValue(field, out var value) ? value : BsonNull.Value;
        return result;
    }

    private static BsonDocument RemoveEpoch(BsonDocument row)
    {
        var copy = (BsonDocument)row.DeepClone();
        copy.Remove("label_epoch");
        return copy;
    }

    private static ObjectId IdOf(BsonDocument row) =>
        row.TryGetValue("_id", out var value) && value.IsObjectId
            ? value.AsObjectId
            : throw new InvalidOperationException("label snapshot row does not have an ObjectId _id");

    // ------------------------------------------------------------------
    // Metadata, chunk storage and bounded validation
    // ------------------------------------------------------------------

    public sealed record SnapshotItem(
        string Id,
        string Name,
        string Created,
        string ActorName,
        string Reason,
        Dictionary<string, int> Counts)
    {
        public Dictionary<string, object?> ToApi() => new()
        {
            ["id"] = Id,
            ["name"] = Name,
            ["created"] = Created,
            ["actor_name"] = ActorName,
            ["reason"] = Reason,
            ["counts"] = Counts
        };
    }

    public sealed record SnapshotPage(IReadOnlyList<SnapshotItem> Items, long Total);

    public async Task<SnapshotPage> ListSnapshotsAsync(
        BsonDocument actor,
        ObjectId projectId,
        int page,
        int limit,
        CancellationToken ct = default)
    {
        var project = await RequireProjectAsync(projectId, ct);
        await RequireCapabilityAsync(actor, project, "create", ct);

        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);
        var filter = new BsonDocument { ["project"] = projectId, ["ready"] = true };
        var total = await Snapshots.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await Snapshots.Find(filter)
            .Sort(new BsonDocument { ["created"] = -1, ["_id"] = -1 })
            .Skip((page - 1) * limit)
            .Limit(limit)
            .ToListAsync(ct);
        return new SnapshotPage(rows.Select(row => new SnapshotItem(
            row["_id"].AsObjectId.ToString(),
            row.Text("name"),
            IsoTimestamp(row, "created"),
            row.Text("actor_name"),
            row.Text("reason"),
            row.GetValue("counts", new BsonDocument()).AsBsonDocument
                .ToDictionary(element => element.Name, element => element.Value.ToInt32(),
                    StringComparer.Ordinal))).ToList(), total);
    }

    public async Task<ObjectId> CreateSnapshotAsync(
        BsonDocument actor,
        ObjectId projectId,
        string name = "",
        string reason = "manual",
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        await using var session = await EnterAsync(projectId, recovery: false, expectedRevision, ct);
        await RequireCapabilityAsync(actor, session.Project, "create", ct);
        return await CreateSnapshotCoreAsync(session, actor, name, reason, ct);
    }

    internal async Task<ObjectId> CreateSnapshotCoreAsync(
        LabelSession session,
        BsonDocument actor,
        string name,
        string reason,
        CancellationToken ct)
    {
        if (ProjectLabelLock.HasRecovery(session.Project))
            throw OperationError("项目需要先恢复未完成的操作");

        var data = await LoadLabelDataAsync(session.ProjectId, ct);
        var digest = DataDigest(data);
        var snapshotId = ObjectId.GenerateNewId();
        var now = Now();
        var metadata = new BsonDocument
        {
            ["_id"] = snapshotId,
            ["project"] = session.ProjectId,
            ["name"] = NormalizeSnapshotName(name, now),
            ["actor"] = actor.GetValue("_id", BsonNull.Value),
            ["actor_name"] = actor.Text("n"),
            ["created"] = now,
            ["version"] = Version,
            ["reason"] = reason,
            ["ready"] = false,
            ["digest"] = digest,
            ["counts"] = new BsonDocument(data.Counts.Select(pair =>
                new BsonElement(pair.Key, pair.Value))),
            ["revision"] = session.Revision
        };

        await Snapshots.InsertOneAsync(metadata, cancellationToken: ct);
        try
        {
            var index = 0;
            foreach (var kind in Kinds)
            {
                var batch = new List<BsonDocument>();
                var size = 0;
                foreach (var row in data[kind])
                {
                    var rowBytes = row.ToBson();
                    if (rowBytes.Length > ChunkBytes)
                        throw OperationError("单个标签数据超过快照分块大小限制");
                    if (batch.Count > 0 && size + rowBytes.Length > ChunkBytes)
                    {
                        await InsertChunkAsync(snapshotId, index++, kind, batch, ct);
                        batch = [];
                        size = 0;
                    }
                    batch.Add(row);
                    size += rowBytes.Length;
                }
                if (batch.Count > 0) await InsertChunkAsync(snapshotId, index++, kind, batch, ct);
            }

            await Snapshots.UpdateOneAsync(new BsonDocument("_id", snapshotId),
                new BsonDocument("$set", new BsonDocument { ["ready"] = true, ["chunks"] = index }),
                cancellationToken: ct);
            return snapshotId;
        }
        catch
        {
            await Chunks.DeleteManyAsync(new BsonDocument("snapshot", snapshotId), CancellationToken.None);
            await Snapshots.DeleteOneAsync(new BsonDocument("_id", snapshotId), CancellationToken.None);
            throw;
        }
    }

    private async Task InsertChunkAsync(ObjectId snapshotId, int index, string kind,
        IReadOnlyList<BsonDocument> rows, CancellationToken ct)
    {
        var encoded = new BsonDocument
        {
            ["rows"] = new BsonArray(rows.Select(row => (BsonValue)row))
        }.ToBson();
        var chunk = new BsonDocument
        {
            ["snapshot"] = snapshotId,
            ["index"] = index,
            ["kind"] = kind,
            ["data"] = new BsonBinaryData(encoded, BsonBinarySubType.Binary),
            ["hash"] = Sha256(encoded)
        };
        await Chunks.InsertOneAsync(chunk, cancellationToken: ct);
    }

    /// <summary>
    /// Loads and fully validates a snapshot before any live label row can be
    /// touched. Ownership, version, ready state, chunk order/hash and the
    /// global digest are all checked here.
    /// </summary>
    public async Task<LabelData> LoadSnapshotAsync(ObjectId projectId, string snapshotId,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(snapshotId, out var id)) throw OperationError("快照编号无效");

        var metadata = await Snapshots.Find(new BsonDocument
        {
            ["_id"] = id,
            ["project"] = projectId,
            ["ready"] = true
        }).FirstOrDefaultAsync(ct);
        if (metadata is null || metadata.Integer("version", 0) != Version)
            throw OperationError("快照不存在或版本不兼容");

        var parts = await Chunks.Find(new BsonDocument("snapshot", id))
            .Sort(new BsonDocument("index", 1))
            .ToListAsync(ct);
        var expectedChunks = metadata.Integer("chunks", 0);
        if (parts.Count != expectedChunks) throw OperationError("快照数据不完整");

        var data = new Dictionary<string, List<BsonDocument>>(StringComparer.Ordinal);
        foreach (var kind in Kinds) data[kind] = [];
        var kindIndex = 0;
        for (var position = 0; position < parts.Count; position++)
        {
            var part = parts[position];
            if (part.Integer("index", -1) != position)
                throw OperationError("快照完整性校验失败");

            var kind = part.Text("kind");
            var expectedKind = Array.IndexOf(Kinds, kind);
            if (expectedKind < 0 || expectedKind < kindIndex)
                throw OperationError("快照完整性校验失败");
            kindIndex = expectedKind;

            byte[] bytes;
            try
            {
                bytes = part["data"].AsBsonBinaryData.Bytes;
            }
            catch (Exception)
            {
                throw OperationError("快照完整性校验失败");
            }
            if (!string.Equals(part.Text("hash"), Sha256(bytes), StringComparison.Ordinal))
                throw OperationError("快照完整性校验失败");

            BsonDocument decoded;
            try
            {
                decoded = BsonSerializer.Deserialize<BsonDocument>(bytes);
            }
            catch (Exception)
            {
                throw OperationError("快照完整性校验失败");
            }
            var rows = decoded.GetValue("rows", new BsonArray()).AsBsonArray;
            foreach (var value in rows)
            {
                if (!value.IsBsonDocument) throw OperationError("快照完整性校验失败");
                data[kind].Add(value.AsBsonDocument);
            }
        }

        var loaded = new LabelData(data);
        var storedCounts = metadata.GetValue("counts", new BsonDocument()).AsBsonDocument;
        foreach (var kind in Kinds)
        {
            if (loaded[kind].Count != storedCounts.Integer(kind, 0))
                throw OperationError("快照数据不完整");
        }
        if (!string.Equals(DataDigest(loaded), metadata.Text("digest"), StringComparison.Ordinal))
            throw OperationError("快照完整性校验失败");
        return loaded;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ------------------------------------------------------------------
    // Restore fingerprint validation
    // ------------------------------------------------------------------

    private async Task ValidateRestoreAsync(ObjectId projectId, LabelData snapshot,
        CancellationToken ct)
    {
        var current = await LoadLabelDataAsync(projectId, ct);
        var currentFiles = current["files"].ToDictionary(IdOf);
        foreach (var row in snapshot["files"])
        {
            var id = IdOf(row);
            if (!currentFiles.TryGetValue(id, out var existing) || !FingerprintEquals(row, existing))
                throw OperationError("快照关联的文件已删除或被替换，不能完整恢复");
        }

        var currentTargets = current["targets"].ToDictionary(IdOf);
        foreach (var row in snapshot["targets"])
        {
            var id = IdOf(row);
            if (!currentTargets.TryGetValue(id, out var existing) || !FingerprintEquals(row, existing))
                throw OperationError("快照关联的目标语言已删除或改变");
        }
    }

    private static bool FingerprintEquals(BsonDocument left, BsonDocument right)
    {
        if (left.ElementCount != right.ElementCount) return false;
        foreach (var element in left)
        {
            if (!right.TryGetValue(element.Name, out var value) || !value.Equals(element.Value)) return false;
        }
        return true;
    }

    private static string NormalizeSnapshotName(string name, DateTime now)
    {
        var trimmed = name.Trim();
        var limited = LimitRunes(trimmed, 120);
        return limited.Length > 0
            ? limited
            : "快照 " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
    }

    public static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static string LimitRunes(string value, int count)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used++ >= count) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static string IsoTimestamp(BsonDocument row, string field)
    {
        if (!row.TryGetValue(field, out var value) || !value.IsBsonDateTime) return "";
        var time = value.ToUniversalTime();
        var baseValue = time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return time.Microsecond == 0
            ? baseValue
            : baseValue + "." + time.Microsecond.ToString("D6", CultureInfo.InvariantCulture);
    }

    internal static ApiError OperationError(string message) =>
        new("LabelOperationError", 1, message, 409);

    private static ApiError TargetNotExist() =>
        new("TargetNotExistError", 4024, "项目目标语言不存在");

    private static ApiError ProjectNotExist() =>
        new("ProjectNotExistError", 4001, "项目不存在");

    private async Task<BsonDocument> RequireProjectAsync(ObjectId projectId, CancellationToken ct) =>
        await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
        ?? throw ProjectNotExist();

    private async Task FireAsync(string phase, CancellationToken ct)
    {
        if (operationFault is not null) await operationFault(phase, ct);
    }

    private static async Task<List<BsonDocument>> ReadAllAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument filter,
        string sortField,
        CancellationToken ct)
    {
        using var cursor = await collection.FindAsync(filter,
            new FindOptions<BsonDocument> { BatchSize = 1000 }, ct);
        var result = new List<BsonDocument>();
        while (await cursor.MoveNextAsync(ct)) result.AddRange(cursor.Current);
        result.Sort((left, right) => CompareIds(IdOf(left), IdOf(right)));
        return result;
    }

    private static async Task<List<BsonDocument>> ReadAllInAsync(
        IMongoCollection<BsonDocument> collection,
        string field,
        IReadOnlyList<ObjectId> ids,
        CancellationToken ct)
    {
        var result = new List<BsonDocument>();
        for (var offset = 0; offset < ids.Count; offset += MutationBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(offset).Take(MutationBatch).ToList();
            if (batch.Count == 0) continue;
            result.AddRange(await ReadAllAsync(collection,
                new BsonDocument(field, new BsonDocument("$in", new BsonArray(batch))), field, ct));
        }
        // Global sort after all batches, not a per-batch order concatenation.
        result.Sort((left, right) => CompareIds(IdOf(left), IdOf(right)));
        return result;
    }

    private static async Task<List<ObjectId>> FindIdsInAsync(
        IMongoCollection<BsonDocument> collection,
        string field,
        IReadOnlyList<ObjectId> ids,
        CancellationToken ct)
    {
        var result = new List<ObjectId>();
        var options = new FindOptions<BsonDocument>
        {
            BatchSize = MutationBatch,
            Projection = new BsonDocument("_id", 1)
        };
        for (var offset = 0; offset < ids.Count; offset += MutationBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(offset).Take(MutationBatch).ToList();
            if (batch.Count == 0) continue;
            using var cursor = await collection.FindAsync(
                new BsonDocument(field, new BsonDocument("$in", new BsonArray(batch))), options, ct);
            while (await cursor.MoveNextAsync(ct)) result.AddRange(cursor.Current.Select(IdOf));
        }
        result.Sort(CompareIds);
        return result;
    }

    private static int CompareIds(ObjectId left, ObjectId right) =>
        left.CompareTo(right);
}
