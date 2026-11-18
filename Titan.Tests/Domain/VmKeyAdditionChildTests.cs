using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// A key addition is the VM's child entity (titan-ops architecture/code-structure.md#child-entity): the VM must be
// Running to add keys, its own status never changes for it, and the operation ends Added or Failed for good
// (titan-ops features/vm/design.md#vm-key-addition).
public class VmKeyAdditionChildTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantOwner = TestTenant.WithRoomToSpare();

    private static Vm RunningVm()
    {
        var vm = Vm.Create(TenantOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm42", "10.0.0.5");
        return vm;
    }

    [Fact]
    public void AddKeys_OnARunningVm_AddsAnAddition_WaitingOnItsOperation_WithoutChangingTheVmsStatus()
    {
        var vm = RunningVm();
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        Guid keyAdditionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();

        string instanceName = vm.AddKeys(keyAdditionId, operationId, ids);

        Assert.Equal("vm42", instanceName);
        Assert.Equal(VmStatus.Running, vm.Status); // untouched (titan-ops features/vm/design.md#ssh-key)
        VmKeyAddition addition = Assert.Single(vm.KeyAdditions);
        Assert.Equal(keyAdditionId, addition.Id);
        Assert.Equal(vm.Id, addition.VmId);
        Assert.Equal(vm.TenantId, addition.TenantId);
        Assert.Equal(ids, addition.SshKeyIds);
        Assert.Equal(VmKeyAdditionStatus.Adding, addition.Status);
        Assert.Equal(operationId, addition.PendingId);
        Assert.NotNull(addition.AskedAt);
        Assert.False(addition.IsTerminal);
    }

    [Theory]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Stopped)]
    [InlineData(VmStatus.Error)]
    [InlineData(null)]
    public void AddKeys_OnAVmNotRunning_IsRefused(VmStatus? status)
    {
        var vm = Vm.Create(TenantOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (status != null)
        {
            vm.MarkRunning("vm42", "10.0.0.5");
            vm.MarkObserved(status.Value, null);
        }

        var ex = Assert.Throws<BusinessException>(() => vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.KeyAdditions);
    }

    // The create's intent needs the id Incus knows the VM by, as every other intent that enqueues a command does
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    [Fact]
    public void AddKeys_OnAVmWithNoProviderId_IsRefused()
    {
        var vm = Vm.Create(TenantOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

        var ex = Assert.Throws<BusinessException>(() => vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    // Two additions waiting on one VM could lose a key: the worker does pull, append, push per addition, and the
    // command table may run two commands at once. Refused before a second row or command exists.
    [Fact]
    public void AddKeys_WhileAnotherAddition_IsStillAdding_IsRefused_AndAddsNothing()
    {
        var vm = RunningVm();
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);

        var ex = Assert.Throws<BusinessException>(() => vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Single(vm.KeyAdditions);
    }

    // Once the first ends, Added or Failed, a new one is accepted: nothing waits any more.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeys_AfterTheFirstEnds_IsAccepted(bool succeeded)
    {
        var vm = RunningVm();
        Guid firstId = Guid.NewGuid();
        Guid firstOp = Guid.NewGuid();
        vm.AddKeys(firstId, firstOp, [Guid.NewGuid()]);
        if (succeeded)
        {
            vm.KeyAdditionSucceeded(firstId, firstOp, false);
        }
        else
        {
            vm.KeyAdditionFailed(firstId, firstOp, "r", "d");
        }

        Guid secondId = Guid.NewGuid();
        vm.AddKeys(secondId, Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.Equal(2, vm.KeyAdditions.Count);
        Assert.Equal(VmKeyAdditionStatus.Adding, vm.KeyAdditions.Single(a => a.Id == secondId).Status);
    }

    [Fact]
    public void KeyAdditionSucceeded_EndsTheWaitingAddition_RecordingRestartNeeded()
    {
        var vm = RunningVm();
        Guid keyAdditionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        vm.AddKeys(keyAdditionId, operationId, [Guid.NewGuid()]);

        bool ended = vm.KeyAdditionSucceeded(keyAdditionId, operationId, restartNeeded: true);

        Assert.True(ended);
        VmKeyAddition addition = vm.KeyAdditions.Single();
        Assert.Equal(VmKeyAdditionStatus.Added, addition.Status);
        Assert.True(addition.RestartNeeded);
        Assert.Null(addition.PendingId);
        Assert.True(addition.IsTerminal);
    }

    [Fact]
    public void KeyAdditionFailed_EndsTheWaitingAddition_RecordingTheReason()
    {
        var vm = RunningVm();
        Guid keyAdditionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        vm.AddKeys(keyAdditionId, operationId, [Guid.NewGuid()]);

        bool ended = vm.KeyAdditionFailed(keyAdditionId, operationId, "AGENT_NOT_READY", "raw detail");

        Assert.True(ended);
        VmKeyAddition addition = vm.KeyAdditions.Single();
        Assert.Equal(VmKeyAdditionStatus.Failed, addition.Status);
        Assert.Equal("AGENT_NOT_READY", addition.FailureReason);
        Assert.Equal("raw detail", addition.FailureDetail);
        Assert.NotNull(addition.FailedAt);
        Assert.Null(addition.PendingId);
    }

    // A report naming another operation than the one the addition waits on changes nothing: the same shape as every
    // other type on titan-ops architecture/resource-lifecycle.md#shape.
    [Fact]
    public void SucceededAndFailed_WithAForeignOperationId_ReturnFalse_AndChangeNothing()
    {
        var vm = RunningVm();
        Guid keyAdditionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        vm.AddKeys(keyAdditionId, operationId, [Guid.NewGuid()]);

        Assert.False(vm.KeyAdditionSucceeded(keyAdditionId, Guid.NewGuid(), true));
        Assert.False(vm.KeyAdditionFailed(keyAdditionId, Guid.NewGuid(), "r", "d"));
        VmKeyAddition addition = vm.KeyAdditions.Single();
        Assert.Equal(VmKeyAdditionStatus.Adding, addition.Status);
        Assert.Equal(operationId, addition.PendingId);
    }

    // Ended once, a second report of the same operation id does nothing more either.
    [Fact]
    public void Succeeded_Repeated_ReturnsFalse_AndChangesNothingFurther()
    {
        var vm = RunningVm();
        Guid keyAdditionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        vm.AddKeys(keyAdditionId, operationId, [Guid.NewGuid()]);
        vm.KeyAdditionSucceeded(keyAdditionId, operationId, false);

        bool repeat = vm.KeyAdditionSucceeded(keyAdditionId, operationId, true);

        Assert.False(repeat);
        Assert.False(vm.KeyAdditions.Single().RestartNeeded); // the first report's value stands
    }

    // A key addition this VM does not have answers like a missing one.
    [Fact]
    public void AKeyAdditionOfAnotherVm_IsNotFound()
    {
        var vm = RunningVm();
        var other = RunningVm();
        other.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition foreign = other.KeyAdditions.Single();

        Assert.DoesNotContain(vm.KeyAdditions, a => a.Id == foreign.Id);
        Assert.Equal("VM_KEY_ADDITION_NOT_FOUND",
            Assert.Throws<NotFoundException>(() => vm.KeyAdditionSucceeded(foreign.Id, foreign.PendingId!.Value, false)).Code);
        Assert.Equal("VM_KEY_ADDITION_NOT_FOUND",
            Assert.Throws<NotFoundException>(() => vm.KeyAdditionFailed(foreign.Id, foreign.PendingId!.Value, "r", "d")).Code);
    }

    // The VM's delete ends every key addition still waiting (titan-ops architecture/resource-lifecycle.md#shape):
    // nothing will ever report on it again. One already Added or Failed stays as it is.
    [Fact]
    public void DeleteSucceeded_EndsEveryWaitingKeyAddition_AndLeavesEndedOnesAlone()
    {
        var vm = RunningVm();
        // Only one addition may be Adding at a time (AddKeys_WhileAnotherAddition_IsStillAdding_IsRefused_AndAddsNothing):
        // the done one is made and ended first, then the waiting one is added.
        Guid doneId = Guid.NewGuid();
        Guid doneOp = Guid.NewGuid();
        vm.AddKeys(doneId, doneOp, [Guid.NewGuid()]);
        vm.KeyAdditionSucceeded(doneId, doneOp, true);
        Guid waitingId = Guid.NewGuid();
        Guid waitingOp = Guid.NewGuid();
        vm.AddKeys(waitingId, waitingOp, [Guid.NewGuid()]);
        Guid deleteOp = Guid.NewGuid();
        vm.Delete(deleteOp);

        bool ended = vm.DeleteSucceeded(deleteOp);

        Assert.True(ended);
        VmKeyAddition waiting = vm.KeyAdditions.Single(a => a.Id == waitingId);
        Assert.Equal(VmKeyAdditionStatus.Failed, waiting.Status);
        Assert.Null(waiting.PendingId);
        VmKeyAddition done = vm.KeyAdditions.Single(a => a.Id == doneId);
        Assert.Equal(VmKeyAdditionStatus.Added, done.Status);
        Assert.True(done.RestartNeeded); // untouched
    }
}
