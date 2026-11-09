using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Legacy-compatible GET /v1/user/projects (app/apis/me.py:350-430).
// Only direct active project memberships are listed; team-admin inheritance is
// deliberately not a listing source (it can still grant permissions inside a
// project the user is already a direct member of).
public sealed record UserProjectListPage(IReadOnlyList<object> Items, long Total);

public sealed class UserProjectListService(MongoStore store, IdentityPermissions permissions, ProjectService projects)
{
    private static readonly Dictionary<string, int[]> StatusNames = new(StringComparer.Ordinal)
    {
        ["NORMAL"] = [0],
        ["CLEARED"] = [1],
        ["COMPLETED"] = [5, 1]
    };

    public async Task<UserProjectListPage> ListAsync(BsonDocument actor, IReadOnlyList<string> statusValues,
        string word, Pagination page, CancellationToken ct)
    {
        var statuses = ParseStatuses(statusValues);
        var match = new BsonDocument();
        if (statuses.Count != 0)
            match["st"] = new BsonDocument("$in", new BsonArray(statuses.Select(status => (BsonValue)status)));
        if (word.Length != 0)
            match["ns"] = new BsonRegularExpression(Regex.Escape(SearchText.Normalize(word)), "i");

        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["user"] = actor["_id"],
                ["status"] = "active"
            }),
            new BsonDocument("$lookup", new BsonDocument
            {
                ["from"] = "project",
                ["localField"] = "project",
                ["foreignField"] = "_id",
                ["as"] = "_project_rows"
            }),
            new BsonDocument("$unwind", "$_project_rows"),
            new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$_project_rows")),
            new BsonDocument("$match", match),
            new BsonDocument("$sort", new BsonDocument { ["m_e"] = -1, ["_id"] = -1 }),
            new BsonDocument("$facet", new BsonDocument
            {
                ["total"] = new BsonArray { new BsonDocument("$count", "value") },
                ["rows"] = new BsonArray
                {
                    new BsonDocument("$skip", page.Skip),
                    new BsonDocument("$limit", page.Limit)
                }
            })
        };

        var facet = await store.Collection("project_member")
            .Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync(ct);
        var total = facet is not null && facet.TryGetValue("total", out var totalValue) && totalValue.IsBsonArray
            ? totalValue.AsBsonArray.FirstOrDefault()?.AsBsonDocument.GetValue("value", 0).ToInt64() ?? 0
            : 0;
        var rows = facet is not null && facet.TryGetValue("rows", out var rowValue) && rowValue.IsBsonArray
            ? rowValue.AsBsonArray.Where(value => value.IsBsonDocument).Select(value => value.AsBsonDocument).ToArray()
            : [];
        if (rows.Length == 0) return new UserProjectListPage([], total);

        var ids = rows.Select(row => row["_id"].AsObjectId).ToArray();
        var teamIds = rows.Select(row => row.GetValue("t", BsonNull.Value)).Where(value => value.IsObjectId)
            .Select(value => value.AsObjectId).Distinct().ToArray();
        var setIds = rows.Select(row => row.GetValue("ps", BsonNull.Value)).Where(value => value.IsObjectId)
            .Select(value => value.AsObjectId).Distinct().ToArray();
        List<BsonDocument> teams = teamIds.Length == 0 ? [] : await store.Collection("team")
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(teamIds.Select(id => (BsonValue)id))))).ToListAsync(ct);
        var teamsById = teams.ToDictionary(team => team["_id"].AsObjectId);
        List<BsonDocument> sets = setIds.Length == 0 ? [] : await store.Collection("project_set")
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(setIds.Select(id => (BsonValue)id))))).ToListAsync(ct);
        var setsById = sets.ToDictionary(set => set["_id"].AsObjectId);

        // One member query for the whole page. ProjectService.MemberSummariesAsync
        // is per-project and would turn a 100-card page into 100+ queries.
        var memberRows = ids.Length == 0 ? new List<BsonDocument>() : await store.Collection("project_member")
            .Find(new BsonDocument
            {
                ["project"] = new BsonDocument("$in", new BsonArray(ids)),
                ["status"] = new BsonDocument("$in", new BsonArray { "active", "invited" })
            })
            .Sort(new BsonDocument { ["display_name"] = 1, ["_id"] = 1 })
            .Project(new BsonDocument
            {
                ["project"] = 1,
                ["display_name"] = 1,
                ["tags"] = 1,
                ["status"] = 1
            })
            .ToListAsync(ct);
        var summaries = memberRows.GroupBy(row => row["project"].AsObjectId)
            .ToDictionary(group => group.Key, group => group.Select(MemberSummary).ToArray());

        var snapshots = await permissions.ProjectSnapshotsAsync(actor, rows, ct);
        var items = new List<object>(rows.Length);
        foreach (var project in rows)
        {
            var id = project["_id"].AsObjectId;
            var team = teamsById.GetValueOrDefault(project.GetValue("t", BsonNull.Value).IsObjectId
                ? project["t"].AsObjectId : ObjectId.Empty) ?? new BsonDocument { ["_id"] = ObjectId.Empty };
            var set = setsById.GetValueOrDefault(project.GetValue("ps", BsonNull.Value).IsObjectId
                ? project["ps"].AsObjectId : ObjectId.Empty) ?? new BsonDocument { ["_id"] = ObjectId.Empty };
            var snapshot = snapshots.GetValueOrDefault(id) ?? throw new InvalidOperationException("Project snapshot missing");
            var card = Properties(projects.ListCardAsync(project, actor, false, snapshot, team, set));
            card["member_summary"] = summaries.GetValueOrDefault(id) ?? [];
            items.Add(card);
        }
        return new UserProjectListPage(items, total);
    }

    internal static HashSet<int> ParseStatuses(IReadOnlyList<string> values)
    {
        var statuses = new HashSet<int>();
        foreach (var raw in values)
        foreach (var token in raw.Split(','))
        {
            var name = token.Trim();
            if (name.Length == 0) continue;
            if (!StatusNames.TryGetValue(name, out var mapped))
                throw IdentityErrors.InvalidRequest("invalid project status");
            statuses.UnionWith(mapped);
        }
        return statuses;
    }

    private static Dictionary<string, object?> MemberSummary(BsonDocument row) => new()
    {
        ["id"] = row["_id"].ToString(),
        ["display_name"] = row.Text("display_name"),
        ["tags"] = row.GetValue("tags", new BsonArray()) is { IsBsonArray: true } tags
            ? tags.AsBsonArray.Where(value => value.IsString).Select(value => value.AsString).ToArray()
            : [],
        ["status"] = row.Text("status", "active")
    };

    // ListCardAsync returns an anonymous object. Reflection preserves its exact
    // field names/values while allowing the batch member_summary to be attached.
    private static Dictionary<string, object?> Properties(object value) => value.GetType()
        .GetProperties().ToDictionary(property => property.Name, property => property.GetValue(value));
}
