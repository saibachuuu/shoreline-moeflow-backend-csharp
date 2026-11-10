using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

public sealed class TeamService(MongoStore store, TeamMemberService members, IdentityPermissions permissions,
    IdentityNameGate names, TeamMutationGate gates, SiteSettings site, LegacyTypes types, ArchiveSecrets secrets,
    ObjectStorage storage, IConfiguration configuration, TimeProvider clock)
{
    private IMongoCollection<BsonDocument> Teams => store.Collection("team");
    private IMongoCollection<BsonDocument> Jobs => store.Collection("csharp_job");
    private static string Reservation(string name) => "team:" + SearchText.Normalize(name);

    public async Task<BsonDocument> CreateAsync(BsonDocument actor, JsonElement data, string requestId, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
        if ((await site.GetAsync(ct)).GetValue("oacg", true).AsBoolean && !actor.Flag("admin"))
            throw new ApiError("OnlyAllowAdminCreateTeamError", 3008, "仅站点管理员可创建团队");
        var name = data.RequiredText("name", 1024);
        using var named = await names.EnterAsync("team", name, ct);
        ValidateName(name);
        var specification = new BsonDocument
        {
            ["n"] = name,
            ["i"] = data.RequiredText("intro", 140),
            ["m_a"] = Choice(data, "allow_apply_type", 1, 2),
            ["m_t"] = Choice(data, "application_check_type", 1, 2),
            ["dr"] = (await types.DefaultRoleAsync("team", data.RequiredText("default_role", 24), null, ct))["_id"]
        };
        await types.SystemRoleAsync("team", "creator", ct);
        var reserved = await Jobs.Find(new BsonDocument("reserved_name", Reservation(name))).FirstOrDefaultAsync(ct);
        BsonDocument payload;
        if (reserved is not null)
        {
            payload = reserved["payload"].AsBsonDocument;
            if (reserved.Flag("creation_done") || payload["creator"] != actor["_id"] ||
                specification.Any(field => payload["team"].AsBsonDocument.GetValue(field.Name, BsonNull.Value) != field.Value))
                throw ApiError.Field("name", "此团队名已被使用");
        }
        else
        {
            await RequireNameAvailableAsync(name, null, ct);
            var id = ObjectId.GenerateNewId();
            var now = clock.GetUtcNow().UtcDateTime;
            var team = specification.DeepClone().AsBsonDocument;
            team.AddRange(new BsonDocument
            {
                ["_id"] = id,
                ["a"] = "",
                ["u"] = 100000,
                ["m_uc"] = 0,
                ["m_c"] = now,
                ["m_e"] = now,
                ["wqm"] = "qualified",
                ["slp"] = BsonNull.Value,
                ["pme"] = true,
                ["pmo"] = 92,
                ["om"] = 0,
                ["ou"] = 0,
                ["og"] = 0,
                ["ot"] = 0,
                ["aak"] = new BsonArray(),
                ["aau"] = ""
            });
            payload = new BsonDocument
            {
                ["creator"] = actor["_id"],
                ["team"] = team,
                ["request_id"] = requestId,
                ["project_set"] = new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["n"] = "default",
                    ["i"] = "",
                    ["t"] = id,
                    ["d"] = true,
                    ["ct"] = now,
                    ["et"] = now
                }
            };
            // Persist the intent before writing any of the three business rows.
            // Both the HTTP fast path and the leased worker can resume it safely.
            await Jobs.InsertOneAsync(new BsonDocument
            {
                ["_id"] = id,
                ["kind"] = "team-create",
                ["payload"] = payload,
                ["reserved_name"] = Reservation(name),
                ["creation_done"] = false,
                ["cancellable"] = false,
                ["state"] = "pending",
                ["attempts"] = 0,
                ["available_at"] = now,
                ["created_at"] = now
            }, cancellationToken: ct);
        }
        var created = await ResumeCreationAsync(payload, ct);
        await Jobs.UpdateOneAsync(new BsonDocument { ["_id"] = created["_id"], ["state"] = new BsonDocument("$in", new BsonArray { "pending", "failed" }) },
            new BsonDocument("$set", new BsonDocument { ["state"] = "done", ["finished_at"] = clock.GetUtcNow().UtcDateTime }), cancellationToken: ct);
        return created;
    }

    public async Task<BsonDocument> ResumeCreationAsync(BsonDocument payload, CancellationToken ct)
    {
        var original = payload["team"].AsBsonDocument;
        var id = original["_id"].AsObjectId;
        var journal = await Jobs.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Team creation intent is missing");
        if (journal.Flag("creation_done")) return await members.GetTeamAsync(id.ToString(), ct);
        var actor = await store.Collection("user").Find(new BsonDocument("_id", payload["creator"])).FirstOrDefaultAsync(ct)
            ?? throw IdentityErrors.UserMissing();
        if (actor.Flag("b")) throw ApiError.Forbidden();
        BsonDocument team;
        using (await gates.EnterAsync(id, ct))
        {
            team = await Teams.FindOneAndUpdateAsync(new BsonDocument("_id", id), new BsonDocument("$setOnInsert", original),
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
            var sets = store.Collection("project_set");
            if (!await sets.Find(new BsonDocument { ["t"] = id, ["d"] = true }).AnyAsync(ct))
            {
                var projectSet = payload["project_set"].AsBsonDocument;
                await sets.UpdateOneAsync(new BsonDocument("_id", projectSet["_id"]), new BsonDocument("$setOnInsert", projectSet),
                    new UpdateOptions { IsUpsert = true }, ct);
            }
        }
        await members.JoinTrustedAsync(team, actor, "creator", payload.Text("request_id"), ct);
        await Jobs.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument
        {
            ["$set"] = new BsonDocument("creation_done", true),
            ["$unset"] = new BsonDocument("reserved_name", 1)
        }, cancellationToken: ct);
        return await members.GetTeamAsync(id.ToString(), ct);
    }

    public async Task<BsonDocument> UpdateAsync(BsonDocument team, BsonDocument actor, JsonElement data, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
        var requestedName = data.TryGetProperty("name", out _) ? data.RequiredText("name", 1024) : team.Text("n");
        using var named = await names.EnterAsync("team", requestedName, ct);
        using var held = await gates.EnterAsync(team["_id"].AsObjectId, ct);
        team = await members.GetTeamAsync(team["_id"].ToString()!, ct);
        var snapshot = await permissions.TeamSnapshotAsync(actor, team, ct);
        if (!snapshot.Has("team:CHANGE")) throw ApiError.Forbidden();
        if ((data.TryGetProperty("worker_qualification_mode", out _) || data.TryGetProperty("proofread_marker_enabled", out _) ||
            data.TryGetProperty("proofread_marker_opacity", out _)) && snapshot.SourceTags.FirstOrDefault() != "creator") throw ApiError.Forbidden();
        var changes = new BsonDocument();
        if (data.TryGetProperty("name", out _))
        {
            if (requestedName != team.Text("n")) { ValidateName(requestedName); await RequireNameAvailableAsync(requestedName, team["_id"].AsObjectId, ct); }
            changes["n"] = requestedName;
        }
        if (data.TryGetProperty("intro", out _)) changes["i"] = data.RequiredText("intro", 140);
        if (data.TryGetProperty("default_role", out _)) changes["dr"] = (await types.DefaultRoleAsync("team", data.RequiredText("default_role", 24), team["_id"].AsObjectId, ct))["_id"];
        if (data.TryGetProperty("allow_apply_type", out _)) changes["m_a"] = Choice(data, "allow_apply_type", 1, 2);
        if (data.TryGetProperty("application_check_type", out _)) changes["m_t"] = Choice(data, "application_check_type", 1, 2);
        if (data.TryGetProperty("worker_qualification_mode", out _))
        {
            var mode = data.RequiredText("worker_qualification_mode", 16);
            if (mode is not ("qualified" or "open")) throw ApiError.Field("worker_qualification_mode", "工作人员资格校验设置无效");
            changes["wqm"] = mode;
        }
        if (data.TryGetProperty("staff_list_page", out var page))
        {
            if (page.ValueKind == JsonValueKind.Null) changes["slp"] = BsonNull.Value;
            else { var number = data.RequiredInteger("staff_list_page"); if (number == 0) throw ApiError.Field("staff_list_page", "不能为零"); changes["slp"] = number; }
        }
        if (data.TryGetProperty("proofread_marker_enabled", out _)) changes["pme"] = data.RequiredBoolean("proofread_marker_enabled");
        if (data.TryGetProperty("proofread_marker_opacity", out _)) changes["pmo"] = Choice(data, "proofread_marker_opacity", 0, 100);
        if (data.TryGetProperty("archive_api_keys", out var keys)) changes["aak"] = ArchiveKeyList.Apply(team.GetValue("aak", new BsonArray()).AsBsonArray, keys, secrets);
        if (data.TryGetProperty("archive_api_url", out var url))
        {
            var value = url.ValueKind == JsonValueKind.Null ? "" : data.RequiredText("archive_api_url", 512).Trim();
            try { changes["aau"] = value.Length == 0 ? "" : ExternalApiUrl.Normalize(value, configuration.GetSection("Archive:AllowedHosts").Get<string[]>() ?? []); }
            catch (ArgumentException error) { throw new ApiError("ValidateError", 2, error.Message); }
        }
        if (changes.ElementCount == 0) throw new ApiError("RequestDataEmptyError", 110, "请求数据为空");
        changes["m_e"] = clock.GetUtcNow().UtcDateTime;
        return await Teams.FindOneAndUpdateAsync(new BsonDocument("_id", team["_id"]), new BsonDocument("$set", changes),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
    }

    public async Task<Dictionary<string, object?>> ViewAsync(BsonDocument team, BsonDocument? actor, bool chinese, CancellationToken ct,
        bool compact = false, PermissionSnapshot? snapshot = null)
    {
        snapshot ??= await permissions.TeamSnapshotAsync(actor, team, ct);
        var avatar = team.Text("a");
        var view = new Dictionary<string, object?>
        {
            ["group_type"] = "team",
            ["id"] = team["_id"].ToString(),
            ["name"] = team.Text("n"),
            ["avatar"] = avatar.Length == 0 ? configuration["Site:DefaultTeamAvatar"] : await storage.SignGetAsync(
                (configuration["Storage:TeamAvatarPrefix"] ?? "team-avatars/").TrimEnd('/') + "/" + avatar.TrimStart('/'), TimeSpan.FromHours(1)),
            ["has_avatar"] = avatar.Length != 0,
            ["worker_qualification_mode"] = team.Text("wqm", "qualified"),
            ["base_tag"] = snapshot.SourceTags.FirstOrDefault(),
            ["effective_permissions"] = snapshot.EffectivePermissions
                .Where(p => p != "team:AUTO_BECOME_PROJECT_ADMIN")
                .Order(StringComparer.Ordinal).ToArray()
        };
        if (compact) return view;
        object? role = null;
        if (actor is not null && snapshot.SourceTags.Count != 0) role = types.RoleView("team", await types.SystemRoleAsync("team", snapshot.SourceTags[0], ct), chinese);
        view["intro"] = team.Text("i"); view["max_user"] = team.Integer("u", 100000); view["user_count"] = team.Integer("m_uc");
        view["staff_list_page"] = team.GetValue("slp", BsonNull.Value).IsNumeric ? team.Integer("slp") : null;
        view["proofread_marker_enabled"] = !team.Contains("pme") || team.Flag("pme"); view["proofread_marker_opacity"] = team.Integer("pmo", 92);
        view["default_role"] = team.GetValue("dr", BsonNull.Value).IsObjectId ? team["dr"].ToString() : null;
        view["allow_apply_type"] = team.Integer("m_a", 1); view["application_check_type"] = team.Integer("m_t", 2);
        view["is_need_check_application"] = team.Integer("m_t", 2) != 1; view["role"] = role;
        view["create_time"] = team.Timestamp("m_c"); view["edit_time"] = team.Timestamp("m_e");
        view["ocr_quota_month"] = team.Integer("om"); view["ocr_quota_used"] = team.Integer("ou");
        view["archive_api_keys"] = ArchiveKeyList.View(team.GetValue("aak", new BsonArray()).AsBsonArray, secrets); view["archive_api_url"] = team.Text("aau");
        return view;
    }

    public async Task<(List<BsonDocument> Rows, long Count)> ListAsync(BsonDocument actor, string? word, Pagination page, bool own, CancellationToken ct)
    {
        var pipeline = new List<BsonDocument>();
        if (!string.IsNullOrEmpty(word)) pipeline.Add(new BsonDocument("$match", new BsonDocument("n", new BsonRegularExpression(Regex.Escape(word), "i"))));
        pipeline.Add(new BsonDocument("$lookup", new BsonDocument
        {
            ["from"] = "team_member",
            ["let"] = new BsonDocument("id", "$_id"),
            ["pipeline"] = new BsonArray { new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$team", "$$id" }), new BsonDocument("$eq", new BsonArray { "$user", actor["_id"] }),
                new BsonDocument("$eq", new BsonArray { "$status", "active" }) }))), new BsonDocument("$limit", 1),
                new BsonDocument("$project", new BsonDocument("_id", 1)) },
            ["as"] = "_membership"
        }));
        if (own) pipeline.Add(new BsonDocument("$match", new BsonDocument("_membership.0", new BsonDocument("$exists", true))));
        var countTask = Teams.Aggregate<BsonDocument>(pipeline.Append(new BsonDocument("$count", "count")).ToArray()).FirstOrDefaultAsync(ct);
        var rowsPipeline = pipeline.Append(new BsonDocument("$sort", new BsonDocument("_id", 1)))
            .Append(new BsonDocument("$skip", page.Skip)).Append(new BsonDocument("$limit", page.Limit))
            .Append(new BsonDocument("$set", new BsonDocument("_joined", new BsonDocument("$gt", new BsonArray {
                new BsonDocument("$size", "$_membership"), 0 })))).Append(new BsonDocument("$unset", "_membership")).ToList();
        // Dashboard navigation must not materialize settings or encrypted key
        // lists for every team, even when legacy documents contain large lists.
        if (own) rowsPipeline.Add(new BsonDocument("$project", new BsonDocument { ["n"] = 1, ["a"] = 1, ["wqm"] = 1 }));
        var rowsTask = Teams.Aggregate<BsonDocument>(rowsPipeline).ToListAsync(ct);
        await Task.WhenAll(countTask, rowsTask);
        return (await rowsTask, (await countTask)?.GetValue("count", 0).ToInt64() ?? 0);
    }

    private async Task RequireNameAvailableAsync(string name, ObjectId? current, CancellationToken ct)
    {
        // Intentionally includes the current row for case-only renames, matching
        // legacy name validation. Unchanged exact names skip this check.
        if (await Teams.Find(new BsonDocument("n", new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i"))).AnyAsync(ct))
            throw ApiError.Field("name", "此团队名已被使用");
        var reservation = new BsonDocument("reserved_name", Reservation(name));
        if (current is not null) reservation["_id"] = new BsonDocument("$ne", current.Value);
        if (await Jobs.Find(reservation).AnyAsync(ct)) throw ApiError.Field("name", "此团队名正在创建中");
    }

    private static void ValidateName(string name)
    {
        if (name.EnumerateRunes().Count() is < 2 or > 18) throw ApiError.Field("name", "长度为2到18个字符");
        if (!Regex.IsMatch(name, @"\A[\u2E80-\u2FDF\u3040-\u318F\u31A0-\u31BF\u31F0-\u31FF\u3400-\u4DB5\u4E00-\u9FFF\uA960-\uA97F\uAC00-\uD7FFa-zA-Z0-9_]+\z"))
            throw ApiError.Field("name", "仅可使用中文/日文/韩文/英文/数字/_");
    }
    private static long Choice(JsonElement data, string field, int minimum, int maximum)
    {
        var value = data.RequiredInteger(field);
        return value >= minimum && value <= maximum ? value : throw ApiError.Field(field, "选项无效");
    }
}
