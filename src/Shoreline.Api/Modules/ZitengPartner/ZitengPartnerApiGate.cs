using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>Test seam so fake-clock tests can observe the reservation without sleeping.</summary>
public interface IZitengPartnerDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class ZitengPartnerSystemDelay : IZitengPartnerDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}

/// <summary>
/// A process-wide API permit. The one-second (configurable) spacing is stored in
/// MongoDB with a compare-and-swap, so two application processes cannot both
/// believe they own the next slot. The document is a reservation, not a lock:
/// it expires naturally with the clock, so cancellation never leaves a stuck
/// non-TTL lock behind.
/// </summary>
public interface IZitengPartnerApiGate
{
    Task WaitAsync(CancellationToken ct);
}

public sealed class ZitengPartnerApiGate(
    MongoStore store,
    ZitengPartnerSettings settings,
    TimeProvider clock,
    IZitengPartnerDelay delay) : IZitengPartnerApiGate
{
    public const string CollectionName = "ziteng_api_gate";
    private const string DocumentId = "global";

    public async Task WaitAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0, settings.MinIntervalSeconds));
        var collection = store.Collection(CollectionName);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow().UtcDateTime;
            var current = await collection.Find(new BsonDocument("_id", DocumentId))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (current is null)
            {
                try
                {
                    await collection.InsertOneAsync(new BsonDocument
                    {
                        ["_id"] = DocumentId,
                        ["next_at"] = now.Add(interval)
                    }, cancellationToken: ct).ConfigureAwait(false);
                    return;
                }
                catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    continue;
                }
            }

            var stored = current.GetValue("next_at", BsonDateTime.Create(now)).ToUniversalTime();
            var slot = stored > now ? stored : now;
            var desired = slot.Add(interval);
            var result = await collection.UpdateOneAsync(
                new BsonDocument { ["_id"] = DocumentId, ["next_at"] = stored },
                new BsonDocument("$set", new BsonDocument("next_at", desired)),
                cancellationToken: ct).ConfigureAwait(false);
            if (result.MatchedCount != 1) continue;

            var wait = slot - now;
            if (wait > TimeSpan.Zero)
                await delay.DelayAsync(wait, ct).ConfigureAwait(false);
            return;
        }
        throw new ZitengPartnerApiException("查询限速协调失败，请稍后重试");
    }
}

public static class ZitengPartnerGateIndexes
{
    public static Task EnsureAsync(MongoStore store, CancellationToken ct) =>
        // The fixed _id is the only key and is created by the insert; no TTL
        // index is used, and no cleanup deletion can race with a reservation.
        Task.CompletedTask;
}
