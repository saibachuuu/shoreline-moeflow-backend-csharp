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

// Snapshot-only HTTP evidence for the F1 term routes. The shared Program.cs is
// deliberately untouched; this file is copied into the temporary archive build
// whose Program wires AddTermServices() and MapTerms().
[Collection("local-services")]
public sealed class TermHttpTests(LocalServices services)
{
    [Fact]
    public async Task MissingTokenReturnsLegacyCode1003()
    {
        var team = await TeamAsync();
        using var response = await services.Http.GetAsync(
            $"/v1/teams/{team["_id"]}/term-banks");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1003, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BankAndTermCrudUseLegacyRoutesAndPaginationHeaders()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        using var client = Client(owner);
        var teamId = team["_id"].ToString();

        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
        {
            using var created = await client.PostAsJsonAsync($"/v1/teams/{teamId}/term-banks",
                BankBody(source, target, name));
            Assert.Equal("创建成功", (await BodyAsync(created)).GetProperty("message").GetString());
        }

        using var pageTwoResponse = await client.GetAsync(
            $"/v1/teams/{teamId}/term-banks?page=2&limit=2");
        Assert.Equal("2", pageTwoResponse.Headers.GetValues("X-Pagination-Page").Single());
        Assert.Equal("2", pageTwoResponse.Headers.GetValues("X-Pagination-Limit").Single());
        Assert.Equal("3", pageTwoResponse.Headers.GetValues("X-Pagination-Count").Single());
        Assert.Equal("2", pageTwoResponse.Headers.GetValues("X-Pagination-Page-Count").Single());
        Assert.Equal(1, (await BodyAsync(pageTwoResponse)).GetArrayLength());

        var banks = await BodyAsync(await client.GetAsync($"/v1/teams/{teamId}/term-banks?limit=100"));
        Assert.Equal(3, banks.GetArrayLength());
        var bank = banks[0];
        var bankId = bank.GetProperty("id").GetString()!;
        var editedName = bank.GetProperty("name").GetString() + "-edited";

        using var createdTerm = await client.PostAsJsonAsync($"/v1/term-banks/{bankId}/terms",
            new { source = "hello", target = "你好", tip = "" });
        Assert.Equal("创建成功", (await BodyAsync(createdTerm)).GetProperty("message").GetString());

        var terms = await BodyAsync(await client.GetAsync(
            $"/v1/term-banks/{bankId}/terms?word=hello"));
        Assert.Equal(1, terms.GetArrayLength());
        var termId = terms[0].GetProperty("id").GetString()!;

        using var editedBank = await client.PutAsJsonAsync($"/v1/term-banks/{bankId}",
            BankBody(source, target, editedName));
        Assert.Equal("修改成功", (await BodyAsync(editedBank)).GetProperty("message").GetString());

        using var editedTerm = await client.PutAsJsonAsync($"/v1/terms/{termId}",
            new { source = "hello2", target = "你好2", tip = "tip2" });
        Assert.Equal("修改成功", (await BodyAsync(editedTerm)).GetProperty("message").GetString());

        var editedTerms = await BodyAsync(await client.GetAsync(
            $"/v1/term-banks/{bankId}/terms?word=hello2"));
        Assert.Equal(1, editedTerms.GetArrayLength());
        Assert.Equal("hello2", editedTerms[0].GetProperty("source").GetString());
        Assert.Equal("tip2", editedTerms[0].GetProperty("tip").GetString());

        using var deletedTerm = await client.DeleteAsync($"/v1/terms/{termId}");
        Assert.Equal("删除成功", (await BodyAsync(deletedTerm)).GetProperty("message").GetString());
        using var deletedBank = await client.DeleteAsync($"/v1/term-banks/{bankId}");
        Assert.Equal("删除成功", (await BodyAsync(deletedBank)).GetProperty("message").GetString());

        using var after = await client.GetAsync($"/v1/teams/{teamId}/term-banks?limit=100");
        Assert.Equal("2", after.Headers.GetValues("X-Pagination-Count").Single());
        Assert.Equal(2, (await BodyAsync(after)).GetArrayLength());
    }

