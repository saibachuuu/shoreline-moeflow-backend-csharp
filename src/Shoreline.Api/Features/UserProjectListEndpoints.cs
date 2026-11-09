using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class UserProjectListEndpoints
{
    public static void MapUserProjectLists(this WebApplication app)
    {
        app.MapGet("/v1/user/projects", async (HttpContext ctx, CurrentUser current,
            UserProjectListService lists, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(ctx, ct);
            var page = Pagination.From(ctx.Request);
            var statuses = ctx.Request.Query["status"].Select(value => value ?? "").ToArray();
            var word = ctx.Request.Query["word"].FirstOrDefault() ?? "";
            var result = await lists.ListAsync(actor, statuses, word, page, ct);
            page.Apply(ctx.Response, result.Total);
            return result.Items;
        });
    }
}
