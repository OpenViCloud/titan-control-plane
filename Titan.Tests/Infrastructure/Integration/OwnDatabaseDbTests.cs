using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// Postgres behaviour of rows the shared fixture cannot hold for one test alone: the seeded catalog price. Each test gets its own empty database, migrated to the latest schema.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class OwnDatabaseDbTests : IAsyncLifetime
{
    // More decimals than the catalog once kept (numeric(10,6) rounded it to 0.000088); the column is numeric(18,10).
    private const decimal PrecisionProbe = 0.0000877300m;

    private readonly TestPostgres _container = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    // The listed price is kept at the precision usage is billed at (titan-ops architecture/money-and-wallet.md#rate-value-object).
    [Fact]
    public async Task ACatalogPriceWithTenDecimals_SurvivesTheRoundTrip()
    {
        await using (var db = NewDbContext())
        {
            await db.Database.ExecuteSqlAsync($"update core.vm_offerings set price_per_hour = {PrecisionProbe} where id = 'm'");
        }

        await using (var db = NewDbContext())
        {
            var stored = await db.VmOfferings.AsNoTracking().SingleAsync(o => o.Id == "m");
            Assert.Equal(PrecisionProbe, stored.PricePerHour.PerUnit);
        }
    }

    // The seed tests read HasData; this reads what the migrations wrote, so a wrong value in WholeVndPrices.Up is caught
    // (titan-ops features/catalog/design.md#whole-vnd-prices).
    [Fact]
    public async Task TheMigratedCatalog_CarriesTheWholePrices()
    {
        await using var db = NewDbContext();

        var vm = await db.VmOfferings.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.PricePerHour.PerUnit);
        var volume = await db.VolumeOfferings.AsNoTracking().SingleAsync(o => o.Id == "standard");
        var snapshot = await db.SnapshotOfferings.AsNoTracking().SingleAsync(o => o.Id == "standard");

        Assert.Equal(4, vm.Count);
        Assert.Equal(170m, vm["s"]);
        Assert.Equal(320m, vm["m"]);
        Assert.Equal(640m, vm["l"]);
        Assert.Equal(1280m, vm["xl"]);
        Assert.Equal(2m, volume.PricePerGbPerHour.PerUnit);
        Assert.Equal(2m, snapshot.PricePerHour.PerUnit);
    }

    // The read path goes through the Rate value converter: the cost of an interval from the stored rate must equal the
    // cost from the rate the admin approved.
    [Fact]
    public async Task CostFromTheStoredRate_MatchesCostFromTheApprovedRate()
    {
        await using var read = NewDbContext();
        var stored = await read.VmOfferings.AsNoTracking().SingleAsync(o => o.Id == "m");

        const decimal oneTenMinuteInterval = 10m / 60m;
        var approved = 320m;    // the approved number, written here not read from seed

        Assert.Equal(approved, stored.PricePerHour.PerUnit);
        Assert.Equal(approved * oneTenMinuteInterval, stored.PricePerHour * oneTenMinuteInterval);
    }
}
