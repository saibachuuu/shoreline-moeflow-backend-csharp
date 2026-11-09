using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// Direct service/database coverage for the notification read surface. Program.cs
// does not map the routes yet, so nothing here depends on HTTP wiring.
[Collection("local-services")]
public sealed class NotificationTests
{
    private readonly LocalServices services;
    private readonly NotificationClock _clock;
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Enabled"] = "true",
            ["Notifications:SiteOrigin"] = "https://example.test"
        })
        .Build();
    private readonly IdentityPermissions _permissions;
    private readonly NotificationContent _content;
    private readonly NotificationReadService _read;

    private sealed class NotificationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public NotificationTests(LocalServices services)
    {
        this.services = services;
        _clock = new NotificationClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        _permissions = new IdentityPermissions(services.Store, _clock);
        _content = new NotificationContent(services.Store, _permissions, _configuration);
        _read = new NotificationReadService(services.Store, _permissions, _content, _configuration, _clock);
    }

    private async Task<BsonDocument> InsertUserAsync(bool admin = false, bool banned = false)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "user-" + id,
            ["e"] = id + "@example.test",
            ["admin"] = admin,
            ["b"] = banned,
            ["p"] = new BsonDocument()
        };
        await services.Store.Collection("user").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertTeamAsync(string name = "team")
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument { ["_id"] = id, ["n"] = name + "-" + id };
        await services.Store.Collection("team").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertTeamMemberAsync(
        ObjectId teamId, ObjectId userId, string baseTag = "member", string status = "active",
        params string[] tags)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = teamId,
            ["user"] = userId,
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray(tags.Select(tag => (BsonValue)tag)),
            ["worker_qualifications"] = new BsonArray(),
            ["status"] = status
        };
        await services.Store.Collection("team_member").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertProjectAsync(
        ObjectId teamId, ObjectId ownerId, string name, int status = 0)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name + "-" + id,
            ["ns"] = name + "-" + id,
            ["i"] = name + "-" + id,
            ["t"] = teamId,
            ["ps"] = ObjectId.GenerateNewId(),
            ["st"] = status,
            ["ou"] = ownerId
        };
        await services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertProjectMemberAsync(
        ObjectId projectId, ObjectId userId, string status = "active", params string[] tags)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = projectId,
            ["user"] = userId,
            ["tags"] = new BsonArray(tags.Select(tag => (BsonValue)tag)),
            ["status"] = status
        };
        await services.Store.Collection("project_member").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertNoteAsync(
        string category = "system", string state = "published", ObjectId? teamId = null,
        ObjectId? projectId = null, BsonDocument? audience = null, ObjectId? actorId = null,
        string title = "Notice", string searchText = "", BsonArray? nodes = null,
        BsonDocument? payload = null, DateTime? expiresAt = null, DateTime? revokedAt = null,
        string eventType = "announcement")
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["key"] = "test-" + id,
            ["fingerprint"] = "fingerprint-" + id,
            ["category"] = category,
            ["source"] = "core",
            ["event_type"] = eventType,
            ["actor_id"] = actorId ?? ObjectId.GenerateNewId(),
            ["team_id"] = teamId is { } team ? team : BsonNull.Value,
            ["project_id"] = projectId is { } project ? project : BsonNull.Value,
            ["title"] = title,
            ["body"] = "",
            ["nodes"] = nodes ?? new BsonArray(),
            ["search_text"] = searchText,
            ["search_tokens"] = new BsonArray(
                NotificationContent.SearchTokens(searchText).Select(token => (BsonValue)token)),
            ["audience"] = audience ?? new BsonDocument(),
            ["requested_email"] = false,
            ["payload"] = payload ?? new BsonDocument(),
            ["state"] = state,
            ["created_at"] = _clock.Now.UtcDateTime,
            ["publish_at"] = _clock.Now.UtcDateTime,
            ["expires_at"] = expiresAt is { } expires ? new BsonDateTime(expires) : BsonNull.Value,
            ["revoked_at"] = revokedAt is { } revoked ? new BsonDateTime(revoked) : BsonNull.Value,
            ["recipient_count"] = 1,
            ["version"] = 1
        };
        await services.Store.Collection("notification").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertReceiptAsync(
        ObjectId notificationId, ObjectId userId, DateTime? createdAt = null,
        DateTime? readAt = null, bool archived = false)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["notification_id"] = notificationId,
            ["user_id"] = userId,
            ["created_at"] = createdAt is { } created ? new BsonDateTime(created) : new BsonDateTime(_clock.Now.UtcDateTime),
            ["read_at"] = readAt is { } read ? new BsonDateTime(read) : BsonNull.Value,
            ["archived"] = archived
        };
        await services.Store.Collection("notification_receipt").InsertOneAsync(document);
        return document;
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static string Id(BsonDocument document) => document["_id"].AsObjectId.ToString();

    private static string[] NodeTypes(BsonArray nodes) =>
        nodes.Select(node => node.AsBsonDocument.Text("type")).ToArray();

    [Fact]
    public void ParseBody_ValidTagsAndPlainText()
    {
        var nodes = NotificationContent.ParseBody(
            "[b]Hi[/b] [url=https://example.test/x]link[/url]\n[list][*]one[/list]");

        Assert.Equal(["b", "text", "url", "text", "list"], NodeTypes(nodes));
        Assert.Equal("Hi link\none", NotificationContent.Plain(nodes));
        var list = nodes[4].AsBsonDocument["children"].AsBsonArray;
        Assert.Equal("li", list[0].AsBsonDocument.Text("type"));
    }

    [Fact]
    public void ParseBody_EnforcesRuneTokenAndDepthBounds()
    {
        var runeError = Assert.Throws<ApiError>(() => NotificationContent.ParseBody(new string('a', 10001)));
        Assert.Equal(400, runeError.Status);
        Assert.Equal("通知正文不能超过 10000 字符", runeError.Message);

        var tokens = string.Concat(Enumerable.Repeat("[b][/b]", 1001));
        var tokenError = Assert.Throws<ApiError>(() => NotificationContent.ParseBody(tokens));
        Assert.Equal("通知格式节点过多", tokenError.Message);

        var depth = string.Concat(Enumerable.Repeat("[quote]", 12));
        var depthError = Assert.Throws<ApiError>(() => NotificationContent.ParseBody(depth));
        Assert.Equal("BBCode 嵌套过深", depthError.Message);

        var closed = NotificationContent.ParseBody(
            string.Concat(Enumerable.Repeat("[quote]", 11)) + "x" +
            string.Concat(Enumerable.Repeat("[/quote]", 11)));
        Assert.Single(closed);
    }

    [Fact]
    public void ParseBody_RejectsUnsafeUrlsAndMalformedNesting()
    {
        Assert.Equal("链接只支持安全的 HTTP/HTTPS 地址",
            Assert.Throws<ApiError>(() => NotificationContent.ParseBody("[url=javascript:alert(1)]x[/url]")).Message);
        Assert.Equal("链接只支持安全的 HTTP/HTTPS 地址",
            Assert.Throws<ApiError>(() => NotificationContent.ParseBody("[url=https://user:pass@example.test/]x[/url]")).Message);
        Assert.Equal("链接只支持安全的 HTTP/HTTPS 地址",
            Assert.Throws<ApiError>(() => NotificationContent.ParseBody("[url=https://example.test/\u0001]x[/url]")).Message);
        Assert.Equal("不支持的标签参数",
            Assert.Throws<ApiError>(() => NotificationContent.ParseBody("[b=oops]x[/b]")).Message);
        Assert.Equal("BBCode 标签未正确闭合",
            Assert.Throws<ApiError>(() => NotificationContent.ParseBody("[b]x")).Message);
    }

    [Fact]
    public void RenderHtml_EscapesTextAndAttributeValues()
    {
        var html = NotificationContent.RenderHtml(NotificationContent.ParseBody(
            "[url=https://example.test/?q=1]click <script>alert(1)</script>[/url] <img src=x onerror=1>"));

        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("href=\"https://example.test/?q=1\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void SearchTokens_AreSortedUniqueRunesAndNeverSplitSurrogates()
    {
        Assert.Equal(["😀😀"], NotificationContent.SearchTokens("😀😀"));
        var token = NotificationContent.SearchTokens("😀😀")[0];
        Assert.Equal(2, token.EnumerateRunes().Count());
        Assert.DoesNotContain('\uFFFD', token);

        Assert.Equal(["ab", "bc", "ca"], NotificationContent.SearchTokens("cabc"));
    }

    [Fact]
    public void Normalize_MatchesCaseFoldGoldenVectors()
    {
        Assert.Equal("strasse", NotificationContent.Normalize("Straße"));
        Assert.Equal("σ", NotificationContent.Normalize("Σ"));
        Assert.Equal("σ", NotificationContent.Normalize("ς"));
        Assert.Equal("full", NotificationContent.Normalize("Ｆｕｌｌ"));
        Assert.Equal("ffi", NotificationContent.Normalize("ﬃ"));
        Assert.Equal("a b", NotificationContent.Normalize("  A\tB  "));
    }

    [Fact]
    public async Task VisibleNodes_ProjectAccessRevocationLeaksNothing()
    {
        var owner = await InsertUserAsync();
        var viewer = await InsertUserAsync();
        var team = await InsertTeamAsync();
        var project = await InsertProjectAsync(team["_id"].AsObjectId, owner["_id"].AsObjectId,
            "Secret Project Name");
        var member = await InsertProjectMemberAsync(
            project["_id"].AsObjectId, viewer["_id"].AsObjectId);
        var nodes = new BsonArray
        {
            new BsonDocument { ["type"] = "project", ["project_id"] = Id(project) }
        };

        var visible = await _content.VisibleNodesAsync(nodes, viewer, default);
        Assert.Equal("project", visible[0].AsBsonDocument.Text("type"));
        Assert.Equal(project.Text("n"), visible[0].AsBsonDocument.Text("name"));

        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument("_id", member["_id"]),
            Builders<BsonDocument>.Update.Set("status", "removed"));
        var hidden = await _content.VisibleNodesAsync(nodes, viewer, default);
        Assert.Equal("unavailable", hidden[0].AsBsonDocument.Text("type"));
        Assert.DoesNotContain(project.Text("n"), hidden.ToJson());
        Assert.DoesNotContain(Id(project), hidden.ToJson());
    }

    [Fact]
    public async Task Receipt_RequiresOwnVisibleReceipt()
    {
        var actor = await InsertUserAsync();
        var owner = await InsertUserAsync();
        var stranger = await InsertUserAsync();
        var note = await InsertNoteAsync(actorId: actor["_id"].AsObjectId);
        await InsertReceiptAsync(note["_id"].AsObjectId, owner["_id"].AsObjectId);

        var (_, receipt) = await _read.GetReceiptAsync(owner, Id(note), default);
        Assert.Equal(owner["_id"].AsObjectId, receipt["user_id"].AsObjectId);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(stranger, Id(note), default));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Receipt_ManualAudienceMismatchIsHiddenEvenWithReceipt()
    {
        var owner = await InsertUserAsync();
        var viewer = await InsertUserAsync();
        var other = await InsertUserAsync();
        var team = await InsertTeamAsync();
        var project = await InsertProjectAsync(team["_id"].AsObjectId, owner["_id"].AsObjectId, "manual");
        await InsertProjectMemberAsync(project["_id"].AsObjectId, viewer["_id"].AsObjectId);
        var note = await InsertNoteAsync("project", projectId: project["_id"].AsObjectId, actorId: owner["_id"].AsObjectId,
            audience: new BsonDocument
            {
                ["mode"] = "manual",
                ["user_ids"] = new BsonArray { other["_id"] }
            });
        await InsertReceiptAsync(note["_id"].AsObjectId, viewer["_id"].AsObjectId);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(viewer, Id(note), default));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Receipt_BannedExpiredRevokedAndRemovedMemberAreLiveHidden()
    {
        var owner = await InsertUserAsync();
        var viewer = await InsertUserAsync();
        var banned = await InsertUserAsync(banned: true);

        var system = await InsertNoteAsync(actorId: owner["_id"].AsObjectId);
        var expired = await InsertNoteAsync(actorId: owner["_id"].AsObjectId,
            expiresAt: _clock.Now.UtcDateTime.AddMinutes(-1));
        var revoked = await InsertNoteAsync(actorId: owner["_id"].AsObjectId,
            revokedAt: _clock.Now.UtcDateTime);
        await InsertReceiptAsync(system["_id"].AsObjectId, banned["_id"].AsObjectId);
        await InsertReceiptAsync(expired["_id"].AsObjectId, viewer["_id"].AsObjectId);
        await InsertReceiptAsync(revoked["_id"].AsObjectId, viewer["_id"].AsObjectId);

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(banned, Id(system), default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(viewer, Id(expired), default))).Status);
        Assert.False(_read.IsLive(expired));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(viewer, Id(revoked), default))).Status);

        var team = await InsertTeamAsync();
        var project = await InsertProjectAsync(team["_id"].AsObjectId, owner["_id"].AsObjectId, "member");
        var member = await InsertProjectMemberAsync(project["_id"].AsObjectId, viewer["_id"].AsObjectId);
        var projectNote = await InsertNoteAsync("project", projectId: project["_id"].AsObjectId,
            actorId: owner["_id"].AsObjectId);
        await InsertReceiptAsync(projectNote["_id"].AsObjectId, viewer["_id"].AsObjectId);
        await _read.GetReceiptAsync(viewer, Id(projectNote), default);

        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument("_id", member["_id"]),
            Builders<BsonDocument>.Update.Set("status", "removed"));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(viewer, Id(projectNote), default))).Status);
    }

    [Fact]
    public async Task Inbox_FilteredCursorDoesNotSkipOrDuplicateVisibleRows()
    {
        var actor = await InsertUserAsync();
        var viewer = await InsertUserAsync();
        for (var index = 0; index < 6; index++)
        {
            var note = await InsertNoteAsync(actorId: actor["_id"].AsObjectId,
                title: "alpha " + index, searchText: "alpha " + index);
            await InsertReceiptAsync(note["_id"].AsObjectId, viewer["_id"].AsObjectId);
        }

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var inbox = await _read.InboxAsync(viewer, "alpha", null, cursor, null, null, "2", default);
            var items = Assert.IsType<List<Dictionary<string, object?>>>(inbox["items"]);
            seen.AddRange(items.Select(item => Assert.IsType<string>(item["id"])));
            cursor = inbox["next_cursor"] as string;
            if (cursor is null) break;
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task UnreadCounts_MarkReadCutoffAndArchiveReadSemantics()
    {
        var actor = await InsertUserAsync();
        var viewer = await InsertUserAsync();
        var notes = new List<BsonDocument>();
        for (var index = 0; index < 3; index++)
        {
            var note = await InsertNoteAsync(actorId: actor["_id"].AsObjectId);
            notes.Add(note);
            await InsertReceiptAsync(note["_id"].AsObjectId, viewer["_id"].AsObjectId,
                createdAt: _clock.Now.UtcDateTime.AddMinutes(-1));
        }

        var counts = await _read.UnreadCountsAsync(viewer, default);
        Assert.Equal(3L, counts["total"]);
        Assert.Equal(3L, counts["system"]);

        var marked = await _read.MarkReadAsync(viewer, Json("{}"), null, null, null, default);
        Assert.Equal(3L, marked["changed"]);
        Assert.Equal(0L, (await _read.UnreadCountsAsync(viewer, default))["total"]);

        var archived = await _read.UpdateReceiptAsync(
            viewer, Id(notes[0]), Json("""{"archived":true}"""), default);
        Assert.Equal(true, archived["ok"]);
        var receipt = await services.Store.Collection("notification_receipt").Find(
            Builders<BsonDocument>.Filter.Eq("notification_id", notes[0]["_id"].AsObjectId) &
            Builders<BsonDocument>.Filter.Eq("user_id", viewer["_id"].AsObjectId)).FirstAsync();
        Assert.True(receipt["archived"].AsBoolean);
        Assert.False(receipt["read_at"].IsBsonNull);

        await services.Store.Collection("notification_receipt").UpdateOneAsync(
            new BsonDocument("_id", receipt["_id"]),
            Builders<BsonDocument>.Update.Set("read_at", BsonNull.Value));
        Assert.Equal(0L, (await _read.UnreadCountsAsync(viewer, default))["total"]);

        await _read.UpdateReceiptAsync(viewer, Id(notes[0]), Json("""{"read":false}"""), default);
        var unreadArchived = await services.Store.Collection("notification_receipt")
            .Find(new BsonDocument("_id", receipt["_id"])).FirstAsync();
        Assert.True(unreadArchived["read_at"].IsBsonNull);
        Assert.True(unreadArchived["archived"].AsBoolean);
        Assert.Equal(0L, (await _read.UnreadCountsAsync(viewer, default))["total"]);
    }

    [Fact]
    public async Task Preferences_DefaultsAndVersionCasConflict()
    {
        await NotificationRegistration.InitializeAsync(services.Store, default);
        var user = await InsertUserAsync();
        var defaults = await _read.PreferencesAsync(user, default);
        var defaultCategories = Assert.IsType<Dictionary<string, object?>>(defaults["categories"]);
        Assert.Equal(0L, defaults["version"]);
        Assert.All(NotificationReadService.Categories, category => Assert.Equal(true, defaultCategories[category]));

        var saved = await _read.SavePreferencesAsync(user,
            Json("""{"version":0,"categories":{"system":false}}"""), default);
        Assert.Equal(1L, saved["version"]);
        var savedCategories = Assert.IsType<Dictionary<string, object?>>(saved["categories"]);
        Assert.Equal(false, savedCategories["system"]);

        var stale = await Assert.ThrowsAsync<ApiError>(() => _read.SavePreferencesAsync(user,
            Json("""{"version":0,"categories":{"system":true}}"""), default));
        Assert.Equal(409, stale.Status);

        var invalid = await Assert.ThrowsAsync<ApiError>(() => _read.SavePreferencesAsync(user,
            Json("""{"version":1,"categories":{"system":false},"metadata":{}}"""), default));
        Assert.Equal(400, invalid.Status);
        var nonBool = await Assert.ThrowsAsync<ApiError>(() => _read.SavePreferencesAsync(user,
            Json("""{"version":1,"categories":{"system":"false"}}"""), default));
        Assert.Equal(400, nonBool.Status);
    }

    [Fact]
    public async Task Policy_ReadableByMemberAndWritableOnlyByCreatorWithCas()
    {
        await NotificationRegistration.InitializeAsync(services.Store, default);
        var creator = await InsertUserAsync();
        var admin = await InsertUserAsync();
        var member = await InsertUserAsync();
        var team = await InsertTeamAsync();
        await InsertTeamMemberAsync(team["_id"].AsObjectId, creator["_id"].AsObjectId, "creator");
        await InsertTeamMemberAsync(team["_id"].AsObjectId, admin["_id"].AsObjectId, "admin");
        await InsertTeamMemberAsync(team["_id"].AsObjectId, member["_id"].AsObjectId, "member");

        var defaults = await _read.PolicyAsync(member, Id(team), default);
        Assert.Equal(false, defaults["team_admin"]);
        Assert.Equal(0L, defaults["version"]);

        var forbidden = await Assert.ThrowsAsync<ApiError>(() => _read.SavePolicyAsync(admin, Id(team),
            Json("""{"version":0,"team_admin":true,"project_admin":true}"""), default));
        Assert.Equal(403, forbidden.Status);

        var saved = await _read.SavePolicyAsync(creator, Id(team),
            Json("""{"version":0,"team_admin":true,"project_admin":false}"""), default);
        Assert.Equal(1L, saved["version"]);
        var current = await _read.PolicyAsync(member, Id(team), default);
        Assert.Equal(true, current["team_admin"]);
        Assert.Equal(false, current["project_admin"]);

        var stale = await Assert.ThrowsAsync<ApiError>(() => _read.SavePolicyAsync(creator, Id(team),
            Json("""{"version":0,"team_admin":false,"project_admin":false}"""), default));
        Assert.Equal(409, stale.Status);
        var invalid = await Assert.ThrowsAsync<ApiError>(() => _read.SavePolicyAsync(creator, Id(team),
            Json("""{"version":1,"team_admin":1,"project_admin":false}"""), default));
        Assert.Equal(400, invalid.Status);
    }

    [Fact]
    public async Task Throttle_ConcurrentRequestsAllowExactlyOne()
    {
        await NotificationRegistration.InitializeAsync(services.Store, default);
        var user = await InsertUserAsync();
        async Task<bool> Attempt()
        {
            try
            {
                await _read.ThrottleAsync(user, "cards", 1, default);
                return true;
            }
            catch (ApiError error)
            {
                Assert.Equal(429, error.Status);
                return false;
            }
        }

        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Equal(1, results.Count(result => result));
    }

    [Fact]
    public async Task Capabilities_DisabledReturnsZerosAndNoSend()
    {
        var admin = await InsertUserAsync(admin: true);
        var disabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Enabled"] = "false"
            }).Build();
        var disabled = new NotificationReadService(services.Store, _permissions, _content,
            disabledConfiguration, _clock);

        var capabilities = await disabled.CapabilitiesAsync(admin, "system", null, default);
        Assert.Equal(false, capabilities["enabled"]);
        Assert.Equal(false, capabilities["can_send"]);
        Assert.Equal(false, capabilities["can_view_sent"]);
        Assert.Equal(true, capabilities["can_manage"]);

        var counts = await disabled.UnreadCountsAsync(admin, default);
        Assert.Equal(0, counts["total"]);
        Assert.All(NotificationReadService.Categories, category => Assert.Equal(0, counts[category]));
    }

    [Fact]
    public async Task ProjectCards_SearchAndResolveDoNotLeakRevokedProjects()
    {
        var owner = await InsertUserAsync();
        var viewer = await InsertUserAsync();
        var team = await InsertTeamAsync();
        var prefix = "card-" + Guid.NewGuid().ToString("N");
        var allowed = await InsertProjectAsync(team["_id"].AsObjectId, owner["_id"].AsObjectId, prefix + "-allowed");
        var denied = await InsertProjectAsync(team["_id"].AsObjectId, owner["_id"].AsObjectId, prefix + "-denied");
        var member = await InsertProjectMemberAsync(allowed["_id"].AsObjectId, viewer["_id"].AsObjectId);

        var search = await _read.CardSearchAsync(viewer, prefix, null, default);
        var items = Assert.IsType<List<Dictionary<string, object?>>>(search["items"]);
        Assert.Contains(items, item => (string?)item["id"] == Id(allowed));
        Assert.DoesNotContain(items, item => (string?)item["id"] == Id(denied));

        var resolved = await _read.CardResolveAsync(viewer,
            Json($$"""{"project_ids":["{{Id(allowed)}}","{{Id(denied)}}"]}"""), default);
        var nodes = Assert.IsType<object?[]>(resolved["nodes"]);
        Assert.Equal(2, nodes.Length);
        Assert.Contains(nodes, node =>
            Assert.IsType<Dictionary<string, object?>>(node)["type"] as string == "unavailable");

        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument("_id", member["_id"]),
            Builders<BsonDocument>.Update.Set("status", "removed"));
        var revoked = await _read.CardResolveAsync(viewer,
            Json($$"""{"project_ids":["{{Id(allowed)}}"]}"""), default);
        var revokedNodes = Assert.IsType<object?[]>(revoked["nodes"]);
        var payload = JsonSerializer.Serialize(revokedNodes);
        Assert.DoesNotContain(allowed.Text("n"), payload);
        Assert.DoesNotContain(Id(allowed), payload);
        Assert.Contains("unavailable", payload);
    }

    [Fact]
    public async Task Initialize_CreatesUniqueIndexesAndThrottleTtl()
    {
        await NotificationRegistration.InitializeAsync(services.Store, default);
        using var receiptCursor = await services.Store.Collection("notification_receipt")
            .Indexes.ListAsync();
        var receiptIndexes = await receiptCursor.ToListAsync();
        var uniqueReceipt = receiptIndexes.Single(index => index["name"] == "notification_id_1_user_id_1");
        Assert.True(uniqueReceipt["unique"].AsBoolean);

        using var throttleCursor = await services.Store.Collection("notification_throttle")
            .Indexes.ListAsync();
        var throttleIndexes = await throttleCursor.ToListAsync();
        var ttl = throttleIndexes.Single(index => index["name"] == "expires_at_1");
        Assert.Equal(0, ttl["expireAfterSeconds"].ToInt32());

        var notificationId = ObjectId.GenerateNewId();
        var userId = ObjectId.GenerateNewId();
        await services.Store.Collection("notification_receipt").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["notification_id"] = notificationId,
            ["user_id"] = userId,
            ["created_at"] = _clock.Now.UtcDateTime,
            ["read_at"] = BsonNull.Value,
            ["archived"] = false
        });
        await Assert.ThrowsAsync<MongoWriteException>(() =>
            services.Store.Collection("notification_receipt").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["notification_id"] = notificationId,
                ["user_id"] = userId,
                ["created_at"] = _clock.Now.UtcDateTime,
                ["read_at"] = BsonNull.Value,
                ["archived"] = false
            }));
    }

    [Fact]
    public async Task Detail_ExcerptUsesUnicodeCodePointOffsets()
    {
        var user = await InsertUserAsync();
        var searchText = string.Concat(Enumerable.Repeat("😀", 50)) + "needle" + new string('x', 200);
        var note = await InsertNoteAsync(actorId: user["_id"].AsObjectId, searchText: searchText);

        var detail = await _read.DetailAsync(note, user, searchQuery: "needle", ct: default);

        var excerpt = Assert.IsType<string>(detail["excerpt"]);
        Assert.StartsWith("…" + string.Concat(Enumerable.Repeat("😀", 45)) + "needle", excerpt);
        Assert.EndsWith("…", excerpt);
    }

    [Fact]
    public async Task ProofreadDraftIsVisibleOnlyToItsActor()
    {
        var actor = await InsertUserAsync();
        var stranger = await InsertUserAsync();
        var note = await InsertNoteAsync(
            "project", state: "draft", actorId: actor["_id"].AsObjectId,
            eventType: "proofread_feedback",
            payload: new BsonDocument("text", "private-feedback"));
        await InsertReceiptAsync(note["_id"].AsObjectId, actor["_id"].AsObjectId);
        await InsertReceiptAsync(note["_id"].AsObjectId, stranger["_id"].AsObjectId);

        Assert.False(_read.IsLive(note));
        var (_, actorReceipt) = await _read.GetReceiptAsync(actor, Id(note), default);
        var detail = await _read.DetailAsync(note, actor, receipt: actorReceipt, ct: default);
        Assert.Equal("private-feedback", detail["feedback_text"]);

        var denied = await Assert.ThrowsAsync<ApiError>(() =>
            _read.GetReceiptAsync(stranger, Id(note), default));
        Assert.Equal(404, denied.Status);
        Assert.Equal("通知不存在或不可访问", denied.ResponseMessage);
    }

    [Fact]
    public void Registration_HasNoCircularDependency()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(services.Store);
        collection.AddSingleton<TimeProvider>(_clock);
        collection.AddSingleton(_permissions);
        collection.AddSingleton(_configuration);
        collection.AddNotificationServices();

        using var provider = collection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NotificationReadService>());
    }
}
