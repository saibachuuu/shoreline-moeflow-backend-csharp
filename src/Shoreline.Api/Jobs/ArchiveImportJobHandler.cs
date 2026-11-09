using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Modules.ArchiveImport;

namespace Shoreline.Api.Jobs;

public sealed class ArchiveImportJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => ArchiveImportService.JobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ArchiveImportService>();
        await service.RunAsync(payload, ct);
    }
}
