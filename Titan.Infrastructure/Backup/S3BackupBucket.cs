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
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3BackupBucket(BackupBucketConnection connection)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = connection.Endpoint,
            AuthenticationRegion = connection.Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 0,
            Timeout = CallTimeout,
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
                                      or IOException or OperationCanceledException)
        {
            throw Describe(e);
        }
    }

    /// What an admin can act on, by the S3 error code the bucket answered with.
    public static BackupBucketException Describe(Exception failure)
    {
        if (failure is AmazonServiceException service)
        {
            string code = string.IsNullOrEmpty(service.ErrorCode) ? ((int)service.StatusCode).ToString() : service.ErrorCode;
            return code switch
            {
                "AccessDenied" => new BackupBucketException($"access denied ({code})"),
                "NoSuchBucket" => new BackupBucketException($"the bucket does not exist ({code})"),
                "InvalidAccessKeyId" or "SignatureDoesNotMatch" =>
                    new BackupBucketException($"the access key id or the secret was rejected ({code})"),
                _ => new BackupBucketException($"the bucket answered with an error ({code})"),
            };
        }
        return new BackupBucketException("could not reach the endpoint");
    }
}
