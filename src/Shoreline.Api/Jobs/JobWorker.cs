using MongoDB.Bson;

namespace Shoreline.Api.Jobs;

public sealed class JobWorker(JobQueue queue, IEnumerable<IJobHandler> handlers,
    IConfiguration configuration, ILogger<JobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Optional kill-switch for API-only instances: they enqueue jobs but never
        // consume them. A missing or unparsable value keeps the historical default
        // (enabled). Independent of StoragePool:WorkerEnabled, whose semantics
        // (storage pool background worker) are untouched.
        if (bool.TryParse(configuration["Jobs:WorkerEnabled"], out var workerEnabled) && !workerEnabled)
        {
            logger.LogInformation(
                "Job worker disabled via Jobs:WorkerEnabled=false; this instance runs no queue consumer.");
            return Task.CompletedTask;
        }
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

    private async Task ExecuteJobAsync(BsonDocument job, string owner, IJobHandler handler, CancellationToken shutdownToken)
    {
        var id = job["_id"].AsObjectId;
        var kind = job["kind"].AsString;
        var attempts = job["attempts"].AsInt32;
        // In-flight execution is deliberately NOT linked to the shutdown token:
        // stopping the host stops *claiming*, while a running handler keeps its
        // own token so HostOptions.ShutdownTimeout bounds completion instead of
        // cancelling work mid-write. Only the lease-renewal heartbeat below
        // observes shutdown.
        using var execution = new CancellationTokenSource();
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        var renewal = RenewAsync(id, owner, execution, heartbeat.Token);
        try
        {
            await handler.ExecuteAsync(id, attempts, job["payload"].AsBsonDocument, execution.Token);
            execution.Token.ThrowIfCancellationRequested();
            // Bounded, non-cancelled settle token: a finished job must never be
            // left "running" just because shutdown was requested mid-write.
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await queue.CompleteAsync(id, owner, settle.Token);
            logger.LogInformation("Job {JobId} {Kind} attempt {Attempt} completed", id, kind, attempts);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
        {
            // Lease lost mid-execution (renewal failed or row reclaimed). The job
            // row still names us as owner with an expired lease, so another
            // worker reclaims it after expiry — do not Fail it here.
            logger.LogWarning("Job {JobId} {Kind} attempt {Attempt} lost its lease; leaving for reclaim", id, kind, attempts);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Shutdown raced completion: the job stays "running" under our owner
            // and its lease expires naturally, after which it is reclaimed.
            logger.LogInformation("Job {JobId} {Kind} attempt {Attempt} interrupted by shutdown; lease will expire", id, kind, attempts);
        }
        catch (Exception e)
        {
            logger.LogWarning("Job {JobId} {Kind} attempt {Attempt} failed ({ErrorType})", id, kind, attempts, e.GetType().Name);
            try
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await queue.FailAsync(id, owner, attempts, settle.Token);
            }
            catch (Exception failError)
            {
                // Failing to record the failure must not crash the loop; the
                // expired lease still returns the job to the queue.
                logger.LogWarning("Job {JobId} {Kind} attempt {Attempt} could not record failure ({ErrorType})", id, kind, attempts, failError.GetType().Name);
            }
        }
        finally
        {
            await heartbeat.CancelAsync();
            await renewal;
        }
    }

    private async Task RenewAsync(ObjectId id, string owner, CancellationTokenSource execution, CancellationToken heartbeatToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), heartbeatToken);
                if (!await queue.RenewAsync(id, owner, heartbeatToken)) { await execution.CancelAsync(); return; }
            }
        }
        // Shutdown: stop renewing but leave the in-flight execution alone; the
        // lease expires naturally if the handler does not finish in time.
        catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested) { }
        catch (Exception) { try { await execution.CancelAsync(); } catch (ObjectDisposedException) { } }
    }
}
