using MongoDB.Bson;
using Microsoft.Extensions.DependencyInjection;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Durable bake handler for proofread feedback. JobQueue owns the retry/lease
/// loop; the service re-checks draft state, revocation and label revision on
/// every attempt, so a crash before the final CAS simply retries.
/// </summary>
public sealed class ProofreadDraftJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public const string JobKind = "proofread_draft_bake";

    public string Kind => JobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<Features.ProofreadFeedbackService>();
        await service.BakeAsync(payload, ct);
    }
}
