using System.Collections.Concurrent;
using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Bounded per-project serialization for file mutations (upload / order / rename /
/// move / delete).  This deliberately does not share the invitation or team gates:
/// file mutations must not nest inside those locks.
///
/// Entries are created lazily per project and reference-counted; an entry leaves
/// the table only once no caller holds or waits on it. Retiring marks the entry
/// dead under its lock first, so a late GetOrAdd can never revive a retired
/// entry and two generations of the same project's semaphore can never coexist.
/// The previous 64-shard layout let unrelated projects block one another.
/// </summary>
public sealed class FileMutationGate
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<ObjectId, Entry> _entries = new();

    public Task<IDisposable> EnterAsync(ObjectId projectId, CancellationToken ct)
        => EnterAsync(projectId, DefaultWait, ct);

    public async Task<IDisposable> EnterAsync(ObjectId projectId, TimeSpan wait, CancellationToken ct)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(projectId, static _ => new Entry());
            bool live;
            lock (entry)
            {
                live = !entry.Dead;
                if (live)
                {
                    entry.RefCount++;
                }
            }

            if (!live)
            {
                // The entry is being retired by its last holder; the retry
                // creates a fresh one once the removal lands.
                continue;
            }

            try
            {
                if (!await entry.Semaphore.WaitAsync(wait, ct))
                {
                    throw new ApiError("FileMutationBusy", 2, "文件正在处理中，请稍后重试", 409);
                }
            }
            catch
            {
                RetireIfIdle(projectId, entry);
                throw;
            }

            return new Holder(this, projectId, entry);
        }
    }

    private void RetireIfIdle(ObjectId projectId, Entry entry)
    {
        lock (entry)
        {
            entry.RefCount--;
            if (entry.RefCount == 0 && !entry.Dead)
            {
                entry.Dead = true;
                _entries.TryRemove(new KeyValuePair<ObjectId, Entry>(projectId, entry));
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public bool Dead;     // guarded by the Entry lock
        public int RefCount;  // guarded by the Entry lock
    }

    private sealed class Holder(FileMutationGate gate, ObjectId projectId, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                entry.Semaphore.Release();
                gate.RetireIfIdle(projectId, entry);
            }
        }
    }
}
