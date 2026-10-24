namespace Titan.Application.Commands.Platform.BackupTarget.Contracts;

// Bound straight from the PUT body. SecretAccessKey is write-only: null or empty keeps the stored one.
public record SetBackupTargetCommand(
    string Endpoint, string Region, string Bucket, string AccessKeyId, string? SecretAccessKey);
