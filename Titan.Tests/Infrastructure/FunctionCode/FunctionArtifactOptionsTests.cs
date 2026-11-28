using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Titan.Application.Options;

namespace Titan.Tests.Infrastructure.FunctionCode;

// FunctionCode and FunctionRegistry are required only while a function cluster is configured, checked when the host starts
// (titan-ops features/ovc-function/design.md#code-storage); prd declares no function cluster and sets neither section.
public class FunctionArtifactOptionsTests
{
    private const string Secret = "S3CR3T-function-0123456789";

    private static readonly FunctionClusterOptions NoCluster = new();
    private static readonly FunctionClusterOptions OneCluster = new() { Clusters = [new FunctionClusterConfig { Id = "fn-dev", Name = "fn-dev" }] };

    private static FunctionCodeOptions Code() => new()
    {
        Endpoint = "https://s3.example.test",
        Region = "auto",
        Bucket = "titan-function-code",
        AccessKeyId = "key-id",
        SecretAccessKey = Secret,
    };

    private static FunctionRegistryOptions Registry() => new() { PushUrl = "http://host.lima.internal:5100", PullHost = "localhost:5100" };

    [Fact]
    public void WithoutAFunctionCluster_NothingIsRequired()
    {
        Assert.True(new FunctionCodeValidation(NoCluster).Validate(null, new FunctionCodeOptions()).Succeeded);
        Assert.True(new FunctionRegistryValidation(NoCluster).Validate(null, new FunctionRegistryOptions()).Succeeded);
    }

    [Fact]
    public void WithAFunctionCluster_ValidSections_Start()
    {
        Assert.True(new FunctionCodeValidation(OneCluster).Validate(null, Code()).Succeeded);
        Assert.True(new FunctionRegistryValidation(OneCluster).Validate(null, Registry()).Succeeded);
        Assert.True(new FunctionRegistryValidation(OneCluster).Validate(null, new FunctionRegistryOptions
        {
            PushUrl = "https://registry.example.test/",
            PullHost = "registry.example.test",
        }).Succeeded);
    }

    public static TheoryData<Action<FunctionCodeOptions>, string> BadCode => new()
    {
        { o => o.Endpoint = "", "FunctionCode__Endpoint" },
        { o => o.Endpoint = "http://s3.example.test", "FunctionCode__Endpoint" },
        { o => o.Endpoint = "https://key:secret@s3.example.test", "FunctionCode__Endpoint" },
        { o => o.Endpoint = "https://s3.example.test/bucket", "FunctionCode__Endpoint" },
        { o => o.Region = "", "FunctionCode__Region" },
        { o => o.Bucket = "Upper", "FunctionCode__Bucket" },
        { o => o.AccessKeyId = "", "FunctionCode__AccessKeyId" },
        { o => o.SecretAccessKey = "", "FunctionCode__SecretAccessKey" },
        { o => o.SecretAccessKey = Secret + " x", "FunctionCode__SecretAccessKey" },
    };

    [Theory]
    [MemberData(nameof(BadCode))]
    public void WithAFunctionCluster_ABadCodeSetting_StopsTheHost_NamingTheSettingNotItsValue(Action<FunctionCodeOptions> spoil, string setting)
    {
        FunctionCodeOptions options = Code();
        spoil(options);

        ValidateOptionsResult result = new FunctionCodeValidation(OneCluster).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage);
        Assert.DoesNotContain(Secret, result.FailureMessage);
    }

    [Theory]
    [InlineData("", "localhost:5100", "FunctionRegistry__PushUrl")]
    [InlineData("host.lima.internal:5100", "localhost:5100", "FunctionRegistry__PushUrl")]
    [InlineData("ftp://host:5100", "localhost:5100", "FunctionRegistry__PushUrl")]
    [InlineData("http://host:5100/v2", "localhost:5100", "FunctionRegistry__PushUrl")]
    [InlineData("http://user:pw@host:5100", "localhost:5100", "FunctionRegistry__PushUrl")]
    [InlineData("http://host:5100", "", "FunctionRegistry__PullHost")]
    [InlineData("http://host:5100", "http://localhost:5100", "FunctionRegistry__PullHost")]
    [InlineData("http://host:5100", "localhost:5100/fn", "FunctionRegistry__PullHost")]
    [InlineData("http://host:5100", "LocalHost:5100", "FunctionRegistry__PullHost")]
    public void WithAFunctionCluster_ABadRegistrySetting_StopsTheHost(string pushUrl, string pullHost, string setting)
    {
        ValidateOptionsResult result = new FunctionRegistryValidation(OneCluster)
            .Validate(null, new FunctionRegistryOptions { PushUrl = pushUrl, PullHost = pullHost });

        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage);
    }

    // The same registration shape as Program.cs: bound, validated on start, the validator reading the function clusters.
    private static ServiceProvider Host(FunctionClusterOptions clusters, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(clusters);
        services.AddOptions<FunctionCodeOptions>().BindConfiguration(FunctionCodeOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FunctionCodeOptions>, FunctionCodeValidation>();
        services.AddOptions<FunctionRegistryOptions>().BindConfiguration(FunctionRegistryOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FunctionRegistryOptions>, FunctionRegistryValidation>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AHostWithAFunctionClusterAndNoFunctionCodeSection_DoesNotStart()
    {
        using ServiceProvider host = Host(OneCluster, new()
        {
            ["FunctionRegistry:PushUrl"] = "http://host.lima.internal:5100",
            ["FunctionRegistry:PullHost"] = "localhost:5100",
        });

        var e = Assert.Throws<OptionsValidationException>(() => host.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("FunctionCode__Endpoint", e.Message);
    }

    [Fact]
    public void AHostWithAFunctionClusterAndBothSections_Starts_AndAHostWithoutOne_NeedsNeither()
    {
        using ServiceProvider configured = Host(OneCluster, new()
        {
            ["FunctionCode:Endpoint"] = "https://s3.example.test",
            ["FunctionCode:Region"] = "auto",
            ["FunctionCode:Bucket"] = "titan-function-code",
            ["FunctionCode:AccessKeyId"] = "key-id",
            ["FunctionCode:SecretAccessKey"] = Secret,
            ["FunctionRegistry:PushUrl"] = "http://host.lima.internal:5100",
            ["FunctionRegistry:PullHost"] = "localhost:5100",
        });
        using ServiceProvider prd = Host(NoCluster, new());

        configured.GetRequiredService<IStartupValidator>().Validate();
        prd.GetRequiredService<IStartupValidator>().Validate();
    }
}
