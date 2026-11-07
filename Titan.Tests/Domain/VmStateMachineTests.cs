using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation. Two different laws live on this one aggregate and this file is split to match:
//   - Intent  (Stop, Start, Delete): a REQUEST from a person. Illegal from the wrong state -> throws.
//   - Observation (MarkX)  : a REPORT of what the provider is. Cannot be "illegal" — if it disagrees
//                            with Titan, Titan's copy is stale. Never throws for transition legality.
// How a request ENDS — by its own command's report, never by an observation — is VmRequestLifecycleTests
// (titan-ops architecture/resource-lifecycle.md#invariants). The one invariant the observation path does carry:
// Deleted is absorbing (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
//
// Status is the VM's one status (titan-ops features/vm/design.md#vm-status); InstanceStatus is the Incus status the VM
// holds, worked out from Status and the status the reconciler saw while an operation waits, null while none is known
// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim). What an operation may do from each status is
// what Incus allows there (titan-ops features/vm/design.md#requests-follow-incus).
public class VmStateMachineTests
{
    // What a test starts from: an Incus status, or one of the shapes with no status at all.
    public enum Seed
    {
        Creating,        // a new VM: no instance yet, its create waiting
        Running,
        Ready,
        Stopped,
        Frozen,
        Error,
        NotFound,        // the reconciler did not find the instance
        CreateFailed,    // the create command gave up before Incus held anything
        Deleted
    }

