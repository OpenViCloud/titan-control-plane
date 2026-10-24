using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Titan.Domain.Aggregates.Platform.AdminAuditLog;
using Titan.Domain.Repositories.Platform;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Application.Commands.Platform.BackupTarget;

/// Admin sets the backup target (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target); the secret is sealed here and never leaves.
/// Returns nothing (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#commands-return-no-data).
public class SetBackupTargetCommandHandler(
    IBackupTargetRepository repo,
    IBackupTargetSecretProtector protector,
    IAdminAuditLogRepository auditLog,
    ICurrentUser currentUser)
{
    /// Constants so every reader that filters the audit trail matches the same way.
    public const string SetAction = "backup.target.set";
    public const string ResourceType = "backup_target";
    private const string ResourceKey = "backup-target";

    public async Task HandleAsync(SetBackupTargetCommand command, CancellationToken ct = default)
    {
        var (endpoint, region, bucket, accessKeyId, secretAccessKey) = command;

        bool hasNewSecret = !string.IsNullOrEmpty(secretAccessKey);
        byte[]? secretCipher = null;
        if (hasNewSecret)
        {
            if (!BackupTargetAggregate.IsValidSecretAccessKey(secretAccessKey))
            {
                throw new ArgumentException("secretAccessKey must be 1 to 256 characters without spaces", nameof(command));
            }
            secretCipher = protector.Protect(secretAccessKey!);
        }

        BackupTargetAggregate? existing = await repo.FindAsync(ct);
        BackupTargetAggregate target;
        if (existing is null)
        {
            target = BackupTargetAggregate.Create(endpoint, region, bucket, accessKeyId, secretCipher);
            repo.Add(target);
        }
        else
        {
            existing.Replace(endpoint, region, bucket, accessKeyId, secretCipher);
            target = existing;
        }

        // Staged on the same DbContext as the target, so the trace and the change commit together. The detail says
        // whether the secret changed, never what it is.
        string secretNote = hasNewSecret ? "changed" : "kept";
        auditLog.Add(AdminAuditLog.CreatePlatform(
            SetAction, currentUser.KeycloakUserId, ResourceType, ResourceKey,
            detail: $"endpoint={target.Endpoint}; region={target.Region}; bucket={target.Bucket}; accessKeyId={target.AccessKeyId}; secret={secretNote}"));

        await repo.SaveAsync(ct);
    }
}
