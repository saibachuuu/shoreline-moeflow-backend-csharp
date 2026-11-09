using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.ArchiveImport;

public sealed class ArchiveClaimOwner(ObjectId projectId, ObjectId taskId, string token, DateTime leaseUntil)
{
    public ObjectId ProjectId { get; } = projectId;
    public ObjectId TaskId { get; } = taskId;
    public string Token { get; } = token;
    public DateTime LeaseUntil { get; set; } = leaseUntil;
}

/// <summary>
/// Legacy <c>archive_import_task</c> collection. Fields intentionally match Python db_field names; only C#-internal
/// lease/checkpoint fields (<c>lo/lu/pi/ic</c>) are added.
/// </summary>
public sealed class ArchiveImportTaskStore(MongoStore store, TimeProvider clock)
{
    public const string CollectionName = "archive_import_task";

    private readonly IMongoCollection<BsonDocument> _tasks = store.Collection(CollectionName);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<BsonDocument?> LatestAsync(ObjectId projectId, CancellationToken ct) =>
        _tasks.Find(Builders<BsonDocument>.Filter.Eq("p", projectId))
            .Sort(Builders<BsonDocument>.Sort.Descending("_id"))
            .FirstOrDefaultAsync(ct);

    public Task<BsonDocument?> FindByIdAsync(ObjectId taskId, ObjectId projectId, CancellationToken ct) =>
        _tasks.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("p", projectId)))
            .FirstOrDefaultAsync(ct);

    public Task<BsonDocument?> FindActiveAsync(ObjectId projectId, CancellationToken ct) =>
        _tasks.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("p", projectId),
                Builders<BsonDocument>.Filter.In("s", ArchiveImportStatus.Running)))
            .Sort(Builders<BsonDocument>.Sort.Descending("_id"))
            .FirstOrDefaultAsync(ct);

    public Task<BsonDocument?> FindLegacyAsync(ObjectId projectId, string gid, string token,
        CancellationToken ct)
    {
        var filters = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq("p", projectId),
            Builders<BsonDocument>.Filter.In("s", ArchiveImportStatus.Running),
        };
        if (gid.Length != 0) filters.Add(Builders<BsonDocument>.Filter.Eq("g", gid));
        if (token.Length != 0) filters.Add(Builders<BsonDocument>.Filter.Eq("t", token));
        return _tasks.Find(Builders<BsonDocument>.Filter.And(filters))
            .Sort(Builders<BsonDocument>.Sort.Descending("_id"))
            .FirstOrDefaultAsync(ct);
    }

    public Task InsertAsync(BsonDocument task, CancellationToken ct) => _tasks.InsertOneAsync(task, cancellationToken: ct);

    public async Task<bool> TryTransitionToResolvingAsync(ObjectId taskId, ArchiveClaimOwner owner,
        CancellationToken ct)
    {
        var now = Now;
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["s"] = ArchiveImportStatus.Resolving,
            ["st"] = ArchiveImportText.ResolvingStage,
            ["ut"] = now,
            ["lo"] = owner.Token,
            ["lu"] = owner.LeaseUntil,
        });
        var result = await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("s", ArchiveImportStatus.Queued)),
            update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> TouchOwnerAsync(ObjectId taskId, ArchiveClaimOwner owner, CancellationToken ct)
    {
        var update = new BsonDocument("$set", new BsonDocument { ["lu"] = owner.LeaseUntil });
        var result = await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("lo", owner.Token),
                Builders<BsonDocument>.Filter.In("s", ArchiveImportStatus.Running)),
            update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TrySetStageAsync(ObjectId taskId, ArchiveClaimOwner owner, int status, string stage,
        CancellationToken ct, int? totalPages = null, int? completedPages = null)
    {
        var now = Now;
        var set = new BsonDocument
        {
            ["s"] = status,
            ["st"] = stage,
            ["ut"] = now,
            ["lu"] = owner.LeaseUntil,
        };
        if (totalPages is not null) set["n"] = totalPages.Value;
        if (completedPages is not null) set["c"] = completedPages.Value;
        var result = await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("lo", owner.Token),
                Builders<BsonDocument>.Filter.In("s", ArchiveImportStatus.Running)),
            new BsonDocument("$set", set), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> TryAdvanceAsync(ObjectId taskId, ArchiveClaimOwner owner, int completedPages,
        int imported, CancellationToken ct)
    {
        var now = Now;
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["c"] = completedPages,
            ["pi"] = completedPages,
            ["ic"] = imported,
            ["ut"] = now,
            ["lu"] = owner.LeaseUntil,
        });
        var result = await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("lo", owner.Token),
                Builders<BsonDocument>.Filter.In("s", ArchiveImportStatus.Running)),
            update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> TryFinishAsync(ObjectId taskId, ArchiveClaimOwner owner, int status, string stage,
        string error, int? completedPages, int? totalPages, CancellationToken ct)
    {
        var now = Now;
        var set = new BsonDocument
        {
            ["s"] = status,
            ["st"] = stage,
            ["e"] = error.Length > 2000 ? error[..2000] : error,
            ["ut"] = now,
            ["lo"] = BsonNull.Value,
            ["lu"] = BsonNull.Value,
        };
        if (completedPages is not null) set["c"] = completedPages.Value;
        if (totalPages is not null) set["n"] = totalPages.Value;
        var result = await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("lo", owner.Token)),
            new BsonDocument("$set", set), cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task TryFailNewAsync(ObjectId taskId, string stage, string error, CancellationToken ct)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["s"] = ArchiveImportStatus.Failed,
            ["st"] = stage,
            ["e"] = error.Length > 2000 ? error[..2000] : error,
            ["ut"] = Now,
            ["lo"] = BsonNull.Value,
            ["lu"] = BsonNull.Value,
        });
        await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.Eq("s", ArchiveImportStatus.Queued)),
            update, cancellationToken: ct);
    }

    public async Task<bool> TryMarkStaleFailedAsync(ObjectId taskId, DateTime cutoff, CancellationToken ct)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["s"] = ArchiveImportStatus.Failed,
            ["st"] = ArchiveImportText.FailedStage,
            ["e"] = "任务已中断",
            ["ut"] = Now,
            ["lo"] = BsonNull.Value,
            ["lu"] = BsonNull.Value,
        });
        var result = await _tasks.UpdateOneAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", taskId),
                Builders<BsonDocument>.Filter.In("s", ArchiveImportStatus.Running),
                Builders<BsonDocument>.Filter.Lte("ut", cutoff)),
            update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public Task DismissAsync(ObjectId projectId, CancellationToken ct)
    {
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["d"] = true,
            ["ut"] = Now,
        });
        return _tasks.UpdateManyAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("p", projectId),
                Builders<BsonDocument>.Filter.Nin("s", ArchiveImportStatus.Running)),
            update, cancellationToken: ct);
    }
}

