using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed partial class FileService
{
    private static readonly HashSet<string> OrderSources = new(StringComparer.Ordinal)
    {
        "team_inherited:creator",
        "team_inherited:admin",
        "project_tag:creator",
        "project_tag:admin",
    };

    public async Task<bool> CanOrderAsync(
        BsonDocument actor,
        BsonDocument project,
        CancellationToken ct)
    {
        if (project.Integer("st") != 0)
        {
            return false;
        }

        var snapshot = await _permissions.ProjectSnapshotAsync(actor, project, ct);
        return snapshot.Has("project:ACCESS")
               && snapshot.PermissionSources.Values
                   .SelectMany(values => values)
                   .Any(OrderSources.Contains);
    }

    public async Task<FileOrderView> OrderAsync(
        BsonDocument actor,
        BsonDocument project,
        CancellationToken ct)
    {
        if (!await CanOrderAsync(actor, project, ct))
        {
            throw new ApiError("NoPermissionError", 103, "没有权限");
        }

        var files = await LoadRootImagesAsync(project["_id"].AsObjectId, ct);
        var version = SnapshotVersion(files);
        var defaultIds = files
            .OrderBy(file => file.Text("dn"), Utf8Comparer)
            .ThenBy(file => file.Integer("t"))
            .ThenBy(file => file.Text("sn"), Utf8Comparer)
            .Select(file => file["_id"].AsObjectId.ToString())
            .ToList();

        var items = new List<Dictionary<string, object?>>(files.Count);
        foreach (var file in files)
        {
            items.Add(await ToApiAsync(file, project, ct));
        }

        return new FileOrderView(items, version, defaultIds);
    }

    public async Task SaveOrderAsync(
        BsonDocument actor,
        BsonDocument project,
        IReadOnlyList<string> fileIds,
        string version,
        bool resetToDefault,
        CancellationToken ct)
    {
        if (!await CanOrderAsync(actor, project, ct))
        {
            throw new ApiError("NoPermissionError", 103, "没有权限");
        }

        var projectId = project["_id"].AsObjectId;
        await using var labelSession = await EnterLabelAsync(projectId, ct);
        using var _ = await _gate.EnterAsync(projectId, ct);
        project = await _projects.GetProjectAsync(projectId.ToString(), ct);

        var now = Now.UtcDateTime;
        var lease = new BsonDateTime(now.Add(OrderLease));
        var leaseFilter = Builders<BsonDocument>.Filter.Eq("_id", projectId)
                          & (Builders<BsonDocument>.Filter.Eq("fol", BsonNull.Value)
                             | Builders<BsonDocument>.Filter.Lt("fol", new BsonDateTime(now)));
        var leased = await _projectDocuments.FindOneAndUpdateAsync(
            leaseFilter,
            Builders<BsonDocument>.Update.Set("fol", lease),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct);

        if (leased is null)
        {
            throw ApiError.Invalid("文件顺序正在保存，请稍后重试");
        }

        try
        {
            var files = await LoadRootImagesAsync(projectId, ct);
            var currentVersion = SnapshotVersion(files);
            if (!string.Equals(currentVersion, version, StringComparison.Ordinal))
            {
                throw ApiError.Invalid("文件列表或顺序已变化，请取消编辑后重新加载");
            }

            var orderedIds = files.Select(file => file["_id"].AsObjectId.ToString()).ToList();
            if (fileIds.Count != orderedIds.Count
                || fileIds.Distinct(StringComparer.Ordinal).Count() != fileIds.Count
                || !fileIds.ToHashSet(StringComparer.Ordinal).SetEquals(orderedIds))
            {
                throw ApiError.Invalid("必须提供当前目录全部图片，且不可重复");
            }

            var objectIds = fileIds
                .Select(id => ObjectId.Parse(id))
                .ToList();

            if (resetToDefault)
            {
                await _files.UpdateManyAsync(
                    Builders<BsonDocument>.Filter.Eq("p", projectId)
                    & Builders<BsonDocument>.Filter.Eq("f", BsonNull.Value)
                    & Builders<BsonDocument>.Filter.Eq("t", TypeImage)
                    & Builders<BsonDocument>.Filter.Eq("ac", true)
                    & Builders<BsonDocument>.Filter.In("_id", objectIds),
                    Builders<BsonDocument>.Update.Unset("mo"),
                    cancellationToken: ct);
                // Python returns here: reset persists "no manual order" only.
                return;
            }

            // Saving the unchanged order must not enable manual ordering.
            if (!fileIds.SequenceEqual(orderedIds, StringComparer.Ordinal))
            {
                var writes = new List<WriteModel<BsonDocument>>(objectIds.Count);
                for (var index = 0; index < objectIds.Count; index++)
                {
                    writes.Add(new UpdateOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq("_id", objectIds[index])
                        & Builders<BsonDocument>.Filter.Eq("p", projectId)
                        & Builders<BsonDocument>.Filter.Eq("ac", true),
                        Builders<BsonDocument>.Update.Set("mo", index + 1)));
                }

                await _files.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = true }, ct);
            }
        }
        finally
        {
            await _projectDocuments.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", projectId)
                & Builders<BsonDocument>.Filter.Eq("fol", lease),
                Builders<BsonDocument>.Update.Unset("fol"),
                cancellationToken: CancellationToken.None);
        }
    }

    private async Task<List<BsonDocument>> LoadRootImagesAsync(ObjectId projectId, CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("p", projectId)
                     & Builders<BsonDocument>.Filter.Eq("f", BsonNull.Value)
                     & Builders<BsonDocument>.Filter.Eq("t", TypeImage)
                     & Builders<BsonDocument>.Filter.Eq("ac", true);
        var manual = await _files.Find(filter & Builders<BsonDocument>.Filter.Ne("mo", BsonNull.Value))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        var sort = manual is null
            ? Builders<BsonDocument>.Sort.Ascending("dn").Ascending("t").Ascending("sn")
            : Builders<BsonDocument>.Sort.Ascending("dn").Ascending("t").Ascending("mo")
                .Ascending("sn").Ascending("_id");
        return await _files.Find(filter).Sort(sort).ToListAsync(ct);
    }

    public async Task<(BsonDocument? Previous, BsonDocument? Next)> NeighborsAsync(
        BsonDocument file,
        CancellationToken ct)
    {
        var baseFilter = Builders<BsonDocument>.Filter.Eq("p", file["p"].AsObjectId)
                         & Builders<BsonDocument>.Filter.Eq("f", file.GetValue("f", BsonNull.Value))
                         & Builders<BsonDocument>.Filter.Eq("t", TypeImage)
                         & Builders<BsonDocument>.Filter.Eq("ac", true);
        var hasManual = await _files.CountDocumentsAsync(
            baseFilter & Builders<BsonDocument>.Filter.Ne("mo", BsonNull.Value),
            new CountOptions { Limit = 1 },
            ct) > 0;

        FilterDefinition<BsonDocument> before;
        FilterDefinition<BsonDocument> after;
        SortDefinition<BsonDocument> previousSort;
        SortDefinition<BsonDocument> nextSort;

        if (!hasManual)
        {
            before = baseFilter & Builders<BsonDocument>.Filter.Lt("sn", file.Text("sn"));
            after = baseFilter & Builders<BsonDocument>.Filter.Gt("sn", file.Text("sn"));
            previousSort = Builders<BsonDocument>.Sort.Descending("sn");
            nextSort = Builders<BsonDocument>.Sort.Ascending("sn");
        }
        else
        {
            var manual = file.Contains("mo") ? file["mo"] : BsonNull.Value;
            if (manual.IsBsonNull)
            {
                var sameNull = baseFilter & Builders<BsonDocument>.Filter.Eq("mo", BsonNull.Value);
                before = sameNull
                         & (Builders<BsonDocument>.Filter.Lt("sn", file.Text("sn"))
                            | (Builders<BsonDocument>.Filter.Eq("sn", file.Text("sn"))
                               & Builders<BsonDocument>.Filter.Lt("_id", file["_id"].AsObjectId)));
                after = (sameNull
                         & (Builders<BsonDocument>.Filter.Gt("sn", file.Text("sn"))
                            | (Builders<BsonDocument>.Filter.Eq("sn", file.Text("sn"))
                               & Builders<BsonDocument>.Filter.Gt("_id", file["_id"].AsObjectId))))
                        | (baseFilter & Builders<BsonDocument>.Filter.Ne("mo", BsonNull.Value));
            }
            else
            {
                var same = baseFilter & Builders<BsonDocument>.Filter.Eq("mo", manual);
                before = (same & Builders<BsonDocument>.Filter.Lt("sn", file.Text("sn")))
                         | (same
                            & Builders<BsonDocument>.Filter.Eq("sn", file.Text("sn"))
                            & Builders<BsonDocument>.Filter.Lt("_id", file["_id"].AsObjectId))
                         | (baseFilter
                            & (Builders<BsonDocument>.Filter.Eq("mo", BsonNull.Value)
                               | Builders<BsonDocument>.Filter.Lt("mo", manual)));
                after = (same & Builders<BsonDocument>.Filter.Gt("sn", file.Text("sn")))
                        | (same
                           & Builders<BsonDocument>.Filter.Eq("sn", file.Text("sn"))
                           & Builders<BsonDocument>.Filter.Gt("_id", file["_id"].AsObjectId))
                        | (baseFilter & Builders<BsonDocument>.Filter.Gt("mo", manual));
            }

            previousSort = Builders<BsonDocument>.Sort.Descending("mo").Descending("sn").Descending("_id");
            nextSort = Builders<BsonDocument>.Sort.Ascending("mo").Ascending("sn").Ascending("_id");
        }

        var previous = await _files.Find(before).Sort(previousSort).Limit(1).FirstOrDefaultAsync(ct);
        var next = await _files.Find(after).Sort(nextSort).Limit(1).FirstOrDefaultAsync(ct);
        return (previous, next);
    }

    public async Task<(IReadOnlyList<Dictionary<string, object?>> Items, long Count)> AdminListAsync(
        Pagination page,
        CancellationToken ct)
    {
        var count = await _files.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Empty, cancellationToken: ct);
        var files = await _files.Find(Builders<BsonDocument>.Filter.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("et"))
            .Skip(page.Skip)
            .Limit(page.Limit)
            .ToListAsync(ct);

        // Batch-prefetch projects so admin serialization needs no per-row query.
        var projectIds = files.Select(file => file["p"].AsObjectId).Distinct().ToList();
        var projects = projectIds.Count == 0
            ? new Dictionary<ObjectId, BsonDocument>()
            : (await _projectDocuments.Find(
                    Builders<BsonDocument>.Filter.In("_id", projectIds))
                .ToListAsync(ct)).ToDictionary(project => project["_id"].AsObjectId);

        var items = new List<Dictionary<string, object?>>(files.Count);
        foreach (var file in files)
        {
            // Python's File.to_api is wrapped by @need_activated, including the
            // admin listing, so an inactive revision fails the whole request.
            if (!file.Flag("ac"))
            {
                throw new ApiError("FileNotActivatedError", 4016, "此修订版未激活");
            }

            projects.TryGetValue(file["p"].AsObjectId, out var project);
            items.Add(await ToApiAsync(file, project, ct));
        }

        return (items, count);
    }
}
