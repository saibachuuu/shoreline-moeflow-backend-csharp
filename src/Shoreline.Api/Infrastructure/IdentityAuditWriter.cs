using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

public sealed class IdentityAuditWriter(MongoStore store, TimeProvider clock)
{
    public async Task RecordAsync(BsonDocument actor, string action, BsonDocument team, BsonDocument? member,
        BsonDocument before, BsonDocument after, string requestId, CancellationToken ct, string source = "runtime")
    {
        await store.Collection("identity_audit_event").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["actor"] = actor["_id"],
            ["scope"] = "team",
            ["action"] = action,
            ["project_id"] = BsonNull.Value,
            ["team_id"] = team["_id"].ToString(),
            ["member_id"] = member is null ? BsonNull.Value : member["_id"].ToString(),
            ["target_user_id"] = member?.GetValue("user", BsonNull.Value) is { IsObjectId: true } user ? user.ToString() : BsonNull.Value,
            ["request_id"] = requestId,
            ["source"] = source,
            ["before"] = before,
            ["after"] = after,
            ["permission_sources"] = new BsonDocument(),
            ["create_time"] = clock.GetUtcNow().UtcDateTime
        }, cancellationToken: ct);
    }
}
