using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// How long a stop waits for the guest to shut itself down before the worker cuts the power: a property of the VM, 120 s
// unless the customer set 30 to 1800 (titan-ops features/vm/design.md#vm-stop-timeout). The rule is a predicate the
// command handler asks (ComputeInputTests); the aggregate takes the value it is given.
public class VmStopTimeoutTests
{
    private static Vm AVm(int? stopTimeoutSeconds = null)
    {
        if (stopTimeoutSeconds == null)
        {
            return Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", "tpl", "cluster-1", 20);
        }
        return Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", "tpl", "cluster-1", 20,
            stopTimeoutSeconds: stopTimeoutSeconds.Value);
    }

    [Theory]
    [InlineData(Vm.MinStopTimeoutSeconds, true)]
    [InlineData(Vm.DefaultStopTimeoutSeconds, true)]
    [InlineData(Vm.MaxStopTimeoutSeconds, true)]
    [InlineData(Vm.MinStopTimeoutSeconds - 1, false)]
    [InlineData(Vm.MaxStopTimeoutSeconds + 1, false)]
    [InlineData(0, false)]
    [InlineData(-120, false)]
    public void IsAStopTimeout_Takes30To1800Seconds(int seconds, bool expected)
    {
        Assert.Equal(expected, Vm.IsAStopTimeout(seconds));
    }

    [Fact]
    public void TheConstants_AreTheRulesNumbers()
    {
        Assert.Equal((120, 30, 1800), (Vm.DefaultStopTimeoutSeconds, Vm.MinStopTimeoutSeconds, Vm.MaxStopTimeoutSeconds));
    }

    [Fact]
    public void Create_WithoutAStopTimeout_Waits120Seconds()
    {
        Assert.Equal(120, AVm().StopTimeoutSeconds);
    }

    [Fact]
    public void Create_KeepsTheStopTimeoutItIsGiven()
    {
        Assert.Equal(1800, AVm(1800).StopTimeoutSeconds);
    }

    [Fact]
    public void ChangeStopTimeout_WritesTheNewValue_AndLeavesTheStatusAndOperation()
    {
        Vm vm = AVm();
        vm.MarkRunning("vm-x", "10.0.0.5");
        VmStatus status = vm.Status;
        VmOperation operation = vm.Operation;

        vm.ChangeStopTimeout(600);

        Assert.Equal(600, vm.StopTimeoutSeconds);
        Assert.Equal(status, vm.Status);
        Assert.Equal(operation, vm.Operation);
    }

    // As a rename: a VM whose delete ended takes no more changes.
    [Fact]
    public void ChangeStopTimeout_OnADeletedVm_IsVmInvalidStatus()
    {
        Vm vm = AVm();
        vm.MarkRunning("vm-x", "10.0.0.5");
        vm.Delete(Guid.NewGuid());
        vm.MarkDeleted();

        BusinessException refused = Assert.Throws<BusinessException>(() => vm.ChangeStopTimeout(600));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Equal(120, vm.StopTimeoutSeconds);
    }
}
