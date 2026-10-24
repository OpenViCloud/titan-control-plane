using Titan.Application.Abstractions;

namespace Titan.Infrastructure.Backup;

public sealed class S3BackupBucketFactory : IBackupBucketFactory
{
    public IBackupBucket Open(BackupBucketConnection connection) => new S3BackupBucket(connection);
}
