namespace Titan.Application.Commands.Admin.Vm.Contracts;

// Platform-admin deletes any tenant's VM (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write). The VM id
// is all it needs: the owner is read from the VM, never from the caller.
public record DeleteAdminVmCommand(Guid VmId);
