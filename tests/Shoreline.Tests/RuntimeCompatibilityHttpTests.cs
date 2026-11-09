using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// HTTP-level regressions for the legacy compatibility fixes found by the
// Python runtime differential. These tests intentionally use the committed
// runtime fixture as their seed so they exercise the same document shapes as
// the reference runtime, but they are not a second differential report.
public sealed class RuntimeCompatibilityHttpTests
{
    private static async Task<Harness> CreateHarnessAsync(bool openTeamApplications = false,
        bool createProject = false, Dictionary<string, string?>? configuration = null)
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync(openTeamApplications, createProject, configuration);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task ApplicationPluralRoutesAcceptTeamsAndProjects()
    {
        await using var harness = await CreateHarnessAsync(openTeamApplications: true, createProject: true);
        var target = await harness.CreateExternalUserAsync();
        var applicantToken = await harness.TokenForAsync(target);

        using var teamGet = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams/{harness.TeamId}/applications", harness.Token);
        Assert.Equal(HttpStatusCode.OK, teamGet.StatusCode);
        using var teamGetJson = JsonDocument.Parse(await teamGet.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, teamGetJson.RootElement.ValueKind);

        using var teamPost = await harness.SendJsonAsync(HttpMethod.Post,
            $"/v1/teams/{harness.TeamId}/applications", applicantToken,
            $$"""{"message":"Please add me to the team"}""");
        Assert.Equal(HttpStatusCode.OK, teamPost.StatusCode);
        using var teamPostJson = JsonDocument.Parse(await teamPost.Content.ReadAsStringAsync());
        Assert.Equal("申请成功，请等待管理员审核",
            teamPostJson.RootElement.GetProperty("message").GetString());

        using var projectGet = await harness.SendAsync(HttpMethod.Get,
            $"/v1/projects/{harness.ProjectId}/applications", harness.Token);
        Assert.Equal(HttpStatusCode.OK, projectGet.StatusCode);
        using var projectGetJson = JsonDocument.Parse(await projectGet.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, projectGetJson.RootElement.ValueKind);

        using var projectPost = await harness.SendJsonAsync(HttpMethod.Post,
            $"/v1/projects/{harness.ProjectId}/applications", applicantToken,
            $$"""{"message":"Please add me to the project"}""");
        Assert.Equal(HttpStatusCode.OK, projectPost.StatusCode);
        using var projectPostJson = JsonDocument.Parse(await projectPost.Content.ReadAsStringAsync());
        Assert.Equal("申请成功，请等待管理员审核",
            projectPostJson.RootElement.GetProperty("message").GetString());

        using var teamInfo = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams/{harness.TeamId}/public-info", harness.Token);
        Assert.Equal(HttpStatusCode.OK, teamInfo.StatusCode);
        using var projectInfo = await harness.SendAsync(HttpMethod.Get,
            $"/v1/projects/{harness.ProjectId}/public-info", harness.Token);
        Assert.Equal(HttpStatusCode.OK, projectInfo.StatusCode);

        // The external user must not have been added by merely applying.
        var memberCount = await harness.Services.Store.Collection("team_member")
            .CountDocumentsAsync(new BsonDocument { ["team"] = harness.TeamId, ["user"] = target });
        Assert.Equal(0L, memberCount);
    }

    [Fact]
    public async Task InvitationPluralRoutesAcceptTeamsAndProjects()
    {
        await using var harness = await CreateHarnessAsync(createProject: true);
        var target = await harness.CreateExternalUserAsync();
        var teamRole = await harness.RoleIdAsync("team_role", "member");
        var projectRole = await harness.RoleIdAsync("project_role", "translator");

        using var teamGet = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams/{harness.TeamId}/invitations", harness.Token);
        Assert.Equal(HttpStatusCode.OK, teamGet.StatusCode);
        using var teamGetJson = JsonDocument.Parse(await teamGet.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, teamGetJson.RootElement.ValueKind);

        using var teamPost = await harness.SendJsonAsync(HttpMethod.Post,
            $"/v1/teams/{harness.TeamId}/invitations", harness.Token,
            $$"""{"user_id":"{{target}}","message":"Join the team","role_id":"{{teamRole}}"}""");
        Assert.Equal(HttpStatusCode.OK, teamPost.StatusCode);
        using var teamPostJson = JsonDocument.Parse(await teamPost.Content.ReadAsStringAsync());
        Assert.True(teamPostJson.RootElement.TryGetProperty("invitation", out _));

        using var projectGet = await harness.SendAsync(HttpMethod.Get,
            $"/v1/projects/{harness.ProjectId}/invitations", harness.Token);
        Assert.Equal(HttpStatusCode.OK, projectGet.StatusCode);
        using var projectGetJson = JsonDocument.Parse(await projectGet.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, projectGetJson.RootElement.ValueKind);

        using var projectPost = await harness.SendJsonAsync(HttpMethod.Post,
            $"/v1/projects/{harness.ProjectId}/invitations", harness.Token,
            $$"""{"user_id":"{{target}}","message":"Join the project","role_id":"{{projectRole}}"}""");
        Assert.Equal(HttpStatusCode.OK, projectPost.StatusCode);
        using var projectPostJson = JsonDocument.Parse(await projectPost.Content.ReadAsStringAsync());
        Assert.True(projectPostJson.RootElement.TryGetProperty("invitation", out _));
    }

