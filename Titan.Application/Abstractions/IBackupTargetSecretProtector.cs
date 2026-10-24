namespace Titan.Application.Abstractions;

// The backup target's secret access key at rest (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target).
public interface IBackupTargetSecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] protectedSecret);
}
