using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what the
    // provider is, so no guard and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants). While
    // an operation waits the status stays; only the word is recorded. Running seen before a stop finishes is legitimate.

    /// The reconciler saw the instance with this Incus word and these addresses.
    public void MarkObserved(VmStatus word, string? ipAddress, string? publicIpv6Address)
    {
        if (IsForeignStatus(word))
        {
            throw new BusinessException("INVALID_STATUS", $"'{word}' is not an Incus status.");
        }
        if (IsDeleted || Status == VmStatus.Failed) return;
        ObservedState = word;
        IpAddress = ipAddress;
        PublicIpv6Address = publicIpv6Address;
        if (!IsTransitionStatus)
        {
            Status = word;
        }
    }

    /// The reconciler did not find the instance. Never Deleted: pointing at the wrong cluster also finds nothing
    /// (titan-ops architecture/desired-and-current.md#reconciler-never-dies); only the delete command's Done ends a VM.
    public void MarkNotFound()
    {
        if (IsDeleted || Status == VmStatus.Failed) return;
        ObservedState = null;
        IpAddress = null;
        PublicIpv6Address = null;
        if (!IsTransitionStatus)
        {
            Status = VmStatus.NotFound;
        }
    }
}
