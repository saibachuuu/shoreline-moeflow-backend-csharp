using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// HTTP coverage for the presence slice. The shared Program.cs now calls
// AddPresenceServices, initializes PresenceService in a startup scope, and maps
// MapPresence; integrated build/test revalidation remains pending other modules.
[Collection("local-services")]
public sealed class PresenceHttpTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task MissingTokenReturnsLegacyCode1003()
    {
        var project = await ProjectAsync(await TeamAsync());
        using var response = await services.Http.PostAsync(
            $"/v1/projects/{Id(project)}/presence/heartbeat",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        await ErrorAsync(response, 1003);
    }

    [Fact]
    public async Task MissingProjectAndTeamUseTheirOriginalErrorCodes()
    {
        var user = await UserAsync();
        using var client = Client(user);
        var missingProject = ObjectId.GenerateNewId();
        await ErrorAsync(await client.PostAsJsonAsync(
            $"/v1/projects/{missingProject}/presence/heartbeat", new { action = "working" }), 4001);
        await ErrorAsync(await client.GetAsync($"/v1/projects/{missingProject}/presence"), 4001);

        var missingTeam = ObjectId.GenerateNewId();
        await ErrorAsync(await client.GetAsync(
            $"/v1/teams/{missingTeam}/projects/active-presence"), 3001);
    }

    [Fact]
    public async Task MalformedListAndWrongActionTypeFallBackToWorking()
    {
        var project = await ProjectAsync(await TeamAsync());
        var user = await UserAsync();
        using var client = Client(user);
        var url = $"/v1/projects/{Id(project)}/presence/heartbeat";

        using (var malformed = await client.PostAsync(url,
                   new StringContent("{", Encoding.UTF8, "application/json")))
            Assert.Equal("working", await SingleActionAsync(malformed));

        using (var list = await client.PostAsync(url,
                   new StringContent("[{\"action\":\"ignored\"}]", Encoding.UTF8, "application/json")))
            Assert.Equal("working", await SingleActionAsync(list));

        using (var wrongType = await client.PostAsJsonAsync(url, new { action = 123 }))
            Assert.Equal("working", await SingleActionAsync(wrongType));
    }

    [Fact]
    public async Task UnicodeActionIsTruncatedTo64CodePoints()
    {
        var project = await ProjectAsync(await TeamAsync());
        var user = await UserAsync();
        using var client = Client(user);
        var url = $"/v1/projects/{Id(project)}/presence/heartbeat";
        var emoji = string.Concat(Enumerable.Repeat("😀", 70));

        using var response = await client.PostAsJsonAsync(url, new { action = emoji });
        var action = await SingleActionAsync(response);
        Assert.Equal(64, action.EnumerateRunes().Count());
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 64)), action);
    }

    [Fact]
    public async Task LeaveGetAndTeamAggregateFollowPythonShape()
    {
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync();
        using var client = Client(user);
        var projectUrl = $"/v1/projects/{Id(project)}/presence";

        await BodyAsync(await client.PostAsJsonAsync(projectUrl + "/heartbeat",
            new { action = "translating" }));

        var projectBody = await BodyAsync(await client.GetAsync(projectUrl));
        Assert.Equal(Id(project), projectBody.GetProperty("project_id").GetString());
        var active = projectBody.GetProperty("active_users").EnumerateArray().Single();
        Assert.Equal(Id(user), active.GetProperty("id").GetString());
        Assert.Equal("translating", active.GetProperty("action").GetString());
        Assert.Equal(new[] { "action", "avatar", "id", "last_heartbeat", "name" },
            active.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        var teamBody = await BodyAsync(await client.GetAsync(
            $"/v1/teams/{Id(team)}/projects/active-presence"));
        var group = teamBody.GetProperty("active_projects").GetProperty(Id(project));
        Assert.Equal(Id(project), group.GetProperty("project_id").GetString());
        Assert.Equal(1, group.GetProperty("user_count").GetInt32());
        Assert.Single(group.GetProperty("users").EnumerateArray());

        var leave = await BodyAsync(await client.PostAsJsonAsync(projectUrl + "/leave", new { }));
        Assert.Equal("ok", leave.GetProperty("message").GetString());
        Assert.Empty((await BodyAsync(await client.GetAsync(projectUrl)))
            .GetProperty("active_users").EnumerateArray());

        var emptyTeamBody = await BodyAsync(await client.GetAsync(
            $"/v1/teams/{Id(team)}/projects/active-presence"));
        Assert.Empty(emptyTeamBody.GetProperty("active_projects").EnumerateObject());

        var emptyProject = await ProjectAsync(team);
        Assert.Empty((await BodyAsync(await client.GetAsync(
                $"/v1/projects/{Id(emptyProject)}/presence")))
            .GetProperty("active_users").EnumerateArray());
    }

    [Fact]
    public async Task ValidTokenWithoutMembershipStillHeartbeatsUnderLegacyBehavior()
    {
        var team = await TeamAsync();
        var project = await ProjectAsync(team);
        var user = await UserAsync();

        // No team_member or project_member rows are created. Python's presence
        // module had no membership/ACCESS guard, so this succeeds. This is a
        // characterization of legacy behavior, not an authorization guarantee.
        Assert.Equal(0, await services.Store.Collection("team_member")
            .CountDocumentsAsync(new BsonDocument("team", team["_id"])));
        Assert.Equal(0, await services.Store.Collection("project_member")
            .CountDocumentsAsync(new BsonDocument("project", project["_id"])));

        using var client = Client(user);
        var response = await client.PostAsJsonAsync(
            $"/v1/projects/{Id(project)}/presence/heartbeat", new { action = "working" });
        var body = await BodyAsync(response);
        var active = body.GetProperty("active_users").EnumerateArray().Single();
        Assert.Equal(Id(user), active.GetProperty("id").GetString());
    }

    private static string Id(BsonDocument document) => document["_id"].AsObjectId.ToString();

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>()
                .Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private async Task<BsonDocument> TeamAsync()
    {
        var id = ObjectId.GenerateNewId();
        var team = new BsonDocument { ["_id"] = id, ["n"] = "team-" + id };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task<BsonDocument> ProjectAsync(BsonDocument team)
    {
        var id = ObjectId.GenerateNewId();
        var project = new BsonDocument
        {
            ["_id"] = id,
            ["t"] = team["_id"],
            ["n"] = "project-" + id
        };
        await services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task<BsonDocument> UserAsync()
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = name,
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["e"] = name + "@example.test",
            ["p"] = Password,
            ["admin"] = false,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> SingleActionAsync(HttpResponseMessage response)
    {
        var body = await BodyAsync(response);
        var active = body.GetProperty("active_users").EnumerateArray().Single();
        return Assert.IsType<string>(active.GetProperty("action").GetString());
    }

    private static async Task ErrorAsync(HttpResponseMessage response, int code)
    {
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetInt32());
    }
}
