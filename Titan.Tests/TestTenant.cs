using Titan.Domain.Aggregates.Tenant;

namespace Titan.Tests;

// A tenant the way a signup and an approval make one. A handler test about a cap sets the cap it means to hit on it
// (titan-ops architecture/code-structure.md#quota-gate).
internal static class TestTenant
{
    /// Default TenantQuota: 10 VMs, 10 volumes, 3 snapshots per VM/volume, 20 firewall rules, 10 public IPv6.
    internal static Tenant WithRoomToSpare() => Active("test-tenant", "kc-test");

    /// An approved tenant: approval is activation (titan-ops architecture/tenancy-and-admin.md#approval-is-activation).
    internal static Tenant Active(string name, string ownerKeycloakId, int maxVms = 10, int maxNetworks = 1)
    {
        Tenant tenant = Tenant.CreatePending(name, ownerKeycloakId, maxVms, maxNetworks);
        tenant.Approve();
        return tenant;
    }
}
