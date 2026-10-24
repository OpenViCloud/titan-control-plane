using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// Every Open is a fresh connection to the same in-memory bucket (a disposed one is reopened).
public sealed class FixedBackupBucketFactory(InMemoryBackupBucket bucket) : IBackupBucketFactory
{
    public List<BackupBucketConnection> Opened { get; } = new();

    public IBackupBucket Open(BackupBucketConnection connection)
    {
        Opened.Add(connection);
        bucket.Reopen();
        return bucket;
    }
}
