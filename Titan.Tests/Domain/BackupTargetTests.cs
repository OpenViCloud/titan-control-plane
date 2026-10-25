using System.Text;
using Titan.Domain.Exceptions;
using Titan.Domain.Aggregates.Platform.BackupTarget;

namespace Titan.Tests.Domain;

// The S3 bucket of the environment's backups (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target). One row with a fixed id; the secret is
// write-only: absent on a replace means "keep the one stored".
public class BackupTargetTests
{
    private const string Endpoint = "https://acct.r2.cloudflarestorage.com";
    private static readonly byte[] FirstSecret = Encoding.UTF8.GetBytes("first-cipher");
    private static readonly byte[] SecondSecret = Encoding.UTF8.GetBytes("second-cipher");

    private static BackupTarget Target() => BackupTarget.Create(Endpoint, "auto", "titan-backup-dev", "key-id", FirstSecret);

    [Fact]
    public void Create_UsesTheOneFixedId()
    {
        Assert.Equal(BackupTarget.SingletonId, Target().Id);
        Assert.NotEqual(Guid.Empty, Target().Id);
    }

    [Fact]
    public void Create_KeepsWhatItIsGiven_AndDropsATrailingSlashOfTheEndpoint()
    {
        var target = BackupTarget.Create(Endpoint + "/", "auto", "titan-backup-dev", "key-id", FirstSecret);

        Assert.Equal(Endpoint, target.Endpoint);
        Assert.Equal("auto", target.Region);
        Assert.Equal("titan-backup-dev", target.Bucket);
        Assert.Equal("key-id", target.AccessKeyId);
        Assert.Equal(FirstSecret, target.SecretCipher);
        Assert.NotEqual(default, target.UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_WithoutASecret_IsRefusedWithItsOwnCode(bool empty)
    {
        byte[]? none = empty ? [] : null;

        var ex = Assert.Throws<BusinessException>(
            () => BackupTarget.Create(Endpoint, "auto", "titan-backup-dev", "key-id", none));

        Assert.Equal("BACKUP_TARGET_SECRET_REQUIRED", ex.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replace_WithoutASecret_KeepsTheStoredOne(bool empty)
    {
        var target = Target();
        byte[]? none = empty ? [] : null;

        target.Replace("https://other.example.com", "eu", "other-bucket", "other-key", none);

        Assert.Equal(FirstSecret, target.SecretCipher);
        Assert.Equal("https://other.example.com", target.Endpoint);
        Assert.Equal("eu", target.Region);
        Assert.Equal("other-bucket", target.Bucket);
        Assert.Equal("other-key", target.AccessKeyId);
    }

    [Fact]
    public void Replace_WithASecret_ReplacesIt()
    {
        var target = Target();

        target.Replace(Endpoint, "auto", "titan-backup-dev", "key-id", SecondSecret);

        Assert.Equal(SecondSecret, target.SecretCipher);
    }

    [Fact]
    public void Replace_MovesUpdatedAtForward()
    {
        var target = Target();
        var before = target.UpdatedAt;
        Thread.Sleep(30);

        target.Replace(Endpoint, "auto", "titan-backup-dev", "key-id", null);

        Assert.True(target.UpdatedAt > before);
    }

    [Theory]
    [InlineData("https://acct.r2.cloudflarestorage.com", true)]
    [InlineData("https://acct.r2.cloudflarestorage.com/", true)]
    [InlineData("https://s3.example.com:9000", true)]
    [InlineData("http://acct.r2.cloudflarestorage.com", false)]
    [InlineData("ftp://acct.r2.cloudflarestorage.com", false)]
    [InlineData("https://user:pass@acct.r2.cloudflarestorage.com", false)]
    [InlineData("https://", false)]
    [InlineData("https://s3.example.com/?X-Amz-Signature=abc", false)]
    [InlineData("https://s3.example.com/#frag", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidEndpoint_AcceptsOnlyAnHttpsUrlWithoutCredentials(string? endpoint, bool expected)
    {
        Assert.Equal(expected, BackupTarget.IsValidEndpoint(endpoint));
    }

    [Theory]
    [InlineData("titan-backup-dev", true)]
    [InlineData("a", true)]
    [InlineData("bucket.with.dots", true)]
    [InlineData("Upper", false)]
    [InlineData("has space", false)]
    [InlineData("with/slash", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidBucket_AcceptsLowerCaseDigitsHyphenAndDot(string? bucket, bool expected)
    {
        Assert.Equal(expected, BackupTarget.IsValidBucket(bucket));
    }

    // The audit line carries the endpoint with the other fields at their maximum, so its length is bounded by the audit column.
    [Fact]
    public void IsValidEndpoint_StopsAt200Characters()
    {
        string host = new('a', BackupTarget.MaxEndpointLength - "https://.io".Length);

        Assert.True(BackupTarget.IsValidEndpoint($"https://{host}.io"));
        Assert.False(BackupTarget.IsValidEndpoint($"https://{host}x.io"));
    }

    // A space or control character inside the URL would reach the audit line (forging its `; secret=kept` part) and the S3 client.
    [Theory]
    [InlineData("https://a.com/x y; secret=kept")]
    [InlineData("https://a.com/x\ny")]
    [InlineData("https://a.com/x\ty")]
    [InlineData("https://a.com/x\u0001y")]
    public void IsValidEndpoint_RefusesWhitespaceAndControlCharactersInside(string endpoint)
    {
        Assert.False(BackupTarget.IsValidEndpoint(endpoint));
    }

    [Theory]
    [InlineData("0123456789abcdefABCDEF0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("abc\n", false)]
    [InlineData("has space", false)]
    [InlineData(null, false)]
    public void IsValidSecretAccessKey_IsNonEmptyWithoutWhitespace(string? secret, bool expected)
    {
        Assert.Equal(expected, BackupTarget.IsValidSecretAccessKey(secret));
    }

    [Fact]
    public void IsValidSecretAccessKey_StopsAt256Characters()
    {
        Assert.True(BackupTarget.IsValidSecretAccessKey(new string('s', 256)));
        Assert.False(BackupTarget.IsValidSecretAccessKey(new string('s', 257)));
    }

    [Fact]
    public void IsValidBucket_StopsAt63Characters()
    {
        Assert.True(BackupTarget.IsValidBucket(new string('a', 63)));
        Assert.False(BackupTarget.IsValidBucket(new string('a', 64)));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("eu-west-1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData(null, false)]
    public void IsValidRegion_AcceptsLettersDigitsAndHyphen(string? region, bool expected)
    {
        Assert.Equal(expected, BackupTarget.IsValidRegion(region));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData(null, false)]
    public void IsValidAccessKeyId_IsNonEmptyWithoutSpaces(string? accessKeyId, bool expected)
    {
        Assert.Equal(expected, BackupTarget.IsValidAccessKeyId(accessKeyId));
    }

    [Fact]
    public void IsValidAccessKeyId_StopsAt128Characters()
    {
        Assert.True(BackupTarget.IsValidAccessKeyId(new string('k', 128)));
        Assert.False(BackupTarget.IsValidAccessKeyId(new string('k', 129)));
    }

    // The domain refuses what the validator refuses, so a caller that skips the validator cannot store it either.
    [Fact]
    public void Create_WithAnInvalidField_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => BackupTarget.Create("http://x", "auto", "b", "k", FirstSecret));
        Assert.Throws<ArgumentException>(() => BackupTarget.Create(Endpoint, "", "b", "k", FirstSecret));
        Assert.Throws<ArgumentException>(() => BackupTarget.Create(Endpoint, "auto", "", "k", FirstSecret));
        Assert.Throws<ArgumentException>(() => BackupTarget.Create(Endpoint, "auto", "b", "", FirstSecret));
    }

    [Fact]
    public void Replace_WithAnInvalidField_IsRefused_AndChangesNothing()
    {
        var target = Target();

        Assert.Throws<ArgumentException>(() => target.Replace("http://x", "auto", "b", "k", SecondSecret));

        Assert.Equal(Endpoint, target.Endpoint);
        Assert.Equal(FirstSecret, target.SecretCipher);
    }
}
