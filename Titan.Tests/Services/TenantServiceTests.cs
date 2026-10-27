using Moq;
using Titan.Application.Commands.Platform.Tenant.Contracts;
using TenantCmd = Titan.Application.Commands.Platform.Tenant;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The tenant command handlers. None of them takes an IProviderCommandQueue: approval is activation and no provider is
// asked for anything (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#approval-is-activation) — the compiler holds that, not a
// Verify(Times.Never). Opening the wallet before approval is the adapters' job: TenantResolutionMiddlewareTests and
// UpdateTenantStatusEndpointTests.
public class TenantServiceTests
{
    private readonly Mock<ITenantRepository> _repo = new();

    // Facade over the tenant command handlers (the queries are covered in TenantQueryHandlerTests); it
    // forwards and adds nothing.
    private TenantFacade CreateService() => new(_repo.Object);

    private sealed class TenantFacade(ITenantRepository repo)
    {
        public Task BootstrapAsync(string keycloakUserId, string username, string email, CancellationToken ct = default)
            => new TenantCmd.BootstrapTenantCommandHandler(repo)
                .HandleAsync(new BootstrapTenantCommand(keycloakUserId, username, email), ct);

        public Task ApproveDeclaredAccountAsync(Guid tenantId, CancellationToken ct = default)
            => new TenantCmd.ApproveDeclaredAccountCommandHandler(repo)
                .HandleAsync(new ApproveDeclaredAccountCommand(tenantId), ct);

        public Task UpdateStatusAsync(Guid tenantId, string status, CancellationToken ct = default)
            => new TenantCmd.UpdateTenantStatusCommandHandler(repo)
                .HandleAsync(new UpdateTenantStatusCommand(tenantId, status), ct);

        // An admin client that sends only the two caps it always sent: the other seven arrive absent.
        public Task UpdateQuotaAsync(Guid tenantId, int maxVms, int maxNetworks, CancellationToken ct = default)
            => new TenantCmd.UpdateTenantQuotaCommandHandler(repo)
                .HandleAsync(new UpdateTenantQuotaCommand(
                    tenantId, maxVms, maxNetworks, MaxVolumes: null, MaxVmSnapshotsPerVm: null,
                    MaxVolumeSnapshotsPerVolume: null, MaxFirewallRulesPerNetwork: null, MaxPublicIpv6: null,
                    MaxSshKeys: null, MaxLoadBalancers: null), ct);

        public Task UpdateQuotaAsync(UpdateTenantQuotaCommand command, CancellationToken ct = default)
            => new TenantCmd.UpdateTenantQuotaCommandHandler(repo).HandleAsync(command, ct);

        public Task UpdateDebtCeilingAsync(Guid tenantId, decimal debtCeiling, CancellationToken ct = default)
            => new TenantCmd.UpdateTenantDebtCeilingCommandHandler(repo)
                .HandleAsync(new UpdateTenantDebtCeilingCommand(tenantId, debtCeiling), ct);
    }

    // ── UpdateStatusAsync — same-status guard ─────────────────────────────

