namespace Titan.Application.Commands.Platform.BackupTarget.Contracts;

// Bound straight from the PUT body. SecretAccessKey is write-only: null or empty keeps the stored one.
public record SetBackupTargetCommand(
    string Endpoint, string Region, string Bucket, string AccessKeyId, string? SecretAccessKey)
{
    // The generated ToString would print the secret into any log line or test failure that shows the command.
    public override string ToString() =>
        $"SetBackupTargetCommand {{ Endpoint = {Endpoint}, Region = {Region}, Bucket = {Bucket}, AccessKeyId = {AccessKeyId}, SecretAccessKey = *** }}";
}
