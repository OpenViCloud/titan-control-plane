using Titan.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the verdict on the async money path. Tested
/// through the pure Evaluate(now, state, thresholds) so the clock is an injected oracle rather than
/// DateTimeOffset.UtcNow, and the backlog figures are hand-built: the oracle sits outside the thing
/// under test.
///
/// The threshold is 90 SECONDS, and that number is not the SLO. The SLO (~5-10s, titan-ops architecture/money-and-wallet.md#single-writer-outbox) describes
/// the FAST path — the bus notify. When a notify is lost the sweeper is what recovers the mutation,
/// and its worst case is StaleAfterSeconds (10) + IntervalSeconds (30) = ~40s. Alerting at the SLO
/// would therefore fire every time the safety net does its job correctly. 90s is just over twice the
/// recovery worst case, leaving room for one missed tick.
public class WalletMutationHealthCheckTests
{
    private const int PendingThresholdSeconds = 90;
    private const int SweepIntervalSeconds = 30;
    private const int SweepStaleFactor = 3; // a sweeper is stale after 90s of not running
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static HealthCheckResult Evaluate(WalletMutationHealth state) =>
        WalletMutationHealthCheck.Evaluate(
            Now, state, PendingThresholdSeconds, SweepIntervalSeconds, SweepStaleFactor);

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The distinction that decides whether this check is worth having.
    //
    // A mutation Failed because the wallet could not cover the invoice at apply time is a NORMAL,
    // designed outcome (titan-ops architecture/money-and-wallet.md#single-writer-outbox, risk-accepted): the customer spent their balance elsewhere
    // between pressing pay and the consumer running. Nobody should be woken for it. A mutation Failed
    // because it exhausted its attempts is POISON: a payment is stuck with nobody looking at it.
    //
    // Counting `status = Failed` would merge the two and make the signal useless in the direction
    // that matters — every ordinary shortfall would bury the one real fault.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Poison_mutation_is_Unhealthy()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 1,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperEnabled: true,
            SweeperStartedAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("poison", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Business_failure_without_poison_is_Healthy()
    {
        // The wallet ran short mid-apply: mutation Failed, invoice unpaid, customer can retry. The
        // design working, not a fault. PoisonCount is what separates this from the case above.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperEnabled: true,
            SweeperStartedAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Backlog age, measured against the RECOVERY worst case rather than the SLO.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pending_mutation_older_than_the_threshold_is_Unhealthy_and_says_how_old()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: TimeSpan.FromSeconds(120),
            PendingCount: 3,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperEnabled: true,
            SweeperStartedAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("120", result.Description);
    }

    [Fact]
    public void Pending_mutation_the_sweeper_has_not_reached_yet_is_Healthy()
    {
        // 20s old: younger than StaleAfterSeconds(10) + IntervalSeconds(30). The sweeper has not had its turn, and
        // complaining here would mean alerting on the safety net performing exactly as designed.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: TimeSpan.FromSeconds(20),
            PendingCount: 1,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperEnabled: true,
            SweeperStartedAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void A_mutation_at_the_sweeper_worst_case_is_still_Healthy()
    {
        // 40s = StaleAfterSeconds + IntervalSeconds exactly. The boundary is deliberate: this is the slowest a
        // HEALTHY recovery can be, so it must not read as a fault.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: TimeSpan.FromSeconds(40),
            PendingCount: 1,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperEnabled: true,
            SweeperStartedAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // "Never started" must not read as "healthy": a sweeper that is running perfectly and a sweeper
    // that never booted both produce zero activity, and zero activity is what a naive check reads as
    // fine. This check exists to tell those two apart.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sweeper_that_has_never_run_since_startup_is_Degraded_not_Healthy()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: null,
            SweeperStartedAt: Now.AddMinutes(-10), // long past its first tick
            SweeperEnabled: true));

        Assert.NotEqual(HealthStatus.Healthy, result.Status);
        Assert.Contains("never", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sweeper_within_its_boot_grace_is_Healthy()
    {
        // Started 5s ago with a 30s interval: it has not missed anything, it simply has not ticked
        // yet. Flagging this would make every deploy raise a false alarm.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: null,
            SweeperStartedAt: Now.AddSeconds(-5),
            SweeperEnabled: true));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Sweeper_that_stopped_ticking_is_Unhealthy()
    {
        // It ran, then stopped: 5 minutes without a pass on a 30s timer. The safety net is gone and
        // nothing else in the system would say so.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddMinutes(-5),
            SweeperStartedAt: Now.AddHours(-1),
            SweeperEnabled: true));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("sweep", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_sweeper_is_Unhealthy_even_with_nothing_pending()
    {
        // titan-ops architecture/money-and-wallet.md#single-writer-outbox — the sweeper off is the one configuration that can lose money silently. An
        // empty backlog while it is off is not evidence of health; it is the absence of a witness.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: null,
            SweeperStartedAt: Now.AddHours(-1),
            SweeperEnabled: false));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("disabled", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quiet_and_healthy_system_reports_Healthy()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddSeconds(-10),
            SweeperStartedAt: Now.AddHours(-1),
            SweeperEnabled: true));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}

/// The constants themselves, pinned separately.
///
/// Why this class exists: the suite above passes `PendingThresholdSeconds` in as an argument, which
/// measures the comparison logic and is SILENT about what production actually uses — changing the
/// real constant to the SLO value leaves it green.
///
/// These assert the RELATIONSHIP, not just the digits: the point is not that the value is 90, it is
/// that it clears the sweeper's recovery worst case. Written this way, tuning the sweeper's timers
/// without revisiting the threshold turns the suite red instead of quietly producing an alert that
/// fires whenever the safety net works.
public class WalletMutationHealthThresholdTests
{
    [Fact]
    public void Pending_threshold_clears_the_sweepers_worst_case_recovery()
    {
        var defaults = new WalletSweeperOptions();
        var worstCaseRecovery = defaults.StaleAfterSeconds + defaults.IntervalSeconds;

        Assert.True(
            WalletMutationHealthCheck.PendingThresholdSeconds > worstCaseRecovery,
            $"threshold {WalletMutationHealthCheck.PendingThresholdSeconds}s must exceed the " +
            $"sweeper's worst-case recovery of {worstCaseRecovery}s, or the check fires every time " +
            "the safety net does its job");
    }

    [Fact]
    public void Pending_threshold_is_not_set_to_the_fast_path_SLO()
    {
        // The ~5-10s SLO of titan-ops architecture/money-and-wallet.md#single-writer-outbox describes the bus notify path. Using it here is the specific mistake
        // this test exists to catch: it would alert on every sweeper-recovered mutation.
        Assert.True(WalletMutationHealthCheck.PendingThresholdSeconds > 10);
    }

    [Fact]
    public void Sweeper_stale_factor_tolerates_more_than_one_missed_pass()
    {
        // One slow pass under load is not a dead sweeper. Alerting on a single missed tick is how a
        // signal gets trained out of an operator.
        Assert.True(WalletMutationHealthCheck.SweepStaleFactor >= 2);
    }
}
