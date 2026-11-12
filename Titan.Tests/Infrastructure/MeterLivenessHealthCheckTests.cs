using Microsoft.Extensions.Diagnostics.HealthChecks;
using Titan.Application.Services.Billing.Metering;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

// The meter liveness verdict. Tested through the pure Evaluate(now, snapshot, ...) so the
// clock is an injected oracle, not DateTimeOffset.UtcNow, and the snapshot is hand-built (the oracle
// sits outside the thing under test). interval=10, staleFactor=3 → a meter is stale after 30 minutes
// of no successful tick.
public class MeterLivenessHealthCheckTests
{
    private const int Interval = 10;
    private const int StaleFactor = 3; // stale window = 30 minutes
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, MeterTickState> Snapshot(params (string Name, MeterTickState State)[] entries)
    {
        var d = new Dictionary<string, MeterTickState>();
        foreach (var (name, state) in entries)
            d[name] = state;
        return d;
    }

    // A meter that has not ticked past the stale window MUST flip the check to Unhealthy: a check that
    // is always Healthy would be worse than none.
    [Fact]
    public void Stale_meter_is_reported_Unhealthy_and_named()
    {
        var snapshot = Snapshot(
            ("Compute", new MeterTickState(RegisteredAt: Now.AddHours(-2),
                LastSuccessAt: Now.AddMinutes(-31), LastAttemptAt: Now.AddMinutes(-31))));

        var result = MeterLivenessHealthCheck.Evaluate(Now, snapshot, Interval, StaleFactor);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Compute", result.Description);
    }

    [Fact]
    public void All_meters_ticked_recently_is_Healthy()
    {
        var fresh = new MeterTickState(Now.AddHours(-2), Now.AddMinutes(-5), Now.AddMinutes(-5));
        var snapshot = Snapshot(("Compute", fresh), ("Disk", fresh), ("Snapshot", fresh));

        var result = MeterLivenessHealthCheck.Evaluate(Now, snapshot, Interval, StaleFactor);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // Registered (the migration's row), no tick yet, still inside the grace window
    // (measured from registration) → not a false alarm.
    [Fact]
    public void Registered_but_not_yet_ticked_within_grace_is_Healthy()
    {
        var snapshot = Snapshot(
            ("Compute", new MeterTickState(RegisteredAt: Now.AddMinutes(-20),
                LastSuccessAt: null, LastAttemptAt: null)));

        var result = MeterLivenessHealthCheck.Evaluate(Now, snapshot, Interval, StaleFactor);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // Grace is not forever: registered but still never ticked past the window → Unhealthy.
    [Fact]
    public void Registered_but_never_ticked_past_grace_is_Unhealthy()
    {
        var snapshot = Snapshot(
            ("Disk", new MeterTickState(RegisteredAt: Now.AddMinutes(-31),
                LastSuccessAt: null, LastAttemptAt: null)));

        var result = MeterLivenessHealthCheck.Evaluate(Now, snapshot, Interval, StaleFactor);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Disk", result.Description);
    }

    // One dead meter must not hide behind two healthy ones, and only the dead one is named.
    [Fact]
    public void One_stale_among_healthy_is_Unhealthy_and_names_only_the_stale_one()
    {
        var fresh = new MeterTickState(Now.AddHours(-2), Now.AddMinutes(-5), Now.AddMinutes(-5));
        var stale = new MeterTickState(Now.AddHours(-2), Now.AddMinutes(-45), Now.AddMinutes(-45));
        var snapshot = Snapshot(("Compute", fresh), ("Disk", fresh), ("Snapshot", stale));

        var result = MeterLivenessHealthCheck.Evaluate(Now, snapshot, Interval, StaleFactor);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Snapshot", result.Description);
        Assert.DoesNotContain("Compute", result.Description);
        Assert.DoesNotContain("Disk", result.Description);
    }

    // No row at all (a meter whose migration has not run) — no evidence of a stopped
    // meter, so Healthy rather than a false alarm.
    [Fact]
    public void Empty_snapshot_is_Healthy()
    {
        var result = MeterLivenessHealthCheck.Evaluate(Now, Snapshot(), Interval, StaleFactor);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
