using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The write half of opening a console (titan-ops features/vm/design.md#vm-console): who opened it, recorded once the
// console attached. The console endpoint runs it as the ticket's taker.
public class VmConsoleHandlerTests
{
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeActivityRecorder _activity = new();
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant _owner = TestTenant.WithRoomToSpare();

    public VmConsoleHandlerTests()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(_owner.Id);
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("kc-1");
    }

    private Vm SeedVm(Titan.Domain.Aggregates.Tenant.Tenant owner, bool running)
    {
        Vm vm = Vm.Create(owner.Id, Guid.NewGuid(), "web-1", "s", "tpl-1", "local-mac", 20);
        vm.MarkCreated("vmabc");
        vm.MarkObserved(running ? VmStatus.Running : VmStatus.Stopped, running ? "10.0.0.5" : null);
        _vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private Task RequestAsync(Guid vmId) =>
        new RequestVmConsoleCommandHandler(_vms.Object, _currentUser.Object, _activity)
            .HandleAsync(new RequestVmConsoleCommand(vmId));

    [Fact]
    public async Task Request_OnARunningVm_RecordsTheConsoleAsTheCaller()
    {
        Vm vm = SeedVm(_owner, running: true);

        await RequestAsync(vm.Id);

        FakeActivityRecorder.Entry entry = Assert.Single(_activity.Events);
        Assert.Equal("vm.console_requested", entry.Action);
        Assert.Equal("vm", entry.ResourceType);
        Assert.Equal(vm.Id, entry.ResourceId);
        Assert.Equal("web-1", entry.ResourceName);
        Assert.True(entry.Saved);
        Assert.Null(entry.OutcomeTenantId);
    }

    [Fact]
    public async Task Request_OnAnotherTenantsVm_IsNotFound_AndRecordsNothing()
    {
        Vm vm = SeedVm(TestTenant.WithRoomToSpare(), running: true);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => RequestAsync(vm.Id));

        Assert.Equal("VM_NOT_FOUND", refused.Code);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Request_OnAStoppedVm_IsInvalidStatus_AndRecordsNothing()
    {
        Vm vm = SeedVm(_owner, running: false);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => RequestAsync(vm.Id));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Empty(_activity.Events);
    }
}
