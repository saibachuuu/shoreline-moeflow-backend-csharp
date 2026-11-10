using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Singleton job bridge: the worker is singleton, while ThumbnailService uses
/// scoped Mongo/label services. The handler must be idempotent because the
/// queue can redeliver an expired running job.
/// </summary>
public sealed class ThumbnailJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => ThumbnailService.JobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ThumbnailService>().RunAsync(payload, ct);
    }

    public async Task ExecuteAsync(ObjectId jobId, int attempts, BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ThumbnailService>().RunAsync(payload, attempts, ct);
    }
}
