using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

/// <summary>
/// m0009: backfill project storage usage from the per-file cache and
/// normalize the legacy output filename key.
/// </summary>
public sealed class M0009StorageUsageBackfill : LegacyMigration
{
    public override string Version => "0009";
    public override string Name => "storage_usage_backfill";
    public override string Description => "Backfill Project.file_size and normalise legacy output filename key";
    protected override string ChecksumSpec =>
        "output:fn->file_name;project:fs=sum(file.fs where fs>0) only-when missing-or-zero";

    public override async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        var outputs = MigrationBson.Collection(database, "output");
        var legacyCursor = await outputs.Find(
            Builders<BsonDocument>.Filter.Exists("fn")).ToCursorAsync(ct);
        using (legacyCursor)
        {
            while (await legacyCursor.MoveNextAsync(ct))
            {
                foreach (var legacy in legacyCursor.Current)
                {
                    // $rename behaves inconsistently across server versions when
                    // the target key already exists, so unset/set per document.
                    if (legacy.Contains("file_name"))
                    {
                        await outputs.UpdateOneAsync(
                            Builders<BsonDocument>.Filter.Eq("_id", legacy["_id"]),
                            Builders<BsonDocument>.Update.Unset("fn"),
                            cancellationToken: ct);
                    }
                    else
                    {
                        await outputs.UpdateOneAsync(
                            Builders<BsonDocument>.Filter.Eq("_id", legacy["_id"]),
                            Builders<BsonDocument>.Update
                                .Set("file_name", legacy["fn"])
                                .Unset("fn"),
                            cancellationToken: ct);
                    }
                }
            }
        }

        var totals = await MigrationBson.Collection(database, "file").Aggregate<BsonDocument>(
            new BsonDocument[]
            {
                new("$match", new BsonDocument("fs", new BsonDocument("$gt", 0))),
                new("$group", new BsonDocument { ["_id"] = "$p", ["total"] = new BsonDocument("$sum", "$fs") }),
            }, cancellationToken: ct).ToListAsync(ct);
        foreach (var row in totals)
        {
            if (!row.TryGetValue("_id", out var projectId) || projectId.IsBsonNull) continue;
            if (!row.TryGetValue("total", out var total) || !total.IsNumeric) continue;
            var bytes = total.ToInt64();
            if (bytes <= 0) continue;
            await MigrationBson.Collection(database, "project").UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", projectId),
                    Builders<BsonDocument>.Filter.Or(
                        Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("fs")),
                        Builders<BsonDocument>.Filter.Eq("fs", 0),
                        Builders<BsonDocument>.Filter.Eq("fs", BsonNull.Value))),
                Builders<BsonDocument>.Update.Set("fs", bytes),
                cancellationToken: ct);
        }
    }

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var outputs = MigrationBson.Collection(database, "output");
        if (await outputs.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Exists("fn"), cancellationToken: ct) != 0)
            return false;

        var totals = await MigrationBson.Collection(database, "file").Aggregate<BsonDocument>(
            new BsonDocument[]
            {
                new("$match", new BsonDocument("fs", new BsonDocument("$gt", 0))),
                new("$group", new BsonDocument { ["_id"] = "$p", ["total"] = new BsonDocument("$sum", "$fs") }),
            }, cancellationToken: ct).ToListAsync(ct);
        var staleProjects = totals
            .Where(row => row.TryGetValue("_id", out var id) && !id.IsBsonNull &&
                row.TryGetValue("total", out var total) && total.IsNumeric && total.ToInt64() > 0)
            .Select(row => row["_id"])
            .ToArray();
        if (staleProjects.Length == 0) return true;
        return await MigrationBson.Collection(database, "project").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.In("_id", staleProjects),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("fs")),
                    Builders<BsonDocument>.Filter.Eq("fs", 0),
                    Builders<BsonDocument>.Filter.Eq("fs", BsonNull.Value))),
            cancellationToken: ct) == 0;
    }
}
