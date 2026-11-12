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

[Collection("local-services")]
public sealed class ProjectTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task CreateReturnsProjectMembersAndTargetsAndPersistsCompatibleDocuments()
    {
        var creator = await UserAsync();
        using var client = Client(creator);
        var teamId = await CreateTeamAsync(client, Name("team"));
        var project = await CreateProjectAsync(client, teamId, Name("project"));
        Assert.Equal(0, project.GetProperty("status").GetInt32());
        Assert.Equal("NORMAL", project.GetProperty("identity_status").GetString());
        Assert.Equal(creator["_id"].ToString(), project.GetProperty("owner_user_id").GetString());
        Assert.Equal(1, project.GetProperty("user_count").GetInt32());
        Assert.Equal(2, project.GetProperty("target_count").GetInt32());
        Assert.Equal("project", project.GetProperty("group_type").GetString());
        Assert.Equal("team", project.GetProperty("team").GetProperty("group_type").GetString());
        Assert.Equal("NORMAL", project.GetProperty("status_name").GetString());
        Assert.Contains("project:ACCESS", project.GetProperty("effective_permissions").EnumerateArray().Select(v => v.GetString()));
        Assert.True(project.GetProperty("can_order_files").GetBoolean());
        Assert.Single(project.GetProperty("member_summary").EnumerateArray());
        var stored = await ProjectAsync(ObjectId.Parse(project.GetProperty("id").GetString()));
        Assert.Equal(creator["_id"], stored.GetValue("ou"));
        Assert.Equal(0, stored.Integer("st"));
        Assert.Equal(2, stored.Integer("tc"));
        Assert.Equal(1, stored.Integer("m_uc"));
        var targets = await services.Store.Collection("target").Find(new BsonDocument("t", stored["_id"])).ToListAsync();
        Assert.Equal(2, targets.Count);
        var member = Assert.Single(await services.Store.Collection("project_member").Find(new BsonDocument("project", stored["_id"])).ToListAsync());
        Assert.Equal(creator["_id"], member["user"]);
        Assert.Equal(stored["_id"].ToString() + ":u:" + creator["_id"], member.Text("ik"));
        Assert.Contains("creator", member.GetValue("tags").AsBsonArray.Select(v => v.AsString));
        Assert.Equal("active", member.Text("status"));
        var languages = await services.Store.Collection("language").Find(new BsonDocument("_id", stored["ol"])).ToListAsync();
        Assert.Single(languages);
    }

    [Fact]
    public async Task CreateAcceptsTeamUserAllowApplyType()
    {
        // ProjectAllowApplyType adds TEAM_USER = 3 on top of the team enum
        // (app/models/project.py:74-85). Rejecting it broke project creation
        // from the frontend, which offers all three choices.
        var creator = await UserAsync();
        using var client = Client(creator);
        var teamId = await CreateTeamAsync(client, Name("team"));
        var form = new Dictionary<string, object?>
        {
            ["name"] = Name("project"),
            ["intro"] = "",
            ["allow_apply_type"] = 3,
            ["application_check_type"] = 2,
            ["source_language"] = "ja",
            ["target_languages"] = new[] { "zh-CN" }
        };
        var body = await BodyAsync(await client.PostAsJsonAsync("/v1/teams/" + teamId + "/projects", form));
        var project = body.GetProperty("project");
        Assert.Equal(3, project.GetProperty("allow_apply_type").GetInt32());
        var stored = await ProjectAsync(ObjectId.Parse(project.GetProperty("id").GetString()));
        Assert.Equal(3, stored.Integer("m_a"));
    }

    [Fact]
    public async Task GetProjectReturnsMemberSummaryAndHidesOutsiders()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString();
        var body = await BodyAsync(await owner.GetAsync("/v1/projects/" + projectId));
        var summary = Assert.Single(body.GetProperty("member_summary").EnumerateArray());
        Assert.True(summary.GetProperty("is_owner").GetBoolean());
        Assert.Contains("creator", summary.GetProperty("tags").EnumerateArray().Select(v => v.GetString()));
        var outsider = await UserAsync(admin: false);
        using var stranger = Client(outsider);
        await ErrorAsync(await stranger.GetAsync("/v1/projects/" + projectId), 103);
        await ErrorAsync(await stranger.GetAsync("/v1/teams/" + teamId + "/projects"), 103);
        await ErrorAsync(await stranger.PostAsJsonAsync("/v1/teams/" + teamId + "/projects", ProjectForm()), 103);
    }

    [Fact]
    public async Task EditProjectRenamesAndGuardsPositionLockForTeamCreator()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString();
        var renamed = Name("project");
        var body = await BodyAsync(await owner.PutAsJsonAsync("/v1/projects/" + projectId, new { name = renamed, intro = "hello" }));
        Assert.Equal(renamed, body.GetProperty("project").GetProperty("name").GetString());
        Assert.Single(body.GetProperty("project").GetProperty("member_summary").EnumerateArray());
        Assert.False(body.TryGetProperty("member_summary", out _));
        Assert.Equal("hello", body.GetProperty("project").GetProperty("intro").GetString());
        var member = await UserAsync();
        await MemberAsync(teamId, member);
        using var memberClient = Client(member);
        await ErrorAsync(await memberClient.PutAsJsonAsync("/v1/projects/" + projectId,
            new { name = Name("project"), position_lock_enabled = true, position_lock_tags = new[] { "translator" } }), 103);
        var locked = await BodyAsync(await owner.PutAsJsonAsync("/v1/projects/" + projectId,
            new { position_lock_enabled = true, position_lock_tags = new[] { "translator" } }));
        Assert.True(locked.GetProperty("project").GetProperty("position_lock_enabled").GetBoolean());
        Assert.Contains("translator", locked.GetProperty("project").GetProperty("position_lock_tags").EnumerateArray().Select(v => v.GetString()));
        var unlocked = await BodyAsync(await owner.PutAsJsonAsync("/v1/projects/" + projectId,
            new { position_lock_enabled = false, position_lock_tags = new[] { "translator" } }));
        Assert.Empty(unlocked.GetProperty("project").GetProperty("position_lock_tags").EnumerateArray());
        await ErrorAsync(await owner.PutAsJsonAsync("/v1/projects/" + projectId, (object)new { }), 110);
        // Member without project CHANGE permission cannot edit.
        await ErrorAsync(await memberClient.PutAsJsonAsync("/v1/projects/" + projectId, new { name = Name("project") }), 103);
        // Finished projects reject edits (4002) once cleared/completed.
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(projectId)),
            new BsonDocument("$set", new BsonDocument("st", 5)));
        await ErrorAsync(await owner.PutAsJsonAsync("/v1/projects/" + projectId, new { name = Name("project") }), 4002);
    }

    [Fact]
    public async Task ProjectSetsCreateRenameAndDeleteRehomeProjectsToDefaultSet()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var setBody = await BodyAsync(await owner.PostAsJsonAsync("/v1/teams/" + teamId + "/project-sets", new { name = "shelf" }));
        var setId = setBody.GetProperty("project_set").GetProperty("id").GetString();
        Assert.False(setBody.GetProperty("project_set").GetProperty("default").GetBoolean());
        var renamed = await BodyAsync(await owner.PutAsJsonAsync("/v1/project-sets/" + setId, new { name = "shelf2" }));
        Assert.Equal("shelf2", renamed.GetProperty("project_set").GetProperty("name").GetString());
        var setList = await BodyAsync(await owner.GetAsync("/v1/teams/" + teamId + "/project-sets"));
        Assert.Contains(setList.EnumerateArray(), row => row.GetProperty("id").GetString() == setId);
        var project = await CreateProjectAsync(owner, teamId, Name("project"), setId);
        Assert.Equal(setId, project.GetProperty("project_set").GetProperty("id").GetString());
        var deleted = await BodyAsync(await owner.DeleteAsync("/v1/project-sets/" + setId));
        Assert.Equal("Removed", deleted.GetProperty("message").GetString());
        var stored = await ProjectAsync(ObjectId.Parse(project.GetProperty("id").GetString()));
        var defaultSet = await services.Store.Collection("project_set").Find(new BsonDocument { ["t"] = teamId, ["d"] = true }).FirstAsync();
        Assert.Equal(defaultSet["_id"], stored["ps"]);
        Assert.Equal(0, await services.Store.Collection("project_set").CountDocumentsAsync(new BsonDocument("_id", ObjectId.Parse(setId))));
        await ErrorAsync(await owner.PutAsJsonAsync("/v1/project-sets/" + defaultSet["_id"], new { name = "x" }), 103);
        await ErrorAsync(await owner.DeleteAsync("/v1/project-sets/" + defaultSet["_id"]), 103);
    }

    [Fact]
    public async Task TargetsAddListDuplicateAndDelete()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString();
        var created = await BodyAsync(await owner.PostAsJsonAsync("/v1/projects/" + projectId + "/targets", new { language = "de" }));
        Assert.Equal("Successfully added target language.", created.GetProperty("message").GetString());
        var targetId = created.GetProperty("target").GetProperty("id").GetString();
        Assert.Equal("de", created.GetProperty("target").GetProperty("language").GetProperty("code").GetString());
        // app/apis/project.py:150-156 returns a bare list; pagination lives in
        // X-Pagination-* headers, not in a {count, items} envelope.
        using var listedResponse = await owner.GetAsync("/v1/projects/" + projectId + "/targets");
        var listed = await BodyAsync(listedResponse);
        Assert.Equal(3, listed.GetArrayLength());
        Assert.Equal("3", listedResponse.Headers.GetValues("X-Pagination-Count").Single());
        var afterAdd = await ProjectAsync(ObjectId.Parse(projectId));
        Assert.Equal(3, afterAdd.Integer("tc"));
        await ErrorAsync(await owner.PostAsJsonAsync("/v1/projects/" + projectId + "/targets", new { language = "de" }), 6004);
        await ErrorAsync(await owner.PostAsJsonAsync("/v1/projects/" + projectId + "/targets", new { language = "ja" }), 6002);
        await ErrorAsync(await owner.PostAsJsonAsync("/v1/projects/" + projectId + "/targets", new { language = "nope" }), 2);
        var member = await UserAsync();
        await MemberAsync(teamId, member);
        using var memberClient = Client(member);
        await ErrorAsync(await memberClient.PostAsJsonAsync("/v1/projects/" + projectId + "/targets", new { language = "fr" }), 103);
        var deleted = await BodyAsync(await owner.DeleteAsync("/v1/targets/" + targetId));
        Assert.Equal("Removed", deleted.GetProperty("message").GetString());
        var afterDelete = await ProjectAsync(ObjectId.Parse(projectId));
        Assert.Equal(2, afterDelete.Integer("tc"));
    }

    [Fact]
    public async Task CreateRejectsForeignProjectSetWith4022()
    {
        var first = await UserAsync();
        var second = await UserAsync();
        using var one = Client(first);
        using var two = Client(second);
        var firstTeam = await CreateTeamAsync(one, Name("team"));
        var secondTeam = await CreateTeamAsync(two, Name("team"));
        var foreignSet = await services.Store.Collection("project_set").Find(new BsonDocument("t", firstTeam))
            .Sort(new BsonDocument("_id", 1)).FirstAsync();
        await ErrorAsync(await two.PostAsJsonAsync("/v1/teams/" + secondTeam + "/projects", ProjectForm(foreignSet["_id"].ToString())), 4022);
        Assert.Equal(0, await services.Store.Collection("project").CountDocumentsAsync(new BsonDocument("t", secondTeam)));
    }

    [Fact]
    public async Task TeamProjectListReturnsCards()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var body = await BodyAsync(await owner.GetAsync("/v1/teams/" + teamId + "/projects"));
        // The legacy endpoint returns a bare array (app/apis/team.py:428-438);
        // the frontend calls data.map on it, so a wrapper breaks the page.
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        var card = Assert.Single(body.EnumerateArray());
        Assert.Equal(project.GetProperty("id").GetString(), card.GetProperty("id").GetString());
        Assert.Equal(teamId.ToString(), card.GetProperty("team").GetProperty("id").GetString());
        Assert.True(card.GetProperty("project_set").GetProperty("default").GetBoolean());
        Assert.Contains("project:ACCESS", card.GetProperty("effective_permissions").EnumerateArray().Select(v => v.GetString()));
        // Every card carries the compact member summary the legacy list injects.
        var summary = card.GetProperty("member_summary");
        Assert.Equal(JsonValueKind.Array, summary.ValueKind);
        var member = Assert.Single(summary.EnumerateArray());
        Assert.False(string.IsNullOrEmpty(member.GetProperty("display_name").GetString()));
        Assert.Equal("active", member.GetProperty("status").GetString());
        var counted = await BodyAsync(await owner.GetAsync("/v1/teams/" + teamId + "/projects?status=NORMAL"));
        Assert.Equal(1, counted.GetArrayLength());
        var cleared = await BodyAsync(await owner.GetAsync("/v1/teams/" + teamId + "/projects?status=CLEARED"));
        Assert.Equal(0, cleared.GetArrayLength());
    }

    // ---- helpers ----

    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];

    private static object ProjectForm(string? projectSet = null) => new
    {
        name = Name("project"),
        intro = "",
        allow_apply_type = 1,
        application_check_type = 2,
        project_set = projectSet,
        source_language = "ja",
        target_languages = new[] { "zh-CN", "en" }
    };

    private async Task<ObjectId> CreateTeamAsync(HttpClient client, string name)
    {
        var role = (await services.Store.Collection("team_role").Find(new BsonDocument("m_o", "member")).FirstAsync())["_id"].ToString()!;
        var body = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name,
            intro = "",
            allow_apply_type = 1,
            application_check_type = 2,
            default_role = role
        }));
        return ObjectId.Parse(body.GetProperty("team").GetProperty("id").GetString());
    }

    private async Task<JsonElement> CreateProjectAsync(HttpClient client, ObjectId teamId, string name, string? projectSet = null)
    {
        var form = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["intro"] = "",
            ["allow_apply_type"] = 1,
            ["application_check_type"] = 2,
            ["project_set"] = projectSet,
            ["source_language"] = "ja",
            ["target_languages"] = new[] { "zh-CN", "en" }
        };
        var body = await BodyAsync(await client.PostAsJsonAsync("/v1/teams/" + teamId + "/projects", form));
        return body.GetProperty("project");
    }

    private Task<BsonDocument> ProjectAsync(ObjectId id) => services.Store.Collection("project").Find(new BsonDocument("_id", id)).FirstAsync();

    private async Task<BsonDocument> UserAsync(bool admin = true)
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["e"] = name + "@example.test",
            ["p"] = Password,
            ["admin"] = admin,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task MemberAsync(ObjectId team, BsonDocument user, string tag = "member", string status = "active") =>
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = team,
            ["user"] = user["_id"],
            ["base_tag"] = tag,
            ["status"] = status,
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["version"] = 0
        });

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", services.App.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task ErrorAsync(HttpResponseMessage response, int code)
    {
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task CreateProject_InvokesProjectCreatedHooks_AndSurvivesHookFailure()
    {
        var store = services.Store;
        var clock = TimeProvider.System;
        var types = services.App.Services.GetRequiredService<LegacyTypes>();
        var teams = services.App.Services.GetRequiredService<TeamService>();
        var members = services.App.Services.GetRequiredService<TeamMemberService>();
        var permissions = services.App.Services.GetRequiredService<IdentityPermissions>();

        var invoked = new List<ObjectId>();
        var successHook = new RecordingProjectCreatedHook(invoked);
        var failingHook = new ThrowingProjectCreatedHook();

        var projectService = new ProjectService(
            store, clock, types, permissions, teams, members,
            projectCreatedHooks: [successHook, failingHook]);

        var creator = await UserAsync();
        using var client = Client(creator);
        var teamId = await CreateTeamAsync(client, Name("team"));
        var team = await members.GetTeamAsync(teamId.ToString(), CancellationToken.None);

        var project = await projectService.CreateProjectAsync(
            creator, team, JsonDocument.Parse(JsonSerializer.Serialize(ProjectForm())).RootElement, CancellationToken.None);

        Assert.NotNull(project);
        var createdId = project["_id"].AsObjectId;
        Assert.Contains(createdId, invoked);
    }

    private sealed class RecordingProjectCreatedHook(List<ObjectId> invoked) : IProjectCreatedHook
    {
        public Task OnProjectCreatedAsync(ObjectId projectId, CancellationToken ct)
        {
            invoked.Add(projectId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProjectCreatedHook : IProjectCreatedHook
    {
        public Task OnProjectCreatedAsync(ObjectId projectId, CancellationToken ct) =>
            throw new InvalidOperationException("hook exploded");
    }
}
