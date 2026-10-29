using Titan.Domain.Aggregates.Platform.Tenant;

namespace Titan.Tests;

// The quota decision is inside the creation factories, so every checked factory needs a Tenant.
// Most tests are not about quota at all — they are about the OTHER invariant the
// factory carries (a VM must be Running to snapshot, a snapshot must exist on Incus to restore). Those
// tests use this: a tenant with room to spare, so quota never becomes the reason a test passes.
//
// Tests that ARE about quota build their own tenant with the cap they mean to hit. That split is
// deliberate — a shared "generous" tenant used in a quota test would hide the very thing under test.
internal static class TestTenant
{
    /// Default TenantQuota: 10 VMs, 10 volumes, 3 snapshots per VM/volume, 20 firewall rules,
    /// 10 public IPv6. Paired with a count of 0 at the call site, quota cannot fire.
    internal static Tenant WithRoomToSpare() => Active("test-tenant", "kc-test", "test@titan.local");

    /// An approved tenant: approval is activation (titan-ops architecture/tenancy-and-admin.md#approval-is-activation).
    internal static Tenant Active(string name, string ownerKeycloakId, string email, int maxVms = 10, int maxNetworks = 1)
    {
        Tenant tenant = Tenant.CreatePending(name, ownerKeycloakId, email, maxVms, maxNetworks);
        tenant.Approve();
        return tenant;
    }
}
