using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// J1 snapshot-only HTTP checks. Shared Program wiring is owned by main.
[Collection("local-services")]
public sealed class TeamInsightHttpTests
{
    private readonly LocalServices services;

    public TeamInsightHttpTests(LocalServices services) => this.services = services;

    [Fact]
    public async Task MissingTokenAndMemberWithoutInsightAreDenied()
    {
        using var anonymous = services.App.CreateClient();
        var missing = await anonymous.GetAsync($"/v1/teams/{ObjectId.GenerateNewId()}/insight/users");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NeedTokenError", missingBody.GetProperty("error").GetString());
        Assert.Equal(1003, missingBody.GetProperty("code").GetInt32());

        var team = await InsertTeamAsync();
        var member = await InsertUserAsync("Member");
        await InsertMemberAsync(team, member, "member");
        using var client = Authenticated(services.App, member);
        var denied = await client.GetAsync($"/v1/teams/{team["_id"]}/insight/users");
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        var deniedBody = JsonDocument.Parse(await denied.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NoPermissionError", deniedBody.GetProperty("error").GetString());
        Assert.Equal(103, deniedBody.GetProperty("code").GetInt32());
        Assert.Equal("You do not have permission to view the statistics of this team project.", deniedBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UserListSearchesAliasesAndReportsTotalWithPaginationHeaders()
    {
        var team = await InsertTeamAsync();
        var actor = await InsertUserAsync("Analyst");
        await InsertMemberAsync(team, actor, "member", ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var luna = await InsertUserAsync("Straße");
        await InsertMemberAsync(team, luna, "member", aliases: ["Luna"]);

        using var client = Authenticated(services.App, actor);
        var response = await client.GetAsync($"/v1/teams/{team["_id"]}/insight/users?word=LUNA&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("X-Pagination-Count").Single());
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(luna["_id"].ToString(), rows[0].GetProperty("user").GetProperty("id").GetString());

        var page = await client.GetAsync($"/v1/teams/{team["_id"]}/insight/users?page=1&limit=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("2", page.Headers.GetValues("X-Pagination-Count").Single());
        Assert.Equal(1, JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ProjectUsersReturnMembersAndRejectForeignTeam()
    {
        var team = await InsertTeamAsync();
        var actor = await InsertUserAsync("Analyst");
        await InsertMemberAsync(team, actor, "member", ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var owner = await InsertUserAsync("Owner");
        var regular = await InsertUserAsync("Regular");
        var set = await InsertSetAsync(team);
        var project = await InsertProjectAsync(team, set, "Project");
        await InsertProjectMemberAsync(project, owner, ["creator"]);
        await InsertProjectMemberAsync(project, regular, ["translator"]);

        using var client = Authenticated(services.App, actor);
        var response = await client.GetAsync($"/v1/teams/{team["_id"]}/insight/projects/{project["_id"]}/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("X-Pagination-Count").Single());
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, rows.GetArrayLength());

        var other = await InsertTeamAsync();
        var foreign = await InsertProjectAsync(other, set, "Foreign");
        var missing = await client.GetAsync($"/v1/teams/{team["_id"]}/insight/projects/{foreign["_id"]}/users");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var error = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ProjectNotExistError", error.GetProperty("error").GetString());
        Assert.Equal(4001, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ProjectsExposeOutputsOnlyForAutoBecomeProjectAdmin()
    {
        var team = await InsertTeamAsync();
        var admin = await InsertUserAsync("Team Admin");
        await InsertMemberAsync(team, admin, "admin");
        await PolicyAsync(team, "admin", "team:ACCESS", "team:INSIGHT", "team:AUTO_BECOME_PROJECT_ADMIN");
        var analyst = await InsertUserAsync("Analyst");
        await InsertMemberAsync(team, analyst, "member", ["analyst"]);
        await PolicyAsync(team, "analyst", "team:ACCESS", "team:INSIGHT");
        var set = await InsertSetAsync(team);
        var project = await InsertProjectAsync(team, set, "Project");

        using var adminClient = Authenticated(services.App, admin);
        var adminResponse = await adminClient.GetAsync($"/v1/teams/{team["_id"]}/insight/projects");
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        var adminRows = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, adminRows.GetArrayLength());
        Assert.True(adminRows[0].TryGetProperty("outputs", out var outputs));
        Assert.Equal(0, outputs.GetArrayLength());

        using var analystClient = Authenticated(services.App, analyst);
        var analystResponse = await analystClient.GetAsync($"/v1/teams/{team["_id"]}/insight/projects");
        Assert.Equal(HttpStatusCode.OK, analystResponse.StatusCode);
        var analystRows = JsonDocument.Parse(await analystResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, analystRows.GetArrayLength());
        Assert.False(analystRows[0].TryGetProperty("outputs", out _));
    }

    private static HttpClient Authenticated(WebApplicationFactory<Program> app, BsonDocument user)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));
        return client;
    }

    private async Task<BsonDocument> InsertUserAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name + "-" + id,
            ["e"] = id + "@example.test",
            ["p"] = "http-password-hash",
            ["admin"] = false,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> InsertTeamAsync()
    {
        var team = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "http-team-" + ObjectId.GenerateNewId(),
            ["i"] = "",
            ["a"] = ""
        };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task InsertMemberAsync(BsonDocument team, BsonDocument user, string baseTag,
        string[]? tags = null, string[]? aliases = null)
    {
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray((tags ?? []).Select(value => (BsonValue)value)),
            ["aliases"] = new BsonArray((aliases ?? []).Select(value => (BsonValue)value)),
            ["asrch"] = new BsonArray((aliases ?? []).Select(value => (BsonValue)SearchText.Normalize(value))),
            ["status"] = "active",
            ["version"] = 0
        });
    }

    private async Task PolicyAsync(BsonDocument team, string tag, params string[] permissions)
    {
        var definition = new BsonDocument
        {
            ["name"] = tag,
            ["code"] = tag,
            ["permissions"] = new BsonArray(permissions.Select(value => (BsonValue)value))
        };
        await services.Store.Collection("identity_tag_policy").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("team", team["_id"]),
            Builders<BsonDocument>.Update
                .Set("team_tags." + tag, definition)
                .SetOnInsert("team", team["_id"]),
            new UpdateOptions { IsUpsert = true });
    }

    private async Task<BsonDocument> InsertSetAsync(BsonDocument team)
    {
        var set = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "http-set-" + ObjectId.GenerateNewId(),
            ["i"] = "",
            ["t"] = team["_id"],
            ["d"] = true,
            ["ct"] = DateTime.UtcNow,
            ["et"] = DateTime.UtcNow
        };
        await services.Store.Collection("project_set").InsertOneAsync(set);
        return set;
    }

    private async Task<BsonDocument> InsertProjectAsync(BsonDocument team, BsonDocument set, string name)
    {
        var project = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name + "-" + ObjectId.GenerateNewId(),
            ["ns"] = SearchText.Normalize(name),
            ["i"] = "",
            ["t"] = team["_id"],
            ["ps"] = set["_id"],
            ["st"] = 0,
            ["ou"] = BsonNull.Value,
            ["m_c"] = DateTime.UtcNow,
            ["m_e"] = DateTime.UtcNow
        };
        await services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task InsertProjectMemberAsync(BsonDocument project, BsonDocument user, string[] tags)
    {
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = user["_id"],
            ["external_id"] = BsonNull.Value,
            ["display_name"] = BsonNull.Value,
            ["tags"] = new BsonArray(tags.Select(value => (BsonValue)value)),
            ["status"] = "active",
            ["version"] = 0,
            ["create_time"] = DateTime.UtcNow,
            ["edit_time"] = DateTime.UtcNow
        });
    }
}
