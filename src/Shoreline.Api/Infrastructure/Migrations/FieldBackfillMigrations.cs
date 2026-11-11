using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

/// <summary>m0002: thumbnail lifecycle fields for image files.</summary>
public sealed class M0002ThumbnailStatus : LegacyMigration
{
    public override string Version => "0002";
    public override string Name => "thumbnail_status";
    public override string Description => "Add explicit thumbnail lifecycle fields to image files";
    protected override string ChecksumSpec => "file:{t:2}:set th=0,the=\"\"";

    public override async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        var files = MigrationBson.Collection(database, "file");
        await files.UpdateManyAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("t", 2),
                Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("th"))),
            Builders<BsonDocument>.Update.Set("th", 0).Set("the", string.Empty),
            cancellationToken: ct);
    }

    public override async Task DownAsync(IMongoDatabase database, CancellationToken ct)
    {
        var files = MigrationBson.Collection(database, "file");
        await files.UpdateManyAsync(
            FilterDefinition<BsonDocument>.Empty,
            Builders<BsonDocument>.Update.Unset("th").Unset("the").Unset("tht"),
            cancellationToken: ct);
    }

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var files = MigrationBson.Collection(database, "file");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("t", 2),
            Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("th")));
        return await files.CountDocumentsAsync(filter, cancellationToken: ct) == 0;
    }
}

/// <summary>m0007: backfill missing and null tags on project members.</summary>
public sealed class M0007ProjectMemberTags : LegacyMigration
{
    public override string Version => "0007";
    public override string Name => "project_member_tags";
    public override string Description => "Backfill missing and null tags on project members";
    protected override string ChecksumSpec => "project_member:tags missing-or-null := []";

    private static FilterDefinition<BsonDocument> MissingTagsFilter() =>
        Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("tags")),
            Builders<BsonDocument>.Filter.Eq("tags", BsonNull.Value));

    public override async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        var members = MigrationBson.Collection(database, "project_member");
        await members.UpdateManyAsync(
            MissingTagsFilter(),
            Builders<BsonDocument>.Update.Set("tags", new BsonArray()),
            cancellationToken: ct);
    }

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var members = MigrationBson.Collection(database, "project_member");
        return await members.CountDocumentsAsync(MissingTagsFilter(), cancellationToken: ct) == 0;
    }
}

/// <summary>
/// m0005: normalize identity-member subject indexes to <c>$type</c>-based
/// partial filters (nullable optional fields are omitted rather than stored
/// as null), and drop explicit null placeholders.
/// </summary>
public sealed class M0005IdentityMemberIndexOptions : LegacyMigration
{
    public const string UserIndexName = "project_member_project_user_v1";
    public const string ExternalIndexName = "project_member_project_external_v1";

    public static readonly BsonDocument UserIndexKeys =
        MigrationBson.Keys(("project", 1), ("user", 1));
    public static readonly BsonDocument ExternalIndexKeys =
        MigrationBson.Keys(("project", 1), ("external_id", 1));

    public static readonly BsonDocument UserPartial =
        new("user", new BsonDocument("$type", "objectId"));
    public static readonly BsonDocument ExternalPartial =
        new("external_id", new BsonDocument("$type", "string"));

    public override string Version => "0005";
    public override string Name => "identity_member_index_options";
    public override string Description => "Make identity member subject indexes ignore null optional fields";
    protected override string ChecksumSpec =>
        $"project_member:{UserIndexName}:{UserIndexKeys}:{UserPartial};" +
        $"project_member:{ExternalIndexName}:{ExternalIndexKeys}:{ExternalPartial}";

    public override async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        var members = MigrationBson.Collection(database, "project_member");
        await members.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("external_id", BsonNull.Value),
            Builders<BsonDocument>.Update.Unset("external_id"),
            cancellationToken: ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            UserIndexKeys, UserIndexName, unique: true, UserPartial, ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            ExternalIndexKeys, ExternalIndexName, unique: true, ExternalPartial, ct);
    }

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var indexes = await MigrationBson.ListIndexesSafeAsync(
            MigrationBson.Collection(database, "project_member"), ct);
        return HasIndex(indexes, UserIndexName, UserIndexKeys, UserPartial) &&
            HasIndex(indexes, ExternalIndexName, ExternalIndexKeys, ExternalPartial);
    }

    internal static bool HasIndex(
        List<BsonDocument> indexes, string name, BsonDocument keys, BsonDocument options)
    {
        var index = indexes.FirstOrDefault(i => i.GetValue("name", "").AsString == name);
        if (index is null) return false;
        if (!index.GetValue("key", BsonNull.Value).Equals(keys)) return false;
        if (!index.GetValue("unique", false).ToBoolean()) return false;
        var partial = index.TryGetValue("partialFilterExpression", out var expression)
            ? expression : null;
        return partial is not null && partial.Equals(options);
    }
}
