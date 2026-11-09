using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

// Separate from the team gate: accepting a team invitation enters both, in
// invitation -> team order. Serializes pending reuse and terminal transitions
// without imposing a new unique index on the legacy invitation collection.
// Like TeamMutationGate, this only supports one backend writer process.
public sealed class InvitationMutationGate
{
    private readonly TeamMutationGate gates = new();
    public Task<IDisposable> EnterAsync(ObjectId groupId, CancellationToken ct) => gates.EnterAsync(groupId, ct);
}
