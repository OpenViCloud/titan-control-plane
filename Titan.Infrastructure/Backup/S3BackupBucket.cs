using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Titan.Application.Abstractions;

namespace Titan.Infrastructure.Backup;

// One S3-compatible bucket (Cloudflare R2 in practice). Path-style addressing, and checksums only where the API requires
// them: the SDK's default CRC32 trailer on every upload is what R2 and other S3-compatible stores have rejected.
// Messages are written here, never taken from the SDK, so they cannot carry the key or the secret.
public sealed class S3BackupBucket : IBackupBucket
{
    private const int MaxCodeLength = 40;

    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    /// callTimeout: per S3 call, no retry; the probe's own deadline is longer, so this is the one that names a slow bucket.
    public S3BackupBucket(BackupBucketConnection connection, TimeSpan? callTimeout = null)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = connection.Endpoint,
            AuthenticationRegion = connection.Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 0,
            Timeout = callTimeout ?? TimeSpan.FromSeconds(10),
        };
        _client = new AmazonS3Client(new BasicAWSCredentials(connection.AccessKeyId, connection.SecretAccessKey), config);
        _bucket = connection.Bucket;
    }

    public void Dispose() => _client.Dispose();

    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        await CallAsync(async () =>
        {
            using var body = new MemoryStream(content);
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = body }, ct);
        }, ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        byte[] content = Array.Empty<byte>();
        await CallAsync(async () =>
        {
            using GetObjectResponse response = await _client.GetObjectAsync(_bucket, key, ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }, ct);
        return content;
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct)
    {
        IReadOnlyList<string> keys = Array.Empty<string>();
        await CallAsync(async () =>
        {
            ListObjectsV2Response response = await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, MaxKeys = 100 }, ct);
            keys = (response.S3Objects ?? new List<S3Object>()).Select(o => o.Key).ToList();
        }, ct);
        return keys;
    }

    public async Task<IReadOnlyList<BackupBucketObject>> ListObjectsAsync(string prefix, int? limit, CancellationToken ct)
    {
        var found = new List<BackupBucketObject>();
        await CallAsync(async () =>
        {
            string? token = null;
            do
            {
                var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token };
                if (limit is not null)
                {
                    request.MaxKeys = limit.Value;
                }
                ListObjectsV2Response response = await _client.ListObjectsV2Async(request, ct);
                foreach (S3Object o in response.S3Objects ?? new List<S3Object>())
                {
                    found.Add(new BackupBucketObject(o.Key, o.Size ?? 0, new DateTimeOffset(o.LastModified ?? DateTime.UnixEpoch, TimeSpan.Zero)));
                }
                token = response.IsTruncated == true && limit is null ? response.NextContinuationToken : null;
            }
            while (token is not null);
        }, ct);
        return found;
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await CallAsync(() => _client.DeleteObjectAsync(_bucket, key, ct), ct);
    }

    private static async Task CallAsync(Func<Task> call, CancellationToken ct)
    {
        try
        {
            await call();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is AmazonServiceException or AmazonClientException or HttpRequestException
                                      or IOException or TimeoutException or OperationCanceledException)
        {
            throw Describe(e);
        }
    }

    /// What an admin can act on, by the S3 error code the bucket answered with.
    public static BackupBucketException Describe(Exception failure)
    {
        if (failure is AmazonServiceException service)
        {
            // The code comes from an endpoint the admin chose: only a short plain token is shown, else the HTTP status.
            string code = IsPlainCode(service.ErrorCode) ? service.ErrorCode : ((int)service.StatusCode).ToString();
            return code switch
            {
                "AccessDenied" => new BackupBucketException($"access denied ({code})"),
                "NoSuchBucket" => new BackupBucketException($"the bucket does not exist ({code})"),
                "InvalidAccessKeyId" or "SignatureDoesNotMatch" =>
                    new BackupBucketException($"the access key id or the secret was rejected ({code})"),
                _ => new BackupBucketException($"the bucket answered with an error ({code})"),
            };
        }
        if (failure is OperationCanceledException or TimeoutException
            || failure.InnerException is OperationCanceledException or TimeoutException)
        {
            return new BackupBucketException("the bucket did not answer in time");
        }
        return new BackupBucketException("could not reach the endpoint");
    }

    private static bool IsPlainCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaxCodeLength && code.All(char.IsAsciiLetterOrDigit);
}
