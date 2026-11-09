using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed partial class FileService
{
    /// <summary>
    /// Creates a folder using the same document/counter/epoch rules as the
    /// legacy API.  The HTTP slice does not expose a create-folder route yet;
    /// this service entry point exists so later folder endpoints and restore
    /// jobs go through the same label lock and mutation gate.
    /// </summary>
    public async Task<Dictionary<string, object?>> CreateFolderAsync(
        BsonDocument project,
        string name,
        string? parentId,
        CancellationToken ct)
    {
        EnsureWorking(project);
        var projectId = project["_id"].AsObjectId;

        await using var labelSession = await EnterLabelAsync(projectId, ct);
        using var _ = await _gate.EnterAsync(projectId, ct);
        project = await _projects.GetProjectAsync(projectId.ToString(), ct);

        var parsed = ParseFilename(name, folder: true);
        var parent = await ResolveFolderAsync(project, parentId, ct);
        var duplicate = await FindByNameAsync(projectId, parent, parsed.Name, activated: true, ct);
        if (duplicate is not null)
        {
            throw new ApiError("FilenameDuplicateError", 4007, "文件名重复");
        }

        var ancestors = parent is null
            ? new BsonArray()
            : new BsonArray(parent["a"].AsBsonArray.Append(parent["_id"].AsObjectId));
        var dirSortName = parent is null ? "" : parent.Text("dn") + parent.Text("sn") + "/";
        var now = new BsonDateTime(Now.UtcDateTime);

        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = parsed.Name,
            ["t"] = TypeFolder,
            ["p"] = projectId,
            ["f"] = parent is null ? BsonNull.Value : parent["_id"].AsObjectId,
            ["a"] = ancestors,
            ["mo"] = BsonNull.Value,
            ["sn"] = parsed.SortName,
            ["dn"] = dirSortName,
            ["sa"] = "",
            ["md"] = "",
            ["fs"] = new BsonInt64(0),
            ["cover_bytes"] = new BsonInt64(0),
            ["resample_bytes"] = new BsonInt64(0),
            ["fn"] = FileNotExistUnknown,
            ["th"] = ThumbnailUnknown,
            ["the"] = "",
            ["fo"] = 0,
            ["fc"] = 0,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["et"] = now,
            ["ov"] = BsonNull.Value,
            ["v"] = 1,
            ["ac"] = true,
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
            ["label_epoch"] = new BsonInt64(labelSession.Revision),
        };

        await _files.InsertOneAsync(document, cancellationToken: ct);
        await CreateTargetCachesAsync(document["_id"].AsObjectId, projectId, ct);
        await IncFileChainAsync(document, "fo", 1, includeSelf: false, updateProject: true, isLong: false, ct);

        return await ToApiAsync(document, project, ct);
    }
}
