using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Compatibility errors for the project label lock. The Python implementation
/// raises LabelBusyError with code 1 and HTTP status 409.
/// </summary>
public static class LabelErrors
{
    public const string DefaultBusyMessage = "项目标签正在操作或等待恢复，请刷新后重试";

    public static ApiError Busy(string? detail = null)
    {
        var message = detail is null ? DefaultBusyMessage : $"{DefaultBusyMessage}: {detail}";
        return new ApiError("LabelBusyError", 1, message, 409);
    }

    public static ApiError Stale(string? detail = null) =>
        Busy(detail ?? "标签数据已被批量修改，旧任务或旧对象不能继续写入");

    public static ApiError StaleReference() =>
        Busy("标签引用已过期，请重新加载后写入");

    public static ApiError ProjectChanged() =>
        Busy("项目数据已改变，请重新加载后写入");
}

/// <summary>
/// One held lease for a project. The lease deliberately has no TTL: a stalled
/// writer must not be silently replaced by another owner.
/// </summary>
public sealed class ProjectLabelLease
{
    internal ProjectLabelLease(ILabelLeaseOwner leaseOwner, ObjectId projectId, string owner,
        BsonDocument project, long revision, bool recovery)
    {
        LeaseOwner = leaseOwner;
        ProjectId = projectId;
        Owner = owner;
        Project = project;
        Revision = revision;
        Recovery = recovery;
    }

    internal ILabelLeaseOwner LeaseOwner { get; }
    public ObjectId ProjectId { get; }
    public string Owner { get; }
    public BsonDocument Project { get; }
    public long Revision { get; }
    public bool Recovery { get; }
}

/// <summary>
/// Scoped object shared by middleware and services. It lets a request that has
/// already entered the lock reuse that exact lease instead of trying to acquire
/// the same non-reentrant database lock a second time.
/// </summary>
public sealed class LabelRequestContext
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ObjectId, LabelSession> sessions = new();

    internal async Task<LabelSession> EnterAsync(
        ProjectLabelLock labels,
        ObjectId projectId,
        bool recovery,
        long? expectedRevision,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (sessions.TryGetValue(projectId, out var existing))
            {
                if (expectedRevision is not null && existing.Revision != expectedRevision.Value)
                    throw LabelErrors.Busy("后台任务的标签版本已过期");
                return existing.Borrow();
            }

            var lease = await labels.AcquireAsync(projectId, recovery, expectedRevision, ct);
            var session = new LabelSession(this, lease, owner: true);
            sessions[projectId] = session;
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task ReleaseAsync(LabelSession session)
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (sessions.TryGetValue(session.ProjectId, out var current) &&
                ReferenceEquals(current, session))
            {
                sessions.Remove(session.ProjectId);
            }
        }
        finally
        {
            gate.Release();
        }
        await session.ReleaseLeaseAsync();
    }
}

/// <summary>
/// A use of a project lease. Sessions returned to services are borrowed; the
/// owner (middleware or the outermost service call) removes the lease.
/// </summary>
public sealed class LabelSession : IAsyncDisposable
{
    private readonly LabelRequestContext? context;
    private readonly bool owner;

    internal LabelSession(LabelRequestContext context, ProjectLabelLease lease, bool owner)
    {
        this.context = context;
        this.owner = owner;
        Lease = lease;
    }

    internal ProjectLabelLease Lease { get; }

    public ObjectId ProjectId => Lease.ProjectId;
    public BsonDocument Project => Lease.Project;
    public long Revision => Lease.Revision;
    public bool Recovery => Lease.Recovery;

    /// <summary>
    /// Mirrors LabelDocument._assert_current for the row currently being
    /// mutated. Legacy rows without label_epoch are treated as revision 0.
    /// </summary>
    public void AssertEpoch(BsonDocument row, ApiError? error = null)
    {
        if (row.Integer("label_epoch", 0) != Revision)
            throw error ?? LabelErrors.Stale();
    }

