using System.Globalization;

namespace Shoreline.Api.Infrastructure;

public readonly record struct Pagination(int Page, int Limit)
{
    public int Skip => checked((Page - 1) * Limit);
    public static Pagination From(HttpRequest request)
    {
        var page = int.TryParse(request.Query["page"], out var p) ? Math.Max(1, p) : 1;
        var limit = int.TryParse(request.Query["limit"], out var l) && l > 0 ? Math.Min(l, 100) : 20;
        if ((long)(page - 1) * limit > int.MaxValue) throw ApiError.Invalid("Page is too large");
        return new(page, limit);
    }
    public void Apply(HttpResponse response, long count)
    {
        response.Headers["X-Pagination-Page"] = Page.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Pagination-Limit"] = Limit.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Pagination-Count"] = count.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Pagination-Page-Count"] = ((count + Limit - 1) / Limit).ToString(CultureInfo.InvariantCulture);
    }
}