    [Fact]
    // A second press after the result is already there is a repeat
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat): the aggregate still refuses it
    // (Tenant.Suspend/RequestActivation throw); the handler recognises it first and answers 204.
    public async Task UpdateStatus_SuspendAlreadySuspended_ChangesNothing()
    {
        var tenant = MakeTenant(TenantStatus.Suspended);
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Suspended");

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ActivateAlreadyActive_ChangesNothing()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Active");

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_InvalidStatusString_ThrowsBadRequest()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(tenant.Id, "Deleted"));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task UpdateStatus_TenantNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Tenant?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), "Suspended"));
    }

    [Fact]
    public async Task UpdateStatus_SuspendActive_SavesCalled()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Suspended");

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_ActivateSuspended_SavesCalled()
    {
        var tenant = MakeTenant(TenantStatus.Suspended);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Active");

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // ── Anti-abuse gate: admin approve / reject a PendingApproval tenant ─────────

    [Fact] // approval is activation: no provider account to wait for
    public async Task UpdateStatus_ApprovePending_MakesItActive()
    {
        var tenant = MakeTenant(TenantStatus.PendingApproval);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Active");

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_RejectPending_Suspends()
    {
        var tenant = MakeTenant(TenantStatus.PendingApproval);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Suspended");

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // a rejected signup is approved again by the same press
    public async Task UpdateStatus_ActivateRejectedSignup_MakesItActive()
    {
        var tenant = MakeTenant(TenantStatus.PendingApproval);
        tenant.Suspend();
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateStatusAsync(tenant.Id, "Active");

        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Theory] // only Active and Suspended are admin targets; Provisioning is no status any more
    [InlineData("Provisioning")]
    [InlineData("PendingApproval")]
    public async Task UpdateStatus_PendingInvalidTarget_Throws(string target)
    {
        var tenant = MakeTenant(TenantStatus.PendingApproval);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(tenant.Id, target));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(TenantStatus.PendingApproval, tenant.Status);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── UpdateQuotaAsync ──────────────────────────────────────────

    [Fact]
    public async Task UpdateQuota_SetsBothLimits_SavesOnce()
    {
        var tenant = MakeTenant(TenantStatus.Active); // starts at MaxVms=10, MaxNetworks=1
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateQuotaAsync(tenant.Id, 25, 5);

        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#commands-return-no-data: nothing comes back; the admin UI re-reads the tenant list, which reads the aggregate.
        Assert.Equal(25, tenant.Quota.MaxVms);
        Assert.Equal(5, tenant.Quota.MaxNetworks);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateQuota_TenantNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Tenant?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateQuotaAsync(Guid.NewGuid(), 5, 5));
    }

    [Fact]
    public async Task UpdateQuota_NegativeValue_ThrowsBusiness_DoesNotSave()
    {
        // Defense in depth: the validator returns 400 first, but the domain still rejects a negative
        // quota so no path can persist a nonsensical limit.
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().UpdateQuotaAsync(tenant.Id, -1, 5));
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateQuota_ZeroIsAllowed()
    {
        // Zero is a legitimate cap ("freeze this tenant's growth"), distinct from negative.
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateQuotaAsync(tenant.Id, 0, 0);

        Assert.Equal(0, tenant.Quota.MaxVms);
        Assert.Equal(0, tenant.Quota.MaxNetworks);
    }

    [Fact] // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#tenant-quota — a client that sends two caps must not reset the other seven.
    // Rebuilding the whole value object from what the request carries silently zeroes volumes, snapshots, firewall
    // rules, IPv6, SSH keys and load balancers — no error, no log, and the tenant stops being able to create anything.
    public async Task UpdateQuota_PreservesTheCapsTheEndpointDoesNotCarry()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateQuotaAsync(tenant.Id, 25, 5);

        Assert.Equal(10, tenant.Quota.MaxVolumes);
        Assert.Equal(3, tenant.Quota.MaxVmSnapshotsPerVm);
        Assert.Equal(3, tenant.Quota.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(20, tenant.Quota.MaxFirewallRulesPerNetwork);
        Assert.Equal(10, tenant.Quota.MaxPublicIpv6);
        Assert.Equal(20, tenant.Quota.MaxSshKeys);
        Assert.Equal(5, tenant.Quota.MaxLoadBalancers);
    }

    [Fact]
    public async Task UpdateQuota_AllNineCaps_SetsEachOne()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        // Nine distinct values: a swapped mapping between two caps shows as a wrong number, not a pass.
        await CreateService().UpdateQuotaAsync(new UpdateTenantQuotaCommand(
            tenant.Id, MaxVms: 11, MaxNetworks: 12, MaxVolumes: 13, MaxVmSnapshotsPerVm: 14,
            MaxVolumeSnapshotsPerVolume: 15, MaxFirewallRulesPerNetwork: 16, MaxPublicIpv6: 17, MaxSshKeys: 18,
            MaxLoadBalancers: 19));

        Assert.Equal(11, tenant.Quota.MaxVms);
        Assert.Equal(12, tenant.Quota.MaxNetworks);
        Assert.Equal(13, tenant.Quota.MaxVolumes);
        Assert.Equal(14, tenant.Quota.MaxVmSnapshotsPerVm);
        Assert.Equal(15, tenant.Quota.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(16, tenant.Quota.MaxFirewallRulesPerNetwork);
        Assert.Equal(17, tenant.Quota.MaxPublicIpv6);
        Assert.Equal(18, tenant.Quota.MaxSshKeys);
        Assert.Equal(19, tenant.Quota.MaxLoadBalancers);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateQuota_AnAbsentCap_KeepsTheTenantsOwnValue_NotTheDefault()
    {
        // Seeded away from every default: "kept" must mean this tenant's value, which a handler falling back to
        // TenantQuota.Default would get wrong while the defaults-only test above stays green.
        var tenant = MakeTenant(TenantStatus.Active);
        tenant.UpdateQuota(TenantQuota.Create(1, 2, 3, 4, 5, 6, 7, 8, 9));
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateQuotaAsync(new UpdateTenantQuotaCommand(
            tenant.Id, MaxVms: 21, MaxNetworks: 22, MaxVolumes: null, MaxVmSnapshotsPerVm: null,
            MaxVolumeSnapshotsPerVolume: null, MaxFirewallRulesPerNetwork: null, MaxPublicIpv6: null, MaxSshKeys: 28,
            MaxLoadBalancers: null));

        Assert.Equal(21, tenant.Quota.MaxVms);
        Assert.Equal(22, tenant.Quota.MaxNetworks);
        Assert.Equal(3, tenant.Quota.MaxVolumes);
        Assert.Equal(4, tenant.Quota.MaxVmSnapshotsPerVm);
        Assert.Equal(5, tenant.Quota.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(6, tenant.Quota.MaxFirewallRulesPerNetwork);
        Assert.Equal(7, tenant.Quota.MaxPublicIpv6);
        Assert.Equal(28, tenant.Quota.MaxSshKeys);
        Assert.Equal(9, tenant.Quota.MaxLoadBalancers);
    }

    [Fact]
    public async Task UpdateQuota_ANegativeNewCap_ThrowsBusiness_DoesNotSave()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateService().UpdateQuotaAsync(
            new UpdateTenantQuotaCommand(
                tenant.Id, MaxVms: 1, MaxNetworks: 1, MaxVolumes: null, MaxVmSnapshotsPerVm: null,
                MaxVolumeSnapshotsPerVolume: null, MaxFirewallRulesPerNetwork: null, MaxPublicIpv6: null,
                MaxSshKeys: null, MaxLoadBalancers: -1)));

        Assert.Equal("INVALID_QUOTA", ex.Code);
        Assert.Equal(TenantQuota.DefaultMaxLoadBalancers, tenant.Quota.MaxLoadBalancers);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    // ── UpdateDebtCeilingAsync ──────
    //
    // The ceiling is a REFERENCE number for the admin at month-end close
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#postpaid-no-brake): it blocks nothing, and it is shaped like
    // MaxVms — non-null, seeded at creation — not like a config override with a nullable escape hatch.

    [Fact]
    public void NewTenant_StartsAtTheDefaultDebtCeiling()
    {
        // Same shape as MaxVms/MaxNetworks: the default is baked in at creation, so every tenant row
        // carries a real number and no reader has to consult config to know what applies.
        var tenant = TestTenant.Active("acme", "kc-1", "a@b.c");

        Assert.Equal(500_000L, tenant.DebtCeiling);
    }

    [Fact]
    public async Task UpdateDebtCeiling_SetsNumber_SavesOnce()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateDebtCeilingAsync(tenant.Id, 12_000_000m);

        Assert.Equal(12_000_000L, tenant.DebtCeiling);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateDebtCeiling_Negative_ThrowsBusiness_DoesNotSave()
    {
        // Defense in depth behind the request validator (same shape as UpdateQuota).
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().UpdateDebtCeilingAsync(tenant.Id, -1m));

        Assert.Equal("INVALID_DEBT_CEILING", ex.Code);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateDebtCeiling_Zero_IsAllowed_MeansTheAdminExpectsNoDebtHere()
    {
        var tenant = MakeTenant(TenantStatus.Active);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().UpdateDebtCeilingAsync(tenant.Id, 0m);

        Assert.Equal(0L, tenant.DebtCeiling);
    }

    [Fact]
    public async Task UpdateDebtCeiling_TenantNotFound_ThrowsNotFound()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Tenant?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateDebtCeilingAsync(Guid.NewGuid(), 1m));
    }

    // ── BootstrapAsync (open signup — no domain classification) ─────

    [Fact]
    public async Task Bootstrap_Idempotent_ExistingOwner_DoesNothing()
    {
        _repo.Setup(r => r.ExistsByOwnerAsync("user-1", default)).ReturnsAsync(true);

        await CreateService().BootstrapAsync("user-1", "alice", "alice@example.com");

        _repo.Verify(r => r.Add(It.IsAny<Tenant>()), Times.Never);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Every signup starts pending, whatever the domain: the middleware approves one of an account the realm declares
    // in the same request after opening its wallet, so there is one approval path, not two.
    [Theory]
    [InlineData("alice@gmail.com")]
    [InlineData("bob@openvicloud.com")]
    [InlineData("sv@university.edu.vn")]
    public async Task Bootstrap_CreatesAPendingTenant_ForAnyDomain(string email)
    {
        _repo.Setup(r => r.ExistsByOwnerAsync("user-1", default)).ReturnsAsync(false);
        Tenant? saved = null;
        _repo.Setup(r => r.Add(It.IsAny<Tenant>())).Callback<Tenant>(t => saved = t);

        await CreateService().BootstrapAsync("user-1", "u", email);

        Assert.NotNull(saved);
        Assert.Equal(TenantStatus.PendingApproval, saved!.Status);
        Assert.Equal(email, saved.Email);
        Assert.Equal("u", saved.Name);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // Two first requests of one user race: the loser's insert hits a unique index after the winner committed the
    // tenant with its owner. The loser finds the caller owning a tenant and is done.
    [Fact]
    public async Task Bootstrap_LosingTheRaceToTheSameUser_IsDone()
    {
        _repo.SetupSequence(r => r.ExistsByOwnerAsync("user-1", default)).ReturnsAsync(false).ReturnsAsync(true);
        _repo.Setup(r => r.SaveAsync(default)).ThrowsAsync(new ConcurrencyConflictException("TENANT_CREATE_CONFLICT", "lost"));

        await CreateService().BootstrapAsync("user-1", "alice", "alice@example.com");

        _repo.Verify(r => r.ExistsByOwnerAsync("user-1", default), Times.Exactly(2));
    }

    // The unique index refused the insert and the caller still owns nothing: the name belongs to another user.
    [Fact]
    public async Task Bootstrap_ANameAnotherUserHolds_IsTenantNameTaken()
    {
        _repo.Setup(r => r.ExistsByOwnerAsync("user-2", default)).ReturnsAsync(false);
        _repo.Setup(r => r.SaveAsync(default)).ThrowsAsync(new ConcurrencyConflictException("TENANT_CREATE_CONFLICT", "lost"));

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().BootstrapAsync("user-2", "alice", "other@example.com"));

        Assert.Equal("TENANT_NAME_TAKEN", ex.Code);
    }

    // ── ApproveDeclaredAccountAsync (self-heal, no admin) ────

    [Fact]
    public async Task ApproveDeclaredAccount_Pending_MakesItActive()
    {
        var tenant = MakeTenant(TenantStatus.PendingApproval);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().ApproveDeclaredAccountAsync(tenant.Id);

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // Idempotent — two requests of a declared account can race; the second finds it approved.
    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended)]
    public async Task ApproveDeclaredAccount_NonPending_NoOp(TenantStatus status)
    {
        var tenant = MakeTenant(status);
        _repo.Setup(r => r.FindByIdAsync(tenant.Id, default)).ReturnsAsync(tenant);

        await CreateService().ApproveDeclaredAccountAsync(tenant.Id);

        Assert.Equal(status, tenant.Status);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApproveDeclaredAccount_TenantNotFound_NoOp()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Tenant?)null);

        await CreateService().ApproveDeclaredAccountAsync(Guid.NewGuid());

        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Tenant MakeTenant(TenantStatus status)
    {
        if (status == TenantStatus.PendingApproval)
            return Tenant.CreatePending("acme", "user-1", "admin@acme.com");
        var tenant = TestTenant.Active("acme", "user-1", "admin@acme.com");
        if (status == TenantStatus.Suspended) tenant.Suspend();
        return tenant;
    }
}
