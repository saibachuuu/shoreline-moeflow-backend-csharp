using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed partial class LabelSnapshotService
{
    private const int MutationBatch = 1000;

    private async Task<Dictionary<string, bool>> CapabilitiesCoreAsync(
        BsonDocument actor,
        BsonDocument project,
        CancellationToken ct)
    {
        var snapshot = await permissions.ProjectSnapshotAsync(actor, project, ct);
        var access = IdentityPermissions.CanProject(snapshot, project, "project:ACCESS");
        var manager = access && snapshot.Has("project:MANAGE_MEMBERS");
        var proofread = access && IdentityPermissions.CanProject(snapshot, project, "project:PROOFREAD_TRA");
        var output = access && IdentityPermissions.CanProject(snapshot, project, "project:OUTPUT_TRA");
        var check = access && IdentityPermissions.CanProject(snapshot, project, "project:CHECK_TRA");
        var add = access && IdentityPermissions.CanProject(snapshot, project, "project:ADD_TRA");
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["create"] = access && (manager || proofread || output),
            ["restore"] = manager,
            ["clear_proofread"] = manager || proofread,
            ["merge"] = manager || (proofread && check && add)
        };
    }

    public async Task<Dictionary<string, bool>> CapabilitiesAsync(
        BsonDocument actor, BsonDocument project, CancellationToken ct = default) =>
        await CapabilitiesCoreAsync(actor, project, ct);

    public async Task<Dictionary<string, bool>> CapabilitiesForProjectAsync(
        BsonDocument actor, ObjectId projectId, CancellationToken ct = default)
    {
        var project = await RequireProjectAsync(projectId, ct);
        return await CapabilitiesCoreAsync(actor, project, ct);
    }

    /// <summary>Endpoint-side permission precheck with Python's action order.</summary>
    public async Task RequireForProjectAsync(
        BsonDocument actor, ObjectId projectId, string action, CancellationToken ct = default)
    {
        var project = await RequireProjectAsync(projectId, ct);
        await RequireCapabilityAsync(actor, project, action, ct);
    }

    private async Task RequireCapabilityAsync(
        BsonDocument actor,
        BsonDocument project,
        string action,
        CancellationToken ct)
    {
        var capabilities = await CapabilitiesCoreAsync(actor, project, ct);
        if (!capabilities.TryGetValue(action, out var allowed) || !allowed)
            throw ApiError.Forbidden();
    }

    public sealed record LabelState(
        Dictionary<string, bool> Capabilities,
        bool HasSourceContent,
        Dictionary<string, int> Counts,
        bool RecoveryRequired)
    {
        public Dictionary<string, object?> ToApi() => new()
        {
            ["capabilities"] = Capabilities,
            ["has_source_content"] = HasSourceContent,
            ["counts"] = Counts,
            ["recovery_required"] = RecoveryRequired
        };
    }

    public async Task<LabelState> GetLabelStateAsync(
        BsonDocument actor,
        ObjectId projectId,
        CancellationToken ct = default)
    {
        var project = await RequireProjectAsync(projectId, ct);
        await permissions.RequireProjectAsync(actor, project, "project:ACCESS", ct);
        var data = await LoadLabelDataAsync(projectId, ct);
        return new LabelState(
            await CapabilitiesCoreAsync(actor, project, ct),
            data["sources"].Any(HasNonEmptyContent),
            data.Counts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            ProjectLabelLock.HasRecovery(project));
    }

    public async Task<ObjectId> RestoreSnapshotAsync(
        BsonDocument actor,
        ObjectId projectId,
        string snapshotId,
        CancellationToken ct = default)
    {
        await using var session = await EnterAsync(projectId, recovery: false, expectedRevision: null, ct);
        await RequireCapabilityAsync(actor, session.Project, "restore", ct);
        var data = await LoadSnapshotAsync(projectId, snapshotId, ct);
        // Corruption or a replaced file is rejected before the journal is
        // written or any live row is deleted.
        await ValidateRestoreAsync(projectId, data, ct);
        return await ProtectedOperationAsync(session, actor, "restore",
            () => ReplaceLabelsAsync(projectId, data, ct), ct);
    }

    public async Task<ObjectId> ClearLabelsAsync(
        BsonDocument actor,
        ObjectId projectId,
        string? field,
        bool deleteLabels = false,
        bool clearTips = false,
        CancellationToken ct = default)
    {
        if (field is not ("source" or "translation" or "proofread"))
            throw OperationError("请选择有效栏位");
        if (deleteLabels && field == "proofread")
            throw OperationError("清空校对不能删除标签");

        await using var session = await EnterAsync(projectId, recovery: false, expectedRevision: null, ct);
        await RequireCapabilityAsync(actor, session.Project,
            field == "proofread" ? "clear_proofread" : "restore", ct);

        var data = await LoadLabelDataAsync(projectId, ct);
        if (deleteLabels && field == "translation" && data["sources"].Any(HasNonEmptyContent))
            throw OperationError("原文字段有内容，不能完全删除标签");

        var sourceIds = data["sources"].Select(IdOf).ToList();
        var textFileIds = data["files"]
            .Where(row => row.Integer("t", 0) == 3)
            .Select(IdOf)
            .ToList();

        return await ProtectedOperationAsync(session, actor, "clear", async () =>
        {
            if (field is "source" or "translation")
            {
                await DeleteByReferenceAsync(Translations, "o", sourceIds, ct);
            }
            else
            {
                await UpdateInBatchesAsync(sourceIds, batch =>
                    Translations.UpdateManyAsync(
                        new BsonDocument("o", new BsonDocument("$in", new BsonArray(batch))),
                        new BsonDocument
                        {
                            ["$set"] = new BsonDocument("p", ""),
                            ["$unset"] = new BsonDocument("pr", "")
                        }, cancellationToken: ct), ct);
            }

            if (deleteLabels || clearTips)
                await DeleteByReferenceAsync(Tips, "o", sourceIds, ct);

            if (deleteLabels)
            {
                await DeleteByIdsAsync(Sources, sourceIds, ct);
            }
            else if (field == "source")
            {
                var now = Now();
                // Python only blanks sources belonging to text files. Image
                // sources keep their original content and blank flag.
                await UpdateInBatchesAsync(textFileIds, batch =>
                    Sources.UpdateManyAsync(
                        new BsonDocument("f", new BsonDocument("$in", new BsonArray(batch))),
                        new BsonDocument("$set", new BsonDocument
                        {
                            ["c"] = "",
                            ["pt"] = new BsonArray(),
                            ["e"] = now,
                            ["b"] = true
                        }), cancellationToken: ct), ct);
            }
        }, ct);
    }

    public async Task RecoverAsync(
        BsonDocument actor,
        ObjectId projectId,
        CancellationToken ct = default)
    {
        await using var session = await EnterAsync(projectId, recovery: true, expectedRevision: null, ct);
        await RequireCapabilityAsync(actor, session.Project, "restore", ct);
        if (!ProjectLabelLock.HasRecovery(session.Project))
            throw OperationError("项目没有待恢复操作");

        var marker = session.Project.GetValue("label_recovery");
        if (!marker.IsObjectId) throw OperationError("项目没有待恢复操作");
        var data = await LoadSnapshotAsync(projectId, marker.AsObjectId.ToString(), ct);
        await ValidateRestoreAsync(projectId, data, ct);
        await ReplaceLabelsAsync(projectId, data, ct);
        await RebuildProjectStatisticsAsync(projectId, ct);
        await AdvanceRevisionAsync(projectId, ct);
    }

    // ------------------------------------------------------------------
    // Journaled destructive operation
    // ------------------------------------------------------------------

    private async Task<ObjectId> ProtectedOperationAsync(
        LabelSession session,
        BsonDocument actor,
        string reason,
        Func<Task> body,
        CancellationToken ct)
    {
        var protection = await CreateSnapshotCoreAsync(session, actor, "", reason, ct);
        await Projects.UpdateOneAsync(new BsonDocument("_id", session.ProjectId),
            new BsonDocument("$set", new BsonDocument("label_recovery", protection)),
            cancellationToken: ct);

        try
        {
            await body();
            await FireAsync("before_rebuild", ct);
            await RebuildProjectStatisticsAsync(session.ProjectId, ct);
            await AdvanceRevisionAsync(session.ProjectId, ct);
            return protection;
        }
        catch
        {
            // The marker deliberately stays set until both the data restore and
            // the revision publish succeed. A failed rollback requires the
            // explicit recovery endpoint.
            var data = await LoadSnapshotAsync(session.ProjectId, protection.ToString(), ct);
            await ReplaceLabelsAsync(session.ProjectId, data, ct);
            await FireAsync("rollback_before_rebuild", ct);
            await RebuildProjectStatisticsAsync(session.ProjectId, ct);
            await AdvanceRevisionAsync(session.ProjectId, ct);
            throw;
        }
    }

    private async Task ReplaceLabelsAsync(ObjectId projectId, LabelData data, CancellationToken ct)
    {
        var fileIds = await Files.Find(new BsonDocument("p", projectId))
            .Project(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        var fileObjectIds = fileIds.Select(IdOf).ToList();
        var sourceIds = fileObjectIds.Count == 0
            ? new List<ObjectId>()
            : await FindIdsInAsync(Sources, "f", fileObjectIds, ct);

        await DeleteByReferenceAsync(Translations, "o", sourceIds, ct);
        await DeleteByReferenceAsync(Tips, "o", sourceIds, ct);
        await DeleteByIdsAsync(Sources, sourceIds, ct);
        await InsertInBatchesAsync(Sources, data["sources"], ct);
        await InsertInBatchesAsync(Translations, data["translations"], ct);
        await InsertInBatchesAsync(Tips, data["tips"], ct);
    }

    private async Task AdvanceRevisionAsync(ObjectId projectId, CancellationToken ct)
    {
        var project = await Projects.Find(new BsonDocument("_id", projectId)).FirstOrDefaultAsync(ct)
                      ?? throw ProjectNotExist();
        var epoch = project.Integer("label_revision", 0) + 1;

        var files = await Files.Find(new BsonDocument("p", projectId))
            .Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        var fileIds = files.Select(IdOf).ToList();
        var sourceIds = fileIds.Count == 0
            ? new List<ObjectId>()
            : await FindIdsInAsync(Sources, "f", fileIds, ct);

        var stamp = new BsonDocument("$set", new BsonDocument("label_epoch", epoch));
        await Files.UpdateManyAsync(new BsonDocument("p", projectId), stamp, cancellationToken: ct);
        await Targets.UpdateManyAsync(new BsonDocument("t", projectId), stamp, cancellationToken: ct);
        await UpdateInBatchesAsync(fileIds, batch =>
            FileTargetCaches.UpdateManyAsync(
                new BsonDocument("f", new BsonDocument("$in", new BsonArray(batch))),
                stamp, cancellationToken: ct), ct);
        await UpdateInBatchesAsync(fileIds, batch =>
            Sources.UpdateManyAsync(
                new BsonDocument("f", new BsonDocument("$in", new BsonArray(batch))),
                stamp, cancellationToken: ct), ct);
        await UpdateInBatchesAsync(sourceIds, batch =>
            Translations.UpdateManyAsync(
                new BsonDocument("o", new BsonDocument("$in", new BsonArray(batch))),
                stamp, cancellationToken: ct), ct);
        await UpdateInBatchesAsync(sourceIds, batch =>
            Tips.UpdateManyAsync(
                new BsonDocument("o", new BsonDocument("$in", new BsonArray(batch))),
                stamp, cancellationToken: ct), ct);

        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument
            {
                ["$unset"] = new BsonDocument("label_recovery", ""),
                ["$set"] = new BsonDocument
                {
                    ["label_revision"] = epoch,
                    // Project inherits GroupMixin.edit_time (db_field m_e);
                    // edit_time is only the API field used by project views.
                    ["m_e"] = Now()
                }
            }, cancellationToken: ct);
    }

    private static bool HasNonEmptyContent(BsonDocument row)
    {
        if (!row.TryGetValue("c", out var value)) return false;
        if (value.IsString) return value.AsString.Length > 0;
        return !value.IsBsonNull;
    }

    private static async Task DeleteByIdsAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<ObjectId> ids,
        CancellationToken ct)
    {
        await UpdateInBatchesAsync(ids, batch =>
            collection.DeleteManyAsync(
                new BsonDocument("_id", new BsonDocument("$in", new BsonArray(batch))),
                cancellationToken: ct), ct);
    }

    private static async Task DeleteByReferenceAsync(
        IMongoCollection<BsonDocument> collection,
        string field,
        IReadOnlyList<ObjectId> ids,
        CancellationToken ct)
    {
        await UpdateInBatchesAsync(ids, batch =>
            collection.DeleteManyAsync(
                new BsonDocument(field, new BsonDocument("$in", new BsonArray(batch))),
                cancellationToken: ct), ct);
    }

    private static async Task InsertInBatchesAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<BsonDocument> rows,
        CancellationToken ct)
    {
        for (var offset = 0; offset < rows.Count; offset += MutationBatch)
        {
            var batch = rows.Skip(offset).Take(MutationBatch).ToList();
            if (batch.Count > 0) await collection.InsertManyAsync(batch, cancellationToken: ct);
        }
    }

    private static async Task UpdateInBatchesAsync(
        IReadOnlyList<ObjectId> ids,
        Func<IReadOnlyList<ObjectId>, Task> action,
        CancellationToken ct)
    {
        for (var offset = 0; offset < ids.Count; offset += MutationBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(offset).Take(MutationBatch).ToList();
            if (batch.Count > 0) await action(batch);
        }
    }
}
