using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

// Ports of the index-only legacy migrations plus the baseline. Index creation
// with an identical name and key specification is a server-side no-op, so
// these are idempotent against both Python-created and C#-startup-created
// indexes (InitializeFileIndexesAsync already owns file_list_v2,
// file_admin_edit_time_v1 and file_manual_order_v1).

/// <summary>m0000: the existing schema is the baseline; no data operation.</summary>
public sealed class M0000Baseline : LegacyMigration
{
    public override string Version => "0000";
    public override string Name => "baseline";
    public override string Description => "Establish the versioned migration baseline";

    public override Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
        Task.CompletedTask;

    public override Task DownAsync(IMongoDatabase database, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>m0001: compound index for the hot project-file list path.</summary>
public sealed class M0001FileListIndex : LegacyMigration
{
    public const string IndexName = "file_list_v2";

    public static readonly BsonDocument IndexKeys =
        MigrationBson.Keys(("p", 1), ("ac", 1), ("f", 1), ("dn", 1), ("t", 1), ("sn", 1));

    public override string Version => "0001";
    public override string Name => "file_list_index";
    public override string Description => "Add the compound index used by the default file-list query";
    protected override string ChecksumSpec => $"file:{IndexName}:{IndexKeys}";

    public override Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
        MigrationBson.EnsureNamedIndexAsync(database, "file", IndexKeys, IndexName,
            unique: false, partialFilter: null, ct);

    public override async Task DownAsync(IMongoDatabase database, CancellationToken ct) =>
        await MigrationBson.Collection(database, "file").Indexes.DropOneAsync(IndexName, ct);

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var indexes = await MigrationBson.ListIndexesSafeAsync(
            MigrationBson.Collection(database, "file"), ct);
        return indexes.Any(index => index.GetValue("name", "").AsString == IndexName);
    }
}

/// <summary>m0003: descending edit-time index for the administrator file list.</summary>
public sealed class M0003AdminFileEditTimeIndex : LegacyMigration
{
    public const string IndexName = "file_admin_edit_time_v1";

    public static readonly BsonDocument IndexKeys = MigrationBson.Keys(("et", -1));

    public override string Version => "0003";
    public override string Name => "admin_file_edit_time_index";
    public override string Description => "Add the descending edit-time index used by the administrator file list";
    protected override string ChecksumSpec => $"file:{IndexName}:{IndexKeys}";

    public override Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
        MigrationBson.EnsureNamedIndexAsync(database, "file", IndexKeys, IndexName,
            unique: false, partialFilter: null, ct);

    public override async Task DownAsync(IMongoDatabase database, CancellationToken ct) =>
        await MigrationBson.Collection(database, "file").Indexes.DropOneAsync(IndexName, ct);

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var indexes = await MigrationBson.ListIndexesSafeAsync(
            MigrationBson.Collection(database, "file"), ct);
        return indexes.Any(index => index.GetValue("name", "").AsString == IndexName);
    }
}

/// <summary>m0008: directory-local manual file ordering index.</summary>
public sealed class M0008FileManualOrder : LegacyMigration
{
    public const string IndexName = "file_manual_order_v1";

    public static readonly BsonDocument IndexKeys = MigrationBson.Keys(
        ("p", 1), ("ac", 1), ("f", 1), ("dn", 1), ("t", 1), ("mo", 1), ("sn", 1), ("_id", 1));

    public override string Version => "0008";
    public override string Name => "file_manual_order";
    public override string Description => "Add directory-local manual file ordering index";
    protected override string ChecksumSpec => $"file:{IndexName}:{IndexKeys}";

    public override Task UpAsync(IMongoDatabase database, CancellationToken ct) =>
        MigrationBson.EnsureNamedIndexAsync(database, "file", IndexKeys, IndexName,
            unique: false, partialFilter: null, ct);

    public override async Task DownAsync(IMongoDatabase database, CancellationToken ct) =>
        await MigrationBson.Collection(database, "file").Indexes.DropOneAsync(IndexName, ct);

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var indexes = await MigrationBson.ListIndexesSafeAsync(
            MigrationBson.Collection(database, "file"), ct);
        return indexes.Any(index => index.GetValue("name", "").AsString == IndexName);
    }
}
