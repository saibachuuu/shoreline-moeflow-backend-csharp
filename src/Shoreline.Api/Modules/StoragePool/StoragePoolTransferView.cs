using MongoDB.Bson;

namespace Shoreline.Api.Modules.StoragePool;

internal static class StoragePoolTransferView
{
    // app/modules/storage_pool/models.py:180-183. The C# pipeline tracks
    // processed/verified counters too, but legacy progress_percent only uses
    // copied_objects, so upgrading the percentage would change the admin UI.
    public static int Progress(BsonDocument transfer)
    {
        var total = StoragePoolBson.Int(transfer, "to");
        if (total == 0) return 0;
        return Math.Min(100, (int)((long)StoragePoolBson.Int(transfer, "co") * 100 / total));
    }

    public static BsonDocument ToApi(BsonDocument transfer, BsonDocument? project, BsonDocument? source,
        BsonDocument? target)
    {
        var direction = StoragePoolBson.Int(transfer, "dir");
        var breakdown = transfer.TryGetValue("bd", out var value) && value.IsBsonDocument
            ? value.AsBsonDocument
            : new BsonDocument();
        return new BsonDocument
        {
            ["id"] = StoragePoolBson.ObjectId(transfer, "_id")?.ToString() ?? "",
            ["project"] = RefView(project),
            ["source"] = RefView(source),
            // Legacy models.StorageTransfer.to_api(): MOVE uses the stored target,
            // IN falls back to the source (the destination of a migration), OUT
            // has no target. The admin console renders this ref verbatim.
            ["target_source"] = direction switch
            {
                StoragePoolConstants.DirectionMove => RefView(target),
                StoragePoolConstants.DirectionIn => RefView(source),
                _ => BsonNull.Value,
            },
            ["status"] = StoragePoolBson.Int(transfer, "st"),
            ["status_name"] = StoragePoolConstants.StatusName(StoragePoolBson.Int(transfer, "st")),
            ["direction"] = direction,
            ["direction_name"] = StoragePoolConstants.DirectionName(direction),
            ["total_objects"] = StoragePoolBson.Int(transfer, "to"),
            ["copied_objects"] = StoragePoolBson.Int(transfer, "co"),
            ["total_bytes"] = StoragePoolBson.Long(transfer, "tb"),
            ["copied_bytes"] = StoragePoolBson.Long(transfer, "cb"),
            ["breakdown"] = new BsonDocument
            {
                ["origin_bytes"] = StoragePoolBson.Long(breakdown, "ob"),
                ["output_bytes"] = StoragePoolBson.Long(breakdown, "ov"),
                ["object_count"] = StoragePoolBson.Int(breakdown, "oc"),
            },
            ["progress_percent"] = Progress(transfer),
            ["start_time"] = StoragePoolBson.Iso(transfer, "stt"),
            ["finish_time"] = StoragePoolBson.Iso(transfer, "ftt"),
            ["create_time"] = StoragePoolBson.Iso(transfer, "ct"),
            ["last_error"] = StoragePoolBson.String(transfer, "le"),
        };
    }

    private static BsonValue RefView(BsonDocument? document)
    {
        if (document is null) return BsonNull.Value;
        return new BsonDocument
        {
            ["id"] = StoragePoolBson.ObjectId(document, "_id")?.ToString() ?? "",
            ["name"] = StoragePoolBson.String(document, "n"),
        };
    }
}
