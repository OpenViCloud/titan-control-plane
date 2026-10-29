namespace Titan.Application.Commands.Billing.TopUp.Contracts;

// The body of POST /v1/tenant/topups (titan-ops architecture/code-structure.md#command-is-wire-contract).
// Amount stays decimal on the wire (titan-ops architecture/money-and-wallet.md#money-is-long-vnd); the handler converts to whole VND.
public record RequestTopUpCommand(decimal Amount);
