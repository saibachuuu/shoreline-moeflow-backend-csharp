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

    public async Task<ObjectId> RecordProjectAsync(BsonDocument actor, string action, BsonDocument project,
        BsonDocument? member, BsonDocument before, BsonDocument after, string requestId, CancellationToken ct,
        IReadOnlyDictionary<string, IEnumerable<string>>? permissionSources = null, string source = "runtime")
    {
        var id = ObjectId.GenerateNewId();
        var sources = new BsonDocument();
        if (permissionSources is not null)
            foreach (var (permission, values) in permissionSources)
                sources[permission] = new BsonArray(values.Select(value => (BsonValue)value).ToArray());
        await store.Collection("identity_audit_event").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["actor"] = actor["_id"],
            ["scope"] = "project",
            ["action"] = action,
            ["project_id"] = project["_id"].ToString(),
            ["team_id"] = BsonNull.Value,
            ["member_id"] = member is null ? BsonNull.Value : member["_id"].ToString(),
            ["target_user_id"] = member?.GetValue("user", BsonNull.Value) is { IsObjectId: true } user ? user.ToString() : BsonNull.Value,
            ["request_id"] = requestId,
            ["source"] = source,
            ["before"] = before,
            ["after"] = after,
            ["permission_sources"] = sources,
            ["create_time"] = clock.GetUtcNow().UtcDateTime
        }, cancellationToken: ct);
        return id;
    }
}
