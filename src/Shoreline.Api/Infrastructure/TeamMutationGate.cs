using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

// A fixed-size gate table avoids keeping a semaphore for every team forever.
// Shared by HTTP and jobs in the one business process. This is not a distributed
// lock: do not run a second backend writer against the same database.
public sealed class TeamMutationGate
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> EnterAsync(ObjectId teamId, CancellationToken ct)
    {
        var gate = gates[(int)((uint)teamId.GetHashCode() % (uint)gates.Length)];
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10), ct))
            throw IdentityErrors.InProgress();
        return new Holder(gate);
    }

    private sealed class Holder(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? held = gate;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
