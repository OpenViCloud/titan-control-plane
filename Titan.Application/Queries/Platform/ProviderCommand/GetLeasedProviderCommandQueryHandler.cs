using System.Text.Json;
using Titan.Application.Queries.Platform.ProviderCommand.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Queries.Platform.ProviderCommand;

// The read half of a lease (titan-ops architecture/provider-and-worker.md#provider-command-lease): 404 is "this lease holds
// nothing", the normal answer when no work was due.
public class GetLeasedProviderCommandQueryHandler(IProviderCommandRepository repository)
{
    public async Task<LeasedProviderCommandResult> HandleAsync(GetLeasedProviderCommandQuery query, CancellationToken ct = default)
    {
        ProviderCommandAggregate? leased = await repository.FindByLeaseAsync(query.LeaseId, ct);
        if (leased == null)
        {
            throw NothingLeased(query.LeaseId);
        }
        bool leaseHoldsWork = leased.Status == ProviderCommandStatus.Leased;
        if (!leaseHoldsWork)
        {
            throw NothingLeased(query.LeaseId);
        }
        DateTimeOffset? leaseUntil = leased.LeaseUntil;
        if (leaseUntil == null)
        {
            throw NothingLeased(query.LeaseId);
        }

        using JsonDocument payload = JsonDocument.Parse(leased.Payload);
        return new LeasedProviderCommandResult(
            leased.Id,
            leased.Key,
            leased.Kind,
            leased.ClusterId,
            payload.RootElement.Clone(),
            leased.Attempts,
            ProviderCommandAggregate.MaxAttempts,
            leaseUntil.Value);
    }

    private static NotFoundException NothingLeased(Guid leaseId) =>
        new("PROVIDER_COMMAND_LEASE_NOT_FOUND", $"Lease {leaseId} holds no provider command.");
}
