using Titan.Application.Queries.Platform.BackupTarget.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Queries.Platform.BackupTarget;

public class GetBackupTargetQueryHandler(IBackupTargetRepository repo)
{
    public async Task<BackupTargetQueryResult> HandleAsync(GetBackupTargetQuery query, CancellationToken ct = default)
    {
        var target = await repo.FindAsync(ct)
            ?? throw new NotFoundException("BACKUP_TARGET_NOT_FOUND", "No backup target has been set.");

        return new BackupTargetQueryResult(target.Endpoint, target.Region, target.Bucket, target.AccessKeyId, target.UpdatedAt);
    }
}
