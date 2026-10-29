using Titan.Domain.Exceptions;
namespace Titan.Domain.Aggregates.Platform.Tenant;

public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public TenantStatus Status { get; private set; }
    /// titan-ops architecture/tenancy-and-admin.md#tenant-quota — read by the EnsureCan* rules below.
    public TenantQuota Quota { get; private set; } = TenantQuota.Default;

    /// Whole VND. A reference number the admin reads at month-end close; it blocks nothing
    /// (titan-ops architecture/money-and-wallet.md#postpaid-no-brake).
    public long DebtCeiling { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// The tenant's Incus project, the same on every cluster (titan-ops architecture/tenancy-and-admin.md#project-name).
    /// Derived from the id so a rebuilt cluster gets the same name back.
    public string ProjectName => ProjectNameFor(Id);

    /// For a handler that holds the tenant's id but not the tenant.
    public static string ProjectNameFor(Guid tenantId) => "t-" + tenantId.ToString("N");

    private readonly List<Member> _members = [];
    public IReadOnlyCollection<Member> Members => _members;

    public bool IsActive => Status == TenantStatus.Active;

    private Tenant() { }

    /// Frozen onto each row at creation: changing this constant does not rewrite existing tenants, because a
    /// ceiling is a per-customer judgement, not a global switch.
    public const long DefaultDebtCeiling = 500_000;

    // Every signup lands here; the middleware approves one whose account has the realm role auto-approve in the same request,
    // every other waits for an admin (titan-ops architecture/tenancy-and-admin.md#approval-is-activation).
    public static Tenant CreatePending(string name, string ownerKeycloakId, string email, int maxVms = 10, int maxNetworks = 1)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email,
            Status = TenantStatus.PendingApproval,
            Quota = TenantQuota.Default.WithVmAndNetworkCaps(maxVms, maxNetworks),
            DebtCeiling = DefaultDebtCeiling,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        tenant._members.Add(Member.Create(tenant.Id, ownerKeycloakId));
        return tenant;
    }

    // Approval is activation: there is no provider account to wait for. The caller opens the wallet first.
    public void Approve()
    {
        if (Status != TenantStatus.PendingApproval)
            throw new Titan.Domain.Exceptions.BusinessException("INVALID_STATUS", "Only a pending-approval tenant can be approved.");
        Status = TenantStatus.Active;
    }

    /// BadRequestException (400), not BusinessException (422), to keep the status code of
    /// PATCH /v1/tenants/{id}/status.
    public void Suspend()
    {
        if (Status == TenantStatus.Suspended)
            throw new BadRequestException("INVALID_STATUS", $"Tenant is already {TenantStatus.Suspended}.");
        Status = TenantStatus.Suspended;
    }

    /// The admin's "make this tenant Active", an intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation):
    /// a pending, rejected or suspended tenant becomes Active. A repeat is refused
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat); the handler recognises it first.
    public void RequestActivation()
    {
        if (Status == TenantStatus.Active)
        {
            throw new BadRequestException("INVALID_STATUS", $"Tenant is already {TenantStatus.Active}.");
        }

        Status = TenantStatus.Active;
    }

    /// Changes nothing about what the tenant can do: it is read at month-end close, not at create time.
    public void UpdateDebtCeiling(long debtCeiling)
    {
        if (debtCeiling < 0)
            throw new Titan.Domain.Exceptions.BusinessException(
                "INVALID_DEBT_CEILING", "Debt ceiling must be non-negative.");
        DebtCeiling = debtCeiling;
    }

    // Quota is a create-time gate: lowering it below current usage removes nothing, it only stops new ones.
    public void UpdateQuota(TenantQuota quota)
        => Quota = quota ?? throw new Titan.Domain.Exceptions.BusinessException(
            "INVALID_QUOTA", "Quota must be provided.");

    // The caller counts (a repository query, not part of this aggregate); the tenant decides
    // (titan-ops architecture/code-structure.md#tenant-typed-edge). `internal`, called only from the factories of the
    // things being created, so the compiler stops Titan.Application from skipping the check.

    internal void EnsureCanCreateVm(int activeVmCount)
        => Refuse(activeVmCount, Quota.MaxVms, "VM_QUOTA_EXCEEDED", "VM quota exceeded.");

    internal void EnsureCanCreateNetwork(int activeNetworkCount)
        => Refuse(activeNetworkCount, Quota.MaxNetworks, "NETWORK_QUOTA_EXCEEDED", "Network quota exceeded.");

    internal void EnsureCanCreateVolume(int activeVolumeCount)
        => Refuse(activeVolumeCount, Quota.MaxVolumes, "VOLUME_QUOTA_EXCEEDED",
            $"Volume quota exceeded (max {Quota.MaxVolumes}).");

    /// activeSnapshotCount is the count on one VM: the cap is per VM, not per tenant.
    internal void EnsureCanCreateVmSnapshot(int activeSnapshotCount)
        => Refuse(activeSnapshotCount, Quota.MaxVmSnapshotsPerVm, "VM_SNAPSHOT_QUOTA_EXCEEDED",
            $"VM snapshot quota exceeded (max {Quota.MaxVmSnapshotsPerVm}).");

    /// activeSnapshotCount is the count on one volume: the cap is per volume.
    internal void EnsureCanCreateVolumeSnapshot(int activeSnapshotCount)
        => Refuse(activeSnapshotCount, Quota.MaxVolumeSnapshotsPerVolume, "VOLUME_SNAPSHOT_QUOTA_EXCEEDED",
            $"Snapshot quota exceeded (max {Quota.MaxVolumeSnapshotsPerVolume} per volume).");

    /// ruleCount is the count on one network: the cap is per network.
    internal void EnsureCanCreateFirewallRule(int ruleCount)
        => Refuse(ruleCount, Quota.MaxFirewallRulesPerNetwork, "FIREWALL_QUOTA_EXCEEDED",
            $"Firewall rule quota of {Quota.MaxFirewallRulesPerNetwork} per network has been reached.");

    internal void EnsureCanCreateSshKey(int keyCount)
        => Refuse(keyCount, Quota.MaxSshKeys, "SSH_KEY_QUOTA_EXCEEDED",
            $"SSH key quota exceeded (max {Quota.MaxSshKeys}).");

    internal void EnsureCanCreateLoadBalancer(int activeLoadBalancerCount)
        => Refuse(activeLoadBalancerCount, Quota.MaxLoadBalancers, "LOAD_BALANCER_QUOTA_EXCEEDED",
            $"Load balancer quota exceeded (max {Quota.MaxLoadBalancers}).");

    internal void EnsureCanEnablePublicIpv6(int activeIpv6Count)
        => Refuse(activeIpv6Count, Quota.MaxPublicIpv6, "IPV6_QUOTA_EXCEEDED",
            $"Public IPv6 quota exceeded (max {Quota.MaxPublicIpv6}).");

    // `>=`: the count is what already exists, so a cap of 0 refuses the first one
    // (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none). Reading 0 as "unlimited" would grant infinite
    // resources at the moment an admin tried to cut them off.
    private static void Refuse(int current, int cap, string code, string message)
    {
        if (current >= cap)
            throw new Titan.Domain.Exceptions.BusinessException(code, message);
    }
}
