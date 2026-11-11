using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

// Read-only team insight projections ported from app/apis/team.py:623-756.
// Every page loads its users/projects/members in batches; there is deliberately
// no per-member call into ProjectMemberService.MemberToApiAsync (that helper
// resolves project permissions per row and would be an N+1).
public sealed class TeamInsightService(
    MongoStore store,
    IdentityPermissions permissions,
    ProjectService projects,
    UserView userView,
    ObjectStorage storage,
    IConfiguration configuration)
{
    private const string NoInsightMessage = "您没有权限查看本团队项目统计";
    private static readonly TimeSpan SignedUrlValidity = ObjectStorage.DefaultSignedUrlValidity;

    private IMongoCollection<BsonDocument> TeamMembers => store.Collection("team_member");
    private IMongoCollection<BsonDocument> ProjectMembers => store.Collection("project_member");
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> ProjectSets => store.Collection("project_set");
    private IMongoCollection<BsonDocument> Users => store.Collection("user");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> Languages => store.Collection("language");
    private IMongoCollection<BsonDocument> Outputs => store.Collection("output");

    public async Task<PermissionSnapshot> RequireInsightAsync(BsonDocument actor, BsonDocument team,
        CancellationToken ct)
    {
        var snapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
        if (!snapshot.Has("team:INSIGHT"))
            throw new ApiError("NoPermissionError", 103, NoInsightMessage);
        return snapshot;
    }

    public async Task<BsonDocument> GetUserAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key))
            throw new ApiError("UserNotExistError", 1005, "用户不存在");
        return await Users.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("UserNotExistError", 1005, "用户不存在");
    }

    // GET /v1/teams/{teamId}/insight/users
    public async Task<(object[] Items, long Count)> ListUsersAsync(BsonDocument actor, BsonDocument team,
        string? word, int skip, int limit, HttpContext context, CancellationToken ct)
    {
        await RequireInsightAsync(actor, team, ct);
        context.Items[typeof(CurrentUser)] = actor;

        var members = await TeamMembers.Find(new BsonDocument
        {
            ["team"] = team["_id"], ["status"] = "active"
        }).ToListAsync(ct);
        var memberUserIds = members.Select(MemberUserId).Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToArray();
        var users = await LoadUsersAsync(memberUserIds, ct);

        // list_members: dangling team members (deleted users) are not returned.
        members = members.Where(member =>
        {
            var userId = MemberUserId(member);
            return userId.HasValue && users.ContainsKey(userId.Value);
        }).ToList();

        var needle = SearchText.Normalize(word ?? "");
        if (needle.Length > 0)
        {
            var matchingUsers = users.Values
                .Where(user => Contains(user.Text("ns"), needle) ||
                    Strings(user, "asrch").Any(alias => Contains(alias, needle)))
                .Select(user => user["_id"].AsObjectId).ToHashSet();
            members = members.Where(member =>
            {
                if (Strings(member, "asrch").Any(alias => Contains(alias, needle))) return true;
                var userId = MemberUserId(member);
                return userId.HasValue && matchingUsers.Contains(userId.Value);
            }).ToList();
        }

        members.Sort((left, right) =>
        {
            var leftUser = users[MemberUserId(left)!.Value];
            var rightUser = users[MemberUserId(right)!.Value];
            var comparison = string.CompareOrdinal(SearchText.Normalize(leftUser.Text("n")),
                SearchText.Normalize(rightUser.Text("n")));
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left["_id"].ToString(), right["_id"].ToString());
        });

        var count = members.LongCount();
        var pageMembers = members.Skip(skip).Take(limit).ToList();
        var pageUsers = pageMembers.Select(member => users[MemberUserId(member)!.Value]).ToList();
        var pageUserIds = pageUsers.Select(user => user["_id"].AsObjectId).ToArray();

        var teamProjects = await TeamProjectsAsync(team, ct);
        var projectsById = teamProjects.ToDictionary(project => project["_id"].AsObjectId);
        var relations = pageUserIds.Length == 0
            ? new List<BsonDocument>()
            : await ProjectMembers.Find(new BsonDocument
            {
                ["project"] = new BsonDocument("$in", ArrayOf(teamProjects.Select(p => p["_id"]))),
                ["user"] = new BsonDocument("$in", ArrayOf(pageUserIds)),
                ["status"] = "active"
            }).ToListAsync(ct);
        var relationsByUser = relations
            .GroupBy(relation => relation["user"].AsObjectId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var pageProjectIds = relations.Select(relation => relation["project"].AsObjectId).Distinct().ToArray();
        var sets = await LoadProjectSetsAsync(teamProjects, pageProjectIds, ct);
        var avatars = await LoadAvatarsAsync(pageUsers, ct);

        var data = new List<object>(pageMembers.Count);
        foreach (var user in pageUsers)
        {
            var userId = user["_id"].AsObjectId;
            var userRelations = relationsByUser.GetValueOrDefault(userId) ?? [];
            var projectRows = new List<object>();
            foreach (var relation in userRelations.Take(5))
            {
                var projectId = relation["project"].AsObjectId;
                if (!projectsById.TryGetValue(projectId, out var project)) continue;
                var item = ProjectMini(project, SetForProject(project, sets));
                item["member_summary"] = new[]
                {
                    MemberView(relation, project, user, avatars[userId], false)
                };
                projectRows.Add(item);
            }
            data.Add(new Dictionary<string, object?>
            {
                ["projects"] = projectRows.ToArray(),
                ["count"] = userRelations.LongCount(),
                ["user"] = await userView.ToApiAsync(user, context)
            });
        }
        return (data.ToArray(), count);
    }

    // GET /v1/teams/{teamId}/insight/users/{userId}/projects
    public async Task<(object[] Items, long Count)> ListUserProjectsAsync(BsonDocument actor, BsonDocument team,
        BsonDocument subject, int skip, int limit, CancellationToken ct)
    {
        await RequireInsightAsync(actor, team, ct);
        var relation = await TeamMembers.Find(new BsonDocument
        {
            ["team"] = team["_id"], ["user"] = subject["_id"], ["status"] = "active"
        }).FirstOrDefaultAsync(ct);
        if (relation is null) throw new ApiError("UserNotExistError", 1005, "用户不存在");

        var teamProjects = await TeamProjectsAsync(team, ct);
        var filter = new BsonDocument
        {
            ["user"] = subject["_id"],
            ["project"] = new BsonDocument("$in", ArrayOf(teamProjects.Select(project => project["_id"]))),
            ["status"] = "active"
        };
        var count = await ProjectMembers.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await ProjectMembers.Find(filter).Skip(skip).Limit(limit).ToListAsync(ct);
        var projectsById = teamProjects.ToDictionary(project => project["_id"].AsObjectId);
        var sets = await LoadProjectSetsAsync(teamProjects,
            rows.Select(row => row["project"].AsObjectId).ToArray(), ct);
        var avatar = await AvatarAsync(subject, ct);

        var data = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var projectId = row["project"].AsObjectId;
            if (!projectsById.TryGetValue(projectId, out var project)) continue;
            var mini = ProjectMini(project, SetForProject(project, sets));
            mini["member_summary"] = new[]
            {
                MemberView(row, project, subject, avatar, false)
            };
            data.Add(mini);
        }
        return (data.ToArray(), count);
    }

    // GET /v1/teams/{teamId}/insight/projects
    public async Task<(object[] Items, long Count)> ListProjectsAsync(BsonDocument actor, BsonDocument team,
        string? word, int skip, int limit, HttpContext context, CancellationToken ct)
    {
        var snapshot = await RequireInsightAsync(actor, team, ct);
        var chinese = LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);
        var filter = new BsonDocument { ["t"] = team["_id"], ["st"] = 0 };
        if (!string.IsNullOrEmpty(word))
            filter["ns"] = new BsonRegularExpression(Regex.Escape(word), "i");

        var count = await Projects.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await Projects.Find(filter)
            .Sort(new BsonDocument { ["m_e"] = -1, ["_id"] = -1 })
            .Skip(skip).Limit(limit).ToListAsync(ct);
        var projectIds = rows.Select(project => project["_id"].AsObjectId).ToArray();
        var relations = projectIds.Length == 0
            ? new List<BsonDocument>()
            : await ProjectMembers.Find(new BsonDocument
            {
                ["project"] = new BsonDocument("$in", ArrayOf(rows.Select(project => project["_id"]))),
                ["status"] = "active"
            }).ToListAsync(ct);
        var membersByProject = relations
            .GroupBy(relation => relation["project"].AsObjectId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var memberUserIds = relations.Select(relation => relation["user"])
            .Where(value => value.IsObjectId).Select(value => value.AsObjectId).Distinct().ToArray();
        var memberUsers = await LoadUsersAsync(memberUserIds, ct);
        var avatars = await LoadAvatarsAsync(memberUsers.Values, ct);
        var sets = await LoadProjectSetsAsync(rows, projectIds, ct);
        var includeOutputs = snapshot.Has("team:AUTO_BECOME_PROJECT_ADMIN");
        var outputRows = includeOutputs && projectIds.Length > 0
            ? await Outputs.Find(new BsonDocument
            {
                ["p"] = new BsonDocument("$in", ArrayOf(rows.Select(project => project["_id"])))
            }).Sort(new BsonDocument("ct", -1)).ToListAsync(ct)
            : new List<BsonDocument>();
        var outputsByProject = outputRows.GroupBy(row => row["p"].AsObjectId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var data = new List<object>(rows.Count);
        foreach (var project in rows)
        {
            var projectId = project["_id"].AsObjectId;
            var projectRelations = membersByProject.GetValueOrDefault(projectId) ?? [];
            var users = new List<object>();
            foreach (var relation in projectRelations.Take(5))
            {
                var userId = relation.GetValue("user", BsonNull.Value);
                BsonDocument? user = userId.IsObjectId && memberUsers.TryGetValue(userId.AsObjectId, out var row)
                    ? row : null;
                var member = MemberView(relation, project, user,
                    user is null ? null : avatars.GetValueOrDefault(user["_id"].AsObjectId), true);
                users.Add(member);
            }
            var item = new Dictionary<string, object?>
            {
                ["users"] = users.ToArray(),
                ["count"] = projectRelations.LongCount(),
                ["project"] = ProjectMini(project, SetForProject(project, sets))
            };
            if (includeOutputs)
                item["outputs"] = await OutputViewsAsync(project, outputsByProject.GetValueOrDefault(projectId) ?? [],
                    chinese, ct);
            data.Add(item);
        }
        return (data.ToArray(), count);
    }

    // GET /v1/teams/{teamId}/insight/projects/{projectId}/users
    public async Task<(object[] Items, long Count)> ListProjectUsersAsync(BsonDocument actor, BsonDocument team,
        BsonDocument project, int skip, int limit, CancellationToken ct)
    {
        await RequireInsightAsync(actor, team, ct);
        if (project.GetValue("t", BsonNull.Value) is not { IsObjectId: true } projectTeam ||
            projectTeam.AsObjectId != team["_id"].AsObjectId)
            throw new ApiError("ProjectNotExistError", 4001, "项目不存在");

        var filter = new BsonDocument { ["project"] = project["_id"], ["status"] = "active" };
        var count = await ProjectMembers.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await ProjectMembers.Find(filter).Skip(skip).Limit(limit).ToListAsync(ct);
        var userIds = rows.Select(row => row.GetValue("user", BsonNull.Value))
            .Where(value => value.IsObjectId).Select(value => value.AsObjectId).Distinct().ToArray();
        var users = await LoadUsersAsync(userIds, ct);
        var avatars = await LoadAvatarsAsync(users.Values, ct);
        var data = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var value = row.GetValue("user", BsonNull.Value);
            BsonDocument? user = value.IsObjectId && users.TryGetValue(value.AsObjectId, out var found) ? found : null;
            data.Add(MemberView(row, project, user,
                user is null ? null : avatars.GetValueOrDefault(user["_id"].AsObjectId), true));
        }
        return (data.ToArray(), count);
    }

    private async Task<List<BsonDocument>> TeamProjectsAsync(BsonDocument team, CancellationToken ct) =>
        await Projects.Find(new BsonDocument { ["t"] = team["_id"], ["st"] = 0 }).ToListAsync(ct);

    private async Task<Dictionary<ObjectId, BsonDocument>> LoadUsersAsync(IEnumerable<ObjectId> ids,
        CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0) return new Dictionary<ObjectId, BsonDocument>();
        var rows = await Users.Find(new BsonDocument("_id", new BsonDocument("$in", ArrayOf(distinct))))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row["_id"].AsObjectId);
    }

    private async Task<Dictionary<ObjectId, string?>> LoadAvatarsAsync(IEnumerable<BsonDocument> users,
        CancellationToken ct)
    {
        var result = new Dictionary<ObjectId, string?>();
        foreach (var user in users)
        {
            var id = user["_id"].AsObjectId;
            if (!result.ContainsKey(id)) result[id] = await AvatarAsync(user, ct);
        }
        return result;
    }

    private async Task<string?> AvatarAsync(BsonDocument user, CancellationToken ct)
    {
        _ = ct;
        var avatar = user.Text("a");
        if (avatar.Length == 0) return configuration["Storage:DefaultUserAvatar"];
        return await storage.SignGetAsync(AvatarStoragePrefixes.User(configuration) + avatar, SignedUrlValidity);
    }

    private async Task<Dictionary<ObjectId, BsonDocument>> LoadProjectSetsAsync(
        IEnumerable<BsonDocument> projects, IEnumerable<ObjectId> projectIds, CancellationToken ct)
    {
        var wanted = projectIds.ToHashSet();
        var ids = projects.Where(project => wanted.Contains(project["_id"].AsObjectId))
            .Select(project => project.GetValue("ps", BsonNull.Value))
            .Where(value => value.IsObjectId).Select(value => value.AsObjectId).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<ObjectId, BsonDocument>();
        var rows = await ProjectSets.Find(new BsonDocument("_id", new BsonDocument("$in", ArrayOf(ids))))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row["_id"].AsObjectId);
    }

    private static BsonDocument? SetForProject(BsonDocument project,
        IReadOnlyDictionary<ObjectId, BsonDocument> sets)
    {
        var value = project.GetValue("ps", BsonNull.Value);
        return value.IsObjectId ? sets.GetValueOrDefault(value.AsObjectId) : null;
    }

    private static Dictionary<string, object?> ProjectMini(BsonDocument project, BsonDocument? set) => new()
    {
        ["group_type"] = "project",
        ["id"] = project["_id"].ToString(),
        ["name"] = project.Text("n"),
        ["project_set"] = set is null ? null : ProjectService.ProjectSetView(set)
    };

    private static Dictionary<string, object?> MemberView(BsonDocument member, BsonDocument project,
        BsonDocument? user, string? avatar, bool includeName)
    {
        var tags = Strings(member, "tags");
        var owner = user is not null &&
            project.GetValue("ou", BsonNull.Value) is { IsObjectId: true } ownerId &&
            ownerId.AsObjectId == user["_id"].AsObjectId &&
            member.Text("status", "active") == "active" && tags.Contains("creator");
        var data = new Dictionary<string, object?>
        {
            ["id"] = member["_id"].ToString(),
            ["project_id"] = project["_id"].ToString(),
            ["user_id"] = user is null ? null : user["_id"].ToString(),
            ["external_id"] = member.GetValue("external_id", BsonNull.Value).IsString
                ? member.Text("external_id") : null,
            ["user"] = user is null ? null : new Dictionary<string, object?>
            {
                ["id"] = user["_id"].ToString(),
                ["name"] = user.Text("n"),
                ["avatar"] = avatar,
                ["has_avatar"] = user.Text("a").Length != 0,
                ["aliases"] = Strings(user, "as")
            },
            ["display_name"] = member.Text("display_name"),
            ["tags"] = tags,
            ["effective_permissions"] = Array.Empty<string>(),
            ["status"] = member.Text("status", "active"),
            ["version"] = member.Integer("version"),
            ["is_owner"] = owner,
            ["create_time"] = member.Timestamp("create_time"),
            ["edit_time"] = member.Timestamp("edit_time")
        };
        if (includeName)
            data["name"] = user is null ? member.Text("display_name") : user.Text("n");
        return data;
    }

    private async Task<object[]> OutputViewsAsync(BsonDocument project, IReadOnlyList<BsonDocument> rows,
        bool chinese, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var projectView = await projects.ViewAsync(project, null, chinese, ct);
        var targetIds = rows.Select(row => row.GetValue("t", BsonNull.Value))
            .Where(value => value.IsObjectId).Select(value => value.AsObjectId).Distinct().ToArray();
        var targets = targetIds.Length == 0 ? new Dictionary<ObjectId, BsonDocument>()
            : (await Targets.Find(new BsonDocument("_id", new BsonDocument("$in", ArrayOf(targetIds))))
                .ToListAsync(ct)).ToDictionary(row => row["_id"].AsObjectId);
        var languageIds = targets.Values.Select(target => target.GetValue("l", BsonNull.Value))
            .Where(value => value.IsObjectId).Select(value => value.AsObjectId).Distinct().ToArray();
        var languages = languageIds.Length == 0 ? new Dictionary<ObjectId, BsonDocument>()
            : (await Languages.Find(new BsonDocument("_id", new BsonDocument("$in", ArrayOf(languageIds))))
                .ToListAsync(ct)).ToDictionary(row => row["_id"].AsObjectId);
        var userIds = rows.Select(row => row.GetValue("u", BsonNull.Value))
            .Where(value => value.IsObjectId).Select(value => value.AsObjectId).Distinct().ToArray();
        var users = await LoadUsersAsync(userIds, ct);
        var anonymous = new DefaultHttpContext();
        var userViews = new Dictionary<ObjectId, Dictionary<string, object?>>();
        foreach (var user in users.Values)
            userViews[user["_id"].AsObjectId] = await userView.ToApiAsync(user, anonymous);

        var result = new List<object>(rows.Count);
        foreach (var output in rows)
        {
            var targetValue = output.GetValue("t", BsonNull.Value);
            var target = targetValue.IsObjectId ? targets.GetValueOrDefault(targetValue.AsObjectId) : null;
            var userValue = output.GetValue("u", BsonNull.Value);
            var outputUser = userValue.IsObjectId ? userViews.GetValueOrDefault(userValue.AsObjectId) : null;
            var status = output.Integer("s");
            Dictionary<string, object?> item = new()
            {
                ["id"] = output["_id"].ToString(),
                ["project"] = projectView,
                ["target"] = target is null ? null : TargetView(target, languages, chinese),
                ["user"] = outputUser,
                ["type"] = output.Integer("ty"),
                ["status"] = status,
                ["storage_bytes"] = output.GetValue("sb", 0).ToInt64(),
                ["status_details"] = OutputStatusDetails(),
                ["file_ids_include"] = StringIds(output.GetValue("file_ids_include", BsonNull.Value)),
                ["file_ids_exclude"] = StringIds(output.GetValue("file_ids_exclude", BsonNull.Value)),
                ["create_time"] = output.Timestamp("ct")
            };
            if (status == 4)
            {
                string? link = null;
                var fileName = output.Text("file_name");
                // Storage-pool routing is not part of the J1 port. Fail closed
                // rather than silently signing a default-bucket URL for a
                // project whose artifacts live in another storage source.
                if (!project.GetValue("csid", BsonNull.Value).IsObjectId && fileName.Length > 0)
                    link = await storage.SignGetAsync(OutputPrefix() + output["_id"].AsObjectId + "/" + fileName,
                        SignedUrlValidity,
                        contentDisposition: "attachment");
                item["link"] = link;
            }
            result.Add(item);
        }
        return result.ToArray();
    }

    private object? TargetView(BsonDocument target, IReadOnlyDictionary<ObjectId, BsonDocument> languages,
        bool chinese)
    {
        var languageValue = target.GetValue("l", BsonNull.Value);
        var language = languageValue.IsObjectId ? languages.GetValueOrDefault(languageValue.AsObjectId) : null;
        return new Dictionary<string, object?>
        {
            ["id"] = target["_id"].ToString(),
            ["language"] = language is null ? null : projects.LanguageView(language, chinese),
            ["translated_source_count"] = target.Integer("tsc"),
            ["checked_source_count"] = target.Integer("csc"),
            ["create_time"] = target.Timestamp("ct"),
            ["edit_time"] = target.Timestamp("et"),
            ["intro"] = target.Text("i")
        };
    }

    private string OutputPrefix()
    {
        var value = (configuration["Storage:OutputPrefix"] ?? "outputs/").Trim();
        while (value.StartsWith('/')) value = value[1..];
        return value.Length == 0 ? "" : value.EndsWith('/') ? value : value + "/";
    }

    private static object[] OutputStatusDetails() =>
    [
        new Dictionary<string, object?> { ["id"] = 0, ["name"] = "排队中", ["intro"] = "" },
        new Dictionary<string, object?> { ["id"] = 1, ["name"] = "源文件整理中", ["intro"] = "" },
        new Dictionary<string, object?> { ["id"] = 2, ["name"] = "翻译整理中", ["intro"] = "" },
        new Dictionary<string, object?> { ["id"] = 3, ["name"] = "压缩中", ["intro"] = "" },
        new Dictionary<string, object?> { ["id"] = 4, ["name"] = "已完成", ["intro"] = "" },
        new Dictionary<string, object?> { ["id"] = 5, ["name"] = "导出错误，请重试", ["intro"] = "" }
    ];

    private static string[] StringIds(BsonValue value) => value is BsonArray array
        ? array.Select(item => item.ToString()!).ToArray() : [];

    private static string[] Strings(BsonDocument document, string key) =>
        document.GetValue(key, new BsonArray()) is BsonArray array
            ? array.Where(value => value.IsString).Select(value => value.AsString).ToArray() : [];

    private static ObjectId? MemberUserId(BsonDocument member) =>
        member.GetValue("user", BsonNull.Value) is { IsObjectId: true } value ? value.AsObjectId : null;

    private static bool Contains(string value, string needle) =>
        value.Contains(needle, StringComparison.Ordinal);

    private static BsonArray ArrayOf(IEnumerable<ObjectId> values) =>
        new(values.Select(value => (BsonValue)value));
    private static BsonArray ArrayOf(IEnumerable<BsonValue> values) => new(values);
}
