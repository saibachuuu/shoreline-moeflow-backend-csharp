using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Modules.ZitengPartner;

/// <summary>
/// Add <c>AddZitengPartner()</c>, <c>MapZitengPartner()</c> and
/// <c>InitializeZitengPartnerAsync()</c> to the host. The host intentionally owns
/// those calls so this module does not edit shared Program/common files.
/// </summary>
public static class ZitengPartnerRegistration
{
    public static IServiceCollection AddZitengPartner(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
            ZitengPartnerSettings.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ZitengPartnerStore>();
        services.AddSingleton<IZitengPartnerDelay, ZitengPartnerSystemDelay>();
        services.AddSingleton<IZitengPartnerApiGate, ZitengPartnerApiGate>();
        services.AddHttpClient<ZitengPartnerClient>()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // Same connection-time DNS rebinding guard as archive import:
                // a configured private HTTPS destination can never receive the Bearer API key.
                ConnectCallback = (context, ct) => PublicNetworkGuard.ConnectAsync(context, ct, "合作方主机"),
            });
        services.AddTransient<ZitengPartnerService>();
        services.AddTransient<ZitengPartnerProjectCreatedNotifier>();
        services.AddTransient<IProjectCreatedHook, ZitengPartnerProjectCreatedHook>();
        // The generic Mongo-backed JobWorker already runs in the host; this adds
        // the replacement for tasks.ziteng_partner.check_project.
        services.AddSingleton<IJobHandler, ZitengCheckJobHandler>();
        return services;
    }

    /// <summary>Create the module-owned indexes; index failure is not fatal.</summary>
    public static async Task InitializeZitengPartnerAsync(this IServiceProvider services,
        CancellationToken ct = default)
    {
        // Resolve the settings once during startup so an invalid API URL fails the
        // boot instead of surfacing as a per-request 500 after deployment.
        _ = services.GetRequiredService<ZitengPartnerSettings>();
        try
        {
            await services.GetRequiredService<ZitengPartnerStore>().EnsureIndexesAsync(ct);
        }
        catch (Exception exception)
        {
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("ZitengPartner");
            logger?.LogError("建立紫藤查重索引失败: {Error}", exception.GetType().Name);
        }
    }
}

internal sealed class ZitengPartnerProjectCreatedHook(ZitengPartnerProjectCreatedNotifier notifier) : IProjectCreatedHook
{
    public Task OnProjectCreatedAsync(ObjectId projectId, CancellationToken ct) =>
        notifier.TryNotifyAsync(projectId.ToString(), ct);
}
