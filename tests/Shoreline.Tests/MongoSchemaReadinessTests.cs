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
    [InlineData("aliases_search")]
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
        if (existingKey is null)
            Assert.DoesNotContain(indexes, index => index["name"] == "team_member_alias_search_v1");
        else
            AssertIndex(indexes, "team_member_alias_search_v1", new BsonDocument { ["team"] = 1, ["status"] = 1, [existingKey] = 1 });
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
