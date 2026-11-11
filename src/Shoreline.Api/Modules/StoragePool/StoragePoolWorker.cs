using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.StoragePool;

/// <summary>
/// Optional in-process worker. It is registered only by AddStoragePoolServices;
/// the core application never depends on this module.
/// </summary>
public sealed class StoragePoolWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly IConfiguration configuration;
    private readonly ILogger<StoragePoolWorker> logger;

    public StoragePoolWorker(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<StoragePoolWorker> logger)
    {
        this.scopes = scopes;
        this.clock = clock;
        this.configuration = configuration;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("StoragePool:WorkerEnabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("storage pool worker tick failed: {Message}", Sanitize(error.Message));
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) return;
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<StoragePoolSettingsService>();
        var setting = await settings.ReadAsync(ct).ConfigureAwait(false);
        var store = scope.ServiceProvider.GetRequiredService<MongoStore>();
        if (StoragePoolBson.Bool(setting, "re", true))
        {
            var engine = scope.ServiceProvider.GetRequiredService<StoragePoolTransferEngine>();
            var rows = await store.Collection(StoragePoolConstants.TransferCollection).Find(new BsonDocument
            {
                ["st"] = new BsonDocument("$in", new BsonArray
                    { StoragePoolConstants.Pending, StoragePoolConstants.Copying, StoragePoolConstants.Verified, StoragePoolConstants.Switched }),
            }).Sort(new BsonDocument { ["ct"] = 1 }).Limit(20).ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.TryGetValue("_id", out var id) && id.IsObjectId)
                    await engine.RunAsync(id.AsObjectId, ct).ConfigureAwait(false);
            }
        }

        // The legacy beat entry runs at 03:00 in Celery's UTC schedule and
        // persists its last-run state, so a process restart must not replay
        // an already-consumed day. Claim the UTC day in Mongo before scanning.
        var utcNow = clock.GetUtcNow().UtcDateTime;
        if (utcNow.Hour >= 3)
        {
            var today = DateOnly.FromDateTime(utcNow);
            if (await settings.TryClaimScheduledScanAsync(today, ct).ConfigureAwait(false))
            {
                var scan = scope.ServiceProvider.GetRequiredService<StoragePoolScanService>();
                await scan.RunAsync(false, ct).ConfigureAwait(false);
            }
        }
    }

    private static string Sanitize(string message)
    {
        var text = message.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 300 ? text[..300] : text;
    }
}
