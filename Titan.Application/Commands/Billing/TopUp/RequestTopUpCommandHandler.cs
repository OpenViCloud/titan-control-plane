using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.TopUp.Contracts;
using Titan.Application.Options;
using Titan.Domain.Repositories;
using TopUpAggregate = Titan.Domain.Aggregates.TopUp.TopUp;

namespace Titan.Application.Commands.Billing.TopUp;

/// A Pending TopUp with a generated PaymentRef the customer pays against. ConfirmTopUp or the SePay
/// webhook credits the wallet later.
public class RequestTopUpCommandHandler(ICreditRepository repo, ICurrentUser currentUser, CreditOptions options)
{
    public async Task<RequestTopUpCommandResult> HandleAsync(RequestTopUpCommand command, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        // VND is whole đồng (titan-ops architecture/money-and-wallet.md#money-is-long-vnd); the wire keeps decimal, so convert here.
        var topUp = TopUpAggregate.Create(tenantId, (long)command.Amount, options.Currency);
        repo.AddTopUp(topUp);
        await repo.SaveChangesAsync(ct);
        return new RequestTopUpCommandResult(topUp.Id);
    }
}
