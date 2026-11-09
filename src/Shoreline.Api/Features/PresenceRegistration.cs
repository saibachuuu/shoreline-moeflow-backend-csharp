using Microsoft.Extensions.DependencyInjection;

namespace Shoreline.Api.Features;

public static class PresenceRegistration
{
    public static IServiceCollection AddPresenceServices(this IServiceCollection services)
    {
        services.AddScoped<PresenceService>();
        return services;
    }
}
