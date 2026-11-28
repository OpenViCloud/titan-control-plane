using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Infrastructure.Backup;

namespace Titan.Infrastructure.FunctionCode;

// The function code bucket, an S3-compatible store (R2 on prd), configured like the backup bucket and for the same reasons
// (S3BackupBucket): path-style, checksums only where the API requires them, one plain upload body.
// The client is made on first use: without a function cluster the section is empty and nothing calls this.
public sealed class S3FunctionCodeStore : IFunctionCodeStore, IDisposable
{
    private readonly Lazy<AmazonS3Client> _client;
    private readonly string _bucket;

    public S3FunctionCodeStore(IOptions<FunctionCodeOptions> options) : this(options.Value, null)
    {
    }

    /// callTimeout: per S3 call, no retry. Two minutes carries a 50 MiB zip over a slow link.
    internal S3FunctionCodeStore(FunctionCodeOptions options, TimeSpan? callTimeout)
    {
        _bucket = options.Bucket;
        _client = new Lazy<AmazonS3Client>(() => new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint.Trim().TrimEnd('/'),
                AuthenticationRegion = options.Region,
                ForcePathStyle = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                MaxErrorRetry = 0,
                Timeout = callTimeout ?? TimeSpan.FromMinutes(2),
            }));
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    public Task PutAsync(string key, string filePath, CancellationToken ct) =>
        CallAsync(() => _client.Value.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            // The SDK opens the file itself and streams it; a zip is never read into memory whole.
            FilePath = filePath,
            ContentType = "application/zip",
            // R2 refuses the SDK's aws-chunked framing (titan-ops features/backup/design.md#backup-target).
            UseChunkEncoding = false,
        }, ct), ct);

    public Task DeleteAsync(string key, CancellationToken ct) =>
        CallAsync(async () =>
        {
            try
            {
                await _client.Value.DeleteObjectAsync(_bucket, key, ct);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode != "NoSuchBucket")
            {
                // S3 answers 204 for a key already gone; a store that says 404 instead means the same.
            }
        }, ct);

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
            // Worded by S3BackupBucket.Describe, never by the SDK: the SDK's text can carry the key id.
            throw new FunctionArtifactException("function code bucket: " + S3BackupBucket.Describe(e).Message);
        }
    }
}
