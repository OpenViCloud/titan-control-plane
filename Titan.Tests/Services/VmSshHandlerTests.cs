using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class VmSshHandlerTests
{
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly Guid _tenantId = Guid.NewGuid();

    public VmSshHandlerTests()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(_tenantId);
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("kc-1");
    }

    private Vm SeedVm(Guid tenantId, bool running)
    {
        Vm vm = Vm.CreateUnchecked(tenantId, Guid.NewGuid(), "web-1", "s", "tpl-1", "local-mac", 20);
        vm.MarkCreated("vmabc");
        vm.MarkObserved(running ? VmState.Running : VmState.Stopped, running ? "10.0.0.5" : null);
        _vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private Task RequestAsync(Guid vmId) =>
        new RequestVmSshCommandHandler(_vms.Object, _currentUser.Object, _activity)
            .HandleAsync(new RequestVmSshCommand(vmId));

    private Task<VmShellTargetQueryResult> TargetAsync(Guid vmId) =>
        new GetVmShellTargetQueryHandler(_vms.Object, _currentUser.Object).HandleAsync(new GetVmShellTargetQuery(vmId));

    [Fact]
    public async Task Request_OnARunningVm_RecordsTheRequestAsTheCaller()
    {
        Vm vm = SeedVm(_tenantId, running: true);

        await RequestAsync(vm.Id);

        FakeActivityRecorder.Entry entry = Assert.Single(_activity.Events);
        Assert.Equal("vm.ssh_requested", entry.Action);
        Assert.Equal("vm", entry.ResourceType);
        Assert.Equal(vm.Id, entry.ResourceId);
        Assert.Equal("web-1", entry.ResourceName);
        // Recorded, not staged as an outcome: the actor is the current user, not "system".
        Assert.True(entry.Saved);
        Assert.Null(entry.OutcomeTenantId);
    }

    [Fact]
    public async Task Request_OnAnotherTenantsVm_IsNotFound_AndRecordsNothing()
    {
        Vm vm = SeedVm(Guid.NewGuid(), running: true);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => RequestAsync(vm.Id));

        Assert.Equal("VM_NOT_FOUND", refused.Code);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Request_OnAStoppedVm_IsInvalidStatus_AndRecordsNothing()
    {
        Vm vm = SeedVm(_tenantId, running: false);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => RequestAsync(vm.Id));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Target_OfARunningVm_IsItsClusterProjectAndInstance()
    {
        Vm vm = SeedVm(_tenantId, running: true);

        VmShellTargetQueryResult target = await TargetAsync(vm.Id);

        Assert.Equal("local-mac", target.ClusterId);
        Assert.Equal(Tenant.ProjectNameFor(_tenantId), target.ProjectName);
        Assert.Equal("vmabc", target.InstanceName);
    }

    [Fact]
    public async Task Target_OfAnotherTenantsVm_IsNotFound()
    {
        Vm vm = SeedVm(Guid.NewGuid(), running: true);

        Assert.Equal("VM_NOT_FOUND", (await Assert.ThrowsAsync<NotFoundException>(() => TargetAsync(vm.Id))).Code);
    }

    [Fact]
    public async Task Target_OfAStoppedVm_IsInvalidStatus()
    {
        Vm vm = SeedVm(_tenantId, running: false);

        Assert.Equal("VM_INVALID_STATUS", (await Assert.ThrowsAsync<BusinessException>(() => TargetAsync(vm.Id))).Code);
    }
}
