using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker's metric batch for one cluster, sent every minute (titan-ops features/vm/design.md#vm-metrics). Not an
// observation and not the end of a request: it changes no resource's status. 204 also when nothing was written.
public class RecordVmMetricsEndpoint(RecordVmMetricsCommandHandler handler, IConfiguration config)
    : Endpoint<RecordVmMetricsBody>
{
    public override void Configure()
    {
        Post("internal/clusters/{clusterId}/vm-metrics");
        AllowAnonymous();
        // The validator's verdict waits for the token check below: a caller without the token learns nothing about
        // the body.
        DontThrowIfValidationFails();
    }

    public override async Task HandleAsync(RecordVmMetricsBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A body that is not JSON of this shape binds as a validation failure, and a JSON null as no body at all.
        if (ValidationFailed || body == null)
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        // The validator has refused every null below.
        var samples = new List<VmMetricsSample>();
        foreach (VmMetricsSampleBody vm in body.Vms!)
        {
            samples.Add(new VmMetricsSample(vm.ExternalId!, vm.Source!, vm.CpuSeconds!.Value, vm.MemoryTotalBytes,
                vm.MemoryAvailableBytes, vm.RootFsSizeBytes, vm.RootFsAvailBytes, vm.DiskReadBytes!.Value,
                vm.DiskWrittenBytes!.Value));
        }
        await handler.HandleAsync(
            new RecordVmMetricsCommand(Route<string>("clusterId")!, body.SampledAt!.Value, samples), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the route's cluster. Every field is nullable so a missing one
// reaches the validator instead of binding as 0.
public record RecordVmMetricsBody(DateTimeOffset? SampledAt, List<VmMetricsSampleBody>? Vms);

public record VmMetricsSampleBody(
    string? ExternalId,
    string? Source,
    double? CpuSeconds,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? RootFsSizeBytes,
    long? RootFsAvailBytes,
    long? DiskReadBytes,
    long? DiskWrittenBytes);

// Counters and sizes are never negative; the four memory and root filesystem numbers may be null (no guest agent).
public class RecordVmMetricsValidator : Validator<RecordVmMetricsBody>
{
    public RecordVmMetricsValidator()
    {
        RuleFor(b => b.SampledAt).NotNull();
        RuleFor(b => b.Vms).NotNull();
        RuleForEach(b => b.Vms).NotNull().ChildRules(vm =>
        {
            vm.RuleFor(v => v.ExternalId).NotEmpty();
            vm.RuleFor(v => v.Source)
                .Must(s => s == "agent" || s == "qemu")
                .WithMessage("source must be agent or qemu.");
            vm.RuleFor(v => v.CpuSeconds).NotNull().GreaterThanOrEqualTo(0);
            vm.RuleFor(v => v.DiskReadBytes).NotNull().GreaterThanOrEqualTo(0);
            vm.RuleFor(v => v.DiskWrittenBytes).NotNull().GreaterThanOrEqualTo(0);
            vm.RuleFor(v => v.MemoryTotalBytes).GreaterThanOrEqualTo(0).When(v => v.MemoryTotalBytes != null);
            vm.RuleFor(v => v.MemoryAvailableBytes).GreaterThanOrEqualTo(0).When(v => v.MemoryAvailableBytes != null);
            vm.RuleFor(v => v.RootFsSizeBytes).GreaterThanOrEqualTo(0).When(v => v.RootFsSizeBytes != null);
            vm.RuleFor(v => v.RootFsAvailBytes).GreaterThanOrEqualTo(0).When(v => v.RootFsAvailBytes != null);
        });
    }
}