    [Fact]
    public async Task UnknownGroupTypeReturnsLegacy5001()
    {
        await using var harness = await CreateHarnessAsync();

        using var applications = await harness.SendAsync(HttpMethod.Get,
            $"/v1/widgets/{harness.TeamId}/applications", harness.Token);
        await AssertLegacyErrorAsync(applications, HttpStatusCode.BadRequest, 5001);

        using var invitations = await harness.SendAsync(HttpMethod.Get,
            $"/v1/widgets/{harness.TeamId}/invitations", harness.Token);
        await AssertLegacyErrorAsync(invitations, HttpStatusCode.BadRequest, 5001);
    }

    [Fact]
    public async Task UnsupportedCollectionMethodsReturnMethodNotAllowed()
    {
        await using var harness = await CreateHarnessAsync();

        foreach (var path in new[]
                 {
                     $"/v1/teams/{harness.TeamId}/applications",
                     $"/v1/teams/{harness.TeamId}/invitations"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.Token);
            using var response = await harness.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [Fact]
    public async Task PaginationUsesLegacyDefaultsCapsAndRejectsOverflow()
    {
        await using var harness = await CreateHarnessAsync();

        using var teams = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams?word=Reference", harness.Token);
        Assert.Equal(HttpStatusCode.OK, teams.StatusCode);
        Assert.Equal("30", Header(teams, "X-Pagination-Limit"));

        using var terms = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams/{harness.TeamId}/term-banks", harness.Token);
        Assert.Equal(HttpStatusCode.OK, terms.StatusCode);
        Assert.Equal("30", Header(terms, "X-Pagination-Limit"));

        using var capped = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams?word=Reference&limit=999", harness.Token);
        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);
        Assert.Equal("100", Header(capped, "X-Pagination-Limit"));

        using var overflow = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams?word=Reference&page=2147483647&limit=100", harness.Token);
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
    }

    [Fact]
    public async Task PaginationHonorsTheSyntheticReferenceLimits()
    {
        await using var harness = await CreateHarnessAsync(configuration: new Dictionary<string, string?>
        {
            ["Pagination:DefaultLimit"] = "10",
            ["Pagination:MaxLimit"] = "100"
        });

        using var teams = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams?word=Reference", harness.Token);
        Assert.Equal(HttpStatusCode.OK, teams.StatusCode);
        Assert.Equal("10", Header(teams, "X-Pagination-Limit"));

        using var terms = await harness.SendAsync(HttpMethod.Get,
            $"/v1/teams/{harness.TeamId}/term-banks", harness.Token);
        Assert.Equal(HttpStatusCode.OK, terms.StatusCode);
        Assert.Equal("10", Header(terms, "X-Pagination-Limit"));
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : "";

    private static async Task AssertLegacyErrorAsync(HttpResponseMessage response,
        HttpStatusCode status, int code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetInt32());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private static readonly ObjectId FixtureOwnerId = ObjectId.Parse("650000000000000000000101");
        private static readonly ObjectId FixtureTeamId = ObjectId.Parse("650000000000000000000201");
        private static readonly ObjectId ProjectSetId = ObjectId.Parse("650000000000000000000401");
        private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private JsonDocument? _fixture;
        private LocalServices _services = new();

        public LocalServices Services => _services;
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public string Token { get; private set; } = "";
        public ObjectId TeamId => FixtureTeamId;
        public ObjectId ProjectId { get; private set; }

        public async Task InitializeAsync(bool openTeamApplications, bool createProject,
            Dictionary<string, string?>? configuration)
        {
            await _services.InitializeAsync();
            var repoRoot = FindRepoRoot();
            var fixturePath = Path.Combine(repoRoot, "tools", "legacy-reference", "fixtures",
                "runtime_flask_reference.json");
            _fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
            await SeedAsync(_fixture.RootElement.GetProperty("seed_collections"));

            if (openTeamApplications)
                await _services.Store.Collection("team").UpdateOneAsync(
                    new BsonDocument("_id", TeamId),
                    new BsonDocument("$set", new BsonDocument("m_a", 2)));

            await _services.Store.Collection("project_set").ReplaceOneAsync(
                new BsonDocument("_id", ProjectSetId),
                new BsonDocument
                {
                    ["_id"] = ProjectSetId,
                    ["n"] = "Default",
                    ["i"] = "",
                    ["t"] = TeamId,
                    ["ct"] = FixedTime,
                    ["et"] = FixedTime,
                    ["d"] = true
                }, new ReplaceOptions { IsUpsert = true });

            var overrides = configuration ?? new Dictionary<string, string?>();
            Factory = _services.App.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(overrides)));
            Http = Factory.CreateClient();

            var owner = await _services.Store.Collection("user")
                .Find(new BsonDocument("_id", FixtureOwnerId)).FirstAsync();
            Token = Factory.Services.GetRequiredService<LegacyToken>()
                .Issue(FixtureOwnerId.ToString(), owner.Text("p"));

            if (createProject)
                ProjectId = await CreateProjectAsync();
        }