/// <summary>
/// One live claim per project. A separate collection is used because legacy task rows may already contain several
/// running records, which would make a partial unique index on archive_import_task impossible to create.
/// </summary>
public sealed class ArchiveImportClaimStore(MongoStore store, TimeProvider clock)
{
    public const string CollectionName = "archive_import_active";

    private readonly IMongoCollection<BsonDocument> _claims = store.Collection(CollectionName);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<BsonDocument?> GetAsync(ObjectId projectId, CancellationToken ct) =>
        _claims.Find(Builders<BsonDocument>.Filter.Eq("_id", projectId)).FirstOrDefaultAsync(ct);

    public async Task<bool> TryInsertAsync(ObjectId projectId, ObjectId taskId, CancellationToken ct)
    {
        var now = Now;
        var document = new BsonDocument
        {
            ["_id"] = projectId,
            ["task_id"] = taskId,
            ["owner"] = BsonNull.Value,
            ["lease_until"] = BsonNull.Value,
            ["created_at"] = now,
            ["updated_at"] = now,
        };
        try
        {
            await _claims.InsertOneAsync(document, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
        catch (MongoCommandException error) when (error.Code == 11000)
        {
            return false;
        }
    }

    public async Task<bool> RebindAsync(ObjectId projectId, ObjectId? expectedTaskId, ObjectId taskId,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Filter.Eq("task_id", expectedTaskId is null
                ? BsonNull.Value
                : (BsonValue)new BsonObjectId(expectedTaskId.Value)));
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["task_id"] = taskId,
            ["owner"] = BsonNull.Value,
            ["lease_until"] = BsonNull.Value,
            ["updated_at"] = Now,
        });
        var result = await _claims.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryTakeAsync(ObjectId projectId, ObjectId taskId, string owner, DateTime leaseUntil,
        DateTime now, CancellationToken ct)
    {
        var free = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("owner", BsonNull.Value),
            Builders<BsonDocument>.Filter.Eq("lease_until", BsonNull.Value),
            Builders<BsonDocument>.Filter.Lt("lease_until", now));
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Filter.Eq("task_id", taskId),
            free);
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["owner"] = owner,
            ["lease_until"] = leaseUntil,
            ["updated_at"] = now,
        });
        var result = await _claims.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> RenewAsync(ObjectId projectId, ObjectId taskId, string owner, DateTime leaseUntil,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Filter.Eq("task_id", taskId),
            Builders<BsonDocument>.Filter.Eq("owner", owner));
        var update = new BsonDocument("$set", new BsonDocument
        {
            ["lease_until"] = leaseUntil,
            ["updated_at"] = Now,
        });
        var result = await _claims.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task ReleaseAsync(ObjectId projectId, ObjectId taskId, string owner, CancellationToken ct) =>
        _claims.DeleteOneAsync(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Filter.Eq("task_id", taskId),
            Builders<BsonDocument>.Filter.Eq("owner", owner)), ct);

    public Task ReleaseUnownedAsync(ObjectId projectId, ObjectId taskId, CancellationToken ct) =>
        _claims.DeleteOneAsync(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Filter.Eq("task_id", taskId),
            Builders<BsonDocument>.Filter.Eq("owner", BsonNull.Value)), ct);

    public Task DeleteAsync(ObjectId projectId, ObjectId taskId, CancellationToken ct) =>
        _claims.DeleteOneAsync(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Filter.Eq("task_id", taskId)), ct);
}
