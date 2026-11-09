using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services-admin-user")]
public sealed class AdminUserTests(LocalServices services)
{
    [Fact]
    public async Task OnlyConfiguredOwnerCanGrantAdminAndBannedOwnerIsDenied()
    {
        var (target, targetClient) = await CreateAsync(false);
        var (admin, adminClient) = await CreateAsync(true);
        var (owner, ownerClient) = await CreateAsync(true, "owner@example.test");
        using (targetClient)
        using (adminClient)
        using (ownerClient)
        {
            foreach (var client in new[] { targetClient, adminClient })
            {
                using var denied = await client.PutAsJsonAsync("/v1/admin/admin-status", new { user_id = target["_id"].ToString(), status = true });
                Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
            }
            using var promoted = await ownerClient.PutAsJsonAsync("/v1/admin/admin-status", new { user_id = target["_id"].ToString(), status = "yes" });
            promoted.EnsureSuccessStatusCode();
            Assert.True((await services.Store.Collection("user").Find(new BsonDocument("_id", target["_id"])).SingleAsync())["admin"].AsBoolean);
            await services.Store.Collection("user").UpdateOneAsync(new BsonDocument("_id", owner["_id"]), new BsonDocument("$set", new BsonDocument("b", true)));
            using var banned = await ownerClient.PutAsJsonAsync("/v1/admin/admin-status", new { user_id = admin["_id"].ToString(), status = false });
            Assert.Equal(1006, (await banned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task AdminPasswordResetRevokesExistingTokenAndUserSearchIsPaginated()
    {
        var (user, client) = await CreateAsync(false);
        var (_, adminClient) = await CreateAsync(true);
        using (client)
        using (adminClient)
        {
            using var denied = await client.GetAsync("/v1/admin/users");
            Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
            using var search = await adminClient.GetAsync("/v1/admin/users?limit=1&word=" + Uri.EscapeDataString(user["n"].AsString));
            search.EnsureSuccessStatusCode();
            Assert.Equal("1", Assert.Single(search.Headers.GetValues("X-Pagination-Count")));
            var body = await search.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(user["e"].AsString, body[0].GetProperty("email").GetString());
            using var reset = await adminClient.PutAsJsonAsync($"/v1/admin/users/{user["_id"]}", new { password = "new-test-password" });
            reset.EnsureSuccessStatusCode();
            using var revoked = await client.GetAsync("/v1/user/info");
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            var updated = await services.Store.Collection("user").Find(new BsonDocument("_id", user["_id"])).SingleAsync();
            Assert.True(PasswordHash.Verify(updated["p"].AsString, "new-test-password"));
        }
    }

    private async Task<(BsonDocument, HttpClient)> CreateAsync(bool admin, string? email = null)
    {
        var id = ObjectId.GenerateNewId();
        var name = "a" + id.ToString()[12..];
        var user = new BsonDocument { ["_id"] = id, ["e"] = email ?? id + "@example.test", ["n"] = name,
            ["ns"] = name, ["p"] = PasswordHash.Create("test-password"), ["admin"] = admin, ["c"] = DateTime.UtcNow };
        await services.Store.Collection("user").InsertOneAsync(user);
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), user["p"].AsString));
        return (user, client);
    }
}
