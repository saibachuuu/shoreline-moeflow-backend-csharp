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

[Collection("local-services")]
public sealed class ProfileTests(LocalServices services)
{
    [Theory]
    [InlineData("  Straße  ", "strasse")]
    [InlineData("Σςσ", "σσσ")]
    [InlineData("é", "é")]
    [InlineData("ﬃİ", "ffii̇")]
    [InlineData("ᾈᾀ", "ἀιἀι")]
    [InlineData("ꭰᎠ", "ᎠᎠ")]
    public void SearchMatchesPythonCasefold(string value, string expected) => Assert.Equal(expected, SearchText.Normalize(value));

    [Fact]
    public async Task AliasesNormalizeDeduplicateAuditAndSearchWithoutDisclosingEmail()
    {
        var (user, client) = await UserAsync();
        using (client)
        {
            using var response = await client.PatchAsJsonAsync("/v1/me/aliases", new
            { aliases = new[] { "  Straße ", "STRASSE", "é", "é", "  " }, default_display_name = "Straße" });
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, data.GetProperty("user").GetProperty("aliases").GetArrayLength());
            var audit = await services.Store.Collection("identity_audit_event").Find(new BsonDocument("_id",
                ObjectId.Parse(data.GetProperty("audit_event_id").GetString()))).SingleAsync();
            Assert.Equal(user["_id"], audit["actor"]);
            Assert.Equal("user_aliases_replace", audit["action"].AsString);
            var search = await client.GetFromJsonAsync<JsonElement>("/v1/users?word=STRASSE");
            Assert.Contains(search.EnumerateArray(), item => item.GetProperty("id").GetString() == user["_id"].ToString());
            Assert.All(search.EnumerateArray(), item => Assert.False(item.TryGetProperty("email", out _)));
            using var denied = await client.PatchAsJsonAsync($"/v1/users/{user["_id"]}/aliases", new { aliases = new[] { "attempt" } });
            Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task InvalidAliasCannotPartiallySaveProfileAndDisplayNameClearsWhenRemoved()
    {
        var (user, client) = await UserAsync();
        using (client)
        {
            using var invalid = await client.PutAsJsonAsync("/v1/user/info", new
            { name = "revised_name", signature = "changed", locale = "en", aliases = new[] { "REVISED_NAME" } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.Equal("ALIAS_INVALID", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("identity_code").GetString());
            var unchanged = await services.Store.Collection("user").Find(new BsonDocument("_id", user["_id"])).SingleAsync();
            Assert.Equal(user["n"], unchanged["n"]);
            using var add = await client.PatchAsJsonAsync("/v1/me/aliases", new { aliases = new[] { "alias" }, default_display_name = "alias" });
            add.EnsureSuccessStatusCode();
            using var remove = await client.PatchAsJsonAsync("/v1/me/aliases", new { aliases = Array.Empty<string>() });
            remove.EnsureSuccessStatusCode();
            var updated = await client.GetFromJsonAsync<JsonElement>("/v1/user/info");
            Assert.Equal("", updated.GetProperty("default_display_name").GetString());
        }
    }

    [Fact]
    public async Task ProfileSaveUsesRevisionAndUnicodeLength()
    {
        var (user, client) = await UserAsync();
        using (client)
        {
            using var change = await client.PutAsJsonAsync("/v1/user/info", new
            { name = user["n"].AsString, signature = string.Concat(Enumerable.Repeat("😀", 140)), locale = "en" });
            change.EnsureSuccessStatusCode();
            using var scope = services.App.Services.CreateScope();
            var profiles = scope.ServiceProvider.GetRequiredService<UserProfileService>();
            var error = await Assert.ThrowsAsync<ApiError>(() => profiles.UpdateAsync(user, user,
                JsonSerializer.SerializeToElement(new { aliases = new[] { "stale" } }), true, "", default));
            Assert.Equal(409, error.Status);
            using var missing = await client.PutAsJsonAsync("/v1/user/info", new { });
            var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Array, body.GetProperty("message").GetProperty("name").ValueKind);
        }
    }

    private async Task<(BsonDocument User, HttpClient Client)> UserAsync()
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument { ["_id"] = id, ["n"] = "u" + id.ToString()[12..], ["e"] = id + "@example.test",
            ["p"] = PasswordHash.Create("test-password"), ["l"] = "en", ["s"] = "", ["as"] = new BsonArray() };
        await services.Store.Collection("user").InsertOneAsync(user);
        var token = services.App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), user["p"].AsString);
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (user, client);
    }
}
