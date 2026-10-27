using System.Text.Json;

namespace Titan.Application.Queries.Platform.ProviderCommand.Contracts;

/// What the worker runs. Attempt counts leases of this run, including leases that ran out without a report. At
/// Attempt == MaxAttempts a failure is the last one: the worker sends the resource its give-up callback before failing
/// the command. Attempt > MaxAttempts means the last lease ran out (the worker died mid-way): the worker does not redo
/// the provider work, it only sends the give-up callback and fails the command
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#retry-on-workflow).
public record LeasedProviderCommandResult(
    Guid Id,
    string Key,
    string Kind,
    string ZoneId,
    // The same value as ZoneId, under the name the worker moves to (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#contract-changes).
    string ClusterId,
    JsonElement Payload,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset LeaseUntil);
