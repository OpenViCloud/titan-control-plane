namespace Titan.Application.Commands.Compute.Vm.Contracts;

// The wire contract and the command in one type (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#command-is-wire-contract).
// FastEndpoints binds the HTTP body straight to this and CreateVmValidator validates it, so it appears in
// the OpenAPI document under this name.
public record CreateVmCommand(
    string Name,
    Guid NetworkId,
    string VmOfferingId,
    string TemplateId,
    string ClusterId,
    int? DiskSizeGb,
    string? UserData = null,
    // Saved keys of the caller's tenant to put into the `ubuntu` user's authorized_keys at first boot
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-key).
    List<Guid>? SshKeyIds = null);
