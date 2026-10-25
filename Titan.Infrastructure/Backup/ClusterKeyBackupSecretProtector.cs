using System.Security.Cryptography;
using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Incus;

namespace Titan.Infrastructure.Backup;

// The backup secret is sealed with the same AES-GCM key as Titan's client key, so changing Encryption__ClusterKey means
// entering the secret again (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target).
public sealed class ClusterKeyBackupSecretProtector(IClusterCredentialCipher cipher) : IBackupTargetSecretProtector
{
    public byte[] Protect(string plaintext) => cipher.Encrypt(plaintext);

    public string Unprotect(byte[] protectedSecret)
    {
        try
        {
            return cipher.Decrypt(protectedSecret);
        }
        catch (CryptographicException)
        {
            throw Unreadable();
        }
        catch (ArgumentException)
        {
            // A blob too short to hold nonce and tag: the row is corrupted.
            throw Unreadable();
        }
    }

    private static ServiceUnavailableException Unreadable() =>
        new("BACKUP_TARGET_SECRET_UNREADABLE",
            "The backup target's secret cannot be decrypted with the current Encryption__ClusterKey (key changed, or the stored secret is corrupted). Enter the secret again.");
}
