using Microsoft.Extensions.Options;

namespace Titan.Infrastructure.Auth;

// The server key of the Idempotency request hash, from a Secret (titan-ops architecture/http-contract.md#idempotency-key):
// a request body may carry a console password, and a plain SHA-256 of a short body is guessed offline.
public sealed class IdempotencyHashOptions
{
    public const string SectionName = "Idempotency";
    private const int MinKeyBytes = 32;

    public string HashKey { get; set; } = "";

    public byte[] KeyBytes() => Convert.FromHexString(HashKey);

    /// Null when the control plane may start with this key. The text names the setting, never a value.
    public static string? ProblemWith(IdempotencyHashOptions options)
    {
        bool isHex = options.HashKey.Length % 2 == 0 && options.HashKey.All(Uri.IsHexDigit);
        if (!isHex || options.HashKey.Length < MinKeyBytes * 2)
        {
            return $"{SectionName}:HashKey must be at least {MinKeyBytes} bytes in hex.";
        }
        return null;
    }
}

public sealed class IdempotencyHashValidation : IValidateOptions<IdempotencyHashOptions>
{
    public ValidateOptionsResult Validate(string? name, IdempotencyHashOptions options)
    {
        string? problem = IdempotencyHashOptions.ProblemWith(options);
        if (problem == null)
        {
            return ValidateOptionsResult.Success;
        }
        return ValidateOptionsResult.Fail(problem);
    }
}
