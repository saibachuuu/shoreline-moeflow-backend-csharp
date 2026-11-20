using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Background reconciler for image thumbnail rows that are neither UNKNOWN
/// nor SUCCEEDED. List serialization used to probe object storage for such
/// rows synchronously, so one stalled row cost the whole page seconds of
/// latency (each HEAD to the object endpoint is a network round trip). The
/// list now returns the "generating" placeholder immediately and calls
/// <see cref="Trigger"/>; this service checks the objects once in the
/// background and writes the outcome back with fenced updates so a live
/// thumbnail worker can never be overwritten.
/// </summary>
public sealed class ThumbnailStatusReconciler
{
    public const int DedupSeconds = 60;
    public const int FreshThumbnailMinutes = 10;
    public const string ReconcileFailureMessage = "后台对账：存储中未找到该图片的缩略图对象";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<ThumbnailStatusReconciler>? _logger;

    private readonly SemaphoreSlim _slots = new(4);
    private readonly ConcurrentDictionary<ObjectId, DateTime> _lastTriggered = new();

    public ThumbnailStatusReconciler(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        ILogger<ThumbnailStatusReconciler>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Fire-and-forget. Callers sit on the hot serialization path, so this
    /// method must never block and never throw.
    /// </summary>
    public void Trigger(ObjectId fileId, ObjectId projectId, string saveName, string? storageSourceId)
    {
        try
        {
            if (string.IsNullOrEmpty(saveName))
            {
                return;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            if (_lastTriggered.TryGetValue(fileId, out var triggeredAt)
                && (now - triggeredAt).TotalSeconds < DedupSeconds)
            {
                return;
            }

            _lastTriggered[fileId] = now;
            if (_lastTriggered.Count > 512)
            {
                foreach (var pair in _lastTriggered)
                {
                    if ((now - pair.Value).TotalSeconds >= DedupSeconds)
                    {
                        _lastTriggered.TryRemove(pair.Key, out _);
                    }
                }
            }

            _ = Task.Run(() => ReconcileAsync(fileId, projectId, saveName, storageSourceId));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Thumbnail reconcile trigger failed for file {FileId}", fileId);
        }
    }

    // Internal for tests: they await it directly instead of racing the
    // fire-and-forget task.
    internal async Task ReconcileAsync(ObjectId fileId, ObjectId projectId, string saveName, string? storageSourceId)
    {
        try
        {
            // The slots bound how many storage probes run at once when a big
            // list marks a whole project's rows as interesting together.
            await _slots.WaitAsync();
            try
            {
                await ReconcileCoreAsync(fileId, projectId, saveName, storageSourceId);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Thumbnail reconcile failed for file {FileId}", fileId);
        }
    }

    private async Task ReconcileCoreAsync(ObjectId fileId, ObjectId projectId, string saveName, string? storageSourceId)
    {
        // The reconciler is a singleton while the router is scoped, and the
        // task outlives the request that triggered it, so resolve everything
        // from a private scope.
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MongoStore>();
        var router = scope.ServiceProvider.GetRequiredService<ProjectStorageRouter>();
        var files = store.Collection("file");

        var row = await files.Find(Builders<BsonDocument>.Filter.Eq("_id", fileId)).FirstOrDefaultAsync();
        if (row is null)
        {
            return;
        }

        // A re-upload replaced the row since the list read it; the new row
        // owns its own thumbnail lifecycle, leave it alone.
        if (row.Text("sa") != saveName)
        {
            return;
        }

        var status = row.Integer("th");
        if (status is 0 or 3)
        {
            return;
        }

        // thk marks rows this reconciler already settled, so a confirmed
        // missing object is probed exactly once, not on every list request.
        if (status == 4 && row.Flag("thk"))
        {
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (TryReadThumbnailTime(row) is { } updated && (now - updated).TotalMinutes < FreshThumbnailMinutes)
        {
            // The thumbnail worker touched this row very recently; a probe
            // now could race its in-flight generation.
            return;
        }

        var stem = Stem(saveName);
        var coverKey = FileService.FilePrefix + "cover-" + stem + ".webp";
        var resampleKey = FileService.FilePrefix + "resample-" + stem + ".webp";

        // The small cover always lives on the deployment's default storage;
        // the resample follows the project's storage source.
        await using var defaultLease = await router.ForDefaultAsync();
        await using var projectLease = string.IsNullOrEmpty(storageSourceId)
            ? null
            : await router.ForSourceAsync(ObjectId.Parse(storageSourceId));
        var coverBackend = defaultLease.Backend;
        var resampleBackend = projectLease?.Backend ?? coverBackend;

        var cover = await coverBackend.HeadAsync(coverKey, CancellationToken.None);
        var resample = await resampleBackend.HeadAsync(resampleKey, CancellationToken.None);

        // Fenced write: the row must still be the same save_name and still be
        // in a non-terminal state, so a finished live worker cannot be
        // overwritten and SUCCEEDED rows are never demoted.
        var fence = Builders<BsonDocument>.Filter.Eq("_id", fileId)
            & Builders<BsonDocument>.Filter.Eq("p", projectId)
            & Builders<BsonDocument>.Filter.Eq("sa", saveName)
            & Builders<BsonDocument>.Filter.In("th", new[] { 1, 2, 4 });
        if (cover is not null || resample is not null)
        {
            var promote = Builders<BsonDocument>.Update
                .Set("th", 3)
                .Set("tht", now)
                .Set("thk", false)
                .Unset("the");
            await files.UpdateOneAsync(fence, promote);
            return;
        }

        var failure = Builders<BsonDocument>.Update
            .Set("tht", now)
            .Set("thk", true);
        if (status is 1 or 2)
        {
            failure = failure
                .Set("th", 4)
                .Set("the", ReconcileFailureMessage);
        }
        else
        {
            // The worker already recorded its own failure reason; keep it and
            // only set the probe-off flag.
            failure = failure.Set("the", row.Text("the"));
        }

        await files.UpdateOneAsync(fence, failure);
    }

    private static DateTime? TryReadThumbnailTime(BsonDocument row)
    {
        if (!row.TryGetValue("tht", out var value) || !value.IsBsonDateTime)
        {
            return null;
        }

        return value.ToUniversalTime();
    }

    private static string Stem(string saveName)
    {
        var dot = saveName.LastIndexOf('.');
        return dot >= 0 ? saveName[..dot] : saveName;
    }
}
