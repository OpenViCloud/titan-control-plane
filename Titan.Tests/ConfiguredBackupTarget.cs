using Microsoft.Extensions.Options;
using Titan.Application.Options;

namespace Titan.Tests;

// The backup target of an environment as its GitOps config sets it: a valid one unless a test changes a value.
public static class ConfiguredBackupTarget
{
    public const string Secret = "S3CR3T-value-0123456789";

    public static BackupTargetOptions Valid() => new()
    {
        Endpoint = "https://acct.r2.cloudflarestorage.com",
        Region = "auto",
        Bucket = "titan-backup-dev",
        AccessKeyId = "key-id",
        SecretAccessKey = Secret,
    };

    public static IOptions<BackupTargetOptions> Options(BackupTargetOptions? target = null) =>
        Microsoft.Extensions.Options.Options.Create(target ?? Valid());
}
