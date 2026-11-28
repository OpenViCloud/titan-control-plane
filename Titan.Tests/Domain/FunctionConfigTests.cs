using Titan.Domain.Aggregates.Function;

namespace Titan.Tests.Domain;

// A function version's configuration (titan-ops features/ovc-function/design.md#function-config): the variable name
// rule, the reserved names, the 8 KiB limit, the strict five-field schedule, the secrets a version pins, and which
// versions keep a secret's number alive.
public class FunctionConfigTests
{
    [Theory]
    [InlineData("LOG_LEVEL", true)]
    [InlineData("_x", true)]
    [InlineData("a9", true)]
    [InlineData("9a", false)]
    [InlineData("A-B", false)]
    [InlineData("A.B", false)]
    [InlineData("", false)]
    public void AVariableName_IsLettersDigitsAndUnderscores_NotStartingWithADigit(string name, bool valid)
    {
        Assert.Equal(valid, Function.IsAnEnvName(name));
    }

    [Fact]
    public void AVariableName_IsAtMost128Characters()
    {
        Assert.True(Function.IsAnEnvName(new string('A', 128)));
        Assert.False(Function.IsAnEnvName(new string('A', 129)));
    }

    [Theory]
    [InlineData("TITAN_SCHEDULE", true)]
    [InlineData("TITAN_", true)]
    [InlineData("PORT", true)]
    [InlineData("K_SERVICE", true)]
    [InlineData("K_REVISION", true)]
    [InlineData("K_CONFIGURATION", true)]
    [InlineData("titan_x", false)]
    [InlineData("PORTS", false)]
    [InlineData("K_OTHER", false)]
    public void TheNamesThePlatformSets_AreReserved(string name, bool reserved)
    {
        Assert.Equal(reserved, Function.IsAReservedEnvName(name));
    }

    [Fact]
    public void TheConfigSize_CountsNamesAndValues_InUtf8_AndEachSecretsLength()
    {
        var env = new Dictionary<string, string> { ["A"] = "xyz", ["É"] = "é" };

        int bytes = Function.ConfigBytes(env, [("DB", 100)]);

        Assert.Equal(1 + 3 + 2 + 2 + 2 + 100, bytes);
        Assert.True(Function.IsAConfigSize(Function.MaxConfigBytes));
        Assert.False(Function.IsAConfigSize(Function.MaxConfigBytes + 1));
    }

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 0 1 1 0")]
    [InlineData("59 23 31 12 6")]
    [InlineData("*/5 * * * *")]
    [InlineData("0,15,30,45 9-17 * * 1-5")]
    [InlineData("0-30/10 */2 1,15 */3 0")]
    [InlineData("5/10 * * * *")]
    public void AFiveFieldSchedule_IsOne(string schedule)
    {
        Assert.True(Function.IsASchedule(schedule));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("@hourly")]
    [InlineData("0 0 * JAN *")]
    [InlineData("0 0 * * MON")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 0 *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 7")] // the CronJob API refuses 7 for Sunday
    [InlineData("0 0 * * 0-7")]
    [InlineData("10-5 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/ * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("* * ? * *")]
    [InlineData("* * L * *")]
    [InlineData("007 * * * *")]
    [InlineData(" * * * * *")]
    [InlineData("*  * * * *")]
    [InlineData("*\t* * * *")]
    [InlineData("*,5 * * * *")]
    [InlineData("1-2-3 * * * *")]
    public void AnythingElse_IsNoSchedule(string schedule)
    {
        Assert.False(Function.IsASchedule(schedule));
    }

    [Fact]
    public void AVersion_KeepsItsVariablesSecretsAndSchedule_AsACopy()
    {
        Function function = TestFunctions.AFunction();
        var env = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" };
        FunctionSecretRef db = FunctionSecretRef.Of("DB_PASSWORD", Guid.NewGuid(), "db", 3);

        FunctionVersion version = function.AddVersion(TestFunctions.Spec(function, env: env, secretRefs: [db], schedule: "*/5 * * * *"));
        env["LOG_LEVEL"] = "debug";

        Assert.Equal("info", version.Env["LOG_LEVEL"]);
        Assert.Equal([db], version.SecretRefs);
        Assert.Equal("*/5 * * * *", version.Schedule);
    }

    [Fact]
    public void ASecretRef_StartsAtVersionOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FunctionSecretRef.Of("A", Guid.NewGuid(), "a", 0));
    }

    [Fact]
    public void TheVersionsKeepingASecretNumber_AreTheKeptOnes_OfAFunctionNotDeleted()
    {
        Function function = TestFunctions.AFunction();
        Guid secretId = Guid.NewGuid();
        FunctionVersion first = function.WithSecrets(FunctionSecretRef.Of("A", secretId, "a", 1));
        for (int i = 0; i < Function.KeptVersions; i++)
        {
            function.WithVersion();
        }
        FunctionVersion pinning = function.WithSecrets(FunctionSecretRef.Of("A", secretId, "a", 2));
        function.WithSecrets(FunctionSecretRef.Of("A", Guid.NewGuid(), "other", 1));

        Assert.True(first.IsDiscarded);
        Assert.Equal([pinning], function.KeptVersionsPointingAt(secretId));

        function.Delete();
        Assert.True(function.IsDeleted);
        Assert.Empty(function.KeptVersionsPointingAt(secretId));
    }

    [Fact]
    public void ASecretInUse_IsOneTheRunningOrTheDeployingVersionReads()
    {
        Function function = TestFunctions.AFunction();
        Guid running = Guid.NewGuid();
        Guid deploying = Guid.NewGuid();
        Guid onlyKept = Guid.NewGuid();
        function.Running(function.WithSecrets(FunctionSecretRef.Of("A", running, "a", 1)));
        FunctionVersion next = function.WithSecrets(FunctionSecretRef.Of("B", deploying, "b", 1));
        function.WithSecrets(FunctionSecretRef.Of("C", onlyKept, "c", 1));
        function.Deploy(next.Id);

        Assert.True(function.RunsOrDeploysWithSecret(running));
        Assert.True(function.RunsOrDeploysWithSecret(deploying));
        Assert.False(function.RunsOrDeploysWithSecret(onlyKept));
    }
}
