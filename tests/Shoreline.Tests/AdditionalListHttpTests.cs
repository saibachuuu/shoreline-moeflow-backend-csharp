using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// Snapshot-only HTTP coverage. These routes are wired through
// AdditionalListRegistration in the isolated verification checkout; the shared
// Program.cs in the working copy is intentionally untouched. /v1/admin/users is
// the pre-existing shared route and is exercised here for compatibility.
[Collection("local-services")]
public sealed class AdditionalListHttpTests(LocalServices services)
{
    [Fact]
    public async Task UserProjectsRequiresTokenAndListsOnlyDirectActiveMemberships()
    {
        var (actor, client) = await UserAsync();
        var team = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["n"] = "HTTP Team" };
        var set = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["n"] = "HTTP Set", ["d"] = true };
        await services.Store.Collection("team").InsertOneAsync(team);
        await services.Store.Collection("project_set").InsertOneAsync(set);
        var active = await ProjectAsync(team, set, "HTTP Active");
        var inherited = await ProjectAsync(team, set, "Team admin only");
        var removed = await ProjectAsync(team, set, "Removed");
        var invited = await ProjectAsync(team, set, "Invited");
        await MemberAsync(actor, active, "active");
        await MemberAsync(actor, removed, "removed");
        await MemberAsync(actor, invited, "invited");
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["team"] = team["_id"], ["user"] = actor["_id"],
            ["base_tag"] = "admin", ["status"] = "active"
        });

        using (client)
        using (var anonymous = services.App.CreateClient())
        {
            var missing = await anonymous.GetAsync("/v1/user/projects");
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(1003, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

            var response = await client.GetAsync("/v1/user/projects");
            response.EnsureSuccessStatusCode();
            Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-Pagination-Count")));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(active["_id"].ToString(), Assert.Single(body.EnumerateArray()).GetProperty("id").GetString());
            Assert.DoesNotContain(body.EnumerateArray(), card => card.GetProperty("id").GetString() == inherited["_id"].ToString());

            var invalid = await client.GetAsync("/v1/user/projects?status=normal");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var error = await invalid.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5100, error.GetProperty("code").GetInt32());
            Assert.Equal("身份标签请求参数错误: invalid project status", error.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task AdminVCodesRequiresAdminAndExcludesCaptcha()
    {
        await services.Store.Collection("v_code").DeleteManyAsync(new BsonDocument());
        var captcha = ObjectId.GenerateNewId();
        var emailCode = ObjectId.GenerateNewId();
        await services.Store.Collection("v_code").InsertManyAsync(new[]
        {
            new BsonDocument
            {
                ["_id"] = captcha, ["c"] = "captcha-http", ["t"] = 1, ["i"] = "captcha",
                ["e"] = new BsonDateTime(DateTime.UtcNow.AddDays(1)), ["w"] = 0,
                ["s"] = new BsonDateTime(DateTime.UtcNow)
            },
            new BsonDocument
            {
                ["_id"] = emailCode, ["c"] = "email-http", ["t"] = 2, ["i"] = "http@example.test",
                ["e"] = new BsonDateTime(DateTime.UtcNow.AddDays(1)), ["w"] = 2,
                ["s"] = new BsonDateTime(DateTime.UtcNow.AddMinutes(-1))
            }
        });
        var (_, ordinary) = await UserAsync();
        var (_, admin) = await UserAsync(admin: true);

        using (ordinary)
        using (admin)
        using (var anonymous = services.App.CreateClient())
        {
            var missing = await anonymous.GetAsync("/v1/admin/v-codes");
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(1003, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

            var denied = await ordinary.GetAsync("/v1/admin/v-codes");
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

            var response = await admin.GetAsync("/v1/admin/v-codes");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var item = Assert.Single(body.EnumerateArray());
            Assert.Equal(emailCode.ToString(), item.GetProperty("id").GetString());
            Assert.Equal("验证邮箱", item.GetProperty("intro").GetString());
            Assert.Equal("email-http", item.GetProperty("content").GetString());
            Assert.DoesNotContain(body.EnumerateArray(), row => row.GetProperty("id").GetString() == captcha.ToString());
        }
    }

    [Fact]
    public async Task AdminUsersRequiresAdminAndSearchesAliasesOnlyWithoutCredentials()
    {
        var marker = "alias-" + Guid.NewGuid().ToString("N");
        var (_, ordinary) = await UserAsync();
        var (target, _) = await UserAsync(alias: marker);
        var (_, admin) = await UserAsync(admin: true);

        using (ordinary)
        using (admin)
        using (var anonymous = services.App.CreateClient())
        {
            var missing = await anonymous.GetAsync("/v1/admin/users?word=" + marker);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(1003, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

            var denied = await ordinary.GetAsync("/v1/admin/users?word=" + marker);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

            var byAlias = await admin.GetAsync("/v1/admin/users?word=" + marker);
            byAlias.EnsureSuccessStatusCode();
            var body = await byAlias.Content.ReadFromJsonAsync<JsonElement>();
            var found = Assert.Single(body.EnumerateArray());
            Assert.Equal(target["_id"].ToString(), found.GetProperty("id").GetString());
            Assert.Equal(target["e"].AsString, found.GetProperty("email").GetString());
            Assert.False(found.TryGetProperty("p", out _));
            Assert.False(found.TryGetProperty("token", out _));
            Assert.False(found.TryGetProperty("can_manage_site_admins", out _));

            var byEmail = await admin.GetAsync("/v1/admin/users?word=" + Uri.EscapeDataString(target["e"].AsString));
            byEmail.EnsureSuccessStatusCode();
            Assert.Empty((await byEmail.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        }
    }

    [Fact]
    public async Task AdminUsersSortByCreateTimeDescendingAndPaginate()
    {
        var prefix = "page-" + Guid.NewGuid().ToString("N")[..10];
        var (older, _) = await UserAsync(name: prefix + "-older", created: DateTime.UtcNow.AddMinutes(-10));
        var (newer, _) = await UserAsync(name: prefix + "-newer", created: DateTime.UtcNow.AddMinutes(-1));
        var (_, admin) = await UserAsync(admin: true);

        using (admin)
        {
            var first = await admin.GetAsync($"/v1/admin/users?word={prefix}&limit=1&page=1");
            first.EnsureSuccessStatusCode();
            Assert.Equal("2", Assert.Single(first.Headers.GetValues("X-Pagination-Count")));
            Assert.Equal("1", Assert.Single(first.Headers.GetValues("X-Pagination-Limit")));
            var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(newer["_id"].ToString(), Assert.Single(firstBody.EnumerateArray()).GetProperty("id").GetString());

            var second = await admin.GetAsync($"/v1/admin/users?word={prefix}&limit=1&page=2");
            second.EnsureSuccessStatusCode();
            var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(older["_id"].ToString(), Assert.Single(secondBody.EnumerateArray()).GetProperty("id").GetString());
        }
    }

    private async Task<(BsonDocument User, HttpClient Client)> UserAsync(bool admin = false, string? email = null,
        string? name = null, string? alias = null, DateTime? created = null)
    {
        var id = ObjectId.GenerateNewId();
        var userName = name ?? "http-user-" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id, ["e"] = email ?? id + "@example.test", ["n"] = userName,
            ["ns"] = SearchText.Normalize(userName), ["p"] = PasswordHash.Create("test-password"),
            ["admin"] = admin, ["c"] = created ?? DateTime.UtcNow
        };
        if (alias is not null)
        {
            user["as"] = new BsonArray { alias };
            user["asrch"] = new BsonArray { SearchText.Normalize(alias) };
        }
        await services.Store.Collection("user").InsertOneAsync(user);
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), user["p"].AsString));
        return (user, client);
    }

    private async Task<BsonDocument> ProjectAsync(BsonDocument team, BsonDocument set, string name)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["n"] = name, ["ns"] = SearchText.Normalize(name), ["st"] = 0,
            ["t"] = team["_id"], ["ps"] = set["_id"], ["m_e"] = new BsonDateTime(DateTime.UtcNow),
            ["sc"] = 0, ["tc"] = 0, ["tsc"] = 0, ["csc"] = 0, ["fs"] = 0L
        };
        await services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task MemberAsync(BsonDocument user, BsonDocument project, string status)
    {
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["project"] = project["_id"], ["user"] = user["_id"],
            ["display_name"] = user["n"].AsString, ["status"] = status, ["tags"] = new BsonArray()
        });
    }
}
