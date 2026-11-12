using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Deletes VM metric samples older than RetentionDays (titan-ops features/vm/design.md#vm-metrics). The worker writes one
/// row per VM per minute, so without it the table grows by 10,080 rows per VM each week. Local DB maintenance, like
/// ProviderCommandRetention, not a provider command.
/// </summary>
public class VmMetricRetention(
    IVmMetricStore store,
    IOptions<VmMetricRetentionOptions> options,
    TimeProvider clock,
    ILogger<VmMetricRetention> logger) : IScheduledJob
{
    public const string JobName = "vm-metric-retention";

    public string Name => JobName;

    public Task RunAsync(CancellationToken ct) =>
        RunOnceAsync(store, options.Value.RetentionDays, clock.GetUtcNow(), logger, ct);

    public static async Task<int> RunOnceAsync(
        IVmMetricStore store, int retentionDays, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        int removed = await store.DeleteSampledBeforeAsync(now.AddDays(-retentionDays), ct);
        if (removed > 0)
        {
            logger.LogInformation("VM metric retention removed {Count} samples older than {Days} days",
                removed, retentionDays);
        }
        return removed;
    }
}
