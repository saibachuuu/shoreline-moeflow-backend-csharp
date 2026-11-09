using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class TeamTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task CreationWritesCompatibleTeamDefaultSetAndCreatorWithRecoverableIntent()
    {
        var actor = await UserAsync();
        var name = Name();
        using var client = Client(actor);
        client.DefaultRequestHeaders.Add("X-Request-ID", "team-creation-test");
        var body = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name,
            intro = "介绍😀",
            allow_apply_type = "2",
            application_check_type = "1",
            default_role = await DefaultRoleAsync()
        }));
        var team = body.GetProperty("team");
        var id = ObjectId.Parse(team.GetProperty("id").GetString());
        Assert.Equal(name, team.GetProperty("name").GetString());
        Assert.Equal("creator", team.GetProperty("base_tag").GetString());
        Assert.Equal("creator", team.GetProperty("role").GetProperty("system_code").GetString());
        Assert.Equal(1, team.GetProperty("user_count").GetInt32());
        Assert.Equal(100000, team.GetProperty("max_user").GetInt32());
        Assert.Equal("qualified", team.GetProperty("worker_qualification_mode").GetString());
        Assert.False(team.GetProperty("is_need_check_application").GetBoolean());
        Assert.True(team.GetProperty("staff_list_page").ValueKind == JsonValueKind.Null);
        Assert.Contains("team:DELETE", team.GetProperty("effective_permissions").EnumerateArray().Select(value => value.GetString()));
        var stored = await TeamAsync(id);
        Assert.Equal(2, stored.Integer("m_a"));
        Assert.Equal(1, stored.Integer("m_t"));
        Assert.True(stored["dr"].IsObjectId);
        Assert.True(stored["m_c"].IsBsonDateTime);
        var projectSet = Assert.Single(await services.Store.Collection("project_set").Find(new BsonDocument("t", id)).ToListAsync());
        Assert.True(projectSet.Flag("d"));
        Assert.Equal("default", projectSet.Text("n"));
        Assert.True(projectSet["ct"].IsBsonDateTime);
        var member = Assert.Single(await Members(id).ToListAsync());
        Assert.Equal(actor["_id"], member["user"]);
        Assert.Equal("creator", member.Text("base_tag"));
        Assert.Equal(0, member.Integer("version"));
        var journal = await services.Store.Collection("csharp_job").Find(new BsonDocument("_id", id)).FirstAsync();
        Assert.True(journal.Flag("creation_done"));
        Assert.False(journal.Flag("cancellable"));
        Assert.False(journal.Contains("reserved_name"));
        Assert.Equal("team-creation-test", journal["payload"].AsBsonDocument.Text("request_id"));
        using var scope = services.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TeamService>().ResumeCreationAsync(journal["payload"].AsBsonDocument, CancellationToken.None);
        Assert.Equal(1, await Members(id).CountDocumentsAsync());
        Assert.Equal(1, await services.Store.Collection("project_set").CountDocumentsAsync(new BsonDocument("t", id)));
    }

    [Fact]
    public async Task ConcurrentCaseVariantNamesHaveExactlyOneCreation()
    {
        var first = await UserAsync();
        var second = await UserAsync();
        using var one = Client(first);
        using var two = Client(second);
        var name = Name();
        var role = await DefaultRoleAsync();
        var responses = await Task.WhenAll(one.PostAsJsonAsync("/v1/teams", Form(name, role)),
            two.PostAsJsonAsync("/v1/teams", Form(name.ToUpperInvariant(), role)));
        Assert.Single(responses, response => response.IsSuccessStatusCode);
        foreach (var response in responses)
            if (response.IsSuccessStatusCode) await BodyAsync(response); else await ErrorAsync(response, 2);
        Assert.Equal(1, await services.Store.Collection("team").CountDocumentsAsync(new BsonDocument("n", new BsonRegularExpression("^" + name + "$", "i"))));
        Assert.Equal(1, await services.Store.Collection("csharp_job").CountDocumentsAsync(new BsonDocument("payload.team.n", new BsonRegularExpression("^" + name + "$", "i"))));
    }

    [Fact]
    public async Task SiteCreationPolicyAndTeamPermissionsStaySeparate()
    {
        var ordinary = await UserAsync(admin: false);
        using var plain = Client(ordinary);
        var name = Name();
        await services.Store.Collection("site_setting").UpdateOneAsync(new BsonDocument("n", "site"),
            new BsonDocument("$set", new BsonDocument("oacg", true)), new UpdateOptions { IsUpsert = true });
        await ErrorAsync(await plain.PostAsJsonAsync("/v1/teams", Form(name, await DefaultRoleAsync())), 3008);
        var creator = await UserAsync();
        using var owner = Client(creator);
        var created = await BodyAsync(await owner.PostAsJsonAsync("/v1/teams", Form(name, await DefaultRoleAsync())));
        var id = ObjectId.Parse(created.GetProperty("team").GetProperty("id").GetString());
        await MemberAsync(id, ordinary);
        var admin = await UserAsync();
        await MemberAsync(id, admin, "admin");
        using var manager = Client(admin);
        var path = "/v1/teams/" + id;
        await BodyAsync(await plain.GetAsync(path));
        await ErrorAsync(await plain.PutAsJsonAsync(path, new { intro = "not allowed" }), 103);
        await BodyAsync(await manager.PutAsJsonAsync(path, new { intro = "manager edit" }));
        await ErrorAsync(await manager.PutAsJsonAsync(path, new { intro = "must remain unchanged", worker_qualification_mode = "open" }), 103);
        await ErrorAsync(await manager.PutAsJsonAsync(path, new { proofread_marker_enabled = false }), 103);
        await ErrorAsync(await manager.PutAsJsonAsync(path, new { proofread_marker_opacity = 10 }), 103);
        Assert.Equal("manager edit", (await TeamAsync(id)).Text("i"));
        using var outsider = Client(await UserAsync());
        await ErrorAsync(await outsider.GetAsync(path), 103);
        await ErrorAsync(await outsider.PutAsJsonAsync(path, new { intro = "site admin is not a team manager" }), 103);
    }

    [Fact]
    public async Task AllSettingsAreValidatedBeforeWritingAndLegacyNamesArePreserved()
    {
        var actor = await UserAsync();
        using var client = Client(actor);
        var role = await DefaultRoleAsync();
        var name = Name();
        await ErrorAsync(await client.PostAsJsonAsync("/v1/teams", new
        {
            name,
            intro = string.Concat(Enumerable.Repeat("😀", 141)),
            allow_apply_type = 1,
            application_check_type = 2,
            default_role = role
        }), 2);
        await ErrorAsync(await client.PostAsJsonAsync("/v1/teams", new { name, intro = "", allow_apply_type = 0, application_check_type = 2, default_role = role }), 2);
        var creatorRole = await services.Store.Collection("team_role").Find(new BsonDocument("m_o", "creator")).FirstAsync();
        await ErrorAsync(await client.PostAsJsonAsync("/v1/teams", Form(name, creatorRole["_id"].ToString()!)), 2);
        Assert.False(await services.Store.Collection("team").Find(new BsonDocument("n", name)).AnyAsync());
        var created = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", Form(name, role)));
        var id = ObjectId.Parse(created.GetProperty("team").GetProperty("id").GetString());
        var path = "/v1/teams/" + id;
        await ErrorAsync(await client.PutAsJsonAsync(path, new { intro = "partial edit", proofread_marker_opacity = 101 }), 2);
        await ErrorAsync(await client.PutAsJsonAsync(path, new { staff_list_page = 0 }), 2);
        Assert.Equal("", (await TeamAsync(id)).Text("i"));
        var updated = await BodyAsync(await client.PutAsJsonAsync(path, new
        {
            worker_qualification_mode = "open",
            staff_list_page = -2,
            proofread_marker_enabled = "false",
            proofread_marker_opacity = 100.9,
            allow_apply_type = 2,
            application_check_type = 1
        }));
        var view = updated.GetProperty("team");
        Assert.Equal(-2, view.GetProperty("staff_list_page").GetInt32());
        Assert.False(view.GetProperty("proofread_marker_enabled").GetBoolean());
        Assert.Equal(100, view.GetProperty("proofread_marker_opacity").GetInt32());
        Assert.False(view.GetProperty("is_need_check_application").GetBoolean());
        await ErrorAsync(await client.PutAsJsonAsync(path, new { name = name.ToUpperInvariant() }), 2);
        var legacyName = "legacy_" + ObjectId.GenerateNewId();
        await services.Store.Collection("team").UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("n", legacyName)));
        await BodyAsync(await client.PutAsJsonAsync(path, new { name = legacyName, intro = "legacy name survives" }));
        Assert.Equal(legacyName, (await TeamAsync(id)).Text("n"));
        await ErrorAsync(await client.PutAsJsonAsync(path, new { ignored_field = true }), 110);
    }

    [Fact]
    public async Task ArchiveKeyUpdatesEncryptLegacyValuesAndNeverReturnFullKeys()
    {
        var actor = await UserAsync();
        using var client = Client(actor);
        var created = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", Form(Name(), await DefaultRoleAsync())));
        var id = ObjectId.Parse(created.GetProperty("team").GetProperty("id").GetString());
        var path = "/v1/teams/" + id;
        await services.Store.Collection("team").UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("aak",
            new BsonArray { new BsonDocument { ["id"] = "legacy", ["key"] = "local-legacy-key-WXYZ", ["remark"] = "old", ["enabled"] = false } })));
        var response = await BodyAsync(await client.PutAsJsonAsync(path, new
        {
            archive_api_keys = new object[] {
            new { id = "legacy", key = (string?)null, remark = "retained" }, new { key = "local-new-key-ABCD", enabled = true } },
            archive_api_url = "https://child.api.example.test/base///"
        }));
        var keys = response.GetProperty("team").GetProperty("archive_api_keys");
        Assert.Equal(2, keys.GetArrayLength());
        Assert.Equal("WXYZ", keys[0].GetProperty("key_tail").GetString());
        Assert.False(keys[0].GetProperty("enabled").GetBoolean());
        Assert.Equal("ABCD", keys[1].GetProperty("key_tail").GetString());
        Assert.All(keys.EnumerateArray(), key => Assert.False(key.TryGetProperty("key", out _)));
        Assert.DoesNotContain("local-new-key", response.GetRawText());
        Assert.Equal("https://child.api.example.test/base", response.GetProperty("team").GetProperty("archive_api_url").GetString());
        using var scope = services.App.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ArchiveSecrets>();
        var stored = (await TeamAsync(id))["aak"].AsBsonArray;
        Assert.StartsWith("fernet:", stored[0].AsBsonDocument.Text("key"));
        Assert.Equal("local-legacy-key-WXYZ", secrets.Decrypt(stored[0].AsBsonDocument.Text("key")));
        await ErrorAsync(await client.PutAsJsonAsync(path, new { intro = "must not change", archive_api_keys = new[] { new { id = "unknown" } } }), 2);
        await ErrorAsync(await client.PutAsJsonAsync(path, new { intro = "must not change", archive_api_url = "https://127.0.0.1/" }), 2);
        Assert.Equal("", (await TeamAsync(id)).Text("i"));
        await BodyAsync(await client.PutAsJsonAsync(path, new { archive_api_keys = new[] { new { id = "legacy", enabled = false } } }));
        Assert.Single((await TeamAsync(id))["aak"].AsBsonArray);
        await BodyAsync(await client.PutAsJsonAsync(path, new { archive_api_keys = Array.Empty<object>(), archive_api_url = (string?)null }));
        Assert.Empty((await TeamAsync(id))["aak"].AsBsonArray);
        Assert.Equal("", (await TeamAsync(id)).Text("aau"));
    }

    [Fact]
    public async Task OwnTeamsUseCompactPagedProjectionAndDiscoveryStaysTestOnly()
    {
        var creator = await UserAsync();
        var user = await UserAsync(admin: false);
        using var owner = Client(creator);
        using var client = Client(user);
        var prefix = Name();
        var ids = new List<ObjectId>();
        for (var index = 0; index < 3; index++)
        {
            var created = await BodyAsync(await owner.PostAsJsonAsync("/v1/teams", Form(prefix + index, await DefaultRoleAsync())));
            var id = ObjectId.Parse(created.GetProperty("team").GetProperty("id").GetString());
            ids.Add(id);
            await MemberAsync(id, user, status: index == 2 ? "removed" : "active");
        }
        using var first = await client.GetAsync($"/v1/user/teams?word={prefix}&page=1&limit=1");
        Assert.Equal("2", first.Headers.GetValues("X-Pagination-Count").Single());
        var pageOne = await BodyAsync(first);
        Assert.Equal(ids[0].ToString(), pageOne[0].GetProperty("id").GetString());
        Assert.Equal("member", pageOne[0].GetProperty("base_tag").GetString());
        Assert.False(pageOne[0].TryGetProperty("archive_api_keys", out _));
        Assert.False(pageOne[0].TryGetProperty("user_count", out _));
        var pageTwo = await BodyAsync(await client.GetAsync($"/v1/user/teams?word={prefix}&page=2&limit=1"));
        Assert.Equal(ids[1].ToString(), pageTwo[0].GetProperty("id").GetString());
        using var scope = services.App.Services.CreateScope();
        var (rows, count) = await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync(user, prefix, new Pagination(1, 10), true, CancellationToken.None);
        Assert.Equal(2, count);
        Assert.All(rows, row => { Assert.False(row.Contains("aak")); Assert.False(row.Contains("dr")); });
        var all = await BodyAsync(await client.GetAsync("/v1/teams?word=" + prefix));
        Assert.Equal(3, all.GetArrayLength());
        Assert.Equal(2, all.EnumerateArray().Count(team => team.GetProperty("joined").GetBoolean()));
        Assert.All(all.EnumerateArray(), team => Assert.True(team.GetProperty("base_tag").ValueKind == JsonValueKind.Null));
        await ErrorAsync(await client.GetAsync("/v1/teams"), 110);
        var environment = services.App.Services.GetRequiredService<IWebHostEnvironment>();
        var original = environment.EnvironmentName;
        try
        {
            environment.EnvironmentName = "Production";
            using var response = await client.GetAsync("/v1/teams?word=" + prefix);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        }
        finally { environment.EnvironmentName = original; }
    }

    [Fact]
    public async Task PartialCreationReplaysSameIntentWithoutReplacingExistingDefaultSet()
    {
        var actor = await UserAsync();
        var name = Name();
        var role = await DefaultRoleAsync();
        var intent = await IntentAsync(actor, name, role);
        var payload = intent["payload"].AsBsonDocument;
        var id = intent["_id"].AsObjectId;
        await services.Store.Collection("team").InsertOneAsync(payload["team"].AsBsonDocument);
        var projectSet = payload["project_set"].DeepClone().AsBsonDocument;
        projectSet["n"] = "existing default";
        await services.Store.Collection("project_set").InsertOneAsync(projectSet);
        using var client = Client(actor);
        var restored = await BodyAsync(await client.PostAsJsonAsync("/v1/teams", Form(name, role)));
        Assert.Equal(id.ToString(), restored.GetProperty("team").GetProperty("id").GetString());
        Assert.Equal("existing default", (await services.Store.Collection("project_set").Find(new BsonDocument("_id", projectSet["_id"])).FirstAsync()).Text("n"));
        var journal = await services.Store.Collection("csharp_job").Find(new BsonDocument("_id", id)).FirstAsync();
        Assert.True(journal.Flag("creation_done"));
        Assert.Equal("done", journal.Text("state"));
        Assert.Equal(1, await Members(id).CountDocumentsAsync());
        using var scope = services.App.Services.CreateScope();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scope.ServiceProvider.GetRequiredService<TeamService>().ResumeCreationAsync(payload, CancellationToken.None)));
        Assert.Equal(1, await Members(id).CountDocumentsAsync());
        Assert.Equal(1, await services.Store.Collection("project_set").CountDocumentsAsync(new BsonDocument("t", id)));
    }

    [Fact]
    public async Task PendingCriticalIntentCannotBeCancelledAndWorkerCanResumeFromNoRows()
    {
        var actor = await UserAsync();
        var intent = await IntentAsync(actor, Name(), await DefaultRoleAsync(), "pending");
        var id = intent["_id"].AsObjectId;
        using var scope = services.App.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        await queue.CancelPendingAsync(id, CancellationToken.None);
        Assert.Equal("pending", (await services.Store.Collection("csharp_job").Find(new BsonDocument("_id", id)).FirstAsync()).Text("state"));
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().Single(candidate => candidate.Kind == "team-create");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => handler.ExecuteAsync(intent["payload"].AsBsonDocument, CancellationToken.None)));
        Assert.Equal(1, (await TeamAsync(id)).Integer("m_uc"));
        Assert.Equal(1, await Members(id).CountDocumentsAsync());
        Assert.Equal(1, await services.Store.Collection("project_set").CountDocumentsAsync(new BsonDocument("t", id)));
        Assert.True((await services.Store.Collection("csharp_job").Find(new BsonDocument("_id", id)).FirstAsync()).Flag("creation_done"));
    }

    private static string Name() => "team_" + ObjectId.GenerateNewId().ToString()[12..];
    private static object Form(string name, string role) => new { name, intro = "", allow_apply_type = 1, application_check_type = 2, default_role = role };
    private async Task<string> DefaultRoleAsync() => (await services.Store.Collection("team_role").Find(new BsonDocument("m_o", "member")).FirstAsync())["_id"].ToString()!;
    private IFindFluent<BsonDocument, BsonDocument> Members(ObjectId id) => services.Store.Collection("team_member").Find(new BsonDocument("team", id));
    private Task<BsonDocument> TeamAsync(ObjectId id) => services.Store.Collection("team").Find(new BsonDocument("_id", id)).FirstAsync();
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
    private async Task<BsonDocument> IntentAsync(BsonDocument user, string name, string role, string state = "failed")
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        var original = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["i"] = "",
            ["a"] = "",
            ["m_a"] = 1L,
            ["m_t"] = 2L,
            ["dr"] = ObjectId.Parse(role),
            ["u"] = 100000,
            ["m_uc"] = 0,
            ["m_c"] = now,
            ["m_e"] = now,
            ["wqm"] = "qualified",
            ["aak"] = new BsonArray()
        };
        var payload = new BsonDocument
        {
            ["creator"] = user["_id"],
            ["team"] = original,
            ["request_id"] = "recovery-test",
            ["project_set"] = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["n"] = "default", ["i"] = "", ["t"] = id, ["d"] = true, ["ct"] = now, ["et"] = now }
        };
        var journal = new BsonDocument
        {
            ["_id"] = id,
            ["kind"] = "team-create",
            ["payload"] = payload,
            ["reserved_name"] = "team:" + SearchText.Normalize(name),
            ["creation_done"] = false,
            ["cancellable"] = false,
            ["state"] = state,
            ["attempts"] = 0,
            ["available_at"] = now.AddYears(20),
            ["created_at"] = now
        };
        await services.Store.Collection("csharp_job").InsertOneAsync(journal);
        return journal;
    }
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }
    private static async Task ErrorAsync(HttpResponseMessage response, int code)
    {
        using (response)
        {
            Assert.False(response.IsSuccessStatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.GetProperty("code").GetInt32());
        }
    }
}
