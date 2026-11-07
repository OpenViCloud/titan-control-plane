namespace Titan.Domain.Aggregates.Vm;

/// The one status of a VM, the only one the API shows (titan-ops features/vm/design.md#vm-status). Five say an operation
/// is waiting; five are the Incus status the VM rests in (Vm.IsIncusStatus, the only ones Vm.InstanceStatus can be); the
/// last four say what is not a resting instance.
public enum VmStatus
{
    Creating,
    Starting,
    Stopping,
    Deleting,
    // A snapshot create or revert is running on the instance; only its command's report moves the status on.
    Snapshotting,
    Running,
    Ready,
    Stopped,
    Frozen,
    Error,
    // The operation ended without the worker reading the instance; the next observation writes the real status.
    Unknown,
    // The reconciler did not find the instance; never Deleted, which only the delete command's Done writes.
    NotFound,
    // The create gave up before Incus held an instance.
    Failed,
    Deleted
}
