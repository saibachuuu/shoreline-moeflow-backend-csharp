using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

public sealed class MongoSchemaReadinessTests : IAsyncLifetime
{
    private readonly string database = "shoreline_schema_test_" + Guid.NewGuid().ToString("N");
    private MongoStore store = null!;

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = TestEnvironment.MongoUri(database)
        }).Build();
        store = new MongoStore(configuration);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() =>
        await new MongoClient(TestEnvironment.MongoUri(database)).DropDatabaseAsync(database);

    [Theory]
    [InlineData(null)]
    [InlineData("asrch")]
    public async Task InitializationIsIdempotentAndPreservesConflictingLegacyIndex(string? existingKey)
    {
        var rows = store.Collection("team_member");
        if (existingKey is not null)
        {
            await rows.Indexes.CreateManyAsync([
                new(new BsonDocument { ["team"] = 1, ["aliases"] = 1 },
                    new CreateIndexOptions { Name = "team_member_aliases_v1", Background = true }),
                new(new BsonDocument { ["team"] = 1, ["worker_qualifications"] = 1 },
                    new CreateIndexOptions { Name = "team_member_worker_qualifications_v1", Background = true })
            ]);
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { ["team"] = 1, ["status"] = 1, [existingKey] = 1 },
                new CreateIndexOptions { Name = "team_member_alias_search_v1", Background = true }));
        }

        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeAsync(CancellationToken.None);

        var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
        AssertIndex(indexes, "team_member_aliases_v1", new BsonDocument { ["team"] = 1, ["aliases"] = 1 });
        AssertIndex(indexes, "team_member_worker_qualifications_v1", new BsonDocument { ["team"] = 1, ["worker_qualifications"] = 1 });
        AssertIndex(indexes, "team_member_alias_search_v1",
            new BsonDocument { ["team"] = 1, ["status"] = 1, ["asrch"] = 1 });
    }

    [Fact]
    public async Task InitializationRefusesToReplaceAConflictingAliasSearchIndex()
    {
        var rows = store.Collection("team_member");
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { ["team"] = 1, ["status"] = 1, ["aliases_search"] = 1 },
            new CreateIndexOptions { Name = "team_member_alias_search_v1" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(CancellationToken.None));

        var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
        AssertIndex(indexes, "team_member_alias_search_v1",
            new BsonDocument { ["team"] = 1, ["status"] = 1, ["aliases_search"] = 1 });
    }

    [Fact]
    public async Task InitializationReusesEquivalentGeneratedNames()
    {
        var rows = store.Collection("team_member");
        await rows.Indexes.CreateManyAsync([
            new(new BsonDocument { ["team"] = 1, ["aliases"] = 1 }),
            new(new BsonDocument { ["team"] = 1, ["worker_qualifications"] = 1 })
        ]);
        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeAsync(CancellationToken.None);
        var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
        AssertIndex(indexes, "team_1_aliases_1", new BsonDocument { ["team"] = 1, ["aliases"] = 1 });
        AssertIndex(indexes, "team_1_worker_qualifications_1", new BsonDocument { ["team"] = 1, ["worker_qualifications"] = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentInitializersReuseEquivalentIndexes(bool existing)
    {
        var rows = store.Collection("team_member");
        if (existing)
            await rows.Indexes.CreateManyAsync([
                new(new BsonDocument { ["team"] = 1, ["aliases"] = 1 }),
                new(new BsonDocument { ["team"] = 1, ["worker_qualifications"] = 1 })
            ]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.InitializeAsync(CancellationToken.None)));
        await store.InitializeAsync(CancellationToken.None);
        var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
        AssertIndex(indexes, existing ? "team_1_aliases_1" : "team_member_aliases_v1", new BsonDocument { ["team"] = 1, ["aliases"] = 1 });
        AssertIndex(indexes, existing ? "team_1_worker_qualifications_1" : "team_member_worker_qualifications_v1", new BsonDocument { ["team"] = 1, ["worker_qualifications"] = 1 });
    }

    [Theory]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("wrong-key")]
    public async Task ConflictingIndexFailsClosedWithoutDroppingIt(string kind)
    {
        var rows = store.Collection("team_member");
        var keys = new BsonDocument { ["team"] = 1, [kind == "wrong-key" ? "other" : "aliases"] = 1 };
        var options = new CreateIndexOptions<BsonDocument>
        {
            Name = kind == "wrong-key" ? "team_member_aliases_v1" : "old_alias_index",
            Unique = kind == "unique",
            Sparse = kind == "sparse",
            PartialFilterExpression = kind == "partial" ? new BsonDocument("aliases", new BsonDocument("$exists", true)) : null
        };
        await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys, options));
        var before = (await (await rows.Indexes.ListAsync()).ToListAsync()).Single(index => index["name"] == options.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(CancellationToken.None));
        var after = (await (await rows.Indexes.ListAsync()).ToListAsync()).Single(index => index["name"] == options.Name);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DuplicatePreflightReportsWithoutModifyingRecords()
    {
        var teamId = ObjectId.GenerateNewId();
        var userId = ObjectId.GenerateNewId();
        var rows = store.Collection("team_member");
        BsonDocument[] original = [
            new() { ["_id"] = ObjectId.GenerateNewId(), ["team"] = teamId, ["user"] = userId, ["base_tag"] = "member" },
            new() { ["_id"] = ObjectId.GenerateNewId(), ["team"] = teamId, ["user"] = userId, ["base_tag"] = "admin" }
        ];
        await rows.InsertManyAsync(original);
        var before = await rows.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();

        var issues = await MongoSchemaPreflight.InspectTeamMemberDuplicatesAsync(store);

        var issue = Assert.Single(issues);
        Assert.Equal("duplicate-team-member", issue.Code);
        Assert.Equal(teamId, issue.Key["team"].AsObjectId);
        Assert.Equal(userId, issue.Key["user"].AsObjectId);
        Assert.Equal(2, issue.Count);
        Assert.Equal(before, await rows.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync());
    }

    private static void AssertIndex(List<BsonDocument> indexes, string name, BsonDocument keys)
    {
        var index = Assert.Single(indexes, item => item["name"] == name);
        Assert.Equal(keys, index["key"].AsBsonDocument);
        Assert.False(index.GetValue("unique", false).ToBoolean());
        Assert.False(index.GetValue("sparse", false).ToBoolean());
        Assert.False(index.Contains("partialFilterExpression"));
    }
}
