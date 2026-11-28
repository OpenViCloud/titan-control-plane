using Microsoft.Extensions.Options;

namespace Titan.Application.Options;

/// The bucket that keeps every function version's zip (titan-ops features/ovc-function/design.md#code-storage): a bucket of
/// its own, not the backup bucket. Endpoint, region and bucket in the Deployment env, the key pair in a Secret.
public class FunctionCodeOptions
{
    public const string SectionName = "FunctionCode";
    public const int MaxBucketLength = 63;
    public const int MaxEndpointLength = 200;

    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    /// What is wrong with the configured values, or null. The message names the setting, never a value: the key pair is in here.
    public string? Problem()
    {
        if (!IsValidEndpoint(Endpoint))
        {
            return $"{SectionName}__Endpoint must be an https URL of at most {MaxEndpointLength} characters, without credentials, path, query or fragment";
        }
        if (Region.Length == 0 || Region.Length > 64 || !Region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return $"{SectionName}__Region must be 1 to 64 letters, digits or hyphens";
        }
        bool bucketIsValid = Bucket.Length > 0 && Bucket.Length <= MaxBucketLength
            && Bucket.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' || c == '.');
        if (!bucketIsValid)
        {
            return $"{SectionName}__Bucket must be 1 to {MaxBucketLength} characters of a-z, 0-9, '-' or '.'";
        }
        if (!IsToken(AccessKeyId, 128))
        {
            return $"{SectionName}__AccessKeyId must be 1 to 128 characters without spaces";
        }
        if (!IsToken(SecretAccessKey, 256))
        {
            return $"{SectionName}__SecretAccessKey must be 1 to 256 characters without spaces";
        }
        return null;
    }

    private static bool IsValidEndpoint(string endpoint)
    {
        string trimmed = endpoint.Trim();
        // Uri.TryCreate tolerates spaces and control characters inside the string, and the raw string is what gets used.
        if (trimmed.Length == 0 || trimmed.Length > MaxEndpointLength || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }
        bool isAnOrigin = uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
        return isAnOrigin && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    private static bool IsToken(string value, int maxLength) =>
        value.Length > 0 && value.Length <= maxLength && !value.Any(char.IsWhiteSpace);
}

/// Required only where a function cluster is configured; elsewhere (prd today) the section is not read at all.
public sealed class FunctionCodeValidation(FunctionClusterOptions functionClusters) : IValidateOptions<FunctionCodeOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionCodeOptions options)
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
