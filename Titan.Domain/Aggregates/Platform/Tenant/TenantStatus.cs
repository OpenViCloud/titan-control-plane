namespace Titan.Domain.Aggregates.Platform.Tenant;

public enum TenantStatus
{
    // Signup awaiting approval: a platform-admin decision, unless the account has the realm role auto-approve.
    PendingApproval,
    Active,
    Suspended
}
