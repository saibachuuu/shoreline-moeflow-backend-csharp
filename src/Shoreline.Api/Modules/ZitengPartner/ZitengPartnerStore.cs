using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// Persistence for the three module-owned collections. The BSON document shape
/// follows the Python models, with two private fields added to
/// <c>ziteng_check</c> for safe async execution: <c>generation</c> (incremented by
/// every explicit retry) and <c>lease_owner</c>/<c>lease_until</c> (a recoverable
/// worker lease). None of those private fields is exposed through the HTTP API.
/// </summary>
public sealed class ZitengPartnerStore(MongoStore store, TimeProvider clock)
{
    public const string WorkCollection = "ziteng_work";
    public const string QueryCollection = "ziteng_query";
    public const string CheckCollection = "ziteng_check";

    public const int StatusQueued = 0;
    public const int StatusRunning = 1;
    public const int StatusSucceeded = 2;
    public const int StatusFailed = 3;

    public const string VerdictClear = "clear";
    public const string VerdictSuspected = "suspected";
    public const string VerdictFailed = "failed";

    public sealed record CheckSnapshot(
        BsonDocument Document,
        string ProjectId,
        string Keyword,
        int Status,
        string Verdict,
        IReadOnlyList<BsonDocument> Suspects,
        int SuspectCount,
        string Error,
        DateTime UpdatedAt,
        long Generation)
    {
        public bool IsRunning => Status == StatusRunning;

        public Dictionary<string, object?> ToApi()
        {
            var suspects = Suspects.Select(ToPlain).ToList();
            return new Dictionary<string, object?>
            {
                ["project_id"] = ProjectId,
                ["keyword"] = Keyword,
                ["status"] = Status,
                ["verdict"] = Verdict,
                ["suspects"] = suspects,
                ["suspect_count"] = SuspectCount,
                ["error"] = Error,
                ["updated_at"] = Timestamp(UpdatedAt),
            };
        }

        private static string Timestamp(DateTime value) => value.Kind == DateTimeKind.Utc
            ? value.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff",
                System.Globalization.CultureInfo.InvariantCulture);

        private static Dictionary<string, object?> ToPlain(BsonDocument document)
        {
            var plain = new Dictionary<string, object?>();
            foreach (var element in document)
                plain[element.Name] = Plain(element.Value);
            return plain;
        }

        private static object? Plain(BsonValue value) => value switch
        {
            BsonString text => text.AsString,
            BsonBoolean boolean => boolean.AsBoolean,
            BsonInt32 number => number.AsInt32,
            BsonInt64 number => number.AsInt64,
            BsonDouble number => number.AsDouble,
            BsonNull => null,
            BsonArray array => array.Select(Plain).ToList(),
            BsonDocument document => ToPlain(document),
            _ => value.ToString(),
        };
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // Early C# builds used module-specific index names. Python's
        // MongoEngine creates the default `<field>_1` names, and MongoDB
        // refuses the same key under a second name (IndexOptionsConflict),
        // which made a rollback to Python log index failures. Drop the old
        // names before creating the Python-compatible ones.
        await DropIndexIfExistsAsync(WorkCollection, "ziteng_work_id_v1", ct).ConfigureAwait(false);
        await DropIndexIfExistsAsync(WorkCollection, "ziteng_work_state_v1", ct).ConfigureAwait(false);
        await DropIndexIfExistsAsync(QueryCollection, "ziteng_query_keyword_v1", ct).ConfigureAwait(false);
        await DropIndexIfExistsAsync(CheckCollection, "ziteng_check_project_v1", ct).ConfigureAwait(false);
        await DropIndexIfExistsAsync(CheckCollection, "ziteng_check_status_v1", ct).ConfigureAwait(false);