    /// <summary>
    /// Check a referenced label document (file/source/target) before using it.
    /// </summary>
    public void AssertReferenceEpoch(BsonDocument row)
    {
        if (row.Integer("label_epoch", 0) != Revision)
            throw LabelErrors.StaleReference();
    }

    internal LabelSession Borrow() => new(context!, Lease, owner: false);

    internal async Task ReleaseLeaseAsync() => await Lease.LeaseOwner.ReleaseAsync(Lease);

    public async ValueTask DisposeAsync()
    {
        if (owner && context is not null)
            await context.ReleaseAsync(this);
    }
}

internal interface ILabelLeaseOwner
{
    Task ReleaseAsync(ProjectLabelLease lease);
}

/// <summary>
/// Optional fault-injection seam around the lock collection and the post-insert
/// project read. It exists so tests can reproduce transport cancellation and an
/// unknown insert outcome without replacing the real Mongo lock implementation.
/// Production callers leave it null.
/// </summary>
public sealed class ProjectLabelLockHooks
{
    /// <summary>Runs after the lock insert has been acknowledged.</summary>
    public Func<ObjectId, string, CancellationToken, Task>? AfterInsertAsync { get; init; }

    /// <summary>Runs immediately before the post-insert project read.</summary>
    public Func<ObjectId, CancellationToken, Task>? BeforeProjectReadAsync { get; init; }
}

