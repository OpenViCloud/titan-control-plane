using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Migrations;

namespace Titan.Tests.Infrastructure;

// The scaffold wrote defaultValue 0 for the new cap: every existing tenant would have been allowed no key at all
// (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none). The hand edit is pinned here.
public class AddSshKeysMigrationTests
{
    [Fact]
    public void ExistingTenants_GetTheDefaultSshKeyCap()
    {
        AddColumnOperation column = new AddSshKeys().UpOperations
            .OfType<AddColumnOperation>()
            .Single(op => op.Name == "max_ssh_keys");

        Assert.Equal("tenants", column.Table);
        Assert.Equal(TenantQuota.DefaultMaxSshKeys, column.DefaultValue);
    }
}
