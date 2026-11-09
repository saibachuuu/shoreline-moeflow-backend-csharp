using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Notifications;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// Publishing, editing, revoking and scan-driven delivery for notifications.
/// Mirrors the legacy Python notification services and tasks.
/// </summary>
public sealed partial class NotificationPublishService(
    MongoStore store,
    NotificationContent content,
    NotificationReadService read,
    IConfiguration configuration,
    INotificationMailTransport mail)
{
    private static readonly string[] Categories = ["system", "team", "project", "personal"];

    private static readonly HashSet<string> AudienceKeys =
    [
        "mode", "user_ids", "base_roles", "worker_qualifications",
        "tags", "site_roles", "team_ids", "project_ids"
    ];

    private static readonly HashSet<string> NoteKeys =
    [
        "category", "scope_id", "title", "body", "audience",
        "email", "publish_at", "expires_at", "draft"
    ];

    private static readonly HashSet<string> TeamBaseTags = ["creator", "admin", "member"];

    private static readonly HashSet<string> WorkerTags =
    [
        "raw_provider", "scanner", "cropper", "cleaner", "translator",
        "proofreader", "typesetter"
    ];

    private static readonly Regex OffsetSuffix = new(@"(?:[zZ]|[+-]\d{2}:?\d{2})\z", RegexOptions.Compiled);

    private readonly IMongoCollection<BsonDocument> _notifications = store.Collection("notification");
    private readonly IMongoCollection<BsonDocument> _chunks = store.Collection("notification_audience_chunk");
    private readonly IMongoCollection<BsonDocument> _receipts = store.Collection("notification_receipt");
    private readonly IMongoCollection<BsonDocument> _deliveries = store.Collection("notification_delivery");
    private readonly IMongoCollection<BsonDocument> _preferences = store.Collection("notification_preference");
    private readonly IMongoCollection<BsonDocument> _audits = store.Collection("notification_audit");
    private readonly IMongoCollection<BsonDocument> _verified = store.Collection("notification_verified_email");
    private readonly IMongoCollection<BsonDocument> _users = store.Collection("user");
    private readonly IMongoCollection<BsonDocument> _teams = store.Collection("team");
    private readonly IMongoCollection<BsonDocument> _projects = store.Collection("project");
    private readonly IMongoCollection<BsonDocument> _teamMembers = store.Collection("team_member");
    private readonly IMongoCollection<BsonDocument> _projectMembers = store.Collection("project_member");

    public bool UserEmailEnabled => ParseBool(
        configuration["Notifications:UserEmailEnabled"] ?? configuration["ENABLE_USER_EMAIL"]) ?? false;

    public bool LogEmailEnabled => ParseBool(
        configuration["Notifications:LogEmailEnabled"] ?? configuration["ENABLE_LOG_EMAIL"]) ?? false;

    public bool TrustExistingEmails => ParseBool(
        configuration["Notifications:TrustExistingEmails"] ??
        configuration["NOTIFICATION_TRUST_EXISTING_EMAILS"]) ?? false;

    public int MaxAudience => IntegerConfig(
        configuration["Notifications:MaxAudience"] ?? configuration["NOTIFICATION_MAX_AUDIENCE"], 20000);

    public string SiteOrigin =>
        configuration["Notifications:SiteOrigin"] ?? configuration["SITE_ORIGIN"] ?? "";

    // ------------------------------------------------------------------
    // Publish / preview / candidates
    // ------------------------------------------------------------------

    public async Task<BsonDocument> PublishAsync(
        BsonDocument actor, JsonElement dataElement, string? key, CancellationToken ct)
    {
        var data = Object(dataElement);
        return await PublishAsync(actor, data, key, "announcement", null, "core", false, ct);
    }

    public Task<BsonDocument> PublishPersonalAsync(
        BsonDocument actor, string source, IReadOnlyDictionary<string, JsonElement> data, string? key, CancellationToken ct)
    {
        if (!NotificationPersonalSources.IsRegistered(source))
            throw NotificationErrors.Forbidden("未获授权的通知来源或接收关系");
        if (!data.TryGetValue("category", out var category) ||
            category.ValueKind != JsonValueKind.String || category.GetString() != "personal")
            throw NotificationErrors.Forbidden("未获授权的通知来源或接收关系");
        if (!NotificationPersonalSources.Validate(source, actor, data))
            throw NotificationErrors.Forbidden("未获授权的通知来源或接收关系");
        return PublishAsync(actor, data, key, source + ".event", null, source, true, ct);
    }

    internal async Task<BsonDocument> PublishAsync(
        BsonDocument actor,
        IReadOnlyDictionary<string, JsonElement> data,
        string? key,
        string eventType,
        BsonDocument? payload,
        string source,
        bool trusted,
        CancellationToken ct)
    {
        var note = await MakeNoteAsync(actor, data, eventType, payload, source, trusted, ct);

        if (key is null || RuneCount(key) is < 8 or > 160)
            throw NotificationErrors.Fail("需要 8–160 字符的幂等操作键");

        var noteKey = Sha256Hex($"{actor["_id"].AsObjectId}:{source}:{key}");
        var fingerprint = NotificationFingerprint.Compute(data, note.Text("event_type"), payload ?? new BsonDocument());
        note["key"] = noteKey;
        note["fingerprint"] = fingerprint;

        var old = await _notifications.Find(Builders<BsonDocument>.Filter.Eq("key", noteKey)).FirstOrDefaultAsync(ct);
        if (old is not null)
        {
            if (old.Text("fingerprint") != fingerprint)
                throw NotificationErrors.Conflict("相同操作键不能用于不同内容");
            return old;
        }

        var ids = await RecipientIdsAsync(note, ct);
        if (ids.Count == 0)
            throw NotificationErrors.Fail("没有符合条件的接收人");
        EnsureManualMatches(note, ids);

        await read.ThrottleAsync(actor, "publish", 20, ct);

        try
        {
            await _notifications.InsertOneAsync(note, cancellationToken: ct);
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            old = await _notifications.Find(Builders<BsonDocument>.Filter.Eq("key", noteKey)).FirstOrDefaultAsync(ct);
            if (old is null) throw;
            if (old.Text("fingerprint") != fingerprint)
                throw NotificationErrors.Conflict("相同操作键不能用于不同内容");
            return old;
        }

        await read.AuditAsync(actor["_id"].AsObjectId, "publish", note["_id"].AsObjectId, null, noteKey + ":publish", ct);
        await _notifications.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", note["_id"]),
            Builders<BsonDocument>.Update.Set("publish_audited", true), cancellationToken: ct);
        note["publish_audited"] = true;
        return note;
    }

    public async Task<Dictionary<string, object?>> PreviewAsync(
        BsonDocument actor, JsonElement dataElement, string? candidateQuery, CancellationToken ct)
    {
        var data = Object(dataElement);
        var note = await MakeNoteAsync(actor, data, "announcement", null, "core", false, ct);
        await read.ThrottleAsync(actor, "preview", 60, ct);

        var ids = await RecipientIdsAsync(note, ct);
        EnsureManualMatches(note, ids);

        var filter = Builders<BsonDocument>.Filter.In("_id", ids);
        var users = await _users.Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("_id").Include("n").Include("e"))
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);

        var summary = new Dictionary<string, long>(StringComparer.Ordinal);
        var requestedEmail = note.Flag("requested_email");
        var category = note.Text("category");
        foreach (var user in users)
        {
            var reason = requestedEmail ? await EmailReasonAsync(user, category, ct) : "not_requested";
            var bucket = string.IsNullOrEmpty(reason) ? "eligible" : reason;
            summary[bucket] = summary.GetValueOrDefault(bucket) + 1;
        }

        if (!string.IsNullOrEmpty(candidateQuery))
        {
            var term = candidateQuery.Length > 100 ? candidateQuery[..100] : candidateQuery;
            var pattern = Regex.Escape(term);
            var regex = new BsonRegularExpression(pattern, "i");
            users = await _users.Find(filter & Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Regex("n", regex),
                    Builders<BsonDocument>.Filter.Regex("as", regex)))
                .Project(Builders<BsonDocument>.Projection.Include("_id").Include("n").Include("e"))
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["count"] = ids.Count,
            ["email_count"] = summary.GetValueOrDefault("eligible"),
            ["email_summary"] = summary,
            ["users"] = users.Take(100).Select(user => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = user["_id"].AsObjectId.ToString(),
                ["name"] = user.Text("n")
            }).ToArray(),
            ["nodes"] = BsonApi.Value(await content.VisibleNodesAsync(note["nodes"].AsBsonArray, actor, ct))
        };
        return result;
    }

    public async Task<Dictionary<string, object?>> CandidatesAsync(
        BsonDocument actor,
        string? category,
        string? scopeId,
        string? query,
        string? cursor,
        string? limit,
        bool adminSurface,
        CancellationToken ct)
    {
        read.EnsureEnabled();
        await read.ThrottleAsync(actor, "candidates", 120, ct);

        var resolvedCategory = string.IsNullOrWhiteSpace(category) ? "system" : category.Trim();
        if (adminSurface && resolvedCategory != "system")
            throw NotificationErrors.Forbidden("管理入口只选择站点接收人");

        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var categoryDocument = JsonDocument.Parse(JsonSerializer.Serialize(resolvedCategory));
        data["category"] = categoryDocument.RootElement.Clone();
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            using var scopeDocument = JsonDocument.Parse(JsonSerializer.Serialize(scopeId));
            data["scope_id"] = scopeDocument.RootElement.Clone();
        }
        using var titleDocument = JsonDocument.Parse("\"接收人预览\"");
        data["title"] = titleDocument.RootElement.Clone();
        using var audienceDocument = JsonDocument.Parse("{\"mode\":\"all\"}");
        data["audience"] = audienceDocument.RootElement.Clone();

        var note = await MakeNoteAsync(actor, data, "announcement", null, "core", false, ct);
        var ids = await RecipientIdsAsync(note, ct);

        var filter = Builders<BsonDocument>.Filter.In("_id", ids);
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (RuneCount(query) > 100) throw NotificationErrors.Fail("搜索词过长");
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Regex("n", regex),
                Builders<BsonDocument>.Filter.Regex("as", regex));
        }
        if (!string.IsNullOrWhiteSpace(cursor))
            filter &= Builders<BsonDocument>.Filter.Gt("_id", NotificationReadService.Oid(cursor));

        var size = PageSize(limit);
        var rows = await _users.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Limit(size + 1)
            .ToListAsync(ct);

        var items = rows.Take(size).Select(row => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row["_id"].AsObjectId.ToString(),
            ["name"] = row.Text("n")
        }).ToArray();
        string? next = rows.Count > size ? rows[size - 1]["_id"].AsObjectId.ToString() : null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["items"] = items,
            ["next_cursor"] = next
        };
    }

    // ------------------------------------------------------------------
    // Draft editing and revoke
    // ------------------------------------------------------------------

    public async Task<BsonDocument> EditDraftAsync(
        BsonDocument actor, BsonDocument note, JsonElement dataElement, CancellationToken ct)
    {
        read.EnsureEnabled();
        await OrdinaryManageAsync(actor, note, ct);

        var data = Object(dataElement);
        if (!data.TryGetValue("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt64(out var version))
            throw NotificationErrors.Fail("需要当前草稿版本");

        if (HasValue(note, "revoked_at") || note.Text("state") is not ("draft" or "scheduled"))
            throw NotificationErrors.Conflict("只能编辑未发布且未撤回的草稿");

        var requestedCategory = Text(data, "category");
        var requestedScope = Text(data, "scope_id") ?? "";
        var currentScope = NullableId(note, "project_id") ?? NullableId(note, "team_id");
        if (requestedCategory != note.Text("category") ||
            requestedScope != (currentScope?.ToString() ?? ""))
            throw NotificationErrors.Fail("不能修改通知类别或范围");

        var rest = new Dictionary<string, JsonElement>(data, StringComparer.Ordinal);
        rest.Remove("version");
        var fresh = await MakeNoteAsync(actor, rest, "announcement", null, "core", false, ct);

        var ids = await RecipientIdsAsync(fresh, ct);
        if (ids.Count == 0) throw NotificationErrors.Fail("草稿接收人为空或包含范围外用户");
        EnsureManualMatches(fresh, ids);

        var now = read.UtcNow;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", note["_id"])
                     & Builders<BsonDocument>.Filter.Eq("version", version)
                     & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value)
                     & Builders<BsonDocument>.Filter.In("state", new[] { "draft", "scheduled" })
                     & Builders<BsonDocument>.Filter.Lte("lease_until", now);

        var update = Builders<BsonDocument>.Update
            .Set("title", fresh["title"])
            .Set("body", fresh["body"])
            .Set("nodes", fresh["nodes"])
            .Set("search_text", fresh["search_text"])
            .Set("search_tokens", fresh["search_tokens"])
            .Set("audience", fresh["audience"])
            .Set("requested_email", fresh["requested_email"])
            .Set("publish_at", fresh["publish_at"])
            .Set("expires_at", fresh["expires_at"])
            .Set("state", fresh["state"])
            .Inc("version", 1);

        var updated = await _notifications.FindOneAndUpdateAsync(
            filter, update, new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (updated is null) throw NotificationErrors.Conflict("草稿版本冲突");

        await read.AuditAsync(actor["_id"].AsObjectId, "edit_draft", updated["_id"].AsObjectId, null, null, ct);
        return updated;
    }

    public async Task<BsonDocument> RevokeAsync(
        BsonDocument actor, BsonDocument note, JsonElement dataElement, string? key, bool admin, CancellationToken ct)
    {
        read.EnsureEnabled();
        if (admin)
        {
            if (!NotificationReadService.IsAdministrator(actor))
                throw NotificationErrors.Forbidden("需要有效站点管理员权限");
        }
        else
        {
            await OrdinaryManageAsync(actor, note, ct);
        }

        var data = Object(dataElement);
        if (data.Keys.Any(name => name is not ("confirmed" or "version" or "reason")) ||
            !data.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
            throw NotificationErrors.Fail("必须二次确认撤回");
        if (!data.TryGetValue("reason", out var reasonElement) || reasonElement.ValueKind != JsonValueKind.String)
            throw NotificationErrors.Fail("需要撤回理由及当前版本");
        var reason = reasonElement.GetString()!.Trim();
        if (RuneCount(reason) is < 1 or > 500)
            throw NotificationErrors.Fail("需要撤回理由及当前版本");
        if (!data.TryGetValue("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt64(out var version))
            throw NotificationErrors.Fail("需要撤回理由及当前版本");
        if (key is null || RuneCount(key) is < 8 or > 160)
            throw NotificationErrors.Fail("需要撤回操作幂等键");

        var operation = Sha256Hex($"{actor["_id"].AsObjectId}:{key}");
        if (HasValue(note, "revoked_at"))
        {
            if (note.Text("moderation_key") == operation)
            {
                var action = note.Text("moderation_action", "revoke");
                await read.AuditAsync(actor["_id"].AsObjectId, action, note["_id"].AsObjectId,
                    new BsonDocument("reason", note.Text("revoke_reason")), operation + ":revoke", ct);
                await _notifications.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", note["_id"]),
                    Builders<BsonDocument>.Update.Set("moderation_audited", true), cancellationToken: ct);
                return note;
            }
            throw NotificationErrors.Conflict("通知已经撤回，请刷新");
        }

        var now = read.UtcNow;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", note["_id"])
                     & Builders<BsonDocument>.Filter.Eq("version", version)
                     & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value);
        var update = Builders<BsonDocument>.Update
            .Set("revoked_at", now)
            .Set("revoked_by", actor["_id"])
            .Set("revoke_reason", reason)
            .Set("moderation_key", operation)
            .Set("moderation_action", admin ? "admin_revoke" : "revoke")
            .Set("moderation_audited", false)
            .Set("state", "cancelled")
            .Inc("version", 1);

        var updated = await _notifications.FindOneAndUpdateAsync(
            filter, update, new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (updated is null) throw NotificationErrors.Conflict("通知版本已变化，请刷新后再次确认");

        var auditAction = updated.Text("moderation_action", admin ? "admin_revoke" : "revoke");
        await read.AuditAsync(actor["_id"].AsObjectId, auditAction, updated["_id"].AsObjectId,
            new BsonDocument("reason", reason), operation + ":revoke", ct);
        await _notifications.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", updated["_id"]),
            Builders<BsonDocument>.Update.Set("moderation_audited", true), cancellationToken: ct);
        await _deliveries.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("notification_id", updated["_id"])
            & Builders<BsonDocument>.Filter.In("state", new[] { "pending", "retry_wait" }),
            Builders<BsonDocument>.Update.Set("state", "cancelled").Set("reason", "revoked"),
            cancellationToken: ct);

        return updated;
    }

    // ------------------------------------------------------------------
    // Note construction
    // ------------------------------------------------------------------

    private async Task<BsonDocument> MakeNoteAsync(
        BsonDocument actor,
        IReadOnlyDictionary<string, JsonElement> data,
        string eventType,
        BsonDocument? payload,
        string source,
        bool trusted,
        CancellationToken ct)
    {
        read.EnsureEnabled();
        if (!NotificationReadService.IsActive(actor)) throw NotificationErrors.Forbidden("用户不可用");
        if (data.Keys.Any(name => !NoteKeys.Contains(name)))
            throw NotificationErrors.Fail("通知请求包含不支持的字段");

        var category = Text(data, "category");
        if (category is null || !Categories.Contains(category))
            throw NotificationErrors.Forbidden("不能发布此类通知");
        if (category == "personal" && !trusted)
            throw NotificationErrors.Forbidden("不能发布此类通知");

        var scopeId = Text(data, "scope_id");
        var group = await read.ScopeAsync(category, scopeId, ct);
        if (!trusted && !await read.CanPublishAsync(actor, category, scopeId, ct))
            throw NotificationErrors.Forbidden("没有当前范围的通知发送权限");

        if (!data.TryGetValue("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String)
            throw NotificationErrors.Fail("标题须为 1–200 字符的单行文本");
        var title = titleElement.GetString()!.Trim();
        if (RuneCount(title) is < 1 or > 200 || title.Any(ch => ch < 32))
            throw NotificationErrors.Fail("标题须为 1–200 字符的单行文本");

        var body = "";
        if (data.TryGetValue("body", out var bodyElement))
        {
            if (bodyElement.ValueKind != JsonValueKind.String)
                throw NotificationErrors.Fail("通知正文格式不正确");
            body = bodyElement.GetString()!;
        }

        if (data.TryGetValue("email", out var emailElement) && emailElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw NotificationErrors.Fail("通知开关必须为布尔值");
        if (data.TryGetValue("draft", out var draftElement) && draftElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw NotificationErrors.Fail("通知开关必须为布尔值");

        data.TryGetValue("audience", out var audienceElement);
        var audience = await ValidateAudienceAsync(category, audienceElement, ct);
        var nodes = await content.PrepareAsync(body, actor, ct);

        var now = read.UtcNow;
        var publishAt = DateValue(data, "publish_at") ?? now;
        var expiresAt = DateValue(data, "expires_at");
        if (expiresAt is { } expiry && expiry <= Max(now, publishAt))
            throw NotificationErrors.Fail("失效时间必须晚于发送时间");
        if (publishAt > now.AddDays(366))
            throw NotificationErrors.Fail("定时时间不能超过一年");

        var text = NotificationContent.Normalize(title + " " + NotificationContent.Plain(nodes));
        var state = data.TryGetValue("draft", out var draft) && draft.ValueKind == JsonValueKind.True
            ? "draft"
            : publishAt > now ? "scheduled" : "preparing";

        ObjectId? teamId = null;
        ObjectId? projectId = null;
        if (category == "team" && group is not null)
        {
            teamId = group["_id"].AsObjectId;
        }
        else if (category == "project" && group is not null)
        {
            projectId = group["_id"].AsObjectId;
            teamId = NullableId(group, "t");
        }

        var note = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["category"] = category,
            ["source"] = source,
            ["event_type"] = eventType,
            ["actor_id"] = actor["_id"],
            ["team_id"] = teamId is { } team ? team : BsonNull.Value,
            ["project_id"] = projectId is { } project ? project : BsonNull.Value,
            ["title"] = title,
            ["body"] = body,
            ["nodes"] = nodes,
            ["search_text"] = text,
            ["search_tokens"] = new BsonArray(NotificationContent.SearchTokens(text).Select(token => (BsonValue)token)),
            ["audience"] = audience,
            ["requested_email"] = data.TryGetValue("email", out var requested) && requested.ValueKind == JsonValueKind.True,
            ["payload"] = payload ?? new BsonDocument(),
            ["state"] = state,
            ["created_at"] = now,
            ["publish_at"] = publishAt,
            ["expires_at"] = expiresAt is { } value ? value : BsonNull.Value,
            ["revoked_at"] = BsonNull.Value,
            ["revoked_by"] = BsonNull.Value,
            ["revoke_reason"] = "",
            ["moderation_key"] = "",
            ["moderation_action"] = "revoke",
            ["moderation_audited"] = false,
            ["publish_audited"] = false,
            ["version"] = 0,
            ["cursor"] = "",
            ["next_chunk"] = 0,
            ["recipient_count"] = 0,
            ["error"] = "",
            ["lease_token"] = "",
            ["lease_until"] = DateTime.MinValue
        };
        return note;
    }

    private async Task<BsonDocument> ValidateAudienceAsync(string category, JsonElement? value, CancellationToken ct)
    {
        if (value is not { ValueKind: JsonValueKind.Object })
            throw NotificationErrors.Fail("收件人筛选无效");
        var data = Object(value.Value);
        if (data.Keys.Any(name => !AudienceKeys.Contains(name)))
            throw NotificationErrors.Fail("收件人筛选无效");

        var mode = Text(data, "mode");
        if (mode is not ("all" or "condition" or "manual"))
            throw NotificationErrors.Fail("请明确选择收件人模式");

        var result = new BsonDocument { ["mode"] = mode };
        foreach (var (name, element) in data)
        {
            if (name == "mode") continue;
            if (element.ValueKind != JsonValueKind.Array)
                throw NotificationErrors.Fail("收件人条件必须是有界字符串列表");
            var items = element.EnumerateArray().ToList();
            if (items.Count > (name == "user_ids" ? 1000 : 30))
                throw NotificationErrors.Fail("收件人条件必须是有界字符串列表");
            var values = new List<string>();
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw NotificationErrors.Fail("收件人条件必须是有界字符串列表");
                values.Add(item.GetString()!);
            }
            values = values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
            result[name] = new BsonArray(values.Select(item => (BsonValue)item));
        }

        if (mode == "all" && result.Names.Count() != 1)
            throw NotificationErrors.Fail("全体模式不能附带其他条件");

        if (mode == "manual")
        {
            var names = result.Names.ToHashSet(StringComparer.Ordinal);
            result.TryGetValue("user_ids", out var manualIds);
            if (!names.SetEquals(["mode", "user_ids"]) || manualIds is null ||
                manualIds.AsBsonArray.Count == 0)
                throw NotificationErrors.Fail("请选择接收用户");
            result["user_ids"] = new BsonArray(manualIds.AsBsonArray
                .Select(item => (BsonValue)NotificationReadService.Oid(item.AsString).ToString()));
        }
        else if (result.Contains("user_ids"))
        {
            throw NotificationErrors.Fail("手选用户与条件模式互斥");
        }

        if (mode == "condition" && result.All(pair => pair.Name == "mode" || pair.Value.AsBsonArray.Count == 0))
            throw NotificationErrors.Fail("请选择至少一项筛选条件");

        if (category == "system")
        {
            if (result.Contains("base_roles") || result.Contains("worker_qualifications") || result.Contains("tags"))
                throw NotificationErrors.Fail("站点通知不支持该成员条件");
            if (result.TryGetValue("site_roles", out var siteRoles) &&
                siteRoles.AsBsonArray.Any(item => item.AsString is not ("admin" or "member")))
                throw NotificationErrors.Fail("站点身份条件无效");
            if (result.Contains("team_ids") && result.Contains("project_ids"))
                throw NotificationErrors.Fail("团队范围与项目范围不能混用");
            foreach (var name in new[] { "team_ids", "project_ids" })
            {
                if (!result.TryGetValue(name, out var raw)) continue;
                var ids = raw.AsBsonArray.Select(item => NotificationReadService.Oid(item.AsString)).ToList();
                var collection = name == "team_ids" ? _teams : _projects;
                var count = await collection.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.In("_id", ids), cancellationToken: ct);
                if (count != ids.Count) throw NotificationErrors.Fail("接收范围不存在");
                result[name] = new BsonArray(ids.Select(id => (BsonValue)id.ToString()));
            }
        }
        else if (category is "team" or "project")
        {
            if (result.Contains("site_roles") || result.Contains("team_ids") || result.Contains("project_ids"))
                throw NotificationErrors.Fail("不能扩大当前通知范围");
            if (result.TryGetValue("base_roles", out var baseRoles) &&
                baseRoles.AsBsonArray.Any(item => !TeamBaseTags.Contains(item.AsString)))
                throw NotificationErrors.Fail("成员身份无效");
            foreach (var name in new[] { "worker_qualifications", "tags" })
            {
                if (result.TryGetValue(name, out var positions) &&
                    positions.AsBsonArray.Any(item => !WorkerTags.Contains(item.AsString)))
                    throw NotificationErrors.Fail("工作人员职位无效");
            }
        }

        if (category == "personal" && mode != "manual")
            throw NotificationErrors.Fail("个人通知只能由受信服务指定用户");

        return result;
    }

    // ------------------------------------------------------------------
    // Recipient resolution
    // ------------------------------------------------------------------

    public async Task<List<ObjectId>> RecipientIdsAsync(BsonDocument note, CancellationToken ct)
    {
        var audience = note["audience"].AsBsonDocument;
        var limit = MaxAudience;
        HashSet<ObjectId>? ids = null;
        var category = note.Text("category");

        if (category == "team")
        {
            var teamId = NullableId(note, "team_id");
            if (teamId is null) return [];
            if (await _teams.Find(Builders<BsonDocument>.Filter.Eq("_id", teamId.Value)).FirstOrDefaultAsync(ct) is null)
                return [];
            var filter = Builders<BsonDocument>.Filter.Eq("team", teamId.Value)
                         & Builders<BsonDocument>.Filter.Eq("status", "active");
            if (audience.TryGetValue("base_roles", out var roles) && roles.AsBsonArray.Count > 0)
                filter &= Builders<BsonDocument>.Filter.In("base_tag", roles.AsBsonArray.Select(item => item.AsString));
            var positions = Positions(audience);
            if (positions.Count > 0)
                filter &= Builders<BsonDocument>.Filter.In("worker_qualifications", positions);
            var rows = await _teamMembers.Find(filter).Limit(limit + 1).ToListAsync(ct);
            if (rows.Count > limit) throw NotificationErrors.Fail("接收范围超过配置上限");
            ids = rows.Where(row => row.Contains("user") && row["user"].IsObjectId)
                .Select(row => row["user"].AsObjectId).ToHashSet();
        }
        else if (category == "project")
        {
            var projectId = NullableId(note, "project_id");
            if (projectId is null) return [];
            if (await _projects.Find(Builders<BsonDocument>.Filter.Eq("_id", projectId.Value)).FirstOrDefaultAsync(ct) is null)
                return [];
            var filter = Builders<BsonDocument>.Filter.Eq("project", projectId.Value)
                         & Builders<BsonDocument>.Filter.Eq("status", "active")
                         & Builders<BsonDocument>.Filter.Ne("user", BsonNull.Value);
            if (audience.TryGetValue("base_roles", out var roles) && roles.AsBsonArray.Count > 0)
            {
                var wanted = roles.AsBsonArray.Select(item => item.AsString).ToList();
                var direct = wanted.Where(tag => tag != "member").ToList();
                FilterDefinition<BsonDocument>? baseFilter = direct.Count > 0
                    ? Builders<BsonDocument>.Filter.In("tags", direct)
                    : null;
                if (wanted.Contains("member"))
                {
                    var member = Builders<BsonDocument>.Filter.Nin("tags", new[] { "creator", "admin" });
                    baseFilter = baseFilter is null ? member : Builders<BsonDocument>.Filter.Or(baseFilter, member);
                }
                if (baseFilter is not null) filter &= baseFilter;
            }
            var positions = Positions(audience);
            if (positions.Count > 0)
                filter &= Builders<BsonDocument>.Filter.In("tags", positions);
            var rows = await _projectMembers.Find(filter).Limit(limit + 1).ToListAsync(ct);
            if (rows.Count > limit) throw NotificationErrors.Fail("接收范围超过配置上限");
            ids = rows.Where(row => row.Contains("user") && row["user"].IsObjectId)
                .Select(row => row["user"].AsObjectId).ToHashSet();
        }

        if (note.Text("event_type") == "proofread_feedback" && note["payload"].AsBsonDocument.Flag("cc_myself"))
        {
            var actorId = NullableId(note, "actor_id");
            var projectId = NullableId(note, "project_id");
            if (actorId is not null && projectId is not null)
            {
                var actor = await _users.Find(Builders<BsonDocument>.Filter.Eq("_id", actorId.Value)).FirstOrDefaultAsync(ct);
                if (NotificationReadService.IsActive(actor))
                {
                    var project = await _projects.Find(Builders<BsonDocument>.Filter.Eq("_id", projectId.Value)).FirstOrDefaultAsync(ct);
                    if (project is not null && await read.CanViewProofreadAsync(actor!, "project", projectId.Value.ToString(), ct))
                    {
                        ids ??= [];
                        ids.Add(actorId.Value);
                    }
                }
            }
        }

        var scopedTeamIds = ParseIds(audience, "team_ids");
        var scopedProjectIds = ParseIds(audience, "project_ids");
        if (scopedTeamIds.Count > 0 || scopedProjectIds.Count > 0)
        {
            var teamIds = scopedTeamIds;
            var projectIds = scopedProjectIds;
            ids = [];
            if (teamIds.Count > 0)
            {
                var rows = await _teamMembers.Find(
                        Builders<BsonDocument>.Filter.In("team", teamIds)
                        & Builders<BsonDocument>.Filter.Eq("status", "active"))
                    .Limit(limit + 1).ToListAsync(ct);
                if (rows.Count > limit) throw NotificationErrors.Fail("接收范围超过配置上限");
                foreach (var row in rows)
                    if (row.Contains("user") && row["user"].IsObjectId) ids.Add(row["user"].AsObjectId);
            }
            if (projectIds.Count > 0)
            {
                var rows = await _projectMembers.Find(
                        Builders<BsonDocument>.Filter.In("project", projectIds)
                        & Builders<BsonDocument>.Filter.Eq("status", "active")
                        & Builders<BsonDocument>.Filter.Ne("user", BsonNull.Value))
                    .Limit(limit + 1).ToListAsync(ct);
                if (rows.Count > limit) throw NotificationErrors.Fail("接收范围超过配置上限");
                foreach (var row in rows)
                    if (row.Contains("user") && row["user"].IsObjectId) ids.Add(row["user"].AsObjectId);
            }
        }

        if (audience.TryGetValue("site_roles", out var siteRoles) && siteRoles.AsBsonArray.Count == 1)
        {
            var role = siteRoles.AsBsonArray[0].AsString;
            var query = role == "admin"
                ? Builders<BsonDocument>.Filter.Eq("admin", true)
                : Builders<BsonDocument>.Filter.Ne("admin", true);
            var users = await _users.Find(query)
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .ToListAsync(ct);
            var allowed = users.Select(user => user["_id"].AsObjectId).ToHashSet();
            ids = ids is null ? allowed : ids.Intersect(allowed).ToHashSet();
        }

        if (audience.Text("mode") == "manual")
        {
            var selected = ParseIds(audience, "user_ids").ToHashSet();
            ids = ids is null ? selected : ids.Intersect(selected).ToHashSet();
        }

        var finalFilter = Builders<BsonDocument>.Filter.Ne("b", true);
        if (ids is not null) finalFilter &= Builders<BsonDocument>.Filter.In("_id", ids);
        var final = await _users.Find(finalFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Limit(limit + 1)
            .ToListAsync(ct);
        if (final.Count > limit) throw NotificationErrors.Fail("接收范围超过配置上限");
        return final.Select(user => user["_id"].AsObjectId).ToList();
    }

    private static List<string> Positions(BsonDocument audience)
    {
        if (audience.TryGetValue("worker_qualifications", out var workers) && workers.AsBsonArray.Count > 0)
            return workers.AsBsonArray.Select(item => item.AsString).ToList();
        if (audience.TryGetValue("tags", out var tags) && tags.AsBsonArray.Count > 0)
            return tags.AsBsonArray.Select(item => item.AsString).ToList();
        return [];
    }

    private List<ObjectId> ParseIds(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value) || value.IsBsonNull) return [];
        return value.AsBsonArray.Where(item => item.IsString || item.IsObjectId)
            .Select(item => item.IsObjectId ? item.AsObjectId : NotificationReadService.Oid(item.AsString)).ToList();
    }

    private void EnsureManualMatches(BsonDocument note, IReadOnlyCollection<ObjectId> ids)
    {
        var audience = note["audience"].AsBsonDocument;
        if (audience.Text("mode") != "manual") return;
        var selected = ParseIds(audience, "user_ids");
        if (selected.Count != ids.Count)
            throw NotificationErrors.Fail("指定用户不在有效接收范围内");
    }

    // ------------------------------------------------------------------
    // Live recipient permission
    // ------------------------------------------------------------------

    public async Task<bool> RecipientEligibleAsync(BsonDocument note, BsonDocument user, CancellationToken ct)
    {
        if (!NotificationReadService.IsActive(user)) return false;
        var audience = note["audience"].AsBsonDocument;
        var category = note.Text("category");

        if (audience.Text("mode") == "manual" &&
            !ParseIds(audience, "user_ids").Contains(user["_id"].AsObjectId))
            return false;

        if (audience.TryGetValue("site_roles", out var siteRoles) && siteRoles.AsBsonArray.Count > 0)
        {
            var role = user.Flag("admin") ? "admin" : "member";
            if (!siteRoles.AsBsonArray.Any(item => item.IsString && item.AsString == role)) return false;
        }

        var teamIds = ParseIds(audience, "team_ids");
        if (teamIds.Count > 0)
        {
            var count = await _teamMembers.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.In("team", teamIds)
                & Builders<BsonDocument>.Filter.Eq("user", user["_id"])
                & Builders<BsonDocument>.Filter.Eq("status", "active"), cancellationToken: ct);
            if (count == 0) return false;
        }

        var projectIds = ParseIds(audience, "project_ids");
        if (projectIds.Count > 0)
        {
            var count = await _projectMembers.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.In("project", projectIds)
                & Builders<BsonDocument>.Filter.Eq("user", user["_id"])
                & Builders<BsonDocument>.Filter.Eq("status", "active"), cancellationToken: ct);
            if (count == 0) return false;
        }

        if (category is "system" or "personal") return true;

        var positions = Positions(audience).ToHashSet(StringComparer.Ordinal);
        var wanted = audience.TryGetValue("base_roles", out var roles)
            ? roles.AsBsonArray.Select(item => item.AsString).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        if (category == "team")
        {
            var teamId = NullableId(note, "team_id");
            if (teamId is null) return false;
            if (await _teams.Find(Builders<BsonDocument>.Filter.Eq("_id", teamId.Value)).FirstOrDefaultAsync(ct) is null)
                return false;
            var member = await _teamMembers.Find(
                Builders<BsonDocument>.Filter.Eq("team", teamId.Value)
                & Builders<BsonDocument>.Filter.Eq("user", user["_id"])
                & Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
            if (member is null) return false;
            if (wanted.Count > 0 && !wanted.Contains(member.Text("base_tag"))) return false;
            if (positions.Count > 0 && !positions.Overlaps(StringList(member, "worker_qualifications")))
                return false;
            return true;
        }

        if (category == "project")
        {
            var projectId = NullableId(note, "project_id");
            if (projectId is null) return false;
            var project = await _projects.Find(Builders<BsonDocument>.Filter.Eq("_id", projectId.Value)).FirstOrDefaultAsync(ct);
            if (project is null) return false;
            if (note.Text("event_type") == "proofread_feedback" &&
                note["payload"].AsBsonDocument.Flag("cc_myself") &&
                SameId(note, "actor_id", user["_id"]))
                return await read.CanViewProofreadAsync(user, "project", projectId.Value.ToString(), ct);
            var member = await _projectMembers.Find(
                Builders<BsonDocument>.Filter.Eq("project", projectId.Value)
                & Builders<BsonDocument>.Filter.Eq("user", user["_id"])
                & Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
            if (member is null) return false;
            var baseTags = StringList(member, "tags");
            var baseSet = baseTags.Intersect(new[] { "creator", "admin" }).ToHashSet(StringComparer.Ordinal);
            if (baseSet.Count == 0) baseSet.Add("member");
            if (wanted.Count > 0 && !wanted.Overlaps(baseSet)) return false;
            if (positions.Count > 0 && !positions.Overlaps(baseTags)) return false;
            return true;
        }

        return false;
    }

    public async Task<bool> AuthorAllowedAsync(BsonDocument note, CancellationToken ct)
    {
        var actorId = NullableId(note, "actor_id");
        if (actorId is null) return false;
        var actor = await _users.Find(Builders<BsonDocument>.Filter.Eq("_id", actorId.Value)).FirstOrDefaultAsync(ct);
        if (!NotificationReadService.IsActive(actor)) return false;

        if (note.Text("event_type") == "proofread_feedback")
        {
            var projectId = NullableId(note, "project_id");
            return projectId is not null &&
                   await read.CanViewProofreadAsync(actor!, "project", projectId.Value.ToString(), ct);
        }
        if (note.Text("category") == "personal")
            return NotificationPersonalSources.IsRegistered(note.Text("source"));

        var scope = (NullableId(note, "project_id") ?? NullableId(note, "team_id"))?.ToString();
        try
        {
            return await read.CanPublishAsync(actor!, note.Text("category"), scope, ct);
        }
        catch (ApiError)
        {
            return false;
        }
    }

    public async Task<string> EmailReasonAsync(BsonDocument user, string category, CancellationToken ct)
    {
        var email = user.Text("e");
        if (string.IsNullOrEmpty(email)) return "no_email";
        if (!UserEmailEnabled) return "channel_disabled";

        var preference = await _preferences.Find(
            Builders<BsonDocument>.Filter.Eq("user_id", user["_id"])).FirstOrDefaultAsync(ct);
        if (preference is not null && preference.TryGetValue("categories", out var categories) &&
            categories.IsBsonDocument && categories.AsBsonDocument.TryGetValue(category, out var enabled) &&
            !enabled.ToBoolean())
            return "preference_disabled";

        if (!TrustExistingEmails)
        {
            var digest = Sha256Hex(email.Trim().ToLowerInvariant());
            if (await _verified.Find(Builders<BsonDocument>.Filter.Eq("digest", digest)).FirstOrDefaultAsync(ct) is null)
                return "unverified_email";
        }
        return "";
    }

    public Task MarkVerifiedAsync(string email, CancellationToken ct)
    {
        var digest = Sha256Hex(email.Trim().ToLowerInvariant());
        return _verified.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("digest", digest),
            Builders<BsonDocument>.Update.SetOnInsert("digest", digest)
                .SetOnInsert("verified_at", read.UtcNow),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    // ------------------------------------------------------------------
    // Ordinary management
    // ------------------------------------------------------------------

    public async Task OrdinaryManageAsync(BsonDocument actor, BsonDocument note, CancellationToken ct)
    {
        if (note.Text("event_type") == "proofread_feedback" &&
            SameId(note, "actor_id", actor["_id"]) &&
            await read.CanViewProofreadAsync(actor, "project", NullableId(note, "project_id")?.ToString(), ct))
            return;

        var scopeId = (NullableId(note, "project_id") ?? NullableId(note, "team_id"))?.ToString();
        if (!await read.CanPublishAsync(actor, note.Text("category"), scopeId, ct))
            throw NotificationErrors.Forbidden("没有当前范围的发件管理权限");
        if (!SameId(note, "actor_id", actor["_id"]) && !await ScopeOwnerAsync(actor, note, ct))
            throw NotificationErrors.Forbidden("只能管理本人发送或本人创建范围的通知");
    }

    public async Task<bool> ScopeOwnerAsync(BsonDocument actor, BsonDocument note, CancellationToken ct)
    {
        var category = note.Text("category");
        if (category == "team")
        {
            var teamId = NullableId(note, "team_id");
            if (teamId is null) return false;
            var member = await _teamMembers.Find(
                Builders<BsonDocument>.Filter.Eq("team", teamId.Value)
                & Builders<BsonDocument>.Filter.Eq("user", actor["_id"])
                & Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
            return member is not null && member.Text("base_tag") == "creator";
        }
        if (category == "project")
        {
            var projectId = NullableId(note, "project_id");
            if (projectId is null) return false;
            var project = await _projects.Find(Builders<BsonDocument>.Filter.Eq("_id", projectId.Value)).FirstOrDefaultAsync(ct);
            if (project is null) return false;
            var owner = NullableId(project, "ou");
            if (owner is not null) return owner == actor["_id"].AsObjectId;
            var member = await _projectMembers.Find(
                Builders<BsonDocument>.Filter.Eq("project", projectId.Value)
                & Builders<BsonDocument>.Filter.Eq("user", actor["_id"])
                & Builders<BsonDocument>.Filter.Eq("status", "active")).FirstOrDefaultAsync(ct);
            return member is not null && StringList(member, "tags").Contains("creator");
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    internal IMongoCollection<BsonDocument> Notifications => _notifications;
    internal IMongoCollection<BsonDocument> Chunks => _chunks;
    internal IMongoCollection<BsonDocument> Receipts => _receipts;
    internal IMongoCollection<BsonDocument> Deliveries => _deliveries;
    internal IMongoCollection<BsonDocument> Audits => _audits;
    internal INotificationMailTransport Mail => mail;
    internal NotificationReadService Read => read;
    internal NotificationContent Content => content;

    internal static Dictionary<string, JsonElement> Object(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw NotificationErrors.Fail("请求必须是 JSON 对象");
        return data.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
    }

    internal static string? Text(IReadOnlyDictionary<string, JsonElement> data, string key) =>
        data.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static DateTime? DateValue(IReadOnlyDictionary<string, JsonElement> data, string key)
    {
        if (!data.TryGetValue(key, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String)
            throw NotificationErrors.Fail("日期必须是带时区的 ISO 8601 格式");
        var text = element.GetString();
        if (string.IsNullOrEmpty(text)) return null;
        if (!OffsetSuffix.IsMatch(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw NotificationErrors.Fail("日期必须是带时区的 ISO 8601 格式");
        return value.UtcDateTime;
    }

    internal static ObjectId? NullableId(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && value.IsObjectId ? value.AsObjectId : null;

    internal static bool HasValue(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && !value.IsBsonNull;

    internal static bool SameId(BsonDocument document, string key, BsonValue id) =>
        document.TryGetValue(key, out var value) && value.IsObjectId && value.AsObjectId == id.AsObjectId;

    internal static HashSet<string> StringList(BsonDocument document, string key)
    {
        if (!document.TryGetValue(key, out var value) || !value.IsBsonArray) return new HashSet<string>(StringComparer.Ordinal);
        return value.AsBsonArray.Where(item => item.IsString)
            .Select(item => item.AsString).ToHashSet(StringComparer.Ordinal);
    }

    internal static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;

    internal static int PageSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 20;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size is < 1 or > 100)
            throw NotificationErrors.Fail("分页数量必须在 1–100 之间");
        return size;
    }

    internal static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => null
    };

    internal static int IntegerConfig(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    internal static int RuneCount(string value) => value.EnumerateRunes().Count();

    internal static bool IsDuplicateKey(Exception error) => error switch
    {
        MongoWriteException write => write.WriteError?.Category == ServerErrorCategory.DuplicateKey,
        MongoBulkWriteException bulk => bulk.WriteErrors.Any(item => item.Category == ServerErrorCategory.DuplicateKey),
        MongoCommandException command => command.Code == 11000,
        _ => false
    };

    internal static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Python-compatible fingerprint used for publish idempotency.
/// json.dumps(value, sort_keys=True, default=str) with ensure_ascii=True.
/// </summary>
public static class NotificationFingerprint
{
    public static string Compute(
        IReadOnlyDictionary<string, JsonElement> data, string eventType, BsonDocument payload)
    {
        var builder = new StringBuilder();
        builder.Append("{\"data\": ");
        WriteObject(builder, data);
        builder.Append(", \"payload\": ");
        WriteBson(builder, payload);
        builder.Append(", \"type\": ");
        WriteString(builder, eventType);
        builder.Append('}');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void WriteObject(StringBuilder builder, IReadOnlyDictionary<string, JsonElement> data)
    {
        builder.Append('{');
        var first = true;
        foreach (var (key, value) in data.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first) builder.Append(", ");
            first = false;
            WriteString(builder, key);
            builder.Append(": ");
            WriteElement(builder, value);
        }
        builder.Append('}');
    }

    private static void WriteElement(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                builder.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(", ");
                    first = false;
                    WriteString(builder, property.Name);
                    builder.Append(": ");
                    WriteElement(builder, property.Value);
                }
                builder.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                builder.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) builder.Append(", ");
                    first = false;
                    WriteElement(builder, item);
                }
                builder.Append(']');
                break;
            }
            case JsonValueKind.String:
                WriteString(builder, value.GetString()!);
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer)) builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                else if (value.TryGetDecimal(out var number) && value.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0)
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                else if (double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                    builder.Append(PythonFloat(real));
                else builder.Append(value.GetRawText());
                break;
        }
    }

    private static void WriteBson(StringBuilder builder, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                builder.Append('{');
                var documentFirst = true;
                foreach (var element in value.AsBsonDocument.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!documentFirst) builder.Append(", ");
                    documentFirst = false;
                    WriteString(builder, element.Name);
                    builder.Append(": ");
                    WriteBson(builder, element.Value);
                }
                builder.Append('}');
                break;
            case BsonType.Array:
                builder.Append('[');
                var arrayFirst = true;
                foreach (var item in value.AsBsonArray)
                {
                    if (!arrayFirst) builder.Append(", ");
                    arrayFirst = false;
                    WriteBson(builder, item);
                }
                builder.Append(']');
                break;
            case BsonType.String:
                WriteString(builder, value.AsString);
                break;
            case BsonType.Boolean:
                builder.Append(value.AsBoolean ? "true" : "false");
                break;
            case BsonType.Null:
            case BsonType.Undefined:
                builder.Append("null");
                break;
            case BsonType.Int32:
                builder.Append(value.AsInt32.ToString(CultureInfo.InvariantCulture));
                break;
            case BsonType.Int64:
                builder.Append(value.AsInt64.ToString(CultureInfo.InvariantCulture));
                break;
            case BsonType.Double:
                builder.Append(PythonFloat(value.AsDouble));
                break;
            case BsonType.Decimal128:
                builder.Append(value.AsDecimal128.ToString());
                break;
            case BsonType.ObjectId:
                WriteString(builder, value.AsObjectId.ToString());
                break;
            case BsonType.DateTime:
                WriteString(builder, PythonDateTime(value.ToUniversalTime()));
                break;
            default:
                WriteString(builder, value.ToString() ?? "");
                break;
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            switch (scalar)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (scalar < 0x20 || scalar > 0x7E)
                    {
                        if (scalar <= 0xFFFF)
                        {
                            builder.Append("\\u").Append(scalar.ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            var offset = scalar - 0x10000;
                            var high = 0xD800 + (offset >> 10);
                            var low = 0xDC00 + (offset & 0x3FF);
                            builder.Append("\\u").Append(high.ToString("x4", CultureInfo.InvariantCulture))
                                .Append("\\u").Append(low.ToString("x4", CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        builder.Append((char)scalar);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    internal static string PythonFloat(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";
        var absolute = Math.Abs(value);
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (absolute >= 1e16 || absolute < 1e-4)
        {
            var raw = value.ToString("R", CultureInfo.InvariantCulture);
            var index = raw.IndexOfAny(['E', 'e']);
            string mantissa;
            int exponent;
            if (index >= 0)
            {
                mantissa = raw[..index];
                exponent = int.Parse(raw[(index + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            else
            {
                exponent = (int)Math.Floor(Math.Log10(absolute) + 1e-12);
                mantissa = (value / Math.Pow(10, exponent)).ToString("R", CultureInfo.InvariantCulture);
            }
            if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
            return $"{mantissa}e{(exponent >= 0 ? "+" : "-")}{Math.Abs(exponent):00}";
        }
        if (!text.Contains('.')) text += ".0";
        return text;
    }

    private static string PythonDateTime(DateTime value)
    {
        var text = value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return value.Millisecond == 0 && value.Ticks % TimeSpan.TicksPerSecond == 0
            ? text
            : text + "." + (value.Ticks % TimeSpan.TicksPerSecond / 10).ToString("D6", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Trusted in-process registry for personal notification sources.
/// </summary>
public static class NotificationPersonalSources
{
    private static readonly Regex NamePattern = new(@"\Amodule:[a-z][a-z0-9_]{0,63}\z", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, Func<BsonDocument, IReadOnlyDictionary<string, JsonElement>, bool>> Validators =
        new(StringComparer.Ordinal);

    public static void RegisterSource(
        string name, Func<BsonDocument, IReadOnlyDictionary<string, JsonElement>, bool> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (!NamePattern.IsMatch(name)) throw new ArgumentException("invalid notification source registration", nameof(name));
        if (!Validators.TryAdd(name, validator)) throw new ArgumentException("invalid notification source registration", nameof(name));
    }

    public static bool IsRegistered(string name) => Validators.ContainsKey(name);

    public static bool Validate(string name, BsonDocument actor, IReadOnlyDictionary<string, JsonElement> data) =>
        Validators.TryGetValue(name, out var validator) && validator(actor, data);
}
