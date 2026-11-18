using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    // ---- Key additions: adding SSH keys to a running VM at once, through Incus's file API
    // (titan-ops features/vm/design.md#ssh-key). The VM's own status never changes for it
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmKeyAddition; this is the way in.

    private readonly List<VmKeyAddition> _keyAdditions = [];
    public IReadOnlyCollection<VmKeyAddition> KeyAdditions => _keyAdditions;

    /// Incus writes a key through its file API only to a running instance. The VM's status does not change for it,
    /// unlike every other intent on this file.
    private void EnsureCanAddKeys()
    {
        if (Status != VmStatus.Running)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running to add keys.");
        }
    }

    /// Two additions waiting at once would race the worker's pull-append-push of one file on the instance and could
    /// lose a key: only one may be Adding at a time. The VM's status does not say this (it is untouched by AddKeys),
    /// so the check reads the additions themselves.
    private void EnsureNoKeyAdditionWaiting()
    {
        bool oneIsWaiting = _keyAdditions.Any(a => a.Status == VmKeyAdditionStatus.Adding);
        if (oneIsWaiting)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another key addition for this VM is still in progress.");
        }
    }

    /// Intent: add a set of keys to this VM, with the ids the handler chose: the addition's and its operation's. The
    /// VM must be Running (EnsureCanAddKeys) with no other addition waiting (EnsureNoKeyAdditionWaiting); its own
    /// status is untouched. The operation's command writes to the instance whose name this hands back
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    public string AddKeys(Guid keyAdditionId, Guid operationId, List<Guid> sshKeyIds)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        EnsureCanAddKeys();
        EnsureNoKeyAdditionWaiting();
        EnsureOnTheProvider();
        VmKeyAddition addition = VmKeyAddition.Create(keyAdditionId, Id, TenantId, sshKeyIds, operationId);
        _keyAdditions.Add(addition);
        return ExternalId!;
    }

    /// A key addition this VM does not have answers like a missing one, the same 404 one of another VM gets.
    private VmKeyAddition GetKeyAddition(Guid keyAdditionId)
    {
        foreach (VmKeyAddition addition in _keyAdditions)
        {
            if (addition.Id == keyAdditionId)
            {
                return addition;
            }
        }
        throw new NotFoundException("VM_KEY_ADDITION_NOT_FOUND", "Key addition not found.");
    }

    /// The report of the addition's own operation (titan-ops features/vm/design.md#ssh-key); each is false when it
    /// names another operation than the one the addition waits on.
    public bool KeyAdditionSucceeded(Guid keyAdditionId, Guid operationId, bool restartNeeded) =>
        GetKeyAddition(keyAdditionId).Succeeded(operationId, restartNeeded);

    public bool KeyAdditionFailed(Guid keyAdditionId, Guid operationId, string? reason, string? detail) =>
        GetKeyAddition(keyAdditionId).Failed(operationId, reason, detail);

    /// The VM's delete takes every key addition still Adding with it: nothing will ever report on it again
    /// (titan-ops architecture/resource-lifecycle.md#shape).
    private void EndKeyAdditionsWithTheVm()
    {
        foreach (VmKeyAddition addition in _keyAdditions)
        {
            addition.EndWithItsVm();
        }
    }
}
