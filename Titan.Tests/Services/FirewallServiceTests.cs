using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Queries.Networking.FirewallRule;
using Titan.Application.Queries.Networking.FirewallRule.Contracts;
using Titan.Infrastructure.Endpoints.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// A rule is written through its network (titan-ops architecture/code-structure.md#child-entity): the create and delete
// handlers load the network (locked, for a create), change it, and save it through INetworkRepository. The rule
// repository is read side only, so only the list query asks it.
public class FirewallServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly string Project = Titan.Domain.Aggregates.Tenant.Tenant.ProjectNameFor(TenantId);
    private static readonly Guid NetworkId = Guid.NewGuid();

    private readonly Mock<IFirewallRuleRepository> _repo = new();
    private readonly Mock<INetworkRepository> _networkRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IActivityRecorder> _activity = new();

    // titan-ops architecture/tenancy-and-admin.md#tenant-quota — the handlers load the tenant to enforce its
    // quota; Tenant.Create's default caps are what the quota assertions in this file count against.
    public FirewallServiceTests()
        => _tenantRepo
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestTenant.Active("t", "kc", "t@x.io"));

    // Facade over the firewall command and query handlers so the tests keep one call shape.
    private FirewallFacade CreateService() =>
        new(_repo.Object, _networkRepo.Object, _tenantRepo.Object, _currentUser.Object, _commands.Object, _activity.Object);

    private sealed class FirewallFacade(
        IFirewallRuleRepository repo, INetworkRepository networkRepo, ITenantRepository tenantRepo,
        ICurrentUser currentUser, IProviderCommandQueue commands, IActivityRecorder activity)
    {
        public Task<CreateFirewallRuleCommandResult> CreateAsync(Guid networkId, CreateFirewallRuleRequest req, CancellationToken ct = default)
            => new CreateFirewallRuleCommandHandler(networkRepo, tenantRepo, currentUser, commands, activity).HandleAsync(
                new CreateFirewallRuleCommand(networkId, req.Direction, req.Protocol, req.StartPort, req.EndPort, req.Cidr), ct);

        public Task<List<FirewallRuleQueryResult>> ListAsync(Guid networkId, CancellationToken ct = default)
            => new ListFirewallRulesQueryHandler(repo, networkRepo, currentUser).HandleAsync(new ListFirewallRulesQuery(networkId), ct);

        public Task DeleteAsync(Guid networkId, Guid ruleId, CancellationToken ct = default)
            => new DeleteFirewallRuleCommandHandler(networkRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteFirewallRuleCommand(networkId, ruleId), ct);

        public Task ReportAsync(UpdateFirewallRuleStatusCommand report, CancellationToken ct = default)
            => new UpdateFirewallRuleStatusCommandHandler(networkRepo, activity).HandleAsync(report, ct);
    }

    // ── CreateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_IngressTcp_ReturnsDto()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();

        var result = await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so the rule's own fields are asserted on the
        // network that now holds it — the aggregate that owns them.
        FirewallRule added = Assert.Single(network.FirewallRules);
        Assert.Equal(added.Id, result.RuleId);
        Assert.Equal(network.Id, added.NetworkId);
        Assert.Equal(TenantId, added.TenantId);
        Assert.Equal(FirewallDirection.Ingress, added.Direction);
        Assert.Equal(FirewallProtocol.TCP, added.Protocol);
        Assert.Equal(80, added.StartPort);
        Assert.Equal(80, added.EndPort);
        Assert.Equal("0.0.0.0/0", added.Cidr);
        Assert.Null(added.ExternalId);
        Assert.Equal("null/NotCreated/Exists/Creating", Shape(added));
        _activity.Verify(a => a.Stage("firewall_rule.create_requested", "firewall_rule", added.Id, "Ingress TCP 80 from 0.0.0.0/0"), Times.Once);
        _networkRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueFirewallRuleCreateAsync(
            It.IsAny<Guid>(), NetworkId, "Ingress", "TCP", 80, 80, "0.0.0.0/0",
            "cs-net-id", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // The network is loaded locked (titan-ops architecture/code-structure.md#child-entity): the cap is counted on what
    // it holds, so a second create at the cap must wait for this one to commit.
    [Fact]
    public async Task Create_LoadsTheNetworkForUpdate_NotTheUnlockedRead()
    {
        SetupCurrentUser();
        SetupActiveNetwork();

        await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default);

        _networkRepo.Verify(r => r.FindByIdForUpdateAsync(NetworkId, It.IsAny<CancellationToken>()), Times.Once);
        _networkRepo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_EgressUdp_EnqueuesTheCommandWithEgressDirection()
    {
        SetupCurrentUser();
        SetupActiveNetwork();

        await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Egress", "UDP", 53, 53, "0.0.0.0/0"), default);

        _commands.Verify(w => w.EnqueueFirewallRuleCreateAsync(
            It.IsAny<Guid>(), NetworkId, "Egress", "UDP", 53, 53, "0.0.0.0/0",
            "cs-net-id", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_IcmpNoPorts_SavesNullPorts()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();

        await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "ICMP", null, null, "10.0.0.0/8"), default);

        FirewallRule saved = Assert.Single(network.FirewallRules);
        Assert.Null(saved.StartPort);
        Assert.Null(saved.EndPort);
    }

    [Fact]
    public async Task Create_AllProtocolNoPorts_SavesNullPorts()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();

        var result = await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Egress", "All", null, null, "0.0.0.0/0"), default);

        FirewallRule saved = Assert.Single(network.FirewallRules);
        Assert.Equal(saved.Id, result.RuleId);
        Assert.Equal(FirewallProtocol.All, saved.Protocol);
        Assert.Null(saved.StartPort);
        Assert.Null(saved.EndPort);
    }

    // The cap is the tenant's default (20) counted on the network's own rules: 20 rules Incus holds fill it.
    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        for (int i = 0; i < 20; i++)
        {
            network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 1000 + i, 1000 + i, "0.0.0.0/0")
                .MarkActive($"cs-rule-{i}");
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default));

        Assert.Equal("FIREWALL_QUOTA_EXCEEDED", ex.Code);
        Assert.Equal(20, network.FirewallRules.Count);
        _networkRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueFirewallRuleCreateAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A rule whose delete is waiting stopped counting the moment it was asked for (DCS-Q1), so the cap is not full.
    [Fact]
    public async Task Create_AtTheCapWithOneRuleBeingDeleted_IsAccepted()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        for (int i = 0; i < 20; i++)
        {
            network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 1000 + i, 1000 + i, "0.0.0.0/0")
                .MarkActive($"cs-rule-{i}");
        }
        network.FirewallRules.First().RequestDelete();

        var result = await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default);

        Assert.Contains(network.FirewallRules, r => r.Id == result.RuleId);
        Assert.Equal(20, network.FirewallRuleQuotaUsage);
    }

    [Fact]
    public async Task Create_NetworkNotActive_ThrowsBusinessException()
    {
        SetupCurrentUser();
        var network = Network.CreateUnchecked(TenantId, "net-1", "cluster-1"); // status = Creating
        _networkRepo.Setup(r => r.FindByIdForUpdateAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default));

        Assert.Equal("FIREWALL_NETWORK_NOT_READY", ex.Code);
        Assert.Empty(network.FirewallRules);
    }

    [Fact]
    public async Task Create_NetworkBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser();
        var network = MakeActiveNetwork(Guid.NewGuid()); // different tenant
        _networkRepo.Setup(r => r.FindByIdForUpdateAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────

    // The raw error of a give-up is admin-only (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void FirewallRuleQueryResult_HasNoFailureDetailField()
    {
        Assert.Null(typeof(FirewallRuleQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(FirewallRuleQueryResult).GetProperty("DesiredState"));
        Assert.NotNull(typeof(FirewallRuleQueryResult).GetProperty("RequestFailureReason"));
        Assert.NotNull(typeof(FirewallRuleQueryResult).GetProperty("CurrentExistence"));
    }

    [Fact]
    public async Task List_ReturnsRulesForNetwork()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        var rules = new List<FirewallRule>
        {
            network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0"),
            network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 443, 443, "0.0.0.0/0"),
            network.AddFirewallRuleUnchecked(FirewallDirection.Egress, FirewallProtocol.UDP, 53, 53, "0.0.0.0/0"),
        };
        _repo.Setup(r => r.ListByNetworkAsync(NetworkId, default)).ReturnsAsync(rules);

        var result = await CreateService().ListAsync(NetworkId, default);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task List_NetworkBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser();
        var network = MakeActiveNetwork(Guid.NewGuid());
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().ListAsync(NetworkId, default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    // A foreign network must answer exactly like a missing one, and nothing else is read: the rule repository is
    // read side only and the network is the only thing loaded.
    [Fact]
    public async Task Delete_NetworkBelongsToOtherTenant_ThrowsNotFound_AndNeverReadsTheRule()
    {
        SetupCurrentUser();
        var network = MakeActiveNetwork(Guid.NewGuid()); // different tenant
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(NetworkId, Guid.NewGuid(), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
        _repo.VerifyNoOtherCalls();
    }

    // titan-ops architecture/desired-and-current.md#forget-on-provider-confirm: the rule is NOT forgotten when the delete is accepted — it stays Deleting until the
    // provider confirms, so a failed delete cannot leave a rule enforced that Titan no longer lists.
    [Fact]
    public async Task Delete_ActiveRule_WaitsForTheProvider_AndRecordsTheRequest()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        FirewallRule rule = MakeRule(network, Seed.Active);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("enabled/Exists/Deleted/Deleting", Shape(rule));
        _networkRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _activity.Verify(a => a.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, "Ingress TCP 80 from 0.0.0.0/0"), Times.Once);
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            rule.Id, NetworkId, "cs-rule-abc", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers like the first.
    [Theory]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public async Task Delete_Repeated_ChangesNothing(Seed seed)
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        FirewallRule rule = MakeRule(network, seed);
        string before = Shape(rule);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal(before, Shape(rule));
        _networkRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _activity.VerifyNoOtherCalls();
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A rule whose create failed never gets an ExternalId, so refusing it as NOT_READY would make it
    // undeletable while it still counts against the quota.
    // A2 for rules: the security group exists, so the delete must reach the provider.
    [Fact]
    public async Task Delete_RuleWhoseCreateFailedAfterItsIdArrived_GoesToTheProvider()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        FirewallRule rule = MakeRule(network, Seed.Creating);
        rule.MarkCreated("cs-rule-7");
        rule.MarkRequestFailed("reason", "detail");

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("null/Exists/Deleted/Deleting", Shape(rule));
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            rule.Id, NetworkId, "cs-rule-7", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_RuleWhoseCreateFailed_IsDeletedLocally_WithRequestAndOutcome()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        FirewallRule rule = MakeRule(network, Seed.Error);
        Assert.Null(rule.ExternalId);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("null/Deleted/Deleted/-", Shape(rule));
        _networkRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _activity.Verify(a => a.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
        _activity.Verify(a => a.StageOutcome(TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_AfterAFailedDelete_SendsTheDeleteAgain()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        FirewallRule rule = MakeRule(network, Seed.DeleteRequested);
        // the failed delete gives the rule back
        await CreateService().ReportAsync(new UpdateFirewallRuleStatusCommand(NetworkId, rule.Id, "RequestFailed", Reason: "Firewall rule deletion failed."));
        Assert.Equal("enabled/Exists/Exists/-", Shape(rule));

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("enabled/Exists/Deleted/Deleting", Shape(rule));
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            rule.Id, NetworkId, "cs-rule-abc", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ExternalIdNull_ThrowsBusinessException()
    {
        SetupCurrentUser();
        Network network = SetupActiveNetwork();
        // ExternalId is null — the create command has not reported yet
        FirewallRule rule = MakeRule(network, Seed.Creating);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(NetworkId, rule.Id, default));

        Assert.Equal("FIREWALL_RULE_NOT_READY", ex.Code);
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_RuleNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser();
        SetupActiveNetwork();

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(NetworkId, Guid.NewGuid(), default));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
    }

    // The rule exists, in another network of the same tenant: the network asked does not have it, so 404.
    [Fact]
    public async Task Delete_RuleBelongsToOtherNetwork_ThrowsNotFoundException()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        Network otherNetwork = MakeActiveNetwork(TenantId);
        FirewallRule rule = MakeRule(otherNetwork, Seed.Active);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(NetworkId, rule.Id, default));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
        Assert.Equal("enabled/Exists/Exists/-", Shape(rule));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser() =>
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);

    /// The network every handler loads: unlocked for a read or a delete, locked for a create, by rule id for a
    /// callback. One instance, so a test reads the handler's writes off it.
    private Network SetupActiveNetwork()
    {
        Network network = MakeActiveNetwork(TenantId);
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(network);
        _networkRepo.Setup(r => r.FindByIdForUpdateAsync(NetworkId, default)).ReturnsAsync(network);
        _networkRepo.Setup(r => r.FindByFirewallRuleIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(network);
        return network;
    }

    public enum Seed { Creating, Active, Error, DeleteRequested, Deleted }

    private static FirewallRule MakeRule(Network network, Seed seed)
    {
        FirewallRule rule = network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0");
        switch (seed)
        {
            case Seed.Creating:
                break;
            case Seed.Active:
                rule.MarkActive("cs-rule-abc");
                break;
            case Seed.Error:
                rule.MarkRequestFailed("Firewall rule creation failed.", "raw");
                break;
            case Seed.DeleteRequested:
                rule.MarkActive("cs-rule-abc");
                rule.RequestDelete();
                break;
            case Seed.Deleted:
                rule.MarkActive("cs-rule-abc");
                rule.RequestDelete();
                rule.MarkDeleted();
                break;
        }
        return rule;
    }

    private static string Shape(FirewallRule r)
    {
        string state = "null";
        if (r.CurrentState != null)
        {
            state = r.CurrentState.Value.ToString();
        }
        string transition = "-";
        if (r.Transition != null)
        {
            transition = r.Transition.Value.ToString();
        }
        return $"{state}/{r.CurrentExistence}/{r.DesiredExistence}/{transition}";
    }

    /// Network.CreateUnchecked picks its own id: the repository mock answers the route id NetworkId with this network,
    /// and a rule it adds carries the network's real id.
    private static Network MakeActiveNetwork(Guid tenantId)
    {
        var network = Network.CreateUnchecked(tenantId, "net-1", "cluster-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }
}
