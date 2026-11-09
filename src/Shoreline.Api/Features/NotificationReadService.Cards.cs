using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed partial class NotificationReadService
{
    // GET /v1/notification-project-cards. Cards never expose inaccessible or
    // deleted project names; access is re-checked on every request.
    public async Task<Dictionary<string, object?>> CardSearchAsync(
        BsonDocument user, string? query, string? cursor, CancellationToken ct)
    {
        EnsureEnabled();
        await ThrottleAsync(user, "cards", 120, ct);

        var word = (query ?? "").Trim();
        if (word.EnumerateRunes().Count() > 100) throw NotificationErrors.Fail("搜索词过长");

        if (!IsActive(user))
            return new Dictionary<string, object?> { ["items"] = new List<Dictionary<string, object?>>(), ["next_cursor"] = null };

        var filter = Builders<BsonDocument>.Filter.Empty;
        if (word.Length > 0)
            filter &= Builders<BsonDocument>.Filter.Regex("n",
                new BsonRegularExpression(Regex.Escape(word), "i"));
        if (!string.IsNullOrEmpty(cursor))
            filter &= Builders<BsonDocument>.Filter.Gt("_id", Oid(cursor));

        var rows = await store.Collection("project")
            .Find(filter, new FindOptions { MaxTime = TimeSpan.FromMilliseconds(2000) })
            .Project(new BsonDocument
            {
                ["_id"] = 1,
                ["n"] = 1,
                ["t"] = 1,
                ["ou"] = 1,
                ["st"] = 1
            })
            .Sort(new BsonDocument("_id", 1))
            .Limit(100)
            .ToListAsync(ct);

        var snapshots = rows.Count == 0
            ? new Dictionary<ObjectId, PermissionSnapshot>()
            : await permissions.ProjectSnapshotsAsync(user, rows, ct);
        var items = new List<Dictionary<string, object?>>();
        foreach (var project in rows)
        {
            if (!snapshots.TryGetValue(project["_id"].AsObjectId, out var snapshot) ||
                !IdentityPermissions.CanProject(snapshot, project, "project:ACCESS"))
                continue;
            items.Add(new Dictionary<string, object?>
            {
                ["id"] = project["_id"].AsObjectId.ToString(),
                ["name"] = project.Text("n")
            });
        }

        return new Dictionary<string, object?>
        {
            ["items"] = items,
            ["next_cursor"] = rows.Count == 100
                ? rows[^1]["_id"].AsObjectId.ToString()
                : null
        };
    }

    // POST /v1/notification-project-cards/resolve. The content service batches
    // the project lookup and rewrites inaccessible/deleted cards to an opaque
    // "unavailable" node, so callers cannot infer names or ids through cards.
    public async Task<Dictionary<string, object?>> CardResolveAsync(
        BsonDocument user, JsonElement data, CancellationToken ct)
    {
        EnsureEnabled();
        if (data.ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("请求必须是 JSON 对象");
        var properties = data.EnumerateObject().ToArray();
        if (properties.Length != 1 ||
            !data.TryGetProperty("project_ids", out var ids) ||
            ids.ValueKind != JsonValueKind.Array ||
            ids.GetArrayLength() > 5)
            throw NotificationErrors.Fail("最多解析 5 个项目");

        var nodes = new BsonArray();
        foreach (var value in ids.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw NotificationErrors.Fail("无效的对象标识符");
            nodes.Add(new BsonDocument
            {
                ["type"] = "project",
                ["project_id"] = Oid(value.GetString()).ToString()
            });
        }

        var visible = await content.VisibleNodesAsync(nodes, user, ct);
        return new Dictionary<string, object?> { ["nodes"] = BsonApi.Value(visible) };
    }
}
