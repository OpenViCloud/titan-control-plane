namespace Titan.Application.Abstractions;

public interface IBackupBucketFactory
{
    IBackupBucket Open(BackupBucketConnection connection);
}
