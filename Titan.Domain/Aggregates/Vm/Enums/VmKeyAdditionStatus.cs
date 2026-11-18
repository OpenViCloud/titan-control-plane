namespace Titan.Domain.Aggregates.Vm;

/// The one status of a key addition: adding a set of SSH keys to a running VM at once, through Incus's file API
/// (titan-ops features/vm/design.md#ssh-key). One operation, so there is no resting status: it is Adding until the
/// operation reports, then Added or Failed for good.
public enum VmKeyAdditionStatus
{
    Adding,
    Added,
    Failed
}
