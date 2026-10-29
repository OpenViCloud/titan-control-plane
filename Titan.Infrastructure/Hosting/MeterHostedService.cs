using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.Metering;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// One timer ticks every registered <see cref="IMeter"/> (titan-ops architecture/money-and-wallet.md#append-only-metering).
/// Each meter has its own try/catch, so one throwing does not stop the others. Every attempt is stamped
/// into <see cref="MeterHeartbeat"/> so the liveness check can see a meter that has gone silent.
/// </summary>
public class MeterHostedService(
    IServiceScopeFactory scopeFactory,
    MeterHeartbeat heartbeat,
    IOptions<BillingMeterOptions> options,
    ILogger<MeterHostedService> logger) : BackgroundService
{
    private readonly BillingMeterOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Declared up front so liveness can tell "not ticked yet" (the timer is wait-first) from a stopped meter.
        using (var scope = scopeFactory.CreateScope())
        {
            foreach (var meter in scope.ServiceProvider.GetServices<IMeter>())
                heartbeat.Register(meter.Name, DateTimeOffset.UtcNow);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_opts.IntervalMinutes));

        // Wait-first: no pass during startup, and skips host tests running on the InMemory provider.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            foreach (var meter in scope.ServiceProvider.GetServices<IMeter>())
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    await meter.RunTickAsync(now, stoppingToken);
                    heartbeat.Record(meter.Name, success: true, now);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The heartbeat stamps the failure so a run of them surfaces on /healthz/meters.
                    logger.LogError(ex, "{Meter} meter tick failed", meter.Name);
                    heartbeat.Record(meter.Name, success: false, now);
                }
            }
        }
    }
}
