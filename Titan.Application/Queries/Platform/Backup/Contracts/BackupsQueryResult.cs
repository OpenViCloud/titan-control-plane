namespace Titan.Application.Queries.Platform.Backup.Contracts;

/// ReadAt: when the bucket was last read (null before the first pass).
public record BackupsQueryResult(DateTimeOffset? ReadAt, IReadOnlyList<BackupItemQueryResult> Items);
