using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The versions of a function (titan-ops features/ovc-function/design.md#function-versions): numbered in upload order,
// the KeptVersions newest kept plus the one that runs and the one being deployed, the rest discarded for the cleanup
// job, which alone removes a row.
public class FunctionVersionTests
{
    [Fact]
    public void AddVersion_NumbersInUploadOrder_AndKeepsWhatItWasGiven()
    {
        Function function = TestFunctions.AFunction();
        FunctionVersionSpec spec = TestFunctions.Spec(function, handler: "app.run", memoryMiB: 512, timeoutSeconds: 60);

        FunctionVersion v1 = function.AddVersion(spec);
        FunctionVersion v2 = function.WithVersion();

        Assert.Equal(1, v1.Number);
        Assert.Equal(2, v2.Number);
        Assert.Equal(spec.VersionId, v1.Id);
        Assert.Equal(function.Id, v1.FunctionId);
        Assert.Equal("app.run", v1.Handler);
        Assert.Equal(512, v1.MemoryMiB);
        Assert.Equal(500, v1.CpuMillicores);
        Assert.Equal(60, v1.TimeoutSeconds);
        Assert.Equal(TestFunctions.RuntimeName, v1.RuntimeName);
        Assert.Equal(TestFunctions.RuntimeImage, v1.RuntimeImage);
        Assert.Equal($"functions/{function.TenantId}/{function.Id}/{spec.VersionId}.zip", v1.CodeKey);
        Assert.Equal(TestFunctions.Digest, v1.ImageDigest);
        Assert.Null(v1.DiscardedAt);
    }

    [Fact]
    public void SevenUploads_KeepTheFiveNewest_AndDiscardTheTwoOldest()
    {
        Function function = TestFunctions.AFunction();
        List<FunctionVersion> versions = Enumerable.Range(0, 7).Select(_ => function.WithVersion()).ToList();

        Assert.Equal([true, true, false, false, false, false, false], versions.Select(v => v.IsDiscarded));
        Assert.Equal(2, function.VersionsToClean.Count());
    }

    [Fact]
    public void TheRunningVersion_IsKept_ThoughOlderThanTheFiveNewest()
    {
        Function function = TestFunctions.AFunction();
        FunctionVersion running = function.Running();
        for (int i = 0; i < 6; i++) function.WithVersion();

        Assert.False(running.IsDiscarded);
        Assert.Equal(running.Id, function.GetVersion(running.Id).Id);
        Assert.Single(function.Versions, v => v.IsDiscarded);
    }

    [Fact]
    public void TheDeployingVersion_IsKept_AndDiscardedOnceItNoLongerRunsNorWaits()
    {
        Function function = TestFunctions.AFunction();
        FunctionVersion old = function.WithVersion();
        Guid operationId = function.Deploy(old.Id);
        for (int i = 0; i < 5; i++) function.WithVersion();
        Assert.False(old.IsDiscarded);

        function.DeployFailed(operationId, null, "revision not ready", null);

        Assert.True(old.IsDiscarded);
    }

    [Fact]
    public void AnOldRunningVersion_IsDiscarded_OnceANewerOneRuns()
    {
        Function function = TestFunctions.AFunction();
        FunctionVersion old = function.Running();
        List<FunctionVersion> newer = Enumerable.Range(0, 5).Select(_ => function.WithVersion()).ToList();
        Assert.False(old.IsDiscarded);

        function.DeploySucceeded(function.Deploy(newer[^1].Id), null);

        Assert.True(old.IsDiscarded);
    }

    [Fact]
    public void AddVersion_ToADeletingOrDeletedFunction_IsInvalidStatus()
    {
        Function deleting = TestFunctions.AFunction();
        deleting.Running();
        deleting.Delete();
        Function deleted = TestFunctions.AFunction();
        deleted.Delete();

        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleting.WithVersion()).Code);
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleted.WithVersion()).Code);
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleting.EnsureTakesVersions()).Code);
    }

    [Fact]
    public void GetVersion_OfAnotherFunctionsVersion_Is404()
    {
        Function function = TestFunctions.AFunction();
        Function other = TestFunctions.AFunction();
        FunctionVersion foreign = other.WithVersion();

        Assert.Equal("FUNCTION_VERSION_NOT_FOUND", Assert.Throws<NotFoundException>(() => function.GetVersion(foreign.Id)).Code);
    }

    [Fact]
    public void ForgetCode_RemovesADiscardedVersion_AndRefusesAKeptOne()
    {
        Function function = TestFunctions.AFunction();
        List<FunctionVersion> versions = Enumerable.Range(0, 6).Select(_ => function.WithVersion()).ToList();

        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => function.ForgetCode(versions[^1].Id)).Code);
        function.ForgetCode(versions[0].Id);
        function.ForgetCode(Guid.NewGuid());

        Assert.Equal(5, function.Versions.Count);
        Assert.Empty(function.VersionsToClean);
    }

    [Fact]
    public void ForgetCode_OfADeletedFunction_RemovesEveryVersion()
    {
        Function function = TestFunctions.AFunction();
        FunctionVersion version = function.WithVersion();
        function.Delete();

        function.ForgetCode(version.Id);

        Assert.Empty(function.Versions);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(256, true)]
    [InlineData(2048, true)]
    [InlineData(0, false)]
    [InlineData(127, false)]
    [InlineData(200, false)]
    [InlineData(2176, false)]
    public void IsAMemorySize_Is128To2048_InStepsOf128(int memoryMiB, bool expected)
    {
        Assert.Equal(expected, Function.IsAMemorySize(memoryMiB));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(300, true)]
    [InlineData(0, false)]
    [InlineData(301, false)]
    public void IsATimeout_Is1To300(int seconds, bool expected)
    {
        Assert.Equal(expected, Function.IsATimeout(seconds));
    }

    [Fact]
    public void TheNamespaceLimits_FollowTheFunctionLimits()
    {
        Assert.Equal(43520, Function.TenantQuotaMemoryMiB);
        Assert.Equal(125, Function.DefaultCpuMillicores);
        Assert.Equal(2000, Function.MaxCpuMillicores);
    }
}
