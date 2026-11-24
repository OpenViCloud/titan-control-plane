using Microsoft.EntityFrameworkCore;
using Titan.Application.Options;
using Titan.Application.Queries.Admin.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The Backups page's list of an admin's restores of a backup archive, read from the vm.restore and volume.restore rows
// (titan-ops features/backup/design.md#restore-as-new-resource). The rows are written by the real queue, so the reader
// reads what the writer writes; the new resources' names come from the real repositories (InMemory).
public class ListBackupRestoresQueryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private const string VmArchive = "20261120T010203Z";
    private const string VolumeArchive = "20261121T040506Z";

    private readonly FakeProviderCommandRepository _rows = new();
    private readonly FixedClock _clock = new(Now);
    private readonly TitanDbContext _db = new(new DbContextOptionsBuilder<TitanDbContext>()
        .UseInMemoryDatabase($"backup-restores-{Guid.NewGuid()}").Options);

    public void Dispose() => _db.Dispose();

    private Task<IReadOnlyList<BackupRestoreQueryResult>> RunAsync(params string[] clusters) =>
        new ListBackupRestoresQueryHandler(_rows, new VmRepository(_db), new VolumeRepository(_db), _clock,
                new ClusterOptions { Clusters = (clusters.Length == 0 ? ["mock"] : clusters).Select(id => new ClusterConfig { Id = id, Name = "Cluster " + id }).ToList() })
            .HandleAsync(new ListBackupRestoresQuery());

    // An admin's VM restore asked at `at`, as its handler writes it: the new VM's row and its command.
    private async Task<(Vm Copy, ProviderCommand Row)> VmRestoredAsync(DateTimeOffset at, string cluster = "mock", bool keepTheRow = true)
    {
        Vm source = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-1", "m", "tpl-1", cluster, 20);
        source.MarkRunning("vm-web-1", "10.0.0.2");
        Vm copy = Vm.RestoreFromBackup(source, "web-1-restored");
        if (keepTheRow)
        {
            _db.Vms.Add(copy);
            await _db.SaveChangesAsync();
        }
        _clock.Now = at;
        await new ProviderCommandQueue(_rows, _clock).EnqueueVmRestoreAsync(source, copy, copy.Operation.PendingId!.Value, VmArchive,
            "t-1", []);
        _clock.Now = Now;
        return (copy, _rows.Rows[^1]);
    }

    private async Task<(Volume Copy, ProviderCommand Row)> VolumeRestoredAsync(DateTimeOffset at, bool keepTheRow = true)
    {
        Volume source = Volume.Create(TestTenant.WithRoomToSpare().Id, "data-1", "mock", "std", 10);
        source.MarkCreated("vol-data-1");
        Volume copy = Volume.RestoreFromBackup(source, "data-1-restored");
        if (keepTheRow)
        {
            _db.Volumes.Add(copy);
            await _db.SaveChangesAsync();
        }
        _clock.Now = at;
        await new ProviderCommandQueue(_rows, _clock).EnqueueVolumeRestoreAsync(source, copy, copy.Operation.PendingId!.Value,
            VolumeArchive, "t-1");
        _clock.Now = Now;
        return (copy, _rows.Rows[^1]);
    }

    private static void Ended(ProviderCommand row, string? failedWith = null)
    {
        Guid lease = Guid.NewGuid();
        row.Lease(lease, row.CreatedAt);
        if (failedWith == null)
        {
            row.Complete(lease);
        }
        else
        {
            row.Fail(lease, failedWith, retryable: false, row.CreatedAt);
        }
    }

    [Fact]
    public async Task EachRestore_IsALine_NewestFirst_NamingTheNewResource_ItsTenant_AndTheArchivesTime()
    {
        (Vm vmCopy, _) = await VmRestoredAsync(Now.AddDays(-2));
        (Volume volumeCopy, _) = await VolumeRestoredAsync(Now.AddHours(-1));

        IReadOnlyList<BackupRestoreQueryResult> lines = await RunAsync();

        Assert.Equal(2, lines.Count);
        BackupRestoreQueryResult volume = lines[0];
        Assert.Equal(("volume", volumeCopy.Id, "data-1-restored", (Guid?)volumeCopy.TenantId),
            (volume.Kind, volume.ResourceId, volume.Name, volume.TenantId));
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 4, 5, 6, TimeSpan.Zero), volume.TakenAt);
        Assert.Equal(Now.AddHours(-1), volume.AskedAt);
        BackupRestoreQueryResult vm = lines[1];
        Assert.Equal(("vm", vmCopy.Id, "web-1-restored", (Guid?)vmCopy.TenantId), (vm.Kind, vm.ResourceId, vm.Name, vm.TenantId));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 1, 2, 3, TimeSpan.Zero), vm.TakenAt);
        Assert.Equal(Now.AddDays(-2), vm.AskedAt);
    }

    // Waiting or with the worker is one state to the admin: the restore is not over yet. A failure says why; a retryable
    // failure that put the row back to Pending is still running, and its error is not the answer yet.
    [Fact]
    public async Task TheStateIsRunning_UntilTheRowEnds_ThenDoneOrFailedWithItsReason()
    {
        (_, ProviderCommand pending) = await VolumeRestoredAsync(Now.AddHours(-5));
        (_, ProviderCommand leased) = await VolumeRestoredAsync(Now.AddHours(-4));
        leased.Lease(Guid.NewGuid(), Now);
        (_, ProviderCommand retrying) = await VolumeRestoredAsync(Now.AddHours(-3));
        Guid lease = Guid.NewGuid();
        retrying.Lease(lease, Now);
        retrying.Fail(lease, "pool unreadable", retryable: true, Now);
        (_, ProviderCommand done) = await VolumeRestoredAsync(Now.AddHours(-2));
        Ended(done);
        (_, ProviderCommand failed) = await VolumeRestoredAsync(Now.AddHours(-1));
        Ended(failed, failedWith: "storage pool too full");

        IReadOnlyList<BackupRestoreQueryResult> lines = await RunAsync();

        Assert.Equal(
            new (string, string?)[] { ("failed", "storage pool too full"), ("done", null), ("running", null), ("running", null), ("running", null) },
            lines.Select(l => (l.State, l.Reason)).ToArray());
        Assert.Equal(ProviderCommandStatus.Pending, retrying.Status);
    }

    // Retention deletes finished rows after 30 days, so the list keeps to them; a drill and the other kinds are no restore.
    [Fact]
    public async Task OnlyTheRestoresOfTheLastThirtyDays_AreListed_NoDrillNorAnyOtherCommand()
    {
        await VmRestoredAsync(Now.AddDays(-31));
        (Vm recent, _) = await VmRestoredAsync(Now.AddDays(-29));
        _rows.Rows.Add(ProviderCommand.Create("restore-drill-mock-20261123", ProviderCommandKinds.RestoreDrill, "mock",
            """{"kind":"vm","name":"web-1"}""", Now.AddHours(-1)));
        _rows.Rows.Add(ProviderCommand.Create($"vm-create-{Guid.NewGuid()}-{Guid.NewGuid()}", ProviderCommandKinds.VmCreate, "mock",
            "{}", Now.AddHours(-1), Guid.NewGuid(), Guid.NewGuid()));

        BackupRestoreQueryResult line = Assert.Single(await RunAsync());

        Assert.Equal(recent.Id, line.ResourceId);
    }

    // The tenant may delete a restored resource; its row is soft-deleted and still names it. A row that is gone leaves
    // the line without a name.
    [Fact]
    public async Task ARestoreWhoseResourceRowIsGone_HasNoName()
    {
        (Vm vm, _) = await VmRestoredAsync(Now.AddHours(-2), keepTheRow: false);
        (Volume volume, _) = await VolumeRestoredAsync(Now.AddHours(-1), keepTheRow: false);

        IReadOnlyList<BackupRestoreQueryResult> lines = await RunAsync();

        Assert.Equal(new[] { (volume.Id, (string?)null), (vm.Id, (string?)null) }, lines.Select(l => (l.ResourceId, l.Name)).ToArray());
    }

    // A row whose payload this reader does not know still counts as a restore; it only has no tenant or archive time to show.
    [Fact]
    public async Task ARowWhosePayloadCannotBeRead_IsStillALine_WithoutTenantOrArchiveTime()
    {
        Guid resource = Guid.NewGuid();
        _rows.Rows.Add(ProviderCommand.Create($"vm-create-{resource}-{Guid.NewGuid()}", ProviderCommandKinds.VmRestore, "mock",
            """{"backupKey":"not-a-time"}""", Now.AddHours(-1), null, resource));

        BackupRestoreQueryResult line = Assert.Single(await RunAsync());

        Assert.Equal(("vm", resource, "running"), (line.Kind, line.ResourceId, line.State));
        Assert.Null(line.TenantId);
        Assert.Null(line.TakenAt);
    }

    [Fact]
    public async Task TheRestoresOfEveryConfiguredCluster_AreListed_InOneList()
    {
        (Vm first, _) = await VmRestoredAsync(Now.AddHours(-2), cluster: "a");
        (Vm second, _) = await VmRestoredAsync(Now.AddHours(-1), cluster: "b");

        IReadOnlyList<BackupRestoreQueryResult> lines = await RunAsync("a", "b");

        Assert.Equal(new[] { second.Id, first.Id }, lines.Select(l => l.ResourceId).ToArray());
    }
}
