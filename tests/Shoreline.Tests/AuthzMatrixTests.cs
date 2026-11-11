using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// Shared world builder for the authorization matrix (docs/authz-matrix.md).
/// Each test builds its own team + project through the public HTTP API, then
/// pins role identities with direct member-row inserts. Nothing here guesses:
/// every expectation was checked against the Flask oracle first.
/// </summary>
internal sealed class AuthzWorld : IAsyncDisposable
{
    private static readonly string Password = PasswordHash.Create("authz-matrix-password");

    public required LocalServices Services { get; init; }
    public required BsonDocument Owner { get; init; }
    public required BsonDocument TeamAdmin { get; init; }
    public required BsonDocument TeamMember { get; init; }
    public required BsonDocument Translator { get; init; }
    public required BsonDocument Proofreader { get; init; }
    public required BsonDocument Typesetter { get; init; }
    public required BsonDocument Stranger { get; init; }
    public required BsonDocument Banned { get; init; }
    public required BsonDocument SiteAdmin { get; init; }
    public required ObjectId TeamId { get; init; }
    public required ObjectId ProjectId { get; init; }
    public required ObjectId TargetId { get; init; }
    public required Dictionary<string, HttpClient> Clients { get; init; }

    public HttpClient Anon { get; private set; } = null!;
    public HttpClient BadToken { get; private set; } = null!;
    public HttpClient WrongScheme { get; private set; } = null!;

