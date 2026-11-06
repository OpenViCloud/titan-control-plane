using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    /// The VM half of Volume.Attach. It runs BEFORE the cluster check there, so a VM in the wrong state
    /// answers VM_INVALID_STATUS even when the clusters also differ. An allowlist: a new status is refused.
    public void EnsureVolumeCanAttach()
    {
        if (!IncusCanAttach())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to attach a volume.");
    }

    /// A load balancer of this VM's network may send to it: the instance is on Incus and its delete was not pressed.
    /// Any Incus state will do — the health check takes a stopped VM out of rotation
    /// (titan-ops features/load-balancer/design.md#lb-and-vm-network). The worker reads the MAC from the instance
    /// ExternalId names (titan-ops features/load-balancer/design.md#backend-address).
    public void EnsureLoadBalancerCanSendTo(Guid networkId)
    {
        bool instanceIsThere = Status == VmStatus.Running
                               || Status == VmStatus.Ready
                               || Status == VmStatus.Stopped
                               || Status == VmStatus.Frozen
                               || Status == VmStatus.Error
                               || Status == VmStatus.Starting
                               || Status == VmStatus.Stopping
                               || Status == VmStatus.Snapshotting;
        bool usable = NetworkId == networkId && instanceIsThere && ExternalId != null;
        if (!usable)
            throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID",
                $"VM '{Id}' must be on Incus, on the load balancer's network, and not being deleted.");
    }

    /// Incus attaches a volume to an instance that is Running, Ready, Stopped or Frozen
    /// (titan-ops features/vm/design.md#operations-follow-incus); Error is not offered. A waiting start still leaves the
    /// instance Stopped on Incus, so Starting is accepted; a waiting stop, delete or snapshot would race the command
    /// (titan-ops features/vm/design.md#conflict-and-delete-wins).
    private bool IncusCanAttach() =>
        Status == VmStatus.Running
        || Status == VmStatus.Ready
        || Status == VmStatus.Stopped
        || Status == VmStatus.Frozen
        || Status == VmStatus.Starting;

    /// A snapshot or revert takes the same statuses, except Starting: the Start's report would find the VM Snapshotting
    /// and be ignored, so the Start would never end (titan-ops features/vm/design.md#vm-snapshot).
    private bool IncusCanSnapshot() =>
        Status == VmStatus.Running
        || Status == VmStatus.Ready
        || Status == VmStatus.Stopped
        || Status == VmStatus.Frozen;

    /// A shell session execs inside the guest, which needs its agent, which runs only while Incus reports Running
    /// or Ready (titan-ops features/vm/design.md#ssh-session). A waiting stop or delete would cut the session, and its
    /// status is then Stopping or Deleting; Snapshotting is refused too: only Running and Ready pass.
    public void EnsureShellCanOpen()
    {
        bool guestRuns = Status == VmStatus.Running || Status == VmStatus.Ready;
        if (!guestRuns)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running or Ready to open an SSH session.");
        }
    }
}
