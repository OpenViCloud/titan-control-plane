using FastEndpoints;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// Tests the SAVED target (no body). 200 either way: a failed probe is a result to render, not an HTTP error; 404
// BACKUP_TARGET_NOT_FOUND until an admin sets the target. Covered by TestBackupTargetCommandTests and S3BackupBucketTests.
public class TestBackupTargetEndpoint(TestBackupTargetCommandHandler handler)
    : EndpointWithoutRequest<BackupTargetTestResult>
{
    public override void Configure()
    {
        Post("v1/admin/backup-target/test");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await handler.HandleAsync(new TestBackupTargetCommand(), ct);
        await SendAsync(result, cancellation: ct);
    }
}
