using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Xunit;

namespace Shoreline.Tests;

// The Python models create the identity indexes with mongoengine's option
// shapes; MongoDB refuses a second index carrying the same name but different
// options ("Index with name: ... already exists with different options"), and
// an unhandled createIndexes failure aborts startup. A C# takeover of a live
// database must survive the legacy definitions.
public sealed class MongoStoreIndexCompatTests
{
    [Fact]
    public async Task Initialize_AcceptsLegacyIdentityIndexDefinitions()
    {
        var database = "mongo_store_legacy_indexes_" + Guid.NewGuid().ToString("N");
        var store = new MongoStore(BuildConfiguration(database));
        try
        {
            var members = store.Collection("project_member");
            await members.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ik"),
                new CreateIndexOptions { Unique = true, Name = "project_member_identity_key_v1" }));
            await members.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("user"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true, Name = "project_member_project_user_v1",
                    PartialFilterExpression = new BsonDocument("user", new BsonDocument("$type", "objectId")),
                }));
            await members.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("external_id"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true, Name = "project_member_project_external_v1",
                    PartialFilterExpression = new BsonDocument("external_id", new BsonDocument("$type", "string")),
                }));

            await store.InitializeAsync(CancellationToken.None);

            var indexes = await (await members.Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, index => index["name"].AsString == "project_member_project_user_v1");
            Assert.Contains(indexes, index => index["name"].AsString == "project_member_project_external_v1");
            var identity = Assert.Single(indexes, index => index["name"].AsString == "project_member_identity_key_v1");
            Assert.True(identity.Contains("partialFilterExpression"));
        }
        finally
        {
            await store.Database.Client.DropDatabaseAsync(database);
        }
    }

    private static IConfiguration BuildConfiguration(string database) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = TestEnvironment.MongoUri(database),
            })
            .Build();
}
