using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Platform.BackupTarget;

// The S3 bucket every environment's backups go to (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target). One
// row under a fixed id, like ProviderClientIdentity. The secret arrives encrypted; this aggregate never sees it in plaintext.
public class BackupTarget
{
    public static readonly Guid SingletonId = new("7174616e-0000-4000-8000-000000000002");
    public const int MaxBucketLength = 63;

    public Guid Id { get; private set; }
    public string Endpoint { get; private set; } = default!;
    public string Region { get; private set; } = default!;
    public string Bucket { get; private set; } = default!;
    public string AccessKeyId { get; private set; } = default!;
    public byte[] SecretCipher { get; private set; } = default!;
    public DateTimeOffset UpdatedAt { get; private set; }

    private BackupTarget() { }

    public static bool IsValidEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }
        bool isAUri = Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri);
        if (!isAUri || uri is null)
        {
            return false;
        }
        // Credentials in the URL would end up in logs and the audit trail; the key pair has its own fields.
        return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    }

    public static bool IsValidRegion(string? region)
    {
        if (string.IsNullOrEmpty(region) || region.Length > 64)
        {
            return false;
        }
        return region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    public static bool IsValidBucket(string? bucket)
    {
        if (string.IsNullOrEmpty(bucket) || bucket.Length > MaxBucketLength)
        {
            return false;
        }
        return bucket.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' || c == '.');
    }

    public static bool IsValidAccessKeyId(string? accessKeyId)
    {
        if (string.IsNullOrEmpty(accessKeyId) || accessKeyId.Length > 128)
        {
            return false;
        }
        return !accessKeyId.Any(char.IsWhiteSpace);
    }

    public static BackupTarget Create(string endpoint, string region, string bucket, string accessKeyId, byte[]? secretCipher)
    {
        EnsureValid(endpoint, region, bucket, accessKeyId);
        if (secretCipher is null || secretCipher.Length == 0)
        {
            throw new BusinessException("BACKUP_TARGET_SECRET_REQUIRED",
                "The first backup target needs a secret access key.");
        }
        return new BackupTarget
        {
            Id = SingletonId,
            Endpoint = endpoint.Trim().TrimEnd('/'),
            Region = region,
            Bucket = bucket,
            AccessKeyId = accessKeyId,
            SecretCipher = secretCipher,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// A null or empty secretCipher keeps the stored one (the secret is write-only).
    public void Replace(string endpoint, string region, string bucket, string accessKeyId, byte[]? secretCipher)
    {
        EnsureValid(endpoint, region, bucket, accessKeyId);

        Endpoint = endpoint.Trim().TrimEnd('/');
        Region = region;
        Bucket = bucket;
        AccessKeyId = accessKeyId;
        bool hasNewSecret = secretCipher is not null && secretCipher.Length > 0;
        if (hasNewSecret)
        {
            SecretCipher = secretCipher!;
        }
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void EnsureValid(string endpoint, string region, string bucket, string accessKeyId)
    {
        if (!IsValidEndpoint(endpoint))
        {
            throw new ArgumentException("endpoint must be an https URL without credentials", nameof(endpoint));
        }
        if (!IsValidRegion(region))
        {
            throw new ArgumentException("region must be 1 to 64 letters, digits or hyphens", nameof(region));
        }
        if (!IsValidBucket(bucket))
        {
            throw new ArgumentException("bucket must be 1 to 63 characters of a-z, 0-9, '-' or '.'", nameof(bucket));
        }
        if (!IsValidAccessKeyId(accessKeyId))
        {
            throw new ArgumentException("accessKeyId must be 1 to 128 characters without spaces", nameof(accessKeyId));
        }
    }
}
