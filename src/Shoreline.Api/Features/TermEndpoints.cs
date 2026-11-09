using System.Globalization;
using System.Text.Json;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class TermEndpoints
{
    public static void MapTermEndpoints(this WebApplication app)
    {
        // app/apis/urls.py: team Blueprint /v1/teams/<team_id>/term-banks.
        app.MapGet("/v1/teams/{teamId}/term-banks", async (string teamId, HttpContext context,
            CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var page = ParsePage(context);
            var (rows, count) = await terms.ListBanksAsync(actor, teamId, Word(context), page.Skip, page.Limit, ct);
            Apply(context, page, count);
            return Results.Json(await terms.BanksViewAsync(rows, context, ct));
        });
        app.MapPost("/v1/teams/{teamId}/term-banks", async (string teamId, JsonElement data,
            HttpContext context, CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            await terms.CreateBankAsync(actor, teamId, data, ct);
            return new { message = "创建成功" };
        });

        // term_bank Blueprint /v1/term-banks/<term_bank_id> and /terms.
        app.MapPut("/v1/term-banks/{termBankId}", async (string termBankId, JsonElement data,
            HttpContext context, CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            await terms.EditBankAsync(actor, termBankId, data, ct);
            return new { message = "修改成功" };
        });
        app.MapDelete("/v1/term-banks/{termBankId}", async (string termBankId, HttpContext context,
            CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            await terms.DeleteBankAsync(actor, termBankId, ct);
            return new { message = "删除成功" };
        });
        app.MapGet("/v1/term-banks/{termBankId}/terms", async (string termBankId, HttpContext context,
            CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            var page = ParsePage(context);
            var (rows, count) = await terms.ListTermsAsync(actor, termBankId, Word(context), page.Skip,
                page.Limit, ct);
            Apply(context, page, count);
            return Results.Json(await terms.TermsViewAsync(rows, context, ct));
        });
        app.MapPost("/v1/term-banks/{termBankId}/terms", async (string termBankId, JsonElement data,
            HttpContext context, CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            await terms.CreateTermAsync(actor, termBankId, data, ct);
            return new { message = "创建成功" };
        });

        // term Blueprint /v1/terms/<term_id>; there is intentionally no GET here.
        app.MapPut("/v1/terms/{termId}", async (string termId, JsonElement data, HttpContext context,
            CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            await terms.EditTermAsync(actor, termId, data, ct);
            return new { message = "修改成功" };
        });
        app.MapDelete("/v1/terms/{termId}", async (string termId, HttpContext context,
            CurrentUser current, TermService terms, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(context, ct);
            await terms.DeleteTermAsync(actor, termId, ct);
            return new { message = "删除成功" };
        });
    }

    private static string Word(HttpContext context) => context.Request.Query["word"].ToString();

    // Public for direct compatibility tests. The shared helper now owns the
    // legacy default (30) and max (100), so term pages behave like other APIs.
    public static (int Page, int Limit, int Skip) ParsePage(HttpContext context)
    {
        var pagination = Pagination.From(context.Request);
        return (pagination.Page, pagination.Limit, pagination.Skip);
    }

    private static void Apply(HttpContext context, (int Page, int Limit, int Skip) page, long count)
    {
        context.Response.Headers["X-Pagination-Page"] = page.Page.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Pagination-Limit"] = page.Limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Pagination-Count"] = count.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Pagination-Page-Count"] =
            ((count + page.Limit - 1) / page.Limit).ToString(CultureInfo.InvariantCulture);
    }
}
