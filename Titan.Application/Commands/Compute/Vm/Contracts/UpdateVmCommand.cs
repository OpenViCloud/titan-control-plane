namespace Titan.Application.Commands.Compute.Vm.Contracts;

// Carries the route id as well as the body. FastEndpoints binds the body to UpdateVmBody and the endpoint fills VmId
// from the route. Each field absent leaves that property as it is (titan-ops features/vm/design.md#vm-stop-timeout).
public record UpdateVmCommand(Guid VmId, string? Name = null, int? StopTimeoutSeconds = null);
