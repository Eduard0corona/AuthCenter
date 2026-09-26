using AuthCenter.Application.Interfaces;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services.Governance;

/// <summary>
/// Expires access requests nobody decided, completes the access reviews past their due date and
/// starts the recurring ones again. Safe with several instances: each campaign is claimed by one.
/// </summary>
public sealed class GovernanceMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<GovernanceSettings> settings,
    ILogger<GovernanceMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.MaintenanceEnabled)
            return;

        using var timer = new PeriodicTimer(settings.Value.MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Governance maintenance failed");
            }
        }
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var expired = await scope.ServiceProvider.GetRequiredService<IAccessGovernanceService>().ExpireRequestsAsync(ct);
            if (expired > 0)
                logger.LogInformation("Governance maintenance expired {Count} access requests", expired);
        }
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var campaigns = await scope.ServiceProvider.GetRequiredService<IAccessReviewService>().RunDueWorkAsync(ct);
            if (campaigns > 0)
                logger.LogInformation("Governance maintenance completed or repeated {Count} access reviews", campaigns);
        }
    }
}