        await store.Collection(WorkCollection).Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("work_id"),
                new CreateIndexOptions { Unique = true, Name = "work_id_1" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("state"),
                new CreateIndexOptions { Name = "state_1" }),
        ], ct);
        // Deliberately no TTL index: freshness is logical (queried_at + cache
        // TTL) and cached works must never be deleted underneath a query.
        await store.Collection(QueryCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("keyword"),
                new CreateIndexOptions { Unique = true, Name = "keyword_1" }), cancellationToken: ct);
        await store.Collection(CheckCollection).Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("project_id"),
                new CreateIndexOptions { Unique = true, Name = "project_id_1" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("status"),
                new CreateIndexOptions { Name = "status_1" }),
        ], ct);
    }

    public async Task<CheckSnapshot?> GetCheckAsync(string projectId, CancellationToken ct = default)
    {
        var document = await store.Collection(CheckCollection)
            .Find(new BsonDocument("project_id", projectId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return document is null ? null : Snapshot(document);
    }

    /// <summary>Get or create the project's check; update the keyword after a rename.</summary>
    public async Task<CheckSnapshot> EnsureCheckAsync(string projectId, string projectName,
        CancellationToken ct = default)
    {
        var keyword = ZitengPartnerTitle.Extract(projectName);
        var collection = store.Collection(CheckCollection);
        var existing = await collection.Find(new BsonDocument("project_id", projectId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Text("keyword") != keyword)
            {
                var now = Now();
                await collection.UpdateOneAsync(
                    new BsonDocument("_id", existing["_id"]),
                    new BsonDocument("$set", new BsonDocument
                    {
                        ["keyword"] = keyword,
                        ["updated_at"] = now,
                    }), cancellationToken: ct).ConfigureAwait(false);
                existing["keyword"] = keyword;
                existing["updated_at"] = now;
            }
            return Snapshot(existing);
        }

        var now2 = Now();
        var created = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project_id"] = projectId,
            ["keyword"] = keyword,
            ["status"] = StatusQueued,
            ["verdict"] = "",
            ["suspects"] = new BsonArray(),
            ["suspect_count"] = 0,
            ["error"] = "",
            ["created_at"] = now2,
            ["updated_at"] = now2,
            ["generation"] = 0L,
            ["lease_owner"] = "",
            ["lease_until"] = BsonNull.Value,
        };
        try
        {
            await collection.InsertOneAsync(created, cancellationToken: ct).ConfigureAwait(false);
            return Snapshot(created);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var raced = await collection.Find(new BsonDocument("project_id", projectId))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (raced is null) throw;
            return Snapshot(raced);
        }
    }

    /// <summary>
    /// Reset a finished/running check to queued and advance the generation in one
    /// atomic write. A job carrying an older generation can no longer claim or
    /// complete this check. The result payload is intentionally kept, matching
    /// the Python reset which only clears status/verdict/error.
    /// </summary>
    public async Task<long?> ResetForRetryAsync(string projectId, CancellationToken ct = default)
    {
        var updated = await store.Collection(CheckCollection).FindOneAndUpdateAsync(
            new BsonDocument("project_id", projectId),
            new BsonDocument
            {
                ["$inc"] = new BsonDocument("generation", 1L),
                ["$set"] = new BsonDocument
                {
                    ["status"] = StatusQueued,
                    ["verdict"] = "",
                    ["error"] = "",
                    ["updated_at"] = Now(),
                    ["lease_owner"] = "",
                    ["lease_until"] = BsonNull.Value,
                },
            },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct).ConfigureAwait(false);
        return updated is null ? null : updated.Integer("generation");
    }

    /// <summary>
    /// Claim a queued check, or a running check whose worker lease expired. The
    /// generation and owner fence prevent an older job from writing over a newer
    /// explicit retry.
    /// </summary>
    public async Task<bool> ClaimAsync(string projectId, long generation, string owner,
        DateTime leaseUntil, CancellationToken ct = default)
    {
        var now = Now();
        var filter = new BsonDocument
        {
            ["project_id"] = projectId,
            ["generation"] = generation,
            ["$or"] = new BsonArray
            {
                new BsonDocument("status", StatusQueued),
                new BsonDocument
                {
                    ["status"] = StatusRunning,
                    ["$or"] = new BsonArray
                    {
                        new BsonDocument("lease_until", BsonNull.Value),
                        new BsonDocument("lease_until", new BsonDocument("$exists", false)),
                        new BsonDocument("lease_until", new BsonDocument("$lte", now)),
                    },
                },
            },
        };
        var result = await store.Collection(CheckCollection).UpdateOneAsync(
            filter,
            new BsonDocument("$set", new BsonDocument
            {
                ["status"] = StatusRunning,
                ["lease_owner"] = owner,
                ["lease_until"] = BsonDateTime.Create(leaseUntil),
                ["updated_at"] = now,
            }), cancellationToken: ct).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> CompleteAsync(string projectId, long generation, string owner,
        string verdict, IReadOnlyList<BsonDocument> suspects, CancellationToken ct = default)
    {
        var result = await store.Collection(CheckCollection).UpdateOneAsync(
            Fence(projectId, generation, owner),
            new BsonDocument("$set", new BsonDocument
            {
                ["verdict"] = verdict,
                ["suspects"] = new BsonArray(suspects),
                ["suspect_count"] = suspects.Count,
                ["status"] = StatusSucceeded,
                ["error"] = "",
                ["updated_at"] = Now(),
            }), cancellationToken: ct).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    /// <summary>Mark a genuine failure. This must never be written as clear.</summary>
    public async Task<bool> FailAsync(string projectId, long generation, string owner,
        string message, CancellationToken ct = default)
    {
        var result = await store.Collection(CheckCollection).UpdateOneAsync(
            Fence(projectId, generation, owner),
            new BsonDocument("$set", new BsonDocument
            {
                ["status"] = StatusFailed,
                ["verdict"] = VerdictFailed,
                ["error"] = message,
                ["updated_at"] = Now(),
            }), cancellationToken: ct).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    /// <summary>Failure path for a check that could not even be enqueued.</summary>
    public async Task<bool> FailQueuedAsync(string projectId, long generation, string message,
        CancellationToken ct = default)
    {
        var result = await store.Collection(CheckCollection).UpdateOneAsync(
            new BsonDocument
            {
                ["project_id"] = projectId,
                ["generation"] = generation,
                ["status"] = StatusQueued,
            },
            new BsonDocument("$set", new BsonDocument
            {
                ["status"] = StatusFailed,
                ["verdict"] = VerdictFailed,
                ["error"] = message,
                ["updated_at"] = Now(),
            }), cancellationToken: ct).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    private static BsonDocument Fence(string projectId, long generation, string owner) => new()
    {
        ["project_id"] = projectId,
        ["generation"] = generation,
        ["status"] = StatusRunning,
        ["lease_owner"] = owner,
    };

    /// <summary>Fresh cached query, preserving the cached work order and skipping lost works.</summary>
    public async Task<(IReadOnlyList<ZitengPartnerWork> Works, bool Truncated)?> GetCachedAsync(
        string keyword, double ttlSeconds, CancellationToken ct = default)
    {
        var query = await store.Collection(QueryCollection)
            .Find(new BsonDocument("keyword", keyword))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (query is null || !query.TryGetValue("queried_at", out var queried) || !queried.IsBsonDateTime)
            return null;
        var age = clock.GetUtcNow().UtcDateTime - queried.ToUniversalTime();
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age >= TimeSpan.FromSeconds(Math.Max(0, ttlSeconds))) return null;

        var ids = query.TryGetValue("work_ids", out var rawIds) && rawIds.IsBsonArray
            ? rawIds.AsBsonArray.Where(value => value.IsString).Select(value => value.AsString).ToList()
            : [];
        if (ids.Count == 0) return ([], query.Flag("truncated"));

        var documents = await store.Collection(WorkCollection)
            .Find(new BsonDocument("work_id", new BsonDocument("$in", new BsonArray(ids))))
            .ToListAsync(ct).ConfigureAwait(false);
        var byId = new Dictionary<string, ZitengPartnerWork>(StringComparer.Ordinal);
        foreach (var document in documents)
            byId[document.Text("work_id")] = Work(document);
        var ordered = new List<ZitengPartnerWork>();
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var work)) ordered.Add(work);
        return (ordered, query.Flag("truncated"));
    }

    /// <summary>Upsert each work by the partner id, then upsert the query cache.</summary>
    public async Task StoreAsync(string keyword, IReadOnlyList<ZitengPartnerWork> works, int total,
        bool truncated, CancellationToken ct = default)
    {
        var now = Now();
        var workIds = new BsonArray();
        foreach (var work in works)
        {
            workIds.Add(work.Id);
            await store.Collection(WorkCollection).UpdateOneAsync(
                new BsonDocument("work_id", work.Id),
                new BsonDocument
                {
                    ["$set"] = new BsonDocument
                    {
                        ["reference"] = work.Reference,
                        ["display_title"] = work.DisplayTitle,
                        ["original_title"] = work.OriginalTitle,
                        ["author"] = work.Author,
                        ["circle"] = work.Circle,
                        ["state"] = work.State,
                        ["stage"] = work.Stage,
                        ["fetched_at"] = now,
                    },
                    ["$setOnInsert"] = new BsonDocument("normalized_title", ""),
                },
                new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
        }

        await store.Collection(QueryCollection).UpdateOneAsync(
            new BsonDocument("keyword", keyword),
            new BsonDocument("$set", new BsonDocument
            {
                ["work_ids"] = workIds,
                ["total"] = total,
                ["truncated"] = truncated,
                ["queried_at"] = now,
            }),
            new UpdateOptions { IsUpsert = true }, ct).ConfigureAwait(false);
    }

    /// <summary>Current project name, or "" when the project was deleted.</summary>
    public async Task<string> ReadProjectNameAsync(string projectId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(projectId, out var id)) return "";
        var project = await store.Collection("project")
            .Find(new BsonDocument("_id", id))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return project?.Text("n") ?? "";
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static CheckSnapshot Snapshot(BsonDocument document)
    {
        var suspects = document.TryGetValue("suspects", out var raw) && raw.IsBsonArray
            ? raw.AsBsonArray.Where(value => value.IsBsonDocument).Select(value => value.AsBsonDocument).ToList()
            : [];
        DateTime updated;
        if (document.TryGetValue("updated_at", out var rawUpdated) && rawUpdated.IsBsonDateTime)
            updated = rawUpdated.ToUniversalTime();
        else
            updated = DateTime.UnixEpoch;
        return new CheckSnapshot(
            document,
            document.Text("project_id"),
            document.Text("keyword"),
            (int)document.Integer("status"),
            document.Text("verdict"),
            suspects,
            (int)document.Integer("suspect_count", suspects.Count),
            document.Text("error"),
            updated,
            document.Integer("generation"));
    }

    private static ZitengPartnerWork Work(BsonDocument document) => new(
        document.Text("work_id"),
        document.Text("reference"),
        document.Text("display_title"),
        document.Text("original_title"),
        document.Text("author"),
        document.Text("circle"),
        document.Text("state"),
        document.Text("stage"));

    private async Task DropIndexIfExistsAsync(string collection, string name, CancellationToken ct)
    {
        try
        {
            await store.Collection(collection).Indexes.DropOneAsync(name, ct).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.Code is 26 or 27)
        {
            // NamespaceNotFound / IndexNotFound: nothing to migrate.
        }
    }
}
