namespace Titan.Application.Queries.Platform.BackupTarget.Contracts;

// No secret field of any kind: the access key id identifies the key, the secret never leaves.
public record BackupTargetQueryResult(string Endpoint, string Region, string Bucket, string AccessKeyId);
