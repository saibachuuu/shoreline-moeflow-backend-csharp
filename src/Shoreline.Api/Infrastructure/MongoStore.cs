using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure;

// Preserve the Python BSON schema. A singleton client shares a small connection pool.
public sealed class MongoStore
{
    public IMongoDatabase Database { get; }
    public MongoStore(IConfiguration configuration)
    {
        var uri = configuration["Mongo:Uri"] ?? throw new InvalidOperationException("Mongo:Uri is required");
        var url = MongoUrl.Create(uri);
        if (string.IsNullOrEmpty(url.DatabaseName))
            throw new InvalidOperationException("Mongo:Uri must include a database name");
        var settings = MongoClientSettings.FromUrl(url);
        settings.MaxConnectionPoolSize = 20;
        settings.MinConnectionPoolSize = 0;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ApplicationName = "shoreline-csharp";
        Database = new MongoClient(settings).GetDatabase(url.DatabaseName);
    }

    public IMongoCollection<BsonDocument> Collection(string name) => Database.GetCollection<BsonDocument>(name);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await Collection("site_setting").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("n"), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        var users = Collection("user");
        await users.Indexes.CreateManyAsync([
            new(Builders<BsonDocument>.IndexKeys.Ascending("e"), new CreateIndexOptions { Unique = true }),
            new(Builders<BsonDocument>.IndexKeys.Ascending("n"), new CreateIndexOptions { Unique = true })
        ], ct);
        await Collection("csharp_job").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("state").Ascending("available_at")),
            cancellationToken: ct);
        await Collection("csharp_job").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("code_key"),
                new CreateIndexOptions<BsonDocument> { Unique = true, PartialFilterExpression = new BsonDocument("code_key", new BsonDocument("$exists", true)) }),
            cancellationToken: ct);
        await Collection("csharp_job").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("purge_at"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await Collection("csharp_captcha").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
    }
}

public static class BsonFields
{
    public static string Text(this BsonDocument d, string key, string fallback = "") =>
        d.TryGetValue(key, out var value) && value.IsString ? value.AsString : fallback;
    public static bool Flag(this BsonDocument d, string key) =>
        d.TryGetValue(key, out var value) && value.IsBoolean && value.AsBoolean;
    public static ObjectId Id(string value) => ObjectId.TryParse(value, out var id)
        ? id : throw ApiError.Invalid("Invalid object id");
}
