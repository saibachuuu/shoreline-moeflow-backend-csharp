using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// N1 port of the read/preference/policy/cards half of app/services/notifications.py.
// Publish/dispatch/revoke stay in N2; helpers are public so N2 can reuse the
// exact live visibility checks without copying authorization rules.
public sealed partial class NotificationReadService(
    MongoStore store,
    IdentityPermissions permissions,
    NotificationContent content,
    IConfiguration configuration,
    TimeProvider clock)
{
    public static readonly string[] Categories = ["system", "team", "project", "personal"];

    private static readonly string[] HiddenStates = ["draft", "scheduled", "preparing", "cancelled"];
    private static readonly Regex HexId = new(@"\A[a-fA-F0-9]{24}\z", RegexOptions.Compiled);

    private IMongoCollection<BsonDocument> Notifications => store.Collection("notification");
    private IMongoCollection<BsonDocument> Receipts => store.Collection("notification_receipt");
    private IMongoCollection<BsonDocument> Preferences => store.Collection("notification_preference");
    private IMongoCollection<BsonDocument> Policies => store.Collection("notification_policy");
    private IMongoCollection<BsonDocument> Audits => store.Collection("notification_audit");
    private IMongoCollection<BsonDocument> Throttles => store.Collection("notification_throttle");
    private IMongoCollection<BsonDocument> Deliveries => store.Collection("notification_delivery");

    public bool Enabled =>
        ParseBool(configuration["Notifications:Enabled"]) ??
        ParseBool(configuration["ENABLE_NOTIFICATIONS"]) ??
        false;

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public void EnsureEnabled()
    {
        if (!Enabled) throw NotificationErrors.Unavailable("通知系统尚未启用");
    }

    public static ObjectId Oid(string? value)
    {
        if (value is null || !ObjectId.TryParse(value, out var id))
            throw NotificationErrors.Fail("无效的对象标识符");
        return id;
    }

    public static bool IsActive(BsonDocument? user) => user is not null && !user.Flag("b");

    public static bool IsAdministrator(BsonDocument? user) => IsActive(user) && user!.Flag("admin");

    public async Task<BsonDocument?> ScopeAsync(string category, string? scopeId, CancellationToken ct)
    {
        if (category is not ("team" or "project")) return null;
        var id = Oid(scopeId);
        var name = category == "team" ? "team" : "project";
        var group = await store.Collection(name).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        return group ?? throw NotificationErrors.NotFound("关联对象不可访问");
    }

    public async Task<bool> CanPublishAsync(BsonDocument user, string category, string? scopeId, CancellationToken ct)
    {
        if (!IsActive(user)) return false;
        if (category == "system") return IsAdministrator(user);
        if (category is not ("team" or "project")) return false;
        var group = await ScopeAsync(category, scopeId, ct);
        if (group is null) return false;

        if (category == "team")
        {
            var member = await store.Collection("team_member").Find(
                Builders<BsonDocument>.Filter.Eq("team", group["_id"]) &
                Builders<BsonDocument>.Filter.Eq("user", user["_id"]) &
                Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
            if (member is null) return false;
            if (member.Text("base_tag") == "creator") return true;
            var policy = await Policies.Find(new BsonDocument("team_id", group["_id"])).FirstOrDefaultAsync(ct);
            return member.Text("base_tag") == "admin" && policy is not null && policy.Flag("team_admin");
        }

        var projectMember = await store.Collection("project_member").Find(
            Builders<BsonDocument>.Filter.Eq("project", group["_id"]) &
            Builders<BsonDocument>.Filter.Eq("user", user["_id"]) &
            Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
        if (projectMember is null || group.Integer("st") == 1) return false;
        var tags = StringSet(projectMember, "tags");
        if (group.TryGetValue("ou", out var owner) && !owner.IsBsonNull)
        {
            if (owner.IsObjectId && owner.AsObjectId == user["_id"].AsObjectId) return true;
            if (owner.IsString && owner.AsString == user["_id"].AsObjectId.ToString()) return true;
        }
        else if (tags.Contains("creator")) return true;

        if (!group.TryGetValue("t", out var teamId) || !teamId.IsObjectId) return false;
        var teamPolicy = await Policies.Find(new BsonDocument("team_id", teamId)).FirstOrDefaultAsync(ct);
        return tags.Contains("admin") && teamPolicy is not null && teamPolicy.Flag("project_admin");
    }

    public async Task<bool> CanViewProofreadAsync(BsonDocument user, string category, string? scopeId, CancellationToken ct)
    {
        if (!IsActive(user) || category != "project") return false;
        var project = await ScopeAsync(category, scopeId, ct);
        if (project is null) return false;
        var snapshot = await permissions.ProjectSnapshotAsync(user, project, ct);
        return IdentityPermissions.CanProject(snapshot, project, "project:PROOFREAD_TRA");
    }

    public bool IsLive(BsonDocument note) => IsLiveNote(note, UtcNow);

    public async Task<bool> ReceiptVisibleAsync(BsonDocument note, BsonDocument user, CancellationToken ct)
    {
        var context = await PreloadAsync([note], user, ct);
        return ReceiptVisible(note, user, context);
    }

    public async Task<BsonDocument> GetNoteAsync(string id, CancellationToken ct) =>
        await Notifications.Find(new BsonDocument("_id", Oid(id))).FirstOrDefaultAsync(ct)
        ?? throw NotificationErrors.NotFound("通知不存在或不可访问");

    public async Task<(BsonDocument Note, BsonDocument Receipt)> GetReceiptAsync(
        BsonDocument user, string id, CancellationToken ct)
    {
        var note = await GetNoteAsync(id, ct);
        var receipt = await Receipts.Find(
            Builders<BsonDocument>.Filter.Eq("notification_id", note["_id"]) &
            Builders<BsonDocument>.Filter.Eq("user_id", user["_id"])).FirstOrDefaultAsync(ct);
        if (receipt is null) throw NotificationErrors.NotFound("通知不存在或不可访问");
        var context = await PreloadAsync([note], user, ct);
        if (!ReceiptVisible(note, user, context))
            throw NotificationErrors.NotFound("通知不存在或不可访问");
        return (note, receipt);
    }

    public async Task<Dictionary<string, object?>> DetailAsync(
        BsonDocument note,
        BsonDocument user,
        bool management = false,
        BsonDocument? receipt = null,
        bool full = true,
        IReadOnlyDictionary<ObjectId, string>? actorNames = null,
        IReadOnlyDictionary<ObjectId, Dictionary<string, long>>? deliveryCounts = null,
        string searchQuery = "",
        CancellationToken ct = default)
    {
        var actorId = note.TryGetValue("actor_id", out var actorValue) && actorValue.IsObjectId
            ? actorValue.AsObjectId : ObjectId.Empty;
        if (actorNames is null)
        {
            var actor = actorId == ObjectId.Empty ? null : await store.Collection("user")
                .Find(new BsonDocument("_id", actorId)).Project(new BsonDocument("n", 1)).FirstOrDefaultAsync(ct);
            actorNames = new Dictionary<ObjectId, string>();
            if (actor is not null) actorNames = new Dictionary<ObjectId, string> { [actorId] = actor.Text("n") };
        }

        var result = new Dictionary<string, object?>
        {
            ["id"] = note["_id"].AsObjectId.ToString(),
            ["category"] = note.Text("category"),
            ["source"] = note.Text("source", "core"),
            ["event_type"] = note.Text("event_type", "announcement"),
            ["title"] = note.Text("title"),
            ["actor_id"] = actorId == ObjectId.Empty ? "" : actorId.ToString(),
            ["actor_name"] = actorNames.TryGetValue(actorId, out var actorName) ? actorName : "已删除用户",
            ["team_id"] = NullableId(note, "team_id"),
            ["project_id"] = NullableId(note, "project_id"),
            ["state"] = note.Text("state"),
            ["version"] = note.Integer("version"),
            ["created_at"] = SerializeDate(note, "created_at"),
            ["publish_at"] = SerializeDate(note, "publish_at"),
            ["expires_at"] = SerializeDate(note, "expires_at"),
            ["revoked_at"] = SerializeDate(note, "revoked_at"),
            ["recipient_count"] = note.Integer("recipient_count"),
            ["read"] = receipt is not null && HasValue(receipt, "read_at"),
            ["archived"] = receipt is not null && receipt.Flag("archived")
        };

        if (!string.IsNullOrEmpty(searchQuery))
        {
            var text = note.Text("search_text");
            var target = NotificationContent.Normalize(searchQuery);
            var index = text.IndexOf(target, StringComparison.Ordinal);
            var start = index < 0 ? 0 : Math.Max(0, RuneCount(text[..index]) - 45);
            var excerpt = TakeRunes(text, start, 180);
            result["excerpt"] = (start > 0 ? "…" : "") + excerpt +
                (text.EnumerateRunes().Count() > start + 180 ? "…" : "");
        }

        if (full)
        {
            var nodes = note.TryGetValue("nodes", out var nodeValue) && nodeValue.IsBsonArray
                ? nodeValue.AsBsonArray : new BsonArray();
            result["nodes"] = BsonApi.Value(await content.VisibleNodesAsync(nodes, user, ct));
            if (note.Text("event_type") == "proofread_feedback")
                result["feedback_text"] = note.TryGetValue("payload", out var payload) && payload.IsBsonDocument
                    ? payload.AsBsonDocument.Text("text") : "";
        }

        if (management)
        {
            var payload = note.TryGetValue("payload", out var payloadValue) && payloadValue.IsBsonDocument
                ? payloadValue.AsBsonDocument : new BsonDocument();
            result["body"] = full ? note.Text("body") : null;
            result["audience"] = note.TryGetValue("audience", out var audience)
                ? BsonApi.Value(audience) : new Dictionary<string, object?>();
            result["email"] = note.Flag("requested_email");
            result["revoke_reason"] = note.Text("revoke_reason");
            result["error"] = note.Text("error");

            if (deliveryCounts is null)
            {
                var loaded = new Dictionary<ObjectId, Dictionary<string, long>>();
                using var cursor = await Deliveries.Aggregate()
                    .Match(new BsonDocument("notification_id", note["_id"]))
                    .Group(new BsonDocument
                    {
                        ["_id"] = "$state",
                        ["count"] = new BsonDocument("$sum", 1)
                    })
                    .ToCursorAsync(ct);
                var states = new Dictionary<string, long>();
                while (await cursor.MoveNextAsync(ct))
                    foreach (var row in cursor.Current)
                        states[row["_id"].IsString ? row["_id"].AsString : ""] = row["count"].ToInt64();
                loaded[note["_id"].AsObjectId] = states;
                deliveryCounts = loaded;
            }
            result["deliveries"] = deliveryCounts.TryGetValue(note["_id"].AsObjectId, out var counts)
                ? counts : new Dictionary<string, long>();

            if (full)
            {
                var reasons = new Dictionary<string, long>();
                var reasonRows = await Deliveries.Aggregate()
                    .Match(new BsonDocument
                    {
                        ["notification_id"] = note["_id"],
                        ["reason"] = new BsonDocument("$nin", new BsonArray { "", BsonNull.Value })
                    })
                    .Group(new BsonDocument
                    {
                        ["_id"] = "$reason",
                        ["count"] = new BsonDocument("$sum", 1)
                    })
                    .ToListAsync(ct);
                foreach (var row in reasonRows)
                    if (row["_id"].IsString) reasons[row["_id"].AsString] = row["count"].ToInt64();
                result["delivery_reasons"] = reasons;
            }
        }
        return result;
    }

    public async Task<List<Dictionary<string, object?>>> DetailManyAsync(
        IReadOnlyList<BsonDocument> notes,
        BsonDocument user,
        IReadOnlyDictionary<ObjectId, BsonDocument>? receipts = null,
        string searchQuery = "",
        bool management = false,
        CancellationToken ct = default)
    {
        var actorIds = notes
            .Where(note => note.TryGetValue("actor_id", out var value) && value.IsObjectId)
            .Select(note => note["actor_id"].AsObjectId)
            .Distinct()
            .ToArray();
        var actorNames = new Dictionary<ObjectId, string>();
        if (actorIds.Length > 0)
        {
            var actors = await store.Collection("user")
                .Find(new BsonDocument("_id", new BsonDocument("$in",
                    new BsonArray(actorIds.Select(id => (BsonValue)id)))))
                .Project(new BsonDocument("n", 1))
                .ToListAsync(ct);
            foreach (var actor in actors) actorNames[actor["_id"].AsObjectId] = actor.Text("n");
        }

        Dictionary<ObjectId, Dictionary<string, long>>? counts = null;
        if (management && notes.Count > 0)
        {
            counts = new Dictionary<ObjectId, Dictionary<string, long>>();
            var rows = await Deliveries.Aggregate()
                .Match(new BsonDocument("notification_id", new BsonDocument("$in",
                    new BsonArray(notes.Select(note => note["_id"])))))
                .Group(new BsonDocument
                {
                    ["_id"] = new BsonDocument
                    {
                        ["notification"] = "$notification_id",
                        ["state"] = "$state"
                    },
                    ["count"] = new BsonDocument("$sum", 1)
                })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                var key = row["_id"]["notification"].AsObjectId;
                if (!counts.TryGetValue(key, out var states))
                    counts[key] = states = new Dictionary<string, long>();
                states[row["_id"]["state"].IsString ? row["_id"]["state"].AsString : ""] =
                    row["count"].ToInt64();
            }
        }

        var result = new List<Dictionary<string, object?>>(notes.Count);
        foreach (var note in notes)
        {
            var receipt = receipts is not null && receipts.TryGetValue(note["_id"].AsObjectId, out var value)
                ? value : null;
            result.Add(await DetailAsync(note, user, management, receipt, false, actorNames,
                counts, searchQuery, ct));
        }
        return result;
    }

    public async Task<Dictionary<string, object?>> InboxAsync(
        BsonDocument user, string? query, string? category, string? cursor, string? unread,
        string? archived, string? limit, CancellationToken ct)
    {
        EnsureEnabled();
        var size = PageSize(limit);
        var pairs = new List<(BsonDocument Note, BsonDocument Receipt)>();
        string? nextCursor = null;

        await foreach (var pair in ReceiptPageAsync(user, unread == "true", true, query, category,
            cursor, archived == "true", ct))
        {
            if (pairs.Count == size)
            {
                nextCursor = pairs[^1].Receipt["_id"].AsObjectId.ToString();
                break;
            }
            pairs.Add(pair);
        }

        var receipts = pairs.ToDictionary(pair => pair.Note["_id"].AsObjectId, pair => pair.Receipt);
        var rows = await DetailManyAsync(pairs.Select(pair => pair.Note).ToList(), user,
            receipts, query ?? "", false, ct);
        return new Dictionary<string, object?>
        {
            ["items"] = rows,
            ["next_cursor"] = nextCursor
        };
    }

    public async Task<Dictionary<string, object?>> UnreadCountsAsync(BsonDocument user, CancellationToken ct)
    {
        var counts = Categories.ToDictionary(category => category, _ => 0L, StringComparer.Ordinal);
        if (!Enabled)
        {
            return new Dictionary<string, object?> { ["total"] = 0 }
                .Concat(Categories.Select(category => KeyValuePair.Create<string, object?>(category, 0)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        await foreach (var pair in ReceiptPageAsync(user, true, false, null, null, null, false, ct))
        {
            var category = pair.Note.Text("category");
            if (counts.ContainsKey(category)) counts[category]++;
        }
        var result = new Dictionary<string, object?> { ["total"] = counts.Values.Sum() };
        foreach (var category in Categories) result[category] = counts[category];
        return result;
    }

    public async Task<Dictionary<string, object?>> MarkReadAsync(
        BsonDocument user, JsonElement data, string? query, string? category, string? cursor,
        CancellationToken ct)
    {
        EnsureEnabled();
        if (data.ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("请求必须是 JSON 对象");
        foreach (var property in data.EnumerateObject())
            if (property.Name != "category")
                throw NotificationErrors.Fail("分类无效");
        string? selected = null;
        if (data.TryGetProperty("category", out var categoryValue) &&
            categoryValue.ValueKind != JsonValueKind.Null)
        {
            if (categoryValue.ValueKind != JsonValueKind.String)
                throw NotificationErrors.Fail("分类无效");
            selected = categoryValue.GetString();
        }
        if (selected is not null && !Categories.Contains(selected, StringComparer.Ordinal))
            throw NotificationErrors.Fail("分类无效");

        var cutoff = UtcNow;
        var changed = 0L;
        await foreach (var pair in ReceiptPageAsync(user, true, true, query, category, cursor, false, ct))
        {
            if (selected is not null && pair.Note.Text("category") != selected) continue;
            var update = Builders<BsonDocument>.Update.Set("read_at", cutoff);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", pair.Receipt["_id"]) &
                Builders<BsonDocument>.Filter.Eq("user_id", user["_id"]) &
                Builders<BsonDocument>.Filter.Lte("created_at", cutoff) &
                Builders<BsonDocument>.Filter.Eq("read_at", BsonNull.Value);
            var result = await Receipts.UpdateOneAsync(filter, update, cancellationToken: ct);
            changed += result.ModifiedCount;
        }
        return new Dictionary<string, object?> { ["changed"] = changed };
    }

    public async Task<Dictionary<string, object?>> UpdateReceiptAsync(
        BsonDocument user, string id, JsonElement data, CancellationToken ct)
    {
        EnsureEnabled();
        var (_, receipt) = await GetReceiptAsync(user, id, ct);
        if (data.ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("请求必须是 JSON 对象");
        var properties = data.EnumerateObject().ToArray();
        if (properties.Length == 0 ||
            properties.Any(property => property.Name is not ("read" or "archived")) ||
            properties.Any(property => property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw NotificationErrors.Fail("只可修改本人的已读和归档状态");

        var update = Builders<BsonDocument>.Update.Combine();
        var now = UtcNow;
        if (data.TryGetProperty("read", out var read))
            update = read.GetBoolean()
                ? update.Set("read_at", new BsonDateTime(now))
                : update.Set("read_at", BsonNull.Value);
        if (data.TryGetProperty("archived", out var archived))
        {
            var value = archived.GetBoolean();
            update = update.Set("archived", value);
            if (value) update = update.Set("read_at", now);
        }
        await Receipts.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", receipt["_id"]) &
            Builders<BsonDocument>.Filter.Eq("user_id", user["_id"]), update, cancellationToken: ct);
        return new Dictionary<string, object?> { ["ok"] = true };
    }

    public async Task<Dictionary<string, object?>> PreferencesAsync(BsonDocument user, CancellationToken ct)
    {
        var row = await Preferences.Find(new BsonDocument("user_id", user["_id"])).FirstOrDefaultAsync(ct);
        var categories = Categories.ToDictionary(category => category, _ => (object?)true, StringComparer.Ordinal);
        if (row is not null && row.TryGetValue("categories", out var stored) && stored.IsBsonDocument)
            foreach (var element in stored.AsBsonDocument)
                if (categories.ContainsKey(element.Name)) categories[element.Name] = element.Value.IsBoolean
                    ? element.Value.AsBoolean : true;
        return new Dictionary<string, object?>
        {
            ["categories"] = categories,
            ["version"] = row?.Integer("version") ?? 0
        };
    }

    public async Task<Dictionary<string, object?>> SavePreferencesAsync(
        BsonDocument user, JsonElement data, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("请求必须是 JSON 对象");
        var properties = data.EnumerateObject().ToArray();
        var names = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Length != 2 ||
            !names.SetEquals(new[] { "version", "categories" }) ||
            !TryStrictInteger(data.GetProperty("version"), out var version) ||
            data.GetProperty("categories").ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("通知偏好无效");

        var categories = new BsonDocument();
        foreach (var property in data.GetProperty("categories").EnumerateObject())
        {
            if (!Categories.Contains(property.Name, StringComparer.Ordinal) ||
                property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw NotificationErrors.Fail("通知偏好无效");
            categories[property.Name] = property.Value.GetBoolean();
        }

        try
        {
            var row = await Preferences.FindOneAndUpdateAsync(
                Builders<BsonDocument>.Filter.Eq("user_id", user["_id"]) &
                Builders<BsonDocument>.Filter.Eq("version", version),
                Builders<BsonDocument>.Update.Set("categories", categories)
                    .Inc("version", 1),
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                }, ct)
                ?? throw NotificationErrors.Conflict("偏好版本冲突");
            return new Dictionary<string, object?>
            {
                ["version"] = row.Integer("version"),
                ["categories"] = BsonApi.Value(row["categories"])
            };
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            throw NotificationErrors.Conflict("偏好版本冲突");
        }
    }

    public async Task<Dictionary<string, object?>> PolicyAsync(
        BsonDocument user, string teamId, CancellationToken ct)
    {
        var id = await PolicyTeamAsync(user, teamId, false, ct);
        var row = await Policies.Find(new BsonDocument("team_id", id)).FirstOrDefaultAsync(ct);
        return new Dictionary<string, object?>
        {
            ["team_admin"] = row is not null && row.Flag("team_admin"),
            ["project_admin"] = row is not null && row.Flag("project_admin"),
            ["version"] = row?.Integer("version") ?? 0
        };
    }

    public async Task<Dictionary<string, object?>> SavePolicyAsync(
        BsonDocument user, string teamId, JsonElement data, CancellationToken ct)
    {
        EnsureEnabled();
        var id = await PolicyTeamAsync(user, teamId, true, ct);
        if (data.ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("请求必须是 JSON 对象");
        var properties = data.EnumerateObject().ToArray();
        var names = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (properties.Length != 3 ||
            !names.SetEquals(new[] { "version", "team_admin", "project_admin" }) ||
            !TryStrictInteger(data.GetProperty("version"), out var version) ||
            data.GetProperty("team_admin").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            data.GetProperty("project_admin").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw NotificationErrors.Fail("通知权限设置无效");

        var teamAdmin = data.GetProperty("team_admin").GetBoolean();
        var projectAdmin = data.GetProperty("project_admin").GetBoolean();
        BsonDocument row;
        try
        {
            row = await Policies.FindOneAndUpdateAsync(
                Builders<BsonDocument>.Filter.Eq("team_id", id) &
                Builders<BsonDocument>.Filter.Eq("version", version),
                Builders<BsonDocument>.Update
                    .Set("team_admin", teamAdmin)
                    .Set("project_admin", projectAdmin)
                    .Set("updated_by", user["_id"])
                    .Inc("version", 1),
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                }, ct)
                ?? throw NotificationErrors.Conflict("设置版本冲突");
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            throw NotificationErrors.Conflict("设置版本冲突");
        }

        await AuditAsync(user["_id"].AsObjectId, "policy", null, new BsonDocument
        {
            ["team_id"] = id.ToString(),
            ["version"] = version,
            ["team_admin"] = teamAdmin,
            ["project_admin"] = projectAdmin
        }, null, ct);
        return new Dictionary<string, object?> { ["version"] = row.Integer("version") };
    }

    public async Task<Dictionary<string, object?>> CapabilitiesAsync(
        BsonDocument user, string? category, string? scopeId, CancellationToken ct)
    {
        category ??= "system";
        var isEnabled = Enabled;
        var canSend = isEnabled && await CanPublishAsync(user, category, scopeId, ct);
        var creator = false;
        if (isEnabled && category == "team" && IsActive(user))
        {
            var teamId = Oid(scopeId);
            creator = await store.Collection("team_member").Find(
                Builders<BsonDocument>.Filter.Eq("team", teamId) &
                Builders<BsonDocument>.Filter.Eq("user", user["_id"]) &
                Builders<BsonDocument>.Filter.Eq("status", "active") &
                Builders<BsonDocument>.Filter.Eq("base_tag", "creator")).AnyAsync(ct);
        }
        return new Dictionary<string, object?>
        {
            ["enabled"] = isEnabled,
            ["can_send"] = canSend,
            ["can_view_sent"] = canSend ||
                (isEnabled && await CanViewProofreadAsync(user, category, scopeId, ct)),
            ["can_manage"] = IsAdministrator(user),
            ["can_manage_policy"] = creator
        };
    }

    public async Task ThrottleAsync(BsonDocument user, string action, int limit, CancellationToken ct)
    {
        var now = UtcNow;
        var key = $"{user["_id"].AsObjectId}:{action}:{now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";
        try
        {
            await Throttles.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("key", key) &
                Builders<BsonDocument>.Filter.Lt("count", limit),
                Builders<BsonDocument>.Update
                    .Inc("count", 1)
                    .SetOnInsert("expires_at", now.AddMinutes(2)),
                new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            throw NotificationErrors.TooManyRequests("操作过于频繁，请稍后重试");
        }
    }

    public async Task AuditAsync(ObjectId actorId, string action, ObjectId? notificationId,
        BsonDocument? detail, string? key, CancellationToken ct)
    {
        key ??= Guid.NewGuid().ToString();
        await Audits.UpdateOneAsync(
            new BsonDocument("key", key),
            new BsonDocument("$setOnInsert", new BsonDocument
            {
                ["actor_id"] = actorId,
                ["action"] = action,
                ["notification_id"] = notificationId.HasValue ? (BsonValue)notificationId.Value : BsonNull.Value,
                ["detail"] = detail ?? new BsonDocument(),
                ["created_at"] = UtcNow
            }),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    private async Task<ObjectId> PolicyTeamAsync(
        BsonDocument user, string teamId, bool write, CancellationToken ct)
    {
        var id = Oid(teamId);
        var member = await store.Collection("team_member").Find(
            Builders<BsonDocument>.Filter.Eq("team", id) &
            Builders<BsonDocument>.Filter.Eq("user", user["_id"]) &
            Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
        if (member is null || (write && member.Text("base_tag") != "creator"))
            throw NotificationErrors.Forbidden(write
                ? "只有团队创建者可以调整通知发送权限"
                : "无权查看团队策略");
        return id;
    }

    private async IAsyncEnumerable<(BsonDocument Note, BsonDocument Receipt)> ReceiptPageAsync(
        BsonDocument user,
        bool unreadOnly,
        bool filtered,
        string? query,
        string? category,
        string? cursor,
        bool archived,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var receiptFilter = Builders<BsonDocument>.Filter.Eq("user_id", user["_id"]);
        if (unreadOnly)
        {
            receiptFilter &= Builders<BsonDocument>.Filter.Eq("read_at", BsonNull.Value) &
                Builders<BsonDocument>.Filter.Eq("archived", false);
        }
        else
        {
            receiptFilter &= Builders<BsonDocument>.Filter.Eq("archived", archived);
        }
        if (filtered && !string.IsNullOrEmpty(cursor))
            receiptFilter &= Builders<BsonDocument>.Filter.Lt("_id", Oid(cursor));

        var noteFilter = BuildNoteFilter(query, category);
        BsonValue? after = null;
        while (true)
        {
            var batchFilter = receiptFilter;
            if (after is not null)
                batchFilter &= Builders<BsonDocument>.Filter.Lt("_id", after);

            var batch = await Receipts.Find(batchFilter)
                .Sort(new BsonDocument("_id", -1))
                .Limit(100)
                .ToListAsync(ct);
            if (batch.Count == 0) yield break;

            var ids = new BsonArray(batch.Select(row => row["notification_id"]));
            var notes = await Notifications.Find(
                    Builders<BsonDocument>.Filter.In("_id", ids) & noteFilter,
                    new FindOptions { MaxTime = TimeSpan.FromMilliseconds(2000) })
                .Project(Builders<BsonDocument>.Projection
                    .Exclude("nodes")
                    .Exclude("body")
                    .Exclude("search_tokens")
                    .Exclude("payload.html")
                    .Exclude("payload.text"))
                .ToListAsync(ct);
            var noteMap = notes.ToDictionary(row => row["_id"].AsObjectId);
            var context = await PreloadAsync(notes, user, ct);

            foreach (var receipt in batch)
            {
                var notificationId = receipt["notification_id"].AsObjectId;
                if (noteMap.TryGetValue(notificationId, out var note) &&
                    ReceiptVisible(note, user, context))
                    yield return (note, receipt);
            }
            after = batch[^1]["_id"];
            if (batch.Count < 100) yield break;
        }
    }

    private FilterDefinition<BsonDocument> BuildNoteFilter(string? query, string? category)
    {
        var filter = Builders<BsonDocument>.Filter.Empty;
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length > 0)
        {
            if (RuneCount(trimmed) > 100) throw NotificationErrors.Fail("搜索词过长");
            if (trimmed.Length == 24 && HexId.IsMatch(trimmed))
                filter &= Builders<BsonDocument>.Filter.Eq("_id", Oid(trimmed));
            else
            {
                var value = NotificationContent.Normalize(trimmed);
                if (RuneCount(value) < 2) throw NotificationErrors.Fail("搜索词至少两个字符");
                var tokens = NotificationContent.SearchTokens(trimmed).Take(30).ToArray();
                filter &= Builders<BsonDocument>.Filter.All("search_tokens", tokens);
                filter &= Builders<BsonDocument>.Filter.Regex("search_text",
                    new BsonRegularExpression(Regex.Escape(value)));
            }
        }
        if (!string.IsNullOrEmpty(category))
        {
            if (!Categories.Contains(category, StringComparer.Ordinal))
                throw NotificationErrors.Fail("通知分类无效");
            filter &= Builders<BsonDocument>.Filter.Eq("category", category);
        }
        return filter;
    }

    public static bool ReceiptVisible(
        BsonDocument note, BsonDocument? user, NotificationVisibilityContext context)
    {
        if (!IsActive(user)) return false;
        if (!IsLiveNote(note, context.Now))
            return note.Text("event_type") == "proofread_feedback" &&
                note.Text("state") is "draft" or "preparing" &&
                SameId(note, "actor_id", user!["_id"]);
        return RecipientEligible(note, user!, context);
    }

    private static bool RecipientEligible(
        BsonDocument note, BsonDocument user, NotificationVisibilityContext context)
    {
        if (!IsActive(user)) return false;
        var audience = note.TryGetValue("audience", out var audienceValue) && audienceValue.IsBsonDocument
            ? audienceValue.AsBsonDocument : new BsonDocument();

        if (audience.Text("mode") == "manual")
        {
            var selected = IdStrings(audience, "user_ids");
            if (!selected.Contains(user["_id"].AsObjectId.ToString())) return false;
        }
        var roles = StringList(audience, "site_roles");
        if (roles.Count > 0)
        {
            var role = user.Flag("admin") ? "admin" : "member";
            if (!roles.Contains(role)) return false;
        }
        var teamIds = IdStrings(audience, "team_ids");
        if (teamIds.Count > 0 && !teamIds.Any(id => context.TeamMembers.ContainsKey(ObjectId.Parse(id))))
            return false;
        var projectIds = IdStrings(audience, "project_ids");
        if (projectIds.Count > 0 && !projectIds.Any(id => context.ProjectMembers.ContainsKey(ObjectId.Parse(id))))
            return false;

        var category = note.Text("category");
        if (category is "system" or "personal") return true;

        var positions = StringList(audience, "tags");
        if (positions.Count == 0) positions = StringList(audience, "worker_qualifications");
        var wanted = StringList(audience, "base_roles");

        if (category == "team")
        {
            if (!note.TryGetValue("team_id", out var teamValue) || !teamValue.IsObjectId) return false;
            var teamId = teamValue.AsObjectId;
            if (!context.Teams.ContainsKey(teamId)) return false;
            if (!context.TeamMembers.TryGetValue(teamId, out var member)) return false;
            if (wanted.Count > 0 && !wanted.Contains(member.Text("base_tag"))) return false;
            return positions.Count == 0 ||
                positions.Overlaps(StringList(member, "worker_qualifications"));
        }

        if (!note.TryGetValue("project_id", out var projectValue) || !projectValue.IsObjectId)
            return false;
        var projectId = projectValue.AsObjectId;
        if (!context.Projects.ContainsKey(projectId)) return false;

        var payload = note.TryGetValue("payload", out var payloadValue) && payloadValue.IsBsonDocument
            ? payloadValue.AsBsonDocument : new BsonDocument();
        if (note.Text("event_type") == "proofread_feedback" && payload.Flag("cc_myself") &&
            SameId(note, "actor_id", user["_id"]))
        {
            return context.ProjectSnapshots.TryGetValue(projectId, out var snapshot) &&
                IdentityPermissions.CanProject(snapshot, context.Projects[projectId], "project:PROOFREAD_TRA");
        }

        if (!context.ProjectMembers.TryGetValue(projectId, out var projectMember)) return false;
        var tags = StringList(projectMember, "tags");
        var baseRoles = new HashSet<string>(tags.Where(tag => tag is "creator" or "admin"),
            StringComparer.Ordinal);
        if (baseRoles.Count == 0) baseRoles.Add("member");
        return (wanted.Count == 0 || wanted.Overlaps(baseRoles)) &&
            (positions.Count == 0 || positions.Overlaps(tags));
    }

    private async Task<NotificationVisibilityContext> PreloadAsync(
        IReadOnlyList<BsonDocument> notes, BsonDocument? user, CancellationToken ct)
    {
        var context = new NotificationVisibilityContext { Now = UtcNow };
        var teamIds = new HashSet<ObjectId>();
        var projectIds = new HashSet<ObjectId>();
        foreach (var note in notes)
        {
            AddId(teamIds, note.GetValue("team_id", BsonNull.Value));
            AddId(projectIds, note.GetValue("project_id", BsonNull.Value));
            if (note.TryGetValue("audience", out var audience) && audience.IsBsonDocument)
            {
                foreach (var id in IdStrings(audience.AsBsonDocument, "team_ids")) teamIds.Add(ObjectId.Parse(id));
                foreach (var id in IdStrings(audience.AsBsonDocument, "project_ids")) projectIds.Add(ObjectId.Parse(id));
            }
        }

        if (teamIds.Count > 0)
        {
            var teams = await store.Collection("team").Find(
                new BsonDocument("_id", new BsonDocument("$in",
                    new BsonArray(teamIds.Select(id => (BsonValue)id))))).ToListAsync(ct);
            foreach (var team in teams) context.Teams[team["_id"].AsObjectId] = team;
        }
        BsonDocument[] projectRows = [];
        if (projectIds.Count > 0)
        {
            projectRows = (await store.Collection("project").Find(
                new BsonDocument("_id", new BsonDocument("$in",
                    new BsonArray(projectIds.Select(id => (BsonValue)id)))))
                .ToListAsync(ct)).ToArray();
            foreach (var project in projectRows) context.Projects[project["_id"].AsObjectId] = project;
        }

        if (user is not null && IsActive(user))
        {
            if (teamIds.Count > 0)
            {
                var members = await store.Collection("team_member").Find(
                    Builders<BsonDocument>.Filter.In("team", teamIds.Select(id => (BsonValue)id)) &
                    Builders<BsonDocument>.Filter.Eq("user", user["_id"]) &
                    Builders<BsonDocument>.Filter.Eq("status", "active")).ToListAsync(ct);
                foreach (var member in members)
                    context.TeamMembers[member["team"].AsObjectId] = member;
            }
            if (projectIds.Count > 0)
            {
                var members = await store.Collection("project_member").Find(
                    Builders<BsonDocument>.Filter.In("project", projectIds.Select(id => (BsonValue)id)) &
                    Builders<BsonDocument>.Filter.Eq("user", user["_id"]) &
                    Builders<BsonDocument>.Filter.Eq("status", "active")).ToListAsync(ct);
                foreach (var member in members)
                    context.ProjectMembers[member["project"].AsObjectId] = member;
                if (projectRows.Length > 0)
                {
                    var snapshots = await permissions.ProjectSnapshotsAsync(user, projectRows, ct);
                    foreach (var snapshot in snapshots) context.ProjectSnapshots[snapshot.Key] = snapshot.Value;
                }
            }
        }
        return context;
    }

    private static bool IsLiveNote(BsonDocument note, DateTime now)
    {
        if (note.TryGetValue("revoked_at", out var revoked) && !revoked.IsBsonNull) return false;
        if (HiddenStates.Contains(note.Text("state"), StringComparer.Ordinal)) return false;
        return !note.TryGetValue("expires_at", out var expires) ||
            expires.IsBsonNull ||
            !expires.IsBsonDateTime ||
            expires.ToUniversalTime() > now;
    }

    private static bool SameId(BsonDocument note, string field, BsonValue value) =>
        note.TryGetValue(field, out var current) && current.IsObjectId && value.IsObjectId &&
        current.AsObjectId == value.AsObjectId;

    private static void AddId(HashSet<ObjectId> ids, BsonValue value)
    {
        if (value.IsObjectId) ids.Add(value.AsObjectId);
        else if (value.IsString && ObjectId.TryParse(value.AsString, out var parsed)) ids.Add(parsed);
    }

    private static HashSet<string> StringList(BsonDocument document, string key)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!document.TryGetValue(key, out var value) || !value.IsBsonArray) return result;
        foreach (var item in value.AsBsonArray)
            if (item.IsString) result.Add(item.AsString);
        return result;
    }

    private static HashSet<string> StringSet(BsonDocument document, string key) => StringList(document, key);

    private static HashSet<string> IdStrings(BsonDocument document, string key)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!document.TryGetValue(key, out var value) || !value.IsBsonArray) return result;
        foreach (var item in value.AsBsonArray)
        {
            if (item.IsObjectId) result.Add(item.AsObjectId.ToString());
            else if (item.IsString && ObjectId.TryParse(item.AsString, out var id))
                result.Add(id.ToString());
        }
        return result;
    }

    private static string? NullableId(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value)) return null;
        if (value.IsObjectId) return value.AsObjectId.ToString();
        if (value.IsString && ObjectId.TryParse(value.AsString, out var id)) return id.ToString();
        return null;
    }

    private static object? SerializeDate(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value) || value.IsBsonNull || !value.IsBsonDateTime)
            return null;
        var date = value.ToUniversalTime();
        var format = date.Microsecond == 0 ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-ddTHH:mm:ss.ffffff";
        return date.ToString(format, CultureInfo.InvariantCulture) + "Z";
    }

    private static bool HasValue(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && !value.IsBsonNull;

    private static int PageSize(string? limit)
    {
        if (limit is null) return 20;
        if (int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value is >= 1 and <= 100) return value;
        throw NotificationErrors.Fail("分页数量必须在 1–100 之间");
    }

    private static bool TryStrictInteger(JsonElement value, out long number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (!value.TryGetInt64(out number)) return false;
        return true;
    }

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        null or "" => null,
        _ => null
    };

    private static bool IsDuplicateKey(Exception error) => error switch
    {
        MongoWriteException write => write.WriteError?.Category == ServerErrorCategory.DuplicateKey,
        MongoBulkWriteException bulk => bulk.WriteErrors.Any(item =>
            item.Category == ServerErrorCategory.DuplicateKey),
        MongoCommandException command => command.Code == 11000,
        _ => false
    };

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static string TakeRunes(string value, int skip, int count)
    {
        var runes = value.EnumerateRunes().Skip(skip).Take(count);
        var builder = new System.Text.StringBuilder();
        foreach (var rune in runes) builder.Append(rune.ToString());
        return builder.ToString();
    }
}

public sealed class NotificationVisibilityContext
{
    internal DateTime Now { get; set; }
    internal Dictionary<ObjectId, BsonDocument> Teams { get; } = [];
    internal Dictionary<ObjectId, BsonDocument> Projects { get; } = [];
    internal Dictionary<ObjectId, BsonDocument> TeamMembers { get; } = [];
    internal Dictionary<ObjectId, BsonDocument> ProjectMembers { get; } = [];
    internal Dictionary<ObjectId, PermissionSnapshot> ProjectSnapshots { get; } = [];
    internal HashSet<ObjectId> ActiveTeams => [.. TeamMembers.Keys];
    internal HashSet<ObjectId> ActiveProjects => [.. ProjectMembers.Keys];
}
