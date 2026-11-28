using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.FunctionSecret;

/// One value of a secret, immutable (titan-ops features/ovc-function/design.md#function-secrets): only its ciphertext,
/// bound to the tenant, the secret's name and this number, and the master key it was encrypted under. A child of
/// FunctionSecret, made only by FunctionSecret.Create and AddVersion, removed only when it falls out of the kept versions.
public class FunctionSecretVersion : IEntity
{
    public Guid SecretId { get; private set; }
    /// 1, 2, 3… in the order the values were given.
    public int Number { get; private set; }
    /// The master key (FunctionSecrets:Keys:<keyId>) the value was encrypted under; old keys still decrypt their rows.
    public string KeyId { get; private set; } = default!;
    /// AES-GCM's 12-byte nonce, fresh for every encryption.
    public byte[] Nonce { get; private set; } = default!;
    /// The encrypted value followed by its 16-byte tag.
    public byte[] Ciphertext { get; private set; } = default!;
    /// UTF-8 length of the value, counted toward a function version's configuration limit without decrypting.
    public int ValueBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// The member who gave the value, as the activity feed records them.
    public string CreatedBy { get; private set; } = default!;

    private FunctionSecretVersion() { }

    internal static FunctionSecretVersion Create(Guid secretId, FunctionSecretValue value, string createdBy, DateTimeOffset at) => new()
    {
        SecretId = secretId,
        Number = value.Number,
        KeyId = value.KeyId,
        Nonce = value.Nonce,
        Ciphertext = value.Ciphertext,
        ValueBytes = value.ValueBytes,
        CreatedAt = at,
        CreatedBy = createdBy,
    };
}
