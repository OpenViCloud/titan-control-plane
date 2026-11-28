using System.Text.Json;
using FluentValidation.TestHelper;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Infrastructure.Endpoints.Function;
using Titan.Infrastructure.Endpoints.FunctionSecret;

namespace Titan.Tests.Infrastructure;

// The 400s of a version's configuration (titan-ops features/ovc-function/design.md#function-env) and of the secret
// routes (titan-ops features/ovc-function/design.md#function-secrets), read from the JSON a client sends.
public class FunctionConfigValidatorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static TestValidationResult<UploadFunctionVersionConfig> Validate(string config) =>
        new UploadFunctionVersionConfigValidator().TestValidate(JsonSerializer.Deserialize<UploadFunctionVersionConfig>(config, Json)!);

    [Fact]
    public void AConfigWithVariablesSecretsAndASchedule_IsValid_AndReadsAsGiven()
    {
        const string config = """
            {"runtime":"python3.15","handler":"main.handler",
             "env":{"LOG_LEVEL":"info","apiBase":"https://x.test"},
             "secrets":{"DB":{"secret":"DB_PASSWORD","version":"latest"},"KEY":{"secret":"API_KEY","version":3}},
             "schedule":"0 * * * *"}
            """;

        Validate(config).ShouldNotHaveAnyValidationErrors();
        UploadFunctionVersionConfig parsed = JsonSerializer.Deserialize<UploadFunctionVersionConfig>(config, Json)!;
        Assert.Equal("info", parsed.Env!["LOG_LEVEL"]);
        Assert.True(parsed.Env.ContainsKey("apiBase"));
        Assert.Equal((true, (int?)null), UploadFunctionVersionSecretConfig.VersionOf(parsed.Secrets!["DB"]!.Version));
        Assert.Equal((true, (int?)3), UploadFunctionVersionSecretConfig.VersionOf(parsed.Secrets["KEY"]!.Version));
    }

    [Fact]
    public void AConfigWithoutAny_IsValid()
    {
        Validate("""{"runtime":"go"}""").ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("""{"runtime":"go","env":{"TITAN_X":"1"}}""")]
    [InlineData("""{"runtime":"go","env":{"PORT":"8080"}}""")]
    [InlineData("""{"runtime":"go","env":{"K_SERVICE":"x"}}""")]
    [InlineData("""{"runtime":"go","env":{"K_REVISION":"x"}}""")]
    [InlineData("""{"runtime":"go","env":{"K_CONFIGURATION":"x"}}""")]
    [InlineData("""{"runtime":"go","secrets":{"TITAN_DB":{"secret":"DB","version":"latest"}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"PORT":{"secret":"DB","version":1}}}""")]
    public void AReservedName_Is400(string config)
    {
        Assert.False(Validate(config).IsValid);
    }

    [Theory]
    [InlineData("""{"runtime":"go","env":{"1A":"x"}}""")]
    [InlineData("""{"runtime":"go","env":{"A-B":"x"}}""")]
    [InlineData("""{"runtime":"go","env":{"A":null}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A-B":{"secret":"DB","version":1}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"secret":"db-password","version":1}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"version":1}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"secret":"DB"}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"secret":"DB","version":0}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"secret":"DB","version":"2"}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"secret":"DB","version":"newest"}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":{"secret":"DB","version":1.5}}}""")]
    [InlineData("""{"runtime":"go","secrets":{"A":null}}""")]
    public void ANameOrASecretOutsideTheRule_Is400(string config)
    {
        Assert.False(Validate(config).IsValid);
    }

    [Fact]
    public void ANameInBothEnvAndSecrets_Is400()
    {
        var result = Validate("""{"runtime":"go","env":{"DB":"x"},"secrets":{"DB":{"secret":"DB","version":1}}}""");

        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("both"));
    }

    [Theory]
    [InlineData("@hourly")]
    [InlineData("0 0 * * MON")]
    [InlineData("* * * *")]
    [InlineData("")]
    public void AScheduleThatIsNotFiveFields_Is400(string schedule)
    {
        Assert.False(Validate($$"""{"runtime":"go","schedule":"{{schedule}}"}""").IsValid);
    }

    private static string EnvOf(string value, string more) => "{\"runtime\":\"go\",\"env\":{\"E\":\"" + value + "\"}" + more + "}";

    // What is known here, the variables and the secrets' variable names; the handler adds each secret's length.
    [Fact]
    public void VariablesOverEightKiB_Are400()
    {
        string fits = new('x', Function.MaxConfigBytes - 1);

        Validate(EnvOf(fits, "")).ShouldNotHaveAnyValidationErrors();
        Assert.False(Validate(EnvOf(fits + "x", "")).IsValid);
        Assert.False(Validate(EnvOf(fits, ",\"secrets\":{\"D\":{\"secret\":\"DB\",\"version\":1}}")).IsValid);
    }

    // ── the secret routes ──

    [Theory]
    [InlineData("DB_PASSWORD", "v", true)]
    [InlineData("db-password", "v", false)]
    [InlineData("1DB", "v", false)]
    [InlineData("", "v", false)]
    [InlineData("DB", "", false)]
    [InlineData("DB", null, false)]
    public void ASecretsNameAndValue(string name, string? value, bool valid)
    {
        Assert.Equal(valid, new CreateFunctionSecretValidator().TestValidate(new CreateFunctionSecretCommand(name, value!)).IsValid);
    }

    [Fact]
    public void ASecretsValue_IsAtMostFourKiB_OfUtf8()
    {
        string fits = new('é', FunctionSecret.MaxValueBytes / 2);

        Assert.True(new AddFunctionSecretVersionValidator().TestValidate(new AddFunctionSecretVersionBody(fits)).IsValid);
        var over = new AddFunctionSecretVersionValidator().TestValidate(new AddFunctionSecretVersionBody(fits + "x"));
        Assert.False(over.IsValid);
        Assert.DoesNotContain(fits, string.Join(" ", over.Errors.Select(e => e.ErrorMessage)));
        Assert.DoesNotContain(fits, new AddFunctionSecretVersionBody(fits).ToString());
    }
}
