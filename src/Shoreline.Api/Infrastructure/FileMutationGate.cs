using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Bounded per-project serialization for file mutations (upload / order / rename /
/// move / delete).  This deliberately does not share the invitation or team gates:
/// file mutations must not nest inside those locks.
/// </summary>
public sealed class FileMutationGate
{
    private const int ShardCount = 64;
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim[] _shards = Enumerable
        .Range(0, ShardCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public Task<IDisposable> EnterAsync(ObjectId projectId, CancellationToken ct)
        => EnterAsync(projectId, DefaultWait, ct);

    public async Task<IDisposable> EnterAsync(ObjectId projectId, TimeSpan wait, CancellationToken ct)
    {
        var shard = _shards[(int)((uint)projectId.GetHashCode() % ShardCount)];
        if (!await shard.WaitAsync(wait, ct))
        {
            throw new ApiError("FileMutationBusy", 2, "文件正在处理中，请稍后重试", 409);
        }

        return new Holder(shard);
    }

    private sealed class Holder(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
