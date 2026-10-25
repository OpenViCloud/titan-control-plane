using Titan.Application.Options;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Services;

// The target is what the environment's GitOps config says. No key pair is "not configured" (404), a value that cannot work is
// the environment's error (503) naming the setting, and no message carries the key pair
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/user-stories.md#bak-01).
public class BackupTargetOptionsTests
{
    private static BackupTargetOptions With(Action<BackupTargetOptions> change)
    {
        var target = ConfiguredBackupTarget.Valid();
        change(target);
        return target;
    }

    [Fact]
    public void AValidTarget_HasNoProblem_AndGivesItsConnection()
    {
        var target = ConfiguredBackupTarget.Valid();

        Assert.Null(target.Problem());
        Assert.Equal(
            new Titan.Application.Abstractions.BackupBucketConnection(
                "https://acct.r2.cloudflarestorage.com", "auto", "titan-backup-dev", "key-id", ConfiguredBackupTarget.Secret),
            target.ToConnection());
    }

    [Fact]
    public void ATrailingSlashOnTheEndpoint_IsDropped()
    {
        var target = With(t => t.Endpoint = "https://acct.r2.cloudflarestorage.com/");

        Assert.Equal("https://acct.r2.cloudflarestorage.com", target.ToConnection().Endpoint);
    }

    [Fact]
    public void WhitespaceAroundTheEndpoint_NeverReachesTheConnection()
    {
        var target = With(t => t.Endpoint = "  https://acct.r2.cloudflarestorage.com/\n");

        Assert.Equal("https://acct.r2.cloudflarestorage.com", target.ToConnection().Endpoint);
    }

    [Theory]
    [InlineData("", "only-a-secret")]
    [InlineData("only-a-key-id", "")]
    public void HalfAKeyPair_IsAKeyPair_ThatCannotWork_NotMissing(string keyId, string secret)
    {
        var target = With(t => { t.AccessKeyId = keyId; t.SecretAccessKey = secret; });

        Assert.True(target.HasKeyPair);
        var error = Assert.Throws<ServiceUnavailableException>(() => target.ToConnection());
        Assert.Equal("BACKUP_TARGET_CONFIG_INVALID", error.Code);
    }

    [Fact]
    public void NoKeyPair_IsNotFound_WhateverTheOtherValuesAre()
    {
        var target = With(t => { t.AccessKeyId = ""; t.SecretAccessKey = ""; t.Endpoint = "not a url"; });

        Assert.False(target.HasKeyPair);
        var error = Assert.Throws<NotFoundException>(() => target.ToConnection());
        Assert.Equal("BACKUP_TARGET_NOT_FOUND", error.Code);
    }

    [Theory]
    [InlineData("endpoint", "http://acct.r2.cloudflarestorage.com", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "acct.r2.cloudflarestorage.com", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "https://acct.r2.cloudflarestorage.com/bucket", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "https://acct.r2.cloudflarestorage.com?x=1", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "https://acct.r2.cloudflarestorage.com#frag", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "https://user:pass@acct.r2.cloudflarestorage.com", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "https://acct.r2.cloud\nflarestorage.com", "BackupTarget__Endpoint")]
    [InlineData("endpoint", "", "BackupTarget__Endpoint")]
    [InlineData("region", "", "BackupTarget__Region")]
    [InlineData("region", "us east", "BackupTarget__Region")]
    [InlineData("bucket", "", "BackupTarget__Bucket")]
    [InlineData("bucket", "Bad_Bucket", "BackupTarget__Bucket")]
    [InlineData("bucket", "bucket with space", "BackupTarget__Bucket")]
    [InlineData("accessKeyId", "key id", "BackupTarget__AccessKeyId")]
    [InlineData("secret", "", "BackupTarget__SecretAccessKey")]
    [InlineData("secret", "has space", "BackupTarget__SecretAccessKey")]
    public void AValueThatCannotWork_Is503_NamingTheSetting_AndNeverTheKeyPair(string field, string value, string setting)
    {
        var target = With(t =>
        {
            switch (field)
            {
                case "endpoint": t.Endpoint = value; break;
                case "region": t.Region = value; break;
                case "bucket": t.Bucket = value; break;
                case "accessKeyId": t.AccessKeyId = value; break;
                default: t.SecretAccessKey = value; break;
            }
        });

        var error = Assert.Throws<ServiceUnavailableException>(() => target.ToConnection());

        Assert.Equal("BACKUP_TARGET_CONFIG_INVALID", error.Code);
        Assert.Contains(setting, error.Message);
        Assert.DoesNotContain(ConfiguredBackupTarget.Secret, error.Message);
    }

    [Fact]
    public void ABucketOf63Characters_Works_AndOf64_DoesNot()
    {
        Assert.Null(With(t => t.Bucket = new string('a', 63)).Problem());
        Assert.NotNull(With(t => t.Bucket = new string('a', 64)).Problem());
    }

    [Fact]
    public void AnEndpointOf200Characters_Works_AndOf201_DoesNot()
    {
        string host(int length) => "https://" + new string('a', length - "https://".Length - ".com".Length) + ".com";

        Assert.Null(With(t => t.Endpoint = host(200)).Problem());
        Assert.NotNull(With(t => t.Endpoint = host(201)).Problem());
    }

    [Fact]
    public void AKeyIdOf128Characters_AndASecretOf256_Work_AndOneMoreDoesNot()
    {
        Assert.Null(With(t => { t.AccessKeyId = new string('k', 128); t.SecretAccessKey = new string('s', 256); }).Problem());
        Assert.NotNull(With(t => t.AccessKeyId = new string('k', 129)).Problem());
        Assert.NotNull(With(t => t.SecretAccessKey = new string('s', 257)).Problem());
    }

    [Fact]
    public void TheOptionsPrintNoKeyPair_WhenAnExceptionOrALogShowsThem()
    {
        // BackupBucketConnection is what travels; its ToString hides the secret. The options themselves are never logged.
        string text = ConfiguredBackupTarget.Valid().ToConnection().ToString();

        Assert.DoesNotContain(ConfiguredBackupTarget.Secret, text);
    }
}
