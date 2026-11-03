namespace Titan.Application.Commands.Networking.Network.Contracts;

// The reconciler's observation of one network (titan-ops architecture/resource-lifecycle.md#callback-routes): whether Incus
// has it and, when it does, its status word for word. NetworkId comes from the route.
public record ObserveNetworkCommand(Guid NetworkId, bool Exists, string? Status = null);
