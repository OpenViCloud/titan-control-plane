namespace Titan.Domain.Aggregates.Platform.Tenant;

public enum TenantStatus
{
    // Signup awaiting approval: a platform-admin decision, unless the realm declares the account.
    PendingApproval,
    Active,
    Suspended
}
