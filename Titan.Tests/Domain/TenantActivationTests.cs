using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#approval-is-activation. A tenant has no provider account any more: its
// Incus project is created per cluster when its first resource there is, so approving it makes it Active at once.
public class TenantActivationTests
{
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

    [Theory] // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat — the handler recognises the repeat first
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

        t.RequestActivation();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Fact] // a rejected signup is a suspended pending one; activating it approves it
    public void RequestActivation_OnARejectedSignup_MakesItActive()
    {
        var t = Pending();
        t.Suspend();

        t.RequestActivation();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Fact]
    public void RequestActivation_OnASuspendedTenant_BringsItBack()
    {
        var t = Active();
        t.Suspend();

        t.RequestActivation();

        Assert.Equal(TenantStatus.Active, t.Status);
    }

    [Fact]
    public void RequestActivation_OnAnActiveTenant_IsRefused()
    {
        var t = Active();

        var ex = Assert.Throws<BadRequestException>(() => t.RequestActivation());

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public void Suspend_Twice_IsRefused()
    {
        var t = Active();
        t.Suspend();

        var ex = Assert.Throws<BadRequestException>(() => t.Suspend());

        Assert.Equal("INVALID_STATUS", ex.Code);
    }
}
