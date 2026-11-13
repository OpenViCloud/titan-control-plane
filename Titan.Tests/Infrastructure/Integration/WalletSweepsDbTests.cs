using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The wallet sweeper's passes are the one row of core.wallet_sweeps, so /healthz/wallet answers the same on every
/// control plane pod whichever pod the sweeper's CronJob reached (titan-ops features/billing/wallet/design.md#healthz-wallet).
/// The upsert is raw SQL, so it is measured here on Postgres. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletSweepsDbTests(PostgresFixture pg) : IAsyncLifetime
{
    // To the second: the column keeps microseconds.
    private static DateTimeOffset Ago(TimeSpan span)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow - span;
        return new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, TimeSpan.Zero);
    }

    public async Task InitializeAsync()
    {
        // Back to what the migration leaves: the sweeper registered, never run. The fixture's database is shared across
        // the collection, so the mutation table is cleared too (as WalletMutationHealthSnapshotDbTests does): the health
        // verdict below then reads the sweeper alone, not a poison row another test left.
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE core.wallet_sweeps SET registered_at = now(), last_attempt_at = NULL, last_success_at = NULL");
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM core.wallet_mutations");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<WalletSweepState?> GetAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new PostgresWalletSweeps(db).GetAsync(CancellationToken.None);
    }

    private async Task RecordAsync(bool success, DateTimeOffset at)
    {
        await using TitanDbContext db = pg.NewDbContext();
        await new PostgresWalletSweeps(db).RecordAsync(success, at, CancellationToken.None);
    }

    private async Task<HealthCheckResult> HealthAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        var check = new WalletMutationHealthCheck(
            new WalletMutationRepository(db), new PostgresWalletSweeps(db), Options.Create(new WalletSweeperOptions()));
        return await check.CheckHealthAsync(new HealthCheckContext());
    }

    [Fact]
    public async Task TheMigration_RegistersTheSweeper_NeverRun()
    {
        WalletSweepState? state = await GetAsync();

        Assert.NotNull(state);
        Assert.Null(state.LastAttemptAt);
        Assert.Null(state.LastSuccessAt);
    }

    [Fact]
    public async Task ASuccess_SetsTheAttemptAndTheSuccess()
    {
        DateTimeOffset at = Ago(TimeSpan.FromSeconds(30));

        await RecordAsync(success: true, at);

        WalletSweepState? state = await GetAsync();
        Assert.Equal(at, state!.LastAttemptAt);
        Assert.Equal(at, state.LastSuccessAt);
    }

    [Fact]
    public async Task AFailure_KeepsTheLastSuccess_AndAdvancesTheAttempt()
    {
        DateTimeOffset success = Ago(TimeSpan.FromMinutes(4));
        DateTimeOffset failure = Ago(TimeSpan.FromMinutes(2));

        await RecordAsync(success: true, success);
        await RecordAsync(success: false, failure);

        WalletSweepState? state = await GetAsync();
        Assert.Equal(failure, state!.LastAttemptAt);
        Assert.Equal(success, state.LastSuccessAt);
    }

    // The verdict read through a context other than the one that wrote: what a pod the CronJob did not reach sees.
    [Fact]
    public async Task ASweepJustNow_IsHealthy_ReadFromAnotherPod()
    {
        await RecordAsync(success: true, Ago(TimeSpan.FromSeconds(10)));

        Assert.Equal(HealthStatus.Healthy, (await HealthAsync()).Status);
    }

    [Fact]
    public async Task ASweeperWhoseLastSuccessIsOlderThanTheWindow_IsUnhealthy()
    {
        await RecordAsync(success: true, Ago(TimeSpan.FromMinutes(10)));

        HealthCheckResult result = await HealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("sweep", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    // A CronJob that calls on time but whose every pass throws: the attempts are fresh, the last success is not.
    [Fact]
    public async Task ASweeperWhosePassesAllFail_IsUnhealthy_ThoughItsAttemptIsFresh()
    {
        await RecordAsync(success: true, Ago(TimeSpan.FromMinutes(10)));
        await RecordAsync(success: false, Ago(TimeSpan.FromSeconds(10)));

        Assert.Equal(HealthStatus.Unhealthy, (await HealthAsync()).Status);
    }

    [Fact]
    public async Task ASweeperNeverRun_PastTheWindowFromItsRegistration_IsUnhealthy()
    {
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE core.wallet_sweeps SET registered_at = now() - interval '10 minutes'");
        }

        Assert.Equal(HealthStatus.Unhealthy, (await HealthAsync()).Status);
    }
}