    public static async Task<AuthzWorld> CreateAsync(LocalServices services, int allowApplyType = 1)
    {
        // Team founding is gated by the site setting (oacg, default true, both
        // stacks). The matrix world opens founding so the owner stays a plain
        // user; the gate itself is covered by Teams_CreateGate_... below.
        await services.Store.Collection("site_setting").UpdateOneAsync(
            new BsonDocument("n", "site"),
            new BsonDocument
            {
                ["$set"] = new BsonDocument("oacg", false),
                ["$setOnInsert"] = new BsonDocument
                {
                    ["n"] = "site", ["ew"] = true,
                    ["we"] = new BsonArray(), ["ajt"] = new BsonArray(),
                    ["h"] = "", ["hc"] = "", ["st"] = "", ["hw"] = "", ["hi"] = ""
                }
            }, new UpdateOptions { IsUpsert = true });
        var owner = await UserAsync(services, admin: false);
        var teamAdmin = await UserAsync(services, admin: false);
        var teamMember = await UserAsync(services, admin: false);
        var translator = await UserAsync(services, admin: false);
        var proofreader = await UserAsync(services, admin: false);
        var typesetter = await UserAsync(services, admin: false);
        var stranger = await UserAsync(services, admin: false);
        var banned = await UserAsync(services, admin: false, banned: true);
        var siteAdmin = await UserAsync(services, admin: true);

        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal);
        HttpClient ClientOf(BsonDocument user)
        {
            var client = services.App.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                services.App.Services.GetRequiredService<LegacyToken>()
                    .Issue(user["_id"].AsObjectId.ToString(), user.Text("p")));
            return client;
        }

        var ownerClient = ClientOf(owner);
        clients["owner"] = ownerClient;
        clients["teamAdmin"] = ClientOf(teamAdmin);
        clients["teamMember"] = ClientOf(teamMember);
        clients["translator"] = ClientOf(translator);
        clients["proofreader"] = ClientOf(proofreader);
        clients["typesetter"] = ClientOf(typesetter);
        clients["stranger"] = ClientOf(stranger);
        clients["banned"] = ClientOf(banned);
        clients["siteAdmin"] = ClientOf(siteAdmin);

        var teamBody = await BodyAsync(await ownerClient.PostAsJsonAsync("/v1/teams", new
        {
            name = Name("team"), intro = "", allow_apply_type = 1, application_check_type = 2,
            default_role = (await RoleAsync(services, "team", "member")).ToString()
        }));
        var teamId = ObjectId.Parse(teamBody.GetProperty("team").GetProperty("id").GetString());
        var projectBody = await BodyAsync(await ownerClient.PostAsJsonAsync($"/v1/teams/{teamId}/projects", new
        {
            name = Name("project"), intro = "", allow_apply_type = allowApplyType, application_check_type = 2,
            source_language = "ja", target_languages = new[] { "zh-CN" }
        }));
        var projectId = ObjectId.Parse(projectBody.GetProperty("project").GetProperty("id").GetString());
        var target = await services.Store.Collection("target")
            .Find(new BsonDocument("t", projectId)).FirstAsync();

        await TeamMemberAsync(services, teamId, teamAdmin, baseTag: "admin");
        await TeamMemberAsync(services, teamId, teamMember, baseTag: "member");
        await ProjectMemberAsync(services, projectId, translator, "translator");
        await ProjectMemberAsync(services, projectId, proofreader, "proofreader");
        await ProjectMemberAsync(services, projectId, typesetter, "typesetter");
        await ProjectMemberAsync(services, projectId, banned, "translator");

        var world = new AuthzWorld
        {
            Services = services,
            Owner = owner,
            TeamAdmin = teamAdmin,
            TeamMember = teamMember,
            Translator = translator,
            Proofreader = proofreader,
            Typesetter = typesetter,
            Stranger = stranger,
            Banned = banned,
            SiteAdmin = siteAdmin,
            TeamId = teamId,
            ProjectId = projectId,
            TargetId = target["_id"].AsObjectId,
            Clients = clients,
            Anon = services.App.CreateClient(),
            BadToken = services.App.CreateClient(),
            WrongScheme = services.App.CreateClient(),
        };
        world.BadToken.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        world.WrongScheme.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Token", "not-a-real-token");
        return world;
    }

    public HttpClient Client(string name) => Clients[name];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in Clients.Values) client.Dispose();
        Anon.Dispose();
        BadToken.Dispose();
        WrongScheme.Dispose();
        await ValueTask.CompletedTask;
    }

    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];

    private static async Task<BsonDocument> UserAsync(LocalServices services, bool admin, bool banned = false)
    {
        var id = ObjectId.GenerateNewId();
        var name = "z" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id, ["n"] = name, ["ns"] = SearchText.Normalize(name), ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(), ["e"] = name + "@example.test", ["p"] = Password,
            ["admin"] = admin, ["b"] = banned
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private static async Task<ObjectId> RoleAsync(LocalServices services, string scope, string code) =>
        (await services.Store.Collection(scope + "_role").Find(new BsonDocument("m_o", code)).FirstAsync())["_id"].AsObjectId;

    private static async Task TeamMemberAsync(LocalServices services, ObjectId team, BsonDocument user, string baseTag)
    {
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["team"] = team, ["user"] = user["_id"],
            ["base_tag"] = baseTag, ["status"] = "active", ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray { "translator", "proofreader" },
            ["default_display_name"] = "", ["aliases"] = new BsonArray(),
            ["asrch"] = new BsonArray(), ["version"] = 0
        });
    }

    private static async Task ProjectMemberAsync(LocalServices services, ObjectId project, BsonDocument user, string tag)
    {
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project, ["user"] = user["_id"],
            ["status"] = "active", ["tags"] = new BsonArray { tag },
            ["display_name"] = user.Text("n"), ["version"] = 0
        });
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

internal static class AuthzAssert
{
    public static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, int code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetInt32());
        return body;
    }

    public static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

