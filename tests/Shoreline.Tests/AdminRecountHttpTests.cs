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

/// <summary>
/// Incremental counters drift whenever one update is skipped, and nothing
/// repairs them. The admin recount endpoint rebuilds them from the translation
/// collection.
/// </summary>
[Collection("local-services")]
public sealed class AdminRecountHttpTests(LocalServices services)
{
    [Fact]
    public async Task RecountRebuildsDriftedCountersFromTranslations()
    {
        var (user, userClient) = await UserAsync();
        var (_, admin) = await UserAsync(admin: true);
        var project = await ProjectAsync("Recount project");
        var file = await FileAsync(project);
        var source = await SourceAsync(file);
        var target = await TargetAsync(project);
        await TranslationAsync(source, target, user);

        // Simulate the drift: translations exist but the counters say zero.
        await services.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument { ["tsc"] = 0, ["csc"] = 0 }));
        await services.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", file["_id"]),
            new BsonDocument("$set", new BsonDocument { ["tsc"] = 0, ["csc"] = 0 }));
        await services.Store.Collection("target").UpdateOneAsync(
            new BsonDocument("_id", target["_id"]),
            new BsonDocument("$set", new BsonDocument { ["tsc"] = 0, ["csc"] = 0 }));

        using (userClient)
        using (admin)
        {
            var path = $"/v1/admin/projects/{project["_id"]}/recount";

            var denied = await userClient.PostAsync(path, null);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal(103, (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

            var response = await admin.PostAsync(path, null);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, body.GetProperty("translated_source_count").GetInt32());
            Assert.Equal(1, body.GetProperty("translated_delta").GetInt32());

            var stored = await services.Store.Collection("project")
                .Find(new BsonDocument("_id", project["_id"])).FirstAsync();
            Assert.Equal(1, stored.Integer("tsc"));
            var storedTarget = await services.Store.Collection("target")
                .Find(new BsonDocument("_id", target["_id"])).FirstAsync();
            Assert.Equal(1, storedTarget.Integer("tsc"));
            var cache = await services.Store.Collection("file_target_cache")
                .Find(new BsonDocument { ["f"] = file["_id"], ["t"] = target["_id"] }).FirstAsync();
            Assert.Equal(1, cache.Integer("ts"));
        }
    }

    private async Task<(BsonDocument User, HttpClient Client)> UserAsync(bool admin = false)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["e"] = id + "@example.test",
            ["n"] = "recount-user-" + id.ToString()[12..],
            ["p"] = PasswordHash.Create("test-password"),
            ["admin"] = admin,
            ["c"] = new BsonDateTime(DateTime.UtcNow),
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), user["p"].AsString));
        return (user, client);
    }

    private async Task<BsonDocument> ProjectAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["st"] = 0,
            ["sc"] = 0,
            ["tc"] = 1,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["m_e"] = new BsonDateTime(DateTime.UtcNow),
        };
        await services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> FileAsync(BsonDocument project)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = project["_id"],
            ["n"] = "page.png",
            ["t"] = 0,
            ["ac"] = true,
            ["a"] = new BsonArray(),
            ["sc"] = 1,
            ["tsc"] = 0,
            ["csc"] = 0,
        };
        await services.Store.Collection("file").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> SourceAsync(BsonDocument file)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = file["_id"],
            ["r"] = 0,
            ["c"] = "",
            ["b"] = false,
            ["p"] = 0,
            ["cm"] = true,
            ["ct"] = new BsonDateTime(DateTime.UtcNow),
            ["e"] = new BsonDateTime(DateTime.UtcNow),
        };
        await services.Store.Collection("source").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> TargetAsync(BsonDocument project)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = project["_id"],
            ["l"] = ObjectId.GenerateNewId(),
            ["tsc"] = 0,
            ["csc"] = 0,
        };
        await services.Store.Collection("target").InsertOneAsync(document);
        return document;
    }

    private async Task TranslationAsync(BsonDocument source, BsonDocument target, BsonDocument user)
    {
        await services.Store.Collection("translation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = source["_id"],
            ["t"] = target["_id"],
            ["u"] = user["_id"],
            ["c"] = "译",
            ["p"] = "",
            ["s"] = false,
            ["mt"] = false,
            ["ct"] = new BsonDateTime(DateTime.UtcNow),
            ["e"] = new BsonDateTime(DateTime.UtcNow),
        });
    }
}
