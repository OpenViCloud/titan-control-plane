using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Services.Platform.ProviderCommand;

/// Shared by the lease handlers: a lease id that names no row is a worker reporting on work another lease took over.
public static class ProviderCommandLease
{
    public static async Task<ProviderCommandAggregate> RequireAsync(
        IProviderCommandRepository repository, Guid leaseId, CancellationToken ct)
    {
        ProviderCommandAggregate? leased = await repository.FindByLeaseAsync(leaseId, ct);
        if (leased == null)
        {
            throw new NotFoundException("PROVIDER_COMMAND_LEASE_NOT_FOUND",
                $"No provider command is held by lease {leaseId}.");
        }
        return leased;
    }
}