[Collection("local-services")]
public sealed class AuthzMatrixTests(LocalServices services)
{
    [Fact]
    public async Task AuthPrimitives_anonymous_badToken_banned_stranger_siteAdmin()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        // Anonymous: 401 + NeedTokenError.
        await AuthzAssert.ErrorAsync(await world.Anon.GetAsync($"/v1/teams/{world.TeamId}"),
            HttpStatusCode.Unauthorized, 1003);
        // Garbage bearer token: 401 + BadTokenError.
        await AuthzAssert.ErrorAsync(await world.BadToken.GetAsync($"/v1/teams/{world.TeamId}"),
            HttpStatusCode.Unauthorized, 1004);
        // Wrong scheme: 401 + BadTokenError (Python: token must start with "Bearer ").
        await AuthzAssert.ErrorAsync(await world.WrongScheme.GetAsync($"/v1/teams/{world.TeamId}"),
            HttpStatusCode.Unauthorized, 1004);
        // Banned identity with a valid token: 400 + UserBannedError.
        await AuthzAssert.ErrorAsync(await world.Client("banned").GetAsync($"/v1/teams/{world.TeamId}"),
            HttpStatusCode.BadRequest, 1006);
        // Stranger: 400 + NoPermissionError (the codebase maps "forbidden" to 400/103).
        await AuthzAssert.ErrorAsync(await world.Client("stranger").GetAsync($"/v1/teams/{world.TeamId}"),
            HttpStatusCode.BadRequest, 103);
        // Site admin has no membership bypass: same as stranger.
        await AuthzAssert.ErrorAsync(await world.Client("siteAdmin").GetAsync($"/v1/teams/{world.TeamId}"),
            HttpStatusCode.BadRequest, 103);
        // Team member reads fine (GET returns the team view directly).
        Assert.Equal(world.TeamId.ToString(),
            (await AuthzAssert.OkAsync(await world.Client("teamMember").GetAsync($"/v1/teams/{world.TeamId}")))
            .GetProperty("id").GetString());
    }

    [Fact]
    public async Task Teams_CreateGate_onlyAdminWhenEnabled()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        // Re-enable the gate explicitly: the world opens it, fixtures share one DB.
        await services.Store.Collection("site_setting").UpdateOneAsync(
            new BsonDocument("n", "site"),
            new BsonDocument("$set", new BsonDocument("oacg", true)));

        var defaultRole = (await services.Store.Collection("team_role")
            .Find(new BsonDocument("m_o", "member")).FirstAsync())["_id"].AsObjectId.ToString();
        object Body() => new
        {
            name = "gt_" + ObjectId.GenerateNewId().ToString()[12..],
            intro = "",
            allow_apply_type = 1,
            application_check_type = 2,
            default_role = defaultRole
        };
        // Banned is rejected by the auth layer before the gate is even read.
        await AuthzAssert.ErrorAsync(await world.Client("banned").PostAsJsonAsync("/v1/teams", Body()),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(await world.Client("stranger").PostAsJsonAsync("/v1/teams", Body()),
            HttpStatusCode.BadRequest, 3008);
        await AuthzAssert.ErrorAsync(await world.Client("owner").PostAsJsonAsync("/v1/teams", Body()),
            HttpStatusCode.BadRequest, 3008);
        var created = await AuthzAssert.OkAsync(await world.Client("siteAdmin").PostAsJsonAsync("/v1/teams", Body()));
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("team").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Teams_Create_anyAuthenticatedUser_anonymousDenied()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        await AuthzAssert.ErrorAsync(
            await world.Anon.PostAsJsonAsync("/v1/teams", new { name = "anon-team" }),
            HttpStatusCode.Unauthorized, 1003);
        // Any logged-in user (even a stranger elsewhere) may found a team.
        var created = await AuthzAssert.OkAsync(
            await world.Client("stranger").PostAsJsonAsync("/v1/teams", new
            {
                name = "fd_" + ObjectId.GenerateNewId().ToString()[12..],
                intro = "",
                allow_apply_type = 1,
                application_check_type = 2,
                default_role = (await services.Store.Collection("team_role")
                    .Find(new BsonDocument("m_o", "member")).FirstAsync())["_id"].AsObjectId.ToString()
            }));
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("team").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Teams_Update_changeRequiresManager_creatorOnlyFields()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}";

        await AuthzAssert.ErrorAsync(await world.Client("teamMember").PutAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await world.Client("stranger").PutAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 103);
        // Team admin holds team:CHANGE: intro edit works.
        await AuthzAssert.OkAsync(await world.Client("teamAdmin").PutAsJsonAsync(path, new { intro = "edited" }));
        // ...but the proofread marker dials are creator-only.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PutAsJsonAsync(path, new { proofread_marker_opacity = 50 }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync(path, new { proofread_marker_opacity = 50 }));
    }

    [Fact]
    public async Task Teams_Delete_onlyCreator()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}";

        await AuthzAssert.ErrorAsync(await world.Client("teamMember").DeleteAsync(path),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await world.Client("teamAdmin").DeleteAsync(path),
            HttpStatusCode.BadRequest, 103);
        // Dissolution refuses teams with working projects: complete it first.
        await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync($"/v1/projects/{world.ProjectId}/complete", new { }));
        await AuthzAssert.OkAsync(await world.Client("owner").DeleteAsync(path));
        // The row is gone afterwards (legacy TeamNotExistError rides on 400).
        await AuthzAssert.ErrorAsync(await world.Client("owner").GetAsync(path),
            HttpStatusCode.BadRequest, 3001);
    }

    [Fact]
    public async Task Teams_MembersList_requiresTeamAccess()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}/members";

        await AuthzAssert.ErrorAsync(await world.Anon.GetAsync(path), HttpStatusCode.Unauthorized, 1003);
        await AuthzAssert.ErrorAsync(await world.Client("stranger").GetAsync(path),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await world.Client("translator").GetAsync(path),
            HttpStatusCode.BadRequest, 103);
        var listed = await AuthzAssert.OkAsync(await world.Client("teamMember").GetAsync(path));
        Assert.True(listed.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Teams_MembersAdd_managerOnly_adminTagCreatorOnly()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}/members";

        // Plain member cannot invite.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].ToString(), base_tag = "member" }),
            HttpStatusCode.BadRequest, 103);
        // Team admin can add members but cannot mint fellow admins.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PostAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].ToString(), base_tag = "admin" }),
            HttpStatusCode.BadRequest, 103);
        var added = await AuthzAssert.OkAsync(
            await world.Client("teamAdmin").PostAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].ToString(), base_tag = "member" }));
        Assert.Equal("member", added.GetProperty("member").GetProperty("base_tag").GetString());
        // Only the creator can mint an admin.
        var minted = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(path,
                new { user_id = world.Translator["_id"].ToString(), base_tag = "admin" }));
        Assert.Equal("admin", minted.GetProperty("member").GetProperty("base_tag").GetString());
    }

    [Fact]
    public async Task Teams_MemberUpdateRemove_managerHierarchy()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        Task<BsonDocument> MemberRowAsync(BsonDocument user) => services.Store.Collection("team_member")
            .Find(new BsonDocument { ["team"] = world.TeamId, ["user"] = user["_id"] }).FirstAsync();
        var memberRow = await MemberRowAsync(world.TeamMember);
        var memberPath = $"/v1/teams/{world.TeamId}/members/{memberRow["_id"]}";

        // Version is fenced before the manager check (valid body needed to reach 103).
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PatchAsJsonAsync(memberPath,
                new { expected_version = 0, base_tag = "admin" }),
            HttpStatusCode.BadRequest, 103);
        // A non-creator manager cannot mint fellow admins (mirrors AddAsync).
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PatchAsJsonAsync(memberPath,
                new { expected_version = (int)memberRow.Integer("version"), base_tag = "admin" }),
            HttpStatusCode.BadRequest, 103);
        // Only the creator can mint an admin.
        await AuthzAssert.OkAsync(
            await world.Client("owner").PatchAsJsonAsync(memberPath,
                new { expected_version = (int)memberRow.Integer("version"), base_tag = "admin" }));
        // A manager can remove a plain member...
        await AuthzAssert.OkAsync(await world.Client("owner").DeleteAsync(memberPath));
        // ...but nobody removes the creator row (system tag is protected: 422/5116).
        var creatorRow = await MemberRowAsync(world.Owner);
        await AuthzAssert.ErrorAsync(
            await world.Client("owner").DeleteAsync($"/v1/teams/{world.TeamId}/members/{creatorRow["_id"]}"),
            HttpStatusCode.UnprocessableEntity, 5116);
    }

    [Fact]
    public async Task Teams_TagPolicy_managerOnly_systemTagsCreatorOnly()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}/identity-tag-policy";

        await AuthzAssert.ErrorAsync(await world.Client("teamMember").GetAsync(path),
            HttpStatusCode.BadRequest, 103);
        var policy = await AuthzAssert.OkAsync(await world.Client("teamAdmin").GetAsync(path));
        var version = policy.GetProperty("version").GetInt32();
        // Team admin may not edit system team tags.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PatchAsJsonAsync(path, new
            {
                expected_version = version,
                upserts = new[] { new { scope = "team", code = "admin", name = "admin", permissions = new[] { "team:ACCESS" } } },
                removes = Array.Empty<object>()
            }),
            HttpStatusCode.BadRequest, 103);
        // Creator passes with a no-op patch.
        await AuthzAssert.OkAsync(
            await world.Client("owner").PatchAsJsonAsync(path, new
            {
                expected_version = version,
                upserts = Array.Empty<object>(),
                removes = Array.Empty<object>()
            }));
    }
}
