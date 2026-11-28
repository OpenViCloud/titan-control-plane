using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Exceptions;
using Titan.Application.Options;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// OVC Function at the handler (titan-ops features/ovc-function/design.md#function-lifecycle): create with its quota,
// upload through the inspector, the bucket and the registry before the row, the deploy gate, delete, the worker's
// report and observation, the reads, and the cleanup job. The fake repository hands out the same objects to every read.
public class FunctionServiceTests
{
    private const string FnDev = TestFunctions.Cluster;
    private static readonly DateTimeOffset Today = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly FakeFunctionCodeInspector _inspector = new();
    private readonly FakeFunctionCodeStore _store = new();
    private readonly FakeFunctionImageRegistry _registry = new();
    private readonly FixedClock _clock = new(Today);
    private FakeFunctionRepository _repo = new();
    private readonly FakeFunctionSecretRepository _secrets = new();
    private FakeFunctionRuntimeRepository _runtimes;
    private readonly FunctionRuntime _python;

    private static readonly FunctionClusterOptions Clusters = new() { Clusters = [new FunctionClusterConfig { Id = FnDev, Name = "Function dev" }] };

    public FunctionServiceTests()
    {
        _currentUser.Setup(u => u.TenantId).Returns(_tenantId);
        _commands.SetReturnsDefault(Task.CompletedTask);
        _python = Runtime("python3.15", new DateOnly(2031, 10, 31), availableIn: FnDev);
        _runtimes = new FakeFunctionRuntimeRepository(_python, Runtime("go", null, availableIn: FnDev));
    }

    private static FunctionRuntime Runtime(string name, DateOnly? supportEndsOn, string? availableIn)
    {
        FunctionRuntime runtime = FunctionRuntime.Create(name,
            $"ghcr.io/openvicloud/titan-function-runtimes/{name}@sha256:{new string('a', 64)}", "amd64", supportEndsOn);
        if (availableIn != null)
        {
            runtime.SyncSucceededIn(availableIn, runtime.SyncIn(availableIn));
        }
        return runtime;
    }

    private Function Seed(Function? function = null)
    {
        function ??= TestFunctions.AFunction(_tenantId);
        _repo.Rows.Add(function);
        return function;
    }

    // A version whose image is in the registry, packed on the python runtime of the catalog.
    private FunctionVersion VersionOn(Function function, FunctionRuntime? runtime = null, params FunctionSecretRef[] secretRefs)
    {
        runtime ??= _python;
        FunctionVersion version = function.AddVersion(TestFunctions.Spec(function, runtimeId: runtime.Id, runtimeName: runtime.Name,
            runtimeImage: runtime.Image, digest: _registry.NextDigest, secretRefs: secretRefs));
        _registry.Images.Add(version.ImageDigest);
        return version;
    }

    private CreateFunctionCommandHandler Create() => new(_repo, Clusters, _currentUser.Object);

    private UploadFunctionVersionCommandHandler Upload() =>
        new(_repo, _secrets, _runtimes, _inspector, _store, _registry, _currentUser.Object, _clock);

    private DeployFunctionCommandHandler Deploy() => new(_repo, _secrets, _runtimes, _registry, _currentUser.Object, _commands.Object);

    private DeleteFunctionCommandHandler Delete() => new(_repo, _currentUser.Object, _commands.Object);

    private EndFunctionRequestCommandHandler End() => new(_repo);

    private CleanFunctionCodeCommandHandler Cleanup() => new(_repo, _store, _registry, NullLogger<CleanFunctionCodeCommandHandler>.Instance);

    private UploadFunctionVersionCommand UploadOf(Function function, string runtime = "python3.15", string? handler = "main.handler") =>
        new(function.Id, runtime, handler, 256, 30, "/tmp/upload/code.zip", "/tmp/upload",
            new Dictionary<string, string>(), new Dictionary<string, UploadFunctionSecretRef>(), null);

    // A secret of the tenant with versions 1..versions, each value valueBytes long; the ciphertext is never read here.
    private FunctionSecret SecretOfTheTenant(string name = "DB_PASSWORD", int versions = 1, int valueBytes = 10)
    {
        static FunctionSecretValue Numbered(int number, int bytes) =>
            new(number, "k1", new byte[FunctionSecret.NonceBytes], new byte[bytes + FunctionSecret.TagBytes], bytes);
        FunctionSecret secret = FunctionSecret.Create(_tenantId, name, Numbered(1, valueBytes), "kc-member");
        for (int number = 2; number <= versions; number++)
        {
            secret.AddVersion(Numbered(number, valueBytes), "kc-member", []);
        }
        _secrets.Saved.Add(secret);
        return secret;
    }

