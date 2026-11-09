using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Resolves the scoped import service in its own scope, matching
/// TeamCreationHandler. The job body never touches a broker directly and never
/// reports success unless RunImportAsync completed.
/// </summary>
public sealed class ProjectImportJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => ProjectImportService.JobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ProjectImportService>().RunImportAsync(payload, ct);
    }
}
