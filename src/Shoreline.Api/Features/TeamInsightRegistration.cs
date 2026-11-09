namespace Shoreline.Api.Features;

public static class TeamInsightRegistration
{
    public static IServiceCollection AddTeamInsightServices(this IServiceCollection services)
    {
        services.AddScoped<TeamInsightService>();
        return services;
    }
}
