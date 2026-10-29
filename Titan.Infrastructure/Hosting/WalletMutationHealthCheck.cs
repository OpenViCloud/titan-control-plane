using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Repositories.Billing;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Every failure of the async wallet path is quiet: a lost notify, a dead sweeper and a poison mutation
/// all just leave rows Pending (titan-ops architecture/money-and-wallet.md#single-writer-outbox). Not an alert:
/// nothing polls this (titan-ops infra/runbooks/runbook-wallet-async.md).
/// </summary>
public sealed class WalletMutationHealthCheck(
    IWalletMutationRepository mutations,
    WalletSweeperHeartbeat heartbeat,
    IOptions<WalletSweeperOptions> options) : IHealthCheck
{
    /// Not the fast-path SLO: it must clear the sweeper's recovery of a lost notify
    /// (StaleAfterSeconds + IntervalSeconds, ~40s by default), or it fires every time the safety net works.
    public const int PendingThresholdSeconds = 90;

    /// Three missed passes rather than one: a single slow pass under load is not a fault.
    public const int SweepStaleFactor = 3;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var opts = options.Value;
        var now = DateTimeOffset.UtcNow;

        var backlog = await mutations.GetHealthSnapshotAsync(now, opts.MaxAttempts, ct);

        var state = new WalletMutationHealth(
            PoisonCount: backlog.PoisonCount,
            PendingCount: backlog.PendingCount,
            OldestPendingAge: backlog.OldestPendingAge,
            LastSweepAt: heartbeat.LastSweepAt,
            SweeperStartedAt: heartbeat.StartedAt,
            SweeperEnabled: opts.Enabled);

        return Evaluate(now, state, PendingThresholdSeconds, opts.IntervalSeconds, SweepStaleFactor);
    }

    /// <summary>
    /// Pure, so WalletMutationHealthCheckTests can inject the clock and state. Ordered worst-first, and each
    /// branch names which of the unrelated faults broke.
    /// </summary>
    public static HealthCheckResult Evaluate(
        DateTimeOffset now,
        WalletMutationHealth state,
        int pendingThresholdSeconds,
        int sweepIntervalSeconds,
        int sweepStaleFactor)
    {
        // Outranks an empty backlog: with the sweeper off, a quiet queue is the absence of a witness, not health.
        if (!state.SweeperEnabled)
            return HealthCheckResult.Unhealthy(
                "wallet sweeper is disabled — a lost notify will never be recovered (docs/architecture/money-and-wallet.md#single-writer-outbox)");

        if (state.PoisonCount > 0)
            return HealthCheckResult.Unhealthy(
                $"{state.PoisonCount} poison wallet mutation(s) given up on — a payment is stuck");

        var staleWindow = TimeSpan.FromSeconds((double)sweepIntervalSeconds * sweepStaleFactor);

        // Never-run and running-fine both show an empty backlog, so measure against the boot instant.
        if (state.LastSweepAt is null)
        {
            var since = state.SweeperStartedAt is null ? (TimeSpan?)null : now - state.SweeperStartedAt.Value;

            if (since is null || since > staleWindow)
                return HealthCheckResult.Degraded(
                    "wallet sweeper has never completed a sweep since startup — " +
                    "cannot distinguish a working safety net from an absent one");

            // Inside the boot grace window: no first tick yet.
        }
        else if (now - state.LastSweepAt.Value > staleWindow)
        {
            return HealthCheckResult.Unhealthy(
                $"wallet sweeper last swept {(int)(now - state.LastSweepAt.Value).TotalSeconds}s ago " +
                $"(expected every {sweepIntervalSeconds}s) — the safety net has stopped");
        }

        // Last: the machinery is alive here, so an old mutation means the work itself is not getting through.
        if (state.OldestPendingAge is { } age && age.TotalSeconds > pendingThresholdSeconds)
            return HealthCheckResult.Unhealthy(
                $"oldest pending wallet mutation is {(int)age.TotalSeconds}s old " +
                $"(threshold {pendingThresholdSeconds}s), {state.PendingCount} pending");

        return HealthCheckResult.Healthy(
            state.PendingCount == 0
                ? "no wallet mutations pending; sweeper alive"
                : $"{state.PendingCount} wallet mutation(s) pending within the expected window; sweeper alive");
    }
}
