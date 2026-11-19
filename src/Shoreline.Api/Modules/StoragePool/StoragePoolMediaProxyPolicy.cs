using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.StoragePool;

/// <summary>
/// Caches the site-wide "never expire reverse-proxied media URLs" flag from
/// <c>storage_pool_setting</c>. A short TTL keeps multi-replica readers fresh
/// without hitting MongoDB for every media link in a listing, while
/// <see cref="StoragePoolSettingsService"/> pushes updates immediately on the
/// local instance.
/// </summary>
public sealed class StoragePoolMediaProxyPolicy : IMediaProxyPolicy
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly MongoStore store;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long expiresAtUtcTicks;
    private volatile bool cachedValue;

    public StoragePoolMediaProxyPolicy(MongoStore store, TimeProvider clock)
    {
        this.store = store;
        this.clock = clock;
    }

    public void Set(bool neverExpire)
    {
        cachedValue = neverExpire;
        Volatile.Write(ref expiresAtUtcTicks, clock.GetUtcNow().UtcTicks + CacheTtl.Ticks);
    }

    public async ValueTask<bool> UseNeverExpireUrlsAsync(CancellationToken cancellationToken = default)
    {
        if (clock.GetUtcNow().UtcTicks < Volatile.Read(ref expiresAtUtcTicks))
            return cachedValue;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (clock.GetUtcNow().UtcTicks < Volatile.Read(ref expiresAtUtcTicks))
                return cachedValue;

            var row = await store.Collection(StoragePoolConstants.SettingCollection)
                .Find(new BsonDocument("_id", "default"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var value = row is not null && StoragePoolBson.Bool(row, "pmu");
            Set(value);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }
}
