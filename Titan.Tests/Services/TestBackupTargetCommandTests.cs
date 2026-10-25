using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The probe writes, lists, reads back and deletes one object of its own in the configured target. A target that fails is a result
// to render (ok: false), never an exception; what is the environment's own fault (no key pair, a configuration that cannot work) keeps its error.
public class TestBackupTargetCommandTests
{
    private const string Secret = ConfiguredBackupTarget.Secret;

    private readonly InMemoryBackupBucket _bucket = new();
    private readonly FixedBackupBucketFactory _factory;
    private readonly BackupTargetOptions _target = ConfiguredBackupTarget.Valid();

    public TestBackupTargetCommandTests()
    {
        _factory = new FixedBackupBucketFactory(_bucket);
    }

    private TestBackupTargetCommandHandler Handler(TimeSpan? timeout = null) =>
        new(ConfiguredBackupTarget.Options(_target), _factory)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
            CleanupTimeout = TimeSpan.FromMilliseconds(200),
        };

    private Task<BackupTargetTestResult> RunAsync(TestBackupTargetCommandHandler? handler = null, CancellationToken ct = default) =>
        (handler ?? Handler()).HandleAsync(new TestBackupTargetCommand(), ct);

    [Fact]
    public async Task ARightTarget_IsOk_WithItsLatency_AndLeavesNothingInTheBucket()
    {
        var result = await RunAsync();

        Assert.True(result.Ok);
        Assert.NotNull(result.LatencyMs);
        Assert.Null(result.Error);
        Assert.Empty(_bucket.Objects);
        Assert.Single(_bucket.PutKeys);
        Assert.StartsWith("_probe/", _bucket.PutKeys[0]);
    }

    [Fact]
    public async Task TheProbe_OpensTheConfiguredTarget_WithItsKeyPair()
    {
        await RunAsync();

        var opened = Assert.Single(_factory.Opened);
        Assert.Equal(
            new BackupBucketConnection("https://acct.r2.cloudflarestorage.com", "auto", "titan-backup-dev", "key-id", Secret), opened);
    }

    [Fact]
    public async Task TwoProbes_UseTwoObjects_OfDifferentContent()
    {
        var contents = new List<byte[]>();
        _bucket.OnGet = (_, stored) => { contents.Add(stored); return stored; };

        await RunAsync();
        await RunAsync();

        Assert.NotEqual(_bucket.PutKeys[0], _bucket.PutKeys[1]);
        Assert.False(contents[0].SequenceEqual(contents[1]));
        Assert.True(contents[0].Length > 0);
    }

    [Fact]
    public async Task NoKeyPair_IsNotFound()
    {
        _target.AccessKeyId = "";
        _target.SecretAccessKey = "";

        var error = await Assert.ThrowsAsync<NotFoundException>(() => RunAsync());

        Assert.Equal("BACKUP_TARGET_NOT_FOUND", error.Code);
        Assert.Empty(_factory.Opened);
    }

    [Fact]
    public async Task AConfigurationThatCannotWork_KeepsItsOwnError_NotAnOkFalse()
    {
        _target.Bucket = "Bad_Bucket";

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => RunAsync());

        Assert.Equal("BACKUP_TARGET_CONFIG_INVALID", error.Code);
        Assert.Contains("BackupTarget__Bucket", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
        Assert.Empty(_factory.Opened);
    }

    [Fact]
    public async Task OnlyHalfAKeyPair_IsAConfigurationThatCannotWork_NotMissing()
    {
        _target.SecretAccessKey = "";

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => RunAsync());

        Assert.Equal("BACKUP_TARGET_CONFIG_INVALID", error.Code);
        Assert.Contains("BackupTarget__SecretAccessKey", error.Message);
    }

    [Fact]
    public async Task AWriteThatIsDenied_IsOkFalse_WithTheNamedReason_AndNoLatency()
    {
        _bucket.OnPut = _ => throw new BackupBucketException("access denied (AccessDenied)");

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.Null(result.LatencyMs);
        Assert.Equal("access denied (AccessDenied)", result.Error);
        Assert.Empty(_bucket.Objects);
    }

    [Fact]
    public async Task AnObjectReadBackDifferently_IsOkFalse_AndIsStillDeleted()
    {
        _bucket.OnGet = (_, stored) => stored.Reverse().Append((byte)1).ToArray();

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("SHA-256", result.Error);
        Assert.Empty(_bucket.Objects);
    }

    [Fact]
    public async Task AnObjectTheListDoesNotShow_IsOkFalse_AndIsStillDeleted()
    {
        _bucket.OnList = (_, _) => Array.Empty<string>();

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("list", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_bucket.Objects);
    }

    [Fact]
    public async Task ADeleteThatFails_IsOkFalse()
    {
        _bucket.OnDelete = _ => throw new BackupBucketException("delete refused (AccessDenied)");

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.Equal("delete refused (AccessDenied)", result.Error);
    }

    [Fact]
    public async Task ABucketThatStillListsTheObjectAfterItsDelete_IsOkFalse()
    {
        int lists = 0;
        _bucket.OnList = (_, keys) => ++lists == 1 ? keys : new[] { _bucket.PutKeys[0] };

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.Contains("still", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnErrorThatCarriesTheSecret_IsShownWithoutIt()
    {
        _bucket.OnPut = _ => throw new BackupBucketException($"signature rejected for {Secret}");

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.DoesNotContain(Secret, result.Error);
        Assert.Contains("***", result.Error);
    }

    [Fact]
    public async Task ABucketThatNeverAnswers_IsOkFalse_NamingTheWait()
    {
        _bucket.OnPut = _ => Task.Delay(Timeout.InfiniteTimeSpan);

        var result = await RunAsync(Handler(TimeSpan.FromMilliseconds(50)));

        Assert.False(result.Ok);
        Assert.Contains("did not answer", result.Error);
    }

    [Fact]
    public async Task APutThatLandsButNeverAnswers_IsStillDeleted_BeforeTheBucketIsDisposed()
    {
        _bucket.OnPutWithToken = (key, ct) =>
        {
            _bucket.Objects[key] = new byte[] { 1 };
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };

        var result = await RunAsync(Handler(TimeSpan.FromMilliseconds(50)));

        Assert.False(result.Ok);
        Assert.Empty(_bucket.Objects);
        Assert.True(_bucket.Disposed);
    }

    [Fact]
    public async Task ADeleteThatFailsOnce_IsRetried_SoNothingIsLeftBehind_ButTheResultIsStillOkFalse()
    {
        int deletes = 0;
        _bucket.OnDelete = _ => ++deletes == 1 ? throw new BackupBucketException("delete failed (InternalError)") : Task.CompletedTask;

        var result = await RunAsync();

        Assert.False(result.Ok);
        Assert.Equal("delete failed (InternalError)", result.Error);
        Assert.Empty(_bucket.Objects);
    }

    [Fact]
    public async Task ACallerWhoWalksAway_CancelsTheProbe_InsteadOfGettingOkFalse()
    {
        using var cts = new CancellationTokenSource();
        _bucket.OnPut = _ => { cts.Cancel(); return Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(ct: cts.Token));
    }
}
