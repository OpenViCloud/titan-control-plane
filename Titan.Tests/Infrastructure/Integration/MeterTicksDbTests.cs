using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.Metering;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// The meter's ticks are rows of core.meter_ticks, so /healthz/meters answers the same on every control plane pod
/// whichever pod the meter's CronJob reached (titan-ops features/billing/metering/design.md#meter-liveness). The upsert is
/// raw SQL, so it is measured here on Postgres. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class MeterTicksDbTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly string[] Meters = ["Compute", "Disk", "Snapshot"];

    // To the second: the column keeps microseconds.
    private static DateTimeOffset Ago(TimeSpan span)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow - span;
        return new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, TimeSpan.Zero);
    }

    public async Task InitializeAsync()
    {
        // Back to what the migration leaves: the three meters, registered, never ticked.
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE core.meter_ticks SET registered_at = now(), last_attempt_at = NULL, last_success_at = NULL");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<IReadOnlyDictionary<string, MeterTickState>> SnapshotAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new PostgresMeterTicks(db).SnapshotAsync(CancellationToken.None);
    }

    private async Task RecordAsync(string meter, bool success, DateTimeOffset at)
    {
        await using TitanDbContext db = pg.NewDbContext();
        await new PostgresMeterTicks(db).RecordAsync(meter, success, at, CancellationToken.None);
    }

    private async Task<HealthStatus> HealthAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        var check = new MeterLivenessHealthCheck(new PostgresMeterTicks(db), Options.Create(new BillingMeterOptions()));
        return (await check.CheckHealthAsync(new HealthCheckContext())).Status;
    }

    [Fact]
    public async Task TheMigration_RegistersTheThreeMeters_NeverTicked()
    {
        IReadOnlyDictionary<string, MeterTickState> snapshot = await SnapshotAsync();

        Assert.Equal(Meters, snapshot.Keys.Order());
        Assert.All(snapshot.Values, s => Assert.Null(s.LastSuccessAt));
    }

    [Fact]
    public async Task AFailure_KeepsTheLastSuccess_AndAdvancesTheAttempt()
    {
        DateTimeOffset success = Ago(TimeSpan.FromMinutes(20));
        DateTimeOffset failure = Ago(TimeSpan.FromMinutes(10));

        await RecordAsync("Compute", true, success);
        await RecordAsync("Compute", false, failure);

        MeterTickState compute = (await SnapshotAsync())["Compute"];
        Assert.Equal(success, compute.LastSuccessAt);
        Assert.Equal(failure, compute.LastAttemptAt);
    }

    // AC-3.2: ticks recorded through one DbContext, as one pod records them, are read through another.
    [Fact]
    public async Task EveryMeterTickedJustNow_IsHealthy_ReadFromAnotherPod()
    {
        foreach (string meter in Meters)
        {
            await RecordAsync(meter, true, DateTimeOffset.UtcNow);
        }

        Assert.Equal(HealthStatus.Healthy, await HealthAsync());
    }

    // AC-3.3: a meter whose last success is older than MeterStaleFactor × IntervalMinutes (30 minutes by default).
    [Fact]
    public async Task AMeterWhoseLastSuccessIsOlderThanTheWindow_IsUnhealthy()
    {
        await RecordAsync("Compute", true, DateTimeOffset.UtcNow);
        await RecordAsync("Disk", true, DateTimeOffset.UtcNow);
        await RecordAsync("Snapshot", true, Ago(TimeSpan.FromMinutes(31)));

        Assert.Equal(HealthStatus.Unhealthy, await HealthAsync());
    }

    // AC-3.3, never ticked: the window counts from the registration the migration wrote.
    [Fact]
    public async Task AMeterNeverTicked_RegisteredLongerAgoThanTheWindow_IsUnhealthy()
    {
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE core.meter_ticks SET registered_at = now() - interval '31 minutes'");
        }

        Assert.Equal(HealthStatus.Unhealthy, await HealthAsync());
    }
}
