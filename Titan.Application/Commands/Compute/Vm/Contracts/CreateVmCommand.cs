namespace Titan.Application.Commands.Compute.Vm.Contracts;

// The wire contract and the command in one type (titan-ops architecture/code-structure.md#command-is-wire-contract).
// FastEndpoints binds the HTTP body straight to this and CreateVmValidator validates it, so it appears in
// the OpenAPI document under this name.
public record CreateVmCommand(
    string Name,
    Guid NetworkId,
    string VmOfferingId,
    string TemplateId,
    // Absent: the network's cluster; another one than the network's is refused
    // (titan-ops features/catalog/design.md#cluster-is-a-real-choice).
    string? ClusterId,
    int? DiskSizeGb,
    string? UserData = null,
    // Saved keys of the caller's tenant that sshd accepts for the `ubuntu` user, written once at the VM's first boot
    // (titan-ops features/vm/design.md#ssh-key).
    List<Guid>? SshKeyIds = null);
