using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Queries.Platform.BackupTarget.Contracts;

namespace Titan.Application.Queries.Platform.BackupTarget;

public class GetBackupTargetQueryHandler(IOptions<BackupTargetOptions> options)
{
    public Task<BackupTargetQueryResult> HandleAsync(GetBackupTargetQuery query, CancellationToken ct = default)
    {
        var connection = options.Value.ToConnection();
        return Task.FromResult(new BackupTargetQueryResult(connection.Endpoint, connection.Region, connection.Bucket, connection.AccessKeyId));
    }
}