    private static Vm NewVm() => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);


    private static Vm VmIn(Seed seed)
    {
        var vm = NewVm();
        if (seed == Seed.Creating)
        {
            return vm;
        }
        if (seed == Seed.CreateFailed)
        {
            vm.CreateFailed(vm.Operation.PendingId!.Value, null, "VM provisioning failed due to a system error.", "raw error");
            return vm;
        }

        vm.MarkRunning("42", "10.0.0.5");
        switch (seed)
        {
            case Seed.Running: break;
            case Seed.Ready: vm.MarkObserved(VmStatus.Ready, "10.0.0.5", null); break;
            case Seed.Stopped: vm.MarkObserved(VmStatus.Stopped, null, null); break;
            case Seed.Frozen: vm.MarkObserved(VmStatus.Frozen, "10.0.0.5", null); break;
            case Seed.Error: vm.MarkObserved(VmStatus.Error, null, null); break;
            case Seed.NotFound: vm.MarkNotFound(); break;
            case Seed.Deleted:
                Guid delete = Guid.NewGuid();
                vm.Delete(delete);
                vm.DeleteSucceeded(delete);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        Assert.Null(vm.Operation.PendingId); // the seeder itself must be honest: nothing left waiting
        return vm;
    }

    // ---------- a new VM ----------

    [Fact]
    public void ANewVm_HasNoIncusWord_IsCreating_AndIsWaitingForItsCreate()
    {
        var vm = NewVm();

        Assert.Null(vm.InstanceStatus);
        Assert.Equal(VmStatus.Creating, vm.Status);
        Assert.NotNull(vm.Operation.PendingId);
        Assert.NotNull(vm.Operation.AskedAt);
    }

    // A VM whose create ended with the instance in Error, then was stopped: never ran, yet a Start is a start. Reading
    // "never ran" as "the create waits" once showed Creating, recorded vm.created, and left an abandoned start nobody
    // could clear.
    [Fact]
    public void Start_OfAVmThatNeverRan_AfterItsCreateEnded_IsAStart_NotACreate()
    {
        var vm = NewVm();
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "42", VmStatus.Error, null, null);
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);
        vm.StopSucceeded(stop, VmStatus.Stopped, null, null);

        Guid start = Guid.NewGuid();
        vm.Start(start);

        Assert.Equal(VmStatus.Starting, vm.Status);
        Assert.Equal(start, vm.Operation.PendingId);
    }

    // ---------- Observation: the Incus status, verbatim ----------

    [Theory]
    [InlineData(VmStatus.Running)]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Stopped)]
    [InlineData(VmStatus.Frozen)]
    [InlineData(VmStatus.Error)]
    public void Observed_RecordsTheIncusStatusAsIs(VmStatus incus)
    {
        var vm = VmIn(Seed.Running);

        vm.MarkObserved(incus, "10.0.0.7", null);

        Assert.Equal(incus, vm.InstanceStatus);
        Assert.Equal(incus, vm.Status);
        Assert.Equal("10.0.0.7", vm.IpAddress);
    }

    [Fact] // the callback used to refuse Running without an address; Incus says Running, so it is
    public void Running_WithoutAnIpv4_IsRunning_WithNoAddress()
    {
        var vm = VmIn(Seed.Stopped);

        vm.MarkObserved(VmStatus.Running, null, null);

        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Null(vm.IpAddress);
    }

    [Fact] // pausing outside Titan: with no request waiting the status simply follows, and the VM keeps its address
    public void Frozen_OutsideTitan_IsFrozen_AndKeepsWaitingNothing()
    {
        var vm = VmIn(Seed.Running);

        vm.MarkObserved(VmStatus.Frozen, "10.0.0.5", null);

        Assert.Equal(VmStatus.Frozen, vm.InstanceStatus);
        Assert.Equal(VmStatus.Frozen, vm.Status);
        Assert.Null(vm.Operation.PendingId);
    }

    [Fact] // Error is not terminal: the instance can come back
    public void Error_ThenRunning_Recovers()
    {
        var vm = VmIn(Seed.Error);

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);

        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(VmStatus.Running, vm.Status);
    }

    // ---------- Observation: not found, deleted ----------

    [Fact] // absent is never Deleted: pointing at the wrong cluster also finds nothing
    public void NotFound_IsNotFound_ClearsTheIncusWordAndAddress_ButNotTheId()
    {
        var vm = VmIn(Seed.Running);

        vm.MarkNotFound();

        Assert.Equal(VmStatus.NotFound, vm.Status);
        Assert.Null(vm.InstanceStatus);
        Assert.Null(vm.IpAddress);
        Assert.Equal("42", vm.ExternalId);
        Assert.False(vm.IsDeleted);
    }

    [Fact] // found again (the cluster came back): the status is what Incus says
    public void NotFound_ThenSeenAgain_TakesItsIncusStatus()
    {
        var vm = VmIn(Seed.NotFound);

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
    }

    // A money test, not a tidiness test: ComputeMeterService bills a VM whose instance exists and runs. A stale
    // poll flipping a deleted VM back to Running bills a machine that no longer exists.
    [Fact]
    public void Deleted_IgnoresEveryLaterObservationAndReport_AndNeverThrows()
    {
        foreach (var mutate in new Action<Vm>[]
        {
            v => v.MarkObserved(VmStatus.Running, "10.0.0.9", null),
            v => v.MarkObserved(VmStatus.Error, null, null),
            v => v.MarkNotFound(),
            v => v.CreateSucceeded(Guid.NewGuid(), "9", VmStatus.Running, "10.0.0.5", null),
            v => v.CreateFailed(Guid.NewGuid(), null, "late", "late detail"),
        })
        {
            var vm = VmIn(Seed.Deleted);
            mutate(vm);
            Assert.Equal(VmStatus.Deleted, vm.Status);
            Assert.Null(vm.InstanceStatus);
            Assert.Null(vm.Operation.FailureReason);
        }
    }

    [Fact]
    public void IsDeleted_IsTrueOnlyForDeleted()
    {
        Assert.True(VmIn(Seed.Deleted).IsDeleted);
        foreach (var seed in Enum.GetValues<Seed>().Where(s => s != Seed.Deleted))
            Assert.False(VmIn(seed).IsDeleted);
    }

    // ---------- Shell session: only a running guest has an agent to exec in ----------

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    public void EnsureShellCanOpen_OfARunningVm_Passes_AndItsInstanceIsNamed(Seed from)
    {
        var vm = VmIn(from);

        vm.EnsureShellCanOpen();

        Assert.Equal("42", vm.ExternalId);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.Deleted)]
    public void EnsureShellCanOpen_OfAVmThatIsNotRunning_IsRefused(Seed from)
    {
        var vm = VmIn(from);

        var refused = Assert.Throws<BusinessException>(() => vm.EnsureShellCanOpen());
        Assert.Equal("VM_INVALID_STATUS", refused.Code);
    }

    [Fact]
    public void EnsureShellCanOpen_WhileAStopIsWaiting_IsRefused()
    {
        var vm = VmIn(Seed.Running);
        vm.Stop(Guid.NewGuid());

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.EnsureShellCanOpen()).Code);
    }

    [Fact]
    public void EnsureShellCanOpen_WhileADeleteIsWaiting_IsRefused()
    {
        var vm = VmIn(Seed.Running);
        vm.Delete(Guid.NewGuid());

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.EnsureShellCanOpen()).Code);
    }

    // ---------- Intent: stop / start follow what Incus allows ----------

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    public void RequestStop_FromEveryStatusIncusStops_IsStopping_AndLeavesTheIncusWordToTheReconciler(Seed from)
    {
        var vm = VmIn(from);
        VmStatus? before = vm.InstanceStatus;

        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(stop, vm.Operation.PendingId);
        Assert.Equal(before, vm.InstanceStatus); // Stop does not write the Incus status
    }

    [Fact]
    public void RequestStop_OnAStoppedVm_IsRefused()
    {
        var vm = VmIn(Seed.Stopped);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid())).Code);
        Assert.Null(vm.Operation.PendingId);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void RequestStop_WithNoIncusStatus_Throws_AndRecordsNothing(Seed from)
    {
        var vm = VmIn(from);
        Guid? beforeRequest = vm.Operation.PendingId;
        VmStatus beforeStatus = vm.Status;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid())).Code);
        Assert.Equal(beforeRequest, vm.Operation.PendingId);
        Assert.Equal(beforeStatus, vm.Status);
    }

    [Fact]
    public void RequestStop_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var vm = VmIn(Seed.Running);
        Guid first = Guid.NewGuid();
        vm.Stop(first);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid())).Code);
        Assert.Equal(first, vm.Operation.PendingId);
    }

    [Theory] // Start on a Frozen instance is a resume (titan-ops features/vm/design.md#requests-follow-incus)
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    public void RequestStart_FromStoppedOrFrozen_IsAccepted(Seed from)
    {
        var vm = VmIn(from);
        VmStatus? before = vm.InstanceStatus;

        Guid start = Guid.NewGuid();
        vm.Start(start);

        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(VmStatus.Starting, vm.Status);
        Assert.Equal(start, vm.Operation.PendingId);
        Assert.Equal(before, vm.InstanceStatus);
    }

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    public void RequestStart_OnARunningVm_IsRefused(Seed from)
    {
        var vm = VmIn(from);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid())).Code);
        Assert.Null(vm.Operation.PendingId);
    }

    [Theory] // Error is offered stop and delete only
    [InlineData(Seed.Error)]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void RequestStart_FromAnythingElse_Throws(Seed from)
    {
        var vm = VmIn(from);
        VmStatus? before = vm.InstanceStatus;
        VmStatus beforeStatus = vm.Status;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid())).Code);
        Assert.Equal(before, vm.InstanceStatus);
        Assert.Equal(beforeStatus, vm.Status);
    }

    [Fact]
    public void RequestStart_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var vm = VmIn(Seed.Stopped);
        Guid first = Guid.NewGuid();
        vm.Start(first);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid())).Code);
        Assert.Equal(first, vm.Operation.PendingId);
    }

    [Fact]
    public void RequestStart_WhileAStopIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Running);
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid())).Code);
        Assert.Equal(stop, vm.Operation.PendingId);
    }

    [Fact] // Incus still reads Stopped, so a stop would be "already done" — but a start waits
    public void RequestStop_WhileAStartIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Stopped);
        vm.Start(Guid.NewGuid());

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid())).Code);
    }

    [Fact]
    public void StopOrStart_WhileADeleteIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Running);
        vm.Delete(Guid.NewGuid());

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid())).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid())).Code);
        Assert.Equal(VmStatus.Deleting, vm.Status);
    }

    [Fact] // a guest-OS shutdown leaves nothing waiting, so a real start is not mistaken for a repeat
    public void StartAfterGuestShutdown_IsANewRequest()
    {
        var vm = VmIn(Seed.Running);
        vm.MarkObserved(VmStatus.Stopped, null, null);

        Guid start = Guid.NewGuid();
        vm.Start(start);

        Assert.Equal(VmStatus.Starting, vm.Status);
        Assert.Equal(start, vm.Operation.PendingId);
    }

    // ---------- Intent: delete ----------

    [Theory] // the worker force-stops whatever Incus reports, then deletes; an instance already gone is deleted
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    [InlineData(Seed.NotFound)]
    public void RequestDelete_WithAProviderId_FromAnyStatus_IsDeleting_AndLeavesTheIncusWordAlone(Seed from)
    {
        var vm = VmIn(from);
        VmStatus? beforeState = vm.InstanceStatus;
        Guid delete = Guid.NewGuid();

        vm.Delete(delete);

        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Equal(delete, vm.Operation.PendingId);
        Assert.Equal(beforeState, vm.InstanceStatus);
        Assert.Equal("42", vm.ExternalId);
    }

    [Fact] // the provider holds an instance whose create failed after it was made: its delete reaches the provider
    public void RequestDelete_OfACreateThatFailedWithAnId_ReachesTheProvider()
    {
        var vm = NewVm();
        vm.CreateFailed(vm.Operation.PendingId!.Value, "42", "VM provisioning failed.", "raw");
        Guid delete = Guid.NewGuid();

        vm.Delete(delete);

        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Equal(delete, vm.Operation.PendingId);
    }

    [Fact] // without an id the create may still instantiate: nothing is safe to forget
    public void RequestDelete_WhileTheCreateWaitsWithoutAnId_IsRefused()
    {
        var vm = NewVm();
        Guid? create = vm.Operation.PendingId;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
        Assert.Equal(VmStatus.Creating, vm.Status);
        Assert.Equal(create, vm.Operation.PendingId);
    }

    [Fact] // a create that gave up before Incus held anything: no command to send, so it is Deleted here at once
    public void RequestDelete_OfAFailedCreate_IsDeletedAtOnce()
    {
        var vm = VmIn(Seed.CreateFailed);

        vm.Delete(Guid.NewGuid());

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.True(vm.IsDeleted);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
    }

    [Fact] // a VM with a provider id still exists there; deleting it here would stop billing a running machine
    public void RequestDelete_OfAVmWithAProviderId_IsNeverDeletedAtOnce_AndKeepsTheIncusWord()
    {
        foreach (var seed in new[] { Seed.Running, Seed.Error, Seed.NotFound })
        {
            var vm = VmIn(seed);
            VmStatus? before = vm.InstanceStatus;
            bool billedBefore = vm.IsBilled;

            vm.Delete(Guid.NewGuid());

            Assert.Equal(VmStatus.Deleting, vm.Status);
            Assert.Equal(before, vm.InstanceStatus);
            Assert.False(vm.IsDeleted);
            Assert.Equal("42", vm.ExternalId);
            Assert.Equal(billedBefore, vm.IsBilled); // billing follows Incus until the delete's Done
        }
    }

    [Fact]
    public void RequestDelete_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var vm = VmIn(Seed.Running);
        Guid first = Guid.NewGuid();
        vm.Delete(first);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
        Assert.Equal(first, vm.Operation.PendingId);
    }

    [Fact]
    public void RequestDelete_OnADeletedVm_IsRefused()
    {
        var vm = VmIn(Seed.Deleted);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
    }

    // ---------- snapshot and attach follow what Incus allows ----------

    [Theory] // titan-ops features/vm/design.md#requests-follow-incus
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    public void SnapshotAndAttach_FromEveryStatusIncusAllows_AreAccepted(Seed from)
    {
        Vm snapshotted = VmIn(from);
        Guid snapshotId = Guid.NewGuid();
        snapshotted.AddSnapshot(snapshotId, Guid.NewGuid(), "s");
        Assert.Equal(snapshotId, Assert.Single(snapshotted.Snapshots).Id);
        Assert.Equal("42", snapshotted.ExternalId);

        Vm attached = VmIn(from);
        attached.EnsureVolumeCanAttach();
        Assert.Equal("42", attached.ExternalId);
    }

    [Theory]
    [InlineData(Seed.Error)]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void SnapshotAndAttach_FromAnythingElse_Throw(Seed from)
    {
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => VmIn(from).AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s")).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => VmIn(from).EnsureVolumeCanAttach()).Code);
    }

    [Theory] // a stop or delete waiting races any other command against this VM
    [InlineData(VmStatus.Stopping)]
    [InlineData(VmStatus.Deleting)]
    public void SnapshotAndAttach_WhileAStopOrDeleteIsWaiting_Throw(VmStatus waiting)
    {
        var vm = VmIn(Seed.Running);
        if (waiting == VmStatus.Stopping)
        {
            vm.Stop(Guid.NewGuid());
        }
        else
        {
            vm.Delete(Guid.NewGuid());
        }

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s")).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.EnsureVolumeCanAttach()).Code);
        Assert.Empty(vm.Snapshots);
    }

    [Fact] // a waiting START does not block: Incus still has the instance Stopped, and attach is legal from Stopped
    public void EnsureVolumeCanAttach_WhileAStartIsWaiting_IsAllowed()
    {
        var vm = VmIn(Seed.Stopped);
        vm.Start(Guid.NewGuid());

        vm.EnsureVolumeCanAttach();

        Assert.Equal("42", vm.ExternalId);
    }

    [Fact] // the refusal names every status the guard accepts, so a caller can read what to wait for
    public void EnsureVolumeCanAttach_RefusedFromError_NamesEveryStatusItAccepts()
    {
        var thrown = Assert.Throws<BusinessException>(() => VmIn(Seed.Error).EnsureVolumeCanAttach());

        foreach (VmStatus accepted in new[] { VmStatus.Running, VmStatus.Ready, VmStatus.Stopped, VmStatus.Frozen, VmStatus.Starting })
        {
            Assert.Contains(accepted.ToString(), thrown.Message);
        }
    }

    // ---------- Intent: rename ----------

    [Fact] // asserting the name did NOT change is the half that matters: a guard that throws after mutating is no guard
    public void Rename_OfADeletedVm_Throws_AndLeavesTheNameAlone()
    {
        var vm = VmIn(Seed.Deleted);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Rename("renamed")).Code);
        Assert.Equal("vm-1", vm.Name);
    }

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.CreateFailed)]
    public void Rename_OfAVmNotDeleted_ChangesTheName(Seed from)
    {
        var vm = VmIn(from);
        vm.Rename("renamed");
        Assert.Equal("renamed", vm.Name);
    }
}