    private UploadFunctionVersionCommand UploadWith(Function function, Dictionary<string, string>? env = null,
        Dictionary<string, UploadFunctionSecretRef>? secrets = null, string? schedule = null) =>
        UploadOf(function) with
        {
            Env = env ?? new Dictionary<string, string>(),
            Secrets = secrets ?? new Dictionary<string, UploadFunctionSecretRef>(),
            Schedule = schedule,
        };

    private void AssertNothingWritten(Function function)
    {
        Assert.Empty(_inspector.Packed);
        Assert.Empty(_store.Put);
        Assert.Empty(_registry.RuntimeCopies);
        Assert.Empty(function.Versions);
        Assert.Equal(0, _repo.Saves);
    }

    private void VerifyDeploys(Times times) =>
        _commands.Verify(q => q.EnqueueFunctionDeployAsync(It.IsAny<Function>(), It.IsAny<FunctionVersion>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), times);

    private void VerifyDeletes(Times times) =>
        _commands.Verify(q => q.EnqueueFunctionDeleteAsync(It.IsAny<Function>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), times);

    // ── create ──

    [Fact]
    public async Task Create_MakesAnInactiveFunction_OnTheFunctionCluster()
    {
        CreateFunctionCommandResult result = await Create().HandleAsync(new CreateFunctionCommand("hello", FnDev));

        Function function = Assert.Single(_repo.Rows);
        Assert.Equal(result.FunctionId, function.Id);
        Assert.Equal(_tenantId, function.TenantId);
        Assert.Equal(FnDev, function.ClusterId);
        Assert.Equal(FunctionStatus.Inactive, function.Status);
        Assert.Equal(1, _repo.Saves);
    }

    [Fact]
    public async Task Create_OnAClusterThatIsNoFunctionCluster_IsClusterNotFound()
    {
        var e = await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(new CreateFunctionCommand("hello", "vn-hcm-1")));

