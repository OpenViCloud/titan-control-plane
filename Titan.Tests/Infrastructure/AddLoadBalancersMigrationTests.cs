using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Migrations;

namespace Titan.Tests.Infrastructure;

// The scaffold wrote defaultValue 0 for the new cap: every existing tenant would have been allowed no load balancer
// at all (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none). The hand edit is pinned here.
public class AddLoadBalancersMigrationTests
{
    [Fact]
    public void ExistingTenants_GetTheDefaultLoadBalancerCap()
    {
        AddColumnOperation column = new AddLoadBalancers().UpOperations
            .OfType<AddColumnOperation>()
            .Single(op => op.Name == "max_load_balancers");

        Assert.Equal("tenants", column.Table);
        Assert.Equal(TenantQuota.DefaultMaxLoadBalancers, column.DefaultValue);
    }

    // A network deleted before this migration has no release time: the allocator reads null as the oldest.
    [Fact]
    public void TheReleaseTime_IsNullable_SoOldRowsNeedNoBackfill()
    {
        AddColumnOperation column = new AddLoadBalancers().UpOperations
            .OfType<AddColumnOperation>()
            .Single(op => op.Name == "public_ipv6_released_at");

        Assert.Equal("networks", column.Table);
        Assert.True(column.IsNullable);
    }
}
