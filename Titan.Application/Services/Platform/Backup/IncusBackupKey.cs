using System.Globalization;
using Titan.Domain.Aggregates.BackupRecord;

namespace Titan.Application.Services.Platform.Backup;

/// One VM or volume backup, read from its object key: incus/<clusterId>/<tenantId>/<vms|volumes>/<id>/<yyyyMMddTHHmmssZ>.tar.gz,
/// the key the worker gives it (titan-ops features/backup/design.md#incus-backup).
/// No manifest: an object under such a key is a whole backup, since the bucket shows an upload only once it is complete.
public record IncusBackupKey(string Kind, Guid ResourceId, Guid TenantId, string BackupKey, DateTimeOffset TakenAt)
{
    private const string Extension = ".tar.gz";
    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// The shape of BackupKey, the time as the object key writes it. A request that names an archive is held to it
    /// before anything is read: the worker puts the key it is given back into an object key.
    public const string BackupKeyPattern = @"^[0-9]{8}T[0-9]{6}Z\z";

    /// The backup the key names, or null for any other object.
    public static IncusBackupKey? Parse(string objectKey)
    {
        if (!objectKey.StartsWith(BackupLayout.IncusRoot, StringComparison.Ordinal) ||
            !objectKey.EndsWith(Extension, StringComparison.Ordinal))
        {
            return null;
        }
        string[] parts = objectKey[BackupLayout.IncusRoot.Length..^Extension.Length].Split('/');
        if (parts.Length != 5)
        {
            return null;
        }
        string kind;
        if (parts[2] == "vms")
        {
            kind = BackupRecord.VmKind;
        }
        else if (parts[2] == "volumes")
        {
            kind = BackupRecord.VolumeKind;
        }
        else
        {
            return null;
        }
        if (!Guid.TryParse(parts[1], out Guid tenantId) || !Guid.TryParse(parts[3], out Guid resourceId))
        {
            return null;
        }
        DateTimeOffset? takenAt = TakenAtOf(parts[4]);
        if (takenAt == null)
        {
            return null;
        }
        return new IncusBackupKey(kind, resourceId, tenantId, parts[4], takenAt.Value);
    }

    /// The time a backup key names, or null when it names none.
    public static DateTimeOffset? TakenAtOf(string backupKey)
    {
        bool isTime = DateTime.TryParseExact(backupKey, TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime takenAt);
        if (!isTime)
        {
            return null;
        }
        return new DateTimeOffset(takenAt, TimeSpan.Zero);
    }
}
