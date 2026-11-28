using Microsoft.Extensions.Options;

namespace Titan.Application.Options;

/// The master keys of function secrets (titan-ops features/ovc-function/design.md#function-secrets):
/// FunctionSecrets__Keys__<keyId> is the base64 of 32 bytes, from the control plane's Secret; new values are encrypted
/// under FunctionSecrets__ActiveKeyId, and each row names the key it was encrypted under, so a key added later takes the
/// new values while the old one still decrypts its rows.
public class FunctionSecretsOptions
{
    public const string SectionName = "FunctionSecrets";
    public const int KeyBytes = 32;

    public string ActiveKeyId { get; set; } = "";

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// What stops the host, naming the setting, never a key; null when the keys can work.
    public string? Problem()
    {
        if (Keys.Count == 0)
        {
            return $"{SectionName}__Keys__<keyId> must hold at least one key, the base64 of {KeyBytes} bytes";
        }
        foreach ((string keyId, string value) in Keys)
        {
            if (FindKeyBytes(value) == null)
            {
                return $"{SectionName}__Keys__{keyId} must be the base64 of exactly {KeyBytes} bytes";
            }
        }
        if (string.IsNullOrWhiteSpace(ActiveKeyId))
        {
            return $"{SectionName}__ActiveKeyId must name one of {SectionName}__Keys";
        }
        if (!Keys.ContainsKey(ActiveKeyId))
        {
            return $"{SectionName}__ActiveKeyId names no key of {SectionName}__Keys";
        }
        return null;
    }

    /// The key's bytes, or null when the setting is not the base64 of exactly KeyBytes bytes.
    public static byte[]? FindKeyBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        byte[] buffer = new byte[KeyBytes + 3];
        if (!Convert.TryFromBase64String(value.Trim(), buffer, out int written) || written != KeyBytes)
        {
            return null;
        }
        return buffer[..KeyBytes];
    }
}

/// Required at start only where a function cluster is configured, as FunctionRegistry. Without a cluster (prd today) the
/// host starts with no key, and a secret's create or new version then fails as a 500 naming the missing setting: no
/// function can use a secret there.
public sealed class FunctionSecretsValidation(FunctionClusterOptions functionClusters) : IValidateOptions<FunctionSecretsOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionSecretsOptions options)
    {
        if (functionClusters.Clusters.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }
        string? problem = options.Problem();
        return problem is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{problem} (required while {FunctionClusterOptions.SectionName} names a cluster)");
    }
}
