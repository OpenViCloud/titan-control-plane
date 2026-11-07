using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VMs waiting on a request under the old columns meets the migration that gives the request its kind
/// and time (titan-ops architecture/resource-lifecycle.md#shape): the kind is read from the columns it used to be derived
/// from, in the order Vm.PendingIntent read them, and the two columns the model no longer writes stay with a default,
/// so the image after the migration can still insert. The old column names below are the schema before it, by necessity.
/// The rows go on through VmSingleStatus, so a waiting request is read back as the status it moved the VM to.
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
    private async Task<Guid> SeedVmAsync(string desiredExistence, string desiredState, bool createFinished, bool waiting, string? externalId = "ext")
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
        {desiredExistence}, {externalId}, true, {createFinished}, 'm-mig', 'tpl', 'cluster-a', 20, now(), {intent})");
        return id;
    }

    private async Task<(Guid? Id, VmStatus Status, DateTimeOffset? AskedAt)> PendingColumnsAsync(Guid vmId)
    {
        await using TitanDbContext db = NewDbContext();
        Vm vm = await db.Vms.SingleAsync(v => v.Id == vmId);
        return (vm.Operation.PendingId, vm.Status, vm.Operation.AskedAt);
    }

    [Fact]
    public async Task EveryWaitingRequest_GetsItsKindFromTheOldColumns_AndATime()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedVmAsync("Deleted", "Running", true, waiting: true);
        Guid stopping = await SeedVmAsync("Exists", "Stopped", true, waiting: true);
        Guid creating = await SeedVmAsync("Exists", "Running", false, waiting: true, externalId: null);
        Guid starting = await SeedVmAsync("Exists", "Running", true, waiting: true);
        Guid resting = await SeedVmAsync("Exists", "Running", true, waiting: false);

        await MigrateToLatestAsync();

        Assert.Equal(VmStatus.Deleting, (await PendingColumnsAsync(deleting)).Status);
        Assert.Equal(VmStatus.Stopping, (await PendingColumnsAsync(stopping)).Status);
        Assert.Equal(VmStatus.Creating, (await PendingColumnsAsync(creating)).Status);
        Assert.Equal(VmStatus.Starting, (await PendingColumnsAsync(starting)).Status);
        Assert.NotNull((await PendingColumnsAsync(starting)).Id);
        Assert.NotNull((await PendingColumnsAsync(starting)).AskedAt);
        Assert.Equal((null, VmStatus.Running, null), await PendingColumnsAsync(resting));
    }

    // A delete whose command already gave up: the image before this one kept it waiting and let the next press reopen the
    // row. A repeat now sends nothing and no report is coming, so it would wait for ever; the migration ends it as Failed
    // (titan-ops architecture/resource-lifecycle.md#kinds). One whose row is still open keeps waiting, for its command's report.
    [Fact]
    public async Task ADeleteWhoseCommandAlreadyFailed_EndsAsFailed_AndOneStillOpenKeepsWaiting()
    {
        await MigrateToAsync(MigrationBefore);
        Guid gaveUp = await SeedVmAsync("Deleted", "Running", true, waiting: true);
        Guid stillOpen = await SeedVmAsync("Deleted", "Running", true, waiting: true);
        await using (TitanDbContext seed = NewDbContext())
        {
            await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, $"vm-delete-{gaveUp}", ProviderCommandKinds.VmDelete,
                "cluster-a", "{}", "Failed", "AfterFailureOnly");
            await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, $"vm-delete-{stillOpen}", ProviderCommandKinds.VmDelete,
                "cluster-a", "{}", "Pending", "AfterFailureOnly");
        }

        await MigrateToLatestAsync();

        await using TitanDbContext read = NewDbContext();
        Vm ended = await read.Vms.SingleAsync(v => v.Id == gaveUp);
        Assert.Null(ended.Operation.PendingId);
        // The delete is over and the instance is still there: the VM rests on its last Incus status.
        Assert.Equal(VmStatus.Running, ended.Status);
        Assert.NotNull(ended.Operation.FailureReason);
        Assert.NotNull(ended.Operation.FailedAt);
        Vm waiting = await read.Vms.SingleAsync(v => v.Id == stillOpen);
        Assert.NotNull(waiting.Operation.PendingId);
        Assert.Equal(VmStatus.Deleting, waiting.Status);
    }

    // The worker reported Created and the old image waited for the reconciler to see Running or Ready; an observation no
    // longer ends a create. Its Done has arrived (titan-ops architecture/resource-lifecycle.md#kinds).
    [Fact]
    public async Task ACreateWhoseInstanceNameArrived_IsOver_AndOneWithoutItKeepsWaiting()
    {
        await MigrateToAsync(MigrationBefore);
        Guid named = await SeedVmAsync("Exists", "Running", false, waiting: true);
        Guid unnamed = await SeedVmAsync("Exists", "Running", false, waiting: true, externalId: null);

        await MigrateToLatestAsync();

        await using TitanDbContext read = NewDbContext();
        Vm over = await read.Vms.SingleAsync(v => v.Id == named);
        Assert.Null(over.Operation.PendingId);
        Assert.Equal(VmStatus.Running, over.Status);
        Assert.Equal("ext", over.ExternalId);
        Assert.Null(over.Operation.FailureReason);
        Assert.Equal(VmStatus.Creating, (await PendingColumnsAsync(unnamed)).Status);
    }

    // A command still open meets the new worker with the request's key and id, so its report names the request and
    // a give-up of a delete has a route to come back on (titan-ops architecture/resource-lifecycle.md#callback-routes).
    // A row of a VM not waiting on it, or a finished one, is left alone.
    [Fact]
    public async Task OpenCommands_TakeTheKeyAndTheIdOfTheirRequest()
    {
        await MigrateToAsync(MigrationBefore);
        Guid creating = await SeedVmAsync("Exists", "Running", false, waiting: true, externalId: null);
        Guid deleting = await SeedVmAsync("Deleted", "Running", true, waiting: true);
        Guid stopping = await SeedVmAsync("Exists", "Stopped", true, waiting: true);
        Guid resting = await SeedVmAsync("Exists", "Running", true, waiting: false);
        Guid stopIntent;
        await using (TitanDbContext seed = NewDbContext())
        {
            stopIntent = (await seed.Database
                .SqlQuery<Guid>($"SELECT pending_intent_id AS \"Value\" FROM core.vms WHERE id = {stopping}").ToListAsync()).Single();
            Task Open(string key, string kind) =>
                ProviderCommandRowsBeforeOneRule.InsertAsync(seed, key, kind, "cluster-a", "{\"vmId\":\"x\"}");
            await Open($"vm-provision-{creating}", ProviderCommandKinds.VmCreate);
            await Open($"vm-delete-{deleting}", ProviderCommandKinds.VmDelete);
            await Open($"vm-stop-{stopping}-{stopIntent}", ProviderCommandKinds.VmStop);
            await Open($"vm-delete-{resting}", ProviderCommandKinds.VmDelete);
        }

        await MigrateToLatestAsync();

        await using TitanDbContext read = NewDbContext();
        async Task AssertCarried(Guid vmId, string kind)
        {
            Guid requestId = (await read.Vms.SingleAsync(v => v.Id == vmId)).Operation.PendingId!.Value;
            ProviderCommand row = await read.ProviderCommands.SingleAsync(c => c.Key == $"vm-{kind}-{vmId}-{requestId}");
            using var payload = System.Text.Json.JsonDocument.Parse(row.Payload);
            Assert.Equal(requestId.ToString(), payload.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("x", payload.RootElement.GetProperty("vmId").GetString());
        }
        await AssertCarried(creating, "create");
        await AssertCarried(deleting, "delete");
        await AssertCarried(stopping, "stop");
        Assert.Equal(stopIntent, (await read.Vms.SingleAsync(v => v.Id == stopping)).Operation.PendingId!.Value);
        ProviderCommand untouched = await read.ProviderCommands.SingleAsync(c => c.Key == $"vm-delete-{resting}");
        Assert.DoesNotContain("requestId", untouched.Payload);
    }
}
