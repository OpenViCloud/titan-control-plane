using Titan.Application.Commands.Platform.Backup;
using Titan.Domain.Aggregates.Platform.BackupRecord;

namespace Titan.Tests.Services;

// The worker names each object incus/<cluster>/<tenant>/<vms|volumes>/<id>/<UTC time>.tar.gz
// (OpenViCloud/titan-incus-worker:resource/backup/target.go ObjectKey); the key alone says what the backup is.
public class IncusBackupKeyTests
{
    private static readonly Guid Tenant = Guid.Parse("0123abcd-0000-4000-8000-000000000001");
    private static readonly Guid Vm = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void AVmBackupKey_GivesKindResourceTenantAndTime()
    {
        IncusBackupKey? key = IncusBackupKey.Parse($"incus/local-mac/{Tenant}/vms/{Vm}/20261026T020709Z.tar.gz");

        Assert.NotNull(key);
        Assert.Equal(BackupRecord.VmKind, key.Kind);
        Assert.Equal(Vm, key.ResourceId);
        Assert.Equal(Tenant, key.TenantId);
        Assert.Equal("20261026T020709Z", key.BackupKey);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 2, 7, 9, TimeSpan.Zero), key.TakenAt);
    }

    [Fact]
    public void AVolumeBackupKey_IsAVolume()
    {
        IncusBackupKey? key = IncusBackupKey.Parse($"incus/local-mac/{Tenant}/volumes/{Vm}/20261026T020709Z.tar.gz");

        Assert.Equal(BackupRecord.VolumeKind, key!.Kind);
    }

    // Anything else under incus/ is not a backup the page can name: skipped, never a failure of the whole pass.
    [Theory]
    [InlineData("incus/local-mac/0123abcd-0000-4000-8000-000000000001/vms/11111111-2222-3333-4444-555555555555/20261026T020709Z.tar")]
    [InlineData("incus/local-mac/0123abcd-0000-4000-8000-000000000001/disks/11111111-2222-3333-4444-555555555555/20261026T020709Z.tar.gz")]
    [InlineData("incus/local-mac/not-a-tenant/vms/11111111-2222-3333-4444-555555555555/20261026T020709Z.tar.gz")]
    [InlineData("incus/local-mac/0123abcd-0000-4000-8000-000000000001/vms/not-a-vm/20261026T020709Z.tar.gz")]
    [InlineData("incus/local-mac/0123abcd-0000-4000-8000-000000000001/vms/11111111-2222-3333-4444-555555555555/2026-10-26.tar.gz")]
    [InlineData("incus/local-mac/0123abcd-0000-4000-8000-000000000001/vms/11111111-2222-3333-4444-555555555555/x/20261026T020709Z.tar.gz")]
    [InlineData("incus/_spike/spike-s1/spikevm/20261026-1.tar.gz")]
    [InlineData("other/local-mac/0123abcd-0000-4000-8000-000000000001/vms/11111111-2222-3333-4444-555555555555/20261026T020709Z.tar.gz")]
    [InlineData("postgres/titan-db/base/20261024T140631/backup.info")]
    public void AnyOtherKey_IsNotABackup(string objectKey)
    {
        Assert.Null(IncusBackupKey.Parse(objectKey));
    }
}
