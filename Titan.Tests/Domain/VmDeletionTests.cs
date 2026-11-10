using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/domain-services.md#cross-aggregate-lock: a VM a load balancer still sends to is not deleted, by its
// tenant or by an admin, and VmDeletion is the only way to delete one. Silent about the lock; VmDeletionRaceDbTests runs
// the race on Postgres.
public class VmDeletionTests
{
    private readonly Mock<ILoadBalancerRepository> _loadBalancers = new();
    private readonly Tenant _tenant = TestTenant.Active("vd", "kc-vd", "vd@titan.local");

    private readonly Mock<IVolumeRepository> _volumes = new();

    public VmDeletionTests() =>
        _volumes.Setup(v => v.ListAttachedToVmLockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Titan.Domain.Aggregates.Volume.Volume>());

    private VmDeletion Deletion() => new(_loadBalancers.Object, _volumes.Object);

    private Vm OnIncus()
    {
        Vm vm = Vm.Create(_tenant.Id, Guid.NewGuid(), "vm-vd", "m", "tpl", "cluster-1", 20);
        vm.MarkCreated("vm-ext");
        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);
        return vm;
    }

    [Fact]
    public async Task AVmALoadBalancerSendsTo_IsRefused()
    {
        Vm vm = OnIncus();
        _loadBalancers.Setup(r => r.IsBackendAsync(vm.Id, default)).ReturnsAsync(true);

        BusinessException ex = await Assert.ThrowsAsync<BusinessException>(() => Deletion().DeleteAsync(vm, Guid.NewGuid()));

        Assert.Equal("VM_IS_LOAD_BALANCER_BACKEND", ex.Code);
    }

    [Fact]
    public async Task AVmOnIncus_NoLoadBalancerSendsTo_IsDispatched_WithItsProviderId()
    {
        Vm vm = OnIncus();
        Guid delete = Guid.NewGuid();

        await Deletion().DeleteAsync(vm, delete);

        Assert.Equal("vm-ext", vm.ExternalId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Equal(delete, vm.Operation.PendingId);
    }

    [Fact]
    public async Task AVmThatNeverReachedTheProvider_IsDeletedHere()
    {
        Vm vm = Vm.Create(_tenant.Id, Guid.NewGuid(), "vm-vd", "m", "tpl", "cluster-1", 20);
        vm.MarkRequestFailed("create gave up", null);

        await Deletion().DeleteAsync(vm, Guid.NewGuid());

        Assert.True(vm.IsDeleted); // nothing on the provider: no command, deleted here
        Assert.Null(vm.Operation.PendingId);
    }

    // The VM's own refusal comes before the load balancer's: a snapshot being created makes the VM Snapshotting.
    [Fact]
    public async Task AVmWithASnapshotBeingCreated_IsRefusedForThat_First()
    {
        Vm vm = OnIncus();
        vm.WithSnapshot("creating");
        _loadBalancers.Setup(r => r.IsBackendAsync(vm.Id, default)).ReturnsAsync(true);

        BusinessException ex = await Assert.ThrowsAsync<BusinessException>(() => Deletion().DeleteAsync(vm, Guid.NewGuid()));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    // The compiler keeps Titan.Application off these two, so VmDeletion is the only way to delete a VM.
    [Fact]
    public void OnlyVmDeletionDeletesAVm()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] publicMethods = [.. typeof(Vm).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(Vm.Rename), publicMethods);
        Assert.DoesNotContain(nameof(Vm.Delete), publicMethods);
    }
}
