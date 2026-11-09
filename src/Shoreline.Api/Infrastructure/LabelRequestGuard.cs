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
/// while a recovery is pending, takes the shared project label lock for every
/// non-OPTIONS request (reads included), enforces X-Label-Revision on writes
/// after a bulk operation, and appends the current revision headers.
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

        var scope = context.RequestServices.GetRequiredService<LabelRequestContext>();
        LabelSession? session = null;
        try
        {
            session = await labels.EnterAsync(scope, pid, recovery: isOperation,
                expectedRevision: null, ct: context.RequestAborted);

            // Re-read after lock acquisition. The pre-lock value can race a
            // completed restore.
            var locked = await store.Collection("project").Find(new BsonDocument("_id", pid))
                .Project(new BsonDocument("label_revision", 1))
                .FirstOrDefaultAsync(context.RequestAborted);
            revision = locked?.Integer("label_revision", 0) ?? 0;

            var expected = request.Headers.TryGetValue("X-Label-Revision", out var values)
                ? values.ToString()
                : null;
            var labelPath = LabelResources.Any(resource =>
                request.Path.Value?.Contains("/" + resource, StringComparison.Ordinal) == true);

            if (request.Method is not ("GET" or "HEAD") && labelPath && revision > 0 && expected is null)
                throw LabelErrors.Busy("批量操作后的标签写入必须携带版本号，请重新打开编辑页面");

            if (request.Method is not ("GET" or "HEAD") && expected is not null &&
                expected != revision.ToString(CultureInfo.InvariantCulture))
                throw LabelErrors.Busy("标签数据已被批量修改，请重新打开编辑页面");

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
