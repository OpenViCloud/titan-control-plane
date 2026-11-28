using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Domain.Aggregates.FunctionSecret;

namespace Titan.Infrastructure.FunctionSecrets;

// AES-256-GCM at rest (titan-ops features/ovc-function/design.md#function-secrets): a fresh 12-byte nonce for every
// encryption, the 16-byte tag after the ciphertext, and the tenant, the secret's name and the version as associated
// data, so a row moved to another tenant, name or number fails its tag. New values take the active key; a row names its
// key, so an older key still decrypts. No message here carries a value or a key.
public sealed class AesGcmFunctionSecretCipher(IOptions<FunctionSecretsOptions> options) : IFunctionSecretCipher
{
    public FunctionSecretValue Encrypt(Guid tenantId, string secretName, int version, string plaintext)
    {
        FunctionSecretsOptions keys = options.Value;
        string keyId = keys.ActiveKeyId;
        byte[] key = KeyOf(keys, keyId);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(FunctionSecret.NonceBytes);
        byte[] sealedValue = new byte[plain.Length + FunctionSecret.TagBytes];
        try
        {
            using var aes = new AesGcm(key, FunctionSecret.TagBytes);
            aes.Encrypt(nonce, plain, sealedValue.AsSpan(0, plain.Length), sealedValue.AsSpan(plain.Length),
                AssociatedData(tenantId, secretName, version));
            return new FunctionSecretValue(version, keyId, nonce, sealedValue, plain.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Decrypt(Guid tenantId, string secretName, int version, string keyId, byte[] nonce, byte[] ciphertext)
    {
        if (ciphertext.Length < FunctionSecret.TagBytes)
        {
            throw new CryptographicException("A function secret's ciphertext is shorter than its tag.");
        }
        byte[] key = KeyOf(options.Value, keyId);
        int length = ciphertext.Length - FunctionSecret.TagBytes;
        byte[] plain = new byte[length];
        try
        {
            using var aes = new AesGcm(key, FunctionSecret.TagBytes);
            aes.Decrypt(nonce, ciphertext.AsSpan(0, length), ciphertext.AsSpan(length), plain,
                AssociatedData(tenantId, secretName, version));
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// `{tenantId}|{secretName}|{version}`, the tenant as "D", in UTF-8.
    internal static byte[] AssociatedData(Guid tenantId, string secretName, int version) =>
        Encoding.UTF8.GetBytes($"{tenantId:D}|{secretName}|{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

    // A copy, zeroed after use; the message names the setting, never its value.
    private static byte[] KeyOf(FunctionSecretsOptions keys, string keyId)
    {
        if (string.IsNullOrEmpty(keyId) || !keys.Keys.TryGetValue(keyId, out string? encoded))
        {
            throw new InvalidOperationException(
                $"{FunctionSecretsOptions.SectionName}__Keys__{keyId} is not configured: function secrets cannot be encrypted or decrypted.");
        }
        return FunctionSecretsOptions.FindKeyBytes(encoded)
            ?? throw new InvalidOperationException(
                $"{FunctionSecretsOptions.SectionName}__Keys__{keyId} is not the base64 of {FunctionSecretsOptions.KeyBytes} bytes.");
    }
}
