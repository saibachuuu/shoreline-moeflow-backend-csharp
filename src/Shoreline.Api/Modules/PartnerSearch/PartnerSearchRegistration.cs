using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shoreline.Api.Modules.PartnerSearch;

public static class PartnerSearchRegistration
{
    /// <summary>
    /// Registers the optional partner-search module. Program.cs is deliberately
    /// not modified in this phase; the host must call this method (and
    /// MapPartnerSearch) explicitly. Registering the module does not enable the
    /// feature: that still depends on the site setting partner_search_enabled.
    /// </summary>
    public static IServiceCollection AddPartnerSearchServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PartnerSearchCoverUrl>();
        services.AddScoped<PartnerSearchService>();
        return services;
    }
}
