using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

[Collection("local-services-language")]
public sealed class LanguageTests(LocalServices services)
{
    [Theory]
    [InlineData("", false)]
    [InlineData("zh-CN", true)]
    [InlineData("zh-CN,en;q=0.5", false)]
    [InlineData("en-US,zh;q=0.9", true)]
    [InlineData("fr,*;q=0.7", true)]
    [InlineData("en;q=0,zh;q=0", false)]
    [InlineData("zh;q=0,*;q=1", false)]
    public void LocaleNegotiationMatchesLegacyWerkzeug(string header, bool chinese)
    {
        var accepted = string.IsNullOrEmpty(header) ? [] : StringWithQualityHeaderValue.ParseList(new[] { header });
        Assert.Equal(chinese, LanguageCatalog.ChinesePreferred(accepted));
    }

    [Fact]
    public async Task PublicCatalogUsesLegacyArrayShapeAndLocalizedNamesWithoutAuthentication()
    {
        var english = await services.Http.GetFromJsonAsync<JsonElement>("/v1/languages");
        Assert.Equal(JsonValueKind.Array, english.ValueKind);
        Assert.Equal(106, english.GetArrayLength());
        var first = english[0];
        Assert.Equal("ja", first.GetProperty("code").GetString());
        Assert.Equal("Japanese", first.GetProperty("lo_name").GetString());
        Assert.True(first.GetProperty("no_space").GetBoolean());
        Assert.Equal("ja", first.GetProperty("g_ocr_code").GetString());
        Assert.Equal("ja", first.GetProperty("g_tra_code").GetString());
        Assert.True(ObjectId.TryParse(first.GetProperty("id").GetString(), out _));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/languages");
        request.Headers.AcceptLanguage.ParseAdd("zh");
        using var response = await services.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var chinese = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("日语", chinese[0].GetProperty("lo_name").GetString());
        Assert.Equal("日语", chinese[0].GetProperty("i18n_name").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), chinese[0].GetProperty("id").GetString());
        Assert.Equal("pong", await services.Http.GetStringAsync("/ping"));
    }

    [Fact]
    public Task ExistingCatalogIsPreservedIncludingCustomIdsAndBlankCodeFiltering() => WithDatabaseAsync(async store =>
    {
        var languages = store.Collection("language");
        var row = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["co"] = "custom", ["e"] = "Custom", ["c"] = "Custom", ["s"] = 42 };
        await languages.InsertManyAsync(new[] { row, new BsonDocument("co", "") });
        var catalog = new LanguageCatalog(store);
        await catalog.InitializeAsync();
        Assert.Equal(2, await languages.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(row, await languages.Find(new BsonDocument("_id", row["_id"])).FirstAsync());
        var result = JsonSerializer.SerializeToElement(await catalog.ListAsync(false));
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("Custom", result[0].GetProperty("lo_name").GetString());
        Assert.Equal(row["_id"].ToString(), result[0].GetProperty("id").GetString());
    });

    [Fact]
    public Task ConcurrentInitializationAndInterruptedSeedResumeWithoutReplacingRows() => WithDatabaseAsync(async store =>
    {
        var languages = store.Collection("language");
        var japanese = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["co"] = "ja", ["c"] = "保留名称", ["e"] = "Keep me", ["s"] = 0 };
        await languages.InsertOneAsync(japanese);
        await store.Collection("csharp_seed").InsertOneAsync(new BsonDocument { ["_id"] = "languages-v1", ["complete"] = false });
        var catalog = new LanguageCatalog(store);
        await Task.WhenAll(catalog.InitializeAsync(), catalog.InitializeAsync());
        Assert.Equal(106, await languages.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(japanese, await languages.Find(new BsonDocument("co", "ja")).FirstAsync());
        Assert.True((await store.Collection("csharp_seed").Find(new BsonDocument("_id", "languages-v1")).FirstAsync()).Flag("complete"));
        var before = await languages.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("co", 1)).ToListAsync();
        await catalog.InitializeAsync();
        Assert.Equal(before, await languages.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("co", 1)).ToListAsync());
    });

    private static async Task WithDatabaseAsync(Func<MongoStore, Task> action)
    {
        var name = "shoreline_csharp_language_test_" + Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Mongo:Uri"] = "mongodb://127.0.0.1:" + TestEnvironment.MongoPort + "/" + name }).Build();
        try { await action(new MongoStore(configuration)); }
        finally { await new MongoClient("mongodb://127.0.0.1:" + TestEnvironment.MongoPort).DropDatabaseAsync(name); }
    }
}
