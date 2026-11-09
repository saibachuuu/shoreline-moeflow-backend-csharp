using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Singleton job bridge. A fresh DI scope is created for every job so the
/// scoped label context and output services are never captured by the worker.
/// </summary>
public sealed class OutputJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => OutputService.JobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OutputExportService>();
        await service.RunAsync(payload, ct);
    }
}
