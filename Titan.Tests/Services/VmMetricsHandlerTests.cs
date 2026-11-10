using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Tests.Fakes;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Tests.Services;

// titan-ops features/vm/design.md#vm-metrics: the worker's batch lands as one row per VM of that cluster, and the
// tenant reads its own VM's points back.
public class VmMetricsHandlerTests
{
    private static readonly DateTimeOffset SampledAt = new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);

    private static VmMetricsSample Sample(string externalId, string source = "agent") =>
        new(externalId, source, 496.24, 2059284480, 1780793344, 10222313472, 8203649024, 443663360, 440393728);

    [Fact]
    public async Task ABatchNamingAVmOfTheCluster_WritesOneRowWithItsSevenNumbersAndSource()
    {
        var store = new FakeVmMetricStore();
        Guid vmId = Guid.NewGuid();
        store.AddVm("local", "vm7atdlelbvjafpojgfreep4u23i", vmId);

        await new RecordVmMetricsCommandHandler(store).HandleAsync(
            new RecordVmMetricsCommand("local", SampledAt, [Sample("vm7atdlelbvjafpojgfreep4u23i")]));

        VmMetricReading row = Assert.Single(store.Readings);
        Assert.Equal(
            new VmMetricReading(vmId, SampledAt, "agent", 496.24, 2059284480, 1780793344, 10222313472, 8203649024,
                443663360, 440393728),
            row);
    }

    // A name only another cluster's VM carries, or no VM at all: dropped, the rest of the batch still lands.
    [Fact]
    public async Task ANameNoVmOfThatClusterCarries_IsDropped_AndTheRestIsWritten()
    {
        var store = new FakeVmMetricStore();
        Guid here = Guid.NewGuid();
        store.AddVm("local", "vmaaaaaaaaaaaaaaaaaaaaaaaaaa", here);
        store.AddVm("singapore01", "vmbbbbbbbbbbbbbbbbbbbbbbbbbb", Guid.NewGuid());

        await new RecordVmMetricsCommandHandler(store).HandleAsync(new RecordVmMetricsCommand("local", SampledAt,
        [
            Sample("vmaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            Sample("vmbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            Sample("vmcccccccccccccccccccccccccc"),
        ]));

        Assert.Equal([here], store.Readings.Select(r => r.VmId));
    }

    [Fact]
    public async Task TheSameBatchSentTwice_IsOneRow()
    {
        var store = new FakeVmMetricStore();
        store.AddVm("local", "vmaaaaaaaaaaaaaaaaaaaaaaaaaa", Guid.NewGuid());
        var handler = new RecordVmMetricsCommandHandler(store);
        var command = new RecordVmMetricsCommand("local", SampledAt, [Sample("vmaaaaaaaaaaaaaaaaaaaaaaaaaa")]);

        await handler.HandleAsync(command);
        await handler.HandleAsync(command);

        Assert.Single(store.Readings);
    }

    [Fact]
    public async Task ANameTwiceInOneBatch_IsOneRow()
    {
        var store = new FakeVmMetricStore();
        store.AddVm("local", "vmaaaaaaaaaaaaaaaaaaaaaaaaaa", Guid.NewGuid());

        await new RecordVmMetricsCommandHandler(store).HandleAsync(new RecordVmMetricsCommand("local", SampledAt,
            [Sample("vmaaaaaaaaaaaaaaaaaaaaaaaaaa"), Sample("vmaaaaaaaaaaaaaaaaaaaaaaaaaa", "qemu")]));

        Assert.Equal("agent", Assert.Single(store.Readings).Source);
    }

    [Fact]
    public async Task AnEmptyBatch_WritesNothing()
    {
        var store = new FakeVmMetricStore();

        await new RecordVmMetricsCommandHandler(store).HandleAsync(new RecordVmMetricsCommand("local", SampledAt, []));

        Assert.Empty(store.Readings);
    }

    // ── Reading ───────────────────────────────────────────────────────────

    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    private static ICurrentUser Caller(Guid tenantId)
    {
        var caller = new Mock<ICurrentUser>();
        caller.Setup(u => u.TenantId).Returns(tenantId);
        return caller.Object;
    }

    private static (GetVmMetricsQueryHandler Handler, VmAggregate Vm, FakeVmMetricStore Store) Reader(
        Guid callerTenant, int vcpuCount, DateTimeOffset now)
    {
        VmAggregate vm = VmAggregate.Create(Owner.Id, Guid.NewGuid(), "web-1", "m", "tpl-1", "local", 20);
        var vms = new Mock<IVmRepository>();
        vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        var offerings = new Mock<IVmOfferingRepository>();
        offerings.Setup(r => r.FindByIdAsync("m", It.IsAny<CancellationToken>()))
            .ReturnsAsync(VmOffering.Create("m", "Medium", vcpuCount, 2048, 1000m));
        var store = new FakeVmMetricStore();
        var handler = new GetVmMetricsQueryHandler(vms.Object, offerings.Object, store, Caller(callerTenant),
            new FixedClock(now));
        return (handler, vm, store);
    }

    private static VmMetricReading Reading(Guid vmId, DateTimeOffset at, double cpuSeconds) =>
        new(vmId, at, "agent", cpuSeconds, 2_000_000, 1_500_000, null, null, 0, 0);

    [Fact]
    public async Task TheOwner_ReadsTheRangeWithTheReadingBeforeIt_AtTheOfferingsVcpuCount()
    {
        DateTimeOffset now = SampledAt.AddHours(1);
        var (handler, vm, store) = Reader(Owner.Id, vcpuCount: 2, now);
        store.Readings.Add(Reading(vm.Id, SampledAt.AddMinutes(-2), 10));
        store.Readings.Add(Reading(vm.Id, SampledAt.AddMinutes(-1), 40));
        store.Readings.Add(Reading(vm.Id, SampledAt.AddMinutes(1), 100));

        VmMetricsQueryResult result = await handler.HandleAsync(new GetVmMetricsQuery(vm.Id, "1h"));

        Assert.Equal("1h", result.Range);
        Assert.Equal(60, result.StepSeconds);
        VmMetricPointQueryResult point = Assert.Single(result.Points);
        Assert.Equal(SampledAt.AddMinutes(1), point.At);
        // 60 CPU seconds over 120 s on 2 vCPUs.
        Assert.Equal(25.0, point.CpuPercent);
    }

    [Theory]
    [InlineData("24h", 300)]
    [InlineData("7d", 1800)]
    public async Task TheLongerRanges_AnswerTheirStep(string range, int stepSeconds)
    {
        var (handler, vm, _) = Reader(Owner.Id, vcpuCount: 1, SampledAt);

        VmMetricsQueryResult result = await handler.HandleAsync(new GetVmMetricsQuery(vm.Id, range));

        Assert.Equal(range, result.Range);
        Assert.Equal(stepSeconds, result.StepSeconds);
        Assert.Empty(result.Points);
    }

    [Fact]
    public async Task AnotherTenantsVm_Is404VmNotFound_WithNoNumbers()
    {
        var (handler, vm, store) = Reader(Guid.NewGuid(), vcpuCount: 1, SampledAt.AddHours(1));
        store.Readings.Add(Reading(vm.Id, SampledAt, 10));

        NotFoundException notFound = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(new GetVmMetricsQuery(vm.Id, "1h")));

        Assert.Equal("VM_NOT_FOUND", notFound.Code);
    }

    [Fact]
    public async Task AnUnknownVm_Is404VmNotFound()
    {
        var (handler, _, _) = Reader(Owner.Id, vcpuCount: 1, SampledAt);

        NotFoundException notFound = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(new GetVmMetricsQuery(Guid.NewGuid(), "1h")));

        Assert.Equal("VM_NOT_FOUND", notFound.Code);
    }
}
