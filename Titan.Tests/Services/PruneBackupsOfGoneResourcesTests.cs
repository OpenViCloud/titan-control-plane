using Moq;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Domain.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The backups of a VM or volume Titan no longer holds stay seven days after the newest, then go
// (titan-ops features/backup/design.md#backups-of-gone-resources).
public class PruneBackupsOfGoneResourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("0123abcd-0000-4000-8000-000000000001");

    private readonly InMemoryBackupBucket _bucket = new();
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<IVolumeRepository> _volumes = new();

    public PruneBackupsOfGoneResourcesTests()
    {
        Alive([], []);
    }

    private void Alive(Guid[] vms, Guid[] volumes)
    {
        _vms.Setup(r => r.ListIdsNotDeletedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Where(vms.Contains).ToList());
        _volumes.Setup(r => r.ListIdsNotDeletedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Where(volumes.Contains).ToList());
    }

    private Task RunAsync() => new PruneBackupsOfGoneResourcesCommandHandler(
            ConfiguredBackupTarget.Options(ConfiguredBackupTarget.Valid()), new FixedBackupBucketFactory(_bucket),
            _vms.Object, _volumes.Object, new FixedClock(Now))
        .HandleAsync(new PruneBackupsOfGoneResourcesCommand());

    // The worker's key, spelled out here rather than taken from the code under test.
    private static string Key(string collection, Guid id, DateTimeOffset at) =>
        $"incus/local-mac/{Tenant}/{collection}/{id}/{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.tar.gz";

    private string Put(string collection, Guid id, DateTimeOffset at)
    {
        string key = Key(collection, id, at);
        _bucket.Objects[key] = new byte[1];
        _bucket.Modified[key] = at;
        return key;
    }

    [Fact]
    public async Task AGoneVm_WhoseNewestBackupIsOverSevenDaysOld_LosesEveryBackup()
    {
        Guid gone = Guid.NewGuid();
        string newest = Put("vms", gone, Now.AddDays(-8));
        string older = Put("vms", gone, Now.AddDays(-9));

        await RunAsync();

        Assert.False(_bucket.Objects.ContainsKey(newest));
        Assert.False(_bucket.Objects.ContainsKey(older));
    }

    [Fact]
    public async Task AGoneVolume_WhoseNewestBackupIsUnderSevenDaysOld_KeepsThemAll()
    {
        Guid gone = Guid.NewGuid();
        string newest = Put("volumes", gone, Now.AddDays(-6));
        string older = Put("volumes", gone, Now.AddDays(-12));

        await RunAsync();

        Assert.True(_bucket.Objects.ContainsKey(newest));
        Assert.True(_bucket.Objects.ContainsKey(older));
    }

    [Fact]
    public async Task AVmOrVolumeTitanStillHolds_KeepsItsBackups_HoweverOld()
    {
        Guid vm = Guid.NewGuid();
        Guid volume = Guid.NewGuid();
        Alive([vm], [volume]);
        string vmKey = Put("vms", vm, Now.AddDays(-30));
        string volumeKey = Put("volumes", volume, Now.AddDays(-30));

        await RunAsync();

        Assert.True(_bucket.Objects.ContainsKey(vmKey));
        Assert.True(_bucket.Objects.ContainsKey(volumeKey));
    }

    // A volume id Titan holds as a VM's says nothing about the volume: each kind asks its own repository.
    [Fact]
    public async Task AnIdAliveAsAVm_DoesNotKeepAVolumeOfTheSameId()
    {
        Guid id = Guid.NewGuid();
        Alive([id], []);
        string volumeKey = Put("volumes", id, Now.AddDays(-8));

        await RunAsync();

        Assert.False(_bucket.Objects.ContainsKey(volumeKey));
    }

    [Fact]
    public async Task ObjectsThatAreNotDiskBackups_AreLeftAlone()
    {
        const string config = "incus/local-mac/cluster/20261001T000000Z.yaml";
        _bucket.Objects[config] = new byte[1];
        _bucket.Objects["postgres/titan-db/base/x/backup.info"] = new byte[1];

        await RunAsync();

        Assert.True(_bucket.Objects.ContainsKey(config));
        Assert.True(_bucket.Objects.ContainsKey("postgres/titan-db/base/x/backup.info"));
    }

    [Fact]
    public async Task NoKeyPair_DoesNothing()
    {
        Guid gone = Guid.NewGuid();
        string key = Put("vms", gone, Now.AddDays(-8));

        await new PruneBackupsOfGoneResourcesCommandHandler(
                ConfiguredBackupTarget.Options(new Titan.Application.Options.BackupTargetOptions()),
                new FixedBackupBucketFactory(_bucket), _vms.Object, _volumes.Object, new FixedClock(Now))
            .HandleAsync(new PruneBackupsOfGoneResourcesCommand());

        Assert.True(_bucket.Objects.ContainsKey(key));
    }
}
