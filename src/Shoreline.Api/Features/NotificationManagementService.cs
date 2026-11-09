using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

// N3 port of the sent/admin management reads and the notification sync payload.
// Authorization stays delegated to N1's NotificationReadService and N2's
// NotificationPublishService; this service only shapes the legacy queries.
public sealed partial class NotificationManagementService(
    MongoStore store,
    NotificationReadService read,
    NotificationPublishService publish,
    TimeProvider clock)
{
    private static readonly string[] DeliveryStates =
    [
        "pending", "leased", "accepted", "retry_wait",
        "skipped", "failed", "unknown", "cancelled"
    ];

    private static readonly string[] SyncViews =
    [
        "", "inbox", "scope", "admin", "inbox_detail",
        "scope_detail", "admin_detail", "compose"
    ];

    private static readonly Regex HexId = new(@"\A[a-fA-F0-9]{24}\z", RegexOptions.Compiled);
    private static readonly Regex TimeZoneSuffix = new(@"(?:[zZ]|[+-]\d{2}:?\d{2})\z", RegexOptions.Compiled);

    private static readonly HashSet<string> DeliveryStateSet =
        new(DeliveryStates, StringComparer.Ordinal);

    private IMongoCollection<BsonDocument> Notifications => store.Collection("notification");
    private IMongoCollection<BsonDocument> Deliveries => store.Collection("notification_delivery");

    // Kept as an instance property so tests can force the safe-cap branch
    // without inserting twenty thousand delivery rows. Production uses 20,000.
    public int DeliveryStateLimit { get; init; } = 20000;

    // Public only so the compatibility tests can pin the exact Python
    // json.dumps vectors without duplicating the canonicalizer.
    public static string CanonicalJson(object? value) => NotificationManagementJson.Canonical(value);

    public static string CanonicalRevision(object? value) => NotificationManagementJson.Revision(value);

    public async Task<Dictionary<string, object?>> SentAsync(
        BsonDocument user, NotificationManagementQuery query, CancellationToken ct) =>
        await PageAsync(user, query, admin: false, ct);

    public async Task<Dictionary<string, object?>> AdminListAsync(
        BsonDocument user, NotificationManagementQuery query, CancellationToken ct) =>
        await PageAsync(user, query, admin: true, ct);

    public async Task<Dictionary<string, object?>> DetailAsync(
        BsonDocument user, string notificationId, bool admin, CancellationToken ct)
    {
        read.EnsureEnabled();
        if (admin) RequireAdministrator(user);
        var note = await read.GetNoteAsync(notificationId, ct);
        if (admin)
        {
            await read.AuditAsync(user["_id"].AsObjectId, "admin_read", note["_id"].AsObjectId,
                null, null, ct);
        }
        else
        {
            await publish.OrdinaryManageAsync(user, note, ct);
        }
        return await read.DetailAsync(note, user, management: true, full: true, ct: ct);
    }

    public async Task<Dictionary<string, object?>> SyncAsync(
        BsonDocument user, NotificationSyncQuery query, CancellationToken ct)
    {
        if (!SyncViews.Contains(query.View ?? "", StringComparer.Ordinal))
            throw NotificationErrors.Fail("同步视图无效");

        var view = query.View ?? "";
        var scoped = view is "scope" or "scope_detail" or "compose" &&
            !string.IsNullOrEmpty(query.ScopeId);
        var capabilities = await read.CapabilitiesAsync(
            user,
            scoped ? query.Category ?? "system" : "system",
            scoped ? query.ScopeId : null,
            ct);
        var result = new Dictionary<string, object?>
        {
            ["enabled"] = capabilities.TryGetValue("enabled", out var enabled) && enabled is true,
            ["capabilities"] = capabilities,
            ["counts"] = await read.UnreadCountsAsync(user, ct)
        };

        if (result["enabled"] is true)
        {
            var page = query.Page ?? new NotificationManagementQuery();
            if (view is "scope" or "admin")
            {
                page = page with
                {
                    Category = query.Category ?? page.Category,
                    ScopeId = query.ScopeId ?? page.ScopeId
                };
            }
            try
            {
                switch (view)
                {
                    case "inbox":
                        result["page"] = await read.InboxAsync(user, page.Q,
                            page.Category, page.Cursor, page.Unread,
                            page.Archived, page.Limit, ct);
                        break;
                    case "scope":
                    case "admin":
                        result["page"] = await PageAsync(user, page, view == "admin", ct);
                        break;
                    case "inbox_detail":
                        var (note, receipt) = await read.GetReceiptAsync(
                            user, RequiredNotificationId(query.NotificationId), ct);
                        result["notice"] = await read.DetailAsync(note, user,
                            receipt: receipt, full: true, ct: ct);
                        break;
                    case "scope_detail":
                    case "admin_detail":
                        if (view == "admin_detail") RequireAdministrator(user);
                        var managed = await read.GetNoteAsync(
                            RequiredNotificationId(query.NotificationId), ct);
                        if (view == "admin_detail")
                        {
                            await read.AuditAsync(user["_id"].AsObjectId, "admin_read",
                                managed["_id"].AsObjectId, null, null, ct);
                        }
                        else
                        {
                            await publish.OrdinaryManageAsync(user, managed, ct);
                        }
                        result["notice"] = await read.DetailAsync(managed, user,
                            management: true, full: true, ct: ct);
                        break;
                }
            }
            catch (ApiError error) when (error.Error == NotificationErrors.ErrorName)
            {
                // Permission loss/revocation must not echo the cached private
                // notification that used to be visible.
                result["view_error"] = new Dictionary<string, object?>
                {
                    ["status"] = error.Status,
                    ["message"] = error.ResponseMessage
                };
            }
        }

        var revision = NotificationManagementJson.Revision(result);
        if (string.Equals(revision, query.Revision, StringComparison.Ordinal))
            return new Dictionary<string, object?>
            {
                ["unchanged"] = true,
                ["revision"] = revision
            };
        result["revision"] = revision;
        return result;
    }

    private async Task<Dictionary<string, object?>> PageAsync(
        BsonDocument user,
        NotificationManagementQuery query,
        bool admin,
        CancellationToken ct)
    {
        read.EnsureEnabled();
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Empty;

        if (admin)
        {
            RequireAdministrator(user);
            await read.ThrottleAsync(user, "admin_search", 120, ct);
            await read.AuditAsync(user["_id"].AsObjectId, "admin_search", null, null, null, ct);
        }
        else
        {
            var category = query.Category;
            var scopeId = query.ScopeId;
            var canSend = await read.CanPublishAsync(user, category ?? "", scopeId, ct);
            if (!canSend && !await read.CanViewProofreadAsync(user, category ?? "", scopeId, ct))
                throw NotificationErrors.Forbidden("没有当前范围的发件查看权限");

            if (!await ScopeOwnerAsync(user, category, scopeId, ct))
                filter &= Builders<BsonDocument>.Filter.Eq("actor_id", user["_id"]);
            if (!canSend)
                filter &= Builders<BsonDocument>.Filter.Eq("event_type", "proofread_feedback");
            if (category == "team")
            {
                filter &= Builders<BsonDocument>.Filter.Eq("team_id",
                    NotificationReadService.Oid(scopeId));
            }
            else if (category == "project")
            {
                filter &= Builders<BsonDocument>.Filter.Eq("project_id",
                    NotificationReadService.Oid(scopeId));
            }
            else
            {
                filter &= Builders<BsonDocument>.Filter.Eq("actor_id", user["_id"]);
            }
        }

        filter &= SearchFilter(query.Q, query.Category);
        filter = await ApplyDeliveryStateAsync(filter, query.DeliveryState, ct);
        filter = ApplyExpiry(filter, query.Expired);
        filter = ApplyOidFilter(filter, "team_id", query.TeamId);
        filter = ApplyOidFilter(filter, "project_id", query.ProjectId);
        filter = ApplyOidFilter(filter, "actor_id", query.ActorId);
        filter = ApplyTextFilter(filter, "state", query.State);
        filter = ApplyTextFilter(filter, "source", query.Source);
        filter = ApplyTextFilter(filter, "event_type", query.EventType);
        filter = ApplyRevoked(filter, query.Revoked);
        filter = ApplyDateRange(filter, query.From, query.To);

        if (!string.IsNullOrEmpty(query.Cursor))
            filter &= Builders<BsonDocument>.Filter.Lt("_id",
                NotificationReadService.Oid(query.Cursor));

        var limit = PageSize(query.Limit);
        var docs = await Notifications
            .Find(filter, new FindOptions { MaxTime = TimeSpan.FromMilliseconds(2000) })
            .Project(Builders<BsonDocument>.Projection
                .Exclude("nodes")
                .Exclude("body")
                .Exclude("search_tokens")
                .Exclude("payload.html")
                .Exclude("payload.text"))
            .Sort(new BsonDocument("_id", -1))
            .Limit(limit + 1)
            .ToListAsync(ct);

        string? nextCursor = docs.Count > limit
            ? docs[limit - 1]["_id"].AsObjectId.ToString()
            : null;
        var items = docs.Take(limit).ToList();
        return new Dictionary<string, object?>
        {
            ["items"] = await read.DetailManyAsync(items, user, searchQuery: query.Q ?? "",
                management: true, ct: ct),
            ["next_cursor"] = nextCursor
        };
    }

    private async Task<bool> ScopeOwnerAsync(
        BsonDocument user, string? category, string? scopeId, CancellationToken ct)
    {
        if (category is not ("team" or "project")) return false;
        var scope = NotificationReadService.Oid(scopeId);
        var probe = new BsonDocument { ["category"] = category };
        if (category == "team") probe["team_id"] = scope;
        else probe["project_id"] = scope;
        return await publish.ScopeOwnerAsync(user, probe, ct);
    }

    private async Task<FilterDefinition<BsonDocument>> ApplyDeliveryStateAsync(
        FilterDefinition<BsonDocument> filter, string? deliveryState, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deliveryState)) return filter;
        if (!DeliveryStateSet.Contains(deliveryState))
            throw NotificationErrors.Fail("邮件状态无效");

        var rows = await Deliveries
            .Aggregate(new AggregateOptions { MaxTime = TimeSpan.FromMilliseconds(2000) })
            .Match(Builders<BsonDocument>.Filter.Eq("state", deliveryState))
            .Group(new BsonDocument("_id", "$notification_id"))
            .Limit(DeliveryStateLimit + 1)
            .ToListAsync(ct);
        if (rows.Count > DeliveryStateLimit)
            throw NotificationErrors.Fail("邮件状态检索超过安全上限", 422);

        var ids = new BsonArray(rows
            .Where(row => row.TryGetValue("_id", out var id) && id.IsObjectId)
            .Select(row => (BsonValue)row["_id"].AsObjectId));
        return filter & Builders<BsonDocument>.Filter.In("_id", ids);
    }

    private FilterDefinition<BsonDocument> ApplyExpiry(
        FilterDefinition<BsonDocument> filter, string? expired)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (expired == "true")
            return filter & Builders<BsonDocument>.Filter.Lte("expires_at", now);
        if (expired == "false")
            return filter & (Builders<BsonDocument>.Filter.Eq("expires_at", BsonNull.Value) |
                Builders<BsonDocument>.Filter.Gt("expires_at", now));
        return filter;
    }

    private static FilterDefinition<BsonDocument> ApplyOidFilter(
        FilterDefinition<BsonDocument> filter, string field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return filter;
        return filter & Builders<BsonDocument>.Filter.Eq(field,
            NotificationReadService.Oid(value));
    }

    private static FilterDefinition<BsonDocument> ApplyTextFilter(
        FilterDefinition<BsonDocument> filter, string field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return filter;
        return filter & Builders<BsonDocument>.Filter.Eq(field, TakeRunes(value, 0, 100));
    }

    private static FilterDefinition<BsonDocument> ApplyRevoked(
        FilterDefinition<BsonDocument> filter, string? revoked)
    {
        if (revoked == "true")
            return filter & Builders<BsonDocument>.Filter.Ne("revoked_at", BsonNull.Value);
        if (revoked == "false")
            return filter & Builders<BsonDocument>.Filter.Eq("revoked_at", BsonNull.Value);
        return filter;
    }

    private static FilterDefinition<BsonDocument> ApplyDateRange(
        FilterDefinition<BsonDocument> filter, string? from, string? to)
    {
        if (!string.IsNullOrEmpty(from))
            filter &= Builders<BsonDocument>.Filter.Gte("created_at", ParseDate(from));
        if (!string.IsNullOrEmpty(to))
            filter &= Builders<BsonDocument>.Filter.Lte("created_at", ParseDate(to));
        return filter;
    }

    private static FilterDefinition<BsonDocument> SearchFilter(string? query, string? category)
    {
        var filter = Builders<BsonDocument>.Filter.Empty;
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length > 0)
        {
            if (RuneCount(trimmed) > 100)
                throw NotificationErrors.Fail("搜索词过长");
            if (trimmed.Length == 24 && HexId.IsMatch(trimmed))
            {
                filter &= Builders<BsonDocument>.Filter.Eq("_id",
                    NotificationReadService.Oid(trimmed));
            }
            else
            {
                var value = NotificationContent.Normalize(trimmed);
                if (RuneCount(value) < 2)
                    throw NotificationErrors.Fail("搜索词至少两个字符");
                var tokens = NotificationContent.SearchTokens(value).Take(30).ToArray();
                filter &= Builders<BsonDocument>.Filter.All("search_tokens", tokens);
                filter &= Builders<BsonDocument>.Filter.Regex("search_text",
                    new BsonRegularExpression(Regex.Escape(value)));
            }
        }
        if (!string.IsNullOrEmpty(category))
        {
            if (!NotificationReadService.Categories.Contains(category, StringComparer.Ordinal))
                throw NotificationErrors.Fail("通知分类无效");
            filter &= Builders<BsonDocument>.Filter.Eq("category", category);
        }
        return filter;
    }

    private static DateTime ParseDate(string value)
    {
        if (!TimeZoneSuffix.IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw NotificationErrors.Fail("日期必须是带时区的 ISO 8601 格式");
        return parsed.UtcDateTime;
    }

    private static int PageSize(string? limit)
    {
        if (limit is null) return 20;
        if (int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var value) && value is >= 1 and <= 100)
            return value;
        throw NotificationErrors.Fail("分页数量必须在 1–100 之间");
    }

    private static string RequiredNotificationId(string? value) =>
        string.IsNullOrEmpty(value)
            ? throw NotificationErrors.Fail("无效的对象标识符")
            : value;

    private static void RequireAdministrator(BsonDocument user)
    {
        if (!NotificationReadService.IsAdministrator(user))
            throw NotificationErrors.Forbidden("需要全站管理员权限");
    }

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static string TakeRunes(string value, int skip, int count)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var rune in value.EnumerateRunes().Skip(skip).Take(count))
            builder.Append(rune.ToString());
        return builder.ToString();
    }
}

