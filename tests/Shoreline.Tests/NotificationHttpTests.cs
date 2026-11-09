using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// N1 HTTP wiring checks against the shared Program.cs registration.
[Collection("local-services")]
public sealed class NotificationHttpTests
{
    private readonly LocalServices services;

    public NotificationHttpTests(LocalServices services) => this.services = services;

    [Fact]
    public async Task MissingTokenAndDisabledSurfaceMatchLegacy()
    {
        using var anonymous = services.App.CreateClient();
        var missing = await anonymous.GetAsync("/v1/me/notifications");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NeedTokenError", missingBody.GetProperty("error").GetString());
        Assert.Equal(1003, missingBody.GetProperty("code").GetInt32());

        var user = await InsertUserAsync();
        using var client = Authenticated(services.App, user);
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/me/notification-capabilities");
        Assert.False(capabilities.GetProperty("enabled").GetBoolean());
        Assert.False(capabilities.GetProperty("can_send").GetBoolean());
        Assert.False(capabilities.GetProperty("can_view_sent").GetBoolean());

        var counts = await client.GetFromJsonAsync<JsonElement>("/v1/me/notifications/unread-counts");
        Assert.Equal(0, counts.GetProperty("total").GetInt32());
        Assert.Equal(0, counts.GetProperty("system").GetInt32());
        Assert.Equal(0, counts.GetProperty("team").GetInt32());

        var inbox = await client.GetAsync("/v1/me/notifications");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, inbox.StatusCode);
        var inboxBody = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NotificationError", inboxBody.GetProperty("error").GetString());
        Assert.Equal(4600, inboxBody.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task EnabledInboxReadReceiptAndMembershipRevocation()
    {
        using var app = EnabledApp();
        using var client = app.CreateClient();
        var user = await InsertUserAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));

        var team = ObjectId.GenerateNewId();
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = team,
            ["n"] = "http-team"
        });
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = team,
            ["user"] = user["_id"],
            ["base_tag"] = "member",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["status"] = "active"
        });
        var note = await InsertNoteAsync("team", team);
        await InsertReceiptAsync(note, user);

        var inbox = await client.GetFromJsonAsync<JsonElement>("/v1/me/notifications");
        Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.False(inbox.GetProperty("items")[0].GetProperty("read").GetBoolean());

        var notificationId = note.ToString();
        var read = await client.PatchAsJsonAsync($"/v1/me/notifications/{notificationId}",
            new { read = true });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/v1/me/notifications/{notificationId}");
        Assert.True(detail.GetProperty("read").GetBoolean());

        await services.Store.Collection("team_member").UpdateOneAsync(
            new BsonDocument { ["team"] = team, ["user"] = user["_id"] },
            Builders<BsonDocument>.Update.Set("status", "removed"));
        var hidden = await client.GetAsync($"/v1/me/notifications/{notificationId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var hiddenBody = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(4600, hiddenBody.GetProperty("code").GetInt32());

        var after = await client.GetFromJsonAsync<JsonElement>("/v1/me/notifications");
        Assert.Empty(after.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task CardsHideRevokedProjectAndDoNotLeakNameOrId()
    {
        using var app = EnabledApp();
        using var client = app.CreateClient();
        var user = await InsertUserAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));

        var team = ObjectId.GenerateNewId();
        var project = ObjectId.GenerateNewId();
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = team,
            ["n"] = "cards-team"
        });
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = project,
            ["n"] = "visible-card-name",
            ["ns"] = "visible-card-name",
            ["i"] = "",
            ["t"] = team,
            ["ps"] = new BsonArray(),
            ["st"] = 0,
            ["ou"] = ObjectId.Empty
        });
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project,
            ["user"] = user["_id"],
            ["tags"] = new BsonArray(),
            ["status"] = "active"
        });

        var cards = await client.GetFromJsonAsync<JsonElement>("/v1/notification-project-cards?q=visible-card-name");
        Assert.Single(cards.GetProperty("items").EnumerateArray());
        Assert.Equal(project.ToString(), cards.GetProperty("items")[0].GetProperty("id").GetString());

        await services.Store.Collection("project_member").UpdateOneAsync(
            new BsonDocument { ["project"] = project, ["user"] = user["_id"] },
            Builders<BsonDocument>.Update.Set("status", "removed"));

        var hidden = await client.GetFromJsonAsync<JsonElement>("/v1/notification-project-cards?q=visible-card-name");
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());

        var resolved = await client.PostAsJsonAsync("/v1/notification-project-cards/resolve",
            new { project_ids = new[] { project.ToString() } });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var body = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unavailable", body.GetProperty("nodes")[0].GetProperty("type").GetString());
        Assert.False(body.GetProperty("nodes")[0].TryGetProperty("project_id", out _));
        Assert.False(body.GetProperty("nodes")[0].TryGetProperty("name", out _));
        Assert.DoesNotContain("visible-card-name", body.GetRawText());
        Assert.DoesNotContain(project.ToString(), body.GetRawText());
    }

    private WebApplicationFactory<Program> EnabledApp() =>
        services.App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Enabled"] = "true",
                ["Notifications:SiteOrigin"] = "https://example.test"
            })));

    private static HttpClient Authenticated(WebApplicationFactory<Program> app, BsonDocument user)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));
        return client;
    }

    private async Task<BsonDocument> InsertUserAsync()
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "http-user-" + id,
            ["e"] = id + "@example.test",
            ["admin"] = false,
            ["b"] = false,
            ["p"] = "http-password-hash"
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<ObjectId> InsertNoteAsync(string category, ObjectId team)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("notification").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["key"] = "http-note-" + id,
            ["category"] = category,
            ["source"] = "core",
            ["event_type"] = "announcement",
            ["actor_id"] = ObjectId.GenerateNewId(),
            ["team_id"] = team,
            ["title"] = "HTTP note title",
            ["body"] = "HTTP note body",
            ["nodes"] = new BsonArray
            {
                new BsonDocument { ["type"] = "text", ["text"] = "HTTP note body" }
            },
            ["search_text"] = "http note body",
            ["search_tokens"] = new BsonArray { "ht", "tp", "no", "ot", "te", "bo", "od" },
            ["audience"] = new BsonDocument
            {
                ["base_roles"] = new BsonArray { "member" },
                ["tags"] = new BsonArray()
            },
            ["state"] = "published",
            ["created_at"] = new BsonDateTime(DateTime.UtcNow),
            ["publish_at"] = new BsonDateTime(DateTime.UtcNow),
            ["version"] = 1,
            ["recipient_count"] = 1
        });
        return id;
    }

    private async Task InsertReceiptAsync(ObjectId note, BsonDocument user) =>
        await services.Store.Collection("notification_receipt").InsertOneAsync(new BsonDocument
        {
            ["notification_id"] = note,
            ["user_id"] = user["_id"],
            ["created_at"] = new BsonDateTime(DateTime.UtcNow),
            ["read_at"] = BsonNull.Value,
            ["archived"] = false
        });
}
