namespace Titan.Application.Commands.Billing.TopUp.Contracts;

// titan-ops architecture/http-contract.md#create-returns-id: the grant mints a TopUp, so it answers with that id and nothing more.
public record GrantTopUpCommandResult(Guid TopUpId);
