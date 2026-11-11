using MongoDB.Bson;

namespace Shoreline.Api.Modules.StoragePool;

internal static class StoragePoolTransferView
{
    public static int Progress(BsonDocument transfer)
    {
        var status = StoragePoolBson.Int(transfer, "st");
        if (status == StoragePoolConstants.Done) return 100;
        var total = StoragePoolBson.Int(transfer, "to");
        if (total == 0) return status == StoragePoolConstants.Switched ? 95 : 0;
        var processed = StoragePoolBson.Int(transfer, "processed");
        var skipped = StoragePoolBson.Int(transfer, "skipped");
        var copied = Math.Min(90, (int)((long)processed * 90 / total));
        var real = Math.Max(1, total - skipped);
        if (status == StoragePoolConstants.Switched)
            return Math.Min(99, 95 + (int)((long)StoragePoolBson.Int(transfer, "deleted") * 4 / real));
        if (status == StoragePoolConstants.Verified)
            return Math.Min(94, 90 + (int)((long)StoragePoolBson.Int(transfer, "verified") * 4 / real));
        return copied;
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
            ["processed_objects"] = StoragePoolBson.Int(transfer, "processed"),
            ["skipped_objects"] = StoragePoolBson.Int(transfer, "skipped"),
            ["verified_objects"] = StoragePoolBson.Int(transfer, "verified"),
            ["deleted_objects"] = StoragePoolBson.Int(transfer, "deleted"),
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
