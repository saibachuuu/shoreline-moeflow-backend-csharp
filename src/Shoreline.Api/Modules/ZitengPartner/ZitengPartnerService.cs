using MongoDB.Bson;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// The service layer mirrors <c>service.py</c> + the enqueue half of <c>tasks.py</c>.
/// HTTP enqueueing never performs partner network I/O; only the job handler runs
/// <see cref="RunAsync"/>.
/// </summary>
public sealed class ZitengPartnerService(
    ZitengPartnerStore store,
    ZitengPartnerClient client,
    ZitengPartnerSettings settings,
    JobQueue jobs,
    TimeProvider clock,
    ILogger<ZitengPartnerService> logger)
{
    public const string JobKind = "ziteng_check";
    private static readonly TimeSpan WorkerLease = TimeSpan.FromSeconds(90);

    /// <summary>
    /// POST behavior: ensure the check, advance the generation, enqueue a job that
    /// carries that generation, then read the current row back. No partner request
    /// is made on the HTTP thread.
    /// </summary>
    public async Task<ZitengPartnerStore.CheckSnapshot?> RequestCheckAsync(string projectId, string projectName,
        CancellationToken ct = default)
    {
        await store.EnsureCheckAsync(projectId, projectName, ct);
        var generation = await store.ResetForRetryAsync(projectId, ct);
        if (generation is null) return await store.GetCheckAsync(projectId, ct);
        try
        {
            await jobs.EnqueueAsync(JobKind, Payload(projectId, generation.Value), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("紫藤查重入队失败 project={ProjectId}: {Error}", projectId, settings.Redact(exception.Message));
            await store.FailQueuedAsync(projectId, generation.Value, "查重任务入队失败", ct);
        }
        return await store.GetCheckAsync(projectId, ct);
    }

    /// <summary>
    /// Project-created hook body. It only enqueues (never calls the partner API) and
    /// the public notifier swallows/logs failures so project creation is unaffected.
    /// </summary>
    public async Task EnqueueCreatedProjectAsync(string projectId, CancellationToken ct = default)
    {
        var name = await store.ReadProjectNameAsync(projectId, ct);
        var check = await store.EnsureCheckAsync(projectId, name, ct);
        await jobs.EnqueueAsync(JobKind, Payload(projectId, check.Generation), ct);
    }

    public async Task RunAsync(string projectId, long generation, string owner, CancellationToken ct = default)
    {
        var check = await store.GetCheckAsync(projectId, ct);
        if (check is null)
        {
            logger.LogWarning("紫藤查重任务不存在，跳过: {ProjectId}", projectId);
            return;
        }
        if (check.Generation != generation)
        {
            logger.LogInformation("紫藤查重任务代次已过期，跳过: {ProjectId} generation={Generation}",
                projectId, generation);
            return;
        }

        var leaseUntil = clock.GetUtcNow().UtcDateTime.Add(WorkerLease);
        if (!await store.ClaimAsync(projectId, generation, owner, leaseUntil, ct))
        {
            logger.LogInformation("紫藤查重任务已被领取或已完成，跳过: {ProjectId}", projectId);
            return;
        }

        try
        {
            var keyword = check.Keyword;
            if (!ZitengPartnerText.IsSearchable(keyword))
            {
                // Not a failure: the title itself cannot be searched. Keep the
                // keyword for diagnostics and record an explicit clear.
                await store.CompleteAsync(projectId, generation, owner, ZitengPartnerStore.VerdictClear, [], ct);
                return;
            }

            IReadOnlyList<ZitengPartnerWork> works;
            var cached = await store.GetCachedAsync(keyword, settings.CacheTtlSeconds, ct);
            if (cached is not null)
            {
                works = cached.Value.Works;
            }
            else
            {
                var result = await client.SearchAsync(keyword, ct);
                await store.StoreAsync(keyword, result.Works, result.Total, result.Truncated, ct);
                works = result.Works;
            }

            // Re-read the project at completion time: a rename or deletion while
            // the network call was in flight must not use the stale title.
            var projectName = await store.ReadProjectNameAsync(projectId, ct);
            var projectTitle = projectName.Length > 0 ? projectName : keyword;
            var suspects = ZitengPartnerMatching.FindSuspects(projectTitle, works, settings.MaxResults);
            var payload = suspects.Select(SuspectDocument).ToList();
            var verdict = payload.Count > 0
                ? ZitengPartnerStore.VerdictSuspected
                : ZitengPartnerStore.VerdictClear;
            await store.CompleteAsync(projectId, generation, owner, verdict, payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The job worker is stopping or lost its lease. Leave the check in
            // running; the lease lets a later attempt reclaim it.
            throw;
        }
        catch (ZitengPartnerApiException exception)
        {
            logger.LogWarning("紫藤查重失败 project={ProjectId}: {Error}", projectId,
                settings.Redact(exception.Message));
            await store.FailAsync(projectId, generation, owner,
                settings.Redact(exception.Message), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError("紫藤查重异常 project={ProjectId}: {Error}", projectId,
                settings.Redact(exception.GetType().Name));
            await store.FailAsync(projectId, generation, owner,
                "内部错误: " + settings.Redact(exception.Message), CancellationToken.None);
        }
    }

    private static BsonDocument Payload(string projectId, long generation) => new()
    {
        ["projectId"] = projectId,
        ["generation"] = generation,
    };

    private static BsonDocument SuspectDocument(ZitengPartnerSuspect suspect) => new()
    {
        ["id"] = suspect.Work.Id,
        ["reference"] = suspect.Work.Reference,
        ["display_title"] = suspect.Work.DisplayTitle,
        ["original_title"] = suspect.Work.OriginalTitle,
        ["author"] = suspect.Work.Author,
        ["circle"] = suspect.Work.Circle,
        ["state"] = suspect.Work.State,
        ["stage"] = suspect.Work.Stage,
        ["level"] = suspect.Level,
        ["score"] = ZitengPartnerMatching.RoundScore(suspect.Score),
    };
}

/// <summary>
/// Public entry point for the core project-created registry. The core code does
/// not currently have such a registry; this notifier is the contract to wire.
/// It never throws into project creation.
/// </summary>
public sealed class ZitengPartnerProjectCreatedNotifier(
    ZitengPartnerService service,
    ILogger<ZitengPartnerProjectCreatedNotifier> logger)
{
    public async Task TryNotifyAsync(string projectId, CancellationToken ct = default)
    {
        try
        {
            await service.EnqueueCreatedProjectAsync(projectId, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("触发紫藤查重失败 project={ProjectId}: {Error}", projectId,
                exception.GetType().Name);
        }
    }
}
