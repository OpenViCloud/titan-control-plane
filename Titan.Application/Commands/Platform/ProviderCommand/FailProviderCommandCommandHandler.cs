using Titan.Application.Commands.Platform.ProviderCommand.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Application.Services.Platform.ProviderCommand;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Commands.Platform.ProviderCommand;

public class FailProviderCommandCommandHandler(IProviderCommandRepository repository, TimeProvider clock)
{
    public async Task HandleAsync(FailProviderCommandCommand command, CancellationToken ct = default)
    {
        ProviderCommandAggregate leased = await ProviderCommandLease.RequireAsync(repository, command.LeaseId, ct);
        // The row still names this lease but is no longer Leased: the first report of this failure landed.
        bool alreadyReported = leased.Status != ProviderCommandStatus.Leased;
        if (alreadyReported)
        {
            return;
        }
        leased.Fail(command.LeaseId, command.Error, command.Retryable, clock.GetUtcNow());
        await repository.SaveChangesAsync(ct);
    }
}
