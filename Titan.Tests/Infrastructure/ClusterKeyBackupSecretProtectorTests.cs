using Microsoft.Extensions.Options;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Backup;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure;

// A secret sealed under another Encryption__ClusterKey, or cut short, must say so by name instead of surfacing as a 500.
public class ClusterKeyBackupSecretProtectorTests
{
    private static ClusterKeyBackupSecretProtector Protector(byte keySeed) =>
        new(new AesGcmClusterCredentialCipher(Options.Create(new ClusterEncryptionOptions
        {
            ClusterKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + keySeed)).ToArray()),
        })));

    [Fact]
    public void ASecret_ComesBackWithTheSameKey()
    {
        var protector = Protector(0);

        Assert.Equal("S3CR3T-value", protector.Unprotect(protector.Protect("S3CR3T-value")));
    }

    [Fact]
    public void ASecretSealedUnderAnotherKey_IsUnreadable_ByName()
    {
        byte[] sealedByOther = Protector(7).Protect("S3CR3T-value");

        var error = Assert.Throws<ServiceUnavailableException>(() => Protector(0).Unprotect(sealedByOther));

        Assert.Equal("BACKUP_TARGET_SECRET_UNREADABLE", error.Code);
    }

    [Fact]
    public void ABlobTooShortToHoldNonceAndTag_IsUnreadable_ByName()
    {
        var error = Assert.Throws<ServiceUnavailableException>(() => Protector(0).Unprotect(new byte[] { 1, 2, 3 }));

        Assert.Equal("BACKUP_TARGET_SECRET_UNREADABLE", error.Code);
    }
}
