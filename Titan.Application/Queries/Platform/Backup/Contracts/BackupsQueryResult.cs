namespace Titan.Application.Queries.Platform.Backup.Contracts;

/// ReadAt: when the bucket was last tried, whether or not that pass could read it (null before the first pass).
public record BackupsQueryResult(DateTimeOffset? ReadAt, IReadOnlyList<BackupItemQueryResult> Items);
