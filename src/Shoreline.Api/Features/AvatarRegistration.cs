namespace Shoreline.Api.Features;

public static class AvatarRegistration
{
    public static IServiceCollection AddAvatarServices(this IServiceCollection services)
    {
        services.AddScoped<AvatarService>();
        return services;
    }

    public static WebApplication MapAvatarEndpoints(this WebApplication app)
    {
        app.MapAvatar();
        return app;
    }
}
