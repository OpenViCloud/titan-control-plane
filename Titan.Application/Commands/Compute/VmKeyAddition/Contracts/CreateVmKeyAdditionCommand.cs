namespace Titan.Application.Commands.Compute.VmKeyAddition.Contracts;

// {vmId} from the route, SshKeyIds from the body — the endpoint composes both (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateVmKeyAdditionCommand(Guid VmId, List<Guid> SshKeyIds);