public sealed record NotificationManagementQuery(
    string? Q = null,
    string? Category = null,
    string? ScopeId = null,
    string? DeliveryState = null,
    string? Expired = null,
    string? TeamId = null,
    string? ProjectId = null,
    string? ActorId = null,
    string? State = null,
    string? Source = null,
    string? EventType = null,
    string? Revoked = null,
    string? From = null,
    string? To = null,
    string? Cursor = null,
    string? Limit = null,
    string? Unread = null,
    string? Archived = null)
{
    public static NotificationManagementQuery FromQuery(IQueryCollection query) => new(
        query["q"].FirstOrDefault(),
        query["category"].FirstOrDefault(),
        query["scope_id"].FirstOrDefault(),
        query["delivery_state"].FirstOrDefault(),
        query["expired"].FirstOrDefault(),
        query["team_id"].FirstOrDefault(),
        query["project_id"].FirstOrDefault(),
        query["actor_id"].FirstOrDefault(),
        query["state"].FirstOrDefault(),
        query["source"].FirstOrDefault(),
        query["event_type"].FirstOrDefault(),
        query["revoked"].FirstOrDefault(),
        query["from"].FirstOrDefault(),
        query["to"].FirstOrDefault(),
        query["cursor"].FirstOrDefault(),
        query["limit"].FirstOrDefault(),
        query["unread"].FirstOrDefault(),
        query["archived"].FirstOrDefault());
}

public sealed record NotificationSyncQuery(
    string? View = null,
    string? Category = null,
    string? ScopeId = null,
    string? NotificationId = null,
    string? Revision = null,
    NotificationManagementQuery? Page = null)
{
    public static NotificationSyncQuery From(IQueryCollection query) => new(
        query["view"].FirstOrDefault(),
        query["category"].FirstOrDefault(),
        query["scope_id"].FirstOrDefault(),
        query["notification_id"].FirstOrDefault(),
        query["revision"].FirstOrDefault(),
        NotificationManagementQuery.FromQuery(query));
}
