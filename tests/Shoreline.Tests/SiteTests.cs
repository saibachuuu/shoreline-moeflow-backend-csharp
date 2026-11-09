using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class SiteTests(LocalServices services)
{
    [Fact]
    public async Task SettingsAreAdminOnlyValidateAtomicallyAndPreserveExtensionFields()
    {
        var collection = services.Store.Collection("site_setting");
        await collection.UpdateOneAsync(new BsonDocument("n", "site"),
            new BsonDocument("$set", new BsonDocument("extension_data", "preserve")), new UpdateOptions { IsUpsert = true });
        var user = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["n"] = "s" + Guid.NewGuid().ToString("N")[..12],
            ["e"] = Guid.NewGuid() + "@example.test", ["p"] = PasswordHash.Create("test-password"), ["admin"] = false };
        await services.Store.Collection("user").InsertOneAsync(user);
        using var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(user["_id"].ToString()!, user["p"].AsString));
        using var denied = await client.GetAsync("/v1/admin/site-setting");
        Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
        await services.Store.Collection("user").UpdateOneAsync(new BsonDocument("_id", user["_id"]), new BsonDocument("$set", new BsonDocument("admin", true)));
        var team = ObjectId.GenerateNewId().ToString();
        using var update = await client.PutAsJsonAsync("/v1/admin/site-setting", new
        { enable_whitelist = false, whitelist_emails = new[] { "allowed@example.test" }, only_allow_admin_create_team = true,
            auto_join_team_ids = new[] { team }, homepage_html = "<p>Welcome</p>", homepage_css = "p{color:red}", custom_site_title = "欶澜" });
        update.EnsureSuccessStatusCode();
        var view = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(team, view.GetProperty("auto_join_team_ids")[0].GetString());
        var row = await collection.Find(new BsonDocument("n", "site")).SingleAsync();
        Assert.Equal("preserve", row["extension_data"].AsString);
        Assert.True(row["ajt"][0].IsObjectId);
        using var invalid = await client.PutAsJsonAsync("/v1/admin/site-setting", new
        { enable_whitelist = true, whitelist_emails = new[] { "bad" }, only_allow_admin_create_team = false, auto_join_team_ids = new[] { team } });
        Assert.False(invalid.IsSuccessStatusCode);
        row = await collection.Find(new BsonDocument("n", "site")).SingleAsync();
        Assert.False(row["ew"].AsBoolean);
        var homepage = await services.Http.GetFromJsonAsync<JsonElement>("/v1/homepage");
        Assert.Equal("<p>Welcome</p>", homepage.GetProperty("html").GetString());
        Assert.False(homepage.TryGetProperty("whitelist_emails", out _));
        Assert.False(homepage.TryGetProperty("auto_join_team_ids", out _));

        using var aliases = await client.PatchAsJsonAsync($"/v1/users/{user["_id"]}/aliases", new { aliases = new[] { "route-check" } });
        aliases.EnsureSuccessStatusCode();
        Assert.Equal("route-check", (await aliases.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("aliases")[0].GetString());
    }
}
