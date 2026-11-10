using FastEndpoints;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Queries.Compute.Vm;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Endpoints.Vm;
using Titan.Tests.Fakes;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Tests.Infrastructure;

// titan-ops features/vm/design.md#vm-metrics: the worker's batch is checked before anything is written, and the
// tenant's range word is one of three. FastEndpoints.Factory.Create skips validators, so the validators are run directly.
public class VmMetricsEndpointTests
{
    private static readonly DateTimeOffset SampledAt = new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);

    private static VmMetricsSampleBody Sample() =>
        new("vm7atdlelbvjafpojgfreep4u23i", "agent", 496.24, 2059284480, 1780793344, 10222313472, 8203649024,
            443663360, 440393728);

    private static RecordVmMetricsBody Body(VmMetricsSampleBody sample) => new(SampledAt, [sample]);

    private readonly RecordVmMetricsValidator _batch = new();

    [Fact]
    public void AWholeSample_IsAccepted_AndOneWithoutTheFourOptionalNumbersToo()
    {
        Assert.True(_batch.TestValidate(Body(Sample())).IsValid);
        VmMetricsSampleBody bare = Sample() with
        {
            MemoryTotalBytes = null,
            MemoryAvailableBytes = null,
            RootFsSizeBytes = null,
            RootFsAvailBytes = null,
        };
        Assert.True(_batch.TestValidate(Body(bare)).IsValid);
    }

    [Fact]
    public void AnEmptyBatch_IsAccepted()
    {
        Assert.True(_batch.TestValidate(new RecordVmMetricsBody(SampledAt, [])).IsValid);
    }

    public static TheoryData<string, VmMetricsSampleBody> BadSamples => new()
    {
        { "negative cpuSeconds", Sample() with { CpuSeconds = -1 } },
        { "negative diskReadBytes", Sample() with { DiskReadBytes = -1 } },
        { "negative diskWrittenBytes", Sample() with { DiskWrittenBytes = -1 } },
        { "negative memoryTotalBytes", Sample() with { MemoryTotalBytes = -1 } },
        { "negative memoryAvailableBytes", Sample() with { MemoryAvailableBytes = -1 } },
        { "negative rootFsSizeBytes", Sample() with { RootFsSizeBytes = -1 } },
        { "negative rootFsAvailBytes", Sample() with { RootFsAvailBytes = -1 } },
        { "missing cpuSeconds", Sample() with { CpuSeconds = null } },
        { "missing diskReadBytes", Sample() with { DiskReadBytes = null } },
        { "missing diskWrittenBytes", Sample() with { DiskWrittenBytes = null } },
        { "missing externalId", Sample() with { ExternalId = null } },
        { "empty externalId", Sample() with { ExternalId = "" } },
        { "missing source", Sample() with { Source = null } },
        { "unknown source", Sample() with { Source = "lxc" } },
    };

    [Theory]
    [MemberData(nameof(BadSamples))]
    public void ABadSample_Is400(string why, VmMetricsSampleBody sample)
    {
        Assert.False(_batch.TestValidate(Body(sample)).IsValid, why);
    }

    [Fact]
    public void ABatchWithoutSampledAtOrVms_Is400()
    {
        Assert.False(_batch.TestValidate(new RecordVmMetricsBody(null, [Sample()])).IsValid);
        Assert.False(_batch.TestValidate(new RecordVmMetricsBody(SampledAt, null)).IsValid);
        Assert.False(_batch.TestValidate(new RecordVmMetricsBody(SampledAt, [null!])).IsValid);
    }

    [Fact]
    public async Task ABatchWithTheToken_Is204_AndLandsUnderTheRoutesCluster()
    {
        var store = new FakeVmMetricStore();
        Guid vmId = Guid.NewGuid();
        store.AddVm("local", "vm7atdlelbvjafpojgfreep4u23i", vmId);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "secret" })
            .Build();
        var ep = Factory.Create<RecordVmMetricsEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["clusterId"] = "local";
                ctx.Request.Headers["X-Internal-Token"] = "secret";
            },
            new RecordVmMetricsCommandHandler(store), config);

        await ep.HandleAsync(Body(Sample()), CancellationToken.None);

        Assert.Equal(204, ep.HttpContext.Response.StatusCode);
        Assert.Equal(vmId, Assert.Single(store.Readings).VmId);
    }

    // ── The tenant's read ─────────────────────────────────────────────────

    private readonly GetVmMetricsValidator _range = new();

    [Theory]
    [InlineData(null)]
    [InlineData("1h")]
    [InlineData("24h")]
    [InlineData("7d")]
    public void AbsentOrOneOfTheThreeRanges_IsAccepted(string? range)
    {
        Assert.True(_range.TestValidate(new GetVmMetricsRequest(range)).IsValid);
    }

    [Theory]
    [InlineData("2h")]
    [InlineData("1d")]
    [InlineData("")]
    public void AnyOtherRange_Is400(string range)
    {
        Assert.False(_range.TestValidate(new GetVmMetricsRequest(range)).IsValid);
    }

    [Fact]
    public async Task NoRange_ReadsOneHour()
    {
        Titan.Domain.Aggregates.Tenant.Tenant owner = TestTenant.WithRoomToSpare();
        VmAggregate vm = VmAggregate.Create(owner.Id, Guid.NewGuid(), "web-1", "m", "tpl-1", "local", 20);
        var vms = new Mock<IVmRepository>();
        vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        var offerings = new Mock<IVmOfferingRepository>();
        offerings.Setup(r => r.FindByIdAsync("m", It.IsAny<CancellationToken>()))
            .ReturnsAsync(VmOffering.Create("m", "Medium", 2, 2048, 1000m));
        var caller = new Mock<ICurrentUser>();
        caller.Setup(u => u.TenantId).Returns(owner.Id);
        var handler = new GetVmMetricsQueryHandler(vms.Object, offerings.Object, new FakeVmMetricStore(), caller.Object,
            new FixedClock(SampledAt));
        var ep = Factory.Create<GetVmMetricsEndpoint>(
            ctx => ctx.Request.RouteValues["vmId"] = vm.Id.ToString(),
            handler);

        await ep.HandleAsync(new GetVmMetricsRequest(null), CancellationToken.None);

        Assert.Equal("1h", ep.Response.Range);
        Assert.Equal(60, ep.Response.StepSeconds);
    }
}
