using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class LegacyTypeTests(LocalServices services)
{
    [Fact]
    public async Task SystemRolesAndTypeOptionsKeepLegacyShapesAndNumericPermissions()
    {
        var roles = await services.Http.GetFromJsonAsync<JsonElement>("/v1/types/system-role?group_type=team");
        Assert.Equal(4, roles.GetArrayLength());
        Assert.DoesNotContain(roles.EnumerateArray(), row => row.GetProperty("system_code").GetString() == "creator");
        var admin = Assert.Single(roles.EnumerateArray(), row => row.GetProperty("system_code").GetString() == "admin");
        Assert.Equal(400, admin.GetProperty("level").GetInt32());
        Assert.True(ObjectId.TryParse(admin.GetProperty("id").GetString(), out _));
        Assert.Contains(admin.GetProperty("permissions").EnumerateArray(), row => row.GetProperty("id").GetInt32() == 1020);
        var all = await services.Http.GetFromJsonAsync<JsonElement>("/v1/types/system-role?group_type=team&with_creator=TRUE");
        Assert.Equal(5, all.GetArrayLength());
        Assert.Contains(all.EnumerateArray(), row => row.GetProperty("id").GetString() == admin.GetProperty("id").GetString());
        var projects = await services.Http.GetFromJsonAsync<JsonElement>("/v1/types/system-role?group_type=project");
        Assert.Equal(6, projects.GetArrayLength());
        Assert.Contains(projects.EnumerateArray(), row => row.GetProperty("system_code").GetString() == "picture_editor");
        var allow = await services.Http.GetFromJsonAsync<JsonElement>("/v1/types/allow-apply-type?group_type=project");
        Assert.Equal(new[] { 1, 2, 3 }, allow.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()));
        var permissions = await services.Http.GetFromJsonAsync<JsonElement>("/v1/types/permission?group_type=project");
        Assert.Equal(26, permissions.GetArrayLength());
        Assert.Equal("Upload Image", Assert.Single(permissions.EnumerateArray(), row => row.GetProperty("id").GetInt32() == 1020).GetProperty("name").GetString());
        var locales = await services.Http.GetFromJsonAsync<JsonElement>("/v1/types/locale");
        Assert.Equal(4, locales.GetArrayLength());
        Assert.Contains(locales.EnumerateArray(), row => row.GetProperty("id").GetString() == "zh_CN");
    }

    [Fact]
    public async Task PublicTypesNegotiateChineseAndRejectInvalidGroupWithoutAuthentication()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/types/permission?group_type=team");
        request.Headers.AcceptLanguage.ParseAdd("zh");
        request.Headers.Authorization = new("Bearer", "intentionally-invalid-public-token");
        using var response = await services.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("访问团队", Assert.Single(rows.EnumerateArray(), row => row.GetProperty("id").GetInt32() == 1).GetProperty("name").GetString());
        using var bad = await services.Http.GetAsync("/v1/types/system-role?group_type=other");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(111, (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
        using var unknown = await services.Http.GetAsync("/v1/types/unknown?group_type=team");
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
    }

    [Fact]
    public Task ExistingRoleCollectionsAreNotRecreatedOrOverwritten() => WithDatabaseAsync(async store =>
    {
        var original = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["m_s"] = true, ["m_o"] = "creator", ["m_n"] = "自定义历史角色", ["m_p"] = new BsonArray { 1 } };
        await store.Collection("team_role").InsertOneAsync(original);
        var catalog = new LegacyTypes(store, TimeProvider.System);
        await catalog.InitializeAsync();
        Assert.Equal(1, await store.Collection("team_role").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(original, await store.Collection("team_role").Find(FilterDefinition<BsonDocument>.Empty).FirstAsync());
        Assert.False(await store.Collection("csharp_seed").Find(new BsonDocument("_id", "team-roles-v1")).AnyAsync());
        Assert.Equal(7, await store.Collection("project_role").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    });

    [Fact]
    public Task InterruptedAndConcurrentRoleSeedingIsIdempotentAndPreservesReferences() => WithDatabaseAsync(async store =>
    {
        var original = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["m_s"] = true, ["m_o"] = "admin", ["m_n"] = "保留名称", ["m_l"] = 401, ["m_p"] = new BsonArray { 1 } };
        await store.Collection("team_role").InsertOneAsync(original);
        await store.Collection("csharp_seed").InsertOneAsync(new BsonDocument { ["_id"] = "team-roles-v1", ["complete"] = false });
        var catalog = new LegacyTypes(store, TimeProvider.System);
        await Task.WhenAll(catalog.InitializeAsync(), catalog.InitializeAsync(), catalog.InitializeAsync());
        Assert.Equal(5, await store.Collection("team_role").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(7, await store.Collection("project_role").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(original, await store.Collection("team_role").Find(new BsonDocument("m_o", "admin")).FirstAsync());
        Assert.True((await store.Collection("csharp_seed").Find(new BsonDocument("_id", "team-roles-v1")).FirstAsync()).Flag("complete"));
        await catalog.InitializeAsync();
        Assert.Equal(original["_id"], (await catalog.SystemRoleAsync("team", "admin", CancellationToken.None))["_id"]);
    });

    [Fact]
    public async Task DefaultRoleValidationDoesNotAcceptCreatorsWrongScopesOrForeignCustomRoles()
    {
        var catalog = services.App.Services.GetRequiredService<LegacyTypes>();
        var member = await catalog.SystemRoleAsync("team", "member", CancellationToken.None);
        Assert.Equal(member, await catalog.DefaultRoleAsync("team", member["_id"].ToString()!, null, CancellationToken.None));
        var creator = await catalog.SystemRoleAsync("team", "creator", CancellationToken.None);
        Assert.Equal(2, (await Assert.ThrowsAsync<ApiError>(() => catalog.DefaultRoleAsync("team", creator["_id"].ToString()!, null, CancellationToken.None))).Code);
        var translator = await catalog.SystemRoleAsync("project", "translator", CancellationToken.None);
        Assert.Equal(2, (await Assert.ThrowsAsync<ApiError>(() => catalog.DefaultRoleAsync("team", translator["_id"].ToString()!, null, CancellationToken.None))).Code);
        var group = ObjectId.GenerateNewId();
        var custom = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["m_s"] = false, ["m_n"] = "Custom", ["g"] = group };
        await services.Store.Collection("team_role").InsertOneAsync(custom);
        Assert.Equal(custom, await catalog.DefaultRoleAsync("team", custom["_id"].ToString()!, group, CancellationToken.None));
        Assert.Equal(2, (await Assert.ThrowsAsync<ApiError>(() => catalog.DefaultRoleAsync("team", custom["_id"].ToString()!, ObjectId.GenerateNewId(), CancellationToken.None))).Code);
        Assert.Equal(2, (await Assert.ThrowsAsync<ApiError>(() => catalog.DefaultRoleAsync("team", custom["_id"].ToString()!, null, CancellationToken.None))).Code);
    }

    private static async Task WithDatabaseAsync(Func<MongoStore, Task> action)
    {
        var name = "shoreline_types_test_" + Guid.NewGuid().ToString("N");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:Uri"] = "mongodb://127.0.0.1:" + TestEnvironment.MongoPort + "/" + name }).Build();
        try { await action(new MongoStore(config)); }
        finally { await new MongoClient("mongodb://127.0.0.1:" + TestEnvironment.MongoPort).DropDatabaseAsync(name); }
    }
}
