using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Hosting;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/resource-lifecycle.md#one-key-per-request: finished rows older than 30 days go, open rows never.
public class ProviderCommandRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static ProviderCommand Row(FakeProviderCommandRepository repo, ProviderCommandStatus status, DateTimeOffset createdAt)
    {
        ProviderCommand row = ProviderCommand.Create($"test-{Guid.NewGuid()}", ProviderCommandKinds.VmDelete, "mock", "{}",
            createdAt);
        Guid lease = Guid.NewGuid();
        if (status != ProviderCommandStatus.Pending)
        {
            row.Lease(lease, createdAt);
        }
        if (status == ProviderCommandStatus.Done)
        {
            row.Complete(lease);
        }
        if (status == ProviderCommandStatus.Failed)
        {
            row.Fail(lease, "gave up", retryable: false, createdAt);
        }
        repo.Add(row);
        return row;
    }

    [Fact]
    public void KeepsThirtyDays_ByDefault()
    {
        Assert.Equal(30, new ProviderCommandRetentionOptions().RetentionDays);
    }

    [Fact]
    public async Task RunOnce_DeletesTheFinishedRowsOlderThanTheRetention_AndNoOpenRow()
    {
        var repo = new FakeProviderCommandRepository();
        DateTimeOffset old = Now.AddDays(-31);
        DateTimeOffset young = Now.AddDays(-29);
        ProviderCommand oldDone = Row(repo, ProviderCommandStatus.Done, old);
        ProviderCommand oldFailed = Row(repo, ProviderCommandStatus.Failed, old);
        ProviderCommand oldPending = Row(repo, ProviderCommandStatus.Pending, old);
        ProviderCommand oldLeased = Row(repo, ProviderCommandStatus.Leased, old);
        ProviderCommand youngDone = Row(repo, ProviderCommandStatus.Done, young);
        ProviderCommand youngFailed = Row(repo, ProviderCommandStatus.Failed, young);

        int removed = await ProviderCommandRetention.RunOnceAsync(repo, 30, Now, NullLogger.Instance, default);

        Assert.Equal(2, removed);
        Assert.DoesNotContain(oldDone, repo.Rows);
        Assert.DoesNotContain(oldFailed, repo.Rows);
        Assert.Equal([oldPending, oldLeased, youngDone, youngFailed], repo.Rows);
    }

    [Fact]
    public async Task StartsWithoutFaulting_AndStopsOnRequest()
    {
        var service = new ProviderCommandRetention(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ProviderCommandRetentionOptions()), TimeProvider.System,
            NullLogger<ProviderCommandRetention>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully || service.ExecuteTask.IsCanceled);
    }
}
