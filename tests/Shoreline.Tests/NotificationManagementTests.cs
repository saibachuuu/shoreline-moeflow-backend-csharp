using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Notifications;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class NotificationManagementTests(LocalServices services)
{
    private const string PasswordHash = "seed-password-hash";

    [Fact]
    public async Task SiteAdminOrdinarySentOnlyListsOwnSystemNotifications()
    {
        var admin = await AddUserAsync(admin: true, "site-admin");
        var other = await AddUserAsync(name: "other-actor");
        var own = await AddNoteAsync(admin, "system", title: "own-system");
        var foreign = await AddNoteAsync(other, "system", title: "foreign-system");
        var api = Build();

        var items = Items(await api.Management.SentAsync(admin,
            new NotificationManagementQuery(Category: "system"), default));

        Assert.Contains(items, item => Id(item) == own["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(items, item => Id(item) == foreign["_id"].AsObjectId.ToString());
    }

    [Fact]
    public async Task TeamCreatorSeesScopeWhileTeamAdminSeesOnlyOwn()
    {
        var creator = await AddUserAsync(name: "team-creator");
        var admin = await AddUserAsync(name: "team-admin");
        var team = await AddTeamAsync();
        await AddTeamMemberAsync(team, creator, "creator");
        await AddTeamMemberAsync(team, admin, "admin");
        await AddPolicyAsync(team, teamAdmin: true);
        var creatorNote = await AddNoteAsync(creator, "team", teamId: team);
        var adminNote = await AddNoteAsync(admin, "team", teamId: team);
        var api = Build();

        var creatorItems = Items(await api.Management.SentAsync(creator,
            new NotificationManagementQuery(Category: "team", ScopeId: team.ToString()), default));
        Assert.Contains(creatorItems, item => Id(item) == creatorNote["_id"].AsObjectId.ToString());
        Assert.Contains(creatorItems, item => Id(item) == adminNote["_id"].AsObjectId.ToString());

        var adminItems = Items(await api.Management.SentAsync(admin,
            new NotificationManagementQuery(Category: "team", ScopeId: team.ToString()), default));
        Assert.Contains(adminItems, item => Id(item) == adminNote["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(adminItems, item => Id(item) == creatorNote["_id"].AsObjectId.ToString());
    }

    [Fact]
    public async Task ProjectOwnerSeesWholeScopeWhileRemovedMemberIsDenied()
    {
        var owner = await AddUserAsync(name: "project-owner");
        var contributor = await AddUserAsync(name: "project-contributor");
        var team = await AddTeamAsync();
        var project = await AddProjectAsync(owner, team);
        await AddProjectMemberAsync(project, owner, []);
        await AddProjectMemberAsync(project, contributor, []);
        var ownerNote = await AddNoteAsync(owner, "project", projectId: project);
        var contributorNote = await AddNoteAsync(contributor, "project", projectId: project);
        var api = Build();

        var items = Items(await api.Management.SentAsync(owner,
            new NotificationManagementQuery(Category: "project", ScopeId: project.ToString()), default));
        Assert.Contains(items, item => Id(item) == ownerNote["_id"].AsObjectId.ToString());
        Assert.Contains(items, item => Id(item) == contributorNote["_id"].AsObjectId.ToString());

        await Store.Collection("project_member").DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("project", project) &
            Builders<BsonDocument>.Filter.Eq("user", contributor["_id"]));
        var error = await Assert.ThrowsAsync<ApiError>(() => api.Management.SentAsync(contributor,
            new NotificationManagementQuery(Category: "project", ScopeId: project.ToString()), default));
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task ProofreadOnlyMemberSeesOnlyOwnProofreadFeedback()
    {
        var owner = await AddUserAsync(name: "proofread-owner");
        var proofreader = await AddUserAsync(name: "proofreader");
        var team = await AddTeamAsync();
        var project = await AddProjectAsync(owner, team);
        await AddProjectMemberAsync(project, owner, []);
        await AddProjectMemberAsync(project, proofreader, ["proofreader"]);
        var ownFeedback = await AddNoteAsync(proofreader, "project", projectId: project,
            eventType: "proofread_feedback", title: "own-feedback");
        var foreignFeedback = await AddNoteAsync(owner, "project", projectId: project,
            eventType: "proofread_feedback", title: "foreign-feedback");
        var ownAnnouncement = await AddNoteAsync(proofreader, "project", projectId: project,
            eventType: "announcement", title: "own-announcement");
        var api = Build();

        var items = Items(await api.Management.SentAsync(proofreader,
            new NotificationManagementQuery(Category: "project", ScopeId: project.ToString()), default));

        Assert.Contains(items, item => Id(item) == ownFeedback["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(items, item => Id(item) == foreignFeedback["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(items, item => Id(item) == ownAnnouncement["_id"].AsObjectId.ToString());
    }

    [Fact]
    public async Task TeamAndProjectScopesAreStrictlyFiltered()
    {
        var owner = await AddUserAsync(name: "scope-owner");
        var teamA = await AddTeamAsync("team-a");
        var teamB = await AddTeamAsync("team-b");
        var projectA = await AddProjectAsync(owner, teamA);
        var projectB = await AddProjectAsync(owner, teamB);
        await AddProjectMemberAsync(projectA, owner, []);
        await AddProjectMemberAsync(projectB, owner, []);
        await AddTeamMemberAsync(teamA, owner, "creator");
        await AddTeamMemberAsync(teamB, owner, "creator");
        var ownA = await AddNoteAsync(owner, "project", projectId: projectA);
        var ownB = await AddNoteAsync(owner, "project", projectId: projectB);
        var teamNoteB = await AddNoteAsync(owner, "team", teamId: teamB);
        var api = Build();

        var projectItems = Items(await api.Management.SentAsync(owner,
            new NotificationManagementQuery(Category: "project", ScopeId: projectA.ToString()), default));
        Assert.Contains(projectItems, item => Id(item) == ownA["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(projectItems, item => Id(item) == ownB["_id"].AsObjectId.ToString());

        var teamItems = Items(await api.Management.SentAsync(owner,
            new NotificationManagementQuery(Category: "team", ScopeId: teamA.ToString()), default));
        Assert.DoesNotContain(teamItems, item => Id(item) == teamNoteB["_id"].AsObjectId.ToString());
    }

    [Fact]
    public async Task SearchUsesLiteralSubstringTokensAndValidatesInput()
    {
        var admin = await AddUserAsync(admin: true, "search-admin");
        var literal = await AddNoteAsync(admin, "system", title: "literal", searchText: "a.b[test] page");
        var near = await AddNoteAsync(admin, "system", title: "near", searchText: "axb[test] page");
        var api = Build();

        var items = Items(await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(Q: "a.b", ActorId: admin["_id"].AsObjectId.ToString()), default));
        Assert.Contains(items, item => Id(item) == literal["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(items, item => Id(item) == near["_id"].AsObjectId.ToString());

        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(Q: "a"), default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(Q: new string('x', 101)), default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(Category: "not-a-category"), default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(Cursor: "not-an-object-id"), default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(DeliveryState: "bogus"), default))).Status);
    }

    [Fact]
    public async Task PaginationAndDeliveryStateFiltersAreBounded()
    {
        var admin = await AddUserAsync(admin: true, "paging-admin");
        var first = await AddNoteAsync(admin, "system", title: "page-1");
        var second = await AddNoteAsync(admin, "system", title: "page-2");
        var third = await AddNoteAsync(admin, "system", title: "page-3");
        await AddDeliveryAsync(first, admin, "accepted");
        await AddDeliveryAsync(second, admin, "failed");
        var api = Build();

        var page1 = await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(
                ActorId: admin["_id"].AsObjectId.ToString(), Limit: "2"), default);
        var items1 = Items(page1);
        Assert.Equal(2, items1.Count);
        var cursor = page1["next_cursor"] as string;
        Assert.False(string.IsNullOrEmpty(cursor));

        var page2 = await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(
                ActorId: admin["_id"].AsObjectId.ToString(), Cursor: cursor, Limit: "2"), default);
        Assert.Single(Items(page2));

        var accepted = Items(await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(
                ActorId: admin["_id"].AsObjectId.ToString(),
                DeliveryState: "accepted"), default));
        Assert.Contains(accepted, item => Id(item) == first["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(accepted, item => Id(item) == second["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(accepted, item => Id(item) == third["_id"].AsObjectId.ToString());
    }

    [Fact]
    public async Task ExpiryRevocationAndDateFiltersFollowLegacySemantics()
    {
        var admin = await AddUserAsync(admin: true, "expiry-admin");
        var expired = await AddNoteAsync(admin, "system", title: "expired",
            expires: DateTime.UtcNow.AddHours(-1));
        var live = await AddNoteAsync(admin, "system", title: "live",
            expires: DateTime.UtcNow.AddHours(1));
        var revoked = await AddNoteAsync(admin, "system", title: "revoked",
            revoked: DateTime.UtcNow.AddMinutes(-5));
        var api = Build();
        var actor = admin["_id"].AsObjectId.ToString();

        var expiredItems = Items(await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(ActorId: actor, Expired: "true"), default));
        Assert.Contains(expiredItems, item => Id(item) == expired["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(expiredItems, item => Id(item) == live["_id"].AsObjectId.ToString());

        var revokeItems = Items(await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(ActorId: actor, Revoked: "true"), default));
        Assert.Contains(revokeItems, item => Id(item) == revoked["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(revokeItems, item => Id(item) == live["_id"].AsObjectId.ToString());

        var activeItems = Items(await api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(ActorId: actor, Revoked: "false"), default));
        Assert.Contains(activeItems, item => Id(item) == live["_id"].AsObjectId.ToString());
        Assert.DoesNotContain(activeItems, item => Id(item) == revoked["_id"].AsObjectId.ToString());

        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(From: "2020-01-01T00:00:00"), default))).Status);
    }

    [Fact]
    public async Task DeliveryStateSafetyCapReturns422()
    {
        var admin = await AddUserAsync(admin: true, "delivery-cap-admin");
        for (var index = 0; index < 3; index++)
        {
            var note = await AddNoteAsync(admin, "system", title: "cap-" + index);
            await AddDeliveryAsync(note, admin, "accepted");
        }
        var api = Build(deliveryStateLimit: 2);

        var error = await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(admin,
            new NotificationManagementQuery(DeliveryState: "accepted"), default));
        Assert.Equal(422, error.Status);
    }

    [Fact]
    public async Task ListProjectionNeverLeaksBodyNodesOrPayload()
    {
        var admin = await AddUserAsync(admin: true, "projection-admin");
        var note = await AddNoteAsync(admin, "system", title: "safe-title",
            body: "private-body", payloadHtml: "payload-html");
        var api = Build();

        var result = await api.Management.SentAsync(admin,
            new NotificationManagementQuery(Category: "system"), default);
        var items = Items(result);
        var item = Assert.Single(items, row => Id(row) == note["_id"].AsObjectId.ToString());
        Assert.True(item.ContainsKey("body"));
        Assert.Null(item["body"]);
        Assert.False(item.ContainsKey("nodes"));
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-body", json);
        Assert.DoesNotContain("payload-html", json);
    }

    [Fact]
    public async Task OrdinaryDetailIsOwnerScopedAndAdminDetailIsAudited()
    {
        var creator = await AddUserAsync(name: "detail-creator");
        var teamAdmin = await AddUserAsync(name: "detail-team-admin");
        var siteAdmin = await AddUserAsync(admin: true, "detail-site-admin");
        var team = await AddTeamAsync();
        await AddTeamMemberAsync(team, creator, "creator");
        await AddTeamMemberAsync(team, teamAdmin, "admin");
        await AddPolicyAsync(team, teamAdmin: true);
        var own = await AddNoteAsync(teamAdmin, "team", teamId: team, title: "admin-own",
            body: "own-body");
        var foreign = await AddNoteAsync(creator, "team", teamId: team, title: "creator-note",
            body: "foreign-body");
        var api = Build();

        var ownDetail = await api.Management.DetailAsync(teamAdmin,
            own["_id"].AsObjectId.ToString(), admin: false, default);
        Assert.Equal("admin-own", ownDetail["title"]);
        Assert.Equal("own-body", ownDetail["body"]);

        var error = await Assert.ThrowsAsync<ApiError>(() => api.Management.DetailAsync(teamAdmin,
            foreign["_id"].AsObjectId.ToString(), admin: false, default));
        Assert.Equal(403, error.Status);

        var adminDetail = await api.Management.DetailAsync(siteAdmin,
            foreign["_id"].AsObjectId.ToString(), admin: true, default);
        Assert.Equal("foreign-body", adminDetail["body"]);
        var audit = await Store.Collection("notification_audit").Find(
            Builders<BsonDocument>.Filter.Eq("action", "admin_read") &
            Builders<BsonDocument>.Filter.Eq("notification_id", foreign["_id"])).FirstOrDefaultAsync();
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task AdminListRequiresAdminAndWritesSearchAudit()
    {
        var normal = await AddUserAsync(name: "normal-admin-caller");
        var admin = await AddUserAsync(admin: true, "real-admin");
        var api = Build();

        var error = await Assert.ThrowsAsync<ApiError>(() => api.Management.AdminListAsync(normal,
            new NotificationManagementQuery(), default));
        Assert.Equal(403, error.Status);

        await api.Management.AdminListAsync(admin, new NotificationManagementQuery(), default);
        var audit = await Store.Collection("notification_audit").Find(
            Builders<BsonDocument>.Filter.Eq("actor_id", admin["_id"]) &
            Builders<BsonDocument>.Filter.Eq("action", "admin_search")).FirstOrDefaultAsync();
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task DisabledSyncReturnsEmptyResultWithoutPage()
    {
        var user = await AddUserAsync(name: "disabled-user");
        var api = Build(enabled: false);

        var result = await api.Management.SyncAsync(user,
            new NotificationSyncQuery(View: "inbox"), default);

        Assert.Equal(false, result["enabled"]);
        Assert.False(result.ContainsKey("page"));
        Assert.True(result.ContainsKey("revision"));
        var counts = Assert.IsType<Dictionary<string, object?>>(result["counts"]);
        Assert.Equal(0, Convert.ToInt32(counts["total"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SyncPermissionLossReturnsViewErrorWithoutPrivateBody()
    {
        var owner = await AddUserAsync(name: "sync-owner");
        var team = await AddTeamAsync();
        var project = await AddProjectAsync(owner, team);
        await AddProjectMemberAsync(project, owner, []);
        var note = await AddNoteAsync(owner, "project", projectId: project,
            title: "sync-private", body: "sync-private-body");
        var api = Build();

        await Store.Collection("project_member").DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("project", project) &
            Builders<BsonDocument>.Filter.Eq("user", owner["_id"]));
        var result = await api.Management.SyncAsync(owner, new NotificationSyncQuery(
            View: "scope_detail",
            Category: "project",
            ScopeId: project.ToString(),
            NotificationId: note["_id"].AsObjectId.ToString()), default);

        Assert.False(result.ContainsKey("notice"));
        var viewError = Assert.IsType<Dictionary<string, object?>>(result["view_error"]);
        Assert.Equal(403, viewError["status"]);
        Assert.DoesNotContain("sync-private-body", NotificationManagementService.CanonicalJson(result));
    }

    [Fact]
    public async Task SyncScopedViewReturnsScopePageAndStableRevision()
    {
        var owner = await AddUserAsync(name: "sync-scope-owner");
        var team = await AddTeamAsync();
        var project = await AddProjectAsync(owner, team);
        await AddProjectMemberAsync(project, owner, []);
        var note = await AddNoteAsync(owner, "project", projectId: project,
            title: "同步😀<b>猫</b>", body: "body-汉字");
        var api = Build();
        var query = new NotificationSyncQuery(
            View: "scope",
            Category: "project",
            ScopeId: project.ToString());

        var first = await api.Management.SyncAsync(owner, query, default);
        Assert.Equal(true, first["enabled"]);
        var capabilities = Assert.IsType<Dictionary<string, object?>>(first["capabilities"]);
        Assert.Equal(true, capabilities["can_send"]);
        Assert.True(first.ContainsKey("page"), NotificationManagementService.CanonicalJson(first));
        var page = Assert.IsType<Dictionary<string, object?>>(first["page"]);
        var items = Assert.IsType<List<Dictionary<string, object?>>>(page["items"]);
        Assert.Contains(items, item => Id(item) == note["_id"].AsObjectId.ToString());
        var revision = Assert.IsType<string>(first["revision"]);

        var unchanged = await api.Management.SyncAsync(owner,
            query with { Revision = revision }, default);
        Assert.Equal(2, unchanged.Count);
        Assert.Equal(true, unchanged["unchanged"]);
        Assert.Equal(revision, unchanged["revision"]);
    }

    [Fact]
    public void CanonicalJsonMatchesIndependentPythonVectors()
    {
        var first = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["note"] = null,
            ["title"] = "汉字😀 <b>猫</b>",
            ["counts"] = new Dictionary<string, object?>
            {
                ["total"] = 2,
                ["system"] = 1
            }
        };
        Assert.Equal(
            "{\"counts\": {\"system\": 1, \"total\": 2}, \"enabled\": true, \"note\": null, " +
            "\"title\": \"汉字😀 <b>猫</b>\"}",
            NotificationManagementService.CanonicalJson(first));
        Assert.Equal("c5c889bd7912ca52cdfb60fa86e38e4320856e634496bc980191fa82f7a827a7",
            NotificationManagementService.CanonicalRevision(first));

        var second = new Dictionary<string, object?>
        {
            ["a"] = new object?[]
            {
                true, false, null, "line\nbreak\t\"quote\"", "emoji😀汉字"
            },
            ["z"] = new Dictionary<string, object?>
            {
                ["html"] = "<script>&",
                ["nested"] = 0
            }
        };
        Assert.Equal(
            "{\"a\": [true, false, null, \"line\\nbreak\\t\\\"quote\\\"\", " +
            "\"emoji😀汉字\"], \"z\": {\"html\": \"<script>&\", \"nested\": 0}}",
            NotificationManagementService.CanonicalJson(second));
        Assert.Equal("9ba076ac582c921054e24a9f7a0ab09b542c35e097f25f46ca3c3e8923fb1baf",
            NotificationManagementService.CanonicalRevision(second));
    }

    [Fact]
    public void CanonicalJsonMatchesPythonFloatUnicodeAndControlVectors()
    {
        // Expected values were produced by CPython's json.dumps(sort_keys=True,
        // ensure_ascii=False); they are not round-tripped through this implementation.
        var floats = new Dictionary<string, object?>
        {
            ["a"] = 0.0,
            ["b"] = -0.0,
            ["c"] = 1e-05,
            ["d"] = 1e20
        };
        Assert.Equal("{\"a\": 0.0, \"b\": -0.0, \"c\": 1e-05, \"d\": 1e+20}",
            NotificationManagementService.CanonicalJson(floats));
        Assert.Equal("f7d24e8618173b43a11d33a3a8df510e03bd360f673a0970a978200c8efe655c",
            NotificationManagementService.CanonicalRevision(floats));

        var unicodeKeys = new Dictionary<string, object?>
        {
            ["a"] = "汉字",
            ["b"] = true,
            ["n"] = null,
            ["\uE000"] = "bmp",
            ["\U0001F600"] = "emoji"
        };
        Assert.Equal(
            "{\"a\": \"汉字\", \"b\": true, \"n\": null, \"\uE000\": \"bmp\", \"\U0001F600\": \"emoji\"}",
            NotificationManagementService.CanonicalJson(unicodeKeys));
        Assert.Equal("1a0369a6c30b068463edc296e586f3d6f8f28b759aea5988860cc66859ddfe66",
            NotificationManagementService.CanonicalRevision(unicodeKeys));

        var mixed = new Dictionary<string, object?>
        {
            ["a"] = new object?[]
            {
                true, false, null, "line\nbreak\t\"quote\"\u0001", "emoji😀汉字"
            },
            ["f"] = 1.2345678901234567e16,
            ["z"] = new Dictionary<string, object?>
            {
                ["html"] = "<script>&",
                ["nested"] = 1.5
            }
        };
        Assert.Equal(
            "{\"a\": [true, false, null, \"line\\nbreak\\t\\\"quote\\\"\\u0001\", \"emoji😀汉字\"], " +
            "\"f\": 1.2345678901234568e+16, \"z\": {\"html\": \"<script>&\", \"nested\": 1.5}}",
            NotificationManagementService.CanonicalJson(mixed));
        Assert.Equal("f294629e5b31b8e8cff1d86c476821e204584369295656578fffee6974691c96",
            NotificationManagementService.CanonicalRevision(mixed));

        var date = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            .AddTicks(1_230_000);
        Assert.Equal("\"2024-01-02T03:04:05.123000Z\"",
            NotificationManagementService.CanonicalJson(date));
        Assert.Equal("\"2024-01-02T03:04:05.123000Z\"",
            NotificationManagementService.CanonicalJson(new BsonDateTime(date)));
        var wholeSecond = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal("\"2024-01-02T03:04:05Z\"",
            NotificationManagementService.CanonicalJson(wholeSecond));
    }

    [Fact]
    public void CanonicalSupportsValueTypedDictionaries()
    {
        // Notification sync builds revision input from Dictionary<string, long>
        // counters, which only implement the non-generic IDictionary while their
        // enumerator still yields KeyValuePair<string, long>. Treating those
        // entries as DictionaryEntry used to throw InvalidCastException and turn
        // the whole /v1/me/notification-sync request into a 500.
        var counts = new Dictionary<string, long> { ["project"] = 12, ["system"] = 0 };
        var flags = new Dictionary<string, bool> { ["on"] = true, ["off"] = false };
        var nested = new Dictionary<string, object?>
        {
            ["counts"] = counts,
            ["flags"] = flags
        };

        Assert.Equal("{\"counts\": {\"project\": 12, \"system\": 0}, \"flags\": {\"off\": false, \"on\": true}}",
            NotificationManagementService.CanonicalJson(nested));
    }

    [Fact]
    public async Task HttpSyncDisabledAlwaysSetsNoStore()
    {
        var user = await AddUserAsync(name: "http-disabled-user");
        await using var app = await StartAppAsync(enabled: false, user);
        using var client = app.GetTestClient();
        Authorize(client, user);

        var response = await client.GetAsync("/v1/me/notification-sync?view=inbox");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("page", out _));
    }

    [Fact]
    public async Task HttpAdminListForbidsNormalUsersAndSentRequiresToken()
    {
        var normal = await AddUserAsync(name: "http-normal-user");
        await using var app = await StartAppAsync(enabled: true, normal);
        using var client = app.GetTestClient();

        var anonymous = await client.GetAsync("/v1/notifications/sent");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Authorize(client, normal);
        var forbidden = await client.GetAsync("/v1/admin/notifications");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var body = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
        Assert.Equal(NotificationErrors.ErrorCode, body.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task HttpSyncInvalidViewReturns400ThroughNotificationError()
    {
        var user = await AddUserAsync(name: "http-invalid-view");
        await using var app = await StartAppAsync(enabled: true, user);
        using var client = app.GetTestClient();
        Authorize(client, user);

        var response = await client.GetAsync("/v1/me/notification-sync?view=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NotificationError", body.RootElement.GetProperty("error").GetString());
    }

    private MongoStore Store => services.Store;

    private Api Build(bool enabled = true, int deliveryStateLimit = 20000)
    {
        var configuration = Configuration(enabled);
        var permissions = new IdentityPermissions(Store, TimeProvider.System);
        var content = new NotificationContent(Store, permissions, configuration);
        var read = new NotificationReadService(Store, permissions, content, configuration,
            TimeProvider.System);
        var publish = new NotificationPublishService(Store, content, read, configuration,
            new FakeMailTransport());
        var management = new NotificationManagementService(Store, read, publish,
            TimeProvider.System) { DeliveryStateLimit = deliveryStateLimit };
        return new Api(management);
    }

    private IConfiguration Configuration(bool enabled)
    {
        var values = services.Configuration.AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        values["Notifications:Enabled"] = enabled ? "true" : "false";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<WebApplication> StartAppAsync(bool enabled, BsonDocument user)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var configuration = Configuration(enabled);
        builder.Services.AddSingleton(Store);
        builder.Services.AddSingleton<IConfiguration>(configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IdentityPermissions>();
        builder.Services.AddSingleton<LegacyToken>();
        builder.Services.AddScoped<CurrentUser>();
        builder.Services.AddNotificationServices();
        builder.Services.AddScoped<NotificationPublishService>();
        builder.Services.AddSingleton<INotificationMailTransport, FakeMailTransport>();
        builder.Services.AddNotificationManagementServices();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (ApiError error)
            {
                context.Response.StatusCode = error.Status;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = error.Error,
                    code = error.Code,
                    message = error.ResponseMessage
                });
            }
        });
        NotificationManagementEndpoints.MapNotificationManagementEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private void Authorize(HttpClient client, BsonDocument user)
    {
        var token = new LegacyToken(Configuration(enabled: true), TimeProvider.System)
            .Issue(user["_id"].AsObjectId.ToString(), PasswordHash);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<BsonDocument> AddUserAsync(bool admin = false, string name = "user")
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["e"] = $"{name}-{id}@example.test",
            ["p"] = PasswordHash,
            ["admin"] = admin
        };
        await Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<ObjectId> AddTeamAsync(string name = "team")
    {
        var id = ObjectId.GenerateNewId();
        await Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = $"{name}-{id}"
        });
        return id;
    }

    private async Task AddTeamMemberAsync(ObjectId team, BsonDocument user, string baseTag,
        string status = "active")
    {
        await Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team,
            ["user"] = user["_id"],
            ["status"] = status,
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray()
        });
    }

    private async Task AddPolicyAsync(ObjectId team, bool teamAdmin)
    {
        await Store.Collection("notification_policy").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team_id"] = team,
            ["team_admin"] = teamAdmin,
            ["project_admin"] = false,
            ["version"] = 1
        });
    }

    private async Task<ObjectId> AddProjectAsync(BsonDocument owner, ObjectId team)
    {
        var id = ObjectId.GenerateNewId();
        await Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "project",
            ["t"] = team,
            ["ou"] = owner["_id"],
            ["st"] = 0,
            ["stv"] = 0
        });
        return id;
    }

    private async Task AddProjectMemberAsync(ObjectId project, BsonDocument user,
        IEnumerable<string> tags, string status = "active")
    {
        await Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project,
            ["user"] = user["_id"],
            ["status"] = status,
            ["tags"] = new BsonArray(tags.Select(tag => (BsonValue)tag))
        });
    }

    private async Task<BsonDocument> AddNoteAsync(BsonDocument actor, string category,
        ObjectId? teamId = null, ObjectId? projectId = null,
        string eventType = "announcement", string title = "notification",
        string body = "body", string searchText = "notification body",
        DateTime? expires = null, DateTime? revoked = null,
        string verdict = "published", string payloadHtml = "<b>payload</b>")
    {
        _ = verdict;
        var now = DateTime.UtcNow;
        var note = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["key"] = Guid.NewGuid().ToString("N"),
            ["category"] = category,
            ["source"] = "core",
            ["event_type"] = eventType,
            ["actor_id"] = actor["_id"],
            ["team_id"] = teamId.HasValue ? teamId.Value : BsonNull.Value,
            ["project_id"] = projectId.HasValue ? projectId.Value : BsonNull.Value,
            ["title"] = title,
            ["body"] = body,
            ["nodes"] = new BsonArray
            {
                new BsonDocument { ["type"] = "text", ["text"] = body }
            },
            ["search_text"] = NotificationContent.Normalize(searchText),
            ["search_tokens"] = new BsonArray(
                NotificationContent.SearchTokens(searchText).Select(token => (BsonValue)token)),
            ["audience"] = new BsonDocument { ["mode"] = "all" },
            ["requested_email"] = false,
            ["payload"] = new BsonDocument
            {
                ["html"] = payloadHtml,
                ["text"] = body
            },
            ["state"] = "published",
            ["created_at"] = now,
            ["publish_at"] = now,
            ["expires_at"] = expires.HasValue ? expires.Value : BsonNull.Value,
            ["revoked_at"] = revoked.HasValue ? revoked.Value : BsonNull.Value,
            ["revoke_reason"] = "",
            ["version"] = 1,
            ["recipient_count"] = 0,
            ["error"] = ""
        };
        await Store.Collection("notification").InsertOneAsync(note);
        return note;
    }

    private async Task AddDeliveryAsync(BsonDocument note, BsonDocument user, string state)
    {
        await Store.Collection("notification_delivery").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["notification_id"] = note["_id"],
            ["user_id"] = user["_id"],
            ["state"] = state,
            ["created_at"] = DateTime.UtcNow
        });
    }

    private static List<Dictionary<string, object?>> Items(
        Dictionary<string, object?> result) =>
        Assert.IsType<List<Dictionary<string, object?>>>(result["items"]);

    private static string Id(Dictionary<string, object?> item) =>
        Assert.IsType<string>(item["id"]);

    private sealed record Api(NotificationManagementService Management);

    public sealed class FakeMailTransport : INotificationMailTransport
    {
        public Task<NotificationMailResult> SendAsync(NotificationMailMessage message,
            CancellationToken ct) => Task.FromResult(new NotificationMailResult("skipped", "test"));
    }
}
