namespace Titan.Application.Queries.Platform.Backup.Contracts;

// One archive of a VM or volume; BackupKey is what a restore names it by.
public record BackupArchiveQueryResult(string BackupKey, DateTimeOffset TakenAt, long SizeBytes);
