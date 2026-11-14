using System.Text;
using Microsoft.Extensions.Options;

namespace Titan.Infrastructure.Auth;

// The server key of the Idempotency request hash, from a Secret (titan-ops architecture/http-contract.md#idempotency-key):
// a request body may carry a console password, and a plain SHA-256 of a short body is guessed offline. The key is the
// UTF-8 bytes of a random string: dev makes 64 hex characters, prd's Terraform 48 letters and digits.
public sealed class IdempotencyHashOptions
{
    public const string SectionName = "Idempotency";
    private const int MinKeyLength = 32;

    public string HashKey { get; set; } = "";

    public byte[] KeyBytes() => Encoding.UTF8.GetBytes(HashKey);

    /// Null when the control plane may start with this key. The text names the setting, never a value.
    public static string? ProblemWith(IdempotencyHashOptions options)
    {
        if (options.HashKey.Length < MinKeyLength)
        {
            return $"{SectionName}:HashKey must be at least {MinKeyLength} characters.";
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
