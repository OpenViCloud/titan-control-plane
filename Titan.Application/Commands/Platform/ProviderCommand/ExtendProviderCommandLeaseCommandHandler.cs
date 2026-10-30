using Titan.Application.Commands.Platform.ProviderCommand.Contracts;
using Titan.Application.Services.Platform.ProviderCommand;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Commands.Platform.ProviderCommand;

// The worker extends while it waits on a long provider operation. A lease that lost its row answers 409 and the
// worker stops: another lease now owns the command.
public class ExtendProviderCommandLeaseCommandHandler(IProviderCommandRepository repository, TimeProvider clock)
{
    public async Task HandleAsync(ExtendProviderCommandLeaseCommand command, CancellationToken ct = default)
    {
        ProviderCommandAggregate leased = await ProviderCommandLease.RequireAsync(repository, command.LeaseId, ct);
        leased.ExtendLease(command.LeaseId, clock.GetUtcNow());
        await repository.SaveChangesAsync(ct);
    }
}
