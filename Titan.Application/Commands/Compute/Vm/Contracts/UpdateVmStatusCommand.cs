namespace Titan.Application.Commands.Compute.Vm.Contracts;

// The worker's callback payload. An OBSERVATION of what the provider already did rather than an intent
// (titan-ops architecture/intent-and-observation.md#intent-versus-observation) — but it WRITES, so it is a command. VmId comes from the route, the rest from the body.
//
// Report says which of four reports this is (titan-ops features/vm/design.md#status-mapping): Observed (the
// reconciler: Exists, and Status — the Incus status word for word — when it exists), Created and Deleted (the
// create and delete commands), and the create command's give-up (Reason/Detail).
public record UpdateVmStatusCommand(
    Guid VmId,
    string Report,
    bool? Exists = null,
    string? Status = null,
    string? ExternalId = null,
    string? IpAddress = null,
    string? PublicIpv6Address = null,
    string? Reason = null,
    string? Detail = null);
