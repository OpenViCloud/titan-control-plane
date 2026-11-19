using System.Globalization;

namespace Titan.Application.Services.Platform.Backup;

/// One cluster config backup, read from its object key: incus/<clusterId>/cluster/<yyyyMMddTHHmmssZ>.yaml, the key the
/// worker gives it (titan-ops features/backup/design.md#cluster-backup). Like a disk backup, no manifest: the bucket shows
/// an object only once its upload is complete.
public record ClusterConfigBackupKey(string ClusterId, string BackupKey, DateTimeOffset TakenAt)
{
    private const string Folder = "cluster";
    private const string Extension = ".yaml";
    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// The config backup the key names, or null for any other object.
    public static ClusterConfigBackupKey? Parse(string objectKey)
    {
        if (!objectKey.StartsWith(BackupLayout.IncusRoot, StringComparison.Ordinal) ||
            !objectKey.EndsWith(Extension, StringComparison.Ordinal))
        {
            return null;
        }
        string[] parts = objectKey[BackupLayout.IncusRoot.Length..^Extension.Length].Split('/');
        if (parts.Length != 3 || parts[1] != Folder || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }
        bool isTime = DateTime.TryParseExact(parts[2], TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime takenAt);
        if (!isTime)
        {
            return null;
        }
        return new ClusterConfigBackupKey(parts[0], parts[2], new DateTimeOffset(takenAt, TimeSpan.Zero));
    }
}
