using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Notifications;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

// End-to-end parity for site notifications + notification mail, mirroring the
// Python oracle (app/services/notifications.py, app/services/notification_mail.py,
// app/tasks/notification.py, app/apis/notification.py).
//
// Everything runs against disposable local Mongo through LocalServices with a
// fake INotificationMailTransport: no Mailpit/SMTP traffic is needed because the
// production MailKit transport always requires STARTTLS/SMTPS while the
// disposable Mailpit listener offers neither. What IS asserted end to end is
// the dispatch decision (who gets a delivery row, with which message id), the
// scan/lease state machine, and the exact retry/backoff/terminal states.
[Collection("local-services")]
public sealed class NotificationE2ETests
{
    private readonly LocalServices _services;
    private readonly NotificationClock _clock;
    private readonly IConfiguration _configuration;
    private readonly IdentityPermissions _permissions;
    private readonly NotificationContent _content;
    private readonly NotificationReadService _read;
    private readonly FakeMailTransport _mail = new();
    private readonly NotificationPublishService _publish;

    public NotificationE2ETests(LocalServices services)
    {
        _services = services;
        _clock = new NotificationClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        _configuration = Configuration(trust: true, userEmail: true);
        _permissions = new IdentityPermissions(services.Store, _clock);
        _content = new NotificationContent(services.Store, _permissions, _configuration);
        _read = new NotificationReadService(services.Store, _permissions, _content, _configuration, _clock);
        _publish = new NotificationPublishService(services.Store, _content, _read, _configuration, _mail);
    }

    private sealed class NotificationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeMailTransport : INotificationMailTransport
    {
        public List<NotificationMailMessage> Messages { get; } = [];
        public Func<NotificationMailMessage, Task<NotificationMailResult>>? Handler { get; set; }

        public Task<NotificationMailResult> SendAsync(NotificationMailMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Handler?.Invoke(message) ?? Task.FromResult(new NotificationMailResult("accepted", ""));
        }
    }

