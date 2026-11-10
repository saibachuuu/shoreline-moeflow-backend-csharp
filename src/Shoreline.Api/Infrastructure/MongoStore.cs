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
        await Collection("csharp_job").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("reserved_name"), new CreateIndexOptions<BsonDocument>
            { Unique = true, PartialFilterExpression = new BsonDocument("reserved_name", new BsonDocument("$exists", true)) }), cancellationToken: ct);
        await Collection("team").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("n"), new CreateIndexOptions<BsonDocument>
            { Name = "csharp_team_name_unique_v1", Unique = true, PartialFilterExpression = new BsonDocument("n", new BsonDocument("$type", "string")) }), cancellationToken: ct);
        await Collection("project_set").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("t").Ascending("d")), cancellationToken: ct);
        await Collection("csharp_captcha").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await Collection("team_member").Indexes.CreateManyAsync([
            new(Builders<BsonDocument>.IndexKeys.Ascending("team").Ascending("user"),
                new CreateIndexOptions { Unique = true, Name = "team_member_team_user_v1" }),
            new(Builders<BsonDocument>.IndexKeys.Ascending("team").Ascending("status"),
                new CreateIndexOptions { Name = "team_member_status_v1" }),
            new(Builders<BsonDocument>.IndexKeys.Ascending("team").Ascending("aliases"),
                new CreateIndexOptions { Name = "team_member_aliases_v1" }),
            new(Builders<BsonDocument>.IndexKeys.Ascending("team").Ascending("worker_qualifications"),
                new CreateIndexOptions { Name = "team_member_worker_qualifications_v1" })
        ], ct);
        await Collection("identity_tag_policy").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("team"), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await Collection("project_member").Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ik"),
                new CreateIndexOptions<BsonDocument> { Unique = true, Name = "project_member_identity_key_v1",
                    PartialFilterExpression = new BsonDocument("ik", new BsonDocument("$type", "string")) }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("user"),
                new CreateIndexOptions<BsonDocument> { Unique = true, Name = "project_member_project_user_v1",
                    PartialFilterExpression = new BsonDocument("user", new BsonDocument("$type", "objectId")) }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("external_id"),
                new CreateIndexOptions<BsonDocument> { Unique = true, Name = "project_member_project_external_v1",
                    PartialFilterExpression = new BsonDocument("external_id", new BsonDocument("$type", "string")) }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("status"),
                new CreateIndexOptions { Name = "project_member_status_v1" }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("tags"),
                new CreateIndexOptions { Name = "project_member_tags_v1" }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("display_name"),
                new CreateIndexOptions { Name = "project_member_display_name_v1" }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("status").Ascending("display_name_search"),
                new CreateIndexOptions { Name = "project_member_display_name_search_v1" })
        ], cancellationToken: ct);
        await Collection("identity_operation").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("operation_id"),
            new CreateIndexOptions { Unique = true, Name = "identity_operation_project_operation_v1" }), cancellationToken: ct);
    }
}

public static class BsonFields
{
    public static string Text(this BsonDocument d, string key, string fallback = "") =>
        d.TryGetValue(key, out var value) && value.IsString ? value.AsString : fallback;
    public static bool Flag(this BsonDocument d, string key) =>
        d.TryGetValue(key, out var value) && value.IsBoolean && value.AsBoolean;
    public static long Integer(this BsonDocument d, string key, long fallback = 0) =>
        d.TryGetValue(key, out var value) && value.IsNumeric ? value.ToInt64() : fallback;
    public static string Timestamp(this BsonDocument d, string key) =>
        d.TryGetValue(key, out var value) && value.IsBsonDateTime
            ? value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture) : "";
    public static ObjectId Id(string value) => ObjectId.TryParse(value, out var id)
        ? id : throw ApiError.Invalid("Invalid object id");
}
