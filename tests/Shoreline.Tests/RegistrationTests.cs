using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class RegistrationTests(LocalServices services)
{
    private string UniqueEmail() => Guid.NewGuid().ToString("N") + "@example.test";
    private string UniqueName() => "u" + Guid.NewGuid().ToString("N")[..11];

    private async Task<string> IssueCodeAsync(string email)
    {
        var codes = services.App.Services.GetRequiredService<EmailCodeService>();
        await codes.IssueAsync(2, email, default);
        var row = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
        return row["verification"]["c"].AsString;
    }

    private async Task<BsonDocument?> SaveSiteSettingAsync(bool enableWhitelist, string[] whitelist, ObjectId[] autoJoin)
    {
        var collection = services.Store.Collection("site_setting");
        var original = await collection.Find(new BsonDocument("n", "site")).FirstOrDefaultAsync();
        await collection.UpdateOneAsync(new BsonDocument("n", "site"), new BsonDocument("$set", new BsonDocument
        {
            ["ew"] = enableWhitelist,
            ["we"] = new BsonArray(whitelist.Select(e => new BsonString(e))),
            ["ajt"] = new BsonArray(autoJoin.Select(id => new BsonObjectId(id)))
        }), new UpdateOptions { IsUpsert = true });
        return original;
    }

    private async Task RestoreSiteSettingAsync(BsonDocument? original)
    {
        var collection = services.Store.Collection("site_setting");
        await collection.DeleteOneAsync(new BsonDocument("n", "site"));
        if (original is not null) await collection.InsertOneAsync(original);
    }

    private async Task<HttpResponseMessage> RegisterAsync(string email, string name, string password, string vCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/users")
        { Content = JsonContent.Create(new { email, name, password, v_code = vCode }) };
        return await services.Http.SendAsync(request);
    }

    [Fact]
    public async Task RegistersWithLowercasedEmailReturnsTokenAndConsumesCodeOnce()
    {
        var original = await SaveSiteSettingAsync(false, [], []);
        try
        {
            var email = UniqueEmail();
            var name = UniqueName();
            var code = await IssueCodeAsync(email);
            using var response = await RegisterAsync(email.ToUpperInvariant(), name, "initial-password", code);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("注册成功", data.GetProperty("message").GetString());
            var token = data.GetProperty("token").GetString();
            Assert.False(string.IsNullOrEmpty(token));
            var user = await services.Store.Collection("user").Find(new BsonDocument("e", email)).SingleAsync();
            Assert.Equal(name, user["n"].AsString);
            Assert.Equal(user["ns"].AsString, Shoreline.Api.Infrastructure.SearchText.Normalize(name));
            var job = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
            Assert.True(job["verification"]["consumed"].AsBoolean);
            using var client = services.App.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var info = await client.GetAsync("/v1/user/info");
            info.EnsureSuccessStatusCode();
            // A consumed code cannot register again, even for a fresh name.
            using var replay = await RegisterAsync(email, UniqueName(), "initial-password", code);
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.Equal(1, await services.Store.Collection("user").CountDocumentsAsync(new BsonDocument("e", email)));
        }
        finally { await RestoreSiteSettingAsync(original); }
    }

    [Fact]
    public async Task WrongCodeIsRejectedWithoutCreatingUser()
    {
        var original = await SaveSiteSettingAsync(false, [], []);
        try
        {
            var email = UniqueEmail();
            var name = UniqueName();
            await IssueCodeAsync(email);
            using var response = await RegisterAsync(email, name, "initial-password", "wrong-code");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, data.GetProperty("code").GetInt32());
            Assert.Equal(0, await services.Store.Collection("user").CountDocumentsAsync(new BsonDocument("e", email)));
            var job = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
            Assert.False(job["verification"]["consumed"].AsBoolean);
            Assert.Equal(1, job["verification"]["w"].AsInt32);
        }
        finally { await RestoreSiteSettingAsync(original); }
    }

    [Fact]
    public async Task FieldErrorsAreCollectedAndDoNotConsumeTheCode()
    {
        var original = await SaveSiteSettingAsync(false, [], []);
        try
        {
            var email = UniqueEmail();
            var name = UniqueName();
            var code = await IssueCodeAsync(email);
            // Invalid name and short password: both fields reported, code kept.
            using var invalid = await RegisterAsync(email, "x", "12345", code);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var invalidData = await invalid.Content.ReadFromJsonAsync<JsonElement>();
            var message = invalidData.GetProperty("message");
            Assert.True(message.TryGetProperty("name", out _));
            Assert.True(message.TryGetProperty("password", out _));
            var job = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
            Assert.False(job["verification"]["consumed"].AsBoolean);
            // The same code still registers successfully afterwards.
            using var retry = await RegisterAsync(email, name, "initial-password", code);
            retry.EnsureSuccessStatusCode();
            // Duplicate email and duplicate (case-insensitive) name are reported together.
            var otherEmail = UniqueEmail();
            var otherCode = await IssueCodeAsync(otherEmail);
            using var duplicate = await RegisterAsync(email, name.ToUpperInvariant(), "initial-password", otherCode);
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            var duplicateMessage = (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
            Assert.True(duplicateMessage.TryGetProperty("email", out _));
            Assert.True(duplicateMessage.TryGetProperty("name", out _));
            Assert.Equal(0, await services.Store.Collection("user").CountDocumentsAsync(new BsonDocument("e", otherEmail)));
        }
        finally { await RestoreSiteSettingAsync(original); }
    }

    [Fact]
    public async Task WhitelistRejectionUsesPythonCodeAndOrder()
    {
        var original = await SaveSiteSettingAsync(true, [], []);
        try
        {
            var email = UniqueEmail();
            var code = await IssueCodeAsync(email);
            using var response = await RegisterAsync(email, UniqueName(), "initial-password", code);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("EmailNotInWhitelistError", data.GetProperty("error").GetString());
            Assert.Equal(1012, data.GetProperty("code").GetInt32());
            Assert.Equal(0, await services.Store.Collection("user").CountDocumentsAsync(new BsonDocument("e", email)));
        }
        finally { await RestoreSiteSettingAsync(original); }
    }

    [Fact]
    public async Task AutoJoinAssignsMemberAndAdminRolesAndSkipsMissingTeams()
    {
        var beginnerRole = ObjectId.GenerateNewId();
        var adminRole = ObjectId.GenerateNewId();
        var roles = services.Store.Collection("team_role");
        await roles.InsertOneAsync(new BsonDocument { ["_id"] = beginnerRole, ["m_o"] = "beginner" });
        await roles.InsertOneAsync(new BsonDocument { ["_id"] = adminRole, ["m_o"] = "admin" });
        var memberTeam = ObjectId.GenerateNewId();
        var adminTeam = ObjectId.GenerateNewId();
        var teams = services.Store.Collection("team");
        await teams.InsertOneAsync(new BsonDocument { ["_id"] = memberTeam, ["n"] = "member-team", ["dr"] = beginnerRole });
        await teams.InsertOneAsync(new BsonDocument { ["_id"] = adminTeam, ["n"] = "admin-team", ["dr"] = adminRole });
        var original = await SaveSiteSettingAsync(false, [], [memberTeam, adminTeam, ObjectId.GenerateNewId()]);
        try
        {
            var email = UniqueEmail();
            var code = await IssueCodeAsync(email);
            using var response = await RegisterAsync(email, UniqueName(), "initial-password", code);
            response.EnsureSuccessStatusCode();
            var user = await services.Store.Collection("user").Find(new BsonDocument("e", email)).SingleAsync();
            var members = services.Store.Collection("team_member");
            var memberRow = await members.Find(new BsonDocument { ["team"] = memberTeam, ["user"] = user["_id"] }).SingleAsync();
            var adminRow = await members.Find(new BsonDocument { ["team"] = adminTeam, ["user"] = user["_id"] }).SingleAsync();
            Assert.Equal("member", memberRow["base_tag"].AsString);
            Assert.Equal("admin", adminRow["base_tag"].AsString);
        }
        finally { await RestoreSiteSettingAsync(original); }
    }

    [Fact]
    public async Task LegacyCodeRegistersAndIsDeleted()
    {
        var original = await SaveSiteSettingAsync(false, [], []);
        try
        {
            var email = UniqueEmail();
            await services.Store.Collection("v_code").InsertOneAsync(new BsonDocument
            { ["_cls"] = "VCode", ["t"] = 2, ["i"] = email, ["c"] = "123456", ["w"] = 0, ["e"] = DateTime.UtcNow.AddMinutes(10) });
            using var response = await RegisterAsync(email, UniqueName(), "initial-password", "123456");
            response.EnsureSuccessStatusCode();
            Assert.Equal(1, await services.Store.Collection("user").CountDocumentsAsync(new BsonDocument("e", email)));
            Assert.Equal(0, await services.Store.Collection("v_code").CountDocumentsAsync(new BsonDocument { ["t"] = 2, ["i"] = email }));
        }
        finally { await RestoreSiteSettingAsync(original); }
    }

    [Fact]
    public async Task ConcurrentRegistrationsWithTheSameNameOnlyOneSucceeds()
    {
        var original = await SaveSiteSettingAsync(false, [], []);
        try
        {
            var firstEmail = UniqueEmail();
            var secondEmail = UniqueEmail();
            var firstCode = await IssueCodeAsync(firstEmail);
            var secondCode = await IssueCodeAsync(secondEmail);
            var name = UniqueName();
            var attempts = await Task.WhenAll(
                RegisterAsync(firstEmail, name, "initial-password", firstCode),
                RegisterAsync(secondEmail, name, "initial-password", secondCode));
            Assert.Single(attempts, r => r.IsSuccessStatusCode);
            var failed = Assert.Single(attempts, r => !r.IsSuccessStatusCode);
            var message = (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
            Assert.True(message.TryGetProperty("name", out _));
            Assert.Equal(1, await services.Store.Collection("user").CountDocumentsAsync(new BsonDocument("n", name)));
        }
        finally { await RestoreSiteSettingAsync(original); }
    }
}
