using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Legacy DELETE /v1/teams/{team_id}. Python Team.clear() first clears every
/// project (physical files/output objects and content rows), then lets
/// MongoEngine apply the project/team CASCADE/PULL delete rules.
/// </summary>
public sealed class TeamDissolutionService(
    MongoStore store,
    IdentityPermissions permissions,
    TeamMemberService members,
    TeamLifecycleLock lifecycle,
    ProjectLabelLock labels,
    LabelRequestContext labelContext,
    ObjectStorage storage,
    IConfiguration configuration,
    ILogger<TeamDissolutionService> logger,
    ProjectStorageRouter? router = null)
{
    private const int ProjectBatchSize = 100;
    private const int TermBatchSize = 200;

    public async Task<TeamDissolutionResult> DeleteAsync(
        BsonDocument actor,
        string teamId,
        CancellationToken ct = default)
    {
        var team = await members.GetTeamAsync(teamId, ct);
        var id = team["_id"].AsObjectId;
        await RequireDeleteAsync(actor, team, ct);

        await using var lease = await lifecycle.AcquireAsync(id, ct);
        try
        {
            // The team read by AcquireAsync is after the unique lock row is
            // inserted. Re-check the actor and the working-project invariant
            // under that ownership before writing the durable marker.
            await RequireDeleteAsync(actor, lease.Team, ct);
            await RequireNoWorkingProjectsAsync(id, ct);

            await lifecycle.MarkDeletingAsync(lease, ct);
            var owned = await lifecycle.ReadOwnedTeamAsync(lease, ct);
            await RequireDeleteAsync(actor, owned, ct);
            await RequireNoWorkingProjectsAsync(id, ct);

            await DeleteProjectsAsync(lease, actor, id, ct);

            await lifecycle.ReadOwnedTeamAsync(lease, ct);
            await DeleteTeamChildrenAsync(id, ct);

            // Belt and braces against a concurrent writer that is not yet wired
            // to EnsureWritableAsync: never delete the team while any project
            // row still points at it.
            if (await CountProjectsAsync(id, ct) != 0)
                throw TeamLifecycleErrors.DeleteIncomplete();

            var deleted = await store.Collection("team").DeleteOneAsync(
                new BsonDocument
                {
                    ["_id"] = id,
                    ["lc_phase"] = "deleting",
                    ["lc_owner"] = lease.Owner
                }, ct);
            if (deleted.DeletedCount != 1)
                throw TeamLifecycleErrors.Busy("团队解散声明已失效，请重试");

            return new TeamDissolutionResult("解散成功");
        }
        catch (Exception error)
        {
            await lifecycle.MarkFailureAsync(lease, error.GetType().Name);
            if (error is ApiError or OperationCanceledException) throw;
            logger.LogWarning(
                "Team dissolution failed for team {TeamId} owner {Owner} ({ErrorType})",
                id,
                lease.Owner,
                error.GetType().Name);
            throw TeamLifecycleErrors.DeleteIncomplete();
        }
    }

    private async Task RequireDeleteAsync(
        BsonDocument actor,
        BsonDocument team,
        CancellationToken ct)
    {
        var snapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
        if (!snapshot.Has("team:DELETE")) throw ApiError.Forbidden();
    }

    private static ApiError WorkingProjectsError() =>
        new("NoPermissionError", 103,
            "此团队含有未完结的项目，不能解散（请先完结或者转移项目）");

    private async Task RequireNoWorkingProjectsAsync(ObjectId teamId, CancellationToken ct)
    {
        if (await CountWorkingProjectsAsync(teamId, ct) > 0)
            throw WorkingProjectsError();
    }

    private Task<long> CountWorkingProjectsAsync(ObjectId teamId, CancellationToken ct) =>
        store.Collection("project").CountDocumentsAsync(
            new BsonDocument
            {
                ["t"] = teamId,
                ["$or"] = new BsonArray
                {
                    new BsonDocument("st", 0),
                    new BsonDocument("st", new BsonDocument("$exists", false))
                }
            }, cancellationToken: ct);

    private Task<long> CountProjectsAsync(ObjectId teamId, CancellationToken ct) =>
        store.Collection("project").CountDocumentsAsync(
            new BsonDocument("t", teamId), cancellationToken: ct);

    private async Task DeleteProjectsAsync(
        TeamLifecycleLease lease,
        BsonDocument actor,
        ObjectId teamId,
        CancellationToken ct)
    {
        BsonValue? lastId = null;
        while (true)
        {
            var filter = new BsonDocument("t", teamId);
            if (lastId is not null)
                filter["_id"] = new BsonDocument("$gt", lastId!);

            var batch = await store.Collection("project")
                .Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(ProjectBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var project in batch)
            {
                await DeleteProjectAsync(lease, project, ct);
                lastId = project["_id"];
            }

            // Do not hold any project label lease between batches. Re-read the
            // team marker and permission after each batch so a stale owner
            // cannot continue an irreversible delete.
            var owned = await lifecycle.ReadOwnedTeamAsync(lease, ct);
            await RequireDeleteAsync(actor, owned, ct);
            await RequireNoWorkingProjectsAsync(teamId, ct);
        }
    }

    private async Task DeleteProjectAsync(
        TeamLifecycleLease lease,
        BsonDocument listedProject,
        CancellationToken ct)
    {
        var projectId = listedProject["_id"].AsObjectId;
        await using var label = await labels.EnterAsync(
            labelContext, projectId, recovery: false, expectedRevision: null, ct);
        var project = label.Project;

        // The label session re-reads the project after its lock is inserted.
        // Never trust the pre-lock list snapshot for status or ownership.
        if (project.Integer("st", 0) == 0)
            throw WorkingProjectsError();
        if (!project.TryGetValue("t", out var teamRef) ||
            teamRef.BsonType != BsonType.ObjectId || teamRef.AsObjectId != lease.TeamId)
            throw TeamLifecycleErrors.Busy("项目归属已改变，请重试");

        if (router is not null)
        {
            await ProjectContentCleanup.ClearProjectContentsAsync(
                store, router, storage, configuration, project, ct);
        }
        else
        {
            await ProjectContentCleanup.ClearProjectContentsAsync(
                store, storage, configuration, project, ct);
        }
        await DeleteProjectTargetsAsync(project, ct);
        await DeleteProjectRowsAsync(projectId, lease.TeamId, ct);
    }

    private async Task DeleteProjectTargetsAsync(BsonDocument project, CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        BsonValue? lastId = null;
        while (true)
        {
            var filter = new BsonDocument("p", projectId);
            if (lastId is not null)
                filter["_id"] = new BsonDocument("$gt", lastId!);

            var batch = await store.Collection("target")
                .Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(ProjectBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var target in batch)
            {
                if (router is not null)
                {
                    await ProjectContentCleanup.DeleteTargetCascadesAsync(
                        store, router, storage, configuration, project, target, ct);
                }
                else
                {
                    await ProjectContentCleanup.DeleteTargetCascadesAsync(
                        store, storage, configuration, project, target, ct);
                }
                lastId = target["_id"];
            }
        }

        // CreateTargetCachesAsync/DeleteTargetCascadesAsync already cover
        // translation/tip/file_target_cache; this is only a defensive row
        // cleanup for a target whose cascade was interrupted before the retry.
        await store.Collection("target").DeleteManyAsync(
            new BsonDocument("p", projectId), ct);
    }

    private async Task DeleteProjectRowsAsync(
        ObjectId projectId,
        ObjectId teamId,
        CancellationToken ct)
    {
        await DeleteGenericRefsAsync("application", "project", projectId, ct);
        await DeleteGenericRefsAsync("invitation", "project", projectId, ct);

        await store.Collection("project_user_relation")
            .DeleteManyAsync(new BsonDocument("g", projectId), ct);
        await store.Collection("project_member")
            .DeleteManyAsync(new BsonDocument("project", projectId), ct);
        await store.Collection("project_role")
            .DeleteManyAsync(new BsonDocument("g", projectId), ct);
        await store.Collection("file")
            .DeleteManyAsync(new BsonDocument("p", projectId), ct);
        await store.Collection("output")
            .DeleteManyAsync(new BsonDocument("p", projectId), ct);
        await store.Collection("target")
            .DeleteManyAsync(new BsonDocument("p", projectId), ct);

        var deleted = await store.Collection("project").DeleteOneAsync(
            new BsonDocument { ["_id"] = projectId, ["t"] = teamId }, ct);
        if (deleted.DeletedCount != 1)
            throw TeamLifecycleErrors.Busy("项目归属已改变，请重试");
    }

    private async Task DeleteTeamChildrenAsync(ObjectId teamId, CancellationToken ct)
    {
        await DeleteTermBanksAsync(teamId, ct);
        await DeleteGenericRefsAsync("application", "team", teamId, ct);
        await DeleteGenericRefsAsync("invitation", "team", teamId, ct);

        await store.Collection("project_set")
            .DeleteManyAsync(new BsonDocument("t", teamId), ct);
        await store.Collection("team_user_relation")
            .DeleteManyAsync(new BsonDocument("g", teamId), ct);
        await store.Collection("team_member")
            .DeleteManyAsync(new BsonDocument("team", teamId), ct);

        // Only grouped roles are CASCADE. A role without g is a system role and
        // must survive team deletion (legacy TeamRole.g CASCADE semantics).
        await store.Collection("team_role")
            .DeleteManyAsync(new BsonDocument("g", teamId), ct);
    }

    private async Task DeleteTermBanksAsync(ObjectId teamId, CancellationToken ct)
    {
        var banks = store.Collection("term_bank");
        BsonValue? lastId = null;
        while (true)
        {
            var filter = new BsonDocument("t", teamId);
            if (lastId is not null)
                filter["_id"] = new BsonDocument("$gt", lastId!);

            var batch = await banks.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(ProjectBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var bank in batch)
            {
                var bankId = bank["_id"].AsObjectId;
                await DeleteTermsForBankAsync(bankId, ct);

                // Project.term_banks is ReferenceField(TermBank, PULL). The
                // pull is global, not team-scoped, so a foreign project that
                // still points at this bank must not retain a dangling ref.
                await store.Collection("project").UpdateManyAsync(
                    new BsonDocument("tb", bankId),
                    new BsonDocument("$pull", new BsonDocument("tb", bankId)),
                    cancellationToken: ct);

                await banks.DeleteOneAsync(
                    new BsonDocument { ["_id"] = bankId, ["t"] = teamId }, ct);
                lastId = bank["_id"];
            }
        }
    }

    private async Task DeleteTermsForBankAsync(ObjectId bankId, CancellationToken ct)
    {
        var terms = store.Collection("term");
        BsonValue? lastId = null;
        while (true)
        {
            var filter = new BsonDocument("tb", bankId);
            if (lastId is not null)
                filter["_id"] = new BsonDocument("$gt", lastId!);

            var batch = await terms.Find(filter)
                .Sort(new BsonDocument("_id", 1))
                .Limit(TermBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            var ids = batch.Select(row => row["_id"].AsObjectId).ToArray();
            var inIds = new BsonArray(ids.Select(id => (BsonValue)new BsonObjectId(id)));
            await store.Collection("source").UpdateManyAsync(
                new BsonDocument("pt", new BsonDocument("$in", inIds)),
                new BsonDocument("$pull", new BsonDocument("pt", new BsonDocument("$in", inIds))),
                cancellationToken: ct);
            await terms.DeleteManyAsync(
                new BsonDocument
                {
                    ["tb"] = bankId,
                    ["_id"] = new BsonDocument("$in", inIds)
                }, ct);
            lastId = batch[^1]["_id"];
        }
    }

    /// <summary>
    /// Delete Application/Invitation rows whose GenericReferenceField points
    /// at the given collection/id. Legacy stores
    /// <c>{"_cls": "...", "_ref": {"$ref": "project", "$id": ...}}</c>; the
    /// nested dot query also handles a bare DBRef and field-order variation.
    /// </summary>
    private async Task DeleteGenericRefsAsync(
        string collection,
        string referenceCollection,
        ObjectId id,
        CancellationToken ct)
    {
        var nested = new BsonDocument
        {
            ["g._ref.$id"] = id,
            ["g._ref.$ref"] = referenceCollection
        };
        var camelNested = new BsonDocument
        {
            ["g._ref.$id"] = id,
            ["g._ref.$ref"] = char.ToUpperInvariant(referenceCollection[0]) + referenceCollection[1..]
        };
        var bare = new BsonDocument("g._ref", new BsonDocument
        {
            ["$ref"] = referenceCollection,
            ["$id"] = id
        });
        var bareCamel = new BsonDocument("g._ref", new BsonDocument
        {
            ["$ref"] = char.ToUpperInvariant(referenceCollection[0]) + referenceCollection[1..],
            ["$id"] = id
        });

        await store.Collection(collection).DeleteManyAsync(
            new BsonDocument("$or", new BsonArray { nested, camelNested, bare, bareCamel }), ct);
    }
}

public sealed record TeamDissolutionResult(string Message);

