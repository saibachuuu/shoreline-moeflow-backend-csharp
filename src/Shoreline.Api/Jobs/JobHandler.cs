using MongoDB.Bson;

namespace Shoreline.Api.Jobs;

// Handlers must be idempotent: an interrupted job is reclaimed after its lease expires.
public interface IJobHandler
{
    string Kind { get; }
    Task ExecuteAsync(BsonDocument payload, CancellationToken ct);
    Task ExecuteAsync(ObjectId jobId, int attempts, BsonDocument payload, CancellationToken ct) => ExecuteAsync(payload, ct);
}
