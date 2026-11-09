using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

// These are real WebApplicationFactory<Program> facts. The shared Program is
// intentionally not modified for N3; validation runs in an isolated snapshot
// whose private Program copy registers the N3 services and maps these routes.
[Collection("local-services")]
public sealed class NotificationManagementHttpTests(LocalServices services)
{
    [Fact]
    public async Task MissingTokenOnSyncIsUnauthorizedAndNoStore()
    {
        using var app = App();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/v1/me/notification-sync?view=inbox");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NeedTokenError", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(1003, body.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task AdminDetailWritesReadAuditAndListNeverLeaksPrivateBody()
    {
        using var app = App();
        var admin = await AddUserAsync(admin: true, name: "n3-http-admin");
        var note = await AddNoteAsync(admin, "private-body-n3", "system");
        using var client = Authenticated(app, admin);

        var detail = await client.GetAsync($"/v1/admin/notifications/{note["_id"]}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using (var body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            Assert.Equal(note["_id"].AsObjectId.ToString(),
                body.RootElement.GetProperty("id").GetString());
            Assert.Equal("private-body-n3", body.RootElement.GetProperty("body").GetString());
        }

        var list = await client.GetAsync("/v1/admin/notifications?category=system");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listText = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-body-n3", listText);
        using (var body = JsonDocument.Parse(listText))
        {
            var items = body.RootElement.GetProperty("items");
            var matching = items.EnumerateArray()
                .Where(value => value.GetProperty("id").GetString()
                    == note["_id"].AsObjectId.ToString())
                .ToArray();
            var item = Assert.Single(matching);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("body").ValueKind);
            Assert.False(item.TryGetProperty("nodes", out _));
        }

        var audits = services.Store.Collection("notification_audit");
        var readAudit = await audits.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("actor_id", admin["_id"])
            & Builders<BsonDocument>.Filter.Eq("action", "admin_read")
            & Builders<BsonDocument>.Filter.Eq("notification_id", note["_id"]));
        var searchAudit = await audits.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("actor_id", admin["_id"])
            & Builders<BsonDocument>.Filter.Eq("action", "admin_search"));
        Assert.Equal(1, readAudit);
        Assert.True(searchAudit >= 1);
    }

    [Fact]
    public async Task ScopeSyncForwardsCategoryAndScopeAndReturnsStableRevision()
    {
        using var app = App();
        var owner = await AddUserAsync(name: "n3-http-scope-owner");
        var team = await AddTeamAsync();
        await AddTeamMemberAsync(team, owner, "creator");
        var note = await AddNoteAsync(owner, "scope-body-n3", "team", team: team);
        using var client = Authenticated(app, owner);

        var path = "/v1/me/notification-sync?view=scope&category=team&scope_id="
            + team["_id"].AsObjectId;
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Contains(root.GetProperty("page").GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == note["_id"].AsObjectId.ToString());
        var revision = root.GetProperty("revision").GetString();
        Assert.False(string.IsNullOrWhiteSpace(revision));

        var unchanged = await client.GetAsync($"{path}&revision={revision}");
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.True(unchanged.Headers.CacheControl?.NoStore);
        using var unchangedBody = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
        Assert.True(unchangedBody.RootElement.GetProperty("unchanged").GetBoolean());
        Assert.Equal(revision, unchangedBody.RootElement.GetProperty("revision").GetString());
        Assert.Equal(2, unchangedBody.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task SyncErrorsAndDisabledViewAlwaysSetNoStore()
    {
        using var app = App();
        var user = await AddUserAsync(name: "n3-http-sync-errors");
        using var client = Authenticated(app, user);

        var invalid = await client.GetAsync("/v1/me/notification-sync?view=not-a-view");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True(invalid.Headers.CacheControl?.NoStore);
        using (var body = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
        {
            Assert.Equal("NotificationError", body.RootElement.GetProperty("error").GetString());
            Assert.Equal(4600, body.RootElement.GetProperty("code").GetInt32());
        }

        using var disabled = App(enabled: false);
        using var disabledClient = Authenticated(disabled, user);
        var response = await disabledClient.GetAsync("/v1/me/notification-sync?view=scope");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var disabledBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(disabledBody.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(disabledBody.RootElement.TryGetProperty("page", out _));
    }

    private WebApplicationFactory<Program> App(bool enabled = true) =>
        services.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Notifications:Enabled"] = enabled ? "true" : "false",
                    ["Notifications:SiteOrigin"] = "https://example.test",
                    ["Notifications:WorkerEnabled"] = "false",
                    ["Notifications:MaxAudience"] = "20000"
                })));

    private static HttpClient Authenticated(WebApplicationFactory<Program> app, BsonDocument user)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>()
                .Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));
        return client;
    }

    private async Task<BsonDocument> AddUserAsync(bool admin = false, string? name = null)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name ?? "n3-http-" + id,
            ["e"] = name ?? id.ToString(),
            ["admin"] = admin,
            ["b"] = false,
            ["p"] = "seed-password-hash"
        };
        user["e"] = user.Text("e") + "-" + id + "@example.test";
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> AddTeamAsync()
    {
        var id = ObjectId.GenerateNewId();
        var team = new BsonDocument { ["_id"] = id, ["n"] = "n3-team-" + id };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task AddTeamMemberAsync(BsonDocument team, BsonDocument user, string baseTag)
    {
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
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

    private async Task<BsonDocument> AddNoteAsync(
        BsonDocument actor, string body, string category, BsonDocument? team = null)
    {
        var now = DateTime.UtcNow;
        var note = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["key"] = "n3-http-" + ObjectId.GenerateNewId(),
            ["fingerprint"] = "n3-http-" + ObjectId.GenerateNewId(),
            ["category"] = category,
            ["source"] = "core",
            ["event_type"] = "announcement",
            ["actor_id"] = actor["_id"],
            ["team_id"] = team?["_id"] ?? BsonNull.Value,
            ["project_id"] = BsonNull.Value,
            ["title"] = "HTTP notice",
            ["body"] = body,
            ["nodes"] = new BsonArray { new BsonDocument { ["type"] = "text", ["text"] = body } },
            ["search_text"] = body,
            ["search_tokens"] = new BsonArray(),
            ["audience"] = new BsonDocument("mode", "all"),
            ["requested_email"] = false,
            ["payload"] = new BsonDocument(),
            ["state"] = "published",
            ["created_at"] = now,
            ["publish_at"] = now,
            ["expires_at"] = BsonNull.Value,
            ["revoked_at"] = BsonNull.Value,
            ["revoked_by"] = BsonNull.Value,
            ["revoke_reason"] = "",
            ["moderation_key"] = "",
            ["moderation_action"] = "",
            ["moderation_audited"] = false,
            ["publish_audited"] = true,
            ["version"] = 1,
            ["cursor"] = "",
            ["next_chunk"] = 0,
            ["recipient_count"] = 0,
            ["error"] = "",
            ["lease_token"] = "",
            ["lease_until"] = DateTime.MinValue
        };
        await services.Store.Collection("notification").InsertOneAsync(note);
        return note;
    }
}
