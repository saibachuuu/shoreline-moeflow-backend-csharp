namespace Shoreline.Api.Features;

// Intentionally isolated registration hook for the additional list endpoints.
// The shared Program.cs is not modified by this branch/snapshot; the main agent
// wires these two calls after review.
public static class AdditionalListRegistration
{
    public static IServiceCollection AddAdditionalListServices(this IServiceCollection services)
    {
        services.AddScoped<UserProjectListService>();
        services.AddScoped<AdminListService>();
        return services;
    }

    public static void MapAdditionalLists(this WebApplication app)
    {
        app.MapUserProjectLists();
        app.MapAdminLists();
    }
}
