namespace Titan.Application.Queries.Platform.Backup.Contracts;

// Reason names why a health is not Ok (see BackupHealthPolicy); TenantId is for the VM and volume backups of iteration 2.
public record BackupItemQueryResult(
    string Kind, string Name, Guid? TenantId, DateTimeOffset? TakenAt, long? SizeBytes,
    BackupHealth Health, string? Reason, DateTimeOffset? LastWalAt);
