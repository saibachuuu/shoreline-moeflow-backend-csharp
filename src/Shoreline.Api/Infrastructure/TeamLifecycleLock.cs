using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Errors returned by the durable team lifecycle guard. 5120 is kept free for
/// this feature so it cannot be confused with the existing project label lock
/// (code 1) or the permission/validation errors used by team endpoints.
/// </summary>
public static class TeamLifecycleErrors
{
    public const string BusyMessage = "团队正在解散，不能执行该操作";

    public static ApiError Busy(string? detail = null)
    {
        var message = detail is null ? BusyMessage : $"{BusyMessage}: {detail}";
        return new ApiError("TeamDissolvingError", 5120, message, 409);
    }

    public static ApiError TeamNotFound() => new("TeamNotExistError", 3001, "团队不存在");

    public static ApiError DeleteIncomplete() => IdentityErrors.ClearRetryable();
}

/// <summary>
/// Test seam for the claim protocol. The production path does not need a
/// hook; tests use it to simulate a lost insert acknowledgement or a failure
/// after the durable deleting marker was written.
/// </summary>
public sealed class TeamLifecycleLockHooks
{
    public Func<ObjectId, string, CancellationToken, Task>? AfterInsertAsync { get; init; }
    public Func<ObjectId, string, CancellationToken, Task>? AfterMarkAsync { get; init; }
}

/// <summary>
/// A lease acquired by one DELETE request. The lease is deliberately not tied
/// to a TTL: a crashed owner must not be replaced silently. Release is always
/// owner-scoped and uses an independent five second budget so a cancelled
/// request cannot leave the durable row behind.
/// </summary>
public sealed class TeamLifecycleLease : IAsyncDisposable
{
    internal TeamLifecycleLease(
        TeamLifecycleLock owner,
        ObjectId teamId,
        string ownerId,
        BsonDocument team,
        bool resumed)
    {
        LeaseOwner = owner;
        TeamId = teamId;
        Owner = ownerId;
        Team = team;
        Resumed = resumed;
    }

    internal TeamLifecycleLock LeaseOwner { get; }
    public ObjectId TeamId { get; }
    public string Owner { get; }
    public BsonDocument Team { get; internal set; }
    public bool Resumed { get; }

    public async ValueTask DisposeAsync() => await LeaseOwner.ReleaseAsync(this);
}

