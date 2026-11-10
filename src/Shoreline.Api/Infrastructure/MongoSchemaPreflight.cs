using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure;

public sealed record MongoSchemaIssue(string Code, string Message, BsonDocument Key, long Count);

public static class MongoSchemaPreflight
{
    // Read-only check for the legacy uniqueness contract used by team membership.
    public static async Task<IReadOnlyList<MongoSchemaIssue>> InspectTeamMemberDuplicatesAsync(
        MongoStore store, CancellationToken ct = default)
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = new BsonDocument { ["team"] = "$team", ["user"] = "$user" },
                ["count"] = new BsonDocument("$sum", 1)
            }),
            new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1)))
        };
        var groups = await store.Collection("team_member").Aggregate<BsonDocument>(pipeline)
            .ToListAsync(ct);
        return groups.Select(group => new MongoSchemaIssue(
            "duplicate-team-member",
            "Multiple team_member records share the team/user key required by the unique index.",
            group["_id"].AsBsonDocument,
            group["count"].ToInt64())).ToArray();
    }
}
