using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Notifications;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class NotificationPublishTests
{
    private readonly LocalServices _services;
    private readonly NotificationClock _clock;
    private readonly IConfiguration _configuration;
    private readonly IdentityPermissions _permissions;
    private readonly NotificationContent _content;
    private readonly NotificationReadService _read;
    private readonly FakeMailTransport _mail = new();
    private readonly NotificationPublishService _publish;

    public NotificationPublishTests(LocalServices services)
    {
        _services = services;
        _clock = new NotificationClock(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
        _configuration = Configuration(true, true);
        _permissions = new IdentityPermissions(services.Store, _clock);
        _content = new NotificationContent(services.Store, _permissions, _configuration);
        _read = new NotificationReadService(services.Store, _permissions, _content, _configuration, _clock);
        _publish = new NotificationPublishService(
            services.Store, _content, _read, _configuration, _mail);
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
    // Facts
    // ------------------------------------------------------------------

    [Fact]
    public void Fingerprint_MatchesPythonUnicodeAndSortedKeys()
    {
        var first = NotificationPublishServiceData(
            """{"title":"萌翻 通知","n":1,"ok":true,"f":1.0,"nested":{"b":2,"a":"é"}}""");
        Assert.Equal(
            "860d58dae717e1705c9279ba48efb69afc319aeef06d30d28fa83595f4b1a4b7",
            NotificationFingerprint.Compute(first, "announcement", new BsonDocument()));

        var second = NotificationPublishServiceData(
            """{"title":"A","n":2,"f":1e20,"g":0.00001,"h":-0.0}""");
        Assert.Equal(
            "4c99cbb2b39aad0bf4c25b4f813ae121276670658acb40ed22cea5f13ec6d7a8",
            NotificationFingerprint.Compute(second, "announcement", new BsonDocument()));

        // Python oracle: json.dumps(..., sort_keys=True, default=str) stringifies
        // BSON datetime as "YYYY-MM-DD HH:MM:SS.ffffff" and ObjectId as hex.
        var third = NotificationPublishServiceData("""{"title":"P","n":3}""");
        var payload = new BsonDocument
        {
            ["when"] = new BsonDateTime(new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc)),
            ["oid"] = ObjectId.Parse("64b7f0c2a1b2c3d4e5f60718"),
            ["z"] = true
        };
        Assert.Equal(
            "a019d9beba2829053946bb81b99eee4f41c1369f9b81e52eb5da152b56812460",
            NotificationFingerprint.Compute(third, "announcement", payload));
    }

    [Fact]
    public async Task Publish_ReusesSameKeyForSameRawData()
    {
        var actor = await InsertUserAsync(admin: true);
        var data = SystemData("Idempotent");

        var first = await _publish.PublishAsync(actor, Json(data), "idempotent-key-1", default);
        var second = await _publish.PublishAsync(actor, Json(data), "idempotent-key-1", default);

        Assert.Equal(first["_id"], second["_id"]);
        Assert.Equal(1L, await CountAsync("notification", Builders<BsonDocument>.Filter.Eq("key", first.Text("key"))));
    }

    [Fact]
    public async Task Publish_RejectsSameKeyWithDifferentData()
    {
        var actor = await InsertUserAsync(admin: true);
        await _publish.PublishAsync(actor, Json(SystemData("First")), "conflict-key-1", default);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.PublishAsync(actor, Json(SystemData("Second")), "conflict-key-1", default));
        Assert.Equal(409, error.Status);
        Assert.Equal("相同操作键不能用于不同内容", error.Message);
    }

    [Fact]
    public async Task Publish_ThrottlesAfterTwentyPerMinute()
    {
        // Use a minute far away from other facts so the shared database throttle
        // bucket cannot already contain entries from another test.
        _clock.Now = new DateTimeOffset(2031, 5, 6, 7, 8, 0, TimeSpan.Zero);
        var actor = await InsertUserAsync(admin: true);
        for (var index = 0; index < 20; index++)
            await _publish.PublishAsync(actor, Json(SystemData($"Throttle {index}")), $"throttle-key-{index}", default);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.PublishAsync(actor, Json(SystemData("Throttle 20")), "throttle-key-20", default));
        Assert.Equal(429, error.Status);
    }

    [Fact]
    public async Task Publish_ProjectAuthorNeedsLiveMembershipNotInheritedTeamAdmin()
    {
        var team = await InsertTeamAsync();
        var teamAdmin = await InsertUserAsync();
        await InsertTeamMemberAsync(team, teamAdmin, "admin");
        var project = await InsertProjectAsync(team, teamAdmin);

        var error = await Assert.ThrowsAsync<ApiError>(() => _publish.PublishAsync(
            teamAdmin, Json(ProjectData(project, "No membership")), "project-key-1", default));
        Assert.Equal(403, error.Status);
        Assert.Equal("没有当前范围的通知发送权限", error.Message);

        await InsertProjectMemberAsync(project, teamAdmin, tags: ["member"]);
        var note = await _publish.PublishAsync(teamAdmin, Json(ProjectData(project, "Member")), "project-key-2", default);
        Assert.Equal(project["_id"], note["project_id"]);

        var disabled = await InsertProjectAsync(team, teamAdmin, status: 1);
        await InsertProjectMemberAsync(disabled, teamAdmin, tags: ["member"]);
        var second = await Assert.ThrowsAsync<ApiError>(() => _publish.PublishAsync(
            teamAdmin, Json(ProjectData(disabled, "Disabled")), "project-key-3", default));
        Assert.Equal(403, second.Status);
    }

    [Fact]
    public async Task RecipientIds_ManualRejectsBannedAndOutOfScopeUsers()
    {
        var actor = await InsertUserAsync(admin: true);
        var valid = await InsertUserAsync();
        var banned = await InsertUserAsync(banned: true);

        var data = SystemData("Manual");
        data["audience"] = new Dictionary<string, object?>
        {
            ["mode"] = "manual",
            ["user_ids"] = new[] { valid["_id"].AsObjectId.ToString(), banned["_id"].AsObjectId.ToString() }
        };
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.PublishAsync(actor, Json(data), "manual-key-1", default));
        Assert.Equal("指定用户不在有效接收范围内", error.Message);

        data["audience"] = new Dictionary<string, object?>
        {
            ["mode"] = "manual",
            ["user_ids"] = new[] { valid["_id"].AsObjectId.ToString() }
        };
        var note = await _publish.PublishAsync(actor, Json(data), "manual-key-2", default);
        var ids = await _publish.RecipientIdsAsync(note, default);
        Assert.Equal(new List<ObjectId> { valid["_id"].AsObjectId }, ids);
    }

    [Fact]
    public async Task RecipientIds_TeamUsesBaseTagAndWorkerPositions()
    {
        var actor = await InsertUserAsync();
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team, actor, "creator");
        var translator = await InsertUserAsync();
        await InsertTeamMemberAsync(team, translator, "member", workerQualifications: ["translator"]);
        var cleaner = await InsertUserAsync();
        await InsertTeamMemberAsync(team, cleaner, "member", workerQualifications: ["cleaner"]);
        var creator = await InsertUserAsync();
        await InsertTeamMemberAsync(team, creator, "creator", workerQualifications: ["translator"]);

        var data = new Dictionary<string, object?>
        {
            ["category"] = "team",
            ["scope_id"] = team["_id"].AsObjectId.ToString(),
            ["title"] = "Team scope",
            ["body"] = "",
            ["audience"] = new Dictionary<string, object?>
            {
                ["mode"] = "condition",
                ["base_roles"] = new[] { "member" },
                ["worker_qualifications"] = new[] { "translator" }
            }
        };
        var note = await _publish.PublishAsync(actor, Json(data), "team-scope-key-1", default);
        var ids = await _publish.RecipientIdsAsync(note, default);

        Assert.Equal(new List<ObjectId> { translator["_id"].AsObjectId }, ids);
    }

    [Fact]
    public async Task Dispatch_PreparesDurableChunksThenFansOutAcrossTicks()
    {
        var actor = await InsertUserAsync();
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team, actor, "creator");
        await InsertUsersAndMembersAsync(team, 449);

        var data = TeamData(team, "Bulk");
        var note = await _publish.PublishAsync(actor, Json(data), "bulk-key-1", default);
        var id = note["_id"].AsObjectId;

        await _publish.DispatchAsync(id, default);
        note = await GetNoteAsync(id);
        Assert.Equal("preparing", note.Text("state"));
        Assert.Equal(400L, note.Integer("recipient_count"));
        Assert.Equal(4L, await CountAsync("notification_audience_chunk",
            Builders<BsonDocument>.Filter.Eq("notification_id", id)));

        // Simulate a worker crash: release the lease without touching the durable chunks.
        await _services.Store.Collection("notification").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("lease_token", "").Set("lease_until", DateTime.MinValue));

        await _publish.DispatchAsync(id, default);
        note = await GetNoteAsync(id);
        Assert.Equal("dispatching", note.Text("state"));
        Assert.Equal(450L, note.Integer("recipient_count"));
        Assert.Equal(5L, await CountAsync("notification_audience_chunk",
            Builders<BsonDocument>.Filter.Eq("notification_id", id)));

        await _publish.DispatchAsync(id, default);
        note = await GetNoteAsync(id);
        Assert.Equal("published", note.Text("state"));
        Assert.Equal(450L, note.Integer("recipient_count"));
        Assert.Equal(450L, await CountAsync("notification_receipt",
            Builders<BsonDocument>.Filter.Eq("notification_id", id)));

        await _publish.DispatchAsync(id, default);
        Assert.Equal(450L, await CountAsync("notification_receipt",
            Builders<BsonDocument>.Filter.Eq("notification_id", id)));
    }

    [Fact]
    public async Task Dispatch_ScheduleLateReturnsDraft()
    {
        var actor = await InsertUserAsync(admin: true);
        var data = SystemData("Late");
        data["publish_at"] = _clock.Now.AddHours(1).ToString("O");
        var note = await _publish.PublishAsync(actor, Json(data), "late-key-1", default);
        Assert.Equal("scheduled", note.Text("state"));

        _clock.Now = _clock.Now.AddHours(3);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        note = await GetNoteAsync(note["_id"].AsObjectId);
        Assert.Equal("draft", note.Text("state"));
        Assert.Equal("schedule_late", note.Text("error"));
    }

    [Fact]
    public async Task Dispatch_ExpiredNoteIsCancelled()
    {
        var actor = await InsertUserAsync(admin: true);
        var data = SystemData("Expiring");
        data["expires_at"] = _clock.Now.AddMinutes(30).ToString("O");
        var note = await _publish.PublishAsync(actor, Json(data), "expiring-key-1", default);

        _clock.Now = _clock.Now.AddHours(1);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        note = await GetNoteAsync(note["_id"].AsObjectId);
        Assert.Equal("cancelled", note.Text("state"));
        Assert.Equal("expired", note.Text("error"));
    }

    [Fact]
    public async Task Dispatch_AuthorLosingScopeCancels()
    {
        var team = await InsertTeamAsync();
        var owner = await InsertUserAsync();
        await InsertTeamMemberAsync(team, owner, "creator");
        var project = await InsertProjectAsync(team, owner);
        var membership = await InsertProjectMemberAsync(project, owner, tags: ["creator"]);

        var note = await _publish.PublishAsync(owner, Json(ProjectData(project, "Author")), "author-key-1", default);
        await _services.Store.Collection("project_member").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", membership["_id"]),
            Builders<BsonDocument>.Update.Set("status", "removed"));

        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        note = await GetNoteAsync(note["_id"].AsObjectId);
        Assert.Equal("cancelled", note.Text("state"));
        Assert.Equal("sender_permission_lost", note.Text("error"));
    }

    [Fact]
    public async Task Dispatch_SkipsRecipientsBannedBeforeFanOut()
    {
        var actor = await InsertUserAsync();
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team, actor, "creator");
        var first = await InsertUserAsync();
        await InsertTeamMemberAsync(team, first);
        var second = await InsertUserAsync();
        await InsertTeamMemberAsync(team, second);

        var note = await _publish.PublishAsync(actor, Json(TeamData(team, "Ban")), "ban-key-1", default);
        await _services.Store.Collection("user").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", second["_id"]),
            Builders<BsonDocument>.Update.Set("b", true));

        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        Assert.Equal(2L, await CountAsync("notification_receipt",
            Builders<BsonDocument>.Filter.Eq("notification_id", note["_id"])));
    }

    [Fact]
    public async Task Deliver_RevokedBeforeSmtpDoesNotSend()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var data = SystemData("Revoked mail");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;

        var note = await _publish.PublishAsync(actor, Json(data), "revoked-mail-key-1", default);
        var id = note["_id"].AsObjectId;
        await _publish.DispatchAsync(id, default);
        var delivery = await DeliveryAsync(id);

        await _services.Store.Collection("notification").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("revoked_at", _clock.Now.UtcDateTime)
                .Set("state", "cancelled"));
        await _publish.DeliverAsync(delivery["_id"].AsObjectId, default);

        Assert.Empty(_mail.Messages);
        var updated = await _services.Store.Collection("notification_delivery")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", delivery["_id"])).FirstAsync();
        Assert.Equal("cancelled", updated.Text("state"));
        Assert.Equal("revoked_or_expired", updated.Text("reason"));
    }

    [Fact]
    public async Task DraftEdit_UsesVersionCasAndRejectsScopeChange()
    {
        var actor = await InsertUserAsync(admin: true);
        var note = await InsertNoteAsync(actor, state: "draft", version: 0,
            audience: new BsonDocument("mode", "all"));

        var edit = new Dictionary<string, object?>
        {
            ["version"] = 0,
            ["category"] = "system",
            ["title"] = "Updated",
            ["body"] = "",
            ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" },
            ["draft"] = true
        };
        var updated = await _publish.EditDraftAsync(actor, note, Json(edit), default);
        Assert.Equal("Updated", updated.Text("title"));
        Assert.Equal(1L, updated.Integer("version"));

        var stale = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.EditDraftAsync(actor, updated, Json(edit), default));
        Assert.Equal(409, stale.Status);
        Assert.Equal("草稿版本冲突", stale.Message);

        var changed = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["category"] = "team",
            ["scope_id"] = ObjectId.GenerateNewId().ToString(),
            ["title"] = "Changed",
            ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" }
        };
        var scope = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.EditDraftAsync(actor, updated, Json(changed), default));
        Assert.Equal("不能修改通知类别或范围", scope.Message);
    }

    [Fact]
    public async Task Revoke_IsKeyIdempotentAndAuditsOnce()
    {
        var actor = await InsertUserAsync(admin: true);
        var note = await InsertNoteAsync(actor, state: "published", version: 1,
            audience: new BsonDocument("mode", "all"));

        var data = new Dictionary<string, object?>
        {
            ["confirmed"] = true,
            ["version"] = 1,
            ["reason"] = "Spam"
        };
        var revoked = await _publish.RevokeAsync(actor, note, Json(data), "revoke-key-123", true, default);
        Assert.Equal("cancelled", revoked.Text("state"));
        Assert.Equal(2L, revoked.Integer("version"));
        Assert.True(revoked.Contains("revoked_at") && !revoked["revoked_at"].IsBsonNull);
        var auditKey = revoked.Text("moderation_key") + ":revoke";
        Assert.Equal(1L, await CountAsync("notification_audit",
            Builders<BsonDocument>.Filter.Eq("key", auditKey)));

        var again = await _publish.RevokeAsync(actor, revoked, Json(data), "revoke-key-123", true, default);
        Assert.Equal(2L, again.Integer("version"));
        Assert.Equal(1L, await CountAsync("notification_audit",
            Builders<BsonDocument>.Filter.Eq("key", auditKey)));

        var conflict = await Assert.ThrowsAsync<ApiError>(() =>
            _publish.RevokeAsync(actor, revoked, Json(data), "revoke-key-456", true, default));
        Assert.Equal(409, conflict.Status);
        Assert.Equal("通知已经撤回，请刷新", conflict.Message);
    }

    [Fact]
    public async Task Scan_RecoversRevokedAuditAfterCrash()
    {
        var actor = await InsertUserAsync(admin: true);
        var note = await InsertNoteAsync(actor, state: "published", version: 2,
            audience: new BsonDocument("mode", "all"),
            revokedAt: _clock.Now.UtcDateTime, moderationKey: "crash-operation", moderationAudited: false);

        await CancelOtherDueNotesAsync(note["_id"].AsObjectId);
        await _publish.ScanAsync(default);

        var updated = await GetNoteAsync(note["_id"].AsObjectId);
        Assert.True(updated.Flag("moderation_audited"));
        Assert.Equal(1L, await CountAsync("notification_audit",
            Builders<BsonDocument>.Filter.Eq("key", "crash-operation:revoke")));
    }

    [Fact]
    public async Task EmailReason_FollowsLiveAddressPreferencesAndVerification()
    {
        var untrusted = PublishService(trust: false);
        var noEmail = await InsertNoEmailUserAsync();
        var unverified = await InsertUserAsync();
        var verified = await InsertUserAsync();
        var preferred = await InsertUserAsync();

        Assert.Equal("no_email", await untrusted.EmailReasonAsync(noEmail, "system", default));
        Assert.Equal("unverified_email", await untrusted.EmailReasonAsync(unverified, "system", default));

        await untrusted.MarkVerifiedAsync(verified.Text("e"), default);
        Assert.Equal("", await untrusted.EmailReasonAsync(verified, "system", default));

        await _services.Store.Collection("notification_preference").InsertOneAsync(new BsonDocument
        {
            ["user_id"] = preferred["_id"],
            ["categories"] = new BsonDocument("system", false),
            ["version"] = 0
        });
        Assert.Equal("preference_disabled", await untrusted.EmailReasonAsync(preferred, "system", default));

        var disabled = PublishService(trust: false, userEmail: false);
        Assert.Equal("channel_disabled", await disabled.EmailReasonAsync(verified, "system", default));
    }

    [Fact]
    public void MailGate_EnforcesAllowlistHeadersAndTls()
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dest@example.test" };
        var settings = new NotificationMailSettings(
            true, false, true, false, "127.0.0.1", 25, false, "noreply@example.test",
            null, null, true, allowlist);

        var other = ProtectedNotificationMail.Check(Message("other@example.test"), settings);
        Assert.Equal("skipped", other.Status);
        Assert.Equal("recipient_not_allowed", other.Reason);
        Assert.Equal("ok", ProtectedNotificationMail.Check(Message("DEST@example.test"), settings).Status);
        Assert.Equal("invalid_header",
            ProtectedNotificationMail.Check(Message("dest@example.test", subject: "bad\r\nheader"), settings).Reason);
        Assert.Equal("invalid_email",
            ProtectedNotificationMail.Check(Message("not-an-address"), settings).Reason);
        Assert.Equal("smtp_unconfigured",
            ProtectedNotificationMail.Check(Message("dest@example.test"), settings with { Host = "" }).Reason);
        Assert.Equal("channel_disabled",
            ProtectedNotificationMail.Check(Message("dest@example.test"), settings with
            {
                UserEmailEnabled = false,
                LogEmailEnabled = false
            }).Reason);
        Assert.Equal("channel_disabled",
            ProtectedNotificationMail.Check(
                Message("dest@example.test"), settings with
                {
                    UserEmailEnabled = false,
                    LogEmailEnabled = false
                },
                NotificationMailChannel.Log).Reason);

        Assert.Equal(MailKit.Security.SecureSocketOptions.StartTls,
            ProtectedNotificationMail.SocketOptions(false));
        Assert.Equal(MailKit.Security.SecureSocketOptions.SslOnConnect,
            ProtectedNotificationMail.SocketOptions(true));
    }

    [Theory]
    [InlineData(true, true, "ok", "ok")]
    [InlineData(true, false, "ok", "skipped")]
    [InlineData(false, true, "skipped", "ok")]
    [InlineData(false, false, "skipped", "skipped")]
    public void MailGate_SelectsUserAndLogSwitchesIndependently(
        bool userEmail, bool logEmail, string userStatus, string logStatus)
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dest@example.test" };
        var settings = new NotificationMailSettings(
            userEmail, logEmail, true, false, "127.0.0.1", 25, false,
            "noreply@example.test", null, null, true, allowlist);

        var user = ProtectedNotificationMail.Check(Message("dest@example.test"), settings);
        Assert.Equal(userStatus, user.Status);
        Assert.Equal(userStatus == "ok" ? "" : "channel_disabled", user.Reason);

        var log = ProtectedNotificationMail.Check(
            Message("dest@example.test"), settings, NotificationMailChannel.Log);
        Assert.Equal(logStatus, log.Status);
        Assert.Equal(logStatus == "ok" ? "" : "channel_disabled", log.Reason);
    }

    [Fact]
    public void MailSettings_ReadsUserAndLogSwitchesFromLegacyAndNewKeys()
    {
        var legacy = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ENABLE_USER_EMAIL"] = "false",
            ["ENABLE_LOG_EMAIL"] = "True"
        }).Build();
        var legacySettings = NotificationMailSettings.FromConfiguration(legacy, new TestHostEnvironment());
        Assert.False(legacySettings.UserEmailEnabled);
        Assert.True(legacySettings.LogEmailEnabled);

        var current = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:UserEmailEnabled"] = "true",
            ["Notifications:LogEmailEnabled"] = "false",
            ["ENABLE_USER_EMAIL"] = "false",
            ["ENABLE_LOG_EMAIL"] = "true"
        }).Build();
        var currentSettings = NotificationMailSettings.FromConfiguration(current, new TestHostEnvironment());
        Assert.True(currentSettings.UserEmailEnabled);
        Assert.False(currentSettings.LogEmailEnabled);
    }

    [Fact]
    public async Task ProtectedTransport_DoesNotSendUserMailWhenOnlyLogChannelIsEnabled()
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dest@example.test" };
        var settings = new NotificationMailSettings(
            false, true, true, false, "127.0.0.1", TestEnvironment.SmtpPort, false,
            "noreply@example.test", null, null, true, allowlist);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:UserEmailEnabled"] = "false",
            ["Notifications:LogEmailEnabled"] = "true",
            ["TESTING"] = "YES",
            ["Mail:Host"] = settings.Host,
            ["Mail:Port"] = settings.Port.ToString(),
            ["Mail:UseSsl"] = "false",
            ["Mail:From"] = settings.From,
            ["Notifications:EmailAllowlist:0"] = "dest@example.test"
        }).Build();
        var transport = new MailKitNotificationTransport(
            configuration, new TestHostEnvironment(), NullLogger<MailKitNotificationTransport>.Instance);

        var result = await transport.SendAsync(Message("dest@example.test"), default);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("channel_disabled", result.Reason);
    }

    [Fact]
    public async Task ProtectedTransport_RejectsRecipientOutsideAllowlistWithoutSending()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:UserEmailEnabled"] = "true",
            ["Notifications:LogEmailEnabled"] = "false",
            ["TESTING"] = "YES",
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = TestEnvironment.SmtpPort.ToString(),
            ["Mail:UseSsl"] = "false",
            ["Mail:From"] = "noreply@example.test",
            ["Notifications:EmailAllowlist:0"] = "allowed@example.test"
        }).Build();
        var transport = new MailKitNotificationTransport(
            configuration, new TestHostEnvironment(), NullLogger<MailKitNotificationTransport>.Instance);

        var result = await transport.SendAsync(Message("dest@example.test"), default);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("recipient_not_allowed", result.Reason);
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Shoreline.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void Mail_ClassifiesCancellationBySendBoundary()
    {
        var beforeSend = MailKitNotificationTransport.ClassifyFailure(
            new OperationCanceledException(), sending: false, accepted: false);
        Assert.Equal("retry_wait", beforeSend.Status);
        Assert.Equal("transport_interrupted", beforeSend.Reason);

        var duringSend = MailKitNotificationTransport.ClassifyFailure(
            new OperationCanceledException(), sending: true, accepted: false);
        Assert.Equal("unknown", duringSend.Status);
        Assert.Equal("transport_interrupted", duringSend.Reason);

        var afterAccept = MailKitNotificationTransport.ClassifyFailure(
            new OperationCanceledException(), sending: true, accepted: true);
        Assert.Equal("accepted", afterAccept.Status);
        Assert.Equal("", afterAccept.Reason);

        var configuration = MailKitNotificationTransport.ClassifyFailure(
            new NotSupportedException(), sending: true, accepted: false);
        Assert.Equal("failed", configuration.Status);
        Assert.Equal("smtp_configuration", configuration.Reason);
    }

    [Fact]
    public async Task Deliver_RetriesThenAcceptsAndRecordsBoundary()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var data = SystemData("Deliver");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), "deliver-key-1", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);

        var delivery = await DeliveryAsync(note["_id"].AsObjectId);
        var calls = 0;
        _mail.Handler = _ => Task.FromResult(++calls < 5
            ? new NotificationMailResult("retry_wait", "transport_interrupted")
            : new NotificationMailResult("accepted", ""));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            _clock.Now = _clock.Now.AddHours(2);
            await _publish.DeliverAsync(delivery["_id"].AsObjectId, default);
        }

        delivery = await DeliveryAsync(note["_id"].AsObjectId);
        Assert.Equal("accepted", delivery.Text("state"));
        Assert.Equal(5L, delivery.Integer("attempts"));
        Assert.True(delivery.Contains("accepted_at") && !delivery["accepted_at"].IsBsonNull);
        Assert.Equal(5, _mail.Messages.Count);
        var sent = _mail.Messages[^1];
        Assert.Equal($"<notification.{note["_id"].AsObjectId}.{recipient["_id"].AsObjectId}@moeflow.invalid>",
            sent.MessageId);
        Assert.Contains("/dashboard/notifications/", sent.Text);
        Assert.Equal("Deliver", sent.Subject);
    }

    [Fact]
    public async Task Deliver_FailsAfterFiveRetriesAndNeverResendsUnknown()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var data = SystemData("Retries");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), "retry-key-1", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        var delivery = await DeliveryAsync(note["_id"].AsObjectId);

        _mail.Handler = _ => Task.FromResult(new NotificationMailResult("retry_wait", "smtp_response"));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            _clock.Now = _clock.Now.AddHours(2);
            await _publish.DeliverAsync(delivery["_id"].AsObjectId, default);
        }
        delivery = await DeliveryAsync(note["_id"].AsObjectId);
        Assert.Equal("failed", delivery.Text("state"));
        Assert.Equal(5L, delivery.Integer("attempts"));

        _mail.Handler = _ => Task.FromResult(new NotificationMailResult("unknown", "transport_interrupted"));
        var second = SystemData("Unknown");
        second["audience"] = ManualAudience(recipient);
        second["email"] = true;
        var unknownNote = await _publish.PublishAsync(actor, Json(second), "unknown-key-1", default);
        await _publish.DispatchAsync(unknownNote["_id"].AsObjectId, default);
        var unknown = await DeliveryAsync(unknownNote["_id"].AsObjectId);
        await _publish.DeliverAsync(unknown["_id"].AsObjectId, default);

        var before = _mail.Messages.Count;
        await CancelOtherDueNotesAsync(unknownNote["_id"].AsObjectId);
        await _publish.ScanAsync(default);
        Assert.Equal("unknown", (await DeliveryAsync(unknownNote["_id"].AsObjectId)).Text("state"));
        Assert.Equal(before, _mail.Messages.Count);
    }

    [Fact]
    public async Task Deliver_StaleLeaseCannotWriteResult()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var data = SystemData("Stale");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), "stale-key-1", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        var delivery = await DeliveryAsync(note["_id"].AsObjectId);
        var deliveryId = delivery["_id"].AsObjectId;

        _mail.Handler = async _ =>
        {
            await _services.Store.Collection("notification_delivery").UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", deliveryId),
                Builders<BsonDocument>.Update.Set("lease_token", "stolen"));
            return new NotificationMailResult("accepted", "");
        };
        await _publish.DeliverAsync(deliveryId, default);

        var current = await _services.Store.Collection("notification_delivery")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", deliveryId)).FirstAsync();
        Assert.Equal("leased", current.Text("state"));
        Assert.Equal("stolen", current.Text("lease_token"));
    }

    [Fact]
    public async Task Deliver_BannedRecipientIsSkipped()
    {
        var actor = await InsertUserAsync(admin: true);
        var recipient = await InsertUserAsync();
        var data = SystemData("Banned");
        data["audience"] = ManualAudience(recipient);
        data["email"] = true;
        var note = await _publish.PublishAsync(actor, Json(data), "banned-key-1", default);
        await _publish.DispatchAsync(note["_id"].AsObjectId, default);
        var delivery = await DeliveryAsync(note["_id"].AsObjectId);

        await _services.Store.Collection("user").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", recipient["_id"]),
            Builders<BsonDocument>.Update.Set("b", true));
        await _publish.DeliverAsync(delivery["_id"].AsObjectId, default);

        delivery = await DeliveryAsync(note["_id"].AsObjectId);
        Assert.Equal("skipped", delivery.Text("state"));
        Assert.Equal("recipient_ineligible", delivery.Text("reason"));
    }

    [Fact]
    public async Task Candidates_EscapesQueryAndPaginatesById()
    {
        var actor = await InsertUserAsync();
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team, actor, "creator");
        var literal = await InsertUserAsync(name: "a(b*c");
        await InsertTeamMemberAsync(team, literal);
        var literalTwo = await InsertUserAsync(name: "a(b*c2");
        await InsertTeamMemberAsync(team, literalTwo);
        var decoy = await InsertUserAsync(name: "axbxc");
        await InsertTeamMemberAsync(team, decoy);

        var first = await _publish.CandidatesAsync(
            actor, "team", team["_id"].AsObjectId.ToString(), "a(b*", null, "1", false, default);
        var items = Assert.IsType<object?[]>(first["items"]);
        Assert.Single(items);
        var row = Assert.IsType<Dictionary<string, object?>>(items[0]);
        Assert.Contains(row["id"], new object?[]
        {
            literal["_id"].AsObjectId.ToString(),
            literalTwo["_id"].AsObjectId.ToString(),
        });
        Assert.NotNull(first["next_cursor"]);

        var second = await _publish.CandidatesAsync(
            actor, "team", team["_id"].AsObjectId.ToString(), "a(b*",
            first["next_cursor"]?.ToString(), "1", false, default);
        var secondItems = Assert.IsType<object?[]>(second["items"]);
        Assert.Single(secondItems);
        var secondRow = Assert.IsType<Dictionary<string, object?>>(secondItems[0]);
        Assert.NotEqual(row["id"], secondRow["id"]);
        Assert.Null(second["next_cursor"]);

        var error = await Assert.ThrowsAsync<ApiError>(() => _publish.CandidatesAsync(
            actor, "team", team["_id"].AsObjectId.ToString(), null, null, "0", false, default));
        Assert.Equal("分页数量必须在 1–100 之间", error.Message);
    }

    [Fact]
    public async Task Preview_ReportsEligibilitySummaryAndVisibleNodes()
    {
        var untrusted = PublishService(trust: false);
        var actor = await InsertUserAsync();
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team, actor, "creator");
        var noEmail = await InsertNoEmailUserAsync();
        await InsertTeamMemberAsync(team, noEmail);
        var unverified = await InsertUserAsync();
        await InsertTeamMemberAsync(team, unverified);
        var verified = await InsertUserAsync();
        await InsertTeamMemberAsync(team, verified);
        await untrusted.MarkVerifiedAsync(verified.Text("e"), default);

        var data = TeamData(team, "Preview");
        data["email"] = true;
        var preview = await untrusted.PreviewAsync(actor, Json(data), null, default);

        Assert.Equal(4, Convert.ToInt32(preview["count"]));
        Assert.Equal(1L, Convert.ToInt64(preview["email_count"]));
        var summary = Assert.IsType<Dictionary<string, long>>(preview["email_summary"]);
        Assert.Equal(1L, summary["eligible"]);
        Assert.Equal(1L, summary["no_email"]);
        Assert.Equal(2L, summary["unverified_email"]);
    }

    [Fact]
    public async Task DisabledNotificationsRejectPublishAndScan()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Enabled"] = "false",
            ["Notifications:SiteOrigin"] = "https://example.test"
        }).Build();
        var permissions = new IdentityPermissions(_services.Store, _clock);
        var content = new NotificationContent(_services.Store, permissions, config);
        var read = new NotificationReadService(_services.Store, permissions, content, config, _clock);
        var publish = new NotificationPublishService(
            _services.Store, content, read, config, _mail);

        var actor = await InsertUserAsync(admin: true);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            publish.PublishAsync(actor, Json(SystemData("Disabled")), "disabled-key-1", default));
        Assert.Equal(503, error.Status);

        var note = await InsertNoteAsync(actor, state: "preparing", version: 1,
            audience: new BsonDocument("mode", "all"), publishAt: _clock.Now.AddHours(-1).UtcDateTime);
        await publish.ScanAsync(default);
        Assert.Equal("preparing", (await GetNoteAsync(note["_id"].AsObjectId)).Text("state"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private NotificationPublishService PublishService(bool trust, bool userEmail = true) =>
        new(_services.Store, _content, _read, Configuration(trust, userEmail), _mail);

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static Dictionary<string, JsonElement> NotificationPublishServiceData(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

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

    private static NotificationMailMessage Message(
        string to, string subject = "Notification", string? html = null) =>
        new(to, subject, "body", html, "<notification.1.2@moeflow.invalid>", null);

    private async Task<BsonDocument> InsertUserAsync(
        bool admin = false, bool banned = false, string? email = null, string? name = null)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name ?? "user-" + id,
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

    private async Task<BsonDocument> InsertTeamMemberAsync(
        BsonDocument team, BsonDocument user, string baseTag = "member",
        string status = "active", string[]? workerQualifications = null)
    {
        var member = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray((workerQualifications ?? []).Select(tag => (BsonValue)tag)),
            ["worker_qualifications"] = new BsonArray((workerQualifications ?? []).Select(tag => (BsonValue)tag)),
            ["status"] = status
        };
        await _services.Store.Collection("team_member").InsertOneAsync(member);
        return member;
    }

    private async Task InsertUsersAndMembersAsync(BsonDocument team, int count)
    {
        var users = new List<BsonDocument>();
        var members = new List<BsonDocument>();
        for (var index = 0; index < count; index++)
        {
            var id = ObjectId.GenerateNewId();
            users.Add(new BsonDocument
            {
                ["_id"] = id,
                ["n"] = "bulk-" + id,
                ["e"] = id + "@example.test",
                ["admin"] = false,
                ["b"] = false,
                ["p"] = new BsonDocument()
            });
            members.Add(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["team"] = team["_id"],
                ["user"] = id,
                ["base_tag"] = "member",
                ["tags"] = new BsonArray(),
                ["worker_qualifications"] = new BsonArray(),
                ["status"] = "active"
            });
        }
        await _services.Store.Collection("user").InsertManyAsync(users);
        await _services.Store.Collection("team_member").InsertManyAsync(members);
    }

    private async Task<BsonDocument> InsertProjectAsync(
        BsonDocument team, BsonDocument owner, string name = "project", int status = 0)
    {
        var id = ObjectId.GenerateNewId();
        var project = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name + "-" + id,
            ["ns"] = name + "-" + id,
            ["i"] = name + "-" + id,
            ["t"] = team["_id"],
            ["ps"] = ObjectId.GenerateNewId(),
            ["st"] = status,
            ["ou"] = owner["_id"]
        };
        await _services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task<BsonDocument> InsertProjectMemberAsync(
        BsonDocument project, BsonDocument user, string status = "active", params string[] tags)
    {
        var member = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = user["_id"],
            ["tags"] = new BsonArray(tags.Select(tag => (BsonValue)tag)),
            ["status"] = status
        };
        await _services.Store.Collection("project_member").InsertOneAsync(member);
        return member;
    }

    private async Task<BsonDocument> InsertNoteAsync(
        BsonDocument actor,
        string category = "system",
        string state = "published",
        long version = 1,
        BsonDocument? audience = null,
        BsonDocument? team = null,
        BsonDocument? project = null,
        DateTime? publishAt = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        string moderationKey = "",
        bool moderationAudited = false)
    {
        var now = _clock.Now.UtcDateTime;
        var note = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["key"] = "test-" + ObjectId.GenerateNewId(),
            ["fingerprint"] = "fingerprint-" + ObjectId.GenerateNewId(),
            ["category"] = category,
            ["source"] = "core",
            ["event_type"] = "announcement",
            ["actor_id"] = actor["_id"],
            ["team_id"] = team?["_id"] ?? BsonNull.Value,
            ["project_id"] = project?["_id"] ?? BsonNull.Value,
            ["title"] = "Notice",
            ["body"] = "",
            ["nodes"] = new BsonArray { new BsonDocument { ["type"] = "text", ["text"] = "Notice" } },
            ["search_text"] = "notice",
            ["search_tokens"] = new BsonArray(),
            ["audience"] = audience ?? new BsonDocument("mode", "all"),
            ["requested_email"] = false,
            ["payload"] = new BsonDocument(),
            ["state"] = state,
            ["created_at"] = now,
            ["publish_at"] = publishAt ?? now,
            ["expires_at"] = expiresAt is { } expiry ? expiry : BsonNull.Value,
            ["revoked_at"] = revokedAt is { } revoked ? revoked : BsonNull.Value,
            ["revoked_by"] = revokedAt is null ? BsonNull.Value : actor["_id"],
            ["revoke_reason"] = revokedAt is null ? "" : "Spam",
            ["moderation_key"] = moderationKey,
            ["moderation_action"] = "revoke",
            ["moderation_audited"] = moderationAudited,
            ["publish_audited"] = true,
            ["version"] = version,
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

    private async Task<BsonDocument> GetNoteAsync(ObjectId id) =>
        await _services.Store.Collection("notification")
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

    // ------------------------------------------------------------------
    // HTTP facts
    // ------------------------------------------------------------------

    [Fact]
    public async Task Http_RequiresTokenAndRejectsNonObjectAndUnknownFields()
    {
        using var app = HttpApp();
        using var anonymous = app.CreateClient();
        var missing = await anonymous.PostAsJsonAsync("/v1/notifications", SystemBody());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NeedTokenError", missingBody.GetProperty("error").GetString());
        Assert.Equal(1003, missingBody.GetProperty("code").GetInt32());

        var user = await InsertUserAsync();
        using var client = Authenticated(app, user);

        var array = await client.PostAsync("/v1/notifications",
            new StringContent("[1,2]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, array.StatusCode);
        var arrayBody = JsonDocument.Parse(await array.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NotificationError", arrayBody.GetProperty("error").GetString());
        Assert.Equal("请求必须是 JSON 对象", arrayBody.GetProperty("message").GetString());

        var unknown = SystemBody();
        unknown["unknown_field"] = 1;
        var rejected = await client.PostAsJsonAsync("/v1/notifications", unknown);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("通知请求包含不支持的字段", rejectedBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Http_AdminSystemSurfaceEnforcesAdministratorAndScope()
    {
        using var app = HttpApp();
        var admin = await InsertUserAsync(admin: true);
        using var adminClient = Authenticated(app, admin);
        var published = await Post(adminClient, "/v1/notifications",
            SystemBody("HTTP site notice"), "http-admin-key-01");
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var note = JsonDocument.Parse(await published.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("system", note.GetProperty("category").GetString());
        Assert.Equal("preparing", note.GetProperty("state").GetString());
        Assert.Equal(0, note.GetProperty("version").GetInt32());

        var team = await InsertTeamAsync("http-team");
        var teamBody = SystemBody("HTTP team notice");
        teamBody["category"] = "team";
        teamBody["scope_id"] = team["_id"].AsObjectId.ToString();
        var adminScope = await adminClient.PostAsJsonAsync("/v1/admin/notifications", teamBody);
        Assert.Equal(HttpStatusCode.Forbidden, adminScope.StatusCode);
        var adminScopeBody = JsonDocument.Parse(await adminScope.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("全站管理入口只能新建站点通知", adminScopeBody.GetProperty("message").GetString());

        var ordinary = await InsertUserAsync();
        using var ordinaryClient = Authenticated(app, ordinary);
        var denied = await ordinaryClient.PostAsJsonAsync("/v1/notifications", SystemBody("HTTP denied"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedBody = JsonDocument.Parse(await denied.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("没有当前范围的通知发送权限", deniedBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Http_IdempotentPublishReturns202AndChangedPayloadConflicts()
    {
        using var app = HttpApp();
        var admin = await InsertUserAsync(admin: true);
        using var client = Authenticated(app, admin);
        const string key = "http-idempotency-01";

        var first = await Post(client, "/v1/notifications", SystemBody("HTTP idempotent"), key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;

        var second = await Post(client, "/v1/notifications", SystemBody("HTTP idempotent"), key);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(firstBody.GetProperty("id").GetString(), secondBody.GetProperty("id").GetString());
        Assert.Equal(0, secondBody.GetProperty("version").GetInt32());

        var changed = await Post(client, "/v1/notifications", SystemBody("HTTP changed"), key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var changedBody = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("相同操作键不能用于不同内容", changedBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Http_DraftCasAndRevokeCas()
    {
        using var app = HttpApp();
        var admin = await InsertUserAsync(admin: true);
        using var client = Authenticated(app, admin);

        var created = await Post(client, "/v1/notifications",
            SystemBody("HTTP draft", draft: true), "http-draft-key-01");
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var draft = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id = draft.GetProperty("id").GetString()!;
        Assert.Equal("draft", draft.GetProperty("state").GetString());
        Assert.Equal(0, draft.GetProperty("version").GetInt32());

        var edit = DraftEdit(0, "HTTP draft v2");
        var edited = await client.PatchAsJsonAsync($"/v1/notifications/{id}", edit);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var editedBody = JsonDocument.Parse(await edited.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, editedBody.GetProperty("version").GetInt32());
        Assert.Equal("HTTP draft v2", editedBody.GetProperty("title").GetString());

        var stale = await client.PatchAsJsonAsync($"/v1/notifications/{id}", DraftEdit(0, "HTTP draft v3"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("草稿版本冲突", staleBody.GetProperty("message").GetString());

        var revoked = await Post(client, $"/v1/notifications/{id}/revoke",
            RevokeBody(1, "spam"), "http-revoke-01");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var revokedBody = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("cancelled", revokedBody.GetProperty("state").GetString());
        Assert.Equal("spam", revokedBody.GetProperty("revoke_reason").GetString());
        Assert.Equal(2, revokedBody.GetProperty("version").GetInt32());

        var repeated = await Post(client, $"/v1/notifications/{id}/revoke",
            RevokeBody(1, "spam"), "http-revoke-01");
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        var different = await Post(client, $"/v1/notifications/{id}/revoke",
            RevokeBody(2, "spam"), "http-revoke-02");
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        var differentBody = JsonDocument.Parse(await different.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("通知已经撤回，请刷新", differentBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Http_DisabledPublishReturns503()
    {
        using var app = HttpApp(enabled: false);
        var admin = await InsertUserAsync(admin: true);
        using var client = Authenticated(app, admin);
        var response = await client.PostAsJsonAsync("/v1/notifications", SystemBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NotificationError", body.GetProperty("error").GetString());
        Assert.Equal(4600, body.GetProperty("code").GetInt32());
        Assert.Equal("通知系统尚未启用", body.GetProperty("message").GetString());
    }

    private WebApplicationFactory<Program> HttpApp(bool enabled = true) =>
        _services.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Enabled"] = enabled ? "true" : "false",
                ["Notifications:SiteOrigin"] = "https://example.test",
                ["Notifications:WorkerEnabled"] = "false",
                ["Notifications:UserEmailEnabled"] = "true",
                ["Notifications:TrustExistingEmails"] = "true",
                ["Notifications:MaxAudience"] = "20000"
            })));

    private static HttpClient Authenticated(WebApplicationFactory<Program> app, BsonDocument user)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));
        return client;
    }

    private static Dictionary<string, object?> SystemBody(string title = "HTTP notice", bool draft = false) => new()
    {
        ["category"] = "system",
        ["title"] = title,
        ["body"] = "",
        ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" },
        ["draft"] = draft
    };

    private static Dictionary<string, object?> DraftEdit(long version, string title) => new()
    {
        ["version"] = version,
        ["category"] = "system",
        ["title"] = title,
        ["body"] = "",
        ["audience"] = new Dictionary<string, object?> { ["mode"] = "all" },
        ["draft"] = true
    };

    private static Dictionary<string, object?> RevokeBody(long version, string reason) => new()
    {
        ["confirmed"] = true,
        ["version"] = version,
        ["reason"] = reason
    };

    private static async Task<HttpResponseMessage> Post(
        HttpClient client, string path, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}

