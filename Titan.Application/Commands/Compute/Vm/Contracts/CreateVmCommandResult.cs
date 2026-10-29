namespace Titan.Application.Commands.Compute.Vm.Contracts;

// Only the id (titan-ops architecture/http-contract.md#create-returns-id): the identity it just minted is the one
// thing the caller could not have known.
public record CreateVmCommandResult(Guid VmId);
