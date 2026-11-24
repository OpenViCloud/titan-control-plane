namespace Titan.Application.Queries.Admin.Backup.Contracts;

/// An admin's restore of a backup archive as a new VM or volume (titan-ops features/backup/design.md#restore-as-new-resource).
/// Kind is "vm" or "volume"; ResourceId is the new resource and Name its name now, null when its row is gone. TakenAt
/// is when the archive was taken. State is "running" (waiting or with the worker), "done" or "failed" (Reason says why).
/// AskedAt is the moment the restore was asked, the row keeping no finish time.
public record BackupRestoreQueryResult(
    string Kind,
    Guid ResourceId,
    string? Name,
    Guid? TenantId,
    DateTimeOffset? TakenAt,
    string State,
    DateTimeOffset AskedAt,
    string? Reason);
