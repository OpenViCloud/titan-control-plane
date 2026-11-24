using Titan.Application.Queries.Platform.Backup.Contracts;

namespace Titan.Application.Queries.Admin.Backup.Contracts;

/// What the Backups page reads: every protected resource (the platform's Postgres and the tenants' disks), each
/// cluster's latest restore drill, and the admin's restores of a backup archive of the last 30 days.
public record AdminBackupsResult(
    DateTimeOffset? ReadAt,
    IReadOnlyList<BackupItemQueryResult> Items,
    IReadOnlyList<RestoreDrillQueryResult> RestoreDrills,
    IReadOnlyList<BackupRestoreQueryResult> Restores);
