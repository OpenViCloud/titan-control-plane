using Titan.Domain.Values;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Application.Commands.Admin.Vm;
using Titan.Application.Commands.Admin.Vm.Contracts;
using Titan.Application.Queries.Admin.Network;
using Titan.Application.Queries.Admin.Network.Contracts;
using Titan.Application.Queries.Admin.Vm;
using Titan.Application.Queries.Admin.Vm.Contracts;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// Cross-tenant read (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read) and cross-tenant
// delete (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write): reads forward the (nullable)
// tenant filter untouched; delete bypasses tenant scope but keeps the tenant path's lifecycle guards.
// Read filtering lives in the repo (AdminResourceRepositoryTests); isolation of the tenant path is
// pinned by VmServiceTests/NetworkServiceTests.
public class AdminResourceServiceTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantAOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantA = TenantAOwner.Id;
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantBOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantB = TenantBOwner.Id;

    private static Vm RunningVm(Titan.Domain.Aggregates.Tenant.Tenant owner, string name)
    {
        var vm = Vm.Create(owner.Id, Guid.NewGuid(), name, "s", "tmpl-1", "vn-hcm-1", 20);
        vm.MarkRunning("one-123", "10.0.0.5");
        return vm;
    }

    private static Network NetFor(Titan.Domain.Aggregates.Tenant.Tenant owner, string name)
    {
        var net = TestAggregates.Network(owner, name, "vn-hcm-1");
        net.MarkActive("one-net-9", "10.0.0.0/24", "10.0.0.1");
        return net;
    }

    private static (AdminFacade svc, Mock<IProviderCommandQueue> wf, Mock<IAdminAuditLogRepository> audit)
        Build(IVmRepository vmRepo, INetworkRepository? netRepo = null,
            List<Titan.Domain.Aggregates.Volume.Volume>? attachedVolumes = null, bool isLoadBalancerBackend = false)
    {
        var wf = new Mock<IProviderCommandQueue>();
        wf.SetReturnsDefault(Task.CompletedTask);
        var audit = new Mock<IAdminAuditLogRepository>();
        var volumes = new Mock<IVolumeRepository>();
        volumes.Setup(v => v.ListAttachedToVmLockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(attachedVolumes ?? new List<Titan.Domain.Aggregates.Volume.Volume>());
        var user = Mock.Of<ICurrentUser>(u => u.KeycloakUserId == "admin-1");
        var svc = new AdminFacade(
            new ListAdminVmsQueryHandler(vmRepo),
            new ListAdminNetworksQueryHandler(netRepo ?? new StubNetworkRepo()),
            new DeleteAdminVmCommandHandler(
                vmRepo, volumes.Object, audit.Object, wf.Object, user, NullLogger<DeleteAdminVmCommandHandler>.Instance,
                new Titan.Domain.Services.VmDeletion(Mock.Of<ILoadBalancerRepository>(
                    r => r.IsBackendAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult(isLoadBalancerBackend)))));
        return (svc, wf, audit);
    }

    // Facade over the two query handlers and the command handler so the tests keep one call shape.
    private sealed class AdminFacade(
        ListAdminVmsQueryHandler listVms, ListAdminNetworksQueryHandler listNetworks, DeleteAdminVmCommandHandler deleteVm)
    {
        public Task<List<AdminVmQueryResult>> ListVmsAsync(Guid? tenantId, CancellationToken ct = default)
            => listVms.HandleAsync(new ListAdminVmsQuery(tenantId), ct);

        public Task<List<AdminNetworkQueryResult>> ListNetworksAsync(Guid? tenantId, CancellationToken ct = default)
            => listNetworks.HandleAsync(new ListAdminNetworksQuery(tenantId), ct);

        public Task DeleteVmAsync(Guid vmId, CancellationToken ct = default)
            => deleteVm.HandleAsync(new DeleteAdminVmCommand(vmId), ct);
    }

    // The delete carries the Incus project of the tenant that OWNS the VM, not the caller's.
    private static readonly string TenantBProject = Tenant.ProjectNameFor(TenantB);

    // ── ListVms / ListNetworks ────────────────────────────────

    [Fact]
    public async Task ListVms_ReturnsEveryTenantsVms_EachCarryingTenantId()
    {
        var (svc, _, _) = Build(new StubVmRepo(RunningVm(TenantAOwner, "a-web"), RunningVm(TenantBOwner, "b-db")));

        var dtos = await svc.ListVmsAsync(null);

        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.TenantId == TenantA && d.Name == "a-web");
        Assert.Contains(dtos, d => d.TenantId == TenantB && d.Name == "b-db");
        Assert.All(dtos, d => Assert.NotEqual(Guid.Empty, d.TenantId));
    }

    [Fact]
    public async Task ListVms_ForwardsTenantFilterToRepo_Untouched()
    {
        var repo = new StubVmRepo(RunningVm(TenantAOwner, "a-web"));
        var (svc, _, _) = Build(repo);

        await svc.ListVmsAsync(TenantA);
        Assert.Equal(TenantA, repo.LastTenantFilter);
        await svc.ListVmsAsync(null);
        Assert.Null(repo.LastTenantFilter);
    }

    [Fact]
    public async Task ListVms_MapsDomainFieldsOntoDto()
    {
        var (svc, _, _) = Build(new StubVmRepo(RunningVm(TenantAOwner, "a-web")));

        var dto = Assert.Single(await svc.ListVmsAsync(null));

        Assert.Equal("a-web", dto.Name);
        Assert.Equal("Running", dto.Status);
        Assert.Equal("10.0.0.5", dto.IpAddress);
        Assert.Equal("s", dto.VmOfferingId);
        Assert.Equal("vn-hcm-1", dto.ClusterId);
    }

    [Fact]
    public async Task ListNetworks_ReturnsEveryTenantsNetworks_EachCarryingTenantId()
    {
        var (svc, _, _) = Build(new StubVmRepo(), new StubNetworkRepo(NetFor(TenantAOwner, "a-net"), NetFor(TenantBOwner, "b-net")));

        var dtos = await svc.ListNetworksAsync(null);

        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.TenantId == TenantA && d.Name == "a-net");
        Assert.Contains(dtos, d => d.TenantId == TenantB && d.Name == "b-net");
    }

    [Fact]
    public async Task ListNetworks_ForwardsTenantFilterToRepo_Untouched()
    {
        var repo = new StubNetworkRepo(NetFor(TenantAOwner, "a-net"));
        var (svc, _, _) = Build(new StubVmRepo(), repo);

        await svc.ListNetworksAsync(TenantB);
        Assert.Equal(TenantB, repo.LastTenantFilter);
        await svc.ListNetworksAsync(null);
        Assert.Null(repo.LastTenantFilter);
    }

    [Fact]
    public async Task ListNetworks_MapsDomainFieldsOntoDto()
    {
        var (svc, _, _) = Build(new StubVmRepo(), new StubNetworkRepo(NetFor(TenantAOwner, "a-net")));

        var dto = Assert.Single(await svc.ListNetworksAsync(null));

        Assert.Equal("a-net", dto.Name);
        Assert.Equal("Created", dto.CurrentState);
        Assert.Equal("Exists", dto.CurrentExistence);
        Assert.Equal("10.0.0.0/24", dto.Cidr);
        Assert.Equal("vn-hcm-1", dto.ClusterId);
    }

    [Fact]
    public async Task ListNetworks_CarriesTheFailureDetail_TheTenantNeverSees()
    {
        var failed = TestAggregates.Network(TenantAOwner, "failed-net", "vn-hcm-1");
        failed.MarkRequestFailed("Network creation failed.", "create network: project t-1 not found");
        var (svc, _, _) = Build(new StubVmRepo(), new StubNetworkRepo(failed));

        var dto = Assert.Single(await svc.ListNetworksAsync(null));

        Assert.Null(dto.CurrentState);
        Assert.Equal("NotCreated", dto.CurrentExistence);
        Assert.Equal("Network creation failed.", dto.RequestFailureReason);
        Assert.Equal("create network: project t-1 not found", dto.RequestFailureDetail);
        Assert.NotNull(dto.RequestFailedAt);
    }

    // ── DeleteVmAsync (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write) ────────────────────────────────────

    [Fact]
    public async Task DeleteVm_RunningVmWithExternalId_EnqueuesTheDeleteCommand_AcrossTenant()
    {
        var vm = RunningVm(TenantBOwner, "victim");                       // owned by a DIFFERENT tenant
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo);

        await svc.DeleteVmAsync(vm.Id);

        // Enqueues the same delete command as the tenant path, in the OWNING tenant's project — proving the
        // admin path resolves the owner, not the caller.
        wf.Verify(w => w.EnqueueVmDeleteAsync(vm.Id, It.IsAny<Guid>(), "one-123", TenantBProject, "vn-hcm-1", default), Times.Once);

        // titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write — the audit row is flushed by the VM
        // repo's SaveAsync together with the waiting delete and the provider command (same DbContext).
        audit.Verify(a => a.Add(It.Is<AdminAuditLog>(e =>
            e.Action == "vm.delete.cross_tenant" && e.ActorKeycloakUserId == "admin-1"
            && e.ResourceType == "vm" && e.ResourceId == vm.Id && e.OwnerTenantId == TenantB)), Times.Once);
        Assert.True(repo.SaveCalled);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.NotNull(vm.PendingOperationId);
    }

    // titan-ops features/volume/design.md#vm-delete-releases-volumes — the admin path releases the VM's volumes like the
    // tenant path, or the owner's volume would be stuck attached to a VM that no longer exists.
    [Fact]
    public async Task DeleteVm_ReleasesTheVolumesTheVmHolds()
    {
        var vm = RunningVm(TenantBOwner, "victim");
        var volume = Titan.Domain.Aggregates.Volume.Volume.Create(TenantBOwner.Id, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        volume.Attach(vm);
        volume.MarkAttached(vm.Id);
        var (svc, _, _) = Build(new StubVmRepo(vm), attachedVolumes: new() { volume });

        await svc.DeleteVmAsync(vm.Id);

        // Released, not a request of the volume (titan-ops architecture/resource-lifecycle.md#kinds): nothing waits on it.
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Pending);
    }

    // titan-ops features/vm/design.md#admin-delete-audit — a repeated admin delete writes the audit row once and sends
    // nothing more: the delete already waits, and its command carries it (titan-ops architecture/resource-lifecycle.md#repeat).
    [Fact]
    public async Task DeleteVm_Repeated_AuditsOnce_AndSendsNothingMore()
    {
        var vm = RunningVm(TenantBOwner, "victim");
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo);

        await svc.DeleteVmAsync(vm.Id);
        Guid firstRequest = vm.PendingOperationId!.Value;
        await svc.DeleteVmAsync(vm.Id);

        audit.Verify(a => a.Add(It.IsAny<AdminAuditLog>()), Times.Once);
        wf.Verify(w => w.EnqueueVmDeleteAsync(vm.Id, firstRequest, "one-123", TenantBProject, "vn-hcm-1", default), Times.Once);
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Once);
        Assert.Equal(firstRequest, vm.PendingOperationId!.Value);
    }

    [Fact] // deleting a VM that is already gone is done, not an error, and audits nothing
    public async Task DeleteVm_AlreadyDeleted_IsANoOp()
    {
        var vm = RunningVm(TenantBOwner, "gone");
        vm.Delete(Guid.NewGuid());
        vm.MarkDeleted();
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo);

        await svc.DeleteVmAsync(vm.Id);

        audit.Verify(a => a.Add(It.IsAny<AdminAuditLog>()), Times.Never);
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        Assert.False(repo.SaveCalled);
    }

    [Fact]
    public async Task DeleteVm_NotFound_ThrowsNotFound_NoCommand()
    {
        var (svc, wf, audit) = Build(new StubVmRepo());

        await Assert.ThrowsAsync<NotFoundException>(() => svc.DeleteVmAsync(Guid.NewGuid()));
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        // A rejected attempt (nothing destructive happened) writes no audit row.
        audit.Verify(a => a.Add(It.IsAny<AdminAuditLog>()), Times.Never);
    }

    [Fact]
    public async Task DeleteVm_InvalidStatus_Throws_NoCommand()
    {
        var vm = Vm.Create(TenantBOwner.Id, Guid.NewGuid(), "starting", "s", "t", "z", 10); // Starting — not deletable
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.DeleteVmAsync(vm.Id));
        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        // Guard rejected before the destructive call — no audit row.
        audit.Verify(a => a.Add(It.IsAny<AdminAuditLog>()), Times.Never);
    }

    [Fact]
    public async Task DeleteVm_SnapshotInProgress_Throws_NoCommand()
    {
        var vm = RunningVm(TenantBOwner, "busy");
        vm.WithSnapshot("creating"); // a snapshot command still touching the instance: the VM is Snapshotting
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.DeleteVmAsync(vm.Id));
        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        // Guard rejected before the destructive call — no audit row.
        audit.Verify(a => a.Add(It.IsAny<AdminAuditLog>()), Times.Never);
    }

    // An admin is refused as the tenant is, rather than leaving a load balancer sending to a deleted VM
    // (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    [Fact]
    public async Task DeleteVm_ALoadBalancerBackend_IsRefused_NoAudit_NoCommand()
    {
        var vm = RunningVm(TenantBOwner, "behind-lb");
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo, isLoadBalancerBackend: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.DeleteVmAsync(vm.Id));
        Assert.Equal("VM_IS_LOAD_BALANCER_BACKEND", ex.Code);
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        audit.Verify(a => a.Add(It.IsAny<AdminAuditLog>()), Times.Never);
    }

    [Fact]
    public async Task DeleteVm_OfACreateRefusedBeforeProvisioning_DeletesLocally_NoCommand()
    {
        var vm = Vm.Create(TenantBOwner.Id, Guid.NewGuid(), "orphan", "s", "t", "z", 10);
        vm.MarkRequestFailed("never provisioned", null);            // the create gave up, no ExternalId
        var repo = new StubVmRepo(vm);
        var (svc, wf, audit) = Build(repo);

        await svc.DeleteVmAsync(vm.Id);

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.True(repo.SaveCalled);
        wf.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        // The VM was destroyed locally, so an audit row is staged; it persists with the Deleted status through the
        // VM repo's SaveAsync (same DbContext), so there is no separate audit save.
        audit.Verify(a => a.Add(It.Is<AdminAuditLog>(e =>
            e.Action == "vm.delete.cross_tenant" && e.ResourceId == vm.Id && e.OwnerTenantId == TenantB)), Times.Once);
    }
}

