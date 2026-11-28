using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Titan.Application.Options;

/// The code registry (titan-ops features/ovc-function/design.md#code-storage): the control plane pushes to PushUrl, the
/// function cluster's kubelets pull from PullHost. Two names for one registry, because the two sides reach it by different
/// routes (on dev the control plane runs in a VM, the kubelets on the host).
public partial class FunctionRegistryOptions
{
    public const string SectionName = "FunctionRegistry";

    /// The registry's origin as the control plane reaches it, e.g. http://host.lima.internal:5100.
    public string PushUrl { get; set; } = "";

    /// host[:port] in the image name a kubelet pulls, e.g. localhost:5100: no scheme, no path.
    public string PullHost { get; set; } = "";

    public string? Problem()
    {
        if (!IsValidPushUrl(PushUrl))
        {
            return $"{SectionName}__PushUrl must be an http or https URL of a registry origin, without credentials, path, query or fragment";
        }
        if (!PullHostPattern().IsMatch(PullHost))
        {
            return $"{SectionName}__PullHost must be a lower-case host name with an optional port, without scheme or path";
        }
        return null;
    }

    private static bool IsValidPushUrl(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }
        bool isHttp = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        bool isAnOrigin = uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
        return isHttp && isAnOrigin && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    // An image reference's registry part: a containerd name with upper case or a scheme is not a pullable reference.
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:[0-9]{1,5})?$")]
    private static partial Regex PullHostPattern();
}

/// Required only where a function cluster is configured; elsewhere (prd today) the section is not read at all.
public sealed class FunctionRegistryValidation(FunctionClusterOptions functionClusters) : IValidateOptions<FunctionRegistryOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionRegistryOptions options)
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
