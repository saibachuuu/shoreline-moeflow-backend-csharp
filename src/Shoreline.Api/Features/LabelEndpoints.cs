using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// E1 label routes. Permission and error behavior live in LabelService so
/// direct service calls and HTTP calls share the same rules.
/// </summary>
public static class LabelEndpoints
{
    public static void MapLabels(this WebApplication app)
    {
        app.MapGet("/v1/files/{fileId}/sources", ListSourcesAsync);
        app.MapMethods("/v1/files/{fileId}/sources", ["PATCH"], BatchSelectAsync);
        app.MapPost("/v1/files/{fileId}/sources", CreateSourceAsync);

        app.MapPut("/v1/sources/{sourceId}", EditSourceAsync);
        app.MapDelete("/v1/sources/{sourceId}", DeleteSourceAsync);
        app.MapPut("/v1/sources/{sourceId}/rank", MoveRankAsync);
        app.MapPost("/v1/sources/{sourceId}/translations", CreateTranslationAsync);
        app.MapPost("/v1/sources/{sourceId}/tips", CreateTipAsync);

        app.MapPut("/v1/translations/{translationId}", EditTranslationAsync);
        app.MapDelete("/v1/translations/{translationId}", DeleteTranslationAsync);

        app.MapPut("/v1/tips/{tipId}", EditTipAsync);
        app.MapDelete("/v1/tips/{tipId}", DeleteTipAsync);
    }

    private static async Task<IResult> ListSourcesAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string fileId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var fileIdValue = ParseFileId(fileId);
        var targetId = RequiredQueryObjectId(context.Request.Query["target_id"].ToString(), "target_id");
        var paging = true;
        if (context.Request.Query.TryGetValue("paging", out var raw))
            paging = IsTrue(raw.ToString());

