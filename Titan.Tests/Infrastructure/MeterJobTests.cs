using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Services.Billing.Metering;
using Titan.Infrastructure.Hosting;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// One pass of every meter when the meter's CronJob calls POST /internal/jobs/meter
// (titan-ops features/billing/metering/design.md#meter-liveness): each meter's outcome is recorded where every pod's
// /healthz/meters reads it, and one meter failing neither stops the others nor passes unseen.
public class MeterJobTests
{
    private sealed class StubMeter(string name, bool throws) : IMeter
    {
        public string Name => name;
        public int Ticks { get; private set; }

        public Task<int> RunTickAsync(DateTimeOffset now, CancellationToken ct)
        {
            Ticks++;
            if (throws)
            {
                throw new InvalidOperationException("catalog broken");
            }
            return Task.FromResult(1);
        }
    }

    private readonly FakeMeterTicks _ticks = new();

    private MeterJob Job(params IMeter[] meters) => new(meters, _ticks, NullLogger<MeterJob>.Instance);

    [Fact]
    public async Task EveryMeter_TicksOnce_AndItsSuccessIsRecorded()
    {
        var compute = new StubMeter("Compute", throws: false);
        var disk = new StubMeter("Disk", throws: false);

        await Job(compute, disk).RunAsync(CancellationToken.None);

        Assert.Equal(1, compute.Ticks);
        Assert.Equal(1, disk.Ticks);
        Assert.Equal(new[] { ("Compute", true), ("Disk", true) }, _ticks.Recorded);
    }

    [Fact]
    public async Task AFailingMeter_IsRecordedAsFailed_TheOthersStillTick_AndThePassFails()
    {
        var compute = new StubMeter("Compute", throws: true);
        var disk = new StubMeter("Disk", throws: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Job(compute, disk).RunAsync(CancellationToken.None));

        Assert.Equal(1, disk.Ticks);
        Assert.Equal(new[] { ("Compute", false), ("Disk", true) }, _ticks.Recorded);
    }

    [Fact]
    public void ItsJobName_IsMeter()
    {
        Assert.Equal("meter", Job().Name);
    }
}
