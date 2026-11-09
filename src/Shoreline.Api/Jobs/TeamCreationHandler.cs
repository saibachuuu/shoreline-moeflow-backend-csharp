using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

public sealed class TeamCreationHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => "team-create";
    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TeamService>().ResumeCreationAsync(payload, ct);
    }
}
