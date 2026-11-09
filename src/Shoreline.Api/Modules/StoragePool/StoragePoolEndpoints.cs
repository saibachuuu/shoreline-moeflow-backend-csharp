using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Modules.StoragePool;

public static class StoragePoolEndpoints
{
    public static void MapStoragePool(this WebApplication app)
    {
        var group = app.MapGroup("/v1/admin/storage-pool");

        group.MapGet("/sources", async (HttpContext context, CurrentUser current,
            StoragePoolSourceService sources, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var pagination = Pagination.From(context.Request);
            var rows = await sources.ListAsync(pagination.Skip, pagination.Limit, ct);
            var count = await sources.CountAsync(ct);
            pagination.Apply(context.Response, count);
            return rows.Select(StoragePoolApiJson.ToObject).ToList();
        });

        group.MapPost("/sources", async (HttpContext context, CurrentUser current,
            StoragePoolSourceService sources, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var data = await BodyAsync(context.Request, ct);
            var row = await sources.CreateAsync(data, actor["_id"].AsObjectId, ct);
            return StoragePoolApiJson.ToObject(row);
        });

        group.MapPut("/sources/{sourceId}", async (string sourceId, HttpContext context, CurrentUser current,
            StoragePoolSourceService sources, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var data = await BodyAsync(context.Request, ct);
            var row = await sources.UpdateAsync(sourceId, data, actor["_id"].AsObjectId, ct);
            return StoragePoolApiJson.ToObject(row);
        });

        group.MapDelete("/sources/{sourceId}", async (string sourceId, HttpContext context, CurrentUser current,
            StoragePoolSourceService sources, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            await sources.DeleteAsync(sourceId, ct);
            return new { deleted = true };
        });

        group.MapPost("/sources/{sourceId}/test", async (string sourceId, HttpContext context, CurrentUser current,
            StoragePoolSourceService sources, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var result = await sources.TestAsync(sourceId, ct);
            return StoragePoolApiJson.ToObject(result);
        });

        group.MapGet("/transfers", async (HttpContext context, CurrentUser current,
            StoragePoolTransferService transfers, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var pagination = Pagination.From(context.Request);
            var projectId = context.Request.Query["project_id"].ToString();
            var (rows, count) = await transfers.ListAsync(projectId, pagination.Skip, pagination.Limit, ct);
            pagination.Apply(context.Response, count);
            return rows.Select(StoragePoolApiJson.ToObject).ToList();
        });

        group.MapPost("/transfers/{transferId}/retry", async (string transferId, HttpContext context,
            CurrentUser current, StoragePoolTransferService transfers, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            await transfers.RetryAsync(transferId, ct);
            return StoragePoolApiJson.ToObject(await transfers.GetAsync(transferId, ct));
        });

        group.MapPost("/transfers/{transferId}/cancel", async (string transferId, HttpContext context,
            CurrentUser current, StoragePoolTransferService transfers, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            await transfers.CancelAsync(transferId, ct);
            return StoragePoolApiJson.ToObject(await transfers.GetAsync(transferId, ct));
        });

        group.MapDelete("/transfers/{transferId}", async (string transferId, HttpContext context,
            CurrentUser current, StoragePoolTransferService transfers, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            await transfers.DeleteAsync(transferId, ct);
            return new { deleted = true };
        });

        group.MapPost("/projects/{projectId}/migrate", async (string projectId, HttpContext context,
            CurrentUser current, StoragePoolProjectService projects, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var data = await BodyAsync(context.Request, ct);
            return StoragePoolApiJson.ToObject(await projects.MigrateAsync(projectId, data, ct));
        });

        group.MapPost("/projects/{projectId}/restore", async (string projectId, HttpContext context,
            CurrentUser current, StoragePoolProjectService projects, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            return StoragePoolApiJson.ToObject(await projects.RestoreAsync(projectId, default, ct));
        });

        group.MapPost("/projects/{projectId}/move", async (string projectId, HttpContext context,
            CurrentUser current, StoragePoolProjectService projects, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var data = await BodyAsync(context.Request, ct);
            return StoragePoolApiJson.ToObject(await projects.MoveAsync(projectId, data, ct));
        });

        group.MapPost("/projects/{projectId}/lock", async (string projectId, HttpContext context,
            CurrentUser current, StoragePoolProjectService projects, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var data = await OptionalBodyAsync(context.Request, ct);
            return StoragePoolApiJson.ToObject(await projects.LockAsync(projectId, data, ct));
        });

        group.MapGet("/stats", async (HttpContext context, CurrentUser current,
            StoragePoolProjectService projects, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            return StoragePoolApiJson.ToObject(await projects.StatsAsync(ct));
        });

        group.MapGet("/usage", async (HttpContext context, CurrentUser current,
            StoragePoolProjectService projects, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            return StoragePoolApiJson.ToObject(await projects.UsageAsync(ct));
        });

        group.MapGet("/settings", async (HttpContext context, CurrentUser current,
            StoragePoolSettingsService settings, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            return StoragePoolApiJson.ToObject(await settings.GetViewAsync(ct));
        });

        group.MapPut("/settings", async (HttpContext context, CurrentUser current,
            StoragePoolSettingsService settings, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(context, ct);
            var data = await BodyAsync(context.Request, ct);
            return StoragePoolApiJson.ToObject(await settings.UpdateAsync(data, actor["_id"].AsObjectId, ct));
        });

        group.MapPost("/scan", async (HttpContext context, CurrentUser current,
            StoragePoolScanService scan, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(context, ct);
            var data = await OptionalBodyAsync(context.Request, ct);
            var force = StoragePoolJson.Has(data, "force") && StoragePoolJson.RequiredBoolean(data, "force");
            return StoragePoolApiJson.ToObject(await scan.RunAsync(force, ct));
        });
    }

    private static async Task<JsonElement> BodyAsync(HttpRequest request, CancellationToken ct)
    {
        var value = await OptionalBodyAsync(request, ct);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw ApiError.Invalid("请求体不能为空");
        return value;
    }

    private static async Task<JsonElement> OptionalBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0) return default;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
