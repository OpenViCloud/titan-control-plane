using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.FunctionSecret;

/// A tenant's named secret for its functions (titan-ops features/ovc-function/design.md#function-secrets): immutable
/// versions numbered 1, 2, 3…, each holding only the ciphertext of its value. Titan-only: nothing of it is on the function
/// cluster until a deploy hands the worker a version a function version points at. Deleting it removes the row and its
/// versions.
public partial class FunctionSecret : IAggregateRoot
{
    /// The name rule of an environment variable (Function.EnvNamePattern): a secret is used as one.
    public const int NameMaxLength = 128;
    public const string NamePattern = "^[A-Za-z_][A-Za-z0-9_]*$";

    // The limits of titan-ops features/ovc-function/design.md#function-secrets. Constants, not TenantQuota caps, as
    // Function's.
    public const int MaxPerTenant = 50;
    /// UTF-8 bytes of one value.
    public const int MaxValueBytes = 4096;
    /// The newest versions kept, plus every version a kept function version points at.
    public const int KeptVersions = 10;
    /// AES-GCM's nonce and tag, in bytes.
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<FunctionSecretVersion> _versions = [];
    public IReadOnlyCollection<FunctionSecretVersion> Versions => _versions;

    private FunctionSecret() { }

    /// The secret with its first version, number 1. The name is the handler's to check
    /// (titan-ops architecture/domain-methods.md#names-and-sizes).
    public static FunctionSecret Create(Guid tenantId, string name, FunctionSecretValue first, string createdBy)
    {
        var secret = new FunctionSecret
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        secret.Append(first, createdBy);
        return secret;
    }

    private static readonly System.Text.RegularExpressions.Regex NameShape =
        new(NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    /// The shape rule of a secret name (titan-ops architecture/domain-methods.md#names-and-sizes).
    public static bool IsAName(string? name) =>
        name != null && name.Length > 0 && name.Length <= NameMaxLength && NameShape.IsMatch(name);

    /// A value fits when it is 1 to MaxValueBytes UTF-8 bytes.
    public static bool IsAValueSize(int valueBytes) => valueBytes >= 1 && valueBytes <= MaxValueBytes;

    /// The number the next version takes: the value is encrypted under it before the version exists, since the number
    /// is part of what the ciphertext is bound to.
    public int NextVersionNumber => _versions.Count == 0 ? 1 : _versions.Max(v => v.Number) + 1;

    /// The newest version's number: what "latest" is pinned to when a function version is made.
    public int LatestVersionNumber => _versions.Max(v => v.Number);

    /// The version of that number, or null when it was never made or is no longer kept.
    public FunctionSecretVersion? FindVersion(int number) => _versions.FirstOrDefault(v => v.Number == number);

    private FunctionSecretVersion Append(FunctionSecretValue value, string createdBy)
    {
        if (value.Number != NextVersionNumber)
        {
            throw new ArgumentException($"the next version is {NextVersionNumber}, not {value.Number}", nameof(value));
        }
        if (value.Nonce.Length != NonceBytes)
        {
            throw new ArgumentException($"a nonce is {NonceBytes} bytes", nameof(value));
        }
        if (value.Ciphertext.Length < TagBytes)
        {
            throw new ArgumentException($"a ciphertext ends with its {TagBytes}-byte tag", nameof(value));
        }
        if (!IsAValueSize(value.ValueBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.ValueBytes, $"a value is 1 to {MaxValueBytes} bytes");
        }
        var version = FunctionSecretVersion.Create(Id, value, createdBy, DateTimeOffset.UtcNow);
        _versions.Add(version);
        return version;
    }
}