    private static IConfiguration Configuration(bool trust, bool userEmail = true) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Enabled"] = "true",
            ["Notifications:SiteOrigin"] = "https://example.test",
            ["Notifications:MaxAudience"] = "20000",
            ["Notifications:UserEmailEnabled"] = userEmail ? "true" : "false",
            ["Notifications:TrustExistingEmails"] = trust ? "true" : "false"
        }).Build();

    // ------------------------------------------------------------------
    // 1. Publish: event fields, targets, idempotency key, fingerprint
    // ------------------------------------------------------------------

    [Fact]
    public async Task Publish_RecordsEventFieldsTargetsAndDedupKey()
    {
        var actor = await InsertUserAsync(admin: true);
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team, actor, "creator");

        var system = await _publish.PublishAsync(actor, Json(SystemData("E2E system")), "e2e-key-fields-1", default);
        Assert.Equal("system", system.Text("category"));
        Assert.Equal("core", system.Text("source"));
        Assert.Equal("announcement", system.Text("event_type"));
        Assert.Equal(actor["_id"].AsObjectId, system["actor_id"].AsObjectId);
        Assert.True(system["team_id"].IsBsonNull && system["project_id"].IsBsonNull);
        var expectedKey = Sha256($"{actor["_id"].AsObjectId}:core:e2e-key-fields-1");
        Assert.Equal(expectedKey, system.Text("key"));
        Assert.False(string.IsNullOrEmpty(system.Text("fingerprint")));

        var project = await InsertProjectAsync(team, actor);
        await InsertProjectMemberAsync(project, actor, "creator");
        var teamNote = await _publish.PublishAsync(actor, Json(TeamData(team, "E2E team")), "e2e-key-fields-2", default);
        Assert.Equal("team", teamNote.Text("category"));
        Assert.Equal(team["_id"].AsObjectId, teamNote["team_id"].AsObjectId);
        var projectNote = await _publish.PublishAsync(actor, Json(ProjectData(project, "E2E project")), "e2e-key-fields-3", default);
        Assert.Equal("project", projectNote.Text("category"));
        Assert.Equal(project["_id"].AsObjectId, projectNote["project_id"].AsObjectId);
        Assert.Equal(team["_id"].AsObjectId, projectNote["team_id"].AsObjectId);

        // Same key + same content reuses the stored note (dedup); same key +
        // different content is a 409 with the NotificationError/4600 contract.
        var reused = await _publish.PublishAsync(actor, Json(SystemData("E2E system")), "e2e-key-fields-1", default);
        Assert.Equal(system["_id"], reused["_id"]);

        var conflict = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.PublishAsync(actor, Json(SystemData("E2E system changed")), "e2e-key-fields-1", default));
        Assert.Equal("NotificationError", conflict.Error);
        Assert.Equal(4600, conflict.Code);
        Assert.Equal(409, conflict.Status);
        Assert.Equal("相同操作键不能用于不同内容", conflict.Message);
    }

    // ------------------------------------------------------------------
    // 2. Dispatch: same target aggregates via idempotent fan-out
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_SameTargetAggregatesWithoutDuplicates()
    {
        var actor = await InsertUserAsync(admin: true);
        var members = new List<BsonDocument>();
        for (var index = 0; index < 3; index++) members.Add(await InsertUserAsync());
        var data = SystemData("E2E aggregate");
        data["audience"] = new Dictionary<string, object?>
        {
            ["mode"] = "manual",
            ["user_ids"] = members.Select(member => member["_id"].AsObjectId.ToString()).ToArray()
        };
        var note = await _publish.PublishAsync(actor, Json(data), "e2e-key-aggregate-1", default);
        var id = note["_id"].AsObjectId;

        // Two concurrent scans race for the same lease; only one may fan out.
        await Task.WhenAll(_publish.DispatchAsync(id, default), _publish.DispatchAsync(id, default));
        await _publish.ScanAsync(default);

        var receiptFilter = Builders<BsonDocument>.Filter.Eq("notification_id", id);
        Assert.Equal(3L, await CountAsync("notification_receipt", receiptFilter));
        var stored = await GetNoteAsync(id);
        Assert.Equal("published", stored.Text("state"));
        Assert.Equal(3L, stored.Integer("recipient_count"));
        // No mail was requested: fan-out creates receipts but no deliveries.
        Assert.Equal(0L, await CountAsync("notification_delivery", receiptFilter));
        Assert.DoesNotContain(_mail.Messages, message => message.MessageId != null && message.MessageId.Contains(id.ToString()));
    }

    // ------------------------------------------------------------------
    // 3. Pull: pagination, ordering, unread counts
    // ------------------------------------------------------------------

    [Fact]
    public async Task Inbox_PaginationSortingAndUnreadCounts()
    {
        var actor = await InsertUserAsync(admin: true);
        var viewer = await InsertUserAsync();
        for (var index = 0; index < 3; index++)
        {
            var data = SystemData($"E2E inbox {index}");
            data["audience"] = ManualAudience(viewer);
            var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-inbox-{index}-{Guid.NewGuid():N}", default);
            _clock.Now = _clock.Now.AddMinutes(1);
            await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        }

        var first = await _read.InboxAsync(viewer, null, null, null, null, null, "2", default);
        var items = (List<Dictionary<string, object?>>)first["items"]!;
        Assert.Equal(2, items.Count);
        Assert.NotNull(first["next_cursor"]);
        // Newest first (receipt _id descending): the last published note leads.
        Assert.Equal("E2E inbox 2", items[0]["title"]);

        var second = await _read.InboxAsync(viewer, null, null, (string?)first["next_cursor"], null, null, "2", default);
        var rest = (List<Dictionary<string, object?>>)second["items"]!;
        Assert.Single(rest);
        Assert.Equal("E2E inbox 0", rest[0]["title"]);

        var counts = await _read.UnreadCountsAsync(viewer, default);
        Assert.Equal(3L, Convert.ToInt64(counts["total"]));
        Assert.Equal(3L, Convert.ToInt64(counts["system"]));
    }

    // ------------------------------------------------------------------
    // 4. Pull: unread filter + archived soft-delete
    // ------------------------------------------------------------------

    [Fact]
    public async Task Inbox_UnreadFilterAndArchivedSoftDelete()
    {
        var actor = await InsertUserAsync(admin: true);
        var viewer = await InsertUserAsync();
        var ids = new List<ObjectId>();
        for (var index = 0; index < 3; index++)
        {
            var data = SystemData($"E2E filter {index}");
            data["audience"] = ManualAudience(viewer);
            var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-filter-{index}-{Guid.NewGuid():N}", default);
            ids.Add(note["_id"].AsObjectId);
            await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        }

        await _read.UpdateReceiptAsync(viewer, ids[0].ToString(), Json(new { read = true }), default);
        var unread = await _read.InboxAsync(viewer, null, null, null, "true", null, "20", default);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)unread["items"]!).Count);

        await _read.UpdateReceiptAsync(viewer, ids[1].ToString(), Json(new { archived = true }), default);
        var visible = await _read.InboxAsync(viewer, null, null, null, null, null, "20", default);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)visible["items"]!).Count);
        var archived = await _read.InboxAsync(viewer, null, null, null, null, "true", "20", default);
        var archivedItems = (List<Dictionary<string, object?>>)archived["items"]!;
        Assert.Single(archivedItems);
        Assert.True((bool)archivedItems[0]["archived"]!);
        // Archiving implies read: the archived note leaves the unread set.
        Assert.True((bool)archivedItems[0]["read"]!);
        var unreadAfter = await _read.InboxAsync(viewer, null, null, null, "true", null, "20", default);
        Assert.Single(((List<Dictionary<string, object?>>)unreadAfter["items"]!));
    }

    // ------------------------------------------------------------------
    // 5. Read: single read + foreign receipts are unreachable
    // ------------------------------------------------------------------

    [Fact]
    public async Task Read_SingleReadAndForeignReceiptIsolation()
    {
        var actor = await InsertUserAsync(admin: true);
        var owner = await InsertUserAsync();
        var stranger = await InsertUserAsync();
        var data = SystemData("E2E private");
        data["audience"] = ManualAudience(owner);
        var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-private-{Guid.NewGuid():N}", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        var id = note["_id"].AsObjectId.ToString();

        await _read.UpdateReceiptAsync(owner, id, Json(new { read = true }), default);
        var (_, receipt) = await _read.GetReceiptAsync(owner, id, default);
        Assert.False(receipt["read_at"].IsBsonNull);

        // The stranger has no receipt: detail, single-read and archived writes
        // all answer 404 instead of leaking the note.
        var missing = await Assert.ThrowsAsync<ApiError>(() => _read.GetReceiptAsync(stranger, id, default));
        Assert.Equal("NotificationError", missing.Error);
        Assert.Equal(4600, missing.Code);
        Assert.Equal(404, missing.Status);
        var foreignWrite = await Assert.ThrowsAsync<ApiError>(() =>
            _read.UpdateReceiptAsync(stranger, id, Json(new { read = true }), default));
        Assert.Equal(404, foreignWrite.Status);
        var strangerUnread = await _read.UnreadCountsAsync(stranger, default);
        Assert.Equal(0L, Convert.ToInt64(strangerUnread["total"]));
    }

    // ------------------------------------------------------------------
    // 6. Read: mark-all-read cutoff keeps later arrivals unread
    // ------------------------------------------------------------------

    [Fact]
    public async Task Read_MarkAllReadCutoffKeepsNewArrivalsUnread()
    {
        var actor = await InsertUserAsync(admin: true);
        var viewer = await InsertUserAsync();
        for (var index = 0; index < 2; index++)
        {
            var data = SystemData($"E2E markall {index}");
            data["audience"] = ManualAudience(viewer);
            var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-markall-{index}-{Guid.NewGuid():N}", default);
            await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        }

        var marked = await _read.MarkReadAsync(viewer, Json(new { }), null, null, null, default);
        Assert.Equal(2L, Convert.ToInt64(marked["changed"]));
        Assert.Equal(0L, Convert.ToInt64((await _read.UnreadCountsAsync(viewer, default))["total"]));

        _clock.Now = _clock.Now.AddMinutes(5);
        var late = SystemData("E2E markall late");
        late["audience"] = ManualAudience(viewer);
        var lateNote = await _publish.PublishAsync(actor, Json(late), $"e2e-key-markall-late-{Guid.NewGuid():N}", default);
        await _publish.DispatchAsync(lateNote["_id"].AsObjectId, default);
        Assert.Equal(1L, Convert.ToInt64((await _read.UnreadCountsAsync(viewer, default))["total"]));
    }

    // ------------------------------------------------------------------
    // 7. Subscription + channel gates: who may receive mail
    // ------------------------------------------------------------------

    [Fact]
    public async Task Subscription_PreferenceAndChannelGates()
    {
        var recipient = await InsertUserAsync();
        var untrusted = new NotificationPublishService(_services.Store, _content, _read,
            Configuration(trust: false), _mail);
        Assert.Equal("unverified_email", await untrusted.EmailReasonAsync(recipient, "system", default));
        await _publish.MarkVerifiedAsync(recipient.Text("e"), default);
        Assert.Equal("", await untrusted.EmailReasonAsync(recipient, "system", default));

        // Per-category unsubscribe: disabling "system" blocks system mail only.
        var saved = await _read.SavePreferencesAsync(recipient,
            Json(new { version = 0, categories = new { system = false, team = true, project = true, personal = true } }), default);
        Assert.True(Convert.ToInt64(saved["version"]) >= 1);
        Assert.Equal("preference_disabled", await _publish.EmailReasonAsync(recipient, "system", default));
        Assert.Equal("", await _publish.EmailReasonAsync(recipient, "team", default));

        // No email address on file.
        var noEmail = await InsertNoEmailUserAsync();
        Assert.Equal("no_email", await _publish.EmailReasonAsync(noEmail, "system", default));

        // Channel disabled: the notification mail switch is off.
        var muted = new NotificationPublishService(_services.Store, _content, _read,
            Configuration(trust: true, userEmail: false), _mail);
        Assert.Equal("channel_disabled", await muted.EmailReasonAsync(recipient, "team", default));
    }

    // ------------------------------------------------------------------
    // 8. Verification-code mail vs notification mail: separate channels
    // ------------------------------------------------------------------

    [Fact]
    public async Task Mail_VerificationCodePathIsIndependentOfNotificationChannel()
    {
        // Notification mail flows through the fake INotificationMailTransport
        // and honors Notifications:UserEmailEnabled; verification codes are
        // issued through a durable job gated by Mail:Enabled and never touch
        // the notification transport.
        var recipient = await InsertUserAsync();
        var actor = await InsertUserAsync(admin: true);
        var data = SystemData("E2E channel split");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-split-{Guid.NewGuid():N}", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        await _publish.DeliverAsync((await DeliveryAsync(note["_id"].AsObjectId))["_id"].AsObjectId, default);
        Assert.Contains(_mail.Messages, message =>
            message.MessageId != null && message.MessageId.Contains(note["_id"].AsObjectId.ToString()));

        var codeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mail:Enabled"] = "false"
        }).Build();
        var codes = new EmailCodeService(_services.Store, codeConfiguration, _clock);
        var issueError = await Assert.ThrowsAsync<ApiError>(() =>
            codes.IssueAsync(1, "code-user@example.test", default));
        Assert.Equal("MailUnavailableError", issueError.Error);
        Assert.Equal(2000, issueError.Code);
        Assert.Equal(503, issueError.Status);
    }

    // ------------------------------------------------------------------
    // 9. Scanner: expired lease -> unknown, never blindly resent
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scan_ExpiredLeaseBecomesUnknownWithoutResend()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var note = await InsertNoteAsync(actor, state: "published", requestedEmail: true);
        var before = _clock.Now.UtcDateTime;
        var delivery = await InsertDeliveryAsync(note["_id"].AsObjectId, recipient["_id"].AsObjectId,
            state: "leased", attempts: 1, nextAttemptAt: before.AddMinutes(-1),
            leaseUntil: before.AddMinutes(-1), leaseToken: "crashed-sender");
        await CancelOtherDueNotesAsync(note["_id"].AsObjectId);

        var sentBefore = CountMessagesFor(note["_id"].AsObjectId);
        await _publish.ScanAsync(default);

        var current = await GetDeliveryAsync(delivery["_id"].AsObjectId);
        Assert.Equal("unknown", current.Text("state"));
        Assert.Equal("lease_expired", current.Text("reason"));
        Assert.Equal(1L, current.Integer("attempts"));
        Assert.Equal(sentBefore, CountMessagesFor(note["_id"].AsObjectId));
    }

    // ------------------------------------------------------------------
    // 10. Delivery: exponential backoff + five-attempt cap
    // ------------------------------------------------------------------

    [Fact]
    public async Task Deliver_BackoffScheduleAndFiveAttemptCap()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var data = SystemData("E2E backoff");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-backoff-{Guid.NewGuid():N}", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        var deliveryId = (await DeliveryAsync(note["_id"].AsObjectId))["_id"].AsObjectId;

        _mail.Handler = _ => Task.FromResult(new NotificationMailResult("retry_wait", "smtp_response"));
        var expectedDelays = new[] { 60, 120, 240, 480 };
        for (var attempt = 0; attempt < 4; attempt++)
        {
            _clock.Now = _clock.Now.AddHours(2);
            var tick = _clock.Now.UtcDateTime;
            await _publish.DeliverAsync(deliveryId, default);
            var current = await GetDeliveryAsync(deliveryId);
            Assert.Equal("retry_wait", current.Text("state"));
            Assert.Equal(attempt + 1, current.Integer("attempts"));
            var delay = (current["next_attempt_at"].ToUniversalTime() - tick).TotalSeconds;
            Assert.InRange(delay, expectedDelays[attempt] - 5, expectedDelays[attempt] + 5);
        }

        _clock.Now = _clock.Now.AddHours(2);
        await _publish.DeliverAsync(deliveryId, default);
        var terminal = await GetDeliveryAsync(deliveryId);
        Assert.Equal("failed", terminal.Text("state"));
        Assert.Equal(5L, terminal.Integer("attempts"));

        // Terminal states are never retried.
        var sent = CountMessagesFor(note["_id"].AsObjectId);
        await _publish.DeliverAsync(deliveryId, default);
        Assert.Equal("failed", (await GetDeliveryAsync(deliveryId)).Text("state"));
        Assert.Equal(sent, CountMessagesFor(note["_id"].AsObjectId));
    }

    // ------------------------------------------------------------------
    // 11. Scanner: concurrent scans deliver exactly once
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scan_ConcurrentScansDeliverExactlyOnce()
    {
        var actor = await InsertUserAsync(admin: true);
        var first = await InsertUserAsync();
        var second = await InsertUserAsync();
        var data = SystemData("E2E exactly once");
        data["audience"] = new Dictionary<string, object?>
        {
            ["mode"] = "manual",
            ["user_ids"] = new[] { first["_id"].AsObjectId.ToString(), second["_id"].AsObjectId.ToString() }
        };
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), $"e2e-key-once-{Guid.NewGuid():N}", default);
        var id = note["_id"].AsObjectId;
        await CancelOtherDueNotesAsync(id);

        await _publish.ScanAsync(default);
        await _publish.ScanAsync(default);

        var filter = Builders<BsonDocument>.Filter.Eq("notification_id", id);
        Assert.Equal(2L, await CountAsync("notification_receipt", filter));
        var deliveries = await _services.Store.Collection("notification_delivery").Find(filter).ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal("accepted", delivery.Text("state")));
        Assert.Equal(2, CountMessagesFor(id));
        Assert.Equal("published", (await GetNoteAsync(id)).Text("state"));
    }

    // ------------------------------------------------------------------
    // 12. Error contract: NotificationError/4600 + {error,code,message}
    // ------------------------------------------------------------------

    [Fact]
    public void ErrorContract_NotificationErrorShapeMatchesPython()
    {
        // Python oracle: app/core/api.py:34-39 renders
        // {"error": <class name>, "code": 4600, "message": ...} and
        // app/services/notification_content.py:12 fixes code 4600.
        Assert.Equal("NotificationError", NotificationErrors.ErrorName);
        Assert.Equal(4600, NotificationErrors.ErrorCode);
        Assert.Equal(400, NotificationErrors.Fail("x").Status);
        Assert.Equal(404, NotificationErrors.NotFound().Status);
        Assert.Equal(403, NotificationErrors.Forbidden("x").Status);
        Assert.Equal(409, NotificationErrors.Conflict("x").Status);
        Assert.Equal(429, NotificationErrors.TooManyRequests().Status);
        Assert.Equal(503, NotificationErrors.Unavailable().Status);
    }

    [Fact]
    public async Task ErrorContract_HttpBodyUsesErrorCodeMessageTriple()
    {
        using var app = EnabledApp();
        using var client = app.CreateClient();
        var user = await InsertUserAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));

        var response = await client.GetAsync($"/v1/me/notifications/{ObjectId.GenerateNewId()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NotificationError", body.GetProperty("error").GetString());
        Assert.Equal(4600, body.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    // ------------------------------------------------------------------
    // 13. TTL: throttle buckets expire via the expires_at TTL index
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ttl_ThrottleBucketIndexExpiresDocuments()
    {
        // Python oracle: app/models/notification.py:156
        // {"fields": ["expires_at"], "expireAfterSeconds": 0}.
        using var cursor = await _services.Store.Collection("notification_throttle").Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        var ttl = indexes.FirstOrDefault(index =>
            index["key"].AsBsonDocument.Names.Contains("expires_at"));
        Assert.NotNull(ttl);
        Assert.Equal(0, ttl["expireAfterSeconds"].ToInt64());
        var unique = indexes.FirstOrDefault(index =>
            index["key"].AsBsonDocument.Names.Contains("key"));
        Assert.NotNull(unique);
        Assert.True(unique["unique"].AsBoolean);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private WebApplicationFactory<Program> EnabledApp() =>
        _services.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Enabled"] = "true",
                ["Notifications:SiteOrigin"] = "https://example.test"
            })));

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static string Sha256(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Dictionary<string, object?> SystemData(string title) => new()
    {
        ["category"] = "system",
        ["title"] = title,
        ["body"] = "",
        ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" }
    };

    private static Dictionary<string, object?> TeamData(BsonDocument team, string title) => new()
    {
        ["category"] = "team",
        ["scope_id"] = team["_id"].AsObjectId.ToString(),
        ["title"] = title,
        ["body"] = "",
        ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" }
    };

    private static Dictionary<string, object?> ProjectData(BsonDocument project, string title) => new()
    {
        ["category"] = "project",
        ["scope_id"] = project["_id"].AsObjectId.ToString(),
        ["title"] = title,
        ["body"] = "",
        ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" }
    };

    private static Dictionary<string, object?> ManualAudience(BsonDocument user) => new()
    {
        ["mode"] = "manual",
        ["user_ids"] = new[] { user["_id"].AsObjectId.ToString() }
    };

    private int CountMessagesFor(ObjectId noteId) =>
        _mail.Messages.Count(message =>
            message.MessageId != null && message.MessageId.Contains(noteId.ToString()));

    private async Task<BsonDocument> InsertUserAsync(
        bool admin = false, bool banned = false, string? email = null, string? name = null)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name ?? "e2e-user-" + id,
            ["e"] = email ?? id + "@example.test",
            ["admin"] = admin,
            ["b"] = banned,
            ["p"] = new BsonDocument()
        };
        await _services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> InsertNoEmailUserAsync()
    {
        await _services.Store.Collection("user").DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("e", ""));
        return await InsertUserAsync(email: "");
    }

    private async Task<BsonDocument> InsertTeamAsync(string name = "team")
    {
        var id = ObjectId.GenerateNewId();
        var team = new BsonDocument { ["_id"] = id, ["n"] = name + "-" + id };
        await _services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task InsertTeamMemberAsync(BsonDocument team, BsonDocument user, string baseTag = "member")
    {
        await _services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["status"] = "active"
        });
    }

    private async Task<BsonDocument> InsertProjectAsync(BsonDocument team, BsonDocument owner, int status = 0)
    {
        var id = ObjectId.GenerateNewId();
        var project = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "e2e-project-" + id,
            ["ns"] = "e2e-project-" + id,
            ["i"] = "e2e-project-" + id,
            ["t"] = team["_id"],
            ["ps"] = ObjectId.GenerateNewId(),
            ["st"] = status,
            ["ou"] = owner["_id"]
        };
        await _services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task InsertProjectMemberAsync(
        BsonDocument project, BsonDocument user, params string[] tags)
    {
        await _services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = user["_id"],
            ["tags"] = new BsonArray(tags.Select(tag => (BsonValue)tag)),
            ["status"] = "active"
        });
    }

    private async Task<BsonDocument> InsertNoteAsync(
        BsonDocument actor, string state = "published", bool requestedEmail = false)
    {
        var now = _clock.Now.UtcDateTime;
        var note = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["key"] = "e2e-note-" + ObjectId.GenerateNewId(),
            ["fingerprint"] = "e2e-fingerprint-" + ObjectId.GenerateNewId(),
            ["category"] = "system",
            ["source"] = "core",
            ["event_type"] = "announcement",
            ["actor_id"] = actor["_id"],
            ["team_id"] = BsonNull.Value,
            ["project_id"] = BsonNull.Value,
            ["title"] = "E2E seeded note",
            ["body"] = "",
            ["nodes"] = new BsonArray { new BsonDocument { ["type"] = "text", ["text"] = "E2E" } },
            ["search_text"] = "e2e seeded note",
            ["search_tokens"] = new BsonArray(),
            ["audience"] = new BsonDocument("mode", "all"),
            ["requested_email"] = requestedEmail,
            ["payload"] = new BsonDocument(),
            ["state"] = state,
            ["created_at"] = now,
            ["publish_at"] = now,
            ["expires_at"] = BsonNull.Value,
            ["revoked_at"] = BsonNull.Value,
            ["revoked_by"] = BsonNull.Value,
            ["revoke_reason"] = "",
            ["moderation_key"] = "",
            ["moderation_action"] = "revoke",
            ["moderation_audited"] = true,
            ["publish_audited"] = true,
            ["version"] = 1,
            ["cursor"] = "",
            ["next_chunk"] = 0,
            ["recipient_count"] = 0,
            ["error"] = "",
            ["lease_token"] = "",
            ["lease_until"] = DateTime.MinValue
        };
        await _services.Store.Collection("notification").InsertOneAsync(note);
        return note;
    }

    private async Task<BsonDocument> InsertDeliveryAsync(
        ObjectId noteId, ObjectId userId, string state, int attempts,
        DateTime nextAttemptAt, DateTime leaseUntil, string leaseToken)
    {
        var delivery = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["notification_id"] = noteId,
            ["user_id"] = userId,
            ["state"] = state,
            ["attempts"] = attempts,
            ["next_attempt_at"] = nextAttemptAt,
            ["lease_until"] = leaseUntil,
            ["lease_token"] = leaseToken,
            ["message_id"] = $"<notification.{noteId}.{userId}@moeflow.invalid>",
            ["reason"] = "",
            ["accepted_at"] = BsonNull.Value
        };
        await _services.Store.Collection("notification_delivery").InsertOneAsync(delivery);
        return delivery;
    }

    private async Task<BsonDocument> GetNoteAsync(ObjectId id) =>
        await _services.Store.Collection("notification")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();

    private async Task<BsonDocument> GetDeliveryAsync(ObjectId id) =>
        await _services.Store.Collection("notification_delivery")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();

    private async Task<BsonDocument> DeliveryAsync(ObjectId notificationId) =>
        await _services.Store.Collection("notification_delivery")
            .Find(Builders<BsonDocument>.Filter.Eq("notification_id", notificationId)).FirstAsync();

    private async Task<long> CountAsync(string collection, FilterDefinition<BsonDocument> filter) =>
        await _services.Store.Collection(collection).CountDocumentsAsync(filter);

    private async Task CancelOtherDueNotesAsync(ObjectId keep)
    {
        await _services.Store.Collection("notification").UpdateManyAsync(
            Builders<BsonDocument>.Filter.In("state", new[] { "preparing", "scheduled", "dispatching" })
            & Builders<BsonDocument>.Filter.Ne("_id", keep),
            Builders<BsonDocument>.Update.Set("state", "cancelled"));
    }
}
