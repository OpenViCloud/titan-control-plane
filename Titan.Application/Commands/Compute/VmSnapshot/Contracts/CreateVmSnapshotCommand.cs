namespace Titan.Application.Commands.Compute.VmSnapshot.Contracts;

// {vmId} from the route, Name from the body — the endpoint composes both (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateVmSnapshotCommand(Guid VmId, string Name);
