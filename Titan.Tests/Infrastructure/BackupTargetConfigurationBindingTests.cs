using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Titan.Application.Options;

namespace Titan.Tests.Infrastructure;

// The setting names are the ones the Deployment's env and the Secret in GitOps use (BackupTarget__Endpoint …): rename one here
// and the environment silently has no target, so the names are pinned
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target).
public class BackupTargetConfigurationBindingTests
{
    [Fact]
    public void TheEnvironmentVariablesOfTheDeployment_BindToTheTarget()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackupTarget:Endpoint"] = "https://acct.r2.cloudflarestorage.com",
            ["BackupTarget:Region"] = "auto",
            ["BackupTarget:Bucket"] = "titan-backup-staging",
            ["BackupTarget:AccessKeyId"] = "key-id",
            ["BackupTarget:SecretAccessKey"] = "the-secret",
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<BackupTargetOptions>().Bind(configuration.GetSection(BackupTargetOptions.SectionName));

        var target = services.BuildServiceProvider().GetRequiredService<IOptions<BackupTargetOptions>>().Value;

        Assert.Equal("BackupTarget", BackupTargetOptions.SectionName);
        Assert.Equal("https://acct.r2.cloudflarestorage.com", target.Endpoint);
        Assert.Equal("auto", target.Region);
        Assert.Equal("titan-backup-staging", target.Bucket);
        Assert.Equal("key-id", target.AccessKeyId);
        Assert.Equal("the-secret", target.SecretAccessKey);
        Assert.Null(target.Problem());
    }

    [Fact]
    public void WithNothingSet_ThereIsNoKeyPair_AndTheControlPlaneStillStarts()
    {
        var services = new ServiceCollection();
        services.AddOptions<BackupTargetOptions>().Bind(new ConfigurationBuilder().Build().GetSection(BackupTargetOptions.SectionName));

        var target = services.BuildServiceProvider().GetRequiredService<IOptions<BackupTargetOptions>>().Value;

        Assert.False(target.HasKeyPair);
    }
}
