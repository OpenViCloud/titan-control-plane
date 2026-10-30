using Titan.Application.Commands.Platform.ProviderCommand.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Commands.Platform.ProviderCommand;

// The worker asks for work under a lease id it minted, then reads what it got with GetLeasedProviderCommandQuery
// (titan-ops architecture/provider-and-worker.md#provider-command-lease). Nothing claimable, or another worker won the
// same row: the lease stays empty and the worker asks again on its next tick.
public class LeaseProviderCommandCommandHandler(IProviderCommandRepository repository, TimeProvider clock)
{
    private const int Candidates = 1;

    public async Task HandleAsync(LeaseProviderCommandCommand command, CancellationToken ct = default)
    {
        ProviderCommandAggregate? alreadyLeased = await repository.FindByLeaseAsync(command.LeaseId, ct);
        bool leaseAlreadyTaken = alreadyLeased != null;
        if (leaseAlreadyTaken)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<ProviderCommandAggregate> claimable =
            await repository.ListClaimableAsync(command.Kinds, now, Candidates, ct);
        if (claimable.Count == 0)
        {
            return;
        }

        claimable[0].Lease(command.LeaseId, now);
        try
        {
            await repository.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Another worker leased this row first; this lease stays empty.
        }
    }
}
