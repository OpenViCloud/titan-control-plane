using Titan.Application.Services.Platform.Backup;

namespace Titan.Tests.Services;

// The worker's key of a cluster's config: incus/<clusterId>/cluster/<yyyyMMddTHHmmssZ>.yaml
// (titan-ops features/backup/design.md#cluster-backup).
public class ClusterConfigBackupKeyTests
{
    [Fact]
    public void AConfigKey_GivesItsClusterAndUtcTime()
    {
        ClusterConfigBackupKey? key = ClusterConfigBackupKey.Parse("incus/singapore01/cluster/20261119T020304Z.yaml");

        Assert.NotNull(key);
        Assert.Equal("singapore01", key.ClusterId);
        Assert.Equal("20261119T020304Z", key.BackupKey);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 2, 3, 4, TimeSpan.Zero), key.TakenAt);
    }

    [Theory]
    [InlineData("incus/singapore01/cluster/20261119T020304Z.tar.gz")]
    [InlineData("incus/singapore01/clusters/20261119T020304Z.yaml")]
    [InlineData("incus/singapore01/cluster/not-a-time.yaml")]
    [InlineData("incus/singapore01/0123abcd-0000-4000-8000-000000000001/vms/11111111-2222-3333-4444-555555555555/20261119T020304Z.tar.gz")]
    [InlineData("postgres/singapore01/cluster/20261119T020304Z.yaml")]
    [InlineData("incus//cluster/20261119T020304Z.yaml")]
    public void AnyOtherObject_IsNotAConfig(string objectKey)
    {
        Assert.Null(ClusterConfigBackupKey.Parse(objectKey));
    }
}