/// <summary>
/// Durable, team-scoped lifecycle owner. The unique <c>_id</c> in
/// <c>team_lifecycle_lock</c> is the mutual exclusion primitive; the team
/// document also carries a resumable <c>lc_phase</c> marker so a partial
/// deletion can be retried by a later owner.
///
/// Existing write paths (create project, project-set move/import, invitation
/// join) are not edited by K1. They must call <see cref="EnsureWritableAsync"/>
/// before accepting a new write; until that wiring lands, this service can
/// only reject its own concurrent deletes and any caller that explicitly uses
/// the guard.
/// </summary>
public sealed class TeamLifecycleLock(
    MongoStore store,
    TimeProvider clock,
    ILogger<TeamLifecycleLock> logger)
{
    private const string DeletingPhase = "deleting";

    private IMongoCollection<BsonDocument> Locks => store.Collection("team_lifecycle_lock");
    private IMongoCollection<BsonDocument> Teams => store.Collection("team");

    public TeamLifecycleLockHooks? Hooks { get; init; }

    /// <summary>
    /// Try to acquire the team. A duplicate owner row means another delete is
    /// already in progress and returns 5120. Any uncertain insert outcome is
    /// followed by an owner-scoped bounded cleanup before the original failure
    /// is rethrown, so an acknowledged-but-lost insert cannot leak a forever
    /// lock.
    /// </summary>
    public async Task<TeamLifecycleLease> AcquireAsync(ObjectId teamId, CancellationToken ct = default)
    {
        var owner = Guid.NewGuid().ToString("N");
        var created = clock.GetUtcNow().UtcDateTime;
        try
        {
            try
            {
                await Locks.InsertOneAsync(new BsonDocument
                {
                    ["_id"] = teamId,
                    ["owner"] = owner,
                    ["created"] = created
                }, new InsertOneOptions(), ct);
            }
            catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
            {
                throw TeamLifecycleErrors.Busy();
            }

            if (Hooks?.AfterInsertAsync is { } afterInsert)
                await afterInsert(teamId, owner, ct);

            var team = await Teams.Find(new BsonDocument("_id", teamId)).FirstOrDefaultAsync(ct);
            if (team is null) throw TeamLifecycleErrors.TeamNotFound();

            return new TeamLifecycleLease(
                this,
                teamId,
                owner,
                team,
                resumed: team.Text("lc_phase") == DeletingPhase);
        }
        catch
        {
            await ReleaseBoundedAsync(teamId, owner);
            throw;
        }
    }

    /// <summary>
    /// Write the durable deleting marker before any project row is changed.
    /// The marker is left in place on a partial failure; a later retry with a
    /// new owner may resume it after acquiring the now-free lock.
    /// </summary>
    public async Task MarkDeletingAsync(TeamLifecycleLease lease, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var started = lease.Team.TryGetValue("lc_started", out var previous) &&
                      previous.IsBsonDateTime
            ? previous
            : now;

        var result = await Teams.UpdateOneAsync(
            new BsonDocument("_id", lease.TeamId),
            new BsonDocument("$set", new BsonDocument
            {
                ["lc_phase"] = DeletingPhase,
                ["lc_owner"] = lease.Owner,
                ["lc_started"] = started,
                ["lc_updated"] = now,
                ["lc_error"] = ""
            }),
            cancellationToken: ct);

        if (result.MatchedCount != 1) throw TeamLifecycleErrors.TeamNotFound();
        if (Hooks?.AfterMarkAsync is { } afterMark)
            await afterMark(lease.TeamId, lease.Owner, ct);
    }

    /// <summary>
    /// Re-read the team after the claim and verify this owner still owns the
    /// deleting marker. The returned document is fresh; callers must not rely
    /// on the initial pre-lock snapshot.
    /// </summary>
    public async Task<BsonDocument> ReadOwnedTeamAsync(
        TeamLifecycleLease lease,
        CancellationToken ct = default)
    {
        var team = await Teams.Find(new BsonDocument("_id", lease.TeamId)).FirstOrDefaultAsync(ct);
        if (team is null) throw TeamLifecycleErrors.TeamNotFound();
        if (team.Text("lc_phase") != DeletingPhase || team.Text("lc_owner") != lease.Owner)
            throw TeamLifecycleErrors.Busy("解散声明已失效，请重试");
        lease.Team = team;
        return team;
    }

    /// <summary>
    /// Contract for existing writers. Returns 5120 if a delete currently owns
    /// the team or if a durable deleting marker exists. This method is not
    /// retroactively invoked by TeamService/ProjectService in this slice.
    /// </summary>
    public async Task EnsureWritableAsync(ObjectId teamId, CancellationToken ct = default)
    {
        var team = await Teams.Find(new BsonDocument("_id", teamId))
            .Project(new BsonDocument { ["_id"] = 1, ["lc_phase"] = 1 })
            .FirstOrDefaultAsync(ct);
        if (team is null) throw TeamLifecycleErrors.TeamNotFound();

        var locked = await Locks.Find(new BsonDocument("_id", teamId))
            .Project(new BsonDocument { ["_id"] = 1 })
            .AnyAsync(ct);
        if (locked || team.Text("lc_phase") == DeletingPhase)
            throw TeamLifecycleErrors.Busy();
    }

    public async Task<bool> IsDeletingAsync(ObjectId teamId, CancellationToken ct = default)
    {
        var locked = await Locks.Find(new BsonDocument("_id", teamId))
            .Project(new BsonDocument { ["_id"] = 1 })
            .AnyAsync(ct);
        if (locked) return true;

        var team = await Teams.Find(new BsonDocument("_id", teamId))
            .Project(new BsonDocument { ["lc_phase"] = 1 })
            .FirstOrDefaultAsync(ct);
        return team is not null && team.Text("lc_phase") == DeletingPhase;
    }

    /// <summary>
    /// Best-effort journal update for a partial failure. The deleting marker is
    /// intentionally retained; the lock is released by the lease, allowing a
    /// later retry to resume cleanup.
    /// </summary>
    public async Task MarkFailureAsync(TeamLifecycleLease lease, string errorType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Teams.UpdateOneAsync(
                new BsonDocument { ["_id"] = lease.TeamId, ["lc_owner"] = lease.Owner },
                new BsonDocument("$set", new BsonDocument
                {
                    ["lc_updated"] = clock.GetUtcNow().UtcDateTime,
                    ["lc_error"] = errorType
                }),
                cancellationToken: timeout.Token);
        }
        catch (Exception error)
        {
            logger?.LogWarning(
                "Failed to record team dissolution failure for team {TeamId} owner {Owner} ({ErrorType})",
                lease.TeamId,
                lease.Owner,
                error.GetType().Name);
        }
    }

    public async Task ReleaseAsync(TeamLifecycleLease lease) =>
        await ReleaseBoundedAsync(lease.TeamId, lease.Owner);

    /// <summary>
    /// Owner-scoped CAS delete with an independent five second budget. A
    /// different owner is never released, including during an uncertain claim.
    /// </summary>
    private async Task<bool> ReleaseBoundedAsync(ObjectId teamId, string owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Locks.DeleteOneAsync(
                new BsonDocument { ["_id"] = teamId, ["owner"] = owner },
                timeout.Token);
            return true;
        }
        catch (Exception error)
        {
            logger?.LogWarning(
                "Failed to release team lifecycle lock for team {TeamId} owner {Owner} ({ErrorType}); manual recovery may be required",
                teamId,
                owner,
                error.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// One idempotent startup pass. Indexes are intentionally non-unique except
    /// the lock _id supplied by Mongo. No TTL index is created.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await Locks.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("owner")),
            cancellationToken: ct);

        await Teams.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("lc_phase")),
            cancellationToken: ct);

        await store.Collection("project").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("t").Ascending("_id")),
            cancellationToken: ct);
    }
}
