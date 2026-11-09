using System.Net;
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

[Collection("local-services")]
public sealed class TeamIdentityTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");
    private sealed record Team(BsonDocument Document, BsonDocument Creator, BsonDocument Admin, BsonDocument Plain,
        BsonDocument CreatorMember, BsonDocument AdminMember, BsonDocument PlainMember)
    {
        public string Path => $"/v1/teams/{Document["_id"]}";
    }

    [Fact]
    public async Task AddRestoreAndSoftRemovalPreserveIdsAliasesAndAudit()
    {
        var team = await TeamAsync();
        var user = await UserAsync();
        using var manager = Client(team.Creator);
        manager.DefaultRequestHeaders.Add("X-Request-ID", "team-member-test");
        var added = await BodyAsync(await manager.PostAsJsonAsync(team.Path + "/members", new
        { user_id = user["_id"].ToString(), worker_qualifications = new[] { "translator", "translator" } }));
        var id = added.GetProperty("member").GetProperty("id").GetString();
        Assert.Equal(0, added.GetProperty("member").GetProperty("version").GetInt32());
        Assert.Single(added.GetProperty("member").GetProperty("worker_qualifications").EnumerateArray());
        using var own = Client(user);
        await BodyAsync(await own.PatchAsJsonAsync($"{team.Path}/members/{id}/aliases", new { aliases = new[] { "Stored alias" }, expected_version = 0 }));
        await BodyAsync(await manager.DeleteAsync($"{team.Path}/members/{id}"));
        var removed = await MemberAsync(id!);
        Assert.Equal("removed", removed["status"].AsString);
        Assert.Equal(2, removed.Integer("version"));
        Assert.True(removed["removed_time"].IsBsonDateTime);
        var restored = await BodyAsync(await manager.PostAsJsonAsync(team.Path + "/members", new { user_id = user["_id"].ToString() }));
        Assert.Equal(id, restored.GetProperty("member").GetProperty("id").GetString());
        Assert.Equal(3, restored.GetProperty("member").GetProperty("version").GetInt32());
        Assert.Equal("Stored alias", restored.GetProperty("member").GetProperty("aliases")[0].GetString());
        Assert.True((await MemberAsync(id!))["removed_time"].IsBsonNull);
        var duplicate = await BodyAsync(await manager.PostAsJsonAsync(team.Path + "/members", new { user_id = user["_id"].ToString(), base_tag = "creator" }));
        Assert.Equal(3, duplicate.GetProperty("member").GetProperty("version").GetInt32());
        var audits = await services.Store.Collection("identity_audit_event").Find(new BsonDocument("member_id", id)).ToListAsync();
        Assert.Equal(4, audits.Count);
        Assert.All(audits.Where(a => a["actor"] == team.Creator["_id"]), a => Assert.Equal("team-member-test", a["request_id"].AsString));
        Assert.Equal(4, (await ReloadTeamAsync(team)).Integer("m_uc"));
    }

    [Fact]
    public async Task BaseHierarchyAndSiteAdminStatusCannotBeBypassed()
    {
        var team = await TeamAsync();
        using var plain = Client(team.Plain);
        using var admin = Client(team.Admin);
        using var creator = Client(team.Creator);
        var memberPath = $"{team.Path}/members/{team.PlainMember["_id"]}";
        await ErrorAsync(await plain.PatchAsJsonAsync(memberPath, new { expected_version = 0, worker_qualifications = new[] { "proofreader" } }), 103);
        await ErrorAsync(await admin.PatchAsJsonAsync(memberPath, new { expected_version = 0, base_tag = "admin" }), 103);
        await ErrorAsync(await admin.PatchAsJsonAsync($"{team.Path}/members/{team.CreatorMember["_id"]}", new { expected_version = 0, tags = Array.Empty<string>() }), 103);
        await ErrorAsync(await creator.PatchAsJsonAsync($"{team.Path}/members/{team.CreatorMember["_id"]}", new { expected_version = 0, base_tag = "member" }), 103);
        await ErrorAsync(await creator.DeleteAsync($"{team.Path}/members/{team.CreatorMember["_id"]}"), 5116);
        await BodyAsync(await admin.PatchAsJsonAsync($"{team.Path}/members/{team.AdminMember["_id"]}", new { expected_version = 0, worker_qualifications = new[] { "translator" } }));
        await BodyAsync(await plain.PatchAsJsonAsync(memberPath + "/aliases", new { expected_version = 0, aliases = new[] { "Own alias" } }));
        await BodyAsync(await plain.DeleteAsync(memberPath));
        using var outsider = Client(await UserAsync(siteAdmin: true));
        await ErrorAsync(await outsider.GetAsync(team.Path + "/members"), 103);
        await ErrorAsync(await outsider.GetAsync(team.Path + "/identity-tag-policy"), 103);
        await ErrorAsync(await outsider.PostAsJsonAsync(team.Path + "/members", new { user_id = team.Plain["_id"].ToString() }), 103);
    }

    [Fact]
    public async Task ConcurrentAdmissionsRespectCapacityAndRepairStaleCounters()
    {
        var team = await TeamAsync(maximum: 5);
        await services.Store.Collection("team").UpdateOneAsync(new BsonDocument("_id", team.Document["_id"]), new BsonDocument("$set", new BsonDocument("m_uc", 999)));
        var candidates = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => UserAsync()));
        using var creator = Client(team.Creator);
        var responses = await Task.WhenAll(candidates.Select(user => creator.PostAsJsonAsync(team.Path + "/members", new { user_id = user["_id"].ToString() })));
        Assert.Equal(2, responses.Count(r => r.IsSuccessStatusCode));
        foreach (var response in responses)
        {
            if (response.IsSuccessStatusCode) await BodyAsync(response);
            else await ErrorAsync(response, 5107);
        }
        Assert.Equal(5, (await ReloadTeamAsync(team)).Integer("m_uc"));
        Assert.Equal(5, await services.Store.Collection("team_member").CountDocumentsAsync(new BsonDocument { ["team"] = team.Document["_id"], ["status"] = "active" }));
    }

    [Fact]
    public async Task ConcurrentEditsOnlyApplyOneVersionAndAudit()
    {
        var team = await TeamAsync();
        using var creator = Client(team.Creator);
        var path = $"{team.Path}/members/{team.PlainMember["_id"]}";
        var responses = await Task.WhenAll(new[] { "translator", "proofreader" }.Select(tag =>
            creator.PatchAsJsonAsync(path, new { expected_version = 0, worker_qualifications = new[] { tag } })));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        foreach (var response in responses)
            if (response.IsSuccessStatusCode) await BodyAsync(response); else await ErrorAsync(response, 5115);
        var member = await MemberAsync(team.PlainMember["_id"].ToString()!);
        Assert.Equal(1, member.Integer("version"));
        Assert.Single(member["worker_qualifications"].AsBsonArray);
        Assert.Equal(1, await services.Store.Collection("identity_audit_event").CountDocumentsAsync(new BsonDocument("member_id", member["_id"].ToString())));
    }

    [Fact]
    public async Task CrossTeamMemberIdsAndUnknownUsersAreRejected()
    {
        var team = await TeamAsync();
        var other = await TeamAsync();
        using var creator = Client(team.Creator);
        await ErrorAsync(await creator.PatchAsJsonAsync($"{team.Path}/members/{other.PlainMember["_id"]}", new { expected_version = 0, tags = Array.Empty<string>() }), 5104);
        await ErrorAsync(await creator.DeleteAsync($"{team.Path}/members/{other.PlainMember["_id"]}"), 5104);
        await ErrorAsync(await creator.PostAsJsonAsync(team.Path + "/members", new { user_id = ObjectId.GenerateNewId().ToString() }), 5105);
        await ErrorAsync(await creator.PostAsJsonAsync(team.Path + "/members", new { user_id = "invalid-id" }), 5105);
        Assert.Equal(0, (await MemberAsync(other.PlainMember["_id"].ToString()!)).Integer("version"));
    }

    [Fact]
    public async Task AliasesSearchAndPaginationKeepUserFieldsPrivate()
    {
        var team = await TeamAsync();
        var alpha = await UserAsync("Alpha" + ObjectId.GenerateNewId().ToString()[20..]);
        var beta = await UserAsync("beta" + ObjectId.GenerateNewId().ToString()[20..]);
        var first = await SeedMemberAsync(team.Document, alpha);
        var second = await SeedMemberAsync(team.Document, beta);
        using var firstUser = Client(alpha);
        await BodyAsync(await firstUser.PatchAsJsonAsync($"{team.Path}/members/{first["_id"]}/aliases", new
        { expected_version = 0, aliases = new[] { "  Straße ", "STRASSE", "é", "é" } }));
        await services.Store.Collection("user").UpdateOneAsync(new BsonDocument("_id", beta["_id"]), new BsonDocument("$set", new BsonDocument
        { ["as"] = new BsonArray { "Straße" }, ["asrch"] = new BsonArray { "strasse" } }));
        using var creator = Client(team.Creator);
        using var pageOne = await creator.GetAsync(team.Path + "/members?word=STRASSE&limit=1&page=1");
        Assert.Equal("2", pageOne.Headers.GetValues("X-Pagination-Count").Single());
        var one = await pageOne.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first["_id"].ToString(), one[0].GetProperty("id").GetString());
        Assert.Equal(2, one[0].GetProperty("aliases").GetArrayLength());
        Assert.False(one[0].GetProperty("user").TryGetProperty("email", out _));
        Assert.False(one[0].GetProperty("user").TryGetProperty("p", out _));
        using var pageTwo = await creator.GetAsync(team.Path + "/members?word=STRASSE&limit=1&page=2");
        var two = await pageTwo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(second["_id"].ToString(), two[0].GetProperty("id").GetString());
        await BodyAsync(await creator.DeleteAsync($"{team.Path}/members/{second["_id"]}"));
        using var removedPage = await creator.GetAsync(team.Path + "/members?status=removed&word=strasse");
        Assert.Equal("1", removedPage.Headers.GetValues("X-Pagination-Count").Single());
        var removed = await removedPage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("removed", removed[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task DisplayNameSyncOnlyTouchesActiveMembersInThisTeamAndPreservesVersions()
    {
        var team = await TeamAsync();
        var other = await TeamAsync();
        await services.Store.Collection("user").UpdateOneAsync(new BsonDocument("_id", team.Plain["_id"]), new BsonDocument("$set", new BsonDocument("ddn", "Site alias")));
        var localProject = await ProjectAsync(team.Document);
        var outsideProject = await ProjectAsync(other.Document);
        var active = await ProjectMemberAsync(localProject, team.Plain, "active");
        var invited = await ProjectMemberAsync(localProject, team.Plain, "invited");
        var outside = await ProjectMemberAsync(outsideProject, team.Plain, "active");
        using var own = Client(team.Plain);
        var path = $"{team.Path}/members/{team.PlainMember["_id"]}/default-display-name";
        var name = string.Concat(Enumerable.Repeat("😀", 140));
        await BodyAsync(await own.PatchAsJsonAsync(path, new { expected_version = 0, default_display_name = name, sync_to_projects = true }));
        var first = await services.Store.Collection("project_member").Find(new BsonDocument("_id", active["_id"])).SingleAsync();
        Assert.Equal(name, first["display_name"].AsString);
        Assert.Equal(SearchText.Normalize(name), first["dns"].AsString);
        Assert.Equal(1, first.Integer("version"));
        await BodyAsync(await own.PatchAsJsonAsync(path, new { expected_version = 1, default_display_name = "", sync_to_projects = true }));
        var fallback = await services.Store.Collection("project_member").Find(new BsonDocument("_id", active["_id"])).SingleAsync();
        Assert.Equal("Site alias", fallback["display_name"].AsString);
        Assert.Equal(2, fallback.Integer("version"));
        foreach (var untouched in new[] { invited, outside })
        {
            var row = await services.Store.Collection("project_member").Find(new BsonDocument("_id", untouched["_id"])).SingleAsync();
            Assert.Equal("Original", row["display_name"].AsString);
            Assert.Equal(0, row.Integer("version"));
        }
        await ErrorAsync(await own.PatchAsJsonAsync(path, new { expected_version = 2, default_display_name = name + "😀" }), 5100);
        Assert.Equal(2, (await MemberAsync(team.PlainMember["_id"].ToString()!)).Integer("version"));
    }

    [Theory]
    [InlineData("null", 5100)]
    [InlineData("[]", 5100)]
    [InlineData("{\"expected_version\":\"0\",\"tags\":[]}", 5100)]
    [InlineData("{\"expected_version\":0,\"tags\":[7]}", 5100)]
    [InlineData("{\"expected_version\":0,\"worker_qualifications\":[\"admin\"]}", 5101)]
    [InlineData("{\"expected_version\":0,\"base_tag\":\"creator\"}", 5116)]
    public async Task InvalidMemberRequestsDoNotPartiallyChangeData(string body, int error)
    {
        var team = await TeamAsync();
        using var creator = Client(team.Creator);
        await ErrorAsync(await creator.PatchAsync($"{team.Path}/members/{team.PlainMember["_id"]}", new StringContent(body, Encoding.UTF8, "application/json")), error);
        var row = await MemberAsync(team.PlainMember["_id"].ToString()!);
        Assert.Equal(0, row.Integer("version"));
        Assert.Equal("member", row["base_tag"].AsString);
        Assert.Empty(row["worker_qualifications"].AsBsonArray);
    }

    [Fact]
    public async Task PolicyDefaultsCustomTagsAndOverridesFeedLivePermissions()
    {
        var team = await TeamAsync();
        using var creator = Client(team.Creator);
        var path = team.Path + "/identity-tag-policy";
        var initial = await BodyAsync(await creator.GetAsync(path));
        Assert.False(initial.GetProperty("team_tags").GetProperty("creator").GetProperty("assignable").GetBoolean());
        Assert.False(initial.GetProperty("project_tags").GetProperty("creator").GetProperty("assignable").GetBoolean());
        var changed = await BodyAsync(await creator.PatchAsJsonAsync(path, new
        {
            expected_version = 0,
            upserts = new[] {
            new { scope = "team", code = "reporter", name = "Reporter", permissions = new[] { "team:ACCESS", "team:INSIGHT" } },
            new { scope = "project", code = "translator", name = "translator", permissions = new[] { "project:ACCESS" } } }
        }));
        Assert.Equal("team_override", changed.GetProperty("project_tags").GetProperty("translator").GetProperty("source").GetString());
        Assert.Contains(changed.GetProperty("project_tags").GetProperty("translator").GetProperty("initial_permissions").EnumerateArray(), p => p.GetString() == "project:ADD_TRA");
        await BodyAsync(await creator.PatchAsJsonAsync($"{team.Path}/members/{team.PlainMember["_id"]}", new { expected_version = 0, tags = new[] { "reporter" } }));
        using var scope = services.App.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IdentityPermissions>().TeamSnapshotAsync(team.Plain, team.Document);
        Assert.True(snapshot.Has("team:INSIGHT"));
        Assert.False(snapshot.Has("team:DELETE"));
        var audit = await services.Store.Collection("identity_audit_event").Find(new BsonDocument { ["team_id"] = team.Document["_id"].ToString(), ["action"] = "identity_tag_policy_update" }).SingleAsync();
        Assert.Equal(0, audit["before"]["version"].ToInt32());
        Assert.Equal(1, audit["after"]["version"].ToInt32());
    }

    [Fact]
    public async Task OnlyCreatorMayChangeTheTeamBaseHierarchy()
    {
        var team = await TeamAsync();
        using var admin = Client(team.Admin);
        using var creator = Client(team.Creator);
        var path = team.Path + "/identity-tag-policy";
        await ErrorAsync(await admin.PatchAsJsonAsync(path, new
        {
            expected_version = 0,
            upserts = new[] {
            new { scope = "team", code = "member", permissions = new[] { "team:ACCESS", "team:DELETE" } } }
        }), 103);
        await BodyAsync(await creator.PatchAsJsonAsync(path, new
        {
            expected_version = 0,
            upserts = new[] {
            new { scope = "team", code = "member", permissions = new[] { "team:ACCESS" } } }
        }));
        await ErrorAsync(await admin.PatchAsJsonAsync(path, new { expected_version = 1, removes = new[] { new { scope = "team", code = "member" } } }), 103);
        var reset = await BodyAsync(await creator.PatchAsJsonAsync(path, new { expected_version = 1, removes = new[] { new { scope = "team", code = "member" } } }));
        Assert.Equal("site", reset.GetProperty("team_tags").GetProperty("member").GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("{\"scope\":\"project\",\"code\":\"custom\",\"permissions\":[\"project:MANAGE_MEMBERS\"]}", 5116)]
    [InlineData("{\"scope\":\"team\",\"code\":\"custom\",\"permissions\":[\"project:ACCESS\"]}", 5101)]
    [InlineData("{\"scope\":\"project\",\"code\":\"translator\",\"name\":\"renamed\"}", 5116)]
    [InlineData("{\"scope\":\"team\",\"code\":\"BAD.code\"}", 5101)]
    [InlineData("{\"scope\":\"project\",\"code\":\"custom\",\"permissions\":[1]}", 5100)]
    public async Task InvalidPolicyDefinitionsNeverPartiallyCommit(string definition, int error)
    {
        var team = await TeamAsync();
        using var creator = Client(team.Creator);
        var body = "{\"expected_version\":0,\"upserts\":[{\"scope\":\"team\",\"code\":\"valid_first\"}," + definition + "]}";
        await ErrorAsync(await creator.PatchAsync(team.Path + "/identity-tag-policy", new StringContent(body, Encoding.UTF8, "application/json")), error);
        var policy = await services.Store.Collection("identity_tag_policy").Find(new BsonDocument("team", team.Document["_id"])).SingleAsync();
        Assert.Equal(0, policy.Integer("version"));
        Assert.Empty(policy["team_tags"].AsBsonDocument);
    }

    [Fact]
    public async Task PolicyCannotDeleteTagsUsedByRemovedMembersOrOtherProjectRecords()
    {
        var team = await TeamAsync();
        using var creator = Client(team.Creator);
        var path = team.Path + "/identity-tag-policy";
        await BodyAsync(await creator.PatchAsJsonAsync(path, new
        {
            expected_version = 0,
            upserts = new[] {
            new { scope = "team", code = "reporter", permissions = new[] { "team:ACCESS" } },
            new { scope = "project", code = "captioner", permissions = new[] { "project:ACCESS" } } }
        }));
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", team.PlainMember["_id"]), new BsonDocument("$set", new BsonDocument
        { ["status"] = "removed", ["tags"] = new BsonArray { "reporter" } }));
        var project = await ProjectAsync(team.Document);
        var member = await ProjectMemberAsync(project, team.Plain, "removed", "captioner");
        await ErrorAsync(await creator.PatchAsJsonAsync(path, new { expected_version = 1, removes = new[] { new { scope = "team", code = "reporter" } } }), 5117);
        await ErrorAsync(await creator.PatchAsJsonAsync(path, new { expected_version = 1, removes = new[] { new { scope = "project", code = "captioner" } } }), 5117);
        await services.Store.Collection("team_member").UpdateOneAsync(new BsonDocument("_id", team.PlainMember["_id"]), new BsonDocument("$set", new BsonDocument("tags", new BsonArray())));
        await services.Store.Collection("project_member").UpdateOneAsync(new BsonDocument("_id", member["_id"]), new BsonDocument("$set", new BsonDocument("tags", new BsonArray())));
        var cleared = await BodyAsync(await creator.PatchAsJsonAsync(path, new
        {
            expected_version = 1,
            removes = new[] {
            new { scope = "team", code = "reporter" }, new { scope = "project", code = "captioner" } }
        }));
        Assert.False(cleared.GetProperty("team_tags").TryGetProperty("reporter", out _));
        Assert.False(cleared.GetProperty("project_tags").TryGetProperty("captioner", out _));
    }

    [Fact]
    public async Task ConcurrentPolicyUpdatesAreVersionFenced()
    {
        var team = await TeamAsync();
        using var creator = Client(team.Creator);
        var responses = await Task.WhenAll(new[] { "first_tag", "second_tag" }.Select(code => creator.PatchAsJsonAsync(team.Path + "/identity-tag-policy",
            new { expected_version = 0, upserts = new[] { new { scope = "team", code } } })));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        foreach (var response in responses)
            if (response.IsSuccessStatusCode) await BodyAsync(response); else await ErrorAsync(response, 5115);
        var policy = await services.Store.Collection("identity_tag_policy").Find(new BsonDocument("team", team.Document["_id"])).SingleAsync();
        Assert.Equal(1, policy.Integer("version"));
        Assert.Single(policy["team_tags"].AsBsonDocument);
    }

    private async Task<Team> TeamAsync(int maximum = 100)
    {
        var document = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["u"] = maximum, ["m_uc"] = 3, ["wqm"] = "qualified" };
        await services.Store.Collection("team").InsertOneAsync(document);
        var creator = await UserAsync();
        var admin = await UserAsync();
        var plain = await UserAsync();
        return new(document, creator, admin, plain, await SeedMemberAsync(document, creator, "creator"),
            await SeedMemberAsync(document, admin, "admin"), await SeedMemberAsync(document, plain));
    }

    private async Task<BsonDocument> UserAsync(string? name = null, bool siteAdmin = false)
    {
        var id = ObjectId.GenerateNewId();
        name ??= "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["e"] = id + "@example.test",
            ["p"] = Password,
            ["l"] = "en",
            ["s"] = "",
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["admin"] = siteAdmin
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> SeedMemberAsync(BsonDocument team, BsonDocument user, string baseTag = "member")
    {
        var now = DateTime.UtcNow;
        var member = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["aliases"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["default_display_name"] = "",
            ["version"] = 0,
            ["status"] = "active",
            ["create_time"] = now,
            ["edit_time"] = now
        };
        await services.Store.Collection("team_member").InsertOneAsync(member);
        return member;
    }

    private async Task<BsonDocument> ProjectAsync(BsonDocument team)
    {
        var project = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["t"] = team["_id"], ["st"] = 0 };
        await services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task<BsonDocument> ProjectMemberAsync(BsonDocument project, BsonDocument user, string status, string tag = "translator")
    {
        var member = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = user["_id"],
            ["status"] = status,
            ["tags"] = new BsonArray { tag },
            ["version"] = 0,
            ["display_name"] = "Original",
            ["dns"] = "original"
        };
        await services.Store.Collection("project_member").InsertOneAsync(member);
        return member;
    }

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        var token = services.App.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].ToString()!, user["p"].AsString);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<BsonDocument> ReloadTeamAsync(Team team) => services.Store.Collection("team").Find(new BsonDocument("_id", team.Document["_id"])).SingleAsync();
    private Task<BsonDocument> MemberAsync(string id) => services.Store.Collection("team_member").Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(response.IsSuccessStatusCode, body.ToString());
            return body;
        }
    }

    private static async Task ErrorAsync(HttpResponseMessage response, int code)
    {
        using (response)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(code, body.GetProperty("code").GetInt32());
            if (code is >= 5100 and <= 5118) Assert.True(body.TryGetProperty("identity_code", out _));
        }
    }
}
