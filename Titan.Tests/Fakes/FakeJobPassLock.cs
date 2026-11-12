using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

/// Records the jobs whose lock a pass took, in order; never waits.
public sealed class FakeJobPassLock : IJobPassLock
{
    public List<string> Taken { get; } = [];

    public Task TakeAsync(string job, CancellationToken ct)
    {
        Taken.Add(job);
        return Task.CompletedTask;
    }
}
