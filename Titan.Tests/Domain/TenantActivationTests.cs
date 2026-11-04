using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Tests.Domain;

// titan-ops architecture/tenancy-and-admin.md#approval-is-activation. A tenant has no provider account any more: its
// Incus project is created per cluster when its first resource there is, so approving it makes it Active at once, and
// TenantActivation opens its wallet in the same save. Silent about the lock on the tenant row;
// FirstSignInDbTests.TwoFirstRequestsOfADeclaredAccount_TakeTurns_OneWallet_AndBothPass runs two at once on Postgres.
public class TenantActivationTests
{
    private readonly Mock<IWalletRepository> _wallets = new();

    private TenantActivation Activation() => new(_wallets.Object);

    private void HasAWallet(Tenant tenant) =>
        _wallets.Setup(w => w.GetByTenantForUpdateAsync(tenant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Wallet.Open(tenant.Id, "VND"));

    private static Tenant Pending() => Tenant.CreatePending("acme", "owner-1", "admin@acme.com");

    private static Tenant Active()
    {
        var t = Pending();
        t.Approve();
        return t;
    }

    [Fact]
    public void CreatePending_WaitsForApproval()
    {
        Assert.Equal(TenantStatus.PendingApproval, Pending().Status);
    }

    [Fact]
    public void Approve_OnAPendingSignup_MakesItActive()
    {
        var t = Pending();

        t.Approve();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Theory] // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat — the handler recognises the repeat first
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended)]
    public void Approve_OnATenantThatIsNotPending_IsRefused(TenantStatus from)
    {
        var t = Active();
        if (from == TenantStatus.Suspended)
        {
            t.Suspend();
        }

        var ex = Assert.Throws<BusinessException>(() => t.Approve());

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(from, t.Status);
    }

    [Fact]
    public void RequestActivation_OnAPendingSignup_MakesItActive()
    {
        var t = Pending();

        t.Activate();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Fact] // a rejected signup is a suspended pending one; activating it approves it
    public void RequestActivation_OnARejectedSignup_MakesItActive()
    {
        var t = Pending();
        t.Suspend();

        t.Activate();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Fact]
    public void RequestActivation_OnASuspendedTenant_BringsItBack()
    {
        var t = Active();
        t.Suspend();

        t.Activate();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Fact]
    public void RequestActivation_OnAnActiveTenant_IsRefused()
    {
        var t = Active();

        var ex = Assert.Throws<BusinessException>(() => t.Activate());

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public void Suspend_Twice_IsRefused()
    {
        var t = Active();
        t.Suspend();

        var ex = Assert.Throws<BusinessException>(() => t.Suspend());

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    // ── TenantActivation: the tenant and its wallet ──────────────────────────

    [Fact]
    public async Task ApproveThroughTheService_OpensAnEmptyWallet_AndTheTenantIsActive()
    {
        var t = Pending();
        var added = new List<Wallet>();
        _wallets.Setup(w => w.Add(It.IsAny<Wallet>())).Callback<Wallet>(added.Add);

        await Activation().ApproveAsync(t, "VND");

        Assert.Equal(TenantStatus.Active, t.Status);
        Wallet wallet = Assert.Single(added);
        Assert.Equal(t.Id, wallet.TenantId);
        Assert.Equal("VND", wallet.Currency);
        Assert.Equal(0L, wallet.AvailableAmount);
    }

    [Fact]
    public async Task ApproveThroughTheService_ATenantThatIsNotPending_IsRefused_AndOpensNoWallet()
    {
        var t = Active();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => Activation().ApproveAsync(t, "VND"));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _wallets.Verify(w => w.Add(It.IsAny<Wallet>()), Times.Never);
    }

    [Fact]
    public async Task ActivateThroughTheService_ASuspendedTenant_KeepsTheWalletItHad()
    {
        var t = Active();
        t.Suspend();
        HasAWallet(t);

        await Activation().ActivateAsync(t, "VND");

        Assert.Equal(TenantStatus.Active, t.Status);
        _wallets.Verify(w => w.Add(It.IsAny<Wallet>()), Times.Never);
    }

    [Fact]
    public async Task ActivateThroughTheService_APendingSignup_OpensItsWallet()
    {
        var t = Pending();
        var added = new List<Wallet>();
        _wallets.Setup(w => w.Add(It.IsAny<Wallet>())).Callback<Wallet>(added.Add);

        await Activation().ActivateAsync(t, "VND");

        Assert.Equal(TenantStatus.Active, t.Status);
        Assert.Equal(t.Id, Assert.Single(added).TenantId);
    }

    [Fact]
    public async Task ActivateThroughTheService_AnActiveTenant_IsRefused_AndOpensNoWallet()
    {
        var t = Active();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => Activation().ActivateAsync(t, "VND"));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _wallets.Verify(w => w.Add(It.IsAny<Wallet>()), Times.Never);
    }

    // The compiler keeps Titan.Application off these two, so TenantActivation is the only way a tenant becomes Active.
    [Fact]
    public void OnlyTenantActivationMakesATenantActive()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] publicMethods = [.. typeof(Tenant).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(Tenant.Suspend), publicMethods);
        Assert.DoesNotContain(nameof(Tenant.Approve), publicMethods);
        Assert.DoesNotContain(nameof(Tenant.Activate), publicMethods);
    }
}