// Members the admin handlers do not use throw, to catch an accidental route through tenant-scoped
// code (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read).
file sealed class StubVmRepo(params Vm[] seed) : IVmRepository
{
    private readonly List<Vm> _vms = [.. seed];
    public Guid? LastTenantFilter { get; private set; }
    public bool SaveCalled { get; private set; }

    public Task<List<Vm>> ListAllAsync(Guid? tenantId, CancellationToken ct = default)
    {
        LastTenantFilter = tenantId;
        var items = tenantId is null ? _vms : _vms.Where(v => v.TenantId == tenantId);
        return Task.FromResult(items.ToList());
    }

    public Task<Vm?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_vms.FirstOrDefault(v => v.Id == id));

    public Task SaveAsync(CancellationToken ct = default) { SaveCalled = true; return Task.CompletedTask; }

    public Task<Vm?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default) => FindByIdAsync(id, ct);

    public Task<List<Vm>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<Vm>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<Vm>> ListBillableAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<Vm>> ListByExternalIdsAsync(Guid tenantId, IReadOnlyCollection<string> externalIds, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<Vm>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
    public void Add(Vm vm) => throw new NotSupportedException();
}

file sealed class StubNetworkRepo(params Network[] seed) : INetworkRepository
{
    public Guid? LastTenantFilter { get; private set; }

    public Task<List<Network>> ListAllAsync(Guid? tenantId, CancellationToken ct = default)
    {
        LastTenantFilter = tenantId;
        var items = tenantId is null ? seed : seed.Where(n => n.TenantId == tenantId);
        return Task.FromResult(items.ToList());
    }

    public Task<Network?> FindByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Network?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Network?> FindByFirewallRuleIdAsync(Guid ruleId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<Network>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> CountPublicIpv6ActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasLiveLoadBalancersAsync(Guid networkId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ExistsByNameAsync(Guid tenantId, NetworkName name, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasActiveVmsAsync(Guid networkId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasCidrOverlapAsync(Guid tenantId, Ipv4Cidr cidr, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<Network>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) => throw new NotSupportedException();
    public void Add(Network network) => throw new NotSupportedException();
    public Task SaveAsync(CancellationToken ct = default) => throw new NotSupportedException();
}
