using Titan.Application.Abstractions;
using Titan.Infrastructure.Incus;

namespace Titan.Infrastructure.Backup;

// The backup secret is sealed with the same AES-GCM key as Titan's client key, so changing Encryption__ClusterKey means
// entering the secret again (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target).
public sealed class ClusterKeyBackupSecretProtector(IClusterCredentialCipher cipher) : IBackupTargetSecretProtector
{
    public byte[] Protect(string plaintext) => cipher.Encrypt(plaintext);

    public string Unprotect(byte[] protectedSecret) => cipher.Decrypt(protectedSecret);
}
