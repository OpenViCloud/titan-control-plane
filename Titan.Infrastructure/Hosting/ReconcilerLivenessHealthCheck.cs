using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Options;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Same shape as MeterLivenessHealthCheck, over the polls every replica records in core.reconciler_polls. Only the
/// kinds the worker runs and the clusters configured now count: a cluster removed from config, or a kind no longer
/// reconciled, leaves a row that never polls again (titan-ops architecture/desired-and-current.md#desired-and-current).
/// </summary>
public sealed class ReconcilerLivenessHealthCheck(
    ReconcilerHeartbeat heartbeat,
    ClusterOptions clusters,
    IOptions<ReconcilerLivenessOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, DateTimeOffset> polls = Expected(await heartbeat.SnapshotAsync(ct), clusters.Clusters.Select(c => c.Id));
        return Evaluate(DateTimeOffset.UtcNow, polls, options.Value.StaleMinutes);
    }

    /// The polls of a known kind in a configured cluster.
    public static IReadOnlyDictionary<string, DateTimeOffset> Expected(
        IReadOnlyDictionary<string, DateTimeOffset> polls, IEnumerable<string> clusterIds)
    {
        var expected = new HashSet<string>(
            ReconcilerHeartbeat.Kinds.SelectMany(kind => clusterIds.Select(id => ReconcilerHeartbeat.KeyFor(kind, id))));
        return polls.Where(p => expected.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
    }

    /// No poll is Healthy: nothing is expected yet, so there is no evidence of a stopped reconciler.
    public static HealthCheckResult Evaluate(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> polls,
        int staleMinutes)
    {
        TimeSpan staleWindow = TimeSpan.FromMinutes(staleMinutes);

        var stale = polls.Where(p => now - p.Value > staleWindow).Select(p => p.Key).ToList();
        if (stale.Count == 0)
        {
            return HealthCheckResult.Healthy("every reconciler polled within the stale window");
        }

        stale.Sort(StringComparer.Ordinal);
        return HealthCheckResult.Unhealthy($"reconcilers not polling: {string.Join(", ", stale)}");
    }
}
