using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

/// <summary>
/// The import job needs only the "placeholder file" slice of Project.create_file:
/// top-level names (LabelPlus names cannot contain /), image-only support,
/// reuse of an active case-insensitive image, and the legacy counters/caches.
/// It intentionally does not duplicate the rest of FileService CRUD.
/// </summary>
public sealed partial class ProjectImportService
{
    private IMongoCollection<BsonDocument> Files => store.Collection("file");
    private IMongoCollection<BsonDocument> TargetCaches => store.Collection("file_target_cache");

    public async Task<BsonDocument> EnsurePlaceholderFileAsync(BsonDocument project, string name,
        CancellationToken ct)
    {
        var parsed = FileService.ParseFilename(name, folder: false);
        if (parsed.Type != FileService.TypeImage)
            throw new ApiError("FilenameIllegalError", 107, "文件名错误: 暂不支持的文件格式");

        var projectId = project["_id"].AsObjectId;
        var existing = await Files.Find(new BsonDocument
        {
            ["p"] = projectId,
            ["f"] = BsonNull.Value,
            ["ac"] = true,
            ["n"] = new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(parsed.Name) + "$", "i")
        }).FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            if (existing.Integer("t") == FileService.TypeImage) return existing;
            throw new ApiError("FilenameDuplicateError", 4007, "文件名重复");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = parsed.Name,
            ["t"] = parsed.Type,
            ["p"] = projectId,
            ["f"] = BsonNull.Value,
            ["a"] = new BsonArray(),
            ["mo"] = await LastManualOrderAsync(projectId, parsed.Type, ct) is { } order
                ? new BsonInt32(order + 1)
                : BsonNull.Value,
            ["sn"] = parsed.SortName,
            ["dn"] = "",
            ["sa"] = "",
            ["md"] = "",
            ["fs"] = new BsonInt64(0),
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["fn"] = FileService.FileNotExistNotUpload,
            ["th"] = FileService.ThumbnailUnknown,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = now,
            ["v"] = 1,
            ["ac"] = true,
            ["ss"] = 0,
            ["ps"] = 0,
            ["pt"] = 0,
            ["op"] = 0,
            ["ft"] = 0
        };

        await Files.InsertOneAsync(document, cancellationToken: ct);

        var targetIds = await Targets.Find(new BsonDocument("t", projectId))
            .Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (targetIds.Count > 0)
        {
            await TargetCaches.InsertManyAsync(targetIds.Select(target => new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = document["_id"],
                ["t"] = target["_id"],
                ["ts"] = 0,
                ["cs"] = 0,
                ["e"] = now
            }), cancellationToken: ct);
        }

        // Project.create_file increments file_count on ancestors and the project
        // only (update_self=False); a top-level file therefore has no self bump.
        await Projects.UpdateOneAsync(new BsonDocument("_id", projectId),
            new BsonDocument("$inc", new BsonDocument("fc", 1)), cancellationToken: ct);
        return document;
    }

    private async Task<int?> LastManualOrderAsync(ObjectId projectId, int type, CancellationToken ct)
    {
        var last = await Files.Find(new BsonDocument
        {
            ["p"] = projectId,
            ["f"] = BsonNull.Value,
            ["t"] = type,
            ["ac"] = true,
            ["mo"] = new BsonDocument("$ne", BsonNull.Value)
        }).Sort(new BsonDocument("mo", -1)).Limit(1).FirstOrDefaultAsync(ct);
        return last is null ? null : last["mo"].ToInt32();
    }
}
