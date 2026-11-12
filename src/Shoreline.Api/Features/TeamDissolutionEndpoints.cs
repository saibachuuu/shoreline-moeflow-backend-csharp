using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class TeamDissolutionEndpoints
{
    public static WebApplication MapTeamDissolutionEndpoints(this WebApplication app)
    {
        // TeamEndpoints currently has no DELETE. K1 owns this route directly;
        // adding DELETE to MapTeams would create a duplicate route.
        app.MapDelete("/v1/teams/{teamId}", async (
            HttpContext http,
            string teamId,
            CurrentUser current,
            TeamDissolutionService service,
            CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(http, ct);
            var result = await service.DeleteAsync(actor, teamId, ct);
            return Results.Json(new { message = LegacySuccessMessages.Get(http, result.Message) });
        });

        return app;
    }
}
