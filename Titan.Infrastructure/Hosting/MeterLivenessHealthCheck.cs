using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.Metering;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// A silently dead meter stops recording usage and under-bills with no error; this turns it into a signal on
/// /healthz/meters. A meter not yet ticked is measured from its registration, giving a boot grace window.
/// </summary>
public sealed class MeterLivenessHealthCheck(
    IMeterTicks ticks,
    IOptions<BillingMeterOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var opts = options.Value;
        IReadOnlyDictionary<string, MeterTickState> snapshot = await ticks.SnapshotAsync(ct);
        return Evaluate(DateTimeOffset.UtcNow, snapshot, opts.IntervalMinutes, opts.MeterStaleFactor);
    }

    /// <summary>
    /// Pure, so MeterLivenessHealthCheckTests can inject the clock. An empty snapshot is Healthy: there is no evidence of a
    /// stopped meter (the migration that adds a meter writes its row).
    /// </summary>
    public static HealthCheckResult Evaluate(
        DateTimeOffset now,
        IReadOnlyDictionary<string, MeterTickState> snapshot,
        int intervalMinutes,
        int staleFactor)
    {
        var staleWindow = TimeSpan.FromMinutes((double)intervalMinutes * staleFactor);

        var stale = new List<string>();
        foreach (var (name, state) in snapshot)
        {
            var reference = state.LastSuccessAt ?? state.RegisteredAt;
            if (now - reference > staleWindow)
                stale.Add(name);
        }

        if (stale.Count == 0)
            return HealthCheckResult.Healthy("all meters ticked within the stale window");

        stale.Sort(StringComparer.Ordinal);
        return HealthCheckResult.Unhealthy($"meters not ticking: {string.Join(", ", stale)}");
    }
}
