namespace Shoreline.Api.Features;

public static class AdminStorageOverviewRegistration
{
    public static IServiceCollection AddAdminStorageOverviewServices(this IServiceCollection services)
    {
        services.AddSingleton<AdminStorageOverviewService>();
        return services;
    }

    public static WebApplication MapAdminStorageOverviewEndpoints(this WebApplication app)
    {
        app.MapAdminStorageOverview();
        return app;
    }
}
