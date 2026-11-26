using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Networking.Network;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Application.Services;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.Network;

// The worker's report that a network request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant: there must never be a route to this handler
// from a customer path. The row is locked so a request made meanwhile is ordered before or after this report, never
// interleaved (titan-ops architecture/resource-lifecycle.md#one-key-per-request). A report the network is not waiting on
// is ignored and still answers 204: the worker has nothing to retry.
public class EndNetworkRequestCommandHandler(
    INetworkRepository repo,
    NetworkRoutes routes,
    ITenantRepository tenants,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ILogger<EndNetworkRequestCommandHandler> logger)
{
    public async Task HandleAsync(EndNetworkRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the network is loaded: a malformed report is the worker's bug, whatever the network.
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);
        NetworkStatus? status = null;
        if (command.Status != null)
        {
            status = NetworkReports.ParseStatus(command.Status);
        }

        NetworkAggregate network = await repo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        NetworkStatus? ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = await NetworkReports.FinishOperationAsync(routes, network, command.RequestId, command.ExternalId,
                status: status, cidr: command.Cidr, gateway: command.Gateway, activity, ct);
        }
        else
        {
            ended = await NetworkReports.FailOperationAsync(routes, network, command.RequestId, command.ExternalId,
                status: status, cidr: command.Cidr, gateway: command.Gateway, reason: command.Reason, detail: command.Detail,
                activity, ct);
        }
        if (ended == null)
        {
            return;
        }

        // A rule needs the network on Incus (AddFirewallRule), so the default network's rules wait for this report
        // (titan-ops features/network/design.md#default-network).
        if (ended == NetworkStatus.Creating && network.OpensSshWhenCreated)
        {
            await OpenDefaultPortsAsync(network, ct);
        }

        await repo.SaveAsync(ct);
    }

    // The create's Done is saved whether the rules are added or not.
    private async Task OpenDefaultPortsAsync(NetworkAggregate network, CancellationToken ct)
    {
        TenantAggregate? tenant = await tenants.FindByIdAsync(network.TenantId, ct);
        if (tenant == null)
        {
            logger.LogWarning("Default network {NetworkId}: tenant {TenantId} not found; no rule added.",
                network.Id, network.TenantId);
            return;
        }

        await DefaultNetworkPorts.OpenAsync(network, tenant, DefaultNetworkPorts.WhenCreated, commands, activity, logger, ct);
    }
}
