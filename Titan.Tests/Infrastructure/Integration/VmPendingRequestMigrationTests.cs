using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VMs waiting on a request under the old columns meets the migration that gives the request its kind
/// and time (titan-ops architecture/resource-lifecycle.md#shape): the kind is read from the columns it used to be derived
/// from, in the order Vm.PendingIntent read them, and the two columns the model no longer writes stay with a default,
/// so the image after the migration can still insert. The old column names below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmPendingRequestMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261101102929_ClusterIpv6Route";

    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task MigrateToAsync(string migration)
    {
        await using TitanDbContext db = NewDbContext();
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task MigrateToLatestAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // A VM row as the image before this migration wrote it: the request's id in pending_intent_id, its kind implied
    // by desired_existence, desired_state and create_finished.
    private async Task<Guid> SeedVmAsync(string desiredExistence, string desiredState, bool createFinished, bool waiting)
    {
        Guid id = Guid.NewGuid();
        Guid? intent = waiting ? Guid.NewGuid() : null;
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour)
VALUES ('m-mig', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING;
INSERT INTO core.vms (id, tenant_id, network_id, name, current_state, desired_state, current_existence, desired_existence,
                      external_id, has_been_running, create_finished, vm_offering_id, template_id, cluster_id, disk_size_gb,
                      created_at, pending_intent_id)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"vm-" + id.ToString("N")[..8]}, 'Running', {desiredState}, 'Exists',
        {desiredExistence}, 'ext', true, {createFinished}, 'm-mig', 'tpl', 'cluster-a', 20, now(), {intent})");
        return id;
    }

    private async Task<(Guid? Id, string? Kind, DateTimeOffset? AskedAt)> PendingColumnsAsync(Guid vmId)
    {
        await using TitanDbContext db = NewDbContext();
        Vm vm = await db.Vms.SingleAsync(v => v.Id == vmId);
        return (vm.Pending?.Id, vm.Pending?.Kind.ToString(), vm.Pending?.AskedAt);
    }

    [Fact]
    public async Task EveryWaitingRequest_GetsItsKindFromTheOldColumns_AndATime()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedVmAsync("Deleted", "Running", true, waiting: true);
        Guid stopping = await SeedVmAsync("Exists", "Stopped", true, waiting: true);
        Guid creating = await SeedVmAsync("Exists", "Running", false, waiting: true);
        Guid starting = await SeedVmAsync("Exists", "Running", true, waiting: true);
        Guid resting = await SeedVmAsync("Exists", "Running", true, waiting: false);

        await MigrateToLatestAsync();

        Assert.Equal("Delete", (await PendingColumnsAsync(deleting)).Kind);
        Assert.Equal("Stop", (await PendingColumnsAsync(stopping)).Kind);
        Assert.Equal("Create", (await PendingColumnsAsync(creating)).Kind);
        Assert.Equal("Start", (await PendingColumnsAsync(starting)).Kind);
        Assert.NotNull((await PendingColumnsAsync(starting)).AskedAt);
        Assert.Equal((null, null, null), await PendingColumnsAsync(resting));
    }

    [Fact] // the model no longer writes create_finished and has_been_running; the table still has them, with a default
    public async Task AVmInsertedByTheNewModel_Succeeds_WithTheOldColumnsDefaulted()
    {
        await MigrateToLatestAsync();
        Vm vm = Vm.Create(Guid.NewGuid(), Guid.NewGuid(), $"vm-new-{Guid.NewGuid():N}"[..16], "m-mig", "tpl", "cluster-a", 20);
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('m-mig', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        List<bool> olds = await read.Database
            .SqlQuery<bool>($"SELECT (create_finished OR has_been_running) AS \"Value\" FROM core.vms WHERE id = {vm.Id}")
            .ToListAsync();
        Assert.Equal([false], olds);
        Vm stored = await read.Vms.SingleAsync(v => v.Id == vm.Id);
        Assert.Equal(VmRequestKind.Create, stored.Pending!.Kind);
    }
}
