using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed partial class FileService
{
    public async Task RenameAsync(
        BsonDocument file,
        BsonDocument project,
        string name,
        CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        await using var labelSession = await EnterLabelAsync(projectId, ct);
        using var _ = await _gate.EnterAsync(projectId, ct);
        file = await GetFileAsync(file["_id"].AsObjectId.ToString(), ct);
        EnsureActivated(file);
        labelSession.AssertReferenceEpoch(file);

        var folder = file.Integer("t") == TypeFolder;
        var parsed = ParseFilename(name, folder);
        if (parsed.Type != file.Integer("t"))
        {
            throw new ApiError("SuffixNotInFileTypeError", 8003, "后缀名必须属于原文件的文件类型");
        }

        var parentValue = file.GetValue("f", BsonNull.Value);
        var duplicateFilter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                              & Builders<BsonDocument>.Filter.Eq("f", parentValue)
                              & Builders<BsonDocument>.Filter.Eq("ac", true)
                              & Builders<BsonDocument>.Filter.Regex(
                                  "n", new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i"));
        var duplicate = await _files.Find(duplicateFilter).FirstOrDefaultAsync(ct);
        if (duplicate is not null && duplicate["_id"].AsObjectId != file["_id"].AsObjectId)
        {
            throw new ApiError("FilenameDuplicateError", 4007, "文件名重复");
        }

        if (duplicate is not null && duplicate.Text("n") == name)
        {
            return;
        }

        var oldPrefix = file.Text("dn") + file.Text("sn");
        var newPrefix = file.Text("dn") + parsed.SortName;
        await _files.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", file["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("n", name).Set("sn", parsed.SortName),
            cancellationToken: ct);

        if (folder)
        {
            await RewriteDescendantDirectoriesAsync(
                file["_id"].AsObjectId, oldPrefix, newPrefix, ct);
        }
    }

    public async Task MoveAsync(
        BsonDocument file,
        BsonDocument project,
        string? parentId,
        CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        await using var labelSession = await EnterLabelAsync(projectId, ct);
        using var _ = await _gate.EnterAsync(projectId, ct);
        file = await GetFileAsync(file["_id"].AsObjectId.ToString(), ct);
        EnsureActivated(file);
        labelSession.AssertReferenceEpoch(file);

        var target = await ResolveFolderAsync(project, parentId, ct);
        if (target is not null)
        {
            labelSession.AssertReferenceEpoch(target);
        }
        var fileId = file["_id"].AsObjectId;
        if (target is not null && target["_id"].AsObjectId == fileId)
        {
            throw new ApiError("FileParentIsSelfError", 4012, "目标文件夹不能是自己本身");
        }

        if (target is not null)
        {
            var inside = await _files.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("_id", target["_id"].AsObjectId)
                & Builders<BsonDocument>.Filter.Eq("a", fileId),
                cancellationToken: ct);
            if (inside > 0)
            {
                throw new ApiError("FileParentIsSubFolderError", 4014, "不能移动到自己的子文件夹");
            }
        }

        var oldParentValue = file.GetValue("f", BsonNull.Value);
        if (SameParent(oldParentValue, target))
        {
            throw new ApiError("FileParentIsSameError", 4013, "目标文件夹与原父级文件夹相同");
        }

        var duplicate = await FindByNameAsync(projectId, target, file.Text("n"), activated: true, ct);
        if (duplicate is not null && duplicate["_id"].AsObjectId != fileId)
        {
            throw new ApiError("FilenameDuplicateError", 4007, "文件名重复");
        }

        var oldAncestors = AncestorIds(file);
        var oldDir = file.Text("dn");
        var newAncestors = AncestorsOf(target);
        var newDir = target is null ? "" : target.Text("dn") + target.Text("sn") + "/";
        var manualOrder = await LastManualOrderAsync(projectId, target, file.Integer("t"), ct);
        BsonValue newManual = manualOrder.HasValue ? new BsonInt32(checked((int)(manualOrder.Value + 1))) : BsonNull.Value;

        await _files.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", fileId),
            Builders<BsonDocument>.Update
                .Set("f", ParentValue(target))
                .Set("a", newAncestors)
                .Set("dn", newDir)
                .Set("mo", newManual),
            cancellationToken: ct);

        // Deactivated revisions of the same logical file follow the move.
        var revisionFilter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                             & Builders<BsonDocument>.Filter.Eq("f", oldParentValue)
                             & Builders<BsonDocument>.Filter.Eq("ac", false)
                             & Builders<BsonDocument>.Filter.Regex(
                                 "n", new BsonRegularExpression("^" + Regex.Escape(file.Text("n")) + "$", "i"))
                             & Builders<BsonDocument>.Filter.Ne("_id", fileId);
        await _files.UpdateManyAsync(
            revisionFilter,
            Builders<BsonDocument>.Update
                .Set("f", ParentValue(target))
                .Set("a", newAncestors)
                .Set("dn", newDir),
            cancellationToken: ct);

        if (file.Integer("t") == TypeFolder)
        {
            var descendants = Builders<BsonDocument>.Filter.Eq("p", projectId)
                              & Builders<BsonDocument>.Filter.Eq("a", fileId);
            if (oldAncestors.Count > 0)
            {
                await _files.UpdateManyAsync(
                    descendants,
                    Builders<BsonDocument>.Update.PullAll("a", oldAncestors.Values),
                    cancellationToken: ct);
            }

            if (newAncestors.Count > 0)
            {
                await _files.UpdateManyAsync(
                    descendants,
                    Builders<BsonDocument>.Update.PushEach("a", newAncestors.Values, position: 0),
                    cancellationToken: ct);
            }

            await RewriteDescendantDirectoriesAsync(fileId, oldDir + file.Text("sn"), newDir + file.Text("sn"), ct);
        }

        var oldParent = await LoadParentAsync(oldParentValue, ct);
        var fileCount = file.Integer("fc");
        var folderCount = file.Integer("fo");
        if (file.Integer("t") == TypeFolder)
        {
            if (target is not null)
            {
                await IncFileChainAsync(target, "fc", fileCount, true, false, false, ct);
                await IncFileChainAsync(target, "fo", folderCount + 1, true, false, false, ct);
            }

            if (oldParent is not null)
            {
                await IncFileChainAsync(oldParent, "fc", -fileCount, true, false, false, ct);
                await IncFileChainAsync(oldParent, "fo", -(folderCount + 1), true, false, false, ct);
            }
        }
        else
        {
            if (target is not null)
            {
                await IncFileChainAsync(target, "fc", 1, true, false, false, ct);
            }

            if (oldParent is not null)
            {
                await IncFileChainAsync(oldParent, "fc", -1, true, false, false, ct);
            }
        }

        await MoveSourceAndSizeCountsAsync(file, target, oldParent, ct);
    }

    private async Task MoveSourceAndSizeCountsAsync(
        BsonDocument file,
        BsonDocument? target,
        BsonDocument? oldParent,
        CancellationToken ct)
    {
        var sourceCount = file.Integer("sc");
        var fileSize = ToLong(file.GetValue("fs", 0L));
        var translated = file.Integer("tsc");
        var checkedCount = file.Integer("csc");

        if (target is not null)
        {
            await IncFileChainAsync(target, "sc", sourceCount, true, false, false, ct);
            await IncFileChainAsync(target, "fs", fileSize, true, false, true, ct);
            await IncTargetChainsAsync(target, translated, checkedCount, false, ct);
        }

        if (oldParent is not null)
        {
            await IncFileChainAsync(oldParent, "sc", -sourceCount, true, false, false, ct);
            await IncFileChainAsync(oldParent, "fs", -fileSize, true, false, true, ct);
            await IncTargetChainsAsync(oldParent, -translated, -checkedCount, false, ct);
        }
    }

    public async Task DeleteAsync(
        BsonDocument file,
        BsonDocument project,
        CancellationToken ct)
    {
        EnsureDefaultStorage(project);
        var projectId = project["_id"].AsObjectId;
        await using var labelSession = await EnterLabelAsync(projectId, ct);
        using var _ = await _gate.EnterAsync(projectId, ct);
        file = await GetFileAsync(file["_id"].AsObjectId.ToString(), ct);
        EnsureActivated(file);
        labelSession.AssertReferenceEpoch(file);

        var fileId = file["_id"].AsObjectId;
        var parentValue = file.GetValue("f", BsonNull.Value);
        var fileSize = ToLong(file.GetValue("fs", 0L));
        var sourceCount = file.Integer("sc");
        var translated = file.Integer("tsc");
        var checkedCount = file.Integer("csc");
        var folder = file.Integer("t") == TypeFolder;

        if (folder)
        {
            var filter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                         & Builders<BsonDocument>.Filter.Eq("a", fileId);
            await DeleteFilesInBatchesAsync(filter, deleteObjects: true, ct);
        }
        else
        {
            var revisionFilter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                                 & Builders<BsonDocument>.Filter.Eq("f", parentValue)
                                 & Builders<BsonDocument>.Filter.Eq("ac", false)
                                 & Builders<BsonDocument>.Filter.Regex(
                                     "n", new BsonRegularExpression("^" + Regex.Escape(file.Text("n")) + "$", "i"))
                                 & Builders<BsonDocument>.Filter.Ne("_id", fileId);
            await DeleteFilesInBatchesAsync(revisionFilter, deleteObjects: true, ct);
        }

        await DeleteStoredObjectsAsync(file.Text("sa"), ct);
        await DeleteRelatedAsync([fileId], ct);
        await _files.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", fileId), ct);

        await IncFileChainAsync(file, "fs", -fileSize, false, true, true, ct);
        if (folder)
        {
            await IncFileChainAsync(file, "fo", -(file.Integer("fo") + 1), false, true, false, ct);
            await IncFileChainAsync(file, "fc", -file.Integer("fc"), false, true, false, ct);
        }
        else
        {
            await IncFileChainAsync(file, "fc", -1, false, true, false, ct);
        }

        await IncTargetChainsAsync(
            file, -translated, -checkedCount, true, ct, includeSelf: false);
        await IncFileChainAsync(file, "sc", -sourceCount, false, true, false, ct);
        await TouchEditTimeAsync(project, file, ct);
    }

    private async Task DeleteFilesInBatchesAsync(
        FilterDefinition<BsonDocument> filter,
        bool deleteObjects,
        CancellationToken ct)
    {
        const int batchSize = 200;
        while (true)
        {
            var batch = await _files.Find(filter)
                .Project(Builders<BsonDocument>.Projection.Include("_id").Include("sa").Include("t"))
                .Limit(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return;
            }

            var ids = new List<ObjectId>(batch.Count);
            foreach (var document in batch)
            {
                ids.Add(document["_id"].AsObjectId);
                if (deleteObjects && document.Integer("t") != TypeFolder)
                {
                    await DeleteStoredObjectsAsync(document.Text("sa"), ct);
                }
            }

            await DeleteRelatedAsync(ids, ct);
            await _files.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("_id", ids),
                cancellationToken: ct);
        }
    }

    private async Task DeleteRelatedAsync(IReadOnlyCollection<ObjectId> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        // Bounded cascade: never build a giant $in from all sources of a file.
        // Repeatedly take 200 source ids, delete their translations/tips/source
        // rows, then continue until the file has no sources left.
        const int sourceBatchSize = 200;
        while (true)
        {
            var sources = await _sourceDocuments.Find(
                    Builders<BsonDocument>.Filter.In("f", fileIds))
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .Limit(sourceBatchSize)
                .ToListAsync(ct);
            if (sources.Count == 0)
            {
                break;
            }

            var ids = sources.Select(source => source["_id"].AsObjectId).ToList();
            await _translationDocuments.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("o", ids), ct);
            await _tipDocuments.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("o", ids), ct);
            await _sourceDocuments.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("_id", ids), ct);
        }

        await _caches.DeleteManyAsync(
            Builders<BsonDocument>.Filter.In("f", fileIds), ct);
    }

    private async Task DeleteStoredObjectsAsync(string saveName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(saveName))
        {
            return;
        }

        foreach (var key in StoredObjectKeys(saveName))
        {
            await _storage.DeleteAsync(key, ct);
        }
    }

    private async Task RewriteDescendantDirectoriesAsync(
        ObjectId folderId,
        string oldPrefix,
        string newPrefix,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("a", folderId);
        using var cursor = await _files.Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("_id").Include("dn"))
            .ToCursorAsync(ct);
        var writes = new List<WriteModel<BsonDocument>>(200);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var document in cursor.Current)
            {
                var dir = document.Text("dn");
                var suffix = dir.StartsWith(oldPrefix, StringComparison.Ordinal)
                    ? dir[oldPrefix.Length..]
                    : dir;
                writes.Add(new UpdateOneModel<BsonDocument>(
                    Builders<BsonDocument>.Filter.Eq("_id", document["_id"].AsObjectId),
                    Builders<BsonDocument>.Update.Set("dn", newPrefix + suffix)));
                if (writes.Count >= 200)
                {
                    await _files.BulkWriteAsync(writes, cancellationToken: ct);
                    writes.Clear();
                }
            }
        }

        if (writes.Count > 0)
        {
            await _files.BulkWriteAsync(writes, cancellationToken: ct);
        }
    }

    private async Task<BsonDocument> CreateRevisionAsync(
        BsonDocument project,
        BsonDocument old,
        long labelRevision,
        CancellationToken ct)
    {
        EnsureActivated(old);
        var projectId = project["_id"].AsObjectId;
        var oldParentValue = old.GetValue("f", BsonNull.Value);
        var revisionCount = await _files.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("p", projectId)
            & Builders<BsonDocument>.Filter.Eq("f", oldParentValue)
            & Builders<BsonDocument>.Filter.Regex(
                "n", new BsonRegularExpression("^" + Regex.Escape(old.Text("n")) + "$", "i")),
            cancellationToken: ct);

        var now = new BsonDateTime(Now.UtcDateTime);
        var revision = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = old.Text("n"),
            ["t"] = old.Integer("t"),
            ["p"] = projectId,
            ["f"] = oldParentValue,
            ["a"] = AncestorIds(old),
            ["mo"] = old.Contains("mo") ? old["mo"] : BsonNull.Value,
            ["sn"] = old.Text("sn"),
            ["dn"] = old.Text("dn"),
            ["sa"] = "",
            ["md"] = "",
            ["fs"] = new BsonInt64(0),
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["fn"] = FileNotExistNotUpload,
            ["th"] = ThumbnailUnknown,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = now,
            ["ov"] = old["_id"].AsObjectId,
            ["v"] = (int)revisionCount + 1,
            ["ac"] = false,
            ["ss"] = 0,
            ["sti"] = 0,
            ["sri"] = 0,
            ["sst"] = 0,
            ["ps"] = 0,
            ["pt"] = new BsonArray(),
            ["pti"] = "",
            ["pst"] = BsonNull.Value,
            ["pe"] = 0,
            ["op"] = 0,
            ["ft"] = 0,
            ["ftt"] = 0,
            ["fts"] = "",
            ["sm"] = 0,
            ["label_epoch"] = new BsonInt64(labelRevision),
        };

        await _files.InsertOneAsync(revision, cancellationToken: ct);
        await CreateTargetCachesAsync(revision["_id"].AsObjectId, projectId, ct);

        // activate_revision: new starts at zero, so parents/project subtract the
        // old activated revision's counters, then upload adds the new bytes.
        await _files.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", old["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("ac", false), cancellationToken: ct);
        await _files.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", revision["_id"].AsObjectId),
            Builders<BsonDocument>.Update.Set("ac", true), cancellationToken: ct);
        revision["ac"] = true;

        await IncFileChainAsync(revision, "sc", -old.Integer("sc"), false, true, false, ct);
        await IncFileChainAsync(revision, "fs", -ToLong(old.GetValue("fs", 0L)), false, true, true, ct);
        await ApplyDerivedBytesAsync(
            project,
            -ToLong(old.GetValue("cover_bytes", 0L)),
            -ToLong(old.GetValue("resample_bytes", 0L)),
            ct);
        await IncTargetChainsAsync(
            revision, -old.Integer("tsc"), -old.Integer("csc"), true, ct, includeSelf: false);
        await TouchEditTimeAsync(project, revision, ct);
        return revision;
    }

    // ------------------------------------------------------------- cache math

    private async Task IncFileChainAsync(
        BsonDocument file,
        string field,
        long delta,
        bool includeSelf,
        bool updateProject,
        bool isLong,
        CancellationToken ct)
    {
        if (delta == 0)
        {
            return;
        }

        var ids = new BsonArray();
        foreach (var ancestor in AncestorIds(file))
        {
            ids.Add(ancestor);
        }

        if (includeSelf)
        {
            ids.Add(file["_id"].AsObjectId);
        }

        if (ids.Count > 0)
        {
            var value = isLong ? (BsonValue)new BsonInt64(delta) : new BsonInt32((int)delta);
            await _files.UpdateManyAsync(
                Builders<BsonDocument>.Filter.In("_id", ids),
                Builders<BsonDocument>.Update.Inc(field, value),
                cancellationToken: ct);
        }

        if (updateProject)
        {
            await IncProjectAsync(file["p"].AsObjectId, field, delta, isLong, ct);
        }
    }

    private async Task IncProjectAsync(
        ObjectId projectId,
        string field,
        long delta,
        bool isLong,
        CancellationToken ct)
    {
        if (delta == 0)
        {
            return;
        }

        var value = isLong ? (BsonValue)new BsonInt64(delta) : new BsonInt32((int)delta);
        await _projectDocuments.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Update.Inc(field, value),
            cancellationToken: ct);
    }

    private async Task IncTargetChainsAsync(
        BsonDocument file,
        long translatedDelta,
        long checkedDelta,
        bool updateProject,
        CancellationToken ct,
        bool includeSelf = true)
    {
        if (translatedDelta == 0 && checkedDelta == 0)
        {
            return;
        }

        var ids = new BsonArray();
        foreach (var ancestor in AncestorIds(file))
        {
            ids.Add(ancestor);
        }

        if (includeSelf)
        {
            ids.Add(file["_id"].AsObjectId);
        }

        var targets = await _targetDocuments.Find(
                Builders<BsonDocument>.Filter.Eq("t", file["p"].AsObjectId))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(ct);

        foreach (var target in targets)
        {
            var targetId = target["_id"].AsObjectId;
            var cacheEditTime = new BsonDateTime(Now.UtcDateTime);
            if (translatedDelta != 0)
            {
                await _caches.UpdateManyAsync(
                    Builders<BsonDocument>.Filter.In("f", ids)
                    & Builders<BsonDocument>.Filter.Eq("t", targetId),
                    Builders<BsonDocument>.Update
                        .Inc("ts", new BsonInt32((int)translatedDelta))
                        .Set("e", cacheEditTime),
                    cancellationToken: ct);
            }

            if (checkedDelta != 0)
            {
                await _caches.UpdateManyAsync(
                    Builders<BsonDocument>.Filter.In("f", ids)
                    & Builders<BsonDocument>.Filter.Eq("t", targetId),
                    Builders<BsonDocument>.Update
                        .Inc("cs", new BsonInt32((int)checkedDelta))
                        .Set("e", cacheEditTime),
                    cancellationToken: ct);
            }

            if (updateProject)
            {
                if (translatedDelta != 0)
                {
                    await _targetDocuments.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", targetId),
                        Builders<BsonDocument>.Update.Inc("tsc", new BsonInt32((int)translatedDelta)),
                        cancellationToken: ct);
                }

                if (checkedDelta != 0)
                {
                    await _targetDocuments.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", targetId),
                        Builders<BsonDocument>.Update.Inc("csc", new BsonInt32((int)checkedDelta)),
                        cancellationToken: ct);
                }
            }
        }

        if (translatedDelta != 0)
        {
            await _files.UpdateManyAsync(
                Builders<BsonDocument>.Filter.In("_id", ids),
                Builders<BsonDocument>.Update.Inc("tsc", new BsonInt32((int)translatedDelta)),
                cancellationToken: ct);
        }

        if (checkedDelta != 0)
        {
            await _files.UpdateManyAsync(
                Builders<BsonDocument>.Filter.In("_id", ids),
                Builders<BsonDocument>.Update.Inc("csc", new BsonInt32((int)checkedDelta)),
                cancellationToken: ct);
        }

        if (updateProject)
        {
            if (translatedDelta != 0)
            {
                await IncProjectAsync(file["p"].AsObjectId, "tsc", translatedDelta, false, ct);
            }

            if (checkedDelta != 0)
            {
                await IncProjectAsync(file["p"].AsObjectId, "csc", checkedDelta, false, ct);
            }
        }
    }

    private async Task ApplyDerivedBytesAsync(
        BsonDocument project,
        long coverDelta,
        long resampleDelta,
        CancellationToken ct)
    {
        if (coverDelta == 0 && resampleDelta == 0)
        {
            return;
        }

        var projectId = project["_id"].AsObjectId;
        var fileSizeDelta = (long)Math.Round((coverDelta + resampleDelta) / 1024d, MidpointRounding.ToEven);
        await _projectDocuments.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Update
                .Inc("cover_bytes", new BsonInt64(coverDelta))
                .Inc("resample_bytes", new BsonInt64(resampleDelta))
                .Inc("fs", new BsonInt64(fileSizeDelta)),
            cancellationToken: ct);
    }

    private async Task TouchEditTimeAsync(
        BsonDocument project,
        BsonDocument file,
        CancellationToken ct)
    {
        var now = new BsonDateTime(Now.UtcDateTime);
        var ids = new BsonArray();
        foreach (var ancestor in AncestorIds(file))
        {
            ids.Add(ancestor);
        }

        ids.Add(file["_id"].AsObjectId);
        await _files.UpdateManyAsync(
            Builders<BsonDocument>.Filter.In("_id", ids),
            Builders<BsonDocument>.Update.Set("et", now),
            cancellationToken: ct);

        var projectId = project["_id"].AsObjectId;
        await _projectDocuments.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Update.Set("m_e", now),
            cancellationToken: ct);

        if (project.Contains("ps") && !project["ps"].IsBsonNull)
        {
            await _projectSetDocuments.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", project["ps"].AsObjectId),
                Builders<BsonDocument>.Update.Set("et", now),
                cancellationToken: ct);
        }

        if (project.Contains("t") && !project["t"].IsBsonNull)
        {
            await _teamDocuments.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", project["t"].AsObjectId),
                Builders<BsonDocument>.Update.Set("m_e", now),
                cancellationToken: ct);
        }
    }

    private async Task<BsonDocument?> LoadParentAsync(BsonValue parent, CancellationToken ct)
    {
        if (parent.IsBsonNull)
        {
            return null;
        }

        return await _files.Find(
                Builders<BsonDocument>.Filter.Eq("_id", parent.AsObjectId))
            .FirstOrDefaultAsync(ct);
    }

    private static bool SameParent(BsonValue oldParent, BsonDocument? target)
    {
        if (oldParent.IsBsonNull)
        {
            return target is null;
        }

        return target is not null && oldParent.AsObjectId == target["_id"].AsObjectId;
    }

    private static void EnsureActivated(BsonDocument file)
    {
        if (!file.Flag("ac"))
        {
            throw new ApiError("FileNotActivatedError", 4016, "此修订版未激活");
        }
    }
}
