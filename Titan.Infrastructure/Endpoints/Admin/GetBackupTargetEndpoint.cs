using FastEndpoints;
using Titan.Application.Queries.Platform.BackupTarget;
using Titan.Application.Queries.Platform.BackupTarget.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// Never returns the secret; 404 BACKUP_TARGET_NOT_FOUND until an admin sets the target.
public class GetBackupTargetEndpoint(GetBackupTargetQueryHandler handler)
    : EndpointWithoutRequest<BackupTargetQueryResult>
{
    public override void Configure()
    {
        Get("v1/admin/backup-target");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetBackupTargetQuery(), ct);
        await SendAsync(result, cancellation: ct);
    }
}
