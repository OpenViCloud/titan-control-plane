namespace Titan.Application.Abstractions;

// A named failure of the bucket (denied, unknown bucket, unreachable, timed out). The message is written by the adapter and
// is safe to show an admin: it never carries the secret or the SDK's own text.
public class BackupBucketException(string message) : Exception(message);
