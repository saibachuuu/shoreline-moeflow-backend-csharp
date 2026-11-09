using MongoDB.Bson;
using Shoreline.Api.Modules.ZitengPartner;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Replaces the Celery task <c>tasks.ziteng_partner.check_project</c>. The payload
/// is private to the module: <c>{ projectId, generation }</c>. Every run is
/// idempotent; an old generation or an already-claimed check is skipped.
/// </summary>
public sealed class ZitengCheckJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public const string JobKind = "ziteng_check";

    public string Kind => JobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        var projectId = payload.Text("projectId", payload.Text("project_id"));
        if (projectId.Length == 0) return;
        var generation = payload.Integer("generation");
        var owner = "ziteng:" + Environment.MachineName + ":" + Guid.NewGuid().ToString("N");
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ZitengPartnerService>();
        await service.RunAsync(projectId, generation, owner, ct);
    }
}
