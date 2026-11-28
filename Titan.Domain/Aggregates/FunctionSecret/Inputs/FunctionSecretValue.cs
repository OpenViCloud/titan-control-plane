namespace Titan.Domain.Aggregates.FunctionSecret;

/// What the cipher made of one value, handed to FunctionSecret.Create and AddVersion: the number it was encrypted under
/// (part of what the ciphertext is bound to), the key, the nonce, the ciphertext with its tag, and the value's UTF-8
/// length. Never the value (titan-ops features/ovc-function/design.md#function-secrets).
public sealed record FunctionSecretValue(int Number, string KeyId, byte[] Nonce, byte[] Ciphertext, int ValueBytes);