/// <summary>
/// Shared, collection-backed project label mutex.
/// Collection: project_label_lock { _id: projectId, owner: uuid, created: utc }.
/// Insert is unique; acquisition polls for at most two seconds every 25 ms.
/// </summary>
public sealed class ProjectLabelLock(
    MongoStore store,
    TimeProvider clock,
    ILogger<ProjectLabelLock>? logger = null,
    ProjectLabelLockHooks? hooks = null) : ILabelLeaseOwner
{
    public const int WaitMilliseconds = 2000;
    public const int PollMilliseconds = 25;

    private IMongoCollection<BsonDocument> Locks => store.Collection("project_label_lock");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");

    public async Task<LabelSession> EnterAsync(
        LabelRequestContext context,
        ObjectId projectId,
        bool recovery = false,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        return await context.EnterAsync(this, projectId, recovery, expectedRevision, ct);
    }

    /// <summary>
    /// Explicit entry point for snapshot/recovery jobs. The returned session is
    /// owned by the caller and must be disposed; no TTL or owner takeover is
    /// offered. Pass the same <paramref name="context"/> to services in the same
    /// scope (or to <see cref="EnterAsync"/>) so their calls reuse this lease.
    /// </summary>
    public Task<LabelSession> AcquireRecoveryAsync(
        LabelRequestContext context,
        ObjectId projectId,
        long? expectedRevision = null,
        CancellationToken ct = default) =>
        context.EnterAsync(this, projectId, recovery: true, expectedRevision, ct);

    /// <summary>
    /// Explicit entry point for snapshot/recovery jobs. Callers receive an
    /// owning session and must dispose it; no TTL or owner takeover is offered.
    /// </summary>
    public async Task<LabelSession> AcquireRecoveryAsync(
        ObjectId projectId,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var lease = await AcquireAsync(projectId, recovery: true, expectedRevision, ct);
        var context = new LabelRequestContext();
        return new LabelSession(context, lease, owner: true);
    }

    internal async Task<ProjectLabelLease> AcquireAsync(
        ObjectId projectId,
        bool recovery,
        long? expectedRevision,
        CancellationToken ct)
    {
        // The owner is created before any write. From this point on every
        // failure path attempts a bounded, owner-scoped cleanup. That includes
        // an insert whose acknowledgement was lost: if the server did write our
        // row, the delete removes it; if it did not, the delete is a no-op.
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            if (expectedRevision is not null)
            {
                var current = await Projects.Find(new BsonDocument("_id", projectId))
                    .Project(new BsonDocument("label_revision", 1))
                    .FirstOrDefaultAsync(ct);
                if (current is null) throw ProjectNotFound();
                if (current.Integer("label_revision", 0) != expectedRevision.Value)
                    throw LabelErrors.Busy("后台任务的标签版本已过期");
            }

            var created = clock.GetUtcNow().UtcDateTime;
            var deadline = Stopwatch.GetTimestamp() + (WaitMilliseconds * Stopwatch.Frequency / 1000);
            while (true)
            {
                try
                {
                    await Locks.InsertOneAsync(
                        new BsonDocument { ["_id"] = projectId, ["owner"] = owner, ["created"] = created },
                        cancellationToken: ct);
                    break;
                }
                catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
                {
                    if (Stopwatch.GetTimestamp() >= deadline) throw LabelErrors.Busy();
                    await Task.Delay(PollMilliseconds, ct);
                }
            }

            if (hooks?.AfterInsertAsync is { } afterInsert)
                await afterInsert(projectId, owner, ct);

            if (hooks?.BeforeProjectReadAsync is { } beforeProjectRead)
                await beforeProjectRead(projectId, ct);

            var project = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct);
            if (project is null) throw ProjectNotFound();

            if (expectedRevision is not null && project.Integer("label_revision", 0) != expectedRevision.Value)
                throw LabelErrors.Busy("后台任务的标签版本已过期");

            if (HasRecovery(project) && !recovery)
                throw LabelErrors.Busy();

            return new ProjectLabelLease(this, projectId, owner, project,
                project.Integer("label_revision", 0), recovery);
        }
        catch
        {
            // Never use the cancelled request token here. A lock without a TTL
            // would remain stuck forever if this cleanup were skipped.
            await ReleaseBoundedAsync(projectId, owner);
            throw;
        }
    }

    async Task ILabelLeaseOwner.ReleaseAsync(ProjectLabelLease lease) =>
        await ReleaseBoundedAsync(lease.ProjectId, lease.Owner);

    /// <summary>
    /// Owner-scoped CAS delete with an independent five second budget. Returns
    /// false only when the delete could not be attempted or failed; that case is
    /// logged because the lock has no TTL and needs manual recovery attention.
    /// </summary>
    private async Task<bool> ReleaseBoundedAsync(ObjectId projectId, string owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Locks.DeleteOneAsync(
                new BsonDocument { ["_id"] = projectId, ["owner"] = owner },
                timeout.Token);
            return true;
        }
        catch (Exception error)
        {
            // A lock without a TTL cannot be silently abandoned. Log with the
            // project/owner only (ids are not credentials) so an operator can
            // recover the row if the bounded delete could not reach Mongo.
            // Never log the exception object: Mongo messages can include endpoint
            // or other connection details. Only the non-secret ids and type name
            // are sufficient for an operator to locate the stuck row.
            logger?.LogWarning(
                "Failed to release project label lock for project {ProjectId} owner {Owner} ({ErrorType}); manual recovery may be required",
                projectId,
                owner,
                error.GetType().Name);
            return false;
        }
    }

    public static bool HasRecovery(BsonDocument project) =>
        project.TryGetValue("label_recovery", out var value) &&
        value is not null && value != BsonNull.Value && !value.IsBsonNull;

    private static ApiError ProjectNotFound() =>
        new("ProjectNotExistError", 4001, "项目不存在");
}

/// <summary>
/// Convenience wrappers used by services and the request guard.
/// </summary>
public static class ProjectLabelLockExtensions
{
    public static async Task<long> ReadRevisionAsync(this ProjectLabelLock labels, MongoStore store,
        ObjectId projectId, CancellationToken ct = default)
    {
        var row = await store.Collection("project").Find(new BsonDocument("_id", projectId))
            .Project(new BsonDocument("label_revision", 1))
            .FirstOrDefaultAsync(ct);
        return row?.Integer("label_revision", 0) ?? 0;
    }
}
