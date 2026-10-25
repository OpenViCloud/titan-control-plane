namespace Titan.Application.Abstractions;

// Where and how to reach the backup bucket. The secret is plaintext here, only for the lifetime of one call.
public record BackupBucketConnection(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretAccessKey)
{
    // The generated ToString would print the secret into any log line or test failure that shows the connection.
    public override string ToString() =>
        $"BackupBucketConnection {{ Endpoint = {Endpoint}, Region = {Region}, Bucket = {Bucket}, AccessKeyId = {AccessKeyId}, SecretAccessKey = *** }}";
}
