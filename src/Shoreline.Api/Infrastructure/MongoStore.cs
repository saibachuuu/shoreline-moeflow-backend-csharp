using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure;

// Preserve the Python BSON schema. A singleton client shares a small connection pool.
public sealed class MongoStore
{
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);

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
        // Index migration is not safe to run concurrently in the same process:
        // MongoDB rejects dropIndexes while another index build is in progress.
        await InitializationGate.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
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
        await DropIndexIfExistsAsync("team", "csharp_team_name_unique_v1", ct);
        await Collection("team").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("n"),
            new CreateIndexOptions { Name = "n_1", Unique = true }), cancellationToken: ct);
        await Collection("project_set").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("t").Ascending("d")), cancellationToken: ct);
        await Collection("csharp_captcha").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await Collection("team_member").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("team").Ascending("user"),
            new CreateIndexOptions { Unique = true, Name = "team_member_team_user_v1" }), cancellationToken: ct);
        // Drop the descriptive names used by earlier C# builds; MongoEngine
        // creates these keys under its default names at Python startup.
        foreach (var legacy in new[]
                 {
                     "team_member_status_v1", "team_member_aliases_v1",
                     "team_member_worker_qualifications_v1",
                 })
        {
            await DropIndexIfExistsAsync("team_member", legacy, ct);
        }
        await EnsureTeamMemberQueryIndexAsync(["team", "status"], "team_1_status_1", ct);
        await EnsureTeamMemberQueryIndexAsync(["team", "aliases"], "team_1_aliases_1", ct);
        await EnsureTeamMemberQueryIndexAsync(["team", "worker_qualifications"], "team_1_worker_qualifications_1", ct);
        await EnsureTeamMemberQueryIndexAsync(["team", "status", "asrch"], "team_member_alias_search_v1", ct);
        await Collection("identity_tag_policy").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("team"), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await DropIndexIfExistsAsync("project_member", "project_member_display_name_search_v1", ct);
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
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("status").Ascending("dns"),
                new CreateIndexOptions { Name = "project_member_display_name_search_v1" })
        ], cancellationToken: ct);
        await DropIndexIfExistsAsync("identity_operation", "identity_operation_project_operation_v1", ct);
        await Collection("identity_operation").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("project").Ascending("operation_id"),
            new CreateIndexOptions { Unique = true, Name = "project_1_operation_id_1" }), cancellationToken: ct);
    }

    private async Task DropIndexIfExistsAsync(string collection, string name, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await Collection(collection).Indexes.DropOneAsync(name, ct);
                return;
            }
            catch (MongoCommandException ex) when (ex.Code is 26 or 27)
            {
                // NamespaceNotFound / IndexNotFound: nothing to migrate.
                return;
            }
            catch (MongoCommandException ex) when (ex.Code == 125 && attempt < 5)
            {
                // BackgroundOperationInProgress: another initializer is building
                // an index on this collection; retry once it completes.
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task EnsureTeamMemberQueryIndexAsync(string[] fields, string name, CancellationToken ct)
    {
        var collection = Collection("team_member");
        var keys = new BsonDocument();
        foreach (var field in fields) keys[field] = 1;

        // Early C# builds used this name for a key that Python does not own
        // (`aliases_search`); the runtime queries `asrch`. The name belongs to
        // C#, so replace a mismatching definition instead of failing startup.
        var catalog = await (await collection.Indexes.ListAsync(ct)).ToListAsync(ct);
        var named = catalog.FirstOrDefault(index => index.GetValue("name", "") == name);
        if (named is not null && !named.GetValue("key", BsonNull.Value).Equals(keys))
        {
            await DropIndexIfExistsAsync("team_member", name, ct);
        }

        async Task<bool> HasEquivalentAsync()
        {
            var indexes = await (await collection.Indexes.ListAsync(ct)).ToListAsync(ct);
            var candidates = indexes.Where(index => index.GetValue("name", "") == name ||
                index.GetValue("key", BsonNull.Value).Equals(keys)).ToArray();
            foreach (var index in candidates)
            {
                // Only server metadata and the legacy background hint are ignored.
                // Unknown options are rejected rather than silently weakening the contract.
                if (!index.GetValue("key", BsonNull.Value).Equals(keys) ||
                    index.GetValue("unique", false).ToBoolean() || index.GetValue("sparse", false).ToBoolean() ||
                    index.Names.Any(option => option is not ("v" or "key" or "name" or "ns" or "background" or "unique" or "sparse")))
                    throw new InvalidOperationException($"Conflicting team_member index for {name}; no index was removed or replaced.");
            }
            return candidates.Length > 0;
        }

        if (await HasEquivalentAsync()) return;
        try
        {
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys,
                new CreateIndexOptions { Name = name }), cancellationToken: ct);
        }
        catch (MongoCommandException error) when (error.Code is 85 or 86)
        {
            // Another initializer may have created an equivalent spec under a different name.
            // Never ignore a conflict unless the current catalog proves equivalence.
            if (!await HasEquivalentAsync()) throw;
        }
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
