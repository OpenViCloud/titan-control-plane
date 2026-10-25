using FastEndpoints;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// The newest backup of each protected resource and its health. Read-only: backing up and restoring happen elsewhere.
public class ListBackupsEndpoint(ListBackupsQueryHandler handler) : EndpointWithoutRequest<BackupsQueryResult>
{
    public override void Configure()
    {
        Get("v1/admin/backups");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListBackupsQuery(), ct);
        await SendAsync(result, cancellation: ct);
    }
}
