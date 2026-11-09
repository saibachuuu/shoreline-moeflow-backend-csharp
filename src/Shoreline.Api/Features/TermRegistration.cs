using Microsoft.Extensions.DependencyInjection;

namespace Shoreline.Api.Features;

public static class TermRegistration
{
    public static IServiceCollection AddTermServices(this IServiceCollection services)
    {
        services.AddScoped<TermService>();
        return services;
    }

    // The main integration host calls this after AddTermServices().
    public static WebApplication MapTerms(this WebApplication app)
    {
        app.MapTermEndpoints();
        return app;
    }
}
