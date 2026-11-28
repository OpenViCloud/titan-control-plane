using Titan.Domain.Aggregates.FunctionSecret;

namespace Titan.Application.Abstractions;

// Encrypts a function secret's value at rest (titan-ops features/ovc-function/design.md#function-secrets): AES-256-GCM
// under the active master key, bound to the tenant, the secret's name and the version number, so a ciphertext copied to
// another secret, tenant or number does not decrypt. Neither method writes a value or a key to an exception or a log.
public interface IFunctionSecretCipher
{
    /// The value encrypted as version `version` of the tenant's secret `secretName`, under a fresh nonce.
    FunctionSecretValue Encrypt(Guid tenantId, string secretName, int version, string plaintext);

    /// The value of one stored version, decrypted with the key the row names (an older key still decrypts its rows).
    string Decrypt(Guid tenantId, string secretName, int version, string keyId, byte[] nonce, byte[] ciphertext);
}
