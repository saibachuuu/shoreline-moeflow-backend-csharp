using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Polls the database for due notifications and email deliveries.
/// A hosted service is used instead of a single job-queue item because a queued
/// job cannot be retried reliably after a process crash.
/// </summary>
public sealed class NotificationScanWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<NotificationScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (bool.TryParse(configuration["Notifications:WorkerEnabled"], out var workerEnabled) && !workerEnabled)
            return;

        var seconds = int.TryParse(configuration["Notifications:ScanIntervalSeconds"], out var parsed)
            ? Math.Max(1, parsed)
            : 10;
        var interval = TimeSpan.FromSeconds(seconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var publish = scope.ServiceProvider.GetRequiredService<NotificationPublishService>();
                await publish.ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("notification scan failed: {Type}", error.GetType().Name);
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