        public async Task<string> CreateExternalUserAsync()
        {
            var id = ObjectId.GenerateNewId();
            await _services.Store.Collection("user").InsertOneAsync(new BsonDocument
            {
                ["_id"] = id,
                ["n"] = "External " + id.ToString()[^8..],
                ["ns"] = "external " + id.ToString()[^8..],
                ["as"] = new BsonArray(),
                ["asrch"] = "",
                ["e"] = id + "@example.test",
                ["p"] = "synthetic-password-hash",
                ["b"] = false,
                ["admin"] = false,
                ["c"] = FixedTime
            });
            return id.ToString();
        }

        public async Task<string> TokenForAsync(string userId)
        {
            var user = await _services.Store.Collection("user")
                .Find(new BsonDocument("_id", ObjectId.Parse(userId))).FirstAsync();
            return Factory.Services.GetRequiredService<LegacyToken>()
                .Issue(userId, user.Text("p"));
        }

        public async Task<ObjectId> RoleIdAsync(string collection, string code) =>
            (await _services.Store.Collection(collection)
                .Find(new BsonDocument("m_o", code)).FirstAsync())["_id"].AsObjectId;

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await Http.SendAsync(request);
        }

        public Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string token,
            string json)
        {
            var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return Http.SendAsync(request);
        }

        private async Task<ObjectId> CreateProjectAsync()
        {
            using var response = await SendJsonAsync(HttpMethod.Post,
                $"/v1/teams/{TeamId}/projects", Token,
                """{"name":"Plural Project","source_language":"ja","target_languages":["en"],"allow_apply_type":2,"application_check_type":2}""");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var json = JsonDocument.Parse(body);
            return ObjectId.Parse(json.RootElement.GetProperty("project").GetProperty("id").GetString()!);
        }

        private async Task SeedAsync(JsonElement collections)
        {
            var names = collections.EnumerateObject().Select(p => p.Name)
                .Order(StringComparer.Ordinal).ToArray();
            foreach (var name in names)
                await _services.Store.Database.DropCollectionAsync(name);
            foreach (var name in names)
            {
                foreach (var doc in collections.GetProperty(name).GetProperty("documents").EnumerateArray())
                {
                    var bson = Convert.FromBase64String(doc.GetProperty("bson_base64").GetString()!);
                    await _services.Store.Collection(name)
                        .InsertOneAsync(BsonSerializer.Deserialize<BsonDocument>(bson));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Factory is not null) await Factory.DisposeAsync();
            _fixture?.Dispose();
            await _services.DisposeAsync();
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Shoreline.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Shoreline.sln not found");
        }
    }
}
