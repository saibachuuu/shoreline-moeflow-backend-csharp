using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Celery <c>tasks.output_team_projects_task</c> equivalent. A fresh scope is
/// used per job so the scoped label context and storage router are not captured
/// by the singleton worker.
/// </summary>
public sealed class TeamOutputJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => OutputService.TeamJobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutputService>()
            .RunTeamExportAsync(payload, ct);
    }
}