        var result = await labels.ListSourcesAsync(context, actor, fileIdValue, targetId, paging, ct);
        if (!result.Paged) return Results.Json(result.Items);
        new Pagination(result.Page, result.Limit).Apply(context.Response, result.Total);
        return Results.Json(result.Items);
    }

    private static async Task<IResult> BatchSelectAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string fileId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var fileIdValue = ParseFileId(fileId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Array) throw ApiError.Invalid("Invalid request");

        var selections = new List<BatchSelection>();
        foreach (var element in body.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
            selections.Add(new BatchSelection(
                RequiredObjectId(element, "source_id"),
                RequiredObjectId(element, "translation_id")));
        }

        await labels.BatchSelectAsync(context, actor, fileIdValue, selections, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateSourceAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string fileId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var fileIdValue = ParseFileId(fileId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");

        var content = OptionalString(body, "content") ?? "";
        var x = OptionalDouble(body, "x") ?? 0;
        var y = OptionalDouble(body, "y") ?? 0;
        var positionType = OptionalInteger(body, "position_type") ?? LabelService.PositionIn;

        var result = await labels.CreateSourceAsync(context, actor, fileIdValue,
            content, x, y, positionType, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> EditSourceAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string sourceId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var sourceIdValue = ParseSourceId(sourceId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");

        var edit = new SourceEditRequest(
            OptionalString(body, "content"),
            OptionalDouble(body, "x"),
            OptionalDouble(body, "y"),
            OptionalDoubleList(body, "vertices"),
            OptionalInteger(body, "position_type"));

        return Results.Json(await labels.EditSourceAsync(context, actor, sourceIdValue, edit, ct));
    }

    private static async Task<IResult> DeleteSourceAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string sourceId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        await labels.DeleteSourceAsync(context, actor, ParseSourceId(sourceId), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MoveRankAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string sourceId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var sourceIdValue = ParseSourceId(sourceId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
        var next = RequiredString(body, "next_source_id");

        return Results.Json(await labels.MoveSourceRankAsync(context, actor, sourceIdValue, next, ct));
    }

    private static async Task<IResult> CreateTranslationAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string sourceId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var sourceIdValue = ParseSourceId(sourceId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");

        var content = RequiredString(body, "content");
        var targetId = RequiredObjectId(body, "target_id");
        var result = await labels.CreateTranslationAsync(context, actor, sourceIdValue, content, targetId, ct);
        return result is null ? Results.NoContent() : Results.Json(result);
    }

    private static async Task<IResult> EditTranslationAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string translationId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var translationIdValue = ParseTranslationId(translationId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");

        var edit = new TranslationEditRequest(
            OptionalString(body, "content"),
            OptionalString(body, "proofread_content"),
            OptionalBoolean(body, "selected"));
        var result = await labels.EditTranslationAsync(context, actor, translationIdValue, edit, ct);
        return result is null ? Results.NoContent() : Results.Json(result);
    }

    private static async Task<IResult> DeleteTranslationAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string translationId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        await labels.DeleteTranslationAsync(context, actor, ParseTranslationId(translationId), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateTipAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string sourceId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var sourceIdValue = ParseSourceId(sourceId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");

        var content = RequiredString(body, "content");
        var targetId = RequiredObjectId(body, "target_id");
        return Results.Json(await labels.CreateTipAsync(context, actor, sourceIdValue, content, targetId, ct));
    }

    private static async Task<IResult> EditTipAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string tipId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        var tipIdValue = ParseTipId(tipId);
        var body = await ReadJsonAsync(context, ct);
        if (body.ValueKind != JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
        var content = RequiredString(body, "content");

        return Results.Json(await labels.EditTipAsync(context, actor, tipIdValue, content, ct));
    }

    private static async Task<IResult> DeleteTipAsync(
        HttpContext context,
        CurrentUser current,
        LabelService labels,
        string tipId,
        CancellationToken ct)
    {
        var actor = await current.RequireAsync(context, ct);
        await labels.DeleteTipAsync(context, actor, ParseTipId(tipId), ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------
    // Request parsing helpers
    // ------------------------------------------------------------------

    private static async Task<JsonElement> ReadJsonAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw ApiError.Invalid("Invalid request");
        }
    }

    private static ObjectId ParseFileId(string raw) =>
        ObjectId.TryParse(raw, out var id)
            ? id
            : throw new ApiError("FileNotExistError", 8001, "文件/文件夹不存在，可能已被删除");

    private static ObjectId ParseSourceId(string raw) =>
        ObjectId.TryParse(raw, out var id)
            ? id
            : throw new ApiError("SourceNotExistError", 8005, "原文不存在，已被删除");

    private static ObjectId ParseTranslationId(string raw) =>
        ObjectId.TryParse(raw, out var id)
            ? id
            : throw new ApiError("NotExistError", 104, "未定义的错误");

    private static ObjectId ParseTipId(string raw) =>
        ObjectId.TryParse(raw, out var id)
            ? id
            : throw new ApiError("NotExistError", 104, "未定义的错误");

    private static ObjectId RequiredObjectId(string raw, string field) =>
        ObjectId.TryParse(raw, out var id) ? id : throw ApiError.Field(field, "Invalid ObjectId.");

    private static ObjectId RequiredQueryObjectId(string raw, string field) =>
        string.IsNullOrEmpty(raw)
            ? throw ApiError.Field(field, "Missing data for required field.")
            : RequiredObjectId(raw, field);

    private static ObjectId RequiredObjectId(JsonElement body, string field) =>
        RequiredObjectId(RequiredString(body, field), field);

    private static string RequiredString(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw ApiError.Field(field, "Missing data for required field.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw ApiError.Field(field, "Not a valid string.");
        return value.GetString();
    }

    private static double? OptionalDouble(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return 1;
        if (value.ValueKind == JsonValueKind.False) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        throw ApiError.Field(field, "Not a valid number.");
    }

    private static int? OptionalInteger(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return 1;
        if (value.ValueKind == JsonValueKind.False) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        throw ApiError.Field(field, "整数格式不正确");
    }

    private static bool? OptionalBoolean(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value)) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        if (value.ValueKind == JsonValueKind.True ||
            text is "1" or "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" or "t" or "T" or "y" or "Y")
            return true;
        if (value.ValueKind == JsonValueKind.False ||
            text is "0" or "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" or "f" or "F" or "n" or "N")
            return false;
        throw ApiError.Field(field, "布尔值格式不正确");
    }

    private static IReadOnlyList<double>? OptionalDoubleList(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) throw ApiError.Field(field, "Not a valid list.");
        var result = new List<double>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
                result.Add(number);
            else if (element.ValueKind == JsonValueKind.String &&
                     double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                result.Add(number);
            else
                throw ApiError.Field(field, "Not a valid number.");
        }
        return result;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
