using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Commands.Platform.Backup.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// VM and volume backups in the bucket become cache rows, and each resource keeps its seven newest
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#incus-backup).
public class RefreshIncusBackupsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("0123abcd-0000-4000-8000-000000000001");
    private static readonly Guid Vm = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Volume = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private readonly InMemoryBackupBucket _bucket = new();
    private readonly FakeBackupRecordRepository _records = new();
    private readonly FakeBackupObservationRepository _observations = new();

    private Task RunAsync() => new RefreshBackupStatusCommandHandler(
        ConfiguredBackupTarget.Options(ConfiguredBackupTarget.Valid()), new FixedBackupBucketFactory(_bucket), _records,
        _observations, new FixedClock(Now))
        .HandleAsync(new RefreshBackupStatusCommand());

    // The worker's key, spelled out here rather than taken from the code under test.
    private static string Key(string collection, Guid id, DateTimeOffset at) =>
        $"incus/local-mac/{Tenant}/{collection}/{id}/{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.tar.gz";

    private void Put(string key, long size)
    {
        _bucket.Objects[key] = new byte[size];
        _bucket.Modified[key] = Now;
    }

    private List<BackupRecord> RowsOf(string kind) => _records.Rows.Where(r => r.Kind == kind).ToList();

    private BackupObservation IncusObservation =>
        Assert.Single(_observations.Rows, o => o.Kind == BackupLayout.IncusObservationKind);

    [Fact]
    public async Task EachBackupObject_IsARow_WithItsTenant_TimeFromTheKey_AndItsSize()
    {
        Put(Key("vms", Vm, Now.AddHours(-3)), 667);
        Put(Key("volumes", Volume, Now.AddHours(-2)), 1);

        await RunAsync();

        BackupRecord vm = Assert.Single(RowsOf(BackupRecord.VmKind));
        Assert.Equal(Vm.ToString(), vm.ResourceName);
        Assert.Equal(Tenant, vm.TenantId);
        Assert.Equal(Now.AddHours(-3), vm.TakenAt);
        Assert.Equal(667, vm.SizeBytes);
        Assert.Equal(BackupRecordStatus.Complete, vm.Status);
        Assert.Equal(Volume.ToString(), Assert.Single(RowsOf(BackupRecord.VolumeKind)).ResourceName);
        Assert.Equal(BackupReadOutcome.Read, IncusObservation.Outcome);
    }

    [Fact]
    public async Task AnEighthBackup_RemovesTheOldestFromTheBucket_AndItsRow()
    {
        for (int day = 0; day < 8; day++)
        {
            Put(Key("vms", Vm, Now.AddDays(-day)), 10);
        }

        await RunAsync();

        Assert.Equal(7, RowsOf(BackupRecord.VmKind).Count);
        Assert.DoesNotContain(Key("vms", Vm, Now.AddDays(-7)), _bucket.Objects.Keys);
        Assert.Contains(Key("vms", Vm, Now.AddDays(-6)), _bucket.Objects.Keys);
        Assert.DoesNotContain(RowsOf(BackupRecord.VmKind), r => r.TakenAt == Now.AddDays(-7));
    }

    // Seven per resource, not seven in all: one VM's history never pushes out another's.
    [Fact]
    public async Task TheSevenAreCountedPerResource()
    {
        for (int day = 0; day < 7; day++)
        {
            Put(Key("vms", Vm, Now.AddDays(-day)), 10);
            Put(Key("volumes", Volume, Now.AddDays(-day)), 10);
        }

        await RunAsync();

        Assert.Equal(14, _bucket.Objects.Count);
        Assert.Equal(7, RowsOf(BackupRecord.VmKind).Count);
        Assert.Equal(7, RowsOf(BackupRecord.VolumeKind).Count);
    }

    // Two VMs of one kind, ten backups between them: under seven each, so none goes.
    [Fact]
    public async Task TwoVmsOfOneKind_EachKeepsItsOwnBackups()
    {
        Guid other = Guid.Parse("22222222-2222-3333-4444-555555555555");
        for (int day = 0; day < 5; day++)
        {
            Put(Key("vms", Vm, Now.AddDays(-day)), 10);
            Put(Key("vms", other, Now.AddDays(-day)), 10);
        }

        await RunAsync();

        Assert.Equal(10, _bucket.Objects.Count);
        Assert.Equal(10, RowsOf(BackupRecord.VmKind).Count);
    }

    [Fact]
    public async Task ABackupNoLongerInTheBucket_LosesItsRow()
    {
        string key = Key("vms", Vm, Now.AddHours(-3));
        Put(key, 10);
        await RunAsync();
        _bucket.Objects.Remove(key);

        await RunAsync();

        Assert.Empty(RowsOf(BackupRecord.VmKind));
    }

    // A second pass over the same bucket changes nothing: the rows are keyed by kind, resource and time.
    [Fact]
    public async Task ASecondPass_KeepsTheSameRows()
    {
        Put(Key("vms", Vm, Now.AddHours(-3)), 10);
        await RunAsync();
        Guid id = Assert.Single(RowsOf(BackupRecord.VmKind)).Id;

        await RunAsync();

        Assert.Equal(id, Assert.Single(RowsOf(BackupRecord.VmKind)).Id);
    }

    [Fact]
    public async Task AnObjectThatIsNotABackup_IsSkipped_NotDeleted()
    {
        Put("incus/_spike/spike-s1/spikevm/20261026-1.tar.gz", 10);

        await RunAsync();

        Assert.Empty(RowsOf(BackupRecord.VmKind));
        Assert.Contains("incus/_spike/spike-s1/spikevm/20261026-1.tar.gz", _bucket.Objects.Keys);
        Assert.Equal(BackupReadOutcome.Read, IncusObservation.Outcome);
    }

    // Postgres rows are another pass's: the incus pass neither removes nor counts them.
    [Fact]
    public async Task PostgresRows_AreLeftToThePostgresPass()
    {
        Put(Key("vms", Vm, Now.AddHours(-3)), 10);
        Put("postgres/titan-db/base/20261024T140631/backup.info", 10);
        _bucket.Objects["postgres/titan-db/base/20261024T140631/backup.info"] =
            System.Text.Encoding.UTF8.GetBytes("status=DONE\nend_time=2026-10-24 14:06:33+00:00\n");

        await RunAsync();

        Assert.Equal("20261024T140631", Assert.Single(RowsOf(BackupRecord.PostgresKind)).BackupKey);
        Assert.Single(RowsOf(BackupRecord.VmKind));
    }

    [Fact]
    public async Task ABucketThatCannotBeRead_KeepsTheRows_AndSaysSo()
    {
        Put(Key("vms", Vm, Now.AddHours(-3)), 10);
        await RunAsync();
        _bucket.OnListObjects = _ => throw new BackupBucketException("access denied (AccessDenied)");

        await RunAsync();

        Assert.Single(RowsOf(BackupRecord.VmKind));
        Assert.Equal(BackupReadOutcome.Unreadable, IncusObservation.Outcome);
    }
}
