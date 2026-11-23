using Titan.Application.Queries.Platform.Backup.Contracts;

namespace Titan.Application.Queries.Admin.Backup.Contracts;

/// What the Backups page reads: every protected resource (the platform's Postgres and the tenants' disks) and each
/// cluster's latest restore drill.
public record AdminBackupsResult(
    DateTimeOffset? ReadAt,
    IReadOnlyList<BackupItemQueryResult> Items,
    IReadOnlyList<RestoreDrillQueryResult> RestoreDrills);
