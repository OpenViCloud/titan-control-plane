using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Domain.Aggregates.SshKey;

/// A public key a tenant saved to put into VMs at create (titan-ops features/vm/design.md#ssh-key). Titan-only: no
/// provider object, so no desired/current state; deleting it removes the row and leaves VMs that already have it.
public class SshKey : AggregateRoot
{
    public const int MaxNameLength = 64;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Type { get; private set; } = default!;

    /// `<type> <base64 blob>` without the comment (SshPublicKey.Line).
    public string PublicKey { get; private set; } = default!;
    public string Fingerprint { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private SshKey() { }

    /// keyCount is how many keys the tenant already has; the tenant decides
    /// (titan-ops architecture/code-structure.md#tenant-typed-edge).
    public static SshKey Create(TenantAggregate tenant, int keyCount, string name, string publicKeyText)
    {
        tenant.EnsureCanCreateSshKey(keyCount);
        return CreateUnchecked(tenant.Id, name, publicKeyText);
    }

    /// Test seam — internal, reachable only from Titan.Tests. See Vm.CreateUnchecked.
    internal static SshKey CreateUnchecked(Guid tenantId, string name, string publicKeyText)
    {
        string trimmedName = name.Trim();
        if (trimmedName.Length == 0 || trimmedName.Length > MaxNameLength)
        {
            throw new BadRequestException("INVALID_SSH_KEY_NAME", $"The key name must be 1 to {MaxNameLength} characters.");
        }

        SshPublicKey key = SshPublicKey.Parse(publicKeyText);
        return new SshKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = trimmedName,
            Type = key.Type,
            PublicKey = key.Line,
            Fingerprint = key.Fingerprint,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