        Assert.Equal("CLUSTER_NOT_FOUND", e.Code);
        Assert.Empty(_repo.Rows);
    }

    [Fact]
    public async Task Create_WithANameOutsideTheRule_Is422()
    {
        var e = await Assert.ThrowsAsync<BusinessException>(() => Create().HandleAsync(new CreateFunctionCommand("-bad name", FnDev)));

        Assert.Equal("INVALID_FUNCTION_NAME", e.Code);
        Assert.Empty(_repo.Rows);
    }

    [Fact]
    public async Task Create_TheTwentyFirst_IsRefused_AndDeletingOne_MakesRoom()
    {
        for (int i = 0; i < Function.MaxPerTenant; i++)
        {
            Seed();
        }
        Seed(TestFunctions.AFunction()); // another tenant's does not count

        var e = await Assert.ThrowsAsync<BusinessException>(() => Create().HandleAsync(new CreateFunctionCommand("one-more", FnDev)));
        Assert.Equal("FUNCTION_QUOTA_EXCEEDED", e.Code);
        Assert.Equal(Function.MaxPerTenant + 1, _repo.Rows.Count);

        _repo.Rows[0].Delete();
        await Create().HandleAsync(new CreateFunctionCommand("one-more", FnDev));
        Assert.Equal(Function.MaxPerTenant + 2, _repo.Rows.Count);
    }

    // ── upload ──

    [Fact]
    public async Task Upload_WritesTheZip_PushesTheImage_ThenAddsTheVersion_UnderTheLock()
    {
        Function function = Seed();

        UploadFunctionVersionCommandResult result = await Upload().HandleAsync(UploadOf(function));

        FunctionVersion version = Assert.Single(function.Versions);
        Assert.Equal(result.VersionId, version.Id);
        Assert.Equal([$"functions/{_tenantId}/{function.Id}/{version.Id}.zip"], _store.Put);
        Assert.Equal(["python3.15"], _registry.RuntimeCopies);
        Assert.Equal(_registry.NextDigest, version.ImageDigest);
        Assert.Equal($"{FakeFunctionImageRegistry.PullHost}/fn/{_tenantId}/{function.Id}@{_registry.NextDigest}", version.Image);
        Assert.Equal(_python.Id, version.RuntimeId);
        Assert.Equal(_python.Image, version.RuntimeImage);
        Assert.Equal("main.handler", version.Handler);
        Assert.Equal(_inspector.Layer.ZipSha256, version.CodeSha256);
        Assert.Equal(_inspector.Layer.ZipBytes, version.CodeBytes);
        Assert.Equal(_inspector.Layer.UnpackedBytes, version.UnpackedBytes);
        Assert.Equal(_inspector.Layer.FileCount, version.FileCount);
        Assert.Equal([function.Id], _repo.Locked);
        Assert.Equal(1, _repo.Saves);
        var packed = Assert.Single(_inspector.Packed);
        Assert.Equal(FunctionCodeLanguage.Python, packed.Language);
        Assert.Equal("/tmp/upload", packed.WorkDir);
    }

    [Fact]
    public async Task Upload_OnGo_StoresNoHandler_AndPacksForGo()
    {
        Function function = Seed();

        await Upload().HandleAsync(UploadOf(function, runtime: "go", handler: "ignored.handler"));

        Assert.Null(Assert.Single(function.Versions).Handler);
        var packed = Assert.Single(_inspector.Packed);
        Assert.Equal(FunctionCodeLanguage.Go, packed.Language);
        Assert.Null(packed.Handler);
    }

    [Fact]
    public async Task Upload_OnPythonWithoutAHandler_Is400_BeforeAnythingIsWritten()
    {
        Function function = Seed();

        var e = await Assert.ThrowsAsync<BadRequestException>(() => Upload().HandleAsync(UploadOf(function, handler: null)));

        Assert.Equal("VALIDATION_ERROR", e.Code);
        Assert.Empty(_store.Put);
        Assert.Empty(function.Versions);
    }

    [Theory]
    [InlineData("ruby")]
    [InlineData("nodejs26")] // not in this catalog
    public async Task Upload_OnARuntimeNotInTheCatalog_IsNotUsable_AndWritesNothing(string runtime)
    {
        Function function = Seed();

        var e = await Assert.ThrowsAsync<BusinessException>(() => Upload().HandleAsync(UploadOf(function, runtime: runtime)));

        Assert.Equal("FUNCTION_RUNTIME_NOT_USABLE", e.Code);
        Assert.Empty(_inspector.Packed);
        Assert.Empty(_store.Put);
        Assert.Empty(function.Versions);
    }

    [Fact]
    public async Task Upload_OnARuntimePastItsSupportDate_IsNotUsable()
    {
        Function function = Seed();
        _clock.Now = new DateTimeOffset(2031, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var e = await Assert.ThrowsAsync<BusinessException>(() => Upload().HandleAsync(UploadOf(function)));

        Assert.Equal("FUNCTION_RUNTIME_NOT_USABLE", e.Code);
        Assert.Empty(function.Versions);
    }

    [Fact]
    public async Task Upload_OnItsLastSupportedDay_IsTaken()
    {
        Function function = Seed();
        _clock.Now = new DateTimeOffset(2031, 10, 31, 23, 0, 0, TimeSpan.Zero);

        await Upload().HandleAsync(UploadOf(function));

        Assert.Single(function.Versions);
    }

    [Fact]
    public async Task Upload_ThatTheInspectorRefuses_IsCodeInvalid_AndWritesNothing()
    {
        Function function = Seed();
        _inspector.Refusal = "The zip holds a symlink.";

        var e = await Assert.ThrowsAsync<BusinessException>(() => Upload().HandleAsync(UploadOf(function)));

        Assert.Equal("FUNCTION_CODE_INVALID", e.Code);
        Assert.Empty(_store.Put);
        Assert.Empty(_registry.Images);
        Assert.Empty(function.Versions);
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Upload_WhenTheBucketFails_Is503_AndAddsNoVersion()
    {
        Function function = Seed();
        _store.PutFails = true;

        var e = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Upload().HandleAsync(UploadOf(function)));

        Assert.Equal("FUNCTION_ARTIFACT_UNAVAILABLE", e.Code);
        Assert.Empty(function.Versions);
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Upload_WhenTheRegistryFails_Is503_AndAddsNoVersion()
    {
        Function function = Seed();
        _registry.Fails = true;

        var e = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Upload().HandleAsync(UploadOf(function)));

        Assert.Equal("FUNCTION_ARTIFACT_UNAVAILABLE", e.Code);
        Assert.Empty(function.Versions);
    }

    [Fact]
    public async Task Upload_ToAnotherTenantsFunction_IsNotFound()
    {
        Function foreign = Seed(TestFunctions.AFunction());

        var e = await Assert.ThrowsAsync<NotFoundException>(() => Upload().HandleAsync(UploadOf(foreign)));

        Assert.Equal("FUNCTION_NOT_FOUND", e.Code);
        Assert.Empty(_inspector.Packed);
    }

    [Fact]
    public async Task Upload_ToADeletingFunction_IsInvalidStatus_BeforeAnythingIsWritten()
    {
        Function function = Seed();
        function.Running();
        function.Delete();

        var e = await Assert.ThrowsAsync<BusinessException>(() => Upload().HandleAsync(UploadOf(function)));

        Assert.Equal("INVALID_STATUS", e.Code);
        Assert.Empty(_inspector.Packed);
        Assert.Empty(_store.Put);
    }

    // ── upload: variables, secrets, schedule (titan-ops features/ovc-function/design.md#function-config) ──

    [Fact]
    public async Task Upload_KeepsTheVariablesAndTheSchedule_AndPinsLatest_ToTheNewestNumber()
    {
        Function function = Seed();
        FunctionSecret db = SecretOfTheTenant(versions: 3);

        await Upload().HandleAsync(UploadWith(function,
            env: new() { ["LOG_LEVEL"] = "info" },
            secrets: new() { ["DB"] = new UploadFunctionSecretRef("DB_PASSWORD", null) },
            schedule: "*/5 * * * *"));

        FunctionVersion version = Assert.Single(function.Versions);
        Assert.Equal(new Dictionary<string, string> { ["LOG_LEVEL"] = "info" }, version.Env);
        Assert.Equal([FunctionSecretRef.Of("DB", db.Id, "DB_PASSWORD", 3)], version.SecretRefs);
        Assert.Equal("*/5 * * * *", version.Schedule);

        // A later value does not move the pinned number.
        db.AddVersion(new FunctionSecretValue(4, "k1", new byte[12], new byte[26], 10), "kc-member", []);
        Assert.Equal(3, Assert.Single(version.SecretRefs).Version);
    }

    [Fact]
    public async Task Upload_ANumberedVersion_IsPinnedAsAsked()
    {
        Function function = Seed();
        FunctionSecret db = SecretOfTheTenant(versions: 3);

        await Upload().HandleAsync(UploadWith(function, secrets: new() { ["DB"] = new UploadFunctionSecretRef("DB_PASSWORD", 2) }));

        Assert.Equal(FunctionSecretRef.Of("DB", db.Id, "DB_PASSWORD", 2), Assert.Single(Assert.Single(function.Versions).SecretRefs));
    }

    public static TheoryData<string, int?> SecretsNotThere => new()
    {
        { "NOPE", null },
        { "DB_PASSWORD", 4 },
        { "THEIRS", null },
    };

    [Theory]
    [MemberData(nameof(SecretsNotThere))]
    public async Task Upload_ASecretTheTenantHasNot_OrAVersionItDoesNotKeep_Is422_AndWritesNothing(string name, int? number)
    {
        Function function = Seed();
        SecretOfTheTenant(versions: 3);
        _secrets.Saved.Add(FunctionSecret.Create(Guid.NewGuid(), "THEIRS",
            new FunctionSecretValue(1, "k1", new byte[12], new byte[26], 10), "kc-other"));

        var e = await Assert.ThrowsAsync<BusinessException>(() =>
            Upload().HandleAsync(UploadWith(function, secrets: new() { ["DB"] = new UploadFunctionSecretRef(name, number) })));

        Assert.Equal("FUNCTION_SECRET_NOT_FOUND", e.Code);
        AssertNothingWritten(function);
    }

    // 8 KiB of names and values, a secret counted by its variable's name and its value's length.
    [Fact]
    public async Task Upload_OverTheConfigLimit_CountingTheSecretsLength_Is400_AndWritesNothing()
    {
        Function function = Seed();
        SecretOfTheTenant(valueBytes: 4000);
        string fill = new('x', Function.MaxConfigBytes - 4000 - "DB".Length - "E".Length);
        var fits = UploadWith(function, env: new() { ["E"] = fill }, secrets: new() { ["DB"] = new UploadFunctionSecretRef("DB_PASSWORD", null) });
        var over = UploadWith(function, env: new() { ["E"] = fill + "x" }, secrets: new() { ["DB"] = new UploadFunctionSecretRef("DB_PASSWORD", null) });

        var e = await Assert.ThrowsAsync<BadRequestException>(() => Upload().HandleAsync(over));

        Assert.Equal("VALIDATION_ERROR", e.Code);
        AssertNothingWritten(function);
        await Upload().HandleAsync(fits);
        Assert.Single(function.Versions);
    }

    // ── deploy ──

    [Fact]
    public async Task Deploy_EnqueuesOneCommand_KeyedOnItsRequest_BeforeTheSave()
    {
        Function function = Seed();
        FunctionVersion version = VersionOn(function);

        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id));

        Assert.Equal(FunctionStatus.Deploying, function.Status);
        _commands.Verify(q => q.EnqueueFunctionDeployAsync(function, version, function.Operation.PendingId!.Value,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, _repo.Saves);
        Assert.Equal([function.Id], _repo.Locked);
    }

    [Fact]
    public async Task Deploy_Repeated_WhileItWaits_KeepsTheSameRequest_AndSendsNothingMore()
    {
        Function function = Seed();
        FunctionVersion version = VersionOn(function);
        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id));
        Guid first = function.Operation.PendingId!.Value;

        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id));

        Assert.Equal(first, function.Operation.PendingId);
        VerifyDeploys(Times.Once());
        Assert.Equal(1, _repo.Saves);
    }

    [Fact]
    public async Task Deploy_OfAnotherVersion_WhileOneWaits_IsInvalidStatus()
    {
        Function function = Seed();
        FunctionVersion v1 = VersionOn(function);
        FunctionVersion v2 = VersionOn(function);
        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, v1.Id));

        var e = await Assert.ThrowsAsync<BusinessException>(() => Deploy().HandleAsync(new DeployFunctionCommand(function.Id, v2.Id)));

        Assert.Equal("INVALID_STATUS", e.Code);
        VerifyDeploys(Times.Once());
    }

    [Fact]
    public async Task Deploy_OfTheRunningVersion_IsARepeat_AndSendsNothing()
    {
        Function function = Seed();
        FunctionVersion version = VersionOn(function);
        function.Running(version);

        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id));

        Assert.Equal(FunctionStatus.Active, function.Status);
        VerifyDeploys(Times.Never());
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Deploy_WhereTheRuntimeIsNotOnTheCluster_IsNotUsable_AndSendsNothing()
    {
        FunctionRuntime syncing = Runtime("nodejs26", new DateOnly(2029, 4, 30), availableIn: null);
        syncing.SyncIn(FnDev);
        _runtimes = new FakeFunctionRuntimeRepository(_python, syncing);
        Function function = Seed();
        FunctionVersion version = VersionOn(function, syncing);

        var e = await Assert.ThrowsAsync<BusinessException>(() => Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id)));

        Assert.Equal("FUNCTION_RUNTIME_NOT_USABLE", e.Code);
        Assert.Equal(FunctionStatus.Inactive, function.Status);
        VerifyDeploys(Times.Never());
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Deploy_WhoseImageIsNotInTheRegistry_IsNotUsable_AndSendsNothing()
    {
        Function function = Seed();
        FunctionVersion version = VersionOn(function);
        _registry.Images.Clear();

        var e = await Assert.ThrowsAsync<BusinessException>(() => Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id)));

        Assert.Equal("FUNCTION_RUNTIME_NOT_USABLE", e.Code);
        VerifyDeploys(Times.Never());
    }

    [Fact]
    public async Task Deploy_OfAnOldVersion_OfARuntimePastItsSupportDate_IsTaken()
    {
        Function function = Seed();
        FunctionVersion version = VersionOn(function);
        _clock.Now = new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id));

        Assert.Equal(FunctionStatus.Deploying, function.Status);
        VerifyDeploys(Times.Once());
    }

    [Fact]
    public async Task Deploy_OfADiscardedVersion_IsVersionNotFound()
    {
        Function function = Seed();
        FunctionVersion first = VersionOn(function);
        for (int i = 0; i < Function.KeptVersions; i++) VersionOn(function);

        var e = await Assert.ThrowsAsync<NotFoundException>(() => Deploy().HandleAsync(new DeployFunctionCommand(function.Id, first.Id)));

        Assert.Equal("FUNCTION_VERSION_NOT_FOUND", e.Code);
        VerifyDeploys(Times.Never());
    }

    // The worker reads every pinned secret version during the deploy (titan-ops features/ovc-function/design.md#function-secrets).
    [Fact]
    public async Task Deploy_AVersionWhoseSecretVersionIsGone_Is422_AndSendsNothing()
    {
        Function function = Seed();
        FunctionSecret db = SecretOfTheTenant();
        FunctionVersion stale = VersionOn(function, null, FunctionSecretRef.Of("DB", db.Id, db.Name, 2));
        FunctionVersion deleted = VersionOn(function, null, FunctionSecretRef.Of("DB", Guid.NewGuid(), "GONE", 1));

        var e1 = await Assert.ThrowsAsync<BusinessException>(() => Deploy().HandleAsync(new DeployFunctionCommand(function.Id, stale.Id)));
        var e2 = await Assert.ThrowsAsync<BusinessException>(() => Deploy().HandleAsync(new DeployFunctionCommand(function.Id, deleted.Id)));

        Assert.Equal(("FUNCTION_SECRET_NOT_FOUND", "FUNCTION_SECRET_NOT_FOUND"), (e1.Code, e2.Code));
        Assert.Equal(FunctionStatus.Inactive, function.Status);
        VerifyDeploys(Times.Never());
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Deploy_AVersionWhoseSecretsAreThere_IsSent()
    {
        Function function = Seed();
        FunctionSecret db = SecretOfTheTenant();
        FunctionVersion version = VersionOn(function, null, FunctionSecretRef.Of("DB", db.Id, db.Name, 1));

        await Deploy().HandleAsync(new DeployFunctionCommand(function.Id, version.Id));

        Assert.Equal(FunctionStatus.Deploying, function.Status);
        VerifyDeploys(Times.Once());
    }

    // ── delete ──

    [Fact]
    public async Task Delete_OfAFunctionNeverDeployed_IsDoneAtOnce_WithoutACommand()
    {
        Function function = Seed();
        VersionOn(function);

        await Delete().HandleAsync(new DeleteFunctionCommand(function.Id));

        Assert.Equal(FunctionStatus.Deleted, function.Status);
        VerifyDeletes(Times.Never());
        Assert.Equal(1, _repo.Saves);
    }

    [Fact]
    public async Task Delete_OfARunningFunction_EnqueuesOneCommand_KeyedOnItsRequest()
    {
        Function function = Seed();
        function.Running(VersionOn(function));

        await Delete().HandleAsync(new DeleteFunctionCommand(function.Id));

        Assert.Equal(FunctionStatus.Deleting, function.Status);
        _commands.Verify(q => q.EnqueueFunctionDeleteAsync(function, function.Operation.PendingId!.Value, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal([function.Id], _repo.Locked);
    }

    [Fact]
    public async Task Delete_Repeated_OrOfADeletedOne_SendsNothing()
    {
        Function function = Seed();
        function.Running(VersionOn(function));
        await Delete().HandleAsync(new DeleteFunctionCommand(function.Id));
        Guid first = function.Operation.PendingId!.Value;

        await Delete().HandleAsync(new DeleteFunctionCommand(function.Id));
        function.DeleteSucceeded(first);
        await Delete().HandleAsync(new DeleteFunctionCommand(function.Id));

        VerifyDeletes(Times.Once());
        Assert.Equal(1, _repo.Saves);
    }

    [Fact]
    public async Task Delete_WhileADeployWaits_IsInvalidStatus()
    {
        Function function = Seed();
        function.Deploy(VersionOn(function).Id);

        var e = await Assert.ThrowsAsync<BusinessException>(() => Delete().HandleAsync(new DeleteFunctionCommand(function.Id)));

        Assert.Equal("INVALID_STATUS", e.Code);
        VerifyDeletes(Times.Never());
    }

    // ── the worker's report and observation ──

    [Fact]
    public async Task Report_DoneOfTheDeploy_RunsTheVersion_AndNamesTheService()
    {
        Function function = Seed();
        FunctionVersion version = VersionOn(function);
        Guid requestId = function.Deploy(version.Id);

        await End().HandleAsync(new EndFunctionRequestCommand(function.Id, requestId, "Done", "fn-abc"));

        Assert.Equal(FunctionStatus.Active, function.Status);
        Assert.Equal(version.Id, function.RunningVersionId);
        Assert.Equal("fn-abc", function.ExternalId);
        Assert.Equal(1, _repo.Saves);
        Assert.Equal([function.Id], _repo.Locked);
    }

    [Fact]
    public async Task Report_DoneOfTheDeploy_WithoutAServiceName_Is422()
    {
        Function function = Seed();
        Guid requestId = function.Deploy(VersionOn(function).Id);

        var e = await Assert.ThrowsAsync<BusinessException>(() =>
            End().HandleAsync(new EndFunctionRequestCommand(function.Id, requestId, "Done")));

        Assert.Equal("INVALID_STATUS", e.Code);
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Report_FailedOfTheFirstDeploy_IsInactive_AndOfADelete_RestsActive()
    {
        Function first = Seed();
        Guid deploy = first.Deploy(VersionOn(first).Id);
        Function running = Seed(TestFunctions.AFunction(_tenantId));
        running.Running(VersionOn(running));
        Guid delete = running.Delete()!.OperationId;

        await End().HandleAsync(new EndFunctionRequestCommand(first.Id, deploy, "Failed", Reason: "revision not ready", Detail: "raw"));
        await End().HandleAsync(new EndFunctionRequestCommand(running.Id, delete, "Failed", Reason: "api refused"));

        Assert.Equal(FunctionStatus.Inactive, first.Status);
        Assert.Equal("revision not ready", first.Operation.FailureReason);
        Assert.Equal(FunctionStatus.Active, running.Status);
        Assert.Equal("api refused", running.Operation.FailureReason);
    }

    [Fact]
    public async Task Report_DoneOfTheDelete_IsDeleted()
    {
        Function function = Seed();
        function.Running(VersionOn(function));
        Guid delete = function.Delete()!.OperationId;

        await End().HandleAsync(new EndFunctionRequestCommand(function.Id, delete, "Done"));

        Assert.Equal(FunctionStatus.Deleted, function.Status);
    }

    [Fact]
    public async Task Report_OfARequestNotWaiting_IsIgnored_AndSavesNothing()
    {
        Function function = Seed();
        function.Deploy(VersionOn(function).Id);

        await End().HandleAsync(new EndFunctionRequestCommand(function.Id, Guid.NewGuid(), "Done", "fn-x"));

        Assert.Equal(FunctionStatus.Deploying, function.Status);
        Assert.Equal(0, _repo.Saves);
    }

    [Fact]
    public async Task Report_ForNoFunction_IsNotFound_AndAnUnknownOutcome_Is400()
    {
        var missing = await Assert.ThrowsAsync<NotFoundException>(() =>
            End().HandleAsync(new EndFunctionRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done", "fn-x")));
        var unknown = await Assert.ThrowsAsync<BadRequestException>(() =>
            End().HandleAsync(new EndFunctionRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Finished")));

        Assert.Equal("FUNCTION_NOT_FOUND", missing.Code);
        Assert.Equal("INVALID_STATUS", unknown.Code);
    }

    [Fact]
    public async Task Observation_MovesARunningFunction_BetweenActiveAndNotFound_UnderTheLock()
    {
        Function function = Seed();
        function.Running(VersionOn(function));
        var handler = new ObserveFunctionCommandHandler(_repo);

        await handler.HandleAsync(new ObserveFunctionCommand(function.Id, false));
        Assert.Equal(FunctionStatus.NotFound, function.Status);
        await handler.HandleAsync(new ObserveFunctionCommand(function.Id, true));
        Assert.Equal(FunctionStatus.Active, function.Status);
        Assert.Equal([function.Id, function.Id], _repo.Locked);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new ObserveFunctionCommand(Guid.NewGuid(), true)));
    }

    // ── reads ──

    [Fact]
    public async Task Get_ShowsTheUrl_OnceTheServiceIsNamed()
    {
        var urls = new FunctionUrlOptions { Scheme = "http", Domain = "fn.localhost" };
        Function function = Seed();
        var handler = new GetFunctionQueryHandler(_repo, _currentUser.Object, urls);

        FunctionQueryResult before = await handler.HandleAsync(new GetFunctionQuery(function.Id));
        FunctionVersion version = function.Running(VersionOn(function));
        FunctionQueryResult after = await handler.HandleAsync(new GetFunctionQuery(function.Id));

        Assert.Null(before.Url);
        Assert.Equal(FunctionStatus.Inactive, before.Status);
        Assert.Equal($"http://{function.ExternalId}.fn.localhost", after.Url);
        Assert.Equal(FunctionStatus.Active, after.Status);
        Assert.Equal(version.Id, after.RunningVersionId);
        Assert.Equal("hello", after.Name);
        Assert.Equal(FnDev, after.ClusterId);
    }

    [Fact]
    public async Task Get_OfAnotherTenantsFunction_IsNotFound()
    {
        Function foreign = Seed(TestFunctions.AFunction());
        var handler = new GetFunctionQueryHandler(_repo, _currentUser.Object, new FunctionUrlOptions());

        var e = await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new GetFunctionQuery(foreign.Id)));

        Assert.Equal("FUNCTION_NOT_FOUND", e.Code);
    }

    [Fact]
    public async Task List_ShowsTheTenantsFunctions_ButNotTheDeletedOnes()
    {
        Function kept = Seed();
        Function deleted = Seed(TestFunctions.AFunction(_tenantId, name: "gone"));
        deleted.Delete();
        Seed(TestFunctions.AFunction());

        List<FunctionQueryResult> rows = await new ListFunctionsQueryHandler(_repo, _currentUser.Object, new FunctionUrlOptions())
            .HandleAsync(new ListFunctionsQuery());

        Assert.Equal([kept.Id], rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Versions_AreTheKeptOnes_NewestFirst()
    {
        Function function = Seed();
        List<FunctionVersion> versions = Enumerable.Range(0, 7).Select(_ => VersionOn(function)).ToList();

        List<FunctionVersionQueryResult> rows = await new ListFunctionVersionsQueryHandler(_repo, _currentUser.Object)
            .HandleAsync(new ListFunctionVersionsQuery(function.Id));

        Assert.Equal([7, 6, 5, 4, 3], rows.Select(r => r.Number));
        FunctionVersionQueryResult newest = rows[0];
        Assert.Equal(versions[^1].Id, newest.Id);
        Assert.Equal("python3.15", newest.Runtime);
        Assert.Equal("main.handler", newest.Handler);
        Assert.Equal(256, newest.MemoryMiB);
        Assert.Equal(30, newest.TimeoutSeconds);
        Assert.Equal(versions[^1].CodeSha256, newest.CodeSha256);
        Assert.Equal(versions[^1].CodeBytes, newest.CodeBytes);
    }

    [Fact]
    public async Task Reconcile_ListsTheNamedFunctionsOfTheCluster_WithWhatTitanLastSaw()
    {
        Function active = Seed();
        active.Running(VersionOn(active));
        Function missing = Seed(TestFunctions.AFunction(_tenantId, name: "missing"));
        missing.Running(VersionOn(missing));
        missing.MarkNotFound();
        Seed(TestFunctions.AFunction(_tenantId, name: "never"));
        Function elsewhere = Seed(TestFunctions.AFunction(_tenantId, clusterId: "fn-other", name: "elsewhere"));
        elsewhere.Running(VersionOn(elsewhere));

        List<ReconcilableFunctionQueryResult> rows = await new ListReconcilableFunctionsQueryHandler(_repo)
            .HandleAsync(new ListReconcilableFunctionsQuery(FnDev));

        Assert.Equal(
            [(active.Id, _tenantId, "Exists"), (missing.Id, _tenantId, "NotFound")],
            rows.Select(r => (r.Id, r.TenantId, r.CurrentExistence)).OrderBy(r => r.CurrentExistence));
    }

    // ── cleanup ──

    [Fact]
    public async Task Cleanup_RemovesTheZipAndImage_OfDiscardedVersions_AndOfDeletedFunctions_ThenTheirRows()
    {
        Function live = Seed();
        List<FunctionVersion> versions = Enumerable.Range(0, 6).Select(_ => VersionOn(live)).ToList();
        Function gone = Seed(TestFunctions.AFunction(_tenantId, name: "gone"));
        FunctionVersion goneVersion = VersionOn(gone);
        gone.Delete();

        await Cleanup().HandleAsync(new CleanFunctionCodeCommand());

        Assert.Equal([versions[0].CodeKey, goneVersion.CodeKey], _store.Deleted);
        Assert.Equal(2, _registry.DeletedImages.Count);
        Assert.Equal(5, live.Versions.Count);
        Assert.DoesNotContain(live.Versions, v => v.Id == versions[0].Id);
        Assert.Empty(gone.Versions);
        Assert.Equal([live.Id, gone.Id], _repo.Locked);
    }

    [Fact]
    public async Task Cleanup_KeepsTheRowOfAVersionWhoseZipCouldNotBeDeleted_AndGoesOn()
    {
        Function first = Seed();
        FunctionVersion stuck = VersionOn(first);
        first.Delete();
        Function second = Seed(TestFunctions.AFunction(_tenantId, name: "second"));
        FunctionVersion cleaned = VersionOn(second);
        second.Delete();
        _store.DeleteFailsFor.Add(stuck.CodeKey);

        await Cleanup().HandleAsync(new CleanFunctionCodeCommand());

        Assert.Contains(first.Versions, v => v.Id == stuck.Id);
        Assert.Empty(second.Versions);
        Assert.Equal([cleaned.CodeKey], _store.Deleted);

        _store.DeleteFailsFor.Clear();
        await Cleanup().HandleAsync(new CleanFunctionCodeCommand());
        Assert.Empty(first.Versions);
    }
}