    [Fact]
    public async Task ListIsTeamScopedAndOwnerBranchWorksOverHttp()
    {
        var ownerA = await UserAsync();
        var ownerB = await UserAsync();
        var outsider = await UserAsync();
        var teamA = await TeamAsync();
        var teamB = await TeamAsync();
        await MemberAsync(teamA, ownerA, "creator");
        await MemberAsync(teamB, ownerB, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");

        var localAlpha = await BankAsync(teamA, ownerA, source, target, "Alpha A");
        var foreignAlpha = await BankAsync(teamB, ownerB, source, target, "Alpha B");
        var ownerOnly = await BankAsync(teamA, outsider, source, target, "Owner Only");

        using var clientA = Client(ownerA);
        using var clientB = Client(ownerB);
        var teamAId = teamA["_id"].ToString();
        var teamBId = teamB["_id"].ToString();

        var allA = await BodyAsync(await clientA.GetAsync($"/v1/teams/{teamAId}/term-banks?limit=100"));
        Assert.Equal(2, allA.GetArrayLength());
        var idsA = allA.EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
        Assert.Contains(localAlpha["_id"].ToString(), idsA);
        Assert.Contains(ownerOnly["_id"].ToString(), idsA);
        Assert.DoesNotContain(foreignAlpha["_id"].ToString(), idsA);

        // Both teams have an "Alpha" bank. A missing t filter would leak the
        // foreign bank into this search/count.
        var searchA = await BodyAsync(await clientA.GetAsync(
            $"/v1/teams/{teamAId}/term-banks?word=Alpha&limit=100"));
        Assert.Equal(1, searchA.GetArrayLength());
        Assert.Equal(localAlpha["_id"].ToString(), searchA[0].GetProperty("id").GetString());

        using var crossTeamList = await clientA.GetAsync($"/v1/teams/{teamBId}/term-banks");
        Assert.Equal(103, (await ErrorBodyAsync(crossTeamList)).GetProperty("code").GetInt32());

        // outsider has no team membership but owns a bank: the legacy owner
        // branch must still allow edit without CHANGE_TERM_BANK. teamB's creator
        // is not a teamA member and is not the bank owner, so this must fail.
        using var ownerEdit = await clientB.PutAsJsonAsync($"/v1/term-banks/{ownerOnly["_id"]}",
            BankBody(source, target, "Owner Edited"));
        Assert.Equal(103, (await ErrorBodyAsync(ownerEdit)).GetProperty("code").GetInt32());

        using var outsiderClient = Client(outsider);
        using var ownerEditByOutsider = await outsiderClient.PutAsJsonAsync(
            $"/v1/term-banks/{ownerOnly["_id"]}", BankBody(source, target, "Owner Edited"));
        Assert.Equal("修改成功", (await BodyAsync(ownerEditByOutsider)).GetProperty("message").GetString());

        using var nonOwnerEdit = await clientB.PutAsJsonAsync($"/v1/term-banks/{localAlpha["_id"]}",
            BankBody(source, target, "Not Allowed"));
        Assert.Equal(103, (await ErrorBodyAsync(nonOwnerEdit)).GetProperty("code").GetInt32());
    }

    // ---- helpers ---------------------------------------------------------

    private static object BankBody(BsonDocument source, BsonDocument target, string name) => new
    {
        name,
        tip = "",
        source_language_id = source["_id"].ToString(),
        target_language_id = target["_id"].ToString()
    };

    private async Task<BsonDocument> UserAsync()
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
            ["p"] = PasswordHash.Create("test-password"),
            ["admin"] = false,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> TeamAsync()
    {
        var team = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "team_" + ObjectId.GenerateNewId().ToString()[12..],
            ["c"] = DateTime.UtcNow
        };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task MemberAsync(BsonDocument team, BsonDocument user, string baseTag) =>
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["status"] = "active",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["version"] = 0
        });

    private async Task<BsonDocument> LanguageAsync(string code, string name)
    {
        var existing = await services.Store.Collection("language")
            .Find(new BsonDocument("co", code)).FirstOrDefaultAsync();
        if (existing is not null) return existing;
        var language = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["e"] = name,
            ["c"] = name,
            ["co"] = code,
            ["gt"] = code,
            ["go"] = code,
            ["n"] = false,
            ["s"] = 0
        };
        await services.Store.Collection("language").InsertOneAsync(language);
        return language;
    }

    private async Task<BsonDocument> BankAsync(BsonDocument team, BsonDocument owner,
        BsonDocument source, BsonDocument target, string name)
    {
        var now = DateTime.UtcNow;
        var bank = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = team["_id"],
            ["n"] = name,
            ["sl"] = source["_id"],
            ["tl"] = target["_id"],
            ["ti"] = "",
            ["u"] = owner["_id"],
            ["c"] = now,
            ["e"] = now
        };
        await services.Store.Collection("term_bank").InsertOneAsync(bank);
        return bank;
    }

    private HttpClient Client(BsonDocument user)
    {
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>()
                .Issue(user["_id"].ToString()!, user.Text("p")));
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ErrorBodyAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
