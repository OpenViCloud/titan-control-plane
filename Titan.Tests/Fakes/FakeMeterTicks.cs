using Titan.Application.Services.Billing.Metering;

namespace Titan.Tests.Fakes;

public sealed class FakeMeterTicks : IMeterTicks
{
    public List<(string Meter, bool Success)> Recorded { get; } = [];

    public Task RecordAsync(string meter, bool success, DateTimeOffset at, CancellationToken ct)
    {
        Recorded.Add((meter, success));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, MeterTickState>> SnapshotAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, MeterTickState>>(new Dictionary<string, MeterTickState>());
}
