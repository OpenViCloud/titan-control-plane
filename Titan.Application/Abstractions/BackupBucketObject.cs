namespace Titan.Application.Abstractions;

public record BackupBucketObject(string Key, long Size, DateTimeOffset LastModified);
