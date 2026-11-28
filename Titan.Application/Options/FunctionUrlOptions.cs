namespace Titan.Application.Options;

/// Where a function answers: "<scheme>://<Knative Service>.<domain>", the domain template of the function cluster's
/// Knative (titan-ops features/ovc-function/design.md#function-url). Dev: http and fn.localhost.
public class FunctionUrlOptions
{
    public const string SectionName = "FunctionUrl";

    public string Scheme { get; set; } = "https";
    public string Domain { get; set; } = "";

    /// Null until the first deploy names the Service, or while no domain is configured.
    public string? UrlOf(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId) || string.IsNullOrWhiteSpace(Domain))
        {
            return null;
        }
        return $"{Scheme}://{externalId}.{Domain}";
    }
}
