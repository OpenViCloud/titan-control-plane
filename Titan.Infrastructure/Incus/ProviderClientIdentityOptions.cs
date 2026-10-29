using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Titan.Infrastructure.Incus;

// Titan's client identity toward every Incus cluster, from a Secret (titan-ops features/cluster/design.md#provider-client-identity).
public sealed class ProviderClientIdentityOptions
{
    public const string SectionName = "ProviderClientIdentity";

    public string CertificatePem { get; set; } = "";
    public string KeyPem { get; set; } = "";

    public bool IsSet => !string.IsNullOrWhiteSpace(CertificatePem) || !string.IsNullOrWhiteSpace(KeyPem);

    /// Null when the control plane may start with these values. The text names the setting, never a value.
    public static string? ProblemWith(ProviderClientIdentityOptions options, bool clustersConfigured)
    {
        if (!options.IsSet)
        {
            if (clustersConfigured)
            {
                return $"{SectionName}:CertificatePem and {SectionName}:KeyPem must be set: a cluster is configured and Titan reaches it with this identity.";
            }
            return null;
        }
        if (string.IsNullOrWhiteSpace(options.CertificatePem) || string.IsNullOrWhiteSpace(options.KeyPem))
        {
            return $"{SectionName}:CertificatePem and {SectionName}:KeyPem must be set together.";
        }
        try
        {
            using X509Certificate2 _ = X509Certificate2.CreateFromPem(options.CertificatePem, options.KeyPem);
            return null;
        }
        catch (CryptographicException)
        {
            return $"{SectionName}: the certificate and the key do not form one identity (unreadable PEM, or a key of another certificate).";
        }
        catch (ArgumentException)
        {
            return $"{SectionName}: the certificate and the key do not form one identity (unreadable PEM, or a key of another certificate).";
        }
    }
}

public sealed class ProviderClientIdentityValidation(bool clustersConfigured) : IValidateOptions<ProviderClientIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderClientIdentityOptions options)
    {
        string? problem = ProviderClientIdentityOptions.ProblemWith(options, clustersConfigured);
        if (problem != null)
        {
            return ValidateOptionsResult.Fail(problem);
        }
        return ValidateOptionsResult.Success;
    }
}
