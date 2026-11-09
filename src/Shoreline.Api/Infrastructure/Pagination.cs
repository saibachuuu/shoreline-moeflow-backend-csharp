using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shoreline.Api.Infrastructure;

public readonly record struct Pagination(int Page, int Limit)
{
    public int Skip => checked((Page - 1) * Limit);

    public static Pagination From(HttpRequest request) => From(request, null, null);

    // Legacy APIKIT_PAGINATION_DEFAULT_LIMIT defaults to 30 and MAX_LIMIT to
    // 100. Callers with a different explicit default (for example insights)
    // can pass one without changing global configuration.
    public static Pagination From(HttpRequest request, int? defaultLimit, int? maxLimit)
    {
        var configuration = ResolveConfiguration(request);
        var configuredDefault = PositiveSetting(configuration, "Pagination:DefaultLimit", 30);
        var configuredMax = PositiveSetting(configuration, "Pagination:MaxLimit", 100);

        var effectiveDefault = defaultLimit is > 0 ? defaultLimit.Value : configuredDefault;
        var effectiveMax = maxLimit is > 0 ? maxLimit.Value : configuredMax;
        if (effectiveMax <= 0) effectiveMax = 100;
        if (effectiveDefault <= 0) effectiveDefault = 30;
        if (effectiveDefault > effectiveMax) effectiveDefault = effectiveMax;

        var page = int.TryParse(request.Query["page"], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var requestedPage) && requestedPage > 0
            ? requestedPage
            : 1;
        var limit = int.TryParse(request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var requestedLimit) && requestedLimit > 0
            ? Math.Min(requestedLimit, effectiveMax)
            : effectiveDefault;
        if ((long)(page - 1) * limit > int.MaxValue) throw ApiError.Invalid("Page is too large");
        return new(page, limit);
    }

    private static IConfiguration? ResolveConfiguration(HttpRequest request)
    {
        try
        {
            return request.HttpContext.RequestServices?.GetService<IConfiguration>();
        }
        catch (InvalidOperationException)
        {
            // DefaultHttpContext in direct unit tests has no request services.
            return null;
        }
    }

    private static int PositiveSetting(IConfiguration? configuration, string key, int fallback)
    {
        if (configuration is null) return fallback;
        return int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : fallback;
    }

    public void Apply(HttpResponse response, long count)
    {
        response.Headers["X-Pagination-Page"] = Page.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Pagination-Limit"] = Limit.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Pagination-Count"] = count.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Pagination-Page-Count"] = ((count + Limit - 1) / Limit).ToString(CultureInfo.InvariantCulture);
    }
}
