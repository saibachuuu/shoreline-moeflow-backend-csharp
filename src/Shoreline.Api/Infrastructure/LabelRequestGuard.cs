using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Optional test seam for the best-effort revision-header hook. Production
/// registrations leave every hook null.
/// </summary>
public sealed class LabelRequestGuardHooks
{
    public Func<HttpContext, CancellationToken, Task>? BeforeRevisionHeadersAsync { get; init; }
}

/// <summary>
/// Request-level label guard, ported from app/services/label_lock.py:293-399.
///
/// The guard resolves the project from the route, rejects ordinary requests
/// while a recovery is pending, validates X-Label-Revision on label writes
/// after a bulk operation, and appends the current revision headers. Reads
/// and single-row label writes pass through without the project lock: they
/// only observe committed state, LabelService guards its writes with a
/// per-row label_epoch CAS filter, and FileService takes the mutation gate
/// plus the label lease itself around file metadata. Bulk label operations
/// (the /label- and /snapshots routes) still take the shared project lock
/// because they rewrite many rows at once.
///
/// It is intentionally not registered by Program.cs; the integration owner
/// must place it inside the existing error middleware and after routing.
/// </summary>
public sealed class LabelRequestGuard(
    RequestDelegate next,
    MongoStore store,
    ProjectLabelLock labels,
    LabelRequestGuardHooks? hooks = null)
{
    private static readonly string[] LabelResources =
        ["files", "sources", "translations", "tips", "targets", "label-", "snapshots"];

    public async Task InvokeAsync(HttpContext context)
    {
        var projectId = await ResolveProjectIdAsync(context, context.RequestAborted);
        if (projectId is null)
        {
            await next(context);
            return;
        }

        var pid = projectId.Value;
        var request = context.Request;
        var isOperation = request.Path.Value?.Contains("/label-", StringComparison.Ordinal) == true ||
                          request.Path.Value?.Contains("/snapshots", StringComparison.Ordinal) == true;

        var row = await store.Collection("project").Find(new BsonDocument("_id", pid))
            .Project(new BsonDocument { ["label_revision"] = 1, ["label_recovery"] = 1 })
            .FirstOrDefaultAsync(context.RequestAborted);
        if (row is null)
        {
            // Python returns early when the project disappeared, but still emits
            // revision headers in after_request.
            await InvokeAndAddHeadersAsync(context, pid, session: null);
            return;
        }

        var revision = row.Integer("label_revision", 0);
        if (ProjectLabelLock.HasRecovery(row) && !isOperation)
        {
            context.Response.OnStarting(async () =>
            {
                await AddRevisionHeadersAsync(context, pid).ConfigureAwait(false);
            });
            throw LabelErrors.Busy();
        }

        if (request.Method == HttpMethods.Options)
        {
            await InvokeAndAddHeadersAsync(context, pid, session: null);
            return;
        }

        if (request.Method is "GET" or "HEAD")
        {
            // Reads never queue behind the project lock. A long writer (an
            // upload or a bulk operation) must not block anyone who is only
            // looking at the project; the revision header still reports the
            // snapshot the response was built from. The recovery barrier
            // above keeps reads out of a project mid-restore.
            await InvokeAndAddHeadersAsync(context, pid, session: null);
            return;
        }

        if (!isOperation)
        {
            // Ordinary label writes (single source/translation/tip rows,
            // uploads) no longer take the project lock: LabelService guards
            // each row with a label_epoch CAS filter and FileService takes
            // the mutation gate plus the label lease itself, so a completed
            // bulk operation rejects stale writers with 409 instead of every
            // write queueing behind one long request. The revision header is
            // still validated here so bulk-modified data fails fast.
            AssertRevisionHeader(context, pid, revision);
            await InvokeAndAddHeadersAsync(context, pid, session: null);
            return;
        }

        var scope = context.RequestServices.GetRequiredService<LabelRequestContext>();
        LabelSession? session = null;
        try
        {
            // Bulk label operations and snapshots keep the real project lock:
            // they rewrite many rows and cannot be guarded per row.
            session = await labels.EnterAsync(scope, pid, recovery: true,
                expectedRevision: null, ct: context.RequestAborted);

            // Re-read after lock acquisition. The pre-lock value can race a
            // completed restore.
            var locked = await store.Collection("project").Find(new BsonDocument("_id", pid))
                .Project(new BsonDocument("label_revision", 1))
                .FirstOrDefaultAsync(context.RequestAborted);
            revision = locked?.Integer("label_revision", 0) ?? 0;

            AssertRevisionHeader(context, pid, revision);

            // Headers must be attached before the response starts. A minimal API
            // endpoint writes its body before this middleware resumes, so setting
            // them only in finally would be too late.
            context.Response.OnStarting(async () =>
            {
                await AddRevisionHeadersAsync(context, pid).ConfigureAwait(false);
            });

            await next(context);
        }
        finally
        {
            try
            {
                await AddRevisionHeadersAsync(context, pid).ConfigureAwait(false);
            }
            finally
            {
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Fast-fail validation of X-Label-Revision on label writes after a bulk
    /// operation. Optimistic single-row writers re-validate per row with a
    /// label_epoch CAS filter, so this check only avoids wasted work.
    /// </summary>
    private void AssertRevisionHeader(HttpContext context, ObjectId pid, long revision)
    {
        var request = context.Request;
        if (request.Method is "GET" or "HEAD")
        {
            return;
        }

        var labelPath = LabelResources.Any(resource =>
            request.Path.Value?.Contains("/" + resource, StringComparison.Ordinal) == true);

        var expected = request.Headers.TryGetValue("X-Label-Revision", out var values)
            ? values.ToString()
            : null;

        if (labelPath && revision > 0 && expected is null)
            throw LabelErrors.Busy("批量操作后的标签写入必须携带版本号，请重新打开编辑页面");

        if (expected is not null &&
            expected != revision.ToString(CultureInfo.InvariantCulture))
            throw LabelErrors.Busy("标签数据已被批量修改，请重新打开编辑页面");
    }

    private async Task InvokeAndAddHeadersAsync(HttpContext context, ObjectId pid, LabelSession? session)
    {
        try
        {
            context.Response.OnStarting(async () =>
            {
                await AddRevisionHeadersAsync(context, pid).ConfigureAwait(false);
            });
            await next(context);
        }
        finally
        {
            try
            {
                await AddRevisionHeadersAsync(context, pid).ConfigureAwait(false);
            }
            finally
            {
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task AddRevisionHeadersAsync(HttpContext context, ObjectId pid)
    {
        // This hook is best effort: it must never delay a request indefinitely
        // and it must never keep an owned label lock from being released.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (context.Response.HasStarted) return;
            if (hooks?.BeforeRevisionHeadersAsync is { } before)
                await before(context, timeout.Token).ConfigureAwait(false);
            var row = await store.Collection("project").Find(new BsonDocument("_id", pid))
                .Project(new BsonDocument("label_revision", 1))
                .FirstOrDefaultAsync(timeout.Token);
            var revision = row?.Integer("label_revision", 0) ?? 0;
            context.Response.Headers["X-Label-Project"] = pid.ToString();
            context.Response.Headers["X-Label-Revision"] = revision.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.Append("Access-Control-Expose-Headers",
                "X-Label-Project, X-Label-Revision");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Bounded header enrichment timed out; preserving the endpoint
            // result is more important than a diagnostic header.
        }
        catch (InvalidOperationException)
        {
            // Response already started (for example a streaming endpoint);
            // headers can no longer be changed.
        }
        catch (MongoException)
        {
            // The after-hook is best effort and must not hide the original error.
        }
        catch (Exception)
        {
            // A test seam or an unexpected header failure must not mask the
            // original response or prevent lock release.
        }
    }

    private async Task<ObjectId?> ResolveProjectIdAsync(HttpContext context, CancellationToken ct)
    {
        if (TryRouteId(context, "projectId", "project_id", out var projectId))
            return projectId;

        if (TryRouteId(context, "fileId", "file_id", out var fileId))
            return await FileProjectAsync(fileId, ct);
        if (TryRouteId(context, "sourceId", "source_id", out var sourceId))
            return await SourceProjectAsync(sourceId, ct);
        if (TryRouteId(context, "translationId", "translation_id", out var translationId))
            return await TranslationProjectAsync(translationId, ct);
        if (TryRouteId(context, "tipId", "tip_id", out var tipId))
            return await TipProjectAsync(tipId, ct);
        if (TryRouteId(context, "targetId", "target_id", out var targetId))
        {
            var target = await store.Collection("target").Find(new BsonDocument("_id", targetId))
                .FirstOrDefaultAsync(ct);
            return target is null ? null : AsObjectId(target.GetValue("t", BsonNull.Value));
        }

        // Minimal APIs normally expose RouteValues, but the guard is also useful
        // when the host maps routes without a handler parameter name.
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries)
                       ?? [];
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            var resource = segments[index];
            if (!ObjectId.TryParse(segments[index + 1], out var id)) continue;
            switch (resource)
            {
                case "projects": return id;
                case "files": return await FileProjectAsync(id, ct);
                case "sources": return await SourceProjectAsync(id, ct);
                case "translations": return await TranslationProjectAsync(id, ct);
                case "tips": return await TipProjectAsync(id, ct);
                case "targets":
                    var target = await store.Collection("target").Find(new BsonDocument("_id", id))
                        .FirstOrDefaultAsync(ct);
                    return target is null ? null : AsObjectId(target.GetValue("t", BsonNull.Value));
            }
        }

        return null;
    }

    private static bool TryRouteId(HttpContext context, string camel, string snake, out ObjectId id)
    {
        id = default;
        object? raw = null;
        if (context.Request.RouteValues.TryGetValue(camel, out var value) ||
            context.Request.RouteValues.TryGetValue(snake, out value))
            raw = value;
        return raw is not null && ObjectId.TryParse(raw.ToString(), out id);
    }

    private async Task<ObjectId?> FileProjectAsync(ObjectId fileId, CancellationToken ct)
    {
        var file = await store.Collection("file").Find(new BsonDocument("_id", fileId)).FirstOrDefaultAsync(ct);
        return file is null ? null : AsObjectId(file.GetValue("p", BsonNull.Value));
    }

    private async Task<ObjectId?> SourceProjectAsync(ObjectId sourceId, CancellationToken ct)
    {
        var source = await store.Collection("source").Find(new BsonDocument("_id", sourceId)).FirstOrDefaultAsync(ct);
        var fileId = source is null ? null : AsObjectId(source.GetValue("f", BsonNull.Value));
        return fileId is null ? null : await FileProjectAsync(fileId.Value, ct);
    }

    private async Task<ObjectId?> TranslationProjectAsync(ObjectId translationId, CancellationToken ct)
    {
        var translation = await store.Collection("translation").Find(new BsonDocument("_id", translationId))
            .FirstOrDefaultAsync(ct);
        var sourceId = translation is null ? null : AsObjectId(translation.GetValue("o", BsonNull.Value));
        return sourceId is null ? null : await SourceProjectAsync(sourceId.Value, ct);
    }

    private async Task<ObjectId?> TipProjectAsync(ObjectId tipId, CancellationToken ct)
    {
        var tip = await store.Collection("tip").Find(new BsonDocument("_id", tipId)).FirstOrDefaultAsync(ct);
        var sourceId = tip is null ? null : AsObjectId(tip.GetValue("o", BsonNull.Value));
        return sourceId is null ? null : await SourceProjectAsync(sourceId.Value, ct);
    }

    private static ObjectId? AsObjectId(BsonValue value)
    {
        if (value.IsObjectId) return value.AsObjectId;
        if (value.IsString && ObjectId.TryParse(value.AsString, out var parsed)) return parsed;
        return null;
    }
}

public static class LabelRequestGuardExtensions
{
    public static IApplicationBuilder UseLabelRequestGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<LabelRequestGuard>();
}
