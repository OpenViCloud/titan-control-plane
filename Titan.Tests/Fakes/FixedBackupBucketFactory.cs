using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

public sealed class FixedBackupBucketFactory(IBackupBucket bucket) : IBackupBucketFactory
{
    public List<BackupBucketConnection> Opened { get; } = new();

    public IBackupBucket Open(BackupBucketConnection connection)
    {
        Opened.Add(connection);
        return bucket;
    }
}
