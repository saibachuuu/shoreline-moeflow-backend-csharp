using Shoreline.Api.Security;

namespace Shoreline.Api.Infrastructure;

// All name writers (profile changes, registration and team mutations) share this
// bounded gate. It is intentionally not a distributed, multi-writer lock.
public sealed class IdentityNameGate
{
    private readonly SemaphoreSlim[] slots = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> EnterAsync(string scope, string name, CancellationToken ct)
    {
        var hash = StringComparer.Ordinal.GetHashCode(scope + "\0" + SearchText.Normalize(name));
        var gate = slots[(uint)hash % (uint)slots.Length];
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10), ct)) throw IdentityErrors.InProgress();
        return new Holder(gate);
    }

    private sealed class Holder(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
