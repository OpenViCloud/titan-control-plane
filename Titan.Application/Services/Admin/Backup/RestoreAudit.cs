using Microsoft.Extensions.Logging;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Admin.Backup;

/// The trace of an admin's restore of a backup archive (titan-ops features/backup/design.md#restore-as-new-resource):
/// one entry per VM or volume it makes, written the same by the two restore handlers. Two sinks on purpose
/// (titan-ops architecture/tenancy-and-admin.md#admin-audit): a structured log line for the log aggregator, and a
/// durable row that survives log loss. The row is staged, not saved: the handler's one save writes it with the new
/// resource and its command, so the worker never restores what the audit log does not show.
public static class RestoreAudit
{
    public const string VmAction = "vm.restore.cross_tenant";
    public const string VolumeAction = "volume.restore.cross_tenant";

    /// The owner is the tenant of the source, never the caller's own: an admin belongs to a tenant too.
    public static void Stage(
        ILogger logger, IAdminAuditLogRepository auditLog, string? actorKeycloakUserId,
        string action, string resourceType, Guid newResourceId, Guid ownerTenantId, Guid sourceId, string backupKey)
    {
        logger.LogWarning(
            "admin cross-tenant action {Action} by actor {Actor} on {ResourceType} {ResourceId} owned by tenant {OwnerTenantId}, "
            + "restored from {SourceId} backup {BackupKey}",
            action, actorKeycloakUserId, resourceType, newResourceId, ownerTenantId, sourceId, backupKey);
        auditLog.Add(AdminAuditLog.Create(action, actorKeycloakUserId, resourceType, newResourceId, ownerTenantId,
            $"source={sourceId} backup={backupKey}"));
    }
}
