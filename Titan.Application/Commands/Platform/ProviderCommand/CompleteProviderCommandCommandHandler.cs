using Titan.Application.Commands.Platform.ProviderCommand.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Application.Services.Platform.ProviderCommand;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Commands.Platform.ProviderCommand;

public class CompleteProviderCommandCommandHandler(IProviderCommandRepository repository, TimeProvider clock)
{
    public async Task HandleAsync(CompleteProviderCommandCommand command, CancellationToken ct = default)
    {
        ProviderCommandAggregate leased = await ProviderCommandLease.RequireAsync(repository, command.LeaseId, ct);
        // A worker retrying its report after a lost response: the first one landed (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        bool alreadyCompleted = leased.Status == ProviderCommandStatus.Done;
        if (alreadyCompleted)
        {
            return;
        }
        leased.Complete(command.LeaseId, clock.GetUtcNow());
        await repository.SaveChangesAsync(ct);
    }
}
