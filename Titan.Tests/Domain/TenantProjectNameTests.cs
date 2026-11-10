using Titan.Domain.Aggregates.Tenant;

namespace Titan.Tests.Domain;

// titan-ops architecture/tenancy-and-admin.md#project-name. Titan names the tenant's Incus project from the tenant id, so a
// rebuilt cluster gets the same name back instead of a provider-assigned id that collides with an old one.
public class TenantProjectNameTests
{
    [Fact]
    public void ProjectName_IsTheTenantIdWithoutDashes_BehindAPrefix()
    {
        var t = Tenant.CreatePending("acme", "owner-1");

        string expected = "t-" + t.Id.ToString("N");
        Assert.Equal(expected, Tenant.ProjectNameFor(t.Id));
    }

    [Fact] // the oracle is the id alone: name, email and status do not take part
    public void ProjectName_DoesNotDependOnAnythingButTheId()
    {
        var t = Tenant.CreatePending("acme", "owner-1");
        string before = Tenant.ProjectNameFor(t.Id);

        t.Approve();
        t.Suspend();

        Assert.Equal(before, Tenant.ProjectNameFor(t.Id));
        Assert.Matches("^t-[0-9a-f]{32}$", Tenant.ProjectNameFor(t.Id));
    }

    [Fact]
    public void TwoTenants_GetTwoProjects()
    {
        var a = Tenant.CreatePending("acme", "owner-1");
        var b = Tenant.CreatePending("acme", "owner-2");

        Assert.NotEqual(Tenant.ProjectNameFor(a.Id), Tenant.ProjectNameFor(b.Id));
    }
}
