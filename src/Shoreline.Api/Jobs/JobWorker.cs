using MongoDB.Bson;

namespace Shoreline.Api.Jobs;

public sealed class JobWorker(JobQueue queue, IEnumerable<IJobHandler> handlers,
    IConfiguration configuration, ILogger<JobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Operator kill switch for split API/worker deployments: API-only
        // instances set Jobs:WorkerEnabled=false so HTTP stays up while a
        // dedicated worker instance drains the Mongo queue. Enqueueing is
        // unaffected (JobQueue stays registered); only this consumer stops.
        // A missing or unparsable value keeps the historical default: enabled.
        if (bool.TryParse(configuration["Jobs:WorkerEnabled"], out var workerEnabled) && !workerEnabled)
            return Task.CompletedTask;

        var registry = handlers.ToDictionary(h => h.Kind);
        var concurrency = Math.Clamp(configuration.GetValue("Jobs:Concurrency", 1), 1, 8);
        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunAsync(registry, stoppingToken)));
    }

    private async Task RunAsync(Dictionary<string, IJobHandler> registry, CancellationToken ct)
    {
        if (registry.Count == 0) return;
        var owner = Guid.NewGuid().ToString("N");
        var kinds = registry.Keys.ToArray();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await queue.ClaimAsync(owner, kinds, ct);
                if (job is null) { await Task.Delay(500, ct); continue; }
                await ExecuteJobAsync(job, owner, registry[job["kind"].AsString], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Never log payloads or exception messages containing external credentials.
                logger.LogError("Job worker failed ({ErrorType})", e.GetType().Name);
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task ExecuteJobAsync(BsonDocument job, string owner, IJobHandler handler, CancellationToken ct)
    {
        var id = job["_id"].AsObjectId;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewAsync(id, owner, execution, heartbeat.Token);
        try
        {
            await handler.ExecuteAsync(id, job["attempts"].AsInt32, job["payload"].AsBsonDocument, execution.Token);
            execution.Token.ThrowIfCancellationRequested();
            await queue.CompleteAsync(id, owner, ct);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (Exception e)
        {
            logger.LogWarning("Job {JobId} failed ({ErrorType})", id, e.GetType().Name);
            await queue.FailAsync(id, owner, job["attempts"].AsInt32, ct);
        }
        finally
        {
            await heartbeat.CancelAsync();
            await renewal;
        }
    }

    private async Task RenewAsync(ObjectId id, string owner, CancellationTokenSource execution, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (!await queue.RenewAsync(id, owner, ct)) { await execution.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { await execution.CancelAsync(); }
    }
}
