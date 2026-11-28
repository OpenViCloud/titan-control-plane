using System.Text;

namespace Titan.Domain.Aggregates.Function;

public partial class Function
{
    /// An environment variable's name (titan-ops features/ovc-function/design.md#function-env); a secret's name follows
    /// the same rule (FunctionSecret.NamePattern).
    public const int EnvNameMaxLength = 128;
    public const string EnvNamePattern = "^[A-Za-z_][A-Za-z0-9_]*$";
    /// The names Titan and Knative set in the container themselves.
    public const string ReservedEnvPrefix = "TITAN_";
    public static readonly IReadOnlyList<string> ReservedEnvNames = ["PORT", "K_SERVICE", "K_REVISION", "K_CONFIGURATION"];
    /// UTF-8 bytes of a version's variables and secrets together: every name and value, and every secret's variable
    /// name and value (titan-ops features/ovc-function/design.md#function-limits).
    public const int MaxConfigBytes = 8192;

    private static readonly System.Text.RegularExpressions.Regex EnvNameShape =
        new(EnvNamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    /// The shape rule of a variable's name.
    public static bool IsAnEnvName(string? name) =>
        name != null && name.Length > 0 && name.Length <= EnvNameMaxLength && EnvNameShape.IsMatch(name);

    /// A name the container already has, from Titan (TITAN_*) or from Knative; a version may not set it.
    public static bool IsAReservedEnvName(string name) =>
        name.StartsWith(ReservedEnvPrefix, StringComparison.Ordinal) || ReservedEnvNames.Contains(name, StringComparer.Ordinal);

    /// What a version's variables and secrets weigh against MaxConfigBytes: each secret counts its variable's name and
    /// its value's length (FunctionSecretVersion.ValueBytes), never decrypted for this.
    public static int ConfigBytes(IReadOnlyDictionary<string, string> env, IEnumerable<(string EnvName, int ValueBytes)> secrets)
    {
        int total = 0;
        foreach ((string name, string value) in env)
        {
            total += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
        }
        foreach ((string envName, int valueBytes) in secrets)
        {
            total += Encoding.UTF8.GetByteCount(envName) + valueBytes;
        }
        return total;
    }

    /// ConfigBytes within the limit.
    public static bool IsAConfigSize(int configBytes) => configBytes >= 0 && configBytes <= MaxConfigBytes;

    /// The versions a secret's number must outlive: the kept versions of this function, while it is not Deleted, that
    /// point at the secret (titan-ops features/ovc-function/design.md#function-secrets).
    public IEnumerable<FunctionVersion> KeptVersionsPointingAt(Guid secretId)
    {
        if (IsDeleted)
        {
            return [];
        }
        return _versions.Where(v => !v.IsDiscarded && v.SecretRefs.Any(r => r.SecretId == secretId));
    }

    /// The function's running or deploying version points at the secret: deleting it would leave that version, which
    /// the cluster runs or is about to, unable to deploy again.
    public bool RunsOrDeploysWithSecret(Guid secretId)
    {
        if (IsDeleted)
        {
            return false;
        }
        return _versions.Any(v => (v.Id == RunningVersionId || v.Id == DeployingVersionId)
                                  && v.SecretRefs.Any(r => r.SecretId == secretId));
    }
}
