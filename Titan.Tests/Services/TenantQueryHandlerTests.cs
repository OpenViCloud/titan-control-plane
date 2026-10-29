using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Platform.Tenant;
using Titan.Application.Queries.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// What the portal and admin portal read: the caller's own tenant, its members, and the admin's
// cross-tenant list — plus the two refusals the shared CurrentTenant lookup owns (NOT_A_MEMBER,
// TENANT_NOT_FOUND).
public class TenantQueryHandlerTests
{
    private readonly Mock<ITenantRepository> _repo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    private Tenant CallerTenant()
    {
        var tenant = TestTenant.Active("acme", "kc-owner", "acme@example.com");
        _currentUser.SetupGet(u => u.TenantId).Returns(tenant.Id);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        return tenant;
    }

    [Fact]
    public async Task GetTenant_ReturnsTheCallersTenant_WithQuotaAndDebtCeiling()
    {
        var tenant = CallerTenant();

        var result = await new GetTenantQueryHandler(_repo.Object, _currentUser.Object)
            .HandleAsync(new GetTenantQuery());

        Assert.Equal(tenant.Id, result.Id);
        Assert.Equal("acme", result.Name);
        Assert.Equal(TenantStatus.Active, result.Status);
        Assert.Equal(tenant.Quota.MaxVms, result.MaxVms);
        Assert.Equal(tenant.Quota.MaxNetworks, result.MaxNetworks);
        Assert.Equal(tenant.DebtCeiling, result.DebtCeiling);
    }

    // The portal's quota card reads these two caps; distinct values catch a swapped mapping.
    [Fact]
    public async Task GetTenant_CarriesTheVolumeAndPublicIpv6Caps()
    {
        var tenant = CallerTenant();
        tenant.UpdateQuota(TenantQuota.Create(
            maxVms: 5, maxNetworks: 3, maxVolumes: 7, maxVmSnapshotsPerVm: 3,
            maxVolumeSnapshotsPerVolume: 3, maxFirewallRulesPerNetwork: 20, maxPublicIpv6: 4, maxSshKeys: 20, maxLoadBalancers: 5));

        var result = await new GetTenantQueryHandler(_repo.Object, _currentUser.Object)
            .HandleAsync(new GetTenantQuery());

        Assert.Equal(7, result.MaxVolumes);
        Assert.Equal(4, result.MaxPublicIpv6);
    }

    // titan-ops architecture/tenancy-and-admin.md#approval-is-activation: a tenant has no provider account, so the API
    // carries none — the Incus project is per cluster and named from the id.
    [Fact]
    public async Task GetTenant_CarriesNoProviderAccount()
    {
        CallerTenant();

        var result = await new GetTenantQueryHandler(_repo.Object, _currentUser.Object)
            .HandleAsync(new GetTenantQuery());

        Assert.Equal(TenantStatus.Active, result.Status);
        var fields = typeof(TenantQueryResult).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("ExternalAccountName", fields);
        Assert.DoesNotContain("ProvisioningError", fields);
        Assert.DoesNotContain("ProviderAccount", fields);
    }

    [Fact]
    public async Task ListTenantMembers_ReturnsTheOwner()
    {
        CallerTenant();

        var members = await new ListTenantMembersQueryHandler(_repo.Object, _currentUser.Object)
            .HandleAsync(new ListTenantMembersQuery());

        var owner = Assert.Single(members);
        Assert.Equal("kc-owner", owner.KeycloakUserId);
    }

    [Fact]
    public async Task GetTenant_CallerWithoutTenant_Is403NotAMember_AndReadsNothing()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns((Guid?)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            new GetTenantQueryHandler(_repo.Object, _currentUser.Object).HandleAsync(new GetTenantQuery()));

        Assert.Equal("NOT_A_MEMBER", ex.Code);
        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListTenantMembers_TenantGone_Is404TenantNotFound()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(Guid.NewGuid());
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            new ListTenantMembersQueryHandler(_repo.Object, _currentUser.Object).HandleAsync(new ListTenantMembersQuery()));

        Assert.Equal("TENANT_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task ListTenants_ReturnsEveryTenant_AcrossThePlatform()
    {
        var a = TestTenant.Active("a", "kc-a", "a@example.com");
        var b = Tenant.CreatePending("b", "kc-b", "b@example.com");
        _repo.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Tenant> { a, b });

        var result = await new ListTenantsQueryHandler(_repo.Object).HandleAsync(new ListTenantsQuery());

        Assert.Equal(new[] { (a.Id, "Active"), (b.Id, "PendingApproval") },
            result.Select(t => (t.Id, t.Status)).ToArray());
    }

    // The admin tenant list carries every cap the quota dialog edits; distinct values catch a swapped mapping.
    [Fact]
    public async Task ListTenants_CarriesAllNineCaps_AndTheDebtCeiling()
    {
        var tenant = TestTenant.Active("a", "kc-a", "a@example.com");
        tenant.UpdateQuota(TenantQuota.Create(1, 2, 3, 4, 5, 6, 7, 8, 9));
        _repo.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Tenant> { tenant });

        var row = Assert.Single(await new ListTenantsQueryHandler(_repo.Object).HandleAsync(new ListTenantsQuery()));

        Assert.Equal(1, row.MaxVms);
        Assert.Equal(2, row.MaxNetworks);
        Assert.Equal(3, row.MaxVolumes);
        Assert.Equal(4, row.MaxVmSnapshotsPerVm);
        Assert.Equal(5, row.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(6, row.MaxFirewallRulesPerNetwork);
        Assert.Equal(7, row.MaxPublicIpv6);
        Assert.Equal(8, row.MaxSshKeys);
        Assert.Equal(9, row.MaxLoadBalancers);
        Assert.Equal(tenant.DebtCeiling, row.DebtCeiling);
    }
}
