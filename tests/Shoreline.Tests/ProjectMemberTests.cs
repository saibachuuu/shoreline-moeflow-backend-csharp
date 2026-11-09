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
public sealed class ProjectMemberTests(LocalServices services)
{
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public async Task ChangesAddIsIdempotentAndDuplicateAddConflicts()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString()!;
        var member = await UserAsync();
        await MemberAsync(teamId, member, "member", ["translator"]);

        var payload = new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", user_id = member["_id"].ToString()!, tags = new[] { "translator" } }
            }
        };
        var first = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", payload));
        Assert.False(first.GetProperty("stopped").GetBoolean());
        var result = Assert.Single(first.GetProperty("results").EnumerateArray());
        Assert.Equal("succeeded", result.GetProperty("status").GetString());
        Assert.Equal("active", result.GetProperty("member_status").GetString());
        // Replaying the same operation_id returns the recorded result (no double insert).
        var replay = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", payload));
        Assert.Equal(result.GetProperty("member_id").GetString(), Assert.Single(replay.GetProperty("results").EnumerateArray()).GetProperty("member_id").GetString());
        var stored = await ProjectAsync(ObjectId.Parse(projectId));
        Assert.Equal(2, stored.Integer("m_uc"));
        var rows = await services.Store.Collection("project_member").Find(new BsonDocument("project", stored["_id"])).ToListAsync();
        Assert.Equal(2, rows.Count);

        // Adding the same active user with a new operation id conflicts.
        var duplicate = new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", user_id = member["_id"].ToString()!, tags = Array.Empty<string>() }
            }
        };
        var duplicateResponse = await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", duplicate);
        Assert.False(duplicateResponse.IsSuccessStatusCode);
        var failed = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(failed.GetProperty("stopped").GetBoolean());
        Assert.Equal(5106, failed.GetProperty("failed").GetProperty("error_code").GetInt32());
    }

    [Fact]
    public async Task UpdateRemoveAndRestoreUseVersionConflicts()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString()!;
        var member = await UserAsync();
        await MemberAsync(teamId, member, "member", ["translator", "proofreader"]);
        var added = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", user_id = member["_id"].ToString()!, tags = new[] { "translator" } }
            }
        }));
        var result = Assert.Single(added.GetProperty("results").EnumerateArray());
        var memberId = result.GetProperty("member_id").GetString()!;
        var version = result.GetProperty("member_version").GetInt32();

        var conflictResponse = await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "update", member_id = memberId, expected_member_version = version + 42, changes = new { display_name = "new name" } }
            }
        });
        Assert.False(conflictResponse.IsSuccessStatusCode);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5108, conflict.GetProperty("failed").GetProperty("error_code").GetInt32());

        var updated = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "update", member_id = memberId, expected_member_version = version, changes = new { tags = new[] { "proofreader" } } }
            }
        }));
        var updateResult = Assert.Single(updated.GetProperty("results").EnumerateArray());
        Assert.Equal(version + 1, updateResult.GetProperty("member_version").GetInt32());

        var removed = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "update", member_id = memberId, expected_member_version = version + 1, changes = new { status = "removed" } }
            }
        }));
        Assert.Equal("removed", Assert.Single(removed.GetProperty("results").EnumerateArray()).GetProperty("member_status").GetString());

        var restored = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", member_id = memberId, expected_member_version = version + 2, tags = new[] { "translator" } }
            }
        }));
        Assert.Equal("active", Assert.Single(restored.GetProperty("results").EnumerateArray()).GetProperty("member_status").GetString());

        var list = await BodyAsync(await owner.GetAsync($"/v1/projects/{projectId}/members"));
        Assert.Single(list.EnumerateArray(), row => row.GetProperty("id").GetString() == memberId);
    }

    [Fact]
    public async Task ExternalMemberBindRequiresMergeThenMergeAndHardDeleteWork()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString()!;
        var member = await UserAsync();
        await MemberAsync(teamId, member, "member", ["translator"]);
        var memberUserId = member["_id"].ToString()!;

        var external = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", display_name = "外聘译者", tags = Array.Empty<string>(), external_id = Name("ext") }
            }
        }));
        var externalId = Assert.Single(external.GetProperty("results").EnumerateArray()).GetProperty("member_id").GetString()!;
        var externalVersion = Assert.Single(external.GetProperty("results").EnumerateArray()).GetProperty("member_version").GetInt32();

        // First bind binds the external member to the user.
        var bound = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/{externalId}/bind",
            new { user_id = memberUserId, expected_version = externalVersion }));
        Assert.Equal(memberUserId, bound.GetProperty("member").GetProperty("user_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(bound.GetProperty("audit_event_id").GetString()));

        // A second external member for the same user auto-merges on bind
        // (Python semantics): tags union, display name adopted, alias deleted.
        var external2 = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", display_name = "外聘校对", tags = new[] { "translator" }, external_id = Name("ext") }
            }
        }));
        var external2Id = Assert.Single(external2.GetProperty("results").EnumerateArray()).GetProperty("member_id").GetString()!;
        var external2Version = Assert.Single(external2.GetProperty("results").EnumerateArray()).GetProperty("member_version").GetInt32();
        var autoMerged = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/{external2Id}/bind",
            new { user_id = memberUserId, expected_version = external2Version }));
        Assert.Equal(memberUserId, autoMerged.GetProperty("member").GetProperty("user_id").GetString());
        Assert.Equal("外聘校对", autoMerged.GetProperty("member").GetProperty("display_name").GetString());
        Assert.Contains("translator", autoMerged.GetProperty("member").GetProperty("tags").EnumerateArray().Select(v => v.GetString()));
        Assert.Empty(await services.Store.Collection("project_member").Find(new BsonDocument("_id", ObjectId.Parse(external2Id))).ToListAsync());

        var memberRow = await services.Store.Collection("project_member").Find(new BsonDocument("user", member["_id"])).FirstAsync();

        // Explicit merge: a fresh external source merges into the registered target.
        var externalX = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", display_name = "外聘摄影", tags = new[] { "translator" }, external_id = Name("ext") }
            }
        }));
        var externalXId = Assert.Single(externalX.GetProperty("results").EnumerateArray()).GetProperty("member_id").GetString()!;
        var externalXVersion = Assert.Single(externalX.GetProperty("results").EnumerateArray()).GetProperty("member_version").GetInt32();
        var merged = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/{externalXId}/merge", new
        {
            target_member_id = memberRow["_id"].ToString()!,
            expected_source_version = externalXVersion,
            expected_target_version = memberRow.Integer("version"),
            tags = new[] { "translator" }
        }));
        Assert.Equal(memberUserId, merged.GetProperty("member").GetProperty("user_id").GetString());
        Assert.Equal("removed", merged.GetProperty("source_member").GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(merged.GetProperty("audit_event_id").GetString()));

        // Hard-delete the remaining external-shaped row (create a fresh one).
        var external3 = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", display_name = "外聘清理", tags = Array.Empty<string>(), external_id = Name("ext") }
            }
        }));
        var external3Id = Assert.Single(external3.GetProperty("results").EnumerateArray()).GetProperty("member_id").GetString()!;
        var deleted = await BodyAsync(await owner.DeleteAsync($"/v1/projects/{projectId}/members/{external3Id}"));
        Assert.False(string.IsNullOrWhiteSpace(deleted.GetProperty("audit_event_id").GetString()));
        Assert.Empty(await services.Store.Collection("project_member").Find(new BsonDocument("_id", ObjectId.Parse(external3Id))).ToListAsync());
    }

    [Fact]
    public async Task NonManagerCannotAddAndPositionLockBlocksAssignedTag()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString()!;
        var stranger = await UserAsync();
        await MemberAsync(teamId, stranger, "member", ["translator"]);
        using var strangerClient = Client(stranger);

        // Ordinary team member is not a project manager: external adds with
        // tags are manager-only (empty tags are allowed without tags).
        var strangerResponse = await strangerClient.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", display_name = "外聘", tags = new[] { "scanner" }, external_id = Name("ext") }
            }
        });
        Assert.False(strangerResponse.IsSuccessStatusCode);
        var strangerBody = await strangerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(strangerBody.GetProperty("stopped").GetBoolean());
        Assert.Equal(103, strangerBody.GetProperty("failed").GetProperty("error_code").GetInt32());

        // Position lock: translator is locked to the team creator; a team
        // admin (manager but not creator) cannot assign it.
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(projectId)),
            new BsonDocument("$set", new BsonDocument { ["ple"] = true, ["plt"] = new BsonArray { "translator" } }));
        var admin = await UserAsync();
        await MemberAsync(teamId, admin, "admin", ["translator"]);
        using var adminClient = Client(admin);
        var member = await UserAsync();
        await MemberAsync(teamId, member, "member", ["translator"]);
        var lockedResponse = await adminClient.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", user_id = member["_id"].ToString()!, tags = new[] { "translator" } }
            }
        });
        Assert.False(lockedResponse.IsSuccessStatusCode);
        var failed = await lockedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(failed.GetProperty("stopped").GetBoolean());
        Assert.Equal(103, failed.GetProperty("failed").GetProperty("error_code").GetInt32());
    }

    [Fact]
    public async Task CompleteReopenAndClearLifecycle()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString()!;

        var completed = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/complete", new { }));
        Assert.Equal("COMPLETED", completed.GetProperty("status").GetString());
        Assert.True(completed.GetProperty("changed").GetBoolean());
        var again = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/complete", new { }));
        Assert.False(again.GetProperty("changed").GetBoolean());

        var reopened = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/reopen", new { }));
        Assert.Equal("NORMAL", reopened.GetProperty("status").GetString());
        Assert.True(reopened.GetProperty("changed").GetBoolean());

        // Seed file/output docs and counts; clear must remove docs and zero counts.
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument { ["project"] = ObjectId.Parse(projectId), ["size"] = 10 });
        await services.Store.Collection("output").InsertOneAsync(new BsonDocument { ["project"] = ObjectId.Parse(projectId), ["size"] = 5 });
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(projectId)),
            new BsonDocument("$set", new BsonDocument { ["fo"] = 1, ["fc"] = 1, ["fs"] = 10, ["ob"] = 5 }));

        var cleared = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/clear", new { }));
        Assert.Equal("CLEARED", cleared.GetProperty("status").GetString());
        Assert.True(cleared.GetProperty("changed").GetBoolean());
        Assert.Empty(await services.Store.Collection("file").Find(new BsonDocument("project", ObjectId.Parse(projectId))).ToListAsync());
        Assert.Empty(await services.Store.Collection("output").Find(new BsonDocument("project", ObjectId.Parse(projectId))).ToListAsync());
        var stored = await ProjectAsync(ObjectId.Parse(projectId));
        Assert.Equal(1, stored.Integer("st"));
        Assert.False(stored.Flag("cip"));
        Assert.Equal(0, stored.Integer("fo"));
        Assert.Equal(0, stored.Integer("ob"));

        var clearedAgain = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/clear", new { }));
        Assert.False(clearedAgain.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task TransferOwnerChangesCreatorTagsAndIsIdempotent()
    {
        var creator = await UserAsync();
        using var owner = Client(creator);
        var teamId = await CreateTeamAsync(owner, Name("team"));
        var project = await CreateProjectAsync(owner, teamId, Name("project"));
        var projectId = project.GetProperty("id").GetString()!;
        var successor = await UserAsync();
        await MemberAsync(teamId, successor, "member", ["translator"]);
        await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/members/changes", new
        {
            operations = new[]
            {
                new { operation_id = Name("op"), action = "add", user_id = successor["_id"].ToString()!, tags = new[] { "translator" } }
            }
        }));

        var transferred = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/owner/transfer",
            new { new_owner_user_id = successor["_id"].ToString()!, keep_old_owner_admin = true }));
        Assert.True(transferred.GetProperty("changed").GetBoolean());
        Assert.Equal(successor["_id"].ToString(), transferred.GetProperty("project").GetProperty("owner_user_id").GetString());
        Assert.Contains("creator", transferred.GetProperty("member").GetProperty("tags").EnumerateArray().Select(v => v.GetString()));
        var oldOwner = await services.Store.Collection("project_member").Find(new BsonDocument("user", creator["_id"])).FirstAsync();
        Assert.DoesNotContain("creator", oldOwner.GetValue("tags").AsBsonArray.Select(v => v.AsString));
        Assert.Contains("admin", oldOwner.GetValue("tags").AsBsonArray.Select(v => v.AsString));

        var again = await BodyAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/owner/transfer",
            new { new_owner_user_id = successor["_id"].ToString()! }));
        Assert.False(again.GetProperty("changed").GetBoolean());

        await ErrorAsync(await owner.PostAsJsonAsync($"/v1/projects/{projectId}/owner/transfer",
            new { new_owner_user_id = creator["_id"].ToString()!, expected_version = 999 }), 5110);
    }

    // ---- helpers ----

    private static string Name(string prefix) => prefix + "_" + ObjectId.GenerateNewId().ToString()[12..];

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

    private async Task<JsonElement> CreateProjectAsync(HttpClient client, ObjectId teamId, string name)
    {
        var form = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["intro"] = "",
            ["allow_apply_type"] = 1,
            ["application_check_type"] = 2,
            ["project_set"] = null,
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

    private async Task MemberAsync(ObjectId team, BsonDocument user, string tag = "member", string[]? qualifications = null) =>
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = team,
            ["user"] = user["_id"],
            ["base_tag"] = tag,
            ["status"] = "active",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(qualifications ?? Array.Empty<string>()),
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
}
