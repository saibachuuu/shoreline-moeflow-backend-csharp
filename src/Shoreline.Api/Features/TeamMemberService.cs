using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public sealed class TeamMemberService(MongoStore store, IdentityPermissions permissions, TeamMutationGate gates,
    IdentityAuditWriter audit, IConfiguration configuration, TimeProvider clock)
{
    private IMongoCollection<BsonDocument> Members => store.Collection("team_member");

    public async Task<BsonDocument> GetTeamAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key)) throw new ApiError("TeamNotExistError", 3001, "团队不存在");
        return await store.Collection("team").Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("TeamNotExistError", 3001, "团队不存在");
    }

    public async Task<BsonDocument> GetAsync(BsonDocument team, string memberId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(memberId, out var id)) throw IdentityErrors.TeamMemberMissing();
        return await Members.Find(new BsonDocument { ["_id"] = id, ["team"] = team["_id"] }).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.TeamMemberMissing();
    }

    public Task<BsonDocument?> ForUserAsync(BsonDocument team, BsonDocument user, bool activeOnly, CancellationToken ct)
    {
        var filter = new BsonDocument { ["team"] = team["_id"], ["user"] = user["_id"] };
        if (activeOnly) filter["status"] = "active";
        return Members.Find(filter).FirstOrDefaultAsync(ct)!;
    }

    public async Task<BsonDocument> RequireManagerAsync(BsonDocument team, BsonDocument actor, CancellationToken ct)
    {
        var relation = await ForUserAsync(team, actor, true, ct);
        return relation?.Text("base_tag") is "creator" or "admin" ? relation : throw ApiError.Forbidden();
    }

    private static bool CanManage(BsonDocument? relation, BsonDocument actor, BsonDocument target)
    {
        if (relation?.Text("base_tag") is not ("creator" or "admin")) return false;
        if (actor["_id"] == target["user"]) return true;
        return relation.Text("base_tag") == "creator" ? target.Text("base_tag", "member") != "creator" : target.Text("base_tag", "member") == "member";
    }

    private static JsonElement Tags(JsonElement data, string field) => data.TryGetProperty(field, out var value)
        ? value : JsonSerializer.SerializeToElement(Array.Empty<string>());
    private static string[] Qualifications(JsonElement value)
    {
        var tags = IdentityPermissions.NormalizeTags(value);
        if (tags.Any(tag => !IdentityPermissions.IsWorkerTag(tag))) throw IdentityErrors.InvalidTag();
        return tags;
    }

    public async Task<BsonDocument> AddAsync(BsonDocument team, BsonDocument actor, JsonElement data, string requestId, CancellationToken ct)
    {
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        team = await GetTeamAsync(team["_id"].ToString()!, ct);
        var relation = await RequireManagerAsync(team, actor, ct);
        IdentityErrors.RequireObject(data);
        var key = IdentityErrors.Value(data, "user_id");
        if (key.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || key.ValueKind == JsonValueKind.String && key.GetString()!.Length == 0)
            throw IdentityErrors.InvalidRequest("user_id is required");
        if (key.ValueKind != JsonValueKind.String || !ObjectId.TryParse(key.GetString(), out var userId)) throw IdentityErrors.UserMissing();
        var user = await store.Collection("user").Find(new BsonDocument("_id", userId)).FirstOrDefaultAsync(ct) ?? throw IdentityErrors.UserMissing();
        var member = await ForUserAsync(team, user, false, ct);
        if (member?.Text("status", "active") == "active") return member;
        var baseTag = data.TryGetProperty("base_tag", out var baseValue) ? baseValue.ValueKind == JsonValueKind.String ? baseValue.GetString() : null : "member";
        if (baseTag is not ("member" or "admin" or "creator")) throw IdentityErrors.InvalidTag();
        if (baseTag == "creator") throw IdentityErrors.ProtectedTag();
        if (baseTag == "admin" && relation.Text("base_tag") != "creator") throw ApiError.Forbidden();
        var tags = await permissions.ValidateTeamTagsAsync(team, Tags(data, "tags"), ct);
        var qualifications = Qualifications(Tags(data, "worker_qualifications"));
        return await AddOrRestoreLockedAsync(team, actor, user, member, baseTag!, tags, qualifications, requestId, ct);
    }

    // Trusted creation/registration paths only; this is not exposed as a way to
    // bypass the ordinary membership API's manager/creator authorization.
    internal async Task<BsonDocument> JoinTrustedAsync(BsonDocument team, BsonDocument user, string baseTag, string requestId, CancellationToken ct)
    {
        if (baseTag is not ("creator" or "admin" or "member")) throw IdentityErrors.InvalidTag();
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        team = await GetTeamAsync(team["_id"].ToString()!, ct);
        var member = await ForUserAsync(team, user, false, ct);
        if (member?.Text("status", "active") == "active") return member;
        var tags = member?.GetValue("tags", new BsonArray()).AsBsonArray.Where(value => value.IsString).Select(value => value.AsString).ToArray() ?? [];
        var qualifications = member?.GetValue("worker_qualifications", new BsonArray()).AsBsonArray.Where(value => value.IsString).Select(value => value.AsString).ToArray() ?? [];
        return await AddOrRestoreLockedAsync(team, user, user, member, baseTag, tags, qualifications, requestId, ct);
    }

    /// <summary>Python User.join_team for application approval: any role code
    /// collapses to one of the three team base tags.</summary>
    internal Task<BsonDocument> JoinApplicationRoleAsync(BsonDocument team, BsonDocument user, string? roleCode,
        string requestId, CancellationToken ct) =>
        JoinTrustedAsync(team, user, roleCode is "creator" or "admin" or "member" ? roleCode : "member",
            requestId, ct);

    private async Task<BsonDocument> AddOrRestoreLockedAsync(BsonDocument team, BsonDocument actor, BsonDocument user,
        BsonDocument? member, string baseTag, string[] tags, string[] qualifications, string requestId, CancellationToken ct)
    {
        var before = member is null ? new BsonDocument() : State(member);
        await SyncCountAsync(team, ct);
        var reservation = await store.Collection("team").UpdateOneAsync(new BsonDocument
        { ["_id"] = team["_id"], ["m_uc"] = new BsonDocument("$lt", team.Integer("u", 100000)) },
            new BsonDocument("$inc", new BsonDocument("m_uc", 1)), cancellationToken: ct);
        if (reservation.ModifiedCount != 1) throw IdentityErrors.Capacity();
        try
        {
            if (member is null)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                member = new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["team"] = team["_id"],
                    ["user"] = user["_id"],
                    ["base_tag"] = baseTag,
                    ["tags"] = new BsonArray(tags),
                    ["worker_qualifications"] = new BsonArray(qualifications),
                    ["aliases"] = new BsonArray(),
                    ["asrch"] = new BsonArray(),
                    ["default_display_name"] = "",
                    ["status"] = "active",
                    ["version"] = 0,
                    ["create_time"] = now,
                    ["edit_time"] = now,
                    ["removed_time"] = BsonNull.Value
                };
                await Members.InsertOneAsync(member, cancellationToken: ct);
            }
            else
            {
                member = await SaveAsync(member, new BsonDocument
                {
                    ["base_tag"] = baseTag,
                    ["tags"] = new BsonArray(tags),
                    ["worker_qualifications"] = new BsonArray(qualifications),
                    ["status"] = "active",
                    ["removed_time"] = BsonNull.Value
                }, ct);
            }
        }
        finally
        {
            // An aborted request must not strand its reserved slot. A process
            // crash is repaired by the absolute recount before the next add.
            await SyncCountAsync(team, CancellationToken.None);
        }
        await audit.RecordAsync(actor, "team_member_add_or_restore", team, member, before, State(member), requestId, CancellationToken.None);
        return member;
    }

    public async Task<BsonDocument> UpdateAsync(BsonDocument team, string memberId, BsonDocument actor, JsonElement data, string requestId, CancellationToken ct)
    {
        IdentityErrors.RequireObject(data);
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        var member = await GetAsync(team, memberId, ct);
        CheckVersion(member, IdentityErrors.ExpectedVersion(data));
        var relation = await RequireManagerAsync(team, actor, ct);
        if (!CanManage(relation, actor, member)) throw ApiError.Forbidden();
        var changes = new BsonDocument();
        if (data.TryGetProperty("base_tag", out var baseValue))
        {
            if (actor["_id"] == member["user"]) throw ApiError.Forbidden();
            var tag = baseValue.ValueKind == JsonValueKind.String ? baseValue.GetString() : null;
            if (tag is not ("member" or "admin")) throw IdentityErrors.ProtectedTag();
            if (tag == "admin" && relation.Text("base_tag") != "creator" || member.Text("base_tag") == "creator") throw ApiError.Forbidden();
            changes["base_tag"] = tag;
        }
        if (data.TryGetProperty("tags", out var tags)) changes["tags"] = new BsonArray(await permissions.ValidateTeamTagsAsync(team, tags, ct));
        if (data.TryGetProperty("worker_qualifications", out var qualifications)) changes["worker_qualifications"] = new BsonArray(Qualifications(qualifications));
        if (changes.ElementCount == 0) throw IdentityErrors.InvalidRequest("no editable team member fields");
        var updated = await SaveAsync(member, changes, ct);
        await audit.RecordAsync(actor, "team_member_update", team, updated, State(member), State(updated), requestId, CancellationToken.None);
        return updated;
    }

    private async Task<BsonDocument> RequirePersonalEditAsync(BsonDocument team, string memberId, BsonDocument actor, JsonElement data, CancellationToken ct)
    {
        IdentityErrors.RequireObject(data);
        var member = await GetAsync(team, memberId, ct);
        if (member.Text("status", "active") != "active") throw ApiError.Forbidden();
        CheckVersion(member, IdentityErrors.ExpectedVersion(data));
        if (actor["_id"] != member["user"] && !CanManage(await ForUserAsync(team, actor, true, ct), actor, member)) throw ApiError.Forbidden();
        return member;
    }

    public async Task<BsonDocument> UpdateAliasesAsync(BsonDocument team, string memberId, BsonDocument actor, JsonElement data, string requestId, CancellationToken ct)
    {
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        var member = await RequirePersonalEditAsync(team, memberId, actor, data, ct);
        var user = await store.Collection("user").Find(new BsonDocument("_id", member["user"])).FirstOrDefaultAsync(ct) ?? throw IdentityErrors.UserMissing();
        var aliases = IdentityAliases.Normalize(IdentityErrors.Value(data, "aliases"), user.Text("n"), configuration.GetValue("Teams:MaxAliases", 10));
        var updated = await SaveAsync(member, new BsonDocument
        {
            ["aliases"] = new BsonArray(aliases),
            ["asrch"] = new BsonArray(aliases.Select(SearchText.Normalize))
        }, ct);
        await audit.RecordAsync(actor, "team_member_aliases_replace", team, updated, State(member), State(updated), requestId, CancellationToken.None);
        return updated;
    }

    public async Task<BsonDocument> UpdateDisplayNameAsync(BsonDocument team, string memberId, BsonDocument actor, JsonElement data, string requestId, CancellationToken ct)
    {
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        var member = await RequirePersonalEditAsync(team, memberId, actor, data, ct);
        var field = IdentityErrors.Value(data, "default_display_name");
        if (field.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String)) throw IdentityErrors.InvalidRequest("default_display_name must be a string");
        var name = field.ValueKind == JsonValueKind.String ? field.GetString()!.Trim() : "";
        if (name.EnumerateRunes().Count() > 140) throw IdentityErrors.InvalidRequest("default_display_name is too long");
        var updated = await SaveAsync(member, new BsonDocument("default_display_name", name), ct);
        if (IdentityErrors.Truthy(IdentityErrors.Value(data, "sync_to_projects")))
        {
            var user = await store.Collection("user").Find(new BsonDocument("_id", member["user"])).FirstOrDefaultAsync(ct) ?? throw IdentityErrors.UserMissing();
            var effective = name.Length == 0 ? user.Text("ddn").Length > 0 ? user.Text("ddn") : user.Text("n") : name;
            using var projects = await store.Collection("project").Find(new BsonDocument("t", team["_id"]), new FindOptions { BatchSize = 200 })
                .Project(new BsonDocument("_id", 1)).ToCursorAsync(ct);
            while (await projects.MoveNextAsync(ct))
            {
                var ids = new BsonArray(projects.Current.Select(p => p["_id"]));
                await store.Collection("project_member").UpdateManyAsync(new BsonDocument
                {
                    ["project"] = new BsonDocument("$in", ids),
                    ["user"] = member["user"],
                    ["status"] = "active"
                }, new BsonDocument
                {
                    ["$set"] = new BsonDocument { ["display_name"] = effective, ["dns"] = SearchText.Normalize(effective), ["edit_time"] = clock.GetUtcNow().UtcDateTime },
                    ["$inc"] = new BsonDocument("version", 1)
                }, cancellationToken: ct);
            }
        }
        await audit.RecordAsync(actor, "team_member_default_display_name_update", team, updated, State(member), State(updated), requestId, CancellationToken.None);
        return updated;
    }

    public async Task<BsonDocument> RemoveAsync(BsonDocument team, string memberId, BsonDocument actor, string requestId, CancellationToken ct)
    {
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        var member = await GetAsync(team, memberId, ct);
        if (member.Text("base_tag") == "creator") throw IdentityErrors.ProtectedTag();
        if (actor["_id"] != member["user"] && !CanManage(await ForUserAsync(team, actor, true, ct), actor, member)) throw ApiError.Forbidden();
        if (member.Text("status", "active") == "removed") return member;
        var updated = await SaveAsync(member, new BsonDocument { ["status"] = "removed", ["removed_time"] = clock.GetUtcNow().UtcDateTime }, ct);
        await SyncCountAsync(team, CancellationToken.None);
        await audit.RecordAsync(actor, "team_member_remove", team, updated, State(member), State(updated), requestId, CancellationToken.None);
        return updated;
    }

    private async Task SyncCountAsync(BsonDocument team, CancellationToken ct)
    {
        var count = await Members.CountDocumentsAsync(new BsonDocument { ["team"] = team["_id"], ["status"] = "active" }, cancellationToken: ct);
        await store.Collection("team").UpdateOneAsync(new BsonDocument("_id", team["_id"]), new BsonDocument("$set", new BsonDocument("m_uc", count)), cancellationToken: ct);
    }

    private static void CheckVersion(BsonDocument member, int expected)
    {
        if (member.Integer("version") != expected) throw IdentityErrors.VersionConflict();
    }

    private async Task<BsonDocument> SaveAsync(BsonDocument member, BsonDocument changes, CancellationToken ct)
    {
        changes["edit_time"] = clock.GetUtcNow().UtcDateTime;
        return await Members.FindOneAndUpdateAsync<BsonDocument>(IdentityErrors.VersionFilter(member, checked((int)member.Integer("version"))),
            new BsonDocument { ["$set"] = changes, ["$inc"] = new BsonDocument("version", 1) }, new() { ReturnDocument = ReturnDocument.After }, ct)
            ?? throw IdentityErrors.VersionConflict();
    }

    private static BsonDocument State(BsonDocument member) => new()
    {
        ["id"] = member["_id"].ToString(),
        ["user_id"] = member["user"].ToString(),
        ["base_tag"] = member.Text("base_tag", "member"),
        ["tags"] = member.GetValue("tags", new BsonArray()),
        ["worker_qualifications"] = member.GetValue("worker_qualifications", new BsonArray()),
        ["aliases"] = member.GetValue("aliases", new BsonArray()),
        ["default_display_name"] = member.Text("default_display_name"),
        ["status"] = member.Text("status", "active"),
        ["version"] = member.Integer("version")
    };

    public async Task<(IReadOnlyList<BsonDocument> Rows, long Count)> ListAsync(BsonDocument team, BsonDocument actor,
        string? status, string? word, Pagination page, CancellationToken ct)
    {
        if (!(await permissions.TeamSnapshotAsync(actor, team, ct)).Has("team:ACCESS")) throw ApiError.Forbidden();
        var statuses = (status ?? "active").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (statuses.Length == 0) statuses = ["active"];
        var match = new BsonDocument { ["team"] = team["_id"], ["status"] = new BsonDocument("$in", new BsonArray(statuses)) };
        var pipeline = new List<BsonDocument>
        {
            new("$match", match),
            new("$lookup", new BsonDocument { ["from"] = "user", ["localField"] = "user", ["foreignField"] = "_id", ["as"] = "csharp_list_user",
                ["pipeline"] = new BsonArray { new BsonDocument("$project", new BsonDocument { ["n"] = 1, ["ns"] = 1, ["asrch"] = 1,
                    ["as"] = 1, ["ddn"] = 1, ["s"] = 1, ["a"] = 1, ["l"] = 1, ["admin"] = 1, ["e"] = 1 }) } }),
            new("$unwind", new BsonDocument { ["path"] = "$csharp_list_user", ["preserveNullAndEmptyArrays"] = true })
        };
        var needle = SearchText.Normalize(word ?? "");
        if (needle.Length > 0)
        {
            var pattern = new BsonRegularExpression(Regex.Escape(needle), "i");
            pipeline.Add(new BsonDocument("$match", new BsonDocument("$or", new BsonArray {
                new BsonDocument("asrch", pattern), new BsonDocument("csharp_list_user.ns", pattern), new BsonDocument("csharp_list_user.asrch", pattern) })));
        }
        var countRows = await Members.Aggregate<BsonDocument>(pipeline.Append(new BsonDocument("$count", "count")).ToArray()).ToListAsync(ct);
        var count = countRows.Count == 0 ? 0 : countRows[0].Integer("count");
        pipeline.Add(new BsonDocument("$set", new BsonDocument("csharp_sort_name", new BsonDocument("$ifNull", new BsonArray {
            "$csharp_list_user.ns", new BsonDocument("$toLower", "$csharp_list_user.n") }))));
        pipeline.Add(new BsonDocument("$sort", new BsonDocument { ["csharp_sort_name"] = 1, ["_id"] = 1 }));
        pipeline.Add(new BsonDocument("$skip", page.Skip));
        pipeline.Add(new BsonDocument("$limit", page.Limit));
        var rows = await Members.Aggregate<BsonDocument>(pipeline.ToArray(), new AggregateOptions { AllowDiskUse = true }).ToListAsync(ct);
        return (rows, count);
    }

    public async Task<object> ToApiAsync(BsonDocument member, UserView users, HttpContext context, CancellationToken ct)
    {
        var user = member.TryGetValue("csharp_list_user", out var joined) && joined.IsBsonDocument ? joined.AsBsonDocument :
            await store.Collection("user").Find(new BsonDocument("_id", member["user"])).FirstOrDefaultAsync(ct);
        return new
        {
            id = member["_id"].ToString(),
            team_id = member["team"].ToString(),
            user_id = user?["_id"].ToString(),
            user = user is null ? null : await users.ToApiAsync(user, context),
            base_tag = member.Text("base_tag", "member"),
            tags = Strings(member, "tags"),
            worker_qualifications = Strings(member, "worker_qualifications"),
            aliases = Strings(member, "aliases"),
            default_display_name = member.Text("default_display_name"),
            version = member.Integer("version"),
            status = member.Text("status", "active"),
            create_time = member.Timestamp("create_time"),
            edit_time = member.Timestamp("edit_time")
        };
    }

    private static string[] Strings(BsonDocument document, string key) => document.TryGetValue(key, out var value) && value.IsBsonArray
        ? value.AsBsonArray.Where(v => v.IsString).Select(v => v.AsString).ToArray() : [];
}
