using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Formats;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Output rows, cooldown/pruning and API projection. All mutations take the
/// existing project label lock; the long download/zip phase lives in
/// <see cref="OutputExportService"/> and only reacquires the lock for publish.
/// </summary>
public sealed class OutputService(
    MongoStore store,
    ProjectService projects,
    ProjectLabelLock labels,
    LabelRequestContext labelContext,
    ObjectStorage storage,
    UserView userView,
    JobQueue jobs,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<OutputService> logger,
    ProjectStorageRouter? router = null)
{
    public const string JobKind = "output_project";
    /// <summary>
    /// Celery <c>tasks.output_team_projects_task</c> equivalent. The route keeps
    /// the Python <c>run_sync</c> path, while background/recovery callers can
    /// enqueue this kind instead of blocking on the whole team walk.
    /// </summary>
    public const string TeamJobKind = "output_team_projects";

    private readonly ProjectCleanupOptions cleanup = ProjectCleanupOptions.FromConfiguration(configuration);
    private IMongoCollection<BsonDocument> Outputs => store.Collection("output");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");

    private async Task<StorageBackendLease?> LeaseForProjectAsync(
        BsonDocument? project,
        CancellationToken ct)
    {
        if (project is null) return null;
        if (ProjectStorageRouter.ReadSourceId(project) is null) return null;
        if (router is null) throw ProjectStorageRouter.SourceUnavailable();
        return await router.ForProjectAsync(project, ct);
    }

    public static ApiError TargetNotExist() =>
        new("TargetNotExistError", 4024, "项目目标语言不存在");

    public static ApiError NotFound() =>
        new("OutputNotExistError", 9001, "导出导出文件不存在");

    public static ApiError TooFast() =>
        new("OutputTooFastError", 9002, "导出过于频繁，请稍后再试");

    public static void EnsureTargetBelongsToProject(BsonDocument project, BsonDocument target)
    {
        var targetProject = target.GetValue("t", BsonNull.Value);
        if (!targetProject.IsObjectId || targetProject.AsObjectId != project["_id"].AsObjectId)
            throw TargetNotExist();
    }

    public async Task<List<BsonDocument>> ListTargetsAsync(ObjectId projectId, CancellationToken ct) =>
        // Python iterates project.targets() in its natural collection order.
        await Targets.Find(new BsonDocument("t", projectId)).ToListAsync(ct);

    public async Task<BsonDocument> GetAsync(ObjectId outputId, CancellationToken ct)
    {
        var output = await Outputs.Find(new BsonDocument("_id", outputId)).FirstOrDefaultAsync(ct);
        return output ?? throw NotFound();
    }

    public async Task<List<BsonDocument>> ListAsync(
        ObjectId projectId,
        ObjectId targetId,
        CancellationToken ct) =>
        await Outputs.Find(new BsonDocument { ["p"] = projectId, ["t"] = targetId })
            .Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 })
            .ToListAsync(ct);

    public async Task<BsonDocument> CreateAsync(
        BsonDocument project,
        BsonDocument target,
        BsonDocument actor,
        int type,
        IReadOnlyList<ObjectId>? include,
        IReadOnlyList<ObjectId>? exclude,
        CancellationToken ct)
    {
        if (type is not (OutputTypes.All or OutputTypes.OnlyText))
            throw ApiError.Field("type", "导出类型不正确");
        EnsureTargetBelongsToProject(project, target);
        var projectId = project["_id"].AsObjectId;
        var targetId = target["_id"].AsObjectId;
        BsonDocument created;
        long revision;

        await using (var session = await labels.EnterAsync(labelContext, projectId, recovery: false,
                         expectedRevision: null, ct))
        {
            await using var storageLease = await LeaseForProjectAsync(session.Project, ct);
            var last = await Outputs.Find(new BsonDocument { ["p"] = projectId, ["t"] = targetId })
                .Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 })
                .FirstOrDefaultAsync(ct);
            if (last is not null && OutputPolicy.IsTooFast(
                    last.GetValue("ct", BsonNull.Value),
                    clock.GetUtcNow().UtcDateTime,
                    OutputPolicy.WaitSeconds(configuration)))
                throw TooFast();

            await PruneAsync(session.Project, targetId, ct);
            created = await InsertAsync(projectId, targetId, actor, type, include, exclude, ct);
            revision = session.Revision;
        }

        await EnqueueAsync(created["_id"].AsObjectId, projectId, revision, ct);
        return created;
    }

    /// <summary>Python ProjectOutputListAPI.post: one output for every target that is not cooling down.</summary>
    public async Task<List<BsonDocument>> CreateAllAsync(
        BsonDocument project,
        BsonDocument actor,
        CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var created = new List<(BsonDocument Output, long Revision)>();

        await using (var session = await labels.EnterAsync(labelContext, projectId, recovery: false,
                         expectedRevision: null, ct))
        {
            await using var storageLease = await LeaseForProjectAsync(session.Project, ct);
            var targets = await ListTargetsAsync(projectId, ct);
            var wait = OutputPolicy.WaitSeconds(configuration);
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var target in targets)
            {
                var targetId = target["_id"].AsObjectId;
                var last = await Outputs.Find(new BsonDocument { ["p"] = projectId, ["t"] = targetId })
                    .Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 })
                    .FirstOrDefaultAsync(ct);
                if (last is not null && OutputPolicy.IsTooFast(last.GetValue("ct", BsonNull.Value), now, wait))
                    continue;

                await PruneAsync(session.Project, targetId, ct);
                var output = await InsertAsync(projectId, targetId, actor, OutputTypes.All, null, null, ct);
                created.Add((output, session.Revision));
            }
        }

        Exception? enqueueFailure = null;
        foreach (var (output, revision) in created)
        {
            try
            {
                await EnqueueAsync(output["_id"].AsObjectId, projectId, revision, ct);
            }
            catch (Exception exception)
            {
                // EnqueueAsync has already marked this output as error. Keep
                // enqueueing the other persisted outputs so none of them are
                // left in the non-resumable "queued but no job" state.
                enqueueFailure ??= exception;
            }
        }

        if (enqueueFailure is not null) throw enqueueFailure;
        return created.Select(item => item.Output).ToList();
    }

    /// <summary>
    /// Python app/tasks/output_team_projects.py: create one ALL output for every
    /// target of every WORKING project in the team, subject to the same
    /// cooldown/prune rules as <see cref="CreateAllAsync"/>. Projects are read
    /// in natural collection order, matching Team.projects(status=WORKING).
    /// </summary>
    public async Task CreateTeamAsync(BsonDocument team, BsonDocument actor, CancellationToken ct)
    {
        var projects = await store.Collection("project")
            .Find(new BsonDocument { ["t"] = team["_id"], ["st"] = 0 })
            .ToListAsync(ct);
        foreach (var project in projects)
            // Python has no per-project catch: the first failure aborts the
            // task, leaving already-queued outputs intact. An unavailable pool
            // source surfaces the same way instead of silently reading a
            // different backend.
            await CreateAllAsync(project, actor, ct);
    }

    /// <summary>
    /// Python <c>output_team_projects(team_id, current_user_id)</c> async
    /// branch (app/tasks/output_team_projects.py:69-80). The durable job is
    /// written before returning; the job handler resolves the same team/actor
    /// and calls <see cref="CreateTeamAsync"/>. The HTTP route keeps the
    /// <c>run_sync</c> path because it has always returned after creating the
    /// per-target output rows in this codebase.
    /// </summary>
    public Task<ObjectId> ScheduleTeamAsync(ObjectId teamId, ObjectId actorId, CancellationToken ct) =>
        jobs.EnqueueAsync(TeamJobKind, new BsonDocument
        {
            ["team_id"] = teamId.ToString(),
            ["current_user_id"] = actorId.ToString()
        }, ct);

    /// <summary>Job body: <c>tasks.output_team_projects_task</c>.</summary>
    public async Task RunTeamExportAsync(BsonDocument payload, CancellationToken ct)
    {
        if (!ObjectId.TryParse(payload.Text("team_id"), out var teamId)
            || !ObjectId.TryParse(payload.Text("current_user_id"), out var actorId))
            throw new ApiError("ValidateError", 2, "Invalid request");

        var team = await store.Collection("team").Find(new BsonDocument("_id", teamId))
            .FirstOrDefaultAsync(ct) ?? throw new ApiError("TeamNotExistError", 3001, "团队不存在");
        var actor = await store.Collection("user").Find(new BsonDocument("_id", actorId))
            .FirstOrDefaultAsync(ct) ?? throw IdentityErrors.UserMissing();
        await CreateTeamAsync(team, actor, ct);
    }

    private async Task PruneAsync(BsonDocument project, ObjectId targetId, CancellationToken ct)
    {
        var projectId = project["_id"].AsObjectId;
        var rows = await Outputs.Find(new BsonDocument { ["p"] = projectId, ["t"] = targetId })
            .Sort(new BsonDocument { ["ct"] = -1, ["_id"] = -1 })
            .ToListAsync(ct);
        if (rows.Count <= 2) return;

        var old = rows.Skip(2).ToList();
        await using var storageLease = await LeaseForProjectAsync(project, ct);
        var objectStorage = storageLease?.Backend ?? storage;
        await OutputObjectCleanup.DeleteObjectsStrictAsync(
            store, cleanup, objectStorage.DeleteAsync, project, old, ct);

        var ids = new BsonArray(old.Select(row => row["_id"]));
        await Outputs.DeleteManyAsync(new BsonDocument
        {
            ["p"] = projectId,
            ["t"] = targetId,
            ["_id"] = new BsonDocument("$in", ids)
        }, ct);
    }

    private async Task<BsonDocument> InsertAsync(
        ObjectId projectId,
        ObjectId targetId,
        BsonDocument actor,
        int type,
        IReadOnlyList<ObjectId>? include,
        IReadOnlyList<ObjectId>? exclude,
        CancellationToken ct)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = projectId,
            ["t"] = targetId,
            ["u"] = actor.GetValue("_id", BsonNull.Value),
            ["s"] = OutputStatus.Queuing,
            ["ty"] = type,
            ["file_name"] = "",
            ["sb"] = 0L,
            ["ct"] = clock.GetUtcNow().UtcDateTime,
            ["file_ids_include"] = new BsonArray((include ?? Array.Empty<ObjectId>()).Select(id => (BsonValue)id)),
            ["file_ids_exclude"] = new BsonArray((exclude ?? Array.Empty<ObjectId>()).Select(id => (BsonValue)id))
        };
        await Outputs.InsertOneAsync(document, cancellationToken: ct);
        return document;
    }

    private async Task EnqueueAsync(ObjectId outputId, ObjectId projectId, long revision, CancellationToken ct)
    {
        ObjectId jobId;
        try
        {
            jobId = await jobs.EnqueueAsync(JobKind, new BsonDocument
            {
                ["output_id"] = outputId,
                ["project_id"] = projectId,
                ["label_revision"] = revision
            }, ct);
        }
        catch (Exception exception)
        {
            logger.LogError("Output job enqueue failed ({ErrorType})", exception.GetType().Name);
            await Outputs.UpdateOneAsync(
                new BsonDocument { ["_id"] = outputId, ["s"] = OutputStatus.Queuing },
                new BsonDocument("$set", new BsonDocument
                {
                    ["s"] = OutputStatus.Error,
                    ["job_error"] = exception.GetType().Name
                }),
                cancellationToken: CancellationToken.None);
            throw;
        }

        // The job already exists. A failure to attach its id is diagnostic only;
        // the worker claims by kind/payload and must not be disabled by it.
        try
        {
            await Outputs.UpdateOneAsync(
                new BsonDocument { ["_id"] = outputId, ["s"] = OutputStatus.Queuing },
                new BsonDocument("$set", new BsonDocument("job_id", jobId)),
                cancellationToken: ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Output job id was not recorded ({ErrorType})", exception.GetType().Name);
        }
    }

    public async Task<object> ViewAsync(
        BsonDocument project,
        BsonDocument target,
        BsonDocument output,
        HttpContext context,
        CancellationToken ct) =>
        (await ViewsAsync(project, [(output, target)], context, ct))[0];

    public async Task<object[]> ViewsAsync(
        BsonDocument project,
        IReadOnlyList<(BsonDocument Output, BsonDocument Target)> rows,
        HttpContext context,
        CancellationToken ct)
    {
        var chinese = LanguageCatalog.ChinesePreferred(context.Request.GetTypedHeaders().AcceptLanguage);
        var projectView = await projects.ViewAsync(project, null, chinese, ct);

        var targetViews = new Dictionary<ObjectId, object>();
        foreach (var target in rows.Select(row => row.Target).DistinctBy(row => row["_id"]))
            targetViews[target["_id"].AsObjectId] = await projects.TargetViewAsync(target, chinese, ct);

        var userIds = rows.Select(row => row.Output.GetValue("u", BsonNull.Value))
            .Where(value => value.IsObjectId)
            .Select(value => value.AsObjectId)
            .Distinct()
            .ToList();
        var userRows = userIds.Count == 0
            ? new List<BsonDocument>()
            : await store.Collection("user")
                .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(userIds))))
                .ToListAsync(ct);
        var userViews = new Dictionary<ObjectId, Dictionary<string, object?>>();
        // Python User.to_api reads g.current_user, so an admin viewer sees the
        // output owner's email and the owner sees can_manage_site_admins.
        foreach (var user in userRows)
            userViews[user["_id"].AsObjectId] = await userView.ToApiAsync(user, context);

        var result = new List<object>(rows.Count);
        foreach (var (output, target) in rows)
        {
            var userId = output.GetValue("u", BsonNull.Value);
            var user = userId.IsObjectId && userViews.TryGetValue(userId.AsObjectId, out var view) ? view : null;
            var status = output.Integer("s");
            var statusBytes = output.GetValue("sb", BsonNull.Value);
            string? link = null;
            if (status == OutputStatus.Succeeded)
            {
                var fileName = output.Text("file_name");
                if (fileName.Length > 0)
                {
                    await using var storageLease = await LeaseForProjectAsync(project, ct);
                    var objectStorage = storageLease?.Backend ?? storage;
                    link = await objectStorage.SignGetAsync(
                        cleanup.OutputPrefix + output["_id"].AsObjectId + "/" + fileName,
                        TimeSpan.FromDays(7),
                        contentDisposition: "attachment");
                }
            }

            var item = new Dictionary<string, object?>
            {
                ["id"] = output["_id"].AsObjectId.ToString(),
                ["project"] = projectView,
                ["target"] = targetViews[target["_id"].AsObjectId],
                ["user"] = user,
                ["type"] = output.Integer("ty"),
                ["status"] = status,
                ["storage_bytes"] = statusBytes.IsNumeric ? statusBytes.ToInt64() : 0L,
                ["status_details"] = OutputStatusDetails.All(chinese),
                ["file_ids_include"] = StringIds(output.GetValue("file_ids_include", BsonNull.Value)),
                ["file_ids_exclude"] = StringIds(output.GetValue("file_ids_exclude", BsonNull.Value)),
                ["create_time"] = output.GetValue("ct", BsonNull.Value) is BsonDateTime created
                    ? PythonValue.Iso(created)
                    : ""
            };
            if (status == OutputStatus.Succeeded) item["link"] = link;
            result.Add(item);
        }
        return result.ToArray();
    }

    private static string[] StringIds(BsonValue value) => value is BsonArray array
        ? array.Select(item => item.ToString() ?? "").ToArray()
        : [];
}
