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

    // Gregorian digits whatever the host's culture: a Buddhist-calendar culture would write another year into the key.
    private static string Day(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
