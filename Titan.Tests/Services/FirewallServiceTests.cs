using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Queries.Networking.FirewallRule;
using Titan.Application.Queries.Networking.FirewallRule.Contracts;
using Titan.Infrastructure.Endpoints.Network;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Networking;

using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class FirewallServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly string Project = Titan.Domain.Aggregates.Platform.Tenant.Tenant.ProjectNameFor(TenantId);
    private static readonly Guid NetworkId = Guid.NewGuid();

    private readonly Mock<IFirewallRuleRepository> _repo = new();
    private readonly Mock<INetworkRepository> _networkRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IActivityRecorder> _activity = new();

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#tenant-quota — the handlers load the tenant to enforce its
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
            => new CreateFirewallRuleCommandHandler(repo, networkRepo, tenantRepo, currentUser, commands, activity).HandleAsync(
                new CreateFirewallRuleCommand(networkId, req.Direction, req.Protocol, req.StartPort, req.EndPort, req.Cidr), ct);

        public Task<List<FirewallRuleQueryResult>> ListAsync(Guid networkId, CancellationToken ct = default)
            => new ListFirewallRulesQueryHandler(repo, networkRepo, currentUser).HandleAsync(new ListFirewallRulesQuery(networkId), ct);

        public Task DeleteAsync(Guid networkId, Guid ruleId, CancellationToken ct = default)
            => new DeleteFirewallRuleCommandHandler(repo, networkRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteFirewallRuleCommand(networkId, ruleId), ct);

        public Task ReportAsync(UpdateFirewallRuleStatusCommand report, CancellationToken ct = default)
            => new UpdateFirewallRuleStatusCommandHandler(repo, activity).HandleAsync(report, ct);
    }

    // ── CreateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_IngressTcp_ReturnsDto()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        _repo.Setup(r => r.CountByNetworkAsync(NetworkId, default)).ReturnsAsync(0);
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#create-returns-id: the response carries only the id, so the rule's own fields are asserted on the
        // instance that was added — the layer that still owns them.
        FirewallRule? added = null;
        _repo.Setup(r => r.Add(It.IsAny<FirewallRule>())).Callback<FirewallRule>(r => added = r);

        var result = await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default);

        Assert.NotNull(added);
        Assert.Equal(added!.Id, result.RuleId);
        Assert.Equal(FirewallDirection.Ingress, added.Direction);
        Assert.Equal(FirewallProtocol.TCP, added.Protocol);
        Assert.Equal(80, added.StartPort);
        Assert.Equal(80, added.EndPort);
        Assert.Equal("0.0.0.0/0", added.Cidr);
        Assert.Null(added.ExternalId);
        Assert.Equal("null/NotCreated/Exists/Creating", Shape(added));
        _activity.Verify(a => a.Stage("firewall_rule.create_requested", "firewall_rule", added.Id, "Ingress TCP 80 from 0.0.0.0/0"), Times.Once);
        _repo.Verify(r => r.Add(It.IsAny<FirewallRule>()), Times.Once);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueFirewallRuleCreateAsync(
            It.IsAny<Guid>(), NetworkId, "Ingress", "TCP", 80, 80, "0.0.0.0/0",
            "cs-net-id", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_EgressUdp_EnqueuesTheCommandWithEgressDirection()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        _repo.Setup(r => r.CountByNetworkAsync(NetworkId, default)).ReturnsAsync(0);

        await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Egress", "UDP", 53, 53, "0.0.0.0/0"), default);

        _commands.Verify(w => w.EnqueueFirewallRuleCreateAsync(
            It.IsAny<Guid>(), NetworkId, "Egress", "UDP", 53, 53, "0.0.0.0/0",
            "cs-net-id", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_IcmpNoPorts_SavesNullPorts()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        _repo.Setup(r => r.CountByNetworkAsync(NetworkId, default)).ReturnsAsync(0);

        FirewallRule? saved = null;
        _repo.Setup(r => r.Add(It.IsAny<FirewallRule>())).Callback<FirewallRule>(r => saved = r);

        await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "ICMP", null, null, "10.0.0.0/8"), default);

        Assert.NotNull(saved);
        Assert.Null(saved.StartPort);
        Assert.Null(saved.EndPort);
    }

    [Fact]
    public async Task Create_AllProtocolNoPorts_SavesNullPorts()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        _repo.Setup(r => r.CountByNetworkAsync(NetworkId, default)).ReturnsAsync(0);

        FirewallRule? saved = null;
        _repo.Setup(r => r.Add(It.IsAny<FirewallRule>())).Callback<FirewallRule>(r => saved = r);

        var result = await CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Egress", "All", null, null, "0.0.0.0/0"), default);

        Assert.NotNull(saved);
        Assert.Equal(saved!.Id, result.RuleId);
        Assert.Equal(FirewallProtocol.All, saved.Protocol);
        Assert.Null(saved.StartPort);
        Assert.Null(saved.EndPort);
    }

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        _repo.Setup(r => r.CountByNetworkAsync(NetworkId, default)).ReturnsAsync(20);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default));

        Assert.Equal("FIREWALL_QUOTA_EXCEEDED", ex.Code);
        _commands.Verify(w => w.EnqueueFirewallRuleCreateAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_NetworkNotActive_ThrowsBusinessException()
    {
        SetupCurrentUser();
        var network = Network.CreateUnchecked(TenantId, "net-1", "cluster-1"); // status = Creating
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default));

        Assert.Equal("FIREWALL_NETWORK_NOT_READY", ex.Code);
    }

    [Fact]
    public async Task Create_NetworkBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser();
        var network = MakeActiveNetwork(Guid.NewGuid()); // different tenant
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(NetworkId, new CreateFirewallRuleRequest("Ingress", "TCP", 80, 80, "0.0.0.0/0"), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────

    // The raw error of a give-up is admin-only (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure).
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
        SetupActiveNetwork();
        var rules = new List<FirewallRule>
        {
            FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0"),
            FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 443, 443, "0.0.0.0/0"),
            FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Egress, FirewallProtocol.UDP, 53, 53, "0.0.0.0/0"),
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

    // A foreign network must answer exactly like a missing one, and the rule repository must not even
    // be asked.
    [Fact]
    public async Task Delete_NetworkBelongsToOtherTenant_ThrowsNotFound_AndNeverReadsTheRule()
    {
        SetupCurrentUser();
        var network = MakeActiveNetwork(Guid.NewGuid()); // different tenant
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(NetworkId, Guid.NewGuid(), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#forget-on-provider-confirm: the rule is NOT forgotten when the delete is accepted — it stays Deleting until the
    // provider confirms, so a failed delete cannot leave a rule enforced that Titan no longer lists.
    [Fact]
    public async Task Delete_ActiveRule_WaitsForTheProvider_AndRecordsTheRequest()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        FirewallRule rule = MakeRule(Seed.Active);
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("enabled/Exists/Deleted/Deleting", Shape(rule));
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _activity.Verify(a => a.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, "Ingress TCP 80 from 0.0.0.0/0"), Times.Once);
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            rule.Id, NetworkId, "cs-rule-abc", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers like the first.
    [Theory]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public async Task Delete_Repeated_ChangesNothing(Seed seed)
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        FirewallRule rule = MakeRule(seed);
        string before = Shape(rule);
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal(before, Shape(rule));
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
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
        SetupActiveNetwork();
        FirewallRule rule = MakeRule(Seed.Creating);
        rule.MarkCreated("cs-rule-7");
        rule.MarkRequestFailed("reason", "detail");
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("null/Exists/Deleted/Deleting", Shape(rule));
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            rule.Id, NetworkId, "cs-rule-7", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_RuleWhoseCreateFailed_IsDeletedLocally_WithRequestAndOutcome()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        FirewallRule rule = MakeRule(Seed.Error);
        Assert.Null(rule.ExternalId);
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        await CreateService().DeleteAsync(NetworkId, rule.Id, default);

        Assert.Equal("null/Deleted/Deleted/-", Shape(rule));
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _activity.Verify(a => a.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
        _activity.Verify(a => a.StageOutcome(TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
        _commands.Verify(w => w.EnqueueFirewallRuleDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_AfterAFailedDelete_SendsTheDeleteAgain()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        FirewallRule rule = MakeRule(Seed.DeleteRequested);
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);
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
        SetupActiveNetwork();
        var rule = FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0");
        // ExternalId is null — the create command has not reported yet
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);

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
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((FirewallRule?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(NetworkId, Guid.NewGuid(), default));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Delete_RuleBelongsToOtherNetwork_ThrowsNotFoundException()
    {
        SetupCurrentUser();
        SetupActiveNetwork();
        var rule = FirewallRule.CreateUnchecked(Guid.NewGuid(), TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0");
        rule.MarkActive("cs-rule-abc");
        _repo.Setup(r => r.FindByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(NetworkId, rule.Id, default));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser() =>
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);

    private void SetupActiveNetwork() =>
        _networkRepo.Setup(r => r.FindByIdAsync(NetworkId, default)).ReturnsAsync(MakeActiveNetwork(TenantId));

    public enum Seed { Creating, Active, Error, DeleteRequested, Deleted }

    private static FirewallRule MakeRule(Seed seed)
    {
        FirewallRule rule = FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0");
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

    private static Network MakeActiveNetwork(Guid tenantId)
    {
        var network = Network.CreateUnchecked(tenantId, "net-1", "cluster-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }
}
