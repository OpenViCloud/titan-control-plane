using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// The VM has one status (titan-ops features/vm/design.md#vm-status). The aggregate only says which move is legal and
// writes the status it lands on; the request id it keeps is compared with the report, nothing more.
public class VmStatusTests
{
    private static Vm NewVm() => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm VmThat(VmStatus incusSays)
    {
        Vm vm = NewVm();
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-1-on-incus", incusSays, "10.0.0.5", null);
        return vm;
    }

    // ---------- requests ----------

    [Fact]
    public void ANewVm_IsCreating_AndWaitsOnItsCreate()
    {
        Vm vm = NewVm();

        Assert.Equal(VmStatus.Creating, vm.Status);
        Assert.NotNull(vm.Operation.PendingId);
        Assert.Null(vm.InstanceStatus);
    }

    [Theory]
    [InlineData(VmStatus.Running)]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Frozen)]
    [InlineData(VmStatus.Error)]
    public void Stop_FromAnIncusStatusThatStops_IsStopping_WithANewRequest(VmStatus incusSays)
    {
        Vm vm = VmThat(incusSays);

        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(stop, vm.Operation.PendingId);
        Assert.Equal("vm-1-on-incus", vm.ExternalId);
    }

    [Fact]
    public void Stop_WhenStopped_IsRefused()
    {
        Vm vm = VmThat(VmStatus.Stopped);

        var error = Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid()));

        Assert.Equal("VM_INVALID_STATUS", error.Code);
        Assert.Equal(VmStatus.Stopped, vm.Status);
    }

    [Theory]
    [InlineData(VmStatus.Stopped)]
    [InlineData(VmStatus.Frozen)]
    public void Start_FromStoppedOrFrozen_IsStarting(VmStatus incusSays)
    {
        Vm vm = VmThat(incusSays);

        vm.Start(Guid.NewGuid());

        Assert.Equal(VmStatus.Starting, vm.Status);
    }

    [Theory]
    [InlineData(VmStatus.Running)]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Error)]
    public void Start_FromAnyOtherIncusStatus_IsRefused(VmStatus incusSays)
    {
        Vm vm = VmThat(incusSays);

        Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid()));
        Assert.Equal(incusSays, vm.Status);
    }

    [Fact]
    public void StopOrStart_WhileARequestWaits_IsRefused()
    {
        Vm vm = VmThat(VmStatus.Running);
        vm.Stop(Guid.NewGuid());

        Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid()));
        Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid()));
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    // An operation that changes the instance's status waits for the one already running: only that operation's own
    // report moves the status on (titan-ops features/vm/design.md#conflict-and-delete-wins).
    [Theory]
    [InlineData(VmStatus.Running)]  // a waiting stop
    [InlineData(VmStatus.Stopped)]  // a waiting start
    public void Delete_WhileAnOperationWaits_IsRefused_AndTheOperationStillEnds(VmStatus resting)
    {
        Vm vm = VmThat(resting);
        Guid waiting = resting == VmStatus.Running ? Stop(vm) : Start(vm);
        VmStatus waitingStatus = vm.Status;

        var refused = Assert.Throws<BusinessException>(() => Delete(vm));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Equal(waitingStatus, vm.Status);
        Assert.Equal(waiting, vm.Operation.PendingId);
        bool ended = resting == VmStatus.Running
            ? vm.StopSucceeded(waiting, VmStatus.Stopped, null, null)
            : vm.StartSucceeded(waiting, VmStatus.Running, null, null);
        Assert.True(ended);
        Assert.Null(vm.Operation.PendingId);
    }

    // The refusal says why: another operation is running, not that the status is wrong for this move.
    [Fact]
    public void Start_WhileAStopWaits_SaysAnotherOperationIsRunning()
    {
        Vm vm = VmThat(VmStatus.Running);
        Stop(vm);

        var refused = Assert.Throws<BusinessException>(() => vm.Start(Guid.NewGuid()));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Equal("Another operation for this VM is still in progress.", refused.Message);
    }

    [Fact]
    public void Stop_WhileAStartWaits_SaysAnotherOperationIsRunning()
    {
        Vm vm = VmThat(VmStatus.Stopped);
        vm.Start(Guid.NewGuid());

        var refused = Assert.Throws<BusinessException>(() => vm.Stop(Guid.NewGuid()));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Equal("Another operation for this VM is still in progress.", refused.Message);
    }

    [Fact]
    public void Delete_WhileTheCreateWaitsWithoutAnInstance_IsRefused()
    {
        Vm vm = NewVm();

        Assert.Throws<BusinessException>(() => Delete(vm));
        Assert.Equal(VmStatus.Creating, vm.Status);
    }

    // ---------- reports ----------

    [Fact]
    public void Done_OfTheStop_WritesWhatIncusSays_AndEndsTheRequest()
    {
        Vm vm = VmThat(VmStatus.Running);
        Guid stop = Stop(vm);

        bool ended = vm.StopSucceeded(stop, VmStatus.Stopped, null, null);

        Assert.True(ended);
        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
        Assert.Null(vm.Operation.PendingId);
    }

    [Fact]
    public void Done_OfTheDelete_ArrivingTwice_EndsItOnce()
    {
        Vm vm = VmThat(VmStatus.Running);
        Guid delete = Delete(vm);

        bool first = vm.DeleteSucceeded(delete);
        bool second = vm.DeleteSucceeded(delete);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.Null(vm.InstanceStatus);
    }

    [Fact]
    public void Done_WithoutAnIncusWord_IsUnknown_AndForgetsTheOldWord()
    {
        Vm vm = VmThat(VmStatus.Stopped);
        Guid start = Guid.NewGuid();
        vm.Start(start);

        vm.StartSucceeded(start, null, null, null);

        Assert.Equal(VmStatus.Unknown, vm.Status);
        Assert.Null(vm.InstanceStatus);
    }

    [Fact]
    public void Done_OfTheCreate_WithoutAnIncusWord_IsUnknown_UntilTheFirstObservation()
    {
        Vm vm = NewVm();
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-1-on-incus", null, null, null);
        Assert.Equal(VmStatus.Unknown, vm.Status);

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);

        Assert.Equal(VmStatus.Running, vm.Status);
    }

    [Fact]
    public void Failed_OfTheStop_GoesBackToTheLastIncusWord_WithTheReason()
    {
        Vm vm = VmThat(VmStatus.Running);
        Guid stop = Stop(vm);

        bool ended = vm.StopFailed(stop, "VM stop failed.", "raw");

        Assert.True(ended);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal("VM stop failed.", vm.Operation.FailureReason);
        Assert.Null(vm.Operation.PendingId);
    }

    [Fact]
    public void Failed_OfTheDelete_GoesBack_AndTheNextDeleteIsANewRequest()
    {
        Vm vm = VmThat(VmStatus.Stopped);
        Guid first = Delete(vm);

        vm.DeleteFailed(first, "VM delete failed.", "raw");
        Guid second = Delete(vm);

        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Failed_OfTheCreate_BeforeAnInstance_IsFailed_AndDeletesHere()
    {
        Vm vm = NewVm();
        vm.CreateFailed(vm.Operation.PendingId!.Value, null, "VM provisioning failed.", "raw");

        Assert.Equal(VmStatus.Failed, vm.Status);
        Assert.False(vm.OccupiesQuota);

        Delete(vm);

        Assert.Equal(VmStatus.Deleted, vm.Status); // nothing on the provider: no command, deleted here
        Assert.Null(vm.Operation.PendingId);
    }

    [Fact]
    public void Failed_OfTheCreate_AfterIncusMadeTheInstance_RestsInTheIncusWord_AndCounts()
    {
        Vm vm = NewVm();
        vm.CreateFailed(vm.Operation.PendingId!.Value, "vm-1-on-incus", "VM provisioning failed.", "raw");
        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.True(vm.OccupiesQuota);
        Guid delete = Delete(vm);
        Assert.Equal(VmStatus.Deleting, vm.Status); // the instance is on the provider: its delete waits on a command
        Assert.Equal(delete, vm.Operation.PendingId);
    }

    // ---------- observations ----------

    [Fact]
    public void AnObservation_AtRest_WritesTheIncusWord()
    {
        Vm vm = VmThat(VmStatus.Running);

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Equal(VmStatus.Stopped, vm.Status);
    }

    [Fact]
    public void AnObservation_WhileARequestWaits_KeepsTheStatus_ButRecordsTheWord()
    {
        Vm vm = VmThat(VmStatus.Running);
        vm.Stop(Guid.NewGuid());

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
        Assert.NotNull(vm.Operation.PendingId);
    }

    [Fact]
    public void NotFound_AtRest_IsNotFound_AndSeeingItAgain_GivesTheWordBack()
    {
        Vm vm = VmThat(VmStatus.Running);

        vm.MarkNotFound();
        Assert.Equal(VmStatus.NotFound, vm.Status);
        Assert.Null(vm.InstanceStatus);

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);
        Assert.Equal(VmStatus.Running, vm.Status);
    }

    [Fact]
    public void NotFound_WhileADeleteWaits_KeepsDeleting()
    {
        Vm vm = VmThat(VmStatus.Running);
        Delete(vm);

        vm.MarkNotFound();

        Assert.Equal(VmStatus.Deleting, vm.Status);
    }

    [Theory]
    [InlineData(VmStatus.Creating)]
    [InlineData(VmStatus.Stopping)]
    [InlineData(VmStatus.Unknown)]
    [InlineData(VmStatus.NotFound)]
    [InlineData(VmStatus.Failed)]
    [InlineData(VmStatus.Deleted)]
    public void AReport_OrAnObservation_OfAStatusIncusNeverSays_IsRefused_AndChangesNothing(VmStatus notAnIncusWord)
    {
        Vm vm = VmThat(VmStatus.Running);
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        var observed = Assert.Throws<BusinessException>(() => vm.MarkObserved(notAnIncusWord, null, null));
        var reported = Assert.Throws<BusinessException>(() => vm.StopSucceeded(stop, notAnIncusWord, null, null));

        Assert.Equal("INVALID_STATUS", observed.Code);
        Assert.Equal("INVALID_STATUS", reported.Code);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(stop, vm.Operation.PendingId);
    }

    [Fact]
    public void Deleted_IgnoresEveryLaterObservation()
    {
        Vm vm = VmThat(VmStatus.Running);
        Guid delete = Delete(vm);
        vm.DeleteSucceeded(delete);

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);
        vm.MarkNotFound();

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.True(vm.IsDeleted);
    }

    private static Guid Start(Vm vm)
    {
        Guid start = Guid.NewGuid();
        vm.Start(start);
        return start;
    }

    private static Guid Stop(Vm vm)
    {
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);
        return stop;
    }

    // What VmDeletion does with the VM, without its load balancer check: the operation id the handler chose.
    private static Guid Delete(Vm vm)
    {
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);
        return delete;
    }
}
