using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation. Two different laws live on this one aggregate and this file is split to match:
//   - Intent  (RequestX)   : a REQUEST from a person. Illegal from the wrong state -> throws.
//   - Observation (MarkX)  : a REPORT of what the provider is. Cannot be "illegal" — if it disagrees
//                            with Titan, Titan's copy is stale. Never throws for transition legality.
// The one invariant the observation path does carry: Deleted is absorbing
// (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
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

    private static Vm NewVm() => Vm.CreateUnchecked(
        Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm VmIn(Seed seed)
    {
        var vm = NewVm();
        if (seed == Seed.Creating)
        {
            return vm;
        }
        if (seed == Seed.CreateFailed)
        {
            vm.MarkRequestFailed("VM provisioning failed due to a system error.", "raw error");
            return vm;
        }

        vm.MarkCreated("42");
        vm.MarkObserved(VmState.Running, "10.0.0.5");
        switch (seed)
        {
            case Seed.Running: break;
            case Seed.Ready: vm.MarkObserved(VmState.Ready, "10.0.0.5"); break;
            case Seed.Stopped: vm.MarkObserved(VmState.Stopped, null); break;
            case Seed.Frozen: vm.MarkObserved(VmState.Frozen, "10.0.0.5"); break;
            case Seed.Error: vm.MarkObserved(VmState.Error, null); break;
            case Seed.NotFound: vm.MarkNotFound(); break;
            case Seed.Deleted: vm.RequestDelete(); vm.MarkDeleted(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        Assert.Null(vm.PendingIntent); // the seeder itself must be honest: nothing left waiting
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
        Assert.Equal(VmIntent.Create, vm.PendingIntent);
        Assert.Equal(VmTransition.Creating, vm.Transition);
    }

    // ---------- the create ends once, whether or not a guest ever ran ----------

    // A VM whose create ended in Error, then was stopped: never ran, yet a Start is a start. Reading "never ran" as
    // "the create waits" showed Creating, recorded vm.created, and left an abandoned start nobody could clear.
    [Fact]
    public void Start_OfAVmThatNeverRan_AfterItsCreateEnded_IsAStart_NotACreate()
    {
        var vm = NewVm();
        vm.MarkCreated("42");
        vm.MarkObserved(VmState.Error, null);
        vm.RequestStop();
        vm.MarkObserved(VmState.Stopped, null);
        Assert.False(vm.HasBeenRunning);

        VmIntentTicket start = vm.RequestStart();

        Assert.Equal(VmIntent.Start, vm.PendingIntent);
        Assert.Equal(VmTransition.Starting, vm.Transition);
        Assert.Equal(VmIntent.Start, vm.MarkObserved(VmState.Running, "10.0.0.5"));
        Assert.NotEqual(Guid.Empty, start.IntentId);
    }

    [Fact]
    public void AbandonedStart_OfAVmThatNeverRan_FreesTheVm()
    {
        var vm = NewVm();
        vm.MarkCreated("42");
        vm.MarkNotFound();
        vm.MarkObserved(VmState.Stopped, null);
        VmIntentTicket start = vm.RequestStart();

        Assert.Equal(VmIntent.Start, vm.AbandonRequest(start.IntentId));
        Assert.Null(vm.PendingIntent);
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
    }

    [Fact] // a guest paused during its first boot has not finished the create; Running afterwards does
    public void Frozen_DuringTheCreate_ThenRunning_FulfilsTheCreate()
    {
        var vm = NewVm();
        vm.MarkCreated("42");

        Assert.Null(vm.MarkObserved(VmState.Frozen, "10.0.0.5"));
        Assert.Equal(VmIntent.Create, vm.PendingIntent);
        Assert.Equal(VmIntent.Create, vm.MarkObserved(VmState.Running, "10.0.0.5"));
    }

    [Fact] // the id arrives before any status: the instance exists, its status is still the reconciler's to say
    public void Created_RecordsTheIdAndThatTheInstanceExists_ButNoStatus()
    {
        var vm = NewVm();

        vm.MarkCreated("42");

        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Equal(VmIntent.Create, vm.PendingIntent);
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

        vm.MarkObserved(incus, "10.0.0.7");

        Assert.Equal(incus, vm.CurrentState);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
    }

    [Theory] // Ready is Running with a guest that said it is ready: both finish the create
    [InlineData(VmState.Running)]
    [InlineData(VmState.Ready)]
    public void FirstRunningOrReady_FulfilsTheCreate_NotAStart(VmState incus)
    {
        var vm = NewVm();
        vm.MarkCreated("42");

        VmIntent? fulfilled = vm.MarkObserved(incus, "10.0.0.5");

        Assert.Equal(VmIntent.Create, fulfilled);
        Assert.Null(vm.PendingIntent);
        Assert.True(vm.HasBeenRunning);
    }

    [Fact] // QEMU reports Stopped while its start operation holds the instance lock: that is not the end of the create
    public void Stopped_WhileTheCreateWaits_FulfilsNothing_AndTheCreateKeepsWaiting()
    {
        var vm = NewVm();
        vm.MarkCreated("42");

        VmIntent? fulfilled = vm.MarkObserved(VmState.Stopped, null);

        Assert.Null(fulfilled);
        Assert.Equal(VmIntent.Create, vm.PendingIntent);
        Assert.False(vm.HasBeenRunning);
    }

    [Fact] // the callback used to refuse Running without an address; Incus says Running, so it is
    public void Running_WithoutAnIpv4_IsRunning_WithNoAddress()
    {
        var vm = NewVm();
        vm.MarkCreated("42");

        VmIntent? fulfilled = vm.MarkObserved(VmState.Running, null);

        Assert.Equal(VmIntent.Create, fulfilled);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Null(vm.IpAddress);
    }

    [Fact] // the reconciler reports every change; only the one that ends a waiting request is an outcome
    public void RepeatedRunning_FulfilsNothing()
    {
        var vm = VmIn(Seed.Running);

        Assert.Null(vm.MarkObserved(VmState.Running, "10.0.0.5"));
    }

    [Theory]
    [InlineData(VmState.Running)]
    [InlineData(VmState.Ready)]
    public void RunningOrReady_FulfilsAWaitingStart(VmState incus)
    {
        var vm = VmIn(Seed.Stopped);
        vm.RequestStart();

        Assert.Equal(VmIntent.Start, vm.MarkObserved(incus, "10.0.0.5"));
        Assert.Null(vm.PendingIntent);
    }

    [Fact] // the poll before the forced stop lands still reads Running — that does not end the stop
    public void Running_DoesNotEndAWaitingStop()
    {
        var vm = VmIn(Seed.Running);
        var stop = vm.RequestStop();

        Assert.Null(vm.MarkObserved(VmState.Running, "10.0.0.5"));
        Assert.Equal(VmIntent.Stop, vm.PendingIntent);
        Assert.Equal(stop.IntentId, vm.PendingIntentId);
        Assert.Equal(VmTransition.Stopping, vm.Transition);
    }

    [Fact]
    public void Stopped_FulfilsAWaitingStop()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestStop();

        Assert.Equal(VmIntent.Stop, vm.MarkObserved(VmState.Stopped, null));
        Assert.Null(vm.PendingIntent);
    }

    [Fact] // a stop asked of a Frozen VM is done when Incus shows it Stopped
    public void Stopped_FulfilsAStopAskedOfAFrozenVm()
    {
        var vm = VmIn(Seed.Frozen);
        vm.RequestStop();

        Assert.Equal(VmIntent.Stop, vm.MarkObserved(VmState.Stopped, null));
    }

    [Fact] // a guest-OS shutdown is not the outcome of anyone's request: drift
    public void GuestShutdown_IsDrift_NotAWaitingStart()
    {
        var vm = VmIn(Seed.Running);

        VmIntent? fulfilled = vm.MarkObserved(VmState.Stopped, null);

        Assert.Null(fulfilled);
        Assert.Equal(VmState.Stopped, vm.CurrentState);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Null(vm.Transition);
    }

    [Fact] // pausing outside Titan is drift too, and the VM keeps the address it had
    public void Frozen_OutsideTitan_IsDrift_AndKeepsWaitingNothing()
    {
        var vm = VmIn(Seed.Running);

        VmIntent? fulfilled = vm.MarkObserved(VmState.Frozen, "10.0.0.5");

        Assert.Null(fulfilled);
        Assert.Equal(VmState.Frozen, vm.CurrentState);
        Assert.Null(vm.Transition);
    }

    [Fact] // the stop cannot complete from Error; the person may ask again
    public void Error_DropsAWaitingStop()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestStop();

        vm.MarkObserved(VmState.Error, null);

        Assert.Null(vm.PendingIntent);
    }

    [Fact] // a create that lands in Error is over, with no outcome
    public void Error_DropsAWaitingCreate()
    {
        var vm = NewVm();
        vm.MarkCreated("42");

        Assert.Null(vm.MarkObserved(VmState.Error, null));
        Assert.Null(vm.PendingIntent);
        Assert.False(vm.HasBeenRunning);
    }

    [Fact] // an observation never withdraws a waiting delete
    public void Error_KeepsAWaitingDelete()
    {
        var vm = VmIn(Seed.Running);
        var delete = vm.RequestDelete();

        vm.MarkObserved(VmState.Error, null);

        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
        Assert.Equal(delete.IntentId, vm.PendingIntentId);
    }

    [Theory] // a delete is ended only by the delete command, never by a status
    [InlineData(VmState.Running)]
    [InlineData(VmState.Ready)]
    [InlineData(VmState.Stopped)]
    public void AStatusWhileADeleteWaits_KeepsTheDelete(VmState incus)
    {
        var vm = VmIn(Seed.Running);
        vm.RequestDelete();

        VmIntent? fulfilled = vm.MarkObserved(incus, "10.0.0.5");

        Assert.Null(fulfilled);
        Assert.Equal(VmTransition.Deleting, vm.Transition);
    }

    [Fact] // Error is not terminal: the instance can come back
    public void Error_ThenRunning_Recovers()
    {
        var vm = VmIn(Seed.Error);

        vm.MarkObserved(VmState.Running, "10.0.0.5");

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

    [Fact]
    public void NotFound_KeepsAWaitingDelete_AndDropsAWaitingStop()
    {
        var deleting = VmIn(Seed.Running);
        deleting.RequestDelete();
        var stopping = VmIn(Seed.Running);
        stopping.RequestStop();

        deleting.MarkNotFound();
        stopping.MarkNotFound();

        Assert.Equal(VmIntent.Delete, deleting.PendingIntent);
        Assert.Null(stopping.PendingIntent);
    }

    [Fact] // found again (the cluster came back): exists, with its status
    public void NotFound_ThenSeenAgain_Exists()
    {
        var vm = VmIn(Seed.NotFound);

        vm.MarkObserved(VmState.Stopped, null);

        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal(VmState.Stopped, vm.CurrentState);
    }

    [Fact]
    public void Deleted_FulfilsAWaitingDelete_AndClearsTheStatus()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestDelete();

        VmIntent? fulfilled = vm.MarkDeleted().Fulfilled;
        VmIntent? second = vm.MarkDeleted().Fulfilled;

        Assert.Equal(VmIntent.Delete, fulfilled);
        Assert.Null(second);
        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Null(vm.IpAddress);
        Assert.Null(vm.PendingIntent);
    }

    [Fact] // the delete of a VM in Error or not found: the command's report is the only one, and it ends the delete
    public void Deleted_AfterADeleteOfANotFoundVm_FulfilsTheDelete()
    {
        var vm = VmIn(Seed.NotFound);
        vm.RequestDelete();

        Assert.Equal(VmIntent.Delete, vm.MarkDeleted().Fulfilled);
    }

    [Fact] // deleted outside a request while a stop waited: the stop can never happen, nobody asked for a delete
    public void Deleted_DropsAnyOtherIntent_WithoutFulfillingIt()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestStop();

        Assert.Null(vm.MarkDeleted().Fulfilled);
        Assert.Null(vm.PendingIntent);
    }

    // A money test, not a tidiness test: ComputeMeterService bills a VM whose instance exists and runs. A stale
    // poll flipping a deleted VM back to Running bills a machine that no longer exists.
    [Fact]
    public void Deleted_IgnoresEveryLaterObservation_AndNeverThrows()
    {
        foreach (var mutate in new Action<Vm>[]
        {
            v => v.MarkObserved(VmState.Running, "10.0.0.9"),
            v => v.MarkObserved(VmState.Error, null),
            v => v.MarkNotFound(),
            v => v.MarkCreated("9"),
            v => v.MarkRequestFailed("late", "late detail"),
            v => v.MarkDeleted()
        })
        {
            var vm = VmIn(Seed.Deleted);
            mutate(vm);
            Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
            Assert.Null(vm.CurrentState);
            Assert.Null(vm.RequestFailureReason);
        }
    }

    [Fact]
    public void IsTerminal_IsTrueOnlyForDeleted()
    {
        Assert.True(VmIn(Seed.Deleted).IsTerminal);
        foreach (var seed in Enum.GetValues<Seed>().Where(s => s != Seed.Deleted))
            Assert.False(VmIn(seed).IsTerminal);
    }

    // ---------- Observation: a create the provider refused ----------

    [Fact] // the reason the tenant reads, the raw error for the admin, and the create is over
    public void RequestFailed_RecordsTheFailure_EndsTheCreate_AndLeavesTheInstanceNotCreated()
    {
        var vm = NewVm();

        vm.MarkRequestFailed("VM provisioning failed due to a system error.", "raw error");

        Assert.Equal("VM provisioning failed due to a system error.", vm.RequestFailureReason);
        Assert.Equal("raw error", vm.RequestFailureDetail);
        Assert.NotNull(vm.RequestFailedAt);
        Assert.Null(vm.PendingIntent);
        Assert.Equal(CurrentExistence.NotCreated, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
    }

    [Fact] // a late give-up report must not withdraw a delete asked since
    public void RequestFailed_KeepsAWaitingDelete()
    {
        var vm = NewVm();
        vm.MarkCreated("42");
        vm.RequestDelete();

        vm.MarkRequestFailed("late", null);

        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
    }

    // A give-up from an overlapping lease after the guest already ran is stale: the create succeeded, so no failure
    // text appears under a running VM.
    [Fact]
    public void RequestFailed_AfterTheVmRan_RecordsNoFailure()
    {
        Vm vm = VmIn(Seed.Running);

        vm.MarkRequestFailed("late", "raw");

        Assert.Null(vm.RequestFailureReason);
        Assert.Null(vm.RequestFailedAt);
        Assert.Equal(VmState.Running, vm.CurrentState);
    }

    // A guest paused during its first boot has run but its create still waits: a give-up then ends the create, so
    // the VM is not left Creating with Stop and Start refused.
    [Fact]
    public void RequestFailed_WhileTheCreateWaitsOnAFrozenGuest_EndsTheCreate()
    {
        var vm = NewVm();
        vm.MarkCreated("42");
        vm.MarkObserved(VmState.Frozen, "10.0.0.5");

        vm.MarkRequestFailed("refused", "raw");

        Assert.Null(vm.PendingIntent);
        Assert.True(vm.CreateFinished);
        Assert.Equal("refused", vm.RequestFailureReason);
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
        vm.RequestStop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ShellInstanceName()).Code);
    }

    [Fact]
    public void ShellInstanceName_WhileADeleteIsWaiting_IsRefused()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestDelete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ShellInstanceName()).Code);
    }

    // ---------- Intent: stop / start follow what Incus allows ----------

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    public void RequestStop_FromEveryStatusIncusStops_RecordsTheIntent_AndLeavesStatusToTheReconciler(Seed from)
    {
        var vm = VmIn(from);
        VmState? before = vm.CurrentState;

        var ticket = vm.RequestStop();

        Assert.Equal(VmIntentOutcome.Dispatch, ticket.Outcome);
        Assert.Equal("42", ticket.ProviderId);
        Assert.Equal(VmIntent.Stop, vm.PendingIntent);
        Assert.Equal(vm.PendingIntentId, ticket.IntentId);
        Assert.Equal(before, vm.CurrentState); // single-writer: the CP never writes CurrentState
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
        Assert.Equal(VmTransition.Stopping, vm.Transition);
    }

    [Fact]
    public void RequestStop_OnAStoppedVm_IsRefused()
    {
        var vm = VmIn(Seed.Stopped);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStop()).Code);
        Assert.Null(vm.PendingIntent);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void RequestStop_WithNoIncusStatus_Throws_AndRecordsNothing(Seed from)
    {
        var vm = VmIn(from);
        var before = vm.PendingIntent;

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStop()).Code);
        Assert.Equal(before, vm.PendingIntent);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
    }

    [Fact]
    public void RequestStop_Twice_IsRefused_AndKeepsTheFirstIntent()
    {
        var vm = VmIn(Seed.Running);
        var first = vm.RequestStop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStop()).Code);
        Assert.Equal(first.IntentId, vm.PendingIntentId);
    }

    [Theory] // Start on a Frozen instance is a resume (titan-ops features/vm/design.md#requests-follow-incus)
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    public void RequestStart_FromStoppedOrFrozen_IsAccepted(Seed from)
    {
        var vm = VmIn(from);
        VmState? before = vm.CurrentState;

        var ticket = vm.RequestStart();

        Assert.Equal(VmIntentOutcome.Dispatch, ticket.Outcome);
        Assert.Equal("42", ticket.ProviderId);
        Assert.Equal(VmIntent.Start, vm.PendingIntent);
        Assert.Equal(before, vm.CurrentState);
    }

    [Theory]
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    public void RequestStart_OnARunningVm_IsRefused(Seed from)
    {
        var vm = VmIn(from);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStart()).Code);
        Assert.Null(vm.PendingIntent);
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

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStart()).Code);
        Assert.Equal(before, vm.CurrentState);
    }

    [Fact]
    public void RequestStart_Twice_IsRefused_AndKeepsTheFirstIntent()
    {
        var vm = VmIn(Seed.Stopped);
        var first = vm.RequestStart();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStart()).Code);
        Assert.Equal(first.IntentId, vm.PendingIntentId);
    }

    [Fact]
    public void RequestStart_WhileAStopIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Running);
        var stop = vm.RequestStop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStart()).Code);
        Assert.Equal(stop.IntentId, vm.PendingIntentId);
    }

    [Fact] // the VM reads Stopped, so a stop would be "already done" — but a start waits
    public void RequestStop_WhileAStartIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Stopped);
        vm.RequestStart();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStop()).Code);
    }

    [Fact]
    public void StopOrStart_WhileADeleteIsWaiting_Throws()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestDelete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStop()).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestStart()).Code);
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
    }

    [Fact] // a guest-OS shutdown leaves nothing waiting, so a real start is not mistaken for a repeat
    public void StartAfterGuestShutdown_IsANewRequest()
    {
        var vm = VmIn(Seed.Running);
        vm.MarkObserved(VmState.Stopped, null);

        VmIntentTicket ticket = vm.RequestStart();

        Assert.Equal(VmTransition.Starting, vm.Transition);
        Assert.Equal(ticket.IntentId, vm.PendingIntentId);
    }

    // ---------- Intent: delete ----------

    [Theory] // the worker force-stops whatever Incus reports, then deletes; an instance already gone is deleted
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    [InlineData(Seed.Error)]
    [InlineData(Seed.NotFound)]
    public void RequestDelete_WithAProviderId_FromAnyStatus_Dispatches_AndLeavesTheCurrentFieldsAlone(Seed from)
    {
        var vm = VmIn(from);
        VmState? beforeState = vm.CurrentState;
        CurrentExistence beforeExistence = vm.CurrentExistence;

        var ticket = vm.RequestDelete();

        Assert.Equal(VmIntentOutcome.Dispatch, ticket.Outcome);
        Assert.Equal("42", ticket.ProviderId);
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
        Assert.Equal(DesiredExistence.Deleted, vm.DesiredExistence);
        Assert.Equal(beforeState, vm.CurrentState);
        Assert.Equal(beforeExistence, vm.CurrentExistence);
        Assert.Equal(VmTransition.Deleting, vm.Transition);
    }

    [Fact] // the provider holds an instance it never ran: its delete reaches the provider
    public void RequestDelete_WhileTheCreateWaitsWithAnId_Dispatches()
    {
        var vm = NewVm();
        vm.MarkCreated("42");

        var ticket = vm.RequestDelete();

        Assert.Equal(VmIntentOutcome.Dispatch, ticket.Outcome);
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
    }

    [Fact] // without an id the create may still instantiate: nothing is safe to forget
    public void RequestDelete_WhileTheCreateWaitsWithoutAnId_IsRefused()
    {
        var vm = NewVm();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);
        Assert.Equal(VmIntent.Create, vm.PendingIntent);
    }

    [Fact] // a create that gave up before Incus held anything: the caller finishes it
    public void RequestDelete_OfAFailedCreate_AsksTheCallerToFinishIt_ThenDeleteLocallyEndsIt()
    {
        var vm = VmIn(Seed.CreateFailed);

        var ticket = vm.RequestDelete();

        Assert.Equal(VmIntentOutcome.FinishLocally, ticket.Outcome);
        Assert.Equal(CurrentExistence.NotCreated, vm.CurrentExistence); // nothing mutated until the caller says so

        vm.DeleteLocally();

        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, vm.DesiredExistence);
        Assert.Null(vm.Transition);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);
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
    public void RequestDelete_Twice_IsRefused_AndKeepsTheFirstIntent()
    {
        var vm = VmIn(Seed.Running);
        var first = vm.RequestDelete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);
        Assert.Equal(first.IntentId, vm.PendingIntentId);
    }

    [Fact]
    public void RequestDelete_OnADeletedVm_IsRefused()
    {
        var vm = VmIn(Seed.Deleted);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);
    }

    [Fact] // a delete overrides a waiting stop, as a new intent (delete wins)
    public void RequestDelete_WhileAStopIsWaiting_ReplacesIt()
    {
        var vm = VmIn(Seed.Running);
        var stop = vm.RequestStop();

        var delete = vm.RequestDelete();

        Assert.NotEqual(stop.IntentId, delete.IntentId);
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
    }

    // ---------- ResendWaitingIntent: the handler's retry path (titan-ops features/vm/design.md#resend-waiting-intent) ----------

    [Theory]
    [InlineData(VmIntent.Stop)]
    [InlineData(VmIntent.Delete)]
    public void ResendWaitingIntent_ReturnsTheWaitingIntent_AndChangesNothing(VmIntent intent)
    {
        var vm = VmIn(Seed.Running);
        var first = intent == VmIntent.Stop ? vm.RequestStop() : vm.RequestDelete();

        var again = vm.ResendWaitingIntent(intent);

        Assert.Equal(first.IntentId, again.IntentId);
        Assert.Equal("42", again.ProviderId);
        Assert.Equal(intent, vm.PendingIntent);
        Assert.Equal(VmState.Running, vm.CurrentState);
    }

    [Fact]
    public void ResendWaitingIntent_ForAnotherIntent_IsRefused()
    {
        var vm = VmIn(Seed.Running);
        vm.RequestStop();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ResendWaitingIntent(VmIntent.Delete)).Code);
        Assert.Equal(VmIntent.Stop, vm.PendingIntent);
    }

    [Fact]
    public void ResendWaitingIntent_WithNothingWaiting_IsRefused()
    {
        var vm = VmIn(Seed.Running);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.ResendWaitingIntent(VmIntent.Stop)).Code);
    }

    // ---------- snapshot and attach follow what Incus allows ----------

    [Theory] // titan-ops features/vm/design.md#requests-follow-incus
    [InlineData(Seed.Running)]
    [InlineData(Seed.Ready)]
    [InlineData(Seed.Stopped)]
    [InlineData(Seed.Frozen)]
    public void SnapshotAndAttach_FromEveryStatusIncusAllows_HandOutTheId(Seed from)
    {
        Assert.Equal("42", VmIn(from).BeginSnapshot());
        Assert.Equal("42", VmIn(from).BeginVolumeAttach());
    }

    [Theory]
    [InlineData(Seed.Error)]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void SnapshotAndAttach_FromAnythingElse_Throw(Seed from)
    {
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => VmIn(from).BeginSnapshot()).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => VmIn(from).BeginVolumeAttach()).Code);
    }

    [Theory] // a stop or delete waiting races any other command against this VM
    [InlineData(VmIntent.Stop)]
    [InlineData(VmIntent.Delete)]
    public void SnapshotAndAttach_WhileAStopOrDeleteIsWaiting_Throw(VmIntent waiting)
    {
        var vm = VmIn(Seed.Running);
        if (waiting == VmIntent.Stop) vm.RequestStop(); else vm.RequestDelete();

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.BeginSnapshot()).Code);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.BeginVolumeAttach()).Code);
    }

    [Fact] // a waiting START does not block: the VM is Stopped, and attach is legal from Stopped
    public void BeginVolumeAttach_WhileAStartIsWaiting_IsAllowed()
    {
        var vm = VmIn(Seed.Stopped);
        vm.RequestStart();

        Assert.Equal("42", vm.BeginVolumeAttach());
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

    // ---------- a stop/start the provider refused ----------

    [Fact]
    public void AbandonRequest_OnTheWaitingStop_ClearsIt_AndPutsDesiredBack()
    {
        Vm vm = VmIn(Seed.Running);
        VmIntentTicket stop = vm.RequestStop();

        VmIntent? failed = vm.AbandonRequest(stop.IntentId);

        Assert.Equal(VmIntent.Stop, failed);
        Assert.Null(vm.PendingIntent);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Null(vm.Transition);
    }

    [Fact]
    public void AbandonRequest_OnTheWaitingStart_PutsDesiredBackToStopped()
    {
        Vm vm = VmIn(Seed.Stopped);
        VmIntentTicket start = vm.RequestStart();

        Assert.Equal(VmIntent.Start, vm.AbandonRequest(start.IntentId));
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
    }

    [Fact] // a resume that failed leaves a Frozen VM wanting what it wanted before the pause
    public void AbandonRequest_OnAStartOfAFrozenVm_PutsDesiredBackToRunning()
    {
        Vm vm = VmIn(Seed.Frozen);
        VmIntentTicket start = vm.RequestStart();

        Assert.Equal(VmIntent.Start, vm.AbandonRequest(start.IntentId));
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
    }

    [Fact]
    public void AfterAFailedStop_AnotherStopIsANewRequest()
    {
        Vm vm = VmIn(Seed.Running);
        VmIntentTicket stop = vm.RequestStop();
        vm.AbandonRequest(stop.IntentId);

        VmIntentTicket again = vm.RequestStop();

        Assert.NotEqual(stop.IntentId, again.IntentId);
    }

    [Fact]
    public void AbandonRequest_ForAnotherIntentId_ChangesNothing()
    {
        Vm vm = VmIn(Seed.Running);
        VmIntentTicket stop = vm.RequestStop();

        Assert.Null(vm.AbandonRequest(Guid.NewGuid()));
        Assert.Equal(stop.IntentId, vm.PendingIntentId);
    }

    [Fact]
    public void AbandonRequest_Twice_ReturnsTheIntentOnlyOnce()
    {
        Vm vm = VmIn(Seed.Running);
        VmIntentTicket stop = vm.RequestStop();

        vm.AbandonRequest(stop.IntentId);

        Assert.Null(vm.AbandonRequest(stop.IntentId));
    }

    [Fact] // a delete keeps waiting until the provider shows the VM gone
    public void AbandonRequest_OnAWaitingDelete_ChangesNothing()
    {
        Vm vm = VmIn(Seed.Running);
        VmIntentTicket delete = vm.RequestDelete();

        Assert.Null(vm.AbandonRequest(delete.IntentId));
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
    }

    [Fact] // the stop command giving up after a delete replaced it names the OLD id
    public void AbandonForAStopReplacedByADelete_ChangesNothing()
    {
        var vm = VmIn(Seed.Running);
        VmIntentTicket stop = vm.RequestStop();
        vm.RequestDelete();

        Assert.Null(vm.AbandonRequest(stop.IntentId));
        Assert.Equal(DesiredExistence.Deleted, vm.DesiredExistence);
        Assert.Equal(VmTransition.Deleting, vm.Transition);
    }

    [Fact]
    public void AbandonRequest_OnADeletedVm_ChangesNothing()
    {
        Vm vm = VmIn(Seed.Deleted);

        Assert.Null(vm.AbandonRequest(Guid.NewGuid()));
        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
    }
}
