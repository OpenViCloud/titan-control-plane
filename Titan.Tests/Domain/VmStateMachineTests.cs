using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation. Two different laws live on this one aggregate and this file is split to match:
//   - Intent  (RequestX)   : a REQUEST from a person. Illegal from the wrong state -> throws.
//   - Observation (MarkX)  : a REPORT of what the provider is. Cannot be "illegal" — if it disagrees
//                            with Titan, Titan's copy is stale. Never throws for transition legality.
// How a request ENDS — by its own command's report, never by an observation — is VmRequestLifecycleTests
// (titan-ops architecture/resource-lifecycle.md#invariants). The one invariant the observation path does carry:
// Deleted is absorbing (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
//
// CurrentState is the Incus status word for word, null while Incus has no instance; whether the instance
// exists is CurrentExistence (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim). What a
// request may do from each status is what Incus allows there (titan-ops features/vm/design.md#requests-follow-incus).
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

    private static VmObservation Seen(VmState state, string? ip = "10.0.0.5") => new(state, ip, null);

    private static Vm VmIn(Seed seed)
    {
        var vm = NewVm();
        if (seed == Seed.Creating)
        {
            return vm;
        }
        if (seed == Seed.CreateFailed)
        {
            vm.EndOperation(vm.Pending!.Id, VmOperationEnd.Failed(null, null, "VM provisioning failed due to a system error.", "raw error"));
            return vm;
        }

        vm.MarkRunning("42", "10.0.0.5");
        switch (seed)
        {
            case Seed.Running: break;
            case Seed.Ready: vm.MarkObserved(Seen(VmState.Ready)); break;
            case Seed.Stopped: vm.MarkObserved(Seen(VmState.Stopped, null)); break;
            case Seed.Frozen: vm.MarkObserved(Seen(VmState.Frozen)); break;
            case Seed.Error: vm.MarkObserved(Seen(VmState.Error, null)); break;
            case Seed.NotFound: vm.MarkNotFound(); break;
            case Seed.Deleted:
                OperationTicket delete = vm.Delete()!;
                vm.EndOperation(delete.OperationId, VmOperationEnd.Done(null, null));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        Assert.Null(vm.Pending); // the seeder itself must be honest: nothing left waiting
        return vm;
    }

    // ---------- a new VM ----------

    [Fact]
    public void ANewVm_HasNoIncusStatus_IsNotCreated_AndIsWaitingForItsCreate()
    {
        var vm = NewVm();

        Assert.Null(vm.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, vm.CurrentExistence);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Equal(DesiredExistence.Exists, vm.DesiredExistence);
        Assert.Equal(VmOperationKind.Create, vm.Pending!.Kind);
        Assert.Equal(VmTransition.Creating, vm.Transition);
    }

    // A VM whose create ended with the instance in Error, then was stopped: never ran, yet a Start is a start. Reading
    // "never ran" as "the create waits" once showed Creating, recorded vm.created, and left an abandoned start nobody
    // could clear.
    [Fact]
    public void Start_OfAVmThatNeverRan_AfterItsCreateEnded_IsAStart_NotACreate()
    {
        var vm = NewVm();
        vm.EndOperation(vm.Pending!.Id, VmOperationEnd.Done("42", Seen(VmState.Error, null)));
        OperationTicket stop = vm.Stop();
        vm.EndOperation(stop.OperationId, VmOperationEnd.Done(null, Seen(VmState.Stopped, null)));

        OperationTicket start = vm.Start();

        Assert.Equal(VmOperationKind.Start, vm.Pending!.Kind);
        Assert.Equal(VmTransition.Starting, vm.Transition);
        Assert.Equal(start.OperationId, vm.Pending.Id);
    }

    // ---------- Observation: the Incus status, verbatim ----------

    [Theory]
    [InlineData(VmState.Running)]
    [InlineData(VmState.Ready)]
    [InlineData(VmState.Stopped)]
    [InlineData(VmState.Frozen)]
    [InlineData(VmState.Error)]
    public void Observed_RecordsTheIncusStatusAsIs(VmState incus)
    {
        var vm = VmIn(Seed.Running);

        vm.MarkObserved(Seen(incus, "10.0.0.7"));

        Assert.Equal(incus, vm.CurrentState);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal("10.0.0.7", vm.IpAddress);
    }

    [Fact] // the callback used to refuse Running without an address; Incus says Running, so it is
    public void Running_WithoutAnIpv4_IsRunning_WithNoAddress()
    {
        var vm = VmIn(Seed.Stopped);

        vm.MarkObserved(Seen(VmState.Running, null));

        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Null(vm.IpAddress);
    }

    [Fact] // pausing outside Titan is drift, and the VM keeps the address it had
    public void Frozen_OutsideTitan_IsDrift_AndKeepsWaitingNothing()
    {
        var vm = VmIn(Seed.Running);

        vm.MarkObserved(Seen(VmState.Frozen));

        Assert.Equal(VmState.Frozen, vm.CurrentState);
        Assert.Null(vm.Pending);
        Assert.Null(vm.Transition);
    }

    [Fact] // Error is not terminal: the instance can come back
    public void Error_ThenRunning_Recovers()
    {
        var vm = VmIn(Seed.Error);

        vm.MarkObserved(Seen(VmState.Running));

        Assert.Equal(VmState.Running, vm.CurrentState);
    }

    // ---------- Observation: not found, deleted ----------

    [Fact] // absent is never Deleted: pointing at the wrong cluster also finds nothing
    public void NotFound_ClearsTheStatusAndAddress_ButNotTheId()
    {
        var vm = VmIn(Seed.Running);

        vm.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Null(vm.IpAddress);
        Assert.Equal("42", vm.ExternalId);
        Assert.False(vm.IsTerminal);
    }

    [Fact] // found again (the cluster came back): exists, with its status
    public void NotFound_ThenSeenAgain_Exists()
    {
        var vm = VmIn(Seed.NotFound);

        vm.MarkObserved(Seen(VmState.Stopped, null));

        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal(VmState.Stopped, vm.CurrentState);
    }

    // A money test, not a tidiness test: ComputeMeterService bills a VM whose instance exists and runs. A stale
    // poll flipping a deleted VM back to Running bills a machine that no longer exists.
    [Fact]
    public void Deleted_IgnoresEveryLaterObservationAndReport_AndNeverThrows()
    {
        foreach (var mutate in new Action<Vm>[]
        {
            v => v.MarkObserved(Seen(VmState.Running, "10.0.0.9")),
            v => v.MarkObserved(Seen(VmState.Error, null)),
            v => v.MarkNotFound(),
            v => v.EndOperation(Guid.NewGuid(), VmOperationEnd.Done("9", Seen(VmState.Running))),
            v => v.EndOperation(Guid.NewGuid(), VmOperationEnd.Failed(null, null, "late", "late detail")),
        })
        {
            var vm = VmIn(Seed.Deleted);
            mutate(vm);
            Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
            Assert.Null(vm.CurrentState);
            Assert.Null(vm.OperationFailureReason);
        }
    }

    [Fact]
    public void IsTerminal_IsTrueOnlyForDeleted()
    {
        Assert.True(VmIn(Seed.Deleted).IsTerminal);
        foreach (var seed in Enum.GetValues<Seed>().Where(s => s != Seed.Deleted))
            Assert.False(VmIn(seed).IsTerminal);
    }

    // ---------- Shell session: only a running guest has an agent to exec in ----------

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    public void ShellInstanceName_OfARunningVm_IsItsInstance(Seed from)
    {
        var vm = VmIn(from);

        Assert.Equal("42", vm.ShellInstanceName());
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.Deleted)]
    public void ShellInstanceName_OfAVmThatIsNotRunning_IsRefused(Seed from)
    {
        var vm = VmIn(from);

        var refused = Assert.Throws<BusinessException>(() => vm.ShellInstanceName());
        Assert.Equal("VM_INVALID_STATUS", refused.Code);
    }

    [Fact]
    public void ShellInstanceName_WhileAStopIsWaiting_IsRefused()
    {
        var vm = VmIn(Seed.Running);
        vm.Stop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ShellInstanceName()).Code);
    }

    [Fact]
    public void ShellInstanceName_WhileADeleteIsWaiting_IsRefused()
    {
        var vm = VmIn(Seed.Running);
        vm.Delete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ShellInstanceName()).Code);
    }

    // ---------- Intent: stop / start follow what Incus allows ----------

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    public void RequestStop_FromEveryStatusIncusStops_RecordsTheRequest_AndLeavesStatusToTheReconciler(Seed from)
    {
        var vm = VmIn(from);
        VmState? before = vm.CurrentState;

        OperationTicket ticket = vm.Stop();

        Assert.Equal("42", ticket.ProviderId);
        Assert.Equal(VmOperationKind.Stop, vm.Pending!.Kind);
        Assert.Equal(vm.Pending.Id, ticket.OperationId);
        Assert.Equal(before, vm.CurrentState); // Stop does not write CurrentState
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
        Assert.Equal(VmTransition.Stopping, vm.Transition);
    }

    [Fact]
    public void RequestStop_OnAStoppedVm_IsRefused()
    {
        var vm = VmIn(Seed.Stopped);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop()).Code);
        Assert.Null(vm.Pending);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void RequestStop_WithNoIncusStatus_Throws_AndRecordsNothing(Seed from)
    {
        var vm = VmIn(from);
        var before = vm.Pending;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop()).Code);
        Assert.Equal(before, vm.Pending);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
    }

    [Fact]
    public void RequestStop_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var vm = VmIn(Seed.Running);
        var first = vm.Stop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop()).Code);
        Assert.Equal(first.OperationId, vm.Pending!.Id);
    }

    [Theory] // Start on a Frozen instance is a resume (titan-ops features/vm/design.md#requests-follow-incus)
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    public void RequestStart_FromStoppedOrFrozen_IsAccepted(Seed from)
    {
        var vm = VmIn(from);
        VmState? before = vm.CurrentState;

        OperationTicket ticket = vm.Start();

        Assert.Equal("42", ticket.ProviderId);
        Assert.Equal(VmOperationKind.Start, vm.Pending!.Kind);
        Assert.Equal(before, vm.CurrentState);
    }

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    public void RequestStart_OnARunningVm_IsRefused(Seed from)
    {
        var vm = VmIn(from);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start()).Code);
        Assert.Null(vm.Pending);
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
        VmState? before = vm.CurrentState;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start()).Code);
        Assert.Equal(before, vm.CurrentState);
    }

    [Fact]
    public void RequestStart_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var vm = VmIn(Seed.Stopped);
        var first = vm.Start();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start()).Code);
        Assert.Equal(first.OperationId, vm.Pending!.Id);
    }

    [Fact]
    public void RequestStart_WhileAStopIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Running);
        var stop = vm.Stop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start()).Code);
        Assert.Equal(stop.OperationId, vm.Pending!.Id);
    }

    [Fact] // the VM reads Stopped, so a stop would be "already done" — but a start waits
    public void RequestStop_WhileAStartIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Stopped);
        vm.Start();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop()).Code);
    }

    [Fact]
    public void StopOrStart_WhileADeleteIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Running);
        vm.Delete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Stop()).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Start()).Code);
        Assert.Equal(VmOperationKind.Delete, vm.Pending!.Kind);
    }

    [Fact] // a guest-OS shutdown leaves nothing waiting, so a real start is not mistaken for a repeat
    public void StartAfterGuestShutdown_IsANewRequest()
    {
        var vm = VmIn(Seed.Running);
        vm.MarkObserved(Seen(VmState.Stopped, null));

        OperationTicket ticket = vm.Start();

        Assert.Equal(VmTransition.Starting, vm.Transition);
        Assert.Equal(ticket.OperationId, vm.Pending!.Id);
    }

    // ---------- Intent: delete ----------

    [Theory] // the worker force-stops whatever Incus reports, then deletes; an instance already gone is deleted
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    [InlineData(Seed.NotFound)]
    public void RequestDelete_WithAProviderId_FromAnyStatus_HandsOutATicket_AndLeavesTheCurrentFieldsAlone(Seed from)
    {
        var vm = VmIn(from);
        VmState? beforeState = vm.CurrentState;
        CurrentExistence beforeExistence = vm.CurrentExistence;

        OperationTicket? ticket = vm.Delete();

        Assert.NotNull(ticket);
        Assert.Equal("42", ticket!.ProviderId);
        Assert.Equal(VmOperationKind.Delete, vm.Pending!.Kind);
        Assert.Equal(DesiredExistence.Deleted, vm.DesiredExistence);
        Assert.Equal(beforeState, vm.CurrentState);
        Assert.Equal(beforeExistence, vm.CurrentExistence);
        Assert.Equal(VmTransition.Deleting, vm.Transition);
    }

    [Fact] // the provider holds an instance whose create failed after it was made: its delete reaches the provider
    public void RequestDelete_OfACreateThatFailedWithAnId_HandsOutATicket()
    {
        var vm = NewVm();
        vm.EndOperation(vm.Pending!.Id, VmOperationEnd.Failed("42", null, "VM provisioning failed.", "raw"));

        OperationTicket? ticket = vm.Delete();

        Assert.NotNull(ticket);
        Assert.Equal("42", ticket!.ProviderId);
        Assert.Equal(VmOperationKind.Delete, vm.Pending!.Kind);
    }

    [Fact] // without an id the create may still instantiate: nothing is safe to forget
    public void RequestDelete_WhileTheCreateWaitsWithoutAnId_IsRefused()
    {
        var vm = NewVm();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete()).Code);
        Assert.Equal(VmOperationKind.Create, vm.Pending!.Kind);
    }

    [Fact] // a create that gave up before Incus held anything: no ticket, the caller finishes it locally
    public void RequestDelete_OfAFailedCreate_HandsOutNoTicket_ThenDeleteLocallyEndsIt()
    {
        var vm = VmIn(Seed.CreateFailed);

        OperationTicket? ticket = vm.Delete();

        Assert.Null(ticket);
        Assert.Equal(CurrentExistence.NotCreated, vm.CurrentExistence); // nothing mutated until the caller says so

        vm.DeleteLocally();

        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, vm.DesiredExistence);
        Assert.Null(vm.Transition);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete()).Code);
    }

    [Fact] // a VM with a provider id still exists there; deleting it here would stop billing a running machine
    public void DeleteLocally_OfAVmWithAProviderId_IsRefused_AndChangesNothing()
    {
        foreach (var seed in new[] { Seed.Running, Seed.Error, Seed.NotFound })
        {
            var vm = VmIn(seed);
            CurrentExistence before = vm.CurrentExistence;

            Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.DeleteLocally()).Code);
            Assert.Equal(before, vm.CurrentExistence);
            Assert.Equal(DesiredExistence.Exists, vm.DesiredExistence);
        }
    }

    [Fact]
    public void DeleteLocally_WhileTheCreateWaitsWithoutAnId_IsRefused()
    {
        var vm = NewVm();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.DeleteLocally()).Code);
        Assert.Equal(CurrentExistence.NotCreated, vm.CurrentExistence);
    }

    [Fact]
    public void RequestDelete_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var vm = VmIn(Seed.Running);
        var first = vm.Delete()!;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete()).Code);
        Assert.Equal(first.OperationId, vm.Pending!.Id);
    }

    [Fact]
    public void RequestDelete_OnADeletedVm_IsRefused()
    {
        var vm = VmIn(Seed.Deleted);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete()).Code);
    }

    [Fact] // a delete overrides a waiting stop, as a new request (delete wins)
    public void RequestDelete_WhileAStopIsWaiting_ReplacesIt()
    {
        var vm = VmIn(Seed.Running);
        var stop = vm.Stop();

        var delete = vm.Delete()!;

        Assert.NotEqual(stop.OperationId, delete.OperationId);
        Assert.Equal(VmOperationKind.Delete, vm.Pending!.Kind);
    }

    // ---------- snapshot and attach follow what Incus allows ----------

    [Theory] // titan-ops features/vm/design.md#requests-follow-incus
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    public void SnapshotAndAttach_FromEveryStatusIncusAllows_HandOutTheId(Seed from)
    {
        Assert.Equal("42", VmIn(from).AddSnapshot("s").ExternalVmId);
        Assert.Equal("42", VmIn(from).ProviderIdForVolumeAttach());
    }

    [Theory]
    [InlineData(Seed.Error)]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void SnapshotAndAttach_FromAnythingElse_Throw(Seed from)
    {
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => VmIn(from).AddSnapshot("s")).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => VmIn(from).ProviderIdForVolumeAttach()).Code);
    }

    [Theory] // a stop or delete waiting races any other command against this VM
    [InlineData(VmOperationKind.Stop)]
    [InlineData(VmOperationKind.Delete)]
    public void SnapshotAndAttach_WhileAStopOrDeleteIsWaiting_Throw(VmOperationKind waiting)
    {
        var vm = VmIn(Seed.Running);
        if (waiting == VmOperationKind.Stop) vm.Stop(); else vm.Delete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.AddSnapshot("s")).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ProviderIdForVolumeAttach()).Code);
    }

    [Fact] // a waiting START does not block: the VM is Stopped, and attach is legal from Stopped
    public void ProviderIdForVolumeAttach_WhileAStartIsWaiting_IsAllowed()
    {
        var vm = VmIn(Seed.Stopped);
        vm.Start();

        Assert.Equal("42", vm.ProviderIdForVolumeAttach());
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
