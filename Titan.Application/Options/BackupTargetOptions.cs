using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;

namespace Titan.Application.Options;

/// The bucket this environment's backups live in, declared in the environment's GitOps config
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target): endpoint, region and bucket in the Deployment env, the key pair in a Secret.
public class BackupTargetOptions
{
    public const string SectionName = "BackupTarget";
    public const int MaxBucketLength = 63;
    public const int MaxEndpointLength = 200;

    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    /// No key pair is how an environment without a backup bucket (or one whose Secret is not there yet) looks: not an error.
    public bool HasKeyPair => AccessKeyId.Length > 0 || SecretAccessKey.Length > 0;

    /// The connection to read or probe the bucket with. No key pair ⇒ 404 BACKUP_TARGET_NOT_FOUND; a value that cannot work
    /// ⇒ 503 BACKUP_TARGET_CONFIG_INVALID, the environment's error and not the target's.
    public BackupBucketConnection ToConnection()
    {
        if (!HasKeyPair)
        {
            throw new NotFoundException("BACKUP_TARGET_NOT_FOUND", "No backup target key pair is configured.");
        }
        string? problem = Problem();
        if (problem is not null)
        {
            throw new ServiceUnavailableException("BACKUP_TARGET_CONFIG_INVALID", problem);
        }
        return new BackupBucketConnection(Endpoint.Trim().TrimEnd('/'), Region, Bucket, AccessKeyId, SecretAccessKey);
    }

    /// What is wrong with the configured values, or null. The message names the setting, never a value: the key pair is in here.
    public string? Problem()
    {
        if (!IsValidEndpoint(Endpoint))
        {
            return "BackupTarget__Endpoint must be an https URL of at most 200 characters, without credentials, path, query or fragment";
        }
        if (!IsValidRegion(Region))
        {
            return "BackupTarget__Region must be 1 to 64 letters, digits or hyphens";
        }
        if (!IsValidBucket(Bucket))
        {
            return "BackupTarget__Bucket must be 1 to 63 characters of a-z, 0-9, '-' or '.'";
        }
        if (!IsValidAccessKeyId(AccessKeyId))
        {
            return "BackupTarget__AccessKeyId must be 1 to 128 characters without spaces";
        }
        if (!IsValidSecretAccessKey(SecretAccessKey))
        {
            return "BackupTarget__SecretAccessKey must be 1 to 256 characters without spaces";
        }
        return null;
    }

    private static bool IsValidEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }
        // Uri.TryCreate tolerates spaces and control characters inside the string, and the raw string is what gets used.
        bool hasStrayCharacters = endpoint.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        if (hasStrayCharacters)
        {
            return false;
        }
        bool isAUri = Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri);
        if (!isAUri || uri is null)
        {
            return false;
        }
        bool isShortEnough = endpoint.Trim().Length <= MaxEndpointLength;
        bool isAnOrigin = uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
        return isShortEnough && isAnOrigin
            && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    private static bool IsValidRegion(string region)
    {
        if (region.Length == 0 || region.Length > 64)
        {
            return false;
        }
        return region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static bool IsValidBucket(string bucket)
    {
        if (bucket.Length == 0 || bucket.Length > MaxBucketLength)
        {
            return false;
        }
        return bucket.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' || c == '.');
    }

    private static bool IsValidAccessKeyId(string accessKeyId)
    {
        if (accessKeyId.Length == 0 || accessKeyId.Length > 128)
        {
            return false;
        }
        return !accessKeyId.Any(char.IsWhiteSpace);
    }

    private static bool IsValidSecretAccessKey(string secretAccessKey)
    {
        if (secretAccessKey.Length == 0 || secretAccessKey.Length > 256)
        {
            return false;
        }
        return !secretAccessKey.Any(char.IsWhiteSpace);
    }
}
