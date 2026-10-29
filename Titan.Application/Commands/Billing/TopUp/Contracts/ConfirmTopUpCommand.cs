namespace Titan.Application.Commands.Billing.TopUp.Contracts;

// {id} comes from the route, PaymentTxnRef from the body — the endpoint composes both (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record ConfirmTopUpCommand(Guid TopUpId, string? PaymentTxnRef);
