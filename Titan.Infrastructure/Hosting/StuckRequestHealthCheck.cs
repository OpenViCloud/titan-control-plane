using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// The line of /healthz/reconcilers that says whether a request has waited past the threshold
/// (titan-ops architecture/resource-lifecycle.md#lost-and-stale). Degraded while the command row is still open — the
/// worker is slow or absent, which the reconciler liveness line says more precisely; Unhealthy when a row is Done,
/// Failed or missing — no command will report the request. Not an alert: nothing polls this.
/// </summary>
public sealed class StuckRequestHealthCheck(
    IPendingRequestReader requests,
    IOptions<RequestStuckOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        TimeSpan threshold = TimeSpan.FromMinutes(options.Value.ThresholdMinutes);
        IReadOnlyList<StuckRequest> stuck = await requests.ListStuckAsync(threshold, ct);
        return Evaluate(stuck, options.Value.ThresholdMinutes);
    }

    /// Pure, so StuckRequestHealthCheckTests can hand it the rows. Worst first.
    public static HealthCheckResult Evaluate(IReadOnlyList<StuckRequest> stuck, int thresholdMinutes)
    {
        if (stuck.Count == 0)
        {
            return HealthCheckResult.Healthy($"no request waiting longer than {thresholdMinutes} min");
        }

        int neverEnding = stuck.Count(r => !r.CommandIsOpen);
        if (neverEnding > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"{neverEnding} request(s) waiting longer than {thresholdMinutes} min whose command row is closed or missing — " +
                "no command will report them (GET /v1/admin/requests/stuck)");
        }

        return HealthCheckResult.Degraded(
            $"{stuck.Count} request(s) waiting longer than {thresholdMinutes} min with their command still open — " +
            "the worker is slow or absent (GET /v1/admin/requests/stuck)");
    }
}
