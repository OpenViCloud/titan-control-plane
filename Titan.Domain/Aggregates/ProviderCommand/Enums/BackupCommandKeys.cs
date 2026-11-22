using System.Globalization;

namespace Titan.Domain.Aggregates.ProviderCommand;

/// The keys of the daily backup commands: the UTC day is in the key, so a day's backup that ran is not asked again and one
/// that failed is (titan-ops features/backup/design.md#incus-backup). The schedule reads them back to know when a cluster's
/// disks are done for the day (titan-ops features/backup/design.md#cluster-backup).
public static class BackupCommandKeys
{
    public static string Vm(Guid vmId, DateOnly day) => $"vm-backup-{vmId}-{Day(day)}";

    public static string Volume(Guid volumeId, DateOnly day) => $"volume-backup-{volumeId}-{Day(day)}";

    public static string Cluster(string clusterId, DateOnly day) => $"cluster-backup-{clusterId}-{Day(day)}";

    /// A config asked again later the same day, after a disk that the day's first config does not know: one key per UTC
    /// hour, so a pass that runs twice in an hour asks it once (titan-ops features/backup/design.md#cluster-backup).
    public static string ClusterAgain(string clusterId, DateOnly day, int hour) =>
        $"cluster-backup-{clusterId}-{Day(day)}{hour.ToString("00", CultureInfo.InvariantCulture)}";

    // Gregorian digits whatever the host's culture: a Buddhist-calendar culture would write another year into the key.
    private static string Day(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
