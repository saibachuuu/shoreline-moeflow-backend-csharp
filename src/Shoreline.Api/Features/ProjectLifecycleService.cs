using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

// Item 34, next stage. Project lifecycle transitions and owner transfer,
// ported 1:1 from Python app/services/project_lifecycle.py. The storage
// backend's pluggable state guards (`_run_state_guards`) are a no-op in core
// and stay one. Clear contents physically deletes file/output objects and
// cascaded rows through ProjectContentCleanup, then zeroes counters.
public sealed class ProjectLifecycleService(MongoStore store, TimeProvider clock, IdentityPermissions permissions,
    IdentityAuditWriter audit, ProjectMemberService members, ObjectStorage? storage = null,
    IConfiguration? configuration = null, ILogger<ProjectLifecycleService>? logger = null,
    Func<string, CancellationToken, Task>? deleteObject = null,
    ProjectStorageRouter? router = null)
{
    private static readonly TimeSpan ClaimReleaseTimeout = TimeSpan.FromSeconds(5);
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    public static string StatusName(int status) => status switch
    {
        1 => "CLEARED",
        5 => "COMPLETED",
        _ => "NORMAL"
    };

    private static BsonDocument ProjectStateDoc(BsonDocument project) => new()
    {
        ["status"] = StatusName((int)project.Integer("st")),
        ["status_version"] = project.Integer("stv"),
        ["owner_user_id"] = project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ou ? ou.ToString() : BsonNull.Value
    };

    private async Task<BsonDocument> RequireActionAsync(BsonDocument actor, BsonDocument project, string action, CancellationToken ct)
    {
        if (!await permissions.CanProjectActionAsync(actor, project, action, ct)) throw ApiError.Forbidden();
        var current = await Projects.Find(new BsonDocument("_id", project["_id"])).FirstOrDefaultAsync(ct);
        if (current is null) throw new ApiError("NotFoundError", 404, "项目不存在");
        return current;
    }

    private static void EnsureClearNotInProgress(BsonDocument project)
    {
        if (project.Flag("cip"))
            throw new ApiError("ProjectStateConflictError", 409, "项目正在处理内容清理");
    }

    /// <summary>
    /// Python `_expected(payload, allowed)`: validates expected_status against
    /// the allowed set, expected_version typing, and the current status_version.
    /// Returns the expected version to fence the CAS write (current.json when omitted).
    /// </summary>
    private static long ExpectedVersion(BsonDocument project, JsonElementLite? payload, params string[] allowed)
    {
        var expectedStatus = payload?.Get("expected_status")?.AsString;
        if (expectedStatus is not null)
        {
            if (!allowed.Contains(expectedStatus))
                throw IdentityErrors.InvalidRequest("expected_status is not allowed for this operation");
            // Python _expected checks the allowed set AND that the supplied
            // status is the current one; the allowed set alone is ambiguous
            // between NORMAL/COMPLETED for clear.
            if (expectedStatus != StatusName((int)project.Integer("st")))
                throw IdentityErrors.StateConflict();
        }
        var raw = payload?.Get("expected_version");
        if (raw is not null && !raw.IsInt32) throw IdentityErrors.InvalidRequest();
        var expected = raw?.AsInt32;
        if (expected is not null && expected.Value != project.Integer("stv"))
            throw IdentityErrors.StateConflict();
        return expected ?? project.Integer("stv");
    }

    /// <summary>
    /// Legacy project documents may predate status_version. Python treats the
    /// default 0 as matching both a literal 0 and a missing field, so every CAS
    /// filter must use the same condition.
    /// </summary>
    private static void AddStatusVersionFilter(BsonDocument filter, long expected)
    {
        if (expected == 0)
        {
            filter["$or"] = new BsonArray
            {
                new BsonDocument("stv", 0),
                new BsonDocument("stv", new BsonDocument("$exists", false))
            };
        }
        else
        {
            filter["stv"] = expected;
        }
    }

    public async Task<(BsonDocument Project, bool Changed)> CompleteAsync(BsonDocument actor, BsonDocument project,
        JsonElementLite? payload, string requestId, CancellationToken ct)
    {
        project = await RequireActionAsync(actor, project, "complete", ct);
        EnsureClearNotInProgress(project);
        var status = project.Integer("st");
        if (status == 5)
        {
            ExpectedVersion(project, payload, "COMPLETED");
            return (project, false);
        }
        if (status != 0) throw IdentityErrors.StateConflict();
        var expected = ExpectedVersion(project, payload, "NORMAL");
        var before = ProjectStateDoc(project);
        var filter = new BsonDocument
        {
            ["_id"] = project["_id"],
            ["st"] = 0,
            ["cip"] = new BsonDocument("$ne", true)
        };
        AddStatusVersionFilter(filter, expected);
        var updated = await Projects.FindOneAndUpdateAsync(
            filter,
            new BsonDocument
            {
                // Python project.completed_time db_field is "ctm".
                ["$set"] = new BsonDocument { ["st"] = 5, ["ctm"] = Now() },
                ["$inc"] = new BsonDocument("stv", 1)
            },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken: ct)
            ?? throw IdentityErrors.StateConflict();
        var snapshot = await permissions.ProjectSnapshotAsync(actor, updated, ct);
        await audit.RecordProjectAsync(actor, "project_complete", updated, null, before, ProjectStateDoc(updated),
            requestId, ct, snapshot.PermissionSources.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<string>)kvp.Value));
        return (updated, true);
    }

    public async Task<(BsonDocument Project, bool Changed)> ReopenAsync(BsonDocument actor, BsonDocument project,
        JsonElementLite? payload, string requestId, CancellationToken ct)
    {
        project = await RequireActionAsync(actor, project, "reopen", ct);
        EnsureClearNotInProgress(project);
        var status = project.Integer("st");
        if (status == 0)
        {
            ExpectedVersion(project, payload, "NORMAL");
            return (project, false);
        }
        if (status != 5) throw IdentityErrors.StateConflict();
        var expected = ExpectedVersion(project, payload, "COMPLETED");
        var before = ProjectStateDoc(project);
        var filter = new BsonDocument
        {
            ["_id"] = project["_id"],
            ["st"] = 5,
            ["cip"] = new BsonDocument("$ne", true)
        };
        AddStatusVersionFilter(filter, expected);
        var updated = await Projects.FindOneAndUpdateAsync(
            filter,
            new BsonDocument
            {
                ["$set"] = new BsonDocument("st", 0),
                ["$unset"] = new BsonDocument("ctm", ""),
                ["$inc"] = new BsonDocument("stv", 1)
            },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken: ct)
            ?? throw IdentityErrors.StateConflict();
        var snapshot = await permissions.ProjectSnapshotAsync(actor, updated, ct);
        await audit.RecordProjectAsync(actor, "project_reopen", updated, null, before, ProjectStateDoc(updated),
            requestId, ct, snapshot.PermissionSources.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<string>)kvp.Value));
        return (updated, true);
    }

    public async Task<(BsonDocument Project, bool Changed)> ClearAsync(BsonDocument actor, BsonDocument project,
        JsonElementLite? payload, string requestId, CancellationToken ct)
    {
        project = await RequireActionAsync(actor, project, "clear", ct);
        EnsureClearNotInProgress(project);
        var status = project.Integer("st");
        if (status == 1)
        {
            ExpectedVersion(project, payload, "CLEARED");
            return (project, false);
        }
        if (status is not (0 or 5)) throw IdentityErrors.StateConflict();
        var expected = ExpectedVersion(project, payload, "NORMAL", "COMPLETED");
        var before = ProjectStateDoc(project);

        // Atomic claim: the expected version and status are part of the filter.
        // A stale expected_version must fail here, before any DB row or object
        // is touched (Python clear claim: status in (NORMAL, COMPLETED),
        // status_version=expected, clear_in_progress__ne=True). The owner token
        // is private to this invocation; final/release CAS filters include it so
        // an older invocation can never unlock or commit a newer claim.
        var owner = ObjectId.GenerateNewId();
        var projectId = project["_id"].AsObjectId;
        var claimFilter = new BsonDocument
        {
            ["_id"] = project["_id"],
            ["st"] = new BsonDocument("$in", new BsonArray { new BsonInt32(0), new BsonInt32(5) }),
            ["cip"] = new BsonDocument("$ne", true)
        };
        AddStatusVersionFilter(claimFilter, expected);
        BsonDocument locked;
        try
        {
            locked = await Projects.FindOneAndUpdateAsync(
                claimFilter,
                new BsonDocument("$set", new BsonDocument { ["cip"] = true, ["clear_owner"] = owner }),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken: ct)
                ?? throw IdentityErrors.StateConflict();
        }
        catch (OperationCanceledException)
        {
            // A cancellation can be observed after the server applied the claim.
            // Release by owner before propagating the original cancellation.
            if (!await TryReleaseClaimAsync(projectId, owner))
                await TryAuditRecoveryRequiredAsync(actor, project, before, requestId);
            throw;
        }
        try
        {
            await ClearContentsAsync(locked, ct);
        }
        catch (OperationCanceledException)
        {
            // The request token may already be cancelled. Release must use its
            // own bounded token, and the original cancellation must propagate so
            // callers do not receive a fake retryable 503.
            if (!await TryReleaseClaimAsync(projectId, owner))
                await TryAuditRecoveryRequiredAsync(actor, project, before, requestId);
            throw;
        }
        catch (Exception)
        {
            // Release the owned claim before any best-effort audit. A failed
            // audit must never leave cip=true behind.
            if (await TryReleaseClaimAsync(projectId, owner))
                await TryAuditRetryableFailureAsync(actor, project, before, requestId);
            else
                await TryAuditRecoveryRequiredAsync(actor, project, before, requestId);
            throw IdentityErrors.ClearRetryable();
        }

        var finalFilter = new BsonDocument
        {
            ["_id"] = project["_id"],
            ["cip"] = true,
            ["clear_owner"] = owner,
            ["st"] = new BsonDocument("$in", new BsonArray { new BsonInt32(0), new BsonInt32(5) })
        };
        AddStatusVersionFilter(finalFilter, expected);
        BsonDocument? updated;
        try
        {
            updated = await Projects.FindOneAndUpdateAsync(
                finalFilter,
                new BsonDocument
                {
                    ["$set"] = new BsonDocument { ["st"] = 1, ["cip"] = false },
                    ["$unset"] = new BsonDocument("clear_owner", ""),
                    ["$inc"] = new BsonDocument("stv", 1)
                },
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            // Do not leave a claim behind when the request token is cancelled
            // around the final status transition.
            if (!await TryReleaseClaimAsync(projectId, owner))
                await TryAuditRecoveryRequiredAsync(actor, project, before, requestId);
            throw;
        }
        if (updated is null)
        {
            // Python recovers the claim before raising the state conflict; the
            // project must not stay permanently locked, and it is not CLEARED.
            // Only the owner that still holds the claim may release it.
            if (!await TryReleaseClaimAsync(projectId, owner))
                await TryAuditRecoveryRequiredAsync(actor, project, before, requestId);
            throw IdentityErrors.StateConflict();
        }
        var snapshot = await permissions.ProjectSnapshotAsync(actor, updated, ct);
        await audit.RecordProjectAsync(actor, "project_clear", updated, null, before, ProjectStateDoc(updated),
            requestId, ct, snapshot.PermissionSources.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<string>)kvp.Value));
        return (updated, true);
    }

    private async Task ClearContentsAsync(BsonDocument project, CancellationToken ct)
    {
        if (deleteObject is not null)
        {
            await ProjectContentCleanup.ClearProjectContentsAsync(
                store, ProjectCleanupOptions.FromConfiguration(configuration), deleteObject, project, ct);
            return;
        }
        if (storage is null)
            throw new InvalidOperationException("Object storage is required to clear project contents.");
        if (router is not null)
        {
            await ProjectContentCleanup.ClearProjectContentsAsync(
                store, router, storage, configuration, project, ct);
            return;
        }
        await ProjectContentCleanup.ClearProjectContentsAsync(store, storage, configuration, project, ct);
    }

    /// <summary>
    /// Best-effort release of a claim owned by this invocation. Uses a fresh
    /// bounded token because the request token may already be cancelled. The
    /// owner token is never logged. If release cannot be proven, the project
    /// remains locked for an explicit recovery retry; we do not claim it was
    /// unlocked.
    /// </summary>
    private async Task<bool> TryReleaseClaimAsync(ObjectId projectId, ObjectId owner)
    {
        using var releaseCts = new CancellationTokenSource(ClaimReleaseTimeout);
        try
        {
            var result = await Projects.UpdateOneAsync(
                new BsonDocument { ["_id"] = projectId, ["cip"] = true, ["clear_owner"] = owner },
                new BsonDocument
                {
                    ["$set"] = new BsonDocument("cip", false),
                    ["$unset"] = new BsonDocument("clear_owner", "")
                },
                cancellationToken: releaseCts.Token);
            if (result.ModifiedCount == 1) return true;
            logger?.LogWarning(
                "Project clear claim release did not match the owned claim; project {ProjectId} may have been superseded.",
                projectId);
            return false;
        }
        catch (Exception ex)
        {
            // Sanitized: no owner token, request input, or exception message.
            logger?.LogWarning(
                "Project clear claim release failed; project {ProjectId} remains retryable. ErrorType={ErrorType}",
                projectId, ex.GetType().Name);
            return false;
        }
    }

    private async Task TryAuditRetryableFailureAsync(BsonDocument actor, BsonDocument project,
        BsonDocument before, string requestId)
    {
        using var auditCts = new CancellationTokenSource(ClaimReleaseTimeout);
        try
        {
            await audit.RecordProjectAsync(actor, "project_clear_retryable_failure", project, null, before,
                ProjectStateDoc(project), requestId, auditCts.Token);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Project clear retryable-failure audit failed; project {ProjectId}. ErrorType={ErrorType}",
                project["_id"], ex.GetType().Name);
        }
    }

    private async Task TryAuditRecoveryRequiredAsync(BsonDocument actor, BsonDocument project,
        BsonDocument before, string requestId)
    {
        using var auditCts = new CancellationTokenSource(ClaimReleaseTimeout);
        try
        {
            await audit.RecordProjectAsync(actor, "project_clear_recovery_required", project, null, before,
                ProjectStateDoc(project), requestId, auditCts.Token);
        }
        catch (Exception ex)
        {
            // The claim row itself is the durable recovery marker; this audit is
            // best-effort and must never hide a failed release.
            logger?.LogWarning(
                "Project clear recovery-required audit failed; project {ProjectId}. ErrorType={ErrorType}",
                project["_id"], ex.GetType().Name);
        }
    }

    public async Task<(BsonDocument Project, BsonDocument Member, bool Changed)> TransferOwnerAsync(
        BsonDocument actor, BsonDocument project, JsonElementLite? payload, string requestId, CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        var relation = await members.ActiveTeamMemberAsync(actor["_id"].AsObjectId, project["t"].AsObjectId, ct);
        var teamManager = relation is not null && relation.Text("base_tag") is "creator" or "admin";
        if (!(snapshot.IsOwner || snapshot.Has("MANAGE_MEMBERS") || teamManager))
            throw ApiError.Forbidden();
        var newOwnerId = payload?.Get("new_owner_user_id")?.AsString;
        if (string.IsNullOrEmpty(newOwnerId) || !ObjectId.TryParse(newOwnerId, out var newOwnerKey))
            throw IdentityErrors.InvalidRequest("new_owner_user_id is required");
        var expectedRaw = payload?.Get("expected_owner_user_id")?.AsString;
        var expectedVersion = payload?.Get("expected_version")?.AsInt32;
        var hasVersion = payload?.Get("expected_version") is not null;
        if (hasVersion && expectedVersion is null) throw IdentityErrors.InvalidRequest();
        var oldOwnerKey = project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ou ? ou.AsObjectId : (ObjectId?)null;
        if (expectedRaw is not null && (oldOwnerKey is null || expectedRaw != oldOwnerKey.Value.ToString()))
            throw IdentityErrors.OwnerConflict();
        if (expectedVersion is not null && expectedVersion.Value != project.Integer("ouv"))
            throw IdentityErrors.OwnerConflict();
        var newOwner = await store.Collection("user").Find(new BsonDocument("_id", newOwnerKey)).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.UserMissing();
        if (await members.ActiveTeamMemberAsync(newOwnerKey, project["t"].AsObjectId, ct) is null)
            throw IdentityErrors.OwnerConflict();
        var newMember = await members.ForUserAsync(project, newOwnerKey, ct);
        if (newMember is null || newMember.Text("status") != "active") throw IdentityErrors.OwnerConflict();
        if (oldOwnerKey is not null && oldOwnerKey.Value == newOwnerKey)
            return (project, newMember, false);

        // Python: ProjectMember.objects(project, user=project.owner_user).first()
        // (no status filter here).
        var oldMember = oldOwnerKey is null ? null : await store.Collection("project_member")
            .Find(new BsonDocument { ["project"] = project["_id"], ["user"] = oldOwnerKey.Value }).FirstOrDefaultAsync(ct);
        if (oldMember is null || oldMember.Text("status") != "active"
            || !oldMember.GetValue("tags", new BsonArray()).AsBsonArray.Any(v => v.IsString && v.AsString == "creator"))
            throw IdentityErrors.OwnerConflict();
        var keepOldAdmin = payload?.Get("keep_old_owner_admin")?.IsTrue == true;
        var oldTags = oldMember.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToHashSet(StringComparer.Ordinal);
        oldTags.Remove("creator");
        if (keepOldAdmin) oldTags.Add("admin");
        var newTags = newMember.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToHashSet(StringComparer.Ordinal);
        newTags.Add("creator");

        var oldSnapshot = new BsonDocument
        {
            ["tags"] = oldMember["tags"].AsBsonArray.DeepClone(),
            ["version"] = oldMember.Integer("version"),
            ["edit_time"] = oldMember["edit_time"],
            ["removed_time"] = oldMember.GetValue("removed_time", BsonNull.Value)
        };
        var newSnapshot = new BsonDocument
        {
            ["tags"] = newMember["tags"].AsBsonArray.DeepClone(),
            ["version"] = newMember.Integer("version"),
            ["edit_time"] = newMember["edit_time"],
            ["removed_time"] = newMember.GetValue("removed_time", BsonNull.Value)
        };
        var oldExpected = (int)oldMember.Integer("version");
        var newExpected = (int)newMember.Integer("version");
        var oldSaved = false;
        var newSaved = false;
        var memberDetail = (BsonDocument m) => new BsonDocument
        {
            ["id"] = m["_id"].ToString(),
            ["user_id"] = m.GetValue("user", BsonNull.Value) is { IsObjectId: true } u ? u.ToString() : BsonNull.Value,
            ["tags"] = m["tags"].AsBsonArray.DeepClone(),
            ["status"] = m.Text("status"),
            ["version"] = m.Integer("version")
        };
        var before = new BsonDocument
        {
            ["project"] = ProjectStateDoc(project),
            ["old_member"] = memberDetail(oldMember),
            ["new_member"] = memberDetail(newMember)
        };
        try
        {
            var updatedOld = await SaveVersionAsync(oldMember,
                new BsonDocument { ["tags"] = new BsonArray(oldTags.OrderBy(t => t, StringComparer.Ordinal).Select(t => (BsonValue)t).ToArray()) },
                oldExpected, ct);
            if (updatedOld is null) throw new InvalidOperationException("old member changed");
            oldSaved = true;
            var updatedNew = await SaveVersionAsync(newMember,
                new BsonDocument { ["tags"] = new BsonArray(newTags.OrderBy(t => t, StringComparer.Ordinal).Select(t => (BsonValue)t).ToArray()) },
                newExpected, ct);
            if (updatedNew is null) throw new InvalidOperationException("new member changed");
            newSaved = true;
            var filter = new BsonDocument
            {
                ["_id"] = project["_id"],
                ["ou"] = oldOwnerKey is not null ? oldOwnerKey.Value : BsonNull.Value,
                ["ouv"] = project.Integer("ouv"),
                ["cip"] = new BsonDocument("$ne", true)
            };
            var updated = await Projects.FindOneAndUpdateAsync(filter,
                new BsonDocument
                {
                    ["$set"] = new BsonDocument("ou", newOwnerKey),
                    ["$inc"] = new BsonDocument { ["ouv"] = 1, ["stv"] = 1 }
                },
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken: ct);
            if (updated is null) throw new InvalidOperationException("project owner changed");
            project = updated;
        }
        catch
        {
            if (newSaved)
                await RestoreVersionAsync(newMember["_id"].AsObjectId, newSnapshot, newExpected, ct);
            if (oldSaved)
                await RestoreVersionAsync(oldMember["_id"].AsObjectId, oldSnapshot, oldExpected, ct);
            throw IdentityErrors.OwnerConflict();
        }
        var refetched = await members.ForUserAsync(project, newOwnerKey, ct) ?? newMember;
        var after = new BsonDocument
        {
            ["project"] = ProjectStateDoc(project),
            ["old_member"] = memberDetail(await store.Collection("project_member")
                .Find(new BsonDocument("_id", oldMember["_id"])).FirstOrDefaultAsync(ct) ?? oldMember),
            ["new_member"] = memberDetail(refetched)
        };
        await audit.RecordProjectAsync(actor, "project_owner_transfer", project, refetched, before, after, requestId, ct);
        return (project, refetched, true);
    }

    private async Task<BsonDocument?> SaveVersionAsync(BsonDocument member, BsonDocument changes, int expected, CancellationToken ct)
    {
        var set = new BsonDocument(changes) { ["edit_time"] = Now() };
        return await store.Collection("project_member").FindOneAndUpdateAsync(
            IdentityErrors.VersionFilter(member, expected),
            new BsonDocument { ["$set"] = set, ["$inc"] = new BsonDocument("version", 1) },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken: ct);
    }

    private async Task RestoreVersionAsync(ObjectId memberId, BsonDocument snapshot, int expectedVersion, CancellationToken ct)
    {
        // Compensating restore: the save already bumped version to expected+1.
        var filter = new BsonDocument { ["_id"] = memberId, ["version"] = expectedVersion + 1 };
        var update = new BsonDocument("$set", new BsonDocument(snapshot));
        await store.Collection("project_member").UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}

/// <summary>Minimal JSON value shim for the lifecycle endpoints.</summary>
public sealed class JsonElementLite
{
    private readonly System.Text.Json.JsonElement _element;
    public JsonElementLite(System.Text.Json.JsonElement element) => _element = element;
    public JsonElementLite? Get(string name) =>
        _element.ValueKind == System.Text.Json.JsonValueKind.Object && _element.TryGetProperty(name, out var value)
            ? new JsonElementLite(value) : null;
    public string? AsString => _element.ValueKind == System.Text.Json.JsonValueKind.String ? _element.GetString() : null;
    public int? AsInt32 => _element.ValueKind == System.Text.Json.JsonValueKind.Number
        && _element.TryGetInt32(out var number) ? number : null;
    public bool IsInt32 => _element.ValueKind == System.Text.Json.JsonValueKind.Number && _element.TryGetInt32(out _);
    public bool IsTrue => _element.ValueKind == System.Text.Json.JsonValueKind.True;
    public System.Text.Json.JsonElement Element => _element;
}
