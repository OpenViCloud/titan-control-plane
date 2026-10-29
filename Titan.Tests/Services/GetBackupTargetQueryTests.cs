using System.Text.Json;
using Titan.Application.Queries.Platform.BackupTarget;
using Titan.Application.Queries.Platform.BackupTarget.Contracts;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Services;

// GET /v1/admin/backup-target shows the configured target and never the secret
// (titan-ops features/backup/user-stories.md#bak-01).
public class GetBackupTargetQueryTests
{
    [Fact]
    public async Task TheConfiguredTarget_IsShown_WithoutAnythingSecret()
    {
        var result = await new GetBackupTargetQueryHandler(ConfiguredBackupTarget.Options()).HandleAsync(new GetBackupTargetQuery());

        Assert.Equal(new BackupTargetQueryResult("https://acct.r2.cloudflarestorage.com", "auto", "titan-backup-dev", "key-id"), result);
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(ConfiguredBackupTarget.Secret, json);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoKeyPair_IsNotFound()
    {
        var target = ConfiguredBackupTarget.Valid();
        target.AccessKeyId = "";
        target.SecretAccessKey = "";

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => new GetBackupTargetQueryHandler(ConfiguredBackupTarget.Options(target)).HandleAsync(new GetBackupTargetQuery()));

        Assert.Equal("BACKUP_TARGET_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task AConfigurationThatCannotWork_Is503_NotThe404()
    {
        var target = ConfiguredBackupTarget.Valid();
        target.Bucket = "";

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => new GetBackupTargetQueryHandler(ConfiguredBackupTarget.Options(target)).HandleAsync(new GetBackupTargetQuery()));

        Assert.Equal("BACKUP_TARGET_CONFIG_INVALID", error.Code);
    }
}
