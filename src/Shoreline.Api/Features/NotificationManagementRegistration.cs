using Microsoft.Extensions.DependencyInjection;

namespace Shoreline.Api.Features;

// Kept separate from Program.cs so the N3 slice can be wired only after the
// N2 publish service is present in the host.
public static class NotificationManagementRegistration
{
    public static IServiceCollection AddNotificationManagementServices(
        this IServiceCollection services)
    {
        services.AddScoped<NotificationManagementService>();
        return services;
    }
}
